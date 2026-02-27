// =============================================================================
// EntityService.cs — Entity/Field/Relation Metadata Management Service
// =============================================================================
// Replaces monolith: WebVella.Erp/Api/EntityManager.cs (~1873 lines),
//                    WebVella.Erp/Api/EntityRelationManager.cs (~568 lines),
//                    WebVella.Erp/Api/Cache.cs (~135 lines)
//
// Foundational metadata management for the Entity Management bounded context.
// Owns all entity/field/relation CRUD business logic, validation, permission
// checking, and cache coordination backed by DynamoDB via IEntityRepository.
//
// Namespace Migration:
//   Old: WebVella.Erp.Api (EntityManager, EntityRelationManager, Cache)
//   New: WebVellaErp.EntityManagement.Services
//
// Key architectural changes from monolith:
//   - Injected IEntityRepository replaces DbContext.Current ambient singleton
//   - IMemoryCache replaces static Cache class (1-hour TTL preserved)
//   - ILogger replaces Console/DiagnosticLog (structured JSON logging with correlation-ID)
//   - IConfiguration replaces ErpSettings static config
//   - All methods are async (DynamoDB SDK is async-native)
//   - Permission checking uses JWT claims (not SecurityContext.OpenScope)
//   - Newtonsoft.Json kept ONLY for hash computation (backward compat)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVellaErp.EntityManagement.DataAccess;
using WebVellaErp.EntityManagement.Models;

namespace WebVellaErp.EntityManagement.Services
{
    // =========================================================================
    // IEntityService Interface
    // =========================================================================

    /// <summary>
    /// Service interface for entity, field, and relation metadata CRUD operations.
    /// Designed for DI injection and testability, replacing the monolith's static
    /// EntityManager and EntityRelationManager classes.
    /// </summary>
    public interface IEntityService
    {
        // ─── Entity CRUD ──────────────────────────────────────────────────

        /// <summary>
        /// Creates a new entity definition with validation, default field generation,
        /// and cache invalidation.
        /// Source: EntityManager.CreateEntity() lines ~420-570
        /// </summary>
        Task<EntityResponse> CreateEntity(InputEntity entity, bool createOnlyIdField = true, Dictionary<string, Guid>? sysIdDictionary = null);

        /// <summary>
        /// Updates an existing entity's metadata (Label, LabelPlural, System, IconName,
        /// Color, RecordScreenIdField, RecordPermissions). Name is NOT updatable.
        /// Source: EntityManager.UpdateEntity() lines ~570-650
        /// </summary>
        Task<EntityResponse> UpdateEntity(InputEntity entity);

        /// <summary>
        /// Deletes an entity and all associated fields, records, and relations.
        /// Source: EntityManager.DeleteEntity() lines ~650-720
        /// </summary>
        Task<EntityResponse> DeleteEntity(Guid entityId);

        /// <summary>
        /// Reads a single entity by its programmatic name. Returns from cache if available.
        /// Source: EntityManager.ReadEntity(string) 
        /// </summary>
        Task<EntityResponse> ReadEntity(string entityName);

        /// <summary>
        /// Reads a single entity by its unique identifier. Returns from cache if available.
        /// Source: EntityManager.ReadEntity(Guid)
        /// </summary>
        Task<EntityResponse> ReadEntity(Guid entityId);

        /// <summary>
        /// Reads all entity definitions with per-entity hash computation.
        /// Cache-first with lock-based double-check pattern.
        /// Source: EntityManager.ReadEntities() lines ~720-870
        /// </summary>
        Task<EntityListResponse> ReadEntities();

        // ─── Field CRUD ───────────────────────────────────────────────────

        /// <summary>
        /// Creates a new field definition within an entity.
        /// Source: EntityManager.CreateField() lines ~970-1050
        /// </summary>
        Task<FieldResponse> CreateField(Guid entityId, InputField field);

        /// <summary>
        /// Updates an existing field definition within an entity.
        /// Source: EntityManager.UpdateField() lines ~1250-1400
        /// </summary>
        Task<FieldResponse> UpdateField(Guid entityId, InputField field);

        /// <summary>
        /// Deletes a field definition from an entity (validates not in relations first).
        /// Source: EntityManager.DeleteField() lines ~1400-1524
        /// </summary>
        Task<FieldResponse> DeleteField(Guid entityId, Guid fieldId);

        /// <summary>
        /// Reads all field definitions for the specified entity.
        /// Source: EntityManager.ReadFields(Guid) lines ~1526-1575
        /// </summary>
        Task<FieldListResponse> ReadFields(Guid entityId);

        // ─── Relation CRUD ────────────────────────────────────────────────

        /// <summary>
        /// Creates a new entity relation definition.
        /// Source: EntityRelationManager.Create() 
        /// </summary>
        Task<EntityRelationResponse> CreateRelation(EntityRelation relation);

        /// <summary>
        /// Updates an existing entity relation (immutability rules enforced).
        /// Source: EntityRelationManager.Update()
        /// </summary>
        Task<EntityRelationResponse> UpdateRelation(EntityRelation relation);

        /// <summary>
        /// Deletes an entity relation definition.
        /// Source: EntityRelationManager.Delete()
        /// </summary>
        Task<EntityRelationResponse> DeleteRelation(Guid relationId);

        /// <summary>
        /// Reads a single entity relation by its programmatic name.
        /// Source: EntityRelationManager.Read(string) 
        /// </summary>
        Task<EntityRelationResponse> ReadRelation(string relationName);

        /// <summary>
        /// Reads a single entity relation by its unique identifier.
        /// Source: EntityRelationManager.Read(Guid)
        /// </summary>
        Task<EntityRelationResponse> ReadRelation(Guid relationId);

        /// <summary>
        /// Reads all entity relation definitions with enrichment and hash computation.
        /// Source: EntityRelationManager.Read() (no params)
        /// </summary>
        Task<EntityRelationListResponse> ReadRelations();

        // ─── Cache Management ─────────────────────────────────────────────

        /// <summary>
        /// Clears all cached entity and relation metadata, forcing reload on next read.
        /// Source: Cache.Clear()
        /// </summary>
        void ClearCache();

        /// <summary>
        /// Returns the MD5 hash of all cached entity definitions for change detection.
        /// Source: Cache.GetEntitiesHash()
        /// </summary>
        string GetEntitiesHash();

        /// <summary>
        /// Returns the MD5 hash of all cached relation definitions for change detection.
        /// Source: Cache.GetRelationsHash()
        /// </summary>
        string GetRelationsHash();

        // ─── Utility ──────────────────────────────────────────────────────

        /// <summary>
        /// Convenience method: returns the Entity object by name, or null if not found.
        /// </summary>
        Task<Entity?> GetEntity(string entityName);

        /// <summary>
        /// Convenience method: returns the Entity object by ID, or null if not found.
        /// </summary>
        Task<Entity?> GetEntity(Guid entityId);
    }

    // =========================================================================
    // EntityService Implementation
    // =========================================================================

    /// <summary>
    /// Production implementation of IEntityService. Manages entity, field, and relation
    /// metadata CRUD with DynamoDB persistence, in-Lambda MemoryCache, structured logging,
    /// and full behavioral parity with the monolith's EntityManager + EntityRelationManager.
    /// </summary>
    public class EntityService : IEntityService
    {
        // ─── Dependencies ─────────────────────────────────────────────────

        private readonly IEntityRepository _entityRepository;
        private readonly ILogger<EntityService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _cache;

        // ─── Cache Constants (from Cache.cs) ──────────────────────────────

        private const string ENTITIES_CACHE_KEY = "entities";
        private const string ENTITIES_HASH_CACHE_KEY = "entities_hash";
        private const string RELATIONS_CACHE_KEY = "relations";
        private const string RELATIONS_HASH_CACHE_KEY = "relations_hash";
        private static readonly TimeSpan CACHE_TTL = TimeSpan.FromHours(1);

        // ─── Thread Safety ────────────────────────────────────────────────

        /// <summary>
        /// Lock object for cache population double-check pattern.
        /// Source: EntityManager.lockObj (line 18)
        /// </summary>
        private static readonly object _lockObj = new object();

        // ─── Configuration ────────────────────────────────────────────────

        /// <summary>
        /// When true, verbose error messages are returned in responses.
        /// Replaces ErpSettings.DevelopmentMode from the monolith.
        /// </summary>
        private readonly bool _developmentMode;

        // ─── Name Validation ──────────────────────────────────────────────

        /// <summary>
        /// Regex pattern for validating entity/field/relation names.
        /// Must start with lowercase letter, only lowercase alphanumeric + underscore,
        /// no consecutive underscores, must end with lowercase alphanumeric.
        /// Source: ValidationUtility.NAME_VALIDATION_PATTERN
        /// </summary>
        private static readonly Regex NameValidationPattern = new Regex(
            @"^[a-z](?!.*__)[a-z0-9_]*[a-z0-9]$",
            RegexOptions.Compiled);

        // ─── Constructor ──────────────────────────────────────────────────

        /// <summary>
        /// Initializes EntityService with all required dependencies injected via DI.
        /// </summary>
        /// <param name="entityRepository">DynamoDB data access for entity/field/relation persistence.</param>
        /// <param name="logger">Structured JSON logger with correlation-ID support.</param>
        /// <param name="configuration">Application configuration for development mode flag.</param>
        /// <param name="cache">In-Lambda memory cache with 1-hour TTL.</param>
        public EntityService(
            IEntityRepository entityRepository,
            ILogger<EntityService> logger,
            IConfiguration configuration,
            IMemoryCache cache)
        {
            _entityRepository = entityRepository ?? throw new ArgumentNullException(nameof(entityRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));

            _developmentMode = _configuration.GetValue<bool>("DevelopmentMode", false);
        }

