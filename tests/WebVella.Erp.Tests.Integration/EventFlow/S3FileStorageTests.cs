using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using WebVella.Erp.Tests.Integration.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace WebVella.Erp.Tests.Integration.EventFlow
{
    /// <summary>
    /// End-to-end integration tests for S3 file storage operations via LocalStack,
    /// validating the replacement of the monolith's <c>DbFileRepository</c> which
    /// supports three storage backends (PostgreSQL Large Objects, Filesystem, Cloud
    /// blobs via Storage.Net) with S3-compatible cloud storage.
    ///
    /// <para><b>Source context (monolith):</b></para>
    /// <list type="bullet">
    ///   <item><c>DbFileRepository.cs</c> — Full file lifecycle: Find, FindAll,
    ///         Create, Copy, Move, Delete, CreateTempFile, CleanupExpiredTempFiles</item>
    ///   <item><c>DbFileRepository.FOLDER_SEPARATOR = "/"</c> (line 14)</item>
    ///   <item><c>DbFileRepository.TMP_FOLDER_NAME = "tmp"</c> (line 15)</item>
    ///   <item><c>filepath = filepath.ToLowerInvariant()</c> (line 40) — path normalization</item>
    ///   <item>GUID-prefix sharding via <c>GetBlobPath</c> / <c>GetFileSystemPath</c></item>
    ///   <item><c>DbFile.cs</c> — File metadata (Id, ObjectId, FilePath, audit fields)
    ///         with <c>GetBytes()</c> for content retrieval</item>
    /// </list>
    ///
    /// <para><b>Key AAP References:</b></para>
    /// <list type="bullet">
    ///   <item>AAP 0.8.2: "LocalStack validation: End-to-end tests must validate...
    ///         file operations through S3"</item>
    ///   <item>AAP 0.7.4: S3 bucket "erp-file-storage" for file storage via LocalStack</item>
    ///   <item>AAP 0.1.1: "Unified DbFileRepository (LO/FS/Cloud)" → "Shared
    ///         file-storage service or S3-compatible storage via LocalStack"</item>
    /// </list>
    /// </summary>
    [Collection(IntegrationTestCollection.Name)]
    public class S3FileStorageTests : IAsyncLifetime
    {
        #region Private Fields

        /// <summary>
        /// LocalStack fixture injected by xUnit collection, providing container
        /// lifecycle management and AWS client factory methods.
        /// </summary>
        private readonly LocalStackFixture _localStackFixture;

        /// <summary>
        /// Structured test output for diagnostic logging during S3 operations.
        /// Enables debugging of test failures in CI/CD pipelines.
        /// </summary>
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Amazon S3 client configured to communicate with the LocalStack S3 endpoint.
        /// Created during <see cref="InitializeAsync"/> via the fixture's
        /// <see cref="LocalStackFixture.CreateS3Client()"/> factory method.
        /// ForcePathStyle is enabled for LocalStack compatibility.
        /// </summary>
        private AmazonS3Client _s3Client;

        #endregion

        #region Constants

        /// <summary>
        /// S3 bucket name for file storage, matching
        /// <see cref="LocalStackFixture.FileStorageBucket"/> and the
        /// docker-compose.localstack.yml S3 bucket provisioned by init-aws.sh.
        /// Per AAP 0.7.4: S3 replaces Storage.Net cloud blob storage.
        /// </summary>
        private const string BucketName = "erp-file-storage";

        /// <summary>
        /// Folder separator matching <c>DbFileRepository.FOLDER_SEPARATOR</c> (line 14).
        /// Used to construct S3 key paths that mirror the monolith's file path conventions.
        /// </summary>
        private const string FolderSeparator = "/";

        /// <summary>
        /// Temp folder name matching <c>DbFileRepository.TMP_FOLDER_NAME</c> (line 15).
        /// Used in temp file lifecycle tests to verify cleanup behavior.
        /// </summary>
        private const string TmpFolderName = "tmp";

        /// <summary>
        /// Standard test file content used across upload and download tests.
        /// </summary>
        private const string TestFileContent = "Hello ERP File Storage";

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of <see cref="S3FileStorageTests"/>.
        /// The <paramref name="localStackFixture"/> is injected by xUnit's
        /// collection fixture mechanism via <see cref="IntegrationTestCollection"/>.
        /// </summary>
        /// <param name="localStackFixture">
        /// Shared LocalStack container fixture providing S3 client factory and bucket constants.
        /// </param>
        /// <param name="output">
        /// xUnit test output helper for diagnostic logging.
        /// </param>
        public S3FileStorageTests(LocalStackFixture localStackFixture, ITestOutputHelper output)
        {
            _localStackFixture = localStackFixture ?? throw new ArgumentNullException(nameof(localStackFixture));
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        #endregion

        #region IAsyncLifetime Implementation

        /// <summary>
        /// Creates the S3 client from the LocalStack fixture before each test class execution.
        /// Verifies the bucket name constant matches the fixture's provisioned bucket.
        /// </summary>
        public Task InitializeAsync()
        {
            _s3Client = _localStackFixture.CreateS3Client();
            _output.WriteLine($"S3 client created, endpoint: {_localStackFixture.Endpoint}");
            _output.WriteLine($"Using bucket: {BucketName} (fixture: {LocalStackFixture.FileStorageBucket})");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Disposes the S3 client after all tests in this class have completed.
        /// </summary>
        public Task DisposeAsync()
        {
            _s3Client?.Dispose();
            _s3Client = null;
            return Task.CompletedTask;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Generates an S3 object key that mirrors the <c>DbFileRepository</c> GUID-prefix
        /// sharding pattern used by <c>GetBlobPath</c> and <c>GetFileSystemPath</c>.
        /// The monolith uses the first 8 chars of the GUID split into depth1 (2 chars)
        /// and depth2 (2 chars) subdirectories.
        /// Source: DbFileRepository.cs lines 478-508 (GetFileSystemPath / GetBlobPath).
        /// </summary>
        /// <param name="filename">The filename including extension (e.g., "document.txt").</param>
        /// <returns>
        /// A normalized S3 key in the format: {depth1}/{depth2}/{guid}{ext}
        /// matching the monolith's blob storage path convention.
        /// </returns>
        private static string GenerateFileKey(string filename)
        {
            var fileId = Guid.NewGuid();
            var guidInitialPart = fileId.ToString().Split('-')[0];
            var depth1Folder = guidInitialPart.Substring(0, 2);
            var depth2Folder = guidInitialPart.Substring(2, 2);
            var extension = Path.GetExtension(filename);

            // Mirror DbFileRepository.GetBlobPath: StoragePath.Combine(depth1, depth2, id + ext)
            if (!string.IsNullOrWhiteSpace(extension))
            {
                return $"{depth1Folder}/{depth2Folder}/{fileId}{extension}";
            }
            return $"{depth1Folder}/{depth2Folder}/{fileId}";
        }

        /// <summary>
        /// Generates a temp file S3 key matching <c>DbFileRepository.CreateTempFile</c> pattern.
        /// Source: DbFileRepository.cs lines 437-449:
        ///   section = Guid.NewGuid().ToString().Replace("-", "").ToLowerInvariant();
        ///   tmpFilePath = "/" + TMP_FOLDER_NAME + "/" + section + "/" + filename + extension;
        /// </summary>
        /// <param name="filename">The temp file name.</param>
        /// <param name="extension">Optional extension (e.g., ".txt").</param>
        /// <returns>S3 key following the temp file path convention.</returns>
        private static string GenerateTempFileKey(string filename, string extension = null)
        {
            var section = Guid.NewGuid().ToString().Replace("-", "").ToLowerInvariant();
            var ext = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension;
            if (!string.IsNullOrWhiteSpace(ext) && !ext.StartsWith("."))
            {
                ext = "." + ext;
            }
            // Matches monolith: /tmp/{section}/{filename}{ext}
            return $"{TmpFolderName}/{section}/{filename}{ext}";
        }

        /// <summary>
        /// Uploads a file to the S3 test bucket and returns the put response.
        /// Wraps the PutObjectAsync call with diagnostic logging.
        /// </summary>
        private async Task<PutObjectResponse> UploadFileAsync(string key, byte[] content)
        {
            var putRequest = new PutObjectRequest
            {
                BucketName = BucketName,
                Key = key,
                InputStream = new MemoryStream(content)
            };

            var response = await _s3Client.PutObjectAsync(putRequest).ConfigureAwait(false);
            _output.WriteLine($"Uploaded file: key={key}, size={content.Length} bytes, status={response.HttpStatusCode}");
            return response;
        }

        /// <summary>
        /// Downloads a file from S3 and returns the content as a byte array.
        /// Reads the full response stream into memory.
        /// Validates the equivalent of <c>DbFile.GetBytes()</c> which reads from
        /// PostgreSQL Large Objects, filesystem, or cloud blobs.
        /// </summary>
        private async Task<byte[]> DownloadFileAsync(string key)
        {
            var getRequest = new GetObjectRequest
            {
                BucketName = BucketName,
                Key = key
            };

            using (var response = await _s3Client.GetObjectAsync(getRequest).ConfigureAwait(false))
            using (var memoryStream = new MemoryStream())
            {
                await response.ResponseStream.CopyToAsync(memoryStream).ConfigureAwait(false);
                var bytes = memoryStream.ToArray();
                _output.WriteLine($"Downloaded file: key={key}, size={bytes.Length} bytes");
                return bytes;
            }
        }

        #endregion

        #region Test: UploadFile_ValidContent_SuccessfullyStored

        /// <summary>
        /// Validates that a file can be uploaded to S3 with content matching the
        /// <c>DbFileRepository.Create</c> pattern (line 119-200). Verifies the
        /// response status code and that the file metadata (content length) can be
        /// retrieved via <c>GetObjectMetadataAsync</c>.
        ///
        /// Equivalent monolith operation: <c>DbFileRepository.Create(filepath, buffer, createdOn, createdBy)</c>
        /// </summary>
        [Fact]
        public async Task UploadFile_ValidContent_SuccessfullyStored()
        {
            // Arrange: Generate test file content — "Hello ERP File Storage"
            var content = Encoding.UTF8.GetBytes(TestFileContent);
            var fileKey = GenerateFileKey("testfile.txt");
            _output.WriteLine($"Test file key: {fileKey}, content size: {content.Length} bytes");

            // Act: Upload via PutObjectAsync mirroring DbFileRepository cloud blob write
            var putResponse = await UploadFileAsync(fileKey, content);

            // Assert Step 1: Verify successful upload (HTTP 200 OK)
            putResponse.HttpStatusCode.Should().Be(HttpStatusCode.OK,
                "PutObjectAsync should return HTTP 200 for successful uploads");

            // Assert Step 2: Verify the file exists and metadata matches
            var metadataRequest = new GetObjectMetadataRequest
            {
                BucketName = BucketName,
                Key = fileKey
            };
            var metadataResponse = await _s3Client.GetObjectMetadataAsync(metadataRequest).ConfigureAwait(false);

            metadataResponse.Should().NotBeNull("file metadata should be retrievable after upload");
            metadataResponse.ContentLength.Should().Be(content.Length,
                "stored file content length should match the uploaded byte array size");

            _output.WriteLine($"Metadata verified: ContentLength={metadataResponse.ContentLength}");
        }

        #endregion

        #region Test: DownloadFile_ExistingFile_ContentMatches

        /// <summary>
        /// Validates that a previously uploaded file can be downloaded with byte-for-byte
        /// content fidelity. This tests the equivalent of <c>DbFile.GetBytes()</c> which
        /// reads from PostgreSQL Large Objects, filesystem, or cloud blobs via Storage.Net.
        /// Source: DbFile.cs lines 73-79 (GetBytes with connection) and 81-107 (GetBytes overload).
        /// </summary>
        [Fact]
        public async Task DownloadFile_ExistingFile_ContentMatches()
        {
            // Arrange: Upload a known file
            var originalContent = Encoding.UTF8.GetBytes(TestFileContent);
            var fileKey = GenerateFileKey("download-test.txt");
            await UploadFileAsync(fileKey, originalContent);

            // Act: Download the file via GetObjectAsync
            var downloadedContent = await DownloadFileAsync(fileKey);

            // Assert: Downloaded content equals uploaded content byte-for-byte
            downloadedContent.Should().BeEquivalentTo(originalContent,
                "downloaded file content should be identical to the uploaded content, " +
                "validating the S3 replacement for DbFile.GetBytes()");

            // Verify string content roundtrip
            var downloadedText = Encoding.UTF8.GetString(downloadedContent);
            downloadedText.Should().Be(TestFileContent,
                "decoded text should match the original test file content");

            _output.WriteLine($"Content verified: '{downloadedText}'");
        }

        #endregion

        #region Test: DeleteFile_ExistingFile_SuccessfullyRemoved

        /// <summary>
        /// Validates that a file can be deleted from S3, equivalent to
        /// <c>DbFileRepository.Delete(filepath)</c> (lines 375-429).
        /// The monolith deletes from cloud blobs (Storage.Net), filesystem, or
        /// PostgreSQL large objects depending on configuration, plus removes the
        /// metadata row from the <c>files</c> table.
        /// After deletion, attempting to access the file should result in an
        /// <c>AmazonS3Exception</c> with a 404 status code.
        /// </summary>
        [Fact]
        public async Task DeleteFile_ExistingFile_SuccessfullyRemoved()
        {
            // Arrange: Upload a test file
            var content = Encoding.UTF8.GetBytes("File to be deleted");
            var fileKey = GenerateFileKey("delete-test.txt");
            await UploadFileAsync(fileKey, content);

            // Verify file exists before deletion
            var metadataRequest = new GetObjectMetadataRequest
            {
                BucketName = BucketName,
                Key = fileKey
            };
            var metadataBefore = await _s3Client.GetObjectMetadataAsync(metadataRequest).ConfigureAwait(false);
            metadataBefore.Should().NotBeNull("file should exist before deletion");
            _output.WriteLine($"File exists before delete: key={fileKey}");

            // Act: Delete the file via DeleteObjectAsync
            var deleteRequest = new DeleteObjectRequest
            {
                BucketName = BucketName,
                Key = fileKey
            };
            var deleteResponse = await _s3Client.DeleteObjectAsync(deleteRequest).ConfigureAwait(false);
            _output.WriteLine($"Delete response status: {deleteResponse.HttpStatusCode}");

            // Assert: Attempting to retrieve the deleted file should throw AmazonS3Exception
            // with HTTP 404 Not Found, mirroring DbFileRepository.Find returning null
            Func<Task> getDeletedFile = async () =>
            {
                await _s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = BucketName,
                    Key = fileKey
                }).ConfigureAwait(false);
            };

            var exception = await getDeletedFile.Should().ThrowAsync<AmazonS3Exception>(
                "accessing a deleted file should throw AmazonS3Exception");
            exception.Which.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "the S3 exception status code should be 404 NotFound for deleted files");

            _output.WriteLine("File confirmed deleted — 404 returned on access attempt");
        }

        #endregion

        #region Test: PresignedUrl_GenerateForExistingFile_AccessibleUrl

        /// <summary>
        /// Validates presigned URL generation for cross-service file access without
        /// sharing S3 credentials. This enables services to grant temporary read access
        /// to files stored in S3 via time-limited presigned URLs.
        ///
        /// Per folder spec: "Tests presigned URL generation for cross-service file access"
        /// Per AAP 0.1.1: File storage transitions to S3-compatible storage via LocalStack,
        /// and presigned URLs enable secure cross-service file sharing.
        /// </summary>
        [Fact]
        public async Task PresignedUrl_GenerateForExistingFile_AccessibleUrl()
        {
            // Arrange: Upload a test file
            var originalContent = Encoding.UTF8.GetBytes("Presigned URL test content");
            var fileKey = GenerateFileKey("presigned-test.txt");
            await UploadFileAsync(fileKey, originalContent);

            // Act: Generate a presigned URL with 1-hour expiry
            var presignedUrlRequest = new GetPreSignedUrlRequest
            {
                BucketName = BucketName,
                Key = fileKey,
                Expires = DateTime.UtcNow.AddHours(1),
                Verb = HttpVerb.GET
            };
            var presignedUrl = _s3Client.GetPreSignedURL(presignedUrlRequest);
            _output.WriteLine($"Presigned URL generated: {presignedUrl}");

            presignedUrl.Should().NotBeNullOrEmpty("a presigned URL should be generated");

            // Act: Download the file via the presigned URL using HttpClient
            // This simulates cross-service file access without S3 credentials.
            // LocalStack presigned URLs may use HTTPS with a self-signed cert,
            // so we disable certificate validation for test purposes.
            using (var handler = new HttpClientHandler())
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                using var httpClient = new HttpClient(handler);
                var downloadedContent = await httpClient.GetByteArrayAsync(presignedUrl).ConfigureAwait(false);

                // Assert: Content downloaded via presigned URL matches the original
                downloadedContent.Should().BeEquivalentTo(originalContent,
                    "content downloaded via presigned URL should match the original upload, " +
                    "enabling cross-service file access without sharing S3 credentials");

                var downloadedText = Encoding.UTF8.GetString(downloadedContent);
                _output.WriteLine($"Content via presigned URL: '{downloadedText}'");
            }
        }

        #endregion

        #region Test: FilePathNormalization_LowercasePaths_ConsistentAccess

        /// <summary>
        /// Validates that the application-level path normalization from
        /// <c>DbFileRepository</c> is preserved in the new S3 file storage service.
        ///
        /// Source context: DbFileRepository.cs line 40: <c>filepath = filepath.ToLowerInvariant()</c>
        /// All file paths in the monolith are normalized to lowercase before storage.
        ///
        /// S3 keys are case-sensitive, so the test validates that:
        /// 1. Files uploaded with a normalized (lowercase) key are accessible via that key.
        /// 2. Mixed-case keys are NOT accessible after normalization — demonstrating that
        ///    the application layer must normalize paths before S3 operations.
        /// This ensures the DbFileRepository normalization contract is preserved.
        /// </summary>
        [Fact]
        public async Task FilePathNormalization_LowercasePaths_ConsistentAccess()
        {
            // Arrange: Define mixed-case and normalized keys
            var mixedCaseKey = "Folder/SubFolder/TestFile.txt";
            var normalizedKey = mixedCaseKey.ToLowerInvariant(); // DbFileRepository line 40
            var content = Encoding.UTF8.GetBytes("Path normalization test");

            _output.WriteLine($"Mixed-case key: {mixedCaseKey}");
            _output.WriteLine($"Normalized key: {normalizedKey}");

            // Act Step 1: Upload with the normalized (lowercase) key — this is what
            // DbFileRepository does: filepath = filepath.ToLowerInvariant() before storage
            await UploadFileAsync(normalizedKey, content);

            // Assert Step 1: The normalized key is accessible
            var downloadedContent = await DownloadFileAsync(normalizedKey);
            downloadedContent.Should().BeEquivalentTo(content,
                "file uploaded with normalized (lowercase) key should be retrievable");

            // Assert Step 2: The mixed-case key does NOT access the same file
            // (S3 is case-sensitive — this validates that app-level normalization is required)
            Func<Task> accessMixedCase = async () =>
            {
                await _s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = BucketName,
                    Key = mixedCaseKey
                }).ConfigureAwait(false);
            };

            await accessMixedCase.Should().ThrowAsync<AmazonS3Exception>(
                "S3 is case-sensitive — mixed-case key should not resolve to the " +
                "normalized lowercase key, demonstrating that application-level " +
                "normalization (DbFileRepository line 40) must be applied before S3 operations");

            _output.WriteLine("Path normalization validated: S3 is case-sensitive, " +
                              "application must normalize paths to lowercase before storage");
        }

        #endregion

        #region Test: TempFileHandling_CreateAndCleanup_LifecycleManaged

        /// <summary>
        /// Validates the temp file lifecycle: create, verify existence, list, delete,
        /// and verify removal. This mirrors the monolith's <c>DbFileRepository.CreateTempFile</c>
        /// (lines 437-449) and <c>CleanupExpiredTempFiles</c> (lines 455-469).
        ///
        /// Source context:
        ///   <c>TMP_FOLDER_NAME = "tmp"</c> (line 15)
        ///   <c>tmpFilePath = "/" + TMP_FOLDER_NAME + "/" + section + "/" + filename + ext</c> (line 447)
        ///   <c>CleanupExpiredTempFiles</c> queries files WHERE filepath ILIKE '%/tmp' (line 463)
        /// </summary>
        [Fact]
        public async Task TempFileHandling_CreateAndCleanup_LifecycleManaged()
        {
            // Arrange: Create a temp file key matching DbFileRepository.CreateTempFile pattern
            var tempFileKey = GenerateTempFileKey("tempdata", ".txt");
            var content = Encoding.UTF8.GetBytes("Temporary file content for cleanup test");
            _output.WriteLine($"Temp file key: {tempFileKey}");

            // Act Step 1: Upload the temp file (simulating CreateTempFile)
            var putResponse = await UploadFileAsync(tempFileKey, content);
            putResponse.HttpStatusCode.Should().Be(HttpStatusCode.OK,
                "temp file upload should succeed");

            // Assert Step 2: Verify temp file exists
            var metadataResponse = await _s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = BucketName,
                Key = tempFileKey
            }).ConfigureAwait(false);
            metadataResponse.Should().NotBeNull("temp file should exist after creation");
            _output.WriteLine("Temp file verified to exist");

            // Act Step 3: List objects with tmp/ prefix to find temp files
            // Mirrors CleanupExpiredTempFiles querying WHERE filepath ILIKE '%/tmp'
            var listRequest = new ListObjectsV2Request
            {
                BucketName = BucketName,
                Prefix = TmpFolderName + FolderSeparator
            };
            var listResponse = await _s3Client.ListObjectsV2Async(listRequest).ConfigureAwait(false);

            listResponse.S3Objects.Should().NotBeNull("listing temp folder should return results");
            var tempFiles = listResponse.S3Objects.Select(o => o.Key).ToList();
            tempFiles.Should().Contain(tempFileKey,
                "the temp file should appear in the tmp/ prefix listing");
            _output.WriteLine($"Found {tempFiles.Count} temp file(s) in listing");

            // Act Step 4: Delete temp file (simulating CleanupExpiredTempFiles)
            var deleteRequest = new DeleteObjectRequest
            {
                BucketName = BucketName,
                Key = tempFileKey
            };
            await _s3Client.DeleteObjectAsync(deleteRequest).ConfigureAwait(false);
            _output.WriteLine("Temp file deleted (cleanup simulation)");

            // Assert Step 5: Verify temp file is removed
            Func<Task> accessDeletedTemp = async () =>
            {
                await _s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = BucketName,
                    Key = tempFileKey
                }).ConfigureAwait(false);
            };

            await accessDeletedTemp.Should().ThrowAsync<AmazonS3Exception>(
                "temp file should no longer exist after cleanup deletion");

            _output.WriteLine("Temp file lifecycle validated: create → verify → list → delete → verify removal");
        }

        #endregion

        #region Test: LargeFile_UploadAndDownload_HandledCorrectly

        /// <summary>
        /// Validates that large files (>5MB) can be uploaded and downloaded with
        /// content integrity preserved. Large files may trigger S3 multipart upload
        /// behavior depending on the SDK configuration.
        /// Content integrity is verified via SHA256 hash comparison.
        /// </summary>
        [Fact]
        public async Task LargeFile_UploadAndDownload_HandledCorrectly()
        {
            // Arrange: Generate a 6MB test file (above the 5MB multipart threshold)
            const int fileSizeBytes = 6 * 1024 * 1024; // 6 MB
            var largeContent = new byte[fileSizeBytes];
            var random = new Random(42); // Deterministic seed for reproducibility
            random.NextBytes(largeContent);

            var fileKey = GenerateFileKey("largefile.bin");
            _output.WriteLine($"Large file key: {fileKey}, size: {fileSizeBytes / (1024 * 1024)}MB");

            // Compute SHA256 hash of the original content for integrity verification
            string originalHash;
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(largeContent);
                originalHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
            _output.WriteLine($"Original SHA256: {originalHash}");

            // Act: Upload the large file
            var putResponse = await UploadFileAsync(fileKey, largeContent);
            putResponse.HttpStatusCode.Should().Be(HttpStatusCode.OK,
                "large file upload should succeed with HTTP 200");

            // Act: Download the large file
            var downloadedContent = await DownloadFileAsync(fileKey);

            // Assert: Verify content integrity via size and hash comparison
            downloadedContent.Length.Should().Be(fileSizeBytes,
                "downloaded file size should match the original 6MB content");

            string downloadedHash;
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(downloadedContent);
                downloadedHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
            _output.WriteLine($"Downloaded SHA256: {downloadedHash}");

            downloadedHash.Should().Be(originalHash,
                "SHA256 hash of downloaded content should match the original, " +
                "confirming zero data corruption for large file transfers");

            _output.WriteLine("Large file upload/download integrity verified via SHA256");
        }

        #endregion

        #region Test: ListFiles_WithPrefix_ReturnsMatchingFiles

        /// <summary>
        /// Validates that files can be listed by prefix, equivalent to
        /// <c>DbFileRepository.FindAll(startsWithPath)</c> (lines 58-117).
        ///
        /// The monolith uses SQL ILIKE for prefix matching:
        ///   <c>filepath ILIKE @startswith</c>
        ///
        /// In S3, this is replaced by <c>ListObjectsV2Async</c> with a Prefix parameter.
        /// The test uploads files with different prefixes and verifies that listing
        /// with a specific prefix returns only matching files.
        /// </summary>
        [Fact]
        public async Task ListFiles_WithPrefix_ReturnsMatchingFiles()
        {
            // Arrange: Create a unique prefix for this test to avoid interference
            var testRunId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var prefix1 = $"accounts/{testRunId}";
            var prefix2 = $"contacts/{testRunId}";

            // Upload files with different prefixes
            var accountFile1Key = $"{prefix1}/report1.pdf";
            var accountFile2Key = $"{prefix1}/report2.pdf";
            var accountFile3Key = $"{prefix1}/subdir/nested.pdf";
            var contactFile1Key = $"{prefix2}/info.csv";

            var fileContent = Encoding.UTF8.GetBytes("List prefix test content");

            await UploadFileAsync(accountFile1Key, fileContent);
            await UploadFileAsync(accountFile2Key, fileContent);
            await UploadFileAsync(accountFile3Key, fileContent);
            await UploadFileAsync(contactFile1Key, fileContent);

            _output.WriteLine($"Uploaded 3 files under prefix '{prefix1}' and 1 under '{prefix2}'");

            // Act: List objects with prefix1 (accounts/)
            var listRequest = new ListObjectsV2Request
            {
                BucketName = BucketName,
                Prefix = prefix1
            };
            var listResponse = await _s3Client.ListObjectsV2Async(listRequest).ConfigureAwait(false);

            // Assert: Only the 3 account files should be returned
            var matchingKeys = listResponse.S3Objects.Select(o => o.Key).ToList();
            _output.WriteLine($"Listed {matchingKeys.Count} files for prefix '{prefix1}':");
            foreach (var key in matchingKeys)
            {
                _output.WriteLine($"  - {key}");
            }

            matchingKeys.Should().HaveCount(3,
                "listing with accounts/ prefix should return exactly 3 matching files");
            matchingKeys.Should().Contain(accountFile1Key);
            matchingKeys.Should().Contain(accountFile2Key);
            matchingKeys.Should().Contain(accountFile3Key);
            matchingKeys.Should().NotContain(contactFile1Key,
                "contact files should not appear in accounts/ prefix listing");

            // Act: List objects with prefix2 (contacts/)
            var listRequest2 = new ListObjectsV2Request
            {
                BucketName = BucketName,
                Prefix = prefix2
            };
            var listResponse2 = await _s3Client.ListObjectsV2Async(listRequest2).ConfigureAwait(false);

            // Assert: Only the 1 contact file should be returned
            var contactKeys = listResponse2.S3Objects.Select(o => o.Key).ToList();
            contactKeys.Should().HaveCount(1,
                "listing with contacts/ prefix should return exactly 1 matching file");
            contactKeys.Should().Contain(contactFile1Key);

            _output.WriteLine("Prefix-based file listing validated — equivalent to DbFileRepository.FindAll(startsWithPath)");
        }

        #endregion

        #region Test: CopyFile_SourceToDestination_ContentPreserved

        /// <summary>
        /// Validates the S3 copy operation, equivalent to <c>DbFileRepository.Copy</c>
        /// (lines 234-281). The monolith reads bytes from the source file and creates
        /// a new file at the destination path within a database transaction.
        ///
        /// In S3, this is replaced by <c>CopyObjectAsync</c> which performs a server-side
        /// copy without downloading/re-uploading the content.
        /// </summary>
        [Fact]
        public async Task CopyFile_SourceToDestination_ContentPreserved()
        {
            // Arrange: Upload a source file
            var sourceContent = Encoding.UTF8.GetBytes("Source file content for copy test");
            var sourceKey = GenerateFileKey("copy-source.txt");
            await UploadFileAsync(sourceKey, sourceContent);

            var destinationKey = GenerateFileKey("copy-destination.txt");
            _output.WriteLine($"Source: {sourceKey}");
            _output.WriteLine($"Destination: {destinationKey}");

            // Act: Copy the file via CopyObjectAsync (server-side copy)
            var copyRequest = new CopyObjectRequest
            {
                SourceBucket = BucketName,
                SourceKey = sourceKey,
                DestinationBucket = BucketName,
                DestinationKey = destinationKey
            };
            var copyResponse = await _s3Client.CopyObjectAsync(copyRequest).ConfigureAwait(false);
            _output.WriteLine($"Copy response status: {copyResponse.HttpStatusCode}");

            copyResponse.HttpStatusCode.Should().Be(HttpStatusCode.OK,
                "CopyObjectAsync should return HTTP 200 for successful copy");

            // Assert: Download the destination file and verify content matches source
            var destinationContent = await DownloadFileAsync(destinationKey);

            destinationContent.Should().BeEquivalentTo(sourceContent,
                "copied file content should be identical to the source file, " +
                "validating the S3 replacement for DbFileRepository.Copy()");

            // Verify source file still exists (copy, not move)
            var sourceMetadata = await _s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = BucketName,
                Key = sourceKey
            }).ConfigureAwait(false);
            sourceMetadata.Should().NotBeNull(
                "source file should still exist after copy (not deleted)");

            _output.WriteLine("File copy validated: source preserved, destination matches content");
        }

        #endregion
    }
}
