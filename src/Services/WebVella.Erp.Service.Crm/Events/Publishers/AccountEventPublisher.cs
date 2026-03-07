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
    /// for the "account" entity, triggering x_search field regeneration on each event.
    /// <para>
    /// <b>Replaces:</b> <c>WebVella.Erp.Plugins.Next.Hooks.Api.AccountHook</c> from the monolith,
    /// which was decorated with <c>[HookAttachment("account", int.MinValue)]</c> and implemented
    /// both <c>IErpPostCreateRecordHook</c> and <c>IErpPostUpdateRecordHook</c>. The original hook
    /// was invoked synchronously and in-process by <c>RecordHookManager</c> after record CRUD operations.
    /// </para>
    /// <para>
    /// <b>Microservice pattern:</b> This class implements two MassTransit <c>IConsumer&lt;T&gt;</c>
    /// interfaces — one for <see cref="RecordCreatedEvent"/> and one for <see cref="RecordUpdatedEvent"/>.
    /// Events are published asynchronously by the Core Platform service's <c>RecordManager</c> via
    /// RabbitMQ (local/Docker) or SNS+SQS (AWS/LocalStack), replacing the monolith's synchronous
    /// hook execution.
    /// </para>
    /// <para>
    /// <b>Business logic preserved:</b> Both <c>Consume</c> methods call
    /// <see cref="SearchService.RegenSearchField(string, EntityRecord, List{string})"/> with the
    /// identical 17-field <see cref="SearchIndexFields"/> list that the original <c>AccountHook</c>
    /// passed via <c>Configuration.AccountSearchIndexFields</c>. The DI-injected
    /// <see cref="SearchService"/> replaces the monolith's direct instantiation (<c>new SearchService()</c>).
    /// </para>
    /// <para>
    /// <b>Idempotency guarantee (AAP §0.8.2):</b> Duplicate event delivery is safe because
    /// <see cref="SearchService.RegenSearchField(string, EntityRecord, List{string})"/> is inherently
    /// idempotent — it overwrites the <c>x_search</c> field with the same computed value regardless
    /// of how many times it executes for the same record state. No additional deduplication mechanism
    /// is required.
    /// </para>
    /// <para>
    /// <b>DI registration:</b> Register via MassTransit consumer configuration in the CRM service's
    /// DI container:
    /// <code>
    /// cfg.AddConsumer&lt;AccountEventPublisher&gt;();
    /// </code>
    /// </para>
    /// </summary>
    public class AccountEventPublisher : IConsumer<RecordCreatedEvent>, IConsumer<RecordUpdatedEvent>
    {
        /// <summary>
        /// The entity name that this consumer filters on. Only events where
        /// <c>EntityName</c> equals "account" (case-insensitive) are processed.
        /// </summary>
        private const string AccountEntityName = "account";

        /// <summary>
        /// The exact set of 17 account search index fields, character-for-character identical to
        /// <c>WebVella.Erp.Plugins.Next.Configuration.AccountSearchIndexFields</c> (Configuration.cs lines 9-11).
        /// <para>
        /// These fields determine which account record properties are included in the denormalized
        /// <c>x_search</c> text column. Fields prefixed with <c>$</c> are relation-qualified
        /// (e.g., <c>$country_1n_account.label</c> resolves the related country entity's label).
        /// </para>
        /// <para>
        /// <b>CRITICAL:</b> Any modification to this list changes search indexing behavior for all
        /// account records. Changes must be coordinated with the CRM search UI and any downstream
        /// consumers that depend on account search results.
        /// </para>
        /// </summary>
        private static readonly List<string> SearchIndexFields = new List<string>
        {
            "city",
            "$country_1n_account.label",
            "email",
            "fax_phone",
            "first_name",
            "fixed_phone",
            "last_name",
            "mobile_phone",
            "name",
            "notes",
            "post_code",
            "region",
            "street",
            "street_2",
            "tax_id",
            "type",
            "website"
        };

        private readonly IPublishEndpoint _publishEndpoint;
        private readonly SearchService _searchService;
        private readonly ILogger<AccountEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="AccountEventPublisher"/> with injected dependencies.
        /// </summary>
        /// <param name="publishEndpoint">
        /// MassTransit publish endpoint for publishing events to the message bus
        /// (RabbitMQ or SNS+SQS). Used for any downstream event re-publishing if needed.
        /// </param>
        /// <param name="searchService">
        /// CRM search service for x_search field regeneration. Replaces the monolith's
        /// <c>new SearchService()</c> instantiation pattern with DI-injected scoped service.
        /// </param>
        /// <param name="logger">
        /// Structured logger for observability of account event processing, including
        /// success tracking and error reporting with record ID context.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="publishEndpoint"/>, <paramref name="searchService"/>,
        /// or <paramref name="logger"/> is null.
        /// </exception>
        public AccountEventPublisher(
            IPublishEndpoint publishEndpoint,
            SearchService searchService,
            ILogger<AccountEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Consumes a <see cref="RecordCreatedEvent"/> and regenerates the x_search field
        /// for newly created account records.
        /// <para>
        /// <b>Business logic equivalent:</b>
        /// <code>
        /// // Original monolith (AccountHook.cs line 14):
        /// new SearchService().RegenSearchField(entityName, record, Configuration.AccountSearchIndexFields);
        /// </code>
        /// </para>
        /// <para>
        /// Events for non-account entities are silently ignored (filtered by
        /// <see cref="AccountEntityName"/> constant with case-insensitive comparison).
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context containing the <see cref="RecordCreatedEvent"/> message
        /// with <c>EntityName</c> and <c>Record</c> properties.
        /// </param>
        /// <returns>A completed task when processing is done.</returns>
        public async Task Consume(ConsumeContext<RecordCreatedEvent> context)
        {
            // Filter: only process events for the "account" entity
            if (!string.Equals(context.Message.EntityName, AccountEntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.Record;

                // Regenerate the x_search denormalized search field — exact equivalent of the
                // original AccountHook.OnPostCreateRecord business logic
                _searchService.RegenSearchField(AccountEntityName, record, SearchIndexFields);

                _logger.LogInformation(
                    "Processed account created event for record {RecordId}, correlation {CorrelationId}",
                    record["id"],
                    context.Message.CorrelationId);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing account created event for record {RecordId}, correlation {CorrelationId}",
                    context.Message.Record?["id"],
                    context.Message.CorrelationId);

                // Re-throw to trigger MassTransit retry policy and eventual dead-letter/error queue
                throw;
            }
        }

        /// <summary>
        /// Consumes a <see cref="RecordUpdatedEvent"/> and regenerates the x_search field
        /// for updated account records using the post-update record state.
        /// <para>
        /// <b>Business logic equivalent:</b>
        /// <code>
        /// // Original monolith (AccountHook.cs line 19):
        /// new SearchService().RegenSearchField(entityName, record, Configuration.AccountSearchIndexFields);
        /// </code>
        /// </para>
        /// <para>
        /// Uses <see cref="RecordUpdatedEvent.NewRecord"/> (the post-update state) rather than
        /// <see cref="RecordUpdatedEvent.OldRecord"/>, matching the original hook behavior where
        /// <c>OnPostUpdateRecord</c> received the record in its post-update state.
        /// </para>
        /// <para>
        /// Events for non-account entities are silently ignored (filtered by
        /// <see cref="AccountEntityName"/> constant with case-insensitive comparison).
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context containing the <see cref="RecordUpdatedEvent"/> message
        /// with <c>EntityName</c>, <c>OldRecord</c>, and <c>NewRecord</c> properties.
        /// </param>
        /// <returns>A completed task when processing is done.</returns>
        public async Task Consume(ConsumeContext<RecordUpdatedEvent> context)
        {
            // Filter: only process events for the "account" entity
            if (!string.Equals(context.Message.EntityName, AccountEntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                // Use NewRecord (post-update state) matching the original hook's behavior
                // where OnPostUpdateRecord received the record after the update was applied
                var record = context.Message.NewRecord;

                // Regenerate the x_search denormalized search field — exact equivalent of the
                // original AccountHook.OnPostUpdateRecord business logic
                _searchService.RegenSearchField(AccountEntityName, record, SearchIndexFields);

                _logger.LogInformation(
                    "Processed account updated event for record {RecordId}, correlation {CorrelationId}",
                    record["id"],
                    context.Message.CorrelationId);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing account updated event for record {RecordId}, correlation {CorrelationId}",
                    context.Message.NewRecord?["id"],
                    context.Message.CorrelationId);

                // Re-throw to trigger MassTransit retry policy and eventual dead-letter/error queue
                throw;
            }
        }
    }
}
