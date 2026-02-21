using System.Text.Json.Serialization;

namespace WebVellaErp.FileManagement.Models
{
    /// <summary>
    /// Request DTO for file upload operations. Used by the UploadHandler Lambda to generate
    /// S3 presigned URLs and persist file metadata to DynamoDB.
    /// <para>
    /// Maps to the monolith's <c>DbFileRepository.Create(string filepath, byte[] buffer, DateTime? createdOn, Guid? createdBy)</c>
    /// method for standard uploads, and <c>DbFileRepository.CreateTempFile(string filename, byte[] buffer, string extension)</c>
    /// for temporary file uploads. In the serverless architecture, file content is uploaded directly
    /// to S3 via a presigned URL rather than being sent as a byte buffer through the API.
    /// </para>
    /// </summary>
    public class UploadFileRequest
    {
        /// <summary>
        /// Logical file path within the file management system.
        /// Convention: lowercase, must start with <c>/</c> (e.g., <c>/documents/report.pdf</c>).
        /// <para>
        /// Maps to the <c>filepath</c> parameter in <c>DbFileRepository.Create()</c>.
        /// The service normalizes this value to lowercase and ensures it begins with <c>/</c>,
        /// matching the monolith's validation: <c>filepath = filepath.ToLowerInvariant()</c> and
        /// <c>if (!filepath.StartsWith(FOLDER_SEPARATOR)) filepath = FOLDER_SEPARATOR + filepath</c>.
        /// </para>
        /// <para>
        /// For temporary uploads (<see cref="IsTemp"/> = true), this field is optional since
        /// the path is auto-generated as <c>/tmp/{section}/{fileName}{extension}</c>.
        /// For standard uploads, this field is required.
        /// </para>
        /// </summary>
        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// MIME content type of the file being uploaded (e.g., <c>image/png</c>, <c>application/pdf</c>).
        /// <para>
        /// Required for the S3 presigned URL <c>Content-Type</c> header. Derived from the
        /// <c>MimeMapping.MimeUtility.GetMimeMapping(path)</c> pattern used in the monolith's
        /// <c>UserFileService.CreateUserFile()</c> for file type classification. The caller
        /// should provide the correct MIME type; if omitted, the service may attempt detection
        /// from the file extension.
        /// </para>
        /// </summary>
        [JsonPropertyName("contentType")]
        public string ContentType { get; set; } = string.Empty;

        /// <summary>
        /// File size in bytes for the upload. Used for S3 upload size validation and
        /// content-length enforcement on the presigned URL.
        /// <para>
        /// Derived from the file content buffer length parameter in <c>DbFileRepository.Create()</c>
        /// (the <c>buffer.Length</c> value). In the serverless architecture, the client provides
        /// the expected file size upfront so the presigned URL can enforce size limits.
        /// </para>
        /// </summary>
        [JsonPropertyName("size")]
        public long Size { get; set; }

        /// <summary>
        /// Identifier of the user performing the upload. Nullable for system-generated uploads
        /// or anonymous temporary file creation.
        /// <para>
        /// Maps to the <c>createdBy</c> parameter in <c>DbFileRepository.Create()</c>.
        /// When set, this value is stored in the file metadata record as both
        /// <c>CreatedBy</c> and <c>ModifiedBy</c>, matching the monolith behavior where
        /// the creator is also the initial modifier.
        /// </para>
        /// </summary>
        [JsonPropertyName("createdBy")]
        public Guid? CreatedBy { get; set; }

        /// <summary>
        /// Indicates whether this is a temporary file upload. Temporary files are stored
        /// under the <c>/tmp/{section}/{filename}</c> path convention.
        /// <para>
        /// Maps to the <c>DbFileRepository.CreateTempFile()</c> behavior. When <c>true</c>,
        /// the service auto-generates the file path as <c>/tmp/{guid}/{fileName}{extension}</c>,
        /// overriding any value in <see cref="FilePath"/>. The <see cref="FileName"/> and
        /// <see cref="Extension"/> properties are used for temp file naming.
        /// </para>
        /// </summary>
        [JsonPropertyName("isTemp")]
        public bool IsTemp { get; set; }

