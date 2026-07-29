<!--{"sort_order":5, "name": "entities", "label": "Entities & Metadata"}-->
# Entities & Metadata

> **Planned target design — Not available in this checkout.** There is **no
> `WebVella.Erp.Api` project** and **no generated OpenAPI document** in
> `WebVella.ERP3.sln`, so every `/api/v1/meta/...` route, HTTP method, request or
> response DTO, status code, and example on this page is **proposed design** and
> **Not available / to be confirmed** until the API host and its OpenAPI document
> exist. The **current** controllers expose the legacy, locale-qualified
> `/api/v3/en_US/meta/...` routes, not `/api/v1/meta/...`
> (`Source: /WebVella.Erp.Web/Controllers/WebApiController.cs:L1473`). The DTO
> shapes shown reflect the **current in-process manager models** (`InputEntity`,
> `InputField`, `EntityRelation`); the target request/response DTOs are Not
> available. **The examples below are illustrative design sketches, not
> runnable.**

The metadata endpoints under `/api/v1/meta/` define and read the platform's
**Entities**, their **fields**, and the **relations** between them. In WebVella
terminology an **Entity** is a *content type* — a definition composed of a meta
record, a set of fields, and a set of relations to other entities — while a
**Record** is a single *instance* of an Entity. Record data (the instances) is
served by a separate surface; see [Records](records.md).

Source: /WebVella.Erp/Api/Models/Entity.cs:L38 (Entity model)

These endpoints are a thin REST transport in front of the platform's
**in-process managers**, which are unchanged by the headless refactor and remain
the authoritative implementation:

- **`EntityManager`** — entity meta and entity-field operations. **Requires the
  `administrator` role.** Source: /WebVella.Erp/Api/EntityManager.cs:L452 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)
- **`EntityRelationManager`** — entity-relation operations. **Requires the
  `administrator` role.** Source: /WebVella.Erp/Api/EntityRelationManager.cs:L399 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)

> **All metadata endpoints require the `administrator` role.** Every request on
> this page is authenticated with an OIDC-issued JWT bearer token whose mapped
> principal **must hold the `administrator` role**; a valid token without it is
> refused with `403 Forbidden`. See
> [Authentication → Claim to role and permission mapping](authentication.md#claim-to-role-and-permission-mapping)
> for how OIDC claims map to the `administrator` role.

In the target design, all requests would be made relative to the versioned base
path `/api/v1/` (see the [API Reference overview](index.md)). Today the
in-process managers return the **legacy** response envelope
(`timestamp`, `success`, `message`, `hash`, `errors`, `accessWarnings`, `object`)
documented in full under [Response Envelope](index.md#response-envelope); the
target `/api/v1/` envelope — and whether HTTP-level failures adopt an
`application/problem+json` shape — is **Not available / to be confirmed**. See
[Errors](errors.md).

Source: /WebVella.Erp/Api/Models/BaseModels.cs:L8-L38 (BaseResponseModel incl. hash, accessWarnings)

## Migrating from the legacy meta API

The legacy Web API exposed the metadata surface under a path that was both
version-qualified and locale-qualified (the retired paths are shown in the
**Legacy endpoint** column below). The headless surface drops the locale segment
and re-versions the routes under `/api/v1/meta/...`. Update any integration that
targets the legacy paths as follows:

| Legacy endpoint (retired) | Replacement (`/api/v1/`) | Source |
|---------------------------|--------------------------|--------|
| `POST /api/v3/en_US/meta/entity` | `POST /api/v1/meta/entity` | Source: /WebVella.Erp.Web/Controllers/WebApiController.cs:L1473 (legacy POST api/v3/en_US/meta/entity) |
| `POST /api/v3/en_US/meta/entity/{Id}/field` | `POST /api/v1/meta/entity/{id}/field` | Source: /WebVella.Erp.Web/Controllers/WebApiController.cs:L1593 (legacy POST .../meta/entity/{Id}/field) |
| `POST /api/v3/en_US/meta/relation` | `POST /api/v1/meta/relation` | Source: /WebVella.Erp.Web/Controllers/WebApiController.cs:L2036 (legacy POST api/v3/en_US/meta/relation) |

The authorization model also changes: the legacy endpoints authorized through a
browser session credential, whereas the `/api/v1/` surface is
**bearer-JWT-only**. See [Authentication](authentication.md) for the token model.

## Authentication and authorization

Every endpoint on this page shares the same authorization requirement, so it is
stated once here and repeated per endpoint for convenience:

- **Transport:** an OIDC-issued **JWT** presented as an HTTP `Authorization:
  Bearer <access_token>` header. There is no session-credential fallback on
  `/api/v1/`.
- **Required role:** the **`administrator`** role. Entity and field operations
  are enforced by `EntityManager`, and relation operations by
  `EntityRelationManager`; both require the `administrator` role.

Source: /WebVella.Erp/Api/EntityManager.cs:L452, /WebVella.Erp/Api/EntityRelationManager.cs:L399 (both gate on SecurityContext.HasMetaPermission); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117

A request that is unauthenticated is rejected with `401 Unauthorized`; a request
that is authenticated but whose principal is not mapped to the `administrator`
role is rejected with `403 Forbidden`. The full status-code catalog and the
problem-details body are documented in [Errors](errors.md).

## Entity metadata endpoints

Entity metadata endpoints define and read Entities and their fields. They are
backed by `EntityManager` and require the `administrator` role.
Source: /WebVella.Erp/Api/EntityManager.cs:L452 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)

### `GET /api/v1/meta/entity` — list entities

Returns the collection of Entity meta definitions known to the platform.

#### Authorization

Requires a JWT bearer token whose principal is mapped to the **`administrator`**
role. Backed by `EntityManager`, which requires the `administrator` role.
Source: /WebVella.Erp/Api/EntityManager.cs:L452 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)

