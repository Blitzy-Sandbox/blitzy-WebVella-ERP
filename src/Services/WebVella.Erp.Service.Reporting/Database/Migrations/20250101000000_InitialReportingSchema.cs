using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Reporting.Database.Migrations
{
    /// <summary>
    /// Initial migration for the Reporting service database (erp_reporting).
    /// Creates four projection tables for report aggregation:
    /// - timelog_projections: denormalized timelog data with task/project/account info
    /// - task_projections: task summaries with project associations
    /// - project_projections: project metadata for report grouping
    /// - report_definitions: stored report configurations
    ///
    /// Replaces the monolith's PluginSettings.Version + ProcessPatches() pattern
    /// with standard EF Core Migrations (AAP 0.7.5).
    ///
    /// The Reporting service follows the CQRS (light) pattern (AAP 0.4.3) — it reads
    /// from event-sourced projections populated by MassTransit consumers, and does NOT
    /// directly access other services' databases. These projection tables store
    /// pre-computed cross-service data that the monolith's ReportService.GetTimelogData()
    /// previously obtained via direct EQL queries across shared tables.
    ///
    /// Cross-service references (account_id, task_id, project_id, created_by) are stored
    /// as plain UUID columns WITHOUT foreign key constraints, per the database-per-service
    /// model (AAP 0.8.2). Resolution of cross-service references happens at the service
    /// layer via gRPC/REST calls (AAP 0.7.1).
    ///
    /// Column naming uses snake_case to match the monolith's PostgreSQL convention.
    /// Column types match PostgreSQL types used by the monolith's entity system:
    /// uuid, text, timestamptz, numeric, boolean, varchar(N).
    /// </summary>
    public partial class InitialReportingSchema : Migration
    {
        /// <summary>
        /// Applies the initial schema for the Reporting service database.
        /// Creates the uuid-ossp extension and all four projection/definition tables
        /// with their associated indexes.
        ///
        /// Table creation order:
        /// 1. timelog_projections — denormalized timelog data (6 indexes including 1 composite)
        /// 2. task_projections — task summaries (1 index)
        /// 3. project_projections — project metadata (2 indexes)
        /// 4. report_definitions — stored report configurations (3 indexes)
        /// </summary>
        /// <param name="migrationBuilder">The EF Core migration builder for DDL operations.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ================================================================
            // Step 1: Enable uuid-ossp extension for UUID generation support.
            // Matches the monolith pattern from DbRepository.CreatePostgresqlExtensions()
            // (source: WebVella.Erp/Database/DbRepository.cs line 30).
            // ================================================================
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";");

            // ================================================================
            // Step 2: Create timelog_projections table.
            // Denormalized timelog data populated by event subscribers consuming
            // timelog events from the Project service. Schema derived from the
            // fields queried in ReportService.GetTimelogData():
            //   - id, is_billable, minutes, logged_on, scope from timelog entity
            //   - task_id extracted from l_related_records JSON (source line 51-53)
            //   - project_id denormalized from task->project relation (source line 108)
            //   - account_id denormalized from project->account (source line 85)
            //
            // Cross-service UUID references (task_id, project_id, account_id) have
            // NO foreign key constraints per database-per-service model (AAP 0.8.2).
            // ================================================================
            migrationBuilder.CreateTable(
                name: "timelog_projections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_billable = table.Column<bool>(type: "boolean", nullable: false),
                    minutes = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 0m),
                    logged_on = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false, defaultValue: "projects"),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    last_modified_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timelog_projections", x => x.id);
                });

            // ================================================================
            // Step 3: Create indexes for timelog_projections.
            // Indexes are optimized for the aggregation query patterns from
            // ReportService.GetTimelogData() filter patterns:
            //   - task_id: join/filter by task (source lines 122)
            //   - project_id: filter by project (source line 108)
            //   - account_id: filter by account (source lines 79-89)
            //   - logged_on: date-range filtering (source lines 42-44)
            //   - scope: scope filtering (source line 45)
            //   - logged_on + scope: composite for the common combined filter
            // ================================================================
            migrationBuilder.CreateIndex(
                name: "idx_timelog_proj_task_id",
                table: "timelog_projections",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "idx_timelog_proj_project_id",
                table: "timelog_projections",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "idx_timelog_proj_account_id",
                table: "timelog_projections",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "idx_timelog_proj_logged_on",
                table: "timelog_projections",
                column: "logged_on");

            migrationBuilder.CreateIndex(
                name: "idx_timelog_proj_scope",
                table: "timelog_projections",
                column: "scope");

            // Composite index for the common date-range + scope filter from
            // ReportService.GetTimelogData() (source lines 42-45):
            // WHERE logged_on >= @from_date AND logged_on <= @to_date AND l_scope CONTAINS @scope
            migrationBuilder.CreateIndex(
                name: "idx_timelog_proj_logged_on_scope",
                table: "timelog_projections",
                columns: new[] { "logged_on", "scope" });

            // ================================================================
            // Step 4: Create task_projections table.
            // Task summaries populated by event subscribers consuming task
            // CRUD events from the Project service. Derived from the task fields
            // queried in ReportService.GetTimelogData() (source line 60):
            //   SELECT id, subject, $task_type_1n_task.label FROM task
            // ================================================================
            migrationBuilder.CreateTable(
                name: "task_projections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    task_type_label = table.Column<string>(type: "text", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    last_modified_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_projections", x => x.id);
                });

            // ================================================================
            // Step 5: Create indexes for task_projections.
            // Subject index supports text-based searches on task names.
            // ================================================================
            migrationBuilder.CreateIndex(
                name: "idx_task_proj_subject",
                table: "task_projections",
                column: "subject");

            // ================================================================
            // Step 6: Create project_projections table.
            // Project metadata populated by event subscribers consuming project
            // CRUD events. Derived from the project fields queried in
            // ReportService.GetTimelogData() (source line 60):
            //   $project_nn_task.id, $project_nn_task.name, $project_nn_task.account_id
            //
            // account_id is a cross-service UUID reference to the CRM service's
            // account entity — stored without FK constraint (AAP 0.8.2).
            // ================================================================
            migrationBuilder.CreateTable(
                name: "project_projections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    last_modified_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_projections", x => x.id);
                });

            // ================================================================
            // Step 7: Create indexes for project_projections.
            // account_id index supports filtering reports by account.
            // name index supports lookup and grouping by project name.
            // ================================================================
            migrationBuilder.CreateIndex(
                name: "idx_project_proj_account_id",
                table: "project_projections",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "idx_project_proj_name",
                table: "project_projections",
                column: "name");

            // ================================================================
            // Step 8: Create report_definitions table.
            // Stored report configurations — a new concept for the Reporting
            // microservice that enables persistent report definitions. In the
            // monolith, ReportService.GetTimelogData() parameters were ephemeral
            // (year, month, accountId passed per call). This table allows saving
            // report configurations for reuse.
            //
            // created_by is a cross-service UUID reference to the Core service's
            // user entity — stored without FK constraint (AAP 0.8.2).
            // ================================================================
            migrationBuilder.CreateTable(
                name: "report_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", maxLength: 500, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    report_type = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false, defaultValue: "timelog"),
                    parameters_json = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    last_modified_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_definitions", x => x.id);
                });

            // ================================================================
            // Step 9: Create indexes for report_definitions.
            // name: supports lookup by report name.
            // report_type: supports filtering by report type (e.g., "timelog").
            // created_by: supports filtering reports by the creating user.
            // ================================================================
            migrationBuilder.CreateIndex(
                name: "idx_report_def_name",
                table: "report_definitions",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "idx_report_def_report_type",
                table: "report_definitions",
                column: "report_type");

            migrationBuilder.CreateIndex(
                name: "idx_report_def_created_by",
                table: "report_definitions",
                column: "created_by");

            // ================================================================
            // Step 10: Seed default report definitions.
            // Pre-populates the Monthly Timelog Report definition so that
            // GET /api/v3.0/p/reporting/definitions returns persisted data
            // from the database rather than hardcoded in-memory values.
            // Uses the well-known ID from ReportController.MonthlyTimelogReportId.
            // ================================================================
            migrationBuilder.InsertData(
                table: "report_definitions",
                columns: new[] { "id", "name", "description", "report_type", "parameters_json", "created_by" },
                values: new object[]
                {
                    new Guid("a0d5e2f1-b3c4-4d6e-8f7a-9b0c1d2e3f4a"),
                    "Monthly Timelog Report",
                    "Aggregated timelog data by task and project for a given month, optionally filtered by account.",
                    "timelog_monthly",
                    "{\"year\":{\"type\":\"int\",\"required\":true},\"month\":{\"type\":\"int\",\"required\":true},\"accountId\":{\"type\":\"Guid?\",\"required\":false}}",
                    new Guid("b0000000-0000-0000-0000-000000000001") // system user
                });
        }

        /// <summary>
        /// Reverses the initial schema migration by dropping all four tables
        /// in reverse creation order. DropTable automatically removes all
        /// associated indexes, primary key constraints, and default values.
        ///
        /// Drop order (reverse of creation):
        /// 1. report_definitions
        /// 2. project_projections
        /// 3. task_projections
        /// 4. timelog_projections
        ///
        /// Per AAP 0.8.1: schema migrations must be idempotent and reversible.
        /// </summary>
        /// <param name="migrationBuilder">The EF Core migration builder for DDL operations.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "report_definitions");
            migrationBuilder.DropTable(name: "project_projections");
            migrationBuilder.DropTable(name: "task_projections");
            migrationBuilder.DropTable(name: "timelog_projections");
        }
    }
}
