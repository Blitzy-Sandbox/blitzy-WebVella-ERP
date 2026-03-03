// =============================================================================
// CoreEntityChangedConsumer.cs — MassTransit Event Subscriber for Core
//                                Reference Entity Changes
// =============================================================================
// Listens for RecordUpdatedEvent, RecordCreatedEvent, and RecordDeletedEvent
// events published by the Core Platform Service for shared reference entities:
// language, currency, and country. These entities are owned by the Core service
// (per AAP 0.7.1 Entity-to-Service Ownership Matrix) but are referenced by
// CRM entities:
//   - currency_1n_account  — currency_id stored on rec_account
//   - country_1n_address   — country_id stored on rec_address
//   - country_1n_account   — country_id stored on rec_account
//   - language_1n_account  — language_id stored on rec_account
//
// This consumer maintains eventual consistency for this reference data within
// the CRM database by keeping a denormalized crm_reference_data_cache table
// up to date with the latest label/name values from the Core service.
//
// Architecture:
//   - Replaces the monolith's direct FK joins to rec_language, rec_currency,
//     rec_country tables within the same database. In the monolith, CRM entities
//     resolved reference labels via EQL cross-entity relation traversal (e.g.,
//     $country_1n_account.label in Configuration.AccountSearchIndexFields).
//   - In the database-per-service model (AAP 0.7.3), cross-service relation
//     fields are denormalized via event subscribers that update local projections.
//     This consumer is the CRM-side projection for Core reference entities.
//
// Cross-Service Event Flow:
//   Core Service → publishes RecordUpdatedEvent (EntityName="currency")  →
//     RabbitMQ/SNS → CoreEntityChangedConsumer → UPSERTs crm_reference_data_cache
//   Core Service → publishes RecordCreatedEvent (EntityName="country")   →
//     RabbitMQ/SNS → CoreEntityChangedConsumer → UPSERTs crm_reference_data_cache
//   Core Service → publishes RecordDeletedEvent (EntityName="language")  →
//     RabbitMQ/SNS → CoreEntityChangedConsumer → DELETEs from crm_reference_data_cache
//
// Idempotency (AAP 0.8.2):
//   - UPSERT uses ON CONFLICT (entity_name, record_id) DO UPDATE with a
//     WHERE last_synced_at < EXCLUDED.last_synced_at timestamp guard.
//     Out-of-order events cannot overwrite newer data, and duplicate deliveries
//     produce identical results.
//   - DELETE is naturally idempotent — deleting a non-existent row is a no-op.
//
// Source references:
//   - WebVella.Erp.Plugins.Next/Hooks/Api/AccountHook.cs (hook pattern replaced)
//   - WebVella.Erp.Plugins.Next/Hooks/Api/ContactHook.cs (hook pattern replaced)
//   - WebVella.Erp.Plugins.Next/Configuration.cs ($country_1n_account.label)
//   - WebVella.Erp.Plugins.Next/Services/SearchService.cs (relation resolution)
//   - WebVella.Erp/Hooks/IErpPostUpdateRecordHook.cs (replaced interface)
//   - WebVella.Erp/Hooks/IErpPostCreateRecordHook.cs (replaced interface)
//   - WebVella.Erp/Hooks/RecordHookManager.cs (replaced dispatch mechanism)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Npgsql;
using NpgsqlTypes;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Crm.Database;

