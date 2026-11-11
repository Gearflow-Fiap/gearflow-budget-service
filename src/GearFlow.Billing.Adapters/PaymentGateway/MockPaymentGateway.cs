using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.Adapters.PaymentGateway;

// Simulation only: local payment intents supply stable evidence across retries and restarts.
public sealed class MockPaymentGateway(IPaymentRepository payments, IUnitOfWork uow) : IPaymentGateway
{
    private static string OrderId(Guid budgetId) => $"MOCK-{budgetId:N}";

    public Task<GatewayPayment> CreateAsync(ChargeRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new GatewayPayment(new VerifiedPayment(OrderId(request.BudgetId), request.BudgetId, request.Amount),
            GatewayStatus.Paid, null, null, request.ExpiresOn, null));
    }

    public async Task<GatewayPayment> GetAsync(string orderId, CancellationToken ct)
    {
        if (!orderId.StartsWith("MOCK-", StringComparison.Ordinal) || !Guid.TryParseExact(orderId[5..], "N", out var budgetId))
            throw new GatewayException("mock.order_not_found", false, "Unknown simulated order.");
        var payment = await uow.ExecuteAsync(token => payments.FindByBudgetAsync(budgetId, token), ct)
            ?? throw new GatewayException("mock.order_not_found", false, "Simulated payment intent is missing.");
        var status = payment.Status switch
        {
            PaymentStatus.Refunded => GatewayStatus.Refunded,
            PaymentStatus.Canceled => GatewayStatus.Canceled,
            PaymentStatus.Failed => GatewayStatus.Rejected,
            _ => GatewayStatus.Paid
        };
        return new(new VerifiedPayment(orderId, budgetId, payment.Amount), status, null, null, payment.ExpiresOn, payment.RefundId);
    }

    public async Task<GatewayPayment?> FindByBudgetAsync(Guid budgetId, DateTimeOffset createdOn, CancellationToken ct)
    {
        var payment = await uow.ExecuteAsync(token => payments.FindByBudgetAsync(budgetId, token), ct);
        return payment?.ProviderOrderId is { } id ? await GetAsync(id, ct) : null;
    }

    // An immediately paid charge requires refund rather than cancellation.
    public Task<GatewayPayment> CancelAsync(string orderId, Guid operationId, CancellationToken ct) => GetAsync(orderId, ct);

    public async Task<GatewayPayment> RefundAsync(string orderId, Money amount, Guid operationId, CancellationToken ct)
    {
        var payment = await GetAsync(orderId, ct);
        if (payment.Evidence.Amount != amount)
            throw new GatewayException("mock.amount_conflict", false, "Refund amount differs from the simulated charge.");
        return payment with { Status = GatewayStatus.Refunded, RefundId = $"MOCK-REFUND-{payment.Evidence.ExternalReference:N}" };
    }
}
