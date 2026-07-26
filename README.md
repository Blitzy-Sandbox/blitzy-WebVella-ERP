[NEW PROJECT ALERT] Check out our new project for [Data collaboration - Tefter.bg](https://github.com/WebVella/WebVella.Tefter).

[NEW PROJECT ALERT] Check out our new project for [Document template generation](https://github.com/WebVella/WebVella.DocumentTemplates).

---

[![Project Homepage](https://img.shields.io/badge/Homepage-blue?style=for-the-badge)](https://webvella.com)
[![Dotnet](https://img.shields.io/badge/platform-.NET-blue?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![GitHub Repo stars](https://img.shields.io/github/stars/WebVella/WebVella-ERP?style=for-the-badge)](https://github.com/WebVella/WebVella-ERP/stargazers)
[![Nuget version](https://img.shields.io/nuget/v/WebVella.ERP?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![Nuget download](https://img.shields.io/nuget/dt/WebVella.ERP?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-green?style=for-the-badge)](https://github.com/WebVella/WebVella-ERP/blob/master/LICENSE.txt)

---

# WebVella ERP

**WebVella ERP** is a free and open-source web platform that targets extreme customization and pluggability in service of any business data-management needs. It is built upon our experience, best practices, and the newest available technologies.

WebVella ERP is evolving into a **headless, container-native platform**. The product is being split into a REST/OpenAPI API host (`WebVella.Erp.Api`, serving `/api/v1/`), a React single-page-application client (`WebVella.Erp.Client`), and a background worker (`WebVella.Erp.Worker`) — all built on top of the unchanged core engine (`WebVella.Erp`) and PostgreSQL, and extended through a formal `IErpPlugin` plugin contract.

> **Status — the headless platform is planned, not yet runnable from this checkout.** The
> `WebVella.Erp.Api`, `WebVella.Erp.Client`, and `WebVella.Erp.Worker` projects, the `/api/v1/`
> surface, the generated OpenAPI document, and the Docker Compose / Kubernetes deployment assets do
> **not exist in this repository yet** — they are delivered by separate implementation workstreams.
> This checkout is still the legacy ASP.NET Core solution (the RazorPages/Blazor hosts under
> `WebVella.Erp.Web`, `WebVella.Erp.Site*`, and `WebVella.Erp.WebAssembly`). Everything below
> describes the **target** design; any command that references the new projects is **not runnable**
> until those projects land. Source: `WebVella.ERP3.sln` (no `WebVella.Erp.Api`, `WebVella.Erp.Client`,
> or `WebVella.Erp.Worker` project is present).

The database of choice is **PostgreSQL**, and **Linux containers are the intended deployment target** — Docker Compose for local development and Kubernetes for production. The authoritative .NET target runtime is an open decision point — **Not available / to be confirmed** (.NET 9 vs .NET 10 / `net10.0`): the project manifests currently declare `net10.0` (see `WebVella.Erp/WebVella.Erp.csproj:L4`), which has not yet been reconciled with the earlier ".NET 9" references and will be confirmed before release.

If you want this project to continue or just like it, we will greatly appreciate your support of the project by:

* giving it a "star"
* contributing to the source
* Become a Sponsor: Click on the Sponsor button and Thank you in advance

## Getting started

The **target** deployment model runs WebVella ERP as a set of Linux containers (Docker Compose for local development, Kubernetes for production). That container workflow is **not yet runnable from this checkout** — the API, client, and worker projects and the Compose assets are absent (see the status note above). [INSTRUCTIONS.md](INSTRUCTIONS.md) keeps the **current** legacy build steps separate from the **planned** container commands, and labels each not-yet-runnable command with the missing project or asset.

### Documentation

The documentation set for the headless platform lives under [`docs/`](docs/):

* Getting Started — [docs/developer/introduction/getting-started.md](docs/developer/introduction/getting-started.md): the current onboarding guide for the headless platform (Docker Compose quick start, target runtime, and database bootstrap). For the quick, top-level build & run steps, see [INSTRUCTIONS.md](INSTRUCTIONS.md).
* API reference (REST / OpenAPI, `/api/v1/`) — [docs/api-reference/](docs/api-reference/)
* Plugin SDK (`IErpPlugin`) — [docs/plugin-sdk/](docs/plugin-sdk/)
* Architecture, including the `ICodeVariable`/`BaseErpPageModel` compatibility shim — [docs/architecture/](docs/architecture/) ([adapter doc](docs/architecture/icodevariable-adapter.md))
* Migration (RazorPages/Blazor → headless) — [docs/migration/](docs/migration/)
* Deployment & operations (Docker Compose / Kubernetes) — [docs/deployment/](docs/deployment/)

Related repositories

[WebVella-ERP-StencilJs](https://github.com/WebVella/WebVella-ERP-StencilJs)

[WebVella-ERP-Seed](https://github.com/WebVella/WebVella-ERP-Seed)

[WebVella-TagHelpers](https://github.com/WebVella/TagHelpers)

### Third party libraries

* see [LIBRARIES](LIBRARIES.md) files

## License

* see [LICENSE](https://github.com/WebVella/WebVella-ERP/blob/master/LICENSE.txt) file

## Contact

### Developer/Company

* Homepage: [webvella.com](http://webvella.com)
* Twitter: [@webvella](https://twitter.com/webvella "webvella on twitter")
