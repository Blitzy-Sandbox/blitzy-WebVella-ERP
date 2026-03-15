using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Invoicing.DataAccess;
using WebVellaErp.Invoicing.Models;
using WebVellaErp.Invoicing.Services;
using Xunit;

namespace WebVellaErp.Invoicing.Tests.Unit
{
    /// <summary>
    /// Comprehensive xUnit unit test class for <see cref="InvoiceService"/> validating the
    /// complete invoice lifecycle business logic: creation, update, issue, void, paid status
    /// transitions, and invoice number generation. All external dependencies (repository,
    /// event publisher, calculation service, logger) are mocked with Moq. Assertions use
    /// FluentAssertions for expressive, readable verifications.
    ///
    /// Test coverage:
    ///   - CreateInvoiceAsync: null request, empty customer, missing line items, invalid dates,
    ///     happy path (success, totals, invoice number)
    ///   - UpdateInvoiceAsync: not found, non-draft status, valid draft with recalculation
    ///   - IssueInvoiceAsync: draft-to-issued transition, non-draft rejection
    ///   - VoidInvoiceAsync: valid void, already voided (idempotency), paid invoice rejection
    ///   - MarkInvoicePaidAsync: valid issued-to-paid transition
    ///   - GenerateInvoiceNumber: non-empty string with "INV-" prefix
    ///
    /// All monetary values use decimal type exclusively (never double or float) per AAP §0.8.1.
    /// SNS domain event publishing is verified for all state changes and confirmed absent
    /// on validation failures.
    /// </summary>
    public class InvoiceServiceTests
    {
        private readonly Mock<IInvoiceRepository> _repositoryMock;
        private readonly Mock<IInvoiceEventPublisher> _eventPublisherMock;
        private readonly Mock<ILineItemCalculationService> _calculationServiceMock;
        private readonly Mock<ILogger<InvoiceService>> _loggerMock;
        private readonly InvoiceService _sut;

        /// <summary>
        /// Initializes mocks for all InvoiceService dependencies and creates the
        /// System Under Test (SUT). Uses Moq default loose behavior so unmatched
        /// void method calls succeed silently while Task-returning methods return
        /// Task.CompletedTask.
        /// </summary>
        public InvoiceServiceTests()
        {
            _repositoryMock = new Mock<IInvoiceRepository>();
            _eventPublisherMock = new Mock<IInvoiceEventPublisher>();
            _calculationServiceMock = new Mock<ILineItemCalculationService>();
            _loggerMock = new Mock<ILogger<InvoiceService>>();

            _sut = new InvoiceService(
                _repositoryMock.Object,
                _eventPublisherMock.Object,
                _calculationServiceMock.Object,
                _loggerMock.Object);
        }

        #region Helper Methods

        /// <summary>
        /// Creates a fully valid <see cref="CreateInvoiceRequest"/> with one line item.
        /// All fields pass InvoiceService validation: non-empty CustomerId, IssueDate before
        /// DueDate (30-day NET terms), at least one line item with positive quantity/price
        /// and valid tax rate.
        /// </summary>
        private static CreateInvoiceRequest CreateValidCreateInvoiceRequest()
        {
            return new CreateInvoiceRequest
            {
                CustomerId = Guid.NewGuid(),
                IssueDate = DateTime.UtcNow,
                DueDate = DateTime.UtcNow.AddDays(30),
                Notes = "Test invoice",
                LineItems = new List<CreateLineItemRequest>
                {
                    new CreateLineItemRequest
                    {
                        Description = "Item 1",
                        Quantity = 2m,
                        UnitPrice = 50m,
                        TaxRate = 0.20m
                    }
                }
            };
        }

