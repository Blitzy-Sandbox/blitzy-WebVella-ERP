using Newtonsoft.Json;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Contains the result data of a record query — field metadata plus
	/// the matching records. Used as the payload of <see cref="QueryResponse"/>.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.QueryResult</c>
	/// to maintain backward compatibility with EQL query results and
	/// REST API v3 record query responses.
	/// </summary>
	public class QueryResult
	{
		/// <summary>
		/// Metadata for each field included in the query result columns.
		/// Provides field type, label, and configuration for client rendering.
		/// </summary>
		[JsonProperty(PropertyName = "fieldsMeta")]
		public List<Field> FieldsMeta { get; set; }

		/// <summary>
		/// The matching records. Each <see cref="EntityRecord"/> is a dynamic
		/// property bag with field names as keys and field values as values.
		/// </summary>
		[JsonProperty(PropertyName = "data")]
		public List<EntityRecord> Data { get; set; }
	}

	/// <summary>
	/// Response envelope for EQL and record query results.
	/// Wraps a <see cref="QueryResult"/> in the standard BaseResponseModel
	/// envelope (success, errors, timestamp, message, object).
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.QueryResponse</c>
	/// to maintain backward compatibility with the REST API v3 contract
	/// for record queries executed by RecordManager.
	/// </summary>
	public class QueryResponse : BaseResponseModel
	{
		public QueryResponse()
		{
			Object = new QueryResult();
		}

		[JsonProperty(PropertyName = "object")]
		public QueryResult Object { get; set; }
	}
}
