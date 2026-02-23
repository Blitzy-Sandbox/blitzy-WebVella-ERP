using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using WebVellaErp.Notifications.Models;

namespace WebVellaErp.Notifications.DataAccess
{
    /// <summary>
    /// Source-generated JSON serializer context for Native AOT compatibility.
    /// Eliminates IL2026/IL3050 trimming warnings for System.Text.Json.
    /// </summary>
    [JsonSerializable(typeof(EmailAddress))]
    [JsonSerializable(typeof(List<EmailAddress>))]
    [JsonSerializable(typeof(List<string>))]
    internal partial class NotificationJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Repository interface for the Notifications microservice data access layer.
    /// Defines all DynamoDB operations for email, SMTP service config, and webhook management.
    /// All methods are async with CancellationToken support per AAP §0.8.5.
    /// </summary>
    public interface INotificationRepository
    {
        // ── Email Operations ──

        /// <summary>
        /// Upserts an email record in DynamoDB. Maps to SmtpInternalService.SaveEmail (source lines 500-513).
        /// Uses PutItem which is an inherent upsert, replacing the source check-then-create-or-update pattern.
        /// </summary>
        Task SaveEmailAsync(Email email, CancellationToken ct = default);

        /// <summary>
        /// Retrieves an email by its unique identifier. Maps to SmtpInternalService.GetEmail (source lines 674-681).
        /// Returns null if the email is not found, matching the source behavior.
        /// </summary>
        Task<Email?> GetEmailByIdAsync(Guid emailId, CancellationToken ct = default);

        /// <summary>
        /// Retrieves pending emails for queue processing with priority DESC, scheduled_on ASC ordering.
        /// Maps to SmtpInternalService.ProcessSmtpQueue (source lines 846-849):
        /// SELECT * FROM email WHERE status=Pending AND scheduled_on IS NOT NULL AND scheduled_on &lt; UTC_NOW
        /// ORDER BY priority DESC, scheduled_on ASC PAGE 1 PAGESIZE {pageSize}
        /// </summary>
        Task<List<Email>> GetPendingEmailsAsync(int pageSize = 10, CancellationToken ct = default);

        /// <summary>
        /// Deletes an email record by its unique identifier.
        /// </summary>
        Task DeleteEmailAsync(Guid emailId, CancellationToken ct = default);

        /// <summary>
        /// Retrieves emails filtered by delivery status using GSI1.
        /// </summary>
        Task<List<Email>> GetEmailsByStatusAsync(EmailStatus status, int limit = 50, CancellationToken ct = default);

        // ── SMTP Service Config Operations ──

        /// <summary>
        /// Retrieves an SMTP service config by ID with read-through caching.
        /// Maps to EmailServiceManager.GetSmtpService(Guid id) (source lines 53-64).
        /// </summary>
        Task<SmtpServiceConfig?> GetSmtpServiceByIdAsync(Guid serviceId, CancellationToken ct = default);

        /// <summary>
        /// Retrieves an SMTP service config by name with read-through caching via GSI2.
        /// Maps to EmailServiceManager.GetSmtpService(string name) (source lines 66-77).
        /// </summary>
        Task<SmtpServiceConfig?> GetSmtpServiceByNameAsync(string name, CancellationToken ct = default);

        /// <summary>
        /// Retrieves the default SMTP service config with caching.
        /// Maps to EmailServiceManager.GetSmtpServiceInternal(name=null) (source lines 91-99).
        /// Enforces the exactly-one-default invariant.
        /// </summary>
        Task<SmtpServiceConfig?> GetDefaultSmtpServiceAsync(CancellationToken ct = default);

        /// <summary>
        /// Creates or updates an SMTP service configuration. Handles default service invariant:
        /// when IsDefault is true, unsets previous default. Clears SMTP cache after mutation.
        /// </summary>
        Task SaveSmtpServiceAsync(SmtpServiceConfig service, CancellationToken ct = default);

        /// <summary>
        /// Deletes an SMTP service configuration. Prevents deletion of the default service
        /// per SmtpServiceRecordHook.OnPreDeleteRecord (source line 47).
        /// </summary>
        Task DeleteSmtpServiceAsync(Guid serviceId, CancellationToken ct = default);

        /// <summary>
        /// Retrieves all SMTP service configurations.
        /// </summary>
        Task<List<SmtpServiceConfig>> GetAllSmtpServicesAsync(CancellationToken ct = default);

        /// <summary>
        /// Clears the SMTP service config cache. Maps to EmailServiceManager.ClearCache() (source lines 30-33).
        /// Called after any SMTP service mutation per SmtpServiceRecordHook pattern.
        /// </summary>
        void ClearSmtpServiceCache();

        // ── Webhook Config Operations ──

        /// <summary>
        /// Creates or updates a webhook configuration.
        /// </summary>
        Task SaveWebhookConfigAsync(WebhookConfig config, CancellationToken ct = default);

        /// <summary>
        /// Retrieves a webhook configuration by its unique identifier.
        /// </summary>
        Task<WebhookConfig?> GetWebhookConfigByIdAsync(Guid webhookId, CancellationToken ct = default);

        /// <summary>
        /// Retrieves all active (enabled) webhook configurations for the specified channel.
        /// </summary>
        Task<List<WebhookConfig>> GetActiveWebhooksByChannelAsync(string channel, CancellationToken ct = default);

        /// <summary>
        /// Deletes a webhook configuration by its unique identifier.
        /// </summary>
        Task DeleteWebhookConfigAsync(Guid webhookId, CancellationToken ct = default);
    }

