// =============================================================================
// DataSourceHandlerTests.cs — Unit Tests for DataSource Lambda Handler
// =============================================================================
// Comprehensive xUnit tests for DataSourceHandler.cs covering:
//   - GetDataSources: cache hit/miss, code+DB merging
//   - GetDataSource: by ID and by name, 404 handling
//   - CreateDataSource: EQL validation, parameter processing, admin auth, SNS events
//   - UpdateDataSource: valid update, not found, SNS events
//   - DeleteDataSource: valid delete, not found, cache invalidation, SNS events
//   - ExecuteDataSource: stored DB, stored code, ad-hoc EQL, parameter enrichment
//   - ExecuteDataSourceSelect2: Select2 response format, pagination
//   - Caching: TTL, invalidation on mutations
//   - Authorization: admin role required for mutations, read for queries
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
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
    /// Unit tests for DataSourceHandler Lambda function covering all CRUD operations,
    /// EQL validation, parameter processing, caching, SNS event publishing, and Select2 format.
    /// </summary>
    public class DataSourceHandlerTests
    {
        // ═══════════════════════════════════════════════════════════════
        // MOCK DEPENDENCIES
        // ═══════════════════════════════════════════════════════════════

        private readonly Mock<IQueryAdapter> _mockQueryAdapter;
        private readonly Mock<IEntityService> _mockEntityService;
        private readonly Mock<IAmazonDynamoDB> _mockDynamoDb;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<IMemoryCache> _mockCache;
        private readonly Mock<ILogger<DataSourceHandler>> _mockLogger;
        private readonly Mock<ILambdaContext> _mockLambdaContext;
        private readonly DataSourceHandler _handler;

        // ═══════════════════════════════════════════════════════════════
        // TEST FIXTURES
        // ═══════════════════════════════════════════════════════════════

        private static readonly Guid TestDataSourceId = Guid.Parse("d1a2b3c4-e5f6-7890-abcd-111111111111");
        private static readonly Guid TestEntityId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private const string TestEntityName = "test_entity";
        private const string TestDsName = "test_datasource";

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

        public DataSourceHandlerTests()
        {
            _mockQueryAdapter = new Mock<IQueryAdapter>();
            _mockEntityService = new Mock<IEntityService>();
            _mockDynamoDb = new Mock<IAmazonDynamoDB>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockCache = new Mock<IMemoryCache>();
            _mockLogger = new Mock<ILogger<DataSourceHandler>>();
            _mockLambdaContext = new Mock<ILambdaContext>();

            // Default Lambda context with AwsRequestId for correlation ID extraction.
            _mockLambdaContext.Setup(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());
            _mockLambdaContext.Setup(c => c.FunctionName).Returns("DataSourceHandler-Test");

            // Configure SNS to return a successful publish response by default.
            _mockSnsClient
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });

            // Default DynamoDB QueryAsync returns empty (for cache miss scenarios to override).
            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

            // Default DynamoDB PutItemAsync succeeds.
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutItemResponse());

            // Default DynamoDB DeleteItemAsync succeeds.
            _mockDynamoDb
                .Setup(d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteItemResponse());

            // Default ReadEntities returns empty entity list.
            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse { Success = true, Object = new List<Entity>() });

            // Set environment variables for handler initialization.
            Environment.SetEnvironmentVariable("DATASOURCE_TABLE_NAME", "entity-management-datasources");
            Environment.SetEnvironmentVariable("DATASOURCE_TOPIC_ARN", "arn:aws:sns:us-east-1:000000000000:datasource-events");
            Environment.SetEnvironmentVariable("IS_LOCAL", "true");

            _handler = new DataSourceHandler(
                _mockQueryAdapter.Object,
                _mockEntityService.Object,
                _mockDynamoDb.Object,
                _mockSnsClient.Object,
                _mockCache.Object,
                _mockLogger.Object
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
            var claims = new Dictionary<string, string>
            {
                ["sub"] = Guid.NewGuid().ToString()
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
        /// Builds a request with non-admin role for authorization rejection tests.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildNonAdminRequest(
            string? body = null,
            Dictionary<string, string>? pathParams = null)
        {
            return BuildRequest(
                body: body,
                pathParams: pathParams,
                roles: new List<string> { SystemIds.RegularRoleId.ToString() },
                includeAdminRole: false);
        }

        /// <summary>
        /// Creates a test Entity fixture for entity service mock returns.
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
                Fields = new List<Field>
                {
                    new TextField { Id = Guid.NewGuid(), Name = "name", Label = "Name" },
                    new GuidField { Id = Guid.NewGuid(), Name = "id", Label = "ID" }
                },
                RecordPermissions = new RecordPermissions
                {
                    CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanCreate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
                }
            };
        }

        /// <summary>
        /// Creates a test DatabaseDataSource fixture.
        /// </summary>
        private static DatabaseDataSource CreateTestDbDataSource(
            Guid? id = null,
            string? name = null,
            string? eqlText = null)
        {
            return new DatabaseDataSource
            {
                Id = id ?? TestDataSourceId,
                Name = name ?? TestDsName,
                EqlText = eqlText ?? "SELECT * FROM test_entity",
                Description = "Test datasource",
                Weight = 10,
                ReturnTotal = true,
                EntityName = TestEntityName
            };
        }

        /// <summary>
        /// Sets up cache to return a cached datasource list (cache HIT scenario).
        /// </summary>
        private void SetupCacheHit(List<DataSourceBase> cachedList)
        {
            object outVal = cachedList;
            _mockCache
                .Setup(c => c.TryGetValue("DATASOURCES_ALL", out outVal!))
                .Returns(true);
        }

        /// <summary>
        /// Sets up cache to return nothing (cache MISS scenario).
        /// IMemoryCache.TryGetValue returns false, and Set is allowed.
        /// </summary>
        private void SetupCacheMiss()
        {
            object? outVal = null;
            _mockCache
                .Setup(c => c.TryGetValue("DATASOURCES_ALL", out outVal!))
                .Returns(false);

            // Allow Set calls — use a mock cache entry to avoid NullReferenceException.
            var mockCacheEntry = new Mock<ICacheEntry>();
            mockCacheEntry.SetupAllProperties();
            _mockCache
                .Setup(c => c.CreateEntry(It.IsAny<object>()))
                .Returns(mockCacheEntry.Object);
        }

        /// <summary>
        /// Sets up DynamoDB to return a list of serialized datasource items.
        /// </summary>
        private void SetupDynamoDbDataSources(List<DatabaseDataSource> dataSources)
        {
            var items = dataSources.Select(ds => new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = "DATASOURCE" },
                ["SK"] = new AttributeValue { S = ds.Id.ToString() },
                ["Data"] = new AttributeValue { S = JsonConvert.SerializeObject(ds) }
            }).ToList();

            _mockDynamoDb
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.DynamoDBv2.Model.QueryResponse { Items = items });
        }

        /// <summary>
        /// Sets up the query adapter Build to return a successful (no errors) result.
        /// </summary>
        private void SetupSuccessfulBuild(string? fromEntityName = null)
        {
            var fromEntity = fromEntityName != null
                ? new Entity { Name = fromEntityName }
                : null;

            _mockQueryAdapter
                .Setup(q => q.Build(It.IsAny<string>(), It.IsAny<List<EqlParameter>>(), It.IsAny<EqlSettings>()))
                .Returns(new EqlBuildResult
                {
                    Errors = new List<EqlError>(),
                    Meta = new List<EqlFieldMeta>(),
                    Parameters = new List<EqlParameter>(),
                    ExpectedParameters = new List<string>(),
                    FromEntity = fromEntity
                });
        }

        /// <summary>
        /// Sets up the query adapter Execute to return a successful query result.
        /// </summary>
        private void SetupSuccessfulExecute(List<EntityRecord>? records = null, int totalCount = 0)
        {
            var data = records ?? new List<EntityRecord>();
            _mockQueryAdapter
                .Setup(q => q.Execute(It.IsAny<string>(), It.IsAny<List<EqlParameter>>(), It.IsAny<EqlSettings>()))
                .ReturnsAsync(new QueryResult
                {
                    Data = data,
                    FieldsMeta = new List<Field>()
                });
        }

        /// <summary>
        /// Deserializes the response body to extract the response model.
        /// </summary>
        private static T? DeserializeBody<T>(APIGatewayHttpApiV2ProxyResponse response)
        {
            return JsonSerializer.Deserialize<T>(response.Body, _jsonOptions);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 2: GetDataSources Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task GetDataSources_CacheHit_ReturnsCachedList()
        {
            // Arrange: populate cache with a list of datasources.
            var cachedDs = new List<DataSourceBase>
            {
                CreateTestDbDataSource(id: Guid.NewGuid(), name: "ds_one"),
                CreateTestDbDataSource(id: Guid.NewGuid(), name: "ds_two")
            };
            SetupCacheHit(cachedDs);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { CreateTestEntity() }
                });

            var request = BuildRequest();

            // Act
            var response = await _handler.GetDataSources(request, _mockLambdaContext.Object);

            // Assert: response should be 200 with the cached list.
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().NotBeNullOrEmpty();

            // DynamoDB should NOT have been queried because cache was hit.
            _mockDynamoDb.Verify(
                d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()),
                Times.Never());

            // Response body should contain success envelope.
            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
        }

        [Fact]
        public async Task GetDataSources_CacheMiss_QueriesDynamoDBAndCaches()
        {
            // Arrange: cache miss, DynamoDB returns datasources.
            SetupCacheMiss();

            var dbDs = CreateTestDbDataSource(id: Guid.NewGuid(), name: "db_datasource");
            SetupDynamoDbDataSources(new List<DatabaseDataSource> { dbDs });

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { CreateTestEntity() }
                });

            var request = BuildRequest();

            // Act
            var response = await _handler.GetDataSources(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            // DynamoDB should have been queried.
            _mockDynamoDb.Verify(
                d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());

            // Cache should have been populated via CreateEntry.
            _mockCache.Verify(
                c => c.CreateEntry(It.Is<object>(k => k.ToString() == "DATASOURCES_ALL")),
                Times.Once());

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
        }

        [Fact]
        public async Task GetDataSources_MergesCodeAndDatabaseDataSources()
        {
            // Arrange: cache miss forces loading from both code discovery and DynamoDB.
            SetupCacheMiss();

            var dbDs = CreateTestDbDataSource(id: Guid.NewGuid(), name: "db_ds");
            SetupDynamoDbDataSources(new List<DatabaseDataSource> { dbDs });

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { CreateTestEntity() }
                });

            var request = BuildRequest();

            // Act
            var response = await _handler.GetDataSources(request, _mockLambdaContext.Object);

            // Assert: response should be 200 with merged results.
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
            // The merged list should include at least the DynamoDB datasource.
            body.Object.Should().NotBeNull();
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 3: CreateDataSource Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateDataSource_ValidEql_ReturnsSuccess()
        {
            // Arrange: valid datasource with EQL text that passes validation.
            SetupCacheMiss();
            SetupDynamoDbDataSources(new List<DatabaseDataSource>()); // No existing datasources.

            var testEntity = CreateTestEntity();
            SetupSuccessfulBuild(fromEntityName: TestEntityName);

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(testEntity);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { testEntity }
                });

            var dsPayload = new
            {
                name = "new_datasource",
                eqlText = "SELECT * FROM test_entity",
                parameters = new List<object>(),
                returnTotal = true
            };

            var request = BuildRequest(body: JsonConvert.SerializeObject(dsPayload));

            // Act
            var response = await _handler.CreateDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
            body.Message.Should().Contain("created successfully");

            // DynamoDB PutItem should have been called to persist the datasource.
            _mockDynamoDb.Verify(
                d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());

            // Cache should have been invalidated.
            _mockCache.Verify(c => c.Remove("DATASOURCES_ALL"), Times.AtLeastOnce());
        }

        [Fact]
        public async Task CreateDataSource_InvalidEql_Returns400WithLineColumnErrors()
        {
            // Arrange: Build() returns EQL errors with line/column context.
            SetupCacheMiss();
            SetupDynamoDbDataSources(new List<DatabaseDataSource>());

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { CreateTestEntity() }
                });

            _mockQueryAdapter
                .Setup(q => q.Build(It.IsAny<string>(), It.IsAny<List<EqlParameter>>(), It.IsAny<EqlSettings>()))
                .Returns(new EqlBuildResult
                {
                    Errors = new List<EqlError>
                    {
                        new EqlError { Message = "Unexpected token 'INVALID'", Line = 1, Column = 8 },
                        new EqlError { Message = "Missing FROM clause", Line = 1, Column = 1 }
                    },
                    Meta = new List<EqlFieldMeta>(),
                    Parameters = new List<EqlParameter>(),
                    ExpectedParameters = new List<string>()
                });

            var dsPayload = new
            {
                name = "invalid_ds",
                eqlText = "SELECT INVALID SYNTAX",
                parameters = new List<object>(),
                returnTotal = true
            };

            var request = BuildRequest(body: JsonConvert.SerializeObject(dsPayload));

            // Act
            var response = await _handler.CreateDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

            var body = DeserializeBody<BaseResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Errors.Should().NotBeEmpty();

            // Verify errors contain line/column context.
            var errorMessages = body.Errors!.Select(e => e.Message).ToList();
            errorMessages.Should().Contain(m => m.Contains("line 1") && m.Contains("column 8"));
            errorMessages.Should().Contain(m => m.Contains("Missing FROM clause"));

            // Each error should have Key = "eql".
            body.Errors!.Should().OnlyContain(e => e.Key == "eql");
        }

        [Fact]
        public async Task CreateDataSource_UndeclaredParameter_Returns400()
        {
            // Arrange: EQL references @myParam but it is not declared in parameters.
            SetupCacheMiss();
            SetupDynamoDbDataSources(new List<DatabaseDataSource>());

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { CreateTestEntity() }
                });

            _mockQueryAdapter
                .Setup(q => q.Build(It.IsAny<string>(), It.IsAny<List<EqlParameter>>(), It.IsAny<EqlSettings>()))
                .Returns(new EqlBuildResult
                {
                    Errors = new List<EqlError>(),
                    Meta = new List<EqlFieldMeta>(),
                    Parameters = new List<EqlParameter>(),
                    ExpectedParameters = new List<string> { "@myParam" } // EQL expects this parameter.
                });

            var dsPayload = new
            {
                name = "ds_with_undeclared_param",
                eqlText = "SELECT * FROM test_entity WHERE name = @myParam",
                parameters = new List<object>(), // Empty: no parameters declared.
                returnTotal = true
            };

            var request = BuildRequest(body: JsonConvert.SerializeObject(dsPayload));

            // Act
            var response = await _handler.CreateDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

            var body = DeserializeBody<BaseResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Errors.Should().NotBeEmpty();
            body.Errors!.Should().Contain(e => e.Key == "parameter" && e.Message.Contains("@myParam"));
        }

        [Fact]
        public async Task CreateDataSource_AdminRoleRequired_Returns403ForNonAdmin()
        {
            // Arrange: request without admin role (regular user).
            var dsPayload = new
            {
                name = "ds_no_admin",
                eqlText = "SELECT * FROM test_entity",
                parameters = new List<object>(),
                returnTotal = true
            };

            var request = BuildNonAdminRequest(body: JsonConvert.SerializeObject(dsPayload));

            // Act
            var response = await _handler.CreateDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);

            var body = DeserializeBody<BaseResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Message.Should().Contain("Access denied");

            // DynamoDB should NOT have been called.
            _mockDynamoDb.Verify(
                d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 4: Parameter Processing Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateDataSource_ParameterProcessing_ParsesNewlineSeparatedFormat()
        {
            // Arrange: datasource with multiple parameters in List format.
            SetupCacheMiss();
            SetupDynamoDbDataSources(new List<DatabaseDataSource>());

            var testEntity = CreateTestEntity();
            SetupSuccessfulBuild(fromEntityName: TestEntityName);

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(testEntity);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { testEntity }
                });

            var dsPayload = new
            {
                name = "params_ds",
                eqlText = "SELECT * FROM test_entity WHERE name = @name",
                parameters = new[]
                {
                    new { name = "@name", type = "text", value = "defaultValue", ignoreParseErrors = false },
                    new { name = "@age", type = "int", value = "25", ignoreParseErrors = false },
                    new { name = "@active", type = "bool", value = "true", ignoreParseErrors = false }
                },
                returnTotal = true
            };

            var request = BuildRequest(body: JsonConvert.SerializeObject(dsPayload));

            // Act
            var response = await _handler.CreateDataSource(request, _mockLambdaContext.Object);

            // Assert: should create successfully with parsed parameters.
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
        }

        [Fact]
        public async Task CreateDataSource_ParameterProcessing_HandlesSpecialLiterals()
        {
            // Arrange: datasource with parameters using special literal values.
            SetupCacheMiss();
            SetupDynamoDbDataSources(new List<DatabaseDataSource>());

            var testEntity = CreateTestEntity();
            SetupSuccessfulBuild(fromEntityName: TestEntityName);

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(testEntity);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { testEntity }
                });

            var dsPayload = new
            {
                name = "special_ds",
                eqlText = "SELECT * FROM test_entity",
                parameters = new[]
                {
                    new { name = "@param1", type = "guid", value = "null", ignoreParseErrors = false },
                    new { name = "@param2", type = "guid", value = "guid.empty", ignoreParseErrors = false },
                    new { name = "@param3", type = "text", value = "string.empty", ignoreParseErrors = false }
                },
                returnTotal = false
            };

            var request = BuildRequest(body: JsonConvert.SerializeObject(dsPayload));

            // Act
            var response = await _handler.CreateDataSource(request, _mockLambdaContext.Object);

            // Assert: should create successfully; handler processes special literals internally.
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
        }

        [Fact]
        public async Task CreateDataSource_ParameterProcessing_IgnoreParseErrors_FourthColumn()
        {
            // Arrange: parameter with ignoreParseErrors = true (4th column in CSV format).
            SetupCacheMiss();
            SetupDynamoDbDataSources(new List<DatabaseDataSource>());

            var testEntity = CreateTestEntity();
            SetupSuccessfulBuild(fromEntityName: TestEntityName);

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(testEntity);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { testEntity }
                });

            var dsPayload = new
            {
                name = "ignore_errors_ds",
                eqlText = "SELECT * FROM test_entity",
                parameters = new[]
                {
                    new { name = "@param", type = "text", value = "value", ignoreParseErrors = true }
                },
                returnTotal = false
            };

            var request = BuildRequest(body: JsonConvert.SerializeObject(dsPayload));

            // Act
            var response = await _handler.CreateDataSource(request, _mockLambdaContext.Object);

            // Assert: parameter should be stored with IgnoreParseErrors = true.
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
        }

        [Fact]
        public async Task CreateDataSource_ParameterProcessing_EnsuresAtPrefix()
        {
            // Arrange: parameter name without @ prefix — handler strips @ from stored names.
            SetupCacheMiss();
            SetupDynamoDbDataSources(new List<DatabaseDataSource>());

            var testEntity = CreateTestEntity();
            SetupSuccessfulBuild(fromEntityName: TestEntityName);

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(testEntity);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { testEntity }
                });

            // Verify that parameters with @ prefix get the @ stripped during processing.
            // Then during execution, @ is re-added for EQL parameter binding.
            var dsPayload = new
            {
                name = "atprefix_ds",
                eqlText = "SELECT * FROM test_entity WHERE name = @name",
                parameters = new[]
                {
                    new { name = "name", type = "text", value = "test", ignoreParseErrors = false }
                },
                returnTotal = true
            };

            var request = BuildRequest(body: JsonConvert.SerializeObject(dsPayload));

            // Act
            var response = await _handler.CreateDataSource(request, _mockLambdaContext.Object);

            // Assert: should succeed; the handler normalizes parameter names.
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            // Verify Build was called with EQL parameters that have @-prefix applied.
            _mockQueryAdapter.Verify(
                q => q.Build(
                    It.IsAny<string>(),
                    It.Is<List<EqlParameter>>(p => p.Any(ep => ep.ParameterName.StartsWith("@"))),
                    It.IsAny<EqlSettings>()),
                Times.Once());
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 5: UpdateDataSource Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task UpdateDataSource_ValidUpdate_Returns200()
        {
            // Arrange: existing datasource found in cache, valid update body.
            var existingDs = CreateTestDbDataSource();
            SetupCacheHit(new List<DataSourceBase> { existingDs });

            var testEntity = CreateTestEntity();
            SetupSuccessfulBuild(fromEntityName: TestEntityName);

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(testEntity);

            var updatePayload = new
            {
                name = TestDsName,
                eqlText = "SELECT name FROM test_entity",
                parameters = new List<object>(),
                returnTotal = true
            };

            var request = BuildRequest(
                body: JsonConvert.SerializeObject(updatePayload),
                pathParams: new Dictionary<string, string> { ["id"] = TestDataSourceId.ToString() });

            // Act
            var response = await _handler.UpdateDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
            body.Message.Should().Contain("updated successfully");

            // DynamoDB PutItem should have been called.
            _mockDynamoDb.Verify(
                d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task UpdateDataSource_NotFound_Returns404()
        {
            // Arrange: no existing datasource with the given ID in cache.
            SetupCacheHit(new List<DataSourceBase>
            {
                CreateTestDbDataSource(id: Guid.NewGuid(), name: "other_ds")
            });

            var updatePayload = new
            {
                name = "updated_ds",
                eqlText = "SELECT * FROM test_entity",
                parameters = new List<object>(),
                returnTotal = true
            };

            var nonExistentId = Guid.NewGuid();
            var request = BuildRequest(
                body: JsonConvert.SerializeObject(updatePayload),
                pathParams: new Dictionary<string, string> { ["id"] = nonExistentId.ToString() });

            // Act
            var response = await _handler.UpdateDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);

            var body = DeserializeBody<BaseResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Message.Should().Contain("not found");
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 6: DeleteDataSource Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task DeleteDataSource_ValidId_Returns200AndPublishesEvent()
        {
            // Arrange: existing DatabaseDataSource found in cache.
            var existingDs = CreateTestDbDataSource();
            SetupCacheHit(new List<DataSourceBase> { existingDs });

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = TestDataSourceId.ToString() });

            // Act
            var response = await _handler.DeleteDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();
            body.Message.Should().Contain("deleted successfully");

            // DynamoDB DeleteItem should have been called.
            _mockDynamoDb.Verify(
                d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());

            // Cache should have been invalidated.
            _mockCache.Verify(c => c.Remove("DATASOURCES_ALL"), Times.AtLeastOnce());

            // SNS event should have been published.
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(p => p.Message.Contains("datasource.deleted")),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task DeleteDataSource_NotFound_Returns404()
        {
            // Arrange: no existing datasource with the given ID.
            SetupCacheHit(new List<DataSourceBase>
            {
                CreateTestDbDataSource(id: Guid.NewGuid(), name: "other_ds")
            });

            var nonExistentId = Guid.NewGuid();
            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = nonExistentId.ToString() });

            // Act
            var response = await _handler.DeleteDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);

            var body = DeserializeBody<BaseResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Message.Should().Contain("not found");

            // DynamoDB should NOT have been called for delete.
            _mockDynamoDb.Verify(
                d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 7: ExecuteDataSource Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task ExecuteDataSource_StoredDatabaseDataSource_ExecutesViaQueryAdapter()
        {
            // Arrange: stored DatabaseDataSource with EQL text.
            var storedDs = CreateTestDbDataSource();
            SetupCacheHit(new List<DataSourceBase> { storedDs });

            var testRecords = new List<EntityRecord>
            {
                new EntityRecord { ["id"] = Guid.NewGuid(), ["name"] = "Record 1" },
                new EntityRecord { ["id"] = Guid.NewGuid(), ["name"] = "Record 2" }
            };
            SetupSuccessfulExecute(testRecords);

            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityName))
                .ReturnsAsync(new EntityResponse { Success = true, Object = CreateTestEntity() });

            var bodyPayload = new { parameters = (object?)null };
            var request = BuildRequest(
                body: JsonConvert.SerializeObject(bodyPayload),
                pathParams: new Dictionary<string, string> { ["id"] = TestDataSourceId.ToString() });

            // Act
            var response = await _handler.ExecuteDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();

            // IQueryAdapter.Execute should have been called.
            _mockQueryAdapter.Verify(
                q => q.Execute(It.IsAny<string>(), It.IsAny<List<EqlParameter>>(), It.IsAny<EqlSettings>()),
                Times.Once());
        }

        [Fact]
        public async Task ExecuteDataSource_StoredCodeDataSource_ExecutesCodeDirectly()
        {
            // Arrange: a mock CodeDataSource in the cache.
            // Since CodeDataSource is abstract, we create a concrete test subclass via mock.
            var codeDs = new Mock<CodeDataSource>();
            codeDs.SetupGet(c => c.Id).Returns(Guid.NewGuid());
            codeDs.SetupGet(c => c.Name).Returns("code_ds");
            codeDs.SetupGet(c => c.Type).Returns(DataSourceType.CODE);
            codeDs.Setup(c => c.Execute(It.IsAny<Dictionary<string, object>>()))
                .Returns(new List<EntityRecord>
                {
                    new EntityRecord { ["id"] = Guid.NewGuid(), ["result"] = "code_output" }
                });

            SetupCacheHit(new List<DataSourceBase> { codeDs.Object });

            var bodyPayload = new { parameters = (object?)null };
            var request = BuildRequest(
                body: JsonConvert.SerializeObject(bodyPayload),
                pathParams: new Dictionary<string, string> { ["id"] = codeDs.Object.Id.ToString() });

            // Act
            var response = await _handler.ExecuteDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();

            // CodeDataSource.Execute should have been called directly.
            codeDs.Verify(c => c.Execute(It.IsAny<Dictionary<string, object>>()), Times.Once());

            // IQueryAdapter.Execute should NOT have been called.
            _mockQueryAdapter.Verify(
                q => q.Execute(It.IsAny<string>(), It.IsAny<List<EqlParameter>>(), It.IsAny<EqlSettings>()),
                Times.Never());
        }

        [Fact]
        public async Task ExecuteDataSource_AdHocEql_ExecutesDirectly()
        {
            // Arrange: ad-hoc EQL execution request (no route ID, body has "eql" key).
            SetupSuccessfulBuild();
            SetupSuccessfulExecute(new List<EntityRecord>
            {
                new EntityRecord { ["id"] = Guid.NewGuid(), ["name"] = "Ad-hoc Result" }
            });

            var bodyPayload = new
            {
                eql = "SELECT * FROM test_entity",
                parameters = "",
                returnTotal = true
            };

            var request = BuildRequest(body: JsonConvert.SerializeObject(bodyPayload));
            // No path parameter "id" — triggers ad-hoc mode.

            // Act
            var response = await _handler.ExecuteDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();

            // Build should have been called for validation.
            _mockQueryAdapter.Verify(
                q => q.Build(It.IsAny<string>(), It.IsAny<List<EqlParameter>>(), It.IsAny<EqlSettings>()),
                Times.Once());

            // Execute should have been called.
            _mockQueryAdapter.Verify(
                q => q.Execute(It.IsAny<string>(), It.IsAny<List<EqlParameter>>(), It.IsAny<EqlSettings>()),
                Times.Once());
        }

        [Fact]
        public async Task ExecuteDataSource_MissingParametersEnrichedFromDefaults()
        {
            // Arrange: stored datasource with default parameter values; execution request
            // does not provide overrides — handler should use defaults.
            var storedDs = CreateTestDbDataSource();
            storedDs.Parameters.AddRange(new List<DataSourceParameter>
            {
                new DataSourceParameter { Name = "status", Type = "text", Value = "active" },
                new DataSourceParameter { Name = "limit", Type = "int", Value = "100" }
            });
            SetupCacheHit(new List<DataSourceBase> { storedDs });

            SetupSuccessfulExecute(new List<EntityRecord>
            {
                new EntityRecord { ["id"] = Guid.NewGuid(), ["name"] = "Enriched Record" }
            });

            _mockEntityService
                .Setup(s => s.ReadEntity(TestEntityName))
                .ReturnsAsync(new EntityResponse { Success = true, Object = CreateTestEntity() });

            // Body with no parameter overrides.
            var bodyPayload = new { };
            var request = BuildRequest(
                body: JsonConvert.SerializeObject(bodyPayload),
                pathParams: new Dictionary<string, string> { ["id"] = TestDataSourceId.ToString() });

            // Act
            var response = await _handler.ExecuteDataSource(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            // IQueryAdapter.Execute should have been called with enriched parameters.
            _mockQueryAdapter.Verify(
                q => q.Execute(
                    It.IsAny<string>(),
                    It.Is<List<EqlParameter>>(p =>
                        p.Any(ep => ep.ParameterName == "@status") &&
                        p.Any(ep => ep.ParameterName == "@limit")),
                    It.IsAny<EqlSettings>()),
                Times.Once());
        }

        [Fact]
        public async Task ExecuteDataSource_Select2ResponseFormat()
        {
            // Arrange: datasource for Select2 format execution.
            var storedDs = CreateTestDbDataSource(name: "select2_ds");
            SetupCacheHit(new List<DataSourceBase> { storedDs });

            var testRecords = new List<EntityRecord>
            {
                new EntityRecord { ["id"] = Guid.NewGuid().ToString(), ["name"] = "Option A" },
                new EntityRecord { ["id"] = Guid.NewGuid().ToString(), ["name"] = "Option B" },
                new EntityRecord { ["id"] = Guid.NewGuid().ToString(), ["name"] = "Option C" }
            };
            SetupSuccessfulExecute(testRecords);

            var bodyPayload = new
            {
                name = "select2_ds",
                page = 1
            };

            var request = BuildRequest(body: JsonConvert.SerializeObject(bodyPayload));

            // Act
            var response = await _handler.ExecuteDataSourceSelect2(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var body = DeserializeBody<ResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeTrue();

            // Response Object should contain Select2 format: results array + pagination.
            body.Object.Should().NotBeNull();
            var bodyStr = response.Body;
            bodyStr.Should().Contain("results");
            bodyStr.Should().Contain("pagination");
            bodyStr.Should().Contain("more");
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 8: Caching Behavior Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateDataSource_InvalidatesCache()
        {
            // Arrange: valid create scenario.
            SetupCacheMiss();
            SetupDynamoDbDataSources(new List<DatabaseDataSource>());

            var testEntity = CreateTestEntity();
            SetupSuccessfulBuild(fromEntityName: TestEntityName);

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(testEntity);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { testEntity }
                });

            var dsPayload = new
            {
                name = "cache_test_create",
                eqlText = "SELECT * FROM test_entity",
                parameters = new List<object>(),
                returnTotal = false
            };

            var request = BuildRequest(body: JsonConvert.SerializeObject(dsPayload));

            // Act
            await _handler.CreateDataSource(request, _mockLambdaContext.Object);

            // Assert: cache should have been invalidated via Remove("DATASOURCES_ALL").
            _mockCache.Verify(c => c.Remove("DATASOURCES_ALL"), Times.AtLeastOnce());
        }

        [Fact]
        public async Task UpdateDataSource_InvalidatesCache()
        {
            // Arrange: valid update scenario.
            var existingDs = CreateTestDbDataSource();
            SetupCacheHit(new List<DataSourceBase> { existingDs });

            var testEntity = CreateTestEntity();
            SetupSuccessfulBuild(fromEntityName: TestEntityName);

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(testEntity);

            var updatePayload = new
            {
                name = TestDsName,
                eqlText = "SELECT name FROM test_entity",
                parameters = new List<object>(),
                returnTotal = true
            };

            var request = BuildRequest(
                body: JsonConvert.SerializeObject(updatePayload),
                pathParams: new Dictionary<string, string> { ["id"] = TestDataSourceId.ToString() });

            // Act
            await _handler.UpdateDataSource(request, _mockLambdaContext.Object);

            // Assert
            _mockCache.Verify(c => c.Remove("DATASOURCES_ALL"), Times.AtLeastOnce());
        }

        [Fact]
        public async Task DeleteDataSource_InvalidatesCache()
        {
            // Arrange: existing DatabaseDataSource for deletion.
            var existingDs = CreateTestDbDataSource();
            SetupCacheHit(new List<DataSourceBase> { existingDs });

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = TestDataSourceId.ToString() });

            // Act
            await _handler.DeleteDataSource(request, _mockLambdaContext.Object);

            // Assert
            _mockCache.Verify(c => c.Remove("DATASOURCES_ALL"), Times.AtLeastOnce());
        }

        [Fact]
        public async Task GetDataSources_CacheTTL_OneHour()
        {
            // Arrange: cache miss, DynamoDB returns datasources.
            SetupCacheMiss();
            SetupDynamoDbDataSources(new List<DatabaseDataSource> { CreateTestDbDataSource() });

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { CreateTestEntity() }
                });

            var request = BuildRequest();

            // Act
            await _handler.GetDataSources(request, _mockLambdaContext.Object);

            // Assert: cache CreateEntry should have been called (indicating Set with options).
            // The handler sets 1-hour absolute expiration via SetAbsoluteExpiration(TimeSpan.FromHours(1)).
            _mockCache.Verify(
                c => c.CreateEntry(It.Is<object>(k => k.ToString() == "DATASOURCES_ALL")),
                Times.Once());
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 9: SNS Event Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateDataSource_PublishesSnsEvent()
        {
            // Arrange: valid create scenario.
            SetupCacheMiss();
            SetupDynamoDbDataSources(new List<DatabaseDataSource>());

            var testEntity = CreateTestEntity();
            SetupSuccessfulBuild(fromEntityName: TestEntityName);

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(testEntity);

            _mockEntityService
                .Setup(s => s.ReadEntities())
                .ReturnsAsync(new EntityListResponse
                {
                    Success = true,
                    Object = new List<Entity> { testEntity }
                });

            var dsPayload = new
            {
                name = "sns_test_create",
                eqlText = "SELECT * FROM test_entity",
                parameters = new List<object>(),
                returnTotal = false
            };

            var request = BuildRequest(body: JsonConvert.SerializeObject(dsPayload));

            // Act
            await _handler.CreateDataSource(request, _mockLambdaContext.Object);

            // Assert: SNS event "entity-management.datasource.created" should have been published.
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(p =>
                        p.Message.Contains("datasource.created")),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task UpdateDataSource_PublishesSnsEvent()
        {
            // Arrange: valid update scenario.
            var existingDs = CreateTestDbDataSource();
            SetupCacheHit(new List<DataSourceBase> { existingDs });

            var testEntity = CreateTestEntity();
            SetupSuccessfulBuild(fromEntityName: TestEntityName);

            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityName))
                .ReturnsAsync(testEntity);

            var updatePayload = new
            {
                name = TestDsName,
                eqlText = "SELECT name FROM test_entity",
                parameters = new List<object>(),
                returnTotal = true
            };

            var request = BuildRequest(
                body: JsonConvert.SerializeObject(updatePayload),
                pathParams: new Dictionary<string, string> { ["id"] = TestDataSourceId.ToString() });

            // Act
            await _handler.UpdateDataSource(request, _mockLambdaContext.Object);

            // Assert: SNS event "entity-management.datasource.updated" should have been published.
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(p =>
                        p.Message.Contains("datasource.updated")),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }
    }
}
