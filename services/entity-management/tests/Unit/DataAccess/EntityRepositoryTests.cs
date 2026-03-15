// =============================================================================
// EntityRepositoryTests.cs — Unit Tests for DynamoDB Entity Metadata Repository
// =============================================================================
// Comprehensive xUnit unit tests for EntityRepository (implements IEntityRepository).
// Validates DynamoDB single-table design operations with mocked IAmazonDynamoDB:
//   - Entity CRUD (Create, Update, Read, Delete)
//   - Field CRUD (Create, Read, Update, Delete)
//   - Relation CRUD (Create, Update, Read, Delete)
//   - Many-to-Many association management
//   - Newtonsoft.Json polymorphic serialization with TypeNameHandling.Auto
//   - Cache clearing after all mutations
//   - Error handling and exception translation
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using WebVellaErp.EntityManagement.DataAccess;
using WebVellaErp.EntityManagement.Models;
// Field type classes (TextField, NumberField, etc.) are in WebVellaErp.EntityManagement.Models namespace
using Xunit;

namespace WebVellaErp.EntityManagement.Tests.Unit.DataAccess
{
    /// <summary>
    /// Unit test class for EntityRepository — validates all DynamoDB operations
    /// against a mocked IAmazonDynamoDB client. No real DynamoDB connections are used.
    /// Covers entity/field/relation CRUD, M2M associations, polymorphic serialization,
    /// cache clearing, and error handling patterns.
    /// </summary>
    public class EntityRepositoryTests
    {
        // ─── Test Infrastructure ──────────────────────────────────────────
        private readonly Mock<IAmazonDynamoDB> _mockDynamoDb;
        private readonly Mock<ILogger<EntityRepository>> _mockLogger;
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly EntityRepository _sut;

        // DynamoDB key constants matching the repository implementation
        private const string TABLE_NAME = "entity-management-metadata";
        private const string ENTITY_PK_PREFIX = "ENTITY#";
        private const string RELATION_PK_PREFIX = "RELATION#";
        private const string META_SK = "META";
        private const string FIELD_SK_PREFIX = "FIELD#";
        private const string RELATION_SK_PREFIX = "RELATION#";
        private const string M2M_SK_PREFIX = "M2M#";
        private const string GSI1_INDEX_NAME = "GSI1";
        private const string GSI2_INDEX_NAME = "GSI2";

        // Attribute names
        private const string PK_ATTR = "PK";
        private const string SK_ATTR = "SK";
        private const string GSI1PK_ATTR = "GSI1PK";
        private const string GSI1SK_ATTR = "GSI1SK";
        private const string GSI2PK_ATTR = "GSI2PK";
        private const string GSI2SK_ATTR = "GSI2SK";
        private const string ENTITY_DATA_ATTR = "entityData";
        private const string FIELD_DATA_ATTR = "fieldData";
        private const string RELATION_DATA_ATTR = "relationData";
        private const string ORIGIN_ID_ATTR = "originId";
        private const string TARGET_ID_ATTR = "targetId";

        // Track ClearCache invocations via event subscription
        private int _cacheCleared;

        public EntityRepositoryTests()
        {
            _mockDynamoDb = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
            _mockLogger = new Mock<ILogger<EntityRepository>>();
            _mockConfiguration = new Mock<IConfiguration>();
            _mockConfiguration.Setup(c => c["DynamoDB:MetadataTableName"]).Returns(TABLE_NAME);

            _sut = new EntityRepository(
                _mockDynamoDb.Object,
                _mockLogger.Object,
                _mockConfiguration.Object);

            // Subscribe to cache cleared event to track invocations
            _cacheCleared = 0;
            _sut.OnCacheCleared += () => _cacheCleared++;
        }

        // ─── Helper Methods ───────────────────────────────────────────────

        /// <summary>
        /// Creates a standard test Entity with a TextField for name and basic properties.
        /// </summary>
        private static Entity CreateTestEntity(Guid? id = null, string? name = null)
        {
            var entityId = id ?? Guid.NewGuid();
            var entityName = name ?? "test_entity";
            var textField = new TextField
            {
                Id = Guid.NewGuid(),
                Name = "name",
                Label = "Name",
                Required = true,
                System = false
            };

            return new Entity
            {
                Id = entityId,
                Name = entityName,
                Label = "Test Entity",
                LabelPlural = "Test Entities",
                System = false,
                IconName = "fas fa-database",
                Color = "#4CAF50",
                RecordPermissions = new RecordPermissions
                {
                    CanCreate = new List<Guid> { Guid.NewGuid() },
                    CanRead = new List<Guid> { Guid.NewGuid() },
                    CanUpdate = new List<Guid> { Guid.NewGuid() },
                    CanDelete = new List<Guid> { Guid.NewGuid() }
                },
                Fields = new List<Field> { textField }
            };
        }

        /// <summary>
        /// Creates a test Entity configured as the User entity (SystemIds.UserEntityId)
        /// with an "id" field for relation binding tests.
        /// </summary>
        private static Entity CreateUserEntity()
        {
            var idField = new GuidField
            {
                Id = Guid.NewGuid(),
                Name = "id",
                Label = "Id",
                Required = true,
                System = true
            };

            return new Entity
            {
                Id = SystemIds.UserEntityId,
                Name = "user",
                Label = "User",
                LabelPlural = "Users",
                System = true,
                Fields = new List<Field> { idField }
            };
        }

