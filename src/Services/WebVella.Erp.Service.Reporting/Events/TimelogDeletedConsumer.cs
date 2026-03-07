using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.Service.Reporting.Database;

namespace WebVella.Erp.Service.Reporting.Events
{
    /// <summary>
    /// MassTransit consumer that processes <see cref="RecordDeletedEvent"/> events
    /// where <c>EntityName == "timelog"</c>, published by the Project service after
    /// a timelog record is deleted via <c>RecordManager.DeleteRecord("timelog", recordId)</c>.
    /// <para>
    /// Removes the corresponding <see cref="TimelogProjection"/> record from the Reporting
    /// service's local database to maintain accurate aggregate calculations in
    /// <c>ReportAggregationService.GetTimelogData()</c>. Without this consumer, deleted
    /// timelogs would remain in the projection table, producing inflated billable and
    /// non-billable minute totals in reports.
    /// </para>
    /// <para>
    /// <b>Replaces monolith pattern:</b> In the monolith, <c>RecordHookManager.ExecutePostDeleteRecordHooks</c>
    /// (see <c>WebVella.Erp/Hooks/RecordHookManager.cs</c> lines 91-98) fired synchronously after record
    /// deletion. The monolith's <c>Timelog.cs</c> hook class only implemented <c>IErpPreDeleteRecordHook</c>
    /// for author-only validation (not <c>IErpPostDeleteRecordHook</c>), but the report data was implicitly
    /// consistent because <c>ReportService.GetTimelogData()</c> queried the live <c>rec_timelog</c> table.
    /// In the microservice architecture with database-per-service, the projection table must be explicitly
    /// updated by consuming the post-delete domain event.
    /// </para>
    /// <para>
    /// <b>Idempotency (AAP 0.8.2):</b> If the projection record does not exist (already deleted or never
    /// created), the consumer logs a warning and returns without error. Duplicate event delivery from
    /// MassTransit retry policies will not cause data corruption or exceptions.
    /// </para>
    /// <para>
    /// <b>Error handling:</b> Exceptions during database operations are logged with structured context
    /// (CorrelationId, RecordId) and re-thrown. MassTransit's retry/error queue infrastructure handles
    /// transient failures (e.g., database connectivity issues) by routing failed messages to the error
    /// queue for later reprocessing.
    /// </para>
    /// </summary>
    public class TimelogDeletedConsumer : IConsumer<RecordDeletedEvent>
    {
        private readonly ReportingDbContext _dbContext;
        private readonly ILogger<TimelogDeletedConsumer> _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="TimelogDeletedConsumer"/> with the required
        /// dependencies injected by the DI container. MassTransit auto-registers this consumer
        /// via assembly scanning in the Reporting service's Program.cs configuration.
        /// </summary>
        /// <param name="dbContext">
        /// The Reporting service's EF Core database context providing access to the
        /// <see cref="ReportingDbContext.TimelogProjections"/> DbSet for querying and removing
        /// timelog projection records.
        /// </param>
        /// <param name="logger">
        /// Structured logger for recording event processing lifecycle: successful deletions,
        /// idempotent skips (projection not found), and error conditions with correlation context.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="dbContext"/> or <paramref name="logger"/> is <c>null</c>.
        /// </exception>
        public TimelogDeletedConsumer(ReportingDbContext dbContext, ILogger<TimelogDeletedConsumer> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Processes a <see cref="RecordDeletedEvent"/> message from the MassTransit message bus.
        /// <para>
        /// <b>Processing flow:</b>
        /// <list type="number">
        ///   <item>Extracts the event message from the <see cref="ConsumeContext{T}"/>.</item>
        ///   <item>Filters by <c>EntityName == "timelog"</c>; returns immediately for non-timelog events.</item>
        ///   <item>Queries the <c>TimelogProjections</c> table for a record matching <c>RecordId</c>.</item>
        ///   <item>If not found: logs a warning (idempotent skip) and returns.</item>
        ///   <item>If found: removes the projection and persists via <c>SaveChangesAsync()</c>.</item>
        /// </list>
        /// </para>
        /// <para>
        /// <b>Source context:</b> In the monolith, timelog deletion flows through
        /// <c>TimeLogService.Delete(Guid recordId)</c> which validates author ownership
        /// (lines 62-73 of <c>TimeLogService.cs</c>) and then calls
        /// <c>RecordManager().DeleteRecord("timelog", recordId)</c> (line 75).
        /// The <c>RecordManager.DeleteRecord</c> method internally invokes
        /// <c>RecordHookManager.ExecutePostDeleteRecordHooks(entityName, record)</c>,
        /// which in the microservice architecture translates to publishing a
        /// <see cref="RecordDeletedEvent"/> on the message bus.
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context containing the <see cref="RecordDeletedEvent"/> message
        /// with <c>EntityName</c>, <c>RecordId</c>, and <c>CorrelationId</c> properties.
        /// </param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task Consume(ConsumeContext<RecordDeletedEvent> context)
        {
            var message = context.Message;

            // Filter by entity name — this consumer only handles timelog deletions.
            // RecordDeletedEvent is a generic event published for all entity types;
            // non-timelog events are silently skipped without logging to avoid noise.
            if (message.EntityName != "timelog")
            {
                return;
            }

            _logger.LogInformation(
                "Processing timelog deleted event. CorrelationId: {CorrelationId}, RecordId: {RecordId}",
                message.CorrelationId,
                message.RecordId);

            try
            {
                // Query for the existing timelog projection by the deleted record's ID.
                // Uses FirstOrDefaultAsync to return null if not found (idempotent handling).
                var existingProjection = await _dbContext.TimelogProjections
                    .FirstOrDefaultAsync(t => t.Id == message.RecordId);

                if (existingProjection == null)
                {
                    // Projection not found — either already deleted by a previous delivery
                    // of this event (MassTransit retry), or the timelog was never projected
                    // (e.g., created before the Reporting service started consuming events).
                    // This is safe to ignore per the idempotency requirement (AAP 0.8.2).
                    _logger.LogWarning(
                        "Timelog projection not found for RecordId: {RecordId}. " +
                        "Already deleted or not yet created. CorrelationId: {CorrelationId}",
                        message.RecordId,
                        message.CorrelationId);
                    return;
                }

                // Remove the projection record and persist the change.
                // This ensures ReportAggregationService.GetTimelogData() will no longer
                // include the deleted timelog's minutes in billable/non-billable aggregates.
                _dbContext.TimelogProjections.Remove(existingProjection);
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation(
                    "Timelog projection deleted successfully. RecordId: {RecordId}, CorrelationId: {CorrelationId}",
                    message.RecordId,
                    message.CorrelationId);
            }
            catch (Exception ex)
            {
                // Log the error with full correlation context for distributed tracing,
                // then re-throw so MassTransit can route the message to the error queue
                // or apply its configured retry policy for transient failures.
                _logger.LogError(
                    ex,
                    "Failed to process timelog deleted event. CorrelationId: {CorrelationId}, RecordId: {RecordId}",
                    message.CorrelationId,
                    message.RecordId);
                throw;
            }
        }
    }
}
