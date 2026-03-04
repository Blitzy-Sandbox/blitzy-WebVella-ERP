using Xunit;

namespace WebVella.Erp.Tests.Admin.Fixtures
{
    /// <summary>
    /// xUnit test collection definition for Admin/SDK service integration tests.
    /// 
    /// <para>This marker class defines the <c>"AdminService"</c> test collection,
    /// which shares a single <see cref="PostgreSqlContainerFixture"/> instance
    /// across all Admin service test classes that are decorated with
    /// <c>[Collection("AdminService")]</c>.</para>
    /// 
    /// <para><b>How it works:</b></para>
    /// <list type="bullet">
    ///   <item>xUnit creates one <see cref="PostgreSqlContainerFixture"/> instance
    ///         before any test class in this collection runs</item>
    ///   <item>The fixture starts a PostgreSQL Testcontainer, creates prerequisite
    ///         tables, and initializes the <c>AdminWebApplicationFactory</c></item>
    ///   <item>All test classes in the collection share this single container,
    ///         avoiding the overhead of creating a new container per class</item>
    ///   <item>After all tests complete, xUnit disposes the fixture, stopping
    ///         the container and cleaning up resources</item>
    /// </list>
    /// 
    /// <para><b>Usage:</b> Decorate test classes with <c>[Collection("AdminService")]</c>
    /// and accept <see cref="PostgreSqlContainerFixture"/> as a constructor parameter.</para>
    /// </summary>
    [CollectionDefinition("AdminService")]
    public class AdminTestCollection : ICollectionFixture<PostgreSqlContainerFixture>
    {
        // This class has no code, and is never created. Its purpose is simply
        // to be the place to apply [CollectionDefinition] and all the
        // ICollectionFixture<> interfaces.
    }
}
