using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using WebVella.Erp.Service.Project.Database;
using Xunit;

namespace WebVella.Erp.Tests.Project.Database
{
    /// <summary>
    /// Tests for the Project service's EF Core migration system.
    /// Validates that the initial migration correctly creates all project-owned
    /// database tables, migration idempotency (running multiple times produces
    /// consistent state), migration reversibility, zero data loss per AAP 0.8.1,
    /// audit field preservation, and migration history tracking.
    ///
    /// All tests use Testcontainers.PostgreSql v4.10.0 for isolated PostgreSQL 16
    /// containers. Each test creates its own database within the shared container
    /// to ensure complete isolation.
    /// </summary>
    [Collection("Database")]
    public class MigrationTests : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _postgres;
        private string _connectionString;

        /// <summary>All expected entity record tables created by the initial migration.</summary>
        private static readonly string[] EntityTables =
        {
            "rec_task", "rec_timelog", "rec_comment", "rec_feed_item",
            "rec_project", "rec_task_type", "rec_task_status", "rec_milestone"
        };

        /// <summary>All expected M:N join/relation tables created by the initial migration.</summary>
        private static readonly string[] JoinTables =
        {
            "rel_project_nn_task", "rel_milestone_nn_task",
            "rel_project_nn_milestone", "rel_comment_nn_attachment"
        };

        /// <summary>Entity tables that must have created_on and created_by audit columns.</summary>
        private static readonly string[] AuditableTables =
        {
            "rec_task", "rec_timelog", "rec_comment", "rec_feed_item", "rec_project"
        };

        /// <summary>
        /// Initializes a new MigrationTests instance, configuring the PostgreSQL
        /// Testcontainer with the postgres:16-alpine image.
        /// </summary>
        public MigrationTests()
        {
            _postgres = new PostgreSqlBuilder("postgres:16-alpine")
                .Build();

            // Enable legacy timestamp behavior for Npgsql 6+ to avoid
            // DateTime kind mismatch exceptions when round-tripping timestamps.
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        }

        /// <summary>Starts the PostgreSQL Testcontainer and captures the connection string.</summary>
        public async Task InitializeAsync()
        {
            await _postgres.StartAsync();
            _connectionString = _postgres.GetConnectionString();
        }

        /// <summary>Stops and disposes the PostgreSQL Testcontainer.</summary>
        public async Task DisposeAsync()
        {
            await _postgres.DisposeAsync();
        }

        #region Helper Methods

        /// <summary>
        /// Creates a new isolated PostgreSQL database within the shared container
        /// and returns a fresh ProjectDbContext plus the connection string for
        /// raw SQL access. Each test calls this for complete isolation.
        /// </summary>
        private (ProjectDbContext Context, string ConnectionString) CreateFreshContext()
        {
            var dbName = "test_" + Guid.NewGuid().ToString("N").Substring(0, 20);

            using (var adminConn = new NpgsqlConnection(_connectionString))
            {
                adminConn.Open();
                using var createCmd = new NpgsqlCommand(
                    $"CREATE DATABASE \"{dbName}\"", adminConn);
                createCmd.ExecuteNonQuery();
            }

            var builder = new NpgsqlConnectionStringBuilder(_connectionString)
            {
                Database = dbName
            };
            var connStr = builder.ConnectionString;

            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseNpgsql(connStr)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

            return (new ProjectDbContext(options), connStr);
        }

        /// <summary>
        /// Creates a ProjectDbContext for an existing database connection string.
        /// Used when a test needs multiple contexts for the same database.
        /// </summary>
        private ProjectDbContext CreateDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseNpgsql(connectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            return new ProjectDbContext(options);
        }

        /// <summary>Queries information_schema to retrieve all public table names.</summary>
        private async Task<List<string>> GetTableNamesAsync(string connectionString)
        {
            var tables = new List<string>();
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT table_name FROM information_schema.tables " +
                "WHERE table_schema = 'public' ORDER BY table_name", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
            return tables;
        }

