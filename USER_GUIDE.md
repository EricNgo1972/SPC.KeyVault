# SPC.KeyVault User Guide

## Overview

SPC.KeyVault provides three main entry points:

- `/` for service health and summary information
- `/ui` for the Blazor admin interface
- `/swagger` for API testing

The system stores:

- secrets in Azure Table `keyvalue`
- API keys in Azure Table `apikeys`
- event logs in Azure Table `eventlogs`

## Before You Start

Make sure the app is running with a valid `STORAGE_CONNECTION_STRING`.

If you need to seed keys outside the UI, insert them directly into Azure Table `apikeys`:

- `PartitionKey = admin`
- `RowKey = <your-api-key>`
- `IsActive = true`
- `IssuedDate = current UTC timestamp`
- `ExpiryDate = optional UTC timestamp`

## Service Health Page

Open:

```text
https://localhost:7298/
```

This page shows:

- total secrets and categories
- total and active API keys
- total event log entries and queued event count
- latest 50 event log rows on demand from the `Events` card

Available actions:

- `Login` opens the Blazor admin login page
- the left navigation panel opens `Swagger`
- the left navigation panel opens `Manage Secrets` and `Manage API Keys` after SPC admin sign-in

## Blazor Admin UI

Open:

```text
https://localhost:7298/ui
```

Login rules:

- authentication is delegated to `auth.phoebus.asia`
- only active admins of tenant `SPC` can sign in
- multi-tenant selection flows are not supported in this version

### Browse Secrets

After login, the UI shows a table of all secrets.

Displayed columns:

- category
- secret name
- secret value, masked by default

Notes:

- the empty category is displayed as `Default`
- select `Reveal` to view a masked value
- select `Hide` to mask it again

### Create a Secret

1. Open `/ui`
2. Select `New secret`
3. Enter category
4. Enter name
5. Enter value
6. Select `Save`

If category is left blank, the secret is stored in the default empty category.

### Edit a Secret

1. Find the secret in the table
2. Select `Edit`
3. Update the value
4. Select `Save`

Current limitation:

- editing category or secret name is not supported from the UI in this version

### Maintain API Keys

Open:

```text
https://localhost:7298/ui/apikeys
```

This page lets an authenticated SPC admin:

- create a new `admin` or `client` API key
- set an optional expiry in days, defaulting to `365`
- view issued and expiry dates
- activate or deactivate existing keys

Important:

- keys stay masked in the list until you select `Show`
- a newly created key is automatically revealed in its row
- `admin` keys are for secret-write and API-key management calls
- `client` keys are for secret-read calls

## Swagger UI

Open:

```text
https://localhost:7298/swagger
```

Use Swagger when you want to test API endpoints directly.

### Authorize in Swagger

1. Open `/swagger`
2. Select `Authorize`
3. Enter the API key value
4. Submit

Use:

- an `admin` API key for admin endpoints
- a `client` API key for secret read endpoints

## API Key Management

Admin API keys can manage other API keys through the HTTP API.

Important endpoints:

- `POST /admin/apikey`
- `GET /admin/apikey`
- `POST /admin/apikey/{category}/{key}/activate`
- `POST /admin/apikey/{category}/{key}/deactivate`

Categories supported for API keys:

- `admin`
- `client`

Authentication performance note:

- the app keeps an in-process cache for `X-API-Key` validation
- valid keys are cached up to 12 hours
- invalid keys are cached for 5 minutes
- keys changed through this app invalidate the cache immediately
- keys changed directly in Azure Table are picked up after cache expiry

## Secret Management API

Admin write endpoints:

- `POST /admin/secret`
- `POST /admin/secret/{category}`

Client read endpoints:

- `GET /secret/{name}`
- `GET /secret/{category}/{name}`

Legacy behavior:

- `POST /admin/secret` uses category `""`
- `GET /secret/{name}` reads category `""`

## Logging

The service writes event logs asynchronously.

Logged events currently include:

- `key_added`
- `key_requested`

Logging is fire-and-forget, so log persistence does not block request handling.

## Troubleshooting

### Root page shows a configuration warning

Check:

- `STORAGE_CONNECTION_STRING` is available to the running process
- Azure Table storage is reachable
- the `keyvalue` and `apikeys` tables can be opened successfully

### UI login fails

Check:

- the account is active
- the tenant is `SPC`
- the tenant role is `Admin`
- the login response from `auth.phoebus.asia` does not require tenant selection

### API returns `401`

Check:

- `X-API-Key` header is present
- the key exists in `apikeys`
- the key is active
- the key is not expired
- the key category matches the endpoint you are calling

### API returns `503`

Check:

- Azure Table storage connectivity
- `STORAGE_CONNECTION_STRING`
- service health page details for which storage area is unavailable
