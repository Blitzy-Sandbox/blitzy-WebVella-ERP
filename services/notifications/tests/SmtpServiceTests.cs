using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Notifications.DataAccess;
using WebVellaErp.Notifications.Models;
using WebVellaErp.Notifications.Services;
using Xunit;

namespace WebVellaErp.Notifications.Tests
{
    /// <summary>
    /// Comprehensive unit tests for the SmtpService class in the Notifications microservice.
    /// Validates SMTP engine behavior including configuration validation, email send/queue logic,
    /// queue processing, CC/BCC/ReplyTo handling, content processing, and XSearch preparation.
    /// All validation error messages are character-for-character verified against the dest implementation.
    /// </summary>
    public class SmtpServiceTests : IDisposable
    {
        private readonly Mock<INotificationRepository> _mockRepository;
        private readonly Mock<IAmazonSimpleEmailServiceV2> _mockSesClient;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<ILogger<SmtpService>> _mockLogger;
        private readonly SmtpService _sut;

        public SmtpServiceTests()
        {
            _mockRepository = new Mock<INotificationRepository>();
            _mockSesClient = new Mock<IAmazonSimpleEmailServiceV2>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockLogger = new Mock<ILogger<SmtpService>>();

            // Set SNS topic ARN environment variable required by SmtpService constructor
            Environment.SetEnvironmentVariable("NOTIFICATIONS_SNS_TOPIC_ARN",
                "arn:aws:sns:us-east-1:000000000000:notifications-test-topic");

            // Default mock behaviors: SNS publish and SES send succeed by default
            _mockSnsClient
                .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            _mockSesClient
                .Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SendEmailResponse());

            // Default: no default SMTP service configured (overridden in tests that need it)
            _mockRepository
                .Setup(r => r.GetDefaultSmtpServiceAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            _sut = new SmtpService(
                _mockRepository.Object,
                _mockSesClient.Object,
                _mockSnsClient.Object,
                _mockLogger.Object);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("NOTIFICATIONS_SNS_TOPIC_ARN", null);
        }

        #region Helper Methods

        /// <summary>
        /// Creates a valid SmtpServiceConfig with all fields populated for testing.
        /// Individual tests override specific fields to trigger targeted validation errors.
        /// </summary>
        private static SmtpServiceConfig CreateValidConfig()
        {
            return new SmtpServiceConfig
            {
                Id = Guid.NewGuid(),
                Name = "Test SMTP Service",
                Server = "smtp.example.com",
                Port = 587,
                DefaultSenderEmail = "sender@example.com",
                DefaultSenderName = "Test Sender",
                DefaultReplyToEmail = "reply@example.com",
                MaxRetriesCount = 3,
                RetryWaitMinutes = 5,
                IsDefault = false,
                IsEnabled = true,
                ConnectionSecurity = 1
            };
        }

        /// <summary>
        /// Creates a test Email instance with all fields populated.
        /// Used across send, queue, and content processing tests.
        /// </summary>
        private static Email CreateTestEmail()
        {
            return new Email
            {
                Id = Guid.NewGuid(),
                Sender = new EmailAddress("Test Sender", "sender@example.com"),
                Recipients = new List<EmailAddress>
                {
                    new EmailAddress("Recipient One", "recipient1@example.com")
                },
                ReplyToEmail = "reply@example.com",
                Subject = "Test Subject",
                ContentText = "Test plain text body",
                ContentHtml = "<p>Test HTML body</p>",
                Status = EmailStatus.Pending,
                Priority = EmailPriority.Normal,
                ServerError = string.Empty,
                ScheduledOn = DateTime.UtcNow,
                SentOn = null,
                RetriesCount = 0,
                ServiceId = Guid.NewGuid(),
                Attachments = new List<string>(),
                CreatedOn = DateTime.UtcNow
            };
        }

        #endregion

        #region Phase 2 — ValidateSmtpServiceCreateAsync

        [Fact]
        public async Task ValidateCreate_DuplicateName_ReturnsError()
        {
            // Arrange
            var config = CreateValidConfig();
            var existingService = CreateValidConfig();
            existingService.Name = config.Name;

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(config.Name, It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingService);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Should().NotBeEmpty();
            errors.Should().Contain(e =>
                e.Key == "name" &&
                e.Message == "Name is already used by another service.");
        }

        [Fact]
        public async Task ValidateCreate_UniqueName_NoError()
        {
            // Arrange
            var config = CreateValidConfig();

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(config.Name, It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert — no error on name key
            errors.Where(e => e.Key == "name").Should().BeEmpty();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65026)]
        public async Task ValidateCreate_InvalidPort_ReturnsError(int port)
        {
            // Arrange
            var config = CreateValidConfig();
            config.Port = port;

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Should().Contain(e =>
                e.Key == "port" &&
                e.Message == "Port must be an integer value between 1 and 65025");
        }

