<!--{"sort_order":2, "name": "create-your-own", "label": "Create your own"}-->
# Create a Plugin for the WebVella Erp

> **Planned target — not yet implemented (headless refactor).** The `IErpPlugin` contract, the `PluginManifest.cs` convention, the async lifecycle methods (`OnLoadAsync(IServiceCollection)`, `MapEndpoints(IEndpointRouteBuilder)`, `OnMigrateAsync(IDbTransaction)`), and the `.wvplugin` plugin host described on this page **do not exist in this repository yet** — they are delivered by the plugin-SDK implementation workstream, and the contract shape itself is **Not available / to be confirmed**. **Today**, a plugin is authored against the legacy base class `ErpPlugin` and overrides `Initialize(IServiceProvider)` — the bundled SDK sample still does exactly this. Source: /WebVella.Erp.Plugins.SDK/SdkPlugin.cs:L10,L15 (`public partial class SdkPlugin : ErpPlugin` … `public override void Initialize(IServiceProvider serviceProvider)`). See [Plugins overview](overview.md) for the current model and [Migrating from ErpPlugin](../../plugin-sdk/migrating-from-erpplugin.md) for the planned port. The folder structure and `IErpPlugin` signatures below describe the **target** authoring model.

To create a plugin you need to add to the solution a project that implements the **`IErpPlugin`** contract (commonly via a `PluginManifest.cs`) and follows a specific structure and a few requirements.

## Plugin name

The naming convention that we follow when creating a plugin is: WebVella.Erp.Plugins.PluginName. You can also add a prefix before the plugin name if needed.

## Folder Structure

The plugin usually has a main `.cs` file and a number of folders that hold the code for the various plugin components. Here is the folder structure we consider best:

<i class="fa fa-fw fa-folder go-orange"></i> Components <br/>
<i class="fa fa-fw fa-folder go-orange"></i> Controllers <br/>
<i class="fa fa-fw fa-folder go-orange"></i> DataSource <br/>
<i class="fa fa-fw fa-folder go-orange"></i> Hooks <br/>
<i class="fa fa-fw fa-folder go-orange"></i> Jobs <br/>
<i class="fa fa-fw fa-folder go-orange"></i> Model <br/>
<i class="fa fa-fw fa-folder go-orange"></i> Pages <br/>
<i class="fa fa-fw fa-folder go-orange"></i> Services <br/>
<i class="fa fa-fw fa-folder go-orange"></i> Utils <br/>
<i class="fa fa-fw fa-file-code go-blue"></i> PluginNamePlugin.cs

Note: the `Controllers` folder is now wired through `MapEndpoints(IEndpointRouteBuilder)` rather than MVC controllers.

## PluginNamePlugin.cs

You can create this file as an ordinary class, but there are several requirements in order to turn it into a plugin:

#### Requirement 1: The Namespace should correspond to the plugin library name
```csharp
namespace WebVella.Erp.Plugins.SDK
```

#### Requirement 2: Should implement `IErpPlugin`

```csharp
public class SdkPlugin : IErpPlugin
```

#### Requirement 3: Should expose the plugin identity via its `Name`

The plugin exposes its identity (its unique name) through the `IErpPlugin` implementation:

```csharp
public string Name => "sdk";
```

#### Requirement 4: Should implement the `IErpPlugin` async lifecycle

Implement the three lifecycle methods invoked by the headless plugin host — `OnLoadAsync(IServiceCollection)`, `MapEndpoints(IEndpointRouteBuilder)`, and `OnMigrateAsync(IDbTransaction)`:

- `OnLoadAsync(IServiceCollection services)` — register the plugin's services / DI at load time (replaces the legacy startup initialization).
- `MapEndpoints(IEndpointRouteBuilder endpoints)` — expose the plugin's HTTP endpoints.
- `OnMigrateAsync(IDbTransaction transaction)` — apply transactional schema/data patches on the host-owned transaction.

```csharp
public Task OnLoadAsync(IServiceCollection services);
public void MapEndpoints(IEndpointRouteBuilder endpoints);
public Task OnMigrateAsync(IDbTransaction transaction);
```

For the complete contract and lifecycle, see the canonical SDK reference: the [IErpPlugin contract](../../plugin-sdk/ierplugin-contract.md). To port a legacy plugin to `IErpPlugin`, follow the [migration guide](../../plugin-sdk/migrating-from-erpplugin.md).

Source: /docs/developer/plugins/create-your-own.md (this page's pre-refactor revision documented the legacy base-class plugin model).

## Components

Here are the page components provided by the plugin.

## Controllers

Here are the HTTP endpoints provided by the plugin. Endpoints are registered via `MapEndpoints(IEndpointRouteBuilder)` rather than MVC controllers.

## DataSource

Here are the code datasources provided by the plugins

## Hooks

Here are the API hooks provided by the plugins

## Jobs

Here are the background jobs of the plugin

## Model

Plugin's model classes

## Pages

Plugin pages. All plugins can override the Site page routes securely. If you need to override another plugin page route the result is not always constant so we do not advise it.

## Services

Plugin's service methods

## Utils

Plugin's utility methods
