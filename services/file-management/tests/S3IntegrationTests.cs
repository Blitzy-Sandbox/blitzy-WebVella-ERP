using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebVellaErp.FileManagement.Models;
using WebVellaErp.FileManagement.Services;
using Xunit;

namespace WebVellaErp.FileManagement.Tests;

/// <summary>
/// Integration tests for S3Service running against real LocalStack S3.
/// Validates presigned URL generation, file upload/download, copy, move, delete,
/// and existence checks against actual S3 endpoints.
///
/// CRITICAL: NO mocked AWS SDK calls — all operations hit LocalStack S3.
/// Requires LocalStack running at http://localhost:4566.
/// Pattern: docker compose up -d → test → docker compose down
/// Per AAP §0.8.4: "All integration and E2E tests MUST execute against LocalStack"
/// </summary>
public class S3IntegrationTests : IAsyncLifetime
{
    /// <summary>
    /// Name of the S3 bucket used exclusively for integration testing.
    /// Created during test fixture initialization and destroyed during teardown.
    /// </summary>
    private const string TestBucketName = "test-s3-integration";

    /// <summary>
    /// LocalStack S3 endpoint URL. All test operations target this endpoint.
    /// Requires LocalStack running via docker compose.
    /// </summary>
    private const string LocalStackEndpoint = "http://localhost:4566";

    /// <summary>
    /// Real AWS S3 client configured for LocalStack. Used both by S3Service under test
    /// and directly for verification operations (e.g., GetObjectMetadata, GetObject).
    /// </summary>
    private readonly IAmazonS3 _s3Client;

    /// <summary>
    /// The system under test: real S3Service instance backed by LocalStack S3.
    /// No mocks — this instance performs real S3 operations.
    /// </summary>
    private readonly S3Service _s3Service;

