using System.Security.Cryptography;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;

public sealed class ApiKeyStore
{
    private const string StorageEnvironmentVariable = "STORAGE_CONNECTION_STRING";
    private const string TableName = "apikeys";
    private const string AdminCategory = "admin";
    private const string ClientCategory = "client";
    private static readonly TimeSpan ValidApiKeyCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan InvalidApiKeyCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        AdminCategory,
        ClientCategory
    };

    private readonly IMemoryCache _cache;
    private readonly TableClient? _tableClient;
    private long _authCacheVersion;

    public ApiKeyStore(IConfiguration configuration, IMemoryCache cache)
    {
        _cache = cache;
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
            StatusMessage = "Unable to initialize API key storage.";
        }
    }

    public bool IsAvailable { get; }

    public string StatusMessage { get; }

    public ApiKeyStoreStatus GetStatus()
    {
        if (_tableClient is null)
        {
            return new ApiKeyStoreStatus(false, false, StatusMessage);
        }

        try
        {
            return new ApiKeyStoreStatus(true, HasActiveAdminKey(), StatusMessage);
        }
        catch (RequestFailedException)
        {
            return new ApiKeyStoreStatus(false, false, "Unable to query API key storage.");
        }
    }

    public ApiKeyAuthenticationResult Authenticate(string apiKey)
    {
        if (_tableClient is null)
        {
            return ApiKeyAuthenticationResult.Unavailable(StatusMessage);
        }

        var cacheKey = BuildAuthCacheKey(apiKey);
        if (_cache.TryGetValue<ApiKeyAuthenticationResult>(cacheKey, out var cachedResult) &&
            cachedResult is not null)
        {
            return cachedResult;
        }

        try
        {
            var adminKey = TryGetEntity(AdminCategory, apiKey);
            if (adminKey is not null)
            {
                var result = IsUsable(adminKey)
                    ? ApiKeyAuthenticationResult.Success(AdminCategory)
                    : ApiKeyAuthenticationResult.Invalid();
                CacheAuthenticationResult(cacheKey, result, adminKey.ExpiryDate);
                return result;
            }

            var clientKey = TryGetEntity(ClientCategory, apiKey);
            if (clientKey is not null)
            {
                var result = IsUsable(clientKey)
                    ? ApiKeyAuthenticationResult.Success(ClientCategory)
                    : ApiKeyAuthenticationResult.Invalid();
                CacheAuthenticationResult(cacheKey, result, clientKey.ExpiryDate);
                return result;
            }

            var invalidResult = ApiKeyAuthenticationResult.Invalid();
            CacheAuthenticationResult(cacheKey, invalidResult, expiryDate: null);
            return invalidResult;
        }
        catch (RequestFailedException)
        {
            return ApiKeyAuthenticationResult.Unavailable("Unable to query API key storage.");
        }
    }

    public ApiKeyListResult List()
    {
        if (_tableClient is null)
        {
            return ApiKeyListResult.Unavailable(StatusMessage);
        }

        try
        {
            var apiKeys = _tableClient.Query<ApiKeyEntity>()
                .Select(entity => new ApiKeyListItem(
                    entity.PartitionKey,
                    MaskKey(entity.RowKey),
                    entity.IsActive,
                    entity.IssuedDate,
                    entity.ExpiryDate))
                .OrderBy(item => item.Category, StringComparer.Ordinal)
                .ThenBy(item => item.IssuedDate)
                .ToList();

            return ApiKeyListResult.Success(apiKeys);
        }
        catch (RequestFailedException)
        {
            return ApiKeyListResult.Unavailable("Unable to query API key storage.");
        }
    }

    public ApiKeyManagementListResult ListForManagement()
    {
        if (_tableClient is null)
        {
            return ApiKeyManagementListResult.Unavailable(StatusMessage);
        }

        try
        {
            var apiKeys = _tableClient.Query<ApiKeyEntity>()
                .Select(entity => new ApiKeyManagementListItem(
                    entity.PartitionKey,
                    entity.RowKey,
                    MaskKey(entity.RowKey),
                    entity.IsActive,
                    entity.IssuedDate,
                    entity.ExpiryDate))
                .OrderBy(item => item.Category, StringComparer.Ordinal)
                .ThenBy(item => item.IssuedDate)
                .ToList();

            return ApiKeyManagementListResult.Success(apiKeys);
        }
        catch (RequestFailedException)
        {
            return ApiKeyManagementListResult.Unavailable("Unable to query API key storage.");
        }
    }

    public ApiKeySummaryResult GetSummary()
    {
        if (_tableClient is null)
        {
            return ApiKeySummaryResult.Unavailable(StatusMessage);
        }

        try
        {
            var apiKeys = _tableClient.Query<ApiKeyEntity>().ToList();
            var activeCount = apiKeys.Count(IsUsable);
            var adminCount = apiKeys.Count(key => string.Equals(key.PartitionKey, AdminCategory, StringComparison.Ordinal));
            var clientCount = apiKeys.Count(key => string.Equals(key.PartitionKey, ClientCategory, StringComparison.Ordinal));

            return ApiKeySummaryResult.Success(apiKeys.Count, activeCount, adminCount, clientCount);
        }
        catch (RequestFailedException)
        {
            return ApiKeySummaryResult.Unavailable("Unable to query API key storage.");
        }
    }

    public ApiKeyCreateResult Create(string category, int? expiryDays)
    {
        if (_tableClient is null)
        {
            return ApiKeyCreateResult.Unavailable(StatusMessage);
        }

        if (!TryNormalizeCategory(category, out var normalizedCategory))
        {
            return ApiKeyCreateResult.Invalid("Category must be 'admin' or 'client'.");
        }

        if (expiryDays is < 0)
        {
            return ApiKeyCreateResult.Invalid("expiryDays must be zero or greater.");
        }

        var key = GenerateApiKey();
        var now = DateTimeOffset.UtcNow;
        var entity = new ApiKeyEntity
        {
            PartitionKey = normalizedCategory,
            RowKey = key,
            IsActive = true,
            IssuedDate = now,
            ExpiryDate = expiryDays.HasValue ? now.AddDays(expiryDays.Value) : null
        };

        try
        {
            _tableClient.AddEntity(entity);
            InvalidateAuthenticationCache();
            return ApiKeyCreateResult.Success(key, normalizedCategory, entity.IsActive, entity.IssuedDate, entity.ExpiryDate);
        }
        catch (RequestFailedException)
        {
            return ApiKeyCreateResult.Unavailable("Unable to create API key.");
        }
    }

    public ApiKeyMutationResult SetActive(string category, string key, bool isActive)
    {
        if (_tableClient is null)
        {
            return ApiKeyMutationResult.Unavailable(StatusMessage);
        }

        if (!TryNormalizeCategory(category, out var normalizedCategory))
        {
            return ApiKeyMutationResult.Invalid("Category must be 'admin' or 'client'.");
        }

        try
        {
            var entity = TryGetEntity(normalizedCategory, key);
            if (entity is null)
            {
                return ApiKeyMutationResult.NotFound();
            }

            entity.IsActive = isActive;
            _tableClient.UpdateEntity(entity, entity.ETag, TableUpdateMode.Replace);
            InvalidateAuthenticationCache();
            return ApiKeyMutationResult.Success(normalizedCategory, isActive);
        }
        catch (RequestFailedException)
        {
            return ApiKeyMutationResult.Unavailable("Unable to update API key.");
        }
    }

    private bool HasActiveAdminKey()
    {
        return _tableClient!
            .Query<ApiKeyEntity>(entity => entity.PartitionKey == AdminCategory)
            .Any(IsUsable);
    }

    private ApiKeyEntity? TryGetEntity(string category, string apiKey)
    {
        var response = _tableClient!.GetEntityIfExists<ApiKeyEntity>(category, apiKey);
        return response.HasValue ? response.Value : null;
    }

    private string BuildAuthCacheKey(string apiKey)
    {
        return $"auth:{Volatile.Read(ref _authCacheVersion)}:{apiKey}";
    }

    private void CacheAuthenticationResult(string cacheKey, ApiKeyAuthenticationResult result, DateTimeOffset? expiryDate)
    {
        var cacheDuration = result.IsAuthenticated
            ? ValidApiKeyCacheDuration
            : InvalidApiKeyCacheDuration;

        if (result.IsAuthenticated && expiryDate.HasValue)
        {
            var timeUntilExpiry = expiryDate.Value - DateTimeOffset.UtcNow;
            if (timeUntilExpiry <= TimeSpan.Zero)
            {
                return;
            }

            cacheDuration = timeUntilExpiry < cacheDuration
                ? timeUntilExpiry
                : cacheDuration;
        }

        _cache.Set(cacheKey, result, cacheDuration);
    }

    private void InvalidateAuthenticationCache()
    {
        Interlocked.Increment(ref _authCacheVersion);
    }

    private static bool IsUsable(ApiKeyEntity entity)
    {
        if (!entity.IsActive)
        {
            return false;
        }

        return !entity.ExpiryDate.HasValue || entity.ExpiryDate.Value > DateTimeOffset.UtcNow;
    }

    private static bool TryNormalizeCategory(string category, out string normalizedCategory)
    {
        normalizedCategory = category.Trim().ToLowerInvariant();
        return AllowedCategories.Contains(normalizedCategory);
    }

    private static string GenerateApiKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string MaskKey(string key)
    {
        if (key.Length <= 8)
        {
            return new string('*', key.Length);
        }

        return $"{key[..4]}...{key[^4..]}";
    }
}

