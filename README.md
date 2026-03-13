# SPC.KeyVault

Minimal ASP.NET Core (.NET 8) secret server with:

- `X-API-Key` authentication
- in-memory secret storage
- JSON file persistence to `secrets.json`
- simple rate limiting
- a human-readable root page at `/`

This project is intentionally small. It does not use JWT, databases, or external packages.

## Requirements

- .NET 8 SDK

## Configuration

Set these environment variables before running:

- `ADMIN_API_KEY`
- `CLIENT_API_KEY`

Linux or macOS, current shell session:

```bash
export ADMIN_API_KEY="your-admin-key"
export CLIENT_API_KEY="your-client-key"
dotnet run
```

Linux or macOS, persistent for the current user:

Add these lines to `~/.bashrc`, `~/.zshrc`, or the shell profile you actually use:

```bash
export ADMIN_API_KEY="your-admin-key"
export CLIENT_API_KEY="your-client-key"
```

Then reload the shell profile or open a new terminal:

```bash
source ~/.bashrc
```

PowerShell, current session:

```powershell
$env:ADMIN_API_KEY = "your-admin-key"
$env:CLIENT_API_KEY = "your-client-key"
dotnet run
```

PowerShell, persistent for current Windows user:

```powershell
[System.Environment]::SetEnvironmentVariable("ADMIN_API_KEY", "your-admin-key", "User")
[System.Environment]::SetEnvironmentVariable("CLIENT_API_KEY", "your-client-key", "User")
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

## Authentication

All API requests must send this header:

```http
X-API-Key: <key>
```

Access rules:

- `ADMIN_API_KEY` can create or update secrets
- `CLIENT_API_KEY` can read secrets
- invalid or missing key returns `401`

## Endpoints

### Root Page

```http
GET /
```

Returns an HTML status and usage page.

### Create or Update Secret

```http
POST /admin/secret
X-API-Key: <ADMIN_API_KEY>
Content-Type: application/json
```

Body:

```json
{
  "name": "llm",
  "value": "sk-abc123"
}
```

Example:

```bash
curl -X POST "https://localhost:7298/admin/secret" \
  -H "X-API-Key: your-admin-key" \
  -H "Content-Type: application/json" \
  -d "{\"name\":\"llm\",\"value\":\"sk-abc123\"}"
```

### Get Secret

```http
GET /secret/{name}
X-API-Key: <CLIENT_API_KEY>
```

Example:

```bash
curl "https://localhost:7298/secret/llm" \
  -H "X-API-Key: your-client-key"
```

If the secret does not exist, the API returns `404`.

## Storage

Secrets are stored:

- in memory while the app is running
- in `secrets.json` so they survive restarts

## Rate Limiting

- `/` allows `30` requests per minute per IP
- API endpoints allow `10` requests per minute per IP per endpoint

When the limit is exceeded, the API returns `429 Too Many Requests`.

## GitHub Release Build

This repo includes a GitHub Actions workflow:

- `.github/workflows/deployment.yml`

Behavior:

- builds on every push and pull request
- publishes a release artifact zip
- when a tag like `v1.0.0` is pushed, it creates a GitHub Release and uploads the zip asset

Example tag push:

```bash
git tag v1.0.0
git push origin v1.0.0
```

## Notes

This is a small internal utility, not a hardened production secrets manager. If you need encryption at rest, audit logging, key rotation, multi-user auth, or secret versioning, this app should be extended before wider use.
