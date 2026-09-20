using System.Collections.Concurrent;
using Guardly.Api.Models;

namespace Guardly.Api.Services;

/// <summary>
/// Lagring i minnet. Används bara lokalt (Storage:UseInMemory = true) så att man kan
/// köra "dotnet run" och testa hela flödet utan Azure-konto. Allt försvinner vid omstart.
/// </summary>
public class InMemoryInspectionStore : IInspectionStore
{
    private readonly ConcurrentDictionary<string, Inspection> _inspections = new();
    private readonly ConcurrentDictionary<string, (byte[] Content, string ContentType, string FileName)> _images = new();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<string> SaveImageAsync(
        string siteId, string inspectionId, string fileName, string contentType,
        Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        var path = $"{siteId}/{inspectionId}{Path.GetExtension(fileName)}";
        _images[$"{siteId}/{inspectionId}"] = (buffer.ToArray(), contentType, fileName);
        return path;
    }

    public Task<StoredImage?> GetImageAsync(string siteId, string inspectionId, CancellationToken cancellationToken)
    {
        if (_images.TryGetValue($"{siteId}/{inspectionId}", out var image))
        {
            return Task.FromResult<StoredImage?>(
                new StoredImage(new MemoryStream(image.Content), image.ContentType, image.FileName));
        }

        return Task.FromResult<StoredImage?>(null);
    }

    public Task SaveAsync(Inspection inspection, CancellationToken cancellationToken)
    {
        _inspections[inspection.Id] = inspection;
        return Task.CompletedTask;
    }

    public Task<Inspection?> GetAsync(string id, string? siteId, CancellationToken cancellationToken)
    {
        _inspections.TryGetValue(id, out var inspection);
        return Task.FromResult<Inspection?>(inspection);
    }

    public Task<IReadOnlyList<InspectionSummary>> ListAsync(
        string? siteId, InspectionStatus? status, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<InspectionSummary> result = _inspections.Values
            .Where(i => string.IsNullOrWhiteSpace(siteId) || i.SiteId == siteId)
            .Where(i => !status.HasValue || i.Status == status.Value)
            .OrderByDescending(i => i.CreatedAt)
            .Take(limit)
            .Select(i => new InspectionSummary(i.Id, i.SiteId, i.Status, i.CreatedAt, i.Warnings.Count, i.HighestSeverity))
            .ToList();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Inspection>> ListFullAsync(string? siteId, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<Inspection> result = _inspections.Values
            .Where(i => string.IsNullOrWhiteSpace(siteId) || i.SiteId == siteId)
            .OrderByDescending(i => i.CreatedAt)
            .Take(limit)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}

/// <summary>Kö i minnet för lokal körning.</summary>
public class InMemoryInspectionQueue : IInspectionQueue
{
    private readonly ConcurrentQueue<InspectionJob> _queue = new();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnqueueAsync(InspectionJob job, CancellationToken cancellationToken)
    {
        _queue.Enqueue(job);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueuedJob>> DequeueAsync(
        int maxMessages, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        var jobs = new List<QueuedJob>();
        while (jobs.Count < maxMessages && _queue.TryDequeue(out var job))
        {
            jobs.Add(new QueuedJob(job, Guid.NewGuid().ToString("N"), "local", 1));
        }

        return Task.FromResult<IReadOnlyList<QueuedJob>>(jobs);
    }

    public Task CompleteAsync(QueuedJob job, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<int> GetApproximateLengthAsync(CancellationToken cancellationToken) => Task.FromResult(_queue.Count);

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}

/// <summary>
/// Fejkad bildanalys för lokal utveckling och demo. Returnerar ett rimligt
/// byggplatsresultat så att regelmotorn kan köras utan att kosta pengar.
/// Varierar svaret utifrån filstorleken så att man får se både varningar och godkänt.
/// </summary>
public class FakeVisionAnalyzer : IVisionAnalyzer
{
    private readonly ILogger<FakeVisionAnalyzer> _logger;

    public FakeVisionAnalyzer(ILogger<FakeVisionAnalyzer> logger)
    {
        _logger = logger;
        _logger.LogWarning(
            "FakeVisionAnalyzer är aktiv — inga riktiga anrop görs mot Azure Computer Vision. " +
            "Sätt Vision:UseFake till false i produktion.");
    }

    public async Task<VisionAnalysisResult> AnalyzeAsync(Stream image, string contentType, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, cancellationToken);
        await Task.Delay(200, cancellationToken);

        // Enkel pseudoslump baserad på bildens storlek, så att samma bild ger samma svar.
        var variant = buffer.Length % 3;

        var result = new VisionAnalysisResult
        {
            Transactions = 3,
            DurationMs = 200,
            Caption = "a construction site with workers"
        };

        result.Objects.Add(new DetectedTag("person", 0.93));
        result.PeopleCount = 1;
        result.Tags.Add(new DetectedTag("construction site", 0.95));
        result.Tags.Add(new DetectedTag("outdoor", 0.91));

        switch (variant)
        {
            case 0:
                // Allt i sin ordning.
                result.Tags.Add(new DetectedTag("helmet", 0.88));
                result.Tags.Add(new DetectedTag("safety vest", 0.84));
                result.Tags.Add(new DetectedTag("work boots", 0.71));
                break;

            case 1:
                // Hjälm saknas.
                result.Tags.Add(new DetectedTag("safety vest", 0.86));
                result.Tags.Add(new DetectedTag("work boots", 0.68));
                result.Tags.Add(new DetectedTag("ladder", 0.82));
                break;

            default:
                // Både hjälm och väst saknas, plus en riskindikator.
                result.Tags.Add(new DetectedTag("scaffolding", 0.79));
                break;
        }

        return result;
    }

    public Task<bool> CanReachServiceAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}
