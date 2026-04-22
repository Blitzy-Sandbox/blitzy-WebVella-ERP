using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.SQS;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using WebVellaErp.Invoicing.Migrations;

namespace WebVellaErp.Invoicing.Tests.Integration
{
    /// <summary>
    /// Shared xUnit test fixture that manages LocalStack connection setup,
    /// FluentMigrator migration execution, and AWS SDK client configuration
    /// for ALL integration tests in the Invoicing service.
    ///
    /// Integration Test Pattern (per AAP §0.8.4):
    /// 1. docker compose up -d   (starts LocalStack with RDS PostgreSQL, SNS, SSM)
    /// 2. dotnet test --filter Category=Integration
    /// 3. docker compose down     (teardown)
    ///
    /// This fixture manages:
    /// - PostgreSQL connection to LocalStack RDS
    /// - FluentMigrator migration execution (creates invoicing schema)
    /// - AWS SDK clients (SNS, SQS, SSM) configured for LocalStack
    /// - Database cleanup between test classes
    ///
    /// All monetary values use decimal type (NEVER double/float)
    /// All SQL uses parameterized queries (NEVER string concatenation)
    ///
    /// Replaces the monolith's DbContext.cs ambient context pattern
    /// (connection management, transaction lifecycle) and DbConnection.cs
    /// (Npgsql wrapper) — adapted for test infrastructure against LocalStack.
    /// </summary>
    public class LocalStackFixture : IAsyncLifetime
    {
        /// <summary>
        /// LocalStack endpoint URL per AAP §0.8.6: AWS_ENDPOINT_URL = http://localhost:4566.
        /// All AWS SDK clients (SNS, SQS, SSM) are configured to connect through this endpoint.
        /// </summary>
        private const string LocalStackEndpoint = "http://localhost:4566";

        /// <summary>
        /// AWS region for LocalStack per AAP §0.8.6: AWS_REGION = us-east-1.
        /// </summary>
        private const string TestRegion = "us-east-1";

        /// <summary>
        /// Test-specific database name for the invoicing service integration tests.
        /// </summary>
        private const string TestDatabaseName = "invoicing_test";

        /// <summary>
        /// Default PostgreSQL connection string for LocalStack RDS PostgreSQL.
        /// LocalStack RDS PostgreSQL exposes on port 4510 by default.
        /// For tests, a hardcoded default is acceptable (per AAP §0.8.6,
        /// production code uses SSM SecureString — NEVER environment variables).
        /// </summary>
        private const string DefaultConnectionString =
            "Host=localhost;Port=4510;Database=invoicing_test;Username=test;Password=test;";

        /// <summary>
        /// Static semaphore to serialize fixture initialization across parallel test classes.
        /// xUnit creates a separate IClassFixture instance per test class. When test classes
        /// run in parallel, multiple fixtures attempt InitializeAsync concurrently, racing on
        /// CREATE DATABASE and CREATE SCHEMA operations. This semaphore ensures only one
        /// fixture performs database/migration setup at a time.
        /// </summary>
        private static readonly SemaphoreSlim _initLock = new(1, 1);

        /// <summary>
        /// Static flag tracking whether FluentMigrator migrations have been applied.
        /// Once the first fixture instance successfully runs MigrateUp(), subsequent
        /// instances skip the migration step to avoid concurrent CREATE SCHEMA conflicts
        /// (PostgreSQL error 23505 on pg_namespace_nspname_index).
        /// </summary>
        private static bool _migrationsCompleted;

        /// <summary>
        /// Reference count of active fixture instances. Incremented in InitializeAsync,
        /// decremented in DisposeAsync. The last fixture to dispose performs schema cleanup.
        /// This prevents early-disposing fixtures from dropping the schema while other
        /// test classes still depend on it.
        /// </summary>
        private static int _activeFixtureCount;

        /// <summary>
        /// PostgreSQL connection string for integration tests.
        /// Configurable via TEST_DB_CONNECTION_STRING environment variable,
        /// falling back to <see cref="DefaultConnectionString"/> if not set.
        /// </summary>
        public string ConnectionString { get; private set; } = string.Empty;

        /// <summary>
        /// SNS client configured for LocalStack (ServiceURL=http://localhost:4566).
        /// Used by integration tests to verify domain event publishing:
        /// invoicing.invoice.created, invoicing.invoice.paid,
        /// invoicing.invoice.voided, invoicing.payment.processed.
        /// Per AAP §0.8.4: no mocked AWS SDK calls in integration tests.
        /// </summary>
        public IAmazonSimpleNotificationService SnsClient { get; private set; } = null!;

