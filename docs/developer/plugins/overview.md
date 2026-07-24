<!--{"sort_order":1, "name": "overview", "label": "Overview"}-->
# What is a WebVella ERP Plugin

A plugin is in general a Razor Class Library that has some specific in order to fully utilized the platform's capabilities. In the current codebase a plugin derives from the abstract `ErpPlugin` base class and is initialized through `Initialize(IServiceProvider)`.

Source: /WebVella.Erp/ErpPlugin.cs:L12 declares `public abstract class ErpPlugin`; /WebVella.Erp/ErpPlugin.cs:L57 declares `public virtual void Initialize(IServiceProvider ServiceProvider)`.

> **Planned (headless refactor — not yet implemented).** In the target headless platform a plugin is planned to implement an `IErpPlugin` contract from a `WebVella.Erp.Plugins.SDK` package and be packaged and loaded by the headless host. That interface and host do not exist in the current checkout. See the planned [plugin SDK contract reference](../../plugin-sdk/ierplugin-contract.md).

The purpose of the plugin is to provide:

- tag helpers
- page components
- pages or page routing overrides
- business logic with the help of Hooks
- extend the web api with its own controllers
- code based datasources
- register background jobs to be run by the system
- register to your page

> **Planned (headless refactor — not yet implemented).** Under the target `IErpPlugin` contract, plugins are planned to expose HTTP endpoints via `MapEndpoints(IEndpointRouteBuilder)` rather than registering MVC controllers. See the planned [plugin SDK contract reference](../../plugin-sdk/ierplugin-contract.md).
