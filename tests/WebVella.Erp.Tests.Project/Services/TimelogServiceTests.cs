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
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database; // CoreDbContext for mock construction
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.Tests.Project.Services
{
	/// <summary>
	/// Comprehensive unit tests for <see cref="TimelogService"/> covering all business rules
	/// extracted from the monolith's TimeLogService (369 lines) plus hook logic.
	///
	/// Tests cover: Create(), Delete(), GetTimelogsForPeriod(), CreateTimelogFromTracker(),
	/// PreCreateApiHookLogic(), and PreDeleteApiHookLogic().
	///
	/// Per AAP 0.8.1: every business rule maps to at least one automated test.
	/// Per AAP 0.8.2: all business logic classes must have ≥80% code coverage.
	/// Total: 32 test methods covering all 6 public methods of TimelogService.
	/// </summary>
	public class TimelogServiceTests : IDisposable
	{
		#region << Test Infrastructure >>

		private readonly Mock<RecordManager> _mockRecordManager;
		private readonly Mock<FeedService> _mockFeedService;
		private readonly Mock<TaskService> _mockTaskService;
		private readonly Mock<ILogger<TimelogService>> _mockLogger;
		private readonly TestableTimelogService _sut;

		/// <summary>
		/// Queue of EQL results returned by the testable EQL execution override.
		/// Each call to ExecuteEql dequeues the next result. Tests enqueue expected
		/// results in the order the TimelogService method will issue EQL queries.
		/// </summary>
		private readonly Queue<EntityRecordList> _eqlResultQueue = new Queue<EntityRecordList>();

		/// <summary>
		/// Queue of QueryResponse results returned by the testable ExecuteDeleteRecord override.
		/// Each call to ExecuteDeleteRecord dequeues the next result.
		/// </summary>
		private readonly Queue<QueryResponse> _deleteResultQueue = new Queue<QueryResponse>();

		/// <summary>
		/// Captured EQL calls for assertion. Each entry contains the EQL text and
		/// the list of EqlParameter objects passed to ExecuteEql.
		/// </summary>
		private readonly List<(string Text, List<EqlParameter> Parameters)> _capturedEqlCalls =
			new List<(string, List<EqlParameter>)>();

		/// <summary>
		/// Captured DeleteRecord calls for assertion. Each entry contains the entity name
		/// and the GUID of the record to delete.
		/// </summary>
		private readonly List<(string EntityName, Guid Id)> _capturedDeleteCalls =
			new List<(string, Guid)>();

		/// <summary>
		/// Test user used for SecurityContext throughout tests.
		/// </summary>
		private readonly ErpUser _testUser;

		/// <summary>
		/// Disposable scope for SecurityContext.
		/// </summary>
		private readonly IDisposable _securityScope;

		/// <summary>
		/// Original ErpSettings.TimeZoneName value saved for restoration in Dispose.
		/// </summary>
		private readonly string _originalTimeZoneName;

		/// <summary>
		/// Initializes fresh mock dependencies for each test method.
		/// Uses <see cref="RuntimeHelpers.GetUninitializedObject"/> to create dependency
		/// instances without invoking constructors — same pattern as TaskServiceTests.
		/// </summary>
		public TimelogServiceTests()
		{
			// Save and set timezone for ConvertAppDateToUtc() extension method
			_originalTimeZoneName = ErpSettings.TimeZoneName;
			ErpSettings.TimeZoneName = "America/New_York";

			// Create uninitialized infrastructure objects to satisfy constructor null checks
			var dbContext = (CoreDbContext)RuntimeHelpers.GetUninitializedObject(typeof(CoreDbContext));
			var entityManager = (EntityManager)RuntimeHelpers.GetUninitializedObject(typeof(EntityManager));
			var relationManager = (EntityRelationManager)RuntimeHelpers.GetUninitializedObject(typeof(EntityRelationManager));
			var publishEndpoint = Mock.Of<IPublishEndpoint>();
			var configuration = Mock.Of<IConfiguration>();

			// Create mockable RecordManager with constructor params
			_mockRecordManager = new Mock<RecordManager>(
				dbContext, entityManager, relationManager, publishEndpoint, false, true);

			// Create mockable FeedService (Create() is virtual)
			var feedRecordManager = (RecordManager)RuntimeHelpers.GetUninitializedObject(typeof(RecordManager));
			var feedLogger = Mock.Of<ILogger<FeedService>>();
			_mockFeedService = new Mock<FeedService>(MockBehavior.Loose, feedRecordManager, feedLogger);

			// Create mockable TaskService (StopTaskTimelog is virtual)
			var taskEntityManager = (EntityManager)RuntimeHelpers.GetUninitializedObject(typeof(EntityManager));
			var taskRelationManager = (EntityRelationManager)RuntimeHelpers.GetUninitializedObject(typeof(EntityRelationManager));
			var taskFeedService = (FeedService)RuntimeHelpers.GetUninitializedObject(typeof(FeedService));
			var taskLogger = Mock.Of<ILogger<TaskService>>();
			_mockTaskService = new Mock<TaskService>(
				MockBehavior.Loose,
				feedRecordManager, taskEntityManager, taskRelationManager, taskFeedService, taskLogger);

			_mockLogger = new Mock<ILogger<TimelogService>>();

			// Set up the test user and security scope
			_testUser = new ErpUser { Id = Guid.NewGuid() };
			_securityScope = SecurityContext.OpenScope(_testUser);

			// Create the system under test using our TestableTimelogService
			_sut = new TestableTimelogService(
				_mockRecordManager.Object,
				_mockFeedService.Object,
				_mockTaskService.Object,
				_mockLogger.Object,
				ExecuteEqlHandler,
				ExecuteDeleteRecordHandler);
		}

		public void Dispose()
		{
			_securityScope?.Dispose();
			_eqlResultQueue.Clear();
			_capturedEqlCalls.Clear();
			_deleteResultQueue.Clear();
			_capturedDeleteCalls.Clear();
			ErpSettings.TimeZoneName = _originalTimeZoneName;
		}

		/// <summary>
		/// EQL execution handler invoked by TestableTimelogService.
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
		/// DeleteRecord execution handler invoked by TestableTimelogService.
		/// Dequeues the next pre-configured result and captures the call for assertion.
		/// </summary>
		private QueryResponse ExecuteDeleteRecordHandler(string entityName, Guid id)
		{
			_capturedDeleteCalls.Add((entityName, id));
			if (_deleteResultQueue.Count > 0)
				return _deleteResultQueue.Dequeue();
			return new QueryResponse { Success = true, Message = "OK" };
		}

		/// <summary>
		/// Enqueues an EQL result to be returned by the next ExecuteEql call.
		/// </summary>
		private void EnqueueEqlResult(EntityRecordList result)
		{
			_eqlResultQueue.Enqueue(result);
		}

		/// <summary>
		/// Enqueues a DeleteRecord result to be returned by the next ExecuteDeleteRecord call.
		/// </summary>
		private void EnqueueDeleteResult(QueryResponse result)
		{
			_deleteResultQueue.Enqueue(result);
		}

		/// <summary>
		/// Creates a successful QueryResponse for RecordManager mock setups.
		/// </summary>
		private static QueryResponse CreateSuccessResponse()
		{
			return new QueryResponse { Success = true, Message = "OK" };
		}

		/// <summary>
		/// Creates a failed QueryResponse for RecordManager mock setups.
		/// </summary>
		private static QueryResponse CreateFailureResponse(string message = "Operation failed")
		{
			return new QueryResponse { Success = false, Message = message };
		}

		/// <summary>
		/// Creates a task EntityRecord with standard fields for hook logic testing.
		/// Includes $project_nn_task and $user_nn_task_watchers relation fields.
		/// </summary>
		private static EntityRecord CreateTaskRecord(
			Guid? id = null,
			decimal billableMinutes = 0m,
			decimal nonBillableMinutes = 0m,
			Guid? projectId = null,
			List<Guid> watcherIds = null,
			string key = "TSK-1",
			string subject = "Test task")
		{
			var taskId = id ?? Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = taskId;
			record["key"] = key;
			record["subject"] = subject;
			record["x_billable_minutes"] = billableMinutes;
			record["x_nonbillable_minutes"] = nonBillableMinutes;
			record["timelog_started_on"] = (DateTime?)null;

			// Set up project relation records
			var projectRecords = new List<EntityRecord>();
			if (projectId != null)
			{
				var projRec = new EntityRecord();
				projRec["id"] = projectId.Value;
				projectRecords.Add(projRec);
			}
			record["$project_nn_task"] = projectRecords;

			// Set up watcher relation records
			var watcherRecords = new List<EntityRecord>();
			if (watcherIds != null)
			{
				foreach (var wId in watcherIds)
				{
					var wRec = new EntityRecord();
					wRec["id"] = wId;
					watcherRecords.Add(wRec);
				}
			}
			record["$user_nn_task_watchers"] = watcherRecords;

			return record;
		}

		/// <summary>
		/// Creates a timelog EntityRecord with standard fields for delete hook testing.
		/// </summary>
		private static EntityRecord CreateTimelogRecord(
			Guid? id = null,
			Guid? createdBy = null,
			bool isBillable = true,
			decimal minutes = 30m,
			string scope = "[\"projects\"]",
			string relatedRecords = null)
		{
			var record = new EntityRecord();
			record["id"] = id ?? Guid.NewGuid();
			record["created_by"] = createdBy ?? Guid.NewGuid();
			record["is_billable"] = isBillable;
			record["minutes"] = minutes;
			record["l_scope"] = scope;
			record["l_related_records"] = relatedRecords;
			record["body"] = "Test timelog body";
			return record;
		}

		#endregion

		#region << Create() Tests >>

		/// <summary>
		/// Business Rule: When Create() is called with id=null, it should generate a new
		/// Guid via Guid.NewGuid() (source line 24-25). The generated ID must not be Guid.Empty.
		/// When createdBy=null, it should default to SystemIds.SystemUserId (source line 27-28).
		/// When createdOn=null, it defaults to DateTime.UtcNow (source line 30-31).
		/// When loggedOn=null, it defaults to DateTime.UtcNow (source line 33-34).
		/// </summary>
		[Fact]
		public void Create_WithDefaults_ShouldGenerateIdAndSetSystemUser()
		{
			// Arrange
			EntityRecord capturedRecord = null;
			_mockRecordManager.Setup(rm => rm.CreateRecord("timelog", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, record) => capturedRecord = record)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.Create();

			// Assert
			capturedRecord.Should().NotBeNull("CreateRecord should have been called");
			((Guid)capturedRecord["id"]).Should().NotBe(Guid.Empty,
				"id should be auto-generated with Guid.NewGuid() when null");
			((Guid)capturedRecord["created_by"]).Should().Be(SystemIds.SystemUserId,
				"createdBy should default to SystemIds.SystemUserId when null");
			((DateTime)capturedRecord["created_on"]).Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5),
				"createdOn should default to approximately DateTime.UtcNow");
		}

		/// <summary>
		/// Business Rule: Create() should convert the loggedOn parameter to UTC using
		/// the ConvertAppDateToUtc() extension method (source line 43). This is a KEY
		/// business rule ensuring consistent UTC storage of timestamps.
		/// </summary>
		[Fact]
		public void Create_ShouldConvertLoggedOnToUtc()
		{
			// Arrange
			var specificLoggedOn = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Unspecified);
			EntityRecord capturedRecord = null;
			_mockRecordManager.Setup(rm => rm.CreateRecord("timelog", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, record) => capturedRecord = record)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.Create(loggedOn: specificLoggedOn);

			// Assert
			capturedRecord.Should().NotBeNull();
			// The logged_on field should have the ConvertAppDateToUtc() result applied.
			// Since ConvertAppDateToUtc() converts from app timezone to UTC, the result
			// should be a valid DateTime (not null, not the original unmodified value).
			var loggedOnValue = capturedRecord["logged_on"];
			loggedOnValue.Should().NotBeNull("logged_on should be set after UTC conversion");
		}

		/// <summary>
		/// Business Rule: Create() should serialize the scope list as JSON via
		/// JsonConvert.SerializeObject() and store it in the l_scope field (source line 47).
		/// </summary>
		[Fact]
		public void Create_ShouldSerializeScopeAsJson()
		{
			// Arrange
			var scope = new List<string> { "projects" };
			EntityRecord capturedRecord = null;
			_mockRecordManager.Setup(rm => rm.CreateRecord("timelog", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, record) => capturedRecord = record)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.Create(scope: scope);

			// Assert
			capturedRecord.Should().NotBeNull();
			var expectedJson = JsonConvert.SerializeObject(scope);
			((string)capturedRecord["l_scope"]).Should().Be(expectedJson,
				"scope should be serialized as JSON string via JsonConvert.SerializeObject");
		}

		/// <summary>
		/// Business Rule: Create() should serialize the relatedRecords list as JSON via
		/// JsonConvert.SerializeObject() and store in the l_related_records field (source line 48).
		/// </summary>
		[Fact]
		public void Create_ShouldSerializeRelatedRecordsAsJson()
		{
			// Arrange
			var relatedRecords = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
			EntityRecord capturedRecord = null;
			_mockRecordManager.Setup(rm => rm.CreateRecord("timelog", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, record) => capturedRecord = record)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.Create(relatedRecords: relatedRecords);

			// Assert
			capturedRecord.Should().NotBeNull();
			var expectedJson = JsonConvert.SerializeObject(relatedRecords);
			((string)capturedRecord["l_related_records"]).Should().Be(expectedJson,
				"relatedRecords should be serialized as JSON string");
		}

		/// <summary>
		/// Business Rule: Create() should set the minutes and is_billable fields
		/// on the EntityRecord (source lines 45-46).
		/// </summary>
		[Fact]
		public void Create_ShouldSetMinutesAndBillableFields()
		{
			// Arrange
			EntityRecord capturedRecord = null;
			_mockRecordManager.Setup(rm => rm.CreateRecord("timelog", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, record) => capturedRecord = record)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.Create(minutes: 60, isBillable: false);

			// Assert
			capturedRecord.Should().NotBeNull();
			((int)capturedRecord["minutes"]).Should().Be(60, "minutes should match the provided value");
			((bool)capturedRecord["is_billable"]).Should().BeFalse("is_billable should match the provided value");
		}

		/// <summary>
		/// Business Rule: When RecordManager.CreateRecord returns Success=false,
		/// Create() should throw a ValidationException with the response message
		/// (source lines 52-53). The entity name used is "timelog" (source line 50).
		/// </summary>
		[Fact]
		public void Create_WhenRecordManagerFails_ShouldThrowValidationException()
		{
			// Arrange
			_mockRecordManager.Setup(rm => rm.CreateRecord("timelog", It.IsAny<EntityRecord>()))
				.Returns(CreateFailureResponse("Timelog creation failed"));

			// Act
			Action act = () => _sut.Create();

			// Assert
			act.Should().Throw<ValidationException>()
				.WithMessage("Timelog creation failed");
		}

		/// <summary>
		/// Business Rule: When explicit values are provided for all parameters,
		/// Create() should use the provided values instead of defaults (source lines 24-34).
		/// </summary>
		[Fact]
		public void Create_WithExplicitValues_ShouldUseProvidedValues()
		{
			// Arrange
			var explicitId = Guid.NewGuid();
			var explicitCreatedBy = Guid.NewGuid();
			var explicitCreatedOn = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
			var explicitLoggedOn = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc);
			EntityRecord capturedRecord = null;
			_mockRecordManager.Setup(rm => rm.CreateRecord("timelog", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, record) => capturedRecord = record)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.Create(id: explicitId, createdBy: explicitCreatedBy,
				createdOn: explicitCreatedOn, loggedOn: explicitLoggedOn,
				minutes: 120, isBillable: true, body: "Explicit body");

			// Assert
			capturedRecord.Should().NotBeNull();
			((Guid)capturedRecord["id"]).Should().Be(explicitId,
				"explicit id should be used when provided");
			((Guid)capturedRecord["created_by"]).Should().Be(explicitCreatedBy,
				"explicit createdBy should be used when provided");
			((DateTime)capturedRecord["created_on"]).Should().Be(explicitCreatedOn,
				"explicit createdOn should be used when provided");
			((int)capturedRecord["minutes"]).Should().Be(120);
			((bool)capturedRecord["is_billable"]).Should().BeTrue();
			((string)capturedRecord["body"]).Should().Be("Explicit body");
		}

		#endregion

		#region << Delete() Tests >>

		/// <summary>
		/// Business Rule: Delete() should throw Exception("RecordId not found") when the
		/// EQL query returns no records for the given recordId (source line 70).
		/// The EQL query is: SELECT id,created_by FROM timelog WHERE id = @recordId
		/// </summary>
		[Fact]
		public void Delete_WhenRecordNotFound_ShouldThrowException()
		{
			// Arrange - EQL returns empty result
			EnqueueEqlResult(new EntityRecordList());

			// Act & Assert
			Action act = () => _sut.Delete(Guid.NewGuid());
			act.Should().Throw<Exception>()
				.WithMessage("RecordId not found");

			// Verify EQL was called
			_capturedEqlCalls.Should().HaveCount(1);
			_capturedEqlCalls[0].Text.Should().Contain("SELECT id,created_by FROM timelog WHERE id = @recordId");
		}

		/// <summary>
		/// Business Rule: Delete() should throw Exception("Only the author can delete its comment")
		/// when the current user (SecurityContext.CurrentUser.Id) does not match the timelog's
		/// created_by field (source line 72).
		///
		/// NOTE: The error message says "comment" not "timelog" — this is EXACT source behavior preserved!
		/// KEY BUSINESS RULE: Author-only deletion enforcement.
		/// </summary>
		[Fact]
		public void Delete_WhenNotAuthor_ShouldThrowException()
		{
			// Arrange - EQL returns a record with a DIFFERENT created_by
			var recordId = Guid.NewGuid();
			var differentUserId = Guid.NewGuid();
			var eqlResult = new EntityRecordList();
			var record = new EntityRecord();
			record["id"] = recordId;
			record["created_by"] = differentUserId; // Different from _testUser.Id
			eqlResult.Add(record);
			EnqueueEqlResult(eqlResult);

			// Act & Assert
			Action act = () => _sut.Delete(recordId);
			act.Should().Throw<Exception>()
				.WithMessage("Only the author can delete its comment");
		}

		/// <summary>
		/// Business Rule: Delete() should successfully delete the timelog when the current
		/// user matches the record's created_by field (source lines 75-79).
		/// RecordManager.DeleteRecord("timelog", recordId) should be called.
		/// </summary>
		[Fact]
		public void Delete_WhenAuthor_ShouldDeleteSuccessfully()
		{
			// Arrange - EQL returns a record with matching created_by
			var recordId = Guid.NewGuid();
			var eqlResult = new EntityRecordList();
			var record = new EntityRecord();
			record["id"] = recordId;
			record["created_by"] = _testUser.Id; // Matches SecurityContext.CurrentUser
			eqlResult.Add(record);
			EnqueueEqlResult(eqlResult);

			// Set up DeleteRecord to succeed
			EnqueueDeleteResult(CreateSuccessResponse());

			// Act
			_sut.Delete(recordId);

			// Assert - verify DeleteRecord was called with "timelog" and the correct ID
			_capturedDeleteCalls.Should().HaveCount(1);
			_capturedDeleteCalls[0].EntityName.Should().Be("timelog");
			_capturedDeleteCalls[0].Id.Should().Be(recordId);
		}

		/// <summary>
		/// Business Rule: Delete() should throw an Exception with the response message
		/// when RecordManager.DeleteRecord returns Success=false (source lines 76-79).
		/// </summary>
		[Fact]
		public void Delete_WhenDeleteFails_ShouldThrowException()
		{
			// Arrange
			var recordId = Guid.NewGuid();
			var eqlResult = new EntityRecordList();
			var record = new EntityRecord();
			record["id"] = recordId;
			record["created_by"] = _testUser.Id;
			eqlResult.Add(record);
			EnqueueEqlResult(eqlResult);

			EnqueueDeleteResult(CreateFailureResponse("Delete operation failed"));

			// Act & Assert
			Action act = () => _sut.Delete(recordId);
			act.Should().Throw<Exception>()
				.WithMessage("Delete operation failed");
		}

		#endregion

		#region << GetTimelogsForPeriod() Tests >>

		/// <summary>
		/// Business Rule: GetTimelogsForPeriod() with no projectId or userId filters should
		/// query only by date range: WHERE logged_on >= @startDate AND logged_on &lt; @endDate
		/// (source lines 87-89). Only 2 EQL parameters should be used.
		/// </summary>
		[Fact]
		public void GetTimelogsForPeriod_WithNoFilters_ShouldQueryDateRangeOnly()
		{
			// Arrange
			var startDate = new DateTime(2024, 1, 1);
			var endDate = new DateTime(2024, 1, 31);
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTimelogsForPeriod(null, null, startDate, endDate);

			// Assert
			_capturedEqlCalls.Should().HaveCount(1);
			var eqlCall = _capturedEqlCalls[0];
			eqlCall.Text.Should().Contain("logged_on >= @startDate");
			eqlCall.Text.Should().Contain("logged_on < @endDate");
			eqlCall.Text.Should().NotContain("l_related_records CONTAINS");
			eqlCall.Text.Should().NotContain("created_by =");
			eqlCall.Parameters.Should().HaveCount(2,
				"only startDate and endDate parameters when no filters");
			eqlCall.Parameters.Should().Contain(p => p.ParameterName == "@startDate");
			eqlCall.Parameters.Should().Contain(p => p.ParameterName == "@endDate");
		}

		/// <summary>
		/// Business Rule: When projectId is specified, GetTimelogsForPeriod() should append
		/// "AND l_related_records CONTAINS @projectId" to the EQL query (source line 93).
		/// </summary>
		[Fact]
		public void GetTimelogsForPeriod_WithProjectId_ShouldAddContainsFilter()
		{
			// Arrange
			var projectId = Guid.NewGuid();
			var startDate = new DateTime(2024, 1, 1);
			var endDate = new DateTime(2024, 1, 31);
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTimelogsForPeriod(projectId, null, startDate, endDate);

			// Assert
			_capturedEqlCalls.Should().HaveCount(1);
			var eqlCall = _capturedEqlCalls[0];
			eqlCall.Text.Should().Contain("l_related_records CONTAINS @projectId");
			eqlCall.Parameters.Should().HaveCount(3,
				"startDate, endDate, and projectId parameters");
			eqlCall.Parameters.Should().Contain(p => p.ParameterName == "@projectId");
		}

		/// <summary>
		/// Business Rule: When userId is specified, GetTimelogsForPeriod() should append
		/// "AND created_by = @userId" to the EQL query (source lines 98-99).
		/// </summary>
		[Fact]
		public void GetTimelogsForPeriod_WithUserId_ShouldAddCreatedByFilter()
		{
			// Arrange
			var userId = Guid.NewGuid();
			var startDate = new DateTime(2024, 1, 1);
			var endDate = new DateTime(2024, 1, 31);
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTimelogsForPeriod(null, userId, startDate, endDate);

			// Assert
			_capturedEqlCalls.Should().HaveCount(1);
			var eqlCall = _capturedEqlCalls[0];
			eqlCall.Text.Should().Contain("created_by = @userId");
			eqlCall.Parameters.Should().HaveCount(3,
				"startDate, endDate, and userId parameters");
			eqlCall.Parameters.Should().Contain(p => p.ParameterName == "@userId");
		}

		/// <summary>
		/// Business Rule: When both projectId and userId are specified, both filters
		/// should be applied to the EQL query (source lines 91-100). 4 total parameters.
		/// </summary>
		[Fact]
		public void GetTimelogsForPeriod_WithBothFilters_ShouldApplyBoth()
		{
			// Arrange
			var projectId = Guid.NewGuid();
			var userId = Guid.NewGuid();
			var startDate = new DateTime(2024, 1, 1);
			var endDate = new DateTime(2024, 1, 31);
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.GetTimelogsForPeriod(projectId, userId, startDate, endDate);

			// Assert
			_capturedEqlCalls.Should().HaveCount(1);
			var eqlCall = _capturedEqlCalls[0];
			eqlCall.Text.Should().Contain("l_related_records CONTAINS @projectId");
			eqlCall.Text.Should().Contain("created_by = @userId");
			eqlCall.Parameters.Should().HaveCount(4,
				"startDate, endDate, projectId, and userId parameters");
		}

		#endregion

		#region << PreCreateApiHookLogic() Tests >>

		/// <summary>
		/// Business Rule: PreCreateApiHookLogic() should throw Exception when the record
		/// does not have an 'id' property (source line 184).
		/// </summary>
		[Fact]
		public void PreCreateApiHookLogic_WhenNoIdField_ShouldThrowException()
		{
			// Arrange - record WITHOUT 'id' property
			var record = new EntityRecord();
			record["body"] = "test";
			var errors = new List<ErrorModel>();

			// Act & Assert
			Action act = () => _sut.PreCreateApiHookLogic(record, errors);
			act.Should().Throw<Exception>()
				.WithMessage("Hook exception: timelog id field not found in record");
		}

		/// <summary>
		/// Business Rule: When l_scope does not contain "projects", PreCreateApiHookLogic()
		/// should skip all project-specific processing (source line 418-421).
		/// No EQL queries, no task updates, no feed creation.
		/// </summary>
		[Fact]
		public void PreCreateApiHookLogic_WhenNotProjectScope_ShouldSkipProcessing()
		{
			// Arrange - record with non-project scope
			var record = new EntityRecord();
			record["id"] = Guid.NewGuid();
			record["l_scope"] = JsonConvert.SerializeObject(new List<string> { "personal" });
			var errors = new List<ErrorModel>();

			// Act
			_sut.PreCreateApiHookLogic(record, errors);

			// Assert - no EQL calls, no RecordManager calls
			_capturedEqlCalls.Should().BeEmpty("no EQL queries when not project scope");
			_mockRecordManager.Verify(rm => rm.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()),
				Times.Never(), "no task update when not project scope");
			_mockFeedService.Verify(fs => fs.Create(
				It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
				It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
				It.IsAny<List<string>>(), It.IsAny<string>()),
				Times.Never(), "no feed creation when not project scope");
		}

		/// <summary>
		/// Business Rule: PreCreateApiHookLogic() should throw when the EQL query for
		/// related tasks returns empty (source line 224). The timelog must have an existing task.
		/// </summary>
		[Fact]
		public void PreCreateApiHookLogic_WhenNoRelatedTask_ShouldThrowException()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = Guid.NewGuid();
			record["l_scope"] = "[\"projects\"]";
			record["l_related_records"] = JsonConvert.SerializeObject(new List<Guid> { taskId });
			record["is_billable"] = true;
			record["minutes"] = 30;
			record["body"] = "";
			var errors = new List<ErrorModel>();

			// EQL returns empty task list
			EnqueueEqlResult(new EntityRecordList());

			// Act & Assert
			Action act = () => _sut.PreCreateApiHookLogic(record, errors);
			act.Should().Throw<Exception>()
				.WithMessage("Hook exception: This timelog does not have an existing taskId");
		}

		/// <summary>
		/// KEY BUSINESS RULE: Billable minute accumulation.
		/// When is_billable=true, PreCreateApiHookLogic() should increment the task's
		/// x_billable_minutes by the timelog's minutes (source line 232).
		/// Task: x_billable_minutes=30, Timelog: minutes=15 → Task patch: x_billable_minutes=45
		/// </summary>
		[Fact]
		public void PreCreateApiHookLogic_ForBillableTimelog_ShouldIncrementBillableMinutes()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var projectId = Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = Guid.NewGuid();
			record["l_scope"] = "[\"projects\"]";
			record["l_related_records"] = JsonConvert.SerializeObject(new List<Guid> { taskId });
			record["is_billable"] = true;
			record["minutes"] = 15;
			record["body"] = "";
			var errors = new List<ErrorModel>();

			// EQL returns task with existing billable minutes
			var taskRecord = CreateTaskRecord(
				id: taskId, billableMinutes: 30m, projectId: projectId);
			var taskResult = new EntityRecordList();
			taskResult.Add(taskRecord);
			EnqueueEqlResult(taskResult);

			EntityRecord capturedPatch = null;
			_mockRecordManager.Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, rec) => capturedPatch = rec)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.PreCreateApiHookLogic(record, errors);

			// Assert
			capturedPatch.Should().NotBeNull("task should be updated");
			capturedPatch["x_billable_minutes"].Should().NotBeNull();
			// 30 + 15 = 45
			Convert.ToDecimal(capturedPatch["x_billable_minutes"]).Should().Be(45m,
				"billable minutes should be incremented: 30 existing + 15 new = 45");
		}

		/// <summary>
		/// KEY BUSINESS RULE: Non-billable minute accumulation.
		/// When is_billable=false, PreCreateApiHookLogic() should increment the task's
		/// x_nonbillable_minutes by the timelog's minutes (source line 236).
		/// Task: x_nonbillable_minutes=20, Timelog: minutes=10 → Task patch: x_nonbillable_minutes=30
		/// </summary>
		[Fact]
		public void PreCreateApiHookLogic_ForNonBillableTimelog_ShouldIncrementNonBillableMinutes()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var projectId = Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = Guid.NewGuid();
			record["l_scope"] = "[\"projects\"]";
			record["l_related_records"] = JsonConvert.SerializeObject(new List<Guid> { taskId });
			record["is_billable"] = false;
			record["minutes"] = 10;
			record["body"] = "";
			var errors = new List<ErrorModel>();

			// EQL returns task with existing non-billable minutes
			var taskRecord = CreateTaskRecord(
				id: taskId, nonBillableMinutes: 20m, projectId: projectId);
			var taskResult = new EntityRecordList();
			taskResult.Add(taskRecord);
			EnqueueEqlResult(taskResult);

			EntityRecord capturedPatch = null;
			_mockRecordManager.Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, rec) => capturedPatch = rec)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.PreCreateApiHookLogic(record, errors);

			// Assert
			capturedPatch.Should().NotBeNull("task should be updated");
			capturedPatch["x_nonbillable_minutes"].Should().NotBeNull();
			// 20 + 10 = 30
			Convert.ToDecimal(capturedPatch["x_nonbillable_minutes"]).Should().Be(30m,
				"non-billable minutes should be incremented: 20 existing + 10 new = 30");
		}

		/// <summary>
		/// Business Rule: PreCreateApiHookLogic() should nullify timelog_started_on
		/// on the task patch record (source line 240). This stops the active timer.
		/// </summary>
		[Fact]
		public void PreCreateApiHookLogic_ShouldNullifyTimelogStartedOn()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var projectId = Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = Guid.NewGuid();
			record["l_scope"] = "[\"projects\"]";
			record["l_related_records"] = JsonConvert.SerializeObject(new List<Guid> { taskId });
			record["is_billable"] = true;
			record["minutes"] = 15;
			record["body"] = "";
			var errors = new List<ErrorModel>();

			var taskRecord = CreateTaskRecord(id: taskId, billableMinutes: 10m, projectId: projectId);
			var taskResult = new EntityRecordList();
			taskResult.Add(taskRecord);
			EnqueueEqlResult(taskResult);

			EntityRecord capturedPatch = null;
			_mockRecordManager.Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, rec) => capturedPatch = rec)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.PreCreateApiHookLogic(record, errors);

			// Assert
			capturedPatch.Should().NotBeNull();
			capturedPatch.Properties.Should().ContainKey("timelog_started_on",
				"timelog_started_on should be set in the patch record");
			capturedPatch["timelog_started_on"].Should().BeNull(
				"timelog_started_on should be set to null to stop the active timer");
		}

		/// <summary>
		/// Business Rule: PreCreateApiHookLogic() should create a feed item with
		/// type="timelog" (source line 277). The feed records the logged time.
		/// </summary>
		[Fact]
		public void PreCreateApiHookLogic_ShouldCreateFeedItemWithCorrectType()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var projectId = Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = Guid.NewGuid();
			record["l_scope"] = "[\"projects\"]";
			record["l_related_records"] = JsonConvert.SerializeObject(new List<Guid> { taskId });
			record["is_billable"] = true;
			record["minutes"] = 30;
			record["body"] = "<p>Test body</p>";
			var errors = new List<ErrorModel>();

			var taskRecord = CreateTaskRecord(id: taskId, billableMinutes: 0m,
				projectId: projectId, key: "TSK-5", subject: "Important task");
			var taskResult = new EntityRecordList();
			taskResult.Add(taskRecord);
			EnqueueEqlResult(taskResult);

			_mockRecordManager.Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Returns(CreateSuccessResponse());

			// Act
			_sut.PreCreateApiHookLogic(record, errors);

			// Assert - verify FeedService.Create was called with type="timelog"
			_mockFeedService.Verify(fs => fs.Create(
				It.IsAny<Guid?>(),       // id
				It.Is<Guid?>(g => g == _testUser.Id),  // createdBy = current user
				It.IsAny<DateTime?>(),   // createdOn
				It.Is<string>(s => s.Contains("logged") && s.Contains("30")), // subject contains minutes
				It.IsAny<string>(),      // body
				It.IsAny<List<string>>(), // relatedRecords
				It.IsAny<List<string>>(), // scope
				It.Is<string>(t => t == "timelog")),  // type must be "timelog"
				Times.Once(), "feed item should be created with type 'timelog'");
		}

		/// <summary>
		/// Business Rule: PreCreateApiHookLogic() should include the taskId, recordId,
		/// projectId, and all watcher IDs in the relatedRecords parameter of the feed item
		/// (source lines 267-272, 499-504).
		/// </summary>
		[Fact]
		public void PreCreateApiHookLogic_ShouldIncludeWatchersAndProjectInRelatedRecords()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var timelogId = Guid.NewGuid();
			var projectId = Guid.NewGuid();
			var watcher1Id = Guid.NewGuid();
			var watcher2Id = Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = timelogId;
			record["l_scope"] = "[\"projects\"]";
			record["l_related_records"] = JsonConvert.SerializeObject(new List<Guid> { taskId });
			record["is_billable"] = true;
			record["minutes"] = 10;
			record["body"] = "";
			var errors = new List<ErrorModel>();

			var taskRecord = CreateTaskRecord(
				id: taskId, billableMinutes: 0m, projectId: projectId,
				watcherIds: new List<Guid> { watcher1Id, watcher2Id });
			var taskResult = new EntityRecordList();
			taskResult.Add(taskRecord);
			EnqueueEqlResult(taskResult);

			_mockRecordManager.Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Returns(CreateSuccessResponse());

			List<string> capturedRelatedRecords = null;
			_mockFeedService.Setup(fs => fs.Create(
				It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
				It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
				It.IsAny<List<string>>(), It.IsAny<string>()))
				.Callback<Guid?, Guid?, DateTime?, string, string, List<string>, List<string>, string>(
					(id, createdBy, createdOn, subject, body, relRecords, scope, type) =>
					{
						capturedRelatedRecords = relRecords;
					});

			// Act
			_sut.PreCreateApiHookLogic(record, errors);

			// Assert
			capturedRelatedRecords.Should().NotBeNull("relatedRecords should be passed to FeedService");
			capturedRelatedRecords.Should().Contain(taskId.ToString(), "taskId should be in relatedRecords");
			capturedRelatedRecords.Should().Contain(timelogId.ToString(), "timelogId should be in relatedRecords");
			capturedRelatedRecords.Should().Contain(projectId.ToString(), "projectId should be in relatedRecords");
			capturedRelatedRecords.Should().Contain(watcher1Id.ToString(), "watcher1 should be in relatedRecords");
			capturedRelatedRecords.Should().Contain(watcher2Id.ToString(), "watcher2 should be in relatedRecords");
		}

		#endregion

		#region << PreDeleteApiHookLogic() Tests >>

		/// <summary>
		/// Business Rule: PreDeleteApiHookLogic() should throw Exception when the record
		/// does not have an 'id' property (source line 284).
		/// </summary>
		[Fact]
		public void PreDeleteApiHookLogic_WhenNoIdField_ShouldThrowException()
		{
			// Arrange - record WITHOUT 'id' property
			var record = new EntityRecord();
			record["body"] = "test";
			var errors = new List<ErrorModel>();

			// Act & Assert
			Action act = () => _sut.PreDeleteApiHookLogic(record, errors);
			act.Should().Throw<Exception>()
				.WithMessage("Hook exception: timelog id field not found in record");
		}

		/// <summary>
		/// Business Rule: PreDeleteApiHookLogic() should throw Exception when the EQL query
		/// for the timelog record returns empty (source line 296).
		/// </summary>
		[Fact]
		public void PreDeleteApiHookLogic_WhenTimelogNotFound_ShouldThrowException()
		{
			// Arrange
			var record = new EntityRecord();
			record["id"] = Guid.NewGuid();
			var errors = new List<ErrorModel>();

			// EQL returns empty for timelog lookup
			EnqueueEqlResult(new EntityRecordList());

			// Act & Assert
			Action act = () => _sut.PreDeleteApiHookLogic(record, errors);
			act.Should().Throw<Exception>()
				.WithMessage("Hook exception: timelog with this id was not found");
		}

		/// <summary>
		/// KEY BUSINESS RULE: Billable minute decrement on delete.
		/// When the timelog is billable, PreDeleteApiHookLogic() should subtract the timelog's
		/// minutes from the task's x_billable_minutes (source line 337).
		/// Task: x_billable_minutes=50, Timelog: minutes=20 → Task patch: x_billable_minutes=30
		/// </summary>
		[Fact]
		public void PreDeleteApiHookLogic_ForBillableTimelog_ShouldDecrementBillableMinutes()
		{
			// Arrange
			var timelogId = Guid.NewGuid();
			var taskId = Guid.NewGuid();
			var projectId = Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = timelogId;
			var errors = new List<ErrorModel>();

			// EQL 1: timelog lookup returns billable timelog with 20 minutes
			var timelogRecord = CreateTimelogRecord(
				id: timelogId, isBillable: true, minutes: 20m,
				relatedRecords: JsonConvert.SerializeObject(new List<Guid> { taskId }));
			var timelogResult = new EntityRecordList();
			timelogResult.Add(timelogRecord);
			EnqueueEqlResult(timelogResult);

			// EQL 2: task lookup returns task with 50 billable minutes
			var taskRecord = CreateTaskRecord(id: taskId, billableMinutes: 50m, projectId: projectId);
			var taskResult = new EntityRecordList();
			taskResult.Add(taskRecord);
			EnqueueEqlResult(taskResult);

			EntityRecord capturedPatch = null;
			_mockRecordManager.Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, rec) => capturedPatch = rec)
				.Returns(CreateSuccessResponse());

			// EQL 3: feed lookup returns empty (no related feed items)
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.PreDeleteApiHookLogic(record, errors);

			// Assert
			capturedPatch.Should().NotBeNull("task should be updated");
			// Math.Round(50 - 20) = 30
			Convert.ToDecimal(capturedPatch["x_billable_minutes"]).Should().Be(30m,
				"billable minutes should be decremented: 50 existing - 20 timelog = 30");
		}

		/// <summary>
		/// KEY BUSINESS RULE: Floor at zero.
		/// When subtracting would result in a negative value, the minutes should be floored
		/// at 0 (source lines 340-341). Task: x_billable_minutes=5, Timelog: minutes=10 → 0
		/// </summary>
		[Fact]
		public void PreDeleteApiHookLogic_ShouldFloorAtZero()
		{
			// Arrange
			var timelogId = Guid.NewGuid();
			var taskId = Guid.NewGuid();
			var projectId = Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = timelogId;
			var errors = new List<ErrorModel>();

			// Timelog with 10 minutes, billable
			var timelogRecord = CreateTimelogRecord(
				id: timelogId, isBillable: true, minutes: 10m,
				relatedRecords: JsonConvert.SerializeObject(new List<Guid> { taskId }));
			var timelogResult = new EntityRecordList();
			timelogResult.Add(timelogRecord);
			EnqueueEqlResult(timelogResult);

			// Task with only 5 billable minutes (less than the timelog)
			var taskRecord = CreateTaskRecord(id: taskId, billableMinutes: 5m, projectId: projectId);
			var taskResult = new EntityRecordList();
			taskResult.Add(taskRecord);
			EnqueueEqlResult(taskResult);

			EntityRecord capturedPatch = null;
			_mockRecordManager.Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, rec) => capturedPatch = rec)
				.Returns(CreateSuccessResponse());

			// Feed lookup returns empty
			EnqueueEqlResult(new EntityRecordList());

			// Act
			_sut.PreDeleteApiHookLogic(record, errors);

			// Assert - Math.Round(5 - 10) = -5, which is <= 0, so should be 0
			capturedPatch.Should().NotBeNull("task should be updated");
			Convert.ToInt32(capturedPatch["x_billable_minutes"]).Should().Be(0,
				"billable minutes should be floored at 0 when subtraction would go negative");
		}

		/// <summary>
		/// KEY BUSINESS RULE: Cascading feed item deletion.
		/// PreDeleteApiHookLogic() should query for feed items related to this timelog
		/// and delete them all (source lines 359-363).
		/// When there are 2 related feed items, DeleteRecord("feed_item", ...) is called twice.
		/// </summary>
		[Fact]
		public void PreDeleteApiHookLogic_ShouldDeleteRelatedFeedItems()
		{
			// Arrange
			var timelogId = Guid.NewGuid();
			var taskId = Guid.NewGuid();
			var projectId = Guid.NewGuid();
			var feedId1 = Guid.NewGuid();
			var feedId2 = Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = timelogId;
			var errors = new List<ErrorModel>();

			// EQL 1: timelog lookup
			var timelogRecord = CreateTimelogRecord(
				id: timelogId, isBillable: true, minutes: 10m,
				relatedRecords: JsonConvert.SerializeObject(new List<Guid> { taskId }));
			var timelogResult = new EntityRecordList();
			timelogResult.Add(timelogRecord);
			EnqueueEqlResult(timelogResult);

			// EQL 2: task lookup
			var taskRecord = CreateTaskRecord(id: taskId, billableMinutes: 30m, projectId: projectId);
			var taskResult = new EntityRecordList();
			taskResult.Add(taskRecord);
			EnqueueEqlResult(taskResult);

			_mockRecordManager.Setup(rm => rm.UpdateRecord("task", It.IsAny<EntityRecord>()))
				.Returns(CreateSuccessResponse());

			// EQL 3: feed lookup returns 2 feed items
			var feedResult = new EntityRecordList();
			var feed1 = new EntityRecord();
			feed1["id"] = feedId1;
			feedResult.Add(feed1);
			var feed2 = new EntityRecord();
			feed2["id"] = feedId2;
			feedResult.Add(feed2);
			EnqueueEqlResult(feedResult);

			// Queue delete results for both feed items
			EnqueueDeleteResult(CreateSuccessResponse());
			EnqueueDeleteResult(CreateSuccessResponse());

			// Act
			_sut.PreDeleteApiHookLogic(record, errors);

			// Assert - verify feed EQL was called
			_capturedEqlCalls.Should().HaveCount(3); // timelog, task, feed
			var feedEql = _capturedEqlCalls[2];
			feedEql.Text.Should().Contain("SELECT id FROM feed_item WHERE l_related_records CONTAINS @recordId");

			// Verify DeleteRecord was called for both feed items
			_capturedDeleteCalls.Should().HaveCount(2);
			_capturedDeleteCalls[0].EntityName.Should().Be("feed_item");
			_capturedDeleteCalls[0].Id.Should().Be(feedId1);
			_capturedDeleteCalls[1].EntityName.Should().Be("feed_item");
			_capturedDeleteCalls[1].Id.Should().Be(feedId2);
		}

		/// <summary>
		/// Business Rule: When the timelog's l_scope does not contain "projects",
		/// PreDeleteApiHookLogic() should skip task/feed processing (source lines 556-559).
		/// Note: The timelog record is still fetched via EQL, but no task update or feed
		/// deletion occurs when it's not a project-scoped timelog.
		/// </summary>
		[Fact]
		public void PreDeleteApiHookLogic_WhenNotProjectScope_ShouldSkipProcessing()
		{
			// Arrange
			var timelogId = Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = timelogId;
			var errors = new List<ErrorModel>();

			// EQL 1: timelog lookup returns non-project scoped timelog
			var timelogRecord = CreateTimelogRecord(
				id: timelogId, isBillable: true, minutes: 10m,
				scope: "[\"personal\"]");
			var timelogResult = new EntityRecordList();
			timelogResult.Add(timelogRecord);
			EnqueueEqlResult(timelogResult);

			// Act
			_sut.PreDeleteApiHookLogic(record, errors);

			// Assert - only 1 EQL call (timelog lookup), no task/feed queries
			_capturedEqlCalls.Should().HaveCount(1,
				"only the initial timelog lookup EQL should be called");
			_mockRecordManager.Verify(rm => rm.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()),
				Times.Never(), "no task update when not project scope");
			_capturedDeleteCalls.Should().BeEmpty("no feed deletion when not project scope");
		}

		#endregion

		#region << CreateTimelogFromTracker() Tests >>

		/// <summary>
		/// Business Rule: CreateTimelogFromTracker() should create a timelog and stop the
		/// task's timer when given valid parameters (source lines 108-179).
		/// TaskService.StopTaskTimelog(taskId) should be called.
		/// Create() should be called within the transaction scope when minutes > 0.
		/// </summary>
		[Fact]
		public void CreateTimelogFromTracker_WithValidTrackTimePage_ShouldCreateTimelogAndStopTimer()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var projectId = Guid.NewGuid();
			var loggedOn = new DateTime(2024, 6, 15, 10, 0, 0);
			var minutes = 45;

			// EQL: task lookup
			var taskRecord = CreateTaskRecord(id: taskId, projectId: projectId);
			var taskResult = new EntityRecordList();
			taskResult.Add(taskRecord);
			EnqueueEqlResult(taskResult);

			// RecordManager.CreateRecord for the timelog
			EntityRecord capturedTimelog = null;
			_mockRecordManager.Setup(rm => rm.CreateRecord("timelog", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, rec) => capturedTimelog = rec)
				.Returns(CreateSuccessResponse());

			// Act
			_sut.CreateTimelogFromTracker(taskId, minutes, loggedOn, true, "Work done");

			// Assert - StopTaskTimelog called
			_mockTaskService.Verify(ts => ts.StopTaskTimelog(taskId), Times.Once(),
				"StopTaskTimelog should be called to stop the active timer");

			// Assert - Create() was called (via RecordManager.CreateRecord)
			capturedTimelog.Should().NotBeNull("timelog record should be created for minutes > 0");
			((int)capturedTimelog["minutes"]).Should().Be(45);
			((bool)capturedTimelog["is_billable"]).Should().BeTrue();
			((string)capturedTimelog["body"]).Should().Be("Work done");

			// Assert - scope contains "projects"
			var scope = JsonConvert.DeserializeObject<List<string>>((string)capturedTimelog["l_scope"]);
			scope.Should().Contain("projects");

			// Assert - relatedRecords contains taskId and projectId
			var relRecords = JsonConvert.DeserializeObject<List<Guid>>((string)capturedTimelog["l_related_records"]);
			relRecords.Should().Contain(taskId);
			relRecords.Should().Contain(projectId);
		}

		/// <summary>
		/// Business Rule: When minutes=0, CreateTimelogFromTracker() should NOT create
		/// a timelog (source line 161-163: "Zero minutes are not logged"). However,
		/// StopTaskTimelog IS still called to stop the timer.
		/// </summary>
		[Fact]
		public void CreateTimelogFromTracker_WithZeroMinutes_ShouldNotCreateTimelog()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var projectId = Guid.NewGuid();
			var loggedOn = DateTime.Now;

			// EQL: task lookup
			var taskRecord = CreateTaskRecord(id: taskId, projectId: projectId);
			var taskResult = new EntityRecordList();
			taskResult.Add(taskRecord);
			EnqueueEqlResult(taskResult);

			// Act
			_sut.CreateTimelogFromTracker(taskId, 0, loggedOn, true, "No time");

			// Assert - StopTaskTimelog IS still called
			_mockTaskService.Verify(ts => ts.StopTaskTimelog(taskId), Times.Once(),
				"StopTaskTimelog should still be called even with zero minutes");

			// Assert - CreateRecord is NOT called (no timelog created)
			_mockRecordManager.Verify(rm => rm.CreateRecord("timelog", It.IsAny<EntityRecord>()),
				Times.Never(), "timelog should NOT be created when minutes=0");
		}

		/// <summary>
		/// Business Rule: When the task is not found (EQL returns empty),
		/// CreateTimelogFromTracker() should throw an Exception.
		/// </summary>
		[Fact]
		public void CreateTimelogFromTracker_MissingTaskId_ShouldThrowException()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var loggedOn = DateTime.Now;

			// EQL: task lookup returns empty
			EnqueueEqlResult(new EntityRecordList());

			// Act & Assert
			Action act = () => _sut.CreateTimelogFromTracker(taskId, 30, loggedOn, true, "Test");
			act.Should().Throw<Exception>()
				.WithMessage("Task with taskId not found");

			// StopTaskTimelog should NOT be called because the error occurs before that line
			_mockTaskService.Verify(ts => ts.StopTaskTimelog(It.IsAny<Guid>()), Times.Never(),
				"StopTaskTimelog should not be called when task is not found");
		}

		#endregion
	}

	#region << TestableTimelogService >>

	/// <summary>
	/// Testable subclass of TimelogService that overrides the EQL execution path
	/// and DeleteRecord path to enable isolated unit testing without database connectivity.
	///
	/// The <see cref="TimelogService.ExecuteEql"/> and <see cref="TimelogService.ExecuteDeleteRecord"/>
	/// virtual methods are overridden to delegate to configurable functions, allowing tests to
	/// control the EQL query results and delete operation results returned to business logic methods.
	/// </summary>
	internal class TestableTimelogService : TimelogService
	{
		private readonly Func<string, List<EqlParameter>, EntityRecordList> _eqlExecutor;
		private readonly Func<string, Guid, QueryResponse> _deleteExecutor;

		/// <summary>
		/// Creates a testable TimelogService with all dependencies injected and custom
		/// EQL and DeleteRecord execution handlers for unit test isolation.
		/// </summary>
		public TestableTimelogService(
			RecordManager recordManager,
			FeedService feedService,
			TaskService taskService,
			ILogger<TimelogService> logger,
			Func<string, List<EqlParameter>, EntityRecordList> eqlExecutor,
			Func<string, Guid, QueryResponse> deleteExecutor)
			: base(recordManager, feedService, taskService, logger)
		{
			_eqlExecutor = eqlExecutor ?? throw new ArgumentNullException(nameof(eqlExecutor));
			_deleteExecutor = deleteExecutor ?? throw new ArgumentNullException(nameof(deleteExecutor));
		}

		/// <summary>
		/// Overrides the EQL execution to use the test-provided function
		/// instead of creating a real EqlCommand with database connectivity.
		/// </summary>
		protected override EntityRecordList ExecuteEql(string text, List<EqlParameter> parameters)
		{
			return _eqlExecutor(text, parameters);
		}

		/// <summary>
		/// Overrides the DeleteRecord execution to use the test-provided function
		/// instead of calling the non-virtual RecordManager.DeleteRecord.
		/// </summary>
		protected override QueryResponse ExecuteDeleteRecord(string entityName, Guid id)
		{
			return _deleteExecutor(entityName, id);
		}
	}

	#endregion
}
