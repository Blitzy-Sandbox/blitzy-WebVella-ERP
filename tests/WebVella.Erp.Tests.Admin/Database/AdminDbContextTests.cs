using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WebVella.Erp.Service.Admin.Database;
using Xunit;

namespace WebVella.Erp.Tests.Admin.Database
{
    /// <summary>
    /// Unit tests validating the AdminDbContext EF Core configuration, entity model
    /// mappings, and architectural constraints. Uses the EF Core in-memory provider
    /// for fast execution without real PostgreSQL. Validates table names, column
    /// mappings (snake_case), primary keys, nullable properties, and cross-service
    /// database isolation per AAP 0.8.2.
    ///
    /// Each test is traceable to monolith source files:
    /// - SystemLogEntry → WebVella.Erp/Diagnostics/Log.cs (system_log table)
    /// - JobEntry → WebVella.Erp/Jobs/JobDataService.cs + Jobs/Models/Job.cs (jobs table)
    /// - SchedulePlanEntry → WebVella.Erp/Jobs/Models/SchedulePlan.cs (schedule_plans table)
    /// - PluginDataEntry → WebVella.Erp.Plugins.SDK/SdkPlugin._.cs (plugin_data table)
    /// </summary>
    public class AdminDbContextTests
    {
        #region Helper Methods

        /// <summary>
        /// Creates an in-memory AdminDbContext for configuration and model inspection.
        /// Each call uses a unique database name via Guid.NewGuid() for test isolation,
        /// ensuring parallel test execution does not cause cross-contamination.
        /// </summary>
        private AdminDbContext CreateInMemoryContext()
        {
            var options = new DbContextOptionsBuilder<AdminDbContext>()
                .UseInMemoryDatabase(databaseName: $"AdminTestDb_{Guid.NewGuid()}")
                .Options;
            return new AdminDbContext(options);
        }

        /// <summary>
        /// Asserts that a property on an entity type maps to the expected snake_case
        /// column name. Validates the column annotation set by HasColumnName() or
        /// [Column] attribute in OnModelCreating.
        /// </summary>
        /// <param name="entityType">The EF Core entity type metadata.</param>
        /// <param name="propertyName">The CLR property name (PascalCase).</param>
        /// <param name="expectedColumnName">The expected PostgreSQL column name (snake_case).</param>
        private static void AssertColumnMapping(
            IEntityType entityType,
            string propertyName,
            string expectedColumnName)
        {
            var property = entityType.FindProperty(propertyName);
            property.Should().NotBeNull(
                $"property '{propertyName}' should exist on entity '{entityType.Name}'");
            property.GetColumnName().Should().Be(expectedColumnName,
                $"property '{propertyName}' should map to column '{expectedColumnName}'");
        }

        /// <summary>
        /// Asserts the nullability configuration of a property on an entity type.
        /// Validates the IsRequired()/IsRequired(false) setting from OnModelCreating
        /// matches the monolith's null checks in SQL and data service code.
        /// </summary>
        /// <param name="entityType">The EF Core entity type metadata.</param>
        /// <param name="propertyName">The CLR property name.</param>
        /// <param name="expectedNullable">True if the property should be nullable; false if required.</param>
        private static void AssertPropertyNullability(
            IEntityType entityType,
            string propertyName,
            bool expectedNullable)
        {
            var property = entityType.FindProperty(propertyName);
            property.Should().NotBeNull(
                $"property '{propertyName}' should exist on entity '{entityType.Name}'");
            if (expectedNullable)
            {
                property.IsNullable.Should().BeTrue(
                    $"property '{propertyName}' should be nullable");
            }
            else
            {
                property.IsNullable.Should().BeFalse(
                    $"property '{propertyName}' should be required (not nullable)");
            }
        }

        #endregion

        #region Constructor and DI Configuration Tests (Tests 1–3)

        /// <summary>
        /// Test 1: Validates AdminDbContext follows standard EF Core DI pattern,
        /// accepting DbContextOptions&lt;AdminDbContext&gt; in its constructor.
        /// This replaces the monolith's static DbContext.Current ambient pattern
        /// from WebVella.Erp/Database/DbContext.cs.
        /// </summary>
        [Fact]
        public void Constructor_ShouldAcceptDbContextOptions()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<AdminDbContext>()
                .UseInMemoryDatabase(databaseName: $"AdminTestDb_{Guid.NewGuid()}")
                .Options;

