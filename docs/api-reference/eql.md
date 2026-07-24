<!--{"sort_order":6, "name": "eql", "label": "EQL Query"}-->
# EQL Query

> **Planned target design — Not available in this checkout.** There is **no
> `WebVella.Erp.Api` project** and **no generated OpenAPI document** in
> `WebVella.ERP3.sln`, so the `/api/v1/eql` route, its HTTP method, request and
> response shapes, and every example on this page are **proposed design** and
> **Not available / to be confirmed** until the API host exists. The EQL engine
> itself is present in the core, but it is currently reached through the legacy
> `POST /api/v3/en_US/eql` route, not `/api/v1/eql`
> (`Source: /WebVella.Erp.Web/Controllers/WebApiController.cs:L63`). **The
> examples below are illustrative design sketches, not runnable**, and the JSON
> responses use the **legacy** in-process envelope; the target `/api/v1/`
> envelope is itself Not available.

**EQL (Entity Query Language)** is WebVella's SQL-like `SELECT` language for retrieving data from Entities. The `/api/v1/eql` endpoint executes a single EQL statement and returns the resulting **`EntityRecord` tree** — a list of records in which related records are nested — inside the platform's standard [response envelope](index.md#response-envelope). This page documents the HTTP endpoint only; the complete language reference (the `SELECT`, `WHERE`, `ORDER BY`, `PAGE`/`PAGESIZE` grammar, operators, and relation traversal) lives in the developer guide and is **not duplicated** here — see [EQL Syntax](../developer/data-sources/eql.md).

Source: /WebVella.Erp/Eql/EqlCommand.cs:L190 (EqlCommand.Execute returns EntityRecordList), /WebVella.Erp/Eql/EqlAbstractTree.cs:L43 (EqlSelectNode — SQL-like SELECT)

The engine parses EQL with the **Irony** grammar parser (`Irony.NetCore` 1.1.11) and implements the language in the core engine's `Eql/` folder; the parsed statement is translated to SQL, executed over PostgreSQL through Npgsql, and materialized back into the `EntityRecord` tree returned by this endpoint.

Source: /WebVella.Erp/WebVella.Erp.csproj:L50 (Irony.NetCore 1.1.11)

## Execute an EQL query

Runs an EQL `SELECT` statement and returns the matching records as a JSON tree in the response envelope. Because a query carries a statement (and, potentially, parameters) in its body, the endpoint is a **`POST`** rather than a `GET`.

### Authorization

This request requires a valid **OIDC-issued JWT** presented as a **bearer token** in the `Authorization` header (`Authorization: Bearer <access_token>`). Query results are further constrained by the caller's authorization: record access is governed by **per-Entity permissions**, so a query only returns records the mapped principal is permitted to read. See [Authentication](authentication.md) for how tokens are obtained, validated, and mapped to roles and permissions.

Source: /WebVella.Erp/Api/Models/Entity.cs:L80 (RecordPermissions — per-Entity read access)

### HTTP request

```http
POST https://<host>/api/v1/eql
Content-Type: application/json
Authorization: Bearer <access_token>
```

- **The exact route template and verb: Not available / to be confirmed.** Needed: the final route and HTTP method for the EQL endpoint as defined by the `WebVella.Erp.Api` host. The `/api/v1/` version prefix is authoritative; `POST` is documented here because queries carry a request body.

### Query parameters

No query-string parameters are required; the EQL statement is supplied in the request body.

### Request body

A JSON object carrying the EQL statement in an `eql` field:

```json
{
  "eql": "SELECT id, name FROM demo_customer"
}
```

The EQL language itself supports **parameterized** statements — parameters are referenced with an `@` prefix (for example `WHERE contact = @contact`) and are supplied by ERP data sources or the EQL API classes internally.

- **Whether this HTTP endpoint accepts a companion `parameters` map alongside the `eql` string, and its exact shape: Not available / to be confirmed.** Needed: the request-body contract defined by the `WebVella.Erp.Api` host — specifically whether named EQL parameters (`@name`) may be passed over HTTP and, if so, the property name and value encoding.

Source: /WebVella.Erp/Eql/EqlAbstractTree.cs:L43 (EqlSelectNode), L36 (EqlArgumentValueNode — @-prefixed parameters); /WebVella.Erp/Eql/EqlParameter.cs:L8 (EqlParameter)

### Request response

On success the response is the standard envelope; the `object` field holds the query result — a **list of `EntityRecord` objects** forming a tree, where each related record set appears as a nested array keyed by its relation name. The result is serialized to JSON and cannot be represented as a flat table. The `id` field is **always** included on every record even if it was not named in the `SELECT` list, because the engine always needs it and adds it automatically.

Source: /WebVella.Erp/Eql/EqlCommand.cs:L190 (Execute returns an EntityRecordList tree; id always included)

```json
{
  "timestamp": "2014-03-03T23:20:23Z",
  "success": true,
  "message": "",
  "hash": null,
  "errors": [],
  "accessWarnings": [],
  "object": [
    { "id": "9e1c2d3c-8ce4-4c8f-a651-e54421baa09c", "name": "Alfreds Futterkiste" },
    { "id": "fc1ca2ea-f3d6-4bf5-8853-bd4a44bf37f9", "name": "Alessandro Moratti" }
  ]
}
```

The envelope fields (`timestamp`, `success`, `message`, `hash`, `errors`, `accessWarnings`, `object`) are described in full in the [API Reference overview](index.md#response-envelope); this is the **legacy** envelope, and the target `/api/v1/` envelope is **Not available / to be confirmed**.

The request pipeline for an EQL query is:

```mermaid
sequenceDiagram
    participant C as Client
    participant API as API (WebVella.Erp.Api)
    participant Auth as Auth (JWT Bearer)
    participant P as EQL Parser (Irony)
    participant Mgr as Manager (RecordManager)
    participant DB as PostgreSQL
    C->>API: POST /api/v1/eql with Authorization Bearer token
    API->>Auth: Validate JWT bearer token
    Auth-->>API: Principal with per-Entity permissions
    API->>P: Parse EQL SELECT statement
    P-->>API: Validated query model
    API->>Mgr: Execute query (permissions enforced)
    Mgr->>DB: Translated SQL over an Npgsql transaction
    DB-->>Mgr: Result rows
    Mgr-->>API: EntityRecord tree
    API-->>C: JSON response envelope (object = record tree)
```

## Worked example

Select every customer together with its related addresses. EQL has **no SQL `JOIN` syntax**; related records are pulled in by referencing the relation name decorated with a `$` prefix (`$relation_name`), so `$customer_1n_address.*` nests each customer's `demo_address` records under a `$customer_1n_address` array.

Source: /WebVella.Erp/Eql/EqlAbstractTree.cs:L74 (EqlRelationFieldNode — $-prefixed relation traversal; no JOIN syntax)

### HTTP request

```bash
curl -X POST "https://<host>/api/v1/eql" \
  -H "Authorization: Bearer <access_token>" \
  -H "Content-Type: application/json" \
  -d '{ "eql": "SELECT *, $customer_1n_address.* FROM demo_customer" }'
```

### Request response

```json
{
  "timestamp": "2014-03-03T23:20:23Z",
  "success": true,
  "message": "",
  "hash": null,
  "errors": [],
  "accessWarnings": [],
  "object": [
    {
      "id": "9e1c2d3c-8ce4-4c8f-a651-e54421baa09c",
      "name": "Alfreds Futterkiste",
      "contact": "Maria Anders",
      "$customer_1n_address": [
        {
          "id": "2c104d0f-d40f-48ee-8964-33265176a70c",
          "customer_id": "9e1c2d3c-8ce4-4c8f-a651-e54421baa09c",
          "address": "Tauentzienstrasse 98",
          "city": "Berlin",
          "country": "Germany"
        },
        {
          "id": "65349ebd-f6ac-4612-a127-f32ebe4f23fd",
          "customer_id": "9e1c2d3c-8ce4-4c8f-a651-e54421baa09c",
          "address": "Anry Barbuys 12",
          "city": "Plovdiv",
          "country": "Bulgaria"
        }
      ]
    },
    {
      "id": "fc1ca2ea-f3d6-4bf5-8853-bd4a44bf37f9",
      "name": "Alessandro Moratti",
      "contact": "Alessandro Moratti",
      "$customer_1n_address": [
        {
          "id": "86344903-513b-4fb3-a14e-9931083c2bd6",
          "customer_id": "fc1ca2ea-f3d6-4bf5-8853-bd4a44bf37f9",
          "address": "Viale Cassala 1",
          "city": "Milano",
          "country": "Italy"
        }
      ]
    }
  ]
}
```

Each customer record carries a `$customer_1n_address` property whose value is the list of related `demo_address` records. Every record — parent and nested — includes its `id`.

## Relations and multi-hop traversal

- **Forward relation:** `$relation_name` pulls the records related to the current record. The join direction is inferred automatically from the Entity of the parent record.
- **Reversed relation:** for a self-referencing relation (an Entity related to itself) use a double dollar, `$$relation_name`, to switch the generated join direction.
- **Multi-hop:** relations chain, for example `$rel1.$rel2.*`, producing successively nested arrays in the result tree.

Only the language reference enumerates the full rules and limitations (for example, only first-level relations are allowed in a `WHERE` clause). See [EQL Syntax](../developer/data-sources/eql.md).

Source: /WebVella.Erp/Eql/EqlAbstractTree.cs:L74 (EqlRelationFieldNode — $ traversal), /WebVella.Erp/Eql/EqlRelationDirectionType.cs:L3 (TargetOrigin/OriginTarget — $$ reversal)

## Errors and side effects

**Side effects: none.** An EQL `SELECT` is **read-only** — it retrieves data and does not create, update, or delete records, so a query has no side effects on server state.

In the target design, failures would be reported with conventional HTTP status codes; the concrete error body (for example an `application/problem+json` shape) is **Not available / to be confirmed**. Today the in-process managers surface errors inside the legacy envelope's `errors[]`. See [Errors](errors.md):

| Status | Meaning | Typical cause for an EQL query |
|--------|---------|--------------------------------|
| `400 Bad Request` | The request was malformed. | The EQL statement failed to parse — a syntax error surfaced by the Irony grammar parser — or the request body was not valid JSON. |
| `401 Unauthorized` | The request is not authenticated. | A missing, invalid, or expired JWT bearer token — see [Authentication](authentication.md). |
| `403 Forbidden` | The caller is authenticated but not authorized. | The mapped principal lacks the per-Entity read permission required for the queried Entity. |
| `404 Not Found` | A referenced object does not exist. | The `FROM` Entity, a selected field, or a referenced relation name is unknown. |

Source: /WebVella.Erp/WebVella.Erp.csproj:L50 (Irony grammar parser used to parse EQL), /WebVella.Erp/Api/Models/Entity.cs:L80 (per-Entity permissions govern record access)

## Related pages

- [EQL Syntax](../developer/data-sources/eql.md) — the complete EQL language reference (`SELECT`, `WHERE`, `ORDER BY`, `PAGE`/`PAGESIZE`, operators, and relation traversal).
- [Records](records.md) — Record CRUD endpoints for reading and writing individual records.
- [Entities & Metadata](entities.md) — Entity and field metadata endpoints.
- [Authentication](authentication.md) — obtaining and presenting the JWT bearer token and how claims map to per-Entity permissions.
- [Errors](errors.md) — the full problem-details error model and the complete list of status codes.
