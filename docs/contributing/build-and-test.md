<!--{"sort_order":1, "name": "build-and-test", "label": "Build & Test"}-->
# Build & Test

This guide covers building, running, and testing the headless **WebVella ERP** platform for contributors: the `WebVella.Erp.Api` REST host, the `WebVella.Erp.Client` React SPA, and the `WebVella.Erp.Worker` background host, all layered on the unchanged `WebVella.Erp` core engine and a PostgreSQL database (AAP §0.5.1). The API, client, and worker projects are introduced by the headless refactor and **do not exist in the checkout yet** (AAP §0.2.2); the commands below describe the **target** workflow against the solution at `Source: /WebVella.ERP3.sln`.

## Prerequisites

Install the tooling below before building. Configuration is supplied by environment-variable **name** only — for example, the database connection string is bound from the `Settings:ConnectionString` configuration key (environment form `Settings__ConnectionString`). `Source: /WebVella.Erp/ErpSettings.cs:L65`. Never commit real connection strings, passwords, or tokens (rule D).

| Prerequisite | Purpose | Notes |
|--------------|---------|-------|
| PostgreSQL | Primary datastore; the core engine connects to it through the Npgsql client. | The target PostgreSQL major version is **Not available / to be confirmed**. |
| Docker + Docker Compose | Container-native local stack (`api`, `worker`, `migrator`, `db`, `idp`). | See [Docker Compose](../deployment/docker-compose.md) for the full topology. |
| .NET SDK | Restores and builds the .NET solution. | Version **Not available / to be confirmed (.NET 9 vs net10.0)** — see the note below. |
| Node.js + npm | Builds and runs the `WebVella.Erp.Client` React SPA. | Required only for the SPA workflow (AAP §0.4). |

> **Note — the required .NET SDK is a decision point (rule F).** `global.json` exists but its SDK version pin is **commented out**, so no SDK version is currently enforced. `Source: /global.json` The core project targets `net10.0` (`Source: /WebVella.Erp/WebVella.Erp.csproj:L4`), while the refactor specification references ".NET 9"; the root README frames this as an open ".NET 9 vs net10.0" decision (`Source: /README.md:L32`). The authoritative target framework is therefore **Not available / to be confirmed (.NET 9 vs net10.0)** and must not be assumed; the missing authority is the project-wide target-framework decision, to be recorded by uncommenting the `global.json` SDK pin and reconciling it with the refactor specification.

## Build the solution

Restore dependencies and build the entire solution from the repository root:

```bash
dotnet restore WebVella.ERP3.sln
dotnet build WebVella.ERP3.sln
```

This builds the core engine, the bundled plugins, and — once the headless refactor lands — the new `WebVella.Erp.Api` and `WebVella.Erp.Worker` projects. The solution currently comprises **17 projects** — the `WebVella.Erp` core engine, `WebVella.Erp.Web`, six plugins (Crm, Mail, MicrosoftCDM, Next, Project, SDK), seven Site host projects, the Blazor WebAssembly client, and `WebVella.Erp.ConsoleApp` — and the three new headless projects (`WebVella.Erp.Api`, `WebVella.Erp.Client`, `WebVella.Erp.Worker`) are additive, not yet present. `Source: /WebVella.ERP3.sln`; AAP §0.2.2.

## Run the services

The headless platform runs as three cooperating processes. The `WebVella.Erp.Api`, `WebVella.Erp.Worker`, and `WebVella.Erp.Client` projects are introduced by the headless refactor and do not exist in the checkout yet (AAP §0.2.2); the commands below are the target workflow.

### API host

```bash
dotnet run --project WebVella.Erp.Api
```

Serves the `/api/v1/` REST surface. The generated **OpenAPI 3.1 document** is served at `/openapi/v1.json`; the interactive **Scalar reference UI** (at `/scalar`) is enabled in the **Development environment only** — see [OpenAPI reference](../api-reference/openapi.md) (AAP §0.6.1).

### Background worker

```bash
dotnet run --project WebVella.Erp.Worker
```

Hosts the scheduled background jobs — for example the SMTP email queue and the daily project-task starter (AAP §0.2.2). The job **scheduler is Not available / to be confirmed (Quartz.NET vs Hangfire)** (AAP §0.1.4). See [Background jobs](../developer/background-jobs/overview.md) for the job catalog.

### React SPA client

From the `WebVella.Erp.Client` project directory:

```bash
npm install
npm run dev
```

`npm run dev` starts the Vite development server. The SPA is built with Vite, TanStack Query, and Radix UI + Tailwind CSS (AAP §0.4).

## Testing

There are **currently no test projects** in the repository. `Source: AAP §0.9.2` Testing is therefore described as the **intended** approach rather than something already present.

Integration tests will use **Testcontainers** to start an ephemeral PostgreSQL container per test run and exercise the API against a real database, so no pre-provisioned server is required (AAP §0.6.1). Once test projects are added to the solution, they run with:

```bash
dotnet test WebVella.ERP3.sln
```

Until then, `dotnet test` finds no test projects; the command becomes active as soon as the first test project is added. `Source: AAP §0.9.2`

## Container build & CI

For the full local stack — `api`, `worker`, one-shot `migrator`, `db`, and `idp` — use Docker Compose instead of starting each service by hand: see [Docker Compose](../deployment/docker-compose.md). The continuous-integration pipeline (solution build, OpenAPI lint, and documentation build) is documented in [CI/CD](../deployment/ci-cd.md).

The documentation site is built **non-interactively** with `mkdocs build`; this is the command CI and automation use. `Source: /mkdocs.yml` For authoring and previewing docs locally, see the [Documentation](documentation.md) guide.

> **Warning — local / human use only.** `mkdocs serve` (and any `--watch` mode) starts a long-running server and must **never** be run in CI or automation; use `mkdocs build` there instead (AAP §0.10.1).
