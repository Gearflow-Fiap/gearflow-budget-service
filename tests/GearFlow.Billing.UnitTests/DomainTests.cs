using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Notifications;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.UnitTests;

public class DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static Budget NewBudget() => new(Guid.NewGuid(), Guid.NewGuid(),
        new Customer("Cliente", new EmailAddress("client@EXAMPLE.COM")),
        [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Diagnóstico", new Money(10.005m), 1.5m)], Now, Now.AddHours(1));
    private static (Budget Budget, Payment Payment, Guid Operation, VerifiedPayment Evidence) PendingPayment()
    {
        var budget = NewBudget();
        budget.Approve(Now);
        var payment = Payment.Request(Guid.NewGuid(), budget, budget.ServiceOrderId, budget.Total, Now);
        var operation = Guid.NewGuid();
        payment.BeginOperation(operation, PaymentOperation.Create, Now);
        var evidence = new VerifiedPayment("order-1", budget.Id, budget.Total);
        payment.RegisterCharge(operation, evidence, "pix", "https://example.com/ticket", Now.AddMinutes(30), Now);
        return (budget, payment, operation, evidence);
    }

    [Theory]
    [InlineData("1.005", "1.01")]
    [InlineData("1.004", "1.00")]
    [InlineData("0.005", "0.01")]
    public void Money_rounds_away_from_zero(string input, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            new Money(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)).Amount);

    [Fact]
    public void Money_has_value_equality_and_rejects_negative_or_other_currency()
    {
        Assert.Equal(new Money(10), new Money(10.001m));
        Assert.Throws<DomainException>(() => new Money(-0.001m));
        Assert.Throws<DomainException>(() => new Money(10, "USD"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.0001)]
    public void Item_rejects_invalid_quantity(decimal quantity) => Assert.Throws<DomainException>(() =>
        new BudgetItem(Guid.NewGuid(), BudgetItemType.Part, "Peça", new Money(1), quantity));

    [Fact]
    public void Budget_snapshots_items_and_rounds_each_line()
    {
        var item = new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Serviço", new Money(10.005m), 1.5m);
        var items = new List<BudgetItem> { item, item };
        var budget = new Budget(Guid.NewGuid(), Guid.NewGuid(), new Customer("A", new EmailAddress("a@example.com")), items, Now, Now.AddHours(1));
        items.Clear();
        Assert.Equal(2, budget.Items.Count);
        Assert.Equal(30.04m, budget.Total.Amount);
        Assert.Throws<NotSupportedException>(() => ((IList<BudgetItem>)budget.Items).Clear());
    }

    [Fact]
    public void Budget_rejects_zero_total_and_non_utc_dates()
    {
        var budget = NewBudget();
        Assert.Throws<DomainException>(() => new Budget(Guid.NewGuid(), Guid.NewGuid(), budget.Customer,
            [new BudgetItem(Guid.NewGuid(), BudgetItemType.Part, "Free", new Money(0), 1)], Now, Now.AddHours(1)));
        Assert.Throws<DomainException>(() => new Budget(Guid.NewGuid(), Guid.NewGuid(), budget.Customer,
            budget.Items, Now.ToOffset(TimeSpan.FromHours(-3)), Now.AddHours(1)));
    }

    [Fact]
    public void Approval_is_idempotent_and_conflicting_review_is_rejected()
    {
        var budget = NewBudget();
        budget.Approve(Now);
        var version = budget.Version;
        budget.Approve(Now.AddMinutes(1));
        Assert.Equal(version, budget.Version);
        Assert.Throws<DomainException>(() => budget.Reject(Now));
        Assert.Single(budget.Events, e => e.Name == "BudgetApproved");
    }

    [Fact]
    public void Expiry_boundary_prevents_approval_and_expiration_is_idempotent()
    {
        var budget = NewBudget();
        Assert.Throws<DomainException>(() => budget.Approve(budget.ExpiresOn));
        Assert.Throws<DomainException>(() => budget.Expire(budget.ExpiresOn.AddTicks(-1)));
        budget.Expire(budget.ExpiresOn);
        budget.Expire(budget.ExpiresOn);
        Assert.Equal(BudgetStatus.Expired, budget.Status);
        Assert.Single(budget.Events, e => e.Name == "BudgetExpired");
    }

    [Fact]
    public void Rejection_detects_divergent_repeated_payload()
    {
        var budget = NewBudget();
        budget.Reject(Now, "Preço");
        budget.Reject(Now, "Preço");
        Assert.Throws<DomainException>(() => budget.Reject(Now, "Prazo"));
    }

    [Theory]
    [InlineData(BudgetStatus.Pending)]
    [InlineData(BudgetStatus.Approved)]
    [InlineData(BudgetStatus.Rejected)]
    [InlineData(BudgetStatus.Expired)]
    public void Unpaid_budget_can_be_canceled(BudgetStatus initial)
    {
        var budget = NewBudget();
        if (initial == BudgetStatus.Approved) budget.Approve(Now);
        if (initial == BudgetStatus.Rejected) budget.Reject(Now);
        if (initial == BudgetStatus.Expired) budget.Expire(budget.ExpiresOn);
        budget.Cancel("Compensação", Now.AddHours(2));
        budget.Cancel("Compensação", Now.AddHours(2));
        Assert.Equal(BudgetStatus.Canceled, budget.Status);
        Assert.Single(budget.Events, e => e.Name == "BudgetCanceled");
    }

    [Fact]
    public void Payment_requires_approved_budget_matching_order_and_amount()
    {
        var budget = NewBudget();
        Assert.Throws<DomainException>(() => Payment.Request(Guid.NewGuid(), budget, budget.ServiceOrderId, budget.Total, Now));
        budget.Approve(Now);
        Assert.Throws<DomainException>(() => Payment.Request(Guid.NewGuid(), budget, Guid.NewGuid(), budget.Total, Now));
        Assert.Throws<DomainException>(() => Payment.Request(Guid.NewGuid(), budget, budget.ServiceOrderId, new Money(99), Now));
    }

    [Fact]
    public void Timeout_preserves_uncertain_operation_and_stable_identity()
    {
        var budget = NewBudget(); budget.Approve(Now);
        var payment = Payment.Request(Guid.NewGuid(), budget, budget.ServiceOrderId, budget.Total, Now);
        var operation = Guid.NewGuid();
        var intent = payment.BeginOperation(operation, PaymentOperation.Create, Now);
        payment.MarkUncertain(operation, Now.AddSeconds(10));
        Assert.Same(intent, payment.BeginOperation(operation, PaymentOperation.Create, Now.AddSeconds(11)));
        Assert.Throws<DomainException>(() => payment.BeginOperation(Guid.NewGuid(), PaymentOperation.Create, Now));
        Assert.Equal(PaymentStatus.Created, payment.Status);
        Assert.Empty(payment.Events);
        Assert.DoesNotContain(payment.Events, e => e.Name == "PaymentFailed");
        payment.RegisterCharge(operation, new VerifiedPayment("order", budget.Id, budget.Total), "pix",
            "https://example.com/ticket", Now.AddMinutes(30), Now.AddSeconds(12));
        Assert.Equal(AttemptStatus.Succeeded, intent.Status);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public void Charge_requires_persisted_intent_and_valid_evidence()
    {
        var (budget, payment, _, _) = PendingPayment();
        Assert.Throws<DomainException>(() => payment.Confirm(new VerifiedPayment("wrong", budget.Id, budget.Total), Now));
        Assert.Throws<DomainException>(() => payment.Confirm(new VerifiedPayment("order-1", Guid.NewGuid(), budget.Total), Now));
        Assert.Throws<DomainException>(() => payment.Confirm(new VerifiedPayment("order-1", budget.Id, new Money(999)), Now));
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public void Confirm_and_refund_are_idempotent_and_late_failure_does_not_regress()
    {
        var (budget, payment, _, evidence) = PendingPayment();
        budget.AwaitPayment(Now);
        payment.Confirm(evidence, Now); budget.MarkPaid(Now);
        payment.Confirm(evidence, Now);
        payment.Fail(evidence, "late rejection", Now);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Throws<DomainException>(() => budget.Cancel("cancel", Now));
        var refund = Guid.NewGuid();
        payment.BeginOperation(refund, PaymentOperation.Refund, Now);
        payment.MarkUncertain(refund, Now);
        payment.ConfirmRefund(refund, evidence, "refund-1", Now); budget.MarkRefunded(Now);
        payment.ConfirmRefund(refund, evidence, "refund-1", Now);
        payment.Confirm(evidence, Now);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(BudgetStatus.Refunded, budget.Status);
        Assert.Single(payment.Events, e => e.Name == "PaymentConfirmed");
        Assert.Single(payment.Events, e => e.Name == "PaymentRefunded");
        Assert.Throws<DomainException>(() => payment.ConfirmRefund(refund, evidence, "other-refund", Now));
        budget.Cancel("Compensação após reembolso", Now);
        Assert.Equal(BudgetStatus.Canceled, budget.Status);
    }

    [Fact]
    public void Payment_winning_cancellation_race_requires_refund()
    {
        var (_, payment, _, evidence) = PendingPayment();
        var cancel = Guid.NewGuid();
        payment.BeginOperation(cancel, PaymentOperation.Cancel, Now);
        payment.Confirm(evidence, Now);
        Assert.Throws<DomainException>(() => payment.ConfirmCancellation(cancel, evidence, Now));
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.DoesNotContain(payment.Events, e => e.Name == "PaymentCanceled");
        payment.BeginOperation(Guid.NewGuid(), PaymentOperation.Refund, Now);
    }

    [Fact]
    public void Late_confirmation_after_cancellation_stays_visible()
    {
        var (_, payment, _, evidence) = PendingPayment();
        var cancel = Guid.NewGuid();
        payment.BeginOperation(cancel, PaymentOperation.Cancel, Now);
        payment.ConfirmCancellation(cancel, evidence, Now);
        payment.Confirm(evidence, Now.AddSeconds(1));
        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }

    [Fact]
    public void Invalid_confirmation_time_does_not_partially_change_payment()
    {
        var (_, payment, _, evidence) = PendingPayment();
        payment.BeginOperation(Guid.NewGuid(), PaymentOperation.Cancel, Now.AddSeconds(10));
        Assert.Throws<DomainException>(() => payment.Confirm(evidence, Now));
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.DoesNotContain(payment.Events, e => e.Name == "PaymentConfirmed");
    }

    [Fact]
    public void Notification_lease_recovery_fences_stale_owner_and_persists_retry_count()
    {
        var notification = new Notification(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "unique", new EmailAddress("a@example.com"), "proposal", Now);
        var owner = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        notification.Claim(owner, Now, TimeSpan.FromMinutes(1));
        Assert.Throws<DomainException>(() => notification.Claim(replacement, Now, TimeSpan.FromMinutes(1)));
        notification.Claim(replacement, Now.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.Throws<DomainException>(() => notification.MarkSent(owner, Now.AddMinutes(1)));
        notification.Retry(replacement, Now.AddMinutes(1), TimeSpan.FromMinutes(1), 3);
        Assert.Equal(2, notification.Attempts);
        Assert.Throws<DomainException>(() => notification.Claim(owner, Now.AddMinutes(1), TimeSpan.FromMinutes(1)));
        notification.Claim(owner, Now.AddMinutes(2), TimeSpan.FromMinutes(1));
        notification.MarkSent(owner, Now.AddMinutes(2));
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.Throws<DomainException>(() => notification.Claim(owner, Now.AddMinutes(3), TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData("not-email")]
    [InlineData("Name <a@example.com>")]
    [InlineData("")]
    public void Email_rejects_non_addresses(string input) => Assert.Throws<DomainException>(() => new EmailAddress(input));

    [Fact]
    public void Email_normalization_preserves_local_part_case()
    {
        Assert.Equal(new EmailAddress("A@EXAMPLE.COM"), new EmailAddress("A@example.com"));
        Assert.NotEqual(new EmailAddress("A@example.com"), new EmailAddress("a@example.com"));
    }
}

