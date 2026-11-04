using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;

namespace GearFlow.Billing.Application.Payments;

public sealed record GetPaymentByIdQuery(Guid PaymentId) : IQuery<PaymentView?>;
public sealed record GetPaymentByBudgetQuery(Guid BudgetId) : IQuery<PaymentView?>;

public sealed class PaymentQueryHandler(IPaymentReadModel readModel)
    : IQueryHandler<GetPaymentByIdQuery, PaymentView?>, IQueryHandler<GetPaymentByBudgetQuery, PaymentView?>
{
    public Task<PaymentView?> HandleAsync(GetPaymentByIdQuery query, CancellationToken ct) => readModel.GetAsync(query.PaymentId, ct);
    public Task<PaymentView?> HandleAsync(GetPaymentByBudgetQuery query, CancellationToken ct) => readModel.FindByBudgetAsync(query.BudgetId, ct);
}
