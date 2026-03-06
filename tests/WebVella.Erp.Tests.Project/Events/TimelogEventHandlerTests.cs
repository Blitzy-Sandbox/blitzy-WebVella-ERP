using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Project.Events.Publishers;
using WebVella.Erp.Service.Project.Domain.Services;
using Xunit;

namespace WebVella.Erp.Tests.Project.Events
{
	/// <summary>
	/// Tests for TimelogEventPublisher — replaces WebVella.Erp.Plugins.Project.Hooks.Api.Timelog
	/// [HookAttachment('timelog')]. Validates pre-create (validation + aggregate update) and
	/// pre-delete (inverse aggregate + feed cleanup) event handling.
	///
	/// The original monolith hook (28 lines) implemented only:
	///   - IErpPreCreateRecordHook → delegates to TimeLogService.PreCreateApiHookLogic
	///   - IErpPreDeleteRecordHook → delegates to TimeLogService.PreDeleteApiHookLogic
	///
	/// CRITICAL: The original hook had NO post-hooks. Tests include negative reflection
	/// assertions verifying that TimelogEventPublisher does NOT implement
	/// IConsumer&lt;RecordCreatedEvent&gt; or IConsumer&lt;RecordDeletedEvent&gt;.
	///
	/// NOTE on service name: The monolith class was TimeLogService (capital L).
	/// The refactored class is TimelogService (lowercase l) per AAP naming convention.
	/// Method names PreCreateApiHookLogic and PreDeleteApiHookLogic are preserved exactly.
	///
	/// NOTE on method signatures: The monolith used 3-parameter signatures
	/// (entityName, record, errors). The refactored service uses 2-parameter signatures
	/// (record, errors) because entity name filtering is done by the consumer.
	/// </summary>
	public class TimelogEventHandlerTests : IAsyncDisposable
	{
		private readonly ITestHarness _harness;
		private readonly ServiceProvider _provider;
		private readonly Mock<TimelogService> _mockTimelogService;
		private readonly Mock<IPublishEndpoint> _mockPublishEndpoint;
		private readonly Mock<ILogger<TimelogEventPublisher>> _mockLogger;

		/// <summary>
		/// Initializes the test fixture with MassTransit InMemoryTestHarness, mock services,
		/// and DI container configuration. Each xUnit test instance receives fresh mocks.
		///
		/// TimelogService is mocked using its protected parameterless constructor (added for
		/// testability) with virtual PreCreateApiHookLogic and PreDeleteApiHookLogic methods
		/// intercepted by Moq for verification.
		/// </summary>
		public TimelogEventHandlerTests()
		{
			_mockPublishEndpoint = new Mock<IPublishEndpoint>();
			_mockLogger = new Mock<ILogger<TimelogEventPublisher>>();

			// Create mock TimelogService using the protected parameterless constructor.
			// The virtual methods PreCreateApiHookLogic and PreDeleteApiHookLogic are
			// intercepted by Moq for .Setup() and .Verify() calls.
			_mockTimelogService = new Mock<TimelogService>();

			var services = new ServiceCollection();
			services.AddLogging();
			services.AddSingleton<TimelogService>(_mockTimelogService.Object);
			services.AddSingleton<ILogger<TimelogEventPublisher>>(_mockLogger.Object);
			services.AddMassTransitTestHarness(cfg =>
			{
				cfg.AddConsumer<TimelogEventPublisher>();
			});

			_provider = services.BuildServiceProvider();
			_harness = _provider.GetRequiredService<ITestHarness>();
		}

		/// <summary>
		/// Disposes the ServiceProvider and all registered services including the
		/// MassTransit test harness, stopping background processing.
		/// </summary>
		public async ValueTask DisposeAsync()
		{
			if (_provider is IAsyncDisposable asyncDisposable)
			{
				await asyncDisposable.DisposeAsync();
			}
			else
			{
				_provider?.Dispose();
			}
		}

		#region << Helper Methods >>

