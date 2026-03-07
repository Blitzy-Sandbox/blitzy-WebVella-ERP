using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace WebVella.Erp.Service.Core.Events.Publishers
{
    /// <summary>
    /// Message contract representing a notification event published on the message bus.
    /// Replaces the monolith's <c>Notification</c> POCO (Channel + Message) that was
    /// serialized to JSON, base64-encoded, and sent via PostgreSQL NOTIFY on the
    /// <c>ERP_NOTIFICATIONS_CHANNNEL</c> SQL channel.
    /// 
    /// In the microservice architecture, this message is published through MassTransit's
    /// <see cref="IPublishEndpoint"/> and routed via RabbitMQ (local/Docker) or
    /// SNS/SQS (LocalStack/AWS). MassTransit handles all serialization internally,
    /// eliminating the need for manual JSON + base64 encoding.
    /// </summary>
    public class NotificationMessage
    {
        /// <summary>
        /// Unique correlation identifier for distributed tracing across microservices.
        /// Generated via <see cref="Guid.NewGuid()"/> at publish time.
        /// Not present in the original monolith's <c>Notification</c> POCO — added
        /// per AAP requirement: "Publishers set CorrelationId and Timestamp on each
        /// event for distributed tracing."
        /// </summary>
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// UTC timestamp indicating when the notification was published.
        /// Set via <see cref="DateTime.UtcNow"/> at publish time.
        /// Not present in the original monolith's <c>Notification</c> POCO — added
        /// per AAP requirement for distributed tracing metadata on every event.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Logical channel name for the notification, used by subscribers to filter
        /// which notifications they process. Maps directly to the original
        /// <c>Notification.Channel</c> property.
        /// 
        /// In the monolith, <c>NotificationContext.HandleNotification</c> filtered
        /// registered listeners by comparing <c>listener.Channel.ToLowerInvariant()</c>
        /// against <c>notification.Channel.ToLowerInvariant()</c> (case-insensitive).
        /// Subscribers in the microservice architecture should apply the same
        /// case-insensitive filtering logic.
        /// </summary>
        public string Channel { get; set; } = string.Empty;

        /// <summary>
        /// Notification payload content. Maps directly to the original
        /// <c>Notification.Message</c> property. In the monolith, this could contain
        /// serialized JSON (e.g., <c>ErpRecordChangeNotification</c> with EntityId,
        /// EntityName, RecordId). Subscribers are responsible for deserializing the
        /// message content based on the channel context.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Asynchronous event publisher that replaces the monolith's PostgreSQL LISTEN/NOTIFY
    /// pub/sub mechanism implemented in <c>NotificationContext</c>.
    /// 
    /// <para><b>Original mechanism (replaced):</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     <c>NotificationContext.SendNotification(Notification)</c> serialized a
    ///     <c>Notification</c> (Channel + Message) to JSON with
    ///     <c>TypeNameHandling.Auto</c>, base64-encoded the payload, and executed
    ///     <c>NOTIFY ERP_NOTIFICATIONS_CHANNNEL, '{base64}'</c> via a new
    ///     <c>NpgsqlConnection</c>.
    ///   </item>
    ///   <item>
    ///     <c>NotificationContext.ListenForNotifications()</c> opened a persistent
    ///     <c>NpgsqlConnection</c>, subscribed via
    ///     <c>LISTEN ERP_NOTIFICATIONS_CHANNNEL</c>, and looped on
    ///     <c>sqlConnection.Wait()</c> to decode base64 payloads and dispatch to
    ///     registered listeners.
    ///   </item>
    ///   <item>
    ///     <c>NotificationContext.HandleNotification(Notification)</c> filtered listeners
    ///     by channel (case-insensitive) and invoked each via
    ///     <c>Task.Run(() => listener.Method.Invoke(...))</c> (fire-and-forget pattern).
    ///   </item>
    /// </list>
    /// 
    /// <para><b>New mechanism:</b></para>
    /// <para>
    /// Uses MassTransit's <see cref="IPublishEndpoint"/> to publish
    /// <see cref="NotificationMessage"/> events on the message bus. MassTransit handles
    /// serialization, transport (RabbitMQ for local/Docker, SNS/SQS for LocalStack/AWS),
    /// and subscriber routing. The fire-and-forget resilience pattern is preserved via
    /// try/catch — notification publish failures are logged but do not propagate to callers.
    /// </para>
    /// 
    /// <para><b>Architectural decisions:</b></para>
    /// <list type="bullet">
    ///   <item>No dependency on Npgsql — PostgreSQL LISTEN/NOTIFY entirely replaced</item>
    ///   <item>No base64 encoding — MassTransit handles serialization internally</item>
    ///   <item>No TypeNameHandling.Auto — MassTransit uses its own serialization strategy</item>
    ///   <item>Async pattern — original <c>SendNotification</c> was synchronous</item>
    ///   <item>CancellationToken support — microservice pattern for graceful shutdown</item>
    ///   <item>CorrelationId + Timestamp on every event — distributed tracing per AAP</item>
    /// </list>
    /// </summary>
    public class NotificationEventPublisher
    {
        private readonly IPublishEndpoint _publishEndpoint;
        private readonly ILogger<NotificationEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="NotificationEventPublisher"/> with
        /// the required MassTransit publish endpoint and structured logger.
        /// 
        /// Registered in the service's DI container; replaces the monolith's
        /// <c>NotificationContext.Current</c> singleton which was initialized once
        /// at application startup.
        /// </summary>
        /// <param name="publishEndpoint">
        /// MassTransit publish endpoint for sending <see cref="NotificationMessage"/>
        /// events to the message bus (RabbitMQ or SNS/SQS).
        /// </param>
        /// <param name="logger">
        /// Structured logger for recording publish success (Debug) and failure (Warning)
        /// events with channel name and correlation ID for distributed tracing.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="publishEndpoint"/> or <paramref name="logger"/> is null.
        /// </exception>
        public NotificationEventPublisher(
            IPublishEndpoint publishEndpoint,
            ILogger<NotificationEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Publishes a notification event on the message bus, replacing the monolith's
        /// <c>NotificationContext.SendNotification(Notification)</c> method.
        /// 
        /// <para><b>Original behavior replaced:</b></para>
        /// <code>
        /// // NotificationContext.SendNotification (lines 149-170):
        /// // 1. Serialized Notification to JSON with TypeNameHandling.Auto
        /// // 2. Base64-encoded the JSON payload
        /// // 3. Executed SQL: NOTIFY ERP_NOTIFICATIONS_CHANNNEL, '{base64}'
        /// // 4. Used a new NpgsqlConnection per call
        /// </code>
        /// 
        /// <para><b>New behavior:</b></para>
        /// <para>
        /// Creates a <see cref="NotificationMessage"/> with a unique
        /// <see cref="NotificationMessage.CorrelationId"/> and UTC
        /// <see cref="NotificationMessage.Timestamp"/>, then publishes it via
        /// MassTransit's <see cref="IPublishEndpoint.Publish{T}(T, CancellationToken)"/>.
        /// </para>
        /// 
        /// <para><b>Fire-and-forget resilience:</b></para>
        /// <para>
        /// The original monolith dispatched notifications to listeners via
        /// <c>Task.Run(() => listener.Method.Invoke(...))</c>, silently swallowing
        /// exceptions from individual listener invocations. This publisher preserves
        /// that resilience pattern: publish failures are caught and logged at Warning
        /// level without propagating to the caller.
        /// </para>
        /// </summary>
        /// <param name="channel">
        /// Logical notification channel name. Maps to <c>Notification.Channel</c>
        /// from the original monolith. Must not be null or whitespace.
        /// </param>
        /// <param name="message">
        /// Notification payload content. Maps to <c>Notification.Message</c> from
        /// the original monolith. Can contain serialized JSON (e.g.,
        /// <c>ErpRecordChangeNotification</c>). Must not be null or whitespace.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for graceful shutdown support. Not present in the
        /// original synchronous <c>SendNotification</c> method — added for
        /// microservice architecture compliance.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="channel"/> is null, empty, or whitespace.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="message"/> is null, empty, or whitespace.
        /// </exception>
        public async Task PublishNotificationAsync(
            string channel,
            string message,
            CancellationToken cancellationToken = default)
        {
            // Input validation — preserving the monolith's validation patterns.
            // The original NotificationContext.AttachListener validated channel/method
            // parameters via ArgumentException/ArgumentNullException checks.
            if (string.IsNullOrWhiteSpace(channel))
                throw new ArgumentException(
                    "Notification channel must not be null or whitespace.",
                    nameof(channel));

            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException(
                    "Notification message must not be null or whitespace.",
                    nameof(message));

            // Create the notification message with distributed tracing metadata.
            // CorrelationId and Timestamp are set per AAP requirement:
            // "Publishers set CorrelationId and Timestamp on each event for
            // distributed tracing."
            var notificationMessage = new NotificationMessage
            {
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
                Channel = channel,
                Message = message
            };

            try
            {
                // Publish via MassTransit — replaces:
                //   string sql = $"notify {SQL_NOTIFICATION_CHANNEL_NAME}, '{encodedText}';";
                //   command.ExecuteNonQuery();
                // MassTransit handles serialization, transport routing (RabbitMQ or SNS/SQS),
                // and delivery guarantees internally.
                await _publishEndpoint.Publish(notificationMessage, cancellationToken);

                // Log successful publish at Debug level with channel and correlation ID
                // for distributed tracing and operational visibility.
                _logger.LogDebug(
                    "Published notification event on channel '{Channel}' with CorrelationId '{CorrelationId}'.",
                    channel,
                    notificationMessage.CorrelationId);
            }
            catch (Exception ex)
            {
                // Fire-and-forget resilience pattern — matching the original monolith's
                // behavior where NotificationContext.HandleNotification dispatched to
                // listeners via Task.Run(() => listener.Method.Invoke(...)) without
                // propagating exceptions from individual listener invocations.
                //
                // Notification publish failures are logged at Warning level but do NOT
                // propagate to the caller, ensuring that a message bus outage does not
                // break core business operations.
                _logger.LogWarning(
                    ex,
                    "Failed to publish notification event on channel '{Channel}' with CorrelationId '{CorrelationId}'. " +
                    "The notification was not delivered to subscribers.",
                    channel,
                    notificationMessage.CorrelationId);
            }
        }
    }
}
