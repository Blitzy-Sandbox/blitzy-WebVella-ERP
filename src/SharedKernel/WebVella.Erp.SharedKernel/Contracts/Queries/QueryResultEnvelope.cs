using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Queries
{
    /// <summary>
    /// Generic typed query result envelope that carries pagination info, total count,
    /// and a strongly-typed result set. Unifies the various monolith result patterns
    /// (<c>QueryResponse</c>, <c>QueryCountResponse</c>, <c>EntityRecordList</c>)
    /// into a single generic envelope for microservice query responses.
    /// </summary>
    /// <typeparam name="T">
    /// The type of the result payload. Common usages include:
    /// <list type="bullet">
    ///   <item><description><c>EntityRecord</c> for single record retrieval</description></item>
    ///   <item><description><c>List&lt;EntityRecord&gt;</c> for record search results</description></item>
    ///   <item><description><c>long</c> for count queries</description></item>
    ///   <item><description>Any other DTO returned by query operations</description></item>
    /// </list>
    /// </typeparam>
    /// <remarks>
    /// This class preserves the API contract shape from the monolith's
    /// <c>BaseResponseModel</c> envelope (success, errors, timestamp, message, hash)
    /// to maintain backward compatibility with REST API v3 responses
    /// (<c>/api/v3/{locale}/...</c>). All properties are annotated with
    /// <c>[JsonProperty]</c> for stable Newtonsoft.Json serialization.
    /// <para>
    /// The envelope extends the base response pattern with query-specific fields:
    /// <c>Data</c> (the generic payload), <c>TotalCount</c> (total matching records),
    /// <c>Skip</c> (pagination offset), and <c>Limit</c> (page size).
    /// </para>
    /// <para>
    /// Implements <see cref="IQueryResult"/> to satisfy the common query result
    /// contract required across all microservice query operations.
    /// </para>
    /// </remarks>
    public class QueryResultEnvelope<T> : IQueryResult
    {
        /// <summary>
        /// Indicates whether the query operation completed successfully.
        /// Defaults to <c>true</c>; error paths explicitly set this to <c>false</c>.
        /// </summary>
        /// <value>
        /// <c>true</c> if the query executed without errors; <c>false</c> if one
        /// or more errors occurred during query processing.
        /// </value>
        [JsonProperty(PropertyName = "success")]
        public bool Success { get; set; }

        /// <summary>
        /// Optional human-readable message describing the query outcome.
        /// Matches the <c>BaseResponseModel.Message</c> field from the monolith API.
        /// </summary>
        /// <value>
        /// A descriptive string providing additional context about the query result,
        /// such as a summary of the operation performed or a reason for failure.
        /// May be <c>null</c> when no additional context is needed.
        /// </value>
        [JsonProperty(PropertyName = "message")]
        public string Message { get; set; }

        /// <summary>
        /// Collection of error details when the query fails or encounters validation issues.
        /// Matches the <c>BaseResponseModel.Errors</c> field from the monolith API.
        /// </summary>
        /// <value>
        /// A list of <see cref="ErrorModel"/> instances, each containing a
        /// <see cref="ErrorModel.Key"/> identifying the error source, a
        /// <see cref="ErrorModel.Value"/> with the offending data, and a
        /// <see cref="ErrorModel.Message"/> describing the error.
        /// An empty list indicates no errors occurred.
        /// </value>
        [JsonProperty(PropertyName = "errors")]
        public List<ErrorModel> Errors { get; set; }

        /// <summary>
        /// UTC timestamp recording when the query response was created.
        /// Matches the <c>BaseResponseModel.Timestamp</c> field from the monolith API.
        /// </summary>
        /// <value>
        /// The <see cref="DateTime"/> value in UTC representing the moment this
        /// response envelope was instantiated.
        /// </value>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Entity metadata cache fingerprint for detecting stale cached data.
        /// Matches the <c>BaseResponseModel.Hash</c> field from the monolith API.
        /// </summary>
        /// <value>
        /// A hash string that clients can use to determine whether their cached
        /// entity metadata is still current, or <c>null</c> if not applicable.
        /// </value>
        [JsonProperty(PropertyName = "hash")]
        public string Hash { get; set; }

        /// <summary>
        /// The generic result payload carrying the query's data.
        /// </summary>
        /// <value>
        /// The strongly-typed result data. Maps to:
        /// <list type="bullet">
        ///   <item><description><c>QueryResponse.Object</c> when <typeparamref name="T"/> is a query result type</description></item>
        ///   <item><description><c>QueryCountResponse.Object</c> when <typeparamref name="T"/> is <c>long</c></description></item>
        ///   <item><description>Direct <c>EntityRecord</c> or <c>List&lt;EntityRecord&gt;</c> for typed results</description></item>
        /// </list>
        /// </value>
        [JsonProperty(PropertyName = "data")]
        public T Data { get; set; }

        /// <summary>
        /// Total number of records matching the query criteria, regardless of pagination.
        /// Derived from the <c>EntityRecordList.TotalCount</c> pattern in the monolith.
        /// </summary>
        /// <value>
        /// The total count of records available. Defaults to <c>0</c>.
        /// </value>
        [JsonProperty(PropertyName = "totalCount")]
        public int TotalCount { get; set; }

        /// <summary>
        /// Number of records skipped for pagination (offset).
        /// Derived from <c>EntityQuery.Skip</c> and <c>SearchQuery.Skip</c> patterns.
        /// </summary>
        /// <value>
        /// The number of records to skip, or <c>null</c> if pagination offset was not specified.
        /// </value>
        [JsonProperty(PropertyName = "skip")]
        public int? Skip { get; set; }

        /// <summary>
        /// Maximum number of records returned per page.
        /// Derived from <c>EntityQuery.Limit</c> and <c>SearchQuery.Limit</c> patterns.
        /// </summary>
        /// <value>
        /// The maximum records in this result page, or <c>null</c> if no limit was specified.
        /// </value>
        [JsonProperty(PropertyName = "limit")]
        public int? Limit { get; set; }

        /// <summary>
        /// Initializes a new instance of <see cref="QueryResultEnvelope{T}"/> with
        /// sensible defaults matching the monolith's <c>BaseResponseModel</c> constructor pattern.
        /// </summary>
        /// <remarks>
        /// Default values:
        /// <list type="bullet">
        ///   <item><description><see cref="Success"/> = <c>true</c></description></item>
        ///   <item><description><see cref="Errors"/> = empty list</description></item>
        ///   <item><description><see cref="Timestamp"/> = <see cref="DateTime.UtcNow"/></description></item>
        ///   <item><description><see cref="TotalCount"/> = <c>0</c></description></item>
        /// </list>
        /// </remarks>
        public QueryResultEnvelope()
        {
            Success = true;
            Errors = new List<ErrorModel>();
            Timestamp = DateTime.UtcNow;
            TotalCount = 0;
        }
    }
}
