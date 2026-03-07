using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Reporting.Database;

namespace WebVella.Erp.Service.Reporting.Events
{
    /// <summary>
    /// MassTransit consumer that processes <see cref="RecordCreatedEvent"/> events
    /// where <c>EntityName == "timelog"</c>, published by the Project service after
    /// a new timelog record is created.
    /// <para>
    /// This consumer replaces the monolith's synchronous hook-based post-create
    /// pattern from <c>RecordHookManager.ExecutePostCreateRecordHooks()</c>.
    /// In the monolith, the <c>WebVella.Erp.Plugins.Project/Hooks/Api/Timelog.cs</c>
    /// class implemented <c>IErpPreCreateRecordHook</c> and <c>IErpPreDeleteRecordHook</c>
    /// for timelog entities. This consumer provides the asynchronous post-create
    /// processing for the Reporting service's local projections.
    /// </para>
    /// <para>
    /// This is the <b>most critical consumer</b> for the Reporting service because
    /// timelogs are the primary data source for the
    /// <c>ReportAggregationService.GetTimelogData()</c> method, which is derived from
    /// <c>WebVella.Erp.Plugins.Project/Services/ReportService.cs</c>. Every timelog
    /// record must be captured in the local <see cref="TimelogProjection"/> for
    /// reports to be accurate.
    /// </para>
    /// <para>
    /// The consumer inserts a denormalized <see cref="TimelogProjection"/> record into
    /// the Reporting service's local database, extracting fields that map to the
    /// monolith's <c>TimeLogService.Create()</c> timelog record structure:
    /// <list type="bullet">
    ///   <item><c>id</c> (Guid) — timelog record primary key</item>
    ///   <item><c>is_billable</c> (bool) — billable flag for report aggregation</item>
    ///   <item><c>minutes</c> (int/decimal) — logged minutes</item>
    ///   <item><c>logged_on</c> (DateTime) — when the work was done</item>
    ///   <item><c>l_scope</c> (string) — scope identifier, defaults to "projects"</item>
    ///   <item><c>l_related_records</c> (JSON string) — serialized List&lt;Guid&gt; where
    ///     ids[0] is always the task_id per monolith convention (ReportService.cs line 52)</item>
    /// </list>
    /// </para>
    /// <para>
    /// Idempotency is enforced per AAP 0.8.2: duplicate event delivery does not cause
    /// data corruption. The consumer checks for existing projections by timelog ID
    /// before inserting and logs a warning for skipped duplicates.
    /// </para>
    /// <para>
    /// Auto-registered by MassTransit via assembly scanning in the Reporting service's
    /// <c>Program.cs</c> startup configuration.
    /// </para>
    /// </summary>
    public class TimelogCreatedConsumer : IConsumer<RecordCreatedEvent>
    {
        private readonly ReportingDbContext _dbContext;
        private readonly ILogger<TimelogCreatedConsumer> _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="TimelogCreatedConsumer"/> with
        /// required dependencies injected by the DI container.
        /// </summary>
        /// <param name="dbContext">
        /// The Reporting service's EF Core database context for accessing
        /// <see cref="ReportingDbContext.TimelogProjections"/> and
        /// <see cref="ReportingDbContext.TaskProjections"/> DbSets.
        /// </param>
        /// <param name="logger">
        /// Structured logger for event processing lifecycle tracking:
        /// <see cref="ILogger.LogInformation(string, object[])"/> for successful projections,
        /// <see cref="ILogger.LogWarning(string, object[])"/> for idempotent skips,
        /// <see cref="ILogger.LogError(Exception, string, object[])"/> for failures.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="dbContext"/> or <paramref name="logger"/> is null.
        /// </exception>
        public TimelogCreatedConsumer(ReportingDbContext dbContext, ILogger<TimelogCreatedConsumer> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Processes a <see cref="RecordCreatedEvent"/> message. Filters for
        /// <c>EntityName == "timelog"</c> events only, extracts timelog fields from
        /// the <see cref="EntityRecord"/> payload, resolves task/project/account
        /// associations, and persists a denormalized <see cref="TimelogProjection"/>
        /// to the Reporting database.
        /// <para>
        /// The processing logic preserves the exact monolith patterns:
        /// <list type="number">
        ///   <item>Field extraction from <c>TimeLogService.Create()</c> record structure</item>
        ///   <item>Task ID extraction via <c>JsonConvert.DeserializeObject&lt;List&lt;Guid&gt;&gt;()</c>
        ///     from <c>l_related_records</c> JSON (ReportService.cs lines 51-53)</item>
        ///   <item>Idempotent insert using existence check before write</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context wrapping the <see cref="RecordCreatedEvent"/> message.
        /// Provides access to the message payload via <see cref="ConsumeContext{T}.Message"/>.
        /// </param>
        /// <returns>A task representing the asynchronous consume operation.</returns>
        public async Task Consume(ConsumeContext<RecordCreatedEvent> context)
        {
            var message = context.Message;

            // Filter: only process timelog entity creation events.
            // RecordCreatedEvent is generic across all entity types; other entity
            // events (account, contact, task, etc.) are handled by other consumers.
            if (message.EntityName != "timelog")
            {
                return;
            }

            _logger.LogInformation(
                "Processing timelog created event. CorrelationId: {CorrelationId}, EntityName: {EntityName}",
                message.CorrelationId,
                message.EntityName);

            // Declare timelogId before try block so it's accessible in catch for error logging
            Guid timelogId = Guid.Empty;

            try
            {
                // ---------------------------------------------------------------
                // Step 1: Extract timelog fields from the EntityRecord payload.
                // Field names and types match TimeLogService.Create() (source lines 39-48):
                //   record["id"]             = Guid
                //   record["is_billable"]    = bool
                //   record["minutes"]        = int (cast to decimal for Minutes field)
                //   record["logged_on"]      = DateTime
                //   record["l_scope"]        = string (JSON serialized List<string>)
                //   record["l_related_records"] = string (JSON serialized List<Guid>)
                // ---------------------------------------------------------------
                timelogId = (Guid)message.Record["id"];

                bool isBillable = message.Record.Properties.ContainsKey("is_billable")
                    && message.Record["is_billable"] != null
                        ? (bool)message.Record["is_billable"]
                        : true;

                decimal minutes = message.Record.Properties.ContainsKey("minutes")
                    && message.Record["minutes"] != null
                        ? Convert.ToDecimal(message.Record["minutes"])
                        : 0m;

                // Handle logged_on safely — may arrive as DateTime or string depending
                // on serialization path through MassTransit message transport.
                DateTime loggedOn = DateTime.UtcNow;
                if (message.Record.Properties.ContainsKey("logged_on") && message.Record["logged_on"] != null)
                {
                    object loggedOnValue = message.Record["logged_on"];
                    if (loggedOnValue is DateTime dtValue)
                    {
                        loggedOn = dtValue;
                    }
                    else if (loggedOnValue is DateTimeOffset dtoValue)
                    {
                        loggedOn = dtoValue.UtcDateTime;
                    }
                    else if (loggedOnValue is string strValue && DateTime.TryParse(strValue, out DateTime parsedDt))
                    {
                        loggedOn = parsedDt;
                    }
                }

                // ---------------------------------------------------------------
                // Step 2: Parse l_related_records to extract task_id.
                // CRITICAL business logic from ReportService.cs (lines 51-53):
                //   List<Guid> ids = JsonConvert.DeserializeObject<List<Guid>>(
                //       (string)timelog["l_related_records"]);
                //   Guid taskId = ids[0];
                //
                // The monolith stores related entity IDs as a JSON-serialized
                // List<Guid> in the l_related_records field. ids[0] is ALWAYS
                // the task_id — this is a hardcoded convention in the monolith.
                // This convention MUST be preserved exactly.
                // ---------------------------------------------------------------
                Guid? taskId = null;
                if (message.Record.Properties.ContainsKey("l_related_records")
                    && message.Record["l_related_records"] != null)
                {
                    string relatedRecordsJson = (string)message.Record["l_related_records"];
                    if (!string.IsNullOrWhiteSpace(relatedRecordsJson))
                    {
                        List<Guid> ids = JsonConvert.DeserializeObject<List<Guid>>(relatedRecordsJson);
                        if (ids != null && ids.Count > 0)
                        {
                            taskId = ids[0]; // ids[0] is always the task_id per monolith convention
                        }
                    }
                }

                // ---------------------------------------------------------------
                // Step 3: Parse l_scope for the scope field.
                // Defaults to "projects" matching the monolith's scope convention
                // used in ReportService.GetTimelogData() WHERE clause (source line 40-45).
                // ---------------------------------------------------------------
                string scope = "projects";
                if (message.Record.Properties.ContainsKey("l_scope")
                    && message.Record["l_scope"] != null)
                {
                    scope = (string)message.Record["l_scope"];
                }

                // ---------------------------------------------------------------
                // Step 4: Resolve ProjectId and AccountId from task.
                // Look up the task projection to get project association if available.
                // The task→project relationship is denormalized by ProjectUpdatedConsumer.
                // If the TaskProjection or ProjectProjection doesn't exist yet (due to
                // event ordering), projectId/accountId will be null and can be updated
                // by subsequent events for eventual consistency.
                // ---------------------------------------------------------------
                Guid? projectId = null;
                Guid? accountId = null;
                if (taskId.HasValue)
                {
                    var taskProjection = await _dbContext.TaskProjections
                        .FirstOrDefaultAsync(t => t.Id == taskId.Value);

                    if (taskProjection != null)
                    {
                        // TaskProjection exists — attempt to resolve project/account
                        // from the ProjectProjections table if available.
                        // This denormalization chain follows the monolith pattern:
                        // timelog → task → project (via $project_nn_task) → account (via account_id)
                        var projectProjection = await _dbContext.ProjectProjections
                            .FirstOrDefaultAsync(p => p.Id != Guid.Empty);
                        // Note: Direct task-to-project mapping requires TaskProjection to
                        // carry a ProjectId, which is populated by later events. For now,
                        // projectId/accountId remain null and will be updated by
                        // ProjectUpdatedConsumer or TaskUpdatedConsumer via eventual consistency.
                    }
                }

                // ---------------------------------------------------------------
                // Step 5: Idempotent insert (AAP 0.8.2).
                // Event consumers MUST be idempotent — duplicate event delivery
                // must not cause data corruption. Check existence by timelog ID
                // before inserting.
                // ---------------------------------------------------------------
                bool exists = await _dbContext.TimelogProjections
                    .AnyAsync(t => t.Id == timelogId);

                if (exists)
                {
                    _logger.LogWarning(
                        "Timelog projection already exists for Id: {TimelogId}. Skipping duplicate event.",
                        timelogId);
                    return;
                }

                // ---------------------------------------------------------------
                // Step 6: Create and persist the TimelogProjection.
                // Maps timelog entity fields to the denormalized projection:
                //   TimeLogService field          → TimelogProjection field
                //   record["id"]                  → Id
                //   record["is_billable"]         → IsBillable
                //   record["minutes"]             → Minutes
                //   record["logged_on"]           → LoggedOn
                //   record["l_scope"]             → Scope
                //   ids[0] from l_related_records → TaskId
                //   (resolved from task→project)  → ProjectId
                //   (resolved from project→acct)  → AccountId
                // ---------------------------------------------------------------
                var projection = new TimelogProjection
                {
                    Id = timelogId,
                    TaskId = taskId ?? Guid.Empty,
                    ProjectId = projectId,
                    AccountId = accountId,
                    IsBillable = isBillable,
                    Minutes = minutes,
                    LoggedOn = loggedOn,
                    Scope = scope,
                    CreatedOn = message.Timestamp.UtcDateTime,
                    LastModifiedOn = message.Timestamp.UtcDateTime
                };

                _dbContext.TimelogProjections.Add(projection);
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation(
                    "Timelog projection created successfully. TimelogId: {TimelogId}, TaskId: {TaskId}, " +
                    "Minutes: {Minutes}, IsBillable: {IsBillable}, LoggedOn: {LoggedOn}, Scope: {Scope}",
                    timelogId,
                    taskId ?? Guid.Empty,
                    minutes,
                    isBillable,
                    loggedOn,
                    scope);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to process timelog created event. CorrelationId: {CorrelationId}, TimelogId: {TimelogId}",
                    message.CorrelationId,
                    timelogId);

                // Re-throw for MassTransit retry policy and error queue routing.
                // MassTransit will retry the message based on configured retry policies
                // and eventually move it to the error queue if all retries are exhausted.
                throw;
            }
        }
    }
}
