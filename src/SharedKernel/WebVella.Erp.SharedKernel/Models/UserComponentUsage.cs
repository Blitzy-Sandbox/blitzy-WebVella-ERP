using Newtonsoft.Json;
using System;

namespace WebVella.Erp.SharedKernel.Models
{
	public class UserComponentUsage
	{
		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; } = ""; //component name

		[JsonProperty(PropertyName = "sdk_used")]
		public int SdkUsed { get; set; } = 0;

		[JsonProperty(PropertyName = "sdk_used_on")]
		public DateTime SdkUsedOn { get; set; } = DateTime.MinValue;
	}
}
