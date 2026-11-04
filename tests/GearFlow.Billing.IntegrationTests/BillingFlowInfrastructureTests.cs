using System.Collections.Concurrent;
using System.Text.Json;
using GearFlow.Billing.Adapters;
using GearFlow.Billing.Adapters.Messaging;
using GearFlow.Billing.Application.Budgets;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using RabbitMQ.Client;

namespace GearFlow.Billing.IntegrationTests;

public sealed class BillingFlowFactAttribute : FactAttribute
{
    public BillingFlowFactAttribute()
    {
        if (!TestInfrastructure.Containers && ((!TestInfrastructure.NativeMongo && string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("GEARFLOW_TEST_MONGO")))
            || string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("GEARFLOW_TEST_RABBIT"))))
            Skip = "Requires Mongo and Rabbit test environments or GEARFLOW_USE_TESTCONTAINERS=true.";
    }
}

public sealed class MercadoPagoSandboxFactAttribute : FactAttribute
{
    public MercadoPagoSandboxFactAttribute()
    {
        if (System.Environment.GetEnvironmentVariable("GEARFLOW_MP_SANDBOX_CONFIRMED") != "true"
            || string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("GEARFLOW_MP_ACCESS_TOKEN")))
            Skip = "Explicit Mercado Pago test credentials and confirmation required; never runs in ordinary CI.";
    }
}

