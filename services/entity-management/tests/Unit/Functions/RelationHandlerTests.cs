using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using WebVellaErp.EntityManagement.Functions;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;
using Xunit;

namespace WebVellaErp.EntityManagement.Tests.Unit.Functions
{
    /// <summary>
    /// Comprehensive unit tests for RelationHandler — the Lambda handler for entity relation
    /// CRUD operations in the Entity Management microservice. Tests cover creation validation,
    /// immutability enforcement on update, relation-type specific constraints (OneToOne/OneToMany/
    /// ManyToMany), cache-aware reads with hash, admin permission gating, SNS event publishing,
    /// and response envelope patterns. All tests follow Arrange-Act-Assert with Moq mocking
    /// and FluentAssertions.
    /// </summary>
    public class RelationHandlerTests
    {
        // ─── Mocks ────────────────────────────────────────────────
        private readonly Mock<IEntityService> _mockEntityService;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<IMemoryCache> _mockCache;
        private readonly Mock<ILogger<RelationHandler>> _mockLogger;
        private readonly Mock<ILambdaContext> _mockContext;

        // ─── System Under Test ────────────────────────────────────
        private readonly RelationHandler _handler;

        // ─── Shared JSON deserialization options ───────────────────
        private readonly JsonSerializerOptions _jsonOptions;

        // ─── Stable Test Fixture IDs ──────────────────────────────
        private readonly Guid _originEntityId;
        private readonly Guid _targetEntityId;
        private readonly Guid _originFieldId;
        private readonly Guid _targetFieldId;

        /// <summary>
        /// Constructor initializes all mocks, sets required environment variables,
        /// creates the handler under test, and establishes stable fixture IDs.
        /// </summary>
        public RelationHandlerTests()
        {
            // Environment variables consumed by RelationHandler constructor
            Environment.SetEnvironmentVariable("RELATION_TOPIC_ARN",
                "arn:aws:sns:us-east-1:000000000000:entity-management-relations");
            Environment.SetEnvironmentVariable("IS_LOCAL", "true");

            _mockEntityService = new Mock<IEntityService>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockCache = new Mock<IMemoryCache>();
            _mockLogger = new Mock<ILogger<RelationHandler>>();

            _mockContext = new Mock<ILambdaContext>();
            _mockContext.Setup(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());

            // Default SNS setup — prevents NullReferenceException when handler awaits PublishAsync
            _mockSnsClient
                .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            _handler = new RelationHandler(
                _mockEntityService.Object,
                _mockSnsClient.Object,
                _mockCache.Object,
                _mockLogger.Object);

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            // Stable IDs reused across test arrangements
            _originEntityId = Guid.NewGuid();
            _targetEntityId = Guid.NewGuid();
            _originFieldId = Guid.NewGuid();
            _targetFieldId = Guid.NewGuid();
        }

        // ═══════════════════════════════════════════════════════════
        // HELPER METHODS
        // ═══════════════════════════════════════════════════════════

        /// <summary>Creates a test Entity populated with the specified fields.</summary>
        private Entity CreateTestEntity(Guid entityId, string name, params Field[] fields)
        {
            return new Entity
            {
                Id = entityId,
                Name = name,
                Label = name,
                Fields = new List<Field>(fields)
            };
        }

        /// <summary>Creates a GuidField with configurable Required/Unique constraints.</summary>
        private GuidField CreateGuidField(Guid fieldId, string name,
            bool required = true, bool unique = true)
        {
            return new GuidField
            {
                Id = fieldId,
                Name = name,
                Required = required,
                Unique = unique
            };
        }

        /// <summary>Creates a valid EntityRelation using the shared fixture IDs.</summary>
        private EntityRelation CreateValidRelation(
            EntityRelationType relationType = EntityRelationType.OneToMany,
            Guid? id = null)
        {
            return new EntityRelation
            {
                Id = id ?? Guid.NewGuid(),
                Name = "test_relation",
                Label = "Test Relation",
                RelationType = relationType,
                OriginEntityId = _originEntityId,
                OriginFieldId = _originFieldId,
                TargetEntityId = _targetEntityId,
                TargetFieldId = _targetFieldId
            };
        }

