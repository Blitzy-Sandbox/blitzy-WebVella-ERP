# Third-Party Libraries

This file is the canonical inventory of the third-party packages used across the WebVella ERP
solution — the .NET runtime and plugins, the Blazor WebAssembly client (still present in the
checkout and slated for retirement by the refactor), and the documentation and refactor tooling for
the headless, container-native platform — grouped by registry and purpose.

Versions reflect the current solution manifests (the project `*.csproj` files and `mkdocs.yml`) at
the time of writing; unresolved or greenfield items that are not yet pinned are marked
**"Not available / to be pinned at adoption"** rather than guessed. Every entry cites its source
manifest so the inventory stays verifiable.

## Conventions

- **Registries** — `NuGet` for .NET packages, `npm` for Node/JavaScript tooling and the SPA,
  `pip` for the MkDocs documentation site.
- **Version strings** are quoted exactly as declared in the manifests. NuGet *interval* notation
  such as `[14.0.0]` denotes an **exact-version pin** (only that version is allowed), whereas a
  bare `14.0.0` is the usual minimum-version floor.
- **Source** columns reference a repository-relative manifest path. Where a package appears in more
  than one manifest, a representative source is cited and "and others" is noted.
- **No secrets** — this document lists package names and versions only. It never contains
  credentials, tokens, connection strings, or keys.
