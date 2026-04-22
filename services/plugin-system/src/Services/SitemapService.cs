using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;

namespace WebVellaErp.PluginSystem.Services
{
    /// <summary>
    /// Result type for sitemap CRUD operations, mirroring the monolith's ResponseModel pattern
    /// from AdminController.cs (lines 88-91 error handling, lines 94-98 success response).
    /// </summary>
    public class SitemapOperationResult
    {
        /// <summary>Indicates whether the operation succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Human-readable message (error description on failure).</summary>
        public string? Message { get; set; }

        /// <summary>Ordered sitemap data returned after mutations (areas/nodes sorted by weight).</summary>
        public object? Sitemap { get; set; }

        /// <summary>Dictionary mapping NodeId to list of PageIds bound to that node.</summary>
        public Dictionary<Guid, List<Guid>>? NodePageDictionary { get; set; }
    }

    /// <summary>
    /// App metadata persisted in DynamoDB. Replaces the monolith's App model from AppService.
    /// DynamoDB key: PK=APP#{Id}, SK=META
    /// </summary>
    public class AppRecord
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? IconClass { get; set; }
        public string? Color { get; set; }
        public int Weight { get; set; }
        public List<Guid> AccessRoles { get; set; } = new();
    }

    /// <summary>
    /// Area metadata persisted in DynamoDB. Replaces the monolith's SitemapArea model.
    /// DynamoDB key: PK=APP#{AppId}, SK=AREA#{Id}
    /// Source: AdminController.cs lines 56-198 (CreateSitemapArea/UpdateSitemapArea/DeleteSitemapArea).
    /// </summary>
    public class AreaRecord
    {
        public Guid Id { get; set; }
        public Guid AppId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public Dictionary<string, string> LabelTranslations { get; set; } = new();
        public string? Description { get; set; }
        public Dictionary<string, string> DescriptionTranslations { get; set; } = new();
        public string? IconClass { get; set; }
        public string? Color { get; set; }
        public int Weight { get; set; }
        public bool ShowGroupNames { get; set; }
        public List<Guid> AccessRoles { get; set; } = new();
    }

    /// <summary>
    /// Node metadata persisted in DynamoDB. Replaces the monolith's SitemapNode model.
    /// DynamoDB key: PK=APP#{AppId}, SK=NODE#{Id}
    /// Source: AdminController.cs lines 201-420 (CreateSitemapNode/UpdateSitemapNode/DeleteSitemapNode).
    /// </summary>
    public class NodeRecord
    {
        public Guid Id { get; set; }
        public Guid AppId { get; set; }
        public Guid AreaId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public Dictionary<string, string> LabelTranslations { get; set; } = new();
        public string? IconClass { get; set; }
        public string? Url { get; set; }
        public int Type { get; set; }
        public Guid? EntityId { get; set; }
        public int Weight { get; set; }
        public List<Guid> AccessRoles { get; set; } = new();
        public List<Guid> EntityListPages { get; set; } = new();
        public List<Guid> EntityCreatePages { get; set; } = new();
        public List<Guid> EntityDetailsPages { get; set; } = new();
        public List<Guid> EntityManagePages { get; set; } = new();
        public Guid? ParentId { get; set; }
        public List<Guid> PageIds { get; set; } = new();
    }

    /// <summary>
    /// Auxiliary data required by the node editor UI.
    /// Source: AdminController.cs lines 422-515 (GetNodeAuxData action).
    /// </summary>
    public class NodeAuxData
    {
        /// <summary>All available entities as select options.</summary>
        public List<SelectOptionRecord> AllEntities { get; set; } = new();

        /// <summary>Available node type options (Default, EntityList, Application).</summary>
        public List<SelectOptionRecord> NodeTypes { get; set; } = new();

        /// <summary>Unattached app pages available for node binding.</summary>
        public List<PageRecord> AppPages { get; set; } = new();

        /// <summary>All entity-specific pages across the application.</summary>
        public List<PageRecord> AllEntityPages { get; set; } = new();
    }

    /// <summary>
    /// Simple key-value pair for select option dropdowns.
    /// Replaces SelectOption from the monolith's entity management system.
    /// </summary>
    public class SelectOptionRecord
    {
        public string Value { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    /// <summary>
    /// Page metadata record for sitemap page bindings.
    /// DynamoDB key: PK=APP#{AppId}, SK=PAGE#{Id}
    /// </summary>
    public class PageRecord
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Guid? NodeId { get; set; }
        public Guid? AreaId { get; set; }
        public Guid? EntityId { get; set; }
        public int Type { get; set; }
        public Guid? AppId { get; set; }
    }

    /// <summary>
    /// Domain event payload for sitemap change events published to SNS.
    /// Used for AOT-compatible source-generated JSON serialization.
    /// </summary>
    public sealed class SitemapDomainEvent
    {
        public string EntityId { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Source-generated JSON serializer context for AOT-compatible serialization
    /// of domain event payloads. Avoids IL2026/IL3050 trimming warnings.
    /// </summary>
    [JsonSerializable(typeof(SitemapDomainEvent))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal partial class SitemapJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Service interface for app/sitemap metadata management operations.
    /// Provides full CRUD for apps, areas, and nodes with DynamoDB persistence
    /// and SNS domain event publishing. Designed for DI injection into Lambda handlers.
    /// </summary>
    public interface ISitemapService
    {
        /// <summary>Creates a new application record in DynamoDB.</summary>
        Task<SitemapOperationResult> CreateAppAsync(Guid appId, string name, string label, string? description, string? iconClass, string? color, int weight, List<Guid>? accessRoles, CancellationToken cancellationToken = default);

        /// <summary>Updates an existing application record.</summary>
        Task<SitemapOperationResult> UpdateAppAsync(Guid appId, string name, string label, string? description, string? iconClass, string? color, int weight, List<Guid>? accessRoles, CancellationToken cancellationToken = default);

        /// <summary>Deletes an app and cascades to all areas, nodes, and page bindings.</summary>
        Task<SitemapOperationResult> DeleteAppAsync(Guid appId, CancellationToken cancellationToken = default);

        /// <summary>Gets a single app by its ID.</summary>
        Task<AppRecord?> GetAppByIdAsync(Guid appId, CancellationToken cancellationToken = default);

        /// <summary>Lists all applications ordered by weight.</summary>
        Task<List<AppRecord>> ListAppsAsync(CancellationToken cancellationToken = default);

        /// <summary>Creates a new sitemap area within an application.</summary>
        Task<SitemapOperationResult> CreateAreaAsync(Guid areaId, Guid appId, string name, string label, Dictionary<string, string>? labelTranslations, string? description, Dictionary<string, string>? descriptionTranslations, string? iconClass, string? color, int weight, bool showGroupNames, List<Guid>? accessRoles, CancellationToken cancellationToken = default);

        /// <summary>Updates an existing sitemap area.</summary>
        Task<SitemapOperationResult> UpdateAreaAsync(Guid areaId, Guid appId, string name, string label, Dictionary<string, string>? labelTranslations, string? description, Dictionary<string, string>? descriptionTranslations, string? iconClass, string? color, int weight, bool showGroupNames, List<Guid>? accessRoles, CancellationToken cancellationToken = default);

        /// <summary>Deletes a sitemap area and cascades to child nodes.</summary>
        Task<SitemapOperationResult> DeleteAreaAsync(Guid areaId, Guid appId, CancellationToken cancellationToken = default);

        /// <summary>Creates a new sitemap node within an area, with page attachments.</summary>
        Task<SitemapOperationResult> CreateNodeAsync(Guid nodeId, Guid appId, Guid areaId, string name, string label, Dictionary<string, string>? labelTranslations, string? iconClass, string? url, int type, Guid? entityId, int weight, List<Guid>? accessRoles, List<Guid>? entityListPages, List<Guid>? entityCreatePages, List<Guid>? entityDetailsPages, List<Guid>? entityManagePages, Guid? parentId, List<Guid>? pageIds, CancellationToken cancellationToken = default);

        /// <summary>Updates an existing sitemap node with page attach/detach diff computation.</summary>
        Task<SitemapOperationResult> UpdateNodeAsync(Guid nodeId, Guid appId, Guid areaId, string name, string label, Dictionary<string, string>? labelTranslations, string? iconClass, string? url, int type, Guid? entityId, int weight, List<Guid>? accessRoles, List<Guid>? entityListPages, List<Guid>? entityCreatePages, List<Guid>? entityDetailsPages, List<Guid>? entityManagePages, Guid? parentId, List<Guid>? pageIds, CancellationToken cancellationToken = default);

        /// <summary>Deletes a sitemap node and detaches all bound pages.</summary>
        Task<SitemapOperationResult> DeleteNodeAsync(Guid nodeId, Guid appId, CancellationToken cancellationToken = default);

        /// <summary>Returns auxiliary data for the node editor (entities, node types, pages).</summary>
        Task<NodeAuxData> GetNodeAuxDataAsync(Guid appId, CancellationToken cancellationToken = default);

        /// <summary>Returns the ordered sitemap tree with node-page dictionary for an application.</summary>
        Task<object> GetOrderedSitemapAsync(Guid appId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Service implementation for app/sitemap metadata management.
    /// Replaces the monolith's AdminController.cs sitemap CRUD operations (lines 53-515)
    /// with DynamoDB persistence and SNS domain event publishing.
    /// Constructor pattern replaces AdminController's inline service instantiation (lines 28-35).
    /// </summary>
    public class SitemapService : ISitemapService
    {
        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<SitemapService> _logger;
        private readonly string _tableName;
        private readonly string _snsTopicArn;

        // DynamoDB sort key prefixes for the single-table design
        private const string SkMeta = "META";
        private const string SkAreaPrefix = "AREA#";
        private const string SkNodePrefix = "NODE#";
        private const string SkPagePrefix = "PAGE#";

        /// <summary>
        /// Initializes the SitemapService with required AWS clients and logger.
        /// Reads table name and SNS topic ARN from environment variables.
        /// </summary>
        public SitemapService(
            IAmazonDynamoDB dynamoDbClient,
            IAmazonSimpleNotificationService snsClient,
            ILogger<SitemapService> logger)
        {
            _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tableName = Environment.GetEnvironmentVariable("PLUGIN_SYSTEM_TABLE_NAME") ?? "plugin-system-table";
            _snsTopicArn = Environment.GetEnvironmentVariable("PLUGIN_SYSTEM_SNS_TOPIC_ARN") ?? string.Empty;
        }

        #region DynamoDB Key Helpers

        /// <summary>Generates the partition key for an application.</summary>
        private static string AppPk(Guid appId) => $"APP#{appId}";

        /// <summary>Generates the sort key for area metadata.</summary>
        private static string AreaSk(Guid areaId) => $"{SkAreaPrefix}{areaId}";

        /// <summary>Generates the sort key for node metadata.</summary>
        private static string NodeSk(Guid nodeId) => $"{SkNodePrefix}{nodeId}";

        /// <summary>Generates the sort key for page binding metadata.</summary>
        private static string PageSk(Guid pageId) => $"{SkPagePrefix}{pageId}";

        #endregion

        #region Marshalling Helpers

        /// <summary>Serializes a list of GUIDs to a DynamoDB list attribute.</summary>
        private static AttributeValue SerializeGuidList(List<Guid>? guids)
        {
            if (guids == null || guids.Count == 0)
                return new AttributeValue { L = new List<AttributeValue>(), IsLSet = true };
            return new AttributeValue
            {
                L = guids.Select(g => new AttributeValue { S = g.ToString() }).ToList(),
                IsLSet = true
            };
        }

        /// <summary>Deserializes a DynamoDB list attribute to a list of GUIDs.</summary>
        private static List<Guid> DeserializeGuidList(Dictionary<string, AttributeValue> item, string key)
        {
            if (!item.TryGetValue(key, out var attr) || attr.L == null)
                return new List<Guid>();
            var result = new List<Guid>();
            foreach (var element in attr.L)
            {
                if (Guid.TryParse(element.S, out var g))
                    result.Add(g);
            }
            return result;
        }

        /// <summary>Serializes a string dictionary to a DynamoDB map attribute.</summary>
        private static AttributeValue SerializeStringDict(Dictionary<string, string>? dict)
        {
            if (dict == null || dict.Count == 0)
                return new AttributeValue { M = new Dictionary<string, AttributeValue>(), IsMSet = true };
            return new AttributeValue
            {
                M = dict.ToDictionary(kv => kv.Key, kv => new AttributeValue { S = kv.Value }),
                IsMSet = true
            };
        }

        /// <summary>Deserializes a DynamoDB map attribute to a string dictionary.</summary>
        private static Dictionary<string, string> DeserializeStringDict(Dictionary<string, AttributeValue> item, string key)
        {
            if (!item.TryGetValue(key, out var attr))
                return new Dictionary<string, string>();

            // Primary path: DynamoDB Map attribute (M) — used when stored as native Map type
            if (attr.M != null && attr.M.Count > 0)
            {
                return attr.M
                    .Where(kv => kv.Value.S != null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.S);
            }

            // Fallback path: JSON string attribute (S) — used when stored as serialized JSON string
            if (attr.S != null)
            {
                return JsonSerializer.Deserialize(attr.S, SitemapJsonContext.Default.DictionaryStringString) ?? new Dictionary<string, string>();
            }

            return new Dictionary<string, string>();
        }

        /// <summary>Gets a string attribute value from a DynamoDB item, returns null if missing.</summary>
        private static string? GetString(Dictionary<string, AttributeValue> item, string key)
        {
            return item.TryGetValue(key, out var attr) ? attr.S : null;
        }

        /// <summary>Gets a required string attribute value from a DynamoDB item.</summary>
        private static string GetRequiredString(Dictionary<string, AttributeValue> item, string key)
        {
            return item.TryGetValue(key, out var attr) && attr.S != null ? attr.S : string.Empty;
        }

        /// <summary>Gets an integer attribute value from a DynamoDB item.</summary>
        private static int GetInt(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.N != null && int.TryParse(attr.N, out var val))
                return val;
            return 0;
        }

        /// <summary>Gets a boolean attribute value from a DynamoDB item.</summary>
        private static bool GetBool(Dictionary<string, AttributeValue> item, string key)
        {
            return item.TryGetValue(key, out var attr) && attr.BOOL;
        }

        /// <summary>Gets an optional GUID attribute value from a DynamoDB item.</summary>
        private static Guid? GetNullableGuid(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null && Guid.TryParse(attr.S, out var g))
                return g;
            return null;
        }

        /// <summary>Gets a required GUID attribute value from a DynamoDB item.</summary>
        private static Guid GetGuid(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null && Guid.TryParse(attr.S, out var g))
                return g;
            return Guid.Empty;
        }

        #endregion

        #region Marshal/Unmarshal Records

        /// <summary>Marshals an AppRecord into a DynamoDB item dictionary.</summary>
        private Dictionary<string, AttributeValue> MarshalApp(AppRecord app)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = AppPk(app.Id) },
                ["SK"] = new AttributeValue { S = SkMeta },
                ["EntityType"] = new AttributeValue { S = "App" },
                ["Id"] = new AttributeValue { S = app.Id.ToString() },
                ["Name"] = new AttributeValue { S = app.Name },
                ["Label"] = new AttributeValue { S = app.Label },
                ["Weight"] = new AttributeValue { N = app.Weight.ToString() },
                ["AccessRoles"] = SerializeGuidList(app.AccessRoles),
                ["UpdatedOn"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") }
            };
            if (app.Description != null)
                item["Description"] = new AttributeValue { S = app.Description };
            if (app.IconClass != null)
                item["IconClass"] = new AttributeValue { S = app.IconClass };
            if (app.Color != null)
                item["Color"] = new AttributeValue { S = app.Color };
            return item;
        }

        /// <summary>Unmarshals a DynamoDB item into an AppRecord.</summary>
        private static AppRecord UnmarshalApp(Dictionary<string, AttributeValue> item)
        {
            return new AppRecord
            {
                Id = GetGuid(item, "Id"),
                Name = GetRequiredString(item, "Name"),
                Label = GetRequiredString(item, "Label"),
                Description = GetString(item, "Description"),
                IconClass = GetString(item, "IconClass"),
                Color = GetString(item, "Color"),
                Weight = GetInt(item, "Weight"),
                AccessRoles = DeserializeGuidList(item, "AccessRoles")
            };
        }

        /// <summary>Marshals an AreaRecord into a DynamoDB item dictionary.</summary>
        private Dictionary<string, AttributeValue> MarshalArea(AreaRecord area)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = AppPk(area.AppId) },
                ["SK"] = new AttributeValue { S = AreaSk(area.Id) },
                ["EntityType"] = new AttributeValue { S = "Area" },
                ["Id"] = new AttributeValue { S = area.Id.ToString() },
                ["AppId"] = new AttributeValue { S = area.AppId.ToString() },
                ["Name"] = new AttributeValue { S = area.Name },
                ["Label"] = new AttributeValue { S = area.Label },
                ["LabelTranslations"] = SerializeStringDict(area.LabelTranslations),
                ["DescriptionTranslations"] = SerializeStringDict(area.DescriptionTranslations),
                ["Weight"] = new AttributeValue { N = area.Weight.ToString() },
                ["ShowGroupNames"] = new AttributeValue { BOOL = area.ShowGroupNames },
                ["AccessRoles"] = SerializeGuidList(area.AccessRoles),
                ["UpdatedOn"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") }
            };
            if (area.Description != null)
                item["Description"] = new AttributeValue { S = area.Description };
            if (area.IconClass != null)
                item["IconClass"] = new AttributeValue { S = area.IconClass };
            if (area.Color != null)
                item["Color"] = new AttributeValue { S = area.Color };
            return item;
        }

        /// <summary>Unmarshals a DynamoDB item into an AreaRecord.</summary>
        private static AreaRecord UnmarshalArea(Dictionary<string, AttributeValue> item)
        {
            return new AreaRecord
            {
                Id = GetGuid(item, "Id"),
                AppId = GetGuid(item, "AppId"),
                Name = GetRequiredString(item, "Name"),
                Label = GetRequiredString(item, "Label"),
                LabelTranslations = DeserializeStringDict(item, "LabelTranslations"),
                Description = GetString(item, "Description"),
                DescriptionTranslations = DeserializeStringDict(item, "DescriptionTranslations"),
                IconClass = GetString(item, "IconClass"),
                Color = GetString(item, "Color"),
                Weight = GetInt(item, "Weight"),
                ShowGroupNames = GetBool(item, "ShowGroupNames"),
                AccessRoles = DeserializeGuidList(item, "AccessRoles")
            };
        }

        /// <summary>Marshals a NodeRecord into a DynamoDB item dictionary.</summary>
        private Dictionary<string, AttributeValue> MarshalNode(NodeRecord node)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = AppPk(node.AppId) },
                ["SK"] = new AttributeValue { S = NodeSk(node.Id) },
                ["EntityType"] = new AttributeValue { S = "Node" },
                ["Id"] = new AttributeValue { S = node.Id.ToString() },
                ["AppId"] = new AttributeValue { S = node.AppId.ToString() },
                ["AreaId"] = new AttributeValue { S = node.AreaId.ToString() },
                ["Name"] = new AttributeValue { S = node.Name },
                ["Label"] = new AttributeValue { S = node.Label },
                ["LabelTranslations"] = SerializeStringDict(node.LabelTranslations),
                ["Type"] = new AttributeValue { N = node.Type.ToString() },
                ["Weight"] = new AttributeValue { N = node.Weight.ToString() },
                ["AccessRoles"] = SerializeGuidList(node.AccessRoles),
                ["EntityListPages"] = SerializeGuidList(node.EntityListPages),
                ["EntityCreatePages"] = SerializeGuidList(node.EntityCreatePages),
                ["EntityDetailsPages"] = SerializeGuidList(node.EntityDetailsPages),
                ["EntityManagePages"] = SerializeGuidList(node.EntityManagePages),
                ["PageIds"] = SerializeGuidList(node.PageIds),
                ["UpdatedOn"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") }
            };
            if (node.IconClass != null)
                item["IconClass"] = new AttributeValue { S = node.IconClass };
            if (node.Url != null)
                item["Url"] = new AttributeValue { S = node.Url };
            if (node.EntityId.HasValue)
                item["EntityId"] = new AttributeValue { S = node.EntityId.Value.ToString() };
            if (node.ParentId.HasValue)
                item["ParentId"] = new AttributeValue { S = node.ParentId.Value.ToString() };
            return item;
        }

        /// <summary>Unmarshals a DynamoDB item into a NodeRecord.</summary>
        private static NodeRecord UnmarshalNode(Dictionary<string, AttributeValue> item)
        {
            return new NodeRecord
            {
                Id = GetGuid(item, "Id"),
                AppId = GetGuid(item, "AppId"),
                AreaId = GetGuid(item, "AreaId"),
                Name = GetRequiredString(item, "Name"),
                Label = GetRequiredString(item, "Label"),
                LabelTranslations = DeserializeStringDict(item, "LabelTranslations"),
                IconClass = GetString(item, "IconClass"),
                Url = GetString(item, "Url"),
                Type = GetInt(item, "Type"),
                EntityId = GetNullableGuid(item, "EntityId"),
                Weight = GetInt(item, "Weight"),
                AccessRoles = DeserializeGuidList(item, "AccessRoles"),
                EntityListPages = DeserializeGuidList(item, "EntityListPages"),
                EntityCreatePages = DeserializeGuidList(item, "EntityCreatePages"),
                EntityDetailsPages = DeserializeGuidList(item, "EntityDetailsPages"),
                EntityManagePages = DeserializeGuidList(item, "EntityManagePages"),
                ParentId = GetNullableGuid(item, "ParentId"),
                PageIds = DeserializeGuidList(item, "PageIds")
            };
        }

        /// <summary>Marshals a PageRecord into a DynamoDB item dictionary.</summary>
        private Dictionary<string, AttributeValue> MarshalPage(PageRecord page)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = AppPk(page.AppId ?? Guid.Empty) },
                ["SK"] = new AttributeValue { S = PageSk(page.Id) },
                ["EntityType"] = new AttributeValue { S = "Page" },
                ["Id"] = new AttributeValue { S = page.Id.ToString() },
                ["Name"] = new AttributeValue { S = page.Name },
                ["Type"] = new AttributeValue { N = page.Type.ToString() },
                ["UpdatedOn"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") }
            };
            if (page.NodeId.HasValue)
                item["NodeId"] = new AttributeValue { S = page.NodeId.Value.ToString() };
            if (page.AreaId.HasValue)
                item["AreaId"] = new AttributeValue { S = page.AreaId.Value.ToString() };
            if (page.EntityId.HasValue)
                item["EntityId"] = new AttributeValue { S = page.EntityId.Value.ToString() };
            if (page.AppId.HasValue)
                item["AppId"] = new AttributeValue { S = page.AppId.Value.ToString() };
            return item;
        }

        /// <summary>Unmarshals a DynamoDB item into a PageRecord.</summary>
        private static PageRecord UnmarshalPage(Dictionary<string, AttributeValue> item)
        {
            return new PageRecord
            {
                Id = GetGuid(item, "Id"),
                Name = GetRequiredString(item, "Name"),
                NodeId = GetNullableGuid(item, "NodeId"),
                AreaId = GetNullableGuid(item, "AreaId"),
                EntityId = GetNullableGuid(item, "EntityId"),
                Type = GetInt(item, "Type"),
                AppId = GetNullableGuid(item, "AppId")
            };
        }

        #endregion

        #region DynamoDB Query Helpers

        /// <summary>
        /// Queries all items sharing a partition key, optionally filtered by sort key prefix.
        /// Handles pagination automatically to return all matching items.
        /// </summary>
        private async Task<List<Dictionary<string, AttributeValue>>> QueryItemsByPartitionAsync(
            string pk,
            string? skPrefix,
            CancellationToken cancellationToken)
        {
            var results = new List<Dictionary<string, AttributeValue>>();
            Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

            do
            {
                var request = new QueryRequest
                {
                    TableName = _tableName,
                    KeyConditionExpression = skPrefix != null
                        ? "PK = :pk AND begins_with(SK, :skPrefix)"
                        : "PK = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = pk }
                    }
                };

                if (skPrefix != null)
                {
                    request.ExpressionAttributeValues[":skPrefix"] = new AttributeValue { S = skPrefix };
                }

                if (lastEvaluatedKey != null)
                {
                    request.ExclusiveStartKey = lastEvaluatedKey;
                }

                QueryResponse response = await _dynamoDbClient.QueryAsync(request, cancellationToken).ConfigureAwait(false);
                results.AddRange(response.Items);
                lastEvaluatedKey = response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null;
            }
            while (lastEvaluatedKey != null);

            return results;
        }

        #endregion

        #region SNS Event Publishing

        /// <summary>
        /// Publishes a domain event to the SNS topic for sitemap changes.
        /// Event naming follows plugin.sitemap.{action} convention per AAP Section 0.8.5.
        /// Failures are logged but do not fail the calling operation (fire-and-forget semantics).
        /// </summary>
        private async Task PublishDomainEventAsync(
            string action,
            string entityType,
            Guid entityId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_snsTopicArn))
            {
                _logger.LogWarning("SNS topic ARN not configured; skipping domain event publish for {Action}", action);
                return;
            }

            try
            {
                var eventPayload = new SitemapDomainEvent
                {
                    EntityId = entityId.ToString(),
                    EntityType = entityType,
                    Action = action,
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    CorrelationId = Guid.NewGuid().ToString()
                };

                var publishRequest = new PublishRequest
                {
                    TopicArn = _snsTopicArn,
                    Message = JsonSerializer.Serialize(eventPayload, SitemapJsonContext.Default.SitemapDomainEvent),
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = $"plugin.sitemap.{action}"
                        },
                        ["entityType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = entityType
                        }
                    }
                };

                PublishResponse publishResponse = await _snsClient.PublishAsync(publishRequest, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Published domain event plugin.sitemap.{Action} for {EntityType} {EntityId}, MessageId={MessageId}",
                    action, entityType, entityId, publishResponse.MessageId);
            }
            catch (Exception ex)
            {
                // Log but do not throw — SNS publish failures should not block CRUD operations
                _logger.LogError(ex,
                    "Failed to publish domain event plugin.sitemap.{Action} for {EntityType} {EntityId}",
                    action, entityType, entityId);
            }
        }

        #endregion

        #region Ordered Sitemap Builder

        /// <summary>
        /// Builds the ordered sitemap structure for an application.
        /// Queries all areas, nodes, and pages for the app, sorts by weight, and returns
        /// a tree structure plus the node-to-page dictionary.
        /// Replaces: appSrv.OrderSitemap(newSitemap) + PageUtils.GetNodePageDictionary(appId)
        /// from AdminController.cs lines 96-97.
        /// </summary>
        private async Task<(object Sitemap, Dictionary<Guid, List<Guid>> NodePageDictionary)> BuildOrderedSitemapAsync(
            Guid appId,
            CancellationToken cancellationToken)
        {
            // Query all items for this app partition
            var allItems = await QueryItemsByPartitionAsync(AppPk(appId), null, cancellationToken).ConfigureAwait(false);

            var areas = new List<AreaRecord>();
            var nodes = new List<NodeRecord>();
            var pages = new List<PageRecord>();

            foreach (var item in allItems)
            {
                var sk = GetRequiredString(item, "SK");
                var entityType = GetString(item, "EntityType");

                if (entityType == "Area" || sk.StartsWith(SkAreaPrefix, StringComparison.Ordinal))
                {
                    areas.Add(UnmarshalArea(item));
                }
                else if (entityType == "Node" || sk.StartsWith(SkNodePrefix, StringComparison.Ordinal))
                {
                    nodes.Add(UnmarshalNode(item));
                }
                else if (entityType == "Page" || sk.StartsWith(SkPagePrefix, StringComparison.Ordinal))
                {
                    pages.Add(UnmarshalPage(item));
                }
            }

            // Sort areas and nodes by weight (matching source: appSrv.OrderSitemap ordering by Weight)
            var orderedAreas = areas.OrderBy(a => a.Weight).ToList();
            var orderedNodes = nodes.OrderBy(n => n.Weight).ToList();

            // Build node-page dictionary (matching source: PageUtils.GetNodePageDictionary)
            var nodePageDictionary = new Dictionary<Guid, List<Guid>>();
            foreach (var node in orderedNodes)
            {
                var nodePages = pages
                    .Where(p => p.NodeId == node.Id)
                    .Select(p => p.Id)
                    .ToList();
                nodePageDictionary[node.Id] = nodePages;
            }

            // Build ordered tree structure
            var sitemapTree = orderedAreas.Select(area => new
            {
                area.Id,
                area.Name,
                area.Label,
                area.LabelTranslations,
                area.Description,
                area.DescriptionTranslations,
                area.IconClass,
                area.Color,
                area.Weight,
                area.ShowGroupNames,
                area.AccessRoles,
                Nodes = orderedNodes
                    .Where(n => n.AreaId == area.Id)
                    .Select(node => new
                    {
                        node.Id,
                        node.Name,
                        node.Label,
                        node.LabelTranslations,
                        node.IconClass,
                        node.Url,
                        node.Type,
                        node.EntityId,
                        node.Weight,
                        node.AccessRoles,
                        node.EntityListPages,
                        node.EntityCreatePages,
                        node.EntityDetailsPages,
                        node.EntityManagePages,
                        node.ParentId,
                        Pages = nodePageDictionary.TryGetValue(node.Id, out var pIds) ? pIds : new List<Guid>()
                    }).ToList()
            }).ToList();

            return (sitemapTree, nodePageDictionary);
        }

        #endregion

        #region App CRUD Operations

        /// <inheritdoc />
        public async Task<SitemapOperationResult> CreateAppAsync(
            Guid appId, string name, string label, string? description,
            string? iconClass, string? color, int weight, List<Guid>? accessRoles,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Generate ID if empty, matching source pattern AdminController line 76
                if (appId == Guid.Empty)
                    appId = Guid.NewGuid();

                if (string.IsNullOrWhiteSpace(name))
                    return new SitemapOperationResult { Success = false, Message = "App name is required." };

                if (string.IsNullOrWhiteSpace(label))
                    return new SitemapOperationResult { Success = false, Message = "App label is required." };

                var app = new AppRecord
                {
                    Id = appId,
                    Name = name,
                    Label = label,
                    Description = description,
                    IconClass = iconClass,
                    Color = color,
                    Weight = weight,
                    AccessRoles = accessRoles ?? new List<Guid>()
                };

                // Idempotent create using condition expression (AAP Section 0.8.5)
                var putRequest = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = MarshalApp(app),
                    ConditionExpression = "attribute_not_exists(PK)"
                };

                try
                {
                    PutItemResponse putResponse = await _dynamoDbClient.PutItemAsync(putRequest, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Created app {AppId} with name '{Name}'", appId, name);
                }
                catch (ConditionalCheckFailedException)
                {
                    // App already exists — idempotent: treat as success
                    _logger.LogInformation("App {AppId} already exists, idempotent create returning success", appId);
                }

                await PublishDomainEventAsync("app_created", "App", appId, cancellationToken).ConfigureAwait(false);

                var (sitemap, nodePageDict) = await BuildOrderedSitemapAsync(appId, cancellationToken).ConfigureAwait(false);
                return new SitemapOperationResult
                {
                    Success = true,
                    Message = "App created successfully.",
                    Sitemap = sitemap,
                    NodePageDictionary = nodePageDict
                };
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error creating app {AppId}", appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating app {AppId}", appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
        }

        /// <inheritdoc />
        public async Task<SitemapOperationResult> UpdateAppAsync(
            Guid appId, string name, string label, string? description,
            string? iconClass, string? color, int weight, List<Guid>? accessRoles,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (appId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "App ID is required." };

                if (string.IsNullOrWhiteSpace(name))
                    return new SitemapOperationResult { Success = false, Message = "App name is required." };

                var app = new AppRecord
                {
                    Id = appId,
                    Name = name,
                    Label = label ?? string.Empty,
                    Description = description,
                    IconClass = iconClass,
                    Color = color,
                    Weight = weight,
                    AccessRoles = accessRoles ?? new List<Guid>()
                };

                var putRequest = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = MarshalApp(app)
                };

                PutItemResponse putResponse = await _dynamoDbClient.PutItemAsync(putRequest, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Updated app {AppId}", appId);

                await PublishDomainEventAsync("app_updated", "App", appId, cancellationToken).ConfigureAwait(false);

                var (sitemap, nodePageDict) = await BuildOrderedSitemapAsync(appId, cancellationToken).ConfigureAwait(false);
                return new SitemapOperationResult
                {
                    Success = true,
                    Message = "App updated successfully.",
                    Sitemap = sitemap,
                    NodePageDictionary = nodePageDict
                };
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error updating app {AppId}", appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error updating app {AppId}", appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
        }

        /// <inheritdoc />
        public async Task<SitemapOperationResult> DeleteAppAsync(
            Guid appId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (appId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "App ID is required." };

                // Query all items in the app partition for cascade delete
                var allItems = await QueryItemsByPartitionAsync(AppPk(appId), null, cancellationToken).ConfigureAwait(false);

                if (allItems.Count > 0)
                {
                    // Batch delete all items (DynamoDB BatchWriteItem supports up to 25 items per batch)
                    var batches = new List<List<WriteRequest>>();
                    var currentBatch = new List<WriteRequest>();

                    foreach (var item in allItems)
                    {
                        currentBatch.Add(new WriteRequest
                        {
                            DeleteRequest = new DeleteRequest
                            {
                                Key = new Dictionary<string, AttributeValue>
                                {
                                    ["PK"] = item["PK"],
                                    ["SK"] = item["SK"]
                                }
                            }
                        });

                        if (currentBatch.Count >= 25)
                        {
                            batches.Add(currentBatch);
                            currentBatch = new List<WriteRequest>();
                        }
                    }

                    if (currentBatch.Count > 0)
                        batches.Add(currentBatch);

                    foreach (var batch in batches)
                    {
                        var batchRequest = new BatchWriteItemRequest
                        {
                            RequestItems = new Dictionary<string, List<WriteRequest>>
                            {
                                [_tableName] = batch
                            }
                        };
                        await _dynamoDbClient.BatchWriteItemAsync(batchRequest, cancellationToken).ConfigureAwait(false);
                    }

                    _logger.LogInformation("Cascade-deleted {Count} items for app {AppId}", allItems.Count, appId);
                }
                else
                {
                    _logger.LogWarning("App {AppId} not found for deletion (idempotent)", appId);
                }

                await PublishDomainEventAsync("app_deleted", "App", appId, cancellationToken).ConfigureAwait(false);

                return new SitemapOperationResult
                {
                    Success = true,
                    Message = "App deleted successfully."
                };
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error deleting app {AppId}", appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error deleting app {AppId}", appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
        }

        /// <inheritdoc />
        public async Task<AppRecord?> GetAppByIdAsync(
            Guid appId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var getRequest = new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = AppPk(appId) },
                        ["SK"] = new AttributeValue { S = SkMeta }
                    }
                };

                GetItemResponse getResponse = await _dynamoDbClient.GetItemAsync(getRequest, cancellationToken).ConfigureAwait(false);

                if (getResponse.Item == null || getResponse.Item.Count == 0)
                {
                    _logger.LogWarning("App {AppId} not found", appId);
                    return null;
                }

                return UnmarshalApp(getResponse.Item);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error fetching app {AppId}", appId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<AppRecord>> ListAppsAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Scan for all APP# partition items with SK=META
                var results = new List<AppRecord>();
                Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

                do
                {
                    var scanRequest = new ScanRequest
                    {
                        TableName = _tableName,
                        FilterExpression = "SK = :sk AND EntityType = :et",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":sk"] = new AttributeValue { S = SkMeta },
                            [":et"] = new AttributeValue { S = "App" }
                        }
                    };

                    if (lastEvaluatedKey != null)
                        scanRequest.ExclusiveStartKey = lastEvaluatedKey;

                    ScanResponse scanResponse = await _dynamoDbClient.ScanAsync(scanRequest, cancellationToken).ConfigureAwait(false);

                    foreach (var item in scanResponse.Items)
                    {
                        results.Add(UnmarshalApp(item));
                    }

                    lastEvaluatedKey = scanResponse.LastEvaluatedKey?.Count > 0
                        ? scanResponse.LastEvaluatedKey
                        : null;
                }
                while (lastEvaluatedKey != null);

                return results.OrderBy(a => a.Weight).ToList();
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error listing apps");
                throw;
            }
        }

        #endregion

        #region Area CRUD Operations

        /// <inheritdoc />
        public async Task<SitemapOperationResult> CreateAreaAsync(
            Guid areaId, Guid appId, string name, string label,
            Dictionary<string, string>? labelTranslations, string? description,
            Dictionary<string, string>? descriptionTranslations, string? iconClass,
            string? color, int weight, bool showGroupNames, List<Guid>? accessRoles,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Validation: source AdminController.cs lines 62-73
                if (appId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "App ID is required for area creation." };

                // Generate ID if empty, matching source AdminController.cs line 76
                if (areaId == Guid.Empty)
                    areaId = Guid.NewGuid();

                if (string.IsNullOrWhiteSpace(name))
                    return new SitemapOperationResult { Success = false, Message = "Area name is required." };

                var area = new AreaRecord
                {
                    Id = areaId,
                    AppId = appId,
                    Name = name,
                    Label = label ?? string.Empty,
                    LabelTranslations = labelTranslations ?? new Dictionary<string, string>(),
                    Description = description,
                    DescriptionTranslations = descriptionTranslations ?? new Dictionary<string, string>(),
                    IconClass = iconClass,
                    Color = color,
                    Weight = weight,
                    ShowGroupNames = showGroupNames,
                    AccessRoles = accessRoles ?? new List<Guid>()
                };

                // Idempotent create using condition expression
                var putRequest = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = MarshalArea(area),
                    ConditionExpression = "attribute_not_exists(PK) AND attribute_not_exists(SK)"
                };

                try
                {
                    PutItemResponse putResponse = await _dynamoDbClient.PutItemAsync(putRequest, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Created area {AreaId} in app {AppId} with name '{Name}'", areaId, appId, name);
                }
                catch (ConditionalCheckFailedException)
                {
                    _logger.LogInformation("Area {AreaId} already exists in app {AppId}, idempotent create", areaId, appId);
                }

                await PublishDomainEventAsync("area_created", "Area", areaId, cancellationToken).ConfigureAwait(false);

                // Return ordered sitemap (matching source AdminController.cs lines 94-98)
                var (sitemap, nodePageDict) = await BuildOrderedSitemapAsync(appId, cancellationToken).ConfigureAwait(false);
                return new SitemapOperationResult
                {
                    Success = true,
                    Message = "Area created successfully.",
                    Sitemap = sitemap,
                    NodePageDictionary = nodePageDict
                };
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error creating area {AreaId} in app {AppId}", areaId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating area {AreaId} in app {AppId}", areaId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
        }

        /// <inheritdoc />
        public async Task<SitemapOperationResult> UpdateAreaAsync(
            Guid areaId, Guid appId, string name, string label,
            Dictionary<string, string>? labelTranslations, string? description,
            Dictionary<string, string>? descriptionTranslations, string? iconClass,
            string? color, int weight, bool showGroupNames, List<Guid>? accessRoles,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Validation: source AdminController.cs lines 125-130
                if (areaId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "Area ID is required for update." };

                if (appId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "App ID is required for area update." };

                var area = new AreaRecord
                {
                    Id = areaId,
                    AppId = appId,
                    Name = name ?? string.Empty,
                    Label = label ?? string.Empty,
                    LabelTranslations = labelTranslations ?? new Dictionary<string, string>(),
                    Description = description,
                    DescriptionTranslations = descriptionTranslations ?? new Dictionary<string, string>(),
                    IconClass = iconClass,
                    Color = color,
                    Weight = weight,
                    ShowGroupNames = showGroupNames,
                    AccessRoles = accessRoles ?? new List<Guid>()
                };

                var putRequest = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = MarshalArea(area)
                };

                PutItemResponse putResponse = await _dynamoDbClient.PutItemAsync(putRequest, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Updated area {AreaId} in app {AppId}", areaId, appId);

                await PublishDomainEventAsync("area_updated", "Area", areaId, cancellationToken).ConfigureAwait(false);

                var (sitemap, nodePageDict) = await BuildOrderedSitemapAsync(appId, cancellationToken).ConfigureAwait(false);
                return new SitemapOperationResult
                {
                    Success = true,
                    Message = "Area updated successfully.",
                    Sitemap = sitemap,
                    NodePageDictionary = nodePageDict
                };
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error updating area {AreaId} in app {AppId}", areaId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error updating area {AreaId} in app {AppId}", areaId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
        }

        /// <inheritdoc />
        public async Task<SitemapOperationResult> DeleteAreaAsync(
            Guid areaId, Guid appId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Validation: source AdminController.cs lines 164-169
                if (areaId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "Area ID is required for deletion." };

                if (appId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "App ID is required for area deletion." };

                // Find and delete child nodes first (cascade delete)
                var nodeItems = await QueryItemsByPartitionAsync(AppPk(appId), SkNodePrefix, cancellationToken).ConfigureAwait(false);
                var childNodes = nodeItems
                    .Where(item => GetGuid(item, "AreaId") == areaId)
                    .ToList();

                var deleteRequests = new List<WriteRequest>();

                // Add child node deletions
                foreach (var nodeItem in childNodes)
                {
                    deleteRequests.Add(new WriteRequest
                    {
                        DeleteRequest = new DeleteRequest
                        {
                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["PK"] = nodeItem["PK"],
                                ["SK"] = nodeItem["SK"]
                            }
                        }
                    });

                    // Also detach pages bound to this node
                    var nodeId = GetGuid(nodeItem, "Id");
                    var pageItems = await QueryItemsByPartitionAsync(AppPk(appId), SkPagePrefix, cancellationToken).ConfigureAwait(false);
                    foreach (var pageItem in pageItems.Where(p => GetNullableGuid(p, "NodeId") == nodeId))
                    {
                        // Update page to remove NodeId/AreaId bindings
                        var page = UnmarshalPage(pageItem);
                        page.NodeId = null;
                        page.AreaId = null;
                        var updatePageRequest = new PutItemRequest
                        {
                            TableName = _tableName,
                            Item = MarshalPage(page)
                        };
                        await _dynamoDbClient.PutItemAsync(updatePageRequest, cancellationToken).ConfigureAwait(false);
                    }
                }

                // Add area deletion
                deleteRequests.Add(new WriteRequest
                {
                    DeleteRequest = new DeleteRequest
                    {
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["PK"] = new AttributeValue { S = AppPk(appId) },
                            ["SK"] = new AttributeValue { S = AreaSk(areaId) }
                        }
                    }
                });

                // Execute batch deletes in chunks of 25
                for (int i = 0; i < deleteRequests.Count; i += 25)
                {
                    var batch = deleteRequests.Skip(i).Take(25).ToList();
                    var batchRequest = new BatchWriteItemRequest
                    {
                        RequestItems = new Dictionary<string, List<WriteRequest>>
                        {
                            [_tableName] = batch
                        }
                    };
                    await _dynamoDbClient.BatchWriteItemAsync(batchRequest, cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation("Deleted area {AreaId} from app {AppId} with {NodeCount} child nodes", areaId, appId, childNodes.Count);

                await PublishDomainEventAsync("area_deleted", "Area", areaId, cancellationToken).ConfigureAwait(false);

                var (sitemap, nodePageDict) = await BuildOrderedSitemapAsync(appId, cancellationToken).ConfigureAwait(false);
                return new SitemapOperationResult
                {
                    Success = true,
                    Message = "Area deleted successfully.",
                    Sitemap = sitemap,
                    NodePageDictionary = nodePageDict
                };
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error deleting area {AreaId} from app {AppId}", areaId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error deleting area {AreaId} from app {AppId}", areaId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
        }

        #endregion

        #region Node CRUD Operations

        /// <inheritdoc />
        public async Task<SitemapOperationResult> CreateNodeAsync(
            Guid nodeId, Guid appId, Guid areaId, string name, string label,
            Dictionary<string, string>? labelTranslations, string? iconClass, string? url,
            int type, Guid? entityId, int weight, List<Guid>? accessRoles,
            List<Guid>? entityListPages, List<Guid>? entityCreatePages,
            List<Guid>? entityDetailsPages, List<Guid>? entityManagePages,
            Guid? parentId, List<Guid>? pageIds,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Validation: source AdminController.cs lines 211-228
                if (appId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "App ID is required for node creation." };

                if (areaId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "Area ID is required for node creation." };

                // Generate ID if empty, matching source AdminController.cs pattern
                if (nodeId == Guid.Empty)
                    nodeId = Guid.NewGuid();

                if (string.IsNullOrWhiteSpace(name))
                    return new SitemapOperationResult { Success = false, Message = "Node name is required." };

                var node = new NodeRecord
                {
                    Id = nodeId,
                    AppId = appId,
                    AreaId = areaId,
                    Name = name,
                    Label = label ?? string.Empty,
                    LabelTranslations = labelTranslations ?? new Dictionary<string, string>(),
                    IconClass = iconClass,
                    Url = url,
                    Type = type,
                    EntityId = entityId,
                    Weight = weight,
                    AccessRoles = accessRoles ?? new List<Guid>(),
                    EntityListPages = entityListPages ?? new List<Guid>(),
                    EntityCreatePages = entityCreatePages ?? new List<Guid>(),
                    EntityDetailsPages = entityDetailsPages ?? new List<Guid>(),
                    EntityManagePages = entityManagePages ?? new List<Guid>(),
                    ParentId = parentId,
                    PageIds = pageIds ?? new List<Guid>()
                };

                // Create node with idempotent condition
                var putRequest = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = MarshalNode(node),
                    ConditionExpression = "attribute_not_exists(PK) AND attribute_not_exists(SK)"
                };

                try
                {
                    PutItemResponse putResponse = await _dynamoDbClient.PutItemAsync(putRequest, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Created node {NodeId} in area {AreaId} of app {AppId}", nodeId, areaId, appId);
                }
                catch (ConditionalCheckFailedException)
                {
                    _logger.LogInformation("Node {NodeId} already exists, idempotent create", nodeId);
                }

                // Attach pages to this node (source AdminController.cs lines 246-254)
                if (pageIds != null && pageIds.Count > 0)
                {
                    await AttachPagesToNodeAsync(appId, nodeId, areaId, pageIds, cancellationToken).ConfigureAwait(false);
                }

                await PublishDomainEventAsync("node_created", "Node", nodeId, cancellationToken).ConfigureAwait(false);

                var (sitemap, nodePageDict) = await BuildOrderedSitemapAsync(appId, cancellationToken).ConfigureAwait(false);
                return new SitemapOperationResult
                {
                    Success = true,
                    Message = "Node created successfully.",
                    Sitemap = sitemap,
                    NodePageDictionary = nodePageDict
                };
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error creating node {NodeId} in app {AppId}", nodeId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating node {NodeId} in app {AppId}", nodeId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
        }

        /// <inheritdoc />
        public async Task<SitemapOperationResult> UpdateNodeAsync(
            Guid nodeId, Guid appId, Guid areaId, string name, string label,
            Dictionary<string, string>? labelTranslations, string? iconClass, string? url,
            int type, Guid? entityId, int weight, List<Guid>? accessRoles,
            List<Guid>? entityListPages, List<Guid>? entityCreatePages,
            List<Guid>? entityDetailsPages, List<Guid>? entityManagePages,
            Guid? parentId, List<Guid>? pageIds,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Validation: source AdminController.cs lines 284-298
                if (nodeId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "Node ID is required for update." };

                if (appId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "App ID is required for node update." };

                if (areaId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "Area ID is required for node update." };

                var node = new NodeRecord
                {
                    Id = nodeId,
                    AppId = appId,
                    AreaId = areaId,
                    Name = name ?? string.Empty,
                    Label = label ?? string.Empty,
                    LabelTranslations = labelTranslations ?? new Dictionary<string, string>(),
                    IconClass = iconClass,
                    Url = url,
                    Type = type,
                    EntityId = entityId,
                    Weight = weight,
                    AccessRoles = accessRoles ?? new List<Guid>(),
                    EntityListPages = entityListPages ?? new List<Guid>(),
                    EntityCreatePages = entityCreatePages ?? new List<Guid>(),
                    EntityDetailsPages = entityDetailsPages ?? new List<Guid>(),
                    EntityManagePages = entityManagePages ?? new List<Guid>(),
                    ParentId = parentId,
                    PageIds = pageIds ?? new List<Guid>()
                };

                // Update node record
                var putRequest = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = MarshalNode(node)
                };

                PutItemResponse putResponse = await _dynamoDbClient.PutItemAsync(putRequest, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Updated node {NodeId} in app {AppId}", nodeId, appId);

                // Page attach/detach diff computation
                // Source: AdminController.cs lines 318-358
                var requestedPageIds = pageIds ?? new List<Guid>();
                var currentPageItems = await QueryItemsByPartitionAsync(AppPk(appId), SkPagePrefix, cancellationToken).ConfigureAwait(false);

                // Find currently attached pages for this node (matching source lines 320-325)
                var currentlyAttachedPages = currentPageItems
                    .Where(p => GetNullableGuid(p, "NodeId") == nodeId)
                    .ToList();
                var currentAttachedPageIds = new HashSet<Guid>(
                    currentlyAttachedPages.Select(p => GetGuid(p, "Id")));

                var requestedPageIdSet = new HashSet<Guid>(requestedPageIds);

                // Pages to attach: in requested but not in current
                var pagesToAttach = requestedPageIds
                    .Where(pid => !currentAttachedPageIds.Contains(pid))
                    .ToList();

                // Pages to detach: in current but not in requested
                var pagesToDetach = currentAttachedPageIds
                    .Where(pid => !requestedPageIdSet.Contains(pid))
                    .ToList();

                // Attach new pages (source lines 329-342)
                if (pagesToAttach.Count > 0)
                {
                    await AttachPagesToNodeAsync(appId, nodeId, areaId, pagesToAttach, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Attached {Count} pages to node {NodeId}", pagesToAttach.Count, nodeId);
                }

                // Detach removed pages (source lines 344-355)
                if (pagesToDetach.Count > 0)
                {
                    await DetachPagesFromNodeAsync(appId, pagesToDetach, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Detached {Count} pages from node {NodeId}", pagesToDetach.Count, nodeId);
                }

                await PublishDomainEventAsync("node_updated", "Node", nodeId, cancellationToken).ConfigureAwait(false);

                var (sitemap, nodePageDict) = await BuildOrderedSitemapAsync(appId, cancellationToken).ConfigureAwait(false);
                return new SitemapOperationResult
                {
                    Success = true,
                    Message = "Node updated successfully.",
                    Sitemap = sitemap,
                    NodePageDictionary = nodePageDict
                };
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error updating node {NodeId} in app {AppId}", nodeId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error updating node {NodeId} in app {AppId}", nodeId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
        }

        /// <inheritdoc />
        public async Task<SitemapOperationResult> DeleteNodeAsync(
            Guid nodeId, Guid appId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Validation: source AdminController.cs lines 383-388
                if (nodeId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "Node ID is required for deletion." };

                if (appId == Guid.Empty)
                    return new SitemapOperationResult { Success = false, Message = "App ID is required for node deletion." };

                // Detach all pages from this node before deleting
                var pageItems = await QueryItemsByPartitionAsync(AppPk(appId), SkPagePrefix, cancellationToken).ConfigureAwait(false);
                var attachedPageIds = pageItems
                    .Where(p => GetNullableGuid(p, "NodeId") == nodeId)
                    .Select(p => GetGuid(p, "Id"))
                    .ToList();

                if (attachedPageIds.Count > 0)
                {
                    await DetachPagesFromNodeAsync(appId, attachedPageIds, cancellationToken).ConfigureAwait(false);
                }

                // Delete the node item
                var deleteRequest = new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = AppPk(appId) },
                        ["SK"] = new AttributeValue { S = NodeSk(nodeId) }
                    }
                };

                DeleteItemResponse deleteResponse = await _dynamoDbClient.DeleteItemAsync(deleteRequest, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Deleted node {NodeId} from app {AppId}, detached {PageCount} pages", nodeId, appId, attachedPageIds.Count);

                await PublishDomainEventAsync("node_deleted", "Node", nodeId, cancellationToken).ConfigureAwait(false);

                var (sitemap, nodePageDict) = await BuildOrderedSitemapAsync(appId, cancellationToken).ConfigureAwait(false);
                return new SitemapOperationResult
                {
                    Success = true,
                    Message = "Node deleted successfully.",
                    Sitemap = sitemap,
                    NodePageDictionary = nodePageDict
                };
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error deleting node {NodeId} from app {AppId}", nodeId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error deleting node {NodeId} from app {AppId}", nodeId, appId);
                return new SitemapOperationResult { Success = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// Attaches pages to a node by updating their NodeId and AreaId.
        /// Source: AdminController.cs lines 246-254 page iteration pattern.
        /// </summary>
        private async Task AttachPagesToNodeAsync(
            Guid appId, Guid nodeId, Guid areaId, List<Guid> pageIds,
            CancellationToken cancellationToken)
        {
            foreach (var pageId in pageIds)
            {
                // Get existing page record
                var getRequest = new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = AppPk(appId) },
                        ["SK"] = new AttributeValue { S = PageSk(pageId) }
                    }
                };

                GetItemResponse getResponse = await _dynamoDbClient.GetItemAsync(getRequest, cancellationToken).ConfigureAwait(false);

                if (getResponse.Item != null && getResponse.Item.Count > 0)
                {
                    var page = UnmarshalPage(getResponse.Item);
                    page.NodeId = nodeId;
                    page.AreaId = areaId;

                    var putRequest = new PutItemRequest
                    {
                        TableName = _tableName,
                        Item = MarshalPage(page)
                    };
                    await _dynamoDbClient.PutItemAsync(putRequest, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Page does not exist yet — create a minimal binding record
                    var page = new PageRecord
                    {
                        Id = pageId,
                        Name = string.Empty,
                        NodeId = nodeId,
                        AreaId = areaId,
                        AppId = appId,
                        Type = 0
                    };
                    var putRequest = new PutItemRequest
                    {
                        TableName = _tableName,
                        Item = MarshalPage(page)
                    };
                    await _dynamoDbClient.PutItemAsync(putRequest, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Detaches pages from their node by clearing NodeId and AreaId.
        /// Source: AdminController.cs lines 344-355 page detach pattern.
        /// </summary>
        private async Task DetachPagesFromNodeAsync(
            Guid appId, List<Guid> pageIds,
            CancellationToken cancellationToken)
        {
            foreach (var pageId in pageIds)
            {
                var getRequest = new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = AppPk(appId) },
                        ["SK"] = new AttributeValue { S = PageSk(pageId) }
                    }
                };

                GetItemResponse getResponse = await _dynamoDbClient.GetItemAsync(getRequest, cancellationToken).ConfigureAwait(false);

                if (getResponse.Item != null && getResponse.Item.Count > 0)
                {
                    var page = UnmarshalPage(getResponse.Item);
                    page.NodeId = null;
                    page.AreaId = null;

                    var putRequest = new PutItemRequest
                    {
                        TableName = _tableName,
                        Item = MarshalPage(page)
                    };
                    await _dynamoDbClient.PutItemAsync(putRequest, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        #endregion

        #region Auxiliary Data and Ordering

        /// <inheritdoc />
        public async Task<NodeAuxData> GetNodeAuxDataAsync(
            Guid appId,
            CancellationToken cancellationToken = default)
        {
            // Source: AdminController.cs lines 422-515 (GetNodeAuxData action)
            // Returns entity select options, node types, unattached app pages, and entity pages.
            try
            {
                var result = new NodeAuxData();

                // 1. Node type options (matching source AdminController.cs lines 430-440)
                // The monolith defined fixed node types: Default(0), Application(1), EntityList(2)
                result.NodeTypes = new List<SelectOptionRecord>
                {
                    new SelectOptionRecord { Value = "0", Label = "Default" },
                    new SelectOptionRecord { Value = "1", Label = "Application" },
                    new SelectOptionRecord { Value = "2", Label = "EntityList" }
                };

                // 2. Retrieve all items for this app to find pages and entities
                var allItems = await QueryItemsByPartitionAsync(AppPk(appId), null, cancellationToken).ConfigureAwait(false);

                var allPages = new List<PageRecord>();
                var nodes = new List<NodeRecord>();
                var entityIds = new HashSet<Guid>();

                foreach (var item in allItems)
                {
                    var entityType = GetString(item, "EntityType");
                    var sk = GetRequiredString(item, "SK");

                    if (entityType == "Page" || sk.StartsWith(SkPagePrefix, StringComparison.Ordinal))
                    {
                        allPages.Add(UnmarshalPage(item));
                    }
                    else if (entityType == "Node" || sk.StartsWith(SkNodePrefix, StringComparison.Ordinal))
                    {
                        var node = UnmarshalNode(item);
                        nodes.Add(node);
                        if (node.EntityId.HasValue && node.EntityId.Value != Guid.Empty)
                        {
                            entityIds.Add(node.EntityId.Value);
                        }
                    }
                }

                // 3. Unattached app pages (pages not bound to any node)
                // Source: AdminController.cs lines 461-465 — pages where NodeId is null or not set
                result.AppPages = allPages
                    .Where(p => !p.NodeId.HasValue || p.NodeId.Value == Guid.Empty)
                    .OrderBy(p => p.Name)
                    .ToList();

                // 4. All entity pages (pages that have an EntityId)
                // Source: AdminController.cs lines 467-480
                result.AllEntityPages = allPages
                    .Where(p => p.EntityId.HasValue && p.EntityId.Value != Guid.Empty)
                    .OrderBy(p => p.Name)
                    .ToList();

                // 5. All entities as select options
                // Source: AdminController.cs lines 445-456 — entity list sorted by name
                // In the microservices architecture, entities are managed by the Entity Management service.
                // This implementation derives entity references from entity IDs already present in the
                // sitemap node and page records within this bounded context's datastore.
                var allEntityGuids = new HashSet<Guid>(entityIds);
                foreach (var page in allPages)
                {
                    if (page.EntityId.HasValue && page.EntityId.Value != Guid.Empty)
                    {
                        allEntityGuids.Add(page.EntityId.Value);
                    }
                }

                result.AllEntities = allEntityGuids
                    .Select(eid => new SelectOptionRecord
                    {
                        Value = eid.ToString(),
                        Label = eid.ToString()
                    })
                    .OrderBy(o => o.Label)
                    .ToList();

                _logger.LogInformation(
                    "Retrieved node aux data for app {AppId}: {EntityCount} entities, {PageCount} unattached pages, {AllPageCount} entity pages",
                    appId, result.AllEntities.Count, result.AppPages.Count, result.AllEntityPages.Count);

                return result;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error fetching node aux data for app {AppId}", appId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<object> GetOrderedSitemapAsync(
            Guid appId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (appId == Guid.Empty)
                {
                    _logger.LogWarning("GetOrderedSitemapAsync called with empty appId");
                    return new
                    {
                        Sitemap = new List<object>(),
                        NodePageDictionary = new Dictionary<Guid, List<Guid>>()
                    };
                }

                var (sitemap, nodePageDict) = await BuildOrderedSitemapAsync(appId, cancellationToken).ConfigureAwait(false);

                return new
                {
                    Sitemap = sitemap,
                    NodePageDictionary = nodePageDict
                };
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error building ordered sitemap for app {AppId}", appId);
                throw;
            }
        }

        #endregion
    }
}