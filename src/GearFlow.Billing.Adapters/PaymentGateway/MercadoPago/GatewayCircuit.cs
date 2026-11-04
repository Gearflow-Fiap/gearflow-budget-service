using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.Options;

namespace GearFlow.Billing.Adapters.PaymentGateway.MercadoPago;

public sealed class GatewayCircuit(IClock clock, IOptions<MercadoPagoOptions> options)
{
    private readonly object sync = new();
    private int failures;
    private DateTimeOffset? openUntil;
    public void Check()
    {
        lock (sync)
        {
            if (openUntil > clock.UtcNow)
                throw new GatewayException("gateway.circuit_open", true, "Payment provider temporarily unavailable.");
        }
    }
    public void Success() { lock (sync) { failures = 0; openUntil = null; } }
    public void Failure()
    {
        lock (sync)
        {
            if (++failures >= options.Value.CircuitFailures)
                openUntil = clock.UtcNow.AddSeconds(options.Value.CircuitBreakSeconds);
        }
    }
}
