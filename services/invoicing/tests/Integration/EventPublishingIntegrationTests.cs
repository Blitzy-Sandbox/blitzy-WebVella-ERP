using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using FluentAssertions;
using Xunit;
using WebVellaErp.Invoicing.Models;
using WebVellaErp.Invoicing.Services;

namespace WebVellaErp.Invoicing.Tests.Integration
{
    /// <summary>
    /// Integration tests verifying all SNS domain event publishing for the Invoicing
    /// bounded context against real LocalStack SNS/SQS services.
    ///
    /// <para>
    /// <b>Replaces:</b> Monolith's synchronous post-CRUD hook execution pattern from
    /// <c>WebVella.Erp/Hooks/RecordHookManager.cs</c> — specifically
    /// <c>ExecutePostCreateRecordHooks()</c>, <c>ExecutePostUpdateRecordHooks()</c>,
    /// and <c>ExecutePostDeleteRecordHooks()</c> which iterated over hooked instances
    /// synchronously within the same process. The new pattern publishes domain events
    /// to SNS topics asynchronously for decoupled cross-service communication.
    /// </para>
    ///
    /// <para>
    /// <b>Test pattern (SNS → SQS verification):</b>
    /// Each test creates an SNS topic, subscribes an SQS queue, publishes an event
    /// via <see cref="InvoiceEventPublisher"/>, then polls the SQS queue to read
    /// and verify the published message. This follows AAP §0.8.4: all integration
    /// tests MUST execute against LocalStack — NO mocked AWS SDK calls.
    /// </para>
    ///
    /// <para>
    /// <b>Event types verified:</b>
    /// <list type="bullet">
    ///   <item><c>invoicing.invoice.created</c></item>
    ///   <item><c>invoicing.invoice.updated</c></item>
    ///   <item><c>invoicing.invoice.issued</c></item>
    ///   <item><c>invoicing.invoice.paid</c></item>
    ///   <item><c>invoicing.invoice.voided</c></item>
    ///   <item><c>invoicing.payment.processed</c></item>
    /// </list>
    /// </para>
    /// </summary>
    public class EventPublishingIntegrationTests : IClassFixture<LocalStackFixture>, IAsyncLifetime
    {
        // ═══════════════════════════════════════════════════════════════════════
        //  Fields
        // ═══════════════════════════════════════════════════════════════════════

        private readonly LocalStackFixture _fixture;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly IAmazonSQS _sqsClient;

        /// <summary>SNS topic ARN for all invoicing events (created per test instance).</summary>
        private string _topicArn = string.Empty;

        /// <summary>SQS queue URL for subscribing and reading published events.</summary>
        private string _testQueueUrl = string.Empty;

        /// <summary>SQS queue ARN for SNS subscription.</summary>
        private string _testQueueArn = string.Empty;

        /// <summary>
        /// Original value of INVOICE_SNS_TOPIC_ARN env var (restored in DisposeAsync
        /// to avoid side-effects on other tests in the same process).
        /// </summary>
        private string? _originalInvoiceTopicArn;

        /// <summary>
        /// Original value of PAYMENT_SNS_TOPIC_ARN env var (restored in DisposeAsync).
        /// </summary>
        private string? _originalPaymentTopicArn;

