using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using SPC.KeyVault.Components;

var builder = WebApplication.CreateBuilder(args);

var azureStorage = builder.Configuration["STORAGE_CONNECTION_STRING"];
var missingConfig = new List<string>();

if (string.IsNullOrWhiteSpace(azureStorage))
{
    missingConfig.Add("STORAGE_CONNECTION_STRING");
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpClient<UiAuthService>(client =>
{
    client.BaseAddress = new Uri("https://auth.phoebus.asia");
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/ui/login";
        options.AccessDeniedPath = "/ui/login";
        options.Cookie.Name = "spc-keyvault-ui";
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SpcAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(UiClaimTypes.TenantId, "SPC");
        policy.RequireClaim(UiClaimTypes.TenantRole, "Admin");
    });
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SPC.KeyVault API",
        Version = "v1",
        Description = "Secret, API key, and health endpoints for SPC KeyVault."
    });

    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "API key required for protected endpoints. Enter the value for X-API-Key.",
        In = ParameterLocation.Header,
        Name = "X-API-Key",
        Type = SecuritySchemeType.ApiKey
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddSingleton<SecretStore>();
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddSingleton<EventLogService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<EventLogService>());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many requests. Please try again later." },
            cancellationToken);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var path = httpContext.Request.Path.Value ?? string.Empty;
        var permitLimit = IsInteractivePath(path) ? 60 : 10;

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"{clientIp}:{path}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "SPC.KeyVault API v1");
    options.RoutePrefix = "swagger";
    options.DocumentTitle = "SPC.KeyVault Swagger";
});
app.UseRateLimiter();
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.MapPost("/ui/login", async Task<IResult> (
    HttpContext httpContext,
    [FromForm] UiLoginRequest request,
    UiAuthService authService) =>
{
    var loginResult = await authService.LoginAsync(request.Email ?? string.Empty, request.Password ?? string.Empty, httpContext.RequestAborted);
    if (!loginResult.Succeeded)
    {
        var error = Uri.EscapeDataString(loginResult.Error ?? "Login failed.");
        return Results.LocalRedirect($"/ui/login?error={error}");
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, loginResult.DisplayName ?? loginResult.Email),
        new(ClaimTypes.Email, loginResult.Email),
        new(ClaimTypes.Role, "ui_admin"),
        new(UiClaimTypes.TenantId, "SPC"),
        new(UiClaimTypes.TenantRole, "Admin")
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    await httpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        principal,
        new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        });

    return Results.LocalRedirect(SanitizeReturnUrl(request.ReturnUrl));
}).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();

app.MapPost("/ui/logout", async Task<IResult> (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/");
}).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();

app.MapPost("/admin/secret", (HttpContext httpContext, List<SecretRequest> requests, SecretStore store) =>
    UpsertSecrets(httpContext, string.Empty, requests, store));

app.MapPost("/admin/secret/{category}", (HttpContext httpContext, string category, List<SecretRequest> requests, SecretStore store) =>
    UpsertSecrets(httpContext, category, requests, store));

app.MapPost("/admin/apikey", (HttpContext httpContext, CreateApiKeyRequest request, ApiKeyStore store, EventLogService eventLogService) =>
    CreateApiKey(httpContext, request, store, eventLogService));

app.MapGet("/admin/apikey", (HttpContext httpContext, ApiKeyStore store) =>
    ListApiKeys(httpContext, store));

app.MapPost("/admin/apikey/{category}/{key}/activate", (HttpContext httpContext, string category, string key, ApiKeyStore store) =>
    SetApiKeyActive(httpContext, category, key, true, store));

app.MapPost("/admin/apikey/{category}/{key}/deactivate", (HttpContext httpContext, string category, string key, ApiKeyStore store) =>
    SetApiKeyActive(httpContext, category, key, false, store));

app.MapGet("/secret/{name}", (HttpContext httpContext, string name, SecretStore store) =>
    GetSecret(httpContext, string.Empty, name, store));

