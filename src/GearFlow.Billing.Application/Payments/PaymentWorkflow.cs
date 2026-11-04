using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Notifications;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.Application.Payments;

// Local transactions surround state changes only. All provider I/O happens between transactions.
public sealed class PaymentWorkflow(IBudgetRepository budgets, IPaymentRepository payments,
    INotificationRepository notifications, IOutboxWriter outbox, IUnitOfWork uow, IPaymentGateway gateway, IClock clock,
    ICommandCompletion? completion = null, ICommercialFenceRepository? fences = null)
    : IPaymentCompensator
{
    public async Task<Result<Guid>> RequestAsync(RequestPaymentCommand command, CancellationToken ct)
    {
        var intent = await uow.ExecuteAsync(async token =>
        {
            var budget = await budgets.GetAsync(command.BudgetId, token);
            if (budget is null) return Result<Payment>.Failure("budget.not_found", "Budget not found.");
            if (budget.ServiceOrderId != command.ServiceOrderId || budget.Total != command.Amount)
                return Result<Payment>.Failure("payment.conflict", "Order, amount or currency does not match the budget.");
            var existing = await payments.FindByBudgetAsync(budget.Id, token);
            if (existing is not null)
            {
                if (existing.Status is not (PaymentStatus.Created or PaymentStatus.Pending) && completion?.Kind == IncomingCommandKind.RequestPayment)
                    await completion.CompleteAsync(token);
                return Result<Payment>.Success(existing);
            }
            if (fences is not null)
            {
                var fence = await fences.GetOrCreateAsync(command.ServiceOrderId, token); var version = fence.Version;
                fence.AuthorizeCommercialAction(); await fences.SaveAsync(fence, version, token);
            }
            try
            {
                var payment = Payment.Request(Guid.NewGuid(), budget, command.ServiceOrderId, command.Amount, clock.UtcNow);
                payment.BeginOperation(Guid.NewGuid(), PaymentOperation.Create, clock.UtcNow);
                await payments.AddAsync(payment, token);
                return Result<Payment>.Success(payment);
            }
            catch (DomainException exception) { return Result<Payment>.Failure(exception.Code, exception.Message); }
        }, ct);
        if (!intent.IsSuccess) return Result<Guid>.Failure(intent.Error!.Code, intent.Error.Message);
        var payment = intent.Value;
        if (payment.Status is PaymentStatus.Paid or PaymentStatus.Failed or PaymentStatus.Canceled or PaymentStatus.Refunded)
            return Result<Guid>.Success(payment.Id);
        var applied = await RecoverAsync(payment, command.CausationId, ct);
        return applied.IsSuccess ? Result<Guid>.Success(payment.Id) : Result<Guid>.Failure(applied.Error!.Code, applied.Error.Message);
    }

    public async Task<Result<Unit>> RecoverAsync(Guid paymentId, CancellationToken ct)
    {
        var payment = await uow.ExecuteAsync(token => payments.GetAsync(paymentId, token), ct);
        return payment is null ? Result<Unit>.Failure("payment.not_found", "Payment not found.") : await RecoverAsync(payment, null, ct);
    }

    private async Task<Result<Unit>> RecoverAsync(Payment payment, Guid? cause, CancellationToken ct)
    {
        try
        {
            GatewayPayment remote;
            if (payment.ProviderOrderId is { } order) remote = await gateway.GetAsync(order, ct);
            else
            {
                var budget = await uow.ExecuteAsync(token => budgets.GetAsync(payment.BudgetId, token), ct)
                    ?? throw new InvalidOperationException("Payment budget is missing.");
                var create = payment.Attempts.Single(x => x.Operation == PaymentOperation.Create);
                var found = await gateway.FindByBudgetAsync(payment.BudgetId, payment.CreatedOn, ct);
                if (found is not null) remote = found;
                else
                {
                    // Never silently create a new charge when the persisted payment window has elapsed.
                    if (clock.UtcNow >= payment.ExpiresOn)
                        return Result<Unit>.Failure("payment.recovery_required", "Creation result is unresolved after the payment window; manual reconciliation is required.");
                    remote = await gateway.CreateAsync(new ChargeRequest(payment.Id, budget.Id, payment.Amount, budget.Customer,
                        create.Id, payment.ExpiresOn!.Value, payment.CreatedOn), ct);
                }
            }
            var applied = await ApplyAsync(remote, cause, ct);
            if (!applied.IsSuccess) return applied;
            if (completion?.Kind == IncomingCommandKind.RequestPayment
                || (completion?.Kind == IncomingCommandKind.RefundPayment && remote.Status == GatewayStatus.Refunded)) return applied;
            // Recover durable compensation intents after a timeout/crash without inventing a new operation.
            var current = await uow.ExecuteAsync(token => payments.GetAsync(payment.Id, token), ct);
            var unfinished = current?.Attempts.SingleOrDefault(x => x.Status is AttemptStatus.Started or AttemptStatus.Uncertain);
            if (unfinished?.Operation == PaymentOperation.Refund && current!.Status == PaymentStatus.Paid)
            {
                var refunded = await gateway.RefundAsync(current.ProviderOrderId!, current.Amount, unfinished.Id, ct);
                var result = await ApplyAsync(refunded, cause, ct);
                return result.IsSuccess && refunded.Status != GatewayStatus.Refunded
                    ? Result<Unit>.Failure("payment.refund_pending", "Provider refund is not definitive yet.") : result;
            }
            if (unfinished?.Operation == PaymentOperation.Cancel && current!.Status == PaymentStatus.Pending)
            {
                var canceled = await gateway.CancelAsync(current.ProviderOrderId!, unfinished.Id, ct);
                var result = await ApplyAsync(canceled, cause, ct);
                return result.IsSuccess && canceled.Status != GatewayStatus.Canceled
                    ? Result<Unit>.Failure("payment.cancel_pending", "Provider cancellation is not definitive yet.") : result;
            }
            return Result<Unit>.Success(default);
        }
        catch (GatewayException exception)
        {
            await RememberUncertaintyAsync(payment.Id, ct);
            return Result<Unit>.Failure(exception.Code, exception.Message);
        }
    }

    public async Task<Result<Unit>> RefreshAsync(string orderId, CancellationToken ct)
    {
        var remote = await gateway.GetAsync(orderId, ct);
        return await ApplyAsync(remote, null, ct);
    }

    public Task<Result<Unit>> ApplyAsync(GatewayPayment remote, Guid? cause, CancellationToken ct) =>
        uow.ExecuteAsync(async token =>
        {
            var payment = await payments.FindByBudgetAsync(remote.Evidence.ExternalReference, token);
            if (payment is null) return Result<Unit>.Failure("payment.not_found", "Provider order has no local payment intent.");
            var budget = await budgets.GetAsync(payment.BudgetId, token)
                ?? throw new InvalidOperationException("Payment budget is missing.");
            var paymentVersion = payment.Version; var budgetVersion = budget.Version; var previous = payment.Status;
            if (remote.Status == GatewayStatus.Refunded && previous != PaymentStatus.Refunded
                && !payment.Attempts.Any(x => x.Operation == PaymentOperation.Refund))
                throw new GatewayException("payment.external_refund", false, "Refund without a local intent requires manual reconciliation.");
            // Domain exceptions abort the transaction rather than committing a partially applied aggregate.
            var create = payment.Attempts.Single(x => x.Operation == PaymentOperation.Create);
            payment.RegisterOrder(create.Id, remote.Evidence, remote.QrCode, remote.TicketUrl, remote.ExpiresOn, clock.UtcNow);
            if (previous == PaymentStatus.Created)
            {
                budget.AwaitPayment(clock.UtcNow);
                await outbox.AppendAsync(new PaymentRequested(Guid.NewGuid(), payment.ServiceOrderId, cause, clock.UtcNow,
                    budget.Id, payment.Id, payment.Amount.Amount, payment.Amount.Currency, payment.ProviderOrderId!, payment.ExpiresOn!.Value), token);
            }
            switch (remote.Status)
            {
                case GatewayStatus.Paid when payment.Status != PaymentStatus.Refunded:
                    payment.Confirm(remote.Evidence, clock.UtcNow);
                    if (budget.Status == BudgetStatus.Canceled) budget.RecognizeLatePayment(clock.UtcNow);
                    else budget.MarkPaid(clock.UtcNow);
                    break;
                case GatewayStatus.Rejected: payment.Fail(remote.Evidence, "provider_rejected", clock.UtcNow); break;
                case GatewayStatus.Canceled:
                    if (payment.Attempts.Any(x => x.Operation == PaymentOperation.Cancel
                        && x.Status is AttemptStatus.Started or AttemptStatus.Uncertain))
                        payment.ObserveCancellation(remote.Evidence, clock.UtcNow);
                    else payment.Fail(remote.Evidence, "provider_canceled_or_expired", clock.UtcNow);
                    break;
                case GatewayStatus.Refunded:
                    if (payment.Status != PaymentStatus.Refunded)
                    {
                        var refund = payment.Attempts.SingleOrDefault(x => x.Operation == PaymentOperation.Refund);
                        if (refund is null) return Result<Unit>.Failure("payment.external_refund", "Refund without a local intent requires manual reconciliation.");
                        payment.ConfirmRefund(refund.Id, remote.Evidence, remote.RefundId!, clock.UtcNow);
                        budget.MarkRefunded(clock.UtcNow);
                    }
                    break;
            }
            if (payment.Status == PaymentStatus.Paid && previous != PaymentStatus.Paid && previous != PaymentStatus.Refunded)
                await outbox.AppendAsync(new PaymentConfirmed(Guid.NewGuid(), payment.ServiceOrderId, cause, clock.UtcNow,
                    budget.Id, payment.Id, payment.Amount.Amount, payment.Amount.Currency, payment.ProviderOrderId!, payment.PaidOn!.Value), token);
            if (payment.Status == PaymentStatus.Failed && previous != PaymentStatus.Failed)
                await outbox.AppendAsync(new PaymentFailed(Guid.NewGuid(), payment.ServiceOrderId, cause, clock.UtcNow,
                    budget.Id, payment.Id, "payment.rejected", "Provider definitively rejected the payment."), token);
            if (payment.Status == PaymentStatus.Refunded && previous != PaymentStatus.Refunded)
            {
                await outbox.AppendAsync(new PaymentRefunded(Guid.NewGuid(), payment.ServiceOrderId, cause, clock.UtcNow,
                    budget.Id, payment.Id, payment.Amount.Amount, payment.Amount.Currency, payment.RefundId), token);
                await notifications.AddAsync(new Notification(Guid.NewGuid(), payment.ServiceOrderId, budget.Id,
                    $"payment:{payment.Id}:refund:v1", budget.Customer.Email, "payment-refunded-v1", clock.UtcNow), token);
            }
            if (payment.Status == PaymentStatus.Pending
                && (previous == PaymentStatus.Created || (payment.QrCode is not null && paymentVersion != payment.Version && previous == PaymentStatus.Pending)))
            {
                if (payment.QrCode is not null)
                    await notifications.AddAsync(new Notification(Guid.NewGuid(), payment.ServiceOrderId, budget.Id,
                        $"payment:{payment.Id}:pix:v1", budget.Customer.Email, "payment-pix-v1", clock.UtcNow), token);
            }
            if (payment.Version != paymentVersion) await payments.SaveAsync(payment, paymentVersion, token);
            if (budget.Version != budgetVersion) await budgets.SaveAsync(budget, budgetVersion, token);
            if (remote.Status != GatewayStatus.Unknown
                && (completion?.Kind == IncomingCommandKind.RequestPayment
                    || (completion?.Kind == IncomingCommandKind.RefundPayment && payment.Status == PaymentStatus.Refunded)))
                await completion.CompleteAsync(token);
            return remote.Status == GatewayStatus.Unknown
                ? Result<Unit>.Failure("payment.unresolved", "Unrecognized provider state requires reconciliation.")
                : Result<Unit>.Success(default);
        }, ct);

    public async Task<Result<Unit>> CancelAsync(Guid serviceOrderId, Guid budgetId, CancellationToken ct)
    {
        var intent = await PrepareCompensationAsync(serviceOrderId, budgetId, null, PaymentOperation.Cancel, ct);
        if (!intent.IsSuccess) return Result<Unit>.Failure(intent.Error!.Code, intent.Error.Message);
        if (intent.Value is null) return Result<Unit>.Success(default);
        var recovered = await RecoverAsync(intent.Value, null, ct);
        if (!recovered.IsSuccess) return recovered;
        var current = await uow.ExecuteAsync(token => payments.GetAsync(intent.Value.Id, token), ct);
        return current!.Status is PaymentStatus.Canceled or PaymentStatus.Failed or PaymentStatus.Refunded
            ? Result<Unit>.Success(default)
            : Result<Unit>.Failure("payment.compensation_required", "Charge cancellation is pending or requires a confirmed refund.");
    }

    public async Task<Result<Unit>> RefundAsync(RefundPaymentCommand command, CancellationToken ct)
    {
        Ensure.Text(command.Reason, "reason");
        if (fences is not null)
            await uow.ExecuteAsync(async token =>
            {
                var existingBudget = await budgets.GetAsync(command.BudgetId, token);
                Ensure.That(existingBudget is null || existingBudget.ServiceOrderId == command.ServiceOrderId, "payment.conflict", "Service order does not match.");
                var fence = await fences.GetOrCreateAsync(command.ServiceOrderId, token); var version = fence.Version;
                fence.RequestRefund(command.Reason); await fences.SaveAsync(fence, version, token); return true;
            }, ct);
        var intent = await PrepareCompensationAsync(command.ServiceOrderId, command.BudgetId, command.PaymentId, PaymentOperation.Refund, ct);
        if (!intent.IsSuccess) return Result<Unit>.Failure(intent.Error!.Code, intent.Error.Message);
        if (intent.Value is null)
        {
            await uow.ExecuteAsync(async token =>
            {
                await outbox.AppendAsync(new PaymentRefunded(Guid.NewGuid(), command.ServiceOrderId, command.CausationId,
                    clock.UtcNow, command.BudgetId, command.PaymentId, 0, "BRL", null), token);
                if (completion?.Kind == IncomingCommandKind.RefundPayment) await completion.CompleteAsync(token);
                return true;
            }, ct);
            return Result<Unit>.Success(default);
        }
        if (intent.Value.Status == PaymentStatus.Refunded) return Result<Unit>.Success(default);
        var recovered = await RecoverAsync(intent.Value, command.CausationId, ct);
        if (!recovered.IsSuccess) return recovered;
        if (completion?.Kind == IncomingCommandKind.RefundPayment) return Result<Unit>.Success(default);
        var current = await uow.ExecuteAsync(token => payments.GetAsync(intent.Value.Id, token), ct);
        return current!.Status == PaymentStatus.Refunded ? Result<Unit>.Success(default)
            : Result<Unit>.Failure("payment.refund_pending", "Refund has not been confirmed by the provider.");
    }

    private Task<Result<Payment?>> PrepareCompensationAsync(Guid orderId, Guid budgetId, Guid? paymentId, PaymentOperation operation, CancellationToken ct) =>
        uow.ExecuteAsync(async token =>
        {
            Ensure.Identity(orderId); Ensure.Identity(budgetId);
            if (paymentId is { } pid) Ensure.Identity(pid);
            var budget = await budgets.GetAsync(budgetId, token);
            if (budget is not null && budget.ServiceOrderId != orderId)
                return Result<Payment?>.Failure("payment.conflict", "Compensation service order does not match the budget.");
            var payment = await payments.FindByBudgetAsync(budgetId, token);
            if (paymentId is { } referencedId)
            {
                var referenced = await payments.GetAsync(referencedId, token);
                if (referenced is not null && (referenced.BudgetId != budgetId || referenced.ServiceOrderId != orderId))
                    return Result<Payment?>.Failure("payment.conflict", "Referenced payment does not match compensation.");
            }
            if (payment is null) return Result<Payment?>.Success(null);
            if (payment.ServiceOrderId != orderId || (paymentId is not null && payment.Id != paymentId))
                return Result<Payment?>.Failure("payment.conflict", "Compensation identity does not match.");
            if (operation == PaymentOperation.Cancel && payment.Status is PaymentStatus.Failed or PaymentStatus.Canceled or PaymentStatus.Refunded)
                return Result<Payment?>.Success(null);
            if (operation == PaymentOperation.Refund && payment.Status == PaymentStatus.Refunded)
            {
                if (completion?.Kind == IncomingCommandKind.RefundPayment) await completion.CompleteAsync(token);
                return Result<Payment?>.Success(payment);
            }
            var version = payment.Version;
            try
            {
                var existing = payment.Attempts.SingleOrDefault(x => x.Operation == operation);
                payment.BeginOperation(existing?.Id ?? Guid.NewGuid(), operation, clock.UtcNow);
            }
            catch (DomainException exception) { return Result<Payment?>.Failure(exception.Code, exception.Message); }
            if (payment.Version != version) await payments.SaveAsync(payment, version, token);
            return Result<Payment?>.Success(payment);
        }, ct);

    private Task<bool> RememberUncertaintyAsync(Guid id, CancellationToken ct) => uow.ExecuteAsync(async token =>
    {
        var payment = await payments.GetAsync(id, token);
        var unfinished = payment?.Attempts.SingleOrDefault(x => x.Status == AttemptStatus.Started);
        if (unfinished is null) return false;
        var version = payment!.Version; payment.MarkUncertain(unfinished.Id, clock.UtcNow);
        await payments.SaveAsync(payment, version, token); return true;
    }, ct);
}
