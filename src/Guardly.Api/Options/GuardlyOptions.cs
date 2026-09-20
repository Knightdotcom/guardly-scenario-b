namespace Guardly.Api.Options;

/// <summary>
/// Inställningar för lagring. Observera att det inte finns någon connection string här —
/// vi anger bara kontonamnet och låter Managed Identity sköta autentiseringen.
/// </summary>
public class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Namnet på storage-kontot, t.ex. "stguardlyprod". Tomt = kör med in-memory-lagring lokalt.</summary>
    public string AccountName { get; set; } = string.Empty;

    /// <summary>Container för originalbilderna.</summary>
    public string ImageContainer { get; set; } = "images";

    /// <summary>Container för inspektionsdokumenten (en JSON-blob per inspektion).</summary>
    public string InspectionContainer { get; set; } = "inspections";

    /// <summary>Kön som bakgrundsworkern läser jobb ifrån.</summary>
    public string QueueName { get; set; } = "inspection-jobs";

    /// <summary>Sätts till true lokalt för att köra helt utan Azure.</summary>
    public bool UseInMemory { get; set; }
}

/// <summary>Inställningar för Azure Computer Vision.</summary>
public class VisionOptions
{
    public const string SectionName = "Vision";

    /// <summary>
    /// Endpoint till Computer Vision-resursen, t.ex. https://cv-iths-kurs.cognitiveservices.azure.com/
    /// Sätts som miljövariabel av pipelinen. Ingen nyckel behövs — vi använder Managed Identity.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API-version för Image Analysis 4.0.</summary>
    public string ApiVersion { get; set; } = "2024-02-01";

    /// <summary>
    /// Vilka features vi ber om. VIKTIGT FÖR KOSTNADEN: Azure debiterar en transaktion
    /// per feature, så tre features = tre transaktioner per bild.
    /// </summary>
    public string Features { get; set; } = "tags,objects,people";

    /// <summary>Timeout per anrop mot Computer Vision.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Antal omförsök vid 429/5xx innan vi ger upp.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Sätts till true lokalt för att använda en fejkad analysator utan Azure.</summary>
    public bool UseFake { get; set; }
}

/// <summary>Trösklar och nyckelord för regelmotorn. Ligger i konfiguration så att Guardly kan justera utan ny deploy.</summary>
public class SafetyRuleOptions
{
    public const string SectionName = "SafetyRules";

    /// <summary>Lägsta konfidens för att vi ska lita på att en tagg faktiskt finns i bilden.</summary>
    public double MinimumConfidence { get; set; } = 0.55;

    /// <summary>Lägsta konfidens för att räkna en person som upptäckt.</summary>
    public double PersonConfidence { get; set; } = 0.60;

    public string[] HelmetKeywords { get; set; } = { "helmet", "hard hat", "hardhat", "safety helmet", "construction helmet" };

    public string[] VestKeywords { get; set; } = { "safety vest", "high visibility", "high-visibility", "hi-vis", "reflective vest", "vest" };

    public string[] BootKeywords { get; set; } = { "boot", "boots", "work boots", "safety shoes", "steel toe" };

    /// <summary>Taggar som indikerar en riskzon i bilden.</summary>
    public string[] HazardKeywords { get; set; } = { "ladder", "scaffolding", "scaffold", "crane", "excavator", "trench", "opening", "forklift", "cable", "fire" };
}

/// <summary>Inställningar för bakgrundsworkern som tömmer kön.</summary>
public class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>Ska den här instansen köra workern? Sätts till false om man vill dela upp API och worker i två Container Apps.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Hur många bilder vi analyserar samtidigt per replica.
    /// Håll den låg — Computer Vision S1 tar bara 10 transaktioner/sekund som standard,
    /// och varje bild kostar en transaktion per feature.
    /// </summary>
    public int MaxConcurrentAnalyses { get; set; } = 3;

    /// <summary>Hur många meddelanden vi hämtar per pollning (max 32 enligt Azure).</summary>
    public int BatchSize { get; set; } = 8;

    /// <summary>Kortaste väntetiden mellan pollningar när kön har jobb.</summary>
    public int MinPollDelaySeconds { get; set; } = 1;

    /// <summary>
    /// Längsta väntetiden när kön är tom. Vi backar av upp till den här nivån
    /// så att replicorna får räknas som "idle" av Azure — idle-priset är åtta gånger lägre.
    /// </summary>
    public int MaxPollDelaySeconds { get; set; } = 30;

    /// <summary>Hur många gånger ett meddelande får misslyckas innan vi ger upp och markerar inspektionen som Failed.</summary>
    public int MaxDequeueCount { get; set; } = 5;
}

/// <summary>Allmänna API-inställningar.</summary>
public class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>Största tillåtna bildstorlek. Computer Vision 4.0 klarar 20 MB.</summary>
    public int MaxImageSizeMb { get; set; } = 20;

    /// <summary>Tillåtna filändelser.</summary>
    public string[] AllowedExtensions { get; set; } = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
}
