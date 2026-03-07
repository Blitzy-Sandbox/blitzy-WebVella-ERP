using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Reporting.Database;

namespace WebVella.Erp.Service.Reporting.Events
{
    /// <summary>
    /// MassTransit consumer that processes <see cref="RecordUpdatedEvent"/> events
    /// where <c>EntityName == "task"</c>, published by the Project service after
    /// task records are updated via <c>RecordManager.UpdateRecord("task", ...)</c>.
    /// <para>
    /// Updates the local <see cref="TaskProjection"/> table in the Reporting service's
    /// database to keep task data synchronized for accurate reporting aggregation.
    /// This replaces the monolith's synchronous hook pattern where
    /// <c>RecordHookManager.ExecutePostUpdateRecordHooks("task", record)</c>
    /// (WebVella.Erp/Hooks/RecordHookManager.cs lines 68-76) iterated all
    /// <c>IErpPostUpdateRecordHook</c> implementations in-process, including the
    /// <c>[HookAttachment("task")]</c> class in
    /// <c>WebVella.Erp.Plugins.Project/Hooks/Api/Task.cs</c> (line 26-28) which
    /// delegated to <c>TaskService.PostUpdateApiHookLogic(entityName, record)</c>.
    /// </para>
    /// <para>
    /// This consumer ONLY handles the reporting projection update aspect. The
    /// <c>PostUpdateApiHookLogic</c> business logic (watcher repair, activity feed
    /// creation, etc.) is handled by the Project service's own event processing.
    /// </para>
    /// <para>
    /// <b>Idempotency:</b> Uses an upsert pattern (check existence → update or create)
    /// per AAP 0.8.2. Duplicate event delivery produces the same final state without
    /// data corruption.
    /// </para>
    /// <para>
    /// <b>Auto-registration:</b> Discovered by MassTransit via assembly scanning
    /// configured in <c>Program.cs</c> (e.g., <c>x.AddConsumers(typeof(Program).Assembly)</c>).
    /// </para>
    /// </summary>
    public class TaskUpdatedConsumer : IConsumer<RecordUpdatedEvent>
    {
        private readonly ReportingDbContext _dbContext;
        private readonly ILogger<TaskUpdatedConsumer> _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="TaskUpdatedConsumer"/> with
        /// required dependencies injected via the DI container.
        /// </summary>
        /// <param name="dbContext">
        /// EF Core database context for the Reporting service, providing access to
        /// <see cref="ReportingDbContext.TaskProjections"/> for idempotent upsert
        /// operations on the denormalized task projection table.
        /// </param>
        /// <param name="logger">
        /// Structured logger for diagnostic output during event processing lifecycle:
        /// event receipt, successful upserts, and error details.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="dbContext"/> or <paramref name="logger"/> is null.
        /// </exception>
        public TaskUpdatedConsumer(ReportingDbContext dbContext, ILogger<TaskUpdatedConsumer> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Processes a <see cref="RecordUpdatedEvent"/> message from the MassTransit
        /// message bus. Filters for <c>EntityName == "task"</c> and performs an
        /// idempotent upsert of the <see cref="TaskProjection"/> record.
        /// <para>
        /// Fields extracted from <see cref="RecordUpdatedEvent.NewRecord"/>:
        /// <list type="bullet">
        ///   <item><c>id</c> (Guid) — task record primary key</item>
        ///   <item><c>subject</c> (string) — task subject, maps to <c>rec["task_subject"]</c>
        ///     in the monolith's ReportService.cs line 109</item>
        ///   <item><c>$task_type_1n_task</c> (List&lt;EntityRecord&gt;) — optional relation
        ///     data; when present, <c>[0]["label"]</c> is extracted for
        ///     <see cref="TaskProjection.TaskTypeLabel"/>, mapping to <c>rec["task_type"]</c>
        ///     in ReportService.cs line 111</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context containing the <see cref="RecordUpdatedEvent"/>
        /// message payload, correlation metadata, and delivery context.
        /// </param>
        /// <returns>A task representing the asynchronous consume operation.</returns>
        public async Task Consume(ConsumeContext<RecordUpdatedEvent> context)
        {
            var message = context.Message;

            // Filter: only process task entity updates; ignore all other entity types.
            // RecordUpdatedEvent is a generic event for all entities — filtering is required
            // because MassTransit delivers all RecordUpdatedEvent messages to all consumers.
            if (message.EntityName != "task")
            {
                return;
            }

            _logger.LogInformation(
                "Processing task updated event. CorrelationId: {CorrelationId}, EntityName: {EntityName}",
                message.CorrelationId,
                message.EntityName);

            try
            {
                // Extract task ID — required field on every task record.
                Guid taskId = (Guid)message.NewRecord["id"];

                // Extract subject — the task's display name used in report output.
                // Maps to rec["task_subject"] in monolith's ReportService.cs line 109.
                string subject = (string)message.NewRecord["subject"];

                // Safely extract task type label from the $task_type_1n_task relation.
                // This relation data may not be present in the event payload — the
                // publishing service may only include changed fields. When absent,
                // preserve the existing TaskTypeLabel in the projection.
                //
                // Pattern from monolith's TaskService.SetCalculationFields() lines 66-73:
                //   if (((List<EntityRecord>)taskRecord["$task_type_1n_task"]).Any())
                //   {
                //       var typeRecord = ((List<EntityRecord>)taskRecord["$task_type_1n_task"]).First();
                //       if (typeRecord != null && typeRecord.Properties.ContainsKey("label"))
                //           type = (string)typeRecord["label"];
                //   }
                string taskTypeLabel = null;
                if (message.NewRecord.Properties.ContainsKey("$task_type_1n_task"))
                {
                    var taskTypes = message.NewRecord["$task_type_1n_task"] as List<EntityRecord>;
                    if (taskTypes != null && taskTypes.Count > 0)
                    {
                        var typeRecord = taskTypes[0];
                        if (typeRecord != null && typeRecord.Properties.ContainsKey("label"))
                        {
                            taskTypeLabel = (string)typeRecord["label"];
                        }
                    }
                }

                // Idempotent upsert: check if a TaskProjection already exists for this task.
                // Per AAP 0.8.2, event consumers MUST be idempotent — duplicate event
                // delivery must not cause data corruption.
                var existingProjection = await _dbContext.TaskProjections
                    .FirstOrDefaultAsync(t => t.Id == taskId);

                if (existingProjection != null)
                {
                    // Update existing projection with new field values.
                    existingProjection.Subject = subject;

                    // Only update TaskTypeLabel if relation data was actually included
                    // in the event payload. If null, it means the relation data was not
                    // populated — preserve the existing value to avoid data loss.
                    if (taskTypeLabel != null)
                    {
                        existingProjection.TaskTypeLabel = taskTypeLabel;
                    }

                    // Convert DateTimeOffset → DateTime for the projection's audit field.
                    existingProjection.LastModifiedOn = message.Timestamp.UtcDateTime;
                }
                else
                {
                    // First time seeing this task — create a new projection record.
                    // This can happen when an update event arrives before the create event
                    // (out-of-order delivery), ensuring no data is lost.
                    var projection = new TaskProjection
                    {
                        Id = taskId,
                        Subject = subject,
                        TaskTypeLabel = taskTypeLabel,
                        CreatedOn = message.Timestamp.UtcDateTime,
                        LastModifiedOn = message.Timestamp.UtcDateTime
                    };
                    _dbContext.TaskProjections.Add(projection);
                }

                await _dbContext.SaveChangesAsync();

                _logger.LogInformation(
                    "Task projection upserted successfully. TaskId: {TaskId}, CorrelationId: {CorrelationId}",
                    taskId,
                    message.CorrelationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to process task updated event. CorrelationId: {CorrelationId}",
                    message.CorrelationId);

                // Re-throw to trigger MassTransit retry policy and eventual
                // error queue routing if all retries are exhausted.
                throw;
            }
        }
    }
}
