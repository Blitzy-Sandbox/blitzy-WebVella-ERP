using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WebVellaErp.Inventory.DataAccess
{
    /// <summary>
    /// Defines the contract for DynamoDB single-table data access operations for the Inventory
    /// (Project Management) microservice. Replaces the monolith's PostgreSQL-based DbRecordRepository
    /// with DynamoDB SDK operations using composite PK/SK keys and Global Secondary Indexes (GSIs).
    /// All methods are async, replacing the monolith's synchronous ADO.NET calls.
    /// </summary>
    public interface IInventoryRepository
    {
        // ── Task CRUD ──
        Task<Models.Task> CreateTaskAsync(Models.Task task);
        Task<Models.Task?> GetTaskByIdAsync(Guid taskId);
        Task<List<Models.Task>> GetTasksByProjectAsync(Guid projectId, int? limit = null, string? exclusiveStartKey = null);
        Task<List<Models.Task>> GetTasksByOwnerAsync(Guid ownerId, DateTime? since = null, int? limit = null);
        Task<List<Models.Task>> GetTasksByStatusAsync(Guid statusId, int? limit = null);
        Task<List<Models.Task>> QueryTasksAsync(string? entityTypeFilter = null, string? sortKeyPrefix = null, int? limit = null, bool scanForward = true, string? exclusiveStartKey = null);
        Task UpdateTaskAsync(Models.Task task);
        Task DeleteTaskAsync(Guid taskId);
        Task<long> CountTasksAsync(Guid? projectId = null, Guid? ownerId = null, Guid? statusId = null);

        // ── Task Watcher (replaces many-to-many relation from DbRelationRepository) ──
        Task AddTaskWatcherAsync(Guid taskId, Guid userId);
        Task RemoveTaskWatcherAsync(Guid taskId, Guid userId);
        Task<List<Guid>> GetTaskWatchersAsync(Guid taskId);

        // ── Timelog CRUD ──
        Task<Models.Timelog> CreateTimelogAsync(Models.Timelog timelog);
        Task<Models.Timelog?> GetTimelogByIdAsync(Guid timelogId);
        Task<List<Models.Timelog>> GetTimelogsByUserAsync(Guid userId, DateTime? since = null, int? limit = null);
        Task<List<Models.Timelog>> GetTimelogsByDateRangeAsync(DateTime startDate, DateTime endDate, Guid? userId = null, Guid? projectId = null);
        Task UpdateTimelogAsync(Models.Timelog timelog);
        Task DeleteTimelogAsync(Guid timelogId);

        // ── Project CRUD ──
        Task<Models.Project> CreateProjectAsync(Models.Project project);
        Task<Models.Project?> GetProjectByIdAsync(Guid projectId);
        Task<List<Models.Project>> GetAllProjectsAsync(int? limit = null, string? exclusiveStartKey = null);
        Task UpdateProjectAsync(Models.Project project);
        Task DeleteProjectAsync(Guid projectId);

        // ── Comment CRUD ──
        Task<Models.Comment> CreateCommentAsync(Models.Comment comment);
        Task<Models.Comment?> GetCommentByIdAsync(Guid commentId);
        Task<List<Models.Comment>> GetCommentsByParentAsync(Guid parentId, int? limit = null);
        Task<List<Models.Comment>> GetCommentsByRelatedRecordAsync(string relatedRecordId, int? limit = null);
        Task UpdateCommentAsync(Models.Comment comment);
        Task DeleteCommentAsync(Guid commentId);

        // ── FeedItem ──
        Task<Models.FeedItem> CreateFeedItemAsync(Models.FeedItem feedItem);
        Task<Models.FeedItem?> GetFeedItemByIdAsync(Guid feedItemId);
        Task<List<Models.FeedItem>> GetFeedItemsByUserAsync(Guid userId, int? limit = null, string? exclusiveStartKey = null);
        Task<List<Models.FeedItem>> GetFeedItemsByRelatedRecordAsync(string relatedRecordId, int? limit = null);
        Task DeleteFeedItemAsync(Guid feedItemId);

        // ── Lookups ──
        Task<Models.TaskType> CreateTaskTypeAsync(Models.TaskType taskType);
        Task<List<Models.TaskType>> GetAllTaskTypesAsync();
        Task<Models.TaskStatus> CreateTaskStatusAsync(Models.TaskStatus taskStatus);
        Task<List<Models.TaskStatus>> GetAllTaskStatusesAsync();

        // ── Batch / Transaction ──
        Task BatchCreateItemsAsync<T>(IEnumerable<T> items, Func<T, Dictionary<string, AttributeValue>> serializer);
        Task TransactWriteAsync(List<TransactWriteItem> transactItems);
    }

    /// <summary>
    /// DynamoDB single-table data access repository for the Inventory (Project Management)
    /// microservice. Implements a single-table design with composite PK/SK keys and 3 Global
    /// Secondary Indexes to serve all access patterns for tasks, timelogs, projects, comments,
    /// feed items, and lookup data.
    ///
    /// Replaces the monolith's PostgreSQL-based persistence:
    ///   - DbRecordRepository (CRUD on rec_* tables)
    ///   - EqlBuilder / EqlCommand (Entity Query Language to SQL translation)
    ///   - DbContext (ambient transactions via NpgsqlTransaction)
    ///   - DbRelationRepository (many-to-many relation management)
    ///
    /// Single-Table Key Design:
    ///   PK / SK patterns per entity type, META as default sort key.
    ///   GSI1 = entity-type index, GSI2 = user index, GSI3 = project-task index.
    /// </summary>
    public class InventoryRepository : IInventoryRepository
    {
        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly ILogger<InventoryRepository> _logger;
        private readonly string _tableName;

        // ── Partition Key prefixes ──
        private const string TASK_PK_PREFIX = "TASK#";
        private const string TIMELOG_PK_PREFIX = "TIMELOG#";
        private const string PROJECT_PK_PREFIX = "PROJECT#";
        private const string COMMENT_PK_PREFIX = "COMMENT#";
        private const string FEEDITEM_PK_PREFIX = "FEEDITEM#";
        private const string TASK_TYPE_PK_PREFIX = "TASK_TYPE#";
        private const string TASK_STATUS_PK_PREFIX = "TASK_STATUS#";

        // ── Sort Key patterns ──
        private const string META_SK = "META";
        private const string WATCHER_SK_PREFIX = "WATCHER#";

        // ── GSI names ──
        private const string GSI1_INDEX_NAME = "GSI1";
        private const string GSI2_INDEX_NAME = "GSI2";
        private const string GSI3_INDEX_NAME = "GSI3";

        // ── GSI key attribute names ──
        private const string GSI1_PK = "GSI1PK";
        private const string GSI1_SK = "GSI1SK";
        private const string GSI2_PK = "GSI2PK";
        private const string GSI2_SK = "GSI2SK";
        private const string GSI3_PK = "GSI3PK";
        private const string GSI3_SK = "GSI3SK";

        // ── Entity type identifiers for GSI1 ──
        private const string ENTITY_TYPE_TASK = "ENTITY#task";
        private const string ENTITY_TYPE_TIMELOG = "ENTITY#timelog";
        private const string ENTITY_TYPE_PROJECT = "ENTITY#project";
        private const string ENTITY_TYPE_COMMENT = "ENTITY#comment";
        private const string ENTITY_TYPE_FEEDITEM = "ENTITY#feeditem";
        private const string ENTITY_TYPE_TASK_TYPE = "ENTITY#task_type";
        private const string ENTITY_TYPE_TASK_STATUS = "ENTITY#task_status";

        // ── Batch / retry constants ──
        private const int DYNAMO_BATCH_LIMIT = 25;
        private const int DYNAMO_TRANSACT_LIMIT = 100;
        private const int MAX_RETRY_ATTEMPTS = 3;

        /// <summary>
        /// Initializes the inventory repository with DynamoDB client, structured logger,
        /// and configuration for table name resolution.
        /// </summary>
        public InventoryRepository(
            IAmazonDynamoDB dynamoDbClient,
            ILogger<InventoryRepository> logger,
            IConfiguration configuration)
        {
            _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tableName = configuration?["DynamoDB:TableName"] ?? "inventory-table";
        }

        // ═══════════════════════════════════════════════════════════════════
        //  TASK CRUD OPERATIONS
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<Models.Task> CreateTaskAsync(Models.Task task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            var item = SerializeTask(task);

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(PK) AND attribute_not_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("Task {TaskId} created successfully", task.Id);
                return task;
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Task {TaskId} already exists", task.Id);
                throw new InvalidOperationException($"Task with id '{task.Id}' already exists.");
            }
            catch (ProvisionedThroughputExceededException ex)
            {
                _logger.LogError(ex, "Provisioned throughput exceeded while creating task {TaskId}", task.Id);
                throw;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while creating task {TaskId}", task.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<Models.Task?> GetTaskByIdAsync(Guid taskId)
        {
            var request = new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = TASK_PK_PREFIX + taskId },
                    ["SK"] = new AttributeValue { S = META_SK }
                },
                ConsistentRead = false
            };

            try
            {
                GetItemResponse response = await _dynamoDbClient.GetItemAsync(request);
                if (response.Item == null || response.Item.Count == 0)
                {
                    _logger.LogInformation("Task {TaskId} not found", taskId);
                    return null;
                }

                return DeserializeTask(response.Item);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while retrieving task {TaskId}", taskId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.Task>> GetTasksByProjectAsync(
            Guid projectId, int? limit = null, string? exclusiveStartKey = null)
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI3_INDEX_NAME,
                KeyConditionExpression = $"{GSI3_PK} = :pk AND begins_with({GSI3_SK}, :skPrefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = PROJECT_PK_PREFIX + projectId },
                    [":skPrefix"] = new AttributeValue { S = TASK_PK_PREFIX }
                },
                ScanIndexForward = true
            };

            if (limit.HasValue) request.Limit = limit.Value;
            if (!string.IsNullOrEmpty(exclusiveStartKey))
                request.ExclusiveStartKey = DeserializePaginationKey(exclusiveStartKey);

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation(
                    "Retrieved {Count} tasks for project {ProjectId}", response.Items.Count, projectId);
                return response.Items.Select(DeserializeTask).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying tasks by project {ProjectId}", projectId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.Task>> GetTasksByOwnerAsync(
            Guid ownerId, DateTime? since = null, int? limit = null)
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI2_INDEX_NAME,
                ScanIndexForward = false // newest first
            };

            if (since.HasValue)
            {
                request.KeyConditionExpression = $"{GSI2_PK} = :pk AND {GSI2_SK} >= :since";
                request.ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = "USER#" + ownerId },
                    [":since"] = new AttributeValue { S = since.Value.ToString("O") }
                };
            }
            else
            {
                request.KeyConditionExpression = $"{GSI2_PK} = :pk";
                request.ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = "USER#" + ownerId }
                };
            }

            // GSI2 is shared across entity types so filter to tasks only
            request.FilterExpression = "begins_with(PK, :taskPrefix)";
            request.ExpressionAttributeValues[":taskPrefix"] = new AttributeValue { S = TASK_PK_PREFIX };

            if (limit.HasValue) request.Limit = limit.Value;

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation(
                    "Retrieved {Count} tasks for owner {OwnerId}", response.Items.Count, ownerId);
                return response.Items.Select(DeserializeTask).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying tasks by owner {OwnerId}", ownerId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.Task>> GetTasksByStatusAsync(Guid statusId, int? limit = null)
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                KeyConditionExpression = $"{GSI1_PK} = :pk",
                FilterExpression = "status_id = :statusId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = ENTITY_TYPE_TASK },
                    [":statusId"] = new AttributeValue { S = statusId.ToString() }
                },
                ScanIndexForward = true
            };

            if (limit.HasValue) request.Limit = limit.Value;

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation(
                    "Retrieved {Count} tasks with status {StatusId}", response.Items.Count, statusId);
                return response.Items.Select(DeserializeTask).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying tasks by status {StatusId}", statusId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.Task>> QueryTasksAsync(
            string? entityTypeFilter = null,
            string? sortKeyPrefix = null,
            int? limit = null,
            bool scanForward = true,
            string? exclusiveStartKey = null)
        {
            string gsi1pk = entityTypeFilter ?? ENTITY_TYPE_TASK;

            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                ScanIndexForward = scanForward
            };

            if (!string.IsNullOrEmpty(sortKeyPrefix))
            {
                request.KeyConditionExpression = $"{GSI1_PK} = :pk AND begins_with({GSI1_SK}, :skPrefix)";
                request.ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = gsi1pk },
                    [":skPrefix"] = new AttributeValue { S = sortKeyPrefix }
                };
            }
            else
            {
                request.KeyConditionExpression = $"{GSI1_PK} = :pk";
                request.ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = gsi1pk }
                };
            }

            if (limit.HasValue) request.Limit = limit.Value;
            if (!string.IsNullOrEmpty(exclusiveStartKey))
                request.ExclusiveStartKey = DeserializePaginationKey(exclusiveStartKey);

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation(
                    "QueryTasksAsync returned {Count} items for GSI1PK={Pk}", response.Items.Count, gsi1pk);
                return response.Items.Select(DeserializeTask).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error in QueryTasksAsync for GSI1PK={Pk}", gsi1pk);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task UpdateTaskAsync(Models.Task task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            var item = SerializeTask(task);

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_exists(PK) AND attribute_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("Task {TaskId} updated successfully", task.Id);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Task {TaskId} not found for update", task.Id);
                throw new InvalidOperationException("Failed to update task. Task not found.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while updating task {TaskId}", task.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeleteTaskAsync(Guid taskId)
        {
            // Verify existence first (adapted from DbRecordRepository.Delete, line 195-204)
            var existing = await GetTaskByIdAsync(taskId);
            if (existing == null)
            {
                throw new InvalidOperationException("There is no task with such id to delete.");
            }

            // Query all items under this task PK (META + WATCHER# items)
            var queryRequest = new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "PK = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = TASK_PK_PREFIX + taskId }
                },
                ProjectionExpression = "PK, SK"
            };

            try
            {
                QueryResponse queryResponse = await _dynamoDbClient.QueryAsync(queryRequest);
                var keysToDelete = queryResponse.Items;

                // Batch delete in chunks of 25 (DynamoDB BatchWriteItem limit)
                foreach (var chunk in ChunkList(keysToDelete, DYNAMO_BATCH_LIMIT))
                {
                    var batchRequest = new BatchWriteItemRequest
                    {
                        RequestItems = new Dictionary<string, List<WriteRequest>>
                        {
                            [_tableName] = chunk.Select(key => new WriteRequest
                            {
                                DeleteRequest = new DeleteRequest { Key = key }
                            }).ToList()
                        }
                    };

                    await ExecuteBatchWriteWithRetry(batchRequest);
                }

                _logger.LogInformation(
                    "Task {TaskId} and {WatcherCount} related items deleted",
                    taskId, keysToDelete.Count - 1);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while deleting task {TaskId}", taskId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<long> CountTasksAsync(
            Guid? projectId = null, Guid? ownerId = null, Guid? statusId = null)
        {
            QueryRequest request;

            if (projectId.HasValue)
            {
                // Count tasks for a specific project via GSI3
                request = new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = GSI3_INDEX_NAME,
                    KeyConditionExpression = $"{GSI3_PK} = :pk AND begins_with({GSI3_SK}, :skPrefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = PROJECT_PK_PREFIX + projectId.Value },
                        [":skPrefix"] = new AttributeValue { S = TASK_PK_PREFIX }
                    },
                    Select = "COUNT"
                };
            }
            else if (ownerId.HasValue)
            {
                // Count tasks for a specific owner via GSI2 with task-type filter
                request = new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = GSI2_INDEX_NAME,
                    KeyConditionExpression = $"{GSI2_PK} = :pk",
                    FilterExpression = "begins_with(PK, :taskPrefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = "USER#" + ownerId.Value },
                        [":taskPrefix"] = new AttributeValue { S = TASK_PK_PREFIX }
                    },
                    Select = "COUNT"
                };
            }
            else if (statusId.HasValue)
            {
                // Count tasks with a specific status via GSI1 with status filter
                request = new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = GSI1_INDEX_NAME,
                    KeyConditionExpression = $"{GSI1_PK} = :pk",
                    FilterExpression = "status_id = :statusId",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = ENTITY_TYPE_TASK },
                        [":statusId"] = new AttributeValue { S = statusId.Value.ToString() }
                    },
                    Select = "COUNT"
                };
            }
            else
            {
                // Count all tasks via GSI1
                request = new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = GSI1_INDEX_NAME,
                    KeyConditionExpression = $"{GSI1_PK} = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = ENTITY_TYPE_TASK }
                    },
                    Select = "COUNT"
                };
            }

            try
            {
                long total = 0;
                QueryResponse response;
                do
                {
                    response = await _dynamoDbClient.QueryAsync(request);
                    total += response.Count;
                    request.ExclusiveStartKey = response.LastEvaluatedKey;
                } while (response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0);

                _logger.LogInformation("CountTasksAsync returned {Count}", total);
                return total;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error in CountTasksAsync");
                throw;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  TASK WATCHER OPERATIONS
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task AddTaskWatcherAsync(Guid taskId, Guid userId)
        {
            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = TASK_PK_PREFIX + taskId },
                    ["SK"] = new AttributeValue { S = WATCHER_SK_PREFIX + userId },
                    ["user_id"] = new AttributeValue { S = userId.ToString() },
                    ["task_id"] = new AttributeValue { S = taskId.ToString() }
                },
                ConditionExpression = "attribute_not_exists(PK) AND attribute_not_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("Watcher {UserId} added to task {TaskId}", userId, taskId);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogInformation("Watcher {UserId} already exists on task {TaskId}", userId, taskId);
                // Idempotent — silently succeed if watcher already exists
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error adding watcher {UserId} to task {TaskId}", userId, taskId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task RemoveTaskWatcherAsync(Guid taskId, Guid userId)
        {
            var request = new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = TASK_PK_PREFIX + taskId },
                    ["SK"] = new AttributeValue { S = WATCHER_SK_PREFIX + userId }
                }
            };

            try
            {
                await _dynamoDbClient.DeleteItemAsync(request);
                _logger.LogInformation("Watcher {UserId} removed from task {TaskId}", userId, taskId);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error removing watcher {UserId} from task {TaskId}", userId, taskId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Guid>> GetTaskWatchersAsync(Guid taskId)
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = TASK_PK_PREFIX + taskId },
                    [":skPrefix"] = new AttributeValue { S = WATCHER_SK_PREFIX }
                },
                ProjectionExpression = "user_id"
            };

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                var watchers = response.Items
                    .Where(item => item.ContainsKey("user_id"))
                    .Select(item => Guid.Parse(item["user_id"].S))
                    .ToList();

                _logger.LogInformation("Task {TaskId} has {Count} watchers", taskId, watchers.Count);
                return watchers;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error getting watchers for task {TaskId}", taskId);
                throw;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  TIMELOG CRUD OPERATIONS
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<Models.Timelog> CreateTimelogAsync(Models.Timelog timelog)
        {
            if (timelog == null) throw new ArgumentNullException(nameof(timelog));

            var item = SerializeTimelog(timelog);

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(PK) AND attribute_not_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("Timelog {TimelogId} created successfully", timelog.Id);
                return timelog;
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Timelog {TimelogId} already exists", timelog.Id);
                throw new InvalidOperationException($"Timelog with id '{timelog.Id}' already exists.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while creating timelog {TimelogId}", timelog.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<Models.Timelog?> GetTimelogByIdAsync(Guid timelogId)
        {
            var request = new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = TIMELOG_PK_PREFIX + timelogId },
                    ["SK"] = new AttributeValue { S = META_SK }
                },
                ConsistentRead = false
            };

            try
            {
                GetItemResponse response = await _dynamoDbClient.GetItemAsync(request);
                if (response.Item == null || response.Item.Count == 0)
                {
                    _logger.LogInformation("Timelog {TimelogId} not found", timelogId);
                    return null;
                }

                return DeserializeTimelog(response.Item);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while retrieving timelog {TimelogId}", timelogId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.Timelog>> GetTimelogsByUserAsync(
            Guid userId, DateTime? since = null, int? limit = null)
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI2_INDEX_NAME,
                ScanIndexForward = false // newest first
            };

            if (since.HasValue)
            {
                request.KeyConditionExpression = $"{GSI2_PK} = :pk AND {GSI2_SK} >= :since";
                request.ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = "USER#" + userId },
                    [":since"] = new AttributeValue { S = since.Value.ToString("O") }
                };
            }
            else
            {
                request.KeyConditionExpression = $"{GSI2_PK} = :pk";
                request.ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = "USER#" + userId }
                };
            }

            // GSI2 shared across entity types — filter to timelogs
            request.FilterExpression = "begins_with(PK, :timelogPrefix)";
            request.ExpressionAttributeValues[":timelogPrefix"] = new AttributeValue { S = TIMELOG_PK_PREFIX };

            if (limit.HasValue) request.Limit = limit.Value;

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation(
                    "Retrieved {Count} timelogs for user {UserId}", response.Items.Count, userId);
                return response.Items.Select(DeserializeTimelog).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying timelogs by user {UserId}", userId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.Timelog>> GetTimelogsByDateRangeAsync(
            DateTime startDate, DateTime endDate, Guid? userId = null, Guid? projectId = null)
        {
            // Use GSI1 with BETWEEN on GSI1SK (logged_on timestamp)
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                KeyConditionExpression = $"{GSI1_PK} = :pk AND {GSI1_SK} BETWEEN :start AND :end",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = ENTITY_TYPE_TIMELOG },
                    [":start"] = new AttributeValue { S = startDate.ToString("O") },
                    [":end"] = new AttributeValue { S = endDate.ToString("O") + "~" } // tilde sorts after all ISO chars
                },
                ScanIndexForward = true
            };

            // Apply optional filters
            var filters = new List<string>();
            if (userId.HasValue)
            {
                filters.Add("created_by = :userId");
                request.ExpressionAttributeValues[":userId"] = new AttributeValue { S = userId.Value.ToString() };
            }
            if (projectId.HasValue)
            {
                filters.Add("contains(l_related_records, :projectId)");
                request.ExpressionAttributeValues[":projectId"] = new AttributeValue { S = projectId.Value.ToString() };
            }
            if (filters.Count > 0)
            {
                request.FilterExpression = string.Join(" AND ", filters);
            }

            try
            {
                var allItems = new List<Dictionary<string, AttributeValue>>();
                QueryResponse response;
                do
                {
                    response = await _dynamoDbClient.QueryAsync(request);
                    allItems.AddRange(response.Items);
                    request.ExclusiveStartKey = response.LastEvaluatedKey;
                } while (response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0);

                _logger.LogInformation(
                    "Retrieved {Count} timelogs between {Start} and {End}",
                    allItems.Count, startDate, endDate);
                return allItems.Select(DeserializeTimelog).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying timelogs by date range");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task UpdateTimelogAsync(Models.Timelog timelog)
        {
            if (timelog == null) throw new ArgumentNullException(nameof(timelog));

            var item = SerializeTimelog(timelog);

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_exists(PK) AND attribute_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("Timelog {TimelogId} updated successfully", timelog.Id);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Timelog {TimelogId} not found for update", timelog.Id);
                throw new InvalidOperationException("Failed to update timelog. Timelog not found.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while updating timelog {TimelogId}", timelog.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeleteTimelogAsync(Guid timelogId)
        {
            var request = new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = TIMELOG_PK_PREFIX + timelogId },
                    ["SK"] = new AttributeValue { S = META_SK }
                },
                ConditionExpression = "attribute_exists(PK)",
                ReturnValues = "ALL_OLD"
            };

            try
            {
                await _dynamoDbClient.DeleteItemAsync(request);
                _logger.LogInformation("Timelog {TimelogId} deleted successfully", timelogId);
            }
            catch (ConditionalCheckFailedException)
            {
                throw new InvalidOperationException("There is no timelog with such id to delete.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while deleting timelog {TimelogId}", timelogId);
                throw;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  PROJECT CRUD OPERATIONS
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<Models.Project> CreateProjectAsync(Models.Project project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));

            var item = SerializeProject(project);

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(PK) AND attribute_not_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("Project {ProjectId} created successfully", project.Id);
                return project;
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Project {ProjectId} already exists", project.Id);
                throw new InvalidOperationException($"Project with id '{project.Id}' already exists.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while creating project {ProjectId}", project.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<Models.Project?> GetProjectByIdAsync(Guid projectId)
        {
            var request = new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = PROJECT_PK_PREFIX + projectId },
                    ["SK"] = new AttributeValue { S = META_SK }
                },
                ConsistentRead = false
            };

            try
            {
                GetItemResponse response = await _dynamoDbClient.GetItemAsync(request);
                if (response.Item == null || response.Item.Count == 0)
                {
                    _logger.LogInformation("Project {ProjectId} not found", projectId);
                    return null;
                }

                return DeserializeProject(response.Item);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while retrieving project {ProjectId}", projectId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.Project>> GetAllProjectsAsync(
            int? limit = null, string? exclusiveStartKey = null)
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                KeyConditionExpression = $"{GSI1_PK} = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = ENTITY_TYPE_PROJECT }
                },
                ScanIndexForward = true // alphabetical by name
            };

            if (limit.HasValue) request.Limit = limit.Value;
            if (!string.IsNullOrEmpty(exclusiveStartKey))
                request.ExclusiveStartKey = DeserializePaginationKey(exclusiveStartKey);

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation("Retrieved {Count} projects", response.Items.Count);
                return response.Items.Select(DeserializeProject).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying all projects");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task UpdateProjectAsync(Models.Project project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));

            var item = SerializeProject(project);

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_exists(PK) AND attribute_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("Project {ProjectId} updated successfully", project.Id);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Project {ProjectId} not found for update", project.Id);
                throw new InvalidOperationException("Failed to update project. Project not found.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while updating project {ProjectId}", project.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeleteProjectAsync(Guid projectId)
        {
            var request = new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = PROJECT_PK_PREFIX + projectId },
                    ["SK"] = new AttributeValue { S = META_SK }
                },
                ConditionExpression = "attribute_exists(PK)",
                ReturnValues = "ALL_OLD"
            };

            try
            {
                await _dynamoDbClient.DeleteItemAsync(request);
                _logger.LogInformation("Project {ProjectId} deleted successfully", projectId);
            }
            catch (ConditionalCheckFailedException)
            {
                throw new InvalidOperationException("There is no project with such id to delete.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while deleting project {ProjectId}", projectId);
                throw;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  COMMENT CRUD OPERATIONS
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<Models.Comment> CreateCommentAsync(Models.Comment comment)
        {
            if (comment == null) throw new ArgumentNullException(nameof(comment));

            var item = SerializeComment(comment);

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(PK) AND attribute_not_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("Comment {CommentId} created successfully", comment.Id);
                return comment;
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Comment {CommentId} already exists", comment.Id);
                throw new InvalidOperationException($"Comment with id '{comment.Id}' already exists.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while creating comment {CommentId}", comment.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<Models.Comment?> GetCommentByIdAsync(Guid commentId)
        {
            var request = new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = COMMENT_PK_PREFIX + commentId },
                    ["SK"] = new AttributeValue { S = META_SK }
                },
                ConsistentRead = false
            };

            try
            {
                GetItemResponse response = await _dynamoDbClient.GetItemAsync(request);
                if (response.Item == null || response.Item.Count == 0)
                {
                    _logger.LogInformation("Comment {CommentId} not found", commentId);
                    return null;
                }

                return DeserializeComment(response.Item);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while retrieving comment {CommentId}", commentId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.Comment>> GetCommentsByParentAsync(Guid parentId, int? limit = null)
        {
            // Uses GSI1 with PARENT#{parentId} for threaded comment queries
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                KeyConditionExpression = $"{GSI1_PK} = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = "PARENT#" + parentId }
                },
                ScanIndexForward = true // chronological order
            };

            if (limit.HasValue) request.Limit = limit.Value;

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation(
                    "Retrieved {Count} comments for parent {ParentId}", response.Items.Count, parentId);
                return response.Items.Select(DeserializeComment).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying comments by parent {ParentId}", parentId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.Comment>> GetCommentsByRelatedRecordAsync(
            string relatedRecordId, int? limit = null)
        {
            if (string.IsNullOrEmpty(relatedRecordId))
                throw new ArgumentNullException(nameof(relatedRecordId));

            // Query GSI1 for all comments, then filter by l_related_records containing the record ID
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                KeyConditionExpression = $"{GSI1_PK} = :pk",
                FilterExpression = "contains(l_related_records, :relatedId)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = ENTITY_TYPE_COMMENT },
                    [":relatedId"] = new AttributeValue { S = relatedRecordId }
                },
                ScanIndexForward = true
            };

            if (limit.HasValue) request.Limit = limit.Value;

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation(
                    "Retrieved {Count} comments for related record {RelatedRecordId}",
                    response.Items.Count, relatedRecordId);
                return response.Items.Select(DeserializeComment).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying comments by related record {RelatedRecordId}", relatedRecordId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task UpdateCommentAsync(Models.Comment comment)
        {
            if (comment == null) throw new ArgumentNullException(nameof(comment));

            var item = SerializeComment(comment);

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_exists(PK) AND attribute_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("Comment {CommentId} updated successfully", comment.Id);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Comment {CommentId} not found for update", comment.Id);
                throw new InvalidOperationException("Failed to update comment. Comment not found.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while updating comment {CommentId}", comment.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeleteCommentAsync(Guid commentId)
        {
            var request = new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = COMMENT_PK_PREFIX + commentId },
                    ["SK"] = new AttributeValue { S = META_SK }
                },
                ConditionExpression = "attribute_exists(PK)",
                ReturnValues = "ALL_OLD"
            };

            try
            {
                await _dynamoDbClient.DeleteItemAsync(request);
                _logger.LogInformation("Comment {CommentId} deleted successfully", commentId);
            }
            catch (ConditionalCheckFailedException)
            {
                throw new InvalidOperationException("There is no comment with such id to delete.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while deleting comment {CommentId}", commentId);
                throw;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  FEEDITEM OPERATIONS
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<Models.FeedItem> CreateFeedItemAsync(Models.FeedItem feedItem)
        {
            if (feedItem == null) throw new ArgumentNullException(nameof(feedItem));

            var item = SerializeFeedItem(feedItem);

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(PK) AND attribute_not_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("FeedItem {FeedItemId} created successfully", feedItem.Id);
                return feedItem;
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("FeedItem {FeedItemId} already exists", feedItem.Id);
                throw new InvalidOperationException($"FeedItem with id '{feedItem.Id}' already exists.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while creating feed item {FeedItemId}", feedItem.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<Models.FeedItem?> GetFeedItemByIdAsync(Guid feedItemId)
        {
            var request = new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = FEEDITEM_PK_PREFIX + feedItemId },
                    ["SK"] = new AttributeValue { S = META_SK }
                },
                ConsistentRead = false
            };

            try
            {
                GetItemResponse response = await _dynamoDbClient.GetItemAsync(request);
                if (response.Item == null || response.Item.Count == 0)
                {
                    _logger.LogInformation("FeedItem {FeedItemId} not found", feedItemId);
                    return null;
                }

                return DeserializeFeedItem(response.Item);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while retrieving feed item {FeedItemId}", feedItemId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.FeedItem>> GetFeedItemsByUserAsync(
            Guid userId, int? limit = null, string? exclusiveStartKey = null)
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI2_INDEX_NAME,
                KeyConditionExpression = $"{GSI2_PK} = :pk",
                FilterExpression = "begins_with(PK, :feedPrefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = "USER#" + userId },
                    [":feedPrefix"] = new AttributeValue { S = FEEDITEM_PK_PREFIX }
                },
                ScanIndexForward = false // newest first
            };

            if (limit.HasValue) request.Limit = limit.Value;
            if (!string.IsNullOrEmpty(exclusiveStartKey))
                request.ExclusiveStartKey = DeserializePaginationKey(exclusiveStartKey);

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation(
                    "Retrieved {Count} feed items for user {UserId}", response.Items.Count, userId);
                return response.Items.Select(DeserializeFeedItem).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying feed items by user {UserId}", userId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.FeedItem>> GetFeedItemsByRelatedRecordAsync(
            string relatedRecordId, int? limit = null)
        {
            if (string.IsNullOrEmpty(relatedRecordId))
                throw new ArgumentNullException(nameof(relatedRecordId));

            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                KeyConditionExpression = $"{GSI1_PK} = :pk",
                FilterExpression = "contains(l_related_records, :relatedId)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = ENTITY_TYPE_FEEDITEM },
                    [":relatedId"] = new AttributeValue { S = relatedRecordId }
                },
                ScanIndexForward = false // newest first
            };

            if (limit.HasValue) request.Limit = limit.Value;

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation(
                    "Retrieved {Count} feed items for related record {RelatedRecordId}",
                    response.Items.Count, relatedRecordId);
                return response.Items.Select(DeserializeFeedItem).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying feed items by related record {RelatedRecordId}", relatedRecordId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeleteFeedItemAsync(Guid feedItemId)
        {
            var request = new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = FEEDITEM_PK_PREFIX + feedItemId },
                    ["SK"] = new AttributeValue { S = META_SK }
                },
                ConditionExpression = "attribute_exists(PK)",
                ReturnValues = "ALL_OLD"
            };

            try
            {
                await _dynamoDbClient.DeleteItemAsync(request);
                _logger.LogInformation("FeedItem {FeedItemId} deleted successfully", feedItemId);
            }
            catch (ConditionalCheckFailedException)
            {
                throw new InvalidOperationException("There is no feed item with such id to delete.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while deleting feed item {FeedItemId}", feedItemId);
                throw;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  TASKTYPE AND TASKSTATUS LOOKUP OPERATIONS
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<Models.TaskType> CreateTaskTypeAsync(Models.TaskType taskType)
        {
            if (taskType == null) throw new ArgumentNullException(nameof(taskType));

            var item = SerializeTaskType(taskType);

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(PK) AND attribute_not_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("TaskType {TaskTypeId} created successfully", taskType.Id);
                return taskType;
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("TaskType {TaskTypeId} already exists", taskType.Id);
                throw new InvalidOperationException($"TaskType with id '{taskType.Id}' already exists.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while creating task type {TaskTypeId}", taskType.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.TaskType>> GetAllTaskTypesAsync()
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                KeyConditionExpression = $"{GSI1_PK} = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = ENTITY_TYPE_TASK_TYPE }
                },
                ScanIndexForward = true // alphabetical by label
            };

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation("Retrieved {Count} task types", response.Items.Count);
                return response.Items.Select(DeserializeTaskType).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying all task types");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<Models.TaskStatus> CreateTaskStatusAsync(Models.TaskStatus taskStatus)
        {
            if (taskStatus == null) throw new ArgumentNullException(nameof(taskStatus));

            var item = SerializeTaskStatus(taskStatus);

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(PK) AND attribute_not_exists(SK)"
            };

            try
            {
                await _dynamoDbClient.PutItemAsync(request);
                _logger.LogInformation("TaskStatus {TaskStatusId} created successfully", taskStatus.Id);
                return taskStatus;
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("TaskStatus {TaskStatusId} already exists", taskStatus.Id);
                throw new InvalidOperationException($"TaskStatus with id '{taskStatus.Id}' already exists.");
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while creating task status {TaskStatusId}", taskStatus.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Models.TaskStatus>> GetAllTaskStatusesAsync()
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GSI1_INDEX_NAME,
                KeyConditionExpression = $"{GSI1_PK} = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = ENTITY_TYPE_TASK_STATUS }
                },
                ScanIndexForward = true // sorted by sort_order (padded)
            };

            try
            {
                QueryResponse response = await _dynamoDbClient.QueryAsync(request);
                _logger.LogInformation("Retrieved {Count} task statuses", response.Items.Count);
                return response.Items.Select(DeserializeTaskStatus).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error querying all task statuses");
                throw;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  BATCH AND TRANSACTIONAL OPERATIONS
        // ═══════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task BatchCreateItemsAsync<T>(
            IEnumerable<T> items,
            Func<T, Dictionary<string, AttributeValue>> serializer)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));

            var allItems = items.ToList();
            if (allItems.Count == 0) return;

            int batchNumber = 0;
            foreach (var chunk in ChunkList(allItems, DYNAMO_BATCH_LIMIT))
            {
                batchNumber++;
                var writeRequests = chunk.Select(item => new WriteRequest
                {
                    PutRequest = new PutRequest
                    {
                        Item = serializer(item)
                    }
                }).ToList();

                var batchRequest = new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>>
                    {
                        [_tableName] = writeRequests
                    }
                };

                await ExecuteBatchWriteWithRetry(batchRequest);
                _logger.LogInformation(
                    "Batch {BatchNumber}: wrote {Count} items successfully", batchNumber, chunk.Count);
            }

            _logger.LogInformation(
                "BatchCreateItemsAsync completed: {TotalCount} items across {BatchCount} batches",
                allItems.Count, batchNumber);
        }

        /// <inheritdoc />
        public async Task TransactWriteAsync(List<TransactWriteItem> transactItems)
        {
            if (transactItems == null) throw new ArgumentNullException(nameof(transactItems));
            if (transactItems.Count == 0) return;
            if (transactItems.Count > DYNAMO_TRANSACT_LIMIT)
            {
                throw new InvalidOperationException(
                    $"TransactWriteItems supports a maximum of {DYNAMO_TRANSACT_LIMIT} items. " +
                    $"Received {transactItems.Count}.");
            }

            var request = new TransactWriteItemsRequest
            {
                TransactItems = transactItems
            };

            try
            {
                await _dynamoDbClient.TransactWriteItemsAsync(request);
                _logger.LogInformation(
                    "TransactWriteAsync completed: {Count} items", transactItems.Count);
            }
            catch (ConditionalCheckFailedException ex)
            {
                _logger.LogError(ex, "Transaction condition check failed for {Count} items", transactItems.Count);
                throw new InvalidOperationException(
                    "Transaction failed due to a conditional check failure.", ex);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error during TransactWriteAsync with {Count} items", transactItems.Count);
                throw;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  PRIVATE UTILITY METHODS
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Executes a BatchWriteItemRequest with exponential backoff retry for unprocessed items.
        /// DynamoDB may return unprocessed items when throughput is exceeded.
        /// </summary>
        private async Task ExecuteBatchWriteWithRetry(BatchWriteItemRequest batchRequest)
        {
            int attempt = 0;
            var currentRequest = batchRequest;

            while (true)
            {
                try
                {
                    var response = await _dynamoDbClient.BatchWriteItemAsync(currentRequest);

                    if (response.UnprocessedItems == null ||
                        !response.UnprocessedItems.Any(kvp => kvp.Value.Count > 0))
                    {
                        return; // all items processed
                    }

                    attempt++;
                    if (attempt >= MAX_RETRY_ATTEMPTS)
                    {
                        int unprocessedCount = response.UnprocessedItems
                            .Sum(kvp => kvp.Value.Count);
                        _logger.LogError(
                            "BatchWriteItem still has {UnprocessedCount} unprocessed items after {Attempts} retries",
                            unprocessedCount, MAX_RETRY_ATTEMPTS);
                        throw new InvalidOperationException(
                            $"BatchWriteItem failed: {unprocessedCount} items remain unprocessed after {MAX_RETRY_ATTEMPTS} retries.");
                    }

                    // Exponential backoff: 100ms, 200ms, 400ms
                    int delayMs = (int)(100 * Math.Pow(2, attempt - 1));
                    _logger.LogWarning(
                        "BatchWriteItem has unprocessed items. Retry attempt {Attempt}/{Max} after {Delay}ms",
                        attempt, MAX_RETRY_ATTEMPTS, delayMs);
                    await System.Threading.Tasks.Task.Delay(delayMs);

                    currentRequest = new BatchWriteItemRequest
                    {
                        RequestItems = response.UnprocessedItems
                    };
                }
                catch (ProvisionedThroughputExceededException ex)
                {
                    attempt++;
                    if (attempt >= MAX_RETRY_ATTEMPTS)
                    {
                        _logger.LogError(ex,
                            "Provisioned throughput exceeded during batch write after {Attempts} retries",
                            MAX_RETRY_ATTEMPTS);
                        throw;
                    }

                    int delayMs = (int)(100 * Math.Pow(2, attempt - 1));
                    _logger.LogWarning(
                        "Throughput exceeded. Retry attempt {Attempt}/{Max} after {Delay}ms",
                        attempt, MAX_RETRY_ATTEMPTS, delayMs);
                    await System.Threading.Tasks.Task.Delay(delayMs);
                }
            }
        }

        /// <summary>
        /// Splits a list into chunks of the specified size for DynamoDB batch operations.
        /// </summary>
        private static List<List<T>> ChunkList<T>(IEnumerable<T> source, int chunkSize)
        {
            var chunks = new List<List<T>>();
            var currentChunk = new List<T>();

            foreach (var item in source)
            {
                currentChunk.Add(item);
                if (currentChunk.Count >= chunkSize)
                {
                    chunks.Add(currentChunk);
                    currentChunk = new List<T>();
                }
            }

            if (currentChunk.Count > 0)
            {
                chunks.Add(currentChunk);
            }

            return chunks;
        }

        /// <summary>
        /// Converts a base64-encoded JSON string back to a DynamoDB ExclusiveStartKey dictionary.
        /// Used for cursor-based pagination across API calls. Uses manual JSON parsing to avoid
        /// System.Text.Json AOT/trimmer issues (IL2026/IL3050) since the payload is simple key-value.
        /// Expected format: {"PK":"value","SK":"value",...}
        /// </summary>
        private static Dictionary<string, AttributeValue> DeserializePaginationKey(string encodedKey)
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedKey));
                var result = new Dictionary<string, AttributeValue>();

                // Manual parse of simple {"key":"value",...} JSON without reflection-based deserializer
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    result[prop.Name] = new AttributeValue { S = prop.Value.GetString() ?? string.Empty };
                }

                return result;
            }
            catch
            {
                return new Dictionary<string, AttributeValue>();
            }
        }

        /// <summary>
        /// Safely retrieves a string value from a DynamoDB item dictionary.
        /// Returns the provided default value if the attribute is missing or null.
        /// </summary>
        private static string GetStringValue(
            Dictionary<string, AttributeValue> item, string key, string defaultValue = "")
        {
            return item.TryGetValue(key, out var attr) && attr.S != null ? attr.S : defaultValue;
        }

        /// <summary>
        /// Safely retrieves a nullable Guid value from a DynamoDB item dictionary.
        /// Returns null if the attribute is missing, null, or cannot be parsed.
        /// </summary>
        private static Guid? GetNullableGuidValue(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null && Guid.TryParse(attr.S, out var result))
            {
                return result;
            }
            return null;
        }

        /// <summary>
        /// Safely retrieves a Guid value from a DynamoDB item dictionary.
        /// Returns Guid.Empty if the attribute is missing or cannot be parsed.
        /// </summary>
        private static Guid GetGuidValue(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null && Guid.TryParse(attr.S, out var result))
            {
                return result;
            }
            return Guid.Empty;
        }

        /// <summary>
        /// Safely retrieves a DateTime value from a DynamoDB item dictionary.
        /// Expects ISO 8601 "O" format strings. Returns DateTime.MinValue if missing/invalid.
        /// </summary>
        private static DateTime GetDateTimeValue(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null &&
                DateTime.TryParse(attr.S, null, System.Globalization.DateTimeStyles.RoundtripKind, out var result))
            {
                return result;
            }
            return DateTime.MinValue;
        }

        /// <summary>
        /// Safely retrieves a nullable DateTime value from a DynamoDB item dictionary.
        /// </summary>
        private static DateTime? GetNullableDateTimeValue(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null &&
                DateTime.TryParse(attr.S, null, System.Globalization.DateTimeStyles.RoundtripKind, out var result))
            {
                return result;
            }
            return null;
        }

        /// <summary>
        /// Safely retrieves a decimal value from a DynamoDB item dictionary.
        /// DynamoDB stores numbers as the N type (string representation of a number).
        /// </summary>
        private static decimal GetDecimalValue(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.N != null &&
                decimal.TryParse(attr.N, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }
            return 0m;
        }

        /// <summary>
        /// Safely retrieves an integer value from a DynamoDB item dictionary.
        /// </summary>
        private static int GetIntValue(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.N != null &&
                int.TryParse(attr.N, out var result))
            {
                return result;
            }
            return 0;
        }

        /// <summary>
        /// Safely retrieves a boolean value from a DynamoDB item dictionary.
        /// </summary>
        private static bool GetBoolValue(Dictionary<string, AttributeValue> item, string key, bool defaultValue = false)
        {
            if (item.TryGetValue(key, out var attr))
            {
                return attr.BOOL;
            }
            return defaultValue;
        }

        /// <summary>
        /// Adds a string attribute to the DynamoDB item dictionary only if the value is non-null and non-empty.
        /// DynamoDB does not allow empty string attributes for non-key attributes.
        /// </summary>
        private static void AddStringAttribute(
            Dictionary<string, AttributeValue> item, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                item[key] = new AttributeValue { S = value };
            }
        }

        /// <summary>
        /// Adds a nullable Guid attribute to the DynamoDB item dictionary only if the value has a value.
        /// </summary>
        private static void AddNullableGuidAttribute(
            Dictionary<string, AttributeValue> item, string key, Guid? value)
        {
            if (value.HasValue)
            {
                item[key] = new AttributeValue { S = value.Value.ToString() };
            }
        }

        /// <summary>
        /// Adds a nullable DateTime attribute (ISO 8601 format) to the DynamoDB item dictionary
        /// only if the value has a value.
        /// </summary>
        private static void AddNullableDateTimeAttribute(
            Dictionary<string, AttributeValue> item, string key, DateTime? value)
        {
            if (value.HasValue)
            {
                item[key] = new AttributeValue { S = value.Value.ToString("O") };
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  SERIALIZATION / DESERIALIZATION HELPERS
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Serializes a Task domain model to a DynamoDB item dictionary including PK/SK and
        /// all three GSI key attributes. Handles nullable fields gracefully.
        /// </summary>
        private Dictionary<string, AttributeValue> SerializeTask(Models.Task task)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                // Primary key
                ["PK"] = new AttributeValue { S = TASK_PK_PREFIX + task.Id },
                ["SK"] = new AttributeValue { S = META_SK },

                // Core identifiers
                ["id"] = new AttributeValue { S = task.Id.ToString() },
                ["subject"] = new AttributeValue { S = task.Subject ?? string.Empty },
                ["created_on"] = new AttributeValue { S = task.CreatedOn.ToString("O") },

                // Numeric fields
                ["number"] = new AttributeValue { N = task.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ["estimated_minutes"] = new AttributeValue { N = task.EstimatedMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ["x_billable_minutes"] = new AttributeValue { N = task.XBillableMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ["x_nonbillable_minutes"] = new AttributeValue { N = task.XNonBillableMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) },

                // Boolean fields
                ["reserve_time"] = new AttributeValue { BOOL = task.ReserveTime },

                // GSI1: entity-type index — sorted by priority then ID
                [GSI1_PK] = new AttributeValue { S = ENTITY_TYPE_TASK },
                [GSI1_SK] = new AttributeValue { S = $"{task.Priority ?? "00000"}#{task.Id}" }
            };

            // Optional string fields (DynamoDB cannot store empty strings for non-key attrs)
            AddStringAttribute(item, "priority", task.Priority);
            AddStringAttribute(item, "body", task.Body);
            AddStringAttribute(item, "key", task.Key);
            AddStringAttribute(item, "recurrence_template", task.RecurrenceTemplate);
            AddStringAttribute(item, "l_scope", task.LScope);
            AddStringAttribute(item, "l_related_records", task.LRelatedRecords);

            // Guid fields (always present as non-nullable in model)
            item["status_id"] = new AttributeValue { S = task.StatusId.ToString() };
            item["type_id"] = new AttributeValue { S = task.TypeId.ToString() };
            item["created_by"] = new AttributeValue { S = task.CreatedBy.ToString() };

            // Nullable Guid fields
            AddNullableGuidAttribute(item, "owner_id", task.OwnerId);
            AddNullableGuidAttribute(item, "last_modified_by", task.LastModifiedBy);
            AddNullableGuidAttribute(item, "recurrence_id", task.RecurrenceId);

            // Nullable DateTime fields
            AddNullableDateTimeAttribute(item, "start_time", task.StartTime);
            AddNullableDateTimeAttribute(item, "end_time", task.EndTime);
            AddNullableDateTimeAttribute(item, "timelog_started_on", task.TimelogStartedOn);
            AddNullableDateTimeAttribute(item, "completed_on", task.CompletedOn);
            AddNullableDateTimeAttribute(item, "last_modified_on", task.LastModifiedOn);

            // GSI2: user index — only when OwnerId is populated
            if (task.OwnerId.HasValue)
            {
                item[GSI2_PK] = new AttributeValue { S = "USER#" + task.OwnerId.Value };
                item[GSI2_SK] = new AttributeValue { S = task.CreatedOn.ToString("O") };
            }

            // GSI3: project-task index — extract project ID from LRelatedRecords if available
            string? projectIdStr = ExtractProjectIdFromRelatedRecords(task.LRelatedRecords);
            if (!string.IsNullOrEmpty(projectIdStr))
            {
                item[GSI3_PK] = new AttributeValue { S = PROJECT_PK_PREFIX + projectIdStr };
                item[GSI3_SK] = new AttributeValue { S = TASK_PK_PREFIX + task.Id };
            }

            return item;
        }

        /// <summary>
        /// Deserializes a DynamoDB item dictionary back to a Task domain model.
        /// Handles missing attributes gracefully by returning default values.
        /// </summary>
        private Models.Task DeserializeTask(Dictionary<string, AttributeValue> item)
        {
            return new Models.Task
            {
                Id = GetGuidValue(item, "id"),
                Subject = GetStringValue(item, "subject"),
                Body = GetStringValue(item, "body"),
                Number = GetDecimalValue(item, "number"),
                Key = GetStringValue(item, "key"),
                StatusId = GetGuidValue(item, "status_id"),
                TypeId = GetGuidValue(item, "type_id"),
                Priority = GetStringValue(item, "priority"),
                OwnerId = GetNullableGuidValue(item, "owner_id"),
                StartTime = GetNullableDateTimeValue(item, "start_time"),
                EndTime = GetNullableDateTimeValue(item, "end_time"),
                EstimatedMinutes = GetDecimalValue(item, "estimated_minutes"),
                TimelogStartedOn = GetNullableDateTimeValue(item, "timelog_started_on"),
                CompletedOn = GetNullableDateTimeValue(item, "completed_on"),
                CreatedBy = GetGuidValue(item, "created_by"),
                CreatedOn = GetDateTimeValue(item, "created_on"),
                LastModifiedBy = GetNullableGuidValue(item, "last_modified_by"),
                LastModifiedOn = GetNullableDateTimeValue(item, "last_modified_on"),
                XBillableMinutes = GetDecimalValue(item, "x_billable_minutes"),
                XNonBillableMinutes = GetDecimalValue(item, "x_nonbillable_minutes"),
                RecurrenceId = GetNullableGuidValue(item, "recurrence_id"),
                RecurrenceTemplate = GetStringValue(item, "recurrence_template"),
                ReserveTime = GetBoolValue(item, "reserve_time"),
                LScope = GetStringValue(item, "l_scope"),
                LRelatedRecords = GetStringValue(item, "l_related_records")
            };
        }

        /// <summary>
        /// Serializes a Project domain model to a DynamoDB item dictionary.
        /// </summary>
        private Dictionary<string, AttributeValue> SerializeProject(Models.Project project)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = PROJECT_PK_PREFIX + project.Id },
                ["SK"] = new AttributeValue { S = META_SK },
                ["id"] = new AttributeValue { S = project.Id.ToString() },
                ["name"] = new AttributeValue { S = project.Name ?? string.Empty },
                ["is_billable"] = new AttributeValue { BOOL = project.IsBillable },
                ["created_by"] = new AttributeValue { S = project.CreatedBy.ToString() },
                ["created_on"] = new AttributeValue { S = project.CreatedOn.ToString("O") },

                // GSI1: entity-type index — sorted alphabetically by name
                [GSI1_PK] = new AttributeValue { S = ENTITY_TYPE_PROJECT },
                [GSI1_SK] = new AttributeValue { S = $"{project.Name ?? string.Empty}#{project.Id}" }
            };

            AddStringAttribute(item, "abbr", project.Abbr);
            item["owner_id"] = new AttributeValue { S = project.OwnerId.ToString() };
            AddNullableGuidAttribute(item, "account_id", project.AccountId);

            // GSI2: user index — always populated since OwnerId is non-nullable
            if (project.OwnerId != Guid.Empty)
            {
                item[GSI2_PK] = new AttributeValue { S = "USER#" + project.OwnerId };
                item[GSI2_SK] = new AttributeValue { S = project.CreatedOn.ToString("O") };
            }

            return item;
        }

        /// <summary>
        /// Deserializes a DynamoDB item dictionary back to a Project domain model.
        /// </summary>
        private Models.Project DeserializeProject(Dictionary<string, AttributeValue> item)
        {
            return new Models.Project
            {
                Id = GetGuidValue(item, "id"),
                Name = GetStringValue(item, "name"),
                Abbr = GetStringValue(item, "abbr"),
                OwnerId = GetGuidValue(item, "owner_id"),
                AccountId = GetNullableGuidValue(item, "account_id"),
                IsBillable = GetBoolValue(item, "is_billable"),
                CreatedBy = GetGuidValue(item, "created_by"),
                CreatedOn = GetDateTimeValue(item, "created_on")
            };
        }

        /// <summary>
        /// Serializes a Timelog domain model to a DynamoDB item dictionary.
        /// </summary>
        private Dictionary<string, AttributeValue> SerializeTimelog(Models.Timelog timelog)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = TIMELOG_PK_PREFIX + timelog.Id },
                ["SK"] = new AttributeValue { S = META_SK },
                ["id"] = new AttributeValue { S = timelog.Id.ToString() },
                ["created_by"] = new AttributeValue { S = timelog.CreatedBy.ToString() },
                ["created_on"] = new AttributeValue { S = timelog.CreatedOn.ToString("O") },
                ["logged_on"] = new AttributeValue { S = timelog.LoggedOn.ToString("O") },
                ["minutes"] = new AttributeValue { N = timelog.Minutes.ToString() },
                ["is_billable"] = new AttributeValue { BOOL = timelog.IsBillable },

                // GSI1: entity-type index — sorted by logged_on timestamp
                [GSI1_PK] = new AttributeValue { S = ENTITY_TYPE_TIMELOG },
                [GSI1_SK] = new AttributeValue { S = $"{timelog.LoggedOn:O}#{timelog.Id}" },

                // GSI2: user index — sorted by logged_on timestamp
                [GSI2_PK] = new AttributeValue { S = "USER#" + timelog.CreatedBy },
                [GSI2_SK] = new AttributeValue { S = timelog.LoggedOn.ToString("O") }
            };

            AddStringAttribute(item, "body", timelog.Body);
            AddStringAttribute(item, "l_scope", timelog.LScope);
            AddStringAttribute(item, "l_related_records", timelog.LRelatedRecords);

            return item;
        }

        /// <summary>
        /// Deserializes a DynamoDB item dictionary back to a Timelog domain model.
        /// </summary>
        private Models.Timelog DeserializeTimelog(Dictionary<string, AttributeValue> item)
        {
            return new Models.Timelog
            {
                Id = GetGuidValue(item, "id"),
                CreatedBy = GetGuidValue(item, "created_by"),
                CreatedOn = GetDateTimeValue(item, "created_on"),
                LoggedOn = GetDateTimeValue(item, "logged_on"),
                Minutes = GetIntValue(item, "minutes"),
                IsBillable = GetBoolValue(item, "is_billable"),
                Body = GetStringValue(item, "body"),
                LScope = GetStringValue(item, "l_scope"),
                LRelatedRecords = GetStringValue(item, "l_related_records")
            };
        }

        /// <summary>
        /// Serializes a Comment domain model to a DynamoDB item dictionary.
        /// Uses PARENT#{parentId} for GSI1PK when the comment is a reply (threaded comments).
        /// </summary>
        private Dictionary<string, AttributeValue> SerializeComment(Models.Comment comment)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = COMMENT_PK_PREFIX + comment.Id },
                ["SK"] = new AttributeValue { S = META_SK },
                ["id"] = new AttributeValue { S = comment.Id.ToString() },
                ["created_by"] = new AttributeValue { S = comment.CreatedBy.ToString() },
                ["created_on"] = new AttributeValue { S = comment.CreatedOn.ToString("O") },

                // GSI2: user index
                [GSI2_PK] = new AttributeValue { S = "USER#" + comment.CreatedBy },
                [GSI2_SK] = new AttributeValue { S = comment.CreatedOn.ToString("O") }
            };

            AddStringAttribute(item, "body", comment.Body);
            AddStringAttribute(item, "l_scope", comment.LScope);
            AddStringAttribute(item, "l_related_records", comment.LRelatedRecords);
            AddNullableGuidAttribute(item, "parent_id", comment.ParentId);

            // GSI1: If reply → PARENT#{parentId}, else → ENTITY#comment
            if (comment.ParentId.HasValue)
            {
                item[GSI1_PK] = new AttributeValue { S = "PARENT#" + comment.ParentId.Value };
            }
            else
            {
                item[GSI1_PK] = new AttributeValue { S = ENTITY_TYPE_COMMENT };
            }
            item[GSI1_SK] = new AttributeValue { S = $"{comment.CreatedOn:O}#{comment.Id}" };

            return item;
        }

        /// <summary>
        /// Deserializes a DynamoDB item dictionary back to a Comment domain model.
        /// </summary>
        private Models.Comment DeserializeComment(Dictionary<string, AttributeValue> item)
        {
            return new Models.Comment
            {
                Id = GetGuidValue(item, "id"),
                Body = GetStringValue(item, "body"),
                ParentId = GetNullableGuidValue(item, "parent_id"),
                CreatedBy = GetGuidValue(item, "created_by"),
                CreatedOn = GetDateTimeValue(item, "created_on"),
                LScope = GetStringValue(item, "l_scope"),
                LRelatedRecords = GetStringValue(item, "l_related_records")
            };
        }

        /// <summary>
        /// Serializes a FeedItem domain model to a DynamoDB item dictionary.
        /// </summary>
        private Dictionary<string, AttributeValue> SerializeFeedItem(Models.FeedItem feedItem)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = FEEDITEM_PK_PREFIX + feedItem.Id },
                ["SK"] = new AttributeValue { S = META_SK },
                ["id"] = new AttributeValue { S = feedItem.Id.ToString() },
                ["created_by"] = new AttributeValue { S = feedItem.CreatedBy.ToString() },
                ["created_on"] = new AttributeValue { S = feedItem.CreatedOn.ToString("O") },

                // GSI1: entity-type index — sorted by creation time
                [GSI1_PK] = new AttributeValue { S = ENTITY_TYPE_FEEDITEM },
                [GSI1_SK] = new AttributeValue { S = $"{feedItem.CreatedOn:O}#{feedItem.Id}" },

                // GSI2: user index
                [GSI2_PK] = new AttributeValue { S = "USER#" + feedItem.CreatedBy },
                [GSI2_SK] = new AttributeValue { S = feedItem.CreatedOn.ToString("O") }
            };

            AddStringAttribute(item, "subject", feedItem.Subject);
            AddStringAttribute(item, "body", feedItem.Body);
            AddStringAttribute(item, "type", feedItem.Type);
            AddStringAttribute(item, "l_related_records", feedItem.LRelatedRecords);
            AddStringAttribute(item, "l_scope", feedItem.LScope);

            return item;
        }

        /// <summary>
        /// Deserializes a DynamoDB item dictionary back to a FeedItem domain model.
        /// </summary>
        private Models.FeedItem DeserializeFeedItem(Dictionary<string, AttributeValue> item)
        {
            return new Models.FeedItem
            {
                Id = GetGuidValue(item, "id"),
                Subject = GetStringValue(item, "subject"),
                Body = GetStringValue(item, "body"),
                Type = GetStringValue(item, "type"),
                CreatedBy = GetGuidValue(item, "created_by"),
                CreatedOn = GetDateTimeValue(item, "created_on"),
                LRelatedRecords = GetStringValue(item, "l_related_records"),
                LScope = GetStringValue(item, "l_scope")
            };
        }

        /// <summary>
        /// Serializes a TaskType lookup model to a DynamoDB item dictionary.
        /// GSI1SK is the label for alphabetical listing.
        /// </summary>
        private Dictionary<string, AttributeValue> SerializeTaskType(Models.TaskType taskType)
        {
            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = TASK_TYPE_PK_PREFIX + taskType.Id },
                ["SK"] = new AttributeValue { S = META_SK },
                ["id"] = new AttributeValue { S = taskType.Id.ToString() },
                ["label"] = new AttributeValue { S = taskType.Label ?? string.Empty },

                // GSI1: entity-type index — sorted by label
                [GSI1_PK] = new AttributeValue { S = ENTITY_TYPE_TASK_TYPE },
                [GSI1_SK] = new AttributeValue { S = taskType.Label ?? string.Empty }
            };
        }

        /// <summary>
        /// Deserializes a DynamoDB item dictionary back to a TaskType lookup model.
        /// </summary>
        private Models.TaskType DeserializeTaskType(Dictionary<string, AttributeValue> item)
        {
            return new Models.TaskType
            {
                Id = GetGuidValue(item, "id"),
                Label = GetStringValue(item, "label")
            };
        }

        /// <summary>
        /// Serializes a TaskStatus lookup model to a DynamoDB item dictionary.
        /// GSI1SK is the sort_order, zero-padded to 10 digits for lexicographic correctness.
        /// </summary>
        private Dictionary<string, AttributeValue> SerializeTaskStatus(Models.TaskStatus taskStatus)
        {
            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = TASK_STATUS_PK_PREFIX + taskStatus.Id },
                ["SK"] = new AttributeValue { S = META_SK },
                ["id"] = new AttributeValue { S = taskStatus.Id.ToString() },
                ["label"] = new AttributeValue { S = taskStatus.Label ?? string.Empty },
                ["is_closed"] = new AttributeValue { BOOL = taskStatus.IsClosed },
                ["sort_order"] = new AttributeValue { N = taskStatus.SortOrder.ToString() },

                // GSI1: entity-type index — sorted by sort_order (zero-padded for lexicographic sort)
                [GSI1_PK] = new AttributeValue { S = ENTITY_TYPE_TASK_STATUS },
                [GSI1_SK] = new AttributeValue { S = Math.Max(taskStatus.SortOrder, 0).ToString("D10") }
            };
        }

        /// <summary>
        /// Deserializes a DynamoDB item dictionary back to a TaskStatus lookup model.
        /// </summary>
        private Models.TaskStatus DeserializeTaskStatus(Dictionary<string, AttributeValue> item)
        {
            return new Models.TaskStatus
            {
                Id = GetGuidValue(item, "id"),
                Label = GetStringValue(item, "label"),
                IsClosed = GetBoolValue(item, "is_closed"),
                SortOrder = GetIntValue(item, "sort_order")
            };
        }

        /// <summary>
        /// Extracts a project ID from the LRelatedRecords JSON string. The LRelatedRecords
        /// field is a serialized list of related record IDs. This method attempts to find
        /// the first GUID that could represent a project association.
        /// Returns null if no project reference is found.
        /// </summary>
        private static string? ExtractProjectIdFromRelatedRecords(string? relatedRecords)
        {
            if (string.IsNullOrEmpty(relatedRecords))
                return null;

            try
            {
                // LRelatedRecords is stored as a JSON-serialized string (e.g., a comma-separated
                // list or JSON array of GUIDs). Parse it to extract the first valid GUID.
                // Uses JsonDocument instead of JsonSerializer.Deserialize<T> to avoid AOT/trimmer
                // issues (IL2026/IL3050) since we only need to extract the first element.
                var trimmed = relatedRecords.Trim();
                if (trimmed.StartsWith("["))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                    var arr = doc.RootElement;
                    if (arr.ValueKind == System.Text.Json.JsonValueKind.Array && arr.GetArrayLength() > 0)
                    {
                        return arr[0].GetString();
                    }
                    return null;
                }

                // Fallback: treat as a comma-separated string of IDs
                var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return parts.Length > 0 ? parts[0] : null;
            }
            catch
            {
                // If parsing fails, return the raw string as a best-effort project ID
                return relatedRecords.Trim();
            }
        }
    }
}