#### HTTP request

```http
GET https://<host>/api/v1/meta/entity
Authorization: Bearer <access_token>
```

#### Query parameters

List results are paginated. The exact pagination parameter names and the default
page size are **Not available / to be confirmed** — needed: the final pagination
parameters (for example `page`/`pageSize`) exposed by the `WebVella.Erp.Api`
endpoint definitions once finalized. See [Pagination](index.md#pagination).

#### Request body

None. `GET` requests do not carry a request body.

#### Side effects

None. This endpoint is read-only and does not modify metadata or storage.

#### Request response

On success the response envelope's `object` is an **array** of Entity meta
objects. Each entry carries the entity meta (for example `id`, `name`, `label`,
`labelPlural`, `system`, `color`, `iconName`) and its collections of fields and
relations.

```json
{
  "success": true,
  "message": "",
  "timestamp": "2014-03-03T23:20:23Z",
  "errors": [],
  "object": [
    {
      "id": "0f6d1b8e-0000-0000-0000-000000000000",
      "name": "user",
      "label": "User",
      "labelPlural": "Users",
      "system": true,
      "color": "#f44336",
      "iconName": "ti-user"
    }
  ]
}
```

Source: /WebVella.Erp/Api/Models/BaseModels.cs:L8-L38 (envelope), /WebVella.Erp/Api/Models/Entity.cs:L101 (EntityListResponse : BaseResponseModel)

#### Error modes

| Status | Cause |
|--------|-------|
| `401 Unauthorized` | Missing, invalid, or expired JWT bearer token. |
| `403 Forbidden` | Authenticated principal is not in the `administrator` role. |

See [Errors](errors.md) for the full problem-details model.

#### Example

```bash
curl "https://<host>/api/v1/meta/entity" \
  -H "Authorization: Bearer <access_token>"
```

### `GET /api/v1/meta/entity/{name}` — read one entity

Returns a single Entity meta definition, identified by its unique `name`. This
maps to `EntityManager.ReadEntity`.
Source: /WebVella.Erp/Api/EntityManager.cs:L800 (ReadEntity(string name))

#### Authorization

Requires a JWT bearer token whose principal is mapped to the **`administrator`**
role. Backed by `EntityManager`, which requires the `administrator` role.
Source: /WebVella.Erp/Api/EntityManager.cs:L452 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)

#### HTTP request

```http
GET https://<host>/api/v1/meta/entity/{name}
Authorization: Bearer <access_token>
```

#### Query parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `name` | `string` | Yes | Path segment. The unique name of the target Entity (for example `user`). |

No query-string parameters are required with this method.

#### Request body

