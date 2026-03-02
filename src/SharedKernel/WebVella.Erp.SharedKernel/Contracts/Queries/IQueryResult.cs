using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Queries
{
    /// <summary>
    /// Base interface for all query results in the WebVella ERP microservice architecture.
    /// Defines the common result envelope properties (success, message, errors) that all
    /// query result types must implement, following the BaseResponseModel pattern from
    /// the monolith's API contracts.
    /// </summary>
    /// <remarks>
    /// This interface extracts the core query result contract from the original
    /// <c>BaseResponseModel</c> (Success, Message, Errors) for CQRS-light usage
    /// across all microservices. Implementations should apply <c>[JsonProperty]</c>
    /// annotations to maintain API contract compatibility with the existing REST
    /// API v3 response envelope format.
    /// <para>
    /// The <see cref="ErrorModel"/> type referenced by the <see cref="Errors"/>
    /// property provides a standard error detail contract with Key, Value, and
    /// Message properties for structured error reporting.
    /// </para>
    /// </remarks>
    public interface IQueryResult
    {
        /// <summary>
        /// Indicates whether the query operation completed successfully.
        /// </summary>
        /// <value>
        /// <c>true</c> if the query executed without errors; <c>false</c> if one
        /// or more errors occurred during query processing.
        /// </value>
        bool Success { get; set; }

        /// <summary>
        /// Optional human-readable message describing the query outcome.
        /// </summary>
        /// <value>
        /// A descriptive string providing additional context about the query result,
        /// such as a summary of the operation performed or a reason for failure.
        /// May be <c>null</c> when no additional context is needed.
        /// </value>
        string Message { get; set; }

        /// <summary>
        /// Collection of error details when the query fails or encounters validation issues.
        /// </summary>
        /// <value>
        /// A list of <see cref="ErrorModel"/> instances, each containing a Key identifying
        /// the error source, a Value with the offending data, and a Message describing the
        /// error. An empty list indicates no errors occurred.
        /// </value>
        List<ErrorModel> Errors { get; set; }
    }
}
