<!--{"sort_order":2, "name": "migrating-from-erpplugin", "label": "Migrating from ErpPlugin"}-->
# Migrating from ErpPlugin to IErpPlugin

This guide is a step-by-step port of a single existing plugin from the legacy `ErpPlugin` base-class model to the new `IErpPlugin` contract. The model changed because the headless platform loads each plugin into an isolated, collectible `AssemblyLoadContext` and drives it through an **asynchronous** load lifecycle, instead of newing up a base class inside a monolithic Razor host. The canonical contract — the three lifecycle methods and their signatures — is documented in [ierplugin-contract.md](ierplugin-contract.md); this page focuses only on the mechanical, per-plugin code port.

Terminology in this document is consistent with the platform glossary: an **Entity** is a metadata-defined type, a **Record** is a row of an Entity, **EQL** is the query language, a **plugin** is an `IErpPlugin` implementation, and a **hook** is a business-logic extension point.

## Mapping at a glance

| Legacy (`ErpPlugin` model) | New (`IErpPlugin` model) |
|---|---|
| Inherit `ErpPlugin` base class | Implement the `IErpPlugin` interface |
| `override string Name` property | Manifest metadata (plugin name on the manifest class) |
| `Initialize(IServiceProvider serviceProvider)` | `OnLoadAsync(IServiceCollection services)` |
| Plugin MVC Controllers | `MapEndpoints(IEndpointRouteBuilder endpoints)` |
| Versioned init patches run inside `Initialize` | `OnMigrateAsync(IDbTransaction transaction)` |
| "Razor Class Library" packaging | `.wvplugin` package (see [packaging-wvplugin.md](packaging-wvplugin.md)) |

## Step-by-step port

Work through the six steps below in order. Each step shows the legacy "before" and the target-state "after"; keep the manifest class small and move behavior into the three lifecycle methods.

#### Step 1: Replace inheritance with interface implementation

Stop inheriting the `ErpPlugin` base class and implement the `IErpPlugin` interface instead. Rename the entry class to the conventional `PluginManifest` — the class the host resolves from the `.wvplugin` package.

Before — `Source: /docs/developer/plugins/create-your-own.md:L37` (also `Source: /WebVella.Erp.Plugins.SDK/SdkPlugin.cs:L10`):

```csharp
public partial class SdkPlugin : ErpPlugin
```

After — implement the interface (see [ierplugin-contract.md](ierplugin-contract.md)):

```csharp
public sealed class PluginManifest : IErpPlugin
```

#### Step 2: Move `Name` to manifest metadata

The legacy `Name` was an overridden, `JsonProperty`-attributed property on the base class. On the manifest, expose the plugin name as a simple read-only property — it is the manifest metadata the host reads to identify the plugin.

Before — `Source: /docs/developer/plugins/create-your-own.md:L43-44` (also `Source: /WebVella.Erp.Plugins.SDK/SdkPlugin.cs:L12-13`):

```csharp
[JsonProperty(PropertyName = "name")]
public override string Name { get; protected set; } = "sdk";
```

After:

```csharp
public string Name => "sdk";
```

#### Step 3: Convert `Initialize(IServiceProvider)` to `OnLoadAsync(IServiceCollection)`

This is the biggest conceptual shift. The legacy `Initialize` received an **already-built** `IServiceProvider` and *resolved* services from it; `OnLoadAsync` instead receives an `IServiceCollection` and *registers* services into it **before** the application's root service provider is built. Move your service wiring into `OnLoadAsync`, and relocate the migration work to `OnMigrateAsync` (Step 5). See [ierplugin-contract.md](ierplugin-contract.md) for the full method contract.

Before — the legacy body opened a system scope and called `SetSchedulePlans()` then `ProcessPatches()` — `Source: /docs/developer/plugins/create-your-own.md:L52` (also `Source: /WebVella.Erp.Plugins.SDK/SdkPlugin.cs:L15-22`):

```csharp
public override void Initialize(IServiceProvider serviceProvider)
{
    using (var ctx = SecurityContext.OpenSystemScope())
    {
        SetSchedulePlans();
        ProcessPatches();
    }
}
```

After — register into the collection; awaited by the host at load time:

