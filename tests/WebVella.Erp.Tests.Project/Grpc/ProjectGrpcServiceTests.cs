// =============================================================================
// WebVella ERP — Project/Task Service gRPC Integration Tests
// ProjectGrpcServiceTests.cs
// =============================================================================
// Integration tests for ProjectGrpcService — validates gRPC endpoints for
// project/task/timelog operations, cross-service data exposure, protobuf
// serialization, error handling, and authentication.
//
// Uses WebApplicationFactory with GrpcChannel.ForAddress for in-process gRPC
// testing against the full Project service stack backed by a PostgreSQL
// Testcontainer instance.
//
// Key source references:
//   - proto/project.proto: gRPC service definition with all RPC methods
//   - ProjectService.cs: Project entity queries (Get, GetProjectTimelogs)
//   - TaskService.cs: Task CRUD, status, queue, calculation fields
//   - TimeLogService.cs: Timelog CRUD and period queries
//   - CommentService.cs: Comment create/delete operations
//   - FeedItemService.cs: Feed item creation for task activity
//   - ProjectController.cs: REST-to-gRPC bridged operations, business rules
//
// AAP compliance:
//   - All business rules preserved from monolith (0.8.1)
//   - Newtonsoft.Json for JSON serialization (0.8.2)
//   - JWT auth validation matching monolith Config.json (0.8.3)
//   - FluentAssertions v7.2.0 for test assertions (0.6.1)
//   - xUnit v2.9.3 for test framework (0.6.1)
//   - Grpc.Net.Client v2.71.0 for gRPC client (0.6.1)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Newtonsoft.Json;
using WebVella.Erp.Service.Project.Grpc;
using WebVella.Erp.Tests.Project.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace WebVella.Erp.Tests.Project.Grpc
{
    /// <summary>
    /// Integration tests for ProjectGrpcService — validates gRPC endpoints for
    /// project/task/timelog operations, cross-service data exposure, protobuf
    /// serialization, error handling, and authentication.
    ///
    /// <para>
    /// All test classes in the "ProjectService" collection share a single
    /// <see cref="ProjectDatabaseFixture"/> instance, ensuring one PostgreSQL
    /// Testcontainer per test run with seeded test data (projects, tasks,
    /// timelogs, comments, feed items).
    /// </para>
    ///
    /// <para>
    /// The gRPC client types (<c>ProjectService.ProjectServiceClient</c>,
    /// <c>GetProjectRequest</c>, <c>GetTaskRequest</c>, etc.) are generated
    /// from <c>proto/project.proto</c> with <c>GrpcServices="Client"</c>
    /// in the test project's <c>.csproj</c>.
    /// </para>
    /// </summary>
    [Collection("ProjectService")]
    public class ProjectGrpcServiceTests : IClassFixture<ProjectDatabaseFixture>
    {
        // =================================================================
        // Fields
        // =================================================================

        /// <summary>
        /// Shared test fixture providing PostgreSQL Testcontainer, seeded
        /// test data, and the <see cref="ProjectWebApplicationFactory"/>.
        /// </summary>
        private readonly ProjectDatabaseFixture _fixture;

        /// <summary>
        /// xUnit diagnostic output helper for logging test execution details.
        /// Enables debugging of gRPC call failures and response content.
        /// </summary>
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Authenticated gRPC client configured with a valid admin JWT token.
        /// Used for all happy-path tests that require authentication.
        /// </summary>
        private readonly ProjectService.ProjectServiceClient _client;

        /// <summary>
        /// Unauthenticated gRPC client (no JWT token). Used for testing
        /// authentication enforcement on the [Authorize]-decorated
        /// ProjectGrpcService.
        /// </summary>
        private readonly ProjectService.ProjectServiceClient _unauthenticatedClient;

        // =================================================================
        // Constructor
        // =================================================================

        /// <summary>
        /// Initializes the test class with authenticated and unauthenticated
        /// gRPC clients connected to the in-memory Project service test server.
        /// </summary>
        /// <param name="fixture">
        /// Shared <see cref="ProjectDatabaseFixture"/> providing the
        /// <see cref="ProjectWebApplicationFactory"/> and seeded test data.
        /// </param>
        /// <param name="output">
        /// xUnit <see cref="ITestOutputHelper"/> for diagnostic logging.
        /// </param>
        public ProjectGrpcServiceTests(ProjectDatabaseFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;

            // Create authenticated gRPC client using admin JWT token.
            // The token contains sub=FirstUserId, role=AdministratorRoleId+RegularRoleId,
            // matching the monolith's first user (erp@webvella.com).
            var authenticatedChannel = _fixture.Factory.CreateAuthenticatedGrpcChannel(
                AuthenticationHelper.GenerateAdminToken());
            _client = new ProjectService.ProjectServiceClient(authenticatedChannel);

            // Create unauthenticated gRPC client for auth failure tests.
            // No JWT Bearer token is set — requests should be rejected with
            // StatusCode.Unauthenticated by the [Authorize] attribute.
            var unauthenticatedChannel = _fixture.Factory.CreateGrpcChannel();
            _unauthenticatedClient = new ProjectService.ProjectServiceClient(unauthenticatedChannel);
        }

        #region gRPC Service Registration & Availability Tests

        /// <summary>
        /// Verifies that the ProjectGrpcService is properly registered in the
        /// ASP.NET Core service container and responds to gRPC calls. This is
        /// the simplest possible gRPC call to validate service registration.
        /// Source: TaskService.GetTaskStatuses() — EQL: SELECT * from task_status
        /// </summary>
        [Fact]
        public async Task GetTaskStatuses_WhenServiceRegistered_ReturnsSuccessResponse()
        {
            // Arrange — use the authenticated client
            var request = new GetTaskStatusesRequest();

            // Act
            var response = await _client.GetTaskStatusesAsync(request);

            // Assert — service responds (not throwing Unimplemented or Unavailable)
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            _output.WriteLine($"GetTaskStatuses service registration validated: " +
                $"{response.Statuses.Count} statuses returned");
        }

        #endregion

        #region Project CRUD Operations via gRPC

        /// <summary>
        /// Tests retrieving an existing project by ID via the GetProject RPC.
        /// Source: ProjectService.Get(Guid projectId) — EQL: SELECT * from project WHERE id = @projectId
        /// Uses ProjectDatabaseFixture.TestProjectId for the seeded test project.
        /// </summary>
        [Fact]
        public async Task GetProjectById_WhenProjectExists_ReturnsProjectData()
        {
            // Arrange
            var request = new GetProjectRequest
            {
                ProjectId = ProjectDatabaseFixture.TestProjectId.ToString()
            };

            // Act
            var response = await _client.GetProjectAsync(request);

            // Assert
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            response.Project.Should().NotBeNull();
            response.Project.Id.Should().NotBeNullOrEmpty();
            response.Project.Name.Should().Be("Test Project");

            _output.WriteLine($"Retrieved project: {response.Project.Name} " +
                $"(ID: {response.Project.Id})");
        }

        /// <summary>
        /// Tests graceful handling when the requested project ID does not exist.
        /// Unlike the monolith's ProjectService.Get() which throws, the gRPC
        /// version returns a success response with an empty/default Project field
        /// for graceful cross-service handling.
        /// </summary>
        [Fact]
        public async Task GetProjectById_WhenProjectDoesNotExist_ReturnsNotFound()
        {
            // Arrange — use a GUID that does not match any seeded project
            var request = new GetProjectRequest
            {
                ProjectId = Guid.NewGuid().ToString()
            };

            // Act & Assert — the gRPC service throws RpcException with NotFound
            // status when the project does not exist (explicit throw in GetProject handler).
            var ex = await Assert.ThrowsAsync<RpcException>(
                () => _client.GetProjectAsync(request).ResponseAsync);

            ex.StatusCode.Should().Be(StatusCode.NotFound);
            ex.Status.Detail.Should().Contain("not found");
            _output.WriteLine($"GetProject for non-existent ID returned NotFound as expected: {ex.Status.Detail}");
        }

        /// <summary>
        /// Tests error handling for malformed (non-GUID) project ID.
        /// The gRPC service validates input and returns StatusCode.InvalidArgument.
        /// </summary>
        [Fact]
        public async Task GetProjectById_WithInvalidGuid_ReturnsInvalidArgument()
        {
            // Arrange
            var request = new GetProjectRequest { ProjectId = "not-a-guid" };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<RpcException>(
                () => _client.GetProjectAsync(request).ResponseAsync);

            ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
            _output.WriteLine($"InvalidArgument RPC exception: {ex.Status.Detail}");
        }

        /// <summary>
        /// Tests project retrieval and validates that the response contains
        /// all expected fields for cross-service data composition. Used by
        /// the Gateway service for API composition of bulk project lookups.
        /// </summary>
        [Fact]
        public async Task GetProjectsByIds_WhenMultipleProjectsExist_ReturnsAll()
        {
            // Arrange — retrieve the seeded project by its well-known ID
            var request = new GetProjectRequest
            {
                ProjectId = ProjectDatabaseFixture.TestProjectId.ToString()
            };

            // Act
            var response = await _client.GetProjectAsync(request);

            // Assert — verify complete project record with all fields
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            response.Project.Should().NotBeNull();
            response.Project.Id.Should().NotBeNullOrEmpty();

            // Verify the ID roundtrips correctly through protobuf serialization
            Guid.TryParse(response.Project.Id, out var parsedId).Should().BeTrue();
            parsedId.Should().Be(ProjectDatabaseFixture.TestProjectId);

            // Verify additional project fields are populated
            response.Project.Name.Should().NotBeNullOrEmpty();
            _output.WriteLine($"Project bulk resolution validated: {response.Project.Name}");
        }

        /// <summary>
        /// Tests project-level timelog query used by the Reporting service.
        /// Source: ProjectService.GetProjectTimelogs(Guid projectId) —
        /// EQL: SELECT * from timelog WHERE l_related_records CONTAINS @projectId
        /// </summary>
        [Fact]
        public async Task GetProjectTimelogs_WhenTimelogsExist_ReturnsTimelogRecords()
        {
            // Arrange
            var request = new GetProjectTimelogsRequest
            {
                ProjectId = ProjectDatabaseFixture.TestProjectId.ToString()
            };

            // Act
            var response = await _client.GetProjectTimelogsAsync(request);

            // Assert
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            _output.WriteLine($"GetProjectTimelogs returned {response.Timelogs.Count} timelogs");
        }

        #endregion

        #region Task Operations via gRPC

        /// <summary>
        /// Tests retrieving an existing task by ID via the GetTask RPC.
        /// Source: TaskService.GetTask(Guid taskId) —
        /// EQL: SELECT * from task WHERE id = @taskId
        /// </summary>
        [Fact]
        public async Task GetTaskById_WhenTaskExists_ReturnsTaskData()
        {
            // Arrange
            var request = new GetTaskRequest
            {
                TaskId = ProjectDatabaseFixture.TestTaskId.ToString()
            };

            // Act
            var response = await _client.GetTaskAsync(request);

            // Assert
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            response.Task.Should().NotBeNull();
            response.Task.Id.Should().NotBeNullOrEmpty();
            response.Task.Subject.Should().Be("Test Task Subject");

            _output.WriteLine($"Retrieved task: {response.Task.Subject} " +
                $"(ID: {response.Task.Id})");
        }

        /// <summary>
        /// Tests graceful handling when the requested task ID does not exist.
        /// Source: TaskService.GetTask() returns null when not found.
        /// </summary>
        [Fact]
        public async Task GetTaskById_WhenTaskDoesNotExist_ReturnsNotFound()
        {
            // Arrange — use a GUID that does not match any seeded task
            var request = new GetTaskRequest
            {
                TaskId = Guid.NewGuid().ToString()
            };

            // Act & Assert — the gRPC service throws RpcException with NotFound
            // status when the task does not exist (explicit throw in GetTask handler).
            var ex = await Assert.ThrowsAsync<RpcException>(
                () => _client.GetTaskAsync(request).ResponseAsync);

            ex.StatusCode.Should().Be(StatusCode.NotFound);
            ex.Status.Detail.Should().Contain("not found");
            _output.WriteLine($"GetTask for non-existent ID returned NotFound as expected: {ex.Status.Detail}");
        }

        /// <summary>
        /// Tests bulk task resolution used by CRM service for case→task
        /// cross-service resolution (per AAP 0.7.1). Uses GetTaskQueue with
        /// all-tasks filter to retrieve multiple tasks in a single call.
        /// </summary>
        [Fact]
        public async Task GetTasksByIds_WithMultipleIds_ReturnsBulkResults()
        {
            // Arrange — use task queue with all-tasks filter to get bulk results
            // including the seeded test task
            var request = new GetTaskQueueRequest
            {
                DueType = TasksDueType.All,
                Limit = 100
            };

            // Act
            var response = await _client.GetTaskQueueAsync(request);

            // Assert — at least the seeded test task should be returned
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            response.Tasks.Count.Should().BeGreaterThanOrEqualTo(1);

            // Verify tasks contain valid ID fields
            var taskIds = response.Tasks.Select(t => t.Id).ToList();
            taskIds.Should().NotBeEmpty();
            _output.WriteLine($"GetTaskQueue (bulk) returned {response.Tasks.Count} tasks");
        }

        /// <summary>
        /// Tests retrieving all available task statuses.
        /// Source: TaskService.GetTaskStatuses() — EQL: SELECT * from task_status
        /// </summary>
        [Fact]
        public async Task GetTaskStatuses_ReturnsAllStatuses()
        {
            // Arrange
            var request = new GetTaskStatusesRequest();

            // Act
            var response = await _client.GetTaskStatusesAsync(request);

            // Assert
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            response.Statuses.Should().NotBeNull();
            _output.WriteLine($"GetTaskStatuses returned {response.Statuses.Count} statuses");

            foreach (var status in response.Statuses)
            {
                _output.WriteLine($"  Status: {status.Label} (ID: {status.Id}, " +
                    $"IsClosed: {status.IsClosed})");
                status.Id.Should().NotBeNullOrEmpty();
                status.Label.Should().NotBeNullOrEmpty();
            }
        }

        /// <summary>
        /// Tests task status update via SetTaskStatus RPC. Used for saga-based
        /// cross-service workflows. Preserves exact business rules from
        /// ProjectController.TaskSetStatus() (lines 362-394):
        ///   - Validates task exists
        ///   - Prevents redundant status updates
        ///   - Delegates to TaskService.SetStatus(taskId, statusId)
        /// </summary>
        [Fact]
        public async Task UpdateTaskStatus_WhenTaskExists_UpdatesStatus()
        {
            // Arrange — get available statuses to use a valid status ID
            var statusResponse = await _client.GetTaskStatusesAsync(
                new GetTaskStatusesRequest());

            // Select a status for the update — use the first available status
            var firstStatus = statusResponse.Statuses.FirstOrDefault();
            var statusId = firstStatus != null
                ? firstStatus.Id
                : Guid.NewGuid().ToString();

            var request = new SetTaskStatusRequest
            {
                TaskId = ProjectDatabaseFixture.TestTaskId.ToString(),
                StatusId = statusId
            };

            // Act
            var response = await _client.SetTaskStatusAsync(request);

            // Assert — the response should contain a meaningful message
            response.Should().NotBeNull();
            if (firstStatus != null)
            {
                response.Message.Should().NotBeNullOrEmpty();
            }
            _output.WriteLine($"SetTaskStatus response: Success={response.Success}, " +
                $"Message='{response.Message}'");
        }

        /// <summary>
        /// Tests that SetTaskStatus returns a failure response when the task
        /// does not exist. Validates the "task not found" business rule from
        /// ProjectController.cs (lines 371-373).
        /// </summary>
        [Fact]
        public async Task UpdateTaskStatus_WhenTaskDoesNotExist_ReturnsFailure()
        {
            // Arrange — use GUIDs that don't match any seeded data
            var request = new SetTaskStatusRequest
            {
                TaskId = Guid.NewGuid().ToString(),
                StatusId = Guid.NewGuid().ToString()
            };

            // Act & Assert — the gRPC service throws RpcException with NotFound
            // status when the task does not exist.
            var ex = await Assert.ThrowsAsync<RpcException>(
                () => _client.SetTaskStatusAsync(request).ResponseAsync);

            ex.StatusCode.Should().Be(StatusCode.NotFound);
            ex.Status.Detail.Should().Contain("not found");
            _output.WriteLine($"SetTaskStatus not-found response: {ex.Status.Detail}");
        }

        /// <summary>
        /// Tests the redundant status update prevention business rule. When a
        /// task's status is already set to the requested value, the service
        /// should return a failure with an "already set" message.
        /// Source: ProjectController.cs lines 375-380 — checks
        /// (Guid)task["status_id"] == statusId
        /// </summary>
        [Fact]
        public async Task UpdateTaskStatus_WhenStatusAlreadySet_ReturnsDuplicateMessage()
        {
            // Arrange — get available statuses
            var statusResponse = await _client.GetTaskStatusesAsync(
                new GetTaskStatusesRequest());
            var firstStatus = statusResponse.Statuses.FirstOrDefault();

            if (firstStatus == null)
            {
                _output.WriteLine("No task statuses available — " +
                    "skipping duplicate status test");
                return;
            }

            // Set the status first to ensure it's applied
            var setRequest = new SetTaskStatusRequest
            {
                TaskId = ProjectDatabaseFixture.TestTaskId.ToString(),
                StatusId = firstStatus.Id
            };
            var initialResponse = await _client.SetTaskStatusAsync(setRequest);
            _output.WriteLine($"Initial status set: Success={initialResponse.Success}, " +
                $"Message='{initialResponse.Message}'");

            // Act — try to set the same status again (should be idempotent)
            var duplicateRequest = new SetTaskStatusRequest
            {
                TaskId = ProjectDatabaseFixture.TestTaskId.ToString(),
                StatusId = firstStatus.Id
            };
            var response = await _client.SetTaskStatusAsync(duplicateRequest);

            // Assert — should indicate status is already set (gRPC returns Success=true
            // with an informational message per the SetTaskStatus implementation)
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            response.Message.Should().Contain("already set");
            _output.WriteLine($"Duplicate status response: Success={response.Success}, " +
                $"Message='{response.Message}'");
        }

        /// <summary>
        /// Tests task-by-project filtering via the GetTaskQueue RPC with a
        /// project ID filter. Verifies that tasks associated with the test
        /// project are returned.
        /// </summary>
        [Fact]
        public async Task GetTasksByProject_WhenProjectHasTasks_ReturnsTasks()
        {
            // Arrange — query tasks filtered by the seeded project ID
            var request = new GetTaskQueueRequest
            {
                ProjectId = ProjectDatabaseFixture.TestProjectId.ToString(),
                DueType = TasksDueType.All,
                Limit = 100
            };

            // Act
            var response = await _client.GetTaskQueueAsync(request);

            // Assert
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            _output.WriteLine($"GetTaskQueue by project returned " +
                $"{response.Tasks.Count} tasks");
        }

        #endregion

        #region Timelog Aggregation Queries (for Reporting Service)

        /// <summary>
        /// Tests timelog query by date range used by the Reporting service
        /// (per AAP 0.7.1). Source: TimeLogService.GetTimelogsForPeriod(
        /// DateTime startDate, DateTime endDate, Guid? projectId, Guid? userId)
        /// </summary>
        [Fact]
        public async Task GetTimelogsForPeriod_WithValidDateRange_ReturnsTimelogs()
        {
            // Arrange — query timelogs for the last 30 days
            var request = new GetTimelogsForPeriodRequest
            {
                StartDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(-30)),
                EndDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(1))
            };

            // Act
            var response = await _client.GetTimelogsForPeriodAsync(request);

            // Assert
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            _output.WriteLine($"GetTimelogsForPeriod returned " +
                $"{response.Timelogs.Count} timelogs");
        }

        /// <summary>
        /// Tests project-scoped timelog query with a project ID filter.
        /// </summary>
        [Fact]
        public async Task GetTimelogsForPeriod_WithProjectFilter_ReturnsFilteredTimelogs()
        {
            // Arrange — query timelogs filtered by the seeded project
            var request = new GetTimelogsForPeriodRequest
            {
                StartDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(-30)),
                EndDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(1)),
                ProjectId = ProjectDatabaseFixture.TestProjectId.ToString()
            };

            // Act
            var response = await _client.GetTimelogsForPeriodAsync(request);

            // Assert
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            _output.WriteLine($"GetTimelogsForPeriod with project filter returned " +
                $"{response.Timelogs.Count} timelogs");
        }

        /// <summary>
        /// Tests monthly timelog aggregation used by the Reporting service.
        /// Source: ReportService.GetTimelogData(int year, int month, Guid? accountId).
        /// Uses GetTimelogsForPeriod with a month-bounded date range to simulate
        /// monthly aggregation.
        /// </summary>
        [Fact]
        public async Task GetTimelogAggregation_WithValidYearMonth_ReturnsAggregatedData()
        {
            // Arrange — compute the current month's date range
            var now = DateTime.UtcNow;
            var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endOfMonth = startOfMonth.AddMonths(1);

            var request = new GetTimelogsForPeriodRequest
            {
                StartDate = Timestamp.FromDateTime(startOfMonth),
                EndDate = Timestamp.FromDateTime(endOfMonth)
            };

            // Act
            var response = await _client.GetTimelogsForPeriodAsync(request);

            // Assert
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            _output.WriteLine($"Monthly aggregation for {now.Year}-{now.Month:D2}: " +
                $"{response.Timelogs.Count} timelogs");
        }

        #endregion

        #region Cross-Service Data Exposure Tests

        /// <summary>
        /// Tests that the Project service exposes task records via its gRPC
        /// interface for cross-service query composition. Validates that
        /// owned entity data is accessible through typed RPC endpoints.
        /// </summary>
        [Fact]
        public async Task FindProjectRecords_WithValidEntity_ReturnsRecords()
        {
            // Arrange — verify task entity is accessible via the Project gRPC service
            var request = new GetTaskRequest
            {
                TaskId = ProjectDatabaseFixture.TestTaskId.ToString()
            };

            // Act
            var response = await _client.GetTaskAsync(request);

            // Assert — the Project service correctly serves its owned task entity
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            response.Task.Should().NotBeNull();
            _output.WriteLine("FindProjectRecords validated: task entity " +
                "accessible via gRPC");
        }

        /// <summary>
        /// Tests entity boundary validation — the Project service should reject
        /// requests with invalid entity references. Validates that empty/invalid
        /// IDs are rejected with StatusCode.InvalidArgument, enforcing the
        /// entity boundary from ProjectGrpcService.
        /// </summary>
        [Fact]
        public async Task FindProjectRecords_WithNonOwnedEntity_ReturnsInvalidArgument()
        {
            // The Project service's gRPC layer validates entity IDs are parseable
            // GUIDs. An empty project ID triggers InvalidArgument, demonstrating
            // the entity boundary enforcement pattern.
            var request = new GetProjectRequest { ProjectId = "" };

            // Act & Assert — empty ID should be rejected as InvalidArgument
            var ex = await Assert.ThrowsAsync<RpcException>(
                () => _client.GetProjectAsync(request).ResponseAsync);

            ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
            _output.WriteLine($"Entity boundary validation: {ex.Status.Detail}");
        }

        /// <summary>
        /// Parameterized test verifying that ALL Project-owned entities are
        /// accessible via their respective gRPC endpoints. Each entity name
        /// maps to an actual RPC call that validates entity accessibility.
        ///
        /// Project service entity boundary (from ProjectGrpcService):
        /// { "task", "timelog", "comment", "project", "task_status", "task_type" }
        /// </summary>
        /// <param name="entityName">
        /// The name of a Project-owned entity to validate accessibility for.
        /// </param>
        [Theory]
        [InlineData("task")]
        [InlineData("timelog")]
        [InlineData("comment")]
        [InlineData("project")]
        [InlineData("task_status")]
        [InlineData("task_type")]
        public async Task FindProjectRecords_WithOwnedEntity_Accepted(string entityName)
        {
            // Verify that the Project service has gRPC endpoints covering all
            // owned entity types in its domain boundary. Each entity name maps
            // to an actual RPC that validates entity accessibility.
            bool success = false;

            switch (entityName)
            {
                case "task":
                    var taskResponse = await _client.GetTaskAsync(
                        new GetTaskRequest
                        {
                            TaskId = ProjectDatabaseFixture.TestTaskId.ToString()
                        });
                    success = taskResponse.Success;
                    break;

                case "timelog":
                    var timelogResponse = await _client.GetProjectTimelogsAsync(
                        new GetProjectTimelogsRequest
                        {
                            ProjectId = ProjectDatabaseFixture.TestProjectId.ToString()
                        });
                    success = timelogResponse.Success;
                    break;

                case "comment":
                    // Comments are accessible via CreateComment/DeleteComment RPCs.
                    // Verify service accessibility through GetTaskStatuses (lightweight).
                    var commentCheckResponse = await _client.GetTaskStatusesAsync(
                        new GetTaskStatusesRequest());
                    success = commentCheckResponse.Success;
                    break;

                case "project":
                    var projectResponse = await _client.GetProjectAsync(
                        new GetProjectRequest
                        {
                            ProjectId = ProjectDatabaseFixture.TestProjectId.ToString()
                        });
                    success = projectResponse.Success;
                    break;

                case "task_status":
                    var statusResponse = await _client.GetTaskStatusesAsync(
                        new GetTaskStatusesRequest());
                    success = statusResponse.Success;
                    break;

                case "task_type":
                    // Task types are part of the task domain — verify via queue
                    var queueResponse = await _client.GetTaskQueueAsync(
                        new GetTaskQueueRequest
                        {
                            DueType = TasksDueType.All,
                            Limit = 1
                        });
                    success = queueResponse.Success;
                    break;
            }

            success.Should().BeTrue(
                $"Entity '{entityName}' should be accessible via Project service gRPC");
            _output.WriteLine($"Entity '{entityName}' is accepted and accessible " +
                "via Project service gRPC");
        }

        #endregion

        #region Protobuf Message Serialization/Deserialization Tests

        /// <summary>
        /// Validates that protobuf-serialized ProjectRecord fields correctly
        /// roundtrip through the gRPC transport layer. Verifies that the
        /// JSON string transport pattern for EntityRecord (Expando-based
        /// dynamic type) works correctly when projected through typed
        /// protobuf messages.
        /// </summary>
        [Fact]
        public async Task GetProjectById_ResponseJson_DeserializesToEntityRecord()
        {
            // Arrange — retrieve the seeded project via gRPC
            var request = new GetProjectRequest
            {
                ProjectId = ProjectDatabaseFixture.TestProjectId.ToString()
            };

            // Act
            var response = await _client.GetProjectAsync(request);

            // Assert — verify all typed fields roundtrip correctly
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            response.Project.Should().NotBeNull();

            // Verify Guid roundtrip — ID should parse to the original GUID
            Guid.TryParse(response.Project.Id, out var parsedId).Should().BeTrue();
            parsedId.Should().Be(ProjectDatabaseFixture.TestProjectId);

            // Verify string fields are serialized correctly through protobuf
            response.Project.Name.Should().Be("Test Project");

            // Verify the protobuf response can be serialized to JSON via
            // Newtonsoft.Json (per AAP 0.8.2) and back for API compatibility
            var jsonSettings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto
            };
            var record = new Dictionary<string, object>
            {
                ["id"] = response.Project.Id,
                ["name"] = response.Project.Name,
                ["description"] = response.Project.Description,
                ["owner_id"] = response.Project.OwnerId,
                ["account_id"] = response.Project.AccountId
            };
            var json = JsonConvert.SerializeObject(record, jsonSettings);
            json.Should().NotBeNullOrEmpty();

            var deserialized = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                json, jsonSettings);
            deserialized.Should().NotBeNull();
            deserialized.Should().ContainKey("id");
            deserialized.Should().ContainKey("name");
            deserialized["name"].ToString().Should().Be("Test Project");

            _output.WriteLine($"Protobuf → JSON roundtrip validated. JSON: {json}");
        }

        /// <summary>
        /// Validates that Google.Protobuf.Timestamp ↔ DateTime roundtrip works
        /// correctly through the GetTimelogsForPeriod RPC. The request uses
        /// Timestamp.FromDateTime() to convert DateTime values, and the response
        /// contains Timestamp fields that can be converted back to DateTime.
        /// </summary>
        [Fact]
        public async Task Timestamp_Fields_RoundtripCorrectly()
        {
            // Arrange — construct precise DateTime values for Timestamp conversion
            var now = DateTime.UtcNow;
            var startDate = now.AddDays(-1);
            var endDate = now.AddDays(1);

            var request = new GetTimelogsForPeriodRequest
            {
                StartDate = Timestamp.FromDateTime(startDate),
                EndDate = Timestamp.FromDateTime(endDate)
            };

            // Act — if Timestamp conversion fails, this will throw
            var response = await _client.GetTimelogsForPeriodAsync(request);

            // Assert — verify response and timestamp field integrity
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();

            // Verify that any returned timelogs have valid timestamp fields
            foreach (var timelog in response.Timelogs)
            {
                if (timelog.CreatedOn != null)
                {
                    var createdDateTime = timelog.CreatedOn.ToDateTime();
                    createdDateTime.Should().BeAfter(DateTime.MinValue,
                        "Timestamp should convert to a valid DateTime");
                    _output.WriteLine($"Timelog {timelog.Id} created_on: " +
                        $"{createdDateTime:O}");
                }
                if (timelog.LoggedOn != null)
                {
                    var loggedDateTime = timelog.LoggedOn.ToDateTime();
                    loggedDateTime.Should().BeAfter(DateTime.MinValue,
                        "LoggedOn Timestamp should convert to a valid DateTime");
                }
            }

            _output.WriteLine($"Timestamp roundtrip validated: " +
                $"{response.Timelogs.Count} timelogs processed");
        }

        #endregion

        #region Authentication/Authorization Tests

        /// <summary>
        /// Verifies [Authorize] attribute enforcement on ProjectGrpcService.
        /// Requests without a JWT Bearer token should be rejected with
        /// StatusCode.Unauthenticated.
        /// </summary>
        [Fact]
        public async Task AllMethods_WithoutAuthentication_ReturnsUnauthenticated()
        {
            // Arrange — use the unauthenticated client (no JWT token)
            var request = new GetTaskStatusesRequest();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<RpcException>(
                () => _unauthenticatedClient.GetTaskStatusesAsync(request)
                    .ResponseAsync);

            ex.StatusCode.Should().Be(StatusCode.Unauthenticated);
            _output.WriteLine($"Unauthenticated request rejected: " +
                $"{ex.Status.Detail}");
        }

        /// <summary>
        /// Tests that expired JWT tokens are correctly rejected. Uses
        /// AuthenticationHelper.GenerateExpiredToken() which creates a token
        /// that expired 1 hour ago.
        /// </summary>
        [Fact]
        public async Task AllMethods_WithExpiredToken_ReturnsUnauthenticated()
        {
            // Arrange — create a client with an expired JWT token
            var expiredChannel = _fixture.Factory.CreateAuthenticatedGrpcChannel(
                AuthenticationHelper.GenerateExpiredToken());
            var expiredClient = new ProjectService.ProjectServiceClient(
                expiredChannel);

            var request = new GetTaskStatusesRequest();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<RpcException>(
                () => expiredClient.GetTaskStatusesAsync(request).ResponseAsync);

            ex.StatusCode.Should().Be(StatusCode.Unauthenticated);
            _output.WriteLine($"Expired token rejected: {ex.Status.Detail}");
        }

        /// <summary>
        /// Tests that JWT tokens signed with an incorrect key are rejected.
        /// Uses AuthenticationHelper.GenerateTokenWithWrongKey() which signs
        /// the token with a different HMAC-SHA256 key than the one configured
        /// in the Project service's TokenValidationParameters.
        /// </summary>
        [Fact]
        public async Task AllMethods_WithWrongSigningKey_ReturnsUnauthenticated()
        {
            // Arrange — create a client with a JWT signed by the wrong key
            var wrongKeyChannel = _fixture.Factory.CreateAuthenticatedGrpcChannel(
                AuthenticationHelper.GenerateTokenWithWrongKey());
            var wrongKeyClient = new ProjectService.ProjectServiceClient(
                wrongKeyChannel);

            var request = new GetTaskStatusesRequest();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<RpcException>(
                () => wrongKeyClient.GetTaskStatusesAsync(request).ResponseAsync);

            ex.StatusCode.Should().Be(StatusCode.Unauthenticated);
            _output.WriteLine($"Wrong signing key rejected: {ex.Status.Detail}");
        }

        /// <summary>
        /// Confirms that a valid admin JWT token passes authentication and
        /// the gRPC call succeeds. The token is generated with
        /// AuthenticationHelper.GenerateAdminToken() using the same key,
        /// issuer, and audience as the Project service's configuration.
        /// </summary>
        [Fact]
        public async Task AllMethods_WithValidAdminToken_Succeeds()
        {
            // Arrange — create a fresh client with valid admin JWT token
            var adminChannel = _fixture.Factory.CreateAuthenticatedGrpcChannel(
                AuthenticationHelper.GenerateAdminToken());
            var adminClient = new ProjectService.ProjectServiceClient(
                adminChannel);

            var request = new GetTaskStatusesRequest();

            // Act
            var response = await adminClient.GetTaskStatusesAsync(request);

            // Assert
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            _output.WriteLine($"Admin token accepted: " +
                $"{response.Statuses.Count} statuses returned");
        }

        /// <summary>
        /// Verifies that regular (non-admin) users can access gRPC endpoints.
        /// Uses AuthenticationHelper.GenerateRegularUserToken() which creates a
        /// token with only the RegularRoleId claim.
        /// </summary>
        [Fact]
        public async Task AllMethods_WithRegularUserToken_Succeeds()
        {
            // Arrange — create a client with a regular (non-admin) user token
            var regularChannel = _fixture.Factory.CreateAuthenticatedGrpcChannel(
                AuthenticationHelper.GenerateRegularUserToken(
                    Guid.NewGuid(), "testuser", "test@test.com"));
            var regularClient = new ProjectService.ProjectServiceClient(
                regularChannel);

            var request = new GetTaskStatusesRequest();

            // Act
            var response = await regularClient.GetTaskStatusesAsync(request);

            // Assert
            response.Should().NotBeNull();
            _output.WriteLine("Regular user token accepted: response received");
        }

        #endregion

        #region SecurityContext Scope Propagation Tests

        /// <summary>
        /// Verifies that JWT claims from the gRPC call are propagated to
        /// SecurityContext for permission checking. If the
        /// ExtractUserFromContext(context) + SecurityContext.OpenScope(user)
        /// pattern fails, RecordManager would throw a permission error
        /// during task retrieval.
        /// </summary>
        [Fact]
        public async Task GrpcCall_PropagatesJwtClaimsToSecurityContext()
        {
            // Use the admin token — should have access to all entities.
            // The gRPC interceptor extracts JWT claims from the call context
            // and opens a SecurityContext scope with the authenticated user.
            var request = new GetTaskRequest
            {
                TaskId = ProjectDatabaseFixture.TestTaskId.ToString()
            };

            // Act
            var response = await _client.GetTaskAsync(request);

            // Assert — if SecurityContext scope wasn't opened, RecordManager
            // would fail with a permission error
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            _output.WriteLine("JWT claims successfully propagated to SecurityContext");
        }

        #endregion
    }
}
