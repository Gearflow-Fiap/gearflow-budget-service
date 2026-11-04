using System.Text;
using GearFlow.Billing.Adapters.Dispatching;
using GearFlow.Billing.Adapters.Security;
using GearFlow.Billing.Application.Budgets;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using GearFlow.Billing.Adapters.Validation;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GearFlow.Billing.Adapters;

public static class DependencyInjection
{
    public static IServiceCollection AddBillingCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(configuration);
        services.AddOptions<ApprovalOptions>().Bind(configuration.GetSection("Billing"))
            .Validate(x => Encoding.UTF8.GetByteCount(x.ApprovalTokenSecret) >= 32,
                "Billing:ApprovalTokenSecret requires at least 32 bytes.")
            .Validate(x => Uri.TryCreate(x.PublicBaseUrl, UriKind.Absolute, out var uri)
                && (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)),
                "Billing:PublicBaseUrl requires HTTPS or loopback HTTP.")
            .ValidateOnStart();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IApprovalTokenService, HmacApprovalTokenService>();
        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddScoped<IUseCaseLog, UseCaseLog>();
        services.AddInputValidators();
        return services;
    }

    // Register only after persistent ports are available. This avoids exposing a host
    // whose business endpoints fail at first resolution, or using volatile storage as production storage.
    public static IServiceCollection AddBillingUseCases(this IServiceCollection services)
    {
        foreach (var type in typeof(GenerateBudgetHandler).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }))
        foreach (var contract in type.GetInterfaces().Where(t => t.IsGenericType
            && (t.GetGenericTypeDefinition() == typeof(ICommandHandler<,>) || t.GetGenericTypeDefinition() == typeof(IQueryHandler<,>))))
            services.AddScoped(contract, type);
        return services;
    }
}
