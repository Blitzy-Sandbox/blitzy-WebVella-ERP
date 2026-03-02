using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Specifies the type of search operation to perform.
	/// Contains = ILIKE pattern matching; Fts = PostgreSQL full-text search.
	/// CRITICAL: Integer values are used in SQL queries and API contracts — do not change.
	/// </summary>
	public enum SearchType
	{
		Contains = 0,
		Fts = 1
	}

	/// <summary>
	/// Specifies the shape of the search result returned.
	/// Compact = minimal fields; Full = all fields including content and aux_data.
	/// CRITICAL: Integer values are part of the API contract — do not change.
	/// </summary>
	public enum SearchResultType
	{
		Compact = 0,
		Full = 1
	}

	/// <summary>
	/// Search request DTO used by SearchManager to query across entities, apps, and records.
	/// All [JsonProperty] annotations preserve REST API v3 contract stability (AAP 0.8.2).
	/// </summary>
	public class SearchQuery
	{
		[JsonProperty(PropertyName = "search_type")]
		public SearchType SearchType { get; set; } = SearchType.Contains;

		[JsonProperty(PropertyName = "result_type")]
		public SearchResultType ResultType { get; set; } = SearchResultType.Compact;

		[JsonProperty(PropertyName = "text")]
		public string Text { get; set; } = string.Empty;

		[JsonProperty(PropertyName = "entities")]
		public List<Guid> Entities { get; set; } = new List<Guid>();

		[JsonProperty(PropertyName = "apps")]
		public List<Guid> Apps { get; set; } = new List<Guid>();

		[JsonProperty(PropertyName = "records")]
		public List<Guid> Records { get; set; } = new List<Guid>();

		[JsonProperty(PropertyName = "skip")]
		public int? Skip { get; set; } = 0;

		[JsonProperty(PropertyName = "limit")]
		public int? Limit { get; set; } = 20;

	}

	/// <summary>
	/// Search result DTO representing a single matched record with its metadata.
	/// All [JsonProperty] annotations preserve REST API v3 contract stability (AAP 0.8.2).
	/// </summary>
	public class SearchResult
	{
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		[JsonProperty(PropertyName = "entities")]
		public List<Guid> Entities { get; set; } = new List<Guid>();

		[JsonProperty(PropertyName = "apps")]
		public List<Guid> Apps { get; set; } = new List<Guid>();

		[JsonProperty(PropertyName = "records")]
		public List<Guid> Records { get; set; } = new List<Guid>();

		[JsonProperty(PropertyName = "content")]
		public string Content { get; set; } = string.Empty;

		[JsonProperty(PropertyName = "stem_content")]
		public string StemContent { get; set; } = string.Empty;

		[JsonProperty(PropertyName = "snippet")]
		public string Snippet { get; set; } = string.Empty;

		[JsonProperty(PropertyName = "url")]
		public string Url { get; set; } = string.Empty;

		[JsonProperty(PropertyName = "aux_data")]
		public string AuxData { get; set; } = string.Empty;

		[JsonProperty(PropertyName = "timestamp")]
		public DateTime Timestamp { get; set; }
	}

	/// <summary>
	/// Typed list of SearchResult with a TotalCount property for pagination support.
	/// Inherits from List&lt;SearchResult&gt; to maintain backward compatibility with
	/// existing code that iterates over search results as a list.
	/// </summary>
	public class SearchResultList : List<SearchResult>
	{
		public int TotalCount { get; set; } = 0;
	}
}