    /// <summary>
    /// DynamoDB single-table design repository for the Notifications microservice.
    /// Replaces the monolith's PostgreSQL-backed EmailServiceManager, SmtpInternalService,
    /// and SmtpServiceRecordHook with DynamoDB operations.
    ///
    /// Key patterns:
    ///   PK=EMAIL#{emailId}, SK=META              — email records (17 attributes)
    ///   PK=SMTP_SERVICE#{serviceId}, SK=META      — SMTP service configs (14 attributes)
    ///   PK=SMTP_SERVICE#DEFAULT, SK=META          — default service pointer
    ///   PK=WEBHOOK#{webhookId}, SK=META           — webhook configurations (7 attributes)
    ///
    /// GSI1 (queue processing):
    ///   GSI1PK=STATUS#{status}, GSI1SK=PRIORITY#{9999-priority}#SCHEDULED#{iso8601}
    ///
    /// GSI2 (SMTP service name lookup):
    ///   GSI2PK=SMTP_SERVICE#{name}, GSI2SK=META
    /// </summary>
    public class NotificationRepository : INotificationRepository
    {
        #region ── Constants ──

        private const string PK = "PK";
        private const string SK = "SK";
        private const string GSI1_INDEX_NAME = "GSI1";
        private const string GSI1_PK = "GSI1PK";
        private const string GSI1_SK = "GSI1SK";
        private const string GSI2_INDEX_NAME = "GSI2";
        private const string GSI2_PK = "GSI2PK";
        private const string GSI2_SK = "GSI2SK";
        private const string ENTITY_TYPE_ATTR = "EntityType";

        private const string ENTITY_TYPE_EMAIL = "EMAIL";
        private const string ENTITY_TYPE_SMTP_SERVICE = "SMTP_SERVICE";
        private const string ENTITY_TYPE_WEBHOOK = "WEBHOOK";

        private const string META_SK = "META";
        private const string SCHEDULED_NONE_SENTINEL = "NONE";

        #endregion

        #region ── Fields ──

        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly IMemoryCache _cache;
        private readonly ILogger<NotificationRepository> _logger;
        private readonly string _tableName;

        /// <summary>
        /// CancellationTokenSource used to invalidate all SMTP cache entries at once.
        /// Replicates EmailServiceManager.ClearCache() (source lines 30-33) which
        /// disposed and recreated the entire MemoryCache instance. Using a change token
        /// achieves the same effect without disposing the shared IMemoryCache.
        /// </summary>
        private CancellationTokenSource _cacheResetTokenSource = new();
        private readonly object _cacheResetLock = new();

        #endregion

        #region ── Constructor ──

        /// <summary>
        /// Initializes the repository with DI-injected dependencies.
        /// IAmazonDynamoDB is configured with AWS_ENDPOINT_URL at DI level for LocalStack compatibility.
        /// </summary>
        /// <param name="dynamoDbClient">DynamoDB client (configured via DI with AWS_ENDPOINT_URL for LocalStack)</param>
        /// <param name="cache">Memory cache for SMTP service config read-through caching</param>
        /// <param name="logger">Structured JSON logger with correlation-ID propagation</param>
        public NotificationRepository(
            IAmazonDynamoDB dynamoDbClient,
            IMemoryCache cache,
            ILogger<NotificationRepository> logger)
        {
            _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tableName = Environment.GetEnvironmentVariable("NOTIFICATIONS_TABLE_NAME") ?? "notifications";
        }

        #endregion

        #region ── Email CRUD ──

        /// <inheritdoc />
        public async Task SaveEmailAsync(Email email, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(email);

            _logger.LogInformation("Saving email {EmailId} with status {Status} and priority {Priority}",
                email.Id, email.Status, email.Priority);

            try
            {
                var item = MapFromEmail(email);
                var request = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item
                };

                await _dynamoDbClient.PutItemAsync(request, ct).ConfigureAwait(false);

                _logger.LogInformation("Email {EmailId} saved successfully", email.Id);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to save email {EmailId}", email.Id);
                throw new Exception($"Failed to save email with id = '{email.Id}': {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public async Task<Email?> GetEmailByIdAsync(Guid emailId, CancellationToken ct = default)
        {
            _logger.LogInformation("Retrieving email {EmailId}", emailId);

            try
            {
                var request = new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"EMAIL#{emailId}" },
                        [SK] = new AttributeValue { S = META_SK }
                    }
                };

                var response = await _dynamoDbClient.GetItemAsync(request, ct).ConfigureAwait(false);

                if (response.Item == null || response.Item.Count == 0)
                {
                    _logger.LogInformation("Email {EmailId} not found", emailId);
                    return null;
                }

                return MapToEmail(response.Item);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to retrieve email {EmailId}", emailId);
                throw new Exception($"Failed to retrieve email with id = '{emailId}': {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public async Task<List<Email>> GetPendingEmailsAsync(int pageSize = 10, CancellationToken ct = default)
        {
            _logger.LogInformation("Querying pending emails with pageSize {PageSize}", pageSize);

            try
            {
                var nowIso = DateTime.UtcNow.ToString("O");

                var request = new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = GSI1_INDEX_NAME,
                    KeyConditionExpression = $"{GSI1_PK} = :pk",
                    FilterExpression = "scheduled_on <> :none AND scheduled_on < :now",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"STATUS#{(int)EmailStatus.Pending}" },
                        [":none"] = new AttributeValue { S = SCHEDULED_NONE_SENTINEL },
                        [":now"] = new AttributeValue { S = nowIso }
                    },
                    ScanIndexForward = true, // ASC on GSI1SK (inverted priority → DESC priority, then ASC scheduled_on)
                    Limit = pageSize
                };

                var emails = new List<Email>();
                QueryResponse? response = null;

                do
                {
                    if (response?.LastEvaluatedKey?.Count > 0)
                    {
                        request.ExclusiveStartKey = response.LastEvaluatedKey;
                    }

                    response = await _dynamoDbClient.QueryAsync(request, ct).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        emails.Add(MapToEmail(item));
                    }

                    // Stop once we have enough emails (Limit on Query limits scanned items, not filtered results)
                    if (emails.Count >= pageSize)
                    {
                        break;
                    }
                }
                while (response.LastEvaluatedKey?.Count > 0);

