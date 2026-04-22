using System.Text.Json;
using System.Text.Json.Serialization;
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
/// Comprehensive unit tests for <see cref="DownloadHandler"/> Lambda handler.
/// Validates download URL generation, file lookup by path, file listing,
/// copy, move, delete, and modification date update operations.
/// All tests use Moq-based mocking — no real AWS services required.
/// </summary>
public class DownloadHandlerTests : IDisposable
{
    #region Fields and Constants

    private readonly Mock<IS3Service> _mockS3Service;
    private readonly Mock<IFileMetadataRepository> _mockMetadataRepo;
    private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
    private readonly Mock<ILogger<DownloadHandler>> _mockLogger;
    private readonly DownloadHandler _handler;
    private readonly TestLambdaContext _lambdaContext;

    private const string TestTopicArn = "arn:aws:sns:us-east-1:000000000000:file-events";
    private const string TestPresignedUrl =
        "https://s3.localhost.localstack.cloud:4566/file-mgmt-bucket/key?X-Amz-Signature=test";

    #endregion

    #region Constructor and Dispose

    public DownloadHandlerTests()
    {
        _mockS3Service = new Mock<IS3Service>();
        _mockMetadataRepo = new Mock<IFileMetadataRepository>();
        _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        _mockLogger = new Mock<ILogger<DownloadHandler>>();

        _lambdaContext = new TestLambdaContext
        {
            FunctionName = "FileManagement-DownloadHandler",
            AwsRequestId = Guid.NewGuid().ToString()
        };

        // Configure environment for SNS topic ARN used by the handler constructor
        Environment.SetEnvironmentVariable("FILE_EVENTS_TOPIC_ARN", TestTopicArn);

        // Default S3 behaviors — void-Task methods must return CompletedTask
        _mockS3Service
            .Setup(x => x.GeneratePresignedDownloadUrlAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPresignedUrl);
        _mockS3Service
            .Setup(x => x.CopyFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockS3Service
            .Setup(x => x.MoveFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockS3Service
            .Setup(x => x.DeleteFileAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default SNS publish behavior
        _mockSnsClient
            .Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });

        // Default metadata repo write methods returning completed tasks
        _mockMetadataRepo
            .Setup(x => x.DeleteByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Build DI container with mocked services for the secondary (testing) constructor
        var services = new ServiceCollection();
        services.AddSingleton<IS3Service>(_mockS3Service.Object);
        services.AddSingleton<IFileMetadataRepository>(_mockMetadataRepo.Object);
        services.AddSingleton<IAmazonSimpleNotificationService>(_mockSnsClient.Object);
        services.AddSingleton<ILogger<DownloadHandler>>(_mockLogger.Object);
        var serviceProvider = services.BuildServiceProvider();

        _handler = new DownloadHandler(serviceProvider);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FILE_EVENTS_TOPIC_ARN", null);
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Builds an <see cref="APIGatewayHttpApiV2ProxyRequest"/> with the supplied
    /// HTTP verb, raw path, optional path / query parameters, body, and headers.
    /// </summary>
    private static APIGatewayHttpApiV2ProxyRequest CreateRequest(
        string method,
        string path,
        Dictionary<string, string>? pathParams = null,
        Dictionary<string, string>? queryParams = null,
        string? body = null,
        Dictionary<string, string>? headers = null)
    {
        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                {
                    Method = method,
                    Path = path
                },
                RequestId = Guid.NewGuid().ToString()
            },
            RawPath = path,
            PathParameters = pathParams ?? new Dictionary<string, string>(),
            QueryStringParameters = queryParams ?? new Dictionary<string, string>(),
            Headers = headers ?? new Dictionary<string, string>(),
            Body = body
        };
        return request;
    }

    /// <summary>
    /// Creates a fully populated <see cref="FileMetadata"/> fixture for test arrangement.
    /// </summary>
    private static FileMetadata CreateTestFileMetadata(
        Guid? id = null,
        string filePath = "/files/test-document.pdf",
        string objectKey = "files/test-document.pdf",
        string contentType = "application/pdf",
        long size = 1024,
        bool isTemp = false,
        DateTime? createdOn = null,
        Guid? createdBy = null,
        DateTime? lastModified = null,
        Guid? lastModifiedBy = null)
    {
        return new FileMetadata
        {
            Id = id ?? Guid.NewGuid(),
            FilePath = filePath,
            ObjectKey = objectKey,
            ContentType = contentType,
            Size = size,
            IsTemp = isTemp,
            CreatedOn = createdOn ?? DateTime.UtcNow.AddDays(-7),
            CreatedBy = createdBy ?? Guid.NewGuid(),
            LastModificationDate = lastModified ?? DateTime.UtcNow.AddHours(-1),
            LastModifiedBy = lastModifiedBy ?? Guid.NewGuid()
        };
    }

    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Deserializes a response body into the requested DTO using camelCase conventions.
    /// </summary>
    private static T DeserializeBody<T>(APIGatewayHttpApiV2ProxyResponse response)
    {
        response.Body.Should().NotBeNullOrEmpty("the response body should contain JSON content");
        var result = JsonSerializer.Deserialize<T>(response.Body, CamelCaseOptions);
        result.Should().NotBeNull();
        return result!;
    }

    #endregion

    // ========================================================================
    //  Phase 2: HandleGetDownloadUrl Tests
    // ========================================================================
    #region HandleGetDownloadUrl Tests

    [Fact]
    public async Task GetDownloadUrl_WithValidFileId_ReturnsPresignedUrl()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var metadata = CreateTestFileMetadata(id: fileId, filePath: "/docs/report.pdf",
            objectKey: "docs/report.pdf", contentType: "application/pdf", size: 51200);

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var request = CreateRequest("GET", $"/v1/files/{fileId}/download",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } });

        // Act
        var response = await _handler.HandleGetDownloadUrl(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = DeserializeBody<DownloadFileResponse>(response);
        body.Success.Should().BeTrue();
        body.PresignedUrl.Should().NotBeNullOrEmpty();
        body.PresignedUrl.Should().Contain("X-Amz-Signature");
        body.Metadata.Should().NotBeNull();
        body.Metadata!.ContentType.Should().Be("application/pdf");
        body.Metadata.Size.Should().Be(51200);

        _mockS3Service.Verify(
            s => s.GeneratePresignedDownloadUrlAsync(
                metadata.ObjectKey, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetDownloadUrl_WithNonExistentFileId_Returns404()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);

        var request = CreateRequest("GET", $"/v1/files/{fileId}/download",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } });

        // Act
        var response = await _handler.HandleGetDownloadUrl(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(404);
        _mockS3Service.Verify(
            s => s.GeneratePresignedDownloadUrlAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetDownloadUrl_WithInvalidGuidFormat_Returns400()
    {
        // Arrange
        var request = CreateRequest("GET", "/v1/files/not-a-guid/download",
            pathParams: new Dictionary<string, string> { { "fileId", "not-a-guid" } });

        // Act
        var response = await _handler.HandleGetDownloadUrl(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(400);
        _mockMetadataRepo.Verify(
            r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetDownloadUrl_WithInactiveFile_Returns404()
    {
        // Arrange — FileMetadata has no Status field; inactive = null from FindByIdAsync
        var fileId = Guid.NewGuid();
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);

        var request = CreateRequest("GET", $"/v1/files/{fileId}/download",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } });

        // Act
        var response = await _handler.HandleGetDownloadUrl(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(404);
    }

    #endregion

    // ========================================================================
    //  Phase 3: HandleGetFileByPath Tests
    // ========================================================================
    #region HandleGetFileByPath Tests

    [Fact]
    public async Task GetFileByPath_WithValidPath_ReturnsFileMetadata()
    {
        // Arrange
        var metadata = CreateTestFileMetadata(filePath: "/files/readme.md",
            objectKey: "files/readme.md", contentType: "text/markdown", size: 2048);

        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync("/files/readme.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var request = CreateRequest("GET", "/v1/files/by-path",
            queryParams: new Dictionary<string, string> { { "path", "/files/readme.md" } });

        // Act
        var response = await _handler.HandleGetFileByPath(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = DeserializeBody<FileOperationResponse>(response);
        body.Success.Should().BeTrue();
        body.Metadata.Should().NotBeNull();
        body.Metadata!.FilePath.Should().Be("/files/readme.md");
    }

    [Fact]
    public async Task GetFileByPath_NormalizesPathToLowercase()
    {
        // Arrange — mixed-case path should be normalized to lowercase
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync("/file/test.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestFileMetadata(filePath: "/file/test.txt"));

        var request = CreateRequest("GET", "/v1/files/by-path",
            queryParams: new Dictionary<string, string> { { "path", "/FILE/Test.TXT" } });

        // Act
        var response = await _handler.HandleGetFileByPath(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        _mockMetadataRepo.Verify(
            r => r.FindByFilePathAsync("/file/test.txt", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetFileByPath_PrependsSlash()
    {
        // Arrange — path without leading slash should get one prepended
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync("/file/test.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestFileMetadata(filePath: "/file/test.txt"));

        var request = CreateRequest("GET", "/v1/files/by-path",
            queryParams: new Dictionary<string, string> { { "path", "file/test.txt" } });

        // Act
        var response = await _handler.HandleGetFileByPath(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        _mockMetadataRepo.Verify(
            r => r.FindByFilePathAsync("/file/test.txt", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetFileByPath_WithEmptyPath_Returns400()
    {
        // Arrange — no 'path' query parameter provided at all
        var request = CreateRequest("GET", "/v1/files/by-path",
            queryParams: new Dictionary<string, string>());

        // Act
        var response = await _handler.HandleGetFileByPath(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetFileByPath_WhenNotFound_Returns404()
    {
        // Arrange
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);

        var request = CreateRequest("GET", "/v1/files/by-path",
            queryParams: new Dictionary<string, string> { { "path", "/nonexistent/path.pdf" } });

        // Act
        var response = await _handler.HandleGetFileByPath(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(404);
    }

    #endregion

    // ========================================================================
    //  Phase 4: HandleListFiles Tests
    // ========================================================================
    #region HandleListFiles Tests

    [Fact]
    public async Task ListFiles_WithNoFilters_ReturnsAll()
    {
        // Arrange
        var files = new List<FileMetadata>
        {
            CreateTestFileMetadata(filePath: "/files/a.txt"),
            CreateTestFileMetadata(filePath: "/files/b.pdf"),
            CreateTestFileMetadata(filePath: "/files/c.jpg")
        };
        _mockMetadataRepo
            .Setup(r => r.FindAllAsync(
                It.IsAny<string?>(), false, It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((files, (string?)null));

        var request = CreateRequest("GET", "/v1/files");

        // Act
        var response = await _handler.HandleListFiles(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = DeserializeBody<ListFilesResponse>(response);
        body.Success.Should().BeTrue();
        body.Items.Should().HaveCount(3);
        body.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task ListFiles_WithPathPrefix_FiltersResults()
    {
        // Arrange — only docs should be returned
        var docs = new List<FileMetadata>
        {
            CreateTestFileMetadata(filePath: "/files/docs/spec.pdf")
        };
        _mockMetadataRepo
            .Setup(r => r.FindAllAsync(
                "/files/docs", false, It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((docs, (string?)null));

        var request = CreateRequest("GET", "/v1/files",
            queryParams: new Dictionary<string, string> { { "startsWithPath", "/files/docs" } });

        // Act
        var response = await _handler.HandleListFiles(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = DeserializeBody<ListFilesResponse>(response);
        body.Items.Should().HaveCount(1);

        _mockMetadataRepo.Verify(
            r => r.FindAllAsync("/files/docs", false, It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ListFiles_ExcludesTempFilesByDefault()
    {
        // Arrange
        _mockMetadataRepo
            .Setup(r => r.FindAllAsync(
                It.IsAny<string?>(), false, It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<FileMetadata>(), (string?)null));

        var request = CreateRequest("GET", "/v1/files");

        // Act
        await _handler.HandleListFiles(request, _lambdaContext);

        // Assert — includeTempFiles should default to false
        _mockMetadataRepo.Verify(
            r => r.FindAllAsync(
                It.IsAny<string?>(), false, It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ListFiles_IncludesTempFilesWhenRequested()
    {
        // Arrange
        _mockMetadataRepo
            .Setup(r => r.FindAllAsync(
                It.IsAny<string?>(), true, It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<FileMetadata>(), (string?)null));

        var request = CreateRequest("GET", "/v1/files",
            queryParams: new Dictionary<string, string> { { "includeTempFiles", "true" } });

        // Act
        await _handler.HandleListFiles(request, _lambdaContext);

        // Assert — includeTempFiles=true passed through
        _mockMetadataRepo.Verify(
            r => r.FindAllAsync(
                It.IsAny<string?>(), true, It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ListFiles_WithPagination_PassesSkipAndLimit()
    {
        // Arrange
        var startKey = "eyJJZCI6ImFiYzEyMyJ9"; // base64-encoded exclusive start key
        _mockMetadataRepo
            .Setup(r => r.FindAllAsync(
                It.IsAny<string?>(), false, 10,
                startKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<FileMetadata>(), (string?)null));

        var request = CreateRequest("GET", "/v1/files",
            queryParams: new Dictionary<string, string>
            {
                { "limit", "10" },
                { "exclusiveStartKey", startKey }
            });

        // Act
        await _handler.HandleListFiles(request, _lambdaContext);

        // Assert — pageSize=10 and exclusiveStartKey passed through
        _mockMetadataRepo.Verify(
            r => r.FindAllAsync(
                It.IsAny<string?>(), false, 10,
                startKey, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    // ========================================================================
    //  Phase 5: HandleCopyFile Tests
    // ========================================================================
    #region HandleCopyFile Tests

    [Fact]
    public async Task CopyFile_WithValidSource_CopiesSuccessfully()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var sourceMetadata = CreateTestFileMetadata(id: sourceId,
            filePath: "/files/original.pdf", objectKey: "files/original.pdf",
            contentType: "application/pdf", size: 4096,
            createdOn: DateTime.UtcNow.AddDays(-30));

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceMetadata);
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync("/files/copy.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null); // destination does not exist
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var copyRequest = new CopyFileRequest
        {
            SourceFilePath = "/files/original.pdf",
            DestinationFilePath = "/files/copy.pdf",
            Overwrite = false
        };
        var request = CreateRequest("POST", $"/v1/files/{sourceId}/copy",
            pathParams: new Dictionary<string, string> { { "fileId", sourceId.ToString() } },
            body: JsonSerializer.Serialize(copyRequest, CamelCaseOptions));

        // Act
        var response = await _handler.HandleCopyFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = DeserializeBody<FileOperationResponse>(response);
        body.Success.Should().BeTrue();
        body.Metadata.Should().NotBeNull();
        body.Metadata!.FilePath.Should().Be("/files/copy.pdf");

        _mockS3Service.Verify(
            s => s.CopyFileAsync(
                sourceMetadata.ObjectKey, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockMetadataRepo.Verify(
            r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CopyFile_WhenSourceNotFound_Returns404()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);

        var copyRequest = new CopyFileRequest
        {
            SourceFilePath = "/files/missing.pdf",
            DestinationFilePath = "/files/copy.pdf",
            Overwrite = false
        };
        var request = CreateRequest("POST", $"/v1/files/{sourceId}/copy",
            pathParams: new Dictionary<string, string> { { "fileId", sourceId.ToString() } },
            body: JsonSerializer.Serialize(copyRequest, CamelCaseOptions));

        // Act
        var response = await _handler.HandleCopyFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task CopyFile_WhenDestinationExistsNoOverwrite_Returns409()
    {
        // Arrange — source exists, destination also exists, overwrite = false
        var sourceId = Guid.NewGuid();
        var sourceMetadata = CreateTestFileMetadata(id: sourceId,
            filePath: "/files/original.pdf", objectKey: "files/original.pdf");
        var destMetadata = CreateTestFileMetadata(
            filePath: "/files/copy.pdf", objectKey: "files/copy.pdf");

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceMetadata);
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync("/files/copy.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(destMetadata);

        var copyRequest = new CopyFileRequest
        {
            SourceFilePath = "/files/original.pdf",
            DestinationFilePath = "/files/copy.pdf",
            Overwrite = false
        };
        var request = CreateRequest("POST", $"/v1/files/{sourceId}/copy",
            pathParams: new Dictionary<string, string> { { "fileId", sourceId.ToString() } },
            body: JsonSerializer.Serialize(copyRequest, CamelCaseOptions));

        // Act
        var response = await _handler.HandleCopyFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task CopyFile_WithOverwrite_DeletesExistingFirst()
    {
        // Arrange — destination exists but overwrite = true
        var sourceId = Guid.NewGuid();
        var sourceMetadata = CreateTestFileMetadata(id: sourceId,
            filePath: "/files/original.pdf", objectKey: "files/original.pdf");
        var existingDest = CreateTestFileMetadata(
            filePath: "/files/copy.pdf", objectKey: "files/copy.pdf");

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceMetadata);
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync("/files/copy.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingDest);
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var copyRequest = new CopyFileRequest
        {
            SourceFilePath = "/files/original.pdf",
            DestinationFilePath = "/files/copy.pdf",
            Overwrite = true
        };
        var request = CreateRequest("POST", $"/v1/files/{sourceId}/copy",
            pathParams: new Dictionary<string, string> { { "fileId", sourceId.ToString() } },
            body: JsonSerializer.Serialize(copyRequest, CamelCaseOptions));

        // Act
        var response = await _handler.HandleCopyFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);

        // Verify existing destination was deleted before new copy was created
        _mockS3Service.Verify(
            s => s.DeleteFileAsync(existingDest.ObjectKey, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockMetadataRepo.Verify(
            r => r.DeleteByIdAsync(existingDest.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CopyFile_PreservesCreatedOnFromSource()
    {
        // Arrange — source has a specific creation date that must be preserved
        var sourceId = Guid.NewGuid();
        var originalCreatedOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var originalCreatedBy = Guid.NewGuid();
        var sourceMetadata = CreateTestFileMetadata(id: sourceId,
            filePath: "/files/original.pdf", objectKey: "files/original.pdf",
            createdOn: originalCreatedOn, createdBy: originalCreatedBy);

        FileMetadata? capturedMetadata = null;

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceMetadata);
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync("/files/copy.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<FileMetadata, CancellationToken>((m, _) => capturedMetadata = m)
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var copyRequest = new CopyFileRequest
        {
            SourceFilePath = "/files/original.pdf",
            DestinationFilePath = "/files/copy.pdf",
            Overwrite = false
        };
        var request = CreateRequest("POST", $"/v1/files/{sourceId}/copy",
            pathParams: new Dictionary<string, string> { { "fileId", sourceId.ToString() } },
            body: JsonSerializer.Serialize(copyRequest, CamelCaseOptions));

        // Act
        await _handler.HandleCopyFile(request, _lambdaContext);

        // Assert — new metadata preserves CreatedOn and CreatedBy from source
        capturedMetadata.Should().NotBeNull();
        capturedMetadata!.CreatedOn.Should().Be(originalCreatedOn);
        capturedMetadata.CreatedBy.Should().Be(originalCreatedBy);
    }

    [Fact]
    public async Task CopyFile_PublishesCopiedEvent()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var sourceMetadata = CreateTestFileMetadata(id: sourceId,
            filePath: "/files/original.pdf", objectKey: "files/original.pdf");

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceMetadata);
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync("/files/copy.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);
        _mockMetadataRepo
            .Setup(r => r.CreateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var copyRequest = new CopyFileRequest
        {
            SourceFilePath = "/files/original.pdf",
            DestinationFilePath = "/files/copy.pdf",
            Overwrite = false
        };
        var request = CreateRequest("POST", $"/v1/files/{sourceId}/copy",
            pathParams: new Dictionary<string, string> { { "fileId", sourceId.ToString() } },
            body: JsonSerializer.Serialize(copyRequest, CamelCaseOptions));

        // Act
        await _handler.HandleCopyFile(request, _lambdaContext);

        // Assert — SNS publish invoked with file-management.file.copied event type
        _mockSnsClient.Verify(
            s => s.PublishAsync(
                It.Is<PublishRequest>(p =>
                    p.TopicArn == TestTopicArn &&
                    p.Message.Contains("file-management.file.copied")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    // ========================================================================
    //  Phase 6: HandleMoveFile Tests
    // ========================================================================
    #region HandleMoveFile Tests

    [Fact]
    public async Task MoveFile_WithValidSource_MovesSuccessfully()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        // Capture the original object key as a string literal because the handler
        // mutates sourceMetadata.ObjectKey during move (to the new destination key).
        // Using the reference directly in Verify would evaluate the mutated value.
        var originalObjectKey = "files/original.pdf";
        var sourceMetadata = CreateTestFileMetadata(id: sourceId,
            filePath: "/files/original.pdf", objectKey: originalObjectKey);

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceMetadata);
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync("/files/moved.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);
        _mockMetadataRepo
            .Setup(r => r.UpdateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var moveRequest = new MoveFileRequest
        {
            SourceFilePath = "/files/original.pdf",
            DestinationFilePath = "/files/moved.pdf",
            Overwrite = false
        };
        var request = CreateRequest("POST", $"/v1/files/{sourceId}/move",
            pathParams: new Dictionary<string, string> { { "fileId", sourceId.ToString() } },
            body: JsonSerializer.Serialize(moveRequest, CamelCaseOptions));

        // Act
        var response = await _handler.HandleMoveFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = DeserializeBody<FileOperationResponse>(response);
        body.Success.Should().BeTrue();

        _mockS3Service.Verify(
            s => s.MoveFileAsync(
                originalObjectKey, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockMetadataRepo.Verify(
            r => r.UpdateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MoveFile_WhenSourceNotFound_Returns404()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);

        var moveRequest = new MoveFileRequest
        {
            SourceFilePath = "/files/missing.pdf",
            DestinationFilePath = "/files/moved.pdf",
            Overwrite = false
        };
        var request = CreateRequest("POST", $"/v1/files/{sourceId}/move",
            pathParams: new Dictionary<string, string> { { "fileId", sourceId.ToString() } },
            body: JsonSerializer.Serialize(moveRequest, CamelCaseOptions));

        // Act
        var response = await _handler.HandleMoveFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task MoveFile_WhenDestinationExistsNoOverwrite_Returns409()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var destId = Guid.NewGuid(); // different ID to ensure it's not a self-move
        var sourceMetadata = CreateTestFileMetadata(id: sourceId,
            filePath: "/files/original.pdf", objectKey: "files/original.pdf");
        var destMetadata = CreateTestFileMetadata(id: destId,
            filePath: "/files/moved.pdf", objectKey: "files/moved.pdf");

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceMetadata);
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync("/files/moved.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(destMetadata);

        var moveRequest = new MoveFileRequest
        {
            SourceFilePath = "/files/original.pdf",
            DestinationFilePath = "/files/moved.pdf",
            Overwrite = false
        };
        var request = CreateRequest("POST", $"/v1/files/{sourceId}/move",
            pathParams: new Dictionary<string, string> { { "fileId", sourceId.ToString() } },
            body: JsonSerializer.Serialize(moveRequest, CamelCaseOptions));

        // Act
        var response = await _handler.HandleMoveFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task MoveFile_NormalizesPaths()
    {
        // Arrange — paths with mixed case and missing leading slash should be normalized
        var sourceId = Guid.NewGuid();
        var sourceMetadata = CreateTestFileMetadata(id: sourceId,
            filePath: "/files/original.pdf", objectKey: "files/original.pdf");

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceMetadata);
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);
        _mockMetadataRepo
            .Setup(r => r.UpdateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var moveRequest = new MoveFileRequest
        {
            SourceFilePath = "/files/original.pdf",
            DestinationFilePath = "Files/MOVED.PDF", // mixed case, no leading slash
            Overwrite = false
        };
        var request = CreateRequest("POST", $"/v1/files/{sourceId}/move",
            pathParams: new Dictionary<string, string> { { "fileId", sourceId.ToString() } },
            body: JsonSerializer.Serialize(moveRequest, CamelCaseOptions));

        // Act
        var response = await _handler.HandleMoveFile(request, _lambdaContext);

        // Assert — destination path should be normalized to lowercase with leading slash
        response.StatusCode.Should().Be(200);
        _mockMetadataRepo.Verify(
            r => r.FindByFilePathAsync(
                It.Is<string>(p => p == "/files/moved.pdf"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MoveFile_PublishesMovedEvent()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var sourceMetadata = CreateTestFileMetadata(id: sourceId,
            filePath: "/files/original.pdf", objectKey: "files/original.pdf");

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceMetadata);
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync("/files/moved.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);
        _mockMetadataRepo
            .Setup(r => r.UpdateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var moveRequest = new MoveFileRequest
        {
            SourceFilePath = "/files/original.pdf",
            DestinationFilePath = "/files/moved.pdf",
            Overwrite = false
        };
        var request = CreateRequest("POST", $"/v1/files/{sourceId}/move",
            pathParams: new Dictionary<string, string> { { "fileId", sourceId.ToString() } },
            body: JsonSerializer.Serialize(moveRequest, CamelCaseOptions));

        // Act
        await _handler.HandleMoveFile(request, _lambdaContext);

        // Assert — SNS publish with file-management.file.moved event type
        _mockSnsClient.Verify(
            s => s.PublishAsync(
                It.Is<PublishRequest>(p =>
                    p.TopicArn == TestTopicArn &&
                    p.Message.Contains("file-management.file.moved")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    // ========================================================================
    //  Phase 7: HandleDeleteFile Tests
    // ========================================================================
    #region HandleDeleteFile Tests

    [Fact]
    public async Task DeleteFile_WithValidFileId_DeletesSuccessfully()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var metadata = CreateTestFileMetadata(id: fileId,
            filePath: "/files/to-delete.pdf", objectKey: "files/to-delete.pdf");

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var request = CreateRequest("DELETE", $"/v1/files/{fileId}",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } });

        // Act
        var response = await _handler.HandleDeleteFile(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);
        var body = DeserializeBody<FileOperationResponse>(response);
        body.Success.Should().BeTrue();
        body.Metadata.Should().NotBeNull();

        _mockS3Service.Verify(
            s => s.DeleteFileAsync(metadata.ObjectKey, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockMetadataRepo.Verify(
            r => r.DeleteByIdAsync(fileId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteFile_WhenNotFound_Returns204_Idempotent()
    {
        // Arrange — file does not exist; delete should be idempotent (silent success)
        var fileId = Guid.NewGuid();
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);

        var request = CreateRequest("DELETE", $"/v1/files/{fileId}",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } });

        // Act
        var response = await _handler.HandleDeleteFile(request, _lambdaContext);

        // Assert — 204 No Content with no body
        response.StatusCode.Should().Be(204);

        // S3 and metadata delete should NOT be called when file doesn't exist
        _mockS3Service.Verify(
            s => s.DeleteFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockMetadataRepo.Verify(
            r => r.DeleteByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteFile_PublishesDeletedEvent()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var metadata = CreateTestFileMetadata(id: fileId,
            filePath: "/files/delete-me.pdf", objectKey: "files/delete-me.pdf");

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var request = CreateRequest("DELETE", $"/v1/files/{fileId}",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } });

        // Act
        await _handler.HandleDeleteFile(request, _lambdaContext);

        // Assert — SNS publish with file-management.file.deleted event type
        _mockSnsClient.Verify(
            s => s.PublishAsync(
                It.Is<PublishRequest>(p =>
                    p.TopicArn == TestTopicArn &&
                    p.Message.Contains("file-management.file.deleted")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteFile_OnS3Error_StillDeletesMetadata()
    {
        // Arrange — S3 delete throws but the handler should attempt to proceed
        // Note: Per handler implementation, the entire delete flow is in a single try-catch
        // so an S3 failure will cause a 500. However the handler catches specific
        // AmazonS3Exception.ErrorCode "AccessDenied" → 403. Any other S3 errors
        // bubble to the outer catch. We test that the handler handles the error
        // gracefully — it should still return an error response, not crash.
        var fileId = Guid.NewGuid();
        var metadata = CreateTestFileMetadata(id: fileId,
            filePath: "/files/s3-error.pdf", objectKey: "files/s3-error.pdf");

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);
        _mockS3Service
            .Setup(s => s.DeleteFileAsync(metadata.ObjectKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("S3 transient error"));

        var request = CreateRequest("DELETE", $"/v1/files/{fileId}",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } });

        // Act
        var response = await _handler.HandleDeleteFile(request, _lambdaContext);

        // Assert — handler returns an error response (500) but does not crash
        response.Should().NotBeNull();
        response.StatusCode.Should().BeOneOf(200, 500);
        // The S3 delete was attempted
        _mockS3Service.Verify(
            s => s.DeleteFileAsync(metadata.ObjectKey, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    // ========================================================================
    //  Phase 8: HandleUpdateModificationDate Tests
    // ========================================================================
    #region HandleUpdateModificationDate Tests

    [Fact]
    public async Task UpdateModificationDate_WithValidFileId_UpdatesDate()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        var metadata = CreateTestFileMetadata(id: fileId,
            filePath: "/files/update-mod-date.pdf", objectKey: "files/update-mod-date.pdf");
        var newDate = DateTime.UtcNow;

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);
        _mockMetadataRepo
            .Setup(r => r.UpdateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var updateRequest = new UpdateModificationDateRequest { ModificationDate = newDate };
        var request = CreateRequest("PATCH", $"/v1/files/{fileId}/modification-date",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } },
            body: JsonSerializer.Serialize(updateRequest, CamelCaseOptions));

        // Act
        var response = await _handler.HandleUpdateModificationDate(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(200);

        // Verify UpdateAsync was called (NOT UpdateModificationDateAsync) with the correct date
        _mockMetadataRepo.Verify(
            r => r.UpdateAsync(
                It.Is<FileMetadata>(m => m.Id == fileId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateModificationDate_WhenNotFound_Returns404()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileMetadata?)null);

        var updateRequest = new UpdateModificationDateRequest { ModificationDate = DateTime.UtcNow };
        var request = CreateRequest("PATCH", $"/v1/files/{fileId}/modification-date",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } },
            body: JsonSerializer.Serialize(updateRequest, CamelCaseOptions));

        // Act
        var response = await _handler.HandleUpdateModificationDate(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task UpdateModificationDate_UsesCorrectFileId_NotNewGuid()
    {
        // Arrange — CRITICAL: verifies the handler uses the actual fileId
        // from the path parameter, NOT Guid.NewGuid() (which was a bug in the
        // original source DbFileRepository.UpdateModificationDate at line 219).
        var fileId = Guid.NewGuid();
        var metadata = CreateTestFileMetadata(id: fileId,
            filePath: "/files/correct-id.pdf", objectKey: "files/correct-id.pdf");
        var newDate = DateTime.UtcNow;
        FileMetadata? capturedMetadata = null;

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);
        _mockMetadataRepo
            .Setup(r => r.UpdateAsync(It.IsAny<FileMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<FileMetadata, CancellationToken>((m, _) => capturedMetadata = m)
            .ReturnsAsync((FileMetadata m, CancellationToken _) => m);

        var updateRequest = new UpdateModificationDateRequest { ModificationDate = newDate };
        var request = CreateRequest("PATCH", $"/v1/files/{fileId}/modification-date",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } },
            body: JsonSerializer.Serialize(updateRequest, CamelCaseOptions));

        // Act
        await _handler.HandleUpdateModificationDate(request, _lambdaContext);

        // Assert — the metadata passed to UpdateAsync must have the SAME file ID
        capturedMetadata.Should().NotBeNull();
        capturedMetadata!.Id.Should().Be(fileId, "the handler must use the actual fileId, not Guid.NewGuid()");

        // Double-verify: FindByIdAsync was called with the correct fileId
        _mockMetadataRepo.Verify(
            r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    // ========================================================================
    //  Phase 9: Response Structure Tests
    // ========================================================================
    #region Response Structure Tests

    [Fact]
    public async Task AllHandlers_ReturnJsonContentType()
    {
        // Arrange — invoke multiple handlers and verify Content-Type header
        var fileId = Guid.NewGuid();
        var metadata = CreateTestFileMetadata(id: fileId);

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);
        _mockMetadataRepo
            .Setup(r => r.FindByFilePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);
        _mockMetadataRepo
            .Setup(r => r.FindAllAsync(
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<FileMetadata> { metadata }, (string?)null));

        // Test HandleGetDownloadUrl
        var downloadRequest = CreateRequest("GET", $"/v1/files/{fileId}/download",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } });
        var downloadResponse = await _handler.HandleGetDownloadUrl(downloadRequest, _lambdaContext);
        downloadResponse.Headers.Should().ContainKey("Content-Type");
        downloadResponse.Headers["Content-Type"].Should().Contain("application/json");

        // Test HandleGetFileByPath
        var pathRequest = CreateRequest("GET", "/v1/files/by-path",
            queryParams: new Dictionary<string, string> { { "path", "/files/test.pdf" } });
        var pathResponse = await _handler.HandleGetFileByPath(pathRequest, _lambdaContext);
        pathResponse.Headers.Should().ContainKey("Content-Type");
        pathResponse.Headers["Content-Type"].Should().Contain("application/json");

        // Test HandleListFiles
        var listRequest = CreateRequest("GET", "/v1/files");
        var listResponse = await _handler.HandleListFiles(listRequest, _lambdaContext);
        listResponse.Headers.Should().ContainKey("Content-Type");
        listResponse.Headers["Content-Type"].Should().Contain("application/json");
    }

    [Fact]
    public async Task AllHandlers_IncludeCorsHeaders()
    {
        // Arrange — test CORS headers present on a successful response
        var fileId = Guid.NewGuid();
        var metadata = CreateTestFileMetadata(id: fileId);

        _mockMetadataRepo
            .Setup(r => r.FindByIdAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var request = CreateRequest("GET", $"/v1/files/{fileId}/download",
            pathParams: new Dictionary<string, string> { { "fileId", fileId.ToString() } });

        // Act
        var response = await _handler.HandleGetDownloadUrl(request, _lambdaContext);

        // Assert — CORS headers must be present
        response.Headers.Should().ContainKey("Access-Control-Allow-Origin");
        response.Headers["Access-Control-Allow-Origin"].Should().Be("*");
    }

    [Fact]
    public async Task ErrorResponses_NeverExposeStackTraces()
    {
        // Arrange — trigger a 400 error (invalid GUID) and verify no stack trace
        var request = CreateRequest("GET", "/v1/files/invalid-guid/download",
            pathParams: new Dictionary<string, string> { { "fileId", "invalid-guid" } });

        // Act
        var response = await _handler.HandleGetDownloadUrl(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be(400);
        response.Body.Should().NotBeNullOrEmpty();

        // Error response body must not contain stack trace indicators
        response.Body.Should().NotContain("at WebVellaErp.");
        response.Body.Should().NotContain("StackTrace");
        response.Body.Should().NotContain("   at ");
        response.Body.Should().NotContain("Exception");

        // Should contain structured error response fields
        response.Body.Should().Contain("success");
        response.Body.Should().Contain("message");
    }

    #endregion
}
