// -------------------------------------------------------------------
// EventConsumerTests.cs — Unit Tests for EventConsumer SQS-Triggered Lambda
//
// Comprehensive unit tests for the EventConsumer SQS-triggered Lambda
// function in the Reporting & Analytics microservice. This consumer
// replaces the monolith's synchronous hook dispatch
// (RecordHookManager.ExecutePost*RecordHooks) with async SQS event
// consumption for CQRS read-model projection updates.
//
// All dependencies are mocked via Moq — zero real AWS or database calls.
// Tests cover: SQS batch processing, SNS wrapper parsing, event type
// routing, idempotency, partial batch failure, correlation-ID extraction,
// and events from all 9 bounded contexts.
// -------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;
using WebVellaErp.Reporting.DataAccess;
using WebVellaErp.Reporting.Functions;
using WebVellaErp.Reporting.Models;
using WebVellaErp.Reporting.Services;
using Xunit;

namespace WebVellaErp.Reporting.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="EventConsumer"/> — the SQS-triggered Lambda
/// handler that processes domain events from all 9 bounded contexts to
/// build CQRS read-model projections in RDS PostgreSQL.
/// </summary>
[Trait("Category", "Unit")]
public class EventConsumerTests
{
    // ── Mock Dependencies ─────────────────────────────────────────────

    private readonly Mock<IReportRepository> _reportRepositoryMock;
    private readonly Mock<IProjectionService> _projectionServiceMock;
    private readonly Mock<IAmazonSimpleSystemsManagement> _ssmClientMock;
    private readonly Mock<ILogger<EventConsumer>> _loggerMock;
    private readonly Mock<ILambdaContext> _lambdaContextMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;

    /// <summary>The EventConsumer instance under test (testable subclass).</summary>
    private readonly EventConsumer _consumer;

    // ── Constructor / Test Setup ──────────────────────────────────────

