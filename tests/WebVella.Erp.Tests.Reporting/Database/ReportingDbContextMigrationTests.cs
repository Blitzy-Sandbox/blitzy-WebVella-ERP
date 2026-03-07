using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using WebVella.Erp.Service.Reporting.Database;
using Xunit;

namespace WebVella.Erp.Tests.Reporting.Database
{
    /// <summary>
    /// Integration tests validating the Reporting service's ReportingDbContext EF Core migrations,
    /// table schema configurations, index creation, cross-service UUID reference constraints
    /// (absence of FK), and DefaultValueSql behaviors.
    ///
    /// These tests run against a real PostgreSQL 16-alpine instance via Testcontainers.PostgreSql.
    /// The Reporting service owns an independent PostgreSQL database (erp_reporting) containing
    /// projection tables populated by event subscribers (MassTransit consumers).
    ///
    /// Covers:
    /// - Migration application and idempotency (AAP 0.8.1)
    /// - Schema verification for all 4 tables: timelog_projections (10 cols),
    ///   task_projections (5 cols), project_projections (5 cols), report_definitions (8 cols)
    /// - Index verification (13+ indexes including 1 composite)
    /// - Zero FK constraint verification (database-per-service, AAP 0.8.2)
    /// - DefaultValueSql("now()") behavior verification
    /// - InMemory provider compatibility
    /// </summary>
    [Collection("Database")]
    public class ReportingDbContextMigrationTests : IAsyncLifetime
    {
        #region Fields and Infrastructure

        private readonly PostgreSqlContainer _postgres;
        private string _connectionString = "";

        /// <summary>
        /// Constructs the test class, configuring a PostgreSQL 16-alpine container
        /// matching the docker-compose.localstack.yml infrastructure (AAP 0.7.4).
        /// </summary>
        public ReportingDbContextMigrationTests()
        {
            _postgres = new PostgreSqlBuilder("postgres:16-alpine")
                .Build();
        }

        /// <summary>
        /// Starts the PostgreSQL container and stores the connection string.
        /// Does NOT run migrations — individual tests control when migrations run
        /// for specific test scenarios (idempotency, reversibility, etc.).
        /// </summary>
        public async Task InitializeAsync()
        {
            // Enable legacy timestamp behavior to match the Reporting service's
            // AppContext switch set in Program.cs
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            await _postgres.StartAsync();
            _connectionString = _postgres.GetConnectionString();
        }

        /// <summary>
        /// Stops and disposes the PostgreSQL container after all tests complete.
        /// </summary>
        public async Task DisposeAsync()
        {
            await _postgres.DisposeAsync();
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a new ReportingDbContext configured with the test PostgreSQL connection string.
        /// Each test MUST create its own context for isolation and dispose it properly.
        /// </summary>
        private ReportingDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ReportingDbContext>()
                .UseNpgsql(_connectionString)
                .Options;
            return new ReportingDbContext(options);
        }

        /// <summary>
        /// Runs all pending EF Core migrations on the test database.
        /// Creates a scoped context, applies migrations, and disposes.
        /// </summary>
        private async Task RunMigrationsAsync()
        {
            using var context = CreateDbContext();
            await context.Database.MigrateAsync();
        }

        /// <summary>
        /// Queries information_schema.columns for the specified table and returns
        /// a list of ColumnInfo DTOs containing column metadata.
        /// </summary>
        private async Task<List<ColumnInfo>> GetTableColumnsAsync(string tableName)
        {
            var columns = new List<ColumnInfo>();
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = new NpgsqlCommand(
                @"SELECT column_name, data_type, is_nullable, column_default, character_maximum_length
                  FROM information_schema.columns
                  WHERE table_name = @tableName
                  ORDER BY ordinal_position", connection);
            cmd.Parameters.AddWithValue("@tableName", tableName);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(new ColumnInfo
                {
                    ColumnName = reader.GetString(0),
                    DataType = reader.GetString(1),
                    IsNullable = reader.GetString(2),
                    ColumnDefault = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CharacterMaximumLength = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4)
                });
            }
            return columns;
        }