public sealed record ApiKeyStoreStatus(bool IsAvailable, bool HasActiveAdminKey, string StatusMessage);

public sealed record ApiKeySummaryResult(bool IsAvailable, string? Error, int TotalCount, int ActiveCount, int AdminCount, int ClientCount)
{
    public static ApiKeySummaryResult Success(int totalCount, int activeCount, int adminCount, int clientCount) =>
        new(true, null, totalCount, activeCount, adminCount, clientCount);

    public static ApiKeySummaryResult Unavailable(string error) =>
        new(false, error, 0, 0, 0, 0);
}

public sealed record ApiKeyAuthenticationResult(bool IsAvailable, bool IsAuthenticated, string? Role, string? Error)
{
    public static ApiKeyAuthenticationResult Success(string role) => new(true, true, role, null);

    public static ApiKeyAuthenticationResult Invalid() => new(true, false, null, null);

    public static ApiKeyAuthenticationResult Unavailable(string error) => new(false, false, null, error);
}

public sealed record ApiKeyCreateResult(
    bool Succeeded,
    bool IsInvalid,
    string? Error,
    string? ApiKey,
    string? Category,
    bool IsActive,
    DateTimeOffset? IssuedDate,
    DateTimeOffset? ExpiryDate)
{
    public static ApiKeyCreateResult Success(string apiKey, string category, bool isActive, DateTimeOffset issuedDate, DateTimeOffset? expiryDate) =>
        new(true, false, null, apiKey, category, isActive, issuedDate, expiryDate);

    public static ApiKeyCreateResult Invalid(string error) =>
        new(false, true, error, null, null, false, null, null);

    public static ApiKeyCreateResult Unavailable(string error) =>
        new(false, false, error, null, null, false, null, null);
}

