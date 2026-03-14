using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;

// NOTE: Assembly-level LambdaSerializer attribute is already declared in EntityHandler.cs.
// Duplicating it here would cause CS0579. Do NOT add [assembly: LambdaSerializer(...)].

namespace WebVellaErp.EntityManagement.Functions
{
    /// <summary>
    /// Lambda handler class for all record CRUD operations in the Entity Management
    /// bounded context. This is the most complex handler in the service — it replaces
    /// the monolith's RecordManager.cs orchestration logic and the record endpoints
    /// from WebApiController.cs with serverless Lambda functions.
    ///
    /// Responsibilities:
    /// - Record Create/Read/Update/Delete operations with full field value normalization
    /// - Record query (Find) and count operations with filter/sort/pagination
    /// - Many-to-Many relation bridge record creation and deletion
    /// - JWT-based entity-level permission enforcement via RecordPermissions
    /// - SNS domain event publishing replacing synchronous post-hook pattern
    /// - Idempotency keys on all write operations (AAP §0.8.5)
    /// - Structured JSON logging with correlation-ID propagation (AAP §0.8.5)
    ///
    /// API Routes:
    /// POST   /v1/entity-management/entities/{entityName}/records        → CreateRecord
    /// GET    /v1/entity-management/entities/{entityName}/records/{id}   → ReadRecord
    /// POST   /v1/entity-management/entities/{entityName}/records/query  → FindRecords
    /// PUT    /v1/entity-management/entities/{entityName}/records/{id}   → UpdateRecord
    /// DELETE /v1/entity-management/entities/{entityName}/records/{id}   → DeleteRecord
    /// POST   /v1/entity-management/relations/{relationId}/records       → CreateRelationManyToManyRecord
    /// DELETE /v1/entity-management/relations/{relationId}/records       → RemoveRelationManyToManyRecord
    /// POST   /v1/entity-management/entities/{entityName}/records/count  → Count
    ///
    /// Hook Migration (AAP §0.7.2):
    /// - Pre-hooks (IErpPreCreate/Update/DeleteRecordHook) → inline validation in handler
    /// - Post-hooks (IErpPostCreate/Update/DeleteRecordHook) → SNS domain events
    /// - Pre/Post M2M relation hooks → SNS domain events
    /// Event naming: {domain}.{entity}.{action} per AAP §0.8.5
    /// </summary>
    public class RecordHandler
    {
        // =====================================================================
        // Constants — preserved from monolith RecordManager.cs
        // =====================================================================

        /// <summary>
        /// Separator character used in record property keys to indicate first-level
        /// relation references. When a record key contains '.', the left segment is
        /// the relation marker ($relationName or $$relationName to flip direction)
        /// and the right segment is the field in the related entity.
        /// Source: RecordManager.RELATION_SEPARATOR
        /// </summary>
        public const char RELATION_SEPARATOR = '.';

        /// <summary>
        /// Separator character used in query result keys to namespace related record
        /// data under the relation name (e.g., "$user_role$name").
        /// Source: RecordManager.RELATION_NAME_RESULT_SEPARATOR
        /// </summary>
        public const char RELATION_NAME_RESULT_SEPARATOR = '$';

        // =====================================================================
        // Dependencies (injected via constructor DI)
        // =====================================================================

        private readonly IRecordService _recordService;
        private readonly IEntityService _entityService;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly IAmazonS3 _s3Client;
        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly ILogger<RecordHandler> _logger;

        // =====================================================================
        // Configuration (from environment variables)
        // =====================================================================

        /// <summary>SNS topic ARN for record domain events. From RECORD_TOPIC_ARN env var.</summary>
        private readonly string? _recordTopicArn;

        /// <summary>Whether running in development/LocalStack mode. From IS_LOCAL env var.</summary>
        private readonly bool _isDevelopmentMode;

        /// <summary>S3 bucket name for file/image storage. From FILES_S3_BUCKET env var.</summary>
        private readonly string? _s3BucketName;

        /// <summary>S3 prefix for temporary file uploads. From FILES_TEMP_PREFIX env var.</summary>
        private readonly string _filesTempPrefix;

        /// <summary>Lazy-initialized EntityHandler for delegating entity metadata requests
        /// when this handler receives /entities/{name} paths via {proxy+} routing.</summary>
        private EntityHandler? _entityHandler;

        /// <summary>Lazy-initialized FieldHandler for delegating field requests
        /// when this handler receives /entities/{id}/fields paths via {proxy+} routing.</summary>
        private FieldHandler? _fieldHandler;

        // =====================================================================
        // Shared JSON serializer options (AOT-safe via System.Text.Json)
        // =====================================================================

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // =====================================================================
        // Constructor
        // =====================================================================

        /// <summary>
        /// Constructs a new RecordHandler with all required service dependencies.
        /// Dependencies are provided by the Lambda function's DI container.
        /// </summary>
        /// <param name="recordService">Record CRUD orchestration service.</param>
        /// <param name="entityService">Entity metadata resolution service.</param>
        /// <param name="snsClient">SNS client for domain event publishing.</param>
        /// <param name="s3Client">S3 client for file/image field processing.</param>
        /// <param name="dynamoDbClient">DynamoDB client for atomic operations.</param>
        /// <param name="logger">Structured logger for correlation-ID tracing.</param>
        public RecordHandler(
            IRecordService recordService,
            IEntityService entityService,
            IAmazonSimpleNotificationService snsClient,
            IAmazonS3 s3Client,
            IAmazonDynamoDB dynamoDbClient,
            ILogger<RecordHandler> logger)
        {
            _recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
            _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _s3Client = s3Client ?? throw new ArgumentNullException(nameof(s3Client));
            _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _recordTopicArn = Environment.GetEnvironmentVariable("RECORD_TOPIC_ARN");
            _isDevelopmentMode = string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"), "true",
                StringComparison.OrdinalIgnoreCase);
            _s3BucketName = Environment.GetEnvironmentVariable("FILES_S3_BUCKET");
            _filesTempPrefix = Environment.GetEnvironmentVariable("FILES_TEMP_PREFIX") ?? "tmp/";
        }

        // =================================================================
        // CreateRecord — POST /v1/entity-management/entities/{entityName}/records
        // =================================================================

        /// <summary>
        /// Lambda handler for creating a new record in the specified entity.
        /// Performs: JWT permission check, field value normalization for 21+ field types,
        /// relation-aware input parsing ($ / $$ notation), S3 file processing,
        /// record persistence via IRecordService, and SNS domain event publishing.
        /// Replaces: WebApiController.CreateRecord + RecordManager.CreateRecord.
        /// </summary>

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

            _logger.LogInformation(
                "RecordHandler.FunctionHandler — Method={Method}, Path={Path}, RequestId={RequestId}",
                method, path, context.AwsRequestId);

            // ── Delegation check: non-record requests via /v1/entities/{proxy+} ──
            // When the API Gateway routes ALL /v1/entities/{proxy+} traffic here,
            // requests for entity metadata or fields don't contain "/records".
            // Delegate those to EntityHandler or FieldHandler respectively.
            //
            // CRITICAL: Requests arriving on /v1/record/{proxy+} are ALWAYS record
            // operations and must NEVER be delegated. The delegation logic only
            // applies when this handler receives traffic via /v1/entities/{proxy+}.
            var pathLowerForDelegation = path.ToLowerInvariant();
            bool isDirectRecordRoute = pathLowerForDelegation.Contains("/v1/record/")
                                       || pathLowerForDelegation.StartsWith("/v1/record");

            var proxyValue = request.PathParameters != null && request.PathParameters.TryGetValue("proxy", out var pv)
                ? pv : string.Empty;
            var proxyLower = proxyValue.ToLowerInvariant();

            bool isRecordPath = isDirectRecordRoute
                                || proxyLower.Contains("/records")
                                || proxyLower.Contains("records")
                                || proxyLower.Contains("/count")
                                || proxyLower.Contains("/query")
                                || proxyLower.Contains("/import")
                                || proxyLower.Contains("/export");

            if (!isRecordPath && !string.IsNullOrEmpty(proxyValue))
            {
                // This is an entity metadata or field request — delegate
                if (proxyLower.Contains("/fields"))
                {
                    var fieldHandler = GetOrCreateFieldHandler();
                    return await fieldHandler.FunctionHandler(request, context);
                }
                else
                {
                    var entityHandler = GetOrCreateEntityHandler();
                    return await entityHandler.FunctionHandler(request, context);
                }
            }

            // ── Route resolution ───────────────────────────────────────────
            // Paths follow the pattern:
            //   /v1/entities/{entityName}/records            — list / create
            //   /v1/entities/{entityName}/records/{id}       — read / update / delete
            //   /v1/entities/{entityName}/records/count      — count
            //   /v1/entities/{entityName}/records/query      — query / find
            //   /v1/record/{proxy+}                          — legacy routes
            //   /v1/entity-management/relations/{id}/records — relation bridge
            //
            // Also accept: /v1/record/{entityName}/records/...
            var pathLower = path.ToLowerInvariant();

            // Relation bridge operations
            if (pathLower.Contains("/relations/") && method == "POST")
                return await CreateRelationManyToManyRecord(request, context);
            if (pathLower.Contains("/relations/") && method == "DELETE")
                return await RemoveRelationManyToManyRecord(request, context);

            // Count endpoint
            if (pathLower.EndsWith("/count") || pathLower.EndsWith("/count/"))
            {
                if (method == "GET" || method == "POST")
                    return await Count(request, context);
            }

            // Query / find endpoint
            if (pathLower.EndsWith("/query") || pathLower.EndsWith("/query/"))
            {
                if (method == "POST")
                    return await FindRecords(request, context);
            }

