using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Specifies the type of search operation to perform.
    /// Contains performs a simple substring match, while Fts uses full-text search capabilities.
    /// </summary>
    public enum SearchType
    {
        /// <summary>
        /// Performs a case-insensitive substring match against indexed content.
        /// </summary>
        Contains = 0,

        /// <summary>
        /// Performs a full-text search using language-aware stemming and ranking.
        /// </summary>
        Fts = 1
    }

    /// <summary>
    /// Specifies the level of detail returned in search results.
    /// Compact returns minimal metadata, while Full includes all available fields.
    /// </summary>
    public enum SearchResultType
    {
        /// <summary>
        /// Returns minimal search result metadata (id, snippet, url).
        /// </summary>
        Compact = 0,

        /// <summary>
        /// Returns full search result data including content, stem content, and auxiliary data.
        /// </summary>
        Full = 1
    }

    /// <summary>
    /// Represents a search query with filtering, pagination, and result type configuration.
    /// Used by the Entity Management service's search handler to execute searches against
    /// the search index. All JSON property names use snake_case for backward API compatibility
    /// with the original WebVella ERP monolith API contracts.
    /// </summary>
    public class SearchQuery
    {
        /// <summary>
        /// The type of search operation to perform (Contains or Fts).
        /// Defaults to <see cref="Models.SearchType.Contains"/>.
        /// </summary>
        [JsonPropertyName("search_type")]
        public SearchType SearchType { get; set; } = SearchType.Contains;

        /// <summary>
        /// The level of detail to include in search results (Compact or Full).
        /// Defaults to <see cref="Models.SearchResultType.Compact"/>.
        /// </summary>
        [JsonPropertyName("result_type")]
        public SearchResultType ResultType { get; set; } = SearchResultType.Compact;

        /// <summary>
        /// The search text to match against indexed content.
        /// Defaults to an empty string.
        /// </summary>
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Optional list of entity IDs to restrict the search scope.
        /// When empty, all entities are searched.
        /// </summary>
        [JsonPropertyName("entities")]
        public List<Guid> Entities { get; set; } = new();

        /// <summary>
        /// Optional list of application IDs to restrict the search scope.
        /// When empty, all applications are searched.
        /// </summary>
        [JsonPropertyName("apps")]
        public List<Guid> Apps { get; set; } = new();

        /// <summary>
        /// Optional list of specific record IDs to restrict the search scope.
        /// When empty, all records are searched.
        /// </summary>
        [JsonPropertyName("records")]
        public List<Guid> Records { get; set; } = new();

        /// <summary>
        /// Number of results to skip for pagination. Defaults to 0.
        /// </summary>
        [JsonPropertyName("skip")]
        public int? Skip { get; set; } = 0;

        /// <summary>
        /// Maximum number of results to return. Defaults to 20.
        /// </summary>
        [JsonPropertyName("limit")]
        public int? Limit { get; set; } = 20;
    }

    /// <summary>
    /// Represents a single search result item containing matched content and metadata.
    /// All JSON property names use snake_case for backward API compatibility with the
    /// original WebVella ERP monolith API contracts.
    /// </summary>
    public class SearchResult
    {
        /// <summary>
        /// Unique identifier of the search result entry in the search index.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// List of entity IDs associated with this search result.
        /// </summary>
        [JsonPropertyName("entities")]
        public List<Guid> Entities { get; set; } = new();

        /// <summary>
        /// List of application IDs associated with this search result.
        /// </summary>
        [JsonPropertyName("apps")]
        public List<Guid> Apps { get; set; } = new();

        /// <summary>
        /// List of record IDs associated with this search result.
        /// </summary>
        [JsonPropertyName("records")]
        public List<Guid> Records { get; set; } = new();

        /// <summary>
        /// The original indexed content of the search result.
        /// </summary>
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// The stemmed (language-processed) content used for full-text search matching.
        /// </summary>
        [JsonPropertyName("stem_content")]
        public string StemContent { get; set; } = string.Empty;

        /// <summary>
        /// A highlighted excerpt/snippet from the matched content for display purposes.
        /// </summary>
        [JsonPropertyName("snippet")]
        public string Snippet { get; set; } = string.Empty;

        /// <summary>
        /// The URL path associated with this search result for navigation.
        /// </summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Auxiliary data stored alongside the search result for additional context.
        /// </summary>
        [JsonPropertyName("aux_data")]
        public string AuxData { get; set; } = string.Empty;

        /// <summary>
        /// The timestamp indicating when this search result was indexed or last updated.
        /// </summary>
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Represents a paginated list of search results with a total count for pagination metadata.
    /// Extends <see cref="List{SearchResult}"/> to provide both collection semantics and
    /// the total matching result count independent of pagination limits.
    /// </summary>
    public class SearchResultList : List<SearchResult>
    {
        /// <summary>
        /// The total number of matching results across all pages, independent of Skip/Limit pagination.
        /// Defaults to 0.
        /// </summary>
        public int TotalCount { get; set; } = 0;
    }
}
