using System.Net.Http.Json;
using System.Text.Json.Serialization;

public sealed class UiAuthService
{
    private readonly HttpClient _httpClient;

    public UiAuthService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<UiLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return UiLoginResult.Failure("Email and password are required.");
        }

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/auth/login",
            new AuthLoginRequest(email, password),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return UiLoginResult.Failure("Login failed.");
        }

        var payload = await response.Content.ReadFromJsonAsync<AuthLoginResponse>(cancellationToken: cancellationToken);
        if (payload is null)
        {
            return UiLoginResult.Failure("Login failed.");
        }

        if (payload.RequiresTenantSelection)
        {
            return UiLoginResult.Failure("This account requires tenant selection and is not supported in the UI yet.");
        }

        if (payload.Tenant is null ||
            !string.Equals(payload.Tenant.TenantId, "SPC", StringComparison.OrdinalIgnoreCase) ||
            //!string.Equals(payload.Tenant.TenantRole, "Admin", StringComparison.OrdinalIgnoreCase) ||
            !payload.Tenant.IsActive)
        {
            return UiLoginResult.Failure("Only active tenant SPC admins can access this UI.");
        }

        if (payload.User is null || !payload.User.IsActive)
        {
            return UiLoginResult.Failure("Only active users can access this UI.");
        }

        return UiLoginResult.Success(
            payload.User.Email ?? email,
            payload.User.DisplayName ?? payload.User.Email ?? email,
            payload.Tenant.TenantId,
            payload.Tenant.TenantRole);
    }
}

public static class UiClaimTypes
{
    public const string TenantId = "tenant_id";
    public const string TenantRole = "tenant_role";
}

public sealed record UiLoginResult(bool Succeeded, string? Error, string Email, string? DisplayName, string TenantId, string TenantRole)
{
    public static UiLoginResult Success(string email, string? displayName, string tenantId, string tenantRole) =>
        new(true, null, email, displayName, tenantId, tenantRole);

    public static UiLoginResult Failure(string error) =>
        new(false, error, string.Empty, null, string.Empty, string.Empty);
}

internal sealed record AuthLoginRequest(string Email, string Password);

internal sealed record AuthLoginResponse(
    [property: JsonPropertyName("requiresTenantSelection")] bool RequiresTenantSelection,
    [property: JsonPropertyName("user")] AuthUserResponse? User,
    [property: JsonPropertyName("tenant")] AuthTenantResponse? Tenant);

internal sealed record AuthUserResponse(
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("isActive")] bool IsActive);

internal sealed record AuthTenantResponse(
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("role")] string TenantRole,
    [property: JsonPropertyName("isActive")] bool IsActive);
