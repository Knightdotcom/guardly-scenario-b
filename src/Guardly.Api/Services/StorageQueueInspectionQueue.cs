using System.Text.Json;
using Azure.Storage.Queues;
using Guardly.Api.Models;
using Guardly.Api.Options;
using Microsoft.Extensions.Options;

namespace Guardly.Api.Services;

/// <summary>
/// Azure Storage Queue mellan uppladdning och analys.
///
/// Det är kön som gör att systemet klarar styrelseordförandens 500 bilder på fem minuter.
/// Uppladdningen blir en snabb blob-skrivning plus ett kömeddelande — ingen väntan på
/// Computer Vision. Analysen sker sedan i den takt Computer Vision tillåter.
/// </summary>
public class StorageQueueInspectionQueue : IInspectionQueue
{
    private readonly QueueClient _queue;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<StorageQueueInspectionQueue> _logger;

    public StorageQueueInspectionQueue(
        QueueServiceClient queueServiceClient,
        IOptions<StorageOptions> options,
        JsonSerializerOptions jsonOptions,
        ILogger<StorageQueueInspectionQueue> logger)
    {
        _queue = queueServiceClient.GetQueueClient(options.Value.QueueName);
        _json = jsonOptions;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        _logger.LogInformation("Kö redo: {QueueName}", _queue.Name);
    }

    public async Task EnqueueAsync(InspectionJob job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(job, _json);
        await _queue.SendMessageAsync(payload, cancellationToken);
    }

    public async Task<IReadOnlyList<QueuedJob>> DequeueAsync(
        int maxMessages, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        var response = await _queue.ReceiveMessagesAsync(maxMessages, visibilityTimeout, cancellationToken);
        var jobs = new List<QueuedJob>();

        foreach (var message in response.Value)
        {
            var job = JsonSerializer.Deserialize<InspectionJob>(message.Body.ToString(), _json);
            if (job is null)
            {
                _logger.LogWarning("Kunde inte tolka kömeddelande {MessageId}, tar bort det", message.MessageId);
                await _queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
                continue;
            }

            jobs.Add(new QueuedJob(job, message.MessageId, message.PopReceipt, message.DequeueCount));
        }

        return jobs;
    }

    public async Task CompleteAsync(QueuedJob job, CancellationToken cancellationToken)
    {
        await _queue.DeleteMessageAsync(job.MessageId, job.PopReceipt, cancellationToken);
    }

    public async Task<int> GetApproximateLengthAsync(CancellationToken cancellationToken)
    {
        var properties = await _queue.GetPropertiesAsync(cancellationToken);
        return properties.Value.ApproximateMessagesCount;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _queue.ExistsAsync(cancellationToken);
            return response.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness: kön svarar inte");
            return false;
        }
    }
}
