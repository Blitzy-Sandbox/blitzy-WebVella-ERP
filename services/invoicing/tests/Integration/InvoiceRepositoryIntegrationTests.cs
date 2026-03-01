using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using WebVellaErp.Invoicing.DataAccess;
using WebVellaErp.Invoicing.Models;

namespace WebVellaErp.Invoicing.Tests.Integration
{
    /// <summary>
    /// Integration tests for <see cref="InvoiceRepository"/> exercising all CRUD operations,
    /// ACID transaction guarantees, pagination, filtering, and financial decimal precision
    /// against a real LocalStack-hosted RDS PostgreSQL instance.
    ///
    /// Every test connects to LocalStack PostgreSQL via <see cref="LocalStackFixture"/>
    /// — zero mocked database connections per AAP §0.8.4.
    ///
    /// Source mapping:
    ///   - WebVella.Erp/Database/DbRecordRepository.cs  → Create/Update/Delete/Find patterns
    ///   - WebVella.Erp/Database/DbRepository.cs         → InsertRecord/UpdateRecord/DeleteRecord DDL helpers
    ///   - WebVella.Erp/Database/DbConnection.cs         → BeginTransaction/RollbackTransaction ACID pattern
    ///   - WebVella.Erp/Api/RecordManager.cs             → Record CRUD orchestration with hooks
    ///   - WebVella.Erp/Api/Definitions.cs               → SystemIds, EntityPermission enums
    /// </summary>
    public class InvoiceRepositoryIntegrationTests : IClassFixture<LocalStackFixture>, IAsyncLifetime
    {
        private readonly LocalStackFixture _fixture;
        private readonly IInvoiceRepository _repository;

        /// <summary>
        /// Constructs the test class with a shared <see cref="LocalStackFixture"/> providing
        /// a PostgreSQL connection string and utility methods for the LocalStack environment.
        /// The <see cref="InvoiceRepository"/> is constructed using the fixture's connection
        /// string and a NullLogger instance per test-isolation requirements.
        /// </summary>
        public InvoiceRepositoryIntegrationTests(LocalStackFixture fixture)
        {
            _fixture = fixture;
            _repository = new InvoiceRepository(
                fixture.ConnectionString,
                fixture.CreateTestLogger<InvoiceRepository>());
        }

        /// <summary>
        /// Resets the database state before each test by truncating all invoicing tables
        /// (invoicing.payments, invoicing.invoice_line_items, invoicing.invoices CASCADE).
        /// Ensures complete test isolation — no residual data from prior test runs.
        /// </summary>
        public async Task InitializeAsync()
        {
            await _fixture.ResetDatabaseAsync();
        }

        /// <summary>
        /// Optional cleanup after each test. No additional teardown required
        /// beyond the per-test truncation in <see cref="InitializeAsync"/>.
        /// </summary>
        public Task DisposeAsync()
        {
            return Task.CompletedTask;
        }