app.MapGet("/secret/{category}/{name}", (HttpContext httpContext, string category, string name, SecretStore store) =>
    GetSecret(httpContext, category, name, store));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static IResult UpsertSecrets(HttpContext httpContext, string category, List<SecretRequest> requests, SecretStore store)
{
    if (!httpContext.User.IsInRole("admin"))
    {
        return Results.Unauthorized();
    }

    if (requests.Count == 0)
    {
        return Results.BadRequest(new { error = "At least one secret is required." });
    }

    var invalidSecret = requests.FirstOrDefault(request => string.IsNullOrWhiteSpace(request.Name));
    if (invalidSecret is not null)
    {
        return Results.BadRequest(new { error = "Every secret must include a name." });
    }

    var writeResult = store.SetMany(category, requests.Select(request => new KeyValuePair<string, string>(
        request.Name,
        request.Value ?? string.Empty)));
    if (!writeResult.Succeeded)
    {
        return Results.Problem(
            detail: writeResult.Error,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Storage unavailable");
    }

    return Results.Ok(new
    {
        category,
        updated = requests.Count,
        secrets = requests.Select(request => new
        {
            name = request.Name,
            value = request.Value ?? string.Empty
        })
    });
}

static IResult CreateApiKey(HttpContext httpContext, CreateApiKeyRequest request, ApiKeyStore store, EventLogService eventLogService)
{
    if (!httpContext.User.IsInRole("admin"))
    {
        return Results.Unauthorized();
    }

    var createResult = store.Create(request.Category ?? string.Empty, request.ExpiryDays);
    if (createResult.IsInvalid)
    {
        return Results.BadRequest(new { error = createResult.Error });
    }

    if (!createResult.Succeeded)
    {
        return Results.Problem(
            detail: createResult.Error,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "API key storage unavailable");
    }

    eventLogService.LogKeyAdded(
        callerRole: httpContext.User.FindFirst(ClaimTypes.Role)?.Value,
        callerKeyPreview: MaskKey(httpContext.Request.Headers["X-API-Key"].ToString()));

    return Results.Ok(new
    {
        apiKey = createResult.ApiKey,
        category = createResult.Category,
        isActive = createResult.IsActive,
        issuedDate = createResult.IssuedDate,
        expiryDate = createResult.ExpiryDate
    });
}

static IResult ListApiKeys(HttpContext httpContext, ApiKeyStore store)
{
    if (!httpContext.User.IsInRole("admin"))
    {
        return Results.Unauthorized();
    }

    var listResult = store.List();
    if (!listResult.IsAvailable)
    {
        return Results.Problem(
            detail: listResult.Error,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "API key storage unavailable");
    }

    return Results.Ok(new { apiKeys = listResult.ApiKeys });
}

static IResult SetApiKeyActive(HttpContext httpContext, string category, string key, bool isActive, ApiKeyStore store)
{
    if (!httpContext.User.IsInRole("admin"))
    {
        return Results.Unauthorized();
    }

    var mutationResult = store.SetActive(category, key, isActive);
    if (mutationResult.IsInvalid)
    {
        return Results.BadRequest(new { error = mutationResult.Error });
    }

    if (mutationResult.IsNotFound)
    {
        return Results.NotFound();
    }

    if (!mutationResult.Succeeded)
    {
        return Results.Problem(
            detail: mutationResult.Error,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "API key storage unavailable");
    }

    return Results.Ok(new
    {
        category = mutationResult.Category,
        key,
        isActive = mutationResult.IsActive
    });
}

static IResult GetSecret(HttpContext httpContext, string category, string name, SecretStore store)
{
    if (!httpContext.User.IsInRole("client"))
    {
        return Results.Unauthorized();
    }

    var lookupResult = store.Get(category, name);
    if (!lookupResult.IsAvailable)
    {
        return Results.Problem(
            detail: lookupResult.Error,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Storage unavailable");
    }

    return lookupResult.Found
        ? Results.Ok(new { category, name, value = lookupResult.Value })
        : Results.NotFound();
}

static string? MaskKey(string? key)
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

static string SanitizeReturnUrl(string? returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/'))
    {
        return "/ui";
    }

    if (returnUrl.StartsWith("//", StringComparison.Ordinal))
    {
        return "/ui";
    }

    return returnUrl;
}

static bool IsInteractivePath(string path)
{
    return path == "/" ||
        path.StartsWith("/ui", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_content", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase);
}

internal sealed record SecretRequest(string Name, string Value);

internal sealed record CreateApiKeyRequest(string Category, int? ExpiryDays);

internal sealed record UiLoginRequest(string? Email, string? Password, string? ReturnUrl);
