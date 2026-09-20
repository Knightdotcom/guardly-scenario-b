using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Guardly.Api.Models;
using Guardly.Api.Options;
using Microsoft.Extensions.Options;

namespace Guardly.Api.Services;

/// <summary>
/// Lagrar inspektioner i Azure Blob Storage. Ett JSON-dokument per inspektion,
/// enligt kravet i uppgiften.
///
/// Namngivningen är medvetet vald: {siteId}/{inspectionId}.json. Blob Storage har
/// ingen riktig mappstruktur, men den som listar kan filtrera på prefix. Det gör att
/// "lista alla inspektioner för arbetsplats X" blir ett enda billigt anrop i stället
/// för en scan över hela containern.
/// </summary>
public class BlobInspectionStore : IInspectionStore
{
    private readonly BlobContainerClient _images;
    private readonly BlobContainerClient _inspections;
    private readonly ILogger<BlobInspectionStore> _logger;
    private readonly JsonSerializerOptions _json;

    public BlobInspectionStore(
        BlobServiceClient blobServiceClient,
        IOptions<StorageOptions> options,
        JsonSerializerOptions jsonOptions,
        ILogger<BlobInspectionStore> logger)
    {
        var settings = options.Value;
        _images = blobServiceClient.GetBlobContainerClient(settings.ImageContainer);
        _inspections = blobServiceClient.GetBlobContainerClient(settings.InspectionContainer);
        _json = jsonOptions;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // PublicAccessType.None — containrarna ska aldrig vara publika.
        await _images.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        await _inspections.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        _logger.LogInformation("Blob-containrar redo: {Images}, {Inspections}", _images.Name, _inspections.Name);
    }

    public async Task<string> SaveImageAsync(
        string siteId, string inspectionId, string fileName, string contentType,
        Stream content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
        }

