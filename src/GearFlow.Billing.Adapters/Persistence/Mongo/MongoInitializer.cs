using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GearFlow.Billing.Adapters.Persistence.Mongo;

public sealed class MongoInitializer(IMongoDatabase database) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var hello = await database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: ct);
        if (!hello.GetValue("isWritablePrimary", false).ToBoolean() || !hello.Contains("setName"))
            throw new InvalidOperationException("Payment persistence requires a writable Mongo replica set primary.");
        foreach (var name in new[] { "budgets", "payments", "notifications", "outbox", "payment_work", "inbox", "replay_audit", "commercial_fences" })
        {
            try { await database.CreateCollectionAsync(name, cancellationToken: ct); }
            catch (MongoCommandException exception) when (exception.Code == 48) { }
        }
        await database.GetCollection<BsonDocument>("budgets").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("serviceOrderId", 1), new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        await database.GetCollection<BsonDocument>("payments").Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(new BsonDocument("budgetId", 1), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<BsonDocument>(new BsonDocument("providerOrderId", 1), new CreateIndexOptions<BsonDocument>
                { Unique = true, PartialFilterExpression = new BsonDocument("providerOrderId", new BsonDocument("$type", "string")) }),
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["status"] = 1, ["lastReconciledOn"] = 1 })
        ], ct);
        await database.GetCollection<BsonDocument>("notifications").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("deduplicationKey", 1), new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        await database.GetCollection<BsonDocument>("inbox").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { ["consumerName"] = 1, ["messageId"] = 1 }, new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        await database.GetCollection<BsonDocument>("outbox").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { ["sentOn"] = 1, ["availableOn"] = 1, ["leaseUntil"] = 1 }), cancellationToken: ct);
        await database.GetCollection<BsonDocument>("notifications").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { ["status"] = 1, ["availableOn"] = 1, ["leaseUntil"] = 1 }), cancellationToken: ct);
        await database.GetCollection<BsonDocument>("payment_work").Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["completedOn"] = 1, ["availableOn"] = 1, ["leaseUntil"] = 1 }),
            // TTL applies only after completion. Unfinished work cannot disappear after a long outage.
            new CreateIndexModel<BsonDocument>(new BsonDocument("completedOn", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(30) })
        ], ct);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
