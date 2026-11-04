using GearFlow.Billing.Application.Payments;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GearFlow.Billing.Adapters.Persistence.Mongo;

public sealed class MongoPaymentWorkStore(IMongoDatabase database) : IPaymentWorkStore
{
    private readonly IMongoCollection<BsonDocument> collection = database.GetCollection<BsonDocument>("payment_work").WithWriteConcern(WriteConcern.WMajority);
    public async Task EnqueueAsync(string key, string? orderId, Guid? paymentId, DateTimeOffset now, CancellationToken ct)
    {
        var document = new BsonDocument
        {
            ["_id"] = key, ["orderId"] = orderId is null ? BsonNull.Value : new BsonString(orderId),
            ["paymentId"] = paymentId is null ? BsonNull.Value : new BsonString(paymentId.Value.ToString("D")),
            ["availableOn"] = now.UtcDateTime, ["completedOn"] = BsonNull.Value,
            ["leaseUntil"] = now.UtcDateTime, ["owner"] = BsonNull.Value, ["attempts"] = 0
        };
        try { await collection.InsertOneAsync(document, cancellationToken: ct); }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }
    public async Task<PaymentWork?> ClaimAsync(Guid owner, DateTimeOffset now, TimeSpan lease, CancellationToken ct)
    {
        var filter = new BsonDocument
        {
            ["completedOn"] = BsonNull.Value, ["availableOn"] = new BsonDocument("$lte", now.UtcDateTime),
            ["leaseUntil"] = new BsonDocument("$lte", now.UtcDateTime)
        };
        var update = new BsonDocument
        {
            ["$set"] = new BsonDocument { ["owner"] = owner.ToString("D"), ["leaseUntil"] = (now + lease).UtcDateTime },
            ["$inc"] = new BsonDocument("attempts", 1)
        };
        var document = await collection.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After, Sort = new BsonDocument("availableOn", 1) }, ct);
        return document is null ? null : new PaymentWork(document["_id"].AsString,
            document["orderId"].IsBsonNull ? null : document["orderId"].AsString,
            document["paymentId"].IsBsonNull ? null : Guid.Parse(document["paymentId"].AsString), owner, document["attempts"].AsInt32);
    }
    public Task CompleteAsync(PaymentWork work, DateTimeOffset now, CancellationToken ct) =>
        collection.UpdateOneAsync(Owned(work, now), new BsonDocument("$set", new BsonDocument
            { ["completedOn"] = now.UtcDateTime, ["owner"] = BsonNull.Value }), cancellationToken: ct);
    public Task DeferAsync(PaymentWork work, DateTimeOffset nextAttemptOn, string code, CancellationToken ct) =>
        collection.UpdateOneAsync(new BsonDocument { ["_id"] = work.Key, ["owner"] = work.Owner.ToString("D"), ["completedOn"] = BsonNull.Value },
            new BsonDocument("$set", new BsonDocument { ["availableOn"] = nextAttemptOn.UtcDateTime,
                ["leaseUntil"] = nextAttemptOn.UtcDateTime, ["owner"] = BsonNull.Value, ["lastError"] = code }), cancellationToken: ct);
    private static BsonDocument Owned(PaymentWork work, DateTimeOffset now) => new()
    {
        ["_id"] = work.Key, ["owner"] = work.Owner.ToString("D"), ["completedOn"] = BsonNull.Value,
        ["leaseUntil"] = new BsonDocument("$gt", now.UtcDateTime)
    };
}
