# WebVella.Erp.Api

*Headless REST/OpenAPI host for WebVella ERP (`/api/v1/`).*

The new **`WebVella.Erp.Api` host project** is the headless REST API for the WebVella ERP
modernization refactor. It exposes an HTTP surface over the **unchanged** core `WebVella.Erp`
engine and supersedes the retired RazorPages/cookie hosting model.

> **Naming nuance.** The core engine already declares a C# **namespace** `WebVella.Erp.Api`
> inside `WebVella.Erp/Api/*.cs` (for example, the `RecordManager` class). This README describes
> the new **host project** of the same name — the REST/OpenAPI application — not that pre-existing
> namespace. `Source: /WebVella.Erp/Api/RecordManager.cs` (declares `namespace WebVella.Erp.Api`).

> **Status — planned target.** This host project is the **planned headless target**; its
> `/api/v1/` surface **does not exist in the current checkout** and is built by a separate
> implementation workstream. `Source: AAP §0.9.2`. The sibling references document the same
> "planned" state. `Source: /docs/developer/server-api/overview.md`, `/docs/developer/web-api/overview.md`.
> This README documents the intended target behavior so consumers and operators can prepare;
> every unresolved item is surfaced under [Decision points](#decision-points).

## What it does

The `WebVella.Erp.Api` host is a **thin transport layer** that exposes the platform's business
capabilities over HTTP. It presents the `/api/v1/**` **Minimal API** surface — **records** (CRUD),
**entities/metadata**, **EQL query**, **files** (upload/download), and **auth** — and delegates
each request to the **unchanged, in-process** core `WebVella.Erp` engine managers rather than
re-implementing any business logic.

Surface-to-manager mapping (the managers are unchanged by the refactor and continue to run
in-process):

| `/api/v1/` surface | Wraps in-process manager | Source |
|--------------------|--------------------------|--------|
| Records (CRUD) | `RecordManager` | `Source: /WebVella.Erp/Api/RecordManager.cs` |
| Entities & metadata | `EntityManager` | `Source: /docs/developer/server-api/overview.md` |
| Entity-relation metadata | `EntityRelationManager` | `Source: /docs/developer/server-api/overview.md` |
| EQL query | EQL engine (Irony-based parser) | `Source: /WebVella.Erp/Eql/` (EqlBuilder, EqlCommand, EqlGrammar); `/WebVella.Erp/WebVella.Erp.csproj:L50` (Irony.NetCore) |

Files (upload/download) and the auth endpoints round out the surface; the full per-resource
contract lives in the [API reference](../docs/api-reference/index.md).

**Request pipeline (one line):** JWT bearer validation → `/api/v1/` endpoint → in-process manager
(for example, `RecordManager` / `EntityManager`) → Npgsql transaction → PostgreSQL.
`Source: /WebVella.Erp/WebVella.Erp.csproj:L61` (Npgsql 9.0.4).

**Authorization model.** Metadata endpoints (entities and entity-relations) require the
`Administration` role, and record access depends on the permissions configured on the target
Entity. These in-process managers — and their authorization rules — are documented under
[Server API](../docs/developer/server-api/overview.md). `Source: /docs/developer/server-api/overview.md`.
The full OIDC claim-to-role/permission mapping is authored once in
[Security architecture](../docs/architecture/security.md).

**OpenAPI & interactive reference.** The host emits an auto-generated **OpenAPI 3.1** document at
`/openapi/v1.json` (via `Microsoft.AspNetCore.OpenApi`), browsable through the **Scalar** reference
UI at `/scalar` in the **Development environment only**. `Source: AAP §0.10.1`. See
[OpenAPI document](../docs/api-reference/openapi.md).

**Authentication.** The surface uses **OIDC/JWT bearer** tokens presented as
`Authorization: Bearer <token>`, superseding the legacy `JWT_OR_COOKIE` policy scheme and the
`erp_auth_base` cookie. `Source: /WebVella.Erp.Site/JWT_README.txt` (legacy scheme being superseded).
It also replaces the legacy `/api/v3/` + cookie surface. `Source: /docs/developer/web-api/overview.md`.

**Public HTTP contracts (rule B).** This host publishes **public HTTP contracts for external
consumers** — purpose, inputs/outputs, authentication requirement, and error modes for every
endpoint. The authoritative, per-resource reference (request/response schemas, examples, and status
codes) lives under [`../docs/api-reference/`](../docs/api-reference/index.md):
[records](../docs/api-reference/records.md),
[entities](../docs/api-reference/entities.md),
[EQL](../docs/api-reference/eql.md),
[files](../docs/api-reference/files.md),
[authentication](../docs/api-reference/authentication.md),
[OpenAPI](../docs/api-reference/openapi.md), and
[errors](../docs/api-reference/errors.md). For the overall design, see the
[architecture overview](../docs/architecture/overview.md).

## How to run / build / test

Build and run the host from the repository root:

```bash
dotnet run --project WebVella.Erp.Api
```

Running the host emits the OpenAPI document at `/openapi/v1.json`; the **Scalar** reference UI is
served at `/scalar` in the **Development** environment only. `Source: AAP §0.10.1`.

**Prerequisites.** A running **PostgreSQL** database (accessed via Npgsql;
`Source: /WebVella.Erp/WebVella.Erp.csproj:L61`) and a running **OIDC/JWT identity provider**. The
specific identity provider is **Not available / to be confirmed** — Duende IdentityServer vs
Keycloak (see [Decision points](#decision-points)). `Source: AAP §0.1.4`.

**Lint the generated contract (CI gate).** The generated OpenAPI document is validated with the
Spectral CLI:

```bash
spectral lint openapi.json
```

`Source: AAP §0.10.1`.

**Container / Compose.** For running the host as a container alongside the database, worker, and
migrator, see [Docker Compose](../docs/deployment/docker-compose.md).

**Tests.** **Not available** — no test project exists in the repository at this time.
`Source: AAP §0.9.2`.

**Tooling context.** OpenAPI document generation is provided by `Microsoft.AspNetCore.OpenApi`, and
the interactive reference UI by `Scalar.AspNetCore`; concrete package versions belong to the SPA/API
implementation workstream and are not asserted here.

## Key configs and defaults

Configuration is referenced **by key name only**. Secrets are **never** written to documentation,
logs, or committed config; supply them through a **Kubernetes Secret** (or an environment variable
once an env-var provider is registered). The consolidated, authoritative reference — including the
verified current defaults — is [Configuration reference](../docs/deployment/configuration-reference.md).

| Configuration (key name) | Env-var form (`:` → `__`) | Purpose | Value handling |
|--------------------------|---------------------------|---------|----------------|
| `Settings:ConnectionString` | `Settings__ConnectionString` | PostgreSQL connection string used by Npgsql. | **Secret** — supply by reference; value never in docs (rule D). |
| `Settings:Jwt:Issuer` | `Settings__Jwt__Issuer` | Expected JWT issuer (`iss`) claim. | Non-secret; has a platform default (see reference). |
| `Settings:Jwt:Audience` | `Settings__Jwt__Audience` | Expected JWT audience (`aud`) claim. | Non-secret; has a platform default (see reference). |
| `Settings:Jwt:Key` | `Settings__Jwt__Key` | Symmetric signing key (legacy validation model). | **Secret** — supply by reference; value never in docs (rule D). |
| OIDC authority / discovery URL | *(key name)* `Not available / to be confirmed` | OIDC authority used to validate bearer tokens against the identity provider. | Non-secret URL; concrete key name pending provider choice. |
| Serilog minimum level | `Settings__Serilog__MinimumLevel` | Minimum structured-log level. | *Proposed —* `Not available / to be confirmed`. |
| OTLP exporter endpoint | `Settings__Otlp__Endpoint` | OTLP endpoint URL for traces/metrics/logs. Must **not** embed credentials — collector auth is a separate Secret. | *Proposed —* `Not available / to be confirmed`. |
| Plugin directory | `Settings__PluginDirectory` | Directory scanned for `AssemblyLoadContext`-loaded `.wvplugin` packages. | *Proposed —* `Not available / to be confirmed`. |

**Target configuration model.** In the container-native target, settings are supplied as
**environment variables** and **Kubernetes Secrets** — not as literal secret values in files.
ASP.NET Core maps hierarchical keys `Section:Key` to environment variables by replacing the `:`
separator with a double underscore, `Section__Key`. `Source: AAP §0.6.5`.

**Legacy model (for context).** Configuration was previously bound from `Config.json` via
`ErpSettings` (key names such as `ConnectionString`, `Jwt:Issuer`, `Jwt:Audience`); the target model
replaces this file-based approach with environment variables and Secrets.
`Source: /WebVella.Erp/ErpSettings.cs`. No literal values — connection password, `EncryptionKey`, or
`Jwt:Key` — are reproduced here (rule D).

## Common failure modes & troubleshooting

| Symptom | Likely cause | Remedy |
|---------|--------------|--------|
| `401 Unauthorized` / `403 Forbidden` on valid-looking requests | JWT validation failure: client/server clock skew, wrong `audience`/`issuer`, missing/expired bearer token, or insufficient role/permission (metadata endpoints require the `Administration` role). | Align the `Settings:Jwt:Issuer` / `Settings:Jwt:Audience` keys, confirm the OIDC authority and signing-key Secret, check clock skew, and confirm the caller's role/permissions. See [Security architecture](../docs/architecture/security.md). |
| Startup or first-query failure; Npgsql connection or timeout errors | Cannot reach PostgreSQL; bad or missing connection string; database migrations not applied. | Verify the `Settings:ConnectionString` key is injected from a Secret, confirm the database is healthy and reachable, and confirm the migration job completed. |
| OpenAPI document build fails at `/openapi/v1.json` | Endpoint metadata or schema-generation error during document build. | Fix the offending endpoint/schema; run `spectral lint openapi.json` as the CI gate to catch contract regressions early. `Source: AAP §0.10.1`. |
| A `.wvplugin` package fails to load; the host starts but a plugin's endpoints are missing | `AssemblyLoadContext` load error, bad package layout, target-framework/ABI mismatch, or a wrong plugin-directory path. | Verify the plugin-directory key and package layout, rebuild against the correct target framework; the host isolates the fault and keeps serving — follow the rollback path. |

For the full operational runbook (triage flow and per-symptom detail), see
[Troubleshooting](../docs/deployment/troubleshooting.md).

## Decision points

The following items are unresolved and must not be guessed (rule F). Documentation stays neutral
until each is confirmed:

1. **Target runtime.** The specification says ".NET 9", but the core project targets `net10.0`. The
   authoritative target is **Not available / to be confirmed**; do not silently pick one.
   `Source: /WebVella.Erp/WebVella.Erp.csproj:L4` (`<TargetFramework>net10.0</TargetFramework>`).
   *What is needed:* the authoritative resolved target framework for the new host projects.
2. **Authentication provider.** Duende IdentityServer vs Keycloak is undecided:
   **Not available / to be confirmed**. Authentication documentation is authored provider-neutral,
   with a provider-specific appendix added once chosen. `Source: AAP §0.1.4`.
   *What is needed:* the chosen provider's OIDC discovery/authority URL and the host's
   claim-mapping policy.
3. **Tests.** No test project exists yet: **Not available**. `Source: AAP §0.9.2`.
   *What is needed:* a test project for the new host (unit + integration) before test guidance can
   be documented.
