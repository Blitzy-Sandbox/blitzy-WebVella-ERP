using System;
using Newtonsoft.Json;

namespace WebVella.Erp.SharedKernel.Contracts.Commands
{
	/// <summary>
	/// Command contract for deleting an entity record in the WebVella ERP microservice architecture.
	/// Replaces the monolith's direct <c>RecordManager.DeleteRecord(entityName, id)</c> call pattern
	/// with a serializable command for cross-service communication via REST, gRPC, or message broker.
	/// Only carries the <see cref="RecordId"/> (not the full EntityRecord) — the owning service loads
	/// the record before deletion to support pre-delete hooks and post-delete event publishing.
	/// </summary>
	/// <remarks>
	/// <para>
	/// In the original monolith, record deletion was performed by calling
	/// <c>RecordManager.DeleteRecord(string entityName, Guid id)</c> (RecordManager.cs line 1579),
	/// which validated the entity, loaded the record, executed pre-delete hooks via
	/// <c>RecordHookManager.ExecutePreDeleteRecordHooks(entityName, record, errors)</c>
	/// (RecordHookManager.cs lines 78-89), performed the database delete, then executed
	/// post-delete hooks. The <c>IErpPreDeleteRecordHook</c> interface received the full
	/// EntityRecord, but this command intentionally carries only the RecordId since the owning
	/// service will load the record internally before deletion.
	/// </para>
	/// <para>
	/// This command is a pure data contract with no business logic, validation, or service
	/// dependencies, following the SharedKernel rules defined in AAP 0.8.2. It is serializable
	/// via Newtonsoft.Json for transport over MassTransit (RabbitMQ or SNS+SQS).
	/// </para>
	/// <para>
	/// Usage: Publish via MassTransit or invoke via REST/gRPC to request record deletion
	/// from the service that owns the specified entity. The <see cref="EntityName"/> acts as
	/// the routing key to determine which microservice handles the command.
	/// </para>
	/// </remarks>
	public class DeleteRecordCommand : ICommand
	{
		/// <summary>
		/// Unique correlation identifier for distributed tracing across microservice boundaries.
		/// Auto-generated via <see cref="Guid.NewGuid()"/> in the parameterless constructor.
		/// Propagated through all downstream service calls and events triggered by this command,
		/// enabling end-to-end tracing of the delete operation across service boundaries.
		/// </summary>
		/// <remarks>
		/// Implements <see cref="ICommand.CorrelationId"/>. In the monolith, operations were
		/// traceable via the in-process call stack. In the microservice architecture, this
		/// CorrelationId enables tracing a delete command from the Gateway through the owning
		/// service's pre-delete logic, database operation, and post-delete event publication.
		/// </remarks>
		[JsonProperty(PropertyName = "correlationId")]
		public Guid CorrelationId { get; set; }

		/// <summary>
		/// UTC timestamp recording when the delete command was created/issued.
		/// Used for temporal ordering, audit logging, and conflict resolution
		/// in the distributed microservice architecture.
		/// </summary>
		/// <remarks>
		/// Implements <see cref="ICommand.Timestamp"/>. Set to <see cref="DateTime.UtcNow"/>
		/// in the parameterless constructor. Enables consumers to determine command freshness
		/// and supports idempotent processing by comparing timestamps of duplicate commands.
		/// </remarks>
		[JsonProperty(PropertyName = "timestamp")]
		public DateTime Timestamp { get; set; }

		/// <summary>
		/// The name of the entity whose record is being deleted. Maps directly to the
		/// <c>entityName</c> parameter in <c>RecordManager.DeleteRecord(string entityName, Guid id)</c>
		/// (RecordManager.cs line 1579). This value acts as the routing key that determines
		/// which microservice owns the entity and should process the deletion.
		/// </summary>
		/// <remarks>
		/// In the monolith, the entity name was used to look up the <c>Entity</c> metadata
		/// and resolve the corresponding <c>rec_*</c> table. In the microservice architecture,
		/// the entity name additionally determines the target service: entities like "user" and
		/// "role" route to the Core service, "account" and "contact" route to CRM, "task" and
		/// "timelog" route to the Project service, etc.
		/// </remarks>
		[JsonProperty(PropertyName = "entityName")]
		public string EntityName { get; set; }

		/// <summary>
		/// The unique identifier of the record to delete. Maps directly to the <c>id</c>
		/// parameter in <c>RecordManager.DeleteRecord(string entityName, Guid id)</c>
		/// (RecordManager.cs line 1579). This is a non-nullable <see cref="Guid"/> because
		/// a valid record ID is always required for deletion.
		/// </summary>
		/// <remarks>
		/// Unlike the Create and Update commands which carry the full <c>EntityRecord</c>
		/// payload, the Delete command only needs the record ID. The owning service will load
		/// the record internally before deletion to:
		/// <list type="number">
		/// <item><description>Execute pre-delete validation and business rules</description></item>
		/// <item><description>Publish a post-delete event containing the deleted record snapshot</description></item>
		/// <item><description>Support cascade delete or referential integrity checks</description></item>
		/// </list>
		/// This mirrors the monolith's pattern where <c>RecordHookManager.ExecutePreDeleteRecordHooks</c>
		/// received the full EntityRecord loaded from the database, not from the caller.
		/// </remarks>
		[JsonProperty(PropertyName = "recordId")]
		public Guid RecordId { get; set; }

		/// <summary>
		/// Initializes a new instance of the <see cref="DeleteRecordCommand"/> class with
		/// auto-generated <see cref="CorrelationId"/> and current UTC <see cref="Timestamp"/>.
		/// The <see cref="EntityName"/> and <see cref="RecordId"/> must be set by the caller.
		/// </summary>
		public DeleteRecordCommand()
		{
			CorrelationId = Guid.NewGuid();
			Timestamp = DateTime.UtcNow;
		}
	}
}
