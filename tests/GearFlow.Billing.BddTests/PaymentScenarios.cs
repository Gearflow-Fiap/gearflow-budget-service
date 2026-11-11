using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Notifications;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.BddTests;

public class PaymentScenarios
{
    [Fact]
    public async Task Mock_confirms_immediately_and_refunds_without_webhook_preserving_events_on_retry()
    {
        var store = new Store();
        var gateway = new GearFlow.Billing.Adapters.PaymentGateway.MockPaymentGateway(store, store);
        PaymentWorkflow Workflow() => new(store, store, store, store, store, gateway, store);
        var command = store.Command();
        Assert.True((await Workflow().RequestAsync(command, default)).IsSuccess);
        Assert.Equal(PaymentStatus.Paid, store.PaymentState!.Status);
        Assert.Equal(BudgetStatus.Paid, store.BudgetState.Status);
        Assert.Single(store.Outbox.OfType<PaymentRequested>());
        Assert.Single(store.Outbox.OfType<PaymentConfirmed>());
        Assert.True((await Workflow().RequestAsync(command, default)).IsSuccess);
        Assert.Single(store.Outbox.OfType<PaymentConfirmed>());

        // Recreate the adapter to exercise recovery without process-local mock state.
        gateway = new GearFlow.Billing.Adapters.PaymentGateway.MockPaymentGateway(store, store);
        Assert.Equal(GatewayStatus.Paid, (await gateway.GetAsync(store.PaymentState.ProviderOrderId!, default)).Status);
        var refund = new RefundPaymentCommand(command.ServiceOrderId, command.BudgetId, store.PaymentState.Id, "Sem estoque");
        Assert.True((await Workflow().RefundAsync(refund, default)).IsSuccess);
        Assert.True((await Workflow().RefundAsync(refund, default)).IsSuccess);
        Assert.Equal(PaymentStatus.Refunded, store.PaymentState.Status);
        Assert.Single(store.Outbox.OfType<PaymentRefunded>());
        Assert.Equal(GatewayStatus.Refunded, (await gateway.GetAsync(store.PaymentState.ProviderOrderId!, default)).Status);
    }

