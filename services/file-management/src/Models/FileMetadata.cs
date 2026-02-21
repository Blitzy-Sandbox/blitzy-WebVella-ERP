using System.Text.Json.Serialization;
using Amazon.DynamoDBv2.DataModel;

namespace WebVellaErp.FileManagement.Models
{
    /// <summary>
    /// Core file metadata domain model for the File Management microservice.
    /// Replaces <c>DbFile</c> from the <c>WebVella.Erp.Database</c> namespace
    /// (<c>WebVella.Erp/Database/DbFile.cs</c>) and adds S3-specific and DynamoDB-specific
    /// properties required by the serverless architecture.
    ///
    /// File metadata is persisted in DynamoDB using a single-table design, while actual
    /// file content is stored in Amazon S3. The DynamoDB key scheme is:
    /// <list type="bullet">
    ///   <item><description>PK: FILE#{fileId} (formatted by the repository layer)</description></item>
    ///   <item><description>SK: META (default for file metadata entries)</description></item>
    ///   <item><description>GSI1-PK: PATH#{normalizedPath} (path-based lookups)</description></item>
    ///   <item><description>GSI1-SK: CREATED#{ISO8601Timestamp} (time-ordered listing)</description></item>
    /// </list>
    ///
    /// Serialization uses <c>System.Text.Json</c> attributes for AOT-compatible
    /// JSON serialization, replacing <c>Newtonsoft.Json</c> from the monolith.
    /// DynamoDB persistence uses <c>Amazon.DynamoDBv2.DataModel</c> attributes for
    /// Object Persistence Model mapping.
    ///
    /// All <see cref="DateTime"/> properties are stored and returned in UTC.
    /// </summary>
    [DynamoDBTable("file-management-files")]
    public class FileMetadata
    {
        #region Constants

        /// <summary>
        /// File path separator constant. All file paths use forward slashes as directory delimiters.
        /// Extracted from <c>DbFileRepository.FOLDER_SEPARATOR</c> (source line 14).
        /// </summary>
        public const string FolderSeparator = "/";

        /// <summary>
        /// Temporary folder name for ephemeral file storage.
        /// Temp files use the path pattern <c>/tmp/{section}/{filename}</c>.
        /// Extracted from <c>DbFileRepository.TMP_FOLDER_NAME</c> (source line 15).
        /// </summary>
        public const string TmpFolderName = "tmp";

        #endregion

        #region Core Properties (mapped from DbFile.cs)

        /// <summary>
        /// Unique file identifier. Maps from <c>DbFile.Id</c> (source line 11).
        /// In the source monolith, populated from <c>(Guid)row["id"]</c> (line 21).
        /// In DynamoDB, the repository layer formats this as <c>FILE#{id}</c> for the
        /// partition key. This property holds the raw <see cref="Guid"/> value.
        /// </summary>
        [DynamoDBHashKey("PK")]
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Logical file path within the virtual file system.
        /// Maps from <c>DbFile.FilePath</c> (source line 13).
        /// Conventions preserved from the monolith:
        /// <list type="bullet">
        ///   <item><description>Always lowercase (enforced in <c>Create()</c> at source line 125: <c>filepath.ToLowerInvariant()</c>)</description></item>
        ///   <item><description>Always starts with <c>/</c> (enforced in <c>Create()</c> at source lines 126-127)</description></item>
        ///   <item><description>Temp files use pattern <c>/tmp/{section}/{filename}</c></description></item>
        /// </list>
        /// Use <see cref="NormalizeFilePath"/> to enforce these conventions.
        /// </summary>
        [DynamoDBProperty("filepath")]
        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// S3 object key where the file content is stored.
        /// <para>
        /// <b>Replaces</b> <c>DbFile.ObjectId</c> (source line 12, type <c>uint</c>).
        /// In the monolith, <c>ObjectId</c> was a PostgreSQL Large Object OID
        /// (<c>(uint)((decimal)row["object_id"])</c>, line 22). In the target serverless
        /// architecture, this is a string-typed S3 key.
        /// </para>
        /// <para>
        /// S3 key format follows the sharding pattern from <c>DbFileRepository.GetBlobPath()</c>
        /// (source lines 496-508): <c>{depth1}/{depth2}/{fileId}{extension}</c>
        /// where depth1/depth2 are the first 4 hex chars of the GUID split into 2-char segments.
        /// </para>
        /// Use <see cref="GenerateObjectKey"/> to create this value.
        /// </summary>
        [DynamoDBProperty("object_key")]
        [JsonPropertyName("objectKey")]
        public string ObjectKey { get; set; } = string.Empty;

