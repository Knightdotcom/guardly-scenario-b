using Guardly.Api.Models;
using Guardly.Api.Options;
using Microsoft.Extensions.Options;

namespace Guardly.Api.Services;

/// <summary>
/// Regelmotorn som gör om Computer Visions taggar till Guardly-varningar.
/// Det här är affärslogiken — Azure levererar råa taggar, vi avgör vad som är farligt.
/// Motorn är helt fri från Azure-beroenden, vilket gör den lätt att enhetstesta.
/// </summary>
public interface ISafetyRuleEngine
{
    /// <summary>Kör alla regler mot ett analysresultat och returnerar de varningar som ska flaggas.</summary>
    IReadOnlyList<SafetyWarning> Evaluate(VisionAnalysisResult analysis);

    /// <summary>Räknar personer som Computer Vision hittade med tillräckligt hög konfidens.</summary>
    int CountConfidentPeople(VisionAnalysisResult analysis);
}

/// <inheritdoc />
public class SafetyRuleEngine : ISafetyRuleEngine
{
    // Koder som används både i API-svaret och i statistiken.
    public const string CodeNoPerson = "NO_PERSON_DETECTED";
    public const string CodeMissingHelmet = "MISSING_HELMET";
    public const string CodeMissingVest = "MISSING_VEST";
    public const string CodeMissingBoots = "MISSING_BOOTS";
    public const string CodeZoneHazard = "ZONE_HAZARD";
    public const string CodeLowQuality = "LOW_IMAGE_QUALITY";

    private readonly SafetyRuleOptions _options;

    public SafetyRuleEngine(IOptions<SafetyRuleOptions> options)
    {
        _options = options.Value;
    }

    public int CountConfidentPeople(VisionAnalysisResult analysis)
    {
        // Räkna "person"-objekt som ligger över tröskeln.
        var fromObjects = analysis.Objects
            .Count(o => IsMatch(o.Name, "person") && o.Confidence >= _options.PersonConfidence);

        // Fallback: om objektdetekteringen inte gav något men taggarna säger "person"
        // litar vi på det — bättre att granska en bild för mycket än en för lite.
        if (fromObjects == 0 && analysis.Tags.Any(t => IsMatch(t.Name, "person") && t.Confidence >= _options.MinimumConfidence))
        {
            return 1;
        }

        return fromObjects;
    }

    public IReadOnlyList<SafetyWarning> Evaluate(VisionAnalysisResult analysis)
    {
        var warnings = new List<SafetyWarning>();

        // Alla signaler i en hög — regler bryr sig inte om det var en tagg eller ett objekt.
        var signals = analysis.Tags
            .Concat(analysis.Objects)
            .Where(s => s.Confidence >= _options.MinimumConfidence)
            .ToList();

        var peopleCount = CountConfidentPeople(analysis);

        // Regel 0: väldigt få taggar betyder oftast suddig eller mörk bild.
        if (analysis.Tags.Count > 0 && analysis.Tags.Count(t => t.Confidence >= _options.MinimumConfidence) <= 1)
        {
            warnings.Add(new SafetyWarning(
                CodeLowQuality,
                "Bilden gav få säkra träffar. Ta om bilden i bättre ljus eller närmare motivet.",
                WarningSeverity.Info,
                0.5));
        }

        // Regel 1: ingen person i bilden — då går det inte att bedöma skyddsutrustning.
        if (peopleCount == 0)
        {
            warnings.Add(new SafetyWarning(
                CodeNoPerson,
                "Ingen person syns i bilden, så skyddsutrustning kunde inte bedömas.",
                WarningSeverity.Info,
                0.9));
        }
        else
        {
            // Regel 2–4: personer finns, men saknas skyddsutrustningen?
            AddMissingPpeWarning(
                warnings, signals, _options.HelmetKeywords, CodeMissingHelmet,
                peopleCount == 1
                    ? "Personen i bilden bär ingen synlig hjälm."
                    : $"Ingen hjälm syns på någon av de {peopleCount} personerna i bilden.",
                WarningSeverity.High);

            AddMissingPpeWarning(
                warnings, signals, _options.VestKeywords, CodeMissingVest,
                peopleCount == 1
                    ? "Personen i bilden bär ingen synlig varselväst."
                    : $"Ingen varselväst syns på någon av de {peopleCount} personerna i bilden.",
                WarningSeverity.High);

            AddMissingPpeWarning(
                warnings, signals, _options.BootKeywords, CodeMissingBoots,
                "Inga skyddsskor syns i bilden. Kontrollera manuellt — fötter hamnar ofta utanför bild.",
                WarningSeverity.Medium);
        }

        // Regel 5: riskindikatorer i miljön, oavsett om det finns personer i bilden.
        foreach (var hazard in FindHazards(signals))
        {
            warnings.Add(new SafetyWarning(
                CodeZoneHazard,
                $"Riskindikator i bilden: {hazard.Name}. Kontrollera avspärrning och fallskydd i zonen.",
                hazard.Confidence >= 0.8 ? WarningSeverity.Medium : WarningSeverity.Info,
                hazard.Confidence));
        }

        return warnings;
    }

    private void AddMissingPpeWarning(
        ICollection<SafetyWarning> warnings,
        IReadOnlyCollection<DetectedTag> signals,
        IEnumerable<string> keywords,
        string code,
        string message,
        WarningSeverity severity)
    {
        var best = BestMatch(signals, keywords);
        if (best is not null)
        {
            // Utrustningen syns — ingen varning.
            return;
        }

        // Konfidensen på en "saknas"-varning är hur säkra vi är på att vi INTE såg något.
        warnings.Add(new SafetyWarning(code, message, severity, 0.7));
    }

    private static DetectedTag? BestMatch(IEnumerable<DetectedTag> signals, IEnumerable<string> keywords)
    {
        var list = keywords.ToList();
        return signals
            .Where(s => list.Any(k => IsMatch(s.Name, k)))
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();
    }

    private IEnumerable<DetectedTag> FindHazards(IEnumerable<DetectedTag> signals)
    {
        // Dedupliceras på namn så att samma stege inte ger fem varningar.
        return signals
            .Where(s => _options.HazardKeywords.Any(k => IsMatch(s.Name, k)))
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(s => s.Confidence).First())
            .OrderByDescending(s => s.Confidence)
            .ToList();
    }

    /// <summary>
    /// Enkel ordmatchning. Computer Vision svarar på engelska, så nyckelorden är engelska.
    /// Vi matchar på hela ord för att slippa att "cap" träffar "capacity".
    /// </summary>
    private static bool IsMatch(string tagName, string keyword)
    {
        if (string.Equals(tagName, keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var words = tagName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        var keywordWords = keyword.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

        // Flerordsnyckelord ("hard hat") måste finnas som sammanhängande fras.
        if (keywordWords.Length > 1)
        {
            return tagName.Replace('-', ' ')
                .Contains(keyword.Replace('-', ' '), StringComparison.OrdinalIgnoreCase);
        }

        return words.Any(w => string.Equals(w, keyword, StringComparison.OrdinalIgnoreCase));
    }
}
