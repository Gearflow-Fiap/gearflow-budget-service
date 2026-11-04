namespace GearFlow.Billing.Application.Common;

// Sanitized gateway failure. Technical uncertainty never implies payment refusal.
public sealed class GatewayException(string code, bool retryable, string message) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
