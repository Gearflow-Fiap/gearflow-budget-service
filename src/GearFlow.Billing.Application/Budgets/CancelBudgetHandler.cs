using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Payments;
using GearFlow.Billing.Application.Payments;

namespace GearFlow.Billing.Application.Budgets;

public sealed class CancelBudgetHandler(IBudgetRepository budgets, IPaymentRepository payments,
    IOutboxWriter outbox, IUnitOfWork uow, IClock clock, IPaymentCompensator? compensator = null,
    ICommandCompletion? completion = null, ICommercialFenceRepository? fences = null)
    : ICommandHandler<CancelBudgetCommand, Result<Unit>>
{
    public async Task<Result<Unit>> HandleAsync(CancelBudgetCommand command, CancellationToken ct)
    {
        try
        {
            Ensure.Identity(command.ServiceOrderId); Ensure.Identity(command.BudgetId); Ensure.Text(command.Reason, "reason");
        }
        catch (DomainException exception) { return Result<Unit>.Failure(exception.Code, exception.Message); }
        if (fences is not null)
            await uow.ExecuteAsync(async token =>
            {
                var existingBudget = await budgets.GetAsync(command.BudgetId, token);
                Ensure.That(existingBudget is null || existingBudget.ServiceOrderId == command.ServiceOrderId, "budget.order", "Service order does not match.");
                var fence = await fences.GetOrCreateAsync(command.ServiceOrderId, token); var version = fence.Version;
                fence.RequestCancellation(command.Reason); await fences.SaveAsync(fence, version, token); return true;
            }, ct);
        if (compensator is not null)
        {
            var compensation = await compensator.CancelAsync(command.ServiceOrderId, command.BudgetId, ct);
            if (!compensation.IsSuccess) return compensation;
        }
        return await uow.ExecuteAsync(async token =>
        {
            try
            {
                Ensure.Identity(command.ServiceOrderId);
                Ensure.Identity(command.BudgetId);
                Ensure.Text(command.Reason, "reason");
            }
            catch (DomainException exception) { return Result<Unit>.Failure(exception.Code, exception.Message); }
            var budget = await budgets.GetAsync(command.BudgetId, token);
            if (budget is null)
            {
                await outbox.AppendAsync(new BudgetCanceled(Guid.NewGuid(), command.ServiceOrderId,
                    command.CausationId, clock.UtcNow, command.BudgetId, command.Reason), token);
                if (completion is not null) await completion.CompleteAsync(token);
                return Result<Unit>.Success(default);
            }
            if (budget.ServiceOrderId != command.ServiceOrderId)
                return Result<Unit>.Failure("budget.order", "Service order does not match.");
            var payment = await payments.FindByBudgetAsync(budget.Id, token);
            if (payment is not null && payment.Status is not (PaymentStatus.Canceled or PaymentStatus.Failed or PaymentStatus.Refunded))
                return Result<Unit>.Failure("payment.compensation_required", "Resolve the remote charge or refund before canceling this budget.");
            var version = budget.Version;
            try { budget.Cancel(command.Reason, clock.UtcNow); }
            catch (DomainException exception) { return Result<Unit>.Failure(exception.Code, exception.Message); }
            if (budget.Version == version)
            {
                if (completion is not null) await completion.CompleteAsync(token);
                return Result<Unit>.Success(default);
            }
            await budgets.SaveAsync(budget, version, token);
            await outbox.AppendAsync(new BudgetCanceled(Guid.NewGuid(), budget.ServiceOrderId, command.CausationId,
                clock.UtcNow, budget.Id, budget.Reason!), token);
            if (completion is not null) await completion.CompleteAsync(token);
            return Result<Unit>.Success(default);
        }, ct);
    }
}
