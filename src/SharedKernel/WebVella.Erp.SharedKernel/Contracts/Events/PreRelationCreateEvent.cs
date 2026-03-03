using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event published before a many-to-many relation record is created between
    /// two entity records. This event replaces the monolith's synchronous
    /// <c>IErpPreCreateManyToManyRelationHook</c> interface with an asynchronous,
    /// event-driven contract for cross-service communication.
    /// <para>
    /// Subscribers can inspect the relation details (relation name, origin record ID,
    /// target record ID) and populate the <see cref="ValidationErrors"/> list to block
    /// the create operation — matching the original hook's
    /// <c>OnPreCreate(string relationName, Guid originId, Guid targetId, List&lt;ErrorModel&gt; errors)</c>
    /// signature and the <c>RecordHookManager.ExecutePreCreateManyToManyRelationHook</c>
    /// execution pattern.
    /// </para>
    /// <para>
    /// This is a pure data contract with no business logic. It is serialized via
    /// Newtonsoft.Json and transported via MassTransit (RabbitMQ for local/Docker,
    /// SNS+SQS for AWS/LocalStack validation).
    /// </para>
    /// </summary>
    public class PreRelationCreateEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the event was created.
        /// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless constructor.
        /// Enables event ordering, idempotency checks, and audit trail reconstruction
        /// across distributed service boundaries.
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing this event and
        /// all related events in the same operation chain across services.
        /// Initialized to <see cref="Guid.NewGuid()"/> in the parameterless constructor.
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the entity name this event relates to. For relation-specific
        /// events this property may be empty or null; the <see cref="RelationName"/>
        /// property carries the relation identifier instead. Retained from
        /// <see cref="IDomainEvent"/> contract for consistent event routing.
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the name of the many-to-many relation being created.
        /// Maps directly to the <c>string relationName</c> parameter from the source
        /// <c>IErpPreCreateManyToManyRelationHook.OnPreCreate</c> method and the
        /// <c>RecordHookManager.ExecutePreCreateManyToManyRelationHook</c> validation
        /// that requires this value to be non-null and non-whitespace.
        /// </summary>
        [JsonProperty(PropertyName = "relationName")]
        public string RelationName { get; set; }

        /// <summary>
        /// Gets or sets the origin record identifier in the many-to-many relation.
        /// This is a NON-nullable <see cref="Guid"/>, matching the source hook signature
        /// <c>Guid originId</c> from <c>IErpPreCreateManyToManyRelationHook.OnPreCreate</c>.
        /// </summary>
        [JsonProperty(PropertyName = "originId")]
        public Guid OriginId { get; set; }

        /// <summary>
        /// Gets or sets the target record identifier in the many-to-many relation.
        /// This is a NON-nullable <see cref="Guid"/>, matching the source hook signature
        /// <c>Guid targetId</c> from <c>IErpPreCreateManyToManyRelationHook.OnPreCreate</c>.
        /// </summary>
        [JsonProperty(PropertyName = "targetId")]
        public Guid TargetId { get; set; }

        /// <summary>
        /// Gets or sets the mutable list of validation errors that event subscribers
        /// can populate to block the relation create operation. Maps to the
        /// <c>List&lt;ErrorModel&gt; errors</c> parameter from the source hook's
        /// <c>OnPreCreate</c> method. If any errors are added by subscribers, the
        /// publishing service will abort the relation creation and return these errors
        /// in the API response.
        /// Initialized to an empty list in the parameterless constructor.
        /// </summary>
        [JsonProperty(PropertyName = "validationErrors")]
        public List<ErrorModel> ValidationErrors { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PreRelationCreateEvent"/> class
        /// with default values: <see cref="Timestamp"/> set to <see cref="DateTime.UtcNow"/>,
        /// <see cref="CorrelationId"/> set to a new <see cref="Guid"/>, and
        /// <see cref="ValidationErrors"/> initialized to an empty list.
        /// </summary>
        public PreRelationCreateEvent()
        {
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = Guid.NewGuid();
            ValidationErrors = new List<ErrorModel>();
        }
    }
}
