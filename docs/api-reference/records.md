<!--{"sort_order":4, "name": "records", "label": "Records"}-->
# Records

A **Record** is a single instance of an [Entity](../developer/entities/create-entity.md) — one row of the Entity's field schema, analogous to a row in a database table. The endpoints on this page provide create, read, update, and delete (CRUD) access to Records over HTTP under the versioned base path `/api/v1/`.

These endpoints are a thin transport layer in front of the platform's in-process `RecordManager`, which performs the actual record operations; the REST host does not reimplement record logic. For example, a create ultimately delegates to `new RecordManager().CreateRecord("offer", PostObject)`.

Source: /docs/developer/server-api/overview.md:L22-L27

This page is the human-readable companion to the auto-generated [OpenAPI document](openapi.md). For querying and filtering Records with the Entity Query Language, see [EQL Query](eql.md).

## Conventions

All Record endpoints share the conventions defined in the [API Reference overview](index.md):

- **Base URL.** Requests are made relative to `https://<host>/api/v1/`. Source: /docs/api-reference/index.md:L8-L14
- **Content type.** Request and response bodies are `application/json` encoded as UTF-8; all timestamps are ISO 8601 strings in the UTC time zone. Source: /docs/api-reference/index.md:L30-L34
- **Response envelope.** Every response is wrapped in the platform's standard envelope — `{ success, message, timestamp, errors, object }` — where `object` carries the payload (a single Record, or an array of Records for list responses). Source: /docs/developer/web-api/response.md
- **Errors.** Transport- and HTTP-level failures use the `application/problem+json` problem-details model; see [Errors](errors.md) for the full status-code catalog. Source: /docs/api-reference/errors.md

### Response envelope

For reference, the envelope returned by every endpoint on this page has the following shape:

```json
{
  "success": true,
  "message": "",
  "timestamp": "2014-03-03T23:20:23Z",
  "errors": [],
  "object": {}
}
```

| Field | Type | Description |
|-------|------|-------------|
| `success` | `bool` | Whether the operation completed successfully. |
| `message` | `string` | Human-readable result message, often surfaced to the end user. |
| `timestamp` | `DateTime` | When the operation executed, as an ISO 8601 string in the UTC time zone. |
| `errors` | `List<ErrorModel>` | Validation or execution errors; empty on success. Each entry is `{ key, value, message }`. |
| `object` | `object` | The payload — a single Record, or an array of Records for list responses. |

Source: /docs/developer/web-api/response.md:L59-L96

## Authorization and permissions

Every endpoint on this page requires a valid **OIDC-issued JSON Web Token (JWT)** presented as a bearer token in the `Authorization` header (`Authorization: Bearer <token>`); the headless surface is bearer-JWT-only. See [Authentication](authentication.md) for how tokens are obtained and validated.

Record access is **not** gated by a single fixed role. Instead, **access depends on the permissions configured on the corresponding Entity** — a caller may, for example, be permitted to read Records of one Entity while being denied create access on another, according to that Entity's permission configuration. Each endpoint below restates the specific permission it requires.

Source: /docs/developer/server-api/overview.md:L24

## Route templates

> **Not available / to be confirmed.** The exact route templates are derived from the `WebVella.Erp.Api` endpoint definitions and are not yet finalized — for example, whether a Record collection is addressed as `/api/v1/record/{entityName}` or `/api/v1/{entityName}/records`. This page uses the `/api/v1/record/{entityName}` form; the `/api/v1/` prefix and the CRUD semantics are authoritative, while the precise segment layout will be confirmed against the endpoint definitions. Needed: the finalized route templates from `WebVella.Erp.Api`.

In every template below, `{entityName}` is the target Entity's name (for example `task`) and `{recordId}` is the target Record's identifier.

## List records

Returns a page of Records that belong to the named Entity. The response `object` is an **array** of Records.

##### Authorization

A bearer token is required. The caller must hold **read permission on the target Entity**; the permission is evaluated against `{entityName}` rather than a single global role. Source: /docs/developer/server-api/overview.md:L24

##### HTTP request

```http
GET https://<host>/api/v1/record/{entityName}
Authorization: Bearer <token>
```

##### Query parameters