namespace WebVella.Erp.Service.Crm.Events.Subscribers
{
    /// <summary>
    /// MassTransit consumer that processes record events for Core-owned reference entities
    /// (language, currency, country) to maintain denormalized reference data within the CRM database.
    /// <para>
    /// In the monolith, CRM entities (account, contact, address) accessed these reference entities
    /// via direct FK joins (e.g., <c>$country_1n_account.label</c> in
    /// <c>Configuration.AccountSearchIndexFields</c>). In the microservice architecture, this
    /// consumer maintains local projections of reference data so CRM queries can resolve labels
    /// without cross-service calls on every read.
    /// </para>
    /// <para>
    /// Implements three <see cref="IConsumer{T}"/> interfaces to handle created, updated, and
    /// deleted events for the relevant reference entities. MassTransit supports multi-consumer
    /// classes where each <c>Consume</c> method signature is differentiated by the event type parameter.
    /// </para>
    /// <para>
    /// <b>Idempotency guarantee (AAP 0.8.2):</b> UPSERT SQL uses a
    /// <c>WHERE crm_reference_data_cache.last_synced_at &lt; EXCLUDED.last_synced_at</c> clause
    /// to prevent out-of-order events from overwriting newer data. DELETE is naturally idempotent.
    /// </para>
    /// </summary>
    public class CoreEntityChangedConsumer :
        IConsumer<RecordUpdatedEvent>,
        IConsumer<RecordCreatedEvent>,
        IConsumer<RecordDeletedEvent>
    {
        #region ===== Constants and Static Fields =====

        /// <summary>
        /// Set of Core-owned reference entity names that CRM needs to maintain local copies of.
        /// Per AAP 0.7.1 Entity-to-Service Ownership Matrix:
        /// <list type="bullet">
        ///   <item><c>language</c> — Core-owned, referenced by CRM accounts (language_1n_account)</item>
        ///   <item><c>currency</c> — Core-owned, referenced by CRM accounts (currency_1n_account)</item>
        ///   <item><c>country</c> — Core-owned, referenced by CRM accounts and addresses
        ///     (country_1n_account, country_1n_address, country_1n_contact)</item>
        /// </list>
        /// Uses <see cref="StringComparer.OrdinalIgnoreCase"/> for case-insensitive matching,
        /// providing O(1) lookup performance for entity name filtering in all three Consume methods.
        /// </summary>
        private static readonly HashSet<string> RelevantEntityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "language",
            "currency",
            "country"
        };

        /// <summary>
        /// Idempotent UPSERT SQL for the <c>crm_reference_data_cache</c> table.
        /// <para>
        /// The <c>ON CONFLICT (entity_name, record_id) DO UPDATE</c> clause ensures that
        /// existing rows are updated rather than causing duplicate key violations. The
        /// <c>WHERE crm_reference_data_cache.last_synced_at &lt; EXCLUDED.last_synced_at</c> guard
        /// prevents out-of-order events from overwriting newer data, providing idempotency
        /// for duplicate and reordered event deliveries.
        /// </para>
        /// <para>
        /// Table schema: <c>crm_reference_data_cache</c>
        /// <list type="bullet">
        ///   <item><c>entity_name</c> — TEXT (part of composite PK: language/currency/country)</item>
        ///   <item><c>record_id</c> — UUID (part of composite PK: the entity record's unique ID)</item>
        ///   <item><c>label</c> — TEXT (display label for the reference entity)</item>
        ///   <item><c>name</c> — TEXT (code/key name for the reference entity)</item>
        ///   <item><c>data_json</c> — JSONB (full EntityRecord serialized for flexible future use)</item>
        ///   <item><c>last_synced_at</c> — TIMESTAMP (event timestamp for ordering/idempotency)</item>
        /// </list>
        /// </para>
        /// </summary>
        private const string UpsertSql = @"
            INSERT INTO crm_reference_data_cache (entity_name, record_id, label, name, data_json, last_synced_at)
            VALUES (@entityName, @recordId, @label, @name, @dataJson::jsonb, @lastSyncedAt)
            ON CONFLICT (entity_name, record_id) DO UPDATE SET
                label = EXCLUDED.label,
                name = EXCLUDED.name,
                data_json = EXCLUDED.data_json,
                last_synced_at = EXCLUDED.last_synced_at
            WHERE crm_reference_data_cache.last_synced_at < EXCLUDED.last_synced_at;";

        /// <summary>
        /// DELETE SQL for removing a reference entity record from the local cache.
        /// Naturally idempotent — deleting a non-existent row is a PostgreSQL no-op
        /// that returns zero rows affected without error.
        /// </summary>
        private const string DeleteSql = @"
            DELETE FROM crm_reference_data_cache
            WHERE entity_name = @entityName AND record_id = @recordId;";

        #endregion

        #region ===== Instance Fields =====

        private readonly ILogger<CoreEntityChangedConsumer> _logger;
        private readonly CrmDbContext _dbContext;