None. `GET` requests do not carry a request body.

#### Side effects

None. This endpoint is read-only and does not modify metadata or storage.

#### Request response

On success the response envelope's `object` is the single Entity meta object for
the requested `name`.

```json
{
  "success": true,
  "message": "",
  "timestamp": "2014-03-03T23:20:23Z",
  "errors": [],
  "object": {
    "id": "0f6d1b8e-0000-0000-0000-000000000000",
    "name": "user",
    "label": "User",
    "labelPlural": "Users",
    "system": true,
    "color": "#f44336",
    "iconName": "ti-user"
  }
}
```

Source: /WebVella.Erp/Api/Models/BaseModels.cs:L8-L38 (envelope), /WebVella.Erp/Api/Models/Entity.cs:L95 (EntityResponse : BaseResponseModel)

#### Error modes

| Status | Cause |
|--------|-------|
| `401 Unauthorized` | Missing, invalid, or expired JWT bearer token. |
| `403 Forbidden` | Authenticated principal is not in the `administrator` role. |
| `404 Not Found` | No Entity exists with the requested `name`. |

See [Errors](errors.md) for the full problem-details model.

#### Example

```bash
curl "https://<host>/api/v1/meta/entity/user" \
  -H "Authorization: Bearer <access_token>"
```

### `POST /api/v1/meta/entity` — create an entity

Creates a new Entity. This maps to `EntityManager.CreateEntity`.
Source: /WebVella.Erp/Api/EntityManager.cs:L452 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)

#### Authorization

Requires a JWT bearer token whose principal is mapped to the **`administrator`**
role. Backed by `EntityManager`, which requires the `administrator` role.
Source: /WebVella.Erp/Api/EntityManager.cs:L452 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)

#### HTTP request

```http
POST https://<host>/api/v1/meta/entity
Authorization: Bearer <access_token>
Content-Type: application/json
```

#### Query parameters

No query parameters are required with this method.

#### Request body

Post an **`InputEntity`** object as the request body. It carries the entity meta
to create — for example `name`, `label`, `labelPlural`, `system`, `color`, and
`iconName`.
Source: /WebVella.Erp/Api/Models/Entity.cs:L7 (InputEntity), /WebVella.Erp/Api/EntityManager.cs:L439 (CreateEntity(InputEntity))

```json
{
  "name": "offer",
  "label": "Offer",
  "labelPlural": "Offers",
  "system": false,
  "color": "#f44336",
  "iconName": "ti-user"
}
```

#### Side effects

Creating an Entity persists its meta **and** provisions its backing storage: the
platform creates and maintains the optimal database structure for the new Entity
(a DDL operation). This is a write operation and is not idempotent.
Source: /WebVella.Erp/Api/EntityManager.cs:L439 (CreateEntity provisions entity storage)

#### Request response

On success the response envelope's `object` is the newly created Entity.
Source: /WebVella.Erp/Api/Models/Entity.cs:L95 (EntityResponse), /WebVella.Erp/Api/EntityManager.cs:L439 (CreateEntity)

```json
{
  "timestamp": "2014-03-03T23:20:23Z",
  "success": true,
  "message": "",
  "errors": [],
  "object": {
    "id": "6b2f9c30-0000-0000-0000-000000000000",
    "name": "offer",
    "label": "Offer",
    "labelPlural": "Offers",
    "system": false,
    "color": "#f44336",
    "iconName": "ti-user"
  }
}
```

#### Error modes

| Status | Cause |
|--------|-------|
| `400 Bad Request` | The request body is missing or is not valid JSON. |
| `401 Unauthorized` | Missing, invalid, or expired JWT bearer token. |
| `403 Forbidden` | Authenticated principal is not in the `administrator` role. |
| `422 Unprocessable Entity` | The `InputEntity` is syntactically valid but fails validation (for example a duplicate or blank `name`). Field-level messages are returned per [Errors](errors.md). |

See [Errors](errors.md) for the full problem-details model.

#### Example

```bash
curl -X POST "https://<host>/api/v1/meta/entity" \
  -H "Authorization: Bearer <access_token>" \
  -H "Content-Type: application/json" \
  -d '{
        "name": "offer",
        "label": "Offer",
        "labelPlural": "Offers",
        "system": false,
        "color": "#f44336",
        "iconName": "ti-user"
      }'
```

