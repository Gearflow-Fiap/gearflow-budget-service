using System.Text.Json;
using GearFlow.Billing.Adapters.Messaging;
using GearFlow.Billing.Adapters.Operations;
using GearFlow.Billing.Adapters.Persistence.Mongo;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using RabbitMQ.Client;

namespace GearFlow.Billing.IntegrationTests;

public class MessagingRecoveryTests
{
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class LostResponsePublisher(IConfirmedPublisher inner) : IConfirmedPublisher
    {
        public async Task PublishAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, string id, string correlation,
            IDictionary<string, object?>? headers, CancellationToken ct)
        {
            await inner.PublishAsync(exchange, routingKey, body, id, correlation, headers, ct);
            throw new IOException("Simulated crash after broker confirm, before local sent marker.");
        }
    }
    private sealed class UnavailablePublisher : IConfirmedPublisher
    {
        public Task PublishAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, string id, string correlation,
            IDictionary<string, object?>? headers, CancellationToken ct) => throw new IOException("Simulated unavailable transport.");
    }

    [BillingFlowFact]
    public Task Confirmed_but_unmarked_outbox_is_republished_with_the_same_identity_after_restart() => WithInfrastructure(async (database, client, settings, channel, ct) =>
    {
        var clock = new Clock(); var session = new MongoSession(client); var store = new MongoBillingStore(database, session, clock);
        var message = new BudgetGenerated(Guid.NewGuid(), Guid.NewGuid(), null, clock.UtcNow, Guid.NewGuid(), 350, "BRL", clock.UtcNow.AddHours(1));
        await session.ExecuteAsync(async token => { await store.AppendAsync(message, token); return true; }, ct);
        var publisher = new RabbitPublisher(Options.Create(settings));
        using (var first = Worker(new LostResponsePublisher(publisher)))
        {
            await first.StartAsync(ct);
            await Until(async () => (await Document()).GetValue("lastError", "").AsString == nameof(IOException), ct);
            await first.StopAsync(ct);
        }
        Assert.True((await Document())["sentOn"].IsBsonNull);
        using (var restarted = Worker(publisher))
        {
            await restarted.StartAsync(ct);
            await Until(async () => !(await Document())["sentOn"].IsBsonNull, ct);
            await restarted.StopAsync(ct);
        }
        var one = await channel.BasicGetAsync(settings.EventsQueue, true, ct);
        var two = await channel.BasicGetAsync(settings.EventsQueue, true, ct);
        Assert.NotNull(one); Assert.NotNull(two);
        Assert.Equal(message.MessageId.ToString("D"), one.BasicProperties.MessageId);
        Assert.Equal(one.BasicProperties.MessageId, two.BasicProperties.MessageId);
        Assert.Equal(one.Body.ToArray(), two.Body.ToArray());
        Assert.Equal(2, (await Document())["attempts"].AsInt32);
        Task<BsonDocument> Document() => database.GetCollection<BsonDocument>("outbox").Find(new BsonDocument("_id", message.MessageId.ToString("D"))).SingleAsync(ct);
        OutboxWorker Worker(IConfirmedPublisher transport) => new(database, transport, Options.Create(settings), clock, new WorkerHealth(), NullLogger<OutboxWorker>.Instance);
    });

    [BillingFlowFact]
    public Task Unavailable_connection_leaves_outbox_pending_and_recovers_when_endpoint_returns() => WithInfrastructure(async (database, client, settings, channel, ct) =>
    {
        var clock = new Clock(); var session = new MongoSession(client); var store = new MongoBillingStore(database, session, clock);
        var message = new BudgetGenerated(Guid.NewGuid(), Guid.NewGuid(), null, clock.UtcNow, Guid.NewGuid(), 350, "BRL", clock.UtcNow.AddHours(1));
        await session.ExecuteAsync(async token => { await store.AppendAsync(message, token); return true; }, ct);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var unusedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var availablePort = settings.Port; settings.Port = unusedPort;
        using (var unavailable = Worker())
        {
            await unavailable.StartAsync(ct);
            await Until(async () => !(await Document()).GetValue("lastError", BsonNull.Value).IsBsonNull, ct);
            await unavailable.StopAsync(ct);
        }
        Assert.True((await Document())["sentOn"].IsBsonNull);
        Assert.Null(await channel.BasicGetAsync(settings.EventsQueue, true, ct));
        settings.Port = availablePort;
        using (var recovered = Worker())
        {
            await recovered.StartAsync(ct);
            await Until(async () => !(await Document())["sentOn"].IsBsonNull, ct);
            await recovered.StopAsync(ct);
        }
        var received = await channel.BasicGetAsync(settings.EventsQueue, true, ct);
        Assert.NotNull(received); Assert.Equal(message.MessageId.ToString("D"), received.BasicProperties.MessageId);
        Assert.Null(await channel.BasicGetAsync(settings.EventsQueue, true, ct));
        Task<BsonDocument> Document() => database.GetCollection<BsonDocument>("outbox").Find(new BsonDocument("_id", message.MessageId.ToString("D"))).SingleAsync(ct);
        OutboxWorker Worker() => new(database, new RabbitPublisher(Options.Create(settings)), Options.Create(settings), clock, new WorkerHealth(), NullLogger<OutboxWorker>.Instance);
    });

    [BillingFlowFact]
    public Task Failed_DLQ_replay_preserves_source_then_confirmed_replay_keeps_ids_and_audit() => WithInfrastructure(async (database, _, settings, channel, ct) =>
    {
        var clock = new Clock(); var order = Guid.NewGuid(); var id = Guid.NewGuid();
        var envelope = new WireEnvelope(id, "GenerateBudget", 1, clock.UtcNow, order, order, null, "gearflow-os-service",
            JsonSerializer.SerializeToElement(new { customer = new { name = "Ana", email = "ana@example.com" }, expiresOn = clock.UtcNow.AddHours(1),
                items = new[] { new { referenceId = Guid.NewGuid(), type = "Job", description = "Serviço", unitPrice = 350, quantity = 1 } } }, WireContracts.Json));
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, WireContracts.Json);
        var dead = new DeadLetter(settings.CommandsExchange, "billing.GenerateBudget", id.ToString("D"), order.ToString("D"),
            Convert.ToBase64String(body), "transport.unavailable", 4, clock.UtcNow, null, null);
        var publisher = new RabbitPublisher(Options.Create(settings));
        await publisher.PublishAsync(settings.DeadLetterExchange, settings.CommandsQueue, JsonSerializer.SerializeToUtf8Bytes(dead, WireContracts.Json),
            id.ToString("D"), order.ToString("D"), null, ct);
        var failed = new DlqReplay(database, new UnavailablePublisher(), Options.Create(settings), clock);
        await Assert.ThrowsAsync<IOException>(() => failed.ReplayOneAsync("test-operator-ticket", ct));
        await Until(async () => (await channel.QueueDeclarePassiveAsync(settings.CommandsQueue + ".dlq", ct)).MessageCount == 1, ct);
        var replay = new DlqReplay(database, publisher, Options.Create(settings), clock);
        Assert.True(await replay.ReplayOneAsync("test-operator-ticket", ct));
        Assert.Equal(0u, (await channel.QueueDeclarePassiveAsync(settings.CommandsQueue + ".dlq", ct)).MessageCount);
        var received = await channel.BasicGetAsync(settings.CommandsQueue, true, ct);
        Assert.NotNull(received); Assert.Equal(id.ToString("D"), received.BasicProperties.MessageId);
        Assert.Equal(order.ToString("D"), received.BasicProperties.CorrelationId); Assert.Equal(body, received.Body.ToArray());
        var audits = await database.GetCollection<BsonDocument>("replay_audit").Find(new BsonDocument()).ToListAsync(ct);
        Assert.Equal(2, audits.Count); Assert.Single(audits, entry => entry["state"] == "published");
        Assert.All(audits, entry => Assert.Equal("test-operator-ticket", entry["actor"].AsString));
    });

    private static async Task WithInfrastructure(Func<IMongoDatabase, MongoClient, RabbitOptions, IChannel, CancellationToken, Task> run)
    {
        await using var mongo = await TestInfrastructure.MongoAsync(); await using var rabbit = await TestInfrastructure.RabbitAsync();
        var client = new MongoClient(mongo.Value); var name = "gearflow_test_" + Guid.NewGuid().ToString("N"); var database = client.GetDatabase(name);
        var settings = rabbit.Value; var prefix = "billing-recovery-" + Guid.NewGuid().ToString("N");
        settings.CommandsExchange = prefix + ".commands"; settings.EventsExchange = prefix + ".events";
        settings.DeadLetterExchange = prefix + ".dead"; settings.CommandsQueue = prefix + ".queue"; settings.EventsQueue = prefix + ".events.queue";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1)); var ct = deadline.Token;
        await new MongoInitializer(database).StartAsync(ct); await new RabbitTopology(Options.Create(settings)).StartAsync(ct);
        await using var connection = await settings.Factory().CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        try { await run(database, client, settings, channel, ct); }
        finally
        {
            await client.DropDatabaseAsync(name);
            foreach (var queue in new[] { settings.CommandsQueue, settings.CommandsQueue + ".dlq", settings.EventsQueue }) await channel.QueueDeleteAsync(queue, false, false);
            foreach (var exchange in new[] { settings.CommandsExchange, settings.EventsExchange, settings.DeadLetterExchange }) await channel.ExchangeDeleteAsync(exchange);
        }
    }
    private static async Task Until(Func<Task<bool>> condition, CancellationToken ct)
    { while (!await condition()) await Task.Delay(100, ct); }
}
