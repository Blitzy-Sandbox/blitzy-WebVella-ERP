<!--{"sort_order":4, "name": "configuration-reference", "label": "Configuration Reference"}-->
# Configuration Reference

> **Planned target — not yet implemented.** The container-native configuration model on this page (environment variables, Kubernetes Secrets, the `:` → `__` mapping, options validation / fail-fast, and the new OIDC / observability / worker / plugin keys) is the **proposed target** and **does not exist in the repository yet**. The current hosts read configuration **only** from a JSON file (`config.json`) via `AddJsonFile`; **no host registers an environment-variable configuration provider** (there is no `AddEnvironmentVariables` call anywhere in the codebase), so environment variables are **not** honored today. Source: /WebVella.Erp.Site/Startup.cs:L43 (`AddJsonFile(configPath)`); Source: /WebVella.Erp.Web/ErpMvcExtensions.cs:L51-L52 (`AddJsonFile` + `ErpSettings.Initialize`); Source: /WebVella.Erp.ConsoleApp/Program.cs:L40 (`AddJsonFile("config.json")`). The [Current configuration](#current-configuration-configjson) section below is verified against the code; the [Proposed container-native settings](#proposed-container-native-settings) section is design intent and marked **Not available / to be confirmed** where a value, key name, precedence rule, or validation behavior is undecided.

<!-- -->

> **No secrets in documentation (rule D).** Configuration is documented by **key name only**; this page never reproduces a literal secret value, connection string, key, or internal host/path — even where the committed sample `config.json` contains one. Placeholders such as `<DB_CONNECTION_STRING>`, `<ENCRYPTION_KEY>`, and `<JWT_SIGNING_KEY>` stand in for real values. **Secrets must be provided via a Secret (or environment variable, once an env-var provider exists) and must never be committed to source control.**

## Current configuration (`config.json`)

Today configuration is read from `WebVella.Erp.Site/Config.json` and bound through `ErpSettings`. **Most** keys live under the top-level `Settings` object; two additional top-level sections exist alongside it — a `Development` section holding dev-only test fixtures (documented under [Development (dev-only)](#development-dev-only)) and an `ApiUrlTemplates` section holding a legacy route template (documented in the **Legacy API URL template** note below). Source: /WebVella.Erp.Site/Config.json:L2 (`Settings` object), L30 (`Development` section), L34 (`ApiUrlTemplates` section); Source: /WebVella.Erp/ErpSettings.cs (settings binding). The hosts build configuration with a JSON file provider only — there is no environment-variable provider — so the values below come from the committed file, not from the environment. Source: /WebVella.Erp.Site/Startup.cs:L43; Source: /WebVella.Erp.Web/ErpMvcExtensions.cs:L51-L52.

The **Sample value** column shows the value present in the committed development `Config.json`, **not** an application-level default. For the `Settings:*` keys there is no defaulting or options-validation layer — a missing key simply binds to null/empty. The two `Development:*` keys are the **exception**: they carry hardcoded application-level defaults in `ErpSettings`, so a missing key falls back to that default rather than null/empty (see [Development (dev-only)](#development-dev-only)). Source: /WebVella.Erp/ErpSettings.cs:L89-L96. Secret and internal-infrastructure values are **not reproduced** (rule D); their rows show the value **type** and how to supply it.

### Database

| Legacy key (`Config.json`) | Purpose | Sample value | Secret? |
|----------------------------|---------|--------------|---------|
| `Settings:ConnectionString` — Source: /WebVella.Erp.Site/Config.json:L4 | PostgreSQL connection string consumed by the Npgsql data-access layer. Source: /WebVella.Erp/WebVella.Erp.csproj:L61 (`Npgsql 9.0.4`) | Secret — required; literal value not reproduced (rule D) | Yes |

### Encryption

| Legacy key (`Config.json`) | Purpose | Sample value | Secret? |
|----------------------------|---------|--------------|---------|
| `Settings:EncryptionKey` — Source: /WebVella.Erp.Site/Config.json:L5 | Symmetric key used to encrypt and decrypt encrypted Entity fields. | Secret — required; literal value not reproduced (rule D) | Yes |

### JWT (current)

The current hosts validate JWT bearer tokens with a **symmetric** signing key read from `Settings:Jwt:Key`. Source: /WebVella.Erp.Site/Config.json:L25; Source: /WebVella.Erp.Site/JWT_README.txt. The move to an external OIDC provider (and, typically, asymmetric/JWKS validation) is a target change described under [Proposed OIDC / identity provider](#oidc-identity-provider-proposed) and in [../architecture/security.md](../architecture/security.md); the current symmetric-key model is documented here as the "before" state.

| Legacy key (`Config.json`) | Purpose | Sample value | Secret? |
|----------------------------|---------|--------------|---------|
| `Settings:Jwt:Key` — Source: /WebVella.Erp.Site/Config.json:L25 | Symmetric signing key used to validate JWT bearer tokens (current model). | Secret — required; literal value not reproduced (rule D) | Yes |
| `Settings:Jwt:Issuer` — Source: /WebVella.Erp.Site/Config.json:L26 | Expected JWT issuer (`iss`) claim. | `webvella-erp` | No |
| `Settings:Jwt:Audience` — Source: /WebVella.Erp.Site/Config.json:L27 | Expected JWT audience (`aud`) claim. | `webvella-erp` | No |

### Email and SMTP

| Legacy key (`Config.json`) | Purpose | Sample value | Secret? |
|----------------------------|---------|--------------|---------|
| `Settings:EmailEnabled` — Source: /WebVella.Erp.Site/Config.json:L14 | Master switch for outbound email delivery. | `false` | No |
| `Settings:EmailSMTPServerName` — Source: /WebVella.Erp.Site/Config.json:L15 | SMTP server host name. | `""` (empty in sample) | No |
| `Settings:EmailSMTPPort` — Source: /WebVella.Erp.Site/Config.json:L16 | SMTP server port. | `25` | No |
| `Settings:EmailSMTPUsername` — Source: /WebVella.Erp.Site/Config.json:L17 | SMTP authentication user name. | Secret — provide via Secret; empty in sample | Yes |
| `Settings:EmailSMTPPassword` — Source: /WebVella.Erp.Site/Config.json:L18 | SMTP authentication password. | Secret — provide via Secret; not reproduced (rule D) | Yes |
| `Settings:EmailFrom` — Source: /WebVella.Erp.Site/Config.json:L19 | Default `From` address for outbound email. | `""` (empty in sample) | No |
| `Settings:EmailTo` — Source: /WebVella.Erp.Site/Config.json:L20 | Default `To` address used for diagnostics/testing. | `""` (empty in sample) | No |

### Storage

| Legacy key (`Config.json`) | Purpose | Sample value | Secret? |
|----------------------------|---------|--------------|---------|
| `Settings:EnableFileSystemStorage` — Source: /WebVella.Erp.Site/Config.json:L12 | Toggles the file-system storage backend (Storage.Net). | `false` | No |
| `Settings:FileSystemStorageFolder` — Source: /WebVella.Erp.Site/Config.json:L13 | Root folder used when file-system storage is enabled. | Internal path — sample value not reproduced | No |

### Localization

| Legacy key (`Config.json`) | Purpose | Sample value | Secret? |
|----------------------------|---------|--------------|---------|
| `Settings:Lang` — Source: /WebVella.Erp.Site/Config.json:L6 | Default language code. | `en` | No |
| `Settings:Locale` — Source: /WebVella.Erp.Site/Config.json:L7 | Default locale (culture). | `en-US` | No |
| `Settings:TimeZoneName` — Source: /WebVella.Erp.Site/Config.json:L8 | Default time-zone name. | `FLE Standard Time` | No |

### Runtime and feature flags

| Legacy key (`Config.json`) | Purpose | Sample value | Secret? |
|----------------------------|---------|--------------|---------|
| `Settings:DevelopmentMode` — Source: /WebVella.Erp.Site/Config.json:L10 | Enables development-only behavior; disable in production. | `true` (dev sample) | No |
| `Settings:EnableBackgroundJobs` — Source: /WebVella.Erp.Site/Config.json:L11 | Enables the in-process job scheduler. In the proposed container-native model this would govern whether the host runs jobs itself or defers them to the (not-yet-existing) `WebVella.Erp.Worker`. | `false` | No |
| `Settings:CacheKey` — Source: /WebVella.Erp.Site/Config.json:L9 | Cache-busting key; if empty, the current date is used. | `""` (empty) | No |
| `Settings:AppName` — Source: /WebVella.Erp.Site/Config.json:L21 | Display name of the application. | `WebVella Next` | No |
| `Settings:NavLogoUrl` — Source: /WebVella.Erp.Site/Config.json:L22 | URL of the navigation logo. | `""` (empty) | No |
| `Settings:SystemMasterBackgroundImageUrl` — Source: /WebVella.Erp.Site/Config.json:L23 | URL of the sign-in / master background image. | `""` (empty) | No |

### Development (dev-only)

These two keys live in a **separate top-level `Development` section** (not under `Settings`) and are **used only in development** to target a known entity/record for local testing. Unlike the `Settings:*` keys, both carry **hardcoded application-level defaults** in `ErpSettings`, so a missing or blank key does **not** bind to null — it falls back to the default. `TestEntityName` defaults to `test`; `TestRecordId` is initialized to a fixed GUID and is only overridden when the configured value parses as a GUID. Source: /WebVella.Erp/ErpSettings.cs:L89-L96. Neither key is a secret. Under the proposed (not-yet-existing) environment-variable provider they would map to `Development__TestEntityName` and `Development__TestRecordId`, but these are **development-only** and are not intended for production configuration.

| Legacy key (`Config.json`) | Purpose | Sample value | Secret? |
|----------------------------|---------|--------------|---------|
| `Development:TestEntityName` — Source: /WebVella.Erp.Site/Config.json:L31 | Dev-only entity name used as a test target during local development. Falls back to the hardcoded default `test` when the key is empty or absent. Source: /WebVella.Erp/ErpSettings.cs:L89 | `test` (dev-only) | No |
| `Development:TestRecordId` — Source: /WebVella.Erp.Site/Config.json:L32 | Dev-only record id used as a test target during local development. Bound only when the configured value parses as a GUID; otherwise the hardcoded default GUID in `ErpSettings` is used. Source: /WebVella.Erp/ErpSettings.cs:L90,L94-L96 | GUID — dev-only sample fixture (literal not reproduced) | No |

> **Legacy API URL template.** The legacy key `Settings:ApiUrlTemplates:FieldInlineEdit` uses a `/api/v3/...` route template. Source: /WebVella.Erp.Site/Config.json:L35 In the headless target the REST surface would be versioned under `/api/v1/`; the legacy template is a "before"-state value and its literal contents are not reproduced here.

## Proposed container-native settings

> **Not available / to be confirmed.** Everything in this section is target design. It requires code that does not exist yet — at minimum an **environment-variable configuration provider** (an `AddEnvironmentVariables` registration, absent today) and an **options-validation layer** to enforce required keys and fail fast on startup. Until that code and the accompanying deployment manifests exist, the key **names, defaults, precedence order, and fail-fast behavior below are proposed, not authoritative**.

### Environment-variable mapping (proposed)

The proposed convention reuses ASP.NET Core's standard mapping of hierarchical keys to environment variables by replacing each `:` separator with a double underscore `__`. This mapping only takes effect once an environment-variable provider is registered (**Not available / to be confirmed**). Under the proposal, the current `Settings:*` keys would also be settable as:

- `Settings:ConnectionString` → `Settings__ConnectionString`
- `Settings:Jwt:Issuer` → `Settings__Jwt__Issuer`
- `Settings:Jwt:Key` → `Settings__Jwt__Key`

The following is **non-executable illustrative pseudocode** for the proposed variable **names** only — no env-var provider reads these today. Secret values are shown as `${VAR:?...}` references that must be supplied from a Secret at runtime; they are never written inline or committed.

```bash
# PROPOSED names only (no environment-variable provider exists yet — illustrative).
# Secret VALUES are injected from a Secret at runtime via the ${VAR:?...} references
# below; they are never written inline or committed to source control.
export Settings__ConnectionString="${DB_CONNECTION_STRING:?provide via Secret}"
export Settings__EncryptionKey="${ENCRYPTION_KEY:?provide via Secret}"
export Settings__Jwt__Key="${JWT_SIGNING_KEY:?provide via Secret}"
export Settings__Jwt__Issuer="webvella-erp"
export Settings__Jwt__Audience="webvella-erp"
export Settings__EmailEnabled="false"
```

### OIDC / identity provider (proposed)

> **Not available / to be confirmed.** The identity provider (Duende IdentityServer vs. Keycloak) is undecided, and no OIDC client or validation code exists yet. The design is provider-neutral. See [../architecture/security.md](../architecture/security.md) for the authentication architecture and claim-to-role/permission mapping.

Security requirements the eventual configuration must satisfy (per RFC 9700 / OAuth 2.1 guidance):

- The **React SPA is a public client** and **must not be issued or configured with a client secret** — a browser-delivered secret cannot be kept confidential. Login uses the **authorization-code flow with PKCE (S256)**, never the implicit flow or a browser-side password/direct-token grant.
- The SPA must validate the OIDC **`state`** and **`nonce`** parameters and use an **exact, pre-registered redirect URI**.
- A **client secret applies only to a confidential (server-side) client** (for example a back-end-for-frontend or the API host validating tokens against the provider), **if** the chosen topology uses one — that topology is itself **Not available / to be confirmed**. Any such secret is a Secret (rule D).
- Token **issuer, audience, signing algorithm / JWKS, lifetimes, and refresh-token rotation** are deferred until the provider and client code exist; document them here only once resolved.

**What is needed:** the authority / discovery URL, the client id, the requested scope names, the exact redirect URI(s), and — **only if** a confidential server-side client is used — that client's secret (supplied via a Secret). Concrete key names are intentionally **not asserted** here to avoid guessing (rule F).

### Worker scheduler (proposed)

> **Not available / to be confirmed.** The worker scheduler (Quartz.NET vs. Hangfire) is undecided, and the `WebVella.Erp.Worker` project does not exist yet. Its schedule settings would be defined here once the scheduler is chosen; no concrete key names are asserted, to avoid guessing.

### Observability (proposed)

> **Not available / to be confirmed.** No structured-logging or tracing stack exists in the codebase yet (no Serilog and no OpenTelemetry packages are referenced). The keys below are proposed. See [../architecture/observability.md](../architecture/observability.md).

Data-handling requirements the eventual logging/tracing configuration must satisfy:

- **Redaction / data classification.** Logs, traces, and log context **must never contain** credentials, tokens, signing keys, connection strings, or personal data (PII). Sensitive fields must be masked or omitted; the connection string is referenced by key name only, never logged as a value (rule D).
- **Correlation IDs.** An inbound correlation-ID header must be **validated and sanitized** before it is logged or propagated (bounded length, restricted character set); client-supplied IDs are not trusted verbatim, and a server-generated ID is used when the inbound value is missing or invalid.
- **OTLP endpoint vs. credentials.** The OTLP exporter **endpoint URL is configuration (non-secret) and must not embed credentials**. Any collector authentication (bearer token or headers) is a **separate Secret**, configured independently of the endpoint URL — never inlined into it.
- **Browser / SPA telemetry.** Exporting telemetry directly from the browser is **pending (Not available / to be confirmed)**: it requires an authenticated, CORS-scoped collector endpoint and must not expose an unauthenticated browser-facing collector.

| Proposed env var | Purpose | Default | Secret? |
|------------------|---------|---------|---------|
| `Settings__Serilog__MinimumLevel` | Minimum log level (for example `Information` or `Warning`). *Proposed — Not available / to be confirmed.* | `Not available / to be confirmed` | No |
| `Settings__Otlp__Endpoint` | OTLP exporter endpoint URL for traces, metrics, and logs. Must **not** embed credentials; collector auth is a separate Secret. *Proposed — Not available / to be confirmed.* | `Not available / to be confirmed` | No |

### Plugin directory (proposed)

> **Not available / to be confirmed.** The `.wvplugin` packaging format and the plugin host that would scan a directory for packages do not exist yet. See [../plugin-sdk/packaging-wvplugin.md](../plugin-sdk/packaging-wvplugin.md), which also documents that the plugin loader is **not** a security sandbox.

| Proposed env var | Purpose | Default | Secret? |
|------------------|---------|---------|---------|
| `Settings__PluginDirectory` | Directory from which `.wvplugin` plugin packages would be loaded. *Proposed — Not available / to be confirmed.* | `Not available / to be confirmed` | No |

## Target runtime

> **Not available / to be confirmed.** The authoritative target runtime is unresolved: the refactor specification states ".NET 9", while the core project currently targets `net10.0`. Source: /WebVella.Erp/WebVella.Erp.csproj:L4 This page does not assert either value; the resolved target framework must be confirmed and recorded here once the decision is made.

## See also

- [docker-compose.md](docker-compose.md) — Docker Compose topology that would consume these variables.
- [kubernetes-helm.md](kubernetes-helm.md) — Helm values and Kubernetes Secret wiring for these keys.
- [troubleshooting.md](troubleshooting.md) — common configuration failure modes and remedies.
- [../architecture/security.md](../architecture/security.md) — authentication architecture, JWT/OIDC, and claim mapping.
- [../architecture/observability.md](../architecture/observability.md) — structured logging, correlation IDs, and OTLP export.
- [../plugin-sdk/packaging-wvplugin.md](../plugin-sdk/packaging-wvplugin.md) — `.wvplugin` packaging and the plugin directory.
