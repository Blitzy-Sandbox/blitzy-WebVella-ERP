using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WebVellaErp.FileManagement.Services
{
    /// <summary>
    /// Defines the contract for Amazon S3 file storage operations within the File Management
    /// bounded-context microservice. This interface replaces ALL THREE storage backends from
    /// the WebVella ERP monolith:
    /// <list type="number">
    ///   <item><description>PostgreSQL Large Objects — <c>NpgsqlLargeObjectManager</c> (DbFile.cs, DbFileRepository.cs)</description></item>
    ///   <item><description>Filesystem storage — <c>File.Open/Move/Delete</c> (DbFileRepository.cs)</description></item>
    ///   <item><description>Cloud blob storage — <c>Storage.Net IBlobStorage</c> (DbFileRepository.cs)</description></item>
    /// </list>
    /// All implementations must be fully async (no blocking .Result/.Wait() calls) and support
    /// cooperative cancellation via <see cref="CancellationToken"/>.
    /// </summary>
    public interface IS3Service
    {
        /// <summary>
        /// Generates a presigned PUT URL for direct client-to-S3 file upload.
        /// Replaces the monolith pattern where <c>DbFileRepository.Create()</c> received <c>byte[] buffer</c>
        /// directly and wrote to PostgreSQL LO / filesystem / blob storage.
        /// </summary>
        /// <param name="objectKey">The S3 object key (use <see cref="GenerateObjectKey"/> to produce).</param>
        /// <param name="contentType">MIME content type for the upload (e.g., "image/png").</param>
        /// <param name="contentLength">Expected content length in bytes for upload size validation.</param>
        /// <param name="expirationMinutes">URL validity period in minutes (default 60).</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
        /// <returns>The presigned upload URL string.</returns>
        Task<string> GeneratePresignedUploadUrlAsync(
            string objectKey,
            string contentType,
            long contentLength,
            int expirationMinutes = 60,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Generates a presigned GET URL for direct client-to-S3 file download.
        /// Replaces the monolith pattern where <c>DbFile.GetBytes()</c> read bytes from PostgreSQL LO,
        /// filesystem, or cloud blob storage.
        /// </summary>
        /// <param name="objectKey">The S3 object key to download.</param>
        /// <param name="expirationMinutes">URL validity period in minutes (default 60).</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
        /// <returns>The presigned download URL string.</returns>
        Task<string> GeneratePresignedDownloadUrlAsync(
            string objectKey,
            int expirationMinutes = 60,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Uploads file content directly to S3 (Lambda-to-S3, server-side upload).
        /// Replaces PostgreSQL LO write, filesystem write, and cloud blob write operations.
        /// Applies AES256 server-side encryption at rest per AAP §0.8.3.
        /// </summary>
        /// <param name="objectKey">The S3 object key to store the file under.</param>
        /// <param name="content">The file content stream.</param>
        /// <param name="contentType">MIME content type of the file.</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
        /// <returns>The S3 PutObject response.</returns>
        Task<PutObjectResponse> UploadFileAsync(
            string objectKey,
            Stream content,
            string contentType,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads file content directly from S3 (server-side download).
        /// Replaces <c>DbFile.GetContentStream()</c> which dispatched across three storage backends.
        /// Caller is responsible for disposing the response stream.
        /// </summary>
        /// <param name="objectKey">The S3 object key to retrieve.</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
        /// <returns>The S3 GetObject response containing the file content stream.</returns>
        Task<GetObjectResponse> DownloadFileAsync(
            string objectKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a file from S3. This operation is idempotent — S3 DeleteObject returns 204
        /// even if the key does not exist. Replaces deletion across all three monolith backends.
        /// </summary>
        /// <param name="objectKey">The S3 object key to delete.</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
        Task DeleteFileAsync(
            string objectKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Copies a file within S3 using server-side copy (no data transfer through Lambda).
        /// Replaces the monolith's read-then-write copy pattern from <c>DbFileRepository.Copy()</c>.
        /// Applies AES256 server-side encryption on the destination object.
        /// </summary>
        /// <param name="sourceObjectKey">The source S3 object key.</param>
        /// <param name="destinationObjectKey">The destination S3 object key.</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
        Task CopyFileAsync(
            string sourceObjectKey,
            string destinationObjectKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Moves a file within S3 (implemented as copy + delete since S3 has no native move).
        /// Replaces the monolith's <c>DbFileRepository.Move()</c> which handled cloud blob copy+delete
        /// and filesystem <c>File.Move()</c>. Idempotent: if source doesn't exist, logs warning and returns.
        /// </summary>
        /// <param name="sourceObjectKey">The source S3 object key.</param>
        /// <param name="destinationObjectKey">The destination S3 object key.</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
        Task MoveFileAsync(
            string sourceObjectKey,
            string destinationObjectKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks whether a file exists in S3 via <c>GetObjectMetadata</c>.
        /// Replaces <c>storage.ExistsAsync(path).Result</c> and <c>File.Exists(path)</c> from the monolith.
        /// </summary>
        /// <param name="objectKey">The S3 object key to check.</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
        /// <returns><c>true</c> if the object exists; <c>false</c> if it returns 404/NoSuchKey.</returns>
        Task<bool> FileExistsAsync(
            string objectKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Detects the MIME content type of a file by its name/extension.
        /// Uses <c>MimeMapping.MimeUtility.GetMimeMapping()</c> — the same library used in the monolith's
        /// <c>UserFileService.cs</c> line 69. Returns "application/octet-stream" as fallback.
        /// </summary>
        /// <param name="fileName">The file name or path to detect MIME type for.</param>
        /// <returns>The detected MIME content type string.</returns>
        Task<string> DetectContentTypeAsync(string fileName);

        /// <summary>
        /// Generates an S3 object key using the sharding pattern from <c>DbFileRepository.GetBlobPath()</c>.
        /// Format: <c>{depth1}/{depth2}/{fileId}{extension}</c> where depth1 and depth2 are derived
        /// from the GUID's first segment for even distribution across S3 partitions.
        /// </summary>
        /// <param name="fileId">The unique file identifier.</param>
        /// <param name="filePath">The logical file path (used to extract the file extension).</param>
        /// <returns>The sharded S3 object key string.</returns>
        string GenerateObjectKey(Guid fileId, string filePath);

        /// <summary>
        /// Generates an S3 object key for temporary files under the <c>tmp/</c> prefix.
        /// The <c>tmp/</c> prefix enables S3 lifecycle rules to auto-delete expired temp files,
        /// replacing the monolith's <c>CleanupExpiredTempFiles()</c> SQL-based cleanup approach.
        /// Format: <c>tmp/{depth1}/{depth2}/{fileId}{extension}</c>.
        /// </summary>
        /// <param name="fileId">The unique file identifier.</param>
        /// <param name="fileName">The file name (used if extension is not provided separately).</param>
        /// <param name="extension">Optional explicit file extension (including leading dot).</param>
        /// <returns>The sharded temporary S3 object key string.</returns>
        string GenerateTempObjectKey(Guid fileId, string fileName, string? extension);

        /// <summary>
        /// Classifies a file into a category based on its MIME content type and file extension.
        /// Replicates the exact classification logic from <c>UserFileService.cs</c> lines 70-89.
        /// Returns one of: "image", "video", "audio", "document", "other".
        /// </summary>
        /// <param name="contentType">The MIME content type (e.g., "image/png").</param>
        /// <param name="fileExtension">The file extension including leading dot (e.g., ".pdf").</param>
        /// <returns>The file type category string.</returns>
        string ClassifyFileType(string contentType, string fileExtension);
    }

    /// <summary>
    /// Production implementation of <see cref="IS3Service"/> providing unified Amazon S3 file
    /// storage operations for the File Management bounded-context microservice.
    /// <para>
    /// This service is the <b>SOLE storage backend</b> for file content, replacing all three
    /// storage backends from the WebVella ERP monolith. It uses the AWS SDK's <see cref="IAmazonS3"/>
    /// client which automatically respects <c>AWS_ENDPOINT_URL</c> for LocalStack compatibility
    /// (no LocalStack-specific code in the service — pure AWS SDK code).
    /// </para>
    /// <para>
    /// Constructor dependencies are provided via DI:
    /// <list type="bullet">
    ///   <item><description><see cref="IAmazonS3"/> — S3 client (pre-configured with endpoint URL at DI level)</description></item>
    ///   <item><description><see cref="ILogger{S3Service}"/> — Structured JSON logging with correlation-ID propagation</description></item>
    ///   <item><description><see cref="IConfiguration"/> — Bucket name and presigned URL expiration configuration</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public class S3Service : IS3Service
    {
        #region Constants

        /// <summary>
        /// Path separator for S3 object keys. Carried from <c>DbFileRepository.FOLDER_SEPARATOR</c>
        /// (source line 14). S3 uses <c>/</c> as folder delimiter for console visualization.
        /// </summary>
        public const string FolderSeparator = "/";

        /// <summary>
        /// Temporary folder prefix name for ephemeral files. Carried from
        /// <c>DbFileRepository.TMP_FOLDER_NAME</c> (source line 15). Used as the S3 key prefix
        /// for lifecycle rule targeting to auto-delete expired temp files.
        /// </summary>
        public const string TmpFolderName = "tmp";

        /// <summary>
        /// Default S3 bucket name when no configuration override is provided.
        /// </summary>
        private const string DefaultBucketName = "file-management-files";

        /// <summary>
        /// Environment variable name for overriding the S3 bucket name.
        /// </summary>
        private const string BucketNameEnvVar = "FILE_MANAGEMENT_BUCKET_NAME";

        /// <summary>
        /// Configuration key for the S3 bucket name (fallback after environment variable).
        /// </summary>
        private const string BucketNameConfigKey = "FileManagement:BucketName";

        /// <summary>
        /// Configuration key for default presigned URL expiration in minutes.
        /// </summary>
        private const string PresignedUrlExpirationConfigKey = "FileManagement:PresignedUrlExpirationMinutes";

        /// <summary>
        /// Default presigned URL expiration in minutes when no configuration is provided.
        /// </summary>
        private const int DefaultPresignedUrlExpirationMinutes = 60;

        /// <summary>
        /// Default MIME type returned when content type detection fails.
        /// </summary>
        private const string DefaultContentType = "application/octet-stream";

        /// <summary>
        /// The set of file extensions classified as "document" type, matching exactly the list
        /// from <c>UserFileService.cs</c> lines 82-84. All 14 extensions must be present.
        /// </summary>
        private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".docx", ".odt", ".rtf", ".txt", ".pdf",
            ".html", ".htm", ".ppt", ".pptx", ".xls", ".xlsx",
            ".ods", ".odp"
        };

        #endregion

        #region Private Fields

        private readonly IAmazonS3 _s3Client;
        private readonly ILogger<S3Service> _logger;
        private readonly string _bucketName;
        private readonly int _defaultPresignedUrlExpirationMinutes;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of <see cref="S3Service"/>.
        /// <para>
        /// The <paramref name="s3Client"/> is expected to be pre-configured at DI level with
        /// <c>AWS_ENDPOINT_URL</c> for LocalStack compatibility. This service contains ZERO
        /// LocalStack-specific logic.
        /// </para>
        /// <para>
        /// Bucket name resolution order:
        /// <list type="number">
        ///   <item><description>Environment variable <c>FILE_MANAGEMENT_BUCKET_NAME</c></description></item>
        ///   <item><description>Configuration key <c>FileManagement:BucketName</c></description></item>
        ///   <item><description>Default: <c>"file-management-files"</c></description></item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="s3Client">The Amazon S3 client for all storage operations.</param>
        /// <param name="logger">Structured logger for operation tracing and error reporting.</param>
        /// <param name="configuration">Application configuration for bucket name and URL expiration.</param>
        /// <exception cref="ArgumentNullException">Thrown if any required parameter is null.</exception>
        public S3Service(IAmazonS3 s3Client, ILogger<S3Service> logger, IConfiguration configuration)
        {
            _s3Client = s3Client ?? throw new ArgumentNullException(nameof(s3Client));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            ArgumentNullException.ThrowIfNull(configuration);

            // Bucket name resolution: env var → config key → default
            // Uses configuration indexer instead of GetValue<T> to avoid IL2026 trimmer warnings for AOT compatibility
            var envBucketName = Environment.GetEnvironmentVariable(BucketNameEnvVar);
            var configBucketName = configuration[BucketNameConfigKey];
            _bucketName = !string.IsNullOrWhiteSpace(envBucketName)
                ? envBucketName
                : !string.IsNullOrWhiteSpace(configBucketName)
                    ? configBucketName
                    : DefaultBucketName;

            var expirationConfig = configuration[PresignedUrlExpirationConfigKey];
            _defaultPresignedUrlExpirationMinutes = int.TryParse(expirationConfig, out var parsedExpiration)
                ? parsedExpiration
                : DefaultPresignedUrlExpirationMinutes;

            _logger.LogInformation(
                "S3Service initialized with bucket {BucketName}, default presigned URL expiration {ExpirationMinutes} minutes",
                _bucketName,
                _defaultPresignedUrlExpirationMinutes);
        }

        #endregion

        #region Presigned URL Generation

        /// <inheritdoc />
        public Task<string> GeneratePresignedUploadUrlAsync(
            string objectKey,
            string contentType,
            long contentLength,
            int expirationMinutes = 60,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

            if (contentLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(contentLength), contentLength,
                    "Content length must be a positive value.");
            }

            try
            {
                var request = new GetPreSignedUrlRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey,
                    Verb = HttpVerb.PUT,
                    Expires = DateTime.UtcNow.AddMinutes(expirationMinutes > 0 ? expirationMinutes : _defaultPresignedUrlExpirationMinutes),
                    ContentType = contentType,
                    ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256
                };

                // Set content-length metadata for upload size validation
                request.Metadata.Add("x-amz-meta-content-length", contentLength.ToString());

                // GetPreSignedURL is synchronous in the AWS SDK — wrap for interface consistency
                var presignedUrl = _s3Client.GetPreSignedURL(request);

                _logger.LogInformation(
                    "Generated presigned upload URL for object {ObjectKey} in bucket {BucketName}, " +
                    "contentType={ContentType}, contentLength={ContentLength}, expirationMinutes={ExpirationMinutes}",
                    objectKey, _bucketName, contentType, contentLength, expirationMinutes);

                return Task.FromResult(presignedUrl);
            }
            catch (AmazonS3Exception ex)
            {
                HandleS3Exception(ex, "GeneratePresignedUploadUrl", objectKey);
                throw; // HandleS3Exception logs and rethrows — this line is for the compiler
            }
        }

        /// <inheritdoc />
        public Task<string> GeneratePresignedDownloadUrlAsync(
            string objectKey,
            int expirationMinutes = 60,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

            try
            {
                var request = new GetPreSignedUrlRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey,
                    Verb = HttpVerb.GET,
                    Expires = DateTime.UtcNow.AddMinutes(expirationMinutes > 0 ? expirationMinutes : _defaultPresignedUrlExpirationMinutes)
                };

                // GetPreSignedURL is synchronous in the AWS SDK — wrap for interface consistency
                var presignedUrl = _s3Client.GetPreSignedURL(request);

                _logger.LogInformation(
                    "Generated presigned download URL for object {ObjectKey} in bucket {BucketName}, " +
                    "expirationMinutes={ExpirationMinutes}",
                    objectKey, _bucketName, expirationMinutes);

                return Task.FromResult(presignedUrl);
            }
            catch (AmazonS3Exception ex)
            {
                HandleS3Exception(ex, "GeneratePresignedDownloadUrl", objectKey);
                throw;
            }
        }

        #endregion

        #region Direct S3 Operations

        /// <inheritdoc />
        public async Task<PutObjectResponse> UploadFileAsync(
            string objectKey,
            Stream content,
            string contentType,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
            ArgumentNullException.ThrowIfNull(content);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

            try
            {
                var request = new PutObjectRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey,
                    InputStream = content,
                    ContentType = contentType,
                    ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256
                };

                var response = await _s3Client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Uploaded file to S3: object {ObjectKey} in bucket {BucketName}, contentType={ContentType}",
                    objectKey, _bucketName, contentType);

                return response;
            }
            catch (AmazonS3Exception ex)
            {
                HandleS3Exception(ex, "UploadFile", objectKey);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<GetObjectResponse> DownloadFileAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

            try
            {
                var request = new GetObjectRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey
                };

                var response = await _s3Client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Downloaded file from S3: object {ObjectKey} in bucket {BucketName}, " +
                    "contentLength={ContentLength}, contentType={ContentType}",
                    objectKey, _bucketName, response.ContentLength, response.Headers.ContentType);

                return response;
            }
            catch (AmazonS3Exception ex)
            {
                HandleS3Exception(ex, "DownloadFile", objectKey);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeleteFileAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

            try
            {
                var request = new DeleteObjectRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey
                };

                // S3 DeleteObject is idempotent — returns 204 even if key doesn't exist.
                // This matches the desired idempotency per AAP §0.8.5.
                await _s3Client.DeleteObjectAsync(request, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Deleted file from S3: object {ObjectKey} in bucket {BucketName}",
                    objectKey, _bucketName);
            }
            catch (AmazonS3Exception ex)
            {
                HandleS3Exception(ex, "DeleteFile", objectKey);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task CopyFileAsync(
            string sourceObjectKey,
            string destinationObjectKey,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceObjectKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationObjectKey);

            try
            {
                var request = new CopyObjectRequest
                {
                    SourceBucket = _bucketName,
                    SourceKey = sourceObjectKey,
                    DestinationBucket = _bucketName,
                    DestinationKey = destinationObjectKey,
                    ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256
                };

                // Server-side copy — no data transfer through Lambda. This significantly
                // improves performance vs. the monolith's read-then-write pattern
                // (DbFileRepository.Copy lines 269-270: GetBytes → Create).
                await _s3Client.CopyObjectAsync(request, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Copied file in S3: source {SourceObjectKey} → destination {DestinationObjectKey} " +
                    "in bucket {BucketName}",
                    sourceObjectKey, destinationObjectKey, _bucketName);
            }
            catch (AmazonS3Exception ex)
            {
                HandleS3Exception(ex, "CopyFile", sourceObjectKey);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task MoveFileAsync(
            string sourceObjectKey,
            string destinationObjectKey,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceObjectKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationObjectKey);

            // Guard: skip self-move to prevent the copy-to-self + delete pattern from
            // destroying the only copy of the file.  This legitimately occurs when a file
            // is "moved" to a new logical path that shares the same extension — because
            // GenerateObjectKey() is deterministic on (fileId, extension), the S3 key
            // does not change even though the DynamoDB metadata filepath does.
            // Mirrors the monolith's File.Move() behaviour which is a no-op when
            // source == destination.
            if (string.Equals(sourceObjectKey, destinationObjectKey, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Move skipped: source and destination S3 keys are identical ({ObjectKey}) " +
                    "in bucket {BucketName}. Metadata-only move — no S3 operation required",
                    sourceObjectKey, _bucketName);
                return;
            }

            try
            {
                // S3 has no native move — implement as copy + delete.
                // This replaces the monolith's Move() which handled:
                //   - Cloud blob: storage.WriteAsync(dest, original).Wait() + storage.DeleteAsync(src).Wait()
                //   - Filesystem: File.Move(src, dest)
                await CopyFileAsync(sourceObjectKey, destinationObjectKey, cancellationToken).ConfigureAwait(false);
                await DeleteFileAsync(sourceObjectKey, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Moved file in S3: source {SourceObjectKey} → destination {DestinationObjectKey} " +
                    "in bucket {BucketName}",
                    sourceObjectKey, destinationObjectKey, _bucketName);
            }
            catch (AmazonS3Exception ex) when (
                string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase) ||
                ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Source doesn't exist — log warning and return without throwing (idempotent behavior).
                // This mirrors how the monolith's Delete() handled missing files: if (file == null) return;
                _logger.LogWarning(
                    "Move operation skipped: source object {SourceObjectKey} not found in bucket {BucketName}. " +
                    "Operation is idempotent — no action taken",
                    sourceObjectKey, _bucketName);
            }
        }

        /// <inheritdoc />
        public async Task<bool> FileExistsAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

            try
            {
                var request = new GetObjectMetadataRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey
                };

                // If GetObjectMetadataAsync succeeds (HTTP 200), the object exists.
                await _s3Client.GetObjectMetadataAsync(request, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "File exists check: object {ObjectKey} EXISTS in bucket {BucketName}",
                    objectKey, _bucketName);

                return true;
            }
            catch (AmazonS3Exception ex) when (
                ex.StatusCode == HttpStatusCode.NotFound ||
                string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ex.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase))
            {
                // 404 / NoSuchKey means the object does not exist — this is expected, not an error.
                _logger.LogInformation(
                    "File exists check: object {ObjectKey} NOT FOUND in bucket {BucketName}",
                    objectKey, _bucketName);

                return false;
            }
            catch (AmazonS3Exception ex)
            {
                // Any other S3 exception (e.g., NoSuchBucket, AccessDenied) is a real error.
                HandleS3Exception(ex, "FileExists", objectKey);
                throw;
            }
        }

        #endregion

        #region Content Type Detection and File Classification

        /// <inheritdoc />
        public Task<string> DetectContentTypeAsync(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return Task.FromResult(DefaultContentType);
            }

            // Use MimeMapping.MimeUtility.GetMimeMapping() — the same library and call pattern
            // from the monolith's UserFileService.cs line 69:
            //   var mimeType = MimeMapping.MimeUtility.GetMimeMapping(path);
            var mimeType = MimeMapping.MimeUtility.GetMimeMapping(fileName);

            // MimeMapping returns "application/octet-stream" for unknown types, which is our
            // desired fallback behavior.
            var result = string.IsNullOrWhiteSpace(mimeType) ? DefaultContentType : mimeType;

            return Task.FromResult(result);
        }

        /// <inheritdoc />
        public string ClassifyFileType(string contentType, string fileExtension)
        {
            // Replicate EXACT classification logic from UserFileService.cs lines 70-89.
            // The source code checked contentType.StartsWith("image") etc. for MIME-based
            // categories, then fell back to extension-based document detection.

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                if (contentType.StartsWith("image", StringComparison.OrdinalIgnoreCase))
                {
                    return "image";
                }

                if (contentType.StartsWith("video", StringComparison.OrdinalIgnoreCase))
                {
                    return "video";
                }

                if (contentType.StartsWith("audio", StringComparison.OrdinalIgnoreCase))
                {
                    return "audio";
                }
            }

            // Extension-based document classification.
            // Source (UserFileService.cs lines 82-84) checked 14 specific extensions:
            //   .doc, .docx, .odt, .rtf, .txt, .pdf, .html, .htm, .ppt, .pptx, .xls, .xlsx, .ods, .odp
            // Using case-insensitive comparison via HashSet with OrdinalIgnoreCase comparer.
            // The monolith used case-sensitive == but the filepath was already lowercased
            // (filepath.ToLowerInvariant()) — we normalize to achieve the same net behavior.
            if (!string.IsNullOrWhiteSpace(fileExtension))
            {
                var normalizedExtension = fileExtension.Trim();
                if (!normalizedExtension.StartsWith('.'))
                {
                    normalizedExtension = "." + normalizedExtension;
                }

                if (DocumentExtensions.Contains(normalizedExtension))
                {
                    return "document";
                }
            }

            return "other";
        }

        #endregion

        #region S3 Key Generation

        /// <inheritdoc />
        public string GenerateObjectKey(Guid fileId, string filePath)
        {
            // Replicate the EXACT sharding pattern from DbFileRepository.GetBlobPath()
            // (source lines 496-508):
            //
            //   var guidIinitialPart = file.Id.ToString().Split(new[] { '-' })[0];   // e.g. "a1b2c3d4"
            //   var fileName = file.FilePath.Split(new[] { '/' }).Last();              // e.g. "readme.txt"
            //   var depth1Folder = guidIinitialPart.Substring(0, 2);                   // e.g. "a1"
            //   var depth2Folder = guidIinitialPart.Substring(2, 2);                   // e.g. "b2"
            //   string filenameExt = Path.GetExtension(fileName);                      // e.g. ".txt"
            //   if (!string.IsNullOrWhiteSpace(filenameExt))
            //       return StoragePath.Combine(depth1Folder, depth2Folder, file.Id + filenameExt);
            //   else
            //       return StoragePath.Combine(depth1Folder, depth2Folder, file.Id.ToString());
            //
            // NOTE: We follow the GetBlobPath() pattern (file.Id + filenameExt) which does NOT
            // have the double-dot bug from GetFileSystemPath() (file.Id + "." + filenameExt).
            // Path.GetExtension includes the leading dot, so "file.Id + filenameExt" correctly
            // produces "a1b2c3d4-e5f6-...-7890.txt".

            if (fileId == Guid.Empty)
            {
                throw new ArgumentException("File ID must not be an empty GUID.", nameof(fileId));
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must not be null or empty.", nameof(filePath));
            }

            var fileIdString = fileId.ToString();
            var guidInitialPart = fileIdString.Split('-')[0]; // First 8 hex chars before first hyphen

            var depth1 = guidInitialPart.Substring(0, 2);
            var depth2 = guidInitialPart.Substring(2, 2);

            // Extract file extension from the file path (or file name)
            var extension = string.Empty;
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                // Extract just the filename portion (last segment after '/')
                var fileName = filePath.Split('/').Last();
                extension = Path.GetExtension(fileName);
            }

            // Build the S3 key: {depth1}/{depth2}/{fileId}{extension}
            // Using string concatenation with "/" instead of StoragePath.Combine (Storage.Net removed)
            if (!string.IsNullOrWhiteSpace(extension))
            {
                return $"{depth1}{FolderSeparator}{depth2}{FolderSeparator}{fileIdString}{extension}";
            }

            return $"{depth1}{FolderSeparator}{depth2}{FolderSeparator}{fileIdString}";
        }

        /// <inheritdoc />
        public string GenerateTempObjectKey(Guid fileId, string fileName, string? extension)
        {
            // Generates S3 keys for temp files under "tmp/" prefix.
            // This enables S3 lifecycle rules to auto-delete expired temp files, replacing
            // the monolith's CleanupExpiredTempFiles() SQL-based cleanup approach
            // (source DbFileRepository.cs lines 455-469).
            //
            // Source pattern from CreateTempFile() (lines 437-449):
            //   FOLDER_SEPARATOR + TMP_FOLDER_NAME + FOLDER_SEPARATOR + section + FOLDER_SEPARATOR + filename + extension
            //
            // Target pattern: tmp/{depth1}/{depth2}/{fileId}{extension}

            // Normalize the extension
            var normalizedExtension = string.Empty;
            if (!string.IsNullOrWhiteSpace(extension))
            {
                normalizedExtension = extension.Trim().ToLowerInvariant();
                if (!normalizedExtension.StartsWith('.'))
                {
                    normalizedExtension = "." + normalizedExtension;
                }
            }
            else if (!string.IsNullOrWhiteSpace(fileName))
            {
                // If no explicit extension, extract from fileName
                normalizedExtension = Path.GetExtension(fileName);
            }

            // Build a path that includes the extension for GenerateObjectKey
            var syntheticPath = !string.IsNullOrWhiteSpace(normalizedExtension)
                ? $"temp{normalizedExtension}"
                : fileName ?? string.Empty;

            // Generate the base sharded key and prepend "tmp/"
            var baseKey = GenerateObjectKey(fileId, syntheticPath);
            return $"{TmpFolderName}{FolderSeparator}{baseKey}";
        }

        #endregion

        #region Error Handling

        /// <summary>
        /// Centralized error handler for <see cref="AmazonS3Exception"/> errors.
        /// Logs errors with structured context and classifies them by severity:
        /// <list type="bullet">
        ///   <item><description><c>NoSuchBucket</c> — Critical infrastructure error (logged at Critical level)</description></item>
        ///   <item><description><c>AccessDenied</c> — Security violation (logged at Warning level)</description></item>
        ///   <item><description>All others — Operational error (logged at Error level)</description></item>
        /// </list>
        /// The exception is always re-thrown after logging for Lambda error reporting.
        /// </summary>
        /// <param name="ex">The Amazon S3 exception to handle.</param>
        /// <param name="operation">The name of the operation that failed (for structured logging).</param>
        /// <param name="objectKey">The S3 object key involved in the failed operation.</param>
        private void HandleS3Exception(AmazonS3Exception ex, string operation, string objectKey)
        {
            if (string.Equals(ex.ErrorCode, "NoSuchBucket", StringComparison.OrdinalIgnoreCase))
            {
                // NoSuchBucket is a critical infrastructure issue — the S3 bucket doesn't exist.
                // This indicates a CDK deployment or configuration failure.
                _logger.LogCritical(
                    ex,
                    "CRITICAL: S3 bucket not found during {Operation}. " +
                    "Bucket={BucketName}, ObjectKey={ObjectKey}, ErrorCode={ErrorCode}, " +
                    "StatusCode={StatusCode}, RequestId={RequestId}",
                    operation, _bucketName, objectKey, ex.ErrorCode,
                    (int)ex.StatusCode, ex.RequestId);
            }
            else if (string.Equals(ex.ErrorCode, "AccessDenied", StringComparison.OrdinalIgnoreCase))
            {
                // AccessDenied is a security issue — IAM permissions are insufficient.
                _logger.LogWarning(
                    ex,
                    "Access denied during S3 {Operation}. " +
                    "Bucket={BucketName}, ObjectKey={ObjectKey}, ErrorCode={ErrorCode}, " +
                    "StatusCode={StatusCode}, RequestId={RequestId}. " +
                    "Verify Lambda IAM role has required S3 permissions",
                    operation, _bucketName, objectKey, ex.ErrorCode,
                    (int)ex.StatusCode, ex.RequestId);
            }
            else
            {
                // General S3 operational error — log at Error level.
                _logger.LogError(
                    ex,
                    "S3 operation {Operation} failed. " +
                    "Bucket={BucketName}, ObjectKey={ObjectKey}, ErrorCode={ErrorCode}, " +
                    "StatusCode={StatusCode}, RequestId={RequestId}",
                    operation, _bucketName, objectKey, ex.ErrorCode,
                    (int)ex.StatusCode, ex.RequestId);
            }

            // Always rethrow — do NOT swallow exceptions. Lambda error reporting
            // and API Gateway error mapping depend on exceptions propagating up.
            throw ex;
        }

        #endregion
    }
}
