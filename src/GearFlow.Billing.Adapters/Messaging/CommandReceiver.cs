using System.Text.Json;
using GearFlow.Billing.Application.Budgets;
using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Core;

namespace GearFlow.Billing.Adapters.Messaging;

public sealed class CommandReceiver(IDispatcher dispatcher, MongoInbox inbox, InboxContext context,
    IUnitOfWork uow, IOutboxWriter outbox, IClock clock)
{
    public async Task ReceiveAsync(WireEnvelope envelope, InboxLease lease, CancellationToken ct)
    {
        context.Activate(lease);
        if (await inbox.IsCompletedAsync(lease, ct)) return;
        Error? error;
        try
        {
            error = WireContracts.Command(envelope) switch
            {
                GenerateBudgetCommand c => (await dispatcher.Send(c, ct)).Error,
                RequestPaymentCommand c => (await dispatcher.Send(c, ct)).Error,
                CancelBudgetCommand c => (await dispatcher.Send(c, ct)).Error,
                RefundPaymentCommand c => (await dispatcher.Send(c, ct)).Error,
                _ => throw new DeliveryException("message.type")
            };
        }
        catch (DomainException exception) { error = new(exception.Code, exception.Message); }
        catch (InputValidationException) { error = new("input.invalid", "Command input is invalid."); }
        catch (JsonException) { throw new DeliveryException("message.payload"); }
        if (await inbox.IsCompletedAsync(lease, ct)) return;
        if (error is null) throw new DeliveryException("inbox.not_completed", true);
        var businessFailure = error.Code is "input.invalid" or "identity.empty" or "value.invalid" or "email.invalid"
            or "money.currency" or "money.negative" or "item.type" or "item.quantity" or "date.utc"
            or "budget.items" or "budget.zero" or "budget.expiry" or "budget.conflict" or "budget.compensating"
            or "budget.state" or "budget.expired" or "payment.conflict" or "payment.amount" or "payment.order";
        if (!businessFailure || envelope.MessageType is "CancelBudget" or "RefundPayment") throw new DeliveryException(error.Code);
        await uow.ExecuteAsync(async token =>
        {
            IntegrationEvent failure;
            if (envelope.MessageType == "GenerateBudget")
                failure = new BudgetGenerationFailed(Guid.NewGuid(), envelope.ServiceOrderId, envelope.MessageId, clock.UtcNow, error.Code, error.Message);
            else
            {
                if (!envelope.Payload.TryGetProperty("budgetId", out var budgetId) || budgetId.ValueKind != JsonValueKind.String
                    || !budgetId.TryGetGuid(out var id) || id == Guid.Empty) throw new DeliveryException("message.budget_id");
                failure = new PaymentFailed(Guid.NewGuid(), envelope.ServiceOrderId, envelope.MessageId, clock.UtcNow, id, null, error.Code, error.Message);
            }
            await outbox.AppendAsync(failure, token);
            await context.CompleteAsync(token);
            return true;
        }, ct);
    }
}
