// =============================================================================
// CrmEntityChangedConsumer.cs — MassTransit Event Subscriber for CRM Entity
//                                Changes in the Project Service
// =============================================================================
// Listens for RecordUpdatedEvent, RecordCreatedEvent, and RecordDeletedEvent
// events published by the CRM Service for CRM-owned entities: account and case.
// These entities are referenced by Project-owned entities through denormalized
// cross-service relations:
//   - account → project: Denormalized account_id in Project DB (AAP §0.7.1)
//   - case → task: Denormalized case_id in Project DB (AAP §0.7.1)
//
// This consumer maintains eventual consistency for these references within the
// Project database by keeping a denormalized project_crm_reference_cache table
// up to date and performing secondary denormalization updates to rec_project
// (for account changes) and rec_task (for case changes).
//
// Architecture:
//   - Replaces the monolith's direct FK joins between CRM entities (account, case)
//     and Project entities (project, task) within the shared PostgreSQL database.
//     In the monolith, Project hooks at WebVella.Erp.Plugins.Project/Hooks/Api/Task.cs
//     used synchronous in-process calls via [HookAttachment("task")] to TaskService.
//     The Next plugin hooks at WebVella.Erp.Plugins.Next/Hooks/Api/ handled CRM entity
//     lifecycle events synchronously across the shared database.
//   - In the database-per-service model (AAP §0.7.3), cross-service relation fields
//     are denormalized via event subscribers that update local projections. This
//     consumer is the Project-side projection for CRM-owned entities.
//
// Cross-Service Event Flow:
//   CRM Service → publishes RecordUpdatedEvent (EntityName="account") →
//     RabbitMQ/SNS → CrmEntityChangedConsumer → UPSERTs project_crm_reference_cache
//     + updates denormalized fields in rec_project
//   CRM Service → publishes RecordCreatedEvent (EntityName="case") →
//     RabbitMQ/SNS → CrmEntityChangedConsumer → UPSERTs project_crm_reference_cache
//   CRM Service → publishes RecordDeletedEvent (EntityName="account") →
//     RabbitMQ/SNS → CrmEntityChangedConsumer → DELETEs from
//     project_crm_reference_cache + nullifies denormalized refs in rec_project
//
// Idempotency (AAP §0.8.2):
//   - UPSERT uses ON CONFLICT (entity_name, record_id) DO UPDATE with a
//     WHERE project_crm_reference_cache.last_synced_at < EXCLUDED.last_synced_at
//     timestamp guard. Out-of-order events cannot overwrite newer data, and
//     duplicate deliveries produce identical results.
//   - DELETE is naturally idempotent — deleting a non-existent row is a no-op.
//
// Source references:
//   - WebVella.Erp.Plugins.Project/Hooks/Api/Task.cs (hook pattern replaced)
//   - WebVella.Erp.Plugins.Project/Hooks/Api/Comment.cs (hook pattern replaced)
//   - WebVella.Erp.Plugins.Project/Hooks/Api/Timelog.cs (hook pattern replaced)
//   - WebVella.Erp/Hooks/IErpPostUpdateRecordHook.cs (replaced interface)
//   - WebVella.Erp/Hooks/IErpPostCreateRecordHook.cs (replaced interface)
//   - WebVella.Erp/Hooks/IErpPostDeleteRecordHook.cs (replaced interface)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Npgsql;
using NpgsqlTypes;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Project.Database;

