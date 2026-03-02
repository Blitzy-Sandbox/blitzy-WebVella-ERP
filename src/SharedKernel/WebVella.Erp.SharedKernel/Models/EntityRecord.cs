using Newtonsoft.Json;
using System;
using WebVella.Erp.SharedKernel.Utilities.Dynamic;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// The foundational data carrier for all record operations across every microservice.
	/// Inherits from <see cref="Expando"/> to provide dynamic property access via a
	/// Properties dictionary, enabling the dynamic entity-field model where field names
	/// are determined at runtime from entity metadata.
	///
	/// Preserved exactly from the monolith's <c>WebVella.Erp.Api.Models.EntityRecord</c>
	/// to maintain backward compatibility with all EQL query results, REST API v3
	/// responses, and cross-service data exchange.
	///
	/// Usage pattern:
	/// <code>
	///   var record = new EntityRecord();
	///   record["name"] = "Acme Corp";
	///   record["id"] = Guid.NewGuid();
	///   string name = (string)record["name"];
	/// </code>
	/// </summary>
	[Serializable]
	public class EntityRecord : Expando
	{
	}
}
