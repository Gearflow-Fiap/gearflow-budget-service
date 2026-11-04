using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Ports.Out;
using MongoDB.Driver;
using GearFlow.Billing.Adapters.Messaging;
using GearFlow.Billing.Adapters.Operations;
using System.Diagnostics;

namespace GearFlow.Billing.Adapters.Persistence.Mongo;

public sealed class MongoOptions
{
    public string Database { get; set; } = "gearflow_billing";
}

public sealed class MongoSession(IMongoClient client, InboxContext? inbox = null, TransactionalTelemetry? telemetry = null) : IUnitOfWork
{
    public IClientSessionHandle Current => current ?? throw new InvalidOperationException("A local transaction is required.");
    private IClientSessionHandle? current;
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (current is not null) throw new InvalidOperationException("Nested transactions are not supported.");
        using var activity = WireContracts.Activities.StartActivity("billing.transaction", ActivityKind.Client);
        activity?.SetTag("db.system", "mongodb");
        using var session = await client.StartSessionAsync(cancellationToken: ct);
        current = session;
        try
        {
            session.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, ReadPreference.Primary, WriteConcern.WMajority));
            var result = await action(ct);
            if (inbox is not null) await inbox.OnCommittingAsync(session, ct);
            for (var attempt = 0; ; attempt++)
            {
                try { await session.CommitTransactionAsync(ct); break; }
                catch (MongoException exception) when (exception.HasErrorLabel("UnknownTransactionCommitResult") && attempt < 2)
                { ct.ThrowIfCancellationRequested(); }
            }
            telemetry?.Commit();
            return result;
        }
        catch (Exception exception)
        {
            if (session.IsInTransaction)
            {
                using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await session.AbortTransactionAsync(abort.Token); }
                catch (MongoException) { }
                catch (OperationCanceledException) { }
            }
            if (exception is MongoException mongo && mongo.HasErrorLabel("TransientTransactionError")) throw new ConcurrencyException();
            throw;
        }
        finally { current = null; inbox?.ResetCompletion(); telemetry?.Reset(); }
    }
}
