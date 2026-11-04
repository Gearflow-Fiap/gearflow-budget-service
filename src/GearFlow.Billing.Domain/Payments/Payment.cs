using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;

namespace GearFlow.Billing.Domain.Payments;

public enum PaymentStatus { Created, Pending, Paid, Failed, Canceled, Refunded }
public enum PaymentOperation { Create, Cancel, Refund }
public enum AttemptStatus { Started, Uncertain, Succeeded, Rejected }

public sealed record AttemptSnapshot(Guid Id, PaymentOperation Operation, AttemptStatus Status, DateTimeOffset StartedOn, DateTimeOffset? CompletedOn);
public sealed record PaymentSnapshot(Guid Id, Guid BudgetId, Guid ServiceOrderId, Money Amount, PaymentStatus Status,
    string? ProviderOrderId, string? QrCode, string? TicketUrl, string? RefundId, DateTimeOffset CreatedOn,
    DateTimeOffset? ExpiresOn, DateTimeOffset? PaidOn, DateTimeOffset? RefundedOn, IReadOnlyList<AttemptSnapshot> Attempts, long Version);

public sealed class PaymentAttempt
{
    internal AttemptSnapshot Snapshot() => new(Id, Operation, Status, StartedOn, CompletedOn);
    internal static PaymentAttempt Restore(AttemptSnapshot state)
    {
        Ensure.That(Enum.IsDefined(state.Operation) && Enum.IsDefined(state.Status), "snapshot.attempt", "Invalid attempt state.");
        var attempt = new PaymentAttempt(state.Id, state.Operation, state.StartedOn);
        if (state.Status == AttemptStatus.Uncertain) attempt.Complete(state.Status, state.StartedOn);
        if (state.Status is AttemptStatus.Succeeded or AttemptStatus.Rejected)
            attempt.Complete(state.Status, state.CompletedOn ?? throw new DomainException("snapshot.attempt", "Completion date is required."));
        Ensure.That(state.Status is AttemptStatus.Succeeded or AttemptStatus.Rejected || state.CompletedOn is null,
            "snapshot.attempt", "Unfinished attempt has a completion date.");
        return attempt;
    }
    public Guid Id { get; }
    public PaymentOperation Operation { get; }
    public AttemptStatus Status { get; private set; }
    public DateTimeOffset StartedOn { get; }
    public DateTimeOffset? CompletedOn { get; private set; }
    internal PaymentAttempt(Guid id, PaymentOperation operation, DateTimeOffset now)
    {
        Id = Ensure.Identity(id);
        Operation = operation;
        StartedOn = Ensure.Utc(now);
    }

    internal void Complete(AttemptStatus status, DateTimeOffset now)
    {
        Ensure.Utc(now);
        Ensure.That(now >= StartedOn, "attempt.time", "Completion precedes start.");
        if (Status == status) return;
        Ensure.That(Status is AttemptStatus.Started or AttemptStatus.Uncertain,
            "attempt.terminal", "Attempt already has a definitive result.");
        Status = status;
        CompletedOn = status == AttemptStatus.Uncertain ? null : now;
    }
}

// Provider snapshots are local concepts, supplied only after the gateway has queried the provider.
public sealed record VerifiedPayment
{
    public string OrderId { get; }
    public Guid ExternalReference { get; }
    public Money Amount { get; }
    public VerifiedPayment(string orderId, Guid externalReference, Money amount)
    {
        OrderId = Ensure.Text(orderId, "provider order", 200);
        ExternalReference = Ensure.Identity(externalReference);
        Amount = amount ?? throw new ArgumentNullException(nameof(amount));
    }
}

public sealed class Payment : Aggregate
{
    public PaymentSnapshot Snapshot() => new(Id, BudgetId, ServiceOrderId, Amount, Status, ProviderOrderId, QrCode, TicketUrl,
        RefundId, CreatedOn, ExpiresOn, PaidOn, RefundedOn, Array.AsReadOnly(attempts.Select(x => x.Snapshot()).ToArray()), Version);

