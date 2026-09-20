using Guardly.Api.Models;

namespace Guardly.Api.Services;

/// <summary>Resultatet av att hämta en originalbild ur Blob Storage.</summary>
/// <param name="Content">Bildens bytes.</param>
/// <param name="ContentType">MIME-typ, t.ex. image/jpeg.</param>
/// <param name="FileName">Ursprungligt filnamn.</param>
public record StoredImage(Stream Content, string ContentType, string FileName);

/// <summary>
/// Lagringslagret. All persistens går genom det här gränssnittet, vilket gör att
/// vi kan köra lokalt mot minnet och i Azure mot Blob Storage utan att röra endpointerna.
/// </summary>
public interface IInspectionStore
{
    /// <summary>Skapar containrar och kö om de saknas. Körs en gång vid uppstart.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Sparar originalbilden och returnerar dess blob-sökväg.</summary>
    Task<string> SaveImageAsync(
        string siteId, string inspectionId, string fileName, string contentType,
        Stream content, CancellationToken cancellationToken);

    /// <summary>Hämtar originalbilden.</summary>
    Task<StoredImage?> GetImageAsync(string siteId, string inspectionId, CancellationToken cancellationToken);

    /// <summary>Skriver inspektionsdokumentet som en JSON-blob. Ett dokument per post.</summary>
    Task SaveAsync(Inspection inspection, CancellationToken cancellationToken);

    /// <summary>Hämtar ett inspektionsdokument. Returnerar null om det inte finns.</summary>
    Task<Inspection?> GetAsync(string id, string? siteId, CancellationToken cancellationToken);

    /// <summary>Listar inspektioner, valfritt filtrerat på arbetsplats.</summary>
    Task<IReadOnlyList<InspectionSummary>> ListAsync(
        string? siteId, InspectionStatus? status, int limit, CancellationToken cancellationToken);

    /// <summary>Hämtar hela dokument för statistik. Begränsad till <paramref name="limit"/> poster.</summary>
    Task<IReadOnlyList<Inspection>> ListFullAsync(string? siteId, int limit, CancellationToken cancellationToken);

    /// <summary>Snabb kontroll att lagringen svarar. Används av /health/ready.</summary>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}

/// <summary>Kön som kopplar ihop uppladdning med bakgrundsanalys.</summary>
public interface IInspectionQueue
{
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Lägger ett analysjobb på kön.</summary>
    Task EnqueueAsync(InspectionJob job, CancellationToken cancellationToken);

    /// <summary>Hämtar upp till <paramref name="maxMessages"/> jobb och döljer dem för andra workers.</summary>
    Task<IReadOnlyList<QueuedJob>> DequeueAsync(int maxMessages, TimeSpan visibilityTimeout, CancellationToken cancellationToken);

    /// <summary>Tar bort ett färdigbehandlat jobb.</summary>
    Task CompleteAsync(QueuedJob job, CancellationToken cancellationToken);

    /// <summary>Ungefärligt antal meddelanden i kön. Används av /health/ready och för larm.</summary>
    Task<int> GetApproximateLengthAsync(CancellationToken cancellationToken);

    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}

/// <summary>Ett jobb som hämtats från kön, med den kvittens som krävs för att ta bort det.</summary>
/// <param name="Job">Själva nyttolasten.</param>
/// <param name="MessageId">Meddelandets id i kön.</param>
/// <param name="PopReceipt">Kvittens som bevisar att vi äger meddelandet just nu.</param>
/// <param name="DequeueCount">Hur många gånger meddelandet hämtats. Styr vår poison-hantering.</param>
public record QueuedJob(InspectionJob Job, string MessageId, string PopReceipt, long DequeueCount);
