# WebVella.Erp.Plugins.SDK

The SDK plugin provides the WebVella ERP administration console — the **Software Development Kit**
application — the built-in admin UI for managing applications, entities, pages, data sources,
security/roles, users, jobs, logs, and tools, together with the plugin's versioned
migration/patch orchestration. It is a Razor class library (Source: `WebVella.Erp.Plugins.SDK.csproj:L1`)
packed as its NuGet package README (Source: `WebVella.Erp.Plugins.SDK.csproj:L12`) and is loaded by a
WebVella ERP host application at runtime (Source: `SdkPlugin.cs:L15`).

> **Hosting-model note.** This project currently uses the legacy `ErpPlugin` /
> `Initialize(IServiceProvider)` model (Source: `SdkPlugin.cs:L10,L15`). The headless refactor
> introduces a new asynchronous `IErpPlugin` contract (`OnLoadAsync` / `MapEndpoints` /
> `OnMigrateAsync`) that will replace it; that migration is **not** yet implemented in this checkout.
> See the [Plugin SDK — IErpPlugin contract](https://github.com/WebVella/WebVella-ERP/blob/master/docs/plugin-sdk/ierplugin-contract.md) and
> [Migrating from ErpPlugin to IErpPlugin](https://github.com/WebVella/WebVella-ERP/blob/master/docs/plugin-sdk/migrating-from-erpplugin.md) guides.

## What it does

- Implements the plugin using the legacy `ErpPlugin` base, registered under the plugin name `"sdk"`
  (Source: `SdkPlugin.cs:L10`, `SdkPlugin.cs:L12-L13`).
- On load, the host invokes `Initialize(IServiceProvider)`, which opens a system security scope and
  runs `SetSchedulePlans()` followed by `ProcessPatches()` (Source: `SdkPlugin.cs:L15-L22`).
- Seeds the **"Software Development Kit"** application and its admin areas (Design/Objects, Access,
  Server) through the versioned patches (Source: `SdkPlugin._.cs:L14-L17`, `SdkPlugin.20181215.cs`).
- Registers a **Daily** schedule plan named **"Clear job and error logs."**
  (Source: `SdkPlugin.cs:L73-L105`).
- Renders the admin console surface for applications, entities, pages, data sources, security/roles,
  users, jobs, logs, and tools (Source: `Pages/` feature folders
  `application/entity/page/data_source/role/user/job/log/tools`; project description
  `WebVella.Erp.Plugins.SDK.csproj:L13`).

## How to run, build, and test

- **Project type:** Razor class library / SDK-style project — `<Project Sdk="Microsoft.NET.Sdk.Razor">`
  with `<AddRazorSupportForMvc>true</AddRazorSupportForMvc>`
  (Source: `WebVella.Erp.Plugins.SDK.csproj:L1,L17`).
- **Target framework:** `net10.0`; package version `1.7.4`; license `Apache-2.0`
  (Source: `WebVella.Erp.Plugins.SDK.csproj:L4,L5,L10`).
- **Dependencies:** framework reference `Microsoft.AspNetCore.App`
  (Source: `WebVella.Erp.Plugins.SDK.csproj:L27`); project references `WebVella.Erp.Web` and
  `WebVella.Erp` (Source: `WebVella.Erp.Plugins.SDK.csproj:L44-L45`).
- **Build:** it builds as part of the solution `WebVella.ERP3.sln`:

  ```bash
  dotnet build WebVella.ERP3.sln
  ```

- **Run:** this is a library and does not run standalone; it is loaded by a WebVella ERP host
  application whose startup invokes the plugin's `Initialize` (Source: `SdkPlugin.cs:L15`).
- **Test:** **Not available.** There is no test project for this plugin in the repository. Adding
  coverage would require a dedicated test project (for example `WebVella.Erp.Plugins.SDK.Tests`)
  exercising the migration/patch runner and the admin endpoints.

## Key configuration and defaults

Configuration is documented **by key name only**; this plugin stores no secret values.

- **Plugin state/settings** are persisted as stringified JSON in the core `plugin_data` entity's
  `data` text field (Source: `SdkPlugin._.cs:L38-L39,L69,L151`). The settings model is
  `PluginSettings`, which holds a single `version` integer (Source: `Model/PluginSettings.cs`).
- **Seed version** is `WEBVELLA_SDK_INIT_VERSION = 20181001` (Source: `SdkPlugin._.cs:L12`); the
  migration runner applies ordered version patches `20181215 → 20190227 → 20200610 → 20201221 →
  20210429` (Source: `SdkPlugin._.cs:L79-L145`).
- **Legacy admin API:** the plugin's admin JSON API is served under `api/v3.0/p/sdk/...`
  (Source: `Controllers/AdminController.cs:L39,L54`) and authenticates via **cookie** authentication
  (Source: `Controllers/AdminController.cs:L16`). This is the **legacy** versioned + cookie-auth
  surface; the refactor's target public surface is **`/api/v1/` with OIDC/JWT bearer auth** — see the
  [REST API reference](https://github.com/WebVella/WebVella-ERP/blob/master/docs/api-reference/index.md).
- **Host-level secrets** (database connection string, JWT signing key, and similar) are **not**
  configured by this plugin; they belong to the host/deployment configuration reference and are
  referenced by name only.

## Common failure modes and troubleshooting

- **Migration/patch rollback.** All version patches run inside a single database transaction; if any
  patch throws, the transaction is rolled back atomically and the exception is rethrown, so a failed
  init version aborts the whole plugin initialization
  (Source: `SdkPlugin._.cs:L31-L35,L153-L160`). *Remedy:* inspect the logs, fix the failing patch or
  data, and restart so the migration re-runs from the last committed version.
- **Administrator-only sitemap mutations.** Sitemap mutation endpoints require the `administrator`
  role; non-admin callers are rejected (Source: `Controllers/AdminController.cs:L53`). *Remedy:*
  perform SDK admin changes with an administrator account.
- **Embedded-resource / Stencil bundle load issues.** The plugin embeds
  `Components\WvSdkPageSitemap\form.js` and all `Snippets\**\*.cs|*.html` as embedded resources
  (Source: `WebVella.Erp.Plugins.SDK.csproj:L35,L39-L40`) and ships compiled Stencil web-component
  bundles under `wwwroot/` (for example `wv-datasource-manage`, `wv-sitemap-manager`, `wv-pb-manager`).
  If the admin UI fails to render or scripts return 404, the assembly or its static web assets are
  likely missing. *Remedy:* confirm the plugin assembly and its static web assets are deployed with
  the host.
- For platform-wide operational issues, see the
  [operations troubleshooting guide](https://github.com/WebVella/WebVella-ERP/blob/master/docs/deployment/troubleshooting.md).

## Related documentation

> **Link note (NuGet package page + publish order).** This README is packed as the NuGet package landing page, where **relative `../docs/` links do not resolve**; the links below are therefore **absolute upstream URLs** on the repository's default branch. They resolve once the documentation set is published there (pin them to a release tag for long-term stability); in a local checkout the same pages live under the `docs/` folder.

- [Plugin SDK — IErpPlugin contract](https://github.com/WebVella/WebVella-ERP/blob/master/docs/plugin-sdk/ierplugin-contract.md)
- [Migrating from ErpPlugin to IErpPlugin](https://github.com/WebVella/WebVella-ERP/blob/master/docs/plugin-sdk/migrating-from-erpplugin.md)
- [REST API reference (`/api/v1/`)](https://github.com/WebVella/WebVella-ERP/blob/master/docs/api-reference/index.md)
- [Operations troubleshooting](https://github.com/WebVella/WebVella-ERP/blob/master/docs/deployment/troubleshooting.md)
