using System.Diagnostics.Metrics;
using GearFlow.Billing.Adapters.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GearFlow.Billing.Adapters.Operations;

public sealed class OperationalMetricsWorker(IMongoDatabase database, IOptions<RabbitOptions> rabbit,
    ILogger<OperationalMetricsWorker> logger) : BackgroundService
{
    private long pendingOutbox, failedNotifications, uncertainPayments, dlqDepth;
    private double outboxLag;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        BillingTelemetry.Meter.CreateObservableGauge("billing.outbox.pending", () => Interlocked.Read(ref pendingOutbox));
        BillingTelemetry.Meter.CreateObservableGauge("billing.outbox.oldest.age", () => Volatile.Read(ref outboxLag), "s");
        BillingTelemetry.Meter.CreateObservableGauge("billing.notifications.failed", () => Interlocked.Read(ref failedNotifications));
        BillingTelemetry.Meter.CreateObservableGauge("billing.payments.uncertain", () => Interlocked.Read(ref uncertainPayments));
        BillingTelemetry.Meter.CreateObservableGauge("billing.dlq.depth", () => Interlocked.Read(ref dlqDepth));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var outbox = database.GetCollection<BsonDocument>("outbox");
                pendingOutbox = await outbox.CountDocumentsAsync(new BsonDocument("sentOn", BsonNull.Value), cancellationToken: ct);
                var oldest = await outbox.Find(new BsonDocument("sentOn", BsonNull.Value)).Sort(new BsonDocument("occurredOn", 1)).FirstOrDefaultAsync(ct);
                outboxLag = oldest is null ? 0 : Math.Max(0, (DateTime.UtcNow - oldest["occurredOn"].ToUniversalTime()).TotalSeconds);
                failedNotifications = await database.GetCollection<BsonDocument>("notifications").CountDocumentsAsync(new BsonDocument("status", "Failed"), cancellationToken: ct);
                uncertainPayments = await database.GetCollection<BsonDocument>("payments").CountDocumentsAsync(new BsonDocument("unfinished", true), cancellationToken: ct);
                if (rabbit.Value.Enabled)
                {
                    await using var connection = await rabbit.Value.Factory().CreateConnectionAsync(ct);
                    await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
                    dlqDepth = (await channel.QueueDeclarePassiveAsync(rabbit.Value.CommandsQueue + ".dlq", ct)).MessageCount;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Metrics collection deferred: {Code}", exception.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
