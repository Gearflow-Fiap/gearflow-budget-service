using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace GearFlow.Billing.Adapters.Messaging;

public sealed class RabbitTopology(IOptions<RabbitOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var settings = options.Value;
        await using var connection = await settings.Factory().CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        foreach (var exchange in new[] { settings.CommandsExchange, settings.EventsExchange, settings.DeadLetterExchange })
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, true, false, cancellationToken: ct);
        foreach (var queue in new[] { settings.CommandsQueue, settings.CommandsQueue + ".dlq", settings.EventsQueue })
            await channel.QueueDeclareAsync(queue, true, false, false, new Dictionary<string, object?> { ["x-queue-type"] = settings.QueueType }, cancellationToken: ct);
        foreach (var command in new[] { "GenerateBudget", "RequestPayment", "CancelBudget", "RefundPayment" })
            await channel.QueueBindAsync(settings.CommandsQueue, settings.CommandsExchange, $"billing.{command}", cancellationToken: ct);
        await channel.QueueBindAsync(settings.CommandsQueue + ".dlq", settings.DeadLetterExchange, settings.CommandsQueue, cancellationToken: ct);
        await channel.QueueBindAsync(settings.EventsQueue, settings.EventsExchange, "gearflow-budget-service.*", cancellationToken: ct);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
