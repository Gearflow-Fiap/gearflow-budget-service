using GearFlow.Billing.Application.Ports.Out;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GearFlow.Billing.Adapters.Messaging;

public enum InboxClaimStatus { Claimed, Completed, Busy, Conflict }
public sealed record InboxClaim(InboxClaimStatus Status, InboxLease Lease);

public sealed class MongoInbox(IMongoDatabase database, IClock clock)
{
    private readonly IMongoCollection<BsonDocument> collection = database.GetCollection<BsonDocument>("inbox").WithWriteConcern(WriteConcern.WMajority);
    public async Task<InboxClaim> ClaimAsync(string consumer, Guid messageId, string digest, IncomingCommandKind kind, CancellationToken ct)
    {
        var lease = new InboxLease($"{consumer}:{messageId:D}", digest, Guid.NewGuid(), kind);
        var now = clock.UtcNow;
        var document = new BsonDocument { ["_id"] = lease.Key, ["consumerName"] = consumer, ["messageId"] = messageId.ToString("D"),
            ["digest"] = digest, ["owner"] = lease.Owner.ToString("D"), ["leaseUntil"] = now.AddMinutes(5).UtcDateTime,
            ["completedOn"] = BsonNull.Value, ["receivedOn"] = now.UtcDateTime };
        try
        {
            await collection.InsertOneAsync(document, cancellationToken: ct);
            return new(InboxClaimStatus.Claimed, lease);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        var existing = await collection.Find(new BsonDocument("_id", lease.Key)).SingleAsync(ct);
        if (existing["digest"].AsString != digest) return new(InboxClaimStatus.Conflict, lease);
        if (!existing["completedOn"].IsBsonNull) return new(InboxClaimStatus.Completed, lease);
        var result = await collection.UpdateOneAsync(new BsonDocument { ["_id"] = lease.Key, ["digest"] = digest,
            ["completedOn"] = BsonNull.Value, ["leaseUntil"] = new BsonDocument("$lte", now.UtcDateTime) },
            new BsonDocument("$set", new BsonDocument { ["owner"] = lease.Owner.ToString("D"), ["leaseUntil"] = now.AddMinutes(5).UtcDateTime }), cancellationToken: ct);
        return new(result.ModifiedCount == 1 ? InboxClaimStatus.Claimed : InboxClaimStatus.Busy, lease);
    }
    public async Task<bool> IsCompletedAsync(InboxLease lease, CancellationToken ct) =>
        await collection.CountDocumentsAsync(new BsonDocument { ["_id"] = lease.Key, ["digest"] = lease.Digest,
            ["completedOn"] = new BsonDocument("$ne", BsonNull.Value) }, cancellationToken: ct) == 1;
    public Task ReleaseAsync(InboxLease lease, CancellationToken ct) => collection.UpdateOneAsync(
        new BsonDocument { ["_id"] = lease.Key, ["owner"] = lease.Owner.ToString("D"), ["completedOn"] = BsonNull.Value },
        new BsonDocument("$set", new BsonDocument("leaseUntil", clock.UtcNow.UtcDateTime)), cancellationToken: ct);
}
