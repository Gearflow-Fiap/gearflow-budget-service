using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.Options;

namespace GearFlow.Billing.Adapters.PaymentGateway.MercadoPago;

public sealed record ValidatedWebhook(string OrderId, string DeduplicationKey);

public sealed class MercadoPagoWebhookValidator(IOptions<MercadoPagoOptions> options, IClock clock)
{
    public ValidatedWebhook? Validate(string dataId, string requestId, string signature)
    {
        if (string.IsNullOrWhiteSpace(dataId) || !dataId.StartsWith("ORD", StringComparison.OrdinalIgnoreCase)
            || dataId.Length > 100 || !dataId.All(char.IsAsciiLetterOrDigit)
            || string.IsNullOrWhiteSpace(requestId) || requestId.Length > 200
            || !requestId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || signature.Length > 512) return null;
        var fields = signature.Split(',').Select(x => x.Trim().Split('=', 2)).ToArray();
        if (fields.Any(x => x.Length != 2) || fields.Count(x => x[0] == "ts") != 1 || fields.Count(x => x[0] == "v1") != 1) return null;
        var timestamp = fields.Single(x => x[0] == "ts")[1];
        var hash = fields.Single(x => x[0] == "v1")[1];
        if (hash.Length != 64 || timestamp.Length is not (10 or 13)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)) return null;
        DateTimeOffset signedOn;
        try { signedOn = timestamp.Length == 13 ? DateTimeOffset.FromUnixTimeMilliseconds(numeric) : DateTimeOffset.FromUnixTimeSeconds(numeric); }
        catch (ArgumentOutOfRangeException) { return null; }
        if ((clock.UtcNow - signedOn).Duration() > TimeSpan.FromSeconds(options.Value.WebhookToleranceSeconds)) return null;
        // MP signs alphanumeric data.id in lowercase; use the query parameter, never body status.
        var manifest = $"id:{dataId.ToLowerInvariant()};request-id:{requestId};ts:{timestamp};";
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.WebhookSecret), Encoding.UTF8.GetBytes(manifest));
        try { if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(hash))) return null; }
        catch (FormatException) { return null; }
        // Stable across body modifications; only authenticated fields determine deduplication.
        return new(dataId.ToUpperInvariant(), "webhook:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))));
    }
}
