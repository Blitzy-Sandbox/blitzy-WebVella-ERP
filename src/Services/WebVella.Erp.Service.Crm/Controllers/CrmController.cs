using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Utilities;
using WebVella.Erp.Service.Crm.Database;
using WebVella.Erp.Service.Crm.Domain.Services;

namespace WebVella.Erp.Service.Crm.Controllers
{
	#region CRM Service Interfaces

	/// <summary>
	/// Abstraction for CRM-scoped record CRUD operations.
	/// Implementations delegate to the local RecordManager (for CRM-owned entities)
	/// or to gRPC calls to the Core Platform service for cross-service queries.
	/// Registered as scoped in the CRM service DI container.
	/// </summary>
	public interface ICrmRecordOperations
	{
		/// <summary>Executes an entity query and returns matching records.</summary>
		QueryResponse Find(EntityQuery query);

		/// <summary>Executes a count query and returns the count of matching records.</summary>
		QueryCountResponse Count(EntityQuery query);

		/// <summary>Creates a new record in the specified entity table.</summary>
		QueryResponse CreateRecord(string entityName, EntityRecord record);

		/// <summary>Updates an existing record by entity name.</summary>
		QueryResponse UpdateRecord(string entityName, EntityRecord record);

		/// <summary>Updates an existing record using the Entity metadata object.</summary>
		QueryResponse UpdateRecord(Entity entity, EntityRecord record);

		/// <summary>Deletes a record by entity name and record ID.</summary>
		QueryResponse DeleteRecord(string entityName, Guid recordId);

		/// <summary>Creates a many-to-many relation record between two entities.</summary>
		QueryResponse CreateRelationManyToManyRecord(Guid relationId, Guid originId, Guid targetId);

		/// <summary>Removes a many-to-many relation record between two entities.</summary>
		QueryResponse RemoveRelationManyToManyRecord(Guid relationId, Guid originId, Guid targetId);
	}

	/// <summary>
	/// Abstraction for reading entity metadata from the Core Platform service.
	/// Used by the CRM controller for entity lookups during relation management.
	/// </summary>
	public interface ICrmEntityOperations
	{
		/// <summary>Reads a single entity definition by its unique identifier.</summary>
		EntityResponse ReadEntity(Guid entityId);
	}

	/// <summary>
	/// Abstraction for reading entity relation metadata.
	/// Used by the CRM controller for relation validation and management.
	/// </summary>
	public interface ICrmRelationOperations
	{
		/// <summary>Reads all entity relations.</summary>
		EntityRelationListResponse Read();

		/// <summary>Reads a single relation by its name.</summary>
		EntityRelationResponse Read(string relationName);
	}

	#endregion

	/// <summary>
	/// CRM Microservice REST API controller exposing CRM-specific record CRUD, search,
	/// and relation management endpoints. Extracted from the monolith's WebApiController.cs,
	/// scoped ONLY to CRM-owned entities: account, contact, case, address, salutation.
	///
	/// Integrates post-mutation search indexing previously handled by AccountHook.cs,
	/// ContactHook.cs, CaseHook.cs via the SearchService.
	///
	/// All mutation endpoints publish domain events via MassTransit IPublishEndpoint
	/// AFTER transaction commit for inter-service event-driven communication.
	/// </summary>
	[Authorize]
	[ApiController]
	[Route("api/v3/{locale}/crm")]
	public class CrmController : Controller
	{
		#region Constants and Static Configuration

		/// <summary>
		/// CRM-owned entities. Only these entities are allowed through this controller.
		/// Source: AAP 0.7.1 Entity-to-Service Ownership Matrix.
		/// </summary>
		private static readonly HashSet<string> CrmEntities = new(StringComparer.OrdinalIgnoreCase)
		{
			"account", "contact", "case", "address", "salutation"
		};

		/// <summary>Separator for relation field names (e.g., "relation.field").</summary>
		private const char RELATION_SEPARATOR = '.';

		/// <summary>Separator for relation name result columns (e.g., "$relation_name.field").</summary>
		private const char RELATION_NAME_RESULT_SEPARATOR = '$';

		#endregion

		#region Dependencies

		private readonly ICrmRecordOperations _recordManager;
		private readonly ICrmEntityOperations _entityManager;
		private readonly ICrmRelationOperations _relationManager;
		private readonly SearchService _searchService;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly ILogger<CrmController> _logger;
		private readonly IConfiguration _configuration;
		private readonly CrmDbContext _crmDbContext;

		/// <summary>
		/// Constructs the CRM controller with all required dependencies injected via DI.
		/// Replaces monolith pattern of new RecordManager(), new EntityManager(), etc.
		/// </summary>
		public CrmController(
			ICrmRecordOperations recordManager,
			ICrmEntityOperations entityManager,
			ICrmRelationOperations relationManager,
			SearchService searchService,
			IPublishEndpoint publishEndpoint,
			ILogger<CrmController> logger,
			IConfiguration configuration,
			CrmDbContext crmDbContext)
		{
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_relationManager = relationManager ?? throw new ArgumentNullException(nameof(relationManager));
			_searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
			_publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
			_crmDbContext = crmDbContext ?? throw new ArgumentNullException(nameof(crmDbContext));
		}

		#endregion

