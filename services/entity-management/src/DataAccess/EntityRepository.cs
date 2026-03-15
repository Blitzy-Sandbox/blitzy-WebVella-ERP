// =============================================================================
// EntityRepository.cs — DynamoDB Single-Table Design for Entity/Field/Relation Metadata
// =============================================================================
// Migrated from:
//   WebVella.Erp/Database/DbEntityRepository.cs — Entity metadata CRUD
//   WebVella.Erp/Database/DbRelationRepository.cs — Relation metadata CRUD
//   WebVella.Erp/Database/DbRepository.cs — CreateNtoNRelation / DeleteNtoNRelation
//
// Replaces three PostgreSQL repositories with a unified DynamoDB single-table
// design repository for the Entity Management microservice.
//
// DynamoDB Single-Table Design:
//   Entity items:   PK=ENTITY#{entityId}        SK=META
//                   GSI1PK=ENTITY_NAME#{name}    GSI1SK=META
//   Field items:    PK=ENTITY#{entityId}         SK=FIELD#{fieldId}
//   Relation items: PK=ENTITY#{originEntityId}   SK=RELATION#{relationId}
//                   GSI2PK=RELATION#{relationId}  GSI2SK=META
//   M2M items:      PK=RELATION#{relationId}     SK=M2M#{originId}#{targetId}
//
// Namespace Migration:
//   Old: WebVella.Erp.Database → New: WebVellaErp.EntityManagement.DataAccess
//
// Serialization: Uses Newtonsoft.Json with TypeNameHandling.Auto for polymorphic
//   field type round-tripping (20+ concrete Field subclasses).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVellaErp.EntityManagement.Models;

namespace WebVellaErp.EntityManagement.DataAccess
{
    /// <summary>
    /// Custom storage exception for data access layer errors.
    /// Replaces PostgreSQL-specific exceptions from the monolith's DbEntityRepository
    /// and DbRelationRepository. Preserves exact error messages from source for
    /// backward compatibility with existing error handling consumers.
    /// </summary>
    public class StorageException : Exception
    {
        /// <summary>Wraps a string message as a storage exception.</summary>
        public StorageException(string message) : base(message) { }

        /// <summary>Wraps an inner exception, using its message.</summary>
        public StorageException(Exception inner) : base(inner.Message, inner) { }

        /// <summary>Wraps a custom message with an inner exception.</summary>
        public StorageException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// JSON converter that handles decimal-to-int conversion during deserialization.
    /// Preserves backward compatibility with entity metadata that may contain decimal
    /// values where integer types are expected due to loose PostgreSQL JSON storage.
    /// Source: DbEntityRepository.cs lines 232-257.
    /// </summary>
    public class DecimalToIntFormatConverter : JsonConverter
    {
        /// <summary>
        /// Determines whether this converter can handle the specified type.
        /// Returns true only for System.Int32 (int) targets.
        /// </summary>
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(int);
        }

