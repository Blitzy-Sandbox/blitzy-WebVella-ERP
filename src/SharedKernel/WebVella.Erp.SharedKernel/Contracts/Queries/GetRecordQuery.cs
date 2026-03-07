using Newtonsoft.Json;
using System;

namespace WebVella.Erp.SharedKernel.Contracts.Queries
{
    /// <summary>
    /// Shared query contract for retrieving a single record by its unique identifier
    /// within a specified entity. Used in the CQRS-light pattern for cross-service
    /// record retrieval requests across the WebVella ERP microservice architecture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This contract is derived from the monolith's <c>RecordManager.Find()</c> pattern,
    /// where records are fetched by entity name and a <c>QueryEQ("id", recordId)</c> filter,
    /// combined with the <c>EntityQuery</c> pattern that carries <c>EntityName</c> and
    /// <c>Fields</c> properties for query scoping.
    /// </para>
    /// <para>
    /// In the microservice architecture, this query object is serialized via Newtonsoft.Json
    /// and transported over REST API, gRPC JSON transcoding, or message broker (MassTransit
    /// with RabbitMQ/SNS+SQS). All properties are annotated with <see cref="JsonPropertyAttribute"/>
    /// using camelCase property names to ensure API contract stability per AAP §0.8.2.
    /// </para>
    /// <para>
    /// This is a pure data contract — no business logic, no validation, no service dependencies.
    /// The owning service's query handler is responsible for interpreting this contract and
    /// executing the corresponding EQL or SQL lookup against its database.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var query = new GetRecordQuery
    /// {
    ///     EntityName = "account",
    ///     RecordId = Guid.Parse("a1b2c3d4-..."),
    ///     Fields = "id,name,email"
    /// };
    /// // query.CorrelationId is auto-generated for distributed tracing
    /// </code>
    /// </example>
    public class GetRecordQuery : IQuery
    {
        /// <summary>
        /// Unique correlation identifier for distributed tracing across microservice boundaries.
        /// Auto-generated via <see cref="Guid.NewGuid()"/> in the default constructor.
        /// Propagated through all downstream service calls and events triggered by this query.
        /// </summary>
        /// <remarks>
        /// Implements <see cref="IQuery.CorrelationId"/>. This property mirrors the
        /// <c>IDomainEvent.CorrelationId</c> pattern, enabling end-to-end tracing from
        /// the API Gateway through Core, CRM, Project, and other services.
        /// </remarks>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// The name of the entity to query. Maps to the entity name used in
        /// <c>RecordManager</c> lookup operations and the <c>entityName</c> parameter
        /// in the monolith's query pipeline.
        /// </summary>
        /// <remarks>
        /// Matches the <c>EntityQuery.EntityName</c> pattern from the monolith source.
        /// Examples include "user", "account", "contact", "task", "email", etc.
        /// The owning service resolves this to the corresponding <c>rec_{entityName}</c>
        /// table in its database.
        /// </remarks>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// The unique identifier of the record to retrieve. Derived from the monolith's
        /// <c>RecordManager.Find()</c> pattern where records are fetched by the <c>id</c>
        /// field using <c>QueryEQ("id", recordId)</c>.
        /// </summary>
        /// <remarks>
        /// All WebVella ERP entities use a <see cref="Guid"/> as their primary key,
        /// stored in the <c>id</c> column of the corresponding <c>rec_*</c> table.
        /// </remarks>
        [JsonProperty(PropertyName = "recordId")]
        public Guid RecordId { get; set; }

        /// <summary>
        /// Comma-separated list of field names to include in the result, or <c>"*"</c>
        /// to retrieve all fields. Defaults to <c>"*"</c> in the parameterless constructor.
        /// </summary>
        /// <remarks>
        /// Matches the <c>EntityQuery.Fields</c> pattern from the monolith source, where
        /// the constructor defaults to <c>"*"</c> when no fields are specified
        /// (source line 26: <c>if (string.IsNullOrWhiteSpace(fields)) fields = "*";</c>).
        /// Accepts field names like <c>"id,name,email"</c> or the wildcard <c>"*"</c>.
        /// </remarks>
        [JsonProperty(PropertyName = "fields")]
        public string Fields { get; set; }

        /// <summary>
        /// Initializes a new instance of <see cref="GetRecordQuery"/> with a
        /// freshly generated <see cref="CorrelationId"/> and <see cref="Fields"/>
        /// defaulting to <c>"*"</c> (all fields).
        /// </summary>
        public GetRecordQuery()
        {
            CorrelationId = Guid.NewGuid();
            Fields = "*";
        }
    }
}
