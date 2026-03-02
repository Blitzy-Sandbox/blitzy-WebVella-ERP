using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace WebVella.Erp.Tests.Integration.Fixtures
{
    /// <summary>
    /// xUnit IAsyncLifetime fixture that uses Testcontainers.PostgreSql (v4.10.0) to
    /// create isolated PostgreSQL databases per service. Creates databases named
    /// erp_core, erp_crm, erp_project, erp_mail matching the AAP 0.7.4 Docker Compose
    /// topology for database-per-service validation.
    ///
    /// Provides connection strings for each service database, supports running EF Core
    /// migrations against test databases, and enables raw Npgsql connections for direct
    /// SQL verification (record counts, checksums) per AAP 0.8.1 zero data loss requirements.
    ///
    /// Usage:
    ///   public class MyMigrationTests : IClassFixture&lt;PostgreSqlFixture&gt;
    ///   {
    ///       private readonly PostgreSqlFixture _fixture;
    ///       public MyMigrationTests(PostgreSqlFixture fixture) { _fixture = fixture; }
    ///   }
    ///
    /// Container lifecycle is managed by xUnit: InitializeAsync starts the PostgreSQL
    /// container and creates all per-service databases; DisposeAsync stops and removes
    /// the container along with all data for a clean test state.
    /// </summary>
    public class PostgreSqlFixture : IAsyncLifetime
    {
        #region Constants

        /// <summary>
        /// Docker image for the PostgreSQL container.
        /// Per AAP 0.7.4 Docker Compose spec: image: postgres:16-alpine
        /// </summary>
        public const string ImageName = "postgres:16-alpine";

        /// <summary>
        /// Default PostgreSQL port inside the container.
        /// Mapped to a dynamic host port by Testcontainers.
        /// </summary>
        private const int PostgresInternalPort = 5432;

        /// <summary>
        /// Default username for the PostgreSQL container.
        /// Matches Config.json pattern: User Id=dev
        /// </summary>
        private const string DefaultUsername = "dev";

        /// <summary>
        /// Default password for the PostgreSQL container.
        /// Matches Config.json pattern: Password=dev
        /// </summary>
        private const string DefaultPassword = "dev";

        /// <summary>
        /// Default admin database used during container initialization.
        /// Service databases are created via this admin connection.
        /// </summary>
        private const string DefaultAdminDatabase = "postgres";

        #endregion

        #region Private Fields

        /// <summary>
        /// The Testcontainers-managed PostgreSQL Docker container instance.
        /// Lifecycle managed via InitializeAsync/DisposeAsync.
        /// </summary>
        private PostgreSqlContainer _container;

        /// <summary>
        /// The list of per-service database names to create during initialization.
        /// Matches the AAP 0.7.4 Docker Compose topology:
        ///   postgres-core → erp_core
        ///   postgres-crm → erp_crm
        ///   postgres-project → erp_project
        ///   postgres-mail → erp_mail
        /// </summary>
        private readonly IReadOnlyList<string> _databaseNames = new List<string>
        {
            "erp_core",
            "erp_crm",
            "erp_project",
            "erp_mail",
            "erp_reporting",
            "erp_admin"
        };

        /// <summary>
        /// PostgreSQL extensions required by the ERP engine.
        /// Source: DbRepository.CreatePostgresqlExtensions() in WebVella.Erp/Database/DbRepository.cs
        ///   - uuid-ossp: UUID generation functions (used for primary keys and entity IDs)
        ///   - pg_trgm: Trigram matching for full-text search (used by SearchManager and FTS engine)
        /// </summary>
        private static readonly string[] RequiredExtensions = new[]
        {
            "uuid-ossp",
            "pg_trgm"
        };

        #endregion

        #region Public Properties

        /// <summary>
        /// Connection string for the erp_core database owned by the Core Platform service.
        /// Format matches Config.json pattern:
        ///   Server={host};Port={port};User Id=dev;Password=dev;Database=erp_core;
        ///   Pooling=true;MinPoolSize=1;MaxPoolSize=100;CommandTimeout=120
        /// </summary>
        public string CoreConnectionString { get; private set; }

        /// <summary>
        /// Connection string for the erp_crm database owned by the CRM service.
        /// Format matches Config.json pattern with Database=erp_crm.
        /// </summary>
        public string CrmConnectionString { get; private set; }

        /// <summary>
        /// Connection string for the erp_project database owned by the Project/Task service.
        /// Format matches Config.json pattern with Database=erp_project.
        /// </summary>
        public string ProjectConnectionString { get; private set; }

        /// <summary>
        /// Connection string for the erp_mail database owned by the Mail/Notification service.
        /// Format matches Config.json pattern with Database=erp_mail.
        /// </summary>
        public string MailConnectionString { get; private set; }

        /// <summary>
        /// Connection string for the erp_reporting database owned by the Reporting service.
        /// Format matches Config.json pattern with Database=erp_reporting.
        /// </summary>
        public string ReportingConnectionString { get; private set; }

        /// <summary>
        /// Connection string for the erp_admin database owned by the Admin/SDK service.
        /// Format matches Config.json pattern with Database=erp_admin.
        /// </summary>
        public string AdminConnectionString { get; private set; }

        /// <summary>
        /// The hostname of the PostgreSQL container on the Docker host.
        /// Typically "localhost" when using the default Docker network.
        /// </summary>
        public string Host { get; private set; }

        /// <summary>
        /// The dynamically mapped host port for the PostgreSQL container.
        /// Testcontainers maps the container's internal port 5432 to this random host port.
        /// </summary>
        public int Port { get; private set; }

        #endregion

        #region IAsyncLifetime Implementation

        /// <summary>
        /// Starts the PostgreSQL Docker container and creates all per-service databases
        /// with the required PostgreSQL extensions.
        ///
        /// Execution steps:
        /// 1. Build and start the PostgreSQL container (postgres:16-alpine)
        /// 2. Extract connection info (host, mapped port)
        /// 3. Create per-service databases: erp_core, erp_crm, erp_project, erp_mail
        /// 4. Build connection strings matching Config.json format for each database
        /// 5. Enable required PostgreSQL extensions (uuid-ossp, pg_trgm) per database
        ///
        /// Per AAP 0.4.1: Each microservice owns an independent PostgreSQL database.
        /// Per AAP 0.7.4: Docker Compose topology uses postgres:16-alpine containers.
        /// Per AAP 0.8.2: Schema migration tests require isolated per-service databases.
        /// </summary>
        public async Task InitializeAsync()
        {
            // Step 1: Build and start the PostgreSQL container.
            // The container uses postgres:16-alpine matching AAP 0.7.4 Docker Compose spec.
            // Username/password match Config.json format (User Id=dev, Password=dev).
            // The admin database "postgres" is used for initial connection to create service DBs.
            _container = new PostgreSqlBuilder(ImageName)
                .WithDatabase(DefaultAdminDatabase)
                .WithUsername(DefaultUsername)
                .WithPassword(DefaultPassword)
                .WithName($"pg-integration-test-{Guid.NewGuid():N}")
                .WithWaitStrategy(
                    Wait.ForUnixContainer()
                        .UntilCommandIsCompleted("pg_isready", "-h", "localhost", "-p", "5432"))
                .Build();

            await _container.StartAsync().ConfigureAwait(false);

            // Step 2: Extract connection info from the running container.
            // Testcontainers maps internal port 5432 to a random available host port.
            Host = _container.Hostname;
            Port = _container.GetMappedPublicPort(PostgresInternalPort);

            // Step 3: Create per-service databases.
            // Each database is created with a separate connection because CREATE DATABASE
            // cannot run inside a transaction block in PostgreSQL.
            // Source pattern: DbContext.CreateContext() in WebVella.Erp/Database/DbContext.cs
            string adminConnectionString = BuildConnectionString(DefaultAdminDatabase);
            foreach (string databaseName in _databaseNames)
            {
                await CreateDatabaseAsync(adminConnectionString, databaseName).ConfigureAwait(false);
            }

            // Step 4: Build connection strings for each per-service database.
            // Format matches Config.json line 4:
            //   Server={host};Port={port};User Id=dev;Password=dev;Database={db};
            //   Pooling=true;MinPoolSize=1;MaxPoolSize=100;CommandTimeout=120
            CoreConnectionString = BuildConnectionString("erp_core");
            CrmConnectionString = BuildConnectionString("erp_crm");
            ProjectConnectionString = BuildConnectionString("erp_project");
            MailConnectionString = BuildConnectionString("erp_mail");
            ReportingConnectionString = BuildConnectionString("erp_reporting");
            AdminConnectionString = BuildConnectionString("erp_admin");

            // Step 5: Enable required PostgreSQL extensions per database.
            // Source: DbRepository.CreatePostgresqlExtensions() in WebVella.Erp/Database/DbRepository.cs
            //   - uuid-ossp: Required for UUID generation (entity IDs, primary keys)
            //   - pg_trgm: Required for trigram-based full-text search
            foreach (string databaseName in _databaseNames)
            {
                string connectionString = GetConnectionString(databaseName);
                await InstallExtensionsAsync(connectionString).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Stops and removes the PostgreSQL Docker container, destroying all databases
        /// and data for a completely clean test state.
        ///
        /// Exceptions during disposal are swallowed to prevent test cleanup failures
        /// from masking actual test assertion failures.
        /// </summary>
        public async Task DisposeAsync()
        {
            if (_container != null)
            {
                try
                {
                    await _container.StopAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Swallow exceptions during container stop.
                    // Container may have already stopped or been removed externally.
                }

                try
                {
                    await _container.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Swallow exceptions during container disposal.
                    // Prevents cleanup failures from masking test assertion failures.
                }
            }
        }

        #endregion

        #region Public Helper Methods

        /// <summary>
        /// Returns the connection string for the specified database name.
        ///
        /// Supported database names match the AAP 0.7.4 Docker Compose topology:
        ///   - "erp_core"      → Core Platform service database
        ///   - "erp_crm"       → CRM service database
        ///   - "erp_project"   → Project/Task service database
        ///   - "erp_mail"      → Mail/Notification service database
        ///   - "erp_reporting" → Reporting service database
        ///   - "erp_admin"     → Admin/SDK service database
        /// </summary>
        /// <param name="databaseName">
        /// The database name (one of: erp_core, erp_crm, erp_project, erp_mail, erp_reporting, erp_admin).
        /// </param>
        /// <returns>
        /// The full ADO.NET connection string for the specified database.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the database name is not one of the six known service databases.
        /// </exception>
        public string GetConnectionString(string databaseName)
        {
            switch (databaseName)
            {
                case "erp_core":
                    return CoreConnectionString;
                case "erp_crm":
                    return CrmConnectionString;
                case "erp_project":
                    return ProjectConnectionString;
                case "erp_mail":
                    return MailConnectionString;
                case "erp_reporting":
                    return ReportingConnectionString;
                case "erp_admin":
                    return AdminConnectionString;
                default:
                    throw new ArgumentException(
                        $"Unknown database name: '{databaseName}'. " +
                        "Expected one of: erp_core, erp_crm, erp_project, erp_mail, erp_reporting, erp_admin.",
                        nameof(databaseName));
            }
        }

        /// <summary>
        /// Runs EF Core migrations against the specified database using the provided
        /// connection string. Creates a <typeparamref name="TDbContext"/> instance with
        /// Npgsql configuration and applies all pending migrations.
        ///
        /// Per AAP 0.5.1: Each service uses EF Core Migrations for schema management.
        /// Per AAP 0.7.5: Each service's initial EF Core migration codifies the current
        /// state of all entities owned by that service.
        ///
        /// The <typeparamref name="TDbContext"/> type must have a constructor accepting
        /// <see cref="DbContextOptions{TDbContext}"/> as a parameter, which is the standard
        /// EF Core DbContext constructor pattern.
        /// </summary>
        /// <typeparam name="TDbContext">
        /// The EF Core DbContext type for the service (e.g., CoreDbContext, CrmDbContext,
        /// ProjectDbContext, MailDbContext).
        /// </typeparam>
        /// <param name="connectionString">
        /// The ADO.NET connection string for the target database. Typically obtained from
        /// <see cref="GetConnectionString(string)"/> or one of the typed properties
        /// (CoreConnectionString, CrmConnectionString, etc.).
        /// </param>
        public async Task RunMigrationsAsync<TDbContext>(string connectionString)
            where TDbContext : DbContext
        {
            // Build EF Core options with the Npgsql provider targeting the specified database.
            // UseNpgsql comes from Npgsql.EntityFrameworkCore.PostgreSQL package.
            var optionsBuilder = new DbContextOptionsBuilder<TDbContext>();
            optionsBuilder.UseNpgsql(connectionString);
            DbContextOptions<TDbContext> options = optionsBuilder.Options;

            // Create the DbContext instance using Activator to support generic type parameter.
            // The TDbContext must have a constructor: public TDbContext(DbContextOptions<TDbContext> options)
            // This is the standard EF Core pattern for all service DbContexts per AAP 0.5.1.
            await using TDbContext context = (TDbContext)Activator.CreateInstance(typeof(TDbContext), options);
            if (context == null)
            {
                throw new InvalidOperationException(
                    $"Failed to create an instance of {typeof(TDbContext).FullName}. " +
                    $"Ensure it has a public constructor accepting DbContextOptions<{typeof(TDbContext).Name}>.");
            }

            // Apply all pending EF Core migrations to bring the database schema up to date.
            // Per AAP 0.7.5: "Each service's initial EF Core migration will codify the
            // current state of all entities owned by that service."
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Creates and opens a raw NpgsqlConnection to the specified service database.
        /// The caller is responsible for disposing the returned connection.
        ///
        /// This method is useful for direct SQL verification in integration tests:
        ///   - Comparing record counts before and after migration
        ///   - Computing checksums for zero data loss validation
        ///   - Verifying schema structure matches expectations
        ///
        /// Per AAP 0.8.1: "Zero data loss during schema migration — every record in every
        /// rec_* table must be accounted for in the target service's database."
        /// Per AAP 0.8.2: "Schema migration tests ensuring zero data loss by comparing
        /// record counts and checksums before and after migration."
        ///
        /// Source pattern: DbConnection constructor in WebVella.Erp/Database/DbConnection.cs
        /// lines 37-43 for NpgsqlConnection creation.
        /// </summary>
        /// <param name="databaseName">
        /// The database name (one of: erp_core, erp_crm, erp_project, erp_mail, erp_reporting, erp_admin).
        /// </param>
        /// <returns>
        /// An opened NpgsqlConnection to the specified database. The caller must dispose it.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the database name is not one of the six known service databases.
        /// </exception>
        public async Task<NpgsqlConnection> CreateRawConnectionAsync(string databaseName)
        {
            string connectionString = GetConnectionString(databaseName);
            NpgsqlConnection connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            return connection;
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Builds an ADO.NET connection string for the specified database.
        /// Format matches the monolith's Config.json pattern (line 4):
        ///   Server={host};Port={port};User Id=dev;Password=dev;Database={db};
        ///   Pooling=true;MinPoolSize=1;MaxPoolSize=100;CommandTimeout=120
        ///
        /// Connection pooling settings match the AAP 0.8.3 requirements:
        ///   - MinPoolSize=1: Minimum one connection in the pool
        ///   - MaxPoolSize=100: Configurable per service (100 matches monolith default)
        ///   - CommandTimeout=120: 120 seconds query timeout for test environments
        /// </summary>
        /// <param name="database">The PostgreSQL database name.</param>
        /// <returns>A formatted ADO.NET connection string.</returns>
        private string BuildConnectionString(string database)
        {
            return string.Format(
                "Server={0};Port={1};User Id={2};Password={3};Database={4};" +
                "Pooling=true;MinPoolSize=1;MaxPoolSize=100;CommandTimeout=120",
                Host,
                Port,
                DefaultUsername,
                DefaultPassword,
                database);
        }

        /// <summary>
        /// Creates a new PostgreSQL database by connecting to the admin database and
        /// executing CREATE DATABASE. Each call uses its own connection because
        /// CREATE DATABASE cannot execute inside a transaction block.
        /// </summary>
        /// <param name="adminConnectionString">
        /// Connection string for the admin (postgres) database.
        /// </param>
        /// <param name="databaseName">
        /// Name of the database to create.
        /// </param>
        private async Task CreateDatabaseAsync(string adminConnectionString, string databaseName)
        {
            await using NpgsqlConnection connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            // CREATE DATABASE cannot run inside a transaction block in PostgreSQL.
            // Each database is created with a fresh, non-transactional connection.
            string sql = $"CREATE DATABASE \"{databaseName}\";";
            await using NpgsqlCommand command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Installs the required PostgreSQL extensions in the specified database.
        /// Extensions installed:
        ///   - uuid-ossp: Provides UUID generation functions (uuid_generate_v4, etc.)
        ///     Required by the ERP engine for entity ID generation and primary key defaults.
        ///   - pg_trgm: Provides trigram matching operators and index support.
        ///     Required by SearchManager for PostgreSQL full-text search with ILIKE.
        ///
        /// Source: DbRepository.CreatePostgresqlExtensions() in
        /// WebVella.Erp/Database/DbRepository.cs lines 26-46.
        /// </summary>
        /// <param name="connectionString">
        /// Connection string for the target database.
        /// </param>
        private async Task InstallExtensionsAsync(string connectionString)
        {
            await using NpgsqlConnection connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            foreach (string extension in RequiredExtensions)
            {
                string sql = $"CREATE EXTENSION IF NOT EXISTS \"{extension}\";";
                await using NpgsqlCommand command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        #endregion
    }
}
