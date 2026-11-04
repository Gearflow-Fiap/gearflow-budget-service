using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using GearFlow.Billing.Adapters.Email;
using GearFlow.Billing.Adapters.Messaging;
using GearFlow.Billing.Adapters.PaymentGateway.MercadoPago;
using GearFlow.Billing.Adapters.Persistence.Mongo;
using GearFlow.Billing.Api;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace GearFlow.Billing.IntegrationTests;

public class BusinessHttpTests
{
    private const string Secret = "http-test-only-secret-32-bytes-long-12345";
    private sealed class State : IBudgetRepository, IBudgetReadModel, IPaymentReadModel, IUnitOfWork, IOutboxWriter, IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        public Budget Budget { get; }
        public List<IntegrationEvent> Events { get; } = [];
        public State() => Budget = new(Guid.NewGuid(), Guid.NewGuid(), new Customer("Ana", new EmailAddress("ana@example.com")),
            [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Serviço", new Money(350), 1)], UtcNow, UtcNow.AddHours(1));
        public Task<Budget?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Budget.Id == id ? Budget : null);
        public Task<Budget?> FindByServiceOrderAsync(Guid id, CancellationToken ct) => Task.FromResult(Budget.ServiceOrderId == id ? Budget : null);
        public Task AddAsync(Budget budget, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(Budget budget, long version, CancellationToken ct) => Task.CompletedTask;
        private BudgetView? View(Guid id) => Budget.Id == id ? new(Budget.Id, Budget.ServiceOrderId, Budget.Status.ToString(), 350, "BRL", Budget.ExpiresOn) : null;
        Task<BudgetView?> IBudgetReadModel.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(View(id));
        Task<BudgetView?> IBudgetReadModel.FindByServiceOrderAsync(Guid id, CancellationToken ct) => Task.FromResult(View(Budget.ServiceOrderId == id ? Budget.Id : Guid.Empty));
        Task<PaymentView?> IPaymentReadModel.GetAsync(Guid id, CancellationToken ct) => Task.FromResult<PaymentView?>(null);
        Task<PaymentView?> IPaymentReadModel.FindByBudgetAsync(Guid id, CancellationToken ct) => Task.FromResult<PaymentView?>(null);
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) => action(ct);
        public Task AppendAsync(IntegrationEvent message, CancellationToken ct) { Events.Add(message); return Task.CompletedTask; }
    }
    private sealed class Factory(bool localPix = false) : WebApplicationFactory<Program>
    {
        public State State { get; } = new();
        private static readonly Dictionary<string, string?> Settings = new()
        {
            ["Billing:ApprovalTokenSecret"] = Secret, ["Billing:PublicBaseUrl"] = "https://localhost",
            ["MercadoPago:Enabled"] = "true", ["MercadoPago:AccessToken"] = "test-only-access-token", ["MercadoPago:WebhookSecret"] = Secret,
            ["ConnectionStrings:Mongo"] = "mongodb://localhost:27017/?replicaSet=rs0", ["RabbitMq:Enabled"] = "false", ["Email:Enabled"] = "false",
            ["BillingHttp:Enabled"] = "true", ["Jwt:Mode"] = "Hmac", ["Jwt:Secret"] = Secret, ["Jwt:Issuer"] = "GearFlow.Api", ["Jwt:Audience"] = "GearFlow.Client",
            ["Telemetry:Enabled"] = "false"
        };
        private Dictionary<string, string?> ConfigurationValues() => localPix ? new(Settings)
        {
            ["MercadoPago:TestMode"] = "true", ["MercadoPago:WebhookEnabled"] = "false", ["MercadoPago:WebhookSecret"] = ""
        } : Settings;
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(ConfigurationValues()));
            return base.CreateHost(builder);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(ConfigurationValues()));
            builder.ConfigureServices(services =>
            {
                var workers = new[] { typeof(MongoInitializer), typeof(PaymentWorkWorker), typeof(PaymentReconciliationWorker), typeof(RabbitTopology), typeof(RabbitCommandWorker), typeof(OutboxWorker), typeof(NotificationWorker) };
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType is { } t && workers.Contains(t)).ToArray()) services.Remove(descriptor);
                services.RemoveAll<IBudgetRepository>(); services.AddSingleton<IBudgetRepository>(State);
                services.RemoveAll<IBudgetReadModel>(); services.AddSingleton<IBudgetReadModel>(State);
                services.RemoveAll<IPaymentReadModel>(); services.AddSingleton<IPaymentReadModel>(State);
                services.RemoveAll<IUnitOfWork>(); services.AddSingleton<IUnitOfWork>(State);
                services.RemoveAll<IOutboxWriter>(); services.AddSingleton<IOutboxWriter>(State);
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(State);
                // This HTTP-only fixture does not exercise persistent fences (covered by Mongo infrastructure tests).
                services.RemoveAll<ICommercialFenceRepository>();
            });
        }
        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        public string ApprovalToken() => Services.GetRequiredService<IApprovalTokenService>().Create(State.Budget.Id, State.Budget.ExpiresOn);
    }
    private static string Token(string? role = null, Guid? order = null, string? secret = null, bool expired = false)
    {
        var claims = new List<Claim> { new("sub", "test-user") };
        if (role is not null) claims.Add(new("role", role)); if (order is { } id) claims.Add(new("service_order_id", id.ToString("D")));
        var jwt = new JwtSecurityToken("GearFlow.Api", "GearFlow.Client", claims, DateTime.UtcNow.AddHours(-1),
            expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret ?? Secret)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    [Fact]
    public async Task Local_Pix_mode_starts_without_webhook_secret_and_does_not_expose_webhook_route()
    {
        using var factory = new Factory(localPix: true); using var client = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Contains((await client.PostAsJsonAsync("/api/payments/webhook", new { type = "order" })).StatusCode,
            new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        var routes = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>();
        Assert.DoesNotContain(routes, route => route.RoutePattern.RawText == "/api/payments/webhook");
    }

    [Fact]
    public async Task Swagger_describes_JWT_queries_and_anonymous_explicit_decisions_without_creation_routes()
    {
        using var factory = new Factory(); using var client = factory.Client();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = json.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/budgets/{id}", out var budget));
        Assert.True(budget.TryGetProperty("get", out _));
        Assert.NotEmpty(json.RootElement.GetProperty("security").EnumerateArray());
        Assert.True(paths.GetProperty("/api/budgets/{id}/approve").TryGetProperty("put", out _));
        Assert.False(paths.TryGetProperty("/api/budgets", out _));
        Assert.True(json.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _));
    }

    [Fact]
    public async Task Queries_require_a_valid_signed_unexpired_JWT_and_authorized_order_or_role()
    {
        using var factory = new Factory(); using var client = factory.Client(); var path = $"/api/budgets/{factory.State.Budget.Id}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(role: "BillingOperator"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(order: factory.State.Budget.ServiceOrderId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(order: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(role: "BillingOperator", secret: "a-different-test-secret-32-bytes-long"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(role: "BillingOperator", expired: true));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Email_GET_never_mutates_and_POST_requires_CSRF_then_records_one_decision()
    {
        using var factory = new Factory(); using var client = factory.Client(); var id = factory.State.Budget.Id;
        var token = factory.ApprovalToken();
        var page = await client.GetAsync($"/api/budgets/{id}/approve?token={token}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode); Assert.Equal(BudgetStatus.Pending, factory.State.Budget.Status); Assert.Empty(factory.State.Events);
        Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
        var form = await page.Content.ReadAsStringAsync();
        var csrf = Regex.Match(form, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(csrf);
        var blocked = await client.PostAsync($"/api/budgets/{id}/approve", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode); Assert.Equal(BudgetStatus.Pending, factory.State.Budget.Status);
        var accepted = await client.PostAsync($"/api/budgets/{id}/approve", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["token"] = token, ["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf) }));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode); Assert.Equal(BudgetStatus.Approved, factory.State.Budget.Status);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/budgets/{id}/approve", new ReviewRequest(token))).StatusCode);
        Assert.Single(factory.State.Events.OfType<BudgetApproved>());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/budgets/{id}/reject", new ReviewRequest(token, "Outra decisão"))).StatusCode);
    }

    [Fact]
    public async Task Tampered_approval_token_does_not_change_state_and_no_REST_creation_route_exists()
    {
        using var factory = new Factory(); using var client = factory.Client(); var id = factory.State.Budget.Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/budgets/{id}/approve", new ReviewRequest("invalid-token"))).StatusCode);
        Assert.Equal(BudgetStatus.Pending, factory.State.Budget.Status); Assert.Empty(factory.State.Events);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/budgets", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/payments", new { })).StatusCode);
    }
}
