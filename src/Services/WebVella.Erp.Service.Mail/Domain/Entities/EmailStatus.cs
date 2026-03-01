namespace WebVella.Erp.Service.Mail.Domain.Entities
{
	/// <summary>
	/// Represents the processing status of an email in the mail queue.
	/// Numeric values are stable and used for database persistence and JSON serialization.
	/// Do not change the assigned integer values without a corresponding data migration.
	/// </summary>
	public enum EmailStatus
	{
		/// <summary>
		/// Email is queued and awaiting processing by the mail queue job.
		/// </summary>
		Pending = 0,

		/// <summary>
		/// Email has been successfully sent via SMTP.
		/// </summary>
		Sent = 1,

		/// <summary>
		/// Email processing was aborted due to an error or manual cancellation.
		/// </summary>
		Aborted = 2
	}
}
