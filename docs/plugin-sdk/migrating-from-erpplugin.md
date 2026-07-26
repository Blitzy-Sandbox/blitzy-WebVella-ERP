<!--{"sort_order":2, "name": "migrating-from-erpplugin", "label": "Migrating from ErpPlugin"}-->
# Migrating from ErpPlugin to IErpPlugin

> **Planned target design — Not available in this checkout.** The `IErpPlugin`
> contract, `PluginManifest`, the `.wvplugin` package, and the collectible-
> `AssemblyLoadContext` plugin host this guide ports *to* **do not exist in this
> repository yet**. This page is a **proposed** migration guide: the "before"
> snippets are the **verified legacy** code, and every "after" snippet is **proposed
> design** that is **Not available / to be confirmed** until the SDK contract and
> host are implemented. Do not treat the "after" code as runnable.
>
> Source (verified legacy): /WebVella.Erp/ErpPlugin.cs:L12 (abstract `ErpPlugin`), L57 (`Initialize(IServiceProvider)`); /WebVella.Erp.Plugins.SDK/SdkPlugin.cs:L10 (`SdkPlugin : ErpPlugin`), L15 (`override void Initialize`).

This guide is a step-by-step port of a single existing plugin from the legacy
`ErpPlugin` base-class model to the proposed `IErpPlugin` contract. The model is
proposed to change because the headless platform would load each plugin into a
collectible `AssemblyLoadContext` and drive it through an **asynchronous** load
lifecycle, instead of newing up a base class inside a monolithic Razor host. The
canonical (proposed) contract — the three lifecycle methods and their signatures —
is documented in [ierplugin-contract.md](ierplugin-contract.md); this page focuses
only on the mechanical, per-plugin code port.

Terminology in this document is consistent with the platform glossary: an
**Entity** is a metadata-defined type, a **Record** is a row of an Entity, **EQL**
is the query language, a **plugin** is an `IErpPlugin` implementation, and a
**hook** is a business-logic extension point.

## Mapping at a glance

| Legacy (`ErpPlugin` model — verified) | New (`IErpPlugin` model — proposed) |
|---|---|
| Inherit `ErpPlugin` base class | Implement the `IErpPlugin` interface |
| `override string Name` property | Manifest metadata (plugin name on the manifest class) |
| `Initialize(IServiceProvider serviceProvider)` | `OnLoadAsync(IServiceCollection services)` |
| Plugin MVC Controllers **with `[Authorize]`** | `MapEndpoints(...)` **with `.RequireAuthorization()`** |
| Versioned init patches run inside `Initialize` | `OnMigrateAsync(IDbTransaction transaction)` |
| "Razor Class Library" packaging | `.wvplugin` package (see [packaging-wvplugin.md](packaging-wvplugin.md)) |

## Step-by-step port

Work through the six steps below in order. Each step shows the verified legacy
"before" and the **proposed** target-state "after"; keep the manifest class small
and move behavior into the three lifecycle methods.

### Step 1: Replace inheritance with interface implementation

Stop inheriting the `ErpPlugin` base class and implement the `IErpPlugin` interface
instead. Rename the entry class to the conventional `PluginManifest` — the class the
host would resolve from the `.wvplugin` package.

Before (verified) — Source: /WebVella.Erp.Plugins.SDK/SdkPlugin.cs:L10:

```csharp
public partial class SdkPlugin : ErpPlugin
```

After (proposed — `IErpPlugin` Not available / to be confirmed):

```csharp
public sealed class PluginManifest : IErpPlugin
```

### Step 2: Move `Name` to manifest metadata

The legacy `Name` is an overridden, `JsonProperty`-attributed property on the base
class. On the manifest, expose the plugin name as a simple read-only property.

Before (verified) — Source: /WebVella.Erp.Plugins.SDK/SdkPlugin.cs:L12-L13:

```csharp
[JsonProperty(PropertyName = "name")]
public override string Name { get; protected set; } = "sdk";
```

After (proposed):

```csharp
public string Name => "sdk";
```

### Step 3: Convert `Initialize(IServiceProvider)` to `OnLoadAsync(IServiceCollection)`

This is the biggest conceptual shift. The legacy `Initialize` receives an
**already-built** `IServiceProvider` and *resolves* services from it; `OnLoadAsync`
would instead receive an `IServiceCollection` and *register* services into it
**before** the application's root service provider is built. Move service wiring
into `OnLoadAsync`, and relocate migration work to `OnMigrateAsync` (Step 5). See
[ierplugin-contract.md](ierplugin-contract.md) for the proposed method contract.

Before (verified) — the legacy body opens a system scope and calls
`SetSchedulePlans()` then `ProcessPatches()` — Source:
/WebVella.Erp.Plugins.SDK/SdkPlugin.cs:L15-L22:

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

After (proposed — register into the collection; awaited by the host at load time):

```csharp
public Task OnLoadAsync(IServiceCollection services)
{
    // register the plugin's services, options, hooks and job types here
    return Task.CompletedTask;
}
```

### Step 4: Convert Controllers to `MapEndpoints` — preserving authorization

