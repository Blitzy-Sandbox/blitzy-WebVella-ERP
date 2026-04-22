using System.Text.Json.Serialization;

namespace WebVellaErp.Notifications.Models
{
    /// <summary>
    /// Represents an email address with an optional display name.
    /// Used throughout the Notifications microservice for specifying
    /// sender, recipient, CC, and BCC addresses in email operations.
    /// </summary>
    /// <remarks>
    /// Ported from WebVella.Erp.Plugins.Mail.Api.EmailAddress.
    /// Migrated from Newtonsoft.Json to System.Text.Json for Native AOT compatibility.
    /// Removed MimeKit dependency (FromMailboxAddress factory method).
    /// </remarks>
    public class EmailAddress
    {
        /// <summary>
        /// Gets or sets the display name associated with the email address.
        /// </summary>
        /// <example>"John Doe"</example>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the email address string (e.g., "user@example.com").
        /// </summary>
        /// <example>"john.doe@example.com"</example>
        [JsonPropertyName("address")]
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// Initializes a new instance of the <see cref="EmailAddress"/> class
        /// with default empty values for both Name and Address.
        /// </summary>
        public EmailAddress()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EmailAddress"/> class
        /// with the specified email address and an empty display name.
        /// </summary>
        /// <param name="address">The email address string.</param>
        public EmailAddress(string address)
        {
            Address = address;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EmailAddress"/> class
        /// with the specified display name and email address.
        /// </summary>
        /// <param name="name">The display name for the email address.</param>
        /// <param name="address">The email address string.</param>
        public EmailAddress(string name, string address)
        {
            Name = name;
            Address = address;
        }
    }
}
