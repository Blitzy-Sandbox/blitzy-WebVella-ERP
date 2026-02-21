using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace WebVellaErp.PluginSystem.Models
{
    /// <summary>
    /// Represents the activation state of a plugin in the Plugin / Extension System.
    /// Serialized as string values ("Active", "Inactive") for JSON API compatibility
    /// using System.Text.Json's JsonStringEnumConverter (AOT-compatible).
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PluginStatus
    {
        /// <summary>
        /// Plugin is active and functional within the system.
        /// </summary>
        Active = 0,

        /// <summary>
        /// Plugin is deactivated and will not be loaded or executed.
        /// </summary>
        Inactive = 1
    }

    /// <summary>
    /// Core domain model representing a plugin/extension in the Plugin System microservice.
    /// 
    /// Extracted from the monolith's abstract <c>WebVella.Erp.ErpPlugin</c> class (ErpPlugin.cs, lines 12-51),
    /// which defined 13 virtual properties with [JsonProperty] attributes for plugin metadata serialization.
    /// 
    /// Key differences from the source monolith:
    /// - Concrete POCO class (source was abstract with virtual/protected set properties)
    /// - Dual-attribute serialization: System.Text.Json (primary, AOT-compatible) + Newtonsoft.Json (backward compat)
    /// - Added Id, Status, CreatedAt, UpdatedAt properties for microservices architecture
    /// - No database dependencies (source had GetPluginData/SavePluginData accessing PostgreSQL plugin_data table)
    /// - No lifecycle methods (Initialize, SetAutoMapperConfiguration, GetJobTypes belong to service layer)
    /// 
    /// All 13 original JSON property names are preserved exactly for backward compatibility:
    /// "name", "prefix", "url", "description", "version", "company", "company_url",
    /// "author", "repository", "license", "settings_url", "plugin_page_url", "icon_url"
    /// </summary>
    public class Plugin
    {
        /// <summary>
        /// Unique identifier for the plugin. NEW property not present in source ErpPlugin.
        /// Used as the DynamoDB hash key in the plugin registry table.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonProperty(PropertyName = "id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Human-readable name of the plugin.
        /// Source: ErpPlugin.cs line 14-15 — [JsonProperty(PropertyName = "name")] public virtual string Name
        /// </summary>
        [JsonPropertyName("name")]
        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; }

        /// <summary>
        /// Short prefix identifier used for namespacing plugin resources (e.g., entity names, field names).
        /// Source: ErpPlugin.cs line 17-18 — [JsonProperty(PropertyName = "prefix")] public virtual string Prefix
        /// </summary>
        [JsonPropertyName("prefix")]
        [JsonProperty(PropertyName = "prefix")]
        public string Prefix { get; set; }

        /// <summary>
        /// URL for the plugin's homepage or documentation.
        /// Source: ErpPlugin.cs line 20-21 — [JsonProperty(PropertyName = "url")] public virtual string Url
        /// </summary>
        [JsonPropertyName("url")]
        [JsonProperty(PropertyName = "url")]
        public string Url { get; set; }

        /// <summary>
        /// Detailed description of the plugin's functionality and purpose.
        /// Source: ErpPlugin.cs line 23-24 — [JsonProperty(PropertyName = "description")] public virtual string Description
        /// </summary>
        [JsonPropertyName("description")]
        [JsonProperty(PropertyName = "description")]
        public string Description { get; set; }

        /// <summary>
        /// Plugin schema/version number for tracking updates and migrations.
        /// Matches the PluginSettings.Version pattern from SDK and MicrosoftCDM plugins.
        /// Source: ErpPlugin.cs line 26-27 — [JsonProperty(PropertyName = "version")] public virtual int Version
        /// </summary>
        [JsonPropertyName("version")]
        [JsonProperty(PropertyName = "version")]
        public int Version { get; set; }

        /// <summary>
        /// Name of the company or organization that developed the plugin.
        /// Source: ErpPlugin.cs line 29-30 — [JsonProperty(PropertyName = "company")] public virtual string Company
        /// </summary>
        [JsonPropertyName("company")]
        [JsonProperty(PropertyName = "company")]
        public string Company { get; set; }

        /// <summary>
        /// URL for the plugin developer's company website.
        /// Source: ErpPlugin.cs line 32-33 — [JsonProperty(PropertyName = "company_url")] public virtual string CompanyUrl
        /// Note: JSON property name uses snake_case ("company_url") for backward compatibility.
        /// </summary>
        [JsonPropertyName("company_url")]
        [JsonProperty(PropertyName = "company_url")]
        public string CompanyUrl { get; set; }

        /// <summary>
        /// Name of the plugin's primary author.
        /// Source: ErpPlugin.cs line 35-36 — [JsonProperty(PropertyName = "author")] public virtual string Author
        /// </summary>
        [JsonPropertyName("author")]
        [JsonProperty(PropertyName = "author")]
        public string Author { get; set; }

        /// <summary>
        /// URL to the source code repository for the plugin.
        /// Source: ErpPlugin.cs line 38-39 — [JsonProperty(PropertyName = "repository")] public virtual string Repository
        /// </summary>
        [JsonPropertyName("repository")]
        [JsonProperty(PropertyName = "repository")]
        public string Repository { get; set; }

        /// <summary>
        /// License identifier for the plugin (e.g., "MIT", "Apache-2.0").
        /// Source: ErpPlugin.cs line 41-42 — [JsonProperty(PropertyName = "license")] public virtual string License
        /// </summary>
        [JsonPropertyName("license")]
        [JsonProperty(PropertyName = "license")]
        public string License { get; set; }

        /// <summary>
        /// URL path to the plugin's settings/configuration page in the admin UI.
        /// Source: ErpPlugin.cs line 44-45 — [JsonProperty(PropertyName = "settings_url")] public virtual string SettingsUrl
        /// Note: JSON property name uses snake_case ("settings_url") for backward compatibility.
        /// </summary>
        [JsonPropertyName("settings_url")]
        [JsonProperty(PropertyName = "settings_url")]
        public string SettingsUrl { get; set; }

        /// <summary>
        /// URL path to the plugin's dedicated admin page.
        /// Source: ErpPlugin.cs line 47-48 — [JsonProperty(PropertyName = "plugin_page_url")] public virtual string PluginPageUrl
        /// Note: JSON property name uses snake_case ("plugin_page_url") for backward compatibility.
        /// </summary>
        [JsonPropertyName("plugin_page_url")]
        [JsonProperty(PropertyName = "plugin_page_url")]
        public string PluginPageUrl { get; set; }

        /// <summary>
        /// URL or path to the plugin's icon image for display in the admin UI.
        /// Source: ErpPlugin.cs line 50-51 — [JsonProperty(PropertyName = "icon_url")] public virtual string IconUrl
        /// Note: JSON property name uses snake_case ("icon_url") for backward compatibility.
        /// </summary>
        [JsonPropertyName("icon_url")]
        [JsonProperty(PropertyName = "icon_url")]
        public string IconUrl { get; set; }

        /// <summary>
        /// Current activation state of the plugin. NEW property for the microservices architecture.
        /// Controls whether the plugin is loaded and executed by the system.
        /// Defaults to <see cref="PluginStatus.Active"/> for newly registered plugins.
        /// </summary>
        [JsonPropertyName("status")]
        [JsonProperty(PropertyName = "status")]
        public PluginStatus Status { get; set; }

        /// <summary>
        /// UTC timestamp when the plugin was first registered in the system.
        /// NEW property for the microservices architecture — replaces the implicit
        /// creation tracking that was embedded in the PostgreSQL plugin_data table.
        /// </summary>
        [JsonPropertyName("created_at")]
        [JsonProperty(PropertyName = "created_at")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// UTC timestamp when the plugin metadata was last modified.
        /// NEW property for the microservices architecture — enables optimistic concurrency
        /// and audit trail for plugin configuration changes.
        /// </summary>
        [JsonPropertyName("updated_at")]
        [JsonProperty(PropertyName = "updated_at")]
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class with safe defaults.
        /// All string properties default to <see cref="string.Empty"/>, Id to <see cref="Guid.Empty"/>,
        /// Version to 0, Status to <see cref="PluginStatus.Active"/>, and timestamps to <see cref="DateTime.UtcNow"/>.
        /// </summary>
        public Plugin()
        {
            Id = Guid.Empty;
            Name = string.Empty;
            Prefix = string.Empty;
            Url = string.Empty;
            Description = string.Empty;
            Version = 0;
            Company = string.Empty;
            CompanyUrl = string.Empty;
            Author = string.Empty;
            Repository = string.Empty;
            License = string.Empty;
            SettingsUrl = string.Empty;
            PluginPageUrl = string.Empty;
            IconUrl = string.Empty;
            Status = PluginStatus.Active;
            CreatedAt = DateTime.UtcNow;
            UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Request DTO for registering a new plugin in the Plugin System.
    /// Used by the PluginHandler Lambda function for POST /v1/plugins operations.
    /// 
    /// Required fields: Name, Prefix, Version.
    /// Optional fields: all URL/description/company/author metadata (nullable).
    /// 
    /// The dual-attribute serialization pattern ensures compatibility with both
    /// System.Text.Json (Lambda Native AOT) and Newtonsoft.Json (backward compat).
    /// </summary>
    public class RegisterPluginRequest
    {
        /// <summary>
        /// Human-readable name of the plugin. Required.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Short prefix identifier for namespacing plugin resources. Required.
        /// </summary>
        [JsonPropertyName("prefix")]
        [JsonProperty(PropertyName = "prefix")]
        public string Prefix { get; set; } = string.Empty;

        /// <summary>
        /// URL for the plugin's homepage or documentation. Optional.
        /// </summary>
        [JsonPropertyName("url")]
        [JsonProperty(PropertyName = "url")]
        public string? Url { get; set; }

        /// <summary>
        /// Detailed description of the plugin's functionality. Optional.
        /// </summary>
        [JsonPropertyName("description")]
        [JsonProperty(PropertyName = "description")]
        public string? Description { get; set; }

        /// <summary>
        /// Plugin schema/version number. Required.
        /// </summary>
        [JsonPropertyName("version")]
        [JsonProperty(PropertyName = "version")]
        public int Version { get; set; }

        /// <summary>
        /// Name of the developing company or organization. Optional.
        /// </summary>
        [JsonPropertyName("company")]
        [JsonProperty(PropertyName = "company")]
        public string? Company { get; set; }

        /// <summary>
        /// URL for the developer's company website. Optional.
        /// </summary>
        [JsonPropertyName("company_url")]
        [JsonProperty(PropertyName = "company_url")]
        public string? CompanyUrl { get; set; }

        /// <summary>
        /// Name of the plugin's primary author. Optional.
        /// </summary>
        [JsonPropertyName("author")]
        [JsonProperty(PropertyName = "author")]
        public string? Author { get; set; }

        /// <summary>
        /// URL to the source code repository. Optional.
        /// </summary>
        [JsonPropertyName("repository")]
        [JsonProperty(PropertyName = "repository")]
        public string? Repository { get; set; }

        /// <summary>
        /// License identifier (e.g., "MIT", "Apache-2.0"). Optional.
        /// </summary>
        [JsonPropertyName("license")]
        [JsonProperty(PropertyName = "license")]
        public string? License { get; set; }

        /// <summary>
        /// URL path to the plugin's settings page. Optional.
        /// </summary>
        [JsonPropertyName("settings_url")]
        [JsonProperty(PropertyName = "settings_url")]
        public string? SettingsUrl { get; set; }

        /// <summary>
        /// URL path to the plugin's dedicated admin page. Optional.
        /// </summary>
        [JsonPropertyName("plugin_page_url")]
        [JsonProperty(PropertyName = "plugin_page_url")]
        public string? PluginPageUrl { get; set; }

        /// <summary>
        /// URL or path to the plugin's icon image. Optional.
        /// </summary>
        [JsonPropertyName("icon_url")]
        [JsonProperty(PropertyName = "icon_url")]
        public string? IconUrl { get; set; }
    }

    /// <summary>
    /// Request DTO for updating an existing plugin's metadata in the Plugin System.
    /// Used by the PluginHandler Lambda function for PUT /v1/plugins/{id} operations.
    /// 
    /// All fields are optional to support partial updates — only provided fields
    /// will be applied to the existing plugin record. Null values indicate no change.
    /// 
    /// The dual-attribute serialization pattern ensures compatibility with both
    /// System.Text.Json (Lambda Native AOT) and Newtonsoft.Json (backward compat).
    /// </summary>
    public class UpdatePluginRequest
    {
        /// <summary>
        /// Updated human-readable name. Null means no change.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonProperty(PropertyName = "name")]
        public string? Name { get; set; }

        /// <summary>
        /// Updated prefix identifier. Null means no change.
        /// </summary>
        [JsonPropertyName("prefix")]
        [JsonProperty(PropertyName = "prefix")]
        public string? Prefix { get; set; }

        /// <summary>
        /// Updated homepage URL. Null means no change.
        /// </summary>
        [JsonPropertyName("url")]
        [JsonProperty(PropertyName = "url")]
        public string? Url { get; set; }

        /// <summary>
        /// Updated description. Null means no change.
        /// </summary>
        [JsonPropertyName("description")]
        [JsonProperty(PropertyName = "description")]
        public string? Description { get; set; }

        /// <summary>
        /// Updated version number. Null means no change.
        /// </summary>
        [JsonPropertyName("version")]
        [JsonProperty(PropertyName = "version")]
        public int? Version { get; set; }

        /// <summary>
        /// Updated company name. Null means no change.
        /// </summary>
        [JsonPropertyName("company")]
        [JsonProperty(PropertyName = "company")]
        public string? Company { get; set; }

        /// <summary>
        /// Updated company URL. Null means no change.
        /// </summary>
        [JsonPropertyName("company_url")]
        [JsonProperty(PropertyName = "company_url")]
        public string? CompanyUrl { get; set; }

        /// <summary>
        /// Updated author name. Null means no change.
        /// </summary>
        [JsonPropertyName("author")]
        [JsonProperty(PropertyName = "author")]
        public string? Author { get; set; }

        /// <summary>
        /// Updated repository URL. Null means no change.
        /// </summary>
        [JsonPropertyName("repository")]
        [JsonProperty(PropertyName = "repository")]
        public string? Repository { get; set; }

        /// <summary>
        /// Updated license identifier. Null means no change.
        /// </summary>
        [JsonPropertyName("license")]
        [JsonProperty(PropertyName = "license")]
        public string? License { get; set; }

        /// <summary>
        /// Updated settings page URL. Null means no change.
        /// </summary>
        [JsonPropertyName("settings_url")]
        [JsonProperty(PropertyName = "settings_url")]
        public string? SettingsUrl { get; set; }

        /// <summary>
        /// Updated plugin admin page URL. Null means no change.
        /// </summary>
        [JsonPropertyName("plugin_page_url")]
        [JsonProperty(PropertyName = "plugin_page_url")]
        public string? PluginPageUrl { get; set; }

        /// <summary>
        /// Updated icon URL. Null means no change.
        /// </summary>
        [JsonPropertyName("icon_url")]
        [JsonProperty(PropertyName = "icon_url")]
        public string? IconUrl { get; set; }

        /// <summary>
        /// Updated activation status. Null means no change.
        /// Enables plugin activation/deactivation via PATCH semantics.
        /// </summary>
        [JsonPropertyName("status")]
        [JsonProperty(PropertyName = "status")]
        public PluginStatus? Status { get; set; }
    }

    /// <summary>
    /// Response DTO for single-plugin operations (create, read, update, delete).
    /// Wraps a <see cref="Plugin"/> instance with success/failure metadata.
    /// Used by the PluginHandler Lambda function for all single-plugin API responses.
    /// </summary>
    public class PluginResponse
    {
        /// <summary>
        /// The plugin data returned by the operation. May be null if the operation failed
        /// (e.g., plugin not found for GET /v1/plugins/{id}).
        /// </summary>
        [JsonPropertyName("plugin")]
        [JsonProperty(PropertyName = "plugin")]
        public Plugin? Plugin { get; set; }

        /// <summary>
        /// Indicates whether the operation completed successfully.
        /// </summary>
        [JsonPropertyName("success")]
        [JsonProperty(PropertyName = "success")]
        public bool Success { get; set; }

        /// <summary>
        /// Optional human-readable message providing additional context about the operation result.
        /// Typically populated for error responses (e.g., "Plugin not found", "Validation failed").
        /// </summary>
        [JsonPropertyName("message")]
        [JsonProperty(PropertyName = "message")]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Response DTO for paginated plugin list operations.
    /// Wraps a collection of <see cref="Plugin"/> instances with pagination metadata and success/failure status.
    /// Used by the PluginHandler Lambda function for GET /v1/plugins list API responses.
    /// </summary>
    public class PluginListResponse
    {
        /// <summary>
        /// The list of plugins matching the query criteria.
        /// Empty list (never null) when no plugins match or the operation fails.
        /// </summary>
        [JsonPropertyName("plugins")]
        [JsonProperty(PropertyName = "plugins")]
        public List<Plugin> Plugins { get; set; } = new List<Plugin>();

        /// <summary>
        /// Total number of plugins matching the query criteria (before pagination).
        /// Used by the frontend for pagination controls.
        /// </summary>
        [JsonPropertyName("total_count")]
        [JsonProperty(PropertyName = "total_count")]
        public int TotalCount { get; set; }

        /// <summary>
        /// Indicates whether the operation completed successfully.
        /// </summary>
        [JsonPropertyName("success")]
        [JsonProperty(PropertyName = "success")]
        public bool Success { get; set; }

        /// <summary>
        /// Optional human-readable message providing additional context about the operation result.
        /// </summary>
        [JsonPropertyName("message")]
        [JsonProperty(PropertyName = "message")]
        public string? Message { get; set; }
    }
}
