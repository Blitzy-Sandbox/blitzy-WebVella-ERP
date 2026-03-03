using System;
using System.Linq;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebVella.Erp.Service.Reporting.Database;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Reporting.Events
{
    /// <summary>
    /// MassTransit consumer that processes <see cref="RecordUpdatedEvent"/> events
    /// where <c>EntityName == "project"</c>, published by the Core or Project service
    /// after a project entity record is updated via <c>RecordManager.UpdateRecord()</c>.
    ///
    /// <para>
    /// <b>Replaces:</b> The monolith's synchronous in-process hook execution pattern where
    /// <c>RecordHookManager.ExecutePostUpdateRecordHooks("project", record)</c> would iterate
    /// all <c>IErpPostUpdateRecordHook</c> implementations and call
    /// <c>OnPostUpdateRecord(entityName, record)</c> sequentially (source: RecordHookManager.cs lines 68-76).
    /// </para>
    ///
    /// <para>
    /// <b>Business context:</b> In the monolith, <c>ReportService.GetTimelogData()</c>
    /// (source: ReportService.cs lines 59-61, 77-97) dynamically joins
    /// timelog → task → project → account at query time using EQL:
    /// <c>SELECT id, subject, $project_nn_task.id, $project_nn_task.name, $project_nn_task.account_id, ...</c>
    /// In the microservice architecture, these cross-entity relationships are denormalized
    /// in local projection tables. This consumer maintains the <see cref="ProjectProjection"/>
    /// table and cascades <c>account_id</c> changes to <see cref="TimelogProjection"/> records,
    /// implementing the CQRS (light) pattern per AAP 0.4.3.
    /// </para>
    ///
    /// <para>
    /// <b>Transport:</b> MassTransit via RabbitMQ (local/Docker) or SNS+SQS
    /// (AWS/LocalStack deployment validation per AAP 0.7.4).
    /// Auto-registered by MassTransit via <c>x.AddConsumers(typeof(Program).Assembly)</c> in Program.cs.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotency (AAP 0.8.2):</b> Uses an upsert pattern — check existence, then update
    /// or create. Duplicate event delivery produces the same final state without data corruption.
    /// </para>
    /// </summary>
    public class ProjectUpdatedConsumer : IConsumer<RecordUpdatedEvent>
    {
        private readonly ReportingDbContext _dbContext;
        private readonly ILogger<ProjectUpdatedConsumer> _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="ProjectUpdatedConsumer"/> with required
        /// dependencies injected via the ASP.NET Core DI container.
        /// </summary>
        /// <param name="dbContext">
        /// EF Core database context for the Reporting microservice's dedicated PostgreSQL database.
        /// Used to perform idempotent upserts on <see cref="ProjectProjection"/> and cascade
        /// <c>AccountId</c> changes to <see cref="TimelogProjection"/> records.
        /// </param>
        /// <param name="logger">
        /// Structured logger for microservice observability. Logs event processing start
        /// with <c>CorrelationId</c> and error details for distributed tracing.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="dbContext"/> or <paramref name="logger"/> is null.
        /// </exception>
        public ProjectUpdatedConsumer(ReportingDbContext dbContext, ILogger<ProjectUpdatedConsumer> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Processes a <see cref="RecordUpdatedEvent"/> message from the MassTransit message bus.
        /// Filters for <c>EntityName == "project"</c>, then performs an idempotent upsert on the
        /// local <see cref="ProjectProjection"/> table and cascades <c>AccountId</c> changes to
        /// all related <see cref="TimelogProjection"/> records.
        ///
        /// <para>
        /// <b>Processing steps:</b>
        /// <list type="number">
        ///   <item>Filter by entity name — only process project entity updates</item>
        ///   <item>Extract project fields (id, name, account_id) from <see cref="RecordUpdatedEvent.NewRecord"/></item>
        ///   <item>Upsert <see cref="ProjectProjection"/> — update existing or create new</item>
        ///   <item>Cascade <c>AccountId</c> to all <see cref="TimelogProjection"/> records
        ///         referencing this project (preserves the monolith's dynamic join behavior
        ///         from ReportService.GetTimelogData() source lines 79-96)</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context carrying the <see cref="RecordUpdatedEvent"/> message payload,
        /// correlation metadata, and delivery context for retry/error queue routing.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the asynchronous consume operation.</returns>
        public async Task Consume(ConsumeContext<RecordUpdatedEvent> context)
        {
            var message = context.Message;

            // Filter: only process project entity update events.
            // RecordUpdatedEvent is a generic event for all entity types;
            // this consumer is only interested in project entity updates.
            if (message.EntityName != "project")
            {
                return;
            }

            _logger.LogInformation(
                "Processing project updated event. CorrelationId: {CorrelationId}, EntityName: {EntityName}",
                message.CorrelationId,
                message.EntityName);

            try
            {
                // Extract project data from the updated record.
                // RecordUpdatedEvent.NewRecord is an EntityRecord (Expando-based dynamic type)
                // with string-indexed field access. These fields map to the monolith's
                // $project_nn_task.id, $project_nn_task.name, $project_nn_task.account_id
                // from ReportService.cs line 60.
                var projectId = (Guid)message.NewRecord["id"];
                var name = (string)message.NewRecord["name"];

                // account_id is nullable — a project may not be associated with an account.
                // Safe extraction handles null and type conversion.
                Guid? accountId = null;
                var rawAccountId = message.NewRecord["account_id"];
                if (rawAccountId != null)
                {
                    accountId = (Guid)rawAccountId;
                }

                // Convert DateTimeOffset (event timestamp) to DateTime (projection field type).
                var eventTimestamp = message.Timestamp.UtcDateTime;

                // --- Idempotent upsert on ProjectProjection (AAP 0.8.2) ---
                // Check for an existing projection record. If found, update it;
                // otherwise, create a new one. Duplicate event delivery produces
                // the same final state without data corruption.
                var existingProjection = await _dbContext.ProjectProjections
                    .FirstOrDefaultAsync(p => p.Id == projectId);

                if (existingProjection != null)
                {
                    // Update existing projection with latest project data.
                    existingProjection.Name = name;
                    existingProjection.AccountId = accountId;
                    existingProjection.LastModifiedOn = eventTimestamp;
                }
                else
                {
                    // Create new projection — first time seeing this project in the
                    // Reporting service's local database.
                    var projection = new ProjectProjection
                    {
                        Id = projectId,
                        Name = name,
                        AccountId = accountId,
                        CreatedOn = eventTimestamp,
                        LastModifiedOn = eventTimestamp
                    };
                    _dbContext.ProjectProjections.Add(projection);
                }

                await _dbContext.SaveChangesAsync();

                // --- Cascade AccountId to TimelogProjection records ---
                // When a project's account_id changes, all timelog projections referencing
                // this project must be updated to reflect the new account association.
                // This preserves the monolith's dynamic join behavior where
                // ReportService.GetTimelogData() (source lines 79-96) filters timelogs
                // by account_id through the timelog → task → project → account chain.
                // In the microservice, this relationship is denormalized in TimelogProjection.
                var affectedTimelogs = await _dbContext.TimelogProjections
                    .Where(t => t.ProjectId == projectId)
                    .ToListAsync();

                if (affectedTimelogs.Count > 0)
                {
                    foreach (var timelog in affectedTimelogs)
                    {
                        timelog.AccountId = accountId;
                        timelog.LastModifiedOn = eventTimestamp;
                    }

                    await _dbContext.SaveChangesAsync();

                    _logger.LogInformation(
                        "Cascaded AccountId update to {TimelogCount} timelog projections for ProjectId: {ProjectId}. CorrelationId: {CorrelationId}",
                        affectedTimelogs.Count,
                        projectId,
                        message.CorrelationId);
                }

                _logger.LogInformation(
                    "Successfully processed project updated event. ProjectId: {ProjectId}, CorrelationId: {CorrelationId}",
                    projectId,
                    message.CorrelationId);
            }
            catch (Exception ex)
            {
                // Log the error with structured context for distributed tracing,
                // then re-throw to trigger MassTransit's retry policy and error queue routing.
                _logger.LogError(
                    ex,
                    "Failed to process project updated event. CorrelationId: {CorrelationId}",
                    message.CorrelationId);
                throw;
            }
        }
    }
}
