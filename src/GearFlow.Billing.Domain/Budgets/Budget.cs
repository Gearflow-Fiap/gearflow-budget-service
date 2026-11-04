using GearFlow.Billing.Domain.Core;

namespace GearFlow.Billing.Domain.Budgets;

public enum BudgetStatus { Pending, Approved, Rejected, Expired, AwaitingPayment, Paid, Canceled, Refunded }

public sealed record BudgetSnapshot(Guid Id, Guid ServiceOrderId, Customer Customer, IReadOnlyList<BudgetItem> Items,
    BudgetStatus Status, DateTimeOffset CreatedOn, DateTimeOffset ExpiresOn, DateTimeOffset? ReviewedOn, string? Reason, long Version);

public sealed class Budget : Aggregate
{
    public BudgetSnapshot Snapshot() => new(Id, ServiceOrderId, Customer, Items, Status, CreatedOn, ExpiresOn, ReviewedOn, Reason, Version);
    public static Budget Restore(BudgetSnapshot state)
    {
        var budget = new Budget(state.Id, state.ServiceOrderId, state.Customer, state.Items, state.CreatedOn, state.ExpiresOn);
        Ensure.That(Enum.IsDefined(state.Status) && state.Version >= 1, "snapshot.budget", "Invalid budget snapshot.");
        if (state.ReviewedOn is { } reviewed)
            Ensure.That(Ensure.Utc(reviewed) >= state.CreatedOn && reviewed < state.ExpiresOn, "snapshot.review", "Invalid review window.");
        Ensure.That(state.Status is not (BudgetStatus.Approved or BudgetStatus.Rejected or BudgetStatus.AwaitingPayment or BudgetStatus.Paid or BudgetStatus.Refunded)
            || state.ReviewedOn is not null, "snapshot.review", "Reviewed budget requires decision date.");
        budget.Status = state.Status; budget.ReviewedOn = state.ReviewedOn; budget.Reason = state.Reason;
        budget.RestoreVersion(state.Version);
        return budget;
    }
    public Guid ServiceOrderId { get; }
    public Customer Customer { get; }
    public IReadOnlyList<BudgetItem> Items { get; }
    public Money Total { get; }
    public BudgetStatus Status { get; private set; }
    public DateTimeOffset CreatedOn { get; }
    public DateTimeOffset ExpiresOn { get; }
    public DateTimeOffset? ReviewedOn { get; private set; }
    public string? Reason { get; private set; }

    public Budget(Guid id, Guid serviceOrderId, Customer customer, IEnumerable<BudgetItem> items,
        DateTimeOffset createdOn, DateTimeOffset expiresOn) : base(id)
    {
        ServiceOrderId = Ensure.Identity(serviceOrderId);
        Customer = customer ?? throw new ArgumentNullException(nameof(customer));
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items.ToArray();
        Ensure.That(snapshot.Length > 0 && snapshot.All(x => x is not null), "budget.items", "Items are required.");
        Items = Array.AsReadOnly(snapshot);
        Total = new Money(Items.Sum(i => i.Subtotal.Amount));
        Ensure.That(Total.Amount > 0, "budget.zero", "A payable budget must have a positive total.");
        CreatedOn = Ensure.Utc(createdOn);
        ExpiresOn = Ensure.Utc(expiresOn);
        Ensure.That(expiresOn > createdOn, "budget.expiry", "Expiry must be in the future.");
        Record("BudgetGenerated", ServiceOrderId, createdOn);
    }

    public void Approve(DateTimeOffset now) => Review(BudgetStatus.Approved, "BudgetApproved", now);
    public void Reject(DateTimeOffset now, string? reason = null)
    {
        var normalized = reason is null ? null : Ensure.Text(reason, "reason");
        if (Status == BudgetStatus.Rejected)
        {
            Ensure.That(Reason == normalized, "budget.conflict", "Repeated decision has a different reason.");
            return;
        }
        Review(BudgetStatus.Rejected, "BudgetRejected", now);
        Reason = normalized;
    }

    private void Review(BudgetStatus target, string fact, DateTimeOffset now)
    {
        Ensure.Utc(now);
        if (Status == target) return;
        Ensure.That(Status == BudgetStatus.Pending, "budget.state", "Budget has already been decided.");
        Ensure.That(now < ExpiresOn && now >= CreatedOn, "budget.expired", "Budget is outside its review window.");
        Status = target;
        ReviewedOn = now;
        Record(fact, ServiceOrderId, now);
    }

    public void EnsureChargeable(Guid serviceOrderId, Money amount)
    {
        Ensure.That(ServiceOrderId == serviceOrderId, "payment.order", "Service order does not match.");
        Ensure.That(Total == amount, "payment.amount", "Amount or currency does not match.");
        Ensure.That(Status == BudgetStatus.Approved, "budget.state", "Budget must be approved before charging.");
    }

    public void AwaitPayment(DateTimeOffset now)
    {
        Ensure.Utc(now);
        if (Status == BudgetStatus.AwaitingPayment) return;
        Ensure.That(Status == BudgetStatus.Approved, "budget.state", "Budget must be approved.");
        Status = BudgetStatus.AwaitingPayment;
        Record("BudgetAwaitingPayment", ServiceOrderId, now);
    }

    public void MarkPaid(DateTimeOffset now)
    {
        Ensure.Utc(now);
        if (Status is BudgetStatus.Paid or BudgetStatus.Refunded) return;
        Ensure.That(Status == BudgetStatus.AwaitingPayment, "budget.state", "Budget must be awaiting payment.");
        Status = BudgetStatus.Paid;
        Record("BudgetPaid", ServiceOrderId, now);
    }

    public void Expire(DateTimeOffset now)
    {
        Ensure.Utc(now);
        if (Status == BudgetStatus.Expired) return;
        Ensure.That(Status == BudgetStatus.Pending && now >= ExpiresOn, "budget.state", "Only an overdue pending budget can expire.");
        Status = BudgetStatus.Expired;
        Reason = "approval_expired";
        Record("BudgetExpired", ServiceOrderId, now);
    }

    // Application must confirm cancellation of any remote charge before calling this operation.
    public void Cancel(string reason, DateTimeOffset now)
    {
        Ensure.Utc(now);
        reason = Ensure.Text(reason, "reason");
        if (Status == BudgetStatus.Canceled) return;
        if (reason == "approval_expired" && Status is BudgetStatus.Pending or BudgetStatus.Expired)
        { Expire(now); return; }
        Ensure.That(Status != BudgetStatus.Paid, "budget.paid", "Paid budgets require confirmed refund.");
        Status = BudgetStatus.Canceled;
        Reason = reason;
        Record("BudgetCanceled", ServiceOrderId, now);
    }

    public void MarkRefunded(DateTimeOffset now)
    {
        Ensure.Utc(now);
        if (Status == BudgetStatus.Refunded) return;
        Ensure.That(Status == BudgetStatus.Paid, "budget.state", "Only paid budgets can be refunded.");
        Status = BudgetStatus.Refunded;
        Record("BudgetRefunded", ServiceOrderId, now);
    }

    public void RecognizeLatePayment(DateTimeOffset now)
    {
        Ensure.Utc(now);
        Ensure.That(Status == BudgetStatus.Canceled && ReviewedOn is not null, "budget.state", "Late payment recovery requires a previously reviewed canceled budget.");
        Status = BudgetStatus.Paid;
        Record("BudgetLatePaymentRecognized", ServiceOrderId, now);
    }
}
