using System.Reflection;
using Guardly.Api.Models;
using Guardly.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Guardly.Api.Endpoints;

/// <summary>Health checks och statistik — allt som inte handlar om en enskild inspektion.</summary>
public static class SystemEndpoints
{
    private const string SystemTag = "System";
    private const string StatsTag = "Statistik";

    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // ------------------------------------------------------------------
        // GET /health — enkel liveness. Svarar alltid 200 om processen lever.
        // ------------------------------------------------------------------
        app.MapGet("/health", (IWebHostEnvironment env) =>
            {
                var version = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

                return Results.Ok(new HealthResponse(
                    Status: "Healthy",
                    Service: "Guardly Inspection API",
                    Version: version,
                    Timestamp: DateTimeOffset.UtcNow,
                    Environment: env.EnvironmentName));
            })
            .WithName("Health")
            .WithTags(SystemTag)
            .WithSummary("Health check")
            .WithDescription(
                "Liveness-kontroll. Svarar 200 OK så länge processen kör. " +
                "Container Apps använder den här för att avgöra om en replica ska startas om.")
            .Produces<HealthResponse>(StatusCodes.Status200OK);

        // ------------------------------------------------------------------
        // GET /health/ready — readiness. Kollar att beroendena faktiskt svarar.
        // ------------------------------------------------------------------
        app.MapGet("/health/ready", async (
                IInspectionStore store,
                IInspectionQueue queue,
                IVisionAnalyzer vision,
                CancellationToken cancellationToken) =>
            {
                var blobOk = await store.IsHealthyAsync(cancellationToken);
                var queueOk = await queue.IsHealthyAsync(cancellationToken);
                var visionOk = await vision.CanReachServiceAsync(cancellationToken);

                var ready = blobOk && queueOk && visionOk;

                var detail = ready
                    ? null
                    : "Kontrollera att managed identity har rätt roller: Storage Blob Data Contributor, " +
                      "Storage Queue Data Contributor och Cognitive Services User.";

                var response = new ReadinessResponse(
                    ready ? "Ready" : "Degraded", blobOk, queueOk, visionOk, detail);

                // 503 när vi inte är redo, så att Container Apps slutar skicka trafik hit.
                return ready
                    ? Results.Ok(response)
                    : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("Readiness")
            .WithTags(SystemTag)
            .WithSummary("Readiness check mot Blob Storage, kö och Computer Vision")
            .WithDescription("Svarar 503 om något beroende inte går att nå, med detaljer om vad som fattas.")
            .Produces<ReadinessResponse>(StatusCodes.Status200OK)
            .Produces<ReadinessResponse>(StatusCodes.Status503ServiceUnavailable);

        // ------------------------------------------------------------------
        // GET /stats — underlag till veckorapporten
        // ------------------------------------------------------------------
        app.MapGet("/stats", async (
                [FromQuery] string? siteId,
                [FromQuery] int? limit,
                IInspectionStore store,
                CancellationToken cancellationToken) =>
            {
                var take = Math.Clamp(limit ?? 500, 1, 2000);
                var inspections = await store.ListFullAsync(siteId, take, cancellationToken);

                var sites = inspections
                    .GroupBy(i => i.SiteId)
                    .Select(g => new SiteStatistics(
                        SiteId: g.Key,
                        TotalInspections: g.Count(),
                        InspectionsWithWarnings: g.Count(i => i.Warnings.Count > 0),
                        TotalWarnings: g.Sum(i => i.Warnings.Count),
                        WarningsByCode: g.SelectMany(i => i.Warnings)
                            .GroupBy(w => w.Code)
                            .ToDictionary(w => w.Key, w => w.Count())))
                    .OrderByDescending(s => s.TotalWarnings)
                    .ToList();

                return Results.Ok(new StatisticsResponse(DateTimeOffset.UtcNow, sites.Count, sites));
            })
            .WithName("Statistics")
            .WithTags(StatsTag)
            .WithSummary("Aggregerad varningsstatistik per arbetsplats")
            .WithDescription(
                "Underlag till veckorapporten: hur många inspektioner, hur många med varningar " +
                "och fördelningen per varningstyp. Läser inspektionsdokumenten från Blob Storage, " +
                "så håll limit rimlig.")
            .Produces<StatisticsResponse>(StatusCodes.Status200OK);
    }
}
