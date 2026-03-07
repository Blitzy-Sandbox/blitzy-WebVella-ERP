#region <--- DIRECTIVES --->

using Newtonsoft.Json;
using System;

#endregion

namespace WebVella.Erp.SharedKernel.Database
{
	public abstract class DbDocumentBase
	{
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }
	}
}