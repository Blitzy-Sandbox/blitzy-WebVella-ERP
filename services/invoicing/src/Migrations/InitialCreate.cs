using System.Data;
using FluentMigrator;

namespace WebVellaErp.Invoicing.Migrations
{
    /// <summary>
    /// Initial FluentMigrator migration (version 1) that creates the complete RDS PostgreSQL
    /// schema for the Invoicing / Billing bounded-context service.
    ///
    /// This migration replaces the monolith's dynamic DDL helpers from DbRepository.cs
    /// (CreateTable, CreateColumn, CreateIndex, SetPrimaryKey, CreateUniqueConstraint,
    /// CreateRelation) with a versioned, auditable migration script.
    ///
    /// Tables created:
    ///   - invoicing.invoices    — Invoice headers with status, amounts, audit columns
    ///   - invoicing.line_items — Individual line items belonging to an invoice
    ///   - invoicing.payments   — Payment records associated with invoices
    ///
    /// Design decisions derived from source monolith:
    ///   - UUID primary keys with uuid_generate_v4() default (DbRepository.cs line 233, updated from v1 to v4)
    ///   - timestamptz for audit timestamps with now() default (DbRepository.cs line 229)
    ///   - numeric (arbitrary precision) for monetary amounts (DBTypeConverter.cs lines 22, 55, 61)
    ///   - Schema-level isolation per AAP §0.4.2 Database-Per-Service pattern
    ///   - No cross-service FK references per AAP §0.8.1 (customer_id is logical, not a DB FK)
    ///   - Audit columns pattern from DbEntityRepository.cs (created_by/last_modified_by relations)
    /// </summary>
    [Migration(1)]
    public class InitialCreate : Migration
    {
        /// <summary>
        /// Applies the initial invoicing schema: creates the PostgreSQL uuid-ossp extension,
        /// the invoicing schema, three tables (invoices, invoice_line_items, payments),
        /// all indexes, foreign keys, and default value expressions.
        /// </summary>
        public override void Up()
        {
            // ──────────────────────────────────────────────────────────────
            // Step 1: PostgreSQL Extensions
            // Derived from DbRepository.CreatePostgresqlExtensions() (source line 30):
            //   CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
            // Enables uuid_generate_v4() for random UUID generation on primary keys.
            // ──────────────────────────────────────────────────────────────
            Execute.Sql("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";");

            // ──────────────────────────────────────────────────────────────
            // Step 2: Create Invoicing Schema
            // Per AAP §0.4.2: Database-Per-Service with schema-level isolation.
            // All invoicing tables are placed in the "invoicing" schema to ensure
            // complete isolation from other bounded contexts sharing the same
            // RDS PostgreSQL instance.
            // ──────────────────────────────────────────────────────────────
            Execute.Sql("CREATE SCHEMA IF NOT EXISTS invoicing;");

            // ──────────────────────────────────────────────────────────────
            // Step 3: Create "invoices" Table
            // Core invoice header table. Column types derived from DBTypeConverter.cs:
            //   - uuid (GuidField, line 37)
            //   - numeric (CurrencyField/NumberField, lines 22/55)
            //   - date (DateField, line 25)
            //   - timestamptz (DateTimeField, line 29)
            //   - text (TextField/MultiLineTextField, line 70)
            //   - varchar(N) (SelectField, line 68)
            // ──────────────────────────────────────────────────────────────
            Create.Table("invoices").InSchema("invoicing")
                .WithColumn("id").AsGuid().NotNullable().PrimaryKey("pk_invoices")
                .WithColumn("invoice_number").AsString(200).NotNullable()
                .WithColumn("customer_id").AsGuid().NotNullable()
                .WithColumn("status").AsString(50).NotNullable().WithDefaultValue("draft")
                .WithColumn("issue_date").AsCustom("timestamptz").Nullable()
                .WithColumn("due_date").AsCustom("timestamptz").Nullable()
                .WithColumn("sub_total").AsDecimal().NotNullable().WithDefaultValue(0m)
                .WithColumn("tax_amount").AsDecimal().NotNullable().WithDefaultValue(0m)
                .WithColumn("total_amount").AsDecimal().NotNullable().WithDefaultValue(0m)
                .WithColumn("currency").AsCustom("jsonb").Nullable()
                .WithColumn("notes").AsCustom("text").Nullable()
                .WithColumn("created_by").AsGuid().NotNullable()
                .WithColumn("created_on").AsCustom("timestamptz").NotNullable()
                .WithColumn("last_modified_by").AsGuid().NotNullable()
                .WithColumn("last_modified_on").AsCustom("timestamptz").NotNullable();

            // PostgreSQL-specific defaults that FluentMigrator cannot express natively.
            // uuid_generate_v4() for random UUIDs (derived from DbRepository.cs line 233,
            // updated from uuid_generate_v1() to v4 for globally unique random IDs).
            Execute.Sql("ALTER TABLE invoicing.invoices ALTER COLUMN id SET DEFAULT uuid_generate_v4();");

            // now() defaults for audit timestamps (derived from DbRepository.cs line 229).
            Execute.Sql("ALTER TABLE invoicing.invoices ALTER COLUMN created_on SET DEFAULT now();");
            Execute.Sql("ALTER TABLE invoicing.invoices ALTER COLUMN last_modified_on SET DEFAULT now();");

            // ──────────────────────────────────────────────────────────────
            // Step 4: Indexes for "invoices" Table
            // Pattern from DbRepository.CreateIndex() (lines 461-491):
            //   CREATE INDEX IF NOT EXISTS "{indexName}" ON "{tableName}" ("{columnName}")
            // ──────────────────────────────────────────────────────────────

            // Index on customer_id for filtering invoices by customer.
            // Note: customer_id is a logical cross-service reference — NOT a database FK
            // per AAP §0.8.1 "Zero cross-service database access".
            Create.Index("idx_invoices_customer_id")
                .OnTable("invoices").InSchema("invoicing")
                .OnColumn("customer_id").Ascending();

            // Index on status for filtering by invoice lifecycle state (draft/issued/paid/voided).
            Create.Index("idx_invoices_status")
                .OnTable("invoices").InSchema("invoicing")
                .OnColumn("status").Ascending();

            // Index on issue_date for date-range queries on issued invoices.
            Create.Index("idx_invoices_issue_date")
                .OnTable("invoices").InSchema("invoicing")
                .OnColumn("issue_date").Ascending();

            // Index on due_date for due date filtering and overdue detection.
            Create.Index("idx_invoices_due_date")
                .OnTable("invoices").InSchema("invoicing")
                .OnColumn("due_date").Ascending();

            // UNIQUE constraint on invoice number for human-readable identifier uniqueness.
            // Pattern from DbRepository.CreateUniqueConstraint() (lines 310-332).
            Create.Index("uq_invoices_invoice_number")
                .OnTable("invoices").InSchema("invoicing")
                .OnColumn("invoice_number").Ascending()
                .WithOptions().Unique();

            // Composite index on (status, due_date) for the common "overdue invoices" query:
            //   SELECT * FROM invoicing.invoices WHERE status = 'issued' AND due_date < now()
            Create.Index("idx_invoices_status_due_date")
                .OnTable("invoices").InSchema("invoicing")
                .OnColumn("status").Ascending()
                .OnColumn("due_date").Ascending();

            // ──────────────────────────────────────────────────────────────
            // Step 5: Create "line_items" Table
            // Individual line items belonging to an invoice. Each line item records
            // a description, quantity, unit price, tax rate, and computed line total.
            // ──────────────────────────────────────────────────────────────
            Create.Table("line_items").InSchema("invoicing")
                .WithColumn("id").AsGuid().NotNullable().PrimaryKey("pk_line_items")
                .WithColumn("invoice_id").AsGuid().NotNullable()
                .WithColumn("description").AsCustom("text").NotNullable()
                .WithColumn("quantity").AsDecimal().NotNullable().WithDefaultValue(1m)
                .WithColumn("unit_price").AsDecimal().NotNullable().WithDefaultValue(0m)
                .WithColumn("tax_rate").AsDecimal().NotNullable().WithDefaultValue(0m)
                .WithColumn("line_total").AsDecimal().NotNullable().WithDefaultValue(0m)
                .WithColumn("sort_order").AsInt32().NotNullable().WithDefaultValue(0);

            // UUID default for line item primary keys.
            Execute.Sql("ALTER TABLE invoicing.line_items ALTER COLUMN id SET DEFAULT uuid_generate_v4();");

            // Foreign key from invoice_line_items.invoice_id → invoices.id with CASCADE delete.
            // When an invoice is deleted, all associated line items are removed automatically.
            // Pattern from DbRepository.CreateRelation() (line 404):
            //   ALTER TABLE "{targetTable}" ADD CONSTRAINT "{relName}" FOREIGN KEY ("{targetField}")
            //   REFERENCES "{originTable}" ("{originField}");
            Create.ForeignKey("fk_line_items_invoice")
                .FromTable("line_items").InSchema("invoicing").ForeignColumn("invoice_id")
                .ToTable("invoices").InSchema("invoicing").PrimaryColumn("id")
                .OnDelete(Rule.Cascade);

            // ──────────────────────────────────────────────────────────────
            // Step 6: Indexes for "line_items" Table
            // ──────────────────────────────────────────────────────────────

            // Index on invoice_id for efficient join/lookup of line items by invoice.
            Create.Index("idx_line_items_invoice_id")
                .OnTable("line_items").InSchema("invoicing")
                .OnColumn("invoice_id").Ascending();

            // Index on sort_order for ordered retrieval of line items within an invoice.
            Create.Index("idx_line_items_sort_order")
                .OnTable("line_items").InSchema("invoicing")
                .OnColumn("sort_order").Ascending();

            // ──────────────────────────────────────────────────────────────
            // Step 7: Create "payments" Table
            // Payment records associated with invoices. Tracks amount, date, method,
            // and an optional external reference number.
            // ──────────────────────────────────────────────────────────────
            Create.Table("payments").InSchema("invoicing")
                .WithColumn("id").AsGuid().NotNullable().PrimaryKey("pk_payments")
                .WithColumn("invoice_id").AsGuid().NotNullable()
                .WithColumn("amount").AsDecimal().NotNullable()
                .WithColumn("payment_date").AsCustom("timestamptz").NotNullable()
                .WithColumn("payment_method").AsString(100).NotNullable()
                .WithColumn("reference_number").AsString(200).Nullable()
                .WithColumn("notes").AsCustom("text").Nullable()
                .WithColumn("created_by").AsGuid().NotNullable()
                .WithColumn("created_on").AsCustom("timestamptz").NotNullable();

            // UUID default for payment primary keys.
            Execute.Sql("ALTER TABLE invoicing.payments ALTER COLUMN id SET DEFAULT uuid_generate_v4();");

            // now() default for payment audit timestamp.
            Execute.Sql("ALTER TABLE invoicing.payments ALTER COLUMN created_on SET DEFAULT now();");

            // Foreign key from payments.invoice_id → invoices.id WITHOUT cascade delete.
            // Payments must be preserved for audit trail integrity even if the invoice
            // record undergoes status changes or corrections.
            Create.ForeignKey("fk_payments_invoice")
                .FromTable("payments").InSchema("invoicing").ForeignColumn("invoice_id")
                .ToTable("invoices").InSchema("invoicing").PrimaryColumn("id");

            // ──────────────────────────────────────────────────────────────
            // Step 8: Indexes for "payments" Table
            // ──────────────────────────────────────────────────────────────

            // Index on invoice_id for efficient lookup of payments by invoice.
            Create.Index("idx_payments_invoice_id")
                .OnTable("payments").InSchema("invoicing")
                .OnColumn("invoice_id").Ascending();

            // Index on payment_date for date-range queries on payment history.
            Create.Index("idx_payments_payment_date")
                .OnTable("payments").InSchema("invoicing")
                .OnColumn("payment_date").Ascending();

            // Index on payment_method for filtering payments by method (bank_transfer, credit_card, cash, etc.).
            Create.Index("idx_payments_payment_method")
                .OnTable("payments").InSchema("invoicing")
                .OnColumn("payment_method").Ascending();
        }

        /// <summary>
        /// Reverses the initial migration by dropping all invoicing tables in reverse
        /// dependency order (payments → invoice_line_items → invoices) and then
        /// removing the invoicing schema.
        ///
        /// Note: The uuid-ossp extension is intentionally NOT dropped because other
        /// schemas and services sharing the same RDS instance may depend on it.
        /// </summary>
        public override void Down()
        {
            // Drop tables in reverse dependency order to satisfy FK constraints.
            // Foreign keys are dropped automatically when the referencing table is dropped.

            // 1. Drop payments table first (references invoices via fk_payments_invoice).
            Delete.Table("payments").InSchema("invoicing");

            // 2. Drop line_items (references invoices via fk_line_items_invoice).
            Delete.Table("line_items").InSchema("invoicing");

            // 3. Drop invoices table last (referenced by both payments and line items).
            Delete.Table("invoices").InSchema("invoicing");

            // 4. Drop the invoicing schema. CASCADE handles any remaining objects
            // (e.g., sequences, functions) that may have been created within the schema.
            Execute.Sql("DROP SCHEMA IF EXISTS invoicing CASCADE;");

            // Note: Do NOT drop the uuid-ossp extension as other schemas may depend on it.
        }
    }
}
