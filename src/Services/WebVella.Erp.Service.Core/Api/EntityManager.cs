using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Utilities;
using WebVella.Erp.SharedKernel.Utilities.Dynamic;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Api
{
	public class EntityManager
	{
		internal static object lockObj = new object();

		private readonly CoreDbContext _dbContext;
		private readonly IConfiguration _configuration;

		/// <summary>
		/// Constructs a new EntityManager with required service dependencies.
		/// Replaces monolith pattern of DbContext.Current singleton with explicit injection.
		/// </summary>
		public EntityManager(CoreDbContext dbContext, IConfiguration configuration)
		{
			_dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		/// <summary>
		/// Helper property replacing static ErpSettings.DevelopmentMode with injected configuration.
		/// </summary>
		private bool IsDevelopmentMode =>
			string.Equals(_configuration["Settings:DevelopmentMode"], "true", StringComparison.OrdinalIgnoreCase);

		#region << Validation methods >>

		private List<ErrorModel> ValidateEntity(Entity entity, bool checkId = true)
		{
			List<ErrorModel> errorList = new List<ErrorModel>();

			if (entity.Id == Guid.Empty)
				errorList.Add(new ErrorModel("id", null, "Id is required!"));

			if (checkId)
			{
				//update
				if (entity.Id != Guid.Empty)
				{
					Entity verifiedEntity = ReadEntity(entity.Id).Object;

					if (verifiedEntity == null)
						errorList.Add(new ErrorModel("id", entity.Id.ToString(), "Entity with such Id does not exist!"));
				}
			}
			else
			{
				//create
			}

			errorList.AddRange(ValidationUtility.ValidateName(entity.Name));

			if (!string.IsNullOrWhiteSpace(entity.Name))
			{
				//Postgres column name width limit
				if (entity.Name.Length > 63)
					errorList.Add(new ErrorModel("name", entity.Name, "Entity name length exceeded. Should be up to 63 chars!"));

				Entity verifiedEntity = ReadEntity(entity.Name).Object;

				if (verifiedEntity != null && verifiedEntity.Id != entity.Id)
					errorList.Add(new ErrorModel("name", entity.Name, "Entity with such Name exists already!"));
			}

			errorList.AddRange(ValidationUtility.ValidateLabel(entity.Label));

			errorList.AddRange(ValidationUtility.ValidateLabelPlural(entity.LabelPlural));

			if (entity.RecordPermissions == null)
				entity.RecordPermissions = new RecordPermissions();

			if (entity.RecordPermissions.CanRead == null)
				entity.RecordPermissions.CanRead = new List<Guid>();

			if (entity.RecordPermissions.CanCreate == null)
				entity.RecordPermissions.CanCreate = new List<Guid>();

			if (entity.RecordPermissions.CanUpdate == null)
				entity.RecordPermissions.CanUpdate = new List<Guid>();

			if (entity.RecordPermissions.CanDelete == null)
				entity.RecordPermissions.CanDelete = new List<Guid>();

			if (string.IsNullOrWhiteSpace(entity.IconName))
				entity.IconName = "fa fa-database";

			return errorList;
		}

		private List<ErrorModel> ValidateFields(Guid entityId, List<InputField> fields, bool checkId = true)
		{
			List<ErrorModel> errorList = new List<ErrorModel>();

			Entity entity = ReadEntity(entityId).Object;

			if (fields.Count == 0)
			{
				errorList.Add(new ErrorModel("fields", null, "There should be at least one field!"));
				return errorList;
			}

			int primaryFieldCount = 0;

			foreach (var field in fields)
			{
				errorList.AddRange(ValidateField(entity, field, false));

				if (field is InputGuidField)
				{
					primaryFieldCount++;
				}

				//Postgres column name width limit
				if (field.Name.Length > 63)
					errorList.Add(new ErrorModel("name", field.Name, "Field name length exceeded. Should be up to 63 chars!"));
			}

			if (primaryFieldCount < 1)
				errorList.Add(new ErrorModel("fields.id", null, "Must have one unique identifier field!"));

			if (primaryFieldCount > 1)
				errorList.Add(new ErrorModel("fields.id", null, "Too many primary fields. Must have only one unique identifier!"));

			return errorList;
		}

		private List<ErrorModel> ValidateField(Entity entity, InputField field, bool checkId = true)
		{
			List<ErrorModel> errorList = new List<ErrorModel>();

			if (field.Id == Guid.Empty)
				errorList.Add(new ErrorModel("id", null, "Id is required!"));

			int fieldsSameIdCount = entity.Fields.Where(f => f.Id == field.Id).Count();

			if ((checkId && fieldsSameIdCount > 1) || (!checkId && fieldsSameIdCount > 0))
				errorList.Add(new ErrorModel("id", null, "There is already a field with such Id!"));

			int fieldsSameNameCount = entity.Fields.Where(f => f.Name == field.Name).Count();

			if ((checkId && fieldsSameNameCount > 1) || (!checkId && fieldsSameNameCount > 0))
				errorList.Add(new ErrorModel("name", null, "There is already a field with such Name!"));

			errorList.AddRange(ValidationUtility.ValidateName(field.Name));

			errorList.AddRange(ValidationUtility.ValidateLabel(field.Label));

			if (field is InputAutoNumberField)
			{
				if (field.Required.HasValue && field.Required.Value && !((InputAutoNumberField)field).DefaultValue.HasValue)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));
			}
			else if (field is InputCheckboxField)
			{
				if (!((InputCheckboxField)field).DefaultValue.HasValue)
					((InputCheckboxField)field).DefaultValue = false;
			}
			else if (field is InputCurrencyField)
			{
				if (field.Required.HasValue && field.Required.Value && !((InputCurrencyField)field).DefaultValue.HasValue)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));
			}
			else if (field is InputDateField)
			{
				if (string.IsNullOrWhiteSpace(((InputDateField)field).Format))
					errorList.Add(new ErrorModel("format", null, "Date format is required!"));

				if (!((InputDateField)field).UseCurrentTimeAsDefaultValue.HasValue)
					((InputDateField)field).UseCurrentTimeAsDefaultValue = false;

				if ((((InputDateField)field).Required.HasValue && ((InputDateField)field).Required.Value) &&
				(!((InputDateField)field).UseCurrentTimeAsDefaultValue.HasValue || !((InputDateField)field).UseCurrentTimeAsDefaultValue.Value) &&
				((InputDateField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required when the field is marked as required and generate new id option is not selected!"));
			}
			else if (field is InputDateTimeField)
			{
				if (string.IsNullOrWhiteSpace(((InputDateTimeField)field).Format))
					errorList.Add(new ErrorModel("format", null, "Datetime format is required!"));

				if (!((InputDateTimeField)field).UseCurrentTimeAsDefaultValue.HasValue)
					((InputDateTimeField)field).UseCurrentTimeAsDefaultValue = false;

				if ((((InputDateTimeField)field).Required.HasValue && ((InputDateTimeField)field).Required.Value) &&
				(!((InputDateTimeField)field).UseCurrentTimeAsDefaultValue.HasValue || !((InputDateTimeField)field).UseCurrentTimeAsDefaultValue.Value) &&
				((InputDateTimeField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required when the field is marked as required and generate new id option is not selected!"));
			}
			else if (field is InputEmailField)
			{
				if (field.Required.HasValue && field.Required.Value && ((InputEmailField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));
			}
			else if (field is InputFileField)
			{
				if (field.Required.HasValue && field.Required.Value && ((InputFileField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));
			}
			else if (field is InputGeographyField)
			{
				if (field.Required.HasValue && field.Required.Value && ((InputGeographyField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));
			}
			else if (field is InputGuidField)
			{
				if ((((InputGuidField)field).Unique.HasValue && ((InputGuidField)field).Unique.Value) &&
					(!((InputGuidField)field).GenerateNewId.HasValue || !((InputGuidField)field).GenerateNewId.Value))
					errorList.Add(new ErrorModel("defaultValue", null, "Generate New Id is required when the field is marked as unique!"));

				if ((((InputGuidField)field).Required.HasValue && ((InputGuidField)field).Required.Value) &&
					(!((InputGuidField)field).GenerateNewId.HasValue || !((InputGuidField)field).GenerateNewId.Value) &&
					((InputGuidField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required when the field is marked as required and generate new id option is not selected!"));
			}
			else if (field is InputHtmlField)
			{
				if (field.Required.HasValue && field.Required.Value && ((InputHtmlField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));
			}
			else if (field is InputImageField)
			{
				if (field.Required.HasValue && field.Required.Value && ((InputImageField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));
			}
			else if (field is InputMultiLineTextField)
			{
				if (field.Required.HasValue && field.Required.Value && ((InputMultiLineTextField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));
			}
			else if (field is InputMultiSelectField)
			{
				if (field.Required.HasValue && field.Required.Value &&
					(((InputMultiSelectField)field).DefaultValue == null || ((InputMultiSelectField)field).DefaultValue.Count() == 0))
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));

				if (((InputMultiSelectField)field).Options != null)
				{
					if (((InputMultiSelectField)field).Options.Count == 0)
						errorList.Add(new ErrorModel("options", null, "Options must contains at least one item!"));

					//Check if all values are unique
					var fieldValueHS = new HashSet<string>();
					foreach (var option in ((InputMultiSelectField)field).Options)
					{
						if (fieldValueHS.Contains(option.Value))
						{
							errorList.Add(new ErrorModel("options", null, "There are duplicated option values!"));
							break;
						}
						else
							fieldValueHS.Add(option.Value);
					}
				}
				else
					errorList.Add(new ErrorModel("options", null, "Options is required!"));
			}
			else if (field is InputNumberField)
			{
				if (field.Required.HasValue && field.Required.Value && !((InputNumberField)field).DefaultValue.HasValue)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));

				if (!((InputNumberField)field).DecimalPlaces.HasValue)
					((InputNumberField)field).DecimalPlaces = 2;
			}
			else if (field is InputPasswordField)
			{
				if (!((InputPasswordField)field).Encrypted.HasValue)
					((InputPasswordField)field).Encrypted = true;
			}
			else if (field is InputPercentField)
			{
				if (field.Required.HasValue && field.Required.Value && !((InputPercentField)field).DefaultValue.HasValue)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));

				if (!((InputPercentField)field).DecimalPlaces.HasValue)
					((InputPercentField)field).DecimalPlaces = 2;
			}
			else if (field is InputPhoneField)
			{
				if (field.Required.HasValue && field.Required.Value && ((InputPhoneField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));
			}
			else if (field is InputSelectField)
			{
				if (field.Required.HasValue && field.Required.Value && string.IsNullOrWhiteSpace(((InputSelectField)field).DefaultValue))
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));

				if (((InputSelectField)field).Options != null)
				{
					if (((InputSelectField)field).Options.Count == 0)
						errorList.Add(new ErrorModel("options", null, "Options must contains at least one item!"));

					//Check if all values are unique
					var fieldValueHS = new HashSet<string>();
					foreach (var option in ((InputSelectField)field).Options)
					{
						if (fieldValueHS.Contains(option.Value))
						{
							errorList.Add(new ErrorModel("options", null, "There are duplicated option values!"));
							break;
						}
						else
							fieldValueHS.Add(option.Value);
					}
				}
				else
					errorList.Add(new ErrorModel("options", null, "Options is required!"));
			}
			else if (field is InputTextField)
			{
				if (field.Required.HasValue && field.Required.Value && ((InputTextField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));
			}
			else if (field is InputUrlField)
			{
				if (field.Required.HasValue && field.Required.Value && ((InputUrlField)field).DefaultValue == null)
					errorList.Add(new ErrorModel("defaultValue", null, "Default Value is required!"));

				if (!((InputUrlField)field).OpenTargetInNewWindow.HasValue)
					((InputUrlField)field).OpenTargetInNewWindow = false;
			}

			return errorList;
		}

		#endregion

		#region << Entity methods >>

		public EntityResponse CreateEntity(InputEntity inputEntity, bool createOnlyIdField = true, bool checkPermissions = true)
		{
			if (!string.IsNullOrWhiteSpace(inputEntity.Name))
			{
				inputEntity.Name = inputEntity.Name.Trim();
			}

			EntityResponse response = new EntityResponse
			{
				Success = true,
				Message = "The entity was successfully created!",
			};

			if (checkPermissions)
			{
				bool hasPermisstion = SecurityContext.HasMetaPermission();
				if (!hasPermisstion)
				{
					response.StatusCode = HttpStatusCode.Forbidden;
					response.Success = false;
					response.Message = "No permissions to manipulate erp meta.";
					response.Errors.Add(new ErrorModel { Message = "Access denied." });
					return response;
				}
			}

			//in order to support external IDs (while import in example)
			//we generate new ID only when it is not specified
			if (!inputEntity.Id.HasValue)
				inputEntity.Id = Guid.NewGuid();

			Entity entity = inputEntity.MapTo<Entity>();

			try
			{
				response.Object = entity;

				response.Errors = ValidateEntity(entity, false);

				if (response.Errors.Count > 0)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The entity was not created. Validation error occurred!";
					return response;
				}

				entity.Fields = BuildDefaultFields(createOnlyIdField);

				DbEntity storageEntity = entity.MapTo<DbEntity>();
				bool result = _dbContext.EntityRepository.Create(storageEntity);
				if (!result)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The entity was not created! An internal error occurred!";
					return response;
				}
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Object = entity;
				response.Timestamp = DateTime.UtcNow;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The entity was not created. An internal error occurred!";

				return response;
			}

			Cache.Clear();

			var createdEntityResponse = ReadEntity(entity.Id);
			response.Object = createdEntityResponse.Object;
			response.Timestamp = DateTime.UtcNow;

			return response;
		}

		public EntityResponse CreateEntity(Entity entity, bool checkPermissions = true)
		{
			InputEntity inputEntity = entity.MapTo<InputEntity>();
			return CreateEntity(inputEntity, createOnlyIdField: true, checkPermissions: checkPermissions);
		}

		public EntityResponse PartialUpdateEntity(Guid id, InputEntity inputEntity)
		{
			// Ensure the inputEntity has the correct ID
			inputEntity.Id = id;

			EntityResponse response = new EntityResponse
			{
				Success = true,
				Message = "The entity was successfully updated!",
			};

			bool hasPermisstion = SecurityContext.HasMetaPermission();
			if (!hasPermisstion)
			{
				response.StatusCode = HttpStatusCode.Forbidden;
				response.Success = false;
				response.Message = "No permissions to manipulate erp meta.";
				response.Errors.Add(new ErrorModel { Message = "Access denied." });
				return response;
			}

			Entity entity = inputEntity.MapTo<Entity>();

			try
			{
				response.Object = entity;
				response.Errors = ValidateEntity(entity, true);

				if (response.Errors.Count > 0)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The entity was not updated. Validation error occurred!";
					return response;
				}

				Entity storageEntity = ReadEntity(entity.Id).Object;

				storageEntity.Label = entity.Label;
				storageEntity.LabelPlural = entity.LabelPlural;
				storageEntity.System = entity.System;
				storageEntity.IconName = entity.IconName;
				storageEntity.Color = entity.Color;
				storageEntity.RecordScreenIdField = entity.RecordScreenIdField;
				storageEntity.RecordPermissions.CanRead = entity.RecordPermissions.CanRead;
				storageEntity.RecordPermissions.CanCreate = entity.RecordPermissions.CanCreate;
				storageEntity.RecordPermissions.CanUpdate = entity.RecordPermissions.CanUpdate;
				storageEntity.RecordPermissions.CanDelete = entity.RecordPermissions.CanDelete;

				bool result = _dbContext.EntityRepository.Update(storageEntity.MapTo<DbEntity>());

				if (!result)
				{
					Cache.Clear();
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The entity was not updated! An internal error occurred!";
					return response;
				}

			}
			catch (Exception e)
			{
				Cache.Clear();
				response.Success = false;
				response.Object = entity;
				response.Timestamp = DateTime.UtcNow;
				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The entity was not updated. An internal error occurred!";
				return response;
			}

			Cache.Clear();

			var updatedEntityResponse = ReadEntity(entity.Id);
			response.Object = updatedEntityResponse.Object;
			response.Timestamp = DateTime.UtcNow;

			return response;
		}

		public EntityResponse DeleteEntity(Guid id)
		{
			EntityResponse response = new EntityResponse
			{
				Success = true,
				Message = "The entity was successfully deleted!",
			};

			bool hasPermisstion = SecurityContext.HasMetaPermission();

			try
			{
				var entityResponse = ReadEntity(id);

				if (!entityResponse.Success)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = entityResponse.Message;
					return response;
				}
				else if (entityResponse.Object == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The entity was not deleted. Validation error occurred!";
					response.Errors.Add(new ErrorModel("id", id.ToString(), "Entity with such Id does not exist!"));
					return response;
				}

				if (!hasPermisstion)
				{
					response.StatusCode = HttpStatusCode.Forbidden;
					response.Success = false;
					response.Message = "No permissions to manipulate erp meta.";
					response.Errors.Add(new ErrorModel { Message = "Access denied." });
					return response;
				}

				//entity, entity records and relations are deleted in storage repository 
				_dbContext.EntityRepository.Delete(id);
			}
			catch (Exception e)
			{
				Cache.Clear();

				response.Timestamp = DateTime.UtcNow;
				response.Success = false;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The entity was not deleted. An internal error occurred!";

				return response;
			}

			Cache.Clear();

			response.Timestamp = DateTime.UtcNow;
			return response;
		}

		public EntityListResponse ReadEntities()
		{
			EntityListResponse response = new EntityListResponse
			{
				Success = true,
				Message = "The entity was successfully returned!",
			};

			//try return from cache			
			var entities = Cache.GetEntities();
			if (entities != null)
			{
				response.Object = entities;
				response.Hash = Cache.GetEntitiesHash();
				return response;
			}

			try
			{
				lock (lockObj)
				{
					List<DbEntity> storageEntityList = _dbContext.EntityRepository.Read();
					entities = storageEntityList.MapTo<Entity>();

					// EntityRelationManager is in same namespace, create inline with injected dependencies
					List<EntityRelation> relationList = new EntityRelationManager(_dbContext, _configuration).Read().Object;

					List<Field> fields = new List<Field>();

					foreach (var entity in entities)
						fields.AddRange(entity.Fields);

					foreach (var entity in entities)
					{
						#region Process Fields

						foreach (var field in entity.Fields)
						{
							field.EntityName = entity.Name;
						}

						#endregion

						//compute hash code
						entity.Hash = CryptoUtility.ComputeOddMD5Hash(JsonConvert.SerializeObject(entity));
					}

					Cache.AddEntities(entities);
					response.Object = entities;
					response.Hash = Cache.GetEntitiesHash();
				}
			}
			catch (Exception e)
			{
				response.Timestamp = DateTime.UtcNow;
				response.Success = false;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "An internal error occurred!";

				return response;
			}

			response.Timestamp = DateTime.Now;

			return response;
		}

		public EntityResponse ReadEntity(Guid id)
		{
			EntityResponse response = new EntityResponse
			{
				Success = true,
				Message = "The entity was successfully returned!",
				Timestamp = DateTime.UtcNow
			};

			try
			{
				EntityListResponse entityListResponse = ReadEntities();

				if (entityListResponse != null && entityListResponse.Object != null)
				{
					List<Entity> entities = entityListResponse.Object;

					Entity entity = entities.FirstOrDefault(e => e.Id == id);
					if (entity != null)
						response.Object = entity;
				}
			}
			catch (Exception e)
			{
				response.Timestamp = DateTime.UtcNow;
				response.Success = false;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "An internal error occurred!";

				return response;
			}

			response.Timestamp = DateTime.Now;

			return response;
		}

		public EntityResponse ReadEntity(string name)
		{
			EntityResponse response = new EntityResponse
			{
				Success = true,
				Message = "The entity was successfully returned!",
				Timestamp = DateTime.UtcNow
			};

			try
			{
				EntityListResponse entityListResponse = ReadEntities();

				if (entityListResponse != null && entityListResponse.Object != null)
				{
					List<Entity> entities = entityListResponse.Object;

					Entity entity = entities.FirstOrDefault(e => e.Name == name);
					if (entity != null)
						response.Object = entity;
				}
			}
			catch (Exception e)
			{
				response.Timestamp = DateTime.UtcNow;
				response.Success = false;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "An internal error occurred!";

				return response;
			}

			response.Timestamp = DateTime.Now;

			return response;
		}

		public EntityResponse CloneEntity(Guid entityToCloneId, string name, string label, string labelPlural)
		{
			InputEntity inputEntity = new InputEntity();
			inputEntity.Id = Guid.NewGuid();
			inputEntity.Name = name;
			inputEntity.Label = label;
			inputEntity.LabelPlural = labelPlural;

			EntityResponse response = new EntityResponse { Success = true, Message = "The entity was successfully cloned!", };
			using (DbConnection connection = _dbContext.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();

					var entityToClone = ReadEntity(entityToCloneId).Object;
					EntityResponse createResponse = CreateEntity(inputEntity);
					if (!createResponse.Success)
					{
						connection.RollbackTransaction();
						return createResponse;
					}

					var entity = createResponse.Object;

					foreach (var field in entityToClone.Fields)
					{
						if (field.Name == "id")
							continue;

						var inputField = field.MapTo<InputField>();
						inputField.Id = Guid.NewGuid();
						var fieldResponse = CreateField(entity.Id, inputField, true);
						if (!fieldResponse.Success)
						{
							connection.RollbackTransaction();
							response.Errors = fieldResponse.Errors;
							response.Success = false;
							response.Object = inputEntity.MapTo<Entity>();
							response.Timestamp = DateTime.UtcNow;
							response.Message = fieldResponse.Message;
							return response;
						}
					}

					connection.CommitTransaction();
				}
				catch (ValidationException valEx)
				{
					connection.RollbackTransaction();

					response.Success = false;
					response.Object = inputEntity.MapTo<Entity>();
					response.Timestamp = DateTime.UtcNow;
					response.Message = valEx.Message;
					response.Errors = valEx.Errors.MapTo<ErrorModel>();
					return response;
				}
				catch (Exception e)
				{
					connection.RollbackTransaction();

					response.Success = false;
					response.Object = inputEntity.MapTo<Entity>();
					response.Timestamp = DateTime.UtcNow;

					if (IsDevelopmentMode)
						response.Message = e.Message + e.StackTrace;
					else
						response.Message = "The entity was not created. An internal error occurred!";

					return response;
				}
			}

			var createdEntityResponse = ReadEntity(inputEntity.Id.Value);
			response.Object = createdEntityResponse.Object;
			response.Timestamp = DateTime.UtcNow;

			return response;
		}

		#endregion

		#region << Field methods >>

		public FieldResponse CreateField(Guid entityId, InputField inputField, bool transactional = true)
		{
			if (!string.IsNullOrWhiteSpace(inputField.Name))
			{
				inputField.Name = inputField.Name.Trim();
			}
			FieldResponse response = new FieldResponse
			{
				Success = true,
				Message = "The field was successfully created!",
			};

			bool hasPermisstion = SecurityContext.HasMetaPermission();
			if (!hasPermisstion)
			{
				response.StatusCode = HttpStatusCode.Forbidden;
				response.Success = false;
				response.Message = "User have no permissions to manipulate erp meta.";
				response.Errors.Add(new ErrorModel { Message = "Access denied." });
				return response;
			}

			Field field = null;

			try
			{
				var entityResponse = ReadEntity(entityId);

				if (!entityResponse.Success)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = entityResponse.Message;
					return response;
				}
				else if (entityResponse.Object == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "Entity with such Id does not exist!";
					return response;
				}
				Entity entity = entityResponse.Object;

				if (inputField.Id == null || inputField.Id == Guid.Empty)
					inputField.Id = Guid.NewGuid();

				response.Errors = ValidateField(entity, inputField, false);

				field = inputField.MapTo<Field>();

				if (response.Errors.Count > 0)
				{
					response.Object = field;
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The field was not created. Validation error occurred!";
					return response;
				}

				entity.Fields.Add(field);

				DbEntity editedEntity = entity.MapTo<DbEntity>();

				using (DbConnection con = _dbContext.CreateConnection())
				{
					con.BeginTransaction();

					try
					{
						bool result = _dbContext.EntityRepository.Update(editedEntity);
						if (!result)
						{
							response.Timestamp = DateTime.UtcNow;
							response.Success = false;
							response.Message = "The field was not created! An internal error occurred!";
							return response;
						}

						_dbContext.RecordRepository.CreateRecordField(entity.Name, field);

						con.CommitTransaction();
					}
					catch
					{
						con.RollbackTransaction();
						Cache.Clear();
						throw;
					}
				}

			}
			catch (Exception e)
			{
				Debug.WriteLine($"Error while creating field (before clear cache): {field?.Name} for entity '{entityId}'");
				Cache.Clear();

				response.Success = false;
				response.Object = field;
				response.Timestamp = DateTime.UtcNow;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The field was not created. An internal error occurred!";

				return response;
			}

			Debug.WriteLine($"Creating field success (before clear cache): {field.Name} for entity '{entityId}'");
			Cache.Clear();

			response.Object = field;
			response.Timestamp = DateTime.UtcNow;

			return response;
		}

		public FieldResponse CreateField(Guid entityId, FieldType type, Expando data, string name, string label, Guid? id = null,
					string placeholderText = "", string helpText = "", string description = "",
					bool system = false, bool required = false, bool unique = false, bool searchable = false, bool auditable = false,
					bool transactional = true)
		{
			Field field = null;

			if (data == null)
				data = new Expando();

			if (!string.IsNullOrWhiteSpace(name))
			{
				name = name.Trim();
			}

			switch (type)
			{
				case FieldType.AutoNumberField:
					field = new AutoNumberField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((AutoNumberField)field).DefaultValue = (decimal?)data["defaultValue"];
					if (HasKey(data, "startingNumber") && data["startingNumber"] != null)
						((AutoNumberField)field).StartingNumber = (decimal?)data["startingNumber"];
					if (HasKey(data, "displayFormat") && data["displayFormat"] != null)
						((AutoNumberField)field).DisplayFormat = (string)data["displayFormat"];
					break;
				case FieldType.CheckboxField:
					field = new CheckboxField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((CheckboxField)field).DefaultValue = (bool?)data["defaultValue"] ?? false;
					break;
				case FieldType.CurrencyField:
					field = new CurrencyField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((CurrencyField)field).DefaultValue = (decimal?)data["defaultValue"];
					if (HasKey(data, "minValue") && data["minValue"] != null)
						((CurrencyField)field).MinValue = (decimal?)data["minValue"];
					if (HasKey(data, "maxValue") && data["maxValue"] != null)
						((CurrencyField)field).MaxValue = (decimal?)data["maxValue"];
					if (HasKey(data, "currency") && data["currency"] != null)
					{
						((CurrencyField)field).Currency = (CurrencyType)data["currency"];
					}
					else
					{
						((CurrencyField)field).Currency = new CurrencyType();
						((CurrencyField)field).Currency.Code = "USD";
						((CurrencyField)field).Currency.DecimalDigits = 2;
						((CurrencyField)field).Currency.Name = "US dollar";
						((CurrencyField)field).Currency.NamePlural = "US dollars";
						((CurrencyField)field).Currency.Rounding = 0;
						((CurrencyField)field).Currency.Symbol = "$";
						((CurrencyField)field).Currency.SymbolNative = "$";
						((CurrencyField)field).Currency.SymbolPlacement = CurrencySymbolPlacement.Before;
						((CurrencyField)field).DefaultValue = 1;
					}
					break;
				case FieldType.DateField:
					field = new DateField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((DateField)field).DefaultValue = (DateTime?)data["defaultValue"];
					if (HasKey(data, "format") && data["format"] != null)
						((DateField)field).Format = (string)data["format"];
					if (HasKey(data, "useCurrentTimeAsDefaultValue") && data["useCurrentTimeAsDefaultValue"] != null)
						((DateField)field).UseCurrentTimeAsDefaultValue = (bool?)data["useCurrentTimeAsDefaultValue"];
					break;
				case FieldType.DateTimeField:
					field = new DateTimeField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((DateTimeField)field).DefaultValue = (DateTime?)data["defaultValue"];
					if (HasKey(data, "format") && data["format"] != null)
						((DateTimeField)field).Format = (string)data["format"];
					if (HasKey(data, "useCurrentTimeAsDefaultValue") && data["useCurrentTimeAsDefaultValue"] != null)
						((DateTimeField)field).UseCurrentTimeAsDefaultValue = (bool?)data["useCurrentTimeAsDefaultValue"];
					break;
				case FieldType.EmailField:
					field = new EmailField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((EmailField)field).DefaultValue = (string)data["defaultValue"];
					if (HasKey(data, "maxLength") && data["maxLength"] != null)
						((EmailField)field).MaxLength = (int?)data["maxLength"];
					break;
				case FieldType.FileField:
					field = new FileField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((FileField)field).DefaultValue = (string)data["defaultValue"];
					break;
				case FieldType.GuidField:
					field = new GuidField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((GuidField)field).DefaultValue = (Guid?)data["defaultValue"];
					if (HasKey(data, "generateNewId") && data["generateNewId"] != null)
						((GuidField)field).GenerateNewId = (bool?)data["generateNewId"];
					break;
				case FieldType.HtmlField:
					field = new HtmlField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((HtmlField)field).DefaultValue = (string)data["defaultValue"];
					break;
				case FieldType.ImageField:
					field = new ImageField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((ImageField)field).DefaultValue = (string)data["defaultValue"];
					break;
				case FieldType.MultiLineTextField:
					field = new MultiLineTextField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((MultiLineTextField)field).DefaultValue = (string)data["defaultValue"];
					if (HasKey(data, "maxLength") && data["maxLength"] != null)
						((MultiLineTextField)field).MaxLength = (int?)data["maxLength"];
					if (HasKey(data, "visibleLineNumber") && data["visibleLineNumber"] != null)
						((MultiLineTextField)field).VisibleLineNumber = (int?)data["visibleLineNumber"];
					break;
				case FieldType.GeographyField:
					field = new GeographyField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((GeographyField)field).DefaultValue = (string)data["defaultValue"];
					if (HasKey(data, "maxLength") && data["maxLength"] != null)
						((GeographyField)field).MaxLength = (int?)data["maxLength"];
					if (HasKey(data, "visibleLineNumber") && data["visibleLineNumber"] != null)
						((GeographyField)field).VisibleLineNumber = (int?)data["visibleLineNumber"];
					if (HasKey(data, "format") && data["format"] != null)
						((GeographyField)field).Format = (GeographyFieldFormat)data["format"];
					if (HasKey(data, "srid") && data["srid"] != null)
						((GeographyField)field).SRID = (int)data["srid"];
					break;
				case FieldType.MultiSelectField:
					field = new MultiSelectField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((MultiSelectField)field).DefaultValue = (IEnumerable<string>)data["defaultValue"];
					if (HasKey(data, "options") && data["options"] != null)
						((MultiSelectField)field).Options = (List<SelectOption>)data["options"];
					break;
				case FieldType.NumberField:
					field = new NumberField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((NumberField)field).DefaultValue = (int?)data["defaultValue"];
					if (HasKey(data, "minValue") && data["minValue"] != null)
						((NumberField)field).MinValue = (decimal?)data["minValue"];
					if (HasKey(data, "maxValue") && data["maxValue"] != null)
						((NumberField)field).MaxValue = (decimal?)data["maxValue"];
					if (HasKey(data, "decimalPlaces") && data["decimalPlaces"] != null)
						((NumberField)field).DecimalPlaces = (byte?)data["decimalPlaces"];
					break;
				case FieldType.PasswordField:
					field = new PasswordField();
					if (HasKey(data, "maxLength") && data["maxLength"] != null)
						((PasswordField)field).MaxLength = (int?)data["maxLength"];
					if (HasKey(data, "minLength") && data["minLength"] != null)
						((PasswordField)field).MinLength = (int?)data["minLength"];
					if (HasKey(data, "encrypted") && data["encrypted"] != null)
						((PasswordField)field).Encrypted = (bool?)data["encrypted"];
					break;
				case FieldType.PercentField:
					field = new PercentField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((PercentField)field).DefaultValue = (decimal?)data["defaultValue"];
					if (HasKey(data, "minValue") && data["minValue"] != null)
						((PercentField)field).MinValue = (decimal?)data["minValue"];
					if (HasKey(data, "maxValue") && data["maxValue"] != null)
						((PercentField)field).MaxValue = (decimal?)data["maxValue"];
					if (HasKey(data, "decimalPlaces") && data["decimalPlaces"] != null)
						((PercentField)field).DecimalPlaces = (byte?)data["decimalPlaces"];
					break;
				case FieldType.PhoneField:
					field = new PhoneField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((PhoneField)field).DefaultValue = (string)data["defaultValue"];
					if (HasKey(data, "format") && data["format"] != null)
						((PhoneField)field).Format = (string)data["format"];
					if (HasKey(data, "maxLength") && data["maxLength"] != null)
						((PhoneField)field).DefaultValue = (string)data["maxLength"];
					break;
				case FieldType.SelectField:
					field = new SelectField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((SelectField)field).DefaultValue = (string)data["defaultValue"];
					if (HasKey(data, "options") && data["options"] != null)
						((SelectField)field).Options = (List<SelectOption>)data["options"];
					break;
				case FieldType.TextField:
					field = new TextField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((TextField)field).DefaultValue = (string)data["defaultValue"];
					if (HasKey(data, "maxLength") && data["maxLength"] != null)
						((TextField)field).MaxLength = (int?)data["maxLength"];
					break;
				case FieldType.UrlField:
					field = new UrlField();
					if (HasKey(data, "defaultValue") && data["defaultValue"] != null)
						((UrlField)field).DefaultValue = (string)data["defaultValue"];
					if (HasKey(data, "maxLength") && data["maxLength"] != null)
						((UrlField)field).MaxLength = (int?)data["maxLength"];
					if (HasKey(data, "openTargetInNewWindow") && data["openTargetInNewWindow"] != null)
						((UrlField)field).OpenTargetInNewWindow = (bool?)data["openTargetInNewWindow"];
					break;
				default:
					{
						FieldResponse response = new FieldResponse();
						response.Timestamp = DateTime.UtcNow;
						response.Success = false;
						response.Message = "Not supported field type!";
						response.Success = false;
						return response;
					}
			}

			field.Id = id.HasValue && id.Value != Guid.Empty ? id.Value : Guid.NewGuid();
			field.Name = name;
			field.Label = label;
			field.PlaceholderText = placeholderText;
			field.Description = description;
			field.HelpText = helpText;
			field.Required = required;
			field.Unique = unique;
			field.Searchable = searchable;
			field.Auditable = auditable;
			field.System = system;

			return CreateField(entityId, field.MapTo<InputField>(), transactional);
		}

		public FieldResponse UpdateField(Guid entityId, InputField inputField)
		{
			FieldResponse response = new FieldResponse();

			bool hasPermisstion = SecurityContext.HasMetaPermission();
			if (!hasPermisstion)
			{
				response.StatusCode = HttpStatusCode.Forbidden;
				response.Success = false;
				response.Message = "User have no permissions to manipulate erp meta.";
				response.Errors.Add(new ErrorModel { Message = "Access denied." });
				return response;
			}

			var entityResponse = ReadEntity(entityId);

			if (!entityResponse.Success)
			{
				response.Timestamp = DateTime.UtcNow;
				response.Success = false;
				response.Message = entityResponse.Message;
				return response;
			}
			else if (entityResponse.Object == null)
			{
				response.Timestamp = DateTime.UtcNow;
				response.Success = false;
				response.Message = "Entity with such Id does not exist!";
				return response;
			}
			Entity entity = entityResponse.Object;

			return UpdateField(entity, inputField);
		}

		public FieldResponse UpdateField(Entity entity, InputField inputField)
		{
			FieldResponse response = new FieldResponse
			{
				Success = true,
				Message = "The field was successfully updated!",
			};

			bool hasPermisstion = SecurityContext.HasMetaPermission();
			if (!hasPermisstion)
			{
				response.StatusCode = HttpStatusCode.Forbidden;
				response.Success = false;
				response.Message = "User have no permissions to manipulate erp meta.";
				response.Errors.Add(new ErrorModel { Message = "Access denied." });
				return response;
			}

			Field field = null;

			try
			{
				response.Errors = ValidateField(entity, inputField, true);

				field = inputField.MapTo<Field>();

				if (field.GetFieldType() == FieldType.DateTimeField)
				{
					var dateTimeField = (DateTimeField)field;
					if (dateTimeField.UseCurrentTimeAsDefaultValue.HasValue && dateTimeField.UseCurrentTimeAsDefaultValue.Value)
						dateTimeField.DefaultValue = null;
				}
				if (field.GetFieldType() == FieldType.DateField)
				{
					var dateField = (DateField)field;
					if (dateField.UseCurrentTimeAsDefaultValue.HasValue && dateField.UseCurrentTimeAsDefaultValue.Value)
						dateField.DefaultValue = null;
				}

				if (response.Errors.Count > 0)
				{
					response.Object = field;
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The field was not updated. Validation error occurred!";
					return response;
				}

				Field fieldForDelete = entity.Fields.FirstOrDefault(f => f.Id == field.Id);
				if (fieldForDelete.Id == field.Id)
					entity.Fields.Remove(fieldForDelete);

				entity.Fields.Add(field);

				_dbContext.RecordRepository.UpdateRecordField(entity.Name, field);

				DbEntity updatedEntity = entity.MapTo<DbEntity>();
				bool result = _dbContext.EntityRepository.Update(updatedEntity);
				if (!result)
				{
					Cache.Clear();
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The field was not updated! An internal error occurred!";
					return response;
				}

			}
			catch (Exception e)
			{
				Cache.Clear();
				response.Success = false;
				response.Object = field;
				response.Timestamp = DateTime.UtcNow;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The field was not updated. An internal error occurred!";

				return response;
			}

			Cache.Clear();

			response.Object = field;
			response.Timestamp = DateTime.UtcNow;

			return response;
		}

		public FieldResponse DeleteField(Guid entityId, Guid id, bool transactional = true)
		{
			FieldResponse response = new FieldResponse
			{
				Success = true,
				Message = "The field was successfully deleted!",
			};

			bool hasPermisstion = SecurityContext.HasMetaPermission();
			if (!hasPermisstion)
			{
				response.StatusCode = HttpStatusCode.Forbidden;
				response.Success = false;
				response.Message = "User have no permissions to manipulate erp meta.";
				response.Errors.Add(new ErrorModel { Message = "Access denied." });
				return response;
			}

			try
			{
				var entityResponse = ReadEntity(entityId);

				if (!entityResponse.Success)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = entityResponse.Message;
					return response;
				}
				else if (entityResponse.Object == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "Entity with such Id does not exist!";
					return response;
				}
				Entity entity = entityResponse.Object;

				Field field = entity.Fields.FirstOrDefault(f => f.Id == id);

				if (field == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The field was not deleted. Validation error occurred!";
					response.Errors.Add(new ErrorModel("id", id.ToString(), "Field with such Id does not exist!"));
					return response;
				}

				//Validate if field is not included in any relation. Check by ID
				#region << validation check >>
				var entityList = ReadEntities().Object;
				var validationErrors = new List<ErrorModel>();

				//Check relations
				var relations = new EntityRelationManager(_dbContext, _configuration).Read().Object;

				foreach (var relation in relations)
				{
					if (relation.OriginFieldId == id)
					{
						var error = new ErrorModel();
						error.Key = "relation";
						error.Value = id.ToString();
						error.Message = "Field used as Origin field in relation: " + relation.Name;
						validationErrors.Add(error);
					}
					else if (relation.TargetFieldId == id)
					{
						var error = new ErrorModel();
						error.Key = "relation";
						error.Value = id.ToString();
						error.Message = "Field used as Target field in relation: " + relation.Name;
						validationErrors.Add(error);
					}
				}

				if (validationErrors.Count > 0)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "The field was not deleted. Validation error occurred!";
					response.Errors = validationErrors;
					return response;
				}
				#endregion

				entity.Fields.Remove(field);

				using (DbConnection con = _dbContext.CreateConnection())
				{
					con.BeginTransaction();
					try
					{
						_dbContext.RecordRepository.RemoveRecordField(entity.Name, field);

						DbEntity updatedEntity = entity.MapTo<DbEntity>();
						bool result = _dbContext.EntityRepository.Update(updatedEntity);
						if (!result)
						{
							response.Timestamp = DateTime.UtcNow;
							response.Success = false;
							response.Message = "The field was not updated! An internal error occurred!";
							return response;
						}

						con.CommitTransaction();
					}
					catch
					{
						con.RollbackTransaction();
						throw;
					}
				}
			}
			catch (Exception e)
			{
				Cache.Clear();
				response.Timestamp = DateTime.UtcNow;
				response.Success = false;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "The field was not deleted. An internal error occurred!";

				return response;
			}

			Cache.Clear();

			response.Timestamp = DateTime.UtcNow;
			return response;
		}

		public FieldListResponse ReadFields(Guid entityId)
		{
			FieldListResponse response = new FieldListResponse
			{
				Success = true,
				Message = "The field was successfully returned!",
			};

			try
			{
				var entityResponse = ReadEntity(entityId);

				if (!entityResponse.Success)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = entityResponse.Message;
					return response;
				}
				else if (entityResponse.Object == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "Entity with such Id does not exist!";
					return response;
				}
				Entity entity = entityResponse.Object;

				FieldList fieldList = new FieldList();
				fieldList.Fields = entity.Fields;

				response.Object = fieldList;
			}
			catch (Exception e)
			{
				response.Timestamp = DateTime.UtcNow;
				response.Success = false;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "An internal error occurred!";

				return response;
			}

			response.Timestamp = DateTime.Now;

			return response;
		}

		public FieldListResponse ReadFields()
		{
			FieldListResponse response = new FieldListResponse
			{
				Success = true,
				Message = "The field was successfully returned!",
			};

			try
			{
				var entitiesResponse = ReadEntities();
				if (!entitiesResponse.Success)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = entitiesResponse.Message;
					return response;
				}
				else if (entitiesResponse.Object == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "There is no entities into database!";
					return response;
				}
				List<Entity> entities = entitiesResponse.Object;

				FieldList fieldList = new FieldList();

				foreach (Entity entity in entities)
				{
					fieldList.Fields.AddRange(entity.Fields.MapTo<Field>());
				}

				response.Object = fieldList;
			}
			catch (Exception e)
			{
				response.Timestamp = DateTime.UtcNow;
				response.Success = false;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "An internal error occurred!";

				return response;
			}

			response.Timestamp = DateTime.Now;

			return response;
		}

		public FieldResponse ReadField(Guid entityId, Guid id)
		{
			FieldResponse response = new FieldResponse
			{
				Success = true,
				Message = "The field was successfully returned!",
			};

			try
			{
				var entityResponse = ReadEntity(entityId);

				if (!entityResponse.Success)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = entityResponse.Message;
					return response;
				}
				else if (entityResponse.Object == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "Entity with such Id does not exist!";
					return response;
				}
				Entity entity = entityResponse.Object;
				Field field = entity.Fields.FirstOrDefault(f => f.Id == id);

				if (field == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "Validation error occurred!";
					response.Errors.Add(new ErrorModel("id", id.ToString(), "Field with such Id does not exist!"));
					return response;
				}

				response.Object = field;
			}
			catch (Exception e)
			{
				response.Timestamp = DateTime.UtcNow;
				response.Success = false;

				if (IsDevelopmentMode)
					response.Message = e.Message + e.StackTrace;
				else
					response.Message = "An internal error occurred!";

				return response;
			}

			response.Timestamp = DateTime.Now;

			return response;
		}

		#endregion

		#region << Help methods >>

		/// <summary>
		/// Public entry point to create default fields for an already-persisted entity.
		/// Reads the entity by ID, creates default fields (id, created_by, last_modified_by,
		/// created_on, last_modified_on) via CreateField, and clears cache.
		/// </summary>
		public void CreateEntityDefaultFields(Guid entityId)
		{
			var defaultFields = BuildDefaultFields(false);
			foreach (var field in defaultFields)
			{
				if (field.Name == "id")
					continue; // id field is always created with the entity

				var inputField = field.MapTo<InputField>();
				CreateField(entityId, inputField, true);
			}
		}

		/// <summary>
		/// Internal helper that constructs in-memory Field objects for default entity fields.
		/// Used by CreateEntity to populate fields before persistence.
		/// When createOnlyIdField is true, only the primary key 'id' field is created.
		/// When false, all 5 default fields are created: id, created_by, last_modified_by, created_on, last_modified_on.
		/// </summary>
		private List<Field> BuildDefaultFields(bool createOnlyIdField = true)
		{
			List<Field> fields = new List<Field>();

			GuidField primaryKeyField = new GuidField();
			primaryKeyField.Id = Guid.NewGuid();
			primaryKeyField.Name = "id";
			primaryKeyField.Label = "Id";
			primaryKeyField.PlaceholderText = "";
			primaryKeyField.Description = "";
			primaryKeyField.HelpText = "";
			primaryKeyField.Required = true;
			primaryKeyField.Unique = true;
			primaryKeyField.Searchable = true;
			primaryKeyField.Auditable = false;
			primaryKeyField.System = true;
			primaryKeyField.DefaultValue = null;
			primaryKeyField.GenerateNewId = true;

			fields.Add(primaryKeyField);

			if (!createOnlyIdField)
			{
				GuidField createdBy = new GuidField();
				createdBy.Id = Guid.NewGuid();
				createdBy.Name = "created_by";
				createdBy.Label = "Created By";
				createdBy.PlaceholderText = "";
				createdBy.Description = "";
				createdBy.HelpText = "";
				createdBy.Required = false;
				createdBy.Unique = false;
				createdBy.Searchable = false;
				createdBy.Auditable = false;
				createdBy.System = true;
				createdBy.DefaultValue = null;
				createdBy.GenerateNewId = false;

				fields.Add(createdBy);

				GuidField lastModifiedBy = new GuidField();
				lastModifiedBy.Id = Guid.NewGuid();
				lastModifiedBy.Name = "last_modified_by";
				lastModifiedBy.Label = "Last Modified By";
				lastModifiedBy.PlaceholderText = "";
				lastModifiedBy.Description = "";
				lastModifiedBy.HelpText = "";
				lastModifiedBy.Required = false;
				lastModifiedBy.Unique = false;
				lastModifiedBy.Searchable = false;
				lastModifiedBy.Auditable = false;
				lastModifiedBy.System = true;
				lastModifiedBy.DefaultValue = null;
				lastModifiedBy.GenerateNewId = false;

				fields.Add(lastModifiedBy);

				DateTimeField createdOn = new DateTimeField();
				createdOn.Id = Guid.NewGuid();
				createdOn.Name = "created_on";
				createdOn.Label = "Created On";
				createdOn.PlaceholderText = "";
				createdOn.Description = "";
				createdOn.HelpText = "";
				createdOn.Required = false;
				createdOn.Unique = false;
				createdOn.Searchable = false;
				createdOn.Auditable = false;
				createdOn.System = true;
				createdOn.DefaultValue = null;
				createdOn.Format = "dd MMM yyyy HH:mm";
				createdOn.UseCurrentTimeAsDefaultValue = true;

				fields.Add(createdOn);

				DateTimeField modifiedOn = new DateTimeField();
				modifiedOn.Id = Guid.NewGuid();
				modifiedOn.Name = "last_modified_on";
				modifiedOn.Label = "Last Modified On";
				modifiedOn.PlaceholderText = "";
				modifiedOn.Description = "";
				modifiedOn.HelpText = "";
				modifiedOn.Required = false;
				modifiedOn.Unique = false;
				modifiedOn.Searchable = false;
				modifiedOn.Auditable = false;
				modifiedOn.System = true;
				modifiedOn.DefaultValue = null;
				modifiedOn.Format = "dd MMM yyyy HH:mm";
				modifiedOn.UseCurrentTimeAsDefaultValue = true;

				fields.Add(modifiedOn);
			}

			return fields;
		}

		public static EntityRecord ConvertToEntityRecord(object inputRecord)
		{
			EntityRecord record = new EntityRecord();

			foreach (var prop in inputRecord.GetType().GetProperties())
			{
				record[prop.Name] = prop.GetValue(inputRecord);
			}

			return record;
		}

		private static bool HasKey(Expando expando, string key)
		{
			return expando.GetProperties().Any(p => p.Key == key);
		}

		public Entity GetEntityByFieldId(Guid fieldId)
		{
			var entityResponse = ReadEntities();
			if (!entityResponse.Success || entityResponse.Object == null)
				return null;

			List<Entity> entities = entityResponse.Object;

			return GetEntityByFieldId(fieldId, entities);
		}

		public static Entity GetEntityByFieldId(Guid fieldId, List<Entity> entities)
		{
			foreach (var entity in entities)
			{
				if (entity.Fields.Any(v => v.Id == fieldId))
					return entity;
			}

			return null;
		}

		#endregion
	}
}