### `POST /api/v1/meta/entity/{id}/field` — create a field

Adds a new field to an existing Entity. This maps to `EntityManager.CreateField`.
Source: /WebVella.Erp/Api/EntityManager.cs:L452 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)

#### Authorization

Requires a JWT bearer token whose principal is mapped to the **`administrator`**
role. Backed by `EntityManager`, which requires the `administrator` role.
Source: /WebVella.Erp/Api/EntityManager.cs:L452 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)

#### HTTP request

```http
POST https://<host>/api/v1/meta/entity/{id}/field
Authorization: Bearer <access_token>
Content-Type: application/json
```

Source: /WebVella.Erp.Web/Controllers/WebApiController.cs:L1593 (legacy field route); /WebVella.Erp/Api/EntityManager.cs:L924 (CreateField)

#### Query parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `id` | `Guid` | Yes | Path segment. The id of the target Entity to which the field is added. |

Source: /WebVella.Erp/Api/EntityManager.cs:L924 (CreateField(Guid entityId, InputField inputField))

#### Request body

Post an **`InputField`** object as the request body — the field definition
(name, label, field type, and type-specific options).
Source: /WebVella.Erp/Api/Models/FieldTypes/BaseField.cs:L10 (InputField), /WebVella.Erp/Api/EntityManager.cs:L924 (CreateField)

```json
{
  "name": "amount",
  "label": "Amount",
  "fieldType": "currency",
  "required": true
}
```

#### Side effects

Creating a field extends the target Entity's meta **and** its backing storage:
the platform adds the corresponding column to the Entity's database structure (a
DDL operation). This is a write operation and is not idempotent.
Source: /WebVella.Erp/Api/EntityManager.cs:L924 (CreateField provisions field storage)

#### Request response

On success the response envelope's `object` is the newly created field (the
persisted field definition).
Source: /WebVella.Erp/Api/Models/FieldTypes/BaseField.cs:L428 (FieldResponse), /WebVella.Erp/Api/EntityManager.cs:L924 (CreateField)

```json
{
  "timestamp": "2014-03-03T23:20:23Z",
  "success": true,
  "message": "",
  "errors": [],
  "object": {
    "id": "9a1c4d20-0000-0000-0000-000000000000",
    "name": "amount",
    "label": "Amount",
    "fieldType": "currency",
    "required": true
  }
}
```

#### Error modes

| Status | Cause |
|--------|-------|
| `400 Bad Request` | The request body is missing or is not valid JSON. |
| `401 Unauthorized` | Missing, invalid, or expired JWT bearer token. |
| `403 Forbidden` | Authenticated principal is not in the `administrator` role. |
| `404 Not Found` | No Entity exists with the supplied `{id}`. |
| `422 Unprocessable Entity` | The `InputField` is syntactically valid but fails validation (for example a duplicate or blank field `name`). Field-level messages are returned per [Errors](errors.md). |

See [Errors](errors.md) for the full problem-details model.

#### Example

```bash
curl -X POST "https://<host>/api/v1/meta/entity/6b2f9c30-0000-0000-0000-000000000000/field" \
  -H "Authorization: Bearer <access_token>" \
  -H "Content-Type: application/json" \
  -d '{
        "name": "amount",
        "label": "Amount",
        "fieldType": "currency",
        "required": true
      }'
```

## Entity relation endpoints

Entity relation endpoints define the relations between Entities. They are backed
by `EntityRelationManager` and require the `administrator` role.
Source: /WebVella.Erp/Api/EntityRelationManager.cs:L399 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)

### `POST /api/v1/meta/relation` — create a relation

Creates a new relation between two Entities. This maps to
`EntityRelationManager.Create`.
Source: /WebVella.Erp/Api/EntityRelationManager.cs:L399 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)

#### Authorization

Requires a JWT bearer token whose principal is mapped to the **`administrator`**
role. Backed by `EntityRelationManager`, which requires the `administrator`
role.
Source: /WebVella.Erp/Api/EntityRelationManager.cs:L399 (HasMetaPermission gate); /WebVella.Erp/Api/SecurityContext.cs:L26, L109-L117 (administrator role)

#### HTTP request

