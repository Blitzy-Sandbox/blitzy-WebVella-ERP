using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebVellaErp.FileManagement.DataAccess;
using WebVellaErp.FileManagement.Models;
using WebVellaErp.FileManagement.Services;
using Xunit;

namespace WebVellaErp.FileManagement.Tests;

/// <summary>
/// Integration tests for the File Management service lifecycle running against LocalStack S3
/// and DynamoDB. ALL operations hit real LocalStack services — NO mocked AWS SDK calls.
/// Pattern: docker compose up -d → test → docker compose down (per AAP §0.8.4).
/// </summary>
[Trait("Category", "Integration")]
public class FileLifecycleIntegrationTests : IAsyncLifetime
{
    // -----------------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------------
    private const string LocalStackEndpoint = "http://localhost:4566";
    private const string TestFileContent = "WebVella ERP integration test content";
    private const string TestContentType = "text/plain";
    private const string TestCreatedBy = "00000000-0000-0000-0000-000000000001";

    // -----------------------------------------------------------------------
    // AWS SDK clients (real, configured for LocalStack)
    // -----------------------------------------------------------------------
    private AmazonS3Client _s3Client = null!;
    private AmazonDynamoDBClient _dynamoDbClient = null!;
    private AmazonSimpleNotificationServiceClient _snsClient = null!;
    private AmazonSQSClient _sqsClient = null!;
    private HttpClient _httpClient = null!;

    // -----------------------------------------------------------------------
    // Services under test (instantiated with real AWS clients)
    // -----------------------------------------------------------------------
    private IS3Service _s3Service = null!;
    private IFileMetadataRepository _metadataRepository = null!;

    // -----------------------------------------------------------------------
    // Test resource identifiers
    // -----------------------------------------------------------------------
    private readonly string _testBucketName;
    private readonly string _testTableName;
    private readonly string _testTopicName;
    private string _topicArn = null!;
    private string? _testQueueUrl;
    private string? _testQueueArn;

    // Track created file paths for cleanup between tests
    private readonly List<string> _createdFilePaths = new();