            // Act
            using var context = new AdminDbContext(options);

            // Assert
            context.Should().NotBeNull();
            context.Database.Should().NotBeNull();
        }

        /// <summary>
        /// Test 2: Validates AdminDbContext constructor does not throw with valid
        /// in-memory database options. Ensures model building completes without error.
        /// </summary>
        [Fact]
        public void Constructor_ShouldNotThrowWithValidOptions()
        {
            // Arrange & Act
            AdminDbContext context = null;
            var act = () =>
            {
                context = CreateInMemoryContext();
            };

            // Assert
            act.Should().NotThrow();
            context.Should().NotBeNull();
            context?.Dispose();
        }

        /// <summary>
        /// Test 3: Validates AdminDbContext integrates properly with ASP.NET Core
        /// dependency injection. Replaces the monolith's singleton DbContext.Current
        /// pattern with scoped DI lifetime.
        /// Source: WebVella.Erp/Database/DbContext.cs (static CreateContext pattern)
        /// </summary>
        [Fact]
        public void Context_ShouldBeRegistrableInDI()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddDbContext<AdminDbContext>(options =>
                options.UseInMemoryDatabase("test_di"));
            using var serviceProvider = services.BuildServiceProvider();

            // Act
            using var context = serviceProvider.GetRequiredService<AdminDbContext>();

