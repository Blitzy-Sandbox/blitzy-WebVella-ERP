<!--{"sort_order":7, "name": "files", "label": "Files"}-->
# Files

The `/api/v1/` surface lets you **upload** binary files as `multipart/form-data`
and **download** them again by their storage path. File contents are not held in
the relational database; they are persisted through the platform's storage
abstraction, **`Storage.Net`**, which can be backed by the local file system or by
another provider depending on configuration (see
[Limits and configuration](#limits-and-configuration)).

Source: /WebVella.Erp/WebVella.Erp.csproj:L62 (Storage.Net 9.3.0)

Every protected request on this page uses an OIDC-issued JWT presented as a
bearer token, exactly as described in [Authentication](authentication.md). The
HTTP status codes returned on failure follow the shared problem-details model
documented in [Errors](errors.md).

## Upload a file

Uploads a single binary file and stores it through the configured `Storage.Net`
provider. The request is sent as `multipart/form-data`, and the response is the
standard JSON [response envelope](index.md#response-envelope) whose `object`
carries the descriptor of the stored file.

##### Authorization

Requires a valid bearer token. Present the OIDC-issued JWT in the `Authorization`
header using the `Bearer` scheme; see [Authentication](authentication.md).

##### HTTP request

```http
POST https://<host>/api/v1/file HTTP/1.1
Content-Type: multipart/form-data; boundary=<boundary>
Authorization: Bearer <access_token>
```

##### Request body

The body is a `multipart/form-data` payload. It must include a binary file part
and may include optional parts that influence where and how the file is stored:

| Part | Required | Type | Description |
|------|----------|------|-------------|
| `file` | Yes | binary | The file content to store, sent as a file part with a filename and its own `Content-Type`. |
| `path` | No | text | An explicit destination path/key for the stored blob. When omitted, the storage layer assigns one. |
| metadata parts | No | text | Additional form fields (for example a display name) captured alongside the file. |

- **The exact set of optional form parts and their names: Not available / to be
  confirmed.** Needed: the multipart field names accepted by the
  `WebVella.Erp.Api` file-upload endpoint beyond the required `file` part.

##### Request response

On success the API returns the standard response envelope; the `object` field
holds the **descriptor of the stored file** — for example its storage path or a
URL by which it can be retrieved:

```json
{
  "success": true,
  "message": "",
  "timestamp": "2014-03-03T23:20:23Z",
  "errors": [],
  "object": {
    "path": "<stored-file-path>"
  }
}
```

Source: /docs/developer/web-api/response.md

- **The exact fields of the stored-file descriptor: Not available / to be
  confirmed.** Needed: the response DTO returned by the `WebVella.Erp.Api`
  file-upload endpoint — for example whether it exposes a storage `path`, a public
  `url`, a content type, and a size.

##### Side effects

Writes a blob to the configured storage provider (`Storage.Net`). The upload
itself does not create a relational record; associating a stored file with a file
field on a record is done through the record endpoints (see [Records](records.md)).

##### Error modes

| Status | Meaning |
|--------|---------|
| `400 Bad Request` | No file part was supplied, or the multipart payload was malformed. |
| `401 Unauthorized` | The bearer token is missing, invalid, or expired — see [Authentication](authentication.md). |
| `403 Forbidden` | The caller is authenticated but not permitted to upload. |
| `413 Payload Too Large` | The uploaded file exceeds the server's maximum accepted size. |
| `415 Unsupported Media Type` | The file's content type is not accepted by the endpoint. |

- **The maximum upload size (governing `413`) and the set of accepted content
  types (governing `415`): Not available / to be confirmed.** Needed: the upload
  options defined by the `WebVella.Erp.Api` host — the maximum multipart body/file
  size and the list of permitted content types.

See [Errors](errors.md) for the full problem-details model behind these status
codes.

##### Example

```bash
curl -X POST "https://<host>/api/v1/file" \
  -H "Authorization: Bearer <access_token>" \
  -F "file=@/path/to/local.pdf"
```

## Download a file

Streams the raw bytes of a previously stored file, identified by its storage
path. Unlike every other endpoint in this reference, a download does **not**
return the JSON response envelope — the body is the file content itself.

##### Authorization

Requires a valid bearer token by default; present the JWT in the `Authorization`
header as described in [Authentication](authentication.md).

- **Whether any files are served publicly (without a bearer token): Not
  available / to be confirmed.** Needed: confirmation from the `WebVella.Erp.Api`
  host of whether specific public assets are exposed anonymously, and under which
  path, if any.

##### HTTP request

```http
GET https://<host>/api/v1/file/{path} HTTP/1.1
Authorization: Bearer <access_token>
```

`{path}` is the storage path/key of the file, as returned in the `object`
descriptor of the [upload response](#upload-a-file).

##### Request response

The response body is the **raw file bytes**, streamed with the `Content-Type`
response header set to the file's media type (and, typically, a
`Content-Disposition` header carrying the filename). This endpoint deliberately
does **not** wrap the payload in the JSON response envelope: the body is the file
content itself, not a `{ "success": ..., "object": ... }` object. Consumers must
therefore read the response as a binary stream rather than parsing it as JSON.

##### Side effects

None. Downloading a file is a read-only operation and does not modify stored
state.

##### Error modes

| Status | Meaning |
|--------|---------|
| `401 Unauthorized` | The bearer token is missing, invalid, or expired — see [Authentication](authentication.md). |
| `403 Forbidden` | The caller is authenticated but not permitted to read the file. |
| `404 Not Found` | No file exists at the requested `{path}`. |

Failures are reported with the shared problem-details body documented in
[Errors](errors.md).

##### Example

```bash
curl "https://<host>/api/v1/file/<stored-file-path>" \
  -H "Authorization: Bearer <access_token>" \
  -o local.pdf
```

## Limits and configuration

File contents are persisted through the **`Storage.Net`** abstraction, so the
physical storage location depends on how the API host is configured rather than
on the endpoints themselves.

Source: /WebVella.Erp/WebVella.Erp.csproj:L62 (Storage.Net 9.3.0)

**Storage configuration keys** — referenced by **name only**. Never embed secret
values or environment-specific paths in documentation; supply them through
application configuration, environment variables, or a Kubernetes Secret, and see
the [Configuration reference](../deployment/configuration-reference.md).

| Config key | Purpose |
|------------|---------|
| `EnableFileSystemStorage` | Toggles the local file-system storage provider on or off. |
| `FileSystemStorageFolder` | Names the setting that points the file-system provider at its root folder. Its value is environment-specific and is **not** reproduced here. |

Source: /WebVella.Erp/ErpSettings.cs:L15 (EnableFileSystemStorage), /WebVella.Erp/ErpSettings.cs:L16 (FileSystemStorageFolder)

- **The maximum upload size and the allowed content types: Not available / to be
  confirmed.** Needed: the upload options defined by the `WebVella.Erp.Api`
  host — specifically the maximum accepted multipart body/file size (governing the
  `413` response) and the list of permitted content types (governing the `415`
  response).

## Related pages

- [Authentication](authentication.md) — how bearer tokens are obtained and
  validated for the requests above.
- [Errors](errors.md) — the problem-details model and the full list of status
  codes these endpoints can return.
- [Records](records.md) — associating an uploaded file with a file field on a
  record.
- [API Reference overview](index.md) — base URL, versioning, and the response
  envelope used by the upload endpoint.
