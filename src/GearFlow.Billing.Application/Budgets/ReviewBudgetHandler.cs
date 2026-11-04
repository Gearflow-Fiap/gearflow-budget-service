using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Core;

namespace GearFlow.Billing.Application.Budgets;

public sealed class ReviewBudgetHandler(IBudgetRepository budgets, IApprovalTokenService tokens,
    IOutboxWriter outbox, IUnitOfWork uow, IClock clock, ICommercialFenceRepository? fences = null)
    : ICommandHandler<ApproveBudgetCommand, Result<Unit>>, ICommandHandler<RejectBudgetCommand, Result<Unit>>
{
    public Task<Result<Unit>> HandleAsync(ApproveBudgetCommand command, CancellationToken ct) =>
        Review(command.BudgetId, command.ApprovalToken, true, null, ct);
    public Task<Result<Unit>> HandleAsync(RejectBudgetCommand command, CancellationToken ct) =>
        Review(command.BudgetId, command.ApprovalToken, false, command.Reason, ct);

    private Task<Result<Unit>> Review(Guid id, string token, bool approve, string? reason, CancellationToken ct) =>
        uow.ExecuteAsync(async cancellation =>
        {
            var budget = await budgets.GetAsync(id, cancellation);
            if (budget is null) return Result<Unit>.Failure("budget.not_found", "Budget not found.");
            if (!tokens.IsValid(token, id, budget.ExpiresOn))
                return Result<Unit>.Failure("approval.forbidden", "Invalid or expired approval token.");
            if (fences is not null)
            {
                var fence = await fences.GetOrCreateAsync(budget.ServiceOrderId, cancellation); var fenceVersion = fence.Version;
                fence.AuthorizeCommercialAction(); await fences.SaveAsync(fence, fenceVersion, cancellation);
            }
            var version = budget.Version;
            try
            {
                if (approve) budget.Approve(clock.UtcNow);
                else budget.Reject(clock.UtcNow, reason);
            }
            catch (DomainException exception) { return Result<Unit>.Failure(exception.Code, exception.Message); }
            if (budget.Version == version) return Result<Unit>.Success(default);
            await budgets.SaveAsync(budget, version, cancellation);
            IntegrationEvent integrationEvent = approve
                ? new BudgetApproved(Guid.NewGuid(), budget.ServiceOrderId, null, clock.UtcNow,
                    id, budget.Total.Amount, budget.Total.Currency)
                : new BudgetRejected(Guid.NewGuid(), budget.ServiceOrderId, null, clock.UtcNow, id, budget.Reason);
            await outbox.AppendAsync(integrationEvent, cancellation);
            return Result<Unit>.Success(default);
        }, ct);
}