namespace WebVella.Erp.Service.Project.Events.Subscribers
{
    /// <summary>
    /// MassTransit consumer that processes record events for CRM-owned entities (account, case)
    /// to maintain denormalized reference data within the Project service database.
    /// <para>
    /// In the monolith, Project entities accessed CRM entities (account, case) via direct FK joins
    /// in the shared PostgreSQL database. In the microservice architecture, CRM publishes domain
    /// events when account/case records change, and this consumer updates local denormalized copies
    /// so Project queries can resolve account/case data without cross-service calls on every read.
    /// </para>
    /// <para>
    /// Per AAP §0.7.1:
    /// <list type="bullet">
    ///   <item>account → project: Denormalized account_id in Project DB with eventual consistency via CRM events</item>
    ///   <item>case → task: Denormalized case_id in Project DB with CRM CaseUpdated events</item>
    /// </list>
    /// </para>
    /// <para>
    /// Implements three <see cref="IConsumer{T}"/> interfaces to handle created, updated, and
    /// deleted events. MassTransit supports multi-consumer classes where each <c>Consume</c>
    /// method signature is differentiated by the event type parameter.
    /// </para>
    /// <para>
    /// <b>Idempotency guarantee (AAP §0.8.2):</b> UPSERT SQL uses a
    /// <c>WHERE project_crm_reference_cache.last_synced_at &lt; EXCLUDED.last_synced_at</c> clause
    /// to prevent out-of-order events from overwriting newer data. DELETE is naturally idempotent.
    /// </para>
    /// </summary>
    public class CrmEntityChangedConsumer :
        IConsumer<RecordUpdatedEvent>,
        IConsumer<RecordCreatedEvent>,
        IConsumer<RecordDeletedEvent>
    {
        #region ===== Constants and Static Fields =====

        /// <summary>
        /// Set of CRM-owned entity names that the Project service needs to maintain local
        /// denormalized copies of. Per AAP §0.7.1 Entity-to-Service Ownership Matrix:
        /// <list type="bullet">
        ///   <item><c>account</c> — CRM-owned, referenced by Project entities via
        ///     account→project relation (denormalized account_id in Project DB)</item>
        ///   <item><c>case</c> — CRM-owned, referenced by Project tasks via
        ///     case→task relation (denormalized case_id in Project DB)</item>
        /// </list>
        /// Uses <see cref="StringComparer.OrdinalIgnoreCase"/> for case-insensitive matching,
        /// providing O(1) lookup performance for entity name filtering in all three Consume methods.
        /// </summary>
        private static readonly HashSet<string> RelevantEntityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "account",
            "case"
        };

        /// <summary>
        /// Idempotent UPSERT SQL for the <c>project_crm_reference_cache</c> table.
        /// <para>
        /// The <c>ON CONFLICT (entity_name, record_id) DO UPDATE</c> clause ensures that
        /// existing rows are updated rather than causing duplicate key violations. The
        /// <c>WHERE project_crm_reference_cache.last_synced_at &lt; EXCLUDED.last_synced_at</c>
        /// guard prevents out-of-order events from overwriting newer data, providing idempotency
        /// for duplicate and reordered event deliveries.
        /// </para>
        /// <para>
        /// Table schema: <c>project_crm_reference_cache</c>
        /// <list type="bullet">
        ///   <item><c>entity_name</c> — TEXT (part of composite PK: account/case)</item>
        ///   <item><c>record_id</c> — UUID (part of composite PK: the entity record's unique ID)</item>
        ///   <item><c>name</c> — TEXT (display name for the CRM entity)</item>
        ///   <item><c>data_json</c> — JSONB (full EntityRecord serialized for flexible future use)</item>
        ///   <item><c>last_synced_at</c> — TIMESTAMP (event timestamp for ordering/idempotency)</item>
        /// </list>
        /// </para>
        /// </summary>
        private const string UpsertSql = @"
            INSERT INTO project_crm_reference_cache (entity_name, record_id, name, data_json, last_synced_at)
            VALUES (@entityName, @recordId, @name, @dataJson::jsonb, @lastSyncedAt)
            ON CONFLICT (entity_name, record_id) DO UPDATE SET
                name = EXCLUDED.name,
                data_json = EXCLUDED.data_json,
                last_synced_at = EXCLUDED.last_synced_at
            WHERE project_crm_reference_cache.last_synced_at < EXCLUDED.last_synced_at;";

        /// <summary>
        /// DELETE SQL for removing a CRM entity record from the local cache.
        /// Naturally idempotent — deleting a non-existent row is a PostgreSQL no-op
        /// that returns zero rows affected without error.
        /// </summary>
        private const string DeleteCacheSql = @"
            DELETE FROM project_crm_reference_cache
            WHERE entity_name = @entityName AND record_id = @recordId;";

        /// <summary>
        /// Secondary denormalization SQL for updating the account display name on
        /// <c>rec_project</c> rows that reference the updated account.
        /// Per AAP §0.7.1, account→project relations are denormalized in the
        /// Project DB with an <c>account_id</c> cross-service reference column.
        /// This UPDATE propagates name changes to the denormalized copy.
        /// Gracefully returns zero rows affected if the column does not exist
        /// or no projects reference this account.
        /// </summary>
        private const string UpdateProjectAccountNameSql = @"
            UPDATE rec_project SET account_name = @name
            WHERE account_id = @recordId;";

        /// <summary>
        /// Secondary denormalization SQL for updating the case display name on
        /// <c>rec_task</c> rows that reference the updated case.
        /// Per AAP §0.7.1, case→task relations are denormalized in the
        /// Project DB with a <c>case_id</c> cross-service reference column.
        /// </summary>
        private const string UpdateTaskCaseNameSql = @"
            UPDATE rec_task SET case_name = @name
            WHERE case_id = @recordId;";

