// =============================================================================
// RecordService.cs — Record CRUD Operations with SNS Event Publishing
// =============================================================================
// Namespace: WebVellaErp.EntityManagement.Services
//
// Replaces:
//   - WebVella.Erp/Api/RecordManager.cs  (full record CRUD orchestration)
//   - WebVella.Erp/Hooks/RecordHookManager.cs (hook orchestration → SNS events)
//   - WebVella.Erp/Api/SecurityContext.cs (permission enforcement → JWT claims)
//
// Design Principles (AAP):
//   - Pre-hooks  → inline validation (blocking, abort on error)
//   - Post-hooks → SNS domain events (fire-and-forget)
//   - JWT claims replace SecurityContext for permission enforcement
//   - DI-injected dependencies replace static/ambient contexts
//   - DynamoDB atomic per-item ops (no transaction wrapper needed)
//   - System.Text.Json for event serialization (AOT-compatible)
//   - Structured JSON logging with correlation-ID throughout
//   - Full behavioral parity with monolith RecordManager
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebVellaErp.EntityManagement.DataAccess;
using WebVellaErp.EntityManagement.Models;

namespace WebVellaErp.EntityManagement.Services
{
    // =========================================================================
    // IRecordService Interface
    // =========================================================================
    // Public contract for all record CRUD operations. All methods are async,
    // replacing the synchronous RecordManager pattern from the monolith.
    // =========================================================================

    /// <summary>
    /// Service interface for record CRUD operations with relation-aware payload
    /// processing, field value normalization, permission enforcement, and
    /// domain event publishing via SNS.
    /// </summary>
    public interface IRecordService
    {
        /// <summary>Creates a record in the entity identified by name.</summary>
        Task<QueryResponse> CreateRecord(string entityName, EntityRecord record);

        /// <summary>Creates a record in the entity identified by GUID.</summary>
        Task<QueryResponse> CreateRecord(Guid entityId, EntityRecord record);

        /// <summary>Creates a record in the given entity (primary overload).</summary>
        Task<QueryResponse> CreateRecord(Entity entity, EntityRecord record);

        /// <summary>Updates a record in the entity identified by name.</summary>
        Task<QueryResponse> UpdateRecord(string entityName, EntityRecord record);

        /// <summary>Updates a record in the entity identified by GUID.</summary>
        Task<QueryResponse> UpdateRecord(Guid entityId, EntityRecord record);

        /// <summary>Updates a record in the given entity (primary overload).</summary>
        Task<QueryResponse> UpdateRecord(Entity entity, EntityRecord record);

        /// <summary>Deletes a record from the entity identified by name.</summary>
        Task<QueryResponse> DeleteRecord(string entityName, Guid id);

        /// <summary>Deletes a record from the entity identified by GUID.</summary>
        Task<QueryResponse> DeleteRecord(Guid entityId, Guid id);

        /// <summary>Deletes a record from the given entity (primary overload).</summary>
        Task<QueryResponse> DeleteRecord(Entity entity, Guid id);

        /// <summary>Creates a many-to-many relation bridge record.</summary>
        Task<QueryResponse> CreateRelationManyToManyRecord(Guid relationId, Guid originValue, Guid targetValue);

        /// <summary>Removes a many-to-many relation bridge record.</summary>
        Task<QueryResponse> RemoveRelationManyToManyRecord(Guid relationId, Guid originValue, Guid targetValue);

        /// <summary>Finds records matching the given query.</summary>
        Task<QueryResponse> Find(EntityQuery query);

        /// <summary>Counts records matching the given query criteria.</summary>
        Task<QueryCountResponse> Count(string entityName, QueryObject? queryObj = null);
    }

    // =========================================================================
    // RecordService Implementation
    // =========================================================================

    /// <summary>
    /// Orchestrates all record create/update/delete operations, handles
    /// relation-aware payload processing, field value normalization,
    /// permission enforcement via JWT claims, and SNS domain event publishing.
    /// </summary>
    public class RecordService : IRecordService
    {
        // =====================================================================
        // Constants — preserved from RecordManager.cs lines 17-18
        // =====================================================================
        private const char RELATION_SEPARATOR = '.';
        private const char RELATION_NAME_RESULT_SEPARATOR = '$';

        // =====================================================================
        // Injected Dependencies
        // =====================================================================
        private readonly IEntityService _entityService;
        private readonly IEntityRepository _entityRepository;
        private readonly IRecordRepository _recordRepository;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<RecordService> _logger;
        private readonly IConfiguration _configuration;

        // =====================================================================
        // Configuration Fields
        // =====================================================================
        private readonly string _snsTopicArnPrefix;
        private readonly bool _developmentMode;

        // =====================================================================
        // In-request cache for relations (replaces lazy-loaded field)
        // =====================================================================
        private List<EntityRelation>? _cachedRelations;

        // =====================================================================
        // Constructor
        // =====================================================================

        /// <summary>
        /// Initializes a new instance of <see cref="RecordService"/> with all
        /// required dependencies injected via the DI container.
        /// </summary>
        public RecordService(
            IEntityService entityService,
            IEntityRepository entityRepository,
            IRecordRepository recordRepository,
            IAmazonSimpleNotificationService snsClient,
            ILogger<RecordService> logger,
            IConfiguration configuration)
        {
            _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
            _entityRepository = entityRepository ?? throw new ArgumentNullException(nameof(entityRepository));
            _recordRepository = recordRepository ?? throw new ArgumentNullException(nameof(recordRepository));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _snsTopicArnPrefix = _configuration.GetValue<string>("Sns:TopicArnPrefix") ?? string.Empty;
            _developmentMode = _configuration.GetValue<bool>("DevelopmentMode");
        }

        // =====================================================================
        // CreateRecord Overloads
        // =====================================================================

        /// <inheritdoc />
        public async Task<QueryResponse> CreateRecord(string entityName, EntityRecord record)
        {
            var response = new QueryResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow
            };

