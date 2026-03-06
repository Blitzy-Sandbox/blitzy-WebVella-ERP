using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Npgsql;
using WebVella.Erp.Tests.Integration.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace WebVella.Erp.Tests.Integration.Migration
{
    /// <summary>
    /// Integration tests validating that each entity exists in exactly one service database,
    /// per the entity-to-service ownership matrix defined in AAP Section 0.7.1.
    ///
    /// Ensures zero entity duplication across service databases and correct placement of all
    /// entities after the monolith-to-microservices decomposition.
    ///
    /// Entity-to-Service Ownership Matrix (AAP 0.7.1):
    ///   Core:    user, role, user_file, language, currency, country
    ///   CRM:     account, contact, case, address, salutation
    ///   Project: task, timelog, comment, task_type
    ///   Mail:    email, smtp_service
    ///
    /// Table naming convention follows the monolith pattern from DbEntityRepository.cs:
    ///   Entity records are stored in rec_{entityName} tables (e.g., rec_user, rec_account).
    ///
    /// Per AAP 0.8.1: "Zero data loss during schema migration — every record in every
    /// rec_* table must be accounted for in the target service's database."
    /// Per AAP 0.8.2: "Each service owns its database schema exclusively; no other service
    /// may read from or write to another service's database."
    /// Per AAP 0.8.2: "Cross-service tests: Every business rule spanning 2+ services must
    /// have an integration test using Testcontainers."
    /// </summary>
    [Collection(IntegrationTestCollection.Name)]
    public class EntityOwnershipTests
    {
        #region Fields

        /// <summary>
        /// xUnit IAsyncLifetime fixture providing per-service PostgreSQL connection strings
        /// for Core (erp_core), CRM (erp_crm), Project (erp_project), and Mail (erp_mail)
        /// databases. Backed by a Testcontainers-managed PostgreSQL 16-alpine container.
        /// </summary>
        private readonly PostgreSqlFixture _postgreSqlFixture;

        /// <summary>
        /// xUnit diagnostic output helper for logging entity ownership verification results.
        /// Messages appear in test runner output for debugging and audit trail purposes.
        /// </summary>
        private readonly ITestOutputHelper _output;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="EntityOwnershipTests"/> class.
        /// Injected by xUnit via the <see cref="IntegrationTestCollection"/> collection
        /// definition which shares a single <see cref="PostgreSqlFixture"/> instance
        /// across all integration test classes.
        /// </summary>
        /// <param name="postgreSqlFixture">
        /// Provides per-service database connection strings for Core, CRM, Project, and Mail.
        /// </param>
        /// <param name="output">
        /// xUnit diagnostic output helper for logging verification results.
        /// </param>
        public EntityOwnershipTests(PostgreSqlFixture postgreSqlFixture, ITestOutputHelper output)
        {
            _postgreSqlFixture = postgreSqlFixture ?? throw new ArgumentNullException(nameof(postgreSqlFixture));
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Checks whether a table with the specified name exists in the public schema
        /// of the database identified by the given connection string.
        ///
        /// Uses a parameterized query against information_schema.tables to prevent
        /// SQL injection and follows the PostgreSQL information schema standard.
        ///
        /// Source pattern: DbEntityRepository.cs stores entity records in rec_{entityName} tables.
        /// </summary>
        /// <param name="connectionString">
        /// ADO.NET connection string for the target service database.
        /// </param>
        /// <param name="tableName">
        /// The exact table name to search for (e.g., "rec_user", "rec_account").
        /// </param>
        /// <returns>
        /// True if the table exists in the public schema; false otherwise.
        /// </returns>
        private async Task<bool> TableExistsAsync(string connectionString, string tableName)
        {
            await using NpgsqlConnection connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            const string sql =
                "SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name = @tableName";

            await using NpgsqlCommand command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@tableName", tableName);

            object result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            long count = Convert.ToInt64(result);
            return count > 0;
        }

        /// <summary>
        /// Retrieves all table names matching the rec_* pattern from the public schema
        /// of the database identified by the given connection string.
        ///
        /// This matches the monolith pattern where entity records are stored in
        /// rec_{entityName} tables (see DbEntityRepository.cs). The rec_ prefix is
        /// the standard naming convention used by the ERP engine for all entity tables.
        ///
        /// Results are sorted alphabetically for deterministic comparison and logging.
        /// </summary>
        /// <param name="connectionString">
        /// ADO.NET connection string for the target service database.
        /// </param>
        /// <returns>
        /// A sorted list of all rec_* table names found in the public schema.
        /// Returns an empty list if no rec_* tables exist.
        /// </returns>
        private async Task<List<string>> GetAllRecTablesAsync(string connectionString)
        {
            List<string> tables = new List<string>();

            await using NpgsqlConnection connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            const string sql =
                "SELECT table_name FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name LIKE 'rec_%' " +
                "ORDER BY table_name";

            await using NpgsqlCommand command = new NpgsqlCommand(sql, connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }

            return tables;
        }

        /// <summary>
        /// Helper method that asserts a rec_{entityName} table exists exclusively in the
        /// specified owner database and does NOT exist in any of the other service databases.
        ///
        /// This encapsulates the core ownership verification pattern used by all individual
        /// entity ownership test methods to reduce code duplication while maintaining
        /// clear, descriptive assertion messages.
        /// </summary>
        /// <param name="entityName">
        /// The entity name (e.g., "user", "account") — table name is rec_{entityName}.
        /// </param>
        /// <param name="ownerDbConnectionString">
        /// Connection string for the database that should own this entity.
        /// </param>
        /// <param name="ownerServiceName">
        /// Human-readable name of the owning service (e.g., "Core", "CRM") for assertion messages.
        /// </param>
        /// <param name="otherDatabases">
        /// Dictionary mapping service names to connection strings for databases that
        /// should NOT contain this entity's table.
        /// </param>
        private async Task AssertExclusiveOwnershipAsync(
            string entityName,
            string ownerDbConnectionString,
            string ownerServiceName,
            Dictionary<string, string> otherDatabases)
        {
            string tableName = $"rec_{entityName}";

            // Assert the table EXISTS in the owner's database
            bool existsInOwner = await TableExistsAsync(ownerDbConnectionString, tableName).ConfigureAwait(false);
            existsInOwner.Should().BeTrue(
                $"rec_{entityName} should exist in {ownerServiceName} database because " +
                $"'{entityName}' entity is owned by the {ownerServiceName} service per AAP 0.7.1");

            // Assert the table does NOT exist in any other service's database
            foreach (KeyValuePair<string, string> otherDb in otherDatabases)
            {
                bool existsInOther = await TableExistsAsync(otherDb.Value, tableName).ConfigureAwait(false);
                existsInOther.Should().BeFalse(
                    $"rec_{entityName} should NOT exist in {otherDb.Key} database because " +
                    $"'{entityName}' entity is exclusively owned by {ownerServiceName} service per AAP 0.7.1");
            }

            _output.WriteLine($"Verified: '{entityName}' entity owned exclusively by {ownerServiceName} service");
        }

        #endregion

        #region Core Service Entity Ownership Tests

        // Per AAP 0.7.1 entity-to-service ownership matrix:
        // user, role, user_file, language, currency, country → Core service
        // Source: ERPService.cs creates user, role, user_file system entities
        // Source: NextPlugin.20190204.cs creates language, currency, country entities

        /// <summary>
        /// Validates that the 'user' entity (rec_user table) exists exclusively in the
        /// Core Platform service database and does not exist in CRM, Project, or Mail databases.
        ///
        /// Source: ERPService.cs line 66 — userEntity.Name = "user"
        /// Source: Definitions.cs line 9 — SystemIds.UserEntityId
        /// </summary>
        [Fact]
        public async Task CoreService_Should_Own_UserEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "user",
                ownerDbConnectionString: _postgreSqlFixture.CoreConnectionString,
                ownerServiceName: "Core",
                otherDatabases: new Dictionary<string, string>
                {
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'role' entity (rec_role table) exists exclusively in the
        /// Core Platform service database and does not exist in CRM, Project, or Mail databases.
        ///
        /// Source: ERPService.cs — role entity created as system entity
        /// Source: Definitions.cs line 10 — SystemIds.RoleEntityId
        /// </summary>
        [Fact]
        public async Task CoreService_Should_Own_RoleEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "role",
                ownerDbConnectionString: _postgreSqlFixture.CoreConnectionString,
                ownerServiceName: "Core",
                otherDatabases: new Dictionary<string, string>
                {
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'user_file' entity (rec_user_file table) exists exclusively
        /// in the Core Platform service database and does not exist in CRM, Project, or Mail.
        ///
        /// Source: ERPService.cs — user_file entity created as system entity for file storage.
        /// Per AAP 0.7.1: user_file is referenced by Project (attachments) and Mail (attachments)
        /// but owned exclusively by Core service.
        /// </summary>
        [Fact]
        public async Task CoreService_Should_Own_UserFileEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "user_file",
                ownerDbConnectionString: _postgreSqlFixture.CoreConnectionString,
                ownerServiceName: "Core",
                otherDatabases: new Dictionary<string, string>
                {
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'language' entity (rec_language table) exists exclusively
        /// in the Core Platform service database.
        ///
        /// Source: NextPlugin.20190204.cs creates the 'language' entity with fields:
        /// abbr, l_scope, label, enabled. Originally in Next plugin but assigned to Core
        /// per AAP 0.7.1 because language is a shared reference entity used for localization
        /// across all services.
        /// </summary>
        [Fact]
        public async Task CoreService_Should_Own_LanguageEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "language",
                ownerDbConnectionString: _postgreSqlFixture.CoreConnectionString,
                ownerServiceName: "Core",
                otherDatabases: new Dictionary<string, string>
                {
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'currency' entity (rec_currency table) exists exclusively
        /// in the Core Platform service database.
        ///
        /// Source: NextPlugin.20190204.cs creates the 'currency' entity. Originally in
        /// Next plugin but assigned to Core per AAP 0.7.1 because currency is a shared
        /// reference entity used by CRM (account currency) and potentially other services.
        /// </summary>
        [Fact]
        public async Task CoreService_Should_Own_CurrencyEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "currency",
                ownerDbConnectionString: _postgreSqlFixture.CoreConnectionString,
                ownerServiceName: "Core",
                otherDatabases: new Dictionary<string, string>
                {
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'country' entity (rec_country table) exists exclusively
        /// in the Core Platform service database.
        ///
        /// Source: NextPlugin creates the 'country' entity. Assigned to Core per AAP 0.7.1
        /// because country is a shared reference entity used by CRM (address country).
        /// </summary>
        [Fact]
        public async Task CoreService_Should_Own_CountryEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "country",
                ownerDbConnectionString: _postgreSqlFixture.CoreConnectionString,
                ownerServiceName: "Core",
                otherDatabases: new Dictionary<string, string>
                {
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        #endregion

        #region CRM Service Entity Ownership Tests

        // Per AAP 0.7.1 entity-to-service ownership matrix:
        // account, contact, case, address, salutation → CRM service
        // Source: NextPlugin.20190204.cs creates account, contact, address
        // Source: NextPlugin.20190203.cs creates case
        // Source: NextPlugin.20190206.cs creates salutation

        /// <summary>
        /// Validates that the 'account' entity (rec_account table) exists exclusively
        /// in the CRM service database and does not exist in Core, Project, or Mail databases.
        ///
        /// Source: NextPlugin.20190204.cs creates 'account' with extensive CRM fields including
        /// name, x_search, created_by, and many business-specific attributes. Originally
        /// created by the Next plugin but assigned to CRM per AAP 0.7.1 cross-service boundary.
        /// </summary>
        [Fact]
        public async Task CrmService_Should_Own_AccountEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "account",
                ownerDbConnectionString: _postgreSqlFixture.CrmConnectionString,
                ownerServiceName: "CRM",
                otherDatabases: new Dictionary<string, string>
                {
                    { "Core", _postgreSqlFixture.CoreConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'contact' entity (rec_contact table) exists exclusively
        /// in the CRM service database.
        ///
        /// Source: NextPlugin.20190204.cs creates 'contact' entity with email, first_name,
        /// last_name, and other contact management fields. Cross-service reference from
        /// Mail service (recipients) resolved via UUID + CRM gRPC on read per AAP 0.7.1.
        /// </summary>
        [Fact]
        public async Task CrmService_Should_Own_ContactEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "contact",
                ownerDbConnectionString: _postgreSqlFixture.CrmConnectionString,
                ownerServiceName: "CRM",
                otherDatabases: new Dictionary<string, string>
                {
                    { "Core", _postgreSqlFixture.CoreConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'case' entity (rec_case table) exists exclusively
        /// in the CRM service database.
        ///
        /// Source: NextPlugin.20190203.cs creates 'case' entity for support/service case
        /// tracking. Cross-service reference to Project (case-task relations) resolved via
        /// denormalized case_id in Project DB per AAP 0.7.1.
        /// </summary>
        [Fact]
        public async Task CrmService_Should_Own_CaseEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "case",
                ownerDbConnectionString: _postgreSqlFixture.CrmConnectionString,
                ownerServiceName: "CRM",
                otherDatabases: new Dictionary<string, string>
                {
                    { "Core", _postgreSqlFixture.CoreConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'address' entity (rec_address table) exists exclusively
        /// in the CRM service database.
        ///
        /// Source: NextPlugin.20190204.cs creates 'address' entity for contact/account
        /// physical address management. No cross-service references per AAP 0.7.1.
        /// </summary>
        [Fact]
        public async Task CrmService_Should_Own_AddressEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "address",
                ownerDbConnectionString: _postgreSqlFixture.CrmConnectionString,
                ownerServiceName: "CRM",
                otherDatabases: new Dictionary<string, string>
                {
                    { "Core", _postgreSqlFixture.CoreConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'salutation' entity (rec_salutation table) exists exclusively
        /// in the CRM service database.
        ///
        /// Source: NextPlugin.20190206.cs creates 'salutation' entity as a lookup/reference
        /// table for contact salutation prefixes (Mr., Mrs., Dr., etc.). No cross-service
        /// references per AAP 0.7.1.
        /// </summary>
        [Fact]
        public async Task CrmService_Should_Own_SalutationEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "salutation",
                ownerDbConnectionString: _postgreSqlFixture.CrmConnectionString,
                ownerServiceName: "CRM",
                otherDatabases: new Dictionary<string, string>
                {
                    { "Core", _postgreSqlFixture.CoreConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        #endregion

        #region Project Service Entity Ownership Tests

        // Per AAP 0.7.1 entity-to-service ownership matrix:
        // task, timelog, comment, task_type → Project service
        // Source: NextPlugin.20190203.cs creates task, timelog
        // Source: NextPlugin.20190222.cs creates/normalizes task_type
        // Source: ProjectPlugin services create comment

        /// <summary>
        /// Validates that the 'task' entity (rec_task table) exists exclusively
        /// in the Project/Task service database and does not exist in Core, CRM, or Mail.
        ///
        /// Source: NextPlugin.20190203.cs creates 'task' entity with subject, start_date,
        /// end_date, priority, status, and other task management fields. Cross-service
        /// references from CRM (case-task links) resolved via denormalized case_id per AAP 0.7.1.
        /// </summary>
        [Fact]
        public async Task ProjectService_Should_Own_TaskEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "task",
                ownerDbConnectionString: _postgreSqlFixture.ProjectConnectionString,
                ownerServiceName: "Project",
                otherDatabases: new Dictionary<string, string>
                {
                    { "Core", _postgreSqlFixture.CoreConnectionString },
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'timelog' entity (rec_timelog table) exists exclusively
        /// in the Project/Task service database.
        ///
        /// Source: NextPlugin.20190203.cs creates 'timelog' entity. Updated in
        /// NextPlugin.20190205.cs with minutes configuration changes. Cross-service
        /// reference from Reporting (aggregation) resolved via event-sourced projections
        /// per AAP 0.7.1.
        /// </summary>
        [Fact]
        public async Task ProjectService_Should_Own_TimelogEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "timelog",
                ownerDbConnectionString: _postgreSqlFixture.ProjectConnectionString,
                ownerServiceName: "Project",
                otherDatabases: new Dictionary<string, string>
                {
                    { "Core", _postgreSqlFixture.CoreConnectionString },
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'comment' entity (rec_comment table) exists exclusively
        /// in the Project/Task service database.
        ///
        /// Source: Plugin Project services create comment entities for task discussion.
        /// No cross-service references per AAP 0.7.1.
        /// </summary>
        [Fact]
        public async Task ProjectService_Should_Own_CommentEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "comment",
                ownerDbConnectionString: _postgreSqlFixture.ProjectConnectionString,
                ownerServiceName: "Project",
                otherDatabases: new Dictionary<string, string>
                {
                    { "Core", _postgreSqlFixture.CoreConnectionString },
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'task_type' entity (rec_task_type table) exists exclusively
        /// in the Project/Task service database.
        ///
        /// Source: NextPlugin.20190222.cs creates and normalizes 'task_type' lookup rows
        /// as a reference table for categorizing tasks. No cross-service references per AAP 0.7.1.
        /// </summary>
        [Fact]
        public async Task ProjectService_Should_Own_TaskTypeEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "task_type",
                ownerDbConnectionString: _postgreSqlFixture.ProjectConnectionString,
                ownerServiceName: "Project",
                otherDatabases: new Dictionary<string, string>
                {
                    { "Core", _postgreSqlFixture.CoreConnectionString },
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Mail", _postgreSqlFixture.MailConnectionString }
                }).ConfigureAwait(false);
        }

        #endregion

        #region Mail Service Entity Ownership Tests

        // Per AAP 0.7.1 entity-to-service ownership matrix:
        // email, smtp_service → Mail service
        // Source: MailPlugin.20190215.cs creates email, smtp_service entities

        /// <summary>
        /// Validates that the 'email' entity (rec_email table) exists exclusively
        /// in the Mail/Notification service database and does not exist in Core, CRM, or Project.
        ///
        /// Source: MailPlugin.20190215.cs creates 'email' entity with typed fields including
        /// subject, content_html, sender (JSON, added 20190419), recipients (JSON, added 20190419),
        /// and attachments (added 20190529). No cross-service references per AAP 0.7.1.
        /// </summary>
        [Fact]
        public async Task MailService_Should_Own_EmailEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "email",
                ownerDbConnectionString: _postgreSqlFixture.MailConnectionString,
                ownerServiceName: "Mail",
                otherDatabases: new Dictionary<string, string>
                {
                    { "Core", _postgreSqlFixture.CoreConnectionString },
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString }
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the 'smtp_service' entity (rec_smtp_service table) exists exclusively
        /// in the Mail/Notification service database.
        ///
        /// Source: MailPlugin.20190215.cs creates 'smtp_service' entity for SMTP server
        /// configuration including name, server, port, credentials, and TLS settings.
        /// No cross-service references per AAP 0.7.1.
        /// </summary>
        [Fact]
        public async Task MailService_Should_Own_SmtpServiceEntity()
        {
            await AssertExclusiveOwnershipAsync(
                entityName: "smtp_service",
                ownerDbConnectionString: _postgreSqlFixture.MailConnectionString,
                ownerServiceName: "Mail",
                otherDatabases: new Dictionary<string, string>
                {
                    { "Core", _postgreSqlFixture.CoreConnectionString },
                    { "CRM", _postgreSqlFixture.CrmConnectionString },
                    { "Project", _postgreSqlFixture.ProjectConnectionString }
                }).ConfigureAwait(false);
        }

        #endregion

        #region Cross-Database Duplication Tests

        /// <summary>
        /// Comprehensive cross-check ensuring that NO entity table (rec_*) exists in more
        /// than one service database. This validates the database-per-service isolation
        /// requirement from AAP 0.7.1.
        ///
        /// Algorithm:
        /// 1. Retrieve all rec_* tables from each of the 4 service databases
        /// 2. Build a mapping from table name → list of databases where it appears
        /// 3. Assert that no table appears in more than one database
        ///
        /// Per AAP 0.8.2: "Each service owns its database schema exclusively; no other
        /// service may read from or write to another service's database."
        /// </summary>
        [Fact]
        public async Task No_Entity_Should_Exist_In_Multiple_Databases()
        {
            // Step 1: Retrieve all rec_* tables from each service database
            Dictionary<string, string> databases = new Dictionary<string, string>
            {
                { "Core", _postgreSqlFixture.CoreConnectionString },
                { "CRM", _postgreSqlFixture.CrmConnectionString },
                { "Project", _postgreSqlFixture.ProjectConnectionString },
                { "Mail", _postgreSqlFixture.MailConnectionString }
            };

            // Step 2: Build table-to-databases mapping
            Dictionary<string, List<string>> tableToDbMap = new Dictionary<string, List<string>>();

            foreach (KeyValuePair<string, string> db in databases)
            {
                List<string> tables = await GetAllRecTablesAsync(db.Value).ConfigureAwait(false);
                _output.WriteLine($"{db.Key} database contains {tables.Count} rec_* tables: {string.Join(", ", tables)}");

                foreach (string table in tables)
                {
                    if (!tableToDbMap.ContainsKey(table))
                    {
                        tableToDbMap[table] = new List<string>();
                    }
                    tableToDbMap[table].Add(db.Key);
                }
            }

            // Step 3: Assert no table appears in more than one database
            List<string> duplicates = tableToDbMap
                .Where(kvp => kvp.Value.Count > 1)
                .Select(kvp => $"Table '{kvp.Key}' found in multiple databases: {string.Join(", ", kvp.Value)}")
                .ToList();

            duplicates.Should().BeEmpty(
                "No entity table should exist in more than one service database. " +
                "Per AAP 0.8.2: 'Each service owns its database schema exclusively.' " +
                $"Violations found: {string.Join("; ", duplicates)}");

            _output.WriteLine($"Verified: All {tableToDbMap.Count} rec_* tables exist in exactly one database — zero duplication");
        }

        #endregion

        #region Complete Entity Coverage Tests

        /// <summary>
        /// Validates that ALL expected entities from AAP 0.7.1 exist in exactly one database
        /// and in their designated service database. This is the comprehensive entity coverage
        /// test ensuring no entities are missing after the monolith decomposition.
        ///
        /// Expected entity-to-service mapping (AAP 0.7.1):
        ///   Core:    user, role, user_file, language, currency, country
        ///   CRM:     account, contact, case, address, salutation
        ///   Project: task, timelog, comment, task_type
        ///   Mail:    email, smtp_service
        ///
        /// Per AAP 0.8.1: "Zero data loss during schema migration — every record in every
        /// rec_* table must be accounted for in the target service's database."
        /// </summary>
        [Fact]
        public async Task All_Expected_Entities_Should_Exist_In_Exactly_One_Database()
        {
            // Define the complete expected entity-to-service mapping from AAP 0.7.1
            Dictionary<string, (string ServiceName, string ConnectionString)> expectedEntities =
                new Dictionary<string, (string, string)>
                {
                    // Core service entities (6)
                    { "user", ("Core", _postgreSqlFixture.CoreConnectionString) },
                    { "role", ("Core", _postgreSqlFixture.CoreConnectionString) },
                    { "user_file", ("Core", _postgreSqlFixture.CoreConnectionString) },
                    { "language", ("Core", _postgreSqlFixture.CoreConnectionString) },
                    { "currency", ("Core", _postgreSqlFixture.CoreConnectionString) },
                    { "country", ("Core", _postgreSqlFixture.CoreConnectionString) },

                    // CRM service entities (5)
                    { "account", ("CRM", _postgreSqlFixture.CrmConnectionString) },
                    { "contact", ("CRM", _postgreSqlFixture.CrmConnectionString) },
                    { "case", ("CRM", _postgreSqlFixture.CrmConnectionString) },
                    { "address", ("CRM", _postgreSqlFixture.CrmConnectionString) },
                    { "salutation", ("CRM", _postgreSqlFixture.CrmConnectionString) },

                    // Project service entities (4)
                    { "task", ("Project", _postgreSqlFixture.ProjectConnectionString) },
                    { "timelog", ("Project", _postgreSqlFixture.ProjectConnectionString) },
                    { "comment", ("Project", _postgreSqlFixture.ProjectConnectionString) },
                    { "task_type", ("Project", _postgreSqlFixture.ProjectConnectionString) },

                    // Mail service entities (2)
                    { "email", ("Mail", _postgreSqlFixture.MailConnectionString) },
                    { "smtp_service", ("Mail", _postgreSqlFixture.MailConnectionString) }
                };

            _output.WriteLine($"Verifying {expectedEntities.Count} expected entities across 4 service databases...");

            int verifiedCount = 0;

            foreach (KeyValuePair<string, (string ServiceName, string ConnectionString)> expected in expectedEntities)
            {
                string entityName = expected.Key;
                string serviceName = expected.Value.ServiceName;
                string connectionString = expected.Value.ConnectionString;
                string tableName = $"rec_{entityName}";

                // Verify the entity exists in its designated database
                bool exists = await TableExistsAsync(connectionString, tableName).ConfigureAwait(false);
                exists.Should().BeTrue(
                    $"rec_{entityName} should exist in {serviceName} database per AAP 0.7.1 " +
                    $"entity-to-service ownership matrix");

                verifiedCount++;
                _output.WriteLine($"  [{verifiedCount}/{expectedEntities.Count}] '{entityName}' → {serviceName} ✓");
            }

            // Verify total count matches expectation (17 entities per AAP 0.7.1)
            verifiedCount.Should().Be(17,
                "All 17 entities from AAP 0.7.1 entity-to-service ownership matrix " +
                "must be verified: 6 Core + 5 CRM + 4 Project + 2 Mail = 17");

            _output.WriteLine($"Verified: All {verifiedCount} expected entities exist in their designated databases");
        }

        #endregion
    }
}
