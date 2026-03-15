using Xunit;
using Moq;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WebVellaErp.Invoicing.Services;
using WebVellaErp.Invoicing.DataAccess;
using WebVellaErp.Invoicing.Models;

namespace WebVellaErp.Invoicing.Tests.Unit
{
    /// <summary>
    /// Comprehensive unit tests for PaymentService covering payment processing validation,
    /// recording, partial payment support, auto-transition to Paid status, and balance calculations.
    /// All external dependencies mocked with Moq, assertions with FluentAssertions.
    /// All monetary amounts use decimal exclusively (NEVER double or float).
    /// </summary>
    public class PaymentServiceTests
    {
        private readonly Mock<IInvoiceRepository> _repositoryMock;
        private readonly Mock<IInvoiceEventPublisher> _eventPublisherMock;
        private readonly Mock<IInvoiceService> _invoiceServiceMock;
        private readonly Mock<ILogger<PaymentService>> _loggerMock;
        private readonly PaymentService _sut;

        public PaymentServiceTests()
        {
            _repositoryMock = new Mock<IInvoiceRepository>();
            _eventPublisherMock = new Mock<IInvoiceEventPublisher>();
            _invoiceServiceMock = new Mock<IInvoiceService>();
            _loggerMock = new Mock<ILogger<PaymentService>>();

            _sut = new PaymentService(
                _repositoryMock.Object,
                _eventPublisherMock.Object,
                _invoiceServiceMock.Object,
                _loggerMock.Object);
        }

        #region Test Helpers

        /// <summary>
        /// Creates a valid CreatePaymentRequest with sensible defaults for testing.
        /// Uses decimal for amount (never double or float) per AAP Section 0.8.
        /// </summary>
        private static CreatePaymentRequest CreateValidCreatePaymentRequest(
            Guid invoiceId,
            decimal amount = 100m)
        {
            return new CreatePaymentRequest
            {
                InvoiceId = invoiceId,
                Amount = amount,
                PaymentDate = DateTime.UtcNow,
                PaymentMethod = PaymentMethod.BankTransfer,
                ReferenceNumber = "REF-001",
                Notes = "Test payment"
            };
        }

        /// <summary>
        /// Creates a fully populated test Invoice with configurable Id, TotalAmount, and Status.
        /// Default status is Issued (the only status that accepts payments).
        /// </summary>
        private static Invoice CreateTestInvoice(
            Guid id,
            decimal totalAmount = 1000m,
            InvoiceStatus status = InvoiceStatus.Issued)
        {
            return new Invoice
            {
                Id = id,
                InvoiceNumber = "INV-TEST-001",
                CustomerId = Guid.NewGuid(),
                Status = status,
                IssueDate = DateTime.UtcNow.AddDays(-30),
                DueDate = DateTime.UtcNow.AddDays(30),
                SubTotal = totalAmount,
                TaxAmount = 0m,
                TotalAmount = totalAmount,
                Notes = "Test invoice"
            };
        }