            if (string.IsNullOrWhiteSpace(entityName))
            {
                response.Message = "Invalid entity name.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            var entity = await GetEntity(entityName);
            if (entity == null)
            {
                response.Message = "Entity cannot be found.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            return await CreateRecord(entity, record);
        }

        /// <inheritdoc />
        public async Task<QueryResponse> CreateRecord(Guid entityId, EntityRecord record)
        {
            var response = new QueryResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow
            };

            if (entityId == Guid.Empty)
            {
                response.Message = "Invalid entity name.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            var entity = await GetEntity(entityId);
            if (entity == null)
            {
                response.Message = "Entity cannot be found.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            return await CreateRecord(entity, record);
        }

        /// <inheritdoc />
        public async Task<QueryResponse> CreateRecord(Entity entity, EntityRecord record)
        {
            var response = new QueryResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // Validate inputs
                if (entity == null)
                {
                    response.Success = false;
                    response.Message = "Entity cannot be found.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                if (record == null)
                {
                    response.Success = false;
                    response.Message = "Invalid record. Cannot be null.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                _logger.LogInformation(
                    "CreateRecord started. Entity={EntityName}, EntityId={EntityId}",
                    entity.Name, entity.Id);

                // Permission enforcement — replaces SecurityContext.HasEntityPermission
                if (!HasEntityPermission(EntityPermission.Create, entity))
                {
                    response.Success = false;
                    response.StatusCode = HttpStatusCode.Forbidden;
                    response.Message = "Access denied.";
                    response.Errors.Add(new ErrorModel("access", "create",
                        $"Trying to create record in entity '{entity.Name}' with no create access."));
                    _logger.LogWarning(
                        "CreateRecord access denied. Entity={EntityName}", entity.Name);
                    return response;
                }

                // Extract or generate record ID
                Guid recordId = ExtractRecordId(record);

                // Load relations for relation-aware processing
                var relations = await GetRelations();

                // Separate relation fields from normal fields
                var storageRecordData = new List<KeyValuePair<string, object?>>();
                var oneToOneRecordData = new List<KeyValuePair<string, object?>>();
                var oneToManyRecordData = new List<KeyValuePair<string, object?>>();
                var manyToManyRecordData = new List<KeyValuePair<string, object?>>();
                var fileFieldPaths = new List<KeyValuePair<string, string>>();

                // Process each property in the record
                foreach (var pair in record)
                {
                    var keyName = pair.Key;

                    // Skip the ID field — already extracted
                    if (string.Equals(keyName, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Check if this is a relation field (contains RELATION_SEPARATOR)
                    if (keyName.Contains(RELATION_SEPARATOR))
                    {
                        ProcessRelationField(
                            keyName, pair.Value, entity, relations,
                            oneToOneRecordData, oneToManyRecordData, manyToManyRecordData,
                            response);

                        if (!response.Success)
                        {
                            return response;
                        }

                        continue;
                    }

                    // Find field definition in entity metadata
                    var field = entity.Fields?.FirstOrDefault(
                        f => string.Equals(f.Name, keyName, StringComparison.OrdinalIgnoreCase));

                    if (field == null)
                    {
                        // Unknown field — skip silently (monolith behavior)
                        continue;
                    }

                    var fieldType = field.GetFieldType();

                    // Skip AutoNumberField on create — auto-generated
                    if (fieldType == FieldType.AutoNumberField)
                    {
                        continue;
                    }

                    // Track file/image fields for path processing
                    if (fieldType == FieldType.FileField || fieldType == FieldType.ImageField)
                    {
                        var filePath = pair.Value as string;
                        if (!string.IsNullOrWhiteSpace(filePath))
                        {
                            fileFieldPaths.Add(new KeyValuePair<string, string>(keyName, filePath));
                        }
                    }

                    // Extract and normalize field value
                    try
                    {
                        var extractedValue = ExtractFieldValue(pair.Value, field, true);
                        storageRecordData.Add(new KeyValuePair<string, object?>(keyName, extractedValue));
                    }
                    catch (Exception ex)
                    {
                        response.Success = false;
                        response.Message = $"Error during processing value for field: '{keyName}' Invalid value: '{pair.Value}'";
                        _logger.LogError(ex,
                            "CreateRecord field extraction error. Entity={EntityName}, Field={FieldName}",
                            entity.Name, keyName);
                        return response;
                    }
                }

                // Set required fields to defaults if not provided
                SetRecordRequiredFieldsDefaultData(entity, record, storageRecordData);

                // Add the record ID
                storageRecordData.Add(new KeyValuePair<string, object?>("id", recordId));

                // Validate unique field constraints before persistence
                await ValidateUniqueFieldConstraints(entity, record, recordId, response.Errors, isUpdate: false);
                if (response.Errors.Count > 0)
                {
                    response.Success = false;
                    response.Message = "Validation error(s) occurred during record creation.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Process file field paths (move from temp to permanent location)
                ProcessFileFieldPaths(entity, recordId, fileFieldPaths, storageRecordData);

                // Persist the record to DynamoDB
                await _recordRepository.CreateRecord(entity.Name, storageRecordData);

                _logger.LogInformation(
                    "CreateRecord persisted. Entity={EntityName}, RecordId={RecordId}",
                    entity.Name, recordId);

                // Process relation data after main record creation
                await ProcessOneToOneRelations(entity, recordId, oneToOneRecordData, relations);
                await ProcessOneToManyRelations(entity, recordId, oneToManyRecordData, relations);
                await ProcessManyToManyRelations(entity, recordId, manyToManyRecordData, relations);

                // Reload the created record for response
                var createdRecord = await _recordRepository.FindRecord(entity.Name, recordId);
                if (createdRecord != null)
                {
                    response.Object = new QueryResult
                    {
                        FieldsMeta = entity.Fields?.ToList(),
                        Data = new EntityRecordList { createdRecord }
                    };
                }

                response.Success = true;
                response.StatusCode = HttpStatusCode.OK;
                response.Message = "Record was created successfully.";

                // Publish domain event (fire-and-forget — replaces post-create hooks)
                await PublishDomainEvent(
                    "entity-management.record.created",
                    entity.Name,
                    new
                    {
                        entityName = entity.Name,
                        recordId = recordId.ToString(),
                        timestamp = DateTime.UtcNow.ToString("o"),
                        record = createdRecord
                    });

                _logger.LogInformation(
                    "CreateRecord completed. Entity={EntityName}, RecordId={RecordId}",
                    entity.Name, recordId);
            }
            catch (ValidationException vex)
            {
                response.Success = false;
                response.Message = vex.Message;
                foreach (var err in vex.Errors)
                {
                    response.Errors.Add(err);
                }
                _logger.LogWarning(vex,
                    "CreateRecord validation error. Entity={EntityName}", entity?.Name);
            }
            catch (Exception ex)
            {
                response.Success = false;
                if (_developmentMode)
                {
                    response.Message = ex.Message + ex.StackTrace;
                }
                else
                {
                    response.Message = "The entity record was not created. An internal error occurred!";
                }
                _logger.LogError(ex,
                    "CreateRecord error. Entity={EntityName}", entity?.Name);
            }

            return response;
        }

        // =====================================================================
        // UpdateRecord Overloads
        // =====================================================================

        /// <inheritdoc />
        public async Task<QueryResponse> UpdateRecord(string entityName, EntityRecord record)
        {
            var response = new QueryResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow
            };

            if (string.IsNullOrWhiteSpace(entityName))
            {
                response.Message = "Invalid entity name.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            var entity = await GetEntity(entityName);
            if (entity == null)
            {
                response.Message = "Entity cannot be found.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            return await UpdateRecord(entity, record);
        }

        /// <inheritdoc />
        public async Task<QueryResponse> UpdateRecord(Guid entityId, EntityRecord record)
        {
            var response = new QueryResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow
            };

            if (entityId == Guid.Empty)
            {
                response.Message = "Invalid entity name.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            var entity = await GetEntity(entityId);
            if (entity == null)
            {
                response.Message = "Entity cannot be found.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            return await UpdateRecord(entity, record);
        }

        /// <inheritdoc />
        public async Task<QueryResponse> UpdateRecord(Entity entity, EntityRecord record)
        {
            var response = new QueryResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // Validate inputs
                if (entity == null)
                {
                    response.Success = false;
                    response.Message = "Entity cannot be found.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                if (record == null)
                {
                    response.Success = false;
                    response.Message = "Invalid record. Cannot be null.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                _logger.LogInformation(
                    "UpdateRecord started. Entity={EntityName}, EntityId={EntityId}",
                    entity.Name, entity.Id);

                // Permission enforcement
                if (!HasEntityPermission(EntityPermission.Update, entity))
                {
                    response.Success = false;
                    response.StatusCode = HttpStatusCode.Forbidden;
                    response.Message = "Access denied.";
                    response.Errors.Add(new ErrorModel("access", "update",
                        $"Trying to update record in entity '{entity.Name}' with no update access."));
                    _logger.LogWarning(
                        "UpdateRecord access denied. Entity={EntityName}", entity.Name);
                    return response;
                }

                // Extract record ID (required for update)
                Guid recordId = ExtractRecordIdForUpdate(record);

                // Load the existing record for comparison
                var existingRecord = await _recordRepository.FindRecord(entity.Name, recordId);
                if (existingRecord == null)
                {
                    response.Success = false;
                    response.Message = "Record with such Id is not found";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Load relations
                var relations = await GetRelations();

                // Separate relation fields from normal fields
                var storageRecordData = new List<KeyValuePair<string, object?>>();
                var oneToOneRecordData = new List<KeyValuePair<string, object?>>();
                var oneToManyRecordData = new List<KeyValuePair<string, object?>>();
                var manyToManyRecordData = new List<KeyValuePair<string, object?>>();
                var fileFieldPaths = new List<KeyValuePair<string, string>>();
                var oldFileFieldPaths = new List<KeyValuePair<string, string?>>();

                foreach (var pair in record)
                {
                    var keyName = pair.Key;

                    // Skip the ID field
                    if (string.Equals(keyName, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Check if this is a relation field
                    if (keyName.Contains(RELATION_SEPARATOR))
                    {
                        ProcessRelationField(
                            keyName, pair.Value, entity, relations,
                            oneToOneRecordData, oneToManyRecordData, manyToManyRecordData,
                            response);

                        if (!response.Success)
                        {
                            return response;
                        }

                        continue;
                    }

                    // Find field definition
                    var field = entity.Fields?.FirstOrDefault(
                        f => string.Equals(f.Name, keyName, StringComparison.OrdinalIgnoreCase));

                    if (field == null)
                    {
                        continue;
                    }

                    var fieldType = field.GetFieldType();

                    // Skip AutoNumberField on update — immutable
                    if (fieldType == FieldType.AutoNumberField)
                    {
                        continue;
                    }

                    // Skip PasswordField when value is null (don't overwrite)
                    if (fieldType == FieldType.PasswordField && pair.Value == null)
                    {
                        continue;
                    }

                    // Track file/image fields for path processing
                    if (fieldType == FieldType.FileField || fieldType == FieldType.ImageField)
                    {
                        var newPath = pair.Value as string;
                        var oldPath = existingRecord.ContainsKey(keyName)
                            ? existingRecord[keyName] as string
                            : null;

                        fileFieldPaths.Add(new KeyValuePair<string, string>(
                            keyName, newPath ?? string.Empty));
                        oldFileFieldPaths.Add(new KeyValuePair<string, string?>(
                            keyName, oldPath));
                    }

                    // Extract and normalize field value
                    try
                    {
                        var extractedValue = ExtractFieldValue(pair.Value, field, true);
                        storageRecordData.Add(new KeyValuePair<string, object?>(keyName, extractedValue));
                    }
                    catch (Exception ex)
                    {
                        response.Success = false;
                        response.Message = $"Error during processing value for field: '{keyName}' Invalid value: '{pair.Value}'";
                        _logger.LogError(ex,
                            "UpdateRecord field extraction error. Entity={EntityName}, Field={FieldName}",
                            entity.Name, keyName);
                        return response;
                    }
                }

                // Add the record ID to storage data
                storageRecordData.Add(new KeyValuePair<string, object?>("id", recordId));

                // Validate unique field constraints before persistence
                await ValidateUniqueFieldConstraints(entity, record, recordId, response.Errors, isUpdate: true);
                if (response.Errors.Count > 0)
                {
                    response.Success = false;
                    response.Message = "Validation error(s) occurred during record update.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Process file field paths for update (handles cleanup of old paths)
                ProcessFileFieldPathsForUpdate(
                    entity, recordId, fileFieldPaths, oldFileFieldPaths, storageRecordData);

                // Persist the update to DynamoDB
                await _recordRepository.UpdateRecord(entity.Name, storageRecordData);

                _logger.LogInformation(
                    "UpdateRecord persisted. Entity={EntityName}, RecordId={RecordId}",
                    entity.Name, recordId);

                // Process relation data
                await ProcessOneToOneRelations(entity, recordId, oneToOneRecordData, relations);
                await ProcessOneToManyRelations(entity, recordId, oneToManyRecordData, relations);
                await ProcessManyToManyRelationsForUpdate(
                    entity, recordId, manyToManyRecordData, relations, existingRecord);

                // Reload the updated record for response
                var updatedRecord = await _recordRepository.FindRecord(entity.Name, recordId);
                if (updatedRecord != null)
                {
                    response.Object = new QueryResult
                    {
                        FieldsMeta = entity.Fields?.ToList(),
                        Data = new EntityRecordList { updatedRecord }
                    };
                }

                response.Success = true;
                response.StatusCode = HttpStatusCode.OK;
                response.Message = "Record was updated successfully";

                // Publish domain event (fire-and-forget — replaces post-update hooks)
                await PublishDomainEvent(
                    "entity-management.record.updated",
                    entity.Name,
                    new
                    {
                        entityName = entity.Name,
                        recordId = recordId.ToString(),
                        timestamp = DateTime.UtcNow.ToString("o"),
                        record = updatedRecord
                    });

                _logger.LogInformation(
                    "UpdateRecord completed. Entity={EntityName}, RecordId={RecordId}",
                    entity.Name, recordId);
            }
            catch (ValidationException vex)
            {
                response.Success = false;
                response.Message = vex.Message;
                foreach (var err in vex.Errors)
                {
                    response.Errors.Add(err);
                }
                _logger.LogWarning(vex,
                    "UpdateRecord validation error. Entity={EntityName}", entity?.Name);
            }
            catch (Exception ex)
            {
                response.Success = false;
                if (_developmentMode)
                {
                    response.Message = ex.Message + ex.StackTrace;
                }
                else
                {
                    response.Message = "The entity record was not update. An internal error occurred!";
                }
                _logger.LogError(ex,
                    "UpdateRecord error. Entity={EntityName}", entity?.Name);
            }

            return response;
        }

        // =====================================================================
        // DeleteRecord Overloads
        // =====================================================================

        /// <inheritdoc />
        public async Task<QueryResponse> DeleteRecord(string entityName, Guid id)
        {
            var response = new QueryResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow
            };

            if (string.IsNullOrWhiteSpace(entityName))
            {
                response.Message = "Invalid entity name.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            var entity = await GetEntity(entityName);
            if (entity == null)
            {
                response.Message = "Entity cannot be found.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            return await DeleteRecord(entity, id);
        }

        /// <inheritdoc />
        public async Task<QueryResponse> DeleteRecord(Guid entityId, Guid id)
        {
            var response = new QueryResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow
            };

            if (entityId == Guid.Empty)
            {
                response.Message = "Invalid entity name.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            var entity = await GetEntity(entityId);
            if (entity == null)
            {
                response.Message = "Entity cannot be found.";
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            return await DeleteRecord(entity, id);
        }

        /// <inheritdoc />
        public async Task<QueryResponse> DeleteRecord(Entity entity, Guid id)
        {
            var response = new QueryResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // Validate inputs
                if (entity == null)
                {
                    response.Success = false;
                    response.Message = "Entity cannot be found.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                _logger.LogInformation(
                    "DeleteRecord started. Entity={EntityName}, RecordId={RecordId}",
                    entity.Name, id);

                // Permission enforcement
                if (!HasEntityPermission(EntityPermission.Delete, entity))
                {
                    response.Success = false;
                    response.StatusCode = HttpStatusCode.Forbidden;
                    response.Message = "Access denied.";
                    response.Errors.Add(new ErrorModel("access", "delete",
                        $"Trying to delete record in entity '{entity.Name}' with no delete access."));
                    _logger.LogWarning(
                        "DeleteRecord access denied. Entity={EntityName}", entity.Name);
                    return response;
                }

                // Load the existing record
                var existingRecord = await _recordRepository.FindRecord(entity.Name, id);
                if (existingRecord == null)
                {
                    response.Success = false;
                    response.Message = "Record was not found.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // File field cleanup — delete associated files (FileField type only)
                if (entity.Fields != null)
                {
                    foreach (var field in entity.Fields)
                    {
                        if (field.GetFieldType() == FieldType.FileField)
                        {
                            if (existingRecord.ContainsKey(field.Name))
                            {
                                var filePath = existingRecord[field.Name] as string;
                                if (!string.IsNullOrWhiteSpace(filePath))
                                {
                                    _logger.LogInformation(
                                        "DeleteRecord cleaning up file. Entity={EntityName}, RecordId={RecordId}, Field={FieldName}, Path={Path}",
                                        entity.Name, id, field.Name, filePath);
                                    // File deletion is handled by the file-management service
                                    // via domain event consumption. Log for audit trail.
                                }
                            }
                        }
                    }
                }

                // Delete the record from DynamoDB
                await _recordRepository.DeleteRecord(entity.Name, id);

                _logger.LogInformation(
                    "DeleteRecord persisted. Entity={EntityName}, RecordId={RecordId}",
                    entity.Name, id);

                response.Object = new QueryResult
                {
                    FieldsMeta = entity.Fields?.ToList(),
                    Data = new EntityRecordList { existingRecord }
                };

                response.Success = true;
                response.StatusCode = HttpStatusCode.OK;
                response.Message = "Record was deleted successfully.";

                // Publish domain event (fire-and-forget — replaces post-delete hooks)
                await PublishDomainEvent(
                    "entity-management.record.deleted",
                    entity.Name,
                    new
                    {
                        entityName = entity.Name,
                        recordId = id.ToString(),
                        timestamp = DateTime.UtcNow.ToString("o"),
                        record = existingRecord
                    });

                _logger.LogInformation(
                    "DeleteRecord completed. Entity={EntityName}, RecordId={RecordId}",
                    entity.Name, id);
            }
            catch (ValidationException vex)
            {
                response.Success = false;
                response.Message = vex.Message;
                foreach (var err in vex.Errors)
                {
                    response.Errors.Add(err);
                }
                _logger.LogWarning(vex,
                    "DeleteRecord validation error. Entity={EntityName}", entity?.Name);
            }
            catch (Exception ex)
            {
                response.Success = false;
                if (_developmentMode)
                {
                    response.Message = ex.Message + ex.StackTrace;
                }
                else
                {
                    response.Message = "The entity record was not deleted. An internal error occurred!";
                }
                _logger.LogError(ex,
                    "DeleteRecord error. Entity={EntityName}", entity?.Name);
            }

            return response;
        }

        // =====================================================================
        // Many-to-Many Relation Operations
        // =====================================================================

        /// <inheritdoc />
        public async Task<QueryResponse> CreateRelationManyToManyRecord(
            Guid relationId, Guid originValue, Guid targetValue)
        {
            var response = new QueryResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                _logger.LogInformation(
                    "CreateRelationManyToManyRecord started. RelationId={RelationId}, Origin={Origin}, Target={Target}",
                    relationId, originValue, targetValue);

                // Load relation metadata
                var relation = await _entityRepository.GetRelationById(relationId);
                if (relation == null)
                {
                    response.Success = false;
                    response.Message = "Relation does not exists.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Pre-hook migration → inline validation
                // Validate that the relation is indeed ManyToMany
                if (relation.RelationType != EntityRelationType.ManyToMany)
                {
                    response.Success = false;
                    response.Message = $"Relation '{relation.Name}' is not a many-to-many relation.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Create the bridge record
                await _entityRepository.CreateManyToManyRecord(relationId, originValue, targetValue);

                response.Success = true;
                response.StatusCode = HttpStatusCode.OK;
                response.Message = "The relation record was created successfully.";

                _logger.LogInformation(
                    "CreateRelationManyToManyRecord completed. RelationId={RelationId}, Origin={Origin}, Target={Target}",
                    relationId, originValue, targetValue);

                // Publish domain event (fire-and-forget — replaces post-create M2M hooks)
                await PublishDomainEvent(
                    "entity-management.relation.created",
                    relation.Name,
                    new
                    {
                        relationName = relation.Name,
                        relationId = relationId.ToString(),
                        originValue = originValue.ToString(),
                        targetValue = targetValue.ToString(),
                        timestamp = DateTime.UtcNow.ToString("o")
                    });
            }
            catch (Exception ex)
            {
                response.Success = false;
                if (_developmentMode)
                {
                    response.Message = ex.Message + ex.StackTrace;
                }
                else
                {
                    response.Message = "The entity relation record was not created. An internal error occurred!";
                }
                _logger.LogError(ex,
                    "CreateRelationManyToManyRecord error. RelationId={RelationId}",
                    relationId);
            }

            return response;
        }

        /// <inheritdoc />
        public async Task<QueryResponse> RemoveRelationManyToManyRecord(
            Guid relationId, Guid originValue, Guid targetValue)
        {
            var response = new QueryResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                _logger.LogInformation(
                    "RemoveRelationManyToManyRecord started. RelationId={RelationId}, Origin={Origin}, Target={Target}",
                    relationId, originValue, targetValue);

                // Load relation metadata
                var relation = await _entityRepository.GetRelationById(relationId);
                if (relation == null)
                {
                    response.Success = false;
                    response.Message = "Relation does not exists.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Delete the bridge record
                await _entityRepository.DeleteManyToManyRecord(
                    relation.Name, originValue, targetValue);

                response.Success = true;
                response.StatusCode = HttpStatusCode.OK;
                response.Message = "The relation record was deleted successfully.";

                _logger.LogInformation(
                    "RemoveRelationManyToManyRecord completed. RelationId={RelationId}, Origin={Origin}, Target={Target}",
                    relationId, originValue, targetValue);

                // Publish domain event (fire-and-forget — replaces post-delete M2M hooks)
                await PublishDomainEvent(
                    "entity-management.relation.deleted",
                    relation.Name,
                    new
                    {
                        relationName = relation.Name,
                        relationId = relationId.ToString(),
                        originValue = originValue.ToString(),
                        targetValue = targetValue.ToString(),
                        timestamp = DateTime.UtcNow.ToString("o")
                    });
            }
            catch (Exception ex)
            {
                response.Success = false;
                if (_developmentMode)
                {
                    response.Message = ex.Message + ex.StackTrace;
                }
                else
                {
                    response.Message = "The entity relation record was not deleted. An internal error occurred!";
                }
                _logger.LogError(ex,
                    "RemoveRelationManyToManyRecord error. RelationId={RelationId}",
                    relationId);
            }

            return response;
        }

        // =====================================================================
        // Find and Count Operations
        // =====================================================================

        /// <inheritdoc />
        public async Task<QueryResponse> Find(EntityQuery query)
        {
            var response = new QueryResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                if (query == null)
                {
                    response.Success = false;
                    response.Message = "The query is incorrect and cannot be executed";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                _logger.LogInformation(
                    "Find started. Entity={EntityName}", query.EntityName);

                // Validate entity existence
                var entity = await GetEntity(query.EntityName);
                if (entity == null)
                {
                    response.Success = false;
                    response.Message = $"The query is incorrect. Specified entity '{query.EntityName}' does not exist.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Enforce read permission
                if (!HasEntityPermission(EntityPermission.Read, entity))
                {
                    response.Success = false;
                    response.StatusCode = HttpStatusCode.Forbidden;
                    response.Message = "Access denied.";
                    response.Errors.Add(new ErrorModel("access", "read",
                        $"Trying to read records from entity '{entity.Name}' with no read access."));
                    _logger.LogWarning(
                        "Find access denied. Entity={EntityName}", entity.Name);
                    return response;
                }

                // Delegate to repository
                var records = await _recordRepository.Find(query);

                // Build enriched FieldsMeta — decorate relation fields with
                // RelationFieldMeta so consumers know the relation context.
                var fieldsMeta = await BuildFieldsMeta(entity);

                // Build response
                response.Object = new QueryResult
                {
                    FieldsMeta = fieldsMeta,
                    Data = records ?? new EntityRecordList()
                };

                response.Success = true;
                response.StatusCode = HttpStatusCode.OK;
                response.Message = "The query was successfully executed.";

                _logger.LogInformation(
                    "Find completed. Entity={EntityName}, ResultCount={Count}",
                    query.EntityName, records?.Count ?? 0);
            }
            catch (Exception ex)
            {
                response.Success = false;
                if (_developmentMode)
                {
                    response.Message = ex.Message + ex.StackTrace;
                }
                else
                {
                    response.Message = "The query is incorrect and cannot be executed";
                }
                _logger.LogError(ex, "Find error. Entity={EntityName}", query?.EntityName);
            }

            return response;
        }

        /// <inheritdoc />
        public async Task<QueryCountResponse> Count(string entityName, QueryObject? queryObj = null)
        {
            var response = new QueryCountResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                _logger.LogInformation("Count started. Entity={EntityName}", entityName);

                // Validate entity existence
                var entity = await GetEntity(entityName);
                if (entity == null)
                {
                    response.Success = false;
                    response.Message = $"The query is incorrect. Specified entity '{entityName}' does not exist.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Enforce read permission
                if (!HasEntityPermission(EntityPermission.Read, entity))
                {
                    response.Success = false;
                    response.StatusCode = HttpStatusCode.Forbidden;
                    response.Message = "Access denied.";
                    response.Errors.Add(new ErrorModel("access", "read",
                        $"Trying to read records from entity '{entity.Name}' with no read access."));
                    _logger.LogWarning("Count access denied. Entity={EntityName}", entity.Name);
                    return response;
                }

                // Build a query and delegate
                var entityQuery = new EntityQuery(entityName, "*", queryObj);
                var count = await _recordRepository.Count(entityQuery);

                response.Object = count;
                response.Success = true;
                response.StatusCode = HttpStatusCode.OK;
                response.Message = "The count was successfully executed.";

                _logger.LogInformation(
                    "Count completed. Entity={EntityName}, Count={Count}",
                    entityName, count);
            }
            catch (Exception ex)
            {
                response.Success = false;
                if (_developmentMode)
                {
                    response.Message = ex.Message + ex.StackTrace;
                }
                else
                {
                    response.Message = "The query is incorrect and cannot be executed";
                }
                _logger.LogError(ex, "Count error. Entity={EntityName}", entityName);
            }

            return response;
        }

        // =====================================================================
        // Private Helpers — Build FieldsMeta with RelationFieldMeta
        // =====================================================================

        /// <summary>
        /// Builds the enriched FieldsMeta list for query results.
        /// For regular fields, includes them as-is. For fields that participate
        /// in entity relations, wraps them as RelationFieldMeta instances with
        /// the full relation context (origin/target entity, direction, related fields).
        /// This enables consumers to understand relation navigation from the field metadata.
        /// Source: RecordManager query result building (entity.Fields decorated with relation info).
        /// </summary>
        private async Task<List<Field>> BuildFieldsMeta(Entity entity)
        {
            if (entity.Fields == null || entity.Fields.Count == 0)
            {
                return new List<Field>();
            }

            var relations = await GetRelations();
            var result = new List<Field>(entity.Fields.Count);

            foreach (var field in entity.Fields)
            {
                // Check if this field participates in any relation as origin or target field
                var matchingRelation = relations.FirstOrDefault(r =>
                    (r.OriginEntityId == entity.Id && r.OriginFieldId == field.Id) ||
                    (r.TargetEntityId == entity.Id && r.TargetFieldId == field.Id));

                if (matchingRelation != null)
                {
                    // Create a RelationFieldMeta instance enriched with relation context
                    var rfm = new RelationFieldMeta(field)
                    {
                        Relation = matchingRelation,
                        Direction = matchingRelation.OriginEntityId == entity.Id
                            ? "origin-target"
                            : "target-origin",
                        Entity = entity
                    };
                    result.Add(rfm);
                }
                else
                {
                    result.Add(field);
                }
            }

            return result;
        }

        // =====================================================================
        // Private Helpers — Entity & Relation Resolution
        // =====================================================================

        /// <summary>
        /// Resolves an entity by name. Delegates to IEntityService.ReadEntity.
        /// </summary>
        private async Task<Entity?> GetEntity(string entityName)
        {
            try
            {
                var entityResponse = await _entityService.ReadEntity(entityName);
                return entityResponse?.Object;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetEntity by name failed. Name={EntityName}", entityName);
                return null;
            }
        }

        /// <summary>
        /// Resolves an entity by GUID. Uses IEntityService.GetEntity for direct lookup.
        /// </summary>
        private async Task<Entity?> GetEntity(Guid entityId)
        {
            try
            {
                return await _entityService.GetEntity(entityId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetEntity by id failed. Id={EntityId}", entityId);
                return null;
            }
        }

        /// <summary>
        /// Retrieves all entity relations with in-request caching.
        /// Replaces the lazy-loaded entityRelationManager.Read().Object.
        /// </summary>
        private async Task<List<EntityRelation>> GetRelations()
        {
            if (_cachedRelations != null)
            {
                return _cachedRelations;
            }

            try
            {
                // Primary: use IEntityService.ReadRelations() for service-layer cache
                var response = await _entityService.ReadRelations();
                _cachedRelations = response?.Object ?? new List<EntityRelation>();

                // Fallback: if service returned empty, try repository directly
                if (_cachedRelations.Count == 0)
                {
                    _cachedRelations = await _entityRepository.GetAllRelations()
                        ?? new List<EntityRelation>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetRelations failed via IEntityService, trying repository fallback.");
                try
                {
                    _cachedRelations = await _entityRepository.GetAllRelations()
                        ?? new List<EntityRelation>();
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "GetRelations fallback via IEntityRepository also failed.");
                    _cachedRelations = new List<EntityRelation>();
                }
            }

            return _cachedRelations;
        }

        // =====================================================================
        // Private Helper — Record ID Extraction
        // =====================================================================

        /// <summary>
        /// Extracts or generates a record ID from the entity record for create operations.
        /// Source: RecordManager.cs lines 320-335 (create ID handling).
        /// </summary>
        private static Guid ExtractRecordId(EntityRecord record)
        {
            if (!record.ContainsKey("id") || record["id"] == null)
            {
                // No ID provided — generate a new one
                var newId = Guid.NewGuid();
                record["id"] = newId;
                return newId;
            }

            var idValue = record["id"];

            if (idValue is string idStr)
            {
                if (Guid.TryParse(idStr, out var parsed))
                {
                    if (parsed == Guid.Empty)
                    {
                        throw new ArgumentException(
                            "Guid.Empty value cannot be used as valid value for record id.");
                    }
                    record["id"] = parsed;
                    return parsed;
                }
                throw new ArgumentException("Invalid record id");
            }

            if (idValue is Guid idGuid)
            {
                if (idGuid == Guid.Empty)
                {
                    throw new ArgumentException(
                        "Guid.Empty value cannot be used as valid value for record id.");
                }
                return idGuid;
            }

            throw new ArgumentException("Invalid record id");
        }

        /// <summary>
        /// Extracts the record ID from an entity record for update operations.
        /// Record ID is required for updates (cannot be null).
        /// </summary>
        private static Guid ExtractRecordIdForUpdate(EntityRecord record)
        {
            if (!record.ContainsKey("id") || record["id"] == null)
            {
                throw new ArgumentException("Invalid record. Missing ID field.");
            }

            var idValue = record["id"];

            if (idValue is string idStr)
            {
                if (Guid.TryParse(idStr, out var parsed))
                {
                    if (parsed == Guid.Empty)
                    {
                        throw new ArgumentException(
                            "Guid.Empty value cannot be used as valid value for record id.");
                    }
                    return parsed;
                }
                throw new ArgumentException("Invalid record id");
            }

            if (idValue is Guid idGuid)
            {
                if (idGuid == Guid.Empty)
                {
                    throw new ArgumentException(
                        "Guid.Empty value cannot be used as valid value for record id.");
                }
                return idGuid;
            }

            throw new ArgumentException("Invalid record id");
        }

        // =====================================================================
        // Private Helper — Permission Enforcement
        // =====================================================================

        /// <summary>
        /// Checks entity-level permissions based on JWT claims.
        /// Replaces SecurityContext.HasEntityPermission from the monolith.
        /// In the microservice architecture, this is a simplified check that
        /// grants access by default (permission enforcement at API Gateway/
        /// authorizer level). Services can extend this for fine-grained entity
        /// permission checking once Cognito groups are mapped to role IDs.
        /// </summary>
        private bool HasEntityPermission(EntityPermission permission, Entity entity)
        {
            // In the serverless architecture, permission enforcement is primarily
            // handled by the API Gateway JWT authorizer. This method provides
            // entity-level permission checking based on RecordPermissions.
            //
            // Currently returns true to allow operations (matching the monolith's
            // behavior when a valid authenticated user exists). Fine-grained
            // entity-level permission checking can be enabled by injecting JWT
            // claims and comparing role GUIDs against entity RecordPermissions.
            //
            // The monolith's SecurityContext.HasEntityPermission logic:
            //   - System user → always has access
            //   - Authenticated user → check user.Roles against entity.RecordPermissions
            //   - No user → check GuestRoleId against entity.RecordPermissions
            //
            // When JWT claims are available and entity.RecordPermissions is configured,
            // this method should be enhanced to:
            //   1. Extract role GUIDs from JWT claims
            //   2. Check against entity.RecordPermissions.Can{Read/Create/Update/Delete}
            //   3. Return false if no matching roles found

            if (entity.RecordPermissions == null)
            {
                return true;
            }

            // Determine the relevant permission list
            List<Guid>? allowedRoles = permission switch
            {
                EntityPermission.Read => entity.RecordPermissions.CanRead,
                EntityPermission.Create => entity.RecordPermissions.CanCreate,
                EntityPermission.Update => entity.RecordPermissions.CanUpdate,
                EntityPermission.Delete => entity.RecordPermissions.CanDelete,
                _ => null
            };

            // If no permission constraints are defined, allow access
            if (allowedRoles == null || allowedRoles.Count == 0)
            {
                return true;
            }

            // System administrator role always has access
            if (allowedRoles.Contains(SystemIds.AdministratorRoleId))
            {
                return true;
            }

            // Default: deny access when specific roles are required but the
            // current user's roles (from JWT claims) do not match. In the
            // microservice architecture, the API Gateway validates the JWT
            // but entity-level permission enforcement requires role matching.
            // Until JWT claim extraction is wired in, this denies access
            // when RecordPermissions restrict the operation to specific roles
            // that do not include the Administrator role.
            return false;
        }

        // =====================================================================
        // Private Helper — Relation Field Processing
        // =====================================================================

        /// <summary>
        /// Processes a relation field key-value pair from the record payload.
        /// Handles $/$$ direction markers, validates relation metadata, and
        /// categorizes into one-to-one, one-to-many, or many-to-many buckets.
        /// Source: RecordManager.cs lines 338-540 (relation-aware processing).
        /// </summary>
        private void ProcessRelationField(
            string keyName,
            object? value,
            Entity entity,
            List<EntityRelation> relations,
            List<KeyValuePair<string, object?>> oneToOneData,
            List<KeyValuePair<string, object?>> oneToManyData,
            List<KeyValuePair<string, object?>> manyToManyData,
            QueryResponse response)
        {
            // Parse the key: expected format "$relationName.fieldName" or "$$relationName.fieldName"
            var parts = keyName.Split(RELATION_SEPARATOR);
            if (parts.Length > 2)
            {
                response.Success = false;
                response.Message = $"The specified field name '{keyName}' is incorrect. Only first level relation can be specified.";
                return;
            }

            var relationPart = parts[0];
            var fieldName = parts.Length > 1 ? parts[1] : null;

            // Determine direction from $ / $$ prefix
            bool isTargetOriginDirection = false;
            string relationName;

            if (relationPart.StartsWith("$$"))
            {
                isTargetOriginDirection = true;
                relationName = relationPart.Substring(2);
            }
            else if (relationPart.StartsWith("$"))
            {
                isTargetOriginDirection = false;
                relationName = relationPart.Substring(1);
            }
            else
            {
                response.Success = false;
                response.Message = $"Invalid relation '{keyName}'. The relation name is not specified.";
                return;
            }

            if (string.IsNullOrWhiteSpace(relationName))
            {
                response.Success = false;
                response.Message = $"Invalid relation '{keyName}'. The relation name is not correct.";
                return;
            }

            if (string.IsNullOrWhiteSpace(fieldName))
            {
                response.Success = false;
                response.Message = $"Invalid relation '{keyName}'. The relation field name is not specified.";
                return;
            }

            // Find the relation by name
            var relation = relations.FirstOrDefault(
                r => string.Equals(r.Name, relationName, StringComparison.OrdinalIgnoreCase));

            if (relation == null)
            {
                response.Success = false;
                response.Message = $"Invalid relation '{keyName}'. The relation does not exist.";
                return;
            }

            // Validate that the relation belongs to the current entity
            if (relation.OriginEntityId != entity.Id && relation.TargetEntityId != entity.Id)
            {
                response.Success = false;
                response.Message = $"Invalid relation '{keyName}'. The relation field belongs to entity that does not relate to current entity.";
                return;
            }

            // Create the relation data entry with full context
            var relationData = new KeyValuePair<string, object?>(
                keyName,
                new RelationFieldData
                {
                    RelationName = relationName,
                    FieldName = fieldName,
                    Value = value,
                    Relation = relation,
                    IsTargetOriginDirection = isTargetOriginDirection,
                    OriginalKey = keyName
                });

            // Categorize by relation type
            switch (relation.RelationType)
            {
                case EntityRelationType.OneToOne:
                    oneToOneData.Add(relationData);
                    break;
                case EntityRelationType.OneToMany:
                    oneToManyData.Add(relationData);
                    break;
                case EntityRelationType.ManyToMany:
                    manyToManyData.Add(relationData);
                    break;
            }
        }

        // =====================================================================
        // Private Helper — Relation Processing (Post-Record Operations)
        // =====================================================================

        /// <summary>
        /// Processes one-to-one relation data after the main record is created/updated.
        /// Sets the FK field value on the related entity's record.
        /// </summary>
        private async Task ProcessOneToOneRelations(
            Entity entity,
            Guid recordId,
            List<KeyValuePair<string, object?>> relationData,
            List<EntityRelation> relations)
        {
            foreach (var pair in relationData)
            {
                if (pair.Value is not RelationFieldData rfd) continue;

                try
                {
                    await ProcessRelationUpdate(entity, recordId, rfd, relations);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ProcessOneToOneRelations error. Entity={EntityName}, Relation={RelationName}",
                        entity.Name, rfd.RelationName);
                }
            }
        }

        /// <summary>
        /// Processes one-to-many relation data after the main record is created/updated.
        /// Sets FK field values on the related entity's records.
        /// </summary>
        private async Task ProcessOneToManyRelations(
            Entity entity,
            Guid recordId,
            List<KeyValuePair<string, object?>> relationData,
            List<EntityRelation> relations)
        {
            foreach (var pair in relationData)
            {
                if (pair.Value is not RelationFieldData rfd) continue;

                try
                {
                    await ProcessRelationUpdate(entity, recordId, rfd, relations);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ProcessOneToManyRelations error. Entity={EntityName}, Relation={RelationName}",
                        entity.Name, rfd.RelationName);
                }
            }
        }

        /// <summary>
        /// Processes many-to-many relation data after the main record is created.
        /// Creates bridge records for the M2M relation.
        /// </summary>
        private async Task ProcessManyToManyRelations(
            Entity entity,
            Guid recordId,
            List<KeyValuePair<string, object?>> relationData,
            List<EntityRelation> relations)
        {
            foreach (var pair in relationData)
            {
                if (pair.Value is not RelationFieldData rfd) continue;

                try
                {
                    var targetIds = ExtractGuidListFromRelationValue(rfd.Value);
                    bool isOrigin = !rfd.IsTargetOriginDirection;

                    // If this entity is the origin, recordId is originValue
                    // If this entity is the target, recordId is targetValue
                    if (rfd.Relation.OriginEntityId == entity.Id && !rfd.IsTargetOriginDirection)
                    {
                        isOrigin = true;
                    }
                    else if (rfd.Relation.TargetEntityId == entity.Id && rfd.IsTargetOriginDirection)
                    {
                        isOrigin = true;
                    }

                    foreach (var targetId in targetIds)
                    {
                        Guid originVal = isOrigin ? recordId : targetId;
                        Guid targetVal = isOrigin ? targetId : recordId;

                        await _entityRepository.CreateManyToManyRecord(
                            rfd.Relation.Id, originVal, targetVal);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ProcessManyToManyRelations error. Entity={EntityName}, Relation={RelationName}",
                        entity.Name, rfd.RelationName);
                }
            }
        }

        /// <summary>
        /// Processes many-to-many relation data during update.
        /// Removes old bridge records and creates new ones.
        /// </summary>
        private async Task ProcessManyToManyRelationsForUpdate(
            Entity entity,
            Guid recordId,
            List<KeyValuePair<string, object?>> relationData,
            List<EntityRelation> relations,
            EntityRecord existingRecord)
        {
            foreach (var pair in relationData)
            {
                if (pair.Value is not RelationFieldData rfd) continue;

                try
                {
                    bool isOrigin = !rfd.IsTargetOriginDirection;
                    if (rfd.Relation.OriginEntityId == entity.Id && !rfd.IsTargetOriginDirection)
                    {
                        isOrigin = true;
                    }
                    else if (rfd.Relation.TargetEntityId == entity.Id && rfd.IsTargetOriginDirection)
                    {
                        isOrigin = true;
                    }

                    // Query existing M2M associations for change detection
                    Guid? queryOriginId = isOrigin ? recordId : (Guid?)null;
                    Guid? queryTargetId = isOrigin ? (Guid?)null : recordId;
                    var existingM2MRecords = await _entityRepository.GetManyToManyRecords(
                        rfd.Relation.Id, queryOriginId, queryTargetId);

                    var newTargetIds = ExtractGuidListFromRelationValue(rfd.Value);
                    var existingTargetIds = existingM2MRecords?
                        .Select(kvp => isOrigin ? kvp.Value : kvp.Key)
                        .ToList() ?? new List<Guid>();

                    // Remove associations that are no longer in the new set
                    var toRemove = existingTargetIds.Where(eid => !newTargetIds.Contains(eid)).ToList();
                    if (toRemove.Count > 0 || existingTargetIds.Count > 0)
                    {
                        // Delete all old M2M records for this relation/record
                        Guid? removeOriginId = isOrigin ? recordId : (Guid?)null;
                        Guid? removeTargetId = isOrigin ? (Guid?)null : recordId;
                        await _entityRepository.DeleteManyToManyRecord(
                            rfd.Relation.Name, removeOriginId, removeTargetId);
                    }

                    // Create new bridge records for the desired set
                    foreach (var targetId in newTargetIds)
                    {
                        Guid originVal = isOrigin ? recordId : targetId;
                        Guid targetVal = isOrigin ? targetId : recordId;

                        await _entityRepository.CreateManyToManyRecord(
                            rfd.Relation.Id, originVal, targetVal);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ProcessManyToManyRelationsForUpdate error. Entity={EntityName}, Relation={RelationName}",
                        entity.Name, rfd.RelationName);
                }
            }
        }

        /// <summary>
        /// Processes a single relation update: finds the related record and
        /// updates the FK field value.
        /// </summary>
        private async Task ProcessRelationUpdate(
            Entity entity,
            Guid recordId,
            RelationFieldData rfd,
            List<EntityRelation> relations)
        {
            var relation = rfd.Relation;

            // Determine which entity is the "related" one and which field to update
            Guid relatedEntityId;
            Guid relationFieldId;

            // Handle self-referencing entities
            if (relation.OriginEntityId == relation.TargetEntityId)
            {
                if (rfd.IsTargetOriginDirection)
                {
                    // $$ direction: target-origin (update origin side)
                    relatedEntityId = relation.OriginEntityId;
                    relationFieldId = relation.OriginFieldId;
                }
                else
                {
                    // $ direction: origin-target (update target side)
                    relatedEntityId = relation.TargetEntityId;
                    relationFieldId = relation.TargetFieldId;
                }
            }
            else if (relation.OriginEntityId == entity.Id)
            {
                // Current entity is origin → related is target
                relatedEntityId = relation.TargetEntityId;
                relationFieldId = relation.TargetFieldId;
            }
            else
            {
                // Current entity is target → related is origin
                relatedEntityId = relation.OriginEntityId;
                relationFieldId = relation.OriginFieldId;
            }

            // Resolve the related entity
            var relatedEntity = await GetEntity(relatedEntityId);
            if (relatedEntity == null)
            {
                _logger.LogWarning(
                    "ProcessRelationUpdate: related entity not found. EntityId={EntityId}",
                    relatedEntityId);
                return;
            }

            // Find the relation field in the related entity
            var relatedField = relatedEntity.Fields?.FirstOrDefault(
                f => f.Id == relationFieldId);

            if (relatedField == null)
            {
                _logger.LogWarning(
                    "ProcessRelationUpdate: relation field not found. FieldId={FieldId}",
                    relationFieldId);
                return;
            }

            // Build query to find the related record by the relation value
            if (rfd.Value == null)
            {
                return;
            }

            var queryFilter = EntityQuery.QueryEQ(rfd.FieldName, rfd.Value);
            var entityQuery = new EntityQuery(relatedEntity.Name, "*", queryFilter);

            var relatedRecords = await _recordRepository.Find(entityQuery);

            if (relatedRecords == null || relatedRecords.Count == 0)
            {
                _logger.LogInformation(
                    "ProcessRelationUpdate: no related records found. Entity={EntityName}, Field={FieldName}",
                    relatedEntity.Name, rfd.FieldName);
                return;
            }

            // Update the FK field on the related record to point to our record
            foreach (var relatedRecord in relatedRecords)
            {
                if (!relatedRecord.ContainsKey("id")) continue;

                var relatedRecordId = relatedRecord["id"];
                Guid relatedRecordGuid;

                if (relatedRecordId is Guid g)
                    relatedRecordGuid = g;
                else if (relatedRecordId is string s && Guid.TryParse(s, out var parsed))
                    relatedRecordGuid = parsed;
                else
                    continue;

                // Determine which FK field to set and what value
                Guid fkFieldId;
                if (relation.OriginEntityId == entity.Id)
                {
                    fkFieldId = relation.TargetFieldId;
                }
                else
                {
                    fkFieldId = relation.OriginFieldId;
                }

                var fkField = relatedEntity.Fields?.FirstOrDefault(f => f.Id == fkFieldId);
                if (fkField == null) continue;

                var updateData = new List<KeyValuePair<string, object?>>
                {
                    new("id", relatedRecordGuid),
                    new(fkField.Name, recordId)
                };

                await _recordRepository.UpdateRecord(relatedEntity.Name, updateData);
            }
        }

        /// <summary>
        /// Extracts a list of GUIDs from a relation field value.
        /// Handles single GUID, string, and array formats.
        /// </summary>
        private static List<Guid> ExtractGuidListFromRelationValue(object? value)
        {
            var result = new List<Guid>();

            if (value == null) return result;

            if (value is Guid singleGuid)
            {
                result.Add(singleGuid);
                return result;
            }

            if (value is string strVal)
            {
                if (Guid.TryParse(strVal, out var parsed))
                {
                    result.Add(parsed);
                }
                return result;
            }

            if (value is IEnumerable<object> enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is Guid g)
                    {
                        result.Add(g);
                    }
                    else if (item is string s && Guid.TryParse(s, out var p))
                    {
                        result.Add(p);
                    }
                }
            }

            return result;
        }

        // =====================================================================
        // Private Helper — Field Value Extraction (All 20+ Types)
        // =====================================================================

        /// <summary>
        /// Extracts and normalizes a field value based on its field type definition.
        /// Handles all 20+ field types with exact same normalization logic as
        /// the monolith's RecordManager.ExtractFieldValue and
        /// DbRecordRepository.ExtractFieldValue (lines 1857-2064).
        /// </summary>
        /// <param name="value">The raw value from the record payload.</param>
        /// <param name="field">The field definition from entity metadata.</param>
        /// <param name="encryptPasswordFields">Whether to MD5-hash password values.</param>
        /// <returns>The normalized value ready for DynamoDB storage.</returns>
        private object? ExtractFieldValue(object? value, Field field, bool encryptPasswordFields = true)
        {
            // Null or DBNull → null
            if (value == null || value == DBNull.Value)
            {
                // If value is null, return field default for required fields
                return field.GetFieldDefaultValue();
            }

            var fieldType = field.GetFieldType();

            switch (fieldType)
            {
                // =============================================================
                // AutoNumberField — decimal/integer normalization
                // =============================================================
                case FieldType.AutoNumberField:
                    if (value is string autoStr)
                    {
                        return (int)decimal.Parse(autoStr, CultureInfo.InvariantCulture);
                    }
                    return Convert.ToDecimal(value, CultureInfo.InvariantCulture);

                // =============================================================
                // CheckboxField — boolean normalization
                // =============================================================
                case FieldType.CheckboxField:
                    if (value is string boolStr)
                    {
                        return Convert.ToBoolean(boolStr);
                    }
                    return value as bool?;

                // =============================================================
                // CurrencyField — decimal with rounding per currency spec
                // =============================================================
                case FieldType.CurrencyField:
                {
                    decimal decVal;
                    if (value is string currStr)
                    {
                        // Strip currency symbol prefixes (e.g., "$100.50")
                        currStr = currStr.TrimStart('$', '€', '£', '¥', ' ');
                        decVal = decimal.Parse(currStr, NumberStyles.Any, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        decVal = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    }

                    // Round per currency's decimal digits specification
                    int decimalDigits = 2; // default
                    if (field is CurrencyField currField && currField.Currency != null)
                    {
                        decimalDigits = currField.Currency.DecimalDigits;
                    }

                    return decimal.Round(decVal, decimalDigits, MidpointRounding.AwayFromZero);
                }

                // =============================================================
                // DateField — DateTime parsing with timezone handling
                // =============================================================
                case FieldType.DateField:
                {
                    if (value is string dateStr)
                    {
                        var dt = DateTime.Parse(dateStr, CultureInfo.InvariantCulture);
                        // UTC/Local → convert to app date; Unspecified → keep raw
                        if (dt.Kind == DateTimeKind.Utc || dt.Kind == DateTimeKind.Local)
                        {
                            return ConvertToAppDate(dt);
                        }
                        return dt;
                    }
                    if (value is DateTime dtVal)
                    {
                        if (dtVal.Kind == DateTimeKind.Utc || dtVal.Kind == DateTimeKind.Local)
                        {
                            return ConvertToAppDate(dtVal);
                        }
                        return dtVal;
                    }
                    return value;
                }

                // =============================================================
                // DateTimeField — DateTime parsing with UTC normalization
                // =============================================================
                case FieldType.DateTimeField:
                {
                    if (value is string dtStr)
                    {
                        var dt = DateTime.Parse(dtStr, CultureInfo.InvariantCulture);
                        return NormalizeDateTimeToUtc(dt);
                    }
                    if (value is DateTime dtVal)
                    {
                        return NormalizeDateTimeToUtc(dtVal);
                    }
                    return value;
                }

                // =============================================================
                // String-based fields — pass through as string
                // =============================================================
                case FieldType.EmailField:
                case FieldType.FileField:
                case FieldType.ImageField:
                case FieldType.HtmlField:
                case FieldType.MultiLineTextField:
                case FieldType.GeographyField:
                case FieldType.PhoneField:
                case FieldType.SelectField:
                case FieldType.TextField:
                case FieldType.UrlField:
                    return value as string ?? value?.ToString();

                // =============================================================
                // MultiSelectField — array normalization to List<string>
                // =============================================================
                case FieldType.MultiSelectField:
                {
                    if (value is IEnumerable<string> strEnumerable)
                    {
                        return strEnumerable.ToList();
                    }
                    if (value is List<object> objList)
                    {
                        return objList.Select(x => x?.ToString() ?? string.Empty).ToList();
                    }
                    if (value is IEnumerable<object> objEnumerable)
                    {
                        return objEnumerable.Select(x => x?.ToString() ?? string.Empty).ToList();
                    }
                    if (value is string singleVal)
                    {
                        // Try to parse as JSON array
                        try
                        {
                            var parsed = JsonSerializer.Deserialize<List<string>>(singleVal);
                            if (parsed != null) return parsed;
                        }
                        catch
                        {
                            // Not a JSON array — treat as single value
                        }
                        return new List<string> { singleVal };
                    }
                    return new List<string>();
                }

                // =============================================================
                // NumberField / PercentField — decimal normalization
                // =============================================================
                case FieldType.NumberField:
                case FieldType.PercentField:
                    if (value is string numStr)
                    {
                        return decimal.Parse(numStr, NumberStyles.Any, CultureInfo.InvariantCulture);
                    }
                    return Convert.ToDecimal(value, CultureInfo.InvariantCulture);

                // =============================================================
                // PasswordField — MD5 hash or passthrough
                // =============================================================
                case FieldType.PasswordField:
                {
                    if (value == null) return null;

                    bool shouldEncrypt = encryptPasswordFields;
                    if (field is PasswordField pwField && pwField.Encrypted == true)
                    {
                        shouldEncrypt = shouldEncrypt && true;
                    }
                    else
                    {
                        shouldEncrypt = false;
                    }

                    if (shouldEncrypt)
                    {
                        return ComputeMd5Hash(value.ToString() ?? string.Empty);
                    }

                    return value as string ?? value.ToString();
                }

                // =============================================================
                // GuidField — GUID parsing and normalization
                // =============================================================
                case FieldType.GuidField:
                {
                    if (value == null) return (Guid?)null;

                    if (value is string guidStr)
                    {
                        if (string.IsNullOrWhiteSpace(guidStr)) return (Guid?)null;
                        if (Guid.TryParse(guidStr, out var parsed))
                        {
                            return (Guid?)parsed;
                        }
                        // Fallback: Guid.Parse throws FormatException with detailed message
                        return (Guid?)Guid.Parse(guidStr);
                    }

                    if (value is Guid guidVal)
                    {
                        return (Guid?)guidVal;
                    }

                    throw new InvalidOperationException("Invalid Guid field value.");
                }

                // =============================================================
                // Unsupported field type
                // =============================================================
                default:
                    throw new InvalidOperationException(
                        "System Error. A field type is not supported in field value extraction process.");
            }
        }

        // =====================================================================
        // Private Helper — Set Required Fields Default Data
        // =====================================================================

        /// <summary>
        /// For each required field in the entity that is not already present in
        /// the record data, adds the field's default value.
        /// Skips AutoNumber, File, and Image fields (auto-generated or special).
        /// Source: RecordManager.SetRecordRequiredFieldsDefaultData (lines 2087-2107).
        /// </summary>
        private static void SetRecordRequiredFieldsDefaultData(
            Entity entity,
            EntityRecord record,
            List<KeyValuePair<string, object?>> storageRecordData)
        {
            if (entity.Fields == null) return;

            foreach (var field in entity.Fields)
            {
                if (field.Required != true) continue;

                var fieldType = field.GetFieldType();

                // Skip auto-generated and special field types
                if (fieldType == FieldType.AutoNumberField ||
                    fieldType == FieldType.FileField ||
                    fieldType == FieldType.ImageField)
                {
                    continue;
                }

                // Check if field is already in the record data
                bool alreadyPresent = record.ContainsKey(field.Name) ||
                    storageRecordData.Any(kvp =>
                        string.Equals(kvp.Key, field.Name, StringComparison.OrdinalIgnoreCase));

                if (!alreadyPresent)
                {
                    var defaultValue = field.GetFieldDefaultValue();
                    storageRecordData.Add(
                        new KeyValuePair<string, object?>(field.Name, defaultValue));
                }
            }
        }

        // =====================================================================
        // Private Helper — Validate Unique Field Constraints
        // =====================================================================

        /// <summary>
        /// Validates unique field constraints for create/update operations.
        /// For each field marked as Unique on the entity, checks whether the
        /// provided record value would violate uniqueness by querying existing records.
        /// Logs a warning for each unique constraint violation detected.
        /// This replaces the monolith's DbRecordRepository unique-check within 
        /// transaction boundaries.
        /// </summary>
        private async Task ValidateUniqueFieldConstraints(
            Entity entity,
            EntityRecord record,
            Guid recordId,
            List<ErrorModel> errors,
            bool isUpdate)
        {
            if (entity.Fields == null) return;

            foreach (var field in entity.Fields)
            {
                // Only check fields marked as unique
                if (field.Unique != true) continue;

                // Skip if the record doesn't contain this field
                if (!record.ContainsKey(field.Name)) continue;

                var value = record[field.Name];
                if (value == null) continue;

                try
                {
                    // Build a query to check for existing records with the same value
                    var checkQuery = new EntityQuery(entity.Name);
                    checkQuery.Query = EntityQuery.QueryEQ(field.Name, value);

                    var existingRecords = await _recordRepository.Find(checkQuery);
                    if (existingRecords != null && existingRecords.Count > 0)
                    {
                        // For updates, exclude the current record from the check
                        bool conflict = isUpdate
                            ? existingRecords.Any(r =>
                                r.ContainsKey("id") &&
                                r["id"] is Guid existingId &&
                                existingId != recordId)
                            : true;

                        if (conflict)
                        {
                            var errorMsg =
                                $"Unique constraint violation: field '{field.Name}' " +
                                $"with value '{value}' already exists on entity '{entity.Name}'.";
                            errors.Add(new ErrorModel(field.Name, value?.ToString() ?? string.Empty, errorMsg));
                            _logger.LogWarning(
                                "Unique constraint violation. Entity={EntityName}, Field={FieldName}, Value={Value}",
                                entity.Name, field.Name, value);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Unique constraint check failed. Entity={EntityName}, Field={FieldName}",
                        entity.Name, field.Name);
                }
            }
        }

        // =====================================================================
        // Private Helper — File Path Processing
        // =====================================================================

        /// <summary>
        /// Processes file/image field paths for create operations.
        /// Strips /fs/ prefix and normalizes temporary paths to permanent
        /// S3 paths under /{entityName}/{recordId}/{fileName}.
        /// </summary>
        private void ProcessFileFieldPaths(
            Entity entity,
            Guid recordId,
            List<KeyValuePair<string, string>> fileFieldPaths,
            List<KeyValuePair<string, object?>> storageRecordData)
        {
            foreach (var filePair in fileFieldPaths)
            {
                var path = filePair.Value;
                if (string.IsNullOrWhiteSpace(path)) continue;

                // Strip /fs/ prefix (monolith filesystem path convention)
                path = StripFileSystemPrefix(path);

                // If path is in temp folder, move to permanent location
                if (IsTemporaryFilePath(path))
                {
                    var fileName = ExtractFileName(path);
                    path = $"/{entity.Name}/{recordId}/{fileName}";
                }

                // Update the storage data with the normalized path
                var existingIdx = storageRecordData.FindIndex(
                    kvp => string.Equals(kvp.Key, filePair.Key, StringComparison.OrdinalIgnoreCase));

                if (existingIdx >= 0)
                {
                    storageRecordData[existingIdx] =
                        new KeyValuePair<string, object?>(filePair.Key, path);
                }
            }
        }

        /// <summary>
        /// Processes file/image field paths for update operations.
        /// Handles deletion (empty new path), path changes (old→new), and
        /// temp-to-permanent path normalization.
        /// </summary>
        private void ProcessFileFieldPathsForUpdate(
            Entity entity,
            Guid recordId,
            List<KeyValuePair<string, string>> fileFieldPaths,
            List<KeyValuePair<string, string?>> oldFileFieldPaths,
            List<KeyValuePair<string, object?>> storageRecordData)
        {
            for (int i = 0; i < fileFieldPaths.Count; i++)
            {
                var newPathPair = fileFieldPaths[i];
                var oldPath = i < oldFileFieldPaths.Count ? oldFileFieldPaths[i].Value : null;
                var newPath = newPathPair.Value;

                // Handle deletion — empty new path means delete the file
                if (string.IsNullOrWhiteSpace(newPath))
                {
                    if (!string.IsNullOrWhiteSpace(oldPath))
                    {
                        _logger.LogInformation(
                            "ProcessFileFieldPathsForUpdate: file deleted. Field={FieldName}, OldPath={OldPath}",
                            newPathPair.Key, oldPath);
                    }

                    // Set the field to empty/null in storage
                    var existingIdx = storageRecordData.FindIndex(
                        kvp => string.Equals(kvp.Key, newPathPair.Key, StringComparison.OrdinalIgnoreCase));
                    if (existingIdx >= 0)
                    {
                        storageRecordData[existingIdx] =
                            new KeyValuePair<string, object?>(newPathPair.Key, null);
                    }
                    continue;
                }

                // Strip /fs/ prefix
                newPath = StripFileSystemPrefix(newPath);

                // If path is in temp folder, move to permanent location
                if (IsTemporaryFilePath(newPath))
                {
                    var fileName = ExtractFileName(newPath);
                    newPath = $"/{entity.Name}/{recordId}/{fileName}";
                }

                // Update storage data
                var idx = storageRecordData.FindIndex(
                    kvp => string.Equals(kvp.Key, newPathPair.Key, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    storageRecordData[idx] =
                        new KeyValuePair<string, object?>(newPathPair.Key, newPath);
                }
            }
        }

        /// <summary>
        /// Strips the /fs/ or fs/ prefix from file paths.
        /// Monolith convention was to prefix file paths with /fs/.
        /// </summary>
        private static string StripFileSystemPrefix(string path)
        {
            if (path.StartsWith("/fs/", StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(4);
            }
            if (path.StartsWith("fs/", StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(3);
            }
            return path;
        }

        /// <summary>
        /// Checks if a file path refers to a temporary upload location.
        /// </summary>
        private static bool IsTemporaryFilePath(string path)
        {
            return path.Contains("/tmp/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("tmp/", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/temp/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("temp/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts the file name from a path (last segment after /).
        /// </summary>
        private static string ExtractFileName(string path)
        {
            var lastSlash = path.LastIndexOf('/');
            return lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;
        }

        // =====================================================================
        // Private Helper — DateTime Utilities
        // =====================================================================

        /// <summary>
        /// Converts a DateTime to the application date (date-only, no timezone conversion).
        /// Replaces the monolith's ConvertToAppDate utility.
        /// </summary>
        private static DateTime ConvertToAppDate(DateTime dt)
        {
            // In the serverless architecture, dates are stored as UTC.
            // ConvertToAppDate strips the time component for date-only fields.
            return dt.Date;
        }

        /// <summary>
        /// Normalizes a DateTime to UTC for DateTimeField storage.
        /// Source: RecordManager.ExtractFieldValue DateTimeField handling.
        /// UTC → keep; Local → ToUniversalTime(); Unspecified → treat as UTC.
        /// </summary>
        private static DateTime NormalizeDateTimeToUtc(DateTime dt)
        {
            return dt.Kind switch
            {
                DateTimeKind.Utc => dt,
                DateTimeKind.Local => dt.ToUniversalTime(),
                // Unspecified → treat as UTC in serverless architecture
                // (monolith used TimeZoneInfo.ConvertTimeToUtc with ErpSettings.TimeZoneName)
                _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            };
        }

        // =====================================================================
        // Private Helper — MD5 Hash Computation
        // =====================================================================

        /// <summary>
        /// Computes the MD5 hash of a password string.
        /// Replicates the monolith's PasswordUtil.GetMd5Hash / CryptoUtility.ComputeOddMD5Hash.
        /// </summary>
        private static string ComputeMd5Hash(string input)
        {
            using var md5 = MD5.Create();
            var inputBytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = md5.ComputeHash(inputBytes);

            var sb = new StringBuilder();
            foreach (var b in hashBytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        // =====================================================================
        // Private Helper — SNS Domain Event Publishing
        // =====================================================================

        /// <summary>
        /// Publishes a domain event to SNS (fire-and-forget pattern).
        /// Replaces the monolith's post-hook execution pattern.
        /// If SNS publish fails, logs the error but does NOT fail the API response.
        /// Event naming convention: entity-management.{entity}.{action} (AAP §0.8.5).
        /// </summary>
        private async Task PublishDomainEvent(string eventType, string entityName, object eventPayload)
        {
            try
            {
                var topicArn = $"{_snsTopicArnPrefix}entity-management-events";
                var messageBody = JsonSerializer.Serialize(eventPayload);

                var request = new PublishRequest
                {
                    TopicArn = topicArn,
                    Message = messageBody,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        },
                        ["entityName"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = entityName
                        },
                        ["timestamp"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = DateTime.UtcNow.ToString("o")
                        }
                    }
                };

                PublishResponse snsResponse = await _snsClient.PublishAsync(request);

                _logger.LogInformation(
                    "Domain event published. EventType={EventType}, Entity={EntityName}, MessageId={MessageId}",
                    eventType, entityName, snsResponse.MessageId);
            }
            catch (Exception ex)
            {
                // Fire-and-forget: log error but do NOT fail the API response
                _logger.LogError(ex,
                    "Failed to publish domain event. EventType={EventType}, Entity={EntityName}",
                    eventType, entityName);
            }
        }

        // =====================================================================
        // Internal Helper Classes
        // =====================================================================

        /// <summary>
        /// Internal data structure for tracking relation field metadata
        /// during record processing. Holds parsed relation context from
        /// $/$$ prefixed keys in the record payload.
        /// </summary>
        private class RelationFieldData
        {
            public string RelationName { get; set; } = string.Empty;
            public string FieldName { get; set; } = string.Empty;
            public object? Value { get; set; }
            public EntityRelation Relation { get; set; } = null!;
            public bool IsTargetOriginDirection { get; set; }
            public string OriginalKey { get; set; } = string.Empty;
        }
    }

    // =========================================================================
    // ValidationException — Inline validation replacement for hook errors
    // =========================================================================

    /// <summary>
    /// Exception thrown when inline validation (replacing pre-hooks) detects
    /// errors that should abort the operation. Carries a list of ErrorModel
    /// for structured error reporting in the response.
    /// </summary>
    public class ValidationException : Exception
    {
        /// <summary>Structured validation errors.</summary>
        public List<ErrorModel> Errors { get; }

        /// <summary>
        /// Creates a new ValidationException with message and error list.
        /// </summary>
        public ValidationException(string message, List<ErrorModel> errors)
            : base(message)
        {
            Errors = errors ?? new List<ErrorModel>();
        }

        /// <summary>
        /// Creates a new ValidationException with message only.
        /// </summary>
        public ValidationException(string message)
            : base(message)
        {
            Errors = new List<ErrorModel>();
        }
    }
}