        // ═══════════════════════════════════════════════════════════════════════
        //  Constructor
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Constructs the test class with a shared <see cref="LocalStackFixture"/> providing
        /// pre-configured SNS and SQS clients targeting LocalStack at http://localhost:4566.
        /// Per AAP §0.8.4: all integration tests hit real LocalStack, zero mocked AWS SDK calls.
        /// </summary>
        /// <param name="fixture">
        /// Shared test fixture with AWS clients configured for LocalStack endpoint.
        /// </param>
        public EventPublishingIntegrationTests(LocalStackFixture fixture)
        {
            _fixture = fixture;
            _snsClient = fixture.SnsClient;
            _sqsClient = fixture.SqsClient;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Lifecycle (IAsyncLifetime)
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Per-test setup: creates an isolated SNS topic and SQS queue, subscribes the
        /// queue to the topic, and configures environment variables so that
        /// <see cref="InvoiceEventPublisher"/> publishes to the test topic.
        ///
        /// <para>
        /// xUnit creates a new test class instance per [RdsFact] method, so each test
        /// runs with its own isolated SNS → SQS channel, preventing cross-test
        /// message interference.
        /// </para>
        /// </summary>
        public async Task InitializeAsync()
        {
            // Save original env vars so we can restore them in DisposeAsync
            _originalInvoiceTopicArn = Environment.GetEnvironmentVariable("INVOICE_SNS_TOPIC_ARN");
            _originalPaymentTopicArn = Environment.GetEnvironmentVariable("PAYMENT_SNS_TOPIC_ARN");

            // Create a unique SNS topic per test instance to ensure complete isolation
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var topicName = $"invoicing-events-{uniqueSuffix}";
            var queueName = $"invoicing-test-queue-{uniqueSuffix}";

            // Step 1: Create SNS topic
            var topicResponse = await _snsClient.CreateTopicAsync(topicName);
            _topicArn = topicResponse.TopicArn;

            // Step 2: Create SQS test queue
            var queueResponse = await _sqsClient.CreateQueueAsync(queueName);
            _testQueueUrl = queueResponse.QueueUrl;

            // Step 3: Resolve queue ARN for SNS subscription
            var attrResponse = await _sqsClient.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = _testQueueUrl,
                    AttributeNames = new List<string> { "QueueArn" }
                });
            _testQueueArn = attrResponse.Attributes["QueueArn"];

            // Step 4: Set SQS queue policy allowing SNS to publish to it
            var queuePolicy = $$"""
            {
                "Version": "2012-10-17",
                "Statement": [
                    {
                        "Effect": "Allow",
                        "Principal": "*",
                        "Action": "sqs:SendMessage",
                        "Resource": "{{_testQueueArn}}",
                        "Condition": {
                            "ArnEquals": {
                                "aws:SourceArn": "{{_topicArn}}"
                            }
                        }
                    }
                ]
            }
            """;

