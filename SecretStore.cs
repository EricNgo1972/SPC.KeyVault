using Azure;
using Azure.Data.Tables;

public sealed class SecretStore
{
    private const string StorageEnvironmentVariable = "STORAGE_CONNECTION_STRING";
    private const string TableName = "keyvalue";
    private readonly TableClient? _tableClient;

    public SecretStore(IConfiguration configuration)
    {
        var connectionString = configuration[StorageEnvironmentVariable];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            StatusMessage = $"Missing environment variable: {StorageEnvironmentVariable}";
            return;
        }

        try
        {
            var serviceClient = new TableServiceClient(connectionString);
            var tableClient = serviceClient.GetTableClient(TableName);
            tableClient.CreateIfNotExists();
            _tableClient = tableClient;
            IsAvailable = true;
            StatusMessage = $"Azure Table storage ready (`{TableName}`)";
        }
        catch (Exception)
        {
            StatusMessage = "Unable to initialize Azure Table storage.";
        }
    }

    public bool IsAvailable { get; }

    public string StatusMessage { get; }

    public StorageWriteResult SetMany(string category, IEnumerable<KeyValuePair<string, string>> secrets)
    {
        if (_tableClient is null)
        {
            return StorageWriteResult.Unavailable(StatusMessage);
        }

        try
        {
            foreach (var secret in secrets)
            {
                _tableClient.UpsertEntity(new SecretEntity
                {
                    PartitionKey = category,
                    RowKey = secret.Key,
                    Value = secret.Value
                }, TableUpdateMode.Replace);
            }

            return StorageWriteResult.Success();
        }
        catch (RequestFailedException)
        {
            return StorageWriteResult.Unavailable("Azure Table storage write failed.");
        }
    }

    public SecretLookupResult Get(string category, string name)
    {
        if (_tableClient is null)
        {
            return SecretLookupResult.Unavailable(StatusMessage);
        }

        try
        {
            var response = _tableClient.GetEntityIfExists<SecretEntity>(category, name);
            if (!response.HasValue)
            {
                return SecretLookupResult.Missing();
            }

            var entity = response.Value;
            return SecretLookupResult.FromValue(entity?.Value ?? string.Empty);
        }
        catch (RequestFailedException)
        {
            return SecretLookupResult.Unavailable("Azure Table storage read failed.");
        }
    }

    public SecretListResult ListAll()
    {
        if (_tableClient is null)
        {
            return SecretListResult.Unavailable(StatusMessage);
        }

        try
        {
            var secrets = _tableClient.Query<SecretEntity>()
                .Select(entity => new SecretListItem(
                    entity.PartitionKey,
                    entity.RowKey,
                    entity.Value ?? string.Empty))
                .OrderBy(item => item.Category, StringComparer.Ordinal)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToList();

            return SecretListResult.Success(secrets);
        }
        catch (RequestFailedException)
        {
            return SecretListResult.Unavailable("Azure Table storage list failed.");
        }
    }

    public SecretSummaryResult GetSummary()
    {
        var listResult = ListAll();
        if (!listResult.IsAvailable)
        {
            return SecretSummaryResult.Unavailable(listResult.Error ?? StatusMessage);
        }

        var secretCount = listResult.Secrets.Count;
        var categoryCount = listResult.Secrets
            .Select(secret => secret.Category)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return SecretSummaryResult.Success(secretCount, categoryCount);
    }
}

public sealed record StorageWriteResult(bool Succeeded, string? Error)
{
    public static StorageWriteResult Success() => new(true, null);

    public static StorageWriteResult Unavailable(string error) => new(false, error);
}

public sealed record SecretLookupResult(bool IsAvailable, bool Found, string? Value, string? Error)
{
    public static SecretLookupResult FromValue(string value) => new(true, true, value, null);

    public static SecretLookupResult Missing() => new(true, false, null, null);

    public static SecretLookupResult Unavailable(string error) => new(false, false, null, error);
}

public sealed record SecretListResult(bool IsAvailable, string? Error, IReadOnlyList<SecretListItem> Secrets)
{
    public static SecretListResult Success(IReadOnlyList<SecretListItem> secrets) => new(true, null, secrets);

    public static SecretListResult Unavailable(string error) => new(false, error, Array.Empty<SecretListItem>());
}

public sealed record SecretListItem(string Category, string Name, string Value);

public sealed record SecretSummaryResult(bool IsAvailable, string? Error, int SecretCount, int CategoryCount)
{
    public static SecretSummaryResult Success(int secretCount, int categoryCount) => new(true, null, secretCount, categoryCount);

    public static SecretSummaryResult Unavailable(string error) => new(false, error, 0, 0);
}

public sealed class SecretEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;

    public string RowKey { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTimeOffset? Timestamp { get; set; }

    public ETag ETag { get; set; }
}