        [Fact]
        public async Task ValidateCreate_NonIntegerPort_ReturnsError()
        {
            // Arrange — use int.MinValue as a sentinel for non-integer input
            var config = CreateValidConfig();
            config.Port = int.MinValue;

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Should().Contain(e =>
                e.Key == "port" &&
                e.Message == "Port must be an integer value between 1 and 65025");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(25)]
        [InlineData(587)]
        [InlineData(465)]
        [InlineData(65025)]
        public async Task ValidateCreate_ValidPort_NoError(int port)
        {
            // Arrange
            var config = CreateValidConfig();
            config.Port = port;

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Where(e => e.Key == "port").Should().BeEmpty();
        }

        [Fact]
        public async Task ValidateCreate_InvalidFromEmail_ReturnsError()
        {
            // Arrange
            var config = CreateValidConfig();
            config.DefaultSenderEmail = "not-email";

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Should().Contain(e =>
                e.Key == "default_sender_email" &&
                e.Message == "Default from email address is invalid");
        }

        [Fact]
        public async Task ValidateCreate_ValidFromEmail_NoError()
        {
            // Arrange
            var config = CreateValidConfig();
            config.DefaultSenderEmail = "test@example.com";

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Where(e => e.Key == "default_sender_email").Should().BeEmpty();
        }

        [Fact]
        public async Task ValidateCreate_InvalidReplyToEmail_ReturnsError()
        {
            // Arrange
            var config = CreateValidConfig();
            config.DefaultReplyToEmail = "not-email";

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Should().Contain(e =>
                e.Key == "default_reply_to_email" &&
                e.Message == "Default reply to email address is invalid");
        }

        [Fact]
        public async Task ValidateCreate_EmptyReplyToEmail_NoError()
        {
            // Arrange — source lines 94-95: null/whitespace reply-to is skipped
            var config = CreateValidConfig();
            config.DefaultReplyToEmail = "  ";

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Where(e => e.Key == "default_reply_to_email").Should().BeEmpty();
        }

        [Fact]
        public async Task ValidateCreate_ValidReplyToEmail_NoError()
        {
            // Arrange
            var config = CreateValidConfig();
            config.DefaultReplyToEmail = "reply@example.com";

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Where(e => e.Key == "default_reply_to_email").Should().BeEmpty();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(11)]
        [InlineData(-1)]
        public async Task ValidateCreate_InvalidMaxRetries_ReturnsError(int retries)
        {
            // Arrange
            var config = CreateValidConfig();
            config.MaxRetriesCount = retries;

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Should().Contain(e =>
                e.Key == "max_retries_count" &&
                e.Message == "Number of retries on error must be an integer value between 1 and 10");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(10)]
        public async Task ValidateCreate_ValidMaxRetries_NoError(int retries)
        {
            // Arrange
            var config = CreateValidConfig();
            config.MaxRetriesCount = retries;

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Where(e => e.Key == "max_retries_count").Should().BeEmpty();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1441)]
        [InlineData(-1)]
        public async Task ValidateCreate_InvalidRetryWait_ReturnsError(int minutes)
        {
            // Arrange
            var config = CreateValidConfig();
            config.RetryWaitMinutes = minutes;

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Should().Contain(e =>
                e.Key == "retry_wait_minutes" &&
                e.Message == "Wait period between retries must be an integer value between 1 and 1440 minutes");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(720)]
        [InlineData(1440)]
        public async Task ValidateCreate_ValidRetryWait_NoError(int minutes)
        {
            // Arrange
            var config = CreateValidConfig();
            config.RetryWaitMinutes = minutes;

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Where(e => e.Key == "retry_wait_minutes").Should().BeEmpty();
        }

