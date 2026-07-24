<!--{"sort_order":2, "name": "openapi", "label": "OpenAPI Document"}-->
# OpenAPI Document

The `WebVella.Erp.Api` host auto-generates an **OpenAPI 3.1** description of every `/api/v1/` endpoint. That machine-readable document is the source of truth from which the human-readable reference pages — [Records](records.md), [Entities & Metadata](entities.md), [EQL Query](eql.md), and [Files](files.md) — are derived.

## Generation

The OpenAPI document is produced by **`Microsoft.AspNetCore.OpenApi` 10.0.x**, the first-party ASP.NET Core package that emits an OpenAPI description for Minimal API endpoints. Its major version tracks the project's target framework, which is `net10.0`.

Source: /WebVella.Erp/WebVella.Erp.csproj:L4 (`<TargetFramework>net10.0</TargetFramework>`)

On .NET 10, `Microsoft.AspNetCore.OpenApi` depends on **`Microsoft.OpenApi` v2.x** for its underlying object model, and the emitted document conforms to the OpenAPI 3.1 specification.

Source: Technical Specification §0.7.1 (Documentation Dependencies)

> **Decision point — Not available / to be confirmed.** The authoritative target runtime is unresolved: the refactor specification says ".NET 9", while the codebase currently targets `net10.0` (`Source: /WebVella.Erp/WebVella.Erp.csproj:L4`). The `Microsoft.AspNetCore.OpenApi` **package major version must track whichever runtime is finally chosen** — `10.0.x` for `net10.0`. This page does not assert one runtime; the version is pinned once the target framework is confirmed.

## Accessing the document

The generated JSON is served by the API host at **`/openapi/v1.json`**:

```bash
curl https://<host>/openapi/v1.json
```

The document describes the same `/api/v1/` surface that the reference pages cover by hand, so client generators and API tooling can consume it directly.

## Interactive reference (Scalar)

An interactive reference UI is provided by **`Scalar.AspNetCore` 2.9.0**, mounted at **`/scalar`** through `MapScalarApiReference()`. Scalar renders the generated OpenAPI document as a browsable, try-it-out reference and is **enabled in the Development environment only** — it is not exposed in Production. Scalar 2.9.0 is compatible with `Microsoft.AspNetCore.OpenApi` 10.0.x and `Microsoft.OpenApi` v2.x.

Source: Technical Specification §0.7.1 (Documentation Dependencies), §0.2.3 (Web Search Research)

```csharp
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();            // serves /openapi/v1.json
    app.MapScalarApiReference(); // serves /scalar
}
```

The snippet above is **illustrative** of the target wiring only; the actual endpoint code lives in the `WebVella.Erp.Api` host and is out of scope for this documentation workstream. To authorize try-it-out calls made from the Scalar UI, supply a bearer token as described in [Authentication](authentication.md).

Source: Technical Specification §0.10.1 (Execution Parameters)

## Building the document

To emit the document locally, run the API host; the OpenAPI JSON is then available at `/openapi/v1.json` (and the Scalar UI at `/scalar` in Development):

```bash
dotnet run --project WebVella.Erp.Api
```

Source: Technical Specification §0.10.1 (Execution Parameters)

## Linting

The generated document is validated in CI with **Spectral** (**`@stoplight/spectral-cli` 6.16.2**), which checks the OpenAPI document for style and correctness issues:

```bash
spectral lint openapi.json
```

The legacy `@stoplight/spectral` package is **deprecated** in favor of `@stoplight/spectral-cli`; only the CLI package is used.

Source: Technical Specification §0.7.1 (Documentation Dependencies)

## Related pages

- [API Reference overview](index.md) — base URL, `/api/v1/` versioning, and the response envelope.
- [Records](records.md), [Entities & Metadata](entities.md), [EQL Query](eql.md), and [Files](files.md) — the human-readable companions to this machine-readable document.
- [Authentication](authentication.md) — how to obtain a bearer token and authorize calls made from the Scalar UI.
