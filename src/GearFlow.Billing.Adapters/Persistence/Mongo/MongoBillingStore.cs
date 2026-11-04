using System.Text.Json;
using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Events;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Notifications;
using GearFlow.Billing.Domain.Payments;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Diagnostics;
using GearFlow.Billing.Adapters.Operations;

namespace GearFlow.Billing.Adapters.Persistence.Mongo;

public sealed class MongoBillingStore(IMongoDatabase database, MongoSession session, IClock clock, TransactionalTelemetry? telemetry = null)
    : IBudgetRepository, IPaymentRepository, INotificationRepository, IOutboxWriter,
      IBudgetReadModel, IPaymentReadModel, IPaymentRecoveryReadModel
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IMongoCollection<BsonDocument> Collection(string name) => database.GetCollection<BsonDocument>(name);
    private async Task<BsonDocument?> Find(string collection, string field, Guid id, CancellationToken ct) =>
        await (await Collection(collection).FindAsync(session.Current, new BsonDocument(field, id.ToString("D")), cancellationToken: ct)).FirstOrDefaultAsync(ct);
    public async Task<Budget?> GetAsync(Guid id, CancellationToken ct) => RestoreBudget(await Find("budgets", "_id", id, ct));
    public async Task<Budget?> FindByServiceOrderAsync(Guid id, CancellationToken ct) => RestoreBudget(await Find("budgets", "serviceOrderId", id, ct));
    async Task<Payment?> IPaymentRepository.GetAsync(Guid id, CancellationToken ct) => RestorePayment(await Find("payments", "_id", id, ct));
    public async Task<Payment?> FindByBudgetAsync(Guid id, CancellationToken ct) => RestorePayment(await Find("payments", "budgetId", id, ct));
    async Task<Notification?> INotificationRepository.GetAsync(Guid id, CancellationToken ct)
    {
        var document = await Find("notifications", "_id", id, ct);
        return document is null ? null : Notification.Restore(JsonSerializer.Deserialize<NotificationSnapshot>(document["snapshot"].AsString, Json)!);
    }
    internal static Budget? RestoreBudget(BsonDocument? document) => document is null ? null
        : Budget.Restore(JsonSerializer.Deserialize<BudgetSnapshot>(document["snapshot"].AsString, Json)!);
    internal static Payment? RestorePayment(BsonDocument? document) => document is null ? null
        : Payment.Restore(JsonSerializer.Deserialize<PaymentSnapshot>(document["snapshot"].AsString, Json)!);
    private BsonDocument BudgetDocument(Budget budget) => new()
    {
        ["_id"] = budget.Id.ToString("D"), ["serviceOrderId"] = budget.ServiceOrderId.ToString("D"),
        ["version"] = budget.Version, ["snapshot"] = JsonSerializer.Serialize(budget.Snapshot(), Json),
        ["status"] = budget.Status.ToString(), ["expiresOn"] = budget.ExpiresOn.UtcDateTime,
        ["createdOn"] = budget.CreatedOn.UtcDateTime,
        ["totalAmount"] = new BsonDecimal128(budget.Total.Amount), ["updatedOn"] = clock.UtcNow.UtcDateTime
    };
    private BsonDocument PaymentDocument(Payment payment) => new()
    {
        ["_id"] = payment.Id.ToString("D"), ["budgetId"] = payment.BudgetId.ToString("D"),
        ["serviceOrderId"] = payment.ServiceOrderId.ToString("D"), ["version"] = payment.Version,
        ["status"] = payment.Status.ToString(), ["providerOrderId"] = payment.ProviderOrderId is null ? BsonNull.Value : new BsonString(payment.ProviderOrderId),
        ["unfinished"] = payment.Attempts.Any(x => x.Status is AttemptStatus.Started or AttemptStatus.Uncertain),
        ["amount"] = new BsonDecimal128(payment.Amount.Amount), ["createdOn"] = payment.CreatedOn.UtcDateTime,
        ["updatedOn"] = clock.UtcNow.UtcDateTime, ["snapshot"] = JsonSerializer.Serialize(payment.Snapshot(), Json)
    };
    private BsonDocument NotificationDocument(Notification notification) => new()
    {
        ["_id"] = notification.Id.ToString("D"), ["deduplicationKey"] = notification.DeduplicationKey,
        ["version"] = notification.Version, ["snapshot"] = JsonSerializer.Serialize(notification.Snapshot(), Json)
        , ["status"] = notification.Status.ToString(), ["availableOn"] = notification.NextAttemptOn.UtcDateTime,
        ["leaseUntil"] = notification.LeaseUntil is { } lease ? new BsonDateTime(lease.UtcDateTime) : BsonNull.Value
    };
    public Task AddAsync(Budget budget, CancellationToken ct) => Insert("budgets", BudgetDocument(budget), ct);
    public Task AddAsync(Payment payment, CancellationToken ct) => Insert("payments", PaymentDocument(payment), ct);
    public async Task AddAsync(Notification notification, CancellationToken ct)
    {
        await Collection("notifications").UpdateOneAsync(session.Current,
            new BsonDocument("deduplicationKey", notification.DeduplicationKey),
            new BsonDocument("$setOnInsert", NotificationDocument(notification)), new UpdateOptions { IsUpsert = true }, ct);
    }
    private async Task Insert(string name, BsonDocument document, CancellationToken ct)
    {
        try { await Collection(name).InsertOneAsync(session.Current, document, cancellationToken: ct); }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { throw new DuplicateException(); }
    }
    public Task SaveAsync(Budget budget, long expectedVersion, CancellationToken ct) => Replace("budgets", BudgetDocument(budget), expectedVersion, ct);
    public Task SaveAsync(Payment payment, long expectedVersion, CancellationToken ct) => Replace("payments", PaymentDocument(payment), expectedVersion, ct);
    public Task SaveAsync(Notification notification, long expectedVersion, CancellationToken ct) => Replace("notifications", NotificationDocument(notification), expectedVersion, ct);
    private async Task Replace(string name, BsonDocument document, long version, CancellationToken ct)
    {
        var result = await Collection(name).ReplaceOneAsync(session.Current,
            new BsonDocument { ["_id"] = document["_id"], ["version"] = version }, document, cancellationToken: ct);
        if (result.MatchedCount != 1) throw new ConcurrencyException();
    }
    public async Task AppendAsync(IntegrationEvent message, CancellationToken ct)
    {
        var document = new BsonDocument
        {
        ["_id"] = message.MessageId.ToString("D"), ["type"] = message.GetType().Name,
        ["serviceOrderId"] = message.ServiceOrderId.ToString("D"), ["occurredOn"] = message.OccurredOn.UtcDateTime,
        ["payload"] = JsonSerializer.Serialize(message, message.GetType(), Json), ["sentOn"] = BsonNull.Value
        , ["traceparent"] = Activity.Current?.Id is { } trace ? new BsonString(trace) : BsonNull.Value,
        ["tracestate"] = Activity.Current?.TraceStateString is { } state ? new BsonString(state) : BsonNull.Value
        };
        await Insert("outbox", document, ct);
        double? approval = null;
        if (message is BudgetApproved approved)
        {
            var budget = await Find("budgets", "_id", approved.BudgetId, ct);
            if (budget is not null && budget.TryGetValue("createdOn", out var created))
                approval = Math.Max(0, (approved.OccurredOn.UtcDateTime - created.ToUniversalTime()).TotalSeconds);
        }
        telemetry?.Add(message, approval);
    }
    private Task<BsonDocument?> FindRead(string collection, string field, Guid id, CancellationToken ct) =>
        Collection(collection).Find(new BsonDocument(field, id.ToString("D"))).FirstOrDefaultAsync(ct)!;
    private static BudgetView? BudgetProjection(BsonDocument? document) => document is null ? null : new(
        Guid.Parse(document["_id"].AsString), Guid.Parse(document["serviceOrderId"].AsString), document["status"].AsString,
        Decimal128.ToDecimal(document["totalAmount"].AsDecimal128), "BRL", new DateTimeOffset(document["expiresOn"].ToUniversalTime()));
    private static PaymentView? PaymentProjection(BsonDocument? document)
    {
        if (document is null) return null;
        using var data = JsonDocument.Parse(document["snapshot"].AsString);
        var root = data.RootElement;
        return new(Guid.Parse(document["_id"].AsString), Guid.Parse(document["budgetId"].AsString), Guid.Parse(document["serviceOrderId"].AsString),
            document["status"].AsString, Decimal128.ToDecimal(document["amount"].AsDecimal128), "BRL",
            root.GetProperty("qrCode").GetString(), root.GetProperty("ticketUrl").GetString(),
            root.GetProperty("expiresOn").ValueKind == JsonValueKind.Null ? null : root.GetProperty("expiresOn").GetDateTimeOffset());
    }
    async Task<BudgetView?> IBudgetReadModel.GetAsync(Guid id, CancellationToken ct) => BudgetProjection(await FindRead("budgets", "_id", id, ct));
    async Task<BudgetView?> IBudgetReadModel.FindByServiceOrderAsync(Guid id, CancellationToken ct) => BudgetProjection(await FindRead("budgets", "serviceOrderId", id, ct));
    async Task<PaymentView?> IPaymentReadModel.GetAsync(Guid id, CancellationToken ct) => PaymentProjection(await FindRead("payments", "_id", id, ct));
    async Task<PaymentView?> IPaymentReadModel.FindByBudgetAsync(Guid id, CancellationToken ct) => PaymentProjection(await FindRead("payments", "budgetId", id, ct));
    public async Task<IReadOnlyList<PaymentCandidate>> FindDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var filter = new BsonDocument("$or", new BsonArray
        {
            new BsonDocument { ["status"] = new BsonDocument("$in", new BsonArray { "Created", "Pending" }), ["updatedOn"] = new BsonDocument("$lte", now.AddMinutes(-10).UtcDateTime) },
            new BsonDocument("unfinished", true),
            new BsonDocument { ["status"] = "Canceled", ["createdOn"] = new BsonDocument("$gte", now.AddDays(-30).UtcDateTime) }
        });
        var due = new BsonDocument("$and", new BsonArray
        {
            filter, new BsonDocument("$or", new BsonArray { new BsonDocument("lastReconciledOn", BsonNull.Value),
                new BsonDocument("lastReconciledOn", new BsonDocument("$lte", now.AddMinutes(-5).UtcDateTime)) })
        });
        var documents = await Collection("payments").Find(due).Sort(new BsonDocument("lastReconciledOn", 1)).Limit(limit).ToListAsync(ct);
        if (documents.Count > 0)
            await Collection("payments").UpdateManyAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(documents.Select(d => d["_id"])))),
                new BsonDocument("$set", new BsonDocument("lastReconciledOn", now.UtcDateTime)), cancellationToken: ct);
        return documents.Select(d => new PaymentCandidate(Guid.Parse(d["_id"].AsString), d["providerOrderId"].IsBsonNull ? null : d["providerOrderId"].AsString)).ToArray();
    }
}
