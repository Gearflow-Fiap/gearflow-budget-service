using System.Reflection;
using System.Runtime.ExceptionServices;
using GearFlow.Billing.Application.Dispatching;
using GearFlow.Billing.Application.Ports.In;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GearFlow.Billing.Adapters.Operations;

namespace GearFlow.Billing.Adapters.Dispatching;

public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    private static readonly MethodInfo CommandMethod = typeof(Dispatcher).GetMethod(nameof(SendTyped), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo QueryMethod = typeof(Dispatcher).GetMethod(nameof(QueryTyped), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default) =>
        Invoke<TResult>(CommandMethod, command, ct);
    public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken ct = default) =>
        Invoke<TResult>(QueryMethod, query, ct);

    private Task<TResult> Invoke<TResult>(MethodInfo method, object request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        try { return (Task<TResult>)method.MakeGenericMethod(request.GetType(), typeof(TResult)).Invoke(this, [request, ct])!; }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private async Task<TResult> SendTyped<TCommand, TResult>(TCommand command, CancellationToken ct) where TCommand : ICommand<TResult>
    {
        // Allow only identifiers into the log scope; never reflect whole command payloads/tokens.
        var identifiers = new Dictionary<string, object?>();
        foreach (var name in new[] { "ServiceOrderId", "BudgetId", "PaymentId", "CausationId" })
            if (typeof(TCommand).GetProperty(name)?.GetValue(command) is Guid id && id != Guid.Empty) identifiers[name] = id;
        if (identifiers.TryGetValue("ServiceOrderId", out var order)) identifiers["CorrelationId"] = order;
        using var scope = services.GetRequiredService<ILogger<Dispatcher>>().BeginScope(identifiers);
        var handler = services.GetRequiredService<ICommandHandler<TCommand, TResult>>();
        var validated = new ValidationDecorator<TCommand, TResult>(handler, services.GetServices<ICommandValidator<TCommand>>());
        return await new LoggingDecorator<TCommand, TResult>(validated, services.GetRequiredService<IUseCaseLog>()).HandleAsync(command, ct);
    }

    private Task<TResult> QueryTyped<TQuery, TResult>(TQuery query, CancellationToken ct) where TQuery : IQuery<TResult> =>
        services.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, ct);
}

public sealed class UseCaseLog(ILogger<UseCaseLog> logger) : IUseCaseLog
{
    public void Completed(string useCase, TimeSpan elapsed, bool success)
    {
        BillingTelemetry.UseCaseDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("usecase.name", useCase));
        logger.LogInformation("Use case {UseCase} completed in {DurationMs} ms; execution completed: {Success}", useCase, elapsed.TotalMilliseconds, success);
    }
}
