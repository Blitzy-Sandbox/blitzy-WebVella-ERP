using Newtonsoft.Json;
using System;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Sort direction enumeration for record query ordering.
	/// Used by <see cref="QuerySortObject"/> and the EQL engine's ORDER BY clause.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.QuerySortType</c>.
	/// </summary>
	public enum QuerySortType
	{
		/// <summary>Ascending sort order (ASC).</summary>
		[SelectOption(Label = "asc")]
		Ascending,
		/// <summary>Descending sort order (DESC).</summary>
		[SelectOption(Label = "desc")]
		Descending
	}

	/// <summary>
	/// Sort specification for record queries, pairing a field name with a
	/// sort direction. Multiple instances can be combined in a list for
	/// multi-column sorting.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.QuerySortObject</c>
	/// to maintain backward compatibility with record query operations,
	/// EQL ORDER BY clauses, and REST API v3 sort parameters.
	/// </summary>
	[Serializable]
	public class QuerySortObject
	{
		/// <summary>
		/// The entity field name to sort by.
		/// </summary>
		[JsonProperty(PropertyName = "fieldName")]
		public string FieldName { get; private set; }

		/// <summary>
		/// The sort direction (ascending or descending).
		/// </summary>
		[JsonProperty(PropertyName = "sortType")]
		public QuerySortType SortType { get; private set; }

		/// <summary>
		/// Creates a new sort specification with the given field name and direction.
		/// </summary>
		/// <param name="fieldName">Entity field name to sort by.</param>
		/// <param name="sortType">Sort direction.</param>
		public QuerySortObject(string fieldName, QuerySortType sortType)
		{
			FieldName = fieldName;
			SortType = sortType;
		}
	}
}
