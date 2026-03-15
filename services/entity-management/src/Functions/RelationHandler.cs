// =============================================================================
// RelationHandler.cs — Relation Metadata CRUD Lambda Handler
// =============================================================================
// Replaces monolith: WebVella.Erp/Api/EntityRelationManager.cs (~568 lines)
//                    + WebApiController.cs relation endpoints (lines 2005-2104)
//
// Lambda handler for entity relation metadata CRUD operations in the Entity
// Management bounded context. Manages 1:1, 1:N, and N:N relation definitions
// with immutability enforcement, GUID field requirements, and domain event
// publishing via SNS.
//
// API Route Mapping:
//   GET    /v1/entity-management/meta/relations           → ReadRelations
//   GET    /v1/entity-management/meta/relations/{idOrName} → ReadRelation
//   POST   /v1/entity-management/meta/relations           → CreateRelation
//   PUT    /v1/entity-management/meta/relations/{id}      → UpdateRelation
//   DELETE /v1/entity-management/meta/relations/{id}      → DeleteRelation
//
// Namespace Migration:
//   Old: WebVella.Erp.Api (EntityRelationManager)
//   New: WebVellaErp.EntityManagement.Functions
//
// Architecture Migration:
//   Old: DbContext.Current.RelationRepository + SecurityContext.HasMetaPermission()
//   New: Injected IEntityService + JWT claims admin role check
//   Old: Cache.GetRelations() / Cache.Clear()
//   New: IEntityService.ReadRelations() (cache-aware) + IEntityService.ClearCache()
//   Old: ErpSettings.DevelopmentMode
//   New: IS_LOCAL environment variable
//   Old: HookManager post-hooks
//   New: SNS domain events (entity-management.relation.{created|updated|deleted})
// =============================================================================

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

// Note: [assembly: LambdaSerializer] attribute is defined in EntityHandler.cs
// and applies to the entire assembly. It must not be duplicated here.

namespace WebVellaErp.EntityManagement.Functions
{
    /// <summary>
    /// Lambda handler class for entity relation metadata CRUD operations.
    /// Provides five handler methods mapped to HTTP API Gateway v2 routes:
    ///   - CreateRelation  (POST)
    ///   - ReadRelation    (GET by id or name)
    ///   - ReadRelations   (GET all with hash-based cache optimization)
    ///   - UpdateRelation  (PUT with immutability enforcement)
    ///   - DeleteRelation  (DELETE)
    ///
    /// Follows the same pattern as EntityHandler.cs for consistency across
    /// the Entity Management bounded context Lambda handlers.
    /// </summary>
    public class RelationHandler
    {
        // ─── Dependencies ─────────────────────────────────────────────────

        private readonly IEntityService _entityService;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly IMemoryCache _cache;
        private readonly ILogger<RelationHandler> _logger;

        /// <summary>
        /// SNS topic ARN for relation domain events, read from RELATION_TOPIC_ARN
        /// environment variable. If not set, domain events are silently skipped.
        /// </summary>
        private readonly string? _relationTopicArn;

        /// <summary>
        /// Development mode flag read from IS_LOCAL environment variable.
        /// When true, error responses include full exception details (message + stack trace).
        /// When false, error responses return a generic "Internal server error" message.
        /// </summary>
        private readonly bool _isDevelopmentMode;

        /// <summary>
        /// Shared JSON serializer options for response body serialization.
        /// PropertyNameCaseInsensitive enables flexible deserialization.
        /// WhenWritingNull omits null properties from responses for cleaner payloads.
        /// </summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Maximum length for relation names. Preserves the PostgreSQL identifier
        /// limit from the monolith for consistency even though DynamoDB has no such limit.
        /// Source: EntityRelationManager.ValidateRelation() — name.Length > 63
        /// </summary>
        private const int MaxNameLength = 63;

        // ─── Constructor ──────────────────────────────────────────────────

        /// <summary>
        /// Constructs the RelationHandler with all required dependencies injected via DI.
        /// All parameters are required; null values throw ArgumentNullException.
        /// </summary>
        /// <param name="entityService">Entity/relation metadata service for CRUD operations.</param>
        /// <param name="snsClient">SNS client for publishing domain events after mutations.</param>
        /// <param name="cache">In-memory cache for relation metadata (1-hour TTL pattern).</param>
        /// <param name="logger">Structured logger for correlation-ID-aware JSON logging.</param>
        public RelationHandler(
            IEntityService entityService,
            IAmazonSimpleNotificationService snsClient,
            IMemoryCache cache,
            ILogger<RelationHandler> logger)
        {
            _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _relationTopicArn = Environment.GetEnvironmentVariable("RELATION_TOPIC_ARN");
            _isDevelopmentMode = string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================================
        // CREATE RELATION
        // =====================================================================

        /// <summary>
        /// Lambda handler for creating a new entity relation definition.
        /// Route: POST /v1/entity-management/meta/relations
        ///
        /// Validates:
        ///   - Admin permission via JWT claims
        ///   - Name length (max 63 chars), format, and uniqueness
        ///   - Origin and target entities exist
        ///   - Origin and target fields exist and are GuidField type
        ///   - Relation-type specific constraints (Required/Unique on fields)
        ///   - No duplicate origin+target entity/field combinations
        ///   - Auto-generates Id if empty/default
        ///
        /// On success: persists to DynamoDB, clears cache, publishes SNS event.
        /// Source: EntityRelationManager.Create() + WebApiController.CreateEntityRelation()
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

            if (method == "POST")
                return await CreateRelation(request, context);
            else if (method == "GET")
                return await ReadRelation(request, context);
            else if (method == "GET")
                return await ReadRelations(request, context);
            else if (method == "PUT")
                return await UpdateRelation(request, context);
            else if (method == "DELETE")
                return await DeleteRelation(request, context);

            // Default: route to CreateRelation
            return await CreateRelation(request, context);
        }

