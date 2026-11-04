using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Domain.Core;

namespace GearFlow.Billing.Application.Payments;

public sealed record RequestPaymentCommand(Guid ServiceOrderId, Guid BudgetId, Money Amount, Guid? CausationId = null) : ICommand<Result<Guid>>;
public sealed record ProcessPaymentWebhookCommand(string OrderId) : ICommand<Result<Unit>>;
public sealed record RecoverPaymentCommand(Guid PaymentId) : ICommand<Result<Unit>>;
public sealed record ReconcilePaymentsCommand : ICommand<Result<Unit>>;
public sealed record RefundPaymentCommand(Guid ServiceOrderId, Guid BudgetId, Guid PaymentId, string Reason,
    Guid? CausationId = null) : ICommand<Result<Unit>>;
