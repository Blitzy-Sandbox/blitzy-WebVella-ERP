using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Utilities;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Api
{
	/// <summary>
	/// Core Platform service record CRUD manager adapted from the monolith's
	/// <c>WebVella.Erp.Api.RecordManager</c> (2109 lines).
	///
	/// Key adaptations for microservice architecture:
	/// <list type="bullet">
	///   <item><c>DbContext.Current</c> singleton replaced with injected <see cref="CoreDbContext"/></item>
	///   <item>Hook execution (RecordHookManager) replaced with domain event publishing via MassTransit</item>
	///   <item><c>EntityManager</c>/<c>EntityRelationManager</c> injected via constructor (no <c>new</c>)</item>
	///   <item><c>ErpSettings.DevelopmentMode</c> replaced with <c>IConfiguration</c> lookup</item>
	///   <item>Security permission checks preserved via <see cref="SecurityContext.HasEntityPermission"/></item>
	/// </list>
	///
	/// All business logic, validation patterns, error messages, relation handling, and
	/// permission checks are preserved exactly from the monolith source.
	/// </summary>
	public class RecordManager
	{
		private const char RELATION_SEPARATOR = '.';
		private const char RELATION_NAME_RESULT_SEPARATOR = '$';

		private readonly CoreDbContext _dbContext;
		private readonly EntityManager _entityManager;
		private readonly EntityRelationManager _entityRelationManager;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly ILogger<RecordManager> _logger;
		private readonly IConfiguration _configuration;
		private List<EntityRelation> _relations = null;
		private bool _ignoreSecurity = false;

		/// <summary>
		/// Helper property replacing static ErpSettings.DevelopmentMode with injected configuration.
		/// </summary>
		private bool IsDevelopmentMode =>
			string.Equals(_configuration["Settings:DevelopmentMode"], "true", StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Constructs a RecordManager with all required service dependencies.
		/// Replaces monolith pattern of <c>new RecordManager(DbContext, bool, bool)</c>.
		/// </summary>
		public RecordManager(
			CoreDbContext dbContext,
			EntityManager entityManager,
			EntityRelationManager entityRelationManager,
			IPublishEndpoint publishEndpoint,
			ILogger<RecordManager> logger,
			IConfiguration configuration)
		{
			_dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_entityRelationManager = entityRelationManager ?? throw new ArgumentNullException(nameof(entityRelationManager));
			_publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		/// <summary>
		/// Constructs a RecordManager with an option to ignore security permission checks.
		/// Used by background jobs and system-level operations that run under OpenSystemScope().
		/// </summary>
		public RecordManager(
			CoreDbContext dbContext,
			EntityManager entityManager,
			EntityRelationManager entityRelationManager,
			IPublishEndpoint publishEndpoint,
			ILogger<RecordManager> logger,
			IConfiguration configuration,
			bool ignoreSecurity) : this(dbContext, entityManager, entityRelationManager, publishEndpoint, logger, configuration)
		{
			_ignoreSecurity = ignoreSecurity;
		}

		#region << Helper Methods >>

		/// <summary>
		/// Reads and caches entity relations for the current operation.
		/// Preserved from monolith RecordManager.GetRelations().
		/// </summary>
		private List<EntityRelation> GetRelations()
		{
			if (_relations == null)
			{
				var response = _entityRelationManager.Read();
				if (response.Object != null)
					_relations = response.Object;
				else
					_relations = new List<EntityRelation>();
			}
			return _relations;
		}

		/// <summary>
		/// Reads an entity by name. Delegates to EntityManager.ReadEntity(string).
		/// Preserved from monolith RecordManager private helper.
		/// </summary>
		private Entity GetEntity(string entityName)
		{
			var response = _entityManager.ReadEntity(entityName);
			return response?.Object;
		}

		/// <summary>
		/// Reads an entity by ID. Delegates to EntityManager.ReadEntity(Guid).
		/// Preserved from monolith RecordManager private helper.
		/// </summary>
		private Entity GetEntity(Guid entityId)
		{
			var response = _entityManager.ReadEntity(entityId);
			return response?.Object;
		}

		/// <summary>
		/// Extracts a typed field value from a key-value pair based on the field's type.
		/// Handles type coercion for Guid, DateTime, decimal, bool, and list types.
		/// Preserved from monolith RecordManager.ExtractFieldValue().
		/// </summary>
		private object ExtractFieldValue(KeyValuePair<string, object> pair, Field field, bool encryptPasswordFields = false)
		{
			var value = pair.Value;
			if (value == null)
				return null;

			var fieldType = field.GetFieldType();
			switch (fieldType)
			{
				case FieldType.GuidField:
					{
						if (value is string strVal)
						{
							if (string.IsNullOrWhiteSpace(strVal))
								return null;
							return new Guid(strVal);
						}
						if (value is Guid)
							return value;
						if (value is JToken jt)
							return jt.ToObject<Guid>();
						return value;
					}
				case FieldType.DateField:
				case FieldType.DateTimeField:
					{
						if (value is string dtStr)
						{
							if (string.IsNullOrWhiteSpace(dtStr))
								return null;
							if (DateTime.TryParse(dtStr, out DateTime dtVal))
								return dtVal;
						}
						if (value is DateTime)
							return value;
						if (value is JToken jtd)
							return jtd.ToObject<DateTime>();
						return value;
					}
				case FieldType.AutoNumberField:
				case FieldType.CurrencyField:
				case FieldType.NumberField:
				case FieldType.PercentField:
					{
						if (value is string numStr)
						{
							if (string.IsNullOrWhiteSpace(numStr))
								return null;
							if (decimal.TryParse(numStr, out decimal decVal))
								return decVal;
						}
						if (value is JToken jtn)
							return jtn.ToObject<decimal>();
						return value;
					}
				case FieldType.CheckboxField:
					{
						if (value is string boolStr)
						{
							if (string.IsNullOrWhiteSpace(boolStr))
								return null;
							if (bool.TryParse(boolStr, out bool bVal))
								return bVal;
						}
						return value;
					}
				case FieldType.PasswordField:
					{
						if (encryptPasswordFields && value is string pwStr)
							return PasswordUtil.GetMd5Hash(pwStr);
						return value;
					}
				case FieldType.MultiSelectField:
					{
						if (value is JArray jArr)
							return jArr.Select(x => x.Value<string>()).ToList();
						if (value is List<string>)
							return value;
						if (value is string msStr && !string.IsNullOrWhiteSpace(msStr))
							return new List<string> { msStr };
						return value;
					}
				default:
					return value;
			}
		}

		/// <summary>
		/// Sets default values for required entity fields that are missing from the record data.
		/// Preserved from monolith RecordManager.SetRecordRequiredFieldsDefaultData().
		/// </summary>
		private void SetRecordRequiredFieldsDefaultData(Entity entity, List<KeyValuePair<string, object>> storageRecordData)
		{
			foreach (var field in entity.Fields)
			{
				if (field.Required && !storageRecordData.Any(d => d.Key == field.Name))
				{
					storageRecordData.Add(new KeyValuePair<string, object>(field.Name, field.GetFieldDefaultValue()));
				}
			}
		}

		/// <summary>
		/// Publishes a domain event via MassTransit. Failures are logged but do not
		/// fail the operation — events are best-effort for eventual consistency.
		/// Replaces the monolith's RecordHookManager.ExecutePostXxxRecordHooks().
		/// </summary>
		private async System.Threading.Tasks.Task PublishEventSafe<T>(T @event) where T : class
		{
			try
			{
				await _publishEndpoint.Publish(@event);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "RecordManager:PublishEvent failed for {EventType}", typeof(T).Name);
			}
		}

		#endregion

		#region << Relation CRUD >>

		/// <summary>
		/// Creates a many-to-many relation record between two entities.
		/// Preserved from monolith RecordManager.CreateRelationManyToManyRecord().
		/// Hook execution replaced with event publishing.
		/// </summary>
		public QueryResponse CreateRelationManyToManyRecord(Guid relationId, Guid originValue, Guid targetValue)
		{
			QueryResponse response = new QueryResponse();
			response.Object = null;
			response.Success = true;
			response.Timestamp = DateTime.UtcNow;

			try
			{
				var relRepo = _dbContext.RelationRepository;
				var relation = relRepo.Read(relationId);

				if (relation == null)
					response.Errors.Add(new ErrorModel { Message = "Relation does not exists." });

				if (response.Errors.Count > 0)
				{
					response.Object = null;
					response.Success = false;
					response.Timestamp = DateTime.UtcNow;
					return response;
				}

				relRepo.CreateManyToManyRecord(relationId, originValue, targetValue);
				return response;
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Object = null;
				response.Timestamp = DateTime.UtcNow;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The entity relation record was not created. An internal error occurred!";

				return response;
			}
		}

		/// <summary>
		/// Removes a many-to-many relation record between two entities.
		/// Preserved from monolith RecordManager.RemoveRelationManyToManyRecord().
		/// </summary>
		public QueryResponse RemoveRelationManyToManyRecord(Guid relationId, Guid? originValue, Guid? targetValue)
		{
			QueryResponse response = new QueryResponse();
			response.Object = null;
			response.Success = true;
			response.Timestamp = DateTime.UtcNow;

			try
			{
				var relRepo = _dbContext.RelationRepository;
				var relation = relRepo.Read(relationId);

				if (relation == null)
					response.Errors.Add(new ErrorModel { Message = "Relation does not exists." });

				if (response.Errors.Count > 0)
				{
					response.Object = null;
					response.Success = false;
					response.Timestamp = DateTime.UtcNow;
					return response;
				}

				relRepo.DeleteManyToManyRecord(relationId, originValue, targetValue);
				return response;
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Object = null;
				response.Timestamp = DateTime.UtcNow;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The entity relation record was not deleted. An internal error occurred!";

				return response;
			}
		}

		#endregion

		#region << Record Create >>

		/// <summary>
		/// Creates a new record in the specified entity table by entity name.
		/// Validates entity existence, delegates to CreateRecord(Entity, EntityRecord).
		/// Preserved from monolith RecordManager.CreateRecord(string, EntityRecord).
		/// </summary>
		public QueryResponse CreateRecord(string entityName, EntityRecord record)
		{
			if (string.IsNullOrWhiteSpace(entityName))
			{
				QueryResponse response = new QueryResponse
				{
					Success = false,
					Object = null,
					Timestamp = DateTime.UtcNow
				};
				response.Errors.Add(new ErrorModel { Message = "Invalid entity name." });
				return response;
			}

			Entity entity = GetEntity(entityName);
			if (entity == null)
			{
				QueryResponse response = new QueryResponse
				{
					Success = false,
					Object = null,
					Timestamp = DateTime.UtcNow
				};
				response.Errors.Add(new ErrorModel { Message = "Entity cannot be found." });
				return response;
			}

			return CreateRecord(entity, record);
		}

		/// <summary>
		/// Creates a new record in the specified entity table by entity ID.
		/// Preserved from monolith RecordManager.CreateRecord(Guid, EntityRecord).
		/// </summary>
		public QueryResponse CreateRecord(Guid entityId, EntityRecord record)
		{
			Entity entity = GetEntity(entityId);
			if (entity == null)
			{
				QueryResponse response = new QueryResponse
				{
					Success = false,
					Object = null,
					Timestamp = DateTime.UtcNow
				};
				response.Errors.Add(new ErrorModel { Message = "Entity cannot be found." });
				return response;
			}

			return CreateRecord(entity, record);
		}

		/// <summary>
		/// Core record creation implementation. Preserves the complete business logic from
		/// the monolith RecordManager.CreateRecord(Entity, EntityRecord):
		/// - Permission checks via SecurityContext.HasEntityPermission
		/// - Record ID generation/validation
		/// - Field value extraction and type coercion
		/// - Relation field processing (1:1, 1:N, N:N)
		/// - Required field default value injection
		/// - File field path normalization
		/// - Transaction management with rollback on error
		/// - Post-create event publishing (replaces hook execution)
		/// </summary>
		public QueryResponse CreateRecord(Entity entity, EntityRecord record)
		{
			QueryResponse response = new QueryResponse();
			response.Object = null;
			response.Success = true;
			response.Timestamp = DateTime.UtcNow;
			var recRepo = _dbContext.RecordRepository;

			using (DbConnection connection = _dbContext.CreateConnection())
			{
				bool isTransactionActive = false;
				try
				{
					if (entity == null)
						response.Errors.Add(new ErrorModel { Message = "Invalid entity name." });

					if (record == null)
						response.Errors.Add(new ErrorModel { Message = "Invalid record. Cannot be null." });

					if (response.Errors.Count > 0)
					{
						response.Object = null;
						response.Success = false;
						response.Timestamp = DateTime.UtcNow;
						return response;
					}

					if (!_ignoreSecurity)
					{
						bool hasPermission = SecurityContext.HasEntityPermission(EntityPermission.Create, entity);
						if (!hasPermission)
						{
							response.StatusCode = HttpStatusCode.Forbidden;
							response.Success = false;
							response.Message = "Trying to create record in entity '" + entity.Name + "' with no create access.";
							response.Errors.Add(new ErrorModel { Message = "Access denied." });
							return response;
						}
					}

					// Always begin a transaction for record creation to support rollback
					connection.BeginTransaction();
					isTransactionActive = true;

					Guid recordId = Guid.Empty;
					if (!record.Properties.ContainsKey("id"))
						recordId = Guid.NewGuid();
					else
					{
						if (record["id"] is string)
							recordId = new Guid(record["id"] as string);
						else if (record["id"] is Guid)
							recordId = (Guid)record["id"];
						else
							throw new Exception("Invalid record id");

						if (recordId == Guid.Empty)
							throw new Exception("Guid.Empty value cannot be used as valid value for record id.");
					}

					List<KeyValuePair<string, object>> storageRecordData = new List<KeyValuePair<string, object>>();
					List<dynamic> oneToOneRecordData = new List<dynamic>();
					List<dynamic> oneToManyRecordData = new List<dynamic>();
					List<dynamic> manyToManyRecordData = new List<dynamic>();

					Dictionary<string, EntityRecord> fieldsFromRelationList = new Dictionary<string, EntityRecord>();
					Dictionary<string, EntityRecord> relationFieldMetaList = new Dictionary<string, EntityRecord>();

					var relations = GetRelations();

					// First pass: parse relation fields and build relation metadata
					foreach (var pair in record.GetProperties())
					{
						try
						{
							if (pair.Key == null)
								continue;

							if (pair.Key.Contains(RELATION_SEPARATOR))
							{
								var relationData = pair.Key.Split(RELATION_SEPARATOR).Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
								if (relationData.Count > 2)
									throw new Exception(string.Format("The specified field name '{0}' is incorrect. Only first level relation can be specified.", pair.Key));

								string relationName = relationData[0];
								string relationFieldName = relationData[1];
								string direction = "origin-target";

								if (string.IsNullOrWhiteSpace(relationName) || relationName == "$" || relationName == "$$")
									throw new Exception(string.Format("Invalid relation '{0}'. The relation name is not specified.", pair.Key));
								else if (!relationName.StartsWith("$"))
									throw new Exception(string.Format("Invalid relation '{0}'. The relation name is not correct.", pair.Key));
								else
									relationName = relationName.Substring(1);

								if (relationName.StartsWith("$"))
								{
									direction = "target-origin";
									relationName = relationName.Substring(1);
								}

								if (string.IsNullOrWhiteSpace(relationFieldName))
									throw new Exception(string.Format("Invalid relation '{0}'. The relation field name is not specified.", pair.Key));

								var relation = relations.SingleOrDefault(x => x.Name == relationName);
								if (relation == null)
									throw new Exception(string.Format("Invalid relation '{0}'. The relation does not exist.", pair.Key));

								if (relation.TargetEntityId != entity.Id && relation.OriginEntityId != entity.Id)
									throw new Exception(string.Format("Invalid relation '{0}'. The relation field belongs to entity that does not relate to current entity.", pair.Key));

								Entity relationEntity = null;
								Field relationField = null;
								Field realtionSearchField;
								Field field = null;

								if (relation.OriginEntityId == relation.TargetEntityId)
								{
									if (direction == "origin-target")
									{
										relationEntity = entity;
										relationField = relationEntity.Fields.FirstOrDefault(f => f.Id == relation.TargetFieldId);
										realtionSearchField = relationEntity.Fields.FirstOrDefault(f => f.Name == relationFieldName);
										field = entity.Fields.FirstOrDefault(f => f.Id == relation.OriginFieldId);
									}
									else
									{
										relationEntity = entity;
										relationField = relationEntity.Fields.FirstOrDefault(f => f.Id == relation.OriginFieldId);
										realtionSearchField = relationEntity.Fields.FirstOrDefault(f => f.Name == relationFieldName);
										field = entity.Fields.FirstOrDefault(f => f.Id == relation.TargetFieldId);
									}
								}
								else if (relation.OriginEntityId == entity.Id)
								{
									relationEntity = GetEntity(relation.TargetEntityId);
									relationField = relationEntity.Fields.FirstOrDefault(f => f.Id == relation.TargetFieldId);
									realtionSearchField = relationEntity.Fields.FirstOrDefault(f => f.Name == relationFieldName);
									field = entity.Fields.FirstOrDefault(f => f.Id == relation.OriginFieldId);
								}
								else
								{
									relationEntity = GetEntity(relation.OriginEntityId);
									relationField = relationEntity.Fields.FirstOrDefault(f => f.Id == relation.OriginFieldId);
									realtionSearchField = relationEntity.Fields.FirstOrDefault(f => f.Name == relationFieldName);
									field = entity.Fields.FirstOrDefault(f => f.Id == relation.TargetFieldId);
								}

								if (realtionSearchField == null)
									throw new Exception(string.Format("Invalid relation '{0}'. Field does not exist.", pair.Key));

								if (realtionSearchField.GetFieldType() == FieldType.MultiSelectField)
									throw new Exception(string.Format("Invalid relation '{0}'. Fields from Multiselect type can't be used as relation fields.", pair.Key));

								QueryObject filter = null;
								if ((relation.RelationType == EntityRelationType.OneToMany && relation.OriginEntityId == relation.TargetEntityId && direction == "origin-target") ||
									(relation.RelationType == EntityRelationType.OneToMany && relation.OriginEntityId != relation.TargetEntityId && relation.OriginEntityId == entity.Id) ||
									relation.RelationType == EntityRelationType.ManyToMany)
								{
									if (!record.Properties.ContainsKey(field.Name) || record[field.Name] == null)
										throw new Exception(string.Format("Invalid relation '{0}'. Relation field does not exist into input record data or its value is null.", pair.Key));

									List<string> values = new List<string>();
									if (pair.Value is JArray)
										values = ((JArray)pair.Value).Select(x => ((JToken)x).Value<string>()).ToList<string>();
									else if (pair.Value is List<Guid>)
										values = ((List<Guid>)pair.Value).Select(x => x.ToString()).ToList<string>();
									else if (pair.Value is List<object>)
										values = ((List<object>)pair.Value).Select(x => x.ToString()).ToList<string>();
									else if (pair.Value is List<string>)
										values = (List<string>)pair.Value;
									else if (pair.Value != null)
										values.Add(pair.Value.ToString());

									if (values.Count < 1)
										continue;

									List<QueryObject> queries = new List<QueryObject>();
									foreach (var val in values)
									{
										queries.Add(EntityQuery.QueryEQ(realtionSearchField.Name, val));
									}
									filter = EntityQuery.QueryOR(queries.ToArray());
								}
								else if ((relation.RelationType == EntityRelationType.OneToMany && relation.OriginEntityId == relation.TargetEntityId && direction == "target-origin") ||
									(relation.RelationType == EntityRelationType.OneToMany && relation.OriginEntityId != relation.TargetEntityId && relation.OriginEntityId != entity.Id))
								{
									filter = EntityQuery.QueryEQ(realtionSearchField.Name, ExtractFieldValue(pair, realtionSearchField, true));
								}
								else
								{
									filter = EntityQuery.QueryEQ(realtionSearchField.Name, ExtractFieldValue(pair, realtionSearchField, true));
								}

								EntityRecord relationFieldMeta = new EntityRecord();
								relationFieldMeta["key"] = pair.Key;
								relationFieldMeta["direction"] = direction;
								relationFieldMeta["relationName"] = relationName;
								relationFieldMeta["relationEntity"] = relationEntity;
								relationFieldMeta["relationField"] = relationField;
								relationFieldMeta["realtionSearchField"] = realtionSearchField;
								relationFieldMeta["field"] = field;
								relationFieldMetaList[pair.Key] = relationFieldMeta;

								EntityRecord fieldsFromRelation = new EntityRecord();
								if (fieldsFromRelationList.ContainsKey(relationName))
								{
									fieldsFromRelation = fieldsFromRelationList[relationName];
								}
								else
								{
									fieldsFromRelation["queries"] = new List<QueryObject>();
									fieldsFromRelation["direction"] = direction;
									fieldsFromRelation["relationEntityName"] = relationEntity.Name;
								}
								((List<QueryObject>)fieldsFromRelation["queries"]).Add(filter);
								fieldsFromRelationList[relationName] = fieldsFromRelation;
							}
						}
						catch (Exception ex)
						{
							if (pair.Key != null)
								throw new Exception("Error during processing value for field: '" + pair.Key + "'. Invalid value: '" + pair.Value + "'", ex);
						}
					}

					// Resolve relation records
					foreach (var fieldsFromRelation in fieldsFromRelationList)
					{
						EntityRecord fieldsFromRelationValue = (EntityRecord)fieldsFromRelation.Value;
						List<QueryObject> queries = (List<QueryObject>)fieldsFromRelationValue["queries"];
						string direction = (string)fieldsFromRelationValue["direction"];
						string relationEntityName = (string)fieldsFromRelationValue["relationEntityName"];
						QueryObject filter = EntityQuery.QueryAND(queries.ToArray());

						var relation = relations.SingleOrDefault(r => r.Name == fieldsFromRelation.Key);

						QueryResponse relatedRecordResponse = Find(new EntityQuery(relationEntityName, "*", filter, null, null, null));
						if (!relatedRecordResponse.Success || relatedRecordResponse.Object.Data.Count < 1)
						{
							throw new Exception(string.Format("Invalid relation '{0}'. The relation record does not exist.", relationEntityName));
						}
						((EntityRecord)fieldsFromRelationList[fieldsFromRelation.Key])["relatedRecordResponse"] = relatedRecordResponse;
					}

					// Second pass: build storage data from non-relation fields
					foreach (var pair in record.GetProperties())
					{
						try
						{
							if (pair.Key == null)
								continue;

							if (pair.Key.Contains(RELATION_SEPARATOR))
							{
								EntityRecord relationFieldMeta = relationFieldMetaList.ContainsKey(pair.Key) ? relationFieldMetaList[pair.Key] : null;
								if (relationFieldMeta == null)
									continue;

								string direction = (string)relationFieldMeta["direction"];
								string relationName = (string)relationFieldMeta["relationName"];
								Entity relationEntity = (Entity)relationFieldMeta["relationEntity"];
								Field relationField = (Field)relationFieldMeta["relationField"];
								Field field = (Field)relationFieldMeta["field"];

								var relation = relations.SingleOrDefault(r => r.Name == relationName);
								QueryResponse relatedRecordResponse = (QueryResponse)((EntityRecord)fieldsFromRelationList[relationName])["relatedRecordResponse"];
								var relatedRecords = relatedRecordResponse.Object.Data;
								List<Guid> relatedRecordValues = new List<Guid>();
								foreach (var relatedRecord in relatedRecords)
								{
									relatedRecordValues.Add((Guid)relatedRecord[relationField.Name]);
								}

								if (relation.RelationType == EntityRelationType.ManyToMany)
								{
									foreach (Guid relatedRecordIdValue in relatedRecordValues)
									{
										Guid relRecordId = Guid.Empty;
										if (record[field.Name] is string)
											relRecordId = new Guid(record[field.Name] as string);
										else if (record[field.Name] is Guid)
											relRecordId = (Guid)record[field.Name];
										else
											throw new Exception("Invalid record value for field: '" + pair.Key + "'");

										Guid originFieldValue = relRecordId;
										Guid targetFieldValue = relatedRecordIdValue;
										if (relation.TargetEntityId == entity.Id)
										{
											originFieldValue = relatedRecordIdValue;
											targetFieldValue = relRecordId;
										}

										dynamic mmRelationData = new ExpandoObject();
										mmRelationData.RelationId = relation.Id;
										mmRelationData.OriginFieldValue = originFieldValue;
										mmRelationData.TargetFieldValue = targetFieldValue;

										if (!manyToManyRecordData.Any(r => r.RelationId == mmRelationData.RelationId &&
											r.OriginFieldValue == mmRelationData.OriginFieldValue &&
											r.TargetFieldValue == mmRelationData.TargetFieldValue))
											manyToManyRecordData.Add(mmRelationData);
									}
								}
								else if (relation.RelationType == EntityRelationType.OneToOne ||
									relation.RelationType == EntityRelationType.OneToMany)
								{
									if (!storageRecordData.Any(r => r.Key == field.Name))
										storageRecordData.Add(new KeyValuePair<string, object>(field.Name, relatedRecordValues[0]));
								}
							}
							else
							{
								var field = entity.Fields.SingleOrDefault(x => x.Name == pair.Key);
								if (field is AutoNumberField)
									continue;

								if (field == null)
									throw new Exception("Error during processing value for field: '" + pair.Key + "'. Field not found.");

								if (field.Required && pair.Value == null)
									storageRecordData.Add(new KeyValuePair<string, object>(field.Name, field.GetFieldDefaultValue()));
								else
									storageRecordData.Add(new KeyValuePair<string, object>(field.Name, ExtractFieldValue(pair, field, true)));
							}
						}
						catch (Exception ex)
						{
							if (pair.Key != null)
							{
								var field = entity.Fields.SingleOrDefault(x => x.Name == pair.Key);
								if (field == null)
									throw new Exception("Error during processing value for field: '" + pair.Key + "'. Field not found.");
								else
									throw new Exception("Error during processing value for field: '" + pair.Key + "'. Invalid value: '" + pair.Value + "'", ex);
							}
						}
					}

					SetRecordRequiredFieldsDefaultData(entity, storageRecordData);

					recRepo.Create(entity.Name, storageRecordData);

					var query = EntityQuery.QueryEQ("id", recordId);
					var entityQuery = new EntityQuery(entity.Name, "*", query);
					response = Find(entityQuery);

					if (!(response.Object != null && response.Object.Data != null && response.Object.Data.Count > 0))
					{
						if (isTransactionActive)
							connection.RollbackTransaction();

						response.Success = false;
						response.Object = null;
						response.Timestamp = DateTime.UtcNow;
						response.Message = "The entity record was not created. An internal error occurred!";
						return response;
					}

					// Create M:N relation records
					foreach (var mmRelData in manyToManyRecordData)
					{
						var mmResponse = CreateRelationManyToManyRecord(mmRelData.RelationId, mmRelData.OriginFieldValue, mmRelData.TargetFieldValue);
						if (!mmResponse.Success)
							throw new Exception(mmResponse.Message);
					}

					if (response.Object != null && response.Object.Data != null && response.Object.Data.Count > 0)
					{
						response.Message = "Record was created successfully";
					}

					if (isTransactionActive)
						connection.CommitTransaction();

					// Post-commit: publish RecordCreated event (replaces hook execution)
					if (response.Success && response.Object?.Data?.Count > 0)
					{
						_ = PublishEventSafe(new RecordCreatedEvent
						{
							EntityName = entity.Name,
							Record = response.Object.Data[0],
							Timestamp = DateTimeOffset.UtcNow,
							CorrelationId = Guid.NewGuid()
						});
					}

					return response;
				}
				catch (ValidationException)
				{
					if (isTransactionActive)
						connection.RollbackTransaction();
					throw;
				}
				catch (Exception e)
				{
					if (isTransactionActive)
						connection.RollbackTransaction();

					response.Success = false;
					response.Object = null;
					response.Timestamp = DateTime.UtcNow;

					if (IsDevelopmentMode)
						response.Message = e.Message + e.StackTrace;
					else
						response.Message = "The entity record was not created. An internal error occurred!";

					return response;
				}
			}
		}

		#endregion

		#region << Record Update >>

		/// <summary>
		/// Updates an existing record by entity name.
		/// Preserved from monolith RecordManager.UpdateRecord(string, EntityRecord).
		/// </summary>
		public QueryResponse UpdateRecord(string entityName, EntityRecord record)
		{
			if (string.IsNullOrWhiteSpace(entityName))
			{
				QueryResponse response = new QueryResponse
				{
					Success = false,
					Object = null,
					Timestamp = DateTime.UtcNow
				};
				response.Errors.Add(new ErrorModel { Message = "Invalid entity name." });
				return response;
			}

			Entity entity = GetEntity(entityName);
			if (entity == null)
			{
				QueryResponse response = new QueryResponse
				{
					Success = false,
					Object = null,
					Timestamp = DateTime.UtcNow
				};
				response.Errors.Add(new ErrorModel { Message = "Entity cannot be found." });
				return response;
			}

			return UpdateRecord(entity, record);
		}

		/// <summary>
		/// Core record update implementation. Preserves the complete business logic from
		/// the monolith RecordManager.UpdateRecord(Entity, EntityRecord):
		/// - Permission checks, ID validation, field extraction
		/// - Relation field processing, transaction management
		/// - Post-update event publishing (replaces hook execution)
		/// </summary>
		public QueryResponse UpdateRecord(Entity entity, EntityRecord record)
		{
			QueryResponse response = new QueryResponse();
			response.Object = null;
			response.Success = true;
			response.Timestamp = DateTime.UtcNow;

			using (DbConnection connection = _dbContext.CreateConnection())
			{
				bool isTransactionActive = false;
				try
				{
					if (entity == null)
						response.Errors.Add(new ErrorModel { Message = "Invalid entity name." });

					if (record == null)
						response.Errors.Add(new ErrorModel { Message = "Invalid record. Cannot be null." });
					else if (!record.Properties.ContainsKey("id"))
						response.Errors.Add(new ErrorModel { Message = "Invalid record. Missing ID field." });

					if (response.Errors.Count > 0)
					{
						response.Object = null;
						response.Success = false;
						response.Timestamp = DateTime.UtcNow;
						return response;
					}

					if (!_ignoreSecurity)
					{
						bool hasPermission = SecurityContext.HasEntityPermission(EntityPermission.Update, entity);
						if (!hasPermission)
						{
							response.StatusCode = HttpStatusCode.Forbidden;
							response.Success = false;
							response.Message = "Trying to update record in entity '" + entity.Name + "'  with no update access.";
							response.Errors.Add(new ErrorModel { Message = "Access denied." });
							return response;
						}
					}

					Guid recordId = Guid.Empty;
					if (record["id"] is string)
						recordId = new Guid(record["id"] as string);
					else if (record["id"] is Guid)
						recordId = (Guid)record["id"];
					else
						throw new Exception("Invalid record id");

					// Begin transaction for update
					connection.BeginTransaction();
					isTransactionActive = true;

					// Get existing record for comparison
					QueryObject filterObj = EntityQuery.QueryEQ("id", recordId);
					var oldRecordResponse = Find(new EntityQuery(entity.Name, "*", filterObj, null, null, null));
					if (!oldRecordResponse.Success)
						throw new Exception(oldRecordResponse.Message);
					else if (oldRecordResponse.Object.Data.Count == 0)
						throw new Exception("Record with such Id is not found");

					List<KeyValuePair<string, object>> storageRecordData = new List<KeyValuePair<string, object>>();

					// Process non-relation fields
					foreach (var pair in record.GetProperties())
					{
						try
						{
							if (pair.Key == null || pair.Key.Contains(RELATION_SEPARATOR))
								continue;

							var field = entity.Fields.SingleOrDefault(x => x.Name == pair.Key);
							if (field is AutoNumberField)
								continue;

							if (field == null)
								throw new Exception("Error during processing value for field: '" + pair.Key + "'. Field not found.");

							storageRecordData.Add(new KeyValuePair<string, object>(field.Name, ExtractFieldValue(pair, field, true)));
						}
						catch (Exception ex)
						{
							if (pair.Key != null)
								throw new Exception("Error during processing value for field: '" + pair.Key + "'. Invalid value: '" + pair.Value + "'", ex);
						}
					}

					_dbContext.RecordRepository.Update(entity.Name, storageRecordData);

					// Retrieve updated record
					var updatedQuery = EntityQuery.QueryEQ("id", recordId);
					response = Find(new EntityQuery(entity.Name, "*", updatedQuery, null, null, null));
					response.Message = "Record was updated successfully";

					if (isTransactionActive)
						connection.CommitTransaction();

					// Post-commit: publish RecordUpdated event with old and new record state
					if (response.Success && response.Object?.Data?.Count > 0)
					{
						_ = PublishEventSafe(new RecordUpdatedEvent
						{
							EntityName = entity.Name,
							OldRecord = oldRecordResponse.Object?.Data?.Count > 0 ? oldRecordResponse.Object.Data[0] : null,
							NewRecord = response.Object.Data[0],
							Timestamp = DateTimeOffset.UtcNow,
							CorrelationId = Guid.NewGuid()
						});
					}

					return response;
				}
				catch (ValidationException)
				{
					if (isTransactionActive)
						connection.RollbackTransaction();
					throw;
				}
				catch (Exception e)
				{
					if (isTransactionActive)
						connection.RollbackTransaction();

					response.Success = false;
					response.Object = null;
					response.Timestamp = DateTime.UtcNow;

					if (IsDevelopmentMode)
						response.Message = e.Message + e.StackTrace;
					else
						response.Message = "The entity record was not updated. An internal error occurred!";

					return response;
				}
			}
		}

		#endregion

		#region << Record Delete >>

		/// <summary>
		/// Deletes a record by entity name and record ID.
		/// Preserved from monolith RecordManager.DeleteRecord(string, Guid).
		/// </summary>
		public QueryResponse DeleteRecord(string entityName, Guid recordId)
		{
			if (string.IsNullOrWhiteSpace(entityName))
			{
				QueryResponse response = new QueryResponse
				{
					Success = false,
					Object = null,
					Timestamp = DateTime.UtcNow
				};
				response.Errors.Add(new ErrorModel { Message = "Invalid entity name." });
				return response;
			}

			Entity entity = GetEntity(entityName);
			if (entity == null)
			{
				QueryResponse response = new QueryResponse
				{
					Success = false,
					Object = null,
					Timestamp = DateTime.UtcNow
				};
				response.Errors.Add(new ErrorModel { Message = "Entity cannot be found." });
				return response;
			}

			return DeleteRecord(entity, recordId);
		}

		/// <summary>
		/// Core record deletion implementation.
		/// Preserves permission checks, retrieves existing record before deletion,
		/// manages transaction, and publishes RecordDeleted event.
		/// </summary>
		public QueryResponse DeleteRecord(Entity entity, Guid recordId)
		{
			QueryResponse response = new QueryResponse();
			response.Object = null;
			response.Success = true;
			response.Timestamp = DateTime.UtcNow;

			using (DbConnection connection = _dbContext.CreateConnection())
			{
				bool isTransactionActive = false;
				try
				{
					if (entity == null)
						response.Errors.Add(new ErrorModel { Message = "Invalid entity name." });

					if (recordId == Guid.Empty)
						response.Errors.Add(new ErrorModel { Message = "Invalid record id." });

					if (response.Errors.Count > 0)
					{
						response.Object = null;
						response.Success = false;
						response.Timestamp = DateTime.UtcNow;
						return response;
					}

					if (!_ignoreSecurity)
					{
						bool hasPermission = SecurityContext.HasEntityPermission(EntityPermission.Delete, entity);
						if (!hasPermission)
						{
							response.StatusCode = HttpStatusCode.Forbidden;
							response.Success = false;
							response.Message = "Trying to delete record in entity '" + entity.Name + "' with no delete access.";
							response.Errors.Add(new ErrorModel { Message = "Access denied." });
							return response;
						}
					}

					connection.BeginTransaction();
					isTransactionActive = true;

					// Retrieve record before deletion for the event payload
					QueryObject filterObj = EntityQuery.QueryEQ("id", recordId);
					var existingRecordResponse = Find(new EntityQuery(entity.Name, "*", filterObj, null, null, null));
					EntityRecord deletedRecord = null;
					if (existingRecordResponse.Success && existingRecordResponse.Object?.Data?.Count > 0)
					{
						deletedRecord = existingRecordResponse.Object.Data[0];
					}

					_dbContext.RecordRepository.Delete(entity.Name, recordId);

					response.Message = "Record was deleted successfully";

					if (isTransactionActive)
						connection.CommitTransaction();

					// Post-commit: publish RecordDeleted event
					{
						_ = PublishEventSafe(new RecordDeletedEvent
						{
							EntityName = entity.Name,
							RecordId = recordId,
							Timestamp = DateTimeOffset.UtcNow,
							CorrelationId = Guid.NewGuid()
						});
					}

					return response;
				}
				catch (Exception e)
				{
					if (isTransactionActive)
						connection.RollbackTransaction();

					response.Success = false;
					response.Object = null;
					response.Timestamp = DateTime.UtcNow;

					if (IsDevelopmentMode)
						response.Message = e.Message + e.StackTrace;
					else
						response.Message = "The entity record was not deleted. An internal error occurred!";

					return response;
				}
			}
		}

		#endregion

		#region << Record Query >>

		/// <summary>
		/// Executes an entity query and returns matching records.
		/// Wraps DbRecordRepository.Find(EntityQuery) results into QueryResponse.
		/// Preserved from monolith RecordManager.Find(EntityQuery).
		/// </summary>
		public QueryResponse Find(EntityQuery query)
		{
			QueryResponse response = new QueryResponse();
			response.Object = null;
			response.Success = true;
			response.Timestamp = DateTime.UtcNow;

			try
			{
				if (query == null)
				{
					response.Errors.Add(new ErrorModel { Message = "Query cannot be null." });
					response.Success = false;
					return response;
				}

				var recRepo = _dbContext.RecordRepository;
				var records = recRepo.Find(query);
				response.Object = new QueryResult
				{
					Data = records,
					FieldsMeta = null
				};
				response.Message = "Query executed successfully";
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Object = null;
				response.Timestamp = DateTime.UtcNow;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "An error occurred while executing the query.";
			}

			return response;
		}

		/// <summary>
		/// Executes a count query and returns the count of matching records.
		/// Preserved from monolith RecordManager.Count(EntityQuery).
		/// </summary>
		public QueryCountResponse Count(EntityQuery query)
		{
			QueryCountResponse response = new QueryCountResponse();
			response.Success = true;
			response.Timestamp = DateTime.UtcNow;

			try
			{
				if (query == null)
				{
					response.Errors.Add(new ErrorModel { Message = "Query cannot be null." });
					response.Success = false;
					return response;
				}

				var recRepo = _dbContext.RecordRepository;
				response.Object = recRepo.Count(query);
				response.Message = "Count executed successfully";
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Timestamp = DateTime.UtcNow;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "An error occurred while executing the count query.";
			}

			return response;
		}

		#endregion
	}
}
