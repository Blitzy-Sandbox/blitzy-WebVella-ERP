using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Core.Api;

namespace WebVella.Erp.Service.Project.Domain.Services
{
	/// <summary>
	/// Feed item writer service for the Project microservice.
	///
	/// Extracted from the monolith's <c>WebVella.Erp.Plugins.Project.Services.FeedItemService</c>
	/// (55 lines). All business logic is preserved verbatim from the source, with only the
	/// following microservice-specific adaptations:
	///
	/// <list type="number">
	///   <item>Class renamed from <c>FeedItemService</c> to <c>FeedService</c> per AAP target naming</item>
	///   <item>Namespace changed from <c>WebVella.Erp.Plugins.Project.Services</c> to
	///         <c>WebVella.Erp.Service.Project.Domain.Services</c></item>
	///   <item><c>BaseService</c> inheritance removed; replaced with constructor dependency injection</item>
	///   <item><c>new RecordManager()</c> replaced with injected <c>_recordManager</c> instance</item>
	///   <item>Import statements updated to reference SharedKernel namespaces
	///         (<c>WebVella.Erp.SharedKernel.Models</c> for <see cref="EntityRecord"/> and
	///         <see cref="SystemIds"/>)</item>
	///   <item><see cref="ILogger{T}"/> added for structured logging in the microservice environment</item>
	/// </list>
	///
	/// <para>
	/// <b>Foundational Service:</b> This is the most foundational service in the Project
	/// microservice — it is consumed by <c>TaskService</c>, <c>TimelogService</c>, and
	/// <c>CommentService</c> to create activity feed entries for user actions.
	/// </para>
	///
	/// <para>
	/// <b>Entity Ownership:</b> The <c>feed_item</c> entity table is owned by the Project
	/// service in the database-per-service model (AAP Section 0.7.1).
	/// </para>
	/// </summary>
	public class FeedService
	{
		private readonly RecordManager _recordManager;
		private readonly ILogger<FeedService> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="FeedService"/> class.
		/// Replaces the monolith's <c>BaseService</c> inheritance and
		/// <c>new RecordManager()</c> instantiation with constructor dependency injection.
		/// </summary>
		/// <param name="recordManager">
		/// Record CRUD orchestrator injected via DI, replacing the monolith pattern of
		/// <c>new RecordManager()</c>. Used to persist feed_item records via
		/// <see cref="RecordManager.CreateRecord(string, EntityRecord)"/>.
		/// </param>
		/// <param name="logger">
		/// Structured logger for distributed tracing and observability, replacing
		/// the monolith's implicit logging patterns.
		/// </param>
		public FeedService(RecordManager recordManager, ILogger<FeedService> logger)
		{
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <summary>
		/// Creates a new feed item record in the <c>feed_item</c> entity table.
		///
		/// Preserved verbatim from the monolith's <c>FeedItemService.Create()</c>
		/// (source lines 15-52). All parameter defaults, field assignments, JSON
		/// serialization, and error handling patterns are identical to the source.
		///
		/// <para><b>Default Values:</b></para>
		/// <list type="bullet">
		///   <item><paramref name="id"/>: <c>Guid.NewGuid()</c> if null</item>
		///   <item><paramref name="createdBy"/>: <c>SystemIds.SystemUserId</c> if null</item>
		///   <item><paramref name="createdOn"/>: <c>DateTime.Now</c> if null (deliberately
		///         uses local time, NOT <c>DateTime.UtcNow</c>, preserving source behavior)</item>
		///   <item><paramref name="type"/>: <c>"system"</c> if null or whitespace</item>
		/// </list>
		///
		/// <para><b>JSON Serialization:</b> The <paramref name="relatedRecords"/> and
		/// <paramref name="scope"/> lists are serialized to JSON strings via
		/// <c>JsonConvert.SerializeObject()</c> before storage in the <c>l_related_records</c>
		/// and <c>l_scope</c> fields respectively.</para>
		/// </summary>
		/// <param name="id">Optional record ID. Defaults to <c>Guid.NewGuid()</c>.</param>
		/// <param name="createdBy">Optional creator user ID. Defaults to <c>SystemIds.SystemUserId</c>.</param>
		/// <param name="createdOn">Optional creation timestamp. Defaults to <c>DateTime.Now</c>.</param>
		/// <param name="subject">Feed item subject line. Defaults to empty string.</param>
		/// <param name="body">Feed item body content. Defaults to empty string.</param>
		/// <param name="relatedRecords">
		/// List of related record identifiers (as strings, NOT Guids — preserving exact
		/// source type). Serialized to JSON for storage in <c>l_related_records</c> field.
		/// </param>
		/// <param name="scope">
		/// List of scope identifiers (as strings). Serialized to JSON for storage in
		/// <c>l_scope</c> field.
		/// </param>
		/// <param name="type">Feed item type. Defaults to <c>"system"</c> if null or whitespace.</param>
		/// <exception cref="Exception">
		/// Thrown when <see cref="RecordManager.CreateRecord(string, EntityRecord)"/> returns
		/// a non-success response. The exception message contains the response's error message.
		/// </exception>
		public void Create(Guid? id = null, Guid? createdBy = null, DateTime? createdOn = null,
			string subject = "", string body = "", List<string> relatedRecords = null,
			List<string> scope = null, string type = "")
		{
			#region << Init >>
			if (id == null)
				id = Guid.NewGuid();

			if (createdBy == null)
				createdBy = SystemIds.SystemUserId;

			if (createdOn == null)
				createdOn = DateTime.Now;

			if (String.IsNullOrWhiteSpace(type))
			{
				type = "system";
			}
			#endregion
			try
			{
				var record = new EntityRecord();
				record["id"] = id;
				record["created_by"] = createdBy;
				record["created_on"] = createdOn;
				record["subject"] = subject;
				record["body"] = body;
				record["l_related_records"] = JsonConvert.SerializeObject(relatedRecords);
				record["l_scope"] = JsonConvert.SerializeObject(scope);
				record["type"] = type;
				var createFeedResponse = _recordManager.CreateRecord("feed_item", record);
				if (!createFeedResponse.Success)
					throw new Exception(createFeedResponse.Message);
			}
			catch (Exception)
			{
				throw;
			}
		}
	}
}
