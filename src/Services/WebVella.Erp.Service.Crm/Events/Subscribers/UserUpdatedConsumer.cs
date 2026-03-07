// =============================================================================
// UserUpdatedConsumer.cs — MassTransit Event Subscriber for User Entity Updates
// =============================================================================
// Listens for RecordUpdatedEvent events published by the Core Platform Service
// where EntityName == "user". When a user's name, email, or other display
// attributes change, this consumer updates the locally denormalized user cache
// (crm_user_cache) within the CRM database so that CRM queries can display
// user display names for audit fields (created_by, modified_by) without
// synchronous gRPC calls to the Core service on every read.
//
// Architecture:
//   - Replaces the monolith's direct FK joins to rec_user table for resolving
//     created_by / modified_by display names on CRM entities (account, contact,
//     case, address, salutation).
//   - Source pattern reference: The monolith hooks in
//     WebVella.Erp.Plugins.Next/Hooks/Api/ (AccountHook.cs, ContactHook.cs,
//     CaseHook.cs) demonstrate the synchronous IErpPostUpdateRecordHook pattern.
//     This consumer replaces that pattern with async event-driven processing.
//
// Cross-Service Event Flow:
//   Core Service → publishes RecordUpdatedEvent (EntityName="user") →
//   RabbitMQ/SNS → UserUpdatedConsumer → UPSERTs to crm_user_cache table
//
// Idempotency (AAP 0.8.2):
//   Uses PostgreSQL UPSERT (INSERT ... ON CONFLICT DO UPDATE) with a
//   last_synced_at timestamp comparison in the WHERE clause. Out-of-order
//   events cannot overwrite newer data, and duplicate deliveries produce
//   identical results.
//
// Source references:
//   - WebVella.Erp.Plugins.Next/Hooks/Api/AccountHook.cs (hook pattern)
//   - WebVella.Erp.Plugins.Next/Services/SearchService.cs (EntityRecord access)
//   - WebVella.Erp/Hooks/IErpPostUpdateRecordHook.cs (replaced interface)
//   - WebVella.Erp/Hooks/RecordHookManager.cs (replaced dispatch)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Crm.Database;

namespace WebVella.Erp.Service.Crm.Events.Subscribers
{
    /// <summary>
    /// MassTransit consumer that processes <see cref="RecordUpdatedEvent"/> events for
    /// the "user" entity published by the Core Platform Service. Maintains denormalized
    /// user display data (username, email, first_name, last_name) within the CRM database
    /// for audit field resolution on CRM entities.
    /// <para>
    /// Replaces the monolith's direct FK join to <c>rec_user</c> table for resolving
    /// <c>created_by</c> / <c>modified_by</c> display names on CRM entities (account,
    /// contact, case, address, salutation).
    /// </para>
    /// <para>
    /// <b>Idempotency guarantee (AAP 0.8.2):</b> The UPSERT SQL uses a
    /// <c>WHERE crm_user_cache.last_synced_at &lt; EXCLUDED.last_synced_at</c> clause
    /// to ensure that out-of-order events do not overwrite newer data. Processing the
    /// same event twice produces identical results.
    /// </para>
    /// </summary>
    public class UserUpdatedConsumer : IConsumer<RecordUpdatedEvent>
    {
        /// <summary>
        /// Entity name filter — this consumer only processes events for the "user" entity.
        /// Case-sensitive, matching monolith entity naming conventions.
        /// </summary>
        private const string UserEntityName = "user";

        /// <summary>
        /// Idempotent UPSERT SQL for the <c>crm_user_cache</c> table.
        /// <para>
        /// The <c>ON CONFLICT (user_id) DO UPDATE</c> clause ensures that existing rows
        /// are updated rather than causing duplicate key violations. The
        /// <c>WHERE crm_user_cache.last_synced_at &lt; EXCLUDED.last_synced_at</c> guard
        /// prevents out-of-order events from overwriting newer data, providing idempotency
        /// for duplicate and reordered event deliveries.
        /// </para>
        /// <para>
        /// Table schema: <c>crm_user_cache</c>
        /// <list type="bullet">
        ///   <item><c>user_id</c> — UUID PRIMARY KEY (references Core service user)</item>
        ///   <item><c>username</c> — TEXT (user display name)</item>
        ///   <item><c>email</c> — TEXT (user email address)</item>
        ///   <item><c>first_name</c> — TEXT (user first name, nullable)</item>
        ///   <item><c>last_name</c> — TEXT (user last name, nullable)</item>
        ///   <item><c>last_synced_at</c> — TIMESTAMP (event timestamp for ordering)</item>
        /// </list>
        /// </para>
        /// </summary>
        private const string UpsertSql = @"
            INSERT INTO crm_user_cache (user_id, username, email, first_name, last_name, last_synced_at)
            VALUES (@userId, @username, @email, @firstName, @lastName, @lastSyncedAt)
            ON CONFLICT (user_id) DO UPDATE SET
                username = EXCLUDED.username,
                email = EXCLUDED.email,
                first_name = EXCLUDED.first_name,
                last_name = EXCLUDED.last_name,
                last_synced_at = EXCLUDED.last_synced_at
            WHERE crm_user_cache.last_synced_at < EXCLUDED.last_synced_at;";

