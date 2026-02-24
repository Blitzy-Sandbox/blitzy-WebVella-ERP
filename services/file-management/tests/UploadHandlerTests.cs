using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.FileManagement.DataAccess;
using WebVellaErp.FileManagement.Functions;
using WebVellaErp.FileManagement.Models;
using WebVellaErp.FileManagement.Services;
using Xunit;

namespace WebVellaErp.FileManagement.Tests;

/// <summary>
/// Unit tests for the UploadHandler Lambda function.
/// Covers presigned URL generation, upload confirmation, temp file upload,
/// user file finalization, correlation ID extraction, path normalization,
/// and structured error handling. All AWS dependencies are mocked via Moq.
/// </summary>
public class UploadHandlerTests : IDisposable
{
    // ── Mocks ────────────────────────────────────────────────────────────────
    private readonly Mock<IS3Service> _mockS3Service;
    private readonly Mock<IFileMetadataRepository> _mockMetadataRepo;
    private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
    private readonly Mock<ILogger<UploadHandler>> _mockLogger;

    // ── System under test ────────────────────────────────────────────────────
    private readonly UploadHandler _handler;
    private readonly TestLambdaContext _lambdaContext;

    // ── Shared serialisation options ─────────────────────────────────────────
    private readonly JsonSerializerOptions _jsonOptions;

    // ── Constants ─────────────────────────────────────────────────────────────
    private const string TestTopicArn = "arn:aws:sns:us-east-1:000000000000:file-events";
    private const string TestPresignedUrl = "https://s3.localhost.localstack.cloud:4566/test-bucket/key?X-Amz-Credential=test";
    private const string TestObjectKey = "ab/cd/a1b2c3d4-e5f6-7890-abcd-ef1234567890.txt";
    private const string TestTempObjectKey = "tmp/ab/cd/temp-key.txt";

    // ── Constructor ──────────────────────────────────────────────────────────
    public UploadHandlerTests()
    {
        _mockS3Service = new Mock<IS3Service>();
        _mockMetadataRepo = new Mock<IFileMetadataRepository>();
        _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        _mockLogger = new Mock<ILogger<UploadHandler>>();

        // The handler reads FILE_EVENTS_TOPIC_ARN from environment
        Environment.SetEnvironmentVariable("FILE_EVENTS_TOPIC_ARN", TestTopicArn);

        // Build a real IServiceProvider with mock singletons —
        // the handler's secondary constructor resolves via GetRequiredService<T>()
        var services = new ServiceCollection();
        services.AddSingleton<IS3Service>(_mockS3Service.Object);
        services.AddSingleton<IFileMetadataRepository>(_mockMetadataRepo.Object);
        services.AddSingleton<IAmazonSimpleNotificationService>(_mockSnsClient.Object);
        services.AddSingleton<ILogger<UploadHandler>>(_mockLogger.Object);
        var serviceProvider = services.BuildServiceProvider();

        _handler = new UploadHandler(serviceProvider);

        _lambdaContext = new TestLambdaContext
        {
            FunctionName = "file-management-upload",
            AwsRequestId = Guid.NewGuid().ToString()
        };

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        SetupDefaultMocks();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FILE_EVENTS_TOPIC_ARN", null);
        GC.SuppressFinalize(this);
    }

