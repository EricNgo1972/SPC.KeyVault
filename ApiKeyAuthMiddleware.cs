using System.Security.Claims;

public sealed class ApiKeyAuthMiddleware
{
    private const string ApiKeyHeaderName = "X-API-Key";

    private readonly RequestDelegate _next;
    private readonly string? _adminApiKey;
    private readonly string? _clientApiKey;

    public ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _adminApiKey = configuration["ADMIN_API_KEY"];
        _clientApiKey = configuration["CLIENT_API_KEY"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) &&
            context.Request.Path.Equals("/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var apiKey = context.Request.Headers[ApiKeyHeaderName].ToString().Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var role = ResolveRole(apiKey);
        if (role is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)],
            authenticationType: "ApiKey");

        context.User = new ClaimsPrincipal(identity);

        await _next(context);
    }

    private string? ResolveRole(string apiKey)
    {
        if (!string.IsNullOrEmpty(_adminApiKey) && apiKey == _adminApiKey)
        {
            return "admin";
        }

        if (!string.IsNullOrEmpty(_clientApiKey) && apiKey == _clientApiKey)
        {
            return "client";
        }

        return null;
    }
}
