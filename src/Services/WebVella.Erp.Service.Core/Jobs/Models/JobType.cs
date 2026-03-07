using Newtonsoft.Json;
using System;

namespace WebVella.Erp.Service.Core.Jobs
{
	/// <summary>
	/// Job priority enumeration for scheduling precedence.
	/// Preserved exactly from monolith <c>WebVella.Erp.Jobs.JobPriority</c>.
	/// </summary>
	[Serializable]
	public enum JobPriority
	{
		Low = 1,
		Medium = 2,
		High = 3,
		Higher = 4,
		Highest = 5
	}

	/// <summary>
	/// Represents a registered job type with its CLR type reference, priority, and single-instance constraint.
	/// Preserved exactly from monolith <c>WebVella.Erp.Jobs.JobType</c>.
	/// </summary>
	[Serializable]
	public class JobType
	{
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; }

		[JsonProperty(PropertyName = "default_job_priority_id")]
		public JobPriority DefaultPriority { get; set; }

		[JsonProperty(PropertyName = "assembly")]
		public string Assembly { get; set; }

		[JsonProperty(PropertyName = "complete_class_name")]
		public string CompleteClassName { get; set; }

		[JsonProperty(PropertyName = "allow_single_instance")]
		public bool AllowSingleInstance { get; set; }

		[JsonIgnore]
		public Type ErpJobType { get; set; }
	}
}
