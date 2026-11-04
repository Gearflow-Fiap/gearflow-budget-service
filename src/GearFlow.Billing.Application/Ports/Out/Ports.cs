using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Notifications;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.Application.Ports.Out;

public interface IClock { DateTimeOffset UtcNow { get; } }

// All repositories and writers in a scope participate in the same local transaction.
// Implementations must roll back all writes on exception, including Inbox and Outbox.
// No network operation belongs inside this callback.
public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
}

public interface IBudgetRepository
{
    Task<Budget?> GetAsync(Guid id, CancellationToken ct);
    Task<Budget?> FindByServiceOrderAsync(Guid serviceOrderId, CancellationToken ct);
    Task AddAsync(Budget budget, CancellationToken ct);
    Task SaveAsync(Budget budget, long expectedVersion, CancellationToken ct);
}

public interface IPaymentRepository
{
    Task<Payment?> GetAsync(Guid id, CancellationToken ct);
    Task<Payment?> FindByBudgetAsync(Guid budgetId, CancellationToken ct);
    Task AddAsync(Payment payment, CancellationToken ct);
    Task SaveAsync(Payment payment, long expectedVersion, CancellationToken ct);
}

public interface INotificationRepository
{
    Task AddAsync(Notification notification, CancellationToken ct);
    Task<Notification?> GetAsync(Guid id, CancellationToken ct);
    Task SaveAsync(Notification notification, long expectedVersion, CancellationToken ct);
}

public sealed record BudgetView(Guid Id, Guid ServiceOrderId, string Status, decimal TotalAmount,
    string Currency, DateTimeOffset ExpiresOn);
public sealed record PaymentView(Guid Id, Guid BudgetId, Guid ServiceOrderId, string Status,
    decimal Amount, string Currency, string? QrCode, string? TicketUrl, DateTimeOffset? ExpiresOn);

public interface IBudgetReadModel
{
    Task<BudgetView?> GetAsync(Guid id, CancellationToken ct);
    Task<BudgetView?> FindByServiceOrderAsync(Guid serviceOrderId, CancellationToken ct);
}

public interface IPaymentReadModel
{
    Task<PaymentView?> GetAsync(Guid id, CancellationToken ct);
    Task<PaymentView?> FindByBudgetAsync(Guid budgetId, CancellationToken ct);
}

public interface IOutboxWriter { Task AppendAsync(IntegrationEvent message, CancellationToken ct); }
public interface IApprovalTokenService
{
    string Create(Guid budgetId, DateTimeOffset expiresOn);
    bool IsValid(string token, Guid budgetId, DateTimeOffset expiresOn);
}

public sealed record EmailMessage(string Recipient, string Subject, string Html, string? MessageId = null);
public interface IEmailSender { Task SendAsync(EmailMessage message, CancellationToken ct); }

public enum GatewayStatus { Pending, Paid, Rejected, Canceled, Refunded, Unknown }
public sealed record ChargeRequest(Guid PaymentId, Guid BudgetId, Money Amount, Customer Customer,
    Guid OperationId, DateTimeOffset ExpiresOn, DateTimeOffset CreatedOn);
public sealed record GatewayPayment(VerifiedPayment Evidence, GatewayStatus Status,
    string? QrCode, string? TicketUrl, DateTimeOffset? ExpiresOn, string? RefundId);

// Transport errors throw; Unknown is never mapped to a definitive payment failure.
// OperationId is persisted before calling these operations and reused on retry.
public interface IPaymentGateway
{
    Task<GatewayPayment> CreateAsync(ChargeRequest request, CancellationToken ct);
    Task<GatewayPayment> GetAsync(string orderId, CancellationToken ct);
    Task<GatewayPayment?> FindByBudgetAsync(Guid budgetId, DateTimeOffset createdOn, CancellationToken ct);
    Task<GatewayPayment> CancelAsync(string orderId, Guid operationId, CancellationToken ct);
    Task<GatewayPayment> RefundAsync(string orderId, Money amount, Guid operationId, CancellationToken ct);
}
