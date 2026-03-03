using System;
using Newtonsoft.Json;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event published after a many-to-many relation record is deleted.
    /// <para>
    /// Replaces the monolith's synchronous <c>IErpPostDeleteManyToManyRelationHook</c>
    /// interface with an asynchronous, event-driven contract. In the monolith,
    /// <c>RecordHookManager.ExecutePostDeleteManyToManyRelationHook(string relationName,
    /// Guid? originId, Guid? targetId)</c> invoked all registered hook instances
    /// in-process after the relation record was removed. In the microservice architecture,
    /// this event is published onto the message bus (MassTransit over RabbitMQ or SNS+SQS)
    /// and consumed asynchronously by any interested service subscriber.
    /// </para>
    /// <para>
    /// This is a post-operation event carrying immutable result data.
    /// It does NOT include a <c>ValidationErrors</c> list because the deletion has
    /// already been committed — subscribers cannot reject the operation.
    /// </para>
    /// <para>
    /// CRITICAL: <see cref="OriginId"/> and <see cref="TargetId"/> are nullable
    /// (<c>Guid?</c>), preserving the original <c>IErpPostDeleteManyToManyRelationHook</c>
    /// signature where bulk deletes may specify <c>null</c> to indicate all records
    /// on one side of the relation were removed. This differs from
    /// <c>RelationCreatedEvent</c> which uses non-nullable <c>Guid</c>.
    /// </para>
    /// </summary>
    public class RelationDeletedEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the relation deletion occurred.
        /// <para>
        /// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless constructor.
        /// Used for event ordering, idempotency checks, and audit trail reconstruction
        /// across distributed services.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing event chains across services.
        /// <para>
        /// Initialized to <see cref="Guid.NewGuid()"/> in the parameterless constructor.
        /// Links related events in a chain (e.g., a relation delete triggering downstream
        /// cache invalidation or search re-indexing across services).
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the entity name this event relates to.
        /// <para>
        /// For relation-specific events, this property may be empty or null while
        /// <see cref="RelationName"/> carries the relation identifier used for
        /// subscriber routing and filtering.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the name of the many-to-many relation that was deleted.
        /// <para>
        /// Matches the <c>relationName</c> parameter from the original
        /// <c>IErpPostDeleteManyToManyRelationHook.OnPostDelete</c> method.
        /// Subscribers use this property to filter events for relations they manage.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "relationName")]
        public string RelationName { get; set; }

        /// <summary>
        /// Gets or sets the origin record identifier of the deleted relation, or <c>null</c>
        /// if all origin-side records were removed in a bulk delete.
        /// <para>
        /// Matches the nullable <c>Guid? originId</c> parameter from the original
        /// <c>IErpPostDeleteManyToManyRelationHook.OnPostDelete</c> method.
        /// A <c>null</c> value indicates that the delete operation targeted all records
        /// on the origin side of the relation.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "originId")]
        public Guid? OriginId { get; set; }

        /// <summary>
        /// Gets or sets the target record identifier of the deleted relation, or <c>null</c>
        /// if all target-side records were removed in a bulk delete.
        /// <para>
        /// Matches the nullable <c>Guid? targetId</c> parameter from the original
        /// <c>IErpPostDeleteManyToManyRelationHook.OnPostDelete</c> method.
        /// A <c>null</c> value indicates that the delete operation targeted all records
        /// on the target side of the relation.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "targetId")]
        public Guid? TargetId { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="RelationDeletedEvent"/> class
        /// with default values for <see cref="Timestamp"/> and <see cref="CorrelationId"/>.
        /// <para>
        /// Required for MassTransit deserialization and Newtonsoft.Json serialization.
        /// </para>
        /// </summary>
        public RelationDeletedEvent()
        {
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = Guid.NewGuid();
        }
    }
}
