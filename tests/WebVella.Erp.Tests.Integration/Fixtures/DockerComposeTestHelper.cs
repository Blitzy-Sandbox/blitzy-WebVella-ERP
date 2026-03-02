using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WebVella.Erp.Tests.Integration.Fixtures
{
    /// <summary>
    /// Helper class providing programmatic control of the <c>docker-compose.localstack.yml</c>
    /// stack for end-to-end testing. Complements the Testcontainers-based fixtures by providing
    /// an alternative "full stack" orchestration mode using Docker Compose.
    ///
    /// <para>
    /// The stack includes: localstack (SQS/SNS/S3 on port 4566), postgres-core (erp_core),
    /// postgres-crm (erp_crm), postgres-project (erp_project), postgres-mail (erp_mail),
    /// redis, rabbitmq, and all microservice containers including the gateway (port 5000).
    /// </para>
    ///
    /// <para>
    /// Per AAP 0.7.4 and 0.8.3: The docker-compose.localstack.yml file must bring up a fully
    /// functional stack that passes all integration tests without any external cloud dependencies.
    /// LocalStack endpoint configuration is injectable via environment variables.
    /// </para>
    /// </summary>
    public sealed class DockerComposeTestHelper : IAsyncDisposable
    {
        // =====================================================================
        // Constants
        // =====================================================================

        /// <summary>
        /// Name of the Docker Compose file used for LocalStack E2E test orchestration.
        /// </summary>
        private const string ComposeFileName = "docker-compose.localstack.yml";

        /// <summary>
        /// Environment variable name for overriding the compose file path in CI environments.
        /// Per AAP 0.8.3: "LocalStack endpoint configuration must be injectable via environment variables."
        /// </summary>
        private const string ComposeFilePathEnvVar = "COMPOSE_FILE_PATH";

        /// <summary>
        /// Default timeout for waiting for all services to become healthy.
        /// Matches CommandTimeout=120 from Config.json line 4.
        /// </summary>
        private static readonly TimeSpan DefaultHealthCheckTimeout = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Polling interval for health check retries.
        /// </summary>
        private static readonly TimeSpan HealthCheckPollInterval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Timeout for individual HTTP health check requests.
        /// </summary>
        private static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Timeout for individual TCP connectivity checks.
        /// </summary>
        private static readonly TimeSpan TcpConnectTimeout = TimeSpan.FromSeconds(3);

        // =====================================================================
        // Port Mappings — from docker-compose.localstack.yml
        // =====================================================================

        /// <summary>LocalStack: host port 4566 → container port 4566 (SQS, SNS, S3).</summary>
        private const int LocalStackPort = 4566;

        /// <summary>PostgreSQL Core service: host port 15432 → container port 5432.</summary>
        private const int PostgresCorePort = 15432;

        /// <summary>PostgreSQL CRM service: host port 15433 → container port 5432.</summary>
        private const int PostgresCrmPort = 15433;

        /// <summary>PostgreSQL Project service: host port 15434 → container port 5432.</summary>
        private const int PostgresProjectPort = 15434;

        /// <summary>PostgreSQL Mail service: host port 15435 → container port 5432.</summary>
        private const int PostgresMailPort = 15435;

        /// <summary>Redis: host port 16379 → container port 6379.</summary>
        private const int RedisPort = 16379;

        /// <summary>RabbitMQ AMQP: host port 15672 → container port 5672.</summary>
        private const int RabbitMqAmqpPort = 15672;

        /// <summary>RabbitMQ Management UI: host port 25672 → container port 15672.</summary>
        private const int RabbitMqManagementPort = 25672;

        /// <summary>API Gateway: host port 5000 → container port 8080.</summary>
        private const int GatewayPort = 5000;

        // =====================================================================
        // Instance Fields
        // =====================================================================

        /// <summary>
        /// Absolute path to the <c>docker-compose.localstack.yml</c> file on disk.
        /// </summary>
        private readonly string _composeFilePath;

        /// <summary>
        /// Unique Docker Compose project name for test isolation, preventing
        /// collisions with other test runs or developer stacks.
        /// </summary>
        private readonly string _projectName;

        /// <summary>
        /// Tracks whether the Docker Compose stack is currently running.
        /// Used by <see cref="DisposeAsync"/> to determine if cleanup is needed.
        /// </summary>
        private bool _isRunning;

        // =====================================================================
        // Constructor
        // =====================================================================

        /// <summary>
        /// Initializes a new instance of <see cref="DockerComposeTestHelper"/>.
        /// </summary>
        /// <param name="composeFilePath">
        /// Optional explicit path to the <c>docker-compose.localstack.yml</c> file.
        /// When <c>null</c>, the path is resolved by walking up from the current directory
        /// or by reading the <c>COMPOSE_FILE_PATH</c> environment variable.
        /// </param>
        public DockerComposeTestHelper(string composeFilePath = null)
        {
            _composeFilePath = composeFilePath ?? ResolveComposeFilePath();
            _projectName = $"erp-integration-tests-{Guid.NewGuid():N}";
            _isRunning = false;
        }

        // =====================================================================
        // ComposeFilePath Resolution
        // =====================================================================

        /// <summary>
        /// Resolves the absolute path to <c>docker-compose.localstack.yml</c> by:
        /// <list type="number">
        ///   <item>Checking the <c>COMPOSE_FILE_PATH</c> environment variable (for CI).</item>
        ///   <item>Walking up the directory tree from the current working directory.</item>
        /// </list>
        /// </summary>
        /// <returns>The absolute path to the compose file.</returns>
        /// <exception cref="FileNotFoundException">
        /// Thrown when the compose file cannot be found by any resolution strategy.
        /// </exception>
        private static string ResolveComposeFilePath()
        {
            // Strategy 1: Environment variable override for CI environments.
            // Per AAP 0.8.3: "LocalStack endpoint configuration must be injectable via
            // environment variables."
            string envPath = Environment.GetEnvironmentVariable(ComposeFilePathEnvVar);
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                if (File.Exists(envPath))
                {
                    return Path.GetFullPath(envPath);
                }

                throw new FileNotFoundException(
                    $"The path specified by environment variable '{ComposeFilePathEnvVar}' " +
                    $"does not exist: '{envPath}'. Ensure the variable points to a valid " +
                    $"'{ComposeFileName}' file.");
            }

            // Strategy 2: Walk up the directory tree from the current working directory,
            // looking for the compose file at each level. This covers both running from
            // the test project output directory and from the solution root.
            string currentDir = Directory.GetCurrentDirectory();
            DirectoryInfo directory = new DirectoryInfo(currentDir);

            while (directory != null)
            {
                string candidatePath = Path.Combine(directory.FullName, ComposeFileName);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException(
                $"Could not find '{ComposeFileName}' by walking up the directory tree " +
                $"from '{currentDir}'. Either place the compose file in an ancestor " +
                $"directory or set the '{ComposeFilePathEnvVar}' environment variable " +
                $"to its absolute path.");
        }

        // =====================================================================
        // StartStackAsync
        // =====================================================================

        /// <summary>
        /// Brings up the full Docker Compose stack in detached mode.
        /// Per AAP 0.7.4, the stack includes: localstack, postgres-core, postgres-crm,
        /// postgres-project, postgres-mail, redis, rabbitmq, core-service, crm-service,
        /// project-service, mail-service, reporting-service, admin-service, and gateway.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when docker compose exits with a non-zero exit code.
        /// </exception>
        public async Task StartStackAsync(CancellationToken cancellationToken = default)
        {
            string arguments = $"compose -f \"{_composeFilePath}\" -p {_projectName} up -d";

            var (exitCode, stdOut, stdErr) = await ExecuteProcessAsync(
                "docker", arguments, cancellationToken).ConfigureAwait(false);

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to start Docker Compose stack (exit code {exitCode}).\n" +
                    $"Command: docker {arguments}\n" +
                    $"StdOut: {stdOut}\n" +
                    $"StdErr: {stdErr}");
            }

            _isRunning = true;

            Console.WriteLine(
                $"[DockerComposeTestHelper] Stack started with project name '{_projectName}'.");
        }

        // =====================================================================
        // WaitForServicesHealthyAsync
        // =====================================================================

        /// <summary>
        /// Polls all infrastructure and application services until they are healthy,
        /// or the specified timeout expires.
        /// </summary>
        /// <param name="timeout">
        /// Maximum time to wait. Defaults to 120 seconds (matching CommandTimeout from Config.json).
        /// </param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <exception cref="TimeoutException">
        /// Thrown when one or more services fail to become healthy within the timeout.
        /// </exception>
        public async Task WaitForServicesHealthyAsync(
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            TimeSpan effectiveTimeout = timeout ?? DefaultHealthCheckTimeout;
            DateTime deadline = DateTime.UtcNow + effectiveTimeout;

            // Track health status per service for comprehensive error reporting.
            bool localStackHealthy = false;
            bool postgresCoreHealthy = false;
            bool postgresCrmHealthy = false;
            bool postgresProjectHealthy = false;
            bool postgresMailHealthy = false;
            bool redisHealthy = false;
            bool rabbitMqHealthy = false;
            bool gatewayHealthy = false;

            using var httpClient = new HttpClient();
            httpClient.Timeout = HttpRequestTimeout;

            Console.WriteLine(
                $"[DockerComposeTestHelper] Waiting for services to become healthy " +
                $"(timeout: {effectiveTimeout.TotalSeconds}s)...");

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Check LocalStack via HTTP health endpoint.
                if (!localStackHealthy)
                {
                    localStackHealthy = await CheckHttpHealthAsync(
                        httpClient,
                        $"http://localhost:{LocalStackPort}/_localstack/health").ConfigureAwait(false);
                }

                // Check PostgreSQL instances via TCP connectivity.
                if (!postgresCoreHealthy)
                {
                    postgresCoreHealthy = await CheckTcpHealthAsync(
                        "localhost", PostgresCorePort).ConfigureAwait(false);
                }

                if (!postgresCrmHealthy)
                {
                    postgresCrmHealthy = await CheckTcpHealthAsync(
                        "localhost", PostgresCrmPort).ConfigureAwait(false);
                }

                if (!postgresProjectHealthy)
                {
                    postgresProjectHealthy = await CheckTcpHealthAsync(
                        "localhost", PostgresProjectPort).ConfigureAwait(false);
                }

                if (!postgresMailHealthy)
                {
                    postgresMailHealthy = await CheckTcpHealthAsync(
                        "localhost", PostgresMailPort).ConfigureAwait(false);
                }

                // Check Redis via TCP connectivity.
                if (!redisHealthy)
                {
                    redisHealthy = await CheckTcpHealthAsync(
                        "localhost", RedisPort).ConfigureAwait(false);
                }

                // Check RabbitMQ via HTTP management API.
                if (!rabbitMqHealthy)
                {
                    rabbitMqHealthy = await CheckHttpHealthAsync(
                        httpClient,
                        $"http://localhost:{RabbitMqManagementPort}").ConfigureAwait(false);
                }

                // Check Gateway via HTTP.
                if (!gatewayHealthy)
                {
                    gatewayHealthy = await CheckHttpHealthAsync(
                        httpClient,
                        $"http://localhost:{GatewayPort}").ConfigureAwait(false);
                }

                // If all services are healthy, we are done.
                if (localStackHealthy && postgresCoreHealthy && postgresCrmHealthy &&
                    postgresProjectHealthy && postgresMailHealthy && redisHealthy &&
                    rabbitMqHealthy && gatewayHealthy)
                {
                    Console.WriteLine(
                        "[DockerComposeTestHelper] All services are healthy.");
                    return;
                }

                // Log progress with current status.
                Console.WriteLine(
                    $"[DockerComposeTestHelper] Health check poll — " +
                    $"LocalStack:{BoolToStatus(localStackHealthy)} " +
                    $"PG-Core:{BoolToStatus(postgresCoreHealthy)} " +
                    $"PG-CRM:{BoolToStatus(postgresCrmHealthy)} " +
                    $"PG-Project:{BoolToStatus(postgresProjectHealthy)} " +
                    $"PG-Mail:{BoolToStatus(postgresMailHealthy)} " +
                    $"Redis:{BoolToStatus(redisHealthy)} " +
                    $"RabbitMQ:{BoolToStatus(rabbitMqHealthy)} " +
                    $"Gateway:{BoolToStatus(gatewayHealthy)}");

                await Task.Delay(HealthCheckPollInterval, cancellationToken).ConfigureAwait(false);
            }

            // Timeout exceeded — build a detailed failure message listing which services failed.
            string failedServices = BuildFailedServicesMessage(
                localStackHealthy, postgresCoreHealthy, postgresCrmHealthy,
                postgresProjectHealthy, postgresMailHealthy, redisHealthy,
                rabbitMqHealthy, gatewayHealthy);

            throw new TimeoutException(
                $"Timed out after {effectiveTimeout.TotalSeconds}s waiting for Docker Compose " +
                $"services to become healthy. Failed services: {failedServices}");
        }

        // =====================================================================
        // GetServiceUrl
        // =====================================================================

        /// <summary>
        /// Returns the URL or connection string for a specific service in the Docker Compose
        /// stack, using host-mapped port numbers from <c>docker-compose.localstack.yml</c>.
        /// </summary>
        /// <param name="serviceName">
        /// The service identifier. Supported values:
        /// <c>"gateway"</c>, <c>"localstack"</c>, <c>"redis"</c>, <c>"rabbitmq"</c>,
        /// <c>"rabbitmq-management"</c>, <c>"postgres-core"</c>, <c>"postgres-crm"</c>,
        /// <c>"postgres-project"</c>, <c>"postgres-mail"</c>.
        /// </param>
        /// <returns>The URL or connection string for the requested service.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="serviceName"/> is not a recognized service name.
        /// </exception>
        public string GetServiceUrl(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                throw new ArgumentException(
                    "Service name cannot be null or empty.", nameof(serviceName));
            }

            switch (serviceName.ToLowerInvariant())
            {
                case "gateway":
                    return $"http://localhost:{GatewayPort}";

                case "localstack":
                    return $"http://localhost:{LocalStackPort}";

                case "redis":
                    return $"localhost:{RedisPort}";

                case "rabbitmq":
                    return $"amqp://guest:guest@localhost:{RabbitMqAmqpPort}";

                case "rabbitmq-management":
                    return $"http://localhost:{RabbitMqManagementPort}";

                case "postgres-core":
                    return BuildPostgresConnectionString(PostgresCorePort, "erp_core");

                case "postgres-crm":
                    return BuildPostgresConnectionString(PostgresCrmPort, "erp_crm");

                case "postgres-project":
                    return BuildPostgresConnectionString(PostgresProjectPort, "erp_project");

                case "postgres-mail":
                    return BuildPostgresConnectionString(PostgresMailPort, "erp_mail");

                default:
                    throw new ArgumentException(
                        $"Unknown service name '{serviceName}'. Supported services: " +
                        "gateway, localstack, redis, rabbitmq, rabbitmq-management, " +
                        "postgres-core, postgres-crm, postgres-project, postgres-mail.",
                        nameof(serviceName));
            }
        }

        // =====================================================================
        // StopStackAsync
        // =====================================================================

        /// <summary>
        /// Tears down the Docker Compose stack, removing all containers, volumes, and
        /// orphaned containers for a clean state.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        public async Task StopStackAsync(CancellationToken cancellationToken = default)
        {
            if (!_isRunning)
            {
                Console.WriteLine(
                    "[DockerComposeTestHelper] Stack is not running; skipping stop.");
                return;
            }

            string arguments =
                $"compose -f \"{_composeFilePath}\" -p {_projectName} down -v --remove-orphans";

            Console.WriteLine(
                $"[DockerComposeTestHelper] Stopping stack (project: '{_projectName}')...");

            var (exitCode, stdOut, stdErr) = await ExecuteProcessAsync(
                "docker", arguments, cancellationToken).ConfigureAwait(false);

            _isRunning = false;

            if (exitCode != 0)
            {
                Console.WriteLine(
                    $"[DockerComposeTestHelper] Warning: docker compose down exited " +
                    $"with code {exitCode}.\nStdOut: {stdOut}\nStdErr: {stdErr}");
            }
            else
            {
                Console.WriteLine(
                    "[DockerComposeTestHelper] Stack stopped and cleaned up successfully.");
            }
        }

        // =====================================================================
        // GetServiceLogsAsync
        // =====================================================================

        /// <summary>
        /// Retrieves the container logs for a specific service in the Docker Compose stack.
        /// Useful for debugging test failures.
        /// </summary>
        /// <param name="serviceName">
        /// The Docker Compose service name (e.g., "core-service", "localstack", "gateway").
        /// </param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>The container log output as a string.</returns>
        public async Task<string> GetServiceLogsAsync(
            string serviceName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                throw new ArgumentException(
                    "Service name cannot be null or empty.", nameof(serviceName));
            }

            string arguments =
                $"compose -f \"{_composeFilePath}\" -p {_projectName} logs {serviceName}";

            var (exitCode, stdOut, stdErr) = await ExecuteProcessAsync(
                "docker", arguments, cancellationToken).ConfigureAwait(false);

            if (exitCode != 0)
            {
                Console.WriteLine(
                    $"[DockerComposeTestHelper] Warning: Failed to retrieve logs for " +
                    $"'{serviceName}' (exit code {exitCode}).\nStdErr: {stdErr}");
            }

            return stdOut;
        }

        // =====================================================================
        // IAsyncDisposable Implementation
        // =====================================================================

        /// <summary>
        /// Disposes the helper by stopping the Docker Compose stack if it is running.
        /// Exceptions are swallowed during disposal to prevent masking test failures.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            try
            {
                Console.WriteLine(
                    "[DockerComposeTestHelper] Disposing — stopping Docker Compose stack...");
                await StopStackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Swallow exceptions during disposal to prevent masking test failures.
                // Log the error for diagnostic purposes.
                Console.WriteLine(
                    $"[DockerComposeTestHelper] Warning: Exception during disposal: {ex.Message}");
            }
        }

        // =====================================================================
        // Private Helpers
        // =====================================================================

        /// <summary>
        /// Executes an external process asynchronously, capturing stdout and stderr.
        /// Supports cancellation via process termination.
        /// </summary>
        /// <param name="command">The executable name (e.g., "docker").</param>
        /// <param name="arguments">The command-line arguments.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>
        /// A tuple containing the process exit code, standard output, and standard error.
        /// </returns>
        private static async Task<(int ExitCode, string StdOut, string StdErr)> ExecuteProcessAsync(
            string command,
            string arguments,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            process.Start();

            // Read stdout and stderr asynchronously to avoid deadlocks.
            // Both streams must be read concurrently; reading one to completion
            // before the other can cause the process to block on the unread buffer.
            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            // Register cancellation to kill the process if the token is triggered.
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process may have already exited between check and kill.
                }
            });

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string stdOut = await stdOutTask.ConfigureAwait(false);
            string stdErr = await stdErrTask.ConfigureAwait(false);

            return (process.ExitCode, stdOut, stdErr);
        }

        /// <summary>
        /// Checks if an HTTP endpoint returns a successful status code.
        /// </summary>
        /// <param name="httpClient">The HTTP client to use.</param>
        /// <param name="url">The URL to probe.</param>
        /// <returns><c>true</c> if the endpoint returned a success status code.</returns>
        private static async Task<bool> CheckHttpHealthAsync(HttpClient httpClient, string url)
        {
            try
            {
                HttpResponseMessage response = await httpClient.GetAsync(url).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                // Connection refused, timeout, DNS failure, etc. — service not ready.
                return false;
            }
        }

        /// <summary>
        /// Checks if a TCP port is accepting connections.
        /// </summary>
        /// <param name="host">The hostname to connect to.</param>
        /// <param name="port">The port number.</param>
        /// <returns><c>true</c> if a TCP connection was established successfully.</returns>
        private static async Task<bool> CheckTcpHealthAsync(string host, int port)
        {
            try
            {
                using var tcpClient = new TcpClient();
                using var cts = new CancellationTokenSource(TcpConnectTimeout);
                await tcpClient.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                tcpClient.Close();
                return true;
            }
            catch (Exception)
            {
                // Connection refused, timeout, etc. — service not ready.
                return false;
            }
        }

        /// <summary>
        /// Converts a boolean health status to a human-readable status string.
        /// </summary>
        private static string BoolToStatus(bool healthy)
        {
            return healthy ? "OK" : "WAITING";
        }

        /// <summary>
        /// Builds a PostgreSQL connection string matching the format from Config.json.
        /// </summary>
        /// <param name="port">The host-mapped PostgreSQL port.</param>
        /// <param name="database">The database name.</param>
        /// <returns>A complete PostgreSQL connection string.</returns>
        private static string BuildPostgresConnectionString(int port, string database)
        {
            return $"Server=localhost;Port={port};User Id=dev;Password=dev;Database={database};" +
                   "Pooling=true;MinPoolSize=1;MaxPoolSize=100;CommandTimeout=120;";
        }

        /// <summary>
        /// Builds a human-readable message listing which services failed their health checks.
        /// </summary>
        private static string BuildFailedServicesMessage(
            bool localStack, bool pgCore, bool pgCrm, bool pgProject, bool pgMail,
            bool redis, bool rabbitMq, bool gateway)
        {
            var failed = new System.Collections.Generic.List<string>();

            if (!localStack) failed.Add($"LocalStack (http://localhost:{LocalStackPort})");
            if (!pgCore) failed.Add($"PostgreSQL-Core (tcp://localhost:{PostgresCorePort})");
            if (!pgCrm) failed.Add($"PostgreSQL-CRM (tcp://localhost:{PostgresCrmPort})");
            if (!pgProject) failed.Add($"PostgreSQL-Project (tcp://localhost:{PostgresProjectPort})");
            if (!pgMail) failed.Add($"PostgreSQL-Mail (tcp://localhost:{PostgresMailPort})");
            if (!redis) failed.Add($"Redis (tcp://localhost:{RedisPort})");
            if (!rabbitMq) failed.Add($"RabbitMQ (http://localhost:{RabbitMqManagementPort})");
            if (!gateway) failed.Add($"Gateway (http://localhost:{GatewayPort})");

            return string.Join(", ", failed);
        }
    }
}