        /// <summary>
        /// SQS client configured for LocalStack (ServiceURL=http://localhost:4566).
        /// Used by integration tests to subscribe test queues to SNS topics
        /// and read published domain events for verification.
        /// Required for the SNS→SQS subscription pattern in EventPublishingIntegrationTests.
        /// </summary>
        public IAmazonSQS SqsClient { get; private set; } = null!;

        /// <summary>
        /// SSM Parameter Store client configured for LocalStack.
        /// Used for seeding DB_CONNECTION_STRING as SSM SecureString
        /// (per AAP §0.8.6: secrets via SSM, NEVER environment variables)
        /// and exposing the client for integration tests that verify
        /// SSM parameter retrieval patterns.
        /// </summary>
        public IAmazonSimpleSystemsManagement SsmClient { get; private set; } = null!;

        /// <summary>
        /// DI container with FluentMigrator runner configured for PostgreSQL.
        /// Exposed as a public property for test classes to resolve
        /// <see cref="IMigrationRunner"/> for migration up/down operations.
        /// </summary>
        public IServiceProvider ServiceProvider { get; private set; } = null!;

        /// <summary>
        /// Initializes the complete test infrastructure in sequence:
        /// 1. Determines PostgreSQL connection string (env var or default)
        /// 2. Configures real AWS SDK clients for LocalStack (SNS, SQS, SSM)
        /// 3. Verifies PostgreSQL connection health (SELECT 1)
        /// 4. Configures and runs FluentMigrator migrations (creates invoicing schema)
        /// 5. Seeds SSM parameter for connection string retrieval tests
        ///
        /// xUnit calls this once before the first test in any class using this fixture.
        /// Connection pattern from source DbConnection.cs lines 37-42:
        /// new NpgsqlConnection(connectionString); connection.Open();
        /// Extension setup pattern from source DbRepository.CreatePostgresqlExtensions (uuid-ossp).
        /// </summary>
        public async Task InitializeAsync()
        {
            // Step 1: Determine connection string from environment or use default.
            // Environment variable allows CI/CD pipelines to override the connection target.
            var envConnectionString = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION_STRING");
            ConnectionString = !string.IsNullOrEmpty(envConnectionString)
                ? envConnectionString
                : DefaultConnectionString;

            // Step 2: Configure AWS SDK clients for LocalStack.
            // Per AAP §0.8.4: ALL integration tests MUST execute against LocalStack.
            // Per AAP §0.8.6: AWS_ENDPOINT_URL = http://localhost:4566.
            // BasicAWSCredentials with dummy "test"/"test" keys for LocalStack authentication.
            var credentials = new BasicAWSCredentials("test", "test");

            var snsConfig = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = LocalStackEndpoint
            };
            SnsClient = new AmazonSimpleNotificationServiceClient(credentials, snsConfig);

            var sqsConfig = new AmazonSQSConfig
            {
                ServiceURL = LocalStackEndpoint
            };
            SqsClient = new AmazonSQSClient(credentials, sqsConfig);

            var ssmConfig = new AmazonSimpleSystemsManagementConfig
            {
                ServiceURL = LocalStackEndpoint
            };
            SsmClient = new AmazonSimpleSystemsManagementClient(credentials, ssmConfig);

