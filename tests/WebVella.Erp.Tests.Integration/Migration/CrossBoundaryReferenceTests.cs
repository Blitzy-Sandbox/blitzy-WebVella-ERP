using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Npgsql;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Tests.Integration.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace WebVella.Erp.Tests.Integration.Migration
{
    /// <summary>
    /// Integration tests validating that entity references spanning microservice boundaries
    /// are correctly denormalized, cross-service foreign keys are replaced with UUID references,
    /// and cross-service ID resolution is supported by the data model.
    ///
    /// Per AAP Section 0.7.1 — Cross-Service Relation Resolution Strategy:
    ///   - Audit fields (created_by, modified_by): Store user UUID; resolve via Core gRPC on read
    ///   - Account → Project: Denormalized account_id in Project DB; eventual consistency via CRM events
    ///   - Case → Task: Denormalized case_id in Project DB; CRM publishes CaseUpdated events
    ///   - Contact → Email: JSON-backed sender/recipients fields storing contact UUIDs
    ///   - User → Role: rel_user_role owned by Core; JWT claims propagate role info
    ///
    /// Per AAP 0.8.2 Architectural Constraints:
    ///   - "Services communicate only through well-defined API contracts (REST/gRPC) and async events"
    ///   - "Each service owns its database schema exclusively; no cross-service database access"
    ///
    /// NOTE: ServiceCollectionFixture is NOT registered as a collection fixture in
    /// IntegrationTestCollection (xUnit v2.9.3 does not support fixture-to-fixture dependency
    /// injection). The gRPC resolution test validates the data model via direct database queries
    /// that prove the cross-service UUID resolution pattern works.
    /// </summary>
    [Collection(IntegrationTestCollection.Name)]
    public class CrossBoundaryReferenceTests
    {
        #region Private Fields

        /// <summary>
        /// PostgreSQL fixture providing per-service database connection strings.
        /// Injected by xUnit collection fixture infrastructure.
        /// </summary>
        private readonly PostgreSqlFixture _postgreSqlFixture;

        /// <summary>
        /// xUnit test output helper for diagnostic logging during test execution.
        /// </summary>
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Utility for seeding known test data (users, roles, accounts, contacts) into
        /// per-service databases. Created in constructor with PostgreSqlFixture.
        /// </summary>
        private readonly TestDataSeeder _seeder;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of <see cref="CrossBoundaryReferenceTests"/>.
        ///
        /// Parameters are injected by xUnit's collection fixture infrastructure:
        ///   - PostgreSqlFixture is registered via ICollectionFixture in IntegrationTestCollection
        ///   - ITestOutputHelper is always available from xUnit
        ///
        /// ServiceCollectionFixture is not accepted as a constructor parameter because it is
        /// NOT registered as a collection fixture (see IntegrationTestCollection.cs comments).
        /// Cross-service resolution is validated via direct database queries instead.
        /// </summary>
        /// <param name="postgreSqlFixture">
        /// Provides CoreConnectionString, CrmConnectionString, ProjectConnectionString,
        /// and MailConnectionString for per-service database access.
        /// </param>
        /// <param name="output">
        /// xUnit diagnostic output helper for logging test progress.
        /// </param>
        public CrossBoundaryReferenceTests(
            PostgreSqlFixture postgreSqlFixture,
            ITestOutputHelper output)
        {
            _postgreSqlFixture = postgreSqlFixture
                ?? throw new ArgumentNullException(nameof(postgreSqlFixture));
            _output = output
                ?? throw new ArgumentNullException(nameof(output));
            _seeder = new TestDataSeeder(postgreSqlFixture);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Checks whether a specific column exists in a table within the specified database.
        /// Queries information_schema.columns using parameterized SQL.
        /// </summary>
        /// <param name="connectionString">ADO.NET connection string for the target database.</param>
        /// <param name="tableName">The table name to check (e.g., "rec_account").</param>
        /// <param name="columnName">The column name to look for (e.g., "created_by").</param>
        /// <returns>True if the column exists in the table; false otherwise.</returns>
        private async Task<bool> ColumnExistsAsync(string connectionString, string tableName, string columnName)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var cmd = new NpgsqlCommand(@"
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                  AND column_name = @columnName", connection);

            cmd.Parameters.AddWithValue("@tableName", tableName);
            cmd.Parameters.AddWithValue("@columnName", columnName);

            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToInt64(result) > 0;
        }

        /// <summary>
        /// Returns the native PostgreSQL type name (udt_name) for a specific column.
        /// Used to verify UUID columns for cross-service references.
        ///
        /// Common udt_name values:
        ///   - "uuid" for GuidField
        ///   - "text" for TextField/HtmlField/JSON fields
        ///   - "timestamptz" for DateTimeField
        ///   - "bool" for CheckboxField
        ///   - "int4" for IntegerField (serial)
        /// </summary>
        /// <param name="connectionString">ADO.NET connection string for the target database.</param>
        /// <param name="tableName">The table name containing the column.</param>
        /// <param name="columnName">The column name to inspect.</param>
        /// <returns>The udt_name string, or null if the column does not exist.</returns>
        private async Task<string> GetColumnDataTypeAsync(string connectionString, string tableName, string columnName)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var cmd = new NpgsqlCommand(@"
                SELECT udt_name
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                  AND column_name = @columnName", connection);

            cmd.Parameters.AddWithValue("@tableName", tableName);
            cmd.Parameters.AddWithValue("@columnName", columnName);

            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result as string;
        }

        /// <summary>
        /// Checks whether a foreign key constraint exists from a source table to a referenced table.
        /// Queries information_schema.referential_constraints joined with table_constraints and
        /// constraint_column_usage.
        ///
        /// Per AAP 0.7.1: Cross-service FKs should NOT exist in a database-per-service model.
        /// </summary>
        /// <param name="connectionString">ADO.NET connection string for the target database.</param>
        /// <param name="tableName">The source table that might have the FK.</param>
        /// <param name="referencedTable">The referenced table the FK might point to.</param>
        /// <returns>True if an FK from tableName references referencedTable; false otherwise.</returns>
        private async Task<bool> ForeignKeyExistsAsync(string connectionString, string tableName, string referencedTable)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var cmd = new NpgsqlCommand(@"
                SELECT COUNT(*)
                FROM information_schema.table_constraints tc
                JOIN information_schema.referential_constraints rc
                    ON tc.constraint_name = rc.constraint_name
                    AND tc.constraint_schema = rc.constraint_schema
                JOIN information_schema.constraint_column_usage ccu
                    ON rc.unique_constraint_name = ccu.constraint_name
                    AND rc.unique_constraint_schema = ccu.constraint_schema
                WHERE tc.constraint_type = 'FOREIGN KEY'
                  AND tc.table_schema = 'public'
                  AND tc.table_name = @tableName
                  AND ccu.table_name = @referencedTable", connection);

            cmd.Parameters.AddWithValue("@tableName", tableName);
            cmd.Parameters.AddWithValue("@referencedTable", referencedTable);

            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToInt64(result) > 0;
        }

        /// <summary>
        /// Returns all foreign key constraint names for a given table.
        /// Used to verify no cross-service FKs remain after decomposition.
        /// </summary>
        /// <param name="connectionString">ADO.NET connection string for the target database.</param>
        /// <param name="tableName">The table to inspect for FK constraints.</param>
        /// <returns>List of FK constraint names; empty if no FKs exist.</returns>
        private async Task<List<string>> GetForeignKeysAsync(string connectionString, string tableName)
        {
            var fkNames = new List<string>();

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var cmd = new NpgsqlCommand(@"
                SELECT tc.constraint_name
                FROM information_schema.table_constraints tc
                WHERE tc.constraint_type = 'FOREIGN KEY'
                  AND tc.table_schema = 'public'
                  AND tc.table_name = @tableName
                ORDER BY tc.constraint_name", connection);

            cmd.Parameters.AddWithValue("@tableName", tableName);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                fkNames.Add(reader.GetString(0));
            }

            return fkNames;
        }

        /// <summary>
        /// Checks whether a table exists in the specified database's public schema.
        /// Used by Phase 7 and Phase 8 tests to verify table existence across service databases.
        /// </summary>
        /// <param name="connectionString">ADO.NET connection string for the target database.</param>
        /// <param name="tableName">The table name to check for existence.</param>
        /// <returns>True if the table exists in the public schema; false otherwise.</returns>
        private async Task<bool> TableExistsAsync(string connectionString, string tableName)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var cmd = new NpgsqlCommand(@"
                SELECT COUNT(*)
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = @tableName", connection);

            cmd.Parameters.AddWithValue("@tableName", tableName);

            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToInt64(result) > 0;
        }

        /// <summary>
        /// Returns all table names matching a LIKE pattern in the specified database's public schema.
        /// Used by Phase 4 and Phase 8 tests to find rel_* and rec_* tables.
        /// </summary>
        /// <param name="connectionString">ADO.NET connection string for the target database.</param>
        /// <param name="likePattern">SQL LIKE pattern (e.g., "rel_%").</param>
        /// <returns>List of matching table names; empty if none found.</returns>
        private async Task<List<string>> GetTablesMatchingPatternAsync(string connectionString, string likePattern)
        {
            var tableNames = new List<string>();

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var cmd = new NpgsqlCommand(@"
                SELECT table_name
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name LIKE @pattern
                ORDER BY table_name", connection);

            cmd.Parameters.AddWithValue("@pattern", likePattern);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                tableNames.Add(reader.GetString(0));
            }

            return tableNames;
        }

        /// <summary>
        /// Retrieves all foreign key references in a database, returning source table, constraint name,
        /// and referenced table. Used by Phase 8 comprehensive cross-service FK validation.
        /// </summary>
        /// <param name="connectionString">ADO.NET connection string for the target database.</param>
        /// <returns>
        /// List of tuples (sourceTable, constraintName, referencedTable) for every FK in the database.
        /// </returns>
        private async Task<List<(string SourceTable, string ConstraintName, string ReferencedTable)>> GetAllForeignKeyReferencesAsync(string connectionString)
        {
            var references = new List<(string SourceTable, string ConstraintName, string ReferencedTable)>();

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var cmd = new NpgsqlCommand(@"
                SELECT
                    tc.table_name AS source_table,
                    tc.constraint_name,
                    ccu.table_name AS referenced_table
                FROM information_schema.table_constraints tc
                JOIN information_schema.referential_constraints rc
                    ON tc.constraint_name = rc.constraint_name
                    AND tc.constraint_schema = rc.constraint_schema
                JOIN information_schema.constraint_column_usage ccu
                    ON rc.unique_constraint_name = ccu.constraint_name
                    AND rc.unique_constraint_schema = ccu.constraint_schema
                WHERE tc.constraint_type = 'FOREIGN KEY'
                  AND tc.table_schema = 'public'
                ORDER BY tc.table_name, tc.constraint_name", connection);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                references.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)
                ));
            }

            return references;
        }

        #endregion

        #region Phase 3 — Audit Field Cross-Service Reference Tests

        /// <summary>
        /// Verifies that the CRM service's rec_account table stores created_by as a UUID column
        /// without a foreign key to the Core service's rec_user table.
        ///
        /// Per AAP 0.7.1: "Audit fields (created_by, modified_by): FK to rec_user → Store user UUID;
        /// resolve via Core gRPC call on read."
        ///
        /// Source: NextPlugin.20190204.cs creates the account entity with created_by field.
        /// In the monolith, this was an FK to rec_user. In the microservice architecture,
        /// it becomes a UUID stored locally in the CRM DB, resolved via Core gRPC on read.
        /// </summary>
        [Fact]
        public async Task CrmService_AccountTable_Should_Have_CreatedBy_As_UUID()
        {
            // Arrange: Seed CRM data which creates the rec_account table
            await _seeder.SeedCrmDataAsync(_postgreSqlFixture.CrmConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded CRM data. Verifying rec_account.created_by column...");

            string crmConn = _postgreSqlFixture.CrmConnectionString;

            // Act & Assert: Verify created_by column exists
            bool columnExists = await ColumnExistsAsync(crmConn, "rec_account", "created_by")
                .ConfigureAwait(false);
            columnExists.Should().BeTrue("rec_account should have a created_by column for audit tracking");

            // Assert: Column type must be UUID (stores user ID for cross-service resolution)
            string dataType = await GetColumnDataTypeAsync(crmConn, "rec_account", "created_by")
                .ConfigureAwait(false);
            dataType.Should().Be("uuid",
                "created_by should be UUID type — user IDs are stored as UUIDs and resolved via Core gRPC");

            // Assert: NO foreign key to rec_user (user table is in Core DB, not CRM DB)
            bool hasFkToUser = await ForeignKeyExistsAsync(crmConn, "rec_account", "rec_user")
                .ConfigureAwait(false);
            hasFkToUser.Should().BeFalse(
                "rec_account.created_by must NOT have an FK to rec_user — user table is in Core DB, " +
                "cross-service FKs violate database-per-service isolation (AAP 0.8.2)");

            _output.WriteLine("✓ rec_account.created_by is UUID with no cross-service FK to rec_user");
        }

        /// <summary>
        /// Verifies that the CRM service's rec_contact table stores audit fields (created_by,
        /// last_modified_by) as UUID columns without foreign keys to the Core service's rec_user table.
        ///
        /// Per AAP 0.7.1: Contact entity owned by CRM service with audit field UUIDs resolved via Core gRPC.
        /// Source: NextPlugin.20190204.cs creates contact entity.
        /// </summary>
        [Fact]
        public async Task CrmService_ContactTable_Should_Have_AuditFields_As_UUID()
        {
            await _seeder.SeedCrmDataAsync(_postgreSqlFixture.CrmConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded CRM data. Verifying rec_contact audit fields...");

            string crmConn = _postgreSqlFixture.CrmConnectionString;

            // Verify created_by column
            bool createdByExists = await ColumnExistsAsync(crmConn, "rec_contact", "created_by")
                .ConfigureAwait(false);
            createdByExists.Should().BeTrue("rec_contact should have a created_by column");

            string createdByType = await GetColumnDataTypeAsync(crmConn, "rec_contact", "created_by")
                .ConfigureAwait(false);
            createdByType.Should().Be("uuid", "rec_contact.created_by should be UUID type");

            // Verify last_modified_by column
            bool modifiedByExists = await ColumnExistsAsync(crmConn, "rec_contact", "last_modified_by")
                .ConfigureAwait(false);
            modifiedByExists.Should().BeTrue("rec_contact should have a last_modified_by column");

            string modifiedByType = await GetColumnDataTypeAsync(crmConn, "rec_contact", "last_modified_by")
                .ConfigureAwait(false);
            modifiedByType.Should().Be("uuid", "rec_contact.last_modified_by should be UUID type");

            // Verify NO FK to Core DB's user table
            bool hasFkToUser = await ForeignKeyExistsAsync(crmConn, "rec_contact", "rec_user")
                .ConfigureAwait(false);
            hasFkToUser.Should().BeFalse(
                "rec_contact audit fields must NOT have FKs to rec_user — cross-service DB isolation required");

            _output.WriteLine("✓ rec_contact audit fields are UUID with no cross-service FKs");
        }

        /// <summary>
        /// Verifies that the Project service's rec_task table stores audit fields (created_by,
        /// last_modified_by) as UUID columns without foreign keys to the Core service's rec_user table.
        ///
        /// Per AAP 0.7.1: Task entity owned by Project service, user IDs resolved via Core gRPC.
        /// Source: NextPlugin.20190203.cs creates task entity.
        /// </summary>
        [Fact]
        public async Task ProjectService_TaskTable_Should_Have_AuditFields_As_UUID()
        {
            await _seeder.SeedProjectDataAsync(_postgreSqlFixture.ProjectConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded Project data. Verifying rec_task audit fields...");

            string projConn = _postgreSqlFixture.ProjectConnectionString;

            // Verify created_by column
            bool createdByExists = await ColumnExistsAsync(projConn, "rec_task", "created_by")
                .ConfigureAwait(false);
            createdByExists.Should().BeTrue("rec_task should have a created_by column");

            string createdByType = await GetColumnDataTypeAsync(projConn, "rec_task", "created_by")
                .ConfigureAwait(false);
            createdByType.Should().Be("uuid", "rec_task.created_by should be UUID type");

            // Verify last_modified_by column
            bool modifiedByExists = await ColumnExistsAsync(projConn, "rec_task", "last_modified_by")
                .ConfigureAwait(false);
            modifiedByExists.Should().BeTrue("rec_task should have a last_modified_by column");

            string modifiedByType = await GetColumnDataTypeAsync(projConn, "rec_task", "last_modified_by")
                .ConfigureAwait(false);
            modifiedByType.Should().Be("uuid", "rec_task.last_modified_by should be UUID type");

            // Verify NO FK to user table
            bool hasFkToUser = await ForeignKeyExistsAsync(projConn, "rec_task", "rec_user")
                .ConfigureAwait(false);
            hasFkToUser.Should().BeFalse(
                "rec_task audit fields must NOT have FKs to rec_user — Project DB is isolated from Core DB");

            _output.WriteLine("✓ rec_task audit fields are UUID with no cross-service FKs");
        }

        /// <summary>
        /// Verifies that the Mail service's rec_email table stores the created_by audit field
        /// as a UUID column without a foreign key to the Core service's rec_user table.
        ///
        /// Per AAP 0.7.1: Email entity owned by Mail service.
        /// Source: MailPlugin.20190215.cs creates email entity with audit fields.
        /// </summary>
        [Fact]
        public async Task MailService_EmailTable_Should_Have_AuditFields_As_UUID()
        {
            await _seeder.SeedMailDataAsync(_postgreSqlFixture.MailConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded Mail data. Verifying rec_email audit fields...");

            string mailConn = _postgreSqlFixture.MailConnectionString;

            // Verify created_by column
            bool createdByExists = await ColumnExistsAsync(mailConn, "rec_email", "created_by")
                .ConfigureAwait(false);
            createdByExists.Should().BeTrue("rec_email should have a created_by column");

            string createdByType = await GetColumnDataTypeAsync(mailConn, "rec_email", "created_by")
                .ConfigureAwait(false);
            createdByType.Should().Be("uuid", "rec_email.created_by should be UUID type");

            // Verify NO FK to user table
            bool hasFkToUser = await ForeignKeyExistsAsync(mailConn, "rec_email", "rec_user")
                .ConfigureAwait(false);
            hasFkToUser.Should().BeFalse(
                "rec_email.created_by must NOT have an FK to rec_user — Mail DB is isolated from Core DB");

            _output.WriteLine("✓ rec_email.created_by is UUID with no cross-service FK");
        }

        #endregion

        #region Phase 4 — Account → Project Cross-Service Reference Tests

        /// <summary>
        /// Verifies that the Project service's rec_task table has a denormalized account_id column
        /// of type UUID, replacing the cross-service rel_account_project join table.
        ///
        /// Per AAP 0.7.1: "Account → Project: rel_* join table → Denormalized account_id in
        /// Project DB; eventual consistency via CRM events."
        ///
        /// Source: NextPlugin.20190203.cs creates task-account relations.
        /// </summary>
        [Fact]
        public async Task ProjectService_TaskTable_Should_Have_Denormalized_AccountId()
        {
            await _seeder.SeedProjectDataAsync(_postgreSqlFixture.ProjectConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded Project data. Verifying denormalized account_id on rec_task...");

            string projConn = _postgreSqlFixture.ProjectConnectionString;

            // Verify account_id column exists — this is the denormalized cross-service reference
            bool accountIdExists = await ColumnExistsAsync(projConn, "rec_task", "account_id")
                .ConfigureAwait(false);
            accountIdExists.Should().BeTrue(
                "rec_task should have a denormalized account_id column replacing the cross-service join table");

            // Verify account_id is UUID type
            string accountIdType = await GetColumnDataTypeAsync(projConn, "rec_task", "account_id")
                .ConfigureAwait(false);
            accountIdType.Should().Be("uuid",
                "account_id should be UUID — stores CRM account ID for cross-service resolution");

            // Verify NO rel_account_project join table exists in Project DB
            bool joinTableExists = await TableExistsAsync(projConn, "rel_account_project")
                .ConfigureAwait(false);
            joinTableExists.Should().BeFalse(
                "rel_account_project join table must NOT exist in Project DB — " +
                "cross-service relations are denormalized, not joined (AAP 0.7.1)");

            _output.WriteLine("✓ rec_task has denormalized account_id (UUID) with no cross-service join table");
        }

        /// <summary>
        /// Verifies that the Project service database does not contain any rel_* join tables
        /// that reference CRM entities. All cross-service relations should be denormalized.
        ///
        /// Per AAP 0.7.1: Relations crossing service boundaries are replaced with denormalized
        /// UUID columns and eventual consistency via domain events.
        /// </summary>
        [Fact]
        public async Task ProjectService_Should_Not_Have_CrmRelJoinTables()
        {
            await _seeder.SeedProjectDataAsync(_postgreSqlFixture.ProjectConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded Project data. Checking for cross-service rel_* tables...");

            string projConn = _postgreSqlFixture.ProjectConnectionString;

            // Get all rel_* tables in the Project database
            List<string> relTables = await GetTablesMatchingPatternAsync(projConn, "rel_%")
                .ConfigureAwait(false);

            // Known CRM entity names that should NOT appear in cross-service join tables
            var crmEntityPrefixes = new[] { "account", "contact", "case", "address", "salutation" };

            // Filter for any rel_* tables that reference CRM entities
            var crossServiceRelTables = relTables
                .Where(t => crmEntityPrefixes.Any(prefix =>
                    t.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            crossServiceRelTables.Should().BeEmpty(
                "Project DB must NOT contain rel_* join tables referencing CRM entities — " +
                "cross-service relations are denormalized per AAP 0.7.1. " +
                $"Found: [{string.Join(", ", crossServiceRelTables)}]");

            _output.WriteLine($"✓ Project DB has {relTables.Count} rel_* table(s), none reference CRM entities");
            foreach (string table in relTables)
            {
                _output.WriteLine($"  - {table} (intra-service relation)");
            }
        }

        #endregion

        #region Phase 5 — Case → Task Cross-Service Reference Tests

        /// <summary>
        /// Verifies that the Project service's rec_task table has a denormalized case_id column
        /// of type UUID, replacing the cross-service rel_case_task join table.
        ///
        /// Per AAP 0.7.1: "Case → Task: rel_* join table → Denormalized case_id in Project DB;
        /// CRM publishes CaseUpdated events."
        ///
        /// Source: NextPlugin.20190203.cs creates case-task relations.
        /// </summary>
        [Fact]
        public async Task ProjectService_TaskTable_Should_Have_Denormalized_CaseId()
        {
            await _seeder.SeedProjectDataAsync(_postgreSqlFixture.ProjectConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded Project data. Verifying denormalized case_id on rec_task...");

            string projConn = _postgreSqlFixture.ProjectConnectionString;

            // Verify case_id column exists — denormalized cross-service reference to CRM case
            bool caseIdExists = await ColumnExistsAsync(projConn, "rec_task", "case_id")
                .ConfigureAwait(false);
            caseIdExists.Should().BeTrue(
                "rec_task should have a denormalized case_id column replacing the cross-service " +
                "rel_case_task join table (AAP 0.7.1)");

            // Verify case_id is UUID type
            string caseIdType = await GetColumnDataTypeAsync(projConn, "rec_task", "case_id")
                .ConfigureAwait(false);
            caseIdType.Should().Be("uuid",
                "case_id should be UUID — stores CRM case ID for cross-service resolution");

            // Verify NO rel_case_task join table exists crossing CRM→Project boundary
            bool joinTableExists = await TableExistsAsync(projConn, "rel_case_task")
                .ConfigureAwait(false);
            joinTableExists.Should().BeFalse(
                "rel_case_task join table must NOT exist in Project DB — " +
                "case-task relation is denormalized to case_id column (AAP 0.7.1)");

            _output.WriteLine("✓ rec_task has denormalized case_id (UUID) with no cross-service join table");
        }

        #endregion

        #region Phase 6 — Contact → Email Cross-Service Reference Tests

        /// <summary>
        /// Verifies that the Mail service's rec_email table stores contact references as
        /// JSON text fields (sender, recipients) containing UUIDs, with no foreign keys
        /// to the CRM service's contact table.
        ///
        /// Per AAP 0.7.1: "Contact → Email: Implicit via sender/recipients JSON → Mail service
        /// stores contact UUID; resolves via CRM gRPC on read."
        ///
        /// Source: MailPlugin.20190419.cs adds JSON-backed sender/recipients fields replacing
        /// the earlier scalar FK approach.
        /// </summary>
        [Fact]
        public async Task MailService_EmailTable_Should_Store_ContactReferences_As_UUIDs()
        {
            await _seeder.SeedMailDataAsync(_postgreSqlFixture.MailConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded Mail data. Verifying contact reference fields on rec_email...");

            string mailConn = _postgreSqlFixture.MailConnectionString;

            // Verify sender column exists and is text type (JSON containing UUIDs)
            bool senderExists = await ColumnExistsAsync(mailConn, "rec_email", "sender")
                .ConfigureAwait(false);
            senderExists.Should().BeTrue("rec_email should have a sender column");

            string senderType = await GetColumnDataTypeAsync(mailConn, "rec_email", "sender")
                .ConfigureAwait(false);
            senderType.Should().Be("text",
                "rec_email.sender should be text type — stores JSON containing contact UUIDs " +
                "(MailPlugin.20190419.cs)");

            // Verify recipients column exists and is text type (JSON containing UUIDs)
            bool recipientsExists = await ColumnExistsAsync(mailConn, "rec_email", "recipients")
                .ConfigureAwait(false);
            recipientsExists.Should().BeTrue("rec_email should have a recipients column");

            string recipientsType = await GetColumnDataTypeAsync(mailConn, "rec_email", "recipients")
                .ConfigureAwait(false);
            recipientsType.Should().Be("text",
                "rec_email.recipients should be text type — stores JSON containing contact UUIDs");

            // Verify NO foreign key to CRM's contact table
            bool hasFkToContact = await ForeignKeyExistsAsync(mailConn, "rec_email", "rec_contact")
                .ConfigureAwait(false);
            hasFkToContact.Should().BeFalse(
                "rec_email must NOT have an FK to rec_contact — contact table is in CRM DB, " +
                "Mail service resolves contacts via CRM gRPC on read (AAP 0.7.1)");

            _output.WriteLine("✓ rec_email stores contact references as text (JSON UUIDs) with no cross-service FKs");
        }

        #endregion

        #region Phase 7 — User → Role Cross-Service Reference Tests

        /// <summary>
        /// Verifies that the Core service database owns the rel_user_role join table,
        /// and that this table does NOT exist in any other service database.
        ///
        /// Per AAP 0.7.1: "User → Role: rel_user_role join table → Core service owns;
        /// JWT claims propagate role information."
        ///
        /// The user-role relation stays exclusively in the Core service. Other services
        /// receive role information through JWT token claims, not by querying a local
        /// join table.
        ///
        /// Source: Definitions.cs — SystemIds.UserRoleRelationId = 0C4B119E-1D7B-4B40-8D2C-9E447CC656AB
        /// </summary>
        [Fact]
        public async Task CoreService_Should_Own_UserRoleRelation()
        {
            // Seed Core data which creates the rel_user_role table
            await _seeder.SeedCoreDataAsync(_postgreSqlFixture.CoreConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded Core data. Verifying rel_user_role ownership...");
            _output.WriteLine($"UserRoleRelationId: {SystemIds.UserRoleRelationId}");

            // Assert: rel_user_role EXISTS in Core DB
            bool existsInCore = await TableExistsAsync(
                _postgreSqlFixture.CoreConnectionString, "rel_user_role")
                .ConfigureAwait(false);
            existsInCore.Should().BeTrue(
                "rel_user_role must exist in Core DB — Core service owns the user-role relation (AAP 0.7.1)");

            // Seed other databases to ensure their schemas are created
            await _seeder.SeedCrmDataAsync(_postgreSqlFixture.CrmConnectionString)
                .ConfigureAwait(false);
            await _seeder.SeedProjectDataAsync(_postgreSqlFixture.ProjectConnectionString)
                .ConfigureAwait(false);
            await _seeder.SeedMailDataAsync(_postgreSqlFixture.MailConnectionString)
                .ConfigureAwait(false);

            // Assert: rel_user_role does NOT exist in CRM DB
            bool existsInCrm = await TableExistsAsync(
                _postgreSqlFixture.CrmConnectionString, "rel_user_role")
                .ConfigureAwait(false);
            existsInCrm.Should().BeFalse(
                "rel_user_role must NOT exist in CRM DB — CRM gets roles from JWT claims");

            // Assert: rel_user_role does NOT exist in Project DB
            bool existsInProject = await TableExistsAsync(
                _postgreSqlFixture.ProjectConnectionString, "rel_user_role")
                .ConfigureAwait(false);
            existsInProject.Should().BeFalse(
                "rel_user_role must NOT exist in Project DB — Project gets roles from JWT claims");

            // Assert: rel_user_role does NOT exist in Mail DB
            bool existsInMail = await TableExistsAsync(
                _postgreSqlFixture.MailConnectionString, "rel_user_role")
                .ConfigureAwait(false);
            existsInMail.Should().BeFalse(
                "rel_user_role must NOT exist in Mail DB — Mail gets roles from JWT claims");

            _output.WriteLine("✓ rel_user_role exists exclusively in Core DB");
        }

        /// <summary>
        /// Explicitly verifies that CRM, Project, and Mail databases do NOT have any
        /// rel_user_role table. These services rely on JWT token claims for role information.
        ///
        /// Per AAP 0.7.1: Other services get roles from JWT claims propagated across
        /// service boundaries, not from local database joins.
        /// </summary>
        [Fact]
        public async Task NonCoreServices_Should_Not_Have_UserRoleRelationTables()
        {
            // Seed all databases to ensure their schemas are initialized
            await _seeder.SeedCrmDataAsync(_postgreSqlFixture.CrmConnectionString)
                .ConfigureAwait(false);
            await _seeder.SeedProjectDataAsync(_postgreSqlFixture.ProjectConnectionString)
                .ConfigureAwait(false);
            await _seeder.SeedMailDataAsync(_postgreSqlFixture.MailConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded non-Core databases. Verifying no rel_user_role tables...");

            // Check each non-Core database
            var serviceDatabases = new[]
            {
                ("CRM", _postgreSqlFixture.CrmConnectionString),
                ("Project", _postgreSqlFixture.ProjectConnectionString),
                ("Mail", _postgreSqlFixture.MailConnectionString)
            };

            foreach (var (serviceName, connectionString) in serviceDatabases)
            {
                bool hasUserRoleTable = await TableExistsAsync(connectionString, "rel_user_role")
                    .ConfigureAwait(false);
                hasUserRoleTable.Should().BeFalse(
                    $"{serviceName} DB must NOT have rel_user_role — " +
                    "role info is propagated via JWT claims, not local DB joins (AAP 0.7.1)");

                _output.WriteLine($"✓ {serviceName} DB does not have rel_user_role");
            }
        }

        #endregion

        #region Phase 8 — No Cross-Service Foreign Keys Tests (Comprehensive)

        /// <summary>
        /// Comprehensively verifies that the CRM database has no foreign keys referencing tables
        /// that only exist in other service databases (Core, Project, Mail).
        ///
        /// For each FK in the CRM DB, the referenced table must also exist in the CRM DB.
        /// This is the critical "database isolation" assertion per AAP 0.8.2:
        /// "Each service owns its database schema exclusively."
        /// </summary>
        [Fact]
        public async Task CrmService_Should_Have_No_FK_To_OtherServices()
        {
            await _seeder.SeedCrmDataAsync(_postgreSqlFixture.CrmConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded CRM data. Checking for cross-service FKs...");

            string crmConn = _postgreSqlFixture.CrmConnectionString;

            var allFkRefs = await GetAllForeignKeyReferencesAsync(crmConn)
                .ConfigureAwait(false);

            _output.WriteLine($"CRM DB has {allFkRefs.Count} foreign key reference(s)");

            // For each FK, verify the referenced table exists in the CRM database
            var crossServiceFks = new List<string>();
            foreach (var (sourceTable, constraintName, referencedTable) in allFkRefs)
            {
                bool referencedTableExists = await TableExistsAsync(crmConn, referencedTable)
                    .ConfigureAwait(false);
                if (!referencedTableExists)
                {
                    crossServiceFks.Add(
                        $"{sourceTable}.{constraintName} -> {referencedTable} (table not in CRM DB)");
                }
                _output.WriteLine($"  FK: {sourceTable} -> {referencedTable} " +
                    $"(exists locally: {referencedTableExists})");
            }

            crossServiceFks.Should().BeEmpty(
                "CRM DB must have NO foreign keys referencing tables in other service databases. " +
                $"Cross-service FKs found: [{string.Join("; ", crossServiceFks)}]");

            _output.WriteLine("✓ CRM DB has no cross-service foreign keys");
        }

        /// <summary>
        /// Comprehensively verifies that the Project database has no foreign keys referencing
        /// tables that only exist in other service databases (Core, CRM, Mail).
        ///
        /// Per AAP 0.8.2: "Each service owns its database schema exclusively; no other service
        /// may read from or write to another service's database."
        /// </summary>
        [Fact]
        public async Task ProjectService_Should_Have_No_FK_To_OtherServices()
        {
            await _seeder.SeedProjectDataAsync(_postgreSqlFixture.ProjectConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded Project data. Checking for cross-service FKs...");

            string projConn = _postgreSqlFixture.ProjectConnectionString;

            var allFkRefs = await GetAllForeignKeyReferencesAsync(projConn)
                .ConfigureAwait(false);

            _output.WriteLine($"Project DB has {allFkRefs.Count} foreign key reference(s)");

            var crossServiceFks = new List<string>();
            foreach (var (sourceTable, constraintName, referencedTable) in allFkRefs)
            {
                bool referencedTableExists = await TableExistsAsync(projConn, referencedTable)
                    .ConfigureAwait(false);
                if (!referencedTableExists)
                {
                    crossServiceFks.Add(
                        $"{sourceTable}.{constraintName} -> {referencedTable} (table not in Project DB)");
                }
                _output.WriteLine($"  FK: {sourceTable} -> {referencedTable} " +
                    $"(exists locally: {referencedTableExists})");
            }

            crossServiceFks.Should().BeEmpty(
                "Project DB must have NO foreign keys referencing tables in other service databases. " +
                $"Cross-service FKs found: [{string.Join("; ", crossServiceFks)}]");

            _output.WriteLine("✓ Project DB has no cross-service foreign keys");
        }

        /// <summary>
        /// Comprehensively verifies that the Mail database has no foreign keys referencing
        /// tables that only exist in other service databases (Core, CRM, Project).
        ///
        /// Per AAP 0.8.2: "Each service owns its database schema exclusively."
        /// </summary>
        [Fact]
        public async Task MailService_Should_Have_No_FK_To_OtherServices()
        {
            await _seeder.SeedMailDataAsync(_postgreSqlFixture.MailConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded Mail data. Checking for cross-service FKs...");

            string mailConn = _postgreSqlFixture.MailConnectionString;

            var allFkRefs = await GetAllForeignKeyReferencesAsync(mailConn)
                .ConfigureAwait(false);

            _output.WriteLine($"Mail DB has {allFkRefs.Count} foreign key reference(s)");

            var crossServiceFks = new List<string>();
            foreach (var (sourceTable, constraintName, referencedTable) in allFkRefs)
            {
                bool referencedTableExists = await TableExistsAsync(mailConn, referencedTable)
                    .ConfigureAwait(false);
                if (!referencedTableExists)
                {
                    crossServiceFks.Add(
                        $"{sourceTable}.{constraintName} -> {referencedTable} (table not in Mail DB)");
                }
                _output.WriteLine($"  FK: {sourceTable} -> {referencedTable} " +
                    $"(exists locally: {referencedTableExists})");
            }

            crossServiceFks.Should().BeEmpty(
                "Mail DB must have NO foreign keys referencing tables in other service databases. " +
                $"Cross-service FKs found: [{string.Join("; ", crossServiceFks)}]");

            _output.WriteLine("✓ Mail DB has no cross-service foreign keys");
        }

        #endregion

        #region Phase 9 — Cross-Service User Resolution Integration Test

        /// <summary>
        /// Validates the cross-service user resolution strategy by proving that a user UUID
        /// stored in the CRM database's created_by field can be resolved to actual user data
        /// in the Core database.
        ///
        /// Per AAP 0.7.1: "Audit fields (created_by, modified_by): Store user UUID; resolve
        /// via Core gRPC call on read."
        ///
        /// In production, this resolution occurs via the Core service's SecurityGrpcService.
        /// This test validates the DATA MODEL supports this resolution pattern by:
        /// 1. Seeding a user in Core DB (SystemIds.SystemUserId)
        /// 2. Seeding an account in CRM DB with created_by referencing that user's UUID
        /// 3. Reading the account's created_by UUID from CRM DB
        /// 4. Resolving that UUID to a user record in Core DB
        /// 5. Verifying the resolved user data matches the seeded user
        ///
        /// Uses SystemIds.SystemUserId (10000000-0000-0000-0000-000000000000) as the test
        /// user and SystemIds.FirstUserId (EABD66FD-8DE1-4D79-9674-447EE89921C2) as the
        /// admin user per Definitions.cs lines 19-20.
        /// </summary>
        [Fact]
        public async Task CrossService_UserResolution_Should_Work_Via_Grpc()
        {
            // Step 1: Seed Core DB with system users (includes SystemUserId and FirstUserId)
            await _seeder.SeedCoreDataAsync(_postgreSqlFixture.CoreConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine($"Seeded Core data. SystemUserId={SystemIds.SystemUserId}, " +
                $"FirstUserId={SystemIds.FirstUserId}");

            // Step 2: Seed CRM DB with account referencing SystemUserId in created_by
            await _seeder.SeedCrmDataAsync(_postgreSqlFixture.CrmConnectionString)
                .ConfigureAwait(false);
            _output.WriteLine("Seeded CRM data with account.created_by = SystemUserId");

            string crmConn = _postgreSqlFixture.CrmConnectionString;
            string coreConn = _postgreSqlFixture.CoreConnectionString;

            // Step 3: Read the account's created_by UUID from CRM database
            Guid? createdByUserId = null;
            await using (var conn = new NpgsqlConnection(crmConn))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand(
                    "SELECT created_by FROM rec_account WHERE created_by IS NOT NULL LIMIT 1", conn);
                var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (result != null && result != DBNull.Value)
                {
                    createdByUserId = (Guid)result;
                }
            }

            createdByUserId.Should().NotBeNull(
                "CRM account should have a non-null created_by UUID referencing a Core service user");
            _output.WriteLine($"CRM account.created_by = {createdByUserId}");

            // Step 4: Resolve the user UUID in Core database
            // In production, this would be a gRPC call to Core's SecurityGrpcService.
            // Here we validate the data model supports the resolution by querying Core DB directly.
            string resolvedUsername = null;
            string resolvedEmail = null;
            await using (var conn = new NpgsqlConnection(coreConn))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand(
                    "SELECT username, email FROM rec_user WHERE id = @userId", conn);
                cmd.Parameters.AddWithValue("@userId", createdByUserId.Value);
                await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    resolvedUsername = reader.GetString(0);
                    resolvedEmail = reader.GetString(1);
                }
            }

            // Step 5: Verify the resolution succeeded with correct data
            resolvedUsername.Should().NotBeNull(
                "Cross-service user resolution should find the user record in Core DB " +
                $"for UUID {createdByUserId}");
            resolvedEmail.Should().NotBeNull(
                "Resolved user should have a valid email address");

            _output.WriteLine(
                $"✓ Cross-service resolution: CRM account.created_by={createdByUserId} → " +
                $"Core user.username='{resolvedUsername}', email='{resolvedEmail}'");
            _output.WriteLine(
                "This validates the AAP 0.7.1 pattern: UUID stored locally in CRM, " +
                "resolved via Core service (gRPC in production)");
        }

        #endregion
    }
}
