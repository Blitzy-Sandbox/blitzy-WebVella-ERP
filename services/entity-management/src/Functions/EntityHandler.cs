using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;

// Assembly-level serializer registration for AOT-safe System.Text.Json Lambda serialization.
// Ensures all Lambda handler methods (CreateEntity, ReadEntity, etc.) use DefaultLambdaJsonSerializer
// for request/response payload serialization instead of Newtonsoft.Json (which is kept only for
// backward-compatible hash computation).
[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace WebVellaErp.EntityManagement.Functions
{
    /// <summary>
    /// Lambda handler for entity metadata CRUD operations in the Entity Management bounded context.
    /// Replaces the entity metadata endpoints from WebApiController.cs (routes api/v3/en_US/meta/entity/...)
    /// and transforms EntityManager.cs entity lifecycle methods into serverless Lambda function entry points.
    ///
    /// Route mapping (API Gateway HTTP API v2):
    ///   GET    /v1/entity-management/meta/entities           → ReadEntities (query param: hash)
    ///   GET    /v1/entity-management/meta/entities/{idOrName} → ReadEntity
    ///   POST   /v1/entity-management/meta/entities           → CreateEntity
    ///   PUT    /v1/entity-management/meta/entities/{id}      → UpdateEntity
    ///   PATCH  /v1/entity-management/meta/entities/{id}      → PatchEntity
    ///   DELETE /v1/entity-management/meta/entities/{id}      → DeleteEntity
    ///   POST   /v1/entity-management/meta/entities/{id}/clone → CloneEntity
    ///
    /// Authorization: All mutation endpoints require administrator role (JWT claims check).
    /// Events: All mutations publish SNS domain events (entity-management.entity.{action}).
    /// Caching: Read endpoints use IMemoryCache with 1-hour TTL and hash-based change detection.
    /// </summary>
    public class EntityHandler
    {
        // ─── Dependencies (injected via constructor DI) ───────────────────

        private readonly IEntityService _entityService;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly IMemoryCache _cache;
        private readonly ILogger<EntityHandler> _logger;

        // ─── Configuration ────────────────────────────────────────────────

        /// <summary>
        /// SNS topic ARN for entity domain events. Retrieved from ENTITY_TOPIC_ARN environment variable.
        /// When empty/null, event publishing is skipped with a warning log.
        /// </summary>
        private readonly string? _entityTopicArn;

        /// <summary>
        /// When true, detailed exception messages and stack traces are included in error responses.
        /// Replaces ErpSettings.DevelopmentMode from the monolith.
        /// Controlled by IS_LOCAL environment variable.
        /// </summary>
        private readonly bool _isDevelopmentMode;

        // ─── JSON Serialization Options ───────────────────────────────────

        /// <summary>
        /// Shared JsonSerializerOptions for System.Text.Json deserialization/serialization.
        /// PropertyNameCaseInsensitive ensures flexible request body parsing.
        /// </summary>
        private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // ─── Valid PATCH Properties ───────────────────────────────────────

        /// <summary>
        /// Set of property names that can be patched on an entity.
        /// Excludes id and name which are immutable after creation.
        /// Source: WebApiController.cs PatchEntity property validation logic.
        /// </summary>
        private static readonly HashSet<string> ValidPatchProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "label", "labelPlural", "system", "iconName", "color",
            "recordPermissions", "recordScreenIdField"
        };

        // ─── Constructor ──────────────────────────────────────────────────

        /// <summary>
        /// Initializes a new instance of the EntityHandler with all required dependencies.
        /// Dependencies are resolved via the Lambda service provider (configured in Startup/Program).
        /// </summary>
        /// <param name="entityService">Entity metadata CRUD service replacing EntityManager.</param>
        /// <param name="snsClient">SNS client for publishing domain events.</param>
        /// <param name="cache">In-memory cache for entity metadata with 1-hour TTL.</param>
        /// <param name="logger">Structured JSON logger with correlation-ID propagation.</param>
        /// <param name="loggerFactory">Logger factory used to create loggers for delegated handlers (e.g., FieldHandler).</param>
        public EntityHandler(
            IEntityService entityService,
            IAmazonSimpleNotificationService snsClient,
            IMemoryCache cache,
            ILogger<EntityHandler> logger,
            ILoggerFactory loggerFactory)
        {
            _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

            _entityTopicArn = Environment.GetEnvironmentVariable("ENTITY_TOPIC_ARN");
            _isDevelopmentMode = string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"), "true", StringComparison.OrdinalIgnoreCase);
        }

        // ─── Lazy FieldHandler for delegated field sub-path routing ──────

        private readonly ILoggerFactory _loggerFactory;
        private FieldHandler? _fieldHandler;

        /// <summary>
        /// Returns a lazily-initialized FieldHandler instance for processing field-related
        /// requests that arrive at the entity endpoint via the {proxy+} catch-all route.
        /// API Gateway routes POST /v1/meta/entity/{entityId}/fields to this handler's
        /// {proxy+} route; the FunctionHandler detects the /fields sub-path and delegates here.
        /// </summary>
        private FieldHandler GetFieldHandler()
        {
            return _fieldHandler ??= new FieldHandler(
                _entityService, _snsClient, _cache, _loggerFactory.CreateLogger<FieldHandler>());
        }

        // ═════════════════════════════════════════════════════════════════
        // PUBLIC LAMBDA HANDLER METHODS
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lambda handler for POST /v1/entity-management/meta/entities
        /// Creates a new entity definition with default fields (id, audit fields).
        /// Source: EntityManager.CreateEntity() + WebApiController POST api/v3/en_US/meta/entity
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request containing InputEntity in body.</param>
        /// <param name="context">Lambda execution context for logging and request correlation.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with EntityResponse envelope (201 Created or error).</returns>

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
            var proxy = GetParam(request.PathParameters, "proxy");

            // ── Field sub-path delegation ─────────────────────────────────
            // API Gateway {proxy+} on /v1/meta/entity/{proxy+} also captures
            // requests like /v1/meta/entity/{entityId}/fields[/{fieldId}].
            // These must be forwarded to the FieldHandler which owns all
            // field CRUD operations and their SNS event publishing.
            if (!string.IsNullOrEmpty(proxy) && proxy.Contains("fields"))
            {
                _logger.LogInformation(
                    "EntityHandler delegating field sub-path to FieldHandler. Path={Path}, Method={Method}",
                    path, method);
                return await GetFieldHandler().FunctionHandler(request, context);
            }

            // Route clone operations first — path-based regardless of method
            if (path.Contains("/clone"))
                return await CloneEntity(request, context);

            if (method == "POST")
                return await CreateEntity(request, context);
            else if (method == "GET")
            {
                // Determine list vs single read:
                // No proxy segment, or bare path without ID → list all entities
                if (string.IsNullOrEmpty(proxy)
                    || path.EndsWith("/entity") || path.EndsWith("/entity/")
                    || path.EndsWith("/entities") || path.EndsWith("/entities/")
                    || path.EndsWith("/list") || path.EndsWith("/list/"))
                    return await ReadEntities(request, context);
                // Otherwise → single entity read by ID or name
                return await ReadEntity(request, context);
            }
            else if (method == "PUT")
                return await UpdateEntity(request, context);
            else if (method == "PATCH")
                return await PatchEntity(request, context);
            else if (method == "DELETE")
                return await DeleteEntity(request, context);

            // Default: route to ReadEntities for safety
            return await ReadEntities(request, context);
        }

        public async Task<APIGatewayHttpApiV2ProxyResponse> CreateEntity(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "CreateEntity started. CorrelationId={CorrelationId}, FunctionName={FunctionName}",
                correlationId, context.FunctionName);

            try
            {
                // Permission gating: EntityPermission.Create requires admin role.
                // Source: [Authorize(Roles = "administrator")] + SecurityContext.HasMetaPermission()
                if (!HasPermission(request, EntityPermission.Create))
                {
                    _logger.LogWarning(
                        "CreateEntity access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator role required.",
                        correlationId);
                }

                // Parse request body into InputEntity.
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                InputEntity? inputEntity;
                try
                {
                    inputEntity = System.Text.Json.JsonSerializer.Deserialize<InputEntity>(
                        request.Body, _jsonOptions);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "CreateEntity invalid JSON body. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid JSON in request body.",
                        correlationId);
                }

                if (inputEntity == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid entity data in request body.",
                        correlationId);
                }

                // Delegate to EntityService which handles validation, default field generation,
                // DynamoDB persistence, and cache management.
                // Source: EntityManager.CreateEntity() — createOnlyIdField=false creates ALL default fields
                // (id, created_on, created_by, last_modified_on, last_modified_by).
                var response = await _entityService.CreateEntity(
                    inputEntity, createOnlyIdField: false, sysIdDictionary: null);

                // Clear metadata cache after mutation (source: Cache.Clear() after every mutation)
                _entityService.ClearCache();

                // Publish SNS domain event for cross-service communication.
                // Replaces monolith's synchronous HookManager post-hook pattern.
                if (response.Success && response.Object != null)
                {
                    await PublishDomainEvent(
                        "entity-management.entity.created",
                        new { entityId = response.Object.Id, entityName = response.Object.Name },
                        correlationId);
                }

                var statusCode = response.Success
                    ? (int)HttpStatusCode.Created
                    : (int)response.StatusCode;

                _logger.LogInformation(
                    "CreateEntity completed. Success={Success}, EntityId={EntityId}, CorrelationId={CorrelationId}",
                    response.Success, response.Object?.Id, correlationId);

                return BuildResponse(statusCode, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "CreateEntity unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while creating the entity.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/entity-management/meta/entities/{idOrName}
        /// Returns a single entity definition by its GUID or programmatic name.
        /// Source: EntityManager.ReadEntity(Guid) / ReadEntity(string) + WebApiController GET endpoints
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request with idOrName path parameter.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with EntityResponse envelope (200 OK or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ReadEntity(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "ReadEntity started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                // Permission gating: EntityPermission.Read requires admin role.
                // Source: [Authorize(Roles = "administrator")] on all entity meta endpoints.
                if (!HasPermission(request, EntityPermission.Read))
                {
                    _logger.LogWarning(
                        "ReadEntity access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden, "Access denied", correlationId,
                        new List<ErrorModel>
                        {
                            new ErrorModel("authorization", "role",
                                "You are not authorized to access entity metadata. Admin role is required.")
                        });
                }

                // Extract entity identifier from path parameters.
                // Supports both GUID (id-based lookup) and string (name-based lookup).
                var idOrName = GetParam(request.PathParameters, "idOrName");
                if (string.IsNullOrEmpty(idOrName))
                    idOrName = GetParam(request.PathParameters, "id");
                // Fallback: extract from {proxy+} path parameter used by API Gateway HTTP API v2.
                // Proxy path may be "{id}", "id/{id}", or "{name}".
                if (string.IsNullOrEmpty(idOrName))
                {
                    var proxy = GetParam(request.PathParameters, "proxy");
                    if (!string.IsNullOrEmpty(proxy))
                    {
                        var segments = proxy.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (segments.Length >= 2 && segments[0] == "id")
                            idOrName = segments[1]; // "id/{uuid}" pattern
                        else if (segments.Length >= 1 && segments[0] != "list" && segments[0] != "entities")
                            idOrName = segments[0]; // "{name}" or "{uuid}" pattern
                    }
                }

                if (string.IsNullOrWhiteSpace(idOrName))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Entity identifier (id or name) is required.",
                        correlationId);
                }

                EntityResponse response;

                // Determine if the identifier is a GUID or entity name.
                // Source: WebApiController had separate routes for id/{entityId} and {Name}.
                // Target: Single route with dynamic dispatch based on format.
                if (Guid.TryParse(idOrName, out var entityId))
                {
                    response = await _entityService.ReadEntity(entityId);
                }
                else
                {
                    response = await _entityService.ReadEntity(idOrName);
                }

                if (!response.Success || response.Object == null)
                {
                    var statusCode = response.Object == null && response.Success
                        ? (int)HttpStatusCode.NotFound
                        : (int)response.StatusCode;

                    if (response.Object == null && response.Errors.Count == 0)
                    {
                        response.Success = false;
                        response.Message = $"Entity '{idOrName}' not found.";
                        statusCode = (int)HttpStatusCode.NotFound;
                    }

                    return BuildResponse(statusCode, response, correlationId);
                }

                _logger.LogInformation(
                    "ReadEntity completed. EntityId={EntityId}, EntityName={EntityName}, CorrelationId={CorrelationId}",
                    response.Object.Id, response.Object.Name, correlationId);

                return BuildResponse((int)HttpStatusCode.OK, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ReadEntity unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while reading the entity.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/entity-management/meta/entities
        /// Returns all entity definitions with hash-based cache optimization.
        /// Source: EntityManager.ReadEntities() + WebApiController GET api/v3/en_US/meta/entity/list
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request with optional hash query parameter.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with EntityListResponse envelope (200 OK).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ReadEntities(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "ReadEntities started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                // Permission gating: EntityPermission.Read requires admin role.
                // Source: [Authorize(Roles = "administrator")] on all entity meta endpoints.
                if (!HasPermission(request, EntityPermission.Read))
                {
                    _logger.LogWarning(
                        "ReadEntities access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden, "Access denied", correlationId,
                        new List<ErrorModel>
                        {
                            new ErrorModel("authorization", "role",
                                "You are not authorized to access entity metadata. Admin role is required.")
                        });
                }

                // Extract optional hash query parameter for cache optimization.
                // If the client-provided hash matches the server hash, return null object
                // to indicate no changes — saves bandwidth for large entity lists.
                // Source: WebApiController GET api/v3/en_US/meta/entity/list with hash param
                var clientHash = GetParamOrNull(request.QueryStringParameters, "hash");

                // Check if we can short-circuit with hash comparison (cache-warm path).
                // Source: EntityManager.ReadEntities() populated Cache.GetEntitiesHash()
                if (!string.IsNullOrEmpty(clientHash))
                {
                    var serverHash = _entityService.GetEntitiesHash();
                    if (!string.IsNullOrEmpty(serverHash) &&
                        string.Equals(serverHash, clientHash, StringComparison.Ordinal))
                    {
                        _logger.LogInformation(
                            "ReadEntities hash match — returning null object. Hash={Hash}, CorrelationId={CorrelationId}",
                            clientHash, correlationId);

                        var cachedResponse = new EntityListResponse
                        {
                            Success = true,
                            Timestamp = DateTime.UtcNow,
                            Object = null,
                            Hash = serverHash,
                            Message = "Hash match — no changes."
                        };
                        return BuildResponse((int)HttpStatusCode.OK, cachedResponse, correlationId);
                    }
                }

                // Full fetch: reads from cache (1-hour TTL) or DynamoDB on cache miss.
                // Source: EntityManager.ReadEntities() with Cache.GetEntities() / Cache.AddEntities()
                var response = await _entityService.ReadEntities();

                // Apply hash comparison if client hash provided but didn't match cached hash
                // (cache may have been repopulated during ReadEntities call).
                if (!string.IsNullOrEmpty(clientHash) && response.Success &&
                    string.Equals(response.Hash, clientHash, StringComparison.Ordinal))
                {
                    response.Object = null;
                    response.Message = "Hash match — no changes.";
                }

                _logger.LogInformation(
                    "ReadEntities completed. EntityCount={Count}, Hash={Hash}, CorrelationId={CorrelationId}",
                    response.Object?.Count ?? 0, response.Hash, correlationId);

                return BuildResponse(
                    response.Success ? (int)HttpStatusCode.OK : (int)response.StatusCode,
                    response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ReadEntities unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while reading entities.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for PUT /v1/entity-management/meta/entities/{id}
        /// Performs a full update of an existing entity definition.
        /// Source: EntityManager.UpdateEntity() + WebApiController PUT api/v3/en_US/meta/entity/{id}
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request with entity id path parameter and InputEntity in body.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with EntityResponse envelope (200 OK or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> UpdateEntity(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "UpdateEntity started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                // Permission gating: EntityPermission.Update requires admin role.
                if (!HasPermission(request, EntityPermission.Update))
                {
                    _logger.LogWarning(
                        "UpdateEntity access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator role required.",
                        correlationId);
                }

                // Extract and validate entity ID from path parameters.
                var idStr = ExtractIdFromPathOrProxy(request.PathParameters, "id", "idOrName");
                if (!Guid.TryParse(idStr, out var entityId) || entityId == Guid.Empty)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Invalid entity ID format: '{idStr}'.",
                        correlationId,
                        new List<ErrorModel>
                        {
                            new ErrorModel("id", idStr, "Entity ID must be a valid non-empty GUID.")
                        });
                }

                // Parse request body.
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                InputEntity? inputEntity;
                try
                {
                    inputEntity = System.Text.Json.JsonSerializer.Deserialize<InputEntity>(
                        request.Body, _jsonOptions);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "UpdateEntity invalid JSON body. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid JSON in request body.",
                        correlationId);
                }

                if (inputEntity == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid entity data in request body.",
                        correlationId);
                }

                // Ensure the entity ID from the path matches or is set on the input.
                // Source: WebApiController assigned entity ID from path to input.
                inputEntity.Id = entityId;

                // Delegate to EntityService which handles validation (checkId=true), persistence, etc.
                // Source: EntityManager.UpdateEntity() with ValidateEntity(checkId=true)
                var response = await _entityService.UpdateEntity(inputEntity);

                // Clear metadata cache after mutation.
                _entityService.ClearCache();

                // Publish SNS domain event.
                if (response.Success && response.Object != null)
                {
                    await PublishDomainEvent(
                        "entity-management.entity.updated",
                        new { entityId = response.Object.Id, entityName = response.Object.Name },
                        correlationId);
                }

                _logger.LogInformation(
                    "UpdateEntity completed. Success={Success}, EntityId={EntityId}, CorrelationId={CorrelationId}",
                    response.Success, entityId, correlationId);

                return BuildResponse(
                    response.Success ? (int)HttpStatusCode.OK : (int)response.StatusCode,
                    response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UpdateEntity unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while updating the entity.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for PATCH /v1/entity-management/meta/entities/{id}
        /// Performs a partial update: reads existing entity, applies only the submitted properties,
        /// validates that all submitted property names exist on InputEntity type, and delegates
        /// the merged entity to IEntityService.UpdateEntity().
        /// Source: WebApiController PATCH api/v3/en_US/meta/entity/{StringId}
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request with entity id path parameter and partial JSON in body.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with EntityResponse envelope (200 OK or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> PatchEntity(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "PatchEntity started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                // Permission gating: EntityPermission.Update required for PATCH.
                if (!HasPermission(request, EntityPermission.Update))
                {
                    _logger.LogWarning(
                        "PatchEntity access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator role required.",
                        correlationId);
                }

                // Extract and validate entity ID from path parameters.
                var idStr = ExtractIdFromPathOrProxy(request.PathParameters, "id", "idOrName");
                if (!Guid.TryParse(idStr, out var entityId) || entityId == Guid.Empty)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Invalid entity ID format: '{idStr}'.",
                        correlationId,
                        new List<ErrorModel>
                        {
                            new ErrorModel("id", idStr, "Entity ID must be a valid non-empty GUID.")
                        });
                }

                // Parse request body.
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required for PATCH.",
                        correlationId);
                }

                // Read existing entity from service to build base InputEntity.
                // Source: WebApiController reads existing entity then maps to InputEntity before patching.
                var existingResponse = await _entityService.ReadEntity(entityId);
                if (!existingResponse.Success || existingResponse.Object == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"Entity with ID '{entityId}' not found.",
                        correlationId);
                }

                var existing = existingResponse.Object;

                // Map existing entity to InputEntity (base for patch application).
                var inputEntity = new InputEntity
                {
                    Id = existing.Id,
                    Name = existing.Name,
                    Label = existing.Label,
                    LabelPlural = existing.LabelPlural,
                    System = existing.System,
                    IconName = existing.IconName,
                    Color = existing.Color,
                    RecordPermissions = existing.RecordPermissions,
                    RecordScreenIdField = existing.RecordScreenIdField
                };

                // Parse patch body as JsonDocument for property-by-property application.
                JsonDocument patchDoc;
                try
                {
                    patchDoc = JsonDocument.Parse(request.Body);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "PatchEntity invalid JSON body. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid JSON in request body.",
                        correlationId);
                }

                using (patchDoc)
                {
                    // Phase 1: Validate all submitted property names exist on InputEntity.
                    // Source: WebApiController iterated JObject.Properties() and checked via reflection.
                    var errors = new List<ErrorModel>();
                    foreach (var prop in patchDoc.RootElement.EnumerateObject())
                    {
                        // Skip id and name — they are immutable after creation.
                        if (string.Equals(prop.Name, "id", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(prop.Name, "name", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!ValidPatchProperties.Contains(prop.Name))
                        {
                            errors.Add(new ErrorModel(
                                "property",
                                prop.Name,
                                $"Property '{prop.Name}' is not a valid patchable entity property. " +
                                $"Valid properties: {string.Join(", ", ValidPatchProperties)}."));
                        }
                    }

                    if (errors.Count > 0)
                    {
                        return BuildErrorResponse(
                            (int)HttpStatusCode.BadRequest,
                            "One or more invalid property names in PATCH body.",
                            correlationId,
                            errors);
                    }

                    // Phase 2: Apply only submitted properties to InputEntity.
                    // Source: WebApiController applied each property via switch on lowercase name.
                    foreach (var prop in patchDoc.RootElement.EnumerateObject())
                    {
                        var propNameLower = prop.Name.ToLowerInvariant();
                        switch (propNameLower)
                        {
                            case "label":
                                inputEntity.Label = prop.Value.ValueKind == JsonValueKind.Null
                                    ? null!
                                    : prop.Value.GetString()!;
                                break;

                            case "labelplural":
                                inputEntity.LabelPlural = prop.Value.ValueKind == JsonValueKind.Null
                                    ? null!
                                    : prop.Value.GetString()!;
                                break;

                            case "system":
                                if (prop.Value.ValueKind == JsonValueKind.True ||
                                    prop.Value.ValueKind == JsonValueKind.False)
                                {
                                    inputEntity.System = prop.Value.GetBoolean();
                                }
                                break;

                            case "iconname":
                                inputEntity.IconName = prop.Value.ValueKind == JsonValueKind.Null
                                    ? null!
                                    : prop.Value.GetString()!;
                                break;

                            case "color":
                                inputEntity.Color = prop.Value.ValueKind == JsonValueKind.Null
                                    ? null!
                                    : prop.Value.GetString()!;
                                break;

                            case "recordpermissions":
                                if (prop.Value.ValueKind == JsonValueKind.Null)
                                {
                                    inputEntity.RecordPermissions = null!;
                                }
                                else
                                {
                                    inputEntity.RecordPermissions =
                                        System.Text.Json.JsonSerializer.Deserialize<RecordPermissions>(
                                            prop.Value.GetRawText(), _jsonOptions)!;
                                }
                                break;

                            case "recordscreenidfield":
                                if (prop.Value.ValueKind == JsonValueKind.Null)
                                {
                                    inputEntity.RecordScreenIdField = null;
                                }
                                else
                                {
                                    var screenFieldStr = prop.Value.GetString();
                                    if (Guid.TryParse(screenFieldStr, out var screenFieldId))
                                    {
                                        inputEntity.RecordScreenIdField = screenFieldId;
                                    }
                                }
                                break;

                            // id and name are silently ignored (immutable).
                        }
                    }
                }

                // Delegate to EntityService.UpdateEntity() with the merged InputEntity.
                var response = await _entityService.UpdateEntity(inputEntity);

                // Clear metadata cache after mutation.
                _entityService.ClearCache();

                // Publish SNS domain event.
                if (response.Success && response.Object != null)
                {
                    await PublishDomainEvent(
                        "entity-management.entity.updated",
                        new { entityId = response.Object.Id, entityName = response.Object.Name },
                        correlationId);
                }

                _logger.LogInformation(
                    "PatchEntity completed. Success={Success}, EntityId={EntityId}, CorrelationId={CorrelationId}",
                    response.Success, entityId, correlationId);

                return BuildResponse(
                    response.Success ? (int)HttpStatusCode.OK : (int)response.StatusCode,
                    response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PatchEntity unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while patching the entity.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for DELETE /v1/entity-management/meta/entities/{id}
        /// Deletes an entity definition and all associated field and relation metadata.
        /// Source: EntityManager.DeleteEntity() + WebApiController DELETE api/v3/en_US/meta/entity/{StringId}
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request with entity id path parameter.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with EntityResponse envelope (200 OK or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> DeleteEntity(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "DeleteEntity started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                // Permission gating: EntityPermission.Delete requires admin role.
                if (!HasPermission(request, EntityPermission.Delete))
                {
                    _logger.LogWarning(
                        "DeleteEntity access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator role required.",
                        correlationId);
                }

                // Extract and validate entity ID from path parameters.
                var idStr = ExtractIdFromPathOrProxy(request.PathParameters, "id", "idOrName");
                if (!Guid.TryParse(idStr, out var entityId) || entityId == Guid.Empty)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Invalid entity ID format: '{idStr}'.",
                        correlationId,
                        new List<ErrorModel>
                        {
                            new ErrorModel("id", idStr, "Entity ID must be a valid non-empty GUID.")
                        });
                }

                // Capture entity name before deletion for event publishing.
                string? entityName = null;
                var readResponse = await _entityService.ReadEntity(entityId);
                if (readResponse.Success && readResponse.Object != null)
                {
                    entityName = readResponse.Object.Name;
                }

                // Delegate to EntityService which validates existence and deletes from DynamoDB.
                // Source: EntityManager.DeleteEntity() reads entity first, then deletes.
                var response = await _entityService.DeleteEntity(entityId);

                // Clear metadata cache after mutation.
                _entityService.ClearCache();

                // Publish SNS domain event.
                if (response.Success)
                {
                    await PublishDomainEvent(
                        "entity-management.entity.deleted",
                        new { entityId, entityName },
                        correlationId);
                }

                _logger.LogInformation(
                    "DeleteEntity completed. Success={Success}, EntityId={EntityId}, EntityName={EntityName}, CorrelationId={CorrelationId}",
                    response.Success, entityId, entityName, correlationId);

                return BuildResponse(
                    response.Success ? (int)HttpStatusCode.OK : (int)response.StatusCode,
                    response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DeleteEntity unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while deleting the entity.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/entity-management/meta/entities/{id}/clone
        /// Clones an existing entity's metadata (label, icon, color, permissions) into a new entity
        /// with a new name. Default system fields (id, audit fields) are generated for the clone.
        /// Source: EntityManager — clone capability derived from CreateEntity + ReadEntity patterns.
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request with source entity id path parameter
        /// and optional body containing { "name": "...", "label": "..." } overrides.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with EntityResponse envelope (201 Created or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> CloneEntity(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "CloneEntity started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                // Permission gating: EntityPermission.Create required for clone.
                if (!HasPermission(request, EntityPermission.Create))
                {
                    _logger.LogWarning(
                        "CloneEntity access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator role required.",
                        correlationId);
                }

                // Extract and validate source entity ID from path parameters.
                var idStr = ExtractIdFromPathOrProxy(request.PathParameters, "id", "idOrName");
                if (!Guid.TryParse(idStr, out var sourceEntityId) || sourceEntityId == Guid.Empty)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Invalid source entity ID format: '{idStr}'.",
                        correlationId,
                        new List<ErrorModel>
                        {
                            new ErrorModel("id", idStr, "Source entity ID must be a valid non-empty GUID.")
                        });
                }

                // Read the source entity to copy its metadata.
                var sourceResponse = await _entityService.ReadEntity(sourceEntityId);
                if (!sourceResponse.Success || sourceResponse.Object == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"Source entity with ID '{sourceEntityId}' not found.",
                        correlationId);
                }

                var source = sourceResponse.Object;

                // Parse optional body overrides for clone name/label.
                string? cloneName = null;
                string? cloneLabel = null;
                string? cloneLabelPlural = null;

                if (!string.IsNullOrWhiteSpace(request.Body))
                {
                    try
                    {
                        using var overrideDoc = JsonDocument.Parse(request.Body);
                        var root = overrideDoc.RootElement;

                        if (root.TryGetProperty("name", out var nameProp) &&
                            nameProp.ValueKind == JsonValueKind.String)
                        {
                            cloneName = nameProp.GetString();
                        }

                        if (root.TryGetProperty("label", out var labelProp) &&
                            labelProp.ValueKind == JsonValueKind.String)
                        {
                            cloneLabel = labelProp.GetString();
                        }

                        if (root.TryGetProperty("labelPlural", out var labelPluralProp) &&
                            labelPluralProp.ValueKind == JsonValueKind.String)
                        {
                            cloneLabelPlural = labelPluralProp.GetString();
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // Non-critical — use defaults for clone name.
                    }
                }

                // Generate clone name if not provided.
                cloneName ??= $"{source.Name}_copy";
                cloneLabel ??= $"{source.Label} (Copy)";
                cloneLabelPlural ??= $"{source.LabelPlural} (Copy)";

                // Build InputEntity from source entity metadata with new identity.
                var cloneInput = new InputEntity
                {
                    Id = Guid.NewGuid(),
                    Name = cloneName,
                    Label = cloneLabel,
                    LabelPlural = cloneLabelPlural,
                    System = false, // Cloned entities are never system entities
                    IconName = source.IconName,
                    Color = source.Color,
                    RecordPermissions = source.RecordPermissions != null
                        ? new RecordPermissions
                        {
                            CanRead = new List<Guid>(source.RecordPermissions.CanRead ?? new List<Guid>()),
                            CanCreate = new List<Guid>(source.RecordPermissions.CanCreate ?? new List<Guid>()),
                            CanUpdate = new List<Guid>(source.RecordPermissions.CanUpdate ?? new List<Guid>()),
                            CanDelete = new List<Guid>(source.RecordPermissions.CanDelete ?? new List<Guid>())
                        }
                        : null!,
                    RecordScreenIdField = source.RecordScreenIdField
                };

                // Create the cloned entity with default fields (id, audit fields).
                // Source: EntityManager.CreateEntity() — createOnlyIdField=false creates ALL default fields.
                var response = await _entityService.CreateEntity(
                    cloneInput, createOnlyIdField: false, sysIdDictionary: null);

                // Clear metadata cache after mutation.
                _entityService.ClearCache();

                // Publish SNS domain event for the clone operation.
                if (response.Success && response.Object != null)
                {
                    await PublishDomainEvent(
                        "entity-management.entity.created",
                        new
                        {
                            entityId = response.Object.Id,
                            entityName = response.Object.Name,
                            clonedFrom = sourceEntityId
                        },
                        correlationId);
                }

                var statusCode = response.Success
                    ? (int)HttpStatusCode.Created
                    : (int)response.StatusCode;

                _logger.LogInformation(
                    "CloneEntity completed. Success={Success}, SourceEntityId={SourceEntityId}, CloneEntityId={CloneEntityId}, CorrelationId={CorrelationId}",
                    response.Success, sourceEntityId, response.Object?.Id, correlationId);

                return BuildResponse(statusCode, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "CloneEntity unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while cloning the entity.",
                    correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // PRIVATE HELPER METHODS
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Safely retrieves a value from an IDictionary{string, string}.
        /// IDictionary does not have GetValueOrDefault (which is only on IReadOnlyDictionary),
        /// so this helper wraps TryGetValue for safe extraction from Lambda request parameters.
        /// </summary>
        private static string GetParam(IDictionary<string, string>? parameters, string key)
        {
            if (parameters != null && parameters.TryGetValue(key, out var value))
                return value ?? string.Empty;
            return string.Empty;
        }

        /// <summary>
        /// Extracts an entity/field/relation ID from API Gateway path parameters.
        /// Supports both named path parameters (e.g. {id}) and the {proxy+}
        /// catch-all used by HTTP API v2 routes like /v1/meta/entity/{proxy+}.
        /// Proxy paths follow these patterns:
        ///   "{uuid}"                   → entity ID directly
        ///   "id/{uuid}"               → legacy /id/{entityId} pattern
        ///   "{uuid}/fields"           → entity ID for field listing
        ///   "{uuid}/fields/{fieldId}" → field ID is last segment
        /// </summary>
        private static string ExtractIdFromPathOrProxy(
            IDictionary<string, string>? parameters, params string[] namedKeys)
        {
            if (parameters == null) return string.Empty;
            // 1. Try named parameters first (works when API GW uses {id} or {idOrName})
            foreach (var key in namedKeys)
            {
                if (parameters.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
                    return val;
            }
            // 2. Fall back to {proxy+} path parameter
            if (!parameters.TryGetValue("proxy", out var proxy) || string.IsNullOrEmpty(proxy))
                return string.Empty;
            var segments = proxy.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return string.Empty;
            // "id/{uuid}" pattern → return uuid
            if (segments.Length >= 2 && segments[0] == "id")
                return segments[1];
            // Single segment → could be a GUID or entity name
            if (segments.Length == 1) return segments[0];
            // Multi-segment: return first segment as the primary ID
            // (e.g. "{entityId}/fields/{fieldId}" → return entityId)
            return segments[0];
        }

        /// <summary>
        /// Safely retrieves a nullable value from an IDictionary{string, string}.
        /// Returns null if the key is not present (used for optional query parameters like 'hash').
        /// </summary>
        private static string? GetParamOrNull(IDictionary<string, string>? parameters, string key)
        {
            if (parameters != null && parameters.TryGetValue(key, out var value))
                return value;
            return null;
        }

        /// <summary>
        /// Checks whether the requesting user has the specified entity metadata permission.
        /// For entity metadata CRUD operations, all permissions (Read, Create, Update, Delete)
        /// require administrator role — matching the source [Authorize(Roles = "administrator")]
        /// attribute on all entity meta endpoints in WebApiController.cs.
        /// Uses SystemIds.AdministratorRoleId for admin GUID comparison.
        /// </summary>
        private bool HasPermission(APIGatewayHttpApiV2ProxyRequest request, EntityPermission permission)
        {
            _logger.LogDebug(
                "Permission check: EntityPermission.{Permission} requires admin role (SystemIds.AdministratorRoleId={AdminRoleId})",
                permission, SystemIds.AdministratorRoleId);
            return IsAdminUser(request);
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
                // Cognito JWT tokens include group membership in cognito:groups claim.
                var claims = request.RequestContext?.Authorizer?.Jwt?.Claims;
                if (claims != null)
                {
                    // Check Cognito groups claim for admin role.
                    if (claims.TryGetValue("cognito:groups", out var groups) &&
                        !string.IsNullOrEmpty(groups))
                    {
                        if (groups.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
                            groups.Contains(adminRoleIdStr, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    // Check custom roles claim for backward compatibility.
                    if (claims.TryGetValue("custom:roles", out var roles) &&
                        !string.IsNullOrEmpty(roles))
                    {
                        if (roles.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
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
                // The custom authorizer enriches the context with roles/isAdmin fields.
                var lambdaAuth = request.RequestContext?.Authorizer?.Lambda;
                if (lambdaAuth != null)
                {
                    // Check isAdmin flag from custom authorizer.
                    // API Gateway may deliver boolean values as JsonElement, string, or bool,
                    // so we handle all representations robustly via ToString().
                    if (lambdaAuth.TryGetValue("isAdmin", out var isAdminObj) && isAdminObj != null)
                    {
                        var isAdminStr = isAdminObj.ToString() ?? "";
                        if (string.Equals(isAdminStr, "true", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(isAdminStr, "True", StringComparison.Ordinal))
                            return true;
                    }

                    // Check roles string from custom authorizer.
                    // Cognito group "admin" maps to administrator role.
                    if (lambdaAuth.TryGetValue("roles", out var rolesObj) && rolesObj != null)
                    {
                        var rolesStr = rolesObj.ToString() ?? "";
                        if (rolesStr.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                            rolesStr.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
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
        /// Extracts or generates a correlation ID for request tracing across services.
        /// Checks X-Correlation-Id header first, falls back to Lambda AwsRequestId.
        /// Per AAP operational requirements: structured JSON logging with correlation-ID propagation.
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
        /// <param name="statusCode">HTTP status code (200, 201, 400, 403, 404, 500).</param>
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
        /// (e.g., entity-management.entity.created, entity-management.entity.updated)
        ///
        /// Uses Newtonsoft.Json (JsonConvert.SerializeObject) for event payload serialization
        /// to maintain backward compatibility with event consumers expecting Newtonsoft-formatted JSON.
        /// </summary>
        /// <param name="action">Domain event type string (e.g., "entity-management.entity.created").</param>
        /// <param name="eventData">Event payload data to include in the SNS message.</param>
        /// <param name="correlationId">Correlation ID for distributed tracing across services.</param>
        private async Task PublishDomainEvent(string action, object eventData, string correlationId)
        {
            if (string.IsNullOrEmpty(_entityTopicArn))
            {
                _logger.LogWarning(
                    "Entity SNS topic ARN not configured (ENTITY_TOPIC_ARN env var). " +
                    "Skipping event publish for {Action}. CorrelationId={CorrelationId}",
                    action, correlationId);
                return;
            }

            try
            {
                // Use Newtonsoft.Json for event serialization for backward compatibility
                // with consumers that rely on Newtonsoft serialization format.
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
                    TopicArn = _entityTopicArn,
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
    }
}
