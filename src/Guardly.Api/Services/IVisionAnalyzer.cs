using Guardly.Api.Models;

namespace Guardly.Api.Services;

/// <summary>Det råa resultatet från Computer Vision, översatt till våra egna typer.</summary>
public class VisionAnalysisResult
{
    public List<DetectedTag> Tags { get; set; } = new();
    public List<DetectedTag> Objects { get; set; } = new();
    public int PeopleCount { get; set; }
    public string? Caption { get; set; }

    /// <summary>Hur många debiterbara transaktioner anropet kostade (en per efterfrågad feature).</summary>
    public int Transactions { get; set; }

    public int DurationMs { get; set; }
}

/// <summary>
/// Abstraktion över Azure Computer Vision. Gör att vi kan enhetstesta regelmotorn
/// och köra lokalt utan Azure genom att byta implementation.
/// </summary>
public interface IVisionAnalyzer
{
    Task<VisionAnalysisResult> AnalyzeAsync(Stream image, string contentType, CancellationToken cancellationToken);

    /// <summary>Lättviktigt anrop för readiness-kontrollen.</summary>
    Task<bool> CanReachServiceAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Kastas när Computer Vision svarar med fel. Bär med sig vilken HTTP-status
/// vårt eget API ska svara med, så att felet blir tydligt för klienten.
/// </summary>
public class VisionServiceException : Exception
{
    /// <summary>Statuskoden Computer Vision gav oss.</summary>
    public int UpstreamStatusCode { get; }

    /// <summary>Statuskoden Guardlys API ska svara klienten med.</summary>
    public int ApiStatusCode { get; }

    /// <summary>Kort rubrik till felsvaret.</summary>
    public string Title { get; }

    /// <summary>Sätts vid 429 så att klienten vet hur länge den ska vänta.</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>Går det att lyckas om vi försöker igen? Styr om workern lägger tillbaka jobbet på kön.</summary>
    public bool IsTransient { get; }

    public VisionServiceException(
        int upstreamStatusCode,
        int apiStatusCode,
        string title,
        string message,
        bool isTransient,
        int? retryAfterSeconds = null,
        Exception? inner = null)
        : base(message, inner)
    {
        UpstreamStatusCode = upstreamStatusCode;
        ApiStatusCode = apiStatusCode;
        Title = title;
        IsTransient = isTransient;
        RetryAfterSeconds = retryAfterSeconds;
    }
}
