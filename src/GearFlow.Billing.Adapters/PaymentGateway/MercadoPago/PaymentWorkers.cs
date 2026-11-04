using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using GearFlow.Billing.Adapters.Operations;

namespace GearFlow.Billing.Adapters.PaymentGateway.MercadoPago;

public sealed class PaymentWorkWorker(IServiceScopeFactory scopes, IPaymentWorkStore workStore, IClock clock,
    ILogger<PaymentWorkWorker> logger, WorkerHealth health) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            PaymentWork? work = null;
            try
            {
                work = await workStore.ClaimAsync(Guid.NewGuid(), clock.UtcNow, TimeSpan.FromMinutes(2), stoppingToken);
                health.Beat("payment-work");
                if (work is null) { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(100));
                using var scope = scopes.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
                Result<Unit> result = work.PaymentId is { } id
                    ? await dispatcher.Send(new RecoverPaymentCommand(id), deadline.Token)
                    : await dispatcher.Send(new ProcessPaymentWebhookCommand(work.OrderId!), deadline.Token);
                if (result.IsSuccess) await workStore.CompleteAsync(work, clock.UtcNow, stoppingToken);
                else await Retry(work, result.Error!.Code, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                var code = exception is GatewayException gateway ? gateway.Code : exception.GetType().Name;
                logger.LogWarning("Payment work deferred: {Code}", code);
                if (work is not null)
                {
                    try { await Retry(work, code, stoppingToken); }
                    catch (Exception) when (!stoppingToken.IsCancellationRequested) { /* lease expiry permits recovery */ }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
    private Task Retry(PaymentWork work, string code, CancellationToken ct) => workStore.DeferAsync(work,
        clock.UtcNow.AddSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Min(8, work.Attempts)))), code, ct);
}

public sealed class PaymentReconciliationWorker(IServiceScopeFactory scopes, ILogger<PaymentReconciliationWorker> logger, WorkerHealth health) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ReconcilePaymentsCommand(), stoppingToken);
                health.Beat("payment-reconciliation");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Payment reconciliation deferred: {Code}", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
