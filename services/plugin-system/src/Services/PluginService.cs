using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using WebVellaErp.PluginSystem.DataAccess;
using WebVellaErp.PluginSystem.Models;

namespace WebVellaErp.PluginSystem.Services
{
    /// <summary>
    /// Defines the contract for plugin lifecycle management operations.
    /// Injected into Lambda handlers (Functions/PluginHandler.cs) via DI for testability.
    /// Replaces the monolith's scattered plugin management across ErpPlugin, IErpService,
    /// ERPService, SdkPlugin, and AdminController.
    /// </summary>
    public interface IPluginService
    {
        /// <summary>
        /// Registers a new plugin in the system.
        /// Replaces ErpPlugin.SavePluginData() INSERT path (ErpPlugin.cs lines 96-103)
        /// and IErpService.InitializePlugins() in-memory plugin list registration.
        /// Idempotent: if a plugin with the same name exists, returns the existing plugin.
        /// </summary>
        /// <param name="request">Registration request containing plugin metadata.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>Response containing the registered plugin or error details.</returns>
        Task<PluginResponse> RegisterPluginAsync(RegisterPluginRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a plugin by its unique identifier.
        /// </summary>
        /// <param name="pluginId">The plugin's unique identifier (UUID).</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>Response containing the plugin or not-found error.</returns>
        Task<PluginResponse> GetPluginByIdAsync(Guid pluginId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a plugin by its unique name.
        /// Replaces ErpPlugin.GetPluginData() (ErpPlugin.cs lines 67-85) lookup pattern:
        /// SELECT * FROM plugin_data WHERE name = @name.
        /// </summary>
        /// <param name="pluginName">The plugin's unique name.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>Response containing the plugin or not-found error.</returns>
        Task<PluginResponse> GetPluginByNameAsync(string pluginName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists all registered plugins with optional status filtering.
        /// Replaces IErpService.Plugins property (List&lt;ErpPlugin&gt;) in-memory plugin list.
        /// </summary>
        /// <param name="statusFilter">Optional status filter (Active/Inactive). Null returns all plugins.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>Response containing the plugin list with count.</returns>
        Task<PluginListResponse> ListPluginsAsync(PluginStatus? statusFilter = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing plugin's metadata using partial update semantics.
        /// Only non-null fields in the request are applied to the existing record.
        /// </summary>
        /// <param name="pluginId">The plugin's unique identifier to update.</param>
        /// <param name="request">Update request with optional fields to modify.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>Response containing the updated plugin or error details.</returns>
        Task<PluginResponse> UpdatePluginAsync(Guid pluginId, UpdatePluginRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Activates a plugin, enabling it for use within the system.
        /// Idempotent: activating an already-active plugin returns success.
        /// Publishes a plugin.plugin.activated domain event via SNS.
        /// </summary>
        /// <param name="pluginId">The plugin's unique identifier to activate.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>Response containing the activated plugin or error details.</returns>
        Task<PluginResponse> ActivatePluginAsync(Guid pluginId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deactivates a plugin, preventing it from being loaded or executed.
        /// Idempotent: deactivating an already-inactive plugin returns success.
        /// Publishes a plugin.plugin.deactivated domain event via SNS.
        /// </summary>
        /// <param name="pluginId">The plugin's unique identifier to deactivate.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>Response containing the deactivated plugin or error details.</returns>
        Task<PluginResponse> DeactivatePluginAsync(Guid pluginId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a plugin registration from the system.
        /// Idempotent: deleting a non-existent plugin returns true without error.
        /// </summary>
        /// <param name="pluginId">The plugin's unique identifier to delete.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>True if the operation completed (including no-op for missing plugins).</returns>
        Task<bool> DeletePluginAsync(Guid pluginId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves plugin settings/data JSON string by plugin name.
        /// DIRECT replacement for ErpPlugin.GetPluginData() (ErpPlugin.cs lines 67-85):
        /// Source: SELECT * FROM plugin_data WHERE name = @name → returns data column or null.
        /// Target: DynamoDB GetItem with PK=PLUGIN#{pluginName}, SK=DATA → returns data attribute or null.
        /// </summary>
        /// <param name="pluginName">The plugin name to look up data for.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        /// <returns>The JSON data string if found; null otherwise.</returns>
        Task<string?> GetPluginDataAsync(string pluginName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves plugin settings/data as a JSON string.
        /// DIRECT replacement for ErpPlugin.SavePluginData(string data) (ErpPlugin.cs lines 87-115):
        /// Source: INSERT-or-UPDATE on plugin_data table.
        /// Target: DynamoDB PutItem (upsert) with PK=PLUGIN#{pluginName}, SK=DATA.
        /// Consumed by all plugin patch files (e.g., SdkPlugin._.cs line 69-71).
        /// </summary>
        /// <param name="pluginName">The plugin name to save data for.</param>
        /// <param name="data">The JSON data string to persist.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        Task SavePluginDataAsync(string pluginName, string data, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Domain event payload for plugin lifecycle change events published to SNS.
    /// Used for AOT-compatible source-generated JSON serialization via <see cref="PluginJsonContext"/>.
    /// Follows the {domain}.{entity}.{action} event naming convention (AAP Section 0.8.5).
    /// </summary>
    public sealed class PluginDomainEvent
    {
        /// <summary>Full event type name, e.g., "plugin.plugin.registered".</summary>
        public string EventType { get; set; } = string.Empty;

        /// <summary>Unique identifier of the affected plugin.</summary>
        public string PluginId { get; set; } = string.Empty;

        /// <summary>Human-readable name of the affected plugin.</summary>
        public string PluginName { get; set; } = string.Empty;

        /// <summary>Short action verb extracted from the event type (registered, activated, deactivated).</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>ISO 8601 UTC timestamp of when the event occurred.</summary>
        public string Timestamp { get; set; } = string.Empty;

        /// <summary>Correlation ID for distributed tracing across services.</summary>
        public string CorrelationId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Source-generated JSON serializer context for AOT-compatible serialization
    /// of domain event payloads. Avoids IL2026/IL3050 trimming warnings when using
    /// System.Text.Json with .NET 9 Native AOT Lambda deployments.
    /// </summary>
    [JsonSerializable(typeof(PluginDomainEvent))]
    internal partial class PluginJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Core plugin lifecycle management service for the Plugin / Extension System microservice.
    /// 
    /// Replaces the monolith's scattered plugin management across:
    /// - ErpPlugin.cs: Abstract plugin base with GetPluginData/SavePluginData (PostgreSQL plugin_data table)
    /// - IErpService.cs: Plugins list property, InitializePlugins contract
    /// - ERPService.cs: Plugin discovery/initialization loop
    /// - SdkPlugin.cs: Initialize() → SecurityContext.OpenSystemScope() → ProcessPatches() lifecycle
    /// - SdkPlugin._.cs: ProcessPatches() with version-gated migration using GetPluginData/SavePluginData
    /// - MicrosoftCDMPlugin.cs: Skeleton Initialize() → ProcessPatches() lifecycle
    /// - AdminController.cs: Constructor DI of RecordManager, SecurityManager, EntityManager, EntityRelationManager
    /// - LogService.cs: Operational cleanup (replaced by CloudWatch Logs)
    /// 
    /// Key architectural transformations:
    /// - DbContext.Current (ambient singleton) → Injected IPluginRepository (DynamoDB-backed)
    /// - SecurityContext.OpenSystemScope() → JWT claims from Lambda event context
    /// - HookManager post-CRUD hooks → SNS domain event publishing
    /// - new RecordManager()/EntityManager() → DI-injected IPluginRepository
    /// - Newtonsoft.Json → System.Text.Json (AOT-compatible)
    /// - Synchronous operations → Async/await with CancellationToken
    /// </summary>
    public class PluginService : IPluginService
    {
        #region Constants — Domain Event Types

        /// <summary>
        /// Domain event published when a new plugin is registered.
        /// Follows {domain}.{entity}.{action} naming convention per AAP Section 0.8.5.
        /// </summary>
        private const string EventPluginRegistered = "plugin.plugin.registered";

        /// <summary>
        /// Domain event published when a plugin is activated.
        /// </summary>
        private const string EventPluginActivated = "plugin.plugin.activated";

        /// <summary>
        /// Domain event published when a plugin is deactivated.
        /// </summary>
        private const string EventPluginDeactivated = "plugin.plugin.deactivated";

        /// <summary>
        /// Environment variable key for the SNS topic ARN used for domain event publishing.
        /// </summary>
        private const string SnsTopicArnEnvVar = "PLUGIN_SYSTEM_SNS_TOPIC_ARN";

        #endregion

        #region Private Fields

        private readonly IPluginRepository _pluginRepository;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<PluginService> _logger;
        private readonly string _snsTopicArn;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of <see cref="PluginService"/>.
        /// 
        /// Replaces multiple monolith constructor patterns:
        /// - AdminController constructor (lines 28-35): new RecordManager(), new SecurityManager(),
        ///   new EntityManager(), new EntityRelationManager() → single IPluginRepository
        /// - SdkPlugin._.cs ProcessPatches() (lines 24-27): new EntityManager(), new EntityRelationManager(),
        ///   new RecordManager() → DI-injected repository
        /// - ErpPlugin.GetPluginData() (line 72): DbContext.Current.CreateConnection() → injected repository
        /// </summary>
        /// <param name="pluginRepository">DynamoDB-backed plugin data access (replaces PostgreSQL persistence).</param>
        /// <param name="snsClient">SNS client for publishing domain events (replaces HookManager).</param>
        /// <param name="logger">Structured logger with correlation-ID propagation (AAP Section 0.8.5).</param>
        /// <exception cref="ArgumentNullException">Thrown when any required dependency is null.</exception>
        public PluginService(
            IPluginRepository pluginRepository,
            IAmazonSimpleNotificationService snsClient,
            ILogger<PluginService> logger)
        {
            _pluginRepository = pluginRepository ?? throw new ArgumentNullException(nameof(pluginRepository));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _snsTopicArn = Environment.GetEnvironmentVariable(SnsTopicArnEnvVar) ?? string.Empty;

            _logger.LogInformation(
                "PluginService initialized. SNS topic ARN configured: {IsConfigured}",
                !string.IsNullOrWhiteSpace(_snsTopicArn));
        }

        #endregion

        #region Plugin Registration

        /// <inheritdoc />
        public async Task<PluginResponse> RegisterPluginAsync(
            RegisterPluginRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                _logger.LogWarning("RegisterPluginAsync called with null request");
                return new PluginResponse
                {
                    Success = false,
                    Message = "Registration request cannot be null"
                };
            }

            // Validate required fields matching source ErpPlugin.GetPluginData() line 69-70 pattern
            var validationError = ValidateRegisterRequest(request);
            if (validationError != null)
            {
                return validationError;
            }

            try
            {
                // Idempotency check (AAP Section 0.8.5): if plugin with same name exists, return it
                var existingPlugin = await _pluginRepository
                    .GetPluginByNameAsync(request.Name, cancellationToken)
                    .ConfigureAwait(false);

                if (existingPlugin != null)
                {
                    _logger.LogInformation(
                        "Plugin already registered with name {PluginName}, returning existing plugin {PluginId}",
                        existingPlugin.Name,
                        existingPlugin.Id);

                    return new PluginResponse
                    {
                        Plugin = existingPlugin,
                        Success = true,
                        Message = "Plugin already registered"
                    };
                }

                // Map request to Plugin model with all 17 properties
                var plugin = new Plugin
                {
                    Id = Guid.NewGuid(),
                    Name = request.Name,
                    Prefix = request.Prefix,
                    Url = request.Url ?? string.Empty,
                    Description = request.Description ?? string.Empty,
                    Version = request.Version,
                    Company = request.Company ?? string.Empty,
                    CompanyUrl = request.CompanyUrl ?? string.Empty,
                    Author = request.Author ?? string.Empty,
                    Repository = request.Repository ?? string.Empty,
                    License = request.License ?? string.Empty,
                    SettingsUrl = request.SettingsUrl ?? string.Empty,
                    PluginPageUrl = request.PluginPageUrl ?? string.Empty,
                    IconUrl = request.IconUrl ?? string.Empty,
                    Status = PluginStatus.Active,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                // Persist to DynamoDB
                await _pluginRepository
                    .CreatePluginAsync(plugin, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Plugin registered successfully: {PluginId} {PluginName} v{PluginVersion}",
                    plugin.Id,
                    plugin.Name,
                    plugin.Version);

                // Publish domain event asynchronously (replaces monolith's synchronous hook system)
                await PublishDomainEventAsync(
                    EventPluginRegistered,
                    plugin.Id,
                    plugin.Name,
                    cancellationToken).ConfigureAwait(false);

                return new PluginResponse
                {
                    Plugin = plugin,
                    Success = true,
                    Message = "Plugin registered successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to register plugin: {PluginName}",
                    request.Name);

                return new PluginResponse
                {
                    Success = false,
                    Message = "An error occurred while registering the plugin"
                };
            }
        }

        #endregion

        #region Plugin Query Operations

        /// <inheritdoc />
        public async Task<PluginResponse> GetPluginByIdAsync(
            Guid pluginId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var plugin = await _pluginRepository
                    .GetPluginByIdAsync(pluginId, cancellationToken)
                    .ConfigureAwait(false);

                if (plugin == null)
                {
                    _logger.LogWarning("Plugin not found by ID: {PluginId}", pluginId);
                    return new PluginResponse
                    {
                        Success = false,
                        Message = "Plugin not found"
                    };
                }

                return new PluginResponse
                {
                    Plugin = plugin,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve plugin by ID: {PluginId}", pluginId);
                return new PluginResponse
                {
                    Success = false,
                    Message = "An error occurred while retrieving the plugin"
                };
            }
        }

        /// <inheritdoc />
        public async Task<PluginResponse> GetPluginByNameAsync(
            string pluginName,
            CancellationToken cancellationToken = default)
        {
            // Validate name matching source ErpPlugin.GetPluginData() line 69:
            // if (string.IsNullOrWhiteSpace(Name)) throw new Exception("Plugin name is not specified")
            if (string.IsNullOrWhiteSpace(pluginName))
            {
                _logger.LogWarning("GetPluginByNameAsync called with null or empty plugin name");
                return new PluginResponse
                {
                    Success = false,
                    Message = "Plugin name must be specified"
                };
            }

            try
            {
                var plugin = await _pluginRepository
                    .GetPluginByNameAsync(pluginName, cancellationToken)
                    .ConfigureAwait(false);

                if (plugin == null)
                {
                    _logger.LogWarning("Plugin not found by name: {PluginName}", pluginName);
                    return new PluginResponse
                    {
                        Success = false,
                        Message = "Plugin not found"
                    };
                }

                return new PluginResponse
                {
                    Plugin = plugin,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve plugin by name: {PluginName}", pluginName);
                return new PluginResponse
                {
                    Success = false,
                    Message = "An error occurred while retrieving the plugin"
                };
            }
        }

        /// <inheritdoc />
        public async Task<PluginListResponse> ListPluginsAsync(
            PluginStatus? statusFilter = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Replaces IErpService.Plugins property (in-memory List<ErpPlugin>)
                // with DynamoDB-backed query supporting optional status filtering
                var plugins = statusFilter.HasValue
                    ? await _pluginRepository
                        .GetPluginsByStatusAsync(statusFilter.Value, cancellationToken)
                        .ConfigureAwait(false)
                    : await _pluginRepository
                        .ListPluginsAsync(cancellationToken)
                        .ConfigureAwait(false);

                _logger.LogInformation(
                    "Listed {PluginCount} plugins with status filter: {StatusFilter}",
                    plugins.Count,
                    statusFilter?.ToString() ?? "None");

                return new PluginListResponse
                {
                    Plugins = plugins,
                    TotalCount = plugins.Count,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list plugins with status filter: {StatusFilter}", statusFilter);
                return new PluginListResponse
                {
                    Success = false,
                    Message = "An error occurred while listing plugins"
                };
            }
        }

        #endregion

        #region Plugin Update and State Transitions

        /// <inheritdoc />
        public async Task<PluginResponse> UpdatePluginAsync(
            Guid pluginId,
            UpdatePluginRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                _logger.LogWarning("UpdatePluginAsync called with null request for plugin: {PluginId}", pluginId);
                return new PluginResponse
                {
                    Success = false,
                    Message = "Update request cannot be null"
                };
            }

            try
            {
                // Retrieve existing plugin
                var plugin = await _pluginRepository
                    .GetPluginByIdAsync(pluginId, cancellationToken)
                    .ConfigureAwait(false);

                if (plugin == null)
                {
                    _logger.LogWarning("Plugin not found for update: {PluginId}", pluginId);
                    return new PluginResponse
                    {
                        Success = false,
                        Message = "Plugin not found"
                    };
                }

                // Apply partial update — only non-null fields from request are applied
                ApplyPartialUpdate(plugin, request);
                plugin.UpdatedAt = DateTime.UtcNow;

                // Persist updated plugin
                await _pluginRepository
                    .UpdatePluginAsync(plugin, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Plugin updated successfully: {PluginId} {PluginName}",
                    plugin.Id,
                    plugin.Name);

                return new PluginResponse
                {
                    Plugin = plugin,
                    Success = true,
                    Message = "Plugin updated successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update plugin: {PluginId}", pluginId);
                return new PluginResponse
                {
                    Success = false,
                    Message = "An error occurred while updating the plugin"
                };
            }
        }

        /// <inheritdoc />
        public async Task<PluginResponse> ActivatePluginAsync(
            Guid pluginId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var plugin = await _pluginRepository
                    .GetPluginByIdAsync(pluginId, cancellationToken)
                    .ConfigureAwait(false);

                if (plugin == null)
                {
                    _logger.LogWarning("Plugin not found for activation: {PluginId}", pluginId);
                    return new PluginResponse
                    {
                        Success = false,
                        Message = "Plugin not found"
                    };
                }

                // Idempotent: if already active, return success without modification
                if (plugin.Status == PluginStatus.Active)
                {
                    _logger.LogInformation(
                        "Plugin {PluginId} {PluginName} is already active, returning success (idempotent)",
                        plugin.Id,
                        plugin.Name);

                    return new PluginResponse
                    {
                        Plugin = plugin,
                        Success = true,
                        Message = "Plugin is already active"
                    };
                }

                // Transition to Active state
                plugin.Status = PluginStatus.Active;
                plugin.UpdatedAt = DateTime.UtcNow;

                await _pluginRepository
                    .UpdatePluginAsync(plugin, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Plugin activated: {PluginId} {PluginName}",
                    plugin.Id,
                    plugin.Name);

                // Publish domain event per AAP Section 0.7.2 (post-hooks → SNS events)
                await PublishDomainEventAsync(
                    EventPluginActivated,
                    plugin.Id,
                    plugin.Name,
                    cancellationToken).ConfigureAwait(false);

                return new PluginResponse
                {
                    Plugin = plugin,
                    Success = true,
                    Message = "Plugin activated successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to activate plugin: {PluginId}", pluginId);
                return new PluginResponse
                {
                    Success = false,
                    Message = "An error occurred while activating the plugin"
                };
            }
        }

        /// <inheritdoc />
        public async Task<PluginResponse> DeactivatePluginAsync(
            Guid pluginId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var plugin = await _pluginRepository
                    .GetPluginByIdAsync(pluginId, cancellationToken)
                    .ConfigureAwait(false);

                if (plugin == null)
                {
                    _logger.LogWarning("Plugin not found for deactivation: {PluginId}", pluginId);
                    return new PluginResponse
                    {
                        Success = false,
                        Message = "Plugin not found"
                    };
                }

                // Idempotent: if already inactive, return success without modification
                if (plugin.Status == PluginStatus.Inactive)
                {
                    _logger.LogInformation(
                        "Plugin {PluginId} {PluginName} is already inactive, returning success (idempotent)",
                        plugin.Id,
                        plugin.Name);

                    return new PluginResponse
                    {
                        Plugin = plugin,
                        Success = true,
                        Message = "Plugin is already inactive"
                    };
                }

                // Transition to Inactive state
                plugin.Status = PluginStatus.Inactive;
                plugin.UpdatedAt = DateTime.UtcNow;

                await _pluginRepository
                    .UpdatePluginAsync(plugin, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Plugin deactivated: {PluginId} {PluginName}",
                    plugin.Id,
                    plugin.Name);

                // Publish domain event per AAP Section 0.7.2
                await PublishDomainEventAsync(
                    EventPluginDeactivated,
                    plugin.Id,
                    plugin.Name,
                    cancellationToken).ConfigureAwait(false);

                return new PluginResponse
                {
                    Plugin = plugin,
                    Success = true,
                    Message = "Plugin deactivated successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deactivate plugin: {PluginId}", pluginId);
                return new PluginResponse
                {
                    Success = false,
                    Message = "An error occurred while deactivating the plugin"
                };
            }
        }

        /// <inheritdoc />
        public async Task<bool> DeletePluginAsync(
            Guid pluginId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Idempotent: IPluginRepository.DeletePluginAsync handles non-existent plugins gracefully
                await _pluginRepository
                    .DeletePluginAsync(pluginId, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation("Plugin deleted: {PluginId}", pluginId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete plugin: {PluginId}", pluginId);
                return false;
            }
        }

        #endregion

        #region Plugin Settings/Data Operations

        /// <inheritdoc />
        public async Task<string?> GetPluginDataAsync(
            string pluginName,
            CancellationToken cancellationToken = default)
        {
            // Validate name matching source ErpPlugin.GetPluginData() line 69:
            // if (string.IsNullOrWhiteSpace(Name)) throw new Exception("Plugin name is not specified")
            if (string.IsNullOrWhiteSpace(pluginName))
            {
                _logger.LogWarning("GetPluginDataAsync called with null or empty plugin name");
                throw new ArgumentException("Plugin name must be specified", nameof(pluginName));
            }

            try
            {
                // DIRECT replacement for ErpPlugin.GetPluginData() (ErpPlugin.cs lines 67-85):
                // Source: SELECT * FROM plugin_data WHERE name = @name → returns data column or null
                // Target: DynamoDB GetItem with PK=PLUGIN#{pluginName}, SK=DATA → returns data attribute or null
                var data = await _pluginRepository
                    .GetPluginDataAsync(pluginName, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Plugin data retrieved for {PluginName}: {HasData}",
                    pluginName,
                    data != null);

                return data;
            }
            catch (ArgumentException)
            {
                throw; // Re-throw validation exceptions
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve plugin data for: {PluginName}", pluginName);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task SavePluginDataAsync(
            string pluginName,
            string data,
            CancellationToken cancellationToken = default)
        {
            // Validate name matching source ErpPlugin.SavePluginData() line 89 pattern
            if (string.IsNullOrWhiteSpace(pluginName))
            {
                _logger.LogWarning("SavePluginDataAsync called with null or empty plugin name");
                throw new ArgumentException("Plugin name must be specified", nameof(pluginName));
            }

            // Validate data is not null — the source ErpPlugin.SavePluginData() accepted a non-null string
            // parameter for the INSERT/UPDATE of plugin_data.data column
            if (data is null)
            {
                _logger.LogWarning("SavePluginDataAsync called with null data for plugin: {PluginName}", pluginName);
                throw new ArgumentException("Plugin data must not be null", nameof(data));
            }

            try
            {
                // DIRECT replacement for ErpPlugin.SavePluginData(string data) (ErpPlugin.cs lines 87-115):
                // Source: INSERT-or-UPDATE on plugin_data table
                // Target: DynamoDB PutItem (upsert) — naturally handles both INSERT and UPDATE
                // Data consumed by: SdkPlugin._.cs line 69-71:
                //   string jsonData = GetPluginData();
                //   currentPluginSettings = JsonConvert.DeserializeObject<PluginSettings>(jsonData);
                await _pluginRepository
                    .SavePluginDataAsync(pluginName, data, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Plugin data saved for {PluginName}, data length: {DataLength}",
                    pluginName,
                    data?.Length ?? 0);
            }
            catch (ArgumentException)
            {
                throw; // Re-throw validation exceptions
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save plugin data for: {PluginName}", pluginName);
                throw;
            }
        }

        #endregion

        #region Private Helpers — Validation

        /// <summary>
        /// Validates a plugin registration request.
        /// Validates required fields: Name, Prefix, Version.
        /// Returns null if valid, or an error PluginResponse if invalid.
        /// 
        /// Source pattern: ErpPlugin.GetPluginData() line 69-70:
        /// if (string.IsNullOrWhiteSpace(Name)) throw new Exception("Plugin name is not specified")
        /// </summary>
        /// <param name="request">The registration request to validate.</param>
        /// <returns>Null if valid; error PluginResponse if invalid.</returns>
        private PluginResponse? ValidateRegisterRequest(RegisterPluginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                _logger.LogWarning("Plugin registration validation failed: Name is required");
                return new PluginResponse
                {
                    Success = false,
                    Message = "Plugin name is required"
                };
            }

            if (string.IsNullOrWhiteSpace(request.Prefix))
            {
                _logger.LogWarning("Plugin registration validation failed: Prefix is required");
                return new PluginResponse
                {
                    Success = false,
                    Message = "Plugin prefix is required"
                };
            }

            if (request.Version < 0)
            {
                _logger.LogWarning(
                    "Plugin registration validation failed: Version must be >= 0, got {Version}",
                    request.Version);
                return new PluginResponse
                {
                    Success = false,
                    Message = "Plugin version must be greater than or equal to zero"
                };
            }

            // Validate Name format: alphanumeric, hyphens, underscores, dots — no whitespace
            foreach (var ch in request.Name)
            {
                if (char.IsWhiteSpace(ch))
                {
                    _logger.LogWarning(
                        "Plugin registration validation failed: Name contains whitespace: {PluginName}",
                        request.Name);
                    return new PluginResponse
                    {
                        Success = false,
                        Message = "Plugin name must not contain whitespace characters"
                    };
                }
            }

            return null; // Valid
        }

        #endregion

        #region Private Helpers — Partial Update

        /// <summary>
        /// Applies partial update semantics to an existing plugin.
        /// Only non-null fields from the request are applied to the plugin.
        /// Matches the UpdatePluginRequest DTO where all fields are optional (nullable).
        /// </summary>
        /// <param name="plugin">The existing plugin to update.</param>
        /// <param name="request">The update request with optional fields.</param>
        private static void ApplyPartialUpdate(Plugin plugin, UpdatePluginRequest request)
        {
            if (request.Name != null)
                plugin.Name = request.Name;

            if (request.Prefix != null)
                plugin.Prefix = request.Prefix;

            if (request.Url != null)
                plugin.Url = request.Url;

            if (request.Description != null)
                plugin.Description = request.Description;

            if (request.Version.HasValue)
                plugin.Version = request.Version.Value;

            if (request.Company != null)
                plugin.Company = request.Company;

            if (request.CompanyUrl != null)
                plugin.CompanyUrl = request.CompanyUrl;

            if (request.Author != null)
                plugin.Author = request.Author;

            if (request.Repository != null)
                plugin.Repository = request.Repository;

            if (request.License != null)
                plugin.License = request.License;

            if (request.SettingsUrl != null)
                plugin.SettingsUrl = request.SettingsUrl;

            if (request.PluginPageUrl != null)
                plugin.PluginPageUrl = request.PluginPageUrl;

            if (request.IconUrl != null)
                plugin.IconUrl = request.IconUrl;

            if (request.Status.HasValue)
                plugin.Status = request.Status.Value;
        }

        #endregion

        #region Private Helpers — SNS Domain Event Publishing

        /// <summary>
        /// Publishes a domain event to the configured SNS topic.
        /// Replaces the monolith's synchronous hook system:
        /// - Source: HookManager.GetHookedInstances&lt;IErpPostCreateRecordHook&gt;() → synchronous in-process
        /// - Target: SNS publish for async domain events
        /// - AAP Section 0.7.2: Post-hooks → SNS events
        /// 
        /// Event publishing errors are logged but do NOT block the calling operation.
        /// This ensures plugin CRUD operations succeed even if SNS is temporarily unavailable.
        /// </summary>
        /// <param name="eventType">Event type following {domain}.{entity}.{action} convention.</param>
        /// <param name="pluginId">The plugin's unique identifier.</param>
        /// <param name="pluginName">The plugin's human-readable name.</param>
        /// <param name="cancellationToken">Cancellation token for async operation.</param>
        private async Task PublishDomainEventAsync(
            string eventType,
            Guid pluginId,
            string pluginName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_snsTopicArn))
            {
                _logger.LogWarning(
                    "SNS topic ARN not configured ({EnvVar}), skipping domain event: {EventType}",
                    SnsTopicArnEnvVar,
                    eventType);
                return;
            }

            try
            {
                // Extract action from event type (e.g., "registered" from "plugin.plugin.registered")
                var action = eventType.Substring(eventType.LastIndexOf('.') + 1);
                var correlationId = Guid.NewGuid().ToString();

                // Build structured JSON event payload using AOT-safe source-generated context
                var eventPayload = new PluginDomainEvent
                {
                    EventType = eventType,
                    PluginId = pluginId.ToString(),
                    PluginName = pluginName,
                    Action = action,
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    CorrelationId = correlationId
                };

                var messageBody = JsonSerializer.Serialize(eventPayload, PluginJsonContext.Default.PluginDomainEvent);

                var publishRequest = new PublishRequest
                {
                    TopicArn = _snsTopicArn,
                    Message = messageBody,
                    MessageAttributes =
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
                        }
                    }
                };

                var response = await _snsClient
                    .PublishAsync(publishRequest, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Domain event published: {EventType} for plugin {PluginId} {PluginName}, MessageId: {MessageId}, CorrelationId: {CorrelationId}",
                    eventType,
                    pluginId,
                    pluginName,
                    response.MessageId,
                    correlationId);
            }
            catch (Exception ex)
            {
                // Log error but do NOT throw — event publishing should not block plugin operations
                _logger.LogError(
                    ex,
                    "Failed to publish domain event: {EventType} for plugin {PluginId} {PluginName}",
                    eventType,
                    pluginId,
                    pluginName);
            }
        }

        #endregion
    }
}
