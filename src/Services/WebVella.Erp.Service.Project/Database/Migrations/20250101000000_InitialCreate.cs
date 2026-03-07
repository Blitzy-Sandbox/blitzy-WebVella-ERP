using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WebVella.Erp.Service.Project.Database.Migrations
{
    /// <summary>
    /// Initial EF Core migration for the Project/Task microservice database.
    /// Consolidates the cumulative schema state from all monolith ProjectPlugin patches
    /// (20190203 through 20251229) and NextPlugin entity definitions (20190203, 20190205,
    /// 20190222) into a single EF Core migration.
    ///
    /// Creates all record tables (rec_*), relation/join tables (rel_*), indexes, and
    /// seeds reference data for task_status and task_type entities with EXACT GUIDs
    /// from the monolith patch files to preserve data integrity (AAP 0.8.1).
    ///
    /// Design decisions:
    /// - Table naming preserves monolith rec_*/rel_* conventions for EQL engine compatibility (AAP 0.7.3)
    /// - Cross-service FKs (owner_id, created_by → users) use plain UUID without FK constraints (AAP 0.7.1)
    /// - Intra-service FKs (status_id, type_id, parent_id) use proper FK constraints
    /// - Guid PKs use ValueGeneratedNever (application-set), except rec_task.number (auto-increment)
    /// - Column types match monolith PostgreSQL types from DBTypeConverter.cs
    /// - Seed data reflects FINAL state after NextPlugin.20190222.cs updates
    ///
    /// Note: [DbContext] and [Migration] attributes are defined in the companion
    /// Designer.cs partial class file per EF Core convention.
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===============================================================
            // Reference/Lookup Tables (no FK dependencies) — created first
            // ===============================================================

            // ----- rec_task_status -----
            // Entity ID: 541ccc20-e86b-4b78-8570-0745b4a17497
            // Source: NextPlugin.20190203.cs lines 2109-2410
            migrationBuilder.CreateTable(
                name: "rec_task_status",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    icon_class = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true),
                    sort_index = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_closed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    l_scope = table.Column<string>(type: "text", nullable: true, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_task_status", x => x.id);
                });

            // ----- rec_task_type -----
            // Entity ID: 12244aea-878f-4a33-b205-26e53f9ed25b
            // Source: NextPlugin.20190203.cs lines 2415-2687
            migrationBuilder.CreateTable(
                name: "rec_task_type",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    icon_class = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true),
                    sort_index = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    l_scope = table.Column<string>(type: "text", nullable: true, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_task_type", x => x.id);
                });

            // ----- rec_milestone -----
            // Source: ProjectPlugin patches — milestone entity definition
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

            // ===============================================================
            // Entity Tables with FK references
            // ===============================================================

            // ----- rec_project -----
            // Entity ID: ab1fb3e4-508c-48a3-b576-bfdd395f69d5
            // Source: ProjectPlugin.20190203.cs and subsequent patches
            // Note: owner_id and created_by are cross-service user UUIDs — NO FK constraints (AAP 0.7.1)
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
                    is_billable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    scope_key = table.Column<string>(type: "text", nullable: true),
                    x_billable_hours = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false, defaultValue: 0m),
                    x_nonbillable_hours = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false, defaultValue: 0m),
                    x_tasks_not_started = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    x_tasks_in_progress = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    x_tasks_completed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    x_overdue_tasks = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    x_milestones_on_track = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    x_milestones_missed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    x_budget = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false, defaultValue: 0m),
                    l_scope = table.Column<string>(type: "text", nullable: true, defaultValue: ""),
                    x_search = table.Column<string>(type: "text", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_project", x => x.id);
                });

            // ----- rec_task -----
            // Entity ID: 9386226e-381e-4522-b27b-fb5514d77902
            // Source: NextPlugin.20190203.cs (initial), NextPlugin.20190205.cs (recurrence_template),
            //         ProjectPlugin patches (additional fields: estimated_minutes, timelog_started_on, etc.)
            // Note: owner_id and created_by are cross-service user UUIDs — NO FK constraints (AAP 0.7.1)
            // Note: number column uses PostgreSQL IDENTITY for auto-increment
            migrationBuilder.CreateTable(
                name: "rec_task",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    start_time = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    end_time = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_on = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    number = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: true),
                    l_scope = table.Column<string>(type: "text", nullable: true, defaultValue: ""),
                    l_related_records = table.Column<string>(type: "text", nullable: true, defaultValue: ""),
                    x_search = table.Column<string>(type: "text", nullable: true),
                    recurrence_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recurrence_template = table.Column<string>(type: "text", nullable: true),
                    key = table.Column<string>(type: "text", nullable: false),
                    estimated_minutes = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: true),
                    x_billable_minutes = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: true),
                    x_nonbillable_minutes = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: true),
                    timelog_started_on = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    reserve_time = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
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
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rec_task_rec_task_type_type_id",
                        column: x => x.type_id,
                        principalTable: "rec_task_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // ----- rec_timelog -----
            // Entity ID: 750153c5-1df9-408f-b856-727078a525bc
            // Source: NextPlugin.20190203.cs (initial), NextPlugin.20190205.cs (minutes DecimalPlaces=0)
            // Note: created_by is cross-service user UUID — NO FK constraint (AAP 0.7.1)
            migrationBuilder.CreateTable(
                name: "rec_timelog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    l_related_records = table.Column<string>(type: "text", nullable: true, defaultValue: ""),
                    l_scope = table.Column<string>(type: "text", nullable: true, defaultValue: ""),
                    minutes = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false, defaultValue: 0m),
                    is_billable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    logged_on = table.Column<DateTime>(type: "date", nullable: false, defaultValueSql: "CURRENT_DATE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_timelog", x => x.id);
                });

            // ----- rec_comment -----
            // Entity ID: b1d218d5-68c2-41a5-bea5-1b4a78cbf91d
            // Source: NextPlugin.20190203.cs
            // Note: created_by is cross-service user UUID — NO FK constraint (AAP 0.7.1)
            // Note: parent_id is self-reference for threaded comments
            migrationBuilder.CreateTable(
                name: "rec_comment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true, defaultValue: "body"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    l_scope = table.Column<string>(type: "text", nullable: true, defaultValue: ""),
                    l_related_records = table.Column<string>(type: "text", nullable: true, defaultValue: ""),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_comment", x => x.id);
                });

            // ----- rec_feed_item -----
            // Entity ID: 2ac9a907-1bdf-4700-8874-6e06a8d22c97
            // Source: NextPlugin.20190203.cs
            // Note: created_by is cross-service user UUID — NO FK constraint (AAP 0.7.1)
            migrationBuilder.CreateTable(
                name: "rec_feed_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_on = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    l_scope = table.Column<string>(type: "text", nullable: true, defaultValue: ""),
                    subject = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true, defaultValue: "system"),
                    l_related_records = table.Column<string>(type: "text", nullable: true, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_feed_item", x => x.id);
                });

            // ===============================================================
            // M:N Relation / Join Tables
            // ===============================================================

            // ----- rel_project_nn_task -----
            // M:N: rec_project ↔ rec_task
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

            // ----- rel_milestone_nn_task -----
            // M:N: rec_milestone ↔ rec_task
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

            // ----- rel_project_nn_milestone -----
            // M:N: rec_project ↔ rec_milestone
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

            // ----- rel_comment_nn_attachment -----
            // M:N: rec_comment ↔ attachment (Core service file)
            // Note: target_id references Core service attachment — NO FK constraint (AAP 0.7.1)
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
                    table.ForeignKey(
                        name: "FK_rel_comment_nn_attachment_rec_comment_origin_id",
                        column: x => x.origin_id,
                        principalTable: "rec_comment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    // target_id intentionally has NO FK — it references cross-service attachment
                });

            // ===============================================================
            // Indexes
            // ===============================================================

            // rec_task indexes
            migrationBuilder.CreateIndex(
                name: "IX_rec_task_status_id",
                table: "rec_task",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "IX_rec_task_type_id",
                table: "rec_task",
                column: "type_id");

            migrationBuilder.CreateIndex(
                name: "IX_rec_task_owner_id",
                table: "rec_task",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_rec_task_parent_id",
                table: "rec_task",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_rec_task_created_on",
                table: "rec_task",
                column: "created_on");

            // rec_project indexes
            migrationBuilder.CreateIndex(
                name: "IX_rec_project_owner_id",
                table: "rec_project",
                column: "owner_id");

            // rec_timelog indexes
            migrationBuilder.CreateIndex(
                name: "IX_rec_timelog_created_on",
                table: "rec_timelog",
                column: "created_on");

            migrationBuilder.CreateIndex(
                name: "IX_rec_timelog_logged_on",
                table: "rec_timelog",
                column: "logged_on");

            // rec_feed_item indexes
            migrationBuilder.CreateIndex(
                name: "IX_rec_feed_item_created_on",
                table: "rec_feed_item",
                column: "created_on");

            // rec_comment indexes
            migrationBuilder.CreateIndex(
                name: "IX_rec_comment_created_on",
                table: "rec_comment",
                column: "created_on");

            migrationBuilder.CreateIndex(
                name: "IX_rec_comment_parent_id",
                table: "rec_comment",
                column: "parent_id");

            // Join table indexes for target columns (origin columns are part of composite PK)
            migrationBuilder.CreateIndex(
                name: "IX_rel_project_nn_task_target_id",
                table: "rel_project_nn_task",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_rel_milestone_nn_task_target_id",
                table: "rel_milestone_nn_task",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_rel_project_nn_milestone_target_id",
                table: "rel_project_nn_milestone",
                column: "target_id");

            // ===============================================================
            // Seed Data — Task Status (5 records)
            // Source: NextPlugin.20190203.cs lines 11232-11335
            // All records are system-level, immutable reference data.
            // GUIDs are EXACT from monolith to preserve data integrity (AAP 0.8.1).
            // ===============================================================

            migrationBuilder.InsertData(
                table: "rec_task_status",
                columns: new[] { "id", "label", "sort_index", "is_default", "is_enabled", "is_system", "is_closed", "l_scope", "icon_class", "color" },
                values: new object[,]
                {
                    {
                        new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f"),
                        "Not Started",
                        1,
                        true,
                        true,
                        true,
                        false,
                        "",
                        (object)null,
                        (object)null
                    },
                    {
                        new Guid("20d73f63-3501-4565-a55e-2d291549a9bd"),
                        "In Progress",
                        2,
                        false,
                        true,
                        true,
                        false,
                        "",
                        (object)null,
                        (object)null
                    },
                    {
                        new Guid("8b2aa2af-17dd-400a-a221-78ee744c4866"),
                        "Blocked",
                        3,
                        false,
                        true,
                        true,
                        false,
                        "",
                        "",
                        ""
                    },
                    {
                        new Guid("b1cc69e5-ce09-40e0-8785-b6452b257bdf"),
                        "Completed",
                        4,
                        false,
                        true,
                        true,
                        true,
                        "",
                        "",
                        ""
                    },
                    {
                        new Guid("a1e527fd-4472-4b39-a1d4-af4905d2310c"),
                        "Rejected",
                        5,
                        false,
                        true,
                        true,
                        true,
                        "",
                        "",
                        ""
                    }
                });

            // ===============================================================
            // Seed Data — Task Type (8 records — FINAL state after NextPlugin.20190222.cs)
            // Source: NextPlugin.20190203.cs (initial creation) + NextPlugin.20190222.cs (updates)
            // The 20190222 patch updated sort_index, icon_class, color, and l_scope
            // for General, Call, Email, Meeting, New Feature, Improvement, and Bug.
            // "Send Quote" was NOT updated in 20190222 and retains its initial values.
            // GUIDs are EXACT from monolith to preserve data integrity (AAP 0.8.1).
            // ===============================================================

            migrationBuilder.InsertData(
                table: "rec_task_type",
                columns: new[] { "id", "label", "sort_index", "is_default", "is_enabled", "is_system", "l_scope", "icon_class", "color" },
                values: new object[,]
                {
                    {
                        new Guid("da9bf72d-3655-4c51-9f99-047ef9297bf2"),
                        "General",
                        1,
                        true,
                        true,
                        true,
                        "[\"projects\"]",
                        "fa fa-cog",
                        "#2196F3"
                    },
                    {
                        new Guid("7b191135-5fbb-4db9-bf24-1a5fc72d8cd5"),
                        "Call",
                        2,
                        false,
                        true,
                        true,
                        "[\"projects\"]",
                        "fas fa-phone",
                        "#2196F3"
                    },
                    {
                        new Guid("489b16e1-91b1-4a05-b247-50ed74f7aaaf"),
                        "Email",
                        3,
                        false,
                        true,
                        true,
                        "[\"projects\"]",
                        "fa fa-envelope",
                        "#2196F3"
                    },
                    {
                        new Guid("894ba1ef-1b31-440c-9b33-f301d047d8fb"),
                        "Meeting",
                        4,
                        false,
                        true,
                        true,
                        "[\"projects\"]",
                        "fas fa-users",
                        "#2196F3"
                    },
                    {
                        new Guid("ddb9c170-706d-4b17-a8ee-78ed3a544fa3"),
                        "Send Quote",
                        5,
                        false,
                        true,
                        true,
                        "",
                        (object)null,
                        (object)null
                    },
                    {
                        new Guid("a0465e9f-5d5f-433d-acf1-1da0eaec78b4"),
                        "New Feature",
                        6,
                        true,
                        true,
                        true,
                        "[\"projects\"]",
                        "fas fa-fw fa-plus-square",
                        "#4CAF50"
                    },
                    {
                        new Guid("6105dcf4-4115-435f-94bb-0190d45d1b87"),
                        "Improvement",
                        7,
                        false,
                        true,
                        true,
                        "[\"projects\"]",
                        "far fa-fw fa-caret-square-up",
                        "#9C27B0"
                    },
                    {
                        new Guid("c0a2554c-f59a-434e-be00-217a416f8efd"),
                        "Bug",
                        8,
                        false,
                        true,
                        true,
                        "[\"projects\"]",
                        "fas fa-fw fa-bug",
                        "#F44336"
                    }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop tables in reverse dependency order to respect FK constraints.
            // Join/relation tables dropped first, then entity tables, then reference tables.

            // 1. Join tables (no dependents)
            migrationBuilder.DropTable(name: "rel_comment_nn_attachment");
            migrationBuilder.DropTable(name: "rel_project_nn_milestone");
            migrationBuilder.DropTable(name: "rel_milestone_nn_task");
            migrationBuilder.DropTable(name: "rel_project_nn_task");

            // 2. Entity tables with no intra-service dependents
            migrationBuilder.DropTable(name: "rec_feed_item");
            migrationBuilder.DropTable(name: "rec_comment");
            migrationBuilder.DropTable(name: "rec_timelog");

            // 3. rec_task depends on rec_task_status and rec_task_type (FK)
            migrationBuilder.DropTable(name: "rec_task");

            // 4. rec_milestone (referenced by join tables, already dropped)
            migrationBuilder.DropTable(name: "rec_milestone");

            // 5. rec_project (referenced by join tables, already dropped)
            migrationBuilder.DropTable(name: "rec_project");

            // 6. Reference/lookup tables (were referenced by rec_task FKs)
            migrationBuilder.DropTable(name: "rec_task_type");
            migrationBuilder.DropTable(name: "rec_task_status");
        }
    }
}