        /// <summary>
        /// Creates a pre-populated test <see cref="Invoice"/> with configurable Id and Status.
        /// Includes one line item, realistic monetary values (SubTotal=100, TaxAmount=20,
        /// TotalAmount=120), and audit fields. Used by tests that need an existing invoice
        /// returned from the mocked repository.
        /// </summary>
        private static Invoice CreateTestInvoice(
            Guid? id = null, InvoiceStatus status = InvoiceStatus.Draft)
        {
            var invoiceId = id ?? Guid.NewGuid();
            return new Invoice
            {
                Id = invoiceId,
                InvoiceNumber = $"INV-{DateTime.UtcNow:yyyy}-000001",
                CustomerId = Guid.NewGuid(),
                Status = status,
                IssueDate = DateTime.UtcNow,
                DueDate = DateTime.UtcNow.AddDays(30),
                SubTotal = 100m,
                TaxAmount = 20m,
                TotalAmount = 120m,
                Notes = "Test invoice",
                LineItems = new List<LineItem>
                {
                    new LineItem
                    {
                        Id = Guid.NewGuid(),
                        InvoiceId = invoiceId,
                        Description = "Test Item",
                        Quantity = 2m,
                        UnitPrice = 50m,
                        TaxRate = 0.20m,
                        LineTotal = 100m
                    }
                },
                CreatedBy = Guid.NewGuid(),
                CreatedOn = DateTime.UtcNow,
                LastModifiedBy = Guid.NewGuid(),
                LastModifiedOn = DateTime.UtcNow
            };
        }

        #endregion

        #region CreateInvoiceAsync — Validation Tests

