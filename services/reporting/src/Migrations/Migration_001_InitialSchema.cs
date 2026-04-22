using FluentMigrator;

namespace WebVellaErp.Reporting.Migrations
{
    /// <summary>
    /// Initial FluentMigrator migration (version 1) that creates the complete RDS PostgreSQL
    /// schema for the Reporting &amp; Analytics bounded-context service.
    ///
    /// This migration replaces the monolith's dynamic DDL helpers from DbRepository.cs
    /// (CreateTable, CreateColumn, CreateIndex, SetPrimaryKey, CreateUniqueConstraint)
    /// with a versioned, auditable migration script.
    ///
    /// Tables created:
    ///   - reporting.report_definitions       — Report definition metadata (maps from monolith's data_source table)
    ///   - reporting.read_model_projections    — CQRS read-model projections from all bounded contexts
    ///   - reporting.event_offsets             — Idempotent event processing offset tracking per domain
    ///
    /// Design decisions derived from source monolith:
    ///   - UUID primary keys with uuid_generate_v4() default (DbRepository.cs line 233, updated from v1 to v4)
    ///   - timestamptz for audit timestamps with now() default (DbRepository.cs line 229)
    ///   - jsonb for flexible JSON storage (upgraded from text/varchar in monolith for query efficiency)
    ///   - Schema-level isolation per AAP §0.4.2 Database-Per-Service pattern
    ///   - No cross-service FK references per AAP §0.8.1 (source_record_id is logical, not a DB FK)
    ///   - CQRS read-model pattern per AAP §0.4.2 — consumes events from all domains
    ///   - Idempotent event consumption per AAP §0.8.5 via event_offsets tracking
    ///
    /// Source file references:
    ///   - WebVella.Erp/Database/DbRepository.cs — DDL patterns (extensions, table/column/index creation)
    ///   - WebVella.Erp/Database/DbDataSourceRepository.cs — data_source table schema (report_definitions mapping)
    ///   - WebVella.Erp/Database/DBTypeConverter.cs — PostgreSQL type mapping (uuid, timestamptz, text, boolean, integer)
    /// </summary>
    [Migration(1)]
    public class Migration_001_InitialSchema : Migration
    {
        /// <summary>
        /// Applies the initial reporting schema: creates the PostgreSQL uuid-ossp extension,
        /// the reporting schema, three tables (report_definitions, read_model_projections,
        /// event_offsets), all indexes (including composite and GIN), and default value expressions.
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
            // Step 2: Create Reporting Schema
            // Per AAP §0.4.2: Database-Per-Service with schema-level isolation.
            // All reporting tables are placed in the "reporting" schema to ensure
            // complete isolation from other bounded contexts sharing the same
            // RDS PostgreSQL instance (e.g., invoicing schema).
            // ──────────────────────────────────────────────────────────────
            Execute.Sql("CREATE SCHEMA IF NOT EXISTS reporting;");

            // ──────────────────────────────────────────────────────────────
            // Step 3: Create "report_definitions" Table
            // Maps from the monolith's public.data_source table (DbDataSourceRepository.cs
            // lines 85-86). Column types derived from DBTypeConverter.cs:
            //   - uuid (GuidField, line 37) for id, created_by
            //   - varchar(N) (various, lines 30-74) for name, entity_name
            //   - text (TextField, line 70) for description, sql_template
            //   - jsonb (upgraded from text/varchar for query efficiency) for parameters_json, fields_json
            //   - boolean (CheckboxField, line 19) for return_total
            //   - integer (AutoNumberField equivalent) for weight
            //   - timestamptz (DateTimeField, line 29) for created_at, updated_at
            //
            // Column mapping from monolith data_source table:
            //   data_source.id             → report_definitions.id
            //   data_source.name           → report_definitions.name
            //   data_source.description    → report_definitions.description
            //   data_source.eql_text + sql_text → report_definitions.sql_template (consolidated)
            //   data_source.parameters_json → report_definitions.parameters_json (text → jsonb)
            //   data_source.fields_json    → report_definitions.fields_json (text → jsonb)
            //   data_source.entity_name    → report_definitions.entity_name
            //   data_source.return_total   → report_definitions.return_total
            //   data_source.weight         → report_definitions.weight
            //   (new)                      → report_definitions.created_by (audit)
            //   (new)                      → report_definitions.created_at (audit)
            //   (new)                      → report_definitions.updated_at (audit)
            // ──────────────────────────────────────────────────────────────
            Create.Table("report_definitions").InSchema("reporting")
                .WithColumn("id").AsGuid().NotNullable().PrimaryKey("pk_report_definitions")
                .WithColumn("name").AsString(255).NotNullable()
                .WithColumn("description").AsCustom("text").Nullable()
                .WithColumn("sql_template").AsCustom("text").NotNullable()
                .WithColumn("parameters_json").AsCustom("jsonb").Nullable()
                .WithColumn("fields_json").AsCustom("jsonb").Nullable()
                .WithColumn("entity_name").AsString(255).Nullable()
                .WithColumn("return_total").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("weight").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("created_by").AsGuid().Nullable()
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
                .WithColumn("updated_at").AsCustom("timestamptz").NotNullable();