        /// <summary>
        /// MIME content type of the file (e.g., "image/png", "application/pdf", "video/mp4").
        /// <para>
        /// <b>New property</b> not present in <c>DbFile.cs</c>.
        /// Derived from <c>MimeMapping.MimeUtility.GetMimeMapping(path)</c> pattern in
        /// <c>UserFileService.cs</c> (line 69). Used for setting the S3 <c>Content-Type</c>
        /// header on presigned URL generation and for file type classification.
        /// </para>
        /// Defaults to <c>"application/octet-stream"</c> (standard binary MIME type).
        /// </summary>
        [DynamoDBProperty("content_type")]
        [JsonPropertyName("contentType")]
        public string ContentType { get; set; } = "application/octet-stream";

        /// <summary>
        /// File size in bytes.
        /// <para>
        /// <b>New property</b> not directly present in <c>DbFile.cs</c>.
        /// Derived from upload buffer length in <c>DbFileRepository.Create()</c>
        /// (line 119, <c>buffer</c> parameter) and <c>UserFileService.cs</c>
        /// (line 65-66) where <c>tempFile.GetBytes().Length</c> was used for size calculation.
        /// </para>
        /// </summary>
        [DynamoDBProperty("size")]
        [JsonPropertyName("size")]
        public long Size { get; set; }

        /// <summary>
        /// ID of the user who created/uploaded the file.
        /// Maps from <c>DbFile.CreatedBy</c> (source line 14).
        /// In the source monolith, populated from <c>(Guid?)row["created_by"]</c>
        /// with <c>DBNull</c> check (source lines 27-29).
        /// Nullable — <c>null</c> when the file was created by system/anonymous
        /// (e.g., temp files from <c>CreateTempFile()</c> at line 448 pass <c>null</c>).
        /// </summary>
        [DynamoDBProperty("created_by")]
        [JsonPropertyName("createdBy")]
        public Guid? CreatedBy { get; set; }

