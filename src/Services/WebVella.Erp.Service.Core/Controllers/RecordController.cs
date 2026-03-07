using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MassTransit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Utilities;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Controllers
{
	/// <summary>
	/// Core Platform Record CRUD and Query REST API Controller.
	/// Extracted from the monolith's WebApiController.cs — the record operations
	/// section spanning EQL query (lines 63-95) and record CRUD/import (lines 2102-3018).
	///
	/// Exposes endpoints for:
	/// - EQL query execution with 600-second timeout (AAP 0.8.3)
	/// - Entity relation record management (attach/detach for 1:1, 1:N, N:N)
	/// - Record CRUD (Create, Read, Update, Patch, Delete)
	/// - CSV record import and evaluation
	///
	/// All mutation endpoints publish domain events via MassTransit after
	/// successful transaction commit for eventual consistency across microservices.
	/// </summary>
	[Authorize]
	[ApiController]
	[Route("api/v3/{locale}")]
	public class RecordController : Controller
	{
		/// <summary>
		/// Separator character between relation name parts when traversing relation paths.
		/// Preserved from monolith WebApiController.cs line 39.
		/// </summary>
		public const char RELATION_SEPARATOR = '.';

		/// <summary>
		/// Prefix character used to identify relation references in field selectors.
		/// Single '$' = origin-target direction, '$$' = target-origin direction.
		/// Preserved from monolith WebApiController.cs line 40.
		/// </summary>
		public const char RELATION_NAME_RESULT_SEPARATOR = '$';

		private readonly RecordManager _recordManager;
		private readonly EntityManager _entityManager;
		private readonly EntityRelationManager _relationManager;
		private readonly ImportExportManager _importExportManager;
		private readonly IPublishEndpoint _publishEndpoint;

		/// <summary>
		/// Constructs a RecordController with all required service dependencies injected via DI.
		/// Replaces monolith pattern of <c>new RecordManager()</c> etc. in WebApiController constructor.
		/// </summary>
		/// <param name="recordManager">Core record CRUD manager for all record mutation operations.</param>
		/// <param name="entityManager">Entity metadata manager for resolving entity definitions and fields.</param>
		/// <param name="relationManager">Entity relation metadata manager for relation type lookups.</param>
		/// <param name="importExportManager">CSV import/export manager for import and evaluation endpoints.</param>
		/// <param name="publishEndpoint">MassTransit publish endpoint for domain event publishing.</param>
		public RecordController(
			RecordManager recordManager,
			EntityManager entityManager,
			EntityRelationManager relationManager,
			ImportExportManager importExportManager,
			IPublishEndpoint publishEndpoint)
		{
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_relationManager = relationManager ?? throw new ArgumentNullException(nameof(relationManager));
			_importExportManager = importExportManager ?? throw new ArgumentNullException(nameof(importExportManager));
			_publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
		}

		#region << Response Helpers — from ApiControllerBase.cs >>

		/// <summary>
		/// List of field names that must never be returned in API responses.
		/// Password hashes are stripped to prevent credential leakage (Issue 7).
		/// </summary>
		private static readonly HashSet<string> SensitiveFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"password"
		};

		/// <summary>
		/// Strips sensitive fields (e.g., password hashes) from all EntityRecord objects
		/// within a response model before returning to the client.
		/// Handles ResponseModel (single object), QueryResponse (data list), and nested structures.
		/// </summary>
		private void StripSensitiveFields(BaseResponseModel response)
		{
			if (response is QueryResponse queryResponse)
			{
				// QueryResponse.Object is QueryResult with Data list
				if (queryResponse.Object?.Data != null)
				{
					foreach (var record in queryResponse.Object.Data)
					{
						StripSensitiveFieldsFromRecord(record);
					}
				}
			}
			else if (response is ResponseModel responseModel)
			{
				// ResponseModel.Object could be EntityRecordList (which IS List<EntityRecord>) or EntityRecord
				if (responseModel.Object is EntityRecordList recordList)
				{
					foreach (var record in recordList)
					{
						StripSensitiveFieldsFromRecord(record);
					}
				}
				else if (responseModel.Object is EntityRecord singleRecord)
				{
					StripSensitiveFieldsFromRecord(singleRecord);
				}
				else if (responseModel.Object is List<EntityRecord> recordListPlain)
				{
					foreach (var record in recordListPlain)
					{
						StripSensitiveFieldsFromRecord(record);
					}
				}
			}
		}

		/// <summary>
		/// Removes all sensitive field entries from a single EntityRecord dictionary.
		/// </summary>
		private void StripSensitiveFieldsFromRecord(EntityRecord record)
		{
			if (record == null) return;
			foreach (var fieldName in SensitiveFieldNames)
			{
				record.Properties.Remove(fieldName);
			}
		}

		/// <summary>
		/// Standard response helper. Sets HTTP status code based on response model state.
		/// Strips sensitive fields (password hashes) before serialization.
		/// If errors exist or Success is false, returns 400 BadRequest (unless StatusCode is overridden).
		/// Preserved from monolith ApiControllerBase.cs lines 16-30.
		/// </summary>
		protected IActionResult DoResponse(BaseResponseModel response)
		{
			// Ensure timestamp is always set to current UTC time
			if (response.Timestamp == default(DateTime))
				response.Timestamp = DateTime.UtcNow;

			// Strip sensitive fields (e.g., password hashes) from all record data
			StripSensitiveFields(response);

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
		/// Returns a 404 Not Found response with empty JSON body.
		/// Preserved from monolith ApiControllerBase.cs lines 32-36.
		/// </summary>
		protected IActionResult DoPageNotFoundResponse()
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(new { });
		}

		/// <summary>
		/// Returns a 404 Not Found response with the provided response model.
		/// Preserved from monolith ApiControllerBase.cs lines 38-42.
		/// </summary>
		protected IActionResult DoItemNotFoundResponse(BaseResponseModel response)
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(response);
		}

		/// <summary>
		/// Returns a 400 Bad Request response with error details.
		/// Sets Success to false and populates Message from exception or default.
		/// Preserved from monolith ApiControllerBase.cs lines 44-62.
		/// </summary>
		protected IActionResult DoBadRequestResponse(BaseResponseModel response, string message = null, Exception ex = null)
		{
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			if (ex != null)
			{
				response.Message = ex.Message;
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

		#endregion

		#region << EQL Query >>

		/// <summary>
		/// Executes an EQL (Entity Query Language) query against core platform entities.
		/// Accepts a JSON body with "eql" (query string) and optional "parameters" array.
		/// Timeout is 600 seconds as required by AAP 0.8.3.
		///
		/// Preserved from monolith WebApiController.cs lines 63-95.
		/// Adapted to accept JObject instead of EqlQuery model (model not in service dependencies).
		/// </summary>
		/// <param name="locale">Locale from route (e.g., "en_US").</param>
		/// <param name="submitObj">JSON body containing "eql" string and optional "parameters" array.</param>
		/// <returns>ResponseModel with EntityRecordList results or error details.</returns>
		[HttpPost("eql")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult EqlQueryAction([FromBody] JObject submitObj)
		{
			var response = new ResponseModel();
			response.Success = true;

			if (submitObj == null)
				return NotFound();

			try
			{
				// Extract EQL query text
				string eqlText = null;
				List<EqlParameter> parameters = null;

				if (submitObj.ContainsKey("eql"))
					eqlText = submitObj["eql"]?.ToString();

				// Parse parameters from JArray if present
				if (submitObj.ContainsKey("parameters") && submitObj["parameters"] is JArray jParams)
				{
					parameters = new List<EqlParameter>();
					foreach (JObject jParam in jParams)
					{
						var name = jParam["name"]?.ToString();
						var value = jParam["value"]?.ToString();
						if (!string.IsNullOrWhiteSpace(name))
						{
							var eqlParam = new EqlParameter(name, value);
							parameters.Add(eqlParam);
						}
					}
				}

				if (string.IsNullOrWhiteSpace(eqlText))
				{
					response.Success = false;
					response.Message = "EQL query text is required.";
					return Json(response);
				}

				// Create and execute EQL command — 600-second timeout is built into EqlCommand.Execute()
				var eqlCommand = new EqlCommand(eqlText, parameters);
				var eqlResult = eqlCommand.Execute();
				response.Object = eqlResult;
			}
			catch (EqlException eqlEx)
			{
				response.Success = false;
				foreach (var eqlError in eqlEx.Errors)
				{
					response.Errors.Add(new ErrorModel("eql", "", eqlError.Message));
				}
				response.Timestamp = DateTime.UtcNow;
				return Json(response);
			}
			catch (Exception ex)
			{
				response.Success = false;
				response.Message = ex.Message;
				response.Timestamp = DateTime.UtcNow;
				return Json(response);
			}

			// Strip sensitive fields (e.g., password hashes) from EQL results
			StripSensitiveFields(response);
			response.Timestamp = DateTime.UtcNow;
			return Json(response);
		}

		#endregion

		#region << Record Relation Management >>

		/// <summary>
		/// Updates entity relation records for an origin record — attach/detach target records.
		/// Handles 1:1, 1:N, and N:N relation types with different code paths:
		/// - 1:1/1:N: Updates FK field on target records via RecordManager.UpdateRecord
		/// - N:N: Uses RecordManager.CreateRelationManyToManyRecord / RemoveRelationManyToManyRecord
		///
		/// Preserved from monolith WebApiController.cs lines 2102-2300 with full transactional semantics.
		/// </summary>
		[AcceptVerbs(new[] { "POST" }, Route = "/api/v3/{locale}/record/relation")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult UpdateEntityRelationRecord(
			[FromBody] InputEntityRelationRecordUpdateModel model)
		{
			BaseResponseModel response = new BaseResponseModel
			{
				Timestamp = DateTime.UtcNow,
				Success = true,
				Errors = new List<ErrorModel>()
			};

			if (model == null)
			{
				response.Errors.Add(new ErrorModel { Message = "Invalid model." });
				response.Success = false;
				return DoResponse(response);
			}

			EntityRelation relation = null;
			if (string.IsNullOrWhiteSpace(model.RelationName))
			{
				response.Errors.Add(new ErrorModel { Message = "Invalid relation name.", Key = "relationName" });
				response.Success = false;
				return DoResponse(response);
			}
			else
			{
				relation = _relationManager.Read(model.RelationName).Object;
				if (relation == null)
				{
					response.Errors.Add(new ErrorModel { Message = "Invalid relation name. No relation with that name.", Key = "relationName" });
					response.Success = false;
					return DoResponse(response);
				}
			}

			var originEntityResponse = _entityManager.ReadEntity(relation.OriginEntityId);
			var targetEntityResponse = _entityManager.ReadEntity(relation.TargetEntityId);
			var originEntity = originEntityResponse?.Object;
			var targetEntity = targetEntityResponse?.Object;

			if (originEntity == null || targetEntity == null)
			{
				response.Errors.Add(new ErrorModel { Message = "Origin or target entity not found for relation.", Key = "relationName" });
				response.Success = false;
				return DoResponse(response);
			}

			var originField = originEntity.Fields.SingleOrDefault(x => x.Id == relation.OriginFieldId);
			var targetField = targetEntity.Fields.SingleOrDefault(x => x.Id == relation.TargetFieldId);

			if (originField == null || targetField == null)
			{
				response.Errors.Add(new ErrorModel { Message = "Origin or target field not found for relation.", Key = "relationName" });
				response.Success = false;
				return DoResponse(response);
			}

			// Defensive null-coalescing: ensure lists are never null even if client omits them
			model.AttachTargetFieldRecordIds ??= new List<Guid>();
			model.DetachTargetFieldRecordIds ??= new List<Guid>();

			if (model.DetachTargetFieldRecordIds != null && model.DetachTargetFieldRecordIds.Any()
				&& targetField.Required && relation.RelationType != EntityRelationType.ManyToMany)
			{
				response.Errors.Add(new ErrorModel { Message = "Cannot detach records, when target field is required.", Key = "originFieldRecordId" });
				response.Success = false;
				return DoResponse(response);
			}

			EntityQuery query = new EntityQuery(originEntity.Name, "id," + originField.Name,
				EntityQuery.QueryEQ("id", model.OriginFieldRecordId), null, null, null);
			QueryResponse result = _recordManager.Find(query);
			if ((result.Object?.Data?.Count ?? 0) == 0)
			{
				response.Errors.Add(new ErrorModel
				{
					Message = "Origin record was not found. Id=[" + model.OriginFieldRecordId + "]",
					Key = "originFieldRecordId"
				});
				response.Success = false;
				return DoResponse(response);
			}

			var originRecord = result.Object.Data[0];
			object originValue = originRecord[originField.Name];

			var attachTargetRecords = new List<EntityRecord>();
			var detachTargetRecords = new List<EntityRecord>();

			foreach (var targetId in model.AttachTargetFieldRecordIds)
			{
				query = new EntityQuery(targetEntity.Name, "id," + targetField.Name,
					EntityQuery.QueryEQ("id", targetId), null, null, null);
				result = _recordManager.Find(query);
				if ((result.Object?.Data?.Count ?? 0) == 0)
				{
					response.Errors.Add(new ErrorModel
					{
						Message = "Attach target record was not found. Id=[" + targetEntity.Id + "]",
						Key = "targetRecordId"
					});
					response.Success = false;
					return DoResponse(response);
				}
				else if (attachTargetRecords.Any(x => (Guid)x["id"] == targetId))
				{
					response.Errors.Add(new ErrorModel
					{
						Message = "Attach target id was duplicated. Id=[" + targetEntity.Id + "]",
						Key = "targetRecordId"
					});
					response.Success = false;
					return DoResponse(response);
				}
				attachTargetRecords.Add(result.Object.Data[0]);
			}

			foreach (var targetId in model.DetachTargetFieldRecordIds)
			{
				query = new EntityQuery(targetEntity.Name, "id," + targetField.Name,
					EntityQuery.QueryEQ("id", targetId), null, null, null);
				result = _recordManager.Find(query);
				if ((result.Object?.Data?.Count ?? 0) == 0)
				{
					response.Errors.Add(new ErrorModel
					{
						Message = "Detach target record was not found. Id=[" + targetEntity.Id + "]",
						Key = "targetRecordId"
					});
					response.Success = false;
					return DoResponse(response);
				}
				else if (detachTargetRecords.Any(x => (Guid)x["id"] == targetId))
				{
					response.Errors.Add(new ErrorModel
					{
						Message = "Detach target id was duplicated. Id=[" + targetEntity.Id + "]",
						Key = "targetRecordId"
					});
					response.Success = false;
					return DoResponse(response);
				}
				detachTargetRecords.Add(result.Object.Data[0]);
			}

			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				connection.BeginTransaction();

				try
				{
					switch (relation.RelationType)
					{
						case EntityRelationType.OneToOne:
						case EntityRelationType.OneToMany:
							{
								foreach (var record in detachTargetRecords)
								{
									record[targetField.Name] = null;
									var updResult = _recordManager.UpdateRecord(targetEntity.Name, record);
									if (!updResult.Success)
									{
										connection.RollbackTransaction();
										response.Errors = updResult.Errors;
										response.Message = "Target record id=[" + record["id"] + "] detach operation failed.";
										response.Success = false;
										return DoResponse(response);
									}
								}

								foreach (var record in attachTargetRecords)
								{
									var patchObject = new EntityRecord();
									patchObject["id"] = (Guid)record["id"];
									patchObject[targetField.Name] = originValue;
									var updResult = _recordManager.UpdateRecord(targetEntity.Name, patchObject);
									if (!updResult.Success)
									{
										connection.RollbackTransaction();
										response.Errors = updResult.Errors;
										response.Message = "Target record id=[" + record["id"] + "] attach operation failed.";
										response.Success = false;
										return DoResponse(response);
									}
								}
							}
							break;
						case EntityRelationType.ManyToMany:
							{
								foreach (var record in detachTargetRecords)
								{
									QueryResponse updResult = _recordManager.RemoveRelationManyToManyRecord(
										relation.Id, (Guid)originValue, (Guid)record[targetField.Name]);
									if (!updResult.Success)
									{
										connection.RollbackTransaction();
										response.Errors = updResult.Errors;
										response.Message = "Target record id=[" + record["id"] + "] detach operation failed.";
										response.Success = false;
										return DoResponse(response);
									}
								}

								foreach (var record in attachTargetRecords)
								{
									QueryResponse updResult = _recordManager.CreateRelationManyToManyRecord(
										relation.Id, (Guid)originValue, (Guid)record[targetField.Name]);
									if (!updResult.Success)
									{
										connection.RollbackTransaction();
										response.Errors = updResult.Errors;
										response.Message = "Target record id=[" + record["id"] + "] attach  operation failed.";
										response.Success = false;
										return DoResponse(response);
									}
								}
							}
							break;
						default:
							{
								connection.RollbackTransaction();
								throw new Exception("Not supported relation type");
							}
					}

					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					response.Success = false;
					response.Message = ex.Message;
					return DoResponse(response);
				}
			}

			return DoResponse(response);
		}

		/// <summary>
		/// Updates entity relation records for a target record — attach/detach origin records (reverse direction).
		/// Mirror of UpdateEntityRelationRecord but operating from the target side.
		///
		/// Preserved from monolith WebApiController.cs lines 2302-2499 with full transactional semantics.
		/// </summary>
		[AcceptVerbs(new[] { "POST" }, Route = "/api/v3/{locale}/record/relation/reverse")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult UpdateEntityRelationRecordReverse(
			[FromBody] InputEntityRelationRecordReverseUpdateModel model)
		{
			BaseResponseModel response = new BaseResponseModel
			{
				Timestamp = DateTime.UtcNow,
				Success = true,
				Errors = new List<ErrorModel>()
			};

			if (model == null)
			{
				response.Errors.Add(new ErrorModel { Message = "Invalid model." });
				response.Success = false;
				return DoResponse(response);
			}

			EntityRelation relation = null;
			if (string.IsNullOrWhiteSpace(model.RelationName))
			{
				response.Errors.Add(new ErrorModel { Message = "Invalid relation name.", Key = "relationName" });
				response.Success = false;
				return DoResponse(response);
			}
			else
			{
				relation = _relationManager.Read(model.RelationName).Object;
				if (relation == null)
				{
					response.Errors.Add(new ErrorModel { Message = "Invalid relation name. No relation with that name.", Key = "relationName" });
					response.Success = false;
					return DoResponse(response);
				}
			}

			var originEntityResp = _entityManager.ReadEntity(relation.OriginEntityId);
			var targetEntityResp = _entityManager.ReadEntity(relation.TargetEntityId);
			var originEntity = originEntityResp?.Object;
			var targetEntity = targetEntityResp?.Object;

			if (originEntity == null || targetEntity == null)
			{
				response.Errors.Add(new ErrorModel { Message = "Origin or target entity not found for relation.", Key = "relationName" });
				response.Success = false;
				return DoResponse(response);
			}

			var originField = originEntity.Fields.SingleOrDefault(x => x.Id == relation.OriginFieldId);
			var targetField = targetEntity.Fields.SingleOrDefault(x => x.Id == relation.TargetFieldId);

			if (originField == null || targetField == null)
			{
				response.Errors.Add(new ErrorModel { Message = "Origin or target field not found for relation.", Key = "relationName" });
				response.Success = false;
				return DoResponse(response);
			}

			// Defensive null-coalescing: ensure lists are never null even if client omits them
			model.AttachOriginFieldRecordIds ??= new List<Guid>();
			model.DetachOriginFieldRecordIds ??= new List<Guid>();

			if (model.DetachOriginFieldRecordIds != null && model.DetachOriginFieldRecordIds.Any()
				&& originField.Required && relation.RelationType != EntityRelationType.ManyToMany)
			{
				response.Errors.Add(new ErrorModel { Message = "Cannot detach records, when origin field is required.", Key = "originFieldRecordId" });
				response.Success = false;
				return DoResponse(response);
			}

			EntityQuery query = new EntityQuery(targetEntity.Name, "id," + targetField.Name,
				EntityQuery.QueryEQ("id", model.TargetFieldRecordId), null, null, null);
			QueryResponse result = _recordManager.Find(query);
			if ((result.Object?.Data?.Count ?? 0) == 0)
			{
				response.Errors.Add(new ErrorModel
				{
					Message = "Target record was not found. Id=[" + model.TargetFieldRecordId + "]",
					Key = "targetFieldRecordId"
				});
				response.Success = false;
				return DoResponse(response);
			}

			var targetRecord = result.Object.Data[0];
			object targetValue = targetRecord[targetField.Name];

			var attachOriginRecords = new List<EntityRecord>();
			var detachOriginRecords = new List<EntityRecord>();

			foreach (var originId in model.AttachOriginFieldRecordIds)
			{
				query = new EntityQuery(originEntity.Name, "id," + originField.Name,
					EntityQuery.QueryEQ("id", originId), null, null, null);
				result = _recordManager.Find(query);
				if ((result.Object?.Data?.Count ?? 0) == 0)
				{
					response.Errors.Add(new ErrorModel
					{
						Message = "Attach origin record was not found. Id=[" + originEntity.Id + "]",
						Key = "originRecordId"
					});
					response.Success = false;
					return DoResponse(response);
				}
				else if (attachOriginRecords.Any(x => (Guid)x["id"] == originId))
				{
					response.Errors.Add(new ErrorModel
					{
						Message = "Attach origin id was duplicated. Id=[" + originEntity.Id + "]",
						Key = "originRecordId"
					});
					response.Success = false;
					return DoResponse(response);
				}
				attachOriginRecords.Add(result.Object.Data[0]);
			}

			foreach (var originId in model.DetachOriginFieldRecordIds)
			{
				query = new EntityQuery(originEntity.Name, "id," + originField.Name,
					EntityQuery.QueryEQ("id", originId), null, null, null);
				result = _recordManager.Find(query);
				if ((result.Object?.Data?.Count ?? 0) == 0)
				{
					response.Errors.Add(new ErrorModel
					{
						Message = "Detach origin record was not found. Id=[" + originEntity.Id + "]",
						Key = "originRecordId"
					});
					response.Success = false;
					return DoResponse(response);
				}
				else if (detachOriginRecords.Any(x => (Guid)x["id"] == originId))
				{
					response.Errors.Add(new ErrorModel
					{
						Message = "Detach origin id was duplicated. Id=[" + originEntity.Id + "]",
						Key = "originRecordId"
					});
					response.Success = false;
					return DoResponse(response);
				}
				detachOriginRecords.Add(result.Object.Data[0]);
			}

			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				connection.BeginTransaction();

				try
				{
					switch (relation.RelationType)
					{
						case EntityRelationType.OneToOne:
						case EntityRelationType.OneToMany:
							{
								foreach (var record in detachOriginRecords)
								{
									record[originField.Name] = null;
									var updResult = _recordManager.UpdateRecord(originEntity.Name, record);
									if (!updResult.Success)
									{
										connection.RollbackTransaction();
										response.Errors = updResult.Errors;
										response.Message = "Origin record id=[" + record["id"] + "] detach operation failed.";
										response.Success = false;
										return DoResponse(response);
									}
								}

								foreach (var record in attachOriginRecords)
								{
									var patchObject = new EntityRecord();
									patchObject["id"] = (Guid)record["id"];
									patchObject[originField.Name] = targetValue;
									var updResult = _recordManager.UpdateRecord(originEntity.Name, patchObject);
									if (!updResult.Success)
									{
										connection.RollbackTransaction();
										response.Errors = updResult.Errors;
										response.Message = "Origin record id=[" + record["id"] + "] attach operation failed.";
										response.Success = false;
										return DoResponse(response);
									}
								}
							}
							break;
						case EntityRelationType.ManyToMany:
							{
								foreach (var record in detachOriginRecords)
								{
									QueryResponse updResult = _recordManager.RemoveRelationManyToManyRecord(
										relation.Id, (Guid)record[originField.Name], (Guid)targetValue);
									if (!updResult.Success)
									{
										connection.RollbackTransaction();
										response.Errors = updResult.Errors;
										response.Message = "Origin record id=[" + record["id"] + "] detach operation failed.";
										response.Success = false;
										return DoResponse(response);
									}
								}

								foreach (var record in attachOriginRecords)
								{
									QueryResponse updResult = _recordManager.CreateRelationManyToManyRecord(
										relation.Id, (Guid)record[originField.Name], (Guid)targetValue);
									if (!updResult.Success)
									{
										connection.RollbackTransaction();
										response.Errors = updResult.Errors;
										response.Message = "Origin record id=[" + record["id"] + "] attach  operation failed.";
										response.Success = false;
										return DoResponse(response);
									}
								}
							}
							break;
						default:
							{
								connection.RollbackTransaction();
								throw new Exception("Not supported relation type");
							}
					}

					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					response.Success = false;
					response.Message = ex.Message;
					return DoResponse(response);
				}
			}

			return DoResponse(response);
		}

		#endregion

		#region << Record CRUD >>

		/// <summary>
		/// Gets a single entity record by its ID with optional field selection.
		/// Preserved from monolith WebApiController.cs lines 2504-2517.
		/// </summary>
		[AcceptVerbs(new[] { "GET" }, Route = "/api/v3/{locale}/record/{entityName}/{recordId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetRecord(Guid recordId, string entityName, string fields = "*")
		{
			QueryObject filterObj = EntityQuery.QueryEQ("id", recordId);
			EntityQuery query = new EntityQuery(entityName, fields, filterObj, null, null, null);
			QueryResponse result = _recordManager.Find(query);
			if (!result.Success)
				return DoResponse(result);

			// Strip sensitive fields before returning
			return DoResponse(result);
		}

		/// <summary>
		/// Deletes a single entity record by its ID within a transactional scope.
		/// Publishes a RecordDeletedEvent after successful commit.
		/// Preserved from monolith WebApiController.cs lines 2521-2551.
		/// </summary>
		[AcceptVerbs(new[] { "DELETE" }, Route = "/api/v3/{locale}/record/{entityName}/{recordId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public async Task<IActionResult> DeleteRecord(Guid recordId, string entityName)
		{
			var result = new QueryResponse();
			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();
					result = _recordManager.DeleteRecord(entityName, recordId);
					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					var response = new ResponseModel
					{
						Success = false,
						Timestamp = DateTime.UtcNow,
						Message = "Error while delete the record: " + ex.Message,
						Object = null
					};
					return Json(response);
				}
			}

			// Publish domain event AFTER transaction commit (AAP 0.8 event publishing)
			if (result.Success)
			{
				await _publishEndpoint.Publish(new RecordDeletedEvent
				{
					EntityName = entityName,
					RecordId = recordId
				});
			}

			return DoResponse(result);
		}

		/// <summary>
		/// Gets entity records matching a regex pattern on a specified field.
		/// Preserved from monolith WebApiController.cs lines 2555-2568.
		/// </summary>
		[AcceptVerbs(new[] { "POST" }, Route = "/api/v3/{locale}/record/{entityName}/regex/{fieldName}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetRecordsByFieldAndRegex(string fieldName, string entityName,
			[FromBody] EntityRecord patternObj)
		{
			QueryObject filterObj = EntityQuery.QueryRegex(fieldName, patternObj["pattern"]);
			EntityQuery query = new EntityQuery(entityName, "*", filterObj, null, null, null);
			QueryResponse result = _recordManager.Find(query);
			if (!result.Success)
				return DoResponse(result);

			// Strip sensitive fields before returning
			return DoResponse(result);
		}

		/// <summary>
		/// Creates a new entity record within a transactional scope.
		/// Assigns a new GUID if no "id" property is provided.
		/// Publishes a RecordCreatedEvent after successful commit.
		/// Preserved from monolith WebApiController.cs lines 2573-2612.
		/// </summary>
		[AcceptVerbs(new[] { "POST" }, Route = "/api/v3/{locale}/record/{entityName}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public async Task<IActionResult> CreateEntityRecord(string entityName,
			[FromBody] EntityRecord postObj)
		{
			// Fix double-dollar-sign problem (Angular does not post $$ property names)
			postObj = Helpers.FixDoubleDollarSignProblem(postObj);

			if (!postObj.GetProperties().Any(x => x.Key == "id"))
				postObj["id"] = Guid.NewGuid();
			else if (string.IsNullOrEmpty(postObj["id"] as string))
				postObj["id"] = Guid.NewGuid();

			// Create transaction
			var result = new QueryResponse();
			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();
					result = _recordManager.CreateRecord(entityName, postObj);
					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					var response = new ResponseModel
					{
						Success = false,
						Timestamp = DateTime.UtcNow,
						Message = "Error while saving the record: " + ex.Message,
						Object = null
					};
					return Json(response);
				}
			}

			// Publish domain event AFTER transaction commit
			if (result.Success)
			{
				await _publishEndpoint.Publish(new RecordCreatedEvent
				{
					EntityName = entityName,
					Record = postObj
				});
			}

			return DoResponse(result);
		}

		/// <summary>
		/// Creates a new entity record and attaches it to a specified relation in a single transaction.
		/// Validates the relation, the related record, and handles 1:1/1:N FK assignment vs N:N join creation.
		/// Publishes a RecordCreatedEvent after successful commit.
		/// Preserved from monolith WebApiController.cs lines 2614-2783.
		/// </summary>
		[AcceptVerbs(new[] { "POST" }, Route = "/api/v3/{locale}/record/{entityName}/with-relation/{relationName}/{relatedRecordId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public async Task<IActionResult> CreateEntityRecordWithRelation(string entityName, string relationName,
			Guid relatedRecordId, [FromBody] EntityRecord postObj)
		{
			var validationErrors = new List<ErrorModel>();

			// 1. Validate relationName
			var allRelations = _relationManager.Read().Object;
			var relation = allRelations?.SingleOrDefault(x => x.Name == relationName);
			string targetEntityName = string.Empty;
			string targetFieldName = string.Empty;
			var relatedRecord = new EntityRecord();
			var relatedRecordResponse = new QueryResponse();

			if (relation == null)
			{
				var error = new ErrorModel
				{
					Key = "relationName",
					Value = relationName,
					Message = "A relation with this name, does not exist"
				};
				validationErrors.Add(error);
			}
			else
			{
				// 1.2 Relation is correct — entityName is part of this relation
				if (relation.OriginEntityName != entityName && relation.TargetEntityName != entityName)
				{
					var error = new ErrorModel
					{
						Key = "relationName",
						Value = relationName,
						Message = "This is not the correct relation, as it does not include the requested entity: " + entityName
					};
					validationErrors.Add(error);
				}
				else
				{
					if (relation.OriginEntityName == entityName)
					{
						relatedRecordResponse = _recordManager.Find(
							new EntityQuery(relation.TargetEntityName, "*", EntityQuery.QueryEQ("id", relatedRecordId)));
						targetFieldName = relation.TargetFieldName;
					}
					else
					{
						relatedRecordResponse = _recordManager.Find(
							new EntityQuery(relation.OriginEntityName, "*", EntityQuery.QueryEQ("id", relatedRecordId)));
						targetFieldName = relation.OriginFieldName;
					}

					// 2. Validate parentRecordId exists
					if (!relatedRecordResponse.Object.Data.Any())
					{
						var error = new ErrorModel
						{
							Key = "parentRecordId",
							Value = relatedRecordId.ToString(),
							Message = "There is no parent record with this Id in the entity: " + entityName
						};
						validationErrors.Add(error);
					}
					else
					{
						relatedRecord = relatedRecordResponse.Object.Data.First();
						// 2.2 Record has value in the related field
						if (!relatedRecord.Properties.ContainsKey(targetFieldName) || relatedRecord[targetFieldName] == null)
						{
							var error = new ErrorModel
							{
								Key = "parentRecordId",
								Value = relatedRecordId.ToString(),
								Message = "The parent record does not have field " + targetFieldName + " or its value is null"
							};
							validationErrors.Add(error);
						}
					}
				}
			}

			if (postObj == null)
				postObj = new EntityRecord();

			if (validationErrors.Count > 0)
			{
				var response = new ResponseModel
				{
					Success = false,
					Timestamp = DateTime.UtcNow,
					Errors = validationErrors,
					Message = "Validation error occurred!",
					Object = null
				};
				return Json(response);
			}

			if (!postObj.GetProperties().Any(x => x.Key == "id"))
				postObj["id"] = Guid.NewGuid();
			else if (string.IsNullOrEmpty(postObj["id"] as string))
				postObj["id"] = Guid.NewGuid();

			// Create transaction
			var createResult = new QueryResponse();
			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();

					// Add the relation field value if the relation is 1:1 or 1:N
					if (relation.RelationType == EntityRelationType.OneToOne || relation.RelationType == EntityRelationType.OneToMany)
					{
						if (relation.OriginEntityName == entityName)
						{
							throw new Exception("We need a case to finish this");
						}
						else
						{
							// If currentEntity is target → assign the correct id value of the origin
							postObj[relation.TargetFieldName] = relatedRecord[relation.OriginFieldName];
						}
					}

					createResult = _recordManager.CreateRecord(entityName, postObj);

					// Create a relation record if it is N:N
					if (relation.RelationType == EntityRelationType.ManyToMany)
					{
						var relResponse = new QueryResponse();
						if (relation.OriginEntityName == entityName && relation.TargetEntityName == entityName)
						{
							throw new Exception("current entity is both target and origin, cannot find relation direction. Probably needs to be extended");
						}
						else if (relation.TargetEntityName == entityName)
						{
							relResponse = _recordManager.CreateRelationManyToManyRecord(
								relation.Id, relatedRecordId, (Guid)postObj["id"]);
						}
						else
						{
							relResponse = _recordManager.CreateRelationManyToManyRecord(
								relation.Id, (Guid)postObj["id"], relatedRecordId);
						}
						if (!relResponse.Success)
						{
							throw new Exception(relResponse.Message);
						}
					}

					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					var response = new ResponseModel
					{
						Success = false,
						Timestamp = DateTime.UtcNow,
						Message = "Error while saving the record: " + ex.Message,
						Object = null
					};
					return Json(response);
				}
			}

			// Publish domain event AFTER transaction commit
			if (createResult.Success)
			{
				await _publishEndpoint.Publish(new RecordCreatedEvent
				{
					EntityName = entityName,
					Record = postObj
				});
			}

			return DoResponse(createResult);
		}

		/// <summary>
		/// Updates an existing entity record (full replacement) within a transactional scope.
		/// Publishes a RecordUpdatedEvent after successful commit.
		/// Preserved from monolith WebApiController.cs lines 2788-2833.
		/// </summary>
		[AcceptVerbs(new[] { "PUT" }, Route = "/api/v3/{locale}/record/{entityName}/{recordId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public async Task<IActionResult> UpdateEntityRecord(string entityName, Guid recordId,
			[FromBody] EntityRecord postObj)
		{
			// Fix double-dollar-sign problem (Angular does not post $$ property names)
			postObj = Helpers.FixDoubleDollarSignProblem(postObj);

			if (!postObj.Properties.ContainsKey("id"))
			{
				postObj["id"] = recordId;
			}

			// Capture old record state for event enrichment (AAP 0.5.1 RecordUpdatedEvent.OldRecord)
			EntityRecord oldRecord = null;
			try
			{
				var findResult = _recordManager.Find(
					new EntityQuery(entityName, "*", EntityQuery.QueryEQ("id", recordId)));
				if (findResult.Success && findResult.Object.Data.Count > 0)
					oldRecord = findResult.Object.Data[0];
			}
			catch
			{
				// Non-fatal: event will have null OldRecord if pre-fetch fails
			}

			// Create transaction
			var result = new QueryResponse();
			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();
					result = _recordManager.UpdateRecord(entityName, postObj);
					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					var response = new ResponseModel
					{
						Success = false,
						Timestamp = DateTime.UtcNow,
						Message = "Error while saving the record: " + ex.Message,
						Object = null
					};
					return Json(response);
				}
			}

			// Publish domain event AFTER transaction commit
			if (result.Success)
			{
				await _publishEndpoint.Publish(new RecordUpdatedEvent
				{
					EntityName = entityName,
					OldRecord = oldRecord,
					NewRecord = postObj
				});
			}

			return DoResponse(result);
		}

		/// <summary>
		/// Partially updates an entity record (PATCH semantics — only provided fields are updated).
		/// Publishes a RecordUpdatedEvent after successful commit.
		/// Preserved from monolith WebApiController.cs lines 2837-2875.
		/// </summary>
		[AcceptVerbs(new[] { "PATCH" }, Route = "/api/v3/{locale}/record/{entityName}/{recordId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public async Task<IActionResult> PatchEntityRecord(string entityName, Guid recordId,
			[FromBody] EntityRecord postObj)
		{
			postObj["id"] = recordId;

			// Capture old record state for event enrichment
			EntityRecord oldRecord = null;
			try
			{
				var findResult = _recordManager.Find(
					new EntityQuery(entityName, "*", EntityQuery.QueryEQ("id", recordId)));
				if (findResult.Success && findResult.Object.Data.Count > 0)
					oldRecord = findResult.Object.Data[0];
			}
			catch
			{
				// Non-fatal: event will have null OldRecord if pre-fetch fails
			}

			// Create transaction
			var result = new QueryResponse();
			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();
					result = _recordManager.UpdateRecord(entityName, postObj);
					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					var response = new ResponseModel
					{
						Success = false,
						Timestamp = DateTime.UtcNow,
						Message = "Error while saving the record: " + ex.Message,
						Object = null
					};
					return Json(response);
				}
			}

			// Publish domain event AFTER transaction commit
			if (result.Success)
			{
				await _publishEndpoint.Publish(new RecordUpdatedEvent
				{
					EntityName = entityName,
					OldRecord = oldRecord,
					NewRecord = postObj
				});
			}

			return DoResponse(result);
		}

		/// <summary>
		/// Gets a list of entity records by entity name with optional ID filtering,
		/// field selection, and result limit.
		/// Preserved from monolith WebApiController.cs lines 2877-2973.
		/// </summary>
		[AcceptVerbs(new[] { "GET" }, Route = "/api/v3/{locale}/record/{entityName}/list")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetRecordsByEntityName(string entityName,
			string ids = "", string fields = "", int? limit = null)
		{
			var response = new QueryResponse();
			var recordIdList = new List<Guid>();
			var fieldList = new List<string>();

			if (!string.IsNullOrWhiteSpace(ids) && ids != "null")
			{
				var idStringList = ids.Split(',');
				var outGuid = Guid.Empty;
				foreach (var idString in idStringList)
				{
					if (Guid.TryParse(idString, out outGuid))
					{
						recordIdList.Add(outGuid);
					}
					else
					{
						response.Message = "One of the record ids is not a Guid";
						response.Timestamp = DateTime.UtcNow;
						response.Success = false;
						response.Object.Data = null;
					}
				}
			}

			if (!string.IsNullOrWhiteSpace(fields) && fields != "null")
			{
				var fieldsArray = fields.Split(',');
				var hasId = false;
				foreach (var fieldName in fieldsArray)
				{
					if (fieldName == "id")
					{
						hasId = true;
					}
					fieldList.Add(fieldName);
				}
				if (!hasId)
				{
					fieldList.Add("id");
				}
			}

			var queryList = new List<QueryObject>();
			foreach (var recordId in recordIdList)
			{
				queryList.Add(EntityQuery.QueryEQ("id", recordId));
			}

			QueryObject recordsFilterObj = null;
			if (queryList.Count > 0)
			{
				recordsFilterObj = EntityQuery.QueryOR(queryList.ToArray());
			}

			var columns = "*";
			if (fieldList.Count > 0)
			{
				if (!fieldList.Contains("id"))
				{
					fieldList.Add("id");
				}
				columns = string.Join(",", fieldList.Select(x => x.ToString()).ToArray());
			}

			EntityQuery query = new EntityQuery(entityName, columns, recordsFilterObj, null, null, null);
			if (limit != null && limit > 0)
			{
				query = new EntityQuery(entityName, columns, recordsFilterObj, null, null, limit);
			}

			var queryResponse = _recordManager.Find(query);
			if (!queryResponse.Success)
			{
				response.Message = queryResponse.Message;
				response.Timestamp = DateTime.UtcNow;
				response.Success = false;
				response.Object = null;
				return DoResponse(response);
			}

			response.Message = "Success";
			response.Timestamp = DateTime.UtcNow;
			response.Success = true;
			response.Object.Data = queryResponse.Object.Data;
			return DoResponse(response);
		}

		#endregion

		#region << CSV Import >>

		/// <summary>
		/// Imports entity records from a CSV file. Restricted to administrator role only.
		/// Preserved from monolith WebApiController.cs lines 2989-3005.
		/// </summary>
		[Authorize(Roles = "administrator")]
		[AcceptVerbs(new[] { "POST" }, Route = "/api/v3/{locale}/record/{entityName}/import")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult ImportEntityRecordsFromCsv(string entityName,
			[FromBody] JObject postObject)
		{
			string fileTempPath = "";

			if (postObject != null && postObject.Properties().Any(p => p.Name == "fileTempPath"))
			{
				fileTempPath = postObject["fileTempPath"].ToString();
			}

			ResponseModel response = _importExportManager.ImportEntityRecordsFromCsv(entityName, fileTempPath);

			return DoResponse(response);
		}

		/// <summary>
		/// Evaluates a CSV import without actually creating records (dry-run preview).
		/// Returns what would be imported along with validation errors.
		/// Restricted to administrator role only.
		/// Preserved from monolith WebApiController.cs lines 3010-3018.
		/// </summary>
		[Authorize(Roles = "administrator")]
		[AcceptVerbs(new[] { "POST" }, Route = "/api/v3/{locale}/record/{entityName}/import-evaluate")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult EvaluateImportEntityRecordsFromCsv(string entityName,
			[FromBody] JObject postObject)
		{
			ResponseModel response = _importExportManager.EvaluateImportEntityRecordsFromCsv(entityName, postObject, controller: this);

			return DoResponse(response);
		}

		#endregion
	}
}
