// ---------------------------------------------------------------------------
// FileMetadataRepositoryTests.cs — Unit Tests for DynamoDB File Metadata Repository
// ---------------------------------------------------------------------------
// Verifies FileMetadataRepository (IFileMetadataRepository) operations:
//   FindByFilePathAsync, FindByIdAsync, FindAllAsync, CreateAsync,
//   UpdateModificationDateAsync, MoveAsync, CopyMetadataAsync, DeleteAsync,
//   CreateTempFileMetadataAsync, and attribute mapping round-trips.
// Uses Moq to mock IAmazonDynamoDB — no real DynamoDB calls.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.FileManagement.DataAccess;
using WebVellaErp.FileManagement.Models;
using Xunit;

namespace WebVellaErp.FileManagement.Tests
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="FileMetadataRepository"/>.
    /// Exercises all IFileMetadataRepository methods with mocked IAmazonDynamoDB,
    /// verifying DynamoDB request construction, filepath normalization, pagination,
    /// temp-file TTL semantics, error messages, and attribute mapping.
    /// </summary>
    public class FileMetadataRepositoryTests
    {
        // ---------------------------------------------------------------------------
        // Test fixtures and mocks
        // ---------------------------------------------------------------------------

        private readonly Mock<IAmazonDynamoDB> _mockDynamoDb;
        private readonly Mock<ILogger<FileMetadataRepository>> _mockLogger;
        private readonly Mock<IConfiguration> _mockConfig;
        private readonly FileMetadataRepository _repository;

        private const string TestTableName = "test-file-metadata";

        /// <summary>
        /// Constructor sets up shared mocks and constructs the repository under test.
        /// Mock config returns table name "test-file-metadata" and leaves TTL at default 24h.
        /// </summary>
        public FileMetadataRepositoryTests()
        {
            _mockDynamoDb = new Mock<IAmazonDynamoDB>(MockBehavior.Loose);
            _mockLogger = new Mock<ILogger<FileMetadataRepository>>();
            _mockConfig = new Mock<IConfiguration>();

            // Configure table name — repository reads from configuration["FILE_MANAGEMENT_TABLE_NAME"]
            _mockConfig.Setup(c => c["FILE_MANAGEMENT_TABLE_NAME"]).Returns(TestTableName);
            // Leave TEMP_FILE_TTL_HOURS null so repository defaults to 24 hours
            _mockConfig.Setup(c => c["TEMP_FILE_TTL_HOURS"]).Returns((string?)null);

            _repository = new FileMetadataRepository(
                _mockDynamoDb.Object,
                _mockLogger.Object,
                _mockConfig.Object);
        }

        // ---------------------------------------------------------------------------
        // Helper: Build a DynamoDB item attribute map for mock responses
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Constructs a valid DynamoDB item dictionary matching the repository's
        /// ToAttributeMap output format. Used to set up mock DynamoDB responses.
        /// </summary>
        private static Dictionary<string, AttributeValue> BuildTestItem(
            Guid id,
            string filepath,
            string? contentType = "application/octet-stream",
            long size = 1024,
            Guid? createdBy = null,
            DateTime? createdOn = null,
            Guid? lastModifiedBy = null,
            DateTime? lastModificationDate = null,
            bool isTemp = false,
            long? ttl = null)
        {
            var now = DateTime.UtcNow;
            var item = new Dictionary<string, AttributeValue>
            {
                { "PK", new AttributeValue { S = $"FILE#{id}" } },
                { "SK", new AttributeValue { S = "META" } },
                { "id", new AttributeValue { S = id.ToString() } },
                { "filepath", new AttributeValue { S = filepath } },
                { "objectKey", new AttributeValue { S = FileMetadata.GenerateObjectKey(id, filepath) } },
                { "contentType", new AttributeValue { S = contentType ?? "application/octet-stream" } },
                { "size", new AttributeValue { N = size.ToString() } },
                { "createdOn", new AttributeValue { S = (createdOn ?? now).ToString("O") } },
                { "lastModificationDate", new AttributeValue { S = (lastModificationDate ?? now).ToString("O") } },
                { "isTemp", new AttributeValue { BOOL = isTemp } }
            };

            item["createdBy"] = createdBy.HasValue
                ? new AttributeValue { S = createdBy.Value.ToString() }
                : new AttributeValue { NULL = true };

            item["lastModifiedBy"] = lastModifiedBy.HasValue
                ? new AttributeValue { S = lastModifiedBy.Value.ToString() }
                : new AttributeValue { NULL = true };

            if (ttl.HasValue)
            {
                item["ttl"] = new AttributeValue { N = ttl.Value.ToString() };
            }

            return item;
        }

        /// <summary>
        /// Creates a Base64-encoded pagination token in the repository's internal format.
        /// Used to test ExclusiveStartKey deserialization.
        /// </summary>
        private static string BuildPaginationToken(Guid fileId)
        {
            var key = new Dictionary<string, Dictionary<string, string>>
            {
                { "PK", new Dictionary<string, string> { { "S", $"FILE#{fileId}" } } },
                { "SK", new Dictionary<string, string> { { "S", "META" } } }
            };
            var json = JsonSerializer.Serialize(key);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        // =========================================================================
        // Phase 2: FindByFilePathAsync Tests
        // =========================================================================

        [Fact]
        public async Task FindByFilePath_WithExistingFile_ReturnsMetadata()
        {
            // Arrange
            var fileId = Guid.NewGuid();
            var filepath = "/documents/report.pdf";
            var item = BuildTestItem(fileId, filepath, "application/pdf", 2048);

            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });

            // Act
            var result = await _repository.FindByFilePathAsync(filepath);

            // Assert
            result.Should().NotBeNull();
            result!.Id.Should().Be(fileId);
            result.FilePath.Should().Be(filepath);
            result.ContentType.Should().Be("application/pdf");
            result.Size.Should().Be(2048);
        }

        [Fact]
        public async Task FindByFilePath_NormalizesToLowercase()
        {
            // Arrange — filepath has mixed casing; should be queried as lowercase
            var fileId = Guid.NewGuid();
            var normalizedPath = "/file/test.txt";
            var item = BuildTestItem(fileId, normalizedPath);

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });

            // Act — pass mixed case filepath
            await _repository.FindByFilePathAsync("/FILE/Test.TXT");

            // Assert — query should use lowercase
            capturedRequest.Should().NotBeNull();
            capturedRequest!.ExpressionAttributeValues[":fp"].S.Should().Be(normalizedPath);
        }

        [Fact]
        public async Task FindByFilePath_PrependsSlash()
        {
            // Arrange — filepath missing leading slash
            var fileId = Guid.NewGuid();
            var normalizedPath = "/file/test.txt";
            var item = BuildTestItem(fileId, normalizedPath);

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });

            // Act — pass path without leading slash
            await _repository.FindByFilePathAsync("file/test.txt");

            // Assert — query should have leading slash
            capturedRequest.Should().NotBeNull();
            capturedRequest!.ExpressionAttributeValues[":fp"].S.Should().Be(normalizedPath);
        }

        [Fact]
        public async Task FindByFilePath_WithNullPath_ThrowsArgumentException()
        {
            // Act & Assert — mirrors source lines 36-37
            Func<Task> act = () => _repository.FindByFilePathAsync(null!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*filepath cannot be null or empty*");
        }

        [Fact]
        public async Task FindByFilePath_WhenNotFound_ReturnsNull()
        {
            // Arrange — empty result set
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            // Act
            var result = await _repository.FindByFilePathAsync("/nonexistent/file.txt");

            // Assert — mirrors source lines 50-55
            result.Should().BeNull();
        }

        [Fact]
        public async Task FindByFilePath_UsesGsiIndex()
        {
            // Arrange
            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            // Act
            await _repository.FindByFilePathAsync("/some/file.txt");

            // Assert — must use GSI-1 "filepath-index"
            capturedRequest.Should().NotBeNull();
            capturedRequest!.IndexName.Should().Be("filepath-index");
        }

        // =========================================================================
        // Phase 3: FindByIdAsync Tests
        // =========================================================================

        [Fact]
        public async Task FindById_WithExistingFile_ReturnsMetadata()
        {
            // Arrange
            var fileId = Guid.NewGuid();
            var filepath = "/docs/manual.pdf";
            var createdBy = Guid.NewGuid();
            var createdOn = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc);
            var item = BuildTestItem(fileId, filepath, "application/pdf", 4096, createdBy, createdOn);

            _mockDynamoDb
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = item });

            // Act
            var result = await _repository.FindByIdAsync(fileId);

            // Assert
            result.Should().NotBeNull();
            result!.Id.Should().Be(fileId);
            result.FilePath.Should().Be(filepath);
            result.ContentType.Should().Be("application/pdf");
            result.Size.Should().Be(4096);
            result.CreatedBy.Should().Be(createdBy);
            result.CreatedOn.Should().Be(createdOn);
        }

        [Fact]
        public async Task FindById_WhenNotFound_ReturnsNull()
        {
            // Arrange — empty item in response
            _mockDynamoDb
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = new Dictionary<string, AttributeValue>() });

            // Act
            var result = await _repository.FindByIdAsync(Guid.NewGuid());

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task FindById_UsesConsistentRead()
        {
            // Arrange
            GetItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<GetItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new GetItemResponse { Item = new Dictionary<string, AttributeValue>() });

            // Act
            await _repository.FindByIdAsync(Guid.NewGuid());

            // Assert — strong consistency for metadata reads
            capturedRequest.Should().NotBeNull();
            capturedRequest!.ConsistentRead.Should().BeTrue();
        }

        // =========================================================================
        // Phase 4: FindAllAsync Tests
        // =========================================================================

        [Fact]
        public async Task FindAll_WithNoFilters_ReturnsAllFiles()
        {
            // Arrange — two files, no path filter, no temp exclusion
            var file1 = BuildTestItem(Guid.NewGuid(), "/docs/a.pdf");
            var file2 = BuildTestItem(Guid.NewGuid(), "/docs/b.pdf");

            ScanRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { file1, file2 }
                });

            // Act — includeTempFiles=true (show all), no prefix
            var result = await _repository.FindAllAsync(
                startsWithPath: null,
                includeTempFiles: true);

            // Assert — returns both, filter only has SK condition
            result.Items.Should().HaveCount(2);
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotContain("begins_with");
            capturedRequest.FilterExpression.Should().NotContain("isTemp");
        }

        [Fact]
        public async Task FindAll_WithPathPrefix_FiltersBeginsWith()
        {
            // Arrange
            ScanRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act
            await _repository.FindAllAsync(
                startsWithPath: "/docs/",
                includeTempFiles: true);

            // Assert — replaces ILIKE pattern from source lines 93-97
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().Contain("begins_with");
            capturedRequest.ExpressionAttributeValues.Should().ContainKey(":prefix");
        }

        [Fact]
        public async Task FindAll_ExcludesTempFiles_ByDefault()
        {
            // Arrange
            ScanRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act — includeTempFiles defaults to false (exclude temp files)
            await _repository.FindAllAsync();

            // Assert — replaces "filepath NOT ILIKE '%/tmp'" from source lines 99-103
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().Contain("isTemp");
            capturedRequest.ExpressionAttributeValues.Should().ContainKey(":notTemp");
            capturedRequest.ExpressionAttributeValues[":notTemp"].BOOL.Should().BeFalse();
        }

        [Fact]
        public async Task FindAll_WithPathAndExcludeTempFiles_CombinesFilters()
        {
            // Arrange
            ScanRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act — both filters active (mirrors source lines 86-91)
            await _repository.FindAllAsync(
                startsWithPath: "/invoices/",
                includeTempFiles: false);

            // Assert — both filters combined with AND
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().Contain("begins_with");
            capturedRequest.FilterExpression.Should().Contain("isTemp");
        }

        [Fact]
        public async Task FindAll_IncludesTempFiles_WhenTrue()
        {
            // Arrange
            ScanRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act — includeTempFiles=true (include temp files, mirrors source lines 93-97)
            await _repository.FindAllAsync(includeTempFiles: true);

            // Assert — no isTemp filter applied
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotContain("isTemp");
        }

        [Fact]
        public async Task FindAll_WithPageSize_SetsLimit()
        {
            // Arrange
            ScanRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act — replaces SQL LIMIT from source lines 72-74
            await _repository.FindAllAsync(pageSize: 25, includeTempFiles: true);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Limit.Should().Be(25);
        }

        [Fact]
        public async Task FindAll_WithExclusiveStartKey_SetsPaginationToken()
        {
            // Arrange — build a valid pagination token
            var startFileId = Guid.NewGuid();
            var paginationToken = BuildPaginationToken(startFileId);

            ScanRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act — replaces SQL OFFSET from source lines 78-79
            await _repository.FindAllAsync(
                exclusiveStartKey: paginationToken,
                includeTempFiles: true);

            // Assert — ExclusiveStartKey should be deserialized from the token
            capturedRequest.Should().NotBeNull();
            capturedRequest!.ExclusiveStartKey.Should().NotBeNull();
            capturedRequest.ExclusiveStartKey.Should().ContainKey("PK");
            capturedRequest.ExclusiveStartKey.Should().ContainKey("SK");
            capturedRequest.ExclusiveStartKey["PK"].S.Should().Be($"FILE#{startFileId}");
        }

        [Fact]
        public async Task FindAll_ReturnsLastEvaluatedKey_ForCursorPagination()
        {
            // Arrange — DynamoDB response has LastEvaluatedKey indicating more pages
            var nextFileId = Guid.NewGuid();
            var lastKey = new Dictionary<string, AttributeValue>
            {
                { "PK", new AttributeValue { S = $"FILE#{nextFileId}" } },
                { "SK", new AttributeValue { S = "META" } }
            };

            var item = BuildTestItem(Guid.NewGuid(), "/docs/file.pdf");
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { item },
                    LastEvaluatedKey = lastKey
                });

            // Act
            var (files, nextPageToken) = await _repository.FindAllAsync(includeTempFiles: true);

            // Assert — serialized key returned for cursor pagination
            files.Should().HaveCount(1);
            nextPageToken.Should().NotBeNullOrWhiteSpace();

            // Verify round-trip: decode the token and check it references the next file
            var decodedJson = Encoding.UTF8.GetString(Convert.FromBase64String(nextPageToken!));
            decodedJson.Should().Contain($"FILE#{nextFileId}");
        }

        // =========================================================================
        // Phase 5: CreateAsync Tests
        // =========================================================================

        [Fact]
        public async Task Create_WithValidMetadata_PersistsToDb()
        {
            // Arrange — no duplicate exists
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            PutItemRequest? capturedPut = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            var fileId = Guid.NewGuid();
            var metadata = new FileMetadata
            {
                Id = fileId,
                FilePath = "/docs/new-file.pdf",
                ObjectKey = FileMetadata.GenerateObjectKey(fileId, "/docs/new-file.pdf"),
                ContentType = "application/pdf",
                Size = 5120,
                CreatedBy = Guid.NewGuid(),
                CreatedOn = DateTime.UtcNow,
                LastModifiedBy = null,
                LastModificationDate = DateTime.UtcNow,
                IsTemp = false
            };

            // Act
            var result = await _repository.CreateAsync(metadata);

            // Assert — PK format is FILE#{id}, SK is META
            capturedPut.Should().NotBeNull();
            capturedPut!.Item.Should().ContainKey("PK");
            capturedPut.Item["PK"].S.Should().Be($"FILE#{fileId}");
            capturedPut.Item.Should().ContainKey("SK");
            capturedPut.Item["SK"].S.Should().Be("META");
            capturedPut.TableName.Should().Be(TestTableName);
            result.Should().NotBeNull();
            result.Id.Should().Be(fileId);
        }

        [Fact]
        public async Task Create_WithNullFilePath_ThrowsArgumentException()
        {
            // Arrange
            var metadata = new FileMetadata
            {
                Id = Guid.NewGuid(),
                FilePath = null!,
                ObjectKey = "test",
                ContentType = "text/plain",
                Size = 100
            };

            // Act & Assert — mirrors source lines 121-122
            Func<Task> act = () => _repository.CreateAsync(metadata);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*filepath cannot be null or empty*");
        }

        [Fact]
        public async Task Create_WhenFileAlreadyExists_ThrowsArgumentException()
        {
            // Arrange — duplicate exists at the same filepath
            var existingItem = BuildTestItem(Guid.NewGuid(), "/docs/existing.pdf");
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { existingItem }
                });

            var metadata = new FileMetadata
            {
                Id = Guid.NewGuid(),
                FilePath = "/docs/existing.pdf",
                ObjectKey = "test",
                ContentType = "text/plain",
                Size = 100
            };

            // Act & Assert — mirrors source line 130
            Func<Task> act = () => _repository.CreateAsync(metadata);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*/docs/existing.pdf: file already exists*");
        }

        [Fact]
        public async Task Create_UsesConditionalExpression()
        {
            // Arrange — no duplicate
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            PutItemRequest? capturedPut = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            var metadata = new FileMetadata
            {
                Id = Guid.NewGuid(),
                FilePath = "/docs/conditional.pdf",
                ObjectKey = "test",
                ContentType = "text/plain",
                Size = 100,
                CreatedOn = DateTime.UtcNow,
                LastModificationDate = DateTime.UtcNow
            };

            // Act
            await _repository.CreateAsync(metadata);

            // Assert — optimistic create with condition to prevent duplicates
            capturedPut.Should().NotBeNull();
            capturedPut!.ConditionExpression.Should().Be("attribute_not_exists(PK)");
        }

        [Fact]
        public async Task Create_NormalizesFilePathOnSave()
        {
            // Arrange
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            PutItemRequest? capturedPut = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            var metadata = new FileMetadata
            {
                Id = Guid.NewGuid(),
                FilePath = "DOCS/MyFile.PDF",  // Mixed case, no leading slash
                ObjectKey = "test",
                ContentType = "text/plain",
                Size = 100,
                CreatedOn = DateTime.UtcNow,
                LastModificationDate = DateTime.UtcNow
            };

            // Act
            await _repository.CreateAsync(metadata);

            // Assert — saved filepath must be lowercase with leading /
            capturedPut.Should().NotBeNull();
            capturedPut!.Item["filepath"].S.Should().Be("/docs/myfile.pdf");
        }

        // =========================================================================
        // Phase 6: UpdateModificationDateAsync Tests
        // =========================================================================

        [Fact]
        public async Task UpdateModificationDate_WithValidFile_UpdatesDate()
        {
            // Arrange — file exists
            var fileId = Guid.NewGuid();
            var filepath = "/docs/report.pdf";
            var item = BuildTestItem(fileId, filepath);
            var modDate = new DateTime(2025, 1, 15, 14, 0, 0, DateTimeKind.Utc);

            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { item }
                });

            UpdateItemRequest? capturedUpdate = null;
            _mockDynamoDb
                .Setup(d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<UpdateItemRequest, CancellationToken>((req, _) => capturedUpdate = req)
                .ReturnsAsync(new UpdateItemResponse
                {
                    Attributes = BuildTestItem(fileId, filepath)
                });

            // Act
            await _repository.UpdateModificationDateAsync(filepath, modDate);

            // Assert — sets lastModificationDate in UpdateExpression
            capturedUpdate.Should().NotBeNull();
            capturedUpdate!.UpdateExpression.Should().Contain("lastModificationDate");
            capturedUpdate.ExpressionAttributeValues.Should().ContainKey(":modDate");
        }

        [Fact]
        public async Task UpdateModificationDate_WhenNotFound_ThrowsArgumentException()
        {
            // Arrange — file does not exist
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            // Act & Assert — mirrors source line 216
            Func<Task> act = () => _repository.UpdateModificationDateAsync(
                "/docs/nonexistent.pdf",
                DateTime.UtcNow);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*file does not exist*");
        }

        [Fact]
        public async Task UpdateModificationDate_UsesCorrectId()
        {
            // Arrange — file exists with a specific known ID
            // CRITICAL: Source had bug on line 219 using Guid.NewGuid() — verify correct behavior
            var knownFileId = Guid.NewGuid();
            var filepath = "/docs/known-file.pdf";
            var item = BuildTestItem(knownFileId, filepath);

            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { item }
                });

            UpdateItemRequest? capturedUpdate = null;
            _mockDynamoDb
                .Setup(d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<UpdateItemRequest, CancellationToken>((req, _) => capturedUpdate = req)
                .ReturnsAsync(new UpdateItemResponse
                {
                    Attributes = BuildTestItem(knownFileId, filepath)
                });

            // Act
            await _repository.UpdateModificationDateAsync(filepath, DateTime.UtcNow);

            // Assert — UpdateItem key must use the found file's actual ID, NOT a new Guid
            capturedUpdate.Should().NotBeNull();
            capturedUpdate!.Key.Should().ContainKey("PK");
            capturedUpdate.Key["PK"].S.Should().Be($"FILE#{knownFileId}");
        }

        // =========================================================================
        // Phase 7: MoveAsync Tests
        // =========================================================================

        [Fact]
        public async Task Move_WithValidSource_UpdatesFilePath()
        {
            // Arrange — source exists, dest does not
            var fileId = Guid.NewGuid();
            var sourceItem = BuildTestItem(fileId, "/old/path.txt");

            // First QueryAsync call (source lookup) returns item
            // Second QueryAsync call (dest lookup) returns empty
            _mockDynamoDb
                .SetupSequence(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { sourceItem }
                })
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            UpdateItemRequest? capturedUpdate = null;
            _mockDynamoDb
                .Setup(d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<UpdateItemRequest, CancellationToken>((req, _) => capturedUpdate = req)
                .ReturnsAsync(new UpdateItemResponse
                {
                    Attributes = BuildTestItem(fileId, "/new/path.txt")
                });

            // Act
            await _repository.MoveAsync("/old/path.txt", "/new/path.txt");

            // Assert — UpdateItem changes filepath attribute to destination
            capturedUpdate.Should().NotBeNull();
            capturedUpdate!.ExpressionAttributeValues.Should().ContainKey(":newPath");
            capturedUpdate.ExpressionAttributeValues[":newPath"].S.Should().Be("/new/path.txt");
        }

        [Fact]
        public async Task Move_WhenSourceNull_ThrowsException()
        {
            // Arrange — source does not exist
            _mockDynamoDb
                .SetupSequence(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act & Assert — mirrors source line 310
            Func<Task> act = () => _repository.MoveAsync("/nonexistent/file.txt", "/new/path.txt");
            await act.Should().ThrowAsync<Exception>()
                .WithMessage("Source file cannot be found.");
        }

        [Fact]
        public async Task Move_WhenDestExistsNoOverwrite_ThrowsException()
        {
            // Arrange — source exists, dest exists, overwrite=false
            var sourceItem = BuildTestItem(Guid.NewGuid(), "/old/file.txt");
            var destItem = BuildTestItem(Guid.NewGuid(), "/new/file.txt");

            _mockDynamoDb
                .SetupSequence(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { sourceItem }
                })
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { destItem }
                });

            // Act & Assert — mirrors source line 313
            Func<Task> act = () => _repository.MoveAsync("/old/file.txt", "/new/file.txt", overwrite: false);
            await act.Should().ThrowAsync<Exception>()
                .WithMessage("Destination file already exists and no overwrite specified.");
        }

        [Fact]
        public async Task Move_WithOverwrite_DeletesExisting()
        {
            // Arrange — source exists, dest exists, overwrite=true
            var sourceFileId = Guid.NewGuid();
            var destFileId = Guid.NewGuid();
            var sourceItem = BuildTestItem(sourceFileId, "/old/file.txt");
            var destItem = BuildTestItem(destFileId, "/new/file.txt");

            _mockDynamoDb
                .SetupSequence(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { sourceItem }
                })
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { destItem }
                });

            _mockDynamoDb
                .Setup(d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteItemResponse());

            _mockDynamoDb
                .Setup(d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateItemResponse
                {
                    Attributes = BuildTestItem(sourceFileId, "/new/file.txt")
                });

            // Act — mirrors source lines 322-323
            await _repository.MoveAsync("/old/file.txt", "/new/file.txt", overwrite: true);

            // Assert — existing dest deleted before update
            _mockDynamoDb.Verify(
                d => d.DeleteItemAsync(
                    It.Is<DeleteItemRequest>(r => r.Key["PK"].S == $"FILE#{destFileId}"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task Move_ValidatesSourcePathNotEmpty()
        {
            // Act & Assert — mirrors source lines 292-293
            Func<Task> act = () => _repository.MoveAsync("", "/dest/file.txt");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*sourceFilepath cannot be null or empty*");
        }

        [Fact]
        public async Task Move_ValidatesDestPathNotEmpty()
        {
            // Act & Assert — mirrors source lines 295-296
            Func<Task> act = () => _repository.MoveAsync("/source/file.txt", "");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*destinationFilepath cannot be null or empty*");
        }

        // =========================================================================
        // Phase 8: CopyMetadataAsync Tests
        // =========================================================================

        [Fact]
        public async Task CopyMetadata_CreatesNewRecordWithNewId()
        {
            // Arrange — source exists, dest does not
            var sourceId = Guid.NewGuid();
            var sourceItem = BuildTestItem(sourceId, "/original/file.txt", size: 2048);

            // Query 1: source lookup → found
            // Query 2: dest lookup → not found
            // Query 3: CreateAsync's internal duplicate check → not found
            _mockDynamoDb
                .SetupSequence(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { sourceItem }
                })
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                })
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            PutItemRequest? capturedPut = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            var result = await _repository.CopyMetadataAsync("/original/file.txt", "/copy/file.txt");

            // Assert — new record has different ID from source
            result.Should().NotBeNull();
            result!.Id.Should().NotBe(sourceId);
            capturedPut.Should().NotBeNull();
            capturedPut!.Item["id"].S.Should().NotBe(sourceId.ToString());
        }

        [Fact]
        public async Task CopyMetadata_PreservesCreatedOnFromSource()
        {
            // Arrange — source has a specific CreatedOn timestamp
            var sourceId = Guid.NewGuid();
            var sourceCreatedOn = new DateTime(2023, 3, 15, 8, 0, 0, DateTimeKind.Utc);
            var sourceItem = BuildTestItem(sourceId, "/original/file.txt", createdOn: sourceCreatedOn);

            _mockDynamoDb
                .SetupSequence(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { sourceItem }
                })
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                })
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            PutItemRequest? capturedPut = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            var result = await _repository.CopyMetadataAsync("/original/file.txt", "/copy/file.txt");

            // Assert — preserves source's CreatedOn (mirrors source line 270)
            result.Should().NotBeNull();
            result!.CreatedOn.Should().Be(sourceCreatedOn);
            capturedPut.Should().NotBeNull();
            var savedCreatedOn = DateTime.Parse(capturedPut!.Item["createdOn"].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            savedCreatedOn.Kind.Should().Be(DateTimeKind.Utc, "stored timestamp must be UTC");
            savedCreatedOn.Should().Be(sourceCreatedOn);
        }

        [Fact]
        public async Task CopyMetadata_PreservesCreatedByFromSource()
        {
            // Arrange — source has a specific CreatedBy user
            var sourceId = Guid.NewGuid();
            var sourceCreatedBy = Guid.NewGuid();
            var sourceItem = BuildTestItem(sourceId, "/original/file.txt", createdBy: sourceCreatedBy);

            _mockDynamoDb
                .SetupSequence(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { sourceItem }
                })
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                })
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            PutItemRequest? capturedPut = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            var result = await _repository.CopyMetadataAsync("/original/file.txt", "/copy/file.txt");

            // Assert — preserves source's CreatedBy (mirrors source line 270)
            result.Should().NotBeNull();
            result!.CreatedBy.Should().Be(sourceCreatedBy);
            capturedPut.Should().NotBeNull();
            capturedPut!.Item["createdBy"].S.Should().Be(sourceCreatedBy.ToString());
        }

        [Fact]
        public async Task CopyMetadata_WhenSourceNotFound_Throws()
        {
            // Arrange — source does not exist
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act & Assert — mirrors source line 255
            Func<Task> act = () => _repository.CopyMetadataAsync("/nonexistent/file.txt", "/copy/file.txt");
            await act.Should().ThrowAsync<Exception>()
                .WithMessage("Source file cannot be found.");
        }

        [Fact]
        public async Task CopyMetadata_WhenDestExistsNoOverwrite_Throws()
        {
            // Arrange — source exists, dest exists, overwrite=false
            var sourceItem = BuildTestItem(Guid.NewGuid(), "/original/file.txt");
            var destItem = BuildTestItem(Guid.NewGuid(), "/copy/file.txt");

            _mockDynamoDb
                .SetupSequence(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { sourceItem }
                })
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { destItem }
                });

            // Act & Assert — mirrors source line 258
            Func<Task> act = () => _repository.CopyMetadataAsync("/original/file.txt", "/copy/file.txt");
            await act.Should().ThrowAsync<Exception>()
                .WithMessage("Destination file already exists and no overwrite specified.");
        }

        // =========================================================================
        // Phase 9: DeleteAsync Tests
        // =========================================================================

        [Fact]
        public async Task Delete_WithExistingFile_DeletesFromDb()
        {
            // Arrange — file exists
            var fileId = Guid.NewGuid();
            var item = BuildTestItem(fileId, "/docs/to-delete.pdf");

            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { item }
                });

            _mockDynamoDb
                .Setup(d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteItemResponse());

            // Act
            await _repository.DeleteAsync("/docs/to-delete.pdf");

            // Assert — DeleteItemAsync called with correct key
            _mockDynamoDb.Verify(
                d => d.DeleteItemAsync(
                    It.Is<DeleteItemRequest>(r => r.Key["PK"].S == $"FILE#{fileId}"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task Delete_WhenNotFound_ReturnsSilently()
        {
            // Arrange — file does not exist
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            // Act — mirrors source lines 387-388: if (file == null) return;
            await _repository.DeleteAsync("/nonexistent/file.txt");

            // Assert — no DeleteItem called, no exception
            _mockDynamoDb.Verify(
                d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        [Fact]
        public async Task Delete_NormalizesFilePath()
        {
            // Arrange
            var fileId = Guid.NewGuid();
            var item = BuildTestItem(fileId, "/docs/myfile.txt");

            QueryRequest? capturedQuery = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedQuery = req)
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { item }
                });

            _mockDynamoDb
                .Setup(d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteItemResponse());

            // Act — pass non-normalized path
            await _repository.DeleteAsync("DOCS/MyFile.TXT");

            // Assert — query used normalized lowercase path with leading /
            capturedQuery.Should().NotBeNull();
            capturedQuery!.ExpressionAttributeValues[":fp"].S.Should().Be("/docs/myfile.txt");
        }

        // =========================================================================
        // Phase 10: CreateTempFileMetadataAsync Tests
        // =========================================================================

        [Fact]
        public async Task CreateTempMetadata_SetsIsTempTrue()
        {
            // Arrange
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            PutItemRequest? capturedPut = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            var result = await _repository.CreateTempFileMetadataAsync("report", ".pdf", "application/pdf", 1024);

            // Assert
            result.Should().NotBeNull();
            result.IsTemp.Should().BeTrue();
            capturedPut.Should().NotBeNull();
            capturedPut!.Item["isTemp"].BOOL.Should().BeTrue();
        }

        [Fact]
        public async Task CreateTempMetadata_SetsTtl()
        {
            // Arrange
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            PutItemRequest? capturedPut = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            var beforeCreate = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();

            // Act
            var result = await _repository.CreateTempFileMetadataAsync("temp", ".dat", "application/octet-stream", 512);

            var afterCreate = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();

            // Assert — TTL set to future Unix timestamp (DynamoDB TTL auto-deletion)
            result.Ttl.Should().NotBeNull();
            result.Ttl!.Value.Should().BeGreaterThanOrEqualTo(beforeCreate);
            result.Ttl!.Value.Should().BeLessThanOrEqualTo(afterCreate);
            capturedPut.Should().NotBeNull();
            capturedPut!.Item.Should().ContainKey("ttl");
        }

        [Fact]
        public async Task CreateTempMetadata_NormalizesExtension()
        {
            // Arrange
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            PutItemRequest? capturedPut = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            // Act — pass uppercase extension without leading dot (mirrors source lines 439-444)
            var result = await _repository.CreateTempFileMetadataAsync("test", "TXT", "text/plain", 256);

            // Assert — extension normalized to lowercase with leading dot
            result.Should().NotBeNull();
            result.FilePath.Should().EndWith(".txt");
            capturedPut.Should().NotBeNull();
            capturedPut!.Item["filepath"].S.Should().EndWith(".txt");
        }

        [Fact]
        public async Task CreateTempMetadata_BuildsCorrectTmpFilePath()
        {
            // Arrange
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutItemResponse());

            // Act — mirrors source line 447: /tmp/{section}/{filename}{ext}
            var result = await _repository.CreateTempFileMetadataAsync("myfile", ".pdf", "application/pdf", 1024);

            // Assert — path matches /tmp/{section}/{filename}{ext} pattern
            result.Should().NotBeNull();
            result.FilePath.Should().StartWith(
                $"{FileMetadataRepository.FOLDER_SEPARATOR}{FileMetadataRepository.TMP_FOLDER_NAME}{FileMetadataRepository.FOLDER_SEPARATOR}");
            result.FilePath.Should().Contain("myfile");
            result.FilePath.Should().EndWith(".pdf");

            // Path structure: /tmp/<32-char-hex>/myfile.pdf
            var parts = result.FilePath.Split('/');
            parts.Should().HaveCount(4); // ["", "tmp", section, "myfile.pdf"]
            parts[1].Should().Be(FileMetadata.TmpFolderName);
            parts[3].Should().Be("myfile.pdf");
        }

        [Fact]
        public async Task CreateTempMetadata_SectionGuidHasNoDashes()
        {
            // Arrange
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutItemResponse());

            // Act — mirrors source line 446: Guid.NewGuid().ToString().Replace("-","")
            var result = await _repository.CreateTempFileMetadataAsync("file", ".txt", "text/plain", 100);

            // Assert — section in path has no hyphens
            var parts = result.FilePath.Split('/');
            var section = parts[2]; // /tmp/{section}/file.txt
            section.Should().NotContain("-");
            section.Should().HaveLength(32); // 32 hex chars (GUID without dashes)
        }

        [Fact]
        public async Task CreateTempMetadata_SetsCreatedByNull()
        {
            // Arrange
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            PutItemRequest? capturedPut = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            // Act — mirrors source line 448: null for createdBy
            var result = await _repository.CreateTempFileMetadataAsync("temp", ".dat", "application/octet-stream", 512);

            // Assert
            result.CreatedBy.Should().BeNull();
            capturedPut.Should().NotBeNull();
            capturedPut!.Item["createdBy"].NULL.Should().BeTrue();
        }

        [Fact]
        public async Task CreateTempMetadata_SetsCreatedOnToUtcNow()
        {
            // Arrange
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutItemResponse());

            var before = DateTime.UtcNow;

            // Act — mirrors source line 448: DateTime.UtcNow
            var result = await _repository.CreateTempFileMetadataAsync("temp", ".log", "text/plain", 128);

            var after = DateTime.UtcNow;

            // Assert — CreatedOn ≈ DateTime.UtcNow
            result.CreatedOn.Should().BeOnOrAfter(before);
            result.CreatedOn.Should().BeOnOrBefore(after);
        }

        // =========================================================================
        // Phase 11: Attribute Mapping Tests (via round-trip through public API)
        // =========================================================================

        [Fact]
        public async Task ToAttributeMap_MapsAllProperties()
        {
            // Arrange — create metadata with all properties populated
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            PutItemRequest? capturedPut = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            var fileId = Guid.NewGuid();
            var createdBy = Guid.NewGuid();
            var lastModifiedBy = Guid.NewGuid();
            long testTtl = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();

            var metadata = new FileMetadata
            {
                Id = fileId,
                FilePath = "/test/all-props.txt",
                ObjectKey = FileMetadata.GenerateObjectKey(fileId, "/test/all-props.txt"),
                ContentType = "text/plain",
                Size = 9999,
                CreatedBy = createdBy,
                CreatedOn = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                LastModifiedBy = lastModifiedBy,
                LastModificationDate = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                IsTemp = true,
                Ttl = testTtl
            };

            // Act — CreateAsync triggers ToAttributeMap internally
            await _repository.CreateAsync(metadata);

            // Assert — verify all 12+ attributes present in the DynamoDB item
            capturedPut.Should().NotBeNull();
            var item = capturedPut!.Item;

            item.Should().ContainKey("PK");
            item["PK"].S.Should().Be($"FILE#{fileId}");
            item.Should().ContainKey("SK");
            item["SK"].S.Should().Be("META");
            item.Should().ContainKey("id");
            item["id"].S.Should().Be(fileId.ToString());
            item.Should().ContainKey("filepath");
            item.Should().ContainKey("objectKey");
            item.Should().ContainKey("contentType");
            item["contentType"].S.Should().Be("text/plain");
            item.Should().ContainKey("size");
            item["size"].N.Should().Be("9999");
            item.Should().ContainKey("createdBy");
            item["createdBy"].S.Should().Be(createdBy.ToString());
            item.Should().ContainKey("createdOn");
            item.Should().ContainKey("lastModifiedBy");
            item["lastModifiedBy"].S.Should().Be(lastModifiedBy.ToString());
            item.Should().ContainKey("lastModificationDate");
            item.Should().ContainKey("isTemp");
            item["isTemp"].BOOL.Should().BeTrue();
            item.Should().ContainKey("ttl");
            item["ttl"].N.Should().Be(testTtl.ToString());
        }

        [Fact]
        public async Task FromAttributeMap_DeserializesCorrectly()
        {
            // Arrange — construct a full DynamoDB item and verify round-trip
            var fileId = Guid.NewGuid();
            var createdBy = Guid.NewGuid();
            var lastModifiedBy = Guid.NewGuid();
            var createdOn = new DateTime(2024, 3, 20, 9, 15, 0, DateTimeKind.Utc);
            var lastModDate = new DateTime(2024, 7, 10, 16, 45, 0, DateTimeKind.Utc);
            long ttlValue = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
            var filepath = "/roundtrip/test.bin";

            var item = BuildTestItem(
                fileId,
                filepath,
                contentType: "application/octet-stream",
                size: 65536,
                createdBy: createdBy,
                createdOn: createdOn,
                lastModifiedBy: lastModifiedBy,
                lastModificationDate: lastModDate,
                isTemp: false,
                ttl: ttlValue);

            _mockDynamoDb
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = item });

            // Act — FindByIdAsync triggers FromAttributeMap internally
            var result = await _repository.FindByIdAsync(fileId);

            // Assert — all properties correctly deserialized
            result.Should().NotBeNull();
            result!.Id.Should().Be(fileId);
            result.FilePath.Should().Be(filepath);
            result.ContentType.Should().Be("application/octet-stream");
            result.Size.Should().Be(65536);
            result.CreatedBy.Should().Be(createdBy);
            result.CreatedOn.Should().Be(createdOn);
            result.LastModifiedBy.Should().Be(lastModifiedBy);
            result.LastModificationDate.Should().Be(lastModDate);
            result.IsTemp.Should().BeFalse();
            result.Ttl.Should().Be(ttlValue);
        }

        [Fact]
        public async Task FromAttributeMap_HandlesNullableGuid()
        {
            // Arrange — item with NULL CreatedBy and LastModifiedBy
            var fileId = Guid.NewGuid();
            var filepath = "/nullable/test.txt";
            var item = BuildTestItem(
                fileId,
                filepath,
                createdBy: null,
                lastModifiedBy: null);

            // Verify the test item has explicit NULL attributes
            item["createdBy"].NULL.Should().BeTrue();
            item["lastModifiedBy"].NULL.Should().BeTrue();

            _mockDynamoDb
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = item });

            // Act — FromAttributeMap handles DynamoDB NULL type
            var result = await _repository.FindByIdAsync(fileId);

            // Assert — nullable Guid properties are null, not default(Guid)
            result.Should().NotBeNull();
            result!.CreatedBy.Should().BeNull();
            result.LastModifiedBy.Should().BeNull();
        }
    }
}
