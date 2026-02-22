using System.Text.Json.Serialization;

namespace WebVellaErp.FileManagement.Models
{
    /// <summary>
    /// Response DTO returned by the Upload Lambda handler after initiating an S3 presigned upload.
    /// <para>
    /// Replaces the pattern where <c>DbFileRepository.Create()</c> (source
    /// <c>DbFileRepository.cs</c> lines 119–200) directly wrote file bytes into PostgreSQL
    /// Large Objects, the local filesystem, or cloud blob storage. In the serverless
    /// architecture, the handler creates a DynamoDB metadata record and returns a
    /// presigned PUT URL so the client uploads content directly to S3.
    /// </para>
    /// </summary>
    public class UploadFileResponse
    {
        /// <summary>
        /// The unique identifier assigned to the newly created file.
        /// In the source monolith, <c>Guid.NewGuid()</c> was generated at
        /// <c>DbFileRepository.Create()</c> line 155.
        /// </summary>
        [JsonPropertyName("fileId")]
        public Guid FileId { get; set; }

        /// <summary>
        /// S3 presigned PUT URL for the client to upload the file content directly to S3.
        /// <para>
        /// This is a new property that replaces the direct binary write performed by
        /// <c>DbFileRepository.Create()</c> (lines 140–188), which wrote bytes to a
        /// PostgreSQL Large Object, filesystem path, or cloud blob storage backend.
        /// The presigned URL is time-limited and scoped to the specific S3 object key.
        /// </para>
        /// </summary>
        [JsonPropertyName("presignedUrl")]
        public string PresignedUrl { get; set; } = string.Empty;

        /// <summary>
        /// The S3 object key where the file content will be stored.
        /// Replaces <c>ObjectId</c> (PostgreSQL Large Object OID) from <c>DbFile.cs</c> line 12.
        /// Generated using the sharding pattern from <c>DbFileRepository.GetBlobPath()</c>
        /// (source lines 496–508): <c>{depth1}/{depth2}/{fileId}{extension}</c>.
        /// </summary>
        [JsonPropertyName("objectKey")]
        public string ObjectKey { get; set; } = string.Empty;

        /// <summary>
        /// UTC timestamp indicating when the presigned upload URL expires.
        /// After this time, the client must request a new presigned URL.
        /// This is a new property for the S3 presigned URL lifecycle — the monolith
        /// had no URL expiration concept since file content was written server-side.
        /// </summary>
        [JsonPropertyName("expiresAt")]
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// The initial file metadata record created in DynamoDB before the upload completes.
        /// References the <see cref="FileMetadata"/> class, which replaces <c>DbFile</c>
        /// from <c>WebVella.Erp.Database</c>.
        /// </summary>
        [JsonPropertyName("metadata")]
        public FileMetadata? Metadata { get; set; }

        /// <summary>
        /// Indicates whether the upload initiation operation succeeded.
        /// Part of the consistent API contract across all File Management responses.
        /// </summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Optional informational or error message describing the operation result.
        /// <c>null</c> on success; contains a descriptive error message on failure
        /// (e.g., "filepath cannot be null or empty" from source line 122,
        /// or "file already exists" from source line 130).
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Response DTO returned by the Download Lambda handler for S3 presigned downloads.
    /// <para>
    /// Replaces the pattern where <c>DbFile.GetBytes()</c> (source <c>DbFile.cs</c>
    /// lines 73–104) directly returned <c>byte[]</c> arrays read from PostgreSQL
    /// Large Objects, the filesystem, or cloud blob storage. In the serverless
    /// architecture, the handler returns a presigned GET URL so the client downloads
    /// content directly from S3.
    /// </para>
    /// </summary>
    public class DownloadFileResponse
    {
        /// <summary>
        /// S3 presigned GET URL for the client to download the file content directly from S3.
        /// <para>
        /// Replaces the direct <c>byte[]</c> return from <c>DbFile.GetBytes()</c>
        /// (source <c>DbFile.cs</c> line 73) and the three storage backend reading
        /// paths in <c>GetContentStream()</c> (lines 36–71).
        /// The presigned URL is time-limited and scoped to the specific S3 object key.
        /// </para>
        /// </summary>
        [JsonPropertyName("presignedUrl")]
        public string PresignedUrl { get; set; } = string.Empty;

        /// <summary>
        /// UTC timestamp indicating when the presigned download URL expires.
        /// After this time, the client must request a new presigned URL.
        /// </summary>
        [JsonPropertyName("expiresAt")]
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// File metadata associated with the download, retrieved from DynamoDB.
        /// Replaces the <c>DbFile</c> object returned by <c>DbFileRepository.Find()</c>
        /// (source line 34). <c>null</c> if the file was not found.
        /// </summary>
        [JsonPropertyName("metadata")]
        public FileMetadata? Metadata { get; set; }