            // CRUD routing by HTTP method
            switch (method)
            {
                case "POST":
                    return await CreateRecord(request, context);

                case "GET":
                    // Determine list vs single read from the {proxy+} segments.
                    // After Vite rewrites, the path is /v1/record/{entityName}[/{id}].
                    // Proxy value is "{entityName}" (list) or "{entityName}/{guid}" (read).
                    // We also support the legacy /v1/.../records/{guid} format.
                    var proxySegs = proxyValue.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    // Check proxy segments for a GUID (record ID) to decide single-read
                    bool hasSingleRecordId = false;
                    if (proxySegs.Length >= 2)
                    {
                        // Skip the first segment (entity name) and check for GUID
                        for (int i = 1; i < proxySegs.Length; i++)
                        {
                            if (Guid.TryParse(proxySegs[i], out _))
                            {
                                hasSingleRecordId = true;
                                break;
                            }
                        }
                    }
                    // Also fallback to the legacy /records/{guid} pattern in RawPath
                    if (!hasSingleRecordId)
                    {
                        var afterRecords = ExtractSegmentAfterRecords(path);
                        if (!string.IsNullOrEmpty(afterRecords) && afterRecords != "/")
                        {
                            if (Guid.TryParse(afterRecords.Trim('/'), out _))
                                hasSingleRecordId = true;
                        }
                    }
                    return hasSingleRecordId
                        ? await ReadRecord(request, context)
                        : await FindRecords(request, context);

                case "PUT":
                    return await UpdateRecord(request, context);

                case "DELETE":
                    return await DeleteRecord(request, context);

                default:
                    return new APIGatewayHttpApiV2ProxyResponse
                    {
                        StatusCode = (int)HttpStatusCode.MethodNotAllowed,
                        Body = System.Text.Json.JsonSerializer.Serialize(
                            new { success = false, message = $"Method {method} not allowed" }),
                        Headers = new Dictionary<string, string>
                        {
                            { "Content-Type", "application/json" }
                        }
                    };
            }
        }

        /// <summary>
        /// Extracts the path segment that follows '/records/' in the raw path.
        /// Returns empty string if the path ends with /records or /records/.
        /// </summary>
        /// <summary>
        /// Lazily creates (or returns cached) EntityHandler for delegating entity
        /// metadata requests that arrive via the shared /v1/entities/{proxy+} route.
        /// </summary>
        private EntityHandler GetOrCreateEntityHandler()
        {
            if (_entityHandler == null)
            {
                var cache = new MemoryCache(new MemoryCacheOptions());
                var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
                _entityHandler = new EntityHandler(
                    _entityService,
                    _snsClient,
                    cache,
                    loggerFactory.CreateLogger<EntityHandler>(),
                    loggerFactory);
            }
            return _entityHandler;
        }

        /// <summary>
        /// Lazily creates (or returns cached) FieldHandler for delegating field
        /// requests that arrive via the shared /v1/entities/{proxy+} route.
        /// </summary>
        private FieldHandler GetOrCreateFieldHandler()
        {
            if (_fieldHandler == null)
            {
                var cache = new MemoryCache(new MemoryCacheOptions());
                var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
                _fieldHandler = new FieldHandler(
                    _entityService,
                    _snsClient,
                    cache,
                    loggerFactory.CreateLogger<FieldHandler>());
            }
            return _fieldHandler;
        }

        /// <summary>
        /// Resolves an entity by name or by GUID id. The frontend may pass either
        /// the entity name (e.g. "test_entity") or the entity UUID (e.g. "a1b2c3d4-...")
        /// as the proxy path segment. This helper tries by GUID first if the value
        /// looks like a GUID, then falls back to name lookup.
        /// </summary>
        private async Task<Entity?> ResolveEntity(string nameOrId)
        {
            if (Guid.TryParse(nameOrId, out var entityGuid))
            {
                var byId = await _entityService.GetEntity(entityGuid);
                if (byId != null) return byId;
            }
            return await _entityService.GetEntity(nameOrId);
        }

        private static string ExtractSegmentAfterRecords(string path)
        {
            const string marker = "/records/";
            var idx = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                // Path ends with /records (no trailing slash)
                if (path.EndsWith("/records", StringComparison.OrdinalIgnoreCase))
                    return string.Empty;
                return string.Empty;
            }
            return path.Substring(idx + marker.Length);
        }

        public async Task<APIGatewayHttpApiV2ProxyResponse> CreateRecord(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] CreateRecord invoked", correlationId);

            try
            {
                // 1. Extract entity name from route
                var entityName = GetParam(request.PathParameters, "entityName");
                if (string.IsNullOrWhiteSpace(entityName))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "Entity name is required in path parameters.", correlationId);
                }

                // 2. Resolve entity metadata
                var entity = await ResolveEntity(entityName);
                if (entity == null)
                {
                    return BuildErrorResponse(HttpStatusCode.NotFound,
                        $"Entity '{entityName}' not found.", correlationId);
                }
                entityName = entity.Name; // Normalize to actual name for DynamoDB PK lookups

                // 3. Permission check — RecordPermissions.CanCreate against JWT roles
                if (!HasPermission(request, entity, EntityPermission.Create))
                {
                    _logger.LogWarning("[{CorrelationId}] Permission denied for CreateRecord on entity '{EntityName}'",
                        correlationId, entityName);
                    return BuildErrorResponse(HttpStatusCode.Forbidden,
                        "Access denied. You do not have permission to create records for this entity.", correlationId);
                }

                // 4. Parse record from request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "Request body is required.", correlationId);
                }

                EntityRecord record;
                try
                {
                    record = DeserializeRecord(request.Body);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{CorrelationId}] Failed to parse record body", correlationId);
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        $"Invalid record body: {ex.Message}", correlationId);
                }

                // 5. Pre-hook validation (synchronous, in-handler — replaces IErpPreCreateRecordHook)
                var preHookErrors = ValidatePreCreateRecord(entity, record);
                if (preHookErrors.Count > 0)
                {
                    return BuildValidationErrorResponse(HttpStatusCode.BadRequest,
                        "Record validation failed.", preHookErrors, correlationId);
                }

                // 6. Assign record ID if not provided
                if (!record.ContainsKey("id") || record["id"] == null)
                {
                    record["id"] = Guid.NewGuid();
                }
                else if (record["id"] is string idStr && Guid.TryParse(idStr, out var parsedId))
                {
                    record["id"] = parsedId;
                }

                // 7. Relation-aware input parsing — process keys containing RELATION_SEPARATOR
                var relationErrors = await ProcessRelationInputs(entity, record, correlationId, isCreate: true);
                if (relationErrors.Count > 0)
                {
                    return BuildValidationErrorResponse(HttpStatusCode.BadRequest,
                        "Relation processing failed.", relationErrors, correlationId);
                }

                // 8. Field value normalization — normalize field values per field type
                NormalizeFieldValues(entity, record);

                // 9. Process file/image fields — move S3 objects from temp to permanent
                await ProcessFileFields(entity, record, correlationId);

                // 10. Persist record via RecordService
                var response = await _recordService.CreateRecord(entity, record);

                if (!response.Success)
                {
                    var statusCode = response.StatusCode != default
                        ? response.StatusCode : HttpStatusCode.BadRequest;
                    return BuildResponse(statusCode, response, correlationId);
                }

                // 11. Post-hook domain event — replaces IErpPostCreateRecordHook
                var recordId = record.ContainsKey("id") ? record["id"]?.ToString() : null;
                await PublishDomainEvent(
                    $"entity-management.{entityName}.created",
                    new
                    {
                        eventType = "record.created",
                        entityName,
                        recordId,
                        record = record as IDictionary<string, object?>,
                        userId = ExtractUserId(request),
                        timestamp = DateTime.UtcNow.ToString("o"),
                        idempotencyKey = $"create-{entityName}-{recordId}-{DateTime.UtcNow.Ticks}"
                    },
                    correlationId);

                _logger.LogInformation(
                    "[{CorrelationId}] Record created successfully: entity={EntityName}, id={RecordId}",
                    correlationId, entityName, recordId);

                // Return the single created record directly in the response envelope
                // (not wrapped in QueryResult). The frontend useCreateRecord hook
                // expects response.object to be a plain EntityRecord.
                EntityRecord? createdEntityRecord = null;
                if (response.Object?.Data != null && response.Object.Data.Count > 0)
                {
                    createdEntityRecord = response.Object.Data[0];
                }
                var singleResponse = new
                {
                    timestamp = response.Timestamp,
                    success = true,
                    message = response.Message ?? "Record was created successfully.",
                    errors = response.Errors ?? new List<ErrorModel>(),
                    accessWarnings = response.AccessWarnings ?? new List<AccessWarningModel>(),
                    @object = createdEntityRecord
                };
                return new APIGatewayHttpApiV2ProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Body = System.Text.Json.JsonSerializer.Serialize(singleResponse,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                        }),
                    Headers = new Dictionary<string, string>
                    {
                        { "Content-Type", "application/json" },
                        { "X-Correlation-Id", correlationId }
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] Unhandled exception in CreateRecord", correlationId);
                return BuildErrorResponse(HttpStatusCode.InternalServerError,
                    "An unexpected error occurred while creating the record.", correlationId);
            }
        }

        // =================================================================
        // ReadRecord — GET /v1/entity-management/entities/{entityName}/records/{id}
        // =================================================================

        /// <summary>
        /// Lambda handler for reading a single record by entity name and record ID.
        /// Performs: JWT permission check, single-record fetch via IRecordService.Find
        /// with QueryEQ on "id", and returns QueryResponse envelope.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ReadRecord(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] ReadRecord invoked", correlationId);

            try
            {
                // 1. Extract path parameters
                var entityName = GetParam(request.PathParameters, "entityName");
                var recordIdStr = GetParam(request.PathParameters, "recordId");

                if (string.IsNullOrWhiteSpace(entityName))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "Entity name is required in path parameters.", correlationId);
                }

                if (!Guid.TryParse(recordIdStr, out var recordId))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "A valid record ID (GUID) is required in path parameters.", correlationId);
                }

                // 2. Resolve entity metadata
                var entity = await ResolveEntity(entityName);
                if (entity == null)
                {
                    return BuildErrorResponse(HttpStatusCode.NotFound,
                        $"Entity '{entityName}' not found.", correlationId);
                }
                entityName = entity.Name; // Normalize to actual name for DynamoDB PK lookups

                // 3. Permission check — RecordPermissions.CanRead
                if (!HasPermission(request, entity, EntityPermission.Read))
                {
                    _logger.LogWarning(
                        "[{CorrelationId}] Permission denied for ReadRecord on entity '{EntityName}'",
                        correlationId, entityName);
                    return BuildErrorResponse(HttpStatusCode.Forbidden,
                        "Access denied. You do not have permission to read records for this entity.",
                        correlationId);
                }

                // 4. Build query for single record by ID
                var query = new EntityQuery(
                    entityName: entityName,
                    fields: "*",
                    query: EntityQuery.QueryEQ("id", recordId));

                // 5. Execute query
                var response = await _recordService.Find(query);

                if (!response.Success)
                {
                    return BuildResponse(
                        response.StatusCode != default ? response.StatusCode : HttpStatusCode.InternalServerError,
                        response, correlationId);
                }

                // 6. Check if record was found
                if (response.Object?.Data == null || response.Object.Data.Count == 0)
                {
                    return BuildErrorResponse(HttpStatusCode.NotFound,
                        $"Record with ID '{recordId}' not found in entity '{entityName}'.",
                        correlationId);
                }

                _logger.LogInformation(
                    "[{CorrelationId}] ReadRecord success: entity={EntityName}, id={RecordId}",
                    correlationId, entityName, recordId);

                // Return the single record directly in the response envelope
                // (not wrapped in QueryResult). The frontend useRecord hook
                // expects response.object to be a plain EntityRecord.
                var singleRecord = response.Object.Data[0];
                var singleResponse = new
                {
                    timestamp = response.Timestamp,
                    success = true,
                    message = "The record was successfully returned.",
                    errors = response.Errors ?? new List<ErrorModel>(),
                    accessWarnings = response.AccessWarnings ?? new List<AccessWarningModel>(),
                    @object = singleRecord
                };
                return new APIGatewayHttpApiV2ProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Body = System.Text.Json.JsonSerializer.Serialize(singleResponse,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                        }),
                    Headers = new Dictionary<string, string>
                    {
                        { "Content-Type", "application/json" },
                        { "X-Correlation-Id", correlationId }
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] Unhandled exception in ReadRecord", correlationId);
                return BuildErrorResponse(HttpStatusCode.InternalServerError,
                    "An unexpected error occurred while reading the record.", correlationId);
            }
        }

        // =================================================================
        // FindRecords — POST /v1/entity-management/entities/{entityName}/records/query
        // =================================================================

        /// <summary>
        /// Lambda handler for querying/searching records with filters, sorting, and
        /// pagination. Accepts an EntityQuery object in the request body or builds one
        /// from query string parameters. Returns paginated EntityRecordList with TotalCount.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> FindRecords(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] FindRecords invoked", correlationId);

            try
            {
                // 1. Extract entity name from route
                var entityName = GetParam(request.PathParameters, "entityName");
                if (string.IsNullOrWhiteSpace(entityName))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "Entity name is required in path parameters.", correlationId);
                }

                // 2. Resolve entity metadata
                var entity = await ResolveEntity(entityName);
                if (entity == null)
                {
                    return BuildErrorResponse(HttpStatusCode.NotFound,
                        $"Entity '{entityName}' not found.", correlationId);
                }
                entityName = entity.Name; // Normalize to actual name for DynamoDB PK lookups

                // 3. Permission check — RecordPermissions.CanRead
                if (!HasPermission(request, entity, EntityPermission.Read))
                {
                    _logger.LogWarning(
                        "[{CorrelationId}] Permission denied for FindRecords on entity '{EntityName}'",
                        correlationId, entityName);
                    return BuildErrorResponse(HttpStatusCode.Forbidden,
                        "Access denied. You do not have permission to read records for this entity.",
                        correlationId);
                }

                // 4. Parse EntityQuery from body or query parameters
                EntityQuery entityQuery;
                if (!string.IsNullOrWhiteSpace(request.Body))
                {
                    try
                    {
                        entityQuery = ParseEntityQuery(entityName, request.Body);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{CorrelationId}] Failed to parse FindRecords query body",
                            correlationId);
                        return BuildErrorResponse(HttpStatusCode.BadRequest,
                            $"Invalid query body: {ex.Message}", correlationId);
                    }
                }
                else
                {
                    // Build query from query string parameters
                    entityQuery = BuildQueryFromParams(entityName, request.QueryStringParameters);
                }

                // 5. Execute query
                var response = await _recordService.Find(entityQuery);

                if (!response.Success)
                {
                    return BuildResponse(
                        response.StatusCode != default ? response.StatusCode : HttpStatusCode.InternalServerError,
                        response, correlationId);
                }

                _logger.LogInformation(
                    "[{CorrelationId}] FindRecords success: entity={EntityName}, count={Count}",
                    correlationId, entityName, response.Object?.Data?.Count ?? 0);

                return BuildResponse(HttpStatusCode.OK, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] Unhandled exception in FindRecords", correlationId);
                return BuildErrorResponse(HttpStatusCode.InternalServerError,
                    "An unexpected error occurred while querying records.", correlationId);
            }
        }

        // =================================================================
        // UpdateRecord — PUT /v1/entity-management/entities/{entityName}/records/{id}
        // =================================================================

        /// <summary>
        /// Lambda handler for updating an existing record. Performs permission check,
        /// field value normalization, relation-aware input parsing, S3 file processing,
        /// record persistence, and SNS domain event publishing.
        /// Replaces: WebApiController.UpdateRecord + RecordManager.UpdateRecord.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> UpdateRecord(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] UpdateRecord invoked", correlationId);

            try
            {
                // 1. Extract path parameters
                var entityName = GetParam(request.PathParameters, "entityName");
                var recordIdStr = GetParam(request.PathParameters, "recordId");

                if (string.IsNullOrWhiteSpace(entityName))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "Entity name is required in path parameters.", correlationId);
                }

                if (!Guid.TryParse(recordIdStr, out var recordId))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "A valid record ID (GUID) is required in path parameters.", correlationId);
                }

                // 2. Resolve entity metadata
                var entity = await ResolveEntity(entityName);
                if (entity == null)
                {
                    return BuildErrorResponse(HttpStatusCode.NotFound,
                        $"Entity '{entityName}' not found.", correlationId);
                }
                entityName = entity.Name; // Normalize to actual name for DynamoDB PK lookups

                // 3. Permission check — RecordPermissions.CanUpdate
                if (!HasPermission(request, entity, EntityPermission.Update))
                {
                    _logger.LogWarning(
                        "[{CorrelationId}] Permission denied for UpdateRecord on entity '{EntityName}'",
                        correlationId, entityName);
                    return BuildErrorResponse(HttpStatusCode.Forbidden,
                        "Access denied. You do not have permission to update records for this entity.",
                        correlationId);
                }

                // 4. Parse record from request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "Request body is required.", correlationId);
                }

                EntityRecord record;
                try
                {
                    record = DeserializeRecord(request.Body);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{CorrelationId}] Failed to parse record body", correlationId);
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        $"Invalid record body: {ex.Message}", correlationId);
                }

                // 5. Ensure record ID from path is set on the record
                record["id"] = recordId;

                // 6. Pre-hook validation (synchronous, in-handler — replaces IErpPreUpdateRecordHook)
                var preHookErrors = ValidatePreUpdateRecord(entity, record);
                if (preHookErrors.Count > 0)
                {
                    return BuildValidationErrorResponse(HttpStatusCode.BadRequest,
                        "Record validation failed.", preHookErrors, correlationId);
                }

                // 7. Relation-aware input parsing
                var relationErrors = await ProcessRelationInputs(entity, record, correlationId, isCreate: false);
                if (relationErrors.Count > 0)
                {
                    return BuildValidationErrorResponse(HttpStatusCode.BadRequest,
                        "Relation processing failed.", relationErrors, correlationId);
                }

                // 8. Field value normalization
                NormalizeFieldValues(entity, record);

                // 9. Process file/image fields — move S3 objects from temp to permanent
                await ProcessFileFields(entity, record, correlationId);

                // 10. Persist update via RecordService
                var response = await _recordService.UpdateRecord(entity, record);

                if (!response.Success)
                {
                    var statusCode = response.StatusCode != default
                        ? response.StatusCode : HttpStatusCode.BadRequest;
                    return BuildResponse(statusCode, response, correlationId);
                }

                // 11. Post-hook domain event — replaces IErpPostUpdateRecordHook
                await PublishDomainEvent(
                    $"entity-management.{entityName}.updated",
                    new
                    {
                        eventType = "record.updated",
                        entityName,
                        recordId = recordId.ToString(),
                        record = record as IDictionary<string, object?>,
                        userId = ExtractUserId(request),
                        timestamp = DateTime.UtcNow.ToString("o"),
                        idempotencyKey = $"update-{entityName}-{recordId}-{DateTime.UtcNow.Ticks}"
                    },
                    correlationId);

                _logger.LogInformation(
                    "[{CorrelationId}] Record updated successfully: entity={EntityName}, id={RecordId}",
                    correlationId, entityName, recordId);

                // Return the single updated record directly in the response envelope
                // (not wrapped in QueryResult). The frontend useUpdateRecord hook
                // expects response.object to be a plain EntityRecord.
                EntityRecord? updatedEntityRecord = null;
                if (response.Object?.Data != null && response.Object.Data.Count > 0)
                {
                    updatedEntityRecord = response.Object.Data[0];
                }
                var singleResponse = new
                {
                    timestamp = response.Timestamp,
                    success = true,
                    message = response.Message ?? "Record was updated successfully.",
                    errors = response.Errors ?? new List<ErrorModel>(),
                    accessWarnings = response.AccessWarnings ?? new List<AccessWarningModel>(),
                    @object = updatedEntityRecord
                };
                return new APIGatewayHttpApiV2ProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Body = System.Text.Json.JsonSerializer.Serialize(singleResponse,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                        }),
                    Headers = new Dictionary<string, string>
                    {
                        { "Content-Type", "application/json" },
                        { "X-Correlation-Id", correlationId }
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] Unhandled exception in UpdateRecord", correlationId);
                return BuildErrorResponse(HttpStatusCode.InternalServerError,
                    "An unexpected error occurred while updating the record.", correlationId);
            }
        }

        // =================================================================
        // DeleteRecord — DELETE /v1/entity-management/entities/{entityName}/records/{id}
        // =================================================================

        /// <summary>
        /// Lambda handler for deleting a record. Verifies record existence, performs
        /// permission check, cleans up S3 file references for FileField/ImageField,
        /// deletes via IRecordService, and publishes SNS domain event.
        /// Replaces: WebApiController.DeleteRecord + RecordManager.DeleteRecord.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> DeleteRecord(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] DeleteRecord invoked", correlationId);

            try
            {
                // 1. Extract path parameters
                var entityName = GetParam(request.PathParameters, "entityName");
                var recordIdStr = GetParam(request.PathParameters, "recordId");

                if (string.IsNullOrWhiteSpace(entityName))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "Entity name is required in path parameters.", correlationId);
                }

                if (!Guid.TryParse(recordIdStr, out var recordId))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "A valid record ID (GUID) is required in path parameters.", correlationId);
                }

                // 2. Resolve entity metadata
                var entity = await ResolveEntity(entityName);
                if (entity == null)
                {
                    return BuildErrorResponse(HttpStatusCode.NotFound,
                        $"Entity '{entityName}' not found.", correlationId);
                }
                entityName = entity.Name; // Normalize to actual name for DynamoDB PK lookups

                // 3. Permission check — RecordPermissions.CanDelete
                if (!HasPermission(request, entity, EntityPermission.Delete))
                {
                    _logger.LogWarning(
                        "[{CorrelationId}] Permission denied for DeleteRecord on entity '{EntityName}'",
                        correlationId, entityName);
                    return BuildErrorResponse(HttpStatusCode.Forbidden,
                        "Access denied. You do not have permission to delete records for this entity.",
                        correlationId);
                }

                // 4. Pre-hook validation (synchronous, in-handler — replaces IErpPreDeleteRecordHook)
                var preHookErrors = ValidatePreDeleteRecord(entity, recordId);
                if (preHookErrors.Count > 0)
                {
                    return BuildValidationErrorResponse(HttpStatusCode.BadRequest,
                        "Delete validation failed.", preHookErrors, correlationId);
                }

                // 5. Fetch existing record for file cleanup and event data
                EntityRecord? existingRecord = null;
                try
                {
                    var fetchQuery = new EntityQuery(
                        entityName: entityName,
                        fields: "*",
                        query: EntityQuery.QueryEQ("id", recordId));
                    var fetchResponse = await _recordService.Find(fetchQuery);
                    if (fetchResponse.Success && fetchResponse.Object?.Data != null
                        && fetchResponse.Object.Data.Count > 0)
                    {
                        existingRecord = fetchResponse.Object.Data[0];
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[{CorrelationId}] Failed to fetch record before delete for file cleanup",
                        correlationId);
                    // Continue with delete even if fetch fails — file cleanup is best-effort
                }

                // 6. Clean up S3 file references for FileField/ImageField
                if (existingRecord != null)
                {
                    await CleanupFileFieldsOnDelete(entity, existingRecord, correlationId);
                }

                // 7. Delete record via RecordService
                var response = await _recordService.DeleteRecord(entity, recordId);

                if (!response.Success)
                {
                    var statusCode = response.StatusCode != default
                        ? response.StatusCode : HttpStatusCode.BadRequest;
                    return BuildResponse(statusCode, response, correlationId);
                }

                // 8. Post-hook domain event — replaces IErpPostDeleteRecordHook
                await PublishDomainEvent(
                    $"entity-management.{entityName}.deleted",
                    new
                    {
                        eventType = "record.deleted",
                        entityName,
                        recordId = recordId.ToString(),
                        userId = ExtractUserId(request),
                        timestamp = DateTime.UtcNow.ToString("o"),
                        idempotencyKey = $"delete-{entityName}-{recordId}-{DateTime.UtcNow.Ticks}"
                    },
                    correlationId);

                _logger.LogInformation(
                    "[{CorrelationId}] Record deleted successfully: entity={EntityName}, id={RecordId}",
                    correlationId, entityName, recordId);

                response.StatusCode = HttpStatusCode.OK;
                return BuildResponse(HttpStatusCode.OK, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] Unhandled exception in DeleteRecord", correlationId);
                return BuildErrorResponse(HttpStatusCode.InternalServerError,
                    "An unexpected error occurred while deleting the record.", correlationId);
            }
        }

        // =================================================================
        // CreateRelationManyToManyRecord — POST /v1/entity-management/relations/{relationId}/records
        // =================================================================

        /// <summary>
        /// Lambda handler for creating a many-to-many relation bridge record.
        /// Accepts relationId from path, originValue and targetValue GUIDs from body.
        /// Creates bridge record via IRecordService, publishes SNS event.
        /// Replaces: RecordManager.CreateRelationManyToManyRecord.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> CreateRelationManyToManyRecord(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] CreateRelationManyToManyRecord invoked", correlationId);

            try
            {
                // 1. Extract relation ID from path
                var relationIdStr = GetParam(request.PathParameters, "relationId");
                if (!Guid.TryParse(relationIdStr, out var relationId))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "A valid relation ID (GUID) is required in path parameters.", correlationId);
                }

                // 2. Parse body for origin and target values
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "Request body with originValue and targetValue is required.", correlationId);
                }

                Guid originValue;
                Guid targetValue;
                try
                {
                    var bodyDoc = JsonDocument.Parse(request.Body);
                    var root = bodyDoc.RootElement;

                    var originStr = root.TryGetProperty("originValue", out var originProp)
                        ? originProp.GetString() : null;
                    var targetStr = root.TryGetProperty("targetValue", out var targetProp)
                        ? targetProp.GetString() : null;

                    if (string.IsNullOrWhiteSpace(originStr) || !Guid.TryParse(originStr, out originValue))
                    {
                        return BuildErrorResponse(HttpStatusCode.BadRequest,
                            "A valid 'originValue' GUID is required in the request body.", correlationId);
                    }
                    if (string.IsNullOrWhiteSpace(targetStr) || !Guid.TryParse(targetStr, out targetValue))
                    {
                        return BuildErrorResponse(HttpStatusCode.BadRequest,
                            "A valid 'targetValue' GUID is required in the request body.", correlationId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{CorrelationId}] Failed to parse M2M body", correlationId);
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        $"Invalid request body: {ex.Message}", correlationId);
                }

                // 3. Validate the relation exists and is M2M type
                var relationResponse = await _entityService.ReadRelation(relationId);
                if (!relationResponse.Success || relationResponse.Object == null)
                {
                    return BuildErrorResponse(HttpStatusCode.NotFound,
                        $"Relation with ID '{relationId}' not found.", correlationId);
                }

                var relation = relationResponse.Object;
                if (relation.RelationType != EntityRelationType.ManyToMany)
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        $"Relation '{relation.Name}' is not a Many-to-Many relation. " +
                        "Only M2M relations support bridge records.", correlationId);
                }

                // 4. Create bridge record via RecordService
                var response = await _recordService.CreateRelationManyToManyRecord(
                    relationId, originValue, targetValue);

                if (!response.Success)
                {
                    var statusCode = response.StatusCode != default
                        ? response.StatusCode : HttpStatusCode.BadRequest;
                    return BuildResponse(statusCode, response, correlationId);
                }

                // 5. Post-hook domain event — replaces IErpPostCreateManyToManyRelationHook
                await PublishDomainEvent(
                    "entity-management.relation.created",
                    new
                    {
                        eventType = "relation.m2m.created",
                        relationId = relationId.ToString(),
                        relationName = relation.Name,
                        originValue = originValue.ToString(),
                        targetValue = targetValue.ToString(),
                        userId = ExtractUserId(request),
                        timestamp = DateTime.UtcNow.ToString("o"),
                        idempotencyKey = $"m2m-create-{relationId}-{originValue}-{targetValue}-{DateTime.UtcNow.Ticks}"
                    },
                    correlationId);

                _logger.LogInformation(
                    "[{CorrelationId}] M2M relation record created: relation={RelationName}, origin={Origin}, target={Target}",
                    correlationId, relation.Name, originValue, targetValue);

                response.StatusCode = HttpStatusCode.OK;
                return BuildResponse(HttpStatusCode.OK, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{CorrelationId}] Unhandled exception in CreateRelationManyToManyRecord", correlationId);
                return BuildErrorResponse(HttpStatusCode.InternalServerError,
                    "An unexpected error occurred while creating the relation record.", correlationId);
            }
        }

        // =================================================================
        // RemoveRelationManyToManyRecord — DELETE /v1/entity-management/relations/{relationId}/records
        // =================================================================

        /// <summary>
        /// Lambda handler for removing a many-to-many relation bridge record.
        /// Accepts relationId from path, originValue and targetValue GUIDs from body.
        /// Deletes bridge record via IRecordService, publishes SNS event.
        /// Replaces: RecordManager.RemoveRelationManyToManyRecord.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> RemoveRelationManyToManyRecord(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] RemoveRelationManyToManyRecord invoked", correlationId);

            try
            {
                // 1. Extract relation ID from path
                var relationIdStr = GetParam(request.PathParameters, "relationId");
                if (!Guid.TryParse(relationIdStr, out var relationId))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "A valid relation ID (GUID) is required in path parameters.", correlationId);
                }

                // 2. Parse body for origin and target values
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "Request body with originValue and targetValue is required.", correlationId);
                }

                Guid originValue;
                Guid targetValue;
                try
                {
                    var bodyDoc = JsonDocument.Parse(request.Body);
                    var root = bodyDoc.RootElement;

                    var originStr = root.TryGetProperty("originValue", out var originProp)
                        ? originProp.GetString() : null;
                    var targetStr = root.TryGetProperty("targetValue", out var targetProp)
                        ? targetProp.GetString() : null;

                    if (string.IsNullOrWhiteSpace(originStr) || !Guid.TryParse(originStr, out originValue))
                    {
                        return BuildErrorResponse(HttpStatusCode.BadRequest,
                            "A valid 'originValue' GUID is required in the request body.", correlationId);
                    }
                    if (string.IsNullOrWhiteSpace(targetStr) || !Guid.TryParse(targetStr, out targetValue))
                    {
                        return BuildErrorResponse(HttpStatusCode.BadRequest,
                            "A valid 'targetValue' GUID is required in the request body.", correlationId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{CorrelationId}] Failed to parse M2M body", correlationId);
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        $"Invalid request body: {ex.Message}", correlationId);
                }

                // 3. Validate the relation exists and is M2M type
                var relationResponse = await _entityService.ReadRelation(relationId);
                if (!relationResponse.Success || relationResponse.Object == null)
                {
                    return BuildErrorResponse(HttpStatusCode.NotFound,
                        $"Relation with ID '{relationId}' not found.", correlationId);
                }

                var relation = relationResponse.Object;
                if (relation.RelationType != EntityRelationType.ManyToMany)
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        $"Relation '{relation.Name}' is not a Many-to-Many relation. " +
                        "Only M2M relations support bridge records.", correlationId);
                }

                // 4. Remove bridge record via RecordService
                var response = await _recordService.RemoveRelationManyToManyRecord(
                    relationId, originValue, targetValue);

                if (!response.Success)
                {
                    var statusCode = response.StatusCode != default
                        ? response.StatusCode : HttpStatusCode.BadRequest;
                    return BuildResponse(statusCode, response, correlationId);
                }

                // 5. Post-hook domain event — replaces IErpPostDeleteManyToManyRelationHook
                await PublishDomainEvent(
                    "entity-management.relation.deleted",
                    new
                    {
                        eventType = "relation.m2m.deleted",
                        relationId = relationId.ToString(),
                        relationName = relation.Name,
                        originValue = originValue.ToString(),
                        targetValue = targetValue.ToString(),
                        userId = ExtractUserId(request),
                        timestamp = DateTime.UtcNow.ToString("o"),
                        idempotencyKey = $"m2m-delete-{relationId}-{originValue}-{targetValue}-{DateTime.UtcNow.Ticks}"
                    },
                    correlationId);

                _logger.LogInformation(
                    "[{CorrelationId}] M2M relation record removed: relation={RelationName}, origin={Origin}, target={Target}",
                    correlationId, relation.Name, originValue, targetValue);

                response.StatusCode = HttpStatusCode.OK;
                return BuildResponse(HttpStatusCode.OK, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{CorrelationId}] Unhandled exception in RemoveRelationManyToManyRecord", correlationId);
                return BuildErrorResponse(HttpStatusCode.InternalServerError,
                    "An unexpected error occurred while removing the relation record.", correlationId);
            }
        }

        // =================================================================
        // Count — POST /v1/entity-management/entities/{entityName}/records/count
        // =================================================================

        /// <summary>
        /// Lambda handler for counting records matching filter criteria.
        /// Permission check, parse optional QueryObject filter, delegate to
        /// IRecordService.Count, return QueryCountResponse.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> Count(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] Count invoked", correlationId);

            try
            {
                // 1. Extract entity name from route
                var entityName = GetParam(request.PathParameters, "entityName");
                if (string.IsNullOrWhiteSpace(entityName))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest,
                        "Entity name is required in path parameters.", correlationId);
                }

                // 2. Resolve entity metadata
                var entity = await ResolveEntity(entityName);
                if (entity == null)
                {
                    return BuildErrorResponse(HttpStatusCode.NotFound,
                        $"Entity '{entityName}' not found.", correlationId);
                }
                entityName = entity.Name; // Normalize to actual name for DynamoDB PK lookups

                // 3. Permission check — RecordPermissions.CanRead (count is a read operation)
                if (!HasPermission(request, entity, EntityPermission.Read))
                {
                    _logger.LogWarning(
                        "[{CorrelationId}] Permission denied for Count on entity '{EntityName}'",
                        correlationId, entityName);
                    return BuildErrorResponse(HttpStatusCode.Forbidden,
                        "Access denied. You do not have permission to read records for this entity.",
                        correlationId);
                }

                // 4. Parse optional filter from body
                QueryObject? queryObj = null;
                if (!string.IsNullOrWhiteSpace(request.Body))
                {
                    try
                    {
                        queryObj = JsonSerializer.Deserialize<QueryObject>(request.Body, _jsonOptions);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{CorrelationId}] Failed to parse count filter", correlationId);
                        return BuildErrorResponse(HttpStatusCode.BadRequest,
                            $"Invalid filter body: {ex.Message}", correlationId);
                    }
                }

                // 5. Execute count
                var response = await _recordService.Count(entityName, queryObj);

                if (!response.Success)
                {
                    return BuildResponse(
                        response.StatusCode != default ? response.StatusCode : HttpStatusCode.InternalServerError,
                        response, correlationId);
                }

                _logger.LogInformation(
                    "[{CorrelationId}] Count success: entity={EntityName}, count={Count}",
                    correlationId, entityName, response.Object);

                return BuildResponse(HttpStatusCode.OK, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] Unhandled exception in Count", correlationId);
                return BuildErrorResponse(HttpStatusCode.InternalServerError,
                    "An unexpected error occurred while counting records.", correlationId);
            }
        }

        // =================================================================
        // Private Helpers — Parameter Extraction
        // =================================================================

        /// <summary>
        /// Safely extracts a parameter value from a dictionary (path/query params).
        /// Returns empty string if the dictionary is null or key not found.
        /// </summary>
        private static string GetParam(IDictionary<string, string>? parameters, string key)
        {
            if (parameters == null) return string.Empty;
            if (parameters.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return value;
            // Fall back to {proxy+} path parameter for HTTP API v2 catch-all routes.
            // Proxy path pattern: "{entityName}" or "{entityName}/{recordId}" or
            // "relations/{entityName}/{recordId}" or "regex/{entityName}/{fieldName}".
            if (!parameters.TryGetValue("proxy", out var proxy) || string.IsNullOrEmpty(proxy))
                return string.Empty;
            var segments = proxy.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Map key to segment index based on proxy structure
            if (key == "entityName")
            {
                // First non-keyword segment is the entity name
                foreach (var seg in segments)
                {
                    if (seg != "relations" && seg != "reverse" && seg != "regex" && seg != "import" && seg != "export")
                        return seg;
                }
            }
            else if (key == "recordId")
            {
                // GUID segment after entityName
                for (var i = 1; i < segments.Length; i++)
                {
                    if (Guid.TryParse(segments[i], out _))
                        return segments[i];
                }
            }
            else if (key == "relationId")
            {
                // Last GUID segment
                for (var i = segments.Length - 1; i >= 0; i--)
                {
                    if (Guid.TryParse(segments[i], out _))
                        return segments[i];
                }
            }
            else if (key == "fieldName")
            {
                // Segment after "regex/{entityName}/"
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    if (segments[i] == "regex" && i + 2 < segments.Length)
                        return segments[i + 2];
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Safely extracts a parameter value from a dictionary, returning null if not found.
        /// </summary>
        private static string? GetParamOrNull(IDictionary<string, string>? parameters, string key)
        {
            if (parameters == null) return null;
            return parameters.TryGetValue(key, out var value) ? value : null;
        }

        // =================================================================
        // Private Helpers — Correlation ID and Tracing
        // =================================================================

        /// <summary>
        /// Extracts or generates a correlation ID for request tracing.
        /// Checks X-Correlation-Id header first, then falls back to Lambda request ID.
        /// Enables structured logging with correlation-ID propagation per AAP §0.8.5.
        /// </summary>
        private static string ExtractCorrelationId(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            if (request.Headers != null)
            {
                // Check case-insensitive header lookup
                foreach (var header in request.Headers)
                {
                    if (string.Equals(header.Key, "x-correlation-id", StringComparison.OrdinalIgnoreCase))
                    {
                        return header.Value;
                    }
                }
            }
            return context.AwsRequestId ?? Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Extracts the authenticated user ID from JWT claims in the request context.
        /// Checks multiple claim formats: Cognito 'sub', custom 'userId', authorizer context.
        /// </summary>
        private static string ExtractUserId(APIGatewayHttpApiV2ProxyRequest request)
        {
            if (request.RequestContext?.Authorizer?.Jwt?.Claims != null)
            {
                var claims = request.RequestContext.Authorizer.Jwt.Claims;
                if (claims.TryGetValue("sub", out var sub) && !string.IsNullOrWhiteSpace(sub))
                    return sub;
                if (claims.TryGetValue("userId", out var userId) && !string.IsNullOrWhiteSpace(userId))
                    return userId;
                if (claims.TryGetValue("custom:userId", out var customUserId)
                    && !string.IsNullOrWhiteSpace(customUserId))
                    return customUserId;
            }

            // Check Lambda authorizer context (custom authorizer)
            if (request.RequestContext?.Authorizer?.Lambda != null)
            {
                foreach (var kv in request.RequestContext.Authorizer.Lambda)
                {
                    if (string.Equals(kv.Key, "userId", StringComparison.OrdinalIgnoreCase)
                        && kv.Value != null)
                    {
                        return kv.Value.ToString() ?? "unknown";
                    }
                }
            }

            return "unknown";
        }

        // =================================================================
        // Private Helpers — Permission Checking
        // =================================================================

        /// <summary>
        /// Checks whether the authenticated user has the specified permission on the entity.
        /// Extracts role GUIDs from JWT claims, then checks against entity's RecordPermissions
        /// (CanRead/CanCreate/CanUpdate/CanDelete GUID lists).
        /// Administrator role always bypasses permission checks.
        /// </summary>
        private bool HasPermission(
            APIGatewayHttpApiV2ProxyRequest request,
            Entity entity,
            EntityPermission permission)
        {
            // Administrator bypass — admin users have all permissions
            if (IsAdminUser(request))
                return true;

            // Extract user role GUIDs from JWT claims
            var userRoles = ExtractUserRoles(request);

            // No roles means no permissions (unless entity allows everyone)
            if (userRoles.Count == 0)
            {
                // Check if guest role is in the permissions list
                var guestRoleId = SystemIds.GuestRoleId;
                return HasRoleInPermissionList(new List<Guid> { guestRoleId }, entity, permission);
            }

            return HasRoleInPermissionList(userRoles, entity, permission);
        }

        /// <summary>
        /// Checks if any of the user's roles appear in the entity's permission list
        /// for the specified operation.
        /// </summary>
        private static bool HasRoleInPermissionList(
            List<Guid> userRoles,
            Entity entity,
            EntityPermission permission)
        {
            if (entity.RecordPermissions == null)
                return false;

            List<Guid> allowedRoles = permission switch
            {
                EntityPermission.Read => entity.RecordPermissions.CanRead,
                EntityPermission.Create => entity.RecordPermissions.CanCreate,
                EntityPermission.Update => entity.RecordPermissions.CanUpdate,
                EntityPermission.Delete => entity.RecordPermissions.CanDelete,
                _ => new List<Guid>()
            };

            return userRoles.Any(role => allowedRoles.Contains(role));
        }

        /// <summary>
        /// Checks whether the current user has the Administrator role.
        /// Inspects JWT claims (cognito:groups, custom:roles, scope) and
        /// Lambda authorizer context (isAdmin, roles).
        /// </summary>
        private static bool IsAdminUser(APIGatewayHttpApiV2ProxyRequest request)
        {
            var adminRoleId = SystemIds.AdministratorRoleId;
            var adminRoleStr = adminRoleId.ToString().ToLowerInvariant();

            // Check JWT claims
            if (request.RequestContext?.Authorizer?.Jwt?.Claims != null)
            {
                var claims = request.RequestContext.Authorizer.Jwt.Claims;

                // Check cognito:groups
                if (claims.TryGetValue("cognito:groups", out var groups)
                    && !string.IsNullOrWhiteSpace(groups))
                {
                    if (groups.Contains("admin", StringComparison.OrdinalIgnoreCase)
                        || groups.Contains(adminRoleStr, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                // Check custom:roles
                if (claims.TryGetValue("custom:roles", out var roles)
                    && !string.IsNullOrWhiteSpace(roles))
                {
                    if (roles.Contains(adminRoleStr, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                // Check scope
                if (claims.TryGetValue("scope", out var scope)
                    && !string.IsNullOrWhiteSpace(scope))
                {
                    if (scope.Contains("admin", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // Check Lambda authorizer context
            if (request.RequestContext?.Authorizer?.Lambda != null)
            {
                foreach (var kv in request.RequestContext.Authorizer.Lambda)
                {
                    if (string.Equals(kv.Key, "isAdmin", StringComparison.OrdinalIgnoreCase)
                        && kv.Value != null)
                    {
                        var val = kv.Value.ToString();
                        if (string.Equals(val, "true", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    if (string.Equals(kv.Key, "roles", StringComparison.OrdinalIgnoreCase)
                        && kv.Value != null)
                    {
                        var rolesVal = kv.Value.ToString() ?? "";
                        if (rolesVal.Contains(adminRoleStr, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Extracts role GUIDs from the JWT claims.
        /// Parses from cognito:groups and custom:roles claim values.
        /// </summary>
        private static List<Guid> ExtractUserRoles(APIGatewayHttpApiV2ProxyRequest request)
        {
            var roles = new List<Guid>();

            if (request.RequestContext?.Authorizer?.Jwt?.Claims == null)
                return roles;

            var claims = request.RequestContext.Authorizer.Jwt.Claims;

            // Parse from cognito:groups — space or comma separated
            if (claims.TryGetValue("cognito:groups", out var groups) && !string.IsNullOrWhiteSpace(groups))
            {
                ParseRoleGuids(groups, roles);
            }

            // Parse from custom:roles
            if (claims.TryGetValue("custom:roles", out var customRoles) && !string.IsNullOrWhiteSpace(customRoles))
            {
                ParseRoleGuids(customRoles, roles);
            }

            // Parse from Lambda authorizer context
            if (request.RequestContext?.Authorizer?.Lambda != null)
            {
                foreach (var kv in request.RequestContext.Authorizer.Lambda)
                {
                    if (string.Equals(kv.Key, "roles", StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                    {
                        ParseRoleGuids(kv.Value.ToString() ?? "", roles);
                    }
                }
            }

            return roles.Distinct().ToList();
        }

        /// <summary>
        /// Parses GUID values from a delimited string (comma, space, pipe, semicolon separated).
        /// </summary>
        private static void ParseRoleGuids(string input, List<Guid> roles)
        {
            var parts = input.Split(new[] { ',', ' ', '|', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (Guid.TryParse(part.Trim(), out var guid))
                {
                    roles.Add(guid);
                }
            }
        }

        // =================================================================
        // Private Helpers — Response Building
        // =================================================================

        /// <summary>
        /// Builds an APIGatewayHttpApiV2ProxyResponse with the given status code, body,
        /// and correlation ID header. Serializes body using System.Text.Json for AOT safety.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(
            HttpStatusCode statusCode,
            object body,
            string correlationId)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = (int)statusCode,
                Body = JsonSerializer.Serialize(body, _jsonOptions),
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "X-Correlation-Id", correlationId }
                }
            };
        }

        /// <summary>
        /// Builds a standardized error response using BaseResponseModel envelope.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(
            HttpStatusCode statusCode,
            string message,
            string correlationId,
            List<ErrorModel>? errors = null)
        {
            var responseModel = new BaseResponseModel
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = message,
                Errors = errors ?? new List<ErrorModel>(),
                StatusCode = statusCode
            };

            return BuildResponse(statusCode, responseModel, correlationId);
        }

        /// <summary>
        /// Builds a validation error response with a list of structured ErrorModel items.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildValidationErrorResponse(
            HttpStatusCode statusCode,
            string message,
            List<ErrorModel> errors,
            string correlationId)
        {
            return BuildErrorResponse(statusCode, message, correlationId, errors);
        }

        // =================================================================
        // Private Helpers — Domain Event Publishing (SNS)
        // =================================================================

        /// <summary>
        /// Publishes a domain event to SNS for post-hook event-driven architecture.
        /// Replaces synchronous RecordHookManager post-hook pattern.
        /// Event naming: {domain}.{entity}.{action} per AAP §0.8.5.
        /// Non-blocking — catches and logs errors without failing the response.
        /// </summary>
        private async Task PublishDomainEvent(
            string eventType,
            object eventData,
            string correlationId)
        {
            if (string.IsNullOrWhiteSpace(_recordTopicArn))
            {
                _logger.LogWarning(
                    "[{CorrelationId}] RECORD_TOPIC_ARN not configured, skipping domain event: {EventType}",
                    correlationId, eventType);
                return;
            }

            try
            {
                var message = Newtonsoft.Json.JsonConvert.SerializeObject(eventData);
                var publishRequest = new PublishRequest
                {
                    TopicArn = _recordTopicArn,
                    Message = message,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        },
                        ["correlationId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        },
                        ["timestamp"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = DateTime.UtcNow.ToString("o")
                        }
                    }
                };

                await _snsClient.PublishAsync(publishRequest);
                _logger.LogInformation(
                    "[{CorrelationId}] Domain event published: {EventType}",
                    correlationId, eventType);
            }
            catch (Exception ex)
            {
                // Non-blocking: log error but do not fail the API response
                _logger.LogError(ex,
                    "[{CorrelationId}] Failed to publish domain event: {EventType}",
                    correlationId, eventType);
            }
        }

        // =================================================================
        // Private Helpers — Pre-Hook Validation (synchronous, in-handler)
        // Replaces IErpPreCreateRecordHook / IErpPreUpdateRecordHook / IErpPreDeleteRecordHook
        // =================================================================

        /// <summary>
        /// Validates record data before creation. Checks required fields (e.g., entity must
        /// have an 'id' field), field type constraints, and business invariants.
        /// Replaces synchronous IErpPreCreateRecordHook execution.
        /// </summary>
        private List<ErrorModel> ValidatePreCreateRecord(Entity entity, EntityRecord record)
        {
            var errors = new List<ErrorModel>();

            // Note: Field-level required/unique validation is delegated to RecordService
            // which has full field metadata. The handler performs only lightweight structural
            // checks. Entities may have zero custom fields (only system-assigned "id"),
            // mirroring the source monolith's RecordManager behavior.

            if (entity.Fields != null)
            {
                foreach (var field in entity.Fields)
                {
                    if (field == null) continue;
                    var fieldType = field.GetFieldType();

                    // AutoNumber fields are auto-generated, skip validation
                    if (fieldType == FieldType.AutoNumberField) continue;

                    // Password fields can be empty on create (optional)
                    if (fieldType == FieldType.PasswordField) continue;

                    // Lightweight structural validation only; full required/unique checks
                    // are in RecordService.CreateRecord
                }
            }

            return errors;
        }

        /// <summary>
        /// Validates record data before update. Ensures record ID is present,
        /// and field values conform to type constraints.
        /// Replaces synchronous IErpPreUpdateRecordHook execution.
        /// </summary>
        private List<ErrorModel> ValidatePreUpdateRecord(Entity entity, EntityRecord record)
        {
            var errors = new List<ErrorModel>();

            // Verify record has an ID
            if (!record.ContainsKey("id") || record["id"] == null)
            {
                errors.Add(new ErrorModel("id", "",
                    "Record ID is required for update operations."));
            }

            return errors;
        }

        /// <summary>
        /// Validates before deletion. Placeholder for any pre-delete business rules.
        /// Replaces synchronous IErpPreDeleteRecordHook execution.
        /// </summary>
        private List<ErrorModel> ValidatePreDeleteRecord(Entity entity, Guid recordId)
        {
            var errors = new List<ErrorModel>();

            // System entities or locked records could be validated here
            // Currently, the permission check handles authorization
            // RecordService handles actual existence validation

            return errors;
        }

        // =================================================================
        // Private Helpers — Record Deserialization
        // =================================================================

        /// <summary>
        /// Deserializes a JSON request body into an EntityRecord (Dictionary&lt;string, object?&gt;).
        /// Uses Newtonsoft.Json for polymorphic value handling (JArray → List, JObject → nested dict).
        /// </summary>
        private static EntityRecord DeserializeRecord(string body)
        {
            var record = new EntityRecord();

            // Parse with Newtonsoft for polymorphic handling of arrays, nested objects, etc.
            var jObj = JObject.Parse(body);

            foreach (var property in jObj.Properties())
            {
                record[property.Name] = ConvertJToken(property.Value);
            }

            return record;
        }

        /// <summary>
        /// Converts a JToken value to a CLR type suitable for EntityRecord storage.
        /// Handles: null, string, integer, float, boolean, array, object.
        /// </summary>
        private static object? ConvertJToken(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return null;
                case JTokenType.String:
                    return token.Value<string>();
                case JTokenType.Integer:
                    return token.Value<long>();
                case JTokenType.Float:
                    return token.Value<decimal>();
                case JTokenType.Boolean:
                    return token.Value<bool>();
                case JTokenType.Guid:
                    return token.Value<Guid>();
                case JTokenType.Date:
                    return token.Value<DateTime>();
                case JTokenType.Array:
                    var list = new List<object?>();
                    foreach (var item in (JArray)token)
                    {
                        list.Add(ConvertJToken(item));
                    }
                    return list;
                case JTokenType.Object:
                    var dict = new Dictionary<string, object?>();
                    foreach (var prop in (JObject)token)
                    {
                        dict[prop.Key] = prop.Value != null ? ConvertJToken(prop.Value) : null;
                    }
                    return dict;
                default:
                    return token.ToString();
            }
        }

        // =================================================================
        // Private Helpers — EntityQuery Parsing
        // =================================================================

        /// <summary>
        /// Parses an EntityQuery from a JSON request body for FindRecords.
        /// Supports fields: "fields" (string), "query" (QueryObject), "sort" (array),
        /// "skip" (int), "limit" (int).
        /// </summary>
        private static EntityQuery ParseEntityQuery(string entityName, string body)
        {
            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // fields — comma-separated string or "*"
            var fields = "*";
            if (root.TryGetProperty("fields", out var fieldsProp))
            {
                fields = fieldsProp.GetString() ?? "*";
            }

            // query — recursive filter tree
            QueryObject? query = null;
            if (root.TryGetProperty("query", out var queryProp)
                && queryProp.ValueKind != JsonValueKind.Null)
            {
                query = JsonSerializer.Deserialize<QueryObject>(
                    queryProp.GetRawText(), _jsonOptions);
            }

            // sort — array of sort objects
            QuerySortObject[]? sort = null;
            if (root.TryGetProperty("sort", out var sortProp)
                && sortProp.ValueKind == JsonValueKind.Array)
            {
                sort = JsonSerializer.Deserialize<QuerySortObject[]>(
                    sortProp.GetRawText(), _jsonOptions);
            }

            // skip and limit
            int? skip = null;
            if (root.TryGetProperty("skip", out var skipProp)
                && skipProp.TryGetInt32(out var skipVal))
            {
                skip = skipVal;
            }

            int? limit = null;
            if (root.TryGetProperty("limit", out var limitProp)
                && limitProp.TryGetInt32(out var limitVal))
            {
                limit = limitVal;
            }

            return new EntityQuery(entityName, fields, query, sort, skip, limit);
        }

        /// <summary>
        /// Builds an EntityQuery from URL query string parameters (fallback for GET-style).
        /// Supports: fields, skip, limit parameters.
        /// </summary>
        private static EntityQuery BuildQueryFromParams(
            string entityName,
            IDictionary<string, string>? queryParams)
        {
            var fields = "*";
            int? skip = null;
            int? limit = null;

            if (queryParams != null)
            {
                if (queryParams.TryGetValue("fields", out var fieldsStr)
                    && !string.IsNullOrWhiteSpace(fieldsStr))
                {
                    fields = fieldsStr;
                }

                if (queryParams.TryGetValue("skip", out var skipStr)
                    && int.TryParse(skipStr, out var skipVal))
                {
                    skip = skipVal;
                }

                if (queryParams.TryGetValue("limit", out var limitStr)
                    && int.TryParse(limitStr, out var limitVal))
                {
                    limit = limitVal;
                }
            }

            return new EntityQuery(entityName, fields, null, null, skip, limit);
        }

        // =================================================================
        // Private Helpers — Field Value Normalization
        // Replaces RecordManager.ExtractFieldValue for 21+ field types
        // =================================================================

        /// <summary>
        /// Normalizes all field values in the record based on the entity's field type
        /// definitions. Handles: Currency rounding, Date/DateTime timezone, GUID parsing,
        /// MultiSelect array conversion, Password hashing, AutoNumber skipping,
        /// File/Image path normalization, and more.
        /// Source: RecordManager.ExtractFieldValue per-type switch logic.
        /// </summary>
        private void NormalizeFieldValues(Entity entity, EntityRecord record)
        {
            if (entity.Fields == null || entity.Fields.Count == 0)
                return;

            // Build a snapshot of keys to iterate safely (record may be modified)
            var keysToProcess = record.Keys
                .Where(k => !k.Contains(RELATION_SEPARATOR))
                .ToList();

            foreach (var key in keysToProcess)
            {
                // Find matching field definition
                var field = entity.Fields.FirstOrDefault(f =>
                    f != null && string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));

                if (field == null)
                    continue; // Skip unknown fields — RecordService handles validation

                var fieldType = field.GetFieldType();
                var value = record[key];

                record[key] = NormalizeFieldValue(fieldType, value, key);
            }
        }

        /// <summary>
        /// Normalizes a single field value based on its FieldType.
        /// Comprehensive switch covering all 21 field types from the source monolith.
        /// </summary>
        private static object? NormalizeFieldValue(FieldType fieldType, object? value, string fieldName)
        {
            if (value == null)
                return null;

            // Treat empty strings as null for typed (non-text) fields. The React form
            // submits "" for untouched inputs; storing "" as a number/date/guid causes
            // ExtractFieldValue to throw in RecordService.
            if (value is string strVal && string.IsNullOrWhiteSpace(strVal))
            {
                switch (fieldType)
                {
                    case FieldType.NumberField:
                    case FieldType.CurrencyField:
                    case FieldType.PercentField:
                    case FieldType.AutoNumberField:
                    case FieldType.DateField:
                    case FieldType.DateTimeField:
                    case FieldType.GuidField:
                    case FieldType.CheckboxField:
                        return null;
                    // For text-like fields, preserve the empty string (or convert to null
                    // depending on whether the field is required — caller handles that).
                    default:
                        break;
                }
            }

            switch (fieldType)
            {
                case FieldType.AutoNumberField:
                    // AutoNumber fields are auto-generated by RecordService; pass through
                    if (value is long || value is int || value is decimal)
                        return Convert.ToDecimal(value);
                    if (decimal.TryParse(value.ToString(), out var autoNum))
                        return autoNum;
                    return value;

                case FieldType.CheckboxField:
                    if (value is bool boolVal)
                        return boolVal;
                    if (bool.TryParse(value.ToString(), out var parsedBool))
                        return parsedBool;
                    // Numeric truthiness: 0=false, non-zero=true
                    if (decimal.TryParse(value.ToString(), out var numBool))
                        return numBool != 0;
                    return false;

                case FieldType.CurrencyField:
                    // Currency: convert to decimal with rounding (source uses Math.Round)
                    if (value is decimal decVal)
                        return Math.Round(decVal, 4);
                    if (decimal.TryParse(value.ToString(), out var parsedDec))
                        return Math.Round(parsedDec, 4);
                    return value;

                case FieldType.DateField:
                    // Date only — strip time component, store as UTC date
                    if (value is DateTime dt)
                        return dt.Date;
                    if (value is DateTimeOffset dto)
                        return dto.UtcDateTime.Date;
                    if (DateTime.TryParse(value.ToString(), out var parsedDate))
                        return parsedDate.Date;
                    return value;

                case FieldType.DateTimeField:
                    // DateTime — preserve full precision, ensure UTC
                    if (value is DateTime dtValue)
                        return dtValue.Kind == DateTimeKind.Utc ? dtValue : dtValue.ToUniversalTime();
                    if (value is DateTimeOffset dtoValue)
                        return dtoValue.UtcDateTime;
                    if (DateTime.TryParse(value.ToString(), out var parsedDateTime))
                        return parsedDateTime.ToUniversalTime();
                    return value;

                case FieldType.EmailField:
                    // Email: trim whitespace, lowercase (normalization)
                    return value.ToString()?.Trim().ToLowerInvariant();

                case FieldType.FileField:
                    // File path normalization — handled by ProcessFileFields separately
                    return NormalizeFilePath(value);

                case FieldType.GuidField:
                    if (value is Guid guidVal)
                        return guidVal;
                    if (Guid.TryParse(value.ToString(), out var parsedGuid))
                        return parsedGuid;
                    return value;

                case FieldType.HtmlField:
                    // HTML: pass through as-is (sanitization is caller responsibility)
                    return value.ToString();

                case FieldType.ImageField:
                    // Image path normalization — handled by ProcessFileFields separately
                    return NormalizeFilePath(value);

                case FieldType.MultiLineTextField:
                    return value.ToString();

                case FieldType.MultiSelectField:
                    // MultiSelect: convert from JArray/string to List<string>
                    return NormalizeMultiSelectValue(value);

                case FieldType.NumberField:
                    if (value is decimal numDecVal)
                        return numDecVal;
                    if (decimal.TryParse(value.ToString(), out var parsedNum))
                        return parsedNum;
                    return value;

                case FieldType.PasswordField:
                    // Password: store as-is (hashing is done by RecordService)
                    // Source: PasswordUtil handles MD5→hash migration
                    return value.ToString();

                case FieldType.PercentField:
                    if (value is decimal pctVal)
                        return pctVal;
                    if (decimal.TryParse(value.ToString(), out var parsedPct))
                        return parsedPct;
                    return value;

                case FieldType.PhoneField:
                    // Phone: trim whitespace
                    return value.ToString()?.Trim();

                case FieldType.SelectField:
                    return value.ToString();

                case FieldType.TextField:
                    return value.ToString();

                case FieldType.UrlField:
                    return value.ToString()?.Trim();

                case FieldType.GeographyField:
                    // Geography: pass through — could be GeoJSON object or WKT string
                    return value;

                default:
                    return value;
            }
        }

        /// <summary>
        /// Normalizes file/image path values. Strips leading /fs/ prefix and normalizes
        /// path separators for S3 key format.
        /// Source: RecordManager file path normalization logic.
        /// </summary>
        private static object? NormalizeFilePath(object? value)
        {
            if (value == null)
                return null;

            var strValue = value.ToString();
            if (string.IsNullOrWhiteSpace(strValue))
                return null;

            // Strip /fs/ or fs/ prefix (monolith URL format)
            if (strValue.StartsWith("/fs/", StringComparison.OrdinalIgnoreCase))
            {
                strValue = strValue.Substring(4);
            }
            else if (strValue.StartsWith("fs/", StringComparison.OrdinalIgnoreCase))
            {
                strValue = strValue.Substring(3);
            }

            // Normalize path separators for S3
            strValue = strValue.Replace('\\', '/');

            return strValue;
        }

        /// <summary>
        /// Normalizes a MultiSelect field value from various input formats into a
        /// List&lt;string&gt;. Handles JArray, List, comma-separated string, and single values.
        /// Source: RecordManager JArray→List conversion logic.
        /// </summary>
        private static object? NormalizeMultiSelectValue(object? value)
        {
            if (value == null)
                return null;

            // Handle JArray (from Newtonsoft deserialization)
            if (value is JArray jArray)
            {
                return jArray.Select(t => t.ToString()).ToList();
            }

            // Handle List<object?> (from ConvertJToken)
            if (value is List<object?> objList)
            {
                return objList.Select(o => o?.ToString() ?? "").ToList();
            }

            // Handle IEnumerable<string>
            if (value is IEnumerable<string> strEnum)
            {
                return strEnum.ToList();
            }

            // Handle comma-separated string
            var strVal = value.ToString();
            if (!string.IsNullOrWhiteSpace(strVal))
            {
                if (strVal.Contains(','))
                {
                    return strVal.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .ToList();
                }
                return new List<string> { strVal };
            }

            return new List<string>();
        }

        // =================================================================
        // Private Helpers — Relation-Aware Input Processing
        // Source: RecordManager.CreateRecord relation parsing (2-pass processing)
        // =================================================================

        /// <summary>
        /// Processes record property keys containing RELATION_SEPARATOR ('.') as
        /// first-level relation references. Left segment is the relation marker
        /// ($relationName or $$relationName to flip direction), right segment is
        /// the field in the related entity used to locate related records.
        /// 
        /// Source logic preserved from RecordManager.CreateRecord:
        /// 1. Keys with '.' are extracted and removed from the record
        /// 2. Left segment parsed: $ prefix = origin→target direction, $$ = target→origin
        /// 3. Right segment is used to query related entity for matching records
        /// 4. Depending on relation type (1:1, 1:N, M:M), appropriate FK writes or
        ///    bridge records are created
        /// </summary>
        private async Task<List<ErrorModel>> ProcessRelationInputs(
            Entity entity,
            EntityRecord record,
            string correlationId,
            bool isCreate)
        {
            var errors = new List<ErrorModel>();

            // Collect relation keys (keys containing RELATION_SEPARATOR)
            var relationKeys = record.Keys
                .Where(k => k.Contains(RELATION_SEPARATOR))
                .ToList();

            if (relationKeys.Count == 0)
                return errors;

            // Fetch all relations for resolution
            EntityRelationListResponse? relationsResponse = null;
            try
            {
                relationsResponse = await _entityService.ReadRelations();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{CorrelationId}] Failed to load relations for relation-aware input processing",
                    correlationId);
                errors.Add(new ErrorModel("relations", "", "Failed to load entity relations."));
                return errors;
            }

            var allRelations = relationsResponse?.Object ?? new List<EntityRelation>();

            // Process each relation key
            foreach (var key in relationKeys)
            {
                var dotIndex = key.IndexOf(RELATION_SEPARATOR);
                if (dotIndex <= 0 || dotIndex >= key.Length - 1)
                {
                    // Remove malformed relation keys from the record
                    record.Remove(key);
                    continue;
                }

                var leftSegment = key.Substring(0, dotIndex);   // e.g., "$user_role" or "$$user_role"
                var rightSegment = key.Substring(dotIndex + 1);  // e.g., "username"
                var relationValue = record[key];

                // Remove the relation key from the record — it's not a real field
                record.Remove(key);

                // Parse relation marker
                bool reverseDirection = false;
                string relationName;

                if (leftSegment.StartsWith("$$"))
                {
                    reverseDirection = true;
                    relationName = leftSegment.Substring(2); // Strip $$
                }
                else if (leftSegment.StartsWith("$"))
                {
                    reverseDirection = false;
                    relationName = leftSegment.Substring(1); // Strip $
                }
                else
                {
                    // No $ prefix — treat as regular relation reference
                    relationName = leftSegment;
                }

                if (string.IsNullOrWhiteSpace(relationName))
                    continue;

                // Find matching relation
                var relation = allRelations.FirstOrDefault(r =>
                    string.Equals(r.Name, relationName, StringComparison.OrdinalIgnoreCase));

                if (relation == null)
                {
                    _logger.LogWarning(
                        "[{CorrelationId}] Relation '{RelationName}' not found for key '{Key}'",
                        correlationId, relationName, key);
                    continue; // Skip unknown relations — don't fail the whole operation
                }

                // Determine direction: which entity is "this" entity and which is "related"
                bool isOriginEntity;
                if (!reverseDirection)
                {
                    // Default: current entity is origin
                    isOriginEntity = relation.OriginEntityId == entity.Id;
                    if (!isOriginEntity && relation.TargetEntityId == entity.Id)
                    {
                        // Current entity is target — flip direction
                        isOriginEntity = false;
                    }
                    else if (!isOriginEntity)
                    {
                        _logger.LogWarning(
                            "[{CorrelationId}] Entity '{EntityName}' is neither origin nor target of relation '{RelationName}'",
                            correlationId, entity.Name, relationName);
                        continue;
                    }
                }
                else
                {
                    // $$ means flip direction explicitly
                    isOriginEntity = relation.TargetEntityId == entity.Id;
                }

                // Resolve the related entity name and FK field
                string relatedEntityName;
                Guid fkFieldId;

                if (isOriginEntity)
                {
                    // Current entity has the origin field, related entity has target field
                    relatedEntityName = relation.TargetEntityName ?? "";
                    fkFieldId = relation.OriginFieldId;
                }
                else
                {
                    // Current entity has the target field, related entity has origin field
                    relatedEntityName = relation.OriginEntityName ?? "";
                    fkFieldId = relation.TargetFieldId;
                }

                if (string.IsNullOrWhiteSpace(relatedEntityName))
                    continue;

                // Find the FK field name on the current entity
                var fkField = entity.Fields?.FirstOrDefault(f => f?.Id == fkFieldId);
                var fkFieldName = fkField?.Name;

                // Handle M:M relations differently — create bridge records
                if (relation.RelationType == EntityRelationType.ManyToMany)
                {
                    await ProcessManyToManyRelationInput(
                        entity, record, relation, relatedEntityName, rightSegment,
                        relationValue, isOriginEntity, isCreate, correlationId, errors);
                    continue;
                }

                // For 1:1 and 1:N: look up related record by rightSegment field value
                if (relationValue == null || string.IsNullOrWhiteSpace(fkFieldName))
                    continue;

                try
                {
                    var lookupQuery = new EntityQuery(
                        entityName: relatedEntityName,
                        fields: "id",
                        query: EntityQuery.QueryEQ(rightSegment, relationValue));

                    var lookupResult = await _recordService.Find(lookupQuery);

                    if (lookupResult.Success && lookupResult.Object?.Data != null
                        && lookupResult.Object.Data.Count > 0)
                    {
                        var relatedRecordId = lookupResult.Object.Data[0].GetValue("id");
                        if (relatedRecordId != null)
                        {
                            // Write the FK value to the current record
                            record[fkFieldName] = relatedRecordId;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[{CorrelationId}] Failed to resolve relation '{RelationName}' for field '{Field}'",
                        correlationId, relationName, rightSegment);
                }
            }

            return errors;
        }

        /// <summary>
        /// Processes a M:M relation input. For create operations, creates bridge records.
        /// For update operations, manages add/remove of bridge records.
        /// </summary>
        private async Task ProcessManyToManyRelationInput(
            Entity entity,
            EntityRecord record,
            EntityRelation relation,
            string relatedEntityName,
            string lookupFieldName,
            object? lookupValue,
            bool isOriginEntity,
            bool isCreate,
            string correlationId,
            List<ErrorModel> errors)
        {
            if (lookupValue == null)
                return;

            try
            {
                // Look up the related record by the specified field
                var lookupQuery = new EntityQuery(
                    entityName: relatedEntityName,
                    fields: "id",
                    query: EntityQuery.QueryEQ(lookupFieldName, lookupValue));

                var lookupResult = await _recordService.Find(lookupQuery);

                if (!lookupResult.Success || lookupResult.Object?.Data == null
                    || lookupResult.Object.Data.Count == 0)
                {
                    _logger.LogWarning(
                        "[{CorrelationId}] M2M related record not found: entity={Entity}, field={Field}, value={Value}",
                        correlationId, relatedEntityName, lookupFieldName, lookupValue);
                    return;
                }

                var relatedRecordId = lookupResult.Object.Data[0].GetValue("id");
                if (relatedRecordId == null || !Guid.TryParse(relatedRecordId.ToString(), out var relatedGuid))
                    return;

                // Determine origin and target values for the bridge record
                var recordIdObj = record.ContainsKey("id") ? record["id"] : null;
                if (recordIdObj == null || !Guid.TryParse(recordIdObj.ToString(), out var currentRecordId))
                    return;

                Guid originValue, targetValue;
                if (isOriginEntity)
                {
                    originValue = currentRecordId;
                    targetValue = relatedGuid;
                }
                else
                {
                    originValue = relatedGuid;
                    targetValue = currentRecordId;
                }

                // Create the bridge record
                await _recordService.CreateRelationManyToManyRecord(
                    relation.Id, originValue, targetValue);

                _logger.LogInformation(
                    "[{CorrelationId}] M2M bridge record created: relation={Relation}, origin={Origin}, target={Target}",
                    correlationId, relation.Name, originValue, targetValue);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[{CorrelationId}] Failed to process M2M relation input for '{Relation}'",
                    correlationId, relation.Name);
            }
        }

        // =================================================================
        // Private Helpers — File/Image Field Processing (S3)
        // =================================================================

        /// <summary>
        /// Processes file and image fields — moves S3 objects from the temp upload
        /// prefix to permanent entity/record-specific paths.
        /// Replaces: monolith DbFileRepository TMP_FOLDER_NAME file move logic.
        /// </summary>
        private async Task ProcessFileFields(
            Entity entity,
            EntityRecord record,
            string correlationId)
        {
            if (string.IsNullOrWhiteSpace(_s3BucketName) || entity.Fields == null)
                return;

            foreach (var field in entity.Fields)
            {
                if (field == null) continue;
                var fieldType = field.GetFieldType();

                if (fieldType != FieldType.FileField && fieldType != FieldType.ImageField)
                    continue;

                if (!record.ContainsKey(field.Name) || record[field.Name] == null)
                    continue;

                var filePath = record[field.Name]?.ToString();
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;

                // Check if file is in temp prefix — needs to be moved
                if (filePath.StartsWith(_filesTempPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var recordId = record.ContainsKey("id") ? record["id"]?.ToString() : "unknown";
                        var fileName = filePath.Substring(_filesTempPrefix.Length);
                        var permanentKey = $"{entity.Name}/{recordId}/{fileName}";

                        // Copy from temp to permanent location
                        await _s3Client.CopyObjectAsync(
                            _s3BucketName, filePath, _s3BucketName, permanentKey);

                        // Delete temp file
                        await _s3Client.DeleteObjectAsync(_s3BucketName, filePath);

                        // Update record with permanent path
                        record[field.Name] = permanentKey;

                        _logger.LogInformation(
                            "[{CorrelationId}] Moved file from '{TempPath}' to '{PermanentPath}'",
                            correlationId, filePath, permanentKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "[{CorrelationId}] Failed to move file from temp for field '{FieldName}'",
                            correlationId, field.Name);
                        // Continue — don't fail the record operation for file move issues
                    }
                }
            }
        }

        /// <summary>
        /// Cleans up S3 objects referenced by File and Image fields when a record is deleted.
        /// Best-effort — logs warnings but doesn't fail the delete operation.
        /// </summary>
        private async Task CleanupFileFieldsOnDelete(
            Entity entity,
            EntityRecord existingRecord,
            string correlationId)
        {
            if (string.IsNullOrWhiteSpace(_s3BucketName) || entity.Fields == null)
                return;

            foreach (var field in entity.Fields)
            {
                if (field == null) continue;
                var fieldType = field.GetFieldType();

                if (fieldType != FieldType.FileField && fieldType != FieldType.ImageField)
                    continue;

                if (!existingRecord.ContainsKey(field.Name) || existingRecord[field.Name] == null)
                    continue;

                var filePath = existingRecord[field.Name]?.ToString();
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;

                try
                {
                    await _s3Client.DeleteObjectAsync(_s3BucketName, filePath);
                    _logger.LogInformation(
                        "[{CorrelationId}] Deleted S3 file '{FilePath}' for field '{FieldName}'",
                        correlationId, filePath, field.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[{CorrelationId}] Failed to delete S3 file '{FilePath}' for field '{FieldName}'",
                        correlationId, filePath, field.Name);
                    // Continue — file cleanup is best-effort
                }
            }
        }

    } // end class RecordHandler
} // end namespace
