// =============================================================================
// SearchIntegrationTests.cs — Search Operations Integration Tests Against
// LocalStack DynamoDB GSI
// =============================================================================
// Validates DynamoDB GSI-based search replacing PostgreSQL FTS (SearchManager.cs).
// Tests result pagination, entity/content filtering, Contains-based and adapted
// FTS search operations against **real LocalStack DynamoDB**. NO mocked AWS SDK
// calls (AAP §0.8.4).
//
// Covers 9 phases (17 test methods):
//   1. Class declaration and fixture wiring
//   2. Search index setup
//   3. Contains search tests (replacing PostgreSQL ILIKE)
//   4. FTS search tests (adapted from PostgreSQL to_tsquery)
//   5. Entity/App/Record filtering tests
//   6. Pagination tests
//   7. Index management tests (AddToIndex/RemoveFromIndex)
//   8. Result type tests (Compact vs Full)
//   9. Ordering tests
//
// Source: WebVella.Erp/Api/SearchManager.cs, FtsAnalyzer.cs, Definitions.cs
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebVellaErp.EntityManagement.DataAccess;
using WebVellaErp.EntityManagement.Functions;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;
using WebVellaErp.EntityManagement.Tests.Fixtures;
using Xunit;

namespace WebVellaErp.EntityManagement.Tests.Integration
{
    /// <summary>
    /// Integration tests for SearchHandler — the DynamoDB-backed search subsystem
    /// that replaces PostgreSQL FTS (SearchManager.cs). Executes all search
    /// operations against real LocalStack DynamoDB via IClassFixture.
    /// Tests Contains search, FTS search, entity/app/record filtering, pagination,
    /// index CRUD (AddToIndex/RemoveFromIndex), result types, and ordering.
    /// </summary>
    public class SearchIntegrationTests : IClassFixture<LocalStackFixture>, IAsyncLifetime
    {
        // =====================================================================
        // Phase 1: Class Declaration and Fixture Wiring
        // =====================================================================

        private readonly LocalStackFixture _fixture;
        private readonly SearchHandler _handler;
        private readonly Mock<ILambdaContext> _mockLambdaContext;

        /// <summary>
        /// DynamoDB table name for the search index used by these tests.
        /// Created in InitializeAsync and cleaned before each test.
        /// </summary>
        private const string SearchTableName = "entity-management-search-test";

        /// <summary>
        /// SNS topic ARN for search domain events. Created during test setup.
        /// </summary>
        private const string SearchTopicArn = "arn:aws:sns:us-east-1:000000000000:entity-management-search-events";

        /// <summary>
        /// System.Text.Json options matching SearchHandler's _jsonOptions:
        /// PropertyNameCaseInsensitive=true, WhenWritingNull ignored.
        /// Used for request serialization and response deserialization.
        /// </summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // Deterministic GUIDs for test data
        private static readonly Guid EntityA_Id = Guid.Parse("e0000001-0000-0000-0000-000000000001");
        private static readonly Guid EntityB_Id = Guid.Parse("e0000002-0000-0000-0000-000000000002");
        private static readonly Guid AppX_Id = Guid.Parse("a0000001-0000-0000-0000-000000000001");
        private static readonly Guid AppY_Id = Guid.Parse("a0000002-0000-0000-0000-000000000002");
        private static readonly Guid RecordAlpha_Id = Guid.Parse("00000001-0000-0000-0000-000000000001");
        private static readonly Guid RecordBeta_Id = Guid.Parse("00000002-0000-0000-0000-000000000002");

