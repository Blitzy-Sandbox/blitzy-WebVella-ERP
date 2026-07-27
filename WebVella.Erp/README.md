# WebVella.Erp — Core Engine

**WebVella.Erp** is the core .NET class library (`net10.0`, package version `1.7.7`) of the open-source [WebVella ERP](https://webvella.com) platform — a metadata-driven engine that models business data as **Entities**, **Records**, and relations, queries them through **EQL** (Entity Query Language), and extends them through **plugins** and **hooks**. Source: `WebVella.Erp/WebVella.Erp.csproj:L4,L11,L16`.

This package is a **library / NuGet package** that, under the headless refactor, is **designed to be consumed** by the platform's (planned) headless hosts — the REST API (`WebVella.Erp.Api`), the background worker (`WebVella.Erp.Worker`), and the plugin hosts. The core engine is **unchanged in code** by the headless refactor: it continues to expose the same in-process managers, and this document describes that existing behavior. Source: `WebVella.Erp/WebVella.Erp.csproj:L6` (OutputType `Library`).

> **Planned (headless refactor — not yet implemented).** The headless hosts and contracts named throughout this README as consumers — `WebVella.Erp.Api`, `WebVella.Erp.Worker`, the new `IErpPlugin` contract, and the `/api/v1/` REST surface — **do not exist in this checkout yet**; they are delivered by separate implementation workstreams (AAP §0.9.2). Everything described below about the **core engine itself** (its managers, the legacy `ErpPlugin` base, `ErpSettings` / JSON configuration, and the package facts) is **current** and verified against source.

| Fact | Value | Source |
|------|-------|--------|
| Package id | `WebVella.Erp` | `WebVella.Erp/WebVella.Erp.csproj:L7` |
| Version | `1.7.7` | `WebVella.Erp/WebVella.Erp.csproj:L11` |
| Target framework | `net10.0` | `WebVella.Erp/WebVella.Erp.csproj:L4` |
| Output type | `Library` | `WebVella.Erp/WebVella.Erp.csproj:L6` |
| License | `Apache-2.0` | `WebVella.Erp/WebVella.Erp.csproj:L14` |
| Project URL | <https://webvella.com> | `WebVella.Erp/WebVella.Erp.csproj:L16` |

> **Target-runtime note.** This package targets `net10.0` (Source: `WebVella.Erp/WebVella.Erp.csproj:L4`). The platform-wide authoritative runtime (the specification cites ".NET 9" while most projects declare `net10.0`) is an open decision point — **Not available / to be confirmed**; see the [deployment configuration reference](../docs/deployment/configuration-reference.md).

---

## What it does

The core engine provides the metadata-driven data model, its PostgreSQL persistence, the system bootstrap, and the extension points (plugins, hooks, background jobs) the platform is built on.

### System bootstrap

`IErpService` is the public bootstrap contract and `ErpService` is its concrete implementation. Source: `WebVella.Erp/IErpService.cs`, `WebVella.Erp/ERPService.cs:L13`. Together they orchestrate startup: they initialize the PostgreSQL schema and the system entities (`user`, `role`, `user_file`), seed the default users and roles, run versioned database upgrades, ensure the required tables / indexes / extensions / casts exist, wire up plugins, forward AutoMapper configuration, and initialize and start the background-job and schedule managers. Source: `WebVella.Erp/ERPService.cs:L18,L28,L30,L34,L36`.

**Public contract** (rule B) — Source: `WebVella.Erp/IErpService.cs`:

| Member | Purpose | Inputs / outputs | Side effects & error modes |
|--------|---------|------------------|----------------------------|
| `InitializeSystemEntities()` | Create/upgrade the system schema and seed system entities | — / `void` | Creates and alters PostgreSQL schema inside a transaction; throws on a failed entity/field creation or insufficient DB privileges. Source: `WebVella.Erp/ERPService.cs:L18,L34,L106` |
| `InitializePlugins(IServiceProvider app)` | Run each registered plugin's startup hook | `IServiceProvider` / `void` | Invokes each plugin's `Initialize`; an exception thrown by a plugin aborts startup. Source: `WebVella.Erp/IErpService.cs:L14` |
| `SetAutoMapperConfiguration()` | Aggregate and apply the AutoMapper configuration (core + plugins) | — / `void` | Configures the process-wide mapper. Source: `WebVella.Erp/IErpService.cs:L15` |
| `InitializeBackgroundJobs(List<JobType> additionalJobTypes = null)` | Register built-in and additional job types | optional `List<JobType>` / `void` | Prepares the job manager; scheduling is inert until started. Source: `WebVella.Erp/IErpService.cs:L12` |
| `StartBackgroundJobProcess()` | Start the background-job processing loop | — / `void` | Begins the job/schedule worker. Source: `WebVella.Erp/IErpService.cs:L13` |
| `Plugins { get; set; }` | The registered `ErpPlugin` instances | `List<ErpPlugin>` | Populated by the host before `InitializePlugins` runs. Source: `WebVella.Erp/IErpService.cs:L9` |

> **Side effect:** the bootstrap creates and alters the PostgreSQL schema on startup — required extensions, casts, system tables, and seed data. Source: `WebVella.Erp/ERPService.cs:L28,L30,L36`.

### Plugin abstraction (legacy base)

The abstract `ErpPlugin` base carries JSON manifest metadata (`name`, `prefix`, `url`, `version`, `company`, `author`, …) and the legacy lifecycle hooks. Source: `WebVella.Erp/ErpPlugin.cs:L12,L14-L51`.

- `Initialize(IServiceProvider)` — legacy startup hook; override to register services/metadata. Source: `WebVella.Erp/ErpPlugin.cs:L57`.
- `SetAutoMapperConfiguration(MapperConfigurationExpression)` — contribute AutoMapper maps. Source: `WebVella.Erp/ErpPlugin.cs:L53`.
- `GetPluginData()` / `SavePluginData(string)` — read/write the plugin's row in the `plugin_data` table; both throw if the plugin `Name` is not set. Source: `WebVella.Erp/ErpPlugin.cs:L67,L87`.

> **Important:** `ErpPlugin` is the **legacy** plugin base and is the model in use today. The **new `IErpPlugin` contract** for the headless platform is **planned — not present in this checkout yet** (`IErpPlugin` has no implementation in source); it is slated to live alongside the `WebVella.Erp.Plugins.SDK` project — see the [IErpPlugin contract guide](../docs/plugin-sdk/ierplugin-contract.md).

### Configuration binding

`ErpSettings` is a static binder over `IConfiguration`; the host calls `ErpSettings.Initialize(IConfiguration)` once at startup to populate the strongly-typed settings used across the engine. Source: `WebVella.Erp/ErpSettings.cs:L7,L56`. See [Configuration](#configuration) below for the key reference.

### Subsystems

Top-level subsystem folders of the engine. Source: `WebVella.Erp/`:

| Folder | Responsibility |
|--------|----------------|
| `Api/` | In-process **managers** — `EntityManager`, `EntityRelationManager`, `RecordManager`, `SecurityManager` / `SecurityContext` — plus caching, CSV import/export, and search indexing. Source: `WebVella.Erp/Api/` |
| `Database/` | PostgreSQL persistence via **Npgsql** (DbContext / connection / transaction, repositories, file storage). Source: `WebVella.Erp/Database/` |
| `Eql/` | **E**ntity **Q**uery **L**anguage — an Irony grammar translated to PostgreSQL. Source: `WebVella.Erp/Eql/` |
| `Hooks/` | Attribute-driven hook discovery / registration / execution for record CRUD/search and relation events. Source: `WebVella.Erp/Hooks/` |
| `Jobs/` | Background jobs and scheduling. Source: `WebVella.Erp/Jobs/` |
| `Notifications/` | PostgreSQL **LISTEN/NOTIFY** pub/sub. Source: `WebVella.Erp/Notifications/` |
| `Recurrence/` | Recurrence plans expanded via **Ical.Net**. Source: `WebVella.Erp/Recurrence/` |
| `Fts/` | Bulgarian full-text analysis (embedded stemming rules). Source: `WebVella.Erp/Fts/`, `WebVella.Erp/WebVella.Erp.csproj:L37-L39` |
| `Diagnostics/` | DB-backed logging over the `system_log` table. Source: `WebVella.Erp/Diagnostics/` |
| `Exceptions/`, `Utilities/` | Validation / exception aggregation and cross-cutting helpers. Source: `WebVella.Erp/Exceptions/`, `WebVella.Erp/Utilities/` |

> **These in-process managers are unchanged by the headless refactor and are distinct from the planned REST surface** that `WebVella.Erp.Api` (`/api/v1/`) will expose (**not yet built** — AAP §0.9.2). They are invoked in-process (for example `new RecordManager().CreateRecord(...)`), whereas the planned REST API would be an HTTP layer built on top of them. See the [in-process server-API reference](../docs/developer/server-api/overview.md) and the [data-access architecture](../docs/architecture/data-access.md).

---

## How to build, run, and test

**Build / pack** — the library is built as part of the solution and packed as a NuGet package (it is a class library, not a runnable host):

```bash
dotnet build WebVella.ERP3.sln
dotnet pack WebVella.Erp/WebVella.Erp.csproj -c Release
```

Source: `WebVella.Erp/WebVella.Erp.csproj:L6` (OutputType `Library`), `:L18,L27` (this `README.md` is the packed `PackageReadmeFile`).

**Run** — the engine is consumed as a library by the API, worker, and plugin hosts; it is not started directly. It requires a **PostgreSQL** database. On first run the bootstrap auto-creates the schema, required extensions/casts, and seed data. Source: `WebVella.Erp/ERPService.cs:L18,L28,L30,L36`. For a full, runnable instance follow the repository [build & run instructions](../INSTRUCTIONS.md) and the [getting-started guide](../docs/developer/introduction/getting-started.md).

**Test** — **Not available.** No test project exists in the repository yet (rule F).

---

## Configuration

The engine reads configuration through `ErpSettings`, bound from `IConfiguration`. Source: `WebVella.Erp/ErpSettings.cs`. Keys are documented **by name only** — never commit real secret values (rule D). In the container-native model these keys are supplied as **environment variables / Kubernetes Secrets**; see the consolidated [configuration reference](../docs/deployment/configuration-reference.md).

**Database & security** (secret — set by name only; never print values):

| Key | Purpose | Default / notes |
|-----|---------|-----------------|
| `Settings:ConnectionString` | PostgreSQL connection string | **Secret** — no default committed. Source: `WebVella.Erp/ErpSettings.cs:L65` |
| `Settings:EncryptionKey` | Symmetric key for encrypted values | **Secret**; legacy misspelled fallback `Settings:EncriptionKey`. Source: `WebVella.Erp/ErpSettings.cs:L59-L64` |
| `Settings:Jwt:Key` | JWT signing key | **Secret** — value not shown. Source: `WebVella.Erp/ErpSettings.cs:L118` |
| `Settings:Jwt:Issuer` | JWT issuer | `webvella-erp`. Source: `WebVella.Erp/ErpSettings.cs:L119` |
| `Settings:Jwt:Audience` | JWT audience | `webvella-erp`. Source: `WebVella.Erp/ErpSettings.cs:L120` |

**Localization & formatting:**

| Key | Purpose | Default / notes |
|-----|---------|-----------------|
| `Settings:Lang` | UI language | `en`. Source: `WebVella.Erp/ErpSettings.cs:L66` |
| `Settings:Locale` | Culture / locale | `en-US`. Source: `WebVella.Erp/ErpSettings.cs:L73` |
| `Settings:TimeZoneName` | Default server time zone | `FLE Standard Time`. Source: `WebVella.Erp/ErpSettings.cs:L70` |
| `Settings:JsonDateTimeFormat` | JSON date/time format | `yyyy-MM-ddTHH:mm:ss.fff`. Source: `WebVella.Erp/ErpSettings.cs:L71` |
| `Settings:CacheKey` | Cache-busting key | Defaults to the current date. Source: `WebVella.Erp/ErpSettings.cs:L74` |

**Storage backends:**

| Key | Purpose | Default / notes |
|-----|---------|-----------------|
| `Settings:EnableFileSystemStorage` | Store files on the local filesystem | `false`. Source: `WebVella.Erp/ErpSettings.cs:L76` |
| `Settings:FileSystemStorageFolder` | Local file-storage folder | A local path (value not shown). Source: `WebVella.Erp/ErpSettings.cs:L77` |
| `Settings:EnableCloudBlobStorage` | Store files in cloud blob storage | `false`. Source: `WebVella.Erp/ErpSettings.cs:L79` |
| `Settings:CloudBlobStorageConnectionString` | Storage.Net blob connection string | **Secret** — value not shown. Source: `WebVella.Erp/ErpSettings.cs:L80` |

**Background jobs:**

| Key | Purpose | Default / notes |
|-----|---------|-----------------|
| `Settings:EnableBackgroundJobs` | Enable the background-job processor | `true`; legacy misspelled fallback `Settings:EnableBackgroungJobs`. Source: `WebVella.Erp/ErpSettings.cs:L82-L87` |

**Email / SMTP:**

| Key | Purpose | Default / notes |
|-----|---------|-----------------|
| `Settings:EmailEnabled` | Enable outbound email | `false`. Source: `WebVella.Erp/ErpSettings.cs:L99` |
| `Settings:EmailSMTPServerName` | SMTP host | — Source: `WebVella.Erp/ErpSettings.cs:L100` |
| `Settings:EmailSMTPPort` | SMTP port | `25`. Source: `WebVella.Erp/ErpSettings.cs:L101` |
| `Settings:EmailSMTPUsername` | SMTP username | — Source: `WebVella.Erp/ErpSettings.cs:L102` |
| `Settings:EmailSMTPPassword` | SMTP password | **Secret** — value not shown. Source: `WebVella.Erp/ErpSettings.cs:L103` |
| `Settings:EmailFrom` | Default From address | — Source: `WebVella.Erp/ErpSettings.cs:L104` |
| `Settings:EmailTo` | Default To address (testing) | — Source: `WebVella.Erp/ErpSettings.cs:L105` |

**Branding & misc:**

| Key | Purpose | Default / notes |
|-----|---------|-----------------|
| `Settings:AppName` | Application display name | — Source: `WebVella.Erp/ErpSettings.cs:L109` |
| `Settings:NavLogoUrl` | Navigation logo URL | — Source: `WebVella.Erp/ErpSettings.cs:L107` |
| `Settings:DevelopmentMode` | Enable development mode | `false`. Source: `WebVella.Erp/ErpSettings.cs:L111` |
| `Settings:ShowAccounting` | Show accounting features | `false`. Source: `WebVella.Erp/ErpSettings.cs:L113` |

> **Rule D reminder:** never commit real secrets. Reference secret keys — the connection string, encryption key, JWT signing key, SMTP password, and cloud-blob connection string — by **name only**, and supply their values via environment variables / Kubernetes Secrets.

---

## Troubleshooting

| Symptom | Likely cause | Remedy |
|---------|--------------|--------|
| Startup fails with a database connection / authentication error | Wrong or missing `Settings:ConnectionString`; PostgreSQL unreachable; insufficient privileges | Verify the connection-string key and that the DB user can reach the server and holds the required privileges. Source: `WebVella.Erp/ErpSettings.cs:L65` |
| Startup fails while creating extensions / casts | First-run bootstrap ensures the required PostgreSQL extensions/casts; the DB user lacks `CREATE EXTENSION` privilege | Grant the privilege or pre-create the extensions, then restart. Source: `WebVella.Erp/ERPService.cs:L28,L30` |
| Startup aborts during plugin load | An exception thrown from a plugin's `Initialize(IServiceProvider)` propagates and aborts bootstrap | Check plugin logs / the `system_log` table and validate the plugin manifest. Source: `WebVella.Erp/ErpPlugin.cs:L57`, `WebVella.Erp/IErpService.cs:L14` |
| Background jobs / schedules do not run | `Settings:EnableBackgroundJobs` is `false`, or the host never called `StartBackgroundJobProcess()` | Confirm the flag is `true` and that the host starts the job process. Source: `WebVella.Erp/ErpSettings.cs:L82`, `WebVella.Erp/IErpService.cs:L13` |

For operational (container / deployment) issues see the [deployment troubleshooting guide](../docs/deployment/troubleshooting.md).

---

## Dependencies

Third-party packages referenced by the core engine. Source: `WebVella.Erp/WebVella.Erp.csproj:L47-L63`:

| Package | Version |
|---------|---------|
| AutoMapper | `14.0.0` |
| CsvHelper | `33.1.0` |
| Ical.Net | `5.1.4` |
| Irony.NetCore | `1.1.11` |
| MimeMapping | `3.1.0` |
| Newtonsoft.Json | `13.0.4` |
| Npgsql | `9.0.4` |
| Storage.Net | `9.3.0` |
| System.Drawing.Common | `10.0.1` |

The project also declares `FrameworkReference Include="Microsoft.AspNetCore.App"`. Source: `WebVella.Erp/WebVella.Erp.csproj:L43`.

> **Security note (AutoMapper `14.0.0`)** — this pinned version is affected by a High-severity
> Denial-of-Service advisory, GHSA-rvv3-g6hj-g44x / CVE-2026-32933 (CWE-674 Uncontrolled Recursion;
> CVSS 3.1 base score 7.5): mapping a deeply nested object graph throws an uncatchable
> `StackOverflowException` and terminates the process. The fix ships in AutoMapper `15.1.1` / `16.1.1`
> — no `14.x` patch is planned — so remediation means moving to `>= 15.1.1` after compatibility and
> license validation. That upgrade is owned by the runtime dependency workstream; this README
> documents the risk only and changes no package version (AAP §0.9.2). The full dependency security
> inventory — including the transitive MimeKit CRLF advisory reached through the Mail plugin's MailKit
> — is in `LIBRARIES.md`, linked below.

For the full repository dependency inventory, see [LIBRARIES.md](../LIBRARIES.md).

---

## See also

> **Link note (publish order).** This README is packed as the NuGet package landing page; the `docs/**`, `INSTRUCTIONS.md`, and `LIBRARIES.md` links here (and above) are **absolute upstream URLs** on the repository's default branch. They resolve **once those files are published there** (pin them to a release tag for long-term stability); in a local checkout the same files live at the repository root and under `docs/`.

- [Build & run instructions (INSTRUCTIONS.md)](../INSTRUCTIONS.md)
- [Getting started](../docs/developer/introduction/getting-started.md)
- [In-process server-API reference](../docs/developer/server-api/overview.md)
- [Plugin SDK — IErpPlugin contract](../docs/plugin-sdk/ierplugin-contract.md)
- [Architecture — data access](../docs/architecture/data-access.md)
- [Deployment — configuration reference](../docs/deployment/configuration-reference.md)
- [Deployment — troubleshooting](../docs/deployment/troubleshooting.md)
- [Third-party libraries (LIBRARIES.md)](../LIBRARIES.md)

---

Licensed under **Apache-2.0**. Source: `WebVella.Erp/WebVella.Erp.csproj:L14`.
