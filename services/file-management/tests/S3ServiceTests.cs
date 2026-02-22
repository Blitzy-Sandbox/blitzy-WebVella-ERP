using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.FileManagement.Models;
using WebVellaErp.FileManagement.Services;
using Xunit;

namespace WebVellaErp.FileManagement.Tests
{
    /// <summary>
    /// Unit tests for <see cref="S3Service"/> — the file storage service that replaces
    /// all three monolith storage backends (PostgreSQL Large Objects, filesystem, and cloud blob).
    /// Uses Moq to mock IAmazonS3 — no real S3 calls are made.
    /// Tests cover: presigned URL generation, S3 operations (upload/download/copy/move/delete),
    /// object key generation (GUID-based sharding matching monolith GetBlobPath pattern),
    /// MIME type detection, and file type classification.
    /// </summary>
    public class S3ServiceTests
    {
        #region Test Infrastructure

        private readonly Mock<IAmazonS3> _mockS3Client;
        private readonly Mock<ILogger<S3Service>> _mockLogger;
        private readonly Mock<IConfiguration> _mockConfig;
        private readonly S3Service _service;
        private const string TestBucketName = "test-file-bucket";

        /// <summary>
        /// Initializes the test class with mocked dependencies for S3Service.
        /// Clears FILE_MANAGEMENT_BUCKET_NAME env var to ensure config-based bucket resolution.
        /// </summary>
        public S3ServiceTests()
        {
            _mockS3Client = new Mock<IAmazonS3>();
            _mockLogger = new Mock<ILogger<S3Service>>();
            _mockConfig = new Mock<IConfiguration>();

            // Clear environment variable to ensure config-based bucket name resolution
            // (env var takes priority over config in S3Service constructor)
            Environment.SetEnvironmentVariable("FILE_MANAGEMENT_BUCKET_NAME", null);

            // Configure mock to return test bucket name via configuration indexer
            // S3Service uses configuration[key] indexer — NOT GetValue<T>() — for AOT compatibility
            _mockConfig.Setup(c => c["FileManagement:BucketName"]).Returns(TestBucketName);
            _mockConfig.Setup(c => c["FileManagement:PresignedUrlExpirationMinutes"]).Returns("60");

            _service = new S3Service(_mockS3Client.Object, _mockLogger.Object, _mockConfig.Object);
        }

        #endregion

        #region Phase 2: GeneratePresignedUploadUrlAsync Tests

