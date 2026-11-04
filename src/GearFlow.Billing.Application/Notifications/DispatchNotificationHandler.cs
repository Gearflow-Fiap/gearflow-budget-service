using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Notifications;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.Application.Notifications;

public interface INotificationReadModel { Task<IReadOnlyList<Guid>> FindDueAsync(DateTimeOffset now, int limit, CancellationToken ct); }
public interface INotificationComposer { EmailMessage Compose(Notification notification, Budget budget, Payment? payment); }
public sealed record DispatchNotificationCommand(Guid NotificationId) : ICommand<Result<Unit>>;

public sealed class DispatchNotificationHandler(INotificationRepository notifications, IBudgetRepository budgets,
    IPaymentRepository payments, IUnitOfWork uow, IClock clock, INotificationComposer composer, IEmailSender email)
    : ICommandHandler<DispatchNotificationCommand, Result<Unit>>
{
    private sealed record Delivery(Notification Notification, Budget Budget, Payment? Payment);
    public async Task<Result<Unit>> HandleAsync(DispatchNotificationCommand command, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var delivery = await uow.ExecuteAsync(async token =>
        {
            var notification = await notifications.GetAsync(command.NotificationId, token);
            if (notification is null) return null;
            var version = notification.Version;
            try { notification.Claim(owner, clock.UtcNow, TimeSpan.FromMinutes(2)); }
            catch (DomainException exception) when (exception.Code == "notification.claim") { return null; }
            var budget = await budgets.GetAsync(notification.BudgetId, token) ?? throw new InvalidOperationException("Notification budget is missing.");
            var payment = await payments.FindByBudgetAsync(budget.Id, token);
            var relevant = NotificationPolicy.IsRelevant(notification, budget, payment, clock.UtcNow);
            if (!relevant) notification.Suppress(owner, clock.UtcNow);
            await notifications.SaveAsync(notification, version, token);
            return relevant ? new Delivery(notification, budget, payment) : null;
        }, ct);
        if (delivery is null) return Result<Unit>.Success(default);
        try
        {
            await email.SendAsync(composer.Compose(delivery.Notification, delivery.Budget, delivery.Payment), ct);
            await uow.ExecuteAsync(async token =>
            {
                var notification = (await notifications.GetAsync(command.NotificationId, token))!; var version = notification.Version;
                notification.MarkSent(owner, clock.UtcNow); await notifications.SaveAsync(notification, version, token); return true;
            }, ct);
            return Result<Unit>.Success(default);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await uow.ExecuteAsync(async token =>
            {
                var notification = (await notifications.GetAsync(command.NotificationId, token))!; var version = notification.Version;
                notification.Retry(owner, clock.UtcNow, TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, notification.Attempts) * 30)), 8);
                await notifications.SaveAsync(notification, version, token); return true;
            }, ct);
            return Result<Unit>.Failure("notification.delivery_failed", "Delivery deferred; consult notification attempts.");
        }
    }
}
