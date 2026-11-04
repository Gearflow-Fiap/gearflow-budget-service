using System.Text.Json;
using GearFlow.Billing.Adapters.Messaging;
using GearFlow.Billing.Application.Events;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace GearFlow.Billing.IntegrationTests;

public sealed class RabbitFactAttribute : FactAttribute
{
    public RabbitFactAttribute()
    {
        if (!TestInfrastructure.Containers && string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("GEARFLOW_TEST_RABBIT")))
            Skip = "Requires GEARFLOW_TEST_RABBIT or GEARFLOW_USE_TESTCONTAINERS=true.";
    }
}

public class RabbitInfrastructureTests
{
    [RabbitFact]
    public async Task Real_confirmed_publish_preserves_properties_and_mandatory_return_is_failure()
    {
        await using var infrastructure = await TestInfrastructure.RabbitAsync(); var settings = infrastructure.Value;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = await settings.Factory().CreateConnectionAsync(deadline.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: deadline.Token);
        var exchange = "billing-test-" + Guid.NewGuid().ToString("N"); var queue = exchange + ".queue";
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, true, false, cancellationToken: deadline.Token);
        await channel.QueueDeclareAsync(queue, true, false, false, cancellationToken: deadline.Token);
        await channel.QueueBindAsync(queue, exchange, "gearflow-budget-service.BudgetGenerated", cancellationToken: deadline.Token);
        try
        {
            var message = new BudgetGenerated(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), 350, "BRL", DateTimeOffset.UtcNow.AddHours(1));
            var publisher = new RabbitPublisher(Options.Create(settings));
            await publisher.PublishAsync(exchange, "gearflow-budget-service.BudgetGenerated", WireContracts.Event(message),
                message.MessageId.ToString("D"), message.ServiceOrderId.ToString("D"), null, deadline.Token);
            var received = await channel.BasicGetAsync(queue, false, deadline.Token);
            Assert.NotNull(received); Assert.True(received.BasicProperties.Persistent);
            Assert.Equal(message.MessageId.ToString("D"), received.BasicProperties.MessageId);
            Assert.Equal(message.ServiceOrderId.ToString("D"), received.BasicProperties.CorrelationId);
            using var json = JsonDocument.Parse(received.Body);
            Assert.Equal(message.MessageId, json.RootElement.GetProperty("messageId").GetGuid());
            await channel.BasicAckAsync(received.DeliveryTag, false, deadline.Token);
            await Assert.ThrowsAsync<PublishReturnException>(() => publisher.PublishAsync(exchange, "unroutable.key", WireContracts.Event(message),
                message.MessageId.ToString("D"), message.ServiceOrderId.ToString("D"), null, deadline.Token));
        }
        finally
        {
            await channel.QueueDeleteAsync(queue, false, false, cancellationToken: deadline.Token);
            await channel.ExchangeDeleteAsync(exchange, cancellationToken: deadline.Token);
        }
    }
}