		#region Response Helper Methods (from ApiControllerBase.cs)

		/// <summary>
		/// Returns a JSON response, setting the HTTP status code based on the response model.
		/// If errors exist but StatusCode is OK, forces to BadRequest.
		/// Preserved exactly from monolith ApiControllerBase.cs lines 16-30.
		/// </summary>
		protected IActionResult DoResponse(BaseResponseModel response)
		{
			// Ensure timestamp is always set to current UTC time (Issue 13)
			if (response.Timestamp == default(DateTime))
				response.Timestamp = DateTime.UtcNow;

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
		/// Returns a 404 Not Found response with an empty JSON body.
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
		/// In development mode, includes detailed exception information.
		/// In production, shows a generic error message.
		/// Preserved from monolith ApiControllerBase.cs lines 44-62.
		/// </summary>
		protected IActionResult DoBadRequestResponse(BaseResponseModel response, string message = null, Exception ex = null)
		{
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			var isDevelopment = string.Equals(
				_configuration["ASPNETCORE_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
				"Development", StringComparison.OrdinalIgnoreCase);

			if (isDevelopment)
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

		#endregion

		#region Entity Validation Helper

		/// <summary>
		/// Validates that the entity name belongs to the CRM service's owned entities.
		/// Returns null if valid, or an IActionResult error response if invalid.
		/// Enforces database-per-service boundary (AAP 0.7.1).
		/// </summary>
		private IActionResult ValidateCrmEntity(string entityName)
		{
			if (string.IsNullOrWhiteSpace(entityName) || !CrmEntities.Contains(entityName))
			{
				var response = new ResponseModel
				{
					Success = false,
					Timestamp = DateTime.UtcNow,
					Message = $"Entity '{entityName}' is not managed by the CRM service."
				};
				HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				return Json(response);
			}
			return null;
		}

		#endregion

		#region Search Index Configuration

		/// <summary>
		/// Returns the list of search index fields for a given CRM entity.
		/// Preserved exactly from WebVella.Erp.Plugins.Next/Configuration.cs.
		/// Field names, relation qualifiers ($relation_name.field_name), and ordering
		/// are character-for-character identical to the monolith.
		/// </summary>
		private static List<string> GetSearchIndexFields(string entityName)
		{
			return entityName.ToLowerInvariant() switch
			{
				"account" => new List<string>
				{
					"city", "$country_1n_account.label", "email", "fax_phone", "first_name", "fixed_phone", "last_name",
					"mobile_phone", "name", "notes", "post_code", "region", "street", "street_2", "tax_id", "type", "website"
				},
				"contact" => new List<string>
				{
					"city", "$country_1n_contact.label", "$account_nn_contact.name", "email", "fax_phone", "first_name",
					"fixed_phone", "job_title", "last_name", "mobile_phone", "notes", "post_code", "region", "street", "street_2"
				},
				"case" => new List<string>
				{
					"$account_nn_case.name", "description", "number", "priority", "$case_status_1n_case.label",
					"$case_type_1n_case.label", "subject"
				},
				_ => new List<string>()
			};
		}

		/// <summary>
		/// Invokes search index regeneration after create/update mutations.
		/// Replaces the hook-based approach (AccountHook, ContactHook, CaseHook).
		/// Only processes entities that have search index configuration.
		/// </summary>
		private void TryRegenSearchIndex(string entityName, EntityRecord record)
		{
			try
			{
				var searchFields = GetSearchIndexFields(entityName);
				if (searchFields.Count > 0)
				{
					_searchService.RegenSearchField(entityName, record, searchFields);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "CrmApi:RegenSearchIndex failed for entity {EntityName}", entityName);
			}
		}

		#endregion

		#region Event Publishing Helper

		/// <summary>
		/// Publishes a domain event via MassTransit after successful transaction commit.
		/// Failures are logged but do not fail the operation — events are best-effort
		/// for eventual consistency.
		/// </summary>
		private async Task PublishEventSafe<T>(T @event) where T : class
		{
			try
			{
				if (_publishEndpoint != null)
				{
					await _publishEndpoint.Publish(@event);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "CrmApi:PublishEvent failed for event type {EventType}", typeof(T).Name);
			}
		}

		#endregion

		#region Record CRUD Endpoints

		/// <summary>
		/// Gets a single CRM record by entity name and record ID.
		/// Adapted from WebApiController.cs lines 2502-2517.
		/// </summary>
		[HttpGet("record/{entityName}/{recordId:guid}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetRecord(string entityName, Guid recordId, string fields = "*")
		{
			var validation = ValidateCrmEntity(entityName);
			if (validation != null) return validation;

			QueryObject filterObj = EntityQuery.QueryEQ("id", recordId);
			EntityQuery query = new EntityQuery(entityName, fields, filterObj, null, null, null);
			QueryResponse result = _recordManager.Find(query);

			if (!result.Success)
				return DoResponse(result);

			return Json(result);
		}

		/// <summary>
		/// Gets a list of CRM records by entity name with optional ID filtering and field projection.
		/// Adapted from WebApiController.cs lines 2877-2972.
		/// </summary>
		[HttpGet("record/{entityName}/list")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetRecordsByEntityName(string entityName, string ids = "", string fields = "", int? limit = null)
		{
			var validation = ValidateCrmEntity(entityName);
			if (validation != null) return validation;

			var response = new QueryResponse();
			var recordIdList = new List<Guid>();
			var fieldList = new List<string>();

			// Parse comma-separated GUIDs with defensive Guid.TryParse
			if (!string.IsNullOrWhiteSpace(ids) && ids != "null")
			{
				var idStringList = ids.Split(',');
				foreach (var idString in idStringList)
				{
					if (Guid.TryParse(idString, out var outGuid))
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

			// Parse comma-separated field names, ensure "id" is always included
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

			// Build QueryObject from ID list
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

			// Build columns string
			var columns = "*";
			if (fieldList.Count > 0)
			{
				if (!fieldList.Contains("id"))
				{
					fieldList.Add("id");
				}
				columns = string.Join(",", fieldList.Select(x => x.ToString()).ToArray());
			}

			// Build and execute query
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

		/// <summary>
		/// Creates a new CRM record with auto-ID generation, transaction management,
		/// post-commit search indexing, and domain event publishing.
		/// Adapted from WebApiController.cs lines 2571-2611.
		/// </summary>
		[HttpPost("record/{entityName}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public async Task<IActionResult> CreateEntityRecord(string entityName, [FromBody] EntityRecord postObj)
		{
			var validation = ValidateCrmEntity(entityName);
			if (validation != null) return validation;

			// Fix Angular $$ property naming issue
			postObj = Helpers.FixDoubleDollarSignProblem(postObj);

			// Auto-generate ID if missing
			if (!postObj.GetProperties().Any(x => x.Key == "id"))
				postObj["id"] = Guid.NewGuid();
			else if (string.IsNullOrEmpty(postObj["id"] as string))
				postObj["id"] = Guid.NewGuid();

			// Create transaction
			var result = new QueryResponse();
			using (var connection = _crmDbContext.CreateConnection())
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
					_logger.LogError(ex, "CrmApi:CreateEntityRecord");
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

			// Post-commit: search indexing + event publishing
			if (result.Success)
			{
				TryRegenSearchIndex(entityName, postObj);
				await PublishEventSafe(new RecordCreatedEvent
				{
					EntityName = entityName,
					Record = postObj,
					Timestamp = DateTimeOffset.UtcNow,
					CorrelationId = Guid.NewGuid()
				});
			}

			return DoResponse(result);
		}

		/// <summary>
		/// Creates a CRM record with a relation to an existing record.
		/// Handles all three relation types: 1:1, 1:N, and N:N.
		/// Adapted from WebApiController.cs lines 2614-2782.
		/// </summary>
		[HttpPost("record/{entityName}/with-relation/{relationName}/{relatedRecordId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public async Task<IActionResult> CreateEntityRecordWithRelation(
			string entityName, string relationName, Guid relatedRecordId, [FromBody] EntityRecord postObj)
		{
			var validation = ValidateCrmEntity(entityName);
			if (validation != null) return validation;

			var validationErrors = new List<ErrorModel>();

			// 1. Validate relationName
			// 1.1 Relation exists
			var relation = _relationManager.Read().Object?.SingleOrDefault(x => x.Name == relationName);
			string targetEntityName = string.Empty;
			string targetFieldName = string.Empty;
			var relatedRecord = new EntityRecord();
			var relatedRecordResponse = new QueryResponse();

			if (relation == null)
			{
				validationErrors.Add(new ErrorModel
				{
					Key = "relationName",
					Value = relationName,
					Message = "A relation with this name, does not exist"
				});
			}
			else
			{
				// 1.2 Relation is correct - entityName is part of this relation
				if (relation.OriginEntityName != entityName && relation.TargetEntityName != entityName)
				{
					validationErrors.Add(new ErrorModel
					{
						Key = "relationName",
						Value = relationName,
						Message = "This is not the correct relation, as it does not include the requested entity: " + entityName
					});
				}
				else
				{
					if (relation.OriginEntityName == entityName)
					{
						relatedRecordResponse = _recordManager.Find(new EntityQuery(relation.TargetEntityName, "*", EntityQuery.QueryEQ("id", relatedRecordId)));
						targetFieldName = relation.TargetFieldName;
					}
					else
					{
						relatedRecordResponse = _recordManager.Find(new EntityQuery(relation.OriginEntityName, "*", EntityQuery.QueryEQ("id", relatedRecordId)));
						targetFieldName = relation.OriginFieldName;
					}

					// 2. Validate relatedRecordId
					// 2.1 relatedRecordId exists
					if (!relatedRecordResponse.Object.Data.Any())
					{
						validationErrors.Add(new ErrorModel
						{
							Key = "parentRecordId",
							Value = relatedRecordId.ToString(),
							Message = "There is no parent record with this Id in the entity: " + entityName
						});
					}
					else
					{
						relatedRecord = relatedRecordResponse.Object.Data.First();
						// 2.2 Record has value in the related field
						if (!relatedRecord.Properties.ContainsKey(targetFieldName) || relatedRecord[targetFieldName] == null)
						{
							validationErrors.Add(new ErrorModel
							{
								Key = "parentRecordId",
								Value = relatedRecordId.ToString(),
								Message = "The parent record does not have field " + targetFieldName + " or its value is null"
							});
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

			// Auto-generate ID if missing
			if (!postObj.GetProperties().Any(x => x.Key == "id"))
				postObj["id"] = Guid.NewGuid();
			else if (string.IsNullOrEmpty(postObj["id"] as string))
				postObj["id"] = Guid.NewGuid();

			// Create transaction
			var result = new QueryResponse();
			using (var connection = _crmDbContext.CreateConnection())
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
							// if currentEntity is target -> get the target field and assign the correct id value of the origin
							postObj[relation.TargetFieldName] = relatedRecord[relation.OriginFieldName];
						}
					}

					result = _recordManager.CreateRecord(entityName, postObj);

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
							relResponse = _recordManager.CreateRelationManyToManyRecord(relation.Id, relatedRecordId, (Guid)postObj["id"]);
						}
						else
						{
							relResponse = _recordManager.CreateRelationManyToManyRecord(relation.Id, (Guid)postObj["id"], relatedRecordId);
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
					_logger.LogError(ex, "CrmApi:CreateEntityRecordWithRelation");
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

			// Post-commit: search indexing + event publishing
			if (result.Success)
			{
				TryRegenSearchIndex(entityName, postObj);
				await PublishEventSafe(new RecordCreatedEvent
				{
					EntityName = entityName,
					Record = postObj,
					Timestamp = DateTimeOffset.UtcNow,
					CorrelationId = Guid.NewGuid()
				});
			}

			return DoResponse(result);
		}

		/// <summary>
		/// Updates an existing CRM record (full replace).
		/// Adapted from WebApiController.cs lines 2786-2833.
		/// User entity special case removed (belongs to Core service).
		/// </summary>
		[HttpPut("record/{entityName}/{recordId:guid}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public async Task<IActionResult> UpdateEntityRecord(string entityName, Guid recordId, [FromBody] EntityRecord postObj)
		{
			var validation = ValidateCrmEntity(entityName);
			if (validation != null) return validation;

			// Fix Angular $$ property naming issue
			postObj = Helpers.FixDoubleDollarSignProblem(postObj);

			if (!postObj.Properties.ContainsKey("id"))
			{
				postObj["id"] = recordId;
			}

			// Create transaction
			var result = new QueryResponse();
			using (var connection = _crmDbContext.CreateConnection())
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
					_logger.LogError(ex, "CrmApi:UpdateEntityRecord");
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

			// Post-commit: search indexing + event publishing
			if (result.Success)
			{
				TryRegenSearchIndex(entityName, postObj);
				await PublishEventSafe(new RecordUpdatedEvent
				{
					EntityName = entityName,
					NewRecord = postObj,
					Timestamp = DateTimeOffset.UtcNow,
					CorrelationId = Guid.NewGuid()
				});
			}

			return DoResponse(result);
		}

		/// <summary>
		/// Patches an existing CRM record (partial update — only submitted fields are updated).
		/// Adapted from WebApiController.cs lines 2835-2875.
		/// User entity special case removed (belongs to Core service).
		/// </summary>
		[HttpPatch("record/{entityName}/{recordId:guid}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public async Task<IActionResult> PatchEntityRecord(string entityName, Guid recordId, [FromBody] EntityRecord postObj)
		{
			var validation = ValidateCrmEntity(entityName);
			if (validation != null) return validation;

			postObj["id"] = recordId;

			// Create transaction
			var result = new QueryResponse();
			using (var connection = _crmDbContext.CreateConnection())
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
					_logger.LogError(ex, "CrmApi:PatchEntityRecord");
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

			// Post-commit: search indexing + event publishing
			if (result.Success)
			{
				TryRegenSearchIndex(entityName, postObj);
				await PublishEventSafe(new RecordUpdatedEvent
				{
					EntityName = entityName,
					NewRecord = postObj,
					Timestamp = DateTimeOffset.UtcNow,
					CorrelationId = Guid.NewGuid()
				});
			}

			return DoResponse(result);
		}

		/// <summary>
		/// Deletes a CRM record with transaction management and event publishing.
		/// Adapted from WebApiController.cs lines 2520-2551.
		/// </summary>
		[HttpDelete("record/{entityName}/{recordId:guid}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public async Task<IActionResult> DeleteRecord(string entityName, Guid recordId)
		{
			var validation = ValidateCrmEntity(entityName);
			if (validation != null) return validation;

			// Create transaction
			var result = new QueryResponse();
			using (var connection = _crmDbContext.CreateConnection())
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
					_logger.LogError(ex, "CrmApi:DeleteRecord");
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

			// Post-commit: event publishing
			if (result.Success)
			{
				await PublishEventSafe(new RecordDeletedEvent
				{
					EntityName = entityName,
					RecordId = recordId,
					Timestamp = DateTimeOffset.UtcNow,
					CorrelationId = Guid.NewGuid()
				});
			}

			return DoResponse(result);
		}

		/// <summary>
		/// Gets CRM records matching a regex pattern on a specific field.
		/// Adapted from WebApiController.cs lines 2553-2568.
		/// </summary>
		[HttpPost("record/{entityName}/regex/{fieldName}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetRecordsByFieldAndRegex(string entityName, string fieldName, [FromBody] EntityRecord patternObj)
		{
			var validation = ValidateCrmEntity(entityName);
			if (validation != null) return validation;

			QueryObject filterObj = EntityQuery.QueryRegex(fieldName, patternObj["pattern"]);
			EntityQuery query = new EntityQuery(entityName, "*", filterObj, null, null, null);
			QueryResponse result = _recordManager.Find(query);

			if (!result.Success)
				return DoResponse(result);

			return Json(result);
		}

		#endregion

		#region CRM Search Endpoint

		/// <summary>
		/// CRM quick search endpoint supporting EQ, contains, startsWith, and FTS match methods.
		/// Adapted from WebApiController.cs lines 3020-3246, scoped to CRM entities.
		/// Supports forced filters, sorting, pagination, and multiple find types
		/// (records, count, records-and-count).
		/// </summary>
		[HttpGet("search")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult CrmSearch(
			string query = "",
			string entityName = "",
			string lookupFieldsCsv = "",
			string sortField = "",
			string sortType = "asc",
			string returnFieldsCsv = "",
			string matchMethod = "EQ",
			bool matchAllFields = false,
			int skipRecords = 0,
			int limitRecords = 5,
			string findType = "records",
			string forceFiltersCsv = "")
		{
			var response = new ResponseModel();
			var responseObject = new EntityRecord();

			try
			{
				// Validate entity is CRM-owned (or empty for all CRM entities)
				if (!string.IsNullOrWhiteSpace(entityName) && !CrmEntities.Contains(entityName))
				{
					throw new Exception($"Entity '{entityName}' is not managed by the CRM service.");
				}

				if (string.IsNullOrWhiteSpace(entityName) || string.IsNullOrWhiteSpace(lookupFieldsCsv)
					|| string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(returnFieldsCsv))
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
							matchesFilter = matchAllFields
								? EntityQuery.QueryAND(filterList.ToArray())
								: EntityQuery.QueryOR(filterList.ToArray());
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
							matchesFilter = matchAllFields
								? EntityQuery.QueryAND(filterList.ToArray())
								: EntityQuery.QueryOR(filterList.ToArray());
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
							matchesFilter = matchAllFields
								? EntityQuery.QueryAND(filterList.ToArray())
								: EntityQuery.QueryOR(filterList.ToArray());
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
							matchesFilter = matchAllFields
								? EntityQuery.QueryAND(filterList.ToArray())
								: EntityQuery.QueryOR(filterList.ToArray());
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
				if (!string.IsNullOrWhiteSpace(forceFiltersCsv))
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

				#region << Generate Sorts >>

				var sortsList = new List<QuerySortObject>();
				if (!string.IsNullOrWhiteSpace(sortField))
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

				// Execute based on findType
				if (findType.ToLowerInvariant() == "records"
					|| findType.ToLowerInvariant() == "records-and-count"
					|| findType.ToLowerInvariant() == "records&count")
				{
					var matchQueryResponse = _recordManager.Find(
						new EntityQuery(entityName, returnFieldsCsv, matchesFilter, sortsList.ToArray(), skipRecords, limitRecords));
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
					var matchQueryResponse = _recordManager.Count(
						new EntityQuery(entityName, returnFieldsCsv, matchesFilter));
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
				_logger.LogError(ex, "CrmApi:CrmSearch");
				response.Success = false;
				response.Message = ex.Message;
				response.Object = null;
				return Json(response);
			}
		}

		#endregion

		#region Relation Record Management Endpoints

		/// <summary>
		/// Updates entity relation records for an origin record (attach/detach target records).
		/// Handles 1:1, 1:N, and N:N relation types.
		/// Adapted from WebApiController.cs lines 2102-2300.
		/// </summary>
		[HttpPut("record/{entityName}/relation")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult UpdateEntityRelationRecord(string entityName, [FromBody] JObject submitObj)
		{
			var validation = ValidateCrmEntity(entityName);
			if (validation != null) return validation;

			BaseResponseModel response = new BaseResponseModel { Timestamp = DateTime.UtcNow, Success = true, Errors = new List<ErrorModel>() };

			if (submitObj == null)
			{
				response.Errors.Add(new ErrorModel { Message = "Invalid model." });
				response.Success = false;
				return DoResponse(response);
			}

			// Parse relation model from JObject
			var relationName = submitObj.Value<string>("relationName");
			var originFieldRecordId = submitObj.Value<Guid?>("originFieldRecordId") ?? Guid.Empty;
			var attachTargetIds = submitObj["attachTargetFieldRecordIds"]?.ToObject<List<Guid>>() ?? new List<Guid>();
			var detachTargetIds = submitObj["detachTargetFieldRecordIds"]?.ToObject<List<Guid>>() ?? new List<Guid>();

			// Validate relation
			EntityRelation relation = null;
			if (string.IsNullOrWhiteSpace(relationName))
			{
				response.Errors.Add(new ErrorModel { Message = "Invalid relation name.", Key = "relationName" });
				response.Success = false;
				return DoResponse(response);
			}
			else
			{
				relation = _relationManager.Read(relationName)?.Object;
				if (relation == null)
				{
					response.Errors.Add(new ErrorModel { Message = "Invalid relation name. No relation with that name.", Key = "relationName" });
					response.Success = false;
					return DoResponse(response);
				}
			}

			var originEntity = _entityManager.ReadEntity(relation.OriginEntityId)?.Object;
			var targetEntity = _entityManager.ReadEntity(relation.TargetEntityId)?.Object;

			if (originEntity == null || targetEntity == null)
			{
				response.Errors.Add(new ErrorModel { Message = "Origin or target entity not found." });
				response.Success = false;
				return DoResponse(response);
			}

			var originField = originEntity.Fields.Single(x => x.Id == relation.OriginFieldId);
			var targetField = targetEntity.Fields.Single(x => x.Id == relation.TargetFieldId);

			if (detachTargetIds.Any() && targetField.Required && relation.RelationType != EntityRelationType.ManyToMany)
			{
				response.Errors.Add(new ErrorModel { Message = "Cannot detach records, when target field is required.", Key = "originFieldRecordId" });
				response.Success = false;
				return DoResponse(response);
			}

			// Find origin record
			EntityQuery originQuery = new EntityQuery(originEntity.Name, "id," + originField.Name, EntityQuery.QueryEQ("id", originFieldRecordId), null, null, null);
			QueryResponse findResult = _recordManager.Find(originQuery);
			if (findResult.Object.Data.Count == 0)
			{
				response.Errors.Add(new ErrorModel { Message = "Origin record was not found. Id=[" + originFieldRecordId + "]", Key = "originFieldRecordId" });
				response.Success = false;
				return DoResponse(response);
			}

			var originRecord = findResult.Object.Data[0];
			object originValue = originRecord[originField.Name];

			// Validate and collect target records
			var attachTargetRecords = new List<EntityRecord>();
			var detachTargetRecords = new List<EntityRecord>();

			foreach (var targetId in attachTargetIds)
			{
				var tQuery = new EntityQuery(targetEntity.Name, "id," + targetField.Name, EntityQuery.QueryEQ("id", targetId), null, null, null);
				var tResult = _recordManager.Find(tQuery);
				if (tResult.Object.Data.Count == 0)
				{
					response.Errors.Add(new ErrorModel { Message = "Attach target record was not found. Id=[" + targetEntity.Id + "]", Key = "targetRecordId" });
					response.Success = false;
					return DoResponse(response);
				}
				else if (attachTargetRecords.Any(x => (Guid)x["id"] == targetId))
				{
					response.Errors.Add(new ErrorModel { Message = "Attach target id was duplicated. Id=[" + targetEntity.Id + "]", Key = "targetRecordId" });
					response.Success = false;
					return DoResponse(response);
				}
				attachTargetRecords.Add(tResult.Object.Data[0]);
			}

			foreach (var targetId in detachTargetIds)
			{
				var tQuery = new EntityQuery(targetEntity.Name, "id," + targetField.Name, EntityQuery.QueryEQ("id", targetId), null, null, null);
				var tResult = _recordManager.Find(tQuery);
				if (tResult.Object.Data.Count == 0)
				{
					response.Errors.Add(new ErrorModel { Message = "Detach target record was not found. Id=[" + targetEntity.Id + "]", Key = "targetRecordId" });
					response.Success = false;
					return DoResponse(response);
				}
				else if (detachTargetRecords.Any(x => (Guid)x["id"] == targetId))
				{
					response.Errors.Add(new ErrorModel { Message = "Detach target id was duplicated. Id=[" + targetEntity.Id + "]", Key = "targetRecordId" });
					response.Success = false;
					return DoResponse(response);
				}
				detachTargetRecords.Add(tResult.Object.Data[0]);
			}

			// Execute within transaction
			using (var connection = _crmDbContext.CreateConnection())
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
									var updResult = _recordManager.UpdateRecord(targetEntity, record);
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
									var updResult = _recordManager.UpdateRecord(targetEntity, patchObject);
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
									QueryResponse updResult = _recordManager.RemoveRelationManyToManyRecord(relation.Id, (Guid)originValue, (Guid)record[targetField.Name]);
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
									QueryResponse updResult = _recordManager.CreateRelationManyToManyRecord(relation.Id, (Guid)originValue, (Guid)record[targetField.Name]);
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
					_logger.LogError(ex, "CrmApi:UpdateEntityRelationRecord");
					response.Success = false;
					response.Message = ex.Message;
					return DoResponse(response);
				}
			}

			return DoResponse(response);
		}

		/// <summary>
		/// Updates entity relation records for a target record (reverse direction: attach/detach origin records).
		/// Handles 1:1, 1:N, and N:N relation types.
		/// Adapted from WebApiController.cs lines 2303-2499.
		/// </summary>
		[HttpPut("record/{entityName}/relation-reverse")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult UpdateEntityRelationRecordReverse(string entityName, [FromBody] JObject submitObj)
		{
			var validation = ValidateCrmEntity(entityName);
			if (validation != null) return validation;

			BaseResponseModel response = new BaseResponseModel { Timestamp = DateTime.UtcNow, Success = true, Errors = new List<ErrorModel>() };

			if (submitObj == null)
			{
				response.Errors.Add(new ErrorModel { Message = "Invalid model." });
				response.Success = false;
				return DoResponse(response);
			}

			// Parse relation model from JObject
			var relationName = submitObj.Value<string>("relationName");
			var targetFieldRecordId = submitObj.Value<Guid?>("targetFieldRecordId") ?? Guid.Empty;
			var attachOriginIds = submitObj["attachOriginFieldRecordIds"]?.ToObject<List<Guid>>() ?? new List<Guid>();
			var detachOriginIds = submitObj["detachOriginFieldRecordIds"]?.ToObject<List<Guid>>() ?? new List<Guid>();

			// Validate relation
			EntityRelation relation = null;
			if (string.IsNullOrWhiteSpace(relationName))
			{
				response.Errors.Add(new ErrorModel { Message = "Invalid relation name.", Key = "relationName" });
				response.Success = false;
				return DoResponse(response);
			}
			else
			{
				relation = _relationManager.Read(relationName)?.Object;
				if (relation == null)
				{
					response.Errors.Add(new ErrorModel { Message = "Invalid relation name. No relation with that name.", Key = "relationName" });
					response.Success = false;
					return DoResponse(response);
				}
			}

			var originEntity = _entityManager.ReadEntity(relation.OriginEntityId)?.Object;
			var targetEntity = _entityManager.ReadEntity(relation.TargetEntityId)?.Object;

			if (originEntity == null || targetEntity == null)
			{
				response.Errors.Add(new ErrorModel { Message = "Origin or target entity not found." });
				response.Success = false;
				return DoResponse(response);
			}

			var originField = originEntity.Fields.Single(x => x.Id == relation.OriginFieldId);
			var targetField = targetEntity.Fields.Single(x => x.Id == relation.TargetFieldId);

			if (detachOriginIds.Any() && originField.Required && relation.RelationType != EntityRelationType.ManyToMany)
			{
				response.Errors.Add(new ErrorModel { Message = "Cannot detach records, when origin field is required.", Key = "originFieldRecordId" });
				response.Success = false;
				return DoResponse(response);
			}

			// Find target record
			EntityQuery targetQuery = new EntityQuery(targetEntity.Name, "id," + targetField.Name, EntityQuery.QueryEQ("id", targetFieldRecordId), null, null, null);
			QueryResponse findResult = _recordManager.Find(targetQuery);
			if (findResult.Object.Data.Count == 0)
			{
				response.Errors.Add(new ErrorModel { Message = "Target record was not found. Id=[" + targetFieldRecordId + "]", Key = "targetFieldRecordId" });
				response.Success = false;
				return DoResponse(response);
			}

			var targetRecord = findResult.Object.Data[0];
			object targetValue = targetRecord[targetField.Name];

			// Validate and collect origin records
			var attachOriginRecords = new List<EntityRecord>();
			var detachOriginRecords = new List<EntityRecord>();

			foreach (var originId in attachOriginIds)
			{
				var oQuery = new EntityQuery(originEntity.Name, "id," + originField.Name, EntityQuery.QueryEQ("id", originId), null, null, null);
				var oResult = _recordManager.Find(oQuery);
				if (oResult.Object.Data.Count == 0)
				{
					response.Errors.Add(new ErrorModel { Message = "Attach origin record was not found. Id=[" + originEntity.Id + "]", Key = "originRecordId" });
					response.Success = false;
					return DoResponse(response);
				}
				else if (attachOriginRecords.Any(x => (Guid)x["id"] == originId))
				{
					response.Errors.Add(new ErrorModel { Message = "Attach origin id was duplicated. Id=[" + originEntity.Id + "]", Key = "originRecordId" });
					response.Success = false;
					return DoResponse(response);
				}
				attachOriginRecords.Add(oResult.Object.Data[0]);
			}

			foreach (var originId in detachOriginIds)
			{
				var oQuery = new EntityQuery(originEntity.Name, "id," + originField.Name, EntityQuery.QueryEQ("id", originId), null, null, null);
				var oResult = _recordManager.Find(oQuery);
				if (oResult.Object.Data.Count == 0)
				{
					response.Errors.Add(new ErrorModel { Message = "Detach origin record was not found. Id=[" + originEntity.Id + "]", Key = "originRecordId" });
					response.Success = false;
					return DoResponse(response);
				}
				else if (detachOriginRecords.Any(x => (Guid)x["id"] == originId))
				{
					response.Errors.Add(new ErrorModel { Message = "Detach origin id was duplicated. Id=[" + originEntity.Id + "]", Key = "originRecordId" });
					response.Success = false;
					return DoResponse(response);
				}
				detachOriginRecords.Add(oResult.Object.Data[0]);
			}

			// Execute within transaction
			using (var connection = _crmDbContext.CreateConnection())
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
									var updResult = _recordManager.UpdateRecord(originEntity, record);
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
									var updResult = _recordManager.UpdateRecord(originEntity, patchObject);
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
									QueryResponse updResult = _recordManager.RemoveRelationManyToManyRecord(relation.Id, (Guid)record[originField.Name], (Guid)targetValue);
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
									QueryResponse updResult = _recordManager.CreateRelationManyToManyRecord(relation.Id, (Guid)record[originField.Name], (Guid)targetValue);
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
					_logger.LogError(ex, "CrmApi:UpdateEntityRelationRecordReverse");
					response.Success = false;
					response.Message = ex.Message;
					return DoResponse(response);
				}
			}

			return DoResponse(response);
		}

		#endregion

		#region Lookup Endpoints

		/// <summary>
		/// Returns all salutation records for dropdown/lookup population.
		/// </summary>
		[HttpGet("lookup/salutation")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetSalutations()
		{
			var response = new ResponseModel();
			try
			{
				var queryResponse = _recordManager.Find(new EntityQuery("salutation", "*", null, null, null, null));
				if (!queryResponse.Success)
				{
					response.Success = false;
					response.Message = queryResponse.Message;
					response.Timestamp = DateTime.UtcNow;
					return DoResponse(response);
				}

				response.Success = true;
				response.Timestamp = DateTime.UtcNow;
				response.Message = "Success";
				response.Object = queryResponse.Object.Data;
				return Json(response);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CrmApi:GetSalutations");
				response.Success = false;
				response.Message = ex.Message;
				response.Timestamp = DateTime.UtcNow;
				return Json(response);
			}
		}

		/// <summary>
		/// Returns address records related to a specific CRM entity record.
		/// Uses relation lookup to find address records linked via entity relations.
		/// </summary>
		[HttpGet("record/{entityName}/{recordId:guid}/addresses")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetAddresses(string entityName, Guid recordId)
		{
			var validation = ValidateCrmEntity(entityName);
			if (validation != null) return validation;

			var response = new ResponseModel();
			try
			{
				// Look up all relations involving the address entity and the requested entity
				var allRelations = _relationManager.Read().Object;
				if (allRelations == null)
				{
					response.Success = false;
					response.Message = "Could not retrieve entity relations.";
					response.Timestamp = DateTime.UtcNow;
					return Json(response);
				}

				// Find the relation between the specified entity and the address entity
				var addressRelation = allRelations.FirstOrDefault(r =>
					(r.OriginEntityName == entityName && r.TargetEntityName == "address") ||
					(r.OriginEntityName == "address" && r.TargetEntityName == entityName));

				if (addressRelation == null)
				{
					// No relation found; return empty result
					response.Success = true;
					response.Timestamp = DateTime.UtcNow;
					response.Message = "No address relation found for entity: " + entityName;
					response.Object = new List<EntityRecord>();
					return Json(response);
				}

				// Query addresses via the relation
				string addressFieldName;
				if (addressRelation.OriginEntityName == entityName)
				{
					// Entity is the origin, address is the target
					// For 1:N, target records have the FK pointing to origin
					addressFieldName = addressRelation.TargetFieldName;
				}
				else
				{
					// Address is the origin, entity is the target
					addressFieldName = addressRelation.OriginFieldName;
				}

				// First get the origin record's field value for the relation
				var entityFieldName = addressRelation.OriginEntityName == entityName
					? addressRelation.OriginFieldName
					: addressRelation.TargetFieldName;

				var entityQuery = new EntityQuery(entityName, "id," + entityFieldName, EntityQuery.QueryEQ("id", recordId), null, null, null);
				var entityResult = _recordManager.Find(entityQuery);

				if (!entityResult.Success || entityResult.Object.Data.Count == 0)
				{
					response.Success = false;
					response.Message = "Record not found.";
					response.Timestamp = DateTime.UtcNow;
					return Json(response);
				}

				var entityRecord = entityResult.Object.Data[0];
				var fieldValue = entityRecord[entityFieldName];

				if (fieldValue == null)
				{
					response.Success = true;
					response.Timestamp = DateTime.UtcNow;
					response.Message = "Success";
					response.Object = new List<EntityRecord>();
					return Json(response);
				}

				// Query address records
				QueryObject addressFilter;
				if (addressRelation.RelationType == EntityRelationType.ManyToMany)
				{
					// For N:N, use the EQL relation query syntax
					var eqlText = $"SELECT * FROM address WHERE $${addressRelation.Name}.id = @recordId";
					var eqlParams = new List<EqlParameter> { new EqlParameter("recordId", recordId) };
					try
					{
						var eqlCmd = new EqlCommand(eqlText, eqlParams);
						var addressRecords = eqlCmd.Execute();
						response.Success = true;
						response.Timestamp = DateTime.UtcNow;
						response.Message = "Success";
						response.Object = addressRecords;
						return Json(response);
					}
					catch (EqlException eqlEx)
					{
						response.Success = false;
						response.Timestamp = DateTime.UtcNow;
						foreach (var eqlError in eqlEx.Errors)
						{
							response.Errors.Add(new ErrorModel("eql", "", eqlError.Message));
						}
						return Json(response);
					}
				}
				else
				{
					// For 1:1 or 1:N, use direct field filter
					addressFilter = EntityQuery.QueryEQ(addressFieldName, fieldValue);
					var addressQuery = new EntityQuery("address", "*", addressFilter, null, null, null);
					var addressResult = _recordManager.Find(addressQuery);

					if (!addressResult.Success)
					{
						response.Success = false;
						response.Message = addressResult.Message;
						response.Timestamp = DateTime.UtcNow;
						return Json(response);
					}

					response.Success = true;
					response.Timestamp = DateTime.UtcNow;
					response.Message = "Success";
					response.Object = addressResult.Object.Data;
					return Json(response);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CrmApi:GetAddresses");
				response.Success = false;
				response.Message = ex.Message;
				response.Timestamp = DateTime.UtcNow;
				return Json(response);
			}
		}

		#endregion
	}
}
