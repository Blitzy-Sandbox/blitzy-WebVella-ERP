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
    /// MassTransit event consumer that handles pre/post create and pre/post update domain events
    /// for the "task" entity, replacing the monolith's synchronous hook class
    /// <c>WebVella.Erp.Plugins.Project.Hooks.Api.Task</c>.
    ///
    /// <para><b>Original source (preserved):</b></para>
    /// <para>
    /// The monolith's <c>Task</c> hook class used the <c>[HookAttachment("task")]</c> attribute
    /// to register four synchronous hook interfaces:
    /// <list type="bullet">
    ///   <item><c>IErpPreCreateRecordHook</c> → <c>OnPreCreateRecord</c></item>
    ///   <item><c>IErpPostCreateRecordHook</c> → <c>OnPostCreateRecord</c></item>
    ///   <item><c>IErpPreUpdateRecordHook</c> → <c>OnPreUpdateRecord</c></item>
    ///   <item><c>IErpPostUpdateRecordHook</c> → <c>OnPostUpdateRecord</c></item>
    /// </list>
    /// Each hook method instantiated <c>new TaskService()</c> and delegated to the corresponding
    /// domain method. In the microservice architecture, this class replaces that pattern with
    /// four MassTransit <c>IConsumer&lt;T&gt;</c> interface implementations that consume domain
    /// events published asynchronously via the message bus (RabbitMQ or SNS+SQS).
    /// </para>
    ///
    /// <para><b>Entity filtering:</b></para>
    /// <para>
    /// All four <c>Consume</c> methods filter by <c>EntityName == "task"</c> (case-insensitive)
    /// before processing, matching the original <c>[HookAttachment("task")]</c> routing behavior.
    /// Events for other entities are silently skipped.
    /// </para>
    ///
    /// <para><b>Dependency injection:</b></para>
    /// <para>
    /// The monolith's <c>new TaskService()</c> instantiation pattern is replaced with constructor-injected
    /// <c>TaskService</c> via ASP.NET Core DI, enabling proper lifetime management and testability.
    /// </para>
    ///
    /// <para><b>Idempotency:</b></para>
    /// <para>
    /// All event handler logic delegates to <see cref="TaskService"/> domain methods that are
    /// designed to be idempotent:
    /// <list type="bullet">
    ///   <item>Pre-hooks are validation-oriented (add errors to validation list) — inherently idempotent.</item>
    ///   <item>Post-create hook uses <c>SetCalculationFields</c> (overwrites key field), creates watchers
    ///         (relation creation checks existence), and creates feed items (new GUIDs each time but
    ///         acceptable for activity logging).</item>
    ///   <item>Post-update hook recalculates key fields (overwrites are idempotent) and conditionally
    ///         adds the owner to the watcher list (checks existence first).</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Error handling:</b></para>
    /// <para>
    /// Each <c>Consume</c> method wraps processing in a try/catch block. Errors are logged with
    /// structured logging (including the record ID) and then re-thrown to allow MassTransit's
    /// built-in retry and error queue mechanisms to handle transient failures.
    /// </para>
    /// </summary>
    public class TaskEventPublisher :
        IConsumer<PreRecordCreateEvent>,
        IConsumer<RecordCreatedEvent>,
        IConsumer<PreRecordUpdateEvent>,
        IConsumer<RecordUpdatedEvent>
    {
        /// <summary>
        /// Entity name constant matching the original <c>[HookAttachment("task")]</c> attribute value.
        /// Used for case-insensitive filtering in all four Consume methods.
        /// </summary>
        private const string EntityName = "task";

        private readonly IPublishEndpoint _publishEndpoint;
        private readonly TaskService _taskService;
        private readonly ILogger<TaskEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskEventPublisher"/> class with all
        /// required dependencies injected via constructor DI.
        ///
        /// <para>Replaces the monolith's <c>new TaskService()</c> instantiation pattern with
        /// DI-injected <paramref name="taskService"/> for proper lifecycle management.</para>
        /// </summary>
        /// <param name="publishEndpoint">
        /// MassTransit publish endpoint for potential downstream event publishing.
        /// </param>
        /// <param name="taskService">
        /// Task domain service containing all business logic previously invoked via
        /// <c>new TaskService().MethodName()</c> in the monolith hook class.
        /// </param>
        /// <param name="logger">
        /// Structured logger for observability of task event processing, including
        /// successful operations and error details for MassTransit retry diagnostics.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any of the three dependencies is null.
        /// </exception>
        public TaskEventPublisher(
            IPublishEndpoint publishEndpoint,
            TaskService taskService,
            ILogger<TaskEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Consumes a <see cref="PreRecordCreateEvent"/> for the "task" entity.
        ///
        /// <para><b>Replaces:</b>
        /// <c>IErpPreCreateRecordHook.OnPreCreateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>
        /// </para>
        ///
        /// <para><b>Original delegation:</b>
        /// <c>new TaskService().PreCreateRecordPageHookLogic(entityName, record, errors)</c>
        /// </para>
        ///
        /// <para><b>Business logic preserved:</b>
        /// Validates that the task record has a <c>$project_nn_task.id</c> relation field set
        /// with exactly one project. Adds <see cref="ErrorModel"/> entries to
        /// <see cref="PreRecordCreateEvent.ValidationErrors"/> if validation fails, which
        /// signals the originating service to abort the create operation.
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context containing the <see cref="PreRecordCreateEvent"/> message.
        /// </param>
        /// <returns>A completed task when processing finishes.</returns>
        public async Task Consume(ConsumeContext<PreRecordCreateEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, EntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.Record;
                var errors = context.Message.ValidationErrors;

                _logger.LogInformation(
                    "Processing task pre-create event for record {RecordId}",
                    record?["id"]);

                _taskService.PreCreateRecordPageHookLogic(record, errors);

                _logger.LogInformation(
                    "Successfully processed task pre-create event for record {RecordId}",
                    record?["id"]);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing task pre-create event for record {RecordId}",
                    context.Message.Record?["id"]);
                throw; // Re-throw for MassTransit retry/error queue handling
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Consumes a <see cref="RecordCreatedEvent"/> for the "task" entity.
        ///
        /// <para><b>Replaces:</b>
        /// <c>IErpPostCreateRecordHook.OnPostCreateRecord(string entityName, EntityRecord record)</c>
        /// </para>
        ///
        /// <para><b>Original delegation:</b>
        /// <c>new TaskService().PostCreateApiHookLogic(entityName, record)</c>
        /// </para>
        ///
        /// <para><b>Business logic preserved:</b>
        /// After a task is created:
        /// <list type="number">
        ///   <item>Calculates the task key (e.g., "PROJ-42") via <c>SetCalculationFields</c></item>
        ///   <item>Initializes the watcher list (task owner, creator, project owner)</item>
        ///   <item>Creates <c>user_nn_task_watchers</c> relations for each watcher</item>
        ///   <item>Creates an activity feed item recording the task creation</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context containing the <see cref="RecordCreatedEvent"/> message.
        /// </param>
        /// <returns>A completed task when processing finishes.</returns>
        public async Task Consume(ConsumeContext<RecordCreatedEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, EntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.Record;

                _logger.LogInformation(
                    "Processing task post-create event for record {RecordId}",
                    record?["id"]);

                _taskService.PostCreateApiHookLogic(record);

                _logger.LogInformation(
                    "Successfully processed task post-create event for record {RecordId}",
                    record?["id"]);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing task post-create event for record {RecordId}",
                    context.Message.Record?["id"]);
                throw; // Re-throw for MassTransit retry/error queue handling
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Consumes a <see cref="PreRecordUpdateEvent"/> for the "task" entity.
        ///
        /// <para><b>Replaces:</b>
        /// <c>IErpPreUpdateRecordHook.OnPreUpdateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>
        /// </para>
        ///
        /// <para><b>Original delegation:</b>
        /// <c>new TaskService().PostPreUpdateApiHookLogic(entityName, record, errors)</c>
        /// </para>
        ///
        /// <para><b>CRITICAL — Method name preservation:</b>
        /// The method name <c>PostPreUpdateApiHookLogic</c> is preserved exactly from the monolith
        /// source (not <c>PreUpdateApiHookLogic</c>). The destination <see cref="TaskService"/>
        /// signature is <c>PostPreUpdateApiHookLogic(EntityRecord record, EntityRecord oldRecord, List&lt;ErrorModel&gt; errors)</c>.
        /// The <c>oldRecord</c> parameter is passed as <c>null</c> because <see cref="PreRecordUpdateEvent"/>
        /// does not carry the old record state — the method retrieves it internally via EQL query.
        /// </para>
        ///
        /// <para><b>Business logic preserved:</b>
        /// When a task is about to be updated:
        /// <list type="number">
        ///   <item>Detects project changes by comparing old vs new <c>$project_nn_task.id</c></item>
        ///   <item>If the project changed: removes old relation, creates new relation, updates key,
        ///         and adds new project owner to watcher list</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context containing the <see cref="PreRecordUpdateEvent"/> message.
        /// </param>
        /// <returns>A completed task when processing finishes.</returns>
        public async Task Consume(ConsumeContext<PreRecordUpdateEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, EntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.Record;
                var errors = context.Message.ValidationErrors;

                _logger.LogInformation(
                    "Processing task pre-update event for record {RecordId}",
                    record?["id"]);

                // The destination TaskService.PostPreUpdateApiHookLogic accepts (record, oldRecord, errors).
                // PreRecordUpdateEvent does not carry the old record state — the method retrieves
                // the current DB state internally via EQL query, so null is passed for oldRecord.
                _taskService.PostPreUpdateApiHookLogic(record, null, errors);

                _logger.LogInformation(
                    "Successfully processed task pre-update event for record {RecordId}",
                    record?["id"]);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing task pre-update event for record {RecordId}",
                    context.Message.Record?["id"]);
                throw; // Re-throw for MassTransit retry/error queue handling
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Consumes a <see cref="RecordUpdatedEvent"/> for the "task" entity.
        ///
        /// <para><b>Replaces:</b>
        /// <c>IErpPostUpdateRecordHook.OnPostUpdateRecord(string entityName, EntityRecord record)</c>
        /// </para>
        ///
        /// <para><b>Original delegation:</b>
        /// <c>new TaskService().PostUpdateApiHookLogic(entityName, record)</c>
        /// </para>
        ///
        /// <para><b>NOTE:</b>
        /// The <see cref="RecordUpdatedEvent"/> is enriched over the original hook signature — it
        /// carries both <see cref="RecordUpdatedEvent.OldRecord"/> (pre-update state) and
        /// <see cref="RecordUpdatedEvent.NewRecord"/> (post-update state). The destination
        /// <see cref="TaskService.PostUpdateApiHookLogic(EntityRecord, EntityRecord)"/> accepts
        /// both record and oldRecord parameters. We pass <c>NewRecord</c> as the primary record
        /// (matching original hook behavior) and <c>OldRecord</c> for change detection.
        /// </para>
        ///
        /// <para><b>Business logic preserved:</b>
        /// After a task is updated:
        /// <list type="number">
        ///   <item>Recalculates the task key (e.g., "PROJ-42") via <c>SetCalculationFields</c></item>
        ///   <item>If the owner changed, checks if the new owner is in the watcher list
        ///         and adds them via <c>user_nn_task_watchers</c> relation if not</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context containing the <see cref="RecordUpdatedEvent"/> message.
        /// </param>
        /// <returns>A completed task when processing finishes.</returns>
        public async Task Consume(ConsumeContext<RecordUpdatedEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, EntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                // Use NewRecord (post-update state) as the primary record, matching original
                // hook behavior where OnPostUpdateRecord received the updated record.
                // OldRecord (pre-update state) is passed as the second parameter for
                // change detection in the destination TaskService method.
                var record = context.Message.NewRecord;
                var oldRecord = context.Message.OldRecord;

                _logger.LogInformation(
                    "Processing task post-update event for record {RecordId}",
                    record?["id"]);

                _taskService.PostUpdateApiHookLogic(record, oldRecord);

                _logger.LogInformation(
                    "Successfully processed task post-update event for record {RecordId}",
                    record?["id"]);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing task post-update event for record {RecordId}",
                    context.Message.NewRecord?["id"]);
                throw; // Re-throw for MassTransit retry/error queue handling
            }

            await Task.CompletedTask;
        }
    }
}
