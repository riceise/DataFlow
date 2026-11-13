using DataFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DataFlow.Infrastructure.Observability;

public static class ObservabilityExtensions
{
    public static IServiceCollection AddDataFlowObservability(this IServiceCollection services,
        IConfiguration configuration, string serviceName, bool worker = false)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t =>
            {
                t.AddSource(DataFlowTelemetry.Name).AddAspNetCoreInstrumentation(o =>
                    o.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation().AddNpgsql();
                // SQL text can contain literals; export only operation timing and safe metadata.
                t.AddProcessor(new SafeDatabaseTraceProcessor());
                if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) { t.AddOtlpExporter(); }
            })
            .WithMetrics(m =>
            {
                m.AddMeter(DataFlowTelemetry.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) { m.AddOtlpExporter(); }
            });
        services.AddHealthChecks()
            .AddCheck<DatabaseReadiness>("postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(5))
            .AddCheck<StorageReadiness>("storage", tags: ["ready"], timeout: TimeSpan.FromSeconds(5))
            .AddCheck<BrokerReadiness>("rabbitmq", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
        if (worker)
        {
            services.AddSingleton<WorkerReadiness>();
            services.AddHealthChecks().AddCheck<WorkerReadiness>("consumer", tags: ["ready"]);
            services.AddHostedService<OperationalMetricsService>();
        }
        return services;
    }

    public static void MapDataFlowHealth(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(new
                {
                    status = report.Status.ToString(),
                    dependencies = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString())
                });
            }
        });
    }
}

internal sealed class SafeDatabaseTraceProcessor : OpenTelemetry.BaseProcessor<System.Diagnostics.Activity>
{
    public override void OnEnd(System.Diagnostics.Activity activity)
    {
        if (activity.Source.Name != "Npgsql") { return; }
        activity.DisplayName = "postgresql";
        activity.SetTag("db.statement", null);
        activity.SetTag("db.query.text", null);
        foreach (var tag in activity.TagObjects.Where(t => t.Key.StartsWith("db.query.parameter.", StringComparison.Ordinal)).ToArray())
        {
            activity.SetTag(tag.Key, null);
        }
    }
}
