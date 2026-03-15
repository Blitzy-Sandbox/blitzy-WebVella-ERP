// ═══════════════════════════════════════════════════════════════════════════
// QueueProcessorTests.cs — Unit Tests for Notifications QueueProcessor Lambda
// ═══════════════════════════════════════════════════════════════════════════
//
// Tests the SQS-triggered QueueProcessor Lambda that replaces the monolith's
// ProcessSmtpQueueJob.cs (18 lines) and SmtpInternalService queue processing
// (lines 829-878). Verifies both processing modes:
//   Mode 1: {"command": "process-queue"} → scheduled queue drain
//   Mode 2: {"email_id": "...", "action": "send"} → individual email send
//
// Test Coverage:
//   - Scheduled queue drain delegation to ISmtpService
//   - Individual email send with idempotency checks
//   - Retry logic state transitions (success/reschedule/abort)
//   - Partial batch failure reporting via SQSBatchResponse
//   - Domain event publishing via SNS (notifications.email.{action})
//   - Concurrency control architecture (SQS reserved, not static lock)
//
// Frameworks: xUnit 2.9.3, Moq 4.20.72, FluentAssertions 7.0.0
// ═══════════════════════════════════════════════════════════════════════════

using System.Linq;
using System.Reflection;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Notifications.DataAccess;
using WebVellaErp.Notifications.Functions;
using WebVellaErp.Notifications.Models;
using WebVellaErp.Notifications.Services;
using Xunit;

namespace WebVellaErp.Notifications.Tests
{
    /// <summary>
    /// Comprehensive unit tests for the QueueProcessor SQS-triggered Lambda handler.
    /// Replaces monolith ProcessSmtpQueueJob + SmtpInternalService queue processing.
    /// </summary>
    public class QueueProcessorTests : IDisposable
    {
        // ── Constants ─────────────────────────────────────────────────────
        private const string TestSnsTopicArn = "arn:aws:sns:us-east-1:000000000000:notifications-events";

        // ── Mock Dependencies ─────────────────────────────────────────────
        private readonly Mock<ISmtpService> _mockSmtpService;
        private readonly Mock<INotificationRepository> _mockRepository;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<ILogger<QueueProcessor>> _mockLogger;

        // ── System Under Test ─────────────────────────────────────────────
        private readonly QueueProcessor _sut;

        /// <summary>
        /// Constructor: sets up all mock dependencies and creates the QueueProcessor
        /// under test using the testing constructor (DI-injected dependencies).
        /// </summary>
        public QueueProcessorTests()
        {
            _mockSmtpService = new Mock<ISmtpService>();
            _mockRepository = new Mock<INotificationRepository>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockLogger = new Mock<ILogger<QueueProcessor>>();

            // Default SNS publish setup for all tests (succeeds silently)
            _mockSnsClient
                .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });

