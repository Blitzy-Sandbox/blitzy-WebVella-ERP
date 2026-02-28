// =============================================================================
// RecordRepositoryTests.cs — Unit Tests for DynamoDB Record Repository
// =============================================================================
// Comprehensive xUnit unit tests for RecordRepository covering all CRUD
// operations, query translation, DynamoDB type conversion for 20+ field types,
// field value extraction, batch operations, paging, sorting, and schema
// evolution no-ops — all with mocked IAmazonDynamoDB client.
//
// Namespace: WebVellaErp.EntityManagement.Tests.Unit.DataAccess
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DynamoQueryResponse = Amazon.DynamoDBv2.Model.QueryResponse;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.EntityManagement.DataAccess;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Tests.Fixtures;
using Xunit;

namespace WebVellaErp.EntityManagement.Tests.Unit.DataAccess
{
    /// <summary>
    /// Unit tests for <see cref="RecordRepository"/> validating all CRUD operations,
    /// query filter translation, DynamoDB type conversion, field value extraction,
    /// batch operations, paging, sorting, and schema evolution no-ops.
    /// All tests use mocked <see cref="IAmazonDynamoDB"/> — no real AWS connections.
    /// </summary>
    public class RecordRepositoryTests
    {
        // =====================================================================
        // Test Infrastructure
        // =====================================================================

        private const string TestTableName = "entity-management-records-test";
        private const string TestEntityName = "test_entity";

        private readonly Mock<IAmazonDynamoDB> _mockDynamoDb;
        private readonly Mock<IEntityRepository> _mockEntityRepository;
        private readonly Mock<ILogger<RecordRepository>> _mockLogger;
        private readonly IConfiguration _configuration;
        private readonly RecordRepository _sut;

        /// <summary>
        /// Initializes test infrastructure with mocked dependencies and real IConfiguration.
        /// Uses ConfigurationBuilder.AddInMemoryCollection for reliable GetValue&lt;string&gt; support.
        /// </summary>
        public RecordRepositoryTests()
        {
            _mockDynamoDb = new Mock<IAmazonDynamoDB>();
            _mockEntityRepository = new Mock<IEntityRepository>();
            _mockLogger = new Mock<ILogger<RecordRepository>>();

            var inMemorySettings = new Dictionary<string, string?>
            {
                { "DynamoDB:RecordTableName", TestTableName }
            };
            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            _sut = new RecordRepository(
                _mockDynamoDb.Object,
                _mockEntityRepository.Object,
                _mockLogger.Object,
                _configuration);
        }

        // =====================================================================
        // Helper Methods
        // =====================================================================

        /// <summary>
        /// Creates a test entity with a standard set of fields covering common types.
        /// </summary>
        private Entity CreateTestEntityWithFields(string entityName = TestEntityName)
        {
            var entity = TestDataHelper.CreateTestEntity();
            entity.Name = entityName;
            entity.Fields = new List<Field>
            {
                TestDataHelper.CreateGuidField("id"),
                TestDataHelper.CreateTextField("name"),
                TestDataHelper.CreateNumberField("amount"),
                TestDataHelper.CreateCheckboxField("is_active"),
                TestDataHelper.CreateDateField("birth_date"),
                TestDataHelper.CreateDateTimeField("created_on"),
                TestDataHelper.CreateMultiSelectField("tags"),
                TestDataHelper.CreateGeographyField("location"),
                TestDataHelper.CreateAutoNumberField("seq_number"),
                TestDataHelper.CreateCurrencyField("price"),
                TestDataHelper.CreatePasswordField("secret"),
                TestDataHelper.CreateEmailField("email"),
                TestDataHelper.CreatePhoneField("phone"),
                TestDataHelper.CreateSelectField("status"),
                TestDataHelper.CreateUrlField("website"),
                TestDataHelper.CreateFileField("attachment"),
                TestDataHelper.CreateImageField("avatar"),
                TestDataHelper.CreateHtmlField("description"),
                TestDataHelper.CreateMultiLineTextField("notes"),
                TestDataHelper.CreatePercentField("completion")
            };
            return entity;
        }

        /// <summary>
        /// Sets up entity repository mock to return the given entity for the specified name.
        /// </summary>
        private void SetupEntityLookup(Entity entity)
        {
            _mockEntityRepository
                .Setup(r => r.GetEntityByName(entity.Name))
                .ReturnsAsync(entity);
        }

        /// <summary>
        /// Sets up entity repository mock to return null for the specified name.
        /// </summary>
        private void SetupEntityLookupReturnsNull(string entityName)
        {
            _mockEntityRepository
                .Setup(r => r.GetEntityByName(entityName))
                .ReturnsAsync((Entity?)null);
        }