    private Payment(PaymentSnapshot state) : base(state.Id)
    {
        BudgetId = Ensure.Identity(state.BudgetId); ServiceOrderId = Ensure.Identity(state.ServiceOrderId);
        Amount = state.Amount ?? throw new ArgumentNullException(nameof(state)); CreatedOn = Ensure.Utc(state.CreatedOn);
        Ensure.That(Amount.Amount > 0 && Enum.IsDefined(state.Status), "snapshot.payment", "Invalid payment state.");
        Ensure.That(state.Status == PaymentStatus.Created || !string.IsNullOrWhiteSpace(state.ProviderOrderId), "snapshot.order", "Provider identity is required.");
        Ensure.That(state.Status is not (PaymentStatus.Paid or PaymentStatus.Refunded) || state.PaidOn is not null,
            "snapshot.paid", "Paid payment requires confirmation date.");
        Ensure.That(state.Status != PaymentStatus.Refunded || (state.RefundedOn is not null && !string.IsNullOrWhiteSpace(state.RefundId)),
            "snapshot.refund", "Refund evidence is required.");
        Status = state.Status; ProviderOrderId = state.ProviderOrderId; QrCode = state.QrCode; TicketUrl = state.TicketUrl;
        RefundId = state.RefundId; ExpiresOn = state.ExpiresOn; PaidOn = state.PaidOn; RefundedOn = state.RefundedOn;
        foreach (var timestamp in new[] { ExpiresOn, PaidOn, RefundedOn }.Where(x => x is not null))
            Ensure.That(Ensure.Utc(timestamp!.Value) >= CreatedOn, "snapshot.time", "Payment timestamp precedes creation.");
        Ensure.That(RefundedOn is null || (PaidOn is not null && RefundedOn >= PaidOn), "snapshot.refund", "Refund precedes payment.");
        if (TicketUrl is not null)
            Ensure.That(Uri.TryCreate(TicketUrl, UriKind.Absolute, out var ticket) && ticket.Scheme == "https", "snapshot.ticket", "Ticket URL must use HTTPS.");
        attempts.AddRange(state.Attempts.Select(PaymentAttempt.Restore));
        Ensure.That(attempts.Select(x => x.Id).Distinct().Count() == attempts.Count
            && attempts.Count(x => x.Status is AttemptStatus.Started or AttemptStatus.Uncertain) <= 1,
            "snapshot.attempts", "Duplicate or competing unfinished attempts.");
        Ensure.That(Status == PaymentStatus.Created || attempts.Count(x => x.Operation == PaymentOperation.Create && x.Status == AttemptStatus.Succeeded) == 1,
            "snapshot.creation", "Registered order requires one successful creation intent.");
        RestoreVersion(state.Version);
    }
    public static Payment Restore(PaymentSnapshot state) => new(state);
    private readonly List<PaymentAttempt> attempts = [];
    public Guid BudgetId { get; }
    public Guid ServiceOrderId { get; }
    public Money Amount { get; }
    public PaymentStatus Status { get; private set; }
    public string? ProviderOrderId { get; private set; }
    public string? QrCode { get; private set; }
    public string? TicketUrl { get; private set; }
    public string? RefundId { get; private set; }
    public DateTimeOffset CreatedOn { get; }
    public DateTimeOffset? ExpiresOn { get; private set; }
    public DateTimeOffset? PaidOn { get; private set; }
    public DateTimeOffset? RefundedOn { get; private set; }
    public IReadOnlyList<PaymentAttempt> Attempts => attempts.AsReadOnly();

    private Payment(Guid id, Budget budget, DateTimeOffset now) : base(id)
    {
        BudgetId = budget.Id;
        ServiceOrderId = budget.ServiceOrderId;
        Amount = budget.Total;
        CreatedOn = Ensure.Utc(now);
        ExpiresOn = now.AddMinutes(30);
    }

    public static Payment Request(Guid id, Budget budget, Guid serviceOrderId, Money amount, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(budget);
        budget.EnsureChargeable(serviceOrderId, amount);
        return new Payment(id, budget, now);
    }