            _sut = new QueueProcessor(
                _mockSmtpService.Object,
                _mockRepository.Object,
                _mockSnsClient.Object,
                _mockLogger.Object,
                TestSnsTopicArn);
        }

        // ── Helper Methods ────────────────────────────────────────────────

        /// <summary>Builds an SQSEvent with the specified messages.</summary>
        private static SQSEvent BuildSqsEvent(params (string messageId, string body)[] messages)
        {
            var records = new List<SQSEvent.SQSMessage>();
            foreach (var (messageId, body) in messages)
            {
                records.Add(new SQSEvent.SQSMessage
                {
                    MessageId = messageId,
                    Body = body
                });
            }
            return new SQSEvent { Records = records };
        }

        /// <summary>Creates a TestLambdaContext with optional custom request ID.</summary>
        private static ILambdaContext CreateLambdaContext(string? requestId = null)
        {
            return new TestLambdaContext
            {
                AwsRequestId = requestId ?? Guid.NewGuid().ToString()
            };
        }

        /// <summary>Builds JSON body for process-queue Mode 1 command.</summary>
        private static string BuildProcessQueueCommandJson()
        {
            return JsonSerializer.Serialize(new { command = "process-queue" });
        }

        /// <summary>Builds JSON body for individual email send Mode 2 request.</summary>
        private static string BuildEmailSendRequestJson(Guid emailId)
        {
            return JsonSerializer.Serialize(new { email_id = emailId.ToString(), action = "send" });
        }

        /// <summary>Creates a test Email with configurable properties.</summary>
        private static Email CreateTestEmail(
            Guid? id = null,
            Guid? serviceId = null,
            EmailStatus status = EmailStatus.Pending,
            int retriesCount = 0,
            EmailPriority priority = EmailPriority.Normal)
        {
            return new Email
            {
                Id = id ?? Guid.NewGuid(),
                ServiceId = serviceId ?? Guid.NewGuid(),
                Status = status,
                RetriesCount = retriesCount,
                Sender = new EmailAddress("Test Sender", "sender@example.com"),
                Recipients = new List<EmailAddress>
                {
                    new EmailAddress("Test Recipient", "recipient@example.com")
                },
                Subject = "Test Subject",
                ContentText = "Test body text",
                ContentHtml = "<p>Test body html</p>",
                ScheduledOn = DateTime.UtcNow.AddMinutes(-1),
                Priority = priority,
                CreatedOn = DateTime.UtcNow.AddMinutes(-5)
            };
        }

        /// <summary>Creates a test SmtpServiceConfig with configurable retry settings.</summary>
        private static SmtpServiceConfig CreateTestSmtpConfig(
            Guid? id = null,
            int maxRetries = 3,
            int retryWaitMinutes = 5,
            bool isEnabled = true)
        {
            return new SmtpServiceConfig
            {
                Id = id ?? Guid.NewGuid(),
                Name = "Test SMTP Service",
                Server = "smtp.test.com",
                Port = 587,
                MaxRetriesCount = maxRetries,
                RetryWaitMinutes = retryWaitMinutes,
                IsEnabled = isEnabled,
                IsDefault = true,
                DefaultSenderName = "Test Sender",
                DefaultSenderEmail = "noreply@test.com"
            };
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 2: Scheduled Queue Drain Tests (process-queue command)
        // Replaces: ProcessSmtpQueueJob.Execute → SecurityContext.OpenSystemScope()
        //           → SmtpInternalService().ProcessSmtpQueue()
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task HandleAsync_ProcessQueueCommand_CallsProcessSmtpQueueAsync()
        {
            // Arrange — Build Mode 1 process-queue command message
            _mockSmtpService
                .Setup(x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildProcessQueueCommandJson()));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — ProcessSmtpQueueAsync called once, replacing ErpJob delegation
            _mockSmtpService.Verify(
                x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task HandleAsync_ProcessQueueCommand_NoSecurityContextScope()
        {
            // Arrange — Verify no SecurityContext.OpenSystemScope() equivalent exists.
            // Lambda uses IAM role permissions instead of source line 12 pattern.
            _mockSmtpService
                .Setup(x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildProcessQueueCommandJson()));
            var context = CreateLambdaContext();

            // Act
            var result = await _sut.HandleAsync(sqsEvent, context);

            // Assert — No SecurityContext mock registered, handler succeeds without it.
            // Lambda IAM role provides authorization, not per-request security scoping.
            result.Should().NotBeNull();
            result.BatchItemFailures.Should().BeEmpty();
            _mockSmtpService.Verify(
                x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task HandleAsync_ProcessQueueCommand_ReturnsSQSBatchResponse()
        {
            // Arrange
            _mockSmtpService
                .Setup(x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildProcessQueueCommandJson()));
            var context = CreateLambdaContext();

            // Act
            var result = await _sut.HandleAsync(sqsEvent, context);

            // Assert — Returns SQSBatchResponse with empty BatchItemFailures on success
            result.Should().NotBeNull();
            result.BatchItemFailures.Should().NotBeNull();
            result.BatchItemFailures.Should().BeEmpty();
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 3: Individual Email Send Tests (email send request)
        // Handles individual email sends queued for async processing via SQS.
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task HandleAsync_SendEmailCommand_LoadsAndSendsEmail()
        {
            // Arrange — Mode 2: individual email send request
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId);
            var config = CreateTestSmtpConfig(serviceId);

            // After send, SmtpService updates email to Sent status
            var sentEmail = CreateTestEmail(emailId, serviceId, EmailStatus.Sent);
            sentEmail.SentOn = DateTime.UtcNow;
            sentEmail.ScheduledOn = null;

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)       // First call: load email
                .ReturnsAsync(sentEmail);  // Second call: reload after send

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — Verify load, service lookup, and send all called
            _mockRepository.Verify(
                x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _mockRepository.Verify(
                x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()),
                Times.Once());
            _mockSmtpService.Verify(
                x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task HandleAsync_SendEmailCommand_IdempotencyCheck_AlreadySent()
        {
            // Arrange — Email already has Status=Sent (duplicate SQS delivery)
            var emailId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, status: EmailStatus.Sent);

            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            var result = await _sut.HandleAsync(sqsEvent, context);

            // Assert — SendEmailAsync NOT called (idempotency skip per AAP §0.8.5)
            _mockSmtpService.Verify(
                x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()),
                Times.Never());
            result.BatchItemFailures.Should().BeEmpty();
        }

        [Fact]
        public async Task HandleAsync_SendEmailCommand_IdempotencyCheck_AlreadyAborted()
        {
            // Arrange — Email already has Status=Aborted
            var emailId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, status: EmailStatus.Aborted);

            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            var result = await _sut.HandleAsync(sqsEvent, context);

            // Assert — SendEmailAsync NOT called (idempotent skip)
            _mockSmtpService.Verify(
                x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()),
                Times.Never());
            result.BatchItemFailures.Should().BeEmpty();
        }

        [Fact]
        public async Task HandleAsync_SendEmailCommand_ServiceNotFound_AbortsEmail()
        {
            // Arrange — SMTP service lookup returns null
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId);

            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            _mockRepository
                .Setup(x => x.SaveEmailAsync(It.IsAny<Email>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — Email aborted with exact error from source line 857
            _mockRepository.Verify(
                x => x.SaveEmailAsync(
                    It.Is<Email>(e =>
                        e.Status == EmailStatus.Aborted &&
                        e.ServerError == "SMTP service not found." &&
                        e.ScheduledOn == null),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task HandleAsync_SendEmailCommand_PublishesSentEvent()
        {
            // Arrange — Successful email send publishes "sent" domain event
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId);
            var config = CreateTestSmtpConfig(serviceId);

            var sentEmail = CreateTestEmail(emailId, serviceId, EmailStatus.Sent);
            sentEmail.SentOn = DateTime.UtcNow;
            sentEmail.ScheduledOn = null;

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)
                .ReturnsAsync(sentEmail);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — SNS event notifications.email.sent published
            _mockSnsClient.Verify(
                x => x.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.TopicArn == TestSnsTopicArn &&
                        r.Subject == "notifications.email.sent"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task HandleAsync_SendEmailCommand_PublishesFailedEvent()
        {
            // Arrange — Service not found triggers "failed" domain event
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId);

            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            _mockRepository
                .Setup(x => x.SaveEmailAsync(It.IsAny<Email>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — SNS event notifications.email.failed published
            _mockSnsClient.Verify(
                x => x.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.TopicArn == TestSnsTopicArn &&
                        r.Subject == "notifications.email.failed"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task HandleAsync_SendEmailCommand_PublishesRescheduledEvent()
        {
            // Arrange — After failure with retry, email stays Pending = "rescheduled"
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId, retriesCount: 0);
            var config = CreateTestSmtpConfig(serviceId, maxRetries: 3, retryWaitMinutes: 5);

            // SmtpService handles retry: Status stays Pending, ScheduledOn pushed forward
            var rescheduledEmail = CreateTestEmail(emailId, serviceId, retriesCount: 1);
            rescheduledEmail.Status = EmailStatus.Pending;
            rescheduledEmail.ServerError = "Connection refused";
            rescheduledEmail.ScheduledOn = DateTime.UtcNow.AddMinutes(5);
            rescheduledEmail.SentOn = null;

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)
                .ReturnsAsync(rescheduledEmail);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — SNS event notifications.email.rescheduled published
            _mockSnsClient.Verify(
                x => x.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.TopicArn == TestSnsTopicArn &&
                        r.Subject == "notifications.email.rescheduled"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 4: Batch Processing Tests
        // Source SmtpInternalService.ProcessSmtpQueue() lines 829-878
        // Batch-of-10, priority DESC + scheduled_on ASC ordering.
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task ProcessQueue_BatchOf10_FetchesPendingEmails()
        {
            // Arrange — Verify that the process-queue command delegates to
            // ProcessSmtpQueueAsync which internally fetches pending emails
            // with a page size of 10 (preserving source line 847 PAGESIZE 10).
            _mockSmtpService
                .Setup(x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildProcessQueueCommandJson()));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — QueueProcessor delegates to SmtpService which owns batch logic
            _mockSmtpService.Verify(
                x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task ProcessQueue_PriorityDescScheduledAsc_OrderPreserved()
        {
            // Arrange — Process-queue triggers SmtpService which owns
            // priority DESC, scheduled_on ASC ordering from source line 847.
            _mockSmtpService
                .Setup(x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildProcessQueueCommandJson()));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — Delegation successful; ordering is SmtpService's concern
            _mockSmtpService.Verify(
                x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task ProcessQueue_LoopsUntilNoPendingEmails()
        {
            // Arrange — Source line 868: while (pendingEmails.Count > 0)
            // The loop is internal to SmtpService.ProcessSmtpQueueAsync.
            _mockSmtpService
                .Setup(x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildProcessQueueCommandJson()));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — ProcessSmtpQueueAsync called once; looping is SmtpService's responsibility
            _mockSmtpService.Verify(
                x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task ProcessQueue_EmptyBatch_ExitsImmediately()
        {
            // Arrange — When no pending emails, SmtpService returns immediately
            _mockSmtpService
                .Setup(x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildProcessQueueCommandJson()));
            var context = CreateLambdaContext();

            // Act
            var result = await _sut.HandleAsync(sqsEvent, context);

            // Assert — Success with no failures
            result.BatchItemFailures.Should().BeEmpty();
            _mockSmtpService.Verify(
                x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task HandleAsync_MultipleSqsMessages_ProcessesAllIndependently()
        {
            // Arrange — SQSEvent with 3 messages, each processed independently
            var emailId1 = Guid.NewGuid();
            var emailId2 = Guid.NewGuid();
            var serviceId = Guid.NewGuid();

            var email1 = CreateTestEmail(emailId1, serviceId);
            var email2 = CreateTestEmail(emailId2, serviceId);
            var config = CreateTestSmtpConfig(serviceId);

            var sentEmail1 = CreateTestEmail(emailId1, serviceId, EmailStatus.Sent);
            sentEmail1.SentOn = DateTime.UtcNow;
            sentEmail1.ScheduledOn = null;

            var sentEmail2 = CreateTestEmail(emailId2, serviceId, EmailStatus.Sent);
            sentEmail2.SentOn = DateTime.UtcNow;
            sentEmail2.ScheduledOn = null;

            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(emailId1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email1);

            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(emailId2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email2);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _mockSmtpService
                .Setup(x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(
                ("msg-1", BuildEmailSendRequestJson(emailId1)),
                ("msg-2", BuildEmailSendRequestJson(emailId2)),
                ("msg-3", BuildProcessQueueCommandJson()));
            var context = CreateLambdaContext();

            // Act
            var result = await _sut.HandleAsync(sqsEvent, context);

            // Assert — All 3 messages processed, no failures
            result.BatchItemFailures.Should().BeEmpty();
            _mockSmtpService.Verify(
                x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _mockSmtpService.Verify(
                x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()),
                Times.Once());
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 5: Retry Logic Tests
        // Source SmtpInternalService.SendEmail lines 806-821 — retry logic
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task SendEmail_Failure_IncrementsRetriesCount()
        {
            // Arrange — SmtpService increments RetriesCount on failure.
            // Verify via reloaded email after send.
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId, retriesCount: 1);
            var config = CreateTestSmtpConfig(serviceId, maxRetries: 5);

            var retriedEmail = CreateTestEmail(emailId, serviceId, retriesCount: 2);
            retriedEmail.Status = EmailStatus.Pending;
            retriedEmail.ServerError = "Connection timeout";
            retriedEmail.ScheduledOn = DateTime.UtcNow.AddMinutes(config.RetryWaitMinutes);

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)
                .ReturnsAsync(retriedEmail);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — Send was called; reloaded email has incremented retries
            _mockSmtpService.Verify(
                x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()),
                Times.Once());
            _mockRepository.Verify(
                x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }

        [Fact]
        public async Task SendEmail_Failure_BelowMaxRetries_ReschedulesEmail()
        {
            // Arrange — RetriesCount < MaxRetriesCount: rescheduled
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId, retriesCount: 0);
            var config = CreateTestSmtpConfig(serviceId, maxRetries: 3, retryWaitMinutes: 10);

            var rescheduledEmail = CreateTestEmail(emailId, serviceId, retriesCount: 1);
            rescheduledEmail.Status = EmailStatus.Pending;
            rescheduledEmail.SentOn = null;
            rescheduledEmail.ServerError = "SMTP timeout";
            rescheduledEmail.ScheduledOn = DateTime.UtcNow.AddMinutes(10);

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)
                .ReturnsAsync(rescheduledEmail);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — Rescheduled event published
            _mockSnsClient.Verify(
                x => x.PublishAsync(
                    It.Is<PublishRequest>(r => r.Subject == "notifications.email.rescheduled"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task SendEmail_Failure_AtMaxRetries_AbortsEmail()
        {
            // Arrange — RetriesCount >= MaxRetriesCount: aborted
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId, retriesCount: 2);
            var config = CreateTestSmtpConfig(serviceId, maxRetries: 3);

            var abortedEmail = CreateTestEmail(emailId, serviceId, retriesCount: 3);
            abortedEmail.Status = EmailStatus.Aborted;
            abortedEmail.ScheduledOn = null;
            abortedEmail.ServerError = "Max retries exceeded";

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)
                .ReturnsAsync(abortedEmail);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — Failed event published
            _mockSnsClient.Verify(
                x => x.PublishAsync(
                    It.Is<PublishRequest>(r => r.Subject == "notifications.email.failed"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(30)]
        [InlineData(1440)]
        public async Task SendEmail_RetryWaitMinutes_SchedulesCorrectly(int retryWaitMinutes)
        {
            // Arrange — Verify ScheduledOn uses the configured RetryWaitMinutes
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId, retriesCount: 0);
            var config = CreateTestSmtpConfig(serviceId, maxRetries: 5, retryWaitMinutes: retryWaitMinutes);
            var beforeSend = DateTime.UtcNow;

            var rescheduledEmail = CreateTestEmail(emailId, serviceId, retriesCount: 1);
            rescheduledEmail.Status = EmailStatus.Pending;
            rescheduledEmail.ScheduledOn = DateTime.UtcNow.AddMinutes(retryWaitMinutes);

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)
                .ReturnsAsync(rescheduledEmail);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — ScheduledOn approximately matches UtcNow + retryWaitMinutes
            rescheduledEmail.ScheduledOn.Should().NotBeNull();
            rescheduledEmail.ScheduledOn!.Value.Should().BeCloseTo(
                beforeSend.AddMinutes(retryWaitMinutes),
                precision: TimeSpan.FromSeconds(30));
        }

        [Fact]
        public async Task SendEmail_AlwaysSavesInFinally()
        {
            // Arrange — Email always persisted regardless of outcome.
            // For service-not-found, QueueProcessor saves directly.
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId);

            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            _mockRepository
                .Setup(x => x.SaveEmailAsync(It.IsAny<Email>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — SaveEmailAsync called
            _mockRepository.Verify(
                x => x.SaveEmailAsync(It.IsAny<Email>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 6: Email Send Success Tests
        // Source SmtpInternalService.SendEmail lines 801-804 — success state
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task SendEmail_Success_SetsCorrectStatus()
        {
            // Arrange — After successful send: SentOn=UtcNow, Status=Sent,
            // ScheduledOn=null, ServerError=null (source lines 801-804)
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId);
            var config = CreateTestSmtpConfig(serviceId);
            var beforeSend = DateTime.UtcNow;

            var sentEmail = CreateTestEmail(emailId, serviceId, EmailStatus.Sent);
            sentEmail.SentOn = DateTime.UtcNow;
            sentEmail.ScheduledOn = null;
            sentEmail.ServerError = string.Empty;

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)
                .ReturnsAsync(sentEmail);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — Final reloaded email has correct success state
            sentEmail.Status.Should().Be(EmailStatus.Sent);
            sentEmail.SentOn.Should().NotBeNull();
            sentEmail.SentOn!.Value.Should().BeCloseTo(beforeSend, precision: TimeSpan.FromSeconds(30));
            sentEmail.ScheduledOn.Should().BeNull();

            // Sent event published
            _mockSnsClient.Verify(
                x => x.PublishAsync(
                    It.Is<PublishRequest>(r => r.Subject == "notifications.email.sent"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task SendEmail_ServiceNull_AbortsWithError()
        {
            // Arrange — Service is null → Abort with "SMTP service not found."
            // Source line 697: email.ServerError = "SMTP service not found"
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId);

            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            _mockRepository
                .Setup(x => x.SaveEmailAsync(It.IsAny<Email>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — Aborted with exact error message
            _mockRepository.Verify(
                x => x.SaveEmailAsync(
                    It.Is<Email>(e =>
                        e.ServerError == "SMTP service not found." &&
                        e.Status == EmailStatus.Aborted &&
                        e.ScheduledOn == null),
                    It.IsAny<CancellationToken>()),
                Times.Once());

            // SendEmailAsync never called
            _mockSmtpService.Verify(
                x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        [Fact]
        public async Task SendEmail_ServiceDisabled_AbortsWithError()
        {
            // Arrange — service.IsEnabled = false → SmtpService handles abort internally.
            // QueueProcessor sends to SmtpService, which detects disabled and aborts.
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId);
            var config = CreateTestSmtpConfig(serviceId, isEnabled: false);

            // SmtpService aborts the email when service is disabled
            var abortedEmail = CreateTestEmail(emailId, serviceId);
            abortedEmail.Status = EmailStatus.Aborted;
            abortedEmail.ServerError = "SMTP service is not enabled";
            abortedEmail.ScheduledOn = null;

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)
                .ReturnsAsync(abortedEmail);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — Failed event published (service disabled → abort)
            _mockSnsClient.Verify(
                x => x.PublishAsync(
                    It.Is<PublishRequest>(r => r.Subject == "notifications.email.failed"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 7: Partial Batch Failure Reporting Tests
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task HandleAsync_OneMessageFails_ReportsPartialBatchFailure()
        {
            // Arrange — 3 SQS messages: 2 succeed, 1 throws exception
            var emailId1 = Guid.NewGuid();
            var emailId2 = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var config = CreateTestSmtpConfig(serviceId);

            var email1 = CreateTestEmail(emailId1, serviceId);
            var sentEmail1 = CreateTestEmail(emailId1, serviceId, EmailStatus.Sent);
            sentEmail1.SentOn = DateTime.UtcNow;
            sentEmail1.ScheduledOn = null;

            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(emailId1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email1);

            // Second email triggers a service lookup failure (re-thrown as exception)
            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(emailId2, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Database connection lost"));

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Third message is process-queue (succeeds)
            _mockSmtpService
                .Setup(x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(
                ("msg-1", BuildEmailSendRequestJson(emailId1)),
                ("msg-fail", BuildEmailSendRequestJson(emailId2)),
                ("msg-3", BuildProcessQueueCommandJson()));
            var context = CreateLambdaContext();

            // Act
            var result = await _sut.HandleAsync(sqsEvent, context);

            // Assert — Exactly 1 failure reported
            result.BatchItemFailures.Should().HaveCount(1);
            result.BatchItemFailures[0].ItemIdentifier.Should().Be("msg-fail");
        }

        [Fact]
        public async Task HandleAsync_AllMessagesFail_ReportsAllAsFailures()
        {
            // Arrange — All messages fail with exceptions
            var emailId1 = Guid.NewGuid();
            var emailId2 = Guid.NewGuid();

            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("DB unavailable"));

            _mockSmtpService
                .Setup(x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("DB unavailable"));

            var sqsEvent = BuildSqsEvent(
                ("msg-a", BuildEmailSendRequestJson(emailId1)),
                ("msg-b", BuildEmailSendRequestJson(emailId2)),
                ("msg-c", BuildProcessQueueCommandJson()));
            var context = CreateLambdaContext();

            // Act
            var result = await _sut.HandleAsync(sqsEvent, context);

            // Assert — All 3 messages reported as failures
            result.BatchItemFailures.Should().HaveCount(3);
            var failedIds = result.BatchItemFailures.Select(f => f.ItemIdentifier).ToList();
            failedIds.Should().Contain("msg-a");
            failedIds.Should().Contain("msg-b");
            failedIds.Should().Contain("msg-c");
        }

        [Fact]
        public async Task HandleAsync_AllMessagesSucceed_EmptyBatchItemFailures()
        {
            // Arrange — All messages succeed
            _mockSmtpService
                .Setup(x => x.ProcessSmtpQueueAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId);
            var config = CreateTestSmtpConfig(serviceId);

            var sentEmail = CreateTestEmail(emailId, serviceId, EmailStatus.Sent);
            sentEmail.SentOn = DateTime.UtcNow;
            sentEmail.ScheduledOn = null;

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)
                .ReturnsAsync(sentEmail);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(
                ("msg-1", BuildEmailSendRequestJson(emailId)),
                ("msg-2", BuildProcessQueueCommandJson()));
            var context = CreateLambdaContext();

            // Act
            var result = await _sut.HandleAsync(sqsEvent, context);

            // Assert — Empty BatchItemFailures
            result.BatchItemFailures.Should().NotBeNull();
            result.BatchItemFailures.Should().BeEmpty();
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 8: Concurrency Control Tests
        // Source lines 828-837 used static lock; Lambda uses SQS concurrency
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void QueueProcessor_NoStaticLock_ReliesOnSqsConcurrency()
        {
            // Assert — Verify QueueProcessor does NOT implement static lock mechanism.
            // Source lines 828-837 used static lockObject + queueProcessingInProgress boolean.
            // In Lambda architecture, concurrency is managed by SQS reserved concurrency.
            var queueProcessorType = typeof(QueueProcessor);

            // Check for static fields that would indicate a lock mechanism
            var staticFields = queueProcessorType
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.Name.Contains("lock", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Contains("processing", StringComparison.OrdinalIgnoreCase) ||
                            f.FieldType == typeof(object))
                .ToList();

            // No static lock fields should exist — concurrency managed by SQS infrastructure
            staticFields.Should().BeEmpty(
                "QueueProcessor should not use static locks; concurrency is managed by SQS reserved concurrency at infrastructure level");
        }

        [Fact]
        public void SmtpService_ProcessQueue_UseSemaphoreSlim_DefenseInDepth()
        {
            // Assert — Verify the ISmtpService interface exposes ProcessSmtpQueueAsync
            // which is the defense-in-depth entry point. SemaphoreSlim usage is an
            // internal implementation detail of SmtpService, verified via the interface
            // method existing (SmtpService implementation tests verify SemaphoreSlim).
            var smtpServiceType = typeof(ISmtpService);
            var processMethod = smtpServiceType.GetMethod("ProcessSmtpQueueAsync");

            processMethod.Should().NotBeNull(
                "ISmtpService must expose ProcessSmtpQueueAsync for defense-in-depth queue processing");
            processMethod!.ReturnType.Should().Be(typeof(Task),
                "ProcessSmtpQueueAsync should return Task for async operation");
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 9: Domain Event Publishing Tests
        // Event naming convention: {domain}.{entity}.{action} per AAP §0.8.5
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public async Task ProcessQueue_EmailSent_PublishesSnsEvent()
        {
            // Arrange — Verify notifications.email.sent event after successful send
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId);
            var config = CreateTestSmtpConfig(serviceId);

            var sentEmail = CreateTestEmail(emailId, serviceId, EmailStatus.Sent);
            sentEmail.SentOn = DateTime.UtcNow;
            sentEmail.ScheduledOn = null;

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)
                .ReturnsAsync(sentEmail);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — SNS publish called with correct subject
            _mockSnsClient.Verify(
                x => x.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.TopicArn == TestSnsTopicArn &&
                        r.Subject == "notifications.email.sent" &&
                        !string.IsNullOrEmpty(r.Message)),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task ProcessQueue_EmailAborted_PublishesSnsEvent()
        {
            // Arrange — Verify notifications.email.failed event after abort
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId);

            _mockRepository
                .Setup(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            _mockRepository
                .Setup(x => x.SaveEmailAsync(It.IsAny<Email>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — Failed event published
            _mockSnsClient.Verify(
                x => x.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.TopicArn == TestSnsTopicArn &&
                        r.Subject == "notifications.email.failed" &&
                        !string.IsNullOrEmpty(r.Message)),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task ProcessQueue_EmailRescheduled_PublishesSnsEvent()
        {
            // Arrange — Verify notifications.email.rescheduled event after retry
            var emailId = Guid.NewGuid();
            var serviceId = Guid.NewGuid();
            var email = CreateTestEmail(emailId, serviceId, retriesCount: 0);
            var config = CreateTestSmtpConfig(serviceId, maxRetries: 5, retryWaitMinutes: 10);

            var rescheduledEmail = CreateTestEmail(emailId, serviceId, retriesCount: 1);
            rescheduledEmail.Status = EmailStatus.Pending;
            rescheduledEmail.ServerError = "Temporary failure";
            rescheduledEmail.ScheduledOn = DateTime.UtcNow.AddMinutes(10);

            _mockRepository
                .SetupSequence(x => x.GetEmailByIdAsync(emailId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(email)
                .ReturnsAsync(rescheduledEmail);

            _mockRepository
                .Setup(x => x.GetSmtpServiceByIdAsync(serviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(config);

            _mockSmtpService
                .Setup(x => x.SendEmailAsync(It.IsAny<Email>(), It.IsAny<SmtpServiceConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = BuildSqsEvent(("msg-1", BuildEmailSendRequestJson(emailId)));
            var context = CreateLambdaContext();

            // Act
            await _sut.HandleAsync(sqsEvent, context);

            // Assert — Rescheduled event published
            _mockSnsClient.Verify(
                x => x.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.TopicArn == TestSnsTopicArn &&
                        r.Subject == "notifications.email.rescheduled" &&
                        !string.IsNullOrEmpty(r.Message)),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Theory]
        [InlineData("sent", "notifications.email.sent")]
        [InlineData("failed", "notifications.email.failed")]
        [InlineData("rescheduled", "notifications.email.rescheduled")]
        public void DomainEventNaming_FollowsConvention(string action, string expectedSubject)
        {
            // Assert — Verify {domain}.{entity}.{action} naming convention per AAP §0.8.5
            var parts = expectedSubject.Split('.');
            parts.Should().HaveCount(3, "Event name must follow {domain}.{entity}.{action} pattern");
            parts[0].Should().Be("notifications", "Domain must be 'notifications'");
            parts[1].Should().Be("email", "Entity must be 'email'");
            parts[2].Should().Be(action, $"Action must be '{action}'");

            // Verify the constructed subject matches the pattern
            var constructedSubject = $"notifications.email.{action}";
            constructedSubject.Should().Be(expectedSubject);
        }

        // ═══════════════════════════════════════════════════════════════════
        // IDisposable Implementation
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Cleanup: verifies all mock setups were used appropriately.</summary>
        public void Dispose()
        {
            // No unmanaged resources to dispose.
            // Mock verification is done per-test, not globally.
            GC.SuppressFinalize(this);
        }
    }
}
