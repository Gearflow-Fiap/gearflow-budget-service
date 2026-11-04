using GearFlow.Billing.Domain.Core;

namespace GearFlow.Billing.Domain.Budgets;

public sealed record CommercialFenceSnapshot(Guid ServiceOrderId, bool CancellationRequested, bool RefundRequested,
    string? Reason, DateTimeOffset CreatedOn, long Version);

// Durable business guard for compensations that arrive before their budget/payment.
// Referenced by service-order identity; it never imports an OS aggregate.
public sealed class CommercialFence : Aggregate
{
    public bool CancellationRequested { get; private set; }
    public bool RefundRequested { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset CreatedOn { get; }
    public CommercialFence(Guid serviceOrderId, DateTimeOffset now) : base(serviceOrderId) => CreatedOn = Ensure.Utc(now);
    public void AuthorizeCommercialAction()
    {
        Ensure.That(!CancellationRequested && !RefundRequested, "budget.compensating", "The service order is fenced for compensation.");
        // A versioned write serializes authorization against a concurrent compensation.
        Changed();
    }
    public void RequestCancellation(string reason) { reason = Ensure.Text(reason, "reason"); Reason ??= reason; CancellationRequested = true; Changed(); }
    public void RequestRefund(string reason) { reason = Ensure.Text(reason, "reason"); Reason ??= reason; RefundRequested = true; Changed(); }
    public CommercialFenceSnapshot Snapshot() => new(Id, CancellationRequested, RefundRequested, Reason, CreatedOn, Version);
    public static CommercialFence Restore(CommercialFenceSnapshot state)
    {
        var fence = new CommercialFence(state.ServiceOrderId, state.CreatedOn);
        Ensure.That((!state.CancellationRequested && !state.RefundRequested) || !string.IsNullOrWhiteSpace(state.Reason), "fence.reason", "Compensation reason required.");
        if (state.Reason is not null) Ensure.Text(state.Reason, "reason");
        fence.CancellationRequested = state.CancellationRequested; fence.RefundRequested = state.RefundRequested; fence.Reason = state.Reason;
        fence.RestoreVersion(state.Version); return fence;
    }
}
