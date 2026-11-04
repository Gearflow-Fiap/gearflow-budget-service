using System.Diagnostics;
using System.Diagnostics.Metrics;
using GearFlow.Billing.Application.Events;

namespace GearFlow.Billing.Adapters.Operations;

public static class BillingTelemetry
{
    public static readonly Meter Meter = new("GearFlow.Billing");
    public static readonly Counter<long> BusinessEvents = Meter.CreateCounter<long>("billing.business.events");
    public static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>("billing.messages.deadlettered");
    public static readonly Histogram<double> ApprovalDuration = Meter.CreateHistogram<double>("billing.budget.approval.duration", "s");
    public static readonly Histogram<double> UseCaseDuration = Meter.CreateHistogram<double>("billing.usecase.duration", "ms");
}

public sealed class TransactionalTelemetry
{
    private readonly List<(IntegrationEvent Event, double? ApprovalSeconds)> pending = [];
    public void Add(IntegrationEvent message, double? approvalSeconds) => pending.Add((message, approvalSeconds));
    public void Commit()
    {
        foreach (var entry in pending)
        {
            BillingTelemetry.BusinessEvents.Add(1, new KeyValuePair<string, object?>("event.name", entry.Event.GetType().Name));
            if (entry.ApprovalSeconds is { } seconds) BillingTelemetry.ApprovalDuration.Record(seconds);
        }
        pending.Clear();
    }
    public void Reset() => pending.Clear();
}