    private sealed class Store : IBudgetRepository, IPaymentRepository, INotificationRepository, IOutboxWriter, IUnitOfWork, IClock
    {
        private readonly SemaphoreSlim transaction = new(1, 1);
        public bool InTransaction { get; private set; }
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-11-02T12:00:00Z");
        public BudgetSnapshot BudgetState { get; private set; }
        public PaymentSnapshot? PaymentState { get; private set; }
        public List<IntegrationEvent> Outbox { get; private set; } = [];
        public Dictionary<string, Notification> Notifications { get; private set; } = [];
        public Type? FailOutbox { get; set; }
        public Store()
        {
            var budget = new Budget(Guid.NewGuid(), Guid.NewGuid(), new Customer("Ana", new EmailAddress("ana@example.com")),
                [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Serviço", new Money(350), 1)], UtcNow, UtcNow.AddDays(1));
            budget.Approve(UtcNow); BudgetState = budget.Snapshot();
        }
        public RequestPaymentCommand Command() => new(BudgetState.ServiceOrderId, BudgetState.Id, new Money(350), Guid.NewGuid());
        public Task<Budget?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(id == BudgetState.Id ? Budget.Restore(BudgetState) : null);
        public Task<Budget?> FindByServiceOrderAsync(Guid id, CancellationToken ct) => Task.FromResult(id == BudgetState.ServiceOrderId ? Budget.Restore(BudgetState) : null);
        public Task AddAsync(Budget budget, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(Budget budget, long version, CancellationToken ct)
        {
            Assert.True(InTransaction); if (BudgetState.Version != version) throw new ConcurrencyException();
            BudgetState = budget.Snapshot(); return Task.CompletedTask;
        }
        Task<Payment?> IPaymentRepository.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(PaymentState?.Id == id ? Payment.Restore(PaymentState) : null);
        public Task<Payment?> FindByBudgetAsync(Guid id, CancellationToken ct)
        {
            Assert.True(InTransaction, "Payment repository reads require an active transaction, as Mongo does.");
            return Task.FromResult(PaymentState?.BudgetId == id ? Payment.Restore(PaymentState) : null);
        }
        public Task AddAsync(Payment payment, CancellationToken ct)
        {
            Assert.True(InTransaction); if (PaymentState is not null) throw new DuplicateException();
            PaymentState = payment.Snapshot(); return Task.CompletedTask;
        }
        public Task SaveAsync(Payment payment, long version, CancellationToken ct)
        {
            Assert.True(InTransaction); if (PaymentState!.Version != version) throw new ConcurrencyException();
            PaymentState = payment.Snapshot(); return Task.CompletedTask;
        }
        public Task AddAsync(Notification notification, CancellationToken ct) { Notifications.TryAdd(notification.DeduplicationKey, notification); return Task.CompletedTask; }
        Task<Notification?> INotificationRepository.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Notifications.Values.SingleOrDefault(n => n.Id == id));
        public Task SaveAsync(Notification notification, long version, CancellationToken ct) => throw new NotSupportedException();
        public Task AppendAsync(IntegrationEvent message, CancellationToken ct)
        {
            Assert.True(InTransaction);
            if (message.GetType() == FailOutbox) throw new IOException("Simulated Outbox failure");
            Outbox.Add(message); return Task.CompletedTask;
        }
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            await transaction.WaitAsync(ct); InTransaction = true;
            var budget = BudgetState; var payment = PaymentState; var outbox = Outbox.ToList(); var notifications = new Dictionary<string, Notification>(Notifications);
            try { return await action(ct); }
            catch { BudgetState = budget; PaymentState = payment; Outbox = outbox; Notifications = notifications; throw; }
            finally { InTransaction = false; transaction.Release(); }
        }
    }
    private sealed class Gateway(Store store) : IPaymentGateway
    {
        public GatewayPayment? Remote { get; set; }
        public int Creates { get; private set; }
        public bool LoseCreateResponse { get; set; }
        public bool LoseRefundResponse { get; set; }
        public bool RefundProcessing { get; set; }
        public List<Guid> CreateKeys { get; } = [];
        public List<Guid> RefundKeys { get; } = [];
        private void OutsideTransaction() => Assert.False(store.InTransaction);
        public Task<GatewayPayment> CreateAsync(ChargeRequest request, CancellationToken ct)
        {
            OutsideTransaction(); Assert.NotNull(store.PaymentState); Creates++; CreateKeys.Add(request.OperationId);
            Remote = new(new VerifiedPayment("ORD123", request.BudgetId, request.Amount), GatewayStatus.Pending, "pix", "https://example.com/pix", request.ExpiresOn, null);
            if (LoseCreateResponse) throw new GatewayException("gateway.timeout", true, "Response lost after provider creation.");
            return Task.FromResult(Remote);
        }
        public Task<GatewayPayment> GetAsync(string id, CancellationToken ct) { OutsideTransaction(); return Task.FromResult(Remote!); }
        public Task<GatewayPayment?> FindByBudgetAsync(Guid id, DateTimeOffset createdOn, CancellationToken ct) { OutsideTransaction(); return Task.FromResult(Remote); }
        public Task<GatewayPayment> CancelAsync(string id, Guid operationId, CancellationToken ct)
        {
            OutsideTransaction(); Remote = Remote! with { Status = GatewayStatus.Canceled }; return Task.FromResult(Remote);
        }
        public Task<GatewayPayment> RefundAsync(string id, Money amount, Guid operationId, CancellationToken ct)
        {
            OutsideTransaction(); RefundKeys.Add(operationId);
            Assert.Contains(store.PaymentState!.Attempts, x => x.Id == operationId && x.Operation == PaymentOperation.Refund);
            if (RefundProcessing) return Task.FromResult(Remote! with { Status = GatewayStatus.Unknown });
            Remote = Remote! with { Status = GatewayStatus.Refunded, RefundId = "REF123" };
            if (LoseRefundResponse) throw new GatewayException("gateway.timeout", true, "Response lost after refund.");
            return Task.FromResult(Remote);
        }
    }
    private static PaymentWorkflow Workflow(Store store, Gateway gateway) => new(store, store, store, store, store, gateway, store);

    [Fact]
    public async Task Given_approved_budget_when_requested_twice_then_single_charge_and_requested_event()
    {
        var store = new Store(); var gateway = new Gateway(store); var flow = Workflow(store, gateway); var command = store.Command();
        var first = await flow.RequestAsync(command, default); var second = await flow.RequestAsync(command, default);
        Assert.True(first.IsSuccess); Assert.Equal(first.Value, second.Value); Assert.Equal(1, gateway.Creates);
        Assert.Equal(BudgetStatus.AwaitingPayment, store.BudgetState.Status); Assert.Equal(PaymentStatus.Pending, store.PaymentState!.Status);
        Assert.Single(store.Outbox.OfType<PaymentRequested>()); Assert.Single(store.Notifications);
    }

    [Fact]
    public async Task Given_lost_creation_response_when_retried_then_recover_existing_order_without_new_charge()
    {
        var store = new Store(); var gateway = new Gateway(store) { LoseCreateResponse = true }; var flow = Workflow(store, gateway); var command = store.Command();
        var uncertain = await flow.RequestAsync(command, default);
        Assert.Equal("gateway.timeout", uncertain.Error!.Code); Assert.Equal(PaymentStatus.Created, store.PaymentState!.Status);
        Assert.Equal(AttemptStatus.Uncertain, Assert.Single(store.PaymentState.Attempts).Status);
        Assert.Empty(store.Outbox);
        Assert.True((await flow.RequestAsync(command, default)).IsSuccess);
        Assert.Equal(1, gateway.Creates); Assert.Single(store.Outbox.OfType<PaymentRequested>());
    }

