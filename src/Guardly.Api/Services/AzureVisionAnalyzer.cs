using System.Net;
using System.Text.Json;
using Azure.Core;
using Guardly.Api.Models;
using Guardly.Api.Options;
using Microsoft.Extensions.Options;

namespace Guardly.Api.Services;

/// <summary>
/// Anropar Azure Computer Vision (Image Analysis 4.0) över REST.
///
/// Varför REST och inte SDK:t? Två skäl. Dels blir beroendekedjan mindre, dels
/// blir det tydligt i koden exakt hur Managed Identity fungerar: vi hämtar en
/// OAuth-token för scopet https://cognitiveservices.azure.com/.default och
/// skickar den som en vanlig Bearer-header. Ingen nyckel finns någonstans.
/// </summary>
public class AzureVisionAnalyzer : IVisionAnalyzer
{
    private const string Scope = "https://cognitiveservices.azure.com/.default";

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly VisionOptions _options;
    private readonly ILogger<AzureVisionAnalyzer> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private AccessToken _cachedToken;

    public AzureVisionAnalyzer(
        HttpClient http,
        TokenCredential credential,
        IOptions<VisionOptions> options,
        ILogger<AzureVisionAnalyzer> logger)
    {
        _http = http;
        _credential = credential;
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            throw new InvalidOperationException(
                "Vision:Endpoint saknas. Sätt miljövariabeln Vision__Endpoint till Computer Vision-resursens URL.");
        }

        _http.BaseAddress = new Uri(_options.Endpoint.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    /// <summary>Antal features vi ber om — samma siffra som antalet debiterade transaktioner.</summary>
    private int FeatureCount =>
        _options.Features.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    public async Task<VisionAnalysisResult> AnalyzeAsync(Stream image, string contentType, CancellationToken cancellationToken)
    {
        // Läs in bilden i minnet. Vi kan behöva skicka samma bytes flera gånger vid omförsök,
        // och en HTTP-request "konsumerar" sin stream.
        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        var url = $"computervision/imageanalysis:analyze" +
                  $"?api-version={_options.ApiVersion}" +
                  $"&features={Uri.EscapeDataString(_options.Features)}";

        var started = DateTimeOffset.UtcNow;
        VisionServiceException? lastError = null;

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await GetTokenAsync(cancellationToken));

                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(NormalizeContentType(contentType));
                request.Content = content;

                using var response = await _http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var result = Parse(body);
                    result.Transactions = FeatureCount;
                    result.DurationMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;

                    _logger.LogInformation(
                        "Computer Vision svarade OK på {DurationMs} ms ({Transactions} transaktioner, försök {Attempt})",
                        result.DurationMs, result.Transactions, attempt);

                    return result;
                }

                lastError = MapError(response, body);

                if (!lastError.IsTransient || attempt == _options.MaxRetries)
                {
                    _logger.LogError(
                        "Computer Vision svarade {StatusCode} ({Title}). Ger upp efter {Attempt} försök. Svar: {Body}",
                        (int)response.StatusCode, lastError.Title, attempt, Truncate(body, 500));
                    throw lastError;
                }

                // Exponentiell backoff. Respektera Retry-After om Azure skickar med det.
                var waitSeconds = lastError.RetryAfterSeconds ?? (int)Math.Pow(2, attempt);
                _logger.LogWarning(
                    "Computer Vision svarade {StatusCode}. Försöker igen om {WaitSeconds} s (försök {Attempt} av {MaxRetries})",
                    (int)response.StatusCode, waitSeconds, attempt, _options.MaxRetries);

                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout, inte avbrott från oss.
                lastError = new VisionServiceException(
                    upstreamStatusCode: 0,
                    apiStatusCode: (int)HttpStatusCode.GatewayTimeout,
                    title: "Computer Vision svarade inte i tid",
                    message: $"Anropet mot Computer Vision tog längre än {_options.TimeoutSeconds} sekunder.",
                    isTransient: true,
                    inner: ex);

                if (attempt == _options.MaxRetries)
                {
                    _logger.LogError(ex, "Timeout mot Computer Vision efter {Attempt} försök", attempt);
                    throw lastError;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                lastError = new VisionServiceException(
                    upstreamStatusCode: 0,
                    apiStatusCode: (int)HttpStatusCode.ServiceUnavailable,
                    title: "Kunde inte nå Computer Vision",
                    message: "Nätverksfel mot Computer Vision-tjänsten.",
                    isTransient: true,
                    inner: ex);

                if (attempt == _options.MaxRetries)
                {
                    _logger.LogError(ex, "Nätverksfel mot Computer Vision efter {Attempt} försök", attempt);
                    throw lastError;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
        }

        throw lastError ?? new VisionServiceException(
            0, (int)HttpStatusCode.BadGateway, "Okänt fel", "Analysen misslyckades av okänd anledning.", false);
    }

