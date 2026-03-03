using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Project.Domain.Services
{
	/// <summary>
	/// Contract for cross-service CRM account validation calls.
	/// Replaces the monolith's direct EQL query against the CRM-owned 'account' entity,
	/// which now resides in the CRM service boundary (AAP Section 0.7.1,
	/// Entity-to-Service Ownership Matrix).
	///
	/// Implementations should use either gRPC or REST HTTP calls to the CRM service
	/// to validate account existence.
	///
	/// When the CRM service publishes AccountCreated/AccountDeleted domain events,
	/// this interface can be replaced with a local event-sourced projection that
	/// eliminates the synchronous cross-service call entirely.
	/// </summary>
	public interface ICrmServiceClient
	{
		/// <summary>
		/// Validates whether an account with the specified ID exists in the CRM service.
		/// </summary>
		/// <param name="accountId">The unique identifier of the account to validate.</param>
		/// <returns>True if the account exists in the CRM service; otherwise, false.</returns>
		Task<bool> AccountExistsAsync(Guid accountId);
	}

	/// <summary>
	/// Monthly timelog report aggregation service that produces per-task billable
	/// and non-billable minute summaries for a given month, optionally filtered
	/// by CRM account.
	///
	/// Extracted from the monolith's <c>WebVella.Erp.Plugins.Project.Services.ReportService</c>
	/// (137 lines). All business logic is preserved verbatim from the source, with
	/// only the following microservice-specific adaptations:
	///
	/// <list type="number">
	///   <item>Class renamed from <c>ReportService</c> to <c>ReportingService</c></item>
	///   <item>Namespace changed to <c>WebVella.Erp.Service.Project.Domain.Services</c></item>
	///   <item>Import statements updated to reference SharedKernel namespaces</item>
	///   <item>Constructor injection added for <see cref="ILogger{T}"/> and <see cref="ICrmServiceClient"/></item>
	///   <item>Account validation (original lines 23-30) replaced with cross-service CRM call</item>
	///   <item>Method signature updated to <c>async Task</c> for cross-service call support</item>
	/// </list>
	///
	/// All EQL queries for intra-service entities (timelog, task) are preserved
	/// as-is since both entities are owned by the Project service.
	///
	/// <para>
	/// <b>Cross-Service Data Note:</b> The <c>$project_nn_task.account_id</c> field
	/// in the task EQL query is a cross-service denormalized field that must be kept
	/// in sync via CRM domain events (AccountUpdated/AccountDeleted). This field
	/// is stored locally in the Project service database and populated via CRM event
	/// subscribers listening for account lifecycle changes.
	/// </para>
	/// </summary>
	public class ReportingService
	{
		private readonly ILogger<ReportingService> _logger;
		private readonly ICrmServiceClient _crmServiceClient;

		/// <summary>
		/// Initializes a new instance of the <see cref="ReportingService"/> class.
		/// </summary>
		/// <param name="logger">Structured logger for distributed tracing and observability.</param>
		/// <param name="crmServiceClient">
		/// Optional cross-service client for CRM account validation.
		/// When null, account validation is skipped with a warning log.
		/// In production deployments, this should always be provided via DI registration.
		/// </param>
		public ReportingService(ILogger<ReportingService> logger, ICrmServiceClient crmServiceClient = null)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_crmServiceClient = crmServiceClient;
		}

		/// <summary>
		/// Retrieves aggregated timelog data for a specified month and year,
		/// optionally filtered by CRM account ID.
		///
		/// Returns a list of <see cref="EntityRecord"/> instances, each containing:
		/// <list type="bullet">
		///   <item><c>task_id</c> (Guid) — The task identifier</item>
		///   <item><c>project_id</c> (Guid) — The project identifier</item>
		///   <item><c>task_subject</c> (string) — Task subject/name</item>
		///   <item><c>project_name</c> (string) — Project name</item>
		///   <item><c>task_type</c> (string) — Task type label</item>
		///   <item><c>billable_minutes</c> (decimal) — Sum of billable timelog minutes for the task</item>
		///   <item><c>non_billable_minutes</c> (decimal) — Sum of non-billable timelog minutes for the task</item>
		/// </list>
		///
		/// <para><b>Business rules preserved from monolith:</b></para>
		/// <list type="bullet">
		///   <item>Month must be 1–12 (inclusive)</item>
		///   <item>Year must be greater than 0</item>
		///   <item>If accountId is specified, it must exist in the CRM service</item>
		///   <item>Tasks without timelog records in the specified period are excluded</item>
		///   <item>Tasks without project associations are excluded</item>
		///   <item>If a task belongs to multiple projects, it appears once per project</item>
		///   <item>When accountId is specified, projects without account_id throw an exception</item>
		///   <item>When accountId is specified, only projects matching that account are included</item>
		/// </list>
		/// </summary>
		/// <param name="year">The year for which to retrieve timelog data. Must be greater than 0.</param>
		/// <param name="month">The month for which to retrieve timelog data. Must be between 1 and 12.</param>
		/// <param name="accountId">Optional CRM account ID to filter results by. When provided, only
		/// tasks associated with projects belonging to this account are included.</param>
		/// <returns>A list of <see cref="EntityRecord"/> instances containing aggregated timelog report data.</returns>
		/// <exception cref="ValidationException">Thrown when month, year, or accountId validation fails.</exception>
		/// <exception cref="Exception">Thrown when a project has no account_id and account filtering is active.</exception>
		public async Task<List<EntityRecord>> GetTimelogData(int year, int month, Guid? accountId)
		{
			_logger.LogDebug("GetTimelogData invoked: year={Year}, month={Month}, accountId={AccountId}",
				year, month, accountId);

			// --- Input validation (preserved verbatim from source lines 15-21) ---
			ValidationException valEx = new ValidationException();

			if (month > 12 || month <= 0)
				valEx.AddError("month", "Invalid month.");

			if (year <= 0)
				valEx.AddError("year", "Invalid year.");

			// --- Account existence validation (source lines 23-30) ---
			// ORIGINAL monolith code (replaced — account entity belongs to CRM service):
			//   List<EqlParameter> eqlParams;
			//   if (accountId.HasValue)
			//   {
			//       eqlParams = new List<EqlParameter>() { new EqlParameter("id", accountId.Value) };
			//       var eqlResult = new EqlCommand("SELECT * FROM account WHERE id = @id ", eqlParams).Execute();
			//       if (!eqlResult.Any())
			//           valEx.AddError("accountId", $"Account with ID:{accountId} not found.");
			//   }
			//
			// NEW: Cross-service CRM call via ICrmServiceClient (AAP Section 0.7.1).
			if (accountId.HasValue)
			{
				if (_crmServiceClient != null)
				{
					bool accountExists = await _crmServiceClient.AccountExistsAsync(accountId.Value);
					if (!accountExists)
						valEx.AddError("accountId", $"Account with ID:{accountId} not found.");
				}
				else
				{
					_logger.LogWarning(
						"ICrmServiceClient not configured; skipping account existence validation for accountId={AccountId}. " +
						"This should only occur during testing or service bootstrap.",
						accountId);
				}
			}

			valEx.CheckAndThrow();

			// --- Load timelog records from database (source lines 34-46) ---
			// Intra-service query: timelog entity is owned by the Project service.
			DateTime fromDate = new DateTime(year, month, 1);
			DateTime toDate = new DateTime(year, month, DateTime.DaysInMonth(year, month));
			List<EqlParameter> eqlParams = new List<EqlParameter>() {
				new EqlParameter("from_date", fromDate),
				new EqlParameter("to_date", toDate),
				new EqlParameter("scope", "projects" ),
			};
			string eql = @"SELECT id,is_billable,l_related_records,minutes FROM timelog 
						   WHERE logged_on >= @from_date AND 
							  logged_on <= @to_date AND
							  l_scope CONTAINS @scope";
			var timelogRecords = new EqlCommand(eql, eqlParams).Execute();

			// --- Extract task IDs from timelog l_related_records JSON (source lines 48-56) ---
			HashSet<Guid> setOfTasksWithTimelog = new HashSet<Guid>();
			foreach (var timelog in timelogRecords)
			{
				List<Guid> ids = JsonConvert.DeserializeObject<List<Guid>>((string)timelog["l_related_records"]);
				Guid taskId = ids[0];
				timelog["task_id"] = taskId;
				if (!setOfTasksWithTimelog.Contains(taskId))
					setOfTasksWithTimelog.Add(taskId);
			}

			// --- Load all tasks with project and task_type relations (source lines 58-61) ---
			// Intra-service query: task entity is owned by the Project service.
			// Note: $project_nn_task.account_id is a cross-service denormalized field
			// that is kept in sync via CRM domain events (AccountUpdated/AccountDeleted).
			eqlParams = new List<EqlParameter>();
			eql = @"SELECT id,subject, $project_nn_task.id, $project_nn_task.name,$project_nn_task.account_id, $task_type_1n_task.label FROM task";
			var tasks = new EqlCommand(eql, eqlParams).Execute();

			// --- Process tasks: split for projects, filter by account, filter by timelog (source lines 63-98) ---
			EntityRecordList processedTasks = new EntityRecordList();
			foreach (var task in tasks)
			{
				// Skip task that has no timelog record in the specified period
				if (!setOfTasksWithTimelog.Contains((Guid)task["id"]))
					continue;

				List<EntityRecord> taskProjects = (List<EntityRecord>)task["$project_nn_task"];
				// Skip tasks with no project association
				if (taskProjects.Count == 0)
					continue;

				// Split tasks to projects if more than one project is related to task
				foreach (var project in taskProjects)
				{
					if (accountId != null)
					{
						if (project["account_id"] == null)
						{
							throw new Exception("There is a project without an account");
						}

						if ((Guid)project["account_id"] == accountId)
						{
							task["project"] = project;
							processedTasks.Add(task);
						}
					}
					else
					{
						task["project"] = project;
						processedTasks.Add(task);
					}
				}
			}
			tasks = processedTasks;
			tasks.TotalCount = processedTasks.Count;

			// --- Build result: aggregate timelog minutes per task (source lines 103-132) ---
			List<EntityRecord> result = new List<EntityRecord>();
			foreach (var task in tasks)
			{
				EntityRecord rec = new EntityRecord();
				rec["task_id"] = task["id"];
				rec["project_id"] = ((EntityRecord)task["project"])["id"];
				rec["task_subject"] = task["subject"];
				rec["project_name"] = ((EntityRecord)task["project"])["name"];
				rec["task_type"] = ((List<EntityRecord>)task["$task_type_1n_task"])[0]["label"];
				rec["billable_minutes"] = (decimal)0;
				rec["non_billable_minutes"] = (decimal)0;

				foreach (var timelog in timelogRecords)
				{
					if ((Guid)timelog["task_id"] != (Guid)task["id"])
						continue;

					if ((bool)timelog["is_billable"])
						rec["billable_minutes"] = (decimal)rec["billable_minutes"] + (decimal)timelog["minutes"];
					else
						rec["non_billable_minutes"] = (decimal)rec["non_billable_minutes"] + (decimal)timelog["minutes"];
				}

				result.Add(rec);
			}

			_logger.LogDebug("GetTimelogData completed: {ResultCount} records returned for year={Year}, month={Month}",
				result.Count, year, month);

			return result;
		}
	}
}
