namespace Guardly.Api.Models;

/// <summary>Svar från POST /inspections när bilden har tagits emot och lagts på kön.</summary>
/// <param name="Id">Inspektionens ID. Används mot GET /inspections/{id}.</param>
/// <param name="Status">Alltid "Queued" i det asynkrona flödet.</param>
/// <param name="SiteId">Arbetsplatsen bilden tillhör.</param>
/// <param name="StatusUrl">Färdig länk att polla för resultatet.</param>
/// <param name="EstimatedReadyInSeconds">Ungefärlig väntetid innan resultatet finns.</param>
public record InspectionAcceptedResponse(
    string Id,
    InspectionStatus Status,
    string SiteId,
    string StatusUrl,
    int EstimatedReadyInSeconds);

/// <summary>Kompakt rad i listan från GET /inspections. Byggs från blob-metadata så vi slipper ladda ner varje JSON-dokument.</summary>
public record InspectionSummary(
    string Id,
    string SiteId,
    InspectionStatus Status,
    DateTimeOffset CreatedAt,
    int WarningCount,
    WarningSeverity? HighestSeverity);

/// <summary>Svar från GET /inspections — en sida med inspektioner.</summary>
public record InspectionListResponse(
    int Count,
    string? SiteId,
    IReadOnlyList<InspectionSummary> Items);

/// <summary>Svar från GET /health.</summary>
public record HealthResponse(
    string Status,
    string Service,
    string Version,
    DateTimeOffset Timestamp,
    string Environment);

/// <summary>Svar från GET /health/ready — kollar att beroenden faktiskt svarar.</summary>
public record ReadinessResponse(
    string Status,
    bool BlobStorage,
    bool Queue,
    bool ComputerVision,
    string? Detail);

/// <summary>Aggregerad statistik per arbetsplats, underlag till veckorapporten.</summary>
public record SiteStatistics(
    string SiteId,
    int TotalInspections,
    int InspectionsWithWarnings,
    int TotalWarnings,
    Dictionary<string, int> WarningsByCode);

/// <summary>Svar från GET /stats.</summary>
public record StatisticsResponse(
    DateTimeOffset GeneratedAt,
    int SiteCount,
    IReadOnlyList<SiteStatistics> Sites);

/// <summary>Meddelandet som läggs på Azure Storage Queue. Håller det litet — själva bilden ligger i Blob Storage.</summary>
public record InspectionJob(string InspectionId, string SiteId, string ImageBlobPath);