        public async Task<APIGatewayHttpApiV2ProxyResponse> CreateRelation(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] CreateRelation — request received", correlationId);

            try
            {
                // ─── Permission Check ─────────────────────────────────
                if (!HasPermission(request, EntityPermission.Create))
                {
                    _logger.LogWarning("[{CorrelationId}] CreateRelation — access denied", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Admin permission required.",
                        correlationId);
                }

                // ─── Parse Request Body ───────────────────────────────
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                EntityRelation? relation;
                try
                {
                    relation = System.Text.Json.JsonSerializer.Deserialize<EntityRelation>(request.Body, _jsonOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[{CorrelationId}] CreateRelation — invalid JSON: {Error}", correlationId, ex.Message);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid JSON in request body.",
                        correlationId);
                }

                if (relation == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Failed to parse relation from request body.",
                        correlationId);
                }

                // ─── Auto-generate Id if empty ────────────────────────
                // Source: WebApiController.CreateEntityRelation() — auto-generate Id if null/empty
                if (relation.Id == Guid.Empty)
                {
                    relation.Id = Guid.NewGuid();
                }

                // ─── Trim name ────────────────────────────────────────
                // Source: EntityRelationManager.Create() — relation.Name = relation.Name.Trim();
                if (!string.IsNullOrEmpty(relation.Name))
                {
                    relation.Name = relation.Name.Trim();
                }

                // ─── Validation ───────────────────────────────────────
                var errors = await ValidateRelationForCreate(relation, correlationId);
                if (errors.Count > 0)
                {
                    _logger.LogWarning("[{CorrelationId}] CreateRelation — validation failed with {Count} errors",
                        correlationId, errors.Count);
                    var errorResponse = new EntityRelationResponse
                    {
                        Success = false,
                        Timestamp = DateTime.UtcNow,
                        Message = "The entity relation was not created. Validation error occurred!",
                        Errors = errors,
                        Object = relation
                    };
                    return BuildResponse((int)HttpStatusCode.BadRequest, errorResponse, correlationId);
                }

                // ─── Persist via EntityService ────────────────────────
                var result = await _entityService.CreateRelation(relation);

                if (!result.Success)
                {
                    _logger.LogWarning("[{CorrelationId}] CreateRelation — service returned failure: {Message}",
                        correlationId, result.Message);
                    return BuildResponse((int)result.StatusCode, result, correlationId);
                }

                // ─── Clear metadata cache ─────────────────────────────
                // Source: EntityRelationManager — Cache.Clear() after every mutation
                _entityService.ClearCache();

                // ─── Publish domain event ─────────────────────────────
                await PublishDomainEvent(
                    "entity-management.relation.created",
                    new { RelationId = relation.Id, RelationName = relation.Name, RelationType = relation.RelationType.ToString() },
                    correlationId);

                _logger.LogInformation("[{CorrelationId}] CreateRelation — relation '{Name}' created successfully (Id={Id})",
                    correlationId, relation.Name, relation.Id);

