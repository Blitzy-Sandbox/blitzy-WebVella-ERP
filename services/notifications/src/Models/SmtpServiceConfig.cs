using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Notifications.Models
{
    /// <summary>
    /// SMTP service configuration model for the Notifications microservice.
    /// Extracted from the properties section of WebVella.Erp.Plugins.Mail/Api/SmtpService.cs (lines 19-63).
    /// Renamed from SmtpService to SmtpServiceConfig to avoid collision with the service-layer class.
    ///
    /// Key changes from source monolith:
    ///   - Newtonsoft [JsonProperty] → System.Text.Json [JsonPropertyName] for Native AOT compatibility
    ///   - internal set → public set (no longer plugin-internal)
    ///   - ConnectionSecurity type changed from MailKit.Security.SecureSocketOptions enum to int
    ///     (MailKit dependency removed; stored as plain integer)
    ///   - Namespace changed from WebVella.Erp.Plugins.Mail.Api to WebVellaErp.Notifications.Models
    ///   - Business logic methods (SendEmail, QueueEmail) excluded — moved to Services layer
    /// </summary>
    public class SmtpServiceConfig
    {
        /// <summary>
        /// Unique identifier for this SMTP service configuration.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Display name for this SMTP service configuration.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// SMTP server hostname or IP address.
        /// </summary>
        [JsonPropertyName("server")]
        public string Server { get; set; } = string.Empty;

        /// <summary>
        /// SMTP server port number (e.g., 25, 465, 587).
        /// </summary>
        [JsonPropertyName("port")]
        public int Port { get; set; }

        /// <summary>
        /// Username for SMTP server authentication.
        /// </summary>
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Password for SMTP server authentication.
        /// Stored encrypted via SSM SecureString in production per AAP §0.8.6.
        /// </summary>
        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Default sender display name used in outgoing emails.
        /// </summary>
        [JsonPropertyName("default_sender_name")]
        public string DefaultSenderName { get; set; } = string.Empty;

        /// <summary>
        /// Default sender email address used in outgoing emails.
        /// </summary>
        [JsonPropertyName("default_sender_email")]
        public string DefaultSenderEmail { get; set; } = string.Empty;

        /// <summary>
        /// Default reply-to email address for outgoing emails.
        /// </summary>
        [JsonPropertyName("default_reply_to_email")]
        public string DefaultReplyToEmail { get; set; } = string.Empty;

        /// <summary>
        /// Maximum number of retry attempts for failed email deliveries.
        /// </summary>
        [JsonPropertyName("max_retries_count")]
        public int MaxRetriesCount { get; set; }

        /// <summary>
        /// Wait time in minutes between retry attempts for failed email deliveries.
        /// </summary>
        [JsonPropertyName("retry_wait_minutes")]
        public int RetryWaitMinutes { get; set; }

        /// <summary>
        /// Whether this is the default SMTP service configuration used when no specific service is requested.
        /// </summary>
        [JsonPropertyName("is_default")]
        public bool IsDefault { get; set; }

        /// <summary>
        /// Whether this SMTP service configuration is active and available for use.
        /// </summary>
        [JsonPropertyName("is_enabled")]
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Connection security mode for the SMTP server connection.
        /// Stored as integer (not MailKit.Security.SecureSocketOptions enum — MailKit dependency removed).
        /// Values correspond to: 0 = None, 1 = Auto, 2 = SslOnConnect, 3 = StartTls, 4 = StartTlsWhenAvailable.
        /// </summary>
        [JsonPropertyName("connection_security")]
        public int ConnectionSecurity { get; set; }
    }
}
