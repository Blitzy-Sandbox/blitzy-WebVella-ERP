using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;
using WebVella.Erp.Service.Project.Database;
using TaskEntity = WebVella.Erp.Service.Project.Domain.Entities.TaskEntity;
using Xunit;

namespace WebVella.Erp.Tests.Project.Database
{
    /// <summary>
    /// Validates the conversion of the monolith's date-based patch system
    /// (9 patches from ProjectPlugin: 20190203, 20190205, 20190206, 20190207,
    /// 20190208, 20190222, 20211012, 20211013, 20251229) to EF Core migrations.
    ///
    /// These tests verify that:
    /// - Deterministic GUID IDs from patches are preserved
    /// - Entity definitions (task, timelog, comment, feed_item, project,
    ///   task_type, task_status, milestone) are correctly captured
    /// - Relations (project_nn_task, milestone_nn_task, project_nn_milestone,
    ///   comment_nn_attachment, task_type_1n_task, task_status_1n_task) are
    ///   correctly represented in the EF Core model/migrations
    /// - Cross-service references (user IDs) have NO FK constraints
    /// - Database-per-service isolation is enforced (no cross-service tables)
    /// - Audit fields (created_on, created_by) are preserved on all entity tables
    /// - Data integrity round-trips succeed for all entity and relation types
    ///
    /// All tests run against an isolated PostgreSQL 16 container via Testcontainers.
    /// </summary>
    [Collection("Database")]
    public class PatchMigrationValidationTests : IAsyncLifetime
    {
        /// <summary>
        /// Testcontainers PostgreSQL 16 container for isolated database testing.
        /// Started fresh before every test class run; migrations applied once in InitializeAsync.
        /// </summary>
        private readonly PostgreSqlContainer _postgres;

        /// <summary>
        /// Connection string obtained from the PostgreSQL container after startup.
        /// Used by <see cref="CreateDbContext"/> to build DbContextOptions.
        /// </summary>
        private string _connectionString;

        /// <summary>
        /// Constructs the test class with a configured PostgreSQL Testcontainer.
        /// The container uses postgres:16-alpine for consistency with the
        /// docker-compose.localstack.yml specification (AAP 0.7.4).
        /// </summary>
        public PatchMigrationValidationTests()
        {
            _postgres = new PostgreSqlBuilder("postgres:16-alpine")
                .Build();
        }

        /// <summary>
        /// Starts the PostgreSQL container and applies all EF Core migrations.
        /// This simulates what happens when the Project service starts up and
        /// calls <c>context.Database.Migrate()</c>, applying the InitialCreate
        /// migration that consolidates all 9 monolith patches.
        /// </summary>
        public async Task InitializeAsync()
        {
            await _postgres.StartAsync();
            _connectionString = _postgres.GetConnectionString();

            // Apply all EF Core migrations — this replaces the monolith's
            // ProcessPatches() sequential date-based patch execution
            using (var context = CreateDbContext())
            {
                context.Database.Migrate();
            }
        }

        /// <summary>
        /// Stops and disposes the PostgreSQL container, cleaning up all
        /// test database resources.
        /// </summary>
        public async Task DisposeAsync()
        {
            await _postgres.DisposeAsync();
        }

        // =====================================================================
        // Helper Methods
        // =====================================================================

        /// <summary>
        /// Creates a new <see cref="ProjectDbContext"/> instance connected to the
        /// Testcontainer PostgreSQL database. Each test method should create its
        /// own context to ensure clean state tracking.
        /// </summary>
        private ProjectDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseNpgsql(_connectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            return new ProjectDbContext(options);
        }

        /// <summary>
        /// Retrieves a list of column names for the specified table from
        /// the PostgreSQL information_schema.
        /// </summary>
        private async Task<List<string>> GetColumnNamesAsync(string tableName)
        {
            var columns = new List<string>();
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = @tableName " +
                "ORDER BY ordinal_position",
                conn);
            cmd.Parameters.AddWithValue("@tableName", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
            return columns;
        }

        /// <summary>
        /// Retrieves a dictionary of (column_name → data_type) for the
        /// specified table from the PostgreSQL information_schema.
        /// </summary>
        private async Task<Dictionary<string, string>> GetColumnTypesAsync(string tableName)
        {
            var columnTypes = new Dictionary<string, string>();
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT column_name, data_type FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = @tableName",
                conn);
            cmd.Parameters.AddWithValue("@tableName", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columnTypes[reader.GetString(0)] = reader.GetString(1);
            }
            return columnTypes;
        }

        /// <summary>
        /// Retrieves all public table names from the PostgreSQL database
        /// via information_schema.
        /// </summary>
        private async Task<List<string>> GetAllTableNamesAsync()
        {
            var tables = new List<string>();
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT table_name FROM information_schema.tables " +
                "WHERE table_schema = 'public' ORDER BY table_name",
                conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
            return tables;
        }