        /// <summary>Queries information_schema to retrieve column names and data types.</summary>
        private async Task<List<(string Name, string DataType)>> GetColumnsAsync(
            string connectionString, string tableName)
        {
            var columns = new List<(string Name, string DataType)>();
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT column_name, data_type FROM information_schema.columns " +
                "WHERE table_name = @table ORDER BY ordinal_position", conn);
            cmd.Parameters.AddWithValue("@table", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
            return columns;
        }

        /// <summary>Queries information_schema for table → column count mapping.</summary>
        private async Task<Dictionary<string, int>> GetTableColumnCountsAsync(
            string connectionString)
        {
            var counts = new Dictionary<string, int>();
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT table_name, COUNT(column_name)::int " +
                "FROM information_schema.columns WHERE table_schema = 'public' " +
                "GROUP BY table_name ORDER BY table_name", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                counts[reader.GetString(0)] = reader.GetInt32(1);
            }
            return counts;
        }

        /// <summary>Gets the row count for a specific table via raw SQL.</summary>
        private async Task<long> GetRecordCountAsync(string connectionString, string tableName)
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM \"{tableName}\"", conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        /// <summary>Retrieves primary key column names for a table via information_schema.</summary>
        private async Task<List<string>> GetPrimaryKeyColumnsAsync(
            string connectionString, string tableName)
        {
            var pkColumns = new List<string>();
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                @"SELECT kcu.column_name
                  FROM information_schema.table_constraints tc
                  JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                   AND tc.table_schema = kcu.table_schema
                  WHERE tc.constraint_type = 'PRIMARY KEY'
                    AND tc.table_name = @table
                  ORDER BY kcu.ordinal_position", conn);
            cmd.Parameters.AddWithValue("@table", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                pkColumns.Add(reader.GetString(0));
            }
            return pkColumns;
        }

        #endregion

        // ===================================================================
        // Phase 2: Initial Migration — Table Creation Tests
        // Per AAP: "Test that initial migration correctly creates all
        // project-owned database tables"
        // ===================================================================

        [Fact]
        public async Task InitialMigration_ShouldCreateAllProjectEntityTables()
        {
            // Arrange & Act
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            // Assert — verify all 8 entity tables
            var tables = await GetTableNamesAsync(connStr);
            foreach (var table in EntityTables)
            {
                tables.Should().Contain(table,
                    $"entity table '{table}' must exist after initial migration");
            }

            // Assert — verify all 4 join/relation tables
            foreach (var table in JoinTables)
            {
                tables.Should().Contain(table,
                    $"join table '{table}' must exist after initial migration");
            }

            // Assert — verify EF Core migrations history table
            tables.Should().Contain("__EFMigrationsHistory",
                "EF Core migrations history table must exist");
        }

        [Fact]
        public async Task InitialMigration_ShouldCreateRecTaskWithAllColumns()
        {
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            var columns = await GetColumnsAsync(connStr, "rec_task");
            var columnNames = columns.Select(c => c.Name).ToList();

            // Core columns from monolith entity definition
            var expectedColumns = new[]
            {
                "id", "subject", "body", "owner_id", "start_date", "target_date",
                "created_on", "created_by", "completed_on", "number", "parent_id",
                "status_id", "type_id", "priority", "x_nonbillable_hours",
                "x_billable_hours", "l_scope", "l_related_records", "x_search",
                "recurrence_id", "key",
                // Columns from patches 20190205 / 20190222 and later
                "estimated_minutes", "x_billable_minutes", "x_nonbillable_minutes",
                "timelog_started_on", "recurrence_template"
            };
            foreach (var col in expectedColumns)
            {
                columnNames.Should().Contain(col,
                    $"rec_task must have column '{col}'");
            }

            // Verify key column data types
            var typeMap = columns.ToDictionary(c => c.Name, c => c.DataType);
            typeMap["id"].Should().Be("uuid");
            typeMap["number"].Should().Be("bigint");
            typeMap["created_on"].Should().Be("timestamp with time zone");
            typeMap["owner_id"].Should().Be("uuid");
            typeMap["created_by"].Should().Be("uuid");
            typeMap["start_date"].Should().Be("timestamp with time zone");
            typeMap["x_nonbillable_hours"].Should().Be("numeric");
            typeMap["estimated_minutes"].Should().Be("numeric");
        }

        [Fact]
        public async Task InitialMigration_ShouldCreateRecTimelogWithAllColumns()
        {
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            var columns = await GetColumnsAsync(connStr, "rec_timelog");
            var columnNames = columns.Select(c => c.Name).ToList();

            var expected = new[]
            {
                "id", "body", "created_by", "created_on",
                "l_related_records", "l_scope", "minutes", "is_billable", "logged_on"
            };
            foreach (var col in expected)
            {
                columnNames.Should().Contain(col,
                    $"rec_timelog must have column '{col}'");
            }

            var typeMap = columns.ToDictionary(c => c.Name, c => c.DataType);
            typeMap["id"].Should().Be("uuid");
            typeMap["minutes"].Should().Be("numeric");
            typeMap["is_billable"].Should().Be("boolean");
            typeMap["created_on"].Should().Be("timestamp with time zone");
        }

        [Fact]
        public async Task InitialMigration_ShouldCreateRecCommentWithAllColumns()
        {
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            var columns = await GetColumnsAsync(connStr, "rec_comment");
            var columnNames = columns.Select(c => c.Name).ToList();

            var expected = new[]
            {
                "id", "body", "created_by", "created_on",
                "l_scope", "l_related_records", "parent_id"
            };
            foreach (var col in expected)
            {
                columnNames.Should().Contain(col,
                    $"rec_comment must have column '{col}'");
            }

            var typeMap = columns.ToDictionary(c => c.Name, c => c.DataType);
            typeMap["id"].Should().Be("uuid");
            typeMap["parent_id"].Should().Be("uuid");
            typeMap["created_on"].Should().Be("timestamp with time zone");
        }

        [Fact]
        public async Task InitialMigration_ShouldCreateRecFeedItemWithAllColumns()
        {
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            var columns = await GetColumnsAsync(connStr, "rec_feed_item");
            var columnNames = columns.Select(c => c.Name).ToList();

            var expected = new[]
            {
                "id", "created_by", "created_on", "l_scope",
                "subject", "body", "type", "l_related_records"
            };
            foreach (var col in expected)
            {
                columnNames.Should().Contain(col,
                    $"rec_feed_item must have column '{col}'");
            }

            var typeMap = columns.ToDictionary(c => c.Name, c => c.DataType);
            typeMap["id"].Should().Be("uuid");
            typeMap["type"].Should().Be("text");
            typeMap["created_on"].Should().Be("timestamp with time zone");
        }

        [Fact]
        public async Task InitialMigration_ShouldCreateJoinTablesWithCompositePrimaryKeys()
        {
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            foreach (var joinTable in JoinTables)
            {
                // Verify origin_id and target_id columns exist with uuid type
                var columns = await GetColumnsAsync(connStr, joinTable);
                var columnNames = columns.Select(c => c.Name).ToList();
                columnNames.Should().Contain("origin_id",
                    $"{joinTable} must have origin_id column");
                columnNames.Should().Contain("target_id",
                    $"{joinTable} must have target_id column");

                var typeMap = columns.ToDictionary(c => c.Name, c => c.DataType);
                typeMap["origin_id"].Should().Be("uuid");
                typeMap["target_id"].Should().Be("uuid");

                // Verify composite PK on (origin_id, target_id)
                var pkColumns = await GetPrimaryKeyColumnsAsync(connStr, joinTable);
                pkColumns.Should().HaveCount(2,
                    $"{joinTable} should have a composite primary key");
                pkColumns.Should().Contain("origin_id");
                pkColumns.Should().Contain("target_id");
            }
        }

        // ===================================================================
        // Phase 3: Migration Idempotency Tests
        // Per AAP: "Test migration idempotency — running migrations multiple
        // times produces consistent state"
        // ===================================================================

        [Fact]
        public async Task RunningMigrationsTwice_ShouldProduceConsistentState()
        {
            var (context1, connStr) = CreateFreshContext();

            // First migration run
            using (context1) { context1.Database.Migrate(); }
            var tablesAfterFirst = await GetTableNamesAsync(connStr);
            var countsAfterFirst = await GetTableColumnCountsAsync(connStr);

            // Second migration run — should be a no-op
            using (var context2 = CreateDbContext(connStr))
            {
                context2.Database.Migrate();
            }
            var tablesAfterSecond = await GetTableNamesAsync(connStr);
            var countsAfterSecond = await GetTableColumnCountsAsync(connStr);

            // Verify identical state
            tablesAfterSecond.Should().BeEquivalentTo(tablesAfterFirst,
                "tables must be identical after running Migrate() twice");
            countsAfterSecond.Should().BeEquivalentTo(countsAfterFirst,
                "column counts must be identical after running Migrate() twice");
        }

        [Fact]
        public async Task RunningMigrationsTwice_ShouldNotDuplicateMigrationHistory()
        {
            var (context1, connStr) = CreateFreshContext();
            using (context1) { context1.Database.Migrate(); }

            using (var context2 = CreateDbContext(connStr))
            {
                context2.Database.Migrate();
            }

            // Query for duplicate migration IDs
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT \"MigrationId\", COUNT(*) FROM \"__EFMigrationsHistory\" " +
                "GROUP BY \"MigrationId\" HAVING COUNT(*) > 1", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            var duplicates = new List<string>();
            while (await reader.ReadAsync())
            {
                duplicates.Add(reader.GetString(0));
            }

            duplicates.Should().BeEmpty(
                "each migration ID should appear exactly once in __EFMigrationsHistory");
        }

        [Fact]
        public async Task MigrationsApplied_ShouldMatchPendingMigrations()
        {
            var (context, connStr) = CreateFreshContext();
            using (context)
            {
                context.Database.Migrate();

                var applied = context.Database.GetAppliedMigrations().ToList();
                var pending = context.Database.GetPendingMigrations().ToList();

                applied.Should().NotBeEmpty(
                    "at least one migration should be applied");
                pending.Should().BeEmpty(
                    "no migrations should be pending after Migrate()");
            }
        }

        // ===================================================================
        // Phase 4: Migration Reversibility Tests
        // Per AAP: "Schema migration scripts must be idempotent and reversible"
        // ===================================================================

        [Fact]
        public async Task MigrationDown_ShouldDropCreatedTables()
        {
            var (context, connStr) = CreateFreshContext();
            using (context)
            {
                // Migrate up — tables should exist
                context.Database.Migrate();
            }

            var tablesAfterUp = await GetTableNamesAsync(connStr);
            foreach (var table in EntityTables.Concat(JoinTables))
            {
                tablesAfterUp.Should().Contain(table);
            }

            // Migrate down to "0" (revert all migrations)
            using (var context2 = CreateDbContext(connStr))
            {
                var migrator = context2.GetInfrastructure().GetRequiredService<IMigrator>();
                migrator.Migrate("0");
            }

            var tablesAfterDown = await GetTableNamesAsync(connStr);

            // All project entity and join tables should be dropped
            foreach (var table in EntityTables)
            {
                tablesAfterDown.Should().NotContain(table,
                    $"entity table '{table}' should be dropped after migration down");
            }
            foreach (var table in JoinTables)
            {
                tablesAfterDown.Should().NotContain(table,
                    $"join table '{table}' should be dropped after migration down");
            }

            // __EFMigrationsHistory should remain (EF Core manages this separately)
            tablesAfterDown.Should().Contain("__EFMigrationsHistory",
                "EF Core history table persists after migration down");
        }

        [Fact]
        public async Task MigrationUpAfterDown_ShouldRecreateAllTables()
        {
            var (context, connStr) = CreateFreshContext();

            // Migrate up
            using (context) { context.Database.Migrate(); }

            // Migrate down to "0"
            using (var ctx2 = CreateDbContext(connStr))
            {
                var migrator = ctx2.GetInfrastructure().GetRequiredService<IMigrator>();
                migrator.Migrate("0");
            }

            // Migrate up again
            using (var ctx3 = CreateDbContext(connStr))
            {
                ctx3.Database.Migrate();
            }

            // Verify all tables are recreated
            var tables = await GetTableNamesAsync(connStr);
            foreach (var table in EntityTables.Concat(JoinTables))
            {
                tables.Should().Contain(table,
                    $"table '{table}' must be recreated after up→down→up cycle");
            }

            // Verify rec_task still has all expected columns
            var taskColumns = await GetColumnsAsync(connStr, "rec_task");
            taskColumns.Select(c => c.Name).Should().Contain("id");
            taskColumns.Select(c => c.Name).Should().Contain("subject");
            taskColumns.Select(c => c.Name).Should().Contain("status_id");
        }

        // ===================================================================
        // Phase 5: Schema Migration Zero Data Loss Tests
        // Per AAP 0.8.1: "Schema migration scripts ensure zero data loss"
        // ===================================================================

        [Fact]
        public async Task MigrationPreservesInsertedData_AfterReapplication()
        {
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            // Insert test records into entity tables via raw SQL
            await using (var conn = new NpgsqlConnection(connStr))
            {
                await conn.OpenAsync();

                // Use a task status that exists from seed data
                var statusId = new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f");
                var typeId = new Guid("da9bf72d-3655-4c51-9f99-047ef9297bf2");
                var projectId = Guid.NewGuid();
                var taskId = Guid.NewGuid();

                await ExecuteNonQueryAsync(conn,
                    "INSERT INTO rec_project (id, name, abbr) VALUES (@id, @name, @abbr)",
                    new NpgsqlParameter("@id", projectId),
                    new NpgsqlParameter("@name", "Test Project"),
                    new NpgsqlParameter("@abbr", "TST"));

                await ExecuteNonQueryAsync(conn,
                    "INSERT INTO rec_task (id, subject, status_id, type_id) " +
                    "VALUES (@id, @subject, @sid, @tid)",
                    new NpgsqlParameter("@id", taskId),
                    new NpgsqlParameter("@subject", "Test Task"),
                    new NpgsqlParameter("@sid", statusId),
                    new NpgsqlParameter("@tid", typeId));

                await ExecuteNonQueryAsync(conn,
                    "INSERT INTO rec_timelog (id) VALUES (@id)",
                    new NpgsqlParameter("@id", Guid.NewGuid()));

                await ExecuteNonQueryAsync(conn,
                    "INSERT INTO rec_comment (id) VALUES (@id)",
                    new NpgsqlParameter("@id", Guid.NewGuid()));

                await ExecuteNonQueryAsync(conn,
                    "INSERT INTO rec_feed_item (id) VALUES (@id)",
                    new NpgsqlParameter("@id", Guid.NewGuid()));

                await ExecuteNonQueryAsync(conn,
                    "INSERT INTO rec_milestone (id, name) VALUES (@id, @name)",
                    new NpgsqlParameter("@id", Guid.NewGuid()),
                    new NpgsqlParameter("@name", "Milestone 1"));
            }

            // Record counts before re-running migrations
            var countsBefore = new Dictionary<string, long>();
            foreach (var table in EntityTables)
            {
                countsBefore[table] = await GetRecordCountAsync(connStr, table);
            }

            // Re-run migrations — should be a no-op preserving data
            using (var ctx2 = CreateDbContext(connStr))
            {
                ctx2.Database.Migrate();
            }

            // Verify counts unchanged — zero data loss
            foreach (var table in EntityTables)
            {
                var countAfter = await GetRecordCountAsync(connStr, table);
                countAfter.Should().Be(countsBefore[table],
                    $"record count in '{table}' must not change after re-running migrations");
            }
        }

        [Fact]
        public async Task AllFieldValues_ShouldSurviveMigration()
        {
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            var taskId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var createdBy = Guid.NewGuid();
            var statusId = new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f");
            var typeId = new Guid("da9bf72d-3655-4c51-9f99-047ef9297bf2");
            var now = DateTime.UtcNow;

            // Insert a task with all fields populated
            await using (var conn = new NpgsqlConnection(connStr))
            {
                await conn.OpenAsync();
                await ExecuteNonQueryAsync(conn,
                    @"INSERT INTO rec_task
                      (id, subject, body, owner_id, start_date, target_date,
                       created_on, created_by, completed_on, parent_id,
                       status_id, type_id, priority, x_nonbillable_hours,
                       x_billable_hours, l_scope, l_related_records, x_search,
                       recurrence_id, key, estimated_minutes,
                       x_billable_minutes, x_nonbillable_minutes)
                      VALUES
                      (@id, @subject, @body, @owner, @start, @target,
                       @created_on, @created_by, @completed, NULL,
                       @status, @type, @priority, @xnh,
                       @xbh, @scope, @rel, @search,
                       @recur, @key, @est,
                       @xbm, @xnm)",
                    new NpgsqlParameter("@id", taskId),
                    new NpgsqlParameter("@subject", "Full field test"),
                    new NpgsqlParameter("@body", "<p>HTML body</p>"),
                    new NpgsqlParameter("@owner", ownerId),
                    new NpgsqlParameter("@start", now),
                    new NpgsqlParameter("@target", now.AddDays(7)),
                    new NpgsqlParameter("@created_on", now),
                    new NpgsqlParameter("@created_by", createdBy),
                    new NpgsqlParameter("@completed", now.AddDays(5)),
                    new NpgsqlParameter("@status", statusId),
                    new NpgsqlParameter("@type", typeId),
                    new NpgsqlParameter("@priority", "2"),
                    new NpgsqlParameter("@xnh", 10.5m),
                    new NpgsqlParameter("@xbh", 20.75m),
                    new NpgsqlParameter("@scope", "[\"projects\"]"),
                    new NpgsqlParameter("@rel", "[\"tasks\"]"),
                    new NpgsqlParameter("@search", "full field test search"),
                    new NpgsqlParameter("@recur", Guid.NewGuid()),
                    new NpgsqlParameter("@key", "TST-1"),
                    new NpgsqlParameter("@est", 120m),
                    new NpgsqlParameter("@xbm", 45.5m),
                    new NpgsqlParameter("@xnm", 30.25m));
            }

            // Read back and verify every field matches
            await using (var conn = new NpgsqlConnection(connStr))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "SELECT subject, body, owner_id, priority, x_nonbillable_hours, " +
                    "x_billable_hours, l_scope, x_search, key, estimated_minutes, " +
                    "x_billable_minutes, x_nonbillable_minutes " +
                    "FROM rec_task WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("@id", taskId);
                await using var reader = await cmd.ExecuteReaderAsync();
                reader.Read().Should().BeTrue("inserted task record should be readable");

                reader.GetString(0).Should().Be("Full field test");
                reader.GetString(1).Should().Be("<p>HTML body</p>");
                reader.GetGuid(2).Should().Be(ownerId);
                reader.GetString(3).Should().Be("2");
                reader.GetDecimal(4).Should().Be(10.5m);
                reader.GetDecimal(5).Should().Be(20.75m);
                reader.GetString(6).Should().Be("[\"projects\"]");
                reader.GetString(7).Should().Be("full field test search");
                reader.GetString(8).Should().Be("TST-1");
                reader.GetDecimal(9).Should().Be(120m);
                reader.GetDecimal(10).Should().Be(45.5m);
                reader.GetDecimal(11).Should().Be(30.25m);
            }
        }

        // ===================================================================
        // Phase 6: Audit Field Preservation Tests
        // Per AAP 0.8.1: "Verify audit fields (created_on, created_by) are
        // preserved on all entity tables"
        // ===================================================================

        [Fact]
        public async Task AuditFields_ShouldBePreservedOnAllEntityTables()
        {
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            foreach (var table in AuditableTables)
            {
                var columns = await GetColumnsAsync(connStr, table);
                var typeMap = columns.ToDictionary(c => c.Name, c => c.DataType);

                typeMap.Should().ContainKey("created_on",
                    $"'{table}' must have 'created_on' audit column");
                typeMap["created_on"].Should().Be("timestamp with time zone",
                    $"'{table}'.created_on must be timestamp with time zone");

                typeMap.Should().ContainKey("created_by",
                    $"'{table}' must have 'created_by' audit column");
                typeMap["created_by"].Should().Be("uuid",
                    $"'{table}'.created_by must be uuid");
            }
        }

        [Fact]
        public async Task AuditFieldValues_ShouldRoundTripCorrectly()
        {
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            var taskId = Guid.NewGuid();
            var createdBy = Guid.NewGuid();
            // Use a specific UTC timestamp to verify precision
            var createdOn = new DateTime(2025, 6, 15, 14, 30, 45, DateTimeKind.Utc);

            await using (var conn = new NpgsqlConnection(connStr))
            {
                await conn.OpenAsync();
                await ExecuteNonQueryAsync(conn,
                    "INSERT INTO rec_task (id, subject, created_on, created_by) " +
                    "VALUES (@id, @subject, @created_on, @created_by)",
                    new NpgsqlParameter("@id", taskId),
                    new NpgsqlParameter("@subject", "Audit test"),
                    new NpgsqlParameter("@created_on", createdOn),
                    new NpgsqlParameter("@created_by", createdBy));
            }

            // Read back and verify exact values
            await using (var conn = new NpgsqlConnection(connStr))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "SELECT created_on, created_by FROM rec_task WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("@id", taskId);
                await using var reader = await cmd.ExecuteReaderAsync();
                reader.Read().Should().BeTrue();

                var readCreatedOn = reader.GetDateTime(0);
                var readCreatedBy = reader.GetGuid(1);

                readCreatedBy.Should().Be(createdBy,
                    "created_by UUID must round-trip exactly");
                readCreatedOn.Should().Be(createdOn,
                    "created_on timestamp must round-trip without truncation or timezone shift");
            }
        }

        // ===================================================================
        // Phase 7: Migration History Tracking Tests
        // Per AAP 0.7.5: "Each plugin's date-based versioning stored in
        // plugin_data must be adapted to per-service EF Core migrations"
        // ===================================================================

        [Fact]
        public async Task MigrationHistory_ShouldUseEFCoreMigrationsTable()
        {
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();

            // Verify __EFMigrationsHistory table exists with entries
            await using var cmd = new NpgsqlCommand(
                "SELECT \"MigrationId\", \"ProductVersion\" " +
                "FROM \"__EFMigrationsHistory\"", conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var entries = new List<(string MigrationId, string ProductVersion)>();
            while (await reader.ReadAsync())
            {
                entries.Add((reader.GetString(0), reader.GetString(1)));
            }

            entries.Should().NotBeEmpty(
                "__EFMigrationsHistory must have at least one entry after migration");

            // Verify product version indicates EF Core
            entries.All(e => !string.IsNullOrEmpty(e.ProductVersion))
                .Should().BeTrue(
                    "all migration entries must have a non-empty ProductVersion");
        }

        [Fact]
        public async Task NoPluginDataTable_ShouldExist()
        {
            var (context, connStr) = CreateFreshContext();
            using (context) { context.Database.Migrate(); }

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_name = 'plugin_data'", conn);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());

            count.Should().Be(0,
                "monolith's plugin_data table must NOT exist — " +
                "replaced by __EFMigrationsHistory per AAP 0.7.5");
        }

        // ===================================================================
        // Phase 8: EF Core Model Consistency Tests
        // ===================================================================

        [Fact]
        public async Task EFCoreModel_ShouldMatchPhysicalSchema()
        {
            var (context, connStr) = CreateFreshContext();
            using (context)
            {
                context.Database.Migrate();

                var entityTypes = context.Model.GetEntityTypes().ToList();
                entityTypes.Should().NotBeEmpty(
                    "EF Core model must have entity types defined");

                // 8 entity types + 4 join entity types = 12 total
                entityTypes.Should().HaveCount(12,
                    "model should have 8 entities + 4 join entities = 12 entity types");

                // Verify each entity type has a matching physical table
                var physicalTables = await GetTableNamesAsync(connStr);
                foreach (var entityType in entityTypes)
                {
                    var tableName = entityType.GetTableName();
                    tableName.Should().NotBeNull(
                        $"entity type '{entityType.Name}' must have a table mapping");
                    physicalTables.Should().Contain(tableName,
                        $"physical table '{tableName}' for entity '{entityType.Name}' must exist");
                }

                // Verify table naming follows monolith convention
                var mappedTableNames = entityTypes
                    .Select(e => e.GetTableName())
                    .Where(t => t != null)
                    .ToList();
                mappedTableNames.Where(t => t.StartsWith("rec_")).Count()
                    .Should().Be(8, "8 entity tables should use rec_ prefix");
                mappedTableNames.Where(t => t.StartsWith("rel_")).Count()
                    .Should().Be(4, "4 join tables should use rel_ prefix");
            }
        }

        [Fact]
        public async Task AllDbSets_ShouldBeQueryable()
        {
            var (context, connStr) = CreateFreshContext();
            using (context)
            {
                context.Database.Migrate();

                // Each DbSet should be queryable (returns empty list, not throws)
                var tasks = context.Tasks.ToList();
                tasks.Should().BeEmpty("no tasks inserted yet");

                var timelogs = context.Timelogs.ToList();
                timelogs.Should().BeEmpty("no timelogs inserted yet");

                var comments = context.Comments.ToList();
                comments.Should().BeEmpty("no comments inserted yet");

                var feedItems = context.FeedItems.ToList();
                feedItems.Should().BeEmpty("no feed items inserted yet");

                var projects = context.Projects.ToList();
                projects.Should().BeEmpty("no projects inserted yet");

                // TaskTypes has seed data from the migration
                var taskTypes = context.TaskTypes.ToList();
                taskTypes.Should().NotBeEmpty(
                    "task types should contain seed data from migration");
                taskTypes.Count.Should().BeGreaterThan(0);

                // TaskStatuses has seed data from the migration
                var taskStatuses = context.TaskStatuses.ToList();
                taskStatuses.Should().NotBeEmpty(
                    "task statuses should contain seed data from migration");
                taskStatuses.Count.Should().BeGreaterThan(0);

                var milestones = context.Milestones.ToList();
                milestones.Should().BeEmpty("no milestones inserted yet");
            }
        }

        #region Private SQL Helpers

        /// <summary>
        /// Executes a non-query SQL command with parameters on an open connection.
        /// </summary>
        private static async Task ExecuteNonQueryAsync(
            NpgsqlConnection conn, string sql, params NpgsqlParameter[] parameters)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(p);
            }
            await cmd.ExecuteNonQueryAsync();
        }

        #endregion
    }
}
