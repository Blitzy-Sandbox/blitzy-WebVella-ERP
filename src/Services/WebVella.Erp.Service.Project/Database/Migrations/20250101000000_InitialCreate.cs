using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebVella.Erp.Service.Project.Database.Migrations
{
    /// <summary>
    /// Initial EF Core migration for the Project/Task microservice database.
    /// Creates all record tables (rec_*), relation/join tables (rel_*),
    /// and seeds reference data for task_status and task_type entities.
    /// This migration consolidates the cumulative schema state from all
    /// monolith ProjectPlugin patches and NextPlugin entity definitions.
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------------------------------------------------------------
            // Reference/Lookup Tables (no FK dependencies)
            // ---------------------------------------------------------------

            migrationBuilder.CreateTable(
                name: "rec_task_status",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    icon_class = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true),
                    sort_index = table.Column<int>(type: "integer", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_closed = table.Column<bool>(type: "boolean", nullable: false),
                    l_scope = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_task_status", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rec_task_type",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    icon_class = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true),
                    sort_index = table.Column<int>(type: "integer", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    l_scope = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_task_type", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rec_milestone",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    start_date = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    end_date = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_milestone", x => x.id);
                });

            // ---------------------------------------------------------------
            // Entity Tables with FK references
            // ---------------------------------------------------------------

            migrationBuilder.CreateTable(
                name: "rec_project",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    abbr = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true),
                    icon = table.Column<string>(type: "text", nullable: true),
                    start_date = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    end_date = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    is_billable = table.Column<bool>(type: "boolean", nullable: false),
                    scope_key = table.Column<string>(type: "text", nullable: true),
                    x_billable_hours = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    x_nonbillable_hours = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    x_tasks_not_started = table.Column<int>(type: "integer", nullable: false),
                    x_tasks_in_progress = table.Column<int>(type: "integer", nullable: false),
                    x_tasks_completed = table.Column<int>(type: "integer", nullable: false),
                    x_overdue_tasks = table.Column<int>(type: "integer", nullable: false),
                    x_milestones_on_track = table.Column<int>(type: "integer", nullable: false),
                    x_milestones_missed = table.Column<int>(type: "integer", nullable: false),
                    x_budget = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_project", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rec_task",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    start_date = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    target_date = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_on = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    priority = table.Column<string>(type: "text", nullable: true),
                    x_nonbillable_minutes = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    x_billable_minutes = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    l_scope = table.Column<string>(type: "text", nullable: true),
                    l_related_records = table.Column<string>(type: "text", nullable: true),
                    x_search = table.Column<string>(type: "text", nullable: true),
                    recurrence_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_task", x => x.id);
                    table.ForeignKey(
                        name: "FK_rec_task_rec_task_parent_id",
                        column: x => x.parent_id,
                        principalTable: "rec_task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_rec_task_rec_task_status_status_id",
                        column: x => x.status_id,
                        principalTable: "rec_task_status",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_rec_task_rec_task_type_type_id",
                        column: x => x.type_id,
                        principalTable: "rec_task_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "rec_timelog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    l_related_records = table.Column<string>(type: "text", nullable: true),
                    l_scope = table.Column<string>(type: "text", nullable: true),
                    minutes = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    is_billable = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_timelog", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rec_comment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    l_scope = table.Column<string>(type: "text", nullable: true),
                    l_related_records = table.Column<string>(type: "text", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_comment", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rec_feed_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    l_scope = table.Column<string>(type: "text", nullable: true),
                    subject = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true),
                    l_related_records = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_feed_item", x => x.id);
                });

            // ---------------------------------------------------------------
            // M:N Join Tables
            // ---------------------------------------------------------------

            migrationBuilder.CreateTable(
                name: "rel_project_nn_task",
                columns: table => new
                {
                    origin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rel_project_nn_task", x => new { x.origin_id, x.target_id });
                    table.ForeignKey(
                        name: "FK_rel_project_nn_task_rec_project_origin_id",
                        column: x => x.origin_id,
                        principalTable: "rec_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_rel_project_nn_task_rec_task_target_id",
                        column: x => x.target_id,
                        principalTable: "rec_task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rel_milestone_nn_task",
                columns: table => new
                {
                    origin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rel_milestone_nn_task", x => new { x.origin_id, x.target_id });
                    table.ForeignKey(
                        name: "FK_rel_milestone_nn_task_rec_milestone_origin_id",
                        column: x => x.origin_id,
                        principalTable: "rec_milestone",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_rel_milestone_nn_task_rec_task_target_id",
                        column: x => x.target_id,
                        principalTable: "rec_task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rel_project_nn_milestone",
                columns: table => new
                {
                    origin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rel_project_nn_milestone", x => new { x.origin_id, x.target_id });
                    table.ForeignKey(
                        name: "FK_rel_project_nn_milestone_rec_project_origin_id",
                        column: x => x.origin_id,
                        principalTable: "rec_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_rel_project_nn_milestone_rec_milestone_target_id",
                        column: x => x.target_id,
                        principalTable: "rec_milestone",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rel_comment_nn_attachment",
                columns: table => new
                {
                    origin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rel_comment_nn_attachment", x => new { x.origin_id, x.target_id });
                });

            // ---------------------------------------------------------------
            // Indexes
            // ---------------------------------------------------------------

            migrationBuilder.CreateIndex(
                name: "IX_rec_task_parent_id",
                table: "rec_task",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_rec_task_status_id",
                table: "rec_task",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "IX_rec_task_type_id",
                table: "rec_task",
                column: "type_id");

            migrationBuilder.CreateIndex(
                name: "IX_rel_milestone_nn_task_TargetId",
                table: "rel_milestone_nn_task",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_rel_project_nn_milestone_TargetId",
                table: "rel_project_nn_milestone",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_rel_project_nn_task_TargetId",
                table: "rel_project_nn_task",
                column: "target_id");

            // ---------------------------------------------------------------
            // Seed Data — Task Status (5 records)
            // ---------------------------------------------------------------

            migrationBuilder.InsertData(
                table: "rec_task_status",
                columns: new[] { "id", "label", "sort_index", "is_default", "is_enabled", "is_closed", "l_scope", "icon_class", "color" },
                values: new object[,]
                {
                    { new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f"), "Not Started", 1, true, true, false, "", null, null },
                    { new Guid("20d73f63-3501-4565-a55e-2d291549a9bd"), "In Progress", 2, false, true, false, "", null, null },
                    { new Guid("8b2aa2af-17dd-400a-a221-78ee744c4866"), "Blocked", 3, false, true, false, "", "", "" },
                    { new Guid("b1cc69e5-ce09-40e0-8785-b6452b257bdf"), "Completed", 4, false, true, true, "", "", "" },
                    { new Guid("a1e527fd-4472-4b39-a1d4-af4905d2310c"), "Rejected", 5, false, true, true, "", "", "" }
                });

            // ---------------------------------------------------------------
            // Seed Data — Task Type (8 records)
            // ---------------------------------------------------------------

            migrationBuilder.InsertData(
                table: "rec_task_type",
                columns: new[] { "id", "label", "sort_index", "is_default", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[,]
                {
                    { new Guid("da9bf72d-3655-4c51-9f99-047ef9297bf2"), "General", 1, true, true, "", null, null },
                    { new Guid("7b191135-5fbb-4db9-bf24-1a5fc72d8cd5"), "Call", 2, false, true, "", null, null },
                    { new Guid("489b16e1-91b1-4a05-b247-50ed74f7aaaf"), "Email", 3, false, true, "", null, null },
                    { new Guid("894ba1ef-1b31-440c-9b33-f301d047d8fb"), "Meeting", 4, false, true, "", null, null },
                    { new Guid("ddb9c170-706d-4b17-a8ee-78ed3a544fa3"), "Send Quote", 5, false, true, "", null, null },
                    { new Guid("6105dcf4-4115-435f-94bb-0190d45d1b87"), "Improvement", 2, false, true, "[\"projects\"]", "far fa-fw fa-caret-square-up", "#9C27B0" },
                    { new Guid("a0465e9f-5d5f-433d-acf1-1da0eaec78b4"), "New Feature", 1, true, true, "[\"projects\"]", "fas fa-fw fa-plus-square", "#4CAF50" },
                    { new Guid("c0a2554c-f59a-434e-be00-217a416f8efd"), "Bug", 3, false, true, "[\"projects\"]", "fas fa-fw fa-bug", "#F44336" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop tables in reverse dependency order
            migrationBuilder.DropTable(name: "rel_comment_nn_attachment");
            migrationBuilder.DropTable(name: "rel_project_nn_milestone");
            migrationBuilder.DropTable(name: "rel_milestone_nn_task");
            migrationBuilder.DropTable(name: "rel_project_nn_task");
            migrationBuilder.DropTable(name: "rec_feed_item");
            migrationBuilder.DropTable(name: "rec_comment");
            migrationBuilder.DropTable(name: "rec_timelog");
            migrationBuilder.DropTable(name: "rec_task");
            migrationBuilder.DropTable(name: "rec_milestone");
            migrationBuilder.DropTable(name: "rec_project");
            migrationBuilder.DropTable(name: "rec_task_type");
            migrationBuilder.DropTable(name: "rec_task_status");
        }
    }
}
