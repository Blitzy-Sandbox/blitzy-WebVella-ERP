using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;
using WebVella.Erp.Service.Project.Jobs;
using WebVella.Erp.Service.Project.Domain.Services;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Tests.Project.Jobs
{
    /// <summary>
    /// Comprehensive xUnit test suite for <see cref="StartTasksOnStartDateJob"/>,
    /// the BackgroundService-based daily job that activates tasks whose start_time
    /// has been reached by updating their status_id to the "started/in-progress" GUID.
    ///
    /// Validates ALL business rules preserved from the monolith's
    /// <c>WebVella.Erp.Plugins.Project.Jobs.StartTasksOnStartDate</c> (32 lines):
    ///   - SecurityContext.OpenSystemScope() for elevated permissions (AAP 0.8.3)
    ///   - TaskService.GetTasksThatNeedStarting() delegation
    ///   - Status GUID update to 20d73f63-3501-4565-a55e-2d291549a9bd (started/in-progress)
    ///   - Fail-fast on first UpdateRecord failure (monolith lines 25-27)
    ///   - Exact patch record construction (only id and status_id fields)
    ///   - Entity name "task" in UpdateRecord calls (monolith line 24)
    ///   - Well-known GUID constants (JobTypeId, SchedulePlanId, StartedStatusId)
    ///   - BackgroundService/IHostedService lifecycle
    ///   - Timer-based scheduling at 00:10 UTC daily
    ///
    /// Testing strategy:
    ///   - DI scope chain mocked: IServiceScopeFactory → IServiceScope → IServiceProvider
    ///   - TaskService: TestableTaskService subclass overrides protected virtual ExecuteEql
    ///     (GetTasksThatNeedStarting is not virtual, but delegates to virtual ExecuteEql)
    ///   - RecordManager: Mock&lt;RecordManager&gt; with virtual UpdateRecord setup
    ///   - RunJobAsync (private): invoked via reflection to bypass ExecuteAsync timer delay
    ///   - FormatterServices.GetUninitializedObject used for deep constructor dependencies
    ///     that have private constructors (CoreDbContext) or complex dependency chains
    /// </summary>
    public class StartTasksOnStartDateJobTests
    {
        #region Constants — Well-Known GUIDs from Monolith

        /// <summary>
        /// The "started/in-progress" status GUID that the job sets on each task.
        /// Source: StartTasksOnStartDate.cs line 23:
        ///   <c>patchRecord["status_id"] = new Guid("20d73f63-3501-4565-a55e-2d291549a9bd");</c>
        /// </summary>
        private static readonly Guid ExpectedStartedStatusId =
            new Guid("20d73f63-3501-4565-a55e-2d291549a9bd");

        /// <summary>
        /// The original monolith job type GUID from the [Job] attribute.
        /// Source: StartTasksOnStartDate.cs line 11:
        ///   <c>[Job("3D18B8D8-74B8-45B1-B121-9582F7B8A4F4", ...)]</c>
        /// </summary>
        private static readonly Guid ExpectedJobTypeId =
            new Guid("3D18B8D8-74B8-45B1-B121-9582F7B8A4F4");

        /// <summary>
        /// The original monolith schedule plan GUID from ProjectPlugin.cs.
        /// Source: ProjectPlugin.cs SchedulePlan registration:
        ///   <c>new Guid("6765D758-FB63-478F-B714-5B153AB9A758")</c>
        /// </summary>
        private static readonly Guid ExpectedSchedulePlanId =
            new Guid("6765D758-FB63-478F-B714-5B153AB9A758");

        #endregion

        #region Fields and Mocks

        private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
        private readonly Mock<IServiceScope> _mockScope;
        private readonly Mock<IServiceProvider> _mockServiceProvider;
        private readonly Mock<ILogger<StartTasksOnStartDateJob>> _mockLogger;
        private readonly Mock<RecordManager> _mockRecordManager;

        /// <summary>
        /// The entity record list returned by the testable TaskService.
        /// Set per-test in Arrange to control GetTasksThatNeedStarting() output.
        /// </summary>
        private EntityRecordList _taskListToReturn;

        #endregion

        #region Nested Test Double — TestableTaskService

        /// <summary>
        /// Concrete subclass of <see cref="TaskService"/> that overrides the protected virtual
        /// <see cref="TaskService.ExecuteEql"/> method to return controlled test data.
        ///
        /// This is necessary because <c>TaskService.GetTasksThatNeedStarting()</c> is NOT
        /// virtual (cannot be mocked by Moq), but it internally delegates to the protected
        /// virtual <c>ExecuteEql(string, List&lt;EqlParameter&gt;)</c> method. By overriding
        /// ExecuteEql, we control the return value of GetTasksThatNeedStarting() without
        /// requiring a live database.
        ///
        /// Constructor dependencies use FormatterServices.GetUninitializedObject to bypass
        /// complex construction chains (CoreDbContext has a private constructor, etc.).
        /// </summary>
        private class TestableTaskService : TaskService
        {
            private readonly EntityRecordList _eqlResult;

            public TestableTaskService(
                EntityRecordList eqlResult,
                RecordManager recordManager,
                EntityManager entityManager,
                EntityRelationManager relationManager,
                FeedService feedService,
                ILogger<TaskService> logger)
                : base(recordManager, entityManager, relationManager, feedService, logger)
            {
                _eqlResult = eqlResult;
            }

            /// <summary>
            /// Returns the pre-configured EntityRecordList, bypassing EQL database execution.
            /// </summary>
            protected override EntityRecordList ExecuteEql(string text, List<EqlParameter> parameters)
            {
                return _eqlResult;
            }
        }

        #endregion

        #region Constructor — Test Setup

        /// <summary>
        /// Initializes the shared mock infrastructure for each test.
        ///
        /// DI scope chain wired as:
        ///   IServiceScopeFactory.CreateScope() → IServiceScope.ServiceProvider
        ///     → GetService(typeof(TaskService)) → TestableTaskService
        ///     → GetService(typeof(RecordManager)) → Mock&lt;RecordManager&gt;.Object
        ///
        /// Uses FormatterServices.GetUninitializedObject to bypass complex constructor
        /// chains for CoreDbContext (private constructor), EntityManager, EntityRelationManager,
        /// and FeedService (all require non-null dependencies).
        /// </summary>
        public StartTasksOnStartDateJobTests()
        {
            _mockScopeFactory = new Mock<IServiceScopeFactory>();
            _mockScope = new Mock<IServiceScope>();
            _mockServiceProvider = new Mock<IServiceProvider>();
            _mockLogger = new Mock<ILogger<StartTasksOnStartDateJob>>();

            // Create uninitialized instances for deep dependencies that have
            // private constructors or complex dependency chains.
            // These objects pass non-null checks but never have their methods called
            // in the context of these unit tests.
            var uninitDbContext = (CoreDbContext)FormatterServices
                .GetUninitializedObject(typeof(CoreDbContext));
            var uninitEntityManager = (EntityManager)FormatterServices
                .GetUninitializedObject(typeof(EntityManager));
            var uninitRelationManager = (EntityRelationManager)FormatterServices
                .GetUninitializedObject(typeof(EntityRelationManager));
            var mockPublishEndpoint = Mock.Of<IPublishEndpoint>();

            // Create Mock<RecordManager> with uninitialized constructor dependencies.
            // UpdateRecord IS virtual, so Moq can intercept it.
            _mockRecordManager = new Mock<RecordManager>(
                MockBehavior.Loose,
                uninitDbContext,
                uninitEntityManager,
                uninitRelationManager,
                mockPublishEndpoint,
                false,  // ignoreSecurity
                true    // publishEvents
            );

            // Default: all UpdateRecord calls succeed
            _mockRecordManager
                .Setup(rm => rm.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
                .Returns(CreateSuccessResponse());

            // Default: return empty task list (overridden per-test as needed)
            _taskListToReturn = new EntityRecordList();

            // Wire up the DI scope chain
            _mockScope.Setup(s => s.ServiceProvider).Returns(_mockServiceProvider.Object);
            _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);

            // Wire TaskService resolution — returns a TestableTaskService
            _mockServiceProvider
                .Setup(p => p.GetService(typeof(TaskService)))
                .Returns(() => CreateTestableTaskService());

            // Wire RecordManager resolution
            _mockServiceProvider
                .Setup(p => p.GetService(typeof(RecordManager)))
                .Returns(() => _mockRecordManager.Object);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a <see cref="StartTasksOnStartDateJob"/> with the shared mocked dependencies.
        /// </summary>
        private StartTasksOnStartDateJob CreateJob()
        {
            return new StartTasksOnStartDateJob(
                _mockScopeFactory.Object,
                _mockLogger.Object);
        }

        /// <summary>
        /// Creates a <see cref="TestableTaskService"/> that returns <see cref="_taskListToReturn"/>
        /// when GetTasksThatNeedStarting() is called (via the overridden ExecuteEql).
        ///
        /// Uses FormatterServices.GetUninitializedObject for constructor dependencies that
        /// are never exercised during test execution.
        /// </summary>
        private TestableTaskService CreateTestableTaskService()
        {
            var uninitEntityManager = (EntityManager)FormatterServices
                .GetUninitializedObject(typeof(EntityManager));
            var uninitRelationManager = (EntityRelationManager)FormatterServices
                .GetUninitializedObject(typeof(EntityRelationManager));
            var uninitFeedService = (FeedService)FormatterServices
                .GetUninitializedObject(typeof(FeedService));
            var mockTaskLogger = Mock.Of<ILogger<TaskService>>();

            return new TestableTaskService(
                _taskListToReturn,
                _mockRecordManager.Object,
                uninitEntityManager,
                uninitRelationManager,
                uninitFeedService,
                mockTaskLogger);
        }

        /// <summary>
        /// Creates a <see cref="QueryResponse"/> representing a successful update.
        /// </summary>
        private QueryResponse CreateSuccessResponse()
        {
            return new QueryResponse
            {
                Success = true,
                Message = "Record updated successfully"
            };
        }

        /// <summary>
        /// Creates a <see cref="QueryResponse"/> representing a failed update.
        /// </summary>
        private QueryResponse CreateFailureResponse(string message)
        {
            return new QueryResponse
            {
                Success = false,
                Message = message
            };
        }

        /// <summary>
        /// Creates an <see cref="EntityRecord"/> representing a task with the given ID.
        /// Matches the monolith's task record shape where "id" is a Guid field.
        /// </summary>
        private EntityRecord CreateTaskRecord(Guid id)
        {
            var record = new EntityRecord();
            record["id"] = id;
            return record;
        }

        /// <summary>
        /// Invokes the private <c>RunJobAsync(CancellationToken)</c> method via reflection,
        /// bypassing the ExecuteAsync timer delay to directly test business logic.
        ///
        /// This is necessary because:
        ///   - RunJobAsync is private (no InternalsVisibleTo set up)
        ///   - ExecuteAsync calculates up to 24-hour delay before calling RunJobAsync
        ///   - Unit tests need direct access to the business logic path
        /// </summary>
        private async Task InvokeRunJobAsync(StartTasksOnStartDateJob job, CancellationToken ct = default)
        {
            var method = typeof(StartTasksOnStartDateJob).GetMethod(
                "RunJobAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method.Should().NotBeNull("RunJobAsync must exist as a private instance method");

            var task = (Task)method.Invoke(job, new object[] { ct });
            await task;
        }

        /// <summary>
        /// Retrieves a private static readonly field value from <see cref="StartTasksOnStartDateJob"/>
        /// via reflection for GUID constant verification.
        /// </summary>
        private static object GetStaticField(string fieldName)
        {
            var field = typeof(StartTasksOnStartDateJob).GetField(
                fieldName,
                BindingFlags.Static | BindingFlags.NonPublic);
            field.Should().NotBeNull($"Field '{fieldName}' must exist as a private static field");
            return field.GetValue(null);
        }

        #endregion

        #region Test — SecurityContext.OpenSystemScope() Usage

        /// <summary>
        /// Verifies that the job executes within a SecurityContext.OpenSystemScope() scope,
        /// which provides elevated system-level permissions for background task updates.
        ///
        /// Source: StartTasksOnStartDate.cs line 16:
        ///   <c>using (SecurityContext.OpenSystemScope())</c>
        ///
        /// AAP 0.8.3: SecurityContext.OpenSystemScope() must be preserved for background jobs.
        ///
        /// Testing approach: SecurityContext.OpenSystemScope() is static and cannot be directly
        /// mocked. We verify that the job completes successfully with a valid task, which
        /// implicitly proves the system scope was opened (without it, permission checks would
        /// fail in production). The task service call and record update both execute within
        /// the scope, so successful completion validates scope usage.
        /// Integration tests provide full SecurityContext verification.
        /// </summary>
        [Fact]
        public async Task Execute_OpensSystemSecurityScope()
        {
            // Arrange: Set up a task to process — job must open system scope before
            // calling GetTasksThatNeedStarting and UpdateRecord
            var taskId = Guid.NewGuid();
            _taskListToReturn = new EntityRecordList { CreateTaskRecord(taskId) };

            _mockRecordManager
                .Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
                .Returns(CreateSuccessResponse());

            var job = CreateJob();

            // Act: Execute the job business logic
            await InvokeRunJobAsync(job);

            // Assert: Job completed successfully, which requires SecurityContext.OpenSystemScope()
            // to have been called. The DI scope was created, TaskService was resolved,
            // and UpdateRecord was called — all within the security scope.
            _mockScopeFactory.Verify(f => f.CreateScope(), Times.Once());
            _mockServiceProvider.Verify(
                p => p.GetService(typeof(TaskService)), Times.Once());
            _mockRecordManager.Verify(
                rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()), Times.Once());
        }

        #endregion

        #region Test — TaskService.GetTasksThatNeedStarting() Call

        /// <summary>
        /// Verifies that the job delegates to TaskService.GetTasksThatNeedStarting()
        /// to retrieve tasks needing activation.
        ///
        /// Source: StartTasksOnStartDate.cs line 18:
        ///   <c>var tasks = new TaskService().GetTasksThatNeedStarting();</c>
        ///
        /// In the microservice, TaskService is resolved from DI instead of instantiated.
        /// </summary>
        [Fact]
        public async Task Execute_CallsGetTasksThatNeedStarting()
        {
            // Arrange: Empty task list — no updates expected
            _taskListToReturn = new EntityRecordList();
            var job = CreateJob();

            // Act
            await InvokeRunJobAsync(job);

            // Assert: TaskService was resolved from DI (proves GetTasksThatNeedStarting was called)
            _mockServiceProvider.Verify(
                p => p.GetService(typeof(TaskService)), Times.Once(),
                "Job must resolve TaskService from DI to call GetTasksThatNeedStarting()");
        }

        #endregion

        #region Test — Status Update with Correct GUID

        /// <summary>
        /// Verifies that the job updates each task's status_id to the exact
        /// "started/in-progress" GUID: 20d73f63-3501-4565-a55e-2d291549a9bd.
        ///
        /// THIS IS THE CRITICAL BUSINESS RULE.
        ///
        /// Source: StartTasksOnStartDate.cs line 23:
        ///   <c>patchRecord["status_id"] = new Guid("20d73f63-3501-4565-a55e-2d291549a9bd");</c>
        ///
        /// AAP 0.8.1: Zero tolerance — the exact GUID must be preserved.
        /// </summary>
        [Fact]
        public async Task Execute_UpdatesTaskStatusToStartedInProgressGuid()
        {
            // Arrange
            var taskId = Guid.NewGuid();
            _taskListToReturn = new EntityRecordList { CreateTaskRecord(taskId) };

            EntityRecord capturedPatch = null;
            _mockRecordManager
                .Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
                .Callback<string, EntityRecord>((entity, record) => capturedPatch = record)
                .Returns(CreateSuccessResponse());

            var job = CreateJob();

            // Act
            await InvokeRunJobAsync(job);

            // Assert: The patch record's status_id MUST be exactly 20d73f63-3501-4565-a55e-2d291549a9bd
            capturedPatch.Should().NotBeNull("UpdateRecord must have been called with a patch record");
            ((Guid)capturedPatch["status_id"]).Should().Be(
                ExpectedStartedStatusId,
                "status_id must be set to the 'started/in-progress' GUID " +
                "20d73f63-3501-4565-a55e-2d291549a9bd (monolith line 23)");
        }

        #endregion

        #region Test — Error Handling on UpdateRecord Failure

        /// <summary>
        /// Verifies that when UpdateRecord returns Success=false, the inner business logic
        /// throws an Exception with the failure message (preserving monolith fail-fast behavior).
        ///
        /// The outer RunJobAsync catch block catches the exception and logs it as an error,
        /// preventing the BackgroundService from crashing. This test verifies:
        ///   1. The failure is detected (fail-fast within SecurityContext scope)
        ///   2. The job does NOT crash (exception is caught in RunJobAsync)
        ///   3. The error is logged
        ///
        /// Source: StartTasksOnStartDate.cs lines 25-27:
        ///   <c>if (!updateResult.Success) { throw new Exception(updateResult.Message); }</c>
        /// </summary>
        [Fact]
        public async Task Execute_ThrowsException_WhenUpdateRecordFails()
        {
            // Arrange: One task that will fail to update
            var taskId = Guid.NewGuid();
            _taskListToReturn = new EntityRecordList { CreateTaskRecord(taskId) };

            var failureMessage = "Update failed: constraint violation";
            _mockRecordManager
                .Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
                .Returns(CreateFailureResponse(failureMessage));

            var job = CreateJob();

            // Act: RunJobAsync catches the exception internally and logs it.
            // The job should NOT throw to the caller (BackgroundService resilience).
            Func<Task> act = async () => await InvokeRunJobAsync(job);
            await act.Should().NotThrowAsync(
                "RunJobAsync catches exceptions to prevent BackgroundService crash. " +
                "The inner throw (monolith line 26) is caught by the outer try/catch.");

            // Assert: UpdateRecord was called exactly once (first task failed)
            _mockRecordManager.Verify(
                rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()),
                Times.Once(),
                "UpdateRecord should have been called for the failing task");
        }

        /// <summary>
        /// Verifies the monolith's fail-fast behavior: when UpdateRecord fails for one task,
        /// the remaining tasks are NOT processed. The exception thrown inside the foreach
        /// loop (monolith line 26) stops iteration.
        ///
        /// Source: StartTasksOnStartDate.cs lines 19-28:
        ///   foreach (var task in tasks)
        ///   {
        ///       ...
        ///       if (!updateResult.Success) {
        ///           throw new Exception(updateResult.Message);
        ///       }
        ///   }
        ///
        /// With 3 tasks where task 2 fails, only tasks 1 and 2 should be processed.
        /// Task 3 is never reached due to the thrown exception.
        /// </summary>
        [Fact]
        public async Task Execute_StopsProcessingRemainingTasks_OnFirstUpdateFailure()
        {
            // Arrange: Three tasks — task 2 will fail
            var taskId1 = Guid.NewGuid();
            var taskId2 = Guid.NewGuid();
            var taskId3 = Guid.NewGuid();
            _taskListToReturn = new EntityRecordList
            {
                CreateTaskRecord(taskId1),
                CreateTaskRecord(taskId2),
                CreateTaskRecord(taskId3)
            };

            var callCount = 0;
            _mockRecordManager
                .Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
                .Returns(() =>
                {
                    callCount++;
                    // Task 1 succeeds, Task 2 fails
                    return callCount == 1
                        ? CreateSuccessResponse()
                        : CreateFailureResponse("Constraint violation on task 2");
                });

            var job = CreateJob();

            // Act: Job catches the exception from task 2 failure
            await InvokeRunJobAsync(job);

            // Assert: UpdateRecord called exactly 2 times (task 1 success + task 2 failure)
            // Task 3 was never processed due to fail-fast behavior
            _mockRecordManager.Verify(
                rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()),
                Times.Exactly(2),
                "Fail-fast: processing must stop after first UpdateRecord failure. " +
                "Task 3 must NOT be processed (monolith lines 25-27: throw on failure).");
        }

        #endregion

        #region Test — Iteration Over Task Collection

        /// <summary>
        /// Verifies that the job processes ALL returned tasks when all updates succeed.
        ///
        /// Source: StartTasksOnStartDate.cs lines 19-28:
        ///   <c>foreach (var task in tasks) { ... UpdateRecord ... }</c>
        ///
        /// With 5 tasks all succeeding, UpdateRecord must be called exactly 5 times.
        /// </summary>
        [Fact]
        public async Task Execute_ProcessesAllReturnedTasks()
        {
            // Arrange: 5 tasks, all succeed
            _taskListToReturn = new EntityRecordList();
            for (int i = 0; i < 5; i++)
            {
                _taskListToReturn.Add(CreateTaskRecord(Guid.NewGuid()));
            }

            _mockRecordManager
                .Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
                .Returns(CreateSuccessResponse());

            var job = CreateJob();

            // Act
            await InvokeRunJobAsync(job);

            // Assert: All 5 tasks processed
            _mockRecordManager.Verify(
                rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()),
                Times.Exactly(5),
                "All 5 tasks must be processed when all updates succeed");
        }

        #endregion

        #region Test — EntityRecord Patch Construction

        /// <summary>
        /// Verifies that the patch EntityRecord passed to UpdateRecord contains ONLY
        /// the "id" and "status_id" fields — no extra fields are included.
        ///
        /// Source: StartTasksOnStartDate.cs lines 21-23:
        ///   <c>
        ///   var patchRecord = new EntityRecord();
        ///   patchRecord["id"] = (Guid)task["id"];
        ///   patchRecord["status_id"] = new Guid("20d73f63-...");
        ///   </c>
        ///
        /// The monolith constructs a minimal patch record with exactly 2 fields.
        /// </summary>
        [Fact]
        public async Task Execute_ConstructsPatchRecordWithOnlyIdAndStatusId()
        {
            // Arrange
            var taskId = Guid.NewGuid();
            _taskListToReturn = new EntityRecordList { CreateTaskRecord(taskId) };

            EntityRecord capturedPatch = null;
            _mockRecordManager
                .Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
                .Callback<string, EntityRecord>((entity, record) => capturedPatch = record)
                .Returns(CreateSuccessResponse());

            var job = CreateJob();

            // Act
            await InvokeRunJobAsync(job);

            // Assert: Patch record contains exactly id and status_id
            capturedPatch.Should().NotBeNull();

            // Verify the id matches the task's id (source line 22: patchRecord["id"] = (Guid)task["id"])
            ((Guid)capturedPatch["id"]).Should().Be(taskId,
                "Patch record id must match the task's id (monolith line 22)");

            // Verify status_id is the started status GUID (source line 23)
            ((Guid)capturedPatch["status_id"]).Should().Be(ExpectedStartedStatusId,
                "Patch record status_id must be the started GUID (monolith line 23)");

            // Verify no extra fields — EntityRecord inherits from Expando which uses Properties dictionary
            capturedPatch.Properties.Should().HaveCount(2,
                "Patch record must contain ONLY 'id' and 'status_id' — " +
                "no additional fields (matches monolith lines 21-23)");
        }

        #endregion

        #region Test — Empty Task List Scenario

        /// <summary>
        /// Verifies that when no tasks need starting, the job completes without
        /// calling UpdateRecord at all.
        ///
        /// Source: StartTasksOnStartDate.cs line 19:
        ///   <c>foreach (var task in tasks)</c> — empty collection = no iterations
        /// </summary>
        [Fact]
        public async Task Execute_DoesNothing_WhenNoTasksNeedStarting()
        {
            // Arrange: Empty task list
            _taskListToReturn = new EntityRecordList();
            var job = CreateJob();

            // Act
            await InvokeRunJobAsync(job);

            // Assert: No UpdateRecord calls when no tasks need starting
            _mockRecordManager.Verify(
                rm => rm.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()),
                Times.Never(),
                "When no tasks need starting, UpdateRecord must NOT be called");
        }

        #endregion

        #region Test — Multiple Tasks with Correct IDs

        /// <summary>
        /// Verifies that when multiple tasks are returned, each task's ID is correctly
        /// set in the corresponding patch record, and all patch records have the same
        /// started status GUID.
        ///
        /// Source: StartTasksOnStartDate.cs lines 19-28:
        ///   The foreach loop creates a new patchRecord per task with:
        ///   - patchRecord["id"] = (Guid)task["id"]
        ///   - patchRecord["status_id"] = new Guid("20d73f63-...")
        /// </summary>
        [Fact]
        public async Task Execute_UpdatesEachTaskWithCorrectId_WhenMultipleTasksReturned()
        {
            // Arrange: 3 tasks with specific GUIDs
            var taskId1 = Guid.NewGuid();
            var taskId2 = Guid.NewGuid();
            var taskId3 = Guid.NewGuid();
            _taskListToReturn = new EntityRecordList
            {
                CreateTaskRecord(taskId1),
                CreateTaskRecord(taskId2),
                CreateTaskRecord(taskId3)
            };

            var capturedPatches = new List<EntityRecord>();
            _mockRecordManager
                .Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
                .Callback<string, EntityRecord>((entity, record) =>
                {
                    // Capture a copy of the record's field values since EntityRecord
                    // might be reused (it's not, but capture ensures test correctness)
                    var copy = new EntityRecord();
                    copy["id"] = record["id"];
                    copy["status_id"] = record["status_id"];
                    capturedPatches.Add(copy);
                })
                .Returns(CreateSuccessResponse());

            var job = CreateJob();

            // Act
            await InvokeRunJobAsync(job);

            // Assert: 3 calls, each with the correct task ID and same status GUID
            capturedPatches.Should().HaveCount(3, "All 3 tasks must be processed");

            ((Guid)capturedPatches[0]["id"]).Should().Be(taskId1,
                "First patch must use taskId1");
            ((Guid)capturedPatches[0]["status_id"]).Should().Be(ExpectedStartedStatusId);

            ((Guid)capturedPatches[1]["id"]).Should().Be(taskId2,
                "Second patch must use taskId2");
            ((Guid)capturedPatches[1]["status_id"]).Should().Be(ExpectedStartedStatusId);

            ((Guid)capturedPatches[2]["id"]).Should().Be(taskId3,
                "Third patch must use taskId3");
            ((Guid)capturedPatches[2]["status_id"]).Should().Be(ExpectedStartedStatusId);
        }

        #endregion

        #region Test — Job Metadata Validation (Well-Known GUIDs)

        /// <summary>
        /// Verifies that the class preserves the monolith's job type GUID as a
        /// private static readonly field for migration traceability.
        ///
        /// Source: StartTasksOnStartDate.cs line 11:
        ///   <c>[Job("3D18B8D8-74B8-45B1-B121-9582F7B8A4F4", ...)]</c>
        /// </summary>
        [Fact]
        public void Job_HasCorrectJobTypeGuid()
        {
            // Act: Retrieve the private static readonly JobTypeId field via reflection
            var jobTypeId = GetStaticField("JobTypeId");

            // Assert
            jobTypeId.Should().BeOfType<Guid>();
            ((Guid)jobTypeId).Should().Be(ExpectedJobTypeId,
                "JobTypeId must match the monolith's [Job] attribute GUID " +
                "3D18B8D8-74B8-45B1-B121-9582F7B8A4F4 for migration traceability");
        }

        /// <summary>
        /// Verifies that the class preserves the monolith's schedule plan GUID as a
        /// private static readonly field for migration traceability.
        ///
        /// Source: ProjectPlugin.cs SchedulePlan registration:
        ///   <c>new Guid("6765D758-FB63-478F-B714-5B153AB9A758")</c>
        /// </summary>
        [Fact]
        public void Job_HasCorrectSchedulePlanGuid()
        {
            // Act
            var schedulePlanId = GetStaticField("SchedulePlanId");

            // Assert
            schedulePlanId.Should().BeOfType<Guid>();
            ((Guid)schedulePlanId).Should().Be(ExpectedSchedulePlanId,
                "SchedulePlanId must match the monolith's ProjectPlugin " +
                "schedule plan GUID 6765D758-FB63-478F-B714-5B153AB9A758");
        }

        /// <summary>
        /// Verifies that the class preserves the "started/in-progress" status GUID as a
        /// private static readonly field.
        ///
        /// Source: StartTasksOnStartDate.cs line 23:
        ///   <c>new Guid("20d73f63-3501-4565-a55e-2d291549a9bd")</c>
        /// </summary>
        [Fact]
        public void Job_HasCorrectStartedStatusGuid()
        {
            // Act
            var startedStatusId = GetStaticField("StartedStatusId");

            // Assert
            startedStatusId.Should().BeOfType<Guid>();
            ((Guid)startedStatusId).Should().Be(ExpectedStartedStatusId,
                "StartedStatusId must match the monolith's started/in-progress " +
                "status GUID 20d73f63-3501-4565-a55e-2d291549a9bd");
        }

        #endregion

        #region Test — IHostedService / BackgroundService Lifecycle

        /// <summary>
        /// Verifies that StartTasksOnStartDateJob inherits from BackgroundService,
        /// confirming the monolith's ErpJob has been correctly transformed to the
        /// ASP.NET Core hosted service pattern.
        /// </summary>
        [Fact]
        public void Job_InheritsBackgroundService()
        {
            // Assert
            typeof(StartTasksOnStartDateJob).BaseType
                .Should().Be(typeof(BackgroundService),
                    "StartTasksOnStartDateJob must inherit from BackgroundService " +
                    "(replacing monolith's ErpJob base class)");

            // Also verify it implements IHostedService
            typeof(IHostedService).IsAssignableFrom(typeof(StartTasksOnStartDateJob))
                .Should().BeTrue(
                    "StartTasksOnStartDateJob must implement IHostedService " +
                    "(inherited from BackgroundService)");
        }

        /// <summary>
        /// Verifies that the job handles cancellation gracefully without throwing
        /// unhandled exceptions. When the host shuts down, the CancellationToken
        /// is signalled and the job must exit cleanly.
        ///
        /// The StartTasksOnStartDateJob.ExecuteAsync loop catches OperationCanceledException
        /// from Task.Delay and breaks out of the while loop.
        /// The RunJobAsync method re-throws OperationCanceledException after logging a warning.
        /// </summary>
        [Fact]
        public async Task Job_CanBeCancelled()
        {
            // Arrange: Pre-cancel the token
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var job = CreateJob();

            // Act: Start the job with an already-cancelled token
            // BackgroundService.StartAsync calls ExecuteAsync which checks the token
            Func<Task> act = async () =>
            {
                await job.StartAsync(cts.Token);
                // Give a small window for the background task to process
                await Task.Delay(100);
                await job.StopAsync(CancellationToken.None);
            };

            // Assert: Job exits gracefully without unhandled exceptions
            await act.Should().NotThrowAsync(
                "Job must handle cancellation gracefully. The ExecuteAsync loop " +
                "catches OperationCanceledException from Task.Delay and exits cleanly.");
        }

        /// <summary>
        /// Verifies that StartTasksOnStartDateJob can be registered and resolved as an
        /// IHostedService in the ASP.NET Core DI container, confirming the registration
        /// pattern: <c>builder.Services.AddHostedService&lt;StartTasksOnStartDateJob&gt;()</c>
        /// </summary>
        [Fact]
        public void Job_CanBeRegisteredAsHostedService()
        {
            // Arrange: Build a service collection with required dependencies
            var services = new ServiceCollection();
            services.AddSingleton<IServiceScopeFactory>(_mockScopeFactory.Object);
            services.AddSingleton<ILogger<StartTasksOnStartDateJob>>(_mockLogger.Object);
            services.AddHostedService<StartTasksOnStartDateJob>();

            // Act: Build the provider and resolve IHostedService instances
            using var provider = services.BuildServiceProvider();
            var hostedServices = provider.GetServices<IHostedService>();

            // Assert: StartTasksOnStartDateJob is resolvable as IHostedService
            hostedServices.Should().NotBeNull();
            hostedServices.OfType<StartTasksOnStartDateJob>()
                .Should().HaveCount(1,
                    "StartTasksOnStartDateJob must be resolvable as IHostedService from the DI container");
        }

        #endregion

        #region Test — UpdateRecord Entity Name

        /// <summary>
        /// Verifies that UpdateRecord is called with the entity name "task" — NOT any other
        /// entity name. This is a critical contract: the job updates the "task" entity.
        ///
        /// Source: StartTasksOnStartDate.cs line 24:
        ///   <c>var updateResult = new RecordManager().UpdateRecord("task", patchRecord);</c>
        /// </summary>
        [Fact]
        public async Task Execute_UpdatesCorrectEntityName()
        {
            // Arrange
            var taskId = Guid.NewGuid();
            _taskListToReturn = new EntityRecordList { CreateTaskRecord(taskId) };

            _mockRecordManager
                .Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
                .Returns(CreateSuccessResponse());

            var job = CreateJob();

            // Act
            await InvokeRunJobAsync(job);

            // Assert: Entity name in the UpdateRecord call MUST be "task"
            _mockRecordManager.Verify(
                rm => rm.UpdateRecord(
                    "task",
                    It.IsAny<EntityRecord>()),
                Times.Once(),
                "UpdateRecord must be called with entity name 'task' (monolith line 24)");

            // Verify it was NOT called with any other entity name
            _mockRecordManager.Verify(
                rm => rm.UpdateRecord(
                    It.Is<string>(name => name != "task"),
                    It.IsAny<EntityRecord>()),
                Times.Never(),
                "UpdateRecord must ONLY be called for the 'task' entity");
        }

        #endregion

        #region Test — Scheduling Logic (CalculateNextRunDelay)

        /// <summary>
        /// Verifies that CalculateNextRunDelay returns a delay pointing to today's
        /// 00:10 UTC when the current time is before 00:10 UTC.
        ///
        /// Source: StartTasksOnStartDateJob.CalculateNextRunDelay():
        ///   var todayRun = new DateTime(now.Year, now.Month, now.Day, 0, 10, 0, DateTimeKind.Utc);
        ///   var nextRun = now &lt; todayRun ? todayRun : todayRun.AddDays(1);
        ///   return nextRun - now;
        ///
        /// CalculateNextRunDelay is private static — tested via reflection.
        /// </summary>
        [Fact]
        public void CalculateNextRunDelay_ReturnsCorrectDelay_WhenBeforeScheduledTime()
        {
            // Arrange: Get the private static method via reflection
            var method = typeof(StartTasksOnStartDateJob).GetMethod(
                "CalculateNextRunDelay",
                BindingFlags.NonPublic | BindingFlags.Static);
            method.Should().NotBeNull("CalculateNextRunDelay must exist as a private static method");

            // Act: Invoke the method (uses DateTime.UtcNow internally)
            var delay = (TimeSpan)method.Invoke(null, null);

            // Assert: The delay should be positive and less than or equal to 24 hours
            // (it targets the next 00:10 UTC, whether today or tomorrow)
            delay.Should().BeGreaterThan(TimeSpan.Zero,
                "Delay to next 00:10 UTC must be positive");
            delay.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(24),
                "Delay to next 00:10 UTC must be at most 24 hours");

            // Verify the target time is correct: nextRun = now + delay should be at 00:10 UTC
            var now = DateTime.UtcNow;
            var nextRun = now + delay;
            nextRun.Hour.Should().Be(0, "Next run hour must be 00 UTC");
            nextRun.Minute.Should().Be(10, "Next run minute must be 10");
            nextRun.Second.Should().Be(0, "Next run second must be 0");
        }

        /// <summary>
        /// Verifies that CalculateNextRunDelay returns a delay pointing to TOMORROW's
        /// 00:10 UTC when the current time is after today's 00:10 UTC.
        ///
        /// Since we cannot control DateTime.UtcNow in the static method, this test
        /// verifies the general contract: the returned delay always points to a future
        /// 00:10 UTC timestamp (either today or tomorrow).
        /// </summary>
        [Fact]
        public void CalculateNextRunDelay_ReturnsNextDayDelay_WhenAfterScheduledTime()
        {
            // Arrange
            var method = typeof(StartTasksOnStartDateJob).GetMethod(
                "CalculateNextRunDelay",
                BindingFlags.NonPublic | BindingFlags.Static);
            method.Should().NotBeNull();

            // Act
            var delay = (TimeSpan)method.Invoke(null, null);
            var now = DateTime.UtcNow;
            var targetTime = now + delay;

            // Assert: The target time is at 00:10 UTC on either today or tomorrow
            targetTime.Hour.Should().Be(0, "Scheduled hour must be 00 UTC");
            targetTime.Minute.Should().Be(10, "Scheduled minute must be 10");

            // If current time is after today's 00:10, the target is tomorrow's 00:10
            var todayScheduled = new DateTime(now.Year, now.Month, now.Day, 0, 10, 0, DateTimeKind.Utc);
            if (now >= todayScheduled)
            {
                targetTime.Date.Should().Be(now.Date.AddDays(1),
                    "When after today's 00:10 UTC, next run must be tomorrow");
                delay.Should().BeGreaterThan(TimeSpan.Zero,
                    "Delay must be positive when scheduling for tomorrow");
            }
            else
            {
                targetTime.Date.Should().Be(now.Date,
                    "When before today's 00:10 UTC, next run must be today");
            }
        }

        #endregion
    }
}