            // Assert
            context.Should().NotBeNull();
        }

        #endregion

        #region DbSet Property Verification Tests (Tests 4–7)

        /// <summary>
        /// Test 4: Validates system_log table is accessible via SystemLogs DbSet.
        /// Source: WebVella.Erp/Diagnostics/Log.cs (queries system_log table)
        /// </summary>
        [Fact]
        public void Context_ShouldExposeSystemLogsDbSet()
        {
            // Arrange
            using var context = CreateInMemoryContext();

            // Assert
            context.SystemLogs.Should().NotBeNull();
            context.SystemLogs.Should().BeAssignableTo<DbSet<SystemLogEntry>>();
        }

        /// <summary>
        /// Test 5: Validates jobs table is accessible via Jobs DbSet.
        /// Source: WebVella.Erp/Jobs/JobDataService.cs (CRUDs the jobs table)
        /// </summary>
        [Fact]
        public void Context_ShouldExposeJobsDbSet()
        {
            // Arrange
            using var context = CreateInMemoryContext();

            // Assert
            context.Jobs.Should().NotBeNull();
            context.Jobs.Should().BeAssignableTo<DbSet<JobEntry>>();
        }

        /// <summary>
        /// Test 6: Validates schedule_plans table is accessible via SchedulePlans DbSet.
        /// Source: WebVella.Erp/Jobs/Models/SchedulePlan.cs
        /// </summary>
        [Fact]
        public void Context_ShouldExposeSchedulePlansDbSet()
        {
            // Arrange
            using var context = CreateInMemoryContext();

            // Assert
            context.SchedulePlans.Should().NotBeNull();
            context.SchedulePlans.Should().BeAssignableTo<DbSet<SchedulePlanEntry>>();
        }

        /// <summary>
        /// Test 7: Validates plugin_data table is accessible via PluginData DbSet.
        /// Source: WebVella.Erp.Plugins.SDK/SdkPlugin._.cs (GetPluginData/SavePluginData)
        /// </summary>
        [Fact]
        public void Context_ShouldExposePluginDataDbSet()
        {
            // Arrange
            using var context = CreateInMemoryContext();

            // Assert
            context.PluginData.Should().NotBeNull();
            context.PluginData.Should().BeAssignableTo<DbSet<PluginDataEntry>>();
        }

        #endregion

        #region Table Name Mapping Tests (Tests 8–11)

        /// <summary>
        /// Test 8: Validates SystemLogEntry maps to the "system_log" PostgreSQL table.
        /// Table name matches the exact monolith table name from Log.cs SQL queries.
        /// </summary>
        [Fact]
        public void SystemLogEntity_ShouldMapToSystemLogTable()
        {
            // Arrange
            using var context = CreateInMemoryContext();

            // Act
            var entityType = context.Model.FindEntityType(typeof(SystemLogEntry));

            // Assert
            entityType.Should().NotBeNull();
            entityType.GetTableName().Should().Be("system_log");
        }

        /// <summary>
        /// Test 9: Validates JobEntry maps to the "jobs" PostgreSQL table.
        /// Table name matches JobDataService.cs SQL: INSERT INTO jobs ...
        /// </summary>
        [Fact]
        public void JobEntity_ShouldMapToJobsTable()
        {
            // Arrange
            using var context = CreateInMemoryContext();

            // Act
            var entityType = context.Model.FindEntityType(typeof(JobEntry));

            // Assert
            entityType.Should().NotBeNull();
            entityType.GetTableName().Should().Be("jobs");
        }

        /// <summary>
        /// Test 10: Validates SchedulePlanEntry maps to the "schedule_plans" PostgreSQL table.
        /// </summary>
        [Fact]
        public void SchedulePlanEntity_ShouldMapToSchedulePlansTable()
        {
            // Arrange
            using var context = CreateInMemoryContext();

            // Act
            var entityType = context.Model.FindEntityType(typeof(SchedulePlanEntry));

            // Assert
            entityType.Should().NotBeNull();
            entityType.GetTableName().Should().Be("schedule_plans");
        }

        /// <summary>
        /// Test 11: Validates PluginDataEntry maps to the "plugin_data" PostgreSQL table.
        /// Source: WebVella.Erp.Plugins.SDK/SdkPlugin._.cs (plugin_data persistence)
        /// </summary>
        [Fact]
        public void PluginDataEntity_ShouldMapToPluginDataTable()
        {
            // Arrange
            using var context = CreateInMemoryContext();

            // Act
            var entityType = context.Model.FindEntityType(typeof(PluginDataEntry));

            // Assert
            entityType.Should().NotBeNull();
            entityType.GetTableName().Should().Be("plugin_data");
        }

        #endregion

        #region Column Mapping Tests (Tests 12–15)

        /// <summary>
        /// Test 12: Validates SystemLogEntry column names match the monolith's snake_case schema.
        /// Source: WebVella.Erp/Diagnostics/Log.cs (dr["id"], dr["created_on"], dr["type"], etc.)
        /// </summary>
        [Fact]
        public void SystemLogEntity_ShouldMapColumnsToSnakeCase()
        {
            // Arrange
            using var context = CreateInMemoryContext();
            var entityType = context.Model.FindEntityType(typeof(SystemLogEntry));
            entityType.Should().NotBeNull();

            // Assert — verify all 7 column name mappings
            AssertColumnMapping(entityType, "Id", "id");
            AssertColumnMapping(entityType, "CreatedOn", "created_on");
            AssertColumnMapping(entityType, "Type", "type");
            AssertColumnMapping(entityType, "Source", "source");
            AssertColumnMapping(entityType, "Message", "message");
            AssertColumnMapping(entityType, "Details", "details");
            AssertColumnMapping(entityType, "NotificationStatus", "notification_status");
        }

        /// <summary>
        /// Test 13: Validates JobEntry column names match the monolith's snake_case schema.
        /// Source: WebVella.Erp/Jobs/JobDataService.cs (NpgsqlParameter names, lines 30–55)
        /// Validates AAP 0.8.3 audit fields: created_on, created_by, last_modified_on, last_modified_by.
        /// </summary>
        [Fact]
        public void JobEntity_ShouldMapColumnsToSnakeCase()
        {
            // Arrange
            using var context = CreateInMemoryContext();
            var entityType = context.Model.FindEntityType(typeof(JobEntry));
            entityType.Should().NotBeNull();

            // Assert — verify all 18 column name mappings
            AssertColumnMapping(entityType, "Id", "id");
            AssertColumnMapping(entityType, "TypeId", "type_id");
            AssertColumnMapping(entityType, "TypeName", "type_name");
            AssertColumnMapping(entityType, "CompleteClassName", "complete_class_name");
            AssertColumnMapping(entityType, "Attributes", "attributes");
            AssertColumnMapping(entityType, "Status", "status");
            AssertColumnMapping(entityType, "Priority", "priority");
            AssertColumnMapping(entityType, "StartedOn", "started_on");
            AssertColumnMapping(entityType, "FinishedOn", "finished_on");
            AssertColumnMapping(entityType, "AbortedBy", "aborted_by");
            AssertColumnMapping(entityType, "CanceledBy", "canceled_by");
            AssertColumnMapping(entityType, "ErrorMessage", "error_message");
            AssertColumnMapping(entityType, "SchedulePlanId", "schedule_plan_id");
            AssertColumnMapping(entityType, "CreatedOn", "created_on");
            AssertColumnMapping(entityType, "CreatedBy", "created_by");
            AssertColumnMapping(entityType, "LastModifiedOn", "last_modified_on");
            AssertColumnMapping(entityType, "LastModifiedBy", "last_modified_by");
            AssertColumnMapping(entityType, "Result", "result");
        }

        /// <summary>
        /// Test 14: Validates SchedulePlanEntry column names match the monolith's snake_case schema.
        /// Source: WebVella.Erp/Jobs/Models/SchedulePlan.cs (JsonProperty names)
        /// </summary>
        [Fact]
        public void SchedulePlanEntity_ShouldMapColumnsToSnakeCase()
        {
            // Arrange
            using var context = CreateInMemoryContext();
            var entityType = context.Model.FindEntityType(typeof(SchedulePlanEntry));
            entityType.Should().NotBeNull();

            // Assert — verify all 18 column name mappings
            AssertColumnMapping(entityType, "Id", "id");
            AssertColumnMapping(entityType, "Name", "name");
            AssertColumnMapping(entityType, "Type", "type");
            AssertColumnMapping(entityType, "StartDate", "start_date");
            AssertColumnMapping(entityType, "EndDate", "end_date");
            AssertColumnMapping(entityType, "ScheduledDays", "schedule_days");
            AssertColumnMapping(entityType, "IntervalInMinutes", "interval_in_minutes");
            AssertColumnMapping(entityType, "StartTimespan", "start_timespan");
            AssertColumnMapping(entityType, "EndTimespan", "end_timespan");
            AssertColumnMapping(entityType, "LastTriggerTime", "last_trigger_time");
            AssertColumnMapping(entityType, "NextTriggerTime", "next_trigger_time");
            AssertColumnMapping(entityType, "JobTypeId", "job_type_id");
            AssertColumnMapping(entityType, "JobAttributes", "job_attributes");
            AssertColumnMapping(entityType, "Enabled", "enabled");
            AssertColumnMapping(entityType, "LastStartedJobId", "last_started_job_id");
            AssertColumnMapping(entityType, "CreatedOn", "created_on");
            AssertColumnMapping(entityType, "LastModifiedBy", "last_modified_by");
            AssertColumnMapping(entityType, "LastModifiedOn", "last_modified_on");
        }

        /// <summary>
        /// Test 15: Validates PluginDataEntry column names match the monolith's snake_case schema.
        /// Source: WebVella.Erp.Plugins.SDK/SdkPlugin._.cs (plugin_data persistence pattern)
        /// </summary>
        [Fact]
        public void PluginDataEntity_ShouldMapColumnsToSnakeCase()
        {
            // Arrange
            using var context = CreateInMemoryContext();
            var entityType = context.Model.FindEntityType(typeof(PluginDataEntry));
            entityType.Should().NotBeNull();

            // Assert — verify all 3 column name mappings
            AssertColumnMapping(entityType, "Id", "id");
            AssertColumnMapping(entityType, "Name", "name");
            AssertColumnMapping(entityType, "Data", "data");
        }

        #endregion

        #region Primary Key Configuration Tests (Test 16)

        /// <summary>
        /// Test 16: Validates all admin entities use UUID (Guid) primary keys,
        /// matching the monolith's Guid-based ID pattern across system_log, jobs,
        /// schedule_plans, and plugin_data tables.
        /// </summary>
        [Fact]
        public void AllEntities_ShouldHaveGuidPrimaryKey()
        {
            // Arrange
            using var context = CreateInMemoryContext();
            var entityClrTypes = new[]
            {
                typeof(SystemLogEntry),
                typeof(JobEntry),
                typeof(SchedulePlanEntry),
                typeof(PluginDataEntry)
            };

            foreach (var clrType in entityClrTypes)
            {
                // Act
                var entityType = context.Model.FindEntityType(clrType);
                entityType.Should().NotBeNull(
                    $"entity type '{clrType.Name}' should be registered in the model");

                var primaryKey = entityType.FindPrimaryKey();

                // Assert — each entity must have a single Guid PK named "Id"
                primaryKey.Should().NotBeNull(
                    $"entity '{clrType.Name}' should have a primary key defined");
                primaryKey.Properties.Should().HaveCount(1,
                    $"entity '{clrType.Name}' should have a single-property primary key");
                primaryKey.Properties[0].Name.Should().Be("Id",
                    $"entity '{clrType.Name}' primary key property should be named 'Id'");
                primaryKey.Properties[0].ClrType.Should().Be(typeof(Guid),
                    $"entity '{clrType.Name}' primary key should be of type Guid (UUID)");
            }
        }

        #endregion

        #region Cross-Service Database Isolation Tests (Tests 17–18)

        /// <summary>
        /// Test 17: Validates AdminDbContext only contains admin-specific entities.
        /// Per AAP 0.8.2: "Each service owns its database schema exclusively; no other
        /// service may read from or write to another service's database."
        /// Ensures no entities from Core (rec_user, rec_role), CRM (rec_account, rec_contact),
        /// Project (rec_task, rec_timelog), or Mail (email, smtp_service) services appear.
        /// </summary>
        [Fact]
        public void AdminDbContext_ShouldOnlyContainAdminEntities()
        {
            // Arrange
            using var context = CreateInMemoryContext();
            var expectedEntityTypes = new HashSet<Type>
            {
                typeof(SystemLogEntry),
                typeof(JobEntry),
                typeof(SchedulePlanEntry),
                typeof(PluginDataEntry)
            };

            // Act
            var actualEntityTypes = context.Model.GetEntityTypes()
                .Select(e => e.ClrType)
                .ToList();

            // Assert — must contain ONLY admin entities, no cross-service leakage
            actualEntityTypes.Should().HaveCount(4,
                "AdminDbContext should contain exactly 4 admin-specific entity types");
            actualEntityTypes.Should().BeEquivalentTo(expectedEntityTypes,
                "AdminDbContext should only contain SystemLogEntry, JobEntry, " +
                "SchedulePlanEntry, and PluginDataEntry — no Core, CRM, Project, or Mail entities");
        }

        /// <summary>
        /// Test 18: Validates AdminDbContext does not expose dynamic entity record tables
        /// (rec_*) or relation join tables (rel_*) that belong to Core/CRM/Project services.
        /// Per AAP 0.8.2: Admin service does not own dynamic entity record tables.
        /// Per AAP 0.4.1: Database-per-service model — Admin owns only system_log, jobs,
        /// schedule_plans, plugin_data.
        /// </summary>
        [Fact]
        public void AdminDbContext_ShouldNotExposeRecordTables()
        {
            // Arrange
            using var context = CreateInMemoryContext();

            // Act — collect all table names from entity model
            var tableNames = context.Model.GetEntityTypes()
                .Select(e => e.GetTableName())
                .Where(name => name != null)
                .ToList();

            // Assert — no rec_* prefix (dynamic entity record tables from Core/CRM/Project)
            tableNames.Should().NotContain(
                name => name.StartsWith("rec_", StringComparison.OrdinalIgnoreCase),
                "AdminDbContext should not contain any dynamic entity record tables (rec_*)");

            // Assert — no rel_* prefix (relation join tables from Core service)
            tableNames.Should().NotContain(
                name => name.StartsWith("rel_", StringComparison.OrdinalIgnoreCase),
                "AdminDbContext should not contain any relation join tables (rel_*)");
        }

        #endregion

        #region Nullable Property Configuration Tests (Tests 19–21)

        /// <summary>
        /// Test 19: Validates JobEntry nullable property configuration matches monolith behavior.
        /// Source: WebVella.Erp/Jobs/JobDataService.cs — null checks:
        ///   if (job.Attributes != null) — line 34
        ///   if (job.StartedOn.HasValue) — line 38
        ///   if (job.FinishedOn.HasValue) — line 40
        ///   if (job.AbortedBy.HasValue) — line 42
        ///   if (job.CanceledBy.HasValue) — line 44
        ///   if (!string.IsNullOrEmpty(job.ErrorMessage)) — line 46
        ///   if (job.SchedulePlanId.HasValue) — line 48
        ///   if (job.CreatedBy.HasValue) — line 52
        ///   if (job.LastModifiedBy.HasValue) — line 54
        /// </summary>
        [Fact]
        public void JobEntity_NullableProperties_ShouldBeConfiguredCorrectly()
        {
            // Arrange
            using var context = CreateInMemoryContext();
            var entityType = context.Model.FindEntityType(typeof(JobEntry));
            entityType.Should().NotBeNull();

            // Assert — nullable properties (from JobDataService.cs null checks)
            AssertPropertyNullability(entityType, "Attributes", expectedNullable: true);
            AssertPropertyNullability(entityType, "StartedOn", expectedNullable: true);
            AssertPropertyNullability(entityType, "FinishedOn", expectedNullable: true);
            AssertPropertyNullability(entityType, "AbortedBy", expectedNullable: true);
            AssertPropertyNullability(entityType, "CanceledBy", expectedNullable: true);
            AssertPropertyNullability(entityType, "ErrorMessage", expectedNullable: true);
            AssertPropertyNullability(entityType, "SchedulePlanId", expectedNullable: true);
            AssertPropertyNullability(entityType, "CreatedBy", expectedNullable: true);
            AssertPropertyNullability(entityType, "LastModifiedBy", expectedNullable: true);
            AssertPropertyNullability(entityType, "Result", expectedNullable: true);

            // Assert — required (non-nullable) properties
            AssertPropertyNullability(entityType, "Id", expectedNullable: false);
            AssertPropertyNullability(entityType, "TypeId", expectedNullable: false);
            AssertPropertyNullability(entityType, "TypeName", expectedNullable: false);
            AssertPropertyNullability(entityType, "CompleteClassName", expectedNullable: false);
            AssertPropertyNullability(entityType, "Status", expectedNullable: false);
            AssertPropertyNullability(entityType, "Priority", expectedNullable: false);
            AssertPropertyNullability(entityType, "CreatedOn", expectedNullable: false);
            AssertPropertyNullability(entityType, "LastModifiedOn", expectedNullable: false);
        }

        /// <summary>
        /// Test 20: Validates SystemLogEntry nullable property configuration.
        /// Source: WebVella.Erp/Diagnostics/Log.cs — line 41: if (dr["details"] == DBNull.Value)
        /// Only the Details column is nullable; all others are required.
        /// </summary>
        [Fact]
        public void SystemLogEntity_NullableProperties_ShouldBeConfiguredCorrectly()
        {
            // Arrange
            using var context = CreateInMemoryContext();
            var entityType = context.Model.FindEntityType(typeof(SystemLogEntry));
            entityType.Should().NotBeNull();

            // Assert — nullable: Details only
            AssertPropertyNullability(entityType, "Details", expectedNullable: true);

            // Assert — required (NOT NULL) properties
            AssertPropertyNullability(entityType, "Id", expectedNullable: false);
            AssertPropertyNullability(entityType, "CreatedOn", expectedNullable: false);
            AssertPropertyNullability(entityType, "Type", expectedNullable: false);
            AssertPropertyNullability(entityType, "Source", expectedNullable: false);
            AssertPropertyNullability(entityType, "Message", expectedNullable: false);
            AssertPropertyNullability(entityType, "NotificationStatus", expectedNullable: false);
        }

        /// <summary>
        /// Test 21: Validates SchedulePlanEntry nullable property configuration.
        /// Source: WebVella.Erp/Jobs/Models/SchedulePlan.cs (nullable CLR types: DateTime?,
        /// int?, Guid?, string for JSON columns).
        /// </summary>
        [Fact]
        public void SchedulePlanEntity_NullableProperties_ShouldBeConfiguredCorrectly()
        {
            // Arrange
            using var context = CreateInMemoryContext();
            var entityType = context.Model.FindEntityType(typeof(SchedulePlanEntry));
            entityType.Should().NotBeNull();

            // Assert — nullable properties
            AssertPropertyNullability(entityType, "StartDate", expectedNullable: true);
            AssertPropertyNullability(entityType, "EndDate", expectedNullable: true);
            AssertPropertyNullability(entityType, "ScheduledDays", expectedNullable: true);
            AssertPropertyNullability(entityType, "IntervalInMinutes", expectedNullable: true);
            AssertPropertyNullability(entityType, "StartTimespan", expectedNullable: true);
            AssertPropertyNullability(entityType, "EndTimespan", expectedNullable: true);
            AssertPropertyNullability(entityType, "LastTriggerTime", expectedNullable: true);
            AssertPropertyNullability(entityType, "NextTriggerTime", expectedNullable: true);
            AssertPropertyNullability(entityType, "JobAttributes", expectedNullable: true);
            AssertPropertyNullability(entityType, "LastStartedJobId", expectedNullable: true);
            AssertPropertyNullability(entityType, "LastModifiedBy", expectedNullable: true);

            // Assert — required (NOT NULL) properties
            AssertPropertyNullability(entityType, "Id", expectedNullable: false);
            AssertPropertyNullability(entityType, "Name", expectedNullable: false);
            AssertPropertyNullability(entityType, "Type", expectedNullable: false);
            AssertPropertyNullability(entityType, "JobTypeId", expectedNullable: false);
            AssertPropertyNullability(entityType, "Enabled", expectedNullable: false);
            AssertPropertyNullability(entityType, "CreatedOn", expectedNullable: false);
            AssertPropertyNullability(entityType, "LastModifiedOn", expectedNullable: false);
        }

        #endregion

        #region Entity Count and Completeness Tests (Tests 22–23)

        /// <summary>
        /// Test 22: Validates AdminDbContext has exactly 4 DbSet properties via reflection.
        /// Prevents accidental addition of entities from other services (Core, CRM, Project, Mail).
        /// The 4 expected DbSets: SystemLogs, Jobs, SchedulePlans, PluginData.
        /// </summary>
        [Fact]
        public void AdminDbContext_ShouldHaveExactlyFourDbSets()
        {
            // Arrange — find all DbSet<T> properties via reflection
            var dbSetProperties = typeof(AdminDbContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType &&
                            p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .ToList();

            // Assert — exactly 4 DbSet properties
            dbSetProperties.Should().HaveCount(4,
                "AdminDbContext should have exactly 4 DbSet properties: " +
                "SystemLogs, Jobs, SchedulePlans, PluginData");

            // Assert — verify the expected DbSet property names
            var propertyNames = dbSetProperties.Select(p => p.Name).ToList();
            propertyNames.Should().Contain("SystemLogs");
            propertyNames.Should().Contain("Jobs");
            propertyNames.Should().Contain("SchedulePlans");
            propertyNames.Should().Contain("PluginData");
        }

        /// <summary>
        /// Test 23: Validates AdminDbContext model registers exactly 4 entity types.
        /// Ensures no additional entities are registered beyond the admin domain.
        /// Per AAP 0.4.1: Admin service owns system_log, jobs, schedule_plans, plugin_data ONLY.
        /// </summary>
        [Fact]
        public void AdminDbContext_ModelEntityCount_ShouldBeFour()
        {
            // Arrange
            using var context = CreateInMemoryContext();

            // Act
            var entityCount = context.Model.GetEntityTypes().Count();

            // Assert
            entityCount.Should().Be(4,
                "AdminDbContext model should contain exactly 4 entity types: " +
                "SystemLogEntry, JobEntry, SchedulePlanEntry, PluginDataEntry");
        }

        #endregion
    }
}
