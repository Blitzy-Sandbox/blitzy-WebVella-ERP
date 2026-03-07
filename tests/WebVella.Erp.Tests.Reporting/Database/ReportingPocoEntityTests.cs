using System;
using FluentAssertions;
using WebVella.Erp.Service.Reporting.Database;
using Xunit;

namespace WebVella.Erp.Tests.Reporting.Database
{
    /// <summary>
    /// Pure unit tests (no database required) validating the C# default property values
    /// for the four POCO entity classes defined in <see cref="ReportingDbContext"/>:
    /// <see cref="TimelogProjection"/>, <see cref="TaskProjection"/>,
    /// <see cref="ProjectProjection"/>, and <see cref="ReportDefinition"/>.
    ///
    /// These are instantiation tests ensuring POCO entities have correct C# initializers
    /// before any EF Core or database interaction. All default values are derived from the
    /// monolith's ReportService.GetTimelogData() cross-entity EQL query patterns and the
    /// TimeLogService domain conventions.
    ///
    /// Key verified invariants:
    /// - Scope defaults to "projects" (monolith ReportService.cs line 40)
    /// - Minutes defaults to 0 (monolith ReportService.cs lines 112-113)
    /// - ParametersJson defaults to "{}" (empty JSON object for report configs)
    /// - ReportType defaults to "timelog" (primary report type from monolith)
    /// - All non-nullable Guid properties default to Guid.Empty
    /// - All nullable Guid?/string? properties default to null
    /// - All string properties with initializers have correct default values
    /// - All POCO classes have public parameterless constructors for EF Core materialization
    /// </summary>
    public class ReportingPocoEntityTests
    {
        #region TimelogProjection Default Value Tests

        /// <summary>
        /// Verifies that a new <see cref="TimelogProjection"/> instance has its
        /// <see cref="TimelogProjection.Scope"/> property defaulting to "projects".
        /// Derived from the monolith's ReportService.cs line 40 which filters with
        /// <c>new EqlParameter("scope", "projects")</c> — the default scope for timelog projections.
        /// </summary>
        [Fact]
        public void TimelogProjection_NewInstance_ShouldHaveDefaultScope()
        {
            // Arrange & Act
            var projection = new TimelogProjection();

            // Assert — scope defaults to "projects" per monolith convention
            projection.Scope.Should().Be("projects");
        }

        /// <summary>
        /// Verifies that a new <see cref="TimelogProjection"/> instance has its
        /// <see cref="TimelogProjection.Minutes"/> property defaulting to zero.
        /// Derived from the monolith's ReportService.cs lines 112-113 which initialize
        /// <c>rec["billable_minutes"] = (decimal)0</c> and <c>rec["non_billable_minutes"] = (decimal)0</c>.
        /// </summary>
        [Fact]
        public void TimelogProjection_NewInstance_ShouldHaveDefaultMinutesZero()
        {
            // Arrange & Act
            var projection = new TimelogProjection();

            // Assert — minutes defaults to decimal zero
            projection.Minutes.Should().Be(0m);
        }

        /// <summary>
        /// Verifies that a new <see cref="TimelogProjection"/> instance has its
        /// <see cref="TimelogProjection.Id"/> property defaulting to <see cref="Guid.Empty"/>.
        /// Non-nullable Guid value type defaults to Guid.Empty in C#.
        /// </summary>
        [Fact]
        public void TimelogProjection_NewInstance_ShouldHaveEmptyGuidForId()
        {
            // Arrange & Act
            var projection = new TimelogProjection();

            // Assert — Guid default is Guid.Empty
            projection.Id.Should().Be(Guid.Empty);
        }

        /// <summary>
        /// Verifies that a new <see cref="TimelogProjection"/> instance has its
        /// <see cref="TimelogProjection.TaskId"/> property defaulting to <see cref="Guid.Empty"/>.
        /// TaskId is a non-nullable Guid referencing the Project service's task entity.
        /// </summary>
        [Fact]
        public void TimelogProjection_NewInstance_ShouldHaveEmptyGuidForTaskId()
        {
            // Arrange & Act
            var projection = new TimelogProjection();

            // Assert — non-nullable Guid defaults to Guid.Empty
            projection.TaskId.Should().Be(Guid.Empty);
        }

