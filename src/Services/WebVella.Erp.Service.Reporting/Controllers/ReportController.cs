using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WebVella.Erp.Service.Reporting.Domain.Services;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Reporting.Controllers
{
	/// <summary>
	/// ASP.NET Core MVC controller exposing report execution and management REST
	/// endpoints for the Reporting microservice. This is a brand-new controller that
	/// encapsulates the reporting REST surface extracted from the monolith, where report
	/// data was consumed in-process by the UI component <c>PcReportAccountMonthlyTimelogs</c>
	/// calling <c>ReportService.GetTimelogData()</c>.
	///
	/// <para>In the microservice architecture the Gateway/BFF proxies requests from
	/// the existing <c>/projects/reports/</c> UI routes to this service's REST API.</para>
	///
	/// <para><b>Route pattern:</b> <c>api/v3.0/p/reporting</c> — follows the monolith
	/// convention of <c>api/v3.0/p/{service}/...</c> (e.g. <c>api/v3.0/p/project/*</c>
	/// in ProjectController, <c>api/v3.0/p/sdk/*</c> in AdminController).</para>
	///
	/// <para><b>Response envelope:</b> All endpoints return <see cref="ResponseModel"/>
	/// (inherits <see cref="BaseResponseModel"/>) preserving the monolith REST API v3
	/// JSON contract: <c>success</c>, <c>errors</c>, <c>timestamp</c>, <c>message</c>,
	/// <c>object</c>.</para>
	///
	/// <para><b>Authentication:</b> <c>[Authorize]</c> at class level enforces JWT Bearer
	/// authentication on all endpoints per AAP 0.8.2.</para>
	/// </summary>
	[Authorize]
	[ApiController]
	[Route("api/v3.0/p/reporting")]
	public class ReportController : Controller
	{
		/// <summary>
		/// Well-known identifier for the Monthly Timelog Report definition.
		/// Used by the <see cref="GetReportResults"/> endpoint to route requests
		/// to the correct report executor.
		/// </summary>
		private static readonly Guid MonthlyTimelogReportId =
			new Guid("a0d5e2f1-b3c4-4d6e-8f7a-9b0c1d2e3f4a");

		private readonly ReportAggregationService _reportAggregationService;
		private readonly ILogger<ReportController> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="ReportController"/> class.
		/// </summary>
		/// <param name="reportAggregationService">
		/// Primary domain service providing report generation and timelog aggregation
		/// business logic. Registered as scoped in <c>Program.cs</c>.
		/// </param>
		/// <param name="logger">
		/// Structured logger for controller-level error logging with correlation
		/// parameters (year, month, accountId).
		/// </param>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="reportAggregationService"/> or
		/// <paramref name="logger"/> is <c>null</c>.
		/// </exception>
		public ReportController(
			ReportAggregationService reportAggregationService,
			ILogger<ReportController> logger)
		{
			_reportAggregationService = reportAggregationService
				?? throw new ArgumentNullException(nameof(reportAggregationService));
			_logger = logger
				?? throw new ArgumentNullException(nameof(logger));
		}

		#region << Response Helper Methods (from ApiControllerBase.cs) >>

		/// <summary>
		/// Standard response handler preserving monolith <c>ApiControllerBase.DoResponse</c>
		/// behavior (source: <c>ApiControllerBase.cs</c> lines 16–30).
		///
		/// If the response contains errors or <c>Success</c> is <c>false</c>:
		///   - When <c>StatusCode</c> is <c>OK</c>, forces HTTP 400 (Bad Request).
		///   - Otherwise uses the response's own <c>StatusCode</c>.
		/// </summary>
		/// <param name="response">The API response envelope to serialize.</param>
		/// <returns>A <see cref="JsonResult"/> wrapping the response model.</returns>
		protected IActionResult DoResponse(BaseResponseModel response)
		{
			if (response.Errors.Count > 0 || !response.Success)
			{
				if (response.StatusCode == HttpStatusCode.OK)
					HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				else
					HttpContext.Response.StatusCode = (int)response.StatusCode;
			}

			return Json(response);
		}

		/// <summary>
		/// Returns an HTTP 404 Not Found response with an empty JSON body.
		/// Preserves monolith <c>ApiControllerBase.DoPageNotFoundResponse</c> behavior
		/// (source: <c>ApiControllerBase.cs</c> lines 32–36).
		/// </summary>
		/// <returns>A <see cref="JsonResult"/> with an empty object.</returns>
		protected IActionResult DoPageNotFoundResponse()
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(new { });
		}

		/// <summary>
		/// Returns an HTTP 404 Not Found response with the given response envelope.
		/// Preserves monolith <c>ApiControllerBase.DoItemNotFoundResponse</c> behavior
		/// (source: <c>ApiControllerBase.cs</c> lines 38–42).
		/// </summary>
		/// <param name="response">The API response envelope to serialize.</param>
		/// <returns>A <see cref="JsonResult"/> wrapping the response model.</returns>
		protected IActionResult DoItemNotFoundResponse(BaseResponseModel response)
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(response);
		}

		/// <summary>
		/// Returns an HTTP 400 Bad Request response with optional error details.
		/// Preserves monolith <c>ApiControllerBase.DoBadRequestResponse</c> behavior
		/// (source: <c>ApiControllerBase.cs</c> lines 44–63):
		///   - In development mode (<see cref="ErpSettings.DevelopmentMode"/> = true):
		///     detailed exception message + stack trace.
		///   - In production mode: generic "An internal error occurred!" message.
		/// </summary>
		/// <param name="response">The API response envelope to populate and serialize.</param>
		/// <param name="message">Optional custom error message.</param>
		/// <param name="ex">Optional exception for detailed diagnostics in dev mode.</param>
		/// <returns>A <see cref="JsonResult"/> wrapping the response model.</returns>
		protected IActionResult DoBadRequestResponse(BaseResponseModel response, string message = null, Exception ex = null)
		{
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			if (ErpSettings.DevelopmentMode)
			{
				if (ex != null)
					response.Message = ex.Message + ex.StackTrace;
			}
			else
			{
				if (string.IsNullOrEmpty(message))
					response.Message = "An internal error occurred!";
			}

			HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
			return Json(response);
		}

		#endregion

		#region << Report Endpoints >>

		/// <summary>
		/// Retrieves monthly timelog report data aggregated by task and project for a
		/// given year and month, optionally filtered by account.
		///
		/// <para><b>Source context:</b> In the monolith, the
		/// <c>PcReportAccountMonthlyTimelogs</c> UI component called
		/// <c>ReportService.GetTimelogData(year, month, accountId)</c> in-process.
		/// This endpoint exposes the same data as a REST API.</para>
		///
		/// <para><b>Response object shape:</b> A <c>List&lt;EntityRecord&gt;</c> where
		/// each record contains: <c>task_id</c>, <c>project_id</c>, <c>task_subject</c>,
		/// <c>project_name</c>, <c>task_type</c>, <c>billable_minutes</c> (decimal),
		/// <c>non_billable_minutes</c> (decimal).</para>
		/// </summary>
		/// <param name="year">The calendar year for the report (must be &gt; 0).</param>
		/// <param name="month">The calendar month for the report (must be 1–12).</param>
		/// <param name="accountId">
		/// Optional account filter. When provided, only tasks linked to projects owned by
		/// this account are included.
		/// </param>
		/// <returns>
		/// A <see cref="ResponseModel"/> envelope with the aggregated timelog data in
		/// the <c>object</c> property.
		/// </returns>
		[HttpGet("timelog/monthly")]
		public IActionResult GetMonthlyTimelogReport(
			[FromQuery] int year,
			[FromQuery] int month,
			[FromQuery] Guid? accountId = null)
		{
			var response = new ResponseModel();
			try
			{
				List<EntityRecord> result = _reportAggregationService.GetTimelogData(year, month, accountId);
				response.Object = result;
				response.Success = true;
				response.Timestamp = DateTime.UtcNow;
				return DoResponse(response);
			}
			catch (ValidationException valEx)
			{
				// Map structured validation errors (month/year/accountId) to the
				// BaseResponseModel.Errors list preserving the monolith response envelope.
				// ValidationError has PropertyName (mapped to ErrorModel.Key) and Message.
				response.Errors = valEx.Errors
					.Select(e => new ErrorModel(e.PropertyName, "", e.Message))
					.ToList();
				response.Success = false;
				response.Timestamp = DateTime.UtcNow;
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				_logger.LogError(
					ex,
					"Error generating monthly timelog report for year={Year}, month={Month}, accountId={AccountId}",
					year, month, accountId);
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		/// <summary>
		/// Retrieves the list of available report definitions supported by the
		/// Reporting service. Currently returns the Monthly Timelog Report as the
		/// single available report type. Designed for future extensibility when
		/// additional report types are added.
		/// </summary>
		/// <returns>
		/// A <see cref="ResponseModel"/> envelope containing a list of report
		/// definition records in the <c>object</c> property.
		/// </returns>
		[HttpGet("definitions")]
		public IActionResult GetReportDefinitions()
		{
			var response = new ResponseModel();
			try
			{
				// Build the report definitions list with the currently supported report type.
				var definitions = new List<EntityRecord>();

				var monthlyTimelogDef = new EntityRecord();
				monthlyTimelogDef["id"] = MonthlyTimelogReportId;
				monthlyTimelogDef["name"] = "Monthly Timelog Report";
				monthlyTimelogDef["description"] = "Aggregated timelog data by task and project for a given month, optionally filtered by account.";
				monthlyTimelogDef["code"] = "timelog_monthly";
				monthlyTimelogDef["parameters"] = new List<EntityRecord>
				{
					CreateParameterRecord("year", "int", true, "Calendar year for the report (must be > 0)"),
					CreateParameterRecord("month", "int", true, "Calendar month for the report (1-12)"),
					CreateParameterRecord("accountId", "Guid?", false, "Optional account filter")
				};
				definitions.Add(monthlyTimelogDef);

				response.Object = definitions;
				response.Success = true;
				response.Timestamp = DateTime.UtcNow;
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error retrieving report definitions");
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		/// <summary>
		/// Retrieves report results by report definition identifier with dynamic
		/// parameters. Routes the request to the appropriate report executor based
		/// on the <paramref name="reportId"/>.
		///
		/// <para>For the Monthly Timelog Report (ID:
		/// <see cref="MonthlyTimelogReportId"/>), delegates to
		/// <see cref="ReportAggregationService.GetTimelogData(int, int, Guid?)"/>.</para>
		/// </summary>
		/// <param name="reportId">
		/// The unique identifier of the report definition to execute.
		/// </param>
		/// <param name="year">Optional calendar year parameter (required for timelog report).</param>
		/// <param name="month">Optional calendar month parameter (required for timelog report).</param>
		/// <param name="accountId">Optional account filter parameter.</param>
		/// <returns>
		/// A <see cref="ResponseModel"/> envelope containing the report results in
		/// the <c>object</c> property, or a 404 response for unknown report identifiers.
		/// </returns>
		[HttpGet("results/{reportId}")]
		public IActionResult GetReportResults(
			Guid reportId,
			[FromQuery] int? year = null,
			[FromQuery] int? month = null,
			[FromQuery] Guid? accountId = null)
		{
			var response = new ResponseModel();
			try
			{
				// Route to the appropriate report executor based on reportId.
				if (reportId == MonthlyTimelogReportId)
				{
					// Validate required parameters for the monthly timelog report.
					if (!year.HasValue || !month.HasValue)
					{
						response.Errors.Add(new ErrorModel(
							"parameters",
							"",
							"The 'year' and 'month' query parameters are required for the Monthly Timelog Report."));
						response.Success = false;
						response.Timestamp = DateTime.UtcNow;
						return DoResponse(response);
					}

					List<EntityRecord> result = _reportAggregationService.GetTimelogData(
						year.Value, month.Value, accountId);
					response.Object = result;
					response.Success = true;
					response.Timestamp = DateTime.UtcNow;
					return DoResponse(response);
				}

				// Unknown report definition — return 404.
				response.Success = false;
				response.Timestamp = DateTime.UtcNow;
				response.Message = $"Report definition with ID:{reportId} not found.";
				return DoItemNotFoundResponse(response);
			}
			catch (ValidationException valEx)
			{
				response.Errors = valEx.Errors
					.Select(e => new ErrorModel(e.PropertyName, "", e.Message))
					.ToList();
				response.Success = false;
				response.Timestamp = DateTime.UtcNow;
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				_logger.LogError(
					ex,
					"Error executing report {ReportId} for year={Year}, month={Month}, accountId={AccountId}",
					reportId, year, month, accountId);
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		#endregion

		#region << Private Helpers >>

		/// <summary>
		/// Creates a parameter definition record for report definitions metadata.
		/// </summary>
		/// <param name="name">Parameter name.</param>
		/// <param name="type">Parameter data type string.</param>
		/// <param name="required">Whether the parameter is required.</param>
		/// <param name="description">Human-readable parameter description.</param>
		/// <returns>An <see cref="EntityRecord"/> describing the parameter.</returns>
		private static EntityRecord CreateParameterRecord(string name, string type, bool required, string description)
		{
			var param = new EntityRecord();
			param["name"] = name;
			param["type"] = type;
			param["required"] = required;
			param["description"] = description;
			return param;
		}

		#endregion
	}
}
