namespace WebVellaErp.Notifications.Models
{
	/// <summary>
	/// Represents the delivery status of an email message in the Notifications service.
	/// Ported from WebVella.Erp.Plugins.Mail.Api.EmailStatus with namespace change only.
	/// </summary>
	public enum EmailStatus
	{
		/// <summary>Email is queued and awaiting delivery.</summary>
		Pending = 0,

		/// <summary>Email has been successfully sent.</summary>
		Sent = 1,

		/// <summary>Email delivery was aborted due to an error or cancellation.</summary>
		Aborted = 2
	}
}
