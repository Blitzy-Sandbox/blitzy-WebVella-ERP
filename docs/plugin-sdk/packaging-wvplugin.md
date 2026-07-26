<!--{"sort_order":3, "name": "packaging-wvplugin", "label": ".wvplugin Packaging"}-->
# Packaging a Plugin (.wvplugin)

> **Planned target design — Not available in this checkout.** The `.wvplugin`
> package format, the `PluginManifest` entry class, the `IErpPlugin` contract, and
> the collectible-`AssemblyLoadContext` plugin host **do not exist in this
> repository yet**. Everything about the `.wvplugin` layout, manifest, and hot-swap
> below is **proposed design** and **Not available / to be confirmed** until the
> host and package format are implemented. What is **verified today** is how the
> legacy SDK plugin is packaged as an ordinary NuGet Razor Class Library (the
> `.csproj` facts cited on this page). The `.wvplugin` layout must be finalized
> against the host's loader once it exists.
>
> Source (verified legacy packaging): /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L5-L6, L12, L24, L35, L39-L40. Missing target artifacts: `.wvplugin` format, `PluginManifest`, `IErpPlugin`, plugin host.

In the proposed headless model, a **plugin** would be distributed as a single
`.wvplugin` package — the unit the plugin host would discover, load, version, and
(when a newer build is dropped in) reload. Each package would be loaded into its
own **collectible** `AssemblyLoadContext`, which would let the host load, unload,
and replace a plugin without restarting the process; see
[assemblyloadcontext-hosting.md](assemblyloadcontext-hosting.md) for the proposed
hosting model.

> **A `.wvplugin` package is not a security boundary (Rule D / H-09).** Loading a
> package into a collectible `AssemblyLoadContext` provides *assembly/dependency
> isolation and unloadability* — **not** a sandbox. Plugin code would run
> **in-process with full host privileges**. Treat every package as trusted code and
> apply the supply-chain controls in [Supply-chain and trust controls](#supply-chain-and-trust-controls-required)
> before loading one.

Terminology in this document is consistent with the platform glossary: an
**Entity** is a metadata-defined type, a **Record** is a row of an Entity, **EQL**
is the query language, a **plugin** is an `IErpPlugin` implementation, and a
**hook** is a business-logic extension point.

A `.wvplugin` package would bundle three things:

1. the plugin's compiled **assemblies** (DLLs);
2. a **manifest** — a `PluginManifest`-style class that implements `IErpPlugin` and
   is the entry point the host resolves (see [ierplugin-contract.md](ierplugin-contract.md)); and
3. any **static assets** the plugin ships (embedded JavaScript, code snippets,
   images).

## Package layout (proposed)

A representative `.wvplugin` package would have the top-level layout below. This
layout is **proposed** and Not available / to be confirmed; the verified items are
the current SDK project's embedded-resource and package settings, cited per line.

```text
assemblies/                             # folder
static/                                 # folder
WebVella.Erp.Plugins.PluginName.dll     # file
README.md                               # file
```

- **`WebVella.Erp.Plugins.PluginName.dll`** — the compiled plugin assembly, which
  would carry the `PluginManifest` (`IErpPlugin`) implementation the host resolves.
  The current SDK plugin embeds `Components\WvSdkPageSitemap\form.js` as an embedded
  resource, Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L35,
  and embeds every `*.cs` and `*.html` file under `Snippets\` directly into the
  assembly, Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L39-L40.
- **`assemblies`** — the plugin's dependency DLLs, proposed to be loaded alongside
  the plugin assembly inside its `AssemblyLoadContext` (see [assemblyloadcontext-hosting.md](assemblyloadcontext-hosting.md)).
- **`static`** — any **loose** companion assets not embedded into the assembly.
- **`README.md`** — the human-readable readme. The current SDK project declares its
  readme with `<PackageReadmeFile>README.md</PackageReadmeFile>` Source:
  /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L12 and packs it at the
  root via `PackagePath="\"`, Source:
  /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L24.

## The plugin manifest (proposed)

The **manifest** would be the class that implements the `IErpPlugin` contract —
conventionally named `PluginManifest` — and the single entry point the host resolves
when it loads the package. It would expose the plugin's `Name` and the three
lifecycle methods (`OnLoadAsync`, `OnMigrateAsync`, `MapEndpoints`); the proposed
contract is documented in [ierplugin-contract.md](ierplugin-contract.md). The
snippet below is design pseudocode (`IErpPlugin` Not available / to be confirmed):

```csharp
public sealed class PluginManifest : IErpPlugin
{
    public string Name => "sdk";
    // OnLoadAsync, OnMigrateAsync, MapEndpoints — see ierplugin-contract.md
}
```

## Versioning

Each `.wvplugin` package would carry an explicit version and package identity. The
current SDK plugin declares, on its project:

- **Version** — `<Version>1.7.4</Version>`. Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L5
- **Package id** — `<PackageId>WebVella.Erp.Plugins.SDK</PackageId>`. Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L6
- **License** — `<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>`. Source: /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L10

The package version would anchor the plugin's **migration** versioning:
`OnMigrateAsync` is proposed to apply transactional, versioned schema/data patches
when the plugin loads, with the package version bounding which patches a build
ships. See [migrations-onmigrateasync.md](migrations-onmigrateasync.md).

### Target runtime of packaged assemblies

> **Not available / to be confirmed.** The authoritative target framework is
> unresolved. The SDK plugin project currently targets `net10.0`. Source:
> /WebVella.Erp.Plugins.SDK/WebVella.Erp.Plugins.SDK.csproj:L4. The refactor
> specification, however, states ".NET 9". This page does not assert either value;
> the resolved target framework must be confirmed and recorded here — and the
> packaged assemblies rebuilt against it — once the decision is made. **What is
> needed:** the confirmed target-framework moniker for all in-scope projects.

## Supply-chain and trust controls (required)

Because a loaded plugin runs in-process with full host privileges and the
`AssemblyLoadContext` is **not** a sandbox, the packaging and loading pipeline must
enforce supply-chain controls. The following are **requirements** the host and
packaging tooling must satisfy (their exact mechanisms are Not available / to be
confirmed until the host exists):

- **Integrity & provenance.** Every `.wvplugin` must be verifiable — a signature
  and/or checksum bound to a **trusted publisher** — and the host must reject a
  package that fails verification.
- **Safe extraction & path handling.** Unpacking must validate every entry path and
  reject traversal (`..`), absolute paths, and symlinks (zip-slip), extracting only
  into the package's own directory.
- **Locked plugin directory.** The directory the host scans must be
  ACL-restricted/read-only to the runtime account so an attacker cannot drop or
  swap a package; writes must go through a controlled deployment step.
- **Dependency & version policy.** Bundled dependencies must be constrained by an
  allowlist/version policy and scanned for known vulnerabilities before deployment.
- **Least privilege.** The host process (and any per-plugin execution) must run with
  the minimum privileges required; document what a plugin can and cannot reach.
- **Quarantine & rollback.** A package that fails verification, load, or migration
  must be quarantined and the previous known-good version restored; the
  operator-facing procedure will be documented in the [plugin rollback plan](../migration/rollback-plan.md).

## Where packages are deployed

At startup the proposed plugin host would scan a **configured plugin directory** and
load every `.wvplugin` package it finds there, each into its own collectible
`AssemblyLoadContext` (see [assemblyloadcontext-hosting.md](assemblyloadcontext-hosting.md)).
The configuration key for that directory — its environment-variable name and default
— will be documented in the deployment
[configuration reference](../deployment/configuration-reference.md); it is not
reproduced here, and no directory path or secret value is hard-coded in this page.
