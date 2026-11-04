using System.Diagnostics;
using GearFlow.Billing.Adapters.Messaging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace GearFlow.Billing.Api;

public sealed class PrivateHttpTagsProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        foreach (var name in new[] { "url.full", "http.url", "http.target" })
        {
            if (activity.GetTagItem(name) is not string value) continue;
            activity.SetTag(name, Uri.TryCreate(value, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Path) : value.Split('?', 2)[0]);
        }
        activity.SetTag("url.query", null);
    }
}

public static class Observability
{
    public static IServiceCollection AddBillingObservability(this IServiceCollection services, IConfiguration configuration, ILoggingBuilder logging)
    {
        if (!configuration.GetValue<bool>("Telemetry:Enabled")) return services;
        services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("gearflow-budget-service"))
            .WithTracing(tracing => tracing.AddSource(WireContracts.Activities.Name)
                .AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddProcessor(new PrivateHttpTagsProcessor()).AddOtlpExporter())
            .WithMetrics(metrics => metrics.AddMeter("GearFlow.Billing").AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddOtlpExporter());
        logging.AddOpenTelemetry(options => { options.IncludeFormattedMessage = false; options.IncludeScopes = true;
            options.ParseStateValues = true; options.AddOtlpExporter(); });
        return services;
    }
}
