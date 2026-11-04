using GearFlow.Billing.Application.Ports.Out;

namespace GearFlow.Billing.Adapters;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
