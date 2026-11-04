using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GearFlow.Billing.Adapters.PaymentGateway.MercadoPago;
using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using Microsoft.Extensions.Options;

namespace GearFlow.Billing.IntegrationTests;

public class MercadoPagoGatewayTests
{
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-11-02T12:00:00Z"); }
    private sealed class BlockingTransport : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++; await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Response("{}");
        }
    }
    private sealed record Request(string Method, string Path, string? Key, string? Authorization, string Body);
    private sealed class Transport(Func<Request, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var captured = new Request(request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Headers.TryGetValues("X-Idempotency-Key", out var keys) ? keys.Single() : null, request.Headers.Authorization?.ToString(), body);
            Requests.Add(captured); return response(captured, Requests.Count);
        }
    }
    private static readonly Guid BudgetId = Guid.Parse("01234567-0123-4123-8123-012345678901");
    private static readonly Guid OperationId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static string Order(string status = "action_required", string detail = "waiting_transfer", string currency = "BRL", bool refund = false) =>
        JsonSerializer.Serialize(new
        {
            id = "ORD123", type = "online", external_reference = BudgetId.ToString("D"), total_amount = "350.00",
            currency_id = currency, status, status_detail = detail, created_date = "2026-11-02T12:00:00Z",
            transactions = new
            {
                payments = new[] { new { id = "PAY123", amount = "350.00", status, status_detail = detail,
                    expiration_time = "PT30M", payment_method = new { id = "pix", type = "bank_transfer", qr_code = "pix-code", ticket_url = "https://example.com/pix" } } },
                refunds = refund ? new[] { new { id = "REF123", amount = "350.00", status = "processed" } } : []
            }
        });
    private static HttpResponseMessage Response(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static MercadoPagoOptions Settings() => new() { AccessToken = "test-secret-access-token", WebhookSecret = "test-webhook-secret-32-bytes-long", RetryDelayMilliseconds = 0 };
    private static MercadoPagoGateway Gateway(Transport transport, Clock? clock = null, MercadoPagoOptions? settings = null)
    {
        clock ??= new Clock(); var opts = Options.Create(settings ?? Settings());
        return new(new HttpClient(transport) { BaseAddress = new Uri("https://api.mercadopago.com/"), Timeout = Timeout.InfiniteTimeSpan }, opts, new GatewayCircuit(clock, opts), clock);
    }
    private static ChargeRequest Charge() => new(Guid.NewGuid(), BudgetId, new Money(350),
        new Customer("Ana", new EmailAddress("ana@example.com")), OperationId,
        DateTimeOffset.Parse("2026-11-02T12:30:00Z"), DateTimeOffset.Parse("2026-11-02T12:00:00Z"));

    [Fact]
    public async Task Creates_Pix_with_documented_payload_invariant_amount_and_stable_key()
    {
        var transport = new Transport((_, _) => Response(Order()));
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
            var result = await Gateway(transport).CreateAsync(Charge(), default);
            Assert.Equal(GatewayStatus.Pending, result.Status); Assert.Equal("pix-code", result.QrCode);
            Assert.Equal(DateTimeOffset.Parse("2026-11-02T12:30:00Z"), result.ExpiresOn);
            var request = Assert.Single(transport.Requests);
            Assert.Equal("POST", request.Method); Assert.Equal("/v1/orders", request.Path);
            Assert.Equal(OperationId.ToString("D"), request.Key); Assert.Equal("Bearer test-secret-access-token", request.Authorization);
            using var json = JsonDocument.Parse(request.Body); var body = json.RootElement;
            Assert.Equal("automatic", body.GetProperty("processing_mode").GetString());
            Assert.Equal("350.00", body.GetProperty("total_amount").GetString());
            Assert.False(body.GetProperty("payer").TryGetProperty("first_name", out _));
            Assert.False(body.TryGetProperty("expiration_time", out _));
            Assert.Equal(BudgetId.ToString("D"), body.GetProperty("external_reference").GetString());
            var payment = body.GetProperty("transactions").GetProperty("payments")[0];
            Assert.Equal("PT30M", payment.GetProperty("expiration_time").GetString());
            Assert.Equal("pix", payment.GetProperty("payment_method").GetProperty("id").GetString());
            Assert.Equal("bank_transfer", payment.GetProperty("payment_method").GetProperty("type").GetString());
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public async Task Test_mode_adds_documented_APRO_without_modifying_customer_snapshot()
    {
        var transport = new Transport((_, _) => Response(Order()));
        var settings = Settings(); settings.TestMode = true;
        var charge = Charge();
        await Gateway(transport, settings: settings).CreateAsync(charge, default);
        using var json = JsonDocument.Parse(Assert.Single(transport.Requests).Body);
        Assert.Equal("APRO", json.RootElement.GetProperty("payer").GetProperty("first_name").GetString());
        Assert.Equal(charge.Customer.Email.Value, json.RootElement.GetProperty("payer").GetProperty("email").GetString());
        Assert.Equal("Ana", charge.Customer.Name);
    }

    [Fact]
    public async Task Retries_transient_errors_with_identical_body_and_idempotency_key()
    {
        var transport = new Transport((_, count) => count < 4 ? Response("unavailable", HttpStatusCode.ServiceUnavailable) : Response(Order()));
        await Gateway(transport).CreateAsync(Charge(), default);
        Assert.Equal(4, transport.Requests.Count);
        Assert.Single(transport.Requests.Select(r => r.Body).Distinct());
        Assert.All(transport.Requests, request => Assert.Equal(OperationId.ToString("D"), request.Key));
    }

    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(409)] [InlineData(422)]
    public async Task Does_not_retry_definitive_HTTP_errors_or_expose_provider_body(int status)
    {
        var transport = new Transport((_, _) => Response("sensitive-provider-body", (HttpStatusCode)status));
        var exception = await Assert.ThrowsAsync<GatewayException>(() => Gateway(transport).CreateAsync(Charge(), default));
        Assert.False(exception.Retryable); Assert.DoesNotContain("sensitive", exception.Message); Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Respects_429_retry_after_and_opens_circuit_after_exhausted_calls()
    {
        var transport = new Transport((_, _) =>
        {
            var response = Response("rate-limited", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero); return response;
        });
        var settings = Settings(); settings.CircuitFailures = 1;
        var gateway = Gateway(transport, settings: settings);
        Assert.Equal("gateway.unavailable", (await Assert.ThrowsAsync<GatewayException>(() => gateway.GetAsync("ORD123", default))).Code);
        Assert.Equal(4, transport.Requests.Count);
        Assert.Equal("gateway.circuit_open", (await Assert.ThrowsAsync<GatewayException>(() => gateway.GetAsync("ORD123", default))).Code);
        Assert.Equal(4, transport.Requests.Count);
    }

    [Theory]
    [InlineData("processed", "accredited", GatewayStatus.Paid)]
    [InlineData("failed", "rejected", GatewayStatus.Rejected)]
    [InlineData("canceled", "canceled", GatewayStatus.Canceled)]
    [InlineData("action_required", "waiting_transfer", GatewayStatus.Pending)]
    [InlineData("processing", "processing", GatewayStatus.Pending)]
    [InlineData("future_state", "unknown", GatewayStatus.Unknown)]
    public async Task Maps_states_without_inventing_a_failure(string status, string detail, GatewayStatus expected)
    {
        var gateway = Gateway(new Transport((_, _) => Response(Order(status, detail))));
        Assert.Equal(expected, (await gateway.GetAsync("ORD123", default)).Status);
    }

    [Fact]
    public async Task Refund_posts_empty_body_and_confirms_through_GET_full_amount()
    {
        var transport = new Transport((request, count) => request.Method == "POST" ? Response("{}", HttpStatusCode.Created)
            : Response(count > 1 ? Order("processed", "refunded", refund: true) : Order("processed", "accredited")));
        var result = await Gateway(transport).RefundAsync("ORD123", new Money(350), OperationId, default);
        Assert.Equal(GatewayStatus.Refunded, result.Status); Assert.Equal("REF123", result.RefundId);
        var refund = Assert.Single(transport.Requests, r => r.Method == "POST");
        Assert.Equal("/v1/orders/ORD123/refund", refund.Path); Assert.Equal("{}", refund.Body);
        Assert.Equal(OperationId.ToString("D"), refund.Key);
    }

    [Fact]
    public async Task Refund_in_processing_is_not_announced_as_refunded()
    {
        var json = Order("processed", "refunded", refund: true).Replace("\"status\":\"processed\"}", "\"status\":\"processing\"}");
        var gateway = Gateway(new Transport((request, _) => request.Method == "POST" ? Response("{}") : Response(json)));
        Assert.Equal(GatewayStatus.Unknown, (await gateway.RefundAsync("ORD123", new Money(350), OperationId, default)).Status);
    }

    [Fact]
    public async Task Cancel_uses_separate_operation_key_and_reads_final_state()
    {
        var transport = new Transport((request, _) => Response(request.Method == "POST" ? "{}" : Order("canceled", "canceled")));
        var result = await Gateway(transport).CancelAsync("ORD123", OperationId, default);
        Assert.Equal(GatewayStatus.Canceled, result.Status);
        Assert.Equal("/v1/orders/ORD123/cancel", transport.Requests[0].Path);
        Assert.Equal(OperationId.ToString("D"), transport.Requests[0].Key);
    }

    [Theory]
    [InlineData("02-get-pending.json", GatewayStatus.Pending)]
    [InlineData("02-get-paid.json", GatewayStatus.Paid)]
    [InlineData("02-get-canceled.json", GatewayStatus.Canceled)]
    [InlineData("02-get-refunded.json", GatewayStatus.Refunded)]
    [InlineData("93-rejected.json", GatewayStatus.Rejected)]
    public async Task Versioned_WireMock_order_fixtures_match_gateway_contract(string file, GatewayStatus expected)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
        var body = fixture.RootElement.GetProperty("response").GetProperty("jsonBody");
        var gateway = Gateway(new Transport((_, _) => Response(body.GetRawText())));
        Assert.Equal(expected, (await gateway.GetAsync(body.GetProperty("id").GetString()!, default)).Status);
    }

    [Fact]
    public async Task Search_uses_mandatory_dates_data_array_then_fetches_full_order()
    {
        var transport = new Transport((request, _) => Response(request.Path.Contains('?') ? "{\"data\":[{\"id\":\"ORD123\"}],\"paging\":{\"total\":1}}" : Order()));
        Assert.NotNull(await Gateway(transport).FindByBudgetAsync(BudgetId, Charge().CreatedOn, default));
        var path = transport.Requests[0].Path;
        Assert.StartsWith("/v1/orders?begin_date=", path); Assert.Contains("end_date=", path); Assert.Contains("external_reference=", path);
        Assert.Equal("/v1/orders/ORD123", transport.Requests[1].Path);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"0\"")]
    public async Task Search_accepts_numeric_or_string_paging_total_returned_by_provider(string total)
    {
        var transport = new Transport((_, _) => Response("{\"data\":[],\"paging\":{\"total\":" + total + "}}"));
        Assert.Null(await Gateway(transport).FindByBudgetAsync(BudgetId, Charge().CreatedOn, default));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"invalid\"")]
    [InlineData("\"1.5\"")]
    [InlineData("{}")]
    public async Task Search_rejects_unrecognized_paging_totals_as_provider_contract_errors(string total)
    {
        var transport = new Transport((_, _) => Response("{\"data\":[],\"paging\":{\"total\":" + total + "}}"));
        var error = await Assert.ThrowsAsync<GatewayException>(() => Gateway(transport).FindByBudgetAsync(BudgetId, Charge().CreatedOn, default));
        Assert.Equal("gateway.invalid_response", error.Code);
    }

    [Fact]
    public async Task Search_with_string_total_still_refuses_remote_duplicates()
    {
        var transport = new Transport((_, _) => Response("{\"data\":[],\"paging\":{\"total\":\"2\"}}"));
        var error = await Assert.ThrowsAsync<GatewayException>(() => Gateway(transport).FindByBudgetAsync(BudgetId, Charge().CreatedOn, default));
        Assert.Equal("gateway.duplicate_orders", error.Code);
    }

    [Fact]
    public async Task Search_refuses_multiple_remote_charges()
    {
        var gateway = Gateway(new Transport((_, _) => Response("{\"data\":[{\"id\":\"ORD1\"},{\"id\":\"ORD2\"}]}")));
        Assert.Equal("gateway.duplicate_orders", (await Assert.ThrowsAsync<GatewayException>(() => gateway.FindByBudgetAsync(BudgetId, Charge().CreatedOn, default))).Code);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("{}")]
    public async Task Invalid_response_is_uncertain_and_never_a_refusal(string json)
    {
        var gateway = Gateway(new Transport((_, _) => Response(json)));
        Assert.Equal("gateway.invalid_response", (await Assert.ThrowsAsync<GatewayException>(() => gateway.GetAsync("ORD123", default))).Code);
    }

    [Fact]
    public async Task Rejects_foreign_currency_wrong_reference_or_different_order()
    {
        await Assert.ThrowsAsync<GatewayException>(() => Gateway(new Transport((_, _) => Response(Order(currency: "USD")))).CreateAsync(Charge(), default));
        await Assert.ThrowsAsync<GatewayException>(() => Gateway(new Transport((_, _) => Response(Order().Replace(BudgetId.ToString(), Guid.NewGuid().ToString())))).CreateAsync(Charge(), default));
        await Assert.ThrowsAsync<GatewayException>(() => Gateway(new Transport((_, _) => Response(Order()))).GetAsync("ORD999", default));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        var transport = new Transport((_, _) => Response(Order()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gateway(transport).CreateAsync(Charge(), new CancellationToken(true)));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Attempt_timeout_retries_only_same_operation_and_returns_uncertainty()
    {
        var transport = new BlockingTransport(); var settings = Settings(); settings.AttemptTimeoutSeconds = 1; settings.MaxAttempts = 2;
        var clock = new Clock(); var options = Options.Create(settings);
        var gateway = new MercadoPagoGateway(new HttpClient(transport) { BaseAddress = new Uri("https://api.mercadopago.com/"), Timeout = Timeout.InfiniteTimeSpan },
            options, new GatewayCircuit(clock, options), clock);
        var failure = await Assert.ThrowsAsync<GatewayException>(() => gateway.CreateAsync(Charge(), default));
        Assert.Equal("gateway.unavailable", failure.Code); Assert.True(failure.Retryable); Assert.Equal(2, transport.Attempts);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Webhook_signature_accepts_seconds_and_milliseconds_binds_query_and_rejects_tampering(bool milliseconds)
    {
        var clock = new Clock(); var options = Options.Create(Settings()); var validator = new MercadoPagoWebhookValidator(options, clock);
        var timestamp = (milliseconds ? clock.UtcNow.ToUnixTimeMilliseconds() : clock.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture);
        var manifest = $"id:ord123;request-id:req-123;ts:{timestamp};";
        var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.WebhookSecret), Encoding.UTF8.GetBytes(manifest)));
        var signature = $"ts={timestamp},v1={hash}";
        Assert.Equal("ORD123", validator.Validate("ORD123", "req-123", signature)!.OrderId);
        Assert.Null(validator.Validate("ORD999", "req-123", signature));
        Assert.Null(validator.Validate("ORD123", "req-999", signature));
        Assert.Null(validator.Validate("ORD123", "req-123", signature + $",ts={timestamp}"));
        clock.UtcNow = clock.UtcNow.AddMinutes(6);
        Assert.Null(validator.Validate("ORD123", "req-123", signature));
    }
}
