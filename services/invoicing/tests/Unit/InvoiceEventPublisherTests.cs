// ---------------------------------------------------------------------------
// InvoiceEventPublisherTests.cs
// Invoicing Bounded Context — Unit Tests for IInvoiceEventPublisher
//
// Comprehensive xUnit unit tests for the InvoiceEventPublisher, validating
// SNS-based domain event publishing that replaces the monolith's synchronous
// RecordHookManager post-CRUD hook dispatch (RecordHookManager.cs lines 45-52,
// 55-63, 68-76, 91-99).
//
// Tests cover:
//   - Correct event type strings ({domain}.{entity}.{action} per AAP §0.8.5)
//   - Domain event envelope structure (eventType, eventId, timestamp,
//     correlationId, source, version, payload)
//   - SNS message attributes for subscription filtering (eventType, entityId)
//   - Fire-and-forget behavior: SNS failure does NOT throw (matches source
//     RecordManager.cs post-hook pattern where persistence is not rolled back)
//   - Error logging on SNS failure
//   - Topic ARN routing: invoice events → INVOICE_SNS_TOPIC_ARN,
//     payment events → PAYMENT_SNS_TOPIC_ARN
//   - All 6 publish methods: created, updated, issued, paid, voided,
//     payment.processed
//
// Source mapping:
//   - WebVella.Erp/Hooks/RecordHookManager.cs  → replaced by SNS event pub
//   - WebVella.Erp/Hooks/HookManager.cs         → replaced by DI mocked SNS
//   - WebVella.Erp/Api/RecordManager.cs          → post-hook fire-and-forget
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Invoicing.Models;
using WebVellaErp.Invoicing.Services;
using Xunit;