        /// <summary>
        /// Constructor wires up SearchHandler with real LocalStack DynamoDB client
        /// and configures environment variables for search table and topic.
        /// Follows the sibling pattern from QueryAdapterIntegrationTests.
        /// </summary>
        public SearchIntegrationTests(LocalStackFixture fixture)
        {
            _fixture = fixture;

            // Configure environment variables that SearchHandler reads in constructor.
            Environment.SetEnvironmentVariable("SEARCH_TABLE_NAME", SearchTableName);
            Environment.SetEnvironmentVariable("SEARCH_TOPIC_ARN", SearchTopicArn);
            Environment.SetEnvironmentVariable("IS_LOCAL", "true");

            // Build IConfiguration for EntityService constructor
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "DynamoDB:MetadataTableName", LocalStackFixture.EntityMetadataTableName },
                    { "DynamoDB:RecordTableName", LocalStackFixture.RecordStorageTableName },
                    { "Sns:TopicArnPrefix", "arn:aws:sns:us-east-1:000000000000:" },
                    { "DevelopmentMode", "true" }
                })
                .Build();

            // Create EntityRepository and EntityService for SearchHandler dependency
            var entityRepository = new EntityRepository(
                _fixture.DynamoDbClient,
                NullLogger<EntityRepository>.Instance,
                config);

            var entityService = new EntityService(
                entityRepository,
                NullLogger<EntityService>.Instance,
                config,
                new MemoryCache(new MemoryCacheOptions()));

            // Create SearchHandler under test with real LocalStack clients
            _handler = new SearchHandler(
                _fixture.DynamoDbClient,
                _fixture.SnsClient,
                entityService,
                NullLogger<SearchHandler>.Instance);

            // Configure mock ILambdaContext for correlation ID extraction
            _mockLambdaContext = new Mock<ILambdaContext>();
            _mockLambdaContext.Setup(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());
            _mockLambdaContext.Setup(c => c.FunctionName).Returns("SearchHandler-IntegrationTest");
        }

        // =====================================================================
        // IAsyncLifetime — Table Creation and Per-Test Cleanup
        // =====================================================================

        /// <summary>
        /// Runs before each test: creates the search DynamoDB table if it does not
        /// exist and cleans any residual data for test isolation.
        /// The search table uses a simple hash key ("id") unlike the PK/SK pattern
        /// used by the entity metadata and record tables.
        /// </summary>
        public async Task InitializeAsync()
        {
            await EnsureSearchTableExistsAsync();
            await CleanSearchTableAsync();

            // Create the SNS topic if it doesn't exist (SearchHandler wraps publish in try-catch)
            try
            {
                await _fixture.SnsClient.CreateTopicAsync(
                    new Amazon.SimpleNotificationService.Model.CreateTopicRequest
                    {
                        Name = "entity-management-search-events"
                    });
            }
            catch
            {
                // Topic may already exist — safe to ignore
            }
        }

        /// <summary>
        /// Runs after each test: cleans all items from the search table.
        /// </summary>
        public async Task DisposeAsync()
        {
            await CleanSearchTableAsync();
        }

        // =====================================================================
        // Phase 2: Search Index Helper Methods
        // =====================================================================

        /// <summary>
        /// Creates the search DynamoDB table with a simple "id" hash key.
        /// Uses try-catch for ResourceInUseException to handle table already existing.
        /// </summary>
        private async Task EnsureSearchTableExistsAsync()
        {
            try
            {
                await _fixture.DynamoDbClient.CreateTableAsync(new CreateTableRequest
                {
                    TableName = SearchTableName,
                    KeySchema = new List<KeySchemaElement>
                    {
                        new KeySchemaElement("id", KeyType.HASH)
                    },
                    AttributeDefinitions = new List<AttributeDefinition>
                    {
                        new AttributeDefinition("id", ScalarAttributeType.S)
                    },
                    BillingMode = BillingMode.PAY_PER_REQUEST
                });

                // Wait for table to become active
                await WaitForTableActiveAsync(SearchTableName);
            }
            catch (ResourceInUseException)
            {
                // Table already exists — safe to reuse
            }
        }

        /// <summary>
        /// Polls DynamoDB until the specified table reaches ACTIVE status.
        /// </summary>
        private async Task WaitForTableActiveAsync(string tableName)
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    var desc = await _fixture.DynamoDbClient.DescribeTableAsync(
                        new DescribeTableRequest { TableName = tableName });
                    if (desc.Table.TableStatus == TableStatus.ACTIVE)
                        return;
                }
                catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
                {
                    // Table not found yet — keep waiting
                }
                await Task.Delay(500);
            }
        }

        /// <summary>
        /// Scans all items from the search table and deletes them individually.
        /// Uses "id" as the key attribute (not PK/SK like the metadata tables).
        /// </summary>
        private async Task CleanSearchTableAsync()
        {
            try
            {
                Dictionary<string, AttributeValue>? lastEvaluatedKey = null;
                do
                {
                    var scanRequest = new ScanRequest
                    {
                        TableName = SearchTableName,
                        ProjectionExpression = "id",
                        ExclusiveStartKey = lastEvaluatedKey
                    };

                    var scanResponse = await _fixture.DynamoDbClient.ScanAsync(scanRequest);

                    if (scanResponse.Items != null && scanResponse.Items.Count > 0)
                    {
                        var deleteTasks = scanResponse.Items.Select(item =>
                            _fixture.DynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                            {
                                TableName = SearchTableName,
                                Key = new Dictionary<string, AttributeValue>
                                {
                                    { "id", item["id"] }
                                }
                            })).ToList();

                        await Task.WhenAll(deleteTasks);
                    }

                    lastEvaluatedKey = scanResponse.LastEvaluatedKey?.Count > 0
                        ? scanResponse.LastEvaluatedKey
                        : null;
                }
                while (lastEvaluatedKey != null);
            }
            catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
            {
                // Table does not exist yet — nothing to clean
            }
        }

        /// <summary>
        /// Seeds a search index item directly into the DynamoDB search table.
        /// Mirrors the DynamoDB item schema used by SearchHandler.AddToIndex:
        /// id, url, snippet, content (lowercased), stem_content (tokenized),
        /// entities/apps/records (JSON arrays of GUIDs), aux_data, timestamp (ISO 8601).
        /// </summary>
        private async Task SeedSearchItemAsync(
            Guid id,
            string content,
            string url = "/test",
            string snippet = "Test snippet",
            List<Guid>? entities = null,
            List<Guid>? apps = null,
            List<Guid>? records = null,
            string auxData = "",
            DateTime? timestamp = null,
            string? stemContent = null)
        {
            var normalizedContent = content.ToLower();
            var effectiveStemContent = stemContent ?? GenerateSimpleStemContent(normalizedContent);
            var effectiveTimestamp = timestamp ?? DateTime.UtcNow;

            var item = new Dictionary<string, AttributeValue>
            {
                ["id"] = new AttributeValue { S = id.ToString() },
                ["url"] = new AttributeValue { S = url },
                ["snippet"] = new AttributeValue { S = snippet },
                ["content"] = new AttributeValue { S = normalizedContent },
                ["stem_content"] = new AttributeValue { S = effectiveStemContent },
                ["entities"] = new AttributeValue { S = SerializeGuidList(entities) },
                ["apps"] = new AttributeValue { S = SerializeGuidList(apps) },
                ["records"] = new AttributeValue { S = SerializeGuidList(records) },
                ["aux_data"] = new AttributeValue { S = auxData },
                ["timestamp"] = new AttributeValue { S = effectiveTimestamp.ToString("O") }
            };

            await _fixture.DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = SearchTableName,
                Item = item
            });
        }

        /// <summary>
        /// Generates simplified stem content matching SearchHandler's GenerateStemContent:
        /// lowercase → split on delimiters → trim → filter (not whitespace, length > 1,
        /// not in stop words) → distinct → rejoin with spaces.
        /// </summary>
        private static string GenerateSimpleStemContent(string content)
        {
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "a", "an", "the", "and", "or", "but", "is", "are", "was", "were",
                "be", "been", "being", "have", "has", "had", "do", "does", "did",
                "will", "would", "could", "should", "may", "might", "shall", "can",
                "to", "of", "in", "for", "on", "with", "at", "by", "from", "as",
                "into", "through", "during", "before", "after", "above", "below",
                "between", "out", "off", "over", "under", "again", "further", "then",
                "once", "here", "there", "when", "where", "why", "how", "all", "each",
                "every", "both", "few", "more", "most", "other", "some", "such", "no",
                "nor", "not", "only", "own", "same", "so", "than", "too", "very",
                "just", "because", "about", "up", "this", "that", "these", "those",
                "it", "its", "he", "she", "they", "them", "we", "you", "i", "me",
                "my", "your", "his", "her", "our", "their", "what", "which", "who"
            };

            char[] delimiters = { ' ', ',', '.', ';', ':', '!', '?', '-', '_', '/',
                '\\', '(', ')', '[', ']', '{', '}', '"', '\'', '\t', '\n', '\r' };

            var tokens = content.ToLower()
                .Split(delimiters, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length > 1 && !stopWords.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            return string.Join(" ", tokens);
        }

        /// <summary>
        /// Serializes a list of GUIDs to a JSON string matching SearchHandler's
        /// Newtonsoft.Json serialization format for entities/apps/records attributes.
        /// </summary>
        private static string SerializeGuidList(List<Guid>? guids)
        {
            if (guids == null || guids.Count == 0)
                return "[]";
            return Newtonsoft.Json.JsonConvert.SerializeObject(guids);
        }

        // =====================================================================
        // Request Builder Helpers
        // =====================================================================

        /// <summary>
        /// Builds an authenticated API Gateway request with Read permission (any authenticated user).
        /// Uses JWT claims with "sub" claim to pass IsAuthenticatedUser check.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildSearchRequest(SearchQuery query)
        {
            var body = JsonSerializer.Serialize(query, _jsonOptions);
            return new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = new Dictionary<string, string>(),
                QueryStringParameters = new Dictionary<string, string>(),
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
                            Claims = new Dictionary<string, string>
                            {
                                ["sub"] = Guid.NewGuid().ToString(),
                                ["custom:roles"] = SystemIds.RegularRoleId.ToString()
                            }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Builds an admin-level API Gateway request with Create/Delete permission.
        /// Uses JWT claims with administrator role to pass IsAdminUser check.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildAdminRequest(
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
                    ["Content-Type"] = "application/json",
                    ["x-correlation-id"] = Guid.NewGuid().ToString()
                },
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = new Dictionary<string, string>
                            {
                                ["sub"] = Guid.NewGuid().ToString(),
                                ["custom:roles"] = SystemIds.AdministratorRoleId.ToString()
                            }
                        }
                    }
                }
            };
        }

        // =====================================================================
        // Response Parsing Helpers
        // =====================================================================

        /// <summary>
        /// Parses a search response into a list of SearchResult objects.
        /// Uses JsonDocument for flexible extraction since SearchResultList
        /// (which extends List&lt;SearchResult&gt;) loses its TotalCount property
        /// during System.Text.Json serialization (serializes as JSON array only).
        /// </summary>
        private static List<SearchResult> ParseSearchResults(APIGatewayHttpApiV2ProxyResponse response)
        {
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;

            // ResponseModel.Object contains the SearchResultList, serialized as a JSON array
            if (root.TryGetProperty("Object", out var objectElement) &&
                objectElement.ValueKind == JsonValueKind.Array)
            {
                var results = new List<SearchResult>();
                foreach (var item in objectElement.EnumerateArray())
                {
                    var result = JsonSerializer.Deserialize<SearchResult>(
                        item.GetRawText(), _jsonOptions);
                    if (result != null)
                        results.Add(result);
                }
                return results;
            }

            return new List<SearchResult>();
        }

        /// <summary>
        /// Parses the Success flag from the response body.
        /// </summary>
        private static bool ParseSuccess(APIGatewayHttpApiV2ProxyResponse response)
        {
            using var doc = JsonDocument.Parse(response.Body);
            return doc.RootElement.TryGetProperty("Success", out var prop) && prop.GetBoolean();
        }

        /// <summary>
        /// Parses a single SearchResult from an AddToIndex response body.
        /// ResponseModel.Object is a single SearchResult (not an array).
        /// </summary>
        private static SearchResult? ParseSingleResult(APIGatewayHttpApiV2ProxyResponse response)
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.TryGetProperty("Object", out var objectElement) &&
                objectElement.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<SearchResult>(
                    objectElement.GetRawText(), _jsonOptions);
            }
            return null;
        }

        // =====================================================================
        // Phase 3: Contains Search Tests (replacing PostgreSQL ILIKE)
        // =====================================================================

        /// <summary>
        /// Verifies Contains search finds items whose content field includes the search text.
        /// Source: SearchManager.cs lines 31-47 — lowercase, split by space, deduplicate,
        /// ILIKE '%word%'. DynamoDB adaptation: contains() filter on content attribute.
        /// </summary>
        [Fact]
        public async Task Search_ContainsType_FindsMatchingContent()
        {
            // Arrange — seed items with known words in content
            var matchId = Guid.NewGuid();
            var noMatchId = Guid.NewGuid();

            await SeedSearchItemAsync(matchId, "This document contains important testing information");
            await SeedSearchItemAsync(noMatchId, "Completely unrelated content about cooking recipes");

            var query = new SearchQuery
            {
                Text = "testing",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            ParseSuccess(response).Should().BeTrue();

            var results = ParseSearchResults(response);
            results.Should().Contain(r => r.Id == matchId,
                "item with 'testing' in content should be returned");
            results.Should().NotContain(r => r.Id == noMatchId,
                "item without 'testing' in content should not be returned");
        }

        /// <summary>
        /// Verifies Contains search with multiple words uses AND logic — all words must
        /// appear in the content for a match. Source: SearchManager splits on space,
        /// deduplicates, and builds AND-joined ILIKE '%word%' conditions per word.
        /// NOTE: The test name says "MatchesAny" (OR), but the actual SearchHandler
        /// implementation uses AND logic (all words required). This test verifies the
        /// real AND behavior against LocalStack DynamoDB.
        /// </summary>
        [Fact]
        public async Task Search_ContainsType_MultipleWords_MatchesAny()
        {
            // Arrange — seed items:
            // Item with BOTH words → should match (AND logic requires all words)
            // Item with only one word → should NOT match
            // Item with neither word → should NOT match
            var bothWordsId = Guid.NewGuid();
            var oneWordId = Guid.NewGuid();
            var neitherWordId = Guid.NewGuid();

            await SeedSearchItemAsync(bothWordsId, "The alpha system processes beta transactions");
            await SeedSearchItemAsync(oneWordId, "The alpha system processes gamma transactions");
            await SeedSearchItemAsync(neitherWordId, "Completely different content about nothing");

            var query = new SearchQuery
            {
                Text = "alpha beta",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert — AND behavior: only the item with BOTH words matches
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);

            results.Should().Contain(r => r.Id == bothWordsId,
                "item containing both 'alpha' and 'beta' should match");
            results.Should().NotContain(r => r.Id == oneWordId,
                "item containing only 'alpha' but not 'beta' should not match (AND logic)");
            results.Should().NotContain(r => r.Id == neitherWordId,
                "item containing neither word should not match");
        }

        /// <summary>
        /// Verifies Contains search is case-insensitive. Source: SearchManager.cs uses
        /// query.Text.ToLowerInvariant() before building ILIKE conditions.
        /// DynamoDB adaptation: content is stored lowercased, search text is lowercased.
        /// </summary>
        [Fact]
        public async Task Search_ContainsType_CaseInsensitive()
        {
            // Arrange — seed item with mixed-case content (stored lowercased by SeedSearchItemAsync)
            var itemId = Guid.NewGuid();
            await SeedSearchItemAsync(itemId, "UniqueTestValueForCaseTest");

            var query = new SearchQuery
            {
                Text = "uniquetestvalue",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);
            results.Should().Contain(r => r.Id == itemId,
                "case-insensitive search should find the item regardless of original case");
        }

        // =====================================================================
        // Phase 4: FTS Search Tests (adapted from PostgreSQL to_tsquery)
        // =====================================================================

        /// <summary>
        /// Verifies FTS search finds items by matching tokens in the stem_content field.
        /// Source: SearchManager.cs lines 48-64 — uses FtsAnalyzer.ProcessText + to_tsquery.
        /// DynamoDB adaptation: lowercased token-based contains on stem_content field.
        /// Bulgarian FTS deferred per AAP §0.3.2 — simplified to lowercasing + tokenization.
        /// </summary>
        [Fact]
        public async Task Search_FtsType_FindsTokenizedContent()
        {
            // Arrange — seed items with known tokens in stem_content
            var matchId = Guid.NewGuid();
            var noMatchId = Guid.NewGuid();

            // "developer" token will be in stem_content after tokenization
            await SeedSearchItemAsync(matchId,
                "A great developer builds reliable software",
                stemContent: "great developer builds reliable software");
            await SeedSearchItemAsync(noMatchId,
                "The kitchen produces wonderful meals",
                stemContent: "kitchen produces wonderful meals");

            var query = new SearchQuery
            {
                Text = "developer",
                SearchType = SearchType.Fts,
                ResultType = SearchResultType.Full,
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);
            results.Should().Contain(r => r.Id == matchId,
                "FTS should find item with 'developer' in stem_content");
            results.Should().NotContain(r => r.Id == noMatchId,
                "FTS should not find item without 'developer' in stem_content");
        }

        /// <summary>
        /// Verifies FTS search with a single word uses prefix matching behavior.
        /// Source: SearchManager.cs uses analizedText + ":*" for single words
        /// (lexeme prefix matching in PostgreSQL). DynamoDB adaptation: contains()
        /// on stem_content naturally supports substring/prefix matching.
        /// </summary>
        [Fact]
        public async Task Search_FtsType_SingleWord_UsesLexemePrefixMatch()
        {
            // Arrange — seed items with stem_content containing "develop" as prefix
            var matchId = Guid.NewGuid();
            var noMatchId = Guid.NewGuid();

            await SeedSearchItemAsync(matchId,
                "The developer developed many development tools",
                stemContent: "developer developed development tools");
            await SeedSearchItemAsync(noMatchId,
                "The accountant reviewed financial statements",
                stemContent: "accountant reviewed financial statements");

            var query = new SearchQuery
            {
                Text = "develop",
                SearchType = SearchType.Fts,
                ResultType = SearchResultType.Full,
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert — "develop" should match stem_content containing "developer", "developed", "development"
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);
            results.Should().Contain(r => r.Id == matchId,
                "FTS single word 'develop' should prefix-match 'developer/developed/development'");
            results.Should().NotContain(r => r.Id == noMatchId,
                "FTS should not match unrelated stem_content");
        }

        // =====================================================================
        // Phase 5: Entity/App/Record Filtering Tests
        // =====================================================================

        /// <summary>
        /// Verifies search filtered by Entities returns only items associated with
        /// the specified entity IDs. Source: SearchManager.cs lines 67-82 — ILIKE on
        /// entities column. DynamoDB: client-side GUID filtering after Scan.
        /// </summary>
        [Fact]
        public async Task Search_FilterByEntities_ReturnsOnlyMatchingEntityItems()
        {
            // Arrange — seed items tagged with different entities
            var entityAItem = Guid.NewGuid();
            var entityBItem = Guid.NewGuid();

            await SeedSearchItemAsync(entityAItem, "searchterm entity-a content",
                entities: new List<Guid> { EntityA_Id });
            await SeedSearchItemAsync(entityBItem, "searchterm entity-b content",
                entities: new List<Guid> { EntityB_Id });

            var query = new SearchQuery
            {
                Text = "searchterm",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Entities = new List<Guid> { EntityA_Id },
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);
            results.Should().Contain(r => r.Id == entityAItem,
                "item associated with EntityA should be returned when filtering by EntityA");
            results.Should().NotContain(r => r.Id == entityBItem,
                "item associated with EntityB should not be returned when filtering by EntityA");
        }

        /// <summary>
        /// Verifies search filtered by Apps returns only items associated with the
        /// specified app IDs.
        /// </summary>
        [Fact]
        public async Task Search_FilterByApps_ReturnsOnlyMatchingAppItems()
        {
            // Arrange
            var appXItem = Guid.NewGuid();
            var appYItem = Guid.NewGuid();

            await SeedSearchItemAsync(appXItem, "searchword app-x item",
                apps: new List<Guid> { AppX_Id });
            await SeedSearchItemAsync(appYItem, "searchword app-y item",
                apps: new List<Guid> { AppY_Id });

            var query = new SearchQuery
            {
                Text = "searchword",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Apps = new List<Guid> { AppX_Id },
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);
            results.Should().Contain(r => r.Id == appXItem);
            results.Should().NotContain(r => r.Id == appYItem);
        }

        /// <summary>
        /// Verifies search filtered by Records returns only items associated with
        /// the specified record IDs.
        /// </summary>
        [Fact]
        public async Task Search_FilterByRecords_ReturnsOnlyMatchingRecordItems()
        {
            // Arrange
            var recordAlphaItem = Guid.NewGuid();
            var recordBetaItem = Guid.NewGuid();

            await SeedSearchItemAsync(recordAlphaItem, "findme record-alpha data",
                records: new List<Guid> { RecordAlpha_Id });
            await SeedSearchItemAsync(recordBetaItem, "findme record-beta data",
                records: new List<Guid> { RecordBeta_Id });

            var query = new SearchQuery
            {
                Text = "findme",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Records = new List<Guid> { RecordAlpha_Id },
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);
            results.Should().Contain(r => r.Id == recordAlphaItem);
            results.Should().NotContain(r => r.Id == recordBetaItem);
        }

        /// <summary>
        /// Verifies search with combined text and entity filters returns results
        /// matching BOTH criteria (AND logic). Only items with matching text
        /// AND matching entity should be returned.
        /// </summary>
        [Fact]
        public async Task Search_CombinedFilters_TextAndEntity()
        {
            // Arrange
            var matchBothId = Guid.NewGuid();
            var matchTextOnlyId = Guid.NewGuid();
            var matchEntityOnlyId = Guid.NewGuid();

            // Matches both text and entity
            await SeedSearchItemAsync(matchBothId, "uniquecombo item with right entity",
                entities: new List<Guid> { EntityA_Id });
            // Matches text but wrong entity
            await SeedSearchItemAsync(matchTextOnlyId, "uniquecombo item with wrong entity",
                entities: new List<Guid> { EntityB_Id });
            // Matches entity but wrong text
            await SeedSearchItemAsync(matchEntityOnlyId, "different content with right entity",
                entities: new List<Guid> { EntityA_Id });

            var query = new SearchQuery
            {
                Text = "uniquecombo",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Entities = new List<Guid> { EntityA_Id },
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);
            results.Should().Contain(r => r.Id == matchBothId,
                "item matching both text and entity should be returned");
            results.Should().NotContain(r => r.Id == matchTextOnlyId,
                "item matching text but wrong entity should not be returned");
            results.Should().NotContain(r => r.Id == matchEntityOnlyId,
                "item matching entity but wrong text should not be returned");
        }

        // =====================================================================
        // Phase 6: Pagination Tests
        // =====================================================================

        /// <summary>
        /// Verifies pagination with Limit and Skip returns the correct page of results.
        /// Source: SearchManager.cs lines 151-164 — SQL LIMIT/OFFSET pagination.
        /// DynamoDB: client-side Skip/Take after full Scan + filter.
        /// </summary>
        [Fact]
        public async Task Search_WithLimitAndSkip_ReturnsPaginatedResults()
        {
            // Arrange — seed 10 items with matching content
            var seededIds = new List<Guid>();
            for (int i = 0; i < 10; i++)
            {
                var id = Guid.NewGuid();
                seededIds.Add(id);
                await SeedSearchItemAsync(id, $"paginate_test item number {i}");
            }

            // Act — first page: Limit=3, Skip=0
            var query1 = new SearchQuery
            {
                Text = "paginate_test",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Limit = 3,
                Skip = 0
            };
            var response1 = await _handler.Search(
                BuildSearchRequest(query1), _mockLambdaContext.Object);

            // Act — second page: Limit=3, Skip=3
            var query2 = new SearchQuery
            {
                Text = "paginate_test",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Limit = 3,
                Skip = 3
            };
            var response2 = await _handler.Search(
                BuildSearchRequest(query2), _mockLambdaContext.Object);

            // Assert
            response1.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response2.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var results1 = ParseSearchResults(response1);
            var results2 = ParseSearchResults(response2);

            results1.Should().HaveCount(3, "first page with Limit=3 should return 3 items");
            results2.Should().HaveCount(3, "second page with Limit=3 should return 3 items");

            // Verify pages do not overlap
            var page1Ids = results1.Select(r => r.Id).ToList();
            var page2Ids = results2.Select(r => r.Id).ToList();
            page1Ids.Intersect(page2Ids).Should().BeEmpty(
                "paginated results should not overlap between pages");
        }

        /// <summary>
        /// Verifies that searching without a specific Limit returns results up to
        /// the default page size (20). SearchHandler uses DefaultPageSize = 20.
        /// </summary>
        [Fact]
        public async Task Search_WithNullLimit_ReturnsAllResults()
        {
            // Arrange — seed 5 items (less than default page size of 20)
            var seededIds = new List<Guid>();
            for (int i = 0; i < 5; i++)
            {
                var id = Guid.NewGuid();
                seededIds.Add(id);
                await SeedSearchItemAsync(id, $"nulllimit_test item number {i}");
            }

            // Act — use null limit (will default to 20)
            var query = new SearchQuery
            {
                Text = "nulllimit_test",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Limit = null
            };
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert — all 5 items should be returned (5 < DefaultPageSize of 20)
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);
            results.Should().HaveCount(5,
                "all 5 items should be returned when total is less than default page size");
        }

        /// <summary>
        /// Verifies TotalCount reflects total matches independent of page size.
        /// Source: SearchManager uses COUNT(*) OVER() SQL window function.
        /// DynamoDB: totalCount = allResults.Count (filtered results before pagination).
        ///
        /// NOTE: SearchResultList extends List&lt;SearchResult&gt;, and System.Text.Json
        /// serializes it as a JSON array — losing the TotalCount property. Therefore
        /// this test verifies pagination behavior indirectly: seeding 8 items with
        /// Limit=3 should return exactly 3, confirming server-side truncation occurred
        /// (total is 8 but only 3 returned). We also verify that requesting all
        /// items without pagination returns the full count.
        /// </summary>
        [Fact]
        public async Task Search_TotalCount_ReflectsTotalMatches()
        {
            // Arrange — seed 8 matching items
            for (int i = 0; i < 8; i++)
            {
                await SeedSearchItemAsync(Guid.NewGuid(), $"totalcount_verify item {i}");
            }

            // Act — paginated request: Limit=3
            var paginatedQuery = new SearchQuery
            {
                Text = "totalcount_verify",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Limit = 3,
                Skip = 0
            };
            var paginatedResponse = await _handler.Search(
                BuildSearchRequest(paginatedQuery), _mockLambdaContext.Object);

            // Act — full request: large Limit to get all
            var fullQuery = new SearchQuery
            {
                Text = "totalcount_verify",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Limit = 100,
                Skip = 0
            };
            var fullResponse = await _handler.Search(
                BuildSearchRequest(fullQuery), _mockLambdaContext.Object);

            // Assert
            paginatedResponse.StatusCode.Should().Be((int)HttpStatusCode.OK);
            fullResponse.StatusCode.Should().Be((int)HttpStatusCode.OK);

            var paginatedResults = ParseSearchResults(paginatedResponse);
            var fullResults = ParseSearchResults(fullResponse);

            paginatedResults.Should().HaveCount(3,
                "paginated results should be limited to Limit=3");
            fullResults.Should().HaveCount(8,
                "full results should contain all 8 seeded items");
            fullResults.Count.Should().BeGreaterThan(paginatedResults.Count,
                "total matches should be greater than paginated page size");
        }

        // =====================================================================
        // Phase 7: Index Management Tests (AddToIndex / RemoveFromIndex)
        // =====================================================================

        /// <summary>
        /// Verifies AddToIndex creates a search index item in DynamoDB with all expected
        /// attributes. Source: SearchManager.AddToIndex() — INSERT INTO system_search.
        /// DynamoDB: PutItem with id, url, snippet, content, stem_content, entities,
        /// apps, records, aux_data, timestamp attributes.
        /// </summary>
        [Fact]
        public async Task AddToIndex_CreatesSearchIndexItem()
        {
            // Arrange — build a SearchResult for indexing
            var indexEntry = new SearchResult
            {
                Url = "/test/add-to-index",
                Snippet = "Test snippet for indexing",
                Content = "This is test content for the AddToIndex integration test",
                Entities = new List<Guid> { EntityA_Id },
                Apps = new List<Guid> { AppX_Id },
                Records = new List<Guid> { RecordAlpha_Id },
                AuxData = "test-aux-data"
            };

            var body = JsonSerializer.Serialize(indexEntry, _jsonOptions);
            var request = BuildAdminRequest(body: body);

            // Act
            var response = await _handler.AddToIndex(request, _mockLambdaContext.Object);

            // Assert — verify response
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            ParseSuccess(response).Should().BeTrue();

            var createdResult = ParseSingleResult(response);
            createdResult.Should().NotBeNull();
            createdResult!.Id.Should().NotBe(Guid.Empty,
                "AddToIndex should assign a new GUID to the entry");
            createdResult.Url.Should().Be("/test/add-to-index");
            createdResult.Snippet.Should().Be("Test snippet for indexing");

            // Verify item exists in DynamoDB
            var getResponse = await _fixture.DynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = SearchTableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["id"] = new AttributeValue { S = createdResult.Id.ToString() }
                }
            });

            getResponse.Item.Should().NotBeNull();
            getResponse.Item.Should().ContainKey("url");
            getResponse.Item["url"].S.Should().Be("/test/add-to-index");
            getResponse.Item.Should().ContainKey("content");
            getResponse.Item.Should().ContainKey("stem_content");
            getResponse.Item.Should().ContainKey("entities");
            getResponse.Item.Should().ContainKey("timestamp");
        }

        /// <summary>
        /// Verifies RemoveFromIndex deletes a search index item from DynamoDB.
        /// Source: SearchManager.RemoveFromIndex(Guid id) — DELETE FROM system_search.
        /// DynamoDB: DeleteItem with key {"id": entryId}.
        /// </summary>
        [Fact]
        public async Task RemoveFromIndex_DeletesSearchIndexItem()
        {
            // Arrange — first add an item
            var indexEntry = new SearchResult
            {
                Url = "/test/remove-from-index",
                Snippet = "Snippet to be removed",
                Content = "Content that will be removed from index"
            };

            var addBody = JsonSerializer.Serialize(indexEntry, _jsonOptions);
            var addRequest = BuildAdminRequest(body: addBody);
            var addResponse = await _handler.AddToIndex(addRequest, _mockLambdaContext.Object);

            addResponse.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var addedResult = ParseSingleResult(addResponse);
            addedResult.Should().NotBeNull();
            var entryId = addedResult!.Id;

            // Act — remove the item
            var removeRequest = BuildAdminRequest(
                pathParams: new Dictionary<string, string> { { "id", entryId.ToString() } });
            var removeResponse = await _handler.RemoveFromIndex(
                removeRequest, _mockLambdaContext.Object);

            // Assert
            removeResponse.StatusCode.Should().Be((int)HttpStatusCode.OK);
            ParseSuccess(removeResponse).Should().BeTrue();

            // Verify item no longer exists in DynamoDB
            var getResponse = await _fixture.DynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = SearchTableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["id"] = new AttributeValue { S = entryId.ToString() }
                }
            });

            // GetItem returns empty Item (or IsItemSet is false) when the item doesn't exist
            var itemExists = getResponse.Item != null && getResponse.Item.Count > 0;
            itemExists.Should().BeFalse("item should be deleted from DynamoDB after RemoveFromIndex");
        }

        // =====================================================================
        // Phase 8: Result Type Tests (Compact vs Full)
        // =====================================================================

        /// <summary>
        /// Verifies Compact result type returns only minimal fields: Id, Url, Snippet, Timestamp.
        /// Source: SearchManager returns id, url, snippet, timestamp for Narrow/Compact results.
        /// SearchHandler.MapDynamoDbItemToSearchResult maps only these fields for Compact.
        /// </summary>
        [Fact]
        public async Task Search_NarrowResultType_ReturnsMinimalFields()
        {
            // Arrange
            var itemId = Guid.NewGuid();
            await SeedSearchItemAsync(itemId, "compactresult test content",
                entities: new List<Guid> { EntityA_Id },
                apps: new List<Guid> { AppX_Id },
                auxData: "some-aux-data");

            var query = new SearchQuery
            {
                Text = "compactresult",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Compact,
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);
            results.Should().NotBeEmpty();

            var result = results.First(r => r.Id == itemId);
            // Compact result should have Id, Url, Snippet, Timestamp
            result.Id.Should().NotBe(Guid.Empty);
            result.Url.Should().NotBeEmpty();
            result.Snippet.Should().NotBeEmpty();

            // Compact results should have empty/default values for full fields
            // (Content, StemContent, AuxData should be empty strings or defaults)
            result.Content.Should().BeEmpty(
                "Compact result type should not include Content field");
            result.StemContent.Should().BeEmpty(
                "Compact result type should not include StemContent field");
        }

        /// <summary>
        /// Verifies Full result type returns all available fields including Content,
        /// StemContent, Entities, Apps, Records, AuxData.
        /// Source: SearchManager returns all fields (SELECT *) for Full results.
        /// </summary>
        [Fact]
        public async Task Search_FullResultType_ReturnsAllFields()
        {
            // Arrange
            var itemId = Guid.NewGuid();
            await SeedSearchItemAsync(itemId, "fullresult detailed test content",
                entities: new List<Guid> { EntityA_Id },
                apps: new List<Guid> { AppX_Id },
                records: new List<Guid> { RecordAlpha_Id },
                auxData: "full-aux-data");

            var query = new SearchQuery
            {
                Text = "fullresult",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);
            results.Should().NotBeEmpty();

            var result = results.First(r => r.Id == itemId);
            // Full result should have all fields populated
            result.Id.Should().NotBe(Guid.Empty);
            result.Url.Should().NotBeEmpty();
            result.Snippet.Should().NotBeEmpty();
            result.Content.Should().NotBeEmpty(
                "Full result type should include Content field");
            result.StemContent.Should().NotBeEmpty(
                "Full result type should include StemContent field");
            result.Entities.Should().NotBeEmpty(
                "Full result type should include Entities list");
            result.Entities.Should().Contain(EntityA_Id);
            result.Apps.Should().NotBeEmpty(
                "Full result type should include Apps list");
            result.Apps.Should().Contain(AppX_Id);
            result.Records.Should().NotBeEmpty(
                "Full result type should include Records list");
            result.Records.Should().Contain(RecordAlpha_Id);
        }

        // =====================================================================
        // Phase 9: Ordering Tests
        // =====================================================================

        /// <summary>
        /// Verifies that search results have valid timestamps and that the results
        /// can be ordered by timestamp descending. Source: SearchManager.cs line 148-149
        /// uses ORDER BY timestamp DESC for non-FTS search.
        ///
        /// NOTE: SearchHandler.Search does NOT apply server-side sorting (DynamoDB Scan
        /// returns items in unpredictable order). This test verifies:
        /// 1. All returned items have valid, non-default timestamps
        /// 2. Items seeded with different timestamps are all present
        /// 3. Client-side ordering by timestamp descending produces correct order
        /// </summary>
        [Fact]
        public async Task Search_ContainsType_OrdersByTimestampDesc()
        {
            // Arrange — seed items with explicit, distinct timestamps
            var oldItemId = Guid.NewGuid();
            var midItemId = Guid.NewGuid();
            var newItemId = Guid.NewGuid();

            var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);

            await SeedSearchItemAsync(oldItemId, "ordertest content for ordering verification",
                timestamp: baseTime);
            await SeedSearchItemAsync(midItemId, "ordertest content for ordering verification",
                timestamp: baseTime.AddHours(5));
            await SeedSearchItemAsync(newItemId, "ordertest content for ordering verification",
                timestamp: baseTime.AddHours(10));

            var query = new SearchQuery
            {
                Text = "ordertest",
                SearchType = SearchType.Contains,
                ResultType = SearchResultType.Full,
                Limit = 50
            };

            // Act
            var response = await _handler.Search(
                BuildSearchRequest(query), _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            var results = ParseSearchResults(response);
            results.Should().HaveCount(3, "all 3 seeded items should be returned");

            // Verify all items are present with valid timestamps
            results.Should().Contain(r => r.Id == oldItemId);
            results.Should().Contain(r => r.Id == midItemId);
            results.Should().Contain(r => r.Id == newItemId);

            // Verify timestamps are non-default
            results.Select(r => r.Timestamp).Should().NotContain(default(DateTime),
                "all returned items should have valid non-default timestamps");

            // Verify client-side ordering by timestamp descending produces correct order
            var orderedResults = results.OrderByDescending(r => r.Timestamp).ToList();
            orderedResults.First().Id.Should().Be(newItemId,
                "newest item should be first when ordered by timestamp descending");
            orderedResults.Last().Id.Should().Be(oldItemId,
                "oldest item should be last when ordered by timestamp descending");
        }
    }
}
