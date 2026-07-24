<!--{"sort_order":8, "name": "errors", "label": "Errors"}-->
# Errors

The `/api/v1/` surface reports transport- and HTTP-level errors using the standard
`application/problem+json` **problem-details** media type defined by
[RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) — the specification that
obsoletes [RFC 7807](https://www.rfc-editor.org/rfc/rfc7807). Successful results,
and the business/validation outcomes produced by the platform's in-process
managers, continue to use the platform **response envelope**
(`success`, `message`, `timestamp`, `errors`, `object`) described in the
[API Reference overview](index.md#response-envelope).

This page is the shared error reference for the API. Every endpoint page —
[Records](records.md), [Entities & Metadata](entities.md), [EQL Query](eql.md),
and [Files](files.md) — links here for the meaning of the status codes it can
return and the shape of a failed response.

Source: /docs/api-reference/index.md:L42 (response envelope), /docs/developer/web-api/response.md:L59 (envelope fields)

## Problem Details (application/problem+json)

When a request fails at the transport or HTTP layer, the API returns a
problem-details object and sets the `Content-Type` response header to
`application/problem+json` (in contrast to the `application/json` used for
successful envelope responses). The object carries the standard members defined
by RFC 9457:

| Member | Type | Description |
|--------|------|-------------|
| `type` | URI (string) | A URI reference identifying the problem *type*. When omitted it defaults to `about:blank`; when present, dereferencing it should yield human-readable documentation for that problem type. |
| `title` | string | A short, human-readable summary of the problem type. It stays constant across occurrences of the same type. |
| `status` | number | The HTTP status code produced by the origin server for this occurrence, duplicated in the body for convenience (for example `400`). |
| `detail` | string | A human-readable explanation specific to *this* occurrence of the problem. |
| `instance` | URI (string) | A URI reference identifying the specific occurrence (for example the request path that failed). |

Beyond the standard members, a problem-details response may carry **extension
members** that add machine-readable context. Validation failures use an `errors`
extension member — a map keyed by the offending field name, with an array of
human-readable messages per field. A `400` validation response therefore looks
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

## Relationship to the response envelope

The legacy Web API reported errors *inside* the response envelope rather than as
a distinct media type. Its envelope carried an `errors` field typed as
`List<ErrorModel>`, where each `ErrorModel` has three properties:

| Property | Meaning |
|----------|---------|
| `key` | The property name, if any, whose validation or execution returned an error. |
| `value` | The property value that caused the problem. |
| `message` | The human-readable message describing the error. |

Source: /docs/developer/web-api/response.md:L59

A legacy failure response therefore took the shape below — `success` set to
`false`, a top-level `message`, an ISO 8601 UTC `timestamp`, one or more entries
in `errors`, and the `object` payload:

```json
{
  "success": false,
  "message": "URL cannot be blank",
  "timestamp": "2014-03-03T23:20:23Z",
  "errors": [
    { "key": "url", "value": "", "message": "URL cannot be blank" }
  ],
  "object": { "id": 1 }
}
```

Source: /docs/developer/web-api/response.md:L39

### Mapping legacy errors to problem details

Consumers migrating from the legacy Web API can translate the two models as
follows:

| Legacy envelope | Target `/api/v1/` problem details |
|-----------------|-----------------------------------|
| `success: false` | A non-2xx HTTP status code plus an `application/problem+json` body. |
| `errors[].message` | A human-readable message in the problem-details `errors` extension, listed under the field named by `errors[].key`. |
| `errors[].key` | The field name used as the key in the problem-details `errors` map. |
| `errors[].value` | The offending value; conveyed through `detail` where useful (problem details keys validation by field, not by value). |
| `message` | The problem-details `title` (and/or `detail`). |

**Which model applies where.** Transport- and HTTP-level failures — malformed
requests, authentication and authorization failures, missing resources, and
unhandled server errors — are reported with a non-2xx status code and an
`application/problem+json` body. Successful results, and the business/validation
outcomes that the platform managers express through the envelope, continue to use
the response envelope with its `errors: List<ErrorModel>` field.

- **The exact boundary between envelope-reported validation errors and
  problem-details validation errors: Not available / to be confirmed.** Needed:
  the final decision from the `WebVella.Erp.Api` host on whether field-level
  validation failures are returned inside the response envelope (`success: false`
  with `errors[]`) or promoted to a `400`/`422` `application/problem+json`
  response using the `errors` extension member — and, if both are used, the
  precise rule that determines which one applies to a given endpoint.

## HTTP status codes

The API uses conventional HTTP status codes to signal the outcome of a request:
`2xx` indicates success (the body is a response envelope), `4xx` indicates a
client error, and `5xx` indicates a server error. Every non-2xx response carries
an `application/problem+json` body, as described under **Problem Details** above.

| Status | Meaning | Typical cause |
|--------|---------|---------------|
| `400 Bad Request` | The request was malformed. | Invalid or non-parseable JSON, a missing required field, or a wrongly typed parameter. |
| `401 Unauthorized` | The request is not authenticated. | A missing, invalid, or expired JWT bearer token — see [Authentication](authentication.md). |
| `403 Forbidden` | The caller is authenticated but not authorized. | The principal lacks the required role or permission — for example a non-`Administration` caller invoking Entity/metadata endpoints. |
| `404 Not Found` | The target resource does not exist. | An unknown Entity, a Record id that is not present, or a missing file. |
| `409 Conflict` | The request conflicts with the current server state. | A concurrency conflict (a stale update) or a uniqueness-constraint violation. |
| `422 Unprocessable Entity` | The request is syntactically valid but semantically invalid. | Field-level validation failed — for example a blank required value or a value that violates an entity rule. |
| `500 Internal Server Error` | An unhandled error occurred on the server. | An unexpected server-side fault; the problem-details `instance` identifies the failing occurrence. |

The `403 Forbidden` case is driven by the platform's role model — the Entity and
metadata operations require the `Administration` role, so a caller without it is
refused.

Source: /docs/developer/server-api/overview.md:L8 (Administration role required for Entity/metadata operations)

## Related pages

- [Authentication](authentication.md) — how bearer tokens are issued and
  validated, and the detail behind the `401` and `403` responses above.
- [API Reference overview](index.md) — the response envelope and the overall
  request/response model.
- The endpoint pages — [Records](records.md), [Entities & Metadata](entities.md),
  [EQL Query](eql.md), and [Files](files.md) — reference the status codes and the
  problem-details model documented on this page.
