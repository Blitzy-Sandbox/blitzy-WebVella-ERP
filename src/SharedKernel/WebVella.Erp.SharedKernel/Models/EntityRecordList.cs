using Newtonsoft.Json;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Pagination-aware list of <see cref="EntityRecord"/> instances returned by
	/// record queries. Extends <see cref="List{EntityRecord}"/> so it can be
	/// enumerated directly while also carrying <see cref="TotalCount"/> for
	/// offset-based pagination.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.EntityRecordList</c>
	/// to maintain backward compatibility with EQL query results, REST API v3
	/// paginated responses, and cross-service data exchange.
	/// </summary>
	public class EntityRecordList : List<EntityRecord>
	{
		/// <summary>
		/// Total number of records matching the query, regardless of offset or limit.
		/// Used by clients for pagination calculations (total pages, "showing X of Y", etc.).
		/// </summary>
		[JsonProperty(PropertyName = "total_count")]
		public int TotalCount { get; set; } = 0;
	}
}