        /// <summary>
        /// UTC timestamp when the file was created.
        /// Maps from <c>DbFile.CreatedOn</c> (source line 15).
        /// In <c>DbFileRepository.Create()</c> (line 158): <c>createdOn ?? DateTime.UtcNow</c>.
        /// Always stored and returned in UTC.
        /// </summary>
        [DynamoDBProperty("created_on")]
        [JsonPropertyName("createdOn")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// ID of the user who last modified the file.
        /// Maps from <c>DbFile.LastModifiedBy</c> (source line 16).
        /// In the source monolith, nullable, populated from <c>(Guid?)row["modified_by"]</c>
        /// with <c>DBNull</c> check (source lines 31-33).
        /// Nullable — <c>null</c> when file has not been modified or was modified by system.
        /// </summary>
        [DynamoDBProperty("modified_by")]
        [JsonPropertyName("lastModifiedBy")]
        public Guid? LastModifiedBy { get; set; }

        /// <summary>
        /// UTC timestamp of the last modification.
        /// Maps from <c>DbFile.LastModificationDate</c> (source line 17).
        /// Updated via <c>DbFileRepository.UpdateModificationDate()</c> (source lines 202-225).
        /// Always stored and returned in UTC.
        /// </summary>
        [DynamoDBProperty("modified_on")]
        [JsonPropertyName("lastModificationDate")]
        public DateTime LastModificationDate { get; set; }

        /// <summary>
        /// Whether this file resides in the temporary storage area.
        /// <para>
        /// <b>New property</b> not directly present in <c>DbFile.cs</c>.
        /// Derived from the temp file path check logic in <c>DbFileRepository.cs</c>:
        /// <list type="bullet">
        ///   <item><description><c>FindAll()</c> excludes temp files via <c>filepath NOT ILIKE %/tmp</c> (line 88-89)</description></item>
        ///   <item><description><c>CreateTempFile()</c> creates files with path <c>/tmp/{section}/{filename}</c> (line 447)</description></item>
        ///   <item><description><c>CleanupExpiredTempFiles()</c> finds files with <c>filepath ILIKE %/tmp</c> (line 463)</description></item>
        /// </list>
        /// </para>
        /// Use <see cref="IsTemporaryFile"/> to compute this from the current <see cref="FilePath"/>.
        /// </summary>
        [DynamoDBProperty("is_temp")]
        [JsonPropertyName("isTemp")]
        public bool IsTemp { get; set; }

        /// <summary>
        /// DynamoDB Time-to-Live attribute for automatic temp file expiry.
        /// Unix epoch timestamp in seconds. When set, DynamoDB automatically deletes
        /// the item after this time.
        /// <para>
        /// Replaces the <c>CleanupExpiredTempFiles()</c> cron approach from the source
        /// monolith (lines 455-469). Only set for temp files (<see cref="IsTemp"/> == true).
        /// </para>
        /// <c>null</c> means no auto-expiry (permanent files).
        /// </summary>
        [DynamoDBProperty("ttl")]
        [JsonPropertyName("ttl")]
        public long? Ttl { get; set; }

        #endregion

        #region DynamoDB Key Properties

        /// <summary>
        /// DynamoDB sort key for single-table design.
        /// Format: <c>"META"</c> for file metadata entries.
        /// This enables future expansion of the file item (e.g., versions, tags)
        /// under the same partition key.
        /// Not serialized to API responses — internal DynamoDB implementation detail.
        /// </summary>
        [DynamoDBRangeKey("SK")]
        [JsonIgnore]
        public string Sk { get; set; } = "META";

        /// <summary>
        /// Global Secondary Index 1 partition key for path-based lookups.
        /// Format: <c>PATH#{normalizedPath}</c> — enables looking up files by path,
        /// replicating the primary lookup pattern from <c>DbFileRepository.Find()</c>
        /// (source line 34).
        /// Not serialized to API responses — internal DynamoDB implementation detail.
        /// </summary>
        [DynamoDBGlobalSecondaryIndexHashKey("GSI1")]
        [JsonIgnore]
        public string Gsi1Pk { get; set; } = string.Empty;

        /// <summary>
        /// Global Secondary Index 1 sort key for time-ordered listing within a path prefix.
        /// Format: <c>CREATED#{ISO8601Timestamp}</c> — enables time-ordered listing
        /// of files within a specific path.
        /// Not serialized to API responses — internal DynamoDB implementation detail.
        /// </summary>
        [DynamoDBGlobalSecondaryIndexRangeKey("GSI1")]
        [JsonIgnore]
        public string Gsi1Sk { get; set; } = string.Empty;

        #endregion

        #region Helper Methods

        /// <summary>
        /// Generates an S3 object key using the sharding pattern from
        /// <c>DbFileRepository.GetBlobPath()</c> (source lines 496-508).
        /// <para>
        /// The sharding strategy distributes files across S3 prefixes using the first
        /// 4 hexadecimal characters of the file's GUID, split into two 2-character
        /// folder segments. This prevents hot partitions in S3 by ensuring even
        /// distribution across prefixes.
        /// </para>
        /// <example>
        /// For fileId = <c>a1b2c3d4-e5f6-...</c> and filePath = <c>/images/photo.png</c>:
        /// Returns <c>"a1/b2/a1b2c3d4-e5f6-...-....png"</c>
        /// </example>
        /// </summary>
        /// <param name="fileId">The unique file identifier (GUID).</param>
        /// <param name="filePath">
        /// The logical file path from which the file extension is extracted.
        /// </param>
        /// <returns>
        /// An S3 object key in the format <c>{depth1}/{depth2}/{fileId}{extension}</c>.
        /// If no extension is present, the key is <c>{depth1}/{depth2}/{fileId}</c>.
        /// </returns>
        public static string GenerateObjectKey(Guid fileId, string filePath)
        {
            // Extract the first 8-character hexadecimal segment of the GUID (before the first '-').
            // Example: "a1b2c3d4-e5f6-7890-abcd-ef1234567890" → "a1b2c3d4"
            var guidInitialPart = fileId.ToString().Split('-')[0];

            // First 2 hex chars form the first-level folder for S3 prefix sharding.
            var depth1Folder = guidInitialPart.Substring(0, 2);

            // Next 2 hex chars form the second-level folder for deeper sharding.
            var depth2Folder = guidInitialPart.Substring(2, 2);

            // Extract the file extension (including the leading dot) from the file path.
            // Path.GetExtension handles full paths correctly: "/images/photo.png" → ".png"
            var extension = Path.GetExtension(filePath);

            if (!string.IsNullOrWhiteSpace(extension))
            {
                return $"{depth1Folder}/{depth2Folder}/{fileId}{extension}";
            }

            return $"{depth1Folder}/{depth2Folder}/{fileId}";
        }

        /// <summary>
        /// Normalizes a file path to match the conventions established by the source monolith.
        /// Replicates the path normalization from <c>DbFileRepository.Create()</c>
        /// (source lines 125-127):
        /// <list type="number">
        ///   <item><description>Convert to lowercase via <c>ToLowerInvariant()</c></description></item>
        ///   <item><description>Ensure the path starts with <c>/</c></description></item>
        /// </list>
        /// </summary>
        /// <param name="filePath">The raw file path to normalize.</param>
        /// <returns>A normalized file path that is lowercase and starts with <c>/</c>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="filePath"/> is null.</exception>
        public static string NormalizeFilePath(string filePath)
        {
            ArgumentNullException.ThrowIfNull(filePath);

            filePath = filePath.ToLowerInvariant();

            if (!filePath.StartsWith(FolderSeparator))
            {
                filePath = FolderSeparator + filePath;
            }

            return filePath;
        }

        /// <summary>
        /// Determines whether this file is a temporary file based on its path.
        /// Replicates the temp file check from <c>DbFileRepository.FindAll()</c>
        /// (source line 88-89) where temp files are identified by the path prefix
        /// <c>/tmp/</c>.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the file path starts with <c>/tmp/</c> (case-insensitive);
        /// otherwise <c>false</c>.
        /// </returns>
        public bool IsTemporaryFile()
        {
            return FilePath.StartsWith(
                FolderSeparator + TmpFolderName + FolderSeparator,
                StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