    [Fact]
    public async Task Given_crash_after_remote_creation_when_retried_then_resume_persisted_intent()
    {
        var store = new Store { FailOutbox = typeof(PaymentRequested) }; var gateway = new Gateway(store); var flow = Workflow(store, gateway); var command = store.Command();
        await Assert.ThrowsAsync<IOException>(() => flow.RequestAsync(command, default));
        Assert.Equal(PaymentStatus.Created, store.PaymentState!.Status); Assert.Equal(BudgetStatus.Approved, store.BudgetState.Status);
        var operation = Assert.Single(store.PaymentState.Attempts).Id;
        store.FailOutbox = null;
        Assert.True((await flow.RequestAsync(command, default)).IsSuccess);
        Assert.Equal(operation, Assert.Single(gateway.CreateKeys)); Assert.Equal(1, gateway.Creates);
    }

    [Fact]
    public async Task Given_paid_order_when_webhook_repeats_then_atomically_confirm_both_roots_once()
    {
        var store = new Store(); var gateway = new Gateway(store); var flow = Workflow(store, gateway);
        await flow.RequestAsync(store.Command(), default); gateway.Remote = gateway.Remote! with { Status = GatewayStatus.Paid };
        Assert.True((await flow.RefreshAsync("ORD123", default)).IsSuccess);
        Assert.True((await flow.RefreshAsync("ORD123", default)).IsSuccess);
        Assert.Equal(PaymentStatus.Paid, store.PaymentState!.Status); Assert.Equal(BudgetStatus.Paid, store.BudgetState.Status);
        Assert.Single(store.Outbox.OfType<PaymentConfirmed>());
    }

    [Fact]
    public async Task Given_confirmation_outbox_failure_then_both_aggregates_roll_back_and_retry_confirms()
    {
        var store = new Store(); var gateway = new Gateway(store); var flow = Workflow(store, gateway);
        await flow.RequestAsync(store.Command(), default); gateway.Remote = gateway.Remote! with { Status = GatewayStatus.Paid };
        store.FailOutbox = typeof(PaymentConfirmed);
        await Assert.ThrowsAsync<IOException>(() => flow.RefreshAsync("ORD123", default));
        Assert.Equal(PaymentStatus.Pending, store.PaymentState!.Status); Assert.Equal(BudgetStatus.AwaitingPayment, store.BudgetState.Status);
        store.FailOutbox = null; await flow.RefreshAsync("ORD123", default);
        Assert.Single(store.Outbox.OfType<PaymentConfirmed>());
    }

    [Fact]
    public async Task Given_wrong_provider_evidence_then_no_financial_change_or_event()
    {
        var store = new Store(); var gateway = new Gateway(store); var flow = Workflow(store, gateway);
        await flow.RequestAsync(store.Command(), default);
        gateway.Remote = gateway.Remote! with { Evidence = new VerifiedPayment("ORD123", store.BudgetState.Id, new Money(999)), Status = GatewayStatus.Paid };
        await Assert.ThrowsAsync<DomainException>(() => flow.RefreshAsync("ORD123", default));
        Assert.Equal(PaymentStatus.Pending, store.PaymentState!.Status); Assert.Empty(store.Outbox.OfType<PaymentConfirmed>());
    }

    [Fact]
    public async Task Given_async_creation_without_QR_when_updated_then_save_display_data_once()
    {
        var store = new Store(); var gateway = new Gateway(store); var flow = Workflow(store, gateway);
        // A remote order already found by reference can be accepted before Pix fields exist.
        gateway.Remote = new(new VerifiedPayment("ORD123", store.BudgetState.Id, new Money(350)), GatewayStatus.Pending, null, null, null, null);
        await flow.RequestAsync(store.Command(), default);
        Assert.Null(store.PaymentState!.QrCode); Assert.Empty(store.Notifications);
        gateway.Remote = gateway.Remote with { QrCode = "pix", TicketUrl = "https://example.com/pix" };
        await flow.RefreshAsync("ORD123", default); await flow.RefreshAsync("ORD123", default);
        Assert.Equal("pix", store.PaymentState.QrCode); Assert.Single(store.Notifications);
        Assert.Single(store.Outbox.OfType<PaymentRequested>());
    }

