# WebVella.Erp.Plugins.Next

The **Next** plugin provides the WebVella *Next* application on top of the core `WebVella.Erp` engine. It is implemented as `NextPlugin : ErpPlugin` and provisions the Next/CRM data model — entities, fields, relations and reference data — while keeping a denormalized full-text search index in sync. `Source: /WebVella.Erp.Plugins.Next/NextPlugin.cs:L8`

> **Refactor context.** This plugin currently derives from the legacy `ErpPlugin` base and is being migrated to the new **`IErpPlugin`** contract as part of the headless, container-native refactor. This README documents the plugin as it exists today; the `IErpPlugin` adoption is delivered by a separate code workstream. See:
>
> - Plugin SDK contract — [`../docs/plugin-sdk/ierplugin-contract.md`](../docs/plugin-sdk/ierplugin-contract.md)
> - Migrating from `ErpPlugin` — [`../docs/plugin-sdk/migrating-from-erpplugin.md`](../docs/plugin-sdk/migrating-from-erpplugin.md)
> - Plugin migration guide — [`../docs/migration/plugin-migration.md`](../docs/migration/plugin-migration.md)

## What it does

The plugin provisions and maintains the WebVella **Next** application — a contact/CRM-style data model — on the core engine. It is a partial class `NextPlugin : ErpPlugin` whose plugin identifier (`Name`, serialized as JSON `name`) is `"next"`. `Source: /WebVella.Erp.Plugins.Next/NextPlugin.cs:L8-L11`

On startup its `Initialize(IServiceProvider)` entry point opens a system security scope and runs `ProcessPatches()` — a linear, version-gated ladder of schema/data migrations. `Source: /WebVella.Erp.Plugins.Next/NextPlugin.cs:L13-L19` `Source: /WebVella.Erp.Plugins.Next/NextPlugin._.cs:L76-L184`

The patches seed the application's entities, fields, relations and reference data:

- **`20190203`** — initial provisioning of system entities (e.g. `timelog`), fields, relations and seed data. `Source: /WebVella.Erp.Plugins.Next/NextPlugin.20190203.cs`
- **`20190204`** — extends the `account` entity with CRM fields and creates the `contact`, `address`, `currency` and `language` entities. `Source: /WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs`
- **`20190205`** — adjusts `timelog.minutes` and adds `task.recurrence_template`. `Source: /WebVella.Erp.Plugins.Next/NextPlugin.20190205.cs`
- **`20190206`** — introduces the `salutation` entity. `Source: /WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs`
- **`20190222`** — normalizes the `task_type` lookup rows. `Source: /WebVella.Erp.Plugins.Next/NextPlugin.20190222.cs`

Representative CRM field names in the model (on entities such as `account`/`contact`) include `name`, `mobile_phone`, `notes`, `post_code`, `region`, `street`, `tax_id` and `website`. `Source: /WebVella.Erp.Plugins.Next/Configuration.cs:L9-L11`

Alongside seeding, the plugin keeps a denormalized `x_search` text value in sync on core entities. Post-create/post-update hooks for `account`, `case`, `contact` and `task` — each attached with `[HookAttachment("<entity>", int.MinValue)]` and implementing `IErpPostCreateRecordHook` + `IErpPostUpdateRecordHook` — call `SearchService.RegenSearchField(...)`. `Source: /WebVella.Erp.Plugins.Next/Hooks/Api/AccountHook.cs:L9-L20` `Source: /WebVella.Erp.Plugins.Next/Services/SearchService.cs:L16` The project is organized into `Hooks/`, `Model/` and `Services/` subfolders supporting this behavior.

## How to run / build / test

This is a .NET class-library plugin targeting `net10.0`, built with `Microsoft.NET.Sdk.Razor` (`AddRazorSupportForMvc=true`); it references `WebVella.Erp.Web` and `WebVella.ERP` (core). `Source: /WebVella.Erp.Plugins.Next/WebVella.Erp.Plugins.Next.csproj:L1-L16`

- **Build** — compiled as part of the solution, e.g. `dotnet build WebVella.ERP3.sln`. It is a library, not a standalone executable. `Source: /WebVella.Erp.Plugins.Next/WebVella.Erp.Plugins.Next.csproj:L1`
- **Run** — the plugin is loaded by the ERP host; its patches run automatically on host startup via `Initialize(...)`. There is no separate run step for the plugin itself. `Source: /WebVella.Erp.Plugins.Next/NextPlugin.cs:L13-L19`
- **Test** — **Not available.** There is no test project for this plugin in the repository; adding coverage would require introducing a test project targeting the plugin's public surface.

