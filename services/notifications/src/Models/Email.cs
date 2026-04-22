using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WebVellaErp.Notifications.Models
{
    /// <summary>
    /// Represents an email message with its full lifecycle metadata.
    /// This is the primary domain model for email operations within the
    /// Notifications microservice, supporting creation, queuing, sending,
    /// scheduling, and retry tracking.
    /// </summary>
    /// <remarks>
    /// Ported from WebVella.Erp.Plugins.Mail.Api.Email.
    /// Key changes from source:
    /// - Namespace changed from WebVella.Erp.Plugins.Mail.Api to WebVellaErp.Notifications.Models
    /// - Migrated from Newtonsoft.Json [JsonProperty] to System.Text.Json [JsonPropertyName] for .NET 9 Native AOT compatibility
    /// - Class accessibility changed from internal to public (no longer plugin-internal)
    /// - All property setters changed from internal to public
    /// - Exact snake_case JSON property names preserved for backward-compatible serialization
    /// </remarks>
    public class Email
    {
        /// <summary>
        /// Gets or sets the unique identifier for this email message.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the SMTP service configuration
        /// used to send this email message.
        /// </summary>
        [JsonPropertyName("service_id")]
        public Guid ServiceId { get; set; }

        /// <summary>
        /// Gets or sets the sender's email address for this message.
        /// References the <see cref="EmailAddress"/> model containing
        /// both a display name and email address string.
        /// </summary>
        [JsonPropertyName("sender")]
        public EmailAddress Sender { get; set; } = new EmailAddress();

        /// <summary>
        /// Gets or sets the list of recipient email addresses for this message.
        /// Each entry is an <see cref="EmailAddress"/> containing both a display
        /// name and email address string.
        /// </summary>
        [JsonPropertyName("recipients")]
        public List<EmailAddress> Recipients { get; set; } = new List<EmailAddress>();

        /// <summary>
        /// Gets or sets the reply-to email address string for this message.
        /// When set, email clients will use this address when the recipient
        /// clicks "Reply" instead of the sender address.
        /// </summary>
        [JsonPropertyName("reply_to_email")]
        public string ReplyToEmail { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the subject line of the email message.
        /// </summary>
        [JsonPropertyName("subject")]
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the plain text content body of the email message.
        /// Used as fallback when the recipient's email client does not support HTML.
        /// </summary>
        [JsonPropertyName("content_text")]
        public string ContentText { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the HTML content body of the email message.
        /// When present, this is the primary content displayed by email clients.
        /// </summary>
        [JsonPropertyName("content_html")]
        public string ContentHtml { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timestamp when this email message was created
        /// and entered the processing queue.
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when this email message was successfully
        /// sent. Null if the email has not yet been sent.
        /// </summary>
        [JsonPropertyName("sent_on")]
        public DateTime? SentOn { get; set; }

        /// <summary>
        /// Gets or sets the current delivery status of this email message.
        /// See <see cref="EmailStatus"/> for possible values: Pending, Sent, Aborted.
        /// </summary>
        [JsonPropertyName("status")]
        public EmailStatus Status { get; set; }

        /// <summary>
        /// Gets or sets the delivery priority of this email message.
        /// See <see cref="EmailPriority"/> for possible values: Low, Normal, High.
        /// </summary>
        [JsonPropertyName("priority")]
        public EmailPriority Priority { get; set; }

        /// <summary>
        /// Gets or sets the server error message captured during the last
        /// failed send attempt. Null or empty when no error has occurred.
        /// </summary>
        [JsonPropertyName("server_error")]
        public string ServerError { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the scheduled delivery timestamp. When set, the email
        /// will not be processed until this time has passed. Null for immediate delivery.
        /// </summary>
        [JsonPropertyName("scheduled_on")]
        public DateTime? ScheduledOn { get; set; }

        /// <summary>
        /// Gets or sets the number of send retry attempts made for this email.
        /// Incremented on each failed delivery attempt before reaching the abort threshold.
        /// </summary>
        [JsonPropertyName("retries_count")]
        public int RetriesCount { get; set; }

        /// <summary>
        /// Gets or sets the composite search index string for this email.
        /// Contains concatenated searchable fields (subject, sender, recipients)
        /// to support efficient text-based lookups.
        /// </summary>
        [JsonPropertyName("x_search")]
        public string XSearch { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the list of attachment identifiers or URIs associated
        /// with this email message. Initialized to an empty list to prevent null reference issues.
        /// </summary>
        [JsonPropertyName("attachments")]
        public List<string> Attachments { get; set; } = new List<string>();
    }
}
