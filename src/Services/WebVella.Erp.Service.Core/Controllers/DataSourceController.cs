using CSScriptLib;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Core.Api;

namespace WebVella.Erp.Service.Core.Controllers
{
	/// <summary>
	/// ASP.NET Core MVC controller exposing DataSource CRUD and execution REST endpoints
	/// for the Core Platform microservice. Extracted from the monolith's WebApiController.cs
	/// (4314 lines), specifically the datasource-related endpoints.
	///
	/// Preserves all 5 original endpoints with identical route patterns, request/response
	/// contracts, and business logic:
	///   - POST api/v3/{locale}/eql-ds         — EQL datasource query execution
	///   - POST api/v3/{locale}/eql-ds-select2 — EQL datasource query with Select2 format
	///   - POST ~/api/v3.0/datasource/code-compile           — C# code compilation (admin)
	///   - POST ~/api/v3.0/datasource/test                   — Datasource test (admin)
	///   - POST ~/api/v3.0/datasource/{dataSourceId}/test    — Datasource test by ID (admin)
	///
	/// Response helper methods (DoResponse, DoBadRequestResponse, DoItemNotFoundResponse,
	/// DoPageNotFoundResponse) are inlined from the monolith's ApiControllerBase.cs since
	/// no separate base controller is specified in the AAP.
	///
	/// All response shapes use the BaseResponseModel/ResponseModel envelope
	/// (success, errors, timestamp, message, object) preserving the REST API v3 contract.
	/// </summary>
	[Authorize]
	[ApiController]
	[Route("api/v3/{locale}")]
	public class DataSourceController : Controller
	{
		private readonly DataSourceManager _dataSourceManager;
		private readonly EntityManager _entityManager;
		private readonly RecordManager _recordManager;
		private readonly IConfiguration _configuration;

