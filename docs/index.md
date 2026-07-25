# WebVella ERP — Documentation

WebVella ERP is a free and open-source, metadata-driven business-data platform — you model your domain as **Entities** and **Records** and query it with **EQL** — for the quick and painless creation of business web applications on the unchanged core engine (`WebVella.Erp`) and PostgreSQL. Source: /WebVella.Erp/WebVella.Erp.csproj:L4. It is evolving into a **headless, container-native platform**: a versioned REST/OpenAPI 3.1 surface (`/api/v1/`) served by the new `WebVella.Erp.Api` host, a React single-page-application client (`WebVella.Erp.Client`), and a background worker (`WebVella.Erp.Worker`), all extended through a formal `IErpPlugin` plugin contract. The authoritative runtime target is still to be confirmed — see **Status / open decisions**, below.

> **Planned (headless refactor — not yet implemented).** The `WebVella.Erp.Api`, `WebVella.Erp.Client`, and `WebVella.Erp.Worker` projects, the `/api/v1/` REST surface, and the container deployment assets do **not exist in this checkout yet**; they are delivered by separate implementation workstreams. This site documents that **target** state, and pages describing the new hosts carry their own "planned" notes.

## Documentation map

New to the platform? Start at the top and work down.

- **[Getting Started](developer/introduction/getting-started.md)** — run the stack locally with Docker Compose.
- **[Developer Guide](developer/introduction/overview.md)** — the core engine and its Entity, Record, EQL, hook, and plugin model (the existing developer reference).
- **[API Reference](api-reference/index.md)** — the REST / OpenAPI 3.1 surface served under `/api/v1/`.
- **[Plugin SDK](plugin-sdk/ierplugin-contract.md)** — author plugins against the `IErpPlugin` contract.
- **[Architecture](architecture/overview.md)** — the headless platform design, including the [ICodeVariable / BaseErpPageModel adapter](architecture/icodevariable-adapter.md) compatibility shim.
- **[Migration](migration/overview.md)** — moving from the RazorPages/Blazor hosts to the headless API + SPA + worker.
- **[Deployment & Operations](deployment/docker-compose.md)** — Docker Compose, Kubernetes/Helm, and the configuration reference.
- **[Contributing](contributing/build-and-test.md)** — build, run, and test the platform, and author these docs.

## Status / open decisions

Three decisions remain open and are documented as **Not available / to be confirmed** rather than guessed:

- **Target runtime** — `.NET 9` (per the specification and the root `README.md`) versus `net10.0` (in code). Source: /WebVella.Erp/WebVella.Erp.csproj:L4 shows `<TargetFramework>net10.0</TargetFramework>`.
- **Authentication provider** — Duende IdentityServer versus Keycloak.
- **Worker scheduler** — Quartz.NET versus Hangfire.