        /// <summary>
        /// Original file name for temp file creation (e.g., <c>report</c> or <c>report.pdf</c>).
        /// Only used when <see cref="IsTemp"/> is <c>true</c>.
        /// <para>
        /// Maps to the <c>filename</c> parameter in <c>DbFileRepository.CreateTempFile(string filename, byte[] buffer, string extension)</c>.
        /// Combined with <see cref="Extension"/> to form the final temp file path segment.
        /// </para>
        /// </summary>
        [JsonPropertyName("fileName")]
        public string? FileName { get; set; }

        /// <summary>
        /// File extension override for temporary file uploads (e.g., <c>.pdf</c>, <c>.jpg</c>).
        /// Only used when <see cref="IsTemp"/> is <c>true</c>.
        /// <para>
        /// Maps to the <c>extension</c> parameter in <c>DbFileRepository.CreateTempFile()</c>.
        /// The service normalizes this value: trimming whitespace, converting to lowercase,
        /// and prepending a dot if not already present (matching the monolith logic:
        /// <c>extension = extension.Trim().ToLowerInvariant(); if (!extension.StartsWith(".")) extension = "." + extension;</c>).
        /// </para>
        /// </summary>
        [JsonPropertyName("extension")]
        public string? Extension { get; set; }
    }

    /// <summary>
    /// Request DTO for file download operations. Used by the DownloadHandler Lambda to
    /// generate S3 presigned download URLs or retrieve file metadata.
    /// <para>
    /// Maps to the monolith's <c>DbFileRepository.Find(string filepath)</c> for path-based
    /// lookup and <c>DbFile.GetBytes()</c> for content retrieval. In the serverless architecture,
    /// file content is served via S3 presigned URLs rather than direct byte streaming.
    /// </para>
    /// <para>
    /// At least one of <see cref="FilePath"/> or <see cref="FileId"/> must be provided.
    /// If both are specified, <see cref="FileId"/> takes precedence for the lookup.
    /// </para>
    /// </summary>
    public class DownloadFileRequest
    {
        /// <summary>
        /// Logical file path to retrieve. Convention: lowercase, starts with <c>/</c>.
        /// <para>
        /// Maps to the <c>filepath</c> parameter in <c>DbFileRepository.Find(string filepath)</c>.
        /// The service normalizes this value to lowercase and ensures it starts with <c>/</c>.
        /// </para>
        /// </summary>
        [JsonPropertyName("filePath")]
        public string? FilePath { get; set; }

        /// <summary>
        /// Alternative lookup by unique file identifier. Takes precedence over
        /// <see cref="FilePath"/> when both are provided.
        /// <para>
        /// Maps to <c>DbFile.Id</c> (Guid primary key in the monolith's <c>files</c> table).
        /// In the target architecture, this maps to the DynamoDB partition key
        /// <c>PK=FILE#{fileId}</c>.
        /// </para>
        /// </summary>
        [JsonPropertyName("fileId")]
        public Guid? FileId { get; set; }

        /// <summary>
        /// Presigned URL expiration time in minutes. Controls how long the generated
        /// S3 presigned download URL remains valid.
        /// <para>
        /// Default: 60 minutes. This is a new parameter for the serverless architecture
        /// (no equivalent in the monolith, where files were streamed directly from
        /// PostgreSQL Large Objects, filesystem, or blob storage). Valid range is
        /// typically 1–10080 minutes (up to 7 days for S3 presigned URLs).
        /// </para>
        /// </summary>
        [JsonPropertyName("expirationMinutes")]
        public int ExpirationMinutes { get; set; } = 60;
    }

    /// <summary>
    /// Request DTO for listing files with filtering and pagination. Used by file listing
    /// Lambda endpoints to query file metadata from DynamoDB.
    /// <para>
    /// Maps to the monolith's <c>DbFileRepository.FindAll(string startsWithPath, bool includeTempFiles, int? skip, int? limit)</c>.
    /// In the serverless architecture, SQL <c>OFFSET/LIMIT</c> pagination is replaced by
    /// DynamoDB cursor-based pagination via <see cref="ExclusiveStartKey"/>, while
    /// <see cref="Page"/> and <see cref="PageSize"/> provide a familiar page-based interface
    /// for the frontend.
    /// </para>
    /// </summary>
    public class ListFilesRequest
    {
        /// <summary>
        /// Filter files by path prefix. Only files whose paths start with this value
        /// are returned. Convention: lowercase, starts with <c>/</c>.
        /// <para>
        /// Maps to the <c>startsWithPath</c> parameter in <c>DbFileRepository.FindAll()</c>.
        /// The service normalizes this value to lowercase and ensures it starts with <c>/</c>,
        /// matching the monolith's <c>ILIKE @startswith</c> SQL pattern.
        /// </para>
        /// </summary>
        [JsonPropertyName("pathPrefix")]
        public string? PathPrefix { get; set; }

