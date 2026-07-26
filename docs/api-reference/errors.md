<!--{"sort_order":8, "name": "errors", "label": "Errors"}-->
# Errors

> **Planned target design — Not available in this checkout.** There is **no
> `WebVella.Erp.Api` project** in `WebVella.ERP3.sln`, so the target `/api/v1/`
> error contract — including whether it adopts RFC 9457
> `application/problem+json` — is **proposed design** and **Not available / to be
> confirmed** until the API host defines it. What is **verified today** is the
> **legacy** error model: the in-process managers return errors *inside* the
> response envelope (`BaseResponseModel`), documented in full below. The RFC 9457
> material on this page describes the *proposed* target and is **not** implemented.

In the target design, the `/api/v1/` surface would report transport- and
HTTP-level errors using the standard `application/problem+json` **problem-details**
media type defined by [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) (which
obsoletes [RFC 7807](https://www.rfc-editor.org/rfc/rfc7807)). Successful results,
and the business/validation outcomes produced by the platform's in-process
managers, use the platform **response envelope** — whose complete legacy field set
(`timestamp`, `success`, `message`, `hash`, `errors`, `accessWarnings`, `object`)
is described in the [API Reference overview](index.md#response-envelope).

Source: /WebVella.Erp/Api/Models/BaseModels.cs:L8-L38 (BaseResponseModel incl. hash, accessWarnings), L40-L48 (ResponseModel.object).

This page is the shared error reference for the API. Every endpoint page —
[Records](records.md), [Entities & Metadata](entities.md), [EQL Query](eql.md),
and [Files](files.md) — links here for the meaning of the status codes it can
return and the shape of a failed response.

## Error content safety

Error responses are returned to clients and are frequently logged, so they **must
never** leak sensitive or internal information. This applies to **every** error
body on this page — both the proposed problem-details `detail`/`instance` and the
legacy envelope `message`/`errors[]`:

- **No internal diagnostics.** Never include stack traces, exception types or
  messages, SQL, connection strings, or internal file-system paths in a
  client-facing error.
- **No secrets (Rule D).** Never echo tokens, credentials, signing keys, or
  configuration secret values.
- **No PII.** Do not place personal data in `detail`, `title`, `instance`, or
  validation messages.
- **`500` is generic.** An unhandled server error returns a generic message and
  (optionally) a correlation id for server-side lookup — never the underlying
  exception. Correlate through logs instead; see
  [Observability](../architecture/observability.md).
- **Validation messages reference field names, not sensitive values.** The legacy
  `errors[].value` echoes the offending input; for sensitive fields it must be
  masked or omitted so a secret is never reflected back to the caller.

## Problem Details — proposed target (application/problem+json)

> **Not available / to be confirmed.** Whether the target adopts RFC 9457, and the
> exact members and extensions it uses, is decided by the `WebVella.Erp.Api` host.
> The following describes the *proposed* shape only.

When a request fails at the transport or HTTP layer, the API would return a
problem-details object and set the `Content-Type` response header to
`application/problem+json` (in contrast to the `application/json` used for
successful envelope responses). The object would carry the standard members
defined by RFC 9457 (each kept safe per [Error content safety](#error-content-safety)):

| Member | Type | Description |
|--------|------|-------------|
| `type` | URI (string) | A URI reference identifying the problem *type*. When omitted it defaults to `about:blank`; when present, dereferencing it should yield human-readable documentation for that problem type. |
| `title` | string | A short, human-readable summary of the problem type. It stays constant across occurrences of the same type. |
| `status` | number | The HTTP status code produced by the origin server for this occurrence, duplicated in the body for convenience (for example `400`). |
| `detail` | string | A human-readable explanation specific to *this* occurrence — kept generic and free of internal diagnostics, secrets, and PII. |
| `instance` | URI (string) | A URI reference identifying the specific occurrence (for example the request path that failed) — with no sensitive query values. |

Beyond the standard members, a problem-details response may carry **extension
members** that add machine-readable context. Validation failures would use an
`errors` extension member — a map keyed by the offending field name, with an array
of human-readable messages per field. A `400` validation response would then look
like this:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "detail": "The request contains one or more invalid fields.",
  "instance": "/api/v1/record/task",
  "errors": {
    "url": [ "URL cannot be blank" ]
  }
}
```

## Relationship to the response envelope (legacy — verified)

The legacy Web API reported errors *inside* the response envelope rather than as a
distinct media type. Its `BaseResponseModel` envelope carries an `errors` field
typed as `List<ErrorModel>`, where each `ErrorModel` has three properties; the
full envelope additionally carries `hash` and `accessWarnings`:

| Property | Meaning |
|----------|---------|
| `key` | The property name, if any, whose validation or execution returned an error. |
| `value` | The property value that caused the problem. **Mask or omit for sensitive fields** (see [Error content safety](#error-content-safety)). |
| `message` | The human-readable message describing the error. |

Source: /WebVella.Erp/Api/Models/BaseModels.cs:L62-L71 (ErrorModel = { key, value, message }), L50-L59 (AccessWarningModel), L8-L38 (BaseResponseModel incl. hash, accessWarnings).

A legacy failure response therefore took the shape below — the complete legacy
envelope with `success` set to `false`, a top-level `message`, an ISO 8601 UTC
`timestamp`, a `hash`, one or more entries in `errors`, an `accessWarnings` list,
and the `object` payload:

```json
{
  "timestamp": "2014-03-03T23:20:23Z",
  "success": false,
  "message": "URL cannot be blank",
  "hash": null,
  "errors": [
    { "key": "url", "value": "", "message": "URL cannot be blank" }
  ],
  "accessWarnings": [],
  "object": { "id": 1 }
}
```

Source: /WebVella.Erp/Api/Models/BaseModels.cs:L8-L38 (BaseResponseModel), /WebVella.Erp/Api/Models/QueryResponse.cs:L9 (QueryResponse : BaseResponseModel).

### Mapping legacy errors to problem details (proposed)

Consumers migrating from the legacy Web API could translate the two models as
follows (the target column is **proposed** and Not available until the API host
defines it):

| Legacy envelope (verified) | Target `/api/v1/` problem details (proposed) |
|----------------------------|----------------------------------------------|
| `success: false` | A non-2xx HTTP status code plus an `application/problem+json` body. |
| `errors[].message` | A human-readable message in the problem-details `errors` extension, listed under the field named by `errors[].key`. |
| `errors[].key` | The field name used as the key in the problem-details `errors` map. |
| `errors[].value` | The offending value; conveyed through `detail` where useful **and safe** (problem details keys validation by field, not by value). |
| `message` | The problem-details `title` (and/or `detail`). |

- **The exact boundary between envelope-reported validation errors and
  problem-details validation errors: Not available / to be confirmed.** Needed:
  the final decision from the `WebVella.Erp.Api` host on whether field-level
  validation failures are returned inside the response envelope (`success: false`
  with `errors[]`) or promoted to a `400`/`422` `application/problem+json`
  response using the `errors` extension member — and, if both are used, the
  precise rule that determines which one applies to a given endpoint.

## HTTP status codes

The API would use conventional HTTP status codes to signal the outcome of a
request: `2xx` indicates success, `4xx` a client error, and `5xx` a server error.
Whether every non-2xx response carries an `application/problem+json` body is part
of the proposed target contract described above and is **Not available / to be
confirmed**.

| Status | Meaning | Typical cause |
|--------|---------|---------------|
| `400 Bad Request` | The request was malformed. | Invalid or non-parseable JSON, a missing required field, or a wrongly typed parameter. |
| `401 Unauthorized` | The request is not authenticated. | A missing, invalid, or expired JWT bearer token — see [Authentication](authentication.md). |
| `403 Forbidden` | The caller is authenticated but not authorized. | The principal lacks the required role or permission — for example a non-`administrator` caller invoking Entity/metadata endpoints. |
| `404 Not Found` | The target resource does not exist. | An unknown Entity, a Record id that is not present, or a missing file. |
| `409 Conflict` | The request conflicts with the current server state. | A concurrency conflict (a stale update) or a uniqueness-constraint violation. |
| `422 Unprocessable Entity` | The request is syntactically valid but semantically invalid. | Field-level validation failed — for example a blank required value or a value that violates an entity rule. |
| `500 Internal Server Error` | An unhandled error occurred on the server. | An unexpected server-side fault; the response is a **generic** message (no stack trace), optionally with a correlation id. |

The `403 Forbidden` case is driven by the platform's role model — the Entity and
metadata operations gate on `SecurityContext.HasMetaPermission()`, which requires
the `administrator` role, so a caller without it is refused.

Source: /WebVella.Erp/Api/SecurityContext.cs:L26 (role name `administrator`), L109-L117 (`HasMetaPermission` → `AdministratorRoleId`); /WebVella.Erp/Api/EntityManager.cs:L452 (metadata operations gate on `HasMetaPermission`).

## Related pages

- [Authentication](authentication.md) — how bearer tokens are issued and
  validated, and the detail behind the `401` and `403` responses above.
- [API Reference overview](index.md) — the response envelope and the overall
  request/response model.
- [Observability](../architecture/observability.md) — correlation ids and how
  server-side errors are logged safely.
- The endpoint pages — [Records](records.md), [Entities & Metadata](entities.md),
  [EQL Query](eql.md), and [Files](files.md) — reference the status codes and the
  error model documented on this page.
