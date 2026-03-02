using System;
using Newtonsoft.Json;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event published after a many-to-many relation record is successfully created.
    /// <para>
    /// Replaces the monolith's synchronous <c>IErpPostCreateManyToManyRelationHook</c> interface
    /// whose <c>OnPostCreate(string relationName, Guid originId, Guid targetId)</c> method was
    /// invoked in-process by <c>RecordHookManager.ExecutePostCreateManyToManyRelationHook</c>.
    /// </para>
    /// <para>
    /// In the microservice architecture, this event is published asynchronously via MassTransit
    /// (RabbitMQ for local/Docker, SNS+SQS for AWS/LocalStack validation) after the owning
    /// service has persisted the relation link. Subscribers in other services react to the event
    /// to maintain denormalized projections or trigger downstream workflows.
    /// </para>
    /// <para>
    /// This is a post-operation event carrying immutable result data. It does not include a
    /// <c>ValidationErrors</c> property because validation occurs before the relation is created
    /// (handled by <c>PreRelationCreateEvent</c>).
    /// </para>
    /// </summary>
    public class RelationCreatedEvent : IDomainEvent
    {
        /// <inheritdoc />
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the relation was created.
        /// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless constructor.
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTime Timestamp { get; set; }

        /// <inheritdoc />
        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing this event across services.
        /// Initialized to <see cref="Guid.NewGuid()"/> in the parameterless constructor.
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <inheritdoc />
        /// <summary>
        /// Gets or sets the entity name this event relates to.
        /// For relation events, this may be empty or null; the <see cref="RelationName"/>
        /// property carries the relation identifier instead.
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the name of the many-to-many relation that was created.
        /// <para>
        /// Maps directly to the <c>relationName</c> parameter from the source
        /// <c>IErpPostCreateManyToManyRelationHook.OnPostCreate</c> method signature.
        /// Used by event subscribers for filtering and routing relation-specific events.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "relationName")]
        public string RelationName { get; set; }

        /// <summary>
        /// Gets or sets the origin record identifier of the created relation link.
        /// <para>
        /// Non-nullable <see cref="Guid"/>, matching the source hook signature
        /// <c>Guid originId</c> from <c>IErpPostCreateManyToManyRelationHook.OnPostCreate</c>.
        /// Identifies the record on the origin side of the many-to-many relation.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "originId")]
        public Guid OriginId { get; set; }

        /// <summary>
        /// Gets or sets the target record identifier of the created relation link.
        /// <para>
        /// Non-nullable <see cref="Guid"/>, matching the source hook signature
        /// <c>Guid targetId</c> from <c>IErpPostCreateManyToManyRelationHook.OnPostCreate</c>.
        /// Identifies the record on the target side of the many-to-many relation.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "targetId")]
        public Guid TargetId { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="RelationCreatedEvent"/> class
        /// with default values for <see cref="Timestamp"/> and <see cref="CorrelationId"/>.
        /// </summary>
        public RelationCreatedEvent()
        {
            Timestamp = DateTime.UtcNow;
            CorrelationId = Guid.NewGuid();
        }
    }
}
