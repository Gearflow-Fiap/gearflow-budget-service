using GearFlow.Billing.Application.Budgets;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.UnitTests;

public class ApplicationTests
{
    private sealed class Ports : IBudgetRepository, IPaymentRepository, IOutboxWriter, IUnitOfWork, IClock,
        IBudgetReadModel, IPaymentReadModel
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-11-02T12:00:00Z");
        public Budget? Budget { get; set; }
        public Payment? Payment { get; set; }
        public List<IntegrationEvent> Outbox { get; } = [];
        public int Saves { get; private set; }
        public Task<Budget?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Budget?.Id == id ? Budget : null);
        public Task<Budget?> FindByServiceOrderAsync(Guid id, CancellationToken ct) => Task.FromResult(Budget?.ServiceOrderId == id ? Budget : null);
        public Task AddAsync(Budget budget, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(Budget budget, long expectedVersion, CancellationToken ct) { Saves++; return Task.CompletedTask; }
        Task<Payment?> IPaymentRepository.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Payment);
        public Task<Payment?> FindByBudgetAsync(Guid id, CancellationToken ct) => Task.FromResult(Payment);
        public Task AddAsync(Payment payment, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(Payment payment, long expectedVersion, CancellationToken ct) => throw new NotSupportedException();
        public Task AppendAsync(IntegrationEvent message, CancellationToken ct) { Outbox.Add(message); return Task.CompletedTask; }
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) => action(ct);
        Task<BudgetView?> IBudgetReadModel.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(BudgetProjection(id));
        Task<BudgetView?> IBudgetReadModel.FindByServiceOrderAsync(Guid id, CancellationToken ct) => Task.FromResult(BudgetProjection(Budget?.ServiceOrderId == id ? Budget.Id : Guid.Empty));
        Task<PaymentView?> IPaymentReadModel.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(PaymentProjection(id));
        Task<PaymentView?> IPaymentReadModel.FindByBudgetAsync(Guid id, CancellationToken ct) => Task.FromResult(PaymentProjection(Payment?.BudgetId == id ? Payment.Id : Guid.Empty));
        private BudgetView? BudgetProjection(Guid id) => Budget?.Id == id ? new(Budget.Id, Budget.ServiceOrderId,
            Budget.Status.ToString(), Budget.Total.Amount, Budget.Total.Currency, Budget.ExpiresOn) : null;
        private PaymentView? PaymentProjection(Guid id) => Payment?.Id == id ? new(Payment.Id, Payment.BudgetId, Payment.ServiceOrderId,
            Payment.Status.ToString(), Payment.Amount.Amount, Payment.Amount.Currency, Payment.QrCode, Payment.TicketUrl, Payment.ExpiresOn) : null;
    }
    private static Ports Pending()
    {
        var ports = new Ports();
        ports.Budget = new Budget(Guid.NewGuid(), Guid.NewGuid(), new Customer("Ana", new EmailAddress("ana@example.com")),
            [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Serviço", new Money(100), 1)], ports.UtcNow, ports.UtcNow.AddDays(1));
        return ports;
    }
    private static CancelBudgetHandler Cancel(Ports ports) => new(ports, ports, ports, ports, ports);

    [Fact]
    public async Task Missing_budget_compensation_succeeds_with_correlated_outbox_event()
    {
        var ports = new Ports(); var order = Guid.NewGuid(); var id = Guid.NewGuid(); var cause = Guid.NewGuid();
        Assert.True((await Cancel(ports).HandleAsync(new(order, id, "rollback", cause), default)).IsSuccess);
        var fact = Assert.IsType<BudgetCanceled>(Assert.Single(ports.Outbox));
        Assert.Equal(order, fact.ServiceOrderId); Assert.Equal(id, fact.BudgetId); Assert.Equal(cause, fact.CausationId);
        Assert.Equal(0, ports.Saves);
    }

    [Fact]
    public async Task Cancel_rejects_invalid_identity_and_wrong_service_order_without_effects()
    {
        var ports = Pending();
        Assert.False((await Cancel(ports).HandleAsync(new(Guid.Empty, ports.Budget!.Id, "rollback"), default)).IsSuccess);
        var result = await Cancel(ports).HandleAsync(new(Guid.NewGuid(), ports.Budget.Id, "rollback"), default);
        Assert.Equal("budget.order", result.Error!.Code);
        Assert.Equal(BudgetStatus.Pending, ports.Budget.Status); Assert.Empty(ports.Outbox); Assert.Equal(0, ports.Saves);
    }

    [Fact]
    public async Task Cancel_is_idempotent_for_unpaid_budget()
    {
        var ports = Pending(); var command = new CancelBudgetCommand(ports.Budget!.ServiceOrderId, ports.Budget.Id, "rollback");
        Assert.True((await Cancel(ports).HandleAsync(command, default)).IsSuccess);
        Assert.True((await Cancel(ports).HandleAsync(command, default)).IsSuccess);
        Assert.Equal(BudgetStatus.Canceled, ports.Budget.Status);
        Assert.Single(ports.Outbox); Assert.Equal(1, ports.Saves);
    }

    [Fact]
    public async Task Cancel_never_announces_success_while_external_charge_is_unresolved()
    {
        var ports = Pending(); var budget = ports.Budget!; budget.Approve(ports.UtcNow);
        ports.Payment = Payment.Request(Guid.NewGuid(), budget, budget.ServiceOrderId, budget.Total, ports.UtcNow);
        var result = await Cancel(ports).HandleAsync(new(budget.ServiceOrderId, budget.Id, "rollback"), default);
        Assert.Equal("payment.compensation_required", result.Error!.Code);
        Assert.Equal(BudgetStatus.Approved, budget.Status); Assert.Empty(ports.Outbox);
    }

    [Fact]
    public async Task Queries_use_read_projections_and_do_not_write_or_publish()
    {
        var ports = Pending(); var budget = ports.Budget!; budget.Approve(ports.UtcNow);
        ports.Payment = Payment.Request(Guid.NewGuid(), budget, budget.ServiceOrderId, budget.Total, ports.UtcNow);
        var budgets = new BudgetQueryHandler(ports); var payments = new PaymentQueryHandler(ports);
        Assert.Equal(budget.Id, (await budgets.HandleAsync(new GetBudgetByIdQuery(budget.Id), default))!.Id);
        Assert.Equal(budget.Id, (await budgets.HandleAsync(new GetBudgetByServiceOrderQuery(budget.ServiceOrderId), default))!.Id);
        Assert.Equal(ports.Payment.Id, (await payments.HandleAsync(new GetPaymentByIdQuery(ports.Payment.Id), default))!.Id);
        Assert.Equal(ports.Payment.Id, (await payments.HandleAsync(new GetPaymentByBudgetQuery(budget.Id), default))!.Id);
        Assert.Null(await budgets.HandleAsync(new GetBudgetByIdQuery(Guid.NewGuid()), default));
        Assert.Null(await payments.HandleAsync(new GetPaymentByBudgetQuery(Guid.NewGuid()), default));
        Assert.Empty(ports.Outbox); Assert.Equal(0, ports.Saves);
    }
}
