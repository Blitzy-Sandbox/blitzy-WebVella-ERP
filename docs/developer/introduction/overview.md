<!--{"sort_order":1, "name": "overview", "label": "Overview"}-->
# Welcome to WebVella ERP Project

Our goal is to create a truly opensource and free platform, that allows the quick and painless creation business web applications on the technology we love. 

## Supported Platforms

The project is currently available only as an ASP.NET Core application (the RazorPages web host together with the Blazor WebAssembly client).

> **Planned (headless refactor — not yet implemented).** A headless, container-native split into a REST API host (`WebVella.Erp.Api`) serving a versioned `/api/v1/` surface, a React single-page application client (`WebVella.Erp.Client`), and a background worker (`WebVella.Erp.Worker`) — all over the **unchanged** core engine (`WebVella.Erp`) and PostgreSQL — is planned. Those target projects do not exist in the current checkout. See the [Migration Overview](../../migration/overview.md).

| technology | supported version |
|------------|-------------------|
| ASP.NET Core | v.9 per spec / `net10.0` in code — Not available / to be confirmed |
| PostgreSQL | v.16 |
| Bootstrap CSS | v.4 (current UI stack) |

Source: /WebVella.Erp/WebVella.Erp.csproj:L4 shows `<TargetFramework>net10.0</TargetFramework>`, while the specification references ".NET 9" / "ASP.NET Core 9" and the root `README.md` frames this as an open ".NET 9 vs net10.0" decision; the authoritative runtime is therefore a decision point that is **Not available / to be confirmed**.

> **Planned (headless refactor — not yet implemented).** The target React SPA client is planned to use Radix UI + Tailwind CSS; exact version pins are deferred to the SPA build workstream. See AAP §0.4.

## Used technologies

To build the project we currently use the following technologies: ASP.NET Core, RazorPages, HTML5, PostgreSQL, Bootstrap, JQuery, StencilJs.

> **Planned (headless refactor — not yet implemented).** The legacy RazorPages, jQuery, StencilJs, and Bootstrap UI stack is planned to be retired in favour of a React SPA (Radix UI + Tailwind CSS) talking to the `/api/v1/` REST host. The underlying core engine — its Entity, Record, EQL, hook, and plugin model — is unchanged by this hosting-model change. See the [Migration Overview](../../migration/overview.md).

## License

The project is licensed under [the Apache License, Version 2.0](http://www.apache.org/licenses/LICENSE-2.0)
