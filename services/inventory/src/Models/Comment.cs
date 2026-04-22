using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Inventory.Models
{
    /// <summary>
    /// Strongly-typed domain model for comments in the Inventory (Project Management) service.
    /// Replaces the dynamic EntityRecord dictionary pattern used in the monolith's CommentService.Create().
    /// Comments support threaded replies via ParentId and are scoped to projects.
    /// </summary>
    public class Comment
    {
        /// <summary>
        /// Primary key identifier for the comment.
        /// Maps to the monolith's record["id"] field.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Comment text content (may contain HTML).
        /// Maps to the monolith's record["body"] field.
        /// </summary>
        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;

        /// <summary>
        /// Parent comment ID for threaded replies; null for top-level comments.
        /// Nullable Guid per source CommentService.Create() parameter (Guid? parentId = null).
        /// Maps to the monolith's record["parent_id"] field.
        /// </summary>
        [JsonPropertyName("parent_id")]
        public Guid? ParentId { get; set; }

        /// <summary>
        /// ID of the user who authored the comment.
        /// Defaults to SystemIds.SystemUserId in the monolith if not provided.
        /// Maps to the monolith's record["created_by"] field.
        /// </summary>
        [JsonPropertyName("created_by")]
        public Guid CreatedBy { get; set; }

        /// <summary>
        /// Creation timestamp of the comment.
        /// Defaults to DateTime.UtcNow in the monolith if not provided.
        /// Maps to the monolith's record["created_on"] field.
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// JSON-serialized list of scope strings (e.g., ["projects"]).
        /// Stored as a JSON string for backward compatibility with the monolith storage pattern
        /// where CommentService serializes List&lt;string&gt; via JsonConvert.SerializeObject(scope).
        /// Maps to the monolith's record["l_scope"] field.
        /// </summary>
        [JsonPropertyName("l_scope")]
        public string LScope { get; set; } = string.Empty;

        /// <summary>
        /// JSON-serialized list of related record GUIDs (task IDs, project IDs).
        /// Stored as a JSON string for backward compatibility with the monolith storage pattern
        /// where CommentService serializes List&lt;Guid&gt; via JsonConvert.SerializeObject(relatedRecords).
        /// Maps to the monolith's record["l_related_records"] field.
        /// </summary>
        [JsonPropertyName("l_related_records")]
        public string LRelatedRecords { get; set; } = string.Empty;
    }
}
