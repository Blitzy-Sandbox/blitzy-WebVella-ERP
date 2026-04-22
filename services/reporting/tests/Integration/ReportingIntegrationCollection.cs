// ─────────────────────────────────────────────────────────────────────────────
// ReportingIntegrationCollection.cs
//
// xUnit collection definition that shares a single LocalStackFixture and
// DatabaseFixture instance across ALL reporting integration test classes.
//
// Without this, xUnit creates separate IClassFixture<LocalStackFixture>
// instances per test class running in parallel, causing race conditions:
//   - Concurrent CREATE DATABASE → 23505 unique constraint violations
//   - Concurrent SQS queue create/delete → QueueDoesNotExistException
//   - Concurrent TRUNCATE → 40P01 deadlock detected
//
// By using [Collection("ReportingIntegration")], all test classes share
// one fixture instance and run serially within the collection.
// ─────────────────────────────────────────────────────────────────────────────

using Xunit;

namespace WebVellaErp.Reporting.Tests.Integration
{
    /// <summary>
    /// Collection definition for all reporting integration tests.
    /// Ensures a single <see cref="LocalStackFixture"/> and <see cref="DatabaseFixture"/>
    /// instance is shared across all test classes in the collection, preventing
    /// parallel resource conflicts on SQS queues, SNS topics, and RDS PostgreSQL.
    /// </summary>
    [CollectionDefinition("ReportingIntegration")]
    public class ReportingIntegrationCollection
        : ICollectionFixture<LocalStackFixture>,
          ICollectionFixture<DatabaseFixture>
    {
        // This class has no code — it exists only to define the xUnit collection.
        // The collection name "ReportingIntegration" must match the [Collection("...")]
        // attribute on each test class that participates in the shared fixture.
    }
}