        /// <summary>
        /// Indicates whether the download operation succeeded.
        /// Part of the consistent API contract across all File Management responses.
        /// </summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Optional informational or error message describing the operation result.
        /// <c>null</c> on success; contains a descriptive error message on failure
        /// (e.g., "file not found" when the requested file does not exist in DynamoDB).
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Response DTO returned by the file listing endpoint with DynamoDB-based pagination.
    /// <para>
    /// Replaces the <c>List&lt;DbFile&gt;</c> return from <c>DbFileRepository.FindAll()</c>
    /// (source <c>DbFileRepository.cs</c> lines 58–117), which performed SQL OFFSET/LIMIT
    /// pagination against the PostgreSQL <c>files</c> table. The serverless architecture
    /// uses DynamoDB pagination with <c>LastEvaluatedKey</c> continuation tokens.
    /// </para>
    /// </summary>
    public class ListFilesResponse
    {
        /// <summary>
        /// List of file metadata items for the current page.
        /// Replaces the <c>List&lt;DbFile&gt;</c> return from <c>FindAll()</c> (lines 112–116).
        /// Initialized to an empty list to avoid null reference issues during serialization.
        /// </summary>
        [JsonPropertyName("items")]
        public List<FileMetadata> Items { get; set; } = new List<FileMetadata>();

        /// <summary>
        /// Total number of files matching the query criteria.
        /// <para>
        /// New property — the source monolith's <c>FindAll()</c> did not perform a count
        /// query; it only returned filtered results. This property enables the frontend
        /// to display total result counts and calculate pagination controls.
        /// </para>
        /// </summary>
        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }

        /// <summary>
        /// Current page number (1-based).
        /// Derived from the skip/limit offset calculation in <c>FindAll()</c> (lines 69–80)
        /// and the <c>UserFileService.GetFilesList()</c> pagination pattern (line 19:
        /// <c>var skipCount = (page-1)*pageSize</c>).
        /// </summary>
        [JsonPropertyName("page")]
        public int Page { get; set; }

        /// <summary>
        /// Number of items per page.
        /// Maps to the <c>limit</c> parameter from <c>FindAll()</c> (line 58) and the
        /// <c>pageSize</c> parameter from <c>UserFileService.GetFilesList()</c> (line 15,
        /// default 30).
        /// </summary>
        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; }

        /// <summary>
        /// DynamoDB pagination continuation token (Base64-encoded last evaluated key).
        /// <para>
        /// Replaces SQL OFFSET/LIMIT pagination from the source. When <c>null</c>,
        /// there are no more pages of results. Clients pass this value in subsequent
        /// requests to retrieve the next page.
        /// </para>
        /// </summary>
        [JsonPropertyName("lastEvaluatedKey")]
        public string? LastEvaluatedKey { get; set; }

        /// <summary>
        /// Indicates whether the list operation succeeded.
        /// Part of the consistent API contract across all File Management responses.
        /// </summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Optional informational or error message describing the operation result.
        /// <c>null</c> on success; contains a descriptive error message on failure.
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Generic response DTO for file operations: Copy, Move, and Delete.
    /// <para>
    /// Replaces the return patterns from:
    /// <list type="bullet">
    ///   <item><description><c>DbFileRepository.Copy()</c> (lines 234–281) — returned <c>DbFile</c> of the new copy</description></item>
    ///   <item><description><c>DbFileRepository.Move()</c> (lines 290–368) — returned <c>DbFile</c> at the new path</description></item>
    ///   <item><description><c>DbFileRepository.Delete()</c> (lines 375–429) — returned void</description></item>
    /// </list>
    /// For Copy/Move, <see cref="Metadata"/> contains the resulting file metadata.
    /// For Delete, <see cref="Metadata"/> is <c>null</c>.
    /// </para>
    /// </summary>
    public class FileOperationResponse
    {
        /// <summary>
        /// The resulting file metadata after the operation completes.
        /// <para>
        /// For <b>Copy</b>: the newly created file metadata (from source line 270–271
        /// <c>Create()</c> call in <c>Copy()</c>).
        /// For <b>Move</b>: the updated file metadata at the destination path
        /// (from source line 360 <c>Find(destinationFilepath)</c>).
        /// For <b>Delete</b>: <c>null</c> (the file no longer exists).
        /// </para>
        /// </summary>
        [JsonPropertyName("metadata")]
        public FileMetadata? Metadata { get; set; }

        /// <summary>
        /// Indicates whether the file operation succeeded.
        /// Part of the consistent API contract across all File Management responses.
        /// </summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Error or confirmation message describing the operation result.
        /// <para>
        /// Maps to exception messages from the source monolith:
        /// <list type="bullet">
        ///   <item><description>"Source file cannot be found." (source line 255 in <c>Copy()</c>, line 311 in <c>Move()</c>)</description></item>
        ///   <item><description>"Destination file already exists and no overwrite specified." (source line 258 in <c>Copy()</c>, line 314 in <c>Move()</c>)</description></item>
        ///   <item><description>"filepath cannot be null or empty" (source line 237/239/378)</description></item>
        /// </list>
        /// <c>null</c> on success for Delete; contains confirmation message for Copy/Move.
        /// </para>
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