        /// <summary>
        /// Verifies that a new <see cref="TimelogProjection"/> instance has its
        /// <see cref="TimelogProjection.ProjectId"/> property defaulting to null.
        /// ProjectId is a nullable Guid? because a timelog may reference a task
        /// with no project association (cross-service reference, no FK).
        /// </summary>
        [Fact]
        public void TimelogProjection_NewInstance_ShouldHaveNullProjectId()
        {
            // Arrange & Act
            var projection = new TimelogProjection();

            // Assert — Guid? nullable defaults to null
            projection.ProjectId.Should().BeNull();
        }

        /// <summary>
        /// Verifies that a new <see cref="TimelogProjection"/> instance has its
        /// <see cref="TimelogProjection.AccountId"/> property defaulting to null.
        /// AccountId is a nullable Guid? because a project may have no associated account
        /// (cross-service reference to CRM service's account entity).
        /// </summary>
        [Fact]
        public void TimelogProjection_NewInstance_ShouldHaveNullAccountId()
        {
            // Arrange & Act
            var projection = new TimelogProjection();

            // Assert — Guid? nullable defaults to null
            projection.AccountId.Should().BeNull();
        }

        /// <summary>
        /// Verifies that a new <see cref="TimelogProjection"/> instance has its
        /// <see cref="TimelogProjection.IsBillable"/> property defaulting to false.
        /// C# bool default is false; the monolith determines billability from the
        /// timelog record's is_billable field at runtime.
        /// </summary>
        [Fact]
        public void TimelogProjection_NewInstance_ShouldHaveFalseIsBillable()
        {
            // Arrange & Act
            var projection = new TimelogProjection();

            // Assert — bool default is false
            projection.IsBillable.Should().BeFalse();
        }

        /// <summary>
        /// Verifies that all properties on <see cref="TimelogProjection"/> can be set
        /// to non-default values and retain those values (round-trip property check).
        /// This ensures the POCO entity supports full read/write property access
        /// for EF Core materialization and event subscriber population.
        /// </summary>
        [Fact]
        public void TimelogProjection_ShouldAllowSettingAllProperties()
        {
            // Arrange
            var id = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var loggedOn = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
            var createdOn = new DateTime(2025, 6, 15, 11, 0, 0, DateTimeKind.Utc);
            var lastModifiedOn = new DateTime(2025, 6, 16, 9, 0, 0, DateTimeKind.Utc);

            // Act
            var projection = new TimelogProjection
            {
                Id = id,
                TaskId = taskId,
                ProjectId = projectId,
                AccountId = accountId,
                IsBillable = true,
                Minutes = 120.5m,
                LoggedOn = loggedOn,
                Scope = "custom_scope",
                CreatedOn = createdOn,
                LastModifiedOn = lastModifiedOn
            };

            // Assert — all properties retain assigned values
            projection.Id.Should().Be(id);
            projection.TaskId.Should().Be(taskId);
            projection.ProjectId.Should().Be(projectId);
            projection.AccountId.Should().Be(accountId);
            projection.IsBillable.Should().Be(true);
            projection.Minutes.Should().Be(120.5m);
            projection.LoggedOn.Should().Be(loggedOn);
            projection.Scope.Should().Be("custom_scope");
            projection.CreatedOn.Should().Be(createdOn);
            projection.LastModifiedOn.Should().Be(lastModifiedOn);
        }

        #endregion

        #region TaskProjection Default Value Tests

        /// <summary>
        /// Verifies that a new <see cref="TaskProjection"/> instance has its
        /// <see cref="TaskProjection.Subject"/> property defaulting to an empty string.
        /// The property initializer <c>string Subject { get; set; } = ""</c> ensures a
        /// non-null default for task subjects before EF Core populates from the database.
        /// </summary>
        [Fact]
        public void TaskProjection_NewInstance_ShouldHaveEmptySubject()
        {
            // Arrange & Act
            var projection = new TaskProjection();

            // Assert — string initializer defaults to ""
            projection.Subject.Should().Be("");
        }

        /// <summary>
        /// Verifies that a new <see cref="TaskProjection"/> instance has its
        /// <see cref="TaskProjection.TaskTypeLabel"/> property defaulting to null.
        /// TaskTypeLabel is a nullable string? because a task may not have a type assigned
        /// (resolved from $task_type_1n_task relation in the monolith).
        /// </summary>
        [Fact]
        public void TaskProjection_NewInstance_ShouldHaveNullTaskTypeLabel()
        {
            // Arrange & Act
            var projection = new TaskProjection();

            // Assert — string? nullable without initializer defaults to null
            projection.TaskTypeLabel.Should().BeNull();
        }

