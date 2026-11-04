using GearFlow.Billing.Adapters;
using GearFlow.Billing.Adapters.Security;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GearFlow.Billing.IntegrationTests;

// Component integration: real DI, dispatcher and cryptography; no Mongo/RabbitMQ substitute.
public class SecurityAndDispatchTests
{
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-11-02T12:00:00Z"); }
    private sealed record Ping(string Text) : ICommand<string>;
    private sealed record Echo(string Text) : IQuery<string>;
    private sealed class PingHandler(List<string> calls) : ICommandHandler<Ping, string>
    {
        public Task<string> HandleAsync(Ping command, CancellationToken ct) { calls.Add("handler"); return Task.FromResult(command.Text); }
    }
    private sealed class EchoHandler : IQueryHandler<Echo, string>
    {
        public Task<string> HandleAsync(Echo query, CancellationToken ct) => Task.FromResult(query.Text);
    }
    private sealed class Validator(List<string> calls) : ICommandValidator<Ping>
    {
        public Task ValidateAsync(Ping command, CancellationToken ct)
        {
            calls.Add("validate");
            if (string.IsNullOrEmpty(command.Text)) throw new ArgumentException("empty");
            return Task.CompletedTask;
        }
    }
    private sealed class Log(List<string> calls) : IUseCaseLog
    {
        public void Completed(string useCase, TimeSpan elapsed, bool success) => calls.Add(success ? "success" : "failure");
    }
    private static IConfiguration Configuration(string secret = "a-32-byte-minimum-test-secret-123456") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:ApprovalTokenSecret"] = secret,
            ["Billing:PublicBaseUrl"] = "http://localhost:5080"
        }).Build();

    [Fact]
    public async Task Dispatcher_runs_validation_handler_and_logging_in_order_and_supports_queries()
    {
        var calls = new List<string>();
        var services = new ServiceCollection().AddLogging().AddBillingCore(Configuration());
        services.AddSingleton<ICommandHandler<Ping, string>>(new PingHandler(calls));
        services.AddSingleton<IQueryHandler<Echo, string>, EchoHandler>();
        services.AddSingleton<ICommandValidator<Ping>>(new Validator(calls));
        services.AddSingleton<IUseCaseLog>(new Log(calls));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        Assert.Equal("hello", await dispatcher.Send(new Ping("hello")));
        Assert.Equal(new[] { "validate", "handler", "success" }, calls);
        Assert.Equal("query", await dispatcher.Query(new Echo("query")));
        calls.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.Send(new Ping("")));
        Assert.Equal(new[] { "validate", "failure" }, calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.Send(new Ping("hello"), new CancellationToken(true)));
    }

    [Fact]
    public void Hmac_is_bound_to_budget_expiry_and_purpose_and_detects_tampering()
    {
        var clock = new Clock();
        var options = Options.Create(new ApprovalOptions { ApprovalTokenSecret = "a-32-byte-minimum-test-secret-123456" });
        var tokens = new HmacApprovalTokenService(options, clock);
        var id = Guid.NewGuid();
        var expiry = clock.UtcNow.AddHours(1);
        var token = tokens.Create(id, expiry);
        Assert.True(tokens.IsValid(token, id, expiry));
        Assert.False(tokens.IsValid(token, Guid.NewGuid(), expiry));
        Assert.False(tokens.IsValid(token, id, expiry.AddSeconds(1)));
        Assert.False(tokens.IsValid(token[..^1] + (token[^1] == '0' ? "1" : "0"), id, expiry));
        Assert.False(tokens.IsValid("invalid", id, expiry));
        Assert.False(tokens.IsValid("1." + new string('z', 64), id, expiry));
        clock.UtcNow = expiry;
        Assert.False(tokens.IsValid(token, id, expiry));
    }

    [Fact]
    public void Missing_or_short_secret_fails_options_validation()
    {
        var services = new ServiceCollection().AddLogging().AddBillingCore(Configuration("short"));
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IApprovalTokenService>());
    }
}