                _logger.LogInformation("Retrieved {Count} pending emails", emails.Count);
                return emails.Take(pageSize).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to query pending emails");
                throw new Exception($"Failed to query pending emails: {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public async Task DeleteEmailAsync(Guid emailId, CancellationToken ct = default)
        {
            _logger.LogInformation("Deleting email {EmailId}", emailId);

            try
            {
                var request = new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"EMAIL#{emailId}" },
                        [SK] = new AttributeValue { S = META_SK }
                    }
                };

                await _dynamoDbClient.DeleteItemAsync(request, ct).ConfigureAwait(false);

                _logger.LogInformation("Email {EmailId} deleted successfully", emailId);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to delete email {EmailId}", emailId);
                throw new Exception($"Failed to delete email with id = '{emailId}': {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public async Task<List<Email>> GetEmailsByStatusAsync(EmailStatus status, int limit = 50, CancellationToken ct = default)
        {
            _logger.LogInformation("Querying emails by status {Status} with limit {Limit}", status, limit);

            try
            {
                var request = new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = GSI1_INDEX_NAME,
                    KeyConditionExpression = $"{GSI1_PK} = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"STATUS#{(int)status}" }
                    },
                    ScanIndexForward = true,
                    Limit = limit
                };

                var emails = new List<Email>();
                var response = await _dynamoDbClient.QueryAsync(request, ct).ConfigureAwait(false);

                foreach (var item in response.Items)
                {
                    emails.Add(MapToEmail(item));
                }

                _logger.LogInformation("Retrieved {Count} emails with status {Status}", emails.Count, status);
                return emails;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to query emails by status {Status}", status);
                throw new Exception($"Failed to query emails by status '{status}': {ex.Message}", ex);
            }
        }

        #endregion

        #region ── SMTP Service Config CRUD ──