        /// <summary>
        /// Configures mock to return the specified invoice when looked up by its Id.
        /// </summary>
        private void SetupInvoiceLookup(Invoice invoice)
        {
            _repositoryMock
                .Setup(r => r.GetInvoiceByIdAsync(invoice.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(invoice);
        }

        /// <summary>
        /// Configures mock to return the specified list of existing payments for an invoice.
        /// Used for partial payment sum calculations and overpayment checks.
        /// </summary>
        private void SetupExistingPayments(Guid invoiceId, List<Payment> payments)
        {
            _repositoryMock
                .Setup(r => r.ListPaymentsForInvoiceAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(payments);
        }

        /// <summary>
        /// Configures mock to pass through CreatePaymentAsync, returning the same Payment
        /// that was passed in (simulates successful persistence).
        /// </summary>
        private void SetupCreatePaymentPassthrough()
        {
            _repositoryMock
                .Setup(r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Payment p, CancellationToken _) => p);
        }

        /// <summary>
        /// Configures event publisher mocks to complete successfully for both
        /// PublishPaymentProcessedAsync (SNS: invoicing.payment.processed) and
        /// PublishInvoicePaidAsync (SNS: invoicing.invoice.paid).
        /// </summary>
        private void SetupEventPublisherDefaults()
        {
            _eventPublisherMock
                .Setup(p => p.PublishPaymentProcessedAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _eventPublisherMock
                .Setup(p => p.PublishInvoicePaidAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        /// <summary>
        /// Configures IInvoiceService.MarkInvoicePaidAsync mock to return success.
        /// Only needed in tests where the invoice becomes fully paid.
        /// </summary>
        private void SetupMarkInvoicePaid()
        {
            _invoiceServiceMock
                .Setup(s => s.MarkInvoicePaidAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InvoiceResponse());
        }

        #endregion

        #region ProcessPaymentAsync — Validation Tests

        [Fact]
        public async Task ProcessPaymentAsync_NullRequest_ReturnsError()
        {
            // Act — pass null request to trigger early validation failure
            var result = await _sut.ProcessPaymentAsync(null!, Guid.NewGuid());

            // Assert — response indicates failure with error containing "null"
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle()
                .Which.Message.Should().Contain("null");

            // Verify — no payment was persisted
            _repositoryMock.Verify(
                r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessPaymentAsync_ZeroAmount_ReturnsError()
        {
            // Arrange — request with Amount = 0m (zero is invalid)
            var invoiceId = Guid.NewGuid();
            var request = CreateValidCreatePaymentRequest(invoiceId, 0m);

            // Act
            var result = await _sut.ProcessPaymentAsync(request, Guid.NewGuid());

            // Assert — validation error for zero amount
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle()
                .Which.Message.Should().Contain("Payment amount must be greater than zero");

            // Verify — no payment was persisted
            _repositoryMock.Verify(
                r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessPaymentAsync_NegativeAmount_ReturnsError()
        {
            // Arrange — request with Amount = -50m (negative is invalid)
            var invoiceId = Guid.NewGuid();
            var request = CreateValidCreatePaymentRequest(invoiceId, -50m);

            // Act
            var result = await _sut.ProcessPaymentAsync(request, Guid.NewGuid());

            // Assert — validation error for negative amount
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle()
                .Which.Message.Should().Contain("Payment amount must be greater than zero");

            // Verify — no payment was persisted
            _repositoryMock.Verify(
                r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessPaymentAsync_NonexistentInvoice_ReturnsError()
        {
            // Arrange — valid request but invoice does not exist in repository
            var invoiceId = Guid.NewGuid();
            var request = CreateValidCreatePaymentRequest(invoiceId, 100m);

            _repositoryMock
                .Setup(r => r.GetInvoiceByIdAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Invoice?)null);

            // Act
            var result = await _sut.ProcessPaymentAsync(request, Guid.NewGuid());

            // Assert — error indicating invoice was not found
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle()
                .Which.Message.Should().Contain("Invoice not found");

            // Verify — no payment was persisted
            _repositoryMock.Verify(
                r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessPaymentAsync_VoidedInvoice_ReturnsError()
        {
            // Arrange — invoice exists but has Status = Voided (cannot accept payments)
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, 1000m, InvoiceStatus.Voided);
            var request = CreateValidCreatePaymentRequest(invoiceId, 100m);

            SetupInvoiceLookup(invoice);

            // Act
            var result = await _sut.ProcessPaymentAsync(request, Guid.NewGuid());

            // Assert — error about voided invoice
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle()
                .Which.Message.Should().Contain("voided invoice");

            // Verify — no payment was persisted
            _repositoryMock.Verify(
                r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessPaymentAsync_DraftInvoice_ReturnsError()
        {
            // Arrange — invoice exists but has Status = Draft (must be issued first)
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, 1000m, InvoiceStatus.Draft);
            var request = CreateValidCreatePaymentRequest(invoiceId, 100m);

            SetupInvoiceLookup(invoice);

            // Act
            var result = await _sut.ProcessPaymentAsync(request, Guid.NewGuid());

            // Assert — error about draft invoice
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle()
                .Which.Message.Should().Contain("draft invoice");

            // Verify — no payment was persisted
            _repositoryMock.Verify(
                r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessPaymentAsync_Overpayment_ReturnsError()
        {
            // Arrange — Invoice TotalAmount=100m, existing payment of 80m, new payment of 30m
            // Total would be 80 + 30 = 110 > 100, exceeds remaining balance of 20m
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, 100m, InvoiceStatus.Issued);
            var request = CreateValidCreatePaymentRequest(invoiceId, 30m);

            SetupInvoiceLookup(invoice);
            SetupExistingPayments(invoiceId, new List<Payment>
            {
                new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = 80m,
                    PaymentDate = DateTime.UtcNow.AddDays(-5),
                    PaymentMethod = PaymentMethod.BankTransfer
                }
            });

            // Act
            var result = await _sut.ProcessPaymentAsync(request, Guid.NewGuid());

            // Assert — error about exceeding remaining balance
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle()
                .Which.Message.Should().Contain("exceeds remaining balance");

            // Verify — no payment was persisted
            _repositoryMock.Verify(
                r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        #endregion

        #region ProcessPaymentAsync — Happy Path Tests

        [Fact]
        public async Task ProcessPaymentAsync_ValidPayment_RecordsSuccessfully()
        {
            // Arrange — Issued invoice TotalAmount=1000m, no existing payments, pay 500m
            var invoiceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, 1000m, InvoiceStatus.Issued);
            var request = CreateValidCreatePaymentRequest(invoiceId, 500m);

            SetupInvoiceLookup(invoice);
            SetupExistingPayments(invoiceId, new List<Payment>());
            SetupCreatePaymentPassthrough();
            SetupEventPublisherDefaults();

            // Act
            var result = await _sut.ProcessPaymentAsync(request, userId);

            // Assert — payment recorded successfully with decimal precision
            result.Success.Should().BeTrue();
            result.Object.Should().NotBeNull();
            result.Object!.Amount.Should().Be(500m);
            result.Object.InvoiceId.Should().Be(invoiceId);
            result.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

            // Verify — payment was persisted to repository
            _repositoryMock.Verify(
                r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Once);

            // Verify — SNS event invoicing.payment.processed was published
            _eventPublisherMock.Verify(
                p => p.PublishPaymentProcessedAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task ProcessPaymentAsync_PartialPayment_DoesNotTransitionToPaid()
        {
            // Arrange — Invoice TotalAmount=1000m, pay only 500m (partial, not fully paid)
            var invoiceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, 1000m, InvoiceStatus.Issued);
            var request = CreateValidCreatePaymentRequest(invoiceId, 500m);

            SetupInvoiceLookup(invoice);
            SetupExistingPayments(invoiceId, new List<Payment>());
            SetupCreatePaymentPassthrough();
            SetupEventPublisherDefaults();

            // Act
            var result = await _sut.ProcessPaymentAsync(request, userId);

            // Assert — payment accepted
            result.Success.Should().BeTrue();

            // Verify — MarkInvoicePaidAsync was NOT called (invoice not fully paid yet)
            _invoiceServiceMock.Verify(
                s => s.MarkInvoicePaidAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);

            // Verify — SNS event invoicing.invoice.paid was NOT published
            _eventPublisherMock.Verify(
                p => p.PublishInvoicePaidAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()),
                Times.Never);

            // Verify — SNS event invoicing.payment.processed WAS still published
            _eventPublisherMock.Verify(
                p => p.PublishPaymentProcessedAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task ProcessPaymentAsync_MultiplePartialPayments_SumsCorrectly()
        {
            // Arrange — Invoice TotalAmount=1000m, existing payment of 300m, new payment of 200m
            // Total after: 300 + 200 = 500 < 1000 → valid partial payment, not fully paid
            var invoiceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, 1000m, InvoiceStatus.Issued);
            var request = CreateValidCreatePaymentRequest(invoiceId, 200m);

            SetupInvoiceLookup(invoice);
            SetupExistingPayments(invoiceId, new List<Payment>
            {
                new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = 300m,
                    PaymentDate = DateTime.UtcNow.AddDays(-5),
                    PaymentMethod = PaymentMethod.BankTransfer
                }
            });
            SetupCreatePaymentPassthrough();
            SetupEventPublisherDefaults();

            // Act
            var result = await _sut.ProcessPaymentAsync(request, userId);

            // Assert — valid partial payment accepted, sum is correct (300 + 200 = 500 < 1000)
            result.Success.Should().BeTrue();
            result.Object.Should().NotBeNull();
            result.Object!.Amount.Should().Be(200m);

            // Verify — not fully paid yet, MarkInvoicePaidAsync NOT called
            _invoiceServiceMock.Verify(
                s => s.MarkInvoicePaidAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessPaymentAsync_FullPayment_AutoTransitionsToPaid()
        {
            // Arrange — Invoice TotalAmount=1000m, no existing payments, pay full 1000m
            var invoiceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, 1000m, InvoiceStatus.Issued);
            var request = CreateValidCreatePaymentRequest(invoiceId, 1000m);

            SetupInvoiceLookup(invoice);
            SetupExistingPayments(invoiceId, new List<Payment>());
            SetupCreatePaymentPassthrough();
            SetupEventPublisherDefaults();
            SetupMarkInvoicePaid();

            // Act
            var result = await _sut.ProcessPaymentAsync(request, userId);

            // Assert — payment recorded, invoice fully paid
            result.Success.Should().BeTrue();
            result.Object.Should().NotBeNull();
            result.Object!.Amount.Should().Be(1000m);

            // Verify — MarkInvoicePaidAsync called with correct invoiceId and userId
            _invoiceServiceMock.Verify(
                s => s.MarkInvoicePaidAsync(invoiceId, userId, It.IsAny<CancellationToken>()),
                Times.Once);

            // Verify — SNS event invoicing.invoice.paid was published
            _eventPublisherMock.Verify(
                p => p.PublishInvoicePaidAsync(
                    It.Is<Invoice>(i => i.Id == invoiceId),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            // Verify — SNS event invoicing.payment.processed was also published
            _eventPublisherMock.Verify(
                p => p.PublishPaymentProcessedAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task ProcessPaymentAsync_FinalPartialPayment_TransitionsToPaid()
        {
            // Arrange — Invoice TotalAmount=1000m, existing payments sum to 700m (400+300), pay remaining 300m
            // After: 400 + 300 + 300 = 1000 >= 1000 → fully paid, auto-transition to Paid
            var invoiceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, 1000m, InvoiceStatus.Issued);
            var request = CreateValidCreatePaymentRequest(invoiceId, 300m);

            SetupInvoiceLookup(invoice);
            SetupExistingPayments(invoiceId, new List<Payment>
            {
                new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = 400m,
                    PaymentDate = DateTime.UtcNow.AddDays(-10),
                    PaymentMethod = PaymentMethod.BankTransfer
                },
                new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = 300m,
                    PaymentDate = DateTime.UtcNow.AddDays(-5),
                    PaymentMethod = PaymentMethod.CreditCard
                }
            });
            SetupCreatePaymentPassthrough();
            SetupEventPublisherDefaults();
            SetupMarkInvoicePaid();

            // Act
            var result = await _sut.ProcessPaymentAsync(request, userId);

            // Assert — final partial accepted, fully paid (400 + 300 + 300 = 1000)
            result.Success.Should().BeTrue();
            result.Object.Should().NotBeNull();
            result.Object!.Amount.Should().Be(300m);

            // Verify — MarkInvoicePaidAsync called (invoice is now fully paid)
            _invoiceServiceMock.Verify(
                s => s.MarkInvoicePaidAsync(invoiceId, userId, It.IsAny<CancellationToken>()),
                Times.Once);

            // Verify — SNS event invoicing.invoice.paid was published
            _eventPublisherMock.Verify(
                p => p.PublishInvoicePaidAsync(
                    It.Is<Invoice>(i => i.Id == invoiceId),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            // Verify — SNS event invoicing.payment.processed was also published
            _eventPublisherMock.Verify(
                p => p.PublishPaymentProcessedAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        #endregion

        #region GetPaymentAsync Tests

        [Fact]
        public async Task GetPaymentAsync_ExistingPayment_ReturnsPayment()
        {
            // Arrange — payment exists in repository
            var paymentId = Guid.NewGuid();
            var testPayment = new Payment
            {
                Id = paymentId,
                InvoiceId = Guid.NewGuid(),
                Amount = 500m,
                PaymentDate = DateTime.UtcNow,
                PaymentMethod = PaymentMethod.BankTransfer,
                ReferenceNumber = "REF-FOUND",
                Notes = "Found payment"
            };

            _repositoryMock
                .Setup(r => r.GetPaymentAsync(paymentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(testPayment);

            // Act
            var result = await _sut.GetPaymentAsync(paymentId);

            // Assert — payment returned successfully with correct data
            result.Success.Should().BeTrue();
            result.Object.Should().NotBeNull();
            result.Object!.Id.Should().Be(paymentId);
            result.Object.Amount.Should().Be(500m);
            result.Object.ReferenceNumber.Should().Be("REF-FOUND");
        }

        [Fact]
        public async Task GetPaymentAsync_NotFound_ReturnsError()
        {
            // Arrange — payment does not exist in repository
            var paymentId = Guid.NewGuid();

            _repositoryMock
                .Setup(r => r.GetPaymentAsync(paymentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Payment?)null);

            // Act
            var result = await _sut.GetPaymentAsync(paymentId);

            // Assert — error response for non-existent payment
            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle()
                .Which.Message.Should().Contain("Payment not found");
        }

        #endregion

        #region ListPaymentsForInvoiceAsync Tests

        [Fact]
        public async Task ListPaymentsForInvoiceAsync_ReturnsAllPayments()
        {
            // Arrange — three payments exist for the invoice
            var invoiceId = Guid.NewGuid();
            var payments = new List<Payment>
            {
                new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = 300m,
                    PaymentDate = DateTime.UtcNow.AddDays(-10),
                    PaymentMethod = PaymentMethod.BankTransfer
                },
                new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = 200m,
                    PaymentDate = DateTime.UtcNow.AddDays(-5),
                    PaymentMethod = PaymentMethod.CreditCard
                },
                new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = 500m,
                    PaymentDate = DateTime.UtcNow,
                    PaymentMethod = PaymentMethod.Cash
                }
            };

            _repositoryMock
                .Setup(r => r.ListPaymentsForInvoiceAsync(invoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(payments);

            // Act
            var result = await _sut.ListPaymentsForInvoiceAsync(invoiceId);

            // Assert — all payments returned with correct count and amounts
            result.Success.Should().BeTrue();
            result.Object.Should().NotBeNull();
            result.Object.Should().HaveCount(3);
            result.TotalCount.Should().Be(3);
            result.Object.Sum(p => p.Amount).Should().Be(1000m);
        }

        #endregion

        #region GetRemainingBalanceAsync Tests

        [Fact]
        public async Task GetRemainingBalanceAsync_NoPayments_ReturnsTotalAmount()
        {
            // Arrange — Invoice TotalAmount=1000m, no payments made yet
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, 1000m, InvoiceStatus.Issued);

            SetupInvoiceLookup(invoice);
            SetupExistingPayments(invoiceId, new List<Payment>());

            // Act
            var result = await _sut.GetRemainingBalanceAsync(invoiceId);

            // Assert — full balance remaining (1000m - 0m = 1000m)
            result.Should().Be(1000m);
        }

        [Fact]
        public async Task GetRemainingBalanceAsync_PartialPayment_ReturnsCorrectBalance()
        {
            // Arrange — Invoice TotalAmount=1000m, payments summing to 400m (250 + 150)
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, 1000m, InvoiceStatus.Issued);

            SetupInvoiceLookup(invoice);
            SetupExistingPayments(invoiceId, new List<Payment>
            {
                new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = 250m,
                    PaymentDate = DateTime.UtcNow.AddDays(-10),
                    PaymentMethod = PaymentMethod.BankTransfer
                },
                new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = 150m,
                    PaymentDate = DateTime.UtcNow.AddDays(-5),
                    PaymentMethod = PaymentMethod.CreditCard
                }
            });

            // Act
            var result = await _sut.GetRemainingBalanceAsync(invoiceId);

            // Assert — 1000m - 400m = 600m remaining
            result.Should().Be(600m);
        }

        [Fact]
        public async Task GetRemainingBalanceAsync_FullyPaid_ReturnsZero()
        {
            // Arrange — Invoice TotalAmount=1000m, payments summing to 1000m (600 + 400)
            var invoiceId = Guid.NewGuid();
            var invoice = CreateTestInvoice(invoiceId, 1000m, InvoiceStatus.Issued);

            SetupInvoiceLookup(invoice);
            SetupExistingPayments(invoiceId, new List<Payment>
            {
                new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = 600m,
                    PaymentDate = DateTime.UtcNow.AddDays(-10),
                    PaymentMethod = PaymentMethod.BankTransfer
                },
                new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = 400m,
                    PaymentDate = DateTime.UtcNow.AddDays(-5),
                    PaymentMethod = PaymentMethod.CreditCard
                }
            });

            // Act
            var result = await _sut.GetRemainingBalanceAsync(invoiceId);

            // Assert — fully paid, zero remaining balance
            result.Should().Be(0m);
        }

        #endregion
    }
}
