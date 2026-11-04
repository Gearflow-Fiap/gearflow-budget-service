using System.Text.Json;
using GearFlow.Billing.Adapters.PaymentGateway.MercadoPago;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.Out;

namespace GearFlow.Billing.Api;

public static class PaymentWebhookEndpoint
{
    public static async Task<IResult> Receive(HttpRequest request, MercadoPagoWebhookValidator signatures,
        IPaymentWorkStore queue, IClock clock, CancellationToken ct)
    {
        if (request.Query["data.id"].Count != 1 || request.Headers["x-request-id"].Count != 1 || request.Headers["x-signature"].Count != 1)
            return Results.Unauthorized();
        var verified = signatures.Validate(request.Query["data.id"].ToString(), request.Headers["x-request-id"].ToString(), request.Headers["x-signature"].ToString());
        if (verified is null) return Results.Unauthorized();
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 16 }, ct);
            if (!body.RootElement.TryGetProperty("type", out var type) || type.GetString() != "order"
                || !body.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("id", out var id)
                || !string.Equals(id.GetString(), verified.OrderId, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return Results.BadRequest(); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await queue.EnqueueAsync(verified.DeduplicationKey, verified.OrderId, null, clock.UtcNow, deadline.Token);
            return Results.Ok();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.StatusCode(503); }
        // Persistence failure propagates as 5xx; never acknowledge undurable work.
    }
}