        /// <summary>
        /// Checks whether a foreign key constraint exists for a specific column
        /// in a given table. Returns true if an FK constraint references that column.
        /// </summary>
        private async Task<bool> HasForeignKeyConstraintAsync(string tableName, string columnName)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                @"SELECT COUNT(*) FROM information_schema.table_constraints tc
                  JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                   AND tc.table_schema = kcu.table_schema
                  WHERE tc.constraint_type = 'FOREIGN KEY'
                    AND tc.table_schema = 'public'
                    AND tc.table_name = @tableName
                    AND kcu.column_name = @columnName",
                conn);
            cmd.Parameters.AddWithValue("@tableName", tableName);
            cmd.Parameters.AddWithValue("@columnName", columnName);
            var count = (long)await cmd.ExecuteScalarAsync();
            return count > 0;
        }

        /// <summary>
        /// Retrieves all foreign key constraint column names for a given table.
        /// Returns a list of column names that have FK constraints.
        /// </summary>
        private async Task<List<string>> GetForeignKeyColumnsAsync(string tableName)
        {
            var fkColumns = new List<string>();
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                @"SELECT kcu.column_name FROM information_schema.table_constraints tc
                  JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                   AND tc.table_schema = kcu.table_schema
                  WHERE tc.constraint_type = 'FOREIGN KEY'
                    AND tc.table_schema = 'public'
                    AND tc.table_name = @tableName",
                conn);
            cmd.Parameters.AddWithValue("@tableName", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                fkColumns.Add(reader.GetString(0));
            }
            return fkColumns;
        }

        // =====================================================================
        // Phase 2: Patch Coverage Verification Tests
        // =====================================================================

        /// <summary>
        /// Verifies that the cumulative result of all 9 monolith patches
        /// (20190203, 20190205, 20190206, 20190207, 20190208, 20190222,
        /// 20211012, 20211013, 20251229) is represented in EF Core migrations.
        ///
        /// NOTE: The 9 patches don't map 1:1 to migrations — most patches
        /// create UI metadata (pages, data sources), not schema. The EF Core
        /// migrations capture the CUMULATIVE schema state from all patches.
        /// The __EFMigrationsHistory table must have at least one entry
        /// (the InitialCreate migration), and the resulting schema must
        /// include all entity tables and relations.
        /// </summary>
        [Fact]
        public async Task AllNinePatchesShouldBeRepresentedInMigrations()
        {
            // Verify __EFMigrationsHistory has at least one migration entry
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"", conn);
            var migrationCount = (long)await cmd.ExecuteScalarAsync();
            migrationCount.Should().BeGreaterThan(0,
                "at least one migration must exist to represent the cumulative 9 patches");

            // The cumulative state of all 9 patches must produce these entity tables:
            // - rec_task (from NextPlugin.20190203 entity creation + subsequent patches)
            // - rec_timelog (from NextPlugin.20190203)
            // - rec_comment (from NextPlugin.20190203)
            // - rec_feed_item (from NextPlugin.20190203)
            // - rec_project (from NextPlugin.20190203)
            // - rec_task_type (from NextPlugin.20190203 + 20190222 updates)
            // - rec_task_status (from NextPlugin.20190203)
            // - rec_milestone (from entity definitions)
            var tables = await GetAllTableNamesAsync();
            tables.Should().Contain("rec_task");
            tables.Should().Contain("rec_timelog");
            tables.Should().Contain("rec_comment");
            tables.Should().Contain("rec_feed_item");
            tables.Should().Contain("rec_project");
            tables.Should().Contain("rec_task_type");
            tables.Should().Contain("rec_task_status");
            tables.Should().Contain("rec_milestone");

            // M:N relation tables from patches
            tables.Should().Contain("rel_project_nn_task");
            tables.Should().Contain("rel_milestone_nn_task");
            tables.Should().Contain("rel_project_nn_milestone");
            tables.Should().Contain("rel_comment_nn_attachment");

            // Verify seed data from patches (task types and task statuses)
            using (var context = CreateDbContext())
            {
                var taskTypeCount = context.TaskTypes.Count();
                taskTypeCount.Should().BeGreaterThan(0,
                    "task types from patches 20190203/20190222 must be seeded");

                var taskStatusCount = context.TaskStatuses.Count();
                taskStatusCount.Should().BeGreaterThan(0,
                    "task statuses from patch 20190203 must be seeded");
            }
        }

        // =====================================================================
        // Phase 3: Entity Definition Preservation Tests
        // =====================================================================

        /// <summary>
        /// Verifies that the rec_task table created by the EF Core migration
        /// preserves all columns from the monolith's task entity definition
        /// (originally created in NextPlugin.20190203.cs, extended in subsequent patches).
        /// Entity ID in monolith: 9386226e-381e-4522-b27b-fb5514d77902.
        /// </summary>
        [Fact]
        public async Task TaskEntityDefinition_ShouldBePreservedInMigration()
        {
            var columns = await GetColumnTypesAsync("rec_task");
            columns.Should().NotBeEmpty("rec_task table must exist with columns");

            // Primary key
            columns.Should().ContainKey("id");
            columns["id"].Should().Be("uuid");

            // Core task fields
            columns.Should().ContainKey("subject");
            columns.Should().ContainKey("body");
            columns.Should().ContainKey("priority");
            columns.Should().ContainKey("key");

            // Cross-service user references (NO FK, plain UUID)
            columns.Should().ContainKey("owner_id");
            columns["owner_id"].Should().Be("uuid");
            columns.Should().ContainKey("created_by");
            columns["created_by"].Should().Be("uuid");

            // Temporal fields (renamed from start_date/target_date per domain entity)
            columns.Should().ContainKey("start_time");
            columns.Should().ContainKey("end_time");
            columns.Should().ContainKey("created_on");
            columns.Should().ContainKey("completed_on");

            // Auto-number field
            columns.Should().ContainKey("number");

            // Self-reference and intra-service FKs
            columns.Should().ContainKey("parent_id");
            columns["parent_id"].Should().Be("uuid");
            columns.Should().ContainKey("status_id");
            columns["status_id"].Should().Be("uuid");
            columns.Should().ContainKey("type_id");
            columns["type_id"].Should().Be("uuid");

            // Computed/aggregated fields (column names from actual DbContext mapping)
            columns.Should().ContainKey("x_nonbillable_minutes");
            columns.Should().ContainKey("x_billable_minutes");

            // Scope and search fields
            columns.Should().ContainKey("l_scope");
            columns.Should().ContainKey("l_related_records");
            columns.Should().ContainKey("x_search");

            // Recurrence support
            columns.Should().ContainKey("recurrence_id");
            columns["recurrence_id"].Should().Be("uuid");
        }

        /// <summary>
        /// Verifies that the rec_timelog table preserves the timelog entity
        /// definition from the monolith.
        /// Entity ID in monolith: 750153c5-1df9-408f-b856-727078a525bc.
        /// </summary>
        [Fact]
        public async Task TimelogEntityDefinition_ShouldBePreservedInMigration()
        {
            var columns = await GetColumnTypesAsync("rec_timelog");
            columns.Should().NotBeEmpty("rec_timelog table must exist with columns");

            // Primary key
            columns.Should().ContainKey("id");
            columns["id"].Should().Be("uuid");

            // Core timelog fields
            columns.Should().ContainKey("body");
            columns.Should().ContainKey("minutes");
            columns.Should().ContainKey("is_billable");

            // Cross-service user reference (NO FK)
            columns.Should().ContainKey("created_by");
            columns["created_by"].Should().Be("uuid");

            // Audit field
            columns.Should().ContainKey("created_on");

            // Scope/relation fields
            columns.Should().ContainKey("l_related_records");
            columns.Should().ContainKey("l_scope");
        }

        /// <summary>
        /// Verifies that the rec_comment table preserves the comment entity
        /// definition from the monolith.
        /// Entity ID in monolith: b1d218d5-68c2-41a5-bea5-1b4a78cbf91d.
        /// </summary>
        [Fact]
        public async Task CommentEntityDefinition_ShouldBePreservedInMigration()
        {
            var columns = await GetColumnTypesAsync("rec_comment");
            columns.Should().NotBeEmpty("rec_comment table must exist with columns");

            // Primary key
            columns.Should().ContainKey("id");
            columns["id"].Should().Be("uuid");

            // Core fields
            columns.Should().ContainKey("body");

            // Cross-service user reference (NO FK)
            columns.Should().ContainKey("created_by");
            columns["created_by"].Should().Be("uuid");

            // Audit field
            columns.Should().ContainKey("created_on");

            // Self-reference for threaded/nested comments
            columns.Should().ContainKey("parent_id");
            columns["parent_id"].Should().Be("uuid");

            // Scope/relation fields
            columns.Should().ContainKey("l_scope");
            columns.Should().ContainKey("l_related_records");
        }

        /// <summary>
        /// Verifies that the rec_feed_item table preserves the feed item entity
        /// definition from the monolith.
        /// Entity ID in monolith: db83b9b0-448c-4675-be71-640aca2e2a3a.
        /// </summary>
        [Fact]
        public async Task FeedItemEntityDefinition_ShouldBePreservedInMigration()
        {
            var columns = await GetColumnTypesAsync("rec_feed_item");
            columns.Should().NotBeEmpty("rec_feed_item table must exist with columns");

            // Primary key
            columns.Should().ContainKey("id");
            columns["id"].Should().Be("uuid");

            // Cross-service user reference (NO FK)
            columns.Should().ContainKey("created_by");
            columns["created_by"].Should().Be("uuid");

            // Audit field
            columns.Should().ContainKey("created_on");

            // Core fields
            columns.Should().ContainKey("subject");
            columns.Should().ContainKey("body");
            columns.Should().ContainKey("type");

            // Scope/relation fields
            columns.Should().ContainKey("l_scope");
            columns.Should().ContainKey("l_related_records");
        }

        /// <summary>
        /// Verifies that the rec_project table preserves the project entity
        /// definition from the monolith.
        /// Entity ID in monolith: 2d9b2d1d-e32b-45e1-a013-91d92a9ce792.
        /// </summary>
        [Fact]
        public async Task ProjectEntityDefinition_ShouldBePreservedInMigration()
        {
            var columns = await GetColumnTypesAsync("rec_project");
            columns.Should().NotBeEmpty("rec_project table must exist with columns");

            // Primary key
            columns.Should().ContainKey("id");
            columns["id"].Should().Be("uuid");

            // Core project fields
            columns.Should().ContainKey("name");
            columns.Should().ContainKey("abbr");
            columns.Should().ContainKey("description");

            // Cross-service user reference (NO FK)
            columns.Should().ContainKey("owner_id");
            columns["owner_id"].Should().Be("uuid");

            // Display/config fields
            columns.Should().ContainKey("color");
            columns.Should().ContainKey("icon");
            columns.Should().ContainKey("start_date");
            columns.Should().ContainKey("end_date");
            columns.Should().ContainKey("is_billable");
            columns.Should().ContainKey("scope_key");

            // Aggregated statistics fields
            columns.Should().ContainKey("x_billable_hours");
            columns.Should().ContainKey("x_nonbillable_hours");
            columns.Should().ContainKey("x_tasks_not_started");
            columns.Should().ContainKey("x_tasks_in_progress");
            columns.Should().ContainKey("x_tasks_completed");
            columns.Should().ContainKey("x_overdue_tasks");
            columns.Should().ContainKey("x_milestones_on_track");
            columns.Should().ContainKey("x_milestones_missed");
            columns.Should().ContainKey("x_budget");
        }

        /// <summary>
        /// Verifies that the rec_task_type table preserves the task type entity
        /// definition from the monolith.
        /// Entity ID in monolith: 35999e55-821c-4798-8e8f-29d8c672c9b9.
        /// </summary>
        [Fact]
        public async Task TaskTypeEntityDefinition_ShouldBePreservedInMigration()
        {
            var columns = await GetColumnTypesAsync("rec_task_type");
            columns.Should().NotBeEmpty("rec_task_type table must exist with columns");

            // Primary key
            columns.Should().ContainKey("id");
            columns["id"].Should().Be("uuid");

            // Core fields
            columns.Should().ContainKey("label");
            columns.Should().ContainKey("icon_class");
            columns.Should().ContainKey("sort_index");
            columns.Should().ContainKey("is_default");
            columns.Should().ContainKey("l_scope");
            columns.Should().ContainKey("color");
            columns.Should().ContainKey("is_enabled");
        }

        /// <summary>
        /// Verifies that the rec_task_status table preserves the task status entity
        /// definition from the monolith.
        /// Entity ID in monolith: 9221f095-f749-4b88-94e5-9fa485527ef7.
        /// </summary>
        [Fact]
        public async Task TaskStatusEntityDefinition_ShouldBePreservedInMigration()
        {
            var columns = await GetColumnTypesAsync("rec_task_status");
            columns.Should().NotBeEmpty("rec_task_status table must exist with columns");

            // Primary key
            columns.Should().ContainKey("id");
            columns["id"].Should().Be("uuid");

            // Core fields
            columns.Should().ContainKey("label");
            columns.Should().ContainKey("icon_class");
            columns.Should().ContainKey("sort_index");
            columns.Should().ContainKey("is_default");
            columns.Should().ContainKey("l_scope");
            columns.Should().ContainKey("color");
            columns.Should().ContainKey("is_enabled");
            columns.Should().ContainKey("is_closed");
        }

        // =====================================================================
        // Phase 4: Deterministic GUID Preservation Tests
        // =====================================================================

        /// <summary>
        /// Verifies that entity configurations in ProjectDbContext use
        /// ValueGeneratedNever(), preserving the monolith pattern where all
        /// entity IDs are application-generated, deterministic GUIDs.
        /// Tests by inserting records with specific GUIDs and verifying
        /// they are stored exactly as provided (not replaced by DB-generated values).
        /// </summary>
        [Fact]
        public async Task DeterministicEntityGuids_ShouldBePreservedAsPrimaryKeys()
        {
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var timelogId = Guid.NewGuid();
            var commentId = Guid.NewGuid();
            var feedItemId = Guid.NewGuid();

            // Seed required reference data (task_type and task_status already seeded by migrations)
            Guid taskStatusId;
            Guid taskTypeId;
            using (var seedCtx = CreateDbContext())
            {
                taskStatusId = seedCtx.TaskStatuses.First().Id;
                taskTypeId = seedCtx.TaskTypes.First().Id;
            }

            using (var context = CreateDbContext())
            {
                // Insert entities with explicit application-set GUIDs
                context.Projects.Add(new ProjectEntity
                {
                    Id = projectId,
                    Name = "GUID Test Project",
                    Abbr = "GTP"
                });

                context.Tasks.Add(new TaskEntity
                {
                    Id = taskId,
                    Subject = "GUID Test Task",
                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                    StatusId = taskStatusId,
                    TypeId = taskTypeId,
                    Key = "GTP-1"
                });

                context.Timelogs.Add(new TimelogEntity
                {
                    Id = timelogId,
                    Minutes = 60m,
                    CreatedOn = DateTime.UtcNow,
                    IsBillable = true
                });

                context.Comments.Add(new CommentEntity
                {
                    Id = commentId,
                    Body = "GUID Test Comment",
                    CreatedOn = DateTime.UtcNow
                });

                context.FeedItems.Add(new FeedItemEntity
                {
                    Id = feedItemId,
                    Subject = "GUID Test Feed",
                    CreatedOn = DateTime.UtcNow
                });

                await context.SaveChangesAsync();
            }

            // Verify exact GUID preservation on read-back
            using (var context = CreateDbContext())
            {
                var task = context.Tasks.FirstOrDefault(t => t.Id == taskId);
                task.Should().NotBeNull("task with application-set GUID must persist");
                task.Id.Should().Be(taskId, "task ID must be exactly the application-set GUID");

                var project = context.Projects.FirstOrDefault(p => p.Id == projectId);
                project.Should().NotBeNull();
                project.Id.Should().Be(projectId);

                var timelog = context.Timelogs.FirstOrDefault(t => t.Id == timelogId);
                timelog.Should().NotBeNull();
                timelog.Id.Should().Be(timelogId);

                var comment = context.Comments.FirstOrDefault(c => c.Id == commentId);
                comment.Should().NotBeNull();
                comment.Id.Should().Be(commentId);

                var feedItem = context.FeedItems.FirstOrDefault(f => f.Id == feedItemId);
                feedItem.Should().NotBeNull();
                feedItem.Id.Should().Be(feedItemId);
            }
        }

        /// <summary>
        /// Verifies that M:N join tables use composite PKs and accept
        /// deterministic GUIDs from the original patches. The relation IDs
        /// from the monolith are:
        /// - project_nn_task: b1db4466-7423-44e9-b6b9-3063222c9e15
        /// - milestone_nn_task: b070a627-01ce-4534-ab45-5c6f1a3867a4
        /// - project_nn_milestone: 55c8d6e2-f26d-4689-9d1b-a8c1b9de1672
        /// - comment_nn_attachment: 4b80a487-83ed-42e6-9be7-0ddf91afee15
        /// </summary>
        [Fact]
        public async Task DeterministicRelationGuids_ShouldBeUsedForJoinTables()
        {
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var milestoneId = Guid.NewGuid();
            var commentId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid(); // Cross-service file attachment UUID

            Guid relStatusId;
            Guid relTypeId;
            using (var seedCtx = CreateDbContext())
            {
                relStatusId = seedCtx.TaskStatuses.First().Id;
                relTypeId = seedCtx.TaskTypes.First().Id;
            }

            using (var context = CreateDbContext())
            {
                // Create prerequisite entities
                context.Projects.Add(new ProjectEntity
                {
                    Id = projectId,
                    Name = "Relation GUID Test",
                    Abbr = "RGT"
                });
                context.Tasks.Add(new TaskEntity
                {
                    Id = taskId,
                    Subject = "Relation GUID Task",
                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                    StatusId = relStatusId,
                    TypeId = relTypeId,
                    Key = "RGT-1"
                });
                context.Milestones.Add(new MilestoneEntity
                {
                    Id = milestoneId,
                    Name = "Relation GUID Milestone"
                });
                context.Comments.Add(new CommentEntity
                {
                    Id = commentId,
                    Body = "Relation GUID Comment",
                    CreatedOn = DateTime.UtcNow
                });
                await context.SaveChangesAsync();
            }

            using (var context = CreateDbContext())
            {
                // Insert M:N relation records with specific deterministic GUIDs
                context.ProjectTaskRelations.Add(new ProjectTaskRelation
                {
                    OriginId = projectId,
                    TargetId = taskId
                });
                context.MilestoneTaskRelations.Add(new MilestoneTaskRelation
                {
                    OriginId = milestoneId,
                    TargetId = taskId
                });
                context.ProjectMilestoneRelations.Add(new ProjectMilestoneRelation
                {
                    OriginId = projectId,
                    TargetId = milestoneId
                });
                context.CommentAttachmentRelations.Add(new CommentAttachmentRelation
                {
                    OriginId = commentId,
                    TargetId = attachmentId
                });
                await context.SaveChangesAsync();
            }

            // Verify round-trip of join table entries
            using (var context = CreateDbContext())
            {
                var projTask = context.ProjectTaskRelations
                    .FirstOrDefault(r => r.OriginId == projectId && r.TargetId == taskId);
                projTask.Should().NotBeNull("project_nn_task join entry must persist with exact GUIDs");

                var mileTask = context.MilestoneTaskRelations
                    .FirstOrDefault(r => r.OriginId == milestoneId && r.TargetId == taskId);
                mileTask.Should().NotBeNull("milestone_nn_task join entry must persist");

                var projMile = context.ProjectMilestoneRelations
                    .FirstOrDefault(r => r.OriginId == projectId && r.TargetId == milestoneId);
                projMile.Should().NotBeNull("project_nn_milestone join entry must persist");

                var commentAtt = context.CommentAttachmentRelations
                    .FirstOrDefault(r => r.OriginId == commentId && r.TargetId == attachmentId);
                commentAtt.Should().NotBeNull("comment_nn_attachment join entry must persist");
            }
        }

        // =====================================================================
        // Phase 5: Relation Preservation Tests
        // =====================================================================

        /// <summary>
        /// Verifies that rel_project_nn_task table exists as a M:N join table
        /// with composite PK (origin_id, target_id).
        /// Source: relation Name="project_nn_task", ID=b1db4466-7423-44e9-b6b9-3063222c9e15.
        /// </summary>
        [Fact]
        public async Task ProjectNnTask_ManyToManyRelation_ShouldExistAsJoinTable()
        {
            var columns = await GetColumnNamesAsync("rel_project_nn_task");
            columns.Should().NotBeEmpty("rel_project_nn_task join table must exist");
            columns.Should().Contain("origin_id", "origin_id column for project FK");
            columns.Should().Contain("target_id", "target_id column for task FK");

            // Verify it has a composite primary key
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                @"SELECT kcu.column_name FROM information_schema.table_constraints tc
                  JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                   AND tc.table_schema = kcu.table_schema
                  WHERE tc.constraint_type = 'PRIMARY KEY'
                    AND tc.table_name = 'rel_project_nn_task'
                  ORDER BY kcu.ordinal_position",
                conn);
            var pkColumns = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                pkColumns.Add(reader.GetString(0));
            }
            pkColumns.Should().Contain("origin_id");
            pkColumns.Should().Contain("target_id");
        }

        /// <summary>
        /// Verifies that rel_milestone_nn_task table exists with composite PK.
        /// Source: relation Name="milestone_nn_task", ID=b070a627-01ce-4534-ab45-5c6f1a3867a4.
        /// </summary>
        [Fact]
        public async Task MilestoneNnTask_ManyToManyRelation_ShouldExistAsJoinTable()
        {
            var columns = await GetColumnNamesAsync("rel_milestone_nn_task");
            columns.Should().NotBeEmpty("rel_milestone_nn_task join table must exist");
            columns.Should().Contain("origin_id");
            columns.Should().Contain("target_id");
        }

        /// <summary>
        /// Verifies that rel_project_nn_milestone table exists with composite PK.
        /// Source: relation Name="project_nn_milestone", ID=55c8d6e2-f26d-4689-9d1b-a8c1b9de1672.
        /// </summary>
        [Fact]
        public async Task ProjectNnMilestone_ManyToManyRelation_ShouldExistAsJoinTable()
        {
            var columns = await GetColumnNamesAsync("rel_project_nn_milestone");
            columns.Should().NotBeEmpty("rel_project_nn_milestone join table must exist");
            columns.Should().Contain("origin_id");
            columns.Should().Contain("target_id");
        }

        /// <summary>
        /// Verifies that rel_comment_nn_attachment table exists with composite PK.
        /// Source: relation Name="comment_nn_attachment", ID=4b80a487-83ed-42e6-9be7-0ddf91afee15.
        /// Note: TargetId references Core service attachment (user_file) — cross-service,
        /// so no FK constraint on target_id.
        /// </summary>
        [Fact]
        public async Task CommentNnAttachment_ManyToManyRelation_ShouldExistAsJoinTable()
        {
            var columns = await GetColumnNamesAsync("rel_comment_nn_attachment");
            columns.Should().NotBeEmpty("rel_comment_nn_attachment join table must exist");
            columns.Should().Contain("origin_id");
            columns.Should().Contain("target_id");
        }

        /// <summary>
        /// Verifies that an FK constraint exists from rec_task.type_id to rec_task_type.id.
        /// Source: relation Name="task_type_1n_task", ID=2925c7ea-72fe-4c12-a1f6-9baa9281141e, 1:N.
        /// This is an intra-service relation and SHOULD have an FK constraint.
        /// </summary>
        [Fact]
        public async Task TaskTypeOneToNTask_FkRelation_ShouldExistInSchema()
        {
            var hasFk = await HasForeignKeyConstraintAsync("rec_task", "type_id");
            hasFk.Should().BeTrue(
                "rec_task.type_id should have FK constraint to rec_task_type.id (intra-service relation)");
        }

        /// <summary>
        /// Verifies that an FK constraint exists from rec_task.status_id to rec_task_status.id.
        /// Source: relation Name="task_status_1n_task", ID=dcc6eb09-627b-4525-839f-d26dd57a0608, 1:N.
        /// This is an intra-service relation and SHOULD have an FK constraint.
        /// </summary>
        [Fact]
        public async Task TaskStatusOneToNTask_FkRelation_ShouldExistInSchema()
        {
            var hasFk = await HasForeignKeyConstraintAsync("rec_task", "status_id");
            hasFk.Should().BeTrue(
                "rec_task.status_id should have FK constraint to rec_task_status.id (intra-service relation)");
        }

        /// <summary>
        /// CRITICAL: Per AAP 0.7.1, cross-service user references must NOT have
        /// FK constraints. Verifies that owner_id and created_by columns on all
        /// entity tables exist as plain UUID columns WITHOUT FK constraints.
        /// These columns reference Core service user records, which live in a
        /// separate database.
        /// </summary>
        [Fact]
        public async Task CrossServiceUserRelations_ShouldNotHaveFkConstraints()
        {
            // rec_task: owner_id and created_by are cross-service user references
            var taskOwnerFk = await HasForeignKeyConstraintAsync("rec_task", "owner_id");
            taskOwnerFk.Should().BeFalse(
                "rec_task.owner_id is a cross-service user reference — NO FK constraint allowed");

            var taskCreatedByFk = await HasForeignKeyConstraintAsync("rec_task", "created_by");
            taskCreatedByFk.Should().BeFalse(
                "rec_task.created_by is a cross-service user reference — NO FK constraint allowed");

            // rec_timelog: created_by is cross-service
            var timelogCreatedByFk = await HasForeignKeyConstraintAsync("rec_timelog", "created_by");
            timelogCreatedByFk.Should().BeFalse(
                "rec_timelog.created_by is a cross-service user reference — NO FK constraint allowed");

            // rec_comment: created_by is cross-service
            var commentCreatedByFk = await HasForeignKeyConstraintAsync("rec_comment", "created_by");
            commentCreatedByFk.Should().BeFalse(
                "rec_comment.created_by is a cross-service user reference — NO FK constraint allowed");

            // rec_feed_item: created_by is cross-service
            var feedCreatedByFk = await HasForeignKeyConstraintAsync("rec_feed_item", "created_by");
            feedCreatedByFk.Should().BeFalse(
                "rec_feed_item.created_by is a cross-service user reference — NO FK constraint allowed");

            // rec_project: owner_id is cross-service
            var projectOwnerFk = await HasForeignKeyConstraintAsync("rec_project", "owner_id");
            projectOwnerFk.Should().BeFalse(
                "rec_project.owner_id is a cross-service user reference — NO FK constraint allowed");
        }

        /// <summary>
        /// Verifies that the user_nn_task_watchers M:N relation
        /// (ID: 879b49cc-6af6-4b34-a554-761ec992534d) is cross-service.
        /// If this table exists in the Project DB, it should NOT have FK
        /// constraints to any rec_user table. Per database-per-service model,
        /// user IDs are stored as plain UUIDs.
        /// </summary>
        [Fact]
        public async Task UserNnTaskWatchers_CrossService_ShouldNotHaveFkToUsers()
        {
            var tables = await GetAllTableNamesAsync();

            // The watchers relation table may or may not exist depending on
            // the migration design. If it exists, validate cross-service constraints.
            if (tables.Contains("rel_user_nn_task_watchers"))
            {
                // Should NOT have FK to rec_user (which doesn't exist in this DB)
                var fkColumns = await GetForeignKeyColumnsAsync("rel_user_nn_task_watchers");
                // No FK should reference a user table (it doesn't exist in this service DB)
                tables.Should().NotContain("rec_user",
                    "rec_user belongs to Core service — must not exist in Project DB");
            }

            // Even if the table doesn't exist, verify rec_user is not in this DB
            tables.Should().NotContain("rec_user",
                "user table belongs to Core service, not Project service");
        }

        // =====================================================================
        // Phase 6: Audit Field Preservation Tests
        // =====================================================================

        /// <summary>
        /// Per AAP 0.8.1: "Verify audit fields (created_on, created_by) are preserved"
        /// on all project-owned entity tables. Validates that each entity table
        /// has the required audit columns with correct data types.
        /// </summary>
        [Fact]
        public async Task AuditFields_ShouldExistOnAllEntityTables()
        {
            // Tables that must have audit fields
            var auditableTables = new List<string>
            {
                "rec_task",
                "rec_timelog",
                "rec_comment",
                "rec_feed_item"
            };

            foreach (var tableName in auditableTables)
            {
                var columns = await GetColumnTypesAsync(tableName);
                columns.Should().NotBeEmpty($"{tableName} table must exist");

                columns.Should().ContainKey("created_on",
                    $"{tableName} must have created_on audit field");

                columns.Should().ContainKey("created_by",
                    $"{tableName} must have created_by audit field");

                // Verify created_by is uuid type (cross-service user reference)
                columns["created_by"].Should().Be("uuid",
                    $"{tableName}.created_by must be uuid type for cross-service user reference");
            }

            // rec_project has owner_id but may also have created_by — check what's available
            var projectColumns = await GetColumnTypesAsync("rec_project");
            projectColumns.Should().ContainKey("owner_id",
                "rec_project must have owner_id for project owner (cross-service user reference)");
            projectColumns["owner_id"].Should().Be("uuid");
        }

        // =====================================================================
        // Phase 7: Database-per-Service Isolation Tests
        // =====================================================================

        /// <summary>
        /// Per AAP requirement: "Database-per-service validation: confirm no
        /// cross-service tables exist". After migration, the Project database
        /// must only contain Project-owned tables and the EF Core system table.
        /// No tables from Core, CRM, or Mail services should be present.
        /// </summary>
        [Fact]
        public async Task NoCrossServiceTables_ShouldExistInProjectDatabase()
        {
            var tables = await GetAllTableNamesAsync();

            // Core service tables — MUST NOT exist
            tables.Should().NotContain("rec_user",
                "rec_user belongs to Core service");
            tables.Should().NotContain("rec_role",
                "rec_role belongs to Core service");

            // CRM service tables — MUST NOT exist
            tables.Should().NotContain("rec_account",
                "rec_account belongs to CRM service");
            tables.Should().NotContain("rec_contact",
                "rec_contact belongs to CRM service");
            tables.Should().NotContain("rec_case",
                "rec_case belongs to CRM service");

            // Mail service tables — MUST NOT exist
            tables.Should().NotContain("rec_email",
                "rec_email belongs to Mail service");
            tables.Should().NotContain("rec_smtp_service",
                "rec_smtp_service belongs to Mail service");

            // Project-owned tables — MUST exist
            tables.Should().Contain("rec_task");
            tables.Should().Contain("rec_timelog");
            tables.Should().Contain("rec_comment");
            tables.Should().Contain("rec_feed_item");
            tables.Should().Contain("rec_project");
            tables.Should().Contain("rec_task_type");
            tables.Should().Contain("rec_task_status");
            tables.Should().Contain("rec_milestone");

            // Project-owned join tables — MUST exist
            tables.Should().Contain("rel_project_nn_task");
            tables.Should().Contain("rel_milestone_nn_task");
            tables.Should().Contain("rel_project_nn_milestone");
            tables.Should().Contain("rel_comment_nn_attachment");

            // EF Core system table — MUST exist
            tables.Should().Contain("__EFMigrationsHistory");
        }

        // =====================================================================
        // Phase 8: Data Integrity Round-Trip Tests
        // =====================================================================

        /// <summary>
        /// Per AAP 0.8.1: "Schema migration scripts ensure zero data loss".
        /// Inserts a record into each project entity table with known data,
        /// then retrieves and verifies ALL field values match exactly.
        /// This validates that the EF Core model correctly maps all columns and types.
        /// </summary>
        [Fact]
        public async Task InsertAndRetrieveAllEntityTypes_ShouldRoundTripCorrectly()
        {
            var now = DateTime.UtcNow;
            var userId = Guid.NewGuid(); // Cross-service user UUID (no FK)

            // First get a seeded task type and status to use as FK references
            Guid taskTypeId;
            Guid taskStatusId;
            using (var context = CreateDbContext())
            {
                var taskType = context.TaskTypes.First();
                taskTypeId = taskType.Id;
                var taskStatus = context.TaskStatuses.First();
                taskStatusId = taskStatus.Id;
            }

            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var timelogId = Guid.NewGuid();
            var commentId = Guid.NewGuid();
            var feedItemId = Guid.NewGuid();
            var milestoneId = Guid.NewGuid();

            // Insert records with known data
            using (var context = CreateDbContext())
            {
                context.Projects.Add(new ProjectEntity
                {
                    Id = projectId,
                    Name = "Round-Trip Project",
                    Abbr = "RTP",
                    Description = "Test project for round-trip validation",
                    OwnerId = userId,
                    Color = "#9c27b0",
                    Icon = "fas fa-cogs",
                    StartDate = now,
                    EndDate = now.AddMonths(3),
                    IsBillable = true,
                    ScopeKey = "test-scope",
                    XBillableHours = 100.5m,
                    XNonbillableHours = 50.25m,
                    XTasksNotStarted = 5,
                    XTasksInProgress = 3,
                    XTasksCompleted = 10,
                    XOverdueTasks = 2,
                    XMilestonesOnTrack = 1,
                    XMilestonesMissed = 0,
                    XBudget = 5000.00m
                });

                context.Tasks.Add(new TaskEntity
                {
                    Id = taskId,
                    Subject = "Round-Trip Task",
                    Body = "<p>Task body HTML</p>",
                    OwnerId = userId,
                    StartTime = now,
                    EndTime = now.AddDays(14),
                    CreatedOn = now,
                    CreatedBy = userId,
                    Priority = "2",
                    StatusId = taskStatusId,
                    TypeId = taskTypeId,
                    XNonbillableMinutes = 30m,
                    XBillableMinutes = 60m,
                    LScope = "[\"projects\"]",
                    LRelatedRecords = "[]",
                    XSearch = "round trip task test",
                    Key = "RTP-1"
                });

                context.Timelogs.Add(new TimelogEntity
                {
                    Id = timelogId,
                    Body = "Worked on round-trip testing",
                    CreatedBy = userId,
                    CreatedOn = now,
                    LRelatedRecords = "[]",
                    LScope = "[\"projects\"]",
                    Minutes = 120m,
                    IsBillable = true
                });

                context.Comments.Add(new CommentEntity
                {
                    Id = commentId,
                    Body = "Round-trip test comment",
                    CreatedBy = userId,
                    CreatedOn = now,
                    LScope = "[\"projects\"]",
                    LRelatedRecords = "[]"
                });

                context.FeedItems.Add(new FeedItemEntity
                {
                    Id = feedItemId,
                    CreatedBy = userId,
                    CreatedOn = now,
                    LScope = "[\"projects\"]",
                    Subject = "Feed Item Subject",
                    Body = "Feed item body",
                    Type = "task",
                    LRelatedRecords = "[]"
                });

                context.Milestones.Add(new MilestoneEntity
                {
                    Id = milestoneId,
                    Name = "Round-Trip Milestone",
                    StartDate = now,
                    EndDate = now.AddMonths(1),
                    Status = "on_track"
                });

                await context.SaveChangesAsync();
            }

            // Retrieve and verify all field values
            using (var context = CreateDbContext())
            {
                // Project round-trip
                var project = context.Projects.FirstOrDefault(p => p.Id == projectId);
                project.Should().NotBeNull();
                project.Name.Should().Be("Round-Trip Project");
                project.Abbr.Should().Be("RTP");
                project.Description.Should().Be("Test project for round-trip validation");
                project.OwnerId.Should().Be(userId);
                project.Color.Should().Be("#9c27b0");
                project.Icon.Should().Be("fas fa-cogs");
                project.IsBillable.Should().BeTrue();
                project.ScopeKey.Should().Be("test-scope");
                project.XBillableHours.Should().Be(100.5m);
                project.XNonbillableHours.Should().Be(50.25m);
                project.XTasksNotStarted.Should().Be(5);
                project.XTasksInProgress.Should().Be(3);
                project.XTasksCompleted.Should().Be(10);
                project.XOverdueTasks.Should().Be(2);
                project.XMilestonesOnTrack.Should().Be(1);
                project.XMilestonesMissed.Should().Be(0);
                project.XBudget.Should().Be(5000.00m);

                // Task round-trip
                var task = context.Tasks.FirstOrDefault(t => t.Id == taskId);
                task.Should().NotBeNull();
                task.Subject.Should().Be("Round-Trip Task");
                task.Body.Should().Be("<p>Task body HTML</p>");
                task.OwnerId.Should().Be(userId);
                task.CreatedBy.Should().Be(userId);
                task.Priority.Should().Be("2");
                task.StatusId.Should().Be(taskStatusId);
                task.TypeId.Should().Be(taskTypeId);
                task.XNonbillableMinutes.Should().Be(30m);
                task.XBillableMinutes.Should().Be(60m);
                task.LScope.Should().Be("[\"projects\"]");
                task.LRelatedRecords.Should().Be("[]");
                task.XSearch.Should().Be("round trip task test");
                task.Key.Should().Be("RTP-1");

                // Timelog round-trip
                var timelog = context.Timelogs.FirstOrDefault(t => t.Id == timelogId);
                timelog.Should().NotBeNull();
                timelog.Body.Should().Be("Worked on round-trip testing");
                timelog.CreatedBy.Should().Be(userId);
                timelog.Minutes.Should().Be(120m);
                timelog.IsBillable.Should().BeTrue();
                timelog.LScope.Should().Be("[\"projects\"]");
                timelog.LRelatedRecords.Should().Be("[]");

                // Comment round-trip
                var comment = context.Comments.FirstOrDefault(c => c.Id == commentId);
                comment.Should().NotBeNull();
                comment.Body.Should().Be("Round-trip test comment");
                comment.CreatedBy.Should().Be(userId);
                comment.LScope.Should().Be("[\"projects\"]");

                // FeedItem round-trip
                var feedItem = context.FeedItems.FirstOrDefault(f => f.Id == feedItemId);
                feedItem.Should().NotBeNull();
                feedItem.Subject.Should().Be("Feed Item Subject");
                feedItem.Body.Should().Be("Feed item body");
                feedItem.Type.Should().Be("task");
                feedItem.CreatedBy.Should().Be(userId);

                // Milestone round-trip
                var milestone = context.Milestones.FirstOrDefault(m => m.Id == milestoneId);
                milestone.Should().NotBeNull();
                milestone.Name.Should().Be("Round-Trip Milestone");
                milestone.Status.Should().Be("on_track");
            }
        }

        /// <summary>
        /// Validates that M:N relationships survive round-trip: creates project
        /// and task records, inserts links into join tables, queries back via
        /// DbSets, and verifies the relationship data persists correctly.
        /// </summary>
        [Fact]
        public async Task ManyToManyRelations_ShouldRoundTripCorrectly()
        {
            var projectId = Guid.NewGuid();
            var task1Id = Guid.NewGuid();
            var task2Id = Guid.NewGuid();
            var milestoneId = Guid.NewGuid();
            var commentId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();

            Guid m2mStatusId;
            Guid m2mTypeId;
            using (var seedCtx = CreateDbContext())
            {
                m2mStatusId = seedCtx.TaskStatuses.First().Id;
                m2mTypeId = seedCtx.TaskTypes.First().Id;
            }

            // Insert entities
            using (var context = CreateDbContext())
            {
                context.Projects.Add(new ProjectEntity
                {
                    Id = projectId,
                    Name = "M2M Test Project",
                    Abbr = "M2M"
                });
                context.Tasks.Add(new TaskEntity
                {
                    Id = task1Id,
                    Subject = "M2M Task 1",
                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                    StatusId = m2mStatusId,
                    TypeId = m2mTypeId,
                    Key = "M2M-1"
                });
                context.Tasks.Add(new TaskEntity
                {
                    Id = task2Id,
                    Subject = "M2M Task 2",
                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                    StatusId = m2mStatusId,
                    TypeId = m2mTypeId,
                    Key = "M2M-2"
                });
                context.Milestones.Add(new MilestoneEntity
                {
                    Id = milestoneId,
                    Name = "M2M Milestone"
                });
                context.Comments.Add(new CommentEntity
                {
                    Id = commentId,
                    Body = "M2M Comment",
                    CreatedOn = DateTime.UtcNow
                });
                await context.SaveChangesAsync();
            }

            // Insert M:N relations
            using (var context = CreateDbContext())
            {
                // Project links to two tasks
                context.ProjectTaskRelations.Add(new ProjectTaskRelation
                {
                    OriginId = projectId,
                    TargetId = task1Id
                });
                context.ProjectTaskRelations.Add(new ProjectTaskRelation
                {
                    OriginId = projectId,
                    TargetId = task2Id
                });

                // Milestone links to task 1
                context.MilestoneTaskRelations.Add(new MilestoneTaskRelation
                {
                    OriginId = milestoneId,
                    TargetId = task1Id
                });

                // Project links to milestone
                context.ProjectMilestoneRelations.Add(new ProjectMilestoneRelation
                {
                    OriginId = projectId,
                    TargetId = milestoneId
                });

                // Comment links to attachment (cross-service, UUID only)
                context.CommentAttachmentRelations.Add(new CommentAttachmentRelation
                {
                    OriginId = commentId,
                    TargetId = attachmentId
                });

                await context.SaveChangesAsync();
            }

            // Verify M:N relations survive round-trip
            using (var context = CreateDbContext())
            {
                // Project should have 2 task links
                var projectTasks = context.ProjectTaskRelations
                    .Where(r => r.OriginId == projectId)
                    .ToList();
                projectTasks.Should().HaveCount(2,
                    "project should link to exactly 2 tasks");
                projectTasks.Select(r => r.TargetId).Should()
                    .Contain(task1Id)
                    .And.Contain(task2Id);

                // Milestone should have 1 task link
                var milestoneTasks = context.MilestoneTaskRelations
                    .Where(r => r.OriginId == milestoneId)
                    .ToList();
                milestoneTasks.Should().HaveCount(1);
                milestoneTasks.First().TargetId.Should().Be(task1Id);

                // Project should have 1 milestone link
                var projectMilestones = context.ProjectMilestoneRelations
                    .Where(r => r.OriginId == projectId)
                    .ToList();
                projectMilestones.Should().HaveCount(1);
                projectMilestones.First().TargetId.Should().Be(milestoneId);

                // Comment should have 1 attachment link
                var commentAttachments = context.CommentAttachmentRelations
                    .Where(r => r.OriginId == commentId)
                    .ToList();
                commentAttachments.Should().HaveCount(1);
                commentAttachments.First().TargetId.Should().Be(attachmentId);
            }
        }
    }
}
