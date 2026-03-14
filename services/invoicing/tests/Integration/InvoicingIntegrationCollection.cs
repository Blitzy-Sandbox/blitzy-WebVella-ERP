using Xunit;

namespace WebVellaErp.Invoicing.Tests.Integration
{
    /// <summary>
    /// Defines a shared xUnit test collection for all invoicing integration tests.
    /// All test classes in this collection:
    /// 1. Share a single <see cref="LocalStackFixture"/> instance (initialized once)
    /// 2. Execute sequentially (not in parallel) to prevent database deadlocks
    ///    and schema race conditions when operating against the shared PostgreSQL database
    ///
    /// This replaces per-class IClassFixture usage which created separate fixture instances
    /// that raced on CREATE SCHEMA and deadlocked on concurrent TRUNCATE operations.
    /// </summary>
    [CollectionDefinition("InvoicingIntegration")]
    public class InvoicingIntegrationCollection : ICollectionFixture<LocalStackFixture>
    {
    }
}
