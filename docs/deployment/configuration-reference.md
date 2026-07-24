<!--{"sort_order":4, "name": "configuration-reference", "label": "Configuration Reference"}-->
# Configuration Reference

The WebVella ERP platform is configured through **environment variables** — mapped from the legacy `Settings` section of `WebVella.Erp.Site/Config.json` — together with **Kubernetes Secrets** for every sensitive value in the container-native deployment. Source: /WebVella.Erp.Site/Config.json:L2 This page is the single, authoritative reference for each configuration key: its environment-variable name, the legacy key it maps to, its purpose, its safe default, and whether it is a secret. It supersedes the scattered `Config.json` / `ErpSettings` mentions elsewhere in the documentation.

> **No secrets in documentation (rule D).** Configuration is documented by **key name and safe default only**; this page never reproduces a literal secret value. Placeholders such as `<DB_CONNECTION_STRING>`, `<ENCRYPTION_KEY>`, and `<JWT_SIGNING_KEY>` stand in for real values. **Secrets must be provided via environment variables or Kubernetes Secrets and must never be committed to source control.**

## Settings

All keys below live under the `Settings` section of the legacy `Config.json` and bind to the application's settings object. Source: /WebVella.Erp.Site/Config.json:L2 Each key is exposed to the container-native platform as an environment variable using the ASP.NET Core `:` → `__` convention described under [Environment variable mapping](#environment-variable-mapping). Secret rows carry `— (required; provide via Secret)` in the **Default** column and must be supplied through a Secret — never hard-coded.

### Database

| Env Var | Maps to (legacy key) | Purpose | Default | Secret? |
|---------|----------------------|---------|---------|---------|
| `Settings__ConnectionString` | `Settings:ConnectionString` — Source: /WebVella.Erp.Site/Config.json:L4 | PostgreSQL connection string consumed by the Npgsql data-access layer. Source: /WebVella.Erp/WebVella.Erp.csproj:L61 | `— (required; provide via Secret)` | Yes |

### Encryption

| Env Var | Maps to (legacy key) | Purpose | Default | Secret? |
|---------|----------------------|---------|---------|---------|
| `Settings__EncryptionKey` | `Settings:EncryptionKey` — Source: /WebVella.Erp.Site/Config.json:L5 | Symmetric key used to encrypt and decrypt encrypted Entity fields. | `— (required; provide via Secret)` | Yes |

### JWT and OIDC

| Env Var | Maps to (legacy key) | Purpose | Default | Secret? |
|---------|----------------------|---------|---------|---------|
| `Settings__Jwt__Key` | `Settings:Jwt:Key` — Source: /WebVella.Erp.Site/Config.json:L25 | Symmetric signing key used to validate JWT bearer tokens. Source: /WebVella.Erp.Site/JWT_README.txt | `— (required; provide via Secret)` | Yes |
| `Settings__Jwt__Issuer` | `Settings:Jwt:Issuer` — Source: /WebVella.Erp.Site/Config.json:L26 | Expected JWT issuer (`iss`) claim. | `webvella-erp` | No |
| `Settings__Jwt__Audience` | `Settings:Jwt:Audience` — Source: /WebVella.Erp.Site/Config.json:L27 | Expected JWT audience (`aud`) claim. | `webvella-erp` | No |