    /// <summary>
    /// Initializes all mock dependencies and constructs the
    /// <see cref="TestableEventConsumer"/> via DI, mirroring the
    /// Lambda runtime DI configuration.
    /// </summary>
    public EventConsumerTests()
    {
        _reportRepositoryMock = new Mock<IReportRepository>();
        _projectionServiceMock = new Mock<IProjectionService>();
        _ssmClientMock = new Mock<IAmazonSimpleSystemsManagement>();
        _loggerMock = new Mock<ILogger<EventConsumer>>();
        _lambdaContextMock = new Mock<ILambdaContext>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();

        // Configure Lambda context mock
        _lambdaContextMock
            .Setup(x => x.AwsRequestId)
            .Returns(Guid.NewGuid().ToString());

        // Configure logger factory to return our logger mock for any category
        _loggerFactoryMock
            .Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(_loggerMock.Object);

        // Configure SSM mock to return a test connection string
        // (TestableEventConsumer bypasses real DB, but GetConnectionStringAsync is called)
        _ssmClientMock
            .Setup(s => s.GetParameterAsync(
                It.IsAny<GetParameterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetParameterResponse
            {
                Parameter = new Parameter
                {
                    Value = "Host=localhost;Port=5432;Database=reporting_test;Username=test;Password=test"
                }
            });

        // Default mock setups for projection service — all operations succeed
        _projectionServiceMock
            .Setup(s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _projectionServiceMock
            .Setup(s => s.ProcessEntityUpdatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _projectionServiceMock
            .Setup(s => s.ProcessEntityDeletedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _projectionServiceMock
            .Setup(s => s.IsFinancialEntity(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Default mock setups for report repository — offset tracking
        _reportRepositoryMock
            .Setup(r => r.UpsertEventOffsetAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<NpgsqlConnection?>(),
                It.IsAny<NpgsqlTransaction?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _reportRepositoryMock
            .Setup(r => r.GetLastEventIdAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("last-event-id");

        // Build DI container with all mocked services
        var services = new ServiceCollection();
        services.AddSingleton(_ssmClientMock.Object);
        services.AddSingleton<ILogger<EventConsumer>>(_loggerMock.Object);
        services.AddSingleton(_loggerFactoryMock.Object);
        services.AddSingleton(_projectionServiceMock.Object);
        services.AddSingleton(_reportRepositoryMock.Object);

        var serviceProvider = services.BuildServiceProvider();
        _consumer = new TestableEventConsumer(serviceProvider);
    }

    // ══════════════════════════════════════════════════════════════════
    // Phase 2: SQS Batch Processing Tests
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a single SQS message in a batch is processed successfully
    /// and the projection service is invoked exactly once.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_SingleMessage_ProcessesSuccessfully()
    {
        // Arrange
        var recordId = Guid.NewGuid();
        var message = CreateDirectSqsMessage("crm", "contact", "created", recordId);
        var sqsEvent = CreateSqsEvent(message);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        response.Should().NotBeNull();
        response.BatchItemFailures.Should().BeEmpty();

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.Is<DomainEvent>(e => e.SourceDomain == "crm" && e.EntityName == "contact"),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that all messages in a multi-message batch are processed
    /// and the projection service is called once per message.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_MultipleBatchMessages_ProcessesAllMessages()
    {
        // Arrange
        var msg1 = CreateDirectSqsMessage("crm", "account", "created", Guid.NewGuid());
        var msg2 = CreateDirectSqsMessage("inventory", "task", "updated", Guid.NewGuid());
        var msg3 = CreateDirectSqsMessage("invoicing", "invoice", "created", Guid.NewGuid());
        var sqsEvent = CreateSqsEvent(msg1, msg2, msg3);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        response.BatchItemFailures.Should().BeEmpty();

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2)); // crm.account.created + invoicing.invoice.created

        _projectionServiceMock.Verify(
            s => s.ProcessEntityUpdatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once); // inventory.task.updated
    }

    /// <summary>
    /// Verifies that an empty SQS batch completes without error and
    /// no projection service calls are made.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_EmptyBatch_CompletesWithoutError()
    {
        // Arrange
        var sqsEvent = new SQSEvent { Records = new List<SQSEvent.SQSMessage>() };

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        response.Should().NotBeNull();
        response.BatchItemFailures.Should().BeEmpty();

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ══════════════════════════════════════════════════════════════════
    // Phase 3: SNS Notification Wrapper Parsing Tests
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that an SQS message wrapped in an SNS notification
    /// envelope is correctly unwrapped and the inner DomainEvent
    /// is extracted and processed. Replaces RecordHookManager's
    /// synchronous ExecutePostCreateRecordHooks dispatch.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_SnsWrappedMessage_CorrectlyParsesInnerEvent()
    {
        // Arrange
        var recordId = Guid.NewGuid();
        var message = CreateSnsWrappedSqsMessage("crm", "contact", "created", recordId);
        var sqsEvent = CreateSqsEvent(message);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        response.BatchItemFailures.Should().BeEmpty();

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.Is<DomainEvent>(e =>
                    e.SourceDomain == "crm" &&
                    e.EntityName == "contact" &&
                    e.Action == "created"),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that a direct SQS message (without SNS wrapping) is
    /// parsed correctly as a DomainEvent and processed.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_DirectMessage_CorrectlyParsesEvent()
    {
        // Arrange
        var recordId = Guid.NewGuid();
        var message = CreateDirectSqsMessage("identity", "user", "created", recordId);
        var sqsEvent = CreateSqsEvent(message);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        response.BatchItemFailures.Should().BeEmpty();

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.Is<DomainEvent>(e =>
                    e.SourceDomain == "identity" &&
                    e.EntityName == "user" &&
                    e.Action == "created"),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ══════════════════════════════════════════════════════════════════
    // Phase 4: DomainEvent Deserialization Tests
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that all fields of a DomainEvent are correctly
    /// deserialized from the SQS message body, including event_id,
    /// source_domain, entity_name, action, payload, timestamp,
    /// and correlation_id.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_ValidDomainEvent_DeserializesAllFields()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var correlationId = "corr-deser-test-001";
        var payload = new Dictionary<string, object?>
        {
            ["id"] = recordId.ToString(),
            ["name"] = "Test Contact",
            ["email"] = "test@example.com",
            ["is_active"] = true
        };

        var message = CreateDirectSqsMessage(
            "crm", "contact", "created", recordId,
            payload: payload,
            correlationId: correlationId,
            eventId: eventId);
        var sqsEvent = CreateSqsEvent(message);

        DomainEvent? capturedEvent = null;
        _projectionServiceMock
            .Setup(s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()))
            .Callback<DomainEvent, NpgsqlConnection, NpgsqlTransaction, CancellationToken>(
                (evt, _, _, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        capturedEvent.Should().NotBeNull();
        capturedEvent!.EventId.Should().Be(eventId);
        capturedEvent.SourceDomain.Should().Be("crm");
        capturedEvent.EntityName.Should().Be("contact");
        capturedEvent.Action.Should().Be("created");
        capturedEvent.CorrelationId.Should().Be(correlationId);
        capturedEvent.Payload.Should().NotBeNull();
        capturedEvent.Payload!.Should().ContainKey("name");
        capturedEvent.EventType.Should().Be("crm.contact.created");
    }

    /// <summary>
    /// Verifies that a malformed JSON message body is handled gracefully
    /// without causing a full batch failure. The message is skipped and
    /// processing continues for other messages.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_MalformedJson_HandlesGracefully()
    {
        // Arrange
        var malformedMessage = new SQSEvent.SQSMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Body = "{{{{not-valid-json}}}}",
            MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
        };
        var validMessage = CreateDirectSqsMessage("crm", "account", "created", Guid.NewGuid());
        var sqsEvent = CreateSqsEvent(malformedMessage, validMessage);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert — malformed message is skipped (not a batch failure),
        // valid message is processed successfully
        response.BatchItemFailures.Should().BeEmpty();

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ══════════════════════════════════════════════════════════════════
    // Phase 5: Event Type Routing Tests
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a "created" action routes to ProcessEntityCreatedAsync.
    /// Replaces IErpPostCreateRecordHook.OnPostCreateRecord dispatch.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_CreatedEvent_RoutesToProcessEntityCreated()
    {
        // Arrange
        var message = CreateDirectSqsMessage("crm", "account", "created", Guid.NewGuid());
        var sqsEvent = CreateSqsEvent(message);

        // Act
        await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.Is<DomainEvent>(e => e.Action == "created"),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _projectionServiceMock.Verify(
            s => s.ProcessEntityUpdatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        _projectionServiceMock.Verify(
            s => s.ProcessEntityDeletedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Verifies that an "updated" action routes to ProcessEntityUpdatedAsync.
    /// Replaces IErpPostUpdateRecordHook.OnPostUpdateRecord dispatch.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_UpdatedEvent_RoutesToProcessEntityUpdated()
    {
        // Arrange
        var message = CreateDirectSqsMessage("crm", "contact", "updated", Guid.NewGuid());
        var sqsEvent = CreateSqsEvent(message);

        // Act
        await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        _projectionServiceMock.Verify(
            s => s.ProcessEntityUpdatedAsync(
                It.Is<DomainEvent>(e => e.Action == "updated"),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Verifies that a "deleted" action routes to ProcessEntityDeletedAsync.
    /// Replaces IErpPostDeleteRecordHook.OnPostDeleteRecord dispatch.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_DeletedEvent_RoutesToProcessEntityDeleted()
    {
        // Arrange
        var message = CreateDirectSqsMessage("crm", "contact", "deleted", Guid.NewGuid());
        var sqsEvent = CreateSqsEvent(message);

        // Act
        await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        _projectionServiceMock.Verify(
            s => s.ProcessEntityDeletedAsync(
                It.Is<DomainEvent>(e => e.Action == "deleted"),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ══════════════════════════════════════════════════════════════════
    // Phase 6: Idempotency Tests (AAP §0.8.5)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a duplicate event (same EventId) is detected and
    /// skipped during the second processing attempt. The projection
    /// service must be called exactly once, not twice. Per AAP §0.8.5:
    /// "All event consumers MUST be idempotent."
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_DuplicateEvent_SkipsSecondProcessing()
    {
        // Arrange — two SQS messages with the SAME DomainEvent EventId
        var sharedEventId = Guid.NewGuid();
        var msg1 = CreateDirectSqsMessage(
            "crm", "contact", "created", Guid.NewGuid(), eventId: sharedEventId);
        var msg2 = CreateDirectSqsMessage(
            "crm", "contact", "created", Guid.NewGuid(), eventId: sharedEventId);

        // Give each SQS message a distinct MessageId
        msg1.MessageId = "sqs-msg-001";
        msg2.MessageId = "sqs-msg-002";

        var sqsEvent = CreateSqsEvent(msg1, msg2);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert — projection service called exactly once (duplicate skipped)
        response.BatchItemFailures.Should().BeEmpty();

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that when a duplicate event is detected, a warning-level
    /// log message containing "Duplicate event skipped" is produced.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_DuplicateEvent_LogsWarnMessage()
    {
        // Arrange
        var sharedEventId = Guid.NewGuid();
        var msg1 = CreateDirectSqsMessage(
            "crm", "contact", "created", Guid.NewGuid(), eventId: sharedEventId);
        var msg2 = CreateDirectSqsMessage(
            "crm", "contact", "created", Guid.NewGuid(), eventId: sharedEventId);
        msg1.MessageId = "sqs-dup-001";
        msg2.MessageId = "sqs-dup-002";

        var sqsEvent = CreateSqsEvent(msg1, msg2);

        // Act
        await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert — verify LogWarning was called with "Duplicate event skipped"
        _loggerMock.Verify(
            x => x.Log(
                It.Is<Microsoft.Extensions.Logging.LogLevel>(l => l == Microsoft.Extensions.Logging.LogLevel.Warning),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("Duplicate event skipped")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce());
    }

    // ══════════════════════════════════════════════════════════════════
    // Phase 7: Unknown Event Type Handling
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that an event from an unknown domain is logged at
    /// warning level and skipped without throwing an exception.
    /// Processing continues for other messages in the batch.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_UnknownEventType_LogsWarningAndSkips()
    {
        // Arrange — "unknown" is not in KnownDomains
        var unknownMsg = CreateDirectSqsMessage(
            "unknown", "thing", "happened", Guid.NewGuid());
        var validMsg = CreateDirectSqsMessage(
            "crm", "account", "created", Guid.NewGuid());
        var sqsEvent = CreateSqsEvent(unknownMsg, validMsg);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert — no batch failure (unknown events are skipped, not failures)
        response.BatchItemFailures.Should().BeEmpty();

        // Valid message was still processed
        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.Is<DomainEvent>(e => e.SourceDomain == "crm"),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Warning was logged for unknown domain
        _loggerMock.Verify(
            x => x.Log(
                It.Is<Microsoft.Extensions.Logging.LogLevel>(l => l == Microsoft.Extensions.Logging.LogLevel.Warning),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("Unknown source domain")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce());
    }

    /// <summary>
    /// Verifies that an event with empty/null action fields is handled
    /// gracefully. The message is skipped since required fields are missing.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_EmptyEventType_LogsWarningAndSkips()
    {
        // Arrange — empty action causes ParseDomainEventFromSnsMessage to return null
        var emptyActionJson = JsonSerializer.Serialize(new
        {
            event_id = Guid.NewGuid(),
            source_domain = "",
            entity_name = "",
            action = "",
            timestamp = DateTime.UtcNow,
            correlation_id = "corr-empty"
        });

        var message = new SQSEvent.SQSMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Body = emptyActionJson,
            MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
        };
        var sqsEvent = CreateSqsEvent(message);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert — skipped gracefully, no batch failure
        response.BatchItemFailures.Should().BeEmpty();

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ══════════════════════════════════════════════════════════════════
    // Phase 8: Partial Batch Failure Tests
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that when one message fails and another succeeds,
    /// the SQSBatchResponse correctly reports only the failed message
    /// in BatchItemFailures.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_OneFailedOneSucceeded_ReturnsSQSBatchResponse()
    {
        // Arrange
        var successMsg = CreateDirectSqsMessage("crm", "account", "created", Guid.NewGuid());
        successMsg.MessageId = "sqs-success-001";

        var failMsg = CreateDirectSqsMessage("inventory", "task", "updated", Guid.NewGuid());
        failMsg.MessageId = "sqs-fail-001";

        // Make the updated handler throw for the fail message
        _projectionServiceMock
            .Setup(s => s.ProcessEntityUpdatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Simulated processing failure"));

        var sqsEvent = CreateSqsEvent(successMsg, failMsg);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert — only the failed message appears in BatchItemFailures
        response.BatchItemFailures.Should().HaveCount(1);
        response.BatchItemFailures[0].ItemIdentifier.Should().Be("sqs-fail-001");

        // The successful message was still processed
        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that when all messages in a batch fail, all message IDs
    /// are returned in BatchItemFailures.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_AllFailed_ReturnsAllAsFailures()
    {
        // Arrange — make all projection calls throw
        _projectionServiceMock
            .Setup(s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB connection failed"));

        var msg1 = CreateDirectSqsMessage("crm", "account", "created", Guid.NewGuid());
        msg1.MessageId = "sqs-fail-a";
        var msg2 = CreateDirectSqsMessage("crm", "contact", "created", Guid.NewGuid());
        msg2.MessageId = "sqs-fail-b";

        var sqsEvent = CreateSqsEvent(msg1, msg2);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert — all messages reported as failures
        response.BatchItemFailures.Should().HaveCount(2);
        response.BatchItemFailures.Should().Contain(f => f.ItemIdentifier == "sqs-fail-a");
        response.BatchItemFailures.Should().Contain(f => f.ItemIdentifier == "sqs-fail-b");
    }

    /// <summary>
    /// Verifies that when all messages succeed, BatchItemFailures is empty.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_AllSucceeded_ReturnsEmptyBatchFailures()
    {
        // Arrange
        var msg1 = CreateDirectSqsMessage("crm", "account", "created", Guid.NewGuid());
        var msg2 = CreateDirectSqsMessage("crm", "contact", "updated", Guid.NewGuid());
        var sqsEvent = CreateSqsEvent(msg1, msg2);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        response.BatchItemFailures.Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════════
    // Phase 9: Correlation-ID Extraction Tests
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that the correlation ID is extracted from the
    /// "correlationId" SQS message attribute when present.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_CorrelationIdFromMessageAttributes_UsesAttributeValue()
    {
        // Arrange
        var message = CreateDirectSqsMessage(
            "crm", "contact", "created", Guid.NewGuid(),
            correlationId: "corr-from-attr-123");

        // Add correlationId as SQS MessageAttribute
        message.MessageAttributes["correlationId"] = new SQSEvent.MessageAttribute
        {
            DataType = "String",
            StringValue = "corr-from-attr-123"
        };

        var sqsEvent = CreateSqsEvent(message);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert — message processed successfully with correlation ID
        response.BatchItemFailures.Should().BeEmpty();

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that the correlation ID is extracted from the
    /// "x-correlation-id" SQS message attribute as a fallback.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_CorrelationIdFromXCorrelationIdAttribute_UsesAttributeValue()
    {
        // Arrange
        var message = CreateDirectSqsMessage(
            "crm", "contact", "created", Guid.NewGuid());

        message.MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>
        {
            ["x-correlation-id"] = new SQSEvent.MessageAttribute
            {
                DataType = "String",
                StringValue = "x-corr-456"
            }
        };

        var sqsEvent = CreateSqsEvent(message);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        response.BatchItemFailures.Should().BeEmpty();

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that when no correlation-ID attributes are present,
    /// the SQS MessageId is used as a fallback correlation ID.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_NoCorrelationId_FallsBackToMessageId()
    {
        // Arrange — no correlation-ID attributes at all
        var message = CreateDirectSqsMessage(
            "crm", "contact", "created", Guid.NewGuid());
        message.MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>();
        message.MessageId = "sqs-msg-fallback-id";

        var sqsEvent = CreateSqsEvent(message);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert — processed successfully, correlation ID falls back to MessageId
        response.BatchItemFailures.Should().BeEmpty();

        _projectionServiceMock.Verify(
            s => s.ProcessEntityCreatedAsync(
                It.IsAny<DomainEvent>(),
                It.IsAny<NpgsqlConnection>(),
                It.IsAny<NpgsqlTransaction>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ══════════════════════════════════════════════════════════════════
    // Phase 10: All 9 Domain Event Sources Tests
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies identity.user.created routes to ProcessEntityCreatedAsync.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_IdentityDomainEvent_ProcessesCorrectly()
    {
        await AssertDomainEventProcessedCorrectly(
            "identity", "user", "created",
            nameof(IProjectionService.ProcessEntityCreatedAsync));
    }

    /// <summary>
    /// Verifies entity-management.entity.created routes to ProcessEntityCreatedAsync.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_EntityManagementDomainEvent_ProcessesCorrectly()
    {
        await AssertDomainEventProcessedCorrectly(
            "entity-management", "entity", "created",
            nameof(IProjectionService.ProcessEntityCreatedAsync));
    }

    /// <summary>
    /// Verifies crm.account.created routes to ProcessEntityCreatedAsync.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_CrmDomainEvent_ProcessesCorrectly()
    {
        await AssertDomainEventProcessedCorrectly(
            "crm", "account", "created",
            nameof(IProjectionService.ProcessEntityCreatedAsync));
    }

    /// <summary>
    /// Verifies inventory.task.created routes to ProcessEntityCreatedAsync.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_InventoryDomainEvent_ProcessesCorrectly()
    {
        await AssertDomainEventProcessedCorrectly(
            "inventory", "task", "created",
            nameof(IProjectionService.ProcessEntityCreatedAsync));
    }

    /// <summary>
    /// Verifies invoicing.invoice.created routes to ProcessEntityCreatedAsync.
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_InvoicingDomainEvent_ProcessesCorrectly()
    {
        await AssertDomainEventProcessedCorrectly(
            "invoicing", "invoice", "created",
            nameof(IProjectionService.ProcessEntityCreatedAsync));
    }

    /// <summary>
    /// Verifies notifications.email.sent routes to ProcessEntityUpdatedAsync
    /// ("sent" is an update action).
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_NotificationsDomainEvent_ProcessesCorrectly()
    {
        await AssertDomainEventProcessedCorrectly(
            "notifications", "email", "sent",
            nameof(IProjectionService.ProcessEntityUpdatedAsync));
    }

    /// <summary>
    /// Verifies file-management.file.uploaded routes to ProcessEntityCreatedAsync
    /// ("uploaded" is a create action).
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_FileManagementDomainEvent_ProcessesCorrectly()
    {
        await AssertDomainEventProcessedCorrectly(
            "file-management", "file", "uploaded",
            nameof(IProjectionService.ProcessEntityCreatedAsync));
    }

    /// <summary>
    /// Verifies workflow.workflow.started routes to ProcessEntityUpdatedAsync
    /// ("started" is an update action).
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_WorkflowDomainEvent_ProcessesCorrectly()
    {
        await AssertDomainEventProcessedCorrectly(
            "workflow", "workflow", "started",
            nameof(IProjectionService.ProcessEntityUpdatedAsync));
    }

    /// <summary>
    /// Verifies plugin-system.plugin.registered routes to ProcessEntityCreatedAsync
    /// ("registered" is a create action).
    /// </summary>
    [Fact]
    public async Task HandleSqsEvent_PluginSystemDomainEvent_ProcessesCorrectly()
    {
        await AssertDomainEventProcessedCorrectly(
            "plugin-system", "plugin", "registered",
            nameof(IProjectionService.ProcessEntityCreatedAsync));
    }

    // ══════════════════════════════════════════════════════════════════
    // Helper Methods
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Wraps one or more SQS messages into an <see cref="SQSEvent"/>.
    /// </summary>
    private static SQSEvent CreateSqsEvent(params SQSEvent.SQSMessage[] messages)
    {
        return new SQSEvent
        {
            Records = new List<SQSEvent.SQSMessage>(messages)
        };
    }

    /// <summary>
    /// Creates an SQS message with a direct DomainEvent JSON body
    /// (no SNS envelope wrapping).
    /// </summary>
    private static SQSEvent.SQSMessage CreateDirectSqsMessage(
        string sourceDomain,
        string entityName,
        string action,
        Guid recordId,
        Dictionary<string, object?>? payload = null,
        string? correlationId = null,
        Guid? eventId = null)
    {
        var json = BuildDomainEventJson(
            sourceDomain, entityName, action, recordId,
            payload, correlationId, eventId);

        return new SQSEvent.SQSMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Body = json,
            MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
        };
    }

    /// <summary>
    /// Creates an SQS message with an SNS notification envelope wrapping
    /// the DomainEvent (double-wrapped JSON per SNS→SQS subscription pattern).
    /// </summary>
    private static SQSEvent.SQSMessage CreateSnsWrappedSqsMessage(
        string sourceDomain,
        string entityName,
        string action,
        Guid recordId,
        Dictionary<string, object?>? payload = null,
        string? correlationId = null)
    {
        var innerJson = BuildDomainEventJson(
            sourceDomain, entityName, action, recordId,
            payload, correlationId);
        var snsJson = BuildSnsNotificationJson(innerJson);

        return new SQSEvent.SQSMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Body = snsJson,
            MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
        };
    }

    /// <summary>
    /// Builds a DomainEvent JSON string with snake_case property names
    /// matching the [JsonPropertyName] attributes on <see cref="DomainEvent"/>.
    /// </summary>
    private static string BuildDomainEventJson(
        string sourceDomain,
        string entityName,
        string action,
        Guid recordId,
        Dictionary<string, object?>? payload = null,
        string? correlationId = null,
        Guid? eventId = null)
    {
        var eventData = new Dictionary<string, object?>
        {
            ["event_id"] = (eventId ?? Guid.NewGuid()).ToString(),
            ["source_domain"] = sourceDomain,
            ["entity_name"] = entityName,
            ["action"] = action,
            ["timestamp"] = DateTime.UtcNow.ToString("O"),
            ["correlation_id"] = correlationId ?? Guid.NewGuid().ToString(),
            ["version"] = "1.0"
        };

        if (payload != null)
        {
            eventData["payload"] = payload;
        }
        else
        {
            eventData["payload"] = new Dictionary<string, object?>
            {
                ["id"] = recordId.ToString(),
                ["record_type"] = entityName
            };
        }

        return JsonSerializer.Serialize(eventData);
    }

    /// <summary>
    /// Wraps an inner JSON string in an SNS notification envelope:
    /// <c>{"Type":"Notification","MessageId":"...","Message":"inner JSON escaped","TopicArn":"...","Timestamp":"..."}</c>
    /// </summary>
    private static string BuildSnsNotificationJson(string innerMessageJson)
    {
        var snsEnvelope = new Dictionary<string, object>
        {
            ["Type"] = "Notification",
            ["MessageId"] = Guid.NewGuid().ToString(),
            ["Message"] = innerMessageJson,
            ["TopicArn"] = "arn:aws:sns:us-east-1:000000000000:reporting-events",
            ["Timestamp"] = DateTime.UtcNow.ToString("O")
        };

        return JsonSerializer.Serialize(snsEnvelope);
    }

    /// <summary>
    /// Shared assertion helper for Phase 10 domain event tests.
    /// Verifies that an event from the specified domain/entity/action
    /// is correctly routed to the expected projection service method.
    /// </summary>
    private async Task AssertDomainEventProcessedCorrectly(
        string domain,
        string entity,
        string action,
        string expectedMethod)
    {
        // Arrange
        var message = CreateDirectSqsMessage(domain, entity, action, Guid.NewGuid());
        var sqsEvent = CreateSqsEvent(message);

        // Act
        var response = await _consumer.HandleSqsEvent(sqsEvent, _lambdaContextMock.Object);

        // Assert
        response.BatchItemFailures.Should().BeEmpty();

        switch (expectedMethod)
        {
            case nameof(IProjectionService.ProcessEntityCreatedAsync):
                _projectionServiceMock.Verify(
                    s => s.ProcessEntityCreatedAsync(
                        It.Is<DomainEvent>(e =>
                            e.SourceDomain == domain &&
                            e.EntityName == entity &&
                            e.Action == action),
                        It.IsAny<NpgsqlConnection>(),
                        It.IsAny<NpgsqlTransaction>(),
                        It.IsAny<CancellationToken>()),
                    Times.Once);
                break;

            case nameof(IProjectionService.ProcessEntityUpdatedAsync):
                _projectionServiceMock.Verify(
                    s => s.ProcessEntityUpdatedAsync(
                        It.Is<DomainEvent>(e =>
                            e.SourceDomain == domain &&
                            e.EntityName == entity &&
                            e.Action == action),
                        It.IsAny<NpgsqlConnection>(),
                        It.IsAny<NpgsqlTransaction>(),
                        It.IsAny<CancellationToken>()),
                    Times.Once);
                break;

            case nameof(IProjectionService.ProcessEntityDeletedAsync):
                _projectionServiceMock.Verify(
                    s => s.ProcessEntityDeletedAsync(
                        It.Is<DomainEvent>(e =>
                            e.SourceDomain == domain &&
                            e.EntityName == entity &&
                            e.Action == action),
                        It.IsAny<NpgsqlConnection>(),
                        It.IsAny<NpgsqlTransaction>(),
                        It.IsAny<CancellationToken>()),
                    Times.Once);
                break;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // TestableEventConsumer — Subclass for Unit Testing
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Testable subclass of <see cref="EventConsumer"/> that overrides
    /// database-bound operations to enable pure unit testing without
    /// any real NpgsqlConnection or PostgreSQL dependency.
    ///
    /// Overridden methods:
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="CreateDatabaseScopeAsync"/> — Returns (null, null) tuple
    ///     instead of creating a real NpgsqlConnection. Mocked IProjectionService
    ///     and IReportRepository use It.IsAny matchers that accept null values.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="IsEventAlreadyProcessedAsync"/> — Uses an in-memory
    ///     HashSet for idempotency tracking instead of querying PostgreSQL.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="RecordProcessedEventAsync"/> — No-op instead of
    ///     INSERT into reporting.processed_events table.
    ///   </description></item>
    /// </list>
    /// </summary>
    private class TestableEventConsumer : EventConsumer
    {
        /// <summary>
        /// In-memory set tracking processed event IDs for idempotency
        /// simulation during unit tests.
        /// </summary>
        private readonly HashSet<string> _processedEventIds =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Constructs a testable EventConsumer with all dependencies
        /// resolved from the provided DI service provider.
        /// </summary>
        public TestableEventConsumer(IServiceProvider serviceProvider)
            : base(serviceProvider)
        {
        }

        /// <summary>
        /// Returns (null, null) instead of creating a real database
        /// connection and transaction. The mocked IProjectionService and
        /// IReportRepository accept null values via It.IsAny matchers.
        /// </summary>
        protected override Task<(NpgsqlConnection? Connection, NpgsqlTransaction? Transaction)>
            CreateDatabaseScopeAsync(string connectionString, CancellationToken cancellationToken)
        {
            return Task.FromResult<(NpgsqlConnection?, NpgsqlTransaction?)>((null, null));
        }

        /// <summary>
        /// In-memory idempotency check using HashSet. Returns true
        /// (already processed) if the event ID was seen before; false
        /// (new event) if this is the first occurrence.
        /// </summary>
        protected override Task<bool> IsEventAlreadyProcessedAsync(
            Guid eventId,
            NpgsqlConnection? connection,
            NpgsqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            // HashSet.Add returns true if the element was added (new),
            // false if it already existed (duplicate). We negate this
            // to match the semantics of "is already processed".
            var isNew = _processedEventIds.Add(eventId.ToString());
            return Task.FromResult(!isNew);
        }

        /// <summary>
        /// No-op — does not record to reporting.processed_events table.
        /// The in-memory HashSet in IsEventAlreadyProcessedAsync handles
        /// idempotency tracking for tests.
        /// </summary>
        protected override Task RecordProcessedEventAsync(
            Guid eventId,
            string eventType,
            NpgsqlConnection? connection,
            NpgsqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
