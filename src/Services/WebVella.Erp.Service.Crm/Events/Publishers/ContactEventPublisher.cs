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
    /// MassTransit event consumer that handles <see cref="RecordCreatedEvent"/> and
    /// <see cref="RecordUpdatedEvent"/> messages for the <c>contact</c> entity.
    /// <para>
    /// <b>Replaces:</b> <c>WebVella.Erp.Plugins.Next.Hooks.Api.ContactHook</c> which was
    /// decorated with <c>[HookAttachment("contact", int.MinValue)]</c> and implemented
    /// <c>IErpPostCreateRecordHook</c> and <c>IErpPostUpdateRecordHook</c>.
    /// </para>
    /// <para>
    /// <b>Business logic preserved (AAP §0.8.1 — zero tolerance):</b> On both record
    /// creation and update events for the "contact" entity, this consumer triggers
    /// <see cref="SearchService.RegenSearchField(string, EntityRecord, List{string})"/>
    /// with the exact same 15-field search index configuration
    /// (<c>ContactSearchIndexFields</c>) to regenerate the denormalized <c>x_search</c>
    /// column used for fast CRM contact filtering and searching.
    /// </para>
    /// <para>
    /// <b>Idempotency (AAP §0.8.2):</b> Duplicate event delivery is safe.
    /// <c>RegenSearchField</c> overwrites the <c>x_search</c> field with a deterministic
    /// computed value derived from the current record state. Re-processing the same event
    /// produces the identical result, so no deduplication mechanism is required.
    /// </para>
    /// <para>
    /// <b>Error handling:</b> Exceptions are logged with structured context (record ID)
    /// and re-thrown to allow MassTransit's retry and error queue policies to handle
    /// transient failures automatically.
    /// </para>
    /// </summary>
    public class ContactEventPublisher : IConsumer<RecordCreatedEvent>, IConsumer<RecordUpdatedEvent>
    {
        /// <summary>
        /// The entity name this consumer filters on. Only events with
        /// <c>EntityName == "contact"</c> (case-insensitive) are processed.
        /// </summary>
        private const string ContactEntityName = "contact";

        /// <summary>
        /// The exact list of indexed field names used for x_search regeneration
        /// of contact records. Character-for-character identical to the monolith's
        /// <c>Configuration.ContactSearchIndexFields</c> (Configuration.cs lines 17-19).
        /// <para>
        /// Includes 15 fields:
        /// <list type="bullet">
        ///   <item><description>Direct fields: city, email, fax_phone, first_name, fixed_phone,
        ///     job_title, last_name, mobile_phone, notes, post_code, region, street, street_2</description></item>
        ///   <item><description>Relation-qualified fields: $country_1n_contact.label, $account_nn_contact.name</description></item>
        /// </list>
        /// </para>
        /// </summary>
        private static readonly List<string> SearchIndexFields = new List<string>
        {
            "city",
            "$country_1n_contact.label",
            "$account_nn_contact.name",
            "email",
            "fax_phone",
            "first_name",
            "fixed_phone",
            "job_title",
            "last_name",
            "mobile_phone",
            "notes",
            "post_code",
            "region",
            "street",
            "street_2"
        };

        private readonly IPublishEndpoint _publishEndpoint;
        private readonly SearchService _searchService;
        private readonly ILogger<ContactEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ContactEventPublisher"/> class
        /// with dependency-injected services. Replaces the monolith pattern of direct
        /// <c>new SearchService()</c> instantiation in <c>ContactHook</c>.
        /// </summary>
        /// <param name="publishEndpoint">
        /// MassTransit publish endpoint for publishing downstream domain events to the
        /// message bus (RabbitMQ for local/Docker, SNS+SQS for AWS/LocalStack).
        /// </param>
        /// <param name="searchService">
        /// CRM search service for regenerating the <c>x_search</c> denormalized search
        /// text field on contact records.
        /// </param>
        /// <param name="logger">
        /// Structured logger for observability of event processing operations.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="publishEndpoint"/>, <paramref name="searchService"/>,
        /// or <paramref name="logger"/> is <c>null</c>.
        /// </exception>
        public ContactEventPublisher(
            IPublishEndpoint publishEndpoint,
            SearchService searchService,
            ILogger<ContactEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Consumes a <see cref="RecordCreatedEvent"/> message. If the event pertains to
        /// the "contact" entity, regenerates the <c>x_search</c> field by delegating to
        /// <see cref="SearchService.RegenSearchField(string, EntityRecord, List{string})"/>.
        /// <para>
        /// <b>Preserves original logic from:</b>
        /// <c>ContactHook.OnPostCreateRecord(string entityName, EntityRecord record)</c>
        /// which called <c>new SearchService().RegenSearchField(entityName, record, Configuration.ContactSearchIndexFields)</c>.
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context wrapping the <see cref="RecordCreatedEvent"/> message.
        /// Provides access to <c>context.Message.EntityName</c> and <c>context.Message.Record</c>.
        /// </param>
        /// <returns>A completed task when processing finishes.</returns>
        public async Task Consume(ConsumeContext<RecordCreatedEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, ContactEntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.Record;

                _searchService.RegenSearchField(ContactEntityName, record, SearchIndexFields);

                _logger.LogInformation(
                    "Processed contact created event for record {RecordId}",
                    record["id"]);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing contact created event for record {RecordId}",
                    context.Message.Record?["id"]);
                throw; // Re-throw for MassTransit retry/error queue
            }
        }

        /// <summary>
        /// Consumes a <see cref="RecordUpdatedEvent"/> message. If the event pertains to
        /// the "contact" entity, regenerates the <c>x_search</c> field using the post-update
        /// record state (<see cref="RecordUpdatedEvent.NewRecord"/>).
        /// <para>
        /// <b>Preserves original logic from:</b>
        /// <c>ContactHook.OnPostUpdateRecord(string entityName, EntityRecord record)</c>
        /// which called <c>new SearchService().RegenSearchField(entityName, record, Configuration.ContactSearchIndexFields)</c>.
        /// The original hook received the post-update record; here we use <c>NewRecord</c>
        /// from the enriched <see cref="RecordUpdatedEvent"/> which carries both pre- and
        /// post-update states.
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context wrapping the <see cref="RecordUpdatedEvent"/> message.
        /// Provides access to <c>context.Message.EntityName</c> and <c>context.Message.NewRecord</c>.
        /// </param>
        /// <returns>A completed task when processing finishes.</returns>
        public async Task Consume(ConsumeContext<RecordUpdatedEvent> context)
        {
            if (!string.Equals(context.Message.EntityName, ContactEntityName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var record = context.Message.NewRecord;

                _searchService.RegenSearchField(ContactEntityName, record, SearchIndexFields);

                _logger.LogInformation(
                    "Processed contact updated event for record {RecordId}",
                    record["id"]);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing contact updated event for record {RecordId}",
                    context.Message.NewRecord?["id"]);
                throw; // Re-throw for MassTransit retry/error queue
            }
        }
    }
}