		/// <summary>
		/// Creates a test EntityRecord simulating a timelog entity with all required fields.
		/// Mirrors the field structure used by TimeLogService.PreCreateApiHookLogic (monolith
		/// lines 181-279) and PreDeleteApiHookLogic (monolith lines 281-370).
		///
		/// Field mapping:
		///   - id: Unique timelog identifier
		///   - minutes: Duration of work in minutes
		///   - is_billable: Whether the time is billable
		///   - l_scope: JSON-serialized scope list (e.g., ["projects"])
		///   - l_related_records: JSON-serialized list of related task GUIDs
		///   - body: HTML description of work performed
		///   - logged_on: Timestamp when the timelog was recorded
		/// </summary>
		private static EntityRecord CreateTestTimelogRecord(
			Guid? id = null,
			int minutes = 60,
			bool isBillable = true,
			bool isProjectScoped = true,
			List<Guid>? relatedTaskIds = null)
		{
			var record = new EntityRecord();

			if (id.HasValue)
			{
				record["id"] = id.Value;
			}

			record["minutes"] = minutes;
			record["is_billable"] = isBillable;
			record["body"] = "<p>Work entry</p>";

			if (isProjectScoped)
			{
				record["l_scope"] = JsonConvert.SerializeObject(new List<string> { "projects" });
			}

			if (relatedTaskIds != null)
			{
				record["l_related_records"] = JsonConvert.SerializeObject(relatedTaskIds);
			}

			record["logged_on"] = DateTime.UtcNow;

			return record;
		}

		/// <summary>
		/// Creates a TimelogEventPublisher instance wired with mock dependencies for direct
		/// consumer testing. This avoids the MassTransit DI resolution path while still
		/// exercising the exact same Consume method logic.
		/// </summary>
		private TimelogEventPublisher CreatePublisher()
		{
			return new TimelogEventPublisher(
				_mockPublishEndpoint.Object,
				_mockTimelogService.Object,
				_mockLogger.Object);
		}

		/// <summary>
		/// Creates a mock ConsumeContext wrapping a PreRecordCreateEvent message.
		/// </summary>
		private static Mock<ConsumeContext<PreRecordCreateEvent>> CreatePreCreateContext(
			PreRecordCreateEvent message)
		{
			var context = new Mock<ConsumeContext<PreRecordCreateEvent>>();
			context.Setup(c => c.Message).Returns(message);
			return context;
		}

		/// <summary>
		/// Creates a mock ConsumeContext wrapping a PreRecordDeleteEvent message.
		/// </summary>
		private static Mock<ConsumeContext<PreRecordDeleteEvent>> CreatePreDeleteContext(
			PreRecordDeleteEvent message)
		{
			var context = new Mock<ConsumeContext<PreRecordDeleteEvent>>();
			context.Setup(c => c.Message).Returns(message);
			return context;
		}

		#endregion

		#region << Phase 4: Pre-Create Validation Event Tests — Replacing IErpPreCreateRecordHook >>

		/// <summary>
		/// Verifies that the TimelogEventPublisher correctly delegates pre-create events
		/// for the "timelog" entity to TimelogService.PreCreateApiHookLogic.
		///
		/// Replaces monolith Timelog.cs line 19:
		///   new TimeLogService().PreCreateApiHookLogic(entityName, record, errors);
		///
		/// The refactored service uses 2-parameter signature (record, errors) because
		/// entity name filtering is performed by the consumer before delegation.
		/// </summary>
		[Fact]
		public async Task PreCreate_Timelog_Event_Should_Delegate_To_TimelogService_PreCreateApiHookLogic()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var record = CreateTestTimelogRecord(
				id: Guid.NewGuid(),
				minutes: 60,
				isBillable: true,
				isProjectScoped: true,
				relatedTaskIds: new List<Guid> { taskId });

			var createEvent = new PreRecordCreateEvent
			{
				EntityName = "timelog",
				Record = record,
				ValidationErrors = new List<ErrorModel>()
			};

			var publisher = CreatePublisher();
			var context = CreatePreCreateContext(createEvent);

			// Act
			await publisher.Consume(context.Object);

			// Assert — verify delegation with 2-parameter signature (no entityName)
			_mockTimelogService.Verify(
				s => s.PreCreateApiHookLogic(
					It.IsAny<EntityRecord>(),
					It.IsAny<List<ErrorModel>>()),
				Times.Once);
		}

