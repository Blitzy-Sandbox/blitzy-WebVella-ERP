<!--{"sort_order":3, "name": "packaging-wvplugin", "label": ".wvplugin Packaging"}-->
# Packaging a Plugin (.wvplugin)

A **plugin** is distributed as a single `.wvplugin` package. The package is the unit the plugin host discovers, loads, versions, and — when a newer build is dropped in — hot-swaps, so bundling everything a plugin needs into one artifact is what makes isolated, side-by-side loading possible. Each package is loaded into its own **collectible** `AssemblyLoadContext`, which lets the host load, unload, and replace a plugin without restarting the process; see [assemblyloadcontext-hosting.md](assemblyloadcontext-hosting.md) for the hosting model.

Terminology in this document is consistent with the platform glossary: an **Entity** is a metadata-defined type, a **Record** is a row of an Entity, **EQL** is the query language, a **plugin** is an `IErpPlugin` implementation, and a **hook** is a business-logic extension point.

A `.wvplugin` package bundles three things:

1. the plugin's compiled **assemblies** (DLLs);
2. a **manifest** — a `PluginManifest.cs`-style class that implements `IErpPlugin` and is the entry point the host resolves (see [ierplugin-contract.md](ierplugin-contract.md)); and
3. any **static assets** the plugin ships (embedded JavaScript, code snippets, images).

## Package layout

A representative `.wvplugin` package has the top-level layout below. Following the repository's documentation convention, folders are shown in orange and files in blue. Source: /docs/developer/plugins/create-your-own.md:L14-23, Source: /docs/developer/components/create-your-own.md:L16-22

<i class="fa fa-fw fa-folder go-orange"></i> assemblies <br/>
<i class="fa fa-fw fa-folder go-orange"></i> static <br/>
<i class="fa fa-fw fa-file-code go-blue"></i> WebVella.Erp.Plugins.PluginName.dll <br/>
<i class="fa fa-fw fa-file-code go-blue"></i> README.md

- **`WebVella.Erp.Plugins.PluginName.dll`** — the compiled plugin assembly. It carries the `PluginManifest` (`IErpPlugin`) implementation the host resolves, and it also carries the plugin's **embedded** static assets. The SDK plugin, for example, embeds `Components\WvSdkPageSitemap\form.js` as an embedded resource, Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L35 and embeds every `*.cs` and `*.html` file under `Snippets\` directly into the assembly. Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L39-40 The library name follows the `WebVella.Erp.Plugins.PluginName` naming convention. Source: /docs/developer/plugins/create-your-own.md:L8
- **`assemblies`** — the plugin's dependency DLLs, loaded alongside the plugin assembly inside its `AssemblyLoadContext` (see [assemblyloadcontext-hosting.md](assemblyloadcontext-hosting.md)).
- **`static`** — any **loose** companion assets (for example, images) that are not embedded into the assembly. Assets compiled in as embedded resources (above) do **not** appear here.
- **`README.md`** — the human-readable readme, packaged at the package root. The SDK project declares its readme with `<PackageReadmeFile>README.md</PackageReadmeFile>` Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L12 and packs it at the root via `PackagePath="\"`. Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L24

## The plugin manifest

The **manifest** is the class that implements the `IErpPlugin` contract — conventionally named `PluginManifest.cs` — and is the single entry point the host resolves when it loads the package. The manifest exposes the plugin's `Name` and the three lifecycle methods (`OnLoadAsync`, `OnMigrateAsync`, `MapEndpoints`); the full contract, signatures, and lifecycle order are documented in [ierplugin-contract.md](ierplugin-contract.md).

```csharp
public sealed class PluginManifest : IErpPlugin
{
    public string Name => "sdk";
    // OnLoadAsync, OnMigrateAsync, MapEndpoints — see ierplugin-contract.md
}
```

## Versioning

Each `.wvplugin` package carries an explicit version and package identity, declared on the plugin project. The SDK plugin, for instance, declares:

- **Version** — `<Version>1.7.4</Version>`. Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L5
- **Package id** — `<PackageId>WebVella.Erp.Plugins.SDK</PackageId>`. Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L6
- **License** — `<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>`. Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L10

The package version is the anchor for the plugin's **migration** versioning: `OnMigrateAsync` applies transactional, versioned schema and data patches when the plugin loads, and the package version bounds which patches a given build ships. The versioned-patch pattern and its rollback semantics are documented in [migrations-onmigrateasync.md](migrations-onmigrateasync.md).

#### Target runtime of packaged assemblies

> **Not available / to be confirmed.** The authoritative target framework is unresolved. The SDK plugin project currently targets `net10.0`. Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L4 The refactor specification, however, states ".NET 9". This page does not assert either value; the resolved target framework must be confirmed and recorded here — and the packaged assemblies rebuilt against it — once the decision is made. **What is needed:** the confirmed target-framework moniker for all in-scope projects.

## Where packages are deployed

At startup the plugin host scans a **configured plugin directory** and loads every `.wvplugin` package it finds there, each into its own collectible `AssemblyLoadContext` (see [assemblyloadcontext-hosting.md](assemblyloadcontext-hosting.md)). The exact configuration key for that directory — its environment-variable name and default — is documented in the deployment [configuration reference](../deployment/configuration-reference.md); it is not reproduced here, and no directory path or secret value is hard-coded in this page.
