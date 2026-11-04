namespace GearFlow.Billing.Application.Ports.In;

public interface ICommand<TResult>;
public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct);
}

public interface IDispatcher
{
    Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default);
    Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken ct = default);
}

public interface ICommandValidator<in TCommand>
{
    Task ValidateAsync(TCommand command, CancellationToken ct);
}

public interface IUseCaseLog
{
    // Only type, outcome and duration. Command payloads can contain tokens and personal data.
    void Completed(string useCase, TimeSpan elapsed, bool success);
}
