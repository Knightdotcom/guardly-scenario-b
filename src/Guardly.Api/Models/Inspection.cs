namespace Guardly.Api.Models;

/// <summary>
/// Var i livscykeln en inspektion befinner sig.
/// Eftersom bildanalysen körs asynkront i bakgrunden hinner en inspektion
/// vara "Queued" en kort stund innan den blir "Completed".
/// </summary>
public enum InspectionStatus
{
    /// <summary>Bilden är uppladdad och ligger på kön, analysen har inte börjat.</summary>
    Queued,

    /// <summary>En bakgrundsworker håller på att analysera bilden just nu.</summary>
    Processing,

    /// <summary>Analysen är klar och resultatet finns i Blob Storage.</summary>
    Completed,

    /// <summary>Analysen misslyckades. Fältet <see cref="Inspection.Error"/> säger varför.</summary>
    Failed
}

/// <summary>Hur allvarlig en varning är. Styr färgen i Guardlys rapportvy.</summary>
public enum WarningSeverity
{
    Info,
    Medium,
    High
}

/// <summary>En tagg eller ett objekt som Azure Computer Vision hittade i bilden.</summary>
/// <param name="Name">Taggens namn, t.ex. "person" eller "helmet".</param>
/// <param name="Confidence">Konfidenspoäng 0.0–1.0 från Computer Vision.</param>
public record DetectedTag(string Name, double Confidence);

/// <summary>
/// En flaggad avvikelse. Det är Guardlys egen regelmotor som skapar de här,
/// Computer Vision levererar bara taggarna som reglerna tittar på.
/// </summary>
/// <param name="Code">Maskinläsbar kod, t.ex. MISSING_HELMET.</param>
/// <param name="Message">Text som visas för platschefen.</param>
/// <param name="Severity">Allvarlighetsgrad.</param>
/// <param name="Confidence">Hur säker regeln är, härledd ur Computer Visions konfidenspoäng.</param>
public record SafetyWarning(string Code, string Message, WarningSeverity Severity, double Confidence);

/// <summary>
/// Ett inspektionsdokument. Sparas som en egen JSON-blob i containern "inspections"
/// under sökvägen {siteId}/{id}.json — ett dokument per post, enligt kravspecen.
/// </summary>
public class Inspection
{
    /// <summary>Inspektionens ID (GUID utan bindestreck).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Arbetsplatsens identifierare, t.ex. "kvarteret-vallgatan". Används som blob-prefix.</summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>Valfri zonbeteckning inom arbetsplatsen, t.ex. "Plan 3, östra gaveln".</summary>
    public string? Zone { get; set; }

    /// <summary>Vem som laddade upp bilden (fritext från klienten).</summary>
    public string? UploadedBy { get; set; }

    public InspectionStatus Status { get; set; } = InspectionStatus.Queued;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>När analysen blev klar. Null så länge status är Queued/Processing.</summary>
    public DateTimeOffset? AnalyzedAt { get; set; }

    /// <summary>Sökväg till originalbilden i containern "images".</summary>
    public string ImageBlobPath { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public long ImageSizeBytes { get; set; }

    /// <summary>Taggar från Computer Vision, sorterade på konfidens.</summary>
    public List<DetectedTag> Tags { get; set; } = new();

    /// <summary>Objekt från Computer Vision (objektdetektering med bounding box).</summary>
    public List<DetectedTag> Objects { get; set; } = new();

    /// <summary>Antal personer Computer Vision hittade i bilden.</summary>
    public int PeopleCount { get; set; }

    /// <summary>Varningar från Guardlys regelmotor.</summary>
    public List<SafetyWarning> Warnings { get; set; } = new();

    /// <summary>
    /// Computer Visions egen bildtext, t.ex. "a construction site with workers".
    /// Null om vi inte begärt featuren 'caption' — den kostar en extra transaktion
    /// och finns bara i vissa regioner.
    /// </summary>
    public string? Caption { get; set; }

    /// <summary>Hur lång tid själva Computer Vision-anropet tog. Bra för felsökning och kostnadsuppföljning.</summary>
    public int? AnalysisDurationMs { get; set; }

    /// <summary>Hur många Computer Vision-transaktioner den här inspektionen kostade (en per feature).</summary>
    public int VisionTransactions { get; set; }

    /// <summary>Felmeddelande om status är Failed.</summary>
    public string? Error { get; set; }

    /// <summary>Högsta allvarlighetsgraden bland varningarna. Null om inga varningar finns.</summary>
    public WarningSeverity? HighestSeverity =>
        Warnings.Count == 0 ? (WarningSeverity?)null : Warnings.Max(w => w.Severity);
}