- **Target framework note** — of the 19 project (`.csproj`) files in the solution, 17 target
  `net10.0` (for example `WebVella.Erp/WebVella.Erp.csproj`) and 2
  (`WebVella.Erp.WebAssembly/Server` and `WebVella.Erp.WebAssembly/Shared`) target `net7.0`, while
  the refactor specification references ".NET 9" / "ASP.NET Core 9" and the root `README.md` frames
  this as an open ".NET 9 vs net10.0" decision. The authoritative target framework is an open decision point (see
  [Open decision points](#open-decision-points)); this document reports the framework moniker each
  manifest actually declares rather than silently resolving the discrepancy.

## Runtime and core dependencies (NuGet)

Domain and runtime libraries used by the core engine (`WebVella.Erp`), the web/host layer
(`WebVella.Erp.Web`, `WebVella.Erp.Site`), and the bundled plugins.

| Package | Version | Purpose | Source |
|---------|---------|---------|--------|
| AutoMapper | `[14.0.0]` (exact pin) | Object-to-object mapping | `WebVella.Erp/WebVella.Erp.csproj` |
| Npgsql | `[9.0.4]` (exact pin) | PostgreSQL ADO.NET data provider (the `WebVella.Erp/Database` layer) | `WebVella.Erp/WebVella.Erp.csproj` |
| Newtonsoft.Json | `13.0.4` | JSON serialization | `WebVella.Erp/WebVella.Erp.csproj` (and `WebVella.Erp.Web`, `WebVella.Erp.Site`) |
| CsvHelper | `33.1.0` | CSV import/export | `WebVella.Erp/WebVella.Erp.csproj` |
| Ical.Net | `5.1.4` | iCalendar parsing and recurrence expansion | `WebVella.Erp/WebVella.Erp.csproj` |
| Irony.NetCore | `1.1.11` | Grammar/parser toolkit powering EQL (Entity Query Language) | `WebVella.Erp/WebVella.Erp.csproj` |
| Storage.Net | `9.3.0` | Blob/file storage abstraction | `WebVella.Erp/WebVella.Erp.csproj` |
| System.Drawing.Common | `10.0.1` | Imaging primitives | `WebVella.Erp/WebVella.Erp.csproj` |
| MimeMapping | `3.1.0` | MIME type lookup by file name/extension | `WebVella.Erp/WebVella.Erp.csproj` (and `WebVella.Erp.Site`) |
| morelinq | `4.4.0` | Additional LINQ operators | `WebVella.Erp.Site/WebVella.Erp.Site.csproj` |
| HtmlAgilityPack | `1.12.4` | HTML parsing | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` |
| CS-Script | `4.13.1` | Dynamic C# scripting engine (code-based data sources) | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` |
| Microsoft.CodeAnalysis.CSharp | `5.0.0` | Roslyn C# compiler APIs (dynamic scripting) | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` |
| Microsoft.CodeAnalysis.CSharp.Scripting | `5.0.0` | Roslyn C# scripting support | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` |
| Microsoft.CodeAnalysis.CSharp.Workspaces | `5.0.0` | Roslyn workspaces | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` |
| Microsoft.CodeAnalysis.Common | `5.0.0` | Roslyn common APIs | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` |
| Wangkanai.Detection | `8.20.0` | Device/client detection | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` |
| WebVella.TagHelpers | `1.8.0` | Razor tag helpers for the admin UI | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` |
| MailKit | `4.14.1` | SMTP/IMAP email delivery (Mail plugin SMTP queue) | `WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj` |

> **Security note (AutoMapper `[14.0.0]`)** — the pinned version is affected by a High-severity
> Denial-of-Service advisory, GHSA-rvv3-g6hj-g44x / CVE-2026-32933 (CWE-674 Uncontrolled Recursion;
> CVSS 3.1 base score 7.5). Mapping a deeply nested object graph recurses without a default depth
> limit, throwing an uncatchable `StackOverflowException` that terminates the entire process; the
> payload can arrive over the network (for example an API request body), needs no authentication, and
> is low-complexity to construct. The fix ships in AutoMapper `15.1.1` and `16.1.1`; **no patch is
> planned for the `14.x` line**, so remediation requires moving to `>= 15.1.1` (validate the license
> terms of the fixed line before adopting) through the implementation workstream after compatibility
> testing. This is documentation only — no package version is changed here (AAP §0.9.2).
> Source: `WebVella.Erp/WebVella.Erp.csproj:L47`.
>
> **Security note (MailKit `4.14.1` and its transitive MimeKit)** — the pinned version predates
> MailKit `4.16.0`, which fixes a Moderate STARTTLS response-injection / SASL-downgrade advisory
> (GHSA-9j88-vvj5-vhgr / CVE-2026-41319; CVSS 3.1 base score 6.5). MailKit `4.14.1` also pulls in
> **MimeKit `4.14.x` transitively**, which is affected by a separate Moderate CRLF-injection advisory,
> GHSA-g7hc-96xr-gvvx / CVE-2026-30227 (CWE-93; CVSS 3.1 base score 6.9): a carriage-return/line-feed
> embedded in a quoted SMTP local-part enables SMTP command injection and email forgery. MimeKit fixes
> it in `4.15.1`, and upgrading MailKit to `>= 4.16.0` transitively raises MimeKit to a fixed line
> (`>= 4.15.1`), remediating **both** advisories in a single step. Plan this upgrade through the
> implementation workstream after compatibility validation; do not treat `4.14.1` as a secure,
> canonical pin. This is documentation only — no package version is changed here (AAP §0.9.2).
> Source: `WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj` (MimeKit is a transitive
> dependency of MailKit).

## Authentication and web hosting (NuGet)

ASP.NET Core hosting, JWT authentication, MVC/Razor, and `Microsoft.Extensions.*` platform
packages. In the current (legacy) hosting model, `WebVella.Erp.Site` selects Bearer/JWT when an
`Authorization` header is present and otherwise falls back to a cookie; the headless refactor moves
to OIDC/JWT bearer only (see the API reference and architecture/security documentation).

| Package | Version | Purpose | Source |
|---------|---------|---------|--------|
| Microsoft.AspNetCore.Authentication.JwtBearer | `10.0.1` | JWT bearer token validation | `WebVella.Erp.Site/WebVella.Erp.Site.csproj` (and `WebVella.Erp.Site.Project`) |
| System.IdentityModel.Tokens.Jwt | `8.15.0` | JWT creation and validation primitives | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` (and `WebVella.Erp.WebAssembly/Client`) |
| Microsoft.AspNetCore.Mvc.NewtonsoftJson | `10.0.1` | JSON.NET input/output formatter for MVC | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` (and `WebVella.Erp.Site`, the `WebVella.Erp.Site.*` hosts, and `WebVella.Erp.Plugins.Project`) |
| Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation | `10.0.1` | Runtime Razor view compilation | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` |
| Microsoft.Web.LibraryManager.Build | `3.0.71` | Client-side library restore (LibMan) at build time | `WebVella.Erp.Site/WebVella.Erp.Site.csproj` |
| Microsoft.Extensions.FileProviders.Embedded | `10.0.1` | Embedded-resource file providers | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` |
| Microsoft.Extensions.Http | `10.0.1` | `HttpClient` factory | `WebVella.Erp.WebAssembly/Client/WebVella.Erp.WebAssembly.csproj` |

### .NET shared framework reference

Several projects reference the ASP.NET Core shared framework rather than individual packages:

- `FrameworkReference Include="Microsoft.AspNetCore.App"` — provides the ASP.NET Core runtime.
  Declared by `WebVella.Erp`, `WebVella.Erp.Web`, and the plugins `WebVella.Erp.Plugins.Crm`,
  `WebVella.Erp.Plugins.Mail`, `WebVella.Erp.Plugins.Next`, `WebVella.Erp.Plugins.Project`, and
  `WebVella.Erp.Plugins.SDK`.
  Source: `WebVella.Erp/WebVella.Erp.csproj` (and the manifests listed above).

### Commented-out (inactive) package references

The following `PackageReference` entries appear in the project manifests but are **commented out**,
so they are **not active dependencies** of the current build. They are listed separately here for
completeness and to prevent them from being mistaken for active packages; each cites the manifest
and the line(s) where it appears commented.

| Package | Version | Status | Source (commented) |
|---------|---------|--------|--------------------|
| Microsoft.AspNetCore.Http.Abstractions | `2.2.0` | commented out (inactive) | `WebVella.Erp/WebVella.Erp.csproj:51` |
| Microsoft.Extensions.Caching.Abstractions | `10.0.0` | commented out (inactive) | `WebVella.Erp/WebVella.Erp.csproj:52-58` |
| Microsoft.Extensions.Caching.Memory | `10.0.0` | commented out (inactive) | `WebVella.Erp/WebVella.Erp.csproj:52-58` |
| Microsoft.Extensions.Configuration.Json | `10.0.0` | commented out (inactive) | `WebVella.Erp/WebVella.Erp.csproj:52-58` |
| Microsoft.Extensions.Hosting.Abstractions | `10.0.0` | commented out (inactive) | `WebVella.Erp/WebVella.Erp.csproj:52-58` |
| Microsoft.Extensions.Logging | `10.0.0` | commented out (inactive) | `WebVella.Erp/WebVella.Erp.csproj:52-58` |
| Microsoft.Extensions.Logging.Console | `10.0.0` | commented out (inactive) | `WebVella.Erp/WebVella.Erp.csproj:52-58` |
| Microsoft.Extensions.Logging.Debug | `10.0.0` | commented out (inactive) | `WebVella.Erp/WebVella.Erp.csproj:52-58` |
| Microsoft.AspNetCore.Mvc.ViewFeatures | `2.2.0` | commented out (inactive) | `WebVella.Erp.Web/WebVella.Erp.Web.csproj:136` |
| Microsoft.AspNetCore.StaticFiles | `2.2.0` | commented out (inactive) | `WebVella.Erp.Web/WebVella.Erp.Web.csproj:137` |
| SixLabors.ImageSharp | `3.1.6` | commented out (inactive) | `WebVella.Erp.Web/WebVella.Erp.Web.csproj:139-140` |
| SixLabors.ImageSharp.Drawing | `2.1.5` | commented out (inactive) | `WebVella.Erp.Web/WebVella.Erp.Web.csproj:139-140` |
| System.Linq | `4.3.0` | commented out (inactive) | `WebVella.Erp.Site/WebVella.Erp.Site.csproj:51-52` |
| System.Threading | `4.3.0` | commented out (inactive) | `WebVella.Erp.Site/WebVella.Erp.Site.csproj:51-52` |
| Microsoft.AspNetCore.ResponseCompression | `2.2.0` | commented out (inactive) | `WebVella.Erp.Site/WebVella.Erp.Site.csproj:56` |

## Legacy client stack (present; slated for retirement)

The Blazor WebAssembly client is still present in the checkout and is part of the hosting model that
the refactor plans to retire; it is slated to be superseded by the planned React single-page
application in `WebVella.Erp.Client` (not yet present in the checkout). It is inventoried here for
completeness and for migration reference — see
[`docs/migration/blazor-retirement.md`](docs/migration/blazor-retirement.md).

| Package | Version | Purpose | Source |
|---------|---------|---------|--------|
| Microsoft.AspNetCore.Components | `10.0.1` | Razor component model | `WebVella.Erp.Plugins.MicrosoftCDM/WebVella.Erp.Plugins.MicrosoftCDM.csproj` |
| Microsoft.AspNetCore.Components.Web | `10.0.1` | Web Razor components | `WebVella.Erp.Plugins.MicrosoftCDM/WebVella.Erp.Plugins.MicrosoftCDM.csproj` |
| Microsoft.AspNetCore.Components.WebAssembly | `10.0.1` | Blazor WebAssembly runtime | `WebVella.Erp.WebAssembly/Client/WebVella.Erp.WebAssembly.csproj` |
| Microsoft.AspNetCore.Components.WebAssembly.DevServer | `10.0.1` | WebAssembly development server (dev-only) | `WebVella.Erp.WebAssembly/Client/WebVella.Erp.WebAssembly.csproj` |
| Microsoft.AspNetCore.Components.WebAssembly.Authentication | `10.0.1` | WebAssembly authentication support | `WebVella.Erp.WebAssembly/Client/WebVella.Erp.WebAssembly.csproj` |
| Microsoft.Extensions.Http | `10.0.1` | `HttpClient` factory for the client | `WebVella.Erp.WebAssembly/Client/WebVella.Erp.WebAssembly.csproj` |
| Blazored.LocalStorage | `4.5.0` | Browser local-storage access | `WebVella.Erp.WebAssembly/Client/WebVella.Erp.WebAssembly.csproj` |
| System.IdentityModel.Tokens.Jwt | `8.15.0` | JWT handling on the client | `WebVella.Erp.WebAssembly/Client/WebVella.Erp.WebAssembly.csproj` |
| Microsoft.AspNetCore.Components.WebAssembly.Server | `7.0.13` | Hosting for the WebAssembly app (targets `net7.0`) | `WebVella.Erp.WebAssembly/Server/WebVella.Erp.WebAssembly.Server.csproj` |

> Note: the `WebVella.Erp.WebAssembly/Server` and `WebVella.Erp.WebAssembly/Shared` projects target
> `net7.0`, unlike the rest of the solution which targets `net10.0`.

## Vendored native libraries

| File | Purpose | Source |
|------|---------|--------|
| `libwkhtmltox.dll` | A ~29 MB PE32+ (x64, Windows) native binary vendored directly in the repository rather than resolved from a package registry. The file name corresponds to the `wkhtmltox` (wkhtmltopdf) HTML-to-PDF/image native library, but its consumer and purpose within this repository are **Not available / unverified** (see note below). | `ExternalLibraries/libwkhtmltox.dll` |

> This binary is checked into the repository under `ExternalLibraries/`. It is not referenced by any
> `PackageReference`, and a repository search for `wkhtmltox`/`wkhtmltopdf` found no loader or caller,
> so its consumer within this solution is **Not available / unverified**. Only the vendored file
> identity above is confirmed. Source: `ExternalLibraries/libwkhtmltox.dll` (no referencing manifest
> or source file found in the checkout).

## New refactor and documentation tooling

Tooling introduced by the headless, container-native refactor and the documentation workstream.
The versions below are those referenced by the Agent Action Plan; they are point-in-time values that
may not be the latest releases, and none of these packages are present in the repository manifests
yet. Treat every version here as **Not available / to be pinned at adoption** after compatibility and
security validation by the implementation workstream.

| Registry | Package | Version | Purpose |
|----------|---------|---------|---------|
| NuGet | Microsoft.AspNetCore.OpenApi | `10.0.x` (matches the `net10.0` target) | Generate the OpenAPI 3.1 document for the new `WebVella.Erp.Api` |
| NuGet | Scalar.AspNetCore | `2.9.0` (AAP-referenced; not the latest — the `2.16.x` series is the current stable line as of mid-2026; final pin to be confirmed) | Interactive OpenAPI reference UI (`MapScalarApiReference()`, Development only) |
| npm | @stoplight/spectral-cli | `6.16.2` | Lint/validate the generated OpenAPI document in CI |
| NuGet | docfx | `2.78.5` | Optional static .NET API reference from C# XML-doc comments |
| npm | typedoc | `0.28.20` | Generate React/TypeScript client API docs from TSDoc comments |
| npm | typedoc-plugin-markdown | `4.12.0` | Emit Markdown from TypeDoc for MkDocs integration |
| npm | @mermaid-js/mermaid-cli | `11.16.0` | Optional CI pre-render of Mermaid diagrams to SVG/PNG (`mmdc`) |
| pip | mkdocs-techdocs-core | `>= 1.0.2` (1.2.x documented) | Existing Backstage TechDocs MkDocs wrapper that renders the docs site (Mermaid needs `>= 1.0.2`). Source: `mkdocs.yml` |
| pip | mkdocs-mermaid2-plugin | existing (unpinned `mermaid2`) | Existing build-time Mermaid rendering. Source: `mkdocs.yml` |

> The legacy `@stoplight/spectral` package is deprecated in favor of `@stoplight/spectral-cli`; only
> the CLI package is used. `Microsoft.AspNetCore.OpenApi` on .NET 10 depends on `Microsoft.OpenApi`
> v2.x; the `Scalar.AspNetCore` version ultimately pinned must be validated for compatibility with
> that combination before adoption.
>
> **Security note (`pymdown-extensions 10.21.3` — hash-pinned transitive dependency of the docs
> toolchain)** — the compiled documentation requirements pin `pymdown-extensions==10.21.3`, which is
> affected by a Medium-severity path-traversal advisory, GHSA-9xwg-3r6f-jcx2 / CVE-2026-61632 (CWE-22
> Improper Limitation of a Pathname to a Restricted Directory; CVSS 3.1 base score 5.3). In the
> `pymdownx.b64` extension, a crafted `<img src>` reference resolves relative to `base_path` without
> normalizing `..` segments, letting a malicious Markdown source read files outside the intended
> directory. The fix ships in `pymdown-extensions 11.0` (the `11.0.0` release; `11.0.1` is the current
> latest). **This pin cannot be bumped in isolation:** `mkdocs-techdocs-core==1.7.0` — the latest
> release, required for correct Mermaid rendering — *directly* hard-pins `pymdown-extensions==10.21.3`
> (`mkdocs-material==9.7.6` only requires `>= 10.2`, so it is not the binding constraint), and there
> is no newer `mkdocs-techdocs-core` release to move to. Recompiling the requirements with
> `pymdown-extensions>=11.0` fails with `ResolutionImpossible`, the conflict tracing to the
> `mkdocs_techdocs_core-1.7.0` wheel's `==10.21.3` requirement. **Residual risk is low and the sink is
> unreachable in this repository:** the vulnerable `pymdownx.b64` extension is *not* enabled — the
> only Markdown extension configured is `pymdownx.superfences` (for Mermaid) — and the built site emits
> zero `data:…;base64,` payloads and zero `base64://` includes. No package version is changed here;
> remediation is deferred to whenever `mkdocs-techdocs-core` publishes a build that relaxes the
> `pymdown-extensions` pin, at which point the requirements are recompiled through the implementation
> workstream (AAP §0.9.2). This is documentation only. Source: `requirements-docs.txt:L355` (the
> hash-pinned `== 10.21.3`, `# via mkdocs-material / mkdocs-mermaid2-plugin / mkdocs-techdocs-core`);
> `mkdocs.yml:L233` (only `pymdownx.superfences` enabled); `requirements-docs.in` (immovability
> rationale for the `mkdocs-techdocs-core==1.7.0` pin).
>
> **Security note (`brace-expansion` — transitive under `@stoplight/spectral-cli`, remediated in CI)**
> — `@stoplight/spectral-cli@6.16.2` (the latest release) transitively resolves `brace-expansion`
> below `5.0.8`, which is affected by a High-severity Denial-of-Service advisory, GHSA-mh99-v99m-4gvg /
> CVE-2026-14257 (CWE-400 Uncontrolled Resource Consumption / CWE-770 Allocation of Resources Without
> Limits; CVSS 3.1 base score 7.5, vector `AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:H`). `expand()` bounds the
> *number* of results it produces but not their length, so a pattern chaining many brace groups
> allocates unbounded memory and crashes the process out-of-memory; the input needs no authentication
> and is low-complexity to construct. The fix ships in `brace-expansion 5.0.8` (all earlier versions
> are affected). Because Spectral is executed from an ephemeral, run-time-only manifest in the
> Documentation CI `openapi-lint` job (there is no committed `package.json` or lockfile anywhere in the
> repository), the workflow pins the fixed transitive version through an npm `overrides` block
> (`"brace-expansion": ">=5.0.8"`) and invokes the locally-installed binary, rather than relying on
> `npx --yes`, which would resolve the vulnerable version. `spectral-cli` is intentionally kept at its
> pinned `6.16.2` — `npm audit fix --force` is *not* used, as it would downgrade `spectral-cli` to
> `1.1.0`. This is documentation-CI configuration only; no application package version is changed
> (AAP §0.9.2). Source: `.github/workflows/docs.yml` (the `openapi-lint` job's ephemeral-manifest
> `overrides` block).

### Client SPA dependencies (greenfield)

The new `WebVella.Erp.Client` single-page application is greenfield — there is no `package.json` in
the checkout yet. The component and styling vocabulary is Radix UI plus Tailwind CSS; **exact
versions are Not available / to be pinned at adoption by the SPA workstream.**

| Registry | Package | Version | Purpose |
|----------|---------|---------|---------|
| npm | @radix-ui/themes | Radix Themes `3.x` — Not available / to be pinned at adoption | Styled, accessible component library with layout primitives (`Box`, `Flex`, `Grid`, `Theme`) |
| npm | @radix-ui/react-* (primitives) | Not available / to be pinned at adoption | Low-level unstyled, accessible component primitives |
| npm | @radix-ui/colors | Not available / to be pinned at adoption | Accessible color scales (Radix Colors) |
| npm | tailwindcss | `v4` — Not available / to be pinned at adoption | Utility-first CSS framework |
| npm | @tanstack/react-query (TanStack Query) | Not available / to be pinned at adoption | Server-state/data-access hooks |
| npm | vite | Not available / to be pinned at adoption | SPA build tool and dev server |

> Styling caveat: Tailwind's base/button reset can interfere with Radix Themes component styling.
> The SPA build must order styles via `postcss-import` (import Tailwind base before Radix Themes
> styles); Radix Themes `3.x` caps CSS selector specificity to improve Tailwind interoperability.

### Optional documentation CI validators

Recommended for the documentation CI gate; **exact versions are Not available / to be pinned at
adoption.**

| Registry | Package | Version | Purpose |
|----------|---------|---------|---------|
| npm | markdownlint-cli | Not available / to be pinned at adoption | Markdown style linting |
| npm/other | lychee *or* markdown-link-check | Not available / to be pinned at adoption | Broken-link checking |

## Open decision points

The following are unresolved and directly affect which dependencies get pinned. They are recorded
as **"Not available / to be confirmed"** rather than assumed:

- **Authentication provider** — Duende IdentityServer vs. Keycloak is undecided; the OIDC/JWT
  configuration and any provider-specific client library are pending that choice.
- **Worker scheduler** — Quartz.NET vs. Hangfire is undecided for `WebVella.Erp.Worker`; the
  scheduler package is pending.
- **Authoritative target framework** — the project manifests declare `net10.0` while the refactor
  specification references ".NET 9" / "ASP.NET Core 9" and the root `README.md` frames this as an
  open ".NET 9 vs net10.0" decision. The resolved target must be confirmed before the framework
  and `Microsoft.Extensions.*` / `Microsoft.AspNetCore.*` package lines are finalized.
