using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Mail.Database.Migrations
{
    /// <summary>
    /// Initial migration for the Mail service database.
    /// Codifies the cumulative schema state from all MailPlugin patches
    /// (20190215 through 20200611) into a single EF Core migration.
    /// Replaces the monolith's MailPlugin.ProcessPatches() method.
    ///
    /// This is NOT an incremental migration — it represents the END STATE
    /// after all 7 date-stamped patches have been applied. Legacy fields
    /// that were created and later deleted (recipient_name, sender_name,
    /// recipient_email, sender_email) are NOT included.
    ///
    /// Tables created:
    ///   - rec_email        (17 columns, 8 indexes)
    ///   - rec_smtp_service (14 columns, 3 indexes)
    ///
    /// Source patches consolidated:
    ///   - Patch20190215: Initial email + smtp_service entity creation
    ///   - Patch20190419: Added sender/recipients, deleted legacy fields, updated x_search
    ///   - Patch20190420: Page/sitemap updates only (no schema changes)
    ///   - Patch20190422: Page/sitemap updates only (no schema changes)
    ///   - Patch20190529: Added attachments field
    ///   - Patch20200610: App metadata update only (no schema changes)
    ///   - Patch20200611: Data source definition update only (no schema changes)
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <summary>
        /// Applies the initial schema for the Mail service database.
        /// Creates the rec_email and rec_smtp_service tables with all columns,
        /// constraints, default values, and indexes matching the monolith's
        /// cumulative final state.
        /// </summary>
        /// <param name="migrationBuilder">The migration builder used to construct DDL operations.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // =================================================================
            // Step 1: Create rec_email table
            // =================================================================
            // Represents the email entity from the monolith.
            // Cumulative final state after Patch20190215, Patch20190419, and
            // Patch20190529. Legacy fields deleted in Patch20190419 are excluded.
            // Total: 17 columns (id + 16 data fields)
            // =================================================================

            migrationBuilder.CreateTable(
                name: "rec_email",
                columns: table => new
                {
                    // Primary key — UUID assigned by application code
                    // Source: Patch20190215 entity creation (systemFieldIdDictionary["id"])
                    id = table.Column<Guid>(type: "uuid", nullable: false),

                    // Email subject line. Text, max 1000 chars, optional.
                    // Source: Patch20190215 InputTextField, MaxLength=1000, Required=false
                    subject = table.Column<string>(type: "text", maxLength: 1000, nullable: true),

                    // Plain text body content. Unlimited length, optional.
                    // Source: Patch20190215 InputTextField, MaxLength=null, Required=false
                    content_text = table.Column<string>(type: "text", nullable: true),

                    // HTML body content. Unlimited length, optional.
                    // Source: Patch20190215 InputHtmlField, Required=false
                    content_html = table.Column<string>(type: "text", nullable: true),

                    // Timestamp when the email was actually sent. Nullable (null = not yet sent).
                    // Source: Patch20190215 InputDateTimeField, Required=false, Searchable=true
                    sent_on = table.Column<DateTime>(type: "timestamptz", nullable: true),

                    // Timestamp when the email record was created. Required, defaults to now().
                    // Source: Patch20190215 InputDateTimeField, Required=true, UseCurrentTimeAsDefaultValue=true
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),

                    // Server error message from last send attempt. Optional.
                    // Source: Patch20190215 InputTextField, MaxLength=null, Required=false
                    server_error = table.Column<string>(type: "text", nullable: true),

                    // Number of retry attempts made. Numeric, required, defaults to 0.
                    // Source: Patch20190215 InputNumberField, Required=true, DefaultValue=0.0, DecimalPlaces=0
                    retries_count = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 0m),

                    // Reference to the smtp_service entity that should send this email.
                    // Source: Patch20190215 InputGuidField, Required=true, Searchable=true
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),

                    // Email priority: "0" = low, "1" = normal (default), "2" = high.
                    // Source: Patch20190215 InputSelectField, Required=true, Searchable=true, DefaultValue="1"
                    priority = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false, defaultValue: "1"),

                    // Reply-to email address override. Optional text field.
                    // Source: Patch20190215 InputTextField, Required=false, DefaultValue=""
                    reply_to_email = table.Column<string>(type: "text", nullable: true),

                    // Scheduled send time. Nullable (null = send immediately).
                    // Source: Patch20190215 InputDateTimeField, Required=false, Searchable=true
                    scheduled_on = table.Column<DateTime>(type: "timestamptz", nullable: true),

                    // Email status: "0" = pending (default), "1" = sent, "2" = aborted.
                    // Source: Patch20190215 InputSelectField, Required=true, DefaultValue="0"
                    status = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false, defaultValue: "0"),

                    // Full-text search index field. Aggregates searchable content for ILIKE queries.
                    // Source: Patch20190215 InputTextField, Required=true, DefaultValue=""
                    // Updated in Patch20190419: Searchable changed to false in metadata, but column/index persists.
                    x_search = table.Column<string>(type: "text", nullable: false, defaultValue: ""),

                    // JSON-serialized sender information. Required, searchable with index.
                    // Source: Patch20190419 InputTextField, Required=true, DefaultValue="[]", Searchable=true
                    // Replaces legacy sender_name and sender_email fields deleted in same patch.
                    sender = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),

                    // JSON-serialized recipients list. Required, searchable with index.
                    // Source: Patch20190419 InputTextField, Required=true, DefaultValue="", Searchable=true
                    // Replaces legacy recipient_name and recipient_email fields deleted in same patch.
                    recipients = table.Column<string>(type: "text", nullable: false, defaultValue: ""),

                    // JSON-serialized attachments list. Required, defaults to "[]".
                    // Source: Patch20190529 InputTextField, Required=true, DefaultValue="[]"
                    attachments = table.Column<string>(type: "text", nullable: false, defaultValue: "[]")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_email", x => x.id);
                });

            // =================================================================
            // Step 2: Create indexes for rec_email
            // =================================================================
            // Indexes match the monolith's Searchable=true fields from patch
            // definitions. The WebVella ERP monolith creates indexes on all
            // fields where Searchable = true.
            // =================================================================

            // sent_on: Searchable=true in Patch20190215
            migrationBuilder.CreateIndex(
                name: "idx_rec_email_sent_on",
                table: "rec_email",
                column: "sent_on");

            // created_on: Searchable=true in Patch20190215
            migrationBuilder.CreateIndex(
                name: "idx_rec_email_created_on",
                table: "rec_email",
                column: "created_on");

            // service_id: Searchable=true in Patch20190215
            migrationBuilder.CreateIndex(
                name: "idx_rec_email_service_id",
                table: "rec_email",
                column: "service_id");

            // priority: Searchable=true in Patch20190215
            migrationBuilder.CreateIndex(
                name: "idx_rec_email_priority",
                table: "rec_email",
                column: "priority");

            // scheduled_on: Searchable=true in Patch20190215
            migrationBuilder.CreateIndex(
                name: "idx_rec_email_scheduled_on",
                table: "rec_email",
                column: "scheduled_on");

            // x_search: Originally Searchable=true in Patch20190215, updated to
            // Searchable=false in Patch20190419 metadata. The database index persists
            // as it was already created and not explicitly dropped.
            migrationBuilder.CreateIndex(
                name: "idx_rec_email_x_search",
                table: "rec_email",
                column: "x_search");

            // sender: Searchable=true in Patch20190419
            migrationBuilder.CreateIndex(
                name: "idx_rec_email_sender",
                table: "rec_email",
                column: "sender");

            // recipients: Searchable=true in Patch20190419
            migrationBuilder.CreateIndex(
                name: "idx_rec_email_recipients",
                table: "rec_email",
                column: "recipients");

            // =================================================================
            // Step 3: Create rec_smtp_service table
            // =================================================================
            // Represents the smtp_service entity from the monolith.
            // Created in Patch20190215 with no subsequent schema modifications.
            // Total: 14 columns (id + 13 data fields)
            // =================================================================

            migrationBuilder.CreateTable(
                name: "rec_smtp_service",
                columns: table => new
                {
                    // Primary key — UUID assigned by application code
                    // Source: Patch20190215 entity creation (systemFieldIdDictionary["id"])
                    id = table.Column<Guid>(type: "uuid", nullable: false),

                    // Default reply-to email address for this SMTP service. Optional.
                    // Source: Patch20190215 InputEmailField, Required=false, MaxLength=null
                    // Note: InputEmailField defaults to varchar(500) in WebVella ERP.
                    default_reply_to_email = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),

                    // Maximum number of send retry attempts before aborting.
                    // Source: Patch20190215 InputNumberField, Required=true, DefaultValue=3.0, Min=0, Max=10
                    max_retries_count = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 3m),

                    // Minutes to wait between retry attempts.
                    // Source: Patch20190215 InputNumberField, Required=true, DefaultValue=60.0, Min=0, Max=1440
                    retry_wait_minutes = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 60m),

                    // Whether this is the default SMTP service. Boolean, required, default false.
                    // Source: Patch20190215 InputCheckboxField, Required=true, DefaultValue=false
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),

                    // Human-readable name for this SMTP service configuration.
                    // Source: Patch20190215 InputTextField, Required=true, Unique=true, Searchable=true, MaxLength=100
                    name = table.Column<string>(type: "text", maxLength: 100, nullable: false),

                    // SMTP server hostname or IP address. Required.
                    // Source: Patch20190215 InputTextField, Required=true, DefaultValue="smtp.domain.com"
                    server = table.Column<string>(type: "text", nullable: false, defaultValue: "smtp.domain.com"),

                    // SMTP authentication username. Optional (only if server requires auth).
                    // Source: Patch20190215 InputTextField, Required=false
                    username = table.Column<string>(type: "text", nullable: true),

                    // SMTP authentication password. Optional (only if server requires auth).
                    // Source: Patch20190215 InputTextField, Required=false
                    password = table.Column<string>(type: "text", nullable: true),

                    // SMTP server port number. Numeric, required, default 25.
                    // Source: Patch20190215 InputNumberField, Required=true, DefaultValue=25.0, Min=1, Max=65535
                    port = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 25m),

                    // Connection security mode: "0"=None, "1"=Auto(default), "2"=SslOnConnect,
                    // "3"=StartTls, "4"=StartTlsWhenAvailable.
                    // Source: Patch20190215 InputSelectField, Required=true, DefaultValue="1"
                    connection_security = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false, defaultValue: "1"),

                    // Default sender display name. Optional, searchable.
                    // Source: Patch20190215 InputTextField, Required=false, Searchable=true
                    default_from_name = table.Column<string>(type: "text", nullable: true),

                    // Default sender email address. Required, searchable.
                    // Source: Patch20190215 InputEmailField, Required=true, Searchable=true, DefaultValue=""
                    // Note: InputEmailField defaults to varchar(500) in WebVella ERP.
                    default_from_email = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false, defaultValue: ""),

                    // Whether this SMTP service is currently active. Boolean, required, default true.
                    // Source: Patch20190215 InputCheckboxField, Required=true, Searchable=true, DefaultValue=true
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_smtp_service", x => x.id);
                });

            // =================================================================
            // Step 4: Create indexes for rec_smtp_service
            // =================================================================
            // Includes unique constraint on name and indexes on searchable fields.
            // =================================================================

            // name: Unique=true, Searchable=true in Patch20190215
            // Unique index enforces the business rule that SMTP service names must be unique.
            migrationBuilder.CreateIndex(
                name: "idx_rec_smtp_service_name",
                table: "rec_smtp_service",
                column: "name",
                unique: true);

            // default_from_name: Searchable=true in Patch20190215
            migrationBuilder.CreateIndex(
                name: "idx_rec_smtp_service_default_from_name",
                table: "rec_smtp_service",
                column: "default_from_name");

            // default_from_email: Searchable=true in Patch20190215
            migrationBuilder.CreateIndex(
                name: "idx_rec_smtp_service_default_from_email",
                table: "rec_smtp_service",
                column: "default_from_email");
        }

        /// <summary>
        /// Reverses the initial migration by dropping both mail service tables.
        /// Tables are dropped in reverse creation order. DropTable automatically
        /// drops all indexes and constraints associated with the table.
        /// </summary>
        /// <param name="migrationBuilder">The migration builder used to construct DDL operations.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop in reverse creation order
            migrationBuilder.DropTable(name: "rec_smtp_service");
            migrationBuilder.DropTable(name: "rec_email");
        }
    }
}
