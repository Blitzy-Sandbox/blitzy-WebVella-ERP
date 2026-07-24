<!--{"sort_order":1, "name": "overview", "label": "Overview"}-->
# Welcome to WebVella ERP Project

Our goal is to create a truly opensource and free platform, that allows the quick and painless creation business web applications on the technology we love. 

## Supported Platforms

WebVella ERP is a headless, container-native platform composed of a REST API host (`WebVella.Erp.Api`) that serves the versioned `/api/v1/` surface, a React single-page application client (`WebVella.Erp.Client`), and a background worker (`WebVella.Erp.Worker`) — all built on the **unchanged** core engine (`WebVella.Erp`) and PostgreSQL.

Source: /docs/developer/introduction/overview.md (corrects the retired hosting-model claim that the platform shipped as a single ASP.NET Core application)

| technology | supported version |
|------------|-------------------|
| ASP.NET Core | to be confirmed — .NET 9 vs net10.0 |
| PostgreSQL | v.16 |
| Radix UI + Tailwind CSS | to be confirmed |

Source: /WebVella.Erp/WebVella.Erp.csproj:L4 shows `<TargetFramework>net10.0</TargetFramework>`, so the authoritative ASP.NET Core runtime (.NET 9 vs net10.0) is a decision point pending confirmation. The React client styling (Radix UI + Tailwind CSS) version pins are deferred to the SPA build workstream (AAP §0.4.4).

## Used technologies

To build the project we use the following technologies: ASP.NET Core (API host), React SPA (Radix UI + Tailwind CSS), PostgreSQL.

The legacy RazorPages, jQuery, StencilJs, and Bootstrap UI stack is being retired as part of the headless refactor; see the [Migration Overview](../../migration/overview.md). The underlying core engine — its Entity, Record, EQL, hook, and plugin model — is unchanged by this hosting-model change.

Source: /docs/migration/overview.md (headless re-hosting: the legacy RazorPages/Blazor UI stack is retired while the core Entity, Record, EQL, and hook model remains unchanged)

## License

The project is licensed under [the Apache License, Version 2.0](http://www.apache.org/licenses/LICENSE-2.0)