        /// <summary>
        /// Whether to include temporary files (stored under <c>/tmp/</c>) in the results.
        /// <para>
        /// Maps to the <c>includeTempFiles</c> parameter in <c>DbFileRepository.FindAll()</c>.
        /// When <c>false</c> (default), files whose paths contain <c>/tmp</c> are excluded,
        /// matching the monolith's <c>filepath NOT ILIKE @tmp_path</c> SQL filter.
        /// </para>
        /// </summary>
        [JsonPropertyName("includeTempFiles")]
        public bool IncludeTempFiles { get; set; }

        /// <summary>
        /// Page number for results pagination (1-based). Combined with <see cref="PageSize"/>
        /// to calculate the DynamoDB query offset.
        /// <para>
        /// Derived from the <c>skip</c>/<c>limit</c> parameters in <c>DbFileRepository.FindAll()</c>.
        /// The monolith used SQL <c>OFFSET skip LIMIT limit</c>; the serverless version translates
        /// <c>Page</c> and <c>PageSize</c> for the frontend while using <see cref="ExclusiveStartKey"/>
        /// internally for efficient DynamoDB pagination.
        /// </para>
        /// </summary>
        [JsonPropertyName("page")]
        public int Page { get; set; } = 1;

        /// <summary>
        /// Maximum number of items to return per page.
        /// <para>
        /// Maps to the <c>limit</c> parameter in <c>DbFileRepository.FindAll()</c>.
        /// Default: 30, matching the default page size used in the monolith's
        /// <c>UserFileService.GetFilesList(pageSize: 30)</c>.
        /// </para>
        /// </summary>
        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; } = 30;

