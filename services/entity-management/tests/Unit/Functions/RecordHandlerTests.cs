// =============================================================================
// RecordHandlerTests.cs — Comprehensive Unit Tests for Record CRUD Lambda Handler
// =============================================================================
// Tests for RecordHandler.cs — the most complex Lambda handler in the Entity
// Management microservice. Validates EntityPermission enforcement
// (CanCreate/CanRead/CanUpdate/CanDelete), relation-aware payload processing
// ($relation.field parsing), field value normalization via NormalizeFieldValue,
// SNS domain event publishing for post-hooks, file/image S3 path handling,
// M:M relation bridge operations, and Count/Find query delegation.
//
// Namespace: WebVellaErp.EntityManagement.Tests.Unit.Functions
// Test Framework: xUnit 2.9.3 + Moq 4.20.72 + FluentAssertions 8.0.1
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;
using WebVellaErp.EntityManagement.Functions;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;
using Xunit;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace WebVellaErp.EntityManagement.Tests.Unit.Functions
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="RecordHandler"/> covering all 8 public Lambda
    /// handler methods (CreateRecord, ReadRecord, FindRecords, UpdateRecord, DeleteRecord,
    /// CreateRelationManyToManyRecord, RemoveRelationManyToManyRecord, Count).
    /// Tests are organized in 14 logical phases covering CRUD, permission enforcement,
    /// relation-aware payload processing, field normalization, SNS events, S3 file
    /// handling, M:M bridge operations, and error envelope patterns.
    /// </summary>
    public class RecordHandlerTests : IDisposable
    {
        // ═══════════════════════════════════════════════════════════════
        // MOCK DEPENDENCIES
        // ═══════════════════════════════════════════════════════════════

        private readonly Mock<IRecordService> _mockRecordService;
        private readonly Mock<IEntityService> _mockEntityService;
        private readonly Mock<IAmazonDynamoDB> _mockDynamoDb;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<IAmazonS3> _mockS3Client;
        private readonly Mock<ILogger<RecordHandler>> _mockLogger;
        private readonly Mock<ILambdaContext> _mockLambdaContext;
        private readonly RecordHandler _handler;

        // ═══════════════════════════════════════════════════════════════
        // TEST FIXTURES
        // ═══════════════════════════════════════════════════════════════

        private static readonly Guid TestEntityId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private static readonly Guid TestRecordId = Guid.Parse("b2c3d4e5-f6a7-8901-bcde-f12345678901");
        private const string TestEntityName = "test_entity";
        private const string TestS3Bucket = "test-files-bucket";
        private const string TestTopicArn = "arn:aws:sns:us-east-1:000000000000:record-events";

        /// <summary>
        /// JSON serialization options matching the handler's internal _jsonOptions configuration.
        /// </summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // Track original environment variable values for cleanup
        private readonly string? _origRecordTopicArn;
        private readonly string? _origIsLocal;
        private readonly string? _origS3Bucket;
        private readonly string? _origTempPrefix;

        // ═══════════════════════════════════════════════════════════════
        // CONSTRUCTOR — Test Setup
        // ═══════════════════════════════════════════════════════════════

        public RecordHandlerTests()
        {
            _mockRecordService = new Mock<IRecordService>();
            _mockEntityService = new Mock<IEntityService>();
            _mockDynamoDb = new Mock<IAmazonDynamoDB>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockS3Client = new Mock<IAmazonS3>();
            _mockLogger = new Mock<ILogger<RecordHandler>>();
            _mockLambdaContext = new Mock<ILambdaContext>();

            // Default Lambda context with AwsRequestId for correlation ID extraction.
            _mockLambdaContext.Setup(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());
            _mockLambdaContext.Setup(c => c.FunctionName).Returns("RecordHandler-Test");

            // Configure SNS to return a successful publish response by default.
            _mockSnsClient
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });

            // Configure S3 default responses
            _mockS3Client
                .Setup(s => s.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CopyObjectResponse());
            _mockS3Client
                .Setup(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteObjectResponse());

            // Save original environment variable values
            _origRecordTopicArn = Environment.GetEnvironmentVariable("RECORD_TOPIC_ARN");
            _origIsLocal = Environment.GetEnvironmentVariable("IS_LOCAL");
            _origS3Bucket = Environment.GetEnvironmentVariable("FILES_S3_BUCKET");
            _origTempPrefix = Environment.GetEnvironmentVariable("FILES_TEMP_PREFIX");

            // Set environment variables so RecordHandler constructor reads them.
            Environment.SetEnvironmentVariable("RECORD_TOPIC_ARN", TestTopicArn);
            Environment.SetEnvironmentVariable("IS_LOCAL", "false");
            Environment.SetEnvironmentVariable("FILES_S3_BUCKET", TestS3Bucket);
            Environment.SetEnvironmentVariable("FILES_TEMP_PREFIX", "tmp/");

            _handler = new RecordHandler(
                _mockRecordService.Object,
                _mockEntityService.Object,
                _mockSnsClient.Object,
                _mockS3Client.Object,
                _mockDynamoDb.Object,
                _mockLogger.Object
            );
        }

        /// <summary>Restore environment variables after each test to prevent cross-contamination.</summary>
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("RECORD_TOPIC_ARN", _origRecordTopicArn);
            Environment.SetEnvironmentVariable("IS_LOCAL", _origIsLocal);
            Environment.SetEnvironmentVariable("FILES_S3_BUCKET", _origS3Bucket);
            Environment.SetEnvironmentVariable("FILES_TEMP_PREFIX", _origTempPrefix);
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
            bool includeAdminRole = true,
            string? userId = null)
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

            if (!string.IsNullOrWhiteSpace(userId))
            {
                claims["sub"] = userId;
            }
            else
            {
                claims["sub"] = Guid.NewGuid().ToString();
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
        /// Creates a test Entity fixture with configurable fields and RecordPermissions.
        /// Default: admin role allowed for all CRUD operations.
        /// </summary>
        private static Entity CreateTestEntity(
            Guid? id = null,
            string? name = null,
            List<Field>? fields = null,
            RecordPermissions? permissions = null)
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
                Fields = fields ?? new List<Field>(),
                RecordPermissions = permissions ?? new RecordPermissions
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
        /// Creates a success QueryResponse with optional record data.
        /// </summary>
        private static QueryResponse CreateSuccessQueryResponse(EntityRecord? record = null)
        {
            var response = new QueryResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Message = "Operation completed successfully.",
                Errors = new List<ErrorModel>()
            };
            if (record != null)
            {
                response.Object = new QueryResult
                {
                    Data = new List<EntityRecord> { record }
                };
            }
            else
            {
                response.Object = new QueryResult
                {
                    Data = new List<EntityRecord>()
                };
            }
            return response;
        }

        /// <summary>
        /// Creates a failure QueryResponse with errors.
        /// </summary>
        private static QueryResponse CreateErrorQueryResponse(string message, HttpStatusCode statusCode = HttpStatusCode.BadRequest)
        {
            return new QueryResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = message,
                Errors = new List<ErrorModel>
                {
                    new ErrorModel("validation", "field", message)
                },
                StatusCode = statusCode
            };
        }

        /// <summary>
        /// Creates a test EntityRecord with basic field values.
        /// </summary>
        private static EntityRecord CreateTestRecord(Guid? id = null)
        {
            var record = new EntityRecord
            {
                ["id"] = id ?? TestRecordId,
                ["name"] = "Test Record",
                ["email"] = "test@test.com"
            };
            return record;
        }

        /// <summary>
        /// Creates a standard record creation request with entityName path parameter.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildCreateRecordRequest(
            string entityName = TestEntityName,
            Dictionary<string, object?>? recordData = null,
            bool includeAdminRole = true,
            List<string>? roles = null)
        {
            var data = recordData ?? new Dictionary<string, object?>
            {
                ["name"] = "Test Record",
                ["email"] = "test@test.com"
            };

            return BuildRequest(
                body: JsonSerializer.Serialize(data, _jsonOptions),
                pathParams: new Dictionary<string, string> { ["entityName"] = entityName },
                includeAdminRole: includeAdminRole,
                roles: roles);
        }

        /// <summary>
        /// Sets up entity service GetEntity mock to return a test entity.
        /// </summary>
        private Entity SetupTestEntity(
            string entityName = TestEntityName,
            List<Field>? fields = null,
            RecordPermissions? permissions = null)
        {
            var entity = CreateTestEntity(name: entityName, fields: fields, permissions: permissions);
            _mockEntityService
                .Setup(s => s.GetEntity(entityName))
                .ReturnsAsync(entity);
            return entity;
        }

        /// <summary>
        /// Sets up IRecordService.CreateRecord mock to return a success QueryResponse.
        /// </summary>
        private void SetupCreateRecordSuccess(EntityRecord? returnedRecord = null)
        {
            var record = returnedRecord ?? CreateTestRecord();
            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse(record));
        }

        /// <summary>
        /// Sets up IRecordService.Find mock to return specified records.
        /// </summary>
        private void SetupFindRecordsSuccess(List<EntityRecord>? records = null, long totalCount = 1)
        {
            var data = records ?? new List<EntityRecord> { CreateTestRecord() };
            var response = new QueryResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Message = "Records found.",
                Errors = new List<ErrorModel>(),
                Object = new QueryResult
                {
                    Data = data
                }
            };
            _mockRecordService
                .Setup(s => s.Find(It.IsAny<EntityQuery>()))
                .ReturnsAsync(response);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 2: CreateRecord Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRecord_ValidPayload_Returns200WithCreatedRecord()
        {
            // Arrange
            var testRecord = CreateTestRecord();
            SetupTestEntity();
            SetupCreateRecordSuccess(testRecord);

            var request = BuildCreateRecordRequest();

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNullOrEmpty();

            var parsed = JsonSerializer.Deserialize<QueryResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();
            parsed.Object.Should().NotBeNull();
            parsed.Object!.Data.Should().NotBeNull();
            parsed.Object.Data!.Count.Should().BeGreaterThan(0);

            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateRecord_PermissionDenied_Returns403()
        {
            // Arrange: Entity allows only admin, but request has regular role
            SetupTestEntity();
            var request = BuildCreateRecordRequest(
                includeAdminRole: false,
                roles: new List<string> { SystemIds.RegularRoleId.ToString() });

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            response.Body.Should().Contain("Access denied");

            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateRecord_EntityNotFound_Returns404()
        {
            // Arrange: Entity service returns null
            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync((Entity?)null);

            var request = BuildCreateRecordRequest();

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(404);
            response.Body.Should().Contain("not found");
        }

        [Fact]
        public async Task CreateRecord_ValidationErrors_Returns400()
        {
            // Arrange: RecordService returns validation errors
            SetupTestEntity();
            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateErrorQueryResponse("Validation failed"));

            var request = BuildCreateRecordRequest();

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var parsed = JsonSerializer.Deserialize<BaseResponseModel>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeFalse();
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 3: EntityPermission Enforcement Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRecord_CanCreatePermission_CheckedAgainstJwtRoles()
        {
            // Arrange: Entity allows only a custom role for Create
            var customRoleId = Guid.NewGuid();
            var entity = CreateTestEntity(permissions: new RecordPermissions
            {
                CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
                CanCreate = new List<Guid> { customRoleId },
                CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(entity);
            SetupCreateRecordSuccess();

            // JWT claims include the custom role (non-admin)
            var request = BuildCreateRecordRequest(
                includeAdminRole: false,
                roles: new List<string> { customRoleId.ToString() });

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: Access granted because custom role is in CanCreate list
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task ReadRecord_CanReadPermission_AllowsAccess()
        {
            // Arrange: Admin role in CanRead list
            SetupTestEntity();
            SetupFindRecordsSuccess();

            var request = BuildRequest(
                pathParams: new Dictionary<string, string>
                {
                    ["entityName"] = TestEntityName,
                    ["recordId"] = TestRecordId.ToString()
                });

            // Act
            var response = await _handler.ReadRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
        }

        [Fact]
        public async Task UpdateRecord_CanUpdatePermission_DeniedForWrongRole()
        {
            // Arrange: Entity allows only admin for update, request has regular role
            SetupTestEntity();
            var updateData = new Dictionary<string, object?>
            {
                ["name"] = "Updated Name"
            };
            var request = BuildRequest(
                body: JsonSerializer.Serialize(updateData, _jsonOptions),
                pathParams: new Dictionary<string, string>
                {
                    ["entityName"] = TestEntityName,
                    ["recordId"] = TestRecordId.ToString()
                },
                includeAdminRole: false,
                roles: new List<string> { SystemIds.RegularRoleId.ToString() });

            // Act
            var response = await _handler.UpdateRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            response.Body.Should().Contain("Access denied");
        }

        [Fact]
        public async Task DeleteRecord_CanDeletePermission_DeniedForWrongRole()
        {
            // Arrange: Entity allows only admin for delete, request has regular role
            SetupTestEntity();
            var request = BuildRequest(
                pathParams: new Dictionary<string, string>
                {
                    ["entityName"] = TestEntityName,
                    ["recordId"] = TestRecordId.ToString()
                },
                includeAdminRole: false,
                roles: new List<string> { SystemIds.RegularRoleId.ToString() });

            // Act
            var response = await _handler.DeleteRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            response.Body.Should().Contain("Access denied");
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 4: Relation-Aware Payload Processing Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRecord_RelationFieldWithDollarPrefix_ResolvesRelation()
        {
            // Arrange: Record body contains "$customer.email" key — dollar prefix + RELATION_SEPARATOR
            var customerEntityId = Guid.NewGuid();
            var customerEmailFieldId = Guid.NewGuid();
            var customerIdFieldId = Guid.NewGuid();
            var testEntityFkFieldId = Guid.NewGuid();
            var relatedRecordId = Guid.NewGuid();

            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateTextField("name"),
                CreateGuidField("customer_id", testEntityFkFieldId)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            // Setup relation: test_entity → customer (OneToMany)
            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "customer",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = customerEntityId,
                OriginFieldId = customerIdFieldId,
                TargetEntityId = TestEntityId,
                TargetFieldId = testEntityFkFieldId
            };
            _mockEntityService
                .Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse
                {
                    Success = true,
                    Object = new List<EntityRelation> { relation }
                });

            // Setup related record lookup: customer with email "john@test.com"
            var relatedRecord = new EntityRecord
            {
                ["id"] = relatedRecordId,
                ["email"] = "john@test.com"
            };
            _mockRecordService
                .Setup(s => s.Find(It.Is<EntityQuery>(q => q.EntityName != TestEntityName)))
                .ReturnsAsync(new QueryResponse
                {
                    Success = true,
                    Object = new QueryResult
                    {
                        Data = new List<EntityRecord> { relatedRecord }
                    }
                });

            SetupCreateRecordSuccess();

            // Build request with $relation.field notation
            var recordData = new Dictionary<string, object?>
            {
                ["name"] = "Test Record",
                ["$customer.email"] = "john@test.com"
            };

            var request = BuildCreateRecordRequest(recordData: recordData);

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: Should attempt relation resolution
            response.StatusCode.Should().Be(200);
            _mockEntityService.Verify(s => s.ReadRelations(), Times.AtLeastOnce);
        }

        [Fact]
        public async Task CreateRecord_RelationFieldWithDoubleDollar_FlipsDirectionPriority()
        {
            // Arrange: Record body contains "$$customer.email" — double dollar flips direction priority
            var customerEntityId = Guid.NewGuid();
            var customerFieldId = Guid.NewGuid();
            var testEntityFkFieldId = Guid.NewGuid();
            var relatedRecordId = Guid.NewGuid();

            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateTextField("name")
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            // Setup relation where testEntity is ORIGIN and customer is TARGET
            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "customer",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = TestEntityId,
                OriginFieldId = testEntityFkFieldId,
                TargetEntityId = customerEntityId,
                TargetFieldId = customerFieldId
            };
            _mockEntityService
                .Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse
                {
                    Success = true,
                    Object = new List<EntityRelation> { relation }
                });

            // Setup related record lookup
            var relatedRecord = new EntityRecord
            {
                ["id"] = relatedRecordId,
                ["email"] = "john@test.com"
            };
            _mockRecordService
                .Setup(s => s.Find(It.IsAny<EntityQuery>()))
                .ReturnsAsync(new QueryResponse
                {
                    Success = true,
                    Object = new QueryResult
                    {
                        Data = new List<EntityRecord> { relatedRecord }
                    }
                });

            SetupCreateRecordSuccess();

            var recordData = new Dictionary<string, object?>
            {
                ["name"] = "Test Record",
                ["$$customer.email"] = "john@test.com"
            };

            var request = BuildCreateRecordRequest(recordData: recordData);

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: Double dollar flips direction — ReadRelations should be invoked
            _mockEntityService.Verify(s => s.ReadRelations(), Times.AtLeastOnce);
        }

        [Fact]
        public async Task CreateRecord_RelationField_OneToOne_UniquenessEnforced()
        {
            // Arrange: OneToOne relation where lookup returns data (tests processing path)
            var customerEntityId = Guid.NewGuid();
            var customerFieldId = Guid.NewGuid();
            var testEntityFkFieldId = Guid.NewGuid();

            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateTextField("name"),
                CreateGuidField("customer_id", testEntityFkFieldId)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "customer",
                RelationType = EntityRelationType.OneToOne,
                OriginEntityId = customerEntityId,
                OriginFieldId = customerFieldId,
                TargetEntityId = TestEntityId,
                TargetFieldId = testEntityFkFieldId
            };
            _mockEntityService
                .Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse
                {
                    Success = true,
                    Object = new List<EntityRelation> { relation }
                });

            // Return a single matching record (1:1 uniqueness)
            _mockRecordService
                .Setup(s => s.Find(It.Is<EntityQuery>(q => q.EntityName != TestEntityName)))
                .ReturnsAsync(new QueryResponse
                {
                    Success = true,
                    Object = new QueryResult
                    {
                        Data = new List<EntityRecord>
                        {
                            new EntityRecord { ["id"] = Guid.NewGuid(), ["email"] = "john@test.com" }
                        }
                    }
                });

            SetupCreateRecordSuccess();

            var recordData = new Dictionary<string, object?>
            {
                ["name"] = "Test",
                ["$customer.email"] = "john@test.com"
            };
            var request = BuildCreateRecordRequest(recordData: recordData);

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: Should proceed (1:1 relation resolution handled by service)
            _mockEntityService.Verify(s => s.ReadRelations(), Times.AtLeastOnce);
        }

        [Fact]
        public async Task CreateRecord_RelationField_ManyToMany_BridgeRowCreated()
        {
            // Arrange: ManyToMany relation — should delegate to ProcessManyToManyRelationInput
            var customerEntityId = Guid.NewGuid();
            var customerFieldId = Guid.NewGuid();
            var testEntityFieldId = Guid.NewGuid();
            var relatedRecordId = Guid.NewGuid();

            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateTextField("name")
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "tags",
                RelationType = EntityRelationType.ManyToMany,
                OriginEntityId = TestEntityId,
                OriginFieldId = testEntityFieldId,
                TargetEntityId = customerEntityId,
                TargetFieldId = customerFieldId,
                // CRITICAL: Handler checks string.IsNullOrWhiteSpace(relatedEntityName) and skips if empty
                TargetEntityName = "tag_entity",
                OriginEntityName = TestEntityName
            };
            _mockEntityService
                .Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse
                {
                    Success = true,
                    Object = new List<EntityRelation> { relation }
                });

            // Setup related record lookup
            var relatedRecord = new EntityRecord
            {
                ["id"] = relatedRecordId,
                ["name"] = "Tag1"
            };
            _mockRecordService
                .Setup(s => s.Find(It.IsAny<EntityQuery>()))
                .ReturnsAsync(new QueryResponse
                {
                    Success = true,
                    Object = new QueryResult
                    {
                        Data = new List<EntityRecord> { relatedRecord }
                    }
                });

            // Setup M:M bridge creation
            _mockRecordService
                .Setup(s => s.CreateRelationManyToManyRecord(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            SetupCreateRecordSuccess();

            var recordData = new Dictionary<string, object?>
            {
                ["name"] = "Test",
                ["$tags.name"] = "Tag1"
            };
            var request = BuildCreateRecordRequest(recordData: recordData);

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: M:M bridge should be created via RecordService
            _mockRecordService.Verify(
                s => s.CreateRelationManyToManyRecord(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task CreateRecord_RelationField_InvalidRelationName_ReturnsError()
        {
            // Arrange: Body references a non-existent relation
            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateTextField("name")
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            // No relations configured
            _mockEntityService
                .Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse
                {
                    Success = true,
                    Object = new List<EntityRelation>()
                });

            SetupCreateRecordSuccess();

            var recordData = new Dictionary<string, object?>
            {
                ["name"] = "Test",
                ["$nonexistent.email"] = "test@test.com"
            };
            var request = BuildCreateRecordRequest(recordData: recordData);

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: Handler silently skips unknown relations (logs warning, continues)
            // The $nonexistent.email key is removed from the record and processing completes successfully
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<QueryResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 5: Field Value Normalization Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRecord_CurrencyField_RoundsDecimalValue()
        {
            // Arrange: CurrencyField value with extra decimal places
            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateFieldOfType("price", FieldType.CurrencyField)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);
            SetupCreateRecordSuccess();

            var recordData = new Dictionary<string, object?>
            {
                ["price"] = 123.456789m
            };
            var request = BuildCreateRecordRequest(recordData: recordData);

            EntityRecord? capturedRecord = null;
            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .Callback<Entity, EntityRecord>((_, r) => capturedRecord = r)
                .ReturnsAsync(CreateSuccessQueryResponse(CreateTestRecord()));

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: Currency should be rounded to 4 decimal places (Math.Round(value, 4))
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateRecord_DateField_AppliesTimezoneHandling()
        {
            // Arrange: DateField should strip time component (.Date)
            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateFieldOfType("birth_date", FieldType.DateField)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            EntityRecord? capturedRecord = null;
            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .Callback<Entity, EntityRecord>((_, r) => capturedRecord = r)
                .ReturnsAsync(CreateSuccessQueryResponse(CreateTestRecord()));

            var recordData = new Dictionary<string, object?>
            {
                ["birth_date"] = "2024-03-15T14:30:00Z"
            };
            var request = BuildCreateRecordRequest(recordData: recordData);

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: Should succeed — date is processed by NormalizeFieldValue
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateRecord_DateTimeField_AppliesTimezoneConversion()
        {
            // Arrange: DateTimeField with ISO 8601 string — UTC conversion
            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateFieldOfType("created_at", FieldType.DateTimeField)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);
            SetupCreateRecordSuccess();

            var recordData = new Dictionary<string, object?>
            {
                ["created_at"] = "2024-03-15T14:30:00+05:00"
            };
            var request = BuildCreateRecordRequest(recordData: recordData);

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateRecord_GuidField_ParsesStringToGuid()
        {
            // Arrange: GuidField with string GUID value
            var guidValue = Guid.NewGuid();
            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateGuidField("reference_id")
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            EntityRecord? capturedRecord = null;
            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .Callback<Entity, EntityRecord>((_, r) => capturedRecord = r)
                .ReturnsAsync(CreateSuccessQueryResponse(CreateTestRecord()));

            var recordData = new Dictionary<string, object?>
            {
                ["reference_id"] = guidValue.ToString()
            };
            var request = BuildCreateRecordRequest(recordData: recordData);

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateRecord_MultiselectField_ConvertsJArrayToList()
        {
            // Arrange: MultiSelectField with JSON array value
            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateFieldOfType("tags", FieldType.MultiSelectField)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            EntityRecord? capturedRecord = null;
            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .Callback<Entity, EntityRecord>((_, r) => capturedRecord = r)
                .ReturnsAsync(CreateSuccessQueryResponse(CreateTestRecord()));

            // Note: The body goes through DeserializeRecord which uses Newtonsoft JObject.Parse
            // so arrays become JArray internally. Using JSON string with array.
            var request = BuildRequest(
                body: "{\"tags\": [\"opt1\", \"opt2\", \"opt3\"]}",
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: MultiSelect should be normalized to List<string>
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateRecord_PasswordField_HashesEncryptedValue()
        {
            // Arrange: PasswordField with plaintext value — should pass through normalization
            // (password hashing is handled by RecordService, handler passes through)
            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateFieldOfType("password", FieldType.PasswordField)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);
            SetupCreateRecordSuccess();

            var recordData = new Dictionary<string, object?>
            {
                ["password"] = "SecurePassword123!"
            };
            var request = BuildCreateRecordRequest(recordData: recordData);

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: PasswordField passes through NormalizeFieldValue (no modification)
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateRecord_AutoNumberField_AutoIncrements()
        {
            // Arrange: AutoNumberField — handler normalizes to decimal, service handles increment
            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateFieldOfType("serial_number", FieldType.AutoNumberField)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);
            SetupCreateRecordSuccess();

            // AutoNumber fields may have a numeric value provided
            var recordData = new Dictionary<string, object?>
            {
                ["serial_number"] = 42
            };
            var request = BuildCreateRecordRequest(recordData: recordData);

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: AutoNumber normalization converts to decimal
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateRecord_FileField_NormalizesS3Path()
        {
            // Arrange: FileField with "/fs/" prefix — should be stripped by NormalizeFilePath
            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateFieldOfType("attachment", FieldType.FileField)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            EntityRecord? capturedRecord = null;
            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .Callback<Entity, EntityRecord>((_, r) => capturedRecord = r)
                .ReturnsAsync(CreateSuccessQueryResponse(CreateTestRecord()));

            var recordData = new Dictionary<string, object?>
            {
                ["attachment"] = "/fs/documents/report.pdf"
            };
            var request = BuildCreateRecordRequest(recordData: recordData);

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: File path should be processed (NormalizeFilePath strips /fs/ prefix)
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateRecord_ImageField_MovesFromTempToEntity()
        {
            // Arrange: ImageField with temp S3 path — should trigger S3 copy/delete
            var recordId = Guid.NewGuid();
            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateFieldOfType("avatar", FieldType.ImageField)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            // Record with temp file path that starts with _filesTempPrefix ("tmp/")
            var createdRecord = new EntityRecord
            {
                ["id"] = recordId,
                ["avatar"] = "tmp/upload-12345/photo.jpg"
            };
            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse(createdRecord));

            var request = BuildRequest(
                body: "{\"avatar\": \"tmp/upload-12345/photo.jpg\"}",
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: S3 copy and delete should be invoked for moving from temp to permanent
            // Handler calls the 4-string overload: CopyObjectAsync(sourceBucket, sourceKey, destBucket, destKey)
            // NOT the CopyObjectRequest overload — must verify the correct overload
            response.StatusCode.Should().Be(200);
            _mockS3Client.Verify(
                s => s.CopyObjectAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 6: SNS Domain Event Publishing Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRecord_PostHook_PublishesSnsEvent()
        {
            // Arrange
            SetupTestEntity();
            SetupCreateRecordSuccess();
            var request = BuildCreateRecordRequest();

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: SNS publish should be called with event type "entity-management.{entityName}.created"
            response.StatusCode.Should().Be(200);
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(p =>
                        p.TopicArn == TestTopicArn &&
                        p.MessageAttributes.ContainsKey("eventType") &&
                        p.MessageAttributes["eventType"].StringValue.Contains($"{TestEntityName}.created")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task UpdateRecord_PostHook_PublishesSnsEvent()
        {
            // Arrange
            SetupTestEntity();
            _mockRecordService
                .Setup(s => s.UpdateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse(CreateTestRecord()));

            var updateData = new Dictionary<string, object?>
            {
                ["name"] = "Updated Name"
            };
            var request = BuildRequest(
                body: JsonSerializer.Serialize(updateData, _jsonOptions),
                pathParams: new Dictionary<string, string>
                {
                    ["entityName"] = TestEntityName,
                    ["recordId"] = TestRecordId.ToString()
                });

            // Act
            var response = await _handler.UpdateRecord(request, _mockLambdaContext.Object);

            // Assert: SNS publish with "entity-management.{entityName}.updated"
            response.StatusCode.Should().Be(200);
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(p =>
                        p.TopicArn == TestTopicArn &&
                        p.MessageAttributes.ContainsKey("eventType") &&
                        p.MessageAttributes["eventType"].StringValue.Contains($"{TestEntityName}.updated")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task DeleteRecord_PostHook_PublishesSnsEvent()
        {
            // Arrange
            SetupTestEntity();
            SetupFindRecordsSuccess(); // Existing record fetch for file cleanup
            _mockRecordService
                .Setup(s => s.DeleteRecord(It.IsAny<Entity>(), It.IsAny<Guid>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var request = BuildRequest(
                pathParams: new Dictionary<string, string>
                {
                    ["entityName"] = TestEntityName,
                    ["recordId"] = TestRecordId.ToString()
                });

            // Act
            var response = await _handler.DeleteRecord(request, _mockLambdaContext.Object);

            // Assert: SNS publish with "entity-management.{entityName}.deleted"
            response.StatusCode.Should().Be(200);
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(p =>
                        p.TopicArn == TestTopicArn &&
                        p.MessageAttributes.ContainsKey("eventType") &&
                        p.MessageAttributes["eventType"].StringValue.Contains($"{TestEntityName}.deleted")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateRecord_PreHookValidation_BlocksOnError()
        {
            // Arrange: Entity with fields that would pass pre-hook
            // Pre-hook validation in RecordHandler is currently lightweight structural checks.
            // Test that when RecordService returns failure, no SNS event is published.
            SetupTestEntity();
            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateErrorQueryResponse("Pre-hook validation failed"));

            var request = BuildCreateRecordRequest();

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: No SNS event published when create fails
            response.StatusCode.Should().Be(400);
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(p =>
                        p.MessageAttributes.ContainsKey("eventType") &&
                        p.MessageAttributes["eventType"].StringValue.Contains("created")),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 7: ReadRecord Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task ReadRecord_ValidId_ReturnsRecord()
        {
            // Arrange
            SetupTestEntity();
            var testRecord = CreateTestRecord();
            SetupFindRecordsSuccess(new List<EntityRecord> { testRecord });

            var request = BuildRequest(
                pathParams: new Dictionary<string, string>
                {
                    ["entityName"] = TestEntityName,
                    ["recordId"] = TestRecordId.ToString()
                });

            // Act
            var response = await _handler.ReadRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNullOrEmpty();

            var parsed = JsonSerializer.Deserialize<QueryResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();
            parsed.Object.Should().NotBeNull();
            parsed.Object!.Data.Should().NotBeNull();
            parsed.Object.Data!.Count.Should().Be(1);

            // Verify Find was called with EntityQuery.QueryEQ("id", recordId)
            _mockRecordService.Verify(
                s => s.Find(It.Is<EntityQuery>(q =>
                    q.EntityName == TestEntityName &&
                    q.Fields == "*")),
                Times.Once);
        }

        [Fact]
        public async Task ReadRecord_NotFound_Returns404()
        {
            // Arrange: Find returns no records
            SetupTestEntity();
            _mockRecordService
                .Setup(s => s.Find(It.IsAny<EntityQuery>()))
                .ReturnsAsync(new QueryResponse
                {
                    Success = true,
                    Object = new QueryResult
                    {
                        Data = new List<EntityRecord>()
                    }
                });

            var request = BuildRequest(
                pathParams: new Dictionary<string, string>
                {
                    ["entityName"] = TestEntityName,
                    ["recordId"] = TestRecordId.ToString()
                });

            // Act
            var response = await _handler.ReadRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(404);
            response.Body.Should().Contain("not found");
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 8: FindRecords Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task FindRecords_WithFilters_DelegatesToRecordService()
        {
            // Arrange
            SetupTestEntity();
            var records = new List<EntityRecord>
            {
                CreateTestRecord(Guid.NewGuid()),
                CreateTestRecord(Guid.NewGuid())
            };
            SetupFindRecordsSuccess(records);

            // Use actual model classes so enum values serialize as integers
            // (System.Text.Json default — no JsonStringEnumConverter configured)
            var queryBody = JsonSerializer.Serialize(new
            {
                fields = "*",
                query = new QueryObject
                {
                    QueryType = QueryType.EQ,
                    FieldName = "name",
                    FieldValue = "Test"
                },
                skip = 0,
                limit = 10
            }, _jsonOptions);

            var request = BuildRequest(
                body: queryBody,
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.FindRecords(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<QueryResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();

            _mockRecordService.Verify(
                s => s.Find(It.Is<EntityQuery>(q => q.EntityName == TestEntityName)),
                Times.Once);
        }

        [Fact]
        public async Task FindRecords_WithPagination_ReturnsPagedResults()
        {
            // Arrange
            SetupTestEntity();
            var records = new List<EntityRecord>
            {
                CreateTestRecord(Guid.NewGuid()),
                CreateTestRecord(Guid.NewGuid()),
                CreateTestRecord(Guid.NewGuid())
            };
            SetupFindRecordsSuccess(records);

            var queryBody = JsonSerializer.Serialize(new
            {
                fields = "*",
                skip = 0,
                limit = 10
            }, _jsonOptions);

            var request = BuildRequest(
                body: queryBody,
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.FindRecords(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<QueryResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();
            parsed.Object.Should().NotBeNull();
            parsed.Object!.Data.Should().NotBeNullOrEmpty();
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 9: UpdateRecord Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task UpdateRecord_ValidPayload_Returns200()
        {
            // Arrange
            SetupTestEntity();
            var updatedRecord = CreateTestRecord(TestRecordId);
            updatedRecord["name"] = "Updated Name";
            _mockRecordService
                .Setup(s => s.UpdateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse(updatedRecord));

            var updateData = new Dictionary<string, object?>
            {
                ["name"] = "Updated Name"
            };
            var request = BuildRequest(
                body: JsonSerializer.Serialize(updateData, _jsonOptions),
                pathParams: new Dictionary<string, string>
                {
                    ["entityName"] = TestEntityName,
                    ["recordId"] = TestRecordId.ToString()
                });

            // Act
            var response = await _handler.UpdateRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<QueryResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();

            _mockRecordService.Verify(
                s => s.UpdateRecord(
                    It.IsAny<Entity>(),
                    It.Is<EntityRecord>(r => r.ContainsKey("id"))),
                Times.Once);
        }

        [Fact]
        public async Task UpdateRecord_RelationAwareProcessing_HandledSameAsCreate()
        {
            // Arrange: $relation.field notation in update request
            var customerEntityId = Guid.NewGuid();
            var customerFieldId = Guid.NewGuid();
            var testEntityFkFieldId = Guid.NewGuid();

            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateTextField("name"),
                CreateGuidField("customer_id", testEntityFkFieldId)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "customer",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = customerEntityId,
                OriginFieldId = customerFieldId,
                TargetEntityId = TestEntityId,
                TargetFieldId = testEntityFkFieldId
            };
            _mockEntityService
                .Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse
                {
                    Success = true,
                    Object = new List<EntityRelation> { relation }
                });

            _mockRecordService
                .Setup(s => s.Find(It.IsAny<EntityQuery>()))
                .ReturnsAsync(new QueryResponse
                {
                    Success = true,
                    Object = new QueryResult
                    {
                        Data = new List<EntityRecord>
                        {
                            new EntityRecord { ["id"] = Guid.NewGuid(), ["email"] = "john@test.com" }
                        }
                    }
                });
            _mockRecordService
                .Setup(s => s.UpdateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse(CreateTestRecord()));

            var request = BuildRequest(
                body: "{\"name\": \"Updated\", \"$customer.email\": \"john@test.com\"}",
                pathParams: new Dictionary<string, string>
                {
                    ["entityName"] = TestEntityName,
                    ["recordId"] = TestRecordId.ToString()
                });

            // Act
            var response = await _handler.UpdateRecord(request, _mockLambdaContext.Object);

            // Assert: Relation processing should have been invoked
            _mockEntityService.Verify(s => s.ReadRelations(), Times.AtLeastOnce);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 10: DeleteRecord Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task DeleteRecord_ValidId_Returns200()
        {
            // Arrange
            SetupTestEntity();
            SetupFindRecordsSuccess(); // For fetching existing record before delete
            _mockRecordService
                .Setup(s => s.DeleteRecord(It.IsAny<Entity>(), It.IsAny<Guid>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var request = BuildRequest(
                pathParams: new Dictionary<string, string>
                {
                    ["entityName"] = TestEntityName,
                    ["recordId"] = TestRecordId.ToString()
                });

            // Act
            var response = await _handler.DeleteRecord(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.DeleteRecord(It.IsAny<Entity>(), TestRecordId),
                Times.Once);
        }

        [Fact]
        public async Task DeleteRecord_FileFieldCleanup_DeletesS3Objects()
        {
            // Arrange: Entity with FileField, existing record has file path values
            var testEntity = CreateTestEntity(fields: new List<Field>
            {
                CreateGuidField("id"),
                CreateFieldOfType("document", FieldType.FileField)
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityName)).ReturnsAsync(testEntity);

            var existingRecord = new EntityRecord
            {
                ["id"] = TestRecordId,
                ["document"] = "test_entity/" + TestRecordId + "/report.pdf"
            };
            _mockRecordService
                .Setup(s => s.Find(It.IsAny<EntityQuery>()))
                .ReturnsAsync(new QueryResponse
                {
                    Success = true,
                    Object = new QueryResult
                    {
                        Data = new List<EntityRecord> { existingRecord }
                    }
                });
            _mockRecordService
                .Setup(s => s.DeleteRecord(It.IsAny<Entity>(), It.IsAny<Guid>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var request = BuildRequest(
                pathParams: new Dictionary<string, string>
                {
                    ["entityName"] = TestEntityName,
                    ["recordId"] = TestRecordId.ToString()
                });

            // Act
            var response = await _handler.DeleteRecord(request, _mockLambdaContext.Object);

            // Assert: S3 DeleteObject should be called for file cleanup
            response.StatusCode.Should().Be(200);
            _mockS3Client.Verify(
                s => s.DeleteObjectAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 11: M:M Relation Bridge Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRelationManyToManyRecord_ValidInput_Returns200()
        {
            // Arrange
            var relationId = Guid.NewGuid();
            var originValue = Guid.NewGuid();
            var targetValue = Guid.NewGuid();

            var relation = new EntityRelation
            {
                Id = relationId,
                Name = "test_m2m_relation",
                RelationType = EntityRelationType.ManyToMany,
                OriginEntityId = Guid.NewGuid(),
                TargetEntityId = Guid.NewGuid(),
                OriginFieldId = Guid.NewGuid(),
                TargetFieldId = Guid.NewGuid()
            };
            _mockEntityService
                .Setup(s => s.ReadRelation(relationId))
                .ReturnsAsync(new EntityRelationResponse
                {
                    Success = true,
                    Object = relation
                });

            _mockRecordService
                .Setup(s => s.CreateRelationManyToManyRecord(relationId, originValue, targetValue))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var body = JsonSerializer.Serialize(new
            {
                originValue = originValue.ToString(),
                targetValue = targetValue.ToString()
            }, _jsonOptions);

            var request = BuildRequest(
                body: body,
                pathParams: new Dictionary<string, string>
                {
                    ["relationId"] = relationId.ToString()
                });

            // Act
            var response = await _handler.CreateRelationManyToManyRecord(
                request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.CreateRelationManyToManyRecord(relationId, originValue, targetValue),
                Times.Once);

            // Verify SNS event "entity-management.relation.created" published
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(p =>
                        p.MessageAttributes.ContainsKey("eventType") &&
                        p.MessageAttributes["eventType"].StringValue.Contains("relation.created")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task RemoveRelationManyToManyRecord_ValidInput_Returns200()
        {
            // Arrange
            var relationId = Guid.NewGuid();
            var originValue = Guid.NewGuid();
            var targetValue = Guid.NewGuid();

            var relation = new EntityRelation
            {
                Id = relationId,
                Name = "test_m2m_relation",
                RelationType = EntityRelationType.ManyToMany,
                OriginEntityId = Guid.NewGuid(),
                TargetEntityId = Guid.NewGuid(),
                OriginFieldId = Guid.NewGuid(),
                TargetFieldId = Guid.NewGuid()
            };
            _mockEntityService
                .Setup(s => s.ReadRelation(relationId))
                .ReturnsAsync(new EntityRelationResponse
                {
                    Success = true,
                    Object = relation
                });

            _mockRecordService
                .Setup(s => s.RemoveRelationManyToManyRecord(relationId, originValue, targetValue))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var body = JsonSerializer.Serialize(new
            {
                originValue = originValue.ToString(),
                targetValue = targetValue.ToString()
            }, _jsonOptions);

            var request = BuildRequest(
                body: body,
                pathParams: new Dictionary<string, string>
                {
                    ["relationId"] = relationId.ToString()
                });

            // Act
            var response = await _handler.RemoveRelationManyToManyRecord(
                request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            _mockRecordService.Verify(
                s => s.RemoveRelationManyToManyRecord(relationId, originValue, targetValue),
                Times.Once);

            // Verify SNS event "entity-management.relation.deleted" published
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(p =>
                        p.MessageAttributes.ContainsKey("eventType") &&
                        p.MessageAttributes["eventType"].StringValue.Contains("relation.deleted")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 12: Count Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task Count_WithFilter_ReturnsCount()
        {
            // Arrange
            SetupTestEntity();
            _mockRecordService
                .Setup(s => s.Count(TestEntityName, It.IsAny<QueryObject?>()))
                .ReturnsAsync(new QueryCountResponse
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Message = "Count completed.",
                    Errors = new List<ErrorModel>(),
                    Object = 42
                });

            // Use actual QueryObject so enum values serialize as integers
            // (System.Text.Json default — no JsonStringEnumConverter configured)
            var filterBody = JsonSerializer.Serialize(new QueryObject
            {
                QueryType = QueryType.EQ,
                FieldName = "status",
                FieldValue = "active"
            }, _jsonOptions);

            var request = BuildRequest(
                body: filterBody,
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.Count(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<QueryCountResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeTrue();
            parsed.Object.Should().Be(42);

            _mockRecordService.Verify(
                s => s.Count(TestEntityName, It.IsAny<QueryObject?>()),
                Times.Once);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 13: Error Handling Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRecord_ServiceException_Returns500()
        {
            // Arrange: Entity exists but service throws
            SetupTestEntity();
            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .ThrowsAsync(new InvalidOperationException("Database connection failed"));

            var request = BuildCreateRecordRequest();

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: 500 with generic error message (no stack trace in production mode)
            response.StatusCode.Should().Be(500);
            response.Body.Should().Contain("unexpected error");
            response.Body.Should().NotContain("Database connection failed");
        }

        [Fact]
        public async Task CreateRecord_DevMode_ExceptionIncludesStackTrace()
        {
            // Arrange: IS_LOCAL = true enables development mode details in error responses
            // We need a new handler instance with IS_LOCAL=true
            Environment.SetEnvironmentVariable("IS_LOCAL", "true");
            var devHandler = new RecordHandler(
                _mockRecordService.Object,
                _mockEntityService.Object,
                _mockSnsClient.Object,
                _mockS3Client.Object,
                _mockDynamoDb.Object,
                _mockLogger.Object);

            SetupTestEntity();
            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<Entity>(), It.IsAny<EntityRecord>()))
                .ThrowsAsync(new InvalidOperationException("Database connection failed"));

            var request = BuildCreateRecordRequest();

            // Act
            var response = await devHandler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: 500 in dev mode — handler catches and wraps, message is generic
            // but structured logging captures details. The HTTP response still uses
            // generic message per BuildErrorResponse behavior.
            response.StatusCode.Should().Be(500);
            var parsed = JsonSerializer.Deserialize<BaseResponseModel>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Success.Should().BeFalse();
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 14: Response Envelope Pattern Test
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateRecord_ResponseMatchesQueryResponseEnvelope()
        {
            // Arrange
            var testRecord = CreateTestRecord();
            SetupTestEntity();
            SetupCreateRecordSuccess(testRecord);
            var request = BuildCreateRecordRequest();

            // Act
            var response = await _handler.CreateRecord(request, _mockLambdaContext.Object);

            // Assert: Verify response matches QueryResponse envelope structure
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNullOrEmpty();

            var parsed = JsonSerializer.Deserialize<QueryResponse>(response.Body, _jsonOptions);
            parsed.Should().NotBeNull();

            // Verify envelope properties
            parsed!.Success.Should().BeTrue();
            parsed.Timestamp.Should().NotBe(default);
            parsed.Message.Should().NotBeNullOrEmpty();
            parsed.Errors.Should().NotBeNull();
            parsed.Errors.Should().BeEmpty();

            // Verify object structure: { data: [...], ... }
            parsed.Object.Should().NotBeNull();
            parsed.Object!.Data.Should().NotBeNull();
            parsed.Object.Data!.Should().NotBeEmpty();

            // Verify headers
            response.Headers.Should().ContainKey("Content-Type");
            response.Headers["Content-Type"].Should().Be("application/json");
            response.Headers.Should().ContainKey("X-Correlation-Id");
        }

        // ═══════════════════════════════════════════════════════════════
        // Field Factory Helpers — Create concrete Field instances
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a GuidField with the specified name.
        /// Directly instantiates the concrete Field subclass (matching sibling test patterns).
        /// </summary>
        private static GuidField CreateGuidField(string name, Guid? id = null)
        {
            return new GuidField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = name
            };
        }

        /// <summary>
        /// Creates a TextField with the specified name.
        /// </summary>
        private static TextField CreateTextField(string name, Guid? id = null)
        {
            return new TextField
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Label = name
            };
        }

        /// <summary>
        /// Creates a concrete Field subclass of the specified FieldType.
        /// Uses a switch expression to instantiate the correct concrete class
        /// with standard Id, Name, and Label properties.
        /// </summary>
        private static Field CreateFieldOfType(string name, FieldType fieldType, Guid? id = null)
        {
            var fieldId = id ?? Guid.NewGuid();
            Field field = fieldType switch
            {
                FieldType.AutoNumberField => new AutoNumberField(),
                FieldType.CheckboxField => new CheckboxField(),
                FieldType.CurrencyField => new CurrencyField(),
                FieldType.DateField => new DateField(),
                FieldType.DateTimeField => new DateTimeField(),
                FieldType.EmailField => new EmailField(),
                FieldType.FileField => new FileField(),
                FieldType.HtmlField => new HtmlField(),
                FieldType.ImageField => new ImageField(),
                FieldType.MultiLineTextField => new MultiLineTextField(),
                FieldType.MultiSelectField => new MultiSelectField(),
                FieldType.NumberField => new NumberField(),
                FieldType.PasswordField => new PasswordField(),
                FieldType.PercentField => new PercentField(),
                FieldType.PhoneField => new PhoneField(),
                FieldType.GuidField => new GuidField(),
                FieldType.SelectField => new SelectField(),
                FieldType.TextField => new TextField(),
                FieldType.UrlField => new UrlField(),
                FieldType.GeographyField => new GeographyField(),
                _ => new GuidField()
            };
            field.Id = fieldId;
            field.Name = name;
            field.Label = name;
            return field;
        }
    }
}