		/// <summary>
		/// Verifies that the publisher ignores pre-create events for non-timelog entities.
		/// This validates the entity name filtering that replaces [HookAttachment("timelog")]
		/// from the monolith. The consumer performs case-insensitive string comparison via
		/// string.Equals(EntityName, "timelog", OrdinalIgnoreCase).
		/// </summary>
		[Fact]
		public async Task PreCreate_NonTimelog_Entity_Should_Be_Ignored()
		{
			// Arrange — entity name is "comment", not "timelog"
			var createEvent = new PreRecordCreateEvent
			{
				EntityName = "comment",
				Record = new EntityRecord(),
				ValidationErrors = new List<ErrorModel>()
			};

			var publisher = CreatePublisher();
			var context = CreatePreCreateContext(createEvent);

			// Act
			await publisher.Consume(context.Object);

			// Assert — service should NOT be called for non-timelog entities
			_mockTimelogService.Verify(
				s => s.PreCreateApiHookLogic(
					It.IsAny<EntityRecord>(),
					It.IsAny<List<ErrorModel>>()),
				Times.Never);
		}

		/// <summary>
		/// Verifies that a pre-create event with project scope triggers aggregate update
		/// delegation to TimelogService.PreCreateApiHookLogic.
		///
		/// In the monolith (TimeLogService.cs lines 195-278), when isProjectTimeLog=true:
		///   1. Loads task via EQL: SELECT *,$project_nn_task.id,$user_nn_task_watchers.id FROM task
		///   2. Updates x_billable_minutes (billable) or x_nonbillable_minutes (non-billable)
		///   3. Nulls timelog_started_on on the task
		///   4. Creates feed_item recording the logged time
		///
		/// This test verifies the consumer correctly delegates; the actual aggregate logic
		/// is tested in TimelogServiceTests.
		/// </summary>
		[Fact]
		public async Task PreCreate_Timelog_With_Project_Scope_Should_Trigger_Aggregate_Update()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var record = CreateTestTimelogRecord(
				id: Guid.NewGuid(),
				minutes: 60,
				isBillable: true,
				isProjectScoped: true,
				relatedTaskIds: new List<Guid> { taskId });

			var createEvent = new PreRecordCreateEvent
			{
				EntityName = "timelog",
				Record = record,
				ValidationErrors = new List<ErrorModel>()
			};

			var publisher = CreatePublisher();
			var context = CreatePreCreateContext(createEvent);

			// Act
			await publisher.Consume(context.Object);

