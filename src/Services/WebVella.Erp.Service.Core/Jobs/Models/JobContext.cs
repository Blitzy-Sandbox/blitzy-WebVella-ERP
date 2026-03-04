using Newtonsoft.Json;
using System;

namespace WebVella.Erp.Service.Core.Jobs
{
	/// <summary>
	/// Data transfer object holding the execution context for a running job.
	/// Contains the job ID, abort flag, priority, dynamic attributes, result, and type reference.
	/// Preserved exactly from monolith <c>WebVella.Erp.Jobs.JobContext</c>.
	/// </summary>
	public class JobContext
	{
		[JsonProperty(PropertyName = "job_id")]
		public Guid JobId { get; set; }

		[JsonProperty(PropertyName = "aborted")]
		public bool Aborted { get; set; }

		[JsonProperty(PropertyName = "priority")]
		public JobPriority Priority { get; set; }

		[JsonProperty(PropertyName = "attributes")]
		public dynamic Attributes { get; set; }

		[JsonProperty(PropertyName = "result")]
		public dynamic Result { get; set; }

		[JsonProperty(PropertyName = "type")]
		public JobType Type { get; set; }

		internal JobContext()
		{
		}
	}
}
