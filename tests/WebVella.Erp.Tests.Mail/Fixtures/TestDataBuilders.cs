using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace WebVella.Erp.Tests.Mail.Fixtures
{
    /// <summary>
    /// Provides integer constants mirroring the EmailStatus and EmailPriority enums
    /// from the Mail service domain models. Using named constants instead of raw integers
    /// improves test readability and ensures consistency with the source enum definitions.
    /// </summary>
    /// <remarks>
    /// Values sourced from:
    ///   - WebVella.Erp.Plugins.Mail/Api/EmailStatus.cs  (Pending=0, Sent=1, Aborted=2)
    ///   - WebVella.Erp.Plugins.Mail/Api/EmailPriority.cs (Low=0, Normal=1, High=2)
    /// </remarks>
    public static class MailTestConstants
    {
        // EmailStatus enum values (from EmailStatus.cs)
        /// <summary>Email is queued and waiting to be sent.</summary>
        public const int StatusPending = 0;

        /// <summary>Email was successfully delivered via SMTP.</summary>
        public const int StatusSent = 1;

        /// <summary>Email delivery was permanently aborted after exhausting retries.</summary>
        public const int StatusAborted = 2;

        // EmailPriority enum values (from EmailPriority.cs)
        /// <summary>Low priority — processed after Normal and High priority emails.</summary>
        public const int PriorityLow = 0;

        /// <summary>Normal priority — default processing order.</summary>
        public const int PriorityNormal = 1;

        /// <summary>High priority — processed before Normal and Low priority emails.</summary>
        public const int PriorityHigh = 2;
    }

    /// <summary>
    /// Fluent builder for constructing SmtpService test data as Dictionary&lt;string, object&gt;
    /// representations. Dictionary keys match the [JsonProperty(PropertyName)] annotations
    /// from the source SmtpService.cs model exactly, ensuring API contract compatibility.
    /// </summary>
    /// <remarks>
    /// The builder intentionally does NOT enforce validation constraints (port range, retries range, etc.)
    /// because test scenarios may need deliberately invalid values to verify validation logic.
    /// Default values represent a valid, enabled SMTP service configuration suitable for most test cases.
    /// 
    /// Source: WebVella.Erp.Plugins.Mail/Api/SmtpService.cs (14 properties, lines 21-61)
    /// </remarks>
    public class SmtpServiceBuilder
    {
        private Guid _id = Guid.NewGuid();
        private string _name = "test-smtp";
        private string _server = "localhost";
        private int _port = 587;
        private string _username = "";
        private string _password = "";
        private string _defaultSenderName = "Test Sender";
        private string _defaultSenderEmail = "noreply@test.webvella.com";
        private string _defaultReplyToEmail = "";
        private int _maxRetriesCount = 3;
        private int _retryWaitMinutes = 5;
        private bool _isDefault = true;
        private bool _isEnabled = true;
        private int _connectionSecurity = 1; // SecureSocketOptions.Auto

        /// <summary>Sets the unique identifier for the SMTP service record.</summary>
        /// <param name="id">SMTP service GUID. Maps to JsonProperty "id".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithId(Guid id)
        {
            _id = id;
            return this;
        }

        /// <summary>Sets the display name for the SMTP service configuration.</summary>
        /// <param name="name">Human-readable service name. Maps to JsonProperty "name".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithName(string name)
        {
            _name = name;
            return this;
        }

        /// <summary>Sets the SMTP server hostname or IP address.</summary>
        /// <param name="server">Server address. Maps to JsonProperty "server".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithServer(string server)
        {
            _server = server;
            return this;
        }

        /// <summary>Sets the SMTP server port number.</summary>
        /// <param name="port">Port number (valid range: 1-65025 per SmtpInternalService validation). Maps to JsonProperty "port".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithPort(int port)
        {
            _port = port;
            return this;
        }

        /// <summary>Sets the SMTP authentication username.</summary>
        /// <param name="username">Authentication username. Maps to JsonProperty "username".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithUsername(string username)
        {
            _username = username;
            return this;
        }

        /// <summary>Sets the SMTP authentication password.</summary>
        /// <param name="password">Authentication password. Maps to JsonProperty "password".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithPassword(string password)
        {
            _password = password;
            return this;
        }

        /// <summary>Sets the default sender display name for outgoing emails.</summary>
        /// <param name="name">Sender display name. Maps to JsonProperty "default_sender_name".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithDefaultSenderName(string name)
        {
            _defaultSenderName = name;
            return this;
        }

        /// <summary>Sets the default sender email address for outgoing emails.</summary>
        /// <param name="email">Sender email address (must be valid email format per SmtpInternalService validation). Maps to JsonProperty "default_sender_email".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithDefaultSenderEmail(string email)
        {
            _defaultSenderEmail = email;
            return this;
        }

        /// <summary>Sets the default reply-to email address for outgoing emails.</summary>
        /// <param name="email">Reply-to email address. Maps to JsonProperty "default_reply_to_email".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithDefaultReplyToEmail(string email)
        {
            _defaultReplyToEmail = email;
            return this;
        }

        /// <summary>Sets the maximum number of delivery retry attempts.</summary>
        /// <param name="count">Retry count (valid range: 1-10 per SmtpInternalService validation). Maps to JsonProperty "max_retries_count".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithMaxRetriesCount(int count)
        {
            _maxRetriesCount = count;
            return this;
        }

        /// <summary>Sets the wait time between delivery retry attempts.</summary>
        /// <param name="minutes">Minutes between retries (valid range: 1-1440 per SmtpInternalService validation). Maps to JsonProperty "retry_wait_minutes".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithRetryWaitMinutes(int minutes)
        {
            _retryWaitMinutes = minutes;
            return this;
        }

        /// <summary>Sets whether this is the default SMTP service used for sending.</summary>
        /// <param name="isDefault">True if default service. Maps to JsonProperty "is_default".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithIsDefault(bool isDefault)
        {
            _isDefault = isDefault;
            return this;
        }

        /// <summary>Sets whether this SMTP service is enabled for email delivery.</summary>
        /// <param name="isEnabled">True if enabled. Maps to JsonProperty "is_enabled".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithIsEnabled(bool isEnabled)
        {
            _isEnabled = isEnabled;
            return this;
        }

        /// <summary>Sets the connection security mode for the SMTP connection.</summary>
        /// <param name="connectionSecurity">
        /// Integer value matching MailKit.Security.SecureSocketOptions enum:
        /// 0=None, 1=Auto, 2=SslOnConnect, 3=StartTls, 4=StartTlsWhenAvailable.
        /// Maps to JsonProperty "connection_security".
        /// </param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public SmtpServiceBuilder WithConnectionSecurity(int connectionSecurity)
        {
            _connectionSecurity = connectionSecurity;
            return this;
        }

        /// <summary>
        /// Builds a Dictionary&lt;string, object&gt; representing an SmtpService record.
        /// All keys use snake_case matching the [JsonProperty(PropertyName)] annotations
        /// from the source SmtpService.cs model exactly.
        /// </summary>
        /// <returns>
        /// A dictionary with 14 entries whose keys match the SmtpService JSON contract:
        /// id, name, server, port, username, password, default_sender_name, default_sender_email,
        /// default_reply_to_email, max_retries_count, retry_wait_minutes, is_default, is_enabled,
        /// connection_security.
        /// </returns>
        public Dictionary<string, object> Build()
        {
            return new Dictionary<string, object>
            {
                ["id"] = _id,
                ["name"] = _name,
                ["server"] = _server,
                ["port"] = _port,
                ["username"] = _username,
                ["password"] = _password,
                ["default_sender_name"] = _defaultSenderName,
                ["default_sender_email"] = _defaultSenderEmail,
                ["default_reply_to_email"] = _defaultReplyToEmail,
                ["max_retries_count"] = _maxRetriesCount,
                ["retry_wait_minutes"] = _retryWaitMinutes,
                ["is_default"] = _isDefault,
                ["is_enabled"] = _isEnabled,
                ["connection_security"] = _connectionSecurity
            };
        }
    }

    /// <summary>
    /// Fluent builder for constructing Email test data as Dictionary&lt;string, object&gt;
    /// representations. Dictionary keys match the [JsonProperty(PropertyName)] annotations
    /// from the source Email.cs model exactly, ensuring API contract compatibility.
    /// </summary>
    /// <remarks>
    /// Sender and recipients are represented as nested Dictionary&lt;string, object&gt; instances
    /// matching the EmailAddress JSON shape, since the source Email class has internal setters
    /// and internal constructors that prevent direct instantiation from test projects.
    ///
    /// Convenience factory methods (PendingEmail, SentEmail, AbortedEmail) provide pre-configured
    /// builders for the most common test scenarios.
    ///
    /// Source: WebVella.Erp.Plugins.Mail/Api/Email.cs (17 properties, lines 9-58)
    /// </remarks>
    public class EmailBuilder
    {
        private Guid _id = Guid.NewGuid();
        private Guid _serviceId = Guid.Empty;
        private Dictionary<string, object> _sender;
        private List<Dictionary<string, object>> _recipients;
        private string _replyToEmail = "";
        private string _subject = "Test Email Subject";
        private string _contentText = "This is a test email body.";
        private string _contentHtml = "<p>This is a test email body.</p>";
        private DateTime _createdOn = DateTime.UtcNow;
        private DateTime? _sentOn = null;
        private int _status = MailTestConstants.StatusPending;
        private int _priority = MailTestConstants.PriorityNormal;
        private string _serverError = "";
        private DateTime? _scheduledOn = null;
        private int _retriesCount = 0;
        private string _xSearch = "";
        private List<string> _attachments;

        /// <summary>
        /// Initializes a new EmailBuilder with sensible defaults for all 17 properties.
        /// The default sender is "Test Sender &lt;noreply@test.webvella.com&gt;" and
        /// there is one default recipient "Recipient &lt;recipient@test.com&gt;".
        /// </summary>
        public EmailBuilder()
        {
            _sender = new Dictionary<string, object>
            {
                ["name"] = "Test Sender",
                ["address"] = "noreply@test.webvella.com"
            };
            _recipients = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["name"] = "Recipient",
                    ["address"] = "recipient@test.com"
                }
            };
            _attachments = new List<string>();
        }

        /// <summary>Sets the unique identifier for the email record.</summary>
        /// <param name="id">Email GUID. Maps to JsonProperty "id".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithId(Guid id)
        {
            _id = id;
            return this;
        }

        /// <summary>Sets the SMTP service identifier that will send/sent this email.</summary>
        /// <param name="serviceId">SMTP service GUID. Maps to JsonProperty "service_id".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithServiceId(Guid serviceId)
        {
            _serviceId = serviceId;
            return this;
        }

        /// <summary>Sets the email sender using name and address components.</summary>
        /// <param name="name">Sender display name.</param>
        /// <param name="address">Sender email address.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithSender(string name, string address)
        {
            _sender = new Dictionary<string, object>
            {
                ["name"] = name,
                ["address"] = address
            };
            return this;
        }

        /// <summary>Replaces the entire recipients list with the provided collection.</summary>
        /// <param name="recipients">List of recipient dictionaries with "name" and "address" keys.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithRecipients(List<Dictionary<string, object>> recipients)
        {
            _recipients = recipients;
            return this;
        }

        /// <summary>Appends a single recipient to the existing recipients list.</summary>
        /// <param name="name">Recipient display name.</param>
        /// <param name="address">Recipient email address.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder AddRecipient(string name, string address)
        {
            _recipients.Add(new Dictionary<string, object>
            {
                ["name"] = name,
                ["address"] = address
            });
            return this;
        }

        /// <summary>Sets the reply-to email address for this email.</summary>
        /// <param name="email">Reply-to address. Maps to JsonProperty "reply_to_email".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithReplyToEmail(string email)
        {
            _replyToEmail = email;
            return this;
        }

        /// <summary>Sets the email subject line.</summary>
        /// <param name="subject">Email subject. Maps to JsonProperty "subject".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithSubject(string subject)
        {
            _subject = subject;
            return this;
        }

        /// <summary>Sets the plain text email body content.</summary>
        /// <param name="text">Plain text body. Maps to JsonProperty "content_text".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithContentText(string text)
        {
            _contentText = text;
            return this;
        }

        /// <summary>Sets the HTML email body content.</summary>
        /// <param name="html">HTML body. Maps to JsonProperty "content_html".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithContentHtml(string html)
        {
            _contentHtml = html;
            return this;
        }

        /// <summary>Sets the timestamp when the email record was created.</summary>
        /// <param name="createdOn">Creation timestamp (UTC). Maps to JsonProperty "created_on".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithCreatedOn(DateTime createdOn)
        {
            _createdOn = createdOn;
            return this;
        }

        /// <summary>Sets the timestamp when the email was actually sent.</summary>
        /// <param name="sentOn">Sent timestamp (UTC), or null if not yet sent. Maps to JsonProperty "sent_on".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithSentOn(DateTime? sentOn)
        {
            _sentOn = sentOn;
            return this;
        }

        /// <summary>Sets the email delivery status.</summary>
        /// <param name="status">
        /// Status integer matching EmailStatus enum: 0=Pending, 1=Sent, 2=Aborted.
        /// Use MailTestConstants for readability. Maps to JsonProperty "status".
        /// </param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithStatus(int status)
        {
            _status = status;
            return this;
        }

        /// <summary>Sets the email delivery priority.</summary>
        /// <param name="priority">
        /// Priority integer matching EmailPriority enum: 0=Low, 1=Normal, 2=High.
        /// Use MailTestConstants for readability. Maps to JsonProperty "priority".
        /// </param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithPriority(int priority)
        {
            _priority = priority;
            return this;
        }

        /// <summary>Sets the server error message from the last failed delivery attempt.</summary>
        /// <param name="error">Error message string. Maps to JsonProperty "server_error".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithServerError(string error)
        {
            _serverError = error;
            return this;
        }

        /// <summary>Sets the scheduled delivery time for deferred email sending.</summary>
        /// <param name="scheduledOn">Scheduled timestamp (UTC), or null for immediate. Maps to JsonProperty "scheduled_on".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithScheduledOn(DateTime? scheduledOn)
        {
            _scheduledOn = scheduledOn;
            return this;
        }

        /// <summary>Sets the number of delivery retry attempts already made.</summary>
        /// <param name="count">Retry count. Maps to JsonProperty "retries_count".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithRetriesCount(int count)
        {
            _retriesCount = count;
            return this;
        }

        /// <summary>Sets the full-text search index content for this email record.</summary>
        /// <param name="xSearch">Search index string. Maps to JsonProperty "x_search".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithXSearch(string xSearch)
        {
            _xSearch = xSearch;
            return this;
        }

        /// <summary>Sets the list of file paths for email attachments.</summary>
        /// <param name="attachments">List of attachment file paths. Maps to JsonProperty "attachments".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailBuilder WithAttachments(List<string> attachments)
        {
            _attachments = attachments;
            return this;
        }

        /// <summary>
        /// Builds a Dictionary&lt;string, object&gt; representing an Email record.
        /// All keys use snake_case matching the [JsonProperty(PropertyName)] annotations
        /// from the source Email.cs model exactly.
        /// </summary>
        /// <returns>
        /// A dictionary with 17 entries whose keys match the Email JSON contract:
        /// id, service_id, sender, recipients, reply_to_email, subject, content_text,
        /// content_html, created_on, sent_on, status, priority, server_error,
        /// scheduled_on, retries_count, x_search, attachments.
        /// </returns>
        public Dictionary<string, object> Build()
        {
            return new Dictionary<string, object>
            {
                ["id"] = _id,
                ["service_id"] = _serviceId,
                ["sender"] = _sender,
                ["recipients"] = _recipients,
                ["reply_to_email"] = _replyToEmail,
                ["subject"] = _subject,
                ["content_text"] = _contentText,
                ["content_html"] = _contentHtml,
                ["created_on"] = _createdOn,
                ["sent_on"] = _sentOn!,
                ["status"] = _status,
                ["priority"] = _priority,
                ["server_error"] = _serverError,
                ["scheduled_on"] = _scheduledOn!,
                ["retries_count"] = _retriesCount,
                ["x_search"] = _xSearch,
                ["attachments"] = _attachments
            };
        }

        /// <summary>
        /// Creates a pre-configured EmailBuilder for a pending (queued) email.
        /// Sets Status=Pending (0) and ScheduledOn=DateTime.UtcNow.
        /// </summary>
        /// <returns>A new EmailBuilder configured for a pending email scenario.</returns>
        public static EmailBuilder PendingEmail()
        {
            return new EmailBuilder()
                .WithStatus(MailTestConstants.StatusPending)
                .WithScheduledOn(DateTime.UtcNow);
        }

        /// <summary>
        /// Creates a pre-configured EmailBuilder for a successfully sent email.
        /// Sets Status=Sent (1) and SentOn=DateTime.UtcNow.
        /// </summary>
        /// <returns>A new EmailBuilder configured for a sent email scenario.</returns>
        public static EmailBuilder SentEmail()
        {
            return new EmailBuilder()
                .WithStatus(MailTestConstants.StatusSent)
                .WithSentOn(DateTime.UtcNow);
        }

        /// <summary>
        /// Creates a pre-configured EmailBuilder for an aborted (failed) email.
        /// Sets Status=Aborted (2), ServerError="Test failure reason", and RetriesCount=3.
        /// </summary>
        /// <returns>A new EmailBuilder configured for an aborted email scenario.</returns>
        public static EmailBuilder AbortedEmail()
        {
            return new EmailBuilder()
                .WithStatus(MailTestConstants.StatusAborted)
                .WithServerError("Test failure reason")
                .WithRetriesCount(3);
        }
    }

    /// <summary>
    /// Fluent builder for constructing EmailAddress test data as Dictionary&lt;string, object&gt;
    /// representations. Dictionary keys match the [JsonProperty(PropertyName)] annotations
    /// from the source EmailAddress.cs model exactly.
    /// </summary>
    /// <remarks>
    /// The EmailAddress model has two properties: "name" and "address", both strings.
    /// The source model supports three constructor overloads (empty, address-only, name+address)
    /// and a static factory FromMailboxAddress(). The builder covers all scenarios via fluent setters.
    ///
    /// Source: WebVella.Erp.Plugins.Mail/Api/EmailAddress.cs (2 properties, lines 8-12)
    /// </remarks>
    public class EmailAddressBuilder
    {
        private string _name = "Test User";
        private string _address = "testuser@test.com";

        /// <summary>Sets the display name for the email address.</summary>
        /// <param name="name">Display name. Maps to JsonProperty "name".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailAddressBuilder WithName(string name)
        {
            _name = name;
            return this;
        }

        /// <summary>Sets the email address string.</summary>
        /// <param name="address">Email address. Maps to JsonProperty "address".</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public EmailAddressBuilder WithAddress(string address)
        {
            _address = address;
            return this;
        }

        /// <summary>
        /// Builds a Dictionary&lt;string, object&gt; representing an EmailAddress record.
        /// Keys match the [JsonProperty(PropertyName)] annotations from EmailAddress.cs.
        /// </summary>
        /// <returns>
        /// A dictionary with 2 entries: "name" and "address".
        /// </returns>
        public Dictionary<string, object> Build()
        {
            return new Dictionary<string, object>
            {
                ["name"] = _name,
                ["address"] = _address
            };
        }

        /// <summary>
        /// Creates a new EmailAddressBuilder with default values
        /// (Name="Test User", Address="testuser@test.com").
        /// </summary>
        /// <returns>A new EmailAddressBuilder with default test values.</returns>
        public static EmailAddressBuilder Default()
        {
            return new EmailAddressBuilder();
        }

        /// <summary>
        /// Creates a new EmailAddressBuilder with a custom email address and a name
        /// derived from the address prefix (the part before the '@' symbol).
        /// </summary>
        /// <param name="address">Email address to use. The name is derived from the local part.</param>
        /// <returns>A new EmailAddressBuilder configured with the specified address.</returns>
        public static EmailAddressBuilder WithTestAddress(string address)
        {
            // Derive the display name from the local part of the email address.
            // For example, "john.doe@example.com" produces "john.doe" as the name.
            string derivedName = address;
            int atIndex = address.IndexOf('@');
            if (atIndex > 0)
            {
                derivedName = address.Substring(0, atIndex);
            }

            return new EmailAddressBuilder()
                .WithName(derivedName)
                .WithAddress(address);
        }
    }
}
