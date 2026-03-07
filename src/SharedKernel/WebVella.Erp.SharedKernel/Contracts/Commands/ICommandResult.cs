using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Commands
{
    /// <summary>
    /// Base interface for all command execution results in the WebVella ERP microservice architecture.
    /// Defines the common result envelope properties (success, message, errors) that all
    /// command result types must implement, following the BaseResponseModel pattern from
    /// the monolith's API contracts.
    /// </summary>
    /// <remarks>
    /// This mirrors the <see cref="WebVella.Erp.SharedKernel.Contracts.Queries.IQueryResult"/> 
    /// interface for the command side of the CQRS-light pattern. Command results carry
    /// the same success/error envelope to maintain API contract backward compatibility.
    /// <para>
    /// The <see cref="ErrorModel"/> type referenced by the <see cref="Errors"/>
    /// property provides a standard error detail contract with Key, Value, and
    /// Message properties for structured error reporting across all microservices.
    /// </para>
    /// </remarks>
    public interface ICommandResult
    {
        /// <summary>
        /// Indicates whether the command operation completed successfully.
        /// </summary>
        /// <value>
        /// <c>true</c> if the command executed without errors; <c>false</c> if one
        /// or more errors occurred during command processing or pre-operation validation.
        /// </value>
        bool Success { get; set; }

        /// <summary>
        /// Optional human-readable message describing the command outcome.
        /// </summary>
        /// <value>
        /// A descriptive string providing additional context about the command result,
        /// such as a summary of the operation performed or a reason for failure.
        /// May be <c>null</c> when no additional context is needed.
        /// </value>
        string Message { get; set; }

        /// <summary>
        /// Collection of error details when the command fails or encounters validation issues.
        /// </summary>
        /// <value>
        /// A list of <see cref="ErrorModel"/> instances, each containing a Key identifying
        /// the error source, a Value with the offending data, and a Message describing the
        /// error. An empty list indicates no errors occurred. Pre-operation validation errors
        /// (from what were previously pre-hooks) and processing errors are collected here.
        /// </value>
        List<ErrorModel> Errors { get; set; }
    }
}
