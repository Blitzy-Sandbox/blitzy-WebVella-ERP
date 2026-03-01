using Newtonsoft.Json;

namespace WebVella.Erp.SharedKernel.Eql
{
	public class EqlSettings
	{
		[JsonProperty(PropertyName = "include_total")]
		public bool IncludeTotal { get; init; } = true;

		[JsonProperty(PropertyName = "distinct")]
		public bool Distinct { get; init; } = false;
	}
}
