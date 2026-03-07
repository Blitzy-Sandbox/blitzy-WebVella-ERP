using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Utilities;
using WebVella.Erp.Service.Core.Api;

namespace WebVella.Erp.Service.Core.Controllers
{
	#region << Typeahead Response Models >>

	/// <summary>
	/// Response envelope for typeahead/multiselect endpoints (e.g., RelatedFieldMultiSelect).
	/// Contains a list of result items and pagination metadata.
	/// Preserved from the monolith's WebVella.Erp.Web.Models.TypeaheadResponse
	/// with identical JSON property names for backward API compatibility.
	/// </summary>
	public class TypeaheadResponse
	{
		[JsonProperty(PropertyName = "results")]
		public List<TypeaheadResponseResult> Results { get; set; } = new List<TypeaheadResponseResult>();

		[JsonProperty(PropertyName = "pagination")]
		public TypeaheadResponsePagination Pagination { get; set; } = new TypeaheadResponsePagination();
	}

	/// <summary>
	/// A single result item in a typeahead response. Contains the unique identifier
	/// and display text for the matched record, plus optional visual metadata (icon, color)
	/// and context (entity name, field name) for rich rendering.
	/// Preserved from the monolith's TypeaheadResponseRow with identical JSON property names.
	/// </summary>
	public class TypeaheadResponseResult
	{
		[JsonProperty(PropertyName = "id")]
		public string Id { get; set; }

		[JsonProperty(PropertyName = "text")]
		public string Text { get; set; } = "";

		[JsonProperty(PropertyName = "iconName")]
		public string IconName { get; set; } = "database";

		[JsonProperty(PropertyName = "color")]
		public string Color { get; set; } = "teal";

		[JsonProperty(PropertyName = "entityName")]
		public string EntityName { get; set; } = "";

		[JsonProperty(PropertyName = "fieldName")]
		public string FieldName { get; set; } = "";
	}

	/// <summary>
	/// Pagination metadata for typeahead responses indicating whether additional
	/// pages of results are available beyond the current page.
	/// Preserved from the monolith's TypeaheadResponsePagination.
	/// </summary>
	public class TypeaheadResponsePagination
	{
		[JsonProperty(PropertyName = "more")]
		public bool More { get; set; } = false;
	}

	#endregion

	/// <summary>
	/// Core Platform Search/Typeahead REST API Controller.
	/// Exposes search and typeahead REST endpoints for the Core Platform microservice.
	///
	/// Extracted from the monolith's WebApiController.cs — specifically the quick-search,
	/// related-field-multiselect, and select-field-add-option endpoints.
	///
	/// Endpoints:
	/// 1. QuickSearch — GET api/v3/{locale}/quick-search
	///    Multi-entity search supporting EQ, CONTAINS, STARTSWITH, and FTS modes
	///    with force filters, sorting, pagination, and configurable find types.
	///
	/// 2. RelatedFieldMultiSelect — GET/POST api/v3.0/p/core/related-field-multiselect
	///    Typeahead query for entity field values used in multiselect UI components.
	///
	/// 3. SelectFieldAddOption — PUT api/v3.0/p/core/select-field-add-option
	///    Admin-only endpoint to add new options to SelectField/MultiSelectField types.
	///
	/// All endpoints use [Authorize] at class level. SelectFieldAddOption additionally
	/// restricts to the "administrator" role.
	/// </summary>
	[Authorize]
	[ApiController]
	public class SearchController : Controller
	{
		private readonly SearchManager _searchManager;
		private readonly EntityManager _entityManager;
		private readonly RecordManager _recordManager;
		private readonly EntityRelationManager _relationManager;
		private readonly IConfiguration _configuration;