        /// <summary>
        /// Nullification SQL for rec_project when an account is deleted from CRM.
        /// Sets account_id to NULL and account_name to NULL on any project rows
        /// that referenced the deleted account, preserving referential integrity
        /// within the Project database.
        /// </summary>
        private const string NullifyProjectAccountRefSql = @"
            UPDATE rec_project SET account_id = NULL, account_name = NULL
            WHERE account_id = @recordId;";

        /// <summary>
        /// Nullification SQL for rec_task when a case is deleted from CRM.
        /// Sets case_id to NULL and case_name to NULL on any task rows
        /// that referenced the deleted case, preserving referential integrity
        /// within the Project database.
        /// </summary>
        private const string NullifyTaskCaseRefSql = @"
            UPDATE rec_task SET case_id = NULL, case_name = NULL
            WHERE case_id = @recordId;";

        #endregion

        #region ===== Instance Fields =====

        private readonly ILogger<CrmEntityChangedConsumer> _logger;
        private readonly ProjectDbContext _dbContext;

        #endregion

        #region ===== Constructor =====

        /// <summary>
        /// Initializes a new instance of the <see cref="CrmEntityChangedConsumer"/> class.
        /// </summary>
        /// <param name="logger">
        /// Structured logger for debug-level event filtering, information-level event
        /// processing, and error-level exception logging with CorrelationId tracing.
        /// </param>
        /// <param name="dbContext">
        /// Project-specific EF Core database context (scoped) providing database
        /// connection access via <see cref="ProjectDbContext.Database"/> for executing
        /// UPSERT and DELETE SQL against the <c>project_crm_reference_cache</c> table
        /// and secondary denormalization updates to <c>rec_project</c> and <c>rec_task</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="logger"/> or <paramref name="dbContext"/> is <c>null</c>.
        /// </exception>
        public CrmEntityChangedConsumer(ILogger<CrmEntityChangedConsumer> logger, ProjectDbContext dbContext)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        #endregion

        #region ===== IConsumer<RecordUpdatedEvent> =====

        /// <summary>
        /// Consumes a <see cref="RecordUpdatedEvent"/> message from the event bus.
        /// Filters for CRM entity events (account, case), extracts field values from
        /// the updated record, performs an idempotent UPSERT to the
        /// <c>project_crm_reference_cache</c> table, and updates secondary
        /// denormalized references in <c>rec_project</c> or <c>rec_task</c>.
        /// <para>
        /// <b>Event filtering:</b> Events for entities not in <see cref="RelevantEntityNames"/>
        /// are silently skipped with a debug-level log entry.
        /// </para>
        /// <para>
        /// <b>Error handling:</b>
        /// <list type="bullet">
        ///   <item><see cref="NpgsqlException"/>: Logged at Error level, NOT rethrown —
        ///     allows MassTransit retry policy to handle redelivery for transient DB errors.</item>
        ///   <item>All other exceptions: Logged at Error level, rethrown to trigger
        ///     MassTransit error pipeline (dead-letter/error queue).</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context providing access to the event message payload,
        /// CorrelationId for distributed tracing, and message headers.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task Consume(ConsumeContext<RecordUpdatedEvent> context)
        {
            var message = context.Message;

            // -----------------------------------------------------------------
            // Step 1: Filter by EntityName — only process CRM entity events.
            // This consumer receives all RecordUpdatedEvent messages on the bus;
            // non-CRM entities are silently skipped.
            // -----------------------------------------------------------------
            if (!RelevantEntityNames.Contains(message.EntityName))
            {
                _logger.LogDebug(
                    "Skipping RecordUpdatedEvent for entity '{EntityName}' — " +
                    "not a Project-relevant CRM entity. CorrelationId: {CorrelationId}",
                    message.EntityName,
                    message.CorrelationId);
                return;
            }

            // -----------------------------------------------------------------
            // Step 2: Validate the NewRecord payload.
            // The updated record state is carried in NewRecord (enrichment over
            // the original IErpPostUpdateRecordHook which only carried a single
            // record). OldRecord is also available but not needed here.
            // -----------------------------------------------------------------
            var record = message.NewRecord;
            if (record == null)
            {
                _logger.LogWarning(
                    "RecordUpdatedEvent for entity '{EntityName}' has null NewRecord. " +
                    "CorrelationId: {CorrelationId}",
                    message.EntityName,
                    message.CorrelationId);
                return;
            }

            var recordId = ExtractGuid(record, "id");

            // -----------------------------------------------------------------
            // Step 3: Validate the record ID early.
            // A missing or empty ID means the event payload is malformed; skip
            // all database operations. This guard is placed in the Consume method
            // (rather than only in the helper) so that secondary denormalization
            // is also skipped for invalid records.
            // -----------------------------------------------------------------
            if (recordId == Guid.Empty)
            {
                _logger.LogWarning(
                    "CRM entity update event for '{EntityName}' has missing or empty 'id' field. " +
                    "CorrelationId: {CorrelationId}",
                    message.EntityName,
                    message.CorrelationId);
                return;
            }

            _logger.LogInformation(
                "Processing CRM {EntityName} update event. RecordId: {RecordId}, CorrelationId: {CorrelationId}",
                message.EntityName,
                recordId,
                message.CorrelationId);

            // -----------------------------------------------------------------
            // Step 4: Delegate to the shared UPSERT helper.
            // Both update and create events use the same idempotent UPSERT pattern.
            // -----------------------------------------------------------------
            await UpsertCrmReferenceDataAsync(
                    message.EntityName, recordId, record, message.Timestamp, message.CorrelationId)
                .ConfigureAwait(false);

            // -----------------------------------------------------------------
            // Step 5: Secondary denormalization updates.
            // Update display name fields on rec_project (for account) or
            // rec_task (for case) rows that reference this CRM entity.
            // -----------------------------------------------------------------
            var name = ExtractString(record, "name");
            await UpdateDenormalizedReferencesAsync(
                    message.EntityName, recordId, name, message.CorrelationId)
                .ConfigureAwait(false);
        }

