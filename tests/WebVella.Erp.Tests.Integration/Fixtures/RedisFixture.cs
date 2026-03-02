using System;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using Xunit;

namespace WebVella.Erp.Tests.Integration.Fixtures
{
    /// <summary>
    /// xUnit IAsyncLifetime fixture that manages a Redis Docker container for distributed cache
    /// testing across services. The monolith's IMemoryCache with 1-hour TTL (WebVella.Erp.Api.Cache)
    /// is being replaced with Redis for distributed caching per AAP 0.1.1. This fixture provides
    /// an isolated Redis instance for testing cache invalidation events across service boundaries.
    ///
    /// Usage: Register via ICollectionFixture&lt;RedisFixture&gt; in IntegrationTestCollection.
    /// The container starts once per test collection and is shared across all test classes.
    ///
    /// Key AAP References:
    /// - AAP 0.1.1: IMemoryCache replaced with Redis distributed cache
    /// - AAP 0.7.4: Docker Compose spec — redis: image: redis:7-alpine
    /// - AAP 0.8.3: Entity metadata cache TTL (1 hour) preserved per service
    /// </summary>
    public class RedisFixture : IAsyncLifetime
    {
        /// <summary>
        /// The Docker image used for the Redis container, matching the AAP 0.7.4
        /// Docker Compose specification: <c>redis: image: redis:7-alpine</c>.
        /// </summary>
        public const string ContainerImage = "redis:7-alpine";

        /// <summary>
        /// Default Redis container port (standard Redis port).
        /// </summary>
        private const int RedisPort = 6379;

        /// <summary>
        /// The built Docker container instance managing the Redis server lifecycle.
        /// Null before InitializeAsync is called.
        /// </summary>
        private IContainer _container;

        /// <summary>
        /// Redis connection string in StackExchange.Redis format (host:port).
        /// Set during InitializeAsync after the container starts and port mapping is established.
        /// Use this to configure IDistributedCache or direct ConnectionMultiplexer connections
        /// in service integration tests.
        /// </summary>
        public string ConnectionString { get; private set; }

        /// <summary>
        /// The host:port endpoint for the Redis container as seen from the host machine.
        /// Equivalent to ConnectionString for StackExchange.Redis but provided separately
        /// for clarity when configuring service endpoints.
        /// </summary>
        public string Endpoint { get; private set; }

        /// <summary>
        /// The dynamically assigned host port mapped to the Redis container's internal port 6379.
        /// Testcontainers assigns a random available port to avoid conflicts with other containers
        /// or services running on the host.
        /// </summary>
        public int Port { get; private set; }

        /// <summary>
        /// Starts the Redis Docker container and establishes port mapping.
        /// Called automatically by xUnit before the first test in the collection runs.
        ///
        /// Container configuration:
        /// - Image: redis:7-alpine (per AAP 0.7.4)
        /// - Port: 6379 with random host port mapping (Testcontainers auto-assigns)
        /// - Wait strategy: Verifies Redis is ready by executing 'redis-cli ping' inside the container
        /// - Container name: Unique per test run to avoid conflicts
        /// </summary>
        public async Task InitializeAsync()
        {
            var containerName = $"redis-integration-test-{Guid.NewGuid():N}";

            _container = new ContainerBuilder(ContainerImage)
                .WithPortBinding(RedisPort, true)
                .WithName(containerName)
                .WithWaitStrategy(
                    Wait.ForUnixContainer()
                        .UntilCommandIsCompleted("redis-cli", "ping"))
                .Build();

            await _container.StartAsync().ConfigureAwait(false);

            // Extract the dynamically assigned host port mapped to Redis container port 6379.
            // Testcontainers allocates random available ports to prevent conflicts.
            var mappedPort = _container.GetMappedPublicPort(RedisPort);
            Port = mappedPort;
            ConnectionString = $"localhost:{mappedPort}";
            Endpoint = $"localhost:{mappedPort}";

            // Log container startup details for debugging test infrastructure issues.
            Console.WriteLine(
                $"[RedisFixture] Container '{containerName}' started. " +
                $"Redis available at {Endpoint} (container port {RedisPort} -> host port {mappedPort}).");
        }

