using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebVella.Erp.Service.Admin.Database.Migrations
{
    /// <summary>
    /// EF Core initial migration for the Admin service.
    /// Codifies the cumulative final state of the monolith's SDK plugin patch system
    /// (SdkPlugin patches 20181215 through 20210429) and system table definitions
    /// from ERPService.cs into a single EF Core migration.
    ///
    /// Creates the four tables owned exclusively by the Admin service
    /// in the database-per-service model:
    ///   1. system_log  — Diagnostic/error logging
    ///   2. jobs        — Background job execution tracking
    ///   3. schedule_plans — Recurring schedule plan definitions
    ///   4. plugin_data — Plugin settings/version persistence
    ///
    /// Seeds the "Clear job and error logs" schedule plan and the SDK
    /// plugin data record with final version 20210429.
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ================================================================
            // 1. Create system_log table
            //    Source: ERPService.cs lines 1158-1185
            //    Columns preserve exact snake_case names and PostgreSQL types.
            // ================================================================
            migrationBuilder.CreateTable(
                name: "system_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_on = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false,
                        defaultValueSql: "'2011-11-11 02:11:11+02'::timestamp with time zone"),
                    type = table.Column<int>(
                        type: "integer",
                        nullable: false,
                        defaultValue: 1),
                    source = table.Column<string>(
                        type: "text",
                        nullable: false,
                        defaultValueSql: "'source'::text"),
                    message = table.Column<string>(
                        type: "text",
                        nullable: false,
                        defaultValueSql: "'message'::text"),
                    details = table.Column<string>(type: "text", nullable: true),
                    notification_status = table.Column<int>(
                        type: "integer",
                        nullable: false,
                        defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("system_log_pkey", x => x.id);
                });

            // Indexes on system_log — exactly matching ERPService.cs lines 1171-1184
            migrationBuilder.CreateIndex(
                name: "idx_system_log_created_on",
                table: "system_log",
                column: "created_on");

            migrationBuilder.CreateIndex(
                name: "idx_system_log_message",
                table: "system_log",
                column: "message");

            migrationBuilder.CreateIndex(
                name: "idx_system_log_notification_status",
                table: "system_log",
                column: "notification_status");

            migrationBuilder.CreateIndex(
                name: "idx_system_log_source",
                table: "system_log",
                column: "source");

            migrationBuilder.CreateIndex(
                name: "idx_system_log_type",
                table: "system_log",
                column: "type");

            // ================================================================
            // 2. Create jobs table
            //    Source: ERPService.cs lines 1061-1084 (base columns)
            //           ERPService.cs line 1143 (result column, added via ALTER TABLE)
            //    The result column is included directly here as cumulative final state.
            // ================================================================
            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_name = table.Column<string>(type: "text", nullable: false),
                    complete_class_name = table.Column<string>(type: "text", nullable: false),
                    attributes = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    started_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    aborted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    canceled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    schedule_plan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_modified_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    last_modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    result = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("jobs_pkey", x => x.id);
                });

            // ================================================================
            // 3. Create schedule_plans table
            //    Source: ERPService.cs lines 1101-1125 (original "schedule_plan")
            //    AdminDbContext maps to "schedule_plans" (plural) per AAP 0.4.1.
            // ================================================================
            migrationBuilder.CreateTable(
                name: "schedule_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    start_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    end_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    schedule_days = table.Column<string>(type: "json", nullable: true),
                    interval_in_minutes = table.Column<int>(type: "integer", nullable: true),
                    start_timespan = table.Column<int>(type: "integer", nullable: true),
                    end_timespan = table.Column<int>(type: "integer", nullable: true),
                    last_trigger_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_trigger_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    job_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_attributes = table.Column<string>(type: "text", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_started_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_modified_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("schedule_plans_pkey", x => x.id);
                });

            // ================================================================
            // 4. Create plugin_data table
            //    Source: ERPService.cs lines 1201-1208
            // ================================================================
            migrationBuilder.CreateTable(
                name: "plugin_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(
                        type: "text",
                        nullable: false,
                        defaultValueSql: "''::text"),
                    data = table.Column<string>(
                        type: "text",
                        nullable: true,
                        defaultValueSql: "''::text")
                },
                constraints: table =>
                {
                    table.PrimaryKey("plugin_data_pkey", x => x.id);
                });

            // Unique constraint on plugin_data.name — matching monolith exactly
            migrationBuilder.CreateIndex(
                name: "idx_u_plugin_data_name",
                table: "plugin_data",
                column: "name",
                unique: true);

            // ================================================================
            // 5. Seed data: "Clear job and error logs" schedule plan
            //    Source: SdkPlugin.cs SetSchedulePlans lines 72-106
            //    GUIDs preserved exactly from monolith source.
            // ================================================================
            migrationBuilder.InsertData(
                table: "schedule_plans",
                columns: new[]
                {
                    "id", "name", "type", "start_date", "end_date",
                    "schedule_days", "interval_in_minutes",
                    "start_timespan", "end_timespan",
                    "last_trigger_time", "next_trigger_time",
                    "job_type_id", "job_attributes", "enabled",
                    "last_started_job_id", "created_on",
                    "last_modified_on", "last_modified_by"
                },
                values: new object[]
                {
                    new Guid("8CC1DF20-0967-4635-B44A-45FD90819105"),  // id
                    "Clear job and error logs.",                         // name
                    2,                                                  // type (Daily)
                    new DateTime(2025, 1, 1, 0, 0, 2, DateTimeKind.Utc), // start_date
                    null,                                               // end_date
                    "{\"scheduled_on_sunday\":true,\"scheduled_on_monday\":true,\"scheduled_on_tuesday\":true,\"scheduled_on_wednesday\":true,\"scheduled_on_thursday\":true,\"scheduled_on_friday\":true,\"scheduled_on_saturday\":true}", // schedule_days (JSON)
                    1440,                                               // interval_in_minutes
                    0,                                                  // start_timespan
                    1440,                                               // end_timespan
                    null,                                               // last_trigger_time
                    null,                                               // next_trigger_time
                    new Guid("99D9A8BB-31E6-4436-B0C2-20BD6AA23786"),  // job_type_id (ClearJobAndErrorLogsJob)
                    null,                                               // job_attributes
                    true,                                               // enabled
                    null,                                               // last_started_job_id
                    new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), // created_on
                    new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), // last_modified_on
                    null                                                // last_modified_by
                });

            // ================================================================
            // 6. Seed data: SDK plugin_data entry
            //    Source: SdkPlugin._.cs line 151 — SavePluginData with final version
            //    Plugin name "sdk" from SdkPlugin.cs line 13
            //    Final version 20210429 after all patches applied
            // ================================================================
            migrationBuilder.InsertData(
                table: "plugin_data",
                columns: new[] { "id", "name", "data" },
                values: new object[]
                {
                    new Guid("A61C4949-6B5C-44E6-94B5-E145B0B4C022"),  // deterministic id for sdk plugin data
                    "sdk",                                              // name (from SdkPlugin.cs Name property)
                    "{\"Version\":20210429}"                            // data (final cumulative version)
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop tables in reverse order of creation to respect any implicit dependencies.

            // 4. Drop plugin_data (created last in Up)
            migrationBuilder.DropTable(name: "plugin_data");

            // 3. Drop schedule_plans
            migrationBuilder.DropTable(name: "schedule_plans");

            // 2. Drop jobs
            migrationBuilder.DropTable(name: "jobs");

            // 1. Drop system_log indexes first, then the table
            migrationBuilder.DropIndex(name: "idx_system_log_type", table: "system_log");
            migrationBuilder.DropIndex(name: "idx_system_log_source", table: "system_log");
            migrationBuilder.DropIndex(name: "idx_system_log_notification_status", table: "system_log");
            migrationBuilder.DropIndex(name: "idx_system_log_message", table: "system_log");
            migrationBuilder.DropIndex(name: "idx_system_log_created_on", table: "system_log");
            migrationBuilder.DropTable(name: "system_log");
        }
    }
}
