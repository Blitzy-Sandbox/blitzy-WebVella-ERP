using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Xunit;
using Moq;
using FluentAssertions;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using MassTransit;
using WebVella.Erp.Service.Project.Domain.Services;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.Project.Services
{
	/// <summary>
	/// Comprehensive unit tests for <see cref="FeedService.Create"/> covering all business
	/// rules extracted from the monolith's <c>FeedItemService.cs</c> (55 lines).
	///
	/// The monolith's <c>FeedItemService</c> was refactored to <c>FeedService</c> with
	/// constructor dependency injection replacing the <c>BaseService</c> inheritance and
	/// <c>new RecordManager()</c> instantiation pattern.
	///
	/// Business rules tested (8 tests, covering all branches for ≥80% code coverage):
	/// <list type="bullet">
	///   <item>Default ID generation — <c>Guid.NewGuid()</c> when <c>id</c> is null (source line 20-21)</item>
	///   <item>Default creator — <c>SystemIds.SystemUserId</c> when <c>createdBy</c> is null (source line 23-24)</item>
	///   <item>Default timestamp — <c>DateTime.Now</c> when <c>createdOn</c> is null (source line 26-27)</item>
	///   <item>Default type — <c>"system"</c> when <c>type</c> is null or whitespace (source line 29-31)</item>
	///   <item>Type preservation when an explicit non-empty value is provided</item>
	///   <item>JSON serialization of <c>relatedRecords</c> → <c>l_related_records</c> field (source line 41)</item>
	///   <item>JSON serialization of <c>scope</c> → <c>l_scope</c> field (source line 42)</item>
	///   <item>Subject and body field mapping (source lines 39-40)</item>
	///   <item>Error handling — <c>Exception</c> thrown when <c>RecordManager.CreateRecord</c> fails (source line 45-46)</item>
	///   <item>Explicit value preservation — provided <c>id</c>, <c>createdBy</c>, <c>createdOn</c> override defaults</item>
	/// </list>
	///
	/// Per AAP 0.8.1: every business rule maps to at least one automated test.
	/// Per AAP 0.8.2: all business logic classes must have ≥80% code coverage.
	/// </summary>
	public class FeedItemServiceTests
	{
		private readonly Mock<RecordManager> _mockRecordManager;
		private readonly Mock<ILogger<FeedService>> _mockLogger;
		private readonly FeedService _sut;

		/// <summary>
		/// Initializes fresh mock dependencies for each test method.
		/// xUnit creates a new test class instance per test, ensuring full isolation.
		///
		/// <para><b>Mock Construction Strategy:</b></para>
		/// <c>RecordManager</c> has a complex constructor requiring <c>CoreDbContext</c>,
		/// <c>EntityManager</c>, <c>EntityRelationManager</c>, and <c>IPublishEndpoint</c>.
		/// Since <c>CoreDbContext</c> has only a private constructor and <c>EntityManager</c>/
		/// <c>EntityRelationManager</c> require <c>CoreDbContext</c> + <c>IConfiguration</c>,
		/// we use <see cref="RuntimeHelpers.GetUninitializedObject"/> to create instances
		/// without invoking constructors. These uninitialized objects satisfy
		/// <c>RecordManager</c>'s null checks while remaining inert — only the virtual
		/// <c>CreateRecord</c> method is mocked and exercised during tests.
		/// </summary>
		public FeedItemServiceTests()
		{
			// Create uninitialized dependency instances for RecordManager's constructor.
			// GetUninitializedObject bypasses all constructors — fields stay at default values.
			// This is safe because we mock the virtual CreateRecord method and never call
			// any real database operations on these objects.
			var dbContext = (CoreDbContext)RuntimeHelpers.GetUninitializedObject(typeof(CoreDbContext));
			var entityManager = (EntityManager)RuntimeHelpers.GetUninitializedObject(typeof(EntityManager));
			var relationManager = (EntityRelationManager)RuntimeHelpers.GetUninitializedObject(typeof(EntityRelationManager));
			var publishEndpoint = Mock.Of<IPublishEndpoint>();

			_mockRecordManager = new Mock<RecordManager>(
				dbContext, entityManager, relationManager, publishEndpoint, false, true);
			_mockLogger = new Mock<ILogger<FeedService>>();
			_sut = new FeedService(_mockRecordManager.Object, _mockLogger.Object);
		}

		#region Test 2.1 — Create_WithDefaults_ShouldGenerateIdAndSetSystemUser

		/// <summary>
		/// Validates that calling <c>Create()</c> with all default parameters (null id, null
		/// createdBy, null createdOn) triggers the default initialization logic:
		/// <list type="bullet">
		///   <item><c>id</c> → <c>Guid.NewGuid()</c> (source line 20-21)</item>
		///   <item><c>createdBy</c> → <c>SystemIds.SystemUserId</c> (source line 23-24)</item>
		///   <item><c>createdOn</c> → <c>DateTime.Now</c> (source line 26-27)</item>
		/// </list>
		/// Also verifies that <c>RecordManager.CreateRecord</c> is called exactly once
		/// with the entity name <c>"feed_item"</c>.
		/// </summary>
		[Fact]
		public void Create_WithDefaults_ShouldGenerateIdAndSetSystemUser()
		{
			// Arrange — capture the EntityRecord passed to CreateRecord
			EntityRecord capturedRecord = null;
			_mockRecordManager
				.Setup(x => x.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((name, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });

			// Act — call with all defaults (null id, null createdBy, null createdOn)
			_sut.Create();

			// Assert — verify default values were applied
			capturedRecord.Should().NotBeNull("CreateRecord should have been called");

			// id should be a non-empty Guid (auto-generated via Guid.NewGuid())
			var recordId = (Guid?)capturedRecord["id"];
			recordId.Should().NotBeNull("id should have been assigned");
			recordId.Value.Should().NotBe(Guid.Empty,
				"a new Guid should be generated when id is null");

			// createdBy should default to SystemIds.SystemUserId
			var createdBy = (Guid?)capturedRecord["created_by"];
			createdBy.Should().Be(SystemIds.SystemUserId,
				"when createdBy is null, it should default to SystemIds.SystemUserId");

			// createdOn should default to approximately DateTime.Now
			var createdOn = (DateTime?)capturedRecord["created_on"];
			createdOn.Should().NotBeNull("createdOn should have been assigned");
			createdOn.Value.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(5),
				"when createdOn is null, it should default to DateTime.Now");

			// Verify CreateRecord was called exactly once with entity name "feed_item"
			_mockRecordManager.Verify(
				x => x.CreateRecord(
					It.Is<string>(s => s == "feed_item"),
					It.IsAny<EntityRecord>()),
				Times.Once());
		}

		#endregion

		#region Test 2.2 — Create_WithEmptyType_ShouldDefaultToSystem

		/// <summary>
		/// Validates the key business rule: when <c>type</c> is empty or whitespace,
		/// it defaults to <c>"system"</c> (source line 29-31).
		///
		/// Source code:
		/// <code>
		/// if (String.IsNullOrWhiteSpace(type)) {
		///     type = "system";
		/// }
		/// </code>
		/// </summary>
		[Fact]
		public void Create_WithEmptyType_ShouldDefaultToSystem()
		{
			// Arrange — capture the EntityRecord
			EntityRecord capturedRecord = null;
			_mockRecordManager
				.Setup(x => x.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((name, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });

			// Act — empty type string triggers the default-to-"system" branch
			_sut.Create(type: "");

			// Assert — type field should be "system" per business rule
			capturedRecord.Should().NotBeNull();
			capturedRecord["type"].Should().Be("system",
				"when type is empty/whitespace, it should default to 'system' (source line 29-31)");

			// Verify entity name is "feed_item"
			_mockRecordManager.Verify(
				x => x.CreateRecord(
					It.Is<string>(s => s == "feed_item"),
					It.IsAny<EntityRecord>()),
				Times.Once());
		}

		#endregion

		#region Test 2.3 — Create_WithExplicitType_ShouldPreserveType

		/// <summary>
		/// Validates that when a non-empty <c>type</c> is provided, the value is preserved
		/// and NOT overwritten to <c>"system"</c>. Ensures the <c>IsNullOrWhiteSpace</c>
		/// check only triggers for empty/whitespace values.
		/// </summary>
		[Fact]
		public void Create_WithExplicitType_ShouldPreserveType()
		{
			// Arrange — capture the EntityRecord
			EntityRecord capturedRecord = null;
			_mockRecordManager
				.Setup(x => x.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((name, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });

			// Act — explicit type "task" should be preserved
			_sut.Create(type: "task");

			// Assert — type field should remain "task"
			capturedRecord.Should().NotBeNull();
			capturedRecord["type"].Should().Be("task",
				"when type is a non-empty value, it should be preserved as-is (not overwritten to 'system')");

			_mockRecordManager.Verify(
				x => x.CreateRecord(
					It.Is<string>(s => s == "feed_item"),
					It.IsAny<EntityRecord>()),
				Times.Once());
		}

		#endregion

		#region Test 2.4 — Create_ShouldSerializeRelatedRecordsAsJson

		/// <summary>
		/// Validates that the <c>relatedRecords</c> parameter is serialized to JSON via
		/// <c>JsonConvert.SerializeObject(relatedRecords)</c> and stored in the
		/// <c>l_related_records</c> field (source line 41). This preserves the exact
		/// monolith serialization behavior for the activity feed's related record tracking.
		/// </summary>
		[Fact]
		public void Create_ShouldSerializeRelatedRecordsAsJson()
		{
			// Arrange — capture the EntityRecord
			EntityRecord capturedRecord = null;
			_mockRecordManager
				.Setup(x => x.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((name, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });

			var relatedRecords = new List<string> { "guid1", "guid2" };

			// Act
			_sut.Create(relatedRecords: relatedRecords);

			// Assert — l_related_records should contain the JSON-serialized list
			capturedRecord.Should().NotBeNull();
			var expectedJson = JsonConvert.SerializeObject(relatedRecords);
			capturedRecord["l_related_records"].Should().Be(expectedJson,
				"relatedRecords should be serialized to JSON via JsonConvert.SerializeObject (source line 41)");

			_mockRecordManager.Verify(
				x => x.CreateRecord(
					It.Is<string>(s => s == "feed_item"),
					It.IsAny<EntityRecord>()),
				Times.Once());
		}

		#endregion

		#region Test 2.5 — Create_ShouldSerializeScopeAsJson

		/// <summary>
		/// Validates that the <c>scope</c> parameter is serialized to JSON via
		/// <c>JsonConvert.SerializeObject(scope)</c> and stored in the <c>l_scope</c>
		/// field (source line 42). Matches the exact monolith serialization behavior.
		/// </summary>
		[Fact]
		public void Create_ShouldSerializeScopeAsJson()
		{
			// Arrange — capture the EntityRecord
			EntityRecord capturedRecord = null;
			_mockRecordManager
				.Setup(x => x.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((name, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });

			var scope = new List<string> { "projects" };

			// Act
			_sut.Create(scope: scope);

			// Assert — l_scope should contain the JSON-serialized list
			capturedRecord.Should().NotBeNull();
			var expectedJson = JsonConvert.SerializeObject(scope);
			capturedRecord["l_scope"].Should().Be(expectedJson,
				"scope should be serialized to JSON via JsonConvert.SerializeObject (source line 42)");

			_mockRecordManager.Verify(
				x => x.CreateRecord(
					It.Is<string>(s => s == "feed_item"),
					It.IsAny<EntityRecord>()),
				Times.Once());
		}

		#endregion

		#region Test 2.6 — Create_ShouldSetSubjectAndBodyFields

		/// <summary>
		/// Validates that the <c>subject</c> and <c>body</c> parameters are correctly
		/// mapped to the <c>"subject"</c> and <c>"body"</c> fields of the EntityRecord
		/// (source lines 39-40).
		/// </summary>
		[Fact]
		public void Create_ShouldSetSubjectAndBodyFields()
		{
			// Arrange — capture the EntityRecord
			EntityRecord capturedRecord = null;
			_mockRecordManager
				.Setup(x => x.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((name, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });

			// Act — provide subject and body values
			_sut.Create(subject: "test subject", body: "test body");

			// Assert — subject and body fields should be set correctly
			capturedRecord.Should().NotBeNull();
			capturedRecord["subject"].Should().Be("test subject",
				"subject parameter should map to record['subject'] (source line 39)");
			capturedRecord["body"].Should().Be("test body",
				"body parameter should map to record['body'] (source line 40)");

			_mockRecordManager.Verify(
				x => x.CreateRecord(
					It.Is<string>(s => s == "feed_item"),
					It.IsAny<EntityRecord>()),
				Times.Once());
		}

		#endregion

		#region Test 2.7 — Create_WhenRecordManagerFails_ShouldThrowException

		/// <summary>
		/// Validates the critical error handling business rule: when
		/// <c>RecordManager.CreateRecord</c> returns a non-success response
		/// (<c>Success == false</c>), an <c>Exception</c> is thrown with the
		/// response's error message (source lines 45-46).
		///
		/// <para><b>Important:</b> The monolith uses plain <c>Exception</c>,
		/// NOT <c>ValidationException</c> or any custom exception type.</para>
		///
		/// Source code:
		/// <code>
		/// if (!createFeedResponse.Success)
		///     throw new Exception(createFeedResponse.Message);
		/// </code>
		/// </summary>
		[Fact]
		public void Create_WhenRecordManagerFails_ShouldThrowException()
		{
			// Arrange — mock RecordManager to return a failure response
			_mockRecordManager
				.Setup(x => x.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Returns(new QueryResponse { Success = false, Message = "Record creation failed" });

			// Act — call Create which should throw
			Action act = () => _sut.Create(subject: "test");

			// Assert — should throw Exception (NOT ValidationException) with the response message
			act.Should().Throw<Exception>()
				.WithMessage("Record creation failed");

			// Verify CreateRecord was still called exactly once
			_mockRecordManager.Verify(
				x => x.CreateRecord(
					It.Is<string>(s => s == "feed_item"),
					It.IsAny<EntityRecord>()),
				Times.Once());
		}

		#endregion

		#region Test 2.8 — Create_WithExplicitValues_ShouldUseProvidedValues

		/// <summary>
		/// Validates that when explicit values are provided for <c>id</c>,
		/// <c>createdBy</c>, and <c>createdOn</c>, these values are used directly
		/// instead of the defaults (<c>Guid.NewGuid()</c>, <c>SystemIds.SystemUserId</c>,
		/// and <c>DateTime.Now</c> respectively).
		///
		/// This confirms the null-check default branches (source lines 19-27) are only
		/// triggered when parameters are null.
		/// </summary>
		[Fact]
		public void Create_WithExplicitValues_ShouldUseProvidedValues()
		{
			// Arrange — capture the EntityRecord
			EntityRecord capturedRecord = null;
			_mockRecordManager
				.Setup(x => x.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((name, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });

			var explicitId = Guid.NewGuid();
			var explicitCreatedBy = Guid.NewGuid();
			var explicitCreatedOn = new DateTime(2024, 6, 15, 10, 30, 0);

			// Act — provide explicit values for all defaultable parameters
			_sut.Create(
				id: explicitId,
				createdBy: explicitCreatedBy,
				createdOn: explicitCreatedOn);

			// Assert — provided values should be used instead of defaults
			capturedRecord.Should().NotBeNull();

			((Guid?)capturedRecord["id"]).Should().Be(explicitId,
				"explicit id should be used instead of Guid.NewGuid()");
			((Guid?)capturedRecord["created_by"]).Should().Be(explicitCreatedBy,
				"explicit createdBy should be used instead of SystemIds.SystemUserId");
			((DateTime?)capturedRecord["created_on"]).Should().Be(explicitCreatedOn,
				"explicit createdOn should be used instead of DateTime.Now");

			_mockRecordManager.Verify(
				x => x.CreateRecord(
					It.Is<string>(s => s == "feed_item"),
					It.IsAny<EntityRecord>()),
				Times.Once());
		}

		#endregion
	}
}
