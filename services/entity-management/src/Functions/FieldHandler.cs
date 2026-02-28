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
using Newtonsoft.Json.Linq;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;

namespace WebVellaErp.EntityManagement.Functions
{
    /// <summary>
    /// Lambda handler for field metadata CRUD operations in the Entity Management bounded context.
    /// Replaces the field metadata endpoints from WebApiController.cs
    /// (routes api/v3/en_US/meta/entity/{Id}/field[/{FieldId}]) and transforms field management
    /// logic from EntityManager.cs into serverless Lambda function entry points.
    ///
    /// Supports 20+ polymorphic field types with type-specific validation using
    /// InputField.ConvertField polymorphic dispatch via Newtonsoft.Json JObject.
    ///
    /// Route mapping (API Gateway HTTP API v2):
    ///   POST   /v1/entity-management/meta/entities/{entityId}/fields              → CreateField
    ///   GET    /v1/entity-management/meta/entities/{entityId}/fields/{fieldId}     → ReadField
    ///   PUT    /v1/entity-management/meta/entities/{entityId}/fields/{fieldId}     → UpdateField
    ///   PATCH  /v1/entity-management/meta/entities/{entityId}/fields/{fieldId}     → PatchField
    ///   DELETE /v1/entity-management/meta/entities/{entityId}/fields/{fieldId}     → DeleteField
    ///
    /// Authorization: All mutation endpoints require administrator role (JWT claims check).
    /// Events: All mutations publish SNS domain events (entity-management.field.{action}).
    /// Caching: Entity metadata cached with IMemoryCache; cache cleared after mutations.
    /// </summary>
    public class FieldHandler
    {
        // ─── Dependencies (injected via constructor DI) ───────────────────

        private readonly IEntityService _entityService;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly IMemoryCache _cache;
        private readonly ILogger<FieldHandler> _logger;

        // ─── Configuration ────────────────────────────────────────────────

        /// <summary>
        /// SNS topic ARN for field domain events. Retrieved from FIELD_TOPIC_ARN environment variable.
        /// Falls back to ENTITY_TOPIC_ARN if FIELD_TOPIC_ARN is not set.
        /// When empty/null, event publishing is skipped with a warning log.
        /// </summary>
        private readonly string? _fieldTopicArn;

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
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // ─── Common patchable field property names ────────────────────────

        /// <summary>
        /// Set of common InputField property names that can be applied during PATCH operations
        /// regardless of field type. These properties exist on the abstract InputField base class.
        /// Source: WebApiController.cs PatchField common-property block (lines 1952-1975).
        /// </summary>
        private static readonly HashSet<string> CommonPatchProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "label", "placeholdertext", "description", "helptext",
            "required", "unique", "searchable", "auditable", "system",
            "enablesecurity", "permissions"
        };

        // ─── Constructor ──────────────────────────────────────────────────

        /// <summary>
        /// Initializes a new instance of the FieldHandler with all required dependencies.
        /// Dependencies are resolved via the Lambda service provider (configured in Startup/Program).
        /// </summary>
        /// <param name="entityService">Entity metadata CRUD service replacing EntityManager — provides
        /// CreateField, UpdateField, DeleteField, ReadFields, ReadEntity, GetEntity, and ClearCache.</param>
        /// <param name="snsClient">SNS client for publishing domain events after field mutations.</param>
        /// <param name="cache">In-memory cache for entity metadata.</param>
        /// <param name="logger">Structured JSON logger with correlation-ID propagation.</param>
        public FieldHandler(
            IEntityService entityService,
            IAmazonSimpleNotificationService snsClient,
            IMemoryCache cache,
            ILogger<FieldHandler> logger)
        {
            _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _fieldTopicArn = Environment.GetEnvironmentVariable("FIELD_TOPIC_ARN")
                ?? Environment.GetEnvironmentVariable("ENTITY_TOPIC_ARN");
            _isDevelopmentMode = string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"), "true", StringComparison.OrdinalIgnoreCase);
        }

