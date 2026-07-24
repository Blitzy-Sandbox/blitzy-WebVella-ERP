<!--{"sort_order":3, "name": "authentication", "label": "Authentication"}-->
# Authentication

> **Planned target design — Not available in this checkout.** There is **no
> `WebVella.Erp.Api` project** in `WebVella.ERP3.sln`, so the `/api/v1/`
> authentication behavior described here is **proposed design**, not implemented
> behavior. Every route, token endpoint, scope name, and claim name below is
> **Not available / to be confirmed** until the API host and the identity
> provider exist. The **current** authorization is the legacy `JWT_OR_COOKIE`
> policy scheme in the RazorPages site host (bearer JWT when an `Authorization`
> header is present, otherwise the `erp_auth_base` cookie), validated against a
> **symmetric** signing key — see [Token validation](#token-validation) for the
> verified legacy details and how the external-OIDC target differs.
> Source: /WebVella.Erp.Site/Startup.cs:L90 (`JWT_OR_COOKIE` default scheme), L96 (`erp_auth_base` cookie).

In the target design, the `/api/v1/` surface would authenticate every protected
request with an **OpenID Connect (OIDC)**-issued **JSON Web Token (JWT)**
presented as an HTTP **Bearer** token. This is intended to replace the session
**authorization cookie** used by the legacy RazorPages web application, whose Web
API performed authorization through a browser cookie. All of the guidance below
is deliberately provider-neutral; the concrete identity provider is an open
decision (see [Identity provider](#identity-provider)).

Source: /WebVella.Erp.Site/Startup.cs:L96 (legacy `erp_auth_base` cookie authorization), /WebVella.Erp.Site/JWT_README.txt (existing JWT notes, superseded by this page)

## Obtaining a token

`/api/v1/` is planned as a **resource server**: it would *consume* access tokens
but not *issue* them. Tokens would be obtained from the platform's OIDC identity
provider using the **authorization-code flow with PKCE**. The browser SPA
(`WebVella.Erp.Client`) is a **public client**: it runs entirely in the user
agent, cannot keep a secret, and therefore **must not be issued or configured
with a client secret**. PKCE (Proof Key for Code Exchange) protects the code
exchange in place of a secret:

1. The client generates a high-entropy **`code_verifier`** and derives a
   **`code_challenge`** from it using the **S256** method, plus a random
   **`state`** and **`nonce`**.
2. The client redirects the user to the identity provider's authorization
   endpoint, sending the `code_challenge`, `state`, and `nonce` and requesting
   the authorization-code grant.
3. The user authenticates with the identity provider.
4. The identity provider redirects back to the client's **registered redirect
   URI** with a short-lived **authorization code**; the client verifies the
   returned `state` matches the value it sent.
5. The client exchanges that code at the token endpoint, presenting the original
   **`code_verifier`** (and **no client secret**), and receives an **access
   token** (a JWT) and, optionally, a **rotating refresh token**. The client
   validates the `nonce` in the resulting ID token.

Tokens are held **in memory** in the SPA (not in `localStorage`), and the
**registered redirect URI** and allowed grant types are configured on the
identity provider. The exact endpoints and client registration are
provider-specific — see the [Identity provider](#identity-provider) decision
point below. The fuller sequence, including claim-mapping internals, lives in the
architecture companion, [Security](../architecture/security.md).

```mermaid
sequenceDiagram
    participant C as Client (SPA, public client)
    participant IdP as Identity Provider (OIDC)
    participant API as API (WebVella.Erp.Api)
    C->>C: Generate code_verifier and code_challenge (S256), state, nonce
    C->>IdP: Authorization request (code grant, code_challenge, state, nonce)
    IdP-->>C: Authorization code (after user authenticates; state echoed)
    C->>IdP: Exchange code at token endpoint (code_verifier, NO client secret)
    IdP-->>C: Access token (JWT) plus optional rotating refresh token
    C->>API: Request /api/v1 with Authorization Bearer access token
    API-->>C: JSON response envelope
```

## Authorizing requests

In the target design, every protected `/api/v1/` request would carry the access
token in the HTTP `Authorization` header using the `Bearer` scheme:

```http
GET https://<host>/api/v1/record/... HTTP/1.1
Authorization: Bearer <access_token>
```

This bearer header is intended to **replace** the legacy `erp_auth_base`
authorization cookie that the RazorPages host relied on. The legacy
`JWT_OR_COOKIE` policy scheme — which forwarded to bearer validation only when an
`Authorization: Bearer` header was present and otherwise fell back to the cookie
— would **not** apply to `/api/v1/`. The headless surface is planned to be
**bearer-JWT-only**: no cookie fallback.

Source: /WebVella.Erp.Site/Startup.cs:L90 (`JWT_OR_COOKIE` policy scheme), L96 (legacy cookie name `erp_auth_base`)

## Token validation

**Target (`/api/v1/`) — Not available / to be confirmed.** As an OIDC resource
server, the API would validate each presented JWT against the **identity
provider's** metadata: the token **issuer** (the IdP's issuer URL), its
**audience** (the API's registered resource identifier), its **lifetime**, and
its **signature** — verified with the IdP's **asymmetric public keys published at
the JWKS endpoint** (`.well-known/openid-configuration` → `jwks_uri`), *not* a
locally held symmetric secret. The concrete issuer, audience, and JWKS values are
**Not available / to be confirmed** until the identity provider (below) and the
`WebVella.Erp.Api` host are defined. A token that fails any check would be
rejected with `401 Unauthorized` (see [Errors](#errors)).

**Legacy (verified) — `WebVella.Erp.Site` host.** The retired site host enabled
four bearer checks — issuer, audience, lifetime, and signature — through
`ValidateIssuer`, `ValidateAudience`, `ValidateLifetime`, and
`ValidateIssuerSigningKey`, but the signature was verified against a **symmetric
`SymmetricSecurityKey`** built from the configured `Settings:Jwt:Key`. This
symmetric, self-issued model is **not** how external-OIDC tokens are validated
and is retained here only to describe the "from" state:

| Parameter | Config key (legacy) | Value | Validation (legacy) |
|-----------|---------------------|-------|---------------------|
| Issuer | `Settings:Jwt:Issuer` | `webvella-erp` (default) | `ValidateIssuer` — the token `iss` must equal the configured issuer. |
| Audience | `Settings:Jwt:Audience` | `webvella-erp` (default) | `ValidateAudience` — the token `aud` must equal the configured audience. |
| Signing key | `Settings:Jwt:Key` | *Not shown — secret (Rule D).* | `ValidateIssuerSigningKey` — **symmetric** `SymmetricSecurityKey`; the signature is verified against this shared secret. |
| Lifetime | token `exp` / `nbf` | validated | `ValidateLifetime` — expired or not-yet-valid tokens are rejected. |

Source: /WebVella.Erp.Site/Startup.cs:L110 (`ValidIssuer = Settings:Jwt:Issuer`), L111 (`ValidAudience = Settings:Jwt:Audience`), L112 (`IssuerSigningKey = new SymmetricSecurityKey(... Settings:Jwt:Key)`); default issuer/audience `webvella-erp` per Technical Specification §1.2.2.3.

> **Security note (Rule D).** Signing material is referenced only by its
> configuration **key name** (for example `Settings:Jwt:Key` in the legacy host,
> or the IdP's JWKS URL in the target). Secret **values** are **never** reproduced
> in documentation, logs, or examples; supply them through an environment
> variable or a Kubernetes Secret. See the
> [Configuration reference](../deployment/configuration-reference.md) for how the
> key, issuer, and audience are provided to the API host.

The legacy issuer and audience **names** default to the non-secret identifier
`webvella-erp`; only the signing-key *value* is sensitive.

## Scopes

Access tokens are planned to carry **scopes** (alongside their other claims) that
gate which parts of the API a caller may use. Scope checks would apply *in
addition* to the token validation above: a token can be valid yet still lack the
scope an endpoint requires, in which case the request would be refused with
`403 Forbidden`.

- **The exact scope names and the endpoints they gate: Not available / to be
  confirmed.** Needed: the scope catalog defined by the `WebVella.Erp.Api` host —
  the set of scope strings and the mapping of each `/api/v1/` endpoint to its
  required scope — once the endpoint definitions are finalized.

## Claim to role and permission mapping

After a token is validated, its OIDC claims would be mapped onto the platform's
**roles** and **permissions**, which is what the in-process managers behind the
endpoints actually enforce today:

- **Metadata / administration operations require the `administrator` role.**
  Entity and field metadata operations (`EntityManager`) and entity-relation
  operations (`EntityRelationManager`) both gate on
  `SecurityContext.HasMetaPermission()`, which returns true only when the user
  holds the `administrator` role (`AdministratorRoleId`). The mapped principal
  must therefore hold `administrator` to call the corresponding entity and
  relation `/api/v1/` endpoints.
- **Record access is governed by per-entity permissions.** Access to record
  operations (`RecordManager`) depends on the `RecordPermissions` configured on
  the target entity (`CanRead`/`CanCreate`/`CanUpdate`/`CanDelete` lists of role
  ids) rather than on a single global role.

Source: /WebVella.Erp/Api/SecurityContext.cs:L26 (role name `administrator`), L109-L117 (`HasMetaPermission` → `AdministratorRoleId`); /WebVella.Erp/Api/EntityManager.cs:L452 (entity meta requires `HasMetaPermission`), L85-L89 and L529-L530 (`RecordPermissions.CanRead`/`CanCreate` per-entity role lists); /WebVella.Erp/Api/EntityRelationManager.cs:L399 (relation meta requires `HasMetaPermission`); /WebVella.Erp/Api/Definitions.cs:L15 (`AdministratorRoleId`).

The mapping from OIDC token claims to WebVella authorization is summarized below:

| OIDC token claim | WebVella role / permission | Effect |
|------------------|----------------------------|--------|
| Role / groups claim *(exact name to be confirmed)* | `administrator` role | Grants entity and relation metadata operations. |
| Role / groups claim *(exact name to be confirmed)* | Per-entity record permissions | Governs record CRUD; evaluated against the target entity. |

- **The exact claim names and values: Not available / to be confirmed.** Needed:
  which OIDC claim (for example a `role` or `groups` claim) carries the
  authorization data, and the claim values that map to the `administrator` role
  and to each per-entity permission. These are determined once the identity
  provider (below) is chosen and the `WebVella.Erp.Api` host defines its
  claim-mapping policy.

## Token refresh

Access tokens are planned to be short-lived. When one expires, the client would
use the **refresh token** obtained during the authorization-code exchange to
request a new access token from the identity provider's token endpoint, without
forcing the user to authenticate interactively again. **Refresh-token rotation**
is expected: each refresh returns a new refresh token and invalidates the prior
one, limiting the value of a leaked token. A request made with an expired access
token would be rejected with `401 Unauthorized`; the client should refresh and
retry.

- **The refresh-token endpoint, token lifetimes, and rotation policy: Not
  available / to be confirmed.** Needed: the access-token and refresh-token
  lifetimes and the refresh/rotation policy, defined by the identity provider and
  the `WebVella.Erp.Api` host once chosen.

## Errors

Authentication and authorization failures would be reported with conventional
HTTP status codes. The concrete target error body (for example an
`application/problem+json` RFC 9457 shape) is **Not available / to be confirmed**
until the API host defines it — see [Errors](errors.md). Error responses must
never echo the token, its claims, stack traces, secrets, or PII:

| Status | Meaning | Typical cause |
|--------|---------|---------------|
| `401 Unauthorized` | The request is not authenticated. | A missing, invalid, malformed, or expired JWT bearer token, or a token that fails issuer, audience, or signature validation. |
| `403 Forbidden` | The caller is authenticated but not authorized. | The mapped principal lacks the required role or permission — for example a non-`administrator` caller invoking an entity/metadata endpoint, or a token missing a required scope. |

See [Errors](errors.md) for the full error model and the complete list of status
codes the API can return.

Source: /WebVella.Erp/Api/SecurityContext.cs:L109-L117 (`HasMetaPermission` gates metadata operations on the `administrator` role).

## Identity provider

> **Decision point — Not available / to be confirmed.** The OIDC identity
> provider for the headless platform is **undecided**: the candidates are
> **Duende IdentityServer** and **Keycloak**. All of the guidance above is
> deliberately **provider-neutral**. The provider-specific details are captured
> in the appendix below and will be completed once the provider is selected.

### Provider-specific configuration (appendix)

> **Not available / to be confirmed.** This appendix is a placeholder pending the
> identity-provider decision. Once **Duende IdentityServer** or **Keycloak** is
> chosen, it will document:
>
> - the OIDC **discovery / issuer URL** (`.well-known/openid-configuration`) and
>   the **`jwks_uri`** used for asymmetric signature validation;
> - **client registration** for the SPA as a **public client** (no secret) with
>   PKCE required, plus any confidential (server-side) clients — client id,
>   redirect URIs, allowed grant types;
> - the concrete **scope and claim names**, resolving the "to be confirmed" items
>   in [Scopes](#scopes) and
>   [Claim to role and permission mapping](#claim-to-role-and-permission-mapping);
> - a sample **token-endpoint** call for the authorization-code + PKCE exchange —
>   with no real secrets (Rule D).

## Supersession and related pages

This page **supersedes `WebVella.Erp.Site/JWT_README.txt`**, the ad-hoc JWT setup
note from the legacy site host. That file is retained only for historical
reference; its cookie-based `JWT_OR_COOKIE` fallback does not apply to the
`/api/v1/` surface.

**Related pages**

- [Security](../architecture/security.md) — the authentication **architecture**
  companion (auth-flow internals, claim-mapping design, and provider trade-offs).
- [API Reference overview](index.md) — base URL, versioning, and the response
  envelope.
- [Errors](errors.md) — the full error model behind the `401` and `403` responses
  above.
- [OpenAPI Document](openapi.md) — authorizing calls interactively in the Scalar
  reference UI.
