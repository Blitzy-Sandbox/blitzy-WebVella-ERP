<!--{"sort_order":2, "name": "openapi", "label": "OpenAPI Document"}-->
# OpenAPI Document

> **Planned target design — Not available in this checkout.** There is **no `WebVella.Erp.Api` project** in `WebVella.ERP3.sln` and **no generated OpenAPI document** anywhere in the checkout, so nothing on this page is runnable today. Every command, endpoint, and package wiring below is **proposed design** for the headless target and is **Not available / to be confirmed** until the API host exists. The version numbers are the values **pinned by the Agent Action Plan** (AAP §0.7.1); confirm the latest published versions against their registries at adoption.

In the target design, the `WebVella.Erp.Api` host would auto-generate an **OpenAPI 3.1** description of every `/api/v1/` endpoint. That machine-readable document is intended to be the source of truth from which the human-readable reference pages — [Records](records.md), [Entities & Metadata](entities.md), [EQL Query](eql.md), and [Files](files.md) — are derived.

## Generation

The OpenAPI document is planned to be produced by **`Microsoft.AspNetCore.OpenApi` 10.0.x**, the first-party ASP.NET Core package that emits an OpenAPI description for Minimal API endpoints. Its major version tracks the project's target framework, which is currently `net10.0`.

Source: /WebVella.Erp/WebVella.Erp.csproj:L4 (`<TargetFramework>net10.0</TargetFramework>`)

On .NET 10, `Microsoft.AspNetCore.OpenApi` depends on **`Microsoft.OpenApi` v2.x** for its underlying object model, and the emitted document would conform to the OpenAPI 3.1 specification.

Source: Technical Specification §0.7.1 (Documentation Dependencies)

> **Decision point — Not available / to be confirmed.** The authoritative target runtime is unresolved: the refactor specification says ".NET 9", while the codebase currently targets `net10.0` (`Source: /WebVella.Erp/WebVella.Erp.csproj:L4`). The `Microsoft.AspNetCore.OpenApi` **package major version must track whichever runtime is finally chosen** — `10.0.x` for `net10.0`. This page does not assert one runtime; the version is pinned once the target framework is confirmed.

## Accessing the document

Once the API host exists, the generated JSON would be served at **`/openapi/v1.json`**. The command below is **illustrative and not runnable today** (no API host); the host name is supplied through a quoted shell variable rather than an unquoted `<...>` placeholder (which the shell would misread as a redirection):

```bash
# Not runnable yet — requires the WebVella.Erp.Api host.
API_HOST="api.example.internal"
curl "https://${API_HOST}/openapi/v1.json" -o openapi.json
```

The document would describe the same `/api/v1/` surface that the reference pages cover by hand, so client generators and API tooling could consume it directly.

## Interactive reference (Scalar)

An interactive reference UI is planned via **`Scalar.AspNetCore`** (version **pinned by AAP §0.7.1 at 2.9.0**; newer releases exist, so confirm the version against the NuGet registry at adoption), mounted at **`/scalar`** through `MapScalarApiReference()`. Scalar renders the generated OpenAPI document as a browsable, try-it-out reference and would be **enabled in the Development environment only** — never exposed in Production. The pinned Scalar version is intended to be compatible with `Microsoft.AspNetCore.OpenApi` 10.0.x and `Microsoft.OpenApi` v2.x.

Source: Technical Specification §0.7.1 (Documentation Dependencies)

```csharp
// Illustrative target wiring only — would live in the WebVella.Erp.Api host (out of scope here).
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();            // would serve /openapi/v1.json
    app.MapScalarApiReference(); // would serve /scalar
}
```

The snippet above is **illustrative** of the target wiring only; the actual endpoint code would live in the `WebVella.Erp.Api` host and is out of scope for this documentation workstream. To authorize try-it-out calls made from the Scalar UI, supply a bearer token as described in [Authentication](authentication.md).

Source: Technical Specification §0.10.1 (Execution Parameters)

## Building the document

To emit the document locally once the host exists, run the API host; the OpenAPI JSON would then be available at `/openapi/v1.json` (and the Scalar UI at `/scalar` in Development). This command is **not runnable today** — the project does not exist:

```bash
# Not runnable yet — the WebVella.Erp.Api project does not exist in this checkout.
dotnet run --project WebVella.Erp.Api
```

Source: Technical Specification §0.10.1 (Execution Parameters)

## Linting

The generated document is planned to be validated in CI with **Spectral** (**`@stoplight/spectral-cli` 6.16.2**), which checks the OpenAPI document for style and correctness issues. Spectral lints a **local file**, so the document must first be written to disk before linting. The sequence below is self-contained — it downloads the document, then lints that file — but is **not runnable today** (no API host to emit the document):

```bash
# Not runnable yet — requires the generated document from the API host.
API_HOST="api.example.internal"
curl "https://${API_HOST}/openapi/v1.json" -o openapi.json
spectral lint openapi.json --ruleset .spectral.yaml
```

Spectral 6.x requires an explicit ruleset; the repository ships `.spectral.yaml` — which extends the built-in `spectral:oas` ruleset — passed via `--ruleset`. Run without a ruleset, Spectral 6.x exits non-zero with "No ruleset has been defined." Source: /.spectral.yaml

The legacy `@stoplight/spectral` package is **deprecated** in favor of `@stoplight/spectral-cli`; only the CLI package is used.

Source: Technical Specification §0.7.1 (Documentation Dependencies)

## Related pages

- [API Reference overview](index.md) — base URL, `/api/v1/` versioning, and the response envelope.
- [Records](records.md), [Entities & Metadata](entities.md), [EQL Query](eql.md), and [Files](files.md) — the human-readable companions to this machine-readable document.
- [Authentication](authentication.md) — how to obtain a bearer token and authorize calls made from the Scalar UI.