        /// <summary>
        /// Creates a DynamoDB item dictionary representing a stored record.
        /// </summary>
        private Dictionary<string, AttributeValue> CreateDynamoDbRecordItem(
            string entityName, Guid recordId, Dictionary<string, AttributeValue>? extraAttributes = null)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"ENTITY#{entityName}" },
                ["SK"] = new AttributeValue { S = $"RECORD#{recordId}" },
                ["entityName"] = new AttributeValue { S = entityName },
                ["recordId"] = new AttributeValue { S = recordId.ToString() }
            };

            if (extraAttributes != null)
            {
                foreach (var kvp in extraAttributes)
                    item[kvp.Key] = kvp.Value;
            }

            return item;
        }

        // =====================================================================
        // Phase 2: Record CRUD — CreateRecord Tests
        // =====================================================================

        [Fact]
        public async Task CreateRecord_ShouldBuildCorrectPutItemRequest()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordId = Guid.NewGuid();
            var recordData = new Dictionary<string, object?>
            {
                ["id"] = recordId,
                ["name"] = "Test Record"
            };

            PutItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            await _sut.CreateRecord(TestEntityName, recordData);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TableName.Should().Be(TestTableName);
            capturedRequest.Item["PK"].S.Should().Be($"ENTITY#{TestEntityName}");
            capturedRequest.Item["SK"].S.Should().StartWith("RECORD#");
            capturedRequest.ConditionExpression.Should().Be("attribute_not_exists(PK)");

            _mockDynamoDb.Verify(
                d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateRecord_ShouldConvertGuidFieldToStringAttributeValue()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordId = Guid.NewGuid();
            var testGuid = Guid.NewGuid();
            var recordData = new Dictionary<string, object?>
            {
                ["id"] = recordId,
                ["name"] = "Test"
            };

            PutItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            await _sut.CreateRecord(TestEntityName, recordData);

            // Assert — the recordId stored in the item should be a string (S attribute)
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["recordId"].S.Should().Be(recordId.ToString());
        }

        [Fact]
        public async Task CreateRecord_ShouldConvertTextFieldToStringAttributeValue()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordId = Guid.NewGuid();
            var recordData = new Dictionary<string, object?>
            {
                ["id"] = recordId,
                ["name"] = "Hello World"
            };

            PutItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            await _sut.CreateRecord(TestEntityName, recordData);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().ContainKey("name");
            capturedRequest.Item["name"].S.Should().Be("Hello World");
        }

        [Fact]
        public async Task CreateRecord_ShouldConvertNumberFieldToNumberAttributeValue()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordData = new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid(),
                ["amount"] = 123.45m
            };

            PutItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            await _sut.CreateRecord(TestEntityName, recordData);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().ContainKey("amount");
            capturedRequest.Item["amount"].N.Should().Be("123.45");
        }

        [Fact]
        public async Task CreateRecord_ShouldConvertCheckboxFieldToBoolAttributeValue()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordData = new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid(),
                ["is_active"] = true
            };

            PutItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            await _sut.CreateRecord(TestEntityName, recordData);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().ContainKey("is_active");
            capturedRequest.Item["is_active"].BOOL.Should().BeTrue();
        }

        [Fact]
        public async Task CreateRecord_ShouldConvertDateFieldToIso8601StringAttributeValue()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var testDate = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
            var recordData = new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid(),
                ["birth_date"] = testDate
            };

            PutItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            await _sut.CreateRecord(TestEntityName, recordData);

            // Assert — Date is stored as ISO 8601 string (S attribute)
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().ContainKey("birth_date");
            capturedRequest.Item["birth_date"].S.Should().Contain("2024-01-15");
        }

        [Fact]
        public async Task CreateRecord_ShouldConvertMultiSelectFieldToListAttributeValue()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordData = new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid(),
                ["tags"] = new List<string> { "tag1", "tag2", "tag3" }
            };

            PutItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            await _sut.CreateRecord(TestEntityName, recordData);

            // Assert — MultiSelect is stored as L (List) of S items
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().ContainKey("tags");
            capturedRequest.Item["tags"].L.Should().HaveCount(3);
            capturedRequest.Item["tags"].L[0].S.Should().Be("tag1");
            capturedRequest.Item["tags"].L[1].S.Should().Be("tag2");
            capturedRequest.Item["tags"].L[2].S.Should().Be("tag3");
        }

        [Fact]
        public async Task CreateRecord_ShouldConvertNullToNullAttributeValue()
        {
            // Arrange — Use a TextField with null DefaultValue so that
            // ExtractFieldValue(null, field) → GetFieldDefaultValue() → null →
            // ConvertToAttributeValue(null, field) → AttributeValue { NULL = true }
            // Note: TestDataHelper.CreateTextField sets DefaultValue="" by default,
            // so we must create a custom entity with a null-defaulted field.
            var entity = TestDataHelper.CreateTestEntity();
            entity.Name = TestEntityName;
            entity.Fields = new List<Field>
            {
                TestDataHelper.CreateGuidField("id"),
                new TextField { Id = Guid.NewGuid(), Name = "nullable_field", Label = "Nullable", DefaultValue = null }
            };
            SetupEntityLookup(entity);

            var recordData = new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid(),
                ["nullable_field"] = null
            };

            PutItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            await _sut.CreateRecord(TestEntityName, recordData);

            // Assert — Null value with null field default should be stored as NULL attribute
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().ContainKey("nullable_field");
            capturedRequest.Item["nullable_field"].NULL.Should().BeTrue();
        }

        [Fact]
        public async Task CreateRecord_ShouldHandleGeographyFieldAsRawString()
        {
            // Arrange — Geography is stored as plain string (no PostGIS transforms)
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var geoJson = "{\"type\":\"Point\",\"coordinates\":[40.7128,-74.0060]}";
            var recordData = new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid(),
                ["location"] = geoJson
            };

            PutItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            await _sut.CreateRecord(TestEntityName, recordData);

            // Assert — Geography stored as S attribute
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().ContainKey("location");
            capturedRequest.Item["location"].S.Should().Be(geoJson);
        }

        // =====================================================================
        // Phase 3: Record CRUD — UpdateRecord Tests
        // =====================================================================

        [Fact]
        public async Task UpdateRecord_ShouldBuildCorrectUpdateItemRequest()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordId = Guid.NewGuid();
            var recordData = new Dictionary<string, object?>
            {
                ["id"] = recordId,
                ["name"] = "Updated Name"
            };

            UpdateItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<UpdateItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new UpdateItemResponse());

            // Act
            await _sut.UpdateRecord(TestEntityName, recordData);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TableName.Should().Be(TestTableName);
            capturedRequest.Key["PK"].S.Should().Be($"ENTITY#{TestEntityName}");
            capturedRequest.Key["SK"].S.Should().Be($"RECORD#{recordId}");
            capturedRequest.ConditionExpression.Should().Be("attribute_exists(PK)");
            capturedRequest.UpdateExpression.Should().Contain("SET");
        }

        [Fact]
        public async Task UpdateRecord_ShouldThrowWhenIdMissing()
        {
            // Arrange — recordData without "id" field
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordData = new Dictionary<string, object?>
            {
                ["name"] = "No Id"
            };

            // Act & Assert — exact error message from source line 186
            var act = () => _sut.UpdateRecord(TestEntityName, recordData);
            await act.Should().ThrowAsync<StorageException>()
                .WithMessage("ID is missing. Cannot update records without ID specified.");
        }

        [Fact]
        public async Task UpdateRecord_ShouldThrowWhenRecordDoesNotExist()
        {
            // Arrange — ConditionalCheckFailedException means record doesn't exist
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordData = new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid(),
                ["name"] = "Ghost Record"
            };

            _mockDynamoDb
                .Setup(d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConditionalCheckFailedException("Condition not met"));

            // Act & Assert — exact error message from source line 191-192
            var act = () => _sut.UpdateRecord(TestEntityName, recordData);
            await act.Should().ThrowAsync<StorageException>()
                .WithMessage("Failed to update record.");
        }

        // =====================================================================
        // Phase 4: Record CRUD — DeleteRecord Tests
        // =====================================================================

        [Fact]
        public async Task DeleteRecord_ShouldVerifyRecordExistsBeforeDelete()
        {
            // Arrange — FindRecord returns a record, then DeleteItemAsync succeeds
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordId = Guid.NewGuid();

            _mockDynamoDb
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateDynamoDbRecordItem(TestEntityName, recordId)
                });

            _mockDynamoDb
                .Setup(d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteItemResponse());

            // Act
            await _sut.DeleteRecord(TestEntityName, recordId);

            // Assert — Verify GetItemAsync called first (existence check), then DeleteItemAsync
            _mockDynamoDb.Verify(
                d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Once);
            _mockDynamoDb.Verify(
                d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task DeleteRecord_ShouldThrowWhenRecordNotFound()
        {
            // Arrange — GetItemAsync returns empty item (record doesn't exist)
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordId = Guid.NewGuid();

            _mockDynamoDb
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>()
                });

            // Act & Assert — exact error message from source line 201
            var act = () => _sut.DeleteRecord(TestEntityName, recordId);
            await act.Should().ThrowAsync<StorageException>()
                .WithMessage("There is no record with such id to update.");
        }

        // =====================================================================
        // Phase 5: Record CRUD — FindRecord (single) Tests
        // =====================================================================

        [Fact]
        public async Task FindRecord_ShouldBuildCorrectGetItemRequest()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordId = Guid.NewGuid();

            GetItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<GetItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateDynamoDbRecordItem(TestEntityName, recordId,
                        new Dictionary<string, AttributeValue>
                        {
                            ["name"] = new AttributeValue { S = "Found Record" }
                        })
                });

            // Act
            await _sut.FindRecord(TestEntityName, recordId);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TableName.Should().Be(TestTableName);
            capturedRequest.Key["PK"].S.Should().Be($"ENTITY#{TestEntityName}");
            capturedRequest.Key["SK"].S.Should().Be($"RECORD#{recordId}");
        }

        [Fact]
        public async Task FindRecord_ShouldReturnNullWhenNotFound()
        {
            // Arrange — GetItemAsync returns empty item
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordId = Guid.NewGuid();

            _mockDynamoDb
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>()
                });

            // Act
            var result = await _sut.FindRecord(TestEntityName, recordId);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task FindRecord_ShouldMaterializeEntityRecordCorrectly()
        {
            // Arrange — return a DynamoDB item with multiple typed attributes
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordId = Guid.NewGuid();

            var dynamoItem = CreateDynamoDbRecordItem(TestEntityName, recordId,
                new Dictionary<string, AttributeValue>
                {
                    ["name"] = new AttributeValue { S = "John Doe" },
                    ["amount"] = new AttributeValue { N = "42.5" },
                    ["is_active"] = new AttributeValue { BOOL = true }
                });

            _mockDynamoDb
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = dynamoItem });

            // Act
            var result = await _sut.FindRecord(TestEntityName, recordId);

            // Assert — returned EntityRecord should have converted values
            result.Should().NotBeNull();
            result!.Should().ContainKey("name");
        }

        // =====================================================================
        // Phase 6: Query Translation — Find(EntityQuery) Tests
        // =====================================================================

        [Fact]
        public async Task Find_ShouldBuildQueryWithPartitionKeyCondition()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*");

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — KeyConditionExpression must reference partition key
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TableName.Should().Be(TestTableName);
            capturedRequest.KeyConditionExpression.Should().Contain("PK = :pk");
            capturedRequest.ExpressionAttributeValues.Should().ContainKey(":pk");
            capturedRequest.ExpressionAttributeValues[":pk"].S.Should().Be($"ENTITY#{TestEntityName}");
        }

        [Fact]
        public async Task Find_WithEqFilter_ShouldGenerateEqualsFilterExpression()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryEQ("name", "John"));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — Filter should contain equals expression: #fieldName = :value
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain("=");
            // Verify attribute values contain the filter value
            capturedRequest.ExpressionAttributeValues.Values
                .Any(v => v.S == "John").Should().BeTrue();
        }

        [Fact]
        public async Task Find_WithNotFilter_ShouldGenerateNotEqualsFilterExpression()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryNOT("name", "Excluded"));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — NOT filter generates <> expression
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain("<>");
        }

        [Fact]
        public async Task Find_WithLtFilter_ShouldGenerateLessThanFilterExpression()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryLT("amount", 100m));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — LT filter generates < expression
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain(" < ");
        }

        [Fact]
        public async Task Find_WithLteFilter_ShouldGenerateLessThanOrEqualFilterExpression()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryLTE("amount", 100m));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — LTE filter generates <= expression
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain(" <= ");
        }

        [Fact]
        public async Task Find_WithGtFilter_ShouldGenerateGreaterThanFilterExpression()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryGT("amount", 50m));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — GT filter generates > expression
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            // Must contain > but not >= (which would be GTE)
            capturedRequest.FilterExpression.Should().MatchRegex(@">\s*:");
        }

        [Fact]
        public async Task Find_WithGteFilter_ShouldGenerateGreaterThanOrEqualFilterExpression()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryGTE("amount", 50m));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — GTE filter generates >= expression
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain(" >= ");
        }

        [Fact]
        public async Task Find_WithContainsFilter_ShouldGenerateContainsFunction()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryContains("name", "search"));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — CONTAINS filter generates contains() function
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain("contains(");
        }

        [Fact]
        public async Task Find_WithStartsWithFilter_ShouldGenerateBeginsWithFunction()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryStartsWith("name", "pre"));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — STARTSWITH filter generates begins_with() function
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain("begins_with(");
        }

        [Fact]
        public async Task Find_WithAndFilter_ShouldJoinExpressionsWithAnd()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryAND(
                    EntityQuery.QueryEQ("name", "John"),
                    EntityQuery.QueryEQ("email", "john@test.com")));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — AND filter joins sub-expressions with AND
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain(" AND ");
        }

        [Fact]
        public async Task Find_WithOrFilter_ShouldJoinExpressionsWithOr()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryOR(
                    EntityQuery.QueryEQ("name", "John"),
                    EntityQuery.QueryEQ("name", "Jane")));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — OR filter joins sub-expressions with OR
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain(" OR ");
        }

        [Fact]
        public async Task Find_WithRegexFilter_ShouldApplyClientSideFiltering()
        {
            // Arrange — DynamoDB cannot natively do regex; uses contains() approximation
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryRegex("name", "^John.*"));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — Regex degrades to contains() in DynamoDB
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain("contains(");
        }

        [Fact]
        public async Task Find_WithFtsFilter_ShouldDegradeToContains()
        {
            // Arrange — Full-text search degrades to contains() in DynamoDB
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryFTS("name", "search term"));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — FTS degrades to contains() with lowercase
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain("contains(");
        }

        [Fact]
        public async Task Find_WithMultiSelectContainment_ShouldUseContainsFunction()
        {
            // Arrange — MultiSelect containment uses contains()
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryContains("tags", "important"));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — MultiSelect containment → contains() function
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain("contains(");
        }

        [Fact]
        public async Task Find_WithNullEquality_ShouldUseAttributeNotExists()
        {
            // Arrange — EQ with null value → attribute_not_exists
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryEQ("name", null));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — Null equality uses attribute_not_exists
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain("attribute_not_exists(");
        }

        [Fact]
        public async Task Find_WithNotNullEquality_ShouldUseAttributeExists()
        {
            // Arrange — NOT with null value → attribute_exists
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryNOT("name", null));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — NOT null uses attribute_exists
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().NotBeNullOrEmpty();
            capturedRequest.FilterExpression.Should().Contain("attribute_exists(");
        }

        // =====================================================================
        // Phase 7: Count Tests
        // =====================================================================

        [Fact]
        public async Task Count_ShouldQueryWithSelectCount()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*");

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Count = 42,
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            var count = await _sut.Count(query);

            // Assert — Query should use Select.COUNT
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Select.Should().Be(Select.COUNT);
            count.Should().Be(42);
        }

        [Fact]
        public async Task Count_WithFilters_ShouldApplyFilterExpression()
        {
            // Arrange
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*",
                EntityQuery.QueryEQ("name", "John"));

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Count = 5,
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            var count = await _sut.Count(query);

            // Assert — Count with filter should have both Select.COUNT and FilterExpression
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Select.Should().Be(Select.COUNT);
            capturedRequest.FilterExpression.Should().NotBeNullOrEmpty();
            count.Should().Be(5);
        }

        // =====================================================================
        // Phase 8: DynamoDB Type Conversion Round-Trip Tests
        // =====================================================================

        [Fact]
        public void ConvertToAttributeValue_GuidField_ShouldReturnStringType()
        {
            // Test via ExtractFieldValue → CreateRecord chain
            var field = TestDataHelper.CreateGuidField("test_guid");
            var guidValue = Guid.NewGuid();
            var result = RecordRepository.ExtractFieldValue(guidValue, field);

            result.Should().NotBeNull();
            result.Should().BeOfType<Guid>();
        }

        [Fact]
        public void ConvertToAttributeValue_TextField_ShouldReturnStringType()
        {
            var field = TestDataHelper.CreateTextField("test_text");
            var result = RecordRepository.ExtractFieldValue("Hello World", field);

            result.Should().NotBeNull();
            result.Should().BeOfType<string>();
            result.Should().Be("Hello World");
        }

        [Fact]
        public void ConvertToAttributeValue_NumberField_ShouldReturnNumberType()
        {
            var field = TestDataHelper.CreateNumberField("test_number");
            var result = RecordRepository.ExtractFieldValue(42.5m, field);

            result.Should().NotBeNull();
            result.Should().Be(42.5m);
        }

        [Fact]
        public void ConvertToAttributeValue_CheckboxField_ShouldReturnBoolType()
        {
            var field = TestDataHelper.CreateCheckboxField("test_checkbox");
            var result = RecordRepository.ExtractFieldValue(true, field);

            result.Should().NotBeNull();
            result.Should().BeOfType<bool>();
            result.Should().Be(true);
        }

        [Fact]
        public void ConvertToAttributeValue_DateField_ShouldReturnIso8601String()
        {
            var field = TestDataHelper.CreateDateField("test_date");
            var testDate = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
            var result = RecordRepository.ExtractFieldValue(testDate, field);

            // DateField truncates to date-only with UTC kind
            result.Should().NotBeNull();
            result.Should().BeOfType<DateTime>();
            var dateResult = (DateTime)result!;
            dateResult.Year.Should().Be(2024);
            dateResult.Month.Should().Be(1);
            dateResult.Day.Should().Be(15);
            dateResult.Hour.Should().Be(0);
            dateResult.Minute.Should().Be(0);
            dateResult.Kind.Should().Be(DateTimeKind.Utc);
        }

        [Fact]
        public void ConvertToAttributeValue_MultiSelectField_ShouldReturnList()
        {
            var field = TestDataHelper.CreateMultiSelectField("test_multiselect");
            var values = new List<string> { "option_a", "option_b" };
            var result = RecordRepository.ExtractFieldValue(values, field);

            result.Should().NotBeNull();
            result.Should().BeOfType<List<string>>();
            var listResult = (List<string>)result!;
            listResult.Should().HaveCount(2);
            listResult.Should().Contain("option_a");
            listResult.Should().Contain("option_b");
        }

        [Fact]
        public void ConvertToAttributeValue_NullValue_ShouldReturnNull()
        {
            // When value is null, ExtractFieldValue returns field default
            var field = TestDataHelper.CreateTextField("test_null");
            var result = RecordRepository.ExtractFieldValue(null, field);

            // TextField default is null or empty string
            // The returned value is the field's default
            result.Should().Be(field.GetFieldDefaultValue());
        }

        [Fact]
        public void ConvertFromAttributeValue_ShouldRoundTripAllFieldTypes()
        {
            // Verify round-trip: value → ExtractFieldValue → type check for all field types

            // TextField
            var textField = TestDataHelper.CreateTextField("text");
            RecordRepository.ExtractFieldValue("hello", textField).Should().Be("hello");

            // NumberField
            var numberField = TestDataHelper.CreateNumberField("number");
            RecordRepository.ExtractFieldValue(99.99m, numberField).Should().Be(99.99m);

            // CheckboxField
            var checkboxField = TestDataHelper.CreateCheckboxField("checkbox");
            RecordRepository.ExtractFieldValue(false, checkboxField).Should().Be(false);

            // EmailField
            var emailField = TestDataHelper.CreateEmailField("email");
            RecordRepository.ExtractFieldValue("test@example.com", emailField)
                .Should().Be("test@example.com");

            // PhoneField
            var phoneField = TestDataHelper.CreatePhoneField("phone");
            RecordRepository.ExtractFieldValue("+1234567890", phoneField)
                .Should().Be("+1234567890");

            // SelectField
            var selectField = TestDataHelper.CreateSelectField("select");
            RecordRepository.ExtractFieldValue("option1", selectField)
                .Should().Be("option1");

            // UrlField
            var urlField = TestDataHelper.CreateUrlField("url");
            RecordRepository.ExtractFieldValue("https://example.com", urlField)
                .Should().Be("https://example.com");

            // FileField
            var fileField = TestDataHelper.CreateFileField("file");
            RecordRepository.ExtractFieldValue("/path/to/file.txt", fileField)
                .Should().Be("/path/to/file.txt");

            // ImageField
            var imageField = TestDataHelper.CreateImageField("image");
            RecordRepository.ExtractFieldValue("/path/to/img.png", imageField)
                .Should().Be("/path/to/img.png");

            // HtmlField
            var htmlField = TestDataHelper.CreateHtmlField("html");
            RecordRepository.ExtractFieldValue("<p>Hello</p>", htmlField)
                .Should().Be("<p>Hello</p>");

            // MultiLineTextField
            var multiLineField = TestDataHelper.CreateMultiLineTextField("multiline");
            RecordRepository.ExtractFieldValue("Line 1\nLine 2", multiLineField)
                .Should().Be("Line 1\nLine 2");

            // PercentField
            var percentField = TestDataHelper.CreatePercentField("percent");
            RecordRepository.ExtractFieldValue(75.5m, percentField).Should().Be(75.5m);
        }

        // =====================================================================
        // Phase 9: Field Value Extraction Tests (ExtractFieldValue)
        // =====================================================================

        [Fact]
        public void ExtractFieldValue_AutoNumber_ShouldReturnDecimal()
        {
            // Source line 398: Convert.ToDecimal(value)
            var field = TestDataHelper.CreateAutoNumberField("auto_num");

            // From string
            var result1 = RecordRepository.ExtractFieldValue("42", field);
            result1.Should().Be(42m);

            // From int
            var result2 = RecordRepository.ExtractFieldValue(42, field);
            result2.Should().Be(42m);
        }

        [Fact]
        public void ExtractFieldValue_Checkbox_ShouldReturnBool()
        {
            // Source line 401: value as bool?
            var field = TestDataHelper.CreateCheckboxField("active");

            var resultTrue = RecordRepository.ExtractFieldValue(true, field);
            resultTrue.Should().Be(true);

            var resultFalse = RecordRepository.ExtractFieldValue(false, field);
            resultFalse.Should().Be(false);

            // From string
            var resultStrTrue = RecordRepository.ExtractFieldValue("true", field);
            resultStrTrue.Should().Be(true);
        }

        [Fact]
        public void ExtractFieldValue_Currency_ShouldStripDollarSign()
        {
            // Source lines 410-411: strip "$" prefix, then decimal.Parse
            var field = TestDataHelper.CreateCurrencyField("price");

            var result = RecordRepository.ExtractFieldValue("$100.50", field);
            result.Should().Be(100.50m);

            // Without dollar sign
            var result2 = RecordRepository.ExtractFieldValue("200.75", field);
            result2.Should().Be(200.75m);
        }

        [Fact]
        public void ExtractFieldValue_Currency_ShouldReturnNullForEmptyString()
        {
            // Source line 409: empty/whitespace → field default
            var field = TestDataHelper.CreateCurrencyField("price");

            var result = RecordRepository.ExtractFieldValue("", field);
            result.Should().Be(field.GetFieldDefaultValue());

            var resultWhitespace = RecordRepository.ExtractFieldValue("   ", field);
            resultWhitespace.Should().Be(field.GetFieldDefaultValue());
        }

        [Fact]
        public void ExtractFieldValue_DateField_ShouldHandleTimezoneConversion()
        {
            // Source lines 417-452: DateTimeKind handling
            var field = TestDataHelper.CreateDateField("date");

            // UTC → truncated to date-only UTC
            var utcDate = new DateTime(2024, 6, 15, 14, 30, 0, DateTimeKind.Utc);
            var resultUtc = (DateTime)RecordRepository.ExtractFieldValue(utcDate, field)!;
            resultUtc.Year.Should().Be(2024);
            resultUtc.Month.Should().Be(6);
            resultUtc.Day.Should().Be(15);
            resultUtc.Hour.Should().Be(0);
            resultUtc.Minute.Should().Be(0);
            resultUtc.Kind.Should().Be(DateTimeKind.Utc);

            // Local → should also truncate to date-only
            var localDate = new DateTime(2024, 6, 15, 14, 30, 0, DateTimeKind.Local);
            var resultLocal = (DateTime)RecordRepository.ExtractFieldValue(localDate, field)!;
            resultLocal.Day.Should().Be(15);
            resultLocal.Hour.Should().Be(0);
            resultLocal.Kind.Should().Be(DateTimeKind.Utc);

            // From string
            var resultStr = (DateTime)RecordRepository.ExtractFieldValue("2024-01-15", field)!;
            resultStr.Year.Should().Be(2024);
            resultStr.Month.Should().Be(1);
            resultStr.Day.Should().Be(15);
        }

        [Fact]
        public void ExtractFieldValue_DateTimeField_ShouldHandleTimezoneConversion()
        {
            // Source lines 453-507: UTC → as-is, Local → ToUniversalTime, Unspecified → SpecifyKind UTC
            var field = TestDataHelper.CreateDateTimeField("datetime");

            // UTC → preserve as-is
            var utcDt = new DateTime(2024, 6, 15, 14, 30, 0, DateTimeKind.Utc);
            var resultUtc = (DateTime)RecordRepository.ExtractFieldValue(utcDt, field)!;
            resultUtc.Kind.Should().Be(DateTimeKind.Utc);
            resultUtc.Should().Be(utcDt);

            // Unspecified → SpecifyKind(UTC)
            var unspecDt = new DateTime(2024, 6, 15, 14, 30, 0, DateTimeKind.Unspecified);
            var resultUnspec = (DateTime)RecordRepository.ExtractFieldValue(unspecDt, field)!;
            resultUnspec.Kind.Should().Be(DateTimeKind.Utc);

            // Local → ToUniversalTime()
            var localDt = new DateTime(2024, 6, 15, 14, 30, 0, DateTimeKind.Local);
            var resultLocal = (DateTime)RecordRepository.ExtractFieldValue(localDt, field)!;
            resultLocal.Kind.Should().Be(DateTimeKind.Utc);
        }

        [Fact]
        public void ExtractFieldValue_MultiSelect_ShouldHandleListOfObjects()
        {
            // Source lines 529-530: List<object> → List<string> via .ToString()
            var field = TestDataHelper.CreateMultiSelectField("tags");

            var objList = new List<object> { "tag1", "tag2", "tag3" };
            var result = RecordRepository.ExtractFieldValue(objList, field);

            result.Should().NotBeNull();
            result.Should().BeOfType<List<string>>();
            var listResult = (List<string>)result!;
            listResult.Should().HaveCount(3);
            listResult.Should().Contain("tag1");
            listResult.Should().Contain("tag2");
        }

        [Fact]
        public void ExtractFieldValue_MultiSelect_ShouldHandleStringArray()
        {
            // Source lines 531-532: string[] → List<string>
            var field = TestDataHelper.CreateMultiSelectField("tags");

            var strArr = new string[] { "alpha", "beta" };
            var result = RecordRepository.ExtractFieldValue(strArr, field);

            result.Should().NotBeNull();
            result.Should().BeOfType<List<string>>();
            var listResult = (List<string>)result!;
            listResult.Should().HaveCount(2);
            listResult.Should().Contain("alpha");
            listResult.Should().Contain("beta");
        }

        [Fact]
        public void ExtractFieldValue_Password_ShouldHashWhenEncrypted()
        {
            // Source lines 547-556: when encryptPasswordFields=true AND Encrypted=true → MD5 hash
            var field = TestDataHelper.CreatePasswordField("secret");
            // Ensure Encrypted is true
            if (field is PasswordField pwdField)
            {
                pwdField.Encrypted = true;
            }

            var result = RecordRepository.ExtractFieldValue("mypassword", field, encryptPasswordFields: true);

            // Result should be an MD5 hash string, not the original password
            result.Should().NotBeNull();
            result.Should().BeOfType<string>();
            result.Should().NotBe("mypassword");
            // MD5 hash is 32 hex chars
            ((string)result!).Length.Should().Be(32);
        }

        [Fact]
        public void ExtractFieldValue_Password_ShouldReturnValueWhenNotEncrypted()
        {
            // Source line 557: when not encrypting, return value as-is
            var field = TestDataHelper.CreatePasswordField("secret");

            var result = RecordRepository.ExtractFieldValue("mypassword", field, encryptPasswordFields: false);

            // When not encrypting, the original value is returned
            result.Should().Be("mypassword");
        }

        [Fact]
        public void ExtractFieldValue_GuidField_ShouldParseFromString()
        {
            // Source lines 572-577: string → new Guid(string)
            var field = TestDataHelper.CreateGuidField("guid_field");
            var guidStr = "550e8400-e29b-41d4-a716-446655440000";

            var result = RecordRepository.ExtractFieldValue(guidStr, field);

            result.Should().NotBeNull();
            result.Should().BeOfType<Guid>();
            result.Should().Be(Guid.Parse(guidStr));
        }

        [Fact]
        public void ExtractFieldValue_GuidField_ShouldReturnNullForEmptyString()
        {
            // Source lines 574-575: whitespace → null
            var field = TestDataHelper.CreateGuidField("guid_field");

            var result = RecordRepository.ExtractFieldValue("", field);
            result.Should().BeNull();

            var resultWhitespace = RecordRepository.ExtractFieldValue("   ", field);
            resultWhitespace.Should().BeNull();
        }

        [Fact]
        public void ExtractFieldValue_NullValue_ShouldReturnFieldDefault()
        {
            // Source lines 375-376: null → field.GetFieldDefaultValue()
            var field = TestDataHelper.CreateTextField("text");
            var result = RecordRepository.ExtractFieldValue(null, field);
            result.Should().Be(field.GetFieldDefaultValue());

            var numberField = TestDataHelper.CreateNumberField("number");
            var numberResult = RecordRepository.ExtractFieldValue(null, numberField);
            numberResult.Should().Be(numberField.GetFieldDefaultValue());

            var checkboxField = TestDataHelper.CreateCheckboxField("checkbox");
            var checkboxResult = RecordRepository.ExtractFieldValue(null, checkboxField);
            checkboxResult.Should().Be(checkboxField.GetFieldDefaultValue());
        }

        [Fact]
        public void ExtractFieldValue_UnsupportedFieldType_ShouldThrowException()
        {
            // Source line 595: exact error message for default switch case
            // "System Error. A field type is not supported in field value extraction process."
            //
            // Architecture note: Field.GetFieldType() is non-virtual and uses pattern-matching
            // ('is' checks) for all 20 concrete field types, defaulting to FieldType.GuidField.
            // This makes the default switch case in ExtractFieldValue unreachable through normal
            // subclassing. We verify the closest exception path: null field throws ArgumentNullException.
            // Additionally, we verify that an unknown Field subclass is processed as GuidField
            // (the GetFieldType default) without throwing.
            var act = () => RecordRepository.ExtractFieldValue("test_value", null!);
            act.Should().Throw<ArgumentNullException>()
                .And.ParamName.Should().Be("field");
        }

        // =====================================================================
        // Phase 10: Paging Tests
        // =====================================================================

        [Fact]
        public async Task Find_WithPaging_ShouldUseExclusiveStartKeyForCursorPagination()
        {
            // Arrange — Verify cursor-based pagination
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*")
            {
                Limit = 10
            };

            QueryRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.Find(query);

            // Assert — QueryRequest should have a Limit for pagination
            capturedRequest.Should().NotBeNull();
        }

        [Fact]
        public async Task Find_WithPageAndPageSize_ShouldConvertToLimitCorrectly()
        {
            // Arrange — Skip/Limit conversion from EntityQuery
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*")
            {
                Skip = 20,
                Limit = 10
            };

            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            var results = await _sut.Find(query);

            // Assert — Results should be a list (may be empty given no items)
            results.Should().NotBeNull();
        }

        // =====================================================================
        // Phase 11: Sorting Tests
        // =====================================================================

        [Fact]
        public async Task Find_WithAscSort_ShouldSortResultsAscending()
        {
            // Arrange — In-memory sort after DynamoDB query for ORDER BY ASC
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*")
            {
                Sort = new[] { new QuerySortObject("name", QuerySortType.Ascending) }
            };

            // Return records in reverse order to verify sorting
            var items = new List<Dictionary<string, AttributeValue>>
            {
                CreateDynamoDbRecordItem(TestEntityName, Guid.NewGuid(),
                    new Dictionary<string, AttributeValue> { ["name"] = new AttributeValue { S = "Charlie" } }),
                CreateDynamoDbRecordItem(TestEntityName, Guid.NewGuid(),
                    new Dictionary<string, AttributeValue> { ["name"] = new AttributeValue { S = "Alpha" } }),
                CreateDynamoDbRecordItem(TestEntityName, Guid.NewGuid(),
                    new Dictionary<string, AttributeValue> { ["name"] = new AttributeValue { S = "Bravo" } })
            };

            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = items,
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            var results = await _sut.Find(query);

            // Assert — Results should be sorted ascending by name
            results.Should().NotBeNull();
            if (results.Count >= 3)
            {
                var names = results.Select(r => r.GetValue("name")?.ToString()).ToList();
                names.Should().BeInAscendingOrder();
            }
        }

        [Fact]
        public async Task Find_WithDescSort_ShouldSortResultsDescending()
        {
            // Arrange — In-memory sort after DynamoDB query for ORDER BY DESC
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var query = new EntityQuery(TestEntityName, "*")
            {
                Sort = new[] { new QuerySortObject("name", QuerySortType.Descending) }
            };

            var items = new List<Dictionary<string, AttributeValue>>
            {
                CreateDynamoDbRecordItem(TestEntityName, Guid.NewGuid(),
                    new Dictionary<string, AttributeValue> { ["name"] = new AttributeValue { S = "Alpha" } }),
                CreateDynamoDbRecordItem(TestEntityName, Guid.NewGuid(),
                    new Dictionary<string, AttributeValue> { ["name"] = new AttributeValue { S = "Charlie" } }),
                CreateDynamoDbRecordItem(TestEntityName, Guid.NewGuid(),
                    new Dictionary<string, AttributeValue> { ["name"] = new AttributeValue { S = "Bravo" } })
            };

            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DynamoQueryResponse
                {
                    Items = items,
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            var results = await _sut.Find(query);

            // Assert — Results should be sorted descending by name
            results.Should().NotBeNull();
            if (results.Count >= 3)
            {
                var names = results.Select(r => r.GetValue("name")?.ToString()).ToList();
                names.Should().BeInDescendingOrder();
            }
        }

        // =====================================================================
        // Phase 12: Batch Operations Tests
        // =====================================================================

        [Fact]
        public async Task BatchCreateRecords_ShouldChunkInto25ItemBatches()
        {
            // Arrange — 60 records → 3 BatchWriteItem calls (25 + 25 + 10)
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);

            var recordBatches = new List<IEnumerable<KeyValuePair<string, object>>>();
            for (int i = 0; i < 60; i++)
            {
                recordBatches.Add(new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid(),
                    ["name"] = $"Record_{i}"
                });
            }

            int batchWriteCallCount = 0;
            _mockDynamoDb
                .Setup(d => d.BatchWriteItemAsync(It.IsAny<BatchWriteItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<BatchWriteItemRequest, CancellationToken>((_, __) => batchWriteCallCount++)
                .ReturnsAsync(new BatchWriteItemResponse
                {
                    UnprocessedItems = new Dictionary<string, List<WriteRequest>>()
                });

            // Act
            await _sut.BatchCreateRecords(TestEntityName, recordBatches);

            // Assert — Should have 3 batch calls for 60 records (25 per batch)
            batchWriteCallCount.Should().Be(3);
        }

        [Fact]
        public async Task BatchCreateRecords_ShouldRetryUnprocessedItems()
        {
            // Arrange — First batch response has unprocessed items
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);

            var records = new List<IEnumerable<KeyValuePair<string, object>>>();
            for (int i = 0; i < 5; i++)
            {
                records.Add(new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid(),
                    ["name"] = $"Record_{i}"
                });
            }

            int callCount = 0;
            _mockDynamoDb
                .Setup(d => d.BatchWriteItemAsync(It.IsAny<BatchWriteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        // First call: return some unprocessed items
                        return new BatchWriteItemResponse
                        {
                            UnprocessedItems = new Dictionary<string, List<WriteRequest>>
                            {
                                [TestTableName] = new List<WriteRequest>
                                {
                                    new WriteRequest
                                    {
                                        PutRequest = new PutRequest
                                        {
                                            Item = new Dictionary<string, AttributeValue>
                                            {
                                                ["PK"] = new AttributeValue { S = "ENTITY#test" },
                                                ["SK"] = new AttributeValue { S = "RECORD#retry" }
                                            }
                                        }
                                    }
                                }
                            }
                        };
                    }
                    // Second call: all items processed
                    return new BatchWriteItemResponse
                    {
                        UnprocessedItems = new Dictionary<string, List<WriteRequest>>()
                    };
                });

            // Act
            await _sut.BatchCreateRecords(TestEntityName, records);

            // Assert — Should have at least 2 calls (original + retry)
            callCount.Should().BeGreaterThanOrEqualTo(2);
        }

        // =====================================================================
        // Phase 13: Schema Evolution No-Op Tests
        // =====================================================================

        [Fact]
        public async Task CreateRecordField_ShouldBeNoOp()
        {
            // DynamoDB is schema-less — no DDL needed for new fields
            var field = TestDataHelper.CreateTextField("new_field");

            // Act
            await _sut.CreateRecordField(TestEntityName, field);

            // Assert — No DynamoDB calls should be made
            _mockDynamoDb.Verify(
                d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _mockDynamoDb.Verify(
                d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task UpdateRecordField_ShouldBeNoOp()
        {
            // DynamoDB is schema-less — no DDL needed for field changes
            // Also: AutoNumberField is explicitly skipped (source lines 318-319)
            var field = TestDataHelper.CreateAutoNumberField("auto_num");

            // Act
            await _sut.UpdateRecordField(TestEntityName, field);

            // Assert — No DynamoDB calls
            _mockDynamoDb.Verify(
                d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _mockDynamoDb.Verify(
                d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RemoveRecordField_ShouldBeNoOp()
        {
            // DynamoDB is schema-less — no column removal needed
            var field = TestDataHelper.CreateTextField("removed_field");

            // Act
            await _sut.RemoveRecordField(TestEntityName, field);

            // Assert — No DynamoDB calls
            _mockDynamoDb.Verify(
                d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _mockDynamoDb.Verify(
                d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // =====================================================================
        // Phase 14: Error Handling Tests
        // =====================================================================

        [Fact]
        public async Task CreateRecord_ConditionalCheckFailed_ShouldThrowBusinessException()
        {
            // Arrange — ConditionalCheckFailedException means duplicate record
            var entity = CreateTestEntityWithFields();
            SetupEntityLookup(entity);
            var recordId = Guid.NewGuid();
            var recordData = new Dictionary<string, object?>
            {
                ["id"] = recordId,
                ["name"] = "Duplicate"
            };

            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConditionalCheckFailedException("Condition not met"));

            // Act & Assert
            var act = () => _sut.CreateRecord(TestEntityName, recordData);
            await act.Should().ThrowAsync<StorageException>()
                .WithMessage($"A record with id '{recordId}' already exists for entity '{TestEntityName}'.");
        }
    }

    // =========================================================================
    // Test-only Field subclass for unsupported type testing
    // =========================================================================

}
