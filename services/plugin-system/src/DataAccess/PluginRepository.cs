using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using WebVellaErp.PluginSystem.Models;

namespace WebVellaErp.PluginSystem.DataAccess
{
    /// <summary>
    /// Defines the contract for DynamoDB-backed plugin persistence operations.
    /// Replaces the monolith's PostgreSQL-based plugin data access:
    /// - ErpPlugin.GetPluginData() / SavePluginData() (ErpPlugin.cs lines 67-115)
    /// - plugin_data table DDL (ERPService.cs lines 1198-1209)
    /// - DbRecordRepository general record persistence patterns
    ///
    /// All operations are async with CancellationToken support, replacing the
    /// synchronous Npgsql patterns from the monolith. Interface enables DI
    /// registration and test mocking.
    /// </summary>
    public interface IPluginRepository
    {
        /// <summary>
        /// Retrieves a plugin by its unique identifier.
        /// DynamoDB key: PK=PLUGIN#{pluginId}, SK=META
        /// </summary>
        /// <param name="pluginId">The plugin's unique identifier (UUID).</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>The Plugin if found; null otherwise.</returns>
        Task<Plugin?> GetPluginByIdAsync(Guid pluginId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a plugin by its unique name.
        /// Replaces: SELECT * FROM plugin_data WHERE name = @name (ErpPlugin.cs line 74)
        /// Uses Scan with filter on name attribute and EntityType = PLUGIN_META.
        /// </summary>
        /// <param name="pluginName">The plugin's unique name.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>The Plugin if found; null otherwise.</returns>
        Task<Plugin?> GetPluginByNameAsync(string pluginName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists all registered plugins in the system.
        /// Paginates through all DynamoDB items with EntityType = PLUGIN_META.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>List of all registered plugins.</returns>
        Task<List<Plugin>> ListPluginsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Queries plugins by their activation status using GSI1.
        /// GSI1PK=STATUS#{status} provides efficient filtering.
        /// </summary>
        /// <param name="status">The plugin status to filter by (Active/Inactive).</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>List of plugins matching the specified status.</returns>
        Task<List<Plugin>> GetPluginsByStatusAsync(PluginStatus status, CancellationToken cancellationToken = default);

        /// <summary>
        /// Registers a new plugin in the system.
        /// Replaces: INSERT INTO plugin_data (id, name, data) VALUES(@id, @name, @data) (ErpPlugin.cs lines 96-103)
        /// Uses PutItem with attribute_not_exists(PK) condition for idempotency.
        /// </summary>
        /// <param name="plugin">The plugin to register.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        Task CreatePluginAsync(Plugin plugin, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing plugin's metadata.
        /// Replaces: UPDATE plugin_data SET data = @data WHERE name = @name (ErpPlugin.cs lines 107-113)
        /// Uses PutItem with attribute_exists(PK) condition to ensure plugin exists.
        /// </summary>
        /// <param name="plugin">The plugin with updated metadata.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        Task UpdatePluginAsync(Plugin plugin, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes a plugin registration and its associated data.
        /// Deletes both the META and DATA items for the plugin.
        /// Idempotent: deleting a non-existent plugin does not throw.
        /// </summary>
        /// <param name="pluginId">The plugin's unique identifier to delete.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        Task DeletePluginAsync(Guid pluginId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves plugin settings/data JSON string by plugin name.
        /// Direct replacement for ErpPlugin.GetPluginData() (ErpPlugin.cs lines 67-85).
        /// DynamoDB key: PK=PLUGIN#{pluginName}, SK=DATA
        /// </summary>
        /// <param name="pluginName">The plugin name to look up data for.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>The JSON data string if found; null otherwise.</returns>
        Task<string?> GetPluginDataAsync(string pluginName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves plugin settings/data as a JSON string.
        /// Direct replacement for ErpPlugin.SavePluginData(string data) (ErpPlugin.cs lines 87-115).
        /// Uses PutItem upsert semantics — naturally handles both INSERT and UPDATE.
        /// DynamoDB key: PK=PLUGIN#{pluginName}, SK=DATA
        /// </summary>
        /// <param name="pluginName">The plugin name to save data for.</param>
        /// <param name="data">The JSON data string to persist.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        Task SavePluginDataAsync(string pluginName, string data, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// DynamoDB single-table design repository for Plugin / Extension System data access.
    ///
    /// Replaces ALL PostgreSQL-based plugin persistence from the monolith:
    /// - ErpPlugin.GetPluginData() / SavePluginData() (Npgsql against plugin_data table)
    /// - DbRecordRepository general record persistence patterns used by plugins
    /// - SdkPlugin._.cs plugin settings read/write via GetPluginData()/SavePluginData()
    /// - ERPService.cs plugin_data table DDL: id UUID PK, name TEXT UNIQUE NOT NULL, data TEXT
    ///
    /// DynamoDB Key Patterns:
    ///   PK=PLUGIN#{pluginId} / SK=META        — Plugin definitions (name, version, status, etc.)
    ///   PK=PLUGIN#{pluginName} / SK=DATA       — Plugin settings/data (replaces plugin_data table)
    ///   GSI1PK=STATUS#{status} / GSI1SK=NAME#{name} — Status-based queries
    ///
    /// All operations are async with CancellationToken, idempotent writes, and structured
    /// JSON logging with correlation-ID propagation per AAP Section 0.8.5.
    /// </summary>
    public class PluginRepository : IPluginRepository
    {
        #region Constants — DynamoDB Single-Table Design

        /// <summary>
        /// Environment variable name for the DynamoDB table name.
        /// Allows different table names per environment (LocalStack, production).
        /// </summary>
        private const string TABLE_NAME_ENV = "PLUGIN_SYSTEM_TABLE_NAME";

        /// <summary>
        /// Default table name fallback when the environment variable is not set.
        /// </summary>
        private const string DEFAULT_TABLE_NAME = "plugin-system";

        /// <summary>Partition key attribute name.</summary>
        private const string PK = "PK";

        /// <summary>Sort key attribute name.</summary>
        private const string SK = "SK";

        /// <summary>GSI1 partition key attribute for status-based queries.</summary>
        private const string GSI1_PK = "GSI1PK";

        /// <summary>GSI1 sort key attribute for status-based queries.</summary>
        private const string GSI1_SK = "GSI1SK";

        /// <summary>GSI1 index name for querying plugins by status.</summary>
        private const string GSI1_NAME = "GSI1";

        /// <summary>Type discriminator attribute for single-table design.</summary>
        private const string ENTITY_TYPE_ATTR = "EntityType";

        /// <summary>Entity type value for plugin definition items.</summary>
        private const string ENTITY_TYPE_PLUGIN = "PLUGIN_META";

        /// <summary>Entity type value for plugin data/settings items.</summary>
        private const string ENTITY_TYPE_PLUGIN_DATA = "PLUGIN_DATA";

        /// <summary>Sort key value for plugin metadata items.</summary>
        private const string SK_META = "META";

        /// <summary>Sort key value for plugin data items.</summary>
        private const string SK_DATA = "DATA";

        /// <summary>Key prefix for plugin partition keys.</summary>
        private const string PK_PREFIX_PLUGIN = "PLUGIN#";

        /// <summary>Key prefix for status GSI partition keys.</summary>
        private const string GSI1_PK_PREFIX_STATUS = "STATUS#";

        /// <summary>Key prefix for name GSI sort keys.</summary>
        private const string GSI1_SK_PREFIX_NAME = "NAME#";

        #endregion

        #region DynamoDB Attribute Names

        private const string ATTR_ID = "id";
        private const string ATTR_NAME = "name";
        private const string ATTR_PREFIX = "prefix";
        private const string ATTR_URL = "url";
        private const string ATTR_DESCRIPTION = "description";
        private const string ATTR_VERSION = "version";
        private const string ATTR_COMPANY = "company";
        private const string ATTR_COMPANY_URL = "company_url";
        private const string ATTR_AUTHOR = "author";
        private const string ATTR_REPOSITORY = "repository";
        private const string ATTR_LICENSE = "license";
        private const string ATTR_SETTINGS_URL = "settings_url";
        private const string ATTR_PLUGIN_PAGE_URL = "plugin_page_url";
        private const string ATTR_ICON_URL = "icon_url";
        private const string ATTR_STATUS = "status";
        private const string ATTR_CREATED_AT = "created_at";
        private const string ATTR_UPDATED_AT = "updated_at";
        private const string ATTR_DATA = "data";
        private const string ATTR_PLUGIN_NAME = "plugin_name";

        #endregion

        #region Private Fields

        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly ILogger<PluginRepository> _logger;
        private readonly string _tableName;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of <see cref="PluginRepository"/>.
        /// Replaces the monolith's DbContext.Current.CreateConnection() ambient singleton pattern.
        /// The IAmazonDynamoDB client is injected via DI and automatically respects AWS_ENDPOINT_URL
        /// for LocalStack compatibility (http://localhost:4566).
        /// </summary>
        /// <param name="dynamoDbClient">DynamoDB client configured via DI (replaces Npgsql connections).</param>
        /// <param name="logger">Structured logger for correlation-ID propagation (AAP Section 0.8.5).</param>
        /// <exception cref="ArgumentNullException">Thrown when dynamoDbClient or logger is null.</exception>
        public PluginRepository(IAmazonDynamoDB dynamoDbClient, ILogger<PluginRepository> logger)
        {
            _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tableName = Environment.GetEnvironmentVariable(TABLE_NAME_ENV) ?? DEFAULT_TABLE_NAME;

            _logger.LogInformation(
                "PluginRepository initialized with table name: {TableName}",
                _tableName);
        }

        #endregion

        #region Plugin Registry CRUD Operations

        /// <inheritdoc />
        public async Task<Plugin?> GetPluginByIdAsync(Guid pluginId, CancellationToken cancellationToken = default)
        {
            if (pluginId == Guid.Empty)
            {
                _logger.LogWarning("GetPluginByIdAsync called with empty plugin ID");
                return null;
            }

            try
            {
                var request = new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { PK, new AttributeValue { S = $"{PK_PREFIX_PLUGIN}{pluginId}" } },
                        { SK, new AttributeValue { S = SK_META } }
                    }
                };

                var response = await _dynamoDbClient.GetItemAsync(request, cancellationToken);

                if (response.Item == null || response.Item.Count == 0)
                {
                    _logger.LogWarning("Plugin not found with ID: {PluginId}", pluginId);
                    return null;
                }

                var plugin = MapToPlugin(response.Item);
                _logger.LogInformation("Successfully retrieved plugin by ID: {PluginId}, Name: {PluginName}", pluginId, plugin.Name);
                return plugin;
            }
            catch (ResourceNotFoundException ex)
            {
                _logger.LogError(ex, "DynamoDB table '{TableName}' not found while getting plugin by ID: {PluginId}", _tableName, pluginId);
                throw;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while getting plugin by ID: {PluginId}", pluginId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<Plugin?> GetPluginByNameAsync(string pluginName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pluginName))
            {
                _logger.LogWarning("GetPluginByNameAsync called with null or empty plugin name");
                return null;
            }

            try
            {
                // Use Scan with filter to find plugin by name.
                // The monolith used: SELECT * FROM plugin_data WHERE name = @name (ErpPlugin.cs line 74)
                // In DynamoDB, we scan with a filter expression since name is not the primary partition key
                // for META items (PK=PLUGIN#{pluginId}). GSI1SK=NAME#{name} can also be queried.
                var request = new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = $"{ATTR_NAME} = :pluginName AND {ENTITY_TYPE_ATTR} = :entityType",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":pluginName", new AttributeValue { S = pluginName } },
                        { ":entityType", new AttributeValue { S = ENTITY_TYPE_PLUGIN } }
                    }
                };

                var plugins = new List<Plugin>();
                ScanResponse response;

                do
                {
                    response = await _dynamoDbClient.ScanAsync(request, cancellationToken);

                    foreach (var item in response.Items)
                    {
                        plugins.Add(MapToPlugin(item));
                    }

                    request.ExclusiveStartKey = response.LastEvaluatedKey;
                }
                while (response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0);

                if (plugins.Count == 0)
                {
                    _logger.LogWarning("Plugin not found with name: {PluginName}", pluginName);
                    return null;
                }

                // The monolith enforced uniqueness via idx_u_plugin_data_name UNIQUE constraint.
                // Return the first match (should be unique due to CreatePluginAsync conditional write).
                var result = plugins[0];
                _logger.LogInformation("Successfully retrieved plugin by name: {PluginName}, ID: {PluginId}", pluginName, result.Id);
                return result;
            }
            catch (ResourceNotFoundException ex)
            {
                _logger.LogError(ex, "DynamoDB table '{TableName}' not found while getting plugin by name: {PluginName}", _tableName, pluginName);
                throw;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while getting plugin by name: {PluginName}", pluginName);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Plugin>> ListPluginsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = $"{ENTITY_TYPE_ATTR} = :entityType",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":entityType", new AttributeValue { S = ENTITY_TYPE_PLUGIN } }
                    }
                };

