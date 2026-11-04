using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace GearFlow.Billing.Adapters.Messaging;

public interface IConfirmedPublisher
{
    Task PublishAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, string messageId,
        string correlationId, IDictionary<string, object?>? headers, CancellationToken ct);
}

public sealed class RabbitPublisher(IOptions<RabbitOptions> options) : IConfirmedPublisher
{
    public async Task PublishAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, string messageId,
        string correlationId, IDictionary<string, object?>? headers, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var connection = await options.Value.Factory().CreateConnectionAsync(timeout.Token);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), timeout.Token);
        var properties = new BasicProperties { Persistent = true, ContentType = "application/json", MessageId = messageId,
            CorrelationId = correlationId, Headers = headers };
        // Tracking turns both nack and mandatory Basic.Return into PublishException.
        await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, body, timeout.Token);
    }
}
