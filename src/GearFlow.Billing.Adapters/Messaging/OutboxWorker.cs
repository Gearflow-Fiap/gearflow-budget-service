using GearFlow.Billing.Adapters.Operations;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GearFlow.Billing.Adapters.Messaging;

public sealed class OutboxWorker(IMongoDatabase database, IConfirmedPublisher publisher, IOptions<RabbitOptions> options,
    IClock clock, WorkerHealth health, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var collection = database.GetCollection<BsonDocument>("outbox").WithWriteConcern(WriteConcern.WMajority);
        while (!stoppingToken.IsCancellationRequested)
        {
            BsonDocument? document = null; var owner = Guid.NewGuid().ToString("D");
            try
            {
                var now = clock.UtcNow;
                document = await collection.FindOneAndUpdateAsync(new BsonDocument { ["sentOn"] = BsonNull.Value,
                    ["$and"] = new BsonArray { new BsonDocument("$or", new BsonArray { new BsonDocument("availableOn", BsonNull.Value), new BsonDocument("availableOn", new BsonDocument("$lte", now.UtcDateTime)) }),
                        new BsonDocument("$or", new BsonArray { new BsonDocument("leaseUntil", BsonNull.Value), new BsonDocument("leaseUntil", new BsonDocument("$lte", now.UtcDateTime)) }) } },
                    new BsonDocument { ["$set"] = new BsonDocument { ["owner"] = owner, ["leaseUntil"] = now.AddSeconds(45).UtcDateTime }, ["$inc"] = new BsonDocument("attempts", 1) },
                    new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After, Sort = new BsonDocument("occurredOn", 1) }, stoppingToken);
                health.Beat("outbox");
                if (document is null) { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); continue; }
                var message = WireContracts.ReadEvent(document["type"].AsString, document["payload"].AsString);
                var headers = new Dictionary<string, object?>();
                foreach (var name in new[] { "traceparent", "tracestate" })
                    if (document.TryGetValue(name, out var value) && value.IsString) headers[name] = value.AsString;
                await publisher.PublishAsync(options.Value.EventsExchange, $"gearflow-budget-service.{message.GetType().Name}",
                    WireContracts.Event(message), message.MessageId.ToString("D"), message.ServiceOrderId.ToString("D"), headers, stoppingToken);
                await collection.UpdateOneAsync(new BsonDocument { ["_id"] = document["_id"], ["owner"] = owner, ["leaseUntil"] = new BsonDocument("$gt", clock.UtcNow.UtcDateTime) },
                    new BsonDocument("$set", new BsonDocument { ["sentOn"] = clock.UtcNow.UtcDateTime, ["owner"] = BsonNull.Value }), cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Outbox deferred: {Code}", exception.GetType().Name);
                if (document is not null)
                {
                    try
                    {
                        await collection.UpdateOneAsync(new BsonDocument { ["_id"] = document["_id"], ["owner"] = owner, ["sentOn"] = BsonNull.Value },
                            new BsonDocument("$set", new BsonDocument { ["availableOn"] = clock.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(8, document["attempts"].AsInt32)))).UtcDateTime,
                                ["leaseUntil"] = clock.UtcNow.UtcDateTime, ["owner"] = BsonNull.Value, ["lastError"] = exception.GetType().Name }), cancellationToken: stoppingToken);
                    }
                    catch (Exception) when (!stoppingToken.IsCancellationRequested) { }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