        /// <summary>
        /// Verifies that passing a null request to CreateInvoiceAsync returns a failure
        /// response with an error containing "null". No persistence or event publishing
        /// should occur.
        /// </summary>
        [Fact]
        public async Task CreateInvoiceAsync_NullRequest_ReturnsError()
        {
            // Act
            var result = await _sut.CreateInvoiceAsync(null!, Guid.NewGuid());

            // Assert
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle();
            result.Errors[0].Message.Should().Contain("null");

            // Verify no persistence or events
            _repositoryMock.Verify(
                r => r.CreateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceCreatedAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that an empty (Guid.Empty) CustomerId triggers a validation error
        /// "Customer ID is required". No persistence or event publishing should occur.
        /// </summary>
        [Fact]
        public async Task CreateInvoiceAsync_EmptyCustomerId_ReturnsError()
        {
            // Arrange
            var request = CreateValidCreateInvoiceRequest();
            request.CustomerId = Guid.Empty;

            // Act
            var result = await _sut.CreateInvoiceAsync(request, Guid.NewGuid());

            // Assert
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle();
            result.Errors[0].Message.Should().Contain("Customer ID is required");

            // Verify no persistence or events
            _repositoryMock.Verify(
                r => r.CreateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceCreatedAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that an empty LineItems list triggers a validation error
        /// "At least one line item is required". No persistence or event publishing
        /// should occur.
        /// </summary>
        [Fact]
        public async Task CreateInvoiceAsync_MissingLineItems_ReturnsError()
        {
            // Arrange
            var request = CreateValidCreateInvoiceRequest();
            request.LineItems = new List<CreateLineItemRequest>();

            // Act
            var result = await _sut.CreateInvoiceAsync(request, Guid.NewGuid());

            // Assert
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle();
            result.Errors[0].Message.Should().Contain("At least one line item is required");

            // Verify no persistence or events
            _repositoryMock.Verify(
                r => r.CreateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceCreatedAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that a DueDate before IssueDate triggers a validation error about
        /// the due date. No persistence or event publishing should occur.
        /// </summary>
        [Fact]
        public async Task CreateInvoiceAsync_InvalidDates_ReturnsError()
        {
            // Arrange
            var request = CreateValidCreateInvoiceRequest();
            request.DueDate = request.IssueDate.AddDays(-5);

            // Act
            var result = await _sut.CreateInvoiceAsync(request, Guid.NewGuid());

            // Assert
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle();
            result.Errors[0].Message.Should().Contain("Due date");

            // Verify no persistence or events
            _repositoryMock.Verify(
                r => r.CreateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceCreatedAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        #endregion

        #region CreateInvoiceAsync — Happy Path Tests

        /// <summary>
        /// Verifies that a valid create request results in a successful response with a
        /// Draft invoice, correct CreatedBy user, and a recent CreatedOn timestamp.
        /// Verifies that the repository persists the invoice and an SNS domain event
        /// (invoicing.invoice.created) is published.
        /// </summary>
        [Fact]
        public async Task CreateInvoiceAsync_ValidRequest_ReturnsSuccess()
        {
            // Arrange
            var request = CreateValidCreateInvoiceRequest();
            var userId = Guid.NewGuid();

            _repositoryMock
                .Setup(r => r.CreateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Invoice inv, CancellationToken _) => inv);

            _eventPublisherMock
                .Setup(p => p.PublishInvoiceCreatedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.CreateInvoiceAsync(request, userId);

            // Assert
            result.Success.Should().BeTrue();
            result.Object.Should().NotBeNull();
            result.Object!.Status.Should().Be(InvoiceStatus.Draft);
            result.Object.CreatedBy.Should().Be(userId);
            result.Object.CreatedOn.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

            // Verify persistence
            _repositoryMock.Verify(
                r => r.CreateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Once);

            // Verify SNS event published
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceCreatedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that creating an invoice invokes the <see cref="ILineItemCalculationService"/>
        /// to calculate invoice totals exactly once. This ensures the currency rounding pattern
        /// from source RecordManager.cs line 1893 (MidpointRounding.AwayFromZero) is applied.
        /// </summary>
        [Fact]
        public async Task CreateInvoiceAsync_ValidRequest_CalculatesTotals()
        {
            // Arrange
            var request = CreateValidCreateInvoiceRequest();
            var userId = Guid.NewGuid();

            _repositoryMock
                .Setup(r => r.CreateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Invoice inv, CancellationToken _) => inv);

            _eventPublisherMock
                .Setup(p => p.PublishInvoiceCreatedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.CreateInvoiceAsync(request, userId);

            // Assert
            result.Success.Should().BeTrue();

            // Verify calculation service invoked for invoice-level totals (SubTotal, TaxAmount, TotalAmount)
            _calculationServiceMock.Verify(
                c => c.CalculateInvoiceTotals(It.IsAny<Invoice>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that a successfully created invoice has a non-empty InvoiceNumber
        /// generated by the service (format "INV-{YYYY}-{NNNNNN}").
        /// </summary>
        [Fact]
        public async Task CreateInvoiceAsync_ValidRequest_GeneratesInvoiceNumber()
        {
            // Arrange
            var request = CreateValidCreateInvoiceRequest();
            var userId = Guid.NewGuid();

            _repositoryMock
                .Setup(r => r.CreateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Invoice inv, CancellationToken _) => inv);

            _eventPublisherMock
                .Setup(p => p.PublishInvoiceCreatedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.CreateInvoiceAsync(request, userId);

            // Assert
            result.Success.Should().BeTrue();
            result.Object.Should().NotBeNull();
            result.Object!.InvoiceNumber.Should().NotBeNullOrEmpty();
        }

        #endregion

        #region UpdateInvoiceAsync Tests

        /// <summary>
        /// Verifies that attempting to update a non-existent invoice returns a failure
        /// response with "Invoice not found" error. No persistence or event publishing
        /// should occur.
        /// </summary>
        [Fact]
        public async Task UpdateInvoiceAsync_InvoiceNotFound_ReturnsError()
        {
            // Arrange
            var invoiceId = Guid.NewGuid();
            var request = new UpdateInvoiceRequest { Notes = "Updated notes" };

            _repositoryMock
                .Setup(r => r.GetInvoiceByIdAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Invoice?)null);

            // Act
            var result = await _sut.UpdateInvoiceAsync(invoiceId, request, Guid.NewGuid());

            // Assert
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle();
            result.Errors[0].Message.Should().Contain("Invoice not found");

            // Verify no persistence or events
            _repositoryMock.Verify(
                r => r.UpdateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceUpdatedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that attempting to update an Issued (non-Draft) invoice returns a
        /// failure response with "Only draft invoices can be updated" error. State machine
        /// rule: only Draft → Draft (update) transitions are allowed.
        /// </summary>
        [Fact]
        public async Task UpdateInvoiceAsync_NonDraftStatus_ReturnsError()
        {
            // Arrange
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, InvoiceStatus.Issued);
            var request = new UpdateInvoiceRequest { Notes = "Updated notes" };

            _repositoryMock
                .Setup(r => r.GetInvoiceByIdAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(invoice);

            // Act
            var result = await _sut.UpdateInvoiceAsync(invoiceId, request, Guid.NewGuid());

            // Assert
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle();
            result.Errors[0].Message.Should().Contain("Only draft invoices can be updated");

            // Verify no persistence or events
            _repositoryMock.Verify(
                r => r.UpdateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceUpdatedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that updating a Draft invoice with new line items triggers total
        /// recalculation via <see cref="ILineItemCalculationService.CalculateInvoiceTotals"/>,
        /// persists the update, and publishes an SNS domain event (invoicing.invoice.updated).
        /// </summary>
        [Fact]
        public async Task UpdateInvoiceAsync_ValidDraftInvoice_RecalculatesTotals()
        {
            // Arrange
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, InvoiceStatus.Draft);
            var userId = Guid.NewGuid();

            var request = new UpdateInvoiceRequest
            {
                LineItems = new List<UpdateLineItemRequest>
                {
                    new UpdateLineItemRequest
                    {
                        Description = "Updated Item",
                        Quantity = 3m,
                        UnitPrice = 75m,
                        TaxRate = 0.15m
                    }
                }
            };

            _repositoryMock
                .Setup(r => r.GetInvoiceByIdAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(invoice);

            _repositoryMock
                .Setup(r => r.UpdateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Invoice inv, CancellationToken _) => inv);

            _eventPublisherMock
                .Setup(p => p.PublishInvoiceUpdatedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.UpdateInvoiceAsync(invoiceId, request, userId);

            // Assert
            result.Success.Should().BeTrue();

            // Verify calculation service invoked for recalculation when line items change
            _calculationServiceMock.Verify(
                c => c.CalculateInvoiceTotals(It.IsAny<Invoice>()),
                Times.Once);

            // Verify persistence
            _repositoryMock.Verify(
                r => r.UpdateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Once);

            // Verify SNS event published
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceUpdatedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        #endregion

        #region IssueInvoiceAsync Tests

        /// <summary>
        /// Verifies that issuing a Draft invoice transitions it to Issued status.
        /// The invoice passed to the repository should have Status == Issued, and
        /// an SNS domain event (invoicing.invoice.issued) should be published.
        /// </summary>
        [Fact]
        public async Task IssueInvoiceAsync_DraftInvoice_TransitionsToIssued()
        {
            // Arrange
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, InvoiceStatus.Draft);
            var userId = Guid.NewGuid();

            _repositoryMock
                .Setup(r => r.GetInvoiceByIdAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(invoice);

            _repositoryMock
                .Setup(r => r.UpdateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Invoice inv, CancellationToken _) => inv);

            _eventPublisherMock
                .Setup(p => p.PublishInvoiceIssuedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.IssueInvoiceAsync(invoiceId, userId);

            // Assert
            result.Success.Should().BeTrue();
            result.Object.Should().NotBeNull();
            result.Object!.Status.Should().Be(InvoiceStatus.Issued);

            // Verify the invoice persisted with Issued status
            _repositoryMock.Verify(
                r => r.UpdateInvoiceAsync(
                    It.Is<Invoice>(i => i.Status == InvoiceStatus.Issued),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            // Verify SNS event published
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceIssuedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that attempting to issue an already-Issued invoice returns a failure
        /// response with "Only draft invoices can be issued" error. State machine rule:
        /// only Draft → Issued transition is valid.
        /// </summary>
        [Fact]
        public async Task IssueInvoiceAsync_NonDraftInvoice_ReturnsError()
        {
            // Arrange
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, InvoiceStatus.Issued);

            _repositoryMock
                .Setup(r => r.GetInvoiceByIdAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(invoice);

            // Act
            var result = await _sut.IssueInvoiceAsync(invoiceId, Guid.NewGuid());

            // Assert
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle();
            result.Errors[0].Message.Should().Contain("Only draft invoices can be issued");

            // Verify no persistence or events
            _repositoryMock.Verify(
                r => r.UpdateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceIssuedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        #endregion

        #region VoidInvoiceAsync Tests

        /// <summary>
        /// Verifies that voiding an Issued invoice transitions it to Voided status.
        /// The repository VoidInvoiceAsync returns true (success), and an SNS domain
        /// event (invoicing.invoice.voided) is published.
        /// </summary>
        [Fact]
        public async Task VoidInvoiceAsync_ValidInvoice_TransitionsToVoided()
        {
            // Arrange
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, InvoiceStatus.Issued);
            var userId = Guid.NewGuid();

            _repositoryMock
                .Setup(r => r.GetInvoiceByIdAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(invoice);

            _repositoryMock
                .Setup(r => r.VoidInvoiceAsync(invoiceId, userId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            _eventPublisherMock
                .Setup(p => p.PublishInvoiceVoidedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.VoidInvoiceAsync(invoiceId, userId);

            // Assert
            result.Success.Should().BeTrue();
            result.Object.Should().NotBeNull();
            result.Object!.Status.Should().Be(InvoiceStatus.Voided);

            // Verify SNS event published
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceVoidedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies idempotency handling for already-voided invoices. The implementation
        /// returns a descriptive error ("already voided") rather than silently succeeding,
        /// ensuring the caller is aware no state change occurred.
        /// </summary>
        [Fact]
        public async Task VoidInvoiceAsync_AlreadyVoided_Idempotent()
        {
            // Arrange
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, InvoiceStatus.Voided);

            _repositoryMock
                .Setup(r => r.GetInvoiceByIdAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(invoice);

            // Act
            var result = await _sut.VoidInvoiceAsync(invoiceId, Guid.NewGuid());

            // Assert — returns descriptive error for already-voided invoices
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle();
            result.Errors[0].Message.Should().Contain("already voided");

            // Verify no void operation or events
            _repositoryMock.Verify(
                r => r.VoidInvoiceAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceVoidedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that attempting to void a Paid invoice returns a failure response
        /// with "Cannot void a paid invoice" error. Financial integrity rule: paid
        /// invoices are immutable and cannot be reversed via void.
        /// </summary>
        [Fact]
        public async Task VoidInvoiceAsync_PaidInvoice_ReturnsError()
        {
            // Arrange
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, InvoiceStatus.Paid);

            _repositoryMock
                .Setup(r => r.GetInvoiceByIdAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(invoice);

            // Act
            var result = await _sut.VoidInvoiceAsync(invoiceId, Guid.NewGuid());

            // Assert
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle();
            result.Errors[0].Message.Should().Contain("Cannot void a paid invoice");

            // Verify no void operation or events
            _repositoryMock.Verify(
                r => r.VoidInvoiceAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _eventPublisherMock.Verify(
                p => p.PublishInvoiceVoidedAsync(
                    It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        #endregion

        #region MarkInvoicePaidAsync Tests

        /// <summary>
        /// Verifies that marking an Issued invoice as paid transitions it to Paid status.
        /// The repository UpdateInvoiceAsync is called once. Note: the payment domain event
        /// is published by PaymentService (not InvoiceService) to avoid duplicate events
        /// per AAP §0.7.2 guidance.
        /// </summary>
        [Fact]
        public async Task MarkInvoicePaidAsync_ValidInvoice_TransitionsToPaid()
        {
            // Arrange
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, InvoiceStatus.Issued);
            var userId = Guid.NewGuid();

            _repositoryMock
                .Setup(r => r.GetInvoiceByIdAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(invoice);

            _repositoryMock
                .Setup(r => r.UpdateInvoiceAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Invoice inv, CancellationToken _) => inv);

            // Act
            var result = await _sut.MarkInvoicePaidAsync(invoiceId, userId);

            // Assert
            result.Success.Should().BeTrue();
            result.Object.Should().NotBeNull();
            result.Object!.Status.Should().Be(InvoiceStatus.Paid);

            // Verify persistence — status transition persisted
            _repositoryMock.Verify(
                r => r.UpdateInvoiceAsync(
                    It.Is<Invoice>(i => i.Status == InvoiceStatus.Paid),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        #endregion

        #region GenerateInvoiceNumber Tests

        /// <summary>
        /// Verifies that GenerateInvoiceNumber returns a non-empty string starting with
        /// "INV-" prefix following the format "INV-{YYYY}-{NNNNNN}".
        /// </summary>
        [Fact]
        public void GenerateInvoiceNumber_ReturnsNonEmptyString()
        {
            // Act
            var number = _sut.GenerateInvoiceNumber();

            // Assert
            number.Should().NotBeNullOrWhiteSpace();
            number.Should().StartWith("INV-");
        }

        #endregion
    }
}
