# WebVella.Erp.Plugins.Crm

CRM metadata/entity-seeding plugin for the WebVella ERP platform.

`WebVella.Erp.Plugins.Crm` is a bundled WebVella ERP plugin that installs and version-migrates the
CRM domain's metadata (entities, relations, and seed records) into the platform database. It ships
as an ASP.NET Core **Razor class library** and is **loaded by a WebVella ERP host at runtime** — it
is not a standalone application. Its only runtime logic today is a versioned, transactional
migration orchestrator; there is no background job, controller, or service in this project.

---

## What it does

- **Seeds and manages CRM metadata/entities.** The plugin creates and updates the CRM entities,
  relations, and records that the platform's CRM features depend on, packaged as a Razor class
  library plugin loaded by a WebVella ERP host.
- **Partial class `CrmPlugin` deriving from the plugin base `ErpPlugin`,** split across two files:
  - `CrmPlugin.cs` — plugin identity and lifecycle entry point. `Source: /WebVella.Erp.Plugins.Crm/CrmPlugin.cs:L10-L22`
  - `CrmPlugin._.cs` — database patch/initialization orchestration. `Source: /WebVella.Erp.Plugins.Crm/CrmPlugin._.cs:L15-L101`
- **Stable identifier `"crm"`.** The plugin's name is exposed via the overridden `Name` property and
  serialized under the JSON key `name`. `Source: /WebVella.Erp.Plugins.Crm/CrmPlugin.cs:L12-L13`
- **Startup lifecycle.** On startup the host calls `Initialize(IServiceProvider)`, which opens a
  system-level security scope (`SecurityContext.OpenSystemScope()`) and runs `ProcessPatches()`.
  `Source: /WebVella.Erp.Plugins.Crm/CrmPlugin.cs:L15-L21`
- **Versioned, transactional migrations.** `ProcessPatches()` instantiates `EntityManager`,
  `EntityRelationManager`, and `RecordManager` to create/alter entities, relations, and records
  inside a single database transaction, and advances a stored version number only after each patch
  commits. The baseline install version is `WEBVELLA_CRM_INIT_VERSION = 20190101`.
  `Source: /WebVella.Erp.Plugins.Crm/CrmPlugin._.cs:L13,L15-L101`
- **Persisted settings DTO.** The `Model/` folder holds `PluginSettings`, a small DTO carrying a
  single `Version` integer serialized under the JSON key `version`.
  `Source: /WebVella.Erp.Plugins.Crm/Model/PluginSettings.cs:L5-L9`

**Scope (do not over-claim).** CRM currently has **no** scheduled/background jobs, **no** controllers,
**no** `Services`/`Jobs`/`Api` subfolders, and **no** applied dated patch files — only a commented
`Patch20190123` template exists as a pattern for future migrations.
`Source: /WebVella.Erp.Plugins.Crm/CrmPlugin._.cs:L58-L79`

**Forward-looking refactor note (planned, not yet in source).** Under the headless, container-native
refactor the plugin adopts the new **`IErpPlugin`** contract: a `PluginManifest.cs` implementing
`IErpPlugin` replaces the legacy `ErpPlugin` / `Initialize(IServiceProvider)` model. This code is
**not present in this project yet**. See:

- Plugin SDK / `IErpPlugin` contract → [../docs/plugin-sdk/ierplugin-contract.md](../docs/plugin-sdk/ierplugin-contract.md)
- Plugin migration guide → [../docs/migration/plugin-migration.md](../docs/migration/plugin-migration.md)

---

## How to build, run, and test

- **Project type & target.** A .NET project using the Razor SDK (`Microsoft.NET.Sdk.Razor`),
  targeting **`net10.0`**, with a shared-framework reference to `Microsoft.AspNetCore.App`.
  `Source: /WebVella.Erp.Plugins.Crm/WebVella.Erp.Plugins.Crm.csproj:L1-L11`
- **Project references.** It references `WebVella.Erp.Web` (the web layer) and `WebVella.Erp` (the
  core engine). `Source: /WebVella.Erp.Plugins.Crm/WebVella.Erp.Plugins.Crm.csproj:L13-L16`
- **Build.** The project builds as part of the solution `WebVella.ERP3.sln`:

  ```bash
  # Build the whole solution (from the repository root)
  dotnet build WebVella.ERP3.sln

  # Or build only this plugin project
  dotnet build WebVella.Erp.Plugins.Crm/WebVella.Erp.Plugins.Crm.csproj
  ```

- **Run.** This plugin is **not** a standalone executable — it is a class-library plugin **loaded by
  a WebVella ERP host** at runtime. Its `Initialize` / `ProcessPatches` migrations run when the host
  starts.
- **Test.** **Not available.** There is no test project for this plugin in the repository. Adding
  coverage would require a dedicated test project (for example, `WebVella.Erp.Plugins.Crm.Tests`)
  exercising `ProcessPatches()` against a disposable database; no such project exists today.

Target-stack build/runtime details for the container-native platform are documented centrally — see
[../docs/deployment/configuration-reference.md](../docs/deployment/configuration-reference.md).

---

## Key configuration and defaults

The CRM plugin has **no dedicated external configuration**. Its only persisted state is a small
settings record stored as **stringified JSON in the `plugin_data` store** (the platform
`plugin_data` entity's `data` text field), read and written via the inherited `GetPluginData()` /
`SavePluginData(...)` methods. `Source: /WebVella.Erp.Plugins.Crm/CrmPlugin._.cs:L47-L54,L84`

| Setting (JSON key) | Type | Default | Meaning | Source |
|--------------------|------|---------|---------|--------|
| `version` | integer | `20190101` (`WEBVELLA_CRM_INIT_VERSION`) | Applied CRM patch/migration version stored in the plugin's `plugin_data` record | `/WebVella.Erp.Plugins.Crm/Model/PluginSettings.cs:L7-L8`; `/WebVella.Erp.Plugins.Crm/CrmPlugin._.cs:L13,L49` |

**No secrets.** This README, and CRM plugin configuration generally, name configuration keys **by
name only** and never contain credentials, tokens, or connection strings. Platform-level connection
settings (database, OIDC/JWT, and so on) are referenced by key name in the consolidated
[../docs/deployment/configuration-reference.md](../docs/deployment/configuration-reference.md).

---

## Common failure modes and troubleshooting

- **Patch/migration failure during startup.** If a patch throws a `ValidationException` (or any
  other `Exception`) while `ProcessPatches()` runs, the open database transaction is **rolled back**
  and the exception is **rethrown**, so the stored `version` is **not** advanced and any partial
  schema/data changes are undone. Symptom: host startup fails during CRM initialization. Remedy:
  inspect the startup logs, fix the failing patch or the conflicting data, and restart the host.
  `Source: /WebVella.Erp.Plugins.Crm/CrmPlugin._.cs:L27-L99`
- **Entity/metadata seeding conflicts.** Attempting to create entities, relations, or records that
  already exist or that violate platform validation raises a `ValidationException`, which is rolled
  back exactly as above. Remedy: reconcile the conflicting metadata/records before re-running.

For operational, cross-cutting troubleshooting (host startup, database connectivity, plugin
loading), see [../docs/deployment/troubleshooting.md](../docs/deployment/troubleshooting.md).
