using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event raised before a many-to-many relation record is deleted.
    /// <para>
    /// This event replaces the monolith's synchronous
    /// <c>IErpPreDeleteManyToManyRelationHook</c> interface, which defined:
    /// <code>
    /// void OnPreDelete(string relationName, Guid? originId, Guid? targetId, List&lt;ErrorModel&gt; errors)
    /// </code>
    /// </para>
    /// <para>
    /// As a <b>pre-operation</b> event, the <see cref="ValidationErrors"/> list is
    /// <b>mutable</b>. Event subscribers may add <see cref="ErrorModel"/> entries to
    /// this list; if any errors are present after all subscribers have processed the
    /// event, the originating service will abort the relation deletion and return the
    /// accumulated validation errors to the caller.
    /// </para>
    /// <para>
    /// The event is serialized with Newtonsoft.Json <c>[JsonProperty]</c> annotations
    /// for stable wire-format compatibility across MassTransit transports
    /// (RabbitMQ for local/Docker, SNS+SQS for AWS/LocalStack validation).
    /// </para>
    /// <para>
    /// <b>CRITICAL:</b> <see cref="OriginId"/> and <see cref="TargetId"/> are
    /// <c>Guid?</c> (nullable), faithfully preserving the original monolith hook
    /// signature where a delete may target all origin or all target records when
    /// the corresponding identifier is <c>null</c>. This differs from the
    /// <c>PreRelationCreateEvent</c> which uses non-nullable <c>Guid</c>.
    /// </para>
    /// </summary>
    public class PreRelationDeleteEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the event was created.
        /// <para>
        /// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless
        /// constructor. Used for event ordering, idempotency checks, and audit
        /// trail reconstruction across distributed services.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing event chains
        /// across service boundaries.
        /// <para>
        /// Initialized to <see cref="Guid.NewGuid()"/> in the parameterless
        /// constructor. Links all related events triggered by a single user action
        /// for distributed tracing and debugging.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the entity name associated with this event.
        /// <para>
        /// For relation-specific events this property may be empty or null, as
        /// the <see cref="RelationName"/> property carries the primary routing
        /// identifier. Subscribers may still use <see cref="EntityName"/> when
        /// relation events are entity-contextual.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the name of the many-to-many relation being deleted.
        /// <para>
        /// Corresponds to the <c>relationName</c> parameter in the original
        /// <c>IErpPreDeleteManyToManyRelationHook.OnPreDelete</c> method and the
        /// <c>RecordHookManager.ExecutePreDeleteManyToManyRelationHook</c> call.
        /// Used by event subscribers to filter and handle only the relation
        /// deletions they are responsible for.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "relationName")]
        public string RelationName { get; set; }

        /// <summary>
        /// Gets or sets the origin record identifier in the relation being deleted.
        /// <para>
        /// <b>Nullable:</b> A <c>null</c> value indicates that the deletion targets
        /// all origin records matching the specified <see cref="TargetId"/>. This
        /// preserves the original monolith <c>Guid? originId</c> parameter from
        /// <c>IErpPreDeleteManyToManyRelationHook.OnPreDelete</c>.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "originId")]
        public Guid? OriginId { get; set; }

        /// <summary>
        /// Gets or sets the target record identifier in the relation being deleted.
        /// <para>
        /// <b>Nullable:</b> A <c>null</c> value indicates that the deletion targets
        /// all target records matching the specified <see cref="OriginId"/>. This
        /// preserves the original monolith <c>Guid? targetId</c> parameter from
        /// <c>IErpPreDeleteManyToManyRelationHook.OnPreDelete</c>.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "targetId")]
        public Guid? TargetId { get; set; }

        /// <summary>
        /// Gets or sets the mutable list of validation errors that event subscribers
        /// may populate to block the relation deletion.
        /// <para>
        /// Matches the <c>List&lt;ErrorModel&gt; errors</c> parameter from the
        /// original <c>IErpPreDeleteManyToManyRelationHook.OnPreDelete</c> method.
        /// After all subscribers have processed this event, the originating service
        /// inspects this list — if it contains any entries, the deletion is aborted
        /// and the errors are returned to the caller.
        /// </para>
        /// <para>
        /// Each <see cref="ErrorModel"/> carries <see cref="ErrorModel.Key"/>,
        /// <see cref="ErrorModel.Value"/>, and <see cref="ErrorModel.Message"/>
        /// properties describing the validation failure.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "validationErrors")]
        public List<ErrorModel> ValidationErrors { get; set; }

        /// <summary>
        /// Initializes a new instance of <see cref="PreRelationDeleteEvent"/> with
        /// sensible defaults for all required properties.
        /// <list type="bullet">
        ///   <item><see cref="Timestamp"/> is set to <see cref="DateTime.UtcNow"/>.</item>
        ///   <item><see cref="CorrelationId"/> is set to a new <see cref="Guid"/>.</item>
        ///   <item><see cref="ValidationErrors"/> is initialized to an empty list.</item>
        /// </list>
        /// </summary>
        public PreRelationDeleteEvent()
        {
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = Guid.NewGuid();
            ValidationErrors = new List<ErrorModel>();
        }
    }
}
