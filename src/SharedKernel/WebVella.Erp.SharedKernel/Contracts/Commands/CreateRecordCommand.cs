using System;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Commands
{
	/// <summary>
	/// Command contract for creating an entity record in the WebVella ERP microservice architecture.
	/// Replaces the monolith's direct <c>RecordManager.CreateRecord(entityName, record)</c> call pattern
	/// with a serializable command for cross-service communication via REST, gRPC, or message broker
	/// (MassTransit with RabbitMQ/SNS+SQS).
	/// </summary>
	/// <remarks>
	/// <para>
	/// In the original monolith, record creation is a synchronous in-process operation:
	/// <c>RecordManager.CreateRecord(string entityName, EntityRecord record)</c> validates the entity,
	/// invokes <c>RecordHookManager.ExecutePreCreateRecordHooks</c>, performs the database insert via
	/// <c>DbRecordRepository</c>, then invokes <c>ExecutePostCreateRecordHooks</c>.
	/// </para>
	/// <para>
	/// In the microservice architecture, a <see cref="CreateRecordCommand"/> is issued by the API Gateway
	/// or another service, routed to the owning service based on <see cref="EntityName"/>, and processed
	/// asynchronously. The owning service validates the command, performs the insert, and publishes a
	/// <c>RecordCreatedEvent</c> for downstream subscribers.
	/// </para>
	/// <para>
	/// The <see cref="EntityName"/> property serves as the routing key that determines which microservice
	/// owns the entity and should process the command. The <see cref="Record"/> property carries the
	/// Expando-based dynamic field values, preserving the monolith's flexible entity-field model.
	/// </para>
	/// <para>
	/// All properties are annotated with <see cref="JsonPropertyAttribute"/> using camelCase naming
	/// to ensure consistent JSON serialization for MassTransit message transport and API contract stability.
	/// </para>
	/// </remarks>
	public class CreateRecordCommand : ICommand
	{
		/// <summary>
		/// Unique correlation identifier for distributed tracing across microservice boundaries.
		/// Auto-generated via <see cref="Guid.NewGuid()"/> in the default constructor.
		/// Propagated through all downstream service calls and events triggered by this command.
		/// </summary>
		/// <remarks>
		/// Implements <see cref="ICommand.CorrelationId"/>.
		/// Enables end-to-end tracing of a create operation from the API Gateway through the
		/// owning service's record creation, pre/post event publishing, and any downstream
		/// event subscribers (e.g., CRM search index regeneration after account creation).
		/// </remarks>
		[JsonProperty(PropertyName = "correlationId")]
		public Guid CorrelationId { get; set; }

		/// <summary>
		/// UTC timestamp recording when the command was created/issued.
		/// Used for temporal ordering, audit logging, and conflict resolution
		/// in the distributed microservice architecture.
		/// </summary>
		/// <remarks>
		/// Implements <see cref="ICommand.Timestamp"/>.
		/// Set to <see cref="DateTime.UtcNow"/> in the default constructor.
		/// Used by the processing service for audit trails and by the message broker
		/// infrastructure for message ordering and deduplication windows.
		/// </remarks>
		[JsonProperty(PropertyName = "timestamp")]
		public DateTime Timestamp { get; set; }

		/// <summary>
		/// The name of the entity to create a record for.
		/// Serves as the routing key that determines which microservice owns the entity
		/// and should process this command.
		/// </summary>
		/// <remarks>
		/// Maps directly to the <c>entityName</c> parameter in the monolith's
		/// <c>RecordManager.CreateRecord(string entityName, EntityRecord record)</c> method
		/// and <c>IErpPreCreateRecordHook.OnPreCreateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>.
		/// Examples: "user", "role", "account", "contact", "task", "email".
		/// The API Gateway or command dispatcher uses this value to route the command
		/// to the appropriate service (e.g., "account" routes to CRM service,
		/// "task" routes to Project service).
		/// </remarks>
		[JsonProperty(PropertyName = "entityName")]
		public string EntityName { get; set; }

		/// <summary>
		/// The <see cref="EntityRecord"/> payload containing all field values for the new record.
		/// Uses the Expando-based dynamic property model to support the flexible entity-field system.
		/// </summary>
		/// <remarks>
		/// Maps to the <c>record</c> parameter in the monolith's
		/// <c>RecordManager.CreateRecord(string entityName, EntityRecord record)</c> and
		/// <c>IErpPreCreateRecordHook.OnPreCreateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>.
		/// The <see cref="EntityRecord"/> inherits from <c>Expando</c>, allowing dynamic field access
		/// via a Properties dictionary (e.g., <c>record["name"] = "Acme Corp"</c>).
		/// Field names and types are determined at runtime from entity metadata.
		/// </remarks>
		[JsonProperty(PropertyName = "record")]
		public EntityRecord Record { get; set; }

		/// <summary>
		/// The unique identifier of the user who initiated the create operation.
		/// <para>
		/// In the monolith, <c>RecordManager.CreateRecord</c> obtains the user identity from
		/// <c>SecurityContext.CurrentUser</c> (an <c>AsyncLocal&lt;Stack&lt;ErpUser&gt;&gt;</c>).
		/// In the microservice architecture, the user identity must be explicitly propagated
		/// with each command since the consuming service cannot access the originating service's
		/// security context. The API Gateway or originating service populates this from the
		/// authenticated JWT claims before dispatching the command.
		/// </para>
		/// </summary>
		[JsonProperty(PropertyName = "userId")]
		public Guid? UserId { get; set; }

		/// <summary>
		/// The roles assigned to the user who initiated the create operation.
		/// <para>
		/// In the monolith, <c>SecurityContext.HasEntityPermission</c> checks the current user's
		/// roles against entity-level <c>RecordPermissions</c>. In the microservice architecture,
		/// roles are propagated from JWT claims so the consuming service can perform authorization
		/// without callback to the Core identity service.
		/// </para>
		/// </summary>
		[JsonProperty(PropertyName = "roles")]
		public System.Collections.Generic.List<string> Roles { get; set; } = new System.Collections.Generic.List<string>();

		/// <summary>
		/// Initializes a new instance of the <see cref="CreateRecordCommand"/> class
		/// with auto-generated <see cref="CorrelationId"/> and current UTC <see cref="Timestamp"/>.
		/// </summary>
		/// <remarks>
		/// The parameterless constructor ensures that every command instance has a unique
		/// correlation ID for distributed tracing and an accurate creation timestamp,
		/// even when deserialized from JSON (Newtonsoft.Json calls the parameterless constructor
		/// then populates properties, but defaults are available for programmatic construction).
		/// </remarks>
		public CreateRecordCommand()
		{
			CorrelationId = Guid.NewGuid();
			Timestamp = DateTime.UtcNow;
		}
	}
}