            await _sqsClient.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = _testQueueUrl,
                Attributes = new Dictionary<string, string>
                {
                    ["Policy"] = queuePolicy
                }
            });

            // Step 5: Subscribe the SQS queue to the SNS topic
            // RawMessageDelivery = false (default) so SNS wraps messages in an envelope
            // containing MessageAttributes, allowing attribute-based verification
            await _snsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = _topicArn,
                Protocol = "sqs",
                Endpoint = _testQueueArn,
                Attributes = new Dictionary<string, string>
                {
                    // Ensure full SNS envelope is delivered to SQS (not raw)
                    // so we can verify MessageAttributes in the envelope
                    ["RawMessageDelivery"] = "false"
                }
            });

            // Step 6: Set environment variables for InvoiceEventPublisher constructor.
            // Both invoice and payment events use the same test topic for simplicity.
            Environment.SetEnvironmentVariable("INVOICE_SNS_TOPIC_ARN", _topicArn);
            Environment.SetEnvironmentVariable("PAYMENT_SNS_TOPIC_ARN", _topicArn);
        }

        /// <summary>
        /// Per-test cleanup: deletes the SQS queue and SNS topic, and restores
        /// the original environment variables.
        /// </summary>
        public async Task DisposeAsync()
        {
            // Delete the SQS queue (ignore errors if already deleted)
            try
            {
                if (!string.IsNullOrEmpty(_testQueueUrl))
                {
                    await _sqsClient.DeleteQueueAsync(_testQueueUrl);
                }
            }
            catch
            {
                // Swallow cleanup failures to avoid masking test failures
            }

            // Delete the SNS topic (ignore errors if already deleted)
            try
            {
                if (!string.IsNullOrEmpty(_topicArn))
                {
                    await _snsClient.DeleteTopicAsync(_topicArn);
                }
            }
            catch
            {
                // Swallow cleanup failures to avoid masking test failures
            }

            // Restore original environment variables
            Environment.SetEnvironmentVariable("INVOICE_SNS_TOPIC_ARN", _originalInvoiceTopicArn);
            Environment.SetEnvironmentVariable("PAYMENT_SNS_TOPIC_ARN", _originalPaymentTopicArn);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Invoice Created Event
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishInvoiceCreatedAsync"/>
        /// publishes a correctly structured <c>invoicing.invoice.created</c> domain event
        /// to the SNS topic. The event payload must contain the invoice data with the
        /// exact <c>Id</c> matching the source invoice.
        ///
        /// <para>
        /// Replaces source pattern: <c>RecordHookManager.ExecutePostCreateRecordHooks()</c>
        /// (RecordManager.cs lines 305-307) where synchronous in-process hooks were
        /// invoked after record creation.
        /// </para>
        /// </summary>
        [RdsFact]
        public async Task PublishInvoiceCreated_ShouldPublishToSNSTopic()
        {
            // Arrange
            var invoice = CreateTestInvoice(InvoiceStatus.Draft);
            var publisher = CreateEventPublisher();

            // Act
            await publisher.PublishInvoiceCreatedAsync(invoice);

            // Assert
            var (eventEnvelope, _) = await ReceiveSnsEventFromSqsAsync();

            var eventType = eventEnvelope.GetProperty("eventType").GetString();
            var eventId = eventEnvelope.GetProperty("eventId").GetString();
            var source = eventEnvelope.GetProperty("source").GetString();
            var payload = eventEnvelope.GetProperty("payload");
            var payloadId = payload.GetProperty("id").GetString();

            eventType.Should().Be("invoicing.invoice.created");
            eventId.Should().NotBeNullOrEmpty();
            Guid.TryParse(eventId, out _).Should().BeTrue("eventId must be a valid GUID for idempotency");
            source.Should().Be("invoicing-service");
            payloadId.Should().Be(invoice.Id.ToString());
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Invoice Updated Event
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishInvoiceUpdatedAsync"/>
        /// publishes a correctly structured <c>invoicing.invoice.updated</c> domain event.
        ///
        /// <para>
        /// Replaces source pattern: <c>RecordHookManager.ExecutePostUpdateRecordHooks()</c>
        /// (RecordManager.cs lines 770-775) where synchronous in-process hooks were
        /// invoked after record updates.
        /// </para>
        /// </summary>
        [RdsFact]
        public async Task PublishInvoiceUpdated_ShouldPublishToSNSTopic()
        {
            // Arrange
            var invoice = CreateTestInvoice(InvoiceStatus.Draft);
            invoice.TotalAmount = 250.75m;
            invoice.SubTotal = 225.00m;
            invoice.TaxAmount = 25.75m;
            var publisher = CreateEventPublisher();

            // Act
            await publisher.PublishInvoiceUpdatedAsync(invoice);

            // Assert
            var (eventEnvelope, _) = await ReceiveSnsEventFromSqsAsync();

            var eventType = eventEnvelope.GetProperty("eventType").GetString();
            var eventId = eventEnvelope.GetProperty("eventId").GetString();
            var source = eventEnvelope.GetProperty("source").GetString();
            var version = eventEnvelope.GetProperty("version").GetInt32();
            var payload = eventEnvelope.GetProperty("payload");
            var payloadId = payload.GetProperty("id").GetString();

            eventType.Should().Be("invoicing.invoice.updated");
            eventId.Should().NotBeNullOrEmpty();
            Guid.TryParse(eventId, out _).Should().BeTrue("eventId must be a valid GUID");
            source.Should().Be("invoicing-service");
            version.Should().BeGreaterThanOrEqualTo(1);
            payloadId.Should().Be(invoice.Id.ToString());
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Invoice Issued Event
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishInvoiceIssuedAsync"/>
        /// publishes a correctly structured <c>invoicing.invoice.issued</c> domain event
        /// when an invoice transitions to <see cref="InvoiceStatus.Issued"/>.
        /// </summary>
        [RdsFact]
        public async Task PublishInvoiceIssued_ShouldPublishToSNSTopic()
        {
            // Arrange
            var invoice = CreateTestInvoice(InvoiceStatus.Issued);
            var publisher = CreateEventPublisher();

            // Act
            await publisher.PublishInvoiceIssuedAsync(invoice);

            // Assert
            var (eventEnvelope, _) = await ReceiveSnsEventFromSqsAsync();

            var eventType = eventEnvelope.GetProperty("eventType").GetString();
            var eventId = eventEnvelope.GetProperty("eventId").GetString();
            var source = eventEnvelope.GetProperty("source").GetString();
            var version = eventEnvelope.GetProperty("version").GetInt32();
            var payload = eventEnvelope.GetProperty("payload");
            var payloadId = payload.GetProperty("id").GetString();

            eventType.Should().Be("invoicing.invoice.issued");
            eventId.Should().NotBeNullOrEmpty();
            Guid.TryParse(eventId, out _).Should().BeTrue("eventId must be a valid GUID");
            source.Should().Be("invoicing-service");
            version.Should().BeGreaterThanOrEqualTo(1);
            payloadId.Should().Be(invoice.Id.ToString());
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Invoice Paid Event
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishInvoicePaidAsync"/>
        /// publishes a correctly structured <c>invoicing.invoice.paid</c> domain event
        /// when an invoice transitions to <see cref="InvoiceStatus.Paid"/>.
        /// </summary>
        [RdsFact]
        public async Task PublishInvoicePaid_ShouldPublishToSNSTopic()
        {
            // Arrange
            var invoice = CreateTestInvoice(InvoiceStatus.Paid);
            var publisher = CreateEventPublisher();

            // Act
            await publisher.PublishInvoicePaidAsync(invoice);

            // Assert
            var (eventEnvelope, _) = await ReceiveSnsEventFromSqsAsync();

            var eventType = eventEnvelope.GetProperty("eventType").GetString();
            var eventId = eventEnvelope.GetProperty("eventId").GetString();
            var source = eventEnvelope.GetProperty("source").GetString();
            var version = eventEnvelope.GetProperty("version").GetInt32();
            var payload = eventEnvelope.GetProperty("payload");
            var payloadId = payload.GetProperty("id").GetString();

            eventType.Should().Be("invoicing.invoice.paid");
            eventId.Should().NotBeNullOrEmpty();
            Guid.TryParse(eventId, out _).Should().BeTrue("eventId must be a valid GUID");
            source.Should().Be("invoicing-service");
            version.Should().BeGreaterThanOrEqualTo(1);
            payloadId.Should().Be(invoice.Id.ToString());
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Invoice Voided Event
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishInvoiceVoidedAsync"/>
        /// publishes a correctly structured <c>invoicing.invoice.voided</c> domain event
        /// when an invoice transitions to <see cref="InvoiceStatus.Voided"/>.
        /// </summary>
        [RdsFact]
        public async Task PublishInvoiceVoided_ShouldPublishToSNSTopic()
        {
            // Arrange
            var invoice = CreateTestInvoice(InvoiceStatus.Voided);
            var publisher = CreateEventPublisher();

            // Act
            await publisher.PublishInvoiceVoidedAsync(invoice);

            // Assert
            var (eventEnvelope, _) = await ReceiveSnsEventFromSqsAsync();

            var eventType = eventEnvelope.GetProperty("eventType").GetString();
            var eventId = eventEnvelope.GetProperty("eventId").GetString();
            var source = eventEnvelope.GetProperty("source").GetString();
            var version = eventEnvelope.GetProperty("version").GetInt32();
            var payload = eventEnvelope.GetProperty("payload");
            var payloadId = payload.GetProperty("id").GetString();

            eventType.Should().Be("invoicing.invoice.voided");
            eventId.Should().NotBeNullOrEmpty();
            Guid.TryParse(eventId, out _).Should().BeTrue("eventId must be a valid GUID");
            source.Should().Be("invoicing-service");
            version.Should().BeGreaterThanOrEqualTo(1);
            payloadId.Should().Be(invoice.Id.ToString());
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Payment Processed Event
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishPaymentProcessedAsync"/>
        /// publishes a correctly structured <c>invoicing.payment.processed</c> domain event.
        /// The event payload must contain the Payment data including <c>InvoiceId</c> and
        /// the exact <c>Amount</c> (193.05m) for financial accuracy verification.
        /// </summary>
        [RdsFact]
        public async Task PublishPaymentProcessed_ShouldPublishToSNSTopic()
        {
            // Arrange
            var payment = CreateTestPayment();
            var publisher = CreateEventPublisher();

            // Act
            await publisher.PublishPaymentProcessedAsync(payment);

            // Assert
            var (eventEnvelope, _) = await ReceiveSnsEventFromSqsAsync();

            var eventType = eventEnvelope.GetProperty("eventType").GetString();
            var eventId = eventEnvelope.GetProperty("eventId").GetString();
            var source = eventEnvelope.GetProperty("source").GetString();
            var version = eventEnvelope.GetProperty("version").GetInt32();
            var payload = eventEnvelope.GetProperty("payload");
            var payloadInvoiceId = payload.GetProperty("invoiceId").GetString();
            var payloadAmount = payload.GetProperty("amount").GetDecimal();

            eventType.Should().Be("invoicing.payment.processed");
            eventId.Should().NotBeNullOrEmpty();
            Guid.TryParse(eventId, out _).Should().BeTrue("eventId must be a valid GUID");
            source.Should().Be("invoicing-service");
            version.Should().BeGreaterThanOrEqualTo(1);
            payloadInvoiceId.Should().Be(payment.InvoiceId.ToString());
            payloadAmount.Should().Be(193.05m);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Event Envelope Structure Validation
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that the published domain event envelope contains ALL required fields
        /// with valid values, as specified by the event schema contract:
        /// <list type="bullet">
        ///   <item><c>eventType</c> — non-empty string matching <c>invoicing.{entity}.{action}</c></item>
        ///   <item><c>eventId</c> — valid GUID string for idempotency (AAP §0.8.5)</item>
        ///   <item><c>timestamp</c> — valid ISO 8601 UTC datetime, recent (within 60 seconds)</item>
        ///   <item><c>correlationId</c> — non-empty string for distributed tracing (AAP §0.8.5)</item>
        ///   <item><c>source</c> — equals "invoicing-service"</item>
        ///   <item><c>version</c> — integer >= 1</item>
        ///   <item><c>payload</c> — non-null object containing event-specific data</item>
        /// </list>
        /// </summary>
        [RdsFact]
        public async Task EventEnvelope_ShouldContainAllRequiredFields()
        {
            // Arrange — publish an invoice.created event as the representative case
            var invoice = CreateTestInvoice(InvoiceStatus.Draft);
            var publisher = CreateEventPublisher();

            // Act
            await publisher.PublishInvoiceCreatedAsync(invoice);

            // Assert — verify every required envelope field
            var (eventEnvelope, _) = await ReceiveSnsEventFromSqsAsync();

            // eventType: non-empty, follows {domain}.{entity}.{action} convention (AAP §0.8.5)
            var eventType = eventEnvelope.GetProperty("eventType").GetString();
            eventType.Should().NotBeNullOrEmpty("eventType is a required envelope field");
            eventType.Should().Be("invoicing.invoice.created",
                "event type must follow {domain}.{entity}.{action} naming convention per AAP §0.8.5");

            // eventId: valid GUID for consumer idempotency / deduplication (AAP §0.8.5)
            var eventId = eventEnvelope.GetProperty("eventId").GetString();
            eventId.Should().NotBeNullOrEmpty("eventId is required for consumer idempotency");
            Guid.TryParse(eventId, out var parsedEventId).Should().BeTrue(
                "eventId must be a valid GUID for deduplication per AAP §0.8.5");
            parsedEventId.Should().NotBe(Guid.Empty, "eventId must not be an empty GUID");

            // timestamp: valid ISO 8601 UTC datetime, recent (within 60 seconds)
            var timestampStr = eventEnvelope.GetProperty("timestamp").GetString();
            timestampStr.Should().NotBeNullOrEmpty("timestamp is a required envelope field");
            DateTime.TryParse(timestampStr, out var parsedTimestamp).Should().BeTrue(
                "timestamp must be a valid ISO 8601 datetime");
            var timeDifference = DateTime.UtcNow - parsedTimestamp.ToUniversalTime();
            timeDifference.Should().BeLessThan(TimeSpan.FromSeconds(60),
                "event timestamp should be recent (generated at publish time)");

            // correlationId: non-empty for distributed tracing (AAP §0.8.5)
            var correlationId = eventEnvelope.GetProperty("correlationId").GetString();
            correlationId.Should().NotBeNullOrEmpty(
                "correlationId is required for distributed tracing per AAP §0.8.5");

            // source: identifies the originating service
            var source = eventEnvelope.GetProperty("source").GetString();
            source.Should().Be("invoicing-service",
                "source must identify the originating bounded context");

            // version: integer >= 1 for backward-compatible event evolution
            var version = eventEnvelope.GetProperty("version").GetInt32();
            version.Should().BeGreaterThanOrEqualTo(1,
                "event schema version must be >= 1 for backward compatibility");

            // payload: non-null object containing the Invoice data
            eventEnvelope.TryGetProperty("payload", out var payload).Should().BeTrue(
                "payload is a required envelope field containing event-specific data");
            payload.ValueKind.Should().Be(JsonValueKind.Object,
                "payload must be a JSON object, not null or primitive");
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  SNS Message Attributes for Subscriber Filtering
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that published SNS messages include <c>MessageAttributes</c> that
        /// enable SQS subscription filter policies. When <c>RawMessageDelivery</c> is
        /// <c>false</c> (default), the SNS envelope JSON delivered to SQS contains a
        /// <c>MessageAttributes</c> field with the attributes set by the publisher.
        ///
        /// <para>
        /// Per AAP §0.4.2 (Event-Driven Architecture): SNS message attributes enable
        /// SQS subscription filter policies so that consumers only receive events they
        /// care about, reducing unnecessary processing.
        /// </para>
        ///
        /// <para>
        /// Expected attributes for invoice events:
        /// <list type="bullet">
        ///   <item><c>eventType</c> — String, e.g. "invoicing.invoice.created"</item>
        ///   <item><c>entityId</c> — String, the invoice ID</item>
        ///   <item><c>correlationId</c> — String, the correlation ID</item>
        /// </list>
        /// </para>
        /// </summary>
        [RdsFact]
        public async Task SNSMessageAttributes_ShouldBeSetForFiltering()
        {
            // Arrange
            var invoice = CreateTestInvoice(InvoiceStatus.Draft);
            var publisher = CreateEventPublisher();

            // Act
            await publisher.PublishInvoiceCreatedAsync(invoice);

            // Assert — read the raw SNS envelope from SQS and verify MessageAttributes
            var (_, snsEnvelope) = await ReceiveSnsEventFromSqsAsync();

            // The SNS envelope (when RawMessageDelivery=false) contains MessageAttributes
            snsEnvelope.TryGetProperty("MessageAttributes", out var messageAttributes)
                .Should().BeTrue("SNS envelope should contain MessageAttributes for filtering");

            // Verify eventType attribute — used by SQS filter policies to route events
            messageAttributes.TryGetProperty("eventType", out var eventTypeAttr).Should().BeTrue(
                "eventType attribute is required for SQS subscription filter policies");
            var eventTypeValue = eventTypeAttr.GetProperty("Value").GetString();
            eventTypeValue.Should().Be("invoicing.invoice.created",
                "eventType attribute must match the published event type");

            // Verify entityId attribute — the invoice ID for entity-level filtering
            messageAttributes.TryGetProperty("entityId", out var entityIdAttr).Should().BeTrue(
                "entityId attribute is required for entity-level filtering");
            var entityIdValue = entityIdAttr.GetProperty("Value").GetString();
            entityIdValue.Should().Be(invoice.Id.ToString(),
                "entityId attribute must match the invoice ID");

            // Verify correlationId attribute — for distributed tracing
            messageAttributes.TryGetProperty("correlationId", out var corrIdAttr).Should().BeTrue(
                "correlationId attribute is required for distributed tracing");
            var corrIdValue = corrIdAttr.GetProperty("Value").GetString();
            corrIdValue.Should().NotBeNullOrEmpty(
                "correlationId attribute must have a non-empty value");
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Helper Methods — Test Data Construction
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a fully populated test <see cref="Invoice"/> model with deterministic
        /// values for reliable assertion. Invoice properties map to source monolith's
        /// entity field definitions from <c>WebVella.Erp.Plugins.Next/NextPlugin</c>.
        /// </summary>
        /// <param name="status">
        /// The <see cref="InvoiceStatus"/> to set — varies by lifecycle event test
        /// (Draft for created/updated, Issued for issued, Paid for paid, Voided for voided).
        /// </param>
        /// <returns>A populated Invoice suitable for event publishing tests.</returns>
        private static Invoice CreateTestInvoice(InvoiceStatus status)
        {
            var invoiceId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var createdBy = Guid.NewGuid();
            var now = DateTime.UtcNow;

            return new Invoice
            {
                Id = invoiceId,
                InvoiceNumber = "INV-2025-0042",
                CustomerId = customerId,
                Status = status,
                IssueDate = now.Date,
                DueDate = now.Date.AddDays(30),
                SubTotal = 85.00m,
                TaxAmount = 15.00m,
                TotalAmount = 100.00m,
                Notes = "Integration test invoice",
                LineItems = new List<LineItem>
                {
                    new LineItem
                    {
                        Id = Guid.NewGuid(),
                        InvoiceId = invoiceId,
                        Description = "Consulting services",
                        Quantity = 2,
                        UnitPrice = 42.50m,
                        TaxRate = 0.1765m,
                        LineTotal = 85.00m
                    }
                },
                CreatedBy = createdBy,
                CreatedOn = now
            };
        }

        /// <summary>
        /// Creates a fully populated test <see cref="Payment"/> model with deterministic
        /// values for assertion. The <c>Amount</c> is set to <c>193.05m</c> as specified
        /// in the schema requirements for decimal precision verification.
        /// </summary>
        /// <returns>A populated Payment suitable for event publishing tests.</returns>
        private static Payment CreateTestPayment()
        {
            return new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = Guid.NewGuid(),
                Amount = 193.05m,
                PaymentDate = DateTime.UtcNow,
                PaymentMethod = PaymentMethod.BankTransfer,
                ReferenceNumber = "BANK-REF-20250224-001",
                Notes = "Integration test payment",
                CreatedBy = Guid.NewGuid(),
                CreatedOn = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates an <see cref="InvoiceEventPublisher"/> instance configured for the
        /// current test's SNS topic (via environment variables set in <see cref="InitializeAsync"/>).
        /// Uses the fixture's SNS client (targeting LocalStack) and a NullLogger for silent execution.
        /// </summary>
        /// <returns>A fully configured InvoiceEventPublisher for integration testing.</returns>
        private InvoiceEventPublisher CreateEventPublisher()
        {
            return new InvoiceEventPublisher(
                _snsClient,
                _fixture.CreateTestLogger<InvoiceEventPublisher>());
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Helper Methods — SQS Message Retrieval and Parsing
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Polls the test SQS queue to receive a published SNS event, parses the SNS
        /// envelope, and extracts the inner domain event JSON. Retries up to 3 times
        /// with 5-second long-polling per attempt to handle transient delivery delays.
        ///
        /// <para>
        /// Returns both the parsed domain event (inner <c>Message</c> field) and the
        /// full SNS envelope for tests that need to verify <c>MessageAttributes</c>.
        /// </para>
        /// </summary>
        /// <returns>
        /// A tuple of (domainEventElement, snsEnvelopeElement) as <see cref="JsonElement"/>
        /// values for flexible property-level assertions.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if no message is received after all retry attempts.
        /// </exception>
        private async Task<(JsonElement DomainEvent, JsonElement SnsEnvelope)> ReceiveSnsEventFromSqsAsync()
        {
            const int maxRetries = 3;
            const int waitTimeSeconds = 5;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                var receiveRequest = new ReceiveMessageRequest
                {
                    QueueUrl = _testQueueUrl,
                    WaitTimeSeconds = waitTimeSeconds,
                    MaxNumberOfMessages = 1,
                    // Request message attributes to be included in the response
                    MessageSystemAttributeNames = new List<string> { "All" }
                };

                var receiveResponse = await _sqsClient.ReceiveMessageAsync(receiveRequest);

                if (receiveResponse.Messages.Any())
                {
                    var sqsMessage = receiveResponse.Messages.First();

                    // Delete the message from the queue after reading to prevent
                    // re-delivery in subsequent test polling
                    await _sqsClient.DeleteMessageAsync(new DeleteMessageRequest
                    {
                        QueueUrl = _testQueueUrl,
                        ReceiptHandle = sqsMessage.ReceiptHandle
                    });

                    // The SQS message body is the SNS notification envelope JSON:
                    // {
                    //   "Type": "Notification",
                    //   "MessageId": "...",
                    //   "TopicArn": "...",
                    //   "Message": "<domain event JSON as escaped string>",
                    //   "Timestamp": "...",
                    //   "MessageAttributes": { ... }
                    // }
                    using var snsEnvelopeDoc = JsonDocument.Parse(sqsMessage.Body);
                    var snsEnvelope = snsEnvelopeDoc.RootElement.Clone();

                    // Extract the inner Message field — this is the actual domain event JSON
                    var innerMessageStr = snsEnvelope.GetProperty("Message").GetString()!;
                    using var domainEventDoc = JsonDocument.Parse(innerMessageStr);
                    var domainEvent = domainEventDoc.RootElement.Clone();

                    return (domainEvent, snsEnvelope);
                }
            }

            throw new InvalidOperationException(
                $"No message received from SQS queue after {maxRetries} attempts " +
                $"(total wait: {maxRetries * waitTimeSeconds}s). " +
                "Verify that LocalStack SNS/SQS is running and the subscription is active.");
        }
    }
}