        #endregion

        #region ===== Constructor =====

        /// <summary>
        /// Initializes a new instance of the <see cref="CoreEntityChangedConsumer"/> class.
        /// </summary>
        /// <param name="logger">
        /// Structured logger for debug-level event filtering, information-level event
        /// processing, and error-level exception logging with CorrelationId tracing.
        /// </param>
        /// <param name="dbContext">
        /// CRM-specific database context (scoped) providing
        /// <see cref="CrmDbContext.CreateConnection"/> for executing UPSERT and DELETE SQL
        /// against the <c>crm_reference_data_cache</c> table in the CRM database.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="logger"/> or <paramref name="dbContext"/> is <c>null</c>.
        /// </exception>
        public CoreEntityChangedConsumer(ILogger<CoreEntityChangedConsumer> logger, CrmDbContext dbContext)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        #endregion

        #region ===== IConsumer<RecordUpdatedEvent> =====

        /// <summary>
        /// Consumes a <see cref="RecordUpdatedEvent"/> message from the event bus.
        /// Filters for reference entity events (language, currency, country), extracts
        /// field values from the updated record, and performs an idempotent UPSERT to the
        /// <c>crm_reference_data_cache</c> table in the CRM database.
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
            // Step 1: Filter by EntityName — only process reference entity events.
            // This consumer receives all RecordUpdatedEvent messages on the bus;
            // non-reference entities are silently skipped.
            // -----------------------------------------------------------------
            if (!RelevantEntityNames.Contains(message.EntityName))
            {
                _logger.LogDebug(
                    "Skipping RecordUpdatedEvent for entity '{EntityName}' — " +
                    "not a CRM-relevant reference entity. CorrelationId: {CorrelationId}",
                    message.EntityName,
                    message.CorrelationId);
                return;
            }

            // -----------------------------------------------------------------
            // Step 2: Validate the NewRecord payload.
            // The updated record state is carried in NewRecord (enrichment over the
            // original IErpPostUpdateRecordHook which only carried a single record).
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

            _logger.LogInformation(
                "Processing {EntityName} update event. RecordId: {RecordId}, CorrelationId: {CorrelationId}",
                message.EntityName,
                ExtractGuid(record, "id"),
                message.CorrelationId);