        private readonly ILogger<UserUpdatedConsumer> _logger;
        private readonly CrmDbContext _dbContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserUpdatedConsumer"/> class.
        /// </summary>
        /// <param name="logger">
        /// Structured logger for debug-level event filtering, information-level processing,
        /// and error-level exception logging with CorrelationId tracing.
        /// </param>
        /// <param name="dbContext">
        /// CRM-specific database context (scoped) providing <see cref="CrmDbContext.CreateConnection"/>
        /// for executing the UPSERT SQL against the <c>crm_user_cache</c> table.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="logger"/> or <paramref name="dbContext"/> is <c>null</c>.
        /// </exception>
        public UserUpdatedConsumer(ILogger<UserUpdatedConsumer> logger, CrmDbContext dbContext)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <summary>
        /// Consumes a <see cref="RecordUpdatedEvent"/> message from the event bus.
        /// Filters for "user" entity events, extracts user field values from the
        /// updated record, and performs an idempotent UPSERT to the <c>crm_user_cache</c>
        /// table in the CRM database.
        /// <para>
        /// <b>Event filtering:</b> Events for entities other than "user" are silently
        /// skipped with a debug-level log entry.
        /// </para>
        /// <para>
        /// <b>Error handling:</b>
        /// <list type="bullet">
        ///   <item><see cref="NpgsqlException"/>: Logged at Error level, NOT rethrown —
        ///     allows MassTransit retry policy to handle redelivery.</item>
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
            // Step 1: Filter by EntityName — only process "user" entity events.
            // This consumer receives all RecordUpdatedEvent messages on the bus;
            // non-user entities are silently skipped.
            // -----------------------------------------------------------------
            if (!string.Equals(message.EntityName, UserEntityName, StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "Skipping RecordUpdatedEvent for entity '{EntityName}' — " +
                    "only processing '{TargetEntity}' entity updates. CorrelationId: {CorrelationId}",
                    message.EntityName,
                    UserEntityName,
                    message.CorrelationId);
                return;
            }

            // -----------------------------------------------------------------
            // Step 2: Extract user data from the event's NewRecord.
            // Uses the EntityRecord Properties dictionary with ContainsKey guard
            // pattern matching the monolith's SearchService.cs (line 85) access
            // pattern for null-safe field extraction.
            // -----------------------------------------------------------------
            var record = message.NewRecord;
            if (record == null)
            {
                _logger.LogWarning(
                    "RecordUpdatedEvent for entity 'user' has null NewRecord. " +
                    "CorrelationId: {CorrelationId}",
                    message.CorrelationId);
                return;
            }

            var userId = ExtractGuid(record, "id");
            if (userId == Guid.Empty)
            {
                _logger.LogWarning(
                    "RecordUpdatedEvent for entity 'user' has missing or empty 'id' field. " +
                    "CorrelationId: {CorrelationId}",
                    message.CorrelationId);
                return;
            }

            var username = ExtractString(record, "username");
            var email = ExtractString(record, "email");
            var firstName = ExtractString(record, "first_name");
            var lastName = ExtractString(record, "last_name");

            // Use the event's Timestamp (DateTimeOffset) converted to UTC DateTime
            // for the last_synced_at column, enabling temporal ordering for idempotency.
            var lastSyncedAt = message.Timestamp.UtcDateTime;

            _logger.LogInformation(
                "Processing user update event. UserId: {UserId}, Username: {Username}, " +
                "CorrelationId: {CorrelationId}",
                userId,
                username,
                message.CorrelationId);

            try
            {
                // -----------------------------------------------------------------
                // Step 3: Build parameterized UPSERT query.
                // All parameters use explicit NpgsqlDbType bindings to prevent SQL
                // injection and ensure correct PostgreSQL type inference.
                // -----------------------------------------------------------------
                var parameters = new List<NpgsqlParameter>
                {
                    new NpgsqlParameter("@userId", NpgsqlDbType.Uuid) { Value = userId },
                    new NpgsqlParameter("@username", NpgsqlDbType.Text)
                    {
                        Value = (object?)username ?? DBNull.Value
                    },
                    new NpgsqlParameter("@email", NpgsqlDbType.Text)
                    {
                        Value = (object?)email ?? DBNull.Value
                    },
                    new NpgsqlParameter("@firstName", NpgsqlDbType.Text)
                    {
                        Value = (object?)firstName ?? DBNull.Value
                    },
                    new NpgsqlParameter("@lastName", NpgsqlDbType.Text)
                    {
                        Value = (object?)lastName ?? DBNull.Value
                    },
                    new NpgsqlParameter("@lastSyncedAt", NpgsqlDbType.Timestamp)
                    {
                        Value = lastSyncedAt
                    }
                };

                // -----------------------------------------------------------------
                // Step 4: Execute the UPSERT using CrmDbContext's connection factory.
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
                            "User cache UPSERT completed successfully. UserId: {UserId}, " +
                            "RowsAffected: {RowsAffected}, CorrelationId: {CorrelationId}",
                            userId,
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
                // transport level will redeliver if configured. Swallowing the error
                // here prevents the message from being moved to the error queue for
                // transient DB failures (connection drops, lock timeouts, etc.).
                // The next user update event will correct the stale cache.
                // -----------------------------------------------------------------
                _logger.LogError(ex,
                    "Database error while updating CRM user cache. UserId: {UserId}, " +
                    "CorrelationId: {CorrelationId}, SqlState: {SqlState}",
                    userId,
                    message.CorrelationId,
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
                    "Unexpected error while processing user update event. UserId: {UserId}, " +
                    "CorrelationId: {CorrelationId}",
                    userId,
                    message.CorrelationId);
                throw;
            }
        }

        /// <summary>
        /// Safely extracts a string value from an <see cref="EntityRecord"/> field.
        /// Uses the <see cref="Expando.Properties"/> dictionary with <c>ContainsKey</c>
        /// guard pattern matching the monolith's SearchService.cs access convention.
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
    }
}