		/// <summary>
		/// Constructs the SearchController with required service dependencies.
		/// Replaces monolith pattern of "new RecordManager()" etc. with explicit DI injection.
		/// </summary>
		/// <param name="searchManager">PostgreSQL FTS and ILIKE search service.</param>
		/// <param name="entityManager">Entity/field metadata CRUD and cache management.</param>
		/// <param name="recordManager">Record CRUD with event publishing and EQL execution.</param>
		/// <param name="relationManager">Entity relation CRUD and lookup service.</param>
		/// <param name="configuration">Application configuration for development mode checks.</param>
		public SearchController(
			SearchManager searchManager,
			EntityManager entityManager,
			RecordManager recordManager,
			EntityRelationManager relationManager,
			IConfiguration configuration)
		{
			_searchManager = searchManager ?? throw new ArgumentNullException(nameof(searchManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_relationManager = relationManager ?? throw new ArgumentNullException(nameof(relationManager));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		/// <summary>
		/// Helper property replacing the static ErpSettings.DevelopmentMode with injected configuration.
		/// Controls the verbosity of error messages in DoBadRequestResponse.
		/// </summary>
		private bool IsDevelopmentMode =>
			string.Equals(_configuration["Settings:DevelopmentMode"], "true", StringComparison.OrdinalIgnoreCase);

		#region << Response Helper Methods >>

		/// <summary>
		/// Standard response handler. If response contains errors or is not successful,
		/// sets HTTP status code from response model. Preserves original ApiControllerBase behavior.
		/// Source: ApiControllerBase.cs lines 16-30
		/// </summary>
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
		/// Bad request response handler. Sets failure state, applies error message
		/// with development-mode stack trace when enabled. Preserves original behavior.
		/// Source: ApiControllerBase.cs lines 44-62
		/// </summary>
		protected IActionResult DoBadRequestResponse(BaseResponseModel response, string message = null, Exception ex = null)
		{
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			if (IsDevelopmentMode)
			{
				if (ex != null)
					response.Message = ex.Message + ex.StackTrace;
			}
			else
			{
				if (string.IsNullOrEmpty(message))
					response.Message = "An internal error occurred!";
				else
					response.Message = message;
			}

			HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
			return Json(response);
		}

		/// <summary>
		/// Item not found response handler. Sets HTTP 404 status code and returns response JSON.
		/// Source: ApiControllerBase.cs lines 38-42
		/// </summary>
		protected IActionResult DoItemNotFoundResponse(BaseResponseModel response)
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(response);
		}

		/// <summary>
		/// Page not found response handler. Sets HTTP 404 and returns empty JSON object.
		/// Source: ApiControllerBase.cs lines 32-36
		/// </summary>
		protected IActionResult DoPageNotFoundResponse()
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(new { });
		}

		#endregion

		#region << Quick Search >>

		/// <summary>
		/// Multi-entity quick search endpoint supporting four search modes:
		/// EQ (exact equality), CONTAINS (ILIKE substring), STARTSWITH (ILIKE prefix),
		/// and FTS (PostgreSQL full-text search).
		///
		/// Supports force filters (fieldName:dataType:value CSV), sorting, pagination,
		/// and configurable find types (records, count, records-and-count).
		///
		/// Preserved exactly from monolith WebApiController.GetQuickSearch (lines 3020-3246).
		/// All business logic, filter parsing, and query composition are identical.
		///
		/// Route: GET api/v3/{locale}/quick-search
		/// </summary>
		/// <param name="locale">Locale identifier from route (e.g., "en_US").</param>
		/// <param name="query">Search query text. Required.</param>
		/// <param name="entityName">Target entity name. Required.</param>
		/// <param name="lookupFieldsCsv">Comma-separated list of fields to search in. Required.</param>
		/// <param name="sortField">Optional field name to sort results by.</param>
		/// <param name="sortType">Sort direction: "asc" (default) or "desc".</param>
		/// <param name="returnFieldsCsv">Comma-separated list of fields to return. Required.</param>
		/// <param name="matchMethod">Search mode: "EQ" (default), "contains", "startsWith", or "FTS".</param>
		/// <param name="matchAllFields">If true, all lookup fields must match (AND); if false, any match suffices (OR).</param>
		/// <param name="skipRecords">Number of records to skip for pagination. Default 0.</param>
		/// <param name="limitRecords">Maximum number of records to return. Default 5.</param>
		/// <param name="findType">Query mode: "records" (default), "count", "records-and-count", or "records&amp;count".</param>
		/// <param name="forceFiltersCsv">Additional equality filters in format "fieldName:dataType:value,..." where dataType is guid|bool|datetime|int|string.</param>
		/// <returns>ResponseModel containing search results and/or count.</returns>
		[HttpGet("/api/v3/{locale}/quick-search")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult QuickSearch(
			[FromQuery] string query = "",
			[FromQuery] string entityName = "",
			[FromQuery] string lookupFieldsCsv = "",
			[FromQuery] string sortField = "",
			[FromQuery] string sortType = "asc",
			[FromQuery] string returnFieldsCsv = "",
			[FromQuery] string matchMethod = "EQ",
			[FromQuery] bool matchAllFields = false,
			[FromQuery] int skipRecords = 0,
			[FromQuery] int limitRecords = 5,
			[FromQuery] string findType = "records",
			[FromQuery] string forceFiltersCsv = "")
		{
			// forceFiltersCsv -> should be in the format "fieldName1:dataType1:eqValue1,fieldName2:dataType2:eqValue2"
			var response = new ResponseModel();
			var responseObject = new EntityRecord();
			try
			{
				if (String.IsNullOrWhiteSpace(entityName) || String.IsNullOrWhiteSpace(lookupFieldsCsv)
					|| String.IsNullOrWhiteSpace(query) || String.IsNullOrWhiteSpace(returnFieldsCsv))
				{
					throw new Exception("missing params. All params are required");
				}

				var lookupFieldsList = new List<string>();
				foreach (var field in lookupFieldsCsv.Split(','))
				{
					lookupFieldsList.Add(field);
				}

				QueryObject matchesFilter = null;

				#region << Generate filters >>
				switch (matchMethod.ToLowerInvariant())
				{
					case "contains":
						if (lookupFieldsList.Count > 1)
						{
							var filterList = new List<QueryObject>();
							foreach (var field in lookupFieldsList)
							{
								filterList.Add(EntityQuery.QueryContains(field, query));
							}
							if (matchAllFields)
							{
								matchesFilter = EntityQuery.QueryAND(filterList.ToArray());
							}
							else
							{
								matchesFilter = EntityQuery.QueryOR(filterList.ToArray());
							}
						}
						else
						{
							matchesFilter = EntityQuery.QueryContains(lookupFieldsList[0], query);
						}
						break;

					case "startswith":
						if (lookupFieldsList.Count > 1)
						{
							var filterList = new List<QueryObject>();
							foreach (var field in lookupFieldsList)
							{
								filterList.Add(EntityQuery.QueryStartsWith(field, query));
							}
							if (matchAllFields)
							{
								matchesFilter = EntityQuery.QueryAND(filterList.ToArray());
							}
							else
							{
								matchesFilter = EntityQuery.QueryOR(filterList.ToArray());
							}
						}
						else
						{
							matchesFilter = EntityQuery.QueryStartsWith(lookupFieldsList[0], query);
						}
						break;

					case "fts":
						if (lookupFieldsList.Count > 1)
						{
							var filterList = new List<QueryObject>();
							foreach (var field in lookupFieldsList)
							{
								filterList.Add(EntityQuery.QueryFTS(field, query));
							}
							if (matchAllFields)
							{
								matchesFilter = EntityQuery.QueryAND(filterList.ToArray());
							}
							else
							{
								matchesFilter = EntityQuery.QueryOR(filterList.ToArray());
							}
						}
						else
						{
							matchesFilter = EntityQuery.QueryFTS(lookupFieldsList[0], query);
						}
						break;

					default: // EQ
						if (lookupFieldsList.Count > 1)
						{
							var filterList = new List<QueryObject>();
							foreach (var field in lookupFieldsList)
							{
								filterList.Add(EntityQuery.QueryEQ(field, query));
							}
							if (matchAllFields)
							{
								matchesFilter = EntityQuery.QueryAND(filterList.ToArray());
							}
							else
							{
								matchesFilter = EntityQuery.QueryOR(filterList.ToArray());
							}
						}
						else
						{
							matchesFilter = EntityQuery.QueryEQ(lookupFieldsList[0], query);
						}
						break;
				}
				#endregion

				#region << Generate force filters >>
				var forceFilters = new List<QueryObject>();
				if (!String.IsNullOrWhiteSpace(forceFiltersCsv))
				{
					foreach (var forceFilter in forceFiltersCsv.Split(','))
					{
						var filterArray = forceFilter.Split(':');
						if (filterArray.Length == 3)
						{
							switch (filterArray[1].ToLowerInvariant())
							{
								case "guid":
									var filterValueGuid = new Guid(filterArray[2]);
									forceFilters.Add(EntityQuery.QueryEQ(filterArray[0], filterValueGuid));
									break;
								case "bool":
									if (filterArray[2] == "true")
									{
										forceFilters.Add(EntityQuery.QueryEQ(filterArray[0], true));
									}
									else
									{
										forceFilters.Add(EntityQuery.QueryEQ(filterArray[0], false));
									}
									break;
								case "datetime":
									var filterValueDate = Convert.ToDateTime(filterArray[2]);
									forceFilters.Add(EntityQuery.QueryEQ(filterArray[0], filterValueDate));
									break;
								case "int":
									var filterValueInt = Convert.ToInt64(filterArray[2]);
									forceFilters.Add(EntityQuery.QueryEQ(filterArray[0], filterValueInt));
									break;
								case "string":
									forceFilters.Add(EntityQuery.QueryEQ(filterArray[0], filterArray[2]));
									break;
								default:
									break;
							}
						}
					}
				}

				if (forceFilters.Count > 0)
				{
					var forceFilterQuery = EntityQuery.QueryAND(forceFilters.ToArray());
					matchesFilter = EntityQuery.QueryAND(forceFilterQuery, matchesFilter);
				}
				#endregion

				var sortsList = new List<QuerySortObject>();
				#region << Generate Sorts >>
				if (!String.IsNullOrWhiteSpace(sortField))
				{
					if (sortType.ToLowerInvariant() == "desc")
					{
						sortsList.Add(new QuerySortObject(sortField, QuerySortType.Descending));
					}
					else
					{
						sortsList.Add(new QuerySortObject(sortField, QuerySortType.Ascending));
					}
				}
				#endregion

				if (findType.ToLowerInvariant() == "records"
					|| findType.ToLowerInvariant() == "records-and-count"
					|| findType.ToLowerInvariant() == "records&count")
				{
					var matchQueryResponse = _recordManager.Find(new EntityQuery(entityName, returnFieldsCsv,
						matchesFilter, sortsList.ToArray(), skipRecords, limitRecords));
					if (!matchQueryResponse.Success)
					{
						throw new Exception(matchQueryResponse.Message);
					}
					responseObject["records"] = matchQueryResponse.Object.Data;
				}

				if (findType.ToLowerInvariant() == "count"
					|| findType.ToLowerInvariant() == "records-and-count"
					|| findType.ToLowerInvariant() == "records&count")
				{
					var matchQueryResponse = _recordManager.Count(new EntityQuery(entityName, returnFieldsCsv, matchesFilter));
					if (!matchQueryResponse.Success)
					{
						throw new Exception(matchQueryResponse.Message);
					}
					responseObject["count"] = matchQueryResponse.Object;
				}

				response.Success = true;
				response.Message = "Quick search success";
				response.Object = responseObject;
				return Json(response);
			}
			catch (Exception ex)
			{
				response.Success = false;
				response.Message = ex.Message;
				response.Object = null;

				if (IsDevelopmentMode)
				{
					response.Message = ex.Message + ex.StackTrace;
				}

				return Json(response);
			}
		}

		#endregion

		#region << UI Component Support >>

		/// <summary>
		/// Related field multiselect typeahead endpoint for UI component support.
		/// Returns paginated field values from the specified entity, filtered by search term,
		/// formatted as a typeahead response with {id, text} items and pagination metadata.
		///
		/// Supports both GET and POST methods for flexibility in client integration.
		/// Preserved from monolith WebApiController.RelatedFieldMultiSelect (lines 1135-1214).
		///
		/// Route: GET/POST api/v3.0/p/core/related-field-multiselect
		/// </summary>
		/// <param name="entityName">Name of the entity to search in. Required.</param>
		/// <param name="fieldName">Name of the field to search and return values for. Required.</param>
		/// <param name="search">Search term to filter field values by (CONTAINS match). Optional.</param>
		/// <param name="page">Page number for pagination (1-based). Default 1.</param>
		/// <param name="pageSize">Number of results per page. Default 5.</param>
		/// <returns>TypeaheadResponse with results and pagination metadata.</returns>
		[Produces("application/json")]
		[Route("api/v3.0/p/core/related-field-multiselect")]
		[AcceptVerbs("GET", "POST")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult RelatedFieldMultiSelect(
			[FromQuery] string entityName = "",
			[FromQuery] string fieldName = "",
			[FromQuery] string search = "",
			[FromQuery] int? page = 1,
			[FromQuery] int? pageSize = 5)
		{
			try
			{
				var response = new TypeaheadResponse();
				var errorResponse = new ResponseModel();

				var currentPage = page ?? 1;
				var currentPageSize = pageSize ?? 5;

				if (String.IsNullOrWhiteSpace(entityName))
				{
					errorResponse.Message = "entity name is required";
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					return Json(errorResponse);
				}
				if (String.IsNullOrWhiteSpace(fieldName))
				{
					errorResponse.Message = "field name is required";
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					return Json(errorResponse);
				}

				// Fetch one extra record beyond page size to determine if more records exist
				var fetchLimit = currentPageSize + 1;
				var skipPages = (currentPage - 1) * currentPageSize;
				var sortList = new List<QuerySortObject>();
				sortList.Add(new QuerySortObject(fieldName, QuerySortType.Ascending));

				var entityQuery = new EntityQuery(entityName, fieldName, null, sortList.ToArray(), skipPages, fetchLimit);
				if (!String.IsNullOrWhiteSpace(search))
				{
					entityQuery = new EntityQuery(entityName, fieldName, EntityQuery.QueryContains(fieldName, search),
						sortList.ToArray(), skipPages, fetchLimit);
				}

				var findResult = _recordManager.Find(entityQuery);
				var resultRecords = new List<EntityRecord>();
				if (!findResult.Success)
				{
					errorResponse.Message = findResult.Message;
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					return Json(errorResponse);
				}

				if (findResult.Object.Data.Count > 0)
				{
					if (findResult.Object.Data.Count > currentPageSize)
					{
						response.Pagination.More = true;
						resultRecords = findResult.Object.Data.Take(currentPageSize).ToList();
					}
					else
					{
						resultRecords = findResult.Object.Data;
					}

					var entity = _entityManager.ReadEntity(entityName).Object;
					foreach (var record in resultRecords)
					{
						response.Results.Add(new TypeaheadResponseResult
						{
							Id = record[fieldName]?.ToString() ?? "",
							Text = record[fieldName]?.ToString() ?? "",
							FieldName = fieldName,
							EntityName = entity != null ? entity.Label : entityName,
							Color = entity != null ? entity.Color : "teal",
							IconName = entity != null ? entity.IconName : "database"
						});
					}
				}
				return new JsonResult(response);
			}
			catch (Exception ex)
			{
				var errorResp = new ResponseModel();
				return DoBadRequestResponse(errorResp, ex.Message, ex);
			}
		}

		/// <summary>
		/// Adds a new option to a SelectField or MultiSelectField on an entity.
		/// Admin-only endpoint (requires "administrator" role).
		///
		/// Accepts a JSON body with entityName, fieldName, and value properties.
		/// Validates the field type, checks for duplicate options (case-insensitive),
		/// and persists the new option via EntityManager.UpdateField().
		///
		/// Preserved from monolith WebApiController.SelectFieldAddOption (lines 1216-1336).
		///
		/// Route: PUT api/v3.0/p/core/select-field-add-option
		/// </summary>
		/// <param name="submitObj">JSON object containing: entityName (string), fieldName (string), value (string).</param>
		/// <returns>ResponseModel indicating success or failure.</returns>
		[Produces("application/json")]
		[Route("api/v3.0/p/core/select-field-add-option")]
		[AcceptVerbs("PUT")]
		[Authorize(Roles = "administrator")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult SelectFieldAddOption([FromBody] JObject submitObj)
		{
			var response = new ResponseModel();
			var entityName = "";
			var fieldName = "";
			var optionValue = "";
			try
			{
				#region << Init SubmitObj >>
				foreach (var prop in submitObj.Properties())
				{
					switch (prop.Name.ToLower())
					{
						case "entityname":
							if (!string.IsNullOrWhiteSpace(prop.Value.ToString()))
								entityName = prop.Value.ToString();
							else
							{
								throw new Exception("EntityName is required");
							}
							break;
						case "fieldname":
							if (!string.IsNullOrWhiteSpace(prop.Value.ToString()))
								fieldName = prop.Value.ToString();
							else
							{
								throw new Exception("Field name is required");
							}
							break;
						case "value":
							if (!string.IsNullOrWhiteSpace(prop.Value.ToString()))
								optionValue = prop.Value.ToString();
							else
							{
								throw new Exception("Option value is required");
							}
							break;
					}
				}
				#endregion

				var entityMeta = _entityManager.ReadEntity(entityName).Object;
				if (entityMeta == null)
				{
					throw new Exception("Entity not found by the provided entityName: " + entityName);
				}

				var fieldMeta = entityMeta.Fields.FirstOrDefault(x => x.Name == fieldName);
				if (fieldMeta == null)
				{
					throw new Exception("Field not found by the provided fieldName: " + fieldName + " in entity " + entityName);
				}

				// Validate that the field is a SelectField or MultiSelectField
				if (fieldMeta.GetFieldType() != FieldType.SelectField &&
					fieldMeta.GetFieldType() != FieldType.MultiSelectField)
				{
					throw new Exception("Field '" + fieldName + "' is not a SelectField or MultiSelectField. Cannot add option to field type: " + fieldMeta.GetFieldType());
				}

				var optionExists = false;
				if (fieldMeta.GetFieldType() == FieldType.SelectField)
				{
					var fieldOptions = ((SelectField)fieldMeta).Options
						.FirstOrDefault(x => x.Value.ToLowerInvariant() == optionValue.ToLowerInvariant());
					if (fieldOptions != null)
					{
						optionExists = true;
					}
				}
				else if (fieldMeta.GetFieldType() == FieldType.MultiSelectField)
				{
					var fieldOptions = ((MultiSelectField)fieldMeta).Options
						.FirstOrDefault(x => x.Value.ToLowerInvariant() == optionValue.ToLowerInvariant());
					if (fieldOptions != null)
					{
						optionExists = true;
					}
				}

				if (optionExists)
				{
					throw new Exception("Record not found!");
				}

				if (fieldMeta.GetFieldType() == FieldType.SelectField)
				{
					var newOption = new SelectOption
					{
						Value = optionValue,
						Label = optionValue
					};
					var newFieldMeta = (SelectField)fieldMeta;
					newFieldMeta.Options.Add(newOption);
					var updateResponse = _entityManager.UpdateField(entityMeta, newFieldMeta.MapTo<InputField>());
					if (!updateResponse.Success)
					{
						throw new Exception(updateResponse.Message);
					}
				}
				else if (fieldMeta.GetFieldType() == FieldType.MultiSelectField)
				{
					var newOption = new SelectOption
					{
						Value = optionValue,
						Label = optionValue
					};
					var newFieldMeta = (MultiSelectField)fieldMeta;
					newFieldMeta.Options.Add(newOption);
					var updateResponse = _entityManager.UpdateField(entityMeta, newFieldMeta.MapTo<InputField>());
					if (!updateResponse.Success)
					{
						throw new Exception(updateResponse.Message);
					}
				}

				response.Success = true;
				response.Message = "Record created successfully";
			}
			catch (Exception ex)
			{
				response.Success = false;
				response.Message = ex.Message;

				if (IsDevelopmentMode)
				{
					response.Message = ex.Message + ex.StackTrace;
				}
			}
			return new JsonResult(response);
		}

		#endregion
	}
}
