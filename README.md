[NEW PROJECT ALERT] Check out our new project for [Data collaboration - Tefter.bg](https://github.com/WebVella/WebVella.Tefter).

[NEW PROJECT ALERT] Check out our new project for [Document template generation](https://github.com/WebVella/WebVella.DocumentTemplates).

---

[![Project Homepage](https://img.shields.io/badge/Homepage-blue?style=for-the-badge)](https://webvella.com)
[![Dotnet](https://img.shields.io/badge/platform-.NET-blue?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![GitHub Repo stars](https://img.shields.io/github/stars/WebVella/WebVella-ERP?style=for-the-badge)](https://github.com/WebVella/WebVella-ERP/stargazers)
[![Nuget version](https://img.shields.io/nuget/v/WebVella.ERP?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![Nuget download](https://img.shields.io/nuget/dt/WebVella.ERP?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![WebVella Document Templates License](https://img.shields.io/badge/MIT-green?style=for-the-badge)](https://github.com/WebVella/WebVella-ERP/blob/master/LICENSE.txt)

---

WebVella ERP 
======
**WebVella ERP** is a free and open-source web platform that targets extreme customization and pluggability in service of any business data-management needs. It is built upon our experience, best practices, and the newest available technologies.

WebVella ERP is evolving into a **headless, container-native platform**. The product is being split into a REST/OpenAPI API host (`WebVella.Erp.Api`, serving `/api/v1/`), a React single-page-application client (`WebVella.Erp.Client`), and a background worker (`WebVella.Erp.Worker`) — all built on top of the unchanged core engine (`WebVella.Erp`) and PostgreSQL, and extended through a formal `IErpPlugin` plugin contract.

The database of choice is **PostgreSQL**, and **Linux containers are the primary deployment target** — Docker Compose for local development and Kubernetes for production. The authoritative .NET **target runtime is a to-be-confirmed decision point** (.NET 9 vs .NET 10 / `net10.0`): the project manifests currently declare `net10.0` (see `WebVella.Erp/WebVella.Erp.csproj`), which has not yet been reconciled with earlier ".NET 9" references and will be confirmed before release.

If you want this project to continue or just like it, we will greatly appreciate your support of the project by: 
* giving it a "star" 
* contributing to the source
* Become a Sponsor: Click on the Sponsor button and Thank you in advance

## Getting started

WebVella ERP runs as a set of Linux containers. For a containerized quick start with Docker Compose, together with build-and-run instructions, see [INSTRUCTIONS.md](INSTRUCTIONS.md).

### Documentation

The documentation set for the headless platform lives under [`docs/`](docs/):

* Getting Started — [docs/developer/introduction/getting-started.md](docs/developer/introduction/getting-started.md)
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
#### Developer/Company
* Homepage: [webvella.com](http://webvella.com)
* Twitter: [@webvella](https://twitter.com/webvella "webvella on twitter")