                var plugins = new List<Plugin>();
                ScanResponse response;

                // Paginate through all results using LastEvaluatedKey
                do
                {
                    response = await _dynamoDbClient.ScanAsync(request, cancellationToken);

                    foreach (var item in response.Items)
                    {
                        plugins.Add(MapToPlugin(item));
                    }

                    request.ExclusiveStartKey = response.LastEvaluatedKey;
                }
                while (response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0);

                _logger.LogInformation("Successfully listed {Count} plugins", plugins.Count);
                return plugins;
            }
            catch (ResourceNotFoundException ex)
            {
                _logger.LogError(ex, "DynamoDB table '{TableName}' not found while listing plugins", _tableName);
                throw;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while listing plugins");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<Plugin>> GetPluginsByStatusAsync(PluginStatus status, CancellationToken cancellationToken = default)
        {
            try
            {
                var statusKey = $"{GSI1_PK_PREFIX_STATUS}{status}";

                var request = new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = GSI1_NAME,
                    KeyConditionExpression = $"{GSI1_PK} = :statusKey",
                    FilterExpression = $"{ENTITY_TYPE_ATTR} = :entityType",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":statusKey", new AttributeValue { S = statusKey } },
                        { ":entityType", new AttributeValue { S = ENTITY_TYPE_PLUGIN } }
                    }
                };

