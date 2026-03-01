using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using WebVellaErp.Invoicing.Migrations;

namespace WebVellaErp.Invoicing.Tests.Integration
{
    /// <summary>
    /// Integration tests that verify the FluentMigrator <see cref="InitialCreate"/> migration
    /// against a real LocalStack-hosted RDS PostgreSQL instance.
    ///
    /// These tests confirm that InitialCreate.Up() correctly creates:
    ///   - The "invoicing" schema
    ///   - The uuid-ossp PostgreSQL extension
    ///   - The invoices table (15 columns with correct types, PK, unique constraint)
    ///   - The invoice_line_items table (8 columns with FK to invoices)
    ///   - The payments table (9 columns with FK to invoices, no cascade)
    ///   - All expected indexes (11 indexes across the 3 tables)
    ///   - uuid_generate_v4() defaults on all id columns
    ///   - now() defaults on all audit timestamp columns
    ///
    /// And that InitialCreate.Down() reverses everything cleanly.
    ///
    /// Per AAP §0.8.4: ALL integration tests MUST execute against LocalStack-hosted
    /// PostgreSQL — no mocked DB connections.
    ///
    /// Replaces verification of DDL patterns from:
    ///   - DbRepository.cs (CreateTable, CreateColumn, CreateIndex, SetPrimaryKey,
    ///     CreateUniqueConstraint, CreateRelation)
    ///   - DBTypeConverter.cs (PostgreSQL type mappings: uuid, numeric, timestamptz, text, date)
    ///   - DbEntityRepository.cs (entity table creation with columns)
    /// </summary>
    public class MigrationIntegrationTests : IClassFixture<LocalStackFixture>, IAsyncLifetime
    {
        /// <summary>
        /// Shared fixture providing LocalStack infrastructure: PostgreSQL connection,
        /// FluentMigrator runner, and AWS SDK clients.
        /// </summary>
        private readonly LocalStackFixture _fixture;

        /// <summary>
        /// Initializes a new instance of <see cref="MigrationIntegrationTests"/>
        /// with the shared <see cref="LocalStackFixture"/>.
        /// </summary>
        /// <param name="fixture">Shared test fixture providing LocalStack infrastructure.</param>
        public MigrationIntegrationTests(LocalStackFixture fixture)
        {
            _fixture = fixture;
        }

