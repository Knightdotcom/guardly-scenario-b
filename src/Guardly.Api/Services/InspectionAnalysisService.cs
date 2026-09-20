using Guardly.Api.Models;
using Guardly.Api.Options;
using Microsoft.Extensions.Options;

namespace Guardly.Api.Services;

/// <summary>
/// Kopplar ihop lagring, Computer Vision och regelmotorn. Samma kod används både av
/// bakgrundsworkern och av det synkrona läget (?sync=true), så analysen beter sig likadant.
/// </summary>
public class InspectionAnalysisService
{
    private readonly IInspectionStore _store;
    private readonly IVisionAnalyzer _vision;
    private readonly ISafetyRuleEngine _rules;
    private readonly StorageOptions _storage;
    private readonly ILogger<InspectionAnalysisService> _logger;

    public InspectionAnalysisService(
        IInspectionStore store,
        IVisionAnalyzer vision,
        ISafetyRuleEngine rules,
        IOptions<StorageOptions> storage,
        ILogger<InspectionAnalysisService> logger)
    {
        _store = store;
        _vision = vision;
        _rules = rules;
        _storage = storage.Value;
        _logger = logger;
    }

    /// <summary>
    /// Kör hela analyskedjan för en inspektion: hämta bilden, anropa Computer Vision,
    /// kör reglerna och spara resultatet.
    /// Kastar <see cref="VisionServiceException"/> vidare så att anroparen kan bestämma
    /// om felet ska bli ett HTTP-svar eller ett omförsök på kön.
    /// </summary>
    public async Task<Inspection> AnalyzeAsync(Inspection inspection, CancellationToken cancellationToken)
    {
        var image = await _store.GetImageAsync(inspection.SiteId, inspection.Id, cancellationToken);
        if (image is null)
        {
            throw new InvalidOperationException(
                $"Originalbilden för inspektion {inspection.Id} saknas i containern {_storage.ImageContainer}.");
        }

        await using (image.Content)
        {
            inspection.Status = InspectionStatus.Processing;
            await _store.SaveAsync(inspection, cancellationToken);

            var analysis = await _vision.AnalyzeAsync(image.Content, image.ContentType, cancellationToken);

            inspection.Tags = analysis.Tags;
            inspection.Objects = analysis.Objects;
            inspection.PeopleCount = _rules.CountConfidentPeople(analysis);
            inspection.Caption = analysis.Caption;
            inspection.Warnings = _rules.Evaluate(analysis).ToList();
            inspection.AnalysisDurationMs = analysis.DurationMs;
            inspection.VisionTransactions = analysis.Transactions;
            inspection.AnalyzedAt = DateTimeOffset.UtcNow;
            inspection.Status = InspectionStatus.Completed;
            inspection.Error = null;
        }

        await _store.SaveAsync(inspection, cancellationToken);

        _logger.LogInformation(
            "Inspektion {InspectionId} klar för {SiteId}: {PeopleCount} personer, {WarningCount} varningar",
            inspection.Id, inspection.SiteId, inspection.PeopleCount, inspection.Warnings.Count);

        return inspection;
    }

    /// <summary>Markerar en inspektion som misslyckad och sparar felorsaken så att den syns i API:et.</summary>
    public async Task MarkFailedAsync(Inspection inspection, string error, CancellationToken cancellationToken)
    {
        inspection.Status = InspectionStatus.Failed;
        inspection.Error = error;
        inspection.AnalyzedAt = DateTimeOffset.UtcNow;
        await _store.SaveAsync(inspection, cancellationToken);
    }
}
