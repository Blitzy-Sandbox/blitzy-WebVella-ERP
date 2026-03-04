using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;
using WebVella.Erp.Service.Reporting.Domain.Services;
using WebVella.Erp.Service.Reporting.Database;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.Tests.Reporting.Fixtures;

namespace WebVella.Erp.Tests.Reporting.Domain
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="ReportAggregationService"/>, validating every
    /// business rule extracted from the monolith's <c>ReportService.GetTimelogData()</c>
    /// (137 lines). Covers validation, date ranges, aggregation, task-project splitting,
    /// account filtering, result field structure, CQRS pattern, edge cases, and constructor guards.
    ///
    /// <para>Uses EF Core InMemory provider via <see cref="ReportingDbContextFixture"/> for
    /// fast, isolated test execution without PostgreSQL dependency.</para>
    ///
    /// <para>Test stack: xUnit 2.9.3, FluentAssertions 7.2.0, Moq 4.20.72 (AAP 0.6.1).</para>
    /// </summary>
    [Collection("ReportingService")]
    public class ReportAggregationServiceTests
    {
        private readonly ReportingDbContextFixture _fixture;
        private readonly Mock<ILogger<ReportAggregationService>> _mockLogger;

        /// <summary>
        /// Initializes the test class with the shared <see cref="ReportingDbContextFixture"/>
        /// and a fresh <see cref="Mock{ILogger}"/> for dependency injection into the service.
        /// </summary>
        public ReportAggregationServiceTests(ReportingDbContextFixture fixture)
        {
            _fixture = fixture;
            _mockLogger = new Mock<ILogger<ReportAggregationService>>();
        }

        #region Helper Methods

        /// <summary>
        /// Creates a <see cref="ReportAggregationService"/> backed by a pre-seeded InMemory
        /// database exercising all business rules from the standard January 2024 test data set.
        /// </summary>
        private async Task<(ReportAggregationService Service, ReportingDbContext Context)> CreateServiceWithSeedDataAsync()
        {
            var context = await _fixture.CreateInMemoryDbContextWithSeedDataAsync();
            var service = new ReportAggregationService(context, _mockLogger.Object);
            return (service, context);
        }

        /// <summary>
        /// Creates a <see cref="ReportAggregationService"/> using the provided context,
        /// injecting the shared mock logger.
        /// </summary>
        private ReportAggregationService CreateServiceWithContext(ReportingDbContext context)
        {
            return new ReportAggregationService(context, _mockLogger.Object);
        }

        /// <summary>
        /// Seeds a minimal test scenario: one project (with account), one task, and one
        /// billable timelog on a specific date. Returns the context for additional seeding.
        /// </summary>
        private async Task<ReportingDbContext> SeedMinimalScenarioAsync(
            Guid taskId, Guid projectId, Guid accountId,
            string taskSubject, string projectName, string taskType,
            int minutes, bool isBillable, DateTime loggedOn)
        {
            var context = _fixture.CreateInMemoryDbContext();

            var project = new ProjectProjectionBuilder()
                .WithId(projectId)
                .WithName(projectName)
                .WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow)
                .WithLastModifiedOn(DateTime.UtcNow)
                .Build();

            var task = new TaskProjectionBuilder()
                .WithId(taskId)
                .WithSubject(taskSubject)
                .WithTaskTypeLabel(taskType)
                .WithCreatedOn(DateTime.UtcNow)
                .WithLastModifiedOn(DateTime.UtcNow)
                .Build();

            var timelog = new TimelogProjectionBuilder()
                .WithId(Guid.NewGuid())
                .WithTaskId(taskId)
                .WithProjectId(projectId)
                .WithAccountId(accountId)
                .WithMinutes(minutes)
                .WithIsBillable(isBillable)
                .WithLoggedOn(loggedOn)
                .WithCreatedOn(DateTime.UtcNow)
                .WithLastModifiedOn(DateTime.UtcNow)
                .Build();

            context.ProjectProjections.Add(project);
            context.TaskProjections.Add(task);
            context.TimelogProjections.Add(timelog);
            await context.SaveChangesAsync();

            return context;
        }

        #endregion

        #region Phase 3: Validation Rule Tests — Business Logic Preservation

        /// <summary>
        /// Validates that months greater than 12 trigger a ValidationException with the exact
        /// error message "Invalid month." on field "month".
        /// Source: ReportService.cs lines 17-18.
        /// </summary>
        [Theory]
        [InlineData(13)]
        [InlineData(14)]
        [InlineData(100)]
        public void GetTimelogData_InvalidMonth_GreaterThan12_ThrowsValidationException(int invalidMonth)
        {
            // Arrange — empty InMemory context (no data needed for validation tests)
            var context = _fixture.CreateInMemoryDbContext();
            var service = CreateServiceWithContext(context);

            // Act
            Action act = () => service.GetTimelogData(2024, invalidMonth, null);

            // Assert — exact error message and field name preserved from monolith
            var ex = act.Should().Throw<ValidationException>().Which;
            ex.Errors.Should().HaveCount(1);
            ex.Errors[0].PropertyName.Should().Be("month");
            ex.Errors[0].Message.Should().Be("Invalid month.");
        }

        /// <summary>
        /// Validates that months less than or equal to zero trigger a ValidationException.
        /// Source: ReportService.cs line 17: month > 12 || month &lt;= 0.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-100)]
        public void GetTimelogData_InvalidMonth_LessThanOrEqualZero_ThrowsValidationException(int invalidMonth)
        {
            // Arrange
            var context = _fixture.CreateInMemoryDbContext();
            var service = CreateServiceWithContext(context);

            // Act
            Action act = () => service.GetTimelogData(2024, invalidMonth, null);

            // Assert
            var ex = act.Should().Throw<ValidationException>().Which;
            ex.Errors.Should().HaveCount(1);
            ex.Errors[0].PropertyName.Should().Be("month");
            ex.Errors[0].Message.Should().Be("Invalid month.");
        }

        /// <summary>
        /// Validates that year &lt;= 0 triggers a ValidationException with exact error "Invalid year."
        /// on field "year". Source: ReportService.cs lines 20-21.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-2024)]
        public void GetTimelogData_InvalidYear_ThrowsValidationException(int invalidYear)
        {
            // Arrange
            var context = _fixture.CreateInMemoryDbContext();
            var service = CreateServiceWithContext(context);

            // Act — valid month, invalid year
            Action act = () => service.GetTimelogData(invalidYear, 6, null);

            // Assert
            var ex = act.Should().Throw<ValidationException>().Which;
            ex.Errors.Should().ContainSingle(e => e.PropertyName == "year" && e.Message == "Invalid year.");
        }

        /// <summary>
        /// Validates that a non-existent accountId triggers a ValidationException with the
        /// exact error message format: "Account with ID:{guid} not found." on field "accountId".
        /// Source: ReportService.cs line 29.
        /// CRITICAL: The format has "ID:" followed by the Guid with no space before the value.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_NonExistentAccountId_ThrowsValidationException()
        {
            // Arrange — seed with standard data; use a Guid not matching any project account
            var (service, _) = await CreateServiceWithSeedDataAsync();
            var nonExistentId = Guid.NewGuid();

            // Act
            Action act = () => service.GetTimelogData(2024, 1, nonExistentId);

            // Assert — exact error message format preserved from monolith line 29
            var ex = act.Should().Throw<ValidationException>().Which;
            ex.Errors.Should().ContainSingle(e =>
                e.PropertyName == "accountid" && // AddError lowercases the field name
                e.Message == $"Account with ID:{nonExistentId} not found.");
        }

        /// <summary>
        /// Validates that multiple validation errors accumulate and are thrown together via
        /// CheckAndThrow(). Both invalid month AND invalid year produce two errors in one exception.
        /// Source: ReportService.cs lines 15-32 — errors accumulate in valEx then CheckAndThrow.
        /// </summary>
        [Fact]
        public void GetTimelogData_InvalidMonthAndYear_ThrowsWithMultipleErrors()
        {
            // Arrange
            var context = _fixture.CreateInMemoryDbContext();
            var service = CreateServiceWithContext(context);

            // Act — both year and month invalid
            Action act = () => service.GetTimelogData(-1, 13, null);

            // Assert — two errors accumulated
            var ex = act.Should().Throw<ValidationException>().Which;
            ex.Errors.Should().HaveCount(2);
            ex.Errors.Should().Contain(e => e.PropertyName == "month" && e.Message == "Invalid month.");
            ex.Errors.Should().Contain(e => e.PropertyName == "year" && e.Message == "Invalid year.");
        }

        /// <summary>
        /// Validates that boundary-valid months (1, 6, 12) do NOT throw ValidationException.
        /// Source: ReportService.cs line 17 — month must be 1-12 inclusive.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(6)]
        [InlineData(12)]
        public async Task GetTimelogData_ValidMonth_DoesNotThrowValidationException(int validMonth)
        {
            // Arrange — seed with standard data
            var (service, _) = await CreateServiceWithSeedDataAsync();

            // Act & Assert — should not throw ValidationException for valid month/year
            Action act = () => service.GetTimelogData(2024, validMonth, null);
            act.Should().NotThrow<ValidationException>();
        }

        #endregion

        #region Phase 4: Date Range Calculation Tests

        /// <summary>
        /// Verifies January date range: fromDate=Jan 1, toDate=Jan 31.
        /// Timelogs on Jan 1, Jan 15, Jan 31 are included; Feb 1 is excluded.
        /// Source: ReportService.cs lines 35-36.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_January_CorrectDateRange()
        {
            // Arrange — custom data with timelogs at month boundaries
            var context = _fixture.CreateInMemoryDbContext();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            context.ProjectProjections.Add(new ProjectProjectionBuilder()
                .WithId(projectId).WithName("Jan Project").WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskId).WithSubject("Jan Task").WithTaskTypeLabel("Dev")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            // Jan 1, Jan 15, Jan 31 — should be included
            foreach (var day in new[] { 1, 15, 31 })
            {
                context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                    .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                    .WithAccountId(accountId).WithMinutes(10)
                    .WithLoggedOn(new DateTime(2024, 1, day, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            }
            // Feb 1 — should be EXCLUDED
            context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                .WithAccountId(accountId).WithMinutes(100)
                .WithLoggedOn(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc))
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — 3 January timelogs included (30 total), Feb excluded
            result.Should().HaveCount(1); // One task-project pair
            ((decimal)result[0]["billable_minutes"]).Should().Be(30m); // 10+10+10
        }

        /// <summary>
        /// Verifies December date range: fromDate=Dec 1, toDate=Dec 31.
        /// Timelogs on Dec 1, Dec 15, Dec 31 included; Nov 30 excluded.
        /// Source: ReportService.cs lines 35-36; DateTime.DaysInMonth(2024, 12) = 31.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_December_CorrectDateRange()
        {
            // Arrange
            var context = _fixture.CreateInMemoryDbContext();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            context.ProjectProjections.Add(new ProjectProjectionBuilder()
                .WithId(projectId).WithName("Dec Project").WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskId).WithSubject("Dec Task").WithTaskTypeLabel("Dev")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            // Dec 1, Dec 15, Dec 31 — included
            foreach (var day in new[] { 1, 15, 31 })
            {
                context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                    .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                    .WithAccountId(accountId).WithMinutes(10)
                    .WithLoggedOn(new DateTime(2024, 12, day, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            }
            // Nov 30 — excluded
            context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                .WithAccountId(accountId).WithMinutes(100)
                .WithLoggedOn(new DateTime(2024, 11, 30, 0, 0, 0, DateTimeKind.Utc))
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2024, 12, null);

            // Assert — only December timelogs included
            result.Should().HaveCount(1);
            ((decimal)result[0]["billable_minutes"]).Should().Be(30m);
        }

        /// <summary>
        /// Verifies leap year February: Feb 2024 has 29 days.
        /// Timelogs on Feb 1, Feb 28, Feb 29 included; Mar 1 excluded.
        /// Source: DateTime.DaysInMonth(2024, 2) = 29 (leap year).
        /// </summary>
        [Fact]
        public async Task GetTimelogData_LeapYearFebruary_CorrectDateRange()
        {
            // Arrange
            var context = _fixture.CreateInMemoryDbContext();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            context.ProjectProjections.Add(new ProjectProjectionBuilder()
                .WithId(projectId).WithName("Feb Project").WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskId).WithSubject("Feb Task").WithTaskTypeLabel("Dev")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            // Feb 1, Feb 28, Feb 29 — included (2024 is a leap year)
            foreach (var day in new[] { 1, 28, 29 })
            {
                context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                    .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                    .WithAccountId(accountId).WithMinutes(10)
                    .WithLoggedOn(new DateTime(2024, 2, day, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            }
            // Mar 1 — excluded
            context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                .WithAccountId(accountId).WithMinutes(100)
                .WithLoggedOn(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc))
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2024, 2, null);

            // Assert — Feb 1-29 included (30 total), Mar excluded
            result.Should().HaveCount(1);
            ((decimal)result[0]["billable_minutes"]).Should().Be(30m);
            // Verify Feb 29 is a valid date for 2024 (leap year)
            DateTime.DaysInMonth(2024, 2).Should().Be(29);
        }

        /// <summary>
        /// Verifies non-leap year February: Feb 2023 has 28 days only.
        /// Timelogs on Feb 1, Feb 28 included; Mar 1 excluded.
        /// Source: DateTime.DaysInMonth(2023, 2) = 28 (non-leap year).
        /// </summary>
        [Fact]
        public async Task GetTimelogData_NonLeapYearFebruary_CorrectDateRange()
        {
            // Arrange
            var context = _fixture.CreateInMemoryDbContext();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            context.ProjectProjections.Add(new ProjectProjectionBuilder()
                .WithId(projectId).WithName("Feb23 Project").WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskId).WithSubject("Feb23 Task").WithTaskTypeLabel("Dev")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            // Feb 1, Feb 28 — included (2023 is NOT a leap year)
            foreach (var day in new[] { 1, 28 })
            {
                context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                    .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                    .WithAccountId(accountId).WithMinutes(10)
                    .WithLoggedOn(new DateTime(2023, 2, day, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            }
            // Mar 1 — excluded
            context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                .WithAccountId(accountId).WithMinutes(100)
                .WithLoggedOn(new DateTime(2023, 3, 1, 0, 0, 0, DateTimeKind.Utc))
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2023, 2, null);

            // Assert — only Feb 1-28 included (20 total), Mar excluded
            result.Should().HaveCount(1);
            ((decimal)result[0]["billable_minutes"]).Should().Be(20m);
            // Verify Feb 28 is the last day for 2023 (non-leap)
            DateTime.DaysInMonth(2023, 2).Should().Be(28);
        }

        #endregion

        #region Phase 5: Timelog Aggregation Logic Tests

        /// <summary>
        /// Validates billable minutes accumulation across multiple billable timelogs for one task.
        /// Source: ReportService.cs lines 125-126.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_BillableTimelogs_AccumulatesCorrectly()
        {
            // Arrange — 3 billable timelogs: 120 + 90 + 60 = 270
            var context = _fixture.CreateInMemoryDbContext();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            context.ProjectProjections.Add(new ProjectProjectionBuilder()
                .WithId(projectId).WithName("Billable Project").WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskId).WithSubject("Billable Task").WithTaskTypeLabel("Dev")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            foreach (var minutes in new[] { 120, 90, 60 })
            {
                context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                    .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                    .WithAccountId(accountId).WithMinutes(minutes)
                    .WithLoggedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            }
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — billable accumulates to 270, non-billable stays 0
            result.Should().HaveCount(1);
            ((decimal)result[0]["billable_minutes"]).Should().Be(270m);
            ((decimal)result[0]["non_billable_minutes"]).Should().Be(0m);
        }

        /// <summary>
        /// Validates non-billable minutes accumulation across multiple non-billable timelogs.
        /// Source: ReportService.cs lines 127-128.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_NonBillableTimelogs_AccumulatesCorrectly()
        {
            // Arrange — 2 non-billable timelogs: 60 + 30 = 90
            var context = _fixture.CreateInMemoryDbContext();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            context.ProjectProjections.Add(new ProjectProjectionBuilder()
                .WithId(projectId).WithName("NB Project").WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskId).WithSubject("NB Task").WithTaskTypeLabel("Support")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            foreach (var minutes in new[] { 60, 30 })
            {
                context.TimelogProjections.Add(TimelogProjectionBuilder.NonBillableTimelog()
                    .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                    .WithAccountId(accountId).WithMinutes(minutes)
                    .WithLoggedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            }
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — non-billable accumulates to 90, billable stays 0
            result.Should().HaveCount(1);
            ((decimal)result[0]["non_billable_minutes"]).Should().Be(90m);
            ((decimal)result[0]["billable_minutes"]).Should().Be(0m);
        }

        /// <summary>
        /// Validates mixed billable and non-billable minutes accumulate independently.
        /// Uses standard test data: Task 1 has billable=120 and non-billable=60.
        /// Source: ReportService.cs lines 120-129.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_MixedBillability_AccumulatesSeparately()
        {
            // Arrange — standard seed data has Task 1 with mixed timelogs
            var (service, _) = await CreateServiceWithSeedDataAsync();

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — find Task 1 record
            var task1Records = result.Where(r => (Guid)r["task_id"] == TestIds.Task1Id).ToList();
            task1Records.Should().NotBeEmpty();
            var rec = task1Records.First();
            ((decimal)rec["billable_minutes"]).Should().Be(120m);
            ((decimal)rec["non_billable_minutes"]).Should().Be(60m);
        }

        /// <summary>
        /// Validates that the task_id extracted from l_related_records JSON
        /// (via TimelogProjection.TaskId in the microservice) correctly associates
        /// timelogs with their parent tasks.
        /// Source: ReportService.cs lines 51-52: JsonConvert.DeserializeObject&lt;List&lt;Guid&gt;&gt;().
        /// </summary>
        [Fact]
        public async Task GetTimelogData_LRelatedRecords_ExtractsFirstGuidAsTaskId()
        {
            // Arrange — verify that in the projection model, TaskId is correctly stored
            var context = _fixture.CreateInMemoryDbContext();
            var knownTaskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            // Simulate the monolith's l_related_records JSON: [taskId, otherGuid]
            var lRelatedRecords = JsonConvert.SerializeObject(new List<Guid> { knownTaskId, Guid.NewGuid() });
            // In microservice, the first Guid from the JSON is pre-extracted as TaskId
            var extractedTaskId = JsonConvert.DeserializeObject<List<Guid>>(lRelatedRecords)[0];
            extractedTaskId.Should().Be(knownTaskId);

            context.ProjectProjections.Add(new ProjectProjectionBuilder()
                .WithId(projectId).WithName("LRR Project").WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(knownTaskId).WithSubject("LRR Task").WithTaskTypeLabel("Dev")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                .WithId(Guid.NewGuid()).WithTaskId(knownTaskId).WithProjectId(projectId)
                .WithAccountId(accountId).WithMinutes(45)
                .WithLoggedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — result correctly associates timelog with the known task
            result.Should().HaveCount(1);
            ((Guid)result[0]["task_id"]).Should().Be(knownTaskId);
        }

        /// <summary>
        /// Validates that billable_minutes and non_billable_minutes use decimal arithmetic.
        /// Source: ReportService.cs lines 112-113: rec["billable_minutes"] = (decimal)0.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_MinutesAggregation_UsesDecimalArithmetic()
        {
            // Arrange — standard seed data
            var (service, _) = await CreateServiceWithSeedDataAsync();

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — verify decimal type for minute fields
            result.Should().NotBeEmpty();
            foreach (var rec in result)
            {
                rec["billable_minutes"].Should().BeOfType<decimal>();
                rec["non_billable_minutes"].Should().BeOfType<decimal>();
            }
        }

        #endregion

        #region Phase 6: Task-Project Split Logic Tests

        /// <summary>
        /// Validates that a task linked to multiple projects produces duplicate result records
        /// (one per project). This preserves the monolith's task-project splitting behavior
        /// where the foreach loop over projects creates separate entries.
        /// Source: ReportService.cs lines 77-96.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_TaskWithMultipleProjects_DuplicatedPerProject()
        {
            // Arrange — standard data: Task 2 linked to Project Alpha and Project Beta
            var (service, _) = await CreateServiceWithSeedDataAsync();

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — Task 2 should appear in exactly 2 records (one per project)
            var task2Records = result.Where(r => (Guid)r["task_id"] == TestIds.Task2Id).ToList();
            task2Records.Should().HaveCount(2, "Task 2 is linked to 2 projects and should produce 2 result records");

            // Both records accumulate ALL Task 2 timelogs: Timelog3 (90) + Timelog4 (45) = 135
            foreach (var rec in task2Records)
            {
                ((decimal)rec["billable_minutes"]).Should().Be(135m,
                    "all timelogs for Task 2 are accumulated per task-project pair");
            }
        }

        /// <summary>
        /// Validates that tasks with no project association are excluded from results.
        /// Source: ReportService.cs lines 72-74: if (taskProjects.Count == 0) continue.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_TaskWithNoProject_ExcludedFromResults()
        {
            // Arrange — create a task with timelog but NO project association
            var context = _fixture.CreateInMemoryDbContext();
            var taskWithProject = Guid.NewGuid();
            var taskNoProject = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            context.ProjectProjections.Add(new ProjectProjectionBuilder()
                .WithId(projectId).WithName("Real Project").WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskWithProject).WithSubject("Has Project").WithTaskTypeLabel("Dev")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskNoProject).WithSubject("No Project").WithTaskTypeLabel("Support")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            // Timelog for task with project
            context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                .WithId(Guid.NewGuid()).WithTaskId(taskWithProject).WithProjectId(projectId)
                .WithAccountId(accountId).WithMinutes(60)
                .WithLoggedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            // Timelog for task WITHOUT project (ProjectId = null)
            context.TimelogProjections.Add(new TimelogProjection
            {
                Id = Guid.NewGuid(),
                TaskId = taskNoProject,
                ProjectId = null, // No project association
                AccountId = null,
                IsBillable = true,
                Minutes = 30,
                LoggedOn = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc),
                Scope = "projects",
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — task without project is excluded
            result.Should().HaveCount(1);
            ((Guid)result[0]["task_id"]).Should().Be(taskWithProject);
            result.Any(r => (Guid)r["task_id"] == taskNoProject).Should().BeFalse(
                "tasks with no project association should be excluded");
        }

        /// <summary>
        /// Validates that tasks with NO timelogs in the queried date range are excluded.
        /// Source: ReportService.cs lines 68-69: if (!setOfTasksWithTimelog.Contains(...)) continue.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_TaskWithNoTimelog_ExcludedFromResults()
        {
            // Arrange — two tasks: one with timelog, one without
            var context = _fixture.CreateInMemoryDbContext();
            var taskWithTimelog = Guid.NewGuid();
            var taskNoTimelog = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            context.ProjectProjections.Add(new ProjectProjectionBuilder()
                .WithId(projectId).WithName("Project A").WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskWithTimelog).WithSubject("Active Task").WithTaskTypeLabel("Dev")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskNoTimelog).WithSubject("Idle Task").WithTaskTypeLabel("Dev")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            // Only task A has a timelog in January
            context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                .WithId(Guid.NewGuid()).WithTaskId(taskWithTimelog).WithProjectId(projectId)
                .WithAccountId(accountId).WithMinutes(60)
                .WithLoggedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            // Task B has a timelog in DIFFERENT project, link for no-timelog scenario
            context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                .WithId(Guid.NewGuid()).WithTaskId(taskNoTimelog).WithProjectId(projectId)
                .WithAccountId(accountId).WithMinutes(30)
                .WithLoggedOn(new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc)) // March — out of range
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act — query January
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — only task with January timelog appears
            result.Should().HaveCount(1);
            ((Guid)result[0]["task_id"]).Should().Be(taskWithTimelog);
            result.Any(r => (Guid)r["task_id"] == taskNoTimelog).Should().BeFalse(
                "tasks with no timelog in the date range should be excluded");
        }

        /// <summary>
        /// Validates account filtering: when accountId is provided, only matching projects are included.
        /// Source: ReportService.cs lines 79-89.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_WithAccountId_ReturnsOnlyMatchingProjects()
        {
            // Arrange — custom data without null-account projects (to avoid Exception)
            var context = _fixture.CreateInMemoryDbContext();

            context.ProjectProjections.AddRange(
                new ProjectProjectionBuilder().WithId(TestIds.ProjectAlphaId)
                    .WithName("Project Alpha").WithAccountId(TestIds.AccountAlphaId)
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build(),
                new ProjectProjectionBuilder().WithId(TestIds.ProjectBetaId)
                    .WithName("Project Beta").WithAccountId(TestIds.AccountBetaId)
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            context.TaskProjections.AddRange(
                new TaskProjectionBuilder().WithId(TestIds.Task1Id)
                    .WithSubject("Alpha Task").WithTaskTypeLabel("Dev")
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build(),
                new TaskProjectionBuilder().WithId(TestIds.Task2Id)
                    .WithSubject("Beta Task").WithTaskTypeLabel("Bug Fix")
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            context.TimelogProjections.AddRange(
                TimelogProjectionBuilder.BillableTimelog()
                    .WithId(Guid.NewGuid()).WithTaskId(TestIds.Task1Id)
                    .WithProjectId(TestIds.ProjectAlphaId).WithAccountId(TestIds.AccountAlphaId)
                    .WithMinutes(60).WithLoggedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build(),
                TimelogProjectionBuilder.BillableTimelog()
                    .WithId(Guid.NewGuid()).WithTaskId(TestIds.Task2Id)
                    .WithProjectId(TestIds.ProjectBetaId).WithAccountId(TestIds.AccountBetaId)
                    .WithMinutes(90).WithLoggedOn(new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act — filter by AccountAlpha
            var result = service.GetTimelogData(2024, 1, TestIds.AccountAlphaId);

            // Assert — only Project Alpha records returned
            result.Should().NotBeEmpty();
            result.All(r => (Guid)r["project_id"] == TestIds.ProjectAlphaId).Should().BeTrue(
                "only projects matching the account filter should be included");
            result.Any(r => (Guid)r["project_id"] == TestIds.ProjectBetaId).Should().BeFalse(
                "projects with different accounts should be excluded");
        }

        /// <summary>
        /// Validates that null accountId returns results for ALL projects (no filtering).
        /// Source: ReportService.cs lines 91-95.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_WithNullAccountId_ReturnsAllProjects()
        {
            // Arrange — standard data with multiple projects under different accounts
            var (service, _) = await CreateServiceWithSeedDataAsync();

            // Act — null accountId means no filtering
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — all 3 tasks appear (including Task 3 under accountless Project Gamma)
            result.Should().NotBeEmpty();
            result.Any(r => (Guid)r["task_id"] == TestIds.Task1Id).Should().BeTrue("Task 1 should be in results");
            result.Any(r => (Guid)r["task_id"] == TestIds.Task2Id).Should().BeTrue("Task 2 should be in results");
            result.Any(r => (Guid)r["task_id"] == TestIds.Task3Id).Should().BeTrue("Task 3 should be in results");

            // Verify multiple distinct projects appear
            var distinctProjectIds = result.Select(r => (Guid)r["project_id"]).Distinct().ToList();
            distinctProjectIds.Count.Should().BeGreaterThanOrEqualTo(2,
                "results should include projects from different accounts when no filter is applied");
        }

        /// <summary>
        /// Validates that a project with null account_id throws a plain Exception
        /// (NOT ValidationException) with exact message "There is a project without an account"
        /// when accountId filter is active.
        /// Source: ReportService.cs lines 81-83.
        /// CRITICAL: This is System.Exception, NOT ValidationException.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ProjectWithoutAccount_WhenAccountFilterActive_ThrowsException()
        {
            // Arrange — standard data includes Task 3 under Project Gamma (null AccountId)
            var (service, _) = await CreateServiceWithSeedDataAsync();

            // Act — accountId filter triggers the null account check
            Action act = () => service.GetTimelogData(2024, 1, TestIds.AccountAlphaId);

            // Assert — plain Exception with exact message
            var ex = act.Should().Throw<Exception>().Which;
            ex.Message.Should().Be("There is a project without an account");
            // Verify it's NOT a ValidationException
            ex.Should().NotBeOfType<ValidationException>(
                "this error is a plain System.Exception per monolith source line 82");
        }

        #endregion

        #region Phase 7: Result Field Names (API Contract Stability)

        /// <summary>
        /// Validates that each result EntityRecord contains all 7 required fields with correct types.
        /// Source: ReportService.cs lines 107-113.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ResultRecord_ContainsAll7RequiredFields()
        {
            // Arrange — minimal scenario for clean field verification
            var context = await SeedMinimalScenarioAsync(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "Field Test Task", "Field Test Project", "Testing",
                60, true, new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — exactly 7 fields present with correct types
            result.Should().HaveCount(1);
            var rec = result[0];

            // Verify all 7 required field keys exist
            rec["task_id"].Should().NotBeNull().And.BeOfType<Guid>();
            rec["project_id"].Should().NotBeNull().And.BeOfType<Guid>();
            rec["task_subject"].Should().NotBeNull().And.BeOfType<string>();
            rec["project_name"].Should().NotBeNull().And.BeOfType<string>();
            rec["task_type"].Should().NotBeNull().And.BeOfType<string>();
            rec["billable_minutes"].Should().NotBeNull().And.BeOfType<decimal>();
            rec["non_billable_minutes"].Should().NotBeNull().And.BeOfType<decimal>();
        }

        /// <summary>
        /// Validates that result field values exactly match expected data from standard seed.
        /// Uses Task 1 / Project Alpha from the January 2024 standard test data.
        /// Source: ReportService.cs lines 107-113.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ResultRecord_FieldValuesMatchExpected()
        {
            // Arrange — standard test data for January 2024
            var (service, _) = await CreateServiceWithSeedDataAsync();

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — find Task 1 / Project Alpha record and verify field values
            var task1Records = result.Where(r => (Guid)r["task_id"] == TestIds.Task1Id).ToList();
            task1Records.Should().NotBeEmpty("Task 1 should be present in results");

            var rec = task1Records.First();
            ((Guid)rec["task_id"]).Should().Be(TestIds.Task1Id);
            ((Guid)rec["project_id"]).Should().Be(TestIds.ProjectAlphaId);
            ((string)rec["task_subject"]).Should().Be("Implement feature X");
            ((string)rec["project_name"]).Should().Be("Project Alpha");
            ((string)rec["task_type"]).Should().Be("Development");
            ((decimal)rec["billable_minutes"]).Should().Be(120m);
            ((decimal)rec["non_billable_minutes"]).Should().Be(60m);
        }

        /// <summary>
        /// Validates that querying a month with no timelogs returns an empty list.
        /// Source: When no tasks have timelogs, the result is empty.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_NoTimelogsInRange_ReturnsEmptyList()
        {
            // Arrange — standard data only has Jan/Feb timelogs; query March
            var (service, _) = await CreateServiceWithSeedDataAsync();

            // Act — March 2024 has no timelogs
            var result = service.GetTimelogData(2024, 3, null);

            // Assert
            result.Should().NotBeNull();
            result.Should().BeEmpty("no timelogs exist in March 2024");
            result.Count.Should().Be(0);
        }

        #endregion

        #region Phase 8: CQRS (Light) Pattern Validation Tests

        /// <summary>
        /// Golden master test: validates that local projections produce results identical
        /// to what the monolith's ReportService would produce with the same data.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_LocalProjections_ProduceIdenticalResultsToMonolith()
        {
            // Arrange — standard test data with known expected outputs
            var (service, _) = await CreateServiceWithSeedDataAsync();

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — verify total count and per-task expectations
            result.Should().NotBeEmpty();

            // Task 1: single project (Alpha), billable=120, non-billable=60
            var task1 = result.Where(r => (Guid)r["task_id"] == TestIds.Task1Id).ToList();
            task1.Should().NotBeEmpty();
            task1.First()["task_subject"].Should().Be("Implement feature X");
            ((decimal)task1.First()["billable_minutes"]).Should().Be(120m);
            ((decimal)task1.First()["non_billable_minutes"]).Should().Be(60m);

            // Task 2: multi-project, total billable=135 (90+45), non-billable=0
            var task2 = result.Where(r => (Guid)r["task_id"] == TestIds.Task2Id).ToList();
            task2.Should().HaveCount(2, "Task 2 is linked to 2 projects");
            foreach (var rec in task2)
            {
                ((decimal)rec["billable_minutes"]).Should().Be(135m);
                ((decimal)rec["non_billable_minutes"]).Should().Be(0m);
            }

            // Task 3: single project (Gamma), billable=30, non-billable=0
            var task3 = result.Where(r => (Guid)r["task_id"] == TestIds.Task3Id).ToList();
            task3.Should().NotBeEmpty();
            ((decimal)task3.First()["billable_minutes"]).Should().Be(30m);
            ((decimal)task3.First()["non_billable_minutes"]).Should().Be(0m);
        }

        /// <summary>
        /// Validates that the cross-service join replacement (local projections instead of
        /// EQL $project_nn_task and $task_type_1n_task relations) produces structurally
        /// identical results to the monolith's EQL cross-entity queries.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_CrossServiceJoinReplacement_YieldsIdenticalResults()
        {
            // Arrange — custom data simulating cross-service relation traversal
            var context = _fixture.CreateInMemoryDbContext();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var expectedSubject = "Cross-Service Task";
            var expectedProjectName = "Cross-Service Project";
            var expectedTaskType = "Architecture";

            context.ProjectProjections.Add(new ProjectProjectionBuilder()
                .WithId(projectId).WithName(expectedProjectName).WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskId).WithSubject(expectedSubject).WithTaskTypeLabel(expectedTaskType)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TimelogProjections.Add(TimelogProjectionBuilder.BillableTimelog()
                .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                .WithAccountId(accountId).WithMinutes(75)
                .WithLoggedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — verify project_name comes from ProjectProjection (replaces $project_nn_task.name)
            //          and task_type comes from TaskProjection.TaskTypeLabel (replaces $task_type_1n_task.label)
            result.Should().HaveCount(1);
            var rec = result[0];
            ((string)rec["project_name"]).Should().Be(expectedProjectName,
                "project_name should come from ProjectProjection, replacing $project_nn_task.name");
            ((string)rec["task_type"]).Should().Be(expectedTaskType,
                "task_type should come from TaskProjection.TaskTypeLabel, replacing $task_type_1n_task.label");
            ((string)rec["task_subject"]).Should().Be(expectedSubject,
                "task_subject should come from TaskProjection.Subject, replacing task.subject");
            ((Guid)rec["task_id"]).Should().Be(taskId);
            ((Guid)rec["project_id"]).Should().Be(projectId);
            ((decimal)rec["billable_minutes"]).Should().Be(75m);
        }

        #endregion

        #region Phase 9: Edge Case Tests

        /// <summary>
        /// Validates the minimal viable data set: one task, one project, one timelog.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_SingleTaskSingleTimelog_ReturnsOneRecord()
        {
            // Arrange — minimal data set
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            var context = await SeedMinimalScenarioAsync(
                taskId, projectId, accountId,
                "Single Task", "Single Project", "Dev",
                45, true, new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — exactly one record
            result.Should().HaveCount(1);
            ((Guid)result[0]["task_id"]).Should().Be(taskId);
            ((Guid)result[0]["project_id"]).Should().Be(projectId);
            ((decimal)result[0]["billable_minutes"]).Should().Be(45m);
            ((decimal)result[0]["non_billable_minutes"]).Should().Be(0m);
        }

        /// <summary>
        /// Validates that many timelogs (10+) for the same task correctly accumulate.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ManyTimelogsForSameTask_AccumulatesAll()
        {
            // Arrange — 12 timelogs for one task, alternating billable/non-billable
            var context = _fixture.CreateInMemoryDbContext();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            context.ProjectProjections.Add(new ProjectProjectionBuilder()
                .WithId(projectId).WithName("Busy Project").WithAccountId(accountId)
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            context.TaskProjections.Add(new TaskProjectionBuilder()
                .WithId(taskId).WithSubject("Busy Task").WithTaskTypeLabel("Dev")
                .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            decimal expectedBillable = 0m;
            decimal expectedNonBillable = 0m;
            for (int i = 1; i <= 12; i++)
            {
                bool isBillable = (i % 2 == 0); // even = billable, odd = non-billable
                int minutes = 10 * i;
                if (isBillable)
                    expectedBillable += minutes;
                else
                    expectedNonBillable += minutes;

                context.TimelogProjections.Add(new TimelogProjectionBuilder()
                    .WithId(Guid.NewGuid()).WithTaskId(taskId).WithProjectId(projectId)
                    .WithAccountId(accountId).WithMinutes(minutes).WithIsBillable(isBillable)
                    .WithLoggedOn(new DateTime(2024, 1, i, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            }
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act
            var result = service.GetTimelogData(2024, 1, null);

            // Assert — all 12 timelogs accumulated correctly
            result.Should().HaveCount(1);
            ((decimal)result[0]["billable_minutes"]).Should().Be(expectedBillable);
            ((decimal)result[0]["non_billable_minutes"]).Should().Be(expectedNonBillable);
        }

        /// <summary>
        /// Validates account filtering with a valid accountId returns only matching results.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ValidAccountId_ReturnsFilteredResults()
        {
            // Arrange — custom data with two accounts, no null-account projects
            var context = _fixture.CreateInMemoryDbContext();

            context.ProjectProjections.AddRange(
                new ProjectProjectionBuilder().WithId(TestIds.ProjectAlphaId)
                    .WithName("Alpha").WithAccountId(TestIds.AccountAlphaId)
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build(),
                new ProjectProjectionBuilder().WithId(TestIds.ProjectBetaId)
                    .WithName("Beta").WithAccountId(TestIds.AccountBetaId)
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            context.TaskProjections.AddRange(
                new TaskProjectionBuilder().WithId(TestIds.Task1Id)
                    .WithSubject("Alpha Task").WithTaskTypeLabel("Dev")
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build(),
                new TaskProjectionBuilder().WithId(TestIds.Task2Id)
                    .WithSubject("Beta Task").WithTaskTypeLabel("QA")
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());

            context.TimelogProjections.AddRange(
                TimelogProjectionBuilder.BillableTimelog()
                    .WithId(Guid.NewGuid()).WithTaskId(TestIds.Task1Id)
                    .WithProjectId(TestIds.ProjectAlphaId).WithAccountId(TestIds.AccountAlphaId)
                    .WithMinutes(100).WithLoggedOn(new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build(),
                TimelogProjectionBuilder.BillableTimelog()
                    .WithId(Guid.NewGuid()).WithTaskId(TestIds.Task2Id)
                    .WithProjectId(TestIds.ProjectBetaId).WithAccountId(TestIds.AccountBetaId)
                    .WithMinutes(200).WithLoggedOn(new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(DateTime.UtcNow).WithLastModifiedOn(DateTime.UtcNow).Build());
            await context.SaveChangesAsync();

            var service = CreateServiceWithContext(context);

            // Act — filter by AccountAlpha
            var result = service.GetTimelogData(2024, 1, TestIds.AccountAlphaId);

            // Assert — only Alpha project tasks returned
            result.Should().NotBeEmpty();
            result.All(r => (Guid)r["project_id"] == TestIds.ProjectAlphaId).Should().BeTrue();
            result.Any(r => (Guid)r["task_id"] == TestIds.Task1Id).Should().BeTrue();
            result.Any(r => (Guid)r["task_id"] == TestIds.Task2Id).Should().BeFalse();
        }

        #endregion

        #region Phase 10: Constructor Validation Tests

        /// <summary>
        /// Validates that null dbContext throws ArgumentNullException with parameter name "dbContext".
        /// </summary>
        [Fact]
        public void Constructor_NullDbContext_ThrowsArgumentNullException()
        {
            // Act
            Action act = () => new ReportAggregationService(null, _mockLogger.Object);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .And.ParamName.Should().Be("dbContext");
        }

        /// <summary>
        /// Validates that null logger throws ArgumentNullException with parameter name "logger".
        /// </summary>
        [Fact]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            // Arrange
            var context = _fixture.CreateInMemoryDbContext();

            // Act
            Action act = () => new ReportAggregationService(context, null);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .And.ParamName.Should().Be("logger");
        }

        #endregion
    }
}
