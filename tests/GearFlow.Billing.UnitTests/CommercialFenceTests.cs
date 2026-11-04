using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Notifications;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.UnitTests;

public class CommercialFenceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Compensation_before_entity_blocks_later_commercial_action_and_survives_restore(bool refund)
    {
        var fence = new CommercialFence(Guid.NewGuid(), DateTimeOffset.Parse("2026-11-03T12:00:00Z"));
        fence.AuthorizeCommercialAction();
        if (refund) fence.RequestRefund("Saga compensation"); else fence.RequestCancellation("Saga compensation");
        var restored = CommercialFence.Restore(fence.Snapshot());
        Assert.Equal(fence.Version, restored.Version);
        Assert.Throws<DomainException>(() => restored.AuthorizeCommercialAction());
        Assert.Throws<DomainException>(() => restored.RequestCancellation(""));
    }

    [Fact]
    public void Approval_expiration_command_keeps_expired_status_and_is_idempotent()
    {
        var now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        var budget = new Budget(Guid.NewGuid(), Guid.NewGuid(), new Customer("Ana", new EmailAddress("ana@example.com")),
            [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Serviço", new Money(10), 1)], now, now.AddHours(1));
        Assert.Throws<DomainException>(() => budget.Cancel("approval_expired", now));
        budget.Cancel("approval_expired", now.AddHours(1)); var version = budget.Version;
        budget.Cancel("approval_expired", now.AddHours(2));
        Assert.Equal(BudgetStatus.Expired, budget.Status); Assert.Equal(version, budget.Version);
    }

    [Fact]
    public void Obsolete_notification_is_suppressed_without_claiming_successful_delivery()
    {
        var now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        var budget = new Budget(Guid.NewGuid(), Guid.NewGuid(), new Customer("Ana", new EmailAddress("ana@example.com")),
            [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Serviço", new Money(10), 1)], now, now.AddHours(1));
        var notification = new Notification(Guid.NewGuid(), budget.ServiceOrderId, budget.Id, "proposal", budget.Customer.Email, "budget-proposal-v1", now);
        Assert.True(NotificationPolicy.IsRelevant(notification, budget, null, now));
        budget.Approve(now);
        Assert.False(NotificationPolicy.IsRelevant(notification, budget, null, now));
        var owner = Guid.NewGuid(); notification.Claim(owner, now, TimeSpan.FromMinutes(1)); notification.Suppress(owner, now);
        Assert.Equal(NotificationStatus.Suppressed, notification.Status);
        Assert.DoesNotContain(notification.Events, e => e.Name == "NotificationSent");
        Assert.Throws<DomainException>(() => notification.Claim(Guid.NewGuid(), now.AddMinutes(2), TimeSpan.FromMinutes(1)));
    }
}
