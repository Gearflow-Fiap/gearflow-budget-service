using System.Security.Cryptography;
using System.Text;
using GearFlow.Billing.Adapters;
using GearFlow.Billing.Adapters.PaymentGateway.MercadoPago;
using GearFlow.Billing.Api;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GearFlow.Billing.IntegrationTests;

public class PaymentWebhookTests
{
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-11-02T12:00:00Z"); }
    private sealed class Queue : IPaymentWorkStore
    {
        public bool Fail { get; set; }
        public Dictionary<string, string?> Items { get; } = [];
        public Task EnqueueAsync(string key, string? order, Guid? paymentId, DateTimeOffset now, CancellationToken ct)
        {
            if (Fail) throw new IOException("Persistence unavailable"); Items.TryAdd(key, order); return Task.CompletedTask;
        }
        public Task<PaymentWork?> ClaimAsync(Guid owner, DateTimeOffset now, TimeSpan lease, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteAsync(PaymentWork work, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task DeferAsync(PaymentWork work, DateTimeOffset next, string code, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
    private const string Secret = "a-sufficiently-long-webhook-secret";
    private static (HttpRequest Request, MercadoPagoWebhookValidator Validator) Request(string body = "{\"type\":\"order\",\"data\":{\"id\":\"ORD123\"}}")
    {
        var clock = new Clock(); var options = Options.Create(new MercadoPagoOptions { WebhookSecret = Secret });
        var timestamp = clock.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"id:ord123;request-id:req-123;ts:{timestamp};")));
        var context = new DefaultHttpContext(); context.Request.QueryString = new QueryString("?data.id=ORD123");
        context.Request.Headers["x-request-id"] = "req-123"; context.Request.Headers["x-signature"] = $"ts={timestamp},v1={hash}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return (context.Request, new MercadoPagoWebhookValidator(options, clock));
    }
    private static int? Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode;

    [Fact]
    public async Task Validated_webhook_acknowledges_after_durable_enqueue_and_duplicate_has_one_work_item()
    {
        var queue = new Queue(); var first = Request(); var second = Request();
        Assert.Equal(200, Status(await PaymentWebhookEndpoint.Receive(first.Request, first.Validator, queue, new Clock(), default)));
        Assert.Equal(200, Status(await PaymentWebhookEndpoint.Receive(second.Request, second.Validator, queue, new Clock(), default)));
        Assert.Equal("ORD123", Assert.Single(queue.Items).Value);
    }

    [Fact]
    public async Task Invalid_signature_cannot_enqueue_even_with_paid_body()
    {
        var queue = new Queue(); var input = Request("{\"type\":\"order\",\"data\":{\"id\":\"ORD123\",\"status\":\"processed\"}}");
        input.Request.Headers["x-signature"] = "ts=1793620800,v1=" + new string('0', 64);
        Assert.Equal(401, Status(await PaymentWebhookEndpoint.Receive(input.Request, input.Validator, queue, new Clock(), default)));
        Assert.Empty(queue.Items);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("{\"type\":\"payment\",\"data\":{\"id\":\"ORD123\"}}")]
    [InlineData("{\"type\":\"order\",\"data\":{\"id\":\"ORD999\"}}")]
    public async Task Bad_body_or_different_resource_cannot_enqueue(string body)
    {
        var queue = new Queue(); var input = Request(body);
        Assert.Equal(400, Status(await PaymentWebhookEndpoint.Receive(input.Request, input.Validator, queue, new Clock(), default)));
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task Persistence_failure_is_not_acknowledged()
    {
        var input = Request();
        await Assert.ThrowsAsync<IOException>(() => PaymentWebhookEndpoint.Receive(input.Request, input.Validator, new Queue { Fail = true }, new Clock(), default));
    }

    [Fact]
    public void Test_mode_and_unsigned_local_operation_are_rejected_outside_Development()
    {
        foreach (var environment in new[] { Environments.Development, Environments.Production })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MercadoPago:AccessToken"] = "test-access-token", ["MercadoPago:TestMode"] = "true", ["MercadoPago:WebhookEnabled"] = "false",
                ["ConnectionStrings:Mongo"] = "mongodb://localhost:27017/?replicaSet=rs0"
            }).Build();
            var services = new ServiceCollection().AddLogging().AddMercadoPagoIntegration(configuration, new Environment { EnvironmentName = environment });
            using var provider = services.BuildServiceProvider();
            if (environment == Environments.Development)
                Assert.False(provider.GetRequiredService<IOptions<MercadoPagoOptions>>().Value.WebhookEnabled);
            else Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<MercadoPagoOptions>>().Value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enabled_integration_composes_all_handlers_with_scope_validation_without_starting_workers(bool mock)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:ApprovalTokenSecret"] = "a-32-byte-minimum-test-secret-123456", ["Billing:PublicBaseUrl"] = "http://localhost:5080",
            ["MercadoPago:UseMock"] = mock.ToString(),
            ["MercadoPago:AccessToken"] = mock ? "" : "test-access-token", ["MercadoPago:WebhookSecret"] = mock ? "" : Secret,
            ["ConnectionStrings:Mongo"] = "mongodb://localhost:27017/?replicaSet=rs0"
        }).Build();
        var services = new ServiceCollection().AddLogging().AddBillingCore(configuration)
            .AddMercadoPagoIntegration(configuration, new Environment());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PaymentWorkflow>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPaymentGateway>());
        Assert.Equal(mock, scope.ServiceProvider.GetRequiredService<IPaymentGateway>() is GearFlow.Billing.Adapters.PaymentGateway.MockPaymentGateway);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPaymentCompensator>());
    }
}
