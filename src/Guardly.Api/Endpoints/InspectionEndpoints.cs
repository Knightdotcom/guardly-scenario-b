using System.Text.RegularExpressions;
using Guardly.Api.Models;
using Guardly.Api.Options;
using Guardly.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Guardly.Api.Endpoints;

/// <summary>
/// Guardlys publika API. Alla endpoints är taggade och dokumenterade med Produces
/// så att Swagger visar rätt statuskoder och modeller.
/// </summary>
public static class InspectionEndpoints
{
    private const string Tag = "Inspektioner";

    public static void MapInspectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/inspections").WithTags(Tag);

        // ------------------------------------------------------------------
        // POST /inspections — ta emot bild, returnera inspektions-ID
        // ------------------------------------------------------------------
        group.MapPost("/", UploadAsync)
            .WithName("CreateInspection")
            .WithTags(Tag)
            .WithSummary("Ladda upp en bild för säkerhetsanalys")
            .WithDescription(
                "Tar emot en bild som multipart/form-data. Fältet 'image' är filen, 'siteId' är " +
                "arbetsplatsen och 'zone' är valfri zonbeteckning.\n\n" +
                "Som standard läggs bilden på kö och svaret blir 202 Accepted med ett inspektions-ID. " +
                "Skicka ?sync=true om du vill vänta in analysen och få resultatet direkt — då " +
                "returneras även eventuella fel från Computer Vision med rätt statuskod.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<InspectionAcceptedResponse>(StatusCodes.Status202Accepted)
            .Produces<Inspection>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .DisableAntiforgery();

        // ------------------------------------------------------------------
        // GET /inspections/{id} — hämta analysresultat
        // ------------------------------------------------------------------
        group.MapGet("/{id}", GetAsync)
            .WithName("GetInspection")
            .WithTags(Tag)
            .WithSummary("Hämta en inspektion med taggar, konfidenspoäng och varningar")
            .WithDescription(
                "Returnerar hela inspektionsdokumentet. Är status Queued eller Processing är " +
                "taggar och varningar fortfarande tomma — polla igen om någon sekund. " +
                "Ange siteId som query-parameter för snabbare uppslag.")
            .Produces<Inspection>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // ------------------------------------------------------------------
        // GET /inspections — lista, filtrerbart per plats
        // ------------------------------------------------------------------
        group.MapGet("/", ListAsync)
            .WithName("ListInspections")
            .WithTags(Tag)
            .WithSummary("Lista inspektioner, filtrerbart per arbetsplats och status")
            .WithDescription(
                "Utan filter listas de senaste inspektionerna över alla arbetsplatser. " +
                "Med ?siteId=... listas bara den arbetsplatsen, vilket är betydligt billigare " +
                "eftersom blobbarna är partitionerade på arbetsplats.")
            .Produces<InspectionListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // ------------------------------------------------------------------
        // GET /inspections/{id}/image — hämta originalbilden
        // ------------------------------------------------------------------
        group.MapGet("/{id}/image", GetImageAsync)
            .WithName("GetInspectionImage")
            .WithTags(Tag)
            .WithSummary("Hämta originalbilden för en inspektion")
            .WithDescription("Strömmar originalbilden från Blob Storage. Containern är privat, så det här är enda vägen in.")
            .Produces<IResult>(StatusCodes.Status200OK, "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // ======================================================================
    // Implementationer
    // ======================================================================

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        [FromQuery] bool? sync,
        IInspectionStore store,
        IInspectionQueue queue,
        InspectionAnalysisService analysisService,
        IOptions<ApiOptions> apiOptions,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        var api = apiOptions.Value;

        if (!request.HasFormContentType)
        {
            return Problem(
                StatusCodes.Status415UnsupportedMediaType,
                "Fel innehållstyp",
                "Bilden ska skickas som multipart/form-data med fältet 'image'.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files["image"] ?? form.Files.FirstOrDefault();

        if (file is null || file.Length == 0)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Ingen bild bifogad",
                "Lägg till en bildfil i formulärfältet 'image'.");
        }

        var maxBytes = (long)api.MaxImageSizeMb * 1024 * 1024;
        if (file.Length > maxBytes)
        {
            return Problem(
                StatusCodes.Status413PayloadTooLarge,
                "Bilden är för stor",
                $"Bilden är {file.Length / 1024 / 1024} MB. Största tillåtna storlek är {api.MaxImageSizeMb} MB.");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!api.AllowedExtensions.Contains(extension))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Filformatet stöds inte",
                $"'{extension}' stöds inte. Tillåtna format: {string.Join(", ", api.AllowedExtensions)}.");
        }

