using System.Text.Json;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using RabbitMQ.Client;

namespace GearFlow.Billing.Adapters.Messaging;

// Explicit operator command, never a recurring automatic replay.
public sealed class DlqReplay(IMongoDatabase database, IConfirmedPublisher publisher, IOptions<RabbitOptions> options, IClock clock)
{
    public async Task<bool> ReplayOneAsync(string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200) throw new ArgumentException("An audit actor is required.");
        await using var connection = await options.Value.Factory().CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var delivery = await channel.BasicGetAsync(options.Value.CommandsQueue + ".dlq", false, ct);
        if (delivery is null) return false;
        var dead = JsonSerializer.Deserialize<DeadLetter>(delivery.Body.Span, WireContracts.Json) ?? throw new DeliveryException("dlq.payload");
        var body = Convert.FromBase64String(dead.BodyBase64);
        if (dead.OriginalExchange != options.Value.CommandsExchange) throw new DeliveryException("dlq.exchange");
        // Requires corrected/valid original payload, keeps its original identity and causality.
        WireContracts.Parse(body, dead.OriginalRoutingKey, dead.MessageId, dead.CorrelationId);
        var id = Guid.NewGuid().ToString("D");
        var audit = database.GetCollection<BsonDocument>("replay_audit").WithWriteConcern(WriteConcern.WMajority);
        await audit.InsertOneAsync(new BsonDocument { ["_id"] = id, ["actor"] = actor, ["messageId"] = dead.MessageId,
            ["startedOn"] = clock.UtcNow.UtcDateTime, ["state"] = "started" }, cancellationToken: ct);
        var headers = new Dictionary<string, object?>();
        if (dead.TraceParent is not null) headers["traceparent"] = dead.TraceParent;
        if (dead.TraceState is not null) headers["tracestate"] = dead.TraceState;
        await publisher.PublishAsync(dead.OriginalExchange, dead.OriginalRoutingKey, body, dead.MessageId!, dead.CorrelationId!, headers, ct);
        await audit.UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument("$set", new BsonDocument
            { ["state"] = "published", ["publishedOn"] = clock.UtcNow.UtcDateTime }), cancellationToken: ct);
        await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        return true;
    }
}
