using System;

namespace WebVella.Erp.SharedKernel.Contracts.Commands
{
	/// <summary>
	/// Base marker interface for all command contracts in the WebVella ERP microservice architecture.
	/// All command types must implement this interface to ensure consistent distributed tracing
	/// and temporal ordering across service boundaries.
	/// </summary>
	/// <remarks>
	/// This is part of the CQRS-light pattern for the SharedKernel. Command contracts are pure
	/// data objects with no business logic, serializable via Newtonsoft.Json for transport
	/// via REST API, gRPC, or message broker (MassTransit with RabbitMQ/SNS+SQS).
	///
	/// In the original monolith, write operations were dispatched through synchronous in-process
	/// hook callbacks (see <c>RecordHookManager</c> and the 12 <c>IErp*Hook</c> interfaces).
	/// Commands replace the "write" side of that hook system with serializable command objects
	/// that can be routed across microservice boundaries.
	///
	/// The monolith's <c>RecordManager</c> CRUD pattern (validate → execute pre-hooks → perform
	/// operation → execute post-hooks → return response) is formalized by this interface:
	/// the command object captures the intent, while event contracts capture the outcome.
	///
	/// Implementations include:
	/// - <see cref="CreateRecordCommand"/> for creating entity records
	/// - <see cref="UpdateRecordCommand"/> for updating entity records
	/// - <see cref="DeleteRecordCommand"/> for deleting entity records
	/// - <see cref="CommandEnvelope{TCommand}"/> wraps any ICommand with user context metadata
	/// </remarks>
	public interface ICommand
	{
		/// <summary>
		/// Unique correlation identifier for distributed tracing across microservice boundaries.
		/// Auto-generated via <see cref="Guid.NewGuid()"/> in implementations' constructors.
		/// Propagated through all downstream service calls and events triggered by this command.
		/// </summary>
		/// <remarks>
		/// In the monolith, operations are in-process and traceable via call stack. In the
		/// microservice architecture, the CorrelationId enables tracing command execution across
		/// service calls (e.g., a Gateway command that triggers record creation in the Core
		/// service, which publishes a RecordCreatedEvent consumed by the CRM service).
		/// This mirrors the <c>IDomainEvent.CorrelationId</c> and <c>IQuery.CorrelationId</c>
		/// patterns established in the Events and Queries folders respectively.
		/// </remarks>
		Guid CorrelationId { get; set; }

		/// <summary>
		/// UTC timestamp recording when the command was created/issued.
		/// Used for temporal ordering, audit logging, and conflict resolution
		/// in the distributed microservice architecture.
		/// </summary>
		/// <remarks>
		/// Set to <see cref="DateTime.UtcNow"/> in implementations' constructors.
		/// Unlike <c>IQuery</c> (which only has CorrelationId), <c>ICommand</c> includes
		/// Timestamp because commands represent state-changing operations that need temporal
		/// ordering for audit trails and conflict resolution. This mirrors the
		/// <c>IDomainEvent.Timestamp</c> pattern in the Events folder.
		/// </remarks>
		DateTime Timestamp { get; set; }
	}
}
