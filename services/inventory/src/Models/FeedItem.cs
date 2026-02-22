using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Inventory.Models
{
    /// <summary>
    /// Strongly-typed domain model for feed/activity items in the Inventory (Project Management) service.
    /// Replaces the dynamic <c>EntityRecord</c> dictionary pattern used in the monolith's
    /// <c>FeedItemService.Create()</c>. Feed items capture activity logs such as task creation,
    /// timelog entries, comments, and system events.
    ///
    /// The <c>Type</c> field categorizes entries:
    ///   - "task"    — created by TaskService.PostCreateApiHookLogic
    ///   - "timelog" — created by TimeLogService.PreCreateApiHookLogic
    ///   - "comment" — created by CommentService.PreCreateApiHookLogic
    ///   - "system"  — default when no explicit type is specified
    ///
    /// <c>LRelatedRecords</c> and <c>LScope</c> are stored as JSON-serialized strings
    /// for backward compatibility with the monolith's storage pattern. The service layer
    /// handles serialization/deserialization using <c>JsonSerializer.Serialize/Deserialize</c>.
    ///
    /// DynamoDB mapping:
    ///   PK: FEED_ITEM#{Id}
    ///   SK: META
    /// </summary>
    public class FeedItem
    {
        /// <summary>
        /// Primary key identifier for the feed item.
        /// Maps to monolith field: <c>record["id"]</c> in FeedItemService.Create() line 36.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Activity summary line describing what happened.
        /// Examples: "created [PROJ-1] Task title", "logged 30 minutes on [PROJ-2]".
        /// Maps to monolith field: <c>record["subject"]</c> in FeedItemService.Create() line 39.
        /// </summary>
        [JsonPropertyName("subject")]
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// Activity body/detail text, typically an HTML snippet produced by
        /// RenderService.GetSnippetFromHtml in the monolith.
        /// Maps to monolith field: <c>record["body"]</c> in FeedItemService.Create() line 40.
        /// </summary>
        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;

        /// <summary>
        /// Activity type classifier. Valid values: "task", "timelog", "comment", "system".
        /// Defaults to "system" when no explicit type is provided (per FeedItemService.Create() lines 29-31).
        /// Maps to monolith field: <c>record["type"]</c> in FeedItemService.Create() line 43.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Identifier of the user who created this feed item.
        /// Maps to monolith field: <c>record["created_by"]</c> in FeedItemService.Create() line 37.
        /// Defaults to SystemIds.SystemUserId if not provided in the monolith.
        /// </summary>
        [JsonPropertyName("created_by")]
        public Guid CreatedBy { get; set; }

        /// <summary>
        /// Timestamp when the feed item was created.
        /// Maps to monolith field: <c>record["created_on"]</c> in FeedItemService.Create() line 38.
        /// Defaults to DateTime.Now if not provided in the monolith.
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// JSON-serialized list of related record GUIDs (task IDs, project IDs, watcher IDs).
        /// Stored as a JSON string for backward compatibility with the monolith's storage pattern
        /// where <c>JsonConvert.SerializeObject(relatedRecords)</c> was used (FeedItemService.Create() line 41).
        /// Example value: <c>["3fa85f64-5717-4562-b3fc-2c963f66afa6","6ba7b810-9dad-11d1-80b4-00c04fd430c8"]</c>
        /// The service layer handles serialization/deserialization via JsonSerializer.
        /// </summary>
        [JsonPropertyName("l_related_records")]
        public string LRelatedRecords { get; set; } = string.Empty;

        /// <summary>
        /// JSON-serialized list of scope strings (e.g., <c>["projects"]</c>).
        /// Stored as a JSON string for backward compatibility with the monolith's storage pattern
        /// where <c>JsonConvert.SerializeObject(scope)</c> was used (FeedItemService.Create() line 42).
        /// The service layer handles serialization/deserialization via JsonSerializer.
        /// </summary>
        [JsonPropertyName("l_scope")]
        public string LScope { get; set; } = string.Empty;
    }
}
