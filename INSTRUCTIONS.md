# WebVella ERP — Build & Run Instructions

WebVella ERP is **evolving into** a headless, container-native platform: a REST/OpenAPI API host
(`WebVella.Erp.Api`, serving `/api/v1/`), a React single-page-application client
(`WebVella.Erp.Client`), and a background worker (`WebVella.Erp.Worker`) — all built on the unchanged
core engine (`WebVella.Erp`) and PostgreSQL, and extended through the planned `IErpPlugin` plugin
contract.

> **Status — the headless platform is not yet runnable from this checkout.** The
> `WebVella.Erp.Api`, `WebVella.Erp.Client`, and `WebVella.Erp.Worker` projects, the one-shot
> database **migrator**, the `/api/v1/` surface, the generated OpenAPI document, and the Docker
> Compose / Kubernetes assets **do not exist in this repository yet** — they are delivered by
> separate implementation workstreams. This checkout is still the legacy ASP.NET Core solution
> (RazorPages/Blazor hosts under `WebVella.Erp.Web`, `WebVella.Erp.Site*`, and
> `WebVella.Erp.WebAssembly`). The **Quick start with Docker Compose** and the API/worker/SPA
> commands below therefore describe the **target** workflow and are **not runnable today**; each is
> annotated with the specific missing project or asset. The **current** path that works against this
> checkout is the legacy solution build in
> [Local development build](#local-development-build-current-checkout). Source: `WebVella.ERP3.sln`
> (no `WebVella.Erp.Api`, `WebVella.Erp.Client`, or `WebVella.Erp.Worker` project present); repository
> root (no `docker-compose.yml`).

This is the **quick, top-level guide**. The deeper guides live under [`docs/`](docs/):

- Getting started — [docs/developer/introduction/getting-started.md](docs/developer/introduction/getting-started.md):
  the current onboarding guide for the headless platform (Docker Compose quick start, target runtime,
  and database bootstrap). ⚠️ This page documents a **default first-run demo sign-in**; reset it
  immediately after the first login and never reuse it outside local development.
- Docker Compose deployment — [docs/deployment/docker-compose.md](docs/deployment/docker-compose.md).
- Configuration reference — [docs/deployment/configuration-reference.md](docs/deployment/configuration-reference.md).
- Build & test workflow — [docs/contributing/build-and-test.md](docs/contributing/build-and-test.md).

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| **PostgreSQL** | The platform's database of choice. You need a reachable instance (local, container, or managed) and an empty database. |
| **.NET SDK** | Required for the current local build path. **Target runtime: Not available / to be confirmed (.NET 9 vs .NET 10 / `net10.0`).** The project manifests currently declare `net10.0` (`WebVella.Erp/WebVella.Erp.csproj:L4`) and `global.json` has **no active SDK pin** (its `sdk.version` is commented out — `global.json:L3`), so install the SDK that matches the runtime confirmed for your checkout. |
| **Docker + Docker Compose** | Required only for the **target** container-native workflow below, which is **not yet runnable** (the API/worker/migrator projects and Compose assets are absent). |
| **Node.js + npm** | Required only for the **target** React SPA (`WebVella.Erp.Client`), which **does not exist yet** (there is no `package.json` in the checkout). |

## Local development build (current checkout)

The commands in this section operate on the legacy solution that is actually present in the checkout.

### Backend (.NET) — current

```bash
# Restore and build the solution that exists in this checkout.
dotnet restore WebVella.ERP3.sln
dotnet build WebVella.ERP3.sln
```

> The `dotnet run --project WebVella.Erp.Api` and `dotnet run --project WebVella.Erp.Worker`
> commands belong to the **target** workflow and are **not runnable yet**: neither the
> `WebVella.Erp.Api` nor the `WebVella.Erp.Worker` project exists in `WebVella.ERP3.sln`. The
> runnable hosts in this checkout are the legacy `WebVella.Erp.Site*` projects (RazorPages/Blazor).

### Database

Create an **empty** PostgreSQL database and point the connection string at it.

> **Migration flow — Not available (target artifact does not exist yet).** The intended model is a
> one-shot database **migrator** that creates the schema and seed data before the API and worker
> start, with a startup gate and rollback path. No migrator project, orchestrator manifest, or target
> plugin host exists in this checkout, so the following are all **pending** and must be defined by the
> implementation workstream: the migrator project/entry point, the plugin migration **ordering
> contract**, the **transaction model** (all-plugin vs per-plugin), the process **exit-code**
> contract, the API/worker **startup gate**, and the deployment manifests. Today, plugin
> initialization runs inside each plugin's **own** transaction via the legacy `ErpPlugin.Initialize`
> model (Source: `WebVella.Erp/ErpPlugin.cs:57`; `WebVella.Erp.Plugins.SDK/SdkPlugin._.cs:35,153,158`).
> The planned migrator flow and rollback path will be documented in
> [docs/migration/database-migration-job.md](docs/migration/database-migration-job.md).

## Quick start with Docker Compose (target workflow — not yet runnable)

> **Not runnable today.** The Compose topology below is intended to bring up the database, the API
> host, the worker, the one-shot database **migrator**, and (optionally) an identity provider —
> **none of which exist yet**. There is no `docker-compose.yml`, no `Dockerfile` for the new hosts,
> and no `WebVella.Erp.Api` / `WebVella.Erp.Worker` project. Treat this section as the **target**
> design only. Source: `WebVella.ERP3.sln` (missing projects); repository root (no `docker-compose.yml`).
> The full topology is documented at [docs/deployment/docker-compose.md](docs/deployment/docker-compose.md).

```bash
# TARGET WORKFLOW — NOT RUNNABLE YET (the projects and assets above do not exist).
# 1. Provide configuration as environment variables in a local .env file.
#    Use KEY NAMES with your own values — never commit real secrets (see Configuration below).
#    NOTE: these are PROPOSED target key names, not yet confirmed against implementation.
cat > .env <<'EOF'
Settings__ConnectionString=<DB_CONNECTION_STRING>
Settings__EncryptionKey=<ENCRYPTION_KEY>
Settings__Jwt__Key=<JWT_SIGNING_KEY>
Settings__Jwt__Issuer=webvella-erp
Settings__Jwt__Audience=webvella-erp
EOF

# 2. Build and start the stack (db, migrator, api, worker; idp optional).
docker compose up --build -d

# 3. Follow the API and worker logs (optional).
docker compose logs -f api worker

# 4. Tear the stack down when finished.
docker compose down
```

Once the API host exists and is running (target behavior):

- The **OpenAPI 3.1 document** is planned to be served at `/openapi/v1.json`.
- The interactive **Scalar API reference UI** is planned at `/scalar` — **Development environment only**.

### Frontend (React SPA — target, does not exist yet)

```bash
# TARGET WORKFLOW — NOT RUNNABLE YET: the WebVella.Erp.Client project does not exist,
# and there is no package.json in the checkout.
cd WebVella.Erp.Client
npm install
npm run dev     # Vite dev server for local development
npm run build   # production build
```

## Configuration

### Current model (this checkout) — on-disk JSON config

The hosts present in this checkout read configuration from an on-disk JSON file (`Config.json`;
lowercased `config.json` in `WebVella.Erp.ConsoleApp`) via `AddJsonFile`, binding the `ErpSettings`
options object (keys such as `Jwt:Key`, `Jwt:Issuer`, `Jwt:Audience`, and the database connection
string). Source: `WebVella.Erp.Site/Startup.cs:43`; `WebVella.Erp.Web/ErpMvcExtensions.cs:51`;
`WebVella.Erp.ConsoleApp/Program.cs:40`.

### Proposed target model (container-native) — environment variables / Secrets

The headless refactor intends to supply configuration as **environment variables** (and, in
Kubernetes, **Secrets**) instead of an on-disk JSON file, with ASP.NET Core mapping hierarchical keys
by replacing each `:` with `__`. The specific **key names, defaults, precedence, and fail-fast
validation below are proposed and Not available / to be confirmed** — no target configuration
provider or options-validation code exists yet (requires `WebVella.Erp.Api`). The eventual
authoritative list will live in
[docs/deployment/configuration-reference.md](docs/deployment/configuration-reference.md).

Proposed configuration surface, **by key name only** (names pending confirmation):

- **Database** — `Settings__ConnectionString` (placeholder `<DB_CONNECTION_STRING>`).
- **Encryption** — `Settings__EncryptionKey` (placeholder `<ENCRYPTION_KEY>`).
- **JWT** — `Settings__Jwt__Key` (placeholder `<JWT_SIGNING_KEY>`), `Settings__Jwt__Issuer`,
  `Settings__Jwt__Audience`.
- **OIDC (external identity provider)** — concrete server-side key names are **illustrative, not
  settled**: the canonical [configuration reference](docs/deployment/configuration-reference.md#oidc-identity-provider-proposed)
  deliberately **does not assert** them pending the auth-provider decision (rule F). Illustratively
  they would follow the house `Settings__` convention (for example `Settings__Oidc__Authority`,
  `Settings__Oidc__ClientId`, `Settings__Oidc__Scopes`). The **browser SPA is a public client**
  and uses the **authorization-code flow with PKCE (S256)** where supported: it ships **no client
  secret**. A client secret applies only to a confidential (server-side) client and must never be
  delivered to the browser. State/nonce, redirect-URI allow-listing, and token-lifetime/refresh
  policy are part of this design; issuer, audience, signature, and token-storage specifics are
  **deferred until the provider and client code exist** (the identity provider is an unresolved
  decision point — see below, and [docs/architecture/security.md](docs/architecture/security.md)).
- **Worker scheduler** — schedule settings for `WebVella.Erp.Worker` (the scheduler is an unresolved
  decision point — see below).
- **Observability** — `Settings__Serilog__MinimumLevel`, `Settings__Otlp__Endpoint`.
- **Plugin directory** — `Settings__PluginDirectory` (the folder scanned for `.wvplugin` packages).

> **No secrets in source control (rule D).** This guide, any `.env` file, and the configuration
> documentation reference **key names and placeholders only**. Real connection strings, keys, and
> tokens must be injected through environment variables or Kubernetes Secrets and **must never be
> committed**.

## Decision points (unresolved)

Three platform decisions are **not yet resolved**; this guide flags them rather than assuming a
value. Confirm each for your checkout before relying on the affected step.

- **Target runtime — Not available / to be confirmed.** .NET 9 vs .NET 10 (`net10.0`). The manifests
  declare `net10.0` (`WebVella.Erp/WebVella.Erp.csproj:L4`) while the specification and root
  `README.md` reference ".NET 9"; `global.json` has no active SDK pin (`global.json:L3`).
- **Authentication provider — Not available / to be confirmed.** Duende IdentityServer vs Keycloak.
  The OIDC keys above are provider-neutral until this is chosen; see
  [docs/architecture/security.md](docs/architecture/security.md).
- **Worker scheduler — Not available / to be confirmed.** Quartz.NET vs Hangfire. The
  `WebVella.Erp.Worker` schedule keys depend on this choice.

## Documentation & further reading

- Documentation site (MkDocs + Backstage TechDocs) under [`docs/`](docs/): [API reference](docs/api-reference/),
  [Plugin SDK](docs/plugin-sdk/), [Architecture](docs/architecture/), [Migration](docs/migration/),
  [Deployment](docs/deployment/).
- Project overview — [README.md](README.md).
- Third-party libraries — [LIBRARIES.md](LIBRARIES.md).
- Authoring and previewing the docs site — [docs/contributing/documentation.md](docs/contributing/documentation.md).

```bash
# Build the documentation site locally (never use "serve" in CI/automation).
mkdocs build
```
