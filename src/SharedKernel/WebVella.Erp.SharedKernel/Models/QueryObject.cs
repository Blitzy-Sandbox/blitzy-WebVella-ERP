using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Enumeration of all filter operators supported by the record query DSL.
	/// Used by <see cref="QueryObject"/> to specify the type of comparison
	/// or logical combination applied to record filtering.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.QueryType</c>
	/// with all operators intact to maintain backward compatibility with
	/// EQL queries, REST API v3 filter parameters, and cross-service
	/// query execution.
	/// </summary>
	public enum QueryType
	{
		/// <summary>Equality comparison: field == value.</summary>
		EQ,
		/// <summary>Inequality comparison: field != value.</summary>
		NOT,
		/// <summary>Less than comparison: field &lt; value.</summary>
		LT,
		/// <summary>Less than or equal comparison: field &lt;= value.</summary>
		LTE,
		/// <summary>Greater than comparison: field &gt; value.</summary>
		GT,
		/// <summary>Greater than or equal comparison: field &gt;= value.</summary>
		GTE,
		/// <summary>Logical AND: all sub-queries must be true.</summary>
		AND,
		/// <summary>Logical OR: at least one sub-query must be true.</summary>
		OR,
		/// <summary>String contains: field LIKE '%value%'.</summary>
		CONTAINS,
		/// <summary>String starts with: field LIKE 'value%'.</summary>
		STARTSWITH,
		/// <summary>Regular expression match with configurable case sensitivity.</summary>
		REGEX,
		/// <summary>Related record filter: records related via a named relation.</summary>
		RELATED,
		/// <summary>Not-related record filter: records NOT related via a named relation.</summary>
		NOTRELATED,
		/// <summary>Full-text search using PostgreSQL tsvector/tsquery.</summary>
		FTS
	}

	/// <summary>
	/// Regex operator configuration for <see cref="QueryType.REGEX"/> queries.
	/// Controls case sensitivity and match/don't-match behavior.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.QueryObjectRegexOperator</c>.
	/// </summary>
	public enum QueryObjectRegexOperator
	{
		/// <summary>Match with case sensitivity (PostgreSQL ~ operator).</summary>
		MatchCaseSensitive,
		/// <summary>Match with case insensitivity (PostgreSQL ~* operator).</summary>
		MatchCaseInsensitive,
		/// <summary>Don't match with case sensitivity (PostgreSQL !~ operator).</summary>
		DontMatchCaseSensitive,
		/// <summary>Don't match with case insensitivity (PostgreSQL !~* operator).</summary>
		DontMatchCaseInsensitive,
	}

	/// <summary>
	/// Recursive query DSL node for constructing record filter expressions.
	/// Supports comparison operators (EQ, NOT, LT, GT, GTE, LTE, CONTAINS,
	/// STARTSWITH, REGEX, FTS), logical combinators (AND, OR), and relation
	/// traversal (RELATED, NOTRELATED).
	///
	/// Queries are built as trees: leaf nodes have FieldName/FieldValue,
	/// branch nodes (AND/OR) have SubQueries lists.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.QueryObject</c>
	/// to maintain backward compatibility with all record query operations,
	/// REST API v3 filter parameters, and cross-service query execution.
	///
	/// Usage:
	/// <code>
	///   var filter = new QueryObject {
	///       QueryType = QueryType.AND,
	///       SubQueries = new List&lt;QueryObject&gt; {
	///           new QueryObject { QueryType = QueryType.EQ, FieldName = "status", FieldValue = "active" },
	///           new QueryObject { QueryType = QueryType.GT, FieldName = "amount", FieldValue = 100m }
	///       }
	///   };
	/// </code>
	/// </summary>
	[Serializable]
	public class QueryObject
	{
		/// <summary>
		/// The type of query operation (comparison, logical combinator, or relation filter).
		/// </summary>
		[JsonProperty(PropertyName = "queryType")]
		public QueryType QueryType { get; set; }

		/// <summary>
		/// Entity field name to compare against. Used by leaf-node operators
		/// (EQ, NOT, LT, GT, GTE, LTE, CONTAINS, STARTSWITH, REGEX, FTS).
		/// Null for branch operators (AND, OR).
		/// </summary>
		[JsonProperty(PropertyName = "fieldName")]
		public string FieldName { get; set; }

		/// <summary>
		/// Value to compare the field against. The runtime type varies by
		/// field type and operator (string, number, Guid, DateTime, etc.).
		/// Null for branch operators (AND, OR).
		/// </summary>
		[JsonProperty(PropertyName = "fieldValue")]
		public object FieldValue { get; set; }

		/// <summary>
		/// Regex operator configuration for REGEX queries. Controls case
		/// sensitivity and match/don't-match behavior.
		/// </summary>
		[JsonProperty(PropertyName = "regexOperator")]
		public QueryObjectRegexOperator RegexOperator { get; set; }

		/// <summary>
		/// Full-text search language configuration for FTS queries.
		/// Maps to PostgreSQL text search configuration (e.g., "english", "simple").
		/// </summary>
		[JsonProperty(PropertyName = "ftsLanguage")]
		public string FtsLanguage { get; set; }

		/// <summary>
		/// Child query nodes for logical combinators (AND, OR) and relation
		/// filters (RELATED, NOTRELATED). Forms a recursive tree structure.
		/// </summary>
		[JsonProperty(PropertyName = "subQueries")]
		public List<QueryObject> SubQueries { get; set; }
	}
}