        // ══════════════════════════════════════════════════════════════════════════════
        //  CreateInvoiceAsync Tests
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceRepository.CreateInvoiceAsync"/> persists both the
        /// invoice header and its line items within an ACID transaction. Confirms that monetary
        /// values are stored with exact <c>decimal</c> precision (NEVER float approximation)
        /// and that audit columns (created_by, created_on) are correctly set.
        ///
        /// Pattern derived from source DbRepository.InsertRecord lines 517-553:
        /// parameterized NpgsqlCommand execution with transaction scope.
        /// </summary>
        [RdsFact]
        public async Task CreateInvoiceAsync_PersistsInvoiceAndLineItems_InACIDTransaction()
        {
            // Arrange
            var invoiceId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var createdBy = Guid.NewGuid();
            var now = DateTime.UtcNow;

            var invoice = new Invoice
            {
                Id = invoiceId,
                InvoiceNumber = "INV-TEST-001",
                CustomerId = customerId,
                Status = InvoiceStatus.Draft,
                IssueDate = now,
                DueDate = now.AddDays(30),
                SubTotal = 200.00m,
                TaxAmount = 20.00m,
                TotalAmount = 220.00m,
                Notes = "Integration test invoice — ACID transaction verification",
                CreatedBy = createdBy,
                CreatedOn = now,
                LastModifiedBy = createdBy,
                LastModifiedOn = now,
                LineItems = new List<LineItem>
                {
                    new LineItem
                    {
                        Id = Guid.NewGuid(),
                        InvoiceId = invoiceId,
                        Description = "Widget A — standard unit",
                        Quantity = 2m,
                        UnitPrice = 50.00m,
                        TaxRate = 0.10m,
                        LineTotal = 110.00m,
                        SortOrder = 1
                    },
                    new LineItem
                    {
                        Id = Guid.NewGuid(),
                        InvoiceId = invoiceId,
                        Description = "Widget B — premium unit",
                        Quantity = 1m,
                        UnitPrice = 100.00m,
                        TaxRate = 0.10m,
                        LineTotal = 110.00m,
                        SortOrder = 2
                    }
                }
            };

            // Act
            var result = await _repository.CreateInvoiceAsync(invoice);

            // Assert — verify invoice exists via direct SQL (bypassing repository layer)
            var invoiceCount = await ExecuteScalar<long>(
                "SELECT COUNT(*) FROM invoicing.invoices WHERE id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            invoiceCount.Should().Be(1);

            // Verify line items via direct SQL
            var lineItemCount = await ExecuteScalar<long>(
                "SELECT COUNT(*) FROM invoicing.line_items WHERE invoice_id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            lineItemCount.Should().Be(2);

            // Verify monetary values with exact decimal precision — NEVER BeApproximately()
            var totalAmount = await ExecuteScalar<decimal>(
                "SELECT total_amount FROM invoicing.invoices WHERE id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            totalAmount.Should().Be(220.00m);

            var subTotal = await ExecuteScalar<decimal>(
                "SELECT sub_total FROM invoicing.invoices WHERE id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            subTotal.Should().Be(200.00m);

            var taxAmount = await ExecuteScalar<decimal>(
                "SELECT tax_amount FROM invoicing.invoices WHERE id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            taxAmount.Should().Be(20.00m);

            // Verify audit columns are set correctly
            var createdById = await ExecuteScalar<Guid>(
                "SELECT created_by FROM invoicing.invoices WHERE id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            createdById.Should().Be(createdBy);
        }

        /// <summary>
        /// Verifies that creating an invoice with an empty (but not null) <see cref="List{LineItem}"/>
        /// persists only the invoice header with zero line item rows. Confirms the repository
        /// handles the empty collection gracefully without errors.
        /// </summary>
        [RdsFact]
        public async Task CreateInvoiceAsync_WithEmptyLineItems_PersistsInvoiceOnly()
        {
            // Arrange
            var invoiceId = Guid.NewGuid();
            var invoice = BuildTestInvoice(invoiceId, "INV-EMPTY-001");
            invoice.LineItems = new List<LineItem>();

            // Act
            await _repository.CreateInvoiceAsync(invoice);

            // Assert — invoice persisted
            var invoiceCount = await ExecuteScalar<long>(
                "SELECT COUNT(*) FROM invoicing.invoices WHERE id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            invoiceCount.Should().Be(1);

            // Assert — zero line items via direct SQL
            var lineItemCount = await ExecuteScalar<long>(
                "SELECT COUNT(*) FROM invoicing.line_items WHERE invoice_id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            lineItemCount.Should().Be(0);

            // Assert — retrieve via repository and verify LineItems collection is empty
            var retrieved = await _repository.GetInvoiceByIdAsync(invoiceId);
            retrieved.Should().NotBeNull();
            retrieved!.LineItems.Should().BeEmpty();
        }

        // ══════════════════════════════════════════════════════════════════════════════
        //  GetInvoiceByIdAsync Tests
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceRepository.GetInvoiceByIdAsync"/> returns the full
        /// invoice with all associated line items hydrated. Asserts all field values match the
        /// originally persisted values with exact decimal comparison.
        ///
        /// Pattern derived from source DbRecordRepository.Find lines 206-245:
        /// SELECT * FROM {table} WHERE id=@id
        /// </summary>
        [RdsFact]
        public async Task GetInvoiceByIdAsync_ReturnsInvoiceWithAllLineItems()
        {
            // Arrange — persist an invoice with 3 line items
            var invoiceId = Guid.NewGuid();
            var invoice = BuildTestInvoice(invoiceId, "INV-GET-001");
            invoice.LineItems = BuildTestLineItems(invoiceId, 3);
            await _repository.CreateInvoiceAsync(invoice);

            // Act
            var retrieved = await _repository.GetInvoiceByIdAsync(invoiceId);

            // Assert — invoice returned with all fields
            retrieved.Should().NotBeNull();
            retrieved!.Id.Should().Be(invoiceId);
            retrieved.InvoiceNumber.Should().Be("INV-GET-001");
            retrieved.CustomerId.Should().Be(invoice.CustomerId);
            retrieved.Status.Should().Be(InvoiceStatus.Draft);
            retrieved.SubTotal.Should().Be(200.00m);
            retrieved.TaxAmount.Should().Be(20.00m);
            retrieved.TotalAmount.Should().Be(220.00m);
            retrieved.CreatedBy.Should().Be(invoice.CreatedBy);

            // Assert — all 3 line items hydrated
            retrieved.LineItems.Should().HaveCount(3);
        }

        /// <summary>
        /// Verifies that <see cref="InvoiceRepository.GetInvoiceByIdAsync"/> returns <c>null</c>
        /// when the requested invoice ID does not exist in the database.
        ///
        /// Pattern derived from source DbRecordRepository line 236: return null when no rows.
        /// </summary>
        [RdsFact]
        public async Task GetInvoiceByIdAsync_NonExistentId_ReturnsNull()
        {
            // Act — query with a random non-existent ID
            var result = await _repository.GetInvoiceByIdAsync(Guid.NewGuid());

            // Assert
            result.Should().BeNull();
        }

        // ══════════════════════════════════════════════════════════════════════════════
        //  ListInvoicesAsync Tests
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceRepository.ListInvoicesAsync"/> correctly paginates
        /// results. Creates 15 invoices and verifies that 3 pages of 5 items each are returned
        /// with the correct total count.
        /// </summary>
        [RdsFact]
        public async Task ListInvoicesAsync_WithPagination_ReturnsCorrectPage()
        {
            // Arrange — create 15 test invoices with sequential invoice numbers
            for (int i = 1; i <= 15; i++)
            {
                var invoice = BuildTestInvoice(Guid.NewGuid(), $"INV-PAGE-{i:D3}");
                await _repository.CreateInvoiceAsync(invoice);
            }

            // Act & Assert — page 1
            var page1 = await _repository.ListInvoicesAsync(page: 1, pageSize: 5);
            page1.Items.Count.Should().Be(5);
            page1.TotalCount.Should().Be(15);

            // Act & Assert — page 2
            var page2 = await _repository.ListInvoicesAsync(page: 2, pageSize: 5);
            page2.Items.Count.Should().Be(5);
            page2.TotalCount.Should().Be(15);

            // Act & Assert — page 3
            var page3 = await _repository.ListInvoicesAsync(page: 3, pageSize: 5);
            page3.Items.Count.Should().Be(5);
            page3.TotalCount.Should().Be(15);
        }

        /// <summary>
        /// Verifies that <see cref="InvoiceRepository.ListInvoicesAsync"/> correctly filters
        /// invoices by <see cref="InvoiceStatus"/>. Creates 3 Draft and 2 Issued invoices,
        /// then verifies each status filter returns the correct count and all items match.
        /// </summary>
        [RdsFact]
        public async Task ListInvoicesAsync_WithStatusFilter_ReturnsFilteredResults()
        {
            // Arrange — 3 Draft invoices
            for (int i = 0; i < 3; i++)
            {
                var draft = BuildTestInvoice(Guid.NewGuid(), $"INV-DRAFT-{i:D3}");
                draft.Status = InvoiceStatus.Draft;
                await _repository.CreateInvoiceAsync(draft);
            }

            // Arrange — 2 Issued invoices
            for (int i = 0; i < 2; i++)
            {
                var issued = BuildTestInvoice(Guid.NewGuid(), $"INV-ISSUED-{i:D3}");
                issued.Status = InvoiceStatus.Issued;
                await _repository.CreateInvoiceAsync(issued);
            }

            // Act & Assert — Draft filter
            var draftResult = await _repository.ListInvoicesAsync(
                page: 1, pageSize: 10, statusFilter: InvoiceStatus.Draft);
            draftResult.Items.Count.Should().Be(3);
            draftResult.Items.All(inv => inv.Status == InvoiceStatus.Draft).Should().BeTrue();

            // Act & Assert — Issued filter
            var issuedResult = await _repository.ListInvoicesAsync(
                page: 1, pageSize: 10, statusFilter: InvoiceStatus.Issued);
            issuedResult.Items.Count.Should().Be(2);
            issuedResult.Items.All(inv => inv.Status == InvoiceStatus.Issued).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that <see cref="InvoiceRepository.ListInvoicesAsync"/> correctly filters
        /// invoices by customer ID (Guid). Creates invoices for 2 different customers and
        /// verifies the filter returns only the targeted customer's invoices.
        /// </summary>
        [RdsFact]
        public async Task ListInvoicesAsync_WithCustomerFilter_ReturnsFilteredResults()
        {
            // Arrange — invoices for customer 1
            var customerId1 = Guid.NewGuid();
            for (int i = 0; i < 3; i++)
            {
                var inv = BuildTestInvoice(Guid.NewGuid(), $"INV-CUST1-{i:D3}");
                inv.CustomerId = customerId1;
                await _repository.CreateInvoiceAsync(inv);
            }

            // Arrange — invoices for customer 2
            var customerId2 = Guid.NewGuid();
            for (int i = 0; i < 2; i++)
            {
                var inv = BuildTestInvoice(Guid.NewGuid(), $"INV-CUST2-{i:D3}");
                inv.CustomerId = customerId2;
                await _repository.CreateInvoiceAsync(inv);
            }

            // Act — filter by customer 1
            var result = await _repository.ListInvoicesAsync(
                page: 1, pageSize: 10, customerFilter: customerId1);

            // Assert
            result.Items.Count.Should().Be(3);
            result.Items.All(inv => inv.CustomerId == customerId1).Should().BeTrue();
        }

        // ══════════════════════════════════════════════════════════════════════════════
        //  UpdateInvoiceAsync Tests
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceRepository.UpdateInvoiceAsync"/> replaces existing
        /// line items with the new set within an ACID transaction. Confirms old line items are
        /// deleted and new ones inserted atomically.
        ///
        /// Pattern derived from source DbRepository.UpdateRecord lines 555-587:
        /// UPDATE {table} SET ... WHERE id=@id within a transaction scope.
        /// </summary>
        [RdsFact]
        public async Task UpdateInvoiceAsync_ReplacesLineItems_InTransaction()
        {
            // Arrange — create invoice with 2 line items
            var invoiceId = Guid.NewGuid();
            var invoice = BuildTestInvoice(invoiceId, "INV-UPD-001");
            invoice.LineItems = BuildTestLineItems(invoiceId, 2);
            await _repository.CreateInvoiceAsync(invoice);

            // Modify: change notes, replace with 3 new line items, update totals
            invoice.Notes = "Updated notes — line items replaced";
            invoice.SubTotal = 300.00m;
            invoice.TaxAmount = 30.00m;
            invoice.TotalAmount = 330.00m;
            invoice.LastModifiedBy = Guid.NewGuid();
            invoice.LastModifiedOn = DateTime.UtcNow;
            invoice.LineItems = BuildTestLineItems(invoiceId, 3);

            // Act
            await _repository.UpdateInvoiceAsync(invoice);

            // Assert — verify new line item count via direct SQL
            var lineItemCount = await ExecuteScalar<long>(
                "SELECT COUNT(*) FROM invoicing.line_items WHERE invoice_id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            lineItemCount.Should().Be(3);

            // Verify updated monetary totals via direct SQL
            var totalAmount = await ExecuteScalar<decimal>(
                "SELECT total_amount FROM invoicing.invoices WHERE id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            totalAmount.Should().Be(330.00m);

            var subTotal = await ExecuteScalar<decimal>(
                "SELECT sub_total FROM invoicing.invoices WHERE id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            subTotal.Should().Be(300.00m);
        }

        /// <summary>
        /// Verifies that updating monetary values on an invoice preserves exact
        /// <c>decimal</c> precision through the round-trip to PostgreSQL and back.
        /// All monetary fields are verified with exact <c>Should().Be()</c> — NEVER
        /// <c>BeApproximately()</c>.
        /// </summary>
        [RdsFact]
        public async Task UpdateInvoiceAsync_UpdateMonetaryValues_PreservesDecimalPrecision()
        {
            // Arrange — create invoice with initial monetary values
            var invoiceId = Guid.NewGuid();
            var invoice = BuildTestInvoice(invoiceId, "INV-DEC-001");
            invoice.TotalAmount = 1234.56m;
            invoice.SubTotal = 1122.33m;
            invoice.TaxAmount = 112.23m;
            await _repository.CreateInvoiceAsync(invoice);

            // Act — update with new precise decimal values
            invoice.TotalAmount = 9999.99m;
            invoice.SubTotal = 9090.90m;
            invoice.TaxAmount = 909.09m;
            invoice.LastModifiedBy = Guid.NewGuid();
            invoice.LastModifiedOn = DateTime.UtcNow;
            await _repository.UpdateInvoiceAsync(invoice);

            // Assert — fetch via repository and verify exact decimal precision
            var retrieved = await _repository.GetInvoiceByIdAsync(invoiceId);
            retrieved.Should().NotBeNull();
            retrieved!.TotalAmount.Should().Be(9999.99m);
            retrieved.SubTotal.Should().Be(9090.90m);
            retrieved.TaxAmount.Should().Be(909.09m);
        }

        // ══════════════════════════════════════════════════════════════════════════════
        //  VoidInvoiceAsync Tests
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceRepository.VoidInvoiceAsync"/> sets the invoice
        /// status to <see cref="InvoiceStatus.Voided"/> and returns <c>true</c> indicating
        /// the void operation was applied.
        /// </summary>
        [RdsFact]
        public async Task VoidInvoiceAsync_SetsVoidedStatus_ReturnsTrue()
        {
            // Arrange — create a Draft invoice
            var invoiceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var invoice = BuildTestInvoice(invoiceId, "INV-VOID-001");
            invoice.Status = InvoiceStatus.Draft;
            await _repository.CreateInvoiceAsync(invoice);

            // Act
            var result = await _repository.VoidInvoiceAsync(invoiceId, userId);

            // Assert
            result.Should().BeTrue();

            var retrieved = await _repository.GetInvoiceByIdAsync(invoiceId);
            retrieved.Should().NotBeNull();
            retrieved!.Status.Should().Be(InvoiceStatus.Voided);
        }

        /// <summary>
        /// Verifies that calling <see cref="InvoiceRepository.VoidInvoiceAsync"/> on an
        /// already-voided invoice returns <c>false</c> without corrupting the record.
        /// The idempotent WHERE clause (<c>WHERE id=@id AND status != 'Voided'</c>) ensures
        /// a second void attempt has zero effect.
        ///
        /// Per AAP §0.8.5: Idempotency keys on all write endpoints — VoidInvoiceAsync
        /// uses WHERE clause for idempotent void.
        /// </summary>
        [RdsFact]
        public async Task VoidInvoiceAsync_AlreadyVoided_ReturnsFalse_IdempotentWhereClause()
        {
            // Arrange — create and void an invoice
            var invoiceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var invoice = BuildTestInvoice(invoiceId, "INV-VOID-002");
            await _repository.CreateInvoiceAsync(invoice);

            var firstVoidResult = await _repository.VoidInvoiceAsync(invoiceId, userId);
            firstVoidResult.Should().BeTrue();

            // Act — attempt to void again (should be idempotent)
            var secondVoidResult = await _repository.VoidInvoiceAsync(invoiceId, userId);

            // Assert — second void returns false, status remains Voided
            secondVoidResult.Should().BeFalse();

            var retrieved = await _repository.GetInvoiceByIdAsync(invoiceId);
            retrieved.Should().NotBeNull();
            retrieved!.Status.Should().Be(InvoiceStatus.Voided);
        }

        // ══════════════════════════════════════════════════════════════════════════════
        //  Payment Operation Tests
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceRepository.CreatePaymentAsync"/> persists a payment
        /// record with exact <c>decimal</c> amount precision. Direct SQL verification confirms
        /// the row exists in <c>invoicing.payments</c> and the amount matches exactly.
        ///
        /// Pattern derived from source DbRepository.InsertRecord: parameterized INSERT.
        /// </summary>
        [RdsFact]
        public async Task CreatePaymentAsync_PersistsPaymentRecord()
        {
            // Arrange — create parent invoice first (FK reference)
            var invoiceId = Guid.NewGuid();
            var invoice = BuildTestInvoice(invoiceId, "INV-PAY-001");
            await _repository.CreateInvoiceAsync(invoice);

            var paymentId = Guid.NewGuid();
            var createdBy = Guid.NewGuid();
            var paymentDate = DateTime.UtcNow;

            var payment = new Payment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                Amount = 150.00m,
                PaymentDate = paymentDate,
                PaymentMethod = PaymentMethod.CreditCard,
                ReferenceNumber = "REF-CC-001",
                Notes = "Credit card payment integration test",
                CreatedBy = createdBy,
                CreatedOn = DateTime.UtcNow
            };

            // Act
            await _repository.CreatePaymentAsync(payment);

            // Assert — verify via direct SQL that payment exists
            var paymentCount = await ExecuteScalar<long>(
                "SELECT COUNT(*) FROM invoicing.payments WHERE id = @id",
                new Dictionary<string, object> { { "id", paymentId } });
            paymentCount.Should().Be(1);

            // Verify amount with exact decimal precision
            var amount = await ExecuteScalar<decimal>(
                "SELECT amount FROM invoicing.payments WHERE id = @id",
                new Dictionary<string, object> { { "id", paymentId } });
            amount.Should().Be(150.00m);
        }

        /// <summary>
        /// Verifies that <see cref="InvoiceRepository.ListPaymentsForInvoiceAsync"/> returns
        /// all payments associated with a given invoice, ordered by <c>payment_date</c>,
        /// with exact <c>decimal</c> amount precision on each payment record.
        /// </summary>
        [RdsFact]
        public async Task ListPaymentsForInvoiceAsync_ReturnsAllPaymentsForInvoice()
        {
            // Arrange — create parent invoice
            var invoiceId = Guid.NewGuid();
            var invoice = BuildTestInvoice(invoiceId, "INV-PAYLIST-001");
            await _repository.CreateInvoiceAsync(invoice);

            // Create 3 payments with ascending dates and varying amounts
            var baseDate = DateTime.UtcNow;
            decimal[] amounts = { 100.00m, 150.00m, 200.00m };
            PaymentMethod[] methods =
            {
                PaymentMethod.CreditCard,
                PaymentMethod.BankTransfer,
                PaymentMethod.CreditCard
            };

            for (int i = 0; i < 3; i++)
            {
                var payment = new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Amount = amounts[i],
                    PaymentDate = baseDate.AddDays(i),
                    PaymentMethod = methods[i],
                    ReferenceNumber = $"REF-{i + 1:D3}",
                    CreatedBy = Guid.NewGuid(),
                    CreatedOn = DateTime.UtcNow
                };
                await _repository.CreatePaymentAsync(payment);
            }

            // Act
            var payments = await _repository.ListPaymentsForInvoiceAsync(invoiceId);

            // Assert — correct count
            payments.Should().HaveCount(3);

            // Assert — ordered by payment_date ascending
            for (int i = 1; i < payments.Count; i++)
            {
                payments[i].PaymentDate.Should().BeOnOrAfter(payments[i - 1].PaymentDate);
            }

            // Assert — monetary amounts match with exact decimal precision
            payments.Sum(p => p.Amount).Should().Be(450.00m);

            // Assert — verify via direct SQL with ExecuteReaderAsync for independent validation
            var directAmounts = await ExecuteReaderValues<decimal>(
                "SELECT amount FROM invoicing.payments WHERE invoice_id = @id ORDER BY payment_date",
                new Dictionary<string, object> { { "id", invoiceId } },
                "amount");
            directAmounts.Should().HaveCount(3);
            directAmounts.Sum().Should().Be(450.00m);
        }

        // ══════════════════════════════════════════════════════════════════════════════
        //  Transaction Rollback on Failure
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="InvoiceRepository.CreateInvoiceAsync"/> rolls back the
        /// entire transaction (invoice header + line items) when a line item insertion fails
        /// due to a database constraint violation.
        ///
        /// Strategy: Creates an invoice with a line item whose <c>InvoiceId</c> references a
        /// non-existent invoice (foreign key violation). The repository inserts the invoice header
        /// first (succeeds within the transaction), then attempts the line item (fails on FK).
        /// The catch block rolls back the entire transaction — verifying that neither the invoice
        /// nor any orphaned line items persist.
        ///
        /// Pattern derived from source DbConnection.cs lines 161-179:
        /// BeginTransaction / RollbackTransaction ACID envelope.
        /// </summary>
        [RdsFact]
        public async Task CreateInvoiceAsync_OnLineItemFailure_RollsBackEntireTransaction()
        {
            // Arrange — build an invoice with a line item whose InvoiceId
            // does NOT match the parent invoice, triggering a FK constraint violation
            var invoiceId = Guid.NewGuid();
            var mismatchedInvoiceId = Guid.NewGuid();
            var invoice = BuildTestInvoice(invoiceId, "INV-ROLLBACK-001");
            invoice.LineItems = new List<LineItem>
            {
                new LineItem
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = mismatchedInvoiceId, // FK violation — references non-existent invoice
                    Description = "Should trigger rollback",
                    Quantity = 1m,
                    UnitPrice = 50.00m,
                    TaxRate = 0.10m,
                    LineTotal = 55.00m,
                    SortOrder = 1
                }
            };

            // Act — expect exception from FK constraint violation
            Func<Task> act = () => _repository.CreateInvoiceAsync(invoice);
            await act.Should().ThrowAsync<Exception>();

            // Assert — verify the invoice was NOT persisted (rollback succeeded)
            var invoiceCount = await ExecuteScalar<long>(
                "SELECT COUNT(*) FROM invoicing.invoices WHERE id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            invoiceCount.Should().Be(0);

            // Assert — verify NO orphaned line items
            var lineItemCount = await ExecuteScalar<long>(
                "SELECT COUNT(*) FROM invoicing.line_items WHERE invoice_id = @id",
                new Dictionary<string, object> { { "id", invoiceId } });
            lineItemCount.Should().Be(0);
        }

        // ══════════════════════════════════════════════════════════════════════════════
        //  Private Helper Methods
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Executes a parameterized SQL query against the LocalStack PostgreSQL instance
        /// and returns the scalar result cast to <typeparamref name="T"/>. Opens a fresh
        /// <see cref="NpgsqlConnection"/> from the fixture's connection string, bypassing
        /// the repository layer entirely for independent verification of persisted state.
        ///
        /// All parameters are bound via <see cref="NpgsqlParameter"/> — NEVER string
        /// concatenation — to prevent SQL injection in test queries.
        /// </summary>
        /// <typeparam name="T">The expected scalar return type (long, decimal, Guid, etc.).</typeparam>
        /// <param name="sql">Parameterized SQL query string (e.g., SELECT COUNT(*) FROM ... WHERE id = @id).</param>
        /// <param name="parameters">Dictionary of parameter name → value pairs. Names are
        /// automatically prefixed with '@' for NpgsqlParameter binding.</param>
        /// <returns>The scalar query result cast to <typeparamref name="T"/>.</returns>
        private async Task<T> ExecuteScalar<T>(string sql, Dictionary<string, object> parameters)
        {
            using var connection = _fixture.CreateNpgsqlConnection();
            await using var cmd = new NpgsqlCommand(sql, connection);

            foreach (var kvp in parameters)
            {
                var paramName = kvp.Key.StartsWith("@") ? kvp.Key : $"@{kvp.Key}";

                // Use explicit NpgsqlDbType.Uuid for Guid parameters to ensure correct
                // PostgreSQL type mapping and prevent implicit type conversion issues.
                if (kvp.Value is Guid guidValue)
                {
                    var param = new NpgsqlParameter(paramName, NpgsqlDbType.Uuid)
                    {
                        Value = guidValue
                    };
                    cmd.Parameters.Add(param);
                }
                else
                {
                    cmd.Parameters.Add(new NpgsqlParameter(paramName, kvp.Value));
                }
            }

            var result = await cmd.ExecuteScalarAsync();

            if (result is null || result is DBNull)
            {
                return default!;
            }

            return (T)Convert.ChangeType(result, typeof(T));
        }

        /// <summary>
        /// Executes a parameterized SQL query using <see cref="NpgsqlCommand.ExecuteReaderAsync"/>
        /// and returns a list of values for the specified column. Used for multi-row direct SQL
        /// verification that bypasses the repository layer entirely.
        ///
        /// All parameters are bound via <see cref="NpgsqlParameter"/> — NEVER string concatenation.
        /// </summary>
        /// <typeparam name="T">The column value type (decimal, Guid, string, etc.).</typeparam>
        /// <param name="sql">Parameterized SQL query string.</param>
        /// <param name="parameters">Dictionary of parameter name → value pairs.</param>
        /// <param name="columnName">The column name to extract from each row.</param>
        /// <returns>A list of column values, one per result row.</returns>
        private async Task<List<T>> ExecuteReaderValues<T>(
            string sql,
            Dictionary<string, object> parameters,
            string columnName)
        {
            var results = new List<T>();

            using var connection = _fixture.CreateNpgsqlConnection();
            await using var cmd = new NpgsqlCommand(sql, connection);

            foreach (var kvp in parameters)
            {
                var paramName = kvp.Key.StartsWith("@") ? kvp.Key : $"@{kvp.Key}";

                if (kvp.Value is Guid guidValue)
                {
                    var param = new NpgsqlParameter(paramName, NpgsqlDbType.Uuid)
                    {
                        Value = guidValue
                    };
                    cmd.Parameters.Add(param);
                }
                else
                {
                    cmd.Parameters.Add(new NpgsqlParameter(paramName, kvp.Value));
                }
            }

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var ordinal = reader.GetOrdinal(columnName);
                var value = reader.GetFieldValue<T>(ordinal);
                results.Add(value);
            }

            return results;
        }

        /// <summary>
        /// Builds a standard test <see cref="Invoice"/> with sensible defaults for all required
        /// fields. Monetary values default to SubTotal=200, Tax=20, Total=220. Status defaults
        /// to <see cref="InvoiceStatus.Draft"/>. Line items default to an empty list.
        /// </summary>
        /// <param name="invoiceId">The invoice's primary key.</param>
        /// <param name="invoiceNumber">A unique human-readable invoice number.</param>
        /// <returns>A fully initialized <see cref="Invoice"/> ready for persistence.</returns>
        private static Invoice BuildTestInvoice(Guid invoiceId, string invoiceNumber)
        {
            var now = DateTime.UtcNow;
            var userId = Guid.NewGuid();

            return new Invoice
            {
                Id = invoiceId,
                InvoiceNumber = invoiceNumber,
                CustomerId = Guid.NewGuid(),
                Status = InvoiceStatus.Draft,
                IssueDate = now,
                DueDate = now.AddDays(30),
                SubTotal = 200.00m,
                TaxAmount = 20.00m,
                TotalAmount = 220.00m,
                Notes = $"Test invoice {invoiceNumber}",
                CreatedBy = userId,
                CreatedOn = now,
                LastModifiedBy = userId,
                LastModifiedOn = now,
                LineItems = new List<LineItem>()
            };
        }

        /// <summary>
        /// Builds a list of test <see cref="LineItem"/> instances for a given invoice.
        /// Each item has incrementally increasing quantity and sort order, with a fixed
        /// UnitPrice of 50.00m and TaxRate of 0.10m (10%).
        /// </summary>
        /// <param name="invoiceId">The parent invoice ID for FK reference.</param>
        /// <param name="count">Number of line items to generate.</param>
        /// <returns>A list of <paramref name="count"/> test line items.</returns>
        private static List<LineItem> BuildTestLineItems(Guid invoiceId, int count)
        {
            var items = new List<LineItem>();

            for (int i = 0; i < count; i++)
            {
                var quantity = (i + 1) * 1.0m;
                var unitPrice = 50.00m;
                var taxRate = 0.10m;
                var lineTotal = quantity * unitPrice * (1m + taxRate);

                items.Add(new LineItem
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Description = $"Test line item {i + 1}",
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    TaxRate = taxRate,
                    LineTotal = lineTotal,
                    SortOrder = i + 1
                });
            }

            return items;
        }
    }
}
