using System.Text;
using System.Text.Json;
using GearFlow.Billing.Adapters.Messaging;
using GearFlow.Billing.Application.Budgets;
using GearFlow.Billing.Application.Events;

namespace GearFlow.Billing.IntegrationTests;

public class WireContractTests
{
    private static WireEnvelope Envelope(JsonElement payload) => new(Guid.NewGuid(), "GenerateBudget", 1,
        DateTimeOffset.Parse("2026-11-03T12:00:00Z"), Guid.Parse("10000000-0000-4000-8000-000000000001"),
        Guid.Parse("10000000-0000-4000-8000-000000000001"), null, "gearflow-os-service", payload);
    private static WireEnvelope Message() => Envelope(JsonSerializer.SerializeToElement(new
    {
        customer = new { name = "Ana", email = "ana@example.com" }, expiresOn = "2026-11-04T12:00:00Z",
        items = new[] { new { referenceId = Guid.NewGuid(), type = "Job", description = "Serviço", unitPrice = 350m, quantity = 1m } }
    }));
    private static byte[] Bytes(WireEnvelope message) => JsonSerializer.SerializeToUtf8Bytes(message, WireContracts.Json);

    [Fact]
    public void Valid_command_maps_to_internal_snapshot_with_causation_and_correlation()
    {
        var message = Message();
        var parsed = WireContracts.Parse(Bytes(message), "billing.GenerateBudget", message.MessageId.ToString("D"), message.ServiceOrderId.ToString("D"));
        var command = Assert.IsType<GenerateBudgetCommand>(WireContracts.Command(parsed));
        Assert.Equal(message.ServiceOrderId, command.ServiceOrderId); Assert.Equal(message.MessageId, command.CausationId);
        Assert.Equal(350, Assert.Single(command.Items).Subtotal.Amount);
    }

    [Theory]
    [InlineData("version")] [InlineData("correlation")] [InlineData("id")] [InlineData("type")] [InlineData("producer")] [InlineData("route")]
    public void Invalid_envelope_cannot_reach_domain(string mismatch)
    {
        var message = Message(); var route = "billing.GenerateBudget";
        message = mismatch switch
        {
            "version" => message with { SchemaVersion = 2 }, "correlation" => message with { CorrelationId = Guid.NewGuid() },
            "id" => message with { MessageId = Guid.Empty }, "type" => message with { MessageType = "0" },
            "producer" => message with { Producer = "gearflow-inventory-service" }, _ => message
        };
        if (mismatch == "route") route = "billing.RequestPayment";
        Assert.Throws<DeliveryException>(() => WireContracts.Parse(Bytes(message), route, message.MessageId.ToString("D"), message.ServiceOrderId.ToString("D")));
    }

    [Fact]
    public void Duplicate_JSON_fields_are_rejected_before_inbox_claim()
    {
        var message = Message();
        var json = Encoding.UTF8.GetString(Bytes(message)).Replace("\"schemaVersion\":1", "\"schemaVersion\":2,\"schemaVersion\":1");
        Assert.Equal("message.duplicate_field", Assert.Throws<DeliveryException>(() => WireContracts.Parse(Encoding.UTF8.GetBytes(json),
            "billing.GenerateBudget", message.MessageId.ToString("D"), message.ServiceOrderId.ToString("D"))).Code);
    }

    [Fact]
    public void Inbox_digest_ignores_property_order_and_numeric_format_but_detects_changes()
    {
        var a = Envelope(JsonDocument.Parse("{\"amount\":350.00,\"currency\":\"BRL\"}").RootElement.Clone());
        var b = a with { Payload = JsonDocument.Parse("{\"currency\":\"BRL\",\"amount\":350}").RootElement.Clone() };
        Assert.Equal(WireContracts.Digest(a), WireContracts.Digest(b));
        Assert.NotEqual(WireContracts.Digest(a), WireContracts.Digest(b with { Payload = JsonSerializer.SerializeToElement(new { amount = 351, currency = "BRL" }) }));
    }

    [Fact]
    public void External_event_has_exact_v1_name_and_business_payload_without_internal_fields()
    {
        var message = new BudgetApproved(Guid.NewGuid(), Guid.NewGuid(), null, DateTimeOffset.UtcNow, Guid.NewGuid(), 350, "BRL");
        using var json = JsonDocument.Parse(WireContracts.Event(message)); var root = json.RootElement;
        Assert.Equal("gearflow-budget-service.BudgetApproved", root.GetProperty("messageType").GetString());
        Assert.Equal(message.ServiceOrderId, root.GetProperty("correlationId").GetGuid());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("causationId").ValueKind);
        var payload = root.GetProperty("payload"); Assert.True(payload.TryGetProperty("approvedOn", out _));
        Assert.False(payload.TryGetProperty("messageId", out _)); Assert.False(payload.TryGetProperty("customer", out _));
    }
}