        /// <summary>
        /// Stops and removes the Redis Docker container.
        /// Called automatically by xUnit after the last test in the collection completes.
        /// Exceptions during disposal are swallowed to prevent masking actual test failures.
        /// </summary>
        public async Task DisposeAsync()
        {
            if (_container != null)
            {
                try
                {
                    Console.WriteLine("[RedisFixture] Stopping Redis container...");
                    await _container.StopAsync().ConfigureAwait(false);
                    Console.WriteLine("[RedisFixture] Redis container stopped successfully.");
                }
                catch (Exception ex)
                {
                    // Swallow exceptions during disposal to prevent masking test failures.
                    // Container cleanup failures should not cause test suite to report false negatives.
                    Console.WriteLine(
                        $"[RedisFixture] Warning: Error stopping container: {ex.Message}");
                }

                try
                {
                    await _container.DisposeAsync().ConfigureAwait(false);
                    Console.WriteLine("[RedisFixture] Redis container disposed.");
                }
                catch (Exception ex)
                {
                    // Swallow disposal exceptions as well.
                    Console.WriteLine(
                        $"[RedisFixture] Warning: Error disposing container: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("[RedisFixture] No container to dispose (was never started).");
            }
        }

        /// <summary>
        /// Creates a new StackExchange.Redis connection to the test Redis instance.
        /// Use this for direct cache verification in integration tests — for example,
        /// to verify that a service operation correctly wrote cache entries or to inspect
        /// cache state after cross-service event processing.
        ///
        /// Callers are responsible for disposing the returned IConnectionMultiplexer when done.
        ///
        /// Example usage:
        /// <code>
        /// using var redis = redisFixture.CreateConnection();
        /// var db = redis.GetDatabase();
        /// var value = await db.StringGetAsync("my-cache-key");
        /// Assert.Equal("expected-value", value.ToString());
        /// </code>
        /// </summary>
        /// <returns>An IConnectionMultiplexer connected to the test Redis instance.</returns>
        /// <exception cref="RedisConnectionException">
        /// Thrown if the connection cannot be established (e.g., container not started).
        /// </exception>
        public IConnectionMultiplexer CreateConnection()
        {
            if (string.IsNullOrEmpty(ConnectionString))
            {
                throw new InvalidOperationException(
                    "Redis connection string is not available. " +
                    "Ensure InitializeAsync has been called before creating connections.");
            }

            return ConnectionMultiplexer.Connect(ConnectionString);
        }

        /// <summary>
        /// Clears all data in the test Redis instance by executing the FLUSHALL command.
        /// Call this between test scenarios to ensure complete test isolation — each test
        /// starts with an empty Redis cache.
        ///
        /// This is critical for testing cache invalidation event flows across services:
        /// one test's cached entity metadata should never leak into another test's assertions.
        ///
        /// Example usage:
        /// <code>
        /// // In test setup or between scenarios
        /// await redisFixture.FlushAllAsync();
        /// </code>
        /// </summary>
        public async Task FlushAllAsync()
        {
            if (string.IsNullOrEmpty(ConnectionString))
            {
                throw new InvalidOperationException(
                    "Redis connection string is not available. " +
                    "Ensure InitializeAsync has been called before flushing data.");
            }

            using var connection = ConnectionMultiplexer.Connect(ConnectionString);

            // Get all servers (there is only one in our test container).
            var endpoints = connection.GetEndPoints();
            foreach (var endpoint in endpoints)
            {
                var server = connection.GetServer(endpoint);
                await server.FlushAllDatabasesAsync().ConfigureAwait(false);
            }

            Console.WriteLine("[RedisFixture] All Redis databases flushed.");
        }
    }
}
