using Newtonsoft.Json;

namespace WebVella.Erp.Service.Core.Jobs
{
	/// <summary>
	/// Configuration settings for the job management subsystem.
	/// Contains the database connection string and enabled flag.
	/// Preserved exactly from monolith <c>WebVella.Erp.Jobs.JobManagerSettings</c>.
	/// </summary>
	public class JobManagerSettings
	{
		[JsonProperty(PropertyName = "db_connection_string")]
		public string DbConnectionString { get; set; }

		[JsonProperty(PropertyName = "enabled")]
		public bool Enabled { get; set; }
	}
}
