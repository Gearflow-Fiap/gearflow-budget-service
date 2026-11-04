using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;

namespace GearFlow.Billing.Application.Budgets;

public sealed record GetBudgetByIdQuery(Guid BudgetId) : IQuery<BudgetView?>;
public sealed record GetBudgetByServiceOrderQuery(Guid ServiceOrderId) : IQuery<BudgetView?>;

public sealed class BudgetQueryHandler(IBudgetReadModel readModel)
    : IQueryHandler<GetBudgetByIdQuery, BudgetView?>, IQueryHandler<GetBudgetByServiceOrderQuery, BudgetView?>
{
    public Task<BudgetView?> HandleAsync(GetBudgetByIdQuery query, CancellationToken ct) => readModel.GetAsync(query.BudgetId, ct);
    public Task<BudgetView?> HandleAsync(GetBudgetByServiceOrderQuery query, CancellationToken ct) => readModel.FindByServiceOrderAsync(query.ServiceOrderId, ct);
}
