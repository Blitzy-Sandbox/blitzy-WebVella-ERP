using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.Integration.Fixtures
{
    /// <summary>
    /// Utility class that seeds known test data (users, roles, entity records) into per-service
    /// databases for cross-service integration test scenarios. Provides JWT token generation for
    /// test authentication matching the AuthService.cs BuildTokenAsync pattern.
    ///
    /// This is NOT an IAsyncLifetime fixture — it is a utility invoked by test classes after
    /// the PostgreSqlFixture has initialized the database containers.
    ///
    /// Usage:
    ///   var seeder = new TestDataSeeder(pgFixture);
    ///   await seeder.SeedAllAsync(pgFixture);
    ///   string adminToken = seeder.GenerateAdminJwtToken();
    ///
    /// Key AAP References:
    ///   - AAP 0.7.1: Cross-service entity dependency — test data reflects entity-to-service ownership
    ///   - AAP 0.8.1: "Zero business rules may be marked as 'preserved' without a corresponding passing test"
    ///   - AAP 0.8.3: JWT config Key/Issuer/Audience from source Config.json lines 24-28
    ///   - Source: AuthService.cs lines 145-159 for JWT token building pattern
    ///   - Source: SecurityContext.cs lines 17-27 for system user definition
    ///   - Source: Definitions.cs lines 6-21 for SystemIds GUIDs
    ///   - Source: ERPService.cs lines 58-80 for user entity creation pattern
    /// </summary>
    public class TestDataSeeder
    {
        #region Constants — JWT Configuration

        /// <summary>
        /// Symmetric signing key for JWT tokens.
        /// Source: Config.json line 25: "Key": "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey"
        /// Per AAP 0.8.3: JWT config from source Config.json.
        /// </summary>
        private const string JwtKey = "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey";

        /// <summary>
        /// JWT token issuer.
        /// Source: Config.json line 26: "Issuer": "webvella-erp"
        /// </summary>
        private const string JwtIssuer = "webvella-erp";

        /// <summary>
        /// JWT token audience.
        /// Source: Config.json line 27: "Audience": "webvella-erp"
        /// </summary>
        private const string JwtAudience = "webvella-erp";

        /// <summary>
        /// JWT token expiration in minutes.
        /// Source: AuthService.cs line 19: JWT_TOKEN_EXPIRY_DURATION_MINUTES = 1440
        /// </summary>
        private const double JwtTokenExpiryMinutes = 1440;

        /// <summary>
        /// JWT token refresh-after threshold in minutes.
        /// Source: AuthService.cs line 20: JWT_TOKEN_FORCE_REFRESH_MINUTES = 120
        /// </summary>
        private const double JwtTokenRefreshMinutes = 120;

        #endregion

        #region Constants — Test-Specific Deterministic GUIDs

        /// <summary>
        /// Deterministic GUID for the test user record.
        /// Used across integration tests for predictable data references.
        /// </summary>
        private static readonly Guid TestUserId = new Guid("A0000001-0000-0000-0000-000000000001");

        /// <summary>
        /// Deterministic GUID for the test CRM account record.
        /// Per AAP 0.7.1: Account entity is owned by CRM service.
        /// </summary>
        private static readonly Guid TestAccountId = new Guid("A0000002-0000-0000-0000-000000000002");

        /// <summary>
        /// Deterministic GUID for the test CRM contact record.
        /// Per AAP 0.7.1: Contact entity is owned by CRM service.
        /// </summary>
        private static readonly Guid TestContactId = new Guid("A0000003-0000-0000-0000-000000000003");

        /// <summary>
        /// Deterministic GUID for the test project record.
        /// Per AAP 0.7.1: Project entity (task, timelog, comment, feed) is owned by Project service.
        /// </summary>
        private static readonly Guid TestProjectId = new Guid("A0000004-0000-0000-0000-000000000004");

        /// <summary>
        /// Deterministic GUID for the test task record.
        /// Per AAP 0.7.1: Task entity is owned by Project service.
        /// </summary>
        private static readonly Guid TestTaskId = new Guid("A0000005-0000-0000-0000-000000000005");

        /// <summary>
        /// Deterministic GUID for the test CRM address record.
        /// Per AAP 0.7.1: Address entity is owned by CRM service.
        /// </summary>
        private static readonly Guid TestAddressId = new Guid("A0000006-0000-0000-0000-000000000006");

        /// <summary>
        /// Deterministic GUID for the test SMTP service configuration record.
        /// Per AAP 0.7.1: smtp_service entity is owned by Mail service.
        /// </summary>
        private static readonly Guid TestSmtpServiceId = new Guid("A0000007-0000-0000-0000-000000000007");

        /// <summary>
        /// Deterministic GUID for the test email queue record.
        /// Per AAP 0.7.1: email entity is owned by Mail service.
        /// </summary>
        private static readonly Guid TestEmailId = new Guid("A0000008-0000-0000-0000-000000000008");

        /// <summary>
        /// Test case ID used as a denormalized cross-service reference in the Project database.
        /// Per AAP 0.7.1: "Case → Task: rel_* join table → Denormalized case_id in Project DB"
        /// </summary>
        private static readonly Guid TestCaseId = new Guid("A0000009-0000-0000-0000-000000000009");

        #endregion

        #region Private Fields

        /// <summary>
        /// The PostgreSQL fixture providing per-service database connection strings.
        /// </summary>
        private readonly PostgreSqlFixture _fixture;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new TestDataSeeder that uses the provided PostgreSqlFixture
        /// for database connection strings to each service's isolated test database.
        /// </summary>
        /// <param name="postgreSqlFixture">
        /// The PostgreSqlFixture providing CoreConnectionString, CrmConnectionString,
        /// ProjectConnectionString, and MailConnectionString for per-service databases.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when postgreSqlFixture is null.
        /// </exception>
        public TestDataSeeder(PostgreSqlFixture postgreSqlFixture)
        {
            _fixture = postgreSqlFixture ?? throw new ArgumentNullException(nameof(postgreSqlFixture));
        }

        #endregion

        #region Public Seed Methods

        /// <summary>
        /// Seeds the Core service database with system users, roles, and user-role relations.
        ///
        /// Creates tables matching the ERPService.cs InitializeSystemEntities pattern:
        ///   - rec_user: User records (system user, first admin user, test user)
        ///   - rec_role: Role records (administrator, regular, guest)
        ///   - rel_user_role: User-to-role relation records
        ///
        /// Source references:
        ///   - ERPService.cs lines 58-80: User entity creation with fields
        ///   - SecurityContext.cs lines 19-26: System user definition
        ///   - Definitions.cs lines 6-21: SystemIds GUIDs
        /// </summary>
        /// <param name="connectionString">
        /// ADO.NET connection string for the Core service database (erp_core).
        /// Typically obtained from PostgreSqlFixture.CoreConnectionString.
        /// </param>
        public async Task SeedCoreDataAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string must not be null or empty.", nameof(connectionString));

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            // Step 1: Create system tables matching ERPService.cs CheckCreateSystemTables pattern.
            // These are simplified schemas for test data — the actual monolith uses dynamic entity
            // tables (rec_{entity_name}) created by EntityManager. For integration tests, we create
            // the essential columns needed for business rule validation.

            await ExecuteNonQueryAsync(connection, @"
                CREATE TABLE IF NOT EXISTS rec_user (
                    id              UUID PRIMARY KEY,
                    username        TEXT NOT NULL,
                    email           TEXT NOT NULL,
                    password        TEXT,
                    first_name      TEXT DEFAULT '',
                    last_name       TEXT DEFAULT '',
                    image           TEXT DEFAULT '',
                    enabled         BOOLEAN NOT NULL DEFAULT TRUE,
                    verified        BOOLEAN NOT NULL DEFAULT TRUE,
                    created_on      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    last_logged_in  TIMESTAMPTZ
                );

                CREATE TABLE IF NOT EXISTS rec_role (
                    id          UUID PRIMARY KEY,
                    name        TEXT NOT NULL,
                    description TEXT DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS rel_user_role (
                    origin_id   UUID NOT NULL,
                    target_id   UUID NOT NULL,
                    PRIMARY KEY (origin_id, target_id)
                );
            ").ConfigureAwait(false);

            // Step 2: Insert system user record.
            // Source: SecurityContext.cs static constructor lines 19-26.
            // SystemUserId = 10000000-0000-0000-0000-000000000000, username="system",
            // email="system@webvella.com", enabled=true
            await InsertUserAsync(connection,
                SystemIds.SystemUserId,
                "system",
                "system@webvella.com",
                null,
                "Local",
                "System",
                true
            ).ConfigureAwait(false);

            // Step 3: Insert first admin user record.
            // Source: Definitions.cs line 20: FirstUserId = EABD66FD-8DE1-4D79-9674-447EE89921C2
            // Source: ERPService.cs user entity pattern — first user is "erpadmin"
            await InsertUserAsync(connection,
                SystemIds.FirstUserId,
                "erpadmin",
                "admin@webvella.com",
                "hashed_password_placeholder_for_test",
                "ERP",
                "Admin",
                true
            ).ConfigureAwait(false);

            // Step 4: Insert test user for general integration testing.
            await InsertUserAsync(connection,
                TestUserId,
                "testuser",
                "testuser@webvella.com",
                "hashed_password_placeholder_for_test",
                "Test",
                "User",
                true
            ).ConfigureAwait(false);

            // Step 5: Insert role records.
            // Source: Definitions.cs lines 15-17.
            await InsertRoleAsync(connection, SystemIds.AdministratorRoleId, "administrator", "Full system access").ConfigureAwait(false);
            await InsertRoleAsync(connection, SystemIds.RegularRoleId, "regular", "Standard user access").ConfigureAwait(false);
            await InsertRoleAsync(connection, SystemIds.GuestRoleId, "guest", "Read-only guest access").ConfigureAwait(false);

            // Step 6: Insert user-role relations.
            // System user → administrator role (matching SecurityContext.cs line 26)
            await InsertUserRoleRelationAsync(connection, SystemIds.SystemUserId, SystemIds.AdministratorRoleId).ConfigureAwait(false);
            // First admin user → administrator role
            await InsertUserRoleRelationAsync(connection, SystemIds.FirstUserId, SystemIds.AdministratorRoleId).ConfigureAwait(false);
            // Test user → regular role
            await InsertUserRoleRelationAsync(connection, TestUserId, SystemIds.RegularRoleId).ConfigureAwait(false);
        }

        /// <summary>
        /// Seeds the CRM service database with test account, contact, and address records.
        ///
        /// Creates tables for CRM entities per AAP 0.7.1 entity-to-service ownership:
        ///   - rec_account: Account records (owned by CRM service)
        ///   - rec_contact: Contact records (owned by CRM service)
        ///   - rec_address: Address records (owned by CRM service)
        ///
        /// Source references:
        ///   - AAP 0.7.1: account, contact, address entities from NextPlugin → CRM service
        ///   - NextPlugin.20190204.cs: Account/contact/address entity provisioning
        /// </summary>
        /// <param name="connectionString">
        /// ADO.NET connection string for the CRM service database (erp_crm).
        /// Typically obtained from PostgreSqlFixture.CrmConnectionString.
        /// </param>
        public async Task SeedCrmDataAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string must not be null or empty.", nameof(connectionString));

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            // Step 1: Create CRM entity metadata and record tables.
            await ExecuteNonQueryAsync(connection, @"
                CREATE TABLE IF NOT EXISTS rec_account (
                    id              UUID PRIMARY KEY,
                    name            TEXT NOT NULL DEFAULT '',
                    x_search        TEXT DEFAULT '',
                    created_by      UUID,
                    created_on      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    last_modified_by UUID,
                    last_modified_on TIMESTAMPTZ
                );

                CREATE TABLE IF NOT EXISTS rec_contact (
                    id              UUID PRIMARY KEY,
                    first_name      TEXT DEFAULT '',
                    last_name       TEXT DEFAULT '',
                    email           TEXT DEFAULT '',
                    x_search        TEXT DEFAULT '',
                    created_by      UUID,
                    created_on      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    last_modified_by UUID,
                    last_modified_on TIMESTAMPTZ
                );

                CREATE TABLE IF NOT EXISTS rec_address (
                    id              UUID PRIMARY KEY,
                    street          TEXT DEFAULT '',
                    city            TEXT DEFAULT '',
                    state           TEXT DEFAULT '',
                    country         TEXT DEFAULT '',
                    zip             TEXT DEFAULT '',
                    created_by      UUID,
                    created_on      TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );
            ").ConfigureAwait(false);

            // Step 2: Insert test account record.
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO rec_account (id, name, x_search, created_by, created_on)
                VALUES (@id, @name, @x_search, @created_by, @created_on)
                ON CONFLICT (id) DO NOTHING;", connection))
            {
                cmd.Parameters.AddWithValue("@id", TestAccountId);
                cmd.Parameters.AddWithValue("@name", "Test Account Inc.");
                cmd.Parameters.AddWithValue("@x_search", "test account inc.");
                cmd.Parameters.AddWithValue("@created_by", SystemIds.SystemUserId);
                cmd.Parameters.AddWithValue("@created_on", DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Step 3: Insert test contact record.
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO rec_contact (id, first_name, last_name, email, x_search, created_by, created_on)
                VALUES (@id, @first_name, @last_name, @email, @x_search, @created_by, @created_on)
                ON CONFLICT (id) DO NOTHING;", connection))
            {
                cmd.Parameters.AddWithValue("@id", TestContactId);
                cmd.Parameters.AddWithValue("@first_name", "John");
                cmd.Parameters.AddWithValue("@last_name", "Doe");
                cmd.Parameters.AddWithValue("@email", "john.doe@test.com");
                cmd.Parameters.AddWithValue("@x_search", "john doe john.doe@test.com");
                cmd.Parameters.AddWithValue("@created_by", SystemIds.SystemUserId);
                cmd.Parameters.AddWithValue("@created_on", DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Step 4: Insert test address record.
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO rec_address (id, street, city, state, country, zip, created_by, created_on)
                VALUES (@id, @street, @city, @state, @country, @zip, @created_by, @created_on)
                ON CONFLICT (id) DO NOTHING;", connection))
            {
                cmd.Parameters.AddWithValue("@id", TestAddressId);
                cmd.Parameters.AddWithValue("@street", "123 Test Street");
                cmd.Parameters.AddWithValue("@city", "Test City");
                cmd.Parameters.AddWithValue("@state", "TS");
                cmd.Parameters.AddWithValue("@country", "US");
                cmd.Parameters.AddWithValue("@zip", "12345");
                cmd.Parameters.AddWithValue("@created_by", SystemIds.SystemUserId);
                cmd.Parameters.AddWithValue("@created_on", DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Seeds the Project service database with test project and task records.
        ///
        /// Creates tables for Project entities per AAP 0.7.1 entity-to-service ownership:
        ///   - rec_project: Project records (owned by Project service)
        ///   - rec_task: Task records with cross-service references (owned by Project service)
        ///
        /// Cross-service references per AAP 0.7.1:
        ///   - Task.account_id: Denormalized reference to CRM account (TestAccountId)
        ///   - Task.case_id: Denormalized reference to CRM case (TestCaseId) per AAP 0.7.1
        ///   - Task.created_by: UUID reference resolved via Core gRPC call on read
        ///
        /// Source references:
        ///   - AAP 0.7.1: Task entity owned by Project service, linked to CRM via denormalized account_id
        ///   - NextPlugin.20190203.cs: Task entity provisioning
        ///   - ProjectPlugin services: Task/project domain logic
        /// </summary>
        /// <param name="connectionString">
        /// ADO.NET connection string for the Project service database (erp_project).
        /// Typically obtained from PostgreSqlFixture.ProjectConnectionString.
        /// </param>
        public async Task SeedProjectDataAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string must not be null or empty.", nameof(connectionString));

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            // Step 1: Create Project entity record tables.
            await ExecuteNonQueryAsync(connection, @"
                CREATE TABLE IF NOT EXISTS rec_project (
                    id              UUID PRIMARY KEY,
                    name            TEXT NOT NULL DEFAULT '',
                    description     TEXT DEFAULT '',
                    owner_id        UUID,
                    status          TEXT DEFAULT 'open',
                    created_by      UUID,
                    created_on      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    last_modified_by UUID,
                    last_modified_on TIMESTAMPTZ
                );

                CREATE TABLE IF NOT EXISTS rec_task (
                    id              UUID PRIMARY KEY,
                    subject         TEXT NOT NULL DEFAULT '',
                    status          TEXT DEFAULT 'not started',
                    priority        TEXT DEFAULT 'normal',
                    project_id      UUID,
                    account_id      UUID,
                    case_id         UUID,
                    created_by      UUID,
                    created_on      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    last_modified_by UUID,
                    last_modified_on TIMESTAMPTZ
                );
            ").ConfigureAwait(false);

            // Step 2: Insert test project record.
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO rec_project (id, name, description, owner_id, status, created_by, created_on)
                VALUES (@id, @name, @description, @owner_id, @status, @created_by, @created_on)
                ON CONFLICT (id) DO NOTHING;", connection))
            {
                cmd.Parameters.AddWithValue("@id", TestProjectId);
                cmd.Parameters.AddWithValue("@name", "Integration Test Project");
                cmd.Parameters.AddWithValue("@description", "Project created for cross-service integration testing");
                cmd.Parameters.AddWithValue("@owner_id", SystemIds.FirstUserId);
                cmd.Parameters.AddWithValue("@status", "open");
                cmd.Parameters.AddWithValue("@created_by", SystemIds.SystemUserId);
                cmd.Parameters.AddWithValue("@created_on", DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Step 3: Insert test task record with cross-service references to CRM account and case.
            // Per AAP 0.7.1: "Denormalized account_id in Project DB; eventual consistency via CRM events"
            // Per AAP 0.7.1: "Case → Task: Denormalized case_id in Project DB; CRM publishes CaseUpdated events"
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO rec_task (id, subject, status, priority, project_id, account_id, case_id, created_by, created_on)
                VALUES (@id, @subject, @status, @priority, @project_id, @account_id, @case_id, @created_by, @created_on)
                ON CONFLICT (id) DO NOTHING;", connection))
            {
                cmd.Parameters.AddWithValue("@id", TestTaskId);
                cmd.Parameters.AddWithValue("@subject", "Integration Test Task");
                cmd.Parameters.AddWithValue("@status", "not started");
                cmd.Parameters.AddWithValue("@priority", "normal");
                cmd.Parameters.AddWithValue("@project_id", TestProjectId);
                cmd.Parameters.AddWithValue("@account_id", TestAccountId);
                cmd.Parameters.AddWithValue("@case_id", TestCaseId);
                cmd.Parameters.AddWithValue("@created_by", SystemIds.SystemUserId);
                cmd.Parameters.AddWithValue("@created_on", DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Seeds the Mail service database with test SMTP service configuration and email queue record.
        ///
        /// Creates tables for Mail entities per AAP 0.7.1 entity-to-service ownership:
        ///   - rec_smtp_service: SMTP server configuration records (owned by Mail service)
        ///   - rec_email: Email queue records (owned by Mail service)
        ///
        /// Source references:
        ///   - AAP 0.7.1: email and smtp_service entities owned by Mail service
        ///   - MailPlugin.20190215.cs: SMTP service and email entity provisioning
        ///   - Mail plugin services: SmtpService, queue processing
        /// </summary>
        /// <param name="connectionString">
        /// ADO.NET connection string for the Mail service database (erp_mail).
        /// Typically obtained from PostgreSqlFixture.MailConnectionString.
        /// </param>
        public async Task SeedMailDataAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string must not be null or empty.", nameof(connectionString));

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            // Step 1: Create Mail entity record tables.
            await ExecuteNonQueryAsync(connection, @"
                CREATE TABLE IF NOT EXISTS rec_smtp_service (
                    id              UUID PRIMARY KEY,
                    name            TEXT NOT NULL DEFAULT '',
                    server_name     TEXT NOT NULL DEFAULT '',
                    port            INTEGER NOT NULL DEFAULT 25,
                    username        TEXT DEFAULT '',
                    password        TEXT DEFAULT '',
                    default_from    TEXT DEFAULT '',
                    is_default      BOOLEAN NOT NULL DEFAULT FALSE,
                    is_enabled      BOOLEAN NOT NULL DEFAULT TRUE,
                    connection_security INTEGER NOT NULL DEFAULT 0,
                    max_retries     INTEGER NOT NULL DEFAULT 3,
                    retry_wait_minutes INTEGER NOT NULL DEFAULT 5,
                    created_on      TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );

                CREATE TABLE IF NOT EXISTS rec_email (
                    id              UUID PRIMARY KEY,
                    subject         TEXT DEFAULT '',
                    content_body    TEXT DEFAULT '',
                    sender          TEXT DEFAULT '',
                    recipients      TEXT DEFAULT '',
                    status          INTEGER NOT NULL DEFAULT 0,
                    priority        INTEGER NOT NULL DEFAULT 1,
                    smtp_service_id UUID,
                    retries         INTEGER NOT NULL DEFAULT 0,
                    scheduled_on    TIMESTAMPTZ,
                    sent_on         TIMESTAMPTZ,
                    created_by      UUID,
                    created_on      TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );
            ").ConfigureAwait(false);

            // Step 2: Insert test SMTP service configuration.
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO rec_smtp_service (id, name, server_name, port, username, password, default_from, is_default, is_enabled, created_on)
                VALUES (@id, @name, @server_name, @port, @username, @password, @default_from, @is_default, @is_enabled, @created_on)
                ON CONFLICT (id) DO NOTHING;", connection))
            {
                cmd.Parameters.AddWithValue("@id", TestSmtpServiceId);
                cmd.Parameters.AddWithValue("@name", "Test SMTP Service");
                cmd.Parameters.AddWithValue("@server_name", "localhost");
                cmd.Parameters.AddWithValue("@port", 25);
                cmd.Parameters.AddWithValue("@username", "testsmtp");
                cmd.Parameters.AddWithValue("@password", "testpassword");
                cmd.Parameters.AddWithValue("@default_from", "noreply@webvella.com");
                cmd.Parameters.AddWithValue("@is_default", true);
                cmd.Parameters.AddWithValue("@is_enabled", true);
                cmd.Parameters.AddWithValue("@created_on", DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Step 3: Insert test email record in queue.
            // Status 0 = pending (default queue status for unprocessed emails)
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO rec_email (id, subject, content_body, sender, recipients, status, priority, smtp_service_id, created_by, created_on)
                VALUES (@id, @subject, @content_body, @sender, @recipients, @status, @priority, @smtp_service_id, @created_by, @created_on)
                ON CONFLICT (id) DO NOTHING;", connection))
            {
                cmd.Parameters.AddWithValue("@id", TestEmailId);
                cmd.Parameters.AddWithValue("@subject", "Integration Test Email");
                cmd.Parameters.AddWithValue("@content_body", "<p>This is a test email for integration testing.</p>");
                cmd.Parameters.AddWithValue("@sender", "noreply@webvella.com");
                cmd.Parameters.AddWithValue("@recipients", "testuser@webvella.com");
                cmd.Parameters.AddWithValue("@status", 0);
                cmd.Parameters.AddWithValue("@priority", 1);
                cmd.Parameters.AddWithValue("@smtp_service_id", TestSmtpServiceId);
                cmd.Parameters.AddWithValue("@created_by", SystemIds.SystemUserId);
                cmd.Parameters.AddWithValue("@created_on", DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Seeds all per-service databases by invoking each service-specific seed method.
        ///
        /// Execution order:
        /// 1. Core — users, roles, user-role relations (foundation for all services)
        /// 2. CRM — accounts, contacts, addresses
        /// 3. Project — projects, tasks (with cross-service references to CRM accounts)
        /// 4. Mail — SMTP config, email queue records
        ///
        /// Per AAP 0.7.1: Cross-service entity references use denormalized UUIDs
        /// (e.g., Task.account_id) resolved via gRPC on read, not foreign keys.
        /// </summary>
        /// <param name="pgFixture">
        /// The PostgreSqlFixture providing connection strings for each service database.
        /// </param>
        public async Task SeedAllAsync(PostgreSqlFixture pgFixture)
        {
            if (pgFixture == null)
                throw new ArgumentNullException(nameof(pgFixture));

            // Seed in dependency order: Core first (users/roles referenced by all services),
            // then domain services that may reference Core data via denormalized UUIDs.
            await SeedCoreDataAsync(pgFixture.CoreConnectionString).ConfigureAwait(false);
            await SeedCrmDataAsync(pgFixture.CrmConnectionString).ConfigureAwait(false);
            await SeedProjectDataAsync(pgFixture.ProjectConnectionString).ConfigureAwait(false);
            await SeedMailDataAsync(pgFixture.MailConnectionString).ConfigureAwait(false);
        }

        #endregion

        #region Public JWT Token Generation Methods

        /// <summary>
        /// Generates a JWT token for test authentication matching the AuthService.cs
        /// BuildTokenAsync pattern (source lines 145-159).
        ///
        /// Claims added:
        ///   - ClaimTypes.NameIdentifier: userId.ToString()
        ///   - ClaimTypes.Email: email
        ///   - ClaimTypes.Role (one per role name)
        ///   - "token_refresh_after": DateTime.UtcNow.AddMinutes(120).ToBinary().ToString()
        ///
        /// Signing: HMAC SHA-256 with symmetric key from Config.json.
        /// Expiry: 1440 minutes (24 hours) from DateTime.Now.
        /// </summary>
        /// <param name="userId">The user's GUID, typically from SystemIds.</param>
        /// <param name="email">The user's email address.</param>
        /// <param name="roles">List of role names to include as ClaimTypes.Role claims.</param>
        /// <returns>The serialized JWT token string.</returns>
        public string GenerateJwtToken(Guid userId, string email, List<string> roles)
        {
            // Build claims list matching AuthService.cs BuildTokenAsync pattern (lines 147-153).
            // CRITICAL: Source uses ClaimTypes.Role.ToString() — we preserve this exact pattern.
            var claims = new List<Claim>();
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
            claims.Add(new Claim(ClaimTypes.Email, email));

            if (roles != null)
            {
                claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role.ToString(), role)));
            }

            // Add token_refresh_after claim matching AuthService.cs line 152-153.
            // Source: DateTime.UtcNow.AddMinutes(JWT_TOKEN_FORCE_REFRESH_MINUTES)
            DateTime tokenRefreshAfterDateTime = DateTime.UtcNow.AddMinutes(JwtTokenRefreshMinutes);
            claims.Add(new Claim(type: "token_refresh_after", value: tokenRefreshAfterDateTime.ToBinary().ToString()));

            // Create signing credentials matching AuthService.cs lines 155-156.
            // Source: new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ErpSettings.JwtKey))
            // Source: new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256Signature)
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256Signature);

            // Create the JWT token matching AuthService.cs lines 157-158.
            // Source: new JwtSecurityToken(ErpSettings.JwtIssuer, ErpSettings.JwtAudience, claims,
            //     expires: DateTime.Now.AddMinutes(JWT_TOKEN_EXPIRY_DURATION_MINUTES), signingCredentials: credentials)
            // NOTE: Source uses DateTime.Now (not UtcNow) for expiry — preserving this exactly.
            var tokenDescriptor = new JwtSecurityToken(
                JwtIssuer,
                JwtAudience,
                claims,
                expires: DateTime.Now.AddMinutes(JwtTokenExpiryMinutes),
                signingCredentials: credentials
            );

            // Serialize to compact string format matching AuthService.cs line 159.
            // Source: new JwtSecurityTokenHandler().WriteToken(tokenDescriptor)
            return new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);
        }

        /// <summary>
        /// Generates a JWT token for the system user with administrator role.
        ///
        /// Uses SystemIds.SystemUserId and the system user email from SecurityContext.cs
        /// static constructor (line 24: "system@webvella.com") with ["administrator"] role.
        /// </summary>
        /// <returns>JWT token string for the system user.</returns>
        public string GenerateSystemJwtToken()
        {
            return GenerateJwtToken(
                SystemIds.SystemUserId,
                "system@webvella.com",
                new List<string> { "administrator" }
            );
        }

        /// <summary>
        /// Generates a JWT token for the first admin user with administrator role.
        ///
        /// Uses SystemIds.FirstUserId and the admin email from ERPService.cs user creation
        /// pattern ("admin@webvella.com") with ["administrator"] role.
        /// </summary>
        /// <returns>JWT token string for the first admin user.</returns>
        public string GenerateAdminJwtToken()
        {
            return GenerateJwtToken(
                SystemIds.FirstUserId,
                "admin@webvella.com",
                new List<string> { "administrator" }
            );
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Executes a non-query SQL command (DDL or DML) against the provided connection.
        /// Used for CREATE TABLE and batch DDL statements.
        /// </summary>
        /// <param name="connection">An opened NpgsqlConnection.</param>
        /// <param name="sql">The SQL command text to execute.</param>
        private static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Inserts a user record into the rec_user table using parameterized queries.
        /// Uses ON CONFLICT DO NOTHING for idempotent seeding.
        ///
        /// Matches the ERPService.cs user entity field schema:
        ///   id, username, email, password, first_name, last_name, enabled, verified, created_on
        /// </summary>
        /// <param name="connection">An opened NpgsqlConnection to the Core database.</param>
        /// <param name="id">User GUID (from SystemIds or test constants).</param>
        /// <param name="username">Username string.</param>
        /// <param name="email">Email address.</param>
        /// <param name="password">Hashed password (null for system user).</param>
        /// <param name="firstName">User's first name.</param>
        /// <param name="lastName">User's last name.</param>
        /// <param name="enabled">Whether the user account is enabled.</param>
        private static async Task InsertUserAsync(
            NpgsqlConnection connection,
            Guid id,
            string username,
            string email,
            string password,
            string firstName,
            string lastName,
            bool enabled)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO rec_user (id, username, email, password, first_name, last_name, enabled, verified, created_on)
                VALUES (@id, @username, @email, @password, @first_name, @last_name, @enabled, @verified, @created_on)
                ON CONFLICT (id) DO NOTHING;", connection);

            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@username", username);
            cmd.Parameters.AddWithValue("@email", email);
            cmd.Parameters.AddWithValue("@password", (object)password ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@first_name", firstName);
            cmd.Parameters.AddWithValue("@last_name", lastName);
            cmd.Parameters.AddWithValue("@enabled", enabled);
            cmd.Parameters.AddWithValue("@verified", true);
            cmd.Parameters.AddWithValue("@created_on", DateTime.UtcNow);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Inserts a role record into the rec_role table using parameterized queries.
        /// Uses ON CONFLICT DO NOTHING for idempotent seeding.
        /// </summary>
        /// <param name="connection">An opened NpgsqlConnection to the Core database.</param>
        /// <param name="id">Role GUID (from SystemIds).</param>
        /// <param name="name">Role name (e.g., "administrator", "regular", "guest").</param>
        /// <param name="description">Role description.</param>
        private static async Task InsertRoleAsync(NpgsqlConnection connection, Guid id, string name, string description)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO rec_role (id, name, description)
                VALUES (@id, @name, @description)
                ON CONFLICT (id) DO NOTHING;", connection);

            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@description", description);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Inserts a user-role relation into the rel_user_role table using parameterized queries.
        /// Uses ON CONFLICT DO NOTHING for idempotent seeding.
        ///
        /// Matches the monolith's relation pattern where:
        ///   origin_id = user ID (from rec_user)
        ///   target_id = role ID (from rec_role)
        /// </summary>
        /// <param name="connection">An opened NpgsqlConnection to the Core database.</param>
        /// <param name="userId">The user GUID (origin of the relation).</param>
        /// <param name="roleId">The role GUID (target of the relation).</param>
        private static async Task InsertUserRoleRelationAsync(NpgsqlConnection connection, Guid userId, Guid roleId)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO rel_user_role (origin_id, target_id)
                VALUES (@origin_id, @target_id)
                ON CONFLICT (origin_id, target_id) DO NOTHING;", connection);

            cmd.Parameters.AddWithValue("@origin_id", userId);
            cmd.Parameters.AddWithValue("@target_id", roleId);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        #endregion
    }
}
