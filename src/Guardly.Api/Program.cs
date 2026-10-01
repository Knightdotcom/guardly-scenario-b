using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Guardly.Api.Endpoints;
using Guardly.Api.Options;
using Guardly.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Konfiguration
// ---------------------------------------------------------------------------
// Alla inställningar kommer från appsettings + miljövariabler. Inga hemligheter
// finns i koden. Pipelinen sätter Vision__Endpoint och Storage__AccountName som
// miljövariabler på Container App:en — och de är dessutom inte hemliga, eftersom
// autentiseringen sker med Managed Identity i stället för nyckel.
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<VisionOptions>(builder.Configuration.GetSection(VisionOptions.SectionName));
builder.Services.Configure<SafetyRuleOptions>(builder.Configuration.GetSection(SafetyRuleOptions.SectionName));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.SectionName));

var storageOptions = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
var visionOptions = builder.Configuration.GetSection(VisionOptions.SectionName).Get<VisionOptions>() ?? new VisionOptions();
var apiOptions = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();

// Kör vi helt lokalt utan Azure?
var useInMemory = storageOptions.UseInMemory || string.IsNullOrWhiteSpace(storageOptions.AccountName);
var useFakeVision = visionOptions.UseFake || string.IsNullOrWhiteSpace(visionOptions.Endpoint);

// ---------------------------------------------------------------------------
// JSON
// ---------------------------------------------------------------------------
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() }
};
builder.Services.AddSingleton(jsonOptions);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// ---------------------------------------------------------------------------
// Uppladdningsgränser
// ---------------------------------------------------------------------------
var maxUploadBytes = (long)apiOptions.MaxImageSizeMb * 1024 * 1024;

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});

// ---------------------------------------------------------------------------
// Managed Identity
// ---------------------------------------------------------------------------
// DefaultAzureCredential provar flera källor i tur och ordning: miljövariabler,
// workload identity, managed identity och till sist utvecklarens az-inloggning.
// I Container Apps hittar den vår user-assigned managed identity. Ingen nyckel,
// ingen connection string, ingenting som kan läcka till git.
var managedIdentityClientId = builder.Configuration["Azure:ManagedIdentityClientId"];

builder.Services.AddSingleton<TokenCredential>(_ =>
    new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = string.IsNullOrWhiteSpace(managedIdentityClientId) ? null : managedIdentityClientId
    }));

// ---------------------------------------------------------------------------
// Lagring: Blob + Queue
// ---------------------------------------------------------------------------
if (useInMemory)
{
    builder.Services.AddSingleton<IInspectionStore, InMemoryInspectionStore>();
    builder.Services.AddSingleton<IInspectionQueue, InMemoryInspectionQueue>();
}
else
{
    builder.Services.AddSingleton(serviceProvider =>
    {
        var credential = serviceProvider.GetRequiredService<TokenCredential>();
        return new BlobServiceClient(
            new Uri($"https://{storageOptions.AccountName}.blob.core.windows.net"), credential);
    });

    builder.Services.AddSingleton(serviceProvider =>
    {
        var credential = serviceProvider.GetRequiredService<TokenCredential>();
        return new QueueServiceClient(
            new Uri($"https://{storageOptions.AccountName}.queue.core.windows.net"),
            credential,
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
    });

    builder.Services.AddSingleton<IInspectionStore, BlobInspectionStore>();
    builder.Services.AddSingleton<IInspectionQueue, StorageQueueInspectionQueue>();
}

// ---------------------------------------------------------------------------
// Computer Vision
// ---------------------------------------------------------------------------
if (useFakeVision)
{
    builder.Services.AddSingleton<IVisionAnalyzer, FakeVisionAnalyzer>();
}
else
{
    // Typad HttpClient ger oss korrekt hantering av socket-återanvändning.
    // Tokencachning sköts av DefaultAzureCredential, som är registrerad som singleton.
    builder.Services.AddHttpClient<IVisionAnalyzer, AzureVisionAnalyzer>();
}

// ---------------------------------------------------------------------------
// Applikationstjänster
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<ISafetyRuleEngine, SafetyRuleEngine>();
builder.Services.AddScoped<InspectionAnalysisService>();
builder.Services.AddHostedService<StorageInitializer>();
builder.Services.AddHostedService<InspectionWorker>();

// ---------------------------------------------------------------------------
// Observability
// ---------------------------------------------------------------------------
// Application Insights läser APPLICATIONINSIGHTS_CONNECTION_STRING automatiskt.
// Bicep sätter den miljövariabeln på Container App:en.
builder.Services.AddApplicationInsightsTelemetry();

builder.Logging.AddConsole();

// ---------------------------------------------------------------------------
// Swagger
// ---------------------------------------------------------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Guardly Inspection API",
        Version = "v1",
        Description =
            "Säkerhetsinspektion av byggplatsfoton. Ladda upp en bild, få tillbaka taggar, " +
            "konfidenspoäng och varningar om skyddsutrustning saknas.\n\n" +
            "Bildanalysen görs av Azure Computer Vision (Image Analysis 4.0). Guardlys egen " +
            "regelmotor avgör vad som ska flaggas som varning.\n\n" +
            "Uppladdningen är asynkron: POST /inspections svarar 202 Accepted direkt och " +
            "analysen sker i bakgrunden. Det är vad som gör att systemet klarar 500 bilder " +
            "på fem minuter utan att någon uppladdning nekas.",
        Contact = new OpenApiContact { Name = "Guardly AB", Email = "support@guardly.example" }
    });

    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }

    // Formulärfälten för POST /inspections — se filtret för varför.
    options.OperationFilter<InspectionUploadOperationFilter>();
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------

// Swagger är på i alla miljöer — det är API:ets dokumentation för Guardlys kunder,
// och API:et innehåller inga hemligheter.
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Guardly Inspection API v1");
    options.DocumentTitle = "Guardly Inspection API";
});

// Global felhantering: oväntade fel blir ett ProblemDetails-svar i stället för en stacktrace.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

        logger.LogError(feature?.Error, "Ohanterat fel på {Path}", context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            title = "Ett oväntat fel inträffade",
            status = 500,
            detail = "Felet är loggat. Kontakta Guardlys support och uppge tidpunkten."
        });
    });
});

// Startsidan pekar vidare till Swagger så att den publika URL:en inte blir en 404.
app.MapGet("/", () => Results.Redirect("/swagger"))
    .ExcludeFromDescription();

app.MapSystemEndpoints();
app.MapInspectionEndpoints();

// En rad i loggen vid uppstart gör felsökningen i Container Apps mycket enklare.
app.Logger.LogInformation(
    "Guardly Inspection API startar. Lagring: {Storage}, Bildanalys: {Vision}, Miljö: {Environment}",
    useInMemory ? "in-memory (lokal)" : $"Blob Storage ({storageOptions.AccountName})",
    useFakeVision ? "fejkad (lokal)" : $"Azure Computer Vision ({visionOptions.Features})",
    app.Environment.EnvironmentName);

app.Run();

/// <summary>Gör Program synlig för framtida integrationstester med WebApplicationFactory.</summary>
public partial class Program
{
}