    public PaymentAttempt BeginOperation(Guid operationId, PaymentOperation operation, DateTimeOffset now)
    {
        Ensure.Utc(now);
        Ensure.That(Enum.IsDefined(operation), "attempt.operation", "Unknown operation.");
        var existing = attempts.SingleOrDefault(x => x.Id == operationId);
        if (existing is not null)
        {
            Ensure.That(existing.Operation == operation, "attempt.conflict", "Operation ID reused for different intent.");
            return existing;
        }
        Ensure.That(!attempts.Any(x => x.Status is AttemptStatus.Started or AttemptStatus.Uncertain),
            "payment.uncertain", "Resolve existing operation before starting another.");
        Ensure.That(operation switch
        {
            PaymentOperation.Create => Status == PaymentStatus.Created,
            PaymentOperation.Cancel => Status == PaymentStatus.Pending,
            PaymentOperation.Refund => Status == PaymentStatus.Paid,
            _ => false
        }, "payment.state", "Operation is not allowed in this payment state.");
        var attempt = new PaymentAttempt(operationId, operation, now);
        attempts.Add(attempt);
        Changed();
        return attempt;
    }

    public void MarkUncertain(Guid operationId, DateTimeOffset now)
    {
        var attempt = Attempt(operationId);
        if (attempt.Status == AttemptStatus.Uncertain) return;
        attempt.Complete(AttemptStatus.Uncertain, now);
        // Technical uncertainty is deliberately not a PaymentFailed fact.
        Changed();
    }