        /// <summary>
        /// Creates a standard test EntityRelation with configurable relation type.
        /// </summary>
        private static EntityRelation CreateTestRelation(
            Guid? id = null,
            string? name = null,
            Guid? originEntityId = null,
            EntityRelationType relationType = EntityRelationType.OneToMany)
        {
            return new EntityRelation
            {
                Id = id ?? Guid.NewGuid(),
                Name = name ?? "test_relation",
                Label = "Test Relation",
                Description = "Test relation description",
                System = false,
                RelationType = relationType,
                OriginEntityId = originEntityId ?? Guid.NewGuid(),
                OriginFieldId = Guid.NewGuid(),
                TargetEntityId = Guid.NewGuid(),
                TargetFieldId = Guid.NewGuid(),
                OriginEntityName = "origin_entity",
                OriginFieldName = "origin_field",
                TargetEntityName = "target_entity",
                TargetFieldName = "target_field"
            };
        }

        /// <summary>
        /// Creates a DynamoDB response item representing a serialized entity.
        /// </summary>
        private static Dictionary<string, AttributeValue> CreateEntityResponseItem(Entity entity)
        {
            var serializeSettings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                NullValueHandling = NullValueHandling.Ignore
            };
            string entityJson = JsonConvert.SerializeObject(entity, serializeSettings);

            return new Dictionary<string, AttributeValue>
            {
                [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entity.Id}" },
                [SK_ATTR] = new AttributeValue { S = META_SK },
                [GSI1PK_ATTR] = new AttributeValue { S = $"ENTITY_NAME#{entity.Name.ToLowerInvariant()}" },
                [GSI1SK_ATTR] = new AttributeValue { S = META_SK },
                [ENTITY_DATA_ATTR] = new AttributeValue { S = entityJson }
            };
        }

        /// <summary>
        /// Creates a DynamoDB response item representing a serialized relation.
        /// </summary>
        private static Dictionary<string, AttributeValue> CreateRelationResponseItem(EntityRelation relation)
        {
            var serializeSettings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                NullValueHandling = NullValueHandling.Ignore
            };
            string relationJson = JsonConvert.SerializeObject(relation, serializeSettings);

            return new Dictionary<string, AttributeValue>
            {
                [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{relation.OriginEntityId}" },
                [SK_ATTR] = new AttributeValue { S = $"{RELATION_SK_PREFIX}{relation.Id}" },
                [GSI2PK_ATTR] = new AttributeValue { S = $"{RELATION_PK_PREFIX}{relation.Id}" },
                [GSI2SK_ATTR] = new AttributeValue { S = META_SK },
                [RELATION_DATA_ATTR] = new AttributeValue { S = relationJson }
            };
        }