        /// <summary>
        /// Reads a JSON value and converts it to int, handling both float and integer tokens.
        /// Float tokens (e.g., 42.0) are converted via Convert.ToInt32 for backward compat.
        /// Source: DbEntityRepository.cs lines 237-251.
        /// </summary>
        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Float)
            {
                return Convert.ToInt32(reader.Value!.ToString());
            }
            if (reader.TokenType == JsonToken.Integer)
            {
                return Convert.ToInt32(reader.Value);
            }
            if (reader.TokenType == JsonToken.Null)
            {
                return existingValue;
            }
            return existingValue;
        }

        /// <summary>
        /// Write is not supported — this is a read-only converter.
        /// Source: DbEntityRepository.cs line 252: throw new NotImplementedException().
        /// </summary>
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Repository interface for entity, field, and relation metadata persistence.
    /// Provides a DI-friendly contract for all metadata CRUD operations against DynamoDB.
    /// All methods are async to support non-blocking DynamoDB SDK calls.
    /// </summary>
    public interface IEntityRepository
    {
        // ─── Entity CRUD ──────────────────────────────────────────────────

        /// <summary>
        /// Creates a new entity definition in DynamoDB, including field items and optional
        /// auto-generated user relations (created_by, modified_by).
        /// Source: DbEntityRepository.Create() lines 38-155.
        /// </summary>
        Task<bool> CreateEntity(Entity entity, Dictionary<string, Guid>? sysIdDictionary = null, bool createOnlyIdField = true);

        /// <summary>
        /// Updates an existing entity definition (replaces entityData JSON).
        /// Source: DbEntityRepository.Update() lines 157-187.
        /// </summary>
        Task<bool> UpdateEntity(Entity entity);

        /// <summary>
        /// Retrieves an entity by its unique GUID identifier using strong consistent read.
        /// Source: DbEntityRepository.Read(Guid) lines 189-193.
        /// </summary>
        Task<Entity?> GetEntityById(Guid entityId);

        /// <summary>
        /// Retrieves an entity by name (case-insensitive) using GSI1 name-based lookup.
        /// Source: DbEntityRepository.Read(string) lines 195-199.
        /// </summary>
        Task<Entity?> GetEntityByName(string entityName);

        /// <summary>
        /// Retrieves all entity definitions from the metadata table.
        /// Source: DbEntityRepository.Read() lines 201-230.
        /// </summary>
        Task<List<Entity>> GetAllEntities();

        /// <summary>
        /// Deletes an entity and all its associated field items, relation items, and M2M items.
        /// </summary>
        Task<bool> DeleteEntity(Guid entityId);

        // ─── Field CRUD ───────────────────────────────────────────────────

        /// <summary>Creates a field definition item and updates parent entity's Fields list.</summary>
        Task CreateField(Guid entityId, Field field);

        /// <summary>Retrieves a single field definition by entity and field ID.</summary>
        Task<Field?> GetField(Guid entityId, Guid fieldId);

        /// <summary>Retrieves all field definitions for the specified entity.</summary>
        Task<List<Field>> GetFields(Guid entityId);

        /// <summary>Updates a field definition item and parent entity's Fields list.</summary>
        Task UpdateField(Guid entityId, Field field);

        /// <summary>Deletes a field definition item and removes it from parent entity's Fields list.</summary>
        Task DeleteField(Guid entityId, Guid fieldId);

        // ─── Relation CRUD ────────────────────────────────────────────────

        /// <summary>
        /// Creates a relation definition. For ManyToMany relations, establishes
        /// the M2M association tracking structure.
        /// Source: DbRelationRepository.Create() lines 38-113.
        /// </summary>
        Task<bool> CreateRelation(EntityRelation relation);

        /// <summary>
        /// Updates an existing relation definition's metadata.
        /// Source: DbRelationRepository.Update() lines 116-151.
        /// </summary>
        Task<bool> UpdateRelation(EntityRelation relation);

        /// <summary>
        /// Retrieves a relation by its unique ID using GSI2 lookup.
        /// Source: DbRelationRepository.Read(Guid) lines 153-157.
        /// </summary>
        Task<EntityRelation?> GetRelationById(Guid relationId);

        /// <summary>
        /// Retrieves a relation by name (case-insensitive).
        /// Source: DbRelationRepository.Read(string) lines 159-163.
        /// </summary>
        Task<EntityRelation?> GetRelationByName(string relationName);

        /// <summary>
        /// Retrieves all relation definitions from the metadata table.
        /// Source: DbRelationRepository.Read() lines 165-185.
        /// </summary>
        Task<List<EntityRelation>> GetAllRelations();

        /// <summary>
        /// Deletes a relation definition and all associated M2M records for ManyToMany relations.
        /// Source: DbRelationRepository.Delete() lines 187-256.
        /// </summary>
        Task<bool> DeleteRelation(Guid relationId);

        // ─── Many-to-Many Record Management ───────────────────────────────

        /// <summary>
        /// Creates a single M2M association record linking origin and target records.
        /// Source: DbRelationRepository.CreateManyToManyRecord() lines 258-270.
        /// </summary>
        Task CreateManyToManyRecord(Guid relationId, Guid originId, Guid targetId);

        /// <summary>
        /// Deletes M2M association records by relation name, optionally filtering by origin/target.
        /// Source: DbRelationRepository.DeleteManyToManyRecord() lines 272-300.
        /// </summary>
        Task DeleteManyToManyRecord(string relationName, Guid? originId = null, Guid? targetId = null);

        /// <summary>
        /// Retrieves M2M association records for a given relation, optionally filtering.
        /// Returns list of (originId, targetId) pairs.
        /// </summary>
        Task<List<KeyValuePair<Guid, Guid>>> GetManyToManyRecords(Guid relationId, Guid? originId = null, Guid? targetId = null);

        // ─── Cache Management ─────────────────────────────────────────────

        /// <summary>
        /// Signals that cached metadata should be invalidated.
        /// Called automatically after all mutation operations.
        /// Source pattern: Cache.Clear() in finally blocks throughout
        /// DbEntityRepository/DbRelationRepository.
        /// </summary>
        void ClearCache();
    }

    /// <summary>
    /// DynamoDB-backed repository implementing IEntityRepository.
    /// Uses single-table design with composite primary keys and two GSIs:
    ///   GSI1 — Entity name-based lookup (sparse index, entity items only)
    ///   GSI2 — Relation ID-based lookup (sparse index, relation items only)
    ///
    /// All metadata is serialized with Newtonsoft.Json TypeNameHandling.Auto
    /// for polymorphic field type round-tripping, exactly matching the monolith's
    /// DbEntityRepository.cs and DbRelationRepository.cs serialization behavior.
    /// </summary>
    public class EntityRepository : IEntityRepository
    {
        // ─── Constants ────────────────────────────────────────────────────

        /// <summary>
        /// Prefix for record collection names. Carried from DbEntityRepository.cs line 17.
        /// Used by RecordRepository to derive DynamoDB key prefixes for entity records.
        /// </summary>
        public const string RECORD_COLLECTION_PREFIX = "rec_";

        // DynamoDB key prefixes
        private const string ENTITY_PK_PREFIX = "ENTITY#";
        private const string RELATION_PK_PREFIX = "RELATION#";
        private const string META_SK = "META";
        private const string FIELD_SK_PREFIX = "FIELD#";
        private const string RELATION_SK_PREFIX = "RELATION#";
        private const string M2M_SK_PREFIX = "M2M#";

        // GSI names
        private const string GSI1_INDEX_NAME = "GSI1";
        private const string GSI2_INDEX_NAME = "GSI2";

        // DynamoDB attribute names
        private const string PK_ATTR = "PK";
        private const string SK_ATTR = "SK";
        private const string GSI1PK_ATTR = "GSI1PK";
        private const string GSI1SK_ATTR = "GSI1SK";
        private const string GSI2PK_ATTR = "GSI2PK";
        private const string GSI2SK_ATTR = "GSI2SK";
        private const string ENTITY_DATA_ATTR = "entityData";
        private const string FIELD_DATA_ATTR = "fieldData";
        private const string RELATION_DATA_ATTR = "relationData";
        private const string ORIGIN_ID_ATTR = "originId";
        private const string TARGET_ID_ATTR = "targetId";

        // Maximum items per BatchWriteItem request (DynamoDB limit is 25)
        private const int MAX_BATCH_WRITE_SIZE = 25;

        // ─── Dependencies ─────────────────────────────────────────────────

        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly ILogger<EntityRepository> _logger;
        private readonly string _tableName;

        // ─── JSON Serialization Settings ──────────────────────────────────
        // CRITICAL: Uses Newtonsoft.Json with TypeNameHandling.Auto for polymorphic
        // field type serialization. This preserves $type discriminators for 20+ concrete
        // Field subclasses, matching exact monolith behavior from DbEntityRepository.cs
        // line 50 and DbRelationRepository.cs line 47.

        private static readonly JsonSerializerSettings SerializeSettings = new()
        {
            TypeNameHandling = TypeNameHandling.Auto,
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// Tolerant deserialization settings matching source DbEntityRepository.cs lines 210-216.
        /// MissingMemberHandling.Ignore allows schema evolution without breaking deserialization.
        /// DecimalToIntFormatConverter handles decimal-to-int conversion from legacy data.
        /// </summary>
        private static readonly JsonSerializerSettings DeserializeSettings = new()
        {
            TypeNameHandling = TypeNameHandling.Auto,
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            Converters = { new DecimalToIntFormatConverter() }
        };

        // ─── Cache Invalidation Event ─────────────────────────────────────

        /// <summary>
        /// Event raised after any mutation operation to signal cache invalidation.
        /// EntityService subscribes to this event to clear its in-memory cache.
        /// </summary>
        public event Action? OnCacheCleared;

        // ─── Constructor ──────────────────────────────────────────────────

        /// <summary>
        /// Initializes EntityRepository with DynamoDB client, logger, and configuration.
        /// Reads table name from IConfiguration["DynamoDB:MetadataTableName"], defaulting
        /// to "entity-management-metadata". The DynamoDB client should be pre-configured
        /// with AWS_ENDPOINT_URL for LocalStack dual-target support (AAP §0.8.6).
        /// </summary>
        public EntityRepository(
            IAmazonDynamoDB dynamoDbClient,
            ILogger<EntityRepository> logger,
            IConfiguration configuration)
        {
            _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tableName = configuration?["DynamoDB:MetadataTableName"] ?? "entity-management-metadata";
        }

        // ═══════════════════════════════════════════════════════════════════
        // ENTITY CRUD OPERATIONS
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<bool> CreateEntity(
            Entity entity,
            Dictionary<string, Guid>? sysIdDictionary = null,
            bool createOnlyIdField = true)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            try
            {
                _logger.LogInformation("Creating entity {EntityId} ({EntityName})", entity.Id, entity.Name);

                // Serialize entity with TypeNameHandling.Auto (source line 50)
                string entityJson = JsonConvert.SerializeObject(entity, SerializeSettings);

                // Put entity metadata item with conditional create (optimistic concurrency)
                var putRequest = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entity.Id}" },
                        [SK_ATTR] = new AttributeValue { S = META_SK },
                        [GSI1PK_ATTR] = new AttributeValue { S = $"ENTITY_NAME#{entity.Name.ToLowerInvariant()}" },
                        [GSI1SK_ATTR] = new AttributeValue { S = META_SK },
                        [ENTITY_DATA_ATTR] = new AttributeValue { S = entityJson }
                    },
                    ConditionExpression = "attribute_not_exists(PK)"
                };

                try
                {
                    await _dynamoDbClient.PutItemAsync(putRequest);
                }
                catch (ConditionalCheckFailedException)
                {
                    _logger.LogWarning("Entity {EntityId} already exists, creation skipped", entity.Id);
                    return false;
                }

                // Create individual field items for each field in the entity
                // Uses BatchWriteItem with PutRequest for efficient bulk creation
                if (entity.Fields != null && entity.Fields.Count > 0)
                {
                    var fieldWriteRequests = new List<WriteRequest>();
                    foreach (var field in entity.Fields)
                    {
                        string fieldJson = JsonConvert.SerializeObject(field, SerializeSettings);
                        var fieldItem = new Dictionary<string, AttributeValue>
                        {
                            [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entity.Id}" },
                            [SK_ATTR] = new AttributeValue { S = $"{FIELD_SK_PREFIX}{field.Id}" },
                            [FIELD_DATA_ATTR] = new AttributeValue { S = fieldJson }
                        };
                        fieldWriteRequests.Add(new WriteRequest
                        {
                            PutRequest = new PutRequest
                            {
                                Item = fieldItem
                            }
                        });
                    }
                    await ExecuteBatchWrite(fieldWriteRequests);
                }

                // Auto-create user relations (source: DbEntityRepository.Create lines 100-139)
                // When entity is NOT the User entity AND we're creating more than just the id field,
                // automatically create user_{entity.Name}_created_by and _modified_by relations.
                if (entity.Id != SystemIds.UserEntityId && !createOnlyIdField)
                {
                    await CreateUserRelationsForEntity(entity, sysIdDictionary);
                }

                _logger.LogInformation("Entity {EntityId} ({EntityName}) created successfully", entity.Id, entity.Name);
                return true;
            }
            catch (Exception ex) when (ex is not StorageException
                                       and not ArgumentNullException
                                       and not ConditionalCheckFailedException)
            {
                _logger.LogError(ex, "Failed to create entity {EntityId}", entity.Id);
                throw new StorageException($"Failed to create entity {entity.Id}", ex);
            }
            finally
            {
                ClearCache();
            }
        }

        /// <inheritdoc />
        public async Task<bool> UpdateEntity(Entity entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            try
            {
                _logger.LogInformation("Updating entity {EntityId} ({EntityName})", entity.Id, entity.Name);

                // Serialize entity with TypeNameHandling.Auto (source line 165)
                string entityJson = JsonConvert.SerializeObject(entity, SerializeSettings);

                // Update entity metadata item with existence check
                var updateRequest = new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entity.Id}" },
                        [SK_ATTR] = new AttributeValue { S = META_SK }
                    },
                    UpdateExpression = "SET #data = :data, #gsi1pk = :gsi1pk",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#data"] = ENTITY_DATA_ATTR,
                        ["#gsi1pk"] = GSI1PK_ATTR
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":data"] = new AttributeValue { S = entityJson },
                        [":gsi1pk"] = new AttributeValue { S = $"ENTITY_NAME#{entity.Name.ToLowerInvariant()}" }
                    },
                    ConditionExpression = "attribute_exists(PK)"
                };

                try
                {
                    await _dynamoDbClient.UpdateItemAsync(updateRequest);
                    _logger.LogInformation("Entity {EntityId} updated successfully", entity.Id);
                    return true;
                }
                catch (ConditionalCheckFailedException)
                {
                    _logger.LogWarning("Entity {EntityId} not found for update", entity.Id);
                    return false;
                }
            }
            catch (Exception ex) when (ex is not StorageException
                                       and not ArgumentNullException
                                       and not ConditionalCheckFailedException)
            {
                _logger.LogError(ex, "Failed to update entity {EntityId}", entity.Id);
                throw new StorageException($"Failed to update entity {entity.Id}", ex);
            }
            finally
            {
                ClearCache();
            }
        }

        /// <inheritdoc />
        public async Task<Entity?> GetEntityById(Guid entityId)
        {
            _logger.LogDebug("Reading entity by ID: {EntityId}", entityId);

            var getRequest = new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entityId}" },
                    [SK_ATTR] = new AttributeValue { S = META_SK }
                },
                ConsistentRead = true
            };

            var response = await _dynamoDbClient.GetItemAsync(getRequest);

            if (response.Item == null || response.Item.Count == 0)
            {
                _logger.LogDebug("Entity {EntityId} not found", entityId);
                return null;
            }

            return DeserializeEntity(response.Item);
        }

        /// <inheritdoc />
        public async Task<Entity?> GetEntityByName(string entityName)
        {
            if (string.IsNullOrEmpty(entityName)) return null;

            _logger.LogDebug("Reading entity by name: {EntityName}", entityName);

            // Query GSI1 using lowercase name for case-insensitive lookup
            // Source: DbEntityRepository.Read(string) line 198: e.Name.ToLowerInvariant()
            var queryRequest = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                KeyConditionExpression = "#gsi1pk = :pk AND #gsi1sk = :sk",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#gsi1pk"] = GSI1PK_ATTR,
                    ["#gsi1sk"] = GSI1SK_ATTR
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = $"ENTITY_NAME#{entityName.ToLowerInvariant()}" },
                    [":sk"] = new AttributeValue { S = META_SK }
                },
                Limit = 1
            };

            var response = await _dynamoDbClient.QueryAsync(queryRequest);

            if (response.Items == null || response.Items.Count == 0)
            {
                _logger.LogDebug("Entity with name '{EntityName}' not found", entityName);
                return null;
            }

            return DeserializeEntity(response.Items[0]);
        }

        /// <inheritdoc />
        public async Task<List<Entity>> GetAllEntities()
        {
            _logger.LogDebug("Reading all entities");

            var entities = new List<Entity>();
            Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

            // Scan GSI1 to retrieve all entity metadata items.
            // GSI1 is a sparse index containing only entity items (those with GSI1PK/GSI1SK).
            do
            {
                var scanRequest = new ScanRequest
                {
                    TableName = _tableName,
                    IndexName = GSI1_INDEX_NAME,
                    FilterExpression = "#gsi1sk = :meta",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#gsi1sk"] = GSI1SK_ATTR
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":meta"] = new AttributeValue { S = META_SK }
                    },
                    ExclusiveStartKey = lastEvaluatedKey?.Count > 0 ? lastEvaluatedKey : null
                };

                var response = await _dynamoDbClient.ScanAsync(scanRequest);

                foreach (var item in response.Items)
                {
                    var entity = DeserializeEntity(item);
                    if (entity != null)
                    {
                        entities.Add(entity);
                    }
                }

                lastEvaluatedKey = response.LastEvaluatedKey;
            } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);

            _logger.LogDebug("Retrieved {Count} entities", entities.Count);
            return entities;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteEntity(Guid entityId)
        {
            try
            {
                _logger.LogInformation("Deleting entity {EntityId}", entityId);

                // Step 1: Read entity to verify existence
                var entity = await GetEntityById(entityId);
                if (entity == null)
                {
                    _logger.LogWarning("Entity {EntityId} not found for deletion", entityId);
                    return false;
                }

                // Step 2: Delete all relations where this entity is origin or target
                // Matching source pattern: deletes all entity relations before dropping entity
                var allRelations = await GetAllRelations();
                var entityRelations = allRelations.Where(r =>
                    r.OriginEntityId == entityId || r.TargetEntityId == entityId).ToList();

                foreach (var relation in entityRelations)
                {
                    await DeleteRelation(relation.Id);
                }

                // Step 3: Collect all items under this entity's PK for batch deletion
                // (field items, remaining relation items under this PK, and the entity META item)
                var deleteRequests = new List<WriteRequest>();

                Dictionary<string, AttributeValue>? lastKey = null;
                do
                {
                    var queryRequest = new QueryRequest
                    {
                        TableName = _tableName,
                        KeyConditionExpression = "#pk = :pk",
                        ExpressionAttributeNames = new Dictionary<string, string>
                        {
                            ["#pk"] = PK_ATTR
                        },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":pk"] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entityId}" }
                        },
                        ExclusiveStartKey = lastKey?.Count > 0 ? lastKey : null
                    };

                    var queryResponse = await _dynamoDbClient.QueryAsync(queryRequest);

                    foreach (var item in queryResponse.Items)
                    {
                        deleteRequests.Add(new WriteRequest
                        {
                            DeleteRequest = new DeleteRequest
                            {
                                Key = new Dictionary<string, AttributeValue>
                                {
                                    [PK_ATTR] = item[PK_ATTR],
                                    [SK_ATTR] = item[SK_ATTR]
                                }
                            }
                        });
                    }

                    lastKey = queryResponse.LastEvaluatedKey;
                } while (lastKey != null && lastKey.Count > 0);

                // Step 4: Execute batch deletes
                if (deleteRequests.Count > 0)
                {
                    await ExecuteBatchWrite(deleteRequests);
                }

                _logger.LogInformation("Entity {EntityId} ({EntityName}) deleted successfully",
                    entityId, entity.Name);
                return true;
            }
            catch (Exception ex) when (ex is not StorageException and not ArgumentNullException)
            {
                _logger.LogError(ex, "Failed to delete entity {EntityId}", entityId);
                throw new StorageException($"Failed to delete entity {entityId}", ex);
            }
            finally
            {
                ClearCache();
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // FIELD CRUD OPERATIONS
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task CreateField(Guid entityId, Field field)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));

            try
            {
                _logger.LogInformation("Creating field {FieldId} ({FieldName}) on entity {EntityId}",
                    field.Id, field.Name, entityId);

                // Create the individual field item
                await CreateFieldItemInternal(entityId, field);

                // Update the parent entity's entityData to include the new field
                await AddFieldToEntityItem(entityId, field);

                _logger.LogInformation("Field {FieldId} created on entity {EntityId}", field.Id, entityId);
            }
            catch (Exception ex) when (ex is not StorageException and not ArgumentNullException)
            {
                _logger.LogError(ex, "Failed to create field {FieldId} on entity {EntityId}",
                    field.Id, entityId);
                throw new StorageException($"Failed to create field {field.Id}", ex);
            }
            finally
            {
                ClearCache();
            }
        }

        /// <inheritdoc />
        public async Task<Field?> GetField(Guid entityId, Guid fieldId)
        {
            _logger.LogDebug("Reading field {FieldId} from entity {EntityId}", fieldId, entityId);

            var getRequest = new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entityId}" },
                    [SK_ATTR] = new AttributeValue { S = $"{FIELD_SK_PREFIX}{fieldId}" }
                },
                ConsistentRead = true
            };

            var response = await _dynamoDbClient.GetItemAsync(getRequest);

            if (response.Item == null || response.Item.Count == 0)
            {
                _logger.LogDebug("Field {FieldId} not found on entity {EntityId}", fieldId, entityId);
                return null;
            }

            if (!response.Item.ContainsKey(FIELD_DATA_ATTR))
                return null;

            string fieldJson = response.Item[FIELD_DATA_ATTR].S;
            return JsonConvert.DeserializeObject<Field>(fieldJson, DeserializeSettings);
        }

        /// <inheritdoc />
        public async Task<List<Field>> GetFields(Guid entityId)
        {
            _logger.LogDebug("Reading all fields for entity {EntityId}", entityId);

            var fields = new List<Field>();
            Dictionary<string, AttributeValue>? lastKey = null;

            do
            {
                var queryRequest = new QueryRequest
                {
                    TableName = _tableName,
                    KeyConditionExpression = "#pk = :pk AND begins_with(#sk, :fieldPrefix)",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#pk"] = PK_ATTR,
                        ["#sk"] = SK_ATTR
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entityId}" },
                        [":fieldPrefix"] = new AttributeValue { S = FIELD_SK_PREFIX }
                    },
                    ConsistentRead = true,
                    ExclusiveStartKey = lastKey?.Count > 0 ? lastKey : null
                };

                var response = await _dynamoDbClient.QueryAsync(queryRequest);

                foreach (var item in response.Items)
                {
                    if (item.ContainsKey(FIELD_DATA_ATTR))
                    {
                        var field = JsonConvert.DeserializeObject<Field>(
                            item[FIELD_DATA_ATTR].S, DeserializeSettings);
                        if (field != null)
                        {
                            fields.Add(field);
                        }
                    }
                }

                lastKey = response.LastEvaluatedKey;
            } while (lastKey != null && lastKey.Count > 0);

            _logger.LogDebug("Retrieved {Count} fields for entity {EntityId}", fields.Count, entityId);
            return fields;
        }

        /// <inheritdoc />
        public async Task UpdateField(Guid entityId, Field field)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));

            try
            {
                _logger.LogInformation("Updating field {FieldId} on entity {EntityId}", field.Id, entityId);

                // Update individual field item
                string fieldJson = JsonConvert.SerializeObject(field, SerializeSettings);

                var updateRequest = new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entityId}" },
                        [SK_ATTR] = new AttributeValue { S = $"{FIELD_SK_PREFIX}{field.Id}" }
                    },
                    UpdateExpression = "SET #data = :data",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#data"] = FIELD_DATA_ATTR
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":data"] = new AttributeValue { S = fieldJson }
                    },
                    ConditionExpression = "attribute_exists(SK)"
                };

                try
                {
                    await _dynamoDbClient.UpdateItemAsync(updateRequest);
                }
                catch (ConditionalCheckFailedException)
                {
                    _logger.LogWarning("Field {FieldId} not found on entity {EntityId} for update",
                        field.Id, entityId);
                    throw new StorageException($"Field {field.Id} not found on entity {entityId}");
                }

                // Update the field within the parent entity's entityData
                await UpdateFieldInEntityItem(entityId, field);

                _logger.LogInformation("Field {FieldId} updated on entity {EntityId}", field.Id, entityId);
            }
            catch (Exception ex) when (ex is not StorageException and not ArgumentNullException)
            {
                _logger.LogError(ex, "Failed to update field {FieldId} on entity {EntityId}",
                    field.Id, entityId);
                throw new StorageException($"Failed to update field {field.Id}", ex);
            }
            finally
            {
                ClearCache();
            }
        }

        /// <inheritdoc />
        public async Task DeleteField(Guid entityId, Guid fieldId)
        {
            try
            {
                _logger.LogInformation("Deleting field {FieldId} from entity {EntityId}", fieldId, entityId);

                // Delete field item
                var deleteRequest = new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entityId}" },
                        [SK_ATTR] = new AttributeValue { S = $"{FIELD_SK_PREFIX}{fieldId}" }
                    },
                    ConditionExpression = "attribute_exists(PK)"
                };

                try
                {
                    await _dynamoDbClient.DeleteItemAsync(deleteRequest);
                }
                catch (ConditionalCheckFailedException)
                {
                    _logger.LogWarning("Field {FieldId} not found on entity {EntityId} for deletion",
                        fieldId, entityId);
                }

                // Remove field from parent entity's Fields list
                await RemoveFieldFromEntityItem(entityId, fieldId);

                _logger.LogInformation("Field {FieldId} deleted from entity {EntityId}", fieldId, entityId);
            }
            catch (Exception ex) when (ex is not StorageException and not ArgumentNullException)
            {
                _logger.LogError(ex, "Failed to delete field {FieldId} from entity {EntityId}",
                    fieldId, entityId);
                throw new StorageException($"Failed to delete field {fieldId}", ex);
            }
            finally
            {
                ClearCache();
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // RELATION CRUD OPERATIONS
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<bool> CreateRelation(EntityRelation relation)
        {
            if (relation == null)
                throw new StorageException("EntityRelation cannot be null");

            try
            {
                _logger.LogInformation("Creating relation {RelationId} ({RelationName})",
                    relation.Id, relation.Name);

                // Serialize relation with TypeNameHandling.Auto (source line 47)
                string relationJson = JsonConvert.SerializeObject(relation, SerializeSettings);

                // Store relation item under origin entity PK with GSI2 for ID-based lookup
                var putRequest = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{relation.OriginEntityId}" },
                        [SK_ATTR] = new AttributeValue { S = $"{RELATION_SK_PREFIX}{relation.Id}" },
                        [GSI2PK_ATTR] = new AttributeValue { S = $"{RELATION_PK_PREFIX}{relation.Id}" },
                        [GSI2SK_ATTR] = new AttributeValue { S = META_SK },
                        [RELATION_DATA_ATTR] = new AttributeValue { S = relationJson }
                    },
                    ConditionExpression = "attribute_not_exists(PK) OR attribute_not_exists(SK)"
                };

                try
                {
                    await _dynamoDbClient.PutItemAsync(putRequest);
                }
                catch (ConditionalCheckFailedException)
                {
                    _logger.LogWarning("Relation {RelationId} already exists", relation.Id);
                    return false;
                }

                // For ManyToMany relations, M2M association records are created on-demand
                // via CreateManyToManyRecord calls. No upfront table creation needed
                // (replaces DbRepository.CreateNtoNRelation which created rel_{name} table).
                if (relation.RelationType == EntityRelationType.ManyToMany)
                {
                    _logger.LogDebug(
                        "ManyToMany relation {RelationId} created; M2M records added on demand",
                        relation.Id);
                }

                _logger.LogInformation("Relation {RelationId} ({RelationName}) created successfully",
                    relation.Id, relation.Name);
                return true;
            }
            catch (Exception ex) when (ex is not StorageException and not ArgumentNullException)
            {
                _logger.LogError(ex, "Failed to create relation {RelationId}", relation.Id);
                throw new StorageException($"Failed to create relation {relation.Id}", ex);
            }
            finally
            {
                ClearCache();
            }
        }

        /// <inheritdoc />
        public async Task<bool> UpdateRelation(EntityRelation relation)
        {
            if (relation == null) throw new ArgumentNullException(nameof(relation));

            try
            {
                _logger.LogInformation("Updating relation {RelationId}", relation.Id);

                // Serialize with TypeNameHandling.Auto (source line 125)
                string relationJson = JsonConvert.SerializeObject(relation, SerializeSettings);

                // Look up existing relation to confirm the origin entity PK
                var existingRelation = await GetRelationById(relation.Id);
                if (existingRelation == null)
                {
                    _logger.LogWarning("Relation {RelationId} not found for update", relation.Id);
                    return false;
                }

                var updateRequest = new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{relation.OriginEntityId}" },
                        [SK_ATTR] = new AttributeValue { S = $"{RELATION_SK_PREFIX}{relation.Id}" }
                    },
                    UpdateExpression = "SET #data = :data",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#data"] = RELATION_DATA_ATTR
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":data"] = new AttributeValue { S = relationJson }
                    },
                    ConditionExpression = "attribute_exists(PK)"
                };

                try
                {
                    await _dynamoDbClient.UpdateItemAsync(updateRequest);
                    _logger.LogInformation("Relation {RelationId} updated successfully", relation.Id);
                    return true;
                }
                catch (ConditionalCheckFailedException)
                {
                    _logger.LogWarning("Relation {RelationId} item not found for update", relation.Id);
                    return false;
                }
            }
            catch (Exception ex) when (ex is not StorageException and not ArgumentNullException)
            {
                _logger.LogError(ex, "Failed to update relation {RelationId}", relation.Id);
                throw new StorageException($"Failed to update relation {relation.Id}", ex);
            }
            finally
            {
                ClearCache();
            }
        }

        /// <inheritdoc />
        public async Task<EntityRelation?> GetRelationById(Guid relationId)
        {
            _logger.LogDebug("Reading relation by ID: {RelationId}", relationId);

            // Query GSI2 for relation lookup by ID
            var queryRequest = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI2_INDEX_NAME,
                KeyConditionExpression = "#gsi2pk = :pk AND #gsi2sk = :sk",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#gsi2pk"] = GSI2PK_ATTR,
                    ["#gsi2sk"] = GSI2SK_ATTR
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = $"{RELATION_PK_PREFIX}{relationId}" },
                    [":sk"] = new AttributeValue { S = META_SK }
                },
                Limit = 1
            };

            var response = await _dynamoDbClient.QueryAsync(queryRequest);

            if (response.Items == null || response.Items.Count == 0)
            {
                _logger.LogDebug("Relation {RelationId} not found", relationId);
                return null;
            }

            return DeserializeRelation(response.Items[0]);
        }

        /// <inheritdoc />
        public async Task<EntityRelation?> GetRelationByName(string relationName)
        {
            if (string.IsNullOrEmpty(relationName)) return null;

            _logger.LogDebug("Reading relation by name: {RelationName}", relationName);

            // Load all relations and filter by name (case-insensitive)
            // Source: DbRelationRepository.Read(string) line 162:
            //   r.Name.ToLowerInvariant() == relationName.ToLowerInvariant()
            var allRelations = await GetAllRelations();
            return allRelations.FirstOrDefault(r =>
                r.Name.ToLowerInvariant() == relationName.ToLowerInvariant());
        }

        /// <inheritdoc />
        public async Task<List<EntityRelation>> GetAllRelations()
        {
            _logger.LogDebug("Reading all relations");

            var relations = new List<EntityRelation>();
            Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

            // Scan GSI2 — sparse index containing only relation items
            // (only relation items have GSI2PK/GSI2SK attributes)
            do
            {
                var scanRequest = new ScanRequest
                {
                    TableName = _tableName,
                    IndexName = GSI2_INDEX_NAME,
                    ExclusiveStartKey = lastEvaluatedKey?.Count > 0 ? lastEvaluatedKey : null
                };

                var response = await _dynamoDbClient.ScanAsync(scanRequest);

                foreach (var item in response.Items)
                {
                    var relation = DeserializeRelation(item);
                    if (relation != null)
                    {
                        relations.Add(relation);
                    }
                }

                lastEvaluatedKey = response.LastEvaluatedKey;
            } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);

            _logger.LogDebug("Retrieved {Count} relations", relations.Count);
            return relations;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteRelation(Guid relationId)
        {
            try
            {
                _logger.LogInformation("Deleting relation {RelationId}", relationId);

                // Step 1: Read relation to verify existence and get metadata
                // Source: DbRelationRepository.Delete() lines 190-194
                var relation = await GetRelationById(relationId);
                if (relation == null)
                {
                    throw new StorageException("There is no record with specified relation id.");
                }

                // Step 2: For ManyToMany, delete all M2M association items
                // Replaces: DbRepository.DeleteNtoNRelation — drops rel_{name} table
                if (relation.RelationType == EntityRelationType.ManyToMany)
                {
                    await DeleteAllManyToManyRecords(relationId);
                }

                // Step 3: Delete the relation metadata item
                var deleteRequest = new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{relation.OriginEntityId}" },
                        [SK_ATTR] = new AttributeValue { S = $"{RELATION_SK_PREFIX}{relation.Id}" }
                    }
                };

                await _dynamoDbClient.DeleteItemAsync(deleteRequest);

                _logger.LogInformation("Relation {RelationId} ({RelationName}) deleted successfully",
                    relationId, relation.Name);
                return true;
            }
            catch (StorageException)
            {
                throw; // Re-throw StorageException with preserved message
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete relation {RelationId}", relationId);
                throw new StorageException($"Failed to delete relation {relationId}", ex);
            }
            finally
            {
                ClearCache();
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // MANY-TO-MANY RECORD MANAGEMENT
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task CreateManyToManyRecord(Guid relationId, Guid originId, Guid targetId)
        {
            _logger.LogDebug("Creating M2M record: relation={RelationId}, origin={OriginId}, target={TargetId}",
                relationId, originId, targetId);

            // Source: DbRelationRepository.CreateManyToManyRecord() lines 258-270
            var putRequest = new PutItemRequest
            {
                TableName = _tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    [PK_ATTR] = new AttributeValue { S = $"{RELATION_PK_PREFIX}{relationId}" },
                    [SK_ATTR] = new AttributeValue { S = $"{M2M_SK_PREFIX}{originId}#{targetId}" },
                    [ORIGIN_ID_ATTR] = new AttributeValue { S = originId.ToString() },
                    [TARGET_ID_ATTR] = new AttributeValue { S = targetId.ToString() }
                }
            };

            await _dynamoDbClient.PutItemAsync(putRequest);
            _logger.LogDebug("M2M record created for relation {RelationId}", relationId);
        }

        /// <inheritdoc />
        public async Task DeleteManyToManyRecord(
            string relationName,
            Guid? originId = null,
            Guid? targetId = null)
        {
            // Source: DbRelationRepository.DeleteManyToManyRecord() lines 272-300
            if (originId == null && targetId == null)
            {
                throw new StorageException(
                    "Both origin id and target id cannot be null when delete many to many relation!");
            }

            _logger.LogDebug(
                "Deleting M2M records: relation={RelationName}, origin={OriginId}, target={TargetId}",
                relationName, originId, targetId);

            // Look up relation by name to get its ID
            var relation = await GetRelationByName(relationName);
            if (relation == null)
            {
                _logger.LogWarning("Relation '{RelationName}' not found for M2M record deletion",
                    relationName);
                return;
            }

            if (originId != null && targetId != null)
            {
                // Delete specific M2M item when both IDs are provided
                var deleteRequest = new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK_ATTR] = new AttributeValue { S = $"{RELATION_PK_PREFIX}{relation.Id}" },
                        [SK_ATTR] = new AttributeValue { S = $"{M2M_SK_PREFIX}{originId}#{targetId}" }
                    }
                };
                await _dynamoDbClient.DeleteItemAsync(deleteRequest);
            }
            else
            {
                // Query all M2M items for this relation and filter by provided ID
                var m2mItems = await QueryManyToManyItems(relation.Id);
                var itemsToDelete = new List<WriteRequest>();

                foreach (var item in m2mItems)
                {
                    bool shouldDelete = false;

                    if (originId != null && item.ContainsKey(ORIGIN_ID_ATTR))
                    {
                        shouldDelete = item[ORIGIN_ID_ATTR].S == originId.Value.ToString();
                    }
                    else if (targetId != null && item.ContainsKey(TARGET_ID_ATTR))
                    {
                        shouldDelete = item[TARGET_ID_ATTR].S == targetId.Value.ToString();
                    }

                    if (shouldDelete)
                    {
                        itemsToDelete.Add(new WriteRequest
                        {
                            DeleteRequest = new DeleteRequest
                            {
                                Key = new Dictionary<string, AttributeValue>
                                {
                                    [PK_ATTR] = item[PK_ATTR],
                                    [SK_ATTR] = item[SK_ATTR]
                                }
                            }
                        });
                    }
                }

                if (itemsToDelete.Count > 0)
                {
                    await ExecuteBatchWrite(itemsToDelete);
                }
            }

            _logger.LogDebug("M2M records deleted for relation '{RelationName}'", relationName);
        }

        /// <inheritdoc />
        public async Task<List<KeyValuePair<Guid, Guid>>> GetManyToManyRecords(
            Guid relationId,
            Guid? originId = null,
            Guid? targetId = null)
        {
            _logger.LogDebug(
                "Reading M2M records: relation={RelationId}, origin={OriginId}, target={TargetId}",
                relationId, originId, targetId);

            var records = new List<KeyValuePair<Guid, Guid>>();
            var m2mItems = await QueryManyToManyItems(relationId);

            foreach (var item in m2mItems)
            {
                if (!item.ContainsKey(ORIGIN_ID_ATTR) || !item.ContainsKey(TARGET_ID_ATTR))
                    continue;

                var itemOriginId = Guid.Parse(item[ORIGIN_ID_ATTR].S);
                var itemTargetId = Guid.Parse(item[TARGET_ID_ATTR].S);

                // Apply optional origin/target filters
                if (originId != null && itemOriginId != originId.Value)
                    continue;
                if (targetId != null && itemTargetId != targetId.Value)
                    continue;

                records.Add(new KeyValuePair<Guid, Guid>(itemOriginId, itemTargetId));
            }

            _logger.LogDebug("Retrieved {Count} M2M records for relation {RelationId}",
                records.Count, relationId);
            return records;
        }

        // ═══════════════════════════════════════════════════════════════════
        // CACHE MANAGEMENT
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public void ClearCache()
        {
            // Source pattern: Cache.Clear() in finally blocks throughout
            // DbEntityRepository and DbRelationRepository.
            OnCacheCleared?.Invoke();
            _logger.LogDebug("Cache invalidation signaled after metadata mutation");
        }

        // ═══════════════════════════════════════════════════════════════════
        // PRIVATE HELPER METHODS
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates an individual field item in DynamoDB without updating the parent entity.
        /// Used internally by CreateEntity to create initial field items.
        /// </summary>
        private async Task CreateFieldItemInternal(Guid entityId, Field field)
        {
            string fieldJson = JsonConvert.SerializeObject(field, SerializeSettings);

            var putRequest = new PutItemRequest
            {
                TableName = _tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entityId}" },
                    [SK_ATTR] = new AttributeValue { S = $"{FIELD_SK_PREFIX}{field.Id}" },
                    [FIELD_DATA_ATTR] = new AttributeValue { S = fieldJson }
                }
            };

            await _dynamoDbClient.PutItemAsync(putRequest);
        }

        /// <summary>
        /// Auto-creates user_{entity.Name}_created_by and user_{entity.Name}_modified_by
        /// relations when a new entity is created (except for the User entity itself).
        /// Source: DbEntityRepository.Create() lines 100-139.
        /// </summary>
        private async Task CreateUserRelationsForEntity(
            Entity entity,
            Dictionary<string, Guid>? sysIdDictionary)
        {
            // Read the User entity to find its "id" field
            var userEntity = await GetEntityById(SystemIds.UserEntityId);
            if (userEntity == null)
            {
                _logger.LogWarning(
                    "User entity not found; skipping auto-relation creation for entity {EntityName}",
                    entity.Name);
                return;
            }

            var userIdField = userEntity.Fields?.SingleOrDefault(f => f.Name == "id");
            if (userIdField == null)
            {
                _logger.LogWarning(
                    "User entity 'id' field not found; skipping auto-relation creation for {EntityName}",
                    entity.Name);
                return;
            }

            // Find created_by and last_modified_by fields in the new entity
            var createdByField = entity.Fields?.SingleOrDefault(f => f.Name == "created_by");
            var modifiedByField = entity.Fields?.SingleOrDefault(f => f.Name == "last_modified_by");

            // Create user_{entity.Name}_created_by relation (OneToMany: User → entity)
            if (createdByField != null)
            {
                var relCreatedBy = new EntityRelation
                {
                    Id = Guid.NewGuid(),
                    Name = $"user_{entity.Name}_created_by",
                    Label = $"user_{entity.Name}_created_by",
                    System = true,
                    RelationType = EntityRelationType.OneToMany,
                    OriginEntityId = SystemIds.UserEntityId,
                    OriginFieldId = userIdField.Id,
                    TargetEntityId = entity.Id,
                    TargetFieldId = createdByField.Id
                };

                // Use deterministic ID from sysIdDictionary if provided
                string relKey = $"user_{entity.Name}_created_by";
                if (sysIdDictionary != null && sysIdDictionary.ContainsKey(relKey))
                {
                    relCreatedBy.Id = sysIdDictionary[relKey];
                }

                // Check if relation already exists before creating
                var existingRelation = await GetRelationByName(relCreatedBy.Name);
                if (existingRelation == null)
                {
                    var result = await CreateRelation(relCreatedBy);
                    if (!result)
                    {
                        throw new StorageException(
                            $"Creation of relation between User and {entity.Name} entities failed!");
                    }
                }
            }

            // Create user_{entity.Name}_modified_by relation (OneToMany: User → entity)
            if (modifiedByField != null)
            {
                var relModifiedBy = new EntityRelation
                {
                    Id = Guid.NewGuid(),
                    Name = $"user_{entity.Name}_modified_by",
                    Label = $"user_{entity.Name}_modified_by",
                    System = true,
                    RelationType = EntityRelationType.OneToMany,
                    OriginEntityId = SystemIds.UserEntityId,
                    OriginFieldId = userIdField.Id,
                    TargetEntityId = entity.Id,
                    TargetFieldId = modifiedByField.Id
                };

                string relKey = $"user_{entity.Name}_modified_by";
                if (sysIdDictionary != null && sysIdDictionary.ContainsKey(relKey))
                {
                    relModifiedBy.Id = sysIdDictionary[relKey];
                }

                var existingRelation = await GetRelationByName(relModifiedBy.Name);
                if (existingRelation == null)
                {
                    var result = await CreateRelation(relModifiedBy);
                    if (!result)
                    {
                        throw new StorageException(
                            $"Creation of relation between User and {entity.Name} entities failed!");
                    }
                }
            }
        }

        /// <summary>
        /// Adds a field to the parent entity's entityData JSON in the entity META item.
        /// </summary>
        private async Task AddFieldToEntityItem(Guid entityId, Field field)
        {
            var entity = await GetEntityById(entityId);
            if (entity == null) return;

            if (entity.Fields == null)
                entity.Fields = new List<Field>();

            // Avoid duplicate fields
            if (!entity.Fields.Any(f => f.Id == field.Id))
            {
                entity.Fields.Add(field);
            }

            await UpdateEntityDataInternal(entity);
        }

        /// <summary>
        /// Updates a field within the parent entity's entityData JSON.
        /// </summary>
        private async Task UpdateFieldInEntityItem(Guid entityId, Field field)
        {
            var entity = await GetEntityById(entityId);
            if (entity == null) return;

            if (entity.Fields != null)
            {
                var existingFieldIndex = entity.Fields.FindIndex(f => f.Id == field.Id);
                if (existingFieldIndex >= 0)
                {
                    entity.Fields[existingFieldIndex] = field;
                }
                else
                {
                    entity.Fields.Add(field);
                }
            }

            await UpdateEntityDataInternal(entity);
        }

        /// <summary>
        /// Removes a field from the parent entity's entityData JSON.
        /// </summary>
        private async Task RemoveFieldFromEntityItem(Guid entityId, Guid fieldId)
        {
            var entity = await GetEntityById(entityId);
            if (entity == null) return;

            if (entity.Fields != null)
            {
                entity.Fields.RemoveAll(f => f.Id == fieldId);
            }

            await UpdateEntityDataInternal(entity);
        }

        /// <summary>
        /// Updates only the entityData attribute on the entity META item (no conditional check).
        /// Used by field helper methods to sync the embedded Fields list.
        /// </summary>
        private async Task UpdateEntityDataInternal(Entity entity)
        {
            string entityJson = JsonConvert.SerializeObject(entity, SerializeSettings);

            var updateRequest = new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    [PK_ATTR] = new AttributeValue { S = $"{ENTITY_PK_PREFIX}{entity.Id}" },
                    [SK_ATTR] = new AttributeValue { S = META_SK }
                },
                UpdateExpression = "SET #data = :data",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#data"] = ENTITY_DATA_ATTR
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":data"] = new AttributeValue { S = entityJson }
                }
            };

            await _dynamoDbClient.UpdateItemAsync(updateRequest);
        }

        /// <summary>
        /// Deserializes a DynamoDB item into an Entity object using tolerant deserialization settings.
        /// Source: DbEntityRepository.Read() lines 210-216.
        /// </summary>
        private Entity? DeserializeEntity(Dictionary<string, AttributeValue> item)
        {
            if (!item.ContainsKey(ENTITY_DATA_ATTR))
                return null;

            string json = item[ENTITY_DATA_ATTR].S;
            return JsonConvert.DeserializeObject<Entity>(json, DeserializeSettings);
        }

        /// <summary>
        /// Deserializes a DynamoDB item into an EntityRelation object.
        /// Source: DbRelationRepository.Read() lines 165-185.
        /// </summary>
        private EntityRelation? DeserializeRelation(Dictionary<string, AttributeValue> item)
        {
            if (!item.ContainsKey(RELATION_DATA_ATTR))
                return null;

            string json = item[RELATION_DATA_ATTR].S;
            return JsonConvert.DeserializeObject<EntityRelation>(json, DeserializeSettings);
        }

        /// <summary>
        /// Queries all M2M association items for a given relation ID.
        /// M2M items have PK=RELATION#{relationId} and SK begins with M2M#.
        /// </summary>
        private async Task<List<Dictionary<string, AttributeValue>>> QueryManyToManyItems(Guid relationId)
        {
            var items = new List<Dictionary<string, AttributeValue>>();
            Dictionary<string, AttributeValue>? lastKey = null;

            do
            {
                var queryRequest = new QueryRequest
                {
                    TableName = _tableName,
                    KeyConditionExpression = "#pk = :pk AND begins_with(#sk, :m2mPrefix)",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#pk"] = PK_ATTR,
                        ["#sk"] = SK_ATTR
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"{RELATION_PK_PREFIX}{relationId}" },
                        [":m2mPrefix"] = new AttributeValue { S = M2M_SK_PREFIX }
                    },
                    ExclusiveStartKey = lastKey?.Count > 0 ? lastKey : null
                };

                var response = await _dynamoDbClient.QueryAsync(queryRequest);
                items.AddRange(response.Items);
                lastKey = response.LastEvaluatedKey;
            } while (lastKey != null && lastKey.Count > 0);

            return items;
        }

        /// <summary>
        /// Deletes all M2M association items for a given relation ID.
        /// Used during DeleteRelation for ManyToMany relation cleanup.
        /// Replaces: DbRepository.DeleteNtoNRelation which dropped the rel_{name} table.
        /// </summary>
        private async Task DeleteAllManyToManyRecords(Guid relationId)
        {
            var m2mItems = await QueryManyToManyItems(relationId);

            if (m2mItems.Count == 0) return;

            var deleteRequests = m2mItems.Select(item => new WriteRequest
            {
                DeleteRequest = new DeleteRequest
                {
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK_ATTR] = item[PK_ATTR],
                        [SK_ATTR] = item[SK_ATTR]
                    }
                }
            }).ToList();

            await ExecuteBatchWrite(deleteRequests);

            _logger.LogDebug("Deleted {Count} M2M records for relation {RelationId}",
                deleteRequests.Count, relationId);
        }

        /// <summary>
        /// Executes batch write requests in chunks of MAX_BATCH_WRITE_SIZE (25),
        /// handling DynamoDB's per-request item limit and processing unprocessed items.
        /// </summary>
        private async Task ExecuteBatchWrite(List<WriteRequest> writeRequests)
        {
            if (writeRequests.Count == 0) return;

            // Process in chunks of 25 (DynamoDB limit)
            for (int i = 0; i < writeRequests.Count; i += MAX_BATCH_WRITE_SIZE)
            {
                var chunk = writeRequests.Skip(i).Take(MAX_BATCH_WRITE_SIZE).ToList();

                var batchRequest = new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>>
                    {
                        [_tableName] = chunk
                    }
                };

                BatchWriteItemResponse response;
                int retryCount = 0;
                const int maxRetries = 5;

                do
                {
                    response = await _dynamoDbClient.BatchWriteItemAsync(batchRequest);

                    // Handle unprocessed items with exponential backoff
                    if (response.UnprocessedItems != null &&
                        response.UnprocessedItems.Count > 0 &&
                        response.UnprocessedItems.ContainsKey(_tableName) &&
                        response.UnprocessedItems[_tableName].Count > 0)
                    {
                        retryCount++;
                        if (retryCount >= maxRetries)
                        {
                            _logger.LogWarning(
                                "Failed to process {Count} items after {Retries} retries",
                                response.UnprocessedItems[_tableName].Count, maxRetries);
                            break;
                        }

                        // Exponential backoff: 50ms, 100ms, 200ms, 400ms, 800ms
                        await Task.Delay(50 * (int)Math.Pow(2, retryCount - 1));

                        batchRequest = new BatchWriteItemRequest
                        {
                            RequestItems = response.UnprocessedItems
                        };
                    }
                    else
                    {
                        break;
                    }
                } while (retryCount < maxRetries);
            }
        }
    }
}