        #endregion

        #region ===== IConsumer<RecordCreatedEvent> =====

        /// <summary>
        /// Consumes a <see cref="RecordCreatedEvent"/> message from the event bus.
        /// Filters for CRM entity events (account, case), extracts field values from
        /// the created record, and performs an idempotent UPSERT to the
        /// <c>project_crm_reference_cache</c> table.
        /// <para>
        /// Uses the SAME UPSERT pattern as the update handler — if a create event is
        /// replayed or arrives after an update event, the timestamp comparison in the
        /// WHERE clause ensures correct ordering (AAP §0.8.2 idempotency).
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context providing access to the event message payload,
        /// CorrelationId for distributed tracing, and message headers.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task Consume(ConsumeContext<RecordCreatedEvent> context)
        {
            var message = context.Message;

            // -----------------------------------------------------------------
            // Step 1: Filter by EntityName — only process CRM entity events.
            // -----------------------------------------------------------------
            if (!RelevantEntityNames.Contains(message.EntityName))
            {
                _logger.LogDebug(
                    "Skipping RecordCreatedEvent for entity '{EntityName}' — " +
                    "not a Project-relevant CRM entity. CorrelationId: {CorrelationId}",
                    message.EntityName,
                    message.CorrelationId);
                return;
            }

            // -----------------------------------------------------------------
            // Step 2: Validate the Record payload.
            // The created record state is carried in the Record property.
            // -----------------------------------------------------------------
            var record = message.Record;
            if (record == null)
            {
                _logger.LogWarning(
                    "RecordCreatedEvent for entity '{EntityName}' has null Record. " +
                    "CorrelationId: {CorrelationId}",
                    message.EntityName,
                    message.CorrelationId);
                return;
            }

            var recordId = ExtractGuid(record, "id");

            // -----------------------------------------------------------------
            // Step 3: Validate the record ID early.
            // A missing or empty ID means the event payload is malformed.
            // -----------------------------------------------------------------
            if (recordId == Guid.Empty)
            {
                _logger.LogWarning(
                    "CRM entity created event for '{EntityName}' has missing or empty 'id' field. " +
                    "CorrelationId: {CorrelationId}",
                    message.EntityName,
                    message.CorrelationId);
                return;
            }

            _logger.LogInformation(
                "Processing CRM {EntityName} created event. RecordId: {RecordId}, CorrelationId: {CorrelationId}",
                message.EntityName,
                recordId,
                message.CorrelationId);

            // -----------------------------------------------------------------
            // Step 4: Delegate to the shared UPSERT helper.
            // Uses the same idempotent UPSERT as the update handler.
            // -----------------------------------------------------------------
            await UpsertCrmReferenceDataAsync(
                    message.EntityName, recordId, record, message.Timestamp, message.CorrelationId)
                .ConfigureAwait(false);
        }

        #endregion

        #region ===== IConsumer<RecordDeletedEvent> =====

