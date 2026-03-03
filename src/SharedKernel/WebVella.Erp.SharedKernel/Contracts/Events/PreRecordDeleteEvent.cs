using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event raised before an entity record is deleted, replacing the monolith's
    /// synchronous <c>IErpPreDeleteRecordHook</c> interface with an asynchronous,
    /// event-driven contract for inter-service communication.
    /// <para>
    /// Source hook signature (WebVella.Erp/Hooks/IErpPreDeleteRecordHook.cs, line 9):
    /// <code>void OnPreDeleteRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</code>
    /// </para>
    /// <para>
    /// This is a <strong>pre-operation</strong> event: subscribers may populate
    /// <see cref="ValidationErrors"/> to block the pending delete operation. The publishing
    /// service inspects the error list after all subscribers have processed the event —
    /// if any errors are present, the delete is aborted and the errors are returned to the caller.
    /// This preserves the monolith's pattern where <c>RecordHookManager.ExecutePreDeleteRecordHooks</c>
    /// (lines 78-89) passes a mutable <c>List&lt;ErrorModel&gt;</c> to each hook instance.
    /// </para>
    /// <para>
    /// Pure data contract — no business logic, no service dependencies. Serialized via
    /// Newtonsoft.Json and transported via MassTransit (RabbitMQ for local/Docker,
    /// SNS+SQS for AWS/LocalStack validation).
    /// </para>
    /// </summary>
    public class PreRecordDeleteEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the pre-delete event was created.
        /// Initialized to <see cref="DateTime.UtcNow"/> by the parameterless constructor.
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing the delete operation
        /// across distributed service boundaries. Initialized to <see cref="Guid.NewGuid()"/>
        /// by the parameterless constructor.
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the name of the entity whose record is being deleted.
        /// Mirrors the <c>entityName</c> parameter from the source hook interface
        /// <c>IErpPreDeleteRecordHook.OnPreDeleteRecord</c> and the
        /// <c>RecordHookManager.ExecutePreDeleteRecordHooks</c> method.
        /// Subscribers use this property to filter events for entities they manage.
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the <see cref="EntityRecord"/> that is about to be deleted.
        /// Corresponds to the <c>EntityRecord record</c> parameter from the source hook
        /// <c>IErpPreDeleteRecordHook.OnPreDeleteRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>.
        /// Contains all current field values of the record targeted for deletion.
        /// </summary>
        [JsonProperty(PropertyName = "record")]
        public EntityRecord Record { get; set; }

        /// <summary>
        /// Gets or sets the mutable list of validation errors that pre-delete event subscribers
        /// can populate to block the pending delete operation.
        /// Corresponds to the <c>List&lt;ErrorModel&gt; errors</c> parameter from the source hook
        /// <c>IErpPreDeleteRecordHook.OnPreDeleteRecord</c>.
        /// <para>
        /// If any <see cref="ErrorModel"/> entries are present after all subscribers have
        /// processed the event, the delete operation is aborted and the errors are surfaced
        /// to the caller in the response envelope.
        /// </para>
        /// Initialized to an empty <c>List&lt;ErrorModel&gt;()</c> by the parameterless constructor.
        /// </summary>
        [JsonProperty(PropertyName = "validationErrors")]
        public List<ErrorModel> ValidationErrors { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PreRecordDeleteEvent"/> class
        /// with default values: <see cref="Timestamp"/> set to <see cref="DateTime.UtcNow"/>,
        /// <see cref="CorrelationId"/> set to a new <see cref="Guid"/>, and
        /// <see cref="ValidationErrors"/> initialized to an empty list.
        /// </summary>
        public PreRecordDeleteEvent()
        {
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = Guid.NewGuid();
            ValidationErrors = new List<ErrorModel>();
        }
    }
}
