<!--{"sort_order":2, "name": "getting-started", "label": "Getting started"}-->
# Getting started

This guide onboards a developer to run WebVella ERP locally. The quickest way is with **Docker Compose** — the container-native stack — so you no longer need a desktop IDE or a hand-configured runtime. This page replaces the previous desktop-IDE setup instructions. Source: /docs/developer/introduction/getting-started.md (the drift being corrected).

> **Planned (headless refactor — not yet implemented).** The per-service container images and the Compose file used below are the intended container-native target and are produced by the code/build workstream; not all of these assets exist in the checkout yet. The full topology, service roles, and their greenfield status are documented in [Docker Compose](../../deployment/docker-compose.md).

## Quick start (Docker Compose)

From the repository root, bring the whole stack up with a single, non-interactive command:

```bash
docker compose up
```

This starts four services — **`db`** (PostgreSQL), **`api`** (the REST host serving `/api/v1/`), **`worker`** (background jobs), and **`migrator`**. The one-shot **`migrator` runs first**: it applies the database schema before the long-running **`api`** and **`worker`** services become ready, so nothing ever serves traffic against an un-migrated database.

- Full Compose topology, per-service roles, and startup ordering: [Docker Compose](../../deployment/docker-compose.md).
- Every setting these services consume — documented by **configuration key name only**, never as a literal value (rule D): [Configuration Reference](../../deployment/configuration-reference.md).

This page keeps only the intro-level `docker compose up`; the multi-step bring-up and the per-service detail live on the deployment page (progressive disclosure).

## Target runtime

> **Not available / to be confirmed — .NET 9 vs `net10.0`.** The authoritative target runtime is unresolved: the refactor specification states ".NET 9", while the core project currently declares `net10.0` and the SDK pin in `global.json` is commented out (no version is enforced). This page deliberately does not assert a single version; the resolved target framework must be confirmed here once the decision is made. Source: /WebVella.Erp/WebVella.Erp.csproj:L4 (`<TargetFramework>net10.0</TargetFramework>`); Source: /global.json (SDK version pin commented out).

## Database bootstrap

WebVella ERP creates its own schema and seed data on first run — you only need to provide an **empty PostgreSQL database**:

1. Create an empty PostgreSQL database for the ERP.
2. Supply the database connection to the stack by **configuration key name only** — never a literal connection string (rule D). The connection key, and every other setting, is listed in the [Configuration Reference](../../deployment/configuration-reference.md).
3. On startup the one-shot **`migrator`** service applies the schema and versioned patches **transactionally** (all-or-nothing), then exits; only after it succeeds do `api` and `worker` start. The full migration and rollback flow is documented in the [Database Migration Job](../../migration/database-migration-job.md).

## Running

Both onboarding paths end in the same `docker compose up` — the only difference is where the service images come from:

- **Build from the sources.** Clone the repository (`git clone https://github.com/WebVella/WebVella-ERP.git`) and run `docker compose up` from the repository root; Compose builds the service images and starts the `db`, `api`, `worker`, and `migrator` services.
- **Prebuilt / seed.** When published container images are available, the same `docker compose up` pulls them instead of building locally — no source checkout required. Image publication is part of the container-native target and is **Not available / to be confirmed** (see [Docker Compose](../../deployment/docker-compose.md)).

Once the `api` service is up, the application requires authentication. Sign in with the default first-run/seed account — email **`erp@webvella.com`**, password **`erp`**. This is the documented default seed credential; change it after first login.

To build your own components or plugins, include the `WebVella.Erp.Plugins.SDK` in your solution. It helps you create and manage ERP objects such as Entities and Relations, though it is not required — the ERP API can be used directly to manage these objects.
