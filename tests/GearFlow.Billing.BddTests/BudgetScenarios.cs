using GearFlow.Billing.Application.Budgets;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Notifications;

namespace GearFlow.Billing.BddTests;

// Executable Given/When/Then scenarios through Application ports.
// Reqnroll and real database scenarios remain separate items in the plan.
public class BudgetScenarios
{
    internal sealed class Store : IBudgetRepository, INotificationRepository, IOutboxWriter, IUnitOfWork, IClock, IApprovalTokenService
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-11-02T12:00:00Z");
        public Dictionary<Guid, Budget> Budgets { get; private set; } = [];
        public List<IntegrationEvent> Outbox { get; private set; } = [];
        public List<Notification> Notifications { get; private set; } = [];
        public bool FailOutbox { get; set; }
        public Task<Budget?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Budgets.GetValueOrDefault(id));
        public Task<Budget?> FindByServiceOrderAsync(Guid id, CancellationToken ct) => Task.FromResult(Budgets.Values.SingleOrDefault(b => b.ServiceOrderId == id));
        public Task AddAsync(Budget budget, CancellationToken ct) { Budgets.Add(budget.Id, budget); return Task.CompletedTask; }
        public Task SaveAsync(Budget budget, long expectedVersion, CancellationToken ct) { Budgets[budget.Id] = budget; return Task.CompletedTask; }
        public Task AddAsync(Notification notification, CancellationToken ct) { Notifications.Add(notification); return Task.CompletedTask; }
        Task<Notification?> INotificationRepository.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Notifications.SingleOrDefault(n => n.Id == id));
        public Task SaveAsync(Notification notification, long expectedVersion, CancellationToken ct) => Task.CompletedTask;
        public Task AppendAsync(IntegrationEvent message, CancellationToken ct)
        {
            if (FailOutbox) throw new IOException("Simulated persistence failure");
            Outbox.Add(message); return Task.CompletedTask;
        }
        public string Create(Guid budgetId, DateTimeOffset expiresOn) => "valid-test-token";
        public bool IsValid(string token, Guid budgetId, DateTimeOffset expiresOn) => token == "valid-test-token";
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            // Snapshot fake proves handler transaction composition, not Mongo transaction semantics.
            var budgets = Budgets.ToDictionary(kv => kv.Key, kv => Copy(kv.Value));
            var outbox = Outbox.ToList(); var notifications = Notifications.ToList();
            try { return await action(ct); }
            catch { Budgets = budgets; Outbox = outbox; Notifications = notifications; throw; }
        }
        private static Budget Copy(Budget source)
        {
            var copy = new Budget(source.Id, source.ServiceOrderId, source.Customer, source.Items, source.CreatedOn, source.ExpiresOn);
            if (source.Status == BudgetStatus.Approved) copy.Approve(source.ReviewedOn!.Value);
            if (source.Status == BudgetStatus.Rejected) copy.Reject(source.ReviewedOn!.Value, source.Reason);
            copy.ClearEvents(); return copy;
        }
    }
    internal static GenerateBudgetCommand Proposal(Store store) => new(Guid.NewGuid(),
        new Customer("Ana", new EmailAddress("ana@example.com")),
        [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Diagnóstico", new GearFlow.Billing.Domain.Core.Money(350), 1)],
        store.UtcNow.AddDays(1), Guid.NewGuid());
    internal static GenerateBudgetHandler Generate(Store store) => new(store, store, store, store, store);
    internal static ReviewBudgetHandler Review(Store store) => new(store, store, store, store, store);

    [Fact]
    public async Task Given_diagnostic_when_generated_twice_then_one_budget_event_and_notification()
    {
        var store = new Store(); var proposal = Proposal(store);
        var first = await Generate(store).HandleAsync(proposal, default);
        var repeated = await Generate(store).HandleAsync(proposal, default);
        Assert.True(first.IsSuccess); Assert.Equal(first.Value, repeated.Value);
        Assert.Single(store.Budgets); Assert.Single(store.Outbox); Assert.Single(store.Notifications);
        var fact = Assert.IsType<BudgetGenerated>(store.Outbox[0]);
        Assert.Equal(proposal.ServiceOrderId, fact.ServiceOrderId);
        Assert.Equal(proposal.CausationId, fact.CausationId);
        Assert.Equal(350, fact.TotalAmount);
    }

    [Fact]
    public async Task Given_same_order_with_changed_snapshot_then_conflict_and_no_new_event()
    {
        var store = new Store(); var proposal = Proposal(store);
        await Generate(store).HandleAsync(proposal, default);
        var result = await Generate(store).HandleAsync(proposal with { ExpiresOn = proposal.ExpiresOn.AddDays(1) }, default);
        Assert.Equal("budget.conflict", result.Error!.Code);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task Given_expired_proposal_when_generated_then_failure_event_without_budget_or_email()
    {
        var store = new Store();
        var result = await Generate(store).HandleAsync(Proposal(store) with { ExpiresOn = store.UtcNow }, default);
        Assert.False(result.IsSuccess); Assert.Empty(store.Budgets); Assert.Empty(store.Notifications);
        Assert.IsType<BudgetGenerationFailed>(Assert.Single(store.Outbox));
    }

    [Fact]
    public async Task Given_pending_budget_when_approved_twice_then_one_decision_and_conflicting_rejection_fails()
    {
        var store = new Store(); var id = (await Generate(store).HandleAsync(Proposal(store), default)).Value;
        var handler = Review(store);
        Assert.True((await handler.HandleAsync(new ApproveBudgetCommand(id, "valid-test-token"), default)).IsSuccess);
        Assert.True((await handler.HandleAsync(new ApproveBudgetCommand(id, "valid-test-token"), default)).IsSuccess);
        Assert.False((await handler.HandleAsync(new RejectBudgetCommand(id, "valid-test-token"), default)).IsSuccess);
        Assert.Single(store.Outbox.OfType<BudgetApproved>());
        Assert.Equal(BudgetStatus.Approved, store.Budgets[id].Status);
    }

    [Fact]
    public async Task Given_tampered_token_when_reviewed_then_state_and_events_unchanged()
    {
        var store = new Store(); var id = (await Generate(store).HandleAsync(Proposal(store), default)).Value;
        var result = await Review(store).HandleAsync(new ApproveBudgetCommand(id, "tampered"), default);
        Assert.Equal("approval.forbidden", result.Error!.Code);
        Assert.Equal(BudgetStatus.Pending, store.Budgets[id].Status);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task Given_outbox_failure_when_generating_then_transaction_rolls_back_and_retry_succeeds()
    {
        var store = new Store { FailOutbox = true }; var proposal = Proposal(store);
        await Assert.ThrowsAsync<IOException>(() => Generate(store).HandleAsync(proposal, default));
        Assert.Empty(store.Budgets); Assert.Empty(store.Outbox); Assert.Empty(store.Notifications);
        store.FailOutbox = false;
        Assert.True((await Generate(store).HandleAsync(proposal, default)).IsSuccess);
        Assert.Single(store.Budgets); Assert.Single(store.Outbox); Assert.Single(store.Notifications);
    }

    [Fact]
    public async Task Given_outbox_failure_when_approving_then_decision_rolls_back()
    {
        var store = new Store(); var id = (await Generate(store).HandleAsync(Proposal(store), default)).Value;
        store.FailOutbox = true;
        await Assert.ThrowsAsync<IOException>(() => Review(store).HandleAsync(new ApproveBudgetCommand(id, "valid-test-token"), default));
        Assert.Equal(BudgetStatus.Pending, store.Budgets[id].Status);
        Assert.Single(store.Outbox);
    }
}
