using System.Text.Json;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.UnitTests;

public class SnapshotTests
{
    [Fact]
    public void Snapshots_roundtrip_with_decimal_precision_private_state_and_no_new_domain_events()
    {
        var now = DateTimeOffset.Parse("2026-11-02T12:00:00Z"); var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var budget = new Budget(Guid.NewGuid(), Guid.NewGuid(), new Customer("Ana", new EmailAddress("ana@example.com")),
            [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Serviço", new Money(100.01m), 1.123m)], now, now.AddDays(1));
        budget.Approve(now);
        var payment = Payment.Request(Guid.NewGuid(), budget, budget.ServiceOrderId, budget.Total, now);
        var intent = payment.BeginOperation(Guid.NewGuid(), PaymentOperation.Create, now);
        payment.MarkUncertain(intent.Id, now);
        var restoredBudget = Budget.Restore(JsonSerializer.Deserialize<BudgetSnapshot>(JsonSerializer.Serialize(budget.Snapshot(), json), json)!);
        var restored = Payment.Restore(JsonSerializer.Deserialize<PaymentSnapshot>(JsonSerializer.Serialize(payment.Snapshot(), json), json)!);
        Assert.Equal(budget.Total, restoredBudget.Total); Assert.Equal(budget.Version, restoredBudget.Version); Assert.Empty(restoredBudget.Events);
        Assert.Equal(AttemptStatus.Uncertain, Assert.Single(restored.Attempts).Status); Assert.Equal(payment.Version, restored.Version); Assert.Empty(restored.Events);
        restored.RegisterOrder(intent.Id, new VerifiedPayment("ORD123", budget.Id, budget.Total), null, null, now.AddMinutes(30), now);
        var pending = Payment.Restore(JsonSerializer.Deserialize<PaymentSnapshot>(JsonSerializer.Serialize(restored.Snapshot(), json), json)!);
        Assert.Equal(PaymentStatus.Pending, pending.Status); Assert.Empty(pending.Events);
        Assert.Equal(AttemptStatus.Succeeded, Assert.Single(pending.Attempts).Status);
    }
}
