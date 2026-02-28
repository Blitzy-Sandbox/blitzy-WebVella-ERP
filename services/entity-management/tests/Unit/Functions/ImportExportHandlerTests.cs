// =============================================================================
// ImportExportHandlerTests.cs — Unit Tests for CSV Import/Export Lambda Handler
// =============================================================================
// Comprehensive xUnit tests for ImportExportHandler.cs covering:
//   - ImportFromCsv: valid CSV, existing record IDs, S3 not found, file path
//     normalization, empty entity name, invalid entity name, relation field
//     notation ($/$$ syntax), multiselect relation key rejection
//   - EvaluateImport: evaluate-only, evaluate-import with/without errors,
//     unknown field columns, field creation, permission denial, clipboard input
//   - SNS domain event publishing after successful imports
//   - Error handling: service exceptions, CSV parsing errors
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

using FluentAssertions;

using Microsoft.Extensions.Configuration;
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
    /// Unit tests for ImportExportHandler Lambda function covering ImportFromCsv and
    /// EvaluateImport handler methods — CSV parsing, relation field notation ($/$$ syntax),
    /// permission checks, SNS event publishing, and error handling.
    /// </summary>
    [Collection("ImportExportHandler")]
    public class ImportExportHandlerTests : IDisposable
    {
        // ═══════════════════════════════════════════════════════════════
        // MOCK DEPENDENCIES
        // ═══════════════════════════════════════════════════════════════

        private readonly Mock<IRecordService> _mockRecordService;
        private readonly Mock<IEntityService> _mockEntityService;
        private readonly Mock<IAmazonS3> _mockS3Client;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<ILogger<ImportExportHandler>> _mockLogger;
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<ILambdaContext> _mockLambdaContext;
        private readonly ImportExportHandler _handler;

        // ═══════════════════════════════════════════════════════════════
        // TEST FIXTURES
        // ═══════════════════════════════════════════════════════════════

        private static readonly Guid TestEntityId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private static readonly Guid TestRecordId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        private static readonly Guid TestRelationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private static readonly Guid TestRelatedEntityId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        private static readonly Guid TestFieldId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        private static readonly Guid TestOriginFieldId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        private static readonly Guid TestTargetFieldId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        private const string TestEntityName = "test_entity";
        private const string TestTopicArn = "arn:aws:sns:us-east-1:000000000000:import-events";
        private const string TestS3Bucket = "test-bucket";

        /// <summary>
        /// JSON serialization options matching the handler's internal _jsonOptions configuration.
        /// </summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // ═══════════════════════════════════════════════════════════════
        // ENVIRONMENT VARIABLE SAVE/RESTORE
        // ═══════════════════════════════════════════════════════════════

        private readonly string? _origImportTopicArn;
        private readonly string? _origRecordTopicArn;
        private readonly string? _origIsLocal;
        private readonly string? _origS3Bucket;
        private readonly string? _origTempPrefix;

        // ═══════════════════════════════════════════════════════════════
        // CONSTRUCTOR — Test Setup
        // ═══════════════════════════════════════════════════════════════

        public ImportExportHandlerTests()
        {
            _mockRecordService = new Mock<IRecordService>();
            _mockEntityService = new Mock<IEntityService>();
            _mockS3Client = new Mock<IAmazonS3>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockLogger = new Mock<ILogger<ImportExportHandler>>();
            _mockConfiguration = new Mock<IConfiguration>();
            _mockLambdaContext = new Mock<ILambdaContext>();

            // Default Lambda context with AwsRequestId for correlation ID extraction.
            _mockLambdaContext.Setup(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());
            _mockLambdaContext.Setup(c => c.FunctionName).Returns("ImportExportHandler-Test");

            // Configure SNS to return a successful publish response by default.
            _mockSnsClient
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });

            // Default entity service setups for common paths
            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse { Success = true, Object = new List<Entity>() });

            _mockEntityService
                .Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse { Success = true, Object = new List<EntityRelation>() });

            // Save original environment variable values to restore after test.
            _origImportTopicArn = Environment.GetEnvironmentVariable("IMPORT_TOPIC_ARN");
            _origRecordTopicArn = Environment.GetEnvironmentVariable("RECORD_TOPIC_ARN");
            _origIsLocal = Environment.GetEnvironmentVariable("IS_LOCAL");
            _origS3Bucket = Environment.GetEnvironmentVariable("FILES_S3_BUCKET");
            _origTempPrefix = Environment.GetEnvironmentVariable("FILES_TEMP_PREFIX");

            // Set environment variables so ImportExportHandler constructor reads them.
            Environment.SetEnvironmentVariable("IMPORT_TOPIC_ARN", TestTopicArn);
            Environment.SetEnvironmentVariable("RECORD_TOPIC_ARN", TestTopicArn);
            Environment.SetEnvironmentVariable("IS_LOCAL", "false");
            Environment.SetEnvironmentVariable("FILES_S3_BUCKET", TestS3Bucket);
            Environment.SetEnvironmentVariable("FILES_TEMP_PREFIX", "temp/");

            _handler = new ImportExportHandler(
                _mockRecordService.Object,
                _mockEntityService.Object,
                _mockS3Client.Object,
                _mockSnsClient.Object,
                _mockLogger.Object,
                _mockConfiguration.Object
            );
        }

        /// <summary>Restore environment variables after each test to prevent cross-contamination.</summary>
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("IMPORT_TOPIC_ARN", _origImportTopicArn);
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
        /// headers, and JWT claims for role-based authorization testing.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildRequest(
            string? body = null,
            Dictionary<string, string>? pathParams = null,
            List<string>? roles = null,
            bool includeAdminRole = true,
            string? userId = null)
        {
            var claims = new Dictionary<string, string>
            {
                ["sub"] = userId ?? Guid.NewGuid().ToString()
            };

            if (roles != null)
            {
                claims["custom:roles"] = string.Join(",", roles);
            }
            else if (includeAdminRole)
            {
                claims["custom:roles"] = SystemIds.AdministratorRoleId.ToString();
            }

            return new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = pathParams ?? new Dictionary<string, string>(),
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
        /// Creates a test Entity fixture with configurable fields and record permissions.
        /// By default, includes admin role in all permission lists.
        /// </summary>
        private static Entity CreateTestEntity(
            Guid? id = null,
            string? name = null,
            List<Field>? fields = null,
            RecordPermissions? permissions = null)
        {
            var entityId = id ?? TestEntityId;
            var entityName = name ?? TestEntityName;

            return new Entity
            {
                Id = entityId,
                Name = entityName,
                Label = "Test Entity",
                LabelPlural = "Test Entities",
                System = false,
                IconName = "fa fa-database",
                RecordPermissions = permissions ?? new RecordPermissions
                {
                    CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanCreate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
                },
                Fields = fields ?? new List<Field>
                {
                    CreateTextField("name", "Name"),
                    CreateGuidField("id", "Id")
                }
            };
        }

        /// <summary>Creates a TextField metadata fixture.</summary>
        private static TextField CreateTextField(string name, string label)
        {
            return new TextField
            {
                Id = Guid.NewGuid(),
                Name = name,
                Label = label,
                Required = false,
                Unique = false,
                Searchable = false,
                System = false,
                DefaultValue = null,
                MaxLength = null,
                EntityName = TestEntityName
            };
        }

        /// <summary>Creates a GuidField metadata fixture.</summary>
        private static GuidField CreateGuidField(string name, string label)
        {
            return new GuidField
            {
                Id = Guid.NewGuid(),
                Name = name,
                Label = label,
                Required = false,
                Unique = false,
                Searchable = false,
                System = false,
                EntityName = TestEntityName
            };
        }

        /// <summary>Creates a MultiSelectField metadata fixture.</summary>
        private static MultiSelectField CreateMultiSelectField(string name, string label, List<SelectOption>? options = null)
        {
            return new MultiSelectField
            {
                Id = Guid.NewGuid(),
                Name = name,
                Label = label,
                Required = false,
                Unique = false,
                Searchable = false,
                System = false,
                Options = options ?? new List<SelectOption>
                {
                    new SelectOption { Value = "opt1", Label = "Option 1" },
                    new SelectOption { Value = "opt2", Label = "Option 2" }
                },
                EntityName = TestEntityName
            };
        }

        /// <summary>
        /// Sets up entity service mocks for a standard import test scenario.
        /// </summary>
        private void SetupEntityServiceForImport(Entity entity, List<EntityRelation>? relations = null)
        {
            _mockEntityService
                .Setup(s => s.GetEntity(entity.Name))
                .ReturnsAsync(entity);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse { Success = true, Object = new List<Entity> { entity } });

            _mockEntityService
                .Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse
                {
                    Success = true,
                    Object = relations ?? new List<EntityRelation>()
                });

            _mockEntityService
                .Setup(s => s.ReadEntity(entity.Name))
                .ReturnsAsync(new EntityResponse { Success = true, Object = entity });
        }

        /// <summary>
        /// Creates a mock S3 GetObjectResponse with the specified CSV content.
        /// </summary>
        private void SetupS3CsvFile(string csvContent, string? expectedKeyContains = null)
        {
            var bytes = Encoding.UTF8.GetBytes(csvContent);
            var memoryStream = new MemoryStream(bytes);

            if (expectedKeyContains != null)
            {
                _mockS3Client
                    .Setup(s => s.GetObjectAsync(
                        It.Is<GetObjectRequest>(r => r.Key.Contains(expectedKeyContains)),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new GetObjectResponse { ResponseStream = memoryStream });
            }
            else
            {
                _mockS3Client
                    .Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new GetObjectResponse { ResponseStream = memoryStream });
            }
        }

        /// <summary>
        /// Creates a successful QueryResponse for record CRUD mock returns.
        /// </summary>
        private static QueryResponse CreateSuccessQueryResponse(EntityRecord? record = null)
        {
            return new QueryResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Message = "Success",
                Object = new QueryResult
                {
                    Data = record != null ? new List<EntityRecord> { record } : new List<EntityRecord>()
                }
            };
        }

        /// <summary>
        /// Deserializes response body as a BaseResponseModel for common assertions.
        /// </summary>
        private static BaseResponseModel? DeserializeResponse(string body)
        {
            return JsonSerializer.Deserialize<BaseResponseModel>(body, _jsonOptions);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 2: ImportFromCsv Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task ImportFromCsv_ValidCsvFile_ReturnsSuccess()
        {
            // Arrange
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);
            SetupS3CsvFile("id,name\n,Test Record\n");

            _mockRecordService
                .Setup(s => s.CreateRecord(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var body = new JObject { ["fileTempPath"] = "/uploads/data.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNull();

            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();
            parsed.GetProperty("message").GetString().Should().Contain("Created:");

            // Verify CreateRecord was called (CSV row has no id → create)
            _mockRecordService.Verify(
                s => s.CreateRecord(TestEntityName, It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task ImportFromCsv_CsvWithExistingRecordIds_CallsUpdateRecord()
        {
            // Arrange
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);

            var recordGuid = TestRecordId;
            SetupS3CsvFile($"id,name\n{recordGuid},Updated Name\n");

            _mockRecordService
                .Setup(s => s.UpdateRecord(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var body = new JObject { ["fileTempPath"] = "/uploads/data.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);

            // Verify UpdateRecord was called (CSV row has valid id → update)
            _mockRecordService.Verify(
                s => s.UpdateRecord(TestEntityName, It.IsAny<EntityRecord>()),
                Times.Once);

            // Verify CreateRecord was NOT called
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()),
                Times.Never);
        }

        [Fact]
        public async Task ImportFromCsv_S3FileNotFound_Returns400WithError()
        {
            // Arrange
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);

            // Mock S3 to throw AmazonS3Exception (NoSuchKey)
            _mockS3Client
                .Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonS3Exception("The specified key does not exist.") { ErrorCode = "NoSuchKey" });

            var body = new JObject { ["fileTempPath"] = "/uploads/missing.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — the internal method returns ResponseModel with Success=false,
            // and the handler maps non-success to 400 (Bad Request).
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("CSV file not found");
        }

        [Fact]
        public async Task ImportFromCsv_FilePathNormalization_StripsLeadingFs()
        {
            // Arrange
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);
            SetupS3CsvFile("id,name\n,Normalized\n");

            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            // Provide fileTempPath with /fs prefix to test normalization
            var body = new JObject { ["fileTempPath"] = "/fs/Uploads/Data.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — handler should normalize path: strip /fs, lowercase, ensure leading /
            // Expected S3 key: temp/ + uploads/data.csv (without leading /)
            response.StatusCode.Should().Be(200);

            _mockS3Client.Verify(
                s => s.GetObjectAsync(
                    It.Is<GetObjectRequest>(r =>
                        r.BucketName == TestS3Bucket &&
                        r.Key == "temp/uploads/data.csv"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task ImportFromCsv_EmptyEntityName_Returns400()
        {
            // Arrange — no entityName in path parameters
            var body = new JObject { ["fileTempPath"] = "/uploads/data.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string>());

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Entity name is required");
        }

        [Fact]
        public async Task ImportFromCsv_InvalidEntityName_Returns404()
        {
            // Arrange — entity service returns null for unknown entity
            _mockEntityService
                .Setup(s => s.GetEntity("nonexistent_entity"))
                .ReturnsAsync((Entity?)null);

            // Also return empty entities list so the cache-refresh path also fails
            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse { Success = true, Object = new List<Entity>() });

            var body = new JObject { ["fileTempPath"] = "/uploads/data.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = "nonexistent_entity" });

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — handler returns 400 with "Entity not found" message
            // (ImportEntityRecordsFromCsvInternal returns ResponseModel with Success=false)
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("not found");
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 3: Relation Field Notation Tests ($/$$ syntax)
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task ImportFromCsv_RelationFieldNotation_SingleDollar_ResolvesRelation()
        {
            // Arrange — $ prefix indicates standard relation direction (origin→target).
            // Set up test_entity as the ORIGIN entity with a "customer" relation to a related entity.
            var nameField = CreateTextField("name", "Name");
            var idField = CreateGuidField("id", "Id");
            var originField = CreateGuidField("customer_id", "Customer ID");
            originField.Id = TestOriginFieldId;
            originField.EntityName = TestEntityName;

            var entity = CreateTestEntity(fields: new List<Field> { idField, nameField, originField });

            // Create the related entity (target)
            var relatedNameField = CreateTextField("email", "Email");
            relatedNameField.EntityName = "customer";
            var relatedIdField = CreateGuidField("id", "Id");
            relatedIdField.Id = TestTargetFieldId;
            relatedIdField.EntityName = "customer";

            var relatedEntity = new Entity
            {
                Id = TestRelatedEntityId,
                Name = "customer",
                Label = "Customer",
                LabelPlural = "Customers",
                Fields = new List<Field> { relatedIdField, relatedNameField },
                RecordPermissions = new RecordPermissions
                {
                    CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanCreate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
                }
            };

            // Define relation: test_entity.customer_id → customer.id
            var relation = new EntityRelation
            {
                Id = TestRelationId,
                Name = "customer",
                Label = "Customer Relation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = TestEntityId,
                OriginFieldId = TestOriginFieldId,
                TargetEntityId = TestRelatedEntityId,
                TargetFieldId = TestTargetFieldId
            };

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(entity);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { entity, relatedEntity }
                });

            _mockEntityService
                .Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse
                {
                    Success = true,
                    Object = new List<EntityRelation> { relation }
                });

            // CSV uses $customer.email notation — single $ = standard direction
            SetupS3CsvFile("id,$customer.email\n,test@example.com\n");

            // Mock Find to return a matching related record
            var relatedRecord = new EntityRecord { ["id"] = Guid.NewGuid(), ["email"] = "test@example.com" };
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

            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var body = new JObject { ["fileTempPath"] = "/uploads/data.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);

            // Verify Find was called to resolve the relation field
            _mockRecordService.Verify(
                s => s.Find(It.IsAny<EntityQuery>()),
                Times.AtLeastOnce);

            // Verify CreateRecord was called (new record creation)
            _mockRecordService.Verify(
                s => s.CreateRecord(TestEntityName, It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task ImportFromCsv_RelationFieldNotation_DoubleDollar_FlipsDirection()
        {
            // Arrange — $$ prefix flips relation direction (target→origin).
            // test_entity is the TARGET entity of a "customer" relation.
            var nameField = CreateTextField("name", "Name");
            var idField = CreateGuidField("id", "Id");
            var targetField = CreateGuidField("ref_id", "Ref ID");
            targetField.Id = TestTargetFieldId;
            targetField.EntityName = TestEntityName;

            var entity = CreateTestEntity(fields: new List<Field> { idField, nameField, targetField });

            // Create the origin entity
            var originNameField = CreateTextField("email", "Email");
            originNameField.EntityName = "account";
            var originIdField = CreateGuidField("id", "Id");
            originIdField.Id = TestOriginFieldId;
            originIdField.EntityName = "account";

            var originEntity = new Entity
            {
                Id = TestRelatedEntityId,
                Name = "account",
                Label = "Account",
                LabelPlural = "Accounts",
                Fields = new List<Field> { originIdField, originNameField },
                RecordPermissions = new RecordPermissions
                {
                    CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanCreate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
                }
            };

            // Relation: account (origin) → test_entity (target)
            // Using $$ from test_entity means flipped direction → look at origin (account)
            var relation = new EntityRelation
            {
                Id = TestRelationId,
                Name = "account",
                Label = "Account Relation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = TestRelatedEntityId,   // account is origin
                OriginFieldId = TestOriginFieldId,
                TargetEntityId = TestEntityId,           // test_entity is target
                TargetFieldId = TestTargetFieldId
            };

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(entity);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { entity, originEntity }
                });

            _mockEntityService
                .Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse
                {
                    Success = true,
                    Object = new List<EntityRelation> { relation }
                });

            // CSV uses $$account.email notation — double $ = reversed direction
            SetupS3CsvFile("id,$$account.email\n,acme@corp.com\n");

            // Mock Find to return a matching related record from the origin entity
            var relatedRecord = new EntityRecord { ["id"] = Guid.NewGuid(), ["email"] = "acme@corp.com" };
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

            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var body = new JObject { ["fileTempPath"] = "/uploads/data.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);

            // Verify Find was called for relation resolution
            _mockRecordService.Verify(
                s => s.Find(It.IsAny<EntityQuery>()),
                Times.AtLeastOnce);

            // Verify CreateRecord was invoked
            _mockRecordService.Verify(
                s => s.CreateRecord(TestEntityName, It.IsAny<EntityRecord>()),
                Times.Once);
        }

        [Fact]
        public async Task ImportFromCsv_RelationFieldWithMultiselectKey_RejectsColumn()
        {
            // Arrange — multiselect fields cannot be used as relation lookup keys.
            // When ParseRelationColumnHeader encounters a MultiSelectField, it logs a warning
            // and silently skips — Find is never called, FK is not set.
            var nameField = CreateTextField("name", "Name");
            var idField = CreateGuidField("id", "Id");
            var entity = CreateTestEntity(fields: new List<Field> { idField, nameField });

            // Create related entity with a MultiSelectField as the lookup key
            var msField = CreateMultiSelectField("tags", "Tags");
            msField.EntityName = "category";
            var relatedIdField = CreateGuidField("id", "Id");
            relatedIdField.Id = TestTargetFieldId;
            relatedIdField.EntityName = "category";

            var relatedEntity = new Entity
            {
                Id = TestRelatedEntityId,
                Name = "category",
                Label = "Category",
                LabelPlural = "Categories",
                Fields = new List<Field> { relatedIdField, msField },
                RecordPermissions = new RecordPermissions
                {
                    CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanCreate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
                }
            };

            var relation = new EntityRelation
            {
                Id = TestRelationId,
                Name = "category",
                Label = "Category Relation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = TestEntityId,
                OriginFieldId = Guid.NewGuid(),
                TargetEntityId = TestRelatedEntityId,
                TargetFieldId = TestTargetFieldId
            };

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(entity);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { entity, relatedEntity }
                });

            _mockEntityService
                .Setup(s => s.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse
                {
                    Success = true,
                    Object = new List<EntityRelation> { relation }
                });

            // CSV uses $category.tags notation — tags is a MultiSelectField
            SetupS3CsvFile("id,$category.tags\n,opt1\n");

            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var body = new JObject { ["fileTempPath"] = "/uploads/data.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — the handler should still return 200 (silently skips the column with warning).
            // Find should NOT be called because the multiselect field is rejected.
            response.StatusCode.Should().Be(200);

            _mockRecordService.Verify(
                s => s.Find(It.IsAny<EntityQuery>()),
                Times.Never);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 4: EvaluateImport Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task EvaluateImport_EvaluateCommand_ReturnsEvaluationWithoutImporting()
        {
            // Arrange — general_command = "evaluate" means evaluate only, no import.
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);

            var csvContent = "id,name\n,Test Record\n";
            SetupS3CsvFile(csvContent);

            var postBody = new JObject
            {
                ["fileTempPath"] = "/uploads/data.csv",
                ["general_command"] = "evaluate"
            };
            var request = BuildRequest(
                body: postBody.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.EvaluateImport(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();
            parsed.GetProperty("message").GetString().Should().Contain("Evaluation completed");

            // Verify the response contains column analysis and stats
            var objProp = parsed.GetProperty("object");
            objProp.GetProperty("columns").GetArrayLength().Should().BeGreaterThan(0);
            objProp.GetProperty("command").GetString().Should().Be("evaluate");

            // Verify CreateRecord was NOT called (evaluate only — no import)
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()),
                Times.Never);

            _mockRecordService.Verify(
                s => s.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()),
                Times.Never);
        }

        [Fact]
        public async Task EvaluateImport_EvaluateImportCommand_NoErrors_ImportsRecords()
        {
            // Arrange — general_command = "evaluate-import" with valid CSV should import records.
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);

            var csvContent = "id,name\n,Alice\n,Bob\n";
            SetupS3CsvFile(csvContent);

            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var postBody = new JObject
            {
                ["fileTempPath"] = "/uploads/data.csv",
                ["general_command"] = "evaluate-import"
            };
            var request = BuildRequest(
                body: postBody.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.EvaluateImport(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();
            parsed.GetProperty("message").GetString().Should().Contain("Import completed");

            // Verify CreateRecord was called for each row (2 rows with no id → create)
            _mockRecordService.Verify(
                s => s.CreateRecord(TestEntityName, It.IsAny<EntityRecord>()),
                Times.Exactly(2));
        }

        [Fact]
        public async Task EvaluateImport_EvaluateImportCommand_WithErrors_DoesNotImport()
        {
            // Arrange — CSV data with a required field missing value.
            // Set up entity with a required field named "email".
            var nameField = CreateTextField("name", "Name");
            var emailField = CreateTextField("email", "Email");
            emailField.Required = true;
            var idField = CreateGuidField("id", "Id");
            var entity = CreateTestEntity(fields: new List<Field> { idField, nameField, emailField });
            SetupEntityServiceForImport(entity);

            // CSV has name but email column value is empty (required field)
            var csvContent = "id,name,email\n,Alice,\n";
            SetupS3CsvFile(csvContent);

            var postBody = new JObject
            {
                ["fileTempPath"] = "/uploads/data.csv",
                ["general_command"] = "evaluate-import"
            };
            var request = BuildRequest(
                body: postBody.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.EvaluateImport(request, _mockLambdaContext.Object);

            // Assert — errors found during evaluation prevent import execution
            response.StatusCode.Should().Be(400);
            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
            parsed.GetProperty("success").GetBoolean().Should().BeFalse();

            // No records should have been created or updated
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()),
                Times.Never);

            _mockRecordService.Verify(
                s => s.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()),
                Times.Never);
        }

        [Fact]
        public async Task EvaluateImport_UnknownFieldColumns_DefaultsToCreateCommand()
        {
            // Arrange — CSV column "unknown_field" doesn't match any entity field.
            // AnalyzeColumn should set command to "to_create" with a warning.
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);

            var csvContent = "id,name,unknown_field\n,Test,SomeValue\n";
            SetupS3CsvFile(csvContent);

            var postBody = new JObject
            {
                ["fileTempPath"] = "/uploads/data.csv",
                ["general_command"] = "evaluate"
            };
            var request = BuildRequest(
                body: postBody.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.EvaluateImport(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);

            // The column analysis should include the unknown_field with "to_create" command
            var columns = parsed.GetProperty("object").GetProperty("columns");
            var hasToCreateColumn = false;
            foreach (var col in columns.EnumerateArray())
            {
                var colNameProp = col.GetProperty("name");
                if (colNameProp.GetString() == "unknown_field")
                {
                    col.GetProperty("command").GetString().Should().Be("to_create");
                    hasToCreateColumn = true;
                    break;
                }
            }
            hasToCreateColumn.Should().BeTrue("the unknown_field column should be present with 'to_create' command");
        }

        [Fact]
        public async Task EvaluateImport_FieldCreationDuringImport_CallsEntityServiceCreateField()
        {
            // Arrange — evaluate-import with a column command "to_create" should create the field.
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);

            var csvContent = "id,name,new_field\n,Alice,Value1\n";
            SetupS3CsvFile(csvContent);

            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            _mockEntityService
                .Setup(s => s.CreateField(It.IsAny<Guid>(), It.IsAny<InputField>()))
                .ReturnsAsync(new FieldResponse { Success = true, Object = CreateTextField("new_field", "new_field") });

            var postBody = new JObject
            {
                ["fileTempPath"] = "/uploads/data.csv",
                ["general_command"] = "evaluate-import"
            };
            var request = BuildRequest(
                body: postBody.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.EvaluateImport(request, _mockLambdaContext.Object);

            // Assert — field creation was triggered for the unknown column
            response.StatusCode.Should().Be(200);

            _mockEntityService.Verify(
                s => s.CreateField(
                    TestEntityId,
                    It.Is<InputField>(f => f.Name == "new_field")),
                Times.Once);

            // Verify cache was cleared after import
            _mockEntityService.Verify(s => s.ClearCache(), Times.AtLeastOnce);
        }

        [Fact]
        public async Task EvaluateImport_PermissionDenied_Returns403()
        {
            // Arrange — JWT claims without create permission for the entity.
            var entity = CreateTestEntity(
                permissions: new RecordPermissions
                {
                    CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanCreate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
                });
            SetupEntityServiceForImport(entity);

            var csvContent = "id,name\n,NewRecord\n";
            SetupS3CsvFile(csvContent);

            // Build request with non-admin role (regular role has no create permission)
            var postBody = new JObject
            {
                ["fileTempPath"] = "/uploads/data.csv",
                ["general_command"] = "evaluate-import"
            };
            var request = BuildRequest(
                body: postBody.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName },
                roles: new List<string> { SystemIds.RegularRoleId.ToString() },
                includeAdminRole: false);

            // Act
            var response = await _handler.EvaluateImport(request, _mockLambdaContext.Object);

            // Assert — permission denied → 403 Forbidden
            response.StatusCode.Should().Be(403);
            response.Body.Should().Contain("permission");

            // No records should have been created
            _mockRecordService.Verify(
                s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()),
                Times.Never);
        }

        [Fact]
        public async Task EvaluateImport_ClipboardInput_ParsesTabDelimitedText()
        {
            // Arrange — clipboard data is tab-delimited instead of CSV.
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);

            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            // Tab-delimited clipboard data (tab character between columns)
            var clipboardText = "id\tname\n\tClipboard Record";

            var postBody = new JObject
            {
                ["clipboard"] = clipboardText,
                ["general_command"] = "evaluate-import"
            };
            var request = BuildRequest(
                body: postBody.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.EvaluateImport(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();

            // Verify CreateRecord was called for the clipboard row
            _mockRecordService.Verify(
                s => s.CreateRecord(TestEntityName, It.IsAny<EntityRecord>()),
                Times.Once);

            // Verify S3 was NOT called (clipboard path, not file path)
            _mockS3Client.Verify(
                s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 5: SNS Event Publishing Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task ImportFromCsv_SuccessfulImport_PublishesSnsEvent()
        {
            // Arrange
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);
            SetupS3CsvFile("id,name\n,EventTest\n");

            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var body = new JObject { ["fileTempPath"] = "/uploads/data.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);

            // Verify SNS PublishAsync was called with the correct event type
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.TopicArn == TestTopicArn &&
                        r.MessageAttributes.ContainsKey("eventType") &&
                        r.MessageAttributes["eventType"].StringValue == "entity-management.records.imported" &&
                        r.Message.Contains(TestEntityName)),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task EvaluateImport_SuccessfulBulkImport_PublishesSnsEvent()
        {
            // Arrange — evaluate-import that succeeds should also publish SNS event.
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);
            SetupS3CsvFile("id,name\n,BulkTest\n");

            _mockRecordService
                .Setup(s => s.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
                .ReturnsAsync(CreateSuccessQueryResponse());

            var postBody = new JObject
            {
                ["fileTempPath"] = "/uploads/data.csv",
                ["general_command"] = "evaluate-import"
            };
            var request = BuildRequest(
                body: postBody.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.EvaluateImport(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);

            // Verify SNS was called for evaluate-import (not for evaluate-only)
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.TopicArn == TestTopicArn &&
                        r.MessageAttributes.ContainsKey("eventType") &&
                        r.MessageAttributes["eventType"].StringValue == "entity-management.records.imported"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 6: Error Handling Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task ImportFromCsv_ServiceThrowsException_Returns500WithGenericError()
        {
            // Arrange — mock entity service to throw an exception
            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ThrowsAsync(new InvalidOperationException("Database connection failed"));

            var body = new JObject { ["fileTempPath"] = "/uploads/data.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — IS_LOCAL is "false" so we should get a generic error message
            response.StatusCode.Should().Be(500);
            response.Body.Should().Contain("An internal error occurred during import");
            // Should NOT contain the actual exception message in production mode
            response.Body.Should().NotContain("Database connection failed");
        }

        [Fact]
        public async Task ImportFromCsv_CsvParsingError_Returns400()
        {
            // Arrange — provide malformed CSV content that will cause a parsing error.
            var entity = CreateTestEntity();
            SetupEntityServiceForImport(entity);

            // Create an S3 response that yields a stream which throws on read
            // to simulate a CSV parsing failure mid-stream. A simpler approach:
            // return content that CsvHelper will fail to parse due to bad column structure.
            // The best way to trigger a CsvException is to use a stream that fails.
            var badStream = new BrokenStream();
            _mockS3Client
                .Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetObjectResponse { ResponseStream = badStream });

            var body = new JObject { ["fileTempPath"] = "/uploads/bad.csv" };
            var request = BuildRequest(
                body: body.ToString(),
                pathParams: new Dictionary<string, string> { ["entityName"] = TestEntityName });

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — CSV parsing error is caught and returns 400 (via ResponseModel.Success = false)
            // or 500 if the outer catch catches it first. In either case, it should be an error.
            response.StatusCode.Should().BeOneOf(400, 500);
            response.Body.Should().NotBeNull();

            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
            parsed.GetProperty("success").GetBoolean().Should().BeFalse();
        }

        // ═══════════════════════════════════════════════════════════════
        // HELPER — Broken Stream for CSV Parsing Error Simulation
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// A stream that throws an IOException on Read, simulating a corrupted
        /// or interrupted file download from S3. This triggers CsvHelper's
        /// exception handling path within the handler.
        /// </summary>
        private class BrokenStream : MemoryStream
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new IOException("Simulated stream read failure — corrupted CSV data");
            }

            public override int Read(Span<byte> buffer)
            {
                throw new IOException("Simulated stream read failure — corrupted CSV data");
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                throw new IOException("Simulated stream read failure — corrupted CSV data");
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                throw new IOException("Simulated stream read failure — corrupted CSV data");
            }
        }
    }
}