    public async Task<bool> CanReachServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Räcker att vi får ut en token — då fungerar Managed Identity och endpointen är satt.
            await GetTokenAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness: kunde inte hämta token för Computer Vision");
            return false;
        }
    }

    /// <summary>
    /// Hämtar en token via Managed Identity och cachar den tills den snart går ut.
    /// Utan cache skulle vi göra ett extra anrop mot Entra ID för varje bild.
    /// </summary>
    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _cachedToken.Token;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return _cachedToken.Token;
            }

            _cachedToken = await _credential.GetTokenAsync(new TokenRequestContext(new[] { Scope }), cancellationToken);
            _logger.LogInformation("Ny token hämtad via Managed Identity, giltig till {ExpiresOn}", _cachedToken.ExpiresOn);
            return _cachedToken.Token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Översätter Computer Visions statuskod till ett vettigt svar från vårt API.
    /// Poängen är att en platschef ska förstå vad som hänt, inte se en rå 429:a.
    /// </summary>
    private static VisionServiceException MapError(HttpResponseMessage response, string body)
    {
        var upstream = (int)response.StatusCode;
        var detail = ExtractErrorMessage(body);

        return upstream switch
        {
            429 => new VisionServiceException(
                upstream,
                429,
                "För många bilder just nu",
                "Bildanalysen är tillfälligt överbelastad. Bilden ligger kvar i kön och analyseras automatiskt.",
                isTransient: true,
                retryAfterSeconds: ReadRetryAfter(response)),

            400 => new VisionServiceException(
                upstream,
                400,
                "Bilden kunde inte analyseras",
                $"Computer Vision avvisade bilden: {detail}",
                isTransient: false),

            401 or 403 => new VisionServiceException(
                upstream,
                502,
                "Behörighetsfel mot bildanalysen",
                "Guardlys managed identity saknar rollen Cognitive Services User på Computer Vision-resursen.",
                isTransient: false),

            404 => new VisionServiceException(
                upstream,
                502,
                "Fel endpoint för bildanalysen",
                "Computer Vision-endpointen svarade 404. Kontrollera Vision__Endpoint och api-version.",
                isTransient: false),

            >= 500 => new VisionServiceException(
                upstream,
                503,
                "Bildanalysen är tillfälligt otillgänglig",
                "Azure Computer Vision svarade med ett serverfel. Bilden analyseras automatiskt när tjänsten är tillbaka.",
                isTransient: true),

            _ => new VisionServiceException(
                upstream,
                502,
                "Oväntat svar från bildanalysen",
                $"Computer Vision svarade {upstream}: {detail}",
                isTransient: false)
        };
    }

    private static int? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return (int)delta.TotalSeconds;
        }

        return response.Headers.TryGetValues("Retry-After", out var values)
               && int.TryParse(values.FirstOrDefault(), out var seconds)
            ? seconds
            : null;
    }

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? "okänt fel";
            }
        }
        catch (JsonException)
        {
            // Svaret var inte JSON — strunt samma, vi loggar råtexten ändå.
        }

        return Truncate(body, 200);
    }

    /// <summary>
    /// Plockar ut det vi behöver ur Image Analysis 4.0-svaret.
    /// Formatet är { "tagsResult": { "values": [...] }, "objectsResult": {...}, "peopleResult": {...} }.
    /// </summary>
    internal static VisionAnalysisResult Parse(string json)
    {
        var result = new VisionAnalysisResult();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("tagsResult", out var tagsResult)
            && tagsResult.TryGetProperty("values", out var tagValues))
        {
            foreach (var tag in tagValues.EnumerateArray())
            {
                var name = tag.TryGetProperty("name", out var n) ? n.GetString() : null;
                var confidence = tag.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0d;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Tags.Add(new DetectedTag(name, Math.Round(confidence, 4)));
                }
            }
        }

        if (root.TryGetProperty("objectsResult", out var objectsResult)
            && objectsResult.TryGetProperty("values", out var objectValues))
        {
            foreach (var obj in objectValues.EnumerateArray())
            {
                // Varje objekt har en lista "tags" med namn och konfidens.
                if (!obj.TryGetProperty("tags", out var objTags))
                {
                    continue;
                }

                foreach (var tag in objTags.EnumerateArray())
                {
                    var name = tag.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var confidence = tag.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0d;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        result.Objects.Add(new DetectedTag(name, Math.Round(confidence, 4)));
                    }
                }
            }
        }

        if (root.TryGetProperty("peopleResult", out var peopleResult)
            && peopleResult.TryGetProperty("values", out var peopleValues))
        {
            foreach (var person in peopleValues.EnumerateArray())
            {
                var confidence = person.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0d;
                // Vi sparar personer som objekt så att regelmotorn kan använda konfidensen.
                result.Objects.Add(new DetectedTag("person", Math.Round(confidence, 4)));
                result.PeopleCount++;
            }
        }

        if (root.TryGetProperty("captionResult", out var captionResult)
            && captionResult.TryGetProperty("text", out var captionText))
        {
            result.Caption = captionText.GetString();
        }

        result.Tags = result.Tags.OrderByDescending(t => t.Confidence).ToList();
        result.Objects = result.Objects.OrderByDescending(o => o.Confidence).ToList();

        return result;
    }

    private static string NormalizeContentType(string contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