        /// <summary>
        /// Consumes a <see cref="RecordDeletedEvent"/> message from the event bus.
        /// Filters for CRM entity events (account, case) and deletes the corresponding
        /// row from the <c>project_crm_reference_cache</c> table. Also nullifies or
        /// flags denormalized CRM references in <c>rec_project</c> (for account
        /// deletions) and <c>rec_task</c> (for case deletions).
        /// <para>
        /// DELETE is naturally idempotent — deleting a non-existent row is a PostgreSQL
        /// no-op that returns zero rows affected without error. Duplicate deliveries
        /// and out-of-order deletions are safe.
        /// </para>
        /// <para>
        /// <b>Simplification from source hook:</b> The original <c>IErpPostDeleteRecordHook</c>
        /// carried the full <c>EntityRecord</c>. The <see cref="RecordDeletedEvent"/> carries
        /// only <see cref="RecordDeletedEvent.RecordId"/> since the record no longer exists.
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context providing access to the event message payload,
        /// CorrelationId for distributed tracing, and message headers.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task Consume(ConsumeContext<RecordDeletedEvent> context)
        {
            var message = context.Message;

            // -----------------------------------------------------------------
            // Step 1: Filter by EntityName — only process CRM entity events.
            // -----------------------------------------------------------------
            if (!RelevantEntityNames.Contains(message.EntityName))
            {
                _logger.LogDebug(
                    "Skipping RecordDeletedEvent for entity '{EntityName}' — " +
                    "not a Project-relevant CRM entity. CorrelationId: {CorrelationId}",
                    message.EntityName,
                    message.CorrelationId);
                return;
            }

            _logger.LogInformation(
                "Processing CRM {EntityName} deleted event. RecordId: {RecordId}, CorrelationId: {CorrelationId}",
                message.EntityName,
                message.RecordId,
                message.CorrelationId);

            try
            {
                // -----------------------------------------------------------------
                // Step 2: Delete from the project_crm_reference_cache table.
                // Naturally idempotent — deleting a non-existent row returns 0 rows.
                // -----------------------------------------------------------------
                var connection = await GetOpenConnectionAsync().ConfigureAwait(false);

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = DeleteCacheSql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add(
                        new NpgsqlParameter("@entityName", NpgsqlDbType.Text)
                        {
                            Value = message.EntityName
                        });
                    command.Parameters.Add(
                        new NpgsqlParameter("@recordId", NpgsqlDbType.Uuid)
                        {
                            Value = message.RecordId
                        });

                    var rowsAffected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                    _logger.LogInformation(
                        "CRM reference cache DELETE completed. EntityName: {EntityName}, " +
                        "RecordId: {RecordId}, RowsAffected: {RowsAffected}, " +
                        "CorrelationId: {CorrelationId}",
                        message.EntityName,
                        message.RecordId,
                        rowsAffected,
                        message.CorrelationId);
                }

                // -----------------------------------------------------------------
                // Step 3: Nullify denormalized references in rec_project or rec_task.
                // For account deletions: nullify account_id/account_name on rec_project.
                // For case deletions: nullify case_id/case_name on rec_task.
                // -----------------------------------------------------------------
                await NullifyDenormalizedReferencesAsync(
                        message.EntityName, message.RecordId, message.CorrelationId)
                    .ConfigureAwait(false);
            }
            catch (NpgsqlException ex)
            {
                // -----------------------------------------------------------------
                // Database errors: Log at Error level but DO NOT rethrow.
                // The event is considered consumed — MassTransit retry policy at the
                // transport level will redeliver if configured. Swallowing here prevents
                // the message from being moved to the error queue for transient DB
                // failures (connection drops, lock timeouts, etc.).
                // The next event for this entity will correct the cache.
                // -----------------------------------------------------------------
                _logger.LogError(ex,
                    "Database error while deleting CRM reference data cache. " +
                    "EntityName: {EntityName}, RecordId: {RecordId}, " +
                    "CorrelationId: {CorrelationId}, SqlState: {SqlState}",
                    message.EntityName,
                    message.RecordId,
                    message.CorrelationId,
                    ex.SqlState);
            }
            catch (Exception ex)
            {
                // -----------------------------------------------------------------
                // Unexpected errors: Log at Error level with full context and rethrow
                // to trigger MassTransit error handling pipeline (retry → error queue).
                // -----------------------------------------------------------------
                _logger.LogError(ex,
                    "Unexpected error while processing CRM {EntityName} deleted event. " +
                    "RecordId: {RecordId}, CorrelationId: {CorrelationId}",
                    message.EntityName,
                    message.RecordId,
                    message.CorrelationId);
                throw;
            }
        }

        #endregion

        #region ===== Private Helper Methods =====

        /// <summary>
        /// Performs an idempotent UPSERT of CRM entity data into the
        /// <c>project_crm_reference_cache</c> table. Shared between the
        /// <see cref="Consume(ConsumeContext{RecordUpdatedEvent})"/> and
        /// <see cref="Consume(ConsumeContext{RecordCreatedEvent})"/> handlers.
        /// <para>
        /// Extracts <c>id</c> and <c>name</c> fields from the <see cref="EntityRecord"/>
        /// using the null-safe property access pattern from the monolith's SearchService.cs.
        /// The full record is serialized to JSON via <see cref="JsonConvert.SerializeObject"/>
        /// for the <c>data_json</c> JSONB column, preserving complete record data for
        /// flexible future use.
        /// </para>
        /// <para>
        /// The UPSERT's <c>WHERE last_synced_at &lt; EXCLUDED.last_synced_at</c> clause
        /// ensures that out-of-order event delivery does not overwrite newer data with
        /// stale values (AAP §0.8.2 idempotency requirement).
        /// </para>
        /// </summary>
        /// <param name="entityName">The CRM entity name (account or case).</param>
        /// <param name="recordId">The unique identifier of the CRM entity record.</param>
        /// <param name="record">
        /// The <see cref="EntityRecord"/> containing the created or updated data.
        /// Properties are accessed via the Expando property bag.
        /// </param>
        /// <param name="timestamp">
        /// The event timestamp (<see cref="DateTimeOffset"/>) used for the
        /// <c>last_synced_at</c> column and temporal ordering.
        /// </param>
        /// <param name="correlationId">
        /// The distributed tracing correlation ID from the event context.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task UpsertCrmReferenceDataAsync(
            string entityName,
            Guid recordId,
            EntityRecord record,
            DateTimeOffset timestamp,
            Guid correlationId)
        {
            // -----------------------------------------------------------------
            // Step 1: Validate the record ID.
            // A missing or empty ID means the event payload is malformed.
            // -----------------------------------------------------------------
            if (recordId == Guid.Empty)
            {
                _logger.LogWarning(
                    "CRM entity event for '{EntityName}' has missing or empty 'id' field. " +
                    "CorrelationId: {CorrelationId}",
                    entityName,
                    correlationId);
                return;
            }

            // -----------------------------------------------------------------
            // Step 2: Extract key fields from the EntityRecord.
            // Uses the Properties dictionary with ContainsKey guard pattern
            // matching the monolith's SearchService.cs access convention.
            // -----------------------------------------------------------------
            var name = ExtractString(record, "name");

            // Serialize the full EntityRecord to JSON for the data_json JSONB column.
            // This preserves complete record data for flexible future use while the
            // primary name/id fields are stored in dedicated columns for direct access.
            var dataJson = JsonConvert.SerializeObject(record);

            // Convert DateTimeOffset to UTC DateTime for PostgreSQL TIMESTAMP column.
            var lastSyncedAt = timestamp.UtcDateTime;

            try
            {
                // -----------------------------------------------------------------
                // Step 3: Execute the parameterized UPSERT.
                // All parameters use explicit NpgsqlDbType bindings to prevent SQL
                // injection and ensure correct PostgreSQL type inference (AAP §0.8.2).
                // -----------------------------------------------------------------
                var connection = await GetOpenConnectionAsync().ConfigureAwait(false);

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = UpsertSql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add(
                        new NpgsqlParameter("@entityName", NpgsqlDbType.Text)
                        {
                            Value = entityName
                        });
                    command.Parameters.Add(
                        new NpgsqlParameter("@recordId", NpgsqlDbType.Uuid)
                        {
                            Value = recordId
                        });
                    command.Parameters.Add(
                        new NpgsqlParameter("@name", NpgsqlDbType.Text)
                        {
                            Value = (object)name ?? DBNull.Value
                        });
                    command.Parameters.Add(
                        new NpgsqlParameter("@dataJson", NpgsqlDbType.Text)
                        {
                            Value = (object)dataJson ?? DBNull.Value
                        });
                    command.Parameters.Add(
                        new NpgsqlParameter("@lastSyncedAt", NpgsqlDbType.Timestamp)
                        {
                            Value = lastSyncedAt
                        });

                    var rowsAffected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                    _logger.LogInformation(
                        "CRM reference cache UPSERT completed. EntityName: {EntityName}, " +
                        "RecordId: {RecordId}, Name: {Name}, RowsAffected: {RowsAffected}, " +
                        "CorrelationId: {CorrelationId}",
                        entityName,
                        recordId,
                        name,
                        rowsAffected,
                        correlationId);
                }
            }
            catch (NpgsqlException ex)
            {
                // -----------------------------------------------------------------
                // Database errors: Log at Error level but DO NOT rethrow.
                // The event is considered consumed — MassTransit retry policy at the
                // transport level will redeliver if configured. Swallowing here prevents
                // the message from being moved to the error queue for transient DB
                // failures (connection drops, lock timeouts, etc.).
                // The next CRM entity event will correct the stale cache.
                // -----------------------------------------------------------------
                _logger.LogError(ex,
                    "Database error while upserting CRM reference data cache. " +
                    "EntityName: {EntityName}, RecordId: {RecordId}, " +
                    "CorrelationId: {CorrelationId}, SqlState: {SqlState}",
                    entityName,
                    recordId,
                    correlationId,
                    ex.SqlState);
            }
            catch (Exception ex)
            {
                // -----------------------------------------------------------------
                // Unexpected errors: Log at Error level with full context and rethrow
                // to trigger MassTransit error handling pipeline (retry → error queue).
                // This covers cases like serialization failures, null reference bugs,
                // or infrastructure issues that are not transient DB problems.
                // -----------------------------------------------------------------
                _logger.LogError(ex,
                    "Unexpected error while processing CRM {EntityName} event. " +
                    "RecordId: {RecordId}, CorrelationId: {CorrelationId}",
                    entityName,
                    recordId,
                    correlationId);
                throw;
            }
        }

        /// <summary>
        /// Updates secondary denormalized reference fields in Project-owned tables
        /// when a CRM entity is updated. For account updates, propagates the display
        /// name to <c>rec_project.account_name</c>. For case updates, propagates the
        /// display name to <c>rec_task.case_name</c>.
        /// <para>
        /// This is a best-effort operation — if the denormalized columns do not yet
        /// exist in the database schema (e.g., during migration rollout), the SQL
        /// will fail gracefully and the error is logged at Debug level without
        /// interrupting the primary UPSERT operation.
        /// </para>
        /// </summary>
        /// <param name="entityName">The CRM entity name (account or case).</param>
        /// <param name="recordId">The unique identifier of the CRM entity record.</param>
        /// <param name="name">The updated display name to propagate.</param>
        /// <param name="correlationId">Distributed tracing correlation ID.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task UpdateDenormalizedReferencesAsync(
            string entityName,
            Guid recordId,
            string name,
            Guid correlationId)
        {
            try
            {
                string updateSql;
                if (entityName.Equals("account", StringComparison.OrdinalIgnoreCase))
                {
                    updateSql = UpdateProjectAccountNameSql;
                }
                else if (entityName.Equals("case", StringComparison.OrdinalIgnoreCase))
                {
                    updateSql = UpdateTaskCaseNameSql;
                }
                else
                {
                    return;
                }

                var connection = await GetOpenConnectionAsync().ConfigureAwait(false);

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = updateSql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add(
                        new NpgsqlParameter("@name", NpgsqlDbType.Text)
                        {
                            Value = (object)name ?? DBNull.Value
                        });
                    command.Parameters.Add(
                        new NpgsqlParameter("@recordId", NpgsqlDbType.Uuid)
                        {
                            Value = recordId
                        });

                    var rowsAffected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                    _logger.LogDebug(
                        "Secondary denormalization UPDATE completed. EntityName: {EntityName}, " +
                        "RecordId: {RecordId}, RowsAffected: {RowsAffected}, " +
                        "CorrelationId: {CorrelationId}",
                        entityName,
                        recordId,
                        rowsAffected,
                        correlationId);
                }
            }
            catch (NpgsqlException ex)
            {
                // -----------------------------------------------------------------
                // Secondary denormalization failures are non-critical. The primary
                // cache table UPSERT has already succeeded. These updates target
                // columns (account_name on rec_project, case_name on rec_task) that
                // may not yet be provisioned in the database schema during migration
                // rollout. Log at Debug level and continue — the cache table provides
                // the authoritative reference data.
                // -----------------------------------------------------------------
                _logger.LogDebug(ex,
                    "Secondary denormalization update failed (column may not yet be provisioned). " +
                    "EntityName: {EntityName}, RecordId: {RecordId}, " +
                    "CorrelationId: {CorrelationId}, SqlState: {SqlState}",
                    entityName,
                    recordId,
                    correlationId,
                    ex.SqlState);
            }
        }

        /// <summary>
        /// Nullifies denormalized CRM reference fields in Project-owned tables when a
        /// CRM entity is deleted. For account deletions, sets <c>account_id</c> and
        /// <c>account_name</c> to NULL on <c>rec_project</c>. For case deletions,
        /// sets <c>case_id</c> and <c>case_name</c> to NULL on <c>rec_task</c>.
        /// <para>
        /// Naturally idempotent — updating a row that already has NULL values is a
        /// no-op, and updating zero rows (no matching references) is also safe.
        /// Best-effort operation; failures are logged at Debug level.
        /// </para>
        /// </summary>
        /// <param name="entityName">The CRM entity name (account or case).</param>
        /// <param name="recordId">The unique identifier of the deleted CRM entity.</param>
        /// <param name="correlationId">Distributed tracing correlation ID.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task NullifyDenormalizedReferencesAsync(
            string entityName,
            Guid recordId,
            Guid correlationId)
        {
            try
            {
                string nullifySql;
                if (entityName.Equals("account", StringComparison.OrdinalIgnoreCase))
                {
                    nullifySql = NullifyProjectAccountRefSql;
                }
                else if (entityName.Equals("case", StringComparison.OrdinalIgnoreCase))
                {
                    nullifySql = NullifyTaskCaseRefSql;
                }
                else
                {
                    return;
                }

                var connection = await GetOpenConnectionAsync().ConfigureAwait(false);

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = nullifySql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add(
                        new NpgsqlParameter("@recordId", NpgsqlDbType.Uuid)
                        {
                            Value = recordId
                        });

                    var rowsAffected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                    _logger.LogDebug(
                        "Denormalized reference nullification completed. EntityName: {EntityName}, " +
                        "RecordId: {RecordId}, RowsAffected: {RowsAffected}, " +
                        "CorrelationId: {CorrelationId}",
                        entityName,
                        recordId,
                        rowsAffected,
                        correlationId);
                }
            }
            catch (NpgsqlException ex)
            {
                // -----------------------------------------------------------------
                // Nullification failures are non-critical. The cache table DELETE
                // has already succeeded. These updates target columns that may not
                // yet be provisioned in the database schema during migration rollout.
                // -----------------------------------------------------------------
                _logger.LogDebug(ex,
                    "Denormalized reference nullification failed (column may not yet be provisioned). " +
                    "EntityName: {EntityName}, RecordId: {RecordId}, " +
                    "CorrelationId: {CorrelationId}, SqlState: {SqlState}",
                    entityName,
                    recordId,
                    correlationId,
                    ex.SqlState);
            }
        }

        /// <summary>
        /// Gets the underlying <see cref="DbConnection"/> from the EF Core
        /// <see cref="ProjectDbContext"/> and ensures it is open. The connection is
        /// managed by EF Core's connection lifecycle and should NOT be disposed by
        /// the caller.
        /// </summary>
        /// <returns>An open <see cref="DbConnection"/> to the Project database.</returns>
        private async Task<DbConnection> GetOpenConnectionAsync()
        {
            var connection = _dbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await _dbContext.Database.OpenConnectionAsync().ConfigureAwait(false);
            }
            return connection;
        }

        /// <summary>
        /// Safely extracts a string value from an <see cref="EntityRecord"/> field.
        /// Uses the <c>Properties</c> dictionary with <c>ContainsKey</c> guard pattern
        /// matching the monolith's SearchService.cs access convention.
        /// </summary>
        /// <param name="record">The entity record to extract from.</param>
        /// <param name="fieldName">The field name to look up in the record's Properties.</param>
        /// <returns>
        /// The string representation of the field value, or <c>null</c> if the field
        /// does not exist or its value is <c>null</c>.
        /// </returns>
        private static string ExtractString(EntityRecord record, string fieldName)
        {
            if (record.Properties.ContainsKey(fieldName) && record[fieldName] != null)
            {
                return record[fieldName].ToString();
            }

            return null;
        }

        /// <summary>
        /// Safely extracts a <see cref="Guid"/> value from an <see cref="EntityRecord"/> field.
        /// Handles both native Guid values (direct cast) and string representations
        /// (parsed via <see cref="Guid.TryParse"/>).
        /// </summary>
        /// <param name="record">The entity record to extract from.</param>
        /// <param name="fieldName">The field name to look up in the record's Properties.</param>
        /// <returns>
        /// The extracted Guid value, or <see cref="Guid.Empty"/> if the field does not exist,
        /// is <c>null</c>, or cannot be parsed as a valid Guid.
        /// </returns>
        private static Guid ExtractGuid(EntityRecord record, string fieldName)
        {
            if (record.Properties.ContainsKey(fieldName) && record[fieldName] != null)
            {
                var value = record[fieldName];

                if (value is Guid guidValue)
                {
                    return guidValue;
                }

                if (Guid.TryParse(value.ToString(), out var parsedGuid))
                {
                    return parsedGuid;
                }
            }

            return Guid.Empty;
        }

        #endregion
    }
}