        /// <summary>
        /// Verifies that a new <see cref="TaskProjection"/> instance has its
        /// <see cref="TaskProjection.Id"/> property defaulting to <see cref="Guid.Empty"/>.
        /// Non-nullable Guid value type defaults to Guid.Empty in C#.
        /// </summary>
        [Fact]
        public void TaskProjection_NewInstance_ShouldHaveEmptyGuidForId()
        {
            // Arrange & Act
            var projection = new TaskProjection();

            // Assert — Guid default is Guid.Empty
            projection.Id.Should().Be(Guid.Empty);
        }

        #endregion

        #region ProjectProjection Default Value Tests

        /// <summary>
        /// Verifies that a new <see cref="ProjectProjection"/> instance has its
        /// <see cref="ProjectProjection.Name"/> property defaulting to an empty string.
        /// The property initializer <c>string Name { get; set; } = ""</c> ensures a
        /// non-null default for project names before EF Core populates from the database.
        /// </summary>
        [Fact]
        public void ProjectProjection_NewInstance_ShouldHaveEmptyName()
        {
            // Arrange & Act
            var projection = new ProjectProjection();

            // Assert — string initializer defaults to ""
            projection.Name.Should().Be("");
        }

        /// <summary>
        /// Verifies that a new <see cref="ProjectProjection"/> instance has its
        /// <see cref="ProjectProjection.AccountId"/> property defaulting to null.
        /// AccountId is a nullable Guid? because a project may not be associated with an account
        /// (cross-service reference to CRM service's account entity).
        /// </summary>
        [Fact]
        public void ProjectProjection_NewInstance_ShouldHaveNullAccountId()
        {
            // Arrange & Act
            var projection = new ProjectProjection();

            // Assert — Guid? nullable defaults to null
            projection.AccountId.Should().BeNull();
        }

        /// <summary>
        /// Verifies that a new <see cref="ProjectProjection"/> instance has its
        /// <see cref="ProjectProjection.Id"/> property defaulting to <see cref="Guid.Empty"/>.
        /// Non-nullable Guid value type defaults to Guid.Empty in C#.
        /// </summary>
        [Fact]
        public void ProjectProjection_NewInstance_ShouldHaveEmptyGuidForId()
        {
            // Arrange & Act
            var projection = new ProjectProjection();

            // Assert — Guid default is Guid.Empty
            projection.Id.Should().Be(Guid.Empty);
        }

        #endregion

        #region ReportDefinition Default Value Tests

        /// <summary>
        /// Verifies that a new <see cref="ReportDefinition"/> instance has its
        /// <see cref="ReportDefinition.ParametersJson"/> property defaulting to "{}".
        /// The property initializer <c>string ParametersJson { get; set; } = "{}"</c>
        /// provides a valid empty JSON object as the default for saved report configurations.
        /// </summary>
        [Fact]
        public void ReportDefinition_NewInstance_ShouldHaveDefaultParametersJson()
        {
            // Arrange & Act
            var definition = new ReportDefinition();

            // Assert — defaults to empty JSON object
            definition.ParametersJson.Should().Be("{}");
        }

        /// <summary>
        /// Verifies that a new <see cref="ReportDefinition"/> instance has its
        /// <see cref="ReportDefinition.Name"/> property defaulting to an empty string.
        /// The property initializer <c>string Name { get; set; } = ""</c> ensures a
        /// non-null default for report names before they are set by the user.
        /// </summary>
        [Fact]
        public void ReportDefinition_NewInstance_ShouldHaveEmptyName()
        {
            // Arrange & Act
            var definition = new ReportDefinition();

            // Assert — string initializer defaults to ""
            definition.Name.Should().Be("");
        }

        /// <summary>
        /// Verifies that a new <see cref="ReportDefinition"/> instance has its
        /// <see cref="ReportDefinition.Description"/> property defaulting to null.
        /// Description is an optional nullable string? with no initializer.
        /// </summary>
        [Fact]
        public void ReportDefinition_NewInstance_ShouldHaveNullDescription()
        {
            // Arrange & Act
            var definition = new ReportDefinition();

            // Assert — string? nullable without initializer defaults to null
            definition.Description.Should().BeNull();
        }

