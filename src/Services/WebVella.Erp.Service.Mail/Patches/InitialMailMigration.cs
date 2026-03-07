// =============================================================================
// InitialMailMigration.cs — Initial EF Core Migration for Mail Service
// =============================================================================
// This migration codifies the CUMULATIVE state of all 7 monolith date-stamped
// patches (20190215 through 20200611) plus the orchestrator logic from
// MailPlugin._.cs. It replaces the monolith's custom date-based plugin
// versioning system (PluginSettings.Version + plugin_data JSON persistence)
// with standard EF Core migrations stored in __EFMigrationsHistory.
//
// CRITICAL: This single initial migration represents the NET EFFECT of ALL 7
// patches applied in sequence. It does NOT replay patches individually — it
// captures the final schema state after all patches have been applied.
//
// Source Patches Consolidated:
//   - MailPlugin.20190215.cs  → Entity/field creation (email + smtp_service)
//   - MailPlugin.20190419.cs  → Add sender/recipients fields, delete legacy
//                                fields, update x_search to Searchable=false
//   - MailPlugin.20190420.cs  → Page body node updates (UI only, see comments)
//   - MailPlugin.20190422.cs  → Page body node updates (UI only, see comments)
//   - MailPlugin.20190529.cs  → Add attachments field to email
//   - MailPlugin.20200610.cs  → Update mail app (weight → 100)
//   - MailPlugin.20200611.cs  → Update AllEmails datasource (final SQL/EQL)
//
// Migration Strategy Compliance (AAP 0.7.5):
//   Current Pattern              → Target Pattern (This File)
//   PluginSettings.Version       → EF Core __EFMigrationsHistory
//   GetPluginData()/SavePluginData() → DbContext.Database.Migrate()
//   Partial class patch methods  → This single EF Core migration class
//   EntityManager.CreateEntity() → migrationBuilder.CreateTable()
//   Single transaction all patches → Per-migration transaction (EF Core default)
// =============================================================================

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace WebVella.Erp.Service.Mail.Patches
{
    // =========================================================================
    // Well-Known GUIDs (Preserved from source patches for traceability)
    // =========================================================================
    //
    // --- Entities ---
    // Email entity:        085e2442-820a-4df7-ab92-516ce23197c4
    // SMTP Service entity: 17698b9f-e533-4f8d-a651-a00f7de2989e
    //
    // --- Email Fields (cumulative final state) ---
    // id (system):      9e8b7cd7-f340-411d-933a-62be8cb591e4
    // subject:          15af2fd2-a1cb-424d-b777-41139c65dbcc
    // content_text:     eb3a49f7-8216-4300-847f-15daca6cd087
    // content_html:     e1fd62b4-5630-4974-8ddf-0324f3d965e9
    // sent_on:          2adbf0ae-1701-4a07-8e2f-82be6740bb7a
    // created_on:       cf69678d-6447-4e2f-9e83-ccc9b5fa610f
    // server_error:     fee93ffb-7991-4f3c-9f78-1b18b9589422
    // retries_count:    9192c99e-ec5c-40d3-a591-fd46a92d15fa
    // service_id:       81119e86-bd2d-456b-8215-daafcac2870c
    // priority:         ffa6a87b-a638-4acd-8306-79ef2bf091c4
    // reply_to_email:   eb9cf95b-0876-4bb2-bfa8-c3970551e582
    // scheduled_on:     5c08f305-8209-4c96-a7ac-03043095cc73
    // status:           b1cd96e9-c786-4261-ab2b-1a51cab243e0
    // x_search:         9ab2ab99-7293-4772-8874-d7ca7383b317
    // sender:           8f59eaa9-873e-4461-83fb-34ecbbc88e7c  (added 20190419)
    // recipients:       ab748700-d13b-4df4-917e-093d74879a8e  (added 20190419)
    // attachments:      3e24f113-0236-4474-b6ed-adf0f29c052f  (added 20190529)
    //
    // --- Email Fields DELETED (NOT in final state) ---
    // recipient_name:   a3015639-7fd9-4231-89e3-76a7a133dd6d  (created 20190215, DELETED 20190419)
    // sender_name:      4d9e646c-0105-4370-ad21-d6547a7cabb1  (created 20190215, DELETED 20190419)
    // recipient_email:  cae76d3b-bf91-47bc-aec0-d7ac26eced7b  (created 20190215, DELETED 20190419)
    // sender_email:     94845377-b845-49fe-b693-789f1ed5740e  (created 20190215, DELETED 20190419)
    //
    // --- SMTP Service Fields ---
    // id (system):              f2f2e3ec-c7d5-4169-b175-b741c24b66b4
    // default_reply_to_email:   5da218c9-cfb3-41ce-9fee-350adc0b2d7d
    // max_retries_count:        5d4a40fd-d4c5-4d20-b45c-0622f8acbe93
    // retry_wait_minutes:       6f6e2836-955c-49a2-8880-7960c1c8206d
    // is_default:               27a24518-c8ea-4ad6-93ce-2baed017d782
    // name:                     d3406be9-6a81-46a3-a0be-f39d7bd55392
    // server:                   b827c863-1d75-4019-a3b5-3b0628b91cb2
    // username:                 e921a804-71ac-4af3-81a9-903b505dc53d
    // password:                 420b9f71-ed26-4fd1-9c25-933d39b7d610
    // port:                     8d52e394-8e1c-4e97-b192-06e238d6c550
    // connection_security:      fdd4123b-7578-4a57-9a57-ef51034fd145
    // default_from_name:      cd7c8228-e40b-4b1b-86d0-4764663f03ec
    // default_from_email:     362cd0c1-ea7c-4aee-8909-df3e91ec15cb
    // is_enabled:               6c3ba722-e78e-4365-8376-86584025c065
    //
    // --- Application/Metadata GUIDs ---
    // Mail App:           9d3b5497-e136-43b7-ad87-857e615a54c9
    // AllEmails DS:        82f0b63e-3647-4106-839c-4d5adca4f3b1
    // Emails Sitemap Area: c5835090-9089-496d-ac0f-6f67bb593384
    // Services Sitemap Area: fe4a1467-099e-4a3e-a9ed-f38a0d81a2f3
    //
    // --- Roles with Permissions ---
    // Administrator: f16ec6db-626d-4c27-8de0-3e7ce542c55f
    // Regular:       bdc56420-caf0-4030-8a0e-d264938e0cda
    // =========================================================================

    /// <summary>
    /// Initial EF Core migration for the Mail/Notification microservice.
    /// Creates the rec_email and rec_smtp_service tables with their final
    /// cumulative schema state, indexes, and seed data (application metadata,
    /// data sources, entity permissions).
    /// </summary>
    [Migration("20190215000000_InitialMailSetup")]
    public class InitialMailMigration : Migration
    {
        /// <summary>
        /// Applies the migration — creates tables, indexes, and seeds metadata.
        /// Tables reflect the FINAL state after ALL 7 patches (20190215–20200611).
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder for DDL operations.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // =================================================================
            // 1. Create rec_email table (17 columns — cumulative final state)
            // =================================================================
            // Source: Patch20190215 (initial 14 + 4 legacy fields)
            //       + Patch20190419 (add sender, recipients; delete 4 legacy fields;
            //                        update x_search Searchable=false)
            //       + Patch20190529 (add attachments)
            // Final column count: 17 (excluding 4 deleted legacy columns)
            migrationBuilder.CreateTable(
                name: "rec_email",
                columns: table => new
                {
                    // Primary key — system field (GUID 9e8b7cd7-f340-411d-933a-62be8cb591e4)
                    id = table.Column<Guid>(type: "uuid", nullable: false),

                    // Subject line — InputTextField, MaxLength=1000 (Patch20190215)
                    subject = table.Column<string>(type: "text", maxLength: 1000, nullable: true),

                    // Plain text content body (Patch20190215)
                    content_text = table.Column<string>(type: "text", nullable: true),

                    // HTML content body — InputHtmlField (Patch20190215)
                    content_html = table.Column<string>(type: "text", nullable: true),

                    // Timestamp when email was sent — Searchable=true (Patch20190215)
                    sent_on = table.Column<DateTime>(type: "timestamptz", nullable: true),

                    // Creation timestamp — Required, UseCurrentTimeAsDefaultValue=true (Patch20190215)
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),

                    // Server error message from send attempts (Patch20190215)
                    server_error = table.Column<string>(type: "text", nullable: true),

                    // Number of send retry attempts — Required, MinValue=0 (Patch20190215)
                    retries_count = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 0m),

                    // FK to SMTP service — Required, Searchable=true (Patch20190215)
                    service_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: Guid.Empty),

                    // Priority level — Select: low=0/normal=1/high=2, Searchable=true (Patch20190215)
                    priority = table.Column<string>(type: "varchar(200)", nullable: false, defaultValue: "1"),

                    // Reply-to email address — Optional (Patch20190215)
                    reply_to_email = table.Column<string>(type: "text", nullable: true, defaultValue: ""),

                    // Scheduled send time — Searchable=true (Patch20190215)
                    scheduled_on = table.Column<DateTime>(type: "timestamptz", nullable: true),

                    // Email status — Select: pending=0/sent=1/aborted=2 (Patch20190215)
                    status = table.Column<string>(type: "varchar(200)", nullable: false, defaultValue: "0"),

                    // Full-text search composite field — Required, Searchable=FALSE
                    // NOTE: Initially Searchable=true in Patch20190215, updated to
                    // Searchable=false in Patch20190419 (line 90). NO index created.
                    x_search = table.Column<string>(type: "text", nullable: false, defaultValue: ""),

                    // Sender JSON — Required, Searchable=true (added Patch20190419)
                    // Format: JSON object {"name":"...", "address":"..."}
                    sender = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),

                    // Recipients JSON — Required, Searchable=true (added Patch20190419)
                    // Format: JSON array [{"name":"...", "address":"..."}]
                    recipients = table.Column<string>(type: "text", nullable: false, defaultValue: ""),

                    // Attachments JSON array — Required (added Patch20190529)
                    attachments = table.Column<string>(type: "text", nullable: false, defaultValue: "[]")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_email", x => x.id);
                });

            // =================================================================
            // 2. Create indexes for rec_email
            // =================================================================
            // Based on Searchable=true fields from the FINAL cumulative state.
            // x_search does NOT get an index (Searchable was set to false in Patch20190419).
            // status does NOT get an index (Searchable=false in Patch20190215).
            migrationBuilder.CreateIndex(
                name: "idx_rec_email_sent_on",
                table: "rec_email",
                column: "sent_on");

            migrationBuilder.CreateIndex(
                name: "idx_rec_email_created_on",
                table: "rec_email",
                column: "created_on");

            migrationBuilder.CreateIndex(
                name: "idx_rec_email_service_id",
                table: "rec_email",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "idx_rec_email_priority",
                table: "rec_email",
                column: "priority");

            migrationBuilder.CreateIndex(
                name: "idx_rec_email_scheduled_on",
                table: "rec_email",
                column: "scheduled_on");

            migrationBuilder.CreateIndex(
                name: "idx_rec_email_sender",
                table: "rec_email",
                column: "sender");

            migrationBuilder.CreateIndex(
                name: "idx_rec_email_recipients",
                table: "rec_email",
                column: "recipients");

            // =================================================================
            // 3. Create rec_smtp_service table (14 columns)
            // =================================================================
            // Source: Patch20190215 only — NO subsequent schema changes to this table.
            migrationBuilder.CreateTable(
                name: "rec_smtp_service",
                columns: table => new
                {
                    // Primary key — system field (GUID f2f2e3ec-c7d5-4169-b175-b741c24b66b4)
                    id = table.Column<Guid>(type: "uuid", nullable: false),

                    // Default reply-to email — InputEmailField, MaxLength=500 (Patch20190215)
                    default_reply_to_email = table.Column<string>(type: "varchar(500)", nullable: true),

                    // Maximum number of send retries — Required, Min=0, Max=10 (Patch20190215)
                    max_retries_count = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 3m),

                    // Minutes to wait between retries — Required, Min=0, Max=1440 (Patch20190215)
                    retry_wait_minutes = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 60m),

                    // Whether this is the default SMTP service (Patch20190215)
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),

                    // Service name — Required, Unique=true, Searchable=true, MaxLength=100 (Patch20190215)
                    name = table.Column<string>(type: "text", maxLength: 100, nullable: false, defaultValue: "smtp service"),

                    // SMTP server hostname/IP — Required (Patch20190215)
                    server = table.Column<string>(type: "text", nullable: false, defaultValue: "smtp.domain.com"),

                    // SMTP authentication username — Optional (Patch20190215)
                    username = table.Column<string>(type: "text", nullable: true),

                    // SMTP authentication password — Optional (Patch20190215)
                    password = table.Column<string>(type: "text", nullable: true),

                    // SMTP port number — Required, Min=1, Max=65535 (Patch20190215)
                    port = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 25m),

                    // Connection security mode — Select: None=0/Auto=1/SslOnConnect=2/
                    // StartTls=3/StartTlsWhenAvailable=4 (Patch20190215)
                    connection_security = table.Column<string>(type: "varchar(200)", nullable: false, defaultValue: "1"),

                    // Default sender display name — Searchable=true (Patch20190215)
                    default_from_name = table.Column<string>(type: "text", nullable: true, defaultValue: ""),

                    // Default sender email address — Required, Searchable=true (Patch20190215)
                    default_from_email = table.Column<string>(type: "varchar(500)", nullable: false, defaultValue: ""),

                    // Whether this service is active — Required, Searchable=true (Patch20190215)
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_smtp_service", x => x.id);
                });

            // =================================================================
            // 4. Create indexes for rec_smtp_service
            // =================================================================
            // Unique constraint on name (Unique=true in Patch20190215)
            migrationBuilder.CreateIndex(
                name: "idx_rec_smtp_service_name_unique",
                table: "rec_smtp_service",
                column: "name",
                unique: true);

            // Searchable index on name (Searchable=true in Patch20190215)
            migrationBuilder.CreateIndex(
                name: "idx_rec_smtp_service_name",
                table: "rec_smtp_service",
                column: "name");

            // Searchable index on default_from_name (Searchable=true in Patch20190215)
            migrationBuilder.CreateIndex(
                name: "idx_rec_smtp_service_default_from_name",
                table: "rec_smtp_service",
                column: "default_from_name");

            // Searchable index on default_from_email (Searchable=true in Patch20190215)
            migrationBuilder.CreateIndex(
                name: "idx_rec_smtp_service_default_from_email",
                table: "rec_smtp_service",
                column: "default_from_email");

            // =================================================================
            // 5. Seed entity permission metadata
            // =================================================================
            // Both entities grant full CRUD to Administrator and Regular roles.
            // Source: Patch20190215 entity creation — RecordPermissions blocks
            SeedEntityPermissions(migrationBuilder);

            // =================================================================
            // 6. Seed application metadata
            // =================================================================
            // Mail application — FINAL state from Patch20200610 (weight=100)
            SeedApplicationMetadata(migrationBuilder);

            // =================================================================
            // 7. Seed data source definitions
            // =================================================================
            // AllEmails — FINAL state from Patch20200611 (supersedes Patch20190215)
            SeedDataSources(migrationBuilder);
        }

        /// <summary>
        /// Reverses the migration — drops tables, removes seed data.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder for DDL operations.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove seed data first (reverse order of creation)
            migrationBuilder.Sql(
                "DELETE FROM data_sources WHERE id = '82f0b63e-3647-4106-839c-4d5adca4f3b1';");

            migrationBuilder.Sql(
                "DELETE FROM apps WHERE id = '9d3b5497-e136-43b7-ad87-857e615a54c9';");

            migrationBuilder.Sql(
                "DELETE FROM entity_permissions WHERE entity_id = '085e2442-820a-4df7-ab92-516ce23197c4';");

            migrationBuilder.Sql(
                "DELETE FROM entity_permissions WHERE entity_id = '17698b9f-e533-4f8d-a651-a00f7de2989e';");

            // Drop tables (indexes are dropped automatically with the tables)
            migrationBuilder.DropTable(name: "rec_email");
            migrationBuilder.DropTable(name: "rec_smtp_service");
        }

        // =====================================================================
        // Private helper methods for seed data insertion
        // =====================================================================

        /// <summary>
        /// Seeds entity permission metadata for the email and smtp_service entities.
        /// Both entities grant CanCreate, CanRead, CanUpdate, CanDelete to:
        ///   - Administrator role (f16ec6db-626d-4c27-8de0-3e7ce542c55f)
        ///   - Regular role (bdc56420-caf0-4030-8a0e-d264938e0cda)
        /// Source: Patch20190215 — entity.RecordPermissions blocks for both entities.
        /// </summary>
        private static void SeedEntityPermissions(MigrationBuilder migrationBuilder)
        {
            // Email entity permissions
            // Entity ID: 085e2442-820a-4df7-ab92-516ce23197c4
            migrationBuilder.Sql(@"
                INSERT INTO entity_permissions (entity_id, role_id, can_create, can_read, can_update, can_delete)
                VALUES
                    ('085e2442-820a-4df7-ab92-516ce23197c4', 'f16ec6db-626d-4c27-8de0-3e7ce542c55f', true, true, true, true),
                    ('085e2442-820a-4df7-ab92-516ce23197c4', 'bdc56420-caf0-4030-8a0e-d264938e0cda', true, true, true, true)
                ON CONFLICT DO NOTHING;
            ");

            // SMTP Service entity permissions
            // Entity ID: 17698b9f-e533-4f8d-a651-a00f7de2989e
            migrationBuilder.Sql(@"
                INSERT INTO entity_permissions (entity_id, role_id, can_create, can_read, can_update, can_delete)
                VALUES
                    ('17698b9f-e533-4f8d-a651-a00f7de2989e', 'f16ec6db-626d-4c27-8de0-3e7ce542c55f', true, true, true, true),
                    ('17698b9f-e533-4f8d-a651-a00f7de2989e', 'bdc56420-caf0-4030-8a0e-d264938e0cda', true, true, true, true)
                ON CONFLICT DO NOTHING;
            ");
        }

        /// <summary>
        /// Seeds the Mail application metadata.
        /// FINAL state from Patch20200610 (supersedes Patch20190215 initial creation
        /// and Patch20190419 first update). Key difference: weight=100 (was 10).
        /// </summary>
        private static void SeedApplicationMetadata(MigrationBuilder migrationBuilder)
        {
            // Mail Application — FINAL state from Patch20200610
            // ID: 9d3b5497-e136-43b7-ad87-857e615a54c9
            migrationBuilder.Sql(@"
                INSERT INTO apps (id, name, label, description, icon_class, author, color, weight, access)
                VALUES (
                    '9d3b5497-e136-43b7-ad87-857e615a54c9',
                    'mail',
                    'Mail',
                    'Provides services for sending emails.',
                    'far fa-envelope',
                    'WebVella',
                    '#8bc34a',
                    100,
                    '[{""id"":""bdc56420-caf0-4030-8a0e-d264938e0cda""}]'
                )
                ON CONFLICT (id) DO UPDATE SET
                    weight = EXCLUDED.weight,
                    access = EXCLUDED.access;
            ");

            // NOTE: Sitemap areas, sitemap nodes, pages, and page body nodes from
            // Patch20190215 and updates from Patches 20190419/20190420/20190422 are
            // UI-layer concerns that may be provisioned by the Gateway/BFF service
            // instead. They are intentionally not included in this data-layer migration.
            //
            // Sitemap areas that would be provisioned by Gateway:
            //   - emails (c5835090-9089-496d-ac0f-6f67bb593384)
            //   - services (fe4a1467-099e-4a3e-a9ed-f38a0d81a2f3)
            //
            // Sitemap nodes that would be provisioned by Gateway:
            //   - all (91696f9d-b439-4823-95b7-afc05debf5e6) — email list
            //   - smtp (fbf00f09-67f1-4d36-97d0-6af509c360aa) — SMTP services
            //
            // Pages that would be provisioned by Gateway:
            //   - create (b5002548-daf7-456f-aa2c-43c205050195) — Create SMTP Service
            //   - details (6ee77414-ca24-4664-b73a-7c3cdd9c6bbb) — SMTP Service Details
            //   - all (31c40750-99c7-4402-9e9b-8157e9459df7) — SMTP Services list
            //   - all_emails (3374a8ee-653b-43f6-a4e8-c6db9a4f76d2) — All emails list
            //   - details (24d7c716-fa27-4ccd-99d1-c7a8813a13f2) — Email Details
            //   - service_test (27bfa096-8cac-4eea-b4c4-7f2a059b745b) — SMTP Service Test
        }

        /// <summary>
        /// Seeds the AllEmails data source definition.
        /// FINAL state from Patch20200611 — supersedes the initial definition
        /// from Patch20190215. Uses the updated EQL/SQL and field definitions.
        /// </summary>
        private static void SeedDataSources(MigrationBuilder migrationBuilder)
        {
            // AllEmails DataSource — FINAL state from Patch20200611
            // ID: 82f0b63e-3647-4106-839c-4d5adca4f3b1
            // Entity: email
            // Weight: 10
            migrationBuilder.Sql(@"
                INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name)
                VALUES (
                    '82f0b63e-3647-4106-839c-4d5adca4f3b1',
                    'AllEmails',
                    'records of all emails',
                    10,
                    'SELECT * FROM email
WHERE x_search CONTAINS @searchQuery
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize',
                    'SELECT row_to_json( X ) FROM (
SELECT 
	 rec_email.""id"" AS ""id"",
	 rec_email.""subject"" AS ""subject"",
	 rec_email.""content_text"" AS ""content_text"",
	 rec_email.""content_html"" AS ""content_html"",
	 rec_email.""sent_on"" AS ""sent_on"",
	 rec_email.""created_on"" AS ""created_on"",
	 rec_email.""server_error"" AS ""server_error"",
	 rec_email.""retries_count"" AS ""retries_count"",
	 rec_email.""service_id"" AS ""service_id"",
	 rec_email.""priority"" AS ""priority"",
	 rec_email.""reply_to_email"" AS ""reply_to_email"",
	 rec_email.""scheduled_on"" AS ""scheduled_on"",
	 rec_email.""status"" AS ""status"",
	 rec_email.""sender"" AS ""sender"",
	 rec_email.""recipients"" AS ""recipients"",
	 rec_email.""x_search"" AS ""x_search"",
	 rec_email.""attachments"" AS ""attachments"",
	 COUNT(*) OVER() AS ___total_count___
FROM rec_email
WHERE  ( rec_email.""x_search""  ILIKE  CONCAT ( ''%'' , @searchQuery , ''%'' ) )
ORDER BY rec_email.""created_on"" DESC
LIMIT 15
OFFSET 0
) X
',
                    '[{""name"":""sortBy"",""type"":""text"",""value"":""created_on"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""desc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""15"",""ignore_parse_errors"":false},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty"",""ignore_parse_errors"":false}]',
                    '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""content_text"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""content_html"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""sent_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""server_error"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""retries_count"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""service_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reply_to_email"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""scheduled_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""sender"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recipients"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""attachments"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]',
                    'email'
                )
                ON CONFLICT (id) DO UPDATE SET
                    eql_text = EXCLUDED.eql_text,
                    sql_text = EXCLUDED.sql_text,
                    parameters_json = EXCLUDED.parameters_json,
                    fields_json = EXCLUDED.fields_json,
                    weight = EXCLUDED.weight;
            ");
        }
    }
}