        /// <summary>
        /// Verifies presigned upload URL generation returns a non-empty URL.
        /// Replaces direct byte upload from DbFileRepository.Create() (source lines 140-188).
        /// </summary>
        [Fact]
        public async Task GeneratePresignedUploadUrl_ReturnsNonEmptyUrl()
        {
            // Arrange
            _mockS3Client.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                .Returns("https://s3.amazonaws.com/test-file-bucket/test-key?X-Amz-Signature=abc123");

            // Act
            var result = await _service.GeneratePresignedUploadUrlAsync("test-key", "image/png", 1024);

            // Assert
            result.Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Verifies the presigned URL request targets the configured bucket name.
        /// </summary>
        [Fact]
        public async Task GeneratePresignedUploadUrl_SetsCorrectBucket()
        {
            // Arrange
            GetPreSignedUrlRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                .Callback<GetPreSignedUrlRequest>(r => capturedRequest = r)
                .Returns("https://test-url.com");

            // Act
            await _service.GeneratePresignedUploadUrlAsync("test-key", "image/png", 1024);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest.BucketName.Should().Be(TestBucketName);
        }

        /// <summary>
        /// Verifies the presigned URL request uses the exact object key provided.
        /// </summary>
        [Fact]
        public async Task GeneratePresignedUploadUrl_SetsCorrectObjectKey()
        {
            // Arrange
            var objectKey = "a1/b2/a1b2c3d4-e5f6-7890-abcd-ef1234567890.png";
            GetPreSignedUrlRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                .Callback<GetPreSignedUrlRequest>(r => capturedRequest = r)
                .Returns("https://test-url.com");

            // Act
            await _service.GeneratePresignedUploadUrlAsync(objectKey, "image/png", 1024);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest.Key.Should().Be(objectKey);
        }

        /// <summary>
        /// Verifies presigned upload URL uses HTTP PUT verb for upload operations.
        /// </summary>
        [Fact]
        public async Task GeneratePresignedUploadUrl_UsesPutVerb()
        {
            // Arrange
            GetPreSignedUrlRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                .Callback<GetPreSignedUrlRequest>(r => capturedRequest = r)
                .Returns("https://test-url.com");

            // Act
            await _service.GeneratePresignedUploadUrlAsync("test-key", "image/png", 1024);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest.Verb.Should().Be(HttpVerb.PUT);
        }

        /// <summary>
        /// Verifies presigned upload URL has correct expiration set.
        /// </summary>
        [Fact]
        public async Task GeneratePresignedUploadUrl_SetsExpiration()
        {
            // Arrange
            var expirationMinutes = 30;
            GetPreSignedUrlRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                .Callback<GetPreSignedUrlRequest>(r => capturedRequest = r)
                .Returns("https://test-url.com");

            // Act
            var beforeCall = DateTime.UtcNow;
            await _service.GeneratePresignedUploadUrlAsync("test-key", "image/png", 1024, expirationMinutes);
            var afterCall = DateTime.UtcNow;

            // Assert — expiration should be approximately 30 minutes from now
            capturedRequest.Should().NotBeNull();
            capturedRequest.Expires.Should().BeAfter(beforeCall.AddMinutes(expirationMinutes - 1));
            capturedRequest.Expires.Should().BeBefore(afterCall.AddMinutes(expirationMinutes + 1));
        }

        /// <summary>
        /// Verifies presigned upload URL includes the correct content type header.
        /// </summary>
        [Fact]
        public async Task GeneratePresignedUploadUrl_SetsContentType()
        {
            // Arrange
            var contentType = "image/png";
            GetPreSignedUrlRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                .Callback<GetPreSignedUrlRequest>(r => capturedRequest = r)
                .Returns("https://test-url.com");

            // Act
            await _service.GeneratePresignedUploadUrlAsync("test-key", contentType, 1024);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest.ContentType.Should().Be(contentType);
        }

        #endregion

        #region Phase 3: GeneratePresignedDownloadUrlAsync Tests

        /// <summary>
        /// Verifies presigned download URL generation returns a non-empty URL.
        /// Replaces DbFile.GetContentStream() (source DbFile.cs lines 36-71).
        /// </summary>
        [Fact]
        public async Task GeneratePresignedDownloadUrl_ReturnsNonEmptyUrl()
        {
            // Arrange
            _mockS3Client.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                .Returns("https://s3.amazonaws.com/test-file-bucket/test-key?X-Amz-Signature=xyz789");

            // Act
            var result = await _service.GeneratePresignedDownloadUrlAsync("test-key");

            // Assert
            result.Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Verifies presigned download URL uses HTTP GET verb.
        /// </summary>
        [Fact]
        public async Task GeneratePresignedDownloadUrl_UsesGetVerb()
        {
            // Arrange
            GetPreSignedUrlRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                .Callback<GetPreSignedUrlRequest>(r => capturedRequest = r)
                .Returns("https://test-url.com");

            // Act
            await _service.GeneratePresignedDownloadUrlAsync("test-key");

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest.Verb.Should().Be(HttpVerb.GET);
        }

        /// <summary>
        /// Verifies presigned download URL has correct expiration configured.
        /// </summary>
        [Fact]
        public async Task GeneratePresignedDownloadUrl_SetsExpiration()
        {
            // Arrange
            var expirationMinutes = 15;
            GetPreSignedUrlRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                .Callback<GetPreSignedUrlRequest>(r => capturedRequest = r)
                .Returns("https://test-url.com");

            // Act
            var beforeCall = DateTime.UtcNow;
            await _service.GeneratePresignedDownloadUrlAsync("test-key", expirationMinutes);
            var afterCall = DateTime.UtcNow;

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest.Expires.Should().BeAfter(beforeCall.AddMinutes(expirationMinutes - 1));
            capturedRequest.Expires.Should().BeBefore(afterCall.AddMinutes(expirationMinutes + 1));
        }

        #endregion

        #region Phase 4: UploadFileAsync Tests

        /// <summary>
        /// Verifies upload calls PutObjectAsync on the S3 client.
        /// Replaces PostgreSQL LO write, filesystem write, and blob write (source lines 140-188).
        /// </summary>
        [Fact]
        public async Task UploadFile_CallsPutObjectAsync()
        {
            // Arrange
            _mockS3Client.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutObjectResponse());

            using var stream = new MemoryStream(new byte[] { 0x01, 0x02, 0x03 });

            // Act
            await _service.UploadFileAsync("test-key", stream, "application/pdf");

            // Assert
            _mockS3Client.Verify(
                x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies upload sets AES256 server-side encryption.
        /// Per AAP §0.8.3: encryption at rest for all datastores.
        /// </summary>
        [Fact]
        public async Task UploadFile_SetsServerSideEncryption()
        {
            // Arrange
            PutObjectRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutObjectRequest, CancellationToken>((r, _) => capturedRequest = r)
                .ReturnsAsync(new PutObjectResponse());

            using var stream = new MemoryStream(new byte[] { 0x01, 0x02 });

            // Act
            await _service.UploadFileAsync("test-key", stream, "image/png");

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest.ServerSideEncryptionMethod.Should().Be(ServerSideEncryptionMethod.AES256);
        }

        /// <summary>
        /// Verifies upload sets the correct content type on the S3 object.
        /// </summary>
        [Fact]
        public async Task UploadFile_SetsContentType()
        {
            // Arrange
            var contentType = "application/pdf";
            PutObjectRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutObjectRequest, CancellationToken>((r, _) => capturedRequest = r)
                .ReturnsAsync(new PutObjectResponse());

            using var stream = new MemoryStream(new byte[] { 0x01 });

            // Act
            await _service.UploadFileAsync("test-key", stream, contentType);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest.ContentType.Should().Be(contentType);
        }

        #endregion

        #region Phase 5: DownloadFileAsync Tests

        /// <summary>
        /// Verifies download calls GetObjectAsync on the S3 client.
        /// Replaces all three backend reads from DbFile.GetContentStream() (source DbFile.cs lines 36-71).
        /// </summary>
        [Fact]
        public async Task DownloadFile_CallsGetObjectAsync()
        {
            // Arrange
            _mockS3Client.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetObjectResponse());

            // Act
            await _service.DownloadFileAsync("test-key");

            // Assert
            _mockS3Client.Verify(
                x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies download uses the correct bucket name and object key.
        /// </summary>
        [Fact]
        public async Task DownloadFile_UsesCorrectBucketAndKey()
        {
            // Arrange
            var objectKey = "a1/b2/test-file-id.pdf";
            GetObjectRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<GetObjectRequest, CancellationToken>((r, _) => capturedRequest = r)
                .ReturnsAsync(new GetObjectResponse());

            // Act
            await _service.DownloadFileAsync(objectKey);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest.BucketName.Should().Be(TestBucketName);
            capturedRequest.Key.Should().Be(objectKey);
        }

        #endregion

        #region Phase 6: DeleteFileAsync Tests

        /// <summary>
        /// Verifies delete calls DeleteObjectAsync on the S3 client.
        /// Replaces three backend deletions from DbFileRepository.Delete() (source lines 395-414).
        /// </summary>
        [Fact]
        public async Task DeleteFile_CallsDeleteObjectAsync()
        {
            // Arrange
            _mockS3Client.Setup(x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteObjectResponse());

            // Act
            await _service.DeleteFileAsync("test-key");

            // Assert
            _mockS3Client.Verify(
                x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies S3 DeleteObject idempotency — no exception when key doesn't exist.
        /// S3 returns 204 even for missing keys; service must maintain this behavior.
        /// </summary>
        [Fact]
        public async Task DeleteFile_IsIdempotent_NoExceptionOnMissing()
        {
            // Arrange — mock returns normally (mimicking S3 204 response on missing key)
            _mockS3Client.Setup(x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteObjectResponse());

            // Act & Assert — should not throw
            Func<Task> act = () => _service.DeleteFileAsync("nonexistent-key");
            await act.Should().NotThrowAsync();
        }

        #endregion

        #region Phase 7: CopyFileAsync Tests

        /// <summary>
        /// Verifies copy calls CopyObjectAsync on the S3 client.
        /// Replaces read-then-write from DbFileRepository.Copy() (source lines 269-270).
        /// </summary>
        [Fact]
        public async Task CopyFile_CallsCopyObjectAsync()
        {
            // Arrange
            _mockS3Client.Setup(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CopyObjectResponse());

            // Act
            await _service.CopyFileAsync("src-key", "dst-key");

            // Assert
            _mockS3Client.Verify(
                x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies copy sets AES256 server-side encryption on the destination object.
        /// Per AAP §0.8.3: encryption at rest for all datastores.
        /// </summary>
        [Fact]
        public async Task CopyFile_SetsServerSideEncryption()
        {
            // Arrange
            CopyObjectRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<CopyObjectRequest, CancellationToken>((r, _) => capturedRequest = r)
                .ReturnsAsync(new CopyObjectResponse());

            // Act
            await _service.CopyFileAsync("src-key", "dst-key");

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest.ServerSideEncryptionMethod.Should().Be(ServerSideEncryptionMethod.AES256);
        }

        /// <summary>
        /// Verifies copy uses the same bucket for source and destination (intra-bucket copy).
        /// </summary>
        [Fact]
        public async Task CopyFile_UsesSameBucketForSourceAndDestination()
        {
            // Arrange
            CopyObjectRequest capturedRequest = null!;
            _mockS3Client.Setup(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<CopyObjectRequest, CancellationToken>((r, _) => capturedRequest = r)
                .ReturnsAsync(new CopyObjectResponse());

            // Act
            await _service.CopyFileAsync("src-key", "dst-key");

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest.SourceBucket.Should().Be(TestBucketName);
            capturedRequest.DestinationBucket.Should().Be(TestBucketName);
            capturedRequest.SourceBucket.Should().Be(capturedRequest.DestinationBucket);
        }

        #endregion

        #region Phase 8: MoveFileAsync Tests

        /// <summary>
        /// Verifies move executes copy-then-delete in the correct order.
        /// Replaces blob copy+delete from DbFileRepository.Move() (source lines 329-344).
        /// </summary>
        [Fact]
        public async Task MoveFile_CopiesThenDeletes()
        {
            // Arrange — track call order to verify copy-before-delete
            var callSequence = new List<string>();

            _mockS3Client.Setup(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<CopyObjectRequest, CancellationToken>((_, _) => callSequence.Add("CopyObject"))
                .ReturnsAsync(new CopyObjectResponse());

            _mockS3Client.Setup(x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<DeleteObjectRequest, CancellationToken>((_, _) => callSequence.Add("DeleteObject"))
                .ReturnsAsync(new DeleteObjectResponse());

            // Act
            await _service.MoveFileAsync("src-key", "dst-key");

            // Assert — copy must happen before delete
            callSequence.Should().Equal("CopyObject", "DeleteObject");
        }

        /// <summary>
        /// Verifies move handles missing source gracefully — logs warning, does not throw.
        /// When source doesn't exist, CopyObjectAsync throws NoSuchKey. MoveFileAsync
        /// catches this and logs a warning instead of propagating (idempotent behavior).
        /// </summary>
        [Fact]
        public async Task MoveFile_WhenSourceMissing_LogsWarning_NoException()
        {
            // Arrange — CopyObjectAsync throws NoSuchKey (source key doesn't exist)
            _mockS3Client.Setup(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonS3Exception(
                    "The specified key does not exist.")
                {
                    ErrorCode = "NoSuchKey",
                    StatusCode = HttpStatusCode.NotFound
                });

            // Act & Assert — should not throw (graceful handling)
            Func<Task> act = () => _service.MoveFileAsync("nonexistent-src", "dst-key");
            await act.Should().NotThrowAsync();

            // Verify delete was NOT called (copy failed before reaching delete step)
            _mockS3Client.Verify(
                x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        #endregion

        #region Phase 9: FileExistsAsync Tests

        /// <summary>
        /// Verifies FileExists returns true when S3 object metadata is retrievable.
        /// Replaces storage.ExistsAsync() (source line 400) and File.Exists() (source line 408).
        /// </summary>
        [Fact]
        public async Task FileExists_WhenExists_ReturnsTrue()
        {
            // Arrange — GetObjectMetadataAsync succeeds → object exists
            _mockS3Client.Setup(x => x.GetObjectMetadataAsync(
                    It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetObjectMetadataResponse());

            // Act
            var result = await _service.FileExistsAsync("existing-key");

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies FileExists returns false when S3 object doesn't exist (404/NoSuchKey).
        /// </summary>
        [Fact]
        public async Task FileExists_WhenNotExists_ReturnsFalse()
        {
            // Arrange — GetObjectMetadataAsync throws 404/NoSuchKey → object doesn't exist
            _mockS3Client.Setup(x => x.GetObjectMetadataAsync(
                    It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonS3Exception(
                    "The specified key does not exist.")
                {
                    ErrorCode = "NoSuchKey",
                    StatusCode = HttpStatusCode.NotFound
                });

            // Act
            var result = await _service.FileExistsAsync("nonexistent-key");

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies FileExists propagates non-404 S3 errors (e.g., 500 InternalServerError).
        /// </summary>
        [Fact]
        public async Task FileExists_OnOtherS3Error_Rethrows()
        {
            // Arrange — GetObjectMetadataAsync throws 500 (non-404 error) → should propagate
            _mockS3Client.Setup(x => x.GetObjectMetadataAsync(
                    It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonS3Exception(
                    "Internal server error.")
                {
                    ErrorCode = "InternalError",
                    StatusCode = HttpStatusCode.InternalServerError
                });

            // Act & Assert
            Func<Task> act = () => _service.FileExistsAsync("test-key");
            await act.Should().ThrowAsync<AmazonS3Exception>();
        }

        #endregion

        #region Phase 10: GenerateObjectKey Tests (CRITICAL — Must Match Monolith Sharding)

        /// <summary>
        /// Verifies S3 object key uses correct GUID-based sharding pattern.
        /// Must match monolith's GetBlobPath() (source DbFileRepository.cs lines 496-508):
        /// depth1 = first 2 hex chars, depth2 = next 2 hex chars of GUID.
        /// Pattern: {depth1}/{depth2}/{fileId}{extension}
        /// Cross-validates with FileMetadata.GenerateObjectKey() for consistency.
        /// </summary>
        [Fact]
        public void GenerateObjectKey_UsesCorrectShardingPattern()
        {
            // Arrange
            var fileId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
            var filePath = "/file/readme.txt";

            // Act
            var result = _service.GenerateObjectKey(fileId, filePath);

            // Assert — must match monolith GetBlobPath() pattern exactly
            result.Should().Be("a1/b2/a1b2c3d4-e5f6-7890-abcd-ef1234567890.txt");

            // Cross-validate with FileMetadata.GenerateObjectKey (same sharding logic)
            var metadataResult = FileMetadata.GenerateObjectKey(fileId, filePath);
            metadataResult.Should().Be(result);
        }

        /// <summary>
        /// Verifies object key correctly extracts file extension from the provided file path.
        /// </summary>
        [Fact]
        public void GenerateObjectKey_ExtractsExtensionFromFilePath()
        {
            // Arrange
            var fileId = Guid.NewGuid();
            var filePath = "/file/document.pdf";

            // Act
            var result = _service.GenerateObjectKey(fileId, filePath);

            // Assert
            result.Should().EndWith(".pdf");
        }

        /// <summary>
        /// Verifies object key handles files with no extension gracefully.
        /// Source GetBlobPath() lines 506-507: returns just fileId with no extension.
        /// Also exercises FileMetadata.NormalizeFilePath for path normalization.
        /// </summary>
        [Fact]
        public void GenerateObjectKey_HandlesNoExtension()
        {
            // Arrange
            var fileId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
            var normalizedPath = FileMetadata.NormalizeFilePath("file/noextension");

            // Act
            var result = _service.GenerateObjectKey(fileId, normalizedPath);

            // Assert — key should end with GUID only, no dot
            var guidStr = fileId.ToString();
            result.Should().EndWith(guidStr);
            result.Should().NotContain(".");
        }

        /// <summary>
        /// CRITICAL regression test: verifies NO double-dot bug.
        /// Source GetFileSystemPath() had a known bug (lines 482-488) where Path.GetExtension()
        /// includes the dot, then code added another dot creating "fileId..txt".
        /// GetBlobPath() (which S3Service follows) does NOT have this bug. Verify NO double dots.
        /// </summary>
        [Fact]
        public void GenerateObjectKey_DoesNotAddDoubleDot()
        {
            // Arrange
            var fileId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
            var filePath = "/file/test.txt";

            // Act
            var result = _service.GenerateObjectKey(fileId, filePath);

            // Assert — no double dots anywhere in the key
            result.Should().NotContain("..");
            result.Should().Contain(".txt");
            result.Should().Be("a1/b2/a1b2c3d4-e5f6-7890-abcd-ef1234567890.txt");
        }

        #endregion

        #region Phase 11: GenerateTempObjectKey Tests

        /// <summary>
        /// Verifies temp object key is prefixed with "tmp/".
        /// Replaces DbFileRepository.CreateTempFile() path generation (source lines 437-449).
        /// </summary>
        [Fact]
        public void GenerateTempObjectKey_PrefixesWithTmp()
        {
            // Arrange
            var fileId = Guid.NewGuid();

            // Act
            var result = _service.GenerateTempObjectKey(fileId, "upload.png", ".png");

            // Assert
            result.Should().StartWith("tmp/");
        }

        /// <summary>
        /// Verifies temp object key uses sharding under the tmp/ prefix.
        /// Pattern: tmp/{depth1}/{depth2}/{fileId}{ext}
        /// </summary>
        [Fact]
        public void GenerateTempObjectKey_UsesShardingUnderTmp()
        {
            // Arrange
            var fileId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

            // Act
            var result = _service.GenerateTempObjectKey(fileId, "document.pdf", ".pdf");

            // Assert — should follow tmp/{depth1}/{depth2}/{fileId}{ext} pattern
            result.Should().StartWith("tmp/");
            result.Should().Contain("a1/b2/");
            result.Should().Contain("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
            result.Should().EndWith(".pdf");
        }

        #endregion

        #region Phase 12: ClassifyFileType Tests

        /// <summary>
        /// Verifies image MIME types are classified as "image".
        /// Mirrors source UserFileService.cs line 70: mimeType.StartsWith("image").
        /// </summary>
        [Fact]
        public void ClassifyFileType_Image_ReturnsImage()
        {
            // Act
            var result = _service.ClassifyFileType("image/png", ".png");

            // Assert
            result.Should().Be("image");
        }

        /// <summary>
        /// Verifies video MIME types are classified as "video".
        /// Mirrors source UserFileService.cs line 76.
        /// </summary>
        [Fact]
        public void ClassifyFileType_Video_ReturnsVideo()
        {
            // Act
            var result = _service.ClassifyFileType("video/mp4", ".mp4");

            // Assert
            result.Should().Be("video");
        }

        /// <summary>
        /// Verifies audio MIME types are classified as "audio".
        /// Mirrors source UserFileService.cs line 79.
        /// </summary>
        [Fact]
        public void ClassifyFileType_Audio_ReturnsAudio()
        {
            // Act
            var result = _service.ClassifyFileType("audio/mpeg", ".mp3");

            // Assert
            result.Should().Be("audio");
        }

        /// <summary>
        /// Verifies all 14 document extensions from source (UserFileService.cs lines 82-84) are
        /// classified as "document". Uses application/octet-stream as contentType to bypass
        /// MIME-based classification and exercise extension-based document detection.
        /// </summary>
        [Theory]
        [InlineData(".doc")]
        [InlineData(".docx")]
        [InlineData(".odt")]
        [InlineData(".rtf")]
        [InlineData(".txt")]
        [InlineData(".pdf")]
        [InlineData(".html")]
        [InlineData(".htm")]
        [InlineData(".ppt")]
        [InlineData(".pptx")]
        [InlineData(".xls")]
        [InlineData(".xlsx")]
        [InlineData(".ods")]
        [InlineData(".odp")]
        public void ClassifyFileType_DocumentExtensions_ReturnsDocument(string extension)
        {
            // Act — use generic MIME type to trigger extension-based classification path
            var result = _service.ClassifyFileType("application/octet-stream", extension);

            // Assert
            result.Should().Be("document");
        }

        /// <summary>
        /// Verifies unrecognized MIME types and extensions are classified as "other".
        /// Mirrors source UserFileService.cs lines 87-89: fallback classification.
        /// </summary>
        [Fact]
        public void ClassifyFileType_UnknownType_ReturnsOther()
        {
            // Act
            var result = _service.ClassifyFileType("application/zip", ".zip");

            // Assert
            result.Should().Be("other");
        }

        #endregion

        #region Phase 13: DetectContentTypeAsync Tests

        /// <summary>
        /// Verifies .png files are detected as image/png.
        /// Replaces MimeMapping.MimeUtility.GetMimeMapping(path) from source UserFileService.cs line 69.
        /// </summary>
        [Fact]
        public async Task DetectContentType_PngFile_ReturnsImagePng()
        {
            // Act
            var result = await _service.DetectContentTypeAsync("photo.png");

            // Assert
            result.Should().Be("image/png");
        }

        /// <summary>
        /// Verifies .pdf files are detected as application/pdf.
        /// </summary>
        [Fact]
        public async Task DetectContentType_PdfFile_ReturnsApplicationPdf()
        {
            // Act
            var result = await _service.DetectContentTypeAsync("document.pdf");

            // Assert
            result.Should().Be("application/pdf");
        }

        /// <summary>
        /// Verifies unknown file extensions default to application/octet-stream.
        /// </summary>
        [Fact]
        public async Task DetectContentType_UnknownFile_ReturnsOctetStream()
        {
            // Act
            var result = await _service.DetectContentTypeAsync("data.xyz123unknownext");

            // Assert
            result.Should().Be("application/octet-stream");
        }

        #endregion
    }
}

