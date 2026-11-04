namespace GearFlow.Billing.Application.Events;

// Internal events; the adapter maps these records to versioned external contracts.
public abstract record IntegrationEvent(Guid MessageId, Guid ServiceOrderId, Guid? CausationId, DateTimeOffset OccurredOn);

public sealed record BudgetGenerated(Guid MessageId, Guid ServiceOrderId, Guid? CausationId,
    DateTimeOffset OccurredOn, Guid BudgetId, decimal TotalAmount, string Currency, DateTimeOffset ExpiresOn)
    : IntegrationEvent(MessageId, ServiceOrderId, CausationId, OccurredOn);
public sealed record BudgetApproved(Guid MessageId, Guid ServiceOrderId, Guid? CausationId,
    DateTimeOffset OccurredOn, Guid BudgetId, decimal TotalAmount, string Currency)
    : IntegrationEvent(MessageId, ServiceOrderId, CausationId, OccurredOn);
public sealed record BudgetRejected(Guid MessageId, Guid ServiceOrderId, Guid? CausationId,
    DateTimeOffset OccurredOn, Guid BudgetId, string? Reason)
    : IntegrationEvent(MessageId, ServiceOrderId, CausationId, OccurredOn);
public sealed record BudgetCanceled(Guid MessageId, Guid ServiceOrderId, Guid? CausationId,
    DateTimeOffset OccurredOn, Guid BudgetId, string Reason)
    : IntegrationEvent(MessageId, ServiceOrderId, CausationId, OccurredOn);
public sealed record BudgetGenerationFailed(Guid MessageId, Guid ServiceOrderId, Guid? CausationId,
    DateTimeOffset OccurredOn, string Code, string Reason)
    : IntegrationEvent(MessageId, ServiceOrderId, CausationId, OccurredOn);

public sealed record PaymentRequested(Guid MessageId, Guid ServiceOrderId, Guid? CausationId, DateTimeOffset OccurredOn,
    Guid BudgetId, Guid PaymentId, decimal Amount, string Currency, string MercadoPagoOrderId, DateTimeOffset ExpiresOn)
    : IntegrationEvent(MessageId, ServiceOrderId, CausationId, OccurredOn);
public sealed record PaymentConfirmed(Guid MessageId, Guid ServiceOrderId, Guid? CausationId, DateTimeOffset OccurredOn,
    Guid BudgetId, Guid PaymentId, decimal Amount, string Currency, string MercadoPagoOrderId, DateTimeOffset PaidOn)
    : IntegrationEvent(MessageId, ServiceOrderId, CausationId, OccurredOn);
public sealed record PaymentFailed(Guid MessageId, Guid ServiceOrderId, Guid? CausationId, DateTimeOffset OccurredOn,
    Guid BudgetId, Guid? PaymentId, string Code, string Reason)
    : IntegrationEvent(MessageId, ServiceOrderId, CausationId, OccurredOn);
public sealed record PaymentRefunded(Guid MessageId, Guid ServiceOrderId, Guid? CausationId, DateTimeOffset OccurredOn,
    Guid BudgetId, Guid PaymentId, decimal Amount, string Currency, string? RefundId)
    : IntegrationEvent(MessageId, ServiceOrderId, CausationId, OccurredOn);
