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
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;

// Note: Assembly-level [LambdaSerializer(typeof(DefaultLambdaJsonSerializer))] attribute
// is already defined in EntityHandler.cs — only one per assembly is allowed.

namespace WebVellaErp.EntityManagement.Functions
{
    /// <summary>
    /// Lambda handler for search operations in the Entity Management bounded context.
    /// Replaces the PostgreSQL FTS-based SearchManager.cs and related search endpoints
    /// from WebApiController.cs with a DynamoDB GSI-based search approach.
    ///
    /// Adapts the monolith's <c>system_search</c> table paradigm to DynamoDB with
    /// Global Secondary Indexes for contains/FTS-like query patterns.
    ///
    /// Route mapping (API Gateway HTTP API v2):
    ///   POST   /v1/entity-management/search             → Search
    ///   POST   /v1/entity-management/search/index        → AddToIndex
    ///   DELETE /v1/entity-management/search/index/{id}   → RemoveFromIndex
    ///   POST   /v1/entity-management/search/quick        → QuickSearch
    ///
    /// Authorization: All endpoints require authenticated user with Read permission (JWT claims check).
    /// Events: Index mutations publish SNS domain events (entity-management.search.{indexed/deindexed}).
    /// </summary>
    public class SearchHandler
    {
        // ─── Dependencies (injected via constructor DI) ───────────────────

        /// <summary>DynamoDB client for search index table operations.</summary>
        private readonly IAmazonDynamoDB _dynamoDbClient;

        /// <summary>SNS client for publishing domain events after index mutations.</summary>
        private readonly IAmazonSimpleNotificationService _snsClient;

        /// <summary>Entity service for metadata lookups during QuickSearch enrichment.</summary>
        private readonly IEntityService _entityService;

        /// <summary>Structured JSON logger with correlation-ID propagation.</summary>
        private readonly ILogger<SearchHandler> _logger;

        // ─── Configuration ────────────────────────────────────────────────

        /// <summary>
        /// DynamoDB table name for the search index. Retrieved from SEARCH_TABLE_NAME environment variable.
        /// Defaults to "entity-management-search" when not configured.
        /// Replaces the monolith's <c>system_search</c> PostgreSQL table.
        /// </summary>
        private readonly string _searchTableName;

        /// <summary>
        /// SNS topic ARN for search domain events. Retrieved from SEARCH_TOPIC_ARN environment variable.
        /// When empty/null, event publishing is skipped with a warning log.
        /// </summary>
        private readonly string? _searchTopicArn;

        /// <summary>
        /// When true, detailed exception messages and stack traces are included in error responses.
        /// Replaces ErpSettings.DevelopmentMode from the monolith.
        /// Controlled by IS_LOCAL environment variable.
        /// </summary>
        private readonly bool _isDevelopmentMode;

        // ─── Constants ────────────────────────────────────────────────────

        /// <summary>
        /// Maximum number of results to return from DynamoDB in a single scan/query.
        /// Protects against unbounded scans consuming excessive read capacity.
        /// </summary>
        private const int MaxScanLimit = 1000;

        /// <summary>
        /// Default page size when client does not specify a limit.
        /// Matches the SearchQuery.Limit default of 20.
        /// </summary>
        private const int DefaultPageSize = 20;

        // ─── JSON Serialization Options ───────────────────────────────────

        /// <summary>
        /// Shared JsonSerializerOptions for System.Text.Json deserialization/serialization.
        /// PropertyNameCaseInsensitive ensures flexible request body parsing.
        /// </summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // ─── Text Processing Constants ────────────────────────────────────

        /// <summary>
        /// Common stop words for simplified text analysis. Replaces the monolith's
        /// FtsAnalyzer.cs Bulgarian stop-word list with a basic English stop-word set.
        /// Bulgarian FTS deferred per AAP §0.3.2.
        /// </summary>
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
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

        /// <summary>
        /// Delimiters for tokenizing search text. Matches the monolith FtsAnalyzer.cs
        /// splitting pattern using space, comma, period, semicolon, and common punctuation.
        /// </summary>
        private static readonly char[] TokenDelimiters = { ' ', ',', '.', ';', ':', '!', '?', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}', '"', '\'' , '\t', '\n', '\r' };

        // ─── Constructor ──────────────────────────────────────────────────

        /// <summary>
        /// Initializes a new instance of the SearchHandler with all required dependencies.
        /// Dependencies are resolved via the Lambda service provider (configured in Startup/Program).
        /// </summary>
        /// <param name="dynamoDbClient">DynamoDB client for search index table operations.</param>
        /// <param name="snsClient">SNS client for publishing domain events.</param>
        /// <param name="entityService">Entity metadata service for QuickSearch enrichment.</param>
        /// <param name="logger">Structured JSON logger with correlation-ID propagation.</param>
        public SearchHandler(
            IAmazonDynamoDB dynamoDbClient,
            IAmazonSimpleNotificationService snsClient,
            IEntityService entityService,
            ILogger<SearchHandler> logger)
        {
            _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _searchTableName = Environment.GetEnvironmentVariable("SEARCH_TABLE_NAME")
                ?? "entity-management-search";
            _searchTopicArn = Environment.GetEnvironmentVariable("SEARCH_TOPIC_ARN");
            _isDevelopmentMode = string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"), "true", StringComparison.OrdinalIgnoreCase);
        }

        // ═════════════════════════════════════════════════════════════════
        // PUBLIC LAMBDA HANDLER METHODS
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lambda handler for POST /v1/entity-management/search
        /// Performs search across the DynamoDB search index table, supporting both
        /// Contains (ILIKE) and FTS (full-text) search modes.
        ///
        /// Source: SearchManager.Search() — replaced PostgreSQL ILIKE / to_tsquery with
        /// DynamoDB Scan + FilterExpression using contains() function.
        ///
        /// Request body: <see cref="SearchQuery"/> JSON with Text, SearchType, ResultType,
        /// optional Entities/Apps/Records filter lists, Skip, and Limit.
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request containing SearchQuery in body.</param>
        /// <param name="context">Lambda execution context for logging and request correlation.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with ResponseModel envelope containing SearchResultList.</returns>

        /// <summary>
        /// Single entry point for managed .NET Lambda runtime (dotnet9).
        /// Routes API Gateway HTTP API v2 requests to the appropriate handler method
        /// based on HTTP method and request path.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var path = request.RawPath ?? request.RequestContext?.Http?.Path ?? string.Empty;
            var method = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? "GET";

            if (method == "GET")
                return await Search(request, context);
            else if (method == "GET")
                return await QuickSearch(request, context);

            // Default: route to Search
            return await Search(request, context);
        }

        public async Task<APIGatewayHttpApiV2ProxyResponse> Search(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "Search started. CorrelationId={CorrelationId}, FunctionName={FunctionName}",
                correlationId, context.FunctionName);

