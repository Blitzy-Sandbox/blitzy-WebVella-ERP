using System;
using Newtonsoft.Json;

namespace WebVella.Erp.SharedKernel.Contracts.Commands
{
	/// <summary>
	/// Generic command envelope that wraps any <see cref="ICommand"/> implementation with
	/// cross-cutting metadata required for microservice command processing.
	/// Carries user context (previously ambient via SecurityContext), service routing information,
	/// and correlation tracking data for distributed tracing.
	/// </summary>
	/// <remarks>
	/// <para>
	/// In the original monolith, cross-cutting concerns such as the current user identity and roles
	/// were available implicitly via <c>SecurityContext.CurrentUser</c> (backed by
	/// <c>AsyncLocal&lt;Stack&lt;ErpUser&gt;&gt;</c>). In the microservice architecture, these must
	/// be propagated explicitly with each command. The <see cref="CommandEnvelope{TCommand}"/>
	/// captures this metadata alongside the command payload so that downstream services can
	/// authorize and audit command execution without callback to the Core service.
	/// </para>
	/// <para>
	/// The envelope mirrors the correlation and timestamp properties defined on <see cref="ICommand"/>
	/// to maintain distributed tracing consistency — the envelope's own <see cref="CorrelationId"/>
	/// typically matches the wrapped command's <see cref="ICommand.CorrelationId"/>, while
	/// the <see cref="Timestamp"/> records when the envelope itself was created (which may differ
	/// from the command creation time if the command was queued before dispatch).
	/// </para>
	/// <para>
	/// All properties are annotated with <see cref="JsonPropertyAttribute"/> for consistent
	/// camelCase JSON serialization via Newtonsoft.Json, ensuring compatibility with MassTransit
	/// message transport over RabbitMQ and SNS+SQS (LocalStack/AWS).
	/// </para>
	/// </remarks>
	/// <typeparam name="TCommand">The command type being wrapped. Must implement <see cref="ICommand"/>.</typeparam>
	public class CommandEnvelope<TCommand> where TCommand : ICommand
	{
		/// <summary>
		/// Unique correlation identifier for distributed tracing across microservice boundaries.
		/// Copied from or matches the wrapped command's <see cref="ICommand.CorrelationId"/> to
		/// ensure end-to-end traceability of a single logical operation through multiple services.
		/// </summary>
		/// <remarks>
		/// Auto-generated via <see cref="Guid.NewGuid()"/> in the parameterless constructor.
		/// Consumers should overwrite this with the wrapped command's CorrelationId when building
		/// the envelope from an existing command to maintain trace continuity.
		/// </remarks>
		[JsonProperty(PropertyName = "correlationId")]
		public Guid CorrelationId { get; set; }

		/// <summary>
		/// UTC timestamp recording when the envelope was created. May differ from the wrapped
		/// command's <see cref="ICommand.Timestamp"/> if the command was created before being
		/// enveloped (e.g., queued or batched before dispatch).
		/// </summary>
		/// <remarks>
		/// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless constructor.
		/// Used for audit logging, message ordering, and staleness detection in the
		/// event-driven architecture.
		/// </remarks>
		[JsonProperty(PropertyName = "timestamp")]
		public DateTime Timestamp { get; set; }

		/// <summary>
		/// The unique identifier of the user issuing the command. Replaces the monolith's ambient
		/// <c>SecurityContext.CurrentUser</c> (<c>AsyncLocal&lt;Stack&lt;ErpUser&gt;&gt;</c>)
		/// with an explicit value extracted from the JWT token.
		/// </summary>
		/// <remarks>
		/// Maps to <c>ErpUser.Id</c> from SharedKernel.Models. In the microservice architecture,
		/// the user identity is propagated via JWT claims rather than thread-local storage.
		/// Downstream services use this ID to enforce record-level permissions and populate
		/// audit fields (created_by, last_modified_by) without requiring a callback to the
		/// Core/Identity service.
		/// </remarks>
		[JsonProperty(PropertyName = "userId")]
		public Guid UserId { get; set; }

		/// <summary>
		/// Array of role identifiers (as strings) for the user issuing the command. Extracted from
		/// JWT claims to enable downstream services to authorize requests without callback to the
		/// Core service.
		/// </summary>
		/// <remarks>
		/// In the monolith, roles were accessed via <c>SecurityContext.CurrentUser.Roles</c>.
		/// In the microservice architecture, JWT tokens issued by the Core service contain all
		/// necessary claims (user ID, roles, permissions) per the API contract. Role IDs are
		/// serialized as strings for maximum interoperability across transport protocols.
		/// Initialized to <see cref="Array.Empty{T}()"/> in the parameterless constructor to
		/// prevent null reference exceptions during deserialization or default construction.
		/// </remarks>
		[JsonProperty(PropertyName = "userRoles")]
		public string[] UserRoles { get; set; }

		/// <summary>
		/// The name of the originating microservice (e.g., "core", "crm", "project", "mail").
		/// Used for routing, audit logging, and dead-letter analysis in the event-driven architecture.
		/// </summary>
		/// <remarks>
		/// In the monolith, hook execution used entity names as routing keys (see
		/// <c>RecordHookManager.ContainsAnyHooksForEntity</c>). In the microservice architecture,
		/// the source service name provides analogous routing context for message consumers to
		/// identify the origin of cross-service commands.
		/// </remarks>
		[JsonProperty(PropertyName = "sourceService")]
		public string SourceService { get; set; }

		/// <summary>
		/// The wrapped command payload. This is the actual <see cref="ICommand"/> implementation
		/// (e.g., CreateRecordCommand, UpdateRecordCommand, DeleteRecordCommand) containing
		/// the operation-specific data.
		/// </summary>
		/// <remarks>
		/// The generic constraint <c>where TCommand : ICommand</c> ensures only valid command
		/// implementations can be wrapped. The command's own <see cref="ICommand.CorrelationId"/>
		/// and <see cref="ICommand.Timestamp"/> are preserved independently from the envelope's
		/// metadata, enabling consumers to distinguish between command creation time and
		/// envelope creation time.
		/// </remarks>
		[JsonProperty(PropertyName = "command")]
		public TCommand Command { get; set; }

		/// <summary>
		/// Initializes a new instance of the <see cref="CommandEnvelope{TCommand}"/> class
		/// with auto-generated metadata defaults.
		/// </summary>
		/// <remarks>
		/// Sets <see cref="CorrelationId"/> to a new <see cref="Guid"/>,
		/// <see cref="Timestamp"/> to <see cref="DateTime.UtcNow"/>, and
		/// <see cref="UserRoles"/> to an empty array. Callers should set <see cref="UserId"/>,
		/// <see cref="SourceService"/>, and <see cref="Command"/> after construction, and
		/// optionally overwrite <see cref="CorrelationId"/> with the command's own
		/// correlation ID for trace continuity.
		/// </remarks>
		public CommandEnvelope()
		{
			CorrelationId = Guid.NewGuid();
			Timestamp = DateTime.UtcNow;
			UserRoles = Array.Empty<string>();
		}
	}
}