        // ═════════════════════════════════════════════════════════════════
        // PUBLIC LAMBDA HANDLER METHODS
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lambda handler for POST /v1/entity-management/meta/entities/{entityId}/fields
        /// Creates a new field definition on the specified entity.
        /// Supports all 20+ polymorphic field types via InputField.ConvertField(JObject).
        /// Source: WebApiController.CreateField (lines 1595-1616) + EntityManager.CreateField()
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request containing InputField JSON in body.</param>
        /// <param name="context">Lambda execution context for logging and request correlation.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with FieldResponse envelope (201 Created or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> CreateField(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "CreateField started. CorrelationId={CorrelationId}, FunctionName={FunctionName}",
                correlationId, context.FunctionName);

            try
            {
                // Permission gating: admin role required for all field mutations.
                // Source: [Authorize(Roles = "administrator")] on WebApiController field endpoints.
                if (!IsAdminUser(request))
                {
                    _logger.LogWarning(
                        "CreateField access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator role required.",
                        correlationId);
                }

                // Extract and validate entity ID from route path parameters.
                var entityIdStr = GetParam(request, "entityId");
                if (string.IsNullOrWhiteSpace(entityIdStr) || !Guid.TryParse(entityIdStr, out var entityId))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid or missing entity ID in route. A valid GUID is required.",
                        correlationId);
                }

                // Validate request body is present.
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                // Polymorphic field deserialization via Newtonsoft.Json JObject.
                // Source: InputField.ConvertField(submitObj) inspects the fieldType property
                // to determine the concrete InputField subclass (20+ field types).
                // System.Text.Json cannot perform runtime discriminator-based polymorphic deserialization,
                // so we retain Newtonsoft.Json JObject for this critical dispatch path.
                InputField inputField;
                try
                {
                    var submitObj = JObject.Parse(request.Body);
                    inputField = InputField.ConvertField(submitObj);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "CreateField invalid field JSON. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Input object is not in valid format! It cannot be converted. {(_isDevelopmentMode ? ex.Message : string.Empty)}".Trim(),
                        correlationId);
                }

                // Delegate to EntityService.CreateField which handles validation, DynamoDB persistence,
                // and default value generation. Source: EntityManager.CreateField(entityId, field)
                var response = await _entityService.CreateField(entityId, inputField);

                // Clear metadata cache after mutation. Source: Cache.Clear() after every mutation.
                _entityService.ClearCache();

                // Publish SNS domain event for cross-service communication.
                // Replaces monolith's synchronous HookManager post-hook pattern.
                if (response.Success && response.Object != null)
                {
                    await PublishDomainEvent(
                        "entity-management.field.created",
                        new
                        {
                            entityId,
                            fieldId = response.Object.Id,
                            fieldName = response.Object.Name
                        },
                        correlationId);
                }

                var statusCode = response.Success
                    ? (int)HttpStatusCode.Created
                    : (int)response.StatusCode;

                _logger.LogInformation(
                    "CreateField completed. Success={Success}, EntityId={EntityId}, FieldId={FieldId}, CorrelationId={CorrelationId}",
                    response.Success, entityId, response.Object?.Id, correlationId);

                return BuildResponse(statusCode, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "CreateField unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while creating the field.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/entity-management/meta/entities/{entityId}/fields/{fieldId}
        /// Returns a single field definition by entity ID and field ID.
        /// Source: WebApiController GET api/v3/en_US/meta/entity/{Id}/field/{FieldId}
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request with entityId and fieldId path parameters.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with FieldResponse envelope (200 OK or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ReadField(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "ReadField started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                // Permission gating: read operations also require admin role for metadata endpoints.
                // Source: [Authorize(Roles = "administrator")] on all entity meta endpoints.
                if (!IsAdminUser(request))
                {
                    _logger.LogWarning(
                        "ReadField access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator role required.",
                        correlationId);
                }

                // Extract and validate entity ID from route path parameters.
                var entityIdStr = GetParam(request, "entityId");
                if (string.IsNullOrWhiteSpace(entityIdStr) || !Guid.TryParse(entityIdStr, out var entityId))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid or missing entity ID in route. A valid GUID is required.",
                        correlationId);
                }

                // Extract and validate field ID from route path parameters.
                var fieldIdStr = GetParam(request, "fieldId");
                if (string.IsNullOrWhiteSpace(fieldIdStr) || !Guid.TryParse(fieldIdStr, out var fieldId))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid or missing field ID in route. A valid GUID is required.",
                        correlationId);
                }

                // Read entity metadata from cache/DynamoDB to locate the field within the entity.
                // Source: DbContext.Current.EntityRepository.Read(entityId) → entity.Fields.FirstOrDefault(f => f.Id == fieldId)
                var entity = await _entityService.GetEntity(entityId);
                if (entity == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"Entity with ID '{entityId}' not found.",
                        correlationId);
                }

                // Find the specific field within the entity's field list by field ID.
                var field = entity.Fields?.FirstOrDefault(f => f.Id == fieldId);
                if (field == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"Field with ID '{fieldId}' not found in entity '{entity.Name}'.",
                        correlationId);
                }

                var response = new FieldResponse
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Object = field,
                    Message = "Field read successfully."
                };

                _logger.LogInformation(
                    "ReadField completed. EntityId={EntityId}, FieldId={FieldId}, FieldName={FieldName}, CorrelationId={CorrelationId}",
                    entityId, fieldId, field.Name, correlationId);

                return BuildResponse((int)HttpStatusCode.OK, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ReadField unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while reading the field.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for PUT /v1/entity-management/meta/entities/{entityId}/fields/{fieldId}
        /// Performs a full update of an existing field definition.
        /// Validates all submitted properties exist on the resolved field type via reflection.
        /// Source: WebApiController.UpdateField (lines 1622-1672)
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request containing complete InputField JSON.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with FieldResponse envelope (200 OK or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> UpdateField(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "UpdateField started. CorrelationId={CorrelationId}, FunctionName={FunctionName}",
                correlationId, context.FunctionName);

            try
            {
                // Permission gating: admin role required for all field mutations.
                if (!IsAdminUser(request))
                {
                    _logger.LogWarning(
                        "UpdateField access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator role required.",
                        correlationId);
                }

                // Extract and validate entity ID from route.
                var entityIdStr = GetParam(request, "entityId");
                if (string.IsNullOrWhiteSpace(entityIdStr) || !Guid.TryParse(entityIdStr, out var entityId))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid or missing entity ID in route. A valid GUID is required.",
                        correlationId);
                }

                // Extract and validate field ID from route.
                var fieldIdStr = GetParam(request, "fieldId");
                if (string.IsNullOrWhiteSpace(fieldIdStr) || !Guid.TryParse(fieldIdStr, out var fieldId))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid or missing field ID in route. A valid GUID is required.",
                        correlationId);
                }

                // Validate request body is present.
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                // Parse body as JObject for property validation and polymorphic dispatch.
                JObject submitObj;
                try
                {
                    submitObj = JObject.Parse(request.Body);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "UpdateField invalid JSON body. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid JSON in request body.",
                        correlationId);
                }

                // Extract fieldType from body — required for property validation.
                // Source: WebApiController.UpdateField requires fieldType to resolve concrete type.
                var fieldTypeProp = submitObj.Properties()
                    .SingleOrDefault(k => k.Name.Equals("fieldType", StringComparison.OrdinalIgnoreCase));
                if (fieldTypeProp == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "fieldType property is required in the request body.",
                        correlationId);
                }

                FieldType fieldType;
                try
                {
                    fieldType = (FieldType)Enum.ToObject(typeof(FieldType), fieldTypeProp.Value.ToObject<int>());
                }
                catch
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid fieldType value. Must be a valid integer corresponding to a FieldType enum.",
                        correlationId);
                }

                // Validate all submitted properties exist on the resolved field type via reflection.
                // Source: WebApiController.UpdateField uses GetProperties() to validate submitted keys
                // and rejects any property not part of the concrete InputField subclass model.
                var inputFieldType = InputField.GetFieldType(fieldType);
                if (inputFieldType == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Unsupported fieldType '{(int)fieldType}'.",
                        correlationId);
                }

                var validPropertyNames = inputFieldType.GetProperties()
                    .Select(p => p.Name.ToLower())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var invalidProperties = new List<string>();
                foreach (var prop in submitObj.Properties())
                {
                    // Skip known metadata properties that are not part of the model.
                    if (prop.Name.Equals("fieldType", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!validPropertyNames.Contains(prop.Name.ToLower()))
                    {
                        invalidProperties.Add(prop.Name);
                    }
                }

                if (invalidProperties.Count > 0)
                {
                    var errors = invalidProperties
                        .Select(p => new ErrorModel("field", p, $"Property '{p}' is not valid for this field type."))
                        .ToList();
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Some properties are not valid for the specified field type.",
                        correlationId,
                        errors);
                }

                // Polymorphic field deserialization via Newtonsoft.Json JObject.
                InputField inputField;
                try
                {
                    inputField = InputField.ConvertField(submitObj);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "UpdateField field conversion failed. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Input object is not in valid format! It cannot be converted. {(_isDevelopmentMode ? ex.Message : string.Empty)}".Trim(),
                        correlationId);
                }

                // Ensure the field ID from the route matches the body (or set it from route).
                if (inputField.Id == null || inputField.Id == Guid.Empty)
                {
                    inputField.Id = fieldId;
                }
                else if (inputField.Id != fieldId)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Field ID in body does not match field ID in route.",
                        correlationId);
                }

                // Delegate to EntityService.UpdateField which handles validation and persistence.
                // Source: EntityManager.UpdateField(entityId, field)
                var response = await _entityService.UpdateField(entityId, inputField);

                // Clear metadata cache after mutation.
                _entityService.ClearCache();

                // Publish SNS domain event.
                if (response.Success && response.Object != null)
                {
                    await PublishDomainEvent(
                        "entity-management.field.updated",
                        new
                        {
                            entityId,
                            fieldId = response.Object.Id,
                            fieldName = response.Object.Name
                        },
                        correlationId);
                }

                var statusCode = response.Success
                    ? (int)HttpStatusCode.OK
                    : (int)response.StatusCode;

                _logger.LogInformation(
                    "UpdateField completed. Success={Success}, EntityId={EntityId}, FieldId={FieldId}, CorrelationId={CorrelationId}",
                    response.Success, entityId, fieldId, correlationId);

                return BuildResponse(statusCode, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UpdateField unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while updating the field.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for PATCH /v1/entity-management/meta/entities/{entityId}/fields/{fieldId}
        /// Performs a partial update of an existing field — only submitted properties are applied.
        /// Contains per-field-type switch logic for type-specific property merging.
        /// Source: WebApiController.PatchField (lines 1678-1978) — complex per-field-type patching.
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request containing partial InputField JSON.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with FieldResponse envelope (200 OK or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> PatchField(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "PatchField started. CorrelationId={CorrelationId}, FunctionName={FunctionName}",
                correlationId, context.FunctionName);

            try
            {
                // Permission gating: admin role required for all field mutations.
                if (!IsAdminUser(request))
                {
                    _logger.LogWarning(
                        "PatchField access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator role required.",
                        correlationId);
                }

                // Extract and validate entity ID.
                var entityIdStr = GetParam(request, "entityId");
                if (string.IsNullOrWhiteSpace(entityIdStr) || !Guid.TryParse(entityIdStr, out var entityId))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid or missing entity ID in route. A valid GUID is required.",
                        correlationId);
                }

                // Extract and validate field ID.
                var fieldIdStr = GetParam(request, "fieldId");
                if (string.IsNullOrWhiteSpace(fieldIdStr) || !Guid.TryParse(fieldIdStr, out var fieldId))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid or missing field ID in route. A valid GUID is required.",
                        correlationId);
                }

                // Validate request body.
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                // Parse body as JObject for per-property inspection and polymorphic dispatch.
                JObject submitObj;
                try
                {
                    submitObj = JObject.Parse(request.Body);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "PatchField invalid JSON body. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid JSON in request body.",
                        correlationId);
                }

                // fieldType property is required in the PATCH body to determine concrete field type.
                // Source: WebApiController.PatchField requires fieldType in submitObj.
                var fieldTypeProp = submitObj.Properties()
                    .SingleOrDefault(k => k.Name.Equals("fieldType", StringComparison.OrdinalIgnoreCase));
                if (fieldTypeProp == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "fieldType property is required in the request body for PATCH operations.",
                        correlationId);
                }

                FieldType fieldType;
                try
                {
                    fieldType = (FieldType)Enum.ToObject(typeof(FieldType), fieldTypeProp.Value.ToObject<int>());
                }
                catch
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid fieldType value. Must be a valid integer corresponding to a FieldType enum.",
                        correlationId);
                }

                // Validate submitted properties against the resolved field type model.
                // Source: WebApiController.PatchField checks submitted keys against inputFieldType.GetProperties().
                var inputFieldType = InputField.GetFieldType(fieldType);
                if (inputFieldType == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Unsupported fieldType '{(int)fieldType}'.",
                        correlationId);
                }

                var validPropertyNames = inputFieldType.GetProperties()
                    .Select(p => p.Name.ToLower())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var invalidProperties = new List<string>();
                foreach (var prop in submitObj.Properties())
                {
                    if (prop.Name.Equals("fieldType", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!validPropertyNames.Contains(prop.Name.ToLower()))
                    {
                        invalidProperties.Add(prop.Name);
                    }
                }

                if (invalidProperties.Count > 0)
                {
                    var errors = invalidProperties
                        .Select(p => new ErrorModel("field", p, $"Property '{p}' is not valid for this field type."))
                        .ToList();
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Some properties are not valid for the specified field type.",
                        correlationId,
                        errors);
                }

                // Read the existing entity to get the current field state for merging.
                // Source: WebApiController.PatchField reads entity from DbContext.Current.EntityRepository.Read(entityId)
                var entity = await _entityService.GetEntity(entityId);
                if (entity == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"Entity with ID '{entityId}' not found.",
                        correlationId);
                }

                // Find the existing field by ID in the entity's field list.
                var existingField = entity.Fields?.FirstOrDefault(f => f.Id == fieldId);
                if (existingField == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"Field with ID '{fieldId}' not found in entity '{entity.Name}'.",
                        correlationId);
                }

                // Polymorphic deserialization of the submitted body to get typed property values.
                InputField inputField;
                try
                {
                    inputField = InputField.ConvertField(submitObj);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "PatchField field conversion failed. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Input object is not in valid format! It cannot be converted. {(_isDevelopmentMode ? ex.Message : string.Empty)}".Trim(),
                        correlationId);
                }

                // Create a new InputField of the correct concrete type and selectively copy
                // only the submitted properties from the deserialized inputField.
                // Source: WebApiController.PatchField per-field-type switch (lines 1744-1960)
                InputField field;
                try
                {
                    field = ApplyPatchProperties(fieldType, inputField, submitObj);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "PatchField property merge failed. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        $"Input object is not in valid format! It cannot be converted. {(_isDevelopmentMode ? ex.Message : string.Empty)}".Trim(),
                        correlationId);
                }

                // Set the field ID from the route to ensure correct identity.
                field.Id = fieldId;

                // Apply common InputField properties that were submitted.
                // Source: WebApiController.PatchField common-property block (lines 1952-1975)
                ApplyCommonPatchProperties(field, inputField, submitObj);

                // Delegate to EntityService.UpdateField which handles validation and persistence.
                // Source: entMan.UpdateField(entity, field) in the monolith.
                var response = await _entityService.UpdateField(entityId, field);

                // Clear metadata cache after mutation.
                _entityService.ClearCache();

                // Publish SNS domain event.
                if (response.Success && response.Object != null)
                {
                    await PublishDomainEvent(
                        "entity-management.field.updated",
                        new
                        {
                            entityId,
                            fieldId = response.Object.Id,
                            fieldName = response.Object.Name,
                            patchOperation = true
                        },
                        correlationId);
                }

                var statusCode = response.Success
                    ? (int)HttpStatusCode.OK
                    : (int)response.StatusCode;

                _logger.LogInformation(
                    "PatchField completed. Success={Success}, EntityId={EntityId}, FieldId={FieldId}, CorrelationId={CorrelationId}",
                    response.Success, entityId, fieldId, correlationId);

                return BuildResponse(statusCode, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PatchField unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while patching the field.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for DELETE /v1/entity-management/meta/entities/{entityId}/fields/{fieldId}
        /// Deletes a field definition from the specified entity.
        /// Source: WebApiController.DeleteField (lines 1984-2000)
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request with entityId and fieldId path parameters.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>APIGatewayHttpApiV2ProxyResponse with FieldResponse envelope (200 OK or error).</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> DeleteField(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "DeleteField started. CorrelationId={CorrelationId}, FunctionName={FunctionName}",
                correlationId, context.FunctionName);

            try
            {
                // Permission gating: admin role required for all field mutations.
                if (!IsAdminUser(request))
                {
                    _logger.LogWarning(
                        "DeleteField access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator role required.",
                        correlationId);
                }

                // Extract and validate entity ID.
                var entityIdStr = GetParam(request, "entityId");
                if (string.IsNullOrWhiteSpace(entityIdStr) || !Guid.TryParse(entityIdStr, out var entityId))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid or missing entity ID in route. A valid GUID is required.",
                        correlationId);
                }

                // Extract and validate field ID.
                var fieldIdStr = GetParam(request, "fieldId");
                if (string.IsNullOrWhiteSpace(fieldIdStr) || !Guid.TryParse(fieldIdStr, out var fieldId))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid or missing field ID in route. A valid GUID is required.",
                        correlationId);
                }

                // Delegate to EntityService.DeleteField which handles validation and persistence.
                // Source: EntityManager.DeleteField(entityId, fieldId)
                var response = await _entityService.DeleteField(entityId, fieldId);

                // Clear metadata cache after mutation.
                _entityService.ClearCache();

                // Publish SNS domain event.
                if (response.Success)
                {
                    await PublishDomainEvent(
                        "entity-management.field.deleted",
                        new
                        {
                            entityId,
                            fieldId
                        },
                        correlationId);
                }

                var statusCode = response.Success
                    ? (int)HttpStatusCode.OK
                    : (int)response.StatusCode;

                _logger.LogInformation(
                    "DeleteField completed. Success={Success}, EntityId={EntityId}, FieldId={FieldId}, CorrelationId={CorrelationId}",
                    response.Success, entityId, fieldId, correlationId);

                return BuildResponse(statusCode, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DeleteField unhandled exception. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.ToString() : "An unexpected error occurred while deleting the field.",
                    correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // PRIVATE HELPER METHODS — PER-FIELD-TYPE PATCH LOGIC
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a new InputField of the correct concrete type and selectively copies
        /// only the submitted properties from the deserialized inputField.
        /// This is the core of the PATCH operation — it implements per-field-type property merging.
        /// Source: WebApiController.PatchField per-field-type switch (lines 1744-1960).
        /// </summary>
        /// <param name="fieldType">The resolved FieldType enum for this field.</param>
        /// <param name="inputField">The fully deserialized InputField from the request body.</param>
        /// <param name="submitObj">The raw JObject for checking which properties were actually submitted.</param>
        /// <returns>A new InputField instance with only submitted type-specific properties applied.</returns>
        private static InputField ApplyPatchProperties(
            FieldType fieldType, InputField inputField, JObject submitObj)
        {
            // Build a set of submitted property names (case-insensitive) for quick lookup.
            var submittedProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in submitObj.Properties())
            {
                submittedProps.Add(prop.Name);
            }

            // Helper to check if a specific property was submitted in the request body.
            bool HasProp(string name) => submittedProps.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));

            switch (fieldType)
            {
                case FieldType.AutoNumberField:
                {
                    var field = new InputAutoNumberField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputAutoNumberField)inputField).DefaultValue;
                    if (HasProp("displayFormat"))
                        field.DisplayFormat = ((InputAutoNumberField)inputField).DisplayFormat;
                    if (HasProp("startingNumber"))
                        field.StartingNumber = ((InputAutoNumberField)inputField).StartingNumber;
                    return field;
                }
                case FieldType.CheckboxField:
                {
                    var field = new InputCheckboxField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputCheckboxField)inputField).DefaultValue;
                    return field;
                }
                case FieldType.CurrencyField:
                {
                    var field = new InputCurrencyField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputCurrencyField)inputField).DefaultValue;
                    if (HasProp("minValue"))
                        field.MinValue = ((InputCurrencyField)inputField).MinValue;
                    if (HasProp("maxValue"))
                        field.MaxValue = ((InputCurrencyField)inputField).MaxValue;
                    if (HasProp("currency"))
                        field.Currency = ((InputCurrencyField)inputField).Currency;
                    return field;
                }
                case FieldType.DateField:
                {
                    var field = new InputDateField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputDateField)inputField).DefaultValue;
                    if (HasProp("format"))
                        field.Format = ((InputDateField)inputField).Format;
                    if (HasProp("useCurrentTimeAsDefaultValue"))
                        field.UseCurrentTimeAsDefaultValue = ((InputDateField)inputField).UseCurrentTimeAsDefaultValue;
                    return field;
                }
                case FieldType.DateTimeField:
                {
                    var field = new InputDateTimeField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputDateTimeField)inputField).DefaultValue;
                    if (HasProp("format"))
                        field.Format = ((InputDateTimeField)inputField).Format;
                    if (HasProp("useCurrentTimeAsDefaultValue"))
                        field.UseCurrentTimeAsDefaultValue = ((InputDateTimeField)inputField).UseCurrentTimeAsDefaultValue;
                    return field;
                }
                case FieldType.EmailField:
                {
                    var field = new InputEmailField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputEmailField)inputField).DefaultValue;
                    if (HasProp("maxLength"))
                        field.MaxLength = ((InputEmailField)inputField).MaxLength;
                    return field;
                }
                case FieldType.FileField:
                {
                    var field = new InputFileField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputFileField)inputField).DefaultValue;
                    return field;
                }
                case FieldType.HtmlField:
                {
                    var field = new InputHtmlField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputHtmlField)inputField).DefaultValue;
                    return field;
                }
                case FieldType.ImageField:
                {
                    var field = new InputImageField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputImageField)inputField).DefaultValue;
                    return field;
                }
                case FieldType.MultiLineTextField:
                {
                    var field = new InputMultiLineTextField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputMultiLineTextField)inputField).DefaultValue;
                    if (HasProp("maxLength"))
                        field.MaxLength = ((InputMultiLineTextField)inputField).MaxLength;
                    if (HasProp("visibleLineNumber"))
                        field.VisibleLineNumber = ((InputMultiLineTextField)inputField).VisibleLineNumber;
                    return field;
                }
                case FieldType.GeographyField:
                {
                    var field = new InputGeographyField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputGeographyField)inputField).DefaultValue;
                    if (HasProp("maxLength"))
                        field.MaxLength = ((InputGeographyField)inputField).MaxLength;
                    if (HasProp("visibleLineNumber"))
                        field.VisibleLineNumber = ((InputGeographyField)inputField).VisibleLineNumber;
                    if (HasProp("format"))
                        field.Format = ((InputGeographyField)inputField).Format;
                    if (HasProp("srid"))
                        field.SRID = ((InputGeographyField)inputField).SRID;
                    return field;
                }
                case FieldType.MultiSelectField:
                {
                    var field = new InputMultiSelectField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputMultiSelectField)inputField).DefaultValue;
                    if (HasProp("options"))
                        field.Options = ((InputMultiSelectField)inputField).Options;
                    return field;
                }
                case FieldType.NumberField:
                {
                    var field = new InputNumberField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputNumberField)inputField).DefaultValue;
                    if (HasProp("minValue"))
                        field.MinValue = ((InputNumberField)inputField).MinValue;
                    if (HasProp("maxValue"))
                        field.MaxValue = ((InputNumberField)inputField).MaxValue;
                    if (HasProp("decimalPlaces"))
                        field.DecimalPlaces = ((InputNumberField)inputField).DecimalPlaces;
                    return field;
                }
                case FieldType.PasswordField:
                {
                    var field = new InputPasswordField();
                    if (HasProp("maxLength"))
                        field.MaxLength = ((InputPasswordField)inputField).MaxLength;
                    if (HasProp("minLength"))
                        field.MinLength = ((InputPasswordField)inputField).MinLength;
                    if (HasProp("encrypted"))
                        field.Encrypted = ((InputPasswordField)inputField).Encrypted;
                    return field;
                }
                case FieldType.PercentField:
                {
                    var field = new InputPercentField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputPercentField)inputField).DefaultValue;
                    if (HasProp("minValue"))
                        field.MinValue = ((InputPercentField)inputField).MinValue;
                    if (HasProp("maxValue"))
                        field.MaxValue = ((InputPercentField)inputField).MaxValue;
                    if (HasProp("decimalPlaces"))
                        field.DecimalPlaces = ((InputPercentField)inputField).DecimalPlaces;
                    return field;
                }
                case FieldType.PhoneField:
                {
                    var field = new InputPhoneField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputPhoneField)inputField).DefaultValue;
                    if (HasProp("format"))
                        field.Format = ((InputPhoneField)inputField).Format;
                    if (HasProp("maxLength"))
                        field.MaxLength = ((InputPhoneField)inputField).MaxLength;
                    return field;
                }
                case FieldType.GuidField:
                {
                    var field = new InputGuidField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputGuidField)inputField).DefaultValue;
                    if (HasProp("generateNewId"))
                        field.GenerateNewId = ((InputGuidField)inputField).GenerateNewId;
                    return field;
                }
                case FieldType.SelectField:
                {
                    var field = new InputSelectField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputSelectField)inputField).DefaultValue;
                    if (HasProp("options"))
                        field.Options = ((InputSelectField)inputField).Options;
                    return field;
                }
                case FieldType.TextField:
                {
                    var field = new InputTextField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputTextField)inputField).DefaultValue;
                    if (HasProp("maxLength"))
                        field.MaxLength = ((InputTextField)inputField).MaxLength;
                    return field;
                }
                case FieldType.UrlField:
                {
                    var field = new InputUrlField();
                    if (HasProp("defaultValue"))
                        field.DefaultValue = ((InputUrlField)inputField).DefaultValue;
                    if (HasProp("maxLength"))
                        field.MaxLength = ((InputUrlField)inputField).MaxLength;
                    if (HasProp("openTargetInNewWindow"))
                        field.OpenTargetInNewWindow = ((InputUrlField)inputField).OpenTargetInNewWindow;
                    return field;
                }
                default:
                    throw new InvalidOperationException($"Unsupported field type '{fieldType}' for PATCH operation.");
            }
        }

        /// <summary>
        /// Applies common InputField base class properties that were submitted in the PATCH request.
        /// These properties exist on all field types: label, placeholderText, description, helpText,
        /// required, unique, searchable, auditable, system, enableSecurity, permissions.
        /// Source: WebApiController.PatchField common-property block (lines 1952-1975).
        /// </summary>
        /// <param name="field">The target InputField to apply common properties to.</param>
        /// <param name="inputField">The deserialized InputField containing property values.</param>
        /// <param name="submitObj">The raw JObject for checking which properties were submitted.</param>
        private static void ApplyCommonPatchProperties(InputField field, InputField inputField, JObject submitObj)
        {
            foreach (var prop in submitObj.Properties())
            {
                var propName = prop.Name.ToLower();

                switch (propName)
                {
                    case "label":
                        field.Label = inputField.Label;
                        break;
                    case "placeholdertext":
                        field.PlaceholderText = inputField.PlaceholderText;
                        break;
                    case "description":
                        field.Description = inputField.Description;
                        break;
                    case "helptext":
                        field.HelpText = inputField.HelpText;
                        break;
                    case "required":
                        field.Required = inputField.Required;
                        break;
                    case "unique":
                        field.Unique = inputField.Unique;
                        break;
                    case "searchable":
                        field.Searchable = inputField.Searchable;
                        break;
                    case "auditable":
                        field.Auditable = inputField.Auditable;
                        break;
                    case "system":
                        field.System = inputField.System;
                        break;
                    case "enablesecurity":
                        field.EnableSecurity = inputField.EnableSecurity;
                        break;
                    case "permissions":
                        field.Permissions = inputField.Permissions;
                        break;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // PRIVATE HELPER METHODS — UTILITY FUNCTIONS
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extracts a required path parameter from the request.
        /// Returns empty string if the parameter is not found.
        /// Pattern: Matches EntityHandler.GetParam() implementation.
        /// </summary>
        private static string GetParam(APIGatewayHttpApiV2ProxyRequest request, string key)
        {
            if (request.PathParameters != null &&
                request.PathParameters.TryGetValue(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            return string.Empty;
        }

        /// <summary>
        /// Extracts an optional path parameter from the request.
        /// Returns null if the parameter is not found.
        /// Pattern: Matches EntityHandler.GetParamOrNull() implementation.
        /// </summary>
        private static string? GetParamOrNull(APIGatewayHttpApiV2ProxyRequest request, string key)
        {
            if (request.PathParameters != null &&
                request.PathParameters.TryGetValue(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            return null;
        }

        /// <summary>
        /// Checks whether the authenticated user has the administrator role.
        /// Inspects JWT claims from the API Gateway HTTP API v2 authorizer:
        ///   - cognito:groups claim for "administrator"
        ///   - custom:roles claim for AdminRoleId GUID
        ///   - scope claim for "administrator"
        ///   - Lambda authorizer context for "isAdmin" or "roles" containing admin
        /// Source: Replaces SecurityContext.HasMetaPermission() from monolith.
        /// Pattern: Matches EntityHandler.IsAdminUser() implementation.
        /// </summary>
        private static bool IsAdminUser(APIGatewayHttpApiV2ProxyRequest request)
        {
            try
            {
                var authorizer = request.RequestContext?.Authorizer;
                if (authorizer == null)
                    return false;

                // Check JWT claims from Cognito authorizer
                if (authorizer.Jwt?.Claims != null)
                {
                    var claims = authorizer.Jwt.Claims;

                    // Check cognito:groups for "administrator"
                    if (claims.TryGetValue("cognito:groups", out var groups) &&
                        !string.IsNullOrWhiteSpace(groups))
                    {
                        if (groups.Contains("administrator", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    // Check custom:roles for admin role GUID
                    if (claims.TryGetValue("custom:roles", out var roles) &&
                        !string.IsNullOrWhiteSpace(roles))
                    {
                        if (roles.Contains(SystemIds.AdministratorRoleId.ToString(), StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    // Check scope claim for "administrator"
                    if (claims.TryGetValue("scope", out var scope) &&
                        !string.IsNullOrWhiteSpace(scope))
                    {
                        if (scope.Contains("administrator", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }

                // Check Lambda authorizer context (fallback for LocalStack custom authorizer)
                if (authorizer.Lambda != null)
                {
                    if (authorizer.Lambda.TryGetValue("isAdmin", out var isAdmin))
                    {
                        if (isAdmin is bool boolVal && boolVal)
                            return true;
                        if (isAdmin is string strVal &&
                            strVal.Equals("true", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    if (authorizer.Lambda.TryGetValue("roles", out var lambdaRoles))
                    {
                        var rolesStr = lambdaRoles?.ToString();
                        if (!string.IsNullOrWhiteSpace(rolesStr) &&
                            rolesStr.Contains("administrator", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }

                return false;
            }
            catch
            {
                // If authorizer data is malformed, deny access
                return false;
            }
        }

        /// <summary>
        /// Extracts the correlation ID from the request headers.
        /// Falls back to the AWS Lambda request ID if no custom correlation ID is present.
        /// Pattern: Matches EntityHandler.ExtractCorrelationId() implementation.
        /// </summary>
        private static string ExtractCorrelationId(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            if (request.Headers != null)
            {
                // Check for x-correlation-id header (case-insensitive lookup)
                foreach (var header in request.Headers)
                {
                    if (header.Key.Equals("x-correlation-id", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(header.Value))
                    {
                        return header.Value;
                    }
                }
            }

            // Fall back to Lambda request ID
            return context.AwsRequestId ?? Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Builds a standardized HTTP API v2 proxy response with JSON body serialization,
        /// Content-Type header, and correlation ID propagation.
        /// Pattern: Matches EntityHandler.BuildResponse() implementation.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse BuildResponse(int statusCode, object body, string correlationId)
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
        /// Builds a standardized error response using the BaseResponseModel envelope.
        /// Includes optional error list for detailed validation failure reporting.
        /// Pattern: Matches EntityHandler.BuildErrorResponse() implementation.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(
            int statusCode, string message, string correlationId, List<ErrorModel>? errors = null)
        {
            var response = new BaseResponseModel
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = message,
                StatusCode = (HttpStatusCode)statusCode,
                Errors = errors ?? new List<ErrorModel>()
            };

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = System.Text.Json.JsonSerializer.Serialize(response, _jsonOptions),
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "X-Correlation-Id", correlationId }
                }
            };
        }

        /// <summary>
        /// Publishes a domain event to the configured SNS topic for field lifecycle events.
        /// Uses Newtonsoft.Json for payload serialization (maintains compatibility with event consumers).
        /// Includes MessageAttributes for eventType, correlationId, and source for SQS filter policies.
        /// Errors during publishing are logged but do NOT fail the main operation (non-blocking).
        /// Source: Replaces monolith HookManager post-hook pattern.
        /// Pattern: Matches EntityHandler.PublishDomainEvent() implementation.
        /// </summary>
        private async Task PublishDomainEvent(string eventType, object eventData, string correlationId)
        {
            if (string.IsNullOrWhiteSpace(_fieldTopicArn))
            {
                _logger.LogWarning("Field topic ARN is not configured. Skipping domain event publish for {EventType}.", eventType);
                return;
            }

            try
            {
                var eventPayload = new
                {
                    EventType = eventType,
                    Timestamp = DateTime.UtcNow,
                    CorrelationId = correlationId,
                    Source = "entity-management",
                    Data = eventData
                };

                var publishRequest = new PublishRequest
                {
                    TopicArn = _fieldTopicArn,
                    Message = Newtonsoft.Json.JsonConvert.SerializeObject(eventPayload),
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
                        ["source"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = "entity-management"
                        }
                    }
                };

                await _snsClient.PublishAsync(publishRequest);
                _logger.LogInformation("Published domain event {EventType} with correlation {CorrelationId}.", eventType, correlationId);
            }
            catch (Exception ex)
            {
                // Non-blocking: log the error but do not fail the main operation
                _logger.LogError(ex, "Failed to publish domain event {EventType} with correlation {CorrelationId}.", eventType, correlationId);
            }
        }
    }
}