        /// <summary>
        /// Checks whether the specified table exists in the test database by querying
        /// information_schema.tables.
        /// </summary>
        private async Task<bool> TableExistsAsync(string tableName)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = new NpgsqlCommand(
                @"SELECT COUNT(*) FROM information_schema.tables
                  WHERE table_schema = 'public' AND table_name = @tableName", connection);
            cmd.Parameters.AddWithValue("@tableName", tableName);

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result) > 0;
        }

        /// <summary>
        /// Returns all index names for the specified table by querying pg_indexes.
        /// </summary>
        private async Task<List<string>> GetIndexNamesAsync(string tableName)
        {
            var indexes = new List<string>();
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = new NpgsqlCommand(
                @"SELECT indexname FROM pg_indexes
                  WHERE schemaname = 'public' AND tablename = @tableName", connection);
            cmd.Parameters.AddWithValue("@tableName", tableName);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                indexes.Add(reader.GetString(0));
            }
            return indexes;
        }

        /// <summary>
        /// Returns all foreign key constraint names for the specified table by querying
        /// information_schema.table_constraints.
        /// </summary>
        private async Task<List<string>> GetForeignKeysAsync(string tableName)
        {
            var fks = new List<string>();
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = new NpgsqlCommand(
                @"SELECT constraint_name FROM information_schema.table_constraints
                  WHERE constraint_type = 'FOREIGN KEY' AND table_name = @tableName", connection);
            cmd.Parameters.AddWithValue("@tableName", tableName);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                fks.Add(reader.GetString(0));
            }
            return fks;
        }

        /// <summary>
        /// Returns the index definition SQL for the specified index name.
        /// Used to verify composite index column coverage.
        /// </summary>
        private async Task<string> GetIndexDefinitionAsync(string indexName)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = new NpgsqlCommand(
                @"SELECT indexdef FROM pg_indexes
                  WHERE schemaname = 'public' AND indexname = @indexName", connection);
            cmd.Parameters.AddWithValue("@indexName", indexName);

            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? "";
        }

        /// <summary>
        /// Returns the row count for the specified table.
        /// </summary>
        private async Task<long> GetRowCountAsync(string tableName)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM \"{tableName}\"", connection);
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }

        /// <summary>
        /// Helper DTO for column metadata retrieved from information_schema.columns.
        /// </summary>
        private class ColumnInfo
        {
            public string ColumnName { get; set; } = "";
            public string DataType { get; set; } = "";
            public string IsNullable { get; set; } = "";
            public string ColumnDefault { get; set; }
            public int? CharacterMaximumLength { get; set; }
        }

        #endregion

        #region Phase 2: Migration Apply Tests

        /// <summary>
        /// Verifies that MigrateAsync() applies all migrations successfully without
        /// throwing any exceptions and that at least one migration is recorded.
        /// Source: AAP 0.7.5 — "Each service's initial EF Core migration will codify the current state"
        /// </summary>
        [Fact]
        public async Task Migrate_ShouldApplyAllMigrationsSuccessfully()
        {
            // Arrange & Act
            using var context = CreateDbContext();
            await context.Database.MigrateAsync();

            // Assert — applied migrations should be non-empty
            var applied = await context.Database.GetAppliedMigrationsAsync();
            applied.Should().NotBeEmpty("at least the initial migration should be applied");
        }

        /// <summary>
        /// Verifies that migration creates all 4 required tables:
        /// timelog_projections, task_projections, project_projections, report_definitions.
        /// These tables correspond to the 4 DbSet properties on ReportingDbContext.
        /// </summary>
        [Fact]
        public async Task Migrate_ShouldCreateAllFourTables()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act & Assert
            (await TableExistsAsync("timelog_projections")).Should().BeTrue("timelog_projections table should exist after migration");
            (await TableExistsAsync("task_projections")).Should().BeTrue("task_projections table should exist after migration");
            (await TableExistsAsync("project_projections")).Should().BeTrue("project_projections table should exist after migration");
            (await TableExistsAsync("report_definitions")).Should().BeTrue("report_definitions table should exist after migration");
        }

        /// <summary>
        /// Verifies that the standard EF Core __EFMigrationsHistory table is created
        /// and contains at least one migration entry.
        /// Source: AAP 0.7.5 — "EF Core __EFMigrationsHistory table replaces plugin_data"
        /// </summary>
        [Fact]
        public async Task Migrate_ShouldCreateMigrationsHistoryTable()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            var historyExists = await TableExistsAsync("__EFMigrationsHistory");

            // Assert
            historyExists.Should().BeTrue("__EFMigrationsHistory table should exist after migration");

            // Verify at least one entry
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM \"__EFMigrationsHistory\"", connection);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            count.Should().BeGreaterThan(0, "at least one migration should be recorded in history");
        }

        /// <summary>
        /// Verifies idempotency: running MigrateAsync() twice on the same database
        /// completes without errors and preserves identical table structure.
        /// Source: AAP 0.8.1 — "Schema migration scripts must be idempotent and reversible"
        /// </summary>
        [Fact]
        public async Task Migrate_ShouldBeIdempotent_RunningTwiceDoesNotFail()
        {
            // Arrange — first migration
            using (var context1 = CreateDbContext())
            {
                await context1.Database.MigrateAsync();
            }

            // Record column counts after first migration
            var timelogCols1 = await GetTableColumnsAsync("timelog_projections");
            var taskCols1 = await GetTableColumnsAsync("task_projections");
            var projectCols1 = await GetTableColumnsAsync("project_projections");
            var reportCols1 = await GetTableColumnsAsync("report_definitions");

            // Act — second migration (should be idempotent)
            using (var context2 = CreateDbContext())
            {
                await context2.Database.MigrateAsync();
            }

            // Assert — column counts should be identical
            var timelogCols2 = await GetTableColumnsAsync("timelog_projections");
            var taskCols2 = await GetTableColumnsAsync("task_projections");
            var projectCols2 = await GetTableColumnsAsync("project_projections");
            var reportCols2 = await GetTableColumnsAsync("report_definitions");

            timelogCols2.Count.Should().Be(timelogCols1.Count, "timelog_projections column count should be unchanged");
            taskCols2.Count.Should().Be(taskCols1.Count, "task_projections column count should be unchanged");
            projectCols2.Count.Should().Be(projectCols1.Count, "project_projections column count should be unchanged");
            reportCols2.Count.Should().Be(reportCols1.Count, "report_definitions column count should be unchanged");
        }

        /// <summary>
        /// Verifies that re-running migrations preserves existing data — zero data loss.
        /// Inserts test data, runs migrations again, and verifies all row counts unchanged.
        /// Source: AAP 0.8.1 — "Zero data loss during schema migration"
        /// </summary>
        [Fact]
        public async Task Migrate_ShouldBeIdempotent_PreservingExistingData()
        {
            // Arrange — run initial migration
            await RunMigrationsAsync();

            // Insert test data via raw SQL
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var testId1 = Guid.NewGuid();
            var testId2 = Guid.NewGuid();
            var testId3 = Guid.NewGuid();
            var testId4 = Guid.NewGuid();

            using (var cmd = new NpgsqlCommand(
                @"INSERT INTO timelog_projections (id, task_id, is_billable, minutes, logged_on)
                  VALUES (@id, @taskId, true, 60, '2024-06-15 10:00:00+00')", connection))
            {
                cmd.Parameters.AddWithValue("@id", testId1);
                cmd.Parameters.AddWithValue("@taskId", Guid.NewGuid());
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = new NpgsqlCommand(
                @"INSERT INTO task_projections (id, subject) VALUES (@id, 'Test Task')", connection))
            {
                cmd.Parameters.AddWithValue("@id", testId2);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = new NpgsqlCommand(
                @"INSERT INTO project_projections (id, name) VALUES (@id, 'Test Project')", connection))
            {
                cmd.Parameters.AddWithValue("@id", testId3);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = new NpgsqlCommand(
                @"INSERT INTO report_definitions (id, name, report_type, created_by)
                  VALUES (@id, 'Test Report', 'timelog', @createdBy)", connection))
            {
                cmd.Parameters.AddWithValue("@id", testId4);
                cmd.Parameters.AddWithValue("@createdBy", Guid.NewGuid());
                await cmd.ExecuteNonQueryAsync();
            }

            // Record row counts
            var timelogCount1 = await GetRowCountAsync("timelog_projections");
            var taskCount1 = await GetRowCountAsync("task_projections");
            var projectCount1 = await GetRowCountAsync("project_projections");
            var reportCount1 = await GetRowCountAsync("report_definitions");

            // Act — re-run migrations on same database
            await RunMigrationsAsync();

            // Assert — zero data loss
            var timelogCount2 = await GetRowCountAsync("timelog_projections");
            var taskCount2 = await GetRowCountAsync("task_projections");
            var projectCount2 = await GetRowCountAsync("project_projections");
            var reportCount2 = await GetRowCountAsync("report_definitions");

            timelogCount2.Should().Be(timelogCount1, "timelog_projections row count should be preserved");
            taskCount2.Should().Be(taskCount1, "task_projections row count should be preserved");
            projectCount2.Should().Be(projectCount1, "project_projections row count should be preserved");
            reportCount2.Should().Be(reportCount1, "report_definitions row count should be preserved");
        }

        /// <summary>
        /// Verifies that down migration (revert to "0") drops all 4 application tables.
        /// Source: AAP 0.8.1 — "Migrations are reversible"
        /// </summary>
        [Fact]
        public async Task Migrate_ShouldBeReversible_DownMigrationDropsTables()
        {
            // Arrange — run UP migration
            await RunMigrationsAsync();

            // Verify tables exist after UP
            (await TableExistsAsync("timelog_projections")).Should().BeTrue();
            (await TableExistsAsync("task_projections")).Should().BeTrue();
            (await TableExistsAsync("project_projections")).Should().BeTrue();
            (await TableExistsAsync("report_definitions")).Should().BeTrue();

            // Act — run DOWN migration to revert to empty state
            using var context = CreateDbContext();
            var migrator = context.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync("0");

            // Assert — all 4 application tables should be dropped
            (await TableExistsAsync("timelog_projections")).Should().BeFalse("timelog_projections should be dropped after down migration");
            (await TableExistsAsync("task_projections")).Should().BeFalse("task_projections should be dropped after down migration");
            (await TableExistsAsync("project_projections")).Should().BeFalse("project_projections should be dropped after down migration");
            (await TableExistsAsync("report_definitions")).Should().BeFalse("report_definitions should be dropped after down migration");
        }

        /// <summary>
        /// Verifies that after full migration, GetPendingMigrationsAsync returns an empty list
        /// indicating all migrations have been applied.
        /// </summary>
        [Fact]
        public async Task GetPendingMigrations_ShouldReturnEmpty_AfterFullMigration()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            using var context = CreateDbContext();
            var pending = await context.Database.GetPendingMigrationsAsync();

            // Assert
            pending.Should().BeEmpty("all migrations should be applied after MigrateAsync()");
        }

        #endregion

        #region Phase 3: timelog_projections Table Configuration Tests

        /// <summary>
        /// Verifies that timelog_projections has all 10 required columns with correct types.
        /// Schema derived from timelog fields in ReportService.cs lines 42-46:
        /// id, is_billable, l_related_records→task_id, minutes, logged_on, l_scope
        /// plus denormalized project_id, account_id, and audit timestamps.
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_ShouldHaveAllRequiredColumns()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            var columns = await GetTableColumnsAsync("timelog_projections");

            // Assert — exactly 10 columns
            columns.Should().HaveCount(10, "timelog_projections should have 10 columns");

            // Verify each column exists
            columns.Should().Contain(c => c.ColumnName == "id", "id column should exist");
            columns.Should().Contain(c => c.ColumnName == "task_id", "task_id column should exist");
            columns.Should().Contain(c => c.ColumnName == "project_id", "project_id column should exist");
            columns.Should().Contain(c => c.ColumnName == "account_id", "account_id column should exist");
            columns.Should().Contain(c => c.ColumnName == "is_billable", "is_billable column should exist");
            columns.Should().Contain(c => c.ColumnName == "minutes", "minutes column should exist");
            columns.Should().Contain(c => c.ColumnName == "logged_on", "logged_on column should exist");
            columns.Should().Contain(c => c.ColumnName == "scope", "scope column should exist");
            columns.Should().Contain(c => c.ColumnName == "created_on", "created_on column should exist");
            columns.Should().Contain(c => c.ColumnName == "last_modified_on", "last_modified_on column should exist");
        }

        /// <summary>
        /// Verifies that the id column on timelog_projections is a non-nullable UUID primary key.
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_IdColumn_ShouldBeUuidPrimaryKey()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act — verify column type
            var columns = await GetTableColumnsAsync("timelog_projections");
            var idCol = columns.First(c => c.ColumnName == "id");
            idCol.DataType.Should().Be("uuid");
            idCol.IsNullable.Should().Be("NO");

            // Verify PK constraint exists
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            using var cmd = new NpgsqlCommand(
                @"SELECT COUNT(*) FROM information_schema.table_constraints
                  WHERE constraint_type = 'PRIMARY KEY' AND table_name = 'timelog_projections'", connection);
            var pkCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            pkCount.Should().BeGreaterThan(0, "timelog_projections should have a primary key constraint");
        }

        /// <summary>
        /// Verifies task_id is a non-nullable UUID column (cross-service reference to Project service).
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_TaskIdColumn_ShouldBeNonNullableUuid()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("timelog_projections");
            var col = columns.First(c => c.ColumnName == "task_id");
            col.DataType.Should().Be("uuid");
            col.IsNullable.Should().Be("NO");
        }

        /// <summary>
        /// Verifies project_id is a nullable UUID column (timelog may have no project association).
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_ProjectIdColumn_ShouldBeNullableUuid()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("timelog_projections");
            var col = columns.First(c => c.ColumnName == "project_id");
            col.DataType.Should().Be("uuid");
            col.IsNullable.Should().Be("YES");
        }

        /// <summary>
        /// Verifies account_id is a nullable UUID column (cross-service reference to CRM's account entity).
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_AccountIdColumn_ShouldBeNullableUuid()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("timelog_projections");
            var col = columns.First(c => c.ColumnName == "account_id");
            col.DataType.Should().Be("uuid");
            col.IsNullable.Should().Be("YES");
        }

        /// <summary>
        /// Verifies is_billable is a non-nullable boolean column.
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_IsBillableColumn_ShouldBeNonNullableBoolean()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("timelog_projections");
            var col = columns.First(c => c.ColumnName == "is_billable");
            col.DataType.Should().Be("boolean");
            col.IsNullable.Should().Be("NO");
        }

        /// <summary>
        /// Verifies minutes is a non-nullable numeric column with a default value of 0.
        /// The EF Core configuration uses HasDefaultValue(0m).
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_MinutesColumn_ShouldBeNumericWithDefaultZero()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("timelog_projections");
            var col = columns.First(c => c.ColumnName == "minutes");
            col.DataType.Should().Be("numeric");
            col.IsNullable.Should().Be("NO");
            col.ColumnDefault.Should().NotBeNull("minutes should have a default value");
            col.ColumnDefault.Should().Contain("0", "minutes default should contain 0");
        }

        /// <summary>
        /// Verifies logged_on is a non-nullable timestamp with time zone column.
        /// Used for date-range filtering: WHERE logged_on >= @from_date AND logged_on &lt;= @to_date
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_LoggedOnColumn_ShouldBeTimestamptz()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("timelog_projections");
            var col = columns.First(c => c.ColumnName == "logged_on");
            col.DataType.Should().Contain("timestamp", "logged_on should be a timestamp type");
            col.IsNullable.Should().Be("NO");
        }

        /// <summary>
        /// Verifies scope is a non-nullable text column with default value 'projects'.
        /// The EF Core configuration uses HasDefaultValue("projects").
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_ScopeColumn_ShouldHaveDefaultProjects()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("timelog_projections");
            var col = columns.First(c => c.ColumnName == "scope");
            col.DataType.Should().Be("text");
            col.IsNullable.Should().Be("NO");
            col.ColumnDefault.Should().NotBeNull("scope should have a default value");
            col.ColumnDefault.Should().Contain("projects", "scope default should contain 'projects'");
        }

        /// <summary>
        /// Verifies created_on is a non-nullable timestamptz column with DefaultValueSql("now()").
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_CreatedOnColumn_ShouldHaveDefaultNow()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("timelog_projections");
            var col = columns.First(c => c.ColumnName == "created_on");
            col.DataType.Should().Contain("timestamp", "created_on should be a timestamp type");
            col.IsNullable.Should().Be("NO");
            col.ColumnDefault.Should().NotBeNull("created_on should have a default value");
            col.ColumnDefault.Should().Contain("now()", "created_on default should be now()");
        }

        /// <summary>
        /// Verifies last_modified_on is a non-nullable timestamptz column with DefaultValueSql("now()").
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_LastModifiedOnColumn_ShouldHaveDefaultNow()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("timelog_projections");
            var col = columns.First(c => c.ColumnName == "last_modified_on");
            col.DataType.Should().Contain("timestamp", "last_modified_on should be a timestamp type");
            col.IsNullable.Should().Be("NO");
            col.ColumnDefault.Should().NotBeNull("last_modified_on should have a default value");
            col.ColumnDefault.Should().Contain("now()", "last_modified_on default should be now()");
        }

        #endregion

        #region Phase 4: task_projections Table Configuration Tests

        /// <summary>
        /// Verifies that task_projections has all 5 required columns.
        /// Based on task fields from ReportService.GetTimelogData() source line 60:
        /// SELECT id, subject, $task_type_1n_task.label FROM task
        /// </summary>
        [Fact]
        public async Task TaskProjectionsTable_ShouldHaveAllRequiredColumns()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("task_projections");

            columns.Should().HaveCount(5, "task_projections should have 5 columns");
            columns.Should().Contain(c => c.ColumnName == "id");
            columns.Should().Contain(c => c.ColumnName == "subject");
            columns.Should().Contain(c => c.ColumnName == "task_type_label");
            columns.Should().Contain(c => c.ColumnName == "created_on");
            columns.Should().Contain(c => c.ColumnName == "last_modified_on");
        }

        /// <summary>
        /// Verifies subject is a non-nullable text column.
        /// </summary>
        [Fact]
        public async Task TaskProjectionsTable_SubjectColumn_ShouldBeNonNullableText()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("task_projections");
            var col = columns.First(c => c.ColumnName == "subject");
            col.DataType.Should().Be("text");
            col.IsNullable.Should().Be("NO");
        }

        /// <summary>
        /// Verifies task_type_label is a nullable text column (task may not have a type assigned).
        /// </summary>
        [Fact]
        public async Task TaskProjectionsTable_TaskTypeLabelColumn_ShouldBeNullableText()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("task_projections");
            var col = columns.First(c => c.ColumnName == "task_type_label");
            col.DataType.Should().Be("text");
            col.IsNullable.Should().Be("YES");
        }

        #endregion

        #region Phase 5: project_projections Table Configuration Tests

        /// <summary>
        /// Verifies that project_projections has all 5 required columns.
        /// Based on project fields from ReportService.GetTimelogData() source line 60:
        /// $project_nn_task.id, $project_nn_task.name, $project_nn_task.account_id
        /// </summary>
        [Fact]
        public async Task ProjectProjectionsTable_ShouldHaveAllRequiredColumns()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("project_projections");

            columns.Should().HaveCount(5, "project_projections should have 5 columns");
            columns.Should().Contain(c => c.ColumnName == "id");
            columns.Should().Contain(c => c.ColumnName == "name");
            columns.Should().Contain(c => c.ColumnName == "account_id");
            columns.Should().Contain(c => c.ColumnName == "created_on");
            columns.Should().Contain(c => c.ColumnName == "last_modified_on");
        }

        /// <summary>
        /// Verifies name is a non-nullable text column.
        /// </summary>
        [Fact]
        public async Task ProjectProjectionsTable_NameColumn_ShouldBeNonNullableText()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("project_projections");
            var col = columns.First(c => c.ColumnName == "name");
            col.DataType.Should().Be("text");
            col.IsNullable.Should().Be("NO");
        }

        /// <summary>
        /// Verifies account_id is a nullable UUID column (cross-service reference to CRM account).
        /// </summary>
        [Fact]
        public async Task ProjectProjectionsTable_AccountIdColumn_ShouldBeNullableUuid()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("project_projections");
            var col = columns.First(c => c.ColumnName == "account_id");
            col.DataType.Should().Be("uuid");
            col.IsNullable.Should().Be("YES");
        }

        #endregion

        #region Phase 6: report_definitions Table Configuration Tests

        /// <summary>
        /// Verifies that report_definitions has all 8 required columns.
        /// Brand new concept for the Reporting microservice — stored report configurations.
        /// </summary>
        [Fact]
        public async Task ReportDefinitionsTable_ShouldHaveAllRequiredColumns()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("report_definitions");

            columns.Should().HaveCount(8, "report_definitions should have 8 columns");
            columns.Should().Contain(c => c.ColumnName == "id");
            columns.Should().Contain(c => c.ColumnName == "name");
            columns.Should().Contain(c => c.ColumnName == "description");
            columns.Should().Contain(c => c.ColumnName == "report_type");
            columns.Should().Contain(c => c.ColumnName == "parameters_json");
            columns.Should().Contain(c => c.ColumnName == "created_by");
            columns.Should().Contain(c => c.ColumnName == "created_on");
            columns.Should().Contain(c => c.ColumnName == "last_modified_on");
        }

        /// <summary>
        /// Verifies report_type is a varchar(200) column matching the HasColumnType("varchar(200)") configuration.
        /// </summary>
        [Fact]
        public async Task ReportDefinitionsTable_ReportTypeColumn_ShouldBeVarchar200()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("report_definitions");
            var col = columns.First(c => c.ColumnName == "report_type");
            col.DataType.Should().Be("character varying");
            col.CharacterMaximumLength.Should().Be(200);
        }

        /// <summary>
        /// Verifies parameters_json is a non-nullable text column with default empty JSON object.
        /// The EF Core configuration uses HasDefaultValue("{}").
        /// </summary>
        [Fact]
        public async Task ReportDefinitionsTable_ParametersJsonColumn_ShouldHaveDefaultEmptyJsonObject()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("report_definitions");
            var col = columns.First(c => c.ColumnName == "parameters_json");
            col.DataType.Should().Be("text");
            col.IsNullable.Should().Be("NO");
            col.ColumnDefault.Should().NotBeNull("parameters_json should have a default value");
            col.ColumnDefault.Should().Contain("{}", "parameters_json default should contain '{}'");
        }

        /// <summary>
        /// Verifies description is a nullable text column (optional report description).
        /// </summary>
        [Fact]
        public async Task ReportDefinitionsTable_DescriptionColumn_ShouldBeNullableText()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("report_definitions");
            var col = columns.First(c => c.ColumnName == "description");
            col.DataType.Should().Be("text");
            col.IsNullable.Should().Be("YES");
        }

        /// <summary>
        /// Verifies created_by is a non-nullable UUID column (cross-service reference to Core user).
        /// </summary>
        [Fact]
        public async Task ReportDefinitionsTable_CreatedByColumn_ShouldBeNonNullableUuid()
        {
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("report_definitions");
            var col = columns.First(c => c.ColumnName == "created_by");
            col.DataType.Should().Be("uuid");
            col.IsNullable.Should().Be("NO");
        }

        #endregion

        #region Phase 7: Index Verification Tests

        /// <summary>
        /// Verifies the critical composite index idx_timelog_proj_logged_on_scope exists
        /// and covers both logged_on AND scope columns. This index optimizes the primary
        /// query pattern from ReportService.GetTimelogData() (source lines 42-45):
        /// WHERE logged_on >= @from_date AND logged_on &lt;= @to_date AND l_scope CONTAINS @scope
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_ShouldHaveCompositeIndex_LoggedOnScope()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("timelog_projections");

            indexes.Should().Contain("idx_timelog_proj_logged_on_scope",
                "composite index for logged_on + scope should exist");

            // Verify the index covers both columns
            var indexDef = await GetIndexDefinitionAsync("idx_timelog_proj_logged_on_scope");
            indexDef.Should().Contain("logged_on", "composite index should cover logged_on column");
            indexDef.Should().Contain("scope", "composite index should cover scope column");
        }

        /// <summary>
        /// Verifies individual index on task_id for join/filter operations.
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_ShouldHaveIndex_TaskId()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("timelog_projections");
            indexes.Should().Contain("idx_timelog_proj_task_id");
        }

        /// <summary>
        /// Verifies individual index on project_id for project-based filtering.
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_ShouldHaveIndex_ProjectId()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("timelog_projections");
            indexes.Should().Contain("idx_timelog_proj_project_id");
        }

        /// <summary>
        /// Verifies individual index on account_id for account-based filtering.
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_ShouldHaveIndex_AccountId()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("timelog_projections");
            indexes.Should().Contain("idx_timelog_proj_account_id");
        }

        /// <summary>
        /// Verifies individual index on logged_on for date-range queries.
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_ShouldHaveIndex_LoggedOn()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("timelog_projections");
            indexes.Should().Contain("idx_timelog_proj_logged_on");
        }

        /// <summary>
        /// Verifies individual index on scope for scope-based filtering.
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_ShouldHaveIndex_Scope()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("timelog_projections");
            indexes.Should().Contain("idx_timelog_proj_scope");
        }

        /// <summary>
        /// Verifies index on report_definitions.name for report lookup.
        /// </summary>
        [Fact]
        public async Task ReportDefinitionsTable_ShouldHaveIndex_Name()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("report_definitions");
            indexes.Should().Contain("idx_report_def_name");
        }

        /// <summary>
        /// Verifies index on report_definitions.report_type for type-based filtering.
        /// </summary>
        [Fact]
        public async Task ReportDefinitionsTable_ShouldHaveIndex_ReportType()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("report_definitions");
            indexes.Should().Contain("idx_report_def_report_type");
        }

        /// <summary>
        /// Verifies index on report_definitions.created_by for user-based filtering.
        /// </summary>
        [Fact]
        public async Task ReportDefinitionsTable_ShouldHaveIndex_CreatedBy()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("report_definitions");
            indexes.Should().Contain("idx_report_def_created_by");
        }

        /// <summary>
        /// Verifies index on task_projections.subject for text-based search.
        /// </summary>
        [Fact]
        public async Task TaskProjectionsTable_ShouldHaveIndex_Subject()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("task_projections");
            indexes.Should().Contain("idx_task_proj_subject");
        }

        /// <summary>
        /// Verifies index on project_projections.account_id for account filtering.
        /// </summary>
        [Fact]
        public async Task ProjectProjectionsTable_ShouldHaveIndex_AccountId()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("project_projections");
            indexes.Should().Contain("idx_project_proj_account_id");
        }

        /// <summary>
        /// Verifies index on project_projections.name for project lookup.
        /// </summary>
        [Fact]
        public async Task ProjectProjectionsTable_ShouldHaveIndex_Name()
        {
            await RunMigrationsAsync();
            var indexes = await GetIndexNamesAsync("project_projections");
            indexes.Should().Contain("idx_project_proj_name");
        }

        #endregion

        #region Phase 8: Cross-Service UUID Reference Tests — NO Foreign Key Constraints

        /// <summary>
        /// Verifies NO foreign key constraints exist on timelog_projections.account_id.
        /// account_id is a cross-service reference to CRM service's account entity (AAP 0.7.1).
        /// Per AAP 0.8.2: "No service may require another service's database."
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_AccountId_ShouldHaveNoForeignKeyConstraint()
        {
            await RunMigrationsAsync();
            var fks = await GetForeignKeysAsync("timelog_projections");
            fks.Should().BeEmpty("timelog_projections should have no FK constraints (database-per-service)");
        }

        /// <summary>
        /// Verifies NO foreign key constraints exist on project_projections.account_id.
        /// account_id is a cross-service reference to CRM service's account entity.
        /// </summary>
        [Fact]
        public async Task ProjectProjectionsTable_AccountId_ShouldHaveNoForeignKeyConstraint()
        {
            await RunMigrationsAsync();
            var fks = await GetForeignKeysAsync("project_projections");
            fks.Should().BeEmpty("project_projections should have no FK constraints (database-per-service)");
        }

        /// <summary>
        /// Verifies NO foreign key constraints exist on report_definitions.created_by.
        /// created_by is a cross-service reference to Core service's user entity.
        /// </summary>
        [Fact]
        public async Task ReportDefinitionsTable_CreatedBy_ShouldHaveNoForeignKeyConstraint()
        {
            await RunMigrationsAsync();
            var fks = await GetForeignKeysAsync("report_definitions");
            fks.Should().BeEmpty("report_definitions should have no FK constraints (database-per-service)");
        }

        /// <summary>
        /// Verifies NO foreign key constraints on timelog_projections.task_id
        /// (cross-service reference to Project service's task entity).
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_TaskId_ShouldHaveNoForeignKeyConstraint()
        {
            await RunMigrationsAsync();
            var fks = await GetForeignKeysAsync("timelog_projections");
            fks.Should().BeEmpty("timelog_projections should have no FK constraints on task_id");
        }

        /// <summary>
        /// Verifies NO foreign key constraints on timelog_projections.project_id
        /// (cross-service reference to Project service's project entity).
        /// </summary>
        [Fact]
        public async Task TimelogProjectionsTable_ProjectId_ShouldHaveNoForeignKeyConstraint()
        {
            await RunMigrationsAsync();
            var fks = await GetForeignKeysAsync("timelog_projections");
            fks.Should().BeEmpty("timelog_projections should have no FK constraints on project_id");
        }

        /// <summary>
        /// Verifies that ALL 4 tables combined have ZERO foreign key constraints.
        /// The Reporting service has NO FK dependencies on any other service's database.
        /// </summary>
        [Fact]
        public async Task AllTables_ShouldHaveZeroForeignKeyConstraints()
        {
            await RunMigrationsAsync();

            var allFks = new List<string>();
            allFks.AddRange(await GetForeignKeysAsync("timelog_projections"));
            allFks.AddRange(await GetForeignKeysAsync("task_projections"));
            allFks.AddRange(await GetForeignKeysAsync("project_projections"));
            allFks.AddRange(await GetForeignKeysAsync("report_definitions"));

            allFks.Should().BeEmpty(
                "the Reporting service should have ZERO FK dependencies on any other service's database");
        }

        /// <summary>
        /// Verifies that cross-service UUID fields accept any valid GUID without
        /// referential integrity violations. Inserts arbitrary UUIDs for task_id,
        /// project_id, and account_id — the referenced entities may not exist in any
        /// other service's database, and that's by design (AAP 0.7.1, 0.8.2).
        /// </summary>
        [Fact]
        public async Task CrossServiceUuidFields_ShouldAcceptAnyValidGuid()
        {
            await RunMigrationsAsync();

            var arbitraryTaskId = Guid.NewGuid();
            var arbitraryProjectId = Guid.NewGuid();
            var arbitraryAccountId = Guid.NewGuid();
            var recordId = Guid.NewGuid();

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Insert with arbitrary cross-service UUIDs — should succeed without FK violations
            using var cmd = new NpgsqlCommand(
                @"INSERT INTO timelog_projections (id, task_id, project_id, account_id, is_billable, minutes, logged_on)
                  VALUES (@id, @taskId, @projectId, @accountId, true, 30, '2024-06-15 10:00:00+00')", connection);
            cmd.Parameters.AddWithValue("@id", recordId);
            cmd.Parameters.AddWithValue("@taskId", arbitraryTaskId);
            cmd.Parameters.AddWithValue("@projectId", arbitraryProjectId);
            cmd.Parameters.AddWithValue("@accountId", arbitraryAccountId);

            // Act & Assert — insert should succeed without any referential integrity exception
            var rowsAffected = await cmd.ExecuteNonQueryAsync();
            rowsAffected.Should().Be(1, "insert with arbitrary cross-service UUIDs should succeed");
        }

        #endregion

        #region Phase 9: DefaultValueSql Verification Tests

        /// <summary>
        /// Verifies that created_on defaults to approximately now() when not specified.
        /// Source: DefaultValueSql("now()") in OnModelCreating.
        /// </summary>
        [Fact]
        public async Task TimelogProjections_CreatedOn_DefaultValueSql_ShouldBeNow()
        {
            await RunMigrationsAsync();
            var recordId = Guid.NewGuid();
            var beforeInsert = DateTime.UtcNow;

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Insert WITHOUT specifying created_on — should default to now()
            using (var cmd = new NpgsqlCommand(
                @"INSERT INTO timelog_projections (id, task_id, is_billable, minutes, logged_on)
                  VALUES (@id, @taskId, true, 45, '2024-06-15 10:00:00+00')", connection))
            {
                cmd.Parameters.AddWithValue("@id", recordId);
                cmd.Parameters.AddWithValue("@taskId", Guid.NewGuid());
                await cmd.ExecuteNonQueryAsync();
            }

            // Read back the created_on value
            using (var cmd = new NpgsqlCommand(
                "SELECT created_on FROM timelog_projections WHERE id = @id", connection))
            {
                cmd.Parameters.AddWithValue("@id", recordId);
                var createdOn = (DateTime)await cmd.ExecuteScalarAsync();
                createdOn.Should().BeCloseTo(beforeInsert, TimeSpan.FromSeconds(30),
                    "created_on should default to approximately now()");
            }
        }

        /// <summary>
        /// Verifies that last_modified_on defaults to approximately now() when not specified.
        /// </summary>
        [Fact]
        public async Task TimelogProjections_LastModifiedOn_DefaultValueSql_ShouldBeNow()
        {
            await RunMigrationsAsync();
            var recordId = Guid.NewGuid();
            var beforeInsert = DateTime.UtcNow;

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using (var cmd = new NpgsqlCommand(
                @"INSERT INTO timelog_projections (id, task_id, is_billable, minutes, logged_on)
                  VALUES (@id, @taskId, false, 15, '2024-06-15 10:00:00+00')", connection))
            {
                cmd.Parameters.AddWithValue("@id", recordId);
                cmd.Parameters.AddWithValue("@taskId", Guid.NewGuid());
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = new NpgsqlCommand(
                "SELECT last_modified_on FROM timelog_projections WHERE id = @id", connection))
            {
                cmd.Parameters.AddWithValue("@id", recordId);
                var lastModified = (DateTime)await cmd.ExecuteScalarAsync();
                lastModified.Should().BeCloseTo(beforeInsert, TimeSpan.FromSeconds(30),
                    "last_modified_on should default to approximately now()");
            }
        }

        /// <summary>
        /// Verifies that report_definitions.created_on defaults to approximately now().
        /// </summary>
        [Fact]
        public async Task ReportDefinitions_CreatedOn_DefaultValueSql_ShouldBeNow()
        {
            await RunMigrationsAsync();
            var recordId = Guid.NewGuid();
            var beforeInsert = DateTime.UtcNow;

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using (var cmd = new NpgsqlCommand(
                @"INSERT INTO report_definitions (id, name, report_type, created_by)
                  VALUES (@id, 'Monthly Report', 'timelog', @createdBy)", connection))
            {
                cmd.Parameters.AddWithValue("@id", recordId);
                cmd.Parameters.AddWithValue("@createdBy", Guid.NewGuid());
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = new NpgsqlCommand(
                "SELECT created_on FROM report_definitions WHERE id = @id", connection))
            {
                cmd.Parameters.AddWithValue("@id", recordId);
                var createdOn = (DateTime)await cmd.ExecuteScalarAsync();
                createdOn.Should().BeCloseTo(beforeInsert, TimeSpan.FromSeconds(30),
                    "report_definitions.created_on should default to approximately now()");
            }
        }

        /// <summary>
        /// Verifies that task_projections timestamp columns (created_on, last_modified_on)
        /// both default to approximately now() when not specified.
        /// </summary>
        [Fact]
        public async Task TaskProjections_TimestampDefaults_ShouldBeNow()
        {
            await RunMigrationsAsync();
            var recordId = Guid.NewGuid();
            var beforeInsert = DateTime.UtcNow;

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Insert without timestamp columns
            using (var cmd = new NpgsqlCommand(
                @"INSERT INTO task_projections (id, subject) VALUES (@id, 'Test Task')", connection))
            {
                cmd.Parameters.AddWithValue("@id", recordId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Read back both timestamps
            using (var cmd = new NpgsqlCommand(
                "SELECT created_on, last_modified_on FROM task_projections WHERE id = @id", connection))
            {
                cmd.Parameters.AddWithValue("@id", recordId);
                using var reader = await cmd.ExecuteReaderAsync();
                await reader.ReadAsync();

                var createdOn = reader.GetDateTime(0);
                var lastModifiedOn = reader.GetDateTime(1);

                createdOn.Should().BeCloseTo(beforeInsert, TimeSpan.FromSeconds(30),
                    "task_projections.created_on should default to now()");
                lastModifiedOn.Should().BeCloseTo(beforeInsert, TimeSpan.FromSeconds(30),
                    "task_projections.last_modified_on should default to now()");
            }
        }

        /// <summary>
        /// Verifies that project_projections timestamp columns (created_on, last_modified_on)
        /// both default to approximately now() when not specified.
        /// </summary>
        [Fact]
        public async Task ProjectProjections_TimestampDefaults_ShouldBeNow()
        {
            await RunMigrationsAsync();
            var recordId = Guid.NewGuid();
            var beforeInsert = DateTime.UtcNow;

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Insert without timestamp columns
            using (var cmd = new NpgsqlCommand(
                @"INSERT INTO project_projections (id, name) VALUES (@id, 'Test Project')", connection))
            {
                cmd.Parameters.AddWithValue("@id", recordId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Read back both timestamps
            using (var cmd = new NpgsqlCommand(
                "SELECT created_on, last_modified_on FROM project_projections WHERE id = @id", connection))
            {
                cmd.Parameters.AddWithValue("@id", recordId);
                using var reader = await cmd.ExecuteReaderAsync();
                await reader.ReadAsync();

                var createdOn = reader.GetDateTime(0);
                var lastModifiedOn = reader.GetDateTime(1);

                createdOn.Should().BeCloseTo(beforeInsert, TimeSpan.FromSeconds(30),
                    "project_projections.created_on should default to now()");
                lastModifiedOn.Should().BeCloseTo(beforeInsert, TimeSpan.FromSeconds(30),
                    "project_projections.last_modified_on should default to now()");
            }
        }

        #endregion

        #region Phase 10: EF Core InMemory Provider Quick-Check Tests

        /// <summary>
        /// Validates that ReportingDbContext configuration works with the fast InMemory provider
        /// for unit test scenarios. Adds entities to all 4 DbSets and verifies they can be
        /// queried back, confirming the DbContext model configuration is valid.
        /// </summary>
        [Fact]
        public async Task ReportingDbContext_ShouldWorkWithInMemoryProvider()
        {
            // Arrange — create context with InMemory provider
            var options = new DbContextOptionsBuilder<ReportingDbContext>()
                .UseInMemoryDatabase(databaseName: "test_db_" + Guid.NewGuid().ToString("N"))
                .Options;

            using var context = new ReportingDbContext(options);

            // Act — add entities to all 4 DbSets
            var timelogId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var reportId = Guid.NewGuid();

            context.TimelogProjections.Add(new TimelogProjection
            {
                Id = timelogId,
                TaskId = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                AccountId = Guid.NewGuid(),
                IsBillable = true,
                Minutes = 120m,
                LoggedOn = DateTime.UtcNow,
                Scope = "projects",
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow
            });

            context.TaskProjections.Add(new TaskProjection
            {
                Id = taskId,
                Subject = "Implement Feature X",
                TaskTypeLabel = "Development",
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow
            });

            context.ProjectProjections.Add(new ProjectProjection
            {
                Id = projectId,
                Name = "Project Alpha",
                AccountId = Guid.NewGuid(),
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow
            });

            context.ReportDefinitions.Add(new ReportDefinition
            {
                Id = reportId,
                Name = "Monthly Timelog Report",
                Description = "Reports timelog data grouped by project and task",
                ReportType = "timelog",
                ParametersJson = "{\"year\":2024,\"month\":6}",
                CreatedBy = Guid.NewGuid(),
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            // Assert — query back all entities
            var timelogs = await context.TimelogProjections.ToListAsync();
            timelogs.Should().HaveCount(1);
            timelogs.First().Id.Should().Be(timelogId);

            var tasks = await context.TaskProjections.ToListAsync();
            tasks.Should().HaveCount(1);
            tasks.First().Subject.Should().Be("Implement Feature X");

            var projects = await context.ProjectProjections.ToListAsync();
            projects.Should().HaveCount(1);
            projects.First().Name.Should().Be("Project Alpha");

            var reports = await context.ReportDefinitions.ToListAsync();
            reports.Should().HaveCount(1);
            reports.First().ReportType.Should().Be("timelog");
        }

        #endregion
    }
}
