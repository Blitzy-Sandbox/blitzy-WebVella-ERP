using System.Text.Json.Serialization;

namespace WebVellaErp.Notifications.Models
{
    /// <summary>
    /// Notification envelope model for the Notifications microservice.
    /// Exact port of WebVella.Erp/Notifications/Notification.cs to the new
    /// WebVellaErp.Notifications.Models namespace with System.Text.Json
    /// attributes added for Native AOT compatibility.
    ///
    /// Properties:
    ///   Channel — Routing/topic key for notification dispatch (e.g., SNS topic,
    ///             SQS queue name, or in-app channel identifier).
    ///   Message — Text payload content delivered through the specified channel.
    ///
    /// JSON serialization uses snake_case property names via [JsonPropertyName]
    /// for consistency across the event-driven architecture and API Gateway
    /// request/response payloads.
    /// </summary>
    public class Notification
    {
        /// <summary>
        /// Routing/topic key for notification dispatch.
        /// Determines which delivery channel (email, webhook, in-app, SNS topic)
        /// should process this notification envelope.
        /// </summary>
        [JsonPropertyName("channel")]
        public string Channel { get; set; } = string.Empty;

        /// <summary>
        /// Text payload content delivered through the specified channel.
        /// Contains the notification body that will be formatted and dispatched
        /// according to the channel's delivery mechanism.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }
}