        /// <summary>
        /// Verifies that a new <see cref="ReportDefinition"/> instance has its
        /// <see cref="ReportDefinition.ReportType"/> property defaulting to "timelog".
        /// The property initializer <c>string ReportType { get; set; } = "timelog"</c>
        /// matches the primary report type from the monolith's ReportService.
        /// </summary>
        [Fact]
        public void ReportDefinition_NewInstance_ShouldHaveDefaultReportTypeTimelog()
        {
            // Arrange & Act
            var definition = new ReportDefinition();

            // Assert — defaults to "timelog" matching the monolith's primary report type
            definition.ReportType.Should().Be("timelog");
        }

        /// <summary>
        /// Verifies that a new <see cref="ReportDefinition"/> instance has its
        /// <see cref="ReportDefinition.CreatedBy"/> property defaulting to <see cref="Guid.Empty"/>.
        /// CreatedBy is a non-nullable Guid referencing the Core service's user entity.
        /// </summary>
        [Fact]
        public void ReportDefinition_NewInstance_ShouldHaveEmptyGuidForCreatedBy()
        {
            // Arrange & Act
            var definition = new ReportDefinition();

            // Assert — Guid default is Guid.Empty
            definition.CreatedBy.Should().Be(Guid.Empty);
        }

        /// <summary>
        /// Verifies that all properties on <see cref="ReportDefinition"/> can be set
        /// to non-default values and retain those values (round-trip property check).
        /// This ensures the POCO entity supports full read/write property access
        /// for EF Core materialization and Reporting service API operations.
        /// </summary>
        [Fact]
        public void ReportDefinition_ShouldAllowSettingAllProperties()
        {
            // Arrange
            var id = Guid.NewGuid();
            var createdBy = Guid.NewGuid();
            var createdOn = new DateTime(2025, 7, 1, 8, 0, 0, DateTimeKind.Utc);
            var lastModifiedOn = new DateTime(2025, 7, 2, 14, 30, 0, DateTimeKind.Utc);

            // Act
            var definition = new ReportDefinition
            {
                Id = id,
                Name = "Monthly Timelog Report",
                Description = "Aggregated timelog data by project and account for monthly billing",
                ReportType = "project_summary",
                ParametersJson = "{\"year\":2025,\"month\":7,\"accountId\":\"abc123\"}",
                CreatedBy = createdBy,
                CreatedOn = createdOn,
                LastModifiedOn = lastModifiedOn
            };

            // Assert — all properties retain assigned values
            definition.Id.Should().Be(id);
            definition.Name.Should().Be("Monthly Timelog Report");
            definition.Description.Should().Be("Aggregated timelog data by project and account for monthly billing");
            definition.ReportType.Should().Be("project_summary");
            definition.ParametersJson.Should().Be("{\"year\":2025,\"month\":7,\"accountId\":\"abc123\"}");
            definition.CreatedBy.Should().Be(createdBy);
            definition.CreatedOn.Should().Be(createdOn);
            definition.LastModifiedOn.Should().Be(lastModifiedOn);
        }

        #endregion

        #region Cross-POCO Validation Tests

        /// <summary>
        /// Verifies that all four POCO entity classes have public parameterless constructors.
        /// EF Core requires parameterless constructors for entity materialization from database
        /// query results. This test uses <see cref="Activator.CreateInstance(Type)"/> to
        /// confirm runtime constructability, matching EF Core's internal instantiation pattern.
        /// </summary>
        [Fact]
        public void AllPocos_ShouldHavePublicParameterlessConstructor()
        {
            // Act & Assert — TimelogProjection
            var timelog = Activator.CreateInstance(typeof(TimelogProjection));
            timelog.Should().NotBeNull();

            // Act & Assert — TaskProjection
            var task = Activator.CreateInstance(typeof(TaskProjection));
            task.Should().NotBeNull();

            // Act & Assert — ProjectProjection
            var project = Activator.CreateInstance(typeof(ProjectProjection));
            project.Should().NotBeNull();

            // Act & Assert — ReportDefinition
            var report = Activator.CreateInstance(typeof(ReportDefinition));
            report.Should().NotBeNull();
        }

        #endregion
    }
}
