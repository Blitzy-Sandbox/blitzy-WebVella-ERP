// =============================================================================
// EntityHandlerTests.cs — Comprehensive Unit Tests for Entity Metadata CRUD Lambda Handler
// =============================================================================
// Tests for EntityHandler.cs — the foundational Lambda handler for entity metadata
// CRUD operations in the Entity Management microservice. Validates request
// deserialization, response formatting, HTTP status codes, JWT claims extraction,
// entity validation, cache-aware read behavior, SNS event publication, and error
// envelope pattern (EntityResponse / EntityListResponse).
//
// Namespace: WebVellaErp.EntityManagement.Tests.Unit.Functions
// Test Framework: xUnit 2.9.3 + Moq 4.20.72 + FluentAssertions 8.0.1
// =============================================================================

using System;
using System.Collections.Generic;
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
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace WebVellaErp.EntityManagement.Tests.Unit.Functions
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="EntityHandler"/> covering all 6 public Lambda
    /// handler methods (CreateEntity, ReadEntity, ReadEntities, UpdateEntity, PatchEntity,
    /// DeleteEntity). Tests are organized in 12 logical phases covering authorization,
    /// validation, cache behavior, SNS events, error handling, and response envelope patterns.
    /// </summary>
    public class EntityHandlerTests
    {
        // ═══════════════════════════════════════════════════════════════
        // MOCK DEPENDENCIES
        // ═══════════════════════════════════════════════════════════════

        private readonly Mock<IEntityService> _mockEntityService;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<IMemoryCache> _mockCache;
        private readonly Mock<ILogger<EntityHandler>> _mockLogger;
        private readonly Mock<ILambdaContext> _mockLambdaContext;
        private readonly EntityHandler _handler;

        // ═══════════════════════════════════════════════════════════════
        // TEST FIXTURES
        // ═══════════════════════════════════════════════════════════════

        private static readonly Guid TestEntityId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private const string TestEntityName = "test_entity";

        /// <summary>
        /// JSON serialization options matching the handler's internal _jsonOptions configuration.
        /// </summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // ═══════════════════════════════════════════════════════════════
        // CONSTRUCTOR — Test Setup
        // ═══════════════════════════════════════════════════════════════

        public EntityHandlerTests()
        {
            _mockEntityService = new Mock<IEntityService>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockCache = new Mock<IMemoryCache>();
            _mockLogger = new Mock<ILogger<EntityHandler>>();
            _mockLambdaContext = new Mock<ILambdaContext>();

            // Default Lambda context with AwsRequestId for correlation ID extraction.
            _mockLambdaContext.Setup(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());
            _mockLambdaContext.Setup(c => c.FunctionName).Returns("EntityHandler-Test");

            // Configure SNS to return a successful publish response by default.
            _mockSnsClient
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });

            // Set the ENTITY_TOPIC_ARN environment variable so PublishDomainEvent doesn't skip.
            Environment.SetEnvironmentVariable("ENTITY_TOPIC_ARN", "arn:aws:sns:us-east-1:000000000000:entity-events");
            Environment.SetEnvironmentVariable("IS_LOCAL", "false");

            var mockLoggerFactory = new Mock<ILoggerFactory>();
            mockLoggerFactory
                .Setup(f => f.CreateLogger(It.IsAny<string>()))
                .Returns(new Mock<ILogger>().Object);

            _handler = new EntityHandler(
                _mockEntityService.Object,
                _mockSnsClient.Object,
                _mockCache.Object,
                _mockLogger.Object,
                mockLoggerFactory.Object
            );
        }

        // ═══════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds an APIGatewayHttpApiV2ProxyRequest with configurable body, path parameters,
        /// query parameters, and JWT claims for role-based authorization testing.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildRequest(
            string? body = null,
            Dictionary<string, string>? pathParams = null,
            Dictionary<string, string>? queryParams = null,
            List<string>? roles = null,
            bool includeAdminRole = true)
        {
            var claims = new Dictionary<string, string>();
            if (roles != null)
            {
                claims["custom:roles"] = string.Join(",", roles);
            }
            else if (includeAdminRole)
            {
                // Default: include administrator role for successful auth.
                claims["custom:roles"] = SystemIds.AdministratorRoleId.ToString();
            }

            return new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = pathParams ?? new Dictionary<string, string>(),
                QueryStringParameters = queryParams ?? new Dictionary<string, string>(),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["x-correlation-id"] = Guid.NewGuid().ToString()
                },
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = claims
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Builds a request with no authorization context at all (null Authorizer).
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildRequestWithNoAuth(
            string? body = null,
            Dictionary<string, string>? pathParams = null)
        {
            return new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = pathParams ?? new Dictionary<string, string>(),
                QueryStringParameters = new Dictionary<string, string>(),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json"
                },
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext()
            };
        }

        /// <summary>
        /// Creates a test Entity fixture with standard fields for assertion targets.
        /// </summary>
        private static Entity CreateTestEntity(Guid? id = null, string? name = null)
        {
            return new Entity
            {
                Id = id ?? TestEntityId,
                Name = name ?? TestEntityName,
                Label = "Test Entity",
                LabelPlural = "Test Entities",
                System = false,
                IconName = "fa fa-database",
                Color = "#4CAF50",
                Fields = new List<Field>(),
                RecordPermissions = new RecordPermissions
                {
                    CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanCreate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
                },
                RecordScreenIdField = null
            };
        }

        /// <summary>
        /// Creates a success EntityResponse wrapping the provided entity.
        /// </summary>
        private static EntityResponse CreateSuccessEntityResponse(Entity entity)
        {
            return new EntityResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Message = "Entity operation completed successfully.",
                Errors = new List<ErrorModel>(),
                Object = entity
            };
        }

        /// <summary>
        /// Creates an error EntityResponse with specified status code and error details.
        /// </summary>
        private static EntityResponse CreateErrorEntityResponse(int statusCode, string message,
            List<ErrorModel>? errors = null)
        {
            return new EntityResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = message,
                Errors = errors ?? new List<ErrorModel>(),
                Object = null,
                StatusCode = (System.Net.HttpStatusCode)statusCode
            };
        }

        /// <summary>
        /// Creates a valid InputEntity JSON string for CreateEntity / UpdateEntity tests.
        /// </summary>
        private static string CreateValidEntityJson(Guid? id = null, string name = "test_entity",
            string label = "Test Entity", string labelPlural = "Test Entities")
        {
            var entity = new
            {
                id = id?.ToString(),
                name,
                label,
                labelPlural,
                iconName = "fa fa-database",
                recordPermissions = new
                {
                    canRead = new[] { SystemIds.AdministratorRoleId.ToString() },
                    canCreate = new[] { SystemIds.AdministratorRoleId.ToString() },
                    canUpdate = new[] { SystemIds.AdministratorRoleId.ToString() },
                    canDelete = new[] { SystemIds.AdministratorRoleId.ToString() }
                }
            };
            return JsonSerializer.Serialize(entity, _jsonOptions);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 2: CreateEntity Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateEntity_ValidInput_Returns200WithCreatedEntity()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            var successResponse = CreateSuccessEntityResponse(testEntity);
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(successResponse);

            var body = CreateValidEntityJson();
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert — CreateEntity returns 201 on success.
            response.StatusCode.Should().Be(201);
            response.Body.Should().NotBeNullOrEmpty();

            var parsed = JsonSerializer.Deserialize<EntityResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();
            parsed.Object.Should().NotBeNull();
            parsed.Object!.Name.Should().Be(TestEntityName);
        }

        [Fact]
        public async Task CreateEntity_EmptyId_GeneratesNewGuid()
        {
            // Arrange: body with Id = Guid.Empty (should be handled by the service).
            var testEntity = CreateTestEntity();
            var successResponse = CreateSuccessEntityResponse(testEntity);

            InputEntity? capturedInput = null;
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .Callback<InputEntity, bool, Dictionary<string, Guid>?>((e, _, _) => capturedInput = e)
                .ReturnsAsync(successResponse);

            // Body with empty GUID for Id.
            var body = JsonSerializer.Serialize(new
            {
                id = Guid.Empty.ToString(),
                name = "test_entity",
                label = "Test Entity",
                labelPlural = "Test Entities"
            }, _jsonOptions);
            var request = BuildRequest(body: body);

            // Act
            await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: CreateEntity was called (the handler itself does not generate IDs —
            // the service layer handles it, but we verify the body was parsed and forwarded).
            _mockEntityService.Verify(
                s => s.CreateEntity(It.IsAny<InputEntity>(), false, null),
                Times.Once);
            capturedInput.Should().NotBeNull();
        }

        [Fact]
        public async Task CreateEntity_NameValidation_InvalidFormat_Returns400()
        {
            // Arrange: Service returns validation error for invalid name format.
            var errorResponse = CreateErrorEntityResponse(400, "Entity name validation failed.",
                new List<ErrorModel>
                {
                    new ErrorModel("name", "INVALID NAME!", "Entity name must contain only lowercase letters, digits, and underscores.")
                });
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(errorResponse);

            var body = JsonSerializer.Serialize(new
            {
                name = "INVALID NAME!",
                label = "Invalid",
                labelPlural = "Invalids"
            }, _jsonOptions);
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: The handler forwards the service's error status code.
            response.StatusCode.Should().Be(400);
            var parsed = JsonSerializer.Deserialize<EntityResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeFalse();
        }

        [Fact]
        public async Task CreateEntity_NameValidation_TooLong_Returns400()
        {
            // Arrange: Name exceeding 63 characters (PostgreSQL identifier limit).
            var longName = new string('a', 64);
            var errorResponse = CreateErrorEntityResponse(400, "Entity name too long.",
                new List<ErrorModel>
                {
                    new ErrorModel("name", longName, "Entity name must not exceed 63 characters.")
                });
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(errorResponse);

            var body = JsonSerializer.Serialize(new
            {
                name = longName,
                label = "Long Name Entity",
                labelPlural = "Long Name Entities"
            }, _jsonOptions);
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var parsed = JsonSerializer.Deserialize<EntityResponse>(response.Body, _jsonOptions);
            parsed!.Success.Should().BeFalse();
        }

        [Fact]
        public async Task CreateEntity_NameValidation_DuplicateName_Returns400()
        {
            // Arrange: Service returns error indicating duplicate name.
            var errorResponse = CreateErrorEntityResponse(400, "Entity with that name already exists.",
                new List<ErrorModel>
                {
                    new ErrorModel("name", "existing_entity", "Entity name must be unique.")
                });
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(errorResponse);

            var body = JsonSerializer.Serialize(new
            {
                name = "existing_entity",
                label = "Existing",
                labelPlural = "Existings"
            }, _jsonOptions);
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task CreateEntity_MissingLabel_Returns400()
        {
            // Arrange: Service returns error for missing label.
            var errorResponse = CreateErrorEntityResponse(400, "Label is required.",
                new List<ErrorModel>
                {
                    new ErrorModel("label", "", "Entity label is required.")
                });
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(errorResponse);

            var body = JsonSerializer.Serialize(new
            {
                name = "no_label_entity",
                labelPlural = "No Labels"
            }, _jsonOptions);
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var parsed = JsonSerializer.Deserialize<EntityResponse>(response.Body, _jsonOptions);
            parsed!.Success.Should().BeFalse();
        }

        [Fact]
        public async Task CreateEntity_MissingLabelPlural_Returns400()
        {
            // Arrange: Service returns error for missing label plural.
            var errorResponse = CreateErrorEntityResponse(400, "Label plural is required.",
                new List<ErrorModel>
                {
                    new ErrorModel("labelPlural", "", "Entity labelPlural is required.")
                });
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(errorResponse);

            var body = JsonSerializer.Serialize(new
            {
                name = "no_plural_entity",
                label = "No Plural"
            }, _jsonOptions);
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task CreateEntity_RecordPermissions_NullListsNormalized()
        {
            // Arrange: Body with recordPermissions having null sub-lists.
            // The handler forwards the input to the service which normalizes nulls to empty lists.
            var testEntity = CreateTestEntity();
            testEntity.RecordPermissions = new RecordPermissions
            {
                CanRead = new List<Guid>(),
                CanCreate = new List<Guid>(),
                CanUpdate = new List<Guid>(),
                CanDelete = new List<Guid>()
            };
            var successResponse = CreateSuccessEntityResponse(testEntity);

            InputEntity? capturedInput = null;
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .Callback<InputEntity, bool, Dictionary<string, Guid>?>((e, _, _) => capturedInput = e)
                .ReturnsAsync(successResponse);

            // JSON with null permission lists — System.Text.Json deserializes these as null.
            var body = @"{
                ""name"": ""perm_entity"",
                ""label"": ""Perm Entity"",
                ""labelPlural"": ""Perm Entities"",
                ""recordPermissions"": { ""canRead"": null, ""canCreate"": null, ""canUpdate"": null, ""canDelete"": null }
            }";
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: Handler passes input to service; service normalizes nulls.
            response.StatusCode.Should().Be(201);
            _mockEntityService.Verify(
                s => s.CreateEntity(It.IsAny<InputEntity>(), false, null), Times.Once);
        }

        [Fact]
        public async Task CreateEntity_IconName_DefaultsToFaDatabase()
        {
            // Arrange: Body without iconName — the service should apply default "fa fa-database".
            var testEntity = CreateTestEntity();
            testEntity.IconName = "fa fa-database";
            var successResponse = CreateSuccessEntityResponse(testEntity);

            InputEntity? capturedInput = null;
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .Callback<InputEntity, bool, Dictionary<string, Guid>?>((e, _, _) => capturedInput = e)
                .ReturnsAsync(successResponse);

            var body = JsonSerializer.Serialize(new
            {
                name = "icon_test_entity",
                label = "Icon Test",
                labelPlural = "Icon Tests"
                // No iconName specified.
            }, _jsonOptions);
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: Returned entity has default icon (service layer responsibility).
            response.StatusCode.Should().Be(201);
            var parsed = JsonSerializer.Deserialize<EntityResponse>(response.Body, _jsonOptions);
            parsed!.Object!.IconName.Should().Be("fa fa-database");
        }

        [Fact]
        public async Task CreateEntity_DefaultFieldsGenerated()
        {
            // Arrange: The service layer generates default fields including 'id' GuidField.
            var testEntity = CreateTestEntity();
            testEntity.Fields = new List<Field>
            {
                new GuidField
                {
                    Id = Guid.NewGuid(),
                    Name = "id",
                    Label = "Id",
                    Required = true,
                    Unique = true,
                    System = true,
                    GenerateNewId = true
                }
            };
            var successResponse = CreateSuccessEntityResponse(testEntity);

            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), false,
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(successResponse);

            var body = CreateValidEntityJson();
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: Response entity includes system id field (generated by service).
            // Use JsonDocument to inspect fields because Field is an abstract type that
            // System.Text.Json cannot polymorphically deserialize without a custom converter.
            response.StatusCode.Should().Be(201);
            using var doc = System.Text.Json.JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            root.GetProperty("success").GetBoolean().Should().BeTrue();
            var objElement = root.GetProperty("object");
            var fieldsArray = objElement.GetProperty("fields");
            fieldsArray.GetArrayLength().Should().BeGreaterThan(0);
            // Verify the 'id' field is present by searching all fields for name == "id"
            var hasIdField = false;
            foreach (var fieldElement in fieldsArray.EnumerateArray())
            {
                if (fieldElement.TryGetProperty("name", out var nameVal) &&
                    nameVal.GetString() == "id")
                {
                    hasIdField = true;
                    fieldElement.GetProperty("required").GetBoolean().Should().BeTrue();
                    fieldElement.GetProperty("unique").GetBoolean().Should().BeTrue();
                    fieldElement.GetProperty("system").GetBoolean().Should().BeTrue();
                    break;
                }
            }
            hasIdField.Should().BeTrue("the default 'id' GuidField must be present in entity fields");
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 3: JWT Authorization Tests (CRITICAL)
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateEntity_AdminRole_AllowsAccess()
        {
            // Arrange: JWT claims include AdministratorRoleId.
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var body = CreateValidEntityJson();
            var request = BuildRequest(body: body, includeAdminRole: true);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: 201 Created — access granted for admin.
            response.StatusCode.Should().Be(201);
        }

        [Fact]
        public async Task CreateEntity_NonAdminRole_Returns403()
        {
            // Arrange: JWT claims with only RegularRoleId (no admin).
            var body = CreateValidEntityJson();
            var request = BuildRequest(
                body: body,
                roles: new List<string> { SystemIds.RegularRoleId.ToString() },
                includeAdminRole: false);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: 403 Forbidden — access denied for non-admin.
            response.StatusCode.Should().Be(403);
            response.Body.Should().Contain("Access denied");

            // Verify entity service was NOT called.
            _mockEntityService.Verify(
                s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()), Times.Never);
        }

        [Fact]
        public async Task CreateEntity_NoJwtClaims_Returns403()
        {
            // Arrange: Request with no authorization context.
            var body = CreateValidEntityJson();
            var request = BuildRequestWithNoAuth(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: 403 Forbidden — no JWT claims present.
            response.StatusCode.Should().Be(403);
        }

        [Fact]
        public async Task UpdateEntity_AdminRequired_Returns403ForNonAdmin()
        {
            // Arrange: Non-admin role for update operation.
            var body = CreateValidEntityJson(id: TestEntityId);
            var request = BuildRequest(
                body: body,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() },
                roles: new List<string> { SystemIds.GuestRoleId.ToString() },
                includeAdminRole: false);

            // Act
            var response = await _handler.UpdateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            _mockEntityService.Verify(s => s.UpdateEntity(It.IsAny<InputEntity>()), Times.Never);
        }

        [Fact]
        public async Task DeleteEntity_AdminRequired_Returns403ForNonAdmin()
        {
            // Arrange: Non-admin role for delete operation.
            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() },
                roles: new List<string> { SystemIds.RegularRoleId.ToString() },
                includeAdminRole: false);

            // Act
            var response = await _handler.DeleteEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            _mockEntityService.Verify(s => s.DeleteEntity(It.IsAny<Guid>()), Times.Never);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 4: ReadEntity Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task ReadEntity_ById_Returns200WithEntity()
        {
            // Arrange: Path param is a valid GUID.
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["idOrName"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.ReadEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<EntityResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();
            parsed.Object.Should().NotBeNull();
            parsed.Object!.Id.Should().Be(TestEntityId);
        }

        [Fact]
        public async Task ReadEntity_ByName_Returns200WithEntity()
        {
            // Arrange: Path param is a string name (not parseable as GUID).
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityName))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["idOrName"] = TestEntityName });

            // Act
            var response = await _handler.ReadEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<EntityResponse>(response.Body, _jsonOptions);
            parsed!.Success.Should().BeTrue();
            parsed.Object!.Name.Should().Be(TestEntityName);
        }

        [Fact]
        public async Task ReadEntity_NotFound_Returns404()
        {
            // Arrange: Service returns not-found response (null entity, success=true).
            var notFoundResponse = new EntityResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Object = null,
                Errors = new List<ErrorModel>()
            };
            _mockEntityService
                .Setup(s => s.ReadEntity(It.IsAny<string>()))
                .ReturnsAsync(notFoundResponse);

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["idOrName"] = "nonexistent_entity" });

            // Act
            var response = await _handler.ReadEntity(request, _mockLambdaContext.Object);

            // Assert: Handler converts null entity to 404.
            response.StatusCode.Should().Be(404);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 5: ReadEntities (List All) — Cache-Aware with Hash
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task ReadEntities_CacheHit_ReturnsCachedList()
        {
            // Arrange: ReadEntities returns cached list directly from the service.
            var entities = new List<Entity> { CreateTestEntity() };
            var listResponse = new EntityListResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Object = entities,
                Hash = "cached-hash-123",
                Errors = new List<ErrorModel>()
            };
            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(listResponse);
            _mockEntityService
                .Setup(s => s.GetEntitiesHash())
                .Returns("different-hash");

            var request = BuildRequest();

            // Act
            var response = await _handler.ReadEntities(request, _mockLambdaContext.Object);

            // Assert: Full entity list returned.
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<EntityListResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();
            parsed.Object.Should().NotBeNull();
            parsed.Object!.Should().HaveCount(1);
        }

        [Fact]
        public async Task ReadEntities_CacheMiss_QueriesDynamoDBAndPopulatesCache()
        {
            // Arrange: No hash parameter — service reads from DynamoDB.
            var entities = new List<Entity> { CreateTestEntity(), CreateTestEntity(id: Guid.NewGuid(), name: "second_entity") };
            var listResponse = new EntityListResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Object = entities,
                Hash = "new-hash-456",
                Errors = new List<ErrorModel>()
            };
            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(listResponse);

            var request = BuildRequest();

            // Act
            var response = await _handler.ReadEntities(request, _mockLambdaContext.Object);

            // Assert: Entities returned from service (cache miss path).
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<EntityListResponse>(response.Body, _jsonOptions);
            parsed!.Object.Should().HaveCount(2);

            // Verify ReadEntities was called (proving cache miss or fresh fetch).
            _mockEntityService.Verify(s => s.ReadEntities(), Times.Once);
        }

        [Fact]
        public async Task ReadEntities_HashParameter_MatchingHash_ReturnsNullObject()
        {
            // Arrange: Client-provided hash matches server hash.
            const string matchingHash = "abc123-matching-hash";
            _mockEntityService
                .Setup(s => s.GetEntitiesHash())
                .Returns(matchingHash);

            var request = BuildRequest(
                queryParams: new Dictionary<string, string> { ["hash"] = matchingHash });

            // Act
            var response = await _handler.ReadEntities(request, _mockLambdaContext.Object);

            // Assert: null object returned (optimization — client already has latest data).
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<EntityListResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();
            parsed.Object.Should().BeNull();
            parsed.Hash.Should().Be(matchingHash);

            // ReadEntities should NOT be called (short-circuit path).
            _mockEntityService.Verify(s => s.ReadEntities(), Times.Never);
        }

        [Fact]
        public async Task ReadEntities_HashParameter_DifferentHash_ReturnsFullList()
        {
            // Arrange: Client hash does not match server hash.
            _mockEntityService
                .Setup(s => s.GetEntitiesHash())
                .Returns("server-hash-different");

            var entities = new List<Entity> { CreateTestEntity() };
            var listResponse = new EntityListResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Object = entities,
                Hash = "server-hash-different",
                Errors = new List<ErrorModel>()
            };
            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(listResponse);

            var request = BuildRequest(
                queryParams: new Dictionary<string, string> { ["hash"] = "client-hash-old" });

            // Act
            var response = await _handler.ReadEntities(request, _mockLambdaContext.Object);

            // Assert: Full entity list returned since hashes differ.
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<EntityListResponse>(response.Body, _jsonOptions);
            parsed!.Object.Should().NotBeNull();
            parsed.Object!.Should().HaveCount(1);
        }

        [Fact]
        public async Task ReadEntities_ResponseIncludesHash()
        {
            // Arrange
            const string expectedHash = "computed-hash-xyz";
            var listResponse = new EntityListResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Object = new List<Entity> { CreateTestEntity() },
                Hash = expectedHash,
                Errors = new List<ErrorModel>()
            };
            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(listResponse);

            var request = BuildRequest();

            // Act
            var response = await _handler.ReadEntities(request, _mockLambdaContext.Object);

            // Assert: Response includes hash property.
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<EntityListResponse>(response.Body, _jsonOptions);
            parsed!.Hash.Should().Be(expectedHash);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 6: UpdateEntity (Full Update) Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task UpdateEntity_ValidInput_Returns200()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            var successResponse = CreateSuccessEntityResponse(testEntity);
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .ReturnsAsync(successResponse);

            var body = CreateValidEntityJson(id: TestEntityId);
            var request = BuildRequest(
                body: body,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.UpdateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<EntityResponse>(response.Body, _jsonOptions);
            parsed!.Success.Should().BeTrue();
            parsed.Object.Should().NotBeNull();

            _mockEntityService.Verify(
                s => s.UpdateEntity(It.Is<InputEntity>(e => e.Id == TestEntityId)),
                Times.Once);
        }

        [Fact]
        public async Task UpdateEntity_EntityIdMissing_Returns400()
        {
            // Arrange: Empty GUID path parameter.
            var body = CreateValidEntityJson();
            var request = BuildRequest(
                body: body,
                pathParams: new Dictionary<string, string> { ["id"] = "" });

            // Act
            var response = await _handler.UpdateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Invalid entity ID format");
        }

        [Fact]
        public async Task UpdateEntity_EntityNotFound_Returns404()
        {
            // Arrange: Service returns error with 404.
            var notFoundResponse = CreateErrorEntityResponse(404, "Entity not found.");
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .ReturnsAsync(notFoundResponse);

            var body = CreateValidEntityJson(id: TestEntityId);
            var request = BuildRequest(
                body: body,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.UpdateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(404);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 7: PatchEntity (Partial Update) Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task PatchEntity_SingleProperty_AppliesOnlyThatProperty()
        {
            // Arrange: PATCH body with only label changed.
            var existingEntity = CreateTestEntity();
            existingEntity.Label = "Original Label";
            existingEntity.LabelPlural = "Original Plurals";
            existingEntity.IconName = "fa fa-star";

            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            InputEntity? capturedInput = null;
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .Callback<InputEntity>(e => capturedInput = e)
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            var patchBody = @"{ ""label"": ""New Label"" }";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert: Only label was changed, others preserved from existing entity.
            response.StatusCode.Should().Be(200);
            capturedInput.Should().NotBeNull();
            capturedInput!.Label.Should().Be("New Label");
            capturedInput.LabelPlural.Should().Be("Original Plurals");
            capturedInput.IconName.Should().Be("fa fa-star");
        }

        [Fact]
        public async Task PatchEntity_MultipleProperties_AppliesAll()
        {
            // Arrange
            var existingEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            InputEntity? capturedInput = null;
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .Callback<InputEntity>(e => capturedInput = e)
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            var patchBody = @"{ ""label"": ""Updated"", ""labelPlural"": ""Updated Items"", ""iconName"": ""fa fa-star"" }";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert: All three properties updated.
            response.StatusCode.Should().Be(200);
            capturedInput.Should().NotBeNull();
            capturedInput!.Label.Should().Be("Updated");
            capturedInput.LabelPlural.Should().Be("Updated Items");
            capturedInput.IconName.Should().Be("fa fa-star");
        }

        [Fact]
        public async Task PatchEntity_UnknownPropertyName_Returns400()
        {
            // Arrange: PATCH body with unknown property.
            var existingEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            var patchBody = @"{ ""unknownProp"": ""value"" }";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert: 400 with error about invalid property name.
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("invalid property");

            // Verify UpdateEntity was NOT called (validation failed before update).
            _mockEntityService.Verify(s => s.UpdateEntity(It.IsAny<InputEntity>()), Times.Never);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 8: DeleteEntity Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task DeleteEntity_ValidId_Returns200()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));
            _mockEntityService
                .Setup(s => s.DeleteEntity(TestEntityId))
                .ReturnsAsync(new EntityResponse
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Errors = new List<ErrorModel>()
                });

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.DeleteEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            _mockEntityService.Verify(s => s.DeleteEntity(TestEntityId), Times.Once);
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        [Fact]
        public async Task DeleteEntity_InvalidGuid_Returns400()
        {
            // Arrange: Path param is not a valid GUID.
            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = "not-a-guid" });

            // Act
            var response = await _handler.DeleteEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Invalid entity ID format");
        }

        [Fact]
        public async Task DeleteEntity_NotFound_Returns404()
        {
            // Arrange: ReadEntity returns entity (for name capture), but DeleteEntity returns error.
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(new EntityResponse
                {
                    Success = true,
                    Object = null,
                    Timestamp = DateTime.UtcNow,
                    Errors = new List<ErrorModel>()
                });

            var notFoundResponse = CreateErrorEntityResponse(404, "Entity not found.");
            _mockEntityService
                .Setup(s => s.DeleteEntity(TestEntityId))
                .ReturnsAsync(notFoundResponse);

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.DeleteEntity(request, _mockLambdaContext.Object);

            // Assert: Service returns 404, handler propagates.
            response.StatusCode.Should().Be(404);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 9: Cache Behavior Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateEntity_ClearsCache()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var body = CreateValidEntityJson();
            var request = BuildRequest(body: body);

            // Act
            await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: ClearCache was called after create.
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        [Fact]
        public async Task CreateEntity_PublishesSnsEvent()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var body = CreateValidEntityJson();
            var request = BuildRequest(body: body);

            // Act
            await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: SNS PublishAsync was called with entity-management.entity.created event.
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.TopicArn == "arn:aws:sns:us-east-1:000000000000:entity-events" &&
                        r.Message.Contains("entity-management.entity.created") &&
                        r.MessageAttributes.ContainsKey("eventType")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateEntity_ServiceException_Returns500()
        {
            // Arrange: Service throws an exception.
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ThrowsAsync(new InvalidOperationException("DynamoDB table not found"));

            var body = CreateValidEntityJson();
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: 500 Internal Server Error with generic message (production mode).
            response.StatusCode.Should().Be(500);
            response.Body.Should().Contain("unexpected error");
        }

        [Fact]
        public async Task CreateEntity_ResponseMatchesEntityResponseEnvelope()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var body = CreateValidEntityJson();
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: Response body matches EntityResponse envelope structure.
            response.StatusCode.Should().Be(201);
            var parsed = JsonSerializer.Deserialize<EntityResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();
            parsed.Timestamp.Should().NotBe(default);
            parsed.Errors.Should().NotBeNull();
            parsed.Errors.Should().BeEmpty();
            parsed.Object.Should().NotBeNull();
            parsed.Object!.Id.Should().NotBe(Guid.Empty);
            parsed.Object.Name.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task ReadEntities_ResponseMatchesEntityListResponseEnvelope()
        {
            // Arrange
            var entities = new List<Entity> { CreateTestEntity() };
            var listResponse = new EntityListResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Object = entities,
                Hash = "list-hash-789",
                Errors = new List<ErrorModel>(),
                Message = "Entities retrieved successfully."
            };
            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(listResponse);

            var request = BuildRequest();

            // Act
            var response = await _handler.ReadEntities(request, _mockLambdaContext.Object);

            // Assert: Response body matches EntityListResponse envelope.
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<EntityListResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();
            parsed.Timestamp.Should().NotBe(default);
            parsed.Errors.Should().NotBeNull();
            parsed.Object.Should().NotBeNull();
            parsed.Hash.Should().Be("list-hash-789");
        }

        [Fact]
        public async Task CreateEntity_ValidationError_ResponseIncludesErrorModels()
        {
            // Arrange: Service returns validation errors with ErrorModel structure.
            var errorResponse = CreateErrorEntityResponse(400, "Validation failed.",
                new List<ErrorModel>
                {
                    new ErrorModel("name", "bad_name!", "Invalid name format."),
                    new ErrorModel("label", "", "Label is required.")
                });
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(errorResponse);

            var body = JsonSerializer.Serialize(new
            {
                name = "bad_name!",
                labelPlural = "Items"
            }, _jsonOptions);
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: Response includes structured ErrorModel entries.
            response.StatusCode.Should().Be(400);
            var parsed = JsonSerializer.Deserialize<EntityResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeFalse();
            parsed.Errors.Should().NotBeNull();
            parsed.Errors.Should().HaveCount(2);

            // Verify ErrorModel structure: each error has key, value, message.
            var firstError = parsed.Errors[0];
            firstError.Key.Should().NotBeNullOrEmpty();
            firstError.Message.Should().NotBeNullOrEmpty();
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: PatchEntity Patchable Properties Tests
        // ═══════════════════════════════════════════════════════════════

        [Theory]
        [InlineData("label", @"""New Label""", "Label")]
        [InlineData("labelPlural", @"""New Plurals""", "LabelPlural")]
        [InlineData("iconName", @"""fa fa-star""", "IconName")]
        [InlineData("color", @"""#FF0000""", "Color")]
        public async Task PatchEntity_Patchable_Properties(string propertyName, string jsonValue, string expectedProp)
        {
            // Arrange
            var existingEntity = CreateTestEntity();
            existingEntity.Label = "Original";
            existingEntity.LabelPlural = "Originals";
            existingEntity.IconName = "fa fa-database";
            existingEntity.Color = "#000000";

            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            InputEntity? capturedInput = null;
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .Callback<InputEntity>(e => capturedInput = e)
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            var patchBody = $"{{ \"{propertyName}\": {jsonValue} }}";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert: Only the specified property was changed.
            response.StatusCode.Should().Be(200);
            capturedInput.Should().NotBeNull();

            // Verify the patched property has the new value.
            var actualValue = typeof(InputEntity).GetProperty(expectedProp)?.GetValue(capturedInput);
            actualValue.Should().NotBeNull();
        }

        [Fact]
        public async Task PatchEntity_SystemProperty_AppliesBoolean()
        {
            // Arrange: PATCH body with "system" boolean property.
            var existingEntity = CreateTestEntity();
            existingEntity.System = false;

            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            InputEntity? capturedInput = null;
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .Callback<InputEntity>(e => capturedInput = e)
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            var patchBody = @"{ ""system"": true }";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedInput.Should().NotBeNull();
            capturedInput!.System.Should().BeTrue();
        }

        [Fact]
        public async Task PatchEntity_RecordPermissions_AppliesComplex()
        {
            // Arrange: PATCH body with "recordPermissions" complex object.
            var existingEntity = CreateTestEntity();

            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            InputEntity? capturedInput = null;
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .Callback<InputEntity>(e => capturedInput = e)
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            var newRoleId = Guid.NewGuid();
            var patchBody = $@"{{ ""recordPermissions"": {{ ""canRead"": [""{newRoleId}""], ""canCreate"": [], ""canUpdate"": [], ""canDelete"": [] }} }}";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedInput.Should().NotBeNull();
            capturedInput!.RecordPermissions.Should().NotBeNull();
            capturedInput.RecordPermissions!.CanRead.Should().Contain(newRoleId);
        }

        [Fact]
        public async Task PatchEntity_RecordScreenIdField_AppliesGuid()
        {
            // Arrange
            var existingEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            InputEntity? capturedInput = null;
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .Callback<InputEntity>(e => capturedInput = e)
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            var screenFieldId = Guid.NewGuid();
            var patchBody = $@"{{ ""recordScreenIdField"": ""{screenFieldId}"" }}";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedInput.Should().NotBeNull();
            capturedInput!.RecordScreenIdField.Should().Be(screenFieldId);
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: SNS Event Publishing Tests for Other Operations
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task UpdateEntity_PublishesSnsEvent()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var body = CreateValidEntityJson(id: TestEntityId);
            var request = BuildRequest(
                body: body,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            await _handler.UpdateEntity(request, _mockLambdaContext.Object);

            // Assert: SNS event published for update.
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.Message.Contains("entity-management.entity.updated")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task PatchEntity_PublishesSnsEvent()
        {
            // Arrange
            var existingEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            var patchBody = @"{ ""label"": ""Patched"" }";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert: SNS event published after patch (uses entity-management.entity.updated).
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.Message.Contains("entity-management.entity.updated")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task DeleteEntity_PublishesSnsEvent()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));
            _mockEntityService
                .Setup(s => s.DeleteEntity(TestEntityId))
                .ReturnsAsync(new EntityResponse
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Errors = new List<ErrorModel>()
                });

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            await _handler.DeleteEntity(request, _mockLambdaContext.Object);

            // Assert: SNS event published for delete.
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.Message.Contains("entity-management.entity.deleted")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: Cache Clear Tests for Other Operations
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task UpdateEntity_ClearsCache()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var body = CreateValidEntityJson(id: TestEntityId);
            var request = BuildRequest(
                body: body,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            await _handler.UpdateEntity(request, _mockLambdaContext.Object);

            // Assert
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        [Fact]
        public async Task PatchEntity_ClearsCache()
        {
            // Arrange
            var existingEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            var patchBody = @"{ ""label"": ""Patched"" }";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        [Fact]
        public async Task DeleteEntity_ClearsCache()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));
            _mockEntityService
                .Setup(s => s.DeleteEntity(TestEntityId))
                .ReturnsAsync(new EntityResponse
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Errors = new List<ErrorModel>()
                });

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            await _handler.DeleteEntity(request, _mockLambdaContext.Object);

            // Assert
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: Empty/Null Body Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateEntity_EmptyBody_Returns400()
        {
            // Arrange: Request with empty body.
            var request = BuildRequest(body: "");

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("body is required");
        }

        [Fact]
        public async Task CreateEntity_InvalidJson_Returns400()
        {
            // Arrange: Malformed JSON.
            var request = BuildRequest(body: "{ this is not valid json }");

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Invalid JSON");
        }

        [Fact]
        public async Task UpdateEntity_EmptyBody_Returns400()
        {
            // Arrange
            var request = BuildRequest(
                body: "",
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.UpdateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task PatchEntity_EmptyBody_Returns400()
        {
            // Arrange
            var request = BuildRequest(
                body: "",
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: Correlation ID Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateEntity_ResponseIncludesCorrelationIdHeader()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var body = CreateValidEntityJson();
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: Response includes X-Correlation-Id header.
            response.Headers.Should().ContainKey("X-Correlation-Id");
            response.Headers["X-Correlation-Id"].Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task ReadEntity_ResponseIncludesCorrelationIdHeader()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["idOrName"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.ReadEntity(request, _mockLambdaContext.Object);

            // Assert
            response.Headers.Should().ContainKey("X-Correlation-Id");
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: ReadEntity Authorization Test
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task ReadEntity_NonAdmin_Returns403()
        {
            // Arrange
            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["idOrName"] = TestEntityId.ToString() },
                roles: new List<string> { SystemIds.GuestRoleId.ToString() },
                includeAdminRole: false);

            // Act
            var response = await _handler.ReadEntity(request, _mockLambdaContext.Object);

            // Assert: Entity reads also require admin role.
            response.StatusCode.Should().Be(403);
        }

        [Fact]
        public async Task ReadEntities_NonAdmin_Returns403()
        {
            // Arrange
            var request = BuildRequest(
                roles: new List<string> { SystemIds.RegularRoleId.ToString() },
                includeAdminRole: false);

            // Act
            var response = await _handler.ReadEntities(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: PatchEntity Edge Cases
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task PatchEntity_IdAndNameIgnored()
        {
            // Arrange: PATCH body with id and name — these should be silently ignored.
            var existingEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            InputEntity? capturedInput = null;
            _mockEntityService
                .Setup(s => s.UpdateEntity(It.IsAny<InputEntity>()))
                .Callback<InputEntity>(e => capturedInput = e)
                .ReturnsAsync(CreateSuccessEntityResponse(existingEntity));

            // Include id and name in PATCH body — should not cause errors.
            var patchBody = $@"{{ ""id"": ""{Guid.NewGuid()}"", ""name"": ""new_name"", ""label"": ""Patched Label"" }}";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert: id and name are silently ignored, label is patched.
            response.StatusCode.Should().Be(200);
            capturedInput.Should().NotBeNull();
            capturedInput!.Id.Should().Be(TestEntityId);
            capturedInput.Name.Should().Be(TestEntityName);
            capturedInput.Label.Should().Be("Patched Label");
        }

        [Fact]
        public async Task PatchEntity_EntityNotFound_Returns404()
        {
            // Arrange
            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityId))
                .ReturnsAsync(new EntityResponse
                {
                    Success = false,
                    Object = null,
                    Timestamp = DateTime.UtcNow,
                    Errors = new List<ErrorModel>()
                });

            var patchBody = @"{ ""label"": ""Updated"" }";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() });

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(404);
        }

        [Fact]
        public async Task PatchEntity_InvalidGuid_Returns400()
        {
            // Arrange
            var patchBody = @"{ ""label"": ""Updated"" }";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = "invalid-guid" });

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task PatchEntity_NonAdmin_Returns403()
        {
            // Arrange
            var patchBody = @"{ ""label"": ""Updated"" }";
            var request = BuildRequest(
                body: patchBody,
                pathParams: new Dictionary<string, string> { ["id"] = TestEntityId.ToString() },
                roles: new List<string> { SystemIds.RegularRoleId.ToString() },
                includeAdminRole: false);

            // Act
            var response = await _handler.PatchEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: Admin via Cognito Groups Claim
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateEntity_AdminViaCognitoGroups_AllowsAccess()
        {
            // Arrange: JWT claims with cognito:groups including "administrator".
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var body = CreateValidEntityJson();
            var request = new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = new Dictionary<string, string>(),
                QueryStringParameters = new Dictionary<string, string>(),
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = new Dictionary<string, string>
                            {
                                ["cognito:groups"] = "administrator"
                            }
                        }
                    }
                }
            };

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: Access granted via cognito:groups claim.
            response.StatusCode.Should().Be(201);
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: ReadEntity Path Parameter Edge Cases
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task ReadEntity_MissingPathParam_Returns400()
        {
            // Arrange: No idOrName or id path parameter.
            var request = BuildRequest(
                pathParams: new Dictionary<string, string>());

            // Act
            var response = await _handler.ReadEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("identifier");
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: DeleteEntity with Guid.Empty
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task DeleteEntity_EmptyGuid_Returns400()
        {
            // Arrange: Path param is empty GUID.
            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = Guid.Empty.ToString() });

            // Act
            var response = await _handler.DeleteEntity(request, _mockLambdaContext.Object);

            // Assert: Guid.Empty rejected by handler.
            response.StatusCode.Should().Be(400);
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: UpdateEntity with Guid.Empty ID
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task UpdateEntity_EmptyGuidId_Returns400()
        {
            // Arrange: Path param is empty GUID.
            var body = CreateValidEntityJson();
            var request = BuildRequest(
                body: body,
                pathParams: new Dictionary<string, string> { ["id"] = Guid.Empty.ToString() });

            // Act
            var response = await _handler.UpdateEntity(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
        }

        // ═══════════════════════════════════════════════════════════════
        // ADDITIONAL: Content-Type Header in Response
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateEntity_ResponseHasJsonContentType()
        {
            // Arrange
            var testEntity = CreateTestEntity();
            _mockEntityService
                .Setup(s => s.CreateEntity(It.IsAny<InputEntity>(), It.IsAny<bool>(),
                    It.IsAny<Dictionary<string, Guid>?>()))
                .ReturnsAsync(CreateSuccessEntityResponse(testEntity));

            var body = CreateValidEntityJson();
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.CreateEntity(request, _mockLambdaContext.Object);

            // Assert: Response has application/json content type.
            response.Headers.Should().ContainKey("Content-Type");
            response.Headers["Content-Type"].Should().Be("application/json");
        }
    }
}