A legacy plugin extends the web API with its own MVC controllers. The SDK plugin's
`AdminController` enforces authorization **on the server**: it carries a class-level
`[Authorize(...)]` so every action requires an authenticated principal, and
sensitive actions add `[Authorize(Roles = "administrator")]`. When you port each
controller action to a Minimal API endpoint, you **must reproduce that
authorization** with `.RequireAuthorization(...)`, and namespace the routes under a
plugin-specific prefix.

> **Do not drop server-side authorization (Rule D / H-08).** A mapped endpoint
> without `.RequireAuthorization(...)` is **public**. Reproduce the legacy
> `[Authorize]` / `[Authorize(Roles = "administrator")]` requirement on **every**
> ported protected endpoint. **UI visibility is never a substitute for endpoint
> authorization** — hiding a control does not protect its route.

Before (verified) — the controller authorizes at the class and action level —
Source: /WebVella.Erp.Plugins.SDK/Controllers/AdminController.cs:L16 (class-level),
L52-L53 (role-restricted action); role name `administrator` per
/WebVella.Erp/Api/SecurityContext.cs:L26:

```csharp
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
public class AdminController : Controller
{
    [Authorize(Roles = "administrator")]
    [AcceptVerbs("POST", Route = "api/v3.0/p/sdk/sitemap/area")]
    public IActionResult CreateSitemapArea([FromBody] SitemapArea area, [FromQuery] Guid? appId = null)
    { /* ... */ }
}
```

After (proposed — the role requirement is carried over with `RequireAuthorization`):

```csharp
public void MapEndpoints(IEndpointRouteBuilder endpoints)
{
    // Reproduce [Authorize(Roles = "administrator")] on the mapped endpoint:
    endpoints.MapPost("/api/v1/plugins/sdk/sitemap/area", CreateSitemapArea)
             .RequireAuthorization(policy => policy.RequireRole("administrator"));
}
```

### Step 5: Convert versioned init patches to `OnMigrateAsync`

The legacy plugin runs its versioned schema patches inside `Initialize` via
`ProcessPatches()`, which opens its **own** connection and transaction, applies each
version-gated patch in ascending order, then commits — rolling back on error.

> **Transaction ownership changes are proposed, not decided (H-07).** The "after"
> code below writes on a host-supplied `IDbTransaction`, but whether the host owns
> one transaction shared across all plugins (cross-plugin atomicity) or each plugin
> keeps its own transaction is **Not available / to be confirmed** until the host
> exists. See [migrations-onmigrateasync.md](migrations-onmigrateasync.md#transaction-scoping-current-vs-target).

Before (verified) — the plugin opens the connection/transaction, runs ascending
version-gated patches, then commits or rolls back — Source:
/WebVella.Erp.Plugins.SDK/SdkPlugin._.cs:L31 (`CreateConnection`), L35
(`BeginTransaction`), L79 (version gate), L153 (`CommitTransaction`), L156-L158
(`catch { RollbackTransaction(); }`):

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

After (proposed — apply the same ascending, idempotent patches on a host-supplied
`transaction`):

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

### Step 6: Repackage as `.wvplugin`

The legacy plugin ships as a "Razor Class Library" NuGet package. The proposed
target plugin would ship as a single `.wvplugin` package bundling the manifest,
assemblies, and static assets, which the host would discover and load into its own
`AssemblyLoadContext`. Follow [packaging-wvplugin.md](packaging-wvplugin.md) for the
proposed package layout and versioning.

Source (verified legacy packaging): /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L6 (`<PackageId>`), L12 (`<PackageReadmeFile>`). The `.wvplugin` format is Not available / to be confirmed.

## Checklist / gotchas

- **Reproduce authorization on every protected endpoint.** Each ported endpoint that
  replaces an `[Authorize]` / `[Authorize(Roles = "administrator")]` action must call
  `.RequireAuthorization(...)` with the equivalent policy/roles. UI visibility is not
  authorization.
- **Schedule-plan registration moves out of `Initialize`.** The SDK plugin calls
  `SetSchedulePlans()` inside `Initialize` — Source:
  /WebVella.Erp.Plugins.SDK/SdkPlugin.cs:L19 — relocate that registration to
  load-time wiring in `OnLoadAsync`.
- **Keep migrations idempotent.** Retain the ascending `if (installed < N)` version
  gates so re-running `OnMigrateAsync` against an up-to-date database applies
  nothing. See [migrations-onmigrateasync.md](migrations-onmigrateasync.md).
- **Transaction ownership is undecided.** Do not assume the host owns the migration
  transaction until the host code exists; see the H-07 note in Step 5.
- **Namespace your endpoint routes.** Prefix every mapped route with
  `/api/v1/plugins/{name}/...` so no two plugins claim the same method and path.
- **No cookie authorization.** The headless API is proposed to authenticate with
  OIDC/JWT bearer tokens; drop cookie-based assumptions carried over from the
  RazorPages host.

## Program-wide migration

This page covers porting **one** plugin's code. Migrating the five bundled plugins
(Crm, Mail, MicrosoftCDM, Next, Project) and any third-party plugins at the program
level — sequencing, compatibility, and rollout — will be covered in the
[plugin program-migration guide](../migration/plugin-migration.md), which will reference this page for the per-plugin code port.