> **⚠️ Linux build blocker (known issue — source-side).** On a **case-sensitive filesystem** (the default on most Linux distributions) the `dotnet build WebVella.ERP3.sln` command **fails** with MSBuild **MSB3202**, because the solution and 14 `.csproj` files reference the core engine as `..\WebVella.ERP\WebVella.Erp.csproj` (upper-case `ERP`, backslash separators) while the directory on disk is `WebVella.Erp` (lower-case `Erp`) — **15 case-mismatched references** in total. Windows and macOS (case-insensitive by default) are unaffected. **Workaround:** create a case-alias symlink at the repository root — `ln -s WebVella.Erp WebVella.ERP` — then re-run the build. The permanent casing fix is an application-source change owned by the code workstream and is out of scope for this documentation set (AAP §0.9.2). Full note: [`INSTRUCTIONS.md`](../INSTRUCTIONS.md). Source: `/WebVella.ERP3.sln:L23` and the 14 `ProjectReference` entries in `/WebVella.Erp.*/*.csproj`.
>
> **Target framework — Not available / to be confirmed.** The project file targets `net10.0` `Source: /WebVella.Erp.Plugins.Next/WebVella.Erp.Plugins.Next.csproj:L4`, while the specification references ".NET 9" / "ASP.NET Core 9" and the root `README.md` frames this as an open ".NET 9 vs net10.0" decision. The authoritative platform target (.NET 9 vs `net10.0`) is an open decision point pending resolution; this README states `net10.0` per the manifest and does not resolve the ambiguity.

## Key configuration and defaults

Configuration keys are documented **by name only**; no secret values, connection strings or credentials appear here.

- **Persisted plugin version** — the plugin stores its state in the `plugin_data` store (the `data` text field, holding stringified JSON) via the inherited `GetPluginData()`/`SavePluginData(...)`; it is deserialized into the internal `PluginSettings` DTO whose `version` key marks the applied patch level. `Source: /WebVella.Erp.Plugins.Next/NextPlugin._.cs:L66-L69` `Source: /WebVella.Erp.Plugins.Next/Model/PluginSettings.cs:L5-L8`
- **Default init version** — when no persisted state exists, the version defaults to `WEBVELLA_NEXT_INIT_VERSION = 20190101`, so the full patch ladder runs on first initialization. `Source: /WebVella.Erp.Plugins.Next/NextPlugin._.cs:L15,L66`
- **Search-index field lists** — the fields included in the `x_search` index are configured as the static lists `AccountSearchIndexFields`, `CaseSearchIndexFields`, `ContactSearchIndexFields` and `TaskSearchIndexFields`. Each entry is either a direct field name or a `$relation.field` token (for example `$country_1n_account.label`). `Source: /WebVella.Erp.Plugins.Next/Configuration.cs:L9-L22`

Container-native/runtime configuration for the host (database connection, OIDC/JWT, plugin directory, logging, and similar) is documented centrally — see [`../docs/deployment/configuration-reference.md`](../docs/deployment/configuration-reference.md). Per the no-secrets rule, that reference names environment variables and Kubernetes Secrets only and never embeds literal secret values.

## Common failure modes & troubleshooting

- **Patch/init failure blocks startup.** `ProcessPatches()` runs the whole patch ladder inside a single database transaction; on any `ValidationException`/`Exception` the transaction is rolled back and the error rethrown, and the persisted version is advanced only on success (commit). A failing patch therefore surfaces as the plugin failing to initialize on host startup, with no partial version advance. `Source: /WebVella.Erp.Plugins.Next/NextPlugin._.cs:L189-L203`
- **Seeding conflicts.** If an entity, field or relation a patch tries to create already exists — or a hard-coded seed GUID collides — the underlying manager returns an error that the patch raises as an exception, and the surrounding transaction is rolled back. Inspect the specific patch (`NextPlugin.20190203.cs` … `NextPlugin.20190222.cs`) named in the error. `Source: /WebVella.Erp.Plugins.Next/NextPlugin._.cs:L86-L96,L194-L203`
- **Search / hook issues.** `SearchService.RegenSearchField(...)` silently skips a configured field or `$relation.field` token that is invalid or missing; if the target entity's metadata cannot be found it throws, and if the `x_search` record update fails it raises a `ValidationException`. Hooks are wired by attribute discovery at load time, so a mis-attached hook simply will not fire. `Source: /WebVella.Erp.Plugins.Next/Services/SearchService.cs:L16-L22,L28-L45,L148-L158`

For operational runbook detail (host startup, database and deployment issues), see [`../docs/deployment/troubleshooting.md`](../docs/deployment/troubleshooting.md).
