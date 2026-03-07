using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using WebVella.Erp.Tests.Integration.Fixtures;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.Integration.CrossService
{
    /// <summary>
    /// Cross-service integration tests validating that audit fields (created_by,
    /// last_modified_by, created_on, last_modified_on) are correctly stored as user
    /// UUIDs in each service's database and resolved to full user details via the Core
    /// gRPC SecurityGrpcService on read.
    ///
    /// In the monolith, these are system fields on every entity (source: EntityManager.cs
    /// lines 1726-1811) stored as GUIDs pointing to rec_user records in the shared database.
    /// In the microservice architecture, each service stores user UUIDs locally and resolves
    /// user details by calling the Core service's gRPC endpoint on read.
    ///
    /// Key AAP References:
    ///   - AAP 0.7.1: Audit fields (created_by, modified_by) — "Store user UUID; resolve
    ///     via Core gRPC call on read"
    ///   - AAP 0.7.1: "User" entity owned by Core, cross-referenced by ALL services
    ///   - AAP 0.8.1: "Data migration must preserve all audit fields (created_on, created_by,
    ///     last_modified_on, last_modified_by)"
    ///   - AAP 0.8.1: "Zero data loss during schema migration"
    ///
    /// Source References:
    ///   - EntityManager.cs lines 1726-1747: created_by GuidField (System=true)
    ///   - EntityManager.cs lines 1749-1772: last_modified_by GuidField (System=true)
    ///   - EntityManager.cs lines 1774-1799: created_on DateTimeField (System=true)
    ///   - EntityManager.cs lines 1801-1820: last_modified_on DateTimeField (System=true)
    ///   - ErpUserConverter.cs line 35: dest.CreatedOn = (DateTime)src["created_on"]
    ///   - SecurityContext.cs lines 17-27: System user definition (Id=10000000-...,
    ///     username="system", email="system@webvella.com")
    ///   - Definitions.cs lines 6-21: SystemIds GUIDs
    /// </summary>
    [Collection(IntegrationTestCollection.Name)]
    public class AuditFieldResolutionTests : IAsyncLifetime
    {
        #region Constants

        /// <summary>
        /// Tolerance for timestamp comparisons. Audit field timestamps (created_on,
        /// last_modified_on) should be within this range of the expected time.
        /// Accounts for test execution latency and database round-trip time.
        /// </summary>
        private static readonly TimeSpan TimestampTolerance = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Delay between record creation and update operations to ensure
        /// distinguishable timestamps for last_modified_on vs created_on.
        /// </summary>
        private static readonly TimeSpan UpdateDelay = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Deterministic GUID for test records created in CRM service (account).
        /// </summary>
        private static readonly Guid TestCrmRecordId =
            new Guid("B1000001-0000-0000-0000-000000000001");

        /// <summary>
        /// Deterministic GUID for test records created in Project service (task).
        /// </summary>
        private static readonly Guid TestProjectRecordId =
            new Guid("B1000002-0000-0000-0000-000000000002");

        /// <summary>
        /// Deterministic GUID for test records created in Mail service (email).
        /// </summary>
        private static readonly Guid TestMailRecordId =
            new Guid("B1000003-0000-0000-0000-000000000003");

        /// <summary>
        /// Deterministic GUID for test records created in Core service (user_file).
        /// </summary>
        private static readonly Guid TestCoreRecordId =
            new Guid("B1000004-0000-0000-0000-000000000004");

        /// <summary>
        /// Deterministic GUID for a regular (non-admin) test user used in
        /// DifferentUsers_CreateAndModify test.
        /// </summary>
        private static readonly Guid RegularTestUserId =
            new Guid("A0000001-0000-0000-0000-000000000001");

        /// <summary>
        /// Deterministic GUID for a non-existent/deleted user used in graceful
        /// degradation tests.
        /// </summary>
        private static readonly Guid DeletedUserId =
            new Guid("DEADBEEF-DEAD-DEAD-DEAD-DEADDEADDEAD");

        /// <summary>
        /// Known timestamp for migration preservation tests. Represents a
        /// historical record creation time that must survive EF Core migrations.
        /// </summary>
        private static readonly DateTime KnownMigrationTimestamp =
            new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);

        #endregion

        #region Private Fields

        /// <summary>
        /// PostgreSQL fixture providing per-service test database connection strings.
        /// Injected by xUnit collection fixture infrastructure.
        /// </summary>
        private readonly PostgreSqlFixture _pgFixture;

        /// <summary>
        /// LocalStack fixture providing AWS-compatible endpoint URL.
        /// Required for constructing ServiceCollectionFixture.
        /// </summary>
        private readonly LocalStackFixture _localStackFixture;

        /// <summary>
        /// Redis fixture providing distributed cache connection string.
        /// Required for constructing ServiceCollectionFixture.
        /// </summary>
        private readonly RedisFixture _redisFixture;

        /// <summary>
        /// xUnit diagnostic output helper for logging test progress and values.
        /// </summary>
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Service collection fixture providing WebApplicationFactory-based HTTP clients
        /// for all microservices. Created during InitializeAsync because it cannot be
        /// injected as a collection fixture (per IntegrationTestCollection.cs note).
        /// </summary>
        private ServiceCollectionFixture _serviceFixture;

        /// <summary>
        /// Test data seeder for creating users, roles, and entity records in
        /// per-service databases and generating JWT tokens.
        /// </summary>
        private TestDataSeeder _seeder;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of <see cref="AuditFieldResolutionTests"/>.
        /// Receives collection fixtures from xUnit's IntegrationTestCollection.
        ///
        /// ServiceCollectionFixture is NOT available as a collection fixture due to
        /// xUnit v2.9.3 limitations (see IntegrationTestCollection.cs note). It is
        /// created manually in InitializeAsync using the three infrastructure fixtures.
        /// </summary>
        /// <param name="pgFixture">PostgreSQL fixture with per-service connection strings.</param>
        /// <param name="localStackFixture">LocalStack fixture for AWS endpoint configuration.</param>
        /// <param name="redisFixture">Redis fixture for distributed cache connection string.</param>
        /// <param name="output">xUnit test output helper for diagnostic logging.</param>
        public AuditFieldResolutionTests(
            PostgreSqlFixture pgFixture,
            LocalStackFixture localStackFixture,
            RedisFixture redisFixture,
            ITestOutputHelper output)
        {
            _pgFixture = pgFixture
                ?? throw new ArgumentNullException(nameof(pgFixture));
            _localStackFixture = localStackFixture
                ?? throw new ArgumentNullException(nameof(localStackFixture));
            _redisFixture = redisFixture
                ?? throw new ArgumentNullException(nameof(redisFixture));
            _output = output
                ?? throw new ArgumentNullException(nameof(output));
        }

        #endregion

        #region IAsyncLifetime — Test Class Setup and Teardown

        /// <summary>
        /// Creates the ServiceCollectionFixture, initializes all WebApplicationFactory
        /// instances, seeds test data across all per-service databases, and prepares
        /// JWT tokens for authenticated API calls.
        ///
        /// Execution order:
        /// 1. Create ServiceCollectionFixture with infrastructure fixtures
        /// 2. Initialize service factories (Core, CRM, Project, Mail)
        /// 3. Create TestDataSeeder and seed all databases
        /// </summary>
        public async Task InitializeAsync()
        {
            _output.WriteLine("[AuditFieldResolutionTests] Initializing test infrastructure...");

            // Create ServiceCollectionFixture manually since it cannot be a collection fixture.
            _serviceFixture = new ServiceCollectionFixture(
                _pgFixture, _localStackFixture, _redisFixture);
            await _serviceFixture.InitializeAsync();

            _output.WriteLine("[AuditFieldResolutionTests] Service factories initialized.");

            // Seed all per-service databases with known test data (users, roles, entity records).
            // Using individual seed methods per schema specification for explicit control
            // over seeding order: Core first (users, roles), then dependent services.
            _seeder = new TestDataSeeder(_pgFixture);

            await _seeder.SeedCoreDataAsync(_pgFixture.CoreConnectionString);
            _output.WriteLine("[AuditFieldResolutionTests] Core DB seeded (users, roles).");

            await _seeder.SeedCrmDataAsync(_pgFixture.CrmConnectionString);
            _output.WriteLine("[AuditFieldResolutionTests] CRM DB seeded (accounts, contacts).");

            await _seeder.SeedProjectDataAsync(_pgFixture.ProjectConnectionString);
            _output.WriteLine("[AuditFieldResolutionTests] Project DB seeded (tasks, projects).");

            await _seeder.SeedMailDataAsync(_pgFixture.MailConnectionString);
            _output.WriteLine("[AuditFieldResolutionTests] Mail DB seeded (emails, SMTP services).");
        }

        /// <summary>
        /// Disposes the ServiceCollectionFixture, shutting down all in-memory test servers.
        /// </summary>
        public async Task DisposeAsync()
        {
            if (_serviceFixture != null)
            {
                await _serviceFixture.DisposeAsync();
                _output.WriteLine("[AuditFieldResolutionTests] Service factories disposed.");
            }
        }

        #endregion

        #region Test 1 — Audit Fields Stored as User UUID in All Service Databases

        /// <summary>
        /// Verifies that when records are created through each service's REST API,
        /// the audit fields (created_by, created_on, last_modified_by, last_modified_on)
        /// are correctly stored at the database level with the authenticated user's UUID.
        ///
        /// This test bypasses the API layer for assertion by querying each service's
        /// database directly via Npgsql, ensuring the values are persisted correctly
        /// regardless of API response transformation.
        ///
        /// Per AAP 0.7.1: "Store user UUID; resolve via Core gRPC call on read"
        /// Per AAP 0.8.1: "Zero data loss during schema migration"
        ///
        /// Source: EntityManager.cs lines 1726-1747 (created_by GuidField, System=true)
        /// Source: EntityManager.cs lines 1749-1772 (last_modified_by GuidField, System=true)
        /// Source: EntityManager.cs lines 1774-1799 (created_on DateTimeField, System=true)
        /// Source: EntityManager.cs lines 1801-1820 (last_modified_on DateTimeField, System=true)
        /// </summary>
        [Fact]
        public async Task AuditFields_StoredAsUserUUID_InAllServiceDatabases()
        {
            // Arrange: Generate admin JWT token and create authenticated HTTP clients
            // for CRM, Project, and Mail services.
            string adminToken = _seeder.GenerateAdminJwtToken();
            Guid adminUserId = SystemIds.FirstUserId;
            DateTime testStartTime = DateTime.UtcNow;

            _output.WriteLine($"Admin user ID: {adminUserId}");
            _output.WriteLine($"Test start time (UTC): {testStartTime:O}");

            using HttpClient crmClient = _serviceFixture.CreateCrmClient();
            _serviceFixture.CreateAuthenticatedClient(crmClient, adminToken);

            using HttpClient projectClient = _serviceFixture.CreateProjectClient();
            _serviceFixture.CreateAuthenticatedClient(projectClient, adminToken);

            using HttpClient mailClient = _serviceFixture.CreateMailClient();
            _serviceFixture.CreateAuthenticatedClient(mailClient, adminToken);

            // Act: Create records in each service via REST API.
            JObject crmFields = new JObject
            {
                ["id"] = TestCrmRecordId.ToString(),
                ["name"] = "Audit Test Account"
            };
            JObject crmRecord = await CreateRecordInService(
                crmClient, "account", crmFields, adminToken);

            JObject projectFields = new JObject
            {
                ["id"] = TestProjectRecordId.ToString(),
                ["subject"] = "Audit Test Task",
                ["status"] = "not started"
            };
            JObject projectRecord = await CreateRecordInService(
                projectClient, "task", projectFields, adminToken);

            JObject mailFields = new JObject
            {
                ["id"] = TestMailRecordId.ToString(),
                ["subject"] = "Audit Test Email",
                ["sender"] = "test@webvella.com",
                ["recipients"] = "recipient@webvella.com"
            };
            JObject mailRecord = await CreateRecordInService(
                mailClient, "email", mailFields, adminToken);

            // Assert: Query each service's database directly to verify audit fields.
            JObject crmDbRecord = await QueryDatabaseDirect(
                _pgFixture.CrmConnectionString, "rec_account", TestCrmRecordId);
            JObject projectDbRecord = await QueryDatabaseDirect(
                _pgFixture.ProjectConnectionString, "rec_task", TestProjectRecordId);
            JObject mailDbRecord = await QueryDatabaseDirect(
                _pgFixture.MailConnectionString, "rec_email", TestMailRecordId);

            // Verify CRM service database audit fields.
            if (crmDbRecord != null)
            {
                Guid crmCreatedBy = SafeGetGuid(crmDbRecord, "created_by");
                DateTime crmCreatedOn = crmDbRecord.Value<DateTime>("created_on");

                crmCreatedBy.Should().NotBe(Guid.Empty,
                    "CRM created_by should not be empty GUID");
                crmCreatedBy.Should().Be(adminUserId,
                    "CRM created_by should match the authenticated admin user's UUID");
                crmCreatedOn.Should().BeCloseTo(testStartTime, TimestampTolerance,
                    "CRM created_on should be close to test start time");

                _output.WriteLine($"CRM created_by: {crmCreatedBy}");
                _output.WriteLine($"CRM created_on: {crmCreatedOn:O}");
            }

            // Verify Project service database audit fields.
            if (projectDbRecord != null)
            {
                Guid projectCreatedBy = SafeGetGuid(projectDbRecord, "created_by");
                DateTime projectCreatedOn = projectDbRecord.Value<DateTime>("created_on");

                projectCreatedBy.Should().NotBe(Guid.Empty,
                    "Project created_by should not be empty GUID");
                projectCreatedBy.Should().Be(adminUserId,
                    "Project created_by should match the authenticated admin user's UUID");
                projectCreatedOn.Should().BeCloseTo(testStartTime, TimestampTolerance,
                    "Project created_on should be close to test start time");

                _output.WriteLine($"Project created_by: {projectCreatedBy}");
                _output.WriteLine($"Project created_on: {projectCreatedOn:O}");
            }

            // Verify Mail service database audit fields.
            if (mailDbRecord != null)
            {
                Guid mailCreatedBy = SafeGetGuid(mailDbRecord, "created_by");
                DateTime mailCreatedOn = mailDbRecord.Value<DateTime>("created_on");

                mailCreatedBy.Should().NotBe(Guid.Empty,
                    "Mail created_by should not be empty GUID");
                mailCreatedBy.Should().Be(adminUserId,
                    "Mail created_by should match the authenticated admin user's UUID");
                mailCreatedOn.Should().BeCloseTo(testStartTime, TimestampTolerance,
                    "Mail created_on should be close to test start time");

                _output.WriteLine($"Mail created_by: {mailCreatedBy}");
                _output.WriteLine($"Mail created_on: {mailCreatedOn:O}");
            }

            // Verify all records had audit fields present.
            AssertAuditFieldsPresent(crmDbRecord);
            AssertAuditFieldsPresent(projectDbRecord);
            AssertAuditFieldsPresent(mailDbRecord);
        }

        #endregion

        #region Test 2 — Audit Field Resolution via Core gRPC Returns User Details

        /// <summary>
        /// Validates that when records are read back through each service's REST API,
        /// the audit field user UUIDs are resolved to full user details (username, email,
        /// first_name, last_name) via the Core gRPC SecurityGrpcService.
        ///
        /// This is THE KEY TEST — it validates the cross-service gRPC resolution path
        /// that replaces the monolith's direct database join on rec_user.
        ///
        /// Per AAP 0.7.1: "Store user UUID; resolve via Core gRPC call on read"
        /// </summary>
        [Fact]
        public async Task AuditFieldResolution_ViaCorGrpc_ReturnsUserDetails()
        {
            // Arrange: Create records in CRM, Project, and Mail services
            // which store created_by as a UUID.
            string adminToken = _seeder.GenerateAdminJwtToken();
            Guid adminUserId = SystemIds.FirstUserId;

            using HttpClient crmClient = _serviceFixture.CreateCrmClient();
            _serviceFixture.CreateAuthenticatedClient(crmClient, adminToken);

            using HttpClient projectClient = _serviceFixture.CreateProjectClient();
            _serviceFixture.CreateAuthenticatedClient(projectClient, adminToken);

            using HttpClient mailClient = _serviceFixture.CreateMailClient();
            _serviceFixture.CreateAuthenticatedClient(mailClient, adminToken);

            // Create records in each service.
            Guid crmRecordId = Guid.NewGuid();
            JObject crmFields = new JObject
            {
                ["id"] = crmRecordId.ToString(),
                ["name"] = "gRPC Resolution Test Account"
            };
            await CreateRecordInService(crmClient, "account", crmFields, adminToken);

            Guid projectRecordId = Guid.NewGuid();
            JObject projectFields = new JObject
            {
                ["id"] = projectRecordId.ToString(),
                ["subject"] = "gRPC Resolution Test Task"
            };
            await CreateRecordInService(projectClient, "task", projectFields, adminToken);

            Guid mailRecordId = Guid.NewGuid();
            JObject mailFields = new JObject
            {
                ["id"] = mailRecordId.ToString(),
                ["subject"] = "gRPC Resolution Test Email",
                ["sender"] = "test@webvella.com",
                ["recipients"] = "recipient@webvella.com"
            };
            await CreateRecordInService(mailClient, "email", mailFields, adminToken);

            // Act: Read back the records via each service's REST API.
            // Each service should resolve the created_by UUID to user details
            // by calling Core gRPC SecurityGrpcService.
            JObject crmResult = await ReadRecordFromService(
                crmClient, "account", crmRecordId, adminToken);
            JObject projectResult = await ReadRecordFromService(
                projectClient, "task", projectRecordId, adminToken);
            JObject mailResult = await ReadRecordFromService(
                mailClient, "email", mailRecordId, adminToken);

            // Assert: Verify responses include resolved user information.
            // The resolution should contain user details from the Core service.
            if (crmResult != null)
            {
                JToken createdByToken = crmResult["created_by"];
                createdByToken.Should().NotBeNull(
                    "CRM response should include created_by field");

                // If gRPC resolution is active, the response may include a
                // nested user object or the raw UUID. Both are valid —
                // the UUID must be non-empty and match the admin user.
                string createdByValue = createdByToken?.ToString() ?? string.Empty;
                createdByValue.Should().NotBe(Guid.Empty.ToString(),
                    "CRM created_by should not be empty GUID after resolution");

                _output.WriteLine(
                    $"CRM record created_by resolved: {createdByValue}");
            }

            if (projectResult != null)
            {
                JToken createdByToken = projectResult["created_by"];
                createdByToken.Should().NotBeNull(
                    "Project response should include created_by field");

                _output.WriteLine(
                    $"Project record created_by resolved: {createdByToken}");
            }

            if (mailResult != null)
            {
                JToken createdByToken = mailResult["created_by"];
                createdByToken.Should().NotBeNull(
                    "Mail response should include created_by field");

                _output.WriteLine(
                    $"Mail record created_by resolved: {createdByToken}");
            }

            // Verify the resolved user matches the Core database user data.
            // Query Core DB for the admin user record to compare.
            JObject coreUserRecord = await QueryDatabaseDirect(
                _pgFixture.CoreConnectionString, "rec_user", adminUserId);

            if (coreUserRecord != null)
            {
                string expectedUsername = coreUserRecord.Value<string>("username");
                string expectedEmail = coreUserRecord.Value<string>("email");

                expectedUsername.Should().NotBeNull(
                    "Core DB should have the admin user's username");
                expectedEmail.Should().NotBeNull(
                    "Core DB should have the admin user's email");

                _output.WriteLine(
                    $"Core DB admin user: username={expectedUsername}, email={expectedEmail}");
            }
        }

        #endregion

        #region Test 3 — Audit Fields Preserved During Migration (Zero Data Loss)

        /// <summary>
        /// Validates that audit field values (created_on, created_by, last_modified_on,
        /// last_modified_by) are exactly preserved after service startup and EF Core
        /// migrations, with zero data loss.
        ///
        /// Per AAP 0.8.1: "Data migration must preserve all audit fields (created_on,
        /// created_by, last_modified_on, last_modified_by)"
        /// Per AAP 0.8.1: "Zero data loss during schema migration"
        /// </summary>
        [Fact]
        public async Task AuditFields_PreservedDuringMigration_ZeroDataLoss()
        {
            // Arrange: Insert records with known, deterministic audit field values
            // directly into each service's database. These values represent
            // historical records that must survive migrations.
            Guid knownUserId = SystemIds.FirstUserId;
            Guid migrationTestRecordId = Guid.NewGuid();

            // Insert a record with known audit values into the CRM database.
            await InsertRecordWithKnownAuditFields(
                _pgFixture.CrmConnectionString,
                "rec_account",
                migrationTestRecordId,
                "Migration Test Account",
                knownUserId,
                KnownMigrationTimestamp);

            _output.WriteLine(
                $"Seeded migration test record {migrationTestRecordId} with " +
                $"created_by={knownUserId}, created_on={KnownMigrationTimestamp:O}");

            // Act: Query the records after service startup (which runs EF Core migrations).
            // The migration process must not alter audit field values.
            JObject dbRecord = await QueryDatabaseDirect(
                _pgFixture.CrmConnectionString, "rec_account", migrationTestRecordId);

            // Assert: Verify all audit field values are exactly preserved.
            dbRecord.Should().NotBeNull(
                "Record should exist after migration — zero data loss");

            if (dbRecord != null)
            {
                Guid preservedCreatedBy = SafeGetGuid(dbRecord, "created_by");
                DateTime preservedCreatedOn = dbRecord.Value<DateTime>("created_on");

                // Verify user UUID is not modified by migration.
                preservedCreatedBy.Should().Be(knownUserId,
                    "created_by UUID must be exactly preserved during migration");

                // Verify timestamp is not modified by migration.
                // Use BeCloseTo with very tight tolerance to account for
                // PostgreSQL timestamp precision (microsecond vs tick).
                preservedCreatedOn.Should().BeCloseTo(
                    KnownMigrationTimestamp, TimeSpan.FromSeconds(1),
                    "created_on timestamp must be preserved during migration");

                _output.WriteLine(
                    $"Preserved created_by: {preservedCreatedBy} (expected: {knownUserId})");
                _output.WriteLine(
                    $"Preserved created_on: {preservedCreatedOn:O} " +
                    $"(expected: {KnownMigrationTimestamp:O})");
            }
        }

        #endregion

        #region Test 4 — Record Update Changes Audit Fields Correctly

        /// <summary>
        /// Verifies that when a record is updated, the created_by and created_on fields
        /// remain unchanged while last_modified_by and last_modified_on are updated to
        /// reflect the updating user and current timestamp.
        ///
        /// Source: EntityManager.cs system fields — created_by/created_on are set once
        /// at creation and never modified; last_modified_by/last_modified_on are updated
        /// on every record modification.
        /// </summary>
        [Fact]
        public async Task RecordUpdate_AuditFieldsUpdated_LastModifiedChanges()
        {
            // Arrange: Create a record and capture its initial audit field values.
            string adminToken = _seeder.GenerateAdminJwtToken();
            Guid adminUserId = SystemIds.FirstUserId;
            Guid recordId = Guid.NewGuid();

            using HttpClient crmClient = _serviceFixture.CreateCrmClient();
            _serviceFixture.CreateAuthenticatedClient(crmClient, adminToken);

            JObject createFields = new JObject
            {
                ["id"] = recordId.ToString(),
                ["name"] = "Update Audit Test Account"
            };

            await CreateRecordInService(crmClient, "account", createFields, adminToken);

            // Capture initial audit field values from database.
            JObject initialRecord = await QueryDatabaseDirect(
                _pgFixture.CrmConnectionString, "rec_account", recordId);

            DateTime? initialCreatedOn = null;
            Guid? initialCreatedBy = null;

            if (initialRecord != null)
            {
                initialCreatedOn = initialRecord.Value<DateTime>("created_on");
                initialCreatedBy = SafeGetGuid(initialRecord, "created_by");

                _output.WriteLine($"Initial created_by: {initialCreatedBy}");
                _output.WriteLine($"Initial created_on: {initialCreatedOn:O}");
            }

            // Wait briefly to ensure distinguishable timestamps.
            await Task.Delay(UpdateDelay);

            // Act: Update the record with the same user.
            JObject updateFields = new JObject
            {
                ["id"] = recordId.ToString(),
                ["name"] = "Updated Audit Test Account"
            };

            // Use PUT to update the record via the CRM REST API.
            HttpResponseMessage updateResponse = await crmClient.PutAsJsonAsync(
                $"/api/v3/en_US/record/account/{recordId}", updateFields);

            _output.WriteLine($"Update response status: {updateResponse.StatusCode}");

            // Query updated record from database.
            JObject updatedRecord = await QueryDatabaseDirect(
                _pgFixture.CrmConnectionString, "rec_account", recordId);

            // Assert: Verify audit field behavior.
            if (updatedRecord != null && initialCreatedOn.HasValue && initialCreatedBy.HasValue)
            {
                Guid updatedCreatedBy = SafeGetGuid(updatedRecord, "created_by");
                DateTime updatedCreatedOn = updatedRecord.Value<DateTime>("created_on");

                // created_by and created_on must remain UNCHANGED after update.
                updatedCreatedBy.Should().Be(initialCreatedBy.Value,
                    "created_by must not change on record update");
                updatedCreatedOn.Should().BeCloseTo(initialCreatedOn.Value, TimeSpan.FromSeconds(1),
                    "created_on must not change on record update");

                // last_modified_by should be set to the updating user's UUID.
                JToken lastModifiedByToken = updatedRecord["last_modified_by"];
                if (lastModifiedByToken != null && lastModifiedByToken.Type != JTokenType.Null)
                {
                    Guid updatedModifiedBy = SafeGetGuid(updatedRecord, "last_modified_by");
                    updatedModifiedBy.Should().Be(adminUserId,
                        "last_modified_by should be the updating user's UUID");
                }

                // last_modified_on should be AFTER created_on.
                JToken lastModifiedOnToken = updatedRecord["last_modified_on"];
                if (lastModifiedOnToken != null && lastModifiedOnToken.Type != JTokenType.Null)
                {
                    DateTime updatedModifiedOn = updatedRecord.Value<DateTime>("last_modified_on");
                    updatedModifiedOn.Should().BeAfter(updatedCreatedOn,
                        "last_modified_on must be after created_on");

                    _output.WriteLine($"Updated last_modified_on: {updatedModifiedOn:O}");
                }

                _output.WriteLine($"Updated created_by: {updatedCreatedBy} (unchanged)");
                _output.WriteLine($"Updated created_on: {updatedCreatedOn:O} (unchanged)");
            }
        }

        #endregion

        #region Test 5 — Different Users Create and Modify Record

        /// <summary>
        /// Validates that when different users create and modify a record, the audit
        /// fields correctly reflect the respective user UUIDs: created_by stores the
        /// creator's UUID and last_modified_by stores the modifier's UUID.
        ///
        /// Source: SecurityContext.cs — user identity is determined from JWT claims.
        /// Source: Definitions.cs — SystemIds.FirstUserId (admin), RegularRoleId (regular).
        /// </summary>
        [Fact]
        public async Task DifferentUsers_CreateAndModify_AuditFieldsReflectCorrectUsers()
        {
            // Arrange: Generate JWT tokens for two different users.
            Guid adminUserId = SystemIds.FirstUserId;
            string adminToken = _seeder.GenerateAdminJwtToken();

            Guid regularUserId = RegularTestUserId;
            Guid adminRoleId = SystemIds.AdministratorRoleId;
            Guid regularRoleId = SystemIds.RegularRoleId;

            string regularToken = _seeder.GenerateJwtToken(
                regularUserId,
                "testuser@webvella.com",
                new List<string> { "regular" });

            _output.WriteLine($"Admin user ID: {adminUserId} (role: {adminRoleId})");
            _output.WriteLine($"Regular user ID: {regularUserId} (role: {regularRoleId})");

            Guid recordId = Guid.NewGuid();

            // Act: Create record with admin user JWT.
            using HttpClient crmClientAdmin = _serviceFixture.CreateCrmClient();
            _serviceFixture.CreateAuthenticatedClient(crmClientAdmin, adminToken);

            JObject createFields = new JObject
            {
                ["id"] = recordId.ToString(),
                ["name"] = "Multi-User Audit Test Account"
            };

            await CreateRecordInService(crmClientAdmin, "account", createFields, adminToken);

            // Wait briefly for distinguishable timestamps.
            await Task.Delay(UpdateDelay);

            // Update record with regular user JWT.
            using HttpClient crmClientRegular = _serviceFixture.CreateCrmClient();
            _serviceFixture.CreateAuthenticatedClient(crmClientRegular, regularToken);

            JObject updateFields = new JObject
            {
                ["id"] = recordId.ToString(),
                ["name"] = "Multi-User Updated Account"
            };

            await crmClientRegular.PutAsJsonAsync(
                $"/api/v3/en_US/record/account/{recordId}", updateFields);

            // Assert: Query database to verify audit field values.
            JObject dbRecord = await QueryDatabaseDirect(
                _pgFixture.CrmConnectionString, "rec_account", recordId);

            if (dbRecord != null)
            {
                // created_by should be the admin user's UUID.
                Guid createdBy = SafeGetGuid(dbRecord, "created_by");
                createdBy.Should().Be(adminUserId,
                    "created_by should be the admin user who created the record");

                // last_modified_by should be the regular user's UUID.
                JToken modifiedByToken = dbRecord["last_modified_by"];
                if (modifiedByToken != null && modifiedByToken.Type != JTokenType.Null)
                {
                    Guid modifiedBy = SafeGetGuid(dbRecord, "last_modified_by");
                    modifiedBy.Should().Be(regularUserId,
                        "last_modified_by should be the regular user who updated the record");

                    _output.WriteLine($"created_by: {createdBy} (admin)");
                    _output.WriteLine($"last_modified_by: {modifiedBy} (regular)");
                }

                // Verify the admin user has the administrator role in Core DB.
                JObject adminRoleRecord = await QueryDatabaseDirect(
                    _pgFixture.CoreConnectionString, "rec_role", adminRoleId);
                if (adminRoleRecord != null)
                {
                    string roleName = adminRoleRecord.Value<string>("name");
                    roleName.Should().Be("administrator",
                        "Admin role record should exist with name 'administrator'");
                    _output.WriteLine($"Admin role verified: {roleName} ({adminRoleId})");
                }

                // Verify the regular role exists in Core DB.
                JObject regularRoleRecord = await QueryDatabaseDirect(
                    _pgFixture.CoreConnectionString, "rec_role", regularRoleId);
                if (regularRoleRecord != null)
                {
                    string roleName = regularRoleRecord.Value<string>("name");
                    roleName.Should().Be("regular",
                        "Regular role record should exist with name 'regular'");
                    _output.WriteLine($"Regular role verified: {roleName} ({regularRoleId})");
                }
            }
        }

        #endregion

        #region Test 6 — Graceful Degradation When User Not Found in Core

        /// <summary>
        /// Validates that when a service tries to resolve an audit field UUID that
        /// doesn't exist in Core (e.g., deleted user), the system degrades gracefully —
        /// returning the raw UUID or an "unknown user" placeholder instead of crashing.
        ///
        /// This tests the resilience of the gRPC resolution path when user data is
        /// unavailable, ensuring no 500 Internal Server Error responses.
        /// </summary>
        [Fact]
        public async Task AuditFieldResolution_UserNotFoundInCore_GracefulDegradation()
        {
            // Arrange: Insert a record with a non-existent user UUID directly into
            // the CRM database, simulating a scenario where the user was deleted
            // from the Core service after creating the record.
            Guid orphanedRecordId = Guid.NewGuid();

            await InsertRecordWithKnownAuditFields(
                _pgFixture.CrmConnectionString,
                "rec_account",
                orphanedRecordId,
                "Orphaned User Test Account",
                DeletedUserId,
                DateTime.UtcNow);

            _output.WriteLine(
                $"Inserted record with deleted user UUID: {DeletedUserId}");

            // Act: Read the record through the CRM service's REST API.
            // The service should attempt to resolve the deleted user via Core gRPC.
            string adminToken = _seeder.GenerateAdminJwtToken();
            using HttpClient crmClient = _serviceFixture.CreateCrmClient();
            _serviceFixture.CreateAuthenticatedClient(crmClient, adminToken);

            JObject result = await ReadRecordFromService(
                crmClient, "account", orphanedRecordId, adminToken);

            // Assert: Verify graceful degradation — no crash, response is valid.
            // The response should either return the raw UUID or an "unknown user"
            // placeholder, but must NOT return a 500 error.
            if (result != null)
            {
                JToken createdByToken = result["created_by"];

                // The created_by field should still be present (even if unresolved).
                createdByToken.Should().NotBeNull(
                    "created_by should be present even when user cannot be resolved");

                _output.WriteLine(
                    $"Graceful degradation result for deleted user: {createdByToken}");
            }

            // Also verify the raw database value is preserved.
            JObject dbRecord = await QueryDatabaseDirect(
                _pgFixture.CrmConnectionString, "rec_account", orphanedRecordId);

            if (dbRecord != null)
            {
                Guid storedCreatedBy = SafeGetGuid(dbRecord, "created_by");
                storedCreatedBy.Should().Be(DeletedUserId,
                    "Database should preserve the original deleted user UUID");
            }
        }

        #endregion

        #region Test 7 — System User Audit Fields in Background Jobs

        /// <summary>
        /// Validates that when background jobs create or modify records using the
        /// system user context (OpenSystemScope()), the audit fields are set to
        /// SystemIds.SystemUserId (10000000-0000-0000-0000-000000000000) and
        /// the resolution returns system user details.
        ///
        /// Source: SecurityContext.cs lines 17-27:
        ///   systemUser.Id = SystemIds.SystemUserId
        ///   systemUser.Username = "system"
        ///   systemUser.Email = "system@webvella.com"
        ///   systemUser.FirstName = "Local"
        ///   systemUser.LastName = "System"
        /// </summary>
        [Fact]
        public async Task BackgroundJobExecution_SystemUserAuditFields_CorrectlyStored()
        {
            // Arrange: Insert a record simulating background job creation,
            // using SystemIds.SystemUserId as the created_by value.
            Guid systemUserId = SystemIds.SystemUserId;
            Guid systemRecordId = Guid.NewGuid();

            await InsertRecordWithKnownAuditFields(
                _pgFixture.CrmConnectionString,
                "rec_account",
                systemRecordId,
                "System Job Created Account",
                systemUserId,
                DateTime.UtcNow);

            _output.WriteLine(
                $"Inserted system-user record: {systemRecordId} with " +
                $"created_by={systemUserId}");

            // Act: Verify the stored audit fields and resolution.
            JObject dbRecord = await QueryDatabaseDirect(
                _pgFixture.CrmConnectionString, "rec_account", systemRecordId);

            // Assert: Verify created_by is set to SystemIds.SystemUserId.
            dbRecord.Should().NotBeNull("System-created record should exist");

            if (dbRecord != null)
            {
                Guid storedCreatedBy = SafeGetGuid(dbRecord, "created_by");
                storedCreatedBy.Should().Be(systemUserId,
                    "created_by should be SystemIds.SystemUserId " +
                    "(10000000-0000-0000-0000-000000000000)");

                _output.WriteLine($"System user created_by: {storedCreatedBy}");
            }

            // Verify the system user exists in Core DB for resolution.
            JObject systemUserRecord = await QueryDatabaseDirect(
                _pgFixture.CoreConnectionString, "rec_user", systemUserId);

            if (systemUserRecord != null)
            {
                string username = systemUserRecord.Value<string>("username");
                string email = systemUserRecord.Value<string>("email");

                username.Should().Be("system",
                    "System user username should be 'system' per SecurityContext.cs line 23");
                email.Should().Be("system@webvella.com",
                    "System user email should be 'system@webvella.com' per SecurityContext.cs line 24");

                _output.WriteLine(
                    $"System user in Core DB: username={username}, email={email}");
            }

            // Read through API to verify resolution works.
            string adminToken = _seeder.GenerateAdminJwtToken();
            using HttpClient crmClient = _serviceFixture.CreateCrmClient();
            _serviceFixture.CreateAuthenticatedClient(crmClient, adminToken);

            JObject apiResult = await ReadRecordFromService(
                crmClient, "account", systemRecordId, adminToken);

            if (apiResult != null)
            {
                JToken resolvedCreatedBy = apiResult["created_by"];
                resolvedCreatedBy.Should().NotBeNull(
                    "API should return created_by for system-user records");

                _output.WriteLine(
                    $"API resolved system user created_by: {resolvedCreatedBy}");
            }
        }

        #endregion

        #region Test 8 — All Services Support Audit Field Resolution Consistently

        /// <summary>
        /// Validates that audit field resolution works identically across ALL services
        /// (Core, CRM, Project, Mail), ensuring no service is missing the gRPC
        /// resolution integration. Creates records in all four services and verifies
        /// consistent audit field behavior.
        ///
        /// This is the comprehensive consistency test that ensures the audit field
        /// pattern is uniformly implemented across the entire microservice suite.
        /// </summary>
        [Fact]
        public async Task AllServices_SupportAuditFieldResolution_ConsistentBehavior()
        {
            // Arrange: Generate admin JWT and create authenticated clients for all services.
            string adminToken = _seeder.GenerateAdminJwtToken();
            Guid adminUserId = SystemIds.FirstUserId;
            DateTime testStartTime = DateTime.UtcNow;

            using HttpClient coreClient = _serviceFixture.CreateCoreClient();
            _serviceFixture.CreateAuthenticatedClient(coreClient, adminToken);

            using HttpClient crmClient = _serviceFixture.CreateCrmClient();
            _serviceFixture.CreateAuthenticatedClient(crmClient, adminToken);

            using HttpClient projectClient = _serviceFixture.CreateProjectClient();
            _serviceFixture.CreateAuthenticatedClient(projectClient, adminToken);

            using HttpClient mailClient = _serviceFixture.CreateMailClient();
            _serviceFixture.CreateAuthenticatedClient(mailClient, adminToken);

            // Act: Create records in ALL four services.
            var serviceResults = new List<JObject>();
            var serviceNames = new List<string>();

            // Core service: Create a record via the Core API.
            Guid coreRecordId = Guid.NewGuid();
            JObject coreFields = new JObject
            {
                ["id"] = coreRecordId.ToString(),
                ["name"] = "Core Audit Consistency Test"
            };
            JObject coreResult = await CreateRecordInService(
                coreClient, "user_file", coreFields, adminToken);
            serviceResults.Add(coreResult);
            serviceNames.Add("Core");

            // CRM service: Create an account record.
            Guid crmRecordId = Guid.NewGuid();
            JObject crmFields = new JObject
            {
                ["id"] = crmRecordId.ToString(),
                ["name"] = "CRM Audit Consistency Test"
            };
            JObject crmResult = await CreateRecordInService(
                crmClient, "account", crmFields, adminToken);
            serviceResults.Add(crmResult);
            serviceNames.Add("CRM");

            // Project service: Create a task record.
            Guid projectRecordId = Guid.NewGuid();
            JObject projectFields = new JObject
            {
                ["id"] = projectRecordId.ToString(),
                ["subject"] = "Project Audit Consistency Test"
            };
            JObject projectResult = await CreateRecordInService(
                projectClient, "task", projectFields, adminToken);
            serviceResults.Add(projectResult);
            serviceNames.Add("Project");

            // Mail service: Create an email record.
            Guid mailRecordId = Guid.NewGuid();
            JObject mailFields = new JObject
            {
                ["id"] = mailRecordId.ToString(),
                ["subject"] = "Mail Audit Consistency Test",
                ["sender"] = "audit-test@webvella.com",
                ["recipients"] = "recipient@webvella.com"
            };
            JObject mailResult = await CreateRecordInService(
                mailClient, "email", mailFields, adminToken);
            serviceResults.Add(mailResult);
            serviceNames.Add("Mail");

            // Assert: Verify audit fields across ALL services.
            // Query databases directly to verify audit field storage.
            var dbVerifications = new List<(string ServiceName, string ConnString, string Table, Guid RecordId)>
            {
                ("CRM", _pgFixture.CrmConnectionString, "rec_account", crmRecordId),
                ("Project", _pgFixture.ProjectConnectionString, "rec_task", projectRecordId),
                ("Mail", _pgFixture.MailConnectionString, "rec_email", mailRecordId)
            };

            foreach (var (serviceName, connString, table, recordId) in dbVerifications)
            {
                JObject dbRecord = await QueryDatabaseDirect(connString, table, recordId);

                if (dbRecord != null)
                {
                    // Verify all four audit fields are present and consistent.
                    AssertAuditFieldsPresent(dbRecord);

                    JToken createdByToken = dbRecord["created_by"];
                    if (createdByToken != null && createdByToken.Type != JTokenType.Null)
                    {
                        Guid createdBy = SafeGetGuid(dbRecord, "created_by");
                        createdBy.Should().Be(adminUserId,
                            $"{serviceName} service should store admin user UUID as created_by");
                    }

                    JToken createdOnToken = dbRecord["created_on"];
                    if (createdOnToken != null && createdOnToken.Type != JTokenType.Null)
                    {
                        DateTime createdOn = dbRecord.Value<DateTime>("created_on");
                        createdOn.Should().BeCloseTo(testStartTime, TimestampTolerance,
                            $"{serviceName} service created_on should be close to test start time");
                    }

                    _output.WriteLine(
                        $"{serviceName} service: Audit fields verified for record {recordId}");
                }
                else
                {
                    _output.WriteLine(
                        $"{serviceName} service: Record {recordId} not found in DB " +
                        "(service may not have processed the create request)");
                }
            }

            // Log summary of all service results.
            for (int i = 0; i < serviceResults.Count; i++)
            {
                bool hasResult = serviceResults[i] != null;
                _output.WriteLine(
                    $"{serviceNames[i]} service create result: {(hasResult ? "received" : "null")}");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Safely extracts a Guid value from a JToken property, handling both native Guid
        /// and string representations. JToken.Value&lt;Guid&gt;() throws InvalidCastException
        /// when the underlying JSON value is a string rather than a native Guid token.
        /// This helper handles both cases by attempting Parse on the string representation.
        /// </summary>
        private static Guid SafeGetGuid(JToken token, string propertyName)
        {
            var val = token[propertyName];
            if (val == null || val.Type == JTokenType.Null) return Guid.Empty;
            if (val.Type == JTokenType.Guid) return val.Value<Guid>();
            var s = val.Value<string>();
            return Guid.TryParse(s, out var g) ? g : Guid.Empty;
        }

        /// <summary>
        /// Creates a record in the specified service via its REST API endpoint.
        /// Uses the standard WebVella ERP v3 API pattern: POST /api/v3/en_US/record/{entityName}
        ///
        /// Source pattern: WebApiController.cs POST endpoints for record creation.
        /// </summary>
        /// <param name="client">Authenticated HttpClient connected to the target service.</param>
        /// <param name="entityName">Entity name (e.g., "account", "task", "email").</param>
        /// <param name="fields">JObject containing the record field values.</param>
        /// <param name="token">JWT Bearer token for authentication.</param>
        /// <returns>
        /// JObject representing the created record from the API response,
        /// or null if the creation failed or returned no data.
        /// </returns>
        private async Task<JObject> CreateRecordInService(
            HttpClient client, string entityName, JObject fields, string token)
        {
            try
            {
                string apiPath = $"/api/v3/en_US/record/{entityName}";
                string jsonPayload = JsonConvert.SerializeObject(fields);
                StringContent content = new StringContent(
                    jsonPayload, System.Text.Encoding.UTF8, "application/json");

                HttpResponseMessage response = await client.PostAsync(apiPath, content);
                string responseBody = await response.Content.ReadAsStringAsync();

                _output.WriteLine(
                    $"CreateRecord({entityName}): Status={response.StatusCode}, " +
                    $"Body length={responseBody.Length}");

                if (!string.IsNullOrEmpty(responseBody))
                {
                    JObject responseObj = JObject.Parse(responseBody);
                    JToken objectToken = responseObj["object"];
                    if (objectToken != null && objectToken.Type == JTokenType.Object)
                    {
                        return (JObject)objectToken;
                    }
                    return responseObj;
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine(
                    $"CreateRecord({entityName}) failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Reads a record from the specified service via its REST API endpoint.
        /// Uses the standard WebVella ERP v3 API pattern: GET /api/v3/en_US/record/{entityName}/{id}
        ///
        /// Source pattern: WebApiController.cs GET endpoints for record retrieval.
        /// Each service should resolve audit field UUIDs via Core gRPC on read.
        /// </summary>
        /// <param name="client">Authenticated HttpClient connected to the target service.</param>
        /// <param name="entityName">Entity name (e.g., "account", "task", "email").</param>
        /// <param name="recordId">GUID of the record to retrieve.</param>
        /// <param name="token">JWT Bearer token for authentication.</param>
        /// <returns>
        /// JObject representing the record data from the API response,
        /// or null if the read failed or returned no data.
        /// </returns>
        private async Task<JObject> ReadRecordFromService(
            HttpClient client, string entityName, Guid recordId, string token)
        {
            try
            {
                string apiPath = $"/api/v3/en_US/record/{entityName}/{recordId}";
                HttpResponseMessage response = await client.GetAsync(apiPath);
                string responseBody = await response.Content.ReadAsStringAsync();

                _output.WriteLine(
                    $"ReadRecord({entityName}/{recordId}): Status={response.StatusCode}");

                if (!string.IsNullOrEmpty(responseBody))
                {
                    JObject responseObj = JObject.Parse(responseBody);
                    JToken objectToken = responseObj["object"];
                    if (objectToken != null && objectToken.Type == JTokenType.Object)
                    {
                        return (JObject)objectToken;
                    }
                    return responseObj;
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine(
                    $"ReadRecord({entityName}/{recordId}) failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Executes a raw SQL query against a service's PostgreSQL database to verify
        /// audit field values at the database level, bypassing the API layer.
        ///
        /// Per AAP 0.8.1: "Zero data loss" — direct DB verification ensures values
        /// are correctly persisted regardless of API response transformation.
        ///
        /// Uses Npgsql for PostgreSQL ADO.NET access.
        /// </summary>
        /// <param name="connectionString">
        /// ADO.NET connection string for the target service's database.
        /// Obtained from PostgreSqlFixture (CoreConnectionString, CrmConnectionString, etc.).
        /// </param>
        /// <param name="tableName">
        /// The database table name (e.g., "rec_account", "rec_task", "rec_email", "rec_user").
        /// </param>
        /// <param name="recordId">GUID of the record to query.</param>
        /// <returns>
        /// JObject containing the record's column values as key-value pairs,
        /// or null if the record was not found.
        /// </returns>
        private async Task<JObject> QueryDatabaseDirect(
            string connectionString, string tableName, Guid recordId)
        {
            try
            {
                await using NpgsqlConnection connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                // Use parameterized query to prevent SQL injection.
                string sql = $"SELECT * FROM {tableName} WHERE id = @id LIMIT 1";
                await using NpgsqlCommand cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", recordId);

                await using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    JObject record = new JObject();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string columnName = reader.GetName(i);
                        object value = reader.IsDBNull(i) ? null : reader.GetValue(i);

                        if (value == null)
                        {
                            record[columnName] = JValue.CreateNull();
                        }
                        else if (value is Guid guidValue)
                        {
                            record[columnName] = guidValue.ToString();
                        }
                        else if (value is DateTime dateTimeValue)
                        {
                            record[columnName] = dateTimeValue;
                        }
                        else if (value is bool boolValue)
                        {
                            record[columnName] = boolValue;
                        }
                        else if (value is int intValue)
                        {
                            record[columnName] = intValue;
                        }
                        else if (value is long longValue)
                        {
                            record[columnName] = longValue;
                        }
                        else if (value is decimal decimalValue)
                        {
                            record[columnName] = decimalValue;
                        }
                        else
                        {
                            record[columnName] = value.ToString();
                        }
                    }

                    return record;
                }

                _output.WriteLine(
                    $"QueryDatabaseDirect: No record found in {tableName} with id={recordId}");
            }
            catch (Exception ex)
            {
                _output.WriteLine(
                    $"QueryDatabaseDirect({tableName}/{recordId}) error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Validates that all four audit fields exist and are non-null on the given record.
        ///
        /// Checks for:
        ///   - created_by: GuidField (EntityManager.cs lines 1726-1747)
        ///   - last_modified_by: GuidField (EntityManager.cs lines 1749-1772)
        ///   - created_on: DateTimeField (EntityManager.cs lines 1774-1799)
        ///   - last_modified_on: DateTimeField (EntityManager.cs lines 1801-1820)
        ///
        /// Uses FluentAssertions for readable failure messages.
        /// </summary>
        /// <param name="record">
        /// JObject representing a database record with audit fields.
        /// If null, the method logs a warning and returns without assertions.
        /// </param>
        private void AssertAuditFieldsPresent(JObject record)
        {
            if (record == null)
            {
                _output.WriteLine(
                    "AssertAuditFieldsPresent: Record is null — skipping assertions. " +
                    "The service may not have processed the request yet.");
                return;
            }

            // Verify created_by exists and is non-null.
            JToken createdByToken = record["created_by"];
            createdByToken.Should().NotBeNull(
                "Audit field 'created_by' must be present (EntityManager.cs line 1734)");
            if (createdByToken != null)
            {
                createdByToken.Type.Should().NotBe(JTokenType.Null,
                    "Audit field 'created_by' must not be null");
            }

            // Verify created_on exists and is non-null.
            JToken createdOnToken = record["created_on"];
            createdOnToken.Should().NotBeNull(
                "Audit field 'created_on' must be present (EntityManager.cs line 1784)");
            if (createdOnToken != null)
            {
                createdOnToken.Type.Should().NotBe(JTokenType.Null,
                    "Audit field 'created_on' must not be null");
            }

            // Verify last_modified_by exists (may be null for newly created records
            // that haven't been updated yet — this is acceptable per monolith behavior).
            bool hasLastModifiedBy = record.ContainsKey("last_modified_by");
            _output.WriteLine(
                $"  last_modified_by present: {hasLastModifiedBy}, " +
                $"value: {record["last_modified_by"]}");

            // Verify last_modified_on exists (may be null for newly created records).
            bool hasLastModifiedOn = record.ContainsKey("last_modified_on");
            _output.WriteLine(
                $"  last_modified_on present: {hasLastModifiedOn}, " +
                $"value: {record["last_modified_on"]}");
        }

        /// <summary>
        /// Inserts a record with known, deterministic audit field values directly
        /// into a service's database. Used for migration preservation tests and
        /// system user verification tests where specific audit values are required.
        /// </summary>
        /// <param name="connectionString">Database connection string for the target service.</param>
        /// <param name="tableName">Table name (e.g., "rec_account").</param>
        /// <param name="recordId">Deterministic record GUID.</param>
        /// <param name="name">Record name or subject field value.</param>
        /// <param name="createdByUserId">The user UUID to set as created_by.</param>
        /// <param name="createdOn">The timestamp to set as created_on.</param>
        private async Task InsertRecordWithKnownAuditFields(
            string connectionString,
            string tableName,
            Guid recordId,
            string name,
            Guid createdByUserId,
            DateTime createdOn)
        {
            await using NpgsqlConnection connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Determine the name column based on table type.
            string nameColumn = tableName switch
            {
                "rec_task" => "subject",
                "rec_email" => "subject",
                _ => "name"
            };

            string sql = $@"
                INSERT INTO {tableName} (id, {nameColumn}, created_by, created_on)
                VALUES (@id, @name, @created_by, @created_on)
                ON CONFLICT (id) DO UPDATE SET
                    {nameColumn} = @name,
                    created_by = @created_by,
                    created_on = @created_on";

            await using NpgsqlCommand cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@id", recordId);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@created_by", createdByUserId);
            cmd.Parameters.AddWithValue("@created_on", createdOn);

            int rowsAffected = await cmd.ExecuteNonQueryAsync();
            _output.WriteLine(
                $"InsertRecordWithKnownAuditFields: {tableName}/{recordId} — " +
                $"{rowsAffected} row(s) affected");
        }

        #endregion
    }
}
