using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using GearFlow.Billing.Application.Budgets;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;

namespace GearFlow.Billing.Adapters.Messaging;

// Local v1 transport DTOs implementing docs/rabbitmq.md. Replace with the agreed shared
// package when its repository/feed is supplied; never import these types into the core.
public sealed record WireEnvelope(Guid MessageId, string MessageType, int SchemaVersion, DateTimeOffset OccurredOn,
    Guid CorrelationId, Guid ServiceOrderId, Guid? CausationId, string Producer, JsonElement Payload);
public sealed record CustomerPayload(string Name, string Email);
public sealed record ItemPayload(Guid ReferenceId, string Type, string Description, decimal UnitPrice, decimal Quantity);
public sealed record GeneratePayload(CustomerPayload Customer, ItemPayload[] Items, DateTimeOffset ExpiresOn);
public sealed record RequestPayload(Guid BudgetId, decimal Amount, string Currency);
public sealed record CancelPayload(Guid BudgetId, string Reason);
public sealed record RefundPayload(Guid BudgetId, Guid PaymentId, string Reason);

public static class WireContracts
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 32 };
    private static readonly JsonSerializerOptions EnvelopeJson = new(Json) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    public static readonly ActivitySource Activities = new("GearFlow.Billing");
    public static WireEnvelope Parse(ReadOnlyMemory<byte> body, string routingKey, string? messageId, string? correlationId)
    {
        if (body.Length == 0 || body.Length > 1_048_576) throw new DeliveryException("message.size");
        try
        {
            using var parsed = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateFields(parsed.RootElement);
            var message = JsonSerializer.Deserialize<WireEnvelope>(body.Span, Json) ?? throw new DeliveryException("message.empty");
            if (message.MessageId == Guid.Empty || message.ServiceOrderId == Guid.Empty || message.CorrelationId != message.ServiceOrderId
                || message.SchemaVersion != 1 || message.OccurredOn.Offset != TimeSpan.Zero || message.OccurredOn == default
                || message.Producer != "gearflow-os-service" || message.MessageType is not ("GenerateBudget" or "RequestPayment" or "CancelBudget" or "RefundPayment")
                || !Enum.TryParse<IncomingCommandKind>(message.MessageType, false, out var kind)
                || !Enum.IsDefined(kind) || routingKey != $"billing.{message.MessageType}"
                || messageId != message.MessageId.ToString("D") || correlationId != message.ServiceOrderId.ToString("D")
                || message.Payload.ValueKind != JsonValueKind.Object) throw new DeliveryException("message.envelope");
            return message;
        }
        catch (JsonException) { throw new DeliveryException("message.json"); }
    }
    private static void RejectDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in value.EnumerateObject())
            { if (!seen.Add(field.Name)) throw new DeliveryException("message.duplicate_field"); RejectDuplicateFields(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item);
    }
    public static string Digest(WireEnvelope message)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream))
            Canonical(JsonSerializer.SerializeToElement(message, EnvelopeJson), writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void Canonical(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        { writer.WriteStartObject(); foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Canonical(property.Value, writer); } writer.WriteEndObject(); }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(item, writer); writer.WriteEndArray(); }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) writer.WriteRawValue(number.ToString("G29", CultureInfo.InvariantCulture));
        else value.WriteTo(writer);
    }
    public static object Command(WireEnvelope message) => message.MessageType switch
    {
        "GenerateBudget" => Generate(message),
        "RequestPayment" => Request(message),
        "CancelBudget" => Cancel(message),
        "RefundPayment" => Refund(message),
        _ => throw new DeliveryException("message.type")
    };
    private static T Payload<T>(WireEnvelope message) => message.Payload.Deserialize<T>(Json) ?? throw new DeliveryException("message.payload");
    private static GenerateBudgetCommand Generate(WireEnvelope message)
    {
        var p = Payload<GeneratePayload>(message);
        if (p.Customer is null || p.Items is null || p.Items.Length > 1000 || p.Items.Any(x => x is null)) throw new DeliveryException("message.payload");
        var customer = new Customer(p.Customer.Name, new EmailAddress(p.Customer.Email));
        var items = p.Items.Select(i => new BudgetItem(i.ReferenceId,
            i.Type is "Job" or "Part" or "Consumable" && Enum.TryParse<BudgetItemType>(i.Type, false, out var type) && Enum.IsDefined(type) ? type
                : throw new DomainException("item.type", "Invalid item type."), i.Description, new Money(i.UnitPrice), i.Quantity)).ToArray();
        return new(message.ServiceOrderId, customer, Array.AsReadOnly(items), p.ExpiresOn, message.MessageId);
    }
    private static RequestPaymentCommand Request(WireEnvelope message)
    { var p = Payload<RequestPayload>(message); return new(message.ServiceOrderId, p.BudgetId, new Money(p.Amount, p.Currency), message.MessageId); }
    private static CancelBudgetCommand Cancel(WireEnvelope message)
    { var p = Payload<CancelPayload>(message); return new(message.ServiceOrderId, p.BudgetId, p.Reason, message.MessageId); }
    private static RefundPaymentCommand Refund(WireEnvelope message)
    { var p = Payload<RefundPayload>(message); return new(message.ServiceOrderId, p.BudgetId, p.PaymentId, p.Reason, message.MessageId); }

    public static byte[] Event(IntegrationEvent message)
    {
        object payload = message switch
        {
            BudgetGenerated e => new { e.BudgetId, e.TotalAmount, e.Currency, e.ExpiresOn },
            BudgetGenerationFailed e => new { e.Code, e.Reason },
            BudgetApproved e => new { e.BudgetId, e.TotalAmount, e.Currency, ApprovedOn = e.OccurredOn },
            BudgetRejected e => new { e.BudgetId, RejectedOn = e.OccurredOn, e.Reason },
            BudgetCanceled e => new { e.BudgetId, CanceledOn = e.OccurredOn, e.Reason },
            PaymentRequested e => new { e.BudgetId, e.PaymentId, e.Amount, e.Currency, e.MercadoPagoOrderId, e.ExpiresOn },
            PaymentConfirmed e => new { e.BudgetId, e.PaymentId, e.Amount, e.Currency, e.MercadoPagoOrderId, e.PaidOn },
            PaymentFailed e => new { e.BudgetId, e.PaymentId, e.Code, e.Reason },
            PaymentRefunded e => new { e.BudgetId, e.PaymentId, e.Amount, e.Currency, RefundedOn = e.OccurredOn, e.RefundId },
            _ => throw new DeliveryException("event.type")
        };
        return JsonSerializer.SerializeToUtf8Bytes(new WireEnvelope(message.MessageId,
            $"gearflow-budget-service.{message.GetType().Name}", 1, message.OccurredOn, message.ServiceOrderId,
            message.ServiceOrderId, message.CausationId, "gearflow-budget-service", JsonSerializer.SerializeToElement(payload, Json)), EnvelopeJson);
    }
    public static IntegrationEvent ReadEvent(string type, string payload) => type switch
    {
        nameof(BudgetGenerated) => JsonSerializer.Deserialize<BudgetGenerated>(payload, Json)!,
        nameof(BudgetGenerationFailed) => JsonSerializer.Deserialize<BudgetGenerationFailed>(payload, Json)!,
        nameof(BudgetApproved) => JsonSerializer.Deserialize<BudgetApproved>(payload, Json)!,
        nameof(BudgetRejected) => JsonSerializer.Deserialize<BudgetRejected>(payload, Json)!,
        nameof(BudgetCanceled) => JsonSerializer.Deserialize<BudgetCanceled>(payload, Json)!,
        nameof(PaymentRequested) => JsonSerializer.Deserialize<PaymentRequested>(payload, Json)!,
        nameof(PaymentConfirmed) => JsonSerializer.Deserialize<PaymentConfirmed>(payload, Json)!,
        nameof(PaymentFailed) => JsonSerializer.Deserialize<PaymentFailed>(payload, Json)!,
        nameof(PaymentRefunded) => JsonSerializer.Deserialize<PaymentRefunded>(payload, Json)!,
        _ => throw new DeliveryException("event.type")
    };
}
