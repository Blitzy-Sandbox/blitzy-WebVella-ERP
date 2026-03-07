using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Crm.Domain.Services;

namespace WebVella.Erp.Service.Crm.Events.Publishers
{
    /// <summary>
    /// MassTransit event consumer that handles post-create and post-update domain events
    /// for the "case" entity, triggering CRM x_search field regeneration and enabling
    /// downstream event-driven communication across microservices.
    ///
    /// <para>
    /// <b>Replaces:</b> <c>WebVella.Erp.Plugins.Next.Hooks.Api.CaseHook</c> from the monolith,
    /// which used the <c>[HookAttachment("case", int.MinValue)]</c> attribute and implemented
    /// <c>IErpPostCreateRecordHook</c> and <c>IErpPostUpdateRecordHook</c> synchronous interfaces.
    /// In the monolith, CaseHook was discovered by <c>RecordHookManager</c> via assembly scanning
    /// and executed synchronously in-process after each case record create/update operation.
    /// </para>
    ///
    /// <para>
    /// <b>Microservice Architecture:</b> This consumer is registered with MassTransit's DI
    /// configuration and receives <see cref="RecordCreatedEvent"/> and <see cref="RecordUpdatedEvent"/>
    /// messages published by the Core Platform service's <c>RecordManager</c> via the message broker
    /// (RabbitMQ for local/Docker, SNS+SQS for AWS/LocalStack). The consumer filters events by
    /// <c>EntityName == "case"</c> (case-insensitive) and delegates x_search regeneration to the
    /// injected <see cref="SearchService"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Business Logic Preservation (AAP §0.8.1 — Zero Tolerance):</b>
    /// <list type="bullet">
    ///   <item>On case create: <c>SearchService.RegenSearchField("case", record, SearchIndexFields)</c> — identical to monolith</item>
    ///   <item>On case update: <c>SearchService.RegenSearchField("case", newRecord, SearchIndexFields)</c> — identical to monolith</item>
    ///   <item>SearchIndexFields (7 items) are character-for-character identical to <c>Configuration.CaseSearchIndexFields</c></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Idempotency (AAP §0.8.2):</b> Duplicate event delivery is safe because
    /// <see cref="SearchService.RegenSearchField"/> overwrites the <c>x_search</c> column with a
    /// deterministic computed value based on the record's current indexed field values. Replaying
    /// the same event produces the same x_search text, so no deduplication logic is required.
    /// </para>
    ///
    /// <para>
    /// <b>Error Handling:</b> Both <see cref="Consume(ConsumeContext{RecordCreatedEvent})"/> and
    /// <see cref="Consume(ConsumeContext{RecordUpdatedEvent})"/> wrap processing in try/catch blocks
    /// that log the error and re-throw for MassTransit's retry/error queue mechanism. This ensures
    /// transient failures (e.g., database connectivity) are retried per the configured retry policy,
    /// and permanent failures are moved to the error queue for manual investigation.
    /// </para>
    /// </summary>
    public class CaseEventPublisher : IConsumer<RecordCreatedEvent>, IConsumer<RecordUpdatedEvent>
    {
        /// <summary>
        /// The entity name that this consumer filters on. Only events where
        /// <c>context.Message.EntityName</c> matches this value (case-insensitive)
        /// are processed. Maps to the monolith's <c>[HookAttachment("case", int.MinValue)]</c>.
        /// </summary>
        private const string EntityName = "case";

        /// <summary>
        /// The search index field definitions for the "case" entity. These field names are
        /// passed to <see cref="SearchService.RegenSearchField"/> to determine which fields
        /// are included in the computed <c>x_search</c> text.
        ///
        /// <para>
        /// Character-for-character identical to <c>Configuration.CaseSearchIndexFields</c>
        /// from the monolith source (<c>WebVella.Erp.Plugins.Next/Configuration.cs</c> lines 13-15).
        /// </para>
        ///
        /// <para>
        /// Field definitions (7 total):
        /// <list type="bullet">
        ///   <item><c>$account_nn_case.name</c> — Relation-qualified: account name via N:N relation</item>
        ///   <item><c>description</c> — Direct field: case description text</item>
        ///   <item><c>number</c> — Direct field: case number identifier</item>
        ///   <item><c>priority</c> — Direct field: case priority value</item>
        ///   <item><c>$case_status_1n_case.label</c> — Relation-qualified: case status label via 1:N relation</item>
        ///   <item><c>$case_type_1n_case.label</c> — Relation-qualified: case type label via 1:N relation</item>
        ///   <item><c>subject</c> — Direct field: case subject line</item>
        /// </list>
        /// </para>
        /// </summary>
        private static readonly List<string> SearchIndexFields = new List<string>
        {
            "$account_nn_case.name",
            "description",
            "number",
            "priority",
            "$case_status_1n_case.label",
            "$case_type_1n_case.label",
            "subject"
        };