```http
POST https://<host>/api/v1/meta/relation
Authorization: Bearer <access_token>
Content-Type: application/json
```

#### Query parameters

No query parameters are required with this method.

#### Request body

Post an **`EntityRelation`** object as the request body — the relation
definition, including its name, type, and the origin/target entity and field
references.
Source: /WebVella.Erp/Api/Models/EntityRelation.cs:L36 (EntityRelation), /WebVella.Erp/Api/EntityRelationManager.cs:L388 (Create)

```json
{
  "name": "user_offer",
  "relationType": "one_to_many",
  "originEntityId": "0f6d1b8e-0000-0000-0000-000000000000",
  "originFieldId": "11111111-0000-0000-0000-000000000000",
  "targetEntityId": "6b2f9c30-0000-0000-0000-000000000000",
  "targetFieldId": "22222222-0000-0000-0000-000000000000"
}
```

#### Side effects

Creating a relation persists the relation definition between the two Entities so
that it can be traversed (for example in EQL queries; see [EQL Query](eql.md)).
This is a write operation and is not idempotent.

#### Request response

On success the response envelope's `object` is the newly created relation.
Source: /WebVella.Erp/Api/Models/EntityRelation.cs:L103 (EntityRelationResponse), /WebVella.Erp/Api/EntityRelationManager.cs:L388 (Create)

```json
{
  "timestamp": "2014-03-03T23:20:23Z",
  "success": true,
  "message": "",
  "errors": [],
  "object": {
    "id": "c3d4e5f6-0000-0000-0000-000000000000",
    "name": "user_offer",
    "relationType": "one_to_many",
    "originEntityId": "0f6d1b8e-0000-0000-0000-000000000000",
    "originFieldId": "11111111-0000-0000-0000-000000000000",
    "targetEntityId": "6b2f9c30-0000-0000-0000-000000000000",
    "targetFieldId": "22222222-0000-0000-0000-000000000000"
  }
}
```

#### Error modes

| Status | Cause |
|--------|-------|
| `400 Bad Request` | The request body is missing or is not valid JSON. |
| `401 Unauthorized` | Missing, invalid, or expired JWT bearer token. |
| `403 Forbidden` | Authenticated principal is not in the `administrator` role. |
| `404 Not Found` | A referenced origin/target Entity or field does not exist. |
| `422 Unprocessable Entity` | The `EntityRelation` is syntactically valid but fails validation (for example a duplicate relation `name` or an incompatible relation type). Field-level messages are returned per [Errors](errors.md). |

See [Errors](errors.md) for the full problem-details model.

#### Example

```bash
curl -X POST "https://<host>/api/v1/meta/relation" \
  -H "Authorization: Bearer <access_token>" \
  -H "Content-Type: application/json" \
  -d '{
        "name": "user_offer",
        "relationType": "one_to_many",
        "originEntityId": "0f6d1b8e-0000-0000-0000-000000000000",
        "originFieldId": "11111111-0000-0000-0000-000000000000",
        "targetEntityId": "6b2f9c30-0000-0000-0000-000000000000",
        "targetFieldId": "22222222-0000-0000-0000-000000000000"
      }'
```

## Related pages

- [Authentication](authentication.md) — the JWT bearer model and the
  [claim-to-role/permission mapping](authentication.md#claim-to-role-and-permission-mapping)
  that grants the `administrator` role these endpoints require.
- [Errors](errors.md) — the problem-details error model and the full list of
  status codes (`400`, `401`, `403`, `404`, `422`, …) referenced above.
- [Records](records.md) — the endpoints for the Record *instances* of the
  Entities defined here.
- [EQL Query](eql.md) — how Entities, fields, and relations are queried; the
  relations created here are traversed in EQL.
- [API Reference overview](index.md) — base URL, versioning, and the response
  envelope.

**Developer entity guides (in-process managers):**

- [Entities → Overview](../developer/entities/overview.md) — the entity model
  (meta, fields, relations) in depth.
- [Entities → Create entity](../developer/entities/create-entity.md) — the
  server-side `EntityManager` API and the web interface for creating entities.
- [Server API → Overview](../developer/server-api/overview.md) — the in-process
  `EntityManager` and `EntityRelationManager` that back these endpoints.