        /// <summary>
        /// Called before each test method. Ensures the invoicing schema is in a
        /// valid state for the test.
        ///
        /// The <see cref="LocalStackFixture"/> runs MigrateUp() during its own
        /// InitializeAsync (once per test collection), so the schema and all
        /// tables already exist. Individual tests operate against this shared
        /// schema state.
        ///
        /// If a previous test (e.g., Down_DropsAllTablesInCorrectOrder) or a
        /// prior test run's DisposeAsync dropped the invoicing schema, this
        /// method detects the absence and re-creates it. The key challenge is
        /// that FluentMigrator's VersionInfo table lives in the public schema
        /// and persists across schema drops — if VersionInfo shows the migration
        /// as already applied, MigrateUp() becomes a no-op even though the
        /// invoicing schema no longer exists. This method handles that by:
        ///   1. Detecting the missing schema
        ///   2. Clearing stale VersionInfo entries so FluentMigrator will re-run
        ///   3. Building a fresh FluentMigrator runner to bypass in-memory caching
        ///   4. Running MigrateUp() to recreate the invoicing schema
        /// </summary>
        public async Task InitializeAsync()
        {
            // Check if the invoicing schema exists. If it does, no setup is needed—
            // the fixture's initial MigrateUp() already created everything.
            await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
            await connection.OpenAsync();

            await using var checkCmd = new NpgsqlCommand(
                "SELECT schema_name FROM information_schema.schemata WHERE schema_name = 'invoicing';",
                connection);
            var schemaExists = await checkCmd.ExecuteScalarAsync();

            if (schemaExists != null)
            {
                // Schema exists — all tables, indexes, and defaults are in place.
                return;
            }

            // Schema was dropped (by the Down() migration test, a prior fixture's
            // DisposeAsync, or a previous test run failure). The FluentMigrator
            // VersionInfo table lives in the public schema and persists across
            // invoicing schema drops. We must clear stale VersionInfo entries so
            // that MigrateUp() will re-execute the InitialCreate migration.
            await using var clearVersionCmd = new NpgsqlCommand(
                @"DO $$
                  BEGIN
                    IF EXISTS (
                      SELECT 1 FROM information_schema.tables
                      WHERE table_schema = 'public' AND table_name = 'VersionInfo'
                    ) THEN
                      DELETE FROM public.""VersionInfo"";
                    END IF;
                  END $$;",
                connection);
            await clearVersionCmd.ExecuteNonQueryAsync();

            // Build a fresh FluentMigrator runner with its own ServiceProvider to
            // bypass any in-memory migration version caching in the fixture's
            // singleton runner. The fresh runner reads the now-empty VersionInfo
            // table and re-executes all migrations.
            var freshServices = new ServiceCollection()
                .AddFluentMigratorCore()
                .ConfigureRunner(rb => rb
                    .AddPostgres()
                    .WithGlobalConnectionString(_fixture.ConnectionString)
                    .ScanIn(typeof(InitialCreate).Assembly).For.Migrations())
                .AddLogging(lb => lb.AddFluentMigratorConsole())
                .BuildServiceProvider(false);

            try
            {
                var freshRunner = freshServices.GetRequiredService<IMigrationRunner>();
                freshRunner.MigrateUp();
            }
            finally
            {
                if (freshServices is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        /// <summary>
        /// Called after each test method. Cleanup is handled by the
        /// <see cref="LocalStackFixture"/> DisposeAsync, so no per-test
        /// teardown is required.
        /// </summary>
        public Task DisposeAsync()
        {
            // Cleanup is deferred to the LocalStackFixture's DisposeAsync()
            // which drops the invoicing schema with CASCADE.
            return Task.CompletedTask;
        }

        // ──────────────────────────────────────────────────────────────────
        // Schema Verification Tests
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that InitialCreate.Up() creates the "invoicing" schema.
        /// Per AAP §0.4.2: Database-Per-Service with schema-level isolation.
        /// Pattern from source DbRepository.CreatePostgresqlExtensions (line 30).
        /// </summary>
        [RdsFact]
        public void InitialCreate_CreatesInvoicingSchema()
        {
            using var connection = _fixture.CreateNpgsqlConnection();
            using var cmd = new NpgsqlCommand(
                "SELECT schema_name FROM information_schema.schemata WHERE schema_name = @schema;",
                connection);
            cmd.Parameters.AddWithValue("@schema", "invoicing");

            var result = cmd.ExecuteScalar();

            result.Should().NotBeNull("the 'invoicing' schema should exist after migration");
            result!.ToString().Should().Be("invoicing");
        }

        /// <summary>
        /// Verifies that InitialCreate.Up() creates the uuid-ossp PostgreSQL extension.
        /// Enables uuid_generate_v4() for random UUID generation on primary keys.
        /// Pattern from source DbRepository.CreatePostgresqlExtensions (line 30):
        ///   CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
        /// </summary>
        [RdsFact]
        public void InitialCreate_CreatesUuidOsspExtension()
        {
            using var connection = _fixture.CreateNpgsqlConnection();
            using var cmd = new NpgsqlCommand(
                "SELECT extname FROM pg_extension WHERE extname = 'uuid-ossp';",
                connection);

            var result = cmd.ExecuteScalar();

            result.Should().NotBeNull("the uuid-ossp extension should be installed");
            result!.ToString().Should().Be("uuid-ossp");
        }

        // ──────────────────────────────────────────────────────────────────
        // Invoices Table Verification Tests
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that InitialCreate.Up() creates the invoicing.invoices table
        /// with exactly 15 columns, each having the correct PostgreSQL data type
        /// and nullability.
        ///
        /// Column types are derived from source DBTypeConverter.cs:
        ///   - uuid (GuidField, line 37)
        ///   - numeric (CurrencyField/NumberField, lines 22/55)
        ///   - date (DateField, line 25)
        ///   - timestamp with time zone (DateTimeField, line 29)
        ///   - text (TextField/MultiLineTextField, line 70)
        ///   - character varying (SelectField, line 68)
        /// </summary>
        [RdsFact]
        public void InitialCreate_CreatesInvoicesTable_WithCorrectColumns()
        {
            var columns = GetColumnInfo("invoicing", "invoices");

            // Verify total column count matches expected 15 columns
            columns.Should().HaveCount(15,
                "invoices table should have exactly 15 columns");

            // Verify each column's data type and nullability
            AssertColumn(columns, "id", "uuid", "NO");
            AssertColumn(columns, "number", "character varying", "NO");
            AssertColumn(columns, "customer_id", "uuid", "NO");
            AssertColumn(columns, "status", "character varying", "NO");
            AssertColumn(columns, "issue_date", "date", "YES");
            AssertColumn(columns, "due_date", "date", "YES");
            AssertColumn(columns, "subtotal", "numeric", "NO");
            AssertColumn(columns, "tax_amount", "numeric", "NO");
            AssertColumn(columns, "total_amount", "numeric", "NO");
            AssertColumn(columns, "currency", "character varying", "NO");
            AssertColumn(columns, "notes", "text", "YES");
            AssertColumn(columns, "created_by", "uuid", "NO");
            AssertColumn(columns, "created_on", "timestamp with time zone", "NO");
            AssertColumn(columns, "last_modified_by", "uuid", "NO");
            AssertColumn(columns, "last_modified_on", "timestamp with time zone", "NO");
        }

        /// <summary>
        /// Verifies that the invoices table has a PRIMARY KEY constraint on the "id" column.
        /// Pattern from source DbRepository.CreateColumn (line 241-242):
        ///   if (isPrimaryKey) sql += " PRIMARY KEY";
        /// </summary>
        [RdsFact]
        public void InitialCreate_InvoicesTable_HasPrimaryKey_OnId()
        {
            using var connection = _fixture.CreateNpgsqlConnection();
            using var cmd = new NpgsqlCommand(@"
                SELECT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                    AND tc.table_schema = kcu.table_schema
                WHERE tc.table_schema = 'invoicing'
                    AND tc.table_name = 'invoices'
                    AND tc.constraint_type = 'PRIMARY KEY';", connection);

            using var reader = cmd.ExecuteReader();
            reader.Read().Should().BeTrue("invoices table should have a primary key");
            reader.GetString(0).Should().Be("id",
                "the primary key should be on the 'id' column");
        }

        /// <summary>
        /// Verifies that the invoices table has a UNIQUE index on the "number" column.
        /// This ensures human-readable invoice numbers are globally unique.
        /// Pattern from source DbRepository.CreateUniqueConstraint (lines 310-332).
        ///
        /// FluentMigrator's Create.Index(...).WithOptions().Unique() creates a UNIQUE INDEX
        /// (not a UNIQUE CONSTRAINT), so we verify via pg_indexes.
        /// </summary>
        [RdsFact]
        public void InitialCreate_InvoicesTable_HasUniqueConstraint_OnNumber()
        {
            // Verify the unique index exists
            IndexExists("invoicing", "uq_invoices_number").Should().BeTrue(
                "a unique index should exist on invoices.number");

            // Verify it is actually a UNIQUE index by inspecting the index definition
            using var connection = _fixture.CreateNpgsqlConnection();
            using var cmd = new NpgsqlCommand(@"
                SELECT indexdef
                FROM pg_indexes
                WHERE schemaname = 'invoicing'
                    AND indexname = 'uq_invoices_number';", connection);

            var indexDef = cmd.ExecuteScalar()?.ToString();
            indexDef.Should().NotBeNull();
            indexDef.Should().Contain("UNIQUE",
                "the index on invoices.number should be a UNIQUE index");
        }

        // ──────────────────────────────────────────────────────────────────
        // Invoice Line Items Table Verification Tests
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that InitialCreate.Up() creates the invoicing.invoice_line_items table
        /// with exactly 8 columns having the correct data types and nullability.
        /// </summary>
        [RdsFact]
        public void InitialCreate_CreatesLineItemsTable_WithCorrectColumns()
        {
            var columns = GetColumnInfo("invoicing", "invoice_line_items");

            // Verify total column count
            columns.Should().HaveCount(8,
                "invoice_line_items table should have exactly 8 columns");

            // Verify each column
            AssertColumn(columns, "id", "uuid", "NO");
            AssertColumn(columns, "invoice_id", "uuid", "NO");
            AssertColumn(columns, "description", "text", "NO");
            AssertColumn(columns, "quantity", "numeric", "NO");
            AssertColumn(columns, "unit_price", "numeric", "NO");
            AssertColumn(columns, "tax_rate", "numeric", "NO");
            AssertColumn(columns, "line_total", "numeric", "NO");
            AssertColumn(columns, "sort_order", "integer", "NO");
        }

        /// <summary>
        /// Verifies that invoice_line_items has a FOREIGN KEY constraint from
        /// invoice_id to invoices.id with ON DELETE CASCADE.
        /// Pattern from source DbRepository.CreateRelation (line 404):
        ///   ALTER TABLE "{targetTable}" ADD CONSTRAINT "{relName}" FOREIGN KEY ("{targetField}")
        ///   REFERENCES "{originTable}" ("{originField}");
        /// </summary>
        [RdsFact]
        public void InitialCreate_LineItemsTable_HasForeignKey_ToInvoices()
        {
            using var connection = _fixture.CreateNpgsqlConnection();

            // Verify FK constraint exists on the invoice_line_items table
            using var fkCmd = new NpgsqlCommand(@"
                SELECT tc.constraint_name
                FROM information_schema.table_constraints tc
                WHERE tc.table_schema = 'invoicing'
                    AND tc.table_name = 'invoice_line_items'
                    AND tc.constraint_type = 'FOREIGN KEY'
                    AND tc.constraint_name = 'fk_line_items_invoice';", connection);

            var constraintName = fkCmd.ExecuteScalar();
            constraintName.Should().NotBeNull(
                "FK constraint 'fk_line_items_invoice' should exist on invoice_line_items");

            // Verify the FK references invoices.id
            using var refCmd = new NpgsqlCommand(@"
                SELECT ccu.table_name, ccu.column_name
                FROM information_schema.constraint_column_usage ccu
                WHERE ccu.constraint_schema = 'invoicing'
                    AND ccu.constraint_name = 'fk_line_items_invoice';", connection);

            using var reader = refCmd.ExecuteReader();
            reader.Read().Should().BeTrue("FK should reference a target table");
            reader.GetString(0).Should().Be("invoices",
                "FK should reference the invoices table");
            reader.GetString(1).Should().Be("id",
                "FK should reference the invoices.id column");
            reader.Close();

            // Verify CASCADE delete rule
            using var cascadeCmd = new NpgsqlCommand(@"
                SELECT rc.delete_rule
                FROM information_schema.referential_constraints rc
                WHERE rc.constraint_schema = 'invoicing'
                    AND rc.constraint_name = 'fk_line_items_invoice';", connection);

            var deleteRule = cascadeCmd.ExecuteScalar()?.ToString();
            deleteRule.Should().Be("CASCADE",
                "line items FK should CASCADE delete when parent invoice is deleted");
        }

        // ──────────────────────────────────────────────────────────────────
        // Payments Table Verification Tests
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that InitialCreate.Up() creates the invoicing.payments table
        /// with exactly 9 columns having the correct data types and nullability.
        /// </summary>
        [RdsFact]
        public void InitialCreate_CreatesPaymentsTable_WithCorrectColumns()
        {
            var columns = GetColumnInfo("invoicing", "payments");

            // Verify total column count
            columns.Should().HaveCount(9,
                "payments table should have exactly 9 columns");

            // Verify each column
            AssertColumn(columns, "id", "uuid", "NO");
            AssertColumn(columns, "invoice_id", "uuid", "NO");
            AssertColumn(columns, "amount", "numeric", "NO");
            AssertColumn(columns, "payment_date", "date", "NO");
            AssertColumn(columns, "payment_method", "character varying", "NO");
            AssertColumn(columns, "reference_number", "character varying", "YES");
            AssertColumn(columns, "notes", "text", "YES");
            AssertColumn(columns, "created_by", "uuid", "NO");
            AssertColumn(columns, "created_on", "timestamp with time zone", "NO");
        }

        /// <summary>
        /// Verifies that payments has a FOREIGN KEY constraint from invoice_id
        /// to invoices.id WITHOUT ON DELETE CASCADE.
        /// Payments must be preserved for audit trail integrity even if the
        /// invoice record undergoes status changes or corrections.
        /// </summary>
        [RdsFact]
        public void InitialCreate_PaymentsTable_HasForeignKey_ToInvoices()
        {
            using var connection = _fixture.CreateNpgsqlConnection();

            // Verify FK constraint exists on the payments table
            using var fkCmd = new NpgsqlCommand(@"
                SELECT tc.constraint_name
                FROM information_schema.table_constraints tc
                WHERE tc.table_schema = 'invoicing'
                    AND tc.table_name = 'payments'
                    AND tc.constraint_type = 'FOREIGN KEY'
                    AND tc.constraint_name = 'fk_payments_invoice';", connection);

            var constraintName = fkCmd.ExecuteScalar();
            constraintName.Should().NotBeNull(
                "FK constraint 'fk_payments_invoice' should exist on payments");

            // Verify the FK references invoices.id
            using var refCmd = new NpgsqlCommand(@"
                SELECT ccu.table_name, ccu.column_name
                FROM information_schema.constraint_column_usage ccu
                WHERE ccu.constraint_schema = 'invoicing'
                    AND ccu.constraint_name = 'fk_payments_invoice';", connection);

            using var reader = refCmd.ExecuteReader();
            reader.Read().Should().BeTrue("FK should reference a target table");
            reader.GetString(0).Should().Be("invoices",
                "FK should reference the invoices table");
            reader.GetString(1).Should().Be("id",
                "FK should reference the invoices.id column");
            reader.Close();

            // Verify NO CASCADE delete rule — payments preserved for audit
            using var cascadeCmd = new NpgsqlCommand(@"
                SELECT rc.delete_rule
                FROM information_schema.referential_constraints rc
                WHERE rc.constraint_schema = 'invoicing'
                    AND rc.constraint_name = 'fk_payments_invoice';", connection);

            var deleteRule = cascadeCmd.ExecuteScalar()?.ToString();
            deleteRule.Should().Be("NO ACTION",
                "payments FK should NOT cascade delete — payments preserved for audit trail");
        }

        // ──────────────────────────────────────────────────────────────────
        // Index Verification Tests
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that InitialCreate.Up() creates all expected indexes across
        /// the three invoicing tables. Pattern from source DbRepository.CreateIndex
        /// (line 468): CREATE INDEX IF NOT EXISTS "{indexName}" ON "{tableName}" ("{columnName}")
        /// </summary>
        [RdsFact]
        public void InitialCreate_CreatesAllExpectedIndexes()
        {
            // Invoices table indexes
            IndexExists("invoicing", "idx_invoices_customer_id").Should().BeTrue(
                "index on invoices.customer_id should exist for customer filtering");
            IndexExists("invoicing", "idx_invoices_status").Should().BeTrue(
                "index on invoices.status should exist for lifecycle state filtering");
            IndexExists("invoicing", "idx_invoices_issue_date").Should().BeTrue(
                "index on invoices.issue_date should exist for date-range queries");
            IndexExists("invoicing", "idx_invoices_due_date").Should().BeTrue(
                "index on invoices.due_date should exist for overdue detection");
            IndexExists("invoicing", "uq_invoices_number").Should().BeTrue(
                "unique index on invoices.number should exist for invoice number uniqueness");
            IndexExists("invoicing", "idx_invoices_status_due_date").Should().BeTrue(
                "composite index on (status, due_date) should exist for overdue invoice queries");

            // Invoice line items table indexes
            IndexExists("invoicing", "idx_line_items_invoice_id").Should().BeTrue(
                "index on invoice_line_items.invoice_id should exist for join lookups");
            IndexExists("invoicing", "idx_line_items_sort_order").Should().BeTrue(
                "index on invoice_line_items.sort_order should exist for ordered retrieval");

            // Payments table indexes
            IndexExists("invoicing", "idx_payments_invoice_id").Should().BeTrue(
                "index on payments.invoice_id should exist for payment lookups by invoice");
            IndexExists("invoicing", "idx_payments_payment_date").Should().BeTrue(
                "index on payments.payment_date should exist for date-range queries");
            IndexExists("invoicing", "idx_payments_payment_method").Should().BeTrue(
                "index on payments.payment_method should exist for method filtering");
        }

        // ──────────────────────────────────────────────────────────────────
        // Default Value Verification Tests
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that all id columns (invoices.id, invoice_line_items.id, payments.id)
        /// have uuid_generate_v4() as default value for random UUID generation.
        /// Pattern from source DbRepository.cs line 233 (updated from uuid_generate_v1() to v4).
        /// </summary>
        [RdsFact]
        public void InitialCreate_IdColumns_HaveUuidGenerateV4Default()
        {
            // Verify invoices.id default
            var invoicesColumns = GetColumnInfo("invoicing", "invoices");
            var invoiceId = invoicesColumns.FirstOrDefault(c => c.Name == "id");
            invoiceId.Should().NotBeNull("invoices.id column should exist");
            invoiceId!.ColumnDefault.Should().NotBeNull("invoices.id should have a default value");
            invoiceId.ColumnDefault.Should().Contain("uuid_generate_v4()",
                "invoices.id default should use uuid_generate_v4() for random UUID generation");

            // Verify invoice_line_items.id default
            var lineItemColumns = GetColumnInfo("invoicing", "invoice_line_items");
            var lineItemId = lineItemColumns.FirstOrDefault(c => c.Name == "id");
            lineItemId.Should().NotBeNull("invoice_line_items.id column should exist");
            lineItemId!.ColumnDefault.Should().NotBeNull(
                "invoice_line_items.id should have a default value");
            lineItemId.ColumnDefault.Should().Contain("uuid_generate_v4()",
                "invoice_line_items.id default should use uuid_generate_v4()");

            // Verify payments.id default
            var paymentColumns = GetColumnInfo("invoicing", "payments");
            var paymentId = paymentColumns.FirstOrDefault(c => c.Name == "id");
            paymentId.Should().NotBeNull("payments.id column should exist");
            paymentId!.ColumnDefault.Should().NotBeNull("payments.id should have a default value");
            paymentId.ColumnDefault.Should().Contain("uuid_generate_v4()",
                "payments.id default should use uuid_generate_v4()");
        }

        /// <summary>
        /// Verifies that audit timestamp columns have now() as default value.
        /// Pattern from source DbRepository.cs line 229: DEFAULT now()
        ///
        /// Verified columns:
        ///   - invoices.created_on
        ///   - invoices.last_modified_on
        ///   - payments.created_on
        /// </summary>
        [RdsFact]
        public void InitialCreate_TimestampColumns_HaveNowDefault()
        {
            // Verify invoices.created_on default
            var invoicesColumns = GetColumnInfo("invoicing", "invoices");
            var createdOn = invoicesColumns.FirstOrDefault(c => c.Name == "created_on");
            createdOn.Should().NotBeNull("invoices.created_on column should exist");
            createdOn!.ColumnDefault.Should().NotBeNull(
                "invoices.created_on should have a default value");
            createdOn.ColumnDefault.Should().Contain("now()",
                "invoices.created_on default should use now() for automatic timestamp");

            // Verify invoices.last_modified_on default
            var lastModifiedOn = invoicesColumns.FirstOrDefault(c => c.Name == "last_modified_on");
            lastModifiedOn.Should().NotBeNull("invoices.last_modified_on column should exist");
            lastModifiedOn!.ColumnDefault.Should().NotBeNull(
                "invoices.last_modified_on should have a default value");
            lastModifiedOn.ColumnDefault.Should().Contain("now()",
                "invoices.last_modified_on default should use now() for automatic timestamp");

            // Verify payments.created_on default
            var paymentColumns = GetColumnInfo("invoicing", "payments");
            var paymentCreatedOn = paymentColumns.FirstOrDefault(c => c.Name == "created_on");
            paymentCreatedOn.Should().NotBeNull("payments.created_on column should exist");
            paymentCreatedOn!.ColumnDefault.Should().NotBeNull(
                "payments.created_on should have a default value");
            paymentCreatedOn.ColumnDefault.Should().Contain("now()",
                "payments.created_on default should use now() for automatic timestamp");
        }

        // ──────────────────────────────────────────────────────────────────
        // Down() Migration Verification Test
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that InitialCreate.Down() drops all tables in the correct
        /// dependency order (payments → invoice_line_items → invoices) and then
        /// removes the invoicing schema entirely.
        ///
        /// Uses <see cref="LocalStackFixture.ServiceProvider"/> to resolve
        /// <see cref="IMigrationRunner"/> directly via
        /// <see cref="ServiceProviderServiceExtensions.GetRequiredService{T}"/>
        /// for explicit migration control.
        ///
        /// After verification, re-runs Up() to restore state for subsequent tests.
        /// </summary>
        [RdsFact]
        public void Down_DropsAllTablesInCorrectOrder()
        {
            // Pre-condition: verify all tables exist before running Down()
            TableExists("invoicing", "invoices").Should().BeTrue(
                "invoices table should exist before Down() migration");
            TableExists("invoicing", "invoice_line_items").Should().BeTrue(
                "invoice_line_items table should exist before Down() migration");
            TableExists("invoicing", "payments").Should().BeTrue(
                "payments table should exist before Down() migration");

            // Act: resolve IMigrationRunner from the fixture's ServiceProvider
            // and execute MigrateDown(0) to reverse all applied migrations.
            // This calls InitialCreate.Down() which drops tables in dependency
            // order: payments → invoice_line_items → invoices → schema.
            var runner = _fixture.ServiceProvider
                .GetRequiredService<IMigrationRunner>();
            runner.MigrateDown(0);

            // Assert: all tables should no longer exist
            TableExists("invoicing", "payments").Should().BeFalse(
                "payments table should be dropped after Down() migration");
            TableExists("invoicing", "invoice_line_items").Should().BeFalse(
                "invoice_line_items table should be dropped after Down() migration");
            TableExists("invoicing", "invoices").Should().BeFalse(
                "invoices table should be dropped after Down() migration");

            // Assert: the invoicing schema itself should no longer exist
            using var connection = _fixture.CreateNpgsqlConnection();
            using var schemaCmd = new NpgsqlCommand(
                "SELECT schema_name FROM information_schema.schemata WHERE schema_name = @schema;",
                connection);
            schemaCmd.Parameters.AddWithValue("@schema", "invoicing");

            var schemaResult = schemaCmd.ExecuteScalar();
            schemaResult.Should().BeNull(
                "the invoicing schema should be dropped after Down() migration " +
                "via DROP SCHEMA IF EXISTS invoicing CASCADE");

            // Restore state: resolve runner and re-run MigrateUp() so that
            // subsequent tests (if any remain) have a valid schema to work with.
            runner.MigrateUp();
        }

        // ──────────────────────────────────────────────────────────────────
        // Private Helper Methods
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Checks whether a table exists in the specified schema.
        /// Uses parameterized query against information_schema.tables.
        /// </summary>
        /// <param name="schema">PostgreSQL schema name (e.g., "invoicing").</param>
        /// <param name="tableName">Table name to check (e.g., "invoices").</param>
        /// <returns>True if the table exists; false otherwise.</returns>
        private bool TableExists(string schema, string tableName)
        {
            using var connection = _fixture.CreateNpgsqlConnection();
            using var cmd = new NpgsqlCommand(@"
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.tables
                    WHERE table_schema = @schema AND table_name = @table
                );", connection);
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", tableName);
            return (bool)cmd.ExecuteScalar()!;
        }

        /// <summary>
        /// Retrieves column metadata for all columns in the specified table,
        /// including column name, data type, nullability, and default value.
        /// Uses parameterized query against information_schema.columns.
        /// </summary>
        /// <param name="schema">PostgreSQL schema name (e.g., "invoicing").</param>
        /// <param name="tableName">Table name to inspect (e.g., "invoices").</param>
        /// <returns>List of <see cref="ColumnInfo"/> records for the table.</returns>
        private List<ColumnInfo> GetColumnInfo(string schema, string tableName)
        {
            var columns = new List<ColumnInfo>();
            using var connection = _fixture.CreateNpgsqlConnection();
            using var cmd = new NpgsqlCommand(@"
                SELECT column_name, data_type, is_nullable, column_default
                FROM information_schema.columns
                WHERE table_schema = @schema AND table_name = @table
                ORDER BY ordinal_position;", connection);
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", tableName);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(new ColumnInfo(
                    Name: reader.GetString(0),
                    DataType: reader.GetString(1),
                    IsNullable: reader.GetString(2),
                    ColumnDefault: reader.IsDBNull(3) ? null : reader.GetString(3)
                ));
            }
            return columns;
        }

        /// <summary>
        /// Checks whether an index exists in the specified schema.
        /// Uses parameterized query against pg_indexes catalog.
        /// </summary>
        /// <param name="schema">PostgreSQL schema name (e.g., "invoicing").</param>
        /// <param name="indexName">Index name to check (e.g., "idx_invoices_customer_id").</param>
        /// <returns>True if the index exists; false otherwise.</returns>
        private bool IndexExists(string schema, string indexName)
        {
            using var connection = _fixture.CreateNpgsqlConnection();
            using var cmd = new NpgsqlCommand(@"
                SELECT EXISTS (
                    SELECT 1 FROM pg_indexes
                    WHERE schemaname = @schema AND indexname = @indexName
                );", connection);
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@indexName", indexName);
            return (bool)cmd.ExecuteScalar()!;
        }

        /// <summary>
        /// Asserts that a column exists in the column list with the expected
        /// data type and nullability flag.
        /// </summary>
        /// <param name="columns">Column metadata list from <see cref="GetColumnInfo"/>.</param>
        /// <param name="name">Expected column name.</param>
        /// <param name="expectedType">Expected PostgreSQL data_type string
        /// (e.g., "uuid", "numeric", "character varying", "timestamp with time zone").</param>
        /// <param name="expectedNullable">"YES" if nullable, "NO" if not nullable.</param>
        private static void AssertColumn(
            List<ColumnInfo> columns,
            string name,
            string expectedType,
            string expectedNullable)
        {
            var column = columns.FirstOrDefault(c => c.Name == name);
            column.Should().NotBeNull($"column '{name}' should exist in the table");
            column!.DataType.Should().Be(expectedType,
                $"column '{name}' data type should be '{expectedType}'");
            column.IsNullable.Should().Be(expectedNullable,
                $"column '{name}' nullability should be '{expectedNullable}'");
        }

        /// <summary>
        /// Immutable record type for column metadata retrieved from
        /// information_schema.columns. Used by <see cref="GetColumnInfo"/>
        /// and <see cref="AssertColumn"/> helper methods.
        /// </summary>
        /// <param name="Name">Column name from information_schema.columns.column_name.</param>
        /// <param name="DataType">PostgreSQL data type from information_schema.columns.data_type.</param>
        /// <param name="IsNullable">"YES" or "NO" from information_schema.columns.is_nullable.</param>
        /// <param name="ColumnDefault">Column default expression from information_schema.columns.column_default,
        /// or null if no default is set.</param>
        private record ColumnInfo(string Name, string DataType, string IsNullable, string? ColumnDefault);
    }
}
