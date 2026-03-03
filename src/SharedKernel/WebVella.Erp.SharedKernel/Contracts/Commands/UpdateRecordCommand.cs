using System;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Commands
{
	/// <summary>
	/// Command contract for updating an entity record in the WebVella ERP microservice architecture.
	/// Replaces the monolith's direct <c>RecordManager.UpdateRecord(entityName, record)</c> call pattern
	/// with a serializable command for cross-service communication via REST, gRPC, or message broker
	/// (MassTransit with RabbitMQ/SNS+SQS).
	/// </summary>
	/// <remarks>
	/// <para>
	/// The <see cref="Record"/> payload must contain the <c>id</c> field to identify which record
	/// to update. The <see cref="EntityName"/> serves as the routing key that determines which
	/// microservice owns the target entity.
	/// </para>
	/// <para>
	/// Derived from the following monolith patterns:
	/// <list type="bullet">
	///   <item>
	///     <description>
	///       <c>RecordManager.UpdateRecord(string entityName, EntityRecord record)</c> — the primary
	///       update entry point that accepts the entity name and a record carrying updated field values.
	///     </description>
	///   </item>
	///   <item>
	///     <description>
	///       <c>IErpPreUpdateRecordHook.OnPreUpdateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>
	///       — the pre-update hook interface that received the same entity name and record payload.
	///     </description>
	///   </item>
	///   <item>
	///     <description>
	///       <c>RecordHookManager.ExecutePreUpdateRecordHooks(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>
	///       — the hook execution orchestrator that dispatched update hooks in-process.
	///     </description>
	///   </item>
	/// </list>
	/// </para>
	/// <para>
	/// In the microservice architecture, this command replaces those synchronous in-process patterns
	/// with an explicit, serializable command object. Pre-update validation that was previously handled
	/// by <c>IErpPreUpdateRecordHook</c> implementations is now performed by the owning service's
	/// command handler before executing the update.
	/// </para>
	/// </remarks>
	public class UpdateRecordCommand : ICommand
	{
		/// <summary>
		/// Unique correlation identifier for distributed tracing across microservice boundaries.
		/// Implements <see cref="ICommand.CorrelationId"/>.
		/// </summary>
		/// <remarks>
		/// Auto-generated via <see cref="Guid.NewGuid()"/> in the parameterless constructor.
		/// Propagated through all downstream service calls and events triggered by this command,
		/// enabling end-to-end tracing of an update operation as it flows through the Core service,
		/// event bus, and any subscriber services (CRM, Project, etc.).
		/// </remarks>
		[JsonProperty(PropertyName = "correlationId")]
		public Guid CorrelationId { get; set; }

		/// <summary>
		/// UTC timestamp recording when the command was created/issued.
		/// Implements <see cref="ICommand.Timestamp"/>.
		/// </summary>
		/// <remarks>
		/// Set to <see cref="DateTime.UtcNow"/> in the parameterless constructor.
		/// Used for temporal ordering, audit logging, and conflict resolution in the distributed
		/// microservice architecture. Commands with the same <see cref="CorrelationId"/> can be
		/// ordered by their <see cref="Timestamp"/> to determine sequence of operations.
		/// </remarks>
		[JsonProperty(PropertyName = "timestamp")]
		public DateTime Timestamp { get; set; }

		/// <summary>
		/// The name of the entity whose record is being updated.
		/// </summary>
		/// <remarks>
		/// Maps directly to the <c>entityName</c> parameter in the monolith's
		/// <c>RecordManager.UpdateRecord(string entityName, EntityRecord record)</c> method.
		/// This serves as the routing key that determines which microservice owns the entity
		/// and should process this update command. For example, "account" routes to the CRM service,
		/// "task" routes to the Project service, and "user" routes to the Core service.
		/// </remarks>
		[JsonProperty(PropertyName = "entityName")]
		public string EntityName { get; set; }

		/// <summary>
		/// The <see cref="EntityRecord"/> payload containing updated field values.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Maps to the <c>record</c> parameter in the monolith's
		/// <c>RecordManager.UpdateRecord(string entityName, EntityRecord record)</c> and the
		/// <c>record</c> parameter in
		/// <c>IErpPreUpdateRecordHook.OnPreUpdateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>.
		/// </para>
		/// <para>
		/// The record <b>must</b> contain the <c>id</c> field (a <see cref="Guid"/>) to identify
		/// which record to update. Additional fields in the record represent the values to be
		/// updated. <see cref="EntityRecord"/> is the Expando-based dynamic record type from
		/// <c>WebVella.Erp.SharedKernel.Models</c> that allows arbitrary field names determined
		/// at runtime from entity metadata.
		/// </para>
		/// </remarks>
		[JsonProperty(PropertyName = "record")]
		public EntityRecord Record { get; set; }

		/// <summary>
		/// Initializes a new instance of the <see cref="UpdateRecordCommand"/> class
		/// with an auto-generated <see cref="CorrelationId"/> and the current UTC time
		/// as the <see cref="Timestamp"/>.
		/// </summary>
		public UpdateRecordCommand()
		{
			CorrelationId = Guid.NewGuid();
			Timestamp = DateTime.UtcNow;
		}
	}
}