                var plugins = new List<Plugin>();
                QueryResponse response;

                // Paginate through all GSI1 query results
                do
                {
                    response = await _dynamoDbClient.QueryAsync(request, cancellationToken);

                    foreach (var item in response.Items)
                    {
                        plugins.Add(MapToPlugin(item));
                    }

                    request.ExclusiveStartKey = response.LastEvaluatedKey;
                }
                while (response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0);

                _logger.LogInformation(
                    "Successfully retrieved {Count} plugins with status: {Status}",
                    plugins.Count,
                    status);

                return plugins;
            }
            catch (ResourceNotFoundException ex)
            {
                _logger.LogError(ex, "DynamoDB table or index '{TableName}/{IndexName}' not found while querying plugins by status: {Status}", _tableName, GSI1_NAME, status);
                throw;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while querying plugins by status: {Status}", status);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task CreatePluginAsync(Plugin plugin, CancellationToken cancellationToken = default)
        {
            if (plugin == null)
            {
                throw new ArgumentNullException(nameof(plugin));
            }

            // Validation matching ErpPlugin.cs lines 69-70 pattern
            if (plugin.Id == Guid.Empty)
            {
                throw new ArgumentException("Plugin ID must not be empty.", nameof(plugin));
            }

            if (string.IsNullOrWhiteSpace(plugin.Name))
            {
                throw new ArgumentException(
                    "Plugin name is not specified while trying to create plugin registration",
                    nameof(plugin));
            }

            try
            {
                var item = MapFromPlugin(plugin);

                var request = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item,
                    // Idempotent: prevent overwriting existing plugin (AAP Section 0.8.5)
                    ConditionExpression = $"attribute_not_exists({PK})"
                };

                await _dynamoDbClient.PutItemAsync(request, cancellationToken);

                _logger.LogInformation(
                    "Successfully created plugin: {PluginName} (ID: {PluginId})",
                    plugin.Name,
                    plugin.Id);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning(
                    "Plugin already exists with ID: {PluginId}, Name: {PluginName}. Create operation is idempotent — no overwrite performed.",
                    plugin.Id,
                    plugin.Name);

                throw new InvalidOperationException(
                    $"A plugin with ID '{plugin.Id}' already exists. Use UpdatePluginAsync to modify existing plugins.");
            }
            catch (ResourceNotFoundException ex)
            {
                _logger.LogError(ex, "DynamoDB table '{TableName}' not found while creating plugin: {PluginName}", _tableName, plugin.Name);
                throw;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while creating plugin: {PluginName} (ID: {PluginId})", plugin.Name, plugin.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task UpdatePluginAsync(Plugin plugin, CancellationToken cancellationToken = default)
        {
            if (plugin == null)
            {
                throw new ArgumentNullException(nameof(plugin));
            }

            if (plugin.Id == Guid.Empty)
            {
                throw new ArgumentException("Plugin ID must not be empty.", nameof(plugin));
            }

            if (string.IsNullOrWhiteSpace(plugin.Name))
            {
                throw new ArgumentException(
                    "Plugin name is not specified while trying to update plugin registration",
                    nameof(plugin));
            }

            try
            {
                // Set updated timestamp
                plugin.UpdatedAt = DateTime.UtcNow;

                var item = MapFromPlugin(plugin);

                var request = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item,
                    // Ensure plugin exists before updating (idempotent per AAP Section 0.8.5)
                    ConditionExpression = $"attribute_exists({PK})"
                };

                await _dynamoDbClient.PutItemAsync(request, cancellationToken);

                _logger.LogInformation(
                    "Successfully updated plugin: {PluginName} (ID: {PluginId})",
                    plugin.Name,
                    plugin.Id);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning(
                    "Plugin not found for update with ID: {PluginId}, Name: {PluginName}",
                    plugin.Id,
                    plugin.Name);

                throw new InvalidOperationException(
                    $"Plugin with ID '{plugin.Id}' does not exist. Use CreatePluginAsync to register new plugins.");
            }
            catch (ResourceNotFoundException ex)
            {
                _logger.LogError(ex, "DynamoDB table '{TableName}' not found while updating plugin: {PluginName}", _tableName, plugin.Name);
                throw;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while updating plugin: {PluginName} (ID: {PluginId})", plugin.Name, plugin.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeletePluginAsync(Guid pluginId, CancellationToken cancellationToken = default)
        {
            if (pluginId == Guid.Empty)
            {
                throw new ArgumentException("Plugin ID must not be empty.", nameof(pluginId));
            }

            try
            {
                // First, retrieve the plugin to get its name for data item cleanup
                var plugin = await GetPluginByIdAsync(pluginId, cancellationToken);

                if (plugin == null)
                {
                    // Idempotent: deleting a non-existent plugin is a no-op (AAP Section 0.8.5)
                    _logger.LogWarning(
                        "Plugin not found for deletion with ID: {PluginId}. Operation is idempotent — no action taken.",
                        pluginId);
                    return;
                }

                // Build batch of items to delete: META item + DATA item (if exists)
                var writeRequests = new List<WriteRequest>
                {
                    new WriteRequest
                    {
                        DeleteRequest = new DeleteRequest
                        {
                            Key = new Dictionary<string, AttributeValue>
                            {
                                { PK, new AttributeValue { S = $"{PK_PREFIX_PLUGIN}{pluginId}" } },
                                { SK, new AttributeValue { S = SK_META } }
                            }
                        }
                    }
                };

                // Also delete the plugin data item if it exists (PK=PLUGIN#{pluginName}, SK=DATA)
                if (!string.IsNullOrWhiteSpace(plugin.Name))
                {
                    writeRequests.Add(new WriteRequest
                    {
                        DeleteRequest = new DeleteRequest
                        {
                            Key = new Dictionary<string, AttributeValue>
                            {
                                { PK, new AttributeValue { S = $"{PK_PREFIX_PLUGIN}{plugin.Name}" } },
                                { SK, new AttributeValue { S = SK_DATA } }
                            }
                        }
                    });
                }

                var batchRequest = new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>>
                    {
                        { _tableName, writeRequests }
                    }
                };

                await _dynamoDbClient.BatchWriteItemAsync(batchRequest, cancellationToken);

                _logger.LogInformation(
                    "Successfully deleted plugin: {PluginName} (ID: {PluginId}) and its associated data",
                    plugin.Name,
                    pluginId);
            }
            catch (ResourceNotFoundException ex)
            {
                _logger.LogError(ex, "DynamoDB table '{TableName}' not found while deleting plugin: {PluginId}", _tableName, pluginId);
                throw;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while deleting plugin: {PluginId}", pluginId);
                throw;
            }
        }

        #endregion

        #region Plugin Data Operations

        /// <inheritdoc />
        /// <remarks>
        /// Direct replacement for ErpPlugin.GetPluginData() (ErpPlugin.cs lines 67-85).
        /// Source pattern:
        ///   using (var connection = DbContext.Current.CreateConnection())
        ///   {
        ///       var cmd = connection.CreateCommand("SELECT * FROM plugin_data WHERE name = @name");
        ///       cmd.Parameters.Add(new NpgsqlParameter("@name", Name));
        ///       DataTable dt = new DataTable();
        ///       new NpgsqlDataAdapter(cmd).Fill(dt);
        ///       if (dt.Rows.Count == 0) return null;
        ///       return (string)dt.Rows[0]["data"];
        ///   }
        ///
        /// DynamoDB translation: GetItem with key PK=PLUGIN#{pluginName}, SK=DATA
        /// Returns the "data" attribute as string, or null if item doesn't exist.
        /// </remarks>
        public async Task<string?> GetPluginDataAsync(string pluginName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pluginName))
            {
                // Matching validation from ErpPlugin.cs line 69-70:
                // "Plugin name is not specified while trying to load plugin data"
                throw new ArgumentException(
                    "Plugin name is not specified while trying to load plugin data",
                    nameof(pluginName));
            }

            try
            {
                var request = new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { PK, new AttributeValue { S = $"{PK_PREFIX_PLUGIN}{pluginName}" } },
                        { SK, new AttributeValue { S = SK_DATA } }
                    }
                };

                var response = await _dynamoDbClient.GetItemAsync(request, cancellationToken);

                if (response.Item == null || response.Item.Count == 0)
                {
                    _logger.LogWarning("Plugin data not found for plugin: {PluginName}", pluginName);
                    return null;
                }

                var data = GetStringOrDefault(response.Item, ATTR_DATA, string.Empty);

                if (string.IsNullOrEmpty(data))
                {
                    _logger.LogWarning("Plugin data is empty for plugin: {PluginName}", pluginName);
                    return null;
                }

                _logger.LogInformation(
                    "Successfully retrieved plugin data for: {PluginName} (length: {DataLength})",
                    pluginName,
                    data.Length);

                return data;
            }
            catch (ResourceNotFoundException ex)
            {
                _logger.LogError(ex, "DynamoDB table '{TableName}' not found while getting plugin data: {PluginName}", _tableName, pluginName);
                throw;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while getting plugin data: {PluginName}", pluginName);
                throw;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Direct replacement for ErpPlugin.SavePluginData(string data) (ErpPlugin.cs lines 87-115).
        /// Source pattern:
        ///   bool pluginDataExists = GetPluginData() != null;
        ///   if (!pluginDataExists)
        ///       INSERT INTO plugin_data (id, name, data) VALUES(@id, @name, @data)
        ///   else
        ///       UPDATE plugin_data SET data = @data WHERE name = @name
        ///
        /// DynamoDB translation: PutItem (upsert) with key PK=PLUGIN#{pluginName}, SK=DATA
        /// PutItem natively replaces the entire item, making it simpler than the source's INSERT-or-UPDATE pattern.
        /// Naturally idempotent per AAP Section 0.8.5.
        ///
        /// This data is consumed by all plugin patch files (SdkPlugin, CrmPlugin, NextPlugin,
        /// ProjectPlugin, MailPlugin, MicrosoftCDM) which call:
        ///   string jsonData = GetPluginData();
        ///   currentPluginSettings = JsonConvert.DeserializeObject&lt;PluginSettings&gt;(jsonData);
        ///   SavePluginData(JsonConvert.SerializeObject(currentPluginSettings));
        /// </remarks>
        public async Task SavePluginDataAsync(string pluginName, string data, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pluginName))
            {
                // Matching validation from ErpPlugin.cs line 89-90:
                // "Plugin name is not specified while trying to load plugin data"
                throw new ArgumentException(
                    "Plugin name is not specified while trying to save plugin data",
                    nameof(pluginName));
            }

            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            try
            {
                var item = new Dictionary<string, AttributeValue>
                {
                    { PK, new AttributeValue { S = $"{PK_PREFIX_PLUGIN}{pluginName}" } },
                    { SK, new AttributeValue { S = SK_DATA } },
                    { ENTITY_TYPE_ATTR, new AttributeValue { S = ENTITY_TYPE_PLUGIN_DATA } },
                    { ATTR_PLUGIN_NAME, new AttributeValue { S = pluginName } },
                    { ATTR_DATA, new AttributeValue { S = data } },
                    { ATTR_UPDATED_AT, new AttributeValue { S = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) } }
                };

                var request = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item
                    // No condition expression — PutItem is a natural upsert (idempotent)
                };

                await _dynamoDbClient.PutItemAsync(request, cancellationToken);

                _logger.LogInformation(
                    "Successfully saved plugin data for: {PluginName} (length: {DataLength})",
                    pluginName,
                    data.Length);
            }
            catch (ResourceNotFoundException ex)
            {
                _logger.LogError(ex, "DynamoDB table '{TableName}' not found while saving plugin data: {PluginName}", _tableName, pluginName);
                throw;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error while saving plugin data: {PluginName}", pluginName);
                throw;
            }
        }

        #endregion

        #region Helper Methods — Attribute Mapping

        /// <summary>
        /// Converts a DynamoDB item (attribute map) to a Plugin domain model.
        /// Maps all 17 properties from DynamoDB AttributeValue dictionaries:
        /// the 13 original properties from ErpPlugin.cs (name, prefix, url, description,
        /// version, company, company_url, author, repository, license, settings_url,
        /// plugin_page_url, icon_url) plus 4 new microservice properties (id, status,
        /// created_at, updated_at).
        /// </summary>
        /// <param name="item">DynamoDB item attribute map.</param>
        /// <returns>Fully populated Plugin model.</returns>
        private Plugin MapToPlugin(Dictionary<string, AttributeValue> item)
        {
            return new Plugin
            {
                Id = Guid.TryParse(GetStringOrDefault(item, ATTR_ID), out var id) ? id : Guid.Empty,
                Name = GetStringOrDefault(item, ATTR_NAME),
                Prefix = GetStringOrDefault(item, ATTR_PREFIX),
                Url = GetStringOrDefault(item, ATTR_URL),
                Description = GetStringOrDefault(item, ATTR_DESCRIPTION),
                Version = GetIntOrDefault(item, ATTR_VERSION),
                Company = GetStringOrDefault(item, ATTR_COMPANY),
                CompanyUrl = GetStringOrDefault(item, ATTR_COMPANY_URL),
                Author = GetStringOrDefault(item, ATTR_AUTHOR),
                Repository = GetStringOrDefault(item, ATTR_REPOSITORY),
                License = GetStringOrDefault(item, ATTR_LICENSE),
                SettingsUrl = GetStringOrDefault(item, ATTR_SETTINGS_URL),
                PluginPageUrl = GetStringOrDefault(item, ATTR_PLUGIN_PAGE_URL),
                IconUrl = GetStringOrDefault(item, ATTR_ICON_URL),
                Status = ParsePluginStatus(item, ATTR_STATUS),
                CreatedAt = ParseDateTimeOrDefault(item, ATTR_CREATED_AT),
                UpdatedAt = ParseDateTimeOrDefault(item, ATTR_UPDATED_AT)
            };
        }

        /// <summary>
        /// Converts a Plugin domain model to a DynamoDB item (attribute map).
        /// Builds all attribute values including PK, SK, GSI1PK, GSI1SK, and EntityType
        /// for the single-table design pattern. String properties use S type, version uses
        /// N type, DateTime values use S type (ISO 8601).
        /// </summary>
        /// <param name="plugin">Plugin model to convert.</param>
        /// <returns>DynamoDB item attribute map ready for PutItem.</returns>
        private Dictionary<string, AttributeValue> MapFromPlugin(Plugin plugin)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                // Primary key: PK=PLUGIN#{pluginId}, SK=META
                { PK, new AttributeValue { S = $"{PK_PREFIX_PLUGIN}{plugin.Id}" } },
                { SK, new AttributeValue { S = SK_META } },

                // GSI1 keys for status-based queries: GSI1PK=STATUS#{status}, GSI1SK=NAME#{name}
                { GSI1_PK, new AttributeValue { S = $"{GSI1_PK_PREFIX_STATUS}{plugin.Status}" } },
                { GSI1_SK, new AttributeValue { S = $"{GSI1_SK_PREFIX_NAME}{plugin.Name}" } },

                // Type discriminator for single-table design
                { ENTITY_TYPE_ATTR, new AttributeValue { S = ENTITY_TYPE_PLUGIN } },

                // All 13 original ErpPlugin.cs properties (lines 14-51)
                { ATTR_ID, new AttributeValue { S = plugin.Id.ToString() } },
                { ATTR_NAME, new AttributeValue { S = plugin.Name ?? string.Empty } },
                { ATTR_PREFIX, new AttributeValue { S = plugin.Prefix ?? string.Empty } },
                { ATTR_URL, new AttributeValue { S = plugin.Url ?? string.Empty } },
                { ATTR_DESCRIPTION, new AttributeValue { S = plugin.Description ?? string.Empty } },
                { ATTR_VERSION, new AttributeValue { N = plugin.Version.ToString(CultureInfo.InvariantCulture) } },
                { ATTR_COMPANY, new AttributeValue { S = plugin.Company ?? string.Empty } },
                { ATTR_COMPANY_URL, new AttributeValue { S = plugin.CompanyUrl ?? string.Empty } },
                { ATTR_AUTHOR, new AttributeValue { S = plugin.Author ?? string.Empty } },
                { ATTR_REPOSITORY, new AttributeValue { S = plugin.Repository ?? string.Empty } },
                { ATTR_LICENSE, new AttributeValue { S = plugin.License ?? string.Empty } },
                { ATTR_SETTINGS_URL, new AttributeValue { S = plugin.SettingsUrl ?? string.Empty } },
                { ATTR_PLUGIN_PAGE_URL, new AttributeValue { S = plugin.PluginPageUrl ?? string.Empty } },
                { ATTR_ICON_URL, new AttributeValue { S = plugin.IconUrl ?? string.Empty } },

                // 4 new microservice properties
                { ATTR_STATUS, new AttributeValue { S = plugin.Status.ToString() } },
                { ATTR_CREATED_AT, new AttributeValue { S = plugin.CreatedAt.ToString("o", CultureInfo.InvariantCulture) } },
                { ATTR_UPDATED_AT, new AttributeValue { S = plugin.UpdatedAt.ToString("o", CultureInfo.InvariantCulture) } }
            };

            return item;
        }

        /// <summary>
        /// Safely extracts a string attribute value from a DynamoDB item.
        /// Returns the default value if the key is missing or the attribute has no S value.
        /// </summary>
        /// <param name="item">DynamoDB item attribute map.</param>
        /// <param name="key">Attribute name to extract.</param>
        /// <param name="defaultValue">Default value when attribute is missing (defaults to empty string).</param>
        /// <returns>The string attribute value or the default.</returns>
        private static string GetStringOrDefault(Dictionary<string, AttributeValue> item, string key, string defaultValue = "")
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null)
            {
                return attr.S;
            }

            return defaultValue;
        }

        /// <summary>
        /// Safely extracts an integer attribute value from a DynamoDB item.
        /// DynamoDB stores numbers as N type (string representation of number).
        /// Returns the default value if the key is missing or parsing fails.
        /// </summary>
        /// <param name="item">DynamoDB item attribute map.</param>
        /// <param name="key">Attribute name to extract.</param>
        /// <param name="defaultValue">Default value when attribute is missing (defaults to 0).</param>
        /// <returns>The integer attribute value or the default.</returns>
        private static int GetIntOrDefault(Dictionary<string, AttributeValue> item, string key, int defaultValue = 0)
        {
            if (item.TryGetValue(key, out var attr) && attr.N != null)
            {
                if (int.TryParse(attr.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
                {
                    return result;
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// Parses a DateTime from an ISO 8601 string attribute in a DynamoDB item.
        /// Returns DateTime.UtcNow as fallback if the key is missing or parsing fails.
        /// All dates are stored and returned as UTC (ISO 8601 "o" format).
        /// </summary>
        /// <param name="item">DynamoDB item attribute map.</param>
        /// <param name="key">Attribute name to parse.</param>
        /// <returns>The parsed DateTime (UTC) or DateTime.UtcNow as fallback.</returns>
        private static DateTime ParseDateTimeOrDefault(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null)
            {
                if (DateTime.TryParse(attr.S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
                {
                    return result.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(result, DateTimeKind.Utc)
                        : result.ToUniversalTime();
                }
            }

            return DateTime.UtcNow;
        }

        /// <summary>
        /// Parses a PluginStatus enum from a string attribute in a DynamoDB item.
        /// Returns PluginStatus.Active as the default fallback, matching the Plugin
        /// model constructor default and the monolith's implicit active-by-default behavior.
        /// </summary>
        /// <param name="item">DynamoDB item attribute map.</param>
        /// <param name="key">Attribute name to parse.</param>
        /// <returns>The parsed PluginStatus or PluginStatus.Active as fallback.</returns>
        private static PluginStatus ParsePluginStatus(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null)
            {
                if (Enum.TryParse<PluginStatus>(attr.S, ignoreCase: true, out var result))
                {
                    return result;
                }
            }

            return PluginStatus.Active;
        }

        #endregion
    }
}
