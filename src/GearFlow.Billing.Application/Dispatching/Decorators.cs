using System.Diagnostics;
using GearFlow.Billing.Application.Ports.In;

namespace GearFlow.Billing.Application.Dispatching;

public sealed class ValidationDecorator<TCommand, TResult>(ICommandHandler<TCommand, TResult> inner,
    IEnumerable<ICommandValidator<TCommand>> validators) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken ct)
    {
        foreach (var validator in validators) await validator.ValidateAsync(command, ct);
        return await inner.HandleAsync(command, ct);
    }
}

public sealed class LoggingDecorator<TCommand, TResult>(ICommandHandler<TCommand, TResult> inner,
    IUseCaseLog log) : ICommandHandler<TCommand, TResult> where TCommand : ICommand<TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var success = false;
        try
        {
            var result = await inner.HandleAsync(command, ct);
            success = true;
            return result;
        }
        finally { log.Completed(typeof(TCommand).Name, Stopwatch.GetElapsedTime(started), success); }
    }
}
