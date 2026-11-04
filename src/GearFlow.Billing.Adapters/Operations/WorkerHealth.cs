using System.Collections.Concurrent;

namespace GearFlow.Billing.Adapters.Operations;

public sealed class WorkerHealth
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> heartbeats = new();
    public void Beat(string worker) => heartbeats[worker] = DateTimeOffset.UtcNow;
    public bool Alive(string worker, TimeSpan tolerance) => heartbeats.TryGetValue(worker, out var last)
        && DateTimeOffset.UtcNow - last <= tolerance;
}