            // PostgreSQL-specific defaults that FluentMigrator cannot express natively.
            // uuid_generate_v4() for random UUIDs (derived from DbRepository.cs line 233,
            // updated from uuid_generate_v1() to v4 for globally unique random IDs).
            Execute.Sql("ALTER TABLE reporting.report_definitions ALTER COLUMN id SET DEFAULT uuid_generate_v4();");

            // now() defaults for audit timestamps (derived from DbRepository.cs line 229).
            Execute.Sql("ALTER TABLE reporting.report_definitions ALTER COLUMN created_at SET DEFAULT now();");
            Execute.Sql("ALTER TABLE reporting.report_definitions ALTER COLUMN updated_at SET DEFAULT now();");

            // ──────────────────────────────────────────────────────────────
            // Step 4: Indexes for "report_definitions" Table
            // Pattern from DbRepository.CreateIndex() (lines 461-491):
            //   CREATE INDEX IF NOT EXISTS "{indexName}" ON "{tableName}" ("{columnName}")
            // Pattern from DbRepository.CreateUniqueConstraint() (lines 310-332):
            //   ALTER TABLE "{tableName}" ADD CONSTRAINT "{constraintName}" UNIQUE (...)
            // ──────────────────────────────────────────────────────────────

            // UNIQUE index on name for human-readable report identifier uniqueness.
            // Maps from monolith's data_source.name unique constraint.
            Create.Index("uq_report_definitions_name")
                .OnTable("report_definitions").InSchema("reporting")
                .OnColumn("name").Ascending()
                .WithOptions().Unique();

            // Index on entity_name for filtering reports by source entity.
            // Common query pattern: "show all reports for entity X".
            Create.Index("idx_report_definitions_entity_name")
                .OnTable("report_definitions").InSchema("reporting")
                .OnColumn("entity_name").Ascending();

            // Index on created_by for filtering reports by creator.
            // Pattern from DbRepository.CreateIndex (line 468).
            Create.Index("idx_report_definitions_created_by")
                .OnTable("report_definitions").InSchema("reporting")
                .OnColumn("created_by").Ascending();

            // ──────────────────────────────────────────────────────────────
            // Step 5: Create "read_model_projections" Table
            // CQRS read-model table for event-sourced projections consumed from
            // ALL bounded contexts. The Reporting service subscribes to domain events
            // from invoicing, crm, inventory, entity-management, etc., and materializes
            // denormalized projections for efficient cross-domain reporting queries.
            //
            // Per AAP §0.4.2: CQRS (partial) — Reporting service consumes events
            // from all domains to build read-optimized projections.
            // Per AAP §0.8.5: Event naming convention {domain}.{entity}.{action}
            // drives the source_domain + source_entity column design.
            // ──────────────────────────────────────────────────────────────
            Create.Table("read_model_projections").InSchema("reporting")
                .WithColumn("id").AsGuid().NotNullable().PrimaryKey("pk_read_model_projections")
                .WithColumn("source_domain").AsString(100).NotNullable()
                .WithColumn("source_entity").AsString(100).NotNullable()
                .WithColumn("source_record_id").AsGuid().NotNullable()
                .WithColumn("projection_data").AsCustom("jsonb").NotNullable()
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
                .WithColumn("updated_at").AsCustom("timestamptz").NotNullable();

            // UUID default for read model projection primary keys.
            Execute.Sql("ALTER TABLE reporting.read_model_projections ALTER COLUMN id SET DEFAULT uuid_generate_v4();");

            // now() defaults for audit timestamps.
            Execute.Sql("ALTER TABLE reporting.read_model_projections ALTER COLUMN created_at SET DEFAULT now();");
            Execute.Sql("ALTER TABLE reporting.read_model_projections ALTER COLUMN updated_at SET DEFAULT now();");

            // ──────────────────────────────────────────────────────────────
            // Step 6: Indexes for "read_model_projections" Table
            // ──────────────────────────────────────────────────────────────

