using GearFlow.Billing.Adapters.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GearFlow.Billing.Adapters.Operations;

public sealed class Readiness(IMongoDatabase database, IOptions<RabbitOptions> rabbit, IConfiguration configuration, WorkerHealth health)
{
    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("RabbitMq:Enabled") || !configuration.GetValue<bool>("Email:Enabled")
            || !configuration.GetValue<bool>("BillingHttp:Enabled")) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var hello = await database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: deadline.Token);
            if (!hello.GetValue("isWritablePrimary", false).ToBoolean()) return false;
            await using var connection = await rabbit.Value.Factory().CreateConnectionAsync(deadline.Token);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: deadline.Token);
            foreach (var name in new[] { rabbit.Value.CommandsExchange, rabbit.Value.EventsExchange, rabbit.Value.DeadLetterExchange })
                await channel.ExchangeDeclarePassiveAsync(name, deadline.Token);
            foreach (var queue in new[] { rabbit.Value.CommandsQueue, rabbit.Value.CommandsQueue + ".dlq", rabbit.Value.EventsQueue })
                await channel.QueueDeclarePassiveAsync(queue, deadline.Token);
            return health.Alive("rabbit-consumer", TimeSpan.FromSeconds(30)) && health.Alive("outbox", TimeSpan.FromMinutes(1))
                && health.Alive("notifications", TimeSpan.FromMinutes(3)) && health.Alive("payment-work", TimeSpan.FromMinutes(3))
                && health.Alive("payment-reconciliation", TimeSpan.FromMinutes(6));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }
}