            // Step 3: Serialize database creation and migration execution.
            // Multiple IClassFixture<LocalStackFixture> instances run InitializeAsync concurrently.
            // Without serialization, concurrent CREATE DATABASE and CREATE SCHEMA operations
            // trigger PostgreSQL unique constraint violations (23505 on pg_namespace_nspname_index).
            // The static semaphore ensures only one fixture performs setup at a time.
            await _initLock.WaitAsync();
            try
            {
                // Step 3a: Ensure the test database exists by connecting to the default 'postgres'
                // database and issuing CREATE DATABASE (PostgreSQL lacks IF NOT EXISTS for databases).
                try
                {
                    var adminConnectionString = "Host=localhost;Port=4510;Database=postgres;Username=test;Password=test;Timeout=10";
                    await using var adminConnection = new NpgsqlConnection(adminConnectionString);
                    await adminConnection.OpenAsync();

                    await using var checkCmd = new NpgsqlCommand(
                        $"SELECT 1 FROM pg_database WHERE datname = '{TestDatabaseName}'", adminConnection);
                    var exists = await checkCmd.ExecuteScalarAsync();

                    if (exists == null)
                    {
                        await using var createCmd = new NpgsqlCommand(
                            $"CREATE DATABASE \"{TestDatabaseName}\"", adminConnection);
                        await createCmd.ExecuteNonQueryAsync();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[LocalStackFixture] WARNING: Failed to create test database '{TestDatabaseName}'. " +
                        $"RDS-dependent integration tests will be skipped via [RdsFact]. Error: {ex.Message}");
                    return;
                }

                // Step 3b: Verify PostgreSQL connection with health check.
                // Pattern from source DbConnection.cs line 42: connection.Open();
                try
                {
                    await using var healthCheckConnection = new NpgsqlConnection(ConnectionString);
                    await healthCheckConnection.OpenAsync();
                    await using var healthCmd = new NpgsqlCommand("SELECT 1;", healthCheckConnection);
                    var healthResult = await healthCmd.ExecuteScalarAsync();
                    if (healthResult == null || Convert.ToInt32(healthResult) != 1)
                    {
                        Console.Error.WriteLine(
                            "[LocalStackFixture] WARNING: PostgreSQL health check returned unexpected result. " +
                            "RDS-dependent integration tests will be skipped via [RdsFact].");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[LocalStackFixture] WARNING: Failed to connect to PostgreSQL at '{ConnectionString}'. " +
                        $"RDS-dependent integration tests will be skipped via [RdsFact]. Error: {ex.Message}");
                    return;
                }

                // Step 4: Configure FluentMigrator DI container.
                // Each fixture instance gets its own ServiceProvider for migration runner resolution.
                var services = new ServiceCollection()
                    .AddFluentMigratorCore()
                    .ConfigureRunner(rb => rb
                        .AddPostgres()
                        .WithGlobalConnectionString(ConnectionString)
                        .ScanIn(typeof(InitialCreate).Assembly).For.Migrations())
                    .AddLogging(lb => lb.AddFluentMigratorConsole())
                    .BuildServiceProvider(false);

                ServiceProvider = services;

                // Execute migrations only once across all parallel fixture instances.
                // FluentMigrator's CREATE SCHEMA is not idempotent — concurrent MigrateUp
                // calls race on schema creation causing 23505 constraint violations.
                if (!_migrationsCompleted)
                {
                    var runner = ServiceProvider.GetRequiredService<IMigrationRunner>();
                    runner.MigrateUp();
                    _migrationsCompleted = true;
                }

                Interlocked.Increment(ref _activeFixtureCount);
            }
            finally
            {
                _initLock.Release();
            }

            // Step 5: Seed SSM parameter for testing SSM retrieval patterns.
            // Per AAP §0.8.6: DB_CONNECTION_STRING stored as SSM SecureString.
            // This is optional — do not fail test infrastructure setup if SSM
            // is not available or encounters an error.
            try
            {
                await SsmClient.PutParameterAsync(new PutParameterRequest
                {
                    Name = "/invoicing/db-connection-string",
                    Value = ConnectionString,
                    Type = ParameterType.SecureString,
                    Overwrite = true
                });
            }
            catch (Exception)
            {
                // SSM seeding is optional — do not fail test infrastructure setup
                // if SSM is not available in the LocalStack instance.
            }
        }

        /// <summary>
        /// Cleans up all test infrastructure:
        /// 1. Drops the invoicing schema via direct SQL (CASCADE removes all dependent objects)
        /// 2. Disposes AWS SDK clients (SNS, SQS, SSM)
        /// 3. Disposes the FluentMigrator ServiceProvider
        ///
        /// xUnit calls this once after the last test completes.
        /// Pattern: Clean teardown to avoid state leaking between test runs.
        /// </summary>
        public async Task DisposeAsync()
        {
            var remaining = Interlocked.Decrement(ref _activeFixtureCount);

            // Only the last fixture to dispose performs schema cleanup.
            // Earlier-disposing fixtures must NOT drop the schema — other test classes
            // may still be running against it in parallel.
            if (remaining <= 0)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(ConnectionString);
                    await connection.OpenAsync();
                    await using var dropCmd = new NpgsqlCommand(
                        "DROP SCHEMA IF EXISTS invoicing CASCADE;", connection);
                    await dropCmd.ExecuteNonQueryAsync();
                }
                catch (Exception)
                {
                    // Best-effort cleanup — do not throw during disposal
                    // as this could mask the actual test failure.
                }

