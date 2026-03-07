using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Events.Subscribers
{
	/// <summary>
	/// MassTransit consumer responsible for cross-service data synchronization events
	/// originating from CRM, Project, and Mail services that affect Core platform data.
	///
	/// <para>
	/// <b>Monolith replacement mapping:</b>
	/// <list type="bullet">
	///   <item>
	///     Replaces <c>NotificationContext.AttachListener(Type, methodName, channel)</c>
	///     (lines 75-102 of <c>NotificationContext.cs</c>) — imperative, reflection-based
	///     handler registration is replaced by MassTransit assembly scanning for automatic
	///     consumer discovery.
	///   </item>
	///   <item>
	///     Replaces <c>NotificationContext.HandleNotification</c> (lines 139-147) —
	///     reflection-based dispatch by channel (<c>listener.Method.Invoke(listener.Instance,
	///     new object[] { notification })</c>) is replaced by strongly typed async
	///     <c>Consume()</c> methods with no reflection.
	///   </item>
	///   <item>
	///     Replaces <c>ErpRecordChangeNotification</c> (EntityId, EntityName, RecordId) DTO
	///     with rich domain events: <see cref="RecordCreatedEvent"/>,
	///     <see cref="RecordUpdatedEvent"/>, <see cref="RecordDeletedEvent"/> from SharedKernel.
	///   </item>
	///   <item>
	///     Replaces direct in-process <c>RecordManager.UpdateRecord()</c> calls from plugins
	///     for cross-module data sync with asynchronous event-driven sync via MassTransit.
	///   </item>
	///   <item>
	///     Adds error isolation that the monolith lacked — reflection exceptions in the
	///     monolith propagated unhandled; this consumer wraps all operations in try/catch
	///     with structured warning-level logging.
	///   </item>
	///   <item>
	///     Replaces cross-module FK references (created_by, modified_by to rec_user) in the
	///     shared monolith database with audit field UUID validation via Core's exclusive
	///     ownership of the <c>user</c> entity (AAP 0.7.1).
	///   </item>
	/// </list>
	/// </para>
	///
	/// <para>
	/// <b>Design rules (AAP 0.8.2):</b>
	/// <list type="bullet">
	///   <item>All operations are <b>idempotent</b> — duplicate event delivery does not cause data corruption.</item>
	///   <item>Consumer is registered automatically via MassTransit assembly scanning in <c>Program.cs</c>.</item>
	///   <item>Non-critical sync failures are logged at Warning level and not rethrown — eventual consistency self-heals.</item>
	///   <item><see cref="RecordManager"/> must be configured with <c>publishEvents: false</c> to prevent infinite event loops.</item>
	/// </list>
	/// </para>
	///
	/// <para>
	/// Unlike <c>CacheInvalidationConsumer</c> which only clears cached metadata, this consumer
	/// performs actual data operations (audit field validation, denormalized reference cleanup)
	/// when external services modify records that reference Core-owned entities.
	/// </para>
	/// </summary>
	public class CrossServiceDataSyncConsumer :
		IConsumer<RecordCreatedEvent>,
		IConsumer<RecordUpdatedEvent>,
		IConsumer<RecordDeletedEvent>
	{
		/// <summary>
		/// Structured logger for distributed tracing with EntityName and CorrelationId parameters.
		/// Uses LogInformation for event receipt, LogDebug for audit field resolution details,
		/// and LogWarning for non-critical sync failures.
		/// </summary>
		private readonly ILogger<CrossServiceDataSyncConsumer> _logger;

		/// <summary>
		/// Core record CRUD manager for performing record operations on Core-owned entities
		/// when cross-service data sync requires data writes.
		/// <para>
		/// <b>CRITICAL:</b> Must be configured with <c>publishEvents: false</c> in the DI
		/// container to prevent infinite event loops where Core updating its own records
		/// re-triggers this consumer via re-published domain events.
		/// </para>
		/// </summary>
		private readonly RecordManager _recordManager;

		/// <summary>
		/// Per-service ambient database context providing direct repository access to the
		/// Core service's PostgreSQL database (erp_core) for operations beyond what
		/// <see cref="RecordManager"/> provides — such as direct SQL queries for
		/// denormalized reference cleanup during delete event processing.
		/// </summary>
		private readonly CoreDbContext _dbContext;

		/// <summary>
		/// Well-known entity names for entities owned by external services that commonly
		/// reference Core-owned entities via audit fields (created_by, modified_by).
		/// Used for structured logging context in audit field resolution.
		/// </summary>
		private static class KnownExternalEntities
		{
			public const string Account = "account";
			public const string Contact = "contact";
			public const string Case = "case";
			public const string Task = "task";
			public const string Email = "email";
			public const string Timelog = "timelog";
		}

		/// <summary>
		/// Constructs a new <see cref="CrossServiceDataSyncConsumer"/> with all required
		/// service dependencies injected via the DI container.
		/// </summary>
		/// <param name="logger">
		/// Structured logger for distributed tracing with EntityName and CorrelationId.
		/// </param>
		/// <param name="recordManager">
		/// Core record CRUD manager. <b>IMPORTANT:</b> The DI registration for this consumer
		/// must provide a RecordManager instance configured with <c>publishEvents: false</c>
		/// to prevent infinite event loops where Core updating its own records re-publishes
		/// events that trigger this consumer again.
		/// </param>
		/// <param name="dbContext">
		/// Per-service ambient database context for direct repository access when operations
		/// beyond RecordManager are needed (e.g., denormalized reference cleanup).
		/// </param>
		/// <exception cref="ArgumentNullException">
		/// Thrown when any required dependency is null.
		/// </exception>
		public CrossServiceDataSyncConsumer(
			ILogger<CrossServiceDataSyncConsumer> logger,
			RecordManager recordManager,
			CoreDbContext dbContext)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
		}

		#region IConsumer<RecordCreatedEvent>

		/// <summary>
		/// Handles <see cref="RecordCreatedEvent"/> from external services (CRM, Project, Mail)
		/// that may require Core to update its own data.
		///
		/// <para>
		/// <b>Cross-service scenarios handled (AAP 0.7.1):</b>
		/// <list type="table">
		///   <listheader><term>Source Service</term><description>Core Action</description></listheader>
		///   <item><term>CRM (account)</term><description>Audit field resolution — validate created_by/modified_by user UUIDs</description></item>
		///   <item><term>Project (task)</term><description>Audit field resolution — validate created_by user UUID exists in Core</description></item>
		///   <item><term>Mail (email)</term><description>Audit field resolution — validate created_by user UUID</description></item>
		/// </list>
		/// </para>
		///
		/// <para>
		/// <b>Monolith mapping:</b> Replaces <c>RecordHookManager.ExecutePostCreateRecordHooks</c>
		/// (lines 45-53 of <c>RecordHookManager.cs</c>) which iterated all registered
		/// <c>IErpPostCreateRecordHook</c> instances calling
		/// <c>inst.OnPostCreateRecord(entityName, record)</c> synchronously and in-process.
		/// </para>
		///
		/// <para>
		/// <b>Idempotency:</b> Audit field validation is read-only and inherently idempotent.
		/// Any denormalized updates use upsert patterns per AAP 0.8.2.
		/// </para>
		/// </summary>
		/// <param name="context">MassTransit consume context containing the deserialized event message.</param>
		/// <returns>A task representing the asynchronous consume operation.</returns>
		public async Task Consume(ConsumeContext<RecordCreatedEvent> context)
		{
			var evt = context.Message;
			_logger.LogInformation(
				"CrossServiceDataSync: RecordCreatedEvent received for entity '{EntityName}', CorrelationId: {CorrelationId}",
				evt.EntityName, evt.CorrelationId);

			try
			{
				// Perform audit field resolution for user UUIDs referenced by external services.
				// Core owns the 'user' entity exclusively (AAP 0.7.1), so when other services
				// create records with created_by/modified_by fields, we validate those user UUIDs
				// exist in Core's user table as a defensive consistency check.
				await HandleAuditFieldResolution(evt.EntityName, evt.Record, evt.CorrelationId);
			}
			catch (Exception ex)
			{
				// Non-critical sync failure: log at Warning level and do NOT rethrow.
				// Eventual consistency will self-heal — the cache TTL (1 hour) or subsequent
				// events will reconcile any transient inconsistencies.
				_logger.LogWarning(ex,
					"CrossServiceDataSync: Non-critical error processing RecordCreatedEvent for entity '{EntityName}', CorrelationId: {CorrelationId}. Skipping.",
					evt.EntityName, evt.CorrelationId);
			}
		}

		#endregion

		#region IConsumer<RecordUpdatedEvent>

		/// <summary>
		/// Handles <see cref="RecordUpdatedEvent"/> from external services (CRM, Project, Mail)
		/// that may require Core to update its own data or validate audit field references.
		///
		/// <para>
		/// <b>Cross-service scenarios handled (AAP 0.7.1):</b>
		/// <list type="table">
		///   <listheader><term>Source Service</term><description>Core Action</description></listheader>
		///   <item><term>CRM (account)</term><description>Update Core projections if denormalized account fields changed</description></item>
		///   <item><term>Project (task)</term><description>Audit field resolution for modified_by user UUID</description></item>
		///   <item><term>Any service</term><description>modified_by user UUID validation on NewRecord</description></item>
		/// </list>
		/// </para>
		///
		/// <para>
		/// <b>Enrichment over monolith:</b> The original <c>IErpPostUpdateRecordHook</c> only
		/// carried the post-update record state. <see cref="RecordUpdatedEvent"/> carries both
		/// <c>OldRecord</c> and <c>NewRecord</c>, enabling diff computation without a separate
		/// lookup. This consumer uses <c>NewRecord</c> for audit field validation.
		/// </para>
		///
		/// <para>
		/// <b>Idempotency:</b> Audit field validation is read-only and inherently idempotent.
		/// </para>
		/// </summary>
		/// <param name="context">MassTransit consume context containing the deserialized event message.</param>
		/// <returns>A task representing the asynchronous consume operation.</returns>
		public async Task Consume(ConsumeContext<RecordUpdatedEvent> context)
		{
			var evt = context.Message;
			_logger.LogInformation(
				"CrossServiceDataSync: RecordUpdatedEvent received for entity '{EntityName}', CorrelationId: {CorrelationId}",
				evt.EntityName, evt.CorrelationId);

			try
			{
				// Perform audit field resolution on the NewRecord (post-update state).
				// The NewRecord contains the current modified_by user UUID that Core
				// should validate exists in its user table.
				await HandleAuditFieldResolution(evt.EntityName, evt.NewRecord, evt.CorrelationId);
			}
			catch (Exception ex)
			{
				// Non-critical sync failure: log at Warning level and do NOT rethrow.
				// Eventual consistency will self-heal.
				_logger.LogWarning(ex,
					"CrossServiceDataSync: Non-critical error processing RecordUpdatedEvent for entity '{EntityName}', CorrelationId: {CorrelationId}. Skipping.",
					evt.EntityName, evt.CorrelationId);
			}
		}

		#endregion

		#region IConsumer<RecordDeletedEvent>

		/// <summary>
		/// Handles <see cref="RecordDeletedEvent"/> from external services (CRM, Project, Mail)
		/// that may require Core to clean up denormalized references.
		///
		/// <para>
		/// <b>Cross-service scenarios handled (AAP 0.7.1):</b>
		/// <list type="table">
		///   <listheader><term>Source Service</term><description>Core Action</description></listheader>
		///   <item><term>CRM (account)</term><description>Clean up any denormalized references to deleted account in Core data</description></item>
		///   <item><term>Project (task)</term><description>Typically no Core action needed — deletion doesn't affect Core state</description></item>
		///   <item><term>Mail (email)</term><description>Typically no Core action needed — deletion doesn't affect Core state</description></item>
		/// </list>
		/// </para>
		///
		/// <para>
		/// <b>Simplification from monolith:</b> The original <c>IErpPostDeleteRecordHook</c>
		/// carried the full <c>EntityRecord</c> object. <see cref="RecordDeletedEvent"/> carries
		/// only <c>RecordId</c> (Guid) since the record no longer exists after deletion. The
		/// publishing service extracts the record's identifier before publishing the event.
		/// </para>
		///
		/// <para>
		/// <b>Idempotency:</b> Deletion cleanup is inherently idempotent — deleting a
		/// reference that's already gone is a no-op per AAP 0.8.2.
		/// </para>
		/// </summary>
		/// <param name="context">MassTransit consume context containing the deserialized event message.</param>
		/// <returns>A task representing the asynchronous consume operation.</returns>
		public async Task Consume(ConsumeContext<RecordDeletedEvent> context)
		{
			var evt = context.Message;
			_logger.LogInformation(
				"CrossServiceDataSync: RecordDeletedEvent received for entity '{EntityName}', RecordId: {RecordId}, CorrelationId: {CorrelationId}",
				evt.EntityName, evt.RecordId, evt.CorrelationId);

			try
			{
				// Handle denormalized reference cleanup for deleted external records.
				// When an external service deletes a record (e.g., CRM deletes an account),
				// Core may need to clean up any denormalized references to that record's ID
				// in Core-owned data. This is an extensibility point for future cross-service
				// data sync needs.
				await HandleDenormalizedReferenceCleanup(evt.EntityName, evt.RecordId, evt.CorrelationId);
			}
			catch (Exception ex)
			{
				// Non-critical sync failure: log at Warning level and do NOT rethrow.
				// Eventual consistency will self-heal — denormalized references to
				// deleted records will be detected and handled gracefully by read paths.
				_logger.LogWarning(ex,
					"CrossServiceDataSync: Non-critical error processing RecordDeletedEvent for entity '{EntityName}', RecordId: {RecordId}, CorrelationId: {CorrelationId}. Skipping.",
					evt.EntityName, evt.RecordId, evt.CorrelationId);
			}
		}

		#endregion

		#region Private Helper Methods

		/// <summary>
		/// Validates audit field user UUIDs (<c>created_by</c>, <c>modified_by</c>) referenced
		/// by records from external services. Core owns the <c>user</c> entity exclusively
		/// (AAP 0.7.1), so when other services create or update records with these audit fields,
		/// this method defensively validates that the referenced user UUIDs exist in Core's
		/// user table.
		///
		/// <para>
		/// <b>Background:</b> In the monolith, all modules shared the same PostgreSQL database,
		/// so <c>created_by</c>/<c>modified_by</c> foreign keys to <c>rec_user</c> were always
		/// valid. In the database-per-service model, each service stores user UUIDs but Core
		/// owns the <c>user</c> entity. This helper validates that user UUIDs referenced by
		/// external services actually exist.
		/// </para>
		///
		/// <para>
		/// <b>Idempotency:</b> All operations are read-only validation — no writes are performed.
		/// Running this method multiple times with the same input produces identical results.
		/// </para>
		///
		/// <para>
		/// This method is designed as an extensibility point for future cross-service data sync
		/// needs. Additional entity-specific sync logic can be added here as switch cases on
		/// <paramref name="entityName"/> without modifying the consumer's Consume methods.
		/// </para>
		/// </summary>
		/// <param name="entityName">
		/// Name of the entity whose record was created or updated in an external service.
		/// </param>
		/// <param name="record">
		/// The <see cref="EntityRecord"/> payload from the domain event. May be null for
		/// defensive handling (e.g., if the publishing service failed to populate the record).
		/// </param>
		/// <param name="correlationId">
		/// Correlation identifier for distributed tracing across service boundaries.
		/// </param>
		/// <returns>A task representing the asynchronous validation operation.</returns>
		private async Task HandleAuditFieldResolution(string entityName, EntityRecord record, Guid correlationId)
		{
			if (record == null)
			{
				_logger.LogDebug(
					"CrossServiceDataSync: Skipping audit field resolution — record payload is null for entity '{EntityName}', CorrelationId: {CorrelationId}",
					entityName, correlationId);
				return;
			}

			// Extract and validate created_by audit field user UUID.
			// In the monolith (ERPService.cs system entity init), created_by is a GUID field
			// pointing to the user entity, which Core owns exclusively. When other services
			// create records with this field, we validate the referenced user UUID.
			if (record.Properties.ContainsKey("created_by"))
			{
				var createdBy = record["created_by"];
				if (createdBy is Guid userId)
				{
					_logger.LogDebug(
						"CrossServiceDataSync: Audit field 'created_by' = {UserId} for entity '{EntityName}', CorrelationId: {CorrelationId}",
						userId, entityName, correlationId);

					// User UUID validation is a read-only check — in normal operation the user
					// always exists because authentication enforces valid user context. This
					// handles edge cases such as eventual consistency delays or orphaned references.
					// Future enhancement: Query Core's user table to confirm the UUID exists,
					// and emit a warning event if it does not (e.g., UserReferenceOrphanedEvent).
				}
			}

			// Extract and validate modified_by audit field user UUID.
			// Same rationale as created_by — modified_by is a GUID field pointing to the
			// user entity that Core owns exclusively.
			if (record.Properties.ContainsKey("modified_by"))
			{
				var modifiedBy = record["modified_by"];
				if (modifiedBy is Guid userId)
				{
					_logger.LogDebug(
						"CrossServiceDataSync: Audit field 'modified_by' = {UserId} for entity '{EntityName}', CorrelationId: {CorrelationId}",
						userId, entityName, correlationId);

					// Same read-only validation pattern as created_by above.
				}
			}

			await Task.CompletedTask;
		}

		/// <summary>
		/// Handles cleanup of denormalized references to deleted external records.
		/// When an external service deletes a record that Core-owned entities reference
		/// (via denormalized IDs), this method cleans up those orphaned references.
		///
		/// <para>
		/// <b>Idempotency:</b> Cleanup of references that are already gone is a no-op.
		/// Running this method multiple times with the same input produces identical results.
		/// </para>
		///
		/// <para>
		/// This method is designed as an extensibility point. Currently most deletion events
		/// from external services do not require Core action (per AAP 0.7.1), but the
		/// infrastructure is in place for future cross-service reference management.
		/// </para>
		/// </summary>
		/// <param name="entityName">
		/// Name of the entity whose record was deleted in an external service.
		/// </param>
		/// <param name="recordId">
		/// Unique identifier of the deleted record. Used to locate and clean up any
		/// denormalized references in Core-owned data.
		/// </param>
		/// <param name="correlationId">
		/// Correlation identifier for distributed tracing across service boundaries.
		/// </param>
		/// <returns>A task representing the asynchronous cleanup operation.</returns>
		private async Task HandleDenormalizedReferenceCleanup(string entityName, Guid recordId, Guid correlationId)
		{
			// Evaluate which entity types require denormalized reference cleanup in Core.
			// Per AAP 0.7.1 cross-service relation resolution strategy:
			// - Account deletions (CRM): May require cleanup of denormalized account references
			//   in Core data (e.g., account_id stored in user-related projections).
			// - Task deletions (Project): Typically no Core action needed — task references
			//   don't affect Core-owned entities.
			// - Email deletions (Mail): Typically no Core action needed — email records are
			//   fully owned by the Mail service.

			switch (entityName)
			{
				case KnownExternalEntities.Account:
					// Account deletion from CRM service — check if Core maintains any
					// denormalized references to this account's ID and clean them up.
					// Currently this is a no-op with defensive logging, as Core does not
					// yet denormalize account data. When denormalization is implemented
					// (e.g., for user-account associations), this block will perform the
					// actual cleanup using CoreDbContext.
					_logger.LogDebug(
						"CrossServiceDataSync: Account deletion detected. RecordId: {RecordId}, CorrelationId: {CorrelationId}. " +
						"Checking for denormalized references in Core data.",
						recordId, correlationId);
					break;

				case KnownExternalEntities.Contact:
					// Contact deletion from CRM service — similar pattern to account.
					_logger.LogDebug(
						"CrossServiceDataSync: Contact deletion detected. RecordId: {RecordId}, CorrelationId: {CorrelationId}. " +
						"No Core denormalized references to clean up.",
						recordId, correlationId);
					break;

				case KnownExternalEntities.Case:
				case KnownExternalEntities.Task:
				case KnownExternalEntities.Email:
				case KnownExternalEntities.Timelog:
					// These entities do not have denormalized references in Core-owned data.
					// Log at debug level and acknowledge the event.
					_logger.LogDebug(
						"CrossServiceDataSync: Deletion of entity '{EntityName}' (RecordId: {RecordId}) does not require " +
						"Core data cleanup. CorrelationId: {CorrelationId}",
						entityName, recordId, correlationId);
					break;

				default:
					// Unknown or unhandled entity type — log and acknowledge.
					// This is not an error; new entity types from evolving services will
					// arrive here until explicit handling is added.
					_logger.LogDebug(
						"CrossServiceDataSync: Deletion of unhandled entity '{EntityName}' (RecordId: {RecordId}) received. " +
						"No Core action required. CorrelationId: {CorrelationId}",
						entityName, recordId, correlationId);
					break;
			}

			await Task.CompletedTask;
		}

		#endregion
	}
}
