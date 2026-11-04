using GearFlow.Billing.Domain.Budgets;

namespace GearFlow.Billing.Application.Ports.Out;

public interface ICommercialFenceRepository
{
    Task<CommercialFence> GetOrCreateAsync(Guid serviceOrderId, CancellationToken ct);
    Task SaveAsync(CommercialFence fence, long expectedVersion, CancellationToken ct);
}