        var path = $"{siteId}/{inspectionId}{extension.ToLowerInvariant()}";
        var blob = _images.GetBlobClient(path);

        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Metadata = new Dictionary<string, string>
            {
                // Blob-metadata måste vara ren ASCII, därför kodar vi filnamnet.
                ["inspectionId"] = inspectionId,
                ["siteId"] = siteId,
                ["originalName"] = ToAsciiSafe(fileName)
            }
        }, cancellationToken);

        _logger.LogInformation("Originalbild sparad: {Path}", path);
        return path;
    }

    public async Task<StoredImage?> GetImageAsync(string siteId, string inspectionId, CancellationToken cancellationToken)
    {
        // Vi vet inte filändelsen, så vi listar på prefix — max en träff.
        var prefix = $"{siteId}/{inspectionId}";
        await foreach (var item in _images.GetBlobsAsync(
            BlobTraits.Metadata, BlobStates.None, prefix, cancellationToken))
        {
            var blob = _images.GetBlobClient(item.Name);
            var download = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);

            var contentType = download.Value.Details.ContentType ?? "application/octet-stream";
            var fileName = item.Metadata.TryGetValue("originalName", out var name)
                ? name
                : Path.GetFileName(item.Name);

            return new StoredImage(download.Value.Content, contentType, fileName);
        }

        return null;
    }

    public async Task SaveAsync(Inspection inspection, CancellationToken cancellationToken)
    {
        var path = BlobPath(inspection.SiteId, inspection.Id);
        var blob = _inspections.GetBlobClient(path);

        var json = JsonSerializer.Serialize(inspection, _json);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" },
            // Metadata gör att GET /inspections kan bygga listan utan att ladda ner varje dokument.
            // Allt här måste vara ASCII — därför ingen zon och inget filnamn.
            Metadata = new Dictionary<string, string>
            {
                ["siteId"] = inspection.SiteId,
                ["status"] = inspection.Status.ToString(),
                ["created"] = inspection.CreatedAt.ToString("O"),
                ["warnings"] = inspection.Warnings.Count.ToString(),
                ["severity"] = inspection.HighestSeverity?.ToString() ?? "None"
            }
        }, cancellationToken);
    }

    public async Task<Inspection?> GetAsync(string id, string? siteId, CancellationToken cancellationToken)
    {
        // Snabbvägen: vet vi arbetsplatsen kan vi gå direkt på bloben.
        if (!string.IsNullOrWhiteSpace(siteId))
        {
            return await ReadAsync(BlobPath(siteId, id), cancellationToken);
        }

        // Annars får vi leta. Containern är partitionerad på siteId, så vi listar
        // och matchar på filnamn. Vid riktigt stora volymer skulle vi lägga ett
        // index i Table Storage i stället — se ARCHITECTURE.md.
        await foreach (var item in _inspections.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            if (item.Name.EndsWith($"/{id}.json", StringComparison.OrdinalIgnoreCase))
            {
                return await ReadAsync(item.Name, cancellationToken);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<InspectionSummary>> ListAsync(
        string? siteId, InspectionStatus? status, int limit, CancellationToken cancellationToken)
    {
        var prefix = string.IsNullOrWhiteSpace(siteId) ? null : $"{siteId}/";
        var results = new List<InspectionSummary>();

        await foreach (var item in _inspections.GetBlobsAsync(
            BlobTraits.Metadata, BlobStates.None, prefix, cancellationToken))
        {
            var summary = ToSummary(item);
            if (summary is null)
            {
                continue;
            }

            if (status.HasValue && summary.Status != status.Value)
            {
                continue;
            }

            results.Add(summary);
        }

        return results
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<Inspection>> ListFullAsync(string? siteId, int limit, CancellationToken cancellationToken)
    {
        var prefix = string.IsNullOrWhiteSpace(siteId) ? null : $"{siteId}/";
        var names = new List<string>();

        await foreach (var item in _inspections.GetBlobsAsync(
            BlobTraits.Metadata, BlobStates.None, prefix, cancellationToken))
        {
            names.Add(item.Name);
            if (names.Count >= limit)
            {
                break;
            }
        }

        var results = new List<Inspection>();
        foreach (var name in names)
        {
            var inspection = await ReadAsync(name, cancellationToken);
            if (inspection is not null)
            {
                results.Add(inspection);
            }
        }

        return results;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _inspections.ExistsAsync(cancellationToken);
            return response.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness: Blob Storage svarar inte");
            return false;
        }
    }

    private async Task<Inspection?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var blob = _inspections.GetBlobClient(path);
            var download = await blob.DownloadContentAsync(cancellationToken);
            return JsonSerializer.Deserialize<Inspection>(download.Value.Content.ToString(), _json);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static InspectionSummary? ToSummary(BlobItem item)
    {
        var id = Path.GetFileNameWithoutExtension(item.Name);
        var metadata = item.Metadata ?? new Dictionary<string, string>();

        var siteId = metadata.TryGetValue("siteId", out var s) ? s : item.Name.Split('/')[0];

        var status = metadata.TryGetValue("status", out var st)
                     && Enum.TryParse<InspectionStatus>(st, out var parsedStatus)
            ? parsedStatus
            : InspectionStatus.Completed;

        var created = metadata.TryGetValue("created", out var c)
                      && DateTimeOffset.TryParse(c, out var parsedCreated)
            ? parsedCreated
            : item.Properties.CreatedOn ?? DateTimeOffset.MinValue;

        var warnings = metadata.TryGetValue("warnings", out var w) && int.TryParse(w, out var parsedWarnings)
            ? parsedWarnings
            : 0;

        var severity = metadata.TryGetValue("severity", out var sev)
                       && Enum.TryParse<WarningSeverity>(sev, out var parsedSeverity)
            ? parsedSeverity
            : (WarningSeverity?)null;

        return new InspectionSummary(id, siteId, status, created, warnings, severity);
    }

    private static string BlobPath(string siteId, string id) => $"{siteId}/{id}.json";

    /// <summary>Blob-metadata får bara innehålla ASCII. Å, Ä och Ö byts ut.</summary>
    private static string ToAsciiSafe(string value)
    {
        var chars = value
            .Replace("å", "a").Replace("ä", "a").Replace("ö", "o")
            .Replace("Å", "A").Replace("Ä", "A").Replace("Ö", "O")
            .Where(c => c < 128)
            .ToArray();

        var result = new string(chars);
        return string.IsNullOrWhiteSpace(result) ? "bild" : result;
    }
}
