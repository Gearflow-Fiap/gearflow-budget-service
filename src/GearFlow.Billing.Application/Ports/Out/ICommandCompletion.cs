namespace GearFlow.Billing.Application.Ports.Out;

public enum IncomingCommandKind { GenerateBudget, RequestPayment, CancelBudget, RefundPayment }

// Optional incoming command receipt. Completion joins the final LOCAL business transaction.
// It must never complete the receipt merely because a remote operation was started.
public interface ICommandCompletion
{
    IncomingCommandKind? Kind { get; }
    Task CompleteAsync(CancellationToken ct);
}
