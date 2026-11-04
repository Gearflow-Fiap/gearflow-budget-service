using GearFlow.Billing.Adapters.Operations;
using GearFlow.Billing.Application.Notifications;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GearFlow.Billing.Adapters.Email;

public sealed class MongoNotificationReadModel(IMongoDatabase database) : INotificationReadModel
{
    public async Task<IReadOnlyList<Guid>> FindDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var documents = await database.GetCollection<BsonDocument>("notifications").Find(new BsonDocument
        {
            ["status"] = new BsonDocument("$in", new BsonArray { "Pending", "Sending" }),
            ["availableOn"] = new BsonDocument("$lte", now.UtcDateTime),
            ["$or"] = new BsonArray { new BsonDocument("leaseUntil", BsonNull.Value), new BsonDocument("leaseUntil", new BsonDocument("$lte", now.UtcDateTime)) }
        }).Sort(new BsonDocument("availableOn", 1)).Limit(limit).ToListAsync(ct);
        return documents.Select(d => Guid.Parse(d["_id"].AsString)).ToArray();
    }
}

public sealed class NotificationWorker(IServiceScopeFactory scopes, INotificationReadModel readModel, IClock clock,
    WorkerHealth health, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = await readModel.FindDueAsync(clock.UtcNow, 10, stoppingToken);
                health.Beat("notifications");
                foreach (var id in due)
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new DispatchNotificationCommand(id), stoppingToken);
                    health.Beat("notifications");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Notification worker deferred: {Code}", exception.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