List responses are paginated. The [overview](index.md#pagination) describes pagination in general terms; the concrete parameter names and default page size are not yet finalized.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `page` | integer | No | 1-based index of the page to return. **Exact name Not available / to be confirmed** (`page` vs `skip`/`take`). |
| `pageSize` | integer | No | Maximum number of Records per page. **Exact name and default page size Not available / to be confirmed.** |
| *filter* | string | No | Optional server-side filter over Record fields. **Availability and syntax Not available / to be confirmed;** for expressive querying use [EQL](eql.md). |

Source: /docs/api-reference/index.md:L36-L40

##### Side effects

None. Listing Records is a read-only operation and does not modify server state.

##### Error modes

| Status | Cause |
|--------|-------|
| `401 Unauthorized` | Missing, invalid, or expired bearer token. |
| `403 Forbidden` | The caller lacks read permission on the Entity. |
| `404 Not Found` | The named Entity (`{entityName}`) does not exist. |

See [Errors](errors.md#http-status-codes) for the full status-code catalog.

##### Request response

If successful, returns the standard envelope with `object` set to an **array** of Records:

```json
{
  "success": true,
  "message": "",
  "timestamp": "2014-03-03T23:20:23Z",
  "errors": [],
  "object": [
    {
      "id": "1f9d0c2e-3b4a-4c5d-9e6f-000000000001",
      "subject": "Prepare quote",
      "is_completed": false
    },
    {
      "id": "2a7c8b1d-5e6f-4a3b-8c9d-000000000002",
      "subject": "Send invoice",
      "is_completed": true
    }
  ]
}
```

The fields inside each Record object are defined by the target Entity's schema. Source: /docs/developer/web-api/response.md:L20-L37

##### Example

```bash
curl -X GET "https://<host>/api/v1/record/task?page=1&pageSize=25" \
  -H "Authorization: Bearer <token>" \
  -H "Accept: application/json"
```

## Get a single record

Returns one Record of the named Entity by its identifier. The response `object` is a **single** Record.

##### Authorization

A bearer token is required. The caller must hold **read permission on the target Entity**. Source: /docs/developer/server-api/overview.md:L24

##### HTTP request

```http
GET https://<host>/api/v1/record/{entityName}/{recordId}
Authorization: Bearer <token>
```

##### Query parameters

No query parameters are required with this method.

##### Side effects

None. Reading a Record is a read-only operation and does not modify server state.

##### Error modes

| Status | Cause |
|--------|-------|
| `401 Unauthorized` | Missing, invalid, or expired bearer token. |
| `403 Forbidden` | The caller lacks read permission on the Entity. |
| `404 Not Found` | The Entity does not exist, or no Record with `{recordId}` exists. |

##### Request response

If successful, returns the standard envelope with `object` set to a **single** Record:

```json
{
  "success": true,
  "message": "",
  "timestamp": "2014-03-03T23:20:23Z",
  "errors": [],
  "object": {
    "id": "1f9d0c2e-3b4a-4c5d-9e6f-000000000001",
    "subject": "Prepare quote",
    "is_completed": false
  }
}
```

Source: /docs/developer/web-api/response.md:L6-L18

##### Example

```bash
curl -X GET "https://<host>/api/v1/record/task/1f9d0c2e-3b4a-4c5d-9e6f-000000000001" \
  -H "Authorization: Bearer <token>" \
  -H "Accept: application/json"
```

## Create a record

Creates a new Record of the named Entity from a JSON body of field values. This endpoint is backed by `RecordManager.CreateRecord`. Source: /docs/developer/server-api/overview.md:L27

##### Authorization

A bearer token is required. The caller must hold **create permission on the target Entity**. Source: /docs/developer/server-api/overview.md:L24

##### HTTP request

```http
POST https://<host>/api/v1/record/{entityName}
Authorization: Bearer <token>
Content-Type: application/json
```

##### Query parameters

No query parameters are required with this method.

##### Request body

A JSON object mapping each Entity **field name** to its **value**. Which fields are accepted, which are required, and their types are defined by the target Entity's schema.

```json
{
  "subject": "Prepare quote",
  "is_completed": false,
  "priority": "high"
}
```

##### Side effects

Inserts a new Record into the database. This is a **write** operation: it persists a new row and may trigger any create **hooks** registered for the Entity. On success, the newly created Record — including any server-assigned fields such as its `id` — is returned in `object`.

##### Error modes

| Status | Cause |
|--------|-------|
| `400 Bad Request` | Malformed or non-parseable JSON body. |
| `401 Unauthorized` | Missing, invalid, or expired bearer token. |
| `403 Forbidden` | The caller lacks create permission on the Entity. |
| `422 Unprocessable Entity` | Field-level validation failed (for example a blank required field). |

##### Request response

On success, returns the envelope with the created Record in `object`. On a validation failure the platform returns `success: false` with one entry per offending field in `errors[]`:

```json
{
  "success": false,
  "message": "URL cannot be blank",
  "timestamp": "2014-03-03T23:20:23Z",
  "errors": [
    {
      "key": "url",
      "value": "",
      "message": "URL cannot be blank"
    }
  ],
  "object": {}
}
```

Whether field-level validation is reported inside the envelope (as above) or promoted to a `422`/`400` `application/problem+json` response is documented in [Errors](errors.md#relationship-to-the-response-envelope). Source: /docs/developer/web-api/response.md:L39-L57

##### Example

```bash
curl -X POST "https://<host>/api/v1/record/task" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"subject":"Prepare quote","is_completed":false,"priority":"high"}'
```

## Update a record

Updates an existing Record identified by `{recordId}`. Use **`PUT`** for a **full** update (replace all writable fields) and **`PATCH`** for a **partial** update (modify only the supplied fields).

##### Authorization

A bearer token is required. The caller must hold **update permission on the target Entity**. Source: /docs/developer/server-api/overview.md:L24

##### HTTP request

```http
PUT   https://<host>/api/v1/record/{entityName}/{recordId}
PATCH https://<host>/api/v1/record/{entityName}/{recordId}
Authorization: Bearer <token>
Content-Type: application/json
```

##### Query parameters

No query parameters are required with this method.

##### Request body

A JSON object of field values. For `PUT`, supply the full set of writable fields; for `PATCH`, supply only the fields to change.

```json
{
  "is_completed": true
}
```

##### Side effects

Updates the stored Record in the database. This is a **write** operation and may trigger any update **hooks** registered for the Entity.

##### Error modes

| Status | Cause |
|--------|-------|
| `400 Bad Request` | Malformed or non-parseable JSON body. |
| `401 Unauthorized` | Missing, invalid, or expired bearer token. |
| `403 Forbidden` | The caller lacks update permission on the Entity. |
| `404 Not Found` | The Entity does not exist, or no Record with `{recordId}` exists. |
| `409 Conflict` | A concurrency conflict (a stale update) or a uniqueness-constraint violation. |
| `422 Unprocessable Entity` | Field-level validation failed. |

##### Request response

On success, returns the envelope with the updated Record in `object`. A validation failure follows the same `success: false` + `errors[]` shape shown under [Create a record](#create-a-record):

```json
{
  "success": true,
  "message": "",
  "timestamp": "2014-03-03T23:20:23Z",
  "errors": [],
  "object": {
    "id": "1f9d0c2e-3b4a-4c5d-9e6f-000000000001",
    "subject": "Prepare quote",
    "is_completed": true
  }
}
```

Source: /docs/developer/web-api/response.md:L39-L57

##### Example

```bash
curl -X PATCH "https://<host>/api/v1/record/task/1f9d0c2e-3b4a-4c5d-9e6f-000000000001" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"is_completed":true}'
```

## Delete a record

Deletes the Record identified by `{recordId}` from the named Entity.

##### Authorization

A bearer token is required. The caller must hold **delete permission on the target Entity**. Source: /docs/developer/server-api/overview.md:L24

##### HTTP request

```http
DELETE https://<host>/api/v1/record/{entityName}/{recordId}
Authorization: Bearer <token>
```

##### Query parameters

No query parameters are required with this method.

##### Side effects

Deletes the Record from the database. This is a **write** operation and may trigger any delete **hooks** registered for the Entity. Deletion **may cascade** to related Records according to the Entity relations configured for `{entityName}`.

- **The exact cascade behavior: Not available / to be confirmed.** Needed: the relation-driven delete/cascade rules enforced by the `WebVella.Erp.Api` host and the in-process managers for the target Entity.

##### Error modes

| Status | Cause |
|--------|-------|
| `401 Unauthorized` | Missing, invalid, or expired bearer token. |
| `403 Forbidden` | The caller lacks delete permission on the Entity. |
| `404 Not Found` | The Entity does not exist, or no Record with `{recordId}` exists. |

##### Request response

On success, returns the standard envelope. The deleted Record is typically returned in `object`:

```json
{
  "success": true,
  "message": "",
  "timestamp": "2014-03-03T23:20:23Z",
  "errors": [],
  "object": {
    "id": "1f9d0c2e-3b4a-4c5d-9e6f-000000000001"
  }
}
```

Source: /docs/developer/web-api/response.md:L39-L57

##### Example

```bash
curl -X DELETE "https://<host>/api/v1/record/task/1f9d0c2e-3b4a-4c5d-9e6f-000000000001" \
  -H "Authorization: Bearer <token>"
```

## Related pages

- [Authentication](authentication.md) — obtaining and presenting the bearer token that every endpoint above requires.
- [Errors](errors.md) — the full problem-details error model and the complete HTTP status-code catalog behind the error modes above.
- [EQL Query](eql.md) — querying and filtering Records with the Entity Query Language.
- [API Reference overview](index.md) — base URL, versioning, pagination, and the response envelope.
- [Server API — RecordManager](../developer/server-api/overview.md) — the in-process manager that backs these endpoints. Record access depends on the permissions configured on the corresponding Entity rather than on a single global role.

Source: /docs/developer/server-api/overview.md:L22-L27