New OIDC authority/client settings introduced by the refactor are documented under [New container-native settings](#new-container-native-settings); the authentication architecture and claim mapping are described in [../architecture/security.md](../architecture/security.md).

### Email and SMTP

| Env Var | Maps to (legacy key) | Purpose | Default | Secret? |
|---------|----------------------|---------|---------|---------|
| `Settings__EmailEnabled` | `Settings:EmailEnabled` — Source: /WebVella.Erp.Site/Config.json:L14 | Master switch for outbound email delivery. | `false` | No |
| `Settings__EmailSMTPServerName` | `Settings:EmailSMTPServerName` — Source: /WebVella.Erp.Site/Config.json:L15 | SMTP server host name. | `—` | No |
| `Settings__EmailSMTPPort` | `Settings:EmailSMTPPort` — Source: /WebVella.Erp.Site/Config.json:L16 | SMTP server port. | `—` | No |
| `Settings__EmailSMTPUsername` | `Settings:EmailSMTPUsername` — Source: /WebVella.Erp.Site/Config.json:L17 | SMTP authentication user name. | `— (required; provide via Secret)` | Yes |
| `Settings__EmailSMTPPassword` | `Settings:EmailSMTPPassword` — Source: /WebVella.Erp.Site/Config.json:L18 | SMTP authentication password. | `— (required; provide via Secret)` | Yes |
| `Settings__EmailFrom` | `Settings:EmailFrom` — Source: /WebVella.Erp.Site/Config.json:L19 | Default `From` address for outbound email. | `—` | No |
| `Settings__EmailTo` | `Settings:EmailTo` — Source: /WebVella.Erp.Site/Config.json:L20 | Default `To` address used for diagnostics/testing. | `—` | No |

### Storage

| Env Var | Maps to (legacy key) | Purpose | Default | Secret? |
|---------|----------------------|---------|---------|---------|
| `Settings__EnableFileSystemStorage` | `Settings:EnableFileSystemStorage` — Source: /WebVella.Erp.Site/Config.json:L12 | Toggles the file-system storage backend (Storage.Net). | `—` | No |
| `Settings__FileSystemStorageFolder` | `Settings:FileSystemStorageFolder` — Source: /WebVella.Erp.Site/Config.json:L13 | Root folder used when file-system storage is enabled. | `—` | No |

### Localization

| Env Var | Maps to (legacy key) | Purpose | Default | Secret? |
|---------|----------------------|---------|---------|---------|
| `Settings__Lang` | `Settings:Lang` — Source: /WebVella.Erp.Site/Config.json:L6 | Default language code. | `—` | No |
| `Settings__Locale` | `Settings:Locale` — Source: /WebVella.Erp.Site/Config.json:L7 | Default locale (culture). | `—` | No |
| `Settings__TimeZoneName` | `Settings:TimeZoneName` — Source: /WebVella.Erp.Site/Config.json:L8 | Default time-zone name. | `—` | No |

### Runtime and feature flags

| Env Var | Maps to (legacy key) | Purpose | Default | Secret? |
|---------|----------------------|---------|---------|---------|
| `Settings__DevelopmentMode` | `Settings:DevelopmentMode` — Source: /WebVella.Erp.Site/Config.json:L10 | Enables development-only behavior; disable in production. | `—` | No |
| `Settings__EnableBackgroundJobs` | `Settings:EnableBackgroundJobs` — Source: /WebVella.Erp.Site/Config.json:L11 | Enables the in-process job scheduler. In the container-native model this governs whether the host runs jobs itself or defers them to `WebVella.Erp.Worker`. | `—` | No |
| `Settings__CacheKey` | `Settings:CacheKey` — Source: /WebVella.Erp.Site/Config.json:L9 | Cache-busting key; if empty, the current date is used. | `—` | No |
| `Settings__AppName` | `Settings:AppName` — Source: /WebVella.Erp.Site/Config.json:L21 | Display name of the application. | `—` | No |
| `Settings__NavLogoUrl` | `Settings:NavLogoUrl` — Source: /WebVella.Erp.Site/Config.json:L22 | URL of the navigation logo. | `—` | No |
| `Settings__SystemMasterBackgroundImageUrl` | `Settings:SystemMasterBackgroundImageUrl` — Source: /WebVella.Erp.Site/Config.json:L23 | URL of the sign-in / master background image. | `—` | No |

> **Legacy API URL template.** The legacy key `Settings:ApiUrlTemplates:FieldInlineEdit` used a `/api/v3/...` route template. Source: /WebVella.Erp.Site/Config.json:L35 In the headless target the REST surface is versioned under `/api/v1/`; the legacy template is retained only for backward-compatibility mapping, and its literal value is not reproduced here.

## New container-native settings

The refactor introduces configuration that has no legacy `Config.json` equivalent. Where the provider or tool is undecided, the setting is rendered as an explicit **"Not available / to be confirmed"** callout and the required inputs are listed, rather than assuming a value (rule F).

### OIDC / identity provider

> **Not available / to be confirmed.** The identity provider (Duende IdentityServer vs. Keycloak) is undecided. Until it is chosen, the OIDC keys below are provider-neutral. **What is needed:** the authority / discovery URL, the client id, the client secret, and the requested scope names. See [../architecture/security.md](../architecture/security.md) for the authentication architecture and claim-to-role/permission mapping.

| Env Var | Maps to (legacy key) | Purpose | Default | Secret? |
|---------|----------------------|---------|---------|---------|
| `Settings__Oidc__Authority` | — (new) | OIDC authority / discovery-document base URL. | `Not available / to be confirmed` | No |
| `Settings__Oidc__ClientId` | — (new) | OIDC client identifier for the platform. | `Not available / to be confirmed` | No |
| `Settings__Oidc__ClientSecret` | — (new) | OIDC client secret used in the authorization-code flow. | `— (required; provide via Secret)` | Yes |
| `Settings__Oidc__Scopes` | — (new) | Space-separated OIDC scope names requested at login. | `Not available / to be confirmed` | No |

### Worker scheduler

> **Not available / to be confirmed.** The worker scheduler (Quartz.NET vs. Hangfire) is undecided. These settings configure the `WebVella.Erp.Worker` schedules once the scheduler is chosen; no concrete key names are asserted here to avoid guessing.

### Observability

Structured logging and tracing for the container-native platform. See [../architecture/observability.md](../architecture/observability.md).

| Env Var | Maps to (legacy key) | Purpose | Default | Secret? |
|---------|----------------------|---------|---------|---------|
| `Settings__Serilog__MinimumLevel` | — (new) | Minimum Serilog log level (for example `Information` or `Warning`). | `—` | No |
| `Settings__Otlp__Endpoint` | — (new) | OTLP exporter endpoint for traces, metrics, and logs. Treat as a secret if the endpoint embeds credentials. | `—` | No |

### Plugin directory

The filesystem directory the plugin host scans for packaged `.wvplugin` artifacts. See [../plugin-sdk/packaging-wvplugin.md](../plugin-sdk/packaging-wvplugin.md).

| Env Var | Maps to (legacy key) | Purpose | Default | Secret? |
|---------|----------------------|---------|---------|---------|
| `Settings__PluginDirectory` | — (new) | Directory from which `.wvplugin` plugin packages are loaded. | `—` | No |

## Environment variable mapping

ASP.NET Core maps hierarchical configuration keys to environment variables by replacing each `:` separator with a double underscore `__`. Because the platform binds the `Settings` section, every key in the tables above is set as an environment variable as follows:

- `Settings:ConnectionString` → `Settings__ConnectionString`
- `Settings:Jwt:Issuer` → `Settings__Jwt__Issuer`
- `Settings:Jwt:Key` → `Settings__Jwt__Key`

```bash
# Environment-variable names only. Secret values are injected from a Secret at runtime,
# never written inline or committed to source control.
Settings__ConnectionString=<DB_CONNECTION_STRING>
Settings__EncryptionKey=<ENCRYPTION_KEY>
Settings__Jwt__Key=<JWT_SIGNING_KEY>
Settings__Jwt__Issuer=webvella-erp
Settings__Jwt__Audience=webvella-erp
Settings__EmailEnabled=false
```

## Target runtime

> **Not available / to be confirmed.** The authoritative target runtime is unresolved: the refactor specification states ".NET 9", while the core project currently targets `net10.0`. Source: /WebVella.Erp/WebVella.Erp.csproj:L4 This page does not assert either value; the resolved target framework must be confirmed and recorded here once the decision is made.

## See also

- [docker-compose.md](docker-compose.md) — Docker Compose topology that consumes these variables.
- [kubernetes-helm.md](kubernetes-helm.md) — Helm values and Kubernetes Secret wiring for these keys.
- [troubleshooting.md](troubleshooting.md) — common configuration failure modes and remedies.
- [../architecture/security.md](../architecture/security.md) — authentication architecture, JWT/OIDC, and claim mapping.
- [../architecture/observability.md](../architecture/observability.md) — structured logging, correlation IDs, and OTLP export.
- [../plugin-sdk/packaging-wvplugin.md](../plugin-sdk/packaging-wvplugin.md) — `.wvplugin` packaging and the plugin directory.
