using GearFlow.Billing.Application.Common;

namespace GearFlow.Billing.Application.Payments;

public interface IPaymentCompensator
{
    Task<Result<Unit>> CancelAsync(Guid serviceOrderId, Guid budgetId, CancellationToken ct);
}

public sealed record PaymentCandidate(Guid PaymentId, string? OrderId);
public interface IPaymentRecoveryReadModel
{
    Task<IReadOnlyList<PaymentCandidate>> FindDueAsync(DateTimeOffset now, int limit, CancellationToken ct);
}

public sealed record PaymentWork(string Key, string? OrderId, Guid? PaymentId, Guid Owner, int Attempts);
public interface IPaymentWorkStore
{
    Task EnqueueAsync(string key, string? orderId, Guid? paymentId, DateTimeOffset now, CancellationToken ct);
    Task<PaymentWork?> ClaimAsync(Guid owner, DateTimeOffset now, TimeSpan lease, CancellationToken ct);
    Task CompleteAsync(PaymentWork work, DateTimeOffset now, CancellationToken ct);
    Task DeferAsync(PaymentWork work, DateTimeOffset nextAttemptOn, string code, CancellationToken ct);
}
