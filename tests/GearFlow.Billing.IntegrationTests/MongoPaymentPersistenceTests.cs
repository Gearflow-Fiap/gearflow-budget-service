using GearFlow.Billing.Adapters.Persistence.Mongo;
using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using GearFlow.Billing.Adapters.Messaging;

namespace GearFlow.Billing.IntegrationTests;

public sealed class MongoFactAttribute : FactAttribute
{
    public MongoFactAttribute()
    {
        if (!TestInfrastructure.Containers && !TestInfrastructure.NativeMongo && string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("GEARFLOW_TEST_MONGO")))
            Skip = "Requires a disposable Mongo replica set, GEARFLOW_TEST_MONGOD, or Testcontainers.";
    }
}

public class MongoPaymentPersistenceTests
{
    [MongoFact]
    public Task Mock_payment_confirms_and_refunds_with_real_transactions_and_idempotent_events() => WithDatabase(async (database, client) =>
    {
        var clock = new Clock(); var uow = new MongoSession(client); var store = new MongoBillingStore(database, uow, clock);
        var budget = new Budget(Guid.NewGuid(), Guid.NewGuid(), new Customer("Teste mock", new EmailAddress("mock@example.invalid")),
            [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Pagamento simulado", new Money(50), 1)], clock.UtcNow, clock.UtcNow.AddDays(1));
        budget.Approve(clock.UtcNow);
        await uow.ExecuteAsync(async ct => { await store.AddAsync(budget, ct); return true; }, default);
        var gateway = new GearFlow.Billing.Adapters.PaymentGateway.MockPaymentGateway(store, uow);
        GearFlow.Billing.Application.Payments.PaymentWorkflow Workflow() => new(store, store, store, store, uow, gateway, clock);
        var command = new GearFlow.Billing.Application.Payments.RequestPaymentCommand(budget.ServiceOrderId, budget.Id, new Money(50));
        Assert.True((await Workflow().RequestAsync(command, default)).IsSuccess);
        Assert.True((await Workflow().RequestAsync(command, default)).IsSuccess);
        var payment = (await uow.ExecuteAsync(ct => store.FindByBudgetAsync(budget.Id, ct), default))!;
        Assert.Equal(GearFlow.Billing.Domain.Payments.PaymentStatus.Paid, payment.Status);
        Assert.Equal(BudgetStatus.Paid, (await uow.ExecuteAsync(ct => store.GetAsync(budget.Id, ct), default))!.Status);
        gateway = new GearFlow.Billing.Adapters.PaymentGateway.MockPaymentGateway(store, uow);
        Assert.Equal(GatewayStatus.Paid, (await gateway.GetAsync(payment.ProviderOrderId!, default)).Status);
        var refund = new GearFlow.Billing.Application.Payments.RefundPaymentCommand(budget.ServiceOrderId, budget.Id, payment.Id, "Teste simulado");
        Assert.True((await Workflow().RefundAsync(refund, default)).IsSuccess);
        Assert.True((await Workflow().RefundAsync(refund, default)).IsSuccess);
        Assert.Equal(GatewayStatus.Refunded, (await gateway.GetAsync(payment.ProviderOrderId!, default)).Status);
        var events = await database.GetCollection<BsonDocument>("outbox").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.Single(events, e => e["type"] == nameof(PaymentRequested));
        Assert.Single(events, e => e["type"] == nameof(PaymentConfirmed));
        Assert.Single(events, e => e["type"] == nameof(PaymentRefunded));
    });

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-11-02T12:00:00Z"); }
    private static async Task WithDatabase(Func<IMongoDatabase, MongoClient, Task> test)
    {
        await using var infrastructure = await TestInfrastructure.MongoAsync();
        var client = new MongoClient(infrastructure.Value);
        var name = "gearflow_test_" + Guid.NewGuid().ToString("N"); var database = client.GetDatabase(name);
        try { await new MongoInitializer(database).StartAsync(default); await test(database, client); }
        finally { await client.DropDatabaseAsync(name); } // This name is created locally above, never a configured application database.
    }

    [MongoFact]
    public Task Inbox_receipt_rolls_back_with_business_state_and_only_final_commit_completes() => WithDatabase(async (database, client) =>
    {
        var clock = new Clock(); var inbox = new MongoInbox(database, clock); var context = new InboxContext(database, clock);
        var id = Guid.NewGuid(); var claim = await inbox.ClaimAsync("test-consumer", id, "digest", IncomingCommandKind.GenerateBudget, default);
        context.Activate(claim.Lease); var uow = new MongoSession(client, context); var store = new MongoBillingStore(database, uow, clock);
        var budget = new Budget(Guid.NewGuid(), Guid.NewGuid(), new Customer("Ana", new EmailAddress("ana@example.com")),
            [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Serviço", new Money(350), 1)], clock.UtcNow, clock.UtcNow.AddDays(1));
        await Assert.ThrowsAsync<IOException>(() => uow.ExecuteAsync<bool>(async ct =>
        {
            await store.AddAsync(budget, ct); await context.CompleteAsync(ct); throw new IOException("Abort final commit");
        }, default));
        Assert.False(await inbox.IsCompletedAsync(claim.Lease, default));
        Assert.Equal(0, await database.GetCollection<BsonDocument>("budgets").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await uow.ExecuteAsync(async ct => { await store.AddAsync(budget, ct); await context.CompleteAsync(ct); return true; }, default);
        Assert.True(await inbox.IsCompletedAsync(claim.Lease, default));
        Assert.Equal(InboxClaimStatus.Completed, (await inbox.ClaimAsync("test-consumer", id, "digest", IncomingCommandKind.GenerateBudget, default)).Status);
        Assert.Equal(InboxClaimStatus.Conflict, (await inbox.ClaimAsync("test-consumer", id, "changed", IncomingCommandKind.GenerateBudget, default)).Status);
    });

    [MongoFact]
    public Task Compensation_fence_survives_missing_budget_and_blocks_late_creation() => WithDatabase(async (database, client) =>
    {
        var clock = new Clock(); var uow = new MongoSession(client); var fences = new MongoCommercialFences(database, uow, clock); var order = Guid.NewGuid();
        await uow.ExecuteAsync(async ct =>
        {
            var fence = await fences.GetOrCreateAsync(order, ct); var version = fence.Version;
            fence.RequestCancellation("Saga compensation before generation"); await fences.SaveAsync(fence, version, ct); return true;
        }, default);
        await Assert.ThrowsAsync<DomainException>(() => uow.ExecuteAsync(async ct =>
        { var fence = await fences.GetOrCreateAsync(order, ct); fence.AuthorizeCommercialAction(); return true; }, default));
        Assert.Equal(0, await database.GetCollection<BsonDocument>("budgets").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    });

    [MongoFact]
    public Task Real_transactions_roll_back_budget_and_outbox_and_reject_stale_versions() => WithDatabase(async (database, client) =>
    {
        var clock = new Clock(); var uow = new MongoSession(client); var store = new MongoBillingStore(database, uow, clock);
        var budget = new Budget(Guid.NewGuid(), Guid.NewGuid(), new Customer("Ana", new EmailAddress("ana@example.com")),
            [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Serviço", new Money(350), 1)], clock.UtcNow, clock.UtcNow.AddDays(1));
        await Assert.ThrowsAsync<IOException>(() => uow.ExecuteAsync<bool>(async ct =>
        {
            await store.AddAsync(budget, ct);
            await store.AppendAsync(new BudgetGenerated(Guid.NewGuid(), budget.ServiceOrderId, null, clock.UtcNow,
                budget.Id, 350, "BRL", budget.ExpiresOn), ct);
            throw new IOException("Rollback");
        }, default));
        Assert.Equal(0, await database.GetCollection<BsonDocument>("budgets").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0, await database.GetCollection<BsonDocument>("outbox").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await uow.ExecuteAsync(async ct => { await store.AddAsync(budget, ct); return true; }, default);
        var stale = await uow.ExecuteAsync(ct => store.GetAsync(budget.Id, ct), default);
        await uow.ExecuteAsync(async ct =>
        {
            var fresh = (await store.GetAsync(budget.Id, ct))!; var version = fresh.Version; fresh.Approve(clock.UtcNow);
            await store.SaveAsync(fresh, version, ct); return true;
        }, default);
        var oldVersion = stale!.Version; stale.Reject(clock.UtcNow);
        await Assert.ThrowsAsync<ConcurrencyException>(() => uow.ExecuteAsync(async ct =>
            { await store.SaveAsync(stale, oldVersion, ct); return true; }, default));
        var view = await ((IBudgetReadModel)store).GetAsync(budget.Id, default);
        Assert.Equal("Approved", view!.Status); // Projection must work without an active transaction.
    });

    [MongoFact]
    public Task Real_queue_deduplicates_across_replicas_and_fences_expired_owner() => WithDatabase(async (database, _) =>
    {
        var clock = new Clock(); var queue = new MongoPaymentWorkStore(database); var replica = new MongoPaymentWorkStore(database);
        await Task.WhenAll(queue.EnqueueAsync("signed-message", "ORD123", null, clock.UtcNow, default),
            replica.EnqueueAsync("signed-message", "ORD123", null, clock.UtcNow, default));
        Assert.Equal(1, await database.GetCollection<BsonDocument>("payment_work").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var claims = await Task.WhenAll(queue.ClaimAsync(Guid.NewGuid(), clock.UtcNow, TimeSpan.FromSeconds(1), default),
            replica.ClaimAsync(Guid.NewGuid(), clock.UtcNow, TimeSpan.FromSeconds(1), default));
        var old = Assert.Single(claims, x => x is not null)!;
        var next = (await replica.ClaimAsync(Guid.NewGuid(), clock.UtcNow.AddSeconds(2), TimeSpan.FromMinutes(1), default))!;
        Assert.Equal(2, next.Attempts);
        await queue.CompleteAsync(old, clock.UtcNow.AddSeconds(2), default);
        var document = await database.GetCollection<BsonDocument>("payment_work").Find(new BsonDocument("_id", "signed-message")).SingleAsync();
        Assert.True(document["completedOn"].IsBsonNull);
        await replica.CompleteAsync(next, clock.UtcNow.AddSeconds(2), default);
        Assert.Null(await queue.ClaimAsync(Guid.NewGuid(), clock.UtcNow.AddSeconds(2), TimeSpan.FromMinutes(1), default));
    });
}
