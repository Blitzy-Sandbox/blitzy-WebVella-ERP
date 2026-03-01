using System;
using MailKit.Security;
using Newtonsoft.Json;

namespace WebVella.Erp.Service.Mail.Domain.Entities
{
    /// <summary>
    /// Represents the configuration model for an SMTP service instance used by
    /// the Mail/Notification microservice. This class contains only the
    /// configuration properties extracted from the monolith's
    /// <c>WebVella.Erp.Plugins.Mail.Api.SmtpService</c> class (lines 17-65).
    ///
    /// Business methods (SendEmail, QueueEmail) have been moved to
    /// <c>WebVella.Erp.Service.Mail.Domain.Services.SmtpService</c>.
    ///
    /// The class name was changed from <c>SmtpService</c> to <c>SmtpServiceConfig</c>
    /// to avoid naming collision with the domain service class.
    ///
    /// All <see cref="JsonPropertyAttribute"/> annotations are preserved exactly
    /// to maintain backward compatibility with the REST API v3 contract. The
    /// snake_case <c>PropertyName</c> values are part of the API contract and
    /// MUST NOT be changed.
    /// </summary>
    public class SmtpServiceConfig
    {
        /// <summary>
        /// Gets or sets the unique identifier for this SMTP service configuration record.
        /// </summary>
        [JsonProperty(PropertyName = "id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the display name of this SMTP service configuration.
        /// Used for administrative identification in the UI and logs.
        /// </summary>
        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the hostname or IP address of the SMTP server.
        /// Example: "smtp.gmail.com", "mail.example.com".
        /// </summary>
        [JsonProperty(PropertyName = "server")]
        public string Server { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the port number for the SMTP server connection.
        /// Common values: 25 (unencrypted), 465 (SSL), 587 (STARTTLS).
        /// </summary>
        [JsonProperty(PropertyName = "port")]
        public int Port { get; set; }

        /// <summary>
        /// Gets or sets the username for SMTP server authentication.
        /// May be empty if the server does not require authentication.
        /// </summary>
        [JsonProperty(PropertyName = "username")]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the password for SMTP server authentication.
        /// May be empty if the server does not require authentication.
        /// </summary>
        [JsonProperty(PropertyName = "password")]
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the default sender display name used in outgoing emails
        /// when no explicit sender name is provided.
        /// Maps to the "From" header's display name portion.
        /// </summary>
        [JsonProperty(PropertyName = "default_sender_name")]
        public string DefaultSenderName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the default sender email address used in outgoing emails
        /// when no explicit sender address is provided.
        /// Maps to the "From" header's email address portion.
        /// </summary>
        [JsonProperty(PropertyName = "default_sender_email")]
        public string DefaultSenderEmail { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the default "Reply-To" email address appended to outgoing emails.
        /// When set, recipient email clients will direct replies to this address
        /// instead of the sender address.
        /// </summary>
        [JsonProperty(PropertyName = "default_reply_to_email")]
        public string DefaultReplyToEmail { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the maximum number of retry attempts for failed email deliveries.
        /// When an email fails to send, it will be retried up to this many times
        /// before being marked as permanently failed.
        /// </summary>
        [JsonProperty(PropertyName = "max_retries_count")]
        public int MaxRetriesCount { get; set; }

        /// <summary>
        /// Gets or sets the number of minutes to wait between retry attempts
        /// for failed email deliveries. Controls the backoff interval for
        /// the mail queue processor.
        /// </summary>
        [JsonProperty(PropertyName = "retry_wait_minutes")]
        public int RetryWaitMinutes { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this SMTP service configuration
        /// is the default configuration used when no specific service is requested.
        /// Only one configuration should be marked as default at a time.
        /// </summary>
        [JsonProperty(PropertyName = "is_default")]
        public bool IsDefault { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this SMTP service configuration
        /// is enabled. Disabled configurations are skipped during mail queue processing
        /// and cannot be used for sending emails.
        /// </summary>
        [JsonProperty(PropertyName = "is_enabled")]
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Gets or sets the TLS/SSL connection security option for the SMTP server.
        /// Determines how the connection is secured when communicating with the
        /// SMTP server. Uses MailKit's <see cref="SecureSocketOptions"/> enum:
        /// <list type="bullet">
        ///   <item><description><see cref="SecureSocketOptions.None"/> — No encryption</description></item>
        ///   <item><description><see cref="SecureSocketOptions.Auto"/> — Auto-detect</description></item>
        ///   <item><description><see cref="SecureSocketOptions.SslOnConnect"/> — Implicit SSL/TLS</description></item>
        ///   <item><description><see cref="SecureSocketOptions.StartTls"/> — Explicit STARTTLS (required)</description></item>
        ///   <item><description><see cref="SecureSocketOptions.StartTlsWhenAvailable"/> — STARTTLS if available</description></item>
        /// </list>
        /// </summary>
        [JsonProperty(PropertyName = "connection_security")]
        public SecureSocketOptions ConnectionSecurity { get; set; }
    }
}
