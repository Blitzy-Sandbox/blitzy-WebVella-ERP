using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.SharedKernel.Contracts.Queries
{
    /// <summary>
    /// CQRS-light query contract for EQL-based record search across microservice boundaries.
    /// Encapsulates all parameters required to execute a parameterized EQL search query
    /// against a specific entity, with optional pagination via Skip and Limit.
    /// </summary>
    /// <remarks>
    /// This contract is derived from patterns observed in the monolith:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <c>EntityQuery</c> — provides the EntityName, Skip (int?), and Limit (int?) property pattern.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <c>EqlCommand</c> — provides the EQL text + <see cref="EqlParameter"/>[] execution model.
    ///       The <see cref="EqlText"/> and <see cref="Parameters"/> properties mirror the
    ///       <c>new EqlCommand(text, params EqlParameter[])</c> constructor signature.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <c>SearchQuery</c> — provides the <c>[JsonProperty]</c> annotation pattern and
    ///       the convention of initializing collections to empty instances to avoid null references.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <c>IErpPreSearchRecordHook</c> — confirms the <c>entityName</c> parameter contract
    ///       used in the monolith's search hook pipeline.
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// This is a pure data contract with NO business logic, NO validation, and NO service
    /// dependencies. It is serializable via Newtonsoft.Json for transport over REST API,
    /// gRPC, or message broker (MassTransit with RabbitMQ / SNS+SQS).
    ///
    /// Usage example:
    /// <code>
    /// var query = new SearchRecordsQuery
    /// {
    ///     EntityName = "account",
    ///     EqlText = "* FROM account WHERE name = @name",
    ///     Parameters = new List&lt;EqlParameter&gt;
    ///     {
    ///         new EqlParameter("name", "Contoso")
    ///     },
    ///     Skip = 0,
    ///     Limit = 20
    /// };
    /// </code>
    /// </remarks>
    public class SearchRecordsQuery : IQuery
    {
        /// <summary>
        /// Unique correlation identifier for distributed tracing across microservice boundaries.
        /// Auto-generated via <see cref="Guid.NewGuid()"/> in the default constructor.
        /// Propagated through all downstream service calls and events triggered by this query.
        /// </summary>
        /// <remarks>
        /// Implements <see cref="IQuery.CorrelationId"/>. This enables end-to-end tracing
        /// from the API Gateway through Core, CRM, Project, and other services when a search
        /// query fans out across service boundaries.
        /// </remarks>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// The name of the entity to search against. Corresponds to the entity's <c>Name</c>
        /// property in the ERP metadata system, e.g., "account", "contact", "task".
        /// </summary>
        /// <remarks>
        /// Matches the <c>EntityQuery.EntityName</c> property from the monolith and the
        /// <c>string entityName</c> parameter of <c>IErpPreSearchRecordHook.OnPreSearchRecord</c>.
        /// Each microservice owns a set of entities — the target service is determined by
        /// resolving the entity name to its owning service via the Gateway's routing table.
        /// </remarks>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// The EQL query text to execute. This is the full EQL SELECT statement string that
        /// the EQL engine parses, builds an AST for, and generates SQL from.
        /// </summary>
        /// <remarks>
        /// Corresponds to the <c>text</c> parameter of <c>new EqlCommand(text, ...)</c> in the
        /// monolith. Example values:
        /// <list type="bullet">
        ///   <item><c>"* FROM account WHERE name = @name"</c></item>
        ///   <item><c>"id, name, $contact.email FROM account"</c></item>
        /// </list>
        /// The EQL engine within each service processes this text using the shared
        /// <c>EqlBuilder</c> from the SharedKernel's Eql namespace.
        /// </remarks>
        [JsonProperty(PropertyName = "eqlText")]
        public string EqlText { get; set; }

        /// <summary>
        /// EQL query parameter bindings for parameterized search execution. Each
        /// <see cref="EqlParameter"/> carries a <see cref="EqlParameter.ParameterName"/>
        /// (prefixed with '@') and a <see cref="EqlParameter.Value"/> for safe, injection-free
        /// query parameterization.
        /// </summary>
        /// <remarks>
        /// Mirrors the <c>params EqlParameter[]</c> parameter of
        /// <c>new EqlCommand(text, params EqlParameter[])</c> in the monolith. Initialized
        /// to an empty list in the default constructor to prevent null reference exceptions,
        /// following the same pattern used in <c>SearchQuery</c> (e.g., <c>new List&lt;Guid&gt;()</c>).
        /// </remarks>
        [JsonProperty(PropertyName = "parameters")]
        public List<EqlParameter> Parameters { get; set; }

        /// <summary>
        /// Number of records to skip for pagination. When null, the EQL engine applies no
        /// OFFSET clause, returning results from the beginning.
        /// </summary>
        /// <remarks>
        /// Matches the nullable <c>int? Skip</c> pattern from <c>EntityQuery.Skip</c>.
        /// Left nullable (unlike <c>SearchQuery.Skip</c> which defaults to 0) because the
        /// EQL engine handles LIMIT/OFFSET natively — callers set these explicitly when
        /// pagination is needed.
        /// </remarks>
        [JsonProperty(PropertyName = "skip")]
        public int? Skip { get; set; }

        /// <summary>
        /// Maximum number of records to return. When null, the EQL engine applies no LIMIT
        /// clause, returning all matching records.
        /// </summary>
        /// <remarks>
        /// Matches the nullable <c>int? Limit</c> pattern from <c>EntityQuery.Limit</c>.
        /// Left nullable (unlike <c>SearchQuery.Limit</c> which defaults to 20) because the
        /// EQL engine handles LIMIT/OFFSET natively — callers set these explicitly when
        /// pagination is needed.
        /// </remarks>
        [JsonProperty(PropertyName = "limit")]
        public int? Limit { get; set; }

        /// <summary>
        /// Initializes a new instance of <see cref="SearchRecordsQuery"/> with a unique
        /// <see cref="CorrelationId"/> and an empty <see cref="Parameters"/> collection.
        /// </summary>
        /// <remarks>
        /// <see cref="CorrelationId"/> is auto-generated via <see cref="Guid.NewGuid()"/>
        /// to enable distributed tracing without requiring callers to manage IDs manually.
        /// <see cref="Parameters"/> is initialized to an empty list to avoid null reference
        /// exceptions when the query has no parameters, matching the monolith's
        /// <c>EqlCommand.Parameters</c> initialization pattern (<c>new List&lt;EqlParameter&gt;()</c>).
        /// <see cref="Skip"/> and <see cref="Limit"/> are intentionally left null — the EQL
        /// engine handles LIMIT/OFFSET natively, and callers set these explicitly when needed.
        /// </remarks>
        public SearchRecordsQuery()
        {
            CorrelationId = Guid.NewGuid();
            Parameters = new List<EqlParameter>();
        }
    }
}