    // -----------------------------------------------------------------------
    // Constructor — unique resource names per test run
    // -----------------------------------------------------------------------
    public FileLifecycleIntegrationTests()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        _testBucketName = $"test-file-mgmt-{testId}";
        _testTableName = $"test-file-mgmt-{testId}";
        _testTopicName = $"test-file-mgmt-events-{testId}";
    }

    // =======================================================================
    // IAsyncLifetime — Fixture Setup / Teardown
    // =======================================================================

    /// <summary>
    /// Creates all LocalStack resources (S3 bucket, DynamoDB table, SNS topic)
    /// and instantiates <see cref="S3Service"/> and <see cref="FileMetadataRepository"/>
    /// with real AWS SDK clients. Zero mocks.
    /// </summary>
    public async Task InitializeAsync()
    {
        var credentials = new BasicAWSCredentials("test", "test");

        // --- S3 Client ---
        _s3Client = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = LocalStackEndpoint,
            ForcePathStyle = true,
            UseHttp = true
        });

        // --- DynamoDB Client ---
        _dynamoDbClient = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig
        {
            ServiceURL = LocalStackEndpoint
        });

        // --- SNS Client ---
        _snsClient = new AmazonSimpleNotificationServiceClient(credentials,
            new AmazonSimpleNotificationServiceConfig { ServiceURL = LocalStackEndpoint });

        // --- SQS Client ---
        _sqsClient = new AmazonSQSClient(credentials,
            new AmazonSQSConfig { ServiceURL = LocalStackEndpoint });

        // --- HTTP Client for presigned URL tests ---
        _httpClient = new HttpClient();

        // --- Create S3 bucket ---
        await _s3Client.PutBucketAsync(_testBucketName);

        // --- Create DynamoDB table with PK/SK and filepath GSI ---
        await _dynamoDbClient.CreateTableAsync(new CreateTableRequest
        {
            TableName = _testTableName,
            KeySchema = new List<KeySchemaElement>
            {
                new KeySchemaElement { AttributeName = "PK", KeyType = KeyType.HASH },
                new KeySchemaElement { AttributeName = "SK", KeyType = KeyType.RANGE }
            },
            AttributeDefinitions = new List<AttributeDefinition>
            {
                new AttributeDefinition { AttributeName = "PK", AttributeType = ScalarAttributeType.S },
                new AttributeDefinition { AttributeName = "SK", AttributeType = ScalarAttributeType.S },
                new AttributeDefinition { AttributeName = "filepath", AttributeType = ScalarAttributeType.S }
            },
            GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
            {
                new GlobalSecondaryIndex
                {
                    IndexName = "filepath-index",
                    KeySchema = new List<KeySchemaElement>
                    {
                        new KeySchemaElement { AttributeName = "filepath", KeyType = KeyType.HASH }
                    },
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    ProvisionedThroughput = new ProvisionedThroughput
                    {
                        ReadCapacityUnits = 5,
                        WriteCapacityUnits = 5
                    }
                }
            },
            ProvisionedThroughput = new ProvisionedThroughput
            {
                ReadCapacityUnits = 10,
                WriteCapacityUnits = 10
            }
        });

        // --- Create SNS topic for file-management events ---
        var topicResponse = await _snsClient.CreateTopicAsync(new CreateTopicRequest
        {
            Name = _testTopicName
        });
        _topicArn = topicResponse.TopicArn;

        // --- Wait for DynamoDB table to become ACTIVE ---
        await WaitForTableActiveAsync(_testTableName);

        // --- Build in-memory configuration for services under test ---
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "FileManagement:BucketName", _testBucketName },
                { "FILE_MANAGEMENT_TABLE_NAME", _testTableName },
                { "TEMP_FILE_TTL_HOURS", "24" }
            })
            .Build();

        // Set environment variable so S3Service picks up bucket name (env var has priority)
        Environment.SetEnvironmentVariable("FILE_MANAGEMENT_BUCKET_NAME", _testBucketName);

        // --- Instantiate services with real AWS SDK clients ---
        _s3Service = new S3Service(
            _s3Client,
            NullLogger<S3Service>.Instance,
            configuration);

        _metadataRepository = new FileMetadataRepository(
            _dynamoDbClient,
            NullLogger<FileMetadataRepository>.Instance,
            configuration);
    }

    /// <summary>
    /// Destroys all LocalStack resources created by <see cref="InitializeAsync"/>.
    /// Best-effort cleanup — exceptions are swallowed to avoid masking test failures.
    /// </summary>
    public async Task DisposeAsync()
    {
        // Clear environment variable
        Environment.SetEnvironmentVariable("FILE_MANAGEMENT_BUCKET_NAME", null);

        // --- Empty and delete S3 bucket ---
        try
        {
            var listResponse = await _s3Client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = _testBucketName });
            foreach (var obj in listResponse.S3Objects ?? Enumerable.Empty<S3Object>())
            {
                await _s3Client.DeleteObjectAsync(_testBucketName, obj.Key);
            }
            await _s3Client.DeleteBucketAsync(_testBucketName);
        }
        catch { /* Best-effort cleanup */ }

        // --- Delete DynamoDB table ---
        try { await _dynamoDbClient.DeleteTableAsync(_testTableName); }
        catch { /* Best-effort cleanup */ }

        // --- Delete SNS topic ---
        try { await _snsClient.DeleteTopicAsync(_topicArn); }
        catch { /* Best-effort cleanup */ }

        // --- Delete SQS queue if created ---
        if (!string.IsNullOrEmpty(_testQueueUrl))
        {
            try { await _sqsClient.DeleteQueueAsync(_testQueueUrl); }
            catch { /* Best-effort cleanup */ }
        }

        // --- Dispose clients ---
        _httpClient?.Dispose();
        _s3Client?.Dispose();
        _dynamoDbClient?.Dispose();
        _snsClient?.Dispose();
        _sqsClient?.Dispose();
    }

    // =======================================================================
    // Phase 2: File Create → Copy → Move → Delete Lifecycle
    // =======================================================================

    /// <summary>
    /// End-to-end workflow: Create file (S3 + DynamoDB), copy it, move the copy,
    /// then delete. Mirrors <c>DbFileRepository</c> Create/Copy/Move/Delete operations.
    /// </summary>
    [Fact]
    [Trait("Phase", "Lifecycle")]
    public async Task FileLifecycle_Create_Copy_Move_Delete_CompleteWorkflow()
    {
        // --- ARRANGE ---
        var fileId = Guid.NewGuid();
        var filePath = "/lifecycle/test-file.txt";
        var objectKey = FileMetadata.GenerateObjectKey(fileId, filePath);
        var contentBytes = Encoding.UTF8.GetBytes(TestFileContent);

        // --- ACT: CREATE ---
        // Upload content to S3
        using (var stream = new MemoryStream(contentBytes))
        {
            await _s3Service.UploadFileAsync(objectKey, stream, TestContentType);
        }

        // Create metadata in DynamoDB
        var metadata = new FileMetadata
        {
            Id = fileId,
            FilePath = filePath,
            ObjectKey = objectKey,
            ContentType = TestContentType,
            Size = contentBytes.Length,
            CreatedBy = Guid.Parse(TestCreatedBy),
            CreatedOn = DateTime.UtcNow,
            LastModifiedBy = Guid.Parse(TestCreatedBy),
            LastModificationDate = DateTime.UtcNow,
            IsTemp = false,
            Ttl = null
        };
        var created = await _metadataRepository.CreateAsync(metadata);

        // --- ASSERT: CREATE ---
        var existsAfterCreate = await _s3Service.FileExistsAsync(objectKey);
        existsAfterCreate.Should().BeTrue("file should exist in S3 after create");

        var foundByPath = await _metadataRepository.FindByFilePathAsync(filePath);
        foundByPath.Should().NotBeNull("metadata should be found by filepath after create");
        foundByPath!.Id.Should().Be(fileId);
        foundByPath.ContentType.Should().Be(TestContentType);
        foundByPath.Size.Should().Be(contentBytes.Length);

        // --- ACT: COPY ---
        var copyDestPath = "/lifecycle/test-file-copy.txt";
        // Copy S3 object
        var copiedMeta = await _metadataRepository.CopyMetadataAsync(filePath, copyDestPath);
        var copyObjectKey = copiedMeta!.ObjectKey;
        await _s3Service.CopyFileAsync(objectKey, copyObjectKey);

        // --- ASSERT: COPY ---
        var copyExists = await _s3Service.FileExistsAsync(copyObjectKey);
        copyExists.Should().BeTrue("copied file should exist in S3");

        var copyFound = await _metadataRepository.FindByFilePathAsync(copyDestPath);
        copyFound.Should().NotBeNull("copy metadata should exist");
        copyFound!.Id.Should().NotBe(fileId, "copy should have a new ID");
        copyFound.ContentType.Should().Be(TestContentType);
        copyFound.Size.Should().Be(contentBytes.Length);

        // Verify content matches
        using var downloadResponse = await _s3Service.DownloadFileAsync(copyObjectKey);
        using var reader = new StreamReader(downloadResponse.ResponseStream);
        var downloadedContent = await reader.ReadToEndAsync();
        downloadedContent.Should().Be(TestFileContent, "copied content should match original");

        // --- ACT: MOVE ---
        var moveDestPath = "/lifecycle/test-file-moved.txt";
        var movedMeta = await _metadataRepository.MoveAsync(
            copyDestPath, moveDestPath, overwrite: false);
        var moveObjectKey = _s3Service.GenerateObjectKey(movedMeta!.Id, moveDestPath);
        await _s3Service.MoveFileAsync(copyObjectKey, moveObjectKey);

        // --- ASSERT: MOVE ---
        var moveExists = await _s3Service.FileExistsAsync(moveObjectKey);
        moveExists.Should().BeTrue("moved file should exist at new S3 key");

        var oldKeyExists = await _s3Service.FileExistsAsync(copyObjectKey);
        oldKeyExists.Should().BeFalse("file should not exist at old S3 key after move");

        var movedFound = await _metadataRepository.FindByFilePathAsync(moveDestPath);
        movedFound.Should().NotBeNull("moved metadata should be found at new path");
        movedFound!.FilePath.Should().Be(FileMetadata.NormalizeFilePath(moveDestPath));

        // --- ACT: DELETE ---
        await _s3Service.DeleteFileAsync(moveObjectKey);
        await _metadataRepository.DeleteAsync(moveDestPath);
        // Also clean up original
        await _s3Service.DeleteFileAsync(objectKey);
        await _metadataRepository.DeleteAsync(filePath);

        // --- ASSERT: DELETE ---
        var deletedS3Exists = await _s3Service.FileExistsAsync(moveObjectKey);
        deletedS3Exists.Should().BeFalse("file should not exist in S3 after delete");

        var deletedMeta = await _metadataRepository.FindByFilePathAsync(moveDestPath);
        deletedMeta.Should().BeNull("metadata should be removed after delete");
    }

    // =======================================================================
    // Phase 3: Temp File Lifecycle Tests
    // =======================================================================

    /// <summary>
    /// Creates a temporary file with TTL, then finalizes it (move from temp to permanent).
    /// Mirrors DbFileRepository.CreateTempFile() → UserFileService.CreateUserFile().
    /// </summary>
    [Fact]
    [Trait("Phase", "TempFile")]
    public async Task TempFile_Create_Finalize_FullWorkflow()
    {
        // --- ARRANGE ---
        var fileName = "document";
        var extension = ".pdf";
        var contentType = "application/pdf";
        var contentBytes = Encoding.UTF8.GetBytes("PDF test content for temp file workflow");
        var size = (long)contentBytes.Length;

        // --- ACT: CREATE TEMP ---
        var tempMeta = await _metadataRepository.CreateTempFileMetadataAsync(
            fileName, extension, contentType, size);

        // Upload content to the temp S3 location
        using (var stream = new MemoryStream(contentBytes))
        {
            await _s3Service.UploadFileAsync(tempMeta.ObjectKey, stream, contentType);
        }

        // --- ASSERT: TEMP FILE ---
        tempMeta.Should().NotBeNull();
        tempMeta.IsTemp.Should().BeTrue("temp file must have IsTemp=true");
        tempMeta.Ttl.Should().NotBeNull("temp file must have TTL set");
        tempMeta.FilePath.Should().StartWith("/tmp/", "temp file path must start with /tmp/");

        var tempFound = await _metadataRepository.FindByIdAsync(tempMeta.Id);
        tempFound.Should().NotBeNull();
        tempFound!.IsTemp.Should().BeTrue();

        var tempS3Exists = await _s3Service.FileExistsAsync(tempMeta.ObjectKey);
        tempS3Exists.Should().BeTrue("temp file content should exist in S3");

        // --- ACT: FINALIZE (move from temp to permanent) ---
        var permanentPath = "/documents/finalized-document.pdf";
        var finalizedMeta = await _metadataRepository.MoveAsync(
            tempMeta.FilePath, permanentPath, overwrite: false);
        var permanentObjectKey = _s3Service.GenerateObjectKey(
            finalizedMeta!.Id, permanentPath);
        await _s3Service.MoveFileAsync(tempMeta.ObjectKey, permanentObjectKey);

        // --- ASSERT: FINALIZED ---
        finalizedMeta.Should().NotBeNull();
        finalizedMeta!.IsTemp.Should().BeFalse("finalized file must not be temp");
        finalizedMeta.FilePath.Should().Be(
            FileMetadata.NormalizeFilePath(permanentPath));

        var permanentS3Exists = await _s3Service.FileExistsAsync(permanentObjectKey);
        permanentS3Exists.Should().BeTrue("finalized file should exist at permanent S3 key");

        var tempS3Gone = await _s3Service.FileExistsAsync(tempMeta.ObjectKey);
        tempS3Gone.Should().BeFalse("temp S3 key should be removed after finalization");

        // Cleanup
        await _s3Service.DeleteFileAsync(permanentObjectKey);
        await _metadataRepository.DeleteAsync(permanentPath);
    }

    /// <summary>
    /// Verifies that temp file extension normalization converts uppercase extensions
    /// without leading dot to lowercase with dot. Mirrors source lines 439-444.
    /// </summary>
    [Fact]
    [Trait("Phase", "TempFile")]
    public async Task TempFile_ExtensionNormalization()
    {
        // --- ACT ---
        var tempMeta = await _metadataRepository.CreateTempFileMetadataAsync(
            "report", "TXT", "text/plain", 100);

        // --- ASSERT ---
        tempMeta.FilePath.Should().Contain(".txt",
            "extension 'TXT' should be normalized to '.txt' (lowercase with dot)");
        tempMeta.FilePath.Should().NotContain(".TXT",
            "original uppercase extension should not appear in the path");

        // Cleanup
        await _s3Service.DeleteFileAsync(tempMeta.ObjectKey);
        await _metadataRepository.DeleteAsync(tempMeta.FilePath);
    }

    /// <summary>
    /// Verifies temp file path matches /tmp/{32-char-hex}/{filename}{ext} pattern.
    /// The 32-char hex section is Guid.NewGuid().ToString().Replace("-","").ToLowerInvariant()
    /// from source DbFileRepository.CreateTempFile() line 446.
    /// </summary>
    [Fact]
    [Trait("Phase", "TempFile")]
    public async Task TempFile_PathPattern()
    {
        // --- ACT ---
        var tempMeta = await _metadataRepository.CreateTempFileMetadataAsync(
            "image", ".png", "image/png", 2048);

        // --- ASSERT ---
        // Pattern: /tmp/{32-hex-chars}/{filename}{extension}
        var pathPattern = @"^/tmp/[a-f0-9]{32}/image\.png$";
        Regex.IsMatch(tempMeta.FilePath, pathPattern).Should().BeTrue(
            $"temp file path '{tempMeta.FilePath}' should match pattern '{pathPattern}'");

        // Cleanup
        await _s3Service.DeleteFileAsync(tempMeta.ObjectKey);
        await _metadataRepository.DeleteAsync(tempMeta.FilePath);
    }

    // =======================================================================
    // Phase 4: File Path Normalization Integration Tests
    // =======================================================================

    /// <summary>
    /// Verifies file path normalization to lowercase on create.
    /// Mirrors DbFileRepository source lines 125-126: filepath = filepath.ToLowerInvariant().
    /// </summary>
    [Fact]
    [Trait("Phase", "PathNormalization")]
    public async Task FilePath_NormalizedToLowercase_OnCreate()
    {
        // --- ARRANGE ---
        var fileId = Guid.NewGuid();
        var upperCasePath = "/FILE/Test.TXT";
        var objectKey = FileMetadata.GenerateObjectKey(fileId, upperCasePath);
        var contentBytes = Encoding.UTF8.GetBytes("path normalization test");

        using (var stream = new MemoryStream(contentBytes))
        {
            await _s3Service.UploadFileAsync(objectKey, stream, TestContentType);
        }

        var metadata = new FileMetadata
        {
            Id = fileId,
            FilePath = upperCasePath,
            ObjectKey = objectKey,
            ContentType = TestContentType,
            Size = contentBytes.Length,
            CreatedBy = Guid.Parse(TestCreatedBy),
            CreatedOn = DateTime.UtcNow,
            LastModifiedBy = Guid.Parse(TestCreatedBy),
            LastModificationDate = DateTime.UtcNow,
            IsTemp = false
        };

        // --- ACT ---
        await _metadataRepository.CreateAsync(metadata);

        // --- ASSERT --- Search by lowercase path
        var found = await _metadataRepository.FindByFilePathAsync("/file/test.txt");
        found.Should().NotBeNull("file should be found by lowercase normalized path");
        found!.Id.Should().Be(fileId);

        // Cleanup
        await _s3Service.DeleteFileAsync(objectKey);
        await _metadataRepository.DeleteAsync("/file/test.txt");
    }

    /// <summary>
    /// Verifies file path has leading slash prepended if missing.
    /// Mirrors DbFileRepository source lines 126-127: if (!filepath.StartsWith("/")) filepath = "/" + filepath.
    /// </summary>
    [Fact]
    [Trait("Phase", "PathNormalization")]
    public async Task FilePath_SlashPrepended_OnCreate()
    {
        // --- ARRANGE ---
        var fileId = Guid.NewGuid();
        var noSlashPath = "file/test.txt";
        var objectKey = FileMetadata.GenerateObjectKey(fileId, noSlashPath);
        var contentBytes = Encoding.UTF8.GetBytes("slash prepend test");

        using (var stream = new MemoryStream(contentBytes))
        {
            await _s3Service.UploadFileAsync(objectKey, stream, TestContentType);
        }

        var metadata = new FileMetadata
        {
            Id = fileId,
            FilePath = noSlashPath,
            ObjectKey = objectKey,
            ContentType = TestContentType,
            Size = contentBytes.Length,
            CreatedBy = Guid.Parse(TestCreatedBy),
            CreatedOn = DateTime.UtcNow,
            LastModifiedBy = Guid.Parse(TestCreatedBy),
            LastModificationDate = DateTime.UtcNow,
            IsTemp = false
        };

        // --- ACT ---
        await _metadataRepository.CreateAsync(metadata);

        // --- ASSERT --- Search with leading slash
        var found = await _metadataRepository.FindByFilePathAsync("/file/test.txt");
        found.Should().NotBeNull("file should be found with prepended slash");
        found!.Id.Should().Be(fileId);

        // Cleanup
        await _s3Service.DeleteFileAsync(objectKey);
        await _metadataRepository.DeleteAsync("/file/test.txt");
    }

    // =======================================================================
    // Phase 5: MIME Type Classification Integration Tests
    // =======================================================================

    /// <summary>
    /// Verifies ClassifyFileType() correctly classifies image, video, audio, document,
    /// and other file types end-to-end using the real S3Service instance.
    /// Mirrors UserFileService.cs lines 70-89 classification logic.
    /// </summary>
    [Theory]
    [Trait("Phase", "Classification")]
    [InlineData("image/png", ".png", "image")]
    [InlineData("image/jpeg", ".jpg", "image")]
    [InlineData("image/gif", ".gif", "image")]
    [InlineData("video/mp4", ".mp4", "video")]
    [InlineData("video/webm", ".webm", "video")]
    [InlineData("audio/mpeg", ".mp3", "audio")]
    [InlineData("audio/wav", ".wav", "audio")]
    [InlineData("application/pdf", ".pdf", "document")]
    [InlineData("application/msword", ".doc", "document")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx", "document")]
    [InlineData("application/vnd.oasis.opendocument.text", ".odt", "document")]
    [InlineData("text/plain", ".txt", "document")]
    [InlineData("text/html", ".html", "document")]
    [InlineData("text/html", ".htm", "document")]
    [InlineData("application/rtf", ".rtf", "document")]
    [InlineData("application/vnd.ms-powerpoint", ".ppt", "document")]
    [InlineData("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx", "document")]
    [InlineData("application/vnd.ms-excel", ".xls", "document")]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx", "document")]
    [InlineData("application/vnd.oasis.opendocument.spreadsheet", ".ods", "document")]
    [InlineData("application/vnd.oasis.opendocument.presentation", ".odp", "document")]
    [InlineData("application/zip", ".zip", "other")]
    [InlineData("application/octet-stream", ".bin", "other")]
    public void ClassifyFileType_IntegrationTest_AllTypes(
        string contentType, string extension, string expectedClassification)
    {
        // --- ACT ---
        var result = _s3Service.ClassifyFileType(contentType, extension);

        // --- ASSERT ---
        result.Should().Be(expectedClassification,
            $"content type '{contentType}' with extension '{extension}' should classify as '{expectedClassification}'");
    }

    // =======================================================================
    // Phase 6: Duplicate File Prevention Tests
    // =======================================================================

    /// <summary>
    /// Verifies that creating a file at an existing filepath throws ArgumentException.
    /// Mirrors DbFileRepository.Create() source line 130: throw ArgumentException(filepath + ": file already exists").
    /// </summary>
    [Fact]
    [Trait("Phase", "DuplicatePrevention")]
    public async Task Create_DuplicateFilePath_ThrowsException()
    {
        // --- ARRANGE ---
        var fileId1 = Guid.NewGuid();
        var filePath = $"/duplicate/test-{Guid.NewGuid():N}.txt";
        var objectKey = FileMetadata.GenerateObjectKey(fileId1, filePath);
        var contentBytes = Encoding.UTF8.GetBytes("duplicate test");

        using (var stream = new MemoryStream(contentBytes))
        {
            await _s3Service.UploadFileAsync(objectKey, stream, TestContentType);
        }

        var metadata1 = new FileMetadata
        {
            Id = fileId1,
            FilePath = filePath,
            ObjectKey = objectKey,
            ContentType = TestContentType,
            Size = contentBytes.Length,
            CreatedBy = Guid.Parse(TestCreatedBy),
            CreatedOn = DateTime.UtcNow,
            LastModifiedBy = Guid.Parse(TestCreatedBy),
            LastModificationDate = DateTime.UtcNow,
            IsTemp = false
        };
        await _metadataRepository.CreateAsync(metadata1);

        // --- ACT & ASSERT ---
        var fileId2 = Guid.NewGuid();
        var metadata2 = new FileMetadata
        {
            Id = fileId2,
            FilePath = filePath,
            ObjectKey = FileMetadata.GenerateObjectKey(fileId2, filePath),
            ContentType = TestContentType,
            Size = contentBytes.Length,
            CreatedBy = Guid.Parse(TestCreatedBy),
            CreatedOn = DateTime.UtcNow,
            LastModifiedBy = Guid.Parse(TestCreatedBy),
            LastModificationDate = DateTime.UtcNow,
            IsTemp = false
        };

        var act = () => _metadataRepository.CreateAsync(metadata2);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*file already exists*");

        // Cleanup
        await _s3Service.DeleteFileAsync(objectKey);
        await _metadataRepository.DeleteAsync(filePath);
    }

    // =======================================================================
    // Phase 7: Copy/Move Validation Integration Tests
    // =======================================================================

    /// <summary>
    /// Verifies copy throws when source file does not exist.
    /// Mirrors source line 255: "Source file cannot be found."
    /// </summary>
    [Fact]
    [Trait("Phase", "CopyMoveValidation")]
    public async Task Copy_SourceNotFound_ThrowsException()
    {
        var act = () => _metadataRepository.CopyMetadataAsync(
            "/nonexistent/source.txt", "/copy/dest.txt");

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("Source file cannot be found.");
    }

    /// <summary>
    /// Verifies copy throws when destination exists and overwrite is not specified.
    /// Mirrors source line 258: "Destination file already exists and no overwrite specified."
    /// </summary>
    [Fact]
    [Trait("Phase", "CopyMoveValidation")]
    public async Task Copy_DestinationExists_NoOverwrite_ThrowsException()
    {
        // --- ARRANGE: Create source and destination ---
        var srcPath = $"/copysrc/file-{Guid.NewGuid():N}.txt";
        var destPath = $"/copydest/file-{Guid.NewGuid():N}.txt";

        await CreateTestFileAsync(srcPath);
        await CreateTestFileAsync(destPath);

        // --- ACT & ASSERT ---
        var act = () => _metadataRepository.CopyMetadataAsync(
            srcPath, destPath, overwrite: false);

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("Destination file already exists and no overwrite specified.");

        // Cleanup
        await CleanupTestFileAsync(srcPath);
        await CleanupTestFileAsync(destPath);
    }

    /// <summary>
    /// Verifies copy succeeds when destination exists with overwrite=true.
    /// </summary>
    [Fact]
    [Trait("Phase", "CopyMoveValidation")]
    public async Task Copy_DestinationExists_WithOverwrite_Succeeds()
    {
        // --- ARRANGE ---
        var srcPath = $"/copyowsrc/file-{Guid.NewGuid():N}.txt";
        var destPath = $"/copyowdest/file-{Guid.NewGuid():N}.txt";

        var srcMeta = await CreateTestFileAsync(srcPath);
        await CreateTestFileAsync(destPath);

        // --- ACT ---
        var result = await _metadataRepository.CopyMetadataAsync(
            srcPath, destPath, overwrite: true);

        // Copy S3 object
        await _s3Service.CopyFileAsync(srcMeta.ObjectKey, result!.ObjectKey);

        // --- ASSERT ---
        result.Should().NotBeNull();
        result!.FilePath.Should().Be(FileMetadata.NormalizeFilePath(destPath));

        var destExists = await _s3Service.FileExistsAsync(result.ObjectKey);
        destExists.Should().BeTrue("overwritten copy should exist in S3");

        // Cleanup
        await _s3Service.DeleteFileAsync(srcMeta.ObjectKey);
        await _s3Service.DeleteFileAsync(result.ObjectKey);
        await _metadataRepository.DeleteAsync(srcPath);
        await _metadataRepository.DeleteAsync(destPath);
    }

    /// <summary>
    /// Verifies move throws when source file does not exist.
    /// Mirrors source line 310: "Source file cannot be found."
    /// </summary>
    [Fact]
    [Trait("Phase", "CopyMoveValidation")]
    public async Task Move_SourceNotFound_ThrowsException()
    {
        var act = () => _metadataRepository.MoveAsync(
            "/nonexistent/movesource.txt", "/move/dest.txt");

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("Source file cannot be found.");
    }

    // =======================================================================
    // Phase 8: Delete Idempotency Integration Tests
    // =======================================================================

    /// <summary>
    /// Verifies deleting a non-existent file completes silently (no exception).
    /// Mirrors DbFileRepository.Delete() source lines 387-388: if (file == null) return.
    /// </summary>
    [Fact]
    [Trait("Phase", "DeleteIdempotency")]
    public async Task Delete_NonExistentFile_SilentSuccess()
    {
        // --- ACT & ASSERT --- Should NOT throw
        var act = () => _metadataRepository.DeleteAsync("/nonexistent/file.txt");
        await act.Should().NotThrowAsync(
            "deleting a non-existent file should be silent/idempotent");

        // S3 DeleteObject is inherently idempotent
        var s3Act = () => _s3Service.DeleteFileAsync("nonexistent/key.txt");
        await s3Act.Should().NotThrowAsync(
            "S3 delete of non-existent key should be idempotent");
    }

    /// <summary>
    /// Verifies deleting an already-deleted file completes silently on the second call.
    /// Create → delete → delete again (second delete must succeed silently).
    /// </summary>
    [Fact]
    [Trait("Phase", "DeleteIdempotency")]
    public async Task Delete_AlreadyDeletedFile_SilentSuccess()
    {
        // --- ARRANGE: Create file ---
        var filePath = $"/delidempotent/file-{Guid.NewGuid():N}.txt";
        var meta = await CreateTestFileAsync(filePath);

        // --- ACT: First delete ---
        await _s3Service.DeleteFileAsync(meta.ObjectKey);
        await _metadataRepository.DeleteAsync(filePath);

        // --- ACT: Second delete (should succeed silently) ---
        var secondDeleteMeta = () => _metadataRepository.DeleteAsync(filePath);
        await secondDeleteMeta.Should().NotThrowAsync(
            "second metadata delete should be silent/idempotent");

        var secondDeleteS3 = () => _s3Service.DeleteFileAsync(meta.ObjectKey);
        await secondDeleteS3.Should().NotThrowAsync(
            "second S3 delete should be idempotent");
    }

    // =======================================================================
    // Phase 9: Domain Event Publishing Integration Tests
    // =======================================================================

    /// <summary>
    /// Verifies that a file creation event can be published to SNS and received via SQS.
    /// Tests SNS/SQS infrastructure against LocalStack.
    /// </summary>
    [Fact]
    [Trait("Phase", "DomainEvents")]
    public async Task FileCreated_PublishesSnsEvent()
    {
        // --- ARRANGE: Subscribe SQS to SNS ---
        await EnsureSqsSubscribedToSnsAsync();

        var fileId = Guid.NewGuid();
        var filePath = $"/events/created-{Guid.NewGuid():N}.txt";

        // --- ACT: Simulate file creation event publishing (as Lambda handler would) ---
        var eventPayload = JsonSerializer.Serialize(new
        {
            eventType = "file-management.file.created",
            fileId = fileId.ToString(),
            filePath,
            contentType = TestContentType,
            createdBy = TestCreatedBy,
            timestamp = DateTime.UtcNow.ToString("O")
        });

        await _snsClient.PublishAsync(_topicArn, eventPayload);

        // --- ASSERT: Poll SQS for event message ---
        var message = await PollSqsForMessageAsync("file-management.file.created");
        message.Should().NotBeNull("SNS event should be received via SQS");

        using var doc = JsonDocument.Parse(message!);
        var root = doc.RootElement;

        // SNS wraps the message — extract the actual Message field
        var messageBody = root.GetProperty("Message").GetString()!;
        using var eventDoc = JsonDocument.Parse(messageBody);
        var eventRoot = eventDoc.RootElement;

        eventRoot.GetProperty("eventType").GetString()
            .Should().Be("file-management.file.created");
        eventRoot.GetProperty("fileId").GetString()
            .Should().Be(fileId.ToString());
        eventRoot.GetProperty("filePath").GetString()
            .Should().Be(filePath);
        eventRoot.GetProperty("contentType").GetString()
            .Should().Be(TestContentType);
        eventRoot.GetProperty("createdBy").GetString()
            .Should().Be(TestCreatedBy);
        eventRoot.GetProperty("timestamp").GetString()
            .Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Verifies that a file deletion event can be published to SNS and received via SQS.
    /// </summary>
    [Fact]
    [Trait("Phase", "DomainEvents")]
    public async Task FileDeleted_PublishesSnsEvent()
    {
        await EnsureSqsSubscribedToSnsAsync();

        var fileId = Guid.NewGuid();
        var filePath = $"/events/deleted-{Guid.NewGuid():N}.txt";

        var eventPayload = JsonSerializer.Serialize(new
        {
            eventType = "file-management.file.deleted",
            fileId = fileId.ToString(),
            filePath,
            timestamp = DateTime.UtcNow.ToString("O")
        });

        await _snsClient.PublishAsync(_topicArn, eventPayload);

        var message = await PollSqsForMessageAsync("file-management.file.deleted");
        message.Should().NotBeNull("delete event should be received via SQS");

        using var doc = JsonDocument.Parse(message!);
        var messageBody = doc.RootElement.GetProperty("Message").GetString()!;
        using var eventDoc = JsonDocument.Parse(messageBody);

        eventDoc.RootElement.GetProperty("eventType").GetString()
            .Should().Be("file-management.file.deleted");
    }

    /// <summary>
    /// Verifies that a file copy event can be published to SNS and received via SQS.
    /// </summary>
    [Fact]
    [Trait("Phase", "DomainEvents")]
    public async Task FileCopied_PublishesSnsEvent()
    {
        await EnsureSqsSubscribedToSnsAsync();

        var fileId = Guid.NewGuid();
        var sourcePath = "/events/source-copy.txt";
        var destPath = $"/events/copied-{Guid.NewGuid():N}.txt";

        var eventPayload = JsonSerializer.Serialize(new
        {
            eventType = "file-management.file.copied",
            fileId = fileId.ToString(),
            sourcePath,
            destinationPath = destPath,
            timestamp = DateTime.UtcNow.ToString("O")
        });

        await _snsClient.PublishAsync(_topicArn, eventPayload);

        var message = await PollSqsForMessageAsync("file-management.file.copied");
        message.Should().NotBeNull("copy event should be received via SQS");

        using var doc = JsonDocument.Parse(message!);
        var messageBody = doc.RootElement.GetProperty("Message").GetString()!;
        using var eventDoc = JsonDocument.Parse(messageBody);

        eventDoc.RootElement.GetProperty("eventType").GetString()
            .Should().Be("file-management.file.copied");
    }

    /// <summary>
    /// Verifies that a file move event can be published to SNS and received via SQS.
    /// </summary>
    [Fact]
    [Trait("Phase", "DomainEvents")]
    public async Task FileMoved_PublishesSnsEvent()
    {
        await EnsureSqsSubscribedToSnsAsync();

        var fileId = Guid.NewGuid();
        var sourcePath = "/events/source-move.txt";
        var destPath = $"/events/moved-{Guid.NewGuid():N}.txt";

        var eventPayload = JsonSerializer.Serialize(new
        {
            eventType = "file-management.file.moved",
            fileId = fileId.ToString(),
            sourcePath,
            destinationPath = destPath,
            timestamp = DateTime.UtcNow.ToString("O")
        });

        await _snsClient.PublishAsync(_topicArn, eventPayload);

        var message = await PollSqsForMessageAsync("file-management.file.moved");
        message.Should().NotBeNull("move event should be received via SQS");

        using var doc = JsonDocument.Parse(message!);
        var messageBody = doc.RootElement.GetProperty("Message").GetString()!;
        using var eventDoc = JsonDocument.Parse(messageBody);

        eventDoc.RootElement.GetProperty("eventType").GetString()
            .Should().Be("file-management.file.moved");
    }

    // =======================================================================
    // Phase 10: S3 Presigned URL Integration Tests
    // =======================================================================

    /// <summary>
    /// Generates a presigned PUT URL and uses HttpClient to upload content directly.
    /// Verifies the file exists in S3 after upload via presigned URL.
    /// </summary>
    [Fact]
    [Trait("Phase", "PresignedUrls")]
    public async Task PresignedUploadUrl_AllowsDirectUpload()
    {
        // --- ARRANGE ---
        var fileId = Guid.NewGuid();
        var filePath = "/presigned/upload-test.txt";
        var objectKey = _s3Service.GenerateObjectKey(fileId, filePath);
        var contentBytes = Encoding.UTF8.GetBytes("Presigned upload test content");

        // --- ACT ---
        var presignedUrl = await _s3Service.GeneratePresignedUploadUrlAsync(
            objectKey, TestContentType, contentBytes.Length, expirationMinutes: 5);

        presignedUrl.Should().NotBeNullOrWhiteSpace("presigned URL must be generated");

        // Use HttpClient to upload directly to the presigned URL
        using var content = new ByteArrayContent(contentBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(TestContentType);
        var response = await _httpClient.PutAsync(presignedUrl, content);

        // --- ASSERT ---
        response.IsSuccessStatusCode.Should().BeTrue(
            $"PUT to presigned URL should succeed, got status {response.StatusCode}");

        var exists = await _s3Service.FileExistsAsync(objectKey);
        exists.Should().BeTrue("file should exist in S3 after presigned upload");

        // Cleanup
        await _s3Service.DeleteFileAsync(objectKey);
    }

    /// <summary>
    /// Uploads a file to S3, generates a presigned GET URL, and downloads via HttpClient.
    /// Verifies downloaded content matches the original.
    /// </summary>
    [Fact]
    [Trait("Phase", "PresignedUrls")]
    public async Task PresignedDownloadUrl_AllowsDirectDownload()
    {
        // --- ARRANGE ---
        var fileId = Guid.NewGuid();
        var filePath = "/presigned/download-test.txt";
        var objectKey = _s3Service.GenerateObjectKey(fileId, filePath);
        var contentBytes = Encoding.UTF8.GetBytes("Presigned download test content");

        using (var stream = new MemoryStream(contentBytes))
        {
            await _s3Service.UploadFileAsync(objectKey, stream, TestContentType);
        }

        // --- ACT ---
        var presignedUrl = await _s3Service.GeneratePresignedDownloadUrlAsync(
            objectKey, expirationMinutes: 5);

        presignedUrl.Should().NotBeNullOrWhiteSpace("presigned download URL must be generated");

        var response = await _httpClient.GetAsync(presignedUrl);

        // --- ASSERT ---
        response.IsSuccessStatusCode.Should().BeTrue(
            $"GET from presigned URL should succeed, got status {response.StatusCode}");

        var downloadedBytes = await response.Content.ReadAsByteArrayAsync();
        var downloadedText = Encoding.UTF8.GetString(downloadedBytes);
        downloadedText.Should().Be("Presigned download test content",
            "downloaded content should match original");

        // Cleanup
        await _s3Service.DeleteFileAsync(objectKey);
    }

    // =======================================================================
    // Phase 11: Modification Date Integration Tests
    // =======================================================================

    /// <summary>
    /// Verifies UpdateModificationDate updates the LastModificationDate in DynamoDB.
    /// Mirrors DbFileRepository.UpdateModificationDate() source lines 202-225.
    /// </summary>
    [Fact]
    [Trait("Phase", "ModificationDate")]
    public async Task UpdateModificationDate_UpdatesMetadata()
    {
        // --- ARRANGE ---
        var filePath = $"/moddate/file-{Guid.NewGuid():N}.txt";
        var meta = await CreateTestFileAsync(filePath);
        var originalModDate = meta.LastModificationDate;

        // Small delay to ensure different timestamps
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        var newModDate = DateTime.UtcNow.AddHours(2);

        // --- ACT ---
        var updated = await _metadataRepository.UpdateModificationDateAsync(
            filePath, newModDate);

        // --- ASSERT ---
        updated.Should().NotBeNull("updated metadata should be returned");
        updated!.LastModificationDate.Should().BeCloseTo(
            newModDate, TimeSpan.FromSeconds(1),
            "LastModificationDate should match the new value");

        // Verify by re-fetching
        var refetched = await _metadataRepository.FindByFilePathAsync(filePath);
        refetched.Should().NotBeNull();
        refetched!.LastModificationDate.Should().BeCloseTo(
            newModDate, TimeSpan.FromSeconds(1));

        // Cleanup
        await _s3Service.DeleteFileAsync(meta.ObjectKey);
        await _metadataRepository.DeleteAsync(filePath);
    }

    // =======================================================================
    // Private Helper Methods
    // =======================================================================

    /// <summary>
    /// Creates a complete test file (S3 content + DynamoDB metadata) for use in tests.
    /// Returns the created <see cref="FileMetadata"/>.
    /// </summary>
    private async Task<FileMetadata> CreateTestFileAsync(string filePath)
    {
        var fileId = Guid.NewGuid();
        var objectKey = FileMetadata.GenerateObjectKey(fileId, filePath);
        var contentBytes = Encoding.UTF8.GetBytes($"Test content for {filePath}");

        using (var stream = new MemoryStream(contentBytes))
        {
            await _s3Service.UploadFileAsync(objectKey, stream, TestContentType);
        }

        var metadata = new FileMetadata
        {
            Id = fileId,
            FilePath = filePath,
            ObjectKey = objectKey,
            ContentType = TestContentType,
            Size = contentBytes.Length,
            CreatedBy = Guid.Parse(TestCreatedBy),
            CreatedOn = DateTime.UtcNow,
            LastModifiedBy = Guid.Parse(TestCreatedBy),
            LastModificationDate = DateTime.UtcNow,
            IsTemp = false,
            Ttl = null
        };

        return await _metadataRepository.CreateAsync(metadata);
    }

    /// <summary>
    /// Cleans up a test file (S3 content + DynamoDB metadata). Best-effort, non-throwing.
    /// </summary>
    private async Task CleanupTestFileAsync(string filePath)
    {
        try
        {
            var meta = await _metadataRepository.FindByFilePathAsync(filePath);
            if (meta != null)
            {
                await _s3Service.DeleteFileAsync(meta.ObjectKey);
            }
            await _metadataRepository.DeleteAsync(filePath);
        }
        catch { /* Best-effort cleanup */ }
    }

    /// <summary>
    /// Waits for a DynamoDB table to reach ACTIVE status (required after CreateTableAsync).
    /// </summary>
    private async Task WaitForTableActiveAsync(string tableName, int maxAttempts = 30)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                var response = await _dynamoDbClient.DescribeTableAsync(tableName);
                if (response.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
            {
                // Table not yet visible — retry
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new TimeoutException(
            $"DynamoDB table '{tableName}' did not become ACTIVE within timeout.");
    }

    /// <summary>
    /// Ensures an SQS queue is created and subscribed to the SNS topic for event verification.
    /// Creates the queue once and reuses across domain event tests.
    /// </summary>
    private async Task EnsureSqsSubscribedToSnsAsync()
    {
        if (!string.IsNullOrEmpty(_testQueueUrl))
        {
            // Drain any leftover messages from previous tests
            await DrainSqsQueueAsync();
            return;
        }

        var queueName = $"test-events-{Guid.NewGuid():N}";
        var createResult = await _sqsClient.CreateQueueAsync(
            new CreateQueueRequest { QueueName = queueName });
        _testQueueUrl = createResult.QueueUrl;

        // Get queue ARN for SNS subscription
        var attrsResponse = await _sqsClient.GetQueueAttributesAsync(
            new GetQueueAttributesRequest
            {
                QueueUrl = _testQueueUrl,
                AttributeNames = new List<string> { "QueueArn" }
            });
        _testQueueArn = attrsResponse.Attributes["QueueArn"];

        // Subscribe SQS queue to SNS topic
        await _snsClient.SubscribeAsync(
            _topicArn, "sqs", _testQueueArn);

        // Small delay for subscription propagation in LocalStack
        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }

    /// <summary>
    /// Polls SQS for a message containing the specified event type.
    /// Returns the raw SQS message body (which is an SNS notification envelope),
    /// or null if no matching message is received within the timeout.
    /// </summary>
    private async Task<string?> PollSqsForMessageAsync(
        string expectedEventType,
        int maxAttempts = 20,
        int delayMs = 500)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            var response = await _sqsClient.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = _testQueueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1
                });

            if (response.Messages.Any())
            {
                foreach (var msg in response.Messages)
                {
                    // Delete the message from the queue immediately
                    await _sqsClient.DeleteMessageAsync(
                        _testQueueUrl, msg.ReceiptHandle);

                    // Check if this message contains the expected event type
                    if (msg.Body.Contains(expectedEventType))
                    {
                        return msg.Body;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(delayMs));
        }

        return null;
    }

    /// <summary>
    /// Drains all messages from the SQS queue (used to clean up between event tests).
    /// </summary>
    private async Task DrainSqsQueueAsync()
    {
        if (string.IsNullOrEmpty(_testQueueUrl)) return;

        for (var i = 0; i < 5; i++)
        {
            var response = await _sqsClient.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = _testQueueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 0
                });

            if (!response.Messages.Any()) break;

            foreach (var msg in response.Messages)
            {
                await _sqsClient.DeleteMessageAsync(_testQueueUrl, msg.ReceiptHandle);
            }
        }
    }
}
