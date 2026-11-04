using GearFlow.Billing.Adapters.PaymentGateway.MercadoPago;
using GearFlow.Billing.Adapters.Persistence.Mongo;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using GearFlow.Billing.Adapters.Messaging;
using GearFlow.Billing.Adapters.Operations;
using Microsoft.Extensions.DependencyInjection.Extensions;
using GearFlow.Billing.Adapters.Email;
using GearFlow.Billing.Application.Notifications;
using System.Net.Mail;

namespace GearFlow.Billing.Adapters;

public static class PaymentIntegrationRegistration
{
    public static IServiceCollection AddMercadoPagoIntegration(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var mockPayments = configuration.GetValue<bool>("MercadoPago:UseMock");
        if (mockPayments)
            services.AddScoped<IPaymentGateway, GearFlow.Billing.Adapters.PaymentGateway.MockPaymentGateway>();
        else
        {
            services.AddOptions<MercadoPagoOptions>().Bind(configuration.GetSection("MercadoPago"))
                .Validate(x => x.AccessToken.Length >= 10 && !x.AccessToken.Contains("placeholder", StringComparison.OrdinalIgnoreCase), "MercadoPago:AccessToken is required.")
                .Validate(x => !x.TestMode || environment.IsDevelopment(), "MercadoPago:TestMode is restricted to Development.")
                .Validate(x => x.WebhookEnabled
                    ? x.WebhookSecret.Length >= 16 && !x.WebhookSecret.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
                    : environment.IsDevelopment() && x.TestMode,
                    "Webhook requires a signing secret; disabling it is allowed only in Development TestMode.")
                .Validate(x => Uri.TryCreate(x.BaseUrl, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo)
                    && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                    && ((uri.Scheme == "https" && uri.Host == "api.mercadopago.com")
                        || (environment.IsDevelopment() && uri.Scheme == "http" && (uri.IsLoopback || uri.Host == "wiremock"))), "Invalid provider BaseUrl.")
                .Validate(x => x.MaxAttempts is >= 1 and <= 4 && x.AttemptTimeoutSeconds is >= 1 and <= 10
                    && x.TotalTimeoutSeconds is >= 10 and <= 55 && x.RetryDelayMilliseconds is >= 0 and <= 5000
                    && x.CircuitFailures is >= 1 and <= 100 && x.CircuitBreakSeconds is >= 1 and <= 300
                    && x.WebhookToleranceSeconds is >= 30 and <= 900, "Invalid provider resilience or signature settings.")
                .ValidateOnStart();
            services.AddSingleton<GatewayCircuit>();
            services.AddSingleton<MercadoPagoWebhookValidator>();
            services.AddHttpClient<IPaymentGateway, MercadoPagoGateway>((provider, http) =>
            {
                http.BaseAddress = new Uri(provider.GetRequiredService<IOptions<MercadoPagoOptions>>().Value.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = Timeout.InfiniteTimeSpan;
            }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) });
        }
        services.AddOptions<MongoOptions>().Bind(configuration.GetSection("Mongo"))
            .Validate(x => !string.IsNullOrWhiteSpace(x.Database) && x.Database.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), "Invalid Mongo database.")
            .ValidateOnStart();
        var connection = configuration.GetConnectionString("Mongo") ?? throw new InvalidOperationException("ConnectionStrings:Mongo is required for payment integration.");
        services.AddSingleton<IMongoClient>(_ =>
        {
            var settings = MongoClientSettings.FromConnectionString(connection);
            if (!environment.IsDevelopment() && settings.Credential is null)
                throw new InvalidOperationException("Production MongoDB requires authenticated credentials.");
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5); settings.ConnectTimeout = TimeSpan.FromSeconds(5);
            return new MongoClient(settings);
        });
        services.AddSingleton(provider => provider.GetRequiredService<IMongoClient>().GetDatabase(provider.GetRequiredService<IOptions<MongoOptions>>().Value.Database));
        services.AddScoped<MongoSession>();
        services.AddScoped<TransactionalTelemetry>();
        services.AddScoped<InboxContext>();
        services.AddScoped<ICommandCompletion>(p => p.GetRequiredService<InboxContext>());
        services.AddSingleton<MongoInbox>();
        services.AddScoped<CommandReceiver>();
        services.TryAddSingleton<WorkerHealth>();
        services.AddSingleton<Readiness>();
        // Readiness can resolve options even when Rabbit is disabled and reports unavailable.
        services.AddOptions<RabbitOptions>().Bind(configuration.GetSection("RabbitMq"));
        services.AddScoped<IUnitOfWork>(p => p.GetRequiredService<MongoSession>());
        services.AddScoped<MongoBillingStore>();
        services.AddScoped<IBudgetRepository>(p => p.GetRequiredService<MongoBillingStore>());
        services.AddScoped<IPaymentRepository>(p => p.GetRequiredService<MongoBillingStore>());
        services.AddScoped<ICommercialFenceRepository, MongoCommercialFences>();
        services.AddScoped<INotificationRepository>(p => p.GetRequiredService<MongoBillingStore>());
        services.AddScoped<IOutboxWriter>(p => p.GetRequiredService<MongoBillingStore>());
        services.AddScoped<IBudgetReadModel>(p => p.GetRequiredService<MongoBillingStore>());
        services.AddScoped<IPaymentReadModel>(p => p.GetRequiredService<MongoBillingStore>());
        services.AddScoped<IPaymentRecoveryReadModel>(p => p.GetRequiredService<MongoBillingStore>());
        services.AddSingleton<IPaymentWorkStore, MongoPaymentWorkStore>();
        services.AddScoped<PaymentWorkflow>();
        services.AddScoped<IPaymentCompensator>(p => p.GetRequiredService<PaymentWorkflow>());
        services.AddBillingUseCases();
        services.AddHostedService<MongoInitializer>();
        services.AddHostedService<PaymentWorkWorker>();
        services.AddHostedService<PaymentReconciliationWorker>();
        if (configuration.GetValue<bool>("Telemetry:Enabled")) services.AddHostedService<OperationalMetricsWorker>();
        // Register composing/sending ports even when the worker is disabled, preserving validated handler DI.
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection("Email"))
            .Validate(x => !x.Enabled || (!string.IsNullOrWhiteSpace(x.Host) && x.Port is > 0 and <= 65535
                && MailAddress.TryCreate(x.FromAddress, out _) && (environment.IsDevelopment() || x.EnableSsl)), "Invalid SMTP settings; TLS required outside Development.")
            .ValidateOnStart();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddScoped<INotificationComposer, NotificationComposer>();
        services.AddSingleton<INotificationReadModel, MongoNotificationReadModel>();
        if (configuration.GetValue<bool>("Email:Enabled")) services.AddHostedService<NotificationWorker>();
        if (configuration.GetValue<bool>("RabbitMq:Enabled"))
        {
            services.AddOptions<RabbitOptions>().Bind(configuration.GetSection("RabbitMq"))
                .Validate(x => !string.IsNullOrWhiteSpace(x.Host) && x.Port is > 0 and <= 65535
                    && !string.IsNullOrWhiteSpace(x.Username) && !string.IsNullOrWhiteSpace(x.Password)
                    && x.QueueType is "classic" or "quorum", "Invalid RabbitMq connection or queue settings.")
                .Validate(x => environment.IsDevelopment() || x.Tls, "RabbitMQ requires TLS outside Development.")
                .ValidateOnStart();
            services.AddSingleton<IConfirmedPublisher, RabbitPublisher>();
            services.AddHostedService<RabbitTopology>();
            services.AddHostedService<RabbitCommandWorker>();
            services.AddHostedService<OutboxWorker>();
            services.AddSingleton<DlqReplay>();
        }
        return services;
    }
}