        /// <summary>
        /// DynamoDB cursor-based pagination token. When provided, the query continues
        /// from where the previous page left off.
        /// <para>
        /// This is a new parameter for the DynamoDB-backed architecture, replacing the
        /// monolith's SQL <c>OFFSET</c>/<c>LIMIT</c> pagination (lines 69–80 of
        /// <c>DbFileRepository.FindAll()</c>). The token is an opaque base64-encoded string
        /// returned by previous list operations. Pass <c>null</c> or omit for the first page.
        /// </para>
        /// </summary>
        [JsonPropertyName("exclusiveStartKey")]
        public string? ExclusiveStartKey { get; set; }
    }

    /// <summary>
    /// Request DTO for file copy operations. Used to copy a file from one logical path
    /// to another within the file management system.
    /// <para>
    /// Maps to the monolith's <c>DbFileRepository.Copy(string sourceFilepath, string destinationFilepath, bool overwrite)</c>.
    /// In the serverless architecture, this translates to an S3 <c>CopyObject</c> operation
    /// combined with a new DynamoDB metadata record for the destination file.
    /// </para>
    /// </summary>
    public class CopyFileRequest
    {
        /// <summary>
        /// Source file path to copy from. Convention: lowercase, starts with <c>/</c>.
        /// <para>
        /// Maps to the <c>sourceFilepath</c> parameter in <c>DbFileRepository.Copy()</c>.
        /// The service validates that the source file exists before copying. The path is
        /// normalized to lowercase and ensured to start with <c>/</c>.
        /// </para>
        /// </summary>
        [JsonPropertyName("sourceFilePath")]
        public string SourceFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Destination file path to copy to. Convention: lowercase, starts with <c>/</c>.
        /// <para>
        /// Maps to the <c>destinationFilepath</c> parameter in <c>DbFileRepository.Copy()</c>.
        /// If a file already exists at this path and <see cref="Overwrite"/> is <c>false</c>,
        /// the operation fails with a conflict error. The path is normalized to lowercase
        /// and ensured to start with <c>/</c>.
        /// </para>
        /// </summary>
        [JsonPropertyName("destinationFilePath")]
        public string DestinationFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Whether to overwrite the destination file if it already exists.
        /// <para>
        /// Maps to the <c>overwrite</c> parameter in <c>DbFileRepository.Copy()</c>.
        /// When <c>false</c> (default) and the destination file exists, the service throws
        /// a conflict error matching the monolith behavior:
        /// <c>"Destination file already exists and no overwrite specified."</c>
        /// When <c>true</c>, the existing destination file is deleted before copying.
        /// </para>
        /// </summary>
        [JsonPropertyName("overwrite")]
        public bool Overwrite { get; set; }
    }

    /// <summary>
    /// Request DTO for file move/rename operations. Used to move a file from one logical
    /// path to another within the file management system.
    /// <para>
    /// Maps to the monolith's <c>DbFileRepository.Move(string sourceFilepath, string destinationFilepath, bool overwrite)</c>.
    /// In the serverless architecture, this translates to an S3 <c>CopyObject</c> + <c>DeleteObject</c>
    /// operation combined with updating the DynamoDB metadata record's file path. The structure
    /// intentionally mirrors <see cref="CopyFileRequest"/> since the monolith's <c>Copy()</c> and
    /// <c>Move()</c> methods share the same parameter signature.
    /// </para>
    /// </summary>
    public class MoveFileRequest
    {
        /// <summary>
        /// Source file path to move from. Convention: lowercase, starts with <c>/</c>.
        /// <para>
        /// Maps to the <c>sourceFilepath</c> parameter in <c>DbFileRepository.Move()</c>.
        /// The service validates that the source file exists before moving. The path is
        /// normalized to lowercase and ensured to start with <c>/</c>.
        /// </para>
        /// </summary>
        [JsonPropertyName("sourceFilePath")]
        public string SourceFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Destination file path to move to. Convention: lowercase, starts with <c>/</c>.
        /// <para>
        /// Maps to the <c>destinationFilepath</c> parameter in <c>DbFileRepository.Move()</c>.
        /// If a file already exists at this path and <see cref="Overwrite"/> is <c>false</c>,
        /// the operation fails with a conflict error. The path is normalized to lowercase
        /// and ensured to start with <c>/</c>.
        /// </para>
        /// </summary>
        [JsonPropertyName("destinationFilePath")]
        public string DestinationFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Whether to overwrite the destination file if it already exists.
        /// <para>
        /// Maps to the <c>overwrite</c> parameter in <c>DbFileRepository.Move()</c>.
        /// When <c>false</c> (default) and the destination file exists, the service throws
        /// a conflict error matching the monolith behavior:
        /// <c>"Destination file already exists and no overwrite specified."</c>
        /// When <c>true</c>, the existing destination file is deleted before moving.
        /// </para>
        /// </summary>
        [JsonPropertyName("overwrite")]
        public bool Overwrite { get; set; }
    }

    /// <summary>
    /// Request DTO for file deletion operations. Used by the file management Lambda
    /// to delete a file's S3 content and DynamoDB metadata record.
    /// <para>
    /// Maps to the monolith's <c>DbFileRepository.Delete(string filepath)</c>. In the
    /// serverless architecture, this translates to an S3 <c>DeleteObject</c> operation
    /// followed by a DynamoDB <c>DeleteItem</c> operation, with a domain event
    /// <c>file-management.file.deleted</c> published to SNS. The monolith's behavior
    /// where deleting a non-existent file is a no-op (silent return) is preserved.
    /// </para>
    /// </summary>
    public class DeleteFileRequest
    {
        /// <summary>
        /// File path to delete. Convention: lowercase, starts with <c>/</c>.
        /// <para>
        /// Maps to the <c>filepath</c> parameter in <c>DbFileRepository.Delete()</c>.
        /// The service normalizes this value to lowercase and ensures it starts with <c>/</c>,
        /// matching the monolith validation pattern. If no file exists at this path,
        /// the operation completes silently without error (matching the monolith behavior
        /// where <c>Find(filepath)</c> returning <c>null</c> results in an early return).
        /// </para>
        /// </summary>
        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;
    }
}
