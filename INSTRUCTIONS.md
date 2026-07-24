# WebVella ERP — Build & Run Instructions

WebVella ERP is evolving into a **headless, container-native platform**: a REST/OpenAPI API host
(`WebVella.Erp.Api`, serving `/api/v1/`), a React single-page-application client
(`WebVella.Erp.Client`), and a background worker (`WebVella.Erp.Worker`) — all built on the unchanged
core engine (`WebVella.Erp`) and PostgreSQL, and extended through the `IErpPlugin` plugin contract.

This is the **quick, top-level guide** that takes you from a fresh clone to a running platform. The
deeper, authoritative guides live under [`docs/`](docs/):

- Getting started — [docs/developer/introduction/getting-started.md](docs/developer/introduction/getting-started.md)
- Docker Compose deployment — [docs/deployment/docker-compose.md](docs/deployment/docker-compose.md)
- Configuration reference — [docs/deployment/configuration-reference.md](docs/deployment/configuration-reference.md)
- Build & test workflow — [docs/contributing/build-and-test.md](docs/contributing/build-and-test.md)

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| **PostgreSQL** | The platform's database of choice. You need a reachable instance (local, container, or managed) and an empty database. |
| **Docker + Docker Compose** | The primary, container-native path (see the quick start below). Linux containers are the primary deployment target. |
| **.NET SDK** | Required for the local build path. **Target runtime: Not available / to be confirmed (.NET 9 vs .NET 10 / `net10.0`).** The project manifests currently declare `net10.0` (`WebVella.Erp/WebVella.Erp.csproj`) and `global.json` has **no active SDK pin** (its `sdk.version` is commented out), so install the SDK that matches the runtime confirmed for your checkout. |
| **Node.js + npm** | Required to build the new React SPA (`WebVella.Erp.Client`). |

## Quick start with Docker Compose (recommended)

The container-native path brings up the database, the API host, the worker, the one-shot database
**migrator**, and (optionally) an identity provider. The full topology and service definitions are
documented in [docs/deployment/docker-compose.md](docs/deployment/docker-compose.md).

```bash
# 1. Provide configuration as environment variables in a local .env file.
#    Use KEY NAMES with your own values — never commit real secrets (see Configuration below).
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

Once the API host is running:

- The **OpenAPI 3.1 document** is served at `/openapi/v1.json`.
- The interactive **Scalar API reference UI** is served at `/scalar` — **Development environment only**.

## Local development build (alternative)

### Backend (.NET)

```bash
# Restore and build the whole solution.
dotnet restore WebVella.ERP3.sln
dotnet build WebVella.ERP3.sln

# Run the REST API host.
dotnet run --project WebVella.Erp.Api

# Run the background worker in a separate shell.
# NOTE: the worker scheduler is an unresolved decision point (see Decision points below).
dotnet run --project WebVella.Erp.Worker
```

### Frontend (React SPA)

```bash
cd WebVella.Erp.Client
npm install
npm run dev     # Vite dev server for local development
npm run build   # production build
```

### Database

Create an **empty** PostgreSQL database and point the connection string at it. On first run the
platform's migration job creates the required schema and seed data — you do not create tables by
hand. The migration-job flow and its rollback path are documented in
[docs/migration/database-migration-job.md](docs/migration/database-migration-job.md).

## Configuration

Configuration is supplied as **environment variables** (and, in Kubernetes, **Secrets**) rather than
an on-disk `Config.json`. ASP.NET Core maps hierarchical keys by replacing each `:` with `__` (for
example `Settings:Jwt:Key` → `Settings__Jwt__Key`). The complete, authoritative list of keys,
defaults, and which values are secret is in
[docs/deployment/configuration-reference.md](docs/deployment/configuration-reference.md).

The configuration surface, **by key name only**:

- **Database** — `Settings__ConnectionString` (placeholder `<DB_CONNECTION_STRING>`).
- **Encryption** — `Settings__EncryptionKey` (placeholder `<ENCRYPTION_KEY>`).
- **JWT / OIDC** — `Settings__Jwt__Key` (placeholder `<JWT_SIGNING_KEY>`), `Settings__Jwt__Issuer`,
  `Settings__Jwt__Audience`, and the OIDC keys `Settings__Oidc__Authority`,
  `Settings__Oidc__ClientId`, `Settings__Oidc__ClientSecret`, `Settings__Oidc__Scopes` (the identity
  provider is an unresolved decision point — see below).
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
  declare `net10.0` (`WebVella.Erp/WebVella.Erp.csproj`) while the specification and root `README.md`
  reference ".NET 9"; `global.json` has no active SDK pin.
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
