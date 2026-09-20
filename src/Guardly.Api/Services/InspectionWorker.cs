using Guardly.Api.Models;
using Guardly.Api.Options;
using Microsoft.Extensions.Options;

namespace Guardly.Api.Services;

/// <summary>
/// Bakgrundsworkern som tömmer kön och analyserar bilder.
///
/// Den körs i samma container som API:et. Det betyder att när Container Apps skalar upp
/// på grund av köns längd får vi fler workers, inte bara fler webbservrar.
///
/// Två saker är medvetet inbyggda:
/// 1. Parallelliteten är begränsad (Worker:MaxConcurrentAnalyses) så att vi inte slår i
///    Computer Visions takgräns på 10 transaktioner per sekund.
/// 2. Pollningen backar av upp till 30 sekunder när kön är tom. Poll varje sekund skulle
///    hålla replicorna "aktiva" dygnet runt hos Azure, och aktiv vCPU kostar åtta gånger
///    mer än idle. Backoffen är alltså en ren kostnadsoptimering.
/// </summary>
public class InspectionWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IInspectionQueue _queue;
    private readonly WorkerOptions _options;
    private readonly ILogger<InspectionWorker> _logger;

    public InspectionWorker(
        IServiceProvider services,
        IInspectionQueue queue,
        IOptions<WorkerOptions> options,
        ILogger<InspectionWorker> logger)
    {
        _services = services;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Bakgrundsworkern är avstängd (Worker:Enabled = false)");
            return;
        }

        _logger.LogInformation(
            "Bakgrundsworkern startad. Max {MaxConcurrent} samtidiga analyser, batchstorlek {BatchSize}",
            _options.MaxConcurrentAnalyses, _options.BatchSize);

        var delaySeconds = _options.MinPollDelaySeconds;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Synlighetstimeouten måste vara längre än värsta tänkbara analystid,
                // annars plockar en annan replica upp samma jobb och vi betalar dubbelt.
                var visibilityTimeout = TimeSpan.FromMinutes(5);
                var jobs = await _queue.DequeueAsync(_options.BatchSize, visibilityTimeout, stoppingToken);

                if (jobs.Count == 0)
                {
                    // Tom kö: backa av gradvis mot maxvärdet.
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                    delaySeconds = Math.Min(delaySeconds * 2, _options.MaxPollDelaySeconds);
                    continue;
                }

                // Det finns jobb — tillbaka till snabb pollning.
                delaySeconds = _options.MinPollDelaySeconds;

                using var throttle = new SemaphoreSlim(_options.MaxConcurrentAnalyses);
                var tasks = jobs.Select(job => ProcessWithThrottleAsync(job, throttle, stoppingToken));
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Workern får aldrig dö. Logga, vänta och fortsätt.
                _logger.LogError(ex, "Oväntat fel i bakgrundsworkern, försöker igen om 10 sekunder");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("Bakgrundsworkern avslutad");
    }

    private async Task ProcessWithThrottleAsync(QueuedJob job, SemaphoreSlim throttle, CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken);
        try
        {
            await ProcessAsync(job, cancellationToken);
        }
        finally
        {
            throttle.Release();
        }
    }

    private async Task ProcessAsync(QueuedJob queued, CancellationToken cancellationToken)
    {
        // Egen scope per jobb — annars delar alla jobb samma tjänstinstanser.
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IInspectionStore>();
        var analysis = scope.ServiceProvider.GetRequiredService<InspectionAnalysisService>();

        var inspection = await store.GetAsync(queued.Job.InspectionId, queued.Job.SiteId, cancellationToken);
        if (inspection is null)
        {
            _logger.LogWarning(
                "Inspektion {InspectionId} finns inte längre, tar bort kömeddelandet", queued.Job.InspectionId);
            await _queue.CompleteAsync(queued, cancellationToken);
            return;
        }

        if (inspection.Status == InspectionStatus.Completed)
        {
            // Idempotens: meddelandet har redan behandlats (kan hända vid timeout på synligheten).
            await _queue.CompleteAsync(queued, cancellationToken);
            return;
        }

        try
        {
            await analysis.AnalyzeAsync(inspection, cancellationToken);
            await _queue.CompleteAsync(queued, cancellationToken);
        }
        catch (VisionServiceException ex) when (ex.IsTransient && queued.DequeueCount < _options.MaxDequeueCount)
        {
            // Tillfälligt fel och vi har försök kvar: låt meddelandet bli synligt igen
            // genom att INTE ta bort det. Azure lägger tillbaka det automatiskt.
            _logger.LogWarning(
                "Tillfälligt fel för inspektion {InspectionId} (försök {DequeueCount}): {Message}. Jobbet ligger kvar på kön.",
                inspection.Id, queued.DequeueCount, ex.Message);
        }
        catch (Exception ex)
        {
            // Permanent fel, eller för många försök. Markera som misslyckad och släpp jobbet.
            var reason = ex is VisionServiceException vision
                ? $"{vision.Title}: {vision.Message}"
                : ex.Message;

            _logger.LogError(
                ex,
                "Inspektion {InspectionId} misslyckades definitivt efter {DequeueCount} försök",
                inspection.Id, queued.DequeueCount);

            await analysis.MarkFailedAsync(inspection, reason, cancellationToken);
            await _queue.CompleteAsync(queued, cancellationToken);
        }
    }
}

/// <summary>
/// Skapar blob-containrar och kö vid uppstart. Ligger som en egen hosted service så att
/// appen startar även om lagringen tillfälligt strular — /health/ready visar då att något är fel
/// i stället för att containern hamnar i en omstartsloop.
/// </summary>
public class StorageInitializer : IHostedService
{
    private readonly IInspectionStore _store;
    private readonly IInspectionQueue _queue;
    private readonly ILogger<StorageInitializer> _logger;

    public StorageInitializer(IInspectionStore store, IInspectionQueue queue, ILogger<StorageInitializer> logger)
    {
        _store = store;
        _queue = queue;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.InitializeAsync(cancellationToken);
            await _queue.InitializeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Kunde inte initiera lagringen vid uppstart. Kontrollera att managed identity har " +
                "rollerna Storage Blob Data Contributor och Storage Queue Data Contributor.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
