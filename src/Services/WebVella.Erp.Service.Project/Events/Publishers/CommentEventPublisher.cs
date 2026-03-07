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
    /// MassTransit event consumer that handles pre-create and post-create domain events
    /// for the "comment" entity in the Project microservice.
    ///
    /// <para>
    /// <b>Replaces:</b> <c>WebVella.Erp.Plugins.Project.Hooks.Api.Comment</c> (23 lines)
    /// which implemented <c>IErpPreCreateRecordHook</c> and <c>IErpPostCreateRecordHook</c>
    /// with a <c>[HookAttachment("comment")]</c> attribute for entity-specific routing.
    /// </para>
    ///
    /// <para>
    /// <b>Original Hook Behavior:</b>
    /// <list type="bullet">
    ///   <item><c>OnPreCreateRecord</c> → <c>new CommentService().PreCreateApiHookLogic(entityName, record, errors)</c>
    ///   — performs project-comment detection, related task loading, and activity feed creation</item>
    ///   <item><c>OnPostCreateRecord</c> → <c>new CommentService().PostCreateApiHookLogic(entityName, record)</c>
    ///   — auto-adds comment creator as task watcher via many-to-many relation</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Microservice Adaptation:</b>
    /// <list type="number">
    ///   <item>Synchronous in-process hook interfaces replaced by MassTransit <c>IConsumer&lt;T&gt;</c>
    ///   pattern consuming <see cref="PreRecordCreateEvent"/> and <see cref="RecordCreatedEvent"/>
    ///   from the message bus (RabbitMQ for local/Docker, SNS+SQS for AWS/LocalStack).</item>
    ///   <item><c>[HookAttachment("comment")]</c> routing replaced by entity name filtering
    ///   in each <c>Consume</c> method via <c>context.Message.EntityName == "comment"</c>.</item>
    ///   <item><c>new CommentService()</c> direct instantiation replaced by constructor DI injection
    ///   of <see cref="CommentService"/> for proper lifecycle management and testability.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Idempotency:</b> Both consumer methods are designed to be safe for duplicate event
    /// delivery as required by AAP §0.8.2:
    /// <list type="bullet">
    ///   <item><c>PreCreateApiHookLogic</c> is validation-oriented (project-comment detection,
    ///   task loading, feed creation) — pre-hooks using validation errors are inherently
    ///   idempotent since they produce the same side effects on replay.</item>
    ///   <item><c>PostCreateApiHookLogic</c> adds comment creator to task watchers — the relation
    ///   creation checks existence first, making it idempotent (adding an already-existing
    ///   watcher relation is a no-op).</item>
    /// </list>
    /// </para>
    /// </summary>
    public class CommentEventPublisher :
        IConsumer<PreRecordCreateEvent>,
        IConsumer<RecordCreatedEvent>
    {
        /// <summary>
        /// Entity name constant matching the original <c>[HookAttachment("comment")]</c> attribute value.
        /// Used for filtering domain events to only process those relevant to the comment entity.
        /// </summary>
        private const string EntityName = "comment";

        private readonly IPublishEndpoint _publishEndpoint;
        private readonly CommentService _commentService;
        private readonly ILogger<CommentEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommentEventPublisher"/> class with all
        /// required dependencies injected via constructor DI.
        ///
        /// Replaces the monolith's <c>new CommentService()</c> instantiation pattern used in
        /// <c>WebVella.Erp.Plugins.Project.Hooks.Api.Comment</c> with proper DI-managed
        /// lifecycle for the comment domain service.
        /// </summary>
        /// <param name="publishEndpoint">
        /// MassTransit publish endpoint for publishing downstream domain events to the message
        /// bus (RabbitMQ for local/Docker, SNS+SQS for AWS/LocalStack validation).
        /// </param>
        /// <param name="commentService">
        /// Comment domain service injected via DI, replacing the monolith's
        /// <c>new CommentService()</c> instantiation. Provides <c>PreCreateApiHookLogic</c>
        /// and <c>PostCreateApiHookLogic</c> methods preserving all original business logic.
        /// </param>
        /// <param name="logger">
        /// Structured logger for distributed tracing and observability of comment event
        /// processing in the microservice architecture.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any of the required dependencies (<paramref name="publishEndpoint"/>,
        /// <paramref name="commentService"/>, or <paramref name="logger"/>) is null.
        /// </exception>
        public CommentEventPublisher(
            IPublishEndpoint publishEndpoint,
            CommentService commentService,
            ILogger<CommentEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _commentService = commentService ?? throw new ArgumentNullException(nameof(commentService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Consumes a <see cref="PreRecordCreateEvent"/> from the message bus, filtering for
        /// the "comment" entity and delegating to <see cref="CommentService.PreCreateApiHookLogic"/>.
        ///
        /// <para>
        /// <b>Replaces:</b> <c>IErpPreCreateRecordHook.OnPreCreateRecord(string entityName,
        /// EntityRecord record, List&lt;ErrorModel&gt; errors)</c> from the monolith's
        /// <c>Comment</c> hook class.
        /// </para>
        ///
        /// <para>
        /// <b>Business Logic (preserved verbatim):</b>
        /// <list type="bullet">
        ///   <item>Detects if the comment is a project comment via <c>l_scope</c> field
        ///   containing "projects"</item>
        ///   <item>Loads related task records via EQL query on <c>l_related_records</c> field</item>
        ///   <item>Retrieves project ID from the task's <c>$project_nn_task</c> relation</item>
        ///   <item>Collects task watcher IDs from <c>$user_nn_task_watchers</c> relation</item>
        ///   <item>Creates an activity feed item via <see cref="FeedService.Create"/></item>
        /// </list>
        /// </para>
        ///
        /// <para>
        /// <b>Idempotency:</b> This method is inherently idempotent — validation-oriented
        /// pre-hook logic produces the same side effects on replay. Feed creation with the
        /// same ID is a controlled operation.
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context providing access to the <see cref="PreRecordCreateEvent"/>
        /// message payload including <c>EntityName</c>, <c>Record</c>, and <c>ValidationErrors</c>.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task Consume(ConsumeContext<PreRecordCreateEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, EntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.Record;
                var errors = context.Message.ValidationErrors;

                // Delegate to domain service — preserves EXACT original behavior:
                // Original: new CommentService().PreCreateApiHookLogic(entityName, record, errors);
                _commentService.PreCreateApiHookLogic(EntityName, record, errors);

                _logger.LogInformation(
                    "Processed comment pre-create event for record {RecordId}",
                    record?["id"]);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing comment pre-create event for record {RecordId}",
                    context.Message.Record?["id"]);

                // Re-throw for MassTransit retry/error queue handling (AAP §0.8.2)
                throw;
            }
        }

        /// <summary>
        /// Consumes a <see cref="RecordCreatedEvent"/> from the message bus, filtering for
        /// the "comment" entity and delegating to <see cref="CommentService.PostCreateApiHookLogic"/>.
        ///
        /// <para>
        /// <b>Replaces:</b> <c>IErpPostCreateRecordHook.OnPostCreateRecord(string entityName,
        /// EntityRecord record)</c> from the monolith's <c>Comment</c> hook class.
        /// </para>
        ///
        /// <para>
        /// <b>Business Logic (preserved verbatim):</b>
        /// <list type="bullet">
        ///   <item>Extracts the comment creator from <c>created_by</c> field</item>
        ///   <item>Checks if the comment is a project comment via <c>l_scope</c> field</item>
        ///   <item>Loads related tasks from <c>l_related_records</c> field via EQL</item>
        ///   <item>Retrieves the <c>user_nn_task_watchers</c> many-to-many relation</item>
        ///   <item>For each related task, adds the comment creator as a watcher if not
        ///   already present via <c>RecordManager.CreateRelationManyToManyRecord</c></item>
        /// </list>
        /// </para>
        ///
        /// <para>
        /// <b>Idempotency:</b> This method is idempotent — the relation creation logic
        /// checks whether the comment creator is already a watcher before creating the
        /// relation, making duplicate event delivery safe.
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context providing access to the <see cref="RecordCreatedEvent"/>
        /// message payload including <c>EntityName</c> and <c>Record</c>.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task Consume(ConsumeContext<RecordCreatedEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, EntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.Record;

                // Delegate to domain service — preserves EXACT original behavior:
                // Original: new CommentService().PostCreateApiHookLogic(entityName, record);
                _commentService.PostCreateApiHookLogic(EntityName, record);

                _logger.LogInformation(
                    "Processed comment post-create event for record {RecordId}",
                    record?["id"]);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing comment post-create event for record {RecordId}",
                    context.Message.Record?["id"]);

                // Re-throw for MassTransit retry/error queue handling (AAP §0.8.2)
                throw;
            }
        }
    }
}