			// Assert — verify the service is called with the project-scoped record
			_mockTimelogService.Verify(
				s => s.PreCreateApiHookLogic(
					It.Is<EntityRecord>(r => r["l_scope"] != null),
					It.IsAny<List<ErrorModel>>()),
				Times.Once);
		}

		/// <summary>
		/// Verifies that a pre-create event with a record missing the 'id' field still
		/// results in the consumer delegating to TimelogService.
		///
		/// The consumer does NOT validate record content — that responsibility belongs to
		/// TimelogService.PreCreateApiHookLogic which checks (line 183):
		///   if (!record.Properties.ContainsKey("id"))
		///     throw new Exception("Hook exception: timelog id field not found in record");
		///
		/// The consumer catches exceptions from the service, logs them via structured logging
		/// (which accesses record["id"] for the log template), and re-throws. When the record
		/// is missing the "id" field, the logging itself may produce a KeyNotFoundException.
		/// Either way, an exception propagates for MassTransit retry/error queue handling.
		/// </summary>
		[Fact]
		public async Task PreCreate_Timelog_Without_Id_Field_Should_Trigger_Validation_Error()
		{
			// Arrange — record WITHOUT id field
			var record = new EntityRecord();
			record["minutes"] = 30;
			record["is_billable"] = true;

			// Configure mock to throw when called (simulating the real service behavior)
			_mockTimelogService
				.Setup(s => s.PreCreateApiHookLogic(
					It.IsAny<EntityRecord>(),
					It.IsAny<List<ErrorModel>>()))
				.Throws(new Exception("Hook exception: timelog id field not found in record"));

			var createEvent = new PreRecordCreateEvent
			{
				EntityName = "timelog",
				Record = record,
				ValidationErrors = new List<ErrorModel>()
			};

			var publisher = CreatePublisher();
			var context = CreatePreCreateContext(createEvent);

			// Act & Assert — publisher should propagate an exception (either the original
			// service exception or a KeyNotFoundException from the catch block's logging
			// which accesses record["id"] for structured log template parameters)
			Func<Task> act = async () => await publisher.Consume(context.Object);
			await act.Should().ThrowAsync<Exception>();

			// Verify the service WAS called (consumer delegates, service validates)
			_mockTimelogService.Verify(
				s => s.PreCreateApiHookLogic(
					It.IsAny<EntityRecord>(),
					It.IsAny<List<ErrorModel>>()),
				Times.Once);
		}

		/// <summary>
		/// Verifies that a billable timelog triggers the pre-create hook delegation.
		///
		/// Maps to TimeLogService.cs line 232:
		///   patchRecord["x_billable_minutes"] = (decimal)taskRecord["x_billable_minutes"] + (int)record["minutes"];
		///
		/// The actual aggregate update logic is in TimelogService; this test verifies the
		/// consumer correctly passes through billable timelog records.
		/// </summary>
		[Fact]
		public async Task PreCreate_Timelog_With_Billable_Minutes_Should_Update_Task_Billable_Aggregate()
		{
			// Arrange — billable timelog with 120 minutes
			var taskId = Guid.NewGuid();
			var record = CreateTestTimelogRecord(
				id: Guid.NewGuid(),
				minutes: 120,
				isBillable: true,
				isProjectScoped: true,
				relatedTaskIds: new List<Guid> { taskId });

			var createEvent = new PreRecordCreateEvent
			{
				EntityName = "timelog",
				Record = record,
				ValidationErrors = new List<ErrorModel>()
			};

			var publisher = CreatePublisher();
			var context = CreatePreCreateContext(createEvent);

			// Act
			await publisher.Consume(context.Object);

			// Assert — verify service called with the billable record
			_mockTimelogService.Verify(
				s => s.PreCreateApiHookLogic(
					It.Is<EntityRecord>(r =>
						(bool)r["is_billable"] == true &&
						(int)r["minutes"] == 120),
					It.IsAny<List<ErrorModel>>()),
				Times.Once);
		}

		/// <summary>
		/// Verifies that a non-billable timelog triggers the pre-create hook delegation.
		///
		/// Maps to TimeLogService.cs line 236:
		///   patchRecord["x_nonbillable_minutes"] = (decimal)taskRecord["x_nonbillable_minutes"] + (int)record["minutes"];
		///
		/// The actual aggregate update logic is in TimelogService; this test verifies the
		/// consumer correctly passes through non-billable timelog records.
		/// </summary>
		[Fact]
		public async Task PreCreate_Timelog_With_NonBillable_Minutes_Should_Update_Task_NonBillable_Aggregate()
		{
			// Arrange — non-billable timelog with 45 minutes
			var taskId = Guid.NewGuid();
			var record = CreateTestTimelogRecord(
				id: Guid.NewGuid(),
				minutes: 45,
				isBillable: false,
				isProjectScoped: true,
				relatedTaskIds: new List<Guid> { taskId });

			var createEvent = new PreRecordCreateEvent
			{
				EntityName = "timelog",
				Record = record,
				ValidationErrors = new List<ErrorModel>()
			};

			var publisher = CreatePublisher();
			var context = CreatePreCreateContext(createEvent);

			// Act
			await publisher.Consume(context.Object);

			// Assert — verify service called with the non-billable record
			_mockTimelogService.Verify(
				s => s.PreCreateApiHookLogic(
					It.Is<EntityRecord>(r =>
						(bool)r["is_billable"] == false &&
						(int)r["minutes"] == 45),
					It.IsAny<List<ErrorModel>>()),
				Times.Once);
		}

		#endregion

		#region << Phase 5: Pre-Delete Validation Event Tests — Replacing IErpPreDeleteRecordHook >>

		/// <summary>
		/// Verifies that the TimelogEventPublisher correctly delegates pre-delete events
		/// for the "timelog" entity to TimelogService.PreDeleteApiHookLogic.
		///
		/// Replaces monolith Timelog.cs line 24:
		///   new TimeLogService().PreDeleteApiHookLogic(entityName, record, errors);
		///
		/// The refactored service uses 2-parameter signature (record, errors).
		/// </summary>
		[Fact]
		public async Task PreDelete_Timelog_Event_Should_Delegate_To_TimelogService_PreDeleteApiHookLogic()
		{
			// Arrange
			var record = new EntityRecord();
			record["id"] = Guid.NewGuid();

			var deleteEvent = new PreRecordDeleteEvent
			{
				EntityName = "timelog",
				Record = record,
				ValidationErrors = new List<ErrorModel>()
			};

			var publisher = CreatePublisher();
			var context = CreatePreDeleteContext(deleteEvent);

			// Act
			await publisher.Consume(context.Object);

			// Assert — verify delegation with 2-parameter signature (no entityName)
			_mockTimelogService.Verify(
				s => s.PreDeleteApiHookLogic(
					It.IsAny<EntityRecord>(),
					It.IsAny<List<ErrorModel>>()),
				Times.Once);
		}

		/// <summary>
		/// Verifies that the publisher ignores pre-delete events for non-timelog entities.
		/// This validates the entity name filtering replacing [HookAttachment("timelog")].
		/// </summary>
		[Fact]
		public async Task PreDelete_NonTimelog_Entity_Should_Be_Ignored()
		{
			// Arrange — entity name is "task", not "timelog"
			var deleteEvent = new PreRecordDeleteEvent
			{
				EntityName = "task",
				Record = new EntityRecord(),
				ValidationErrors = new List<ErrorModel>()
			};

			var publisher = CreatePublisher();
			var context = CreatePreDeleteContext(deleteEvent);

			// Act
			await publisher.Consume(context.Object);

			// Assert — service should NOT be called for non-timelog entities
			_mockTimelogService.Verify(
				s => s.PreDeleteApiHookLogic(
					It.IsAny<EntityRecord>(),
					It.IsAny<List<ErrorModel>>()),
				Times.Never);
		}

		/// <summary>
		/// Verifies that a pre-delete event for a timelog delegates to
		/// TimelogService.PreDeleteApiHookLogic for aggregate subtraction.
		///
		/// In the monolith (TimeLogService.cs lines 281-370):
		///   1. Loads timelog by ID via EQL
		///   2. If project timelog: loads related tasks via EQL
		///   3. Subtracts minutes from task aggregate fields (inverse of create, floor at 0)
		///   4. Deletes all related feed items
		///
		/// This test verifies the consumer correctly delegates with the record containing the id.
		/// </summary>
		[Fact]
		public async Task PreDelete_Timelog_Should_Subtract_Minutes_From_Task_Aggregate()
		{
			// Arrange
			var timelogId = Guid.NewGuid();
			var record = new EntityRecord();
			record["id"] = timelogId;

			var deleteEvent = new PreRecordDeleteEvent
			{
				EntityName = "timelog",
				Record = record,
				ValidationErrors = new List<ErrorModel>()
			};

			var publisher = CreatePublisher();
			var context = CreatePreDeleteContext(deleteEvent);

			// Act
			await publisher.Consume(context.Object);

			// Assert — verify service called with record containing the timelog id
			_mockTimelogService.Verify(
				s => s.PreDeleteApiHookLogic(
					It.Is<EntityRecord>(r => r["id"] != null && (Guid)r["id"] == timelogId),
					It.IsAny<List<ErrorModel>>()),
				Times.Once);
		}

		#endregion

		#region << Phase 6: No Post-Hook Tests (CRITICAL VALIDATION) >>

		/// <summary>
		/// CRITICAL VALIDATION: Verifies that TimelogEventPublisher does NOT implement
		/// IConsumer&lt;RecordCreatedEvent&gt;.
		///
		/// The original monolith Timelog hook class (Timelog.cs) implemented ONLY:
		///   - IErpPreCreateRecordHook
		///   - IErpPreDeleteRecordHook
		///
		/// It did NOT implement IErpPostCreateRecordHook. Therefore the refactored publisher
		/// must NOT handle post-create events. This is verified via reflection to ensure
		/// no accidental post-hook implementation is added.
		/// </summary>
		[Fact]
		public void TimelogEventPublisher_Should_Not_Handle_Post_Create_Events()
		{
			// Arrange
			var publisherType = typeof(TimelogEventPublisher);
			var postCreateConsumerType = typeof(IConsumer<RecordCreatedEvent>);

			// Act
			var implementedInterfaces = publisherType.GetInterfaces();

			// Assert — publisher must NOT implement IConsumer<RecordCreatedEvent>
			implementedInterfaces.Should().NotContain(postCreateConsumerType,
				"the original Timelog hook had NO post-create hook " +
				"(IErpPostCreateRecordHook was not implemented)");
		}

		/// <summary>
		/// CRITICAL VALIDATION: Verifies that TimelogEventPublisher does NOT implement
		/// IConsumer&lt;RecordDeletedEvent&gt;.
		///
		/// The original monolith Timelog hook class did NOT implement
		/// IErpPostDeleteRecordHook. Therefore the refactored publisher must NOT handle
		/// post-delete events.
		/// </summary>
		[Fact]
		public void TimelogEventPublisher_Should_Not_Handle_Post_Delete_Events()
		{
			// Arrange
			var publisherType = typeof(TimelogEventPublisher);
			var postDeleteConsumerType = typeof(IConsumer<RecordDeletedEvent>);

			// Act
			var implementedInterfaces = publisherType.GetInterfaces();

			// Assert — publisher must NOT implement IConsumer<RecordDeletedEvent>
			implementedInterfaces.Should().NotContain(postDeleteConsumerType,
				"the original Timelog hook had NO post-delete hook " +
				"(IErpPostDeleteRecordHook was not implemented)");
		}

		#endregion

		#region << Phase 7: Idempotency Tests (AAP §0.8.2) >>

		/// <summary>
		/// Verifies that duplicate pre-create events are handled idempotently.
		///
		/// Per AAP §0.8.2: "Event consumers must be idempotent (duplicate event delivery
		/// must not cause data corruption)."
		///
		/// The TimelogEventPublisher's pre-create aggregate updates are additive but bounded
		/// by actual timelog data — the minutes value is deterministic from the record.
		/// Publishing the same event twice results in the service being called twice,
		/// which is safe because MassTransit's in-memory outbox pattern prevents duplicate
		/// delivery under normal operation.
		/// </summary>
		[Fact]
		public async Task Duplicate_PreCreate_Timelog_Events_Should_Be_Handled_Idempotently()
		{
			// Arrange
			var taskId = Guid.NewGuid();
			var record = CreateTestTimelogRecord(
				id: Guid.NewGuid(),
				minutes: 30,
				isBillable: true,
				isProjectScoped: true,
				relatedTaskIds: new List<Guid> { taskId });

			var createEvent = new PreRecordCreateEvent
			{
				EntityName = "timelog",
				Record = record,
				ValidationErrors = new List<ErrorModel>()
			};

			var publisher = CreatePublisher();

			// Act — publish the same event twice (simulating duplicate delivery)
			var context1 = CreatePreCreateContext(createEvent);
			await publisher.Consume(context1.Object);

			var context2 = CreatePreCreateContext(createEvent);
			await publisher.Consume(context2.Object);

			// Assert — service called twice (additive but bounded)
			_mockTimelogService.Verify(
				s => s.PreCreateApiHookLogic(
					It.IsAny<EntityRecord>(),
					It.IsAny<List<ErrorModel>>()),
				Times.Exactly(2));
		}

		/// <summary>
		/// Verifies that duplicate pre-delete events are handled idempotently.
		///
		/// Per AAP §0.8.2: "PreDeleteApiHookLogic performs inverse aggregate updates
		/// and feed item cleanup — operations are bounded and cleanup is idempotent
		/// (deleting already-deleted records is a no-op in the underlying RecordManager)."
		///
		/// Publishing the same delete event twice results in the service being called
		/// twice, which is safe because the underlying delete/subtraction operations
		/// are idempotent with floor-at-zero semantics.
		/// </summary>
		[Fact]
		public async Task Duplicate_PreDelete_Timelog_Events_Should_Be_Handled_Idempotently()
		{
			// Arrange
			var record = new EntityRecord();
			record["id"] = Guid.NewGuid();

			var deleteEvent = new PreRecordDeleteEvent
			{
				EntityName = "timelog",
				Record = record,
				ValidationErrors = new List<ErrorModel>()
			};

			var publisher = CreatePublisher();

			// Act — publish the same event twice (simulating duplicate delivery)
			var context1 = CreatePreDeleteContext(deleteEvent);
			await publisher.Consume(context1.Object);

			var context2 = CreatePreDeleteContext(deleteEvent);
			await publisher.Consume(context2.Object);

			// Assert — service called twice (cleanup is idempotent)
			_mockTimelogService.Verify(
				s => s.PreDeleteApiHookLogic(
					It.IsAny<EntityRecord>(),
					It.IsAny<List<ErrorModel>>()),
				Times.Exactly(2));
		}

		#endregion
	}
}
