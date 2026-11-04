using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.Options;

namespace GearFlow.Billing.Adapters.Security;

public sealed class ApprovalOptions
{
    public string ApprovalTokenSecret { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
}

public sealed class HmacApprovalTokenService(IOptions<ApprovalOptions> options, IClock clock) : IApprovalTokenService
{
    private readonly byte[] key = Encoding.UTF8.GetBytes(options.Value.ApprovalTokenSecret);

    public string Create(Guid budgetId, DateTimeOffset expiresOn)
    {
        if (budgetId == Guid.Empty || expiresOn.Offset != TimeSpan.Zero || expiresOn <= clock.UtcNow)
            throw new ArgumentException("Budget and future UTC expiry are required.");
        var expiry = expiresOn.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return expiry + "." + Convert.ToHexString(Sign(budgetId, expiry));
    }

    public bool IsValid(string token, Guid budgetId, DateTimeOffset expiresOn)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100 || budgetId == Guid.Empty || clock.UtcNow >= expiresOn) return false;
        var parts = token.Split('.');
        if (parts.Length != 2 || parts[1].Length != 64 || !long.TryParse(parts[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out var seconds) || seconds != expiresOn.ToUnixTimeSeconds()
            || clock.UtcNow.ToUnixTimeSeconds() >= seconds) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parts[1]), Sign(budgetId, parts[0])); }
        catch (FormatException) { return false; }
    }

    private byte[] Sign(Guid id, string expiry) => HMACSHA256.HashData(key,
        Encoding.UTF8.GetBytes($"billing:budget-review:v1:{id:D}:{expiry}"));
}
