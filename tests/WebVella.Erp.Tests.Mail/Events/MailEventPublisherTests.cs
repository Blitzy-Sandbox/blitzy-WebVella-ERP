using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.Service.Mail.Events.Publishers;

namespace WebVella.Erp.Tests.Mail.Events
{
    /// <summary>
    /// Unit tests for all three Mail service event publishers that replace the monolith's
    /// in-process hook system with asynchronous, event-driven communication via MassTransit.
    /// <para>
    /// Publishers under test:
    /// <list type="bullet">
    ///   <item><see cref="SmtpServiceChangedEventPublisher"/> — Replaces
    ///     <c>SmtpServiceRecordHook.OnPostCreateRecord/OnPostUpdateRecord/OnPreDeleteRecord</c>
    ///     hooks that called <c>EmailServiceManager.ClearCache()</c> (monolith lines 33-50).</item>
    ///   <item><see cref="EmailSentEventPublisher"/> — Publishes an explicit domain event on
    ///     successful SMTP delivery, replacing the implicit side effect in
    ///     <c>SmtpInternalService.SendEmail()</c> when <c>email.Status = EmailStatus.Sent</c>.</item>
    ///   <item><see cref="EmailQueuedEventPublisher"/> — Publishes a domain event when an email
    ///     is queued for async processing, replacing the implicit side effect in
    ///     <c>SmtpInternalService.SaveEmail()</c> when <c>Status = Pending</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Each test method maps to a specific business rule from the monolith's hook system,
    /// ensuring zero regression in event-driven communication behavior per AAP 0.8.1 and 0.8.2.
    /// All tests use Moq for <see cref="IPublishEndpoint"/> and <see cref="ILogger{T}"/> mocking,
    /// and FluentAssertions for expressive, readable assertions on event properties.
    /// </para>
    /// </summary>
    public class MailEventPublisherTests
    {
        #region SmtpServiceChangedEventPublisher Tests

        /// <summary>
        /// Validates that <see cref="SmtpServiceChangedEventPublisher.PublishSmtpServiceCreatedAsync"/>
        /// publishes a <see cref="SmtpServiceChangedEvent"/> with <c>ChangeType = "Created"</c>
        /// and all properties correctly populated.
        /// <para>
        /// Replaces monolith behavior: <c>SmtpServiceRecordHook.OnPostCreateRecord()</c>
        /// (source lines 33-36) which called <c>EmailServiceManager.ClearCache()</c> to dispose
        /// and recreate the process-local <c>IMemoryCache</c>.
        /// </para>
        /// </summary>
        [Fact]
        public async Task SmtpServiceCreated_PublishesEventWithCorrectData()
        {
            // Arrange: Create mocks for IPublishEndpoint and ILogger
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<SmtpServiceChangedEventPublisher>>();
            SmtpServiceChangedEvent capturedEvent = null;

            mockPublishEndpoint
                .Setup(x => x.Publish(It.IsAny<SmtpServiceChangedEvent>(), It.IsAny<CancellationToken>()))
                .Callback<SmtpServiceChangedEvent, CancellationToken>((e, ct) => capturedEvent = e)
                .Returns(Task.CompletedTask);

            var publisher = new SmtpServiceChangedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            var smtpServiceId = Guid.NewGuid();
            var smtpServiceName = "test-smtp";
            var isDefault = false;
            var beforePublish = DateTime.UtcNow;

            // Act: Publish SMTP service created event
            await publisher.PublishSmtpServiceCreatedAsync(smtpServiceId, smtpServiceName, isDefault);

            var afterPublish = DateTime.UtcNow;

            // Assert: Verify Publish was called exactly once
            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<SmtpServiceChangedEvent>(), It.IsAny<CancellationToken>()),
                Times.Once());

