using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Notifications.Models
{
    /// <summary>
    /// Domain event payload model representing a record change event that triggers
    /// notifications within the event-driven architecture. Published to SNS topics
    /// following the naming convention {domain}.{entity}.{action} (e.g.,
    /// notifications.email.created, notifications.email.updated).
    ///
    /// Ported from <c>WebVella.Erp.Notifications.ErpRecordChangeNotification</c>
    /// (original source lines 1-12) with the following transformations:
    ///   - Renamed from <c>ErpRecordChangeNotification</c> to <c>NotificationEvent</c>
    ///   - Namespace changed from <c>WebVella.Erp.Notifications</c> to
    ///     <c>WebVellaErp.Notifications.Models</c>
    ///   - Added <see cref="JsonPropertyNameAttribute"/> on all properties for
    ///     System.Text.Json AOT-compatible serialization with snake_case JSON names
    ///   - Added <see cref="Action"/> property for event type discrimination
    ///     (created/updated/deleted) in the event-driven architecture
    /// </summary>
    public class NotificationEvent
    {
        /// <summary>
        /// Unique identifier of the entity whose record triggered this notification event.
        /// Corresponds to the entity metadata definition in the Entity Management service.
        /// Ported from source <c>ErpRecordChangeNotification.EntityId</c> (line 7).
        /// </summary>
        [JsonPropertyName("entity_id")]
        public Guid EntityId { get; set; }

        /// <summary>
        /// Human-readable name of the entity whose record triggered this notification event
        /// (e.g., "email", "smtp_service", "webhook_config"). Used for event routing and
        /// logging purposes.
        /// Ported from source <c>ErpRecordChangeNotification.EntityName</c> (line 8).
        /// </summary>
        [JsonPropertyName("entity_name")]
        public string EntityName { get; set; } = string.Empty;

        /// <summary>
        /// Unique identifier of the specific record that was created, updated, or deleted.
        /// References a record within the entity identified by <see cref="EntityId"/>.
        /// Ported from source <c>ErpRecordChangeNotification.RecordId</c> (line 9).
        /// </summary>
        [JsonPropertyName("record_id")]
        public Guid RecordId { get; set; }

        /// <summary>
        /// Event type discriminator indicating the CRUD action that triggered this event.
        /// Valid values: "created", "updated", "deleted".
        /// This property is NEW (not present in the original monolith source) and enables
        /// event-driven architecture patterns where consumers can filter and route events
        /// based on the action type. Follows the AAP event naming convention
        /// {domain}.{entity}.{action}.
        /// </summary>
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;
    }
}
