using System.Threading.Channels;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Hosting;

public sealed class EventLogService : BackgroundService
{
    private const string StorageEnvironmentVariable = "STORAGE_CONNECTION_STRING";
    private const string TableName = "eventlogs";
    private readonly Channel<EventLogEntry> _channel = Channel.CreateUnbounded<EventLogEntry>();
    private readonly TableClient? _tableClient;
    private int _queuedCount;

    public EventLogService(IConfiguration configuration)
    {
        var connectionString = configuration[StorageEnvironmentVariable];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        try
        {
            var serviceClient = new TableServiceClient(connectionString);
            var tableClient = serviceClient.GetTableClient(TableName);
            tableClient.CreateIfNotExists();
            _tableClient = tableClient;
        }
        catch (Exception)
        {
            _tableClient = null;
        }
    }

    public void LogKeyAdded(string? callerRole, string? callerKeyPreview)
    {
        Enqueue(new EventLogEntry(
            EventType: "key_added",
            Timestamp: DateTimeOffset.UtcNow,
            CallerRole: callerRole,
            CallerKeyPreview: callerKeyPreview,
            SecretName: null,
            CallerIp: null));
    }

    public void LogKeyRequested(string secretName, string? callerIp, string? callerRole, string? callerKeyPreview)
    {
        Enqueue(new EventLogEntry(
            EventType: "key_requested",
            Timestamp: DateTimeOffset.UtcNow,
            CallerRole: callerRole,
            CallerKeyPreview: callerKeyPreview,
            SecretName: secretName,
            CallerIp: callerIp));
    }

    public EventLogSummaryResult GetSummary()
    {
        if (_tableClient is null)
        {
            return EventLogSummaryResult.Unavailable("Unable to initialize event log storage.");
        }

        try
        {
            var logCount = _tableClient.Query<EventLogEntity>().Count();
            return EventLogSummaryResult.Success(logCount, Volatile.Read(ref _queuedCount));
        }
        catch (RequestFailedException)
        {
            return EventLogSummaryResult.Unavailable("Unable to query event log storage.");
        }
    }

    public EventLogListResult ListLatest(int count)
    {
        if (_tableClient is null)
        {
            return EventLogListResult.Unavailable("Unable to initialize event log storage.");
        }

        try
        {
            var events = _tableClient.Query<EventLogEntity>()
                .OrderByDescending(entry => entry.LoggedAt)
                .Take(count)
                .Select(entry => new EventLogListItem(
                    entry.EventType,
                    entry.LoggedAt,
                    entry.CallerRole,
                    entry.CallerKeyPreview,
                    entry.SecretName,
                    entry.CallerIp))
                .ToList();

            return EventLogListResult.Success(events);
        }
        catch (RequestFailedException)
        {
            return EventLogListResult.Unavailable("Unable to query event log storage.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            Interlocked.Decrement(ref _queuedCount);
            if (_tableClient is null)
            {
                continue;
            }

            try
            {
                await _tableClient.AddEntityAsync(new EventLogEntity
                {
                    PartitionKey = entry.Timestamp.UtcDateTime.ToString("yyyyMMdd"),
                    RowKey = $"{entry.Timestamp.UtcDateTime:HHmmssfffffff}-{Guid.NewGuid():N}",
                    EventType = entry.EventType,
                    CallerRole = entry.CallerRole,
                    CallerKeyPreview = entry.CallerKeyPreview,
                    SecretName = entry.SecretName,
                    CallerIp = entry.CallerIp,
                    LoggedAt = entry.Timestamp
                }, stoppingToken);
            }
            catch (RequestFailedException)
            {
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void Enqueue(EventLogEntry entry)
    {
        if (_channel.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _queuedCount);
        }
    }
}

public sealed record EventLogEntry(
    string EventType,
    DateTimeOffset Timestamp,
    string? CallerRole,
    string? CallerKeyPreview,
    string? SecretName,
    string? CallerIp);

public sealed record EventLogSummaryResult(bool IsAvailable, string? Error, int TotalCount, int QueuedCount)
{
    public static EventLogSummaryResult Success(int totalCount, int queuedCount) => new(true, null, totalCount, queuedCount);

    public static EventLogSummaryResult Unavailable(string error) => new(false, error, 0, 0);
}

public sealed record EventLogListResult(bool IsAvailable, string? Error, IReadOnlyList<EventLogListItem> Events)
{
    public static EventLogListResult Success(IReadOnlyList<EventLogListItem> events) => new(true, null, events);

    public static EventLogListResult Unavailable(string error) => new(false, error, Array.Empty<EventLogListItem>());
}

public sealed record EventLogListItem(
    string EventType,
    DateTimeOffset LoggedAt,
    string? CallerRole,
    string? CallerKeyPreview,
    string? SecretName,
    string? CallerIp);

public sealed class EventLogEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;

    public string RowKey { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string? CallerRole { get; set; }

    public string? CallerKeyPreview { get; set; }

    public string? SecretName { get; set; }

    public string? CallerIp { get; set; }

    public DateTimeOffset LoggedAt { get; set; }

    public DateTimeOffset? Timestamp { get; set; }

    public ETag ETag { get; set; }
}