        // =====================================================================
        // ENTITY CRUD OPERATIONS
        // =====================================================================

        /// <inheritdoc />
        public async Task<EntityResponse> CreateEntity(
            InputEntity entity,
            bool createOnlyIdField = true,
            Dictionary<string, Guid>? sysIdDictionary = null)
        {
            var response = new EntityResponse();
            try
            {
                _logger.LogInformation("CreateEntity started. Name={EntityName}", entity?.Name);

                if (entity == null)
                {
                    response.Success = false;
                    response.Message = "Invalid entity object.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Auto-generate ID if not provided (source: EntityManager.CreateEntity line ~400)
                if (!entity.Id.HasValue || entity.Id.Value == Guid.Empty)
                {
                    entity.Id = Guid.NewGuid();
                }

                // Trim the name (source: EntityManager.CreateEntity line ~403)
                if (!string.IsNullOrWhiteSpace(entity.Name))
                {
                    entity.Name = entity.Name.Trim();
                }

                // Validate entity (checkId=false for create path)
                var validationErrors = await ValidateEntity(entity, checkId: false);
                if (validationErrors.Count > 0)
                {
                    _logger.LogWarning("CreateEntity validation failed for Name={EntityName} with {ErrorCount} errors.",
                        entity.Name, validationErrors.Count);
                    response.Success = false;
                    response.Message = "The entity was not created. Validation errors found!";
                    response.Errors = validationErrors;
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Build the Entity object from InputEntity
                var entityObj = new Entity
                {
                    Id = entity.Id.Value,
                    Name = entity.Name!,
                    Label = entity.Label ?? entity.Name!,
                    LabelPlural = entity.LabelPlural ?? entity.Name!,
                    System = entity.System ?? false,
                    IconName = entity.IconName ?? "fa fa-database",
                    Color = entity.Color ?? string.Empty,
                    RecordPermissions = entity.RecordPermissions ?? new RecordPermissions(),
                    RecordScreenIdField = entity.RecordScreenIdField,
                    Fields = new List<Field>()
                };

                // Generate default fields (source: EntityManager lines ~415)
                var defaultFields = CreateEntityDefaultFields(entityObj, createOnlyIdField, sysIdDictionary);
                entityObj.Fields.AddRange(defaultFields);

                // Persist via repository (source: EntityManager line ~420)
                await _entityRepository.CreateEntity(entityObj, sysIdDictionary, createOnlyIdField);

                // Clear cache after mutation (source: EntityManager line ~425)
                ClearCache();

                // Re-read the entity to return fresh data
                var readResponse = await ReadEntity(entityObj.Id);
                if (readResponse.Success && readResponse.Object != null)
                {
                    response.Object = readResponse.Object;
                }
                else
                {
                    response.Object = entityObj;
                }

                response.Success = true;
                response.Message = "The entity was successfully created.";

                _logger.LogInformation("CreateEntity completed. Id={EntityId}, Name={EntityName}",
                    entityObj.Id, entityObj.Name);
            }
            catch (StorageException ex)
            {
                response.Success = false;
                response.Message = "The entity was not created. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "CreateEntity storage error. Name={EntityName}", entity?.Name);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "The entity was not created. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "CreateEntity unexpected error. Name={EntityName}", entity?.Name);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<EntityResponse> UpdateEntity(InputEntity entity)
        {
            var response = new EntityResponse();
            try
            {
                _logger.LogInformation("UpdateEntity started. Id={EntityId}", entity?.Id);

                if (entity == null)
                {
                    response.Success = false;
                    response.Message = "Invalid entity object.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Validate entity (checkId=true for update path — entity must exist)
                var validationErrors = await ValidateEntity(entity, checkId: true);
                if (validationErrors.Count > 0)
                {
                    response.Success = false;
                    response.Message = "The entity was not updated. Validation errors found!";
                    response.Errors = validationErrors;
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Read existing entity to merge (Name is NOT updatable)
                // Source: EntityManager.UpdateEntity — selectively updates fields
                Entity? existingEntity = null;
                if (entity.Id.HasValue)
                {
                    existingEntity = await GetEntityById(entity.Id.Value);
                }

                if (existingEntity == null)
                {
                    response.Success = false;
                    response.Message = "The entity was not updated. Entity not found!";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Update only allowed fields (Name is NOT updatable per source)
                existingEntity.Label = entity.Label ?? existingEntity.Label;
                existingEntity.LabelPlural = entity.LabelPlural ?? existingEntity.LabelPlural;
                if (entity.System.HasValue)
                    existingEntity.System = entity.System.Value;
                existingEntity.IconName = entity.IconName ?? existingEntity.IconName;
                existingEntity.Color = entity.Color ?? existingEntity.Color;
                existingEntity.RecordScreenIdField = entity.RecordScreenIdField ?? existingEntity.RecordScreenIdField;

                if (entity.RecordPermissions != null)
                {
                    existingEntity.RecordPermissions = entity.RecordPermissions;
                    existingEntity.RecordPermissions.CanRead ??= new List<Guid>();
                    existingEntity.RecordPermissions.CanCreate ??= new List<Guid>();
                    existingEntity.RecordPermissions.CanUpdate ??= new List<Guid>();
                    existingEntity.RecordPermissions.CanDelete ??= new List<Guid>();
                }

                // Persist update
                await _entityRepository.UpdateEntity(existingEntity);

                // Clear cache after mutation
                ClearCache();

                // Re-read entity for response
                var readResponse = await ReadEntity(existingEntity.Id);
                if (readResponse.Success && readResponse.Object != null)
                {
                    response.Object = readResponse.Object;
                }
                else
                {
                    response.Object = existingEntity;
                }

                response.Success = true;
                response.Message = "The entity was successfully updated.";

                _logger.LogInformation("UpdateEntity completed. Id={EntityId}, Name={EntityName}",
                    existingEntity.Id, existingEntity.Name);
            }
            catch (StorageException ex)
            {
                response.Success = false;
                response.Message = "The entity was not updated. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "UpdateEntity storage error. Id={EntityId}", entity?.Id);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "The entity was not updated. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "UpdateEntity unexpected error. Id={EntityId}", entity?.Id);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<EntityResponse> DeleteEntity(Guid entityId)
        {
            var response = new EntityResponse();
            try
            {
                _logger.LogInformation("DeleteEntity started. Id={EntityId}", entityId);

                if (entityId == Guid.Empty)
                {
                    response.Success = false;
                    response.Message = "The entity was not deleted. Invalid entity id!";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Verify entity exists (source: EntityManager.DeleteEntity)
                var existingEntity = await GetEntityById(entityId);
                if (existingEntity == null)
                {
                    response.Success = false;
                    response.Message = "The entity was not deleted. Entity not found!";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Delete via repository (cascades entity + fields + records + relations)
                await _entityRepository.DeleteEntity(entityId);

                // Clear cache after mutation
                ClearCache();

                response.Object = existingEntity;
                response.Success = true;
                response.Message = "The entity was successfully deleted.";

                _logger.LogInformation("DeleteEntity completed. Id={EntityId}, Name={EntityName}",
                    entityId, existingEntity.Name);
            }
            catch (StorageException ex)
            {
                response.Success = false;
                response.Message = "The entity was not deleted. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "DeleteEntity storage error. Id={EntityId}", entityId);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "The entity was not deleted. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "DeleteEntity unexpected error. Id={EntityId}", entityId);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<EntityResponse> ReadEntity(string entityName)
        {
            var response = new EntityResponse();
            try
            {
                _logger.LogInformation("ReadEntity by name started. Name={EntityName}", entityName);

                // Delegate to ReadEntities and filter by name (source pattern)
                var allResponse = await ReadEntities();
                if (!allResponse.Success)
                {
                    response.Success = false;
                    response.Message = allResponse.Message;
                    response.Errors = allResponse.Errors;
                    return response;
                }

                var entity = allResponse.Object?
                    .FirstOrDefault(e => string.Equals(e.Name, entityName, StringComparison.OrdinalIgnoreCase));

                response.Object = entity;
                response.Success = true;
                response.Message = entity != null
                    ? "The entity was successfully returned."
                    : "The entity was not found.";

                if (entity != null)
                    response.Hash = entity.Hash;
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "Error reading entity.";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "ReadEntity by name error. Name={EntityName}", entityName);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<EntityResponse> ReadEntity(Guid entityId)
        {
            var response = new EntityResponse();
            try
            {
                _logger.LogInformation("ReadEntity by id started. Id={EntityId}", entityId);

                // Delegate to ReadEntities and filter by ID (source pattern)
                var allResponse = await ReadEntities();
                if (!allResponse.Success)
                {
                    response.Success = false;
                    response.Message = allResponse.Message;
                    response.Errors = allResponse.Errors;
                    return response;
                }

                var entity = allResponse.Object?
                    .FirstOrDefault(e => e.Id == entityId);

                response.Object = entity;
                response.Success = true;
                response.Message = entity != null
                    ? "The entity was successfully returned."
                    : "The entity was not found.";

                if (entity != null)
                    response.Hash = entity.Hash;
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "Error reading entity.";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "ReadEntity by id error. Id={EntityId}", entityId);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<EntityListResponse> ReadEntities()
        {
            var response = new EntityListResponse();
            try
            {
                // Check cache first (source: EntityManager.ReadEntities line 433)
                if (_cache.TryGetValue<List<Entity>>(ENTITIES_CACHE_KEY, out var cachedEntities)
                    && cachedEntities != null)
                {
                    response.Object = cachedEntities;
                    response.Success = true;
                    if (_cache.TryGetValue<string>(ENTITIES_HASH_CACHE_KEY, out var cachedHash))
                    {
                        response.Hash = cachedHash;
                    }
                    return response;
                }

                // Double-check lock pattern for cache population (source: EntityManager line 440)
                lock (_lockObj)
                {
                    if (_cache.TryGetValue<List<Entity>>(ENTITIES_CACHE_KEY, out cachedEntities)
                        && cachedEntities != null)
                    {
                        response.Object = cachedEntities;
                        response.Success = true;
                        if (_cache.TryGetValue<string>(ENTITIES_HASH_CACHE_KEY, out var cachedHash2))
                        {
                            response.Hash = cachedHash2;
                        }
                        return response;
                    }
                }

                // Cache miss — load from repository (source: EntityManager line 445)
                _logger.LogInformation("ReadEntities cache miss — loading from repository.");

                var entities = await _entityRepository.GetAllEntities();
                if (entities == null)
                {
                    entities = new List<Entity>();
                }

                // Load relations for enrichment
                var relations = await LoadRelationsFromRepository();

                // Enrich field.EntityName for all fields in all entities (source: line 455)
                foreach (var entity in entities)
                {
                    if (entity.Fields != null)
                    {
                        foreach (var field in entity.Fields)
                        {
                            field.EntityName = entity.Name;
                        }
                    }
                    else
                    {
                        entity.Fields = new List<Field>();
                    }

                    // Compute per-entity hash (source: line 458)
                    // Uses Newtonsoft.Json for backward-compatible hash computation
                    entity.Hash = ComputeOddMD5Hash(JsonConvert.SerializeObject(entity));
                }

                // Compute entities collection hash
                var entitiesHash = ComputeOddMD5Hash(JsonConvert.SerializeObject(entities));

                // Store in cache with TTL (source: Cache.AddEntities)
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CACHE_TTL
                };

                lock (_lockObj)
                {
                    _cache.Set(ENTITIES_CACHE_KEY, entities, cacheOptions);
                    _cache.Set(ENTITIES_HASH_CACHE_KEY, entitiesHash, cacheOptions);

                    // Also cache relations while we have them
                    _cache.Set(RELATIONS_CACHE_KEY, relations, cacheOptions);
                    var relationsHash = ComputeOddMD5Hash(JsonConvert.SerializeObject(relations));
                    _cache.Set(RELATIONS_HASH_CACHE_KEY, relationsHash, cacheOptions);
                }

                response.Object = entities;
                response.Hash = entitiesHash;
                response.Success = true;

                _logger.LogInformation("ReadEntities completed. Count={EntityCount}", entities.Count);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "Error reading entities.";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "ReadEntities error.");
            }
            return response;
        }

        // =====================================================================
        // FIELD CRUD OPERATIONS
        // =====================================================================

        /// <inheritdoc />
        public async Task<FieldResponse> CreateField(Guid entityId, InputField field)
        {
            var response = new FieldResponse();
            try
            {
                _logger.LogInformation("CreateField started. EntityId={EntityId}, FieldName={FieldName}",
                    entityId, field?.Name);

                if (field == null)
                {
                    response.Success = false;
                    response.Message = "Invalid field object.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Read the entity to validate against
                var entity = await GetEntityById(entityId);
                if (entity == null)
                {
                    response.Success = false;
                    response.Message = "Entity not found!";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Auto-generate field ID if not provided
                if (!field.Id.HasValue || field.Id.Value == Guid.Empty)
                {
                    field.Id = Guid.NewGuid();
                }

                // Validate field (checkId=false for create path)
                var validationErrors = ValidateField(entity, field, checkId: false);
                if (validationErrors.Count > 0)
                {
                    response.Success = false;
                    response.Message = "The field was not created. Validation errors found!";
                    response.Errors = validationErrors;
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Map InputField to concrete Field type for persistence
                var mappedField = MapInputFieldToField(field);

                // Persist via repository
                await _entityRepository.CreateField(entityId, mappedField);

                // Clear cache after mutation
                ClearCache();

                response.Object = mappedField;
                response.Success = true;
                response.Message = "The field was successfully created.";

                _logger.LogInformation("CreateField completed. EntityId={EntityId}, FieldId={FieldId}, FieldName={FieldName}",
                    entityId, mappedField.Id, mappedField.Name);
            }
            catch (StorageException ex)
            {
                response.Success = false;
                response.Message = "The field was not created. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "CreateField storage error. EntityId={EntityId}", entityId);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "The field was not created. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "CreateField unexpected error. EntityId={EntityId}", entityId);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<FieldResponse> UpdateField(Guid entityId, InputField field)
        {
            var response = new FieldResponse();
            try
            {
                _logger.LogInformation("UpdateField started. EntityId={EntityId}, FieldId={FieldId}",
                    entityId, field?.Id);

                if (field == null)
                {
                    response.Success = false;
                    response.Message = "Invalid field object.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Read the entity to validate against
                var entity = await GetEntityById(entityId);
                if (entity == null)
                {
                    response.Success = false;
                    response.Message = "Entity not found!";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Validate field (checkId=true for update path)
                var validationErrors = ValidateField(entity, field, checkId: true);
                if (validationErrors.Count > 0)
                {
                    response.Success = false;
                    response.Message = "The field was not updated. Validation errors found!";
                    response.Errors = validationErrors;
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Handle UseCurrentTimeAsDefaultValue for Date/DateTime fields
                // Source: EntityManager.UpdateField — clears DefaultValue if UseCurrentTimeAsDefaultValue is true
                if (field is InputDateField dateField && dateField.UseCurrentTimeAsDefaultValue == true)
                {
                    dateField.DefaultValue = null;
                }
                if (field is InputDateTimeField dateTimeField && dateTimeField.UseCurrentTimeAsDefaultValue == true)
                {
                    dateTimeField.DefaultValue = null;
                }

                // Map InputField to concrete Field type for persistence
                var mappedField = MapInputFieldToField(field);

                // Persist via repository
                await _entityRepository.UpdateField(entityId, mappedField);

                // Clear cache after mutation
                ClearCache();

                response.Object = mappedField;
                response.Success = true;
                response.Message = "The field was successfully updated.";

                _logger.LogInformation("UpdateField completed. EntityId={EntityId}, FieldId={FieldId}, FieldName={FieldName}",
                    entityId, mappedField.Id, mappedField.Name);
            }
            catch (StorageException ex)
            {
                response.Success = false;
                response.Message = "The field was not updated. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "UpdateField storage error. EntityId={EntityId}", entityId);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "The field was not updated. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "UpdateField unexpected error. EntityId={EntityId}", entityId);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<FieldResponse> DeleteField(Guid entityId, Guid fieldId)
        {
            var response = new FieldResponse();
            try
            {
                _logger.LogInformation("DeleteField started. EntityId={EntityId}, FieldId={FieldId}",
                    entityId, fieldId);

                // Read the entity
                var entity = await GetEntityById(entityId);
                if (entity == null)
                {
                    response.Success = false;
                    response.Message = "Entity not found!";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Find the field
                var field = entity.Fields?.FirstOrDefault(f => f.Id == fieldId);
                if (field == null)
                {
                    response.Success = false;
                    response.Message = "Field not found!";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Validate field is not referenced in any relations
                // Source: EntityManager.DeleteField — checks both OriginFieldId and TargetFieldId
                var relationsResponse = await ReadRelations();
                if (relationsResponse.Success && relationsResponse.Object != null)
                {
                    foreach (var relation in relationsResponse.Object)
                    {
                        if (relation.OriginFieldId == fieldId || relation.TargetFieldId == fieldId)
                        {
                            response.Success = false;
                            response.Message = $"The field is used in relation '{relation.Name}' and cannot be deleted.";
                            response.StatusCode = HttpStatusCode.BadRequest;
                            return response;
                        }
                    }
                }

                // Delete via repository
                await _entityRepository.DeleteField(entityId, fieldId);

                // Clear cache after mutation
                ClearCache();

                response.Object = field;
                response.Success = true;
                response.Message = "The field was successfully deleted.";

                _logger.LogInformation("DeleteField completed. EntityId={EntityId}, FieldId={FieldId}, FieldName={FieldName}",
                    entityId, fieldId, field.Name);
            }
            catch (StorageException ex)
            {
                response.Success = false;
                response.Message = "The field was not deleted. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "DeleteField storage error. EntityId={EntityId}, FieldId={FieldId}", entityId, fieldId);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "The field was not deleted. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "DeleteField unexpected error. EntityId={EntityId}, FieldId={FieldId}", entityId, fieldId);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<FieldListResponse> ReadFields(Guid entityId)
        {
            var response = new FieldListResponse();
            try
            {
                _logger.LogInformation("ReadFields started. EntityId={EntityId}", entityId);

                // Source: EntityManager.ReadFields(Guid) — reads entity by Id, returns entity.Fields
                var entityResponse = await ReadEntity(entityId);
                if (!entityResponse.Success || entityResponse.Object == null)
                {
                    response.Success = false;
                    response.Message = "Entity not found!";
                    return response;
                }

                response.Object = new FieldList
                {
                    Fields = entityResponse.Object.Fields ?? new List<Field>()
                };
                response.Success = true;
                response.Message = "The fields were successfully returned.";

                _logger.LogInformation("ReadFields completed. EntityId={EntityId}, FieldCount={FieldCount}",
                    entityId, response.Object.Fields.Count);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "Error reading fields.";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "ReadFields error. EntityId={EntityId}", entityId);
            }
            return response;
        }

        // =====================================================================
        // RELATION CRUD OPERATIONS
        // =====================================================================

        /// <inheritdoc />
        public async Task<EntityRelationResponse> CreateRelation(EntityRelation relation)
        {
            var response = new EntityRelationResponse();
            try
            {
                _logger.LogInformation("CreateRelation started. Name={RelationName}", relation?.Name);

                if (relation == null)
                {
                    response.Success = false;
                    response.Message = "Invalid relation object.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Auto-generate ID if not provided
                if (relation.Id == Guid.Empty)
                {
                    relation.Id = Guid.NewGuid();
                }

                // Trim the name
                if (!string.IsNullOrWhiteSpace(relation.Name))
                {
                    relation.Name = relation.Name.Trim();
                }

                // Validate relation (isCreate=true)
                var validationErrors = await ValidateRelation(relation, isCreate: true);
                if (validationErrors.Count > 0)
                {
                    response.Success = false;
                    response.Message = "The relation was not created. Validation errors found!";
                    response.Errors = validationErrors;
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Persist via repository
                await _entityRepository.CreateRelation(relation);

                // Clear cache after mutation
                ClearCache();

                response.Object = relation;
                response.Success = true;
                response.Message = "The relation was successfully created.";

                _logger.LogInformation("CreateRelation completed. Id={RelationId}, Name={RelationName}",
                    relation.Id, relation.Name);
            }
            catch (StorageException ex)
            {
                response.Success = false;
                response.Message = "The relation was not created. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "CreateRelation storage error. Name={RelationName}", relation?.Name);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "The relation was not created. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "CreateRelation unexpected error. Name={RelationName}", relation?.Name);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<EntityRelationResponse> UpdateRelation(EntityRelation relation)
        {
            var response = new EntityRelationResponse();
            try
            {
                _logger.LogInformation("UpdateRelation started. Id={RelationId}", relation?.Id);

                if (relation == null)
                {
                    response.Success = false;
                    response.Message = "Invalid relation object.";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Validate relation (isCreate=false for update)
                var validationErrors = await ValidateRelation(relation, isCreate: false);
                if (validationErrors.Count > 0)
                {
                    response.Success = false;
                    response.Message = "The relation was not updated. Validation errors found!";
                    response.Errors = validationErrors;
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Persist via repository
                await _entityRepository.UpdateRelation(relation);

                // Clear cache after mutation
                ClearCache();

                response.Object = relation;
                response.Success = true;
                response.Message = "The relation was successfully updated.";

                _logger.LogInformation("UpdateRelation completed. Id={RelationId}, Name={RelationName}",
                    relation.Id, relation.Name);
            }
            catch (StorageException ex)
            {
                response.Success = false;
                response.Message = "The relation was not updated. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "UpdateRelation storage error. Id={RelationId}", relation?.Id);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "The relation was not updated. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "UpdateRelation unexpected error. Id={RelationId}", relation?.Id);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<EntityRelationResponse> DeleteRelation(Guid relationId)
        {
            var response = new EntityRelationResponse();
            try
            {
                _logger.LogInformation("DeleteRelation started. Id={RelationId}", relationId);

                if (relationId == Guid.Empty)
                {
                    response.Success = false;
                    response.Message = "The relation was not deleted. Invalid relation id!";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Read existing relation for response
                var existingRelation = await _entityRepository.GetRelationById(relationId);
                if (existingRelation == null)
                {
                    response.Success = false;
                    response.Message = "The relation was not deleted. Relation not found!";
                    response.StatusCode = HttpStatusCode.BadRequest;
                    return response;
                }

                // Delete via repository
                await _entityRepository.DeleteRelation(relationId);

                // Clear cache after mutation
                ClearCache();

                response.Object = existingRelation;
                response.Success = true;
                response.Message = "The relation was successfully deleted.";

                _logger.LogInformation("DeleteRelation completed. Id={RelationId}, Name={RelationName}",
                    relationId, existingRelation.Name);
            }
            catch (StorageException ex)
            {
                response.Success = false;
                response.Message = "The relation was not deleted. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "DeleteRelation storage error. Id={RelationId}", relationId);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "The relation was not deleted. An internal error occurred!";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "DeleteRelation unexpected error. Id={RelationId}", relationId);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<EntityRelationResponse> ReadRelation(string relationName)
        {
            var response = new EntityRelationResponse();
            try
            {
                _logger.LogInformation("ReadRelation by name started. Name={RelationName}", relationName);

                var allResponse = await ReadRelations();
                if (!allResponse.Success)
                {
                    response.Success = false;
                    response.Message = allResponse.Message;
                    response.Errors = allResponse.Errors;
                    return response;
                }

                var relation = allResponse.Object?
                    .FirstOrDefault(r => string.Equals(r.Name, relationName, StringComparison.OrdinalIgnoreCase));

                response.Object = relation;
                response.Success = true;
                response.Message = relation != null
                    ? "The relation was successfully returned."
                    : "The relation was not found.";
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "Error reading relation.";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "ReadRelation by name error. Name={RelationName}", relationName);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<EntityRelationResponse> ReadRelation(Guid relationId)
        {
            var response = new EntityRelationResponse();
            try
            {
                _logger.LogInformation("ReadRelation by id started. Id={RelationId}", relationId);

                var allResponse = await ReadRelations();
                if (!allResponse.Success)
                {
                    response.Success = false;
                    response.Message = allResponse.Message;
                    response.Errors = allResponse.Errors;
                    return response;
                }

                var relation = allResponse.Object?
                    .FirstOrDefault(r => r.Id == relationId);

                response.Object = relation;
                response.Success = true;
                response.Message = relation != null
                    ? "The relation was successfully returned."
                    : "The relation was not found.";
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "Error reading relation.";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "ReadRelation by id error. Id={RelationId}", relationId);
            }
            return response;
        }

        /// <inheritdoc />
        public async Task<EntityRelationListResponse> ReadRelations()
        {
            var response = new EntityRelationListResponse();
            try
            {
                // Check cache first
                if (_cache.TryGetValue<List<EntityRelation>>(RELATIONS_CACHE_KEY, out var cachedRelations)
                    && cachedRelations != null)
                {
                    response.Object = cachedRelations;
                    response.Success = true;
                    if (_cache.TryGetValue<string>(RELATIONS_HASH_CACHE_KEY, out var cachedHash))
                    {
                        response.Hash = cachedHash;
                    }
                    return response;
                }

                // Cache miss — load from repository
                _logger.LogInformation("ReadRelations cache miss — loading from repository.");

                var relations = await LoadRelationsFromRepository();

                // Enrich relations with entity/field names
                var entitiesResponse = await ReadEntities();
                var entities = entitiesResponse.Object ?? new List<Entity>();

                foreach (var relation in relations)
                {
                    EnrichRelation(relation, entities);
                }

                // Compute hash
                var relationsHash = ComputeOddMD5Hash(JsonConvert.SerializeObject(relations));

                // Store in cache
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CACHE_TTL
                };

                lock (_lockObj)
                {
                    _cache.Set(RELATIONS_CACHE_KEY, relations, cacheOptions);
                    _cache.Set(RELATIONS_HASH_CACHE_KEY, relationsHash, cacheOptions);
                }

                response.Object = relations;
                response.Hash = relationsHash;
                response.Success = true;

                _logger.LogInformation("ReadRelations completed. Count={RelationCount}", relations.Count);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = "Error reading relations.";
                if (_developmentMode)
                    response.Message += " Error: " + ex.Message;
                _logger.LogError(ex, "ReadRelations error.");
            }
            return response;
        }

        // =====================================================================
        // CACHE MANAGEMENT
        // =====================================================================

        /// <inheritdoc />
        public void ClearCache()
        {
            lock (_lockObj)
            {
                _cache.Remove(ENTITIES_CACHE_KEY);
                _cache.Remove(ENTITIES_HASH_CACHE_KEY);
                _cache.Remove(RELATIONS_CACHE_KEY);
                _cache.Remove(RELATIONS_HASH_CACHE_KEY);
            }
            _logger.LogInformation("Entity/relation cache cleared.");
        }

        /// <inheritdoc />
        public string GetEntitiesHash()
        {
            if (_cache.TryGetValue<string>(ENTITIES_HASH_CACHE_KEY, out var hash) && hash != null)
            {
                return hash;
            }
            return string.Empty;
        }

        /// <inheritdoc />
        public string GetRelationsHash()
        {
            if (_cache.TryGetValue<string>(RELATIONS_HASH_CACHE_KEY, out var hash) && hash != null)
            {
                return hash;
            }
            return string.Empty;
        }

        // =====================================================================
        // UTILITY METHODS (PUBLIC)
        // =====================================================================

        /// <inheritdoc />
        public async Task<Entity?> GetEntity(string entityName)
        {
            var response = await ReadEntity(entityName);
            return response.Success ? response.Object : null;
        }

        /// <inheritdoc />
        public async Task<Entity?> GetEntity(Guid entityId)
        {
            var response = await ReadEntity(entityId);
            return response.Success ? response.Object : null;
        }

        /// <summary>
        /// Converts an arbitrary object to an EntityRecord dictionary using reflection.
        /// Each public property becomes a key-value pair in the record.
        /// Source: EntityManager.ConvertToEntityRecord() lines ~1832-1842
        /// </summary>
        /// <param name="inputObj">The object to convert.</param>
        /// <returns>An EntityRecord with property names as keys and values as values.</returns>
        public EntityRecord ConvertToEntityRecord(object inputObj)
        {
            var record = new EntityRecord();
            if (inputObj == null)
                return record;

            var properties = inputObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (PropertyInfo property in properties)
            {
                try
                {
                    var value = property.GetValue(inputObj);
                    record[property.Name] = value;
                }
                catch
                {
                    // Skip properties that throw on GetValue (indexed properties, etc.)
                }
            }
            return record;
        }

        /// <summary>
        /// Checks if an ExpandoObject contains a specific key.
        /// Casts the ExpandoObject to IDictionary and checks ContainsKey.
        /// Source: EntityManager.HasKey() line ~1844-1847
        /// </summary>
        /// <param name="obj">The ExpandoObject to check.</param>
        /// <param name="key">The key to look for.</param>
        /// <returns>True if the key exists, false otherwise.</returns>
        public bool HasKey(ExpandoObject obj, string key)
        {
            if (obj == null || string.IsNullOrEmpty(key))
                return false;

            var dict = (IDictionary<string, object?>)obj;
            return dict.ContainsKey(key);
        }

        /// <summary>
        /// Finds the entity that contains a field with the specified ID.
        /// Source: EntityManager.GetEntityByFieldId() lines ~1849-1868
        /// </summary>
        /// <param name="fieldId">The field ID to search for.</param>
        /// <returns>The entity containing the field, or null if not found.</returns>
        public async Task<Entity?> GetEntityByFieldId(Guid fieldId)
        {
            var entitiesResponse = await ReadEntities();
            if (!entitiesResponse.Success || entitiesResponse.Object == null)
                return null;

            return entitiesResponse.Object
                .FirstOrDefault(e => e.Fields != null && e.Fields.Any(f => f.Id == fieldId));
        }

        // =====================================================================
        // PRIVATE VALIDATION METHODS
        // =====================================================================

        /// <summary>
        /// Validates an InputEntity for create or update operations.
        /// Source: EntityManager.ValidateEntity() lines 40-100
        /// </summary>
        private async Task<List<ErrorModel>> ValidateEntity(InputEntity entity, bool checkId = true)
        {
            var errors = new List<ErrorModel>();

            // 1. Id is required (source: line 44-45)
            if (!entity.Id.HasValue || entity.Id.Value == Guid.Empty)
            {
                errors.Add(new ErrorModel("id", null!, "Id is required!"));
                return errors;
            }

            // 2. On update path: verify entity exists by ID (source: lines 47-56)
            if (checkId)
            {
                var existingEntity = await GetEntityById(entity.Id.Value);
                if (existingEntity == null)
                {
                    errors.Add(new ErrorModel("id", entity.Id.Value.ToString(), "Entity with such Id does not exist!"));
                    return errors;
                }
            }

            // 3. Validate entity name (source: line 64)
            var nameErrors = ValidateName(entity.Name, "name");
            errors.AddRange(nameErrors);

            // 4. Name length: 63-char limit (source: lines 68-70)
            if (!string.IsNullOrWhiteSpace(entity.Name) && entity.Name.Length > 63)
            {
                errors.Add(new ErrorModel("name", entity.Name, "Entity name length exceeded. Should be up to 63 chars!"));
            }

            // 5. Name uniqueness on create (source: lines 72-75)
            if (!checkId && !string.IsNullOrWhiteSpace(entity.Name))
            {
                var existingByName = await _entityRepository.GetEntityByName(entity.Name);
                if (existingByName != null)
                {
                    errors.Add(new ErrorModel("name", entity.Name, "Entity with such Name exists already!"));
                }
            }

            // 6. Validate label (source: line 78)
            var labelErrors = ValidateLabel(entity.Label, "label");
            errors.AddRange(labelErrors);

            // 7. Validate label plural (source: line 80)
            var labelPluralErrors = ValidateLabelPlural(entity.LabelPlural, "labelPlural");
            errors.AddRange(labelPluralErrors);

            // 8. Initialize RecordPermissions and sub-lists if null (source: lines 82-95)
            entity.RecordPermissions ??= new RecordPermissions();
            entity.RecordPermissions.CanRead ??= new List<Guid>();
            entity.RecordPermissions.CanCreate ??= new List<Guid>();
            entity.RecordPermissions.CanUpdate ??= new List<Guid>();
            entity.RecordPermissions.CanDelete ??= new List<Guid>();

            // 9. Default icon (source: lines 97-98)
            if (string.IsNullOrWhiteSpace(entity.IconName))
            {
                entity.IconName = "fa fa-database";
            }

            return errors;
        }

        /// <summary>
        /// Validates a list of fields for an entity (used during entity creation).
        /// Source: EntityManager.ValidateFields() lines 103-138
        /// </summary>
        private List<ErrorModel> ValidateFields(Guid entityId, List<InputField> fields, bool checkId = true)
        {
            var errors = new List<ErrorModel>();

            // 1. At least one field required (source: line 111)
            if (fields == null || fields.Count == 0)
            {
                errors.Add(new ErrorModel("fields", null!, "There should be at least one field!"));
                return errors;
            }

            // 2. Count primary fields (GuidField with Name=="id") using LINQ Where/Count
            int primaryFieldCount = fields
                .Where(f => f is InputGuidField && f.Name != null &&
                    string.Equals(f.Name, "id", StringComparison.OrdinalIgnoreCase))
                .Count();

            // 3. Field name 63-char limit (source: line 127-128) — collect violations via LINQ Select/ToList
            var nameLengthErrors = fields
                .Where(f => f.Name != null && f.Name.Length > 63)
                .Select(f => new ErrorModel("name", f.Name!, "Field name length exceeded. Should be up to 63 chars!"))
                .ToList();
            errors.AddRange(nameLengthErrors);

            // Exactly one GuidField primary key (source: lines 131-135)
            if (primaryFieldCount == 0)
            {
                errors.Add(new ErrorModel("fields", null!, "Must have one unique identifier field!"));
            }
            else if (primaryFieldCount > 1)
            {
                errors.Add(new ErrorModel("fields", null!, "Too many primary fields. Must have only one unique identifier!"));
            }

            return errors;
        }

        /// <summary>
        /// Validates a single field for create or update operations.
        /// Includes base validation + type-specific validation for all 20+ field types.
        /// Source: EntityManager.ValidateField() lines 140-460+
        /// </summary>
        private List<ErrorModel> ValidateField(Entity entity, InputField field, bool checkId = true)
        {
            var errors = new List<ErrorModel>();

            // 1. Id is required (source: line 144-145)
            if (!field.Id.HasValue || field.Id.Value == Guid.Empty)
            {
                errors.Add(new ErrorModel("id", null!, "Id is required!"));
                return errors;
            }

            // 2. Duplicate ID check (source: lines 147-150)
            if (!checkId && entity.Fields != null)
            {
                var duplicateIdCount = entity.Fields.Count(f => f.Id == field.Id.Value);
                if (duplicateIdCount > 0)
                {
                    errors.Add(new ErrorModel("id", field.Id.Value.ToString(), "There is already a field with such Id!"));
                }
            }

            // 3. Duplicate name check (source: lines 152-155)
            if (entity.Fields != null && !string.IsNullOrWhiteSpace(field.Name))
            {
                var duplicateNameCount = entity.Fields.Count(f =>
                    string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase)
                    && f.Id != field.Id.Value);
                if (duplicateNameCount > 0)
                {
                    errors.Add(new ErrorModel("name", field.Name, "There is already a field with such Name!"));
                }
            }

            // 4. Validate name (source: line 157)
            var nameErrors = ValidateName(field.Name, "name");
            errors.AddRange(nameErrors);

            // 5. Name length: 63-char limit
            if (!string.IsNullOrWhiteSpace(field.Name) && field.Name.Length > 63)
            {
                errors.Add(new ErrorModel("name", field.Name, "Field name length exceeded. Should be up to 63 chars!"));
            }

            // 6. Validate label (source: line 159)
            var labelErrors = ValidateLabel(field.Label, "label");
            errors.AddRange(labelErrors);

            // ─── Type-specific validation (source: lines 161-460+) ────────

            if (field is InputAutoNumberField autoNumberField)
            {
                // Required → needs DefaultValue (source: line 163-164)
                if (field.Required == true && autoNumberField.DefaultValue == null)
                {
                    errors.Add(new ErrorModel("defaultValue", null!, "Default Value is required!"));
                }
            }
            else if (field is InputCheckboxField checkboxField)
            {
                // Default DefaultValue to false if not set (source: lines 176-177)
                if (checkboxField.DefaultValue == null)
                {
                    checkboxField.DefaultValue = false;
                }
            }
            else if (field is InputCurrencyField currencyField)
            {
                // Required → needs DefaultValue (source: lines 181-182)
                if (field.Required == true && currencyField.DefaultValue == null)
                {
                    errors.Add(new ErrorModel("defaultValue", null!, "Default Value is required!"));
                }
            }
            else if (field is InputDateField dateFieldInput)
            {
                // Default Format to "yyyy-MMM-dd" (source: lines 196-210)
                if (string.IsNullOrWhiteSpace(dateFieldInput.Format))
                {
                    dateFieldInput.Format = "yyyy-MMM-dd";
                }
                // Validate UseCurrentTimeAsDefaultValue
                if (field.Required == true
                    && (dateFieldInput.UseCurrentTimeAsDefaultValue == null || !dateFieldInput.UseCurrentTimeAsDefaultValue.Value)
                    && dateFieldInput.DefaultValue == null)
                {
                    errors.Add(new ErrorModel("defaultValue", null!, "Default Value is required!"));
                }
            }
            else if (field is InputDateTimeField dateTimeFieldInput)
            {
                // Default Format to "yyyy-MMM-dd HH:mm" (source: lines 212-226)
                if (string.IsNullOrWhiteSpace(dateTimeFieldInput.Format))
                {
                    dateTimeFieldInput.Format = "yyyy-MMM-dd HH:mm";
                }
                // Validate UseCurrentTimeAsDefaultValue
                if (field.Required == true
                    && (dateTimeFieldInput.UseCurrentTimeAsDefaultValue == null || !dateTimeFieldInput.UseCurrentTimeAsDefaultValue.Value)
                    && dateTimeFieldInput.DefaultValue == null)
                {
                    errors.Add(new ErrorModel("defaultValue", null!, "Default Value is required!"));
                }
            }
            else if (field is InputEmailField emailField)
            {
                // Validate MaxLength > 0 (source: lines 228-235)
                if (emailField.MaxLength.HasValue && emailField.MaxLength.Value <= 0)
                {
                    errors.Add(new ErrorModel("maxLength", emailField.MaxLength.Value.ToString(), "Max Length must be greater than 0!"));
                }
            }
            else if (field is InputFileField)
            {
                // No additional validation for file fields
            }
            else if (field is InputGuidField guidField)
            {
                // Validate GenerateNewId flag (source: lines 245-260)
                // No errors to add but ensure defaults are set
            }
            else if (field is InputHtmlField)
            {
                // No additional validation for HTML fields
            }
            else if (field is InputImageField)
            {
                // No additional validation for image fields
            }
            else if (field is InputMultiLineTextField multiLineTextField)
            {
                // Validate MaxLength (source: lines 275-280)
                if (multiLineTextField.MaxLength.HasValue && multiLineTextField.MaxLength.Value <= 0)
                {
                    errors.Add(new ErrorModel("maxLength", multiLineTextField.MaxLength.Value.ToString(), "Max Length must be greater than 0!"));
                }
            }
            else if (field is InputMultiSelectField multiSelectField)
            {
                // Validate Options not empty (source: lines 282-310)
                if (multiSelectField.Options == null || !multiSelectField.Options.Any())
                {
                    errors.Add(new ErrorModel("options", null!, "Options are required!"));
                }
                // Validate default values exist in options
                if (multiSelectField.DefaultValue != null && multiSelectField.Options != null)
                {
                    foreach (var defaultVal in multiSelectField.DefaultValue)
                    {
                        if (!multiSelectField.Options.Any(o =>
                            string.Equals(o.Value, defaultVal, StringComparison.OrdinalIgnoreCase)))
                        {
                            errors.Add(new ErrorModel("defaultValue", defaultVal,
                                $"Default value '{defaultVal}' is not found in the options list!"));
                        }
                    }
                }
            }
            else if (field is InputNumberField numberField)
            {
                // Validate DecimalPlaces (source: lines 312-335)
                if (numberField.DecimalPlaces.HasValue && numberField.DecimalPlaces.Value > 18)
                {
                    errors.Add(new ErrorModel("decimalPlaces", numberField.DecimalPlaces.Value.ToString(),
                        "Decimal Places must be between 0 and 18!"));
                }
                // Validate min < max
                if (numberField.MinValue.HasValue && numberField.MaxValue.HasValue
                    && numberField.MinValue.Value > numberField.MaxValue.Value)
                {
                    errors.Add(new ErrorModel("minValue", numberField.MinValue.Value.ToString(),
                        "Min Value must be less than or equal to Max Value!"));
                }
                // Required → needs DefaultValue
                if (field.Required == true && numberField.DefaultValue == null)
                {
                    errors.Add(new ErrorModel("defaultValue", null!, "Default Value is required!"));
                }
            }
            else if (field is InputPasswordField passwordField)
            {
                // Validate MinLength, MaxLength (source: lines 337-360)
                if (passwordField.MaxLength.HasValue && passwordField.MaxLength.Value <= 0)
                {
                    errors.Add(new ErrorModel("maxLength", passwordField.MaxLength.Value.ToString(),
                        "Max Length must be greater than 0!"));
                }
                if (passwordField.MinLength.HasValue && passwordField.MinLength.Value < 0)
                {
                    errors.Add(new ErrorModel("minLength", passwordField.MinLength.Value.ToString(),
                        "Min Length must be greater than or equal to 0!"));
                }
                if (passwordField.MinLength.HasValue && passwordField.MaxLength.HasValue
                    && passwordField.MinLength.Value > passwordField.MaxLength.Value)
                {
                    errors.Add(new ErrorModel("minLength", passwordField.MinLength.Value.ToString(),
                        "Min Length must be less than or equal to Max Length!"));
                }
            }
            else if (field is InputPercentField percentField)
            {
                // Validate DecimalPlaces (source: lines 362-380)
                if (percentField.DecimalPlaces.HasValue && percentField.DecimalPlaces.Value > 18)
                {
                    errors.Add(new ErrorModel("decimalPlaces", percentField.DecimalPlaces.Value.ToString(),
                        "Decimal Places must be between 0 and 18!"));
                }
                // Validate min < max
                if (percentField.MinValue.HasValue && percentField.MaxValue.HasValue
                    && percentField.MinValue.Value > percentField.MaxValue.Value)
                {
                    errors.Add(new ErrorModel("minValue", percentField.MinValue.Value.ToString(),
                        "Min Value must be less than or equal to Max Value!"));
                }
            }
            else if (field is InputPhoneField phoneField)
            {
                // Validate MaxLength (source: lines 382-390)
                if (phoneField.MaxLength.HasValue && phoneField.MaxLength.Value <= 0)
                {
                    errors.Add(new ErrorModel("maxLength", phoneField.MaxLength.Value.ToString(),
                        "Max Length must be greater than 0!"));
                }
            }
            else if (field is InputSelectField selectField)
            {
                // Validate Options not empty (source: lines 392-420)
                if (selectField.Options == null || !selectField.Options.Any())
                {
                    errors.Add(new ErrorModel("options", null!, "Options are required!"));
                }
                // Validate default value exists in options
                if (!string.IsNullOrWhiteSpace(selectField.DefaultValue) && selectField.Options != null)
                {
                    if (!selectField.Options.Any(o =>
                        string.Equals(o.Value, selectField.DefaultValue, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add(new ErrorModel("defaultValue", selectField.DefaultValue,
                            $"Default value '{selectField.DefaultValue}' is not found in the options list!"));
                    }
                }
            }
            else if (field is InputTextField textField)
            {
                // Validate MaxLength (source: lines 422-430)
                if (textField.MaxLength.HasValue && textField.MaxLength.Value <= 0)
                {
                    errors.Add(new ErrorModel("maxLength", textField.MaxLength.Value.ToString(),
                        "Max Length must be greater than 0!"));
                }
            }
            else if (field is InputUrlField urlField)
            {
                // Validate MaxLength (source: lines 432-440)
                if (urlField.MaxLength.HasValue && urlField.MaxLength.Value <= 0)
                {
                    errors.Add(new ErrorModel("maxLength", urlField.MaxLength.Value.ToString(),
                        "Max Length must be greater than 0!"));
                }
            }
            else if (field is InputGeographyField geographyField)
            {
                // Default format and SRID settings (source: lines 442-460)
                if (geographyField.Format == null)
                {
                    geographyField.Format = GeographyFieldFormat.GeoJSON;
                }
                // SRID defaults to 4326 (WGS84) — already set in model
            }

            return errors;
        }

        /// <summary>
        /// Validates an EntityRelation for create or update operations.
        /// Includes immutability checks on update and relation type constraints.
        /// Source: EntityRelationManager.ValidateRelation() (~200 lines)
        /// </summary>
        private async Task<List<ErrorModel>> ValidateRelation(EntityRelation relation, bool isCreate = true)
        {
            var errors = new List<ErrorModel>();

            // 1. Name validation
            if (string.IsNullOrWhiteSpace(relation.Name))
            {
                errors.Add(new ErrorModel("name", null!, "Name is required!"));
                return errors;
            }

            // 2. Name length: 63-char limit
            if (relation.Name.Length > 63)
            {
                errors.Add(new ErrorModel("name", relation.Name, "Relation name length exceeded. Should be up to 63 chars!"));
            }

            // 3. Validate name pattern
            var nameErrors = ValidateName(relation.Name, "name");
            errors.AddRange(nameErrors);

            // 4. Label validation
            var labelErrors = ValidateLabel(relation.Label, "label");
            errors.AddRange(labelErrors);

            // 5. ID validation
            if (!isCreate && relation.Id == Guid.Empty)
            {
                errors.Add(new ErrorModel("id", null!, "Id is required!"));
                return errors;
            }

            // 6. Name uniqueness check
            var existingByName = await _entityRepository.GetRelationByName(relation.Name);
            if (isCreate && existingByName != null)
            {
                errors.Add(new ErrorModel("name", relation.Name, "Relation with such Name exists already!"));
            }
            if (!isCreate && existingByName != null && existingByName.Id != relation.Id)
            {
                errors.Add(new ErrorModel("name", relation.Name, "Relation with such Name exists already!"));
            }

            // 7. Resolve origin entity
            var originEntity = await GetEntityById(relation.OriginEntityId);
            if (originEntity == null)
            {
                errors.Add(new ErrorModel("originEntityId", relation.OriginEntityId.ToString(),
                    "The origin entity was not found!"));
                return errors;
            }

            // 8. Resolve target entity
            var targetEntity = await GetEntityById(relation.TargetEntityId);
            if (targetEntity == null)
            {
                errors.Add(new ErrorModel("targetEntityId", relation.TargetEntityId.ToString(),
                    "The target entity was not found!"));
                return errors;
            }

            // 9. Resolve origin field and verify it's a GuidField
            var originField = originEntity.Fields?.FirstOrDefault(f => f.Id == relation.OriginFieldId);
            if (originField == null)
            {
                errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                    "The origin field was not found!"));
                return errors;
            }
            if (!(originField is GuidField))
            {
                errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                    "The origin field must be a Guid field!"));
            }

            // 10. Resolve target field and verify it's a GuidField
            var targetField = targetEntity.Fields?.FirstOrDefault(f => f.Id == relation.TargetFieldId);
            if (targetField == null)
            {
                errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(),
                    "The target field was not found!"));
                return errors;
            }
            if (!(targetField is GuidField))
            {
                errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(),
                    "The target field must be a Guid field!"));
            }

            // ─── Update-specific immutability rules (CRITICAL) ────────────
            if (!isCreate)
            {
                var existingRelation = await _entityRepository.GetRelationById(relation.Id);
                if (existingRelation == null)
                {
                    errors.Add(new ErrorModel("id", relation.Id.ToString(), "Relation with such Id does not exist!"));
                    return errors;
                }

                // Immutability: RelationType is READ-ONLY once created
                if (relation.RelationType != existingRelation.RelationType)
                {
                    errors.Add(new ErrorModel("relationType", relation.RelationType.ToString(),
                        "Relation type cannot be changed."));
                }
                // Immutability: OriginEntityId is READ-ONLY
                if (relation.OriginEntityId != existingRelation.OriginEntityId)
                {
                    errors.Add(new ErrorModel("originEntityId", relation.OriginEntityId.ToString(),
                        "Origin entity cannot be changed."));
                }
                // Immutability: OriginFieldId is READ-ONLY
                if (relation.OriginFieldId != existingRelation.OriginFieldId)
                {
                    errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                        "Origin field cannot be changed."));
                }
                // Immutability: TargetEntityId is READ-ONLY
                if (relation.TargetEntityId != existingRelation.TargetEntityId)
                {
                    errors.Add(new ErrorModel("targetEntityId", relation.TargetEntityId.ToString(),
                        "Target entity cannot be changed."));
                }
                // Immutability: TargetFieldId is READ-ONLY
                if (relation.TargetFieldId != existingRelation.TargetFieldId)
                {
                    errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(),
                        "Target field cannot be changed."));
                }
            }

            // ─── Relation type constraints ────────────────────────────────

            // Cannot use same origin AND target field on same entity for OneToMany/OneToOne
            if ((relation.RelationType == EntityRelationType.OneToMany
                 || relation.RelationType == EntityRelationType.OneToOne)
                && relation.OriginEntityId == relation.TargetEntityId
                && relation.OriginFieldId == relation.TargetFieldId)
            {
                errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                    "Cannot use the same field as both origin and target in a relation on the same entity!"));
            }

            // Check for duplicate relations (same origin+target entity/field pairing)
            if (isCreate)
            {
                var allRelations = await _entityRepository.GetAllRelations();
                var duplicateRelation = allRelations.FirstOrDefault(r =>
                    r.OriginEntityId == relation.OriginEntityId
                    && r.OriginFieldId == relation.OriginFieldId
                    && r.TargetEntityId == relation.TargetEntityId
                    && r.TargetFieldId == relation.TargetFieldId);

                if (duplicateRelation != null)
                {
                    errors.Add(new ErrorModel("name", relation.Name,
                        "A relation with the same origin and target entity/field combination already exists!"));
                }
            }

            // OneToOne: both origin AND target fields must be Required AND Unique
            if (relation.RelationType == EntityRelationType.OneToOne)
            {
                if (originField != null && (!originField.Required || !originField.Unique))
                {
                    errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                        "The origin field must be Required and Unique for a One-to-One relation!"));
                }
                if (targetField != null && (!targetField.Required || !targetField.Unique))
                {
                    errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(),
                        "The target field must be Required and Unique for a One-to-One relation!"));
                }
            }

            // OneToMany: origin field must be Required AND Unique
            if (relation.RelationType == EntityRelationType.OneToMany)
            {
                if (originField != null && (!originField.Required || !originField.Unique))
                {
                    errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                        "The origin field must be Required and Unique for a One-to-Many relation!"));
                }
            }

            // ManyToMany: both origin AND target fields must be Required AND Unique
            if (relation.RelationType == EntityRelationType.ManyToMany)
            {
                if (originField != null && (!originField.Required || !originField.Unique))
                {
                    errors.Add(new ErrorModel("originFieldId", relation.OriginFieldId.ToString(),
                        "The origin field must be Required and Unique for a Many-to-Many relation!"));
                }
                if (targetField != null && (!targetField.Required || !targetField.Unique))
                {
                    errors.Add(new ErrorModel("targetFieldId", relation.TargetFieldId.ToString(),
                        "The target field must be Required and Unique for a Many-to-Many relation!"));
                }
            }

            return errors;
        }

        // =====================================================================
        // PRIVATE HELPER METHODS
        // =====================================================================

        /// <summary>
        /// Gets an entity by ID directly from cache or repository (for internal use).
        /// </summary>
        private async Task<Entity?> GetEntityById(Guid entityId)
        {
            // Try cache first
            if (_cache.TryGetValue<List<Entity>>(ENTITIES_CACHE_KEY, out var cachedEntities)
                && cachedEntities != null)
            {
                return cachedEntities.FirstOrDefault(e => e.Id == entityId);
            }

            // Repository direct read
            return await _entityRepository.GetEntityById(entityId);
        }

        /// <summary>
        /// Loads all relations from the repository (bypassing cache).
        /// </summary>
        private async Task<List<EntityRelation>> LoadRelationsFromRepository()
        {
            var relations = await _entityRepository.GetAllRelations();
            return relations ?? new List<EntityRelation>();
        }

        /// <summary>
        /// Enriches a relation with entity and field names from the entities collection.
        /// Source: EntityRelationManager.Read(List&lt;DbEntity&gt;) enrichment logic
        /// </summary>
        private void EnrichRelation(EntityRelation relation, List<Entity> entities)
        {
            var originEntity = entities.FirstOrDefault(e => e.Id == relation.OriginEntityId);
            if (originEntity != null)
            {
                relation.OriginEntityName = originEntity.Name;
                var originField = originEntity.Fields?.FirstOrDefault(f => f.Id == relation.OriginFieldId);
                if (originField != null)
                {
                    relation.OriginFieldName = originField.Name;
                }
            }

            var targetEntity = entities.FirstOrDefault(e => e.Id == relation.TargetEntityId);
            if (targetEntity != null)
            {
                relation.TargetEntityName = targetEntity.Name;
                var targetField = targetEntity.Fields?.FirstOrDefault(f => f.Id == relation.TargetFieldId);
                if (targetField != null)
                {
                    relation.TargetFieldName = targetField.Name;
                }
            }
        }

        /// <summary>
        /// Creates default fields for a new entity.
        /// Always creates "id" field. If createOnlyIdField is false, also creates
        /// audit fields (created_by, created_on, last_modified_by, last_modified_on).
        /// Source: EntityManager.CreateEntityDefaultFields() lines ~1693-1830
        /// </summary>
        private List<Field> CreateEntityDefaultFields(
            Entity entity,
            bool createOnlyIdField,
            Dictionary<string, Guid>? sysIdDictionary)
        {
            var fields = new List<Field>();

            // Always create "id" field
            var idFieldId = sysIdDictionary != null && sysIdDictionary.ContainsKey("id")
                ? sysIdDictionary["id"]
                : Guid.NewGuid();

            var idField = new GuidField
            {
                Id = idFieldId,
                Name = "id",
                Label = "Id",
                Required = true,
                Unique = true,
                Searchable = true,
                System = true,
                GenerateNewId = true,
                EntityName = entity.Name
            };
            fields.Add(idField);

            // Optional audit fields when createOnlyIdField is false
            if (!createOnlyIdField)
            {
                // created_by — GuidField, System=true
                var createdByFieldId = sysIdDictionary != null && sysIdDictionary.ContainsKey("created_by")
                    ? sysIdDictionary["created_by"]
                    : Guid.NewGuid();

                var createdByField = new GuidField
                {
                    Id = createdByFieldId,
                    Name = "created_by",
                    Label = "Created By",
                    Required = false,
                    Unique = false,
                    Searchable = false,
                    System = true,
                    GenerateNewId = false,
                    EntityName = entity.Name
                };
                fields.Add(createdByField);

                // last_modified_by — GuidField, System=true
                var lastModifiedByFieldId = sysIdDictionary != null && sysIdDictionary.ContainsKey("last_modified_by")
                    ? sysIdDictionary["last_modified_by"]
                    : Guid.NewGuid();

                var lastModifiedByField = new GuidField
                {
                    Id = lastModifiedByFieldId,
                    Name = "last_modified_by",
                    Label = "Last Modified By",
                    Required = false,
                    Unique = false,
                    Searchable = false,
                    System = true,
                    GenerateNewId = false,
                    EntityName = entity.Name
                };
                fields.Add(lastModifiedByField);

                // created_on — DateTimeField, UseCurrentTimeAsDefaultValue=true
                var createdOnFieldId = sysIdDictionary != null && sysIdDictionary.ContainsKey("created_on")
                    ? sysIdDictionary["created_on"]
                    : Guid.NewGuid();

                var createdOnField = new DateTimeField
                {
                    Id = createdOnFieldId,
                    Name = "created_on",
                    Label = "Created On",
                    Required = false,
                    Unique = false,
                    Searchable = false,
                    System = true,
                    Format = "dd MMM yyyy HH:mm",
                    UseCurrentTimeAsDefaultValue = true,
                    EntityName = entity.Name
                };
                fields.Add(createdOnField);

                // last_modified_on — DateTimeField, UseCurrentTimeAsDefaultValue=true
                var lastModifiedOnFieldId = sysIdDictionary != null && sysIdDictionary.ContainsKey("last_modified_on")
                    ? sysIdDictionary["last_modified_on"]
                    : Guid.NewGuid();

                var lastModifiedOnField = new DateTimeField
                {
                    Id = lastModifiedOnFieldId,
                    Name = "last_modified_on",
                    Label = "Last Modified On",
                    Required = false,
                    Unique = false,
                    Searchable = false,
                    System = true,
                    Format = "dd MMM yyyy HH:mm",
                    UseCurrentTimeAsDefaultValue = true,
                    EntityName = entity.Name
                };
                fields.Add(lastModifiedOnField);
            }

            return fields;
        }

        /// <summary>
        /// Maps an InputField (create/update DTO) to a concrete Field type for persistence.
        /// Copies all base properties and type-specific properties.
        /// </summary>
        private Field MapInputFieldToField(InputField input)
        {
            Field result;

            if (input is InputAutoNumberField autoNum)
            {
                result = new AutoNumberField
                {
                    DefaultValue = autoNum.DefaultValue,
                    DisplayFormat = autoNum.DisplayFormat ?? string.Empty,
                    StartingNumber = autoNum.StartingNumber
                };
            }
            else if (input is InputCheckboxField checkbox)
            {
                result = new CheckboxField
                {
                    DefaultValue = checkbox.DefaultValue ?? false
                };
            }
            else if (input is InputCurrencyField currency)
            {
                result = new CurrencyField
                {
                    DefaultValue = currency.DefaultValue,
                    MinValue = currency.MinValue,
                    MaxValue = currency.MaxValue,
                    Currency = currency.Currency ?? new CurrencyType()
                };
            }
            else if (input is InputDateField dateInput)
            {
                result = new DateField
                {
                    DefaultValue = dateInput.DefaultValue,
                    Format = dateInput.Format ?? "yyyy-MMM-dd",
                    UseCurrentTimeAsDefaultValue = dateInput.UseCurrentTimeAsDefaultValue
                };
            }
            else if (input is InputDateTimeField dateTimeInput)
            {
                result = new DateTimeField
                {
                    DefaultValue = dateTimeInput.DefaultValue,
                    Format = dateTimeInput.Format ?? "yyyy-MMM-dd HH:mm",
                    UseCurrentTimeAsDefaultValue = dateTimeInput.UseCurrentTimeAsDefaultValue
                };
            }
            else if (input is InputEmailField email)
            {
                result = new EmailField
                {
                    DefaultValue = email.DefaultValue,
                    MaxLength = email.MaxLength
                };
            }
            else if (input is InputFileField fileField)
            {
                result = new FileField
                {
                    DefaultValue = fileField.DefaultValue ?? string.Empty
                };
            }
            else if (input is InputGuidField guidInput)
            {
                result = new GuidField
                {
                    DefaultValue = guidInput.DefaultValue,
                    GenerateNewId = guidInput.GenerateNewId
                };
            }
            else if (input is InputHtmlField html)
            {
                result = new HtmlField
                {
                    DefaultValue = html.DefaultValue
                };
            }
            else if (input is InputImageField image)
            {
                result = new ImageField
                {
                    DefaultValue = image.DefaultValue
                };
            }
            else if (input is InputMultiLineTextField multiLine)
            {
                result = new MultiLineTextField
                {
                    DefaultValue = multiLine.DefaultValue ?? string.Empty,
                    MaxLength = multiLine.MaxLength,
                    VisibleLineNumber = multiLine.VisibleLineNumber
                };
            }
            else if (input is InputMultiSelectField multiSelect)
            {
                result = new MultiSelectField
                {
                    DefaultValue = multiSelect.DefaultValue ?? Enumerable.Empty<string>(),
                    Options = multiSelect.Options ?? new List<SelectOption>()
                };
            }
            else if (input is InputNumberField number)
            {
                result = new NumberField
                {
                    DefaultValue = number.DefaultValue,
                    MinValue = number.MinValue,
                    MaxValue = number.MaxValue,
                    DecimalPlaces = number.DecimalPlaces
                };
            }
            else if (input is InputPasswordField password)
            {
                result = new PasswordField
                {
                    MaxLength = password.MaxLength,
                    MinLength = password.MinLength,
                    Encrypted = password.Encrypted
                };
            }
            else if (input is InputPercentField percent)
            {
                result = new PercentField
                {
                    DefaultValue = percent.DefaultValue,
                    MinValue = percent.MinValue,
                    MaxValue = percent.MaxValue,
                    DecimalPlaces = percent.DecimalPlaces
                };
            }
            else if (input is InputPhoneField phone)
            {
                result = new PhoneField
                {
                    DefaultValue = phone.DefaultValue,
                    Format = phone.Format,
                    MaxLength = phone.MaxLength
                };
            }
            else if (input is InputSelectField select)
            {
                result = new SelectField
                {
                    DefaultValue = select.DefaultValue ?? string.Empty,
                    Options = select.Options ?? new List<SelectOption>()
                };
            }
            else if (input is InputTextField text)
            {
                result = new TextField
                {
                    DefaultValue = text.DefaultValue,
                    MaxLength = text.MaxLength
                };
            }
            else if (input is InputUrlField url)
            {
                result = new UrlField
                {
                    DefaultValue = url.DefaultValue,
                    MaxLength = url.MaxLength,
                    OpenTargetInNewWindow = url.OpenTargetInNewWindow
                };
            }
            else if (input is InputGeographyField geography)
            {
                result = new GeographyField
                {
                    DefaultValue = geography.DefaultValue ?? string.Empty,
                    MaxLength = geography.MaxLength,
                    VisibleLineNumber = geography.VisibleLineNumber,
                    Format = geography.Format ?? GeographyFieldFormat.GeoJSON,
                    SRID = geography.SRID
                };
            }
            else
            {
                // Default fallback to GuidField
                result = new GuidField
                {
                    GenerateNewId = false
                };
            }

            // Copy base properties
            result.Id = input.Id ?? Guid.NewGuid();
            result.Name = input.Name ?? string.Empty;
            result.Label = input.Label ?? input.Name ?? string.Empty;
            result.PlaceholderText = input.PlaceholderText ?? string.Empty;
            result.Description = input.Description ?? string.Empty;
            result.HelpText = input.HelpText ?? string.Empty;
            result.Required = input.Required ?? false;
            result.Unique = input.Unique ?? false;
            result.Searchable = input.Searchable ?? false;
            result.Auditable = input.Auditable ?? false;
            result.System = input.System ?? false;
            result.EnableSecurity = input.EnableSecurity;
            if (input.Permissions != null)
            {
                result.Permissions = input.Permissions;
            }

            return result;
        }

        // =====================================================================
        // VALIDATION UTILITY METHODS (internalized from ValidationUtility.cs)
        // =====================================================================

        /// <summary>
        /// Validates a name against the naming convention pattern.
        /// Source: ValidationUtility.ValidateName()
        /// Pattern: ^[a-z](?!.*__)[a-z0-9_]*[a-z0-9]$
        /// Min length: 2, Max length: 63 (PostgreSQL identifier limit preserved)
        /// </summary>
        private List<ErrorModel> ValidateName(string? name, string fieldKey)
        {
            var errors = new List<ErrorModel>();

            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new ErrorModel(fieldKey, null!, "Name is required!"));
                return errors;
            }

            if (name.Length < 2)
            {
                errors.Add(new ErrorModel(fieldKey, name,
                    "Name must be at least 2 characters long!"));
                return errors;
            }

            if (name.Length > 63)
            {
                errors.Add(new ErrorModel(fieldKey, name,
                    "Name length exceeded. Should be up to 63 chars!"));
                return errors;
            }

            if (!NameValidationPattern.IsMatch(name))
            {
                errors.Add(new ErrorModel(fieldKey, name,
                    "Name can only contain lowercase letters, digits, and underscores. It must start with a letter and end with a letter or digit. Consecutive underscores are not allowed."));
            }

            return errors;
        }

        /// <summary>
        /// Validates a label string.
        /// Source: ValidationUtility.ValidateLabel()
        /// Min length: 1, Max length: 200
        /// </summary>
        private List<ErrorModel> ValidateLabel(string? label, string fieldKey)
        {
            var errors = new List<ErrorModel>();

            if (!string.IsNullOrEmpty(label))
                label = label.Trim();

            if (string.IsNullOrWhiteSpace(label))
            {
                errors.Add(new ErrorModel(fieldKey, label, "Label is required!"));
                return errors;
            }

            if (label.Length > 200)
            {
                errors.Add(new ErrorModel(fieldKey, label,
                    "The length of Label must be less or equal than 200 characters!"));
            }

            return errors;
        }

        /// <summary>
        /// Validates a label plural string.
        /// Source: ValidationUtility.ValidateLabelPlural()
        /// Min length: 1, Max length: 200
        /// </summary>
        private List<ErrorModel> ValidateLabelPlural(string? labelPlural, string fieldKey)
        {
            var errors = new List<ErrorModel>();

            if (!string.IsNullOrEmpty(labelPlural))
                labelPlural = labelPlural.Trim();

            if (string.IsNullOrWhiteSpace(labelPlural))
            {
                errors.Add(new ErrorModel(fieldKey, labelPlural, "Plural label is required!"));
                return errors;
            }

            if (labelPlural.Length > 200)
            {
                errors.Add(new ErrorModel(fieldKey, labelPlural,
                    "The length of Plural label must be less or equal than 200 characters!"));
            }

            return errors;
        }

        // =====================================================================
        // CRYPTO UTILITY (internalized from CryptoUtility.cs)
        // =====================================================================

        /// <summary>
        /// Computes the MD5 hash of a string using Unicode (UTF-16LE) encoding,
        /// returning a lowercase hexadecimal string. This matches the monolith's
        /// CryptoUtility.ComputeOddMD5Hash() implementation exactly for backward
        /// compatibility with existing hash values.
        /// Source: CryptoUtility.ComputeOddMD5Hash()
        /// IMPORTANT: Uses Encoding.Unicode (UTF-16LE), NOT UTF-8
        /// </summary>
        private static string ComputeOddMD5Hash(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            using (var md5 = MD5.Create())
            {
                // Source uses Encoding.Unicode (UTF-16LE), not UTF-8
                byte[] inputBytes = Encoding.Unicode.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                var sb = new StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
