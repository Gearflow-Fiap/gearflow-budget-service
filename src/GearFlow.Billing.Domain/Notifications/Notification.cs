using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;

namespace GearFlow.Billing.Domain.Notifications;

public enum NotificationStatus { Pending, Sending, Sent, Failed, Suppressed }

public sealed record NotificationSnapshot(Guid Id, Guid ServiceOrderId, Guid BudgetId, string DeduplicationKey,
    EmailAddress Recipient, string Template, NotificationStatus Status, int Attempts, DateTimeOffset NextAttemptOn,
    DateTimeOffset? LeaseUntil, Guid? LeaseOwner, long Version);

public sealed class Notification : Aggregate
{
    public NotificationSnapshot Snapshot() => new(Id, ServiceOrderId, BudgetId, DeduplicationKey, Recipient, Template,
        Status, Attempts, NextAttemptOn, LeaseUntil, LeaseOwner, Version);
    public static Notification Restore(NotificationSnapshot state)
    {
        var result = new Notification(state.Id, state.ServiceOrderId, state.BudgetId, state.DeduplicationKey,
            state.Recipient, state.Template, state.NextAttemptOn);
        Ensure.That(Enum.IsDefined(state.Status) && state.Attempts >= 0
            && (state.Status != NotificationStatus.Sending || (state.LeaseUntil is not null && state.LeaseOwner is not null)),
            "snapshot.notification", "Invalid notification snapshot.");
        result.Status = state.Status; result.Attempts = state.Attempts;
        result.LeaseUntil = state.LeaseUntil; result.LeaseOwner = state.LeaseOwner;
        result.RestoreVersion(state.Version);
        return result;
    }
    public Guid ServiceOrderId { get; }
    public Guid BudgetId { get; }
    public string DeduplicationKey { get; }
    public EmailAddress Recipient { get; }
    public string Template { get; }
    public NotificationStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptOn { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public Guid? LeaseOwner { get; private set; }

    public Notification(Guid id, Guid serviceOrderId, Guid budgetId, string deduplicationKey,
        EmailAddress recipient, string template, DateTimeOffset now) : base(id)
    {
        ServiceOrderId = Ensure.Identity(serviceOrderId);
        BudgetId = Ensure.Identity(budgetId);
        DeduplicationKey = Ensure.Text(deduplicationKey, "deduplication key", 200);
        Recipient = recipient ?? throw new ArgumentNullException(nameof(recipient));
        Template = Ensure.Text(template, "template", 100);
        NextAttemptOn = Ensure.Utc(now);
    }

    public void Claim(Guid owner, DateTimeOffset now, TimeSpan lease)
    {
        Ensure.Utc(now);
        Ensure.Identity(owner);
        Ensure.That(lease > TimeSpan.Zero && lease <= TimeSpan.FromMinutes(10), "notification.lease", "Invalid lease duration.");
        Ensure.That(Status is not (NotificationStatus.Sent or NotificationStatus.Failed or NotificationStatus.Suppressed) && now >= NextAttemptOn
            && (LeaseUntil is null || LeaseUntil <= now), "notification.claim", "Notification is not available.");
        Status = NotificationStatus.Sending;
        LeaseOwner = owner;
        LeaseUntil = now + lease;
        Attempts++;
        Changed();
    }

    public void MarkSent(Guid owner, DateTimeOffset now)
    {
        CheckLease(owner, now);
        Status = NotificationStatus.Sent;
        LeaseUntil = null;
        LeaseOwner = null;
        Record("NotificationSent", ServiceOrderId, now);
    }

    public void Retry(Guid owner, DateTimeOffset now, TimeSpan delay, int maxAttempts)
    {
        CheckLease(owner, now);
        Ensure.That(delay > TimeSpan.Zero && maxAttempts > 0, "notification.retry", "Invalid retry policy.");
        Status = Attempts >= maxAttempts ? NotificationStatus.Failed : NotificationStatus.Pending;
        NextAttemptOn = now + delay;
        LeaseUntil = null;
        LeaseOwner = null;
        Changed();
    }

    public void Suppress(Guid owner, DateTimeOffset now)
    {
        CheckLease(owner, now); Status = NotificationStatus.Suppressed; LeaseUntil = null; LeaseOwner = null;
        Record("NotificationSuppressed", ServiceOrderId, now);
    }

    private void CheckLease(Guid owner, DateTimeOffset now)
    {
        Ensure.Utc(now);
        Ensure.That(Status == NotificationStatus.Sending && LeaseOwner == owner && LeaseUntil > now,
            "notification.lease", "Notification lease is absent, expired or owned by another worker.");
    }
}
