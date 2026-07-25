# WebVella.Erp.Client

React single-page application (SPA) — the browser admin/app UI of the headless, container-native WebVella ERP platform.

`WebVella.Erp.Client` is the browser user interface of the **headless** WebVella ERP platform. It is the **replacement for the retired Blazor WebAssembly client** (`WebVella.Erp.WebAssembly`), re-implementing the admin/app UI as a modern React SPA that consumes the versioned `/api/v1/` REST surface served by `WebVella.Erp.Api`. Source: WebVella.Erp.WebAssembly/Client/WebVella.Erp.WebAssembly.csproj:L1 (legacy Blazor WebAssembly SDK, being retired).

> **Scope note (evidence-based honesty).** This README documents the **intended** project. The SPA source and its **exact pinned dependency versions, npm script names, and Node.js version** are delivered by the separate SPA implementation workstream and are marked **"Not available / to be pinned at adoption"** throughout — they are not asserted or guessed here. See the migration guides linked below for the retirement/cutover context.

---

## What it does

`WebVella.Erp.Client` is the React SPA **admin / app shell** for the headless WebVella ERP. It renders the management console and end-user screens for the platform's domain objects — **Entities** and their **Records** — and issues **EQL** queries against the server engine. The server-side **plugin** and **hook** model is unchanged by the refactor; the SPA is purely a new presentation tier over the existing engine.

- **Data access.** The SPA talks to the REST API at **`/api/v1/`** (served by `WebVella.Erp.Api`) and uses **TanStack Query** for server-state fetching, caching, and invalidation. This replaces the retired client's hand-rolled `HttpClient`, whose base address was derived as `serverUrl + "api/"`. Source: WebVella.Erp.WebAssembly/Client/Program.cs:L15 (legacy `serverUrl + "api/"`), L20 (legacy `HttpClient { BaseAddress = ... }`). The new SPA targets the **versioned** `/api/v1/` base path instead of the unversioned `api/` prefix.

- **Authentication.** The SPA signs users in with the **OIDC authorization-code flow (with PKCE)** and attaches the resulting **JWT** as an HTTP **bearer** token on every API call (`Authorization: Bearer <token>`). This supersedes the retired client's `CustomAuthenticationProvider` and `Blazored.LocalStorage`-based token handling. Source: WebVella.Erp.WebAssembly/Client/Program.cs:L21-L23 (legacy `CustomAuthenticationProvider` and `Blazored.LocalStorage`). Because it runs entirely in the browser, the SPA is a **public OIDC client**: it uses PKCE and **no client secret** (see [Key configs](#key-configs-and-defaults) and the authentication reference below).

- **UI and layout.** The interface is composed from **Radix UI Themes 3.x** components — the core vocabulary is **`Button`**, **`TextField`**, **`Select`**, **`Table`**, **`Dialog`**, **`Tabs`**, and **`Callout`** — arranged with the layout primitives **`Box`**, **`Flex`**, and **`Grid`** under a single **`Theme`** root wrapper, and styled with **Tailwind CSS v4** utilities. Screen layout must be composed from these Radix primitives plus Tailwind utility classes rather than hand-rolled CSS on raw `<div>` elements, so that spacing, color, radius, and typography stay on the shared Radix token scale.

### Related documentation

- Migration from the RazorPages UI → [../docs/migration/razorpages-to-react.md](../docs/migration/razorpages-to-react.md)
- Blazor WebAssembly client retirement → [../docs/migration/blazor-retirement.md](../docs/migration/blazor-retirement.md)
- API authentication reference (tokens, scopes, claim mapping) → [../docs/api-reference/authentication.md](../docs/api-reference/authentication.md)

---

## How to run, build, and test

### Prerequisites