                // Reset migration flag so subsequent test runs re-create the schema.
                _migrationsCompleted = false;
            }

            // Dispose AWS SDK clients to release HTTP connections and resources.
            SnsClient?.Dispose();
            SqsClient?.Dispose();
            SsmClient?.Dispose();

            // Dispose ServiceProvider if it implements IDisposable.
            // The ServiceProvider built from ServiceCollection implements IDisposable
            // and will dispose all registered singleton/scoped services.
            if (ServiceProvider is IDisposable disposableProvider)
            {
                disposableProvider.Dispose();
            }
        }

        /// <summary>
        /// Creates and opens a new <see cref="NpgsqlConnection"/> for direct SQL queries in tests.
        /// Returns the opened connection ready for immediate SQL execution.
        ///
        /// Pattern from source DbConnection.cs lines 37-42:
        /// <code>
        /// connection = new NpgsqlConnection(connectionString);
        /// connection.Open();
        /// </code>
        ///
        /// Callers are responsible for disposing the returned connection.
        /// </summary>
        /// <returns>An opened <see cref="NpgsqlConnection"/> ready for SQL execution.</returns>
        public NpgsqlConnection CreateNpgsqlConnection()
        {
            var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Truncates all tables in the invoicing schema while preserving schema structure.
        /// Tables are truncated in dependency order with CASCADE for safety:
        /// payments → line_items → invoices.
        ///
        /// Called by individual test classes in their InitializeAsync() for clean test isolation,
        /// ensuring each test class starts with empty tables.
        /// </summary>
        public async Task ResetDatabaseAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            // Check if the invoicing schema exists (it may have been dropped by migration Down tests)
            await using var checkCmd = new NpgsqlCommand(
                "SELECT schema_name FROM information_schema.schemata WHERE schema_name = 'invoicing';",
                connection);
            var schemaExists = await checkCmd.ExecuteScalarAsync();

            if (schemaExists == null)
            {
                // Schema was dropped — re-run migrations to recreate it.
                // Clear stale FluentMigrator VersionInfo so MigrateUp() recognizes the migration as needed.
                await using var clearVersionCmd = new NpgsqlCommand(
                    "DELETE FROM \"VersionInfo\" WHERE \"Version\" IS NOT NULL;", connection);
                try { await clearVersionCmd.ExecuteNonQueryAsync(); } catch { /* VersionInfo may not exist */ }

                // Build a fresh FluentMigrator runner and re-run migrations
                var services = new ServiceCollection()
                    .AddFluentMigratorCore()
                    .ConfigureRunner(rb => rb
                        .AddPostgres()
                        .WithGlobalConnectionString(ConnectionString)
                        .ScanIn(typeof(WebVellaErp.Invoicing.Migrations.InitialCreate).Assembly).For.Migrations())
                    .AddLogging(lb => lb.AddFluentMigratorConsole())
                    .BuildServiceProvider(false);

                var runner = services.GetRequiredService<FluentMigrator.Runner.IMigrationRunner>();
                runner.MigrateUp();
                return;
            }

            await using var truncateCmd = new NpgsqlCommand(
                "TRUNCATE invoicing.payments, invoicing.line_items, invoicing.invoices CASCADE;",
                connection);
            await truncateCmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Gets <see cref="IMigrationRunner"/> from the <see cref="ServiceProvider"/>
        /// and executes all discovered migrations (MigrateUp).
        /// Used by migration tests to re-run migrations after a Down() operation.
        /// </summary>
        public void RunMigrationsUp()
        {
            var runner = ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
        }

        /// <summary>
        /// Gets <see cref="IMigrationRunner"/> from the <see cref="ServiceProvider"/>
        /// and migrates down to version 0 (initial state), reversing all migrations.
        /// Used by migration tests to verify the Down() migration path.
        /// </summary>
        public void RunMigrationsDown()
        {
            var runner = ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateDown(0);
        }

        /// <summary>
        /// Creates an <see cref="ILogger{T}"/> for use in test instances of services/repositories.
        /// Returns <see cref="NullLogger{T}.Instance"/> for silent test execution
        /// without console output, providing a lightweight logger that discards all messages.
        /// </summary>
        /// <typeparam name="T">The type for which to create a logger (typically the service or repository under test).</typeparam>
        /// <returns>An <see cref="ILogger{T}"/> instance suitable for test use.</returns>
        public ILogger<T> CreateTestLogger<T>()
        {
            return NullLogger<T>.Instance;
        }
    }
}
