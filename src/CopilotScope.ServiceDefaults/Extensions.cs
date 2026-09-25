using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace CopilotScope.ServiceDefaults;

/// <summary>
/// The estate's shared kernel (P2): cross-cutting plumbing only — OpenTelemetry,
/// health checks, service discovery and HTTP resilience — exposed as opt-in extension
/// methods over IHostApplicationBuilder / WebApplication. No business type lives here.
///
/// This is the answer to the review's P15 finding: CopilotScope is an observability
/// product that emitted no telemetry about itself. Every service now calls
/// AddServiceDefaults(); the OTLP exporter activates only when
/// OTEL_EXPORTER_OTLP_ENDPOINT is set, so a bare `dotnet run` stays quiet.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Adds an application's own <c>appsettings.json</c>, compiled into its assembly as
    /// <paramref name="resourceName"/>, as the lowest-priority configuration source — but only
    /// when the content root has no <c>appsettings.json</c> of its own.
    ///
    /// A service normally reads its defaults (the model pricing table, the history limits) from
    /// the file next to it. A host that runs the service inside another process — the native
    /// <c>copilotscope</c> binary — has no such file, because two applications' copies would
    /// collide in one publish directory. Loaded into memory rather than as a stream source: a
    /// stream can be read once, and the configuration manager re-reads every source each time
    /// another one is added.
    /// </summary>
    public static TBuilder AddEmbeddedDefaults<TBuilder>(this TBuilder builder, System.Reflection.Assembly assembly,
        string resourceName) where TBuilder : IHostApplicationBuilder
    {
        if (File.Exists(Path.Combine(builder.Environment.ContentRootPath, "appsettings.json"))) return builder;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return builder;

        var defaults = new ConfigurationBuilder().AddJsonStream(stream).Build()
            .AsEnumerable()
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = defaults });
        return builder;
    }

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // Self-telemetry is on unless a host switches it off, and the native copilotscope host
        // does (ADR-004). Its collector is the endpoint OTEL_EXPORTER_OTLP_ENDPOINT points at in
        // a developer's shell, so exporting would send the collector's own spans into itself,
        // forever; and two applications in one process would each install a tracer provider
        // listening to both.
        if (builder.Configuration.GetValue("CopilotScope:SelfTelemetry:Enabled", true))
            builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Every HttpClient gets the standard resilience handler (retries, circuit
            // breaker, timeouts) and service discovery by default — a Foundry or
            // collector blip surfaces as a retried call, not an unhandled 5xx.
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                // Keep health/liveness probe noise out of traces.
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                    !ctx.Request.Path.StartsWithSegments("/health")
                    && !ctx.Request.Path.StartsWithSegments("/alive"))
                .AddHttpClientInstrumentation());

        builder.AddOpenTelemetryExporters();
        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        if (useOtlpExporter)
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // A "live" liveness check the app is running at all; readiness (/health)
            // aggregates every registered check.
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // /health = readiness (all checks), /alive = liveness (the "live"-tagged check).
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/alive", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });
        return app;
    }
}