            try
            {
                // Permission gating: search requires Read permission.
                // Source: SecurityContext in SearchManager used for permission scoping.
                if (!HasPermission(request, EntityPermission.Read))
                {
                    _logger.LogWarning(
                        "Search access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Read permission required.",
                        correlationId);
                }

                // Parse and validate request body into SearchQuery.
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required. Provide a SearchQuery JSON object.",
                        correlationId);
                }

                SearchQuery? searchQuery;
                try
                {
                    searchQuery = System.Text.Json.JsonSerializer.Deserialize<SearchQuery>(
                        request.Body, _jsonOptions);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "Search invalid JSON body. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid JSON in request body.",
                        correlationId);
                }

                if (searchQuery == null || string.IsNullOrWhiteSpace(searchQuery.Text))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Search text is required.",
                        correlationId);
                }

                // Determine effective pagination values.
                // Source: SearchManager used SQL LIMIT/OFFSET; target uses client-side pagination
                // after DynamoDB Scan because DynamoDB Scan does not support offset natively.
                var skip = searchQuery.Skip ?? 0;
                var limit = (searchQuery.Limit.HasValue && searchQuery.Limit.Value > 0)
                    ? searchQuery.Limit.Value
                    : DefaultPageSize;

                _logger.LogInformation(
                    "Search executing. SearchType={SearchType}, ResultType={ResultType}, Text={Text}, " +
                    "Skip={Skip}, Limit={Limit}, CorrelationId={CorrelationId}",
                    searchQuery.SearchType, searchQuery.ResultType, searchQuery.Text,
                    skip, limit, correlationId);

                // Build DynamoDB Scan request with filter expressions.
                // Source: SearchManager built dynamic SQL WHERE clauses with ILIKE/tsquery.
                // Target: DynamoDB Scan with FilterExpression using contains() function.
                var scanRequest = BuildSearchScanRequest(searchQuery);

                var scanResponse = await _dynamoDbClient.ScanAsync(scanRequest);

                _logger.LogInformation(
                    "Search DynamoDB scan completed. ScannedCount={ScannedCount}, MatchCount={MatchCount}, " +
                    "CorrelationId={CorrelationId}",
                    scanResponse.ScannedCount, scanResponse.Count, correlationId);

                // Convert DynamoDB items to SearchResult models.
                var allResults = scanResponse.Items
                    .Select(item => MapDynamoDbItemToSearchResult(item, searchQuery.ResultType))
                    .ToList();

                // Apply additional entity/app/record client-side filtering if DynamoDB
                // filter expression could not fully handle the GUID list intersection.
                allResults = ApplyClientSideFilters(allResults, searchQuery);

                // Build paginated result list.
                // Source: SearchManager used SQL COUNT(*) OVER() for total count with LIMIT/OFFSET.
                // Target: total count from filtered result set, then apply skip/take.
                var totalCount = allResults.Count;
                var pagedResults = allResults
                    .Skip(skip)
                    .Take(limit)
                    .ToList();

                var resultList = new SearchResultList();
                resultList.TotalCount = totalCount;
                resultList.AddRange(pagedResults);

                // Build response envelope matching monolith API contract.
                var responseModel = new ResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Message = "Search completed successfully.",
                    Object = resultList
                };

                _logger.LogInformation(
                    "Search completed. TotalCount={TotalCount}, ReturnedCount={ReturnedCount}, " +
                    "CorrelationId={CorrelationId}",
                    totalCount, pagedResults.Count, correlationId);

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Search unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while executing search.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/entity-management/search/index
        /// Adds a content entry to the DynamoDB search index table.
        ///
        /// Source: SearchManager.AddToIndex() — replaced PostgreSQL INSERT INTO system_search
        /// with DynamoDB PutItem. Normalizes content, generates stem_content via simplified
        /// tokenization (FTS stemming deferred per AAP §0.3.2), and serializes GUID lists.
        ///
        /// Request body JSON:
        /// {
        ///   "url": "/path/to/content",
        ///   "snippet": "Human-readable snippet",
        ///   "content": "Full indexable content",
        ///   "entities": ["guid1", ...],
        ///   "apps": ["guid2", ...],
        ///   "records": ["guid3", ...],
        ///   "aux_data": "optional auxiliary data",
        ///   "timestamp": "optional ISO 8601 UTC override"
        /// }
        ///
        /// After successful indexing, publishes SNS event: entity-management.search.indexed
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request containing index entry in body.</param>
        /// <param name="context">Lambda execution context for logging and request correlation.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with ResponseModel envelope (200 OK or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> AddToIndex(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "AddToIndex started. CorrelationId={CorrelationId}, FunctionName={FunctionName}",
                correlationId, context.FunctionName);

            try
            {
                // Permission gating: indexing requires admin/create permission.
                if (!HasPermission(request, EntityPermission.Create))
                {
                    _logger.LogWarning(
                        "AddToIndex access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Create permission required for index operations.",
                        correlationId);
                }

                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                // Parse the index entry from request body.
                SearchResult? indexEntry;
                try
                {
                    indexEntry = System.Text.Json.JsonSerializer.Deserialize<SearchResult>(
                        request.Body, _jsonOptions);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "AddToIndex invalid JSON body. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid JSON in request body.",
                        correlationId);
                }

                if (indexEntry == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid index entry data in request body.",
                        correlationId);
                }

                // Generate a new unique ID for the search index entry.
                // Source: SearchManager.AddToIndex() generated Guid.NewGuid() for each entry.
                var entryId = Guid.NewGuid();

                // Normalize content values.
                // Source: SearchManager lines 190-193 normalized null strings and lowercased content.
                var url = indexEntry.Url ?? string.Empty;
                var snippet = indexEntry.Snippet ?? string.Empty;
                var content = (indexEntry.Content ?? string.Empty).ToLower();
                var auxData = indexEntry.AuxData ?? string.Empty;

                // Generate stem content via simplified text analysis.
                // Source: SearchManager used FtsAnalyzer.ProcessText() for Bulgarian stemming.
                // Target: simplified lowercasing + tokenization + stop-word removal (FTS deferred per AAP §0.3.2).
                var stemContent = GenerateStemContent(content);

                // Determine timestamp: use provided value or UTC now.
                // Source: SearchManager allowed timestamp override via parameter.
                var timestamp = indexEntry.Timestamp != default
                    ? indexEntry.Timestamp
                    : DateTime.UtcNow;

                // Serialize GUID lists to JSON strings for DynamoDB storage.
                // Source: SearchManager used JsonConvert.SerializeObject(query.Entities) for storage.
                var entitiesJson = JsonConvert.SerializeObject(indexEntry.Entities ?? new List<Guid>());
                var appsJson = JsonConvert.SerializeObject(indexEntry.Apps ?? new List<Guid>());
                var recordsJson = JsonConvert.SerializeObject(indexEntry.Records ?? new List<Guid>());

                // Build DynamoDB PutItem request.
                // Source: SearchManager INSERT INTO system_search (id, url, snippet, content, stem_content,
                //         entities, apps, records, aux_data, timestamp).
                var item = new Dictionary<string, AttributeValue>
                {
                    ["id"] = new AttributeValue { S = entryId.ToString() },
                    ["url"] = new AttributeValue { S = url },
                    ["snippet"] = new AttributeValue { S = snippet },
                    ["content"] = new AttributeValue { S = content },
                    ["stem_content"] = new AttributeValue { S = stemContent },
                    ["entities"] = new AttributeValue { S = entitiesJson },
                    ["apps"] = new AttributeValue { S = appsJson },
                    ["records"] = new AttributeValue { S = recordsJson },
                    ["aux_data"] = new AttributeValue { S = auxData },
                    ["timestamp"] = new AttributeValue { S = timestamp.ToString("O") }
                };

                var putRequest = new PutItemRequest
                {
                    TableName = _searchTableName,
                    Item = item
                };

                await _dynamoDbClient.PutItemAsync(putRequest);

                _logger.LogInformation(
                    "AddToIndex entry created. EntryId={EntryId}, Url={Url}, " +
                    "ContentLength={ContentLength}, StemContentLength={StemContentLength}, " +
                    "CorrelationId={CorrelationId}",
                    entryId, url, content.Length, stemContent.Length, correlationId);

                // Publish SNS domain event: entity-management.search.indexed
                // Source: monolith post-hooks were synchronous; target uses async SNS events
                // per AAP §0.7.2 Hook-to-Event migration.
                await PublishDomainEvent(
                    "entity-management.search.indexed",
                    new
                    {
                        searchEntryId = entryId,
                        url,
                        entitiesCount = indexEntry.Entities?.Count ?? 0,
                        recordsCount = indexEntry.Records?.Count ?? 0
                    },
                    correlationId);

                // Build success response with created entry.
                var createdResult = new SearchResult
                {
                    Id = entryId,
                    Url = url,
                    Snippet = snippet,
                    Content = content,
                    StemContent = stemContent,
                    Entities = indexEntry.Entities ?? new List<Guid>(),
                    Apps = indexEntry.Apps ?? new List<Guid>(),
                    Records = indexEntry.Records ?? new List<Guid>(),
                    AuxData = auxData,
                    Timestamp = timestamp
                };

                var responseModel = new ResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Message = "Content indexed successfully.",
                    Object = createdResult
                };

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AddToIndex unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while indexing content.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for DELETE /v1/entity-management/search/index/{id}
        /// Removes a content entry from the DynamoDB search index by its unique ID.
        ///
        /// Source: SearchManager.RemoveFromIndex(Guid id) — replaced PostgreSQL
        /// DELETE FROM system_search WHERE id = @id with DynamoDB DeleteItem.
        ///
        /// NOTE: The source SearchManager.RemoveFromIndex had a bug on line 235 where it
        /// used Guid.NewGuid() instead of the passed id parameter. This implementation
        /// correctly uses the provided id.
        ///
        /// After successful removal, publishes SNS event: entity-management.search.deindexed
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request with {id} path parameter.</param>
        /// <param name="context">Lambda execution context for logging and request correlation.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with BaseResponseModel envelope (200 OK or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> RemoveFromIndex(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "RemoveFromIndex started. CorrelationId={CorrelationId}, FunctionName={FunctionName}",
                correlationId, context.FunctionName);

            try
            {
                // Permission gating: deindexing requires admin/delete permission.
                if (!HasPermission(request, EntityPermission.Delete))
                {
                    _logger.LogWarning(
                        "RemoveFromIndex access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Delete permission required for deindex operations.",
                        correlationId);
                }

                // Extract the search entry ID from path parameters.
                // Route: DELETE /v1/entity-management/search/index/{id}
                var idStr = GetPathParameter(request, "id");
                if (string.IsNullOrWhiteSpace(idStr))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Search entry ID is required in the path.",
                        correlationId);
                }

                if (!Guid.TryParse(idStr, out var entryId))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Invalid search entry ID format: '{idStr}'. Expected a valid GUID.",
                        correlationId);
                }

                _logger.LogInformation(
                    "RemoveFromIndex deleting entry. EntryId={EntryId}, CorrelationId={CorrelationId}",
                    entryId, correlationId);

                // Build DynamoDB DeleteItem request.
                // Source: SearchManager DELETE FROM system_search WHERE id = @id
                // NOTE: Source had bug using Guid.NewGuid() — this uses the correct passed id.
                var deleteRequest = new DeleteItemRequest
                {
                    TableName = _searchTableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["id"] = new AttributeValue { S = entryId.ToString() }
                    },
                    ReturnValues = ReturnValue.ALL_OLD
                };

                var deleteResponse = await _dynamoDbClient.DeleteItemAsync(deleteRequest);

                // Check if the item actually existed before deletion.
                var itemExisted = deleteResponse.Attributes != null && deleteResponse.Attributes.Count > 0;

                if (!itemExisted)
                {
                    _logger.LogWarning(
                        "RemoveFromIndex entry not found. EntryId={EntryId}, CorrelationId={CorrelationId}",
                        entryId, correlationId);
                }

                // Publish SNS domain event: entity-management.search.deindexed
                await PublishDomainEvent(
                    "entity-management.search.deindexed",
                    new { searchEntryId = entryId, existed = itemExisted },
                    correlationId);

                var responseModel = new BaseResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Message = itemExisted
                        ? "Search index entry removed successfully."
                        : "Search index entry was not found (may have been previously removed)."
                };

                _logger.LogInformation(
                    "RemoveFromIndex completed. EntryId={EntryId}, ItemExisted={ItemExisted}, " +
                    "CorrelationId={CorrelationId}",
                    entryId, itemExisted, correlationId);

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "RemoveFromIndex unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while removing from index.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/entity-management/search/quick
        /// Performs a quick-search across entity records with dynamic query filters,
        /// supporting EQ, contains, startsWith, and FTS filter modes.
        ///
        /// Source: WebApiController.GetQuickSearch() — replaced in-process RecordManager.Find()
        /// with DynamoDB search index queries. Supports force-filters, multi-field matching,
        /// sort options, and paginated results with entity metadata enrichment.
        ///
        /// Request body JSON:
        /// {
        ///   "query": "search text",
        ///   "entity_name": "account",
        ///   "lookup_fields": ["name", "email"],
        ///   "return_fields": ["id", "name", "email", "created_on"],
        ///   "sort_field": "name",
        ///   "sort_type": "asc",
        ///   "match_method": "contains",
        ///   "match_all_fields": false,
        ///   "skip_records": 0,
        ///   "limit_records": 5,
        ///   "find_type": "records",
        ///   "force_filters": "status:string:active,priority:int:1"
        /// }
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request containing quick search params in body.</param>
        /// <param name="context">Lambda execution context for logging and request correlation.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with ResponseModel envelope containing records/count.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> QuickSearch(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "QuickSearch started. CorrelationId={CorrelationId}, FunctionName={FunctionName}",
                correlationId, context.FunctionName);

            try
            {
                // Permission gating: quick search requires Read permission.
                if (!HasPermission(request, EntityPermission.Read))
                {
                    _logger.LogWarning(
                        "QuickSearch access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Read permission required.",
                        correlationId);
                }

                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                // Parse quick-search parameters from request body.
                QuickSearchRequest? quickSearchReq;
                try
                {
                    quickSearchReq = System.Text.Json.JsonSerializer.Deserialize<QuickSearchRequest>(
                        request.Body, _jsonOptions);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "QuickSearch invalid JSON body. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid JSON in request body.",
                        correlationId);
                }

                if (quickSearchReq == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid quick search request.",
                        correlationId);
                }

                // Validate required parameters.
                // Source: WebApiController checked entityName, lookupFieldsCsv, query, returnFieldsCsv.
                if (string.IsNullOrWhiteSpace(quickSearchReq.EntityName) ||
                    quickSearchReq.LookupFields == null || quickSearchReq.LookupFields.Count == 0 ||
                    string.IsNullOrWhiteSpace(quickSearchReq.Query) ||
                    quickSearchReq.ReturnFields == null || quickSearchReq.ReturnFields.Count == 0)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Missing required params. entity_name, lookup_fields, query, and return_fields are all required.",
                        correlationId);
                }

                _logger.LogInformation(
                    "QuickSearch executing. EntityName={EntityName}, MatchMethod={MatchMethod}, " +
                    "Query={Query}, LookupFields={LookupFields}, Skip={Skip}, Limit={Limit}, " +
                    "CorrelationId={CorrelationId}",
                    quickSearchReq.EntityName, quickSearchReq.MatchMethod, quickSearchReq.Query,
                    string.Join(",", quickSearchReq.LookupFields),
                    quickSearchReq.SkipRecords, quickSearchReq.LimitRecords, correlationId);

                // Resolve entity metadata for enrichment and validation.
                // Source: WebApiController instantiated RecordManager directly.
                // Target: use IEntityService.GetEntity() for metadata resolution.
                Entity? entity = null;
                try
                {
                    entity = await _entityService.GetEntity(quickSearchReq.EntityName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "QuickSearch entity lookup failed. EntityName={EntityName}, CorrelationId={CorrelationId}",
                        quickSearchReq.EntityName, correlationId);
                }

                if (entity == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Entity '{quickSearchReq.EntityName}' not found.",
                        correlationId);
                }

                // Build DynamoDB Scan for quick-search matching the source's dynamic filter logic.
                // Source: WebApiController built QueryObject filters per matchMethod (EQ, contains, startsWith, FTS).
                var scanRequest = BuildQuickSearchScanRequest(quickSearchReq, entity);

                var scanResponse = await _dynamoDbClient.ScanAsync(scanRequest);

                _logger.LogInformation(
                    "QuickSearch DynamoDB scan completed. ScannedCount={ScannedCount}, MatchCount={MatchCount}, " +
                    "CorrelationId={CorrelationId}",
                    scanResponse.ScannedCount, scanResponse.Count, correlationId);

                // Parse DynamoDB items to response records.
                var allRecords = scanResponse.Items
                    .Select(MapDynamoDbItemToRecord)
                    .ToList();

                // Apply force-filter client-side filtering if any.
                // Source: WebApiController parsed forceFiltersCsv with format "fieldName:dataType:eqValue".
                if (!string.IsNullOrWhiteSpace(quickSearchReq.ForceFilters))
                {
                    allRecords = ApplyForceFilters(allRecords, quickSearchReq.ForceFilters);
                }

                // Apply sorting.
                // Source: WebApiController built QuerySortObject list.
                allRecords = ApplySorting(allRecords, quickSearchReq.SortField, quickSearchReq.SortType);

                // Build response based on findType.
                // Source: WebApiController supported "records", "count", "records-and-count"/"records&count".
                var responseObject = new Dictionary<string, object>();
                var findType = (quickSearchReq.FindType ?? "records").ToLowerInvariant();

                if (findType == "records" || findType == "records-and-count" || findType == "records&count")
                {
                    var pagedRecords = allRecords
                        .Skip(quickSearchReq.SkipRecords)
                        .Take(quickSearchReq.LimitRecords > 0 ? quickSearchReq.LimitRecords : 5)
                        .ToList();
                    responseObject["records"] = pagedRecords;
                }

                if (findType == "count" || findType == "records-and-count" || findType == "records&count")
                {
                    responseObject["count"] = allRecords.Count;
                }

                var responseModel = new ResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Message = "Quick search success",
                    Object = responseObject
                };

                _logger.LogInformation(
                    "QuickSearch completed. EntityName={EntityName}, TotalMatches={TotalMatches}, " +
                    "CorrelationId={CorrelationId}",
                    quickSearchReq.EntityName, allRecords.Count, correlationId);

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "QuickSearch unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred during quick search.",
                    correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // PRIVATE HELPER METHODS — SEARCH QUERY BUILDING
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds a DynamoDB ScanRequest for the main Search operation.
        /// Translates SearchQuery parameters into DynamoDB FilterExpression with
        /// contains() function, replacing the monolith's PostgreSQL ILIKE / to_tsquery patterns.
        ///
        /// Source transformation:
        ///   - Contains mode: ILIKE '%word%' per word → contains(content, :word_N)
        ///   - FTS mode: to_tsquery(stem_content) → contains(stem_content, :stem_N)
        ///   - Entity filter: ILIKE on entities JSON → contains(entities, :entityId)
        ///   - App filter: ILIKE on apps JSON → contains(apps, :appId)
        ///   - Record filter: ILIKE on records JSON → contains(records, :recordId)
        /// </summary>
        /// <param name="query">The parsed SearchQuery from the request body.</param>
        /// <returns>A fully constructed ScanRequest for DynamoDB.</returns>
        private ScanRequest BuildSearchScanRequest(SearchQuery query)
        {
            var filterExpressions = new List<string>();
            var expressionAttributeValues = new Dictionary<string, AttributeValue>();
            var expressionAttributeNames = new Dictionary<string, string>();

            // Build text search filter.
            // Source: SearchManager lowercased text, split by space, deduplicated,
            // then built ILIKE '%word%' per word (Contains) or to_tsquery (FTS).
            var searchText = query.Text.ToLower().Trim();

            if (query.SearchType == SearchType.Contains)
            {
                // Contains mode: split text into words, build AND filter with contains() per word.
                // Source: SearchManager lines 60-78 — deduplicates words, builds AND conditions.
                var words = searchText
                    .Split(TokenDelimiters, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim())
                    .Where(w => !string.IsNullOrWhiteSpace(w))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (words.Count > 0)
                {
                    var wordFilters = new List<string>();
                    for (var i = 0; i < words.Count; i++)
                    {
                        var paramName = $":word_{i}";
                        wordFilters.Add($"contains(#content, {paramName})");
                        expressionAttributeValues[paramName] = new AttributeValue { S = words[i] };
                    }
                    expressionAttributeNames["#content"] = "content";
                    filterExpressions.Add($"({string.Join(" AND ", wordFilters)})");
                }
            }
            else if (query.SearchType == SearchType.Fts)
            {
                // FTS mode: tokenize and remove stop words, then search stem_content.
                // Source: SearchManager used FtsAnalyzer.ProcessText() for stemming,
                // then built to_tsquery against stem_content column.
                // Target: simplified tokenization + stop-word removal + contains() on stem_content.
                var tokens = searchText
                    .Split(TokenDelimiters, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim().ToLower())
                    .Where(t => !string.IsNullOrWhiteSpace(t) && !StopWords.Contains(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (tokens.Count > 0)
                {
                    var tokenFilters = new List<string>();
                    for (var i = 0; i < tokens.Count; i++)
                    {
                        var paramName = $":stem_{i}";
                        tokenFilters.Add($"contains(#stem_content, {paramName})");
                        expressionAttributeValues[paramName] = new AttributeValue { S = tokens[i] };
                    }
                    expressionAttributeNames["#stem_content"] = "stem_content";
                    filterExpressions.Add($"({string.Join(" AND ", tokenFilters)})");
                }
            }

            // Build entity filter.
            // Source: SearchManager filtered entities via ILIKE '%"guidValue"%' on JSON column.
            if (query.Entities != null && query.Entities.Any())
            {
                var entityFilters = new List<string>();
                for (var i = 0; i < query.Entities.Count; i++)
                {
                    var paramName = $":entity_{i}";
                    entityFilters.Add($"contains(#entities, {paramName})");
                    expressionAttributeValues[paramName] = new AttributeValue { S = query.Entities[i].ToString() };
                }
                expressionAttributeNames["#entities"] = "entities";
                filterExpressions.Add($"({string.Join(" OR ", entityFilters)})");
            }

            // Build app filter.
            if (query.Apps != null && query.Apps.Any())
            {
                var appFilters = new List<string>();
                for (var i = 0; i < query.Apps.Count; i++)
                {
                    var paramName = $":app_{i}";
                    appFilters.Add($"contains(#apps, {paramName})");
                    expressionAttributeValues[paramName] = new AttributeValue { S = query.Apps[i].ToString() };
                }
                expressionAttributeNames["#apps"] = "apps";
                filterExpressions.Add($"({string.Join(" OR ", appFilters)})");
            }

            // Build record filter.
            if (query.Records != null && query.Records.Any())
            {
                var recordFilters = new List<string>();
                for (var i = 0; i < query.Records.Count; i++)
                {
                    var paramName = $":record_{i}";
                    recordFilters.Add($"contains(#records, {paramName})");
                    expressionAttributeValues[paramName] = new AttributeValue { S = query.Records[i].ToString() };
                }
                expressionAttributeNames["#records"] = "records";
                filterExpressions.Add($"({string.Join(" OR ", recordFilters)})");
            }

            var scanRequest = new ScanRequest
            {
                TableName = _searchTableName,
                Limit = MaxScanLimit
            };

            if (filterExpressions.Count > 0)
            {
                scanRequest.FilterExpression = string.Join(" AND ", filterExpressions);
            }

            if (expressionAttributeValues.Count > 0)
            {
                scanRequest.ExpressionAttributeValues = expressionAttributeValues;
            }

            if (expressionAttributeNames.Count > 0)
            {
                scanRequest.ExpressionAttributeNames = expressionAttributeNames;
            }

            return scanRequest;
        }

        /// <summary>
        /// Builds a DynamoDB ScanRequest for the QuickSearch operation.
        /// Translates quick-search parameters (matchMethod, lookupFields, forceFilters)
        /// into DynamoDB FilterExpression, replacing the monolith's QueryObject-based
        /// RecordManager.Find() call.
        ///
        /// Source: WebApiController.GetQuickSearch() built QueryObject filters per matchMethod:
        ///   - EQ: exact match → attribute_exists + equality check
        ///   - contains: partial match → contains(field, :value)
        ///   - startsWith: prefix match → begins_with(field, :value)
        ///   - FTS: full-text → contains(field, :value) on tokenized content
        /// </summary>
        /// <param name="quickSearch">The parsed QuickSearchRequest.</param>
        /// <param name="entity">The resolved entity metadata for field validation.</param>
        /// <returns>A fully constructed ScanRequest for DynamoDB.</returns>
        private ScanRequest BuildQuickSearchScanRequest(QuickSearchRequest quickSearch, Entity entity)
        {
            var filterExpressions = new List<string>();
            var expressionAttributeValues = new Dictionary<string, AttributeValue>();
            var expressionAttributeNames = new Dictionary<string, string>();

            // Entity name filter — restrict scan to records of the target entity.
            expressionAttributeNames["#entity_name"] = "entity_name";
            expressionAttributeValues[":entity_name"] = new AttributeValue { S = quickSearch.EntityName };
            filterExpressions.Add("#entity_name = :entity_name");

            // Build field-level search filters based on matchMethod.
            // Source: WebApiController switch on matchMethod (EQ, contains, startsWith, FTS).
            var matchMethod = (quickSearch.MatchMethod ?? "EQ").ToLowerInvariant();
            var queryText = quickSearch.Query;
            var lookupFields = quickSearch.LookupFields;
            var matchAllFields = quickSearch.MatchAllFields;

            var fieldFilterExpressions = new List<string>();

            for (var i = 0; i < lookupFields.Count; i++)
            {
                var fieldName = lookupFields[i];
                var safeFieldAlias = $"#lookup_{i}";
                var valueAlias = $":lookup_{i}";

                expressionAttributeNames[safeFieldAlias] = fieldName;
                expressionAttributeValues[valueAlias] = new AttributeValue { S = queryText };

                switch (matchMethod)
                {
                    case "contains":
                        fieldFilterExpressions.Add($"contains({safeFieldAlias}, {valueAlias})");
                        break;
                    case "startswith":
                        fieldFilterExpressions.Add($"begins_with({safeFieldAlias}, {valueAlias})");
                        break;
                    case "fts":
                        // FTS mode: use contains on tokenized content similar to standard contains.
                        fieldFilterExpressions.Add($"contains({safeFieldAlias}, {valueAlias})");
                        break;
                    default: // EQ — exact match
                        fieldFilterExpressions.Add($"{safeFieldAlias} = {valueAlias}");
                        break;
                }
            }

            if (fieldFilterExpressions.Count > 0)
            {
                var joiner = matchAllFields ? " AND " : " OR ";
                filterExpressions.Add($"({string.Join(joiner, fieldFilterExpressions)})");
            }

            var scanRequest = new ScanRequest
            {
                TableName = _searchTableName,
                Limit = MaxScanLimit
            };

            if (filterExpressions.Count > 0)
            {
                scanRequest.FilterExpression = string.Join(" AND ", filterExpressions);
            }

            if (expressionAttributeValues.Count > 0)
            {
                scanRequest.ExpressionAttributeValues = expressionAttributeValues;
            }

            if (expressionAttributeNames.Count > 0)
            {
                scanRequest.ExpressionAttributeNames = expressionAttributeNames;
            }

            return scanRequest;
        }

        // ═════════════════════════════════════════════════════════════════
        // PRIVATE HELPER METHODS — RESULT MAPPING
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Maps a DynamoDB item (Dictionary of AttributeValues) to a SearchResult model.
        /// Handles both Compact and Full result types.
        ///
        /// Source: SearchManager mapped DataRow columns to SearchResult properties.
        /// Target: maps DynamoDB string attributes to SearchResult with JSON-deserialized GUID lists.
        ///
        /// Compact result type returns only: Id, Url, Snippet, Timestamp.
        /// Full result type includes all fields: Content, StemContent, Entities, Apps, Records, AuxData.
        /// </summary>
        /// <param name="item">The DynamoDB item attributes.</param>
        /// <param name="resultType">Whether to return compact or full result.</param>
        /// <returns>A populated SearchResult instance.</returns>
        private static SearchResult MapDynamoDbItemToSearchResult(
            Dictionary<string, AttributeValue> item, SearchResultType resultType)
        {
            var result = new SearchResult
            {
                Id = GetGuidAttribute(item, "id"),
                Url = GetStringAttribute(item, "url"),
                Snippet = GetStringAttribute(item, "snippet"),
                Timestamp = GetDateTimeAttribute(item, "timestamp")
            };

            if (resultType == SearchResultType.Full)
            {
                result.Content = GetStringAttribute(item, "content");
                result.StemContent = GetStringAttribute(item, "stem_content");
                result.AuxData = GetStringAttribute(item, "aux_data");

                // Deserialize JSON-serialized GUID lists from DynamoDB string attributes.
                // Source: SearchManager serialized with JsonConvert.SerializeObject(query.Entities).
                result.Entities = DeserializeGuidList(GetStringAttribute(item, "entities"));
                result.Apps = DeserializeGuidList(GetStringAttribute(item, "apps"));
                result.Records = DeserializeGuidList(GetStringAttribute(item, "records"));
            }

            return result;
        }

        /// <summary>
        /// Maps a DynamoDB item to a generic record dictionary for QuickSearch responses.
        /// Returns all non-system attributes as string key-value pairs.
        /// </summary>
        /// <param name="item">The DynamoDB item attributes.</param>
        /// <returns>A dictionary representing the record fields.</returns>
        private static Dictionary<string, object?> MapDynamoDbItemToRecord(
            Dictionary<string, AttributeValue> item)
        {
            var record = new Dictionary<string, object?>();

            foreach (var kvp in item)
            {
                if (kvp.Value.S != null)
                {
                    record[kvp.Key] = kvp.Value.S;
                }
                else if (kvp.Value.N != null)
                {
                    record[kvp.Key] = kvp.Value.N;
                }
                else if (kvp.Value.BOOL != false || kvp.Value.IsBOOLSet)
                {
                    record[kvp.Key] = kvp.Value.BOOL;
                }
                else if (kvp.Value.NULL)
                {
                    record[kvp.Key] = null;
                }
                else if (kvp.Value.L != null && kvp.Value.L.Count > 0)
                {
                    record[kvp.Key] = kvp.Value.L.Select(v => v.S ?? v.N ?? "(complex)").ToList();
                }
                else if (kvp.Value.M != null && kvp.Value.M.Count > 0)
                {
                    record[kvp.Key] = MapDynamoDbItemToRecord(kvp.Value.M);
                }
                else
                {
                    record[kvp.Key] = kvp.Value.S;
                }
            }

            return record;
        }

        // ═════════════════════════════════════════════════════════════════
        // PRIVATE HELPER METHODS — FILTERING AND SORTING
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Applies additional client-side filters for entity/app/record GUID list matching.
        /// DynamoDB contains() on JSON strings may produce false positives for partial GUID matches;
        /// this method performs exact GUID matching on deserialized lists.
        /// </summary>
        /// <param name="results">The initial result set from DynamoDB.</param>
        /// <param name="query">The original search query with filter criteria.</param>
        /// <returns>Filtered result list.</returns>
        private static List<SearchResult> ApplyClientSideFilters(
            List<SearchResult> results, SearchQuery query)
        {
            if (query.Entities != null && query.Entities.Any())
            {
                results = results
                    .Where(r => r.Entities != null &&
                                query.Entities.Any(e => r.Entities.Contains(e)))
                    .ToList();
            }

            if (query.Apps != null && query.Apps.Any())
            {
                results = results
                    .Where(r => r.Apps != null &&
                                query.Apps.Any(a => r.Apps.Contains(a)))
                    .ToList();
            }

            if (query.Records != null && query.Records.Any())
            {
                results = results
                    .Where(r => r.Records != null &&
                                query.Records.Any(rec => r.Records.Contains(rec)))
                    .ToList();
            }

            return results;
        }

        /// <summary>
        /// Applies force-filter conditions to QuickSearch results.
        /// Source: WebApiController parsed forceFiltersCsv with format "fieldName:dataType:eqValue".
        /// Supports data types: guid, bool, datetime, int, string.
        /// </summary>
        /// <param name="records">The unfiltered record list.</param>
        /// <param name="forceFiltersCsv">Comma-separated force filter definitions.</param>
        /// <returns>Filtered record list.</returns>
        private static List<Dictionary<string, object?>> ApplyForceFilters(
            List<Dictionary<string, object?>> records, string forceFiltersCsv)
        {
            if (string.IsNullOrWhiteSpace(forceFiltersCsv))
                return records;

            foreach (var forceFilter in forceFiltersCsv.Split(','))
            {
                var filterParts = forceFilter.Split(':');
                if (filterParts.Length != 3)
                    continue;

                var fieldName = filterParts[0];
                var dataType = filterParts[1].ToLowerInvariant();
                var filterValue = filterParts[2];

                records = records.Where(record =>
                {
                    if (!record.TryGetValue(fieldName, out var fieldValue) || fieldValue == null)
                        return false;

                    var fieldStr = fieldValue.ToString() ?? string.Empty;

                    switch (dataType)
                    {
                        case "guid":
                            return Guid.TryParse(fieldStr, out var fieldGuid) &&
                                   Guid.TryParse(filterValue, out var filterGuid) &&
                                   fieldGuid == filterGuid;

                        case "bool":
                            return string.Equals(fieldStr, filterValue, StringComparison.OrdinalIgnoreCase);

                        case "datetime":
                            return DateTime.TryParse(fieldStr, out var fieldDate) &&
                                   DateTime.TryParse(filterValue, out var filterDate) &&
                                   fieldDate == filterDate;

                        case "int":
                            return long.TryParse(fieldStr, out var fieldLong) &&
                                   long.TryParse(filterValue, out var filterLong) &&
                                   fieldLong == filterLong;

                        case "string":
                        default:
                            return string.Equals(fieldStr, filterValue, StringComparison.OrdinalIgnoreCase);
                    }
                }).ToList();
            }

            return records;
        }

        /// <summary>
        /// Applies sorting to QuickSearch results.
        /// Source: WebApiController built QuerySortObject with field name and ascending/descending direction.
        /// </summary>
        /// <param name="records">The unsorted record list.</param>
        /// <param name="sortField">The field name to sort by (null = no sorting).</param>
        /// <param name="sortType">Sort direction: "asc" or "desc".</param>
        /// <returns>Sorted record list.</returns>
        private static List<Dictionary<string, object?>> ApplySorting(
            List<Dictionary<string, object?>> records, string? sortField, string? sortType)
        {
            if (string.IsNullOrWhiteSpace(sortField))
                return records;

            var isDescending = string.Equals(sortType, "desc", StringComparison.OrdinalIgnoreCase);

            return isDescending
                ? records.OrderByDescending(r =>
                    r.TryGetValue(sortField, out var v) ? v?.ToString() ?? string.Empty : string.Empty).ToList()
                : records.OrderBy(r =>
                    r.TryGetValue(sortField, out var v) ? v?.ToString() ?? string.Empty : string.Empty).ToList();
        }

        // ═════════════════════════════════════════════════════════════════
        // PRIVATE HELPER METHODS — TEXT PROCESSING
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Generates simplified stem content for full-text search indexing.
        /// Replaces the monolith's FtsAnalyzer.ProcessText() which used Bulgarian BulStem
        /// stemmer + stop-word removal. Per AAP §0.3.2, Bulgarian FTS is deferred;
        /// this implementation performs basic lowercasing, tokenization, and English stop-word removal.
        ///
        /// Source: FtsAnalyzer.cs split on delimiters, filtered stop words, applied BulStem.Stemmer.
        /// Target: lowercase → tokenize → remove stop words → rejoin with spaces.
        /// </summary>
        /// <param name="content">The raw content string to process.</param>
        /// <returns>Processed stem content string with stop words removed.</returns>
        private static string GenerateStemContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;

            var tokens = content
                .ToLower()
                .Split(TokenDelimiters, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length > 1 && !StopWords.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return string.Join(" ", tokens);
        }

        // ═════════════════════════════════════════════════════════════════
        // PRIVATE HELPER METHODS — DYNAMODB ATTRIBUTE EXTRACTION
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extracts a string attribute from a DynamoDB item, returning empty string if not present.
        /// </summary>
        private static string GetStringAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            return item.TryGetValue(key, out var attr) && attr.S != null ? attr.S : string.Empty;
        }

        /// <summary>
        /// Extracts a GUID attribute from a DynamoDB item string value, returning Guid.Empty if invalid.
        /// </summary>
        private static Guid GetGuidAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            var str = GetStringAttribute(item, key);
            return Guid.TryParse(str, out var guid) ? guid : Guid.Empty;
        }

        /// <summary>
        /// Extracts a DateTime attribute from a DynamoDB item ISO 8601 string, returning UTC now if invalid.
        /// </summary>
        private static DateTime GetDateTimeAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            var str = GetStringAttribute(item, key);
            return DateTime.TryParse(str, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt
                : DateTime.UtcNow;
        }

        /// <summary>
        /// Deserializes a JSON-serialized list of GUIDs from a DynamoDB string attribute.
        /// Source: SearchManager stored GUID lists via JsonConvert.SerializeObject().
        /// Returns an empty list if deserialization fails.
        /// </summary>
        /// <param name="json">The JSON string containing a GUID array.</param>
        /// <returns>A list of parsed GUIDs.</returns>
        private static List<Guid> DeserializeGuidList(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<Guid>();

            try
            {
                return JsonConvert.DeserializeObject<List<Guid>>(json) ?? new List<Guid>();
            }
            catch
            {
                return new List<Guid>();
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // PRIVATE HELPER METHODS — AUTH, RESPONSE, EVENTS
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks whether the requesting user has the specified entity permission.
        /// For search operations, Read permission requires an authenticated user.
        /// For index mutations, Create/Delete permissions require administrator role.
        /// Source: SecurityContext.HasEntityPermission() from the monolith.
        /// </summary>
        /// <param name="request">The incoming API Gateway request.</param>
        /// <param name="permission">The required permission level.</param>
        /// <returns>True if the user has the required permission.</returns>
        private bool HasPermission(APIGatewayHttpApiV2ProxyRequest request, EntityPermission permission)
        {
            // Read permission: any authenticated user can search.
            if (permission == EntityPermission.Read)
            {
                return IsAuthenticatedUser(request);
            }

            // Create/Update/Delete permissions: require administrator role.
            _logger.LogDebug(
                "Permission check: EntityPermission.{Permission} requires admin role (SystemIds.AdministratorRoleId={AdminRoleId})",
                permission, SystemIds.AdministratorRoleId);
            return IsAdminUser(request);
        }

        /// <summary>
        /// Checks whether the request originates from an authenticated user
        /// (any role, not necessarily administrator).
        /// Validates that JWT claims or Lambda authorizer context are present.
        /// </summary>
        /// <param name="request">The incoming API Gateway request.</param>
        /// <returns>True if the user is authenticated; false otherwise.</returns>
        private static bool IsAuthenticatedUser(APIGatewayHttpApiV2ProxyRequest request)
        {
            // Check JWT authorizer claims (production mode with Cognito).
            var claims = request.RequestContext?.Authorizer?.Jwt?.Claims;
            if (claims != null && claims.Count > 0)
            {
                // Any valid JWT with claims indicates an authenticated user.
                if (claims.ContainsKey("sub") || claims.ContainsKey("cognito:username"))
                    return true;
            }

            // Check Lambda authorizer context (LocalStack mode with custom authorizer).
            var lambdaAuth = request.RequestContext?.Authorizer?.Lambda;
            if (lambdaAuth != null && lambdaAuth.Count > 0)
            {
                if (lambdaAuth.ContainsKey("userId") || lambdaAuth.ContainsKey("sub"))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks whether the request originates from an authenticated administrator user.
        /// Supports both JWT authorizer (Cognito production mode) and Lambda authorizer
        /// (LocalStack development mode) patterns.
        /// Replaces: [Authorize(Roles = "administrator")] + SecurityContext.HasMetaPermission()
        /// </summary>
        /// <param name="request">The incoming API Gateway request with authorizer context.</param>
        /// <returns>True if the user holds the administrator role; false otherwise.</returns>
        private bool IsAdminUser(APIGatewayHttpApiV2ProxyRequest request)
        {
            try
            {
                var adminRoleIdStr = SystemIds.AdministratorRoleId.ToString().ToLowerInvariant();

                // Try JWT authorizer first (production mode with Cognito native JWT authorizer).
                var claims = request.RequestContext?.Authorizer?.Jwt?.Claims;
                if (claims != null)
                {
                    // Check Cognito groups claim for admin role.
                    if (claims.TryGetValue("cognito:groups", out var groups) &&
                        !string.IsNullOrEmpty(groups))
                    {
                        if (groups.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                            groups.Contains(adminRoleIdStr, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    // Check custom roles claim for backward compatibility.
                    if (claims.TryGetValue("custom:roles", out var roles) &&
                        !string.IsNullOrEmpty(roles))
                    {
                        if (roles.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                            roles.Contains(adminRoleIdStr, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    // Check scope claim for admin scope.
                    if (claims.TryGetValue("scope", out var scope) &&
                        !string.IsNullOrEmpty(scope))
                    {
                        if (scope.Contains("admin", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }

                // Try Lambda authorizer context (LocalStack mode with custom authorizer Lambda).
                var lambdaAuth = request.RequestContext?.Authorizer?.Lambda;
                if (lambdaAuth != null)
                {
                    if (lambdaAuth.TryGetValue("isAdmin", out var isAdminObj))
                    {
                        if (string.Equals(isAdminObj?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                            return true;
                        if (isAdminObj is string isAdminStr &&
                            string.Equals(isAdminStr, "true", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    if (lambdaAuth.TryGetValue("roles", out var rolesObj) && rolesObj is string rolesStr)
                    {
                        if (rolesStr.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                            rolesStr.Contains(adminRoleIdStr, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking admin authorization. Defaulting to denied.");
                return false;
            }
        }

        /// <summary>
        /// Extracts a path parameter value from the API Gateway request.
        /// </summary>
        /// <param name="request">The incoming request.</param>
        /// <param name="key">The path parameter key name.</param>
        /// <returns>The parameter value, or null if not found.</returns>
        private static string? GetPathParameter(APIGatewayHttpApiV2ProxyRequest request, string key)
        {
            if (request.PathParameters != null)
            {
                if (request.PathParameters.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                    return value;

                // Fallback: extract from {proxy+} catch-all parameter
                if (request.PathParameters.TryGetValue("proxy", out var proxy) && !string.IsNullOrEmpty(proxy))
                {
                    var segments = proxy.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    // For search handler, the proxy path is typically simple; return the full proxy value
                    // if the requested key is a generic identifier, or scan for GUIDs
                    if (segments.Length > 0)
                    {
                        // If looking for an id-like parameter, find a GUID segment
                        foreach (var seg in segments)
                        {
                            if (Guid.TryParse(seg, out _))
                                return seg;
                        }
                        // Otherwise return first non-keyword segment
                        return segments[0];
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Extracts a query string parameter value from the API Gateway request.
        /// </summary>
        /// <param name="request">The incoming request.</param>
        /// <param name="key">The query string parameter key name.</param>
        /// <returns>The parameter value, or null if not found.</returns>
        private static string? GetQueryParameter(APIGatewayHttpApiV2ProxyRequest request, string key)
        {
            if (request.QueryStringParameters != null &&
                request.QueryStringParameters.TryGetValue(key, out var value))
            {
                return value;
            }
            return null;
        }

        /// <summary>
        /// Extracts or generates a correlation ID for request tracing across services.
        /// Checks X-Correlation-Id header first, falls back to Lambda AwsRequestId.
        /// Per AAP §0.8.5 operational requirements: structured JSON logging with correlation-ID propagation.
        /// </summary>
        /// <param name="request">The incoming API Gateway request.</param>
        /// <param name="context">Lambda execution context providing AwsRequestId.</param>
        /// <returns>A correlation ID string for logging and event propagation.</returns>
        private static string ExtractCorrelationId(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            if (request.Headers != null &&
                request.Headers.TryGetValue("x-correlation-id", out var correlationId) &&
                !string.IsNullOrWhiteSpace(correlationId))
            {
                return correlationId;
            }

            return context.AwsRequestId ?? Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Constructs a standardized APIGatewayHttpApiV2ProxyResponse with JSON-serialized body,
        /// content-type header, and correlation ID propagation.
        /// </summary>
        /// <param name="statusCode">HTTP status code (200, 400, 403, 500).</param>
        /// <param name="body">Response body object to serialize as JSON.</param>
        /// <param name="correlationId">Correlation ID for response header propagation.</param>
        /// <returns>Fully constructed proxy response for API Gateway.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(
            int statusCode, object body, string correlationId)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = System.Text.Json.JsonSerializer.Serialize(body, _jsonOptions),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["X-Correlation-Id"] = correlationId
                }
            };
        }

        /// <summary>
        /// Constructs a standardized error response using BaseResponseModel envelope format.
        /// Includes structured ErrorModel entries for specific validation failures.
        /// </summary>
        /// <param name="statusCode">HTTP error status code.</param>
        /// <param name="message">Human-readable error message.</param>
        /// <param name="correlationId">Correlation ID for response header propagation.</param>
        /// <param name="errors">Optional list of structured error details.</param>
        /// <returns>Fully constructed error proxy response for API Gateway.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(
            int statusCode, string message, string correlationId, List<ErrorModel>? errors = null)
        {
            var responseBody = new BaseResponseModel
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = message,
                Errors = errors ?? new List<ErrorModel>()
            };

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = System.Text.Json.JsonSerializer.Serialize(responseBody, _jsonOptions),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["X-Correlation-Id"] = correlationId
                }
            };
        }

        /// <summary>
        /// Publishes an SNS domain event for cross-service communication.
        /// Replaces the monolith's synchronous HookManager post-hook pattern with async event-driven
        /// architecture per AAP §0.4.2 Event-Driven Architecture.
        ///
        /// Event naming convention: {domain}.{entity}.{action}
        /// (e.g., entity-management.search.indexed, entity-management.search.deindexed)
        ///
        /// Uses Newtonsoft.Json (JsonConvert.SerializeObject) for event payload serialization
        /// to maintain backward compatibility with event consumers expecting Newtonsoft-formatted JSON.
        /// </summary>
        /// <param name="action">Domain event type string.</param>
        /// <param name="eventData">Event payload data to include in the SNS message.</param>
        /// <param name="correlationId">Correlation ID for distributed tracing across services.</param>
        private async Task PublishDomainEvent(string action, object eventData, string correlationId)
        {
            if (string.IsNullOrEmpty(_searchTopicArn))
            {
                _logger.LogWarning(
                    "Search SNS topic ARN not configured (SEARCH_TOPIC_ARN env var). " +
                    "Skipping event publish for {Action}. CorrelationId={CorrelationId}",
                    action, correlationId);
                return;
            }

            try
            {
                var eventMessage = JsonConvert.SerializeObject(new
                {
                    source = "entity-management",
                    detailType = action,
                    timestamp = DateTime.UtcNow.ToString("O"),
                    correlationId,
                    data = eventData
                });

                var publishRequest = new PublishRequest
                {
                    TopicArn = _searchTopicArn,
                    Message = eventMessage,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = action
                        },
                        ["correlationId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        },
                        ["source"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = "entity-management"
                        }
                    }
                };

                await _snsClient.PublishAsync(publishRequest);

                _logger.LogInformation(
                    "Domain event published. Action={Action}, CorrelationId={CorrelationId}",
                    action, correlationId);
            }
            catch (Exception ex)
            {
                // Log but do not fail the request — event publishing is non-blocking.
                // Source pattern: monolith post-hooks were non-blocking.
                _logger.LogError(ex,
                    "Failed to publish domain event. Action={Action}, CorrelationId={CorrelationId}",
                    action, correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // PRIVATE INNER CLASSES — QUICK SEARCH REQUEST DTO
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Request DTO for the QuickSearch endpoint.
        /// Maps to the WebApiController.GetQuickSearch() query parameters:
        ///   - query (string), entityName (string), lookupFieldsCsv (string),
        ///   - sortField (string), sortType (string), returnFieldsCsv (string),
        ///   - matchMethod (string), matchAllFields (bool), skipRecords (int),
        ///   - limitRecords (int), findType (string), forceFiltersCsv (string)
        ///
        /// Converted from query string parameters to JSON body for POST endpoint.
        /// </summary>
        private class QuickSearchRequest
        {
            /// <summary>The search query text.</summary>
            [System.Text.Json.Serialization.JsonPropertyName("query")]
            public string Query { get; set; } = string.Empty;

            /// <summary>The entity name to search within (e.g., "account", "contact").</summary>
            [System.Text.Json.Serialization.JsonPropertyName("entity_name")]
            public string EntityName { get; set; } = string.Empty;

            /// <summary>Fields to search against (replaces lookupFieldsCsv).</summary>
            [System.Text.Json.Serialization.JsonPropertyName("lookup_fields")]
            public List<string> LookupFields { get; set; } = new();

            /// <summary>Fields to include in results (replaces returnFieldsCsv).</summary>
            [System.Text.Json.Serialization.JsonPropertyName("return_fields")]
            public List<string> ReturnFields { get; set; } = new();

            /// <summary>Field name to sort results by.</summary>
            [System.Text.Json.Serialization.JsonPropertyName("sort_field")]
            public string? SortField { get; set; }

            /// <summary>Sort direction: "asc" (default) or "desc".</summary>
            [System.Text.Json.Serialization.JsonPropertyName("sort_type")]
            public string SortType { get; set; } = "asc";

            /// <summary>
            /// Match method: "EQ" (exact), "contains", "startsWith", or "fts".
            /// Source: WebApiController matchMethod parameter.
            /// </summary>
            [System.Text.Json.Serialization.JsonPropertyName("match_method")]
            public string MatchMethod { get; set; } = "EQ";

            /// <summary>
            /// When true, ALL lookup fields must match (AND logic).
            /// When false, ANY lookup field matching suffices (OR logic).
            /// Source: WebApiController matchAllFields parameter.
            /// </summary>
            [System.Text.Json.Serialization.JsonPropertyName("match_all_fields")]
            public bool MatchAllFields { get; set; }

            /// <summary>Number of records to skip for pagination.</summary>
            [System.Text.Json.Serialization.JsonPropertyName("skip_records")]
            public int SkipRecords { get; set; }

            /// <summary>Maximum number of records to return (default: 5).</summary>
            [System.Text.Json.Serialization.JsonPropertyName("limit_records")]
            public int LimitRecords { get; set; } = 5;

            /// <summary>
            /// Result type: "records", "count", "records-and-count", or "records&amp;count".
            /// Source: WebApiController findType parameter.
            /// </summary>
            [System.Text.Json.Serialization.JsonPropertyName("find_type")]
            public string FindType { get; set; } = "records";

            /// <summary>
            /// Comma-separated force filter definitions in format "fieldName:dataType:eqValue".
            /// Source: WebApiController forceFiltersCsv parameter.
            /// Supported dataTypes: guid, bool, datetime, int, string.
            /// </summary>
            [System.Text.Json.Serialization.JsonPropertyName("force_filters")]
            public string? ForceFilters { get; set; }
        }
    }
}
