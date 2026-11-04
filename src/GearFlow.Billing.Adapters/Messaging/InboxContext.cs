using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Ports.Out;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GearFlow.Billing.Adapters.Messaging;

public sealed record InboxLease(string Key, string Digest, Guid Owner, IncomingCommandKind Kind);

public sealed class InboxContext(IMongoDatabase database, IClock clock) : ICommandCompletion
{
    private InboxLease? lease;
    private bool completing;
    public IncomingCommandKind? Kind => lease?.Kind;
    public void Activate(InboxLease value) { lease = value; completing = false; }
    public Task CompleteAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); completing = true; return Task.CompletedTask; }
    public void ResetCompletion() => completing = false;

    public async Task OnCommittingAsync(IClientSessionHandle session, CancellationToken ct)
    {
        if (lease is null) return;
        var now = clock.UtcNow;
        var filter = new BsonDocument { ["_id"] = lease.Key, ["digest"] = lease.Digest, ["owner"] = lease.Owner.ToString("D"),
            ["completedOn"] = BsonNull.Value, ["leaseUntil"] = new BsonDocument("$gt", now.UtcDateTime) };
        var update = completing
            ? new BsonDocument { ["completedOn"] = now.UtcDateTime, ["leaseUntil"] = now.UtcDateTime }
            : new BsonDocument { ["leaseUntil"] = now.AddMinutes(5).UtcDateTime };
        var result = await database.GetCollection<BsonDocument>("inbox").UpdateOneAsync(session, filter,
            new BsonDocument("$set", update), cancellationToken: ct);
        if (result.MatchedCount != 1) throw new ConcurrencyException();
    }
}
