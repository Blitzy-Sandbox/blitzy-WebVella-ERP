using System.Text.Json.Serialization;

namespace WebVellaErp.Notifications.Models
{
    /// <summary>
    /// Webhook configuration model for the Notifications microservice.
    /// Defines an HTTP webhook endpoint that receives notification payloads
    /// when events matching the specified <see cref="Channel"/> occur.
    /// </summary>
    /// <remarks>
    /// This is a brand-new model introduced in the serverless architecture — it did not
    /// exist in the WebVella ERP monolith. It replaces the monolith's PostgreSQL
    /// LISTEN/NOTIFY pub/sub mechanism with HTTP webhook delivery, enabling external
    /// systems to subscribe to domain events via configurable HTTP endpoints.
    ///
    /// Persisted in DynamoDB via <c>NotificationRepository</c> with single-table design
    /// using composite key PK=WEBHOOK#, SK=CONFIG#{Id}.
    ///
    /// Used by <c>WebhookHandler</c> Lambda function to resolve matching webhook
    /// configurations for incoming notification events and dispatch HTTP POST requests
    /// to the configured <see cref="EndpointUrl"/> with exponential backoff retry
    /// governed by <see cref="MaxRetries"/> and <see cref="RetryIntervalSeconds"/>.
    ///
    /// JSON serialization uses snake_case property names via <see cref="JsonPropertyNameAttribute"/>
    /// for consistency across the event-driven architecture and API Gateway
    /// request/response payloads. Compatible with .NET 9 Native AOT compilation.
    /// </remarks>
    public class WebhookConfig
    {
        /// <summary>
        /// Unique identifier for the webhook configuration.
        /// Used as the sort key (SK=CONFIG#{Id}) in the DynamoDB single-table design
        /// and as the idempotency reference for webhook CRUD operations.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Target URL for webhook delivery.
        /// Must be a valid absolute HTTP or HTTPS URL that accepts POST requests
        /// with a JSON notification payload body. The webhook handler sends
        /// notification event data to this endpoint when a matching channel event occurs.
        /// </summary>
        [JsonPropertyName("endpoint_url")]
        public string EndpointUrl { get; set; } = string.Empty;

        /// <summary>
        /// Subscription channel filter that determines which notification events
        /// trigger delivery to this webhook endpoint. Matches against the
        /// <see cref="Notification.Channel"/> property of incoming notification
        /// envelopes using the domain event naming convention
        /// ({domain}.{entity}.{action}, e.g., "invoicing.invoice.created").
        /// </summary>
        [JsonPropertyName("channel")]
        public string Channel { get; set; } = string.Empty;

        /// <summary>
        /// Maximum number of delivery retry attempts before the webhook dispatch
        /// is considered failed and the event is routed to the dead-letter queue.
        /// A value of 0 means no retries — the initial delivery attempt is the only one.
        /// Typical values range from 3 to 5 for production webhook configurations.
        /// </summary>
        [JsonPropertyName("max_retries")]
        public int MaxRetries { get; set; }

        /// <summary>
        /// Wait time in seconds between consecutive retry attempts for failed
        /// webhook deliveries. Used in conjunction with <see cref="MaxRetries"/>
        /// to implement a fixed-interval retry strategy. For example, a value of 30
        /// with MaxRetries=3 would retry at 30s, 60s, and 90s after initial failure.
        /// </summary>
        [JsonPropertyName("retry_interval_seconds")]
        public int RetryIntervalSeconds { get; set; }

        /// <summary>
        /// Indicates whether this webhook configuration is active and should receive
        /// notification deliveries. Disabled webhooks (false) are skipped during
        /// event dispatch without being deleted, allowing temporary suspension
        /// of webhook delivery for maintenance or debugging purposes.
        /// </summary>
        [JsonPropertyName("is_enabled")]
        public bool IsEnabled { get; set; }

        /// <summary>
        /// UTC timestamp recording when this webhook configuration was created.
        /// Set once during initial creation and never modified. Used for
        /// auditing and chronological ordering of webhook configurations.
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }
    }
}