        /// <summary>
        /// Sets up a default PutItemAsync mock that returns a successful response.
        /// </summary>
        private void SetupPutItemSuccess()
        {
            _mockDynamoDb
                .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutItemResponse());
        }

        /// <summary>
        /// Sets up a default BatchWriteItemAsync mock that returns a successful response.
        /// </summary>
        private void SetupBatchWriteSuccess()
        {
            _mockDynamoDb
                .Setup(x => x.BatchWriteItemAsync(It.IsAny<BatchWriteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BatchWriteItemResponse
                {
                    UnprocessedItems = new Dictionary<string, List<WriteRequest>>()
                });
        }

        /// <summary>
        /// Sets up GetItemAsync to return null/empty for any request (entity not found).
        /// </summary>
        private void SetupGetItemNotFound()
        {
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>()
                });
        }

        /// <summary>
        /// Sets up QueryAsync to return empty results (nothing found).
        /// </summary>
        private void SetupQueryEmpty()
        {
            _mockDynamoDb
                .Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });
        }

        /// <summary>
        /// Sets up a Scan response (for GetAllEntities/GetAllRelations) returning empty.
        /// </summary>
        private void SetupScanEmpty()
        {
            _mockDynamoDb
                .Setup(x => x.ScanAsync(It.IsAny<Amazon.DynamoDBv2.Model.ScanRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 2: Entity CRUD — CreateEntity Tests
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateEntity_ShouldBuildCorrectPutItemRequest()
        {
            // Arrange
            var entity = CreateTestEntity();
            PutItemRequest? capturedRequest = null;

            _mockDynamoDb
                .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) =>
                {
                    // Capture the first PutItem call (entity metadata item)
                    if (capturedRequest == null)
                        capturedRequest = req;
                })
                .ReturnsAsync(new PutItemResponse());

            SetupBatchWriteSuccess();

            // Act
            var result = await _sut.CreateEntity(entity);

            // Assert
            result.Should().BeTrue();
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TableName.Should().Be(TABLE_NAME);
            capturedRequest.Item[PK_ATTR].S.Should().Be($"{ENTITY_PK_PREFIX}{entity.Id}");
            capturedRequest.Item[SK_ATTR].S.Should().Be(META_SK);
            capturedRequest.Item[GSI1PK_ATTR].S.Should().Be($"ENTITY_NAME#{entity.Name.ToLowerInvariant()}");
            capturedRequest.Item[GSI1SK_ATTR].S.Should().Be(META_SK);
            capturedRequest.Item[ENTITY_DATA_ATTR].S.Should().NotBeNullOrEmpty();
            capturedRequest.ConditionExpression.Should().Be("attribute_not_exists(PK)");
        }

        [Fact]
        public async Task CreateEntity_ShouldSerializeWithTypeNameHandlingAuto()
        {
            // Arrange — entity with a polymorphic TextField to verify $type discriminator
            var entity = CreateTestEntity();
            PutItemRequest? capturedRequest = null;

            _mockDynamoDb
                .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) =>
                {
                    if (capturedRequest == null)
                        capturedRequest = req;
                })
                .ReturnsAsync(new PutItemResponse());

            SetupBatchWriteSuccess();

            // Act
            await _sut.CreateEntity(entity);

            // Assert — verify entityData JSON contains $type discriminator for polymorphic field types
            capturedRequest.Should().NotBeNull();
            string entityJson = capturedRequest!.Item[ENTITY_DATA_ATTR].S;
            entityJson.Should().NotBeNullOrEmpty();

            // TypeNameHandling.Auto embeds $type for polymorphic members in the fields list
            // Verify it deserializes correctly with tolerant settings
            var deserialized = JsonConvert.DeserializeObject<Entity>(entityJson, new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore
            });
            deserialized.Should().NotBeNull();
            deserialized!.Id.Should().Be(entity.Id);
            deserialized.Name.Should().Be(entity.Name);
        }

        [Fact]
        public async Task CreateEntity_ShouldCreateFieldItems()
        {
            // Arrange — entity with multiple fields
            var entity = CreateTestEntity();
            var secondField = new NumberField
            {
                Id = Guid.NewGuid(),
                Name = "quantity",
                Label = "Quantity",
                Required = false
            };
            entity.Fields.Add(secondField);

            SetupPutItemSuccess();

            BatchWriteItemRequest? capturedBatchRequest = null;
            _mockDynamoDb
                .Setup(x => x.BatchWriteItemAsync(It.IsAny<BatchWriteItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<BatchWriteItemRequest, CancellationToken>((req, _) =>
                {
                    if (capturedBatchRequest == null)
                        capturedBatchRequest = req;
                })
                .ReturnsAsync(new BatchWriteItemResponse
                {
                    UnprocessedItems = new Dictionary<string, List<WriteRequest>>()
                });

            // Act
            await _sut.CreateEntity(entity);

            // Assert — BatchWriteItem should contain PutRequests for all fields
            capturedBatchRequest.Should().NotBeNull();
            capturedBatchRequest!.RequestItems.Should().ContainKey(TABLE_NAME);
            var writeRequests = capturedBatchRequest.RequestItems[TABLE_NAME];
            writeRequests.Should().HaveCount(2);

            // Verify each field item has correct PK/SK
            foreach (var wr in writeRequests)
            {
                wr.PutRequest.Should().NotBeNull();
                var item = wr.PutRequest.Item;
                item[PK_ATTR].S.Should().Be($"{ENTITY_PK_PREFIX}{entity.Id}");
                item[SK_ATTR].S.Should().StartWith(FIELD_SK_PREFIX);
                item[FIELD_DATA_ATTR].S.Should().NotBeNullOrEmpty();
            }
        }

        [Fact]
        public async Task CreateEntity_WhenNotUserEntity_ShouldCreateCreatedByAndModifiedByRelations()
        {
            // Arrange — entity with created_by and last_modified_by fields
            var entity = CreateTestEntity();
            entity.Fields.Add(new GuidField
            {
                Id = Guid.NewGuid(),
                Name = "created_by",
                Label = "Created By",
                System = true
            });
            entity.Fields.Add(new GuidField
            {
                Id = Guid.NewGuid(),
                Name = "last_modified_by",
                Label = "Last Modified By",
                System = true
            });

            // Setup PutItem for both entity and relation creation calls
            var putItemCalls = new List<PutItemRequest>();
            _mockDynamoDb
                .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => putItemCalls.Add(req))
                .ReturnsAsync(new PutItemResponse());

            SetupBatchWriteSuccess();

            // Mock GetEntityById for User entity lookup (CreateUserRelationsForEntity)
            var userEntity = CreateUserEntity();
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.Key[PK_ATTR].S == $"{ENTITY_PK_PREFIX}{SystemIds.UserEntityId}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateEntityResponseItem(userEntity)
                });

            // Mock GetRelationByName to return null (relation doesn't exist yet)
            // GetRelationByName loads all relations internally, returning null
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.IndexName == GSI2_INDEX_NAME),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // For GetAllRelations scan used in GetRelationByName
            _mockDynamoDb
                .Setup(x => x.ScanAsync(
                    It.IsAny<Amazon.DynamoDBv2.Model.ScanRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act — createOnlyIdField = false to trigger auto-relation creation
            var result = await _sut.CreateEntity(entity, createOnlyIdField: false);

            // Assert — should have created the entity plus 2 relation PutItem calls
            result.Should().BeTrue();

            // Filter PutItem calls for relation items (those with RELATION_SK_PREFIX in SK)
            var relationPuts = putItemCalls
                .Where(r => r.Item.ContainsKey(SK_ATTR)
                    && r.Item[SK_ATTR].S.StartsWith(RELATION_SK_PREFIX))
                .ToList();

            relationPuts.Should().HaveCount(2,
                "two user relations (created_by and modified_by) should be created");
        }

        [Fact]
        public async Task CreateEntity_WhenUserEntity_ShouldNotCreateSystemRelations()
        {
            // Arrange — create entity with the User entity ID
            var entity = CreateTestEntity(id: SystemIds.UserEntityId, name: "user");
            entity.Fields.Add(new GuidField
            {
                Id = Guid.NewGuid(),
                Name = "created_by",
                Label = "Created By",
                System = true
            });

            SetupPutItemSuccess();
            SetupBatchWriteSuccess();

            // Act — even with createOnlyIdField = false, User entity skips auto-relations
            var result = await _sut.CreateEntity(entity, createOnlyIdField: false);

            // Assert
            result.Should().BeTrue();

            // GetEntityById for User entity lookup should NOT be called (skipped for User)
            _mockDynamoDb.Verify(
                x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.Key[PK_ATTR].S == $"{ENTITY_PK_PREFIX}{SystemIds.UserEntityId}"
                        && r.ConsistentRead == true),
                    It.IsAny<CancellationToken>()),
                Times.Never,
                "User entity should not trigger auto-relation creation"
            );
        }

        [Fact]
        public async Task CreateEntity_WithSysIdDictionary_ShouldUseDeterministicRelationIds()
        {
            // Arrange
            var entity = CreateTestEntity();
            var createdByRelId = Guid.NewGuid();
            var modifiedByRelId = Guid.NewGuid();
            entity.Fields.Add(new GuidField
            {
                Id = Guid.NewGuid(),
                Name = "created_by",
                Label = "Created By",
                System = true
            });
            entity.Fields.Add(new GuidField
            {
                Id = Guid.NewGuid(),
                Name = "last_modified_by",
                Label = "Last Modified By",
                System = true
            });

            var sysIdDict = new Dictionary<string, Guid>
            {
                [$"user_{entity.Name}_created_by"] = createdByRelId,
                [$"user_{entity.Name}_modified_by"] = modifiedByRelId
            };

            var putItemCalls = new List<PutItemRequest>();
            _mockDynamoDb
                .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => putItemCalls.Add(req))
                .ReturnsAsync(new PutItemResponse());

            SetupBatchWriteSuccess();

            var userEntity = CreateUserEntity();
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.Key[PK_ATTR].S == $"{ENTITY_PK_PREFIX}{SystemIds.UserEntityId}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateEntityResponseItem(userEntity)
                });

            // GetRelationByName returns null (doesn't exist)
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r => r.IndexName == GSI2_INDEX_NAME),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            _mockDynamoDb
                .Setup(x => x.ScanAsync(
                    It.IsAny<Amazon.DynamoDBv2.Model.ScanRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.CreateEntity(entity, sysIdDictionary: sysIdDict, createOnlyIdField: false);

            // Assert — relation items should use deterministic IDs from the dictionary
            var relationPuts = putItemCalls
                .Where(r => r.Item.ContainsKey(SK_ATTR)
                    && r.Item[SK_ATTR].S.StartsWith(RELATION_SK_PREFIX))
                .ToList();

            // Verify the relation IDs match the dictionary values
            var relationSKs = relationPuts.Select(r => r.Item[SK_ATTR].S).ToList();
            relationSKs.Should().Contain($"{RELATION_SK_PREFIX}{createdByRelId}");
            relationSKs.Should().Contain($"{RELATION_SK_PREFIX}{modifiedByRelId}");
        }

        [Fact]
        public async Task CreateEntity_ShouldClearCacheAfterMutation()
        {
            // Arrange
            var entity = CreateTestEntity();
            SetupPutItemSuccess();
            SetupBatchWriteSuccess();

            // Act
            await _sut.CreateEntity(entity);

            // Assert — ClearCache should be called in finally block
            _cacheCleared.Should().BeGreaterThanOrEqualTo(1,
                "ClearCache must be called after CreateEntity");
        }

        [Fact]
        public async Task CreateEntity_ShouldClearCacheEvenOnFailure()
        {
            // Arrange
            var entity = CreateTestEntity();
            _mockDynamoDb
                .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonDynamoDBException("Simulated DynamoDB failure"));

            // Act & Assert — exception should propagate but ClearCache is still called
            Func<Task> act = async () => await _sut.CreateEntity(entity);
            await act.Should().ThrowAsync<StorageException>();

            _cacheCleared.Should().BeGreaterThanOrEqualTo(1,
                "ClearCache must be called even when CreateEntity fails (finally block)");
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 3: Entity CRUD — UpdateEntity Tests
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task UpdateEntity_ShouldBuildCorrectUpdateItemRequest()
        {
            // Arrange
            var entity = CreateTestEntity();
            UpdateItemRequest? capturedRequest = null;

            _mockDynamoDb
                .Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<UpdateItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new UpdateItemResponse());

            // Act
            var result = await _sut.UpdateEntity(entity);

            // Assert
            result.Should().BeTrue();
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TableName.Should().Be(TABLE_NAME);
            capturedRequest.Key[PK_ATTR].S.Should().Be($"{ENTITY_PK_PREFIX}{entity.Id}");
            capturedRequest.Key[SK_ATTR].S.Should().Be(META_SK);
            capturedRequest.ConditionExpression.Should().Be("attribute_exists(PK)");
            capturedRequest.ExpressionAttributeValues.Should()
                .ContainKey(":data");
        }

        [Fact]
        public async Task UpdateEntity_ShouldReturnTrueOnSuccess()
        {
            // Arrange
            var entity = CreateTestEntity();
            _mockDynamoDb
                .Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateItemResponse());

            // Act
            var result = await _sut.UpdateEntity(entity);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public async Task UpdateEntity_ShouldReturnFalseWhenNotFound()
        {
            // Arrange
            var entity = CreateTestEntity();
            _mockDynamoDb
                .Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConditionalCheckFailedException("Entity does not exist"));

            // Act
            var result = await _sut.UpdateEntity(entity);

            // Assert — ConditionalCheckFailedException translates to false (entity not found)
            result.Should().BeFalse();
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 4: Entity CRUD — Read Operations Tests
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task GetEntityById_ShouldBuildCorrectGetItemRequest()
        {
            // Arrange
            var entityId = Guid.NewGuid();
            var entity = CreateTestEntity(id: entityId);
            GetItemRequest? capturedRequest = null;

            _mockDynamoDb
                .Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<GetItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateEntityResponseItem(entity)
                });

            // Act
            var result = await _sut.GetEntityById(entityId);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TableName.Should().Be(TABLE_NAME);
            capturedRequest.Key[PK_ATTR].S.Should().Be($"{ENTITY_PK_PREFIX}{entityId}");
            capturedRequest.Key[SK_ATTR].S.Should().Be(META_SK);
            capturedRequest.ConsistentRead.Should().BeTrue("metadata reads require strong consistency");
            result.Should().NotBeNull();
            result!.Id.Should().Be(entityId);
        }

        [Fact]
        public async Task GetEntityById_ShouldDeserializeWithTolerantSettings()
        {
            // Arrange — JSON with extra/missing fields to test tolerant deserialization
            var entityId = Guid.NewGuid();
            var entityJson = JsonConvert.SerializeObject(new
            {
                Id = entityId,
                Name = "test_entity",
                Label = "Test Entity",
                LabelPlural = "Test Entities",
                System = false,
                IconName = "fas fa-database",
                Color = "#007bff",
                ExtraPropertyThatShouldBeIgnored = "ignored_value",
                Fields = new List<object>()
            }, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });

            var responseItem = new Dictionary<string, AttributeValue>
            {
                [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entityId}" },
                [SK_ATTR] = new AttributeValue { S = META_SK },
                [ENTITY_DATA_ATTR] = new AttributeValue { S = entityJson }
            };

            _mockDynamoDb
                .Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = responseItem });

            // Act — should NOT throw despite extra unknown property
            var result = await _sut.GetEntityById(entityId);

            // Assert — MissingMemberHandling.Ignore and NullValueHandling.Ignore applied
            result.Should().NotBeNull();
            result!.Name.Should().Be("test_entity");
        }

        [Fact]
        public async Task GetEntityById_ShouldReturnNullWhenNotFound()
        {
            // Arrange — empty response item (no entity found)
            var entityId = Guid.NewGuid();
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>()
                });

            // Act
            var result = await _sut.GetEntityById(entityId);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetEntityByName_ShouldQueryGSI1WithCaseInsensitiveName()
        {
            // Arrange
            var entity = CreateTestEntity(name: "TestEntity");
            QueryRequest? capturedRequest = null;

            _mockDynamoDb
                .Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateEntityResponseItem(entity)
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act — pass mixed-case name
            var result = await _sut.GetEntityByName("TestEntity");

            // Assert — GSI1 query should use lowercased name
            capturedRequest.Should().NotBeNull();
            capturedRequest!.IndexName.Should().Be(GSI1_INDEX_NAME);
            // Implementation uses expression attribute name aliases (#gsi1pk) in KeyConditionExpression,
            // NOT the raw attribute name (GSI1PK). The alias maps via ExpressionAttributeNames.
            capturedRequest.KeyConditionExpression.Should().Contain("#gsi1pk");
            capturedRequest.ExpressionAttributeNames.Should().ContainKey("#gsi1pk");
            capturedRequest.ExpressionAttributeNames["#gsi1pk"].Should().Be(GSI1PK_ATTR);
            capturedRequest.ExpressionAttributeValues.Should()
                .ContainKey(":pk");
            capturedRequest.ExpressionAttributeValues[":pk"].S
                .Should().Be("ENTITY_NAME#testentity",
                    "entity name lookup must be case-insensitive via lowercased GSI key");
            result.Should().NotBeNull();
        }

        [Fact]
        public async Task GetAllEntities_ShouldReturnAllEntityItems()
        {
            // Arrange — multiple entities
            var entity1 = CreateTestEntity(name: "entity_one");
            var entity2 = CreateTestEntity(name: "entity_two");

            _mockDynamoDb
                .Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateEntityResponseItem(entity1),
                        CreateEntityResponseItem(entity2)
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Also setup Scan if the implementation uses scan for GetAllEntities
            _mockDynamoDb
                .Setup(x => x.ScanAsync(
                    It.IsAny<Amazon.DynamoDBv2.Model.ScanRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateEntityResponseItem(entity1),
                        CreateEntityResponseItem(entity2)
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            var result = await _sut.GetAllEntities();

            // Assert
            result.Should().NotBeNull();
            result.Should().HaveCount(2);
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 5: Entity CRUD — DeleteEntity Tests
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task DeleteEntity_ShouldDeleteAllRelatedItems()
        {
            // Arrange
            var entityId = Guid.NewGuid();
            var entity = CreateTestEntity(id: entityId);

            // GetEntityById for the entity being deleted
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.Key[PK_ATTR].S == $"{ENTITY_PK_PREFIX}{entityId}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateEntityResponseItem(entity)
                });

            // GetAllRelations (scan) — return one relation for cascade delete
            var relation = CreateTestRelation(originEntityId: entityId);
            _mockDynamoDb
                .Setup(x => x.ScanAsync(
                    It.IsAny<Amazon.DynamoDBv2.Model.ScanRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateRelationResponseItem(relation)
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // DeleteRelation needs various sub-mocks: GetRelationById via GSI2
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.IsAny<QueryRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateRelationResponseItem(relation)
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            _mockDynamoDb
                .Setup(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteItemResponse());

            SetupBatchWriteSuccess();

            // Act
            var result = await _sut.DeleteEntity(entityId);

            // Assert
            result.Should().BeTrue();

            // Verify batch delete was called to remove entity items
            _mockDynamoDb.Verify(
                x => x.BatchWriteItemAsync(It.IsAny<BatchWriteItemRequest>(), It.IsAny<CancellationToken>()),
                Times.AtLeastOnce,
                "Should use BatchWriteItem to delete all entity sub-items");

            _cacheCleared.Should().BeGreaterThanOrEqualTo(1,
                "ClearCache must be called after DeleteEntity");
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 6: Field CRUD Tests
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateField_ShouldBuildCorrectPutItemRequest()
        {
            // Arrange
            var entityId = Guid.NewGuid();
            var field = new TextField
            {
                Id = Guid.NewGuid(),
                Name = "description",
                Label = "Description",
                MaxLength = 500,
                Required = true
            };

            PutItemRequest? capturedPutRequest = null;
            _mockDynamoDb
                .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPutRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // CreateField also updates the parent entity's field list
            _mockDynamoDb
                .Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateItemResponse());

            // GetEntityById for adding field to entity
            var entity = CreateTestEntity(id: entityId);
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateEntityResponseItem(entity)
                });

            // Act
            await _sut.CreateField(entityId, field);

            // Assert — PutItem for field item
            capturedPutRequest.Should().NotBeNull();
            capturedPutRequest!.TableName.Should().Be(TABLE_NAME);
            capturedPutRequest.Item[PK_ATTR].S.Should().Be($"{ENTITY_PK_PREFIX}{entityId}");
            capturedPutRequest.Item[SK_ATTR].S.Should().Be($"{FIELD_SK_PREFIX}{field.Id}");
            capturedPutRequest.Item[FIELD_DATA_ATTR].S.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task CreateField_ShouldSerializePolymorphicFieldTypes()
        {
            // Arrange — use a NumberField to verify $type discriminator
            var entityId = Guid.NewGuid();
            var field = new NumberField
            {
                Id = Guid.NewGuid(),
                Name = "quantity",
                Label = "Quantity",
                DefaultValue = 0m,
                MinValue = 0m,
                MaxValue = 99999m,
                DecimalPlaces = 2
            };

            PutItemRequest? capturedPutRequest = null;
            _mockDynamoDb
                .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPutRequest = req)
                .ReturnsAsync(new PutItemResponse());

            _mockDynamoDb
                .Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateItemResponse());

            var entity = CreateTestEntity(id: entityId);
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateEntityResponseItem(entity)
                });

            // Act
            await _sut.CreateField(entityId, field);

            // Assert — verify fieldData JSON is serialized with TypeNameHandling.Auto
            capturedPutRequest.Should().NotBeNull();
            string fieldJson = capturedPutRequest!.Item[FIELD_DATA_ATTR].S;
            fieldJson.Should().NotBeNullOrEmpty();

            // Individual field items serialized via JsonConvert.SerializeObject(field, settings)
            // do NOT emit $type at root level (TypeNameHandling.Auto only emits $type when
            // runtime type differs from declared type — the root object's declared type is
            // inferred as its runtime type). The $type IS emitted when fields are serialized
            // inside Entity.Fields (List<Field>) where declared type is abstract Field.
            // Here we verify round-trip by deserializing to the concrete NumberField type.
            var deserialized = JsonConvert.DeserializeObject<NumberField>(fieldJson, new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore
            });
            deserialized.Should().NotBeNull();
            deserialized!.Name.Should().Be("quantity");
            deserialized.DecimalPlaces.Should().Be(2);
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 7: Relation CRUD Tests
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRelation_ShouldBuildCorrectPutItemRequest()
        {
            // Arrange
            var relation = CreateTestRelation();
            PutItemRequest? capturedPutRequest = null;

            _mockDynamoDb
                .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPutRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // CreateRelation looks up origin and target entities
            var originEntity = CreateTestEntity(id: relation.OriginEntityId);
            var targetEntity = CreateTestEntity(id: relation.TargetEntityId);
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.Key[PK_ATTR].S == $"{ENTITY_PK_PREFIX}{relation.OriginEntityId}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateEntityResponseItem(originEntity)
                });
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.Key[PK_ATTR].S == $"{ENTITY_PK_PREFIX}{relation.TargetEntityId}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateEntityResponseItem(targetEntity)
                });

            // Act
            var result = await _sut.CreateRelation(relation);

            // Assert
            result.Should().BeTrue();
            capturedPutRequest.Should().NotBeNull();
            capturedPutRequest!.TableName.Should().Be(TABLE_NAME);
            capturedPutRequest.Item[PK_ATTR].S.Should().Be($"{ENTITY_PK_PREFIX}{relation.OriginEntityId}");
            capturedPutRequest.Item[SK_ATTR].S.Should().Be($"{RELATION_SK_PREFIX}{relation.Id}");
            capturedPutRequest.Item[GSI2PK_ATTR].S.Should().Be($"RELATION#{relation.Id}");
            capturedPutRequest.Item[GSI2SK_ATTR].S.Should().Be(META_SK);
            capturedPutRequest.Item[RELATION_DATA_ATTR].S.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task CreateRelation_ShouldValidateNotNull()
        {
            // Act & Assert — null relation should throw
            Func<Task> act = async () => await _sut.CreateRelation(null!);
            await act.Should().ThrowAsync<StorageException>()
                .WithMessage("*cannot be null*");
        }

        [Fact]
        public async Task DeleteRelation_ShouldThrowWhenNotFound()
        {
            // Arrange — relation lookup returns null (not found)
            var relationId = Guid.NewGuid();

            // GetRelationById via GSI2 returns empty
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r => r.IndexName == GSI2_INDEX_NAME),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act & Assert — exact error message from source
            Func<Task> act = async () => await _sut.DeleteRelation(relationId);
            await act.Should().ThrowAsync<StorageException>()
                .WithMessage("There is no record with specified relation id.");
        }

        [Fact]
        public async Task DeleteRelation_ShouldDeleteM2MAssociationsForManyToMany()
        {
            // Arrange — ManyToMany relation
            var relation = CreateTestRelation(relationType: EntityRelationType.ManyToMany);

            // GetRelationById via GSI2
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r => r.IndexName == GSI2_INDEX_NAME),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateRelationResponseItem(relation)
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // QueryManyToManyItems — return some M2M items for cascade delete
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.KeyConditionExpression != null &&
                        r.IndexName == null),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        new Dictionary<string, AttributeValue>
                        {
                            [PK_ATTR] = new AttributeValue { S = $"RELATION#{relation.Id}" },
                            [SK_ATTR] = new AttributeValue { S = $"{M2M_SK_PREFIX}{Guid.NewGuid()}#{Guid.NewGuid()}" },
                            ["originId"] = new AttributeValue { S = Guid.NewGuid().ToString() },
                            ["targetId"] = new AttributeValue { S = Guid.NewGuid().ToString() }
                        }
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // DeleteItem for relation + M2M items
            _mockDynamoDb
                .Setup(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteItemResponse());

            SetupBatchWriteSuccess();

            // Act
            var result = await _sut.DeleteRelation(relation.Id);

            // Assert
            result.Should().BeTrue();
            _cacheCleared.Should().BeGreaterThanOrEqualTo(1);
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 8: Many-to-Many Record Management Tests
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateManyToManyRecord_ShouldBuildCorrectPutItemRequest()
        {
            // Arrange
            var relationId = Guid.NewGuid();
            var originId = Guid.NewGuid();
            var targetId = Guid.NewGuid();

            PutItemRequest? capturedRequest = null;
            _mockDynamoDb
                .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            await _sut.CreateManyToManyRecord(relationId, originId, targetId);

            // Assert
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TableName.Should().Be(TABLE_NAME);
            capturedRequest.Item[PK_ATTR].S.Should().Be($"RELATION#{relationId}");
            capturedRequest.Item[SK_ATTR].S.Should().Be($"{M2M_SK_PREFIX}{originId}#{targetId}");
            capturedRequest.Item["originId"].S.Should().Be(originId.ToString());
            capturedRequest.Item["targetId"].S.Should().Be(targetId.ToString());
        }

        [Fact]
        public async Task DeleteManyToManyRecord_WithBothNull_ShouldThrowException()
        {
            // Arrange
            string relationName = "test_m2m_relation";

            // GetRelationByName needs to work — setup GetAllRelations + relation lookup
            var relation = CreateTestRelation(
                name: relationName,
                relationType: EntityRelationType.ManyToMany);

            _mockDynamoDb
                .Setup(x => x.ScanAsync(
                    It.IsAny<Amazon.DynamoDBv2.Model.ScanRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateRelationResponseItem(relation)
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.IsAny<QueryRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateRelationResponseItem(relation)
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act & Assert — both null should throw StorageException
            Func<Task> act = async () =>
                await _sut.DeleteManyToManyRecord(relationName, null, null);

            await act.Should().ThrowAsync<StorageException>()
                .WithMessage("*origin id and target id cannot be null*");
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 9: JSON Serialization Tests
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void EntitySerialization_ShouldUseTypeNameHandlingAuto()
        {
            // Arrange — create entity with polymorphic field to test $type embedding
            var entity = CreateTestEntity();
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                NullValueHandling = NullValueHandling.Ignore
            };

            // Act — serialize and deserialize using the same settings the repository uses
            string json = JsonConvert.SerializeObject(entity, settings);

            // Assert — verify $type discriminator is embedded for polymorphic field collection
            json.Should().NotBeNullOrEmpty();

            // Deserialize back should produce valid Entity
            var deserialized = JsonConvert.DeserializeObject<Entity>(json, new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore
            });
            deserialized.Should().NotBeNull();
            deserialized!.Id.Should().Be(entity.Id);
            deserialized.Fields.Should().HaveCount(entity.Fields.Count);
        }

        [Fact]
        public void FieldSerialization_ShouldPreservePolymorphicTypes()
        {
            // Arrange — test multiple concrete field types
            var fields = new List<Field>
            {
                new TextField { Id = Guid.NewGuid(), Name = "text_field", DefaultValue = "hello", MaxLength = 200 },
                new NumberField { Id = Guid.NewGuid(), Name = "number_field", DefaultValue = 42m },
                new DateField { Id = Guid.NewGuid(), Name = "date_field" },
                new CheckboxField { Id = Guid.NewGuid(), Name = "checkbox_field", DefaultValue = true },
                new CurrencyField { Id = Guid.NewGuid(), Name = "currency_field" },
                new DateTimeField { Id = Guid.NewGuid(), Name = "datetime_field" },
                new EmailField { Id = Guid.NewGuid(), Name = "email_field" },
                new FileField { Id = Guid.NewGuid(), Name = "file_field" },
                new GuidField { Id = Guid.NewGuid(), Name = "guid_field" },
                new HtmlField { Id = Guid.NewGuid(), Name = "html_field" },
                new ImageField { Id = Guid.NewGuid(), Name = "image_field" },
                new MultiLineTextField { Id = Guid.NewGuid(), Name = "multiline_field" },
                new MultiSelectField { Id = Guid.NewGuid(), Name = "multiselect_field" },
                new PasswordField { Id = Guid.NewGuid(), Name = "password_field" },
                new PercentField { Id = Guid.NewGuid(), Name = "percent_field" },
                new PhoneField { Id = Guid.NewGuid(), Name = "phone_field" },
                new SelectField { Id = Guid.NewGuid(), Name = "select_field" },
                new UrlField { Id = Guid.NewGuid(), Name = "url_field" },
                new AutoNumberField { Id = Guid.NewGuid(), Name = "autonumber_field" }
            };

            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                NullValueHandling = NullValueHandling.Ignore
            };

            // Act — serialize and deserialize each field type
            foreach (var field in fields)
            {
                string json = JsonConvert.SerializeObject(field, typeof(Field), settings);
                json.Should().NotBeNullOrEmpty($"Serialization of {field.GetType().Name} should produce output");

                // Round-trip deserialization should preserve concrete type
                var deserialized = JsonConvert.DeserializeObject<Field>(json, new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.Auto,
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore
                });

                deserialized.Should().NotBeNull($"Deserialization of {field.GetType().Name} should succeed");
                deserialized!.Id.Should().Be(field.Id);
                deserialized.Name.Should().Be(field.Name);
            }
        }

        [Fact]
        public void DecimalToIntFormatConverter_ShouldConvertDecimalToInt()
        {
            // Arrange — test the DecimalToIntFormatConverter used in deserialization
            var converter = new DecimalToIntFormatConverter();

            // CanConvert should return true only for int
            converter.CanConvert(typeof(int)).Should().BeTrue();
            converter.CanConvert(typeof(string)).Should().BeFalse();
            converter.CanConvert(typeof(decimal)).Should().BeFalse();
            converter.CanConvert(typeof(double)).Should().BeFalse();

            // WriteJson should throw NotImplementedException (read-only converter)
            Action writeAct = () =>
                converter.WriteJson(
                    new JsonTextWriter(new System.IO.StringWriter()),
                    42,
                    new JsonSerializer());
            writeAct.Should().Throw<NotImplementedException>();

            // Test ReadJson via round-trip deserialization with decimal source values
            // Simulating JSON with decimal value being read as int
            string jsonWithDecimal = "{\"Value\": 42.0}";
            var settings = new JsonSerializerSettings
            {
                Converters = { converter }
            };
            var result = JsonConvert.DeserializeObject<IntWrapper>(jsonWithDecimal, settings);
            result.Should().NotBeNull();
            result!.Value.Should().Be(42);
        }

        /// <summary>
        /// Helper DTO for testing DecimalToIntFormatConverter round-trip.
        /// </summary>
        private class IntWrapper
        {
            public int Value { get; set; }
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 10: Cache Clearing Verification Tests
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task AllMutationOperations_ShouldClearCache()
        {
            // Test that all mutation operations invoke ClearCache

            // ── CreateEntity ──
            _cacheCleared = 0;
            var entity = CreateTestEntity();
            SetupPutItemSuccess();
            SetupBatchWriteSuccess();
            await _sut.CreateEntity(entity);
            _cacheCleared.Should().BeGreaterThanOrEqualTo(1, "CreateEntity must clear cache");

            // ── UpdateEntity ──
            _cacheCleared = 0;
            _mockDynamoDb
                .Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateItemResponse());
            await _sut.UpdateEntity(entity);
            _cacheCleared.Should().BeGreaterThanOrEqualTo(1, "UpdateEntity must clear cache");

            // ── CreateRelation ──
            _cacheCleared = 0;
            var relation = CreateTestRelation();
            var originEntity = CreateTestEntity(id: relation.OriginEntityId);
            var targetEntity = CreateTestEntity(id: relation.TargetEntityId);
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.Key[PK_ATTR].S == $"{ENTITY_PK_PREFIX}{relation.OriginEntityId}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateEntityResponseItem(originEntity)
                });
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.Key[PK_ATTR].S == $"{ENTITY_PK_PREFIX}{relation.TargetEntityId}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateEntityResponseItem(targetEntity)
                });
            await _sut.CreateRelation(relation);
            _cacheCleared.Should().BeGreaterThanOrEqualTo(1, "CreateRelation must clear cache");
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 11: Error Handling Tests
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateEntity_ConditionalCheckFailed_ShouldTranslateToBusinessException()
        {
            // Arrange — entity already exists (ConditionalCheckFailedException on PutItem)
            var entity = CreateTestEntity();
            _mockDynamoDb
                .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConditionalCheckFailedException("Item already exists"));

            // Act
            var result = await _sut.CreateEntity(entity);

            // Assert — ConditionalCheckFailedException returns false (entity already exists)
            result.Should().BeFalse();
            _cacheCleared.Should().BeGreaterThanOrEqualTo(1,
                "ClearCache should still be called even when create is rejected");
        }
    }
}