    public void RegisterCharge(Guid operationId, VerifiedPayment evidence, string qrCode, string ticketUrl,
        DateTimeOffset expiresOn, DateTimeOffset now)
    {
        Ensure.Utc(now);
        ValidateEvidence(evidence, requireOrder: false);
        if (Status is PaymentStatus.Pending or PaymentStatus.Paid or PaymentStatus.Refunded)
        {
            Ensure.That(ProviderOrderId == evidence.OrderId, "payment.order", "A different charge already exists.");
            return;
        }
        Ensure.That(Status == PaymentStatus.Created, "payment.state", "Charge creation is no longer allowed.");
        Ensure.That(Ensure.Utc(expiresOn) > now, "payment.expiry", "Charge expiry must be in the future.");
        qrCode = Ensure.Text(qrCode, "QR code", 10000);
        ticketUrl = Ensure.Text(ticketUrl, "ticket URL", 2000);
        Ensure.That(Uri.TryCreate(ticketUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
            "payment.ticket", "Ticket URL must use HTTPS.");
        Complete(operationId, PaymentOperation.Create, now);
        ProviderOrderId = evidence.OrderId;
        QrCode = qrCode;
        TicketUrl = ticketUrl;
        ExpiresOn = expiresOn;
        Status = PaymentStatus.Pending;
        Record("PaymentRequested", ServiceOrderId, now);
    }

    // Orders may be accepted asynchronously before Pix display data is available.
    public void RegisterOrder(Guid operationId, VerifiedPayment evidence, string? qrCode, string? ticketUrl,
        DateTimeOffset? expiresOn, DateTimeOffset now)
    {
        Ensure.Utc(now); ValidateEvidence(evidence, requireOrder: false);
        Ensure.That(ProviderOrderId is null || ProviderOrderId == evidence.OrderId, "payment.order", "A different charge already exists.");
        if (ticketUrl is not null)
            Ensure.That(Uri.TryCreate(ticketUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https", "payment.ticket", "Ticket URL must use HTTPS.");
        if (expiresOn is { } expiry) Ensure.That(Ensure.Utc(expiry) > CreatedOn, "payment.expiry", "Invalid charge expiry.");
        if (Status == PaymentStatus.Pending)
        {
            if ((QrCode is null && qrCode is not null) || (TicketUrl is null && ticketUrl is not null))
            { QrCode ??= qrCode; TicketUrl ??= ticketUrl; Changed(); }
            return;
        }
        if (Status is PaymentStatus.Paid or PaymentStatus.Refunded or PaymentStatus.Canceled or PaymentStatus.Failed) return;
        Complete(operationId, PaymentOperation.Create, now);
        ProviderOrderId = evidence.OrderId; QrCode = qrCode; TicketUrl = ticketUrl; ExpiresOn = expiresOn ?? ExpiresOn;
        Status = PaymentStatus.Pending;
        Record("PaymentRequested", ServiceOrderId, now);
    }

    public void ObserveCancellation(VerifiedPayment evidence, DateTimeOffset now)
    {
        Ensure.Utc(now); ValidateEvidence(evidence);
        if (Status is PaymentStatus.Canceled or PaymentStatus.Paid or PaymentStatus.Refunded or PaymentStatus.Failed) return;
        Ensure.That(Status == PaymentStatus.Pending, "payment.state", "Only a pending payment can be canceled.");
        var cancellations = attempts.Where(x => x.Operation == PaymentOperation.Cancel
            && x.Status is AttemptStatus.Started or AttemptStatus.Uncertain).ToArray();
        Ensure.That(cancellations.All(x => now >= x.StartedOn), "attempt.time", "Cancellation predates intent.");
        foreach (var attempt in cancellations) attempt.Complete(AttemptStatus.Succeeded, now);
        Status = PaymentStatus.Canceled; Record("PaymentCanceled", ServiceOrderId, now);
    }

    public void Confirm(VerifiedPayment evidence, DateTimeOffset now)
    {
        Ensure.Utc(now);
        ValidateEvidence(evidence);
        if (Status is PaymentStatus.Paid or PaymentStatus.Refunded) return;
        // A payment winning a cancellation race stays paid and requires explicit refund.
        Ensure.That(Status is PaymentStatus.Pending or PaymentStatus.Canceled,
            "payment.state", "Payment cannot be confirmed from this state.");
        var cancellations = attempts.Where(x => x.Operation == PaymentOperation.Cancel
            && x.Status is AttemptStatus.Started or AttemptStatus.Uncertain).ToArray();
        Ensure.That(cancellations.All(x => now >= x.StartedOn), "attempt.time", "Confirmation precedes cancellation intent.");
        foreach (var cancellation in cancellations)
            cancellation.Complete(AttemptStatus.Rejected, now);
        Status = PaymentStatus.Paid;
        PaidOn = now;
        Record("PaymentConfirmed", ServiceOrderId, now);
    }

    public void Fail(VerifiedPayment evidence, string reason, DateTimeOffset now)
    {
        Ensure.Utc(now);
        ValidateEvidence(evidence);
        Ensure.Text(reason, "reason");
        if (Status is PaymentStatus.Failed or PaymentStatus.Paid or PaymentStatus.Refunded or PaymentStatus.Canceled) return;
        Ensure.That(Status == PaymentStatus.Pending, "payment.state", "Only pending payments can fail.");
        Status = PaymentStatus.Failed;
        Record("PaymentFailed", ServiceOrderId, now);
    }

    public void ConfirmCancellation(Guid operationId, VerifiedPayment evidence, DateTimeOffset now)
    {
        Ensure.Utc(now);
        ValidateEvidence(evidence);
        if (Status == PaymentStatus.Canceled) return;
        Ensure.That(Status == PaymentStatus.Pending, "payment.paid", "Confirmed payment requires refund instead of cancellation.");
        Complete(operationId, PaymentOperation.Cancel, now);
        Status = PaymentStatus.Canceled;
        Record("PaymentCanceled", ServiceOrderId, now);
    }

    public void ConfirmRefund(Guid operationId, VerifiedPayment evidence, string refundId, DateTimeOffset now)
    {
        Ensure.Utc(now);
        ValidateEvidence(evidence);
        refundId = Ensure.Text(refundId, "refund ID", 200);
        if (Status == PaymentStatus.Refunded)
        {
            Ensure.That(RefundId == refundId, "payment.refund", "Refund evidence conflicts with completed refund.");
            return;
        }
        Ensure.That(Status == PaymentStatus.Paid, "payment.state", "Only paid payments can be refunded.");
        Complete(operationId, PaymentOperation.Refund, now);
        RefundId = refundId;
        RefundedOn = now;
        Status = PaymentStatus.Refunded;
        Record("PaymentRefunded", ServiceOrderId, now);
    }

    private PaymentAttempt Attempt(Guid id) => attempts.SingleOrDefault(x => x.Id == id)
        ?? throw new DomainException("attempt.missing", "Operation intent must be persisted before executing it.");

    private void Complete(Guid id, PaymentOperation operation, DateTimeOffset now)
    {
        var attempt = Attempt(id);
        Ensure.That(attempt.Operation == operation, "attempt.operation", "Operation does not match evidence.");
        attempt.Complete(AttemptStatus.Succeeded, now);
    }

    private void ValidateEvidence(VerifiedPayment evidence, bool requireOrder = true)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Ensure.That(evidence.ExternalReference == BudgetId && evidence.Amount == Amount,
            "payment.evidence", "Remote amount, currency or reference does not match.");
        Ensure.That(!requireOrder || evidence.OrderId == ProviderOrderId, "payment.order", "Provider order does not match.");
    }
}