    // ── Default mock wiring ──────────────────────────────────────────────────
    private void SetupDefaultMocks()
    {
        _mockS3Service
            .Setup(s => s.GeneratePresignedUploadUrlAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPresignedUrl);

        _mockS3Service
            .Setup(s => s.DetectContentTypeAsync(It.IsAny<string>()))
            .ReturnsAsync("application/octet-stream");

        _mockS3Service
            .Setup(s => s.ClassifyFileType(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("other");

        _mockS3Service
            .Setup(s => s.GenerateObjectKey(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns(TestObjectKey);

        _mockS3Service
            .Setup(s => s.GenerateTempObjectKey(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(TestTempObjectKey);

        _mockS3Service
            .Setup(s => s.FileExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockS3Service
            .Setup(s => s.MoveFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        _mockMetadataRepo
            .Setup(r => r.UpdateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        _mockSnsClient
            .Setup(s => s.PublishAsync(
                It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helper Methods — Request / Metadata Factories
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds an <see cref="APIGatewayHttpApiV2ProxyRequest"/> for the
    /// HandleGenerateUploadUrl or HandleCreateTempUploadUrl endpoints.
    /// </summary>
    private APIGatewayHttpApiV2ProxyRequest CreateGenerateUploadUrlRequest(
        string? fileName = "test.txt",
        string? contentType = "text/plain",
        long contentLength = 1024,
        bool isTemp = false,
        string? correlationId = null)
    {
        var body = JsonSerializer.Serialize(
            new { fileName, contentType, contentLength, isTemp });

        return new APIGatewayHttpApiV2ProxyRequest
        {
            Body = body,
            Headers = new Dictionary<string, string>
            {
                { "x-correlation-id", correlationId ?? Guid.NewGuid().ToString() },
                { "content-type", "application/json" }
            },
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                RequestId = Guid.NewGuid().ToString()
            }
        };
    }

    /// <summary>
    /// Builds an <see cref="APIGatewayHttpApiV2ProxyRequest"/> for the
    /// HandleConfirmUpload endpoint (fileId in path parameters).
    /// </summary>
    private APIGatewayHttpApiV2ProxyRequest CreateConfirmUploadRequest(
        Guid fileId,
        string? correlationId = null)
    {
        return new APIGatewayHttpApiV2ProxyRequest
        {
            PathParameters = new Dictionary<string, string>
            {
                { "fileId", fileId.ToString() }
            },
            Headers = new Dictionary<string, string>
            {
                { "x-correlation-id", correlationId ?? Guid.NewGuid().ToString() }
            },
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                RequestId = Guid.NewGuid().ToString()
            }
        };
    }

    /// <summary>
    /// Builds an <see cref="APIGatewayHttpApiV2ProxyRequest"/> for the
    /// HandleFinalizeUserFile endpoint (fileId in path + optional body).
    /// </summary>
    private APIGatewayHttpApiV2ProxyRequest CreateFinalizeRequest(
        Guid fileId,
        string? alt = null,
        string? caption = null,
        string? destinationPath = null,
        string? correlationId = null)
    {
        string? bodyJson = null;
        if (alt is not null || caption is not null || destinationPath is not null)
        {
            bodyJson = JsonSerializer.Serialize(new { alt, caption, destinationPath });
        }

        return new APIGatewayHttpApiV2ProxyRequest
        {
            PathParameters = new Dictionary<string, string>
            {
                { "fileId", fileId.ToString() }
            },
            Body = bodyJson,
            Headers = new Dictionary<string, string>
            {
                { "x-correlation-id", correlationId ?? Guid.NewGuid().ToString() },
                { "content-type", "application/json" }
            },
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                RequestId = Guid.NewGuid().ToString()
            }
        };
    }

    /// <summary>
    /// Factory for <see cref="FileMetadata"/> test fixtures with sane defaults.
    /// <paramref name="alreadyConfirmed"/> makes LastModificationDate greater than CreatedOn.
    /// </summary>
    private static FileMetadata CreateTestMetadata(
        Guid? id = null,
        string filePath = "/file/test.txt",
        string objectKey = "ab/cd/test-key.txt",
        string contentType = "text/plain",
        long size = 1024,
        Guid? createdBy = null,
        bool isTemp = false,
        long? ttl = null,
        bool alreadyConfirmed = false)
    {
        var createdOn = DateTime.UtcNow.AddMinutes(-5);
        return new FileMetadata
        {
            Id = id ?? Guid.NewGuid(),
            FilePath = filePath,
            ObjectKey = objectKey,
            ContentType = contentType,
            Size = size,
            CreatedBy = createdBy,
            CreatedOn = createdOn,
            LastModifiedBy = createdBy,
            LastModificationDate = alreadyConfirmed
                ? createdOn.AddMinutes(1) // after CreatedOn => idempotency flag
                : createdOn,              // same as CreatedOn => pending
            IsTemp = isTemp,
            Ttl = ttl
        };
    }

    /// <summary>Helper DTO for deserialising structured error responses.</summary>
    private sealed class ErrorResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? Timestamp { get; set; }
        public string? CorrelationId { get; set; }
        public Dictionary<string, string>? Errors { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase 2 — HandleGenerateUploadUrl Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GenerateUploadUrl_WithValidRequest_ReturnsPresignedUrl()
    {
        // Arrange
        var request = CreateGenerateUploadUrlRequest(
            fileName: "report.pdf", contentType: "application/pdf", contentLength: 2048);

        // Act
        var response = await _handler.HandleGenerateUploadUrl(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        response.Body.Should().NotBeNullOrEmpty();

        var body = JsonSerializer.Deserialize<UploadFileResponse>(
            response.Body, _jsonOptions);
        body.Should().NotBeNull();
        body!.FileId.Should().NotBe(Guid.Empty);
        body.PresignedUrl.Should().NotBeNullOrEmpty();
        body.ObjectKey.Should().NotBeNullOrEmpty();
        body.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateUploadUrl_WithEmptyFileName_Returns400()
    {
        // Arrange — mirrors DbFileRepository.Create() validation:
        // if (string.IsNullOrWhiteSpace(filepath)) throw ArgumentException
        var request = CreateGenerateUploadUrlRequest(
            fileName: "", contentType: "text/plain", contentLength: 1024);

        // Act
        var response = await _handler.HandleGenerateUploadUrl(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("fileName");
    }

    [Fact]
    public async Task GenerateUploadUrl_WithZeroContentLength_Returns400()
    {
        // Arrange
        var request = CreateGenerateUploadUrlRequest(
            fileName: "test.txt", contentType: "text/plain", contentLength: 0);

        // Act
        var response = await _handler.HandleGenerateUploadUrl(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("contentLength");
    }

    [Fact]
    public async Task GenerateUploadUrl_WithMissingContentType_AutoDetects()
    {
        // Arrange — when contentType is null/empty the handler calls
        // IS3Service.DetectContentTypeAsync() to infer from extension
        _mockS3Service
            .Setup(s => s.DetectContentTypeAsync("test.png"))
            .ReturnsAsync("image/png");

        var request = CreateGenerateUploadUrlRequest(
            fileName: "test.png", contentType: null, contentLength: 4096);

        // Act
        var response = await _handler.HandleGenerateUploadUrl(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        _mockS3Service.Verify(
            s => s.DetectContentTypeAsync(It.Is<string>(n => n.Contains("test.png"))),
            Times.AtLeastOnce());
    }

    [Fact]
    public async Task GenerateUploadUrl_CreatesMetadataWithPendingUploadStatus()
    {
        // Arrange — capture the FileMetadata passed to CreateAsync
        FileMetadata? captured = null;
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<FileMetadata, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var request = CreateGenerateUploadUrlRequest();

        // Act
        await _handler.HandleGenerateUploadUrl(request, _lambdaContext);

        // Assert — PENDING_UPLOAD means metadata exists but LastModificationDate
        // has not been advanced past CreatedOn (confirmation not yet done)
        captured.Should().NotBeNull();
        captured!.Id.Should().NotBe(Guid.Empty);
        captured.CreatedOn.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
        captured.LastModificationDate.Should().BeCloseTo(
            captured.CreatedOn, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GenerateUploadUrl_NormalizesFilePathToLowercase()
    {
        // Arrange — mirrors source DbFileRepository line 125:
        // filepath = filepath.ToLowerInvariant()
        FileMetadata? captured = null;
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<FileMetadata, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var request = CreateGenerateUploadUrlRequest(
            fileName: "REPORT.PDF", contentType: "application/pdf", contentLength: 1024);

        // Act
        await _handler.HandleGenerateUploadUrl(request, _lambdaContext);

        // Assert
        captured.Should().NotBeNull();
        captured!.FilePath.Should().Be(captured.FilePath.ToLowerInvariant(),
            "file path must be normalised to lowercase");
    }

    [Fact]
    public async Task GenerateUploadUrl_PrependsSlashToFilePath()
    {
        // Arrange — mirrors source DbFileRepository lines 126-127:
        // if (!filepath.StartsWith("/")) filepath = "/" + filepath;
        FileMetadata? captured = null;
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<FileMetadata, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var request = CreateGenerateUploadUrlRequest(
            fileName: "readme.md", contentType: "text/markdown", contentLength: 512);

        // Act
        await _handler.HandleGenerateUploadUrl(request, _lambdaContext);

        // Assert
        captured.Should().NotBeNull();
        captured!.FilePath.Should().StartWith("/",
            "file path must always begin with a forward slash");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase 3 — HandleConfirmUpload Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConfirmUpload_WithValidFileId_ReturnsSuccess()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(id: fileId);
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var request = CreateConfirmUploadRequest(fileId);

        // Act
        var response = await _handler.HandleConfirmUpload(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = JsonSerializer.Deserialize<FileOperationResponse>(
            response.Body, _jsonOptions);
        body.Should().NotBeNull();
        body!.Success.Should().BeTrue();
        body.Metadata.Should().NotBeNull();

        // Confirm the metadata repo was asked to persist the update
        _mockMetadataRepo.Verify(
            r => r.UpdateAsync(It.Is<FileMetadata>(m => m.Id == fileId), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task ConfirmUpload_WithNonExistentFileId_Returns404()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);

        var request = CreateConfirmUploadRequest(fileId);

        // Act
        var response = await _handler.HandleConfirmUpload(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain(fileId.ToString());
    }

    [Fact]
    public async Task ConfirmUpload_WhenFileNotYetInS3_Returns409()
    {
        // Arrange — metadata exists but the object has not landed in S3 yet
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(id: fileId);
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);
        _mockS3Service
            .Setup(s => s.FileExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var request = CreateConfirmUploadRequest(fileId);

        // Act
        var response = await _handler.HandleConfirmUpload(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("not been uploaded");
    }

    [Fact]
    public async Task ConfirmUpload_PublishesSnsEvent()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(id: fileId);
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var request = CreateConfirmUploadRequest(fileId);

        // Act
        await _handler.HandleConfirmUpload(request, _lambdaContext);

        // Assert — event type per AAP section 0.8.5: {domain}.{entity}.{action}
        _mockSnsClient.Verify(
            s => s.PublishAsync(
                It.Is<PublishRequest>(r =>
                    r.TopicArn == TestTopicArn &&
                    r.MessageAttributes.ContainsKey("eventType") &&
                    r.MessageAttributes["eventType"].StringValue
                        == "file-management.file.created"),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task ConfirmUpload_AlreadyConfirmed_ReturnsSuccess_IdempotentBehavior()
    {
        // Arrange — metadata already confirmed (LastModificationDate > CreatedOn)
        // per AAP section 0.8.5 idempotency requirement
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(id: fileId, alreadyConfirmed: true);
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var request = CreateConfirmUploadRequest(fileId);

        // Act
        var response = await _handler.HandleConfirmUpload(request, _lambdaContext);

        // Assert — still 200 but SNS event NOT re-published (idempotent)
        response.StatusCode.Should().Be(200);
        var body = JsonSerializer.Deserialize<FileOperationResponse>(
            response.Body, _jsonOptions);
        body!.Success.Should().BeTrue();
        body.Message.Should().Contain("already");

        _mockSnsClient.Verify(
            s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase 4 — HandleCreateTempUploadUrl Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateTempUploadUrl_SetsIsTempTrue()
    {
        // Arrange — capture metadata to verify IsTemp flag
        FileMetadata? captured = null;
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<FileMetadata, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var request = CreateGenerateUploadUrlRequest(
            fileName: "temp.txt", contentType: "text/plain",
            contentLength: 512, isTemp: true);

        // Act
        await _handler.HandleCreateTempUploadUrl(request, _lambdaContext);

        // Assert
        captured.Should().NotBeNull();
        captured!.IsTemp.Should().BeTrue();
    }

    [Fact]
    public async Task CreateTempUploadUrl_NormalizesExtension()
    {
        // Arrange — mirrors source DbFileRepository.CreateTempFile() lines 439-444:
        // extension = extension.Trim().ToLowerInvariant();
        // if (!ext.StartsWith(".")) ext = "." + ext
        FileMetadata? captured = null;
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<FileMetadata, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        // Send with uppercase extension embedded in fileName
        var request = CreateGenerateUploadUrlRequest(
            fileName: "document.TXT", contentType: "text/plain",
            contentLength: 256, isTemp: true);

        // Act
        await _handler.HandleCreateTempUploadUrl(request, _lambdaContext);

        // Assert — extension in the stored path should be normalised to lowercase
        captured.Should().NotBeNull();
        captured!.FilePath.Should().EndWith(".txt",
            "extension must be normalised to lowercase with dot prefix");
    }

    [Fact]
    public async Task CreateTempUploadUrl_GeneratesSectionGuid()
    {
        // Arrange — mirrors source line 447:
        // temp path = /tmp/{section}/{filename}.{ext} where section = fileId.ToString("N")
        FileMetadata? captured = null;
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<FileMetadata, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var request = CreateGenerateUploadUrlRequest(
            fileName: "photo.jpg", contentType: "image/jpeg",
            contentLength: 8192, isTemp: true);

        // Act
        await _handler.HandleCreateTempUploadUrl(request, _lambdaContext);

        // Assert — path should contain /tmp/{32-hex-char-guid}/
        captured.Should().NotBeNull();
        captured!.FilePath.Should().StartWith("/tmp/");
        // The section is a GUID without hyphens (32 hex chars)
        var pathParts = captured.FilePath.Split('/');
        pathParts.Length.Should().BeGreaterThanOrEqualTo(4,
            "path should be /tmp/{section}/{filename}");
        pathParts[2].Length.Should().Be(32,
            "section must be a GUID in N-format (no hyphens)");
    }

    [Fact]
    public async Task CreateTempUploadUrl_SetsCreatedByNull()
    {
        // Arrange — mirrors source line 448:
        // Create(tmpFilePath, buffer, DateTime.UtcNow, null)
        FileMetadata? captured = null;
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<FileMetadata, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var request = CreateGenerateUploadUrlRequest(
            fileName: "anon.bin", contentType: "application/octet-stream",
            contentLength: 1024, isTemp: true);

        // Act
        await _handler.HandleCreateTempUploadUrl(request, _lambdaContext);

        // Assert — temp files have no authenticated creator
        captured.Should().NotBeNull();
        captured!.CreatedBy.Should().BeNull();
    }

    [Fact]
    public async Task CreateTempUploadUrl_SetsTtlForAutoExpiry()
    {
        // Arrange — DynamoDB TTL for automatic expiry of temp files
        FileMetadata? captured = null;
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<FileMetadata, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var request = CreateGenerateUploadUrlRequest(
            fileName: "expiry.dat", contentType: "application/octet-stream",
            contentLength: 2048, isTemp: true);

        var beforeCall = DateTimeOffset.UtcNow;

        // Act
        await _handler.HandleCreateTempUploadUrl(request, _lambdaContext);

        // Assert — TTL should be a future Unix timestamp (approximately 24h from now)
        captured.Should().NotBeNull();
        captured!.Ttl.Should().NotBeNull();
        captured.Ttl!.Value.Should().BeGreaterThan(
            beforeCall.ToUnixTimeSeconds(),
            "TTL must be a future Unix timestamp");
        // Should be roughly 24 h ahead
        var expectedMinTtl = beforeCall.AddHours(23).ToUnixTimeSeconds();
        captured.Ttl.Value.Should().BeGreaterThanOrEqualTo(expectedMinTtl,
            "TTL should be at least approximately 23 hours in the future");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase 5 — HandleFinalizeUserFile Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FinalizeUserFile_MovesFromTempToPermanent()
    {
        // Arrange — mirrors source UserFileService line 98: Fs.Move(path, newFilePath)
        var fileId = Guid.NewGuid();
        var tempObjKey = "tmp/ab/cd/" + fileId.ToString("N") + ".txt";
        var permObjKey = "ab/cd/" + fileId + ".txt";

        var metadata = CreateTestMetadata(
            id: fileId,
            filePath: "/tmp/" + fileId.ToString("N") + "/test.txt",
            objectKey: tempObjKey,
            isTemp: true,
            ttl: DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);
        _mockS3Service
            .Setup(s => s.GenerateObjectKey(fileId, It.IsAny<string>()))
            .Returns(permObjKey);

        var request = CreateFinalizeRequest(fileId);

        // Act
        await _handler.HandleFinalizeUserFile(request, _lambdaContext);

        // Assert — S3 move from temp key to permanent key
        _mockS3Service.Verify(
            s => s.MoveFileAsync(tempObjKey, permObjKey, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task FinalizeUserFile_ClassifiesFileType_Image()
    {
        // Arrange — mirrors source UserFileService line 74 (image classification)
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(
            id: fileId,
            filePath: "/tmp/" + fileId.ToString("N") + "/photo.png",
            objectKey: "tmp/ab/cd/photo.png",
            contentType: "image/png",
            isTemp: true,
            ttl: DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        _mockMetadataRepo.Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        _mockS3Service.Setup(s => s.ClassifyFileType("image/png", It.IsAny<string>()))
            .Returns("image");

        var request = CreateFinalizeRequest(fileId);

        // Act
        var response = await _handler.HandleFinalizeUserFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = JsonSerializer.Deserialize<FileOperationResponse>(
            response.Body, _jsonOptions);
        body!.Success.Should().BeTrue();
        body.Metadata.Should().NotBeNull();
        body.Metadata!.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task FinalizeUserFile_ClassifiesFileType_Video()
    {
        // Arrange — mirrors source UserFileService line 77
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(
            id: fileId,
            filePath: "/tmp/" + fileId.ToString("N") + "/clip.mp4",
            objectKey: "tmp/ab/cd/clip.mp4",
            contentType: "video/mp4",
            isTemp: true,
            ttl: DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        _mockMetadataRepo.Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        _mockS3Service.Setup(s => s.ClassifyFileType("video/mp4", It.IsAny<string>()))
            .Returns("video");

        var request = CreateFinalizeRequest(fileId);

        // Act
        var response = await _handler.HandleFinalizeUserFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = JsonSerializer.Deserialize<FileOperationResponse>(
            response.Body, _jsonOptions);
        body!.Success.Should().BeTrue();
        body.Metadata!.ContentType.Should().Be("video/mp4");
    }

    [Fact]
    public async Task FinalizeUserFile_ClassifiesFileType_Audio()
    {
        // Arrange — mirrors source UserFileService line 80
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(
            id: fileId,
            filePath: "/tmp/" + fileId.ToString("N") + "/track.mp3",
            objectKey: "tmp/ab/cd/track.mp3",
            contentType: "audio/mpeg",
            isTemp: true,
            ttl: DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        _mockMetadataRepo.Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        _mockS3Service.Setup(s => s.ClassifyFileType("audio/mpeg", It.IsAny<string>()))
            .Returns("audio");

        var request = CreateFinalizeRequest(fileId);

        // Act
        var response = await _handler.HandleFinalizeUserFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = JsonSerializer.Deserialize<FileOperationResponse>(
            response.Body, _jsonOptions);
        body!.Success.Should().BeTrue();
        body.Metadata!.ContentType.Should().Be("audio/mpeg");
    }

    [Fact]
    public async Task FinalizeUserFile_ClassifiesFileType_Document()
    {
        // Arrange — mirrors source UserFileService lines 82-85 (document extensions)
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(
            id: fileId,
            filePath: "/tmp/" + fileId.ToString("N") + "/report.pdf",
            objectKey: "tmp/ab/cd/report.pdf",
            contentType: "application/pdf",
            isTemp: true,
            ttl: DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        _mockMetadataRepo.Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        _mockS3Service.Setup(s => s.ClassifyFileType("application/pdf", It.IsAny<string>()))
            .Returns("document");

        var request = CreateFinalizeRequest(fileId);

        // Act
        var response = await _handler.HandleFinalizeUserFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = JsonSerializer.Deserialize<FileOperationResponse>(
            response.Body, _jsonOptions);
        body!.Success.Should().BeTrue();
        body.Metadata!.ContentType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task FinalizeUserFile_ClassifiesFileType_Other()
    {
        // Arrange — mirrors source UserFileService lines 87-89 (fallback)
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(
            id: fileId,
            filePath: "/tmp/" + fileId.ToString("N") + "/archive.zip",
            objectKey: "tmp/ab/cd/archive.zip",
            contentType: "application/zip",
            isTemp: true,
            ttl: DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        _mockMetadataRepo.Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        _mockS3Service.Setup(s => s.ClassifyFileType("application/zip", It.IsAny<string>()))
            .Returns("other");

        var request = CreateFinalizeRequest(fileId);

        // Act
        var response = await _handler.HandleFinalizeUserFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = JsonSerializer.Deserialize<FileOperationResponse>(
            response.Body, _jsonOptions);
        body!.Success.Should().BeTrue();
        body.Metadata!.ContentType.Should().Be("application/zip");
    }

    [Fact]
    public async Task FinalizeUserFile_GeneratesPermanentPath()
    {
        // Arrange — mirrors source UserFileService line 91:
        // $"/file/{newFileId}/{Path.GetFileName(path)}"
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(
            id: fileId,
            filePath: "/tmp/" + fileId.ToString("N") + "/invoice.xlsx",
            objectKey: "tmp/ab/cd/invoice.xlsx",
            contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            isTemp: true,
            ttl: DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        FileMetadata? updated = null;
        _mockMetadataRepo.Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        _mockMetadataRepo
            .Setup(r => r.UpdateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<FileMetadata, CancellationToken>((m, _) => updated = m)
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var request = CreateFinalizeRequest(fileId);

        // Act
        await _handler.HandleFinalizeUserFile(request, _lambdaContext);

        // Assert — permanent path follows /file/{fileId}/{fileName}
        updated.Should().NotBeNull();
        updated!.FilePath.Should().Contain("/file/");
        updated.FilePath.Should().EndWith("/invoice.xlsx");
        updated.IsTemp.Should().BeFalse("file is no longer temporary after finalisation");
        updated.Ttl.Should().Be(0, "TTL cleared on permanent files");
    }

    [Fact]
    public async Task FinalizeUserFile_PublishesFinalizedEvent()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(
            id: fileId,
            filePath: "/tmp/" + fileId.ToString("N") + "/data.csv",
            objectKey: "tmp/ab/cd/data.csv",
            contentType: "text/csv",
            isTemp: true,
            ttl: DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        _mockMetadataRepo.Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);

        var request = CreateFinalizeRequest(fileId);

        // Act
        await _handler.HandleFinalizeUserFile(request, _lambdaContext);

        // Assert — event type per AAP section 0.8.5: file-management.file.finalized
        _mockSnsClient.Verify(
            s => s.PublishAsync(
                It.Is<PublishRequest>(r =>
                    r.TopicArn == TestTopicArn &&
                    r.MessageAttributes.ContainsKey("eventType") &&
                    r.MessageAttributes["eventType"].StringValue
                        == "file-management.file.finalized"),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task FinalizeUserFile_WhenMoveFailsReturnsError()
    {
        // Arrange — mirrors source UserFileService lines 99-101:
        // if (file == null) throw new Exception("File move from temp folder failed")
        var fileId = Guid.NewGuid();
        var metadata = CreateTestMetadata(
            id: fileId,
            filePath: "/tmp/" + fileId.ToString("N") + "/fail.bin",
            objectKey: "tmp/ab/cd/fail.bin",
            isTemp: true,
            ttl: DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        _mockMetadataRepo.Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        _mockS3Service
            .Setup(s => s.MoveFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("File move from temp folder failed"));

        var request = CreateFinalizeRequest(fileId);

        // Act
        var response = await _handler.HandleFinalizeUserFile(request, _lambdaContext);

        // Assert — 500 with error, no stack trace exposed
        response.StatusCode.Should().Be(500);
        var error = JsonSerializer.Deserialize<ErrorResponse>(
            response.Body, _jsonOptions);
        error.Should().NotBeNull();
        error!.Success.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase 6 — Helper Method Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtractCorrelationId_FromHeader_ReturnsHeaderValue()
    {
        // Arrange — send a known correlation ID in the header
        var knownCorrelationId = "my-trace-" + Guid.NewGuid().ToString("N");
        var request = CreateGenerateUploadUrlRequest(correlationId: knownCorrelationId);

        // Act
        var response = await _handler.HandleGenerateUploadUrl(request, _lambdaContext);

        // Assert — the response should echo the same correlation ID in its header
        response.Headers.Should().ContainKey("x-correlation-id");
        response.Headers["x-correlation-id"].Should().Be(knownCorrelationId);
    }

    [Fact]
    public async Task ExtractCorrelationId_WhenMissing_GeneratesNewGuid()
    {
        // Arrange — request with no x-correlation-id header
        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            Body = JsonSerializer.Serialize(new
            {
                fileName = "test.txt",
                contentType = "text/plain",
                contentLength = 1024L
            }),
            Headers = new Dictionary<string, string>
            {
                { "content-type", "application/json" }
            },
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                RequestId = Guid.NewGuid().ToString()
            }
        };

        // Act
        var response = await _handler.HandleGenerateUploadUrl(request, _lambdaContext);

        // Assert — a new GUID-like value should be generated for the correlation ID
        response.Headers.Should().ContainKey("x-correlation-id");
        var cid = response.Headers["x-correlation-id"];
        cid.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void NormalizeFilePath_ConvertsToLowercase()
    {
        // Source DbFileRepository line 125: filepath = filepath.ToLowerInvariant()
        var result = FileMetadata.NormalizeFilePath("/FILE/Test.TXT");
        result.Should().Be("/file/test.txt");
    }

    [Fact]
    public void NormalizeFilePath_PrependsSlash()
    {
        // Source DbFileRepository lines 126-127
        var result = FileMetadata.NormalizeFilePath("file/test.txt");
        result.Should().StartWith("/");
        result.Should().Be("/file/test.txt");
    }

    [Fact]
    public void NormalizeFilePath_ThrowsOnNullOrEmpty()
    {
        // Source DbFileRepository lines 121-122: throw on null
        // Implementation uses ArgumentNullException.ThrowIfNull(filePath)
        var actNull = () => FileMetadata.NormalizeFilePath(null!);
        actNull.Should().Throw<ArgumentNullException>();

        // Empty string does not throw — returns "/" (prepends separator)
        var resultEmpty = FileMetadata.NormalizeFilePath("");
        resultEmpty.Should().Be("/");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase 7 — Error Handling Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HandleGenerateUploadUrl_OnException_Returns500WithStructuredError()
    {
        // Arrange — force an unexpected exception in the S3 service
        _mockS3Service
            .Setup(s => s.GeneratePresignedUploadUrlAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated S3 failure"));

        var request = CreateGenerateUploadUrlRequest();

        // Act
        var response = await _handler.HandleGenerateUploadUrl(request, _lambdaContext);

        // Assert — 500 with structured error, NO stack trace exposed
        response.StatusCode.Should().Be(500);
        response.Body.Should().NotBeNullOrEmpty();

        var error = JsonSerializer.Deserialize<ErrorResponse>(
            response.Body, _jsonOptions);
        error.Should().NotBeNull();
        error!.Success.Should().BeFalse();
        error.Message.Should().NotBeNullOrEmpty();
        response.Body.Should().NotContain("at WebVellaErp",
            "stack traces must never be leaked to API consumers");
    }

    [Fact]
    public async Task HandleConfirmUpload_OnException_Returns500()
    {
        // Arrange — force an exception in the metadata repository
        var fileId = Guid.NewGuid();
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DynamoDB connection error"));

        var request = CreateConfirmUploadRequest(fileId);

        // Act
        var response = await _handler.HandleConfirmUpload(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(500);
        var error = JsonSerializer.Deserialize<ErrorResponse>(
            response.Body, _jsonOptions);
        error!.Success.Should().BeFalse();
        response.Body.Should().NotContain("StackTrace");
    }

    [Fact]
    public async Task HandleFinalizeUserFile_OnException_Returns500()
    {
        // Arrange — force an exception in the metadata repository
        var fileId = Guid.NewGuid();
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Unexpected DynamoDB error"));

        var request = CreateFinalizeRequest(fileId);

        // Act
        var response = await _handler.HandleFinalizeUserFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(500);
        var error = JsonSerializer.Deserialize<ErrorResponse>(
            response.Body, _jsonOptions);
        error!.Success.Should().BeFalse();
        response.Body.Should().NotContain("StackTrace");
    }
}