namespace WebVellaErp.Invoicing.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="InvoiceEventPublisher"/> validating all 6 async
    /// publish methods of <see cref="IInvoiceEventPublisher"/>. The SNS client is
    /// mocked via <see cref="Mock{T}"/> — no real AWS calls are made.
    ///
    /// <para>
    /// The InvoiceEventPublisher replaces the monolith's synchronous hook system
    /// (<c>RecordHookManager.ExecutePostCreateRecordHooks</c>,
    /// <c>ExecutePostUpdateRecordHooks</c>, <c>ExecutePostDeleteRecordHooks</c>)
    /// with asynchronous SNS domain event publishing per AAP §0.7.2.
    /// </para>
    ///
    /// <para>
    /// Key test principles:
    /// <list type="bullet">
    ///   <item><description>Fire-and-forget: SNS failures must NOT propagate (matching source RecordManager.cs post-hook pattern)</description></item>
    ///   <item><description>Event naming: <c>{domain}.{entity}.{action}</c> per AAP §0.8.5</description></item>
    ///   <item><description>Event envelope: eventType, eventId (UUID), timestamp (UTC), correlationId, source, version, payload</description></item>
    ///   <item><description>SNS message attributes: eventType and entityId for subscription filtering per AAP §0.7.2</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public class InvoiceEventPublisherTests
    {
        /// <summary>
        /// Mocked AWS SNS client replacing the monolith's reflection-based
        /// <c>HookManager.GetHookedInstances&lt;T&gt;()</c> discovery.
        /// </summary>
        private readonly Mock<IAmazonSimpleNotificationService> _snsClientMock;

        /// <summary>
        /// Mocked logger for verifying error logging on SNS failure scenarios.
        /// InvoiceEventPublisher uses structured JSON logging with correlation-ID
        /// per AAP §0.8.5.
        /// </summary>
        private readonly Mock<ILogger<InvoiceEventPublisher>> _loggerMock;

        /// <summary>
        /// System Under Test — the InvoiceEventPublisher instance being tested.
        /// </summary>
        private readonly InvoiceEventPublisher _sut;

        /// <summary>
        /// Invoice SNS topic ARN used for all invoice lifecycle events
        /// (created, updated, issued, paid, voided).
        /// </summary>
        private const string InvoiceTopicArn = "arn:aws:sns:us-east-1:000000000000:invoicing-events";

        /// <summary>
        /// Payment SNS topic ARN used for payment events (payment.processed).
        /// Separate from invoice topic to enable independent scaling and filtering.
        /// </summary>
        private const string PaymentTopicArn = "arn:aws:sns:us-east-1:000000000000:payment-events";

        /// <summary>
        /// Initializes mocks, environment variables, and the SUT for each test.
        /// Environment variables are set before constructing InvoiceEventPublisher
        /// because the constructor reads INVOICE_SNS_TOPIC_ARN and PAYMENT_SNS_TOPIC_ARN.
        /// </summary>
        public InvoiceEventPublisherTests()
        {
            // Set environment variables BEFORE constructing SUT —
            // InvoiceEventPublisher reads these in its constructor
            Environment.SetEnvironmentVariable("INVOICE_SNS_TOPIC_ARN", InvoiceTopicArn);
            Environment.SetEnvironmentVariable("PAYMENT_SNS_TOPIC_ARN", PaymentTopicArn);

            _snsClientMock = new Mock<IAmazonSimpleNotificationService>();
            _loggerMock = new Mock<ILogger<InvoiceEventPublisher>>();

            _sut = new InvoiceEventPublisher(
                _snsClientMock.Object,
                _loggerMock.Object);
        }

        // =====================================================================
        // Test Helpers
        // =====================================================================

        /// <summary>
        /// Creates a fully populated test invoice with realistic data for use in
        /// all invoice publish method tests. Includes Id, InvoiceNumber, CustomerId,
        /// Status (Draft), TotalAmount, and one LineItem.
        /// </summary>
        /// <returns>A populated <see cref="Invoice"/> instance for testing.</returns>
        private static Invoice CreateTestInvoice()
        {
            var invoiceId = Guid.NewGuid();
            return new Invoice
            {
                Id = invoiceId,
                InvoiceNumber = "INV-2024-0001",
                CustomerId = Guid.NewGuid(),
                Status = InvoiceStatus.Draft,
                TotalAmount = 1250.00m,
                LineItems = new List<LineItem>
                {
                    new LineItem
                    {
                        Id = Guid.NewGuid(),
                        Description = "Consulting Services",
                        Quantity = 10m,
                        UnitPrice = 125.00m
                    }
                }
            };
        }

        /// <summary>
        /// Creates a fully populated test payment with realistic data for use in
        /// payment publish method tests. Includes Id, InvoiceId, Amount, and PaymentDate.
        /// </summary>
        /// <returns>A populated <see cref="Payment"/> instance for testing.</returns>
        private static Payment CreateTestPayment()
        {
            return new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = Guid.NewGuid(),
                Amount = 500.00m,
                PaymentDate = DateTime.UtcNow
            };
        }

        // =====================================================================
        // Phase 2: PublishInvoiceCreatedAsync Tests
        // =====================================================================

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishInvoiceCreatedAsync"/>
        /// calls SNS PublishAsync with a message containing the correct event type
        /// string <c>"invoicing.invoice.created"</c> per AAP §0.8.5 naming convention.
        /// Replaces: <c>RecordHookManager.ExecutePostCreateRecordHooks(entityName, record)</c>
        /// </summary>
        [Fact]
        public async Task PublishInvoiceCreatedAsync_CallsSnsWithCorrectEventType()
        {
            // Arrange
            var testInvoice = CreateTestInvoice();
            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            // Act
            await _sut.PublishInvoiceCreatedAsync(testInvoice);

            // Assert — verify SNS was called with a message containing the correct event type
            _snsClientMock.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r => r.Message.Contains("invoicing.invoice.created")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that the SNS message body follows the standard domain event
        /// envelope structure with all required fields per AAP §0.8.5:
        /// eventType, eventId (valid GUID), timestamp (ISO 8601 UTC),
        /// correlationId (non-empty), source ("invoicing-service"), version (1),
        /// and payload (non-null invoice data).
        /// </summary>
        [Fact]
        public async Task PublishInvoiceCreatedAsync_EventEnvelopeHasCorrectStructure()
        {
            // Arrange
            var testInvoice = CreateTestInvoice();
            PublishRequest? capturedRequest = null;

            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PublishRequest, CancellationToken>((req, ct) => capturedRequest = req)
                .ReturnsAsync(new PublishResponse());

            // Act
            await _sut.PublishInvoiceCreatedAsync(testInvoice);

            // Assert — captured request is not null
            capturedRequest.Should().NotBeNull();

            // Parse the JSON message body to validate envelope structure
            using var jsonDoc = JsonDocument.Parse(capturedRequest!.Message);
            var root = jsonDoc.RootElement;

            // eventType must match the domain event naming convention
            root.GetProperty("eventType").GetString().Should().Be("invoicing.invoice.created");

            // eventId must be a valid GUID for consumer deduplication (AAP §0.8.5)
            var eventIdStr = root.GetProperty("eventId").GetString();
            eventIdStr.Should().NotBeNullOrEmpty();
            Guid.TryParse(eventIdStr, out _).Should().BeTrue();

            // timestamp must be a valid ISO 8601 UTC datetime
            var timestampStr = root.GetProperty("timestamp").GetString();
            timestampStr.Should().NotBeNullOrEmpty();
            DateTime.TryParse(timestampStr, out _).Should().BeTrue();

            // correlationId must be present for distributed tracing (AAP §0.8.5)
            var correlationId = root.GetProperty("correlationId").GetString();
            correlationId.Should().NotBeNullOrEmpty();

            // source identifies the originating service
            root.GetProperty("source").GetString().Should().Be("invoicing-service");

            // version is the event schema version
            root.GetProperty("version").GetInt32().Should().Be(1);

            // payload must contain the invoice data (not null)
            root.GetProperty("payload").ValueKind.Should().NotBe(JsonValueKind.Null);
        }

        /// <summary>
        /// Verifies that SNS message attributes are set correctly for subscription
        /// filtering per AAP §0.7.2. The attributes enable SQS subscription filter
        /// policies to route events to specific consumers.
        /// </summary>
        [Fact]
        public async Task PublishInvoiceCreatedAsync_SetsSnsMessageAttributes()
        {
            // Arrange
            var testInvoice = CreateTestInvoice();
            PublishRequest? capturedRequest = null;

            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PublishRequest, CancellationToken>((req, ct) => capturedRequest = req)
                .ReturnsAsync(new PublishResponse());

            // Act
            await _sut.PublishInvoiceCreatedAsync(testInvoice);

            // Assert — captured request has message attributes
            capturedRequest.Should().NotBeNull();
            capturedRequest!.MessageAttributes.Should().ContainKey("eventType");
            capturedRequest.MessageAttributes["eventType"].StringValue
                .Should().Be("invoicing.invoice.created");

            capturedRequest.MessageAttributes.Should().ContainKey("entityId");
            capturedRequest.MessageAttributes["entityId"].StringValue
                .Should().Be(testInvoice.Id.ToString());
        }

        // =====================================================================
        // Phase 3: Fire-and-Forget Behavior Tests (CRITICAL)
        // =====================================================================

        /// <summary>
        /// CRITICAL: Verifies fire-and-forget behavior — when SNS publishing fails,
        /// the exception must NOT propagate to the caller. This matches the source
        /// RecordManager.cs pattern where post-hooks (lines 770-775) are called AFTER
        /// persistence succeeds, and their failure does not roll back the record.
        /// The persisted invoice data is safe regardless of event publishing outcome.
        /// </summary>
        [Fact]
        public async Task PublishInvoiceCreatedAsync_SnsFailure_DoesNotThrow()
        {
            // Arrange — SNS throws an exception
            var testInvoice = CreateTestInvoice();
            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSimpleNotificationServiceException("SNS error"));

            // Act & Assert — calling publish must NOT throw
            Func<Task> act = async () => await _sut.PublishInvoiceCreatedAsync(testInvoice);
            await act.Should().NotThrowAsync();
        }

        /// <summary>
        /// Verifies that when SNS publishing fails, the error is logged at Error level.
        /// InvoiceEventPublisher catches SNS exceptions and logs them using structured
        /// logging with correlation-ID per AAP §0.8.5.
        /// </summary>
        [Fact]
        public async Task PublishInvoiceCreatedAsync_SnsFailure_LogsError()
        {
            // Arrange — SNS throws an exception
            var testInvoice = CreateTestInvoice();
            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSimpleNotificationServiceException("SNS error"));

            // Act
            await _sut.PublishInvoiceCreatedAsync(testInvoice);

            // Assert — verify logger was called at Error level
            // Using Moq's Verify with It.Is to match the LogError call pattern
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => true),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        // =====================================================================
        // Phase 5: Topic ARN Verification
        // =====================================================================

        /// <summary>
        /// Verifies that invoice events are published to the correct SNS topic ARN
        /// configured via the INVOICE_SNS_TOPIC_ARN environment variable.
        /// All invoice lifecycle events (created, updated, issued, paid, voided)
        /// share the same topic ARN.
        /// </summary>
        [Fact]
        public async Task PublishInvoiceCreatedAsync_UsesInvoiceTopicArn()
        {
            // Arrange
            var testInvoice = CreateTestInvoice();
            PublishRequest? capturedRequest = null;

            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PublishRequest, CancellationToken>((req, ct) => capturedRequest = req)
                .ReturnsAsync(new PublishResponse());

            // Act
            await _sut.PublishInvoiceCreatedAsync(testInvoice);

            // Assert — topic ARN matches the configured invoice topic
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TopicArn.Should().Be(InvoiceTopicArn);
        }

        // =====================================================================
        // Phase 4: All 6 Event Method Tests — Remaining Invoice Events
        // =====================================================================

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishInvoiceUpdatedAsync"/>
        /// calls SNS with event type <c>"invoicing.invoice.updated"</c>.
        /// Replaces: <c>RecordHookManager.ExecutePostUpdateRecordHooks(entityName, record)</c>
        /// </summary>
        [Fact]
        public async Task PublishInvoiceUpdatedAsync_CallsSnsWithCorrectEventType()
        {
            // Arrange
            var testInvoice = CreateTestInvoice();
            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            // Act
            await _sut.PublishInvoiceUpdatedAsync(testInvoice);

            // Assert
            _snsClientMock.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r => r.Message.Contains("invoicing.invoice.updated")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishInvoiceIssuedAsync"/>
        /// calls SNS with event type <c>"invoicing.invoice.issued"</c>.
        /// This event fires when an invoice transitions from Draft to Issued status.
        /// </summary>
        [Fact]
        public async Task PublishInvoiceIssuedAsync_CallsSnsWithCorrectEventType()
        {
            // Arrange
            var testInvoice = CreateTestInvoice();
            testInvoice.Status = InvoiceStatus.Issued;
            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            // Act
            await _sut.PublishInvoiceIssuedAsync(testInvoice);

            // Assert
            _snsClientMock.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r => r.Message.Contains("invoicing.invoice.issued")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishInvoicePaidAsync"/>
        /// calls SNS with event type <c>"invoicing.invoice.paid"</c>.
        /// Per AAP §0.7.2: Post-update hook for status change maps to domain event.
        /// </summary>
        [Fact]
        public async Task PublishInvoicePaidAsync_CallsSnsWithCorrectEventType()
        {
            // Arrange
            var testInvoice = CreateTestInvoice();
            testInvoice.Status = InvoiceStatus.Paid;
            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            // Act
            await _sut.PublishInvoicePaidAsync(testInvoice);

            // Assert
            _snsClientMock.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r => r.Message.Contains("invoicing.invoice.paid")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishInvoiceVoidedAsync"/>
        /// calls SNS with event type <c>"invoicing.invoice.voided"</c>.
        /// Replaces: <c>RecordHookManager.ExecutePostDeleteRecordHooks(entityName, record)</c>
        /// </summary>
        [Fact]
        public async Task PublishInvoiceVoidedAsync_CallsSnsWithCorrectEventType()
        {
            // Arrange
            var testInvoice = CreateTestInvoice();
            testInvoice.Status = InvoiceStatus.Voided;
            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            // Act
            await _sut.PublishInvoiceVoidedAsync(testInvoice);

            // Assert
            _snsClientMock.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r => r.Message.Contains("invoicing.invoice.voided")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // =====================================================================
        // Phase 4: Payment Event Tests
        // =====================================================================

        /// <summary>
        /// Verifies that <see cref="InvoiceEventPublisher.PublishPaymentProcessedAsync"/>
        /// calls SNS with event type <c>"invoicing.payment.processed"</c>.
        /// This event fires when a payment is recorded against an invoice.
        /// Uses the separate PAYMENT_SNS_TOPIC_ARN topic.
        /// </summary>
        [Fact]
        public async Task PublishPaymentProcessedAsync_CallsSnsWithCorrectEventType()
        {
            // Arrange
            var testPayment = CreateTestPayment();
            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            // Act
            await _sut.PublishPaymentProcessedAsync(testPayment);

            // Assert
            _snsClientMock.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r => r.Message.Contains("invoicing.payment.processed")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// CRITICAL: Verifies fire-and-forget behavior for payment events —
        /// when SNS publishing fails, the exception must NOT propagate.
        /// The persisted payment data is safe regardless of event outcome.
        /// </summary>
        [Fact]
        public async Task PublishPaymentProcessedAsync_SnsFailure_DoesNotThrow()
        {
            // Arrange — SNS throws an exception
            var testPayment = CreateTestPayment();
            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSimpleNotificationServiceException("SNS error"));

            // Act & Assert — calling publish must NOT throw
            Func<Task> act = async () => await _sut.PublishPaymentProcessedAsync(testPayment);
            await act.Should().NotThrowAsync();
        }

        /// <summary>
        /// Verifies that payment events are published to the correct SNS topic ARN
        /// configured via the PAYMENT_SNS_TOPIC_ARN environment variable.
        /// Payment events use a separate topic from invoice lifecycle events.
        /// </summary>
        [Fact]
        public async Task PublishPaymentProcessedAsync_UsesPaymentTopicArn()
        {
            // Arrange
            var testPayment = CreateTestPayment();
            PublishRequest? capturedRequest = null;

            _snsClientMock
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PublishRequest, CancellationToken>((req, ct) => capturedRequest = req)
                .ReturnsAsync(new PublishResponse());

            // Act
            await _sut.PublishPaymentProcessedAsync(testPayment);

            // Assert — topic ARN matches the configured payment topic
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TopicArn.Should().Be(PaymentTopicArn);
        }
    }
}