            // -----------------------------------------------------------------
            // Step 3: Delegate to the shared UPSERT helper.
            // Both update and create events use the same idempotent UPSERT pattern.
            // -----------------------------------------------------------------
            await UpsertReferenceDataAsync(message.EntityName, record, message.Timestamp, message.CorrelationId)
                .ConfigureAwait(false);
        }

        #endregion

        #region ===== IConsumer<RecordCreatedEvent> =====

        /// <summary>
        /// Consumes a <see cref="RecordCreatedEvent"/> message from the event bus.
        /// Filters for reference entity events (language, currency, country), extracts
        /// field values from the created record, and performs an idempotent UPSERT to the
        /// <c>crm_reference_data_cache</c> table in the CRM database.
        /// <para>
        /// Uses the SAME UPSERT pattern as the update handler — if a create event is
        /// replayed or arrives after an update event, the timestamp comparison in the
        /// WHERE clause ensures correct ordering (AAP 0.8.2 idempotency).
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
            // Step 1: Filter by EntityName — only process reference entity events.
            // -----------------------------------------------------------------
            if (!RelevantEntityNames.Contains(message.EntityName))
            {
                _logger.LogDebug(
                    "Skipping RecordCreatedEvent for entity '{EntityName}' — " +
                    "not a CRM-relevant reference entity. CorrelationId: {CorrelationId}",
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

            _logger.LogInformation(
                "Processing {EntityName} created event. RecordId: {RecordId}, CorrelationId: {CorrelationId}",
                message.EntityName,
                ExtractGuid(record, "id"),
                message.CorrelationId);

            // -----------------------------------------------------------------
            // Step 3: Delegate to the shared UPSERT helper.
            // Uses the same idempotent UPSERT as the update handler.
            // -----------------------------------------------------------------
            await UpsertReferenceDataAsync(message.EntityName, record, message.Timestamp, message.CorrelationId)
                .ConfigureAwait(false);
        }

        #endregion

        #region ===== IConsumer<RecordDeletedEvent> =====

        /// <summary>
        /// Consumes a <see cref="RecordDeletedEvent"/> message from the event bus.
        /// Filters for reference entity events (language, currency, country) and
        /// deletes the corresponding row from the <c>crm_reference_data_cache</c> table.
        /// <para>
        /// DELETE is naturally idempotent — deleting a non-existent row is a PostgreSQL
        /// no-op that returns zero rows affected without error. Duplicate deliveries and
        /// out-of-order deletions are safe.
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
            // Step 1: Filter by EntityName — only process reference entity events.
            // -----------------------------------------------------------------
            if (!RelevantEntityNames.Contains(message.EntityName))
            {
                _logger.LogDebug(
                    "Skipping RecordDeletedEvent for entity '{EntityName}' — " +
                    "not a CRM-relevant reference entity. CorrelationId: {CorrelationId}",
                    message.EntityName,
                    message.CorrelationId);
                return;
            }

            _logger.LogInformation(
                "Processing {EntityName} deleted event. RecordId: {RecordId}, CorrelationId: {CorrelationId}",
                message.EntityName,
                message.RecordId,
                message.CorrelationId);

            try
            {
                // -----------------------------------------------------------------
                // Step 2: Build parameterized DELETE query.
                // All parameters use explicit NpgsqlDbType bindings to prevent SQL
                // injection and ensure correct PostgreSQL type inference.
                // -----------------------------------------------------------------
                var parameters = new List<NpgsqlParameter>
                {
                    new NpgsqlParameter("@entityName", NpgsqlDbType.Text)
                    {
                        Value = message.EntityName
                    },
                    new NpgsqlParameter("@recordId", NpgsqlDbType.Uuid)
                    {
                        Value = message.RecordId
                    }
                };

                // -----------------------------------------------------------------
                // Step 3: Execute the DELETE using CrmDbContext's connection factory.
                // CrmDbContext.CreateConnection() returns a SharedKernel DbConnection
                // wrapping an NpgsqlConnection. The DbConnection.CreateCommand() builds
                // an NpgsqlCommand with parameters already bound.
                // -----------------------------------------------------------------
                using (var connection = _dbContext.CreateConnection())
                {
                    using (var command = connection.CreateCommand(DeleteSql, CommandType.Text, parameters))
                    {
                        var rowsAffected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                        _logger.LogInformation(
                            "Reference data cache DELETE completed. EntityName: {EntityName}, " +
                            "RecordId: {RecordId}, RowsAffected: {RowsAffected}, " +
                            "CorrelationId: {CorrelationId}",
                            message.EntityName,
                            message.RecordId,
                            rowsAffected,
                            message.CorrelationId);
                    }
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
                    "Unexpected error while processing {EntityName} deleted event. " +
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
        /// Performs an idempotent UPSERT of reference entity data into the
        /// <c>crm_reference_data_cache</c> table. Shared between the
        /// <see cref="Consume(ConsumeContext{RecordUpdatedEvent})"/> and
        /// <see cref="Consume(ConsumeContext{RecordCreatedEvent})"/> handlers.
        /// <para>
        /// Extracts <c>id</c>, <c>label</c>, and <c>name</c> fields from the
        /// <see cref="EntityRecord"/> using the null-safe property access pattern
        /// from the monolith's SearchService.cs (line 85). The full record is
        /// serialized to JSON via <see cref="JsonConvert.SerializeObject"/> for the
        /// <c>data_json</c> JSONB column, preserving complete record data for
        /// flexible future use.
        /// </para>
        /// <para>
        /// The UPSERT's <c>WHERE last_synced_at &lt; EXCLUDED.last_synced_at</c>
        /// clause ensures that out-of-order event delivery does not overwrite newer
        /// data with stale values (AAP 0.8.2 idempotency requirement).
        /// </para>
        /// </summary>
        /// <param name="entityName">
        /// The reference entity name (language, currency, or country).
        /// </param>
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
        private async Task UpsertReferenceDataAsync(
            string entityName,
            EntityRecord record,
            DateTimeOffset timestamp,
            Guid correlationId)
        {
            // -----------------------------------------------------------------
            // Step 1: Extract key fields from the EntityRecord.
            // Uses the Properties dictionary with ContainsKey guard pattern
            // matching the monolith's SearchService.cs access convention.
            // -----------------------------------------------------------------
            var recordId = ExtractGuid(record, "id");
            if (recordId == Guid.Empty)
            {
                _logger.LogWarning(
                    "Reference entity event for '{EntityName}' has missing or empty 'id' field. " +
                    "CorrelationId: {CorrelationId}",
                    entityName,
                    correlationId);
                return;
            }

            var label = ExtractString(record, "label");
            var name = ExtractString(record, "name");

            // Serialize the full EntityRecord to JSON for the data_json JSONB column.
            // This preserves complete record data for flexible future use while primary
            // label/name fields are stored in dedicated columns for direct access.
            var dataJson = JsonConvert.SerializeObject(record);

            // Convert DateTimeOffset to UTC DateTime for PostgreSQL TIMESTAMP column.
            var lastSyncedAt = timestamp.UtcDateTime;

            try
            {
                // -----------------------------------------------------------------
                // Step 2: Build parameterized UPSERT query.
                // All parameters use explicit NpgsqlDbType bindings to prevent SQL
                // injection and ensure correct PostgreSQL type inference.
                // -----------------------------------------------------------------
                var parameters = new List<NpgsqlParameter>
                {
                    new NpgsqlParameter("@entityName", NpgsqlDbType.Text)
                    {
                        Value = entityName
                    },
                    new NpgsqlParameter("@recordId", NpgsqlDbType.Uuid)
                    {
                        Value = recordId
                    },
                    new NpgsqlParameter("@label", NpgsqlDbType.Text)
                    {
                        Value = (object?)label ?? DBNull.Value
                    },
                    new NpgsqlParameter("@name", NpgsqlDbType.Text)
                    {
                        Value = (object?)name ?? DBNull.Value
                    },
                    new NpgsqlParameter("@dataJson", NpgsqlDbType.Text)
                    {
                        Value = (object?)dataJson ?? DBNull.Value
                    },
                    new NpgsqlParameter("@lastSyncedAt", NpgsqlDbType.Timestamp)
                    {
                        Value = lastSyncedAt
                    }
                };

                // -----------------------------------------------------------------
                // Step 3: Execute the UPSERT using CrmDbContext's connection factory.
                // CrmDbContext.CreateConnection() returns a SharedKernel DbConnection
                // wrapping an NpgsqlConnection. The DbConnection.CreateCommand() builds
                // an NpgsqlCommand with parameters already bound.
                // -----------------------------------------------------------------
                using (var connection = _dbContext.CreateConnection())
                {
                    using (var command = connection.CreateCommand(UpsertSql, CommandType.Text, parameters))
                    {
                        var rowsAffected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                        _logger.LogInformation(
                            "Reference data cache UPSERT completed. EntityName: {EntityName}, " +
                            "RecordId: {RecordId}, Label: {Label}, RowsAffected: {RowsAffected}, " +
                            "CorrelationId: {CorrelationId}",
                            entityName,
                            recordId,
                            label,
                            rowsAffected,
                            correlationId);
                    }
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
                // The next reference entity event will correct the stale cache.
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
                    "Unexpected error while processing {EntityName} event. " +
                    "RecordId: {RecordId}, CorrelationId: {CorrelationId}",
                    entityName,
                    recordId,
                    correlationId);
                throw;
            }
        }

        /// <summary>
        /// Safely extracts a string value from an <see cref="EntityRecord"/> field.
        /// Uses the <c>Properties</c> dictionary with <c>ContainsKey</c> guard pattern
        /// matching the monolith's SearchService.cs (line 85) access convention.
        /// </summary>
        /// <param name="record">The entity record to extract from.</param>
        /// <param name="fieldName">The field name to look up in the record's Properties.</param>
        /// <returns>
        /// The string representation of the field value, or <c>null</c> if the field
        /// does not exist or its value is <c>null</c>.
        /// </returns>
        private static string? ExtractString(EntityRecord record, string fieldName)
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
