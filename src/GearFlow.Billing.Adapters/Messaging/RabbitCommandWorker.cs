using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GearFlow.Billing.Adapters.Operations;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace GearFlow.Billing.Adapters.Messaging;

public sealed record DeadLetter(string OriginalExchange, string OriginalRoutingKey, string? MessageId,
    string? CorrelationId, string BodyBase64, string Code, int Attempts, DateTimeOffset FailedOn,
    string? TraceParent, string? TraceState);

public sealed class RabbitCommandWorker(IServiceScopeFactory scopes, MongoInbox inbox, IConfirmedPublisher publisher,
    IOptions<RabbitOptions> options, IClock clock, WorkerHealth health, ILogger<RabbitCommandWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await options.Value.Factory().CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await channel.BasicQosAsync(0, 1, false, stoppingToken);
                var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try { await Process(channel, delivery, stoppingToken); }
                    catch (Exception exception) { failed.TrySetResult(exception); }
                };
                var tag = await channel.BasicConsumeAsync(options.Value.CommandsQueue, false, consumer, stoppingToken);
                while (connection.IsOpen && channel.IsOpen && !failed.Task.IsCompleted && !stoppingToken.IsCancellationRequested)
                { health.Beat("rabbit-consumer"); await Task.WhenAny(failed.Task, Task.Delay(TimeSpan.FromSeconds(3), stoppingToken)); }
                if (channel.IsOpen) await channel.BasicCancelAsync(tag, cancellationToken: stoppingToken);
                if (failed.Task.IsCompleted) throw await failed.Task;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Rabbit consumer reconnecting: {Code}", exception.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
    private async Task Process(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var bytes = delivery.Body.ToArray();
        var trace = Header(delivery.BasicProperties.Headers, "traceparent"); var traceState = Header(delivery.BasicProperties.Headers, "tracestate");
        ActivityContext.TryParse(trace, traceState, true, out var parent);
        var started = WireContracts.Activities.StartActivity("billing.command", ActivityKind.Consumer, parent);
        if (started is null)
        {
            started = new Activity("billing.command").SetIdFormat(ActivityIdFormat.W3C);
            if (parent != default) started.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
            started.TraceStateString = parent.TraceState;
            started.Start();
        }
        using var activity = started;
        InboxLease? lease = null;
        string code = "message.invalid";
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                if (delivery.Exchange != options.Value.CommandsExchange || delivery.BasicProperties.ContentType != "application/json")
                    throw new DeliveryException("message.transport");
                var envelope = WireContracts.Parse(bytes, delivery.RoutingKey, delivery.BasicProperties.MessageId, delivery.BasicProperties.CorrelationId);
                activity?.SetTag("service_order.id", envelope.ServiceOrderId);
                if (lease is null)
                {
                    var claim = await inbox.ClaimAsync(options.Value.CommandsQueue, envelope.MessageId, WireContracts.Digest(envelope),
                        Enum.Parse<IncomingCommandKind>(envelope.MessageType), deadline.Token);
                    if (claim.Status == InboxClaimStatus.Completed) { await channel.BasicAckAsync(delivery.DeliveryTag, false, ct); return; }
                    if (claim.Status == InboxClaimStatus.Conflict) throw new DeliveryException("message.id_conflict");
                    if (claim.Status == InboxClaimStatus.Busy) throw new DeliveryException("inbox.busy", true);
                    lease = claim.Lease;
                }
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CommandReceiver>().ReceiveAsync(envelope, lease, deadline.Token);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                code = exception is DeliveryException rejected ? rejected.Code : exception.GetType().Name;
                if (exception is ConcurrencyException or DuplicateException)
                {
                    if (lease is not null) await inbox.ReleaseAsync(lease, ct);
                    lease = null;
                    if (attempt == 3) throw new DeliveryException("state.concurrent", true);
                }
                if (attempt == 3 && exception is DeliveryException { PreserveSource: true }) throw;
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(new[] { 1, 5, 15 }[attempt]), ct);
            }
        }
        var dead = new DeadLetter(delivery.Exchange, delivery.RoutingKey, delivery.BasicProperties.MessageId,
            delivery.BasicProperties.CorrelationId, Convert.ToBase64String(bytes), code, 4, clock.UtcNow, trace, traceState);
        // If publishing fails, Process throws and the channel closes with source delivery unacknowledged.
        await publisher.PublishAsync(options.Value.DeadLetterExchange, options.Value.CommandsQueue,
            JsonSerializer.SerializeToUtf8Bytes(dead, WireContracts.Json), delivery.BasicProperties.MessageId ?? Guid.NewGuid().ToString("D"),
            delivery.BasicProperties.CorrelationId ?? "", null, ct);
        if (lease is not null) await inbox.ReleaseAsync(lease, ct);
        await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        BillingTelemetry.DeadLetters.Add(1);
        logger.LogWarning("Command moved to DLQ: {Code}", code);
    }
    internal static string? Header(IDictionary<string, object?>? headers, string name) =>
        headers?.TryGetValue(name, out var value) == true ? value switch { byte[] data => Encoding.UTF8.GetString(data), string s => s, _ => null } : null;
}