            // Composite index on (source_domain, source_entity, source_record_id).
            // This is the PRIMARY query pattern for looking up projections by their
            // source bounded context, entity type, and original record ID.
            // Enables efficient upsert-on-event operations: when an event arrives
            // from "crm.contact.updated", the consumer queries by all three columns
            // to find and update the existing projection.
            Create.Index("idx_rmp_domain_entity_record")
                .OnTable("read_model_projections").InSchema("reporting")
                .OnColumn("source_domain").Ascending()
                .OnColumn("source_entity").Ascending()
                .OnColumn("source_record_id").Ascending();

            // Index on source_domain for filtering all projections from a specific
            // bounded context (e.g., "show all CRM projections" for domain-level reports).
            Create.Index("idx_rmp_source_domain")
                .OnTable("read_model_projections").InSchema("reporting")
                .OnColumn("source_domain").Ascending();

            // Index on updated_at for recent-changes queries in reporting dashboards.
            // Supports "most recently updated projections" queries for real-time dashboards.
            Create.Index("idx_rmp_updated_at")
                .OnTable("read_model_projections").InSchema("reporting")
                .OnColumn("updated_at").Ascending();

            // GIN index on projection_data JSONB column for efficient containment (@>)
            // and key-existence (?) queries. Enables ad-hoc filtering within the
            // denormalized projection data without requiring additional columns.
            // Example: WHERE projection_data @> '{"status": "active"}'
            Execute.Sql("CREATE INDEX idx_rmp_projection_data ON reporting.read_model_projections USING gin(projection_data);");

            // ──────────────────────────────────────────────────────────────
            // Step 7: Create "event_offsets" Table
            // Tracks event processing offsets for idempotent consumption per AAP §0.8.5.
            // Each bounded context domain has exactly one row in this table, recording
            // the ID of the last successfully processed event. This enables:
            //   - Idempotent replay: events with IDs <= last_event_id are skipped
            //   - Gap detection: monitor last_processed_at for stale consumers
            //   - Exactly-once semantics: combined with SQS at-least-once delivery
            // ──────────────────────────────────────────────────────────────
            Create.Table("event_offsets").InSchema("reporting")
                .WithColumn("id").AsGuid().NotNullable().PrimaryKey("pk_event_offsets")
                .WithColumn("source_domain").AsString(100).NotNullable()
                .WithColumn("last_event_id").AsString(255).NotNullable()
                .WithColumn("last_processed_at").AsCustom("timestamptz").NotNullable();

            // UUID default for event offset primary keys.
            Execute.Sql("ALTER TABLE reporting.event_offsets ALTER COLUMN id SET DEFAULT uuid_generate_v4();");

            // now() default for last_processed_at timestamp.
            Execute.Sql("ALTER TABLE reporting.event_offsets ALTER COLUMN last_processed_at SET DEFAULT now();");

            // ──────────────────────────────────────────────────────────────
            // Step 8: Indexes for "event_offsets" Table
            // ──────────────────────────────────────────────────────────────

            // UNIQUE index on source_domain ensures exactly one offset per bounded context.
            // This is critical for idempotent event processing — each domain (e.g., "invoicing",
            // "crm", "inventory") has a single offset record that is atomically updated
            // after successful event processing.
            Create.Index("uq_event_offsets_source_domain")
                .OnTable("event_offsets").InSchema("reporting")
                .OnColumn("source_domain").Ascending()
                .WithOptions().Unique();
        }

        /// <summary>
        /// Reverses the initial migration by dropping all reporting tables in reverse
        /// creation order (event_offsets → read_model_projections → report_definitions)
        /// and then removing the reporting schema.
        ///
        /// Note: This Down() method exists for development/testing use only.
        /// Per AAP requirements, production uses forward-only migrations (no automatic rollback).
        ///
        /// Note: The uuid-ossp extension is intentionally NOT dropped because other
        /// schemas and services sharing the same RDS instance may depend on it
        /// (e.g., the invoicing schema also uses uuid_generate_v4()).
        /// </summary>
        public override void Down()
        {
            // Drop tables in reverse creation order. No FK relationships exist between
            // reporting tables (by design — all cross-references are logical, not physical),
            // but maintaining reverse order is a best practice for consistency.

            // 1. Drop event_offsets table (created last in Up).
            Delete.Table("event_offsets").InSchema("reporting");

            // 2. Drop read_model_projections table (created second in Up).
            Delete.Table("read_model_projections").InSchema("reporting");

            // 3. Drop report_definitions table (created first in Up).
            Delete.Table("report_definitions").InSchema("reporting");

            // 4. Drop the reporting schema. CASCADE handles any remaining objects
            // (e.g., sequences, functions, indexes) that may have been created within the schema.
            Execute.Sql("DROP SCHEMA IF EXISTS reporting CASCADE;");

            // Note: Do NOT drop the uuid-ossp extension as other schemas may depend on it.
        }
    }
}
