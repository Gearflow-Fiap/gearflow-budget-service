namespace GearFlow.Billing.Adapters.PaymentGateway.MercadoPago;

public sealed class MercadoPagoOptions
{
    public bool Enabled { get; set; }
    public bool TestMode { get; set; }
    public bool WebhookEnabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://api.mercadopago.com/";
    public string AccessToken { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public int AttemptTimeoutSeconds { get; set; } = 10;
    public int TotalTimeoutSeconds { get; set; } = 55;
    public int MaxAttempts { get; set; } = 4;
    public int RetryDelayMilliseconds { get; set; } = 1000;
    public int CircuitFailures { get; set; } = 5;
    public int CircuitBreakSeconds { get; set; } = 30;
    public int WebhookToleranceSeconds { get; set; } = 300;
}
