using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Notifications;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Notifications;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.UnitTests;

public class NotificationDeliveryTests
{
    private sealed class Ports : INotificationRepository, IBudgetRepository, IPaymentRepository, IUnitOfWork, IClock, INotificationComposer, IEmailSender
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        public NotificationSnapshot State { get; private set; }
        public Budget Budget { get; }
        public bool FailSmtp { get; set; }
        public int Sent { get; private set; }
        private bool inTransaction;
        public Ports()
        {
            Budget = new(Guid.NewGuid(), Guid.NewGuid(), new Customer("Ana", new EmailAddress("ana@example.com")),
                [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "Serviço", new Money(350), 1)], UtcNow, UtcNow.AddHours(1));
            State = new Notification(Guid.NewGuid(), Budget.ServiceOrderId, Budget.Id, "proposal", Budget.Customer.Email, "budget-proposal-v1", UtcNow).Snapshot();
        }
        public Task<Notification?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult<Notification?>(Notification.Restore(State));
        public Task AddAsync(Notification notification, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(Notification notification, long version, CancellationToken ct)
        { Assert.Equal(version, State.Version); State = notification.Snapshot(); return Task.CompletedTask; }
        Task<Budget?> IBudgetRepository.GetAsync(Guid id, CancellationToken ct) => Task.FromResult<Budget?>(Budget);
        public Task<Budget?> FindByServiceOrderAsync(Guid id, CancellationToken ct) => Task.FromResult<Budget?>(Budget);
        public Task AddAsync(Budget budget, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(Budget budget, long version, CancellationToken ct) => throw new NotSupportedException();
        Task<Payment?> IPaymentRepository.GetAsync(Guid id, CancellationToken ct) => Task.FromResult<Payment?>(null);
        public Task<Payment?> FindByBudgetAsync(Guid id, CancellationToken ct) => Task.FromResult<Payment?>(null);
        public Task AddAsync(Payment payment, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(Payment payment, long version, CancellationToken ct) => throw new NotSupportedException();
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            Assert.False(inTransaction); inTransaction = true; var state = State;
            try { return await action(ct); } catch { State = state; throw; } finally { inTransaction = false; }
        }
        public EmailMessage Compose(Notification notification, Budget budget, Payment? payment) => new(notification.Recipient.Value, "Proposal", "HTML");
        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            Assert.False(inTransaction); Assert.Equal(NotificationStatus.Sending, State.Status);
            if (FailSmtp) throw new IOException("SMTP unavailable"); Sent++; return Task.CompletedTask;
        }
    }
    private static DispatchNotificationHandler Handler(Ports p) => new(p, p, p, p, p, p, p);

    [Fact]
    public async Task Smtp_success_marks_sent_and_repeating_job_does_not_send_again()
    {
        var ports = new Ports(); var command = new DispatchNotificationCommand(ports.State.Id);
        Assert.True((await Handler(ports).HandleAsync(command, default)).IsSuccess);
        Assert.True((await Handler(ports).HandleAsync(command, default)).IsSuccess);
        Assert.Equal(1, ports.Sent); Assert.Equal(NotificationStatus.Sent, ports.State.Status); Assert.Equal(1, ports.State.Attempts);
    }
    [Fact]
    public async Task Smtp_failure_persists_retry_and_preserves_business_state()
    {
        var ports = new Ports { FailSmtp = true }; var command = new DispatchNotificationCommand(ports.State.Id);
        Assert.False((await Handler(ports).HandleAsync(command, default)).IsSuccess);
        Assert.Equal(NotificationStatus.Pending, ports.State.Status); Assert.True(ports.State.NextAttemptOn > ports.UtcNow);
        Assert.Equal(BudgetStatus.Pending, ports.Budget.Status); Assert.Equal(0, ports.Sent);
        ports.FailSmtp = false;
        await Handler(ports).HandleAsync(command, default); Assert.Equal(0, ports.Sent);
        ports.UtcNow = ports.State.NextAttemptOn;
        await Handler(ports).HandleAsync(command, default);
        Assert.Equal(1, ports.Sent); Assert.Equal(2, ports.State.Attempts); Assert.Equal(NotificationStatus.Sent, ports.State.Status);
    }
    [Fact]
    public async Task Expired_proposal_is_suppressed_without_an_email()
    {
        var ports = new Ports(); ports.UtcNow = ports.Budget.ExpiresOn;
        await Handler(ports).HandleAsync(new(ports.State.Id), default);
        Assert.Equal(0, ports.Sent); Assert.Equal(NotificationStatus.Suppressed, ports.State.Status);
    }
}
