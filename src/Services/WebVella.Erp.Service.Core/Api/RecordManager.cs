using MassTransit;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using WebVella.Erp.SharedKernel;
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
	///   <item>Hook execution (RecordHookManager) replaced with domain event publishing via MassTransit <see cref="IPublishEndpoint"/></item>
	///   <item><c>EntityManager</c>/<c>EntityRelationManager</c> injected via constructor (no <c>new</c>)</item>
	///   <item><c>ErpSettings.DevelopmentMode</c> used for error detail toggling</item>
	///   <item>Security permission checks preserved via <see cref="SecurityContext.HasEntityPermission"/></item>
	///   <item>Async methods added for CRUD operations that publish events; synchronous wrappers provided for backward compatibility</item>
	/// </list>
	///
	/// All business logic, validation patterns, error messages, relation handling ($/$$ patterns),
	/// typed value normalization, file field handling, and permission checks are preserved exactly
	/// from the monolith source.
	/// </summary>
	public class RecordManager
	{
		private const char RELATION_SEPARATOR = '.';
		private const char RELATION_NAME_RESULT_SEPARATOR = '$';

		private readonly CoreDbContext _dbContext;
		private readonly EntityManager _entityManager;
		private readonly EntityRelationManager _entityRelationManager;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly DbFileRepository _fileRepository;
		private List<EntityRelation> _relations = null;
		private bool _ignoreSecurity;
		private readonly bool _publishEvents;

		/// <summary>
		/// Constructs a RecordManager with all required service dependencies.
		/// Replaces monolith pattern of <c>new RecordManager(DbContext, bool, bool)</c>.
		/// </summary>
		/// <param name="dbContext">Per-service ambient database context replacing the monolith's static DbContext.Current singleton.</param>
		/// <param name="entityManager">Entity metadata CRUD manager for resolving entity definitions.</param>
		/// <param name="entityRelationManager">Entity relation metadata manager for relation lookups.</param>
		/// <param name="publishEndpoint">MassTransit message bus abstraction for publishing domain events.</param>
		/// <param name="ignoreSecurity">When true, bypasses SecurityContext permission checks. Used by system-level operations.</param>
		/// <param name="publishEvents">When true, publishes domain events after CRUD operations. Replaces monolith's executeHooks flag.</param>
		public RecordManager(
			CoreDbContext dbContext,
			EntityManager entityManager,
			EntityRelationManager entityRelationManager,
			IPublishEndpoint publishEndpoint,
			bool ignoreSecurity = false,
			bool publishEvents = true)
		{
			_dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_entityRelationManager = entityRelationManager ?? throw new ArgumentNullException(nameof(entityRelationManager));
			_publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
			_ignoreSecurity = ignoreSecurity;
			_publishEvents = publishEvents;
			_fileRepository = new DbFileRepository(dbContext);
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
				_relations = _entityRelationManager.Read().Object;
			}

			if (_relations == null)
				return new List<EntityRelation>();

			return _relations;
		}

		/// <summary>
		/// Reads an entity by name. Delegates to EntityManager.ReadEntity(string).
		/// Preserved from monolith RecordManager private helper.
		/// </summary>
		private Entity GetEntity(string entityName)
		{
			return _entityManager.ReadEntity(entityName).Object;
		}

		/// <summary>
		/// Reads an entity by ID. Delegates to EntityManager.ReadEntity(Guid).
		/// Preserved from monolith RecordManager private helper.
		/// </summary>
		private Entity GetEntity(Guid entityId)
		{
			return _entityManager.ReadEntity(entityId).Object;
		}

		/// <summary>
		/// Extracts a typed field value from a key-value pair based on the field's type.
		/// Handles type coercion, timezone conversion, password hashing, and multi-select arrays.
		/// Preserved exactly from monolith RecordManager.ExtractFieldValue() (lines 1857-2064).
		/// </summary>
		private object ExtractFieldValue(KeyValuePair<string, object>? fieldValue, Field field, bool encryptPasswordFields = false)
		{
			if (fieldValue != null && fieldValue.Value.Key != null)
			{
				var pair = fieldValue.Value;
				if (pair.Value == DBNull.Value)
				{
					pair = new KeyValuePair<string, object>(pair.Key, null);
				}

				if (field is AutoNumberField)
				{
					if (pair.Value == null)
						return null;
					if (pair.Value is string)
						return (int)decimal.Parse(pair.Value as string);

					return Convert.ToDecimal(pair.Value);
				}
				else if (field is CheckboxField)
				{
					if (pair.Value is string)
						return Convert.ToBoolean(pair.Value as string);
					return pair.Value as bool?;
				}
				else if (field is CurrencyField)
				{
					if (pair.Value == null)
						return null;

					decimal decimalValue;
					if (pair.Value is string)
						decimalValue = decimal.Parse(pair.Value as string);
					else
						decimalValue = Convert.ToDecimal(Convert.ToString(pair.Value));

					return decimal.Round(decimalValue, ((CurrencyField)field).Currency.DecimalDigits, MidpointRounding.AwayFromZero);
				}
				else if (field is DateField)
				{
					if (pair.Value == null)
						return null;

					DateTime? date = null;
					if (pair.Value is string)
					{
						if (string.IsNullOrWhiteSpace(pair.Value as string))
							return null;
						date = DateTime.Parse(pair.Value as string);
						switch (date.Value.Kind)
						{
							case DateTimeKind.Utc:
								return date.Value.ConvertToAppDate();
							case DateTimeKind.Local:
								return date.Value.ConvertToAppDate();
							case DateTimeKind.Unspecified:
								return date.Value;
						}
					}
					else
					{
						date = pair.Value as DateTime?;
						switch (date.Value.Kind)
						{
							case DateTimeKind.Utc:
								return date.Value.ConvertToAppDate();
							case DateTimeKind.Local:
								return date.Value.ConvertToAppDate();
							case DateTimeKind.Unspecified:
								return date.Value;
						}
					}
					return date;
				}
				else if (field is DateTimeField)
				{
					if (pair.Value == null)
						return null;

					DateTime? date = null;
					if (pair.Value is string)
					{
						if (string.IsNullOrWhiteSpace(pair.Value as string))
							return null;
						date = DateTime.Parse(pair.Value as string);
						switch (date.Value.Kind)
						{
							case DateTimeKind.Utc:
								return date;
							case DateTimeKind.Local:
								return date.Value.ToUniversalTime();
							case DateTimeKind.Unspecified:
								{
									var erpTimeZone = TimeZoneInfo.FindSystemTimeZoneById(ErpSettings.TimeZoneName);
									return TimeZoneInfo.ConvertTimeToUtc(date.Value, erpTimeZone);
								}
						}
					}
					else
					{
						date = pair.Value as DateTime?;

						switch (date.Value.Kind)
						{
							case DateTimeKind.Utc:
								return date;
							case DateTimeKind.Local:
								return date.Value.ToUniversalTime();
							case DateTimeKind.Unspecified:
								{
									var erpTimeZone = TimeZoneInfo.FindSystemTimeZoneById(ErpSettings.TimeZoneName);
									return TimeZoneInfo.ConvertTimeToUtc(date.Value, erpTimeZone);
								}
						}
					}
					return date;
				}
				else if (field is EmailField)
					return pair.Value as string;
				else if (field is FileField)
					return pair.Value as string;
				else if (field is ImageField)
					return pair.Value as string;
				else if (field is HtmlField)
					return pair.Value as string;
				else if (field is MultiLineTextField)
					return pair.Value as string;
				else if (field is GeographyField)
					return pair.Value as string;
				else if (field is MultiSelectField)
				{
					if (pair.Value == null)
						return null;
					else if (pair.Value is JArray)
						return ((JArray)pair.Value).Select(x => ((JToken)x).Value<string>()).ToList<string>();
					else if (pair.Value is List<object>)
						return ((List<object>)pair.Value).Select(x => ((object)x).ToString()).ToList<string>();
					else
						return pair.Value as IEnumerable<string>;
				}
				else if (field is NumberField)
				{
					if (pair.Value == null)
						return null;
					if (pair.Value is string)
						return decimal.Parse(pair.Value as string);

					return Convert.ToDecimal(pair.Value);
				}
				else if (field is PasswordField)
				{
					if (encryptPasswordFields)
					{
						if (((PasswordField)field).Encrypted == true)
						{
							if (string.IsNullOrWhiteSpace(pair.Value as string))
								return null;

							return PasswordUtil.GetMd5Hash(pair.Value as string);
						}
					}
					return pair.Value;
				}
				else if (field is PercentField)
				{
					if (pair.Value == null)
						return null;
					if (pair.Value is string)
						return decimal.Parse(pair.Value as string);

					return Convert.ToDecimal(pair.Value);
				}
				else if (field is PhoneField)
					return pair.Value as string;
				else if (field is GuidField)
				{
					if (pair.Value is string)
					{
						if (string.IsNullOrWhiteSpace(pair.Value as string))
							return null;

						return new Guid(pair.Value as string);
					}

					if (pair.Value is Guid)
						return (Guid?)pair.Value;

					if (pair.Value == null)
						return (Guid?)null;

					throw new Exception("Invalid Guid field value.");
				}
				else if (field is SelectField)
					return pair.Value as string;
				else if (field is TextField)
					return pair.Value as string;
				else if (field is UrlField)
					return pair.Value as string;
			}
			else
			{
				return field.GetFieldDefaultValue();
			}

			throw new Exception("System Error. A field type is not supported in field value extraction process.");
		}

		/// <summary>
		/// Sets default values for required entity fields that are missing from the record data.
		/// Excludes AutoNumberField, FileField, and ImageField (handled separately).
		/// Preserved from monolith RecordManager.SetRecordRequiredFieldsDefaultData().
		/// </summary>
		private void SetRecordRequiredFieldsDefaultData(Entity entity, List<KeyValuePair<string, object>> recordData)
		{
			if (recordData == null)
				return;

			if (entity == null)
				return;

			foreach (var field in entity.Fields)
			{
				if (field.Required && !recordData.Any(p => p.Key == field.Name)
					&& field.GetFieldType() != FieldType.AutoNumberField
					&& field.GetFieldType() != FieldType.FileField
					&& field.GetFieldType() != FieldType.ImageField)
				{
					var defaultValue = field.GetFieldDefaultValue();
					recordData.Add(new KeyValuePair<string, object>(field.Name, defaultValue));
				}
			}
		}

		#endregion

		#region << Relation CRUD >>

		/// <summary>
		/// Synchronous wrapper for <see cref="CreateRelationManyToManyRecordAsync"/>.
		/// Provided for backward compatibility with existing callers.
		/// </summary>
		public QueryResponse CreateRelationManyToManyRecord(Guid relationId, Guid originValue, Guid targetValue)
			=> CreateRelationManyToManyRecordAsync(relationId, originValue, targetValue).GetAwaiter().GetResult();

		/// <summary>
		/// Creates a many-to-many relation record between two entities.
		/// Preserved from monolith RecordManager.CreateRelationManyToManyRecord() (lines 51-126).
		/// Hook execution replaced with domain event publishing via MassTransit.
		/// </summary>
		public async Task<QueryResponse> CreateRelationManyToManyRecordAsync(Guid relationId, Guid originValue, Guid targetValue)
		{
			QueryResponse response = new QueryResponse();
			response.Object = null;
			response.Success = true;
			response.Timestamp = DateTime.UtcNow;

			try
			{
				var relation = _dbContext.RelationRepository.Read(relationId);

				if (relation == null)
					response.Errors.Add(new ErrorModel { Message = "Relation does not exists." });

				if (response.Errors.Count > 0)
				{
					response.Object = null;
					response.Success = false;
					response.Timestamp = DateTime.UtcNow;
					return response;
				}

				if (_publishEvents)
				{
					using (var connection = _dbContext.CreateConnection())
					{
						try
						{
							connection.BeginTransaction();

							// Pre-event: publish for notification/audit (replaces RecordHookManager.ExecutePreCreateManyToManyRelationHook)
							await _publishEndpoint.Publish(new PreRelationCreateEvent
							{
								RelationName = relation.Name,
								OriginId = originValue,
								TargetId = targetValue,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});

							_dbContext.RelationRepository.CreateManyToManyRecord(relationId, originValue, targetValue);

							connection.CommitTransaction();

							// Post-event: notify other services asynchronously (replaces RecordHookManager.ExecutePostCreateManyToManyRelationHook)
							await _publishEndpoint.Publish(new RelationCreatedEvent
							{
								RelationName = relation.Name,
								OriginId = originValue,
								TargetId = targetValue,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});

							return response;
						}
						catch
						{
							connection.RollbackTransaction();
							throw;
						}
					}
				}
				else
				{
					_dbContext.RelationRepository.CreateManyToManyRecord(relationId, originValue, targetValue);
					return response;
				}
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Object = null;
				response.Timestamp = DateTime.UtcNow;

				if (ErpSettings.DevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The entity relation record was not created. An internal error occurred!";

				return response;
			}
		}

		/// <summary>
		/// Synchronous wrapper for <see cref="RemoveRelationManyToManyRecordAsync"/>.
		/// Provided for backward compatibility with existing callers.
		/// </summary>
		public QueryResponse RemoveRelationManyToManyRecord(Guid relationId, Guid? originValue, Guid? targetValue)
			=> RemoveRelationManyToManyRecordAsync(relationId, originValue, targetValue).GetAwaiter().GetResult();

		/// <summary>
		/// Removes a many-to-many relation record between two entities.
		/// Preserved from monolith RecordManager.RemoveRelationManyToManyRecord() (lines 128-204).
		/// Hook execution replaced with domain event publishing via MassTransit.
		/// </summary>
		public async Task<QueryResponse> RemoveRelationManyToManyRecordAsync(Guid relationId, Guid? originValue, Guid? targetValue)
		{
			QueryResponse response = new QueryResponse();
			response.Object = null;
			response.Success = true;
			response.Timestamp = DateTime.UtcNow;

			try
			{
				var relation = _dbContext.RelationRepository.Read(relationId);

				if (relation == null)
					response.Errors.Add(new ErrorModel { Message = "Relation does not exists." });

				if (response.Errors.Count > 0)
				{
					response.Object = null;
					response.Success = false;
					response.Timestamp = DateTime.UtcNow;
					return response;
				}

				if (_publishEvents)
				{
					using (var connection = _dbContext.CreateConnection())
					{
						try
						{
							connection.BeginTransaction();

							// Pre-event (replaces RecordHookManager.ExecutePreDeleteManyToManyRelationHook)
							await _publishEndpoint.Publish(new PreRelationDeleteEvent
							{
								RelationName = relation.Name,
								OriginId = originValue,
								TargetId = targetValue,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});

							_dbContext.RelationRepository.DeleteManyToManyRecord(relationId, originValue, targetValue);

							connection.CommitTransaction();

							// Post-event (replaces RecordHookManager.ExecutePostDeleteManyToManyRelationHook)
							await _publishEndpoint.Publish(new RelationDeletedEvent
							{
								RelationName = relation.Name,
								OriginId = originValue,
								TargetId = targetValue,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});

							return response;
						}
						catch
						{
							connection.RollbackTransaction();
							throw;
						}
					}
				}
				else
				{
					_dbContext.RelationRepository.DeleteManyToManyRecord(relationId, originValue, targetValue);
					return response;
				}
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Object = null;
				response.Timestamp = DateTime.UtcNow;

				if (ErpSettings.DevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The entity relation record was not created. An internal error occurred!";

				return response;
			}
		}

		#endregion

		#region << Record Create >>

		/// <summary>
		/// Synchronous wrapper for <see cref="CreateRecordAsync(string, EntityRecord)"/>.
		/// </summary>
		public virtual QueryResponse CreateRecord(string entityName, EntityRecord record)
			=> CreateRecordAsync(entityName, record).GetAwaiter().GetResult();

		/// <summary>
		/// Creates a new record in the specified entity table by entity name.
		/// Validates entity existence, delegates to CreateRecordAsync(Entity, EntityRecord).
		/// Preserved from monolith RecordManager.CreateRecord(string, EntityRecord) (lines 206-234).
		/// </summary>
		public async Task<QueryResponse> CreateRecordAsync(string entityName, EntityRecord record)
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

			return await CreateRecordCoreAsync(entity, record);
		}

		/// <summary>
		/// Core record creation implementation. Preserves the complete business logic from
		/// the monolith RecordManager.CreateRecord(Entity, EntityRecord) (lines 254-902):
		/// - Permission checks via SecurityContext.HasEntityPermission
		/// - Record ID generation/validation
		/// - Relation data separation ($field, $$field patterns)
		/// - Typed value normalization per field type (timezone, multiselect, password, files)
		/// - One-to-one, one-to-many, many-to-many related record processing
		/// - File field temp-to-final path moves
		/// - Transaction management with rollback on error
		/// - Post-create event publishing (replaces hook execution)
		/// </summary>
		private async Task<QueryResponse> CreateRecordCoreAsync(Entity entity, EntityRecord record)
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
						bool hasPermisstion = SecurityContext.HasEntityPermission(EntityPermission.Create, entity);
						if (!hasPermisstion)
						{
							response.StatusCode = HttpStatusCode.Forbidden;
							response.Success = false;
							response.Message = "Trying to create record in entity '" + entity.Name + "' with no create access.";
							response.Errors.Add(new ErrorModel { Message = "Access denied." });
							return response;
						}
					}

					// Always open transaction when publishEvents is true or when relation fields are present
					if (record.Properties.Any(p => p.Key.StartsWith("$")) || _publishEvents)
					{
						connection.BeginTransaction();
						isTransactionActive = true;
					}

					// Pre-create event (replaces RecordHookManager.ExecutePreCreateRecordHooks)
					if (_publishEvents)
					{
						await _publishEndpoint.Publish(new PreRecordCreateEvent
						{
							EntityName = entity.Name,
							Record = record,
							Timestamp = DateTimeOffset.UtcNow,
							CorrelationId = Guid.NewGuid()
						});
					}

					Guid recordId = Guid.Empty;
					if (!record.Properties.ContainsKey("id"))
						recordId = Guid.NewGuid();
					else
					{
						//fixes issue with ID coming from webapi request
						if (record["id"] is string)
							recordId = new Guid(record["id"] as string);
						else if (record["id"] is Guid)
							recordId = (Guid)record["id"];
						else
							throw new Exception("Invalid record id");

						if (recordId == Guid.Empty)
							throw new Exception("Guid.Empty value cannot be used as valid value for record id.");
					}

					record["id"] = recordId;

					List<KeyValuePair<string, object>> storageRecordData = new List<KeyValuePair<string, object>>();
					List<dynamic> oneToOneRecordData = new List<dynamic>();
					List<dynamic> oneToManyRecordData = new List<dynamic>();
					List<dynamic> manyToManyRecordData = new List<dynamic>();

					Dictionary<string, EntityRecord> fieldsFromRelationList = new Dictionary<string, EntityRecord>();
					Dictionary<string, EntityRecord> relationFieldMetaList = new Dictionary<string, EntityRecord>();

					var relations = GetRelations();

					// First pass: parse relation fields and build relation metadata (lines 348-543)
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

								//check for target priority mark $$
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

								if (relation.RelationType == EntityRelationType.OneToOne &&
									((relation.TargetEntityId == entity.Id && field.Name == "id") || (relation.OriginEntityId == entity.Id && relationField.Name == "id")))
									throw new Exception(string.Format("Invalid relation '{0}'. Can't use relations when relation field is id field.", pair.Key));

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
										values = ((List<Guid>)pair.Value).Select(x => ((Guid)x).ToString()).ToList<string>();
									else if (pair.Value is List<object>)
										values = ((List<object>)pair.Value).Select(x => ((object)x).ToString()).ToList<string>();
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
									List<string> values = new List<string>();
									if (pair.Value is JArray)
									{
										values = ((JArray)pair.Value).Select(x => ((JToken)x).Value<string>()).ToList<string>();
										if (values.Count > 0)
										{
											var newPair = new KeyValuePair<string, object>(pair.Key, values[0]);
											filter = EntityQuery.QueryEQ(realtionSearchField.Name, ExtractFieldValue(newPair, realtionSearchField, true));
										}
										else
										{
											throw new Exception("Array has not elements");
										}
									}
									else if (pair.Value is List<Guid>)
									{
										values = ((List<Guid>)pair.Value).Select(x => x.ToString()).ToList();
										if (values.Count > 0)
										{
											var newPair = new KeyValuePair<string, object>(pair.Key, values[0]);
											filter = EntityQuery.QueryEQ(realtionSearchField.Name, ExtractFieldValue(newPair, realtionSearchField, true));
										}
										else
										{
											throw new Exception("Array has not elements");
										}
									}
									else
									{
										filter = EntityQuery.QueryEQ(realtionSearchField.Name, ExtractFieldValue(pair, realtionSearchField, true));
									}
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

								if (fieldsFromRelationList.Any(r => r.Key == relation.Name))
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

					// Resolve relation records (lines 545-571)
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
						else if (relatedRecordResponse.Object.Data.Count > 1 && ((relation.RelationType == EntityRelationType.OneToMany && relation.OriginEntityId == relation.TargetEntityId && direction == "target-origin") ||
							(relation.RelationType == EntityRelationType.OneToMany && relation.OriginEntityId != relation.TargetEntityId && relation.TargetEntityId == entity.Id) ||
							relation.RelationType == EntityRelationType.OneToOne))
						{
							throw new Exception(string.Format("Invalid relation '{0}'. There are multiple relation records matching this value.", relationEntityName));
						}

						((EntityRecord)fieldsFromRelationList[fieldsFromRelation.Key])["relatedRecordResponse"] = relatedRecordResponse;
					}

					// Second pass: build storage data from fields (lines 572-714)
					List<Tuple<Field, string>> fileFields = new List<Tuple<Field, string>>();
					foreach (var pair in record.GetProperties())
					{
						try
						{
							if (pair.Key == null)
								continue;

							if (pair.Key.Contains(RELATION_SEPARATOR))
							{
								EntityRecord relationFieldMeta = relationFieldMetaList.FirstOrDefault(f => f.Key == pair.Key).Value;

								if (relationFieldMeta == null)
									continue;

								string direction = (string)relationFieldMeta["direction"];
								string relationName = (string)relationFieldMeta["relationName"];
								Entity relationEntity = (Entity)relationFieldMeta["relationEntity"];
								Field relationField = (Field)relationFieldMeta["relationField"];
								Field realtionSearchField = (Field)relationFieldMeta["realtionSearchField"];
								Field field = (Field)relationFieldMeta["field"];

								var relation = relations.SingleOrDefault(r => r.Name == relationName);

								QueryResponse relatedRecordResponse = (QueryResponse)((EntityRecord)fieldsFromRelationList[relationName])["relatedRecordResponse"];

								var relatedRecords = relatedRecordResponse.Object.Data;
								List<Guid> relatedRecordValues = new List<Guid>();
								foreach (var relatedRecord in relatedRecords)
								{
									relatedRecordValues.Add((Guid)relatedRecord[relationField.Name]);
								}

								if (relation.RelationType == EntityRelationType.OneToOne &&
									((relation.OriginEntityId == relation.TargetEntityId && direction == "origin-target") || (relation.OriginEntityId != relation.TargetEntityId && relation.OriginEntityId == entity.Id)))
								{
									if (!record.Properties.ContainsKey(field.Name) || record[field.Name] == null)
										throw new Exception(string.Format("Invalid relation '{0}'. Relation field does not exist into input record data or its value is null.", pair.Key));

									var relatedRecord = relatedRecords[0];
									List<KeyValuePair<string, object>> relRecordData = new List<KeyValuePair<string, object>>();
									relRecordData.Add(new KeyValuePair<string, object>("id", relatedRecord["id"]));
									relRecordData.Add(new KeyValuePair<string, object>(relationField.Name, record[field.Name]));

									dynamic ooRelationData = new ExpandoObject();
									ooRelationData.RelationId = relation.Id;
									ooRelationData.RecordData = relRecordData;
									ooRelationData.EntityName = relationEntity.Name;

									oneToOneRecordData.Add(ooRelationData);
								}
								else if (relation.RelationType == EntityRelationType.OneToMany &&
									((relation.OriginEntityId == relation.TargetEntityId && direction == "origin-target") || (relation.OriginEntityId != relation.TargetEntityId && relation.OriginEntityId == entity.Id)))
								{
									if (!record.Properties.ContainsKey(field.Name) || record[field.Name] == null)
										throw new Exception(string.Format("Invalid relation '{0}'. Relation field does not exist into input record data or its value is null.", pair.Key));

									foreach (var data in relatedRecordResponse.Object.Data)
									{
										List<KeyValuePair<string, object>> relRecordData = new List<KeyValuePair<string, object>>();
										relRecordData.Add(new KeyValuePair<string, object>("id", data["id"]));
										relRecordData.Add(new KeyValuePair<string, object>(relationField.Name, record[field.Name]));

										dynamic omRelationData = new ExpandoObject();
										omRelationData.RelationId = relation.Id;
										omRelationData.RecordData = relRecordData;
										omRelationData.EntityName = relationEntity.Name;

										oneToManyRecordData.Add(omRelationData);
									}
								}
								else if (relation.RelationType == EntityRelationType.ManyToMany)
								{
									foreach (Guid relatedRecordIdValue in relatedRecordValues)
									{
										Guid relRecordId = Guid.Empty;
										if (record[field.Name] is string)
											relRecordId = new Guid(record[field.Name] as string);
										else if (record[field.Name] is Guid)
											relRecordId = (Guid)record[field.Name];
										else
											throw new Exception("Invalid record value for field: '" + pair.Key + "'. Invalid value: '" + pair.Value + "'");

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

										if (!manyToManyRecordData.Any(r => r.RelationId == mmRelationData.RelationId && r.OriginFieldValue == mmRelationData.OriginFieldValue && r.TargetFieldValue == mmRelationData.TargetFieldValue))
											manyToManyRecordData.Add(mmRelationData);
									}
								}
								else
								{
									if (!storageRecordData.Any(r => r.Key == field.Name))
										storageRecordData.Add(new KeyValuePair<string, object>(field.Name, relatedRecordValues[0]));
								}
							}
							else
							{
								var field = entity.Fields.SingleOrDefault(x => x.Name == pair.Key);

								if (field is AutoNumberField) //Autonumber Value is always autogenerated
									continue;

								if (field is FileField || field is ImageField)
								{
									fileFields.Add(new Tuple<Field, string>(field, pair.Value as string));
								}
								else
								{
									if (field == null)
										throw new Exception("Error during processing value for field: '" + pair.Key + "'. Field not found.");

									if (field.Required && pair.Value == null)
										storageRecordData.Add(new KeyValuePair<string, object>(field.Name, field.GetFieldDefaultValue()));
									else
										storageRecordData.Add(new KeyValuePair<string, object>(field.Name, ExtractFieldValue(pair, field, true)));
								}
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

					// Process file fields: move temp files to final paths (lines 720-750)
					foreach (var item in fileFields)
					{
						Field field = item.Item1;
						string path = item.Item2;
						if (!string.IsNullOrWhiteSpace(path) && path.StartsWith("/fs/"))
							path = path.Substring(3);

						if (!string.IsNullOrWhiteSpace(path) && path.StartsWith("fs/"))
							path = path.Substring(2);

						if (field.Required && string.IsNullOrWhiteSpace(path))
							storageRecordData.Add(new KeyValuePair<string, object>(field.Name, field.GetFieldDefaultValue()));
						else
						{
							if (!string.IsNullOrWhiteSpace(path) && path.StartsWith(DbFileRepository.FOLDER_SEPARATOR + DbFileRepository.TMP_FOLDER_NAME))
							{
								var fileName = path.Split(new[] { '/' }).Last();
								string source = path;
								string target = $"/{entity.Name}/{record["id"]}/{fileName}";
								var movedFile = _fileRepository.Move(source, target, false);

								storageRecordData.Add(new KeyValuePair<string, object>(field.Name, target));
							}
							else
							{
								storageRecordData.Add(new KeyValuePair<string, object>(field.Name, path));
							}
						}
					}

					recRepo.Create(entity.Name, storageRecordData);

					var query = EntityQuery.QueryEQ("id", recordId);
					var entityQuery = new EntityQuery(entity.Name, "*", query);

					// when user creates record, it is returned ignoring create permissions
					bool oldIgnoreSecurity = _ignoreSecurity;
					_ignoreSecurity = true;
					response = Find(entityQuery);
					_ignoreSecurity = oldIgnoreSecurity;

					//if not created exit immediately
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

					// Process one-to-one related records (lines 775-812)
					foreach (var ooRelData in oneToOneRecordData)
					{
						EntityRecord ooRecord = new EntityRecord();
						if (_publishEvents)
						{
							var data = (IEnumerable<KeyValuePair<string, object>>)ooRelData.RecordData;
							foreach (var obj in data)
								ooRecord[obj.Key] = obj.Value;

							// Pre-update event for related record
							await _publishEndpoint.Publish(new PreRecordUpdateEvent
							{
								EntityName = ooRelData.EntityName,
								Record = ooRecord,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});

							List<KeyValuePair<string, object>> recordData = new List<KeyValuePair<string, object>>();
							foreach (var property in ooRecord.Properties)
								recordData.Add(new KeyValuePair<string, object>(property.Key, property.Value));

							ooRelData.RecordData = recordData;
						}

						recRepo.Update(ooRelData.EntityName, ooRelData.RecordData);

						if (_publishEvents)
						{
							await _publishEndpoint.Publish(new RecordUpdatedEvent
							{
								EntityName = ooRelData.EntityName,
								NewRecord = ooRecord,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});
						}
					}

					// Process one-to-many related records (lines 814-853)
					foreach (var omRelData in oneToManyRecordData)
					{
						EntityRecord ooRecord = new EntityRecord();
						if (_publishEvents)
						{
							var data = (IEnumerable<KeyValuePair<string, object>>)omRelData.RecordData;
							foreach (var obj in data)
								ooRecord[obj.Key] = obj.Value;

							await _publishEndpoint.Publish(new PreRecordUpdateEvent
							{
								EntityName = omRelData.EntityName,
								Record = ooRecord,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});

							List<KeyValuePair<string, object>> recordData = new List<KeyValuePair<string, object>>();
							foreach (var property in ooRecord.Properties)
								recordData.Add(new KeyValuePair<string, object>(property.Key, property.Value));

							omRelData.RecordData = recordData;
						}

						recRepo.Update(omRelData.EntityName, omRelData.RecordData);

						if (_publishEvents)
						{
							await _publishEndpoint.Publish(new RecordUpdatedEvent
							{
								EntityName = omRelData.EntityName,
								NewRecord = ooRecord,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});
						}
					}

					// Process many-to-many relation records (lines 856-862)
					foreach (var mmRelData in manyToManyRecordData)
					{
						var mmResponse = CreateRelationManyToManyRecord(mmRelData.RelationId, mmRelData.OriginFieldValue, mmRelData.TargetFieldValue);

						if (!mmResponse.Success)
							throw new Exception(mmResponse.Message);
					}

					// Execute post-create event (replaces RecordHookManager.ExecutePostCreateRecordHooks, lines 869-870)
					if (response.Object != null && response.Object.Data != null && response.Object.Data.Count > 0)
					{
						response.Message = "Record was created successfully";

						if (_publishEvents)
						{
							await _publishEndpoint.Publish(new RecordCreatedEvent
							{
								EntityName = entity.Name,
								Record = response.Object.Data[0],
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});
						}
					}

					if (isTransactionActive)
						connection.CommitTransaction();

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

					if (ErpSettings.DevelopmentMode)
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
		/// Synchronous wrapper for <see cref="UpdateRecordAsync(string, EntityRecord)"/>.
		/// </summary>
		public virtual QueryResponse UpdateRecord(string entityName, EntityRecord record)
			=> UpdateRecordAsync(entityName, record).GetAwaiter().GetResult();

		/// <summary>
		/// Updates an existing record by entity name.
		/// Preserved from monolith RecordManager.UpdateRecord(string, EntityRecord) (lines 904-932).
		/// </summary>
		public async Task<QueryResponse> UpdateRecordAsync(string entityName, EntityRecord record)
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

			return await UpdateRecordCoreAsync(entity, record);
		}

		/// <summary>
		/// Core record update implementation. Preserves the complete business logic from
		/// the monolith RecordManager.UpdateRecord(Entity, EntityRecord) (lines 952-1577).
		/// Includes nested 1:1, 1:N, and M:N related record updates with event publishing.
		/// </summary>
		private async Task<QueryResponse> UpdateRecordCoreAsync(Entity entity, EntityRecord record)
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
						bool hasPermisstion = SecurityContext.HasEntityPermission(EntityPermission.Update, entity);
						if (!hasPermisstion)
						{
							response.StatusCode = HttpStatusCode.Forbidden;
							response.Success = false;
							response.Message = "Trying to update record in entity '" + entity.Name + "'  with no update access.";
							response.Errors.Add(new ErrorModel { Message = "Access denied." });
							return response;
						}
					}

					//fixes issue with ID coming from webapi request
					Guid recordId = Guid.Empty;
					if (record["id"] is string)
						recordId = new Guid(record["id"] as string);
					else if (record["id"] is Guid)
						recordId = (Guid)record["id"];
					else
						throw new Exception("Invalid record id");

					// Always open transaction for update
					if (record.Properties.Any(p => p.Key.StartsWith("$")) || _publishEvents)
					{
						connection.BeginTransaction();
						isTransactionActive = true;
					}

					// Pre-update event (replaces RecordHookManager.ExecutePreUpdateRecordHooks)
					if (_publishEvents)
					{
						await _publishEndpoint.Publish(new PreRecordUpdateEvent
						{
							EntityName = entity.Name,
							Record = record,
							Timestamp = DateTimeOffset.UtcNow,
							CorrelationId = Guid.NewGuid()
						});
					}

					QueryObject filterObj = EntityQuery.QueryEQ("id", recordId);
					var oldRecordResponse = Find(new EntityQuery(entity.Name, "*", filterObj, null, null, null));
					if (!oldRecordResponse.Success)
						throw new Exception(oldRecordResponse.Message);
					else if (oldRecordResponse.Object.Data.Count == 0)
					{
						throw new Exception("Record with such Id is not found");
					}
					var oldRecord = oldRecordResponse.Object.Data[0];

					List<KeyValuePair<string, object>> storageRecordData = new List<KeyValuePair<string, object>>();
					List<dynamic> oneToOneRecordData = new List<dynamic>();
					List<dynamic> oneToManyRecordData = new List<dynamic>();
					List<dynamic> manyToManyRecordData = new List<dynamic>();

					Dictionary<string, EntityRecord> fieldsFromRelationList = new Dictionary<string, EntityRecord>();
					Dictionary<string, EntityRecord> relationFieldMetaList = new Dictionary<string, EntityRecord>();

					// First pass: parse relation fields (lines 1047-1220)
					foreach (var pair in record.GetProperties())
					{
						try
						{
							if (pair.Key == null)
								continue;

							if (pair.Key.Contains(RELATION_SEPARATOR))
							{
								var relations = GetRelations();

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
									else if (pair.Value is List<object>)
										values = ((List<object>)pair.Value).Select(x => ((object)x).ToString()).ToList<string>();
									else if (pair.Value is List<Guid>)
										values = ((List<Guid>)pair.Value).Select(x => ((Guid)x).ToString()).ToList<string>();
									else if (pair.Value is List<string>)
										values = (List<string>)pair.Value;
									else if (pair.Value != null)
										values.Add(pair.Value.ToString());

									if (relation.RelationType == EntityRelationType.ManyToMany)
									{
										Guid? originFieldOldValue = (Guid)oldRecord[field.Name];
										Guid? targetFieldOldValue = null;
										if (relation.TargetEntityId == entity.Id)
										{
											originFieldOldValue = null;
											targetFieldOldValue = (Guid)oldRecord[field.Name];
										}

										var mmResponse = RemoveRelationManyToManyRecord(relation.Id, originFieldOldValue, targetFieldOldValue);

										if (!mmResponse.Success)
											throw new Exception(mmResponse.Message);
									}

									if (values.Count < 1)
										continue;

									List<QueryObject> queries = new List<QueryObject>();
									foreach (var val in values)
									{
										queries.Add(EntityQuery.QueryEQ(realtionSearchField.Name, val));
									}

									filter = EntityQuery.QueryOR(queries.ToArray());
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

								if (fieldsFromRelationList.Any(r => r.Key == relation.Name))
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

					// Resolve relation records (lines 1222-1248)
					foreach (var fieldsFromRelation in fieldsFromRelationList)
					{
						EntityRecord fieldsFromRelationValue = (EntityRecord)fieldsFromRelation.Value;
						List<QueryObject> queries = (List<QueryObject>)fieldsFromRelationValue["queries"];
						string direction = (string)fieldsFromRelationValue["direction"];
						string relationEntityName = (string)fieldsFromRelationValue["relationEntityName"];
						QueryObject filter = EntityQuery.QueryAND(queries.ToArray());

						var relation = _relations.SingleOrDefault(r => r.Name == fieldsFromRelation.Key);

						QueryResponse relatedRecordResponse = Find(new EntityQuery(relationEntityName, "*", filter, null, null, null));

						if (!relatedRecordResponse.Success || relatedRecordResponse.Object.Data.Count < 1)
						{
							throw new Exception(string.Format("Invalid relation '{0}'. The relation record does not exist.", relationEntityName));
						}
						else if (relatedRecordResponse.Object.Data.Count > 1 && ((relation.RelationType == EntityRelationType.OneToMany && relation.OriginEntityId == relation.TargetEntityId && direction == "target-origin") ||
							(relation.RelationType == EntityRelationType.OneToMany && relation.OriginEntityId != relation.TargetEntityId && relation.TargetEntityId == entity.Id) ||
							relation.RelationType == EntityRelationType.OneToOne))
						{
							throw new Exception(string.Format("Invalid relation '{0}'. There are multiple relation records matching this value.", relationEntityName));
						}

						((EntityRecord)fieldsFromRelationList[fieldsFromRelation.Key])["relatedRecordResponse"] = relatedRecordResponse;
					}

					// Second pass: build storage data (lines 1249-1387)
					List<Tuple<Field, string>> fileFields = new List<Tuple<Field, string>>();
					foreach (var pair in record.GetProperties())
					{
						try
						{
							if (pair.Key == null)
								continue;

							if (pair.Key.Contains(RELATION_SEPARATOR))
							{
								EntityRecord relationFieldMeta = relationFieldMetaList.FirstOrDefault(f => f.Key == pair.Key).Value;

								if (relationFieldMeta == null)
									continue;

								string direction = (string)relationFieldMeta["direction"];
								string relationName = (string)relationFieldMeta["relationName"];
								Entity relationEntity = (Entity)relationFieldMeta["relationEntity"];
								Field relationField = (Field)relationFieldMeta["relationField"];
								Field realtionSearchField = (Field)relationFieldMeta["realtionSearchField"];
								Field field = (Field)relationFieldMeta["field"];

								var relation = _relations.SingleOrDefault(r => r.Name == relationName);

								QueryResponse relatedRecordResponse = (QueryResponse)((EntityRecord)fieldsFromRelationList[relationName])["relatedRecordResponse"];

								var relatedRecords = relatedRecordResponse.Object.Data;
								List<Guid> relatedRecordValues = new List<Guid>();
								foreach (var relatedRecord in relatedRecords)
								{
									relatedRecordValues.Add((Guid)relatedRecord[relationField.Name]);
								}

								if (relation.RelationType == EntityRelationType.OneToOne &&
									((relation.OriginEntityId == relation.TargetEntityId && direction == "origin-target") || relation.OriginEntityId == entity.Id))
								{
									if (!record.Properties.ContainsKey(field.Name) || record[field.Name] == null)
										throw new Exception(string.Format("Invalid relation '{0}'. Relation field does not exist into input record data or its value is null.", pair.Key));

									var relatedRecord = relatedRecords[0];
									List<KeyValuePair<string, object>> relRecordData = new List<KeyValuePair<string, object>>();
									relRecordData.Add(new KeyValuePair<string, object>("id", relatedRecord["id"]));
									relRecordData.Add(new KeyValuePair<string, object>(relationField.Name, record[field.Name]));

									dynamic ooRelationData = new ExpandoObject();
									ooRelationData.RelationId = relation.Id;
									ooRelationData.RecordData = relRecordData;
									ooRelationData.EntityName = relationEntity.Name;

									oneToOneRecordData.Add(ooRelationData);
								}
								else if (relation.RelationType == EntityRelationType.OneToMany &&
									((relation.OriginEntityId == relation.TargetEntityId && direction == "origin-target") || relation.OriginEntityId == entity.Id))
								{
									if (!record.Properties.ContainsKey(field.Name) || record[field.Name] == null)
										throw new Exception(string.Format("Invalid relation '{0}'. Relation field does not exist into input record data or its value is null.", pair.Key));

									foreach (var data in relatedRecordResponse.Object.Data)
									{
										List<KeyValuePair<string, object>> relRecordData = new List<KeyValuePair<string, object>>();
										relRecordData.Add(new KeyValuePair<string, object>("id", data["id"]));
										relRecordData.Add(new KeyValuePair<string, object>(relationField.Name, record[field.Name]));

										dynamic omRelationData = new ExpandoObject();
										omRelationData.RelationId = relation.Id;
										omRelationData.RecordData = relRecordData;
										omRelationData.EntityName = relationEntity.Name;

										oneToManyRecordData.Add(omRelationData);
									}
								}
								else if (relation.RelationType == EntityRelationType.ManyToMany)
								{
									foreach (Guid relatedRecordIdValue in relatedRecordValues)
									{
										Guid relRecordId = Guid.Empty;
										if (record[field.Name] is string)
											relRecordId = new Guid(record[field.Name] as string);
										else if (record[field.Name] is Guid)
											relRecordId = (Guid)record[field.Name];
										else
											throw new Exception("Invalid record value for field: '" + pair.Key + "'. Invalid value: '" + pair.Value + "'");

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

										if (!manyToManyRecordData.Any(r => r.RelationId == mmRelationData.RelationId && r.OriginFieldValue == mmRelationData.OriginFieldValue && r.TargetFieldValue == mmRelationData.TargetFieldValue))
											manyToManyRecordData.Add(mmRelationData);
									}
								}
								else
								{
									if (!storageRecordData.Any(r => r.Key == field.Name))
										storageRecordData.Add(new KeyValuePair<string, object>(field.Name, relatedRecordValues[0]));
								}
							}
							else
							{
								var field = entity.Fields.SingleOrDefault(x => x.Name == pair.Key);

								if (field == null)
									continue;

								if (field is PasswordField && pair.Value == null)
									continue;

								if (field is AutoNumberField)
									continue;

								if (field is FileField || field is ImageField)
								{
									fileFields.Add(new Tuple<Field, string>(field, pair.Value as string));
								}
								else
								{
									if (!storageRecordData.Any(r => r.Key == field.Name))
										storageRecordData.Add(new KeyValuePair<string, object>(field.Name, ExtractFieldValue(pair, field, true)));
								}
							}
						}
						catch (Exception ex)
						{
							if (pair.Key != null)
								throw new Exception("Error during processing value for field: '" + pair.Key + "'. Invalid value: '" + pair.Value + "'", ex);
						}
					}

					var recRepo = _dbContext.RecordRepository;

					// Process file fields (lines 1393-1433)
					foreach (var item in fileFields)
					{
						Field field = item.Item1;
						string path = item.Item2;

						var originalFieldData = oldRecord.GetProperties().First(f => f.Key == field.Name);

						if (string.IsNullOrWhiteSpace(path))
						{
							//delete file
							string pathToDelete = (string)originalFieldData.Value;
							if (!string.IsNullOrWhiteSpace(pathToDelete))
								_fileRepository.Delete(pathToDelete);

							storageRecordData.Add(new KeyValuePair<string, object>(field.Name, field.GetFieldDefaultValue()));
						}
						else
						{
							if (path.StartsWith("/fs/"))
								path = path.Substring(3);

							if (path.StartsWith("fs/"))
								path = path.Substring(2);

							if (path.StartsWith(DbFileRepository.FOLDER_SEPARATOR + DbFileRepository.TMP_FOLDER_NAME))
							{
								var fileName = path.Split(new[] { '/' }).Last();
								string source = path;
								string target = $"/{entity.Name}/{record["id"]}/{fileName}";
								var movedFile = _fileRepository.Move(source, target, true);

								storageRecordData.Add(new KeyValuePair<string, object>(field.Name, target));
							}
							else
							{
								storageRecordData.Add(new KeyValuePair<string, object>(field.Name, path));
							}
						}
					}

					if (!storageRecordData.Any(r => r.Key == "id"))
						storageRecordData.Add(new KeyValuePair<string, object>("id", recordId));

					recRepo.Update(entity.Name, storageRecordData);

					var query = EntityQuery.QueryEQ("id", recordId);
					var entityQuery = new EntityQuery(entity.Name, "*", query);

					bool oldIgnoreSecurity2 = _ignoreSecurity;
					_ignoreSecurity = true;
					response = Find(entityQuery);
					_ignoreSecurity = oldIgnoreSecurity2;

					if (!(response.Object != null && response.Object.Data.Count > 0))
					{
						if (isTransactionActive)
							connection.RollbackTransaction();
						response.Success = false;
						response.Object = null;
						response.Timestamp = DateTime.UtcNow;
						response.Message = "The entity record was not update. An internal error occurred!";
						return response;
					}

					// Process related records (lines 1452-1529)
					foreach (var ooRelData in oneToOneRecordData)
					{
						EntityRecord ooRecord = new EntityRecord();
						if (_publishEvents)
						{
							var data = (IEnumerable<KeyValuePair<string, object>>)ooRelData.RecordData;
							foreach (var obj in data)
								ooRecord[obj.Key] = obj.Value;

							await _publishEndpoint.Publish(new PreRecordUpdateEvent
							{
								EntityName = ooRelData.EntityName,
								Record = ooRecord,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});

							List<KeyValuePair<string, object>> recordData = new List<KeyValuePair<string, object>>();
							foreach (var property in ooRecord.Properties)
								recordData.Add(new KeyValuePair<string, object>(property.Key, property.Value));

							ooRelData.RecordData = recordData;
						}

						recRepo.Update(ooRelData.EntityName, ooRelData.RecordData);

						if (_publishEvents)
						{
							await _publishEndpoint.Publish(new RecordUpdatedEvent
							{
								EntityName = ooRelData.EntityName,
								NewRecord = ooRecord,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});
						}
					}

					foreach (var omRelData in oneToManyRecordData)
					{
						EntityRecord ooRecord = new EntityRecord();
						if (_publishEvents)
						{
							var data = (IEnumerable<KeyValuePair<string, object>>)omRelData.RecordData;
							foreach (var obj in data)
								ooRecord[obj.Key] = obj.Value;

							await _publishEndpoint.Publish(new PreRecordUpdateEvent
							{
								EntityName = omRelData.EntityName,
								Record = ooRecord,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});

							List<KeyValuePair<string, object>> recordData = new List<KeyValuePair<string, object>>();
							foreach (var property in ooRecord.Properties)
								recordData.Add(new KeyValuePair<string, object>(property.Key, property.Value));

							omRelData.RecordData = recordData;
						}

						recRepo.Update(omRelData.EntityName, omRelData.RecordData);

						if (_publishEvents)
						{
							await _publishEndpoint.Publish(new RecordUpdatedEvent
							{
								EntityName = omRelData.EntityName,
								NewRecord = ooRecord,
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});
						}
					}

					// Process M:N relations (lines 1531-1538)
					foreach (var mmRelData in manyToManyRecordData)
					{
						var mmResponse = CreateRelationManyToManyRecord(mmRelData.RelationId, mmRelData.OriginFieldValue, mmRelData.TargetFieldValue);

						if (!mmResponse.Success)
							throw new Exception(mmResponse.Message);
					}

					// Post-update event (replaces RecordHookManager.ExecutePostUpdateRecordHooks, lines 1545-1546)
					if (response.Object != null && response.Object.Data.Count > 0)
					{
						response.Message = "Record was updated successfully";

						if (_publishEvents)
						{
							await _publishEndpoint.Publish(new RecordUpdatedEvent
							{
								EntityName = entity.Name,
								OldRecord = oldRecord,
								NewRecord = response.Object.Data[0],
								Timestamp = DateTimeOffset.UtcNow,
								CorrelationId = Guid.NewGuid()
							});
						}
					}

					if (isTransactionActive)
						connection.CommitTransaction();

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

					if (ErpSettings.DevelopmentMode)
						response.Message = e.Message + e.StackTrace;
					else
						response.Message = "The entity record was not update. An internal error occurred!";

					return response;
				}
			}
		}

		#endregion

		#region << Record Delete >>

		/// <summary>
		/// Synchronous wrapper for <see cref="DeleteRecordAsync(string, Guid)"/>.
		/// </summary>
		public QueryResponse DeleteRecord(string entityName, Guid id)
			=> DeleteRecordAsync(entityName, id).GetAwaiter().GetResult();

		/// <summary>
		/// Deletes a record by entity name and record ID.
		/// Preserved from monolith RecordManager.DeleteRecord(string, Guid) (lines 1579-1607).
		/// </summary>
		public async Task<QueryResponse> DeleteRecordAsync(string entityName, Guid id)
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

			return await DeleteRecordCoreAsync(entity, id);
		}

		/// <summary>
		/// Core record deletion implementation. Preserves the complete business logic from
		/// the monolith RecordManager.DeleteRecord(Entity, Guid) (lines 1627-1734):
		/// - Permission checks
		/// - Retrieves existing record before deletion
		/// - File field cleanup via DbFileRepository (lines 1683-1701)
		/// - Pre/post-delete event publishing (replaces hook execution)
		/// </summary>
		private async Task<QueryResponse> DeleteRecordCoreAsync(Entity entity, Guid id)
		{
			QueryResponse response = new QueryResponse();
			response.Object = null;
			response.Success = true;
			response.Timestamp = DateTime.UtcNow;

			try
			{
				if (entity == null)
				{
					response.Errors.Add(new ErrorModel { Message = "Invalid entity name." });
					response.Success = false;
					return response;
				}

				if (!_ignoreSecurity)
				{
					bool hasPermisstion = SecurityContext.HasEntityPermission(EntityPermission.Delete, entity);
					if (!hasPermisstion)
					{
						response.StatusCode = HttpStatusCode.Forbidden;
						response.Success = false;
						response.Message = "Trying to delete record in entity '" + entity.Name + "' with no delete access.";
						response.Errors.Add(new ErrorModel { Message = "Access denied." });
						return response;
					}
				}

				List<KeyValuePair<string, object>> storageRecordData = new List<KeyValuePair<string, object>>();

				var query = EntityQuery.QueryEQ("id", id);
				var entityQuery = new EntityQuery(entity.Name, "*", query);

				response = Find(entityQuery);
				if (response.Object != null && response.Object.Data.Count == 1)
				{
					// Pre-delete event (replaces RecordHookManager.ExecutePreDeleteRecordHooks, lines 1667-1671)
					if (_publishEvents)
					{
						await _publishEndpoint.Publish(new PreRecordDeleteEvent
						{
							EntityName = entity.Name,
							Record = response.Object.Data[0],
							Timestamp = DateTimeOffset.UtcNow,
							CorrelationId = Guid.NewGuid()
						});
					}

					#region <--- check if entity has any file fields and delete files related to this record --->

					var entityObj = _entityManager.ReadEntities().Object.Single(x => x.Name == entity.Name);
					var fileFields = entityObj.Fields.Where(x => x.GetFieldType() == FieldType.FileField).ToList();
					var record = response.Object.Data[0];

					var filesToDelete = new List<string>();
					foreach (var fileField in fileFields)
					{
						if (!string.IsNullOrWhiteSpace((string)record[fileField.Name]))
							filesToDelete.Add((string)record[fileField.Name]);
					}

					if (filesToDelete.Any())
					{
						foreach (var filepath in filesToDelete)
							_fileRepository.Delete(filepath);
					}

					#endregion

					_dbContext.RecordRepository.Delete(entity.Name, id);

					// Post-delete event (replaces RecordHookManager.ExecutePostDeleteRecordHooks, lines 1707-1708)
					if (_publishEvents)
					{
						await _publishEndpoint.Publish(new RecordDeletedEvent
						{
							EntityName = entity.Name,
							RecordId = id,
							Timestamp = DateTimeOffset.UtcNow,
							CorrelationId = Guid.NewGuid()
						});
					}
				}
				else
				{
					response.Success = false;
					response.Message = "Record was not found.";
					return response;
				}

				return response;
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Object = null;
				response.Timestamp = DateTime.UtcNow;

				if (ErpSettings.DevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The entity record was not update. An internal error occurred!";

				return response;
			}
		}

		#endregion

		#region << Record Query >>

		/// <summary>
		/// Executes an entity query and returns matching records.
		/// Includes entity existence check and read permission check.
		/// Preserved from monolith RecordManager.Find(EntityQuery) (lines 1736-1802).
		/// </summary>
		public QueryResponse Find(EntityQuery query)
		{
			QueryResponse response = new QueryResponse
			{
				Success = true,
				Message = "The query was successfully executed.",
				Timestamp = DateTime.UtcNow
			};

			try
			{
				var entity = GetEntity(query.EntityName);
				if (entity == null)
				{
					response.Success = false;
					response.Message = string.Format("The query is incorrect. Specified entity '{0}' does not exist.", query.EntityName);
					response.Object = null;
					response.Errors.Add(new ErrorModel { Message = response.Message });
					response.Timestamp = DateTime.UtcNow;
					return response;
				}

				if (!_ignoreSecurity)
				{
					bool hasPermisstion = SecurityContext.HasEntityPermission(EntityPermission.Read, entity);
					if (!hasPermisstion)
					{
						response.StatusCode = HttpStatusCode.Forbidden;
						response.Success = false;
						response.Message = "Trying to read records from entity '" + entity.Name + "'  with no read access.";
						response.Errors.Add(new ErrorModel { Message = "Access denied." });
						return response;
					}
				}

				var fields = _dbContext.RecordRepository.ExtractQueryFieldsMeta(query);
				var data = _dbContext.RecordRepository.Find(query);
				response.Object = new QueryResult { FieldsMeta = fields, Data = data };
			}
			catch (Exception ex)
			{
				response.Success = false;
				response.Message = "The query is incorrect and cannot be executed";
				response.Object = null;
				response.Errors.Add(new ErrorModel { Message = ex.Message });
				response.Timestamp = DateTime.UtcNow;
				return response;
			}

			return response;
		}

		/// <summary>
		/// Executes a count query and returns the count of matching records.
		/// Preserved from monolith RecordManager.Count(EntityQuery) (lines 1804-1855).
		/// </summary>
		public QueryCountResponse Count(EntityQuery query)
		{
			QueryCountResponse response = new QueryCountResponse
			{
				Success = true,
				Message = "The query was successfully executed.",
				Timestamp = DateTime.UtcNow
			};

			try
			{
				var entity = GetEntity(query.EntityName);
				if (entity == null)
				{
					response.Success = false;
					response.Message = string.Format("The query is incorrect. Specified entity '{0}' does not exist.", query.EntityName);
					response.Object = 0;
					response.Errors.Add(new ErrorModel { Message = response.Message });
					response.Timestamp = DateTime.UtcNow;
					return response;
				}

				List<Field> fields = _dbContext.RecordRepository.ExtractQueryFieldsMeta(query);
				response.Object = _dbContext.RecordRepository.Count(query);
			}
			catch (Exception ex)
			{
				response.Success = false;
				response.Message = "The query is incorrect and cannot be executed";
				response.Object = 0;
				response.Errors.Add(new ErrorModel { Message = ex.Message });
				response.Timestamp = DateTime.UtcNow;
				return response;
			}

			return response;
		}

		#endregion
	}
}
