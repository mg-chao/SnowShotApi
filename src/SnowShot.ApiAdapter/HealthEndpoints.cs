using SnowShot.Application;

namespace SnowShot.Api;

internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
            .WithName("Liveness").WithTags("Health").Produces(StatusCodes.Status200OK);
        endpoints.MapGet("/health/ready", ReadyAsync)
            .WithName("Readiness").WithTags("Health")
            .Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapGet("/health/components", ComponentsAsync)
            .WithName("DependencyTelemetry").WithTags("Health").Produces(StatusCodes.Status200OK);
        return endpoints;
    }

    private static async Task<IResult> ReadyAsync(IReadinessService readiness, CancellationToken cancellationToken)
    {
        var report = await readiness.CheckAsync(cancellationToken);
        return Results.Json(new
        {
            status = report.Ready ? "ready" : "not_ready",
            policy = new
            {
                configured_revision = report.ConfiguredPolicyRevision,
                configured_fingerprint = report.ConfiguredPolicyFingerprint,
                active_revision = report.ActivePolicyRevision,
                active_fingerprint = report.ActivePolicyFingerprint,
            },
        },
            statusCode: report.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> ComponentsAsync(IReadinessService readiness, CancellationToken cancellationToken)
    {
        var report = await readiness.CheckAsync(cancellationToken);
        return Results.Ok(new
        {
            policy = new
            {
                configured_revision = report.ConfiguredPolicyRevision,
                configured_fingerprint = report.ConfiguredPolicyFingerprint,
                active_revision = report.ActivePolicyRevision,
                active_fingerprint = report.ActivePolicyFingerprint,
            },
            components = report.Components,
        });
    }
}