    /// <summary>
    /// HTTP client for presigned URL validation tests. Used to perform raw HTTP
    /// PUT/GET operations against S3 presigned URLs to verify they work end-to-end.
    /// </summary>
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Constructs the integration test fixture with real AWS clients configured for LocalStack.
    /// Uses BasicAWSCredentials with dummy values (LocalStack accepts any credentials),
    /// ForcePathStyle=true (required for LocalStack S3 path-based addressing),
    /// and an in-memory IConfiguration for the S3Service bucket name setting.
    /// </summary>
    public S3IntegrationTests()
    {
        // Configure real AmazonS3Client for LocalStack — NO mocked SDK
        var s3Config = new AmazonS3Config
        {
            ServiceURL = LocalStackEndpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1"
        };

        // LocalStack accepts any credentials — use dummy values
        var credentials = new BasicAWSCredentials("test", "test");
        _s3Client = new AmazonS3Client(credentials, s3Config);

        // Build in-memory IConfiguration for S3Service bucket name resolution.
        // S3Service checks: env var FILE_MANAGEMENT_BUCKET_NAME → config key → default.
        // We supply the config key so tests are deterministic.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileManagement:BucketName"] = TestBucketName
            })
            .Build();

        // NullLogger — logging output is not the test target
        var logger = NullLogger<S3Service>.Instance;

        // Construct S3Service with real S3 client — the system under test
        _s3Service = new S3Service(_s3Client, logger, configuration);

        // HttpClient for presigned URL tests (Phase 4)
        _httpClient = new HttpClient();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // IAsyncLifetime — Fixture Setup / Teardown
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Async fixture setup: creates the test S3 bucket in LocalStack.
    /// Called by xunit before any test method executes.
    /// Handles BucketAlreadyOwnedByYou / BucketAlreadyExists gracefully for
    /// scenarios where a previous test run didn't clean up properly.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            await _s3Client.PutBucketAsync(new PutBucketRequest
            {
                BucketName = TestBucketName
            });
        }
        catch (AmazonS3Exception ex) when (
            ex.ErrorCode == "BucketAlreadyOwnedByYou" ||
            ex.ErrorCode == "BucketAlreadyExists")
        {
            // Bucket may already exist from a previous test run — safe to ignore
        }
    }

    /// <summary>
    /// Async fixture teardown: empties all objects from the test bucket and deletes it.
    /// Handles pagination for buckets with many test objects. Best-effort cleanup:
    /// if the bucket doesn't exist (e.g., test failed before creation), no error thrown.
    /// </summary>
    public async Task DisposeAsync()
    {
        try
        {
            // Empty the bucket before deletion — S3 requires buckets to be empty
            var listRequest = new ListObjectsV2Request { BucketName = TestBucketName };
            ListObjectsV2Response listResponse;

            do
            {
                listResponse = await _s3Client.ListObjectsV2Async(listRequest);

                foreach (var s3Object in listResponse.S3Objects)
                {
                    await _s3Client.DeleteObjectAsync(new DeleteObjectRequest
                    {
                        BucketName = TestBucketName,
                        Key = s3Object.Key
                    });
                }

                listRequest.ContinuationToken = listResponse.NextContinuationToken;
            }
            while (listResponse.IsTruncated);

            // Delete the now-empty bucket
            await _s3Client.DeleteBucketAsync(new DeleteBucketRequest
            {
                BucketName = TestBucketName
            });
        }
        catch (AmazonS3Exception)
        {
            // Best-effort cleanup — bucket may not exist if setup failed
        }
        finally
        {
            _httpClient.Dispose();
            _s3Client.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 2: Upload Tests
    // Replaces PostgreSQL LO/filesystem/blob writes from DbFileRepository.Create()
    // (source lines 140-188: stream.Write, BlobStorage.WriteAsync, File.Open)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that uploading a text file via S3Service stores the content correctly
    /// in S3. Replaces: source DbFileRepository.Create() stream.Write(buffer) path.
    /// </summary>
    [Fact]
    public async Task Upload_TextFile_SuccessfullyStored()
    {
        // Arrange
        var objectKey = $"test-uploads/{Guid.NewGuid()}.txt";
        var content = "Hello, integration test!";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        await _s3Service.UploadFileAsync(objectKey, stream, "text/plain");

        // Assert — verify object exists in S3 and content matches
        using var response = await _s3Client.GetObjectAsync(TestBucketName, objectKey);
        using var reader = new StreamReader(response.ResponseStream);
        var downloadedContent = await reader.ReadToEndAsync();

        downloadedContent.Should().Be(content);
        response.Headers.ContentType.Should().Be("text/plain");
    }

    /// <summary>
    /// Verifies that uploading binary content (e.g., a PNG image) stores correctly
    /// with the proper ContentType preserved. Also validates ClassifyFileType
    /// correctly categorizes the content type.
    /// </summary>
    [Fact]
    public async Task Upload_BinaryFile_SuccessfullyStored()
    {
        // Arrange — minimal PNG header bytes as binary test data
        var objectKey = $"test-uploads/{Guid.NewGuid()}.png";
        var binaryContent = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
        };
        using var stream = new MemoryStream(binaryContent);

        // Act
        await _s3Service.UploadFileAsync(objectKey, stream, "image/png");

        // Assert — verify content roundtrip and ContentType
        using var response = await _s3Client.GetObjectAsync(TestBucketName, objectKey);
        using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms);
        ms.ToArray().Should().BeEquivalentTo(binaryContent);
        response.Headers.ContentType.Should().Be("image/png");

        // Validate file type classification via ClassifyFileType member
        var fileType = _s3Service.ClassifyFileType("image/png", ".png");
        fileType.Should().Be("image");
    }

    /// <summary>
    /// Verifies that uploaded files have AES256 server-side encryption applied.
    /// Per AAP §0.8.3: Encryption at rest for all datastores.
    /// S3Service sets ServerSideEncryptionMethod.AES256 on PutObjectRequest.
    /// </summary>
    [Fact]
    public async Task Upload_SetsServerSideEncryption()
    {
        // Arrange
        var objectKey = $"test-uploads/{Guid.NewGuid()}.txt";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("encrypted content"));

        // Act
        await _s3Service.UploadFileAsync(objectKey, stream, "text/plain");

        // Assert — verify AES256 server-side encryption metadata
        var metadataResponse = await _s3Client.GetObjectMetadataAsync(
            new GetObjectMetadataRequest
            {
                BucketName = TestBucketName,
                Key = objectKey
            });

        metadataResponse.ServerSideEncryptionMethod.Should().Be(ServerSideEncryptionMethod.AES256);
    }

    /// <summary>
    /// Verifies that a sharded object key generated by S3Service correctly addresses
    /// the stored object in S3. Upload with generated key → GetObject with same key.
    /// </summary>
    [Fact]
    public async Task Upload_WithCorrectObjectKey()
    {
        // Arrange — generate a sharded key via S3Service
        var fileId = Guid.NewGuid();
        var objectKey = _s3Service.GenerateObjectKey(fileId, "document.pdf");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("sharded key content"));

        // Act
        await _s3Service.UploadFileAsync(objectKey, stream, "application/pdf");

        // Assert — object is retrievable using the exact generated key
        using var response = await _s3Client.GetObjectAsync(TestBucketName, objectKey);
        Assert.NotNull(response);
        response.Key.Should().Be(objectKey);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 3: Download Tests
    // Replaces DbFile.GetContentStream() three-backend dispatch
    // (source DbFile.cs lines 36-71: LO read, BlobStorage.OpenReadAsync, File.Open)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that downloading an existing file returns the original content.
    /// Replaces: DbFile.GetContentStream() → BlobStorage.OpenReadAsync / LO read / File.Open
    /// </summary>
    [Fact]
    public async Task Download_ExistingFile_ReturnsContent()
    {
        // Arrange — upload file first to establish known state
        var objectKey = $"test-downloads/{Guid.NewGuid()}.txt";
        var originalContent = "Download test content — verify round-trip fidelity";
        using var uploadStream = new MemoryStream(Encoding.UTF8.GetBytes(originalContent));
        await _s3Service.UploadFileAsync(objectKey, uploadStream, "text/plain");

        // Act
        using var response = await _s3Service.DownloadFileAsync(objectKey);

        // Assert — downloaded content matches exactly
        using var reader = new StreamReader(response.ResponseStream);
        var downloadedContent = await reader.ReadToEndAsync();
        downloadedContent.Should().Be(originalContent);
    }

    /// <summary>
    /// Verifies that downloading a non-existent file throws AmazonS3Exception.
    /// S3Service catches and re-throws the S3 error for callers to handle.
    /// </summary>
    [Fact]
    public async Task Download_NonExistentFile_ThrowsException()
    {
        // Arrange — key that does not exist in S3
        var nonExistentKey = $"non-existent/{Guid.NewGuid()}.txt";

        // Act & Assert — S3Service re-throws AmazonS3Exception from S3 client
        var act = async () => await _s3Service.DownloadFileAsync(nonExistentKey);
        await act.Should().ThrowAsync<AmazonS3Exception>();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 4: Presigned URL Tests
    // NEW capability in serverless architecture — no monolith equivalent.
    // Enables direct browser-to-S3 uploads/downloads without Lambda proxy.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a presigned upload URL allows direct HTTP PUT to S3.
    /// Uses HttpClient to PUT content via the presigned URL, then verifies
    /// the file exists in S3 via FileExistsAsync.
    /// </summary>
    [Fact]
    public async Task PresignedUploadUrl_AllowsHttpPutUpload()
    {
        // Arrange
        var objectKey = $"presigned-uploads/{Guid.NewGuid()}.txt";
        var content = "Presigned upload content — direct to S3";
        var contentBytes = Encoding.UTF8.GetBytes(content);

        var presignedUrl = await _s3Service.GeneratePresignedUploadUrlAsync(
            objectKey, "text/plain", contentBytes.Length, expirationMinutes: 5);
        presignedUrl.Should().NotBeNullOrEmpty();

        // Act — HTTP PUT directly to S3 via presigned URL
        var request = new HttpRequestMessage(HttpMethod.Put, presignedUrl)
        {
            Content = new ByteArrayContent(contentBytes)
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

        var httpResponse = await _httpClient.SendAsync(request);

        // Assert — upload succeeded and file exists in S3
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var exists = await _s3Service.FileExistsAsync(objectKey);
        exists.Should().BeTrue();
    }

    /// <summary>
    /// Verifies that a presigned download URL allows direct HTTP GET from S3.
    /// Uploads a file, generates a presigned GET URL, then uses HttpClient
    /// to download and verify content matches.
    /// </summary>
    [Fact]
    public async Task PresignedDownloadUrl_AllowsHttpGetDownload()
    {
        // Arrange — upload a file to download via presigned URL
        var objectKey = $"presigned-downloads/{Guid.NewGuid()}.txt";
        var originalContent = "Presigned download content — fetched from S3";
        using var uploadStream = new MemoryStream(Encoding.UTF8.GetBytes(originalContent));
        await _s3Service.UploadFileAsync(objectKey, uploadStream, "text/plain");

        var presignedUrl = await _s3Service.GeneratePresignedDownloadUrlAsync(
            objectKey, expirationMinutes: 5);
        presignedUrl.Should().NotBeNullOrEmpty();

        // Act — HTTP GET directly from S3 via presigned URL
        var downloadedContent = await _httpClient.GetStringAsync(presignedUrl);

        // Assert — content matches original upload
        downloadedContent.Should().Be(originalContent);
    }

    /// <summary>
    /// Verifies that presigned URLs respect the specified expiration window.
    /// Generates a URL with 1-minute expiration and verifies it is immediately usable.
    /// Cannot test expiration failure without sleeping > 1 minute.
    /// </summary>
    [Fact]
    public async Task PresignedUrl_RespectsExpiration()
    {
        // Arrange — upload file and generate URL with short expiration
        var objectKey = $"presigned-expiry/{Guid.NewGuid()}.txt";
        var content = "expiry test content";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await _s3Service.UploadFileAsync(objectKey, stream, "text/plain");

        // Act — generate URL with 1-minute expiration (shortest practical)
        // TimeSpan reference: 1-minute window = TimeSpan.FromMinutes(1) equivalent
        var expirationWindow = TimeSpan.FromMinutes(1);
        var presignedUrl = await _s3Service.GeneratePresignedDownloadUrlAsync(
            objectKey, expirationMinutes: (int)expirationWindow.TotalMinutes);

        // Assert — URL is valid and usable within timeframe
        presignedUrl.Should().NotBeNullOrEmpty();
        var downloadedContent = await _httpClient.GetStringAsync(presignedUrl);
        downloadedContent.Should().Be(content);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 5: Copy Tests
    // Replaces DbFileRepository.Copy() read-then-write pattern (source lines 269-270)
    // with S3 server-side copy for efficiency and atomicity.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that copying a file creates a new destination object while preserving
    /// the source object. Both source and destination should exist after the operation
    /// and have identical content.
    /// </summary>
    [Fact]
    public async Task Copy_ExistingFile_CreatesDestination()
    {
        // Arrange — upload source file
        var sourceKey = $"copy-source/{Guid.NewGuid()}.txt";
        var destKey = $"copy-dest/{Guid.NewGuid()}.txt";
        var content = "Content to be copied via server-side S3 copy";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await _s3Service.UploadFileAsync(sourceKey, stream, "text/plain");

        // Act
        await _s3Service.CopyFileAsync(sourceKey, destKey);

        // Assert — both source and destination exist in S3
        var sourceExists = await _s3Service.FileExistsAsync(sourceKey);
        var destExists = await _s3Service.FileExistsAsync(destKey);
        sourceExists.Should().BeTrue();
        destExists.Should().BeTrue();

        // Verify destination content matches source
        using var response = await _s3Client.GetObjectAsync(TestBucketName, destKey);
        using var reader = new StreamReader(response.ResponseStream);
        var destContent = await reader.ReadToEndAsync();
        destContent.Should().Be(content);
    }

    /// <summary>
    /// Verifies that S3 server-side copy preserves the ContentType metadata
    /// from the source object. S3 default MetadataDirective=COPY behavior
    /// ensures metadata is carried over without explicit configuration.
    /// </summary>
    [Fact]
    public async Task Copy_PreservesContentType()
    {
        // Arrange — upload source with specific ContentType
        var sourceKey = $"copy-ct/{Guid.NewGuid()}.pdf";
        var destKey = $"copy-ct/{Guid.NewGuid()}.pdf";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("pdf content simulation"));
        await _s3Service.UploadFileAsync(sourceKey, stream, "application/pdf");

        // Act
        await _s3Service.CopyFileAsync(sourceKey, destKey);

        // Assert — destination preserves ContentType from source
        var metadata = await _s3Client.GetObjectMetadataAsync(
            new GetObjectMetadataRequest
            {
                BucketName = TestBucketName,
                Key = destKey
            });
        metadata.Headers.ContentType.Should().Be("application/pdf");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 6: Move Tests
    // Replaces DbFileRepository.Move() cloud blob copy+delete (source lines 329-344)
    // and filesystem File.Move (source line 355). S3Service implements move
    // as server-side copy followed by source deletion.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that moving a file relocates it: destination exists with correct content,
    /// source is deleted. S3Service implements move as copy + delete.
    /// </summary>
    [Fact]
    public async Task Move_ExistingFile_RelocatesContent()
    {
        // Arrange — upload source file
        var sourceKey = $"move-source/{Guid.NewGuid()}.txt";
        var destKey = $"move-dest/{Guid.NewGuid()}.txt";
        var content = "Content to be moved from source to destination";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await _s3Service.UploadFileAsync(sourceKey, stream, "text/plain");

        // Act
        await _s3Service.MoveFileAsync(sourceKey, destKey);

        // Assert — destination exists, source does NOT exist
        var destExists = await _s3Service.FileExistsAsync(destKey);
        var sourceExists = await _s3Service.FileExistsAsync(sourceKey);
        destExists.Should().BeTrue();
        sourceExists.Should().BeFalse();

        // Verify destination content matches original
        using var response = await _s3Client.GetObjectAsync(TestBucketName, destKey);
        using var reader = new StreamReader(response.ResponseStream);
        var movedContent = await reader.ReadToEndAsync();
        movedContent.Should().Be(content);
    }

    /// <summary>
    /// Verifies that moving a non-existent source file is handled gracefully.
    /// S3Service.MoveFileAsync catches NoSuchKey on the source and logs a warning
    /// instead of throwing — no exception should propagate to the caller.
    /// </summary>
    [Fact]
    public async Task Move_NonExistentSource_HandlesGracefully()
    {
        // Arrange — source key does not exist
        var nonExistentSource = $"move-missing/{Guid.NewGuid()}.txt";
        var destKey = $"move-dest/{Guid.NewGuid()}.txt";

        // Act — S3Service catches NoSuchKey internally and logs warning
        var act = async () => await _s3Service.MoveFileAsync(nonExistentSource, destKey);

        // Assert — no exception thrown (graceful degradation)
        await act.Should().NotThrowAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 7: Delete Tests
    // Replaces three deletion backends from DbFileRepository.Delete()
    // (source lines 395-414: LO unlink, BlobStorage.DeleteAsync, File.Delete)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that deleting an existing file removes it from S3.
    /// FileExistsAsync should return false after deletion.
    /// </summary>
    [Fact]
    public async Task Delete_ExistingFile_RemovesFromS3()
    {
        // Arrange — upload a file to delete
        var objectKey = $"delete-test/{Guid.NewGuid()}.txt";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("delete me"));
        await _s3Service.UploadFileAsync(objectKey, stream, "text/plain");

        // Verify file exists before deletion
        var existsBefore = await _s3Service.FileExistsAsync(objectKey);
        existsBefore.Should().BeTrue();

        // Act
        await _s3Service.DeleteFileAsync(objectKey);

        // Assert
        var existsAfter = await _s3Service.FileExistsAsync(objectKey);
        existsAfter.Should().BeFalse();
    }

    /// <summary>
    /// Verifies that deleting a non-existent key is idempotent — no exception thrown.
    /// S3 DeleteObject returns 204 even for missing keys. Mirrors monolith behavior
    /// at DbFileRepository.Delete() line 387-388: if (file == null) return;
    /// </summary>
    [Fact]
    public async Task Delete_NonExistentFile_IsIdempotent()
    {
        // Arrange — key does not exist in S3
        var nonExistentKey = $"delete-missing/{Guid.NewGuid()}.txt";

        // Act — S3 DeleteObject is natively idempotent
        var act = async () => await _s3Service.DeleteFileAsync(nonExistentKey);

        // Assert — no exception thrown
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies that deleting an already-deleted file is idempotent.
    /// Upload → delete → delete again should succeed without error.
    /// </summary>
    [Fact]
    public async Task Delete_AlreadyDeleted_IsIdempotent()
    {
        // Arrange — upload, then delete once
        var objectKey = $"delete-twice/{Guid.NewGuid()}.txt";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("delete twice test"));
        await _s3Service.UploadFileAsync(objectKey, stream, "text/plain");
        await _s3Service.DeleteFileAsync(objectKey);

        // Act — second delete on already-deleted object
        var act = async () => await _s3Service.DeleteFileAsync(objectKey);

        // Assert — idempotent: second delete succeeds without error
        await act.Should().NotThrowAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 8: FileExists Tests
    // Replaces storage.ExistsAsync(path).Result (source line 400)
    // and File.Exists(path) (source line 408)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that FileExistsAsync returns true for a file that exists in S3.
    /// </summary>
    [Fact]
    public async Task FileExists_WhenPresent_ReturnsTrue()
    {
        // Arrange — upload a file to check
        var objectKey = $"exists-test/{Guid.NewGuid()}.txt";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("existence check"));
        await _s3Service.UploadFileAsync(objectKey, stream, "text/plain");

        // Act
        var exists = await _s3Service.FileExistsAsync(objectKey);

        // Assert
        exists.Should().BeTrue();
    }

    /// <summary>
    /// Verifies that FileExistsAsync returns false for a key that does not exist in S3.
    /// S3Service catches 404/NoSuchKey from GetObjectMetadata and returns false.
    /// </summary>
    [Fact]
    public async Task FileExists_WhenAbsent_ReturnsFalse()
    {
        // Arrange — key that was never uploaded
        var nonExistentKey = $"exists-missing/{Guid.NewGuid()}.txt";

        // Act
        var exists = await _s3Service.FileExistsAsync(nonExistentKey);

        // Assert
        exists.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 9: Object Key Sharding Tests
    // Validates S3 key generation matches monolith GetBlobPath() pattern
    // (source DbFileRepository.cs lines 496-508)
    // Key format: {depth1}/{depth2}/{fileId}{extension}
    // where depth1 = first 2 hex chars, depth2 = next 2 hex chars of GUID
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that S3Service.GenerateObjectKey produces keys matching the monolith's
    /// GetBlobPath() sharding pattern: {depth1}/{depth2}/{fileId}{extension}.
    /// Also verifies that FileMetadata.GenerateObjectKey produces an identical key
    /// and validates a full upload/download round-trip using the sharded key.
    /// Uses FileMetadata.NormalizeFilePath for path validation.
    /// </summary>
    [Fact]
    public async Task ObjectKey_ShardingPattern_MatchesMonolith()
    {
        // Arrange — use a known GUID to verify key format
        var fileId = Guid.NewGuid();
        var filePath = "document.pdf";
        var idHex = fileId.ToString("N");
        var expectedDepth1 = idHex.Substring(0, 2);
        var expectedDepth2 = idHex.Substring(2, 2);
        var expectedKey = $"{expectedDepth1}/{expectedDepth2}/{fileId}.pdf";

        // Act — generate keys from both S3Service and FileMetadata
        var s3Key = _s3Service.GenerateObjectKey(fileId, filePath);
        var metadataKey = FileMetadata.GenerateObjectKey(fileId, filePath);

        // Also validate NormalizeFilePath utility from FileMetadata
        var normalizedPath = FileMetadata.NormalizeFilePath(filePath);
        normalizedPath.Should().NotBeNullOrEmpty();

        // Assert — keys match the monolith sharding pattern
        s3Key.Should().Be(expectedKey);
        metadataKey.Should().Be(expectedKey);
        s3Key.Should().Be(metadataKey);

        // Verify round-trip: upload with sharded key, download with same key
        using var uploadStream = new MemoryStream(
            Encoding.UTF8.GetBytes("sharding round-trip verification"));
        await _s3Service.UploadFileAsync(s3Key, uploadStream, "application/pdf");

        using var response = await _s3Service.DownloadFileAsync(s3Key);
        using var reader = new StreamReader(response.ResponseStream);
        var content = await reader.ReadToEndAsync();
        content.Should().Be("sharding round-trip verification");
    }

    /// <summary>
    /// Verifies that GenerateTempObjectKey produces keys prefixed with "tmp/",
    /// matching the monolith's TMP_FOLDER_NAME constant. Validates that temporary
    /// files can be uploaded and retrieved using the generated key.
    /// </summary>
    [Fact]
    public async Task TempObjectKey_PrefixedWithTmp()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var fileName = "temp-upload.txt";

        // Act — generate temp key
        var tempKey = _s3Service.GenerateTempObjectKey(fileId, fileName, ".txt");

        // Assert — key starts with "tmp/" prefix
        tempKey.Should().StartWith("tmp/");

        // Verify the temp key is usable in S3
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("temporary file content"));
        await _s3Service.UploadFileAsync(tempKey, stream, "text/plain");

        var exists = await _s3Service.FileExistsAsync(tempKey);
        exists.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 10: Large File Handling
    // Validates that S3Service handles files > 1MB correctly via standard
    // PutObject. Verifies content integrity on round-trip.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that uploading a file larger than 1MB succeeds and content integrity
    /// is maintained on download. Uses a deterministic random seed (42) for reproducible
    /// binary content verification.
    /// </summary>
    [Fact]
    public async Task Upload_LargeFile_Succeeds()
    {
        // Arrange — create a 2MB file with deterministic content
        var objectKey = $"large-file/{Guid.NewGuid()}.bin";
        var largeContent = new byte[2 * 1024 * 1024]; // 2MB
        new Random(42).NextBytes(largeContent); // Deterministic seed for reproducibility
        using var stream = new MemoryStream(largeContent);

        // Act
        await _s3Service.UploadFileAsync(objectKey, stream, "application/octet-stream");

        // Assert — download and verify content integrity
        using var response = await _s3Service.DownloadFileAsync(objectKey);
        using var downloadStream = new MemoryStream();
        await response.ResponseStream.CopyToAsync(downloadStream);
        var downloadedContent = downloadStream.ToArray();

        downloadedContent.Should().HaveCount(largeContent.Length);
        downloadedContent.Should().BeEquivalentTo(largeContent);
    }
}
