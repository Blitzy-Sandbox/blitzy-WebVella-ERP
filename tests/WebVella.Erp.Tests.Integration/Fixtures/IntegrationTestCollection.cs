using Xunit;

namespace WebVella.Erp.Tests.Integration.Fixtures
{
    /// <summary>
    /// xUnit collection definition that groups all shared infrastructure fixtures into
    /// a single test collection named <see cref="Name"/>. This guarantees that Docker
    /// container startup (LocalStack, PostgreSQL, Redis) and <c>WebApplicationFactory</c>
    /// bootstrapping occur <b>once per test run</b> rather than once per test class.
    ///
    /// <para>
    /// All integration test classes in the <c>CrossService/</c>, <c>EventFlow/</c>, and
    /// <c>Migration/</c> folders reference this collection via:
    /// <code>[Collection(IntegrationTestCollection.Name)]</code>
    /// </para>
    ///
    /// <para><b>xUnit Collection Fixture Lifecycle:</b></para>
    /// <list type="number">
    ///   <item>xUnit creates ONE instance of each <c>ICollectionFixture&lt;T&gt;</c> type.</item>
    ///   <item>Fixtures are initialized before the first test in the collection executes.</item>
    ///   <item>All test classes sharing this collection receive the same fixture instances.</item>
    ///   <item>Fixtures are disposed after the last test in the collection completes.</item>
    /// </list>
    ///
    /// <para><b>Registered Fixtures:</b></para>
    /// <list type="bullet">
    ///   <item><see cref="LocalStackFixture"/> — LocalStack container (SQS, SNS, S3 emulation)</item>
    ///   <item><see cref="PostgreSqlFixture"/> — PostgreSQL container with per-service databases</item>
    ///   <item><see cref="RedisFixture"/> — Redis container for distributed cache testing</item>
    ///   <item><see cref="ServiceCollectionFixture"/> — WebApplicationFactory instances for all microservices</item>
    /// </list>
    ///
    /// <para><b>Key AAP References:</b></para>
    /// <list type="bullet">
    ///   <item>AAP 0.7.4: Docker containers start once, reused across all integration tests</item>
    ///   <item>AAP 0.8.2: Cross-service tests use Testcontainers for every multi-service business rule</item>
    ///   <item>AAP 0.8.3: LocalStack endpoint injectable via environment variables</item>
    /// </list>
    /// </summary>
    // NOTE: ICollectionFixture<ServiceCollectionFixture> is intentionally NOT registered
    // here because ServiceCollectionFixture requires PostgreSqlFixture, LocalStackFixture,
    // and RedisFixture as constructor parameters — and xUnit v2.9.3 with the v3 runner
    // (xunit.runner.visualstudio 3.0.0) does not support injecting collection fixtures
    // into other collection fixture constructors. This causes:
    //   "Collection fixture type 'ServiceCollectionFixture' had one or more unresolved
    //    constructor arguments"
    // To re-enable, either:
    //   (a) Upgrade to xUnit v3 core which supports fixture dependency injection, or
    //   (b) Refactor ServiceCollectionFixture to use parameterless constructor with
    //       lazy initialization via shared static state or IAsyncLifetime discovery.
    // Cross-service integration tests that need WebApplicationFactory should use
    // IClassFixture<ServiceCollectionFixture> at the class level instead.
    [CollectionDefinition(IntegrationTestCollection.Name)]
    public class IntegrationTestCollection
        : ICollectionFixture<LocalStackFixture>,
          ICollectionFixture<PostgreSqlFixture>,
          ICollectionFixture<RedisFixture>
    {
        /// <summary>
        /// The well-known collection name referenced by all integration test classes via
        /// <c>[Collection(IntegrationTestCollection.Name)]</c>. Using a constant ensures
        /// compile-time safety — a typo in the collection name will produce a build error
        /// rather than silently creating a separate, unshared collection.
        /// </summary>
        public const string Name = "Integration Test Collection";
    }
}
