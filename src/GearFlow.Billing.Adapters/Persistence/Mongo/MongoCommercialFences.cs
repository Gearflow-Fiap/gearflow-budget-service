using System.Text.Json;
using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GearFlow.Billing.Adapters.Persistence.Mongo;

public sealed class MongoCommercialFences(IMongoDatabase database, MongoSession session, IClock clock) : ICommercialFenceRepository
{
    private readonly IMongoCollection<BsonDocument> collection = database.GetCollection<BsonDocument>("commercial_fences");
    public async Task<CommercialFence> GetOrCreateAsync(Guid serviceOrderId, CancellationToken ct)
    {
        var document = await (await collection.FindAsync(session.Current, new BsonDocument("_id", serviceOrderId.ToString("D")), cancellationToken: ct)).FirstOrDefaultAsync(ct);
        return document is null ? new CommercialFence(serviceOrderId, clock.UtcNow)
            : CommercialFence.Restore(JsonSerializer.Deserialize<CommercialFenceSnapshot>(document["snapshot"].AsString, MongoBillingStore.Json)!);
    }
    public async Task SaveAsync(CommercialFence fence, long expectedVersion, CancellationToken ct)
    {
        try
        {
            var result = await collection.ReplaceOneAsync(session.Current, new BsonDocument { ["_id"] = fence.Id.ToString("D"), ["version"] = expectedVersion },
                new BsonDocument { ["_id"] = fence.Id.ToString("D"), ["version"] = fence.Version,
                    ["snapshot"] = JsonSerializer.Serialize(fence.Snapshot(), MongoBillingStore.Json) }, new ReplaceOptions { IsUpsert = expectedVersion == 0 }, ct);
            if (result.MatchedCount == 0 && result.UpsertedId is null) throw new ConcurrencyException();
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { throw new ConcurrencyException(); }
    }
}
