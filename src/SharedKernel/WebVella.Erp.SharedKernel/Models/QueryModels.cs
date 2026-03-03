using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Models
{
	#region <=== Query Enums ===>

	/// <summary>
	/// Defines the set of comparison operators available for constructing query filter conditions
	/// in the EQL engine and record querying system. Each value corresponds to a specific SQL
	/// predicate or composite logical operator used during query translation.
	/// Preserved exactly from WebVella.Erp.Api.Models.QueryType with identical ordinal values.
	/// </summary>
	public enum QueryType
	{
		EQ,
		NOT,
		LT,
		LTE,
		GT,
		GTE,
		AND,
		OR,
		CONTAINS,
		STARTSWITH,
		REGEX,
		RELATED,
		NOTRELATED,
		FTS
	}

	/// <summary>
	/// Specifies the regex matching behavior for REGEX query operations.
	/// Used by <see cref="QueryObject.RegexOperator"/> to control case sensitivity
	/// and match/don't-match semantics in PostgreSQL regex predicates.
	/// Preserved exactly from WebVella.Erp.Api.Models.QueryObjectRegexOperator.
	/// </summary>
	public enum QueryObjectRegexOperator
	{
		MatchCaseSensitive,
		MatchCaseInsensitive,
		DontMatchCaseSensitive,
		DontMatchCaseInsensitive,
	}

	/// <summary>
	/// Defines sort direction for query results. Annotated with <see cref="SelectOptionAttribute"/>
	/// for UI rendering of sort direction options.
	/// Preserved exactly from WebVella.Erp.Api.Models.QuerySortType.
	/// </summary>
	public enum QuerySortType
	{
		[SelectOption(Label = "asc")]
		Ascending,
		[SelectOption(Label = "desc")]
		Descending
	}

	#endregion

	#region <=== Query Filter Objects ===>

	/// <summary>
	/// Represents a single filter condition or composite filter node in a query filter tree.
	/// Leaf nodes have <see cref="QueryType"/>, <see cref="FieldName"/>, and <see cref="FieldValue"/>.
	/// Composite nodes (AND, OR) have <see cref="SubQueries"/> containing child filter nodes.
	/// All properties carry <see cref="JsonPropertyAttribute"/> annotations to ensure stable
	/// JSON serialization for the REST API v3 contract.
	/// Preserved exactly from WebVella.Erp.Api.Models.QueryObject.
	/// </summary>
	[Serializable]
	public class QueryObject
	{
		[JsonProperty(PropertyName = "queryType")]
		public QueryType QueryType { get; set; }

		[JsonProperty(PropertyName = "fieldName")]
		public string FieldName { get; set; }

		[JsonProperty(PropertyName = "fieldValue")]
		public object FieldValue { get; set; }

		[JsonProperty(PropertyName = "regexOperator")]
		public QueryObjectRegexOperator RegexOperator { get; set; }

		[JsonProperty(PropertyName = "ftsLanguage")]
		public string FtsLanguage { get; set; }

		[JsonProperty(PropertyName = "subQueries")]
		public List<QueryObject> SubQueries { get; set; }
	}

	#endregion

	#region <=== Query Sort Objects ===>

	/// <summary>
	/// Represents a single sort directive pairing a field name with a sort direction.
	/// Used in <see cref="EntityQuery.Sort"/> to specify multi-field ordering for query results.
	/// Properties use private setters — values are set through the constructor to ensure
	/// immutability after creation.
	/// Preserved exactly from WebVella.Erp.Api.Models.QuerySortObject.
	/// </summary>
	[Serializable]
	public class QuerySortObject
	{
		[JsonProperty(PropertyName = "fieldName")]
		public string FieldName { get; private set; }

		[JsonProperty(PropertyName = "sortType")]
		public QuerySortType SortType { get; private set; }

		public QuerySortObject(string fieldName, QuerySortType sortType)
		{
			FieldName = fieldName;
			SortType = sortType;
		}
	}

	#endregion

	#region <=== Entity Query ===>

	/// <summary>
	/// The primary query container used by the EQL engine and record managers across all
	/// microservices. Encapsulates entity name, field projection, filter tree, sort directives,
	/// pagination, and argument overrides for a complete query specification.
	///
	/// Provides static factory methods for convenient construction of common filter patterns
	/// (equality, comparison, logical composition, full-text search, and relation queries).
	///
	/// Preserved exactly from WebVella.Erp.Api.Models.EntityQuery, including the
	/// parameterized constructor with validation, [JsonIgnore] on OverwriteArgs, and all
	/// static factory methods with their original signatures and names.
	/// </summary>
	[Serializable]
	public class EntityQuery
	{
		public string EntityName { get; set; }
		public string Fields { get; set; }
		public QueryObject Query { get; set; }
		public QuerySortObject[] Sort { get; set; }
		public int? Skip { get; set; }
		public int? Limit { get; set; }

		[JsonIgnore]
		public List<KeyValuePair<string, string>> OverwriteArgs { get; set; }

		/// <summary>
		/// Constructs a fully specified entity query with validation.
		/// </summary>
		/// <param name="entityName">The name of the entity to query. Must not be null or whitespace.</param>
		/// <param name="fields">Comma-separated field list or "*" for all fields. Defaults to "*".</param>
		/// <param name="query">Optional filter condition tree.</param>
		/// <param name="sort">Optional array of sort directives.</param>
		/// <param name="skip">Optional number of records to skip (pagination offset).</param>
		/// <param name="limit">Optional maximum number of records to return.</param>
		/// <param name="overwriteArgs">Optional argument overrides for parameterized queries.</param>
		/// <exception cref="ArgumentException">Thrown when entityName is null or whitespace.</exception>
		public EntityQuery(string entityName, string fields = "*", QueryObject query = null,
			QuerySortObject[] sort = null, int? skip = null, int? limit = null, List<KeyValuePair<string, string>> overwriteArgs = null)
		{
			if (string.IsNullOrWhiteSpace(entityName))
				throw new ArgumentException("Invalid entity name.");

			if (string.IsNullOrWhiteSpace(fields))
				fields = "*";

			EntityName = entityName;
			Fields = fields;
			Query = query;
			Sort = sort;
			Skip = skip;
			Limit = limit;
			OverwriteArgs = overwriteArgs;
		}

		#region <=== Static Factory Methods ===>

		/// <summary>
		/// Creates an equality (EQ) filter condition: field == value.
		/// </summary>
		/// <param name="fieldName">The field name to compare. Must not be null or whitespace.</param>
		/// <param name="value">The value to compare against.</param>
		/// <returns>A <see cref="QueryObject"/> representing the EQ condition.</returns>
		/// <exception cref="ArgumentNullException">Thrown when fieldName is null or whitespace.</exception>
		public static QueryObject QueryEQ(string fieldName, object value)
		{
			if (string.IsNullOrWhiteSpace(fieldName))
				throw new ArgumentNullException("fieldName");

			return new QueryObject { QueryType = QueryType.EQ, FieldName = fieldName, FieldValue = value };
		}

		/// <summary>
		/// Creates a not-equal (NOT) filter condition: field != value.
		/// </summary>
		/// <param name="fieldName">The field name to compare.</param>
		/// <param name="value">The value to compare against.</param>
		/// <returns>A <see cref="QueryObject"/> representing the NOT condition.</returns>
		public static QueryObject QueryNOT(string fieldName, object value)
		{
			return new QueryObject { QueryType = QueryType.NOT, FieldName = fieldName, FieldValue = value };
		}

		/// <summary>
		/// Creates a less-than (LT) filter condition: field &lt; value.
		/// </summary>
		/// <param name="fieldName">The field name to compare.</param>
		/// <param name="value">The value to compare against.</param>
		/// <returns>A <see cref="QueryObject"/> representing the LT condition.</returns>
		public static QueryObject QueryLT(string fieldName, object value)
		{
			return new QueryObject { QueryType = QueryType.LT, FieldName = fieldName, FieldValue = value };
		}

		/// <summary>
		/// Creates a less-than-or-equal (LTE) filter condition: field &lt;= value.
		/// </summary>
		/// <param name="fieldName">The field name to compare.</param>
		/// <param name="value">The value to compare against.</param>
		/// <returns>A <see cref="QueryObject"/> representing the LTE condition.</returns>
		public static QueryObject QueryLTE(string fieldName, object value)
		{
			return new QueryObject { QueryType = QueryType.LTE, FieldName = fieldName, FieldValue = value };
		}

		/// <summary>
		/// Creates a greater-than (GT) filter condition: field &gt; value.
		/// </summary>
		/// <param name="fieldName">The field name to compare.</param>
		/// <param name="value">The value to compare against.</param>
		/// <returns>A <see cref="QueryObject"/> representing the GT condition.</returns>
		public static QueryObject QueryGT(string fieldName, object value)
		{
			return new QueryObject { QueryType = QueryType.GT, FieldName = fieldName, FieldValue = value };
		}

		/// <summary>
		/// Creates a greater-than-or-equal (GTE) filter condition: field &gt;= value.
		/// </summary>
		/// <param name="fieldName">The field name to compare.</param>
		/// <param name="value">The value to compare against.</param>
		/// <returns>A <see cref="QueryObject"/> representing the GTE condition.</returns>
		public static QueryObject QueryGTE(string fieldName, object value)
		{
			return new QueryObject { QueryType = QueryType.GTE, FieldName = fieldName, FieldValue = value };
		}

		/// <summary>
		/// Creates a logical AND composite filter that requires all sub-queries to match.
		/// </summary>
		/// <param name="queries">The child filter conditions. No element may be null.</param>
		/// <returns>A <see cref="QueryObject"/> representing the AND composition.</returns>
		/// <exception cref="ArgumentException">Thrown when any query element is null.</exception>
		public static QueryObject QueryAND(params QueryObject[] queries)
		{
			foreach (var query in queries)
			{
				if (query == null)
					throw new ArgumentException("Queries contains null values.");
			}

			return new QueryObject { QueryType = QueryType.AND, SubQueries = new List<QueryObject>(queries) };
		}

		/// <summary>
		/// Creates a logical OR composite filter that requires any sub-query to match.
		/// </summary>
		/// <param name="queries">The child filter conditions. No element may be null.</param>
		/// <returns>A <see cref="QueryObject"/> representing the OR composition.</returns>
		/// <exception cref="ArgumentException">Thrown when any query element is null.</exception>
		public static QueryObject QueryOR(params QueryObject[] queries)
		{
			foreach (var query in queries)
			{
				if (query == null)
					throw new ArgumentException("Queries contains null values.");
			}

			return new QueryObject { QueryType = QueryType.OR, SubQueries = new List<QueryObject>(queries) };
		}

		/// <summary>
		/// Creates a CONTAINS filter condition: field contains value (substring match).
		/// </summary>
		/// <param name="fieldName">The field name to search within.</param>
		/// <param name="value">The substring value to search for.</param>
		/// <returns>A <see cref="QueryObject"/> representing the CONTAINS condition.</returns>
		public static QueryObject QueryContains(string fieldName, object value)
		{
			return new QueryObject { QueryType = QueryType.CONTAINS, FieldName = fieldName, FieldValue = value };
		}

		/// <summary>
		/// Creates a CONTAINS filter condition. Alias for <see cref="QueryContains"/> to match
		/// the standardized naming convention used across microservices.
		/// </summary>
		/// <param name="fieldName">The field name to search within.</param>
		/// <param name="value">The substring value to search for.</param>
		/// <returns>A <see cref="QueryObject"/> representing the CONTAINS condition.</returns>
		public static QueryObject QueryCONTAINS(string fieldName, object value)
		{
			return QueryContains(fieldName, value);
		}

		/// <summary>
		/// Creates a STARTSWITH filter condition: field starts with value.
		/// </summary>
		/// <param name="fieldName">The field name to search within.</param>
		/// <param name="value">The prefix value to match against.</param>
		/// <returns>A <see cref="QueryObject"/> representing the STARTSWITH condition.</returns>
		public static QueryObject QueryStartsWith(string fieldName, object value)
		{
			return new QueryObject { QueryType = QueryType.STARTSWITH, FieldName = fieldName, FieldValue = value };
		}

		/// <summary>
		/// Creates a STARTSWITH filter condition. Alias for <see cref="QueryStartsWith"/> to match
		/// the standardized naming convention used across microservices.
		/// </summary>
		/// <param name="fieldName">The field name to search within.</param>
		/// <param name="value">The prefix value to match against.</param>
		/// <returns>A <see cref="QueryObject"/> representing the STARTSWITH condition.</returns>
		public static QueryObject QuerySTARTSWITH(string fieldName, object value)
		{
			return QueryStartsWith(fieldName, value);
		}

		/// <summary>
		/// Creates a REGEX filter condition with configurable case sensitivity and match semantics.
		/// </summary>
		/// <param name="fieldName">The field name to match against.</param>
		/// <param name="value">The regex pattern to apply.</param>
		/// <param name="op">The regex operator controlling case sensitivity. Defaults to MatchCaseSensitive.</param>
		/// <returns>A <see cref="QueryObject"/> representing the REGEX condition.</returns>
		public static QueryObject QueryRegex(string fieldName, object value, QueryObjectRegexOperator op = QueryObjectRegexOperator.MatchCaseSensitive)
		{
			return new QueryObject { QueryType = QueryType.REGEX, FieldName = fieldName, FieldValue = value, RegexOperator = op };
		}

		/// <summary>
		/// Creates a REGEX filter condition. Alias for <see cref="QueryRegex"/> to match
		/// the standardized naming convention used across microservices.
		/// Defaults to <see cref="QueryObjectRegexOperator.MatchCaseSensitive"/>.
		/// </summary>
		/// <param name="fieldName">The field name to match against.</param>
		/// <param name="value">The regex pattern to apply.</param>
		/// <param name="op">The regex operator controlling case sensitivity. Defaults to MatchCaseSensitive.</param>
		/// <returns>A <see cref="QueryObject"/> representing the REGEX condition.</returns>
		public static QueryObject QueryREGEX(string fieldName, object value, QueryObjectRegexOperator op = QueryObjectRegexOperator.MatchCaseSensitive)
		{
			return QueryRegex(fieldName, value, op);
		}

		/// <summary>
		/// Creates a full-text search (FTS) filter condition using PostgreSQL FTS capabilities.
		/// </summary>
		/// <param name="fieldName">The field name to search.</param>
		/// <param name="value">The search term or phrase.</param>
		/// <param name="language">Optional FTS language configuration (e.g., "english", "bulgarian"). Defaults to null.</param>
		/// <returns>A <see cref="QueryObject"/> representing the FTS condition.</returns>
		public static QueryObject QueryFTS(string fieldName, object value, string language = null)
		{
			return new QueryObject { QueryType = QueryType.FTS, FieldName = fieldName, FieldValue = value, FtsLanguage = language };
		}

		/// <summary>
		/// Creates a RELATED filter condition for querying records that have a relation
		/// to another entity through the specified relation name and direction.
		/// </summary>
		/// <param name="relationName">The name of the entity relation.</param>
		/// <param name="direction">The direction of traversal, e.g., "origin-target". Defaults to "origin-target".</param>
		/// <returns>A <see cref="QueryObject"/> representing the RELATED condition.</returns>
		public static QueryObject Related(string relationName, string direction = "origin-target")
		{
			return new QueryObject { QueryType = QueryType.RELATED, FieldName = relationName, FieldValue = direction };
		}

		/// <summary>
		/// Creates a RELATED filter condition. Alias for <see cref="Related"/> to match
		/// the standardized naming convention used across microservices.
		/// </summary>
		/// <param name="relationName">The name of the entity relation.</param>
		/// <param name="direction">The direction of traversal. Defaults to "origin-target".</param>
		/// <returns>A <see cref="QueryObject"/> representing the RELATED condition.</returns>
		public static QueryObject QueryRELATED(string relationName, string direction = "origin-target")
		{
			return Related(relationName, direction);
		}

		/// <summary>
		/// Creates a NOTRELATED filter condition for querying records that do NOT have a
		/// relation to another entity through the specified relation name and direction.
		/// </summary>
		/// <param name="relationName">The name of the entity relation.</param>
		/// <param name="direction">The direction of traversal, e.g., "origin-target". Defaults to "origin-target".</param>
		/// <returns>A <see cref="QueryObject"/> representing the NOTRELATED condition.</returns>
		public static QueryObject NotRelated(string relationName, string direction = "origin-target")
		{
			return new QueryObject { QueryType = QueryType.NOTRELATED, FieldName = relationName, FieldValue = direction };
		}

		/// <summary>
		/// Creates a NOTRELATED filter condition. Alias for <see cref="NotRelated"/> to match
		/// the standardized naming convention used across microservices.
		/// </summary>
		/// <param name="relationName">The name of the entity relation.</param>
		/// <param name="direction">The direction of traversal. Defaults to "origin-target".</param>
		/// <returns>A <see cref="QueryObject"/> representing the NOTRELATED condition.</returns>
		public static QueryObject QueryNOTRELATED(string relationName, string direction = "origin-target")
		{
			return NotRelated(relationName, direction);
		}

		#endregion
	}

	#endregion

	#region <=== Query Results ===>

	/// <summary>
	/// Contains the result data from an EQL query execution, including field metadata
	/// describing the projected columns and the actual data rows.
	/// Preserved exactly from WebVella.Erp.Api.Models.QueryResult.
	/// </summary>
	public class QueryResult
	{
		[JsonProperty(PropertyName = "fieldsMeta")]
		public List<Field> FieldsMeta { get; set; }

		[JsonProperty(PropertyName = "data")]
		public List<EntityRecord> Data { get; set; }
	}

	/// <summary>
	/// Standard API response envelope wrapping a <see cref="QueryResult"/>.
	/// Inherits from <see cref="BaseResponseModel"/> to provide success/error/timestamp
	/// properties consistent with the REST API v3 contract.
	/// Preserved exactly from WebVella.Erp.Api.Models.QueryResponse.
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

	/// <summary>
	/// Standard API response envelope wrapping a record count (long).
	/// Used for count-only queries where the full result set is not needed.
	/// Inherits from <see cref="BaseResponseModel"/> to provide success/error/timestamp
	/// properties consistent with the REST API v3 contract.
	/// Preserved exactly from WebVella.Erp.Api.Models.QueryCountResponse.
	/// </summary>
	public class QueryCountResponse : BaseResponseModel
	{
		[JsonProperty(PropertyName = "object")]
		public long Object { get; set; }
	}

	#endregion
}
