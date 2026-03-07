using System;

namespace WebVella.Erp.SharedKernel.Contracts.Queries
{
    /// <summary>
    /// Base marker interface for all query contracts in the WebVella ERP microservice architecture.
    /// All query types must implement this interface to ensure consistent distributed tracing
    /// via CorrelationId across service boundaries.
    /// </summary>
    /// <remarks>
    /// This is part of the CQRS-light pattern for the SharedKernel. Query contracts are pure
    /// data objects with no business logic, serializable via Newtonsoft.Json for transport
    /// via REST API, gRPC, or message broker (MassTransit with RabbitMQ/SNS+SQS).
    ///
    /// In the monolith, queries are plain parameter objects (e.g., <c>EntityQuery</c> with
    /// EntityName, Fields, Query, Sort, Skip, Limit) passed directly to manager methods
    /// such as <c>RecordManager.Find()</c>. In the microservice architecture, query contracts
    /// are self-describing objects that carry a <see cref="CorrelationId"/> for distributed
    /// tracing across service boundaries.
    ///
    /// Implementations include:
    /// - <see cref="GetRecordQuery"/> for single record retrieval by ID
    /// - <see cref="SearchRecordsQuery"/> for EQL-based record search with parameters
    /// </remarks>
    public interface IQuery
    {
        /// <summary>
        /// Unique correlation identifier for distributed tracing across microservice boundaries.
        /// Auto-generated via <see cref="Guid.NewGuid()"/> in implementations' constructors.
        /// Propagated through all downstream service calls and events triggered by this query.
        /// </summary>
        /// <remarks>
        /// This property mirrors the <c>IDomainEvent.CorrelationId</c> pattern established in
        /// <see cref="WebVella.Erp.SharedKernel.Contracts.Events.IDomainEvent"/>. Both queries
        /// and events use the same <see cref="Guid"/>-based correlation ID to enable end-to-end
        /// distributed tracing — a query originating at the Gateway can be linked to all
        /// downstream events it triggers across Core, CRM, Project, and other services.
        /// </remarks>
        Guid CorrelationId { get; set; }
    }
}
