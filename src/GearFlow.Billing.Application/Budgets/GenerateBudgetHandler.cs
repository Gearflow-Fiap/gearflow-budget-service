using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Notifications;

namespace GearFlow.Billing.Application.Budgets;

public sealed class GenerateBudgetHandler(IBudgetRepository budgets, INotificationRepository notifications,
    IOutboxWriter outbox, IUnitOfWork uow, IClock clock, ICommandCompletion? completion = null, ICommercialFenceRepository? fences = null)
    : ICommandHandler<GenerateBudgetCommand, Result<Guid>>
{
    public Task<Result<Guid>> HandleAsync(GenerateBudgetCommand command, CancellationToken ct) =>
        uow.ExecuteAsync(async token =>
        {
            if (fences is not null)
            {
                var fence = await fences.GetOrCreateAsync(command.ServiceOrderId, token); var version = fence.Version;
                fence.AuthorizeCommercialAction(); await fences.SaveAsync(fence, version, token);
            }
            var existing = await budgets.FindByServiceOrderAsync(command.ServiceOrderId, token);
            if (existing is not null)
            {
                if (existing.Customer != command.Customer || existing.ExpiresOn != command.ExpiresOn
                    || !existing.Items.SequenceEqual(command.Items))
                    return Result<Guid>.Failure("budget.conflict", "Service order already has a different budget snapshot.");
                if (completion is not null) await completion.CompleteAsync(token);
                return Result<Guid>.Success(existing.Id);
            }

            Budget budget;
            try
            {
                budget = new Budget(Guid.NewGuid(), command.ServiceOrderId, command.Customer,
                    command.Items, clock.UtcNow, command.ExpiresOn);
            }
            catch (DomainException exception)
            {
                await outbox.AppendAsync(new BudgetGenerationFailed(Guid.NewGuid(), command.ServiceOrderId,
                    command.CausationId, clock.UtcNow, exception.Code, exception.Message), token);
                if (completion is not null) await completion.CompleteAsync(token);
                return Result<Guid>.Failure(exception.Code, exception.Message);
            }
            await budgets.AddAsync(budget, token);
            await outbox.AppendAsync(new BudgetGenerated(Guid.NewGuid(), budget.ServiceOrderId, command.CausationId,
                clock.UtcNow, budget.Id, budget.Total.Amount, budget.Total.Currency, budget.ExpiresOn), token);
            await notifications.AddAsync(new Notification(Guid.NewGuid(), budget.ServiceOrderId, budget.Id,
                $"budget:{budget.Id}:proposal:v1", budget.Customer.Email, "budget-proposal-v1", clock.UtcNow), token);
            if (completion is not null) await completion.CompleteAsync(token);
            return Result<Guid>.Success(budget.Id);
        }, ct);
}
