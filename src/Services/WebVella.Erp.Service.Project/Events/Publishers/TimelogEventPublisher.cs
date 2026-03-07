using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Project.Domain.Services;

namespace WebVella.Erp.Service.Project.Events.Publishers
{
    /// <summary>
    /// MassTransit event consumer that handles pre-create and pre-delete domain events
    /// for the "timelog" entity in the Project microservice.
    ///
    /// <para>
    /// <b>Replaces:</b> <c>WebVella.Erp.Plugins.Project.Hooks.Api.Timelog</c> (28 lines),
    /// which implemented <c>IErpPreCreateRecordHook</c> and <c>IErpPreDeleteRecordHook</c>
    /// with the <c>[HookAttachment("timelog")]</c> attribute for entity-specific routing.
    /// </para>
    ///
    /// <para>
    /// <b>Architecture Migration:</b> The monolith's synchronous hook-based pattern where
    /// <c>RecordHookManager</c> iterated registered hook instances calling
    /// <c>OnPreCreateRecord</c>/<c>OnPreDeleteRecord</c> is now replaced by asynchronous
    /// MassTransit consumer pattern. Events are published to the message bus (RabbitMQ for
    /// local/Docker, SNS+SQS for AWS/LocalStack validation) and this consumer filters by
    /// <c>EntityName == "timelog"</c> to handle only relevant events.
    /// </para>
    ///
    /// <para>
    /// <b>Business Logic Delegation:</b> All business logic is delegated to
    /// <see cref="TimelogService"/> (renamed from <c>TimeLogService</c> in the monolith),
    /// which is injected via constructor DI replacing the monolith's
    /// <c>new TimeLogService()</c> instantiation pattern.
    /// </para>
    ///
    /// <para>
    /// <b>Pre-create logic:</b> Validates the timelog record, detects project-scoped timelogs,
    /// loads related task records, updates task aggregate fields
    /// (<c>x_billable_minutes</c>/<c>x_nonbillable_minutes</c>), nulls <c>timelog_started_on</c>,
    /// and creates an activity feed entry.
    /// </para>
    ///
    /// <para>
    /// <b>Pre-delete logic:</b> Validates the timelog record, loads the persisted timelog and
    /// related task records, reverses the aggregate minute updates on the task (with floor at 0),
    /// and deletes all related feed items.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotency:</b> Pre-create aggregate updates are additive but bounded by actual
    /// timelog data (minutes value is deterministic from the record). Pre-delete aggregate
    /// updates are subtractive with a floor of zero, and feed item cleanup is idempotent
    /// (deleting already-deleted records is a no-op in the underlying RecordManager).
    /// Both operations are safe for MassTransit retry semantics.
    /// </para>
    ///
    /// <para>
    /// <b>IMPORTANT:</b> This consumer implements ONLY pre-hooks (<c>IConsumer&lt;PreRecordCreateEvent&gt;</c>
    /// and <c>IConsumer&lt;PreRecordDeleteEvent&gt;</c>). The original monolith <c>Timelog</c> hook
    /// class did NOT implement any post-hooks (<c>IErpPostCreateRecordHook</c> or
    /// <c>IErpPostDeleteRecordHook</c>), so no post-event consumers are implemented here.
    /// </para>
    /// </summary>
    public class TimelogEventPublisher :
        IConsumer<PreRecordCreateEvent>,
        IConsumer<PreRecordDeleteEvent>
    {
        /// <summary>
        /// Entity name constant matching the source <c>[HookAttachment("timelog")]</c> attribute value.
        /// Used for case-insensitive filtering in both Consume methods.
        /// </summary>
        private const string EntityName = "timelog";

        private readonly IPublishEndpoint _publishEndpoint;
        private readonly TimelogService _timelogService;
        private readonly ILogger<TimelogEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TimelogEventPublisher"/> class
        /// with all required dependencies injected via constructor DI.
        ///
        /// <para>
        /// Replaces the monolith's <c>new TimeLogService()</c> instantiation pattern
        /// found in <c>Timelog.OnPreCreateRecord</c> and <c>Timelog.OnPreDeleteRecord</c>.
        /// </para>
        /// </summary>
        /// <param name="publishEndpoint">MassTransit publish endpoint for potential downstream event publishing.</param>
        /// <param name="timelogService">
        /// Timelog domain service (renamed from <c>TimeLogService</c> to <c>TimelogService</c>
        /// per AAP naming convention). Provides <c>PreCreateApiHookLogic</c> and
        /// <c>PreDeleteApiHookLogic</c> methods containing all preserved business logic.
        /// </param>
        /// <param name="logger">Structured logger for distributed tracing and observability.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any of the required dependencies is null.
        /// </exception>
        public TimelogEventPublisher(
            IPublishEndpoint publishEndpoint,
            TimelogService timelogService,
            ILogger<TimelogEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _timelogService = timelogService ?? throw new ArgumentNullException(nameof(timelogService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Consumes a <see cref="PreRecordCreateEvent"/> from the message bus,
        /// replacing the monolith's <c>IErpPreCreateRecordHook.OnPreCreateRecord</c> invocation.
        ///
        /// <para>
        /// <b>Original code (Timelog.cs line 19):</b>
        /// <code>new TimeLogService().PreCreateApiHookLogic(entityName, record, errors);</code>
        /// </para>
        ///
        /// <para>
        /// <b>Behavior:</b> Filters by <c>EntityName == "timelog"</c>, then delegates to
        /// <see cref="TimelogService.PreCreateApiHookLogic(EntityRecord, List{ErrorModel})"/>
        /// which performs timelog validation, task aggregate minute updates
        /// (<c>x_billable_minutes</c>/<c>x_nonbillable_minutes</c>), timer reset, and
        /// activity feed creation.
        /// </para>
        ///
        /// <para>
        /// <b>Idempotency:</b> Aggregate updates are additive based on the record's minutes
        /// value. Duplicate processing may over-count; however, the pre-operation event
        /// semantic ensures this runs before the record is committed, and MassTransit's
        /// in-memory outbox pattern prevents duplicate delivery under normal operation.
        /// </para>
        /// </summary>
        /// <param name="context">MassTransit consume context wrapping the <see cref="PreRecordCreateEvent"/> message.</param>
        /// <returns>A completed task when event processing finishes.</returns>
        public async Task Consume(ConsumeContext<PreRecordCreateEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, EntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.Record;
                var errors = context.Message.ValidationErrors;

                _timelogService.PreCreateApiHookLogic(record, errors);

                _logger.LogInformation(
                    "Processed timelog pre-create event for record {RecordId}",
                    record?["id"]);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing timelog pre-create event for record {RecordId}",
                    context.Message.Record?["id"]);
                throw; // Re-throw for MassTransit retry/error queue handling
            }
        }

        /// <summary>
        /// Consumes a <see cref="PreRecordDeleteEvent"/> from the message bus,
        /// replacing the monolith's <c>IErpPreDeleteRecordHook.OnPreDeleteRecord</c> invocation.
        ///
        /// <para>
        /// <b>Original code (Timelog.cs line 24):</b>
        /// <code>new TimeLogService().PreDeleteApiHookLogic(entityName, record, errors);</code>
        /// </para>
        ///
        /// <para>
        /// <b>Behavior:</b> Filters by <c>EntityName == "timelog"</c>, then delegates to
        /// <see cref="TimelogService.PreDeleteApiHookLogic(EntityRecord, List{ErrorModel})"/>
        /// which performs timelog validation, inverse task aggregate minute updates
        /// (subtract minutes with floor at 0), and related feed item cleanup.
        /// </para>
        ///
        /// <para>
        /// <b>Idempotency:</b> Inverse aggregate updates are bounded by a floor of zero,
        /// preventing negative values. Feed item deletion is idempotent — deleting
        /// already-deleted records is a no-op in the RecordManager. These characteristics
        /// make the operation safe for MassTransit retry semantics.
        /// </para>
        /// </summary>
        /// <param name="context">MassTransit consume context wrapping the <see cref="PreRecordDeleteEvent"/> message.</param>
        /// <returns>A completed task when event processing finishes.</returns>
        public async Task Consume(ConsumeContext<PreRecordDeleteEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, EntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.Record;
                var errors = context.Message.ValidationErrors;

                _timelogService.PreDeleteApiHookLogic(record, errors);

                _logger.LogInformation(
                    "Processed timelog pre-delete event for record {RecordId}",
                    record?["id"]);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing timelog pre-delete event for record {RecordId}",
                    context.Message.Record?["id"]);
                throw; // Re-throw for MassTransit retry/error queue handling
            }
        }
    }
}