        var rawSiteId = form["siteId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawSiteId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "siteId saknas",
                "Ange vilken arbetsplats bilden hör till i formulärfältet 'siteId'.");
        }

        var siteId = NormalizeSiteId(rawSiteId);
        if (siteId.Length == 0)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Ogiltigt siteId",
                "siteId får bara innehålla bokstäver, siffror och bindestreck.");
        }

        var inspection = new Inspection
        {
            Id = Guid.NewGuid().ToString("N"),
            SiteId = siteId,
            Zone = form["zone"].FirstOrDefault(),
            UploadedBy = form["uploadedBy"].FirstOrDefault(),
            Status = InspectionStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            OriginalFileName = Path.GetFileName(file.FileName),
            ImageSizeBytes = file.Length
        };

        await using (var stream = file.OpenReadStream())
        {
            inspection.ImageBlobPath = await store.SaveImageAsync(
                siteId, inspection.Id, file.FileName, file.ContentType, stream, cancellationToken);
        }

        await store.SaveAsync(inspection, cancellationToken);

        logger.LogInformation(
            "Inspektion {InspectionId} skapad för {SiteId} ({SizeKb} kB, sync={Sync})",
            inspection.Id, siteId, inspection.ImageSizeBytes / 1024, sync);

        // ---- Synkront läge: analysera direkt och låt eventuella fel slå igenom till klienten ----
        if (sync == true)
        {
            try
            {
                var analyzed = await analysisService.AnalyzeAsync(inspection, cancellationToken);
                return Results.Ok(analyzed);
            }
            catch (VisionServiceException ex)
            {
                // Kravet på tydlig felhantering mot Azure-tjänsten: rätt statuskod,
                // begripligt meddelande och en loggrad med den bakomliggande koden.
                logger.LogError(ex,
                    "Computer Vision-fel för inspektion {InspectionId}: upstream {UpstreamStatus}",
                    inspection.Id, ex.UpstreamStatusCode);

                await analysisService.MarkFailedAsync(inspection, $"{ex.Title}: {ex.Message}", cancellationToken);

                // ProblemDetails enligt RFC 9457, med extrafält så att klienten både
                // vet vilken inspektion det gäller och vad Azure faktiskt svarade.
                var problem = new ProblemDetails
                {
                    Title = ex.Title,
                    Detail = ex.Message,
                    Status = ex.ApiStatusCode
                };

                problem.Extensions["inspectionId"] = inspection.Id;
                problem.Extensions["upstreamStatusCode"] = ex.UpstreamStatusCode;

                if (ex.RetryAfterSeconds.HasValue)
                {
                    problem.Extensions["retryAfterSeconds"] = ex.RetryAfterSeconds.Value;
                }

                return Results.Problem(problem);
            }
        }

        // ---- Asynkront läge (standard): lägg på kön och svara direkt ----
        await queue.EnqueueAsync(
            new InspectionJob(inspection.Id, inspection.SiteId, inspection.ImageBlobPath), cancellationToken);

        var response = new InspectionAcceptedResponse(
            inspection.Id,
            inspection.Status,
            inspection.SiteId,
            $"/inspections/{inspection.Id}?siteId={inspection.SiteId}",
            EstimatedReadyInSeconds: 5);

        return Results.Accepted($"/inspections/{inspection.Id}", response);
    }

    private static async Task<IResult> GetAsync(
        string id,
        [FromQuery] string? siteId,
        IInspectionStore store,
        CancellationToken cancellationToken)
    {
        var inspection = await store.GetAsync(id, NormalizeSiteIdOrNull(siteId), cancellationToken);

        if (inspection is null)
        {
            return Problem(
                StatusCodes.Status404NotFound,
                "Inspektionen hittades inte",
                $"Det finns ingen inspektion med id {id}.");
        }

        return Results.Ok(inspection);
    }

    private static async Task<IResult> ListAsync(
        [FromQuery] string? siteId,
        [FromQuery] string? status,
        [FromQuery] int? limit,
        IInspectionStore store,
        CancellationToken cancellationToken)
    {
        InspectionStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<InspectionStatus>(status, ignoreCase: true, out var value))
            {
                return Problem(
                    StatusCodes.Status400BadRequest,
                    "Ogiltig status",
                    $"'{status}' är inte en giltig status. Giltiga värden: {string.Join(", ", Enum.GetNames<InspectionStatus>())}.");
            }

            parsedStatus = value;
        }

        var take = Math.Clamp(limit ?? 50, 1, 500);
        var normalizedSite = NormalizeSiteIdOrNull(siteId);

        var items = await store.ListAsync(normalizedSite, parsedStatus, take, cancellationToken);

        return Results.Ok(new InspectionListResponse(items.Count, normalizedSite, items));
    }

    private static async Task<IResult> GetImageAsync(
        string id,
        [FromQuery] string? siteId,
        IInspectionStore store,
        CancellationToken cancellationToken)
    {
        // Vi behöver arbetsplatsen för att hitta bilden. Saknas den slår vi upp inspektionen först.
        var site = NormalizeSiteIdOrNull(siteId);
        if (site is null)
        {
            var inspection = await store.GetAsync(id, null, cancellationToken);
            if (inspection is null)
            {
                return Problem(StatusCodes.Status404NotFound, "Inspektionen hittades inte", $"Inget id {id}.");
            }

            site = inspection.SiteId;
        }

        var image = await store.GetImageAsync(site!, id, cancellationToken);
        if (image is null)
        {
            return Problem(
                StatusCodes.Status404NotFound,
                "Bilden hittades inte",
                $"Originalbilden för inspektion {id} finns inte i Blob Storage.");
        }

        return Results.File(image.Content, image.ContentType, image.FileName);
    }

    // ======================================================================
    // Hjälpare
    // ======================================================================

    private static IResult Problem(int statusCode, string title, string detail) =>
        Results.Problem(title: title, detail: detail, statusCode: statusCode);

    /// <summary>
    /// Gör siteId säkert som blob-prefix: små bokstäver, bara a–z, 0–9 och bindestreck.
    /// Viktigt både för att slippa konstiga blob-namn och som skydd mot path traversal.
    /// </summary>
    private static string NormalizeSiteId(string value)
    {
        var lowered = value.Trim().ToLowerInvariant()
            .Replace('å', 'a').Replace('ä', 'a').Replace('ö', 'o')
            .Replace(' ', '-');

        return Regex.Replace(lowered, "[^a-z0-9-]", "").Trim('-');
    }

    private static string? NormalizeSiteIdOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = NormalizeSiteId(value);
        return normalized.Length == 0 ? null : normalized;
    }
}
