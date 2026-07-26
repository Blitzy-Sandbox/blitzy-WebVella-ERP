# WebVella.Erp.Plugins.MicrosoftCDM

A Microsoft Common Data Model (CDM) plugin **scaffold** for WebVella ERP. **No CDM alignment is implemented yet** — the plugin is an empty, version-gated migration scaffold whose patch body is commented out (see *What it does* below).

## What it does

This project is a WebVella ERP plugin **scaffold** intended to integrate the platform with the **Microsoft Common Data Model (CDM)**. **Today it implements no CDM mapping and ships no admin UI** — its `ProcessPatches()` patch body is a commented-out template, so it only persists a version number and commits. `Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin._.cs:L60-L81` (commented `Patch20190123`), `:L86,L88` (only `SavePluginData` + `CommitTransaction` run). It is implemented as a partial class `MicrosoftCDMPlugin` that derives from the platform's legacy `ErpPlugin` base class — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin.cs:L8` — and it identifies itself to the host with `Name = "MicrosoftCDMPlugin"` — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin.cs:L11-L12`. The implementation is split across two partial files: `MicrosoftCDMPlugin.cs` (the lifecycle entry point) and `MicrosoftCDMPlugin._.cs` (the versioned patch/init runner).

**Startup behavior (behavioral surface).** The host invokes `Initialize(IServiceProvider serviceProvider)`, which opens a system security scope and calls `ProcessPatches()` — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin.cs:L14-L20`. `ProcessPatches()` prepares the platform managers `EntityManager`, `EntityRelationManager`, and `RecordManager` and reads the platform `SystemSettings`, then opens a single database transaction — but the schema/metadata/data patch invocation is **commented out**, so today it only persists the baseline version and commits (no CDM schema, metadata, or data is applied). `Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin._.cs:L17-L33` (managers + transaction), `:L60-L81` (commented patch). The intended side effect is idempotent, version-gated initialization; the error mode is an all-or-nothing rollback (see *Common failure modes & troubleshooting*).

**Project structure.** A `Model/` folder holds the internal `PluginSettings` DTO used to persist the plugin's version — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/Model/PluginSettings.cs`. A `wwwroot/` static-asset root exists but currently ships no functional assets — it contains only a placeholder file — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/wwwroot/emtpy.txt`.

**Build model.** The project is a Razor class library (`Microsoft.NET.Sdk.Razor`) that references `Microsoft.AspNetCore.Components` and `Microsoft.AspNetCore.Components.Web` 10.0.1 — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/WebVella.Erp.Plugins.MicrosoftCDM.csproj:L1,L10-L11`.

> **Refactor note.** Today this plugin inherits the legacy `ErpPlugin` base class — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin.cs:L8`. Under the headless refactor it is slated to adopt the new `IErpPlugin` contract (by adding a `PluginManifest.cs`); that code change is delivered by a separate workstream and is **not done yet**. See the [IErpPlugin contract](../docs/plugin-sdk/ierplugin-contract.md) and the [plugin migration guide](../docs/migration/plugin-migration.md).

## How to run/build/test

This is a .NET project targeting `net10.0` — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/WebVella.Erp.Plugins.MicrosoftCDM.csproj:L4` — built with the `Microsoft.NET.Sdk.Razor` SDK — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/WebVella.Erp.Plugins.MicrosoftCDM.csproj:L1`.

It is **not run standalone**. It is a plugin that is **host-loaded** by the WebVella ERP web host: it references `WebVella.Erp.Web` and `WebVella.Erp` — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/WebVella.Erp.Plugins.MicrosoftCDM.csproj:L16-L17` — and is compiled as part of the `WebVella.ERP3.sln` solution. Building the solution (or the host) compiles this plugin:

```bash
dotnet build WebVella.ERP3.sln
```

**Test:** Not available — there is no test project for this plugin in the repository.

## Key configs and defaults

The plugin persists its own settings/version as **stringified JSON** in the platform `plugin_data` entity's `data` text field — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin._.cs:L36-L37`. At init it loads that JSON via `GetPluginData()` and, when present, deserializes it into the `PluginSettings` model, then writes it back via `SavePluginData(JsonConvert.SerializeObject(...))` — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin._.cs:L52,L86`. The persisted shape is `PluginSettings.Version` (JSON key `version`) — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/Model/PluginSettings.cs` — and the baseline init version is `20200824` — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin._.cs:L15,L51`.

| Setting | Key | Default | Source |
|---|---|---|---|
| Plugin data version | `version` | `20200824` | Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin._.cs:L15,L51; Source: /WebVella.Erp.Plugins.MicrosoftCDM/Model/PluginSettings.cs |

- **CDM mapping settings:** Not available — no CDM-specific mapping keys are defined in this project's source today. Configure any such settings by **key name only** once they exist; do not invent keys or values.
- **Platform settings (no secrets):** platform-level configuration (database connection, JWT/OIDC, and similar) is referenced by **key name only** and supplied through environment variables / Kubernetes Secrets. No secret values appear anywhere in this documentation. See the [configuration reference](../docs/deployment/configuration-reference.md).

## Common failure modes & troubleshooting

- **CDM schema/mapping mismatch (only once mappings are implemented)** — no CDM entity/field mappings exist in the source today (the patch body is commented out), so this failure mode is **not reachable yet**. When mapping patches are implemented, a mismatched or missing mapping between the ERP metadata and the Common Data Model surface would surface here; reconcile the mapping against the CDM definitions before re-running init.
- **Patch/init version failure** — all patch work runs inside one transaction opened with `connection.BeginTransaction()` and committed with `connection.CommitTransaction()` — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin._.cs:L33,L88`. If a patch throws (a `ValidationException` or any other `Exception`), the whole transaction is **rolled back atomically** and the error is rethrown — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin._.cs:L91-L100`. The plugin's persisted `version` therefore advances only on a successful commit; a failed init leaves the previous version intact, so fix the root cause and restart the host to retry.
- **Static-asset load issues** — the `wwwroot/` root currently ships only a placeholder — `Source: /WebVella.Erp.Plugins.MicrosoftCDM/wwwroot/emtpy.txt`. Requests for expected static assets that were never added surface here as missing-asset errors.

See the [deployment troubleshooting guide](../docs/deployment/troubleshooting.md) for platform-wide diagnostics.

## Related documentation

- [Plugin SDK — the `IErpPlugin` contract](../docs/plugin-sdk/ierplugin-contract.md)
- [Migrating from `ErpPlugin`](../docs/plugin-sdk/migrating-from-erpplugin.md)
- [Plugin migration guide (bundled plugins)](../docs/migration/plugin-migration.md)
- [Configuration reference](../docs/deployment/configuration-reference.md)
- [Troubleshooting](../docs/deployment/troubleshooting.md)
