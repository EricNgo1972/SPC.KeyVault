using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var adminApiKey = builder.Configuration["ADMIN_API_KEY"];
var clientApiKey = builder.Configuration["CLIENT_API_KEY"];
var missingConfig = new List<string>();

if (string.IsNullOrWhiteSpace(adminApiKey))
{
    missingConfig.Add("ADMIN_API_KEY");
}

if (string.IsNullOrWhiteSpace(clientApiKey))
{
    missingConfig.Add("CLIENT_API_KEY");
}

builder.Services.AddSingleton<SecretStore>();
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
        var permitLimit = path == "/" ? 30 : 10;

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

app.UseRateLimiter();
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.MapGet("/", (HttpContext httpContext) =>
{
    var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
    var statusText = missingConfig.Count == 0 ? "Running" : "InActive, waiting configuration";
    var configurationStatus = missingConfig.Count == 0 ? "Ready" : "Configuration required";
    var configurationMessage = missingConfig.Count == 0
        ? "<p><b>Configuration:</b> API keys are loaded from environment variables.</p>"
        : $"<div class=\"warn\"><b>Configuration Required</b><br/>Missing environment variables: <code>{string.Join(", ", missingConfig)}</code></div>";
    var html = $$"""
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Simple Secret Server</title>
        <style>
            body {
                font-family: system-ui, -apple-system, sans-serif;
                background: #0f172a;
                color: #e5e7eb;
                padding: 24px;
                max-width: 900px;
                margin: auto;
            }

            h1, h2 {
                color: #38bdf8;
            }

            p, li {
                line-height: 1.5;
            }

            code, pre {
                background: #020617;
                border-radius: 6px;
                display: block;
                white-space: pre-wrap;
                margin: 10px 0;
                font-family: Consolas, "Courier New", monospace;
            }

            pre {
                padding: 10px;
                overflow-x: auto;
            }

            ul {
                margin-left: 20px;
                padding-left: 0;
            }

            .warn {
                background: #1e293b;
                border-left: 4px solid #f97316;
                padding: 12px;
                margin: 16px 0;
            }
        </style>
    </head>
    <body>
        <h1>Simple Secret Server</h1>

        <p><b>Status:</b> {{statusText}}</p>
        <p><b>Configuration:</b> {{configurationStatus}}</p>
        <p><b>Storage:</b> In-memory with persisted <code>secrets.json</code></p>
        <p><b>Base URL:</b> {{baseUrl}}</p>
        {{configurationMessage}}

        <div class="warn">
            <b>Authentication Required</b><br/>
            API requests must include:
            <code>X-API-Key: &lt;key&gt;</code>
        </div>

        <h2>Roles</h2>
        <ul>
            <li><code>ADMIN_API_KEY</code> can create or update secrets</li>
            <li><code>CLIENT_API_KEY</code> can read secrets</li>
        </ul>

        <h2>Endpoints</h2>
        <code>POST {{baseUrl}}/admin/secret</code>
        <code>GET {{baseUrl}}/secret/{name}</code>

        <h2>Admin Request</h2>
        <pre>curl -X POST "{{baseUrl}}/admin/secret" ^
        -H "X-API-Key: &lt;ADMIN_API_KEY&gt;" ^
        -H "Content-Type: application/json" ^
        -d "[{\"name\":\"llm\",\"value\":\"sk-abc123\"},{\"name\":\"db\",\"value\":\"conn-string\"}]"</pre>

        <h2>Client Request</h2>
        <pre>curl "{{baseUrl}}/secret/llm" ^
        -H "X-API-Key: &lt;CLIENT_API_KEY&gt;"</pre>

        <h2>Notes</h2>
        <ul>
            <li>Secrets are stored as simple key-value pairs</li>
            <li>Secrets persist across restarts in <code>secrets.json</code></li>
            <li>Root page limit: 30 requests per minute per IP</li>
            <li>API limit: 10 requests per minute per IP per endpoint</li>
            <li>Invalid or missing API keys return <code>401</code></li>
            <li>Missing secrets return <code>404</code></li>
        </ul>
    </body>
    </html>
    """;

    return Results.Content(html, "text/html");
});

app.MapPost("/admin/secret", (HttpContext httpContext, List<SecretRequest> requests, SecretStore store) =>
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

    store.SetMany(requests.Select(request => new KeyValuePair<string, string>(
        request.Name,
        request.Value ?? string.Empty)));

    return Results.Ok(new
    {
        updated = requests.Count,
        secrets = requests.Select(request => new
        {
            name = request.Name,
            value = request.Value ?? string.Empty
        })
    });
});

app.MapGet("/secret/{name}", (HttpContext httpContext, string name, SecretStore store) =>
{
    if (!httpContext.User.IsInRole("client"))
    {
        return Results.Unauthorized();
    }

    return store.TryGet(name, out var value)
        ? Results.Ok(new { name, value })
        : Results.NotFound();
});

app.Run();

internal sealed record SecretRequest(string Name, string Value);