- **Node.js and npm.** The exact Node.js version is **Not available / to be pinned at adoption** (owned by the SPA implementation workstream; needs the project's `.nvmrc`/`engines` field once the SPA is scaffolded).
- **A running `WebVella.Erp.Api` instance** reachable at the configured `/api/v1/` base URL — the SPA is a client of that API and cannot load data without it.
- **A reachable OIDC identity provider** for login; the SPA cannot authenticate without one (provider choice is an open decision — see [Key configs](#key-configs-and-defaults)).

### Install, run, and build

The SPA uses the standard **Vite** workflow:

```bash
npm install       # install dependencies
npm run dev       # start the Vite dev server (local development)
npm run build     # produce a production build (static assets)
```

The exact script names and the pinned dependency versions are **defined by the SPA implementation workstream and are "Not available / to be pinned at adoption"** — the commands above show the intended Vite workflow and must not be read as version assertions.

### Tests

The repository currently contains **no test projects**, and the SPA test runner and test command are **Not available / to be confirmed** (to be defined by the SPA implementation workstream when the project is scaffolded).

### Optional: generate client API docs

React/TypeScript client API docs can be generated from **TSDoc** comments with **TypeDoc**, emitting Markdown for the existing MkDocs/TechDocs site via the `typedoc-plugin-markdown` output plugin:

```bash
npx typedoc       # emits Markdown via typedoc-plugin-markdown for MkDocs
```

Researched reference versions are **TypeDoc `0.28.20`** and **`typedoc-plugin-markdown` `4.12.0`**; the SPA-local pinned versions are **to be pinned at adoption** by the SPA workstream.

### Serving

In development the app is served by the Vite dev server; in production it is built to **static assets** and served from a container image or static host behind the platform's ingress. Container/deployment details live in the deployment docs — see [../docs/deployment/configuration-reference.md](../docs/deployment/configuration-reference.md).

---

## Key configs and defaults

Configuration is provided as **environment variables**. Vite exposes variables to client code only when they are prefixed with **`VITE_`**; the values are baked into the static build at build time. The table lists the **key names only** with **non-secret placeholder** examples.

| Env var (key name) | Purpose | Example (non-secret placeholder) |
|--------------------|---------|----------------------------------|
| `VITE_API_BASE_URL` | Base URL of the WebVella ERP REST API (`/api/v1/`) | `https://localhost:5001/api/v1/` |
| `VITE_OIDC_AUTHORITY` | OIDC issuer / authority (discovery) URL | `<oidc-authority-url>` |
| `VITE_OIDC_CLIENT_ID` | Public SPA OIDC client id (no secret) | `<oidc-client-id>` |
| `VITE_OIDC_REDIRECT_URI` | Post-login redirect URI (must be registered at the IdP) | `https://localhost:5173/callback` |
| `VITE_OIDC_POST_LOGOUT_REDIRECT_URI` | Post-logout redirect URI | `https://localhost:5173/` |
| `VITE_OIDC_SCOPE` | Requested OIDC scopes | `openid profile ...` |

The exact final variable names are owned by the SPA implementation workstream; treat the names above as the intended contract and **to be confirmed** where precision is required.

### No secrets in the SPA

Because it runs in the user agent, the SPA is a **public OIDC client**: it authenticates with the **authorization-code flow plus PKCE and uses NO client secret**. Consequently:

- **No secret values** — client secrets, tokens, connection strings, or signing keys — belong in this repository, in committed `.env` files, or in any example in this README.
- The values above are **non-secret** configuration (public URLs and a public client id).
- Any server-side secrets (for example, on `WebVella.Erp.Api` or the identity provider) live in **environment variables / Kubernetes Secrets** and are referenced **by key name only**, never by literal value. See [../docs/deployment/configuration-reference.md](../docs/deployment/configuration-reference.md).

### Contrast with the retired client

The retired Blazor WebAssembly client read a single `serverUrl` value from `wwwroot/appsettings.json` and derived its API base as `apiUrl = serverUrl + "api/"`. Source: WebVella.Erp.WebAssembly/Client/wwwroot/appsettings.json (single `serverUrl`); Source: WebVella.Erp.WebAssembly/Client/Program.cs:L15 (`serverUrl + "api/"`). The new SPA replaces that single-value JSON model with **`VITE_`-prefixed environment variables** and targets the **versioned `/api/v1/`** base path.

### Decision point — identity provider

The platform's OIDC identity provider — **Duende IdentityServer vs. Keycloak** — is **Not available / to be confirmed**. The concrete `VITE_OIDC_*` values (authority URL, scopes, and the registered redirect URIs) depend on the chosen provider and its client registration. The authentication contract is documented provider-neutrally in [../docs/api-reference/authentication.md](../docs/api-reference/authentication.md), and the deployment/config keys in [../docs/deployment/configuration-reference.md](../docs/deployment/configuration-reference.md).

---

## Common failure modes and troubleshooting

### Tailwind base reset conflicts with Radix Themes styling

- **Symptom.** Radix Themes components — most visibly `Button`, `TextField`, and other form controls — render **unstyled or mis-styled** (missing background, padding, or border) even though the components are used correctly.
- **Cause.** **Tailwind CSS v4's base/preflight layer resets element styling** (notably the `button` and input resets), which **overrides Radix Themes' component styles** when the two stylesheets are loaded in the wrong order.
- **Fix (mandatory ordering).** Control stylesheet import order with **`postcss-import`** so that **Tailwind's base layer is imported *before* the Radix Themes styles** — the Radix Themes CSS must win the cascade over Tailwind's preflight. Radix Themes **3.x additionally caps its CSS selector specificity** specifically to improve interoperability with Tailwind, so with the correct import order the two systems coexist. Keep the `@radix-ui/themes` stylesheet import after the Tailwind base import in the SPA's entry stylesheet.

### CORS errors calling `/api/v1/`

- **Symptom.** Browser console shows blocked cross-origin requests to `/api/v1/`; API responses never reach the SPA.
- **Cause.** The API's CORS policy does not allow the SPA's origin.
- **Fix.** Configure `WebVella.Erp.Api` CORS to allow the SPA's origin(s) — both the Vite dev origin and the deployed production origin.

### 401 Unauthorized / token refresh

- **Symptom.** API calls fail with **`401 Unauthorized`**.
- **Cause.** A missing, expired, or invalid **JWT** access token.
- **Fix.** Ensure every API request carries the OIDC access token as `Authorization: Bearer <token>`, and that the token is **refreshed** (via the OIDC refresh token) before it expires. The token acquisition, validation, and refresh flow — and the `401`/`403` semantics — are documented in [../docs/api-reference/authentication.md](../docs/api-reference/authentication.md).

### OIDC redirect URI mismatch

- **Symptom.** Login fails, or the identity provider rejects the authorization request with a redirect-URI error.
- **Cause.** The value of **`VITE_OIDC_REDIRECT_URI`** is not registered at the identity provider, or does not match the registered value exactly.
- **Fix.** Register the exact redirect URI(s) at the identity provider and ensure `VITE_OIDC_REDIRECT_URI` (and `VITE_OIDC_POST_LOGOUT_REDIRECT_URI`) match the registered values **character-for-character**, including scheme, host, port, and path.