    [Fact]
    public async Task Given_refund_response_lost_when_reconciled_then_one_confirmed_refund_and_no_new_operation()
    {
        var store = new Store(); var gateway = new Gateway(store); var flow = Workflow(store, gateway);
        await flow.RequestAsync(store.Command(), default); gateway.Remote = gateway.Remote! with { Status = GatewayStatus.Paid };
        await flow.RefreshAsync("ORD123", default);
        var command = new RefundPaymentCommand(store.BudgetState.ServiceOrderId, store.BudgetState.Id, store.PaymentState!.Id, "Sem estoque");
        gateway.LoseRefundResponse = true;
        Assert.Equal("gateway.timeout", (await flow.RefundAsync(command, default)).Error!.Code);
        Assert.Equal(PaymentStatus.Paid, store.PaymentState.Status); Assert.Empty(store.Outbox.OfType<PaymentRefunded>());
        Assert.True((await flow.RecoverAsync(store.PaymentState.Id, default)).IsSuccess);
        Assert.Equal(PaymentStatus.Refunded, store.PaymentState.Status); Assert.Equal(BudgetStatus.Refunded, store.BudgetState.Status);
        Assert.Single(store.Outbox.OfType<PaymentRefunded>()); Assert.Single(gateway.RefundKeys);
        gateway.Remote = gateway.Remote! with { Status = GatewayStatus.Paid };
        await flow.RefreshAsync("ORD123", default); // stale provider observation cannot regress refunded state
        Assert.Equal(PaymentStatus.Refunded, store.PaymentState.Status); Assert.Equal(BudgetStatus.Refunded, store.BudgetState.Status);
    }

    [Fact]
    public async Task Given_refund_processing_then_do_not_announce_refunded_and_reuse_operation_on_retry()
    {
        var store = new Store(); var gateway = new Gateway(store); var flow = Workflow(store, gateway);
        await flow.RequestAsync(store.Command(), default); gateway.Remote = gateway.Remote! with { Status = GatewayStatus.Paid };
        await flow.RefreshAsync("ORD123", default); gateway.RefundProcessing = true;
        var command = new RefundPaymentCommand(store.BudgetState.ServiceOrderId, store.BudgetState.Id, store.PaymentState!.Id, "Sem estoque");
        Assert.False((await flow.RefundAsync(command, default)).IsSuccess); Assert.Equal(PaymentStatus.Paid, store.PaymentState.Status);
        Assert.Empty(store.Outbox.OfType<PaymentRefunded>());
        gateway.RefundProcessing = false; await flow.RefundAsync(command, default);
        Assert.Equal(2, gateway.RefundKeys.Count); Assert.Single(gateway.RefundKeys.Distinct()); Assert.Single(store.Outbox.OfType<PaymentRefunded>());
    }

    [Fact]
    public async Task Given_canceled_payment_when_paid_notification_is_late_then_financial_truth_requires_refund()
    {
        var store = new Store(); var gateway = new Gateway(store); var flow = Workflow(store, gateway);
        await flow.RequestAsync(store.Command(), default);
        Assert.True((await flow.CancelAsync(store.BudgetState.ServiceOrderId, store.BudgetState.Id, default)).IsSuccess);
        await store.ExecuteAsync(async ct =>
        {
            var budget = await store.GetAsync(store.BudgetState.Id, ct); var version = budget!.Version;
            budget.Cancel("Cancelamento", store.UtcNow); await store.SaveAsync(budget, version, ct); return true;
        }, default);
        gateway.Remote = gateway.Remote! with { Status = GatewayStatus.Paid };
        await flow.RefreshAsync("ORD123", default);
        Assert.Equal(PaymentStatus.Paid, store.PaymentState!.Status); Assert.Equal(BudgetStatus.Paid, store.BudgetState.Status);
        Assert.Single(store.Outbox.OfType<PaymentConfirmed>());
    }

    [Fact]
    public async Task Given_unknown_provider_state_then_never_publish_payment_failed()
    {
        var store = new Store(); var gateway = new Gateway(store); var flow = Workflow(store, gateway);
        await flow.RequestAsync(store.Command(), default); gateway.Remote = gateway.Remote! with { Status = GatewayStatus.Unknown };
        Assert.False((await flow.RefreshAsync("ORD123", default)).IsSuccess);
        Assert.Equal(PaymentStatus.Pending, store.PaymentState!.Status); Assert.Empty(store.Outbox.OfType<PaymentFailed>());
    }

    [Fact]
    public async Task Given_conflicting_request_amount_then_no_second_charge()
    {
        var store = new Store(); var gateway = new Gateway(store); var flow = Workflow(store, gateway);
        await flow.RequestAsync(store.Command(), default);
        Assert.False((await flow.RequestAsync(store.Command() with { Amount = new Money(999) }, default)).IsSuccess);
        Assert.Equal(1, gateway.Creates);
    }
}
