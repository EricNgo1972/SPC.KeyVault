# SPC.KeyVault

Minimal ASP.NET Core (.NET 8) secret server with:

- service health dashboard at `/`
- `X-API-Key` authentication
- Azure Table secret storage
- Azure Table API key storage
- Azure Table event logging
- Blazor Server admin UI
- Swagger UI
- simple rate limiting

This project is intentionally small. It does not use JWT.

## Requirements

- .NET 8 SDK

## Configuration

Set these environment variables before running:

- `STORAGE_CONNECTION_STRING`

Linux or macOS, current shell session:

```bash
export STORAGE_CONNECTION_STRING="UseDevelopmentStorage=true"
dotnet run
```

Linux or macOS, persistent for the current user:

Add these lines to `~/.bashrc`, `~/.zshrc`, or the shell profile you actually use:

```bash
export STORAGE_CONNECTION_STRING="UseDevelopmentStorage=true"
```

Then reload the shell profile or open a new terminal:

```bash
source ~/.bashrc
```

PowerShell, current session:

```powershell
$env:STORAGE_CONNECTION_STRING = "UseDevelopmentStorage=true"
dotnet run
```

PowerShell, persistent for current Windows user:

```powershell
[System.Environment]::SetEnvironmentVariable("STORAGE_CONNECTION_STRING", "UseDevelopmentStorage=true", "User")
```

After setting persistent values, open a new PowerShell window before running the app.

## Run

```powershell
dotnet run
```

Default local URLs usually include:

- `https://localhost:7298`
- `http://localhost:5149`

Open the root page for usage instructions:

```text
https://localhost:7298/
```

Open Swagger UI:

```text
https://localhost:7298/swagger
```

Open the Blazor admin UI:

```text
https://localhost:7298/ui
```

## Authentication

All API requests must send this header:

```http
X-API-Key: <key>
```

Access rules:

- keys in category `admin` can create or update secrets and manage API keys
- keys in category `client` can read secrets
- tenant `SPC` admins authenticated by `auth.phoebus.asia` can use the Blazor UI
- invalid or missing key returns `401`

The Swagger page itself is open, but calling protected endpoints from Swagger still requires `X-API-Key`.

## Endpoints

### Service Health Page

```http
GET /
```

Returns an HTML service health dashboard with:

- overall service status
- storage health
- service data summary
- a login action for unauthenticated users
- latest event log drill-down from the `Events` summary card

### Blazor Admin UI

```http
GET /ui
```

The admin UI is a Blazor Server area for:

- browsing and editing secrets
- creating admin and client `X-API-Key` values
- activating and deactivating existing API keys

UI login rules:

- signs in through `auth.phoebus.asia`
- only active admins of tenant `SPC` are allowed
- multi-tenant selection flows are not supported in this version

Open API key maintenance directly:

```text
https://localhost:7298/ui/apikeys
```

### Swagger UI

```http
GET /swagger
```

OpenAPI/Swagger page for testing the HTTP API.

Use the `Authorize` button and enter the API key value for header `X-API-Key`.

### API Key Bootstrap

If you need to seed keys outside the UI, insert them directly into Azure Table `apikeys`:

- `PartitionKey = admin`
- `RowKey = <your-api-key>`
- `IsActive = true`
- `IssuedDate = current UTC timestamp`
- `ExpiryDate = optional UTC timestamp`

### Create API Key

```http
POST /admin/apikey
X-API-Key: <admin-key>
Content-Type: application/json
```

Body:

```json
{
  "category": "client",
  "expiryDays": 30
}
```

The server generates the API key and returns it once in the response.

### List API Keys

```http
GET /admin/apikey
X-API-Key: <admin-key>
```

Returns masked key previews with category, active status, issued date, and expiry date.

### Activate API Key

```http
POST /admin/apikey/{category}/{key}/activate
X-API-Key: <admin-key>
```

### Deactivate API Key

