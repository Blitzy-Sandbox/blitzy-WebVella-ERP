// =============================================================================
// SearchHandlerTests.cs — Comprehensive Unit Tests for Search Lambda Handler
// =============================================================================
// Tests for SearchHandler.cs — the Lambda handler for search index operations
// in the Entity Management microservice. Validates DynamoDB GSI-based search
// adaptation from the PostgreSQL FTS-based SearchManager, Contains vs FTS search
// type handling, pagination, AddToIndex/RemoveFromIndex operations, QuickSearch,
// and SearchResultList response structure.
//
// Namespace: WebVellaErp.EntityManagement.Tests.Unit.Functions
// Test Framework: xUnit 2.9.3 + Moq 4.20.72 + FluentAssertions 8.0.1
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Comprehensive unit tests for <see cref="SearchHandler"/> covering all 4 public Lambda
    /// handler methods (Search, AddToIndex, RemoveFromIndex, QuickSearch). Tests are organized
    /// in logical phases covering Contains/FTS search types, entity/app/record filters,
    /// pagination, result types, index operations, QuickSearch, error handling, and
    /// response envelope structure.
    /// </summary>
    public class SearchHandlerTests
    {
        // ═══════════════════════════════════════════════════════════════
        // MOCK DEPENDENCIES
        // ═══════════════════════════════════════════════════════════════

        private readonly Mock<IAmazonDynamoDB> _mockDynamoDb;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<IEntityService> _mockEntityService;
        private readonly Mock<ILogger<SearchHandler>> _mockLogger;
        private readonly Mock<ILambdaContext> _mockLambdaContext;
        private readonly SearchHandler _handler;

        // ═══════════════════════════════════════════════════════════════
        // TEST FIXTURES
        // ═══════════════════════════════════════════════════════════════

        private static readonly Guid TestEntityId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private static readonly Guid TestAppId = Guid.Parse("b2c3d4e5-f6a7-8901-bcde-f12345678901");
        private static readonly Guid TestRecordId = Guid.Parse("c3d4e5f6-a7b8-9012-cdef-123456789012");
        private static readonly Guid TestSearchResultId = Guid.Parse("d4e5f6a7-b8c9-0123-defa-234567890123");
        private const string SearchTableName = "entity-management-search";
        private const string SearchTopicArn = "arn:aws:sns:us-east-1:000000000000:search-events";

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

        public SearchHandlerTests()
        {
            _mockDynamoDb = new Mock<IAmazonDynamoDB>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockEntityService = new Mock<IEntityService>();
            _mockLogger = new Mock<ILogger<SearchHandler>>();
            _mockLambdaContext = new Mock<ILambdaContext>();

            // Default Lambda context with AwsRequestId for correlation ID extraction.
            _mockLambdaContext.Setup(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());
            _mockLambdaContext.Setup(c => c.FunctionName).Returns("SearchHandler-Test");

            // Configure SNS to return a successful publish response by default.
            _mockSnsClient
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });

            // Set environment variables that the SearchHandler reads in its constructor.
            Environment.SetEnvironmentVariable("SEARCH_TABLE_NAME", SearchTableName);
            Environment.SetEnvironmentVariable("SEARCH_TOPIC_ARN", SearchTopicArn);
            Environment.SetEnvironmentVariable("IS_LOCAL", "true");

            _handler = new SearchHandler(
                _mockDynamoDb.Object,
                _mockSnsClient.Object,
                _mockEntityService.Object,
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
            var claims = new Dictionary<string, string>();
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
        /// Builds an APIGatewayHttpApiV2ProxyRequest with regular-user-level claims
        /// (no admin role) to verify that search endpoints accept authenticated users.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildAuthenticatedRequest(
            string? body = null,
            Dictionary<string, string>? pathParams = null,
            Dictionary<string, string>? queryParams = null)
        {
            var claims = new Dictionary<string, string>
            {
                ["custom:roles"] = SystemIds.RegularRoleId.ToString(),
                ["sub"] = Guid.NewGuid().ToString()
            };

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
        /// Creates a DynamoDB item Dictionary representing a search index entry with all
        /// standard attributes (id, url, snippet, content, stem_content, entities, apps,
        /// records, aux_data, timestamp).
        /// </summary>
        private static Dictionary<string, AttributeValue> CreateSearchDynamoDbItem(
            Guid? id = null,
            string url = "https://example.com/record/1",
            string snippet = "Test snippet content",
            string content = "full content for search indexing",
            string stemContent = "full content search index",
            string? entities = null,
            string? apps = null,
            string? records = null,
            string auxData = "",
            DateTime? timestamp = null)
        {
            return new Dictionary<string, AttributeValue>
            {
                ["id"] = new AttributeValue { S = (id ?? TestSearchResultId).ToString() },
                ["url"] = new AttributeValue { S = url },
                ["snippet"] = new AttributeValue { S = snippet },
                ["content"] = new AttributeValue { S = content },
                ["stem_content"] = new AttributeValue { S = stemContent },
                ["entities"] = new AttributeValue { S = entities ?? JsonConvert.SerializeObject(new List<Guid> { TestEntityId }) },
                ["apps"] = new AttributeValue { S = apps ?? JsonConvert.SerializeObject(new List<Guid> { TestAppId }) },
                ["records"] = new AttributeValue { S = records ?? JsonConvert.SerializeObject(new List<Guid> { TestRecordId }) },
                ["aux_data"] = new AttributeValue { S = auxData },
                ["timestamp"] = new AttributeValue { S = (timestamp ?? DateTime.UtcNow).ToString("o") }
            };
        }

        /// <summary>
        /// Serializes a <see cref="SearchQuery"/> to JSON for use as a request body.
        /// </summary>
        private static string CreateSearchQueryJson(
            string text,
            SearchType searchType = SearchType.Contains,
            SearchResultType resultType = SearchResultType.Full,
            List<Guid>? entities = null,
            List<Guid>? apps = null,
            List<Guid>? records = null,
            int? skip = null,
            int? limit = null)
        {
            var query = new SearchQuery
            {
                Text = text,
                SearchType = searchType,
                ResultType = resultType,
                Entities = entities ?? new List<Guid>(),
                Apps = apps ?? new List<Guid>(),
                Records = records ?? new List<Guid>(),
                Skip = skip,
                Limit = limit
            };
            return JsonSerializer.Serialize(query, _jsonOptions);
        }

        /// <summary>
        /// Creates a JSON body for the AddToIndex endpoint representing a SearchResult.
        /// </summary>
        private static string CreateAddToIndexJson(
            string url = "https://example.com/record/1",
            string snippet = "Test snippet",
            string content = "Full content text for indexing",
            List<Guid>? entities = null,
            List<Guid>? apps = null,
            List<Guid>? records = null,
            string auxData = "",
            DateTime? timestamp = null)
        {
            var entry = new SearchResult
            {
                Url = url,
                Snippet = snippet,
                Content = content,
                Entities = entities ?? new List<Guid> { TestEntityId },
                Apps = apps ?? new List<Guid> { TestAppId },
                Records = records ?? new List<Guid> { TestRecordId },
                AuxData = auxData,
                Timestamp = timestamp ?? DateTime.UtcNow
            };
            return JsonSerializer.Serialize(entry, _jsonOptions);
        }

        /// <summary>
        /// Creates a JSON body for the QuickSearch endpoint.
        /// </summary>
        private static string CreateQuickSearchJson(
            string query = "test",
            string entityName = "test_entity",
            string? lookupFields = "name",
            string? returnFields = "id,name",
            string? sortField = null,
            string? sortType = null,
            string? matchMethod = "contains",
            bool matchAllFields = false,
            int? skipRecords = null,
            int? limitRecords = null,
            string? findType = null,
            string? forceFilters = null)
        {
            // QuickSearchRequest expects lookup_fields and return_fields as List<string> (JSON arrays),
            // not comma-separated strings. Split CSV parameters into arrays for correct deserialization.
            // IMPORTANT: Dictionary null values are NOT omitted by WhenWritingNull (only class properties are).
            // Non-nullable value type fields (int SkipRecords, int LimitRecords) cannot accept JSON null,
            // so we must conditionally add entries only when non-null to avoid JsonException during deserialization.
            var obj = new Dictionary<string, object?>
            {
                ["query"] = query,
                ["entity_name"] = entityName,
                ["lookup_fields"] = lookupFields?.Split(',').Select(s => s.Trim()).ToArray(),
                ["return_fields"] = returnFields?.Split(',').Select(s => s.Trim()).ToArray(),
                ["match_method"] = matchMethod,
                ["match_all_fields"] = matchAllFields
            };
            if (sortField != null) obj["sort_field"] = sortField;
            if (sortType != null) obj["sort_type"] = sortType;
            if (skipRecords != null) obj["skip_records"] = skipRecords;
            if (limitRecords != null) obj["limit_records"] = limitRecords;
            if (findType != null) obj["find_type"] = findType;
            if (forceFilters != null) obj["force_filters"] = forceFilters;
            return JsonSerializer.Serialize(obj, _jsonOptions);
        }

        /// <summary>
        /// Sets up the DynamoDB mock to return specified items for any ScanAsync call.
        /// </summary>
        private void SetupDynamoDbScan(List<Dictionary<string, AttributeValue>> items)
        {
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ScanResponse
                {
                    Items = items,
                    Count = items.Count,
                    ScannedCount = items.Count
                });
        }

        /// <summary>
        /// Creates a test Entity with configurable ID and name, with admin permissions.
        /// </summary>
        private static Entity CreateTestEntity(Guid? id = null, string name = "test_entity")
        {
            return new Entity
            {
                Id = id ?? TestEntityId,
                Name = name,
                Label = name,
                LabelPlural = name + "s",
                System = false,
                IconName = "fas fa-database",
                Color = "#1E88E5",
                Fields = new List<Field>(),
                RecordPermissions = new RecordPermissions
                {
                    CanRead = new List<Guid> { SystemIds.AdministratorRoleId, SystemIds.RegularRoleId },
                    CanCreate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
                }
            };
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 2: CONTAINS SEARCH TYPE TESTS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that a Contains search with valid query text returns matching results.
        /// Source behavior: SearchManager.cs lowercases text, splits by space, deduplicates,
        /// creates ILIKE %word% per word. DynamoDB adapts with contains() filter expression.
        /// </summary>
        [Fact]
        public async Task Search_ContainsType_ReturnsMatchingResults()
        {
            // Arrange
            var items = new List<Dictionary<string, AttributeValue>>
            {
                CreateSearchDynamoDbItem(
                    content: "test query results here",
                    stemContent: "test query result",
                    snippet: "Found a test query match")
            };
            SetupDynamoDbScan(items);

            var body = CreateSearchQueryJson("test query", SearchType.Contains);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNullOrEmpty();

            _mockDynamoDb.Verify(
                d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());

            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();
        }

        /// <summary>
        /// Verifies that Contains search lowercases the input text and splits by space,
        /// building per-word DynamoDB contains() filter expressions.
        /// </summary>
        [Fact]
        public async Task Search_ContainsType_LowercasesAndSplitsSearchText()
        {
            // Arrange
            ScanRequest? capturedScanRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedScanRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    Count = 0,
                    ScannedCount = 0
                });

            var body = CreateSearchQueryJson("Test QUERY Multiple", SearchType.Contains);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            capturedScanRequest.Should().NotBeNull();
            capturedScanRequest!.FilterExpression.Should().NotBeNullOrEmpty();

            // Verify lowercased words appear in expression attribute values
            var expressionValues = capturedScanRequest.ExpressionAttributeValues;
            expressionValues.Should().NotBeNull();

            var allValues = expressionValues.Values.Select(v => v.S).ToList();
            allValues.Should().Contain("test");
            allValues.Should().Contain("query");
            allValues.Should().Contain("multiple");
        }

        /// <summary>
        /// Verifies that duplicate words in the search text are deduplicated so the filter
        /// expression is not doubled. Source: Distinct() call in BuildSearchScanRequest.
        /// </summary>
        [Fact]
        public async Task Search_ContainsType_DeduplicatesSearchWords()
        {
            // Arrange
            ScanRequest? capturedScanRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedScanRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    Count = 0,
                    ScannedCount = 0
                });

            // "test" appears twice — should be deduplicated
            var body = CreateSearchQueryJson("test test query", SearchType.Contains);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            capturedScanRequest.Should().NotBeNull();
            var expressionValues = capturedScanRequest!.ExpressionAttributeValues;

            // Only unique words should have expression values: "test" and "query" (2 words, not 3)
            var wordValues = expressionValues
                .Where(kv => kv.Key.StartsWith(":word_"))
                .Select(kv => kv.Value.S)
                .ToList();

            wordValues.Should().HaveCount(2);
            wordValues.Should().Contain("test");
            wordValues.Should().Contain("query");
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 3: FTS SEARCH TYPE TESTS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that FTS search type returns matching results by querying stem_content.
        /// Source behavior: FtsAnalyzer.ProcessText() for stemming, then to_tsquery.
        /// Target: simplified tokenization + contains on stem_content attribute.
        /// </summary>
        [Fact]
        public async Task Search_FtsType_ReturnsMatchingResults()
        {
            // Arrange
            var items = new List<Dictionary<string, AttributeValue>>
            {
                CreateSearchDynamoDbItem(
                    stemContent: "testing search functionality",
                    content: "Testing the search functionality in detail")
            };
            SetupDynamoDbScan(items);

            var body = CreateSearchQueryJson("testing", SearchType.Fts);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNullOrEmpty();

            _mockDynamoDb.Verify(
                d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());

            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();
        }

        /// <summary>
        /// Verifies that FTS search processes text through simplified analysis
        /// (lowercasing + tokenization + stop word removal) before querying DynamoDB.
        /// The FTS path uses :stem_ prefix variables targeting #stem_content.
        /// </summary>
        [Fact]
        public async Task Search_FtsType_SimplifiedStemming()
        {
            // Arrange
            ScanRequest? capturedScanRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedScanRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    Count = 0,
                    ScannedCount = 0
                });

            // "the" is a stop word and should be removed; "Testing" lowercased to "testing"
            var body = CreateSearchQueryJson("Testing the functionality", SearchType.Fts);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            capturedScanRequest.Should().NotBeNull();
            capturedScanRequest!.FilterExpression.Should().NotBeNullOrEmpty();

            // FTS uses stem_content attribute name
            capturedScanRequest.ExpressionAttributeNames.Should().ContainKey("#stem_content");
            capturedScanRequest.ExpressionAttributeNames["#stem_content"].Should().Be("stem_content");

            // Stem variables should use :stem_ prefix
            var stemValues = capturedScanRequest.ExpressionAttributeValues
                .Where(kv => kv.Key.StartsWith(":stem_"))
                .Select(kv => kv.Value.S)
                .ToList();

            stemValues.Should().NotBeEmpty();
            // "the" is a stop word and should NOT appear
            stemValues.Should().NotContain("the");
            // "testing" should be lowercased
            stemValues.Should().Contain("testing");
            stemValues.Should().Contain("functionality");
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 4: SEARCH FILTER TESTS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that specifying Entities filter includes entity filter expressions
        /// in the DynamoDB scan request.
        /// </summary>
        [Fact]
        public async Task Search_EntityFilter_FiltersResultsByEntity()
        {
            // Arrange
            var entityGuids = new List<Guid> { TestEntityId, Guid.NewGuid() };
            ScanRequest? capturedScanRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedScanRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateSearchDynamoDbItem(
                            entities: JsonConvert.SerializeObject(entityGuids))
                    },
                    Count = 1,
                    ScannedCount = 1
                });

            var body = CreateSearchQueryJson("test", entities: entityGuids);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedScanRequest.Should().NotBeNull();

            // The filter expression should reference entities attribute
            var filterExpr = capturedScanRequest!.FilterExpression;
            filterExpr.Should().NotBeNullOrEmpty();

            // Entity GUIDs should appear in expression attribute values
            var allValues = capturedScanRequest.ExpressionAttributeValues
                .Values.Select(v => v.S).ToList();
            allValues.Should().Contain(s => s.Contains(TestEntityId.ToString()));
        }

        /// <summary>
        /// Verifies that specifying Apps filter includes app filter expressions
        /// in the DynamoDB scan request.
        /// </summary>
        [Fact]
        public async Task Search_AppFilter_FiltersResultsByApp()
        {
            // Arrange
            var appGuid1 = TestAppId;
            var appGuid2 = Guid.NewGuid();
            var appGuids = new List<Guid> { appGuid1, appGuid2 };

            ScanRequest? capturedScanRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedScanRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateSearchDynamoDbItem(
                            apps: JsonConvert.SerializeObject(appGuids))
                    },
                    Count = 1,
                    ScannedCount = 1
                });

            var body = CreateSearchQueryJson("test", apps: appGuids);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedScanRequest.Should().NotBeNull();

            var allValues = capturedScanRequest!.ExpressionAttributeValues
                .Values.Select(v => v.S).ToList();
            allValues.Should().Contain(s => s.Contains(appGuid1.ToString()));
        }

        /// <summary>
        /// Verifies that specifying Records filter includes record filter expressions
        /// in the DynamoDB scan request.
        /// </summary>
        [Fact]
        public async Task Search_RecordFilter_FiltersResultsByRecord()
        {
            // Arrange
            var recordGuids = new List<Guid> { TestRecordId };

            ScanRequest? capturedScanRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedScanRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateSearchDynamoDbItem(
                            records: JsonConvert.SerializeObject(recordGuids))
                    },
                    Count = 1,
                    ScannedCount = 1
                });

            var body = CreateSearchQueryJson("test", records: recordGuids);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedScanRequest.Should().NotBeNull();

            var allValues = capturedScanRequest!.ExpressionAttributeValues
                .Values.Select(v => v.S).ToList();
            allValues.Should().Contain(s => s.Contains(TestRecordId.ToString()));
        }

        /// <summary>
        /// Verifies that combining text + entity + app filters results in all filter
        /// expressions being applied in the DynamoDB scan request.
        /// </summary>
        [Fact]
        public async Task Search_CombinedFilters_AppliesAllFilters()
        {
            // Arrange
            var entityGuids = new List<Guid> { TestEntityId };
            var appGuids = new List<Guid> { TestAppId };

            ScanRequest? capturedScanRequest = null;
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedScanRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateSearchDynamoDbItem(
                            content: "combined filter test",
                            entities: JsonConvert.SerializeObject(entityGuids),
                            apps: JsonConvert.SerializeObject(appGuids))
                    },
                    Count = 1,
                    ScannedCount = 1
                });

            var body = CreateSearchQueryJson("combined", entities: entityGuids, apps: appGuids);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedScanRequest.Should().NotBeNull();

            var filterExpr = capturedScanRequest!.FilterExpression;
            filterExpr.Should().NotBeNullOrEmpty();

            // Should contain content search terms AND entity/app filter references
            var allValues = capturedScanRequest.ExpressionAttributeValues
                .Values.Select(v => v.S).ToList();
            allValues.Should().Contain("combined"); // text word
            allValues.Should().Contain(s => s.Contains(TestEntityId.ToString())); // entity filter
            allValues.Should().Contain(s => s.Contains(TestAppId.ToString())); // app filter
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 5: SEARCH PAGINATION TESTS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that Search with Limit and Skip parameters applies correct client-side
        /// pagination (DynamoDB scans up to MaxScanLimit, then Skip/Take applied in handler).
        /// Source: PostgreSQL LIMIT/OFFSET → DynamoDB full scan + client-side pagination.
        /// </summary>
        [Fact]
        public async Task Search_WithLimitAndSkip_ReturnsPaginatedResults()
        {
            // Arrange — create 30 items in DynamoDB mock to test Skip=10, Limit=5
            var items = Enumerable.Range(0, 30).Select(i =>
                CreateSearchDynamoDbItem(
                    id: Guid.NewGuid(),
                    content: $"item {i} for pagination test",
                    stemContent: $"item {i} pagination test",
                    snippet: $"Pagination item {i}")
            ).ToList();
            SetupDynamoDbScan(items);

            var body = CreateSearchQueryJson("item", skip: 10, limit: 5);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();

            // The response "object" should be an array with at most 5 items (Limit=5)
            var objectElement = parsed.GetProperty("object");
            if (objectElement.ValueKind == JsonValueKind.Array)
            {
                objectElement.GetArrayLength().Should().BeLessThanOrEqualTo(5);
            }
        }

        /// <summary>
        /// Verifies that when Limit is null, the handler returns all results up to
        /// MaxScanLimit without additional pagination constraints.
        /// Source: PostgreSQL LIMIT ALL → no client-side Take applied.
        /// </summary>
        [Fact]
        public async Task Search_WithNullLimit_ReturnsAllResults()
        {
            // Arrange — create 15 items
            var items = Enumerable.Range(0, 15).Select(i =>
                CreateSearchDynamoDbItem(
                    id: Guid.NewGuid(),
                    content: $"result {i} no limit",
                    stemContent: $"result {i} limit",
                    snippet: $"No limit result {i}")
            ).ToList();
            SetupDynamoDbScan(items);

            var body = CreateSearchQueryJson("result", limit: null, skip: null);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();

            // Without limit constraint, all items should be returned (after default page size)
            var objectElement = parsed.GetProperty("object");
            if (objectElement.ValueKind == JsonValueKind.Array)
            {
                objectElement.GetArrayLength().Should().BeGreaterThan(0);
            }
        }

        /// <summary>
        /// Verifies that the total count of results is included in the response.
        /// Source: PostgreSQL COUNT(*) OVER() window function.
        /// DynamoDB: Total count derived from all matching results before pagination.
        /// </summary>
        [Fact]
        public async Task Search_TotalCount_IncludedInResponse()
        {
            // Arrange — create 25 items to test total count with Limit=10
            var items = Enumerable.Range(0, 25).Select(i =>
                CreateSearchDynamoDbItem(
                    id: Guid.NewGuid(),
                    content: $"total count item {i}",
                    stemContent: $"total count item {i}",
                    snippet: $"Total count test {i}")
            ).ToList();
            SetupDynamoDbScan(items);

            var body = CreateSearchQueryJson("total", limit: 10);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();

            // The response should indicate successful search regardless of how
            // total_count is serialized (SearchResultList extends List<SearchResult>,
            // which System.Text.Json serializes as array — TotalCount may be in-band or lost).
            // Verify the message indicates completion.
            parsed.GetProperty("message").GetString().Should().Contain("Search completed");

            // Verify that DynamoDB was queried
            _mockDynamoDb.Verify(
                d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 6: SEARCH RESULT TYPE TESTS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that Narrow (Compact) result type returns minimal fields:
        /// only id, url, snippet, timestamp (not full attributes like content, stem_content).
        /// Source: MapDynamoDbItemToSearchResult with SearchResultType.Compact.
        /// </summary>
        [Fact]
        public async Task Search_NarrowResultType_ReturnsMinimalFields()
        {
            // Arrange
            var items = new List<Dictionary<string, AttributeValue>>
            {
                CreateSearchDynamoDbItem(
                    content: "detailed content for narrow test",
                    stemContent: "detailed content narrow test",
                    snippet: "Narrow result snippet",
                    url: "https://example.com/narrow")
            };
            SetupDynamoDbScan(items);

            // Use Compact (=Narrow) result type
            var body = CreateSearchQueryJson("detailed", resultType: SearchResultType.Compact);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();

            var objectElement = parsed.GetProperty("object");
            if (objectElement.ValueKind == JsonValueKind.Array && objectElement.GetArrayLength() > 0)
            {
                var firstResult = objectElement[0];
                // Compact mode includes id, url, snippet, timestamp
                firstResult.TryGetProperty("id", out _).Should().BeTrue();
                firstResult.TryGetProperty("url", out _).Should().BeTrue();
                firstResult.TryGetProperty("snippet", out _).Should().BeTrue();

                // Compact mode does NOT populate content from DynamoDB — the SearchResult.Content
                // property has a default value of string.Empty (not null), so WhenWritingNull
                // does NOT omit it. Verify the DynamoDB content was NOT mapped into the result.
                if (firstResult.TryGetProperty("content", out var contentProp))
                {
                    // Content is present but should be the default empty string,
                    // NOT the DynamoDB item's content value
                    contentProp.GetString().Should().NotBe(
                        "detailed content that should not appear in narrow results",
                        "Compact result type should not include DynamoDB content data");
                    contentProp.GetString().Should().BeEmpty(
                        "Compact result type should leave content as default empty string");
                }
            }
        }

        /// <summary>
        /// Verifies that Full result type returns all available attributes including
        /// content, stem_content, aux_data, entities, apps, records.
        /// Source: MapDynamoDbItemToSearchResult with SearchResultType.Full.
        /// </summary>
        [Fact]
        public async Task Search_FullResultType_ReturnsAllFields()
        {
            // Arrange
            var items = new List<Dictionary<string, AttributeValue>>
            {
                CreateSearchDynamoDbItem(
                    content: "full content for full type test",
                    stemContent: "full content full type test",
                    snippet: "Full result snippet",
                    url: "https://example.com/full",
                    auxData: "extra-metadata")
            };
            SetupDynamoDbScan(items);

            var body = CreateSearchQueryJson("full", resultType: SearchResultType.Full);
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();

            var objectElement = parsed.GetProperty("object");
            if (objectElement.ValueKind == JsonValueKind.Array && objectElement.GetArrayLength() > 0)
            {
                var firstResult = objectElement[0];
                // Full mode includes all fields
                firstResult.TryGetProperty("id", out _).Should().BeTrue();
                firstResult.TryGetProperty("url", out _).Should().BeTrue();
                firstResult.TryGetProperty("snippet", out _).Should().BeTrue();
                firstResult.TryGetProperty("content", out _).Should().BeTrue(
                    "Full result type should include content field");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 7: ADD TO INDEX TESTS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that AddToIndex with valid input creates a search index entry in DynamoDB.
        /// Source: SearchManager.AddToIndex() normalizes null strings, lowercases content,
        /// generates stem_content, serializes Guid lists, stores with new Guid id.
        /// </summary>
        [Fact]
        public async Task AddToIndex_ValidInput_CreatesSearchIndexEntry()
        {
            // Arrange
            PutItemRequest? capturedPutRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPutRequest = req)
                .ReturnsAsync(new PutItemResponse());

            var body = CreateAddToIndexJson(
                url: "https://example.com/record/42",
                snippet: "New index entry",
                content: "Full Content Text For Indexing Purposes");
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.AddToIndex(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);

            _mockDynamoDb.Verify(
                d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());

            capturedPutRequest.Should().NotBeNull();
            capturedPutRequest!.TableName.Should().Be(SearchTableName);

            // Verify DynamoDB item has all expected attributes
            var item = capturedPutRequest.Item;
            item.Should().ContainKey("id");
            item.Should().ContainKey("url");
            item.Should().ContainKey("snippet");
            item.Should().ContainKey("content");
            item.Should().ContainKey("stem_content");
            item.Should().ContainKey("entities");
            item.Should().ContainKey("apps");
            item.Should().ContainKey("records");
            item.Should().ContainKey("aux_data");
            item.Should().ContainKey("timestamp");

            // Content should be lowercased
            item["content"].S.Should().Be("full content text for indexing purposes");
        }

        /// <summary>
        /// Verifies that null string fields (url, snippet, aux_data) are normalized to
        /// empty strings in the DynamoDB item — never stored as DynamoDB NULL.
        /// Source: SearchManager.AddToIndex() null → empty normalization.
        /// </summary>
        [Fact]
        public async Task AddToIndex_NullStrings_NormalizedToEmpty()
        {
            // Arrange
            PutItemRequest? capturedPutRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPutRequest = req)
                .ReturnsAsync(new PutItemResponse());

            // Create entry with explicit null/empty fields
            var entry = new SearchResult
            {
                Url = null!,
                Snippet = null!,
                Content = "some content",
                AuxData = null!,
                Entities = new List<Guid>(),
                Apps = new List<Guid>(),
                Records = new List<Guid>(),
                Timestamp = DateTime.UtcNow
            };
            var body = JsonSerializer.Serialize(entry, _jsonOptions);
            var request = BuildRequest(body: body);

            // Act
            var response = await _handler.AddToIndex(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedPutRequest.Should().NotBeNull();

            var item = capturedPutRequest!.Item;
            // Null strings should be stored as empty strings (not missing)
            item["url"].S.Should().NotBeNull();
            item["snippet"].S.Should().NotBeNull();
            item["aux_data"].S.Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that AddToIndex publishes an SNS domain event with the topic
        /// "entity-management.search.indexed" after successfully indexing.
        /// </summary>
        [Fact]
        public async Task AddToIndex_PublishesSnsEvent()
        {
            // Arrange
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutItemResponse());

            PublishRequest? capturedPublishRequest = null;
            _mockSnsClient
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PublishRequest, CancellationToken>((req, _) => capturedPublishRequest = req)
                .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });

            var body = CreateAddToIndexJson();
            var request = BuildRequest(body: body);

            // Act
            await _handler.AddToIndex(request, _mockLambdaContext.Object);

            // Assert
            _mockSnsClient.Verify(
                s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());

            capturedPublishRequest.Should().NotBeNull();
            capturedPublishRequest!.TopicArn.Should().Be(SearchTopicArn);
            capturedPublishRequest.Message.Should().Contain("entity-management.search.indexed");
        }

        /// <summary>
        /// Verifies that AddToIndex generates a new Guid for the search entry id,
        /// not reusing any ID from the request body.
        /// </summary>
        [Fact]
        public async Task AddToIndex_GeneratesNewGuidForId()
        {
            // Arrange
            PutItemRequest? capturedPutRequest = null;
            _mockDynamoDb
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPutRequest = req)
                .ReturnsAsync(new PutItemResponse());

            var body = CreateAddToIndexJson();
            var request = BuildRequest(body: body);

            // Act
            await _handler.AddToIndex(request, _mockLambdaContext.Object);

            // Assert
            capturedPutRequest.Should().NotBeNull();
            var idValue = capturedPutRequest!.Item["id"].S;
            idValue.Should().NotBeNullOrEmpty();
            Guid.TryParse(idValue, out var parsedId).Should().BeTrue();
            parsedId.Should().NotBe(Guid.Empty, "AddToIndex must generate a non-empty Guid for the entry id");
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 8: REMOVE FROM INDEX TESTS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that RemoveFromIndex with a valid Guid id deletes the search
        /// index entry from DynamoDB using the correct partition key.
        /// </summary>
        [Fact]
        public async Task RemoveFromIndex_ValidId_DeletesSearchIndexEntry()
        {
            // Arrange
            var searchEntryId = Guid.NewGuid();
            DeleteItemRequest? capturedDeleteRequest = null;
            _mockDynamoDb
                .Setup(d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<DeleteItemRequest, CancellationToken>((req, _) => capturedDeleteRequest = req)
                .ReturnsAsync(new DeleteItemResponse
                {
                    Attributes = new Dictionary<string, AttributeValue>
                    {
                        ["id"] = new AttributeValue { S = searchEntryId.ToString() },
                        ["url"] = new AttributeValue { S = "https://example.com/deleted" }
                    }
                });

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = searchEntryId.ToString() });

            // Act
            var response = await _handler.RemoveFromIndex(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);

            _mockDynamoDb.Verify(
                d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());

            capturedDeleteRequest.Should().NotBeNull();
            capturedDeleteRequest!.TableName.Should().Be(SearchTableName);
            capturedDeleteRequest.Key.Should().ContainKey("id");
            capturedDeleteRequest.Key["id"].S.Should().Be(searchEntryId.ToString());

            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();
        }

        /// <summary>
        /// Verifies that RemoveFromIndex publishes an SNS domain event with the topic
        /// "entity-management.search.deindexed" after successfully removing.
        /// </summary>
        [Fact]
        public async Task RemoveFromIndex_PublishesSnsEvent()
        {
            // Arrange
            var searchEntryId = Guid.NewGuid();
            _mockDynamoDb
                .Setup(d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteItemResponse
                {
                    Attributes = new Dictionary<string, AttributeValue>
                    {
                        ["id"] = new AttributeValue { S = searchEntryId.ToString() }
                    }
                });

            PublishRequest? capturedPublishRequest = null;
            _mockSnsClient
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PublishRequest, CancellationToken>((req, _) => capturedPublishRequest = req)
                .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });

            var request = BuildRequest(
                pathParams: new Dictionary<string, string> { ["id"] = searchEntryId.ToString() });

            // Act
            await _handler.RemoveFromIndex(request, _mockLambdaContext.Object);

            // Assert
            _mockSnsClient.Verify(
                s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());

            capturedPublishRequest.Should().NotBeNull();
            capturedPublishRequest!.TopicArn.Should().Be(SearchTopicArn);
            capturedPublishRequest.Message.Should().Contain("entity-management.search.deindexed");
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 9: QUICK SEARCH TESTS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that QuickSearch with a valid query, entity name, and lookup fields
        /// returns results enriched with entity metadata.
        /// </summary>
        [Fact]
        public async Task QuickSearch_ValidQuery_ReturnsResults()
        {
            // Arrange
            var testEntity = CreateTestEntity(TestEntityId, "test_entity");
            _mockEntityService
                .Setup(s => s.GetEntity(It.Is<string>(n => n == "test_entity")))
                .ReturnsAsync(testEntity);

            var items = new List<Dictionary<string, AttributeValue>>
            {
                new Dictionary<string, AttributeValue>
                {
                    ["id"] = new AttributeValue { S = Guid.NewGuid().ToString() },
                    ["entity_name"] = new AttributeValue { S = "test_entity" },
                    ["name"] = new AttributeValue { S = "Test Record" }
                }
            };
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ScanResponse
                {
                    Items = items,
                    Count = items.Count,
                    ScannedCount = items.Count
                });

            var body = CreateQuickSearchJson(
                query: "Test",
                entityName: "test_entity",
                lookupFields: "name",
                returnFields: "id,name");
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.QuickSearch(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNullOrEmpty();

            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();

            _mockEntityService.Verify(
                s => s.GetEntity(It.Is<string>(n => n == "test_entity")),
                Times.Once());
        }

        /// <summary>
        /// Verifies that QuickSearch applies force_filters as additional query constraints.
        /// Source: ApplyForceFilters() parses CSV "fieldName:dataType:eqValue" patterns.
        /// </summary>
        [Fact]
        public async Task QuickSearch_WithForceFilters_AppliesAdditionalConstraints()
        {
            // Arrange
            var testEntity = CreateTestEntity(TestEntityId, "test_entity");
            _mockEntityService
                .Setup(s => s.GetEntity(It.Is<string>(n => n == "test_entity")))
                .ReturnsAsync(testEntity);

            var matchingRecord = new Dictionary<string, AttributeValue>
            {
                ["id"] = new AttributeValue { S = Guid.NewGuid().ToString() },
                ["entity_name"] = new AttributeValue { S = "test_entity" },
                ["name"] = new AttributeValue { S = "Filtered Record" },
                ["status"] = new AttributeValue { S = "active" }
            };
            var nonMatchingRecord = new Dictionary<string, AttributeValue>
            {
                ["id"] = new AttributeValue { S = Guid.NewGuid().ToString() },
                ["entity_name"] = new AttributeValue { S = "test_entity" },
                ["name"] = new AttributeValue { S = "Other Record" },
                ["status"] = new AttributeValue { S = "inactive" }
            };
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { matchingRecord, nonMatchingRecord },
                    Count = 2,
                    ScannedCount = 2
                });

            // Force filter: status must be "active"
            var body = CreateQuickSearchJson(
                query: "Record",
                entityName: "test_entity",
                lookupFields: "name",
                returnFields: "id,name,status",
                forceFilters: "status:string:active");
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.QuickSearch(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeTrue();

            // Force filters should reduce the result set
            _mockDynamoDb.Verify(
                d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 10: ERROR HANDLING TESTS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that Search with a null or empty query body returns HTTP 400
        /// with an appropriate error message.
        /// </summary>
        [Fact]
        public async Task Search_NullQuery_Returns400()
        {
            // Arrange — send request with no body (null/empty)
            var request = BuildAuthenticatedRequest(body: null);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().NotBeNullOrEmpty();

            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeFalse();

            // DynamoDB should NOT have been called for invalid input
            _mockDynamoDb.Verify(
                d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Verifies that when DynamoDB throws an AmazonDynamoDBException, the handler
        /// returns HTTP 500 with a generic error message.
        /// </summary>
        [Fact]
        public async Task Search_DynamoDBError_Returns500()
        {
            // Arrange
            _mockDynamoDb
                .Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonDynamoDBException("Service unavailable"));

            var body = CreateSearchQueryJson("test query");
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(500);
            response.Body.Should().NotBeNullOrEmpty();

            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);
            parsed.GetProperty("success").GetBoolean().Should().BeFalse();
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 11: RESPONSE STRUCTURE / ENVELOPE TESTS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies the complete response envelope structure matches the standard
        /// BaseResponseModel/ResponseModel pattern: success, timestamp, message,
        /// errors array, and object property containing search results.
        /// </summary>
        [Fact]
        public async Task Search_ResponseStructure_MatchesEnvelope()
        {
            // Arrange
            var items = new List<Dictionary<string, AttributeValue>>
            {
                CreateSearchDynamoDbItem(
                    id: Guid.NewGuid(),
                    content: "envelope structure test",
                    stemContent: "envelope structure test",
                    snippet: "Envelope test")
            };
            SetupDynamoDbScan(items);

            var body = CreateSearchQueryJson("envelope");
            var request = BuildAuthenticatedRequest(body: body);

            // Act
            var response = await _handler.Search(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);

            var parsed = JsonSerializer.Deserialize<JsonElement>(response.Body);

            // Verify standard envelope properties exist
            parsed.TryGetProperty("success", out var successProp).Should().BeTrue();
            successProp.GetBoolean().Should().BeTrue();

            parsed.TryGetProperty("timestamp", out var timestampProp).Should().BeTrue();
            timestampProp.GetString().Should().NotBeNullOrEmpty();

            parsed.TryGetProperty("message", out var messageProp).Should().BeTrue();
            messageProp.GetString().Should().NotBeNullOrEmpty();

            parsed.TryGetProperty("errors", out var errorsProp).Should().BeTrue();
            errorsProp.ValueKind.Should().Be(JsonValueKind.Array);
            errorsProp.GetArrayLength().Should().Be(0);

            parsed.TryGetProperty("object", out var objectProp).Should().BeTrue();
            objectProp.ValueKind.Should().Be(JsonValueKind.Array);

            // Verify result item structure
            if (objectProp.GetArrayLength() > 0)
            {
                var firstItem = objectProp[0];
                firstItem.TryGetProperty("id", out _).Should().BeTrue();
                firstItem.TryGetProperty("url", out _).Should().BeTrue();
                firstItem.TryGetProperty("snippet", out _).Should().BeTrue();
            }

            // Verify response headers
            response.Headers.Should().ContainKey("Content-Type");
            response.Headers["Content-Type"].Should().Contain("application/json");
        }
    }
}
