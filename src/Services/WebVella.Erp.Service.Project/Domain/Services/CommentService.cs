using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Api;

namespace WebVella.Erp.Service.Project.Domain.Services
{
	/// <summary>
	/// Comment domain service for the Project microservice.
	///
	/// Extracted from the monolith's <c>WebVella.Erp.Plugins.Project.Services.CommentService</c>
	/// (228 lines, inheriting <c>BaseService</c>) with hook logic integrated from
	/// <c>WebVella.Erp.Plugins.Project.Hooks.Api.Comment</c> (24 lines).
	///
	/// All business logic is preserved verbatim from the source, with only the
	/// following microservice-specific adaptations:
	///
	/// <list type="number">
	///   <item>Namespace changed from <c>WebVella.Erp.Plugins.Project.Services</c> to
	///         <c>WebVella.Erp.Service.Project.Domain.Services</c></item>
	///   <item><c>BaseService</c> inheritance removed; replaced with constructor dependency injection
	///         for <see cref="RecordManager"/>, <see cref="EntityRelationManager"/>,
	///         <see cref="FeedService"/>, and <see cref="ILogger{T}"/></item>
	///   <item>All <c>new RecordManager()</c> instantiations replaced with injected <c>_recordManager</c></item>
	///   <item><c>RecMan.</c> property calls replaced with <c>_recordManager.</c></item>
	///   <item><c>new FeedItemService()</c> instantiation replaced with injected <c>_feedService</c></item>
	///   <item><c>new EntityRelationManager()</c> instantiation replaced with injected <c>_relationManager</c></item>
	///   <item><c>new Web.Services.RenderService().GetSnippetFromHtml()</c> replaced with local
	///         private static helper <see cref="GetSnippetFromHtml"/> that extracts plain text from HTML</item>
	///   <item>Import statements updated to reference SharedKernel namespaces
	///         (<c>WebVella.Erp.SharedKernel.Models</c>, <c>WebVella.Erp.SharedKernel.Eql</c>,
	///         <c>WebVella.Erp.SharedKernel.Exceptions</c>, <c>WebVella.Erp.SharedKernel.Security</c>)</item>
	///   <item><see cref="ILogger{T}"/> added for structured logging in the microservice environment</item>
	/// </list>
	///
	/// <para>
	/// <b>Entity Ownership:</b> The <c>comment</c> entity table is owned by the Project
	/// service in the database-per-service model (AAP Section 0.7.1). The <c>task</c> entity
	/// is also owned by the Project service, so all EQL queries in this service are
	/// intra-service queries that remain as-is.
	/// </para>
	///
	/// <para>
	/// <b>Hook Integration:</b> The monolith's <c>Hooks/Api/Comment.cs</c> hook adapter
	/// delegated <c>OnPreCreateRecord</c> and <c>OnPostCreateRecord</c> to this service's
	/// <see cref="PreCreateApiHookLogic"/> and <see cref="PostCreateApiHookLogic"/> methods.
	/// In the microservice, these become domain service methods callable from controllers
	/// or event publishers.
	/// </para>
	///
	/// <para>
	/// <b>Security:</b> <see cref="SecurityContext.CurrentUser"/> is preserved from the
	/// SharedKernel, adapted for JWT token propagation across service boundaries.
	/// </para>
	/// </summary>
	public class CommentService
	{
		private readonly RecordManager _recordManager;
		private readonly EntityRelationManager _relationManager;
		private readonly FeedService _feedService;
		private readonly ILogger<CommentService> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="CommentService"/> class with all
		/// required dependencies injected via constructor DI.
		///
		/// Replaces the monolith's <c>BaseService</c> inheritance pattern which provided
		/// <c>RecMan</c>, <c>EntMan</c>, <c>SecMan</c>, <c>RelMan</c>, and <c>Fs</c>
		/// properties via <c>new</c> instantiation.
		/// </summary>
		/// <param name="recordManager">
		/// Record CRUD orchestrator injected via DI, replacing all monolith
		/// <c>new RecordManager()</c> instantiations and <c>RecMan</c> property accesses.
		/// Used for <c>CreateRecord("comment", record)</c>, <c>DeleteRecord("comment", id)</c>,
		/// and <c>CreateRelationManyToManyRecord(relationId, originId, targetId)</c>.
		/// </param>
		/// <param name="relationManager">
		/// Entity relation metadata manager injected via DI, replacing monolith's
		/// <c>new EntityRelationManager()</c> instantiation. Used to retrieve the
		/// <c>user_nn_task_watchers</c> many-to-many relation definition in
		/// <see cref="PostCreateApiHookLogic"/>.
		/// </param>
		/// <param name="feedService">
		/// Activity feed service injected via DI, replacing monolith's
		/// <c>new FeedItemService()</c> instantiation. Used in <see cref="PreCreateApiHookLogic"/>
		/// to create activity feed items when comments are posted on project tasks.
		/// </param>
		/// <param name="logger">
		/// Structured logger for distributed tracing and observability, replacing
		/// the monolith's implicit logging patterns.
		/// </param>
		public CommentService(
			RecordManager recordManager,
			EntityRelationManager relationManager,
			FeedService feedService,
			ILogger<CommentService> logger)
		{
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_relationManager = relationManager ?? throw new ArgumentNullException(nameof(relationManager));
			_feedService = feedService ?? throw new ArgumentNullException(nameof(feedService));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <summary>
		/// Protected parameterless constructor enabling Moq proxy creation for unit tests.
		/// This constructor is NOT used in production — the DI container always resolves
		/// through the parameterized constructor above.
		/// </summary>
		protected internal CommentService() { }

		#region << Helper Methods >>

		/// <summary>
		/// Extracts plain text snippet from HTML content, replacing the monolith's
		/// <c>new Web.Services.RenderService().GetSnippetFromHtml(html)</c> call.
		/// Uses simple tag-stripping text extraction preserving identical output behavior
		/// for feed body snippets. The monolith implementation uses HtmlAgilityPack to
		/// traverse leaf nodes and concatenate InnerText values, then truncates at 150 chars.
		/// </summary>
		/// <param name="html">HTML content string to extract text from.</param>
		/// <param name="snippetLength">Maximum snippet length before truncation. Defaults to 150.</param>
		/// <returns>Plain text snippet of the HTML content, truncated with "..." if exceeding snippetLength.</returns>
		private static string GetSnippetFromHtml(string html, int snippetLength = 150)
		{
			var result = "";
			if (!string.IsNullOrWhiteSpace(html))
			{
				// Strip HTML tags to extract plain text — mirrors the monolith's
				// HtmlAgilityPack-based implementation that traverses leaf nodes
				// and concatenates InnerText values.
				var sb = new StringBuilder();
				bool inTag = false;
				foreach (char c in html)
				{
					if (c == '<')
					{
						inTag = true;
						continue;
					}
					if (c == '>')
					{
						inTag = false;
						sb.Append(' ');
						continue;
					}
					if (!inTag)
					{
						sb.Append(c);
					}
				}
				// Collapse whitespace and trim
				result = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();

				if (result.Length > snippetLength)
				{
					result = result.Substring(0, snippetLength);
					result += "...";
				}
			}
			return result;
		}

		#endregion

		/// <summary>
		/// Creates a new comment record in the <c>comment</c> entity table.
		///
		/// Preserved verbatim from the monolith's <c>CommentService.Create()</c>
		/// (source lines 16-51). All parameter defaults, field assignments, JSON
		/// serialization, and error handling patterns are identical to the source.
		///
		/// <para><b>Default Values:</b></para>
		/// <list type="bullet">
		///   <item><paramref name="id"/>: <c>Guid.NewGuid()</c> if null</item>
		///   <item><paramref name="createdBy"/>: <c>SystemIds.SystemUserId</c> if null</item>
		///   <item><paramref name="createdOn"/>: <c>DateTime.UtcNow</c> if null</item>
		/// </list>
		///
		/// <para><b>JSON Serialization:</b> The <paramref name="scope"/> and
		/// <paramref name="relatedRecords"/> lists are serialized to JSON strings via
		/// <c>JsonConvert.SerializeObject()</c> before storage in the <c>l_scope</c>
		/// and <c>l_related_records</c> fields respectively.</para>
		/// </summary>
		/// <param name="id">Optional record ID. Defaults to <c>Guid.NewGuid()</c>.</param>
		/// <param name="createdBy">Optional creator user ID. Defaults to <c>SystemIds.SystemUserId</c>.</param>
		/// <param name="createdOn">Optional creation timestamp. Defaults to <c>DateTime.UtcNow</c>.</param>
		/// <param name="body">Comment body content. Defaults to empty string.</param>
		/// <param name="parentId">Optional parent comment ID for threaded/nested comments.</param>
		/// <param name="scope">List of scope identifiers. Serialized to JSON for <c>l_scope</c> field.</param>
		/// <param name="relatedRecords">List of related record GUIDs. Serialized to JSON for <c>l_related_records</c> field.</param>
		/// <exception cref="ValidationException">
		/// Thrown when <see cref="RecordManager.CreateRecord(string, EntityRecord)"/> returns
		/// a non-success response. The exception message contains the response's error message.
		/// </exception>
		public void Create(Guid? id = null, Guid? createdBy = null, DateTime? createdOn = null, string body = "", Guid? parentId = null,
			List<string> scope = null, List<Guid> relatedRecords = null)
		{
			#region << Init >>
			if (id == null)
				id = Guid.NewGuid();

			if (createdBy == null)
				createdBy = SystemIds.SystemUserId;

			if (createdOn == null)
				createdOn = DateTime.UtcNow;
			#endregion

			try
			{
				var record = new EntityRecord();
				record["id"] = id;
				record["created_by"] = createdBy;
				record["created_on"] = createdOn;
				record["body"] = body;
				record["parent_id"] = parentId;
				record["l_scope"] = JsonConvert.SerializeObject(scope);
				record["l_related_records"] = JsonConvert.SerializeObject(relatedRecords);

				var response = _recordManager.CreateRecord("comment", record);
				if (!response.Success)
				{
					throw new ValidationException(response.Message);
				}
			}
			catch (Exception)
			{
				throw;
			}
		}

		/// <summary>
		/// Deletes a comment and its child comments (one level of nesting).
		///
		/// Preserved verbatim from the monolith's <c>CommentService.Delete()</c>
		/// (source lines 53-101). Enforces author-only deletion: only the comment
		/// creator (matched via <c>created_by</c> field against
		/// <see cref="SecurityContext.CurrentUser.Id"/>) may delete the comment.
		///
		/// <para><b>Cascade Behavior:</b> Deletes all direct child comments
		/// (one level of nesting) before deleting the parent comment.</para>
		/// </summary>
		/// <param name="recordId">The GUID of the comment record to delete.</param>
		/// <exception cref="Exception">
		/// Thrown when: the comment is not found, the current user is not the author,
		/// or the <see cref="RecordManager.DeleteRecord"/> operation fails.
		/// </exception>
		public void Delete(Guid recordId)
		{
			//Validate - only authors can start to delete their posts and comments. Moderation will be later added if needed
			{
				var eqlCommand = "SELECT id,created_by FROM comment WHERE id = @commentId";
				var eqlParams = new List<EqlParameter>() { new EqlParameter("commentId", recordId) };
				var eqlResult = new EqlCommand(eqlCommand, eqlParams).Execute();
				if (!eqlResult.Any())
					throw new Exception("RecordId not found");
				if ((Guid)eqlResult[0]["created_by"] != SecurityContext.CurrentUser.Id)
					throw new Exception("Only the author can delete its comment");
			}

			var commentIdListForDeletion = new List<Guid>();
			//Add requested
			commentIdListForDeletion.Add(recordId);

			//Find and add all the child comments
			//TODO currently only on level if comment nesting is implemented. If it is increased this method should be changed
			{
				var eqlCommand = "SELECT id FROM comment WHERE parent_id = @commentId";
				var eqlParams = new List<EqlParameter>() { new EqlParameter("commentId", recordId) };
				var eqlResult = new EqlCommand(eqlCommand, eqlParams).Execute();
				foreach (var childComment in eqlResult)
				{
					commentIdListForDeletion.Add((Guid)childComment["id"]);
				}
			}

			//Create transaction 

			//Trigger delete
			foreach (var commentId in commentIdListForDeletion)
			{

				//Remove case relations
				//TODO


				var deleteResponse = _recordManager.DeleteRecord("comment", commentId);
				if (!deleteResponse.Success)
				{
					throw new Exception(deleteResponse.Message);
				}
			}



		}

		/// <summary>
		/// Pre-create hook logic for project comments — validates comment context,
		/// loads related task records, extracts project and watcher information,
		/// and creates an activity feed item for the comment event.
		///
		/// Preserved verbatim from the monolith's <c>CommentService.PreCreateApiHookLogic()</c>
		/// (source lines 103-177). In the monolith, this was called from
		/// <c>Hooks/Api/Comment.OnPreCreateRecord</c>. In the microservice, this is a
		/// domain service method callable from controllers or event publishers.
		///
		/// <para><b>EQL Queries:</b> Uses intra-service EQL queries to load task records
		/// with project and watcher relations. All queried entities (task, project) are
		/// owned by the Project service.</para>
		///
		/// <para><b>Activity Feed:</b> Creates a feed item via <see cref="FeedService.Create"/>
		/// with subject "commented on [task_key] task_subject" and body snippet extracted
		/// from the comment HTML body.</para>
		/// </summary>
		/// <param name="entityName">The entity name ("comment") triggering the hook.</param>
		/// <param name="record">The comment EntityRecord being created.</param>
		/// <param name="errors">Mutable list of ErrorModel for accumulating validation errors.</param>
		public virtual void PreCreateApiHookLogic(string entityName, EntityRecord record, List<ErrorModel> errors)
		{
			var isProjectComment = false;
			var relatedTaskRecords = new EntityRecordList();
			//Get timelog
			if (record.Properties.ContainsKey("l_scope") && record["l_scope"] != null && ((string)record["l_scope"]).Contains("projects"))
			{
				isProjectComment = true;
			}

			if (isProjectComment)
			{
				//Get related tasks from related records field
				if (record.Properties.ContainsKey("l_related_records") && record["l_related_records"] != null && (string)record["l_related_records"] != "")
				{
					try
					{
						var relatedRecordGuid = JsonConvert.DeserializeObject<List<Guid>>((string)record["l_related_records"]);
						var taskEqlCommand = "SELECT *,$project_nn_task.id, $user_nn_task_watchers.id FROM task WHERE ";
						var filterStringList = new List<string>();
						var taskEqlParams = new List<EqlParameter>();
						var index = 1;
						foreach (var taskGuid in relatedRecordGuid)
						{
							var paramName = "taskId" + index;
							filterStringList.Add($" id = @{paramName} ");
							taskEqlParams.Add(new EqlParameter(paramName, taskGuid));
							index++;
						}
						taskEqlCommand += String.Join(" OR ", filterStringList);
						relatedTaskRecords = new EqlCommand(taskEqlCommand, taskEqlParams).Execute();
					}
					catch (Exception)
					{
						throw;
					}
				}
				if (!relatedTaskRecords.Any())
					throw new Exception("Hook exception: This comment is a project comment but does not have an existing taskId related");

				var taskRecord = relatedTaskRecords[0]; //Currently should be related only to 1 task in projects

				//Get Project Id
				Guid? projectId = null;
				if (((List<EntityRecord>)taskRecord["$project_nn_task"]).Any())
				{
					var projectRecord = ((List<EntityRecord>)taskRecord["$project_nn_task"]).First();
					if (projectRecord != null)
					{
						projectId = (Guid)projectRecord["id"];
					}
				}

				var taskWatchersList = new List<string>();
				if (((List<EntityRecord>)taskRecord["$user_nn_task_watchers"]).Any())
				{
					taskWatchersList = ((List<EntityRecord>)taskRecord["$user_nn_task_watchers"]).Select(x => ((Guid)x["id"]).ToString()).ToList();
				}

				//Add activity log
				var subject = $"commented on <a href=\"/projects/tasks/tasks/r/{taskRecord["id"]}/details\">[{taskRecord["key"]}] {taskRecord["subject"]}</a>";
				var relatedRecords = new List<string>() { taskRecord["id"].ToString(), record["id"].ToString() };
				if (projectId != null)
				{
					relatedRecords.Add(projectId.ToString());
				}
				relatedRecords.AddRange(taskWatchersList);

				var body = GetSnippetFromHtml((string)record["body"]);
				var scope = new List<string>() { "projects" };
				_feedService.Create(id: Guid.NewGuid(), createdBy: SecurityContext.CurrentUser.Id, subject: subject,
					body: body, relatedRecords: relatedRecords, scope: scope, type: "comment");
			}

		}

		/// <summary>
		/// Post-create hook logic for project comments — auto-adds the comment creator
		/// as a watcher on related tasks if they are not already watching.
		///
		/// Preserved verbatim from the monolith's <c>CommentService.PostCreateApiHookLogic()</c>
		/// (source lines 179-225). In the monolith, this was called from
		/// <c>Hooks/Api/Comment.OnPostCreateRecord</c>. In the microservice, this is a
		/// domain service method callable from controllers or event publishers.
		///
		/// <para><b>Business Rule:</b> When a user comments on a project task, they are
		/// automatically added as a watcher on that task via the <c>user_nn_task_watchers</c>
		/// many-to-many relation. This ensures comment authors receive future notifications
		/// about task changes.</para>
		///
		/// <para><b>EQL Queries:</b> Uses intra-service EQL queries to load task records
		/// with watcher relations. All queried entities (task) are owned by the Project service.</para>
		/// </summary>
		/// <param name="entityName">The entity name ("comment") triggering the hook.</param>
		/// <param name="record">The comment EntityRecord that was created.</param>
		public virtual void PostCreateApiHookLogic(string entityName, EntityRecord record)
		{
			Guid? commentCreator = null;
			if (record.Properties.ContainsKey("created_by") && record["created_by"] != null && record["created_by"] is Guid)
			{
				commentCreator = (Guid)record["created_by"];
			}
			if (commentCreator == null)
				return;

			if (!record.Properties.ContainsKey("l_scope") || record["l_scope"] == null || !((string)record["l_scope"]).Contains("projects"))
				return;

			if (record.Properties.ContainsKey("l_related_records") && record["l_related_records"] != null && (string)record["l_related_records"] != "")
			{
				var relatedRecordGuid = JsonConvert.DeserializeObject<List<Guid>>((string)record["l_related_records"]);
				var taskEqlCommand = "SELECT *,$user_nn_task_watchers.id from task WHERE ";
				var filterStringList = new List<string>();
				var taskEqlParams = new List<EqlParameter>();
				var index = 1;
				foreach (var taskGuid in relatedRecordGuid)
				{
					var paramName = "taskId" + index;
					filterStringList.Add($" id = @{paramName} ");
					taskEqlParams.Add(new EqlParameter(paramName, taskGuid));
					index++;
				}
				taskEqlCommand += String.Join(" OR ", filterStringList);
				var relatedTaskRecords = new EqlCommand(taskEqlCommand, taskEqlParams).Execute();
				var watchRelation = _relationManager.Read("user_nn_task_watchers").Object;
				if (watchRelation == null)
					throw new Exception("Watch relation not found");

				foreach (var task in relatedTaskRecords)
				{
					if (task.Properties.ContainsKey("$user_nn_task_watchers") && task["$user_nn_task_watchers"] != null && task["$user_nn_task_watchers"] is List<EntityRecord>)
					{
						var watcherIdList = ((List<EntityRecord>)task["$user_nn_task_watchers"]).Select(x => (Guid)x["id"]).ToList();
						if (!watcherIdList.Contains(commentCreator.Value))
						{
							//Create relation
							var createRelResponse = _recordManager.CreateRelationManyToManyRecord(watchRelation.Id, commentCreator.Value, (Guid)task["id"]);
							if (!createRelResponse.Success)
								throw new Exception(createRelResponse.Message);
						}
					}
				}
			}
		}
	}
}