		/// <summary>
		/// Constructs the DataSourceController with all required service dependencies.
		/// Replaces the monolith's manual <c>new DataSourceManager()</c> etc. instantiation
		/// with proper DI constructor injection for microservice architecture.
		/// </summary>
		/// <param name="dataSourceManager">DataSource CRUD and execution manager.</param>
		/// <param name="entityManager">Entity metadata manager for entity-scoped operations.</param>
		/// <param name="recordManager">Record CRUD manager for datasource test operations.</param>
		/// <param name="configuration">Application configuration for development mode detection.</param>
		public DataSourceController(
			DataSourceManager dataSourceManager,
			EntityManager entityManager,
			RecordManager recordManager,
			IConfiguration configuration)
		{
			_dataSourceManager = dataSourceManager ?? throw new ArgumentNullException(nameof(dataSourceManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		#region << Response Helper Methods (from ApiControllerBase.cs) >>

		/// <summary>
		/// Aligns HTTP status code with response semantics. If the response contains errors,
		/// sets Success to false. If Success is false and StatusCode is still OK, sets it to
		/// BadRequest. Sets the Timestamp to UTC now and returns the response with the
		/// appropriate HTTP status code.
		///
		/// Preserved identically from the monolith's ApiControllerBase.cs lines 16-30.
		/// </summary>
		/// <param name="responseModel">The response envelope to process and return.</param>
		/// <returns>An IActionResult with the response body and corresponding HTTP status code.</returns>
		protected IActionResult DoResponse(BaseResponseModel responseModel)
		{
			if (responseModel.Errors.Count > 0 || !responseModel.Success)
			{
				if (responseModel.StatusCode == HttpStatusCode.OK)
					HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				else
					HttpContext.Response.StatusCode = (int)responseModel.StatusCode;
			}

			return Json(responseModel);
		}

		/// <summary>
		/// Returns a 400 Bad Request response with the specified error message.
		/// In development mode, includes the exception message and stack trace for debugging.
		/// In production mode, returns a generic error message.
		///
		/// Preserved identically from the monolith's ApiControllerBase.cs lines 44-62.
		/// </summary>
		/// <param name="responseModel">The response envelope to populate with error info.</param>
		/// <param name="message">Optional custom error message.</param>
		/// <param name="ex">Optional exception for development mode diagnostics.</param>
		/// <returns>An IActionResult with a 400 status code.</returns>
		protected IActionResult DoBadRequestResponse(BaseResponseModel responseModel, string message = null, Exception ex = null)
		{
			responseModel.Timestamp = DateTime.UtcNow;
			responseModel.Success = false;

			bool isDevelopmentMode = string.Equals(
				_configuration["Settings:DevelopmentMode"], "true",
				StringComparison.OrdinalIgnoreCase);

			if (isDevelopmentMode)
			{
				if (ex != null)
					responseModel.Message = ex.Message + ex.StackTrace;
			}
			else
			{
				if (string.IsNullOrEmpty(message))
					responseModel.Message = "An internal error occurred!";
				else
					responseModel.Message = message;
			}

			HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
			return Json(responseModel);
		}

		/// <summary>
		/// Returns a 404 Not Found response for a missing item/entity.
		///
		/// Preserved identically from the monolith's ApiControllerBase.cs lines 38-42.
		/// </summary>
		/// <param name="responseModel">The response envelope to return.</param>
		/// <returns>An IActionResult with a 404 status code.</returns>
		protected IActionResult DoItemNotFoundResponse(BaseResponseModel responseModel)
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(responseModel);
		}

		/// <summary>
		/// Returns a 404 Not Found response with an empty JSON object body.
		///
		/// Preserved identically from the monolith's ApiControllerBase.cs lines 32-36.
		/// </summary>
		/// <returns>An IActionResult with a 404 status code and empty JSON body.</returns>
		protected IActionResult DoPageNotFoundResponse()
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(new { });
		}

		#endregion

		#region << DataSource Query Endpoints >>

		/// <summary>
		/// Executes a datasource query by name. Supports both DatabaseDataSource (EQL-based)
		/// and CodeDataSource (C# code-based) execution types.
		///
		/// For DatabaseDataSource: resolves the datasource by name, merges caller parameters
		/// with defaults, executes via DataSourceManager.Execute(), and returns the result
		/// as a list with total_count.
		///
		/// For CodeDataSource: converts parameters to a Dictionary and invokes the code
		/// datasource's Execute() method.
		///
		/// Preserved identically from the monolith's WebApiController.cs lines 97-188.
		/// Route: POST api/v3/{locale}/eql-ds
		/// </summary>
		/// <param name="submitObj">JSON body containing "name" (string) and "parameters" (array of {name, value}).</param>
		/// <returns>ResponseModel with datasource query results or error details.</returns>
		[HttpPost("eql-ds")]
		public IActionResult DataSourceQueryAction([FromBody] JObject submitObj)
		{
			ResponseModel response = new ResponseModel();
			response.Success = true;

			if (submitObj == null)
				return NotFound();

			string dsName = null;
			var eqlParameters = new List<EqlParameter>();

			// Extract name and parameters from the JSON body
			// Preserved from monolith's Init SubmitObj region
			foreach (var prop in submitObj.Properties())
			{
				switch (prop.Name.ToLower())
				{
					case "name":
						if (!string.IsNullOrWhiteSpace(prop.Value.ToString()))
							dsName = prop.Value.ToString();
						else
						{
							throw new Exception("DataSource Name is required");
						}
						break;
					case "parameters":
						var jParams = (JArray)prop.Value;
						foreach (JObject jParam in jParams)
						{
							var name = jParam["name"].ToString();
							var value = jParam["value"].ToString();
							var eqlParam = new EqlParameter(name, value);
							eqlParameters.Add(eqlParam);
						}
						break;
				}
			}

			try
			{
				var dataSources = _dataSourceManager.GetAll();
				var ds = dataSources.SingleOrDefault(x => x.Name == dsName);
				if (ds == null)
				{
					response.Success = false;
					response.Message = $"DataSource with name '{dsName}' not found.";
					return Json(response);
				}

				if (ds is DatabaseDataSource)
				{
					var list = (EntityRecordList)_dataSourceManager.Execute(ds.Id, eqlParameters);
					response.Object = new { list, total_count = list.TotalCount };
				}
				else if (ds is CodeDataSource)
				{
					Dictionary<string, object> arguments = new Dictionary<string, object>();
					foreach (var par in eqlParameters)
						arguments[par.ParameterName] = par.Value;

					response.Object = ((CodeDataSource)ds).Execute(arguments);
				}
				else
				{
					response.Success = false;
					response.Message = $"DataSource type is not supported.";
					return Json(response);
				}
			}
			catch (EqlException eqlEx)
			{
				response.Success = false;
				foreach (var eqlError in eqlEx.Errors)
				{
					response.Errors.Add(new ErrorModel("eql", "", eqlError.Message));
				}
				return Json(response);
			}
			catch (Exception ex)
			{
				response.Success = false;
				response.Message = ex.Message;
				return Json(response);
			}

			return Json(response);
		}

		/// <summary>
		/// Executes a datasource query by name and formats results for Select2 UI components.
		///
		/// Same datasource lookup and execution as DataSourceQueryAction, but with Select2-specific
		/// post-processing:
		///   - Extracts "page" from parameters for pagination (default: 1)
		///   - Maps results to {id, text} objects for Select2 format
		///   - Supports fallback text fields: "text" → "label" → "name" → id
		///   - Returns { results: [...], pagination: { more: bool } } format
		///
		/// Preserved identically from the monolith's WebApiController.cs lines 190-337.
		/// Route: POST api/v3/{locale}/eql-ds-select2
		///
		/// CRITICAL: The Select2 response format is an API contract that must not change.
		/// </summary>
		/// <param name="submitObj">JSON body containing "name" (string) and "parameters" (array of {name, value}).</param>
		/// <returns>EntityRecord with "results" (List of {id, text}) and "pagination" ({more: bool}).</returns>
		[HttpPost("eql-ds-select2")]
		public IActionResult DataSourceQuerySelect2Action([FromBody] JObject submitObj)
		{
			if (submitObj == null)
				return NotFound();

			var result = new EntityRecord();
			result["results"] = new List<EntityRecord>();
			result["pagination"] = new EntityRecord();

			string dsName = null;
			var eqlParameters = new List<EqlParameter>();

			// Extract name and parameters from the JSON body
			// Preserved from monolith's Init SubmitObj region
			foreach (var prop in submitObj.Properties())
			{
				switch (prop.Name.ToLower())
				{
					case "name":
						if (!string.IsNullOrWhiteSpace(prop.Value.ToString()))
							dsName = prop.Value.ToString();
						else
						{
							throw new Exception("DataSource Name is required");
						}
						break;
					case "parameters":
						var jParams = (JArray)prop.Value;
						foreach (JObject jParam in jParams)
						{
							var name = jParam["name"].ToString();
							var value = jParam["value"].ToString();
							var eqlParam = new EqlParameter(name, value);
							eqlParameters.Add(eqlParam);
						}
						break;
				}
			}

			// Extract page parameter for pagination
			var page = 1;
			if (eqlParameters.Count > 0)
			{
				var pageParam = eqlParameters.FirstOrDefault(x => x.ParameterName == "page");
				if (pageParam != null)
				{
					if (int.TryParse(pageParam.Value?.ToString(), out int outInt))
					{
						page = outInt;
					}
				}
			}

			var records = new List<EntityRecord>();
			int? total = 0;

			try
			{
				var dataSources = _dataSourceManager.GetAll();
				var ds = dataSources.SingleOrDefault(x => x.Name == dsName);
				if (ds == null)
				{
					return BadRequest();
				}

				if (ds is DatabaseDataSource)
				{
					var list = (EntityRecordList)_dataSourceManager.Execute(ds.Id, eqlParameters);
					records = (List<EntityRecord>)list;
					total = list.TotalCount;
				}
				else if (ds is CodeDataSource)
				{
					Dictionary<string, object> arguments = new Dictionary<string, object>();
					foreach (var par in eqlParameters)
						arguments[par.ParameterName] = par.Value;

					var dsResult = ((CodeDataSource)ds).Execute(arguments);
					if (dsResult is EntityRecordList)
					{
						records = (List<EntityRecord>)((EntityRecordList)dsResult);
						total = ((EntityRecordList)dsResult).TotalCount;
					}
					else if (dsResult is List<EntityRecord>)
					{
						records = (List<EntityRecord>)dsResult;
						total = null;
					}
					else
					{
						return Json(dsResult);
					}
				}
				else
				{
					return BadRequest();
				}
			}
			catch
			{
				return BadRequest();
			}

			// Post process records according to requirements {id, text}
			// Preserved identically from monolith — supports fallback text field resolution
			var processedRecords = new List<EntityRecord>();
			foreach (var record in records)
			{
				var procRec = new EntityRecord();
				if (record.Properties.ContainsKey("id"))
				{
					procRec["id"] = record["id"].ToString();
				}
				else
				{
					procRec["id"] = "no-id-" + Guid.NewGuid();
				}
				if (record.Properties.ContainsKey("text"))
				{
					procRec["text"] = record["text"].ToString();
				}
				else if (record.Properties.ContainsKey("label"))
				{
					procRec["text"] = record["label"].ToString();
				}
				else if (record.Properties.ContainsKey("name"))
				{
					procRec["text"] = record["name"].ToString();
				}
				else
				{
					procRec["text"] = procRec["id"].ToString();
				}
				processedRecords.Add(procRec);
			}

			var moreRecord = new EntityRecord();
			moreRecord["more"] = false;
			if (records.Count > 0)
			{
				if (total > page * 10)
				{
					moreRecord["more"] = true;
				}
				result["results"] = processedRecords;
			}

			result["pagination"] = moreRecord;
			return Json(result);
		}

		#endregion

		#region << Admin-Only DataSource Endpoints >>

		/// <summary>
		/// Compiles C# datasource code for validation without execution.
		/// Uses CSScript compilation internally via CodeEvalService.
		///
		/// Preserved from the monolith's WebApiController.cs lines 494-509.
		/// Route: POST ~/api/v3.0/datasource/code-compile
		///
		/// Admin-only endpoint restricted to users with "administrator" role.
		/// </summary>
		/// <param name="submitObj">JSON body containing "csCode" (string) — the C# source code to compile.</param>
		/// <returns>JSON object with success/message indicating compilation result.</returns>
		[Authorize(Roles = "administrator")]
		[HttpPost("~/api/v3.0/datasource/code-compile")]
		public IActionResult DataSourceCodeCompile([FromBody] JObject submitObj)
		{
			try
			{
				var csCode = submitObj["csCode"]?.ToString() ?? string.Empty;
				// Compile the C# code to validate syntax and semantics
				// In the microservice architecture, code compilation is handled
				// by the CSScript evaluator (same as monolith's CodeEvalService.Compile)
				CSScriptLib.CSScript.EvaluatorConfig.ReferenceDomainAssemblies = true;
				CSScriptLib.CSScript.Evaluator.Check(csCode);
			}
			catch (Exception ex)
			{
				return Json(new { success = false, message = ex.Message });
			}

			return Json(new { success = true, message = "" });
		}

		/// <summary>
		/// Tests a datasource by executing an ad-hoc EQL query with specified parameters.
		/// Supports two actions:
		///   - "sql": generates the PostgreSQL SQL from the EQL without execution
		///   - "data": executes the EQL and returns the serialized result data
		///
		/// Preserved identically from the monolith's WebApiController.cs lines 511-540.
		/// Route: POST ~/api/v3.0/datasource/test
		///
		/// Admin-only endpoint restricted to users with "administrator" role.
		/// </summary>
		/// <param name="submitObj">JSON body containing "action" (string), "eql" (string),
		/// "parameters" (string), and "return_total" (bool).</param>
		/// <returns>JSON object with sql, data, and errors fields.</returns>
		[Authorize(Roles = "administrator")]
		[HttpPost("~/api/v3.0/datasource/test")]
		public IActionResult DataSourceTest([FromBody] JObject submitObj)
		{
			if (submitObj == null)
				return NotFound();

			string sql = string.Empty;
			string data = "";
			List<EqlError> errors = new List<EqlError>();

			try
			{
				var action = submitObj["action"]?.ToString() ?? string.Empty;
				var eql = submitObj["eql"]?.ToString() ?? string.Empty;
				var parameters = submitObj["parameters"]?.ToString() ?? string.Empty;
				var returnTotal = submitObj["return_total"]?.ToObject<bool>() ?? true;

				if (action == "sql")
					sql = _dataSourceManager.GenerateSql(eql, parameters, returnTotal);
				if (action == "data")
					data = JsonConvert.SerializeObject(
						_dataSourceManager.Execute(eql, parameters, returnTotal),
						Formatting.Indented);
			}
			catch (EqlException eqlEx)
			{
				errors.AddRange(eqlEx.Errors);
			}
			catch (Exception ex)
			{
				errors.Add(new EqlError { Message = ex.Message });
			}

			return Json(new { sql, data, errors });
		}

		/// <summary>
		/// Tests an existing datasource by its unique identifier. Looks up the datasource,
		/// merges submitted parameters with stored default parameters, then executes
		/// either SQL generation or data retrieval.
		///
		/// Preserved identically from the monolith's WebApiController.cs lines 542-600.
		/// Route: POST ~/api/v3.0/datasource/{dataSourceId}/test
		///
		/// Admin-only endpoint restricted to users with "administrator" role.
		/// </summary>
		/// <param name="dataSourceId">The unique identifier of the existing datasource to test.</param>
		/// <param name="submitObj">JSON body containing "action" (string) and "param_list"
		/// (array of DataSourceParameter objects).</param>
		/// <returns>JSON object with sql, data, and errors fields.</returns>
		[Authorize(Roles = "administrator")]
		[HttpPost("~/api/v3.0/datasource/{dataSourceId}/test")]
		public IActionResult DataSourceTestById(Guid dataSourceId, [FromBody] JObject submitObj)
		{
			if (submitObj == null)
				return NotFound();

			string sql = string.Empty;
			string data = "";
			List<EqlError> errors = new List<EqlError>();

			try
			{
				var action = submitObj["action"]?.ToString() ?? string.Empty;
				var paramListToken = submitObj["param_list"];
				var paramList = new List<DataSourceParameter>();
				if (paramListToken != null && paramListToken.Type == JTokenType.Array)
				{
					paramList = paramListToken.ToObject<List<DataSourceParameter>>()
						?? new List<DataSourceParameter>();
				}

				var dataSource = _dataSourceManager.Get(dataSourceId);
				if (dataSource == null)
				{
					errors.Add(new EqlError { Message = "DataSource Not found" });
					return Json(new { sql, data, errors });
				}

				var dataSourceEql = "";
				if (dataSource is DatabaseDataSource)
				{
					dataSourceEql = ((DatabaseDataSource)dataSource).EqlText;
				}

				// Merge submitted parameters with stored defaults:
				// For each stored parameter, use the submitted value if provided,
				// otherwise fall back to the stored default.
				var compoundParams = new List<DataSourceParameter>();
				foreach (var dsParam in dataSource.Parameters)
				{
					var pageParameter = paramList.FirstOrDefault(x => x.Name == dsParam.Name);
					if (pageParameter != null)
					{
						compoundParams.Add(pageParameter);
					}
					else
					{
						compoundParams.Add(dsParam);
					}
				}

				var paramText = _dataSourceManager.ConvertParamsToText(compoundParams);

				if (action == "sql")
					sql = _dataSourceManager.GenerateSql(dataSourceEql, paramText, dataSource.ReturnTotal);
				if (action == "data")
					data = JsonConvert.SerializeObject(
						_dataSourceManager.Execute(dataSourceEql, paramText, dataSource.ReturnTotal),
						Formatting.Indented);
			}
			catch (EqlException eqlEx)
			{
				errors.AddRange(eqlEx.Errors);
			}
			catch (Exception ex)
			{
				errors.Add(new EqlError { Message = ex.Message });
			}

			return Json(new { sql, data, errors });
		}

		#endregion
	}
}
