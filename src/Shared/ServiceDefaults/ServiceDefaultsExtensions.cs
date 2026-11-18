using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ServiceDefaults;

/// <summary>
/// Setup that every service in the system shares. Call <see cref="AddServiceDefaults"/>
/// before <c>builder.Build()</c> and <see cref="MapDefaultEndpoints"/> after it.
/// Milestone 5 adds OpenTelemetry here, so all services get tracing from one place.
/// </summary>
public static class ServiceDefaultsExtensions
{
    public const string HealthEndpointPath = "/health";

    /// <summary>Registers the shared services (for now: health checks).</summary>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        // Liveness only: "the process is up and answering HTTP". It doesn't check the
        // broker or a database yet; Milestone 3 adds a database check for readiness.
        builder.Services.AddHealthChecks();

        return builder;
    }

    /// <summary>Maps the shared endpoints (for now: GET /health).</summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath);

        return app;
    }
}