```csharp
public Task OnLoadAsync(IServiceCollection services)
{
    // register the plugin's services, options, hooks and job types here
    return Task.CompletedTask;
}
```

#### Step 4: Convert Controllers to `MapEndpoints`

A legacy plugin extended the web API with its own MVC controllers — `Source: /docs/developer/plugins/overview.md:L12`. Replace each controller action with a Minimal API endpoint mapped in `MapEndpoints`, and namespace the routes under a plugin-specific prefix.

Before — an MVC controller action:

```csharp
[Route("api/v3.0/p/sdk")]
public class AdminController : Controller
{
    [HttpGet("ping")]
    public IActionResult Ping() => Ok();
}
```

After — a mapped Minimal API endpoint:

```csharp
public void MapEndpoints(IEndpointRouteBuilder endpoints)
{
    endpoints.MapGet("/api/v1/plugins/sdk/ping", () => Results.Ok());
}
```

#### Step 5: Convert versioned init patches to `OnMigrateAsync`

The legacy plugin ran its versioned schema patches inside `Initialize` via `ProcessPatches()`, which opened its **own** connection and transaction, applied each version-gated patch in ascending order, then committed — rolling back on error. In the target model the host owns the transaction and hands it to `OnMigrateAsync`; the plugin only applies patches. See [migrations-onmigrateasync.md](migrations-onmigrateasync.md) for the full pattern.

Before — the plugin opened the connection/transaction, ran ascending version-gated patches, then committed or rolled back — `Source: /WebVella.Erp.Plugins.SDK/SdkPlugin._.cs:L31-35,L79-90,L153-160`:

```csharp
using (var connection = DbContext.Current.CreateConnection())
{
    connection.BeginTransaction();
    if (currentPluginSettings.Version < 20181215)
    {
        currentPluginSettings.Version = 20181215;
        Patch20181215(entMan, relMan, recMan);
    }
    connection.CommitTransaction();
}
```

After — apply the same ascending, idempotent patches on the host-owned `transaction`:

```csharp
public async Task OnMigrateAsync(IDbTransaction transaction)
{
    var installed = ReadInstalledVersion(transaction);
    if (installed < 20181215)
    {
        await Patch20181215Async(transaction);
        installed = 20181215;
    }
    SaveInstalledVersion(transaction, installed);
}
```

#### Step 6: Repackage as `.wvplugin`

The legacy plugin shipped as a "Razor Class Library" — `Source: /docs/developer/plugins/overview.md:L4`. The target plugin ships as a single `.wvplugin` package that bundles the manifest, assemblies, and static assets, which the host discovers and loads into its own `AssemblyLoadContext`. Follow [packaging-wvplugin.md](packaging-wvplugin.md) for the package layout and versioning.

## Checklist / gotchas

- **Schedule-plan registration moves out of `Initialize`.** If your plugin registered schedule plans inside `Initialize` (the SDK plugin called `SetSchedulePlans()` — `Source: /WebVella.Erp.Plugins.SDK/SdkPlugin.cs:L19`), relocate that registration to load-time wiring in `OnLoadAsync`; it no longer belongs alongside the migration logic.
- **Keep migrations idempotent.** Retain the ascending `if (installed < N)` version gates so re-running `OnMigrateAsync` against an up-to-date database applies nothing. See [migrations-onmigrateasync.md](migrations-onmigrateasync.md).
- **Do not open your own connection.** `OnMigrateAsync` writes on the host-owned `IDbTransaction`; a throw rolls back the shared transaction. Never call `DbContext.Current.CreateConnection()` from the manifest.
- **Namespace your endpoint routes.** Prefix every mapped route with `/api/v1/plugins/{name}/...` so that no two plugins claim the same HTTP method and path.
- **No cookie authorization.** The headless API authenticates with OIDC/JWT bearer tokens; drop any cookie-based assumptions carried over from the RazorPages host.

## Program-wide migration

This page covers porting **one** plugin's code. Migrating the five bundled plugins (Crm, Mail, MicrosoftCDM, Next, Project) and any third-party plugins at the program level — sequencing, compatibility, and rollout — is covered in [../migration/plugin-migration.md](../migration/plugin-migration.md), which references this page for the per-plugin code port.
