<!--{"sort_order":1, "name": "overview", "label": "Overview"}-->
# What is a WebVella ERP Plugin

A plugin implements the **`IErpPlugin`** contract (provided by the `WebVella.Erp.Plugins.SDK` package) and is packaged and loaded by the headless host. It still extends the platform with the same capability set.

The purpose of the plugin is to provide:

- tag helpers
- page components
- pages or page routing overrides
- business logic with the help of Hooks
- expose HTTP endpoints via `MapEndpoints(IEndpointRouteBuilder)`
- code based datasources
- register background jobs to be run by the system
- register to your page

See the canonical SDK reference: the [IErpPlugin contract](../../plugin-sdk/ierplugin-contract.md).
