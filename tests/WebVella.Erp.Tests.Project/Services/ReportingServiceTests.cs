using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Moq;
using FluentAssertions;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using WebVella.Erp.Service.Project.Domain.Services;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.Tests.Project.Services
{
    /// <summary>
    /// Testable subclass of <see cref="ReportingService"/> that intercepts EQL command
    /// execution to allow isolated unit testing of business logic without database access.
    /// Overrides the protected virtual <c>ExecuteEql</c> method to return controlled test
    /// data, and captures every EQL call for parameter verification assertions.
    /// </summary>
    internal sealed class TestableReportingService : ReportingService
    {
        private readonly Func<string, List<EqlParameter>, EntityRecordList> _eqlHandler;

        /// <summary>
        /// Ordered list of every EQL call made during <c>GetTimelogData</c> execution.
        /// Each entry contains the EQL text and the parameter list, enabling assertions
        /// on query construction (date range, scope, entity selection).
        /// </summary>
        public List<(string Eql, List<EqlParameter> Parameters)> CapturedEqlCalls { get; }
            = new List<(string, List<EqlParameter>)>();

        /// <summary>
        /// Creates a testable reporting service with a custom EQL execution handler.
        /// </summary>
        /// <param name="logger">Mock logger for the service.</param>
        /// <param name="crmServiceClient">Mock CRM service client for account validation.</param>
        /// <param name="eqlHandler">
        /// Delegate that receives the EQL text and parameters, and returns the controlled
        /// <see cref="EntityRecordList"/> result. The handler is responsible for routing
        /// based on query content (e.g., "FROM timelog" vs "FROM task").
        /// </param>
        public TestableReportingService(
            ILogger<ReportingService> logger,
            ICrmServiceClient crmServiceClient,
            Func<string, List<EqlParameter>, EntityRecordList> eqlHandler)
            : base(logger, crmServiceClient)
        {
            _eqlHandler = eqlHandler ?? throw new ArgumentNullException(nameof(eqlHandler));
        }

        /// <summary>
        /// Intercepts EQL execution: captures the call for later assertion and
        /// delegates to the test-supplied handler instead of hitting the database.
        /// </summary>
        protected override EntityRecordList ExecuteEql(string eql, List<EqlParameter> parameters)
        {
            CapturedEqlCalls.Add((eql, new List<EqlParameter>(parameters)));
            return _eqlHandler(eql, parameters);
        }
    }

    /// <summary>
    /// Comprehensive unit tests for <see cref="ReportingService.GetTimelogData"/> covering
    /// all business rules extracted from the monolith's <c>ReportService</c> (137 lines).
    ///
    /// Tests validate:
    /// <list type="bullet">
    ///   <item>Input parameter validation (month 1–12, year &gt; 0, account existence)</item>
    ///   <item>Timelog loading with correct date range and scope parameters</item>
    ///   <item>Task ID extraction from <c>l_related_records</c> JSON</item>
    ///   <item>Task filtering by timelog existence and project association</item>
    ///   <item>Account-based project filtering</item>
    ///   <item>Multi-project task splitting</item>
    ///   <item>Billable/non-billable minute aggregation</item>
    ///   <item>Complete end-to-end report generation</item>
    /// </list>
    ///
    /// Per AAP 0.8.1: every business rule maps to at least one automated test.
    /// Per AAP 0.8.2: all business logic classes must have ≥80% code coverage.
    /// </summary>
    public class ReportingServiceTests
    {
        private readonly Mock<ILogger<ReportingService>> _mockLogger;
        private readonly Mock<ICrmServiceClient> _mockCrmClient;

        /// <summary>
        /// Initializes fresh mock dependencies for each test method.
        /// xUnit creates a new test class instance per test, ensuring isolation.
        /// </summary>
        public ReportingServiceTests()
        {
            _mockLogger = new Mock<ILogger<ReportingService>>();
            _mockCrmClient = new Mock<ICrmServiceClient>();
        }

        #region Helper Methods — Service Factory

        /// <summary>
        /// Creates a <see cref="TestableReportingService"/> with the given EQL handler
        /// and CRM client account-existence behavior. When no EQL handler is provided,
        /// returns empty <see cref="EntityRecordList"/> for all queries.
        /// </summary>
        private TestableReportingService CreateService(
            Func<string, List<EqlParameter>, EntityRecordList> eqlHandler = null,
            bool accountExists = true)
        {
            _mockCrmClient
                .Setup(c => c.AccountExistsAsync(It.IsAny<Guid>()))
                .ReturnsAsync(accountExists);

            return new TestableReportingService(
                _mockLogger.Object,
                _mockCrmClient.Object,
                eqlHandler ?? ((eql, p) => new EntityRecordList()));
        }

        /// <summary>
        /// Creates a <see cref="TestableReportingService"/> with pre-built timelog and task
        /// result sets. Routes EQL calls based on the FROM clause entity name.
        /// </summary>
        private TestableReportingService CreateServiceWithData(
            EntityRecordList timelogRecords,
            EntityRecordList taskRecords,
            bool accountExists = true)
        {
            return CreateService(
                eqlHandler: (eql, parameters) =>
                {
                    if (eql.IndexOf("FROM timelog", StringComparison.OrdinalIgnoreCase) >= 0)
                        return timelogRecords ?? new EntityRecordList();
                    if (eql.IndexOf("FROM task", StringComparison.OrdinalIgnoreCase) >= 0)
                        return taskRecords ?? new EntityRecordList();
                    return new EntityRecordList();
                },
                accountExists: accountExists);
        }

        #endregion

        #region Helper Methods — Test Data Factories

        /// <summary>
        /// Creates a mock timelog <see cref="EntityRecord"/> with the specified task reference,
        /// minutes, and billable flag. The <c>l_related_records</c> field is JSON-serialized
        /// with <paramref name="taskId"/> as the first (and only) element, matching the
        /// monolith's EQL result format for timelog records.
        /// </summary>
        private static EntityRecord CreateTimelog(Guid taskId, decimal minutes, bool isBillable, Guid? id = null)
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["is_billable"] = isBillable;
            record["minutes"] = minutes;
            record["l_related_records"] = JsonConvert.SerializeObject(new List<Guid> { taskId });
            return record;
        }

        /// <summary>
        /// Creates a mock timelog <see cref="EntityRecord"/> with multiple related record GUIDs.
        /// Used to verify that only <c>ids[0]</c> (the first element) is used as the task ID.
        /// </summary>
        private static EntityRecord CreateTimelogWithMultipleRelated(
            List<Guid> relatedIds, decimal minutes, bool isBillable, Guid? id = null)
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["is_billable"] = isBillable;
            record["minutes"] = minutes;
            record["l_related_records"] = JsonConvert.SerializeObject(relatedIds);
            return record;
        }

        /// <summary>
        /// Creates a mock task <see cref="EntityRecord"/> with project and task-type relation
        /// data matching the EQL query shape:
        /// <c>SELECT id, subject, $project_nn_task.id, $project_nn_task.name,
        /// $project_nn_task.account_id, $task_type_1n_task.label FROM task</c>.
        /// </summary>
        private static EntityRecord CreateTask(
            Guid taskId, string subject,
            List<EntityRecord> projects,
            List<EntityRecord> taskTypes)
        {
            var record = new EntityRecord();
            record["id"] = taskId;
            record["subject"] = subject;
            record["$project_nn_task"] = projects ?? new List<EntityRecord>();
            record["$task_type_1n_task"] = taskTypes ?? new List<EntityRecord>();
            return record;
        }

        /// <summary>
        /// Creates a mock project <see cref="EntityRecord"/> representing a
        /// <c>$project_nn_task</c> relation record with id, name, and account_id fields.
        /// </summary>
        private static EntityRecord CreateProject(Guid projectId, string name, Guid? accountId = null)
        {
            var record = new EntityRecord();
            record["id"] = projectId;
            record["name"] = name;
            record["account_id"] = accountId.HasValue ? (object)accountId.Value : null;
            return record;
        }

        /// <summary>
        /// Creates a mock task-type <see cref="EntityRecord"/> representing a
        /// <c>$task_type_1n_task</c> relation record with a label field.
        /// </summary>
        private static EntityRecord CreateTaskType(string label)
        {
            var record = new EntityRecord();
            record["label"] = label;
            return record;
        }

        #endregion

        // =====================================================================
        // VALIDATION TESTS — Parameter validation (source lines 15-32)
        // =====================================================================

        #region Validation Tests

        /// <summary>
        /// Business rule: month must be &lt;= 12 (source line 17: <c>month &gt; 12</c>).
        /// Verifies that month = 13 triggers <see cref="ValidationException"/> with
        /// error key "month" and message "Invalid month.".
        /// </summary>
        [Fact]
        public async Task GetTimelogData_WithInvalidMonth_Over12_ShouldThrowValidationException()
        {
            var service = CreateService();

            Func<Task> act = () => service.GetTimelogData(2024, 13, null);

            var exAssertions = await act.Should().ThrowAsync<ValidationException>();
            exAssertions.Which.Errors.Should().Contain(e =>
                e.PropertyName == "month" && e.Message == "Invalid month.");
        }

        /// <summary>
        /// Business rule: month must be &gt; 0 (source line 17: <c>month &lt;= 0</c>).
        /// Verifies that month = 0 triggers <see cref="ValidationException"/>.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_WithInvalidMonth_Zero_ShouldThrowValidationException()
        {
            var service = CreateService();

            Func<Task> act = () => service.GetTimelogData(2024, 0, null);

            var exAssertions = await act.Should().ThrowAsync<ValidationException>();
            exAssertions.Which.Errors.Should().Contain(e =>
                e.PropertyName == "month" && e.Message == "Invalid month.");
        }

        /// <summary>
        /// Business rule: month must be &gt; 0 (source line 17: <c>month &lt;= 0</c>).
        /// Verifies that negative month (-1) triggers <see cref="ValidationException"/>.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_WithNegativeMonth_ShouldThrowValidationException()
        {
            var service = CreateService();

            Func<Task> act = () => service.GetTimelogData(2024, -1, null);

            var exAssertions = await act.Should().ThrowAsync<ValidationException>();
            exAssertions.Which.Errors.Should().Contain(e =>
                e.PropertyName == "month" && e.Message == "Invalid month.");
        }

        /// <summary>
        /// Business rule: year must be &gt; 0 (source line 20: <c>year &lt;= 0</c>).
        /// Verifies that year = 0 triggers <see cref="ValidationException"/> with
        /// error key "year" and message "Invalid year.".
        /// </summary>
        [Fact]
        public async Task GetTimelogData_WithInvalidYear_ShouldThrowValidationException()
        {
            var service = CreateService();

            Func<Task> act = () => service.GetTimelogData(0, 6, null);

            var exAssertions = await act.Should().ThrowAsync<ValidationException>();
            exAssertions.Which.Errors.Should().Contain(e =>
                e.PropertyName == "year" && e.Message == "Invalid year.");
        }

        /// <summary>
        /// Business rule: when accountId is provided, it must exist in CRM
        /// (source lines 24-30, refactored to ICrmServiceClient).
        /// Verifies that a non-existent account triggers <see cref="ValidationException"/>
        /// with error key "accountid" (lowercased by <see cref="ValidationError"/>) and
        /// message format <c>$"Account with ID:{accountId} not found."</c>.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_WithNonExistentAccountId_ShouldThrowValidationException()
        {
            var accountId = Guid.NewGuid();
            var service = CreateService(accountExists: false);

            Func<Task> act = () => service.GetTimelogData(2024, 6, accountId);

            var exAssertions = await act.Should().ThrowAsync<ValidationException>();
            exAssertions.Which.Errors.Should().Contain(e =>
                e.PropertyName == "accountid" &&
                e.Message == $"Account with ID:{accountId} not found.");
        }

        /// <summary>
        /// Business rule: <c>CheckAndThrow()</c> aggregates all validation errors before
        /// throwing (source line 32). Verifies that month=13 and year=0 produce a single
        /// <see cref="ValidationException"/> containing at least two errors (month + year).
        /// </summary>
        [Fact]
        public async Task GetTimelogData_WithMultipleValidationErrors_ShouldAggregateErrors()
        {
            var service = CreateService();

            Func<Task> act = () => service.GetTimelogData(0, 13, null);

            var exAssertions = await act.Should().ThrowAsync<ValidationException>();
            exAssertions.Which.Errors.Should().HaveCountGreaterThanOrEqualTo(2);
            exAssertions.Which.Errors.Should().Contain(e => e.PropertyName == "month");
            exAssertions.Which.Errors.Should().Contain(e => e.PropertyName == "year");
        }

        #endregion

        // =====================================================================
        // TIMELOG LOADING AND PROCESSING TESTS (source lines 34-56)
        // =====================================================================

        #region Timelog Loading Tests

        /// <summary>
        /// Business rule: timelog query date range uses first and last day of the given
        /// month (source lines 35-36). Scope parameter must be "projects" (source line 40).
        /// Verifies EQL parameters: from_date, to_date, and scope.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ShouldQueryTimelogsForCorrectDateRange()
        {
            var service = CreateService();

            await service.GetTimelogData(2024, 3, null);

            // Find the timelog EQL call in captured calls
            var timelogCall = service.CapturedEqlCalls
                .FirstOrDefault(c => c.Eql.IndexOf("FROM timelog", StringComparison.OrdinalIgnoreCase) >= 0);
            timelogCall.Eql.Should().NotBeNull("a timelog EQL query should have been executed");

            // Verify from_date parameter = first day of March 2024
            var fromDateParam = timelogCall.Parameters
                .FirstOrDefault(p => p.ParameterName == "@from_date");
            fromDateParam.Should().NotBeNull();
            fromDateParam.Value.Should().Be(new DateTime(2024, 3, 1));

            // Verify to_date parameter = last day of March 2024
            var toDateParam = timelogCall.Parameters
                .FirstOrDefault(p => p.ParameterName == "@to_date");
            toDateParam.Should().NotBeNull();
            toDateParam.Value.Should().Be(new DateTime(2024, 3, 31));

            // Verify scope parameter = "projects"
            var scopeParam = timelogCall.Parameters
                .FirstOrDefault(p => p.ParameterName == "@scope");
            scopeParam.Should().NotBeNull();
            ((string)scopeParam.Value).Should().Be("projects");
        }

        /// <summary>
        /// Business rule: task_id is the FIRST GUID in the l_related_records JSON array
        /// (source line 52: <c>Guid taskId = ids[0]</c>). Verifies that when a timelog
        /// has multiple related record GUIDs, only the first is used as the task reference.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ShouldDeserializeTaskIdFromRelatedRecords()
        {
            var taskId = Guid.NewGuid();
            var extraId = Guid.NewGuid();
            var projectId = Guid.NewGuid();

            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelogWithMultipleRelated(
                new List<Guid> { taskId, extraId }, 30m, true));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(taskId, "Test Task",
                new List<EntityRecord> { CreateProject(projectId, "Project A") },
                new List<EntityRecord> { CreateTaskType("Development") }));

            var service = CreateServiceWithData(timelogs, tasks);

            var result = await service.GetTimelogData(2024, 6, null);

            result.Should().HaveCount(1);
            ((Guid)result[0]["task_id"]).Should().Be(taskId,
                "the first GUID in l_related_records should be used as task_id");
        }

        /// <summary>
        /// Business rule: duplicate task IDs from multiple timelogs are deduplicated
        /// into a unique HashSet (source lines 48-56: <c>setOfTasksWithTimelog</c>).
        /// Verifies that 3 timelogs referencing 2 unique tasks produce 2 result records
        /// (one per task-project combination).
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ShouldBuildUniqueTaskSet()
        {
            var task1Id = Guid.NewGuid();
            var task2Id = Guid.NewGuid();
            var projectId = Guid.NewGuid();

            // 3 timelogs: 2 for task1, 1 for task2
            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(task1Id, 15m, true));
            timelogs.Add(CreateTimelog(task1Id, 30m, true));
            timelogs.Add(CreateTimelog(task2Id, 20m, false));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(task1Id, "Task 1",
                new List<EntityRecord> { CreateProject(projectId, "Project A") },
                new List<EntityRecord> { CreateTaskType("Development") }));
            tasks.Add(CreateTask(task2Id, "Task 2",
                new List<EntityRecord> { CreateProject(projectId, "Project A") },
                new List<EntityRecord> { CreateTaskType("Testing") }));

            var service = CreateServiceWithData(timelogs, tasks);

            var result = await service.GetTimelogData(2024, 6, null);

            // 2 unique tasks, each with 1 project → 2 result records
            result.Should().HaveCount(2);
        }

        #endregion

        // =====================================================================
        // TASK LOADING AND FILTERING TESTS (source lines 58-99)
        // =====================================================================

        #region Task Filtering Tests

        /// <summary>
        /// Business rule: tasks without timelog records in the specified period are
        /// excluded (source lines 67-69: <c>if (!setOfTasksWithTimelog.Contains(...))</c>).
        /// Verifies that only the task WITH timelogs appears in the result.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ShouldSkipTasksWithNoTimelogs()
        {
            var taskWithTimelogId = Guid.NewGuid();
            var taskWithoutTimelogId = Guid.NewGuid();
            var projectId = Guid.NewGuid();

            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(taskWithTimelogId, 30m, true));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(taskWithTimelogId, "Has Timelogs",
                new List<EntityRecord> { CreateProject(projectId, "Project A") },
                new List<EntityRecord> { CreateTaskType("Development") }));
            tasks.Add(CreateTask(taskWithoutTimelogId, "No Timelogs",
                new List<EntityRecord> { CreateProject(projectId, "Project A") },
                new List<EntityRecord> { CreateTaskType("Testing") }));

            var service = CreateServiceWithData(timelogs, tasks);

            var result = await service.GetTimelogData(2024, 6, null);

            result.Should().HaveCount(1);
            result[0]["task_subject"].Should().Be("Has Timelogs");
        }

        /// <summary>
        /// Business rule: tasks without project associations are excluded
        /// (source lines 72-74: <c>if (taskProjects.Count == 0) continue</c>).
        /// Verifies that a task with an empty $project_nn_task list is skipped.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ShouldSkipTasksWithNoProjects()
        {
            var taskWithProjectId = Guid.NewGuid();
            var taskWithoutProjectId = Guid.NewGuid();
            var projectId = Guid.NewGuid();

            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(taskWithProjectId, 30m, true));
            timelogs.Add(CreateTimelog(taskWithoutProjectId, 20m, true));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(taskWithProjectId, "Has Projects",
                new List<EntityRecord> { CreateProject(projectId, "Project A") },
                new List<EntityRecord> { CreateTaskType("Development") }));
            // Empty project list for this task
            tasks.Add(CreateTask(taskWithoutProjectId, "No Projects",
                new List<EntityRecord>(),
                new List<EntityRecord> { CreateTaskType("Testing") }));

            var service = CreateServiceWithData(timelogs, tasks);

            var result = await service.GetTimelogData(2024, 6, null);

            result.Should().HaveCount(1);
            result[0]["task_subject"].Should().Be("Has Projects");
        }

        /// <summary>
        /// Business rule: when accountId is specified, only tasks from projects matching
        /// that account are included (source lines 79-89: <c>if ((Guid)project["account_id"]
        /// == accountId)</c>). Verifies that non-matching projects are filtered out.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_WithAccountId_ShouldFilterByProjectAccount()
        {
            var targetAccountId = Guid.NewGuid();
            var otherAccountId = Guid.NewGuid();
            var task1Id = Guid.NewGuid();
            var task2Id = Guid.NewGuid();
            var project1Id = Guid.NewGuid();
            var project2Id = Guid.NewGuid();

            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(task1Id, 30m, true));
            timelogs.Add(CreateTimelog(task2Id, 20m, true));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(task1Id, "Matching Account Task",
                new List<EntityRecord> { CreateProject(project1Id, "Project A", targetAccountId) },
                new List<EntityRecord> { CreateTaskType("Development") }));
            tasks.Add(CreateTask(task2Id, "Other Account Task",
                new List<EntityRecord> { CreateProject(project2Id, "Project B", otherAccountId) },
                new List<EntityRecord> { CreateTaskType("Testing") }));

            var service = CreateServiceWithData(timelogs, tasks, accountExists: true);

            var result = await service.GetTimelogData(2024, 6, targetAccountId);

            result.Should().HaveCount(1);
            result[0]["task_subject"].Should().Be("Matching Account Task");
            ((Guid)result[0]["project_id"]).Should().Be(project1Id);
        }

        /// <summary>
        /// Business rule: when accountId filter is active and a project has null account_id,
        /// a generic <see cref="Exception"/> is thrown (source lines 81-83:
        /// <c>throw new Exception("There is a project without an account")</c>).
        /// </summary>
        [Fact]
        public async Task GetTimelogData_WithAccountId_WhenProjectHasNullAccount_ShouldThrowException()
        {
            var targetAccountId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();

            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(taskId, 30m, true));

            var tasks = new EntityRecordList();
            // Project with null account_id
            tasks.Add(CreateTask(taskId, "Task With Null Account Project",
                new List<EntityRecord> { CreateProject(projectId, "Project A", null) },
                new List<EntityRecord> { CreateTaskType("Development") }));

            var service = CreateServiceWithData(timelogs, tasks, accountExists: true);

            Func<Task> act = () => service.GetTimelogData(2024, 6, targetAccountId);

            await act.Should().ThrowAsync<Exception>()
                .WithMessage("There is a project without an account");
        }

        /// <summary>
        /// Business rule: when no accountId filter is provided, all tasks with projects
        /// are included regardless of project account (source lines 91-95: else branch).
        /// Verifies that tasks from projects with different accounts are all in the result.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_WithNoAccountFilter_ShouldIncludeAllProjects()
        {
            var account1Id = Guid.NewGuid();
            var account2Id = Guid.NewGuid();
            var task1Id = Guid.NewGuid();
            var task2Id = Guid.NewGuid();
            var project1Id = Guid.NewGuid();
            var project2Id = Guid.NewGuid();

            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(task1Id, 30m, true));
            timelogs.Add(CreateTimelog(task2Id, 20m, true));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(task1Id, "Task 1",
                new List<EntityRecord> { CreateProject(project1Id, "Project A", account1Id) },
                new List<EntityRecord> { CreateTaskType("Development") }));
            tasks.Add(CreateTask(task2Id, "Task 2",
                new List<EntityRecord> { CreateProject(project2Id, "Project B", account2Id) },
                new List<EntityRecord> { CreateTaskType("Testing") }));

            var service = CreateServiceWithData(timelogs, tasks);

            var result = await service.GetTimelogData(2024, 6, null);

            result.Should().HaveCount(2);
        }

        /// <summary>
        /// Business rule: if a task belongs to multiple projects, it appears once per project
        /// in the result (source lines 77-96: <c>foreach (var project in taskProjects)</c>).
        /// Verifies that a task linked to 2 projects produces 2 result records.
        /// NOTE: Due to reference sharing in the source code (task object is added to
        /// processedTasks multiple times), both records reference the same task instance
        /// with the last project assigned.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ShouldSplitTasksAcrossMultipleProjects()
        {
            var taskId = Guid.NewGuid();
            var project1Id = Guid.NewGuid();
            var project2Id = Guid.NewGuid();

            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(taskId, 30m, true));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(taskId, "Multi-Project Task",
                new List<EntityRecord>
                {
                    CreateProject(project1Id, "Project A"),
                    CreateProject(project2Id, "Project B")
                },
                new List<EntityRecord> { CreateTaskType("Development") }));

            var service = CreateServiceWithData(timelogs, tasks);

            var result = await service.GetTimelogData(2024, 6, null);

            // Task appears twice in the result due to split across 2 projects
            result.Should().HaveCount(2);
            // Both records reference the same task
            result.All(r => (Guid)r["task_id"] == taskId).Should().BeTrue(
                "both result records should reference the same task");
        }

        #endregion

        // =====================================================================
        // AGGREGATION TESTS (source lines 103-133)
        // =====================================================================

        #region Aggregation Tests

        /// <summary>
        /// Business rule: billable timelog minutes are summed per task
        /// (source lines 125-126: <c>if (is_billable) billable_minutes += minutes</c>).
        /// Verifies that two billable timelogs (15 + 30) produce billable_minutes = 45.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ShouldAggregateBillableMinutesPerTask()
        {
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();

            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(taskId, 15m, true));
            timelogs.Add(CreateTimelog(taskId, 30m, true));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(taskId, "Billable Task",
                new List<EntityRecord> { CreateProject(projectId, "Project A") },
                new List<EntityRecord> { CreateTaskType("Development") }));

            var service = CreateServiceWithData(timelogs, tasks);

            var result = await service.GetTimelogData(2024, 6, null);

            result.Should().HaveCount(1);
            ((decimal)result[0]["billable_minutes"]).Should().Be(45m);
        }

        /// <summary>
        /// Business rule: non-billable timelog minutes are summed per task
        /// (source lines 127-128: <c>else non_billable_minutes += minutes</c>).
        /// Verifies that two non-billable timelogs (10 + 25) produce non_billable_minutes = 35.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ShouldAggregateNonBillableMinutesPerTask()
        {
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();

            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(taskId, 10m, false));
            timelogs.Add(CreateTimelog(taskId, 25m, false));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(taskId, "Non-Billable Task",
                new List<EntityRecord> { CreateProject(projectId, "Project A") },
                new List<EntityRecord> { CreateTaskType("Review") }));

            var service = CreateServiceWithData(timelogs, tasks);

            var result = await service.GetTimelogData(2024, 6, null);

            result.Should().HaveCount(1);
            ((decimal)result[0]["non_billable_minutes"]).Should().Be(35m);
        }

        /// <summary>
        /// Business rule: result record initializes both minute fields to (decimal)0
        /// (source lines 112-113). Verifies that when a task has only billable timelogs,
        /// <c>non_billable_minutes</c> remains at the initialized value of (decimal)0,
        /// and vice versa.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ShouldInitializeMinutesToZero()
        {
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();

            // Only one billable timelog — non_billable_minutes should remain at 0
            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(taskId, 20m, true));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(taskId, "Task With Only Billable",
                new List<EntityRecord> { CreateProject(projectId, "Project A") },
                new List<EntityRecord> { CreateTaskType("Development") }));

            var service = CreateServiceWithData(timelogs, tasks);

            var result = await service.GetTimelogData(2024, 6, null);

            result.Should().HaveCount(1);
            // billable_minutes should be 20 from the one timelog
            ((decimal)result[0]["billable_minutes"]).Should().Be(20m);
            // non_billable_minutes should remain at the initialized value of (decimal)0
            ((decimal)result[0]["non_billable_minutes"]).Should().Be(0m);
        }

        /// <summary>
        /// Business rule: each result record contains all required output fields
        /// (source lines 107-113): task_id, project_id, task_subject, project_name,
        /// task_type, billable_minutes, non_billable_minutes.
        /// Verifies every field is present and has the correct type and value.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ResultRecord_ShouldContainAllRequiredFields()
        {
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();

            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(taskId, 15m, true));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(taskId, "Test Subject",
                new List<EntityRecord> { CreateProject(projectId, "Test Project") },
                new List<EntityRecord> { CreateTaskType("Bug Fix") }));

            var service = CreateServiceWithData(timelogs, tasks);

            var result = await service.GetTimelogData(2024, 6, null);

            result.Should().HaveCount(1);
            var rec = result[0];

            // Verify all 7 required fields are present and correctly typed
            rec["task_id"].Should().NotBeNull();
            ((Guid)rec["task_id"]).Should().Be(taskId);

            rec["project_id"].Should().NotBeNull();
            ((Guid)rec["project_id"]).Should().Be(projectId);

            rec["task_subject"].Should().NotBeNull();
            ((string)rec["task_subject"]).Should().Be("Test Subject");

            rec["project_name"].Should().NotBeNull();
            ((string)rec["project_name"]).Should().Be("Test Project");

            rec["task_type"].Should().NotBeNull();
            ((string)rec["task_type"]).Should().Be("Bug Fix");

            rec["billable_minutes"].Should().NotBeNull();
            ((decimal)rec["billable_minutes"]).Should().BeOfType(typeof(decimal));

            rec["non_billable_minutes"].Should().NotBeNull();
            ((decimal)rec["non_billable_minutes"]).Should().BeOfType(typeof(decimal));
        }

        /// <summary>
        /// Business rule: each unique task-project combination produces exactly one
        /// result record (source lines 103-132 result loop). Verifies that 2 tasks each
        /// linked to 1 project produce exactly 2 result records.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_ShouldReturnOneRecordPerTaskProjectCombination()
        {
            var task1Id = Guid.NewGuid();
            var task2Id = Guid.NewGuid();
            var project1Id = Guid.NewGuid();
            var project2Id = Guid.NewGuid();

            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(task1Id, 10m, true));
            timelogs.Add(CreateTimelog(task2Id, 20m, false));

            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(task1Id, "Task 1",
                new List<EntityRecord> { CreateProject(project1Id, "Project A") },
                new List<EntityRecord> { CreateTaskType("Development") }));
            tasks.Add(CreateTask(task2Id, "Task 2",
                new List<EntityRecord> { CreateProject(project2Id, "Project B") },
                new List<EntityRecord> { CreateTaskType("Testing") }));

            var service = CreateServiceWithData(timelogs, tasks);

            var result = await service.GetTimelogData(2024, 6, null);

            result.Should().HaveCount(2);
            result.Select(r => (Guid)r["task_id"]).Distinct().Should().HaveCount(2,
                "each task-project combination should produce exactly one record");
        }

        #endregion

        // =====================================================================
        // END-TO-END INTEGRATION-STYLE TEST
        // =====================================================================

        #region End-to-End Test

        /// <summary>
        /// Golden path test exercising the complete GetTimelogData flow:
        /// - 2 tasks linked to projects matching a specific account
        /// - 3 timelogs: 2 billable for task1 (15 + 30 min), 1 non-billable for task2 (45 min)
        /// - Account filter to include only matching projects
        /// - 1 task linked to a non-matching account (should be filtered out)
        ///
        /// Verifies complete report output: correct record count, field values,
        /// and billable/non-billable aggregation per task.
        /// </summary>
        [Fact]
        public async Task GetTimelogData_EndToEnd_ShouldProduceCorrectReport()
        {
            // Arrange: IDs
            var targetAccountId = Guid.NewGuid();
            var otherAccountId = Guid.NewGuid();
            var task1Id = Guid.NewGuid();
            var task2Id = Guid.NewGuid();
            var task3Id = Guid.NewGuid();
            var project1Id = Guid.NewGuid();
            var project2Id = Guid.NewGuid();
            var project3Id = Guid.NewGuid();

            // Arrange: Timelogs
            var timelogs = new EntityRecordList();
            timelogs.Add(CreateTimelog(task1Id, 15m, true));    // billable for task1
            timelogs.Add(CreateTimelog(task1Id, 30m, true));    // billable for task1
            timelogs.Add(CreateTimelog(task2Id, 45m, false));   // non-billable for task2
            timelogs.Add(CreateTimelog(task3Id, 60m, true));    // billable for task3 (filtered by account)

            // Arrange: Tasks with projects
            var tasks = new EntityRecordList();
            tasks.Add(CreateTask(task1Id, "Design Feature",
                new List<EntityRecord> { CreateProject(project1Id, "Alpha Project", targetAccountId) },
                new List<EntityRecord> { CreateTaskType("Feature") }));
            tasks.Add(CreateTask(task2Id, "Fix Bug",
                new List<EntityRecord> { CreateProject(project2Id, "Beta Project", targetAccountId) },
                new List<EntityRecord> { CreateTaskType("Bug") }));
            tasks.Add(CreateTask(task3Id, "Research Spike",
                new List<EntityRecord> { CreateProject(project3Id, "Gamma Project", otherAccountId) },
                new List<EntityRecord> { CreateTaskType("Spike") }));

            var service = CreateServiceWithData(timelogs, tasks, accountExists: true);

            // Act
            var result = await service.GetTimelogData(2024, 6, targetAccountId);

            // Assert: Only 2 tasks match the target account (task3 filtered out)
            result.Should().HaveCount(2);

            // Assert: Task 1 — "Design Feature" with 45 billable minutes (15+30), 0 non-billable
            var task1Record = result.FirstOrDefault(r => (Guid)r["task_id"] == task1Id);
            task1Record.Should().NotBeNull();
            task1Record["task_subject"].Should().Be("Design Feature");
            task1Record["project_name"].Should().Be("Alpha Project");
            ((Guid)task1Record["project_id"]).Should().Be(project1Id);
            ((string)task1Record["task_type"]).Should().Be("Feature");
            ((decimal)task1Record["billable_minutes"]).Should().Be(45m);
            ((decimal)task1Record["non_billable_minutes"]).Should().Be(0m);

            // Assert: Task 2 — "Fix Bug" with 0 billable minutes, 45 non-billable
            var task2Record = result.FirstOrDefault(r => (Guid)r["task_id"] == task2Id);
            task2Record.Should().NotBeNull();
            task2Record["task_subject"].Should().Be("Fix Bug");
            task2Record["project_name"].Should().Be("Beta Project");
            ((Guid)task2Record["project_id"]).Should().Be(project2Id);
            ((string)task2Record["task_type"]).Should().Be("Bug");
            ((decimal)task2Record["billable_minutes"]).Should().Be(0m);
            ((decimal)task2Record["non_billable_minutes"]).Should().Be(45m);

            // Assert: Task 3 — should NOT be in the result (filtered by account)
            result.Any(r => (Guid)r["task_id"] == task3Id).Should().BeFalse(
                "task3 belongs to a different account and should be filtered out");
        }

        #endregion
    }
}