        [Fact]
        public async Task ValidateCreate_InvalidConnectionSecurity_ReturnsError()
        {
            // Arrange — value 5 is outside valid enum range 0-4
            var config = CreateValidConfig();
            config.ConnectionSecurity = 5;

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert — note trailing period in message
            errors.Should().Contain(e =>
                e.Key == "connection_security" &&
                e.Message == "Invalid connection security setting selected.");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public async Task ValidateCreate_ValidConnectionSecurity_NoError(int security)
        {
            // Arrange — 0=None, 1=Auto, 2=SslOnConnect, 3=StartTls, 4=StartTlsWhenAvailable
            var config = CreateValidConfig();
            config.ConnectionSecurity = security;

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceCreateAsync(config, CancellationToken.None);

            // Assert
            errors.Where(e => e.Key == "connection_security").Should().BeEmpty();
        }

        #endregion

        #region Phase 3 — ValidateSmtpServiceUpdateAsync

        [Fact]
        public async Task ValidateUpdate_DuplicateName_DifferentId_ReturnsError()
        {
            // Arrange — another service exists with the same name but a different ID
            var config = CreateValidConfig();
            var existingService = CreateValidConfig();
            existingService.Name = config.Name;
            existingService.Id = Guid.NewGuid(); // Different ID

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(config.Name, It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingService);

            // Act
            var errors = await _sut.ValidateSmtpServiceUpdateAsync(config, CancellationToken.None);

            // Assert
            errors.Should().Contain(e =>
                e.Key == "name" &&
                e.Message == "Name is already used by another service.");
        }

        [Fact]
        public async Task ValidateUpdate_SameName_SameId_NoError()
        {
            // Arrange — updating own record with same name is allowed
            var config = CreateValidConfig();
            var existingService = CreateValidConfig();
            existingService.Name = config.Name;
            existingService.Id = config.Id; // Same ID — own record

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(config.Name, It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingService);

            // Act
            var errors = await _sut.ValidateSmtpServiceUpdateAsync(config, CancellationToken.None);

            // Assert
            errors.Where(e => e.Key == "name").Should().BeEmpty();
        }

        [Fact]
        public async Task ValidateUpdate_AllOtherFieldsValidatedSameAsCreate()
        {
            // Arrange — invalid values for all common fields to verify update uses same validation
            var config = CreateValidConfig();
            config.Port = 0;
            config.DefaultSenderEmail = "invalid";
            config.DefaultReplyToEmail = "invalid";
            config.MaxRetriesCount = 0;
            config.RetryWaitMinutes = 0;
            config.ConnectionSecurity = 99;

            _mockRepository
                .Setup(r => r.GetSmtpServiceByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            var errors = await _sut.ValidateSmtpServiceUpdateAsync(config, CancellationToken.None);

            // Assert — all common validation errors present (same messages as create)
            errors.Should().Contain(e => e.Key == "port");
            errors.Should().Contain(e => e.Key == "default_sender_email");
            errors.Should().Contain(e => e.Key == "default_reply_to_email");
            errors.Should().Contain(e => e.Key == "max_retries_count");
            errors.Should().Contain(e => e.Key == "retry_wait_minutes");
            errors.Should().Contain(e => e.Key == "connection_security");
        }

        #endregion

        #region Phase 4 — HandleDefaultServiceSetupAsync

        [Fact]
        public async Task HandleDefaultSetup_SetDefault_ClearsOtherDefaults()
        {
            // Arrange — config being set as default, with two other services already default
            var config = CreateValidConfig();
            config.IsDefault = true;

            var otherService1 = CreateValidConfig();
            otherService1.IsDefault = true;
            otherService1.Id = Guid.NewGuid();

            var otherService2 = CreateValidConfig();
            otherService2.IsDefault = true;
            otherService2.Id = Guid.NewGuid();

            var allServices = new List<SmtpServiceConfig> { config, otherService1, otherService2 };

            _mockRepository
                .Setup(r => r.GetAllSmtpServicesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(allServices);

            // Act
            var errors = await _sut.HandleDefaultServiceSetupAsync(config, CancellationToken.None);

            // Assert — no errors, other defaults cleared
            errors.Should().BeEmpty();

            _mockRepository.Verify(r => r.SaveSmtpServiceAsync(
                It.Is<SmtpServiceConfig>(s => s.Id == otherService1.Id && !s.IsDefault),
                It.IsAny<CancellationToken>()), Times.Once());

            _mockRepository.Verify(r => r.SaveSmtpServiceAsync(
                It.Is<SmtpServiceConfig>(s => s.Id == otherService2.Id && !s.IsDefault),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task HandleDefaultSetup_SetDefault_DoesNotClearItself()
        {
            // Arrange — the config being set as default should NOT have its own default cleared
            var config = CreateValidConfig();
            config.IsDefault = true;

            var allServices = new List<SmtpServiceConfig> { config };

            _mockRepository
                .Setup(r => r.GetAllSmtpServicesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(allServices);

            // Act
            var errors = await _sut.HandleDefaultServiceSetupAsync(config, CancellationToken.None);

            // Assert — no save call for the config itself (filter: s.Id != config.Id)
            errors.Should().BeEmpty();

            _mockRepository.Verify(r => r.SaveSmtpServiceAsync(
                It.Is<SmtpServiceConfig>(s => s.Id == config.Id),
                It.IsAny<CancellationToken>()), Times.Never());
        }

        [Fact]
        public async Task HandleDefaultSetup_UnsetDefault_CurrentDefault_ReturnsError()
        {
            // Arrange — trying to unset default on the currently default service
            var config = CreateValidConfig();
            config.IsDefault = false;

            var currentInDb = CreateValidConfig();
            currentInDb.Id = config.Id;
            currentInDb.IsDefault = true; // Currently the default

            _mockRepository
                .Setup(r => r.GetSmtpServiceByIdAsync(config.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(currentInDb);

            // Act
            var errors = await _sut.HandleDefaultServiceSetupAsync(config, CancellationToken.None);

            // Assert — error: "Forbidden. There should always be an active default service."
            errors.Should().Contain(e =>
                e.Key == "is_default" &&
                e.Message == "Forbidden. There should always be an active default service.");
        }

        [Fact]
        public async Task HandleDefaultSetup_UnsetDefault_NotCurrentDefault_NoError()
        {
            // Arrange — unsetting default on a non-default service is allowed
            var config = CreateValidConfig();
            config.IsDefault = false;

            var currentInDb = CreateValidConfig();
            currentInDb.Id = config.Id;
            currentInDb.IsDefault = false; // Not the current default

            _mockRepository
                .Setup(r => r.GetSmtpServiceByIdAsync(config.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(currentInDb);

            // Act
            var errors = await _sut.HandleDefaultServiceSetupAsync(config, CancellationToken.None);

            // Assert
            errors.Should().BeEmpty();
        }

        #endregion

        #region Phase 5 — SendEmailAsync

        [Fact]
        public async Task SendEmail_ServiceNull_AbortsEmail()
        {
            // Arrange
            var email = CreateTestEmail();

            // Act — pass null service directly (the overload takes SmtpServiceConfig service)
            await _sut.SendEmailAsync(email, (SmtpServiceConfig)null!, CancellationToken.None);

            // Assert — email aborted with "SMTP service not found" (no period)
            email.Status.Should().Be(EmailStatus.Aborted);
            email.ServerError.Should().Be("SMTP service not found");
            email.ScheduledOn.Should().BeNull();

            _mockRepository.Verify(r => r.SaveEmailAsync(
                It.Is<Email>(e => e.Id == email.Id),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task SendEmail_ServiceDisabled_AbortsEmail()
        {
            // Arrange
            var email = CreateTestEmail();
            var service = CreateValidConfig();
            service.IsEnabled = false;

            // Act
            await _sut.SendEmailAsync(email, service, CancellationToken.None);

            // Assert — email aborted with "SMTP service is not enabled"
            email.Status.Should().Be(EmailStatus.Aborted);
            email.ServerError.Should().Be("SMTP service is not enabled");
            email.ScheduledOn.Should().BeNull();

            _mockRepository.Verify(r => r.SaveEmailAsync(
                It.Is<Email>(e => e.Id == email.Id),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task SendEmail_Success_SetsStatusSent()
        {
            // Arrange
            var email = CreateTestEmail();
            var service = CreateValidConfig();
            service.IsEnabled = true;
            var beforeSend = DateTime.UtcNow;

            // Act
            await _sut.SendEmailAsync(email, service, CancellationToken.None);

            // Assert — email marked as sent
            email.Status.Should().Be(EmailStatus.Sent);
            email.SentOn.Should().NotBeNull();
            email.SentOn!.Value.Should().BeOnOrAfter(beforeSend);
            email.ScheduledOn.Should().BeNull();
            email.ServerError.Should().Be(string.Empty);
        }

        [Fact]
        public async Task SendEmail_Failure_IncrementsRetriesCount()
        {
            // Arrange
            var email = CreateTestEmail();
            email.RetriesCount = 0;
            var service = CreateValidConfig();
            service.IsEnabled = true;
            service.MaxRetriesCount = 5;

            _mockSesClient
                .Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSimpleEmailServiceV2Exception("SES failure"));

            // Act
            await _sut.SendEmailAsync(email, service, CancellationToken.None);

            // Assert — retries count incremented
            email.RetriesCount.Should().Be(1);
        }

        [Fact]
        public async Task SendEmail_Failure_BelowMaxRetries_Reschedules()
        {
            // Arrange
            var email = CreateTestEmail();
            email.RetriesCount = 0;
            email.Priority = EmailPriority.High; // High-priority emails also follow retry logic
            var service = CreateValidConfig();
            service.IsEnabled = true;
            service.MaxRetriesCount = 5;
            service.RetryWaitMinutes = 10;
            var beforeSend = DateTime.UtcNow;

            _mockSesClient
                .Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSimpleEmailServiceV2Exception("SES failure"));

            // Act
            await _sut.SendEmailAsync(email, service, CancellationToken.None);

            // Assert — below max retries: rescheduled with Pending status
            email.RetriesCount.Should().Be(1);
            email.Status.Should().Be(EmailStatus.Pending);
            email.ScheduledOn.Should().NotBeNull();
            email.ScheduledOn!.Value.Should().BeOnOrAfter(
                beforeSend.AddMinutes(service.RetryWaitMinutes));
        }

        [Fact]
        public async Task SendEmail_Failure_AtMaxRetries_Aborts()
        {
            // Arrange — already at max retries minus 1, so next failure triggers abort
            var email = CreateTestEmail();
            email.RetriesCount = 4; // MaxRetriesCount is 5, after increment it becomes 5 >= 5
            email.Priority = EmailPriority.Low; // Low-priority emails still follow abort logic
            var service = CreateValidConfig();
            service.IsEnabled = true;
            service.MaxRetriesCount = 5;

            _mockSesClient
                .Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSimpleEmailServiceV2Exception("SES failure"));

            // Act
            await _sut.SendEmailAsync(email, service, CancellationToken.None);

            // Assert — max retries exceeded: aborted
            email.RetriesCount.Should().Be(5);
            email.Status.Should().Be(EmailStatus.Aborted);
            email.ScheduledOn.Should().BeNull();
        }

        [Fact]
        public async Task SendEmail_AlwaysSavesInFinally()
        {
            // Arrange — verify the finally block always persists the email regardless of outcome
            var email = CreateTestEmail();
            var service = CreateValidConfig();
            service.IsEnabled = true;
            service.MaxRetriesCount = 5;

            _mockSesClient
                .Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Transport error"));

            // Act
            await _sut.SendEmailAsync(email, service, CancellationToken.None);

            // Assert — SaveEmailAsync called in finally block (even after exception)
            _mockRepository.Verify(r => r.SaveEmailAsync(
                It.Is<Email>(e => e.Id == email.Id),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        #endregion

        #region Phase 6 — Email Composition (CC/BCC/ReplyTo)

        [Fact]
        public async Task SendEmail_CcRecipient_AddedToCcList()
        {
            // Arrange — recipient with "cc:" prefix should route to CC list
            var email = CreateTestEmail();
            email.Recipients = new List<EmailAddress>
            {
                new EmailAddress("CC User", "cc:ccuser@example.com"),
                new EmailAddress("Normal User", "normal@example.com")
            };
            var service = CreateValidConfig();
            service.IsEnabled = true;

            SendEmailRequest? capturedRequest = null;
            _mockSesClient
                .Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new SendEmailResponse());

            // Act
            await _sut.SendEmailAsync(email, service, CancellationToken.None);

            // Assert — CC list contains the cc-prefixed address (prefix stripped)
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Destination.CcAddresses.Should().HaveCount(1);
            capturedRequest.Destination.CcAddresses[0].Should().Contain("ccuser@example.com");
            // Normal recipient in To list
            capturedRequest.Destination.ToAddresses.Should().HaveCount(1);
            capturedRequest.Destination.ToAddresses[0].Should().Contain("normal@example.com");
        }

        [Fact]
        public async Task SendEmail_BccRecipient_AddedToBccList()
        {
            // Arrange — recipient with "bcc:" prefix should route to BCC list
            var email = CreateTestEmail();
            email.Recipients = new List<EmailAddress>
            {
                new EmailAddress("BCC User", "bcc:bccuser@example.com"),
                new EmailAddress("Normal User", "normal@example.com")
            };
            var service = CreateValidConfig();
            service.IsEnabled = true;

            SendEmailRequest? capturedRequest = null;
            _mockSesClient
                .Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new SendEmailResponse());

            // Act
            await _sut.SendEmailAsync(email, service, CancellationToken.None);

            // Assert — BCC list contains the bcc-prefixed address (prefix stripped, substring(4))
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Destination.BccAddresses.Should().HaveCount(1);
            capturedRequest.Destination.BccAddresses[0].Should().Contain("bccuser@example.com");
            // Normal recipient in To list
            capturedRequest.Destination.ToAddresses.Should().HaveCount(1);
            capturedRequest.Destination.ToAddresses[0].Should().Contain("normal@example.com");
        }

        [Fact]
        public async Task SendEmail_NormalRecipient_AddedToToList()
        {
            // Arrange — recipients without prefix go to To list
            var email = CreateTestEmail();
            email.Recipients = new List<EmailAddress>
            {
                new EmailAddress("User One", "user1@example.com"),
                new EmailAddress("User Two", "user2@example.com")
            };
            var service = CreateValidConfig();
            service.IsEnabled = true;

            SendEmailRequest? capturedRequest = null;
            _mockSesClient
                .Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new SendEmailResponse());

            // Act
            await _sut.SendEmailAsync(email, service, CancellationToken.None);

            // Assert — both recipients in To list
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Destination.ToAddresses.Should().HaveCount(2);
            capturedRequest.Destination.ToAddresses[0].Should().Contain("user1@example.com");
            capturedRequest.Destination.ToAddresses[1].Should().Contain("user2@example.com");
        }

        [Fact]
        public async Task SendEmail_ReplyTo_SplitBySemicolon()
        {
            // Arrange — semicolon-separated ReplyTo addresses
            var email = CreateTestEmail();
            email.ReplyToEmail = "a@example.com;b@example.com";
            var service = CreateValidConfig();
            service.IsEnabled = true;

            SendEmailRequest? capturedRequest = null;
            _mockSesClient
                .Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new SendEmailResponse());

            // Act
            await _sut.SendEmailAsync(email, service, CancellationToken.None);

            // Assert — ReplyTo list has both addresses
            capturedRequest.Should().NotBeNull();
            capturedRequest!.ReplyToAddresses.Should().HaveCount(2);
            capturedRequest.ReplyToAddresses.Should().Contain("a@example.com");
            capturedRequest.ReplyToAddresses.Should().Contain("b@example.com");
        }

        [Fact]
        public async Task SendEmail_EmptyReplyTo_UsesSenderAddress()
        {
            // Arrange — empty ReplyTo falls back to sender address
            var email = CreateTestEmail();
            email.ReplyToEmail = string.Empty;
            email.Sender = new EmailAddress("Sender", "sender@example.com");
            var service = CreateValidConfig();
            service.IsEnabled = true;

            SendEmailRequest? capturedRequest = null;
            _mockSesClient
                .Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new SendEmailResponse());

            // Act
            await _sut.SendEmailAsync(email, service, CancellationToken.None);

            // Assert — sender address used as ReplyTo fallback
            capturedRequest.Should().NotBeNull();
            capturedRequest!.ReplyToAddresses.Should().HaveCount(1);
            capturedRequest.ReplyToAddresses[0].Should().Be("sender@example.com");
        }

        #endregion

        #region Phase 7 — ProcessSmtpQueueAsync

        [Fact]
        public async Task ProcessQueue_FetchesBatchOf10()
        {
            // Arrange — return empty on first call to exit the loop immediately
            _mockRepository
                .Setup(r => r.GetPendingEmailsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Email>());

            // Act
            await _sut.ProcessSmtpQueueAsync(CancellationToken.None);

            // Assert — called with pageSize=10
            _mockRepository.Verify(r => r.GetPendingEmailsAsync(
                10, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        [Fact]
        public async Task ProcessQueue_ServiceNull_AbortsEmail()
        {
            // Arrange — email in queue with service that no longer exists
            var orphanEmail = CreateTestEmail();
            var callCount = 0;

            _mockRepository
                .Setup(r => r.GetPendingEmailsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    callCount++;
                    return callCount == 1
                        ? new List<Email> { orphanEmail }
                        : new List<Email>();
                });

            _mockRepository
                .Setup(r => r.GetSmtpServiceByIdAsync(orphanEmail.ServiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((SmtpServiceConfig?)null);

            // Act
            await _sut.ProcessSmtpQueueAsync(CancellationToken.None);

            // Assert — orphaned email aborted with period in message
            orphanEmail.Status.Should().Be(EmailStatus.Aborted);
            orphanEmail.ServerError.Should().Be("SMTP service not found.");
            orphanEmail.ScheduledOn.Should().BeNull();

            _mockRepository.Verify(r => r.SaveEmailAsync(
                It.Is<Email>(e => e.Id == orphanEmail.Id && e.Status == EmailStatus.Aborted),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        [Fact]
        public async Task ProcessQueue_ServiceFound_CallsSendEmail()
        {
            // Arrange — email with valid service in queue
            var email = CreateTestEmail();
            var service = CreateValidConfig();
            service.Id = email.ServiceId;
            service.IsEnabled = true;
            var callCount = 0;

            _mockRepository
                .Setup(r => r.GetPendingEmailsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    callCount++;
                    return callCount == 1
                        ? new List<Email> { email }
                        : new List<Email>();
                });

            _mockRepository
                .Setup(r => r.GetSmtpServiceByIdAsync(email.ServiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(service);

            // Act
            await _sut.ProcessSmtpQueueAsync(CancellationToken.None);

            // Assert — SES SendEmailAsync was called (proving SendEmailAsync was invoked)
            _mockSesClient.Verify(x => x.SendEmailAsync(
                It.IsAny<SendEmailRequest>(),
                It.IsAny<CancellationToken>()), Times.Once());

            // Email should be marked as sent
            email.Status.Should().Be(EmailStatus.Sent);
        }

        [Fact]
        public async Task ProcessQueue_LoopsUntilEmpty()
        {
            // Arrange — first fetch returns 2 emails, second returns 1, third returns empty
            var email1 = CreateTestEmail();
            var email2 = CreateTestEmail();
            var email3 = CreateTestEmail();
            var service = CreateValidConfig();
            service.IsEnabled = true;
            var callCount = 0;

            _mockRepository
                .Setup(r => r.GetPendingEmailsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    callCount++;
                    return callCount switch
                    {
                        1 => new List<Email> { email1, email2 },
                        2 => new List<Email> { email3 },
                        _ => new List<Email>()
                    };
                });

            _mockRepository
                .Setup(r => r.GetSmtpServiceByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(service);

            // Act
            await _sut.ProcessSmtpQueueAsync(CancellationToken.None);

            // Assert — GetPendingEmailsAsync called 3 times (2 non-empty + 1 empty to exit loop)
            _mockRepository.Verify(r => r.GetPendingEmailsAsync(
                10, It.IsAny<CancellationToken>()), Times.Exactly(3));
        }

        [Fact]
        public async Task ProcessQueue_ConcurrencyGuard_SemaphoreSlim()
        {
            // This test verifies the SemaphoreSlim(1,1) concurrency guard:
            // a second concurrent call should return immediately without processing.
            var fetchCallCount = 0;
            var blockingTcs = new TaskCompletionSource<List<Email>>();

            _mockRepository
                .Setup(r => r.GetPendingEmailsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref fetchCallCount);
                    if (fetchCallCount == 1) return blockingTcs.Task;
                    return Task.FromResult(new List<Email>());
                });

            // Start first call — acquires the semaphore and blocks on GetPendingEmailsAsync
            var firstCall = _sut.ProcessSmtpQueueAsync(CancellationToken.None);

            // Brief delay to ensure first call has acquired the semaphore
            await Task.Delay(100);

            // Second call — should return immediately (WaitAsync(0) returns false)
            await _sut.ProcessSmtpQueueAsync(CancellationToken.None);

            // Release the first call
            blockingTcs.SetResult(new List<Email>());
            await firstCall;

            // Assert — only one fetch call was made (second call was rejected by semaphore)
            fetchCallCount.Should().Be(1);
        }

        #endregion

        #region Phase 8 — PrepareEmailXSearch

        [Fact]
        public void PrepareEmailXSearch_ConcatenatesAllFields()
        {
            // Arrange
            var email = new Email
            {
                Sender = new EmailAddress("John Doe", "john@example.com"),
                Recipients = new List<EmailAddress>
                {
                    new EmailAddress("Jane Smith", "jane@example.com")
                },
                Subject = "Important Subject",
                ContentText = "Plain text content",
                ContentHtml = "<p>HTML content</p>"
            };

            // Act
            _sut.PrepareEmailXSearch(email);

            // Assert — format: "{SenderName} {SenderAddress} {recipientsText} {Subject} {ContentText} {ContentHtml}"
            // Verify direct property access on EmailAddress.Name and EmailAddress.Address
            email.Sender.Name.Should().Be("John Doe");
            email.Sender.Address.Should().Be("john@example.com");
            email.Recipients[0].Name.Should().Be("Jane Smith");
            email.Recipients[0].Address.Should().Be("jane@example.com");

            email.XSearch.Should().Contain("John Doe");
            email.XSearch.Should().Contain("john@example.com");
            email.XSearch.Should().Contain("Jane Smith");
            email.XSearch.Should().Contain("jane@example.com");
            email.XSearch.Should().Contain("Important Subject");
            email.XSearch.Should().Contain("Plain text content");
            email.XSearch.Should().Contain("<p>HTML content</p>");
        }

        [Fact]
        public void PrepareEmailXSearch_MultipleRecipients_AllIncluded()
        {
            // Arrange — 3 recipients should all appear in XSearch
            var email = new Email
            {
                Sender = new EmailAddress("Sender", "sender@example.com"),
                Recipients = new List<EmailAddress>
                {
                    new EmailAddress("Alice", "alice@example.com"),
                    new EmailAddress("Bob", "bob@example.com"),
                    new EmailAddress("Charlie", "charlie@example.com")
                },
                Subject = "Test",
                ContentText = "text",
                ContentHtml = "html"
            };

            // Act
            _sut.PrepareEmailXSearch(email);

            // Assert
            email.XSearch.Should().Contain("Alice");
            email.XSearch.Should().Contain("alice@example.com");
            email.XSearch.Should().Contain("Bob");
            email.XSearch.Should().Contain("bob@example.com");
            email.XSearch.Should().Contain("Charlie");
            email.XSearch.Should().Contain("charlie@example.com");
        }

        [Fact]
        public void PrepareEmailXSearch_NullSender_HandlesGracefully()
        {
            // Arrange — null sender should not throw NullReferenceException
            var email = new Email
            {
                Sender = null!,
                Recipients = new List<EmailAddress>
                {
                    new EmailAddress("Recipient", "recipient@example.com")
                },
                Subject = "Test",
                ContentText = "text",
                ContentHtml = "html"
            };

            // Act — should not throw
            var act = () => _sut.PrepareEmailXSearch(email);

            // Assert
            act.Should().NotThrow();
            email.XSearch.Should().NotBeNull();
            email.XSearch.Should().Contain("Recipient");
            email.XSearch.Should().Contain("recipient@example.com");
        }

        #endregion

        #region Phase 9 — HTML Content Processing

        [Fact]
        public void ProcessHtmlContent_ImgWithFsSource_GeneratesCid()
        {
            // Arrange — HTML with img tag using /fs source path
            var html = @"<html><body><img src=""/fs/images/logo.png"" alt=""Logo""></body></html>";

            // Act — uses out parameter pattern
            var processedHtml = _sut.ProcessHtmlContent(html, out var inlineResources);

            // Assert — /fs source replaced with cid: reference
            processedHtml.Should().NotContain("/fs/images/logo.png");
            processedHtml.Should().Contain("cid:");
            inlineResources.Should().HaveCount(1);
            inlineResources[0].ContentId.Should().NotBeNullOrWhiteSpace();
            inlineResources[0].FileName.Should().Be("logo.png");
            inlineResources[0].MimeType.Should().Be("image/png");
            // Verify the CID in HTML matches the resource's ContentId
            processedHtml.Should().Contain($"cid:{inlineResources[0].ContentId}");
        }

        [Fact]
        public void ProcessHtmlContent_ImgWithExternalSource_Untouched()
        {
            // Arrange — img with external URL (not /fs) should remain unchanged
            var html = @"<html><body><img src=""https://example.com/img.png"" alt=""External""></body></html>";

            // Act
            var processedHtml = _sut.ProcessHtmlContent(html, out var inlineResources);

            // Assert — external images untouched (only /fs paths processed)
            processedHtml.Should().Contain("https://example.com/img.png");
            inlineResources.Should().BeEmpty();
        }

        [Fact]
        public void ProcessHtmlContent_NullHtml_ReturnsNull()
        {
            // Arrange — null/empty input returns null-safe result

            // Act
            var processedHtml = _sut.ProcessHtmlContent(null!, out var inlineResources);

            // Assert — returns empty string for null input, no inline resources
            processedHtml.Should().BeNullOrEmpty();
            inlineResources.Should().BeEmpty();
        }

        [Fact]
        public void ConvertHtmlToPlainText_ParagraphsToNewlines()
        {
            // Arrange — </p> converts to double newline
            var html = "<p>First paragraph</p><p>Second paragraph</p>";

            // Act
            var result = _sut.ConvertHtmlToPlainText(html);

            // Assert — paragraphs separated by newlines, tags stripped
            result.Should().Contain("First paragraph");
            result.Should().Contain("Second paragraph");
            // Double newline between paragraphs (after stripping tags)
            result.Should().Contain(Environment.NewLine);
        }

        [Fact]
        public void ConvertHtmlToPlainText_BrToNewlines()
        {
            // Arrange — <br> and <br/> convert to newlines
            var html = "Line one<br>Line two<br/>Line three";

            // Act
            var result = _sut.ConvertHtmlToPlainText(html);

            // Assert
            result.Should().Contain("Line one");
            result.Should().Contain("Line two");
            result.Should().Contain("Line three");
            result.Should().Contain(Environment.NewLine);
        }

        [Fact]
        public void ConvertHtmlToPlainText_LinksToUrlFormat()
        {
            // Arrange — <a href="url">text</a> converts to "text (url)"
            var html = @"<a href=""https://example.com"">Click here</a>";

            // Act
            var result = _sut.ConvertHtmlToPlainText(html);

            // Assert — link converted to plaintext URL format
            result.Should().Contain("Click here");
            result.Should().Contain("(https://example.com)");
        }

        [Fact]
        public void ConvertHtmlToPlainText_StripsScriptAndStyle()
        {
            // Arrange — script and style blocks should be completely removed
            var html = "<style>body { color: red; }</style>" +
                       "<script>alert('hello');</script>" +
                       "<p>Visible content</p>";

            // Act
            var result = _sut.ConvertHtmlToPlainText(html);

            // Assert — script and style content fully removed
            result.Should().NotContain("color: red");
            result.Should().NotContain("alert");
            result.Should().NotContain("<script>");
            result.Should().NotContain("<style>");
            result.Should().Contain("Visible content");
        }

        #endregion
    }
}