public class BillingFlowInfrastructureTests
{
    private sealed class RemoteDiagnostics { public string? Failure { get; set; } }
    private sealed class RemoteProbe(RemoteDiagnostics diagnostics) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode < 500 && (int)response.StatusCode != 429)
            {
                diagnostics.Failure = "Mercado Pago HTTP " + (int)response.StatusCode;
                try
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                        && error.GetString() is { Length: > 0 and <= 100 } code && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                        diagnostics.Failure += " (" + code + ")";
                }
                catch (JsonException) { }
            }
            return response;
        }
    }
    private sealed class EmailCapture : IEmailSender
    {
        public ConcurrentQueue<EmailMessage> Messages { get; } = new();
        public Task SendAsync(EmailMessage message, CancellationToken ct) { Messages.Enqueue(message); return Task.CompletedTask; }
    }
    private sealed class Gateway : IPaymentGateway
    {
        public GatewayPayment? Remote { get; set; }
        public int Creates { get; private set; }
        public int Refunds { get; private set; }
        public Task<GatewayPayment> CreateAsync(ChargeRequest request, CancellationToken ct)
        {
            Creates++; Remote = new(new VerifiedPayment("ORDTEST123", request.BudgetId, request.Amount), GatewayStatus.Pending,
                "TEST-PIX", "https://example.com/test-pix", request.ExpiresOn, null); return Task.FromResult(Remote);
        }
        public Task<GatewayPayment> GetAsync(string id, CancellationToken ct) => Task.FromResult(Remote!);
        public Task<GatewayPayment?> FindByBudgetAsync(Guid id, DateTimeOffset created, CancellationToken ct) => Task.FromResult(Remote);
        public Task<GatewayPayment> CancelAsync(string id, Guid operationId, CancellationToken ct)
        { Remote = Remote! with { Status = GatewayStatus.Canceled }; return Task.FromResult(Remote); }
        public Task<GatewayPayment> RefundAsync(string id, GearFlow.Billing.Domain.Core.Money amount, Guid operationId, CancellationToken ct)
        { Refunds++; Remote = Remote! with { Status = GatewayStatus.Refunded, RefundId = "REFTEST123" }; return Task.FromResult(Remote); }
    }

    [BillingFlowFact]
    public Task Real_Mongo_and_Rabbit_flow_generates_decides_charges_confirms_refunds_and_cancels_once() => Run(false);

    [MercadoPagoSandboxFact]
    public Task MercadoPago_sandbox_Pix_is_confirmed_by_query_without_webhook() => Run(true);

    private static async Task Run(bool sandbox)
    {
        await using var mongo = await TestInfrastructure.MongoAsync(); await using var rabbit = await TestInfrastructure.RabbitAsync();
        var prefix = "billing-flow-" + Guid.NewGuid().ToString("N"); var dbName = "gearflow_test_" + Guid.NewGuid().ToString("N");
        var settings = rabbit.Value; settings.Enabled = true; settings.CommandsExchange = prefix + ".commands";
        settings.EventsExchange = prefix + ".events"; settings.DeadLetterExchange = prefix + ".deadletters";
        settings.CommandsQueue = prefix + ".commands.queue"; settings.EventsQueue = prefix + ".events.queue";
        var email = new EmailCapture(); var gateway = new Gateway();
        var diagnostics = new RemoteDiagnostics();
        using var host = Host.CreateDefaultBuilder().UseEnvironment("Development").ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mongo"] = mongo.Value, ["Mongo:Database"] = dbName,
                ["Billing:ApprovalTokenSecret"] = "integration-test-only-key-32-bytes-long", ["Billing:PublicBaseUrl"] = "https://example.com",
                ["MercadoPago:Enabled"] = "true", ["MercadoPago:AccessToken"] = sandbox ? System.Environment.GetEnvironmentVariable("GEARFLOW_MP_ACCESS_TOKEN") : "test-only-access-token",
                ["MercadoPago:TestMode"] = sandbox.ToString(), ["MercadoPago:WebhookEnabled"] = (!sandbox).ToString(),
                ["MercadoPago:WebhookSecret"] = sandbox ? "" : "test-only-webhook-secret",
                ["RabbitMq:Enabled"] = "true", ["RabbitMq:Host"] = settings.Host, ["RabbitMq:Port"] = settings.Port.ToString(),
                ["RabbitMq:Username"] = settings.Username, ["RabbitMq:Password"] = settings.Password, ["RabbitMq:VirtualHost"] = settings.VirtualHost,
                ["RabbitMq:CommandsExchange"] = settings.CommandsExchange, ["RabbitMq:EventsExchange"] = settings.EventsExchange,
                ["RabbitMq:DeadLetterExchange"] = settings.DeadLetterExchange, ["RabbitMq:CommandsQueue"] = settings.CommandsQueue,
                ["RabbitMq:EventsQueue"] = settings.EventsQueue, ["Email:Enabled"] = "true"
            })).ConfigureServices((context, services) =>
            {
                services.AddBillingCore(context.Configuration).AddMercadoPagoIntegration(context.Configuration, context.HostingEnvironment);
                if (!sandbox) { services.RemoveAll<IPaymentGateway>(); services.AddSingleton<IPaymentGateway>(gateway); }
                else services.AddHttpClient<IPaymentGateway, GearFlow.Billing.Adapters.PaymentGateway.MercadoPago.MercadoPagoGateway>()
                    .AddHttpMessageHandler(() => new RemoteProbe(diagnostics));
                services.RemoveAll<IEmailSender>(); services.AddSingleton<IEmailSender>(email);
            }).Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(sandbox ? 3 : 2));
        await host.StartAsync(deadline.Token);
        var database = host.Services.GetRequiredService<IMongoDatabase>(); var publisher = host.Services.GetRequiredService<IConfirmedPublisher>();
        var order = Guid.NewGuid(); var generated = Guid.NewGuid();
        try
        {
            if (sandbox)
            {
                using var probeScope = host.Services.CreateScope();
                Assert.Null(await probeScope.ServiceProvider.GetRequiredService<IPaymentGateway>()
                    .FindByBudgetAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, deadline.Token));
            }
            var amount = sandbox ? 50m : 350m;
            var proposal = Envelope("GenerateBudget", generated, new { customer = new { name = "Ana", email = sandbox ? "test_payer@example.com" : "ana@example.com" },
                expiresOn = DateTimeOffset.UtcNow.AddHours(1), items = new[] { new { referenceId = Guid.NewGuid(), type = "Job", description = "Serviço", unitPrice = amount, quantity = 1m } } });
            await Publish(proposal); await Publish(proposal);
            await Until(async () => await Count("budgets") == 1 && email.Messages.Count == 1);
            var document = await database.GetCollection<BsonDocument>("budgets").Find(new BsonDocument("serviceOrderId", order.ToString("D"))).SingleAsync(deadline.Token);
            var budgetId = Guid.Parse(document["_id"].AsString);
            using (var scope = host.Services.CreateScope())
            {
                var token = scope.ServiceProvider.GetRequiredService<IApprovalTokenService>().Create(budgetId, proposal.Payload.GetProperty("expiresOn").GetDateTimeOffset());
                Assert.True((await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ApproveBudgetCommand(budgetId, token), deadline.Token)).IsSuccess);
            }
            await Publish(Envelope("RequestPayment", Guid.NewGuid(), new { budgetId, amount, currency = "BRL" }));
            if (sandbox)
            {
                // Poll the same recovery use case used by reconciliation; no HTTP webhook or public listener.
                await Until(async () =>
                {
                    var entry = await database.GetCollection<BsonDocument>("payments").Find(new BsonDocument("budgetId", budgetId.ToString("D"))).SingleOrDefaultAsync(deadline.Token);
                    if (entry is null || entry.GetValue("providerOrderId", BsonNull.Value).IsBsonNull) return false;
                    using var scope = host.Services.CreateScope();
                    var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RecoverPaymentCommand(Guid.Parse(entry["_id"].AsString)), deadline.Token);
                    Assert.True(result.IsSuccess, result.Error?.Code);
                    return await EventCount("PaymentConfirmed") == 1;
                }, TimeSpan.FromSeconds(3));
                var paid = await database.GetCollection<BsonDocument>("payments").Find(new BsonDocument("budgetId", budgetId.ToString("D"))).SingleAsync(deadline.Token);
                Assert.Equal("Paid", paid["status"].AsString);
                var budget = await database.GetCollection<BsonDocument>("budgets").Find(new BsonDocument("_id", budgetId.ToString("D"))).SingleAsync(deadline.Token);
                Assert.Equal("Paid", budget["status"].AsString);
                await Until(async () => await Count("outbox", new BsonDocument("sentOn", BsonNull.Value)) == 0);
                var evidence = System.Environment.GetEnvironmentVariable("GEARFLOW_MP_EVIDENCE_PATH");
                if (!string.IsNullOrWhiteSpace(evidence)) await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(new
                { verifiedOn = DateTimeOffset.UtcNow, amount, currency = "BRL", budgetId, providerOrderId = paid["providerOrderId"].AsString,
                    status = "Paid", confirmation = "provider_query", webhookEnabled = false, budgetConfirmed = true, outboxPublished = true }), deadline.Token);
                return;
            }
            await Until(async () => await EventCount("PaymentRequested") == 1 && email.Messages.Count >= 2);
            Assert.Equal(1, gateway.Creates);
            gateway.Remote = gateway.Remote! with { Status = GatewayStatus.Paid };
            using (var scope = host.Services.CreateScope())
                Assert.True((await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ProcessPaymentWebhookCommand("ORDTEST123"), deadline.Token)).IsSuccess);
            await Until(async () => await EventCount("PaymentConfirmed") == 1);
            var payment = await database.GetCollection<BsonDocument>("payments").Find(new BsonDocument("budgetId", budgetId.ToString("D"))).SingleAsync(deadline.Token);
            var paymentId = Guid.Parse(payment["_id"].AsString);
            await Publish(Envelope("RefundPayment", Guid.NewGuid(), new { budgetId, paymentId, reason = "stock_unavailable" }));
            await Until(async () => await EventCount("PaymentRefunded") == 1);
            await Publish(Envelope("CancelBudget", Guid.NewGuid(), new { budgetId, reason = "stock_unavailable" }));
            await Until(async () => await EventCount("BudgetCanceled") == 1 && await Count("inbox", new BsonDocument("completedOn", new BsonDocument("$ne", BsonNull.Value))) == 4);
            Assert.Equal(1, gateway.Refunds); Assert.Equal(1, await Count("budgets"));
            Assert.Equal(1, await EventCount("BudgetGenerated"));
            var read = await database.GetCollection<BsonDocument>("budgets").Find(new BsonDocument("_id", budgetId.ToString("D"))).SingleAsync(deadline.Token);
            Assert.Equal("Canceled", read["status"].AsString);
            await Until(async () => await Count("outbox", new BsonDocument("sentOn", BsonNull.Value)) == 0);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            await host.Services.GetRequiredService<IMongoClient>().DropDatabaseAsync(dbName);
            await using var connection = await settings.Factory().CreateConnectionAsync(); await using var channel = await connection.CreateChannelAsync();
            foreach (var queue in new[] { settings.CommandsQueue, settings.CommandsQueue + ".dlq", settings.EventsQueue }) await channel.QueueDeleteAsync(queue, false, false);
            foreach (var exchange in new[] { settings.CommandsExchange, settings.EventsExchange, settings.DeadLetterExchange }) await channel.ExchangeDeleteAsync(exchange);
        }

        WireEnvelope Envelope(string type, Guid id, object payload) => new(id, type, 1, DateTimeOffset.UtcNow, order, order, null, "gearflow-os-service", JsonSerializer.SerializeToElement(payload, WireContracts.Json));
        Task Publish(WireEnvelope message) => publisher.PublishAsync(settings.CommandsExchange, "billing." + message.MessageType,
            JsonSerializer.SerializeToUtf8Bytes(message, WireContracts.Json), message.MessageId.ToString("D"), order.ToString("D"), null, deadline.Token);
        Task<long> Count(string collection, BsonDocument? filter = null) => database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(filter ?? new BsonDocument(), cancellationToken: deadline.Token);
        Task<long> EventCount(string name) => Count("outbox", new BsonDocument("type", name));
        async Task Until(Func<Task<bool>> condition, TimeSpan? interval = null)
        {
            while (!await condition())
            {
                deadline.Token.ThrowIfCancellationRequested();
                // Fail early on a definitive provider/command failure, without logging payloads or secrets.
                if (sandbox)
                {
                    Assert.True(diagnostics.Failure is null, diagnostics.Failure);
                    var dead = await database.GetCollection<BsonDocument>("outbox").Find(new BsonDocument("type", "PaymentFailed")).FirstOrDefaultAsync(deadline.Token);
                    Assert.Null(dead);
                }
                await Task.Delay(interval ?? TimeSpan.FromMilliseconds(250), deadline.Token);
            }
        }
    }
}
