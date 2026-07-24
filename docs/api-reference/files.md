<!--{"sort_order":7, "name": "files", "label": "Files"}-->
# Files

> **Planned target design — Not available in this checkout.** There is **no
> `WebVella.Erp.Api` project** and **no generated OpenAPI document** in
> `WebVella.ERP3.sln`, so the file routes, HTTP methods, request/response DTOs,
> and examples on this page are **proposed design** and **Not available / to be
> confirmed** until the API host exists. **The examples below are illustrative
> design sketches, not runnable.**
>
> **Security requirement (Blocker).** The target file endpoints **must not**
> accept or return client-controlled filesystem paths. A download **must** be
> addressed by an **opaque file identifier** (the stored file's `Id`), which the
> server resolves to an **ID-derived** physical path; the client never supplies a
> path segment that is used to locate a blob. See
> [Security requirements](#security-requirements-target) below — these controls
> are **acceptance criteria for the endpoint**, not optional hardening. The
> current in-process `DbFileRepository` already derives the physical path from the
> file's `Id` (a Guid, sharded by its prefix) and looks metadata up with a
> parameterized query; it is a data-access class, **not** an HTTP endpoint, and is
> **not** evidence that a path-based HTTP contract is safe.
> Source: /WebVella.Erp/Database/DbFileRepository.cs:L476 (`GetFileSystemPath` derives the physical path from `file.Id`), L495 (`GetBlobPath`), L34-L47 (`Find` uses a parameterized `@filepath` query).

In the target design, the `/api/v1/` surface would let you **upload** binary files
as `multipart/form-data` and **download** them again by their **opaque file
identifier**. File contents are not held in the relational database; they are
persisted through the platform's storage abstraction, **`Storage.Net`**, which can
be backed by the local file system or another provider depending on configuration
(see [Limits and configuration](#limits-and-configuration)).

Source: /WebVella.Erp/WebVella.Erp.csproj:L62 (Storage.Net 9.3.0)

Every protected request on this page would use an OIDC-issued JWT presented as a
bearer token, exactly as described in [Authentication](authentication.md). The
HTTP status codes returned on failure would follow the shared error model
documented in [Errors](errors.md).

## Security requirements (target)

These requirements are **mandatory** for the file endpoints and are stated up
front because they shape the contract on the rest of this page. They are
**acceptance criteria**, and each is currently **Not available / to be confirmed**
against the (non-existent) `WebVella.Erp.Api` host:

- **Opaque identifiers only.** Downloads are addressed by an opaque file `Id`
  (the stored file's Guid), never by a filesystem path or storage key supplied by
  the client. Uploads return that `Id`; they do **not** echo a raw storage path.
- **No client-controlled destination path.** The upload endpoint assigns the
  storage key **server-side** (ID-derived); it must **reject** any client-supplied
  destination path. The current store derives the physical path from `file.Id`
  (`Source: /WebVella.Erp/Database/DbFileRepository.cs:L476`).
- **Canonicalization and traversal rejection.** If any request value is ever used
  to build a storage path, it must be canonicalized and validated against an
  allowlist; inputs containing `..`, absolute paths, drive letters, or
  separators/encoded separators must be rejected. Path-traversal attempts must
  never resolve outside the configured storage root.
- **Object-level authorization.** Authentication is not sufficient: the caller
  must be authorized for **that specific file** (ownership or an explicit
  permission grant), checked on every download — not merely holding a valid token.
- **Upload validation.** Enforce a maximum body/file size (→ `413`) and validate
  the content type against an **allowlist** (→ `415`); consider malware scanning
  before a stored file is made retrievable.
- **Safe response headers.** On download, set a correct, non-sniffable
  `Content-Type` with `X-Content-Type-Options: nosniff`, and a
  `Content-Disposition` whose filename is **sanitized** (no CR/LF, no path
  separators) to prevent header injection and content-type confusion.
- **Path-traversal tests.** The endpoint's acceptance suite must include explicit
  traversal/canonicalization test cases (for example `..%2F`, absolute paths,
  encoded separators) that assert a rejection, not a file read.

## Upload a file

Would upload a single binary file and store it through the configured
`Storage.Net` provider. The request is sent as `multipart/form-data`, and the
response would be the standard JSON [response envelope](index.md#response-envelope)
whose `object` carries the **opaque identifier** of the stored file.

### Authorization

Requires a valid bearer token. Present the OIDC-issued JWT in the `Authorization`
header using the `Bearer` scheme; see [Authentication](authentication.md).

### HTTP request

```http
POST https://<host>/api/v1/file HTTP/1.1
Content-Type: multipart/form-data; boundary=<boundary>
Authorization: Bearer <access_token>
```

### Request body

The body is a `multipart/form-data` payload. It must include a binary file part
and may include optional metadata parts. The storage key is **assigned by the
server** (ID-derived); a client-supplied destination path is **not** accepted (see
[Security requirements](#security-requirements-target)):

| Part | Required | Type | Description |
|------|----------|------|-------------|
| `file` | Yes | binary | The file content to store, sent as a file part with a filename and its own `Content-Type`. |
| metadata parts | No | text | Additional form fields (for example a display name) captured alongside the file. |

- **The exact set of optional form parts and their names: Not available / to be
  confirmed.** Needed: the multipart field names accepted by the
  `WebVella.Erp.Api` file-upload endpoint beyond the required `file` part.

### Request response

On success the API would return the standard response envelope; the `object` field
holds the **opaque identifier** of the stored file (not a raw storage path):

```json
{
  "timestamp": "2014-03-03T23:20:23Z",
  "success": true,
  "message": "",
  "hash": null,
  "errors": [],
  "accessWarnings": [],
  "object": {
    "id": "<opaque-file-id>"
  }
}
```

Source: /WebVella.Erp/Api/Models/BaseModels.cs:L8-L38 (BaseResponseModel incl. hash, accessWarnings).

- **The exact fields of the stored-file descriptor: Not available / to be
  confirmed.** Needed: the response DTO returned by the `WebVella.Erp.Api`
  file-upload endpoint — for example whether it exposes the file `id`, a content
  type, and a size. It must **not** expose a filesystem path or storage key.

### Side effects

Would write a blob to the configured storage provider (`Storage.Net`). The upload
itself does not create a relational record; associating a stored file with a file
field on a record is done through the record endpoints (see [Records](records.md)).

### Error modes

| Status | Meaning |
|--------|---------|
| `400 Bad Request` | No file part was supplied, or the multipart payload was malformed. |
| `401 Unauthorized` | The bearer token is missing, invalid, or expired — see [Authentication](authentication.md). |
| `403 Forbidden` | The caller is authenticated but not permitted to upload. |
| `413 Payload Too Large` | The uploaded file exceeds the server's maximum accepted size. |
| `415 Unsupported Media Type` | The file's content type is not on the accepted allowlist. |

- **The maximum upload size (governing `413`) and the allowlist of accepted
  content types (governing `415`): Not available / to be confirmed.** Needed: the
  upload options defined by the `WebVella.Erp.Api` host — the maximum multipart
  body/file size and the list of permitted content types.

See [Errors](errors.md) for the full error model behind these status codes.

### Example

```bash
curl -X POST "https://<host>/api/v1/file" \
  -H "Authorization: Bearer <access_token>" \
  -F "file=@/path/to/local.pdf"
```

## Download a file

Would stream the raw bytes of a previously stored file, identified by its **opaque
file `Id`** (never a filesystem path). Unlike every other endpoint in this
reference, a download does **not** return the JSON response envelope — the body is
the file content itself.

### Authorization

Requires a valid bearer token **and** an object-level authorization check: the
caller must be permitted to read **that specific file**, not merely be
authenticated (see [Security requirements](#security-requirements-target)). Present
the JWT in the `Authorization` header as described in
[Authentication](authentication.md).

- **Whether any files are served publicly (without a bearer token): Not
  available / to be confirmed.** Needed: confirmation from the `WebVella.Erp.Api`
  host of whether specific public assets are exposed anonymously, and under which
  path, if any.

### HTTP request

```http
GET https://<host>/api/v1/file/{id} HTTP/1.1
Authorization: Bearer <access_token>
```

`{id}` is the **opaque identifier** of the file (the stored file's `Id`), as
returned in the `object` descriptor of the [upload response](#upload-a-file). It
is **not** a filesystem path: the server resolves the `Id` to an **ID-derived**
physical location and never treats `{id}` as a path.
Source: /WebVella.Erp/Database/DbFileRepository.cs:L476 (physical path derived from `file.Id`).

### Request response

The response body would be the **raw file bytes**, streamed with a correct,
non-sniffable `Content-Type` (accompanied by `X-Content-Type-Options: nosniff`)
and a `Content-Disposition` header carrying a **sanitized** filename (no CR/LF or
path separators). This endpoint deliberately does **not** wrap the payload in the
JSON response envelope: the body is the file content itself, not a
`{ "success": ..., "object": ... }` object. Consumers must therefore read the
response as a binary stream rather than parsing it as JSON.

### Side effects

None. Downloading a file is a read-only operation and does not modify stored
state.

### Error modes

| Status | Meaning |
|--------|---------|
| `401 Unauthorized` | The bearer token is missing, invalid, or expired — see [Authentication](authentication.md). |
| `403 Forbidden` | The caller is authenticated but not authorized for this specific file. |
| `404 Not Found` | No file exists for the requested `{id}`. |

Failures would be reported with the shared error model documented in
[Errors](errors.md).

### Example

```bash
curl "https://<host>/api/v1/file/<opaque-file-id>" \
  -H "Authorization: Bearer <access_token>" \
  -o local.pdf
```

## Limits and configuration

File contents are persisted through the **`Storage.Net`** abstraction, so the
physical storage location depends on how the API host is configured rather than on
the endpoints themselves. The physical layout is **derived from the file `Id`**
(sharded by the Guid prefix), not from any client input.

Source: /WebVella.Erp/WebVella.Erp.csproj:L62 (Storage.Net 9.3.0), /WebVella.Erp/Database/DbFileRepository.cs:L476 (`GetFileSystemPath`), L495 (`GetBlobPath`).

**Storage configuration keys** — referenced by **name only**. Never embed secret
values or environment-specific paths in documentation; supply them through
application configuration, environment variables, or a Kubernetes Secret, and see
the [Configuration reference](../deployment/configuration-reference.md).

| Config key | Purpose |
|------------|---------|
| `EnableFileSystemStorage` | Toggles the local file-system storage provider on or off. |
| `FileSystemStorageFolder` | Names the setting that points the file-system provider at its root folder. Its value is environment-specific and is **not** reproduced here. |

Source: /WebVella.Erp/ErpSettings.cs:L15 (EnableFileSystemStorage), /WebVella.Erp/ErpSettings.cs:L16 (FileSystemStorageFolder).

- **The maximum upload size and the allowed content types: Not available / to be
  confirmed.** Needed: the upload options defined by the `WebVella.Erp.Api`
  host — specifically the maximum accepted multipart body/file size (governing the
  `413` response) and the allowlist of permitted content types (governing the
  `415` response).

## Related pages

- [Authentication](authentication.md) — how bearer tokens are obtained and
  validated for the requests above.
- [Errors](errors.md) — the error model and the full list of status codes these
  endpoints can return.
- [Records](records.md) — associating an uploaded file with a file field on a
  record.
- [API Reference overview](index.md) — base URL, versioning, and the response
  envelope used by the upload endpoint.
