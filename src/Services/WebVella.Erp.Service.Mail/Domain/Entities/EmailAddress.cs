using MimeKit;
using Newtonsoft.Json;

namespace WebVella.Erp.Service.Mail.Domain.Entities
{
	/// <summary>
	/// Value object DTO representing an email address with display name and address components.
	/// Preserves full backward compatibility with the monolith WebVella.Erp.Plugins.Mail.Api.EmailAddress
	/// contract, including JSON serialization keys (snake_case via [JsonProperty]).
	/// </summary>
	public class EmailAddress
	{
		/// <summary>
		/// Display name portion of the email address (e.g., "John Doe").
		/// Serialized as "name" in JSON for API contract compatibility.
		/// </summary>
		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; } = string.Empty;

		/// <summary>
		/// Email address portion (e.g., "john.doe@example.com").
		/// Serialized as "address" in JSON for API contract compatibility.
		/// </summary>
		[JsonProperty(PropertyName = "address")]
		public string Address { get; set; } = string.Empty;

		/// <summary>
		/// Parameterless constructor for deserialization and default initialization.
		/// </summary>
		public EmailAddress()
		{ }

		/// <summary>
		/// Constructs an EmailAddress with the specified email address and an empty display name.
		/// </summary>
		/// <param name="address">The email address string.</param>
		public EmailAddress(string address)
		{
			Address = address;
		}

		/// <summary>
		/// Constructs an EmailAddress with both a display name and email address.
		/// </summary>
		/// <param name="name">The display name for the email address.</param>
		/// <param name="address">The email address string.</param>
		public EmailAddress(string name, string address)
		{
			Name = name;
			Address = address;
		}

		/// <summary>
		/// Creates an <see cref="EmailAddress"/> from a MimeKit <see cref="MailboxAddress"/>,
		/// mapping the Name and Address properties.
		/// </summary>
		/// <param name="mbAddress">The MimeKit mailbox address to convert.</param>
		/// <returns>A new <see cref="EmailAddress"/> with Name and Address populated from the MimeKit source.</returns>
		public static EmailAddress FromMailboxAddress(MailboxAddress mbAddress)
		{
			return new EmailAddress { Name = mbAddress.Name, Address = mbAddress.Address };
		}
	}
}
