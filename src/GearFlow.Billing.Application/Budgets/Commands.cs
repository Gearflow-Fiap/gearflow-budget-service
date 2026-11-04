using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Domain.Budgets;

namespace GearFlow.Billing.Application.Budgets;

public sealed record GenerateBudgetCommand(Guid ServiceOrderId, Customer Customer, IReadOnlyList<BudgetItem> Items,
    DateTimeOffset ExpiresOn, Guid? CausationId = null) : ICommand<Result<Guid>>;
public sealed record ApproveBudgetCommand(Guid BudgetId, string ApprovalToken) : ICommand<Result<Unit>>;
public sealed record RejectBudgetCommand(Guid BudgetId, string ApprovalToken, string? Reason = null) : ICommand<Result<Unit>>;
public sealed record CancelBudgetCommand(Guid ServiceOrderId, Guid BudgetId, string Reason,
    Guid? CausationId = null) : ICommand<Result<Unit>>;