```http
POST /admin/apikey/{category}/{key}/deactivate
X-API-Key: <admin-key>
```

### Create or Update Secret

```http
POST /admin/secret
X-API-Key: <admin-key>
Content-Type: application/json
```

Body:

```json
[
  {
    "name": "llm",
    "value": "sk-abc123"
  },
  {
    "name": "db",
    "value": "Server=.;Database=App;User Id=sa;Password=pass;"
  }
]
```

Example:

```bash
curl -X POST "https://localhost:7298/admin/secret" \
  -H "X-API-Key: your-admin-key-from-apikeys-table" \
  -H "Content-Type: application/json" \
  -d "[{\"name\":\"llm\",\"value\":\"sk-abc123\"},{\"name\":\"db\",\"value\":\"conn-string\"}]"
```

This legacy endpoint stores secrets in the default category `""`.

### Create or Update Secret In Category

```http
POST /admin/secret/{category}
X-API-Key: <admin-key>
Content-Type: application/json
```

Example:

```bash
curl -X POST "https://localhost:7298/admin/secret/app" \
  -H "X-API-Key: your-admin-key-from-apikeys-table" \
  -H "Content-Type: application/json" \
  -d "[{\"name\":\"llm\",\"value\":\"sk-abc123\"}]"
```

### Get Secret

```http
GET /secret/{name}
X-API-Key: <client-key>
```

Example:

```bash
curl "https://localhost:7298/secret/llm" \
  -H "X-API-Key: your-client-key-from-apikeys-table"
```

This legacy endpoint reads from the default category `""`.

### Get Secret From Category

```http
GET /secret/{category}/{name}
X-API-Key: <client-key>
```

Example:

```bash
curl "https://localhost:7298/secret/app/llm" \
  -H "X-API-Key: your-client-key-from-apikeys-table"
```

If the secret does not exist, the API returns `404`.

## Storage

Secrets are stored:

- in Azure Table storage table `keyvalue`
- with `PartitionKey = category`
- with `RowKey = name`
- with `Value = secret value`

API keys are stored:

- in Azure Table storage table `apikeys`
- with `PartitionKey = admin` or `client`
- with `RowKey = the API key value`
- with `IsActive`, `IssuedDate`, and optional `ExpiryDate`
- authenticated in-process with a memory cache to avoid querying Azure Table on every request

API key authentication cache:

- valid keys are cached for up to 12 hours
- invalid keys are cached for 5 minutes
- if a key expires sooner, the cache entry lifetime is capped at the key expiry
- creating a key or activating/deactivating a key through this app invalidates the authentication cache immediately
- changes made directly in Azure Table are picked up after the relevant cache entry expires

Events are logged:

- in Azure Table storage table `eventlogs`
- asynchronously, so logging does not block request handling
- as `key_added` when an API key is created
- as `key_requested` when a secret read endpoint is called

If `STORAGE_CONNECTION_STRING` is missing or invalid, the app stays up and the root page reports a storage configuration problem, but secret endpoints return a storage configuration error until Azure storage is available.

## User Guide

End-user and operator steps are documented in [USER_GUIDE.md](/mnt/c/Business%20Solutions/SPC.KeyVault/USER_GUIDE.md).

## Rate Limiting

- `/` allows `30` requests per minute per IP
- API endpoints allow `10` requests per minute per IP per endpoint

When the limit is exceeded, the API returns `429 Too Many Requests`.

## GitHub Release Build

This repo includes a GitHub Actions workflow:

- `.github/workflows/deployment.yml`

Behavior:

- runs manually from the GitHub Actions tab
- builds the project in Release mode
- requires a `release_tag` input like `v1.0.0`
- creates a GitHub Release from the commit you ran the workflow on
- attaches the published zip file to that GitHub Release

## Notes

This is a small internal utility, not a hardened production secrets manager. If you need encryption at rest, audit logging, key rotation, multi-user auth, or secret versioning, this app should be extended before wider use.
