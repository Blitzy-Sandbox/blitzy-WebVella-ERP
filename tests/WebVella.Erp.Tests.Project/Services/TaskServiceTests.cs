using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;
using Moq;
using FluentAssertions;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using MassTransit;
using WebVella.Erp.Service.Project.Domain.Services;
using WebVella.Erp.Service.Project.Domain.Models;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database; // CoreDbContext for mock construction
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.Tests.Project.Services
{
	/// <summary>
	/// Comprehensive unit tests for <see cref="TaskService"/> — the largest and most complex
	/// service in the Project microservice (originally 643 lines in the monolith).
	///
	/// Covers all business rules including:
	///   - Key generation formula ({project_abbreviation}-{task_number})
	///   - Dynamic EQL queue building with 6 TasksDueType filter modes
	///   - Priority icon/color resolution from entity metadata
	///   - Timelog start/stop
	///   - Status management
	///   - Pre/Post create/update hook logic
	///   - Watcher relation management (deduplication, auto-add)
	///   - Feed item creation on task creation
	///   - Closed status exclusion for non-All queue types
	///   - Tasks-that-need-starting query with hardcoded not-started status GUID
	///
	/// Per AAP 0.8.1: every business rule maps to at least one automated test.
	/// Per AAP 0.8.2: all business logic classes must have ≥80% code coverage.
	/// Total: 50 test methods covering all 13 public methods of TaskService.
	/// </summary>
	public class TaskServiceTests : IDisposable
	{
		#region << Test Infrastructure >>

		private readonly Mock<RecordManager> _mockRecordManager;
		private readonly Mock<EntityManager> _mockEntityManager;
		private readonly Mock<EntityRelationManager> _mockRelationManager;
		private readonly Mock<FeedService> _mockFeedService;
		private readonly Mock<ILogger<TaskService>> _mockLogger;
		private readonly TestableTaskService _sut;

		/// <summary>
		/// Queue of EQL results returned by the testable EQL execution override.
		/// Each call to ExecuteEql dequeues the next result. Tests enqueue expected
		/// results in the order the TaskService method will issue EQL queries.
		/// </summary>
		private readonly Queue<EntityRecordList> _eqlResultQueue = new Queue<EntityRecordList>();

		/// <summary>
		/// Captured EQL calls for assertion. Each entry contains the EQL text and
		/// the list of EqlParameter objects passed to ExecuteEql.
		/// </summary>
		private readonly List<(string Text, List<EqlParameter> Parameters)> _capturedEqlCalls =
			new List<(string, List<EqlParameter>)>();

		/// <summary>
		/// Initializes fresh mock dependencies for each test method.
		/// Uses <see cref="RuntimeHelpers.GetUninitializedObject"/> to create dependency
		/// instances without invoking constructors — same pattern as FeedItemServiceTests.
		/// </summary>
		public TaskServiceTests()
		{
			var dbContext = (CoreDbContext)RuntimeHelpers.GetUninitializedObject(typeof(CoreDbContext));
			var entityManager = (EntityManager)RuntimeHelpers.GetUninitializedObject(typeof(EntityManager));
			var relationManager = (EntityRelationManager)RuntimeHelpers.GetUninitializedObject(typeof(EntityRelationManager));
			var publishEndpoint = Mock.Of<IPublishEndpoint>();
			var configuration = Mock.Of<IConfiguration>();

			_mockRecordManager = new Mock<RecordManager>(
				dbContext, entityManager, relationManager, publishEndpoint, false, true);
			_mockEntityManager = new Mock<EntityManager>(dbContext, configuration);
			_mockRelationManager = new Mock<EntityRelationManager>(dbContext, configuration);

			var feedRecordManager = (RecordManager)RuntimeHelpers.GetUninitializedObject(typeof(RecordManager));
			var feedLogger = Mock.Of<ILogger<FeedService>>();
			_mockFeedService = new Mock<FeedService>(feedRecordManager, feedLogger);

			_mockLogger = new Mock<ILogger<TaskService>>();

			_sut = new TestableTaskService(
				_mockRecordManager.Object,
				_mockEntityManager.Object,
				_mockRelationManager.Object,
				_mockFeedService.Object,
				_mockLogger.Object,
				ExecuteEqlHandler);
		}

		public void Dispose()
		{
			_eqlResultQueue.Clear();
			_capturedEqlCalls.Clear();
		}

		/// <summary>
		/// EQL execution handler invoked by TestableTaskService.
		/// Dequeues the next pre-configured result and captures the call for assertion.
		/// </summary>
		private EntityRecordList ExecuteEqlHandler(string text, List<EqlParameter> parameters)
		{
			_capturedEqlCalls.Add((text, new List<EqlParameter>(parameters ?? new List<EqlParameter>())));
			if (_eqlResultQueue.Count > 0)
				return _eqlResultQueue.Dequeue();
			return new EntityRecordList();
		}

		/// <summary>
		/// Enqueues an EQL result to be returned by the next ExecuteEql call.
		/// </summary>
		private void EnqueueEqlResult(EntityRecordList result)
		{
			_eqlResultQueue.Enqueue(result);
		}

		#endregion

		#region << Helper Methods >>

		/// <summary>
		/// Creates a task EntityRecord with standard fields for testing.
		/// </summary>
		private static EntityRecord CreateTaskRecord(
			Guid? id = null,
			decimal number = 1m,
			string subject = "Test task",
			string projectAbbr = "PRJ",
			Guid? projectId = null,
			Guid? projectOwnerId = null,
			Guid? ownerId = null,
			Guid? createdBy = null,
			Guid? statusId = null,
			string statusLabel = "open",
			string typeLabel = "bug",
			string body = "<p>Task body</p>",
			string priority = "medium")
		{
			var taskId = id ?? Guid.NewGuid();
			var projId = projectId ?? Guid.NewGuid();
			var projOwnerId = projectOwnerId ?? Guid.NewGuid();

			var projectRecord = new EntityRecord();
			projectRecord["abbr"] = projectAbbr;
			projectRecord["id"] = projId;
			projectRecord["owner_id"] = (Guid?)projOwnerId;

			var statusRecord = new EntityRecord();
			statusRecord["label"] = statusLabel;

			var typeRecord = new EntityRecord();
			typeRecord["label"] = typeLabel;

			var task = new EntityRecord();
			task["id"] = taskId;
			task["number"] = number;
			task["subject"] = subject;
			task["body"] = body;
			task["priority"] = priority;
			task["$project_nn_task"] = new List<EntityRecord> { projectRecord };
			task["$task_status_1n_task"] = new List<EntityRecord> { statusRecord };
			task["$task_type_1n_task"] = new List<EntityRecord> { typeRecord };
			if (ownerId.HasValue)
				task["owner_id"] = ownerId.Value;
			if (createdBy.HasValue)
				task["created_by"] = createdBy.Value;
			if (statusId.HasValue)
				task["status_id"] = statusId.Value;

			return task;
		}

		/// <summary>
		/// Creates a QueryResponse wrapping task data, simulating RecordManager.Find() result.
		/// </summary>
		private static QueryResponse CreateFindResponse(params EntityRecord[] records)
		{
			var response = new QueryResponse();
			response.Success = true;
			response.Object = new QueryResult
			{
				Data = records.ToList()
			};
			return response;
		}

		/// <summary>
		/// Creates a failed QueryResponse.
		/// </summary>
		private static QueryResponse CreateFailedResponse(string message = "Operation failed")
		{
			return new QueryResponse
			{
				Success = false,
				Message = message,
				Object = new QueryResult { Data = new List<EntityRecord>() }
			};
		}

		/// <summary>
		/// Creates a successful QueryResponse for update operations.
		/// </summary>
		private static QueryResponse CreateSuccessResponse()
		{
			return new QueryResponse { Success = true };
		}

		/// <summary>
		/// Creates an EntityRecordList from the given records.
		/// </summary>
		private static EntityRecordList CreateEqlResult(params EntityRecord[] records)
		{
			var list = new EntityRecordList();
			list.AddRange(records);
			list.TotalCount = records.Length;
			return list;
		}

		/// <summary>
		/// Creates a task status EntityRecord.
		/// </summary>
		private static EntityRecord CreateStatusRecord(Guid? id = null, string label = "open", bool isClosed = false)
		{
			var status = new EntityRecord();
			status["id"] = id ?? Guid.NewGuid();
			status["label"] = label;
			status["is_closed"] = isClosed;
			return status;
		}

		/// <summary>
		/// Sets up _mockRecordManager.Find() to return a QueryResponse with given records.
		/// </summary>
		private void SetupFindResponse(params EntityRecord[] records)
		{
			_mockRecordManager
				.Setup(x => x.Find(It.IsAny<EntityQuery>()))
				.Returns(CreateFindResponse(records));
		}

		/// <summary>
		/// Sets up _mockRecordManager.UpdateRecord() to return success.
		/// </summary>
		private void SetupUpdateSuccess()
		{
			_mockRecordManager
				.Setup(x => x.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Returns(CreateSuccessResponse());
		}

		/// <summary>
		/// Sets up EntityRelationManager.Read(name) to return a relation.
		/// </summary>
		private void SetupRelationRead(string name, Guid? relationId = null)
		{
			var relation = new EntityRelation
			{
				Id = relationId ?? Guid.NewGuid(),
				Name = name
			};
			_mockRelationManager
				.Setup(x => x.Read(name))
				.Returns(new EntityRelationResponse { Success = true, Object = relation });
		}

		/// <summary>
		/// Sets up CreateRelationManyToManyRecord to return success.
		/// </summary>
		private void SetupCreateRelationSuccess()
		{
			_mockRecordManager
				.Setup(x => x.CreateRelationManyToManyRecord(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
				.Returns(CreateSuccessResponse());
		}

		/// <summary>
		/// Sets up RemoveRelationManyToManyRecord to return success.
		/// </summary>
		private void SetupRemoveRelationSuccess()
		{
			_mockRecordManager
				.Setup(x => x.RemoveRelationManyToManyRecord(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid?>()))
				.Returns(CreateSuccessResponse());
		}

		#endregion

		#region << SetCalculationFields Tests >>

		/// <summary>
		/// KEY BUSINESS RULE: Key generation formula is {project_abbreviation}-{task_number}.
		/// The number is cast to decimal and formatted with "N0" (thousands separator, no decimals).
		/// Source line 76: projectAbbr + "-" + ((decimal)taskRecord["number"]).ToString("N0")
		/// </summary>
		[Fact]
		public void SetCalculationFields_ShouldGenerateKeyFromProjectAbbrAndTaskNumber()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var task = CreateTaskRecord(id: taskId, number: 42m, projectAbbr: "PRJ");
			SetupFindResponse(task);

			// Act
			var result = _sut.SetCalculationFields(taskId, out string subject, out Guid projectId, out Guid? projectOwnerId);

			// Assert
			result["key"].Should().Be("PRJ-42");
			result["id"].Should().Be(taskId);
		}

		/// <summary>
		/// When RecordManager.Find returns Success = false, SetCalculationFields throws
		/// Exception with the response message. Source line 36.
		/// </summary>
		[Fact]
		public void SetCalculationFields_WhenTaskNotFound_ShouldThrowException()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			_mockRecordManager
				.Setup(x => x.Find(It.IsAny<EntityQuery>()))
				.Returns(CreateFailedResponse("Task query failed"));

			// Act & Assert
			Action act = () => _sut.SetCalculationFields(taskId, out _, out _, out _);
			act.Should().Throw<Exception>().WithMessage("Task query failed");
		}

		/// <summary>
		/// When Find returns success but empty data, throws "Task with this Id was not found".
		/// Source line 38.
		/// </summary>
		[Fact]
		public void SetCalculationFields_WhenNoTaskData_ShouldThrowException()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			SetupFindResponse(); // empty data

			// Act & Assert
			Action act = () => _sut.SetCalculationFields(taskId, out _, out _, out _);
			act.Should().Throw<Exception>().WithMessage("Task with this Id was not found");
		}

		/// <summary>
		/// Validates that out parameters (subject, projectId, projectOwnerId) are correctly
		/// extracted from the task's related project record. Source lines 41, 54-56.
		/// </summary>
		[Fact]
		public void SetCalculationFields_ShouldReturnProjectIdAndOwnerIdViaOutParams()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var projId = Guid.NewGuid();
			var projOwnerId = Guid.NewGuid();
			var task = CreateTaskRecord(
				id: taskId, number: 1m, subject: "Important task",
				projectAbbr: "IMP", projectId: projId, projectOwnerId: projOwnerId);
			SetupFindResponse(task);

			// Act
			_sut.SetCalculationFields(taskId, out string subject, out Guid projectId, out Guid? projectOwnerId);

			// Assert
			subject.Should().Be("Important task");
			projectId.Should().Be(projId);
			projectOwnerId.Should().Be(projOwnerId);
		}

		/// <summary>
		/// When the task has no related project ($project_nn_task is empty),
		/// projectId should be Guid.Empty and projectOwnerId should be null. Source lines 28-30.
		/// </summary>
		[Fact]
		public void SetCalculationFields_WhenNoProject_ShouldReturnEmptyProjectId()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var task = new EntityRecord();
			task["id"] = taskId;
			task["number"] = 1m;
			task["subject"] = "No project task";
			task["$project_nn_task"] = new List<EntityRecord>(); // empty
			task["$task_status_1n_task"] = new List<EntityRecord>();
			task["$task_type_1n_task"] = new List<EntityRecord>();
			SetupFindResponse(task);

			// Act
			var result = _sut.SetCalculationFields(taskId, out string subject, out Guid projectId, out Guid? projectOwnerId);

			// Assert
			projectId.Should().Be(Guid.Empty);
			projectOwnerId.Should().BeNull();
			result["key"].Should().Be("-1"); // empty abbr + "-" + 1
		}

		/// <summary>
		/// Verifies that status and type labels are extracted from relation data without error.
		/// Source lines 58-73: $task_status_1n_task.label and $task_type_1n_task.label.
		/// </summary>
		[Fact]
		public void SetCalculationFields_ShouldExtractStatusAndTypeFromRelations()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var task = CreateTaskRecord(id: taskId, number: 5m, projectAbbr: "TST",
				statusLabel: "in progress", typeLabel: "feature");
			SetupFindResponse(task);

			// Act — should not throw even with status/type data present
			var result = _sut.SetCalculationFields(taskId, out string subject, out Guid projectId, out Guid? projectOwnerId);

			// Assert — key is still generated correctly
			result["key"].Should().Be("TST-5");
		}

		#endregion

		#region << GetTaskStatuses Tests >>

		/// <summary>
		/// GetTaskStatuses should return all task_status records via EQL.
		/// Source lines 81-91.
		/// </summary>
		[Fact]
		public void GetTaskStatuses_ShouldReturnAllStatuses()
		{
			// Arrange
			var status1 = CreateStatusRecord(label: "open", isClosed: false);
			var status2 = CreateStatusRecord(label: "closed", isClosed: true);
			var status3 = CreateStatusRecord(label: "in progress", isClosed: false);
			EnqueueEqlResult(CreateEqlResult(status1, status2, status3));

			// Act
			var result = _sut.GetTaskStatuses();

			// Assert
			result.Should().HaveCount(3);
			_capturedEqlCalls.Should().HaveCount(1);
			_capturedEqlCalls[0].Text.Should().Contain("SELECT * from task_status");
		}

		/// <summary>
		/// When no task statuses are found, throws "Error: No task statuses found".
		/// Source line 88.
		/// </summary>
		[Fact]
		public void GetTaskStatuses_WhenEmpty_ShouldThrowException()
		{
			// Arrange
			EnqueueEqlResult(new EntityRecordList()); // empty

			// Act & Assert
			Action act = () => _sut.GetTaskStatuses();
			act.Should().Throw<Exception>().WithMessage("Error: No task statuses found");
		}

		#endregion

		#region << GetTask Tests >>

		/// <summary>
		/// GetTask should return the task record when found. Source lines 93-104.
		/// </summary>
		[Fact]
		public void GetTask_WhenFound_ShouldReturnTask()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var task = new EntityRecord();
			task["id"] = taskId;
			task["subject"] = "Found task";
			EnqueueEqlResult(CreateEqlResult(task));

			// Act
			var result = _sut.GetTask(taskId);

			// Assert
			result.Should().NotBeNull();
			result["subject"].Should().Be("Found task");
			_capturedEqlCalls[0].Text.Should().Contain("SELECT * from task WHERE id = @taskId");
		}

		/// <summary>
		/// GetTask returns null when no task is found. Source line 101.
		/// </summary>
		[Fact]
		public void GetTask_WhenNotFound_ShouldReturnNull()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			EnqueueEqlResult(new EntityRecordList()); // empty

			// Act
			var result = _sut.GetTask(taskId);

			// Assert
			result.Should().BeNull();
		}

		#endregion

		#region << GetTaskQueue Tests >>

		/// <summary>
		/// TasksDueType.All should produce EQL with no WHERE clause and no ORDER BY.
		/// Source lines 184-185: "No sort for optimization purposes"
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithTasksDueTypeAll_ShouldNotAddStatusOrTimeFilters()
		{
			// Arrange
			EnqueueEqlResult(new EntityRecordList());

			// Act
			var result = _sut.GetTaskQueue(null, null, TasksDueType.All);

			// Assert
			_capturedEqlCalls.Should().HaveCount(1);
			var eql = _capturedEqlCalls[0].Text;
			eql.Should().Contain("SELECT * from task");
			eql.Should().NotContain("WHERE");
			eql.Should().NotContain("ORDER BY");
		}

		/// <summary>
		/// StartTimeDue filter: (start_time &lt; @currentDateEnd OR start_time = null).
		/// KEY BUSINESS RULE: Due start time includes null start_time. Source line 123.
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithStartTimeDue_ShouldFilterStartTimeBeforeEndOfToday()
		{
			// Arrange — status query returns no closed statuses
			EnqueueEqlResult(CreateEqlResult(CreateStatusRecord(isClosed: false)));
			EnqueueEqlResult(new EntityRecordList()); // task query result

			// Act
			_sut.GetTaskQueue(null, null, TasksDueType.StartTimeDue);

			// Assert — second call is the task query
			var eql = _capturedEqlCalls.Last().Text;
			eql.Should().Contain("(start_time < @currentDateEnd OR start_time = null)");
			eql.Should().Contain("ORDER BY end_time ASC, priority DESC");
		}

		/// <summary>
		/// StartTimeNotDue filter: start_time &gt; @currentDateEnd. Source line 125.
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithStartTimeNotDue_ShouldFilterStartTimeAfterEndOfToday()
		{
			// Arrange
			EnqueueEqlResult(CreateEqlResult(CreateStatusRecord(isClosed: false)));
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTaskQueue(null, null, TasksDueType.StartTimeNotDue);

			// Assert
			var eql = _capturedEqlCalls.Last().Text;
			eql.Should().Contain("start_time > @currentDateEnd");
			eql.Should().Contain("ORDER BY end_time ASC, priority DESC");
		}

		/// <summary>
		/// EndTimeOverdue filter: end_time &lt; @currentDateStart. Source line 129.
		/// ORDER BY end_time ASC, priority DESC. Source line 188.
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithEndTimeOverdue_ShouldFilterEndTimeBeforeToday()
		{
			// Arrange
			EnqueueEqlResult(CreateEqlResult(CreateStatusRecord(isClosed: false)));
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTaskQueue(null, null, TasksDueType.EndTimeOverdue);

			// Assert
			var eql = _capturedEqlCalls.Last().Text;
			eql.Should().Contain("end_time < @currentDateStart");
			eql.Should().Contain("ORDER BY end_time ASC, priority DESC");
		}

		/// <summary>
		/// EndTimeDueToday filter: (end_time &gt;= @currentDateStart AND end_time &lt; @currentDateEnd).
		/// Source line 131. ORDER BY priority DESC. Source line 191.
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithEndTimeDueToday_ShouldFilterEndTimeWithinToday()
		{
			// Arrange
			EnqueueEqlResult(CreateEqlResult(CreateStatusRecord(isClosed: false)));
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTaskQueue(null, null, TasksDueType.EndTimeDueToday);

			// Assert
			var eql = _capturedEqlCalls.Last().Text;
			eql.Should().Contain("(end_time >= @currentDateStart AND end_time < @currentDateEnd)");
			eql.Should().Contain("ORDER BY priority DESC");
		}

		/// <summary>
		/// EndTimeNotDue filter: (end_time &gt;= @currentDateEnd OR end_time = null). Source line 133.
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithEndTimeNotDue_ShouldFilterEndTimeAfterToday()
		{
			// Arrange
			EnqueueEqlResult(CreateEqlResult(CreateStatusRecord(isClosed: false)));
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTaskQueue(null, null, TasksDueType.EndTimeNotDue);

			// Assert
			var eql = _capturedEqlCalls.Last().Text;
			eql.Should().Contain("(end_time >= @currentDateEnd OR end_time = null)");
		}

		/// <summary>
		/// When projectId is provided, EQL should contain $project_nn_task.id = @projectId.
		/// Source line 138/144.
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithProjectId_ShouldAddProjectFilter()
		{
			// Arrange
			var projectId = Guid.NewGuid();
			EnqueueEqlResult(new EntityRecordList()); // All type — no status query

			// Act
			_sut.GetTaskQueue(projectId, null, TasksDueType.All);

			// Assert
			var eql = _capturedEqlCalls[0].Text;
			eql.Should().Contain("$project_nn_task.id = @projectId");
			var projectParam = _capturedEqlCalls[0].Parameters.FirstOrDefault(p => p.ParameterName == "@projectId");
			projectParam.Should().NotBeNull();
		}

		/// <summary>
		/// When userId is provided, EQL should contain owner_id = @userId. Source line 149.
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithUserId_ShouldAddOwnerFilter()
		{
			// Arrange
			var userId = Guid.NewGuid();
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTaskQueue(null, userId, TasksDueType.All);

			// Assert
			var eql = _capturedEqlCalls[0].Text;
			eql.Should().Contain("owner_id = @userId");
		}

		/// <summary>
		/// When both projectId and userId are provided, both filters should be combined.
		/// Source line 138: $project_nn_task.id = @projectId AND owner_id = @userId
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithBothProjectAndUser_ShouldCombineFilters()
		{
			// Arrange
			var projectId = Guid.NewGuid();
			var userId = Guid.NewGuid();
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTaskQueue(projectId, userId, TasksDueType.All);

			// Assert
			var eql = _capturedEqlCalls[0].Text;
			eql.Should().Contain("$project_nn_task.id = @projectId AND owner_id = @userId");
		}

		/// <summary>
		/// KEY BUSINESS RULE: For non-All types, closed statuses are excluded from the queue.
		/// Source lines 156-172: dynamic status_id &lt;&gt; @statusN parameters.
		/// </summary>
		[Fact]
		public void GetTaskQueue_NonAllType_ShouldExcludeClosedStatuses()
		{
			// Arrange — 3 statuses, 2 of which are closed
			var closedId1 = Guid.NewGuid();
			var closedId2 = Guid.NewGuid();
			EnqueueEqlResult(CreateEqlResult(
				CreateStatusRecord(label: "open", isClosed: false),
				CreateStatusRecord(id: closedId1, label: "closed", isClosed: true),
				CreateStatusRecord(id: closedId2, label: "archived", isClosed: true)
			));
			EnqueueEqlResult(new EntityRecordList()); // task query

			// Act
			_sut.GetTaskQueue(null, null, TasksDueType.EndTimeOverdue);

			// Assert
			var eql = _capturedEqlCalls.Last().Text;
			eql.Should().Contain("status_id <> @status1");
			eql.Should().Contain("status_id <> @status2");
		}

		/// <summary>
		/// When limit is specified, EQL should contain PAGE 1 PAGESIZE {limit}.
		/// Source line 209.
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithLimit_ShouldAddPagingClause()
		{
			// Arrange
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTaskQueue(null, null, TasksDueType.All, limit: 10);

			// Assert
			_capturedEqlCalls[0].Text.Should().Contain("PAGE 1 PAGESIZE 10");
		}

		/// <summary>
		/// When limit is null, no PAGE clause should be present.
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithNullLimit_ShouldNotAddPaging()
		{
			// Arrange
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTaskQueue(null, null, TasksDueType.All, limit: null);

			// Assert
			_capturedEqlCalls[0].Text.Should().NotContain("PAGE");
			_capturedEqlCalls[0].Text.Should().NotContain("PAGESIZE");
		}

		/// <summary>
		/// When includeProjectData is true, selected fields should include $project_nn_task.is_billable.
		/// Source lines 108-112.
		/// </summary>
		[Fact]
		public void GetTaskQueue_WithIncludeProjectData_ShouldAddProjectRelationField()
		{
			// Arrange
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTaskQueue(null, null, TasksDueType.All, includeProjectData: true);

			// Assert
			_capturedEqlCalls[0].Text.Should().Contain("$project_nn_task.is_billable");
		}

		#endregion

		#region << GetTaskIconAndColor Tests >>

		/// <summary>
		/// When a known priority value matches a SelectOption, icon class and color are resolved.
		/// Source lines 217-230.
		/// </summary>
		[Fact]
		public void GetTaskIconAndColor_WithKnownPriority_ShouldReturnIconAndColor()
		{
			// Arrange
			var selectField = new SelectField
			{
				Name = "priority",
				Options = new List<SelectOption>
				{
					new SelectOption("high", "High", "fa fa-exclamation", "#ff0000"),
					new SelectOption("medium", "Medium", "fa fa-minus", "#ffaa00"),
					new SelectOption("low", "Low", "fa fa-arrow-down", "#00ff00")
				}
			};
			var entity = new Entity
			{
				Fields = new List<Field> { selectField }
			};
			_mockEntityManager
				.Setup(x => x.ReadEntity("task"))
				.Returns(new EntityResponse { Success = true, Object = entity });

			var record = new EntityRecord();
			record["priority"] = "high";

			// Act
			_sut.GetTaskIconAndColor(record);

			// Assert
			record["icon_class"].Should().Be("fa fa-exclamation");
			record["color"].Should().Be("#ff0000");
		}

		/// <summary>
		/// When priority value doesn't match any option, defaults to iconClass="" and color="#fff".
		/// Source lines 219-220.
		/// </summary>
		[Fact]
		public void GetTaskIconAndColor_WithUnknownPriority_ShouldReturnDefaults()
		{
			// Arrange
			var selectField = new SelectField
			{
				Name = "priority",
				Options = new List<SelectOption>
				{
					new SelectOption("high", "High", "fa fa-exclamation", "#ff0000")
				}
			};
			var entity = new Entity
			{
				Fields = new List<Field> { selectField }
			};
			_mockEntityManager
				.Setup(x => x.ReadEntity("task"))
				.Returns(new EntityResponse { Success = true, Object = entity });

			var record = new EntityRecord();
			record["priority"] = "unknown_priority";

			// Act
			_sut.GetTaskIconAndColor(record);

			// Assert
			record["icon_class"].Should().Be("");
			record["color"].Should().Be("#fff");
		}

		#endregion

		#region << StartTaskTimelog / StopTaskTimelog Tests >>

		/// <summary>
		/// StartTaskTimelog should set timelog_started_on to approximately DateTime.Now.
		/// Source line 236.
		/// </summary>
		[Fact]
		public void StartTaskTimelog_ShouldSetTimelogStartedOn()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			EntityRecord capturedRecord = null;
			_mockRecordManager
				.Setup(x => x.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((name, record) => capturedRecord = record)
				.Returns(CreateSuccessResponse());

			// Act
			var before = DateTime.Now;
			_sut.StartTaskTimelog(taskId);
			var after = DateTime.Now;

			// Assert
			capturedRecord.Should().NotBeNull();
			capturedRecord["id"].Should().Be(taskId);
			var startedOn = (DateTime)capturedRecord["timelog_started_on"];
			startedOn.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
			_mockRecordManager.Verify(x => x.UpdateRecord("task", It.IsAny<EntityRecord>()), Times.Once);
		}

		/// <summary>
		/// When UpdateRecord fails, StartTaskTimelog should throw Exception.
		/// Source line 239.
		/// </summary>
		[Fact]
		public void StartTaskTimelog_WhenUpdateFails_ShouldThrowException()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			_mockRecordManager
				.Setup(x => x.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Returns(CreateFailedResponse("Update failed"));

			// Act & Assert
			Action act = () => _sut.StartTaskTimelog(taskId);
			act.Should().Throw<Exception>().WithMessage("Update failed");
		}

		/// <summary>
		/// StopTaskTimelog should set timelog_started_on to null. Source line 247.
		/// </summary>
		[Fact]
		public void StopTaskTimelog_ShouldNullifyTimelogStartedOn()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			EntityRecord capturedRecord = null;
			_mockRecordManager
				.Setup(x => x.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((name, record) => capturedRecord = record)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.StopTaskTimelog(taskId);

			// Assert
			capturedRecord.Should().NotBeNull();
			capturedRecord["id"].Should().Be(taskId);
			capturedRecord["timelog_started_on"].Should().BeNull();
		}

		/// <summary>
		/// When UpdateRecord fails, StopTaskTimelog should throw Exception.
		/// Source line 250.
		/// </summary>
		[Fact]
		public void StopTaskTimelog_WhenUpdateFails_ShouldThrowException()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			_mockRecordManager
				.Setup(x => x.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Returns(CreateFailedResponse("Stop failed"));

			// Act & Assert
			Action act = () => _sut.StopTaskTimelog(taskId);
			act.Should().Throw<Exception>().WithMessage("Stop failed");
		}

		#endregion

		#region << PreCreateRecordPageHookLogic Tests >>

		/// <summary>
		/// When record doesn't have $project_nn_task.id property, should add error.
		/// Source lines 302-308.
		/// </summary>
		[Fact]
		public void PreCreateRecordPageHookLogic_WhenNoProjectProperty_ShouldAddError()
		{
			// Arrange
			var record = new EntityRecord();
			// No $project_nn_task.id property
			var errors = new List<ErrorModel>();

			// Act
			_sut.PreCreateRecordPageHookLogic(record, errors);

			// Assert
			errors.Should().HaveCount(1);
			errors[0].Key.Should().Be("$project_nn_task.id");
			errors[0].Message.Should().Be("Project is not specified.");
		}

		/// <summary>
		/// When $project_nn_task.id is an empty list, should add "Project is not specified." error.
		/// Source lines 314-319.
		/// </summary>
		[Fact]
		public void PreCreateRecordPageHookLogic_WhenEmptyProjectList_ShouldAddError()
		{
			// Arrange
			var record = new EntityRecord();
			record["$project_nn_task.id"] = new List<Guid>();
			var errors = new List<ErrorModel>();

			// Act
			_sut.PreCreateRecordPageHookLogic(record, errors);

			// Assert
			errors.Should().HaveCount(1);
			errors[0].Key.Should().Be("$project_nn_task.id");
			errors[0].Message.Should().Be("Project is not specified.");
		}

		/// <summary>
		/// KEY BUSINESS RULE: Exactly one project required per task.
		/// When multiple projects are selected, should add "More than one project is selected." error.
		/// Source lines 321-328.
		/// </summary>
		[Fact]
		public void PreCreateRecordPageHookLogic_WhenMultipleProjects_ShouldAddError()
		{
			// Arrange
			var record = new EntityRecord();
			record["$project_nn_task.id"] = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
			var errors = new List<ErrorModel>();

			// Act
			_sut.PreCreateRecordPageHookLogic(record, errors);

			// Assert
			errors.Should().HaveCount(1);
			errors[0].Key.Should().Be("$project_nn_task.id");
			errors[0].Message.Should().Be("More than one project is selected.");
		}

		/// <summary>
		/// When exactly one project is selected, no errors should be added.
		/// </summary>
		[Fact]
		public void PreCreateRecordPageHookLogic_WithSingleProject_ShouldNotAddErrors()
		{
			// Arrange
			var record = new EntityRecord();
			record["$project_nn_task.id"] = new List<Guid> { Guid.NewGuid() };
			var errors = new List<ErrorModel>();

			// Act
			_sut.PreCreateRecordPageHookLogic(record, errors);

			// Assert
			errors.Should().BeEmpty();
		}

		#endregion

		#region << PostCreateApiHookLogic Tests >>

		/// <summary>
		/// PostCreateApiHookLogic should call SetCalculationFields and UpdateRecord.
		/// Source lines 338-341.
		/// </summary>
		[Fact]
		public void PostCreateApiHookLogic_ShouldCallSetCalculationFieldsAndUpdateRecord()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var ownerId = Guid.NewGuid();
			var projId = Guid.NewGuid();
			var projOwnerId = Guid.NewGuid();

			var task = CreateTaskRecord(id: taskId, number: 10m, projectAbbr: "TST",
				projectId: projId, projectOwnerId: projOwnerId,
				ownerId: ownerId, createdBy: ownerId);

			// SetCalculationFields uses _recordManager.Find
			SetupFindResponse(task);
			SetupUpdateSuccess();
			SetupRelationRead("user_nn_task_watchers");
			SetupCreateRelationSuccess();
			_mockFeedService.Setup(x => x.Create(
				It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
				It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
				It.IsAny<List<string>>(), It.IsAny<string>()));

			var record = new EntityRecord();
			record["id"] = taskId;
			record["owner_id"] = ownerId;
			record["created_by"] = ownerId;
			record["body"] = "<p>Test</p>";

			using (SecurityContext.OpenScope(new ErpUser { Id = ownerId }))
			{
				// Act
				_sut.PostCreateApiHookLogic(record);
			}

			// Assert
			_mockRecordManager.Verify(x => x.UpdateRecord("task", It.IsAny<EntityRecord>()), Times.Once);
			_mockRecordManager.Verify(x => x.Find(It.IsAny<EntityQuery>()), Times.Once);
		}

		/// <summary>
		/// KEY BUSINESS RULE: Initial watchers = owner + creator + project owner.
		/// All 3 unique IDs should be in the watchers list. Source lines 344-367.
		/// </summary>
		[Fact]
		public void PostCreateApiHookLogic_ShouldBuildWatcherListFromOwnerCreatorAndProjectOwner()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var ownerId = Guid.NewGuid();
			var creatorId = Guid.NewGuid();
			var projOwnerId = Guid.NewGuid();
			var projId = Guid.NewGuid();

			var task = CreateTaskRecord(id: taskId, number: 1m, projectAbbr: "TST",
				projectId: projId, projectOwnerId: projOwnerId,
				ownerId: ownerId, createdBy: creatorId);
			SetupFindResponse(task);
			SetupUpdateSuccess();

			var watchRelId = Guid.NewGuid();
			SetupRelationRead("user_nn_task_watchers", watchRelId);
			SetupCreateRelationSuccess();
			_mockFeedService.Setup(x => x.Create(
				It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
				It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
				It.IsAny<List<string>>(), It.IsAny<string>()));

			var record = new EntityRecord();
			record["id"] = taskId;
			record["owner_id"] = ownerId;
			record["created_by"] = creatorId;
			record["body"] = "<p>Content</p>";

			using (SecurityContext.OpenScope(new ErpUser { Id = creatorId }))
			{
				// Act
				_sut.PostCreateApiHookLogic(record);
			}

			// Assert — 3 unique watcher relations created (owner, creator, project owner)
			_mockRecordManager.Verify(
				x => x.CreateRelationManyToManyRecord(watchRelId, It.IsAny<Guid>(), taskId),
				Times.Exactly(3));
		}

		/// <summary>
		/// When owner == creator, watchers should only have 2 entries (deduplicated).
		/// Source lines 350-351, 359-360.
		/// </summary>
		[Fact]
		public void PostCreateApiHookLogic_ShouldDeduplicateWatchers()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var sameUserId = Guid.NewGuid();
			var projOwnerId = Guid.NewGuid();
			var projId = Guid.NewGuid();

			var task = CreateTaskRecord(id: taskId, number: 1m, projectAbbr: "TST",
				projectId: projId, projectOwnerId: projOwnerId,
				ownerId: sameUserId, createdBy: sameUserId);
			SetupFindResponse(task);
			SetupUpdateSuccess();

			var watchRelId = Guid.NewGuid();
			SetupRelationRead("user_nn_task_watchers", watchRelId);
			SetupCreateRelationSuccess();
			_mockFeedService.Setup(x => x.Create(
				It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
				It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
				It.IsAny<List<string>>(), It.IsAny<string>()));

			var record = new EntityRecord();
			record["id"] = taskId;
			record["owner_id"] = sameUserId;
			record["created_by"] = sameUserId;
			record["body"] = "";

			using (SecurityContext.OpenScope(new ErpUser { Id = sameUserId }))
			{
				// Act
				_sut.PostCreateApiHookLogic(record);
			}

			// Assert — only 2 watcher relations: sameUserId + projOwnerId (owner=creator deduplicated)
			_mockRecordManager.Verify(
				x => x.CreateRelationManyToManyRecord(watchRelId, It.IsAny<Guid>(), taskId),
				Times.Exactly(2));
		}

		/// <summary>
		/// KEY BUSINESS RULE: Watcher relations created on task creation.
		/// Verifies CreateRelationManyToManyRecord is called for each watcher.
		/// Source lines 370-379.
		/// </summary>
		[Fact]
		public void PostCreateApiHookLogic_ShouldCreateWatcherRelations()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var ownerId = Guid.NewGuid();
			var projId = Guid.NewGuid();

			var task = CreateTaskRecord(id: taskId, number: 1m, projectAbbr: "TST",
				projectId: projId, projectOwnerId: ownerId,
				ownerId: ownerId, createdBy: ownerId);
			SetupFindResponse(task);
			SetupUpdateSuccess();

			var watchRelId = Guid.NewGuid();
			SetupRelationRead("user_nn_task_watchers", watchRelId);
			SetupCreateRelationSuccess();
			_mockFeedService.Setup(x => x.Create(
				It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
				It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
				It.IsAny<List<string>>(), It.IsAny<string>()));

			var record = new EntityRecord();
			record["id"] = taskId;
			record["owner_id"] = ownerId;
			record["created_by"] = ownerId;
			record["body"] = "";

			using (SecurityContext.OpenScope(new ErpUser { Id = ownerId }))
			{
				// Act
				_sut.PostCreateApiHookLogic(record);
			}

			// Assert — single unique watcher (all same user)
			_mockRecordManager.Verify(
				x => x.CreateRelationManyToManyRecord(watchRelId, ownerId, taskId),
				Times.Once);
		}

		/// <summary>
		/// When user_nn_task_watchers relation is not found, throws "Watch relation not found".
		/// Source lines 371-372.
		/// </summary>
		[Fact]
		public void PostCreateApiHookLogic_WhenWatchRelationNotFound_ShouldThrowException()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var ownerId = Guid.NewGuid();
			var projId = Guid.NewGuid();

			var task = CreateTaskRecord(id: taskId, number: 1m, projectAbbr: "TST",
				projectId: projId, projectOwnerId: ownerId,
				ownerId: ownerId, createdBy: ownerId);
			SetupFindResponse(task);
			SetupUpdateSuccess();

			// Return null object for the relation
			_mockRelationManager
				.Setup(x => x.Read("user_nn_task_watchers"))
				.Returns(new EntityRelationResponse { Success = true, Object = null });

			var record = new EntityRecord();
			record["id"] = taskId;
			record["owner_id"] = ownerId;
			record["created_by"] = ownerId;
			record["body"] = "";

			using (SecurityContext.OpenScope(new ErpUser { Id = ownerId }))
			{
				// Act & Assert
				Action act = () => _sut.PostCreateApiHookLogic(record);
				act.Should().Throw<Exception>().WithMessage("Watch relation not found");
			}
		}

		/// <summary>
		/// Verifies FeedService.Create is called with type="task" and correct subject format.
		/// Source lines 383, 393.
		/// </summary>
		[Fact]
		public void PostCreateApiHookLogic_ShouldCreateFeedItemWithTaskType()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var ownerId = Guid.NewGuid();
			var projId = Guid.NewGuid();

			var task = CreateTaskRecord(id: taskId, number: 7m, subject: "My task",
				projectAbbr: "ABC", projectId: projId, projectOwnerId: ownerId,
				ownerId: ownerId, createdBy: ownerId);
			SetupFindResponse(task);
			SetupUpdateSuccess();
			SetupRelationRead("user_nn_task_watchers");
			SetupCreateRelationSuccess();

			string capturedType = null;
			string capturedSubject = null;
			_mockFeedService.Setup(x => x.Create(
				It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
				It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
				It.IsAny<List<string>>(), It.IsAny<string>()))
				.Callback<Guid?, Guid?, DateTime?, string, string, List<string>, List<string>, string>(
					(id, by, on, subj, body, rel, scope, typ) =>
					{
						capturedType = typ;
						capturedSubject = subj;
					});

			var record = new EntityRecord();
			record["id"] = taskId;
			record["owner_id"] = ownerId;
			record["created_by"] = ownerId;
			record["body"] = "<p>Body</p>";

			using (SecurityContext.OpenScope(new ErpUser { Id = ownerId }))
			{
				// Act
				_sut.PostCreateApiHookLogic(record);
			}

			// Assert
			capturedType.Should().Be("task");
			capturedSubject.Should().Contain("created");
			capturedSubject.Should().Contain("[ABC-7]");
			capturedSubject.Should().Contain("My task");
			capturedSubject.Should().Contain($"/projects/tasks/tasks/r/{taskId}/details");
		}

		#endregion

		#region << PostPreUpdateApiHookLogic Tests >>

		/// <summary>
		/// KEY BUSINESS RULE: Project change requires relation swap.
		/// When project changes, should remove old and "create" new relation.
		/// NOTE: Source code uses RemoveRelationManyToManyRecord for BOTH operations
		/// (preserved bug from monolith, line 454). Source lines 449, 454.
		/// </summary>
		[Fact]
		public void PostPreUpdateApiHookLogic_WhenProjectChanges_ShouldRemoveOldAndCreateNewRelation()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var oldProjectId = Guid.NewGuid();
			var newProjectId = Guid.NewGuid();
			var oldProjAbbr = "OLD";
			var newProjOwnerId = Guid.NewGuid();

			// EQL result for old task data
			var oldRecord = new EntityRecord();
			oldRecord["id"] = taskId;
			oldRecord["number"] = 5m;
			var projRec = new EntityRecord();
			projRec["id"] = oldProjectId;
			projRec["abbr"] = oldProjAbbr;
			oldRecord["$project_nn_task"] = new List<EntityRecord> { projRec };
			oldRecord["$user_nn_task_watchers"] = new List<EntityRecord>();

			EnqueueEqlResult(CreateEqlResult(oldRecord)); // first EQL: old task data

			// EQL result for new project owner lookup
			var newProjRec = new EntityRecord();
			newProjRec["id"] = newProjectId;
			newProjRec["owner_id"] = newProjOwnerId;
			EnqueueEqlResult(CreateEqlResult(newProjRec)); // second EQL: project owner

			var projectTaskRel = new EntityRelation { Id = Guid.NewGuid(), Name = "project_nn_task" };
			var watcherRel = new EntityRelation { Id = Guid.NewGuid(), Name = "user_nn_task_watchers" };
			_mockRelationManager
				.Setup(x => x.Read("project_nn_task"))
				.Returns(new EntityRelationResponse { Success = true, Object = projectTaskRel });
			_mockRelationManager
				.Setup(x => x.Read("user_nn_task_watchers"))
				.Returns(new EntityRelationResponse { Success = true, Object = watcherRel });
			SetupRemoveRelationSuccess();

			var record = new EntityRecord();
			record["id"] = taskId;
			record["$project_nn_task.id"] = newProjectId;
			var errors = new List<ErrorModel>();

			// Act
			_sut.PostPreUpdateApiHookLogic(record, null, errors);

			// Assert — RemoveRelationManyToManyRecord called for both old (remove) and new (bug: also remove)
			_mockRecordManager.Verify(
				x => x.RemoveRelationManyToManyRecord(projectTaskRel.Id, It.IsAny<Guid?>(), It.IsAny<Guid?>()),
				Times.Exactly(2));
		}

		/// <summary>
		/// When project hasn't changed, no relation updates should occur.
		/// Source line 440.
		/// </summary>
		[Fact]
		public void PostPreUpdateApiHookLogic_WhenProjectNotChanged_ShouldSkipRelationUpdate()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var sameProjectId = Guid.NewGuid();

			var oldRecord = new EntityRecord();
			oldRecord["id"] = taskId;
			oldRecord["number"] = 5m;
			var projRec = new EntityRecord();
			projRec["id"] = sameProjectId;
			projRec["abbr"] = "TST";
			oldRecord["$project_nn_task"] = new List<EntityRecord> { projRec };
			oldRecord["$user_nn_task_watchers"] = new List<EntityRecord>();

			EnqueueEqlResult(CreateEqlResult(oldRecord));

			var record = new EntityRecord();
			record["id"] = taskId;
			record["$project_nn_task.id"] = sameProjectId; // same project
			var errors = new List<ErrorModel>();

			// Act
			_sut.PostPreUpdateApiHookLogic(record, null, errors);

			// Assert — no relation changes
			_mockRecordManager.Verify(
				x => x.RemoveRelationManyToManyRecord(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid?>()),
				Times.Never);
		}

		/// <summary>
		/// When project changes, the task key should be updated with old project abbreviation.
		/// Source line 459: record["key"] = projectAbbr + "-" + number.ToString("N0")
		/// </summary>
		[Fact]
		public void PostPreUpdateApiHookLogic_ShouldUpdateKeyOnProjectChange()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var oldProjectId = Guid.NewGuid();
			var newProjectId = Guid.NewGuid();

			var oldRecord = new EntityRecord();
			oldRecord["id"] = taskId;
			oldRecord["number"] = 42m;
			var projRec = new EntityRecord();
			projRec["id"] = oldProjectId;
			projRec["abbr"] = "OLDP";
			oldRecord["$project_nn_task"] = new List<EntityRecord> { projRec };
			oldRecord["$user_nn_task_watchers"] = new List<EntityRecord>();

			EnqueueEqlResult(CreateEqlResult(oldRecord));

			// New project owner query result
			var newProjRec = new EntityRecord();
			newProjRec["id"] = newProjectId;
			newProjRec["owner_id"] = Guid.NewGuid();
			EnqueueEqlResult(CreateEqlResult(newProjRec));

			var projectTaskRel = new EntityRelation { Id = Guid.NewGuid(), Name = "project_nn_task" };
			var watcherRel = new EntityRelation { Id = Guid.NewGuid(), Name = "user_nn_task_watchers" };
			_mockRelationManager
				.Setup(x => x.Read("project_nn_task"))
				.Returns(new EntityRelationResponse { Success = true, Object = projectTaskRel });
			_mockRelationManager
				.Setup(x => x.Read("user_nn_task_watchers"))
				.Returns(new EntityRelationResponse { Success = true, Object = watcherRel });
			SetupRemoveRelationSuccess();

			var record = new EntityRecord();
			record["id"] = taskId;
			record["$project_nn_task.id"] = newProjectId;
			var errors = new List<ErrorModel>();

			// Act
			_sut.PostPreUpdateApiHookLogic(record, null, errors);

			// Assert — key uses OLD project abbreviation
			record["key"].Should().Be("OLDP-42");
		}

		/// <summary>
		/// KEY BUSINESS RULE: New project owner auto-added to watchers.
		/// Source lines 474-484.
		/// </summary>
		[Fact]
		public void PostPreUpdateApiHookLogic_ShouldAddNewProjectOwnerToWatchers()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var oldProjectId = Guid.NewGuid();
			var newProjectId = Guid.NewGuid();
			var newProjOwnerId = Guid.NewGuid();

			var oldRecord = new EntityRecord();
			oldRecord["id"] = taskId;
			oldRecord["number"] = 1m;
			var projRec = new EntityRecord();
			projRec["id"] = oldProjectId;
			projRec["abbr"] = "X";
			oldRecord["$project_nn_task"] = new List<EntityRecord> { projRec };
			oldRecord["$user_nn_task_watchers"] = new List<EntityRecord>(); // no existing watchers

			EnqueueEqlResult(CreateEqlResult(oldRecord));

			var newProjRec = new EntityRecord();
			newProjRec["id"] = newProjectId;
			newProjRec["owner_id"] = newProjOwnerId;
			EnqueueEqlResult(CreateEqlResult(newProjRec));

			var projectTaskRel = new EntityRelation { Id = Guid.NewGuid(), Name = "project_nn_task" };
			var watcherRel = new EntityRelation { Id = Guid.NewGuid(), Name = "user_nn_task_watchers" };
			_mockRelationManager
				.Setup(x => x.Read("project_nn_task"))
				.Returns(new EntityRelationResponse { Success = true, Object = projectTaskRel });
			_mockRelationManager
				.Setup(x => x.Read("user_nn_task_watchers"))
				.Returns(new EntityRelationResponse { Success = true, Object = watcherRel });
			SetupRemoveRelationSuccess();

			var record = new EntityRecord();
			record["id"] = taskId;
			record["$project_nn_task.id"] = newProjectId;
			var errors = new List<ErrorModel>();

			// Act
			_sut.PostPreUpdateApiHookLogic(record, null, errors);

			// Assert — watcher relation created for new project owner (using RemoveRelation due to bug preservation)
			_mockRecordManager.Verify(
				x => x.RemoveRelationManyToManyRecord(watcherRel.Id, (Guid?)newProjOwnerId, taskId),
				Times.Once);
		}

		/// <summary>
		/// When project owner is already in watcher list, should not create duplicate relation.
		/// Source line 475.
		/// </summary>
		[Fact]
		public void PostPreUpdateApiHookLogic_WhenProjectOwnerAlreadyWatcher_ShouldNotDuplicate()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var oldProjectId = Guid.NewGuid();
			var newProjectId = Guid.NewGuid();
			var existingWatcherId = Guid.NewGuid();

			var watcherRec = new EntityRecord();
			watcherRec["id"] = existingWatcherId;

			var oldRecord = new EntityRecord();
			oldRecord["id"] = taskId;
			oldRecord["number"] = 1m;
			var projRec = new EntityRecord();
			projRec["id"] = oldProjectId;
			projRec["abbr"] = "X";
			oldRecord["$project_nn_task"] = new List<EntityRecord> { projRec };
			oldRecord["$user_nn_task_watchers"] = new List<EntityRecord> { watcherRec };

			EnqueueEqlResult(CreateEqlResult(oldRecord));

			// New project owner is the SAME as existing watcher
			var newProjRec = new EntityRecord();
			newProjRec["id"] = newProjectId;
			newProjRec["owner_id"] = existingWatcherId;
			EnqueueEqlResult(CreateEqlResult(newProjRec));

			var projectTaskRel = new EntityRelation { Id = Guid.NewGuid(), Name = "project_nn_task" };
			_mockRelationManager
				.Setup(x => x.Read("project_nn_task"))
				.Returns(new EntityRelationResponse { Success = true, Object = projectTaskRel });
			SetupRemoveRelationSuccess();

			var record = new EntityRecord();
			record["id"] = taskId;
			record["$project_nn_task.id"] = newProjectId;
			var errors = new List<ErrorModel>();

			// Act
			_sut.PostPreUpdateApiHookLogic(record, null, errors);

			// Assert — no watcher relation created (already exists)
			// RemoveRelation called exactly 2 times for project swap only, NOT for watcher
			_mockRecordManager.Verify(
				x => x.RemoveRelationManyToManyRecord(projectTaskRel.Id, It.IsAny<Guid?>(), It.IsAny<Guid?>()),
				Times.Exactly(2));
			// No call to user_nn_task_watchers relation (would be the 3rd call)
			_mockRelationManager.Verify(x => x.Read("user_nn_task_watchers"), Times.Never);
		}

		#endregion

		#region << PostUpdateApiHookLogic Tests >>

		/// <summary>
		/// PostUpdateApiHookLogic should recalculate fields and update record.
		/// Source lines 498-501.
		/// </summary>
		[Fact]
		public void PostUpdateApiHookLogic_ShouldRecalculateFields()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var task = CreateTaskRecord(id: taskId, number: 3m, projectAbbr: "UPD");
			SetupFindResponse(task);
			SetupUpdateSuccess();

			// EQL for watchers query — return empty (no owner_id in record)
			var record = new EntityRecord();
			record["id"] = taskId;

			// Act
			_sut.PostUpdateApiHookLogic(record, null);

			// Assert
			_mockRecordManager.Verify(x => x.Find(It.IsAny<EntityQuery>()), Times.Once);
			_mockRecordManager.Verify(x => x.UpdateRecord("task", It.IsAny<EntityRecord>()), Times.Once);
		}

		/// <summary>
		/// KEY BUSINESS RULE: Owner auto-added to watchers on update.
		/// When owner is not in current watchers list, CreateRelationManyToManyRecord is called.
		/// Source lines 521-529.
		/// </summary>
		[Fact]
		public void PostUpdateApiHookLogic_WhenOwnerNotInWatchers_ShouldAddOwnerToWatchers()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var newOwnerId = Guid.NewGuid();
			var task = CreateTaskRecord(id: taskId, number: 3m, projectAbbr: "UPD");
			SetupFindResponse(task);
			SetupUpdateSuccess();

			// EQL result for watchers query — task has no existing watchers
			var watcherTask = new EntityRecord();
			watcherTask["id"] = taskId;
			watcherTask["$user_nn_task_watchers"] = new List<EntityRecord>(); // empty watchers
			EnqueueEqlResult(CreateEqlResult(watcherTask));

			var watchRelId = Guid.NewGuid();
			SetupRelationRead("user_nn_task_watchers", watchRelId);
			SetupCreateRelationSuccess();

			var record = new EntityRecord();
			record["id"] = taskId;
			record["owner_id"] = newOwnerId;

			// Act
			_sut.PostUpdateApiHookLogic(record, null);

			// Assert
			_mockRecordManager.Verify(
				x => x.CreateRelationManyToManyRecord(watchRelId, newOwnerId, taskId),
				Times.Once);
		}

		/// <summary>
		/// When owner is already in watchers list, no duplicate relation is created.
		/// Source line 521.
		/// </summary>
		[Fact]
		public void PostUpdateApiHookLogic_WhenOwnerAlreadyWatcher_ShouldNotDuplicate()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var existingOwnerId = Guid.NewGuid();
			var task = CreateTaskRecord(id: taskId, number: 3m, projectAbbr: "UPD");
			SetupFindResponse(task);
			SetupUpdateSuccess();

			// EQL result — owner is already in watchers
			var watcherRec = new EntityRecord();
			watcherRec["id"] = existingOwnerId;
			var watcherTask = new EntityRecord();
			watcherTask["id"] = taskId;
			watcherTask["$user_nn_task_watchers"] = new List<EntityRecord> { watcherRec };
			EnqueueEqlResult(CreateEqlResult(watcherTask));

			var record = new EntityRecord();
			record["id"] = taskId;
			record["owner_id"] = existingOwnerId;

			// Act
			_sut.PostUpdateApiHookLogic(record, null);

			// Assert — no CreateRelationManyToManyRecord call
			_mockRecordManager.Verify(
				x => x.CreateRelationManyToManyRecord(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>()),
				Times.Never);
		}

		#endregion

		#region << SetStatus Tests >>

		/// <summary>
		/// SetStatus should update task's status_id field. Source lines 534-542.
		/// </summary>
		[Fact]
		public void SetStatus_ShouldUpdateTaskStatusId()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var statusId = Guid.NewGuid();
			EntityRecord capturedRecord = null;
			_mockRecordManager
				.Setup(x => x.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((name, record) => capturedRecord = record)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.SetStatus(taskId, statusId);

			// Assert
			capturedRecord.Should().NotBeNull();
			capturedRecord["id"].Should().Be(taskId);
			capturedRecord["status_id"].Should().Be(statusId);
		}

		/// <summary>
		/// When UpdateRecord fails, SetStatus should throw Exception.
		/// </summary>
		[Fact]
		public void SetStatus_WhenUpdateFails_ShouldThrowException()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var statusId = Guid.NewGuid();
			_mockRecordManager
				.Setup(x => x.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Returns(CreateFailedResponse("Status update failed"));

			// Act & Assert
			Action act = () => _sut.SetStatus(taskId, statusId);
			act.Should().Throw<Exception>().WithMessage("Status update failed");
		}

		#endregion

		#region << GetTasksThatNeedStarting Tests >>

		/// <summary>
		/// KEY BUSINESS RULE: Well-known not-started status GUID: f3fdd750-0c16-4215-93b3-5373bd528d1f.
		/// Queries tasks with this status and start_time &lt;= today. Source lines 544-553.
		/// </summary>
		[Fact]
		public void GetTasksThatNeedStarting_ShouldQueryNotStartedTasksWithPastStartDate()
		{
			// Arrange
			var task1 = new EntityRecord();
			task1["id"] = Guid.NewGuid();
			var task2 = new EntityRecord();
			task2["id"] = Guid.NewGuid();
			EnqueueEqlResult(CreateEqlResult(task1, task2));

			// Act
			var result = _sut.GetTasksThatNeedStarting();

			// Assert
			result.Should().HaveCount(2);
			_capturedEqlCalls.Should().HaveCount(1);
			var eql = _capturedEqlCalls[0].Text;
			eql.Should().Contain("SELECT id FROM task WHERE status_id = @notStartedStatusId AND start_time <= @currentDate");

			// Verify hardcoded not-started status GUID
			var statusParam = _capturedEqlCalls[0].Parameters
				.FirstOrDefault(p => p.ParameterName == "@notStartedStatusId");
			statusParam.Should().NotBeNull();
			statusParam.Value.Should().Be(new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f"));

			// Verify currentDate parameter is today's date
			var dateParam = _capturedEqlCalls[0].Parameters
				.FirstOrDefault(p => p.ParameterName == "@currentDate");
			dateParam.Should().NotBeNull();
			((DateTime)dateParam.Value).Date.Should().Be(DateTime.Now.Date);
		}

		#endregion
	}

	#region << TestableTaskService >>

	/// <summary>
	/// Testable subclass of TaskService that overrides the EQL execution path
	/// to enable isolated unit testing without database connectivity.
	///
	/// The <see cref="TaskService.ExecuteEql"/> virtual method (added for testability)
	/// is overridden to delegate to a configurable function, allowing tests to
	/// control the EQL query results returned to business logic methods.
	/// </summary>
	internal class TestableTaskService : TaskService
	{
		private readonly Func<string, List<EqlParameter>, EntityRecordList> _eqlExecutor;

		/// <summary>
		/// Creates a testable TaskService with all dependencies injected and a
		/// custom EQL execution handler for unit test isolation.
		/// </summary>
		/// <param name="recordManager">Mocked RecordManager for CRUD operations.</param>
		/// <param name="entityManager">Mocked EntityManager for entity metadata.</param>
		/// <param name="relationManager">Mocked EntityRelationManager for relation lookups.</param>
		/// <param name="feedService">Mocked FeedService for activity feed creation.</param>
		/// <param name="logger">Mocked ILogger for structured logging.</param>
		/// <param name="eqlExecutor">
		/// Custom EQL execution function that replaces all <c>new EqlCommand(...).Execute()</c>
		/// calls in the TaskService with controlled test behavior.
		/// </param>
		public TestableTaskService(
			RecordManager recordManager,
			EntityManager entityManager,
			EntityRelationManager relationManager,
			FeedService feedService,
			ILogger<TaskService> logger,
			Func<string, List<EqlParameter>, EntityRecordList> eqlExecutor)
			: base(recordManager, entityManager, relationManager, feedService, logger)
		{
			_eqlExecutor = eqlExecutor ?? throw new ArgumentNullException(nameof(eqlExecutor));
		}

		/// <summary>
		/// Overrides the EQL execution to use the test-provided function
		/// instead of creating a real EqlCommand with database connectivity.
		/// </summary>
		protected override EntityRecordList ExecuteEql(string text, List<EqlParameter> parameters)
		{
			return _eqlExecutor(text, parameters);
		}
	}

	#endregion
}