        /// <summary>
        /// Builds an APIGatewayHttpApiV2ProxyRequest with optional body, path/query params,
        /// and JWT claims that either grant or deny admin access.
        /// </summary>
        private APIGatewayHttpApiV2ProxyRequest BuildRequest(
            string? body = null,
            Dictionary<string, string>? pathParameters = null,
            Dictionary<string, string>? queryStringParameters = null,
            bool isAdmin = true)
        {
            var request = new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = pathParameters ?? new Dictionary<string, string>(),
                QueryStringParameters = queryStringParameters ?? new Dictionary<string, string>(),
                Headers = new Dictionary<string, string>
                {
                    { "x-correlation-id", Guid.NewGuid().ToString() }
                },
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = new Dictionary<string, string>()
                        }
                    }
                }
            };

            if (isAdmin)
            {
                request.RequestContext.Authorizer.Jwt.Claims["cognito:groups"] = "administrator";
            }
            else
            {
                request.RequestContext.Authorizer.Jwt.Claims["cognito:groups"] = "regular_user";
            }

            return request;
        }

        /// <summary>Serializes an object to camelCase JSON matching the handler's format.</summary>
        private string SerializeBody(object obj)
        {
            return System.Text.Json.JsonSerializer.Serialize(obj, _jsonOptions);
        }

        /// <summary>
        /// Sets up mock entities with GuidField fields for origin/target entity resolution
        /// during relation validation. Allows per-field Required/Unique control.
        /// </summary>
        private void SetupEntitiesForValidRelation(
            bool originRequired = true, bool originUnique = true,
            bool targetRequired = true, bool targetUnique = true)
        {
            var originField = CreateGuidField(_originFieldId, "origin_guid", originRequired, originUnique);
            var targetField = CreateGuidField(_targetFieldId, "target_guid", targetRequired, targetUnique);
            var originEntity = CreateTestEntity(_originEntityId, "origin_entity", originField);
            var targetEntity = CreateTestEntity(_targetEntityId, "target_entity", targetField);

            _mockEntityService.Setup(s => s.GetEntity(_originEntityId)).ReturnsAsync(originEntity);
            _mockEntityService.Setup(s => s.GetEntity(_targetEntityId)).ReturnsAsync(targetEntity);
        }

        /// <summary>
        /// Sets up mocks so no existing relations are found — no name conflicts,
        /// no Id collisions, and an empty relation list for duplicate checks.
        /// </summary>
        private void SetupNoExistingRelations()
        {
            _mockEntityService.Setup(s => s.ReadRelation(It.IsAny<string>()))
                .ReturnsAsync(new EntityRelationResponse { Object = null });
            _mockEntityService.Setup(s => s.ReadRelation(It.IsAny<Guid>()))
                .ReturnsAsync(new EntityRelationResponse { Object = null });
            _mockEntityService.Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse { Object = new List<EntityRelation>() });
        }

        /// <summary>Sets up mock for successful relation creation, echoing back the input.</summary>
        private void SetupSuccessfulCreate()
        {
            _mockEntityService.Setup(s => s.CreateRelation(It.IsAny<EntityRelation>()))
                .ReturnsAsync((EntityRelation r) => new EntityRelationResponse
                {
                    Success = true,
                    Object = r
                });
        }

        /// <summary>Sets up mock for successful relation update, echoing back the input.</summary>
        private void SetupSuccessfulUpdate()
        {
            _mockEntityService.Setup(s => s.UpdateRelation(It.IsAny<EntityRelation>()))
                .ReturnsAsync((EntityRelation r) => new EntityRelationResponse
                {
                    Success = true,
                    Object = r
                });
        }

        /// <summary>Sets up mock for successful relation deletion.</summary>
        private void SetupSuccessfulDelete()
        {
            _mockEntityService.Setup(s => s.DeleteRelation(It.IsAny<Guid>()))
                .ReturnsAsync(new EntityRelationResponse { Success = true });
        }

        /// <summary>
        /// Sets up an existing relation in the mock service for update/delete test scenarios.
        /// Configures both ReadRelation(Guid) and ReadRelation(string) lookups.
        /// </summary>
        private void SetupExistingRelationForUpdate(EntityRelation existingRelation)
        {
            _mockEntityService.Setup(s => s.ReadRelation(existingRelation.Id))
                .ReturnsAsync(new EntityRelationResponse { Object = existingRelation });
            _mockEntityService.Setup(s => s.ReadRelation(existingRelation.Name))
                .ReturnsAsync(new EntityRelationResponse { Object = existingRelation });
        }

        /// <summary>
        /// Concrete non-GuidField subclass used to test the field type constraint validation.
        /// Returns FieldType.TextField from GetFieldType() to trigger the "must be a Guid field" error.
        /// </summary>
        private class NonGuidTestField : Field
        {
            public override FieldType GetFieldType() => FieldType.TextField;
        }

        // ═══════════════════════════════════════════════════════════
        // CREATE RELATION TESTS
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRelation_ValidOneToMany_Returns200()
        {
            // Arrange
            SetupEntitiesForValidRelation();
            SetupNoExistingRelations();
            SetupSuccessfulCreate();

            var relation = CreateValidRelation(EntityRelationType.OneToMany);
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — handler returns 201 Created for successful creation
            response.StatusCode.Should().Be(201);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
            body.Object.Should().NotBeNull();
            body.Message.Should().Contain("successfully created");
            _mockEntityService.Verify(s => s.CreateRelation(It.IsAny<EntityRelation>()), Times.Once);
        }

        [Fact]
        public async Task CreateRelation_EmptyId_GeneratesNewGuid()
        {
            // Arrange
            SetupEntitiesForValidRelation();
            SetupNoExistingRelations();
            SetupSuccessfulCreate();

            var relation = CreateValidRelation();
            relation.Id = Guid.Empty; // Handler must auto-generate
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — handler auto-generates a non-empty GUID when Id is Guid.Empty
            response.StatusCode.Should().Be(201);
            _mockEntityService.Verify(s => s.CreateRelation(
                It.Is<EntityRelation>(r => r.Id != Guid.Empty)), Times.Once);
        }

        [Fact]
        public async Task CreateRelation_DuplicateName_Returns400()
        {
            // Arrange — existing relation with same name
            SetupEntitiesForValidRelation();
            var existingRelation = CreateValidRelation();
            existingRelation.Id = Guid.NewGuid();
            existingRelation.Name = "test_relation";

            _mockEntityService.Setup(s => s.ReadRelation("test_relation"))
                .ReturnsAsync(new EntityRelationResponse { Object = existingRelation });
            _mockEntityService.Setup(s => s.ReadRelation(It.IsAny<Guid>()))
                .ReturnsAsync(new EntityRelationResponse { Object = null });
            _mockEntityService.Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse { Object = new List<EntityRelation>() });

            var relation = CreateValidRelation();
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Errors.Should().NotBeEmpty();
            body.Errors.Should().Contain(e => e.Key == "name"
                && e.Message!.Contains("already exists"));
        }

        [Fact]
        public async Task CreateRelation_NameTooLong_Returns400()
        {
            // Arrange — name exceeds 63-character PostgreSQL identifier width limit
            SetupEntitiesForValidRelation();
            SetupNoExistingRelations();

            var relation = CreateValidRelation();
            relation.Name = new string('a', 64);
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Errors.Should().Contain(e => e.Key == "name"
                && e.Message!.Contains("63"));
        }

        [Fact]
        public async Task CreateRelation_AdminRequired_Returns403ForNonAdmin()
        {
            // Arrange — JWT claims without administrator group
            var relation = CreateValidRelation();
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: false);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            var body = System.Text.Json.JsonSerializer.Deserialize<BaseResponseModel>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Message.Should().Contain("Access denied");
        }

        // ═══════════════════════════════════════════════════════════
        // RELATION-TYPE SPECIFIC VALIDATION TESTS
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRelation_OneToOne_BothFieldsMustBeRequiredAndUnique()
        {
            // Arrange — origin field not Required for OneToOne → validation failure
            SetupEntitiesForValidRelation(originRequired: false, originUnique: true);
            SetupNoExistingRelations();

            var relation = CreateValidRelation(EntityRelationType.OneToOne);
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — "For One-to-One relation, the origin field must be Required and Unique."
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Errors.Should().Contain(e =>
                e.Key == "originFieldId"
                && e.Message!.Contains("Required")
                && e.Message!.Contains("Unique"));
        }

        [Fact]
        public async Task CreateRelation_ManyToMany_BothFieldsMustBeRequiredAndUnique()
        {
            // Arrange — target field not Unique for ManyToMany → validation failure
            SetupEntitiesForValidRelation(
                originRequired: true, originUnique: true,
                targetRequired: true, targetUnique: false);
            SetupNoExistingRelations();

            var relation = CreateValidRelation(EntityRelationType.ManyToMany);
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — "For Many-to-Many relation, the target field must be Required and Unique."
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Errors.Should().Contain(e =>
                e.Key == "targetFieldId"
                && e.Message!.Contains("Required")
                && e.Message!.Contains("Unique"));
        }

        [Fact]
        public async Task CreateRelation_OneToMany_OriginFieldMustBeRequiredAndUnique()
        {
            // Arrange — origin field not Required for OneToMany → validation failure
            SetupEntitiesForValidRelation(originRequired: false, originUnique: true);
            SetupNoExistingRelations();

            var relation = CreateValidRelation(EntityRelationType.OneToMany);
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — "For One-to-Many relation, the origin field must be Required and Unique."
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Errors.Should().Contain(e =>
                e.Key == "originFieldId"
                && e.Message!.Contains("origin field"));
        }

        [Fact]
        public async Task CreateRelation_OriginFieldNotGuidType_Returns400()
        {
            // Arrange — origin field is a TextField, not a GuidField
            var nonGuidField = new NonGuidTestField
            {
                Id = _originFieldId,
                Name = "origin_text",
                Required = true,
                Unique = true
            };
            var targetField = CreateGuidField(_targetFieldId, "target_guid");
            var originEntity = CreateTestEntity(_originEntityId, "origin_entity", nonGuidField);
            var targetEntity = CreateTestEntity(_targetEntityId, "target_entity", targetField);

            _mockEntityService.Setup(s => s.GetEntity(_originEntityId)).ReturnsAsync(originEntity);
            _mockEntityService.Setup(s => s.GetEntity(_targetEntityId)).ReturnsAsync(targetEntity);
            SetupNoExistingRelations();

            var relation = CreateValidRelation();
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — "The origin field must be a Guid field."
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Errors.Should().Contain(e =>
                e.Key == "originFieldId"
                && e.Message!.Contains("Guid field"));
        }

        [Fact]
        public async Task CreateRelation_TargetFieldNotGuidType_Returns400()
        {
            // Arrange — target field is a TextField, not a GuidField
            var originField = CreateGuidField(_originFieldId, "origin_guid");
            var nonGuidField = new NonGuidTestField
            {
                Id = _targetFieldId,
                Name = "target_text",
                Required = true,
                Unique = true
            };
            var originEntity = CreateTestEntity(_originEntityId, "origin_entity", originField);
            var targetEntity = CreateTestEntity(_targetEntityId, "target_entity", nonGuidField);

            _mockEntityService.Setup(s => s.GetEntity(_originEntityId)).ReturnsAsync(originEntity);
            _mockEntityService.Setup(s => s.GetEntity(_targetEntityId)).ReturnsAsync(targetEntity);
            SetupNoExistingRelations();

            var relation = CreateValidRelation();
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — "The target field must be a Guid field."
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Errors.Should().Contain(e =>
                e.Key == "targetFieldId"
                && e.Message!.Contains("Guid field"));
        }

        [Fact]
        public async Task CreateRelation_SameOriginTargetFieldOnSameEntity_RejectsForOneToMany()
        {
            // Arrange — same entity AND same field for both origin and target
            var sharedFieldId = Guid.NewGuid();
            var sharedEntityId = Guid.NewGuid();
            var guidField = CreateGuidField(sharedFieldId, "shared_guid");
            var entity = CreateTestEntity(sharedEntityId, "shared_entity", guidField);

            _mockEntityService.Setup(s => s.GetEntity(sharedEntityId)).ReturnsAsync(entity);
            SetupNoExistingRelations();

            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "self_relation",
                Label = "Self Relation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = sharedEntityId,
                OriginFieldId = sharedFieldId,
                TargetEntityId = sharedEntityId,
                TargetFieldId = sharedFieldId
            };
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — "The origin and target fields cannot be the same…"
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Errors.Should().Contain(e => e.Message!.Contains("cannot be the same"));
        }

        [Fact]
        public async Task CreateRelation_DuplicateOriginTargetPairing_Rejected()
        {
            // Arrange — an existing relation has the same origin+target entity/field combo
            SetupEntitiesForValidRelation();

            var existingRelation = CreateValidRelation();
            existingRelation.Id = Guid.NewGuid();
            existingRelation.Name = "existing_relation";

            // No name conflict (different names)
            _mockEntityService.Setup(s => s.ReadRelation(It.IsAny<string>()))
                .ReturnsAsync(new EntityRelationResponse { Object = null });
            _mockEntityService.Setup(s => s.ReadRelation(It.IsAny<Guid>()))
                .ReturnsAsync(new EntityRelationResponse { Object = null });
            // Existing relations list includes one with the same entity/field pairing
            _mockEntityService.Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse
                {
                    Object = new List<EntityRelation> { existingRelation }
                });

            var newRelation = CreateValidRelation();
            newRelation.Name = "new_relation";
            var request = BuildRequest(body: SerializeBody(newRelation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — "A relation with the same origin and target entity/field combination already exists."
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Errors.Should().Contain(e =>
                e.Message!.Contains("same origin and target"));
        }

        [Fact]
        public async Task CreateRelation_OriginEntityNotFound_Returns400()
        {
            // Arrange — origin entity does not exist
            _mockEntityService.Setup(s => s.GetEntity(_originEntityId))
                .ReturnsAsync((Entity?)null);
            var targetField = CreateGuidField(_targetFieldId, "target_guid");
            var targetEntity = CreateTestEntity(_targetEntityId, "target_entity", targetField);
            _mockEntityService.Setup(s => s.GetEntity(_targetEntityId)).ReturnsAsync(targetEntity);
            SetupNoExistingRelations();

            var relation = CreateValidRelation();
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — "The origin entity was not found."
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Errors.Should().Contain(e =>
                e.Key == "originEntityId" && e.Message!.Contains("not found"));
        }

        [Fact]
        public async Task CreateRelation_TargetEntityNotFound_Returns400()
        {
            // Arrange — target entity does not exist
            var originField = CreateGuidField(_originFieldId, "origin_guid");
            var originEntity = CreateTestEntity(_originEntityId, "origin_entity", originField);
            _mockEntityService.Setup(s => s.GetEntity(_originEntityId)).ReturnsAsync(originEntity);
            _mockEntityService.Setup(s => s.GetEntity(_targetEntityId))
                .ReturnsAsync((Entity?)null);
            SetupNoExistingRelations();

            var relation = CreateValidRelation();
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — "The target entity was not found."
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Errors.Should().Contain(e =>
                e.Key == "targetEntityId" && e.Message!.Contains("not found"));
        }

        // ═══════════════════════════════════════════════════════════
        // READ RELATION TESTS
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task ReadRelation_ById_ReturnsCachedRelation()
        {
            // Arrange
            var relationId = Guid.NewGuid();
            var relation = CreateValidRelation(id: relationId);

            _mockEntityService.Setup(s => s.ReadRelation(relationId))
                .ReturnsAsync(new EntityRelationResponse { Success = true, Object = relation });

            var request = BuildRequest(
                pathParameters: new Dictionary<string, string>
                    { { "idOrName", relationId.ToString() } });

            // Act
            var response = await _handler.ReadRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
            body.Object.Should().NotBeNull();
            body.Object!.Id.Should().Be(relationId);
            _mockEntityService.Verify(s => s.ReadRelation(relationId), Times.Once);
        }

        [Fact]
        public async Task ReadRelation_ByName_ReturnsRelation()
        {
            // Arrange
            var relation = CreateValidRelation();
            relation.Name = "my_test_relation";

            _mockEntityService.Setup(s => s.ReadRelation("my_test_relation"))
                .ReturnsAsync(new EntityRelationResponse { Success = true, Object = relation });

            var request = BuildRequest(
                pathParameters: new Dictionary<string, string>
                    { { "idOrName", "my_test_relation" } });

            // Act
            var response = await _handler.ReadRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Object.Should().NotBeNull();
            body.Object!.Name.Should().Be("my_test_relation");
            _mockEntityService.Verify(s => s.ReadRelation("my_test_relation"), Times.Once);
        }

        [Fact]
        public async Task ReadRelation_NotFound_Returns404()
        {
            // Arrange
            var missingId = Guid.NewGuid();
            _mockEntityService.Setup(s => s.ReadRelation(missingId))
                .ReturnsAsync(new EntityRelationResponse { Object = null });

            var request = BuildRequest(
                pathParameters: new Dictionary<string, string>
                    { { "idOrName", missingId.ToString() } });

            // Act
            var response = await _handler.ReadRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(404);
            var body = System.Text.Json.JsonSerializer.Deserialize<BaseResponseModel>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Message.Should().Contain("not found");
        }

        [Fact]
        public async Task ReadRelation_EnrichedWithEntityAndFieldNames()
        {
            // Arrange — EntityService returns relation with populated name properties
            var relationId = Guid.NewGuid();
            var relation = CreateValidRelation(id: relationId);
            relation.OriginEntityName = "account";
            relation.TargetEntityName = "contact";
            relation.OriginFieldName = "account_id";
            relation.TargetFieldName = "contact_id";

            _mockEntityService.Setup(s => s.ReadRelation(relationId))
                .ReturnsAsync(new EntityRelationResponse { Success = true, Object = relation });

            var request = BuildRequest(
                pathParameters: new Dictionary<string, string>
                    { { "idOrName", relationId.ToString() } });

            // Act
            var response = await _handler.ReadRelation(request, _mockContext.Object);

            // Assert — response body includes enriched entity/field names
            response.StatusCode.Should().Be(200);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Object.Should().NotBeNull();
            body.Object!.OriginEntityName.Should().Be("account");
            body.Object.TargetEntityName.Should().Be("contact");
            body.Object.OriginFieldName.Should().Be("account_id");
            body.Object.TargetFieldName.Should().Be("contact_id");
        }

        // ═══════════════════════════════════════════════════════════
        // READ RELATIONS (LIST ALL) TESTS
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task ReadRelations_ReturnsCachedList()
        {
            // Arrange
            var relations = new List<EntityRelation>
            {
                CreateValidRelation(id: Guid.NewGuid()),
                CreateValidRelation(id: Guid.NewGuid())
            };
            relations[0].Name = "relation_one";
            relations[1].Name = "relation_two";

            _mockEntityService.Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse { Object = relations });
            _mockEntityService.Setup(s => s.GetRelationsHash()).Returns("cached-hash");

            var request = BuildRequest();

            // Act
            var response = await _handler.ReadRelations(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationListResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
            body.Object.Should().NotBeNull();
            body.Object.Should().HaveCount(2);
        }

        [Fact]
        public async Task ReadRelations_CacheMiss_QueriesDynamoDB()
        {
            // Arrange — simulates cache-miss scenario at the service layer;
            // handler always calls ReadRelations() and sets hash from GetRelationsHash()
            var relations = new List<EntityRelation>
            {
                CreateValidRelation(id: Guid.NewGuid())
            };
            relations[0].Name = "queried_relation";

            _mockEntityService.Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse { Object = relations });
            _mockEntityService.Setup(s => s.GetRelationsHash()).Returns("new-hash");

            var request = BuildRequest();

            // Act
            var response = await _handler.ReadRelations(request, _mockContext.Object);

            // Assert — verify ReadRelations was called and hash is set in response
            response.StatusCode.Should().Be(200);
            _mockEntityService.Verify(s => s.ReadRelations(), Times.Once);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationListResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Hash.Should().Be("new-hash");
        }

        [Fact]
        public async Task ReadRelations_HashParameter_MatchingHash_ReturnsNullObject()
        {
            // Arrange — client sends hash that matches current → 304-like optimization
            _mockEntityService.Setup(s => s.GetRelationsHash()).Returns("matching-hash");

            var request = BuildRequest(
                queryStringParameters: new Dictionary<string, string>
                    { { "hash", "matching-hash" } });

            // Act
            var response = await _handler.ReadRelations(request, _mockContext.Object);

            // Assert — null Object with "Hash match" message, ReadRelations NOT called
            response.StatusCode.Should().Be(200);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationListResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
            body.Message.Should().Contain("Hash match");
            // Handler sets Object = null!, but EntityRelationListResponse.Object is non-nullable
            // List<EntityRelation> with default initializer, so STJ deserialization restores
            // empty list.  Semantic intent: no relation data is returned.
            if (body.Object is not null)
            {
                body.Object.Should().BeEmpty("hash-match optimization must not return relation data");
            }
            _mockEntityService.Verify(s => s.ReadRelations(), Times.Never);
        }

        [Fact]
        public async Task ReadRelations_HashParameter_DifferentHash_ReturnsFullList()
        {
            // Arrange — client hash differs from current → full fetch required
            _mockEntityService.Setup(s => s.GetRelationsHash()).Returns("current-hash");

            var relations = new List<EntityRelation>
            {
                CreateValidRelation(id: Guid.NewGuid())
            };
            _mockEntityService.Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse { Object = relations });

            var request = BuildRequest(
                queryStringParameters: new Dictionary<string, string>
                    { { "hash", "stale-client-hash" } });

            // Act
            var response = await _handler.ReadRelations(request, _mockContext.Object);

            // Assert — full list returned, ReadRelations called
            response.StatusCode.Should().Be(200);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationListResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Object.Should().NotBeNull();
            body.Object.Should().HaveCount(1);
            _mockEntityService.Verify(s => s.ReadRelations(), Times.Once);
        }

        // ═══════════════════════════════════════════════════════════
        // UPDATE RELATION — IMMUTABILITY ENFORCEMENT TESTS
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task UpdateRelation_ChangeRelationType_Returns400Immutable()
        {
            // Arrange — attempt to change RelationType from OneToMany to ManyToMany
            var existingRelation = CreateValidRelation(EntityRelationType.OneToMany);
            SetupExistingRelationForUpdate(existingRelation);

            var updatedRelation = CreateValidRelation(EntityRelationType.ManyToMany);
            updatedRelation.Id = existingRelation.Id;
            updatedRelation.Name = existingRelation.Name;

            var request = BuildRequest(
                body: SerializeBody(updatedRelation),
                pathParameters: new Dictionary<string, string>
                    { { "id", existingRelation.Id.ToString() } },
                isAdmin: true);

            // Act
            var response = await _handler.UpdateRelation(request, _mockContext.Object);

            // Assert — "RelationType is immutable and cannot be changed after creation."
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Errors.Should().Contain(e =>
                e.Key == "relationType" && e.Message!.Contains("immutable"));
        }

        [Fact]
        public async Task UpdateRelation_ChangeOriginEntityId_Returns400Immutable()
        {
            // Arrange
            var existingRelation = CreateValidRelation();
            SetupExistingRelationForUpdate(existingRelation);

            var updatedRelation = CreateValidRelation();
            updatedRelation.Id = existingRelation.Id;
            updatedRelation.Name = existingRelation.Name;
            updatedRelation.OriginEntityId = Guid.NewGuid(); // Changed — immutable!

            var request = BuildRequest(
                body: SerializeBody(updatedRelation),
                pathParameters: new Dictionary<string, string>
                    { { "id", existingRelation.Id.ToString() } },
                isAdmin: true);

            // Act
            var response = await _handler.UpdateRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body!.Errors.Should().Contain(e =>
                e.Key == "originEntityId" && e.Message!.Contains("immutable"));
        }

        [Fact]
        public async Task UpdateRelation_ChangeOriginFieldId_Returns400Immutable()
        {
            // Arrange
            var existingRelation = CreateValidRelation();
            SetupExistingRelationForUpdate(existingRelation);

            var updatedRelation = CreateValidRelation();
            updatedRelation.Id = existingRelation.Id;
            updatedRelation.Name = existingRelation.Name;
            updatedRelation.OriginFieldId = Guid.NewGuid(); // Changed — immutable!

            var request = BuildRequest(
                body: SerializeBody(updatedRelation),
                pathParameters: new Dictionary<string, string>
                    { { "id", existingRelation.Id.ToString() } },
                isAdmin: true);

            // Act
            var response = await _handler.UpdateRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body!.Errors.Should().Contain(e =>
                e.Key == "originFieldId" && e.Message!.Contains("immutable"));
        }

        [Fact]
        public async Task UpdateRelation_ChangeTargetEntityId_Returns400Immutable()
        {
            // Arrange
            var existingRelation = CreateValidRelation();
            SetupExistingRelationForUpdate(existingRelation);

            var updatedRelation = CreateValidRelation();
            updatedRelation.Id = existingRelation.Id;
            updatedRelation.Name = existingRelation.Name;
            updatedRelation.TargetEntityId = Guid.NewGuid(); // Changed — immutable!

            var request = BuildRequest(
                body: SerializeBody(updatedRelation),
                pathParameters: new Dictionary<string, string>
                    { { "id", existingRelation.Id.ToString() } },
                isAdmin: true);

            // Act
            var response = await _handler.UpdateRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body!.Errors.Should().Contain(e =>
                e.Key == "targetEntityId" && e.Message!.Contains("immutable"));
        }

        [Fact]
        public async Task UpdateRelation_ChangeTargetFieldId_Returns400Immutable()
        {
            // Arrange
            var existingRelation = CreateValidRelation();
            SetupExistingRelationForUpdate(existingRelation);

            var updatedRelation = CreateValidRelation();
            updatedRelation.Id = existingRelation.Id;
            updatedRelation.Name = existingRelation.Name;
            updatedRelation.TargetFieldId = Guid.NewGuid(); // Changed — immutable!

            var request = BuildRequest(
                body: SerializeBody(updatedRelation),
                pathParameters: new Dictionary<string, string>
                    { { "id", existingRelation.Id.ToString() } },
                isAdmin: true);

            // Act
            var response = await _handler.UpdateRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body!.Errors.Should().Contain(e =>
                e.Key == "targetFieldId" && e.Message!.Contains("immutable"));
        }

        [Fact]
        public async Task UpdateRelation_ValidNameLabelChange_Returns200()
        {
            // Arrange — change only Name and Label (allowed mutable properties)
            var existingRelation = CreateValidRelation();
            SetupExistingRelationForUpdate(existingRelation);
            SetupSuccessfulUpdate();

            // No name conflict for the new name
            _mockEntityService.Setup(s => s.ReadRelation("updated_name"))
                .ReturnsAsync(new EntityRelationResponse { Object = null });

            var updatedRelation = new EntityRelation
            {
                Id = existingRelation.Id,
                Name = "updated_name",
                Label = "Updated Label",
                RelationType = existingRelation.RelationType,
                OriginEntityId = existingRelation.OriginEntityId,
                OriginFieldId = existingRelation.OriginFieldId,
                TargetEntityId = existingRelation.TargetEntityId,
                TargetFieldId = existingRelation.TargetFieldId
            };

            var request = BuildRequest(
                body: SerializeBody(updatedRelation),
                pathParameters: new Dictionary<string, string>
                    { { "id", existingRelation.Id.ToString() } },
                isAdmin: true);

            // Act
            var response = await _handler.UpdateRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
            body.Message.Should().Contain("successfully updated");
            _mockEntityService.Verify(s => s.UpdateRelation(It.IsAny<EntityRelation>()), Times.Once);
        }

        [Fact]
        public async Task UpdateRelation_NameUniqueness_ExcludesSelf()
        {
            // Arrange — keeping the same name on update (self-match is NOT a conflict)
            var existingRelation = CreateValidRelation();
            SetupExistingRelationForUpdate(existingRelation);
            SetupSuccessfulUpdate();

            var updatedRelation = new EntityRelation
            {
                Id = existingRelation.Id,
                Name = existingRelation.Name, // Same name — self-match, allowed
                Label = "Label Changed Only",
                RelationType = existingRelation.RelationType,
                OriginEntityId = existingRelation.OriginEntityId,
                OriginFieldId = existingRelation.OriginFieldId,
                TargetEntityId = existingRelation.TargetEntityId,
                TargetFieldId = existingRelation.TargetFieldId
            };

            var request = BuildRequest(
                body: SerializeBody(updatedRelation),
                pathParameters: new Dictionary<string, string>
                    { { "id", existingRelation.Id.ToString() } },
                isAdmin: true);

            // Act
            var response = await _handler.UpdateRelation(request, _mockContext.Object);

            // Assert — should succeed because name uniqueness excludes self
            response.StatusCode.Should().Be(200);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
        }

        // ═══════════════════════════════════════════════════════════
        // DELETE RELATION TESTS
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task DeleteRelation_ValidId_Returns200()
        {
            // Arrange
            var relationId = Guid.NewGuid();
            var existingRelation = CreateValidRelation(id: relationId);

            _mockEntityService.Setup(s => s.ReadRelation(relationId))
                .ReturnsAsync(new EntityRelationResponse { Object = existingRelation });
            SetupSuccessfulDelete();

            var request = BuildRequest(
                pathParameters: new Dictionary<string, string>
                    { { "id", relationId.ToString() } },
                isAdmin: true);

            // Act
            var response = await _handler.DeleteRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
            body.Message.Should().Contain("successfully deleted");
            _mockEntityService.Verify(s => s.DeleteRelation(relationId), Times.Once);
        }

        [Fact]
        public async Task DeleteRelation_NotFound_Returns404()
        {
            // Arrange
            var missingId = Guid.NewGuid();
            _mockEntityService.Setup(s => s.ReadRelation(missingId))
                .ReturnsAsync(new EntityRelationResponse { Object = null });

            var request = BuildRequest(
                pathParameters: new Dictionary<string, string>
                    { { "id", missingId.ToString() } },
                isAdmin: true);

            // Act
            var response = await _handler.DeleteRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(404);
            var body = System.Text.Json.JsonSerializer.Deserialize<BaseResponseModel>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Message.Should().Contain("not found");
        }

        [Fact]
        public async Task DeleteRelation_AdminRequired_Returns403()
        {
            // Arrange — non-admin user attempting delete
            var request = BuildRequest(
                pathParameters: new Dictionary<string, string>
                    { { "id", Guid.NewGuid().ToString() } },
                isAdmin: false);

            // Act
            var response = await _handler.DeleteRelation(request, _mockContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            var body = System.Text.Json.JsonSerializer.Deserialize<BaseResponseModel>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Message.Should().Contain("Access denied");
        }

        // ═══════════════════════════════════════════════════════════
        // CACHE BEHAVIOR TESTS
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRelation_ClearsCache()
        {
            // Arrange
            SetupEntitiesForValidRelation();
            SetupNoExistingRelations();
            SetupSuccessfulCreate();

            var relation = CreateValidRelation();
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — ClearCache must be called after successful creation
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        [Fact]
        public async Task UpdateRelation_ClearsCache()
        {
            // Arrange
            var existingRelation = CreateValidRelation();
            SetupExistingRelationForUpdate(existingRelation);
            SetupSuccessfulUpdate();

            var updatedRelation = new EntityRelation
            {
                Id = existingRelation.Id,
                Name = existingRelation.Name,
                Label = "Updated for cache test",
                RelationType = existingRelation.RelationType,
                OriginEntityId = existingRelation.OriginEntityId,
                OriginFieldId = existingRelation.OriginFieldId,
                TargetEntityId = existingRelation.TargetEntityId,
                TargetFieldId = existingRelation.TargetFieldId
            };

            var request = BuildRequest(
                body: SerializeBody(updatedRelation),
                pathParameters: new Dictionary<string, string>
                    { { "id", existingRelation.Id.ToString() } },
                isAdmin: true);

            // Act
            await _handler.UpdateRelation(request, _mockContext.Object);

            // Assert
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        [Fact]
        public async Task DeleteRelation_ClearsCache()
        {
            // Arrange
            var relationId = Guid.NewGuid();
            var existingRelation = CreateValidRelation(id: relationId);

            _mockEntityService.Setup(s => s.ReadRelation(relationId))
                .ReturnsAsync(new EntityRelationResponse { Object = existingRelation });
            SetupSuccessfulDelete();

            var request = BuildRequest(
                pathParameters: new Dictionary<string, string>
                    { { "id", relationId.ToString() } },
                isAdmin: true);

            // Act
            await _handler.DeleteRelation(request, _mockContext.Object);

            // Assert
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        // ═══════════════════════════════════════════════════════════
        // SNS EVENT TESTS
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRelation_PublishesSnsEvent()
        {
            // Arrange
            SetupEntitiesForValidRelation();
            SetupNoExistingRelations();
            SetupSuccessfulCreate();

            var relation = CreateValidRelation();
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — SNS event published with "entity-management.relation.created"
            _mockSnsClient.Verify(s => s.PublishAsync(
                It.Is<PublishRequest>(p =>
                    p.MessageAttributes.ContainsKey("eventType") &&
                    p.MessageAttributes["eventType"].StringValue ==
                        "entity-management.relation.created" &&
                    p.MessageAttributes.ContainsKey("source") &&
                    p.MessageAttributes["source"].StringValue == "entity-management"),
                It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task UpdateRelation_PublishesSnsEvent()
        {
            // Arrange
            var existingRelation = CreateValidRelation();
            SetupExistingRelationForUpdate(existingRelation);
            SetupSuccessfulUpdate();

            var updatedRelation = new EntityRelation
            {
                Id = existingRelation.Id,
                Name = existingRelation.Name,
                Label = "Updated for SNS test",
                RelationType = existingRelation.RelationType,
                OriginEntityId = existingRelation.OriginEntityId,
                OriginFieldId = existingRelation.OriginFieldId,
                TargetEntityId = existingRelation.TargetEntityId,
                TargetFieldId = existingRelation.TargetFieldId
            };

            var request = BuildRequest(
                body: SerializeBody(updatedRelation),
                pathParameters: new Dictionary<string, string>
                    { { "id", existingRelation.Id.ToString() } },
                isAdmin: true);

            // Act
            await _handler.UpdateRelation(request, _mockContext.Object);

            // Assert — SNS event published with "entity-management.relation.updated"
            _mockSnsClient.Verify(s => s.PublishAsync(
                It.Is<PublishRequest>(p =>
                    p.MessageAttributes.ContainsKey("eventType") &&
                    p.MessageAttributes["eventType"].StringValue ==
                        "entity-management.relation.updated"),
                It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task DeleteRelation_PublishesSnsEvent()
        {
            // Arrange
            var relationId = Guid.NewGuid();
            var existingRelation = CreateValidRelation(id: relationId);

            _mockEntityService.Setup(s => s.ReadRelation(relationId))
                .ReturnsAsync(new EntityRelationResponse { Object = existingRelation });
            SetupSuccessfulDelete();

            var request = BuildRequest(
                pathParameters: new Dictionary<string, string>
                    { { "id", relationId.ToString() } },
                isAdmin: true);

            // Act
            await _handler.DeleteRelation(request, _mockContext.Object);

            // Assert — SNS event published with "entity-management.relation.deleted"
            _mockSnsClient.Verify(s => s.PublishAsync(
                It.Is<PublishRequest>(p =>
                    p.MessageAttributes.ContainsKey("eventType") &&
                    p.MessageAttributes["eventType"].StringValue ==
                        "entity-management.relation.deleted"),
                It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ═══════════════════════════════════════════════════════════
        // RESPONSE ENVELOPE PATTERN TESTS
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRelation_ResponseMatchesEnvelope()
        {
            // Arrange
            SetupEntitiesForValidRelation();
            SetupNoExistingRelations();
            SetupSuccessfulCreate();

            var relation = CreateValidRelation();
            var request = BuildRequest(body: SerializeBody(relation), isAdmin: true);

            // Act
            var response = await _handler.CreateRelation(request, _mockContext.Object);

            // Assert — full EntityRelationResponse envelope structure
            response.StatusCode.Should().Be(201);
            response.Headers.Should().ContainKey("Content-Type");
            response.Headers["Content-Type"].Should().Be("application/json");
            response.Headers.Should().ContainKey("X-Correlation-Id");

            var body = System.Text.Json.JsonSerializer.Deserialize<EntityRelationResponse>(
                response.Body, _jsonOptions);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
            body.Timestamp.Should().NotBe(default);
            body.Message.Should().NotBeNullOrEmpty();
            body.Object.Should().NotBeNull();
            // Errors should be empty/null on success (WhenWritingNull skips null lists)
            if (body.Errors != null)
            {
                body.Errors.Should().BeEmpty();
            }
        }
    }
}