                result.Timestamp = DateTime.UtcNow;
                result.Success = true;
                result.Message = "The entity relation was successfully created!";
                return BuildResponse((int)HttpStatusCode.Created, result, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] CreateRelation — unhandled exception", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "Internal server error.",
                    correlationId);
            }
        }

        // =====================================================================
        // READ RELATION (by id or name)
        // =====================================================================

        /// <summary>
        /// Lambda handler for reading a single entity relation by ID or name.
        /// Route: GET /v1/entity-management/meta/relations/{idOrName}
        ///
        /// Supports both GUID-based and name-based lookups, dispatching to
        /// the appropriate IEntityService.ReadRelation() overload.
        /// Returns enriched relation with OriginEntityName, TargetEntityName,
        /// OriginFieldName, TargetFieldName populated.
        ///
        /// Source: WebApiController.GetEntityRelationMeta(string name)
        ///         + EntityRelationManager.Read(string) / Read(Guid)
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ReadRelation(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] ReadRelation — request received", correlationId);

            try
            {
                var idOrName = GetParam(request.PathParameters, "idOrName");
                if (string.IsNullOrWhiteSpace(idOrName))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Relation identifier (id or name) is required.",
                        correlationId);
                }

                // Dispatch based on whether the parameter is a GUID or a name
                EntityRelationResponse result;
                if (Guid.TryParse(idOrName, out var relationId))
                {
                    result = await _entityService.ReadRelation(relationId);
                }
                else
                {
                    result = await _entityService.ReadRelation(idOrName);
                }

                if (result.Object == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"Relation '{idOrName}' not found.",
                        correlationId);
                }

                _logger.LogInformation("[{CorrelationId}] ReadRelation — returning relation '{Name}'",
                    correlationId, result.Object.Name);

                result.Timestamp = DateTime.UtcNow;
                result.Success = true;
                return BuildResponse((int)HttpStatusCode.OK, result, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] ReadRelation — unhandled exception", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "Internal server error.",
                    correlationId);
            }
        }

        // =====================================================================
        // READ RELATIONS (list all)
        // =====================================================================

        /// <summary>
        /// Lambda handler for reading all entity relation definitions.
        /// Route: GET /v1/entity-management/meta/relations
        ///
        /// Supports hash-based conditional fetch: if the client provides a 'hash'
        /// query parameter matching the current relations hash, returns a null
        /// Object with a "Hash match" message (304-like optimization to avoid
        /// transferring unchanged data).
        ///
        /// Source: WebApiController.GetEntityRelationMetaList(string hash)
        ///         + EntityRelationManager.Read() (no params)
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ReadRelations(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] ReadRelations — request received", correlationId);

            try
            {
                // ─── Hash-based conditional fetch ─────────────────────
                // Source: WebApiController.GetEntityRelationMetaList() — hash comparison nulls Object if match
                var clientHash = GetParamOrNull(request.QueryStringParameters, "hash");
                if (!string.IsNullOrWhiteSpace(clientHash))
                {
                    var currentHash = _entityService.GetRelationsHash();
                    if (string.Equals(clientHash, currentHash, StringComparison.Ordinal))
                    {
                        _logger.LogInformation("[{CorrelationId}] ReadRelations — hash match, returning no data", correlationId);
                        var noChangeResponse = new EntityRelationListResponse
                        {
                            Success = true,
                            Timestamp = DateTime.UtcNow,
                            Message = "Hash match — no changes.",
                            Hash = currentHash,
                            Object = null!
                        };
                        return BuildResponse((int)HttpStatusCode.OK, noChangeResponse, correlationId);
                    }
                }

                // ─── Full fetch ───────────────────────────────────────
                var result = await _entityService.ReadRelations();

                result.Timestamp = DateTime.UtcNow;
                result.Success = true;
                result.Hash = _entityService.GetRelationsHash();

                _logger.LogInformation("[{CorrelationId}] ReadRelations — returning {Count} relations",
                    correlationId, result.Object?.Count ?? 0);

                return BuildResponse((int)HttpStatusCode.OK, result, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] ReadRelations — unhandled exception", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "Internal server error.",
                    correlationId);
            }
        }

        // =====================================================================
        // UPDATE RELATION
        // =====================================================================

        /// <summary>
        /// Lambda handler for updating an existing entity relation definition.
        /// Route: PUT /v1/entity-management/meta/relations/{id}
        ///
        /// CRITICAL: Enforces immutability on 5 properties after creation:
        ///   - RelationType (OneToOne/OneToMany/ManyToMany)
        ///   - OriginEntityId
        ///   - OriginFieldId
        ///   - TargetEntityId
        ///   - TargetFieldId
        /// Only Name and Label (and Description) can be updated.
        ///
        /// Source: EntityRelationManager.Update() + ValidateRelation(ValidationType.Update)
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> UpdateRelation(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] UpdateRelation — request received", correlationId);

            try
            {
                // ─── Permission Check ─────────────────────────────────
                if (!HasPermission(request, EntityPermission.Update))
                {
                    _logger.LogWarning("[{CorrelationId}] UpdateRelation — access denied", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Admin permission required.",
                        correlationId);
                }

                // ─── Extract relation ID from path ────────────────────
                var idStr = GetParam(request.PathParameters, "id");
                if (!Guid.TryParse(idStr, out var relationId) || relationId == Guid.Empty)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid relation id specified.",
                        correlationId,
                        new List<ErrorModel>
                        {
                            new ErrorModel("id", idStr, "The relation id is not a valid GUID.")
                        });
                }

                // ─── Parse Request Body ───────────────────────────────
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                EntityRelation? relation;
                try
                {
                    relation = System.Text.Json.JsonSerializer.Deserialize<EntityRelation>(request.Body, _jsonOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[{CorrelationId}] UpdateRelation — invalid JSON: {Error}", correlationId, ex.Message);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid JSON in request body.",
                        correlationId);
                }

                if (relation == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Failed to parse relation from request body.",
                        correlationId);
                }

                // Ensure the body relation ID matches the path parameter
                relation.Id = relationId;

                // Trim name
                if (!string.IsNullOrEmpty(relation.Name))
                {
                    relation.Name = relation.Name.Trim();
                }

                // ─── Validation ───────────────────────────────────────
                var errors = await ValidateRelationForUpdate(relation, correlationId);
                if (errors.Count > 0)
                {
                    _logger.LogWarning("[{CorrelationId}] UpdateRelation — validation failed with {Count} errors",
                        correlationId, errors.Count);
                    var errorResponse = new EntityRelationResponse
                    {
                        Success = false,
                        Timestamp = DateTime.UtcNow,
                        Message = "The entity relation was not updated. Validation error occurred!",
                        Errors = errors,
                        Object = relation
                    };
                    return BuildResponse((int)HttpStatusCode.BadRequest, errorResponse, correlationId);
                }

                // ─── Persist via EntityService ────────────────────────
                var result = await _entityService.UpdateRelation(relation);

                if (!result.Success)
                {
                    _logger.LogWarning("[{CorrelationId}] UpdateRelation — service returned failure: {Message}",
                        correlationId, result.Message);
                    return BuildResponse((int)result.StatusCode, result, correlationId);
                }

                // ─── Clear metadata cache ─────────────────────────────
                _entityService.ClearCache();

                // ─── Publish domain event ─────────────────────────────
                await PublishDomainEvent(
                    "entity-management.relation.updated",
                    new { RelationId = relation.Id, RelationName = relation.Name },
                    correlationId);

                _logger.LogInformation("[{CorrelationId}] UpdateRelation — relation '{Name}' updated successfully (Id={Id})",
                    correlationId, relation.Name, relation.Id);

                result.Timestamp = DateTime.UtcNow;
                result.Success = true;
                result.Message = "The entity relation was successfully updated!";
                return BuildResponse((int)HttpStatusCode.OK, result, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] UpdateRelation — unhandled exception", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "Internal server error.",
                    correlationId);
            }
        }

        // =====================================================================
        // DELETE RELATION
        // =====================================================================

        /// <summary>
        /// Lambda handler for deleting an entity relation definition.
        /// Route: DELETE /v1/entity-management/meta/relations/{id}
        ///
        /// Verifies the relation exists before deletion, clears cache, and
        /// publishes an SNS domain event for downstream consumers.
        ///
        /// Source: EntityRelationManager.Delete(Guid) + WebApiController.DeleteEntityRelation()
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> DeleteRelation(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] DeleteRelation — request received", correlationId);

            try
            {
                // ─── Permission Check ─────────────────────────────────
                if (!HasPermission(request, EntityPermission.Delete))
                {
                    _logger.LogWarning("[{CorrelationId}] DeleteRelation — access denied", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Admin permission required.",
                        correlationId);
                }

                // ─── Extract relation ID from path ────────────────────
                var idStr = GetParam(request.PathParameters, "id");
                if (!Guid.TryParse(idStr, out var relationId) || relationId == Guid.Empty)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid relation id specified.",
                        correlationId,
                        new List<ErrorModel>
                        {
                            new ErrorModel("id", idStr, "The relation id is not a valid GUID.")
                        });
                }

                // ─── Read existing relation for event data ────────────
                // Source: EntityRelationManager.Delete() — reads relation before delete for response
                var existingResponse = await _entityService.ReadRelation(relationId);
                if (existingResponse.Object == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"Relation with id '{relationId}' not found.",
                        correlationId);
                }

                var relationName = existingResponse.Object.Name;

                // ─── Delete via EntityService ─────────────────────────
                var result = await _entityService.DeleteRelation(relationId);

                if (!result.Success)
                {
                    _logger.LogWarning("[{CorrelationId}] DeleteRelation — service returned failure: {Message}",
                        correlationId, result.Message);
                    return BuildResponse((int)result.StatusCode, result, correlationId);
                }

                // ─── Clear metadata cache ─────────────────────────────
                _entityService.ClearCache();

                // ─── Publish domain event ─────────────────────────────
                await PublishDomainEvent(
                    "entity-management.relation.deleted",
                    new { RelationId = relationId, RelationName = relationName },
                    correlationId);

                _logger.LogInformation("[{CorrelationId}] DeleteRelation — relation '{Name}' deleted successfully (Id={Id})",
                    correlationId, relationName, relationId);

                result.Timestamp = DateTime.UtcNow;
                result.Success = true;
                result.Message = "The entity relation was successfully deleted!";
                return BuildResponse((int)HttpStatusCode.OK, result, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] DeleteRelation — unhandled exception", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "Internal server error.",
                    correlationId);
            }
        }

        // =====================================================================
        // VALIDATION — CREATE
        // =====================================================================

        /// <summary>
        /// Validates an EntityRelation for creation. Implements the full two-level
        /// validation logic from EntityRelationManager.ValidateRelation() with
        /// ValidationType.Create:
        ///
        /// Level 1 (structural):
        ///   - Name length, format, uniqueness
        ///   - Id collision check
        ///   - Origin/target entity existence
        ///   - Origin/target field existence and GuidField type requirement
        ///
        /// Level 2 (semantic, only if Level 1 passes):
        ///   - Relation-type specific constraints (Required/Unique on fields)
        ///   - No same origin+target field on same entity for OneToMany/OneToOne
        ///   - No duplicate origin+target entity/field combinations
        /// </summary>
        /// <param name="relation">The relation to validate.</param>
        /// <param name="correlationId">Request correlation ID for logging.</param>
        /// <returns>List of validation errors; empty if valid.</returns>
        private async Task<List<ErrorModel>> ValidateRelationForCreate(
            EntityRelation relation,
            string correlationId)
        {
            var errors = new List<ErrorModel>();

            // ─── Level 1: Structural Validation ───────────────────────

            // Name length check
            // Source: EntityRelationManager.ValidateRelation() — name.Length > 63
            if (string.IsNullOrWhiteSpace(relation.Name))
            {
                errors.Add(new ErrorModel("name", relation.Name ?? string.Empty, "Name is required."));
            }
            else if (relation.Name.Length > MaxNameLength)
            {
                errors.Add(new ErrorModel("name", relation.Name,
                    $"The name can be no longer than {MaxNameLength} characters."));
            }
            else if (!ValidateName(relation.Name))
            {
                errors.Add(new ErrorModel("name", relation.Name,
                    "Name can only contain underscores and lowercase alphanumeric characters. It must begin with a letter, not with a number or underscore."));
            }

            // Label validation
            if (string.IsNullOrWhiteSpace(relation.Label))
            {
                errors.Add(new ErrorModel("label", relation.Label ?? string.Empty, "Label is required."));
            }

            // Id collision check
            // Source: EntityRelationManager.ValidateRelation() — if Id provided, must not already exist
            if (relation.Id != Guid.Empty)
            {
                var existingById = await _entityService.ReadRelation(relation.Id);
                if (existingById.Object != null)
                {
                    errors.Add(new ErrorModel("id", relation.Id.ToString(),
                        "A relation with this id already exists."));
                }
            }

            // Name uniqueness check
            // Source: EntityRelationManager.ValidateRelation() — checks all existing relation names
            if (!string.IsNullOrWhiteSpace(relation.Name))
            {
                var existingByName = await _entityService.ReadRelation(relation.Name);
                if (existingByName.Object != null)
                {
                    errors.Add(new ErrorModel("name", relation.Name,
                        "A relation with this name already exists."));
                }
            }

            // Resolve origin entity
            // Source: EntityRelationManager.ValidateRelation() — entMan.ReadEntity(relation.OriginEntityId)
            Entity? originEntity = null;
            if (relation.OriginEntityId == Guid.Empty)
            {
                errors.Add(new ErrorModel("originEntityId", relation.OriginEntityId.ToString(),
                    "Origin entity id is required."));
            }
            else
            {
                originEntity = await _entityService.GetEntity(relation.OriginEntityId);
                if (originEntity == null)
                {
                    errors.Add(new ErrorModel("originEntityId", relation.OriginEntityId.ToString(),
                        "The origin entity was not found."));
                }
            }

            // Resolve target entity
            Entity? targetEntity = null;
            if (relation.TargetEntityId == Guid.Empty)
            {
                errors.Add(new ErrorModel("targetEntityId", relation.TargetEntityId.ToString(),
                    "Target entity id is required."));
            }
            else
            {
                targetEntity = await _entityService.GetEntity(relation.TargetEntityId);
                if (targetEntity == null)
                {
                    errors.Add(new ErrorModel("targetEntityId", relation.TargetEntityId.ToString(),
                        "The target entity was not found."));
                }
            }

            // Resolve origin field
            // Source: EntityRelationManager.ValidateRelation() — originEntity fields search by fieldId
            Field? originField = null;
            if (originEntity != null && relation.OriginFieldId != Guid.Empty)
            {
                originField = originEntity.Fields?.FirstOrDefault(f => f.Id == relation.OriginFieldId);
                if (originField == null)
                {
                    errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                        "The origin field was not found in the origin entity."));
                }
                else if (originField.GetFieldType() != FieldType.GuidField)
                {
                    errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                        "The origin field must be a Guid field."));
                }
            }
            else if (originEntity != null && relation.OriginFieldId == Guid.Empty)
            {
                errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                    "Origin field id is required."));
            }

            // Resolve target field
            Field? targetField = null;
            if (targetEntity != null && relation.TargetFieldId != Guid.Empty)
            {
                targetField = targetEntity.Fields?.FirstOrDefault(f => f.Id == relation.TargetFieldId);
                if (targetField == null)
                {
                    errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(),
                        "The target field was not found in the target entity."));
                }
                else if (targetField.GetFieldType() != FieldType.GuidField)
                {
                    errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(),
                        "The target field must be a Guid field."));
                }
            }
            else if (targetEntity != null && relation.TargetFieldId == Guid.Empty)
            {
                errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(),
                    "Target field id is required."));
            }

            // ─── Early return if Level 1 errors exist ─────────────────
            // Source: EntityRelationManager.ValidateRelation() — "if (errors.Count > 0) response.Errors = errors; return;"
            if (errors.Count > 0)
            {
                return errors;
            }

            // ─── Level 2: Semantic Validation ─────────────────────────

            // OneToMany/OneToOne: cannot use same origin and target field on same entity
            // Source: "if (relation.OriginEntityId == relation.TargetEntityId &&
            //          relation.OriginFieldId == relation.TargetFieldId)"
            if (relation.RelationType == EntityRelationType.OneToMany ||
                relation.RelationType == EntityRelationType.OneToOne)
            {
                if (relation.OriginEntityId == relation.TargetEntityId &&
                    relation.OriginFieldId == relation.TargetFieldId)
                {
                    errors.Add(new ErrorModel("", "",
                        "The origin and target fields cannot be the same when the relation is between the same entity."));
                }
            }

            // Duplicate relation check: no two relations with same origin+target entity/field pairing
            // Source: EntityRelationManager.ValidateRelation() — iterates all existing relations
            if (relation.RelationType == EntityRelationType.OneToMany ||
                relation.RelationType == EntityRelationType.OneToOne)
            {
                var allRelationsResponse = await _entityService.ReadRelations();
                if (allRelationsResponse.Object != null)
                {
                    var duplicate = allRelationsResponse.Object.Any(r =>
                        r.OriginEntityId == relation.OriginEntityId &&
                        r.TargetEntityId == relation.TargetEntityId &&
                        r.OriginFieldId == relation.OriginFieldId &&
                        r.TargetFieldId == relation.TargetFieldId);

                    if (duplicate)
                    {
                        errors.Add(new ErrorModel("", "",
                            "A relation with the same origin and target entity/field combination already exists."));
                    }
                }
            }

            // Relation-type specific field constraints
            // Source: EntityRelationManager.ValidateRelation() — checks Required + Unique
            switch (relation.RelationType)
            {
                case EntityRelationType.OneToOne:
                    // Both origin and target GUID fields must be Required AND Unique
                    if (originField != null && (!originField.Required || !originField.Unique))
                    {
                        errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                            "For One-to-One relation, the origin field must be Required and Unique."));
                    }
                    if (targetField != null && (!targetField.Required || !targetField.Unique))
                    {
                        errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(),
                            "For One-to-One relation, the target field must be Required and Unique."));
                    }
                    break;

                case EntityRelationType.OneToMany:
                    // Origin field must be Required AND Unique
                    if (originField != null && (!originField.Required || !originField.Unique))
                    {
                        errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                            "For One-to-Many relation, the origin field must be Required and Unique."));
                    }
                    // Target field: no Required/Unique constraint per source
                    break;

                case EntityRelationType.ManyToMany:
                    // Both origin and target GUID fields must be Required AND Unique
                    if (originField != null && (!originField.Required || !originField.Unique))
                    {
                        errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                            "For Many-to-Many relation, the origin field must be Required and Unique."));
                    }
                    if (targetField != null && (!targetField.Required || !targetField.Unique))
                    {
                        errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(),
                            "For Many-to-Many relation, the target field must be Required and Unique."));
                    }
                    break;
            }

            return errors;
        }

        // =====================================================================
        // VALIDATION — UPDATE
        // =====================================================================

        /// <summary>
        /// Validates an EntityRelation for update. Implements the validation logic
        /// from EntityRelationManager.ValidateRelation() with ValidationType.Update:
        ///
        /// - Relation must exist
        /// - 5 immutable fields enforced (RelationType, OriginEntityId, OriginFieldId,
        ///   TargetEntityId, TargetFieldId)
        /// - Name uniqueness (against other relations, not self)
        /// - Name length and format
        /// - Label validation
        ///
        /// Source: EntityRelationManager.ValidateRelation(ValidationType.Update)
        /// </summary>
        /// <param name="relation">The relation with updated values.</param>
        /// <param name="correlationId">Request correlation ID for logging.</param>
        /// <returns>List of validation errors; empty if valid.</returns>
        private async Task<List<ErrorModel>> ValidateRelationForUpdate(
            EntityRelation relation,
            string correlationId)
        {
            var errors = new List<ErrorModel>();

            // ─── Relation must exist ──────────────────────────────────
            // Source: EntityRelationManager.ValidateRelation(Update) — "Id is required!"
            if (relation.Id == Guid.Empty)
            {
                errors.Add(new ErrorModel("id", "", "Relation id is required for update."));
                return errors;
            }

            var existingResponse = await _entityService.ReadRelation(relation.Id);
            if (existingResponse.Object == null)
            {
                errors.Add(new ErrorModel("id", relation.Id.ToString(),
                    "A relation with this id does not exist."));
                return errors;
            }

            var existing = existingResponse.Object;

            // ─── Immutability enforcement ─────────────────────────────
            // Source: EntityRelationManager.ValidateRelation(Update) — 5 immutable fields
            // CRITICAL: These properties cannot be changed after creation
            if (relation.RelationType != existing.RelationType)
            {
                errors.Add(new ErrorModel("relationType", relation.RelationType.ToString(),
                    "RelationType is immutable and cannot be changed after creation."));
            }

            if (relation.OriginEntityId != existing.OriginEntityId)
            {
                errors.Add(new ErrorModel("originEntityId", relation.OriginEntityId.ToString(),
                    "OriginEntityId is immutable and cannot be changed after creation."));
            }

            if (relation.OriginFieldId != existing.OriginFieldId)
            {
                errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                    "OriginFieldId is immutable and cannot be changed after creation."));
            }

            if (relation.TargetEntityId != existing.TargetEntityId)
            {
                errors.Add(new ErrorModel("targetEntityId", relation.TargetEntityId.ToString(),
                    "TargetEntityId is immutable and cannot be changed after creation."));
            }

            if (relation.TargetFieldId != existing.TargetFieldId)
            {
                errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(),
                    "TargetFieldId is immutable and cannot be changed after creation."));
            }

            // ─── Name validation ──────────────────────────────────────
            if (string.IsNullOrWhiteSpace(relation.Name))
            {
                errors.Add(new ErrorModel("name", relation.Name ?? string.Empty, "Name is required."));
            }
            else if (relation.Name.Length > MaxNameLength)
            {
                errors.Add(new ErrorModel("name", relation.Name,
                    $"The name can be no longer than {MaxNameLength} characters."));
            }
            else if (!ValidateName(relation.Name))
            {
                errors.Add(new ErrorModel("name", relation.Name,
                    "Name can only contain underscores and lowercase alphanumeric characters. It must begin with a letter, not with a number or underscore."));
            }
            else
            {
                // Name uniqueness check (against other relations, not self)
                // Source: checks all existing relation names, excluding self
                var existingByName = await _entityService.ReadRelation(relation.Name);
                if (existingByName.Object != null && existingByName.Object.Id != relation.Id)
                {
                    errors.Add(new ErrorModel("name", relation.Name,
                        "A relation with this name already exists."));
                }
            }

            // ─── Label validation ─────────────────────────────────────
            if (string.IsNullOrWhiteSpace(relation.Label))
            {
                errors.Add(new ErrorModel("label", relation.Label ?? string.Empty, "Label is required."));
            }

            return errors;
        }

        // =====================================================================
        // PRIVATE HELPER METHODS
        // =====================================================================

        /// <summary>
        /// Validates a relation name matches the expected format: lowercase alphanumeric
        /// and underscores, starting with a letter.
        /// Source: ValidationUtility.ValidateName() pattern
        /// </summary>
        /// <param name="name">The name to validate.</param>
        /// <returns>True if the name is valid; false otherwise.</returns>
        private static bool ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            // Must start with a lowercase letter, then lowercase alphanumeric + underscores
            return System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z][a-z0-9_]*$");
        }

        /// <summary>
        /// Extracts a parameter value from a dictionary of path or query parameters.
        /// Returns empty string if the dictionary is null or the key is not found.
        /// Mirrors the GetParam helper in EntityHandler.cs.
        /// </summary>
        /// <param name="parameters">The parameter dictionary (may be null).</param>
        /// <param name="key">The parameter key to look up.</param>
        /// <returns>The parameter value, or empty string if not found.</returns>
        private static string GetParam(IDictionary<string, string>? parameters, string key)
        {
            if (parameters == null)
                return string.Empty;

            // Try named parameter first
            if (parameters.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return value;

            // Fallback: extract from {proxy+} catch-all parameter for HTTP API v2
            if (parameters.TryGetValue("proxy", out var proxy) && !string.IsNullOrEmpty(proxy))
            {
                var segments = proxy.Split('/', StringSplitOptions.RemoveEmptyEntries);
                // For "idOrName" or "id" — the relation ID/name is typically the first or last segment
                if (key is "idOrName" or "id")
                {
                    // If only one segment, that's the ID/name
                    if (segments.Length >= 1)
                    {
                        // Return the first non-keyword segment (skip "relations", "list", etc.)
                        foreach (var seg in segments)
                        {
                            if (seg.Equals("relations", StringComparison.OrdinalIgnoreCase) ||
                                seg.Equals("list", StringComparison.OrdinalIgnoreCase))
                                continue;
                            return seg;
                        }
                    }
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Extracts a parameter value from a dictionary of query parameters.
        /// Returns null if the dictionary is null or the key is not found.
        /// Mirrors the GetParamOrNull helper in EntityHandler.cs.
        /// </summary>
        /// <param name="parameters">The parameter dictionary (may be null).</param>
        /// <param name="key">The parameter key to look up.</param>
        /// <returns>The parameter value, or null if not found.</returns>
        private static string? GetParamOrNull(IDictionary<string, string>? parameters, string key)
        {
            if (parameters == null)
                return null;

            return parameters.TryGetValue(key, out var value) ? value : null;
        }

        /// <summary>
        /// Checks whether the request has permission for the specified operation.
        /// Delegates to IsAdminUser() for the actual JWT claims inspection.
        /// Source: SecurityContext.HasMetaPermission() replaced by JWT admin check.
        /// </summary>
        /// <param name="request">The API Gateway request containing JWT claims.</param>
        /// <param name="permission">The permission level being checked.</param>
        /// <returns>True if the user has the required permission.</returns>
        private bool HasPermission(APIGatewayHttpApiV2ProxyRequest request, EntityPermission permission)
        {
            // Read operations don't require admin — only mutations do
            if (permission == EntityPermission.Read)
                return true;

            var isAdmin = IsAdminUser(request);
            if (!isAdmin)
            {
                _logger.LogWarning("Permission denied for {Permission}: user is not admin (AdminRoleId={AdminRoleId})",
                    permission, SystemIds.AdministratorRoleId);
            }
            return isAdmin;
        }

        /// <summary>
        /// Determines if the current request is from an admin user by inspecting
        /// JWT claims from the API Gateway authorizer context.
        ///
        /// Checks (in order):
        ///   1. JWT authorizer: cognito:groups, custom:roles, scope claims
        ///   2. Lambda authorizer: isAdmin boolean/string, roles string
        ///
        /// Both pathways look for "administrator" role or the SystemIds.AdministratorRoleId GUID.
        /// Mirrors the IsAdminUser helper in EntityHandler.cs.
        /// </summary>
        /// <param name="request">The API Gateway request with authorizer context.</param>
        /// <returns>True if the user has admin privileges.</returns>
        private static bool IsAdminUser(APIGatewayHttpApiV2ProxyRequest request)
        {
            var adminRoleIdStr = SystemIds.AdministratorRoleId.ToString().ToLowerInvariant();

            try
            {
                var authorizer = request.RequestContext?.Authorizer;
                if (authorizer == null)
                    return false;

                // ─── JWT Authorizer (Cognito) ─────────────────────────
                if (authorizer.Jwt?.Claims != null)
                {
                    var claims = authorizer.Jwt.Claims;

                    // Check cognito:groups claim
                    if (claims.TryGetValue("cognito:groups", out var groups) && !string.IsNullOrEmpty(groups))
                    {
                        if (groups.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                            groups.Contains(adminRoleIdStr, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    // Check custom:roles claim
                    if (claims.TryGetValue("custom:roles", out var roles) && !string.IsNullOrEmpty(roles))
                    {
                        if (roles.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                            roles.Contains(adminRoleIdStr, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    // Check scope claim
                    if (claims.TryGetValue("scope", out var scope) && !string.IsNullOrEmpty(scope))
                    {
                        if (scope.Contains("admin", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }

                // ─── Lambda Authorizer ────────────────────────────────
                if (authorizer.Lambda != null)
                {
                    // Check isAdmin flag
                    if (authorizer.Lambda.TryGetValue("isAdmin", out var isAdminObj) && isAdminObj != null)
                    {
                        var isAdminStr = isAdminObj.ToString();
                        if (string.Equals(isAdminStr, "true", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(isAdminStr, "True", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    // Check roles
                    if (authorizer.Lambda.TryGetValue("roles", out var rolesObj) && rolesObj != null)
                    {
                        var rolesStr = rolesObj.ToString();
                        if (rolesStr != null &&
                            (rolesStr.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                             rolesStr.Contains(adminRoleIdStr, StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Silently return false on any claim parsing errors
            }

            return false;
        }

        /// <summary>
        /// Extracts the correlation ID from the request for distributed tracing.
        /// Checks (in order):
        ///   1. x-correlation-id request header
        ///   2. ILambdaContext.AwsRequestId
        ///   3. New GUID as fallback
        /// Mirrors the ExtractCorrelationId helper in EntityHandler.cs.
        /// </summary>
        /// <param name="request">The API Gateway request with headers.</param>
        /// <param name="context">The Lambda context with AWS request ID.</param>
        /// <returns>The correlation ID string.</returns>
        private static string ExtractCorrelationId(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            // Check request headers for existing correlation ID
            if (request.Headers != null &&
                request.Headers.TryGetValue("x-correlation-id", out var corrId) &&
                !string.IsNullOrWhiteSpace(corrId))
            {
                return corrId;
            }

            // Fall back to Lambda AWS request ID
            if (!string.IsNullOrWhiteSpace(context.AwsRequestId))
            {
                return context.AwsRequestId;
            }

            // Ultimate fallback: generate new GUID
            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Builds a successful API response with the specified status code and body.
        /// Serializes the body using System.Text.Json with the shared _jsonOptions.
        /// Includes Content-Type and X-Correlation-Id response headers.
        /// Mirrors the BuildResponse helper in EntityHandler.cs.
        /// </summary>
        /// <param name="statusCode">HTTP status code (e.g., 200, 201).</param>
        /// <param name="body">Response body object to serialize.</param>
        /// <param name="correlationId">Correlation ID for the response header.</param>
        /// <returns>The formatted API Gateway response.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(
            int statusCode,
            object body,
            string correlationId)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = System.Text.Json.JsonSerializer.Serialize(body, _jsonOptions),
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "X-Correlation-Id", correlationId }
                }
            };
        }

        /// <summary>
        /// Builds an error API response with structured error envelope.
        /// Uses BaseResponseModel with Success=false for consistent error format.
        /// Mirrors the BuildErrorResponse helper in EntityHandler.cs.
        /// </summary>
        /// <param name="statusCode">HTTP status code (e.g., 400, 403, 500).</param>
        /// <param name="message">Human-readable error message.</param>
        /// <param name="correlationId">Correlation ID for the response header.</param>
        /// <param name="errors">Optional list of structured error details.</param>
        /// <returns>The formatted error API Gateway response.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(
            int statusCode,
            string message,
            string correlationId,
            List<ErrorModel>? errors = null)
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
                    { "Content-Type", "application/json" },
                    { "X-Correlation-Id", correlationId }
                }
            };
        }

        /// <summary>
        /// Publishes a domain event to SNS for downstream consumers.
        /// Uses Newtonsoft.Json for backward compatibility with existing event consumers.
        /// Includes message attributes for event type, correlation ID, and source.
        /// Non-blocking: exceptions are caught and logged but do not fail the operation.
        ///
        /// Event naming convention: entity-management.relation.{action}
        /// Source: Replaces monolith HookManager post-hook pattern per AAP §0.7.2.
        /// Mirrors the PublishDomainEvent helper in EntityHandler.cs.
        /// </summary>
        /// <param name="action">The event action (e.g., "entity-management.relation.created").</param>
        /// <param name="eventData">The event payload data.</param>
        /// <param name="correlationId">Correlation ID for tracing the event.</param>
        private async Task PublishDomainEvent(string action, object eventData, string correlationId)
        {
            if (string.IsNullOrWhiteSpace(_relationTopicArn))
            {
                _logger.LogWarning("[{CorrelationId}] RELATION_TOPIC_ARN not configured — skipping SNS publish for '{Action}'",
                    correlationId, action);
                return;
            }

            try
            {
                var publishRequest = new PublishRequest
                {
                    TopicArn = _relationTopicArn,
                    // Use Newtonsoft.Json for backward compatibility with existing event consumers
                    Message = JsonConvert.SerializeObject(eventData),
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

                _logger.LogInformation("[{CorrelationId}] Published domain event '{Action}'",
                    correlationId, action);
            }
            catch (Exception ex)
            {
                // Non-blocking: SNS publish failure should not fail the CRUD operation
                _logger.LogError(ex, "[{CorrelationId}] Failed to publish domain event '{Action}'",
                    correlationId, action);
            }
        }
    }
}
