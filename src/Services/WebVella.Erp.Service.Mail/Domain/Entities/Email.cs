using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebVella.Erp.Service.Mail.Domain.Entities
{
	/// <summary>
	/// Domain entity representing an email message within the Mail microservice.
	/// Preserves full backward compatibility with the monolith WebVella.Erp.Plugins.Mail.Api.Email
	/// contract, including all 17 properties and their snake_case JSON serialization keys.
	/// </summary>
	public class Email
	{
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		[JsonProperty(PropertyName = "service_id")]
		public Guid ServiceId { get; set; }

		[JsonProperty(PropertyName = "sender")]
		public EmailAddress Sender { get; set; }

		[JsonProperty(PropertyName = "recipients")]
		public List<EmailAddress> Recipients { get; set; }

		[JsonProperty(PropertyName = "reply_to_email")]
		public string ReplyToEmail { get; set; }

		[JsonProperty(PropertyName = "subject")]
		public string Subject { get; set; }

		[JsonProperty(PropertyName = "content_text")]
		public string ContentText { get; set; }

		[JsonProperty(PropertyName = "content_html")]
		public string ContentHtml { get; set; }

		[JsonProperty(PropertyName = "created_on")]
		public DateTime CreatedOn { get; set; }

		[JsonProperty(PropertyName = "sent_on")]
		public DateTime? SentOn { get; set; }

		[JsonProperty(PropertyName = "status")]
		public EmailStatus Status { get; set; }

		[JsonProperty(PropertyName = "priority")]
		public EmailPriority Priority { get; set; }

		[JsonProperty(PropertyName = "server_error")]
		public string ServerError { get; set; }

		[JsonProperty(PropertyName = "scheduled_on")]
		public DateTime? ScheduledOn { get; set; }

		[JsonProperty(PropertyName = "retries_count")]
		public int RetriesCount { get; set; }

		[JsonProperty(PropertyName = "x_search")]
		public string XSearch { get; set; }

		[JsonProperty(PropertyName = "attachments")]
		public List<string> Attachments { get; set; } = new List<string>();
	}
}