        /// <inheritdoc />
        public async Task<SmtpServiceConfig?> GetSmtpServiceByIdAsync(Guid serviceId, CancellationToken ct = default)
        {
            // Read-through cache pattern matching EmailServiceManager.GetSmtpService(Guid id) lines 53-64
            string cacheKey = $"SMTP-{serviceId}";

            if (_cache.TryGetValue(cacheKey, out SmtpServiceConfig? cached) && cached != null)
            {
                _logger.LogInformation("SMTP service {ServiceId} found in cache", serviceId);
                return cached;
            }

            _logger.LogInformation("SMTP service {ServiceId} cache miss, querying DynamoDB", serviceId);

            try
            {
                var request = new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"SMTP_SERVICE#{serviceId}" },
                        [SK] = new AttributeValue { S = META_SK }
                    }
                };

                var response = await _dynamoDbClient.GetItemAsync(request, ct).ConfigureAwait(false);

                if (response.Item == null || response.Item.Count == 0)
                {
                    _logger.LogWarning("SMTP service {ServiceId} not found", serviceId);
                    return null;
                }

                var service = MapToSmtpServiceConfig(response.Item);
                SetSmtpCacheEntry(cacheKey, service);
                return service;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to retrieve SMTP service {ServiceId}", serviceId);
                throw new Exception($"SmtpService with id = '{serviceId}' not found.", ex);
            }
        }

        /// <inheritdoc />
        public async Task<SmtpServiceConfig?> GetSmtpServiceByNameAsync(string name, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            // Read-through cache pattern matching EmailServiceManager.GetSmtpService(string name) lines 66-77
            string cacheKey = $"SMTP-{name}";

            if (_cache.TryGetValue(cacheKey, out SmtpServiceConfig? cached) && cached != null)
            {
                _logger.LogInformation("SMTP service '{ServiceName}' found in cache", name);
                return cached;
            }

            _logger.LogInformation("SMTP service '{ServiceName}' cache miss, querying GSI2", name);

            try
            {
                var request = new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = GSI2_INDEX_NAME,
                    KeyConditionExpression = $"{GSI2_PK} = :pk AND {GSI2_SK} = :sk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"SMTP_SERVICE#{name}" },
                        [":sk"] = new AttributeValue { S = META_SK }
                    },
                    Limit = 1
                };

                var response = await _dynamoDbClient.QueryAsync(request, ct).ConfigureAwait(false);

                if (response.Items.Count == 0)
                {
                    // Preserve exact error message from EmailServiceManager.GetSmtpServiceInternal(string) line 86
                    throw new Exception($"SmtpService with name '{name}' not found.");
                }

                var service = MapToSmtpServiceConfig(response.Items[0]);
                SetSmtpCacheEntry(cacheKey, service);
                return service;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to retrieve SMTP service by name '{ServiceName}'", name);
                throw new Exception($"SmtpService with name '{name}' not found.", ex);
            }
        }

        /// <inheritdoc />
        public async Task<SmtpServiceConfig?> GetDefaultSmtpServiceAsync(CancellationToken ct = default)
        {
            const string cacheKey = "SMTP-DEFAULT";

            if (_cache.TryGetValue(cacheKey, out SmtpServiceConfig? cached) && cached != null)
            {
                _logger.LogInformation("Default SMTP service found in cache");
                return cached;
            }

            _logger.LogInformation("Default SMTP service cache miss, querying DynamoDB");

            try
            {
                // Step 1: Read the default pointer record
                var pointerRequest = new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = "SMTP_SERVICE#DEFAULT" },
                        [SK] = new AttributeValue { S = META_SK }
                    }
                };

                var pointerResponse = await _dynamoDbClient.GetItemAsync(pointerRequest, ct).ConfigureAwait(false);

                if (pointerResponse.Item == null || pointerResponse.Item.Count == 0)
                {
                    // Preserve exact error message from EmailServiceManager.GetSmtpServiceInternal(null) line 94
                    throw new Exception("Default SmtpService not found.");
                }

                var defaultServiceId = GetStringOrDefault(pointerResponse.Item, "service_id");
                if (string.IsNullOrEmpty(defaultServiceId) || !Guid.TryParse(defaultServiceId, out var serviceGuid))
                {
                    throw new Exception("Default SmtpService not found.");
                }

                // Step 2: Fetch the actual service config
                var serviceRequest = new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"SMTP_SERVICE#{serviceGuid}" },
                        [SK] = new AttributeValue { S = META_SK }
                    }
                };

                var serviceResponse = await _dynamoDbClient.GetItemAsync(serviceRequest, ct).ConfigureAwait(false);

                if (serviceResponse.Item == null || serviceResponse.Item.Count == 0)
                {
                    throw new Exception("Default SmtpService not found.");
                }

                var service = MapToSmtpServiceConfig(serviceResponse.Item);
                SetSmtpCacheEntry(cacheKey, service);
                return service;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to retrieve default SMTP service");
                throw new Exception("Default SmtpService not found.", ex);
            }
        }

        /// <inheritdoc />
        public async Task SaveSmtpServiceAsync(SmtpServiceConfig service, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(service);

            _logger.LogInformation("Saving SMTP service {ServiceId} (Name={ServiceName}, IsDefault={IsDefault})",
                service.Id, service.Name, service.IsDefault);

            try
            {
                // Handle default service invariant per SmtpInternalService.HandleDefaultServiceSetup (source lines 356-385)
                if (service.IsDefault)
                {
                    // Unset is_default on all other services that are currently default
                    await UnsetPreviousDefaultServicesAsync(service.Id, ct).ConfigureAwait(false);

                    // Update the default pointer record
                    await UpdateDefaultPointerAsync(service.Id, ct).ConfigureAwait(false);
                }
                else
                {
                    // Check if this service was previously default and is being unset
                    // Source lines 372-384: prevent unsetting the only default
                    var existingService = await GetSmtpServiceByIdInternalAsync(service.Id, ct).ConfigureAwait(false);
                    if (existingService != null && existingService.IsDefault)
                    {
                        throw new Exception("Forbidden. There should always be an active default service.");
                    }
                }

                // Save the service config
                var item = MapFromSmtpServiceConfig(service);
                var request = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item
                };

                await _dynamoDbClient.PutItemAsync(request, ct).ConfigureAwait(false);

                // Clear cache after mutation per SmtpServiceRecordHook.OnPostCreateRecord/OnPostUpdateRecord
                ClearSmtpServiceCache();

                _logger.LogInformation("SMTP service {ServiceId} saved successfully", service.Id);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to save SMTP service {ServiceId}", service.Id);
                throw new Exception($"Failed to save SMTP service with id = '{service.Id}': {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public async Task DeleteSmtpServiceAsync(Guid serviceId, CancellationToken ct = default)
        {
            _logger.LogInformation("Deleting SMTP service {ServiceId}", serviceId);

            try
            {
                // Check if service is default before deleting
                // Maps to SmtpServiceRecordHook.OnPreDeleteRecord (source lines 43-49)
                var existingService = await GetSmtpServiceByIdInternalAsync(serviceId, ct).ConfigureAwait(false);
                if (existingService != null && existingService.IsDefault)
                {
                    // Preserve exact error message from SmtpServiceRecordHook line 47
                    throw new InvalidOperationException("Default smtp service cannot be deleted.");
                }

                var request = new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"SMTP_SERVICE#{serviceId}" },
                        [SK] = new AttributeValue { S = META_SK }
                    }
                };

                await _dynamoDbClient.DeleteItemAsync(request, ct).ConfigureAwait(false);

                // Clear cache after deletion per SmtpServiceRecordHook pattern
                ClearSmtpServiceCache();

                _logger.LogInformation("SMTP service {ServiceId} deleted successfully", serviceId);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to delete SMTP service {ServiceId}", serviceId);
                throw new Exception($"Failed to delete SMTP service with id = '{serviceId}': {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public async Task<List<SmtpServiceConfig>> GetAllSmtpServicesAsync(CancellationToken ct = default)
        {
            _logger.LogInformation("Retrieving all SMTP service configurations");

            try
            {
                var request = new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = $"{ENTITY_TYPE_ATTR} = :entityType",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":entityType"] = new AttributeValue { S = ENTITY_TYPE_SMTP_SERVICE }
                    }
                };

                var services = new List<SmtpServiceConfig>();
                ScanResponse? response = null;

                do
                {
                    if (response?.LastEvaluatedKey?.Count > 0)
                    {
                        request.ExclusiveStartKey = response.LastEvaluatedKey;
                    }

                    response = await _dynamoDbClient.ScanAsync(request, ct).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        services.Add(MapToSmtpServiceConfig(item));
                    }
                }
                while (response.LastEvaluatedKey?.Count > 0);

                _logger.LogInformation("Retrieved {Count} SMTP service configurations", services.Count);
                return services;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to retrieve all SMTP service configurations");
                throw new Exception($"Failed to retrieve SMTP service configurations: {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public void ClearSmtpServiceCache()
        {
            // Replicates EmailServiceManager.ClearCache() (source lines 30-33) which
            // disposed and recreated the entire MemoryCache. Using CancellationChangeToken
            // achieves the same effect: all cached entries registered with the old token
            // are immediately expired when the token is cancelled.
            lock (_cacheResetLock)
            {
                var oldTokenSource = _cacheResetTokenSource;
                _cacheResetTokenSource = new CancellationTokenSource();
                oldTokenSource.Cancel();
                oldTokenSource.Dispose();
            }

            _logger.LogInformation("SMTP service cache cleared");
        }

        #endregion

        #region ── Webhook Config CRUD ──

        /// <inheritdoc />
        public async Task SaveWebhookConfigAsync(WebhookConfig config, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(config);

            _logger.LogInformation("Saving webhook config {WebhookId} for channel '{Channel}'",
                config.Id, config.Channel);

            try
            {
                var item = MapFromWebhookConfig(config);
                var request = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item
                };

                await _dynamoDbClient.PutItemAsync(request, ct).ConfigureAwait(false);

                _logger.LogInformation("Webhook config {WebhookId} saved successfully", config.Id);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to save webhook config {WebhookId}", config.Id);
                throw new Exception($"Failed to save webhook config with id = '{config.Id}': {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public async Task<WebhookConfig?> GetWebhookConfigByIdAsync(Guid webhookId, CancellationToken ct = default)
        {
            _logger.LogInformation("Retrieving webhook config {WebhookId}", webhookId);

            try
            {
                var request = new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"WEBHOOK#{webhookId}" },
                        [SK] = new AttributeValue { S = META_SK }
                    }
                };

                var response = await _dynamoDbClient.GetItemAsync(request, ct).ConfigureAwait(false);

                if (response.Item == null || response.Item.Count == 0)
                {
                    _logger.LogInformation("Webhook config {WebhookId} not found", webhookId);
                    return null;
                }

                return MapToWebhookConfig(response.Item);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to retrieve webhook config {WebhookId}", webhookId);
                throw new Exception($"Failed to retrieve webhook config with id = '{webhookId}': {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public async Task<List<WebhookConfig>> GetActiveWebhooksByChannelAsync(string channel, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(channel);

            _logger.LogInformation("Querying active webhooks for channel '{Channel}'", channel);

            try
            {
                var request = new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = $"{ENTITY_TYPE_ATTR} = :entityType AND channel = :channel AND is_enabled = :enabled",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":entityType"] = new AttributeValue { S = ENTITY_TYPE_WEBHOOK },
                        [":channel"] = new AttributeValue { S = channel },
                        [":enabled"] = new AttributeValue { BOOL = true }
                    }
                };

                var webhooks = new List<WebhookConfig>();
                ScanResponse? response = null;

                do
                {
                    if (response?.LastEvaluatedKey?.Count > 0)
                    {
                        request.ExclusiveStartKey = response.LastEvaluatedKey;
                    }

                    response = await _dynamoDbClient.ScanAsync(request, ct).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        webhooks.Add(MapToWebhookConfig(item));
                    }
                }
                while (response.LastEvaluatedKey?.Count > 0);

                _logger.LogInformation("Found {Count} active webhooks for channel '{Channel}'", webhooks.Count, channel);
                return webhooks;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to query active webhooks for channel '{Channel}'", channel);
                throw new Exception($"Failed to query webhooks for channel '{channel}': {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        public async Task DeleteWebhookConfigAsync(Guid webhookId, CancellationToken ct = default)
        {
            _logger.LogInformation("Deleting webhook config {WebhookId}", webhookId);

            try
            {
                var request = new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"WEBHOOK#{webhookId}" },
                        [SK] = new AttributeValue { S = META_SK }
                    }
                };

                await _dynamoDbClient.DeleteItemAsync(request, ct).ConfigureAwait(false);

                _logger.LogInformation("Webhook config {WebhookId} deleted successfully", webhookId);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "Failed to delete webhook config {WebhookId}", webhookId);
                throw new Exception($"Failed to delete webhook config with id = '{webhookId}': {ex.Message}", ex);
            }
        }

        #endregion

        #region ── Private: Cache Helpers ──

        /// <summary>
        /// Sets a value in the SMTP service cache with 1-hour absolute expiration and
        /// the current generation's cancellation change token. Replicates the caching
        /// pattern from EmailServiceManager.AddObjectToCache (source lines 35-39).
        /// </summary>
        private void SetSmtpCacheEntry(string cacheKey, SmtpServiceConfig service)
        {
            var options = new MemoryCacheEntryOptions();
            options.SetAbsoluteExpiration(TimeSpan.FromHours(1));

            // Register the change token so ClearSmtpServiceCache() expires this entry
            CancellationTokenSource currentTokenSource;
            lock (_cacheResetLock)
            {
                currentTokenSource = _cacheResetTokenSource;
            }
            options.AddExpirationToken(new CancellationChangeToken(currentTokenSource.Token));

            _cache.Set(cacheKey, service, options);
        }

        #endregion

        #region ── Private: SMTP Service Helpers ──

        /// <summary>
        /// Retrieves an SMTP service config by ID directly from DynamoDB (bypasses cache).
        /// Used internally for invariant checks during save and delete operations.
        /// </summary>
        private async Task<SmtpServiceConfig?> GetSmtpServiceByIdInternalAsync(Guid serviceId, CancellationToken ct)
        {
            var request = new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    [PK] = new AttributeValue { S = $"SMTP_SERVICE#{serviceId}" },
                    [SK] = new AttributeValue { S = META_SK }
                }
            };

            var response = await _dynamoDbClient.GetItemAsync(request, ct).ConfigureAwait(false);

            if (response.Item == null || response.Item.Count == 0)
            {
                return null;
            }

            return MapToSmtpServiceConfig(response.Item);
        }

        /// <summary>
        /// Unsets is_default on all currently-default SMTP services except the specified one.
        /// Maps to SmtpInternalService.HandleDefaultServiceSetup (source lines 356-371).
        /// </summary>
        private async Task UnsetPreviousDefaultServicesAsync(Guid excludeServiceId, CancellationToken ct)
        {
            var allServices = await GetAllSmtpServicesAsync(ct).ConfigureAwait(false);

            foreach (var existingService in allServices.Where(s => s.IsDefault && s.Id != excludeServiceId))
            {
                _logger.LogInformation("Unsetting is_default on SMTP service {ServiceId}", existingService.Id);

                existingService.IsDefault = false;
                var item = MapFromSmtpServiceConfig(existingService);
                var updateRequest = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item
                };

                await _dynamoDbClient.PutItemAsync(updateRequest, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Updates the default service pointer record (PK=SMTP_SERVICE#DEFAULT, SK=META).
        /// </summary>
        private async Task UpdateDefaultPointerAsync(Guid serviceId, CancellationToken ct)
        {
            var pointerItem = new Dictionary<string, AttributeValue>
            {
                [PK] = new AttributeValue { S = "SMTP_SERVICE#DEFAULT" },
                [SK] = new AttributeValue { S = META_SK },
                ["service_id"] = new AttributeValue { S = serviceId.ToString() },
                [ENTITY_TYPE_ATTR] = new AttributeValue { S = "SMTP_SERVICE_DEFAULT_POINTER" }
            };

            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = pointerItem
            };

            await _dynamoDbClient.PutItemAsync(request, ct).ConfigureAwait(false);
        }

        #endregion

        #region ── Private: Email Attribute Mapping ──

        /// <summary>
        /// Serializes an Email domain model to a DynamoDB attribute dictionary.
        /// Maps all 17 properties plus DynamoDB key and GSI attributes.
        /// </summary>
        private Dictionary<string, AttributeValue> MapFromEmail(Email email)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                // Primary key
                [PK] = new AttributeValue { S = $"EMAIL#{email.Id}" },
                [SK] = new AttributeValue { S = META_SK },
                [ENTITY_TYPE_ATTR] = new AttributeValue { S = ENTITY_TYPE_EMAIL },

                // GSI1 — Queue processing: partition by status, sort by inverted priority + scheduled time
                [GSI1_PK] = new AttributeValue { S = $"STATUS#{(int)email.Status}" },
                [GSI1_SK] = new AttributeValue
                {
                    S = $"PRIORITY#{(9999 - (int)email.Priority):D4}#SCHEDULED#{email.ScheduledOn?.ToString("O") ?? SCHEDULED_NONE_SENTINEL}"
                },

                // Domain attributes (all 17 properties from Email model)
                ["id"] = new AttributeValue { S = email.Id.ToString() },
                ["service_id"] = new AttributeValue { S = email.ServiceId.ToString() },
                ["sender"] = new AttributeValue { S = JsonSerializer.Serialize(email.Sender, NotificationJsonContext.Default.EmailAddress) },
                ["recipients"] = new AttributeValue { S = JsonSerializer.Serialize(email.Recipients, NotificationJsonContext.Default.ListEmailAddress) },
                ["reply_to_email"] = new AttributeValue { S = email.ReplyToEmail ?? string.Empty },
                ["subject"] = new AttributeValue { S = email.Subject ?? string.Empty },
                ["content_text"] = new AttributeValue { S = email.ContentText ?? string.Empty },
                ["content_html"] = new AttributeValue { S = email.ContentHtml ?? string.Empty },
                ["created_on"] = new AttributeValue { S = email.CreatedOn.ToString("O") },
                ["status"] = new AttributeValue { N = ((int)email.Status).ToString() },
                ["priority"] = new AttributeValue { N = ((int)email.Priority).ToString() },
                ["server_error"] = new AttributeValue { S = email.ServerError ?? string.Empty },
                ["retries_count"] = new AttributeValue { N = email.RetriesCount.ToString() },
                ["x_search"] = new AttributeValue { S = email.XSearch ?? string.Empty },
                ["attachments"] = new AttributeValue { S = JsonSerializer.Serialize(email.Attachments ?? new List<string>(), NotificationJsonContext.Default.ListString) }
            };

            // Nullable DateTime fields
            if (email.SentOn.HasValue)
            {
                item["sent_on"] = new AttributeValue { S = email.SentOn.Value.ToString("O") };
            }
            else
            {
                item["sent_on"] = new AttributeValue { NULL = true };
            }

            if (email.ScheduledOn.HasValue)
            {
                item["scheduled_on"] = new AttributeValue { S = email.ScheduledOn.Value.ToString("O") };
            }
            else
            {
                item["scheduled_on"] = new AttributeValue { S = SCHEDULED_NONE_SENTINEL };
            }

            return item;
        }

        /// <summary>
        /// Deserializes a DynamoDB attribute dictionary back to an Email domain model.
        /// Handles all 17 properties including nullable DateTimes and JSON-serialized collections.
        /// </summary>
        private Email MapToEmail(Dictionary<string, AttributeValue> item)
        {
            return new Email
            {
                Id = GetGuidOrDefault(item, "id"),
                ServiceId = GetGuidOrDefault(item, "service_id"),
                Sender = DeserializeEmailAddress(GetStringOrDefault(item, "sender")) ?? new EmailAddress(),
                Recipients = DeserializeEmailAddressList(GetStringOrDefault(item, "recipients")) ?? new List<EmailAddress>(),
                ReplyToEmail = GetStringOrDefault(item, "reply_to_email"),
                Subject = GetStringOrDefault(item, "subject"),
                ContentText = GetStringOrDefault(item, "content_text"),
                ContentHtml = GetStringOrDefault(item, "content_html"),
                CreatedOn = ParseDateTime(GetStringOrDefault(item, "created_on")),
                SentOn = ParseNullableDateTime(item.GetValueOrDefault("sent_on")),
                Status = (EmailStatus)GetIntOrDefault(item, "status"),
                Priority = (EmailPriority)GetIntOrDefault(item, "priority"),
                ServerError = GetStringOrDefault(item, "server_error"),
                ScheduledOn = ParseScheduledOn(item.GetValueOrDefault("scheduled_on")),
                RetriesCount = GetIntOrDefault(item, "retries_count"),
                XSearch = GetStringOrDefault(item, "x_search"),
                Attachments = DeserializeStringList(GetStringOrDefault(item, "attachments")) ?? new List<string>()
            };
        }

        #endregion

        #region ── Private: SMTP Service Config Attribute Mapping ──

        /// <summary>
        /// Serializes an SmtpServiceConfig model to a DynamoDB attribute dictionary.
        /// Maps all 14 properties plus key and GSI attributes.
        /// </summary>
        private Dictionary<string, AttributeValue> MapFromSmtpServiceConfig(SmtpServiceConfig config)
        {
            return new Dictionary<string, AttributeValue>
            {
                // Primary key
                [PK] = new AttributeValue { S = $"SMTP_SERVICE#{config.Id}" },
                [SK] = new AttributeValue { S = META_SK },
                [ENTITY_TYPE_ATTR] = new AttributeValue { S = ENTITY_TYPE_SMTP_SERVICE },

                // GSI2 — Name lookup
                [GSI2_PK] = new AttributeValue { S = $"SMTP_SERVICE#{config.Name}" },
                [GSI2_SK] = new AttributeValue { S = META_SK },

                // Domain attributes (all 14 properties from SmtpServiceConfig model)
                ["id"] = new AttributeValue { S = config.Id.ToString() },
                ["name"] = new AttributeValue { S = config.Name ?? string.Empty },
                ["server"] = new AttributeValue { S = config.Server ?? string.Empty },
                ["port"] = new AttributeValue { N = config.Port.ToString() },
                ["username"] = new AttributeValue { S = config.Username ?? string.Empty },
                ["password"] = new AttributeValue { S = config.Password ?? string.Empty },
                ["default_sender_name"] = new AttributeValue { S = config.DefaultSenderName ?? string.Empty },
                ["default_sender_email"] = new AttributeValue { S = config.DefaultSenderEmail ?? string.Empty },
                ["default_reply_to_email"] = new AttributeValue { S = config.DefaultReplyToEmail ?? string.Empty },
                ["max_retries_count"] = new AttributeValue { N = config.MaxRetriesCount.ToString() },
                ["retry_wait_minutes"] = new AttributeValue { N = config.RetryWaitMinutes.ToString() },
                ["is_default"] = new AttributeValue { BOOL = config.IsDefault },
                ["is_enabled"] = new AttributeValue { BOOL = config.IsEnabled },
                ["connection_security"] = new AttributeValue { N = config.ConnectionSecurity.ToString() }
            };
        }

        /// <summary>
        /// Deserializes a DynamoDB attribute dictionary back to an SmtpServiceConfig model.
        /// </summary>
        private SmtpServiceConfig MapToSmtpServiceConfig(Dictionary<string, AttributeValue> item)
        {
            return new SmtpServiceConfig
            {
                Id = GetGuidOrDefault(item, "id"),
                Name = GetStringOrDefault(item, "name"),
                Server = GetStringOrDefault(item, "server"),
                Port = GetIntOrDefault(item, "port"),
                Username = GetStringOrDefault(item, "username"),
                Password = GetStringOrDefault(item, "password"),
                DefaultSenderName = GetStringOrDefault(item, "default_sender_name"),
                DefaultSenderEmail = GetStringOrDefault(item, "default_sender_email"),
                DefaultReplyToEmail = GetStringOrDefault(item, "default_reply_to_email"),
                MaxRetriesCount = GetIntOrDefault(item, "max_retries_count"),
                RetryWaitMinutes = GetIntOrDefault(item, "retry_wait_minutes"),
                IsDefault = GetBoolOrDefault(item, "is_default"),
                IsEnabled = GetBoolOrDefault(item, "is_enabled"),
                ConnectionSecurity = GetIntOrDefault(item, "connection_security")
            };
        }

        #endregion

        #region ── Private: Webhook Config Attribute Mapping ──

        /// <summary>
        /// Serializes a WebhookConfig model to a DynamoDB attribute dictionary.
        /// Maps all 7 properties plus key attributes.
        /// </summary>
        private Dictionary<string, AttributeValue> MapFromWebhookConfig(WebhookConfig config)
        {
            return new Dictionary<string, AttributeValue>
            {
                // Primary key
                [PK] = new AttributeValue { S = $"WEBHOOK#{config.Id}" },
                [SK] = new AttributeValue { S = META_SK },
                [ENTITY_TYPE_ATTR] = new AttributeValue { S = ENTITY_TYPE_WEBHOOK },

                // Domain attributes (all 7 properties from WebhookConfig model)
                ["id"] = new AttributeValue { S = config.Id.ToString() },
                ["endpoint_url"] = new AttributeValue { S = config.EndpointUrl ?? string.Empty },
                ["channel"] = new AttributeValue { S = config.Channel ?? string.Empty },
                ["max_retries"] = new AttributeValue { N = config.MaxRetries.ToString() },
                ["retry_interval_seconds"] = new AttributeValue { N = config.RetryIntervalSeconds.ToString() },
                ["is_enabled"] = new AttributeValue { BOOL = config.IsEnabled },
                ["created_on"] = new AttributeValue { S = config.CreatedOn.ToString("O") }
            };
        }

        /// <summary>
        /// Deserializes a DynamoDB attribute dictionary back to a WebhookConfig model.
        /// </summary>
        private WebhookConfig MapToWebhookConfig(Dictionary<string, AttributeValue> item)
        {
            return new WebhookConfig
            {
                Id = GetGuidOrDefault(item, "id"),
                EndpointUrl = GetStringOrDefault(item, "endpoint_url"),
                Channel = GetStringOrDefault(item, "channel"),
                MaxRetries = GetIntOrDefault(item, "max_retries"),
                RetryIntervalSeconds = GetIntOrDefault(item, "retry_interval_seconds"),
                IsEnabled = GetBoolOrDefault(item, "is_enabled"),
                CreatedOn = ParseDateTime(GetStringOrDefault(item, "created_on"))
            };
        }

        #endregion

        #region ── Private: Common Attribute Helpers ──

        /// <summary>
        /// Safely extracts a string value from a DynamoDB attribute dictionary.
        /// Returns the default value if the key is missing or the attribute type is not string.
        /// </summary>
        private static string GetStringOrDefault(Dictionary<string, AttributeValue> item, string key, string defaultValue = "")
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null)
            {
                return attr.S;
            }
            return defaultValue;
        }

        /// <summary>
        /// Safely extracts an integer value from a DynamoDB attribute dictionary.
        /// Returns the default value if the key is missing or the attribute type is not number.
        /// </summary>
        private static int GetIntOrDefault(Dictionary<string, AttributeValue> item, string key, int defaultValue = 0)
        {
            if (item.TryGetValue(key, out var attr) && attr.N != null)
            {
                return int.TryParse(attr.N, out var result) ? result : defaultValue;
            }
            return defaultValue;
        }

        /// <summary>
        /// Safely extracts a boolean value from a DynamoDB attribute dictionary.
        /// Returns the default value if the key is missing or the attribute type is not boolean.
        /// </summary>
        private static bool GetBoolOrDefault(Dictionary<string, AttributeValue> item, string key, bool defaultValue = false)
        {
            if (item.TryGetValue(key, out var attr))
            {
                return attr.BOOL;
            }
            return defaultValue;
        }

        /// <summary>
        /// Safely extracts a Guid value from a DynamoDB attribute dictionary.
        /// Returns Guid.Empty if the key is missing or the value cannot be parsed.
        /// </summary>
        private static Guid GetGuidOrDefault(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null)
            {
                return Guid.TryParse(attr.S, out var result) ? result : Guid.Empty;
            }
            return Guid.Empty;
        }

        /// <summary>
        /// Parses a nullable DateTime from a DynamoDB AttributeValue.
        /// Returns null if the attribute is null, has NULL=true, or cannot be parsed.
        /// </summary>
        private static DateTime? ParseNullableDateTime(AttributeValue? value)
        {
            if (value == null || value.NULL || string.IsNullOrEmpty(value.S))
            {
                return null;
            }
            return DateTime.TryParse(value.S, null, System.Globalization.DateTimeStyles.RoundtripKind, out var result) ? result : null;
        }

        /// <summary>
        /// Parses a scheduled_on DateTime, handling the NONE sentinel value.
        /// Returns null if the value is NONE, null, or cannot be parsed.
        /// </summary>
        private static DateTime? ParseScheduledOn(AttributeValue? value)
        {
            if (value == null || value.NULL || string.IsNullOrEmpty(value.S) || value.S == SCHEDULED_NONE_SENTINEL)
            {
                return null;
            }
            return DateTime.TryParse(value.S, null, System.Globalization.DateTimeStyles.RoundtripKind, out var result) ? result : null;
        }

        /// <summary>
        /// Parses a non-nullable DateTime from a string value.
        /// Returns DateTime.MinValue if the string cannot be parsed.
        /// </summary>
        private static DateTime ParseDateTime(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return DateTime.MinValue;
            }
            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var result) ? result : DateTime.MinValue;
        }

        /// <summary>
        /// Deserializes a JSON string to an EmailAddress using the source-generated context.
        /// Returns null on failure. AOT-safe for Native AOT compilation.
        /// </summary>
        private static EmailAddress? DeserializeEmailAddress(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(json, NotificationJsonContext.Default.EmailAddress);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Deserializes a JSON string to a List of EmailAddress using the source-generated context.
        /// Returns null on failure. AOT-safe for Native AOT compilation.
        /// </summary>
        private static List<EmailAddress>? DeserializeEmailAddressList(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(json, NotificationJsonContext.Default.ListEmailAddress);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Deserializes a JSON string to a List of strings using the source-generated context.
        /// Returns null on failure. AOT-safe for Native AOT compilation.
        /// </summary>
        private static List<string>? DeserializeStringList(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(json, NotificationJsonContext.Default.ListString);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        #endregion
    }
}
