using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml;
using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Payments;
using Microsoft.Extensions.Options;

namespace GearFlow.Billing.Adapters.PaymentGateway.MercadoPago;

public sealed class MercadoPagoGateway(HttpClient http, IOptions<MercadoPagoOptions> options,
    GatewayCircuit circuit, IClock clock) : IPaymentGateway
{
    private readonly MercadoPagoOptions settings = options.Value;

    public async Task<GatewayPayment> CreateAsync(ChargeRequest request, CancellationToken ct)
    {
        var validity = request.ExpiresOn - request.CreatedOn;
        if (validity < TimeSpan.FromMinutes(30) || validity > TimeSpan.FromDays(30)
            || request.Amount.Amount <= 0 || request.OperationId == Guid.Empty)
            throw new GatewayException("gateway.request", false, "Invalid Pix creation intent.");
        // Duration is derived from the persisted intent, never the current retry time.
        var amount = request.Amount.Amount.ToString("F2", CultureInfo.InvariantCulture);
        var payer = new Dictionary<string, string> { ["email"] = request.Customer.Email.Value };
        if (settings.TestMode) payer["first_name"] = "APRO";
        var payload = new
        {
            type = "online", processing_mode = "automatic", external_reference = request.BudgetId.ToString("D"),
            total_amount = amount,
            payer,
            transactions = new { payments = new[] { new { amount,
                payment_method = new { id = "pix", type = "bank_transfer" }, expiration_time = XmlConvert.ToString(validity) } } }
        };
        using var response = await SendAsync(HttpMethod.Post, "v1/orders", payload, request.OperationId, ct);
        var result = Map(response.RootElement);
        CheckExpected(result, request.BudgetId, request.Amount);
        return result with { ExpiresOn = result.ExpiresOn ?? request.ExpiresOn };
    }

    public async Task<GatewayPayment> GetAsync(string orderId, CancellationToken ct)
    {
        ValidateId(orderId);
        using var response = await SendAsync(HttpMethod.Get, $"v1/orders/{orderId}", null, null, ct);
        var result = Map(response.RootElement);
        if (result.Evidence.OrderId != orderId) throw InvalidResponse();
        return result;
    }

    public async Task<GatewayPayment?> FindByBudgetAsync(Guid budgetId, DateTimeOffset createdOn, CancellationToken ct)
    {
        if (budgetId == Guid.Empty || createdOn.Offset != TimeSpan.Zero || createdOn > clock.UtcNow) throw InvalidResponse();
        // Official search endpoint is /v1/orders, with mandatory date range and data[] response.
        var start = Uri.EscapeDataString(createdOn.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture));
        var end = Uri.EscapeDataString(clock.UtcNow.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture));
        using var response = await SendAsync(HttpMethod.Get,
            $"v1/orders?begin_date={start}&end_date={end}&external_reference={budgetId:D}&type=online&page=1&page_size=2", null, null, ct);
        var root = response.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        // Do not pick an arbitrary charge if remote duplicates exist.
        if (data.GetArrayLength() > 1 || (root.TryGetProperty("paging", out var paging)
            && paging.TryGetProperty("total", out var total) && SearchTotal(total) > 1))
            throw new GatewayException("gateway.duplicate_orders", false, "Multiple provider orders require reconciliation.");
        if (data.GetArrayLength() == 0) return null;
        var result = await GetAsync(Text(data[0], "id") ?? throw InvalidResponse(), ct);
        if (result.Evidence.ExternalReference != budgetId) throw InvalidResponse();
        return result;
    }

    public async Task<GatewayPayment> CancelAsync(string orderId, Guid operationId, CancellationToken ct)
    {
        ValidateId(orderId);
        using var response = await SendAsync(HttpMethod.Post, $"v1/orders/{orderId}/cancel", new { }, operationId, ct);
        return await GetAsync(orderId, ct);
    }

    public async Task<GatewayPayment> RefundAsync(string orderId, Money amount, Guid operationId, CancellationToken ct)
    {
        ValidateId(orderId);
        var existing = await GetAsync(orderId, ct);
        if (existing.Evidence.Amount != amount) throw new GatewayException("gateway.amount", false, "Refund amount does not match the order.");
        if (existing.Status == GatewayStatus.Refunded) return existing;
        // Empty body requests FULL refund. Supplying transactions/amount requests a partial refund.
        using var response = await SendAsync(HttpMethod.Post, $"v1/orders/{orderId}/refund", new { }, operationId, ct);
        return await GetAsync(orderId, ct);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, Guid? key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (method == HttpMethod.Post && (key is null || key == Guid.Empty))
            throw new GatewayException("gateway.idempotency", false, "A stable operation identity is required.");
        circuit.Check();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(settings.TotalTimeoutSeconds));
        try
        {
            for (var attempt = 0; attempt < settings.MaxAttempts; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.AttemptTimeoutSeconds));
                TimeSpan? retryAfter = null;
                try
                {
                    using var message = new HttpRequestMessage(method, path);
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
                    message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    if (key is not null) message.Headers.Add("X-Idempotency-Key", key.Value.ToString("D"));
                    if (body is not null) message.Content = JsonContent.Create(body);
                    using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        // Bounded read: never log provider bodies (tokens, Pix and customer data).
                        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                        using var content = new MemoryStream();
                        var buffer = new byte[8192];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                        {
                            if (content.Length + read > 1_048_576) throw InvalidResponse();
                            content.Write(buffer, 0, read);
                        }
                        var document = content.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(content.ToArray());
                        circuit.Success();
                        return document;
                    }
                    if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                        throw new GatewayException($"gateway.http_{(int)response.StatusCode}", false, "Provider rejected the request; consult the operation before repeating it.");
                    retryAfter = response.Headers.RetryAfter?.Delta;
                    if (response.Headers.RetryAfter?.Date is { } date) retryAfter = date - clock.UtcNow;
                }
                catch (OperationCanceledException) when (!budget.IsCancellationRequested) { }
                catch (HttpRequestException) { }
                catch (JsonException) { throw InvalidResponse(); }
                if (attempt + 1 == settings.MaxAttempts) break;
                var delay = retryAfter ?? TimeSpan.FromMilliseconds(settings.RetryDelayMilliseconds * Math.Pow(2, attempt) + Random.Shared.Next(0, settings.RetryDelayMilliseconds + 1));
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                await Task.Delay(delay, budget.Token);
            }
            circuit.Failure();
            throw new GatewayException("gateway.unavailable", true, "Provider result is uncertain; reuse the persisted operation identity.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            circuit.Failure();
            throw new GatewayException("gateway.timeout", true, "Provider result is uncertain; reconcile before creating another charge.");
        }
    }

    internal static GatewayPayment Map(JsonElement root)
    {
        try
        {
            var id = Text(root, "id") ?? throw InvalidResponse(); ValidateId(id);
            if (!Guid.TryParse(Text(root, "external_reference"), out var reference) || reference == Guid.Empty) throw InvalidResponse();
            var amount = Amount(root, "total_amount");
            var currency = Text(root, "currency_id");
            if ((currency is not null && currency != "BRL") || (currency is null && Text(root, "country_code") != "BRA")
                || Text(root, "type") != "online") throw InvalidResponse();
            var status = Text(root, "status"); var detail = Text(root, "status_detail");
            JsonElement? payment = null; JsonElement? method = null;
            if (root.TryGetProperty("transactions", out var transactions)
                && transactions.TryGetProperty("payments", out var payments) && payments.GetArrayLength() > 0)
            {
                if (payments.GetArrayLength() != 1 || Amount(payments[0], "amount") != amount) throw InvalidResponse();
                payment = payments[0];
                if (payment.Value.TryGetProperty("payment_method", out var pm))
                {
                    if (Text(pm, "id") != "pix" || Text(pm, "type") != "bank_transfer") throw InvalidResponse();
                    method = pm;
                }
            }
            var localStatus = (status, detail) switch
            {
                ("processed", "accredited") when payment is not null && method is not null && Text(payment.Value, "status") == "processed"
                    && Text(payment.Value, "status_detail") == "accredited" => GatewayStatus.Paid,
                ("failed", _) => GatewayStatus.Rejected,
                ("canceled", _) or ("cancelled", _) or ("expired", _) => GatewayStatus.Canceled,
                ("created", _) or ("processing", _) or ("action_required", _) => GatewayStatus.Pending,
                _ => GatewayStatus.Unknown
            };
            string? refundId = null;
            if (localStatus == GatewayStatus.Paid
                && ((Text(root, "total_paid_amount") is not null && Amount(root, "total_paid_amount") != amount)
                    || (payment is { } paid && Text(paid, "paid_amount") is not null && Amount(paid, "paid_amount") != amount)))
                throw InvalidResponse();
            if (status == "refunded" || detail == "refunded")
            {
                localStatus = GatewayStatus.Unknown;
                if (transactions.ValueKind == JsonValueKind.Object && transactions.TryGetProperty("refunds", out var refunds)
                    && refunds.GetArrayLength() == 1 && Text(refunds[0], "status") == "processed"
                    && Amount(refunds[0], "amount") == amount && Text(refunds[0], "id") is { } rid)
                { localStatus = GatewayStatus.Refunded; refundId = rid; }
            }
            DateTimeOffset? expiresOn = null;
            if (payment is { } p && Text(p, "expiration_time") is { } duration
                && DateTimeOffset.TryParse(Text(root, "created_date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created))
                expiresOn = created.ToUniversalTime() + XmlConvert.ToTimeSpan(duration);
            return new GatewayPayment(new VerifiedPayment(id, reference, new Money(amount)), localStatus,
                method is { } m ? Text(m, "qr_code") : null, method is { } m2 ? Text(m2, "ticket_url") : null, expiresOn, refundId);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or DomainException or OverflowException)
        { throw InvalidResponse(); }
    }

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long SearchTotal(JsonElement value)
    {
        // Sandbox currently returns paging.total as a JSON string; other environments use a number.
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0) return number;
        throw InvalidResponse();
    }
    private static decimal Amount(JsonElement element, string name)
    {
        if (!decimal.TryParse(Text(element, name), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || value <= 0 || decimal.Round(value, 2) != value) throw InvalidResponse();
        return value;
    }
    private static void CheckExpected(GatewayPayment result, Guid reference, Money amount)
    {
        if (result.Evidence.ExternalReference != reference || result.Evidence.Amount != amount) throw InvalidResponse();
    }
    private static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 100 || !id.All(char.IsAsciiLetterOrDigit)) throw InvalidResponse();
    }
    private static GatewayException InvalidResponse() => new("gateway.invalid_response", true, "Provider returned an unrecognized or inconsistent order.");
}
