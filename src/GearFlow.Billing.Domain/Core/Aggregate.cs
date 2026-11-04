namespace GearFlow.Billing.Domain.Core;

public sealed record DomainEvent(string Name, Guid AggregateId, Guid ServiceOrderId, DateTimeOffset OccurredOn);

public abstract class Aggregate
{
    private readonly List<DomainEvent> events = [];
    public Guid Id { get; }
    public long Version { get; private set; }
    public IReadOnlyList<DomainEvent> Events => events.AsReadOnly();

    protected Aggregate(Guid id) => Id = Ensure.Identity(id);

    protected void Record(string name, Guid serviceOrderId, DateTimeOffset now)
    {
        events.Add(new DomainEvent(name, Id, serviceOrderId, Ensure.Utc(now)));
        Changed();
    }

    protected void Changed() => Version++;

    protected void RestoreVersion(long version)
    {
        Ensure.That(version >= 0, "snapshot.version", "Invalid aggregate version.");
        Version = version;
        events.Clear();
    }

    public void ClearEvents() => events.Clear();
}