public sealed record ApiKeyMutationResult(bool Succeeded, bool IsInvalid, bool IsNotFound, string? Error, string? Category, bool? IsActive)
{
    public static ApiKeyMutationResult Success(string category, bool isActive) => new(true, false, false, null, category, isActive);

    public static ApiKeyMutationResult Invalid(string error) => new(false, true, false, error, null, null);

    public static ApiKeyMutationResult NotFound() => new(false, false, true, null, null, null);

    public static ApiKeyMutationResult Unavailable(string error) => new(false, false, false, error, null, null);
}

public sealed record ApiKeyListResult(bool IsAvailable, string? Error, IReadOnlyList<ApiKeyListItem> ApiKeys)
{
    public static ApiKeyListResult Success(IReadOnlyList<ApiKeyListItem> apiKeys) => new(true, null, apiKeys);

    public static ApiKeyListResult Unavailable(string error) => new(false, error, Array.Empty<ApiKeyListItem>());
}

public sealed record ApiKeyManagementListResult(bool IsAvailable, string? Error, IReadOnlyList<ApiKeyManagementListItem> ApiKeys)
{
    public static ApiKeyManagementListResult Success(IReadOnlyList<ApiKeyManagementListItem> apiKeys) => new(true, null, apiKeys);

    public static ApiKeyManagementListResult Unavailable(string error) => new(false, error, Array.Empty<ApiKeyManagementListItem>());
}

public sealed record ApiKeyListItem(
    string Category,
    string KeyPreview,
    bool IsActive,
    DateTimeOffset IssuedDate,
    DateTimeOffset? ExpiryDate);

public sealed record ApiKeyManagementListItem(
    string Category,
    string Key,
    string KeyPreview,
    bool IsActive,
    DateTimeOffset IssuedDate,
    DateTimeOffset? ExpiryDate);

public sealed class ApiKeyEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;

    public string RowKey { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTimeOffset IssuedDate { get; set; }

    public DateTimeOffset? ExpiryDate { get; set; }

    public DateTimeOffset? Timestamp { get; set; }

    public ETag ETag { get; set; }
}
