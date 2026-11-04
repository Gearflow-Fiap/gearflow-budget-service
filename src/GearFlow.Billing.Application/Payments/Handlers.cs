using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;

namespace GearFlow.Billing.Application.Payments;

public sealed class RequestPaymentHandler(PaymentWorkflow workflow) : ICommandHandler<RequestPaymentCommand, Result<Guid>>
{
    public Task<Result<Guid>> HandleAsync(RequestPaymentCommand command, CancellationToken ct) => workflow.RequestAsync(command, ct);
}
public sealed class ProcessPaymentWebhookHandler(PaymentWorkflow workflow) : ICommandHandler<ProcessPaymentWebhookCommand, Result<Unit>>
{
    public Task<Result<Unit>> HandleAsync(ProcessPaymentWebhookCommand command, CancellationToken ct) => workflow.RefreshAsync(command.OrderId, ct);
}
public sealed class RecoverPaymentHandler(PaymentWorkflow workflow) : ICommandHandler<RecoverPaymentCommand, Result<Unit>>
{
    public Task<Result<Unit>> HandleAsync(RecoverPaymentCommand command, CancellationToken ct) => workflow.RecoverAsync(command.PaymentId, ct);
}
public sealed class RefundPaymentHandler(PaymentWorkflow workflow) : ICommandHandler<RefundPaymentCommand, Result<Unit>>
{
    public Task<Result<Unit>> HandleAsync(RefundPaymentCommand command, CancellationToken ct) => workflow.RefundAsync(command, ct);
}
public sealed class ReconcilePaymentsHandler(IPaymentRecoveryReadModel readModel, IPaymentWorkStore work, IClock clock)
    : ICommandHandler<ReconcilePaymentsCommand, Result<Unit>>
{
    public async Task<Result<Unit>> HandleAsync(ReconcilePaymentsCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        foreach (var candidate in await readModel.FindDueAsync(now, 100, ct))
            await work.EnqueueAsync($"reconcile:{candidate.PaymentId}:{now.ToUnixTimeSeconds() / 300}",
                null, candidate.PaymentId, now, ct);
        return Result<Unit>.Success(default);
    }
}