        private readonly IPublishEndpoint _publishEndpoint;
        private readonly SearchService _searchService;
        private readonly ILogger<CaseEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="CaseEventPublisher"/> class with
        /// all required dependencies injected via the DI container.
        ///
        /// <para>
        /// Replaces the monolith's pattern of <c>new SearchService()</c> instantiation inside
        /// CaseHook methods with proper constructor injection, enabling testability and
        /// lifecycle management by the DI container.
        /// </para>
        /// </summary>
        /// <param name="publishEndpoint">
        /// MassTransit publish endpoint for publishing downstream domain events to the
        /// message broker (RabbitMQ or SNS+SQS). Reserved for future downstream event
        /// publishing scenarios (e.g., CaseSearchIndexed event for Reporting service).
        /// </param>
        /// <param name="searchService">
        /// CRM x_search field regeneration service. Computes concatenated search index text
        /// from the record's indexed fields and persists it to the x_search column.
        /// </param>
        /// <param name="logger">
        /// Structured logger for observability in the distributed microservice architecture.
        /// Logs event processing start/success at Information level and errors at Error level.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="publishEndpoint"/>, <paramref name="searchService"/>,
        /// or <paramref name="logger"/> is <c>null</c>.
        /// </exception>
        public CaseEventPublisher(
            IPublishEndpoint publishEndpoint,
            SearchService searchService,
            ILogger<CaseEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Consumes a <see cref="RecordCreatedEvent"/> from the message bus.
        /// If the event's <see cref="RecordCreatedEvent.EntityName"/> matches "case"
        /// (case-insensitive), regenerates the x_search field for the newly created record.
        ///
        /// <para>
        /// <b>Business Logic (preserved from monolith):</b>
        /// <code>
        /// // Original: CaseHook.OnPostCreateRecord
        /// new SearchService().RegenSearchField(entityName, record, Configuration.CaseSearchIndexFields);
        /// </code>
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context wrapping the <see cref="RecordCreatedEvent"/> message
        /// with metadata (message ID, correlation ID, headers, cancellation token).
        /// </param>
        /// <returns>A <see cref="Task"/> representing the asynchronous consume operation.</returns>
        public async Task Consume(ConsumeContext<RecordCreatedEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, EntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.Record;

                _logger.LogInformation(
                    "Processing case created event for record {RecordId}, CorrelationId: {CorrelationId}",
                    record?["id"],
                    context.Message.CorrelationId);

                _searchService.RegenSearchField(EntityName, record, SearchIndexFields);

                _logger.LogInformation(
                    "Successfully regenerated x_search for created case record {RecordId}",
                    record?["id"]);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing case created event for record {RecordId}",
                    context.Message.Record?["id"]);
                throw; // Re-throw for MassTransit retry/error queue
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Consumes a <see cref="RecordUpdatedEvent"/> from the message bus.
        /// If the event's <see cref="RecordUpdatedEvent.EntityName"/> matches "case"
        /// (case-insensitive), regenerates the x_search field using the post-update
        /// record state (<see cref="RecordUpdatedEvent.NewRecord"/>).
        ///
        /// <para>
        /// <b>Business Logic (preserved from monolith):</b>
        /// <code>
        /// // Original: CaseHook.OnPostUpdateRecord
        /// new SearchService().RegenSearchField(entityName, record, Configuration.CaseSearchIndexFields);
        /// </code>
        /// Note: The original hook received the post-update record state as its <c>record</c>
        /// parameter. In the enriched <see cref="RecordUpdatedEvent"/>, the equivalent is
        /// <see cref="RecordUpdatedEvent.NewRecord"/> (post-update state), while
        /// <see cref="RecordUpdatedEvent.OldRecord"/> carries the pre-update state.
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context wrapping the <see cref="RecordUpdatedEvent"/> message
        /// with metadata (message ID, correlation ID, headers, cancellation token).
        /// </param>
        /// <returns>A <see cref="Task"/> representing the asynchronous consume operation.</returns>
        public async Task Consume(ConsumeContext<RecordUpdatedEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, EntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.NewRecord;

                _logger.LogInformation(
                    "Processing case updated event for record {RecordId}, CorrelationId: {CorrelationId}",
                    record?["id"],
                    context.Message.CorrelationId);

                _searchService.RegenSearchField(EntityName, record, SearchIndexFields);

                _logger.LogInformation(
                    "Successfully regenerated x_search for updated case record {RecordId}",
                    record?["id"]);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing case updated event for record {RecordId}",
                    context.Message.NewRecord?["id"]);
                throw; // Re-throw for MassTransit retry/error queue
            }

            await Task.CompletedTask;
        }
    }
}
