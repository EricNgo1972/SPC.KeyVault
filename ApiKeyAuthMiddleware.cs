using System.Security.Claims;
using System.Text.Json;

public sealed class ApiKeyAuthMiddleware
{
    private const string ApiKeyHeaderName = "X-API-Key";

    private readonly RequestDelegate _next;
    private readonly ApiKeyStore _apiKeyStore;
    private readonly EventLogService _eventLogService;

    public ApiKeyAuthMiddleware(RequestDelegate next, ApiKeyStore apiKeyStore, EventLogService eventLogService)
    {
        _next = next;
        _apiKeyStore = apiKeyStore;
        _eventLogService = eventLogService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsUiRequest(context))
        {
            await _next(context);
            return;
        }

        var apiKey = context.Request.Headers[ApiKeyHeaderName].ToString().Trim();
        var isSecretRequest = TryGetSecretName(context, out var secretName);

        void LogSecretRequest(string? role)
        {
            if (!isSecretRequest || secretName is null)
            {
                return;
            }

            _eventLogService.LogKeyRequested(
                secretName,
                context.Connection.RemoteIpAddress?.ToString(),
                role,
                MaskKey(apiKey));
        }

        if (HttpMethods.IsGet(context.Request.Method) &&
            context.Request.Path.Equals("/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var storeStatus = _apiKeyStore.GetStatus();
        if (!storeStatus.IsAvailable)
        {
            LogSecretRequest(role: null);
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, storeStatus.StatusMessage);
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogSecretRequest(role: null);
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "Missing API key.");
            return;
        }

        var authResult = _apiKeyStore.Authenticate(apiKey);
        if (!authResult.IsAvailable)
        {
            LogSecretRequest(role: null);
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, authResult.Error ?? "Unable to query API key storage.");
            return;
        }

        if (!authResult.IsAuthenticated || string.IsNullOrWhiteSpace(authResult.Role))
        {
            LogSecretRequest(role: null);
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "Invalid, inactive, or expired API key.");
            return;
        }

        LogSecretRequest(authResult.Role);

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, authResult.Role)],
            authenticationType: "ApiKey");

        context.User = new ClaimsPrincipal(identity);

        await _next(context);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string error)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error }));
    }

    private static bool IsUiRequest(HttpContext context)
    {
        var path = context.Request.Path;
        return path.StartsWithSegments("/ui", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/favicon.ico", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetSecretName(HttpContext context, out string? secretName)
    {
        secretName = null;
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        var segments = context.Request.Path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments is null || segments.Length < 2 || !string.Equals(segments[0], "secret", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        secretName = segments[^1];
        return !string.IsNullOrWhiteSpace(secretName);
    }

    private static string? MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (key.Length <= 8)
        {
            return new string('*', key.Length);
        }

        return $"{key[..4]}...{key[^4..]}";
    }
}