            // Assert: Verify all event properties match expected values
            capturedEvent.Should().NotBeNull();
            capturedEvent.SmtpServiceId.Should().Be(smtpServiceId);
            capturedEvent.SmtpServiceName.Should().Be("test-smtp");
            capturedEvent.ChangeType.Should().Be("Created");
            capturedEvent.IsDefault.Should().BeFalse();
            capturedEvent.EntityName.Should().Be("smtp_service");
            capturedEvent.CorrelationId.Should().NotBe(Guid.Empty);
            capturedEvent.Timestamp.Should().BeOnOrAfter(beforePublish)
                .And.BeOnOrBefore(afterPublish);
        }

        /// <summary>
        /// Validates that <see cref="SmtpServiceChangedEventPublisher.PublishSmtpServiceUpdatedAsync"/>
        /// publishes a <see cref="SmtpServiceChangedEvent"/> with <c>ChangeType = "Updated"</c>
        /// and <c>IsDefault = true</c> when updating the default service.
        /// <para>
        /// Replaces monolith behavior: <c>SmtpServiceRecordHook.OnPostUpdateRecord()</c>
        /// (source lines 38-41) which called <c>EmailServiceManager.ClearCache()</c>.
        /// </para>
        /// </summary>
        [Fact]
        public async Task SmtpServiceUpdated_PublishesEventWithCorrectData()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<SmtpServiceChangedEventPublisher>>();
            SmtpServiceChangedEvent capturedEvent = null;

            mockPublishEndpoint
                .Setup(x => x.Publish(It.IsAny<SmtpServiceChangedEvent>(), It.IsAny<CancellationToken>()))
                .Callback<SmtpServiceChangedEvent, CancellationToken>((e, ct) => capturedEvent = e)
                .Returns(Task.CompletedTask);

            var publisher = new SmtpServiceChangedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            var smtpServiceId = Guid.NewGuid();
            var smtpServiceName = "updated-smtp";
            var isDefault = true;
            var beforePublish = DateTime.UtcNow;

            // Act
            await publisher.PublishSmtpServiceUpdatedAsync(smtpServiceId, smtpServiceName, isDefault);

            var afterPublish = DateTime.UtcNow;

            // Assert: Publish called once with Updated ChangeType and IsDefault=true
            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<SmtpServiceChangedEvent>(), It.IsAny<CancellationToken>()),
                Times.Once());

            capturedEvent.Should().NotBeNull();
            capturedEvent.SmtpServiceId.Should().Be(smtpServiceId);
            capturedEvent.SmtpServiceName.Should().Be("updated-smtp");
            capturedEvent.ChangeType.Should().Be("Updated");
            capturedEvent.IsDefault.Should().BeTrue();
            capturedEvent.EntityName.Should().Be("smtp_service");
            capturedEvent.CorrelationId.Should().NotBe(Guid.Empty);
            capturedEvent.Timestamp.Should().BeOnOrAfter(beforePublish)
                .And.BeOnOrBefore(afterPublish);
        }

        /// <summary>
        /// Validates that <see cref="SmtpServiceChangedEventPublisher.PublishSmtpServiceDeletedAsync"/>
        /// publishes a <see cref="SmtpServiceChangedEvent"/> with <c>ChangeType = "Deleted"</c>
        /// and <c>IsDefault = false</c>.
        /// <para>
        /// Replaces monolith behavior: <c>SmtpServiceRecordHook.OnPreDeleteRecord()</c>
        /// (source lines 43-50) — the else branch at line 49 that called
        /// <c>EmailServiceManager.ClearCache()</c> when the service is NOT the default.
        /// The deletion constraint (<c>service.IsDefault → errors.Add(...)</c> from lines 46-47)
        /// is enforced by the domain service BEFORE the publisher is called; therefore,
        /// delete events always carry <c>IsDefault = false</c>.
        /// </para>
        /// </summary>
        [Fact]
        public async Task SmtpServiceDeleted_PublishesEventForNonDefaultService()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<SmtpServiceChangedEventPublisher>>();
            SmtpServiceChangedEvent capturedEvent = null;

            mockPublishEndpoint
                .Setup(x => x.Publish(It.IsAny<SmtpServiceChangedEvent>(), It.IsAny<CancellationToken>()))
                .Callback<SmtpServiceChangedEvent, CancellationToken>((e, ct) => capturedEvent = e)
                .Returns(Task.CompletedTask);

            var publisher = new SmtpServiceChangedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            var smtpServiceId = Guid.NewGuid();
            var smtpServiceName = "secondary-smtp";

            // Act
            await publisher.PublishSmtpServiceDeletedAsync(smtpServiceId, smtpServiceName);

            // Assert: Event has ChangeType=Deleted and IsDefault=false (by definition)
            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<SmtpServiceChangedEvent>(), It.IsAny<CancellationToken>()),
                Times.Once());

            capturedEvent.Should().NotBeNull();
            capturedEvent.SmtpServiceId.Should().Be(smtpServiceId);
            capturedEvent.SmtpServiceName.Should().Be("secondary-smtp");
            capturedEvent.ChangeType.Should().Be("Deleted");
            // Delete events always carry IsDefault=false per monolith business rule (line 46-47)
            capturedEvent.IsDefault.Should().BeFalse();
            // EntityName matches [HookAttachment("smtp_service")] from source line 11
            capturedEvent.EntityName.Should().Be("smtp_service");
            capturedEvent.CorrelationId.Should().NotBe(Guid.Empty);
        }

        /// <summary>
        /// Validates argument validation: <c>Guid.Empty</c> smtpServiceId throws
        /// <see cref="ArgumentException"/> and Publish is never called.
        /// </summary>
        [Fact]
        public async Task SmtpServiceCreated_ThrowsOnEmptyGuid()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<SmtpServiceChangedEventPublisher>>();
            var publisher = new SmtpServiceChangedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // Act & Assert: ArgumentException thrown before Publish is reached
            await Assert.ThrowsAsync<ArgumentException>(
                () => publisher.PublishSmtpServiceCreatedAsync(Guid.Empty, "test", false));

            // Verify Publish was NOT called (exception thrown before reaching it)
            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<SmtpServiceChangedEvent>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Validates argument validation for update: <c>Guid.Empty</c> smtpServiceId throws
        /// <see cref="ArgumentException"/> and Publish is never called.
        /// </summary>
        [Fact]
        public async Task SmtpServiceUpdated_ThrowsOnEmptyGuid()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<SmtpServiceChangedEventPublisher>>();
            var publisher = new SmtpServiceChangedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => publisher.PublishSmtpServiceUpdatedAsync(Guid.Empty, "test", true));

            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<SmtpServiceChangedEvent>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Validates argument validation for delete: <c>Guid.Empty</c> smtpServiceId throws
        /// <see cref="ArgumentException"/> and Publish is never called.
        /// </summary>
        [Fact]
        public async Task SmtpServiceDeleted_ThrowsOnEmptyGuid()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<SmtpServiceChangedEventPublisher>>();
            var publisher = new SmtpServiceChangedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => publisher.PublishSmtpServiceDeletedAsync(Guid.Empty, "test"));

            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<SmtpServiceChangedEvent>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Validates that each published <see cref="SmtpServiceChangedEvent"/> receives
        /// a unique <see cref="IDomainEvent.CorrelationId"/> for distributed tracing.
        /// Two consecutive publish calls must produce different CorrelationId values.
        /// </summary>
        [Fact]
        public async Task SmtpServiceCreated_EventHasUniqueCorrelationId()
        {
            // Arrange: Capture events from two successive publish calls
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<SmtpServiceChangedEventPublisher>>();
            var capturedEvents = new List<SmtpServiceChangedEvent>();

            mockPublishEndpoint
                .Setup(x => x.Publish(It.IsAny<SmtpServiceChangedEvent>(), It.IsAny<CancellationToken>()))
                .Callback<SmtpServiceChangedEvent, CancellationToken>((e, ct) => capturedEvents.Add(e))
                .Returns(Task.CompletedTask);

            var publisher = new SmtpServiceChangedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // Act: Publish two events with different service IDs
            await publisher.PublishSmtpServiceCreatedAsync(Guid.NewGuid(), "smtp-1", false);
            await publisher.PublishSmtpServiceCreatedAsync(Guid.NewGuid(), "smtp-2", false);

            // Assert: Both events have unique, non-empty CorrelationIds
            capturedEvents.Should().HaveCount(2);
            capturedEvents[0].CorrelationId.Should().NotBe(Guid.Empty);
            capturedEvents[1].CorrelationId.Should().NotBe(Guid.Empty);
            capturedEvents[0].CorrelationId.Should().NotBe(capturedEvents[1].CorrelationId);
        }

        #endregion

        #region EmailSentEventPublisher Tests

        /// <summary>
        /// Validates that <see cref="EmailSentEventPublisher.PublishEmailSentAsync"/> publishes
        /// an <see cref="EmailSentEvent"/> with correct email metadata after successful delivery.
        /// <para>
        /// Replaces monolith behavior: The implicit success side-effect in
        /// <c>SmtpInternalService.SendEmail()</c> when <c>email.Status = EmailStatus.Sent</c>
        /// (line 802 of SmtpInternalService.cs).
        /// </para>
        /// </summary>
        [Fact]
        public async Task EmailSent_PublishesEventAfterSuccessfulDelivery()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<EmailSentEventPublisher>>();
            EmailSentEvent capturedEvent = null;

            mockPublishEndpoint
                .Setup(x => x.Publish(It.IsAny<EmailSentEvent>(), It.IsAny<CancellationToken>()))
                .Callback<EmailSentEvent, CancellationToken>((e, ct) => capturedEvent = e)
                .Returns(Task.CompletedTask);

            var publisher = new EmailSentEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            var emailId = Guid.NewGuid();
            var recipientEmail = "user@example.com";
            var senderEmail = "noreply@erp.com";
            var subject = "Test Subject";
            var sentOn = DateTime.UtcNow;
            var serviceId = Guid.NewGuid();
            var beforePublish = DateTime.UtcNow;

            // Act
            await publisher.PublishEmailSentAsync(
                emailId, recipientEmail, senderEmail, subject, sentOn, serviceId);

            var afterPublish = DateTime.UtcNow;

            // Assert: Publish called once with correct EmailSentEvent data
            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<EmailSentEvent>(), It.IsAny<CancellationToken>()),
                Times.Once());

            capturedEvent.Should().NotBeNull();
            capturedEvent.EmailId.Should().Be(emailId);
            capturedEvent.RecipientEmail.Should().Be("user@example.com");
            capturedEvent.SenderEmail.Should().Be("noreply@erp.com");
            capturedEvent.Subject.Should().Be("Test Subject");
            capturedEvent.SentOn.Should().Be(sentOn);
            capturedEvent.ServiceId.Should().Be(serviceId);
            capturedEvent.EntityName.Should().Be("email");
            capturedEvent.CorrelationId.Should().NotBe(Guid.Empty);
            capturedEvent.Timestamp.Should().BeOnOrAfter(beforePublish)
                .And.BeOnOrBefore(afterPublish);
        }

        /// <summary>
        /// Validates argument validation: <c>Guid.Empty</c> emailId throws
        /// <see cref="ArgumentException"/> and Publish is never called.
        /// </summary>
        [Fact]
        public async Task EmailSent_ThrowsOnEmptyEmailId()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<EmailSentEventPublisher>>();
            var publisher = new EmailSentEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => publisher.PublishEmailSentAsync(
                    Guid.Empty, "user@example.com", "sender@erp.com",
                    "Subject", DateTime.UtcNow, Guid.NewGuid()));

            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<EmailSentEvent>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Validates argument validation: null recipientEmail throws
        /// <see cref="ArgumentException"/> and Publish is never called.
        /// </summary>
        [Fact]
        public async Task EmailSent_ThrowsOnNullRecipientEmail()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<EmailSentEventPublisher>>();
            var publisher = new EmailSentEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => publisher.PublishEmailSentAsync(
                    Guid.NewGuid(), null, "sender@erp.com",
                    "Subject", DateTime.UtcNow, Guid.NewGuid()));

            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<EmailSentEvent>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Validates fire-and-forget resilience: publisher handles null subject gracefully
        /// by normalizing it to <see cref="string.Empty"/> in the event payload.
        /// The publisher code does <c>Subject = subject ?? string.Empty</c>.
        /// </summary>
        [Fact]
        public async Task EmailSent_HandlesNullSubjectGracefully()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<EmailSentEventPublisher>>();
            EmailSentEvent capturedEvent = null;

            mockPublishEndpoint
                .Setup(x => x.Publish(It.IsAny<EmailSentEvent>(), It.IsAny<CancellationToken>()))
                .Callback<EmailSentEvent, CancellationToken>((e, ct) => capturedEvent = e)
                .Returns(Task.CompletedTask);

            var publisher = new EmailSentEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // Act: Pass null subject — should not throw
            await publisher.PublishEmailSentAsync(
                Guid.NewGuid(), "user@example.com", "noreply@erp.com",
                null, DateTime.UtcNow, Guid.NewGuid());

            // Assert: Event published with Subject normalized to empty string
            capturedEvent.Should().NotBeNull();
            capturedEvent.Subject.Should().Be(string.Empty);
        }

        /// <summary>
        /// Validates that the event <see cref="IDomainEvent.Timestamp"/> is set to UTC now
        /// (within a tight window around the publish call), and that
        /// <see cref="EmailSentEvent.SentOn"/> matches the caller-provided value.
        /// </summary>
        [Fact]
        public async Task EmailSent_EventCarriesCorrectTimestamp()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<EmailSentEventPublisher>>();
            EmailSentEvent capturedEvent = null;

            mockPublishEndpoint
                .Setup(x => x.Publish(It.IsAny<EmailSentEvent>(), It.IsAny<CancellationToken>()))
                .Callback<EmailSentEvent, CancellationToken>((e, ct) => capturedEvent = e)
                .Returns(Task.CompletedTask);

            var publisher = new EmailSentEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // SentOn is a historical value (e.g., 5 minutes ago) — different from Timestamp
            var sentOn = DateTime.UtcNow.AddMinutes(-5);
            var beforePublish = DateTime.UtcNow;

            // Act
            await publisher.PublishEmailSentAsync(
                Guid.NewGuid(), "user@example.com", "sender@erp.com",
                "Subject", sentOn, Guid.NewGuid());

            var afterPublish = DateTime.UtcNow;

            // Assert: Timestamp is the event creation time (close to now)
            capturedEvent.Should().NotBeNull();
            capturedEvent.Timestamp.Should().BeOnOrAfter(beforePublish)
                .And.BeOnOrBefore(afterPublish);

            // Assert: SentOn matches the passed-in value exactly (not event timestamp)
            capturedEvent.SentOn.Should().Be(sentOn);
        }

        #endregion

        #region EmailQueuedEventPublisher Tests

        /// <summary>
        /// Validates that <see cref="EmailQueuedEventPublisher.PublishEmailQueuedAsync"/> publishes
        /// an <see cref="EmailQueuedEvent"/> with correct queue metadata.
        /// <para>
        /// Replaces monolith behavior: The implicit side-effect in
        /// <c>SmtpInternalService.SaveEmail()</c> when a new email is created with
        /// <c>Status = Pending</c>.
        /// </para>
        /// </summary>
        [Fact]
        public async Task EmailQueued_PublishesEventWhenEmailQueued()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<EmailQueuedEventPublisher>>();
            EmailQueuedEvent capturedEvent = null;

            mockPublishEndpoint
                .Setup(x => x.Publish(It.IsAny<EmailQueuedEvent>(), It.IsAny<CancellationToken>()))
                .Callback<EmailQueuedEvent, CancellationToken>((e, ct) => capturedEvent = e)
                .Returns(Task.CompletedTask);

            var publisher = new EmailQueuedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            var emailId = Guid.NewGuid();
            var recipientEmail = "queued@example.com";
            var subject = "Queued Mail";
            var priority = 1; // Normal (from EmailPriority enum: Low=0, Normal=1, High=2)
            var scheduledOn = DateTime.UtcNow.AddMinutes(10);
            var serviceId = Guid.NewGuid();

            // Act
            await publisher.PublishEmailQueuedAsync(
                emailId, recipientEmail, subject, priority, scheduledOn, serviceId);

            // Assert: Publish called once with correct EmailQueuedEvent data
            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<EmailQueuedEvent>(), It.IsAny<CancellationToken>()),
                Times.Once());

            capturedEvent.Should().NotBeNull();
            capturedEvent.EmailId.Should().Be(emailId);
            capturedEvent.RecipientEmail.Should().Be("queued@example.com");
            capturedEvent.Subject.Should().Be("Queued Mail");
            capturedEvent.Priority.Should().Be(1);
            capturedEvent.ScheduledOn.Should().Be(scheduledOn);
            capturedEvent.ServiceId.Should().Be(serviceId);
            capturedEvent.EntityName.Should().Be("email");
            capturedEvent.CorrelationId.Should().NotBe(Guid.Empty);
        }

        /// <summary>
        /// Validates argument validation: <c>Guid.Empty</c> emailId throws
        /// <see cref="ArgumentException"/> and Publish is never called.
        /// </summary>
        [Fact]
        public async Task EmailQueued_ThrowsOnEmptyEmailId()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<EmailQueuedEventPublisher>>();
            var publisher = new EmailQueuedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => publisher.PublishEmailQueuedAsync(
                    Guid.Empty, "user@example.com", "Subject",
                    1, DateTime.UtcNow, Guid.NewGuid()));

            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<EmailQueuedEvent>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Validates argument validation: null recipientEmail throws
        /// <see cref="ArgumentException"/> and Publish is never called.
        /// </summary>
        [Fact]
        public async Task EmailQueued_ThrowsOnNullRecipientEmail()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<EmailQueuedEventPublisher>>();
            var publisher = new EmailQueuedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => publisher.PublishEmailQueuedAsync(
                    Guid.NewGuid(), null, "Subject",
                    1, DateTime.UtcNow, Guid.NewGuid()));

            mockPublishEndpoint.Verify(
                x => x.Publish(It.IsAny<EmailQueuedEvent>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Validates that the publisher accepts <c>null</c> scheduledOn for immediate processing.
        /// When <c>scheduledOn = null</c>, the event carries <c>ScheduledOn = null</c>,
        /// signaling the mail queue processor to process the email immediately.
        /// </summary>
        [Fact]
        public async Task EmailQueued_HandlesNullScheduledOn()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<EmailQueuedEventPublisher>>();
            EmailQueuedEvent capturedEvent = null;

            mockPublishEndpoint
                .Setup(x => x.Publish(It.IsAny<EmailQueuedEvent>(), It.IsAny<CancellationToken>()))
                .Callback<EmailQueuedEvent, CancellationToken>((e, ct) => capturedEvent = e)
                .Returns(Task.CompletedTask);

            var publisher = new EmailQueuedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // Act: Pass null scheduledOn for immediate processing
            await publisher.PublishEmailQueuedAsync(
                Guid.NewGuid(), "user@example.com", "Subject",
                1, null, Guid.NewGuid());

            // Assert: Event published with ScheduledOn = null
            capturedEvent.Should().NotBeNull();
            capturedEvent.ScheduledOn.Should().BeNull();
        }

        /// <summary>
        /// Validates that the publisher correctly carries the high-priority value (2)
        /// for urgent emails. Corresponds to the monolith's <c>EmailPriority.High = 2</c> enum
        /// (from <c>WebVella.Erp.Plugins.Mail.Api.EmailPriority</c>), used by
        /// <c>SmtpInternalService.ProcessSmtpQueue()</c> which orders by <c>priority DESC</c>.
        /// </summary>
        [Fact]
        public async Task EmailQueued_SupportsHighPriority()
        {
            // Arrange
            var mockPublishEndpoint = new Mock<IPublishEndpoint>();
            var mockLogger = new Mock<ILogger<EmailQueuedEventPublisher>>();
            EmailQueuedEvent capturedEvent = null;

            mockPublishEndpoint
                .Setup(x => x.Publish(It.IsAny<EmailQueuedEvent>(), It.IsAny<CancellationToken>()))
                .Callback<EmailQueuedEvent, CancellationToken>((e, ct) => capturedEvent = e)
                .Returns(Task.CompletedTask);

            var publisher = new EmailQueuedEventPublisher(
                mockPublishEndpoint.Object, mockLogger.Object);

            // Act: Queue email with High priority (2)
            await publisher.PublishEmailQueuedAsync(
                Guid.NewGuid(), "user@example.com", "Urgent Mail",
                2, DateTime.UtcNow, Guid.NewGuid());

            // Assert: Event carries Priority = 2 (High)
            capturedEvent.Should().NotBeNull();
            capturedEvent.Priority.Should().Be(2);
        }

        #endregion

        #region Cross-Cutting Event Contract Verification Tests

        /// <summary>
        /// Verifies that all three event types used by Mail service publishers implement
        /// the <see cref="IDomainEvent"/> interface from SharedKernel, ensuring they
        /// participate in the MassTransit event-driven messaging system with required
        /// cross-cutting metadata (Timestamp, CorrelationId, EntityName).
        /// </summary>
        [Fact]
        public void AllPublishedEvents_ImplementIDomainEvent()
        {
            // Assert: All event types implement IDomainEvent from SharedKernel
            typeof(SmtpServiceChangedEvent).GetInterfaces()
                .Should().Contain(typeof(IDomainEvent));

            typeof(EmailSentEvent).GetInterfaces()
                .Should().Contain(typeof(IDomainEvent));

            typeof(EmailQueuedEvent).GetInterfaces()
                .Should().Contain(typeof(IDomainEvent));
        }

        /// <summary>
        /// Verifies that each event type's default constructor sets the correct
        /// <see cref="IDomainEvent.EntityName"/> value, matching the monolith's entity
        /// naming convention used by <c>HookManager.GetHookedInstances</c>.
        /// <list type="bullet">
        ///   <item><see cref="SmtpServiceChangedEvent"/> → <c>"smtp_service"</c>
        ///     (matches <c>[HookAttachment("smtp_service")]</c>)</item>
        ///   <item><see cref="EmailSentEvent"/> → <c>"email"</c></item>
        ///   <item><see cref="EmailQueuedEvent"/> → <c>"email"</c></item>
        /// </list>
        /// </summary>
        [Fact]
        public void AllPublishedEvents_HaveCorrectDefaultEntityName()
        {
            // Assert: Default constructor sets correct entity name
            new SmtpServiceChangedEvent().EntityName.Should().Be("smtp_service");
            new EmailSentEvent().EntityName.Should().Be("email");
            new EmailQueuedEvent().EntityName.Should().Be("email");
        }

        #endregion
    }
}
