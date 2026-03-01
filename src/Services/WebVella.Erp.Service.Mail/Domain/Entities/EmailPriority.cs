namespace WebVella.Erp.Service.Mail.Domain.Entities
{
	/// <summary>
	/// Defines the priority levels for email messages.
	/// Numeric values are stable and used for database persistence and JSON serialization.
	/// Do not change the assigned integer values without a corresponding data migration.
	/// </summary>
	public enum EmailPriority
	{
		Low = 0,
		Normal = 1,
		High = 2
	}
}
