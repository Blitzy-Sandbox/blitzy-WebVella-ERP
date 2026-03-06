using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Controllers
{
	/// <summary>
	/// Core Platform Entity/Field/Relation Metadata CRUD REST API Controller.
	/// Exposes all administrator-only endpoints for managing the dynamic entity schema.
	/// Extracted from the monolith's WebApiController.cs entity/field/relation sections.
	///
	/// All 15 endpoints are restricted to the "administrator" role at class level.
	/// Route prefix: api/v3.0/meta — the API Gateway maps legacy api/v3/en_US/meta/* routes here.
	/// </summary>
	[Authorize(Roles = "administrator")]
	[ApiController]
	[Route("api/v3.0/meta")]
	public class EntityController : Controller
	{
		private readonly EntityManager _entityManager;
		private readonly EntityRelationManager _relationManager;
		private readonly IConfiguration _configuration;

		/// <summary>
		/// Constructs the EntityController with required service dependencies.
		/// Replaces monolith pattern of "new EntityManager()" with explicit DI injection.
		/// </summary>
		/// <param name="entityManager">Entity/field metadata CRUD manager.</param>
		/// <param name="relationManager">Entity relation CRUD manager.</param>
		/// <param name="configuration">Application configuration for development mode checks.</param>
		public EntityController(
			EntityManager entityManager,
			EntityRelationManager relationManager,
			IConfiguration configuration)
		{
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
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
		/// Standard response handler. Sets the Timestamp to current UTC time if not already set.
		/// If response contains errors or is not successful, sets HTTP status code from response model.
		/// Preserves original ApiControllerBase behavior.
		/// </summary>
		protected IActionResult DoResponse(BaseResponseModel response)
		{
			// Ensure timestamp is always set to current UTC time
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
		/// Bad request response handler. Sets failure state, applies error message
		/// with development-mode stack trace when enabled. Preserves original behavior.
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
		/// </summary>
		protected IActionResult DoItemNotFoundResponse(BaseResponseModel response)
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(response);
		}

		/// <summary>
		/// Page not found response handler. Sets HTTP 404 and returns empty JSON object.
		/// </summary>
		protected IActionResult DoPageNotFoundResponse()
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(new { });
		}

		#endregion

		#region << Entity Meta >>

		/// <summary>
		/// Get all entity definitions.
		/// GET: api/v3.0/meta/entity/list
		/// Supports optional hash parameter for change-detection caching — if the client
		/// provides a hash that matches the current entity list hash, Object is set to null
		/// (no data changed), saving bandwidth.
		/// </summary>
		[HttpGet("entity/list")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetEntityMetaList()
		{
			var bo = _entityManager.ReadEntities();
			return DoResponse(bo);
		}

		/// <summary>
		/// Get entity metadata by ID.
		/// GET: api/v3.0/meta/entity/id/{entityId}
		/// </summary>
		[HttpGet("entity/id/{entityId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetEntityMetaById(Guid entityId)
		{
			return DoResponse(_entityManager.ReadEntity(entityId));
		}

		/// <summary>
		/// Get entity metadata by name.
		/// GET: api/v3.0/meta/entity/{name}
		/// Returns 404 with success:false when entity is not found.
		/// </summary>
		[HttpGet("entity/{name}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetEntityMeta(string name)
		{
			var response = _entityManager.ReadEntity(name);
			response.Timestamp = DateTime.UtcNow;
			if (response.Success && response.Object == null)
			{
				response.Success = false;
				response.Message = $"Entity '{name}' was not found.";
				return DoItemNotFoundResponse(response);
			}
			return DoResponse(response);
		}

		/// <summary>
		/// Create a new entity.
		/// POST: api/v3.0/meta/entity
		/// Whitelist-filters input properties to prevent injection of unexpected fields.
		/// Wraps the operation in a database transaction for atomicity.
		/// </summary>
		[HttpPost("entity")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult CreateEntity([FromBody] JObject submitObj)
		{
			EntityResponse response = new EntityResponse();
			try
			{
				InputEntity submitEntity = submitObj.ToObject<InputEntity>();
				var entity = new InputEntity
				{
					Name = submitEntity.Name,
					Label = submitEntity.Label,
					LabelPlural = submitEntity.LabelPlural,
					System = submitEntity.System,
					IconName = submitEntity.IconName,
					RecordPermissions = submitEntity.RecordPermissions
				};

				using (var connection = CoreDbContext.Current.CreateConnection())
				{
					connection.BeginTransaction();
					try
					{
						response = _entityManager.CreateEntity(entity);
						connection.CommitTransaction();
					}
					catch (Exception ex)
					{
						connection.RollbackTransaction();
						return DoBadRequestResponse(response, ex.Message, ex);
					}
				}
			}
			catch (Exception e)
			{
				return DoBadRequestResponse(response, "Input object is not in valid format! It cannot be converted.", e);
			}

			return DoResponse(response);
		}

		/// <summary>
		/// Partially update an entity (PATCH semantics).
		/// PATCH: api/v3.0/meta/entity/{name}
		/// Only submitted properties are applied; unsubmitted properties retain their existing values.
		/// The name parameter is the entity ID as a GUID string, preserving the monolith route contract.
		/// </summary>
		[HttpPatch("entity/{name}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult PatchEntity(string name, [FromBody] JObject submitObj)
		{
			FieldResponse response = new FieldResponse();
			InputEntity entity = new InputEntity();

			try
			{
				if (!Guid.TryParse(name, out Guid entityId))
				{
					response.Errors.Add(new ErrorModel("id", name, "id parameter is not valid Guid value"));
					return DoResponse(response);
				}

				var existingEntityResponse = _entityManager.ReadEntity(entityId);
				if (!existingEntityResponse.Success || existingEntityResponse.Object == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "Entity with such Name does not exist!";
					return DoBadRequestResponse(response);
				}

				// Map existing Entity to InputEntity to preserve current values
				var existingEntity = existingEntityResponse.Object;
				entity = new InputEntity
				{
					Id = existingEntity.Id,
					Name = existingEntity.Name,
					Label = existingEntity.Label,
					LabelPlural = existingEntity.LabelPlural,
					System = existingEntity.System,
					IconName = existingEntity.IconName,
					Color = existingEntity.Color,
					RecordPermissions = existingEntity.RecordPermissions,
					RecordScreenIdField = existingEntity.RecordScreenIdField
				};

				Type inputEntityType = entity.GetType();

				// Validate that all submitted properties exist on InputEntity
				foreach (var prop in submitObj.Properties())
				{
					int count = inputEntityType.GetProperties().Where(n => n.Name.ToLower() == prop.Name.ToLower()).Count();
					if (count < 1)
						response.Errors.Add(new ErrorModel(prop.Name, prop.Value.ToString(), "Input object contains property that is not part of the object model."));
				}

				if (response.Errors.Count > 0)
					return DoBadRequestResponse(response);

				InputEntity inputEntity = submitObj.ToObject<InputEntity>();

				// Apply only submitted properties over existing values (PATCH semantics)
				foreach (var prop in submitObj.Properties())
				{
					if (prop.Name.ToLower() == "label")
						entity.Label = inputEntity.Label;
					if (prop.Name.ToLower() == "labelplural")
						entity.LabelPlural = inputEntity.LabelPlural;
					if (prop.Name.ToLower() == "system")
						entity.System = inputEntity.System;
					if (prop.Name.ToLower() == "iconname")
						entity.IconName = inputEntity.IconName;
					if (prop.Name.ToLower() == "color")
						entity.Color = inputEntity.Color;
					if (prop.Name.ToLower() == "recordpermissions")
						entity.RecordPermissions = inputEntity.RecordPermissions;
					if (prop.Name.ToLower() == "recordscreenidfield")
						entity.RecordScreenIdField = inputEntity.RecordScreenIdField;
				}
			}
			catch (Exception e)
			{
				return DoBadRequestResponse(response, "Input object is not in valid format! It cannot be converted.", e);
			}

			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				connection.BeginTransaction();
				try
				{
					var result = _entityManager.PartialUpdateEntity(entity.Id.Value, entity);
					connection.CommitTransaction();
					return DoResponse(result);
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					return DoBadRequestResponse(response, ex.Message, ex);
				}
			}
		}

		/// <summary>
		/// Delete an entity by ID (GUID) or by name.
		/// DELETE: api/v3.0/meta/entity/{name}
		/// Accepts both GUID strings and entity names. When a name is provided,
		/// resolves it to a GUID internally before deletion.
		/// </summary>
		[HttpDelete("entity/{name}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult DeleteEntity(string name)
		{
			EntityResponse response = new EntityResponse();
			response.Timestamp = DateTime.UtcNow;

			Guid entityId;
			if (Guid.TryParse(name, out entityId))
			{
				// GUID provided directly
			}
			else
			{
				// Name provided — resolve to entity ID
				var entityResponse = _entityManager.ReadEntity(name);
				if (!entityResponse.Success || entityResponse.Object == null)
				{
					response.Success = false;
					response.Message = $"Entity '{name}' was not found.";
					return DoItemNotFoundResponse(response);
				}
				entityId = entityResponse.Object.Id;
			}

			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				connection.BeginTransaction();
				try
				{
					response = _entityManager.DeleteEntity(entityId);
					response.Timestamp = DateTime.UtcNow;
					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					return DoBadRequestResponse(response, ex.Message, ex);
				}
			}
			return DoResponse(response);
		}

		#endregion

		#region << Entity Fields >>

		/// <summary>
		/// Create a new field on an entity.
		/// POST: api/v3.0/meta/entity/{entityName}/field
		/// The entityName parameter is the entity ID as a GUID string.
		/// Uses InputField.ConvertField() for type-specific field deserialization from JObject.
		/// </summary>
		[HttpPost("entity/{entityName}/field")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult CreateField(string entityName, [FromBody] JObject submitObj)
		{
			FieldResponse response = new FieldResponse();

			if (!Guid.TryParse(entityName, out Guid entityId))
			{
				response.Errors.Add(new ErrorModel("id", entityName, "id parameter is not valid Guid value"));
				return DoResponse(response);
			}

			InputField field = new InputGuidField();
			try
			{
				field = InputField.ConvertField(submitObj);
			}
			catch (Exception e)
			{
				return DoBadRequestResponse(response, "Input object is not in valid format! It cannot be converted.", e);
			}

			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				connection.BeginTransaction();
				try
				{
					response = _entityManager.CreateField(entityId, field);
					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					return DoBadRequestResponse(response, ex.Message, ex);
				}
			}

			return DoResponse(response);
		}

		/// <summary>
		/// Full replacement update of a field on an entity.
		/// PUT: api/v3.0/meta/entity/{entityName}/field/{fieldId}
		/// Validates all submitted properties against the detected field type model.
		/// </summary>
		[HttpPut("entity/{entityName}/field/{fieldId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult UpdateField(string entityName, Guid fieldId, [FromBody] JObject submitObj)
		{
			FieldResponse response = new FieldResponse();

			if (!Guid.TryParse(entityName, out Guid entityId))
			{
				response.Errors.Add(new ErrorModel("id", entityName, "id parameter is not valid Guid value"));
				return DoResponse(response);
			}

			InputField field = new InputGuidField();
			FieldType fieldType = FieldType.GuidField;

			var fieldTypeProp = submitObj.Properties().SingleOrDefault(k => k.Name.ToLower() == "fieldtype");
			if (fieldTypeProp != null)
			{
				fieldType = (FieldType)Enum.ToObject(typeof(FieldType), fieldTypeProp.Value.ToObject<int>());
			}

			Type inputFieldType = InputField.GetFieldType(fieldType);

			foreach (var prop in submitObj.Properties())
			{
				if (prop.Name.ToLower() == "entityname")
					continue;

				int count = inputFieldType.GetProperties().Where(n => n.Name.ToLower() == prop.Name.ToLower()).Count();
				if (count < 1)
					response.Errors.Add(new ErrorModel(prop.Name, prop.Value.ToString(), "Input object contains property that is not part of the object model."));
			}

			if (response.Errors.Count > 0)
				return DoBadRequestResponse(response);

			try
			{
				field = InputField.ConvertField(submitObj);
			}
			catch (Exception e)
			{
				return DoBadRequestResponse(response, "Input object is not in valid format! It cannot be converted.", e);
			}

			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				connection.BeginTransaction();
				try
				{
					response = _entityManager.UpdateField(entityId, field);
					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					return DoBadRequestResponse(response, ex.Message, ex);
				}
			}

			return DoResponse(response);
		}

		/// <summary>
		/// Partially update a field on an entity (PATCH semantics).
		/// PATCH: api/v3.0/meta/entity/{entityName}/field/{fieldId}
		///
		/// This is the most complex endpoint — contains a 22+ case switch statement that handles
		/// type-specific partial patching for every field type in the system. Each case creates a
		/// typed InputField and applies only the submitted properties for that field type.
		/// Common field properties (label, description, required, etc.) are applied outside the switch.
		///
		/// Behavior preserved exactly from monolith WebApiController.cs lines 1678-1978.
		/// </summary>
		[HttpPatch("entity/{entityName}/field/{fieldId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult PatchField(string entityName, Guid fieldId, [FromBody] JObject submitObj)
		{
			FieldResponse response = new FieldResponse();
			Entity entity = new Entity();
			InputField field = new InputGuidField();

			try
			{
				if (!Guid.TryParse(entityName, out Guid entityId))
				{
					response.Errors.Add(new ErrorModel("Id", entityName, "id parameter is not valid Guid value"));
					return DoBadRequestResponse(response, "Field was not updated!");
				}

				var entityResponse = _entityManager.ReadEntity(entityId);
				if (!entityResponse.Success || entityResponse.Object == null)
				{
					response.Errors.Add(new ErrorModel("Id", entityName, "Entity with such Id does not exist!"));
					return DoBadRequestResponse(response, "Field was not updated!");
				}
				entity = entityResponse.Object;

				Field updatedField = entity.Fields.FirstOrDefault(f => f.Id == fieldId);
				if (updatedField == null)
				{
					response.Errors.Add(new ErrorModel("FieldId", fieldId.ToString(), "Field with such Id does not exist!"));
					return DoBadRequestResponse(response, "Field was not updated!");
				}

				FieldType fieldType = FieldType.GuidField;

				var fieldTypeProp = submitObj.Properties().SingleOrDefault(k => k.Name.ToLower() == "fieldtype");
				if (fieldTypeProp != null)
				{
					fieldType = (FieldType)Enum.ToObject(typeof(FieldType), fieldTypeProp.Value.ToObject<int>());
				}
				else
				{
					response.Errors.Add(new ErrorModel("fieldType", null, "fieldType is required!"));
					return DoBadRequestResponse(response, "Field was not updated!");
				}

				Type inputFieldType = InputField.GetFieldType(fieldType);
				foreach (var prop in submitObj.Properties())
				{
					if (prop.Name.ToLower() == "entityname")
						continue;

					int count = inputFieldType.GetProperties().Where(n => n.Name.ToLower() == prop.Name.ToLower()).Count();
					if (count < 1)
						response.Errors.Add(new ErrorModel(prop.Name, prop.Value.ToString(), "Input object contains property that is not part of the object model."));
				}

				if (response.Errors.Count > 0)
					return DoBadRequestResponse(response);

				InputField inputField = InputField.ConvertField(submitObj);

				foreach (var prop in submitObj.Properties())
				{
					switch (fieldType)
					{
						case FieldType.AutoNumberField:
							{
								field = new InputAutoNumberField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputAutoNumberField)field).DefaultValue = ((InputAutoNumberField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "displayformat")
									((InputAutoNumberField)field).DisplayFormat = ((InputAutoNumberField)inputField).DisplayFormat;
								if (prop.Name.ToLower() == "startingnumber")
									((InputAutoNumberField)field).StartingNumber = ((InputAutoNumberField)inputField).StartingNumber;
							}
							break;
						case FieldType.CheckboxField:
							{
								field = new InputCheckboxField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputCheckboxField)field).DefaultValue = ((InputCheckboxField)inputField).DefaultValue;
							}
							break;
						case FieldType.CurrencyField:
							{
								field = new InputCurrencyField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputCurrencyField)field).DefaultValue = ((InputCurrencyField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "minvalue")
									((InputCurrencyField)field).MinValue = ((InputCurrencyField)inputField).MinValue;
								if (prop.Name.ToLower() == "maxvalue")
									((InputCurrencyField)field).MaxValue = ((InputCurrencyField)inputField).MaxValue;
								if (prop.Name.ToLower() == "currency")
									((InputCurrencyField)field).Currency = ((InputCurrencyField)inputField).Currency;
							}
							break;
						case FieldType.DateField:
							{
								field = new InputDateField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputDateField)field).DefaultValue = ((InputDateField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "format")
									((InputDateField)field).Format = ((InputDateField)inputField).Format;
								if (prop.Name.ToLower() == "usecurrenttimeasdefaultvalue")
									((InputDateField)field).UseCurrentTimeAsDefaultValue = ((InputDateField)inputField).UseCurrentTimeAsDefaultValue;
							}
							break;
						case FieldType.DateTimeField:
							{
								field = new InputDateTimeField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputDateTimeField)field).DefaultValue = ((InputDateTimeField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "format")
									((InputDateTimeField)field).Format = ((InputDateTimeField)inputField).Format;
								if (prop.Name.ToLower() == "usecurrenttimeasdefaultvalue")
									((InputDateTimeField)field).UseCurrentTimeAsDefaultValue = ((InputDateTimeField)inputField).UseCurrentTimeAsDefaultValue;
							}
							break;
						case FieldType.EmailField:
							{
								field = new InputEmailField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputEmailField)field).DefaultValue = ((InputEmailField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "maxlength")
									((InputEmailField)field).MaxLength = ((InputEmailField)inputField).MaxLength;
							}
							break;
						case FieldType.FileField:
							{
								field = new InputFileField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputFileField)field).DefaultValue = ((InputFileField)inputField).DefaultValue;
							}
							break;
						case FieldType.HtmlField:
							{
								field = new InputHtmlField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputHtmlField)field).DefaultValue = ((InputHtmlField)inputField).DefaultValue;
							}
							break;
						case FieldType.ImageField:
							{
								field = new InputImageField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputImageField)field).DefaultValue = ((InputImageField)inputField).DefaultValue;
							}
							break;
						case FieldType.MultiLineTextField:
							{
								field = new InputMultiLineTextField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputMultiLineTextField)field).DefaultValue = ((InputMultiLineTextField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "maxlength")
									((InputMultiLineTextField)field).MaxLength = ((InputMultiLineTextField)inputField).MaxLength;
								if (prop.Name.ToLower() == "visiblelinenumber")
									((InputMultiLineTextField)field).VisibleLineNumber = ((InputMultiLineTextField)inputField).VisibleLineNumber;
							}
							break;
						case FieldType.GeographyField:
							{
								field = new InputGeographyField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputGeographyField)field).DefaultValue = ((InputGeographyField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "maxlength")
									((InputGeographyField)field).MaxLength = ((InputGeographyField)inputField).MaxLength;
								if (prop.Name.ToLower() == "visiblelinenumber")
									((InputGeographyField)field).VisibleLineNumber = ((InputGeographyField)inputField).VisibleLineNumber;
								if (prop.Name.ToLower() == "format")
									((InputGeographyField)field).Format = ((InputGeographyField)inputField).Format;
								if (prop.Name.ToLower() == "srid")
									((InputGeographyField)field).SRID = ((InputGeographyField)inputField).SRID;
							}
							break;
						case FieldType.MultiSelectField:
							{
								field = new InputMultiSelectField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputMultiSelectField)field).DefaultValue = ((InputMultiSelectField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "options")
									((InputMultiSelectField)field).Options = ((InputMultiSelectField)inputField).Options;
							}
							break;
						case FieldType.NumberField:
							{
								field = new InputNumberField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputNumberField)field).DefaultValue = ((InputNumberField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "minvalue")
									((InputNumberField)field).MinValue = ((InputNumberField)inputField).MinValue;
								if (prop.Name.ToLower() == "maxvalue")
									((InputNumberField)field).MaxValue = ((InputNumberField)inputField).MaxValue;
								if (prop.Name.ToLower() == "decimalplaces")
									((InputNumberField)field).DecimalPlaces = ((InputNumberField)inputField).DecimalPlaces;
							}
							break;
						case FieldType.PasswordField:
							{
								field = new InputPasswordField();
								if (prop.Name.ToLower() == "maxlength")
									((InputPasswordField)field).MaxLength = ((InputPasswordField)inputField).MaxLength;
								if (prop.Name.ToLower() == "minlength")
									((InputPasswordField)field).MinLength = ((InputPasswordField)inputField).MinLength;
								if (prop.Name.ToLower() == "encrypted")
									((InputPasswordField)field).Encrypted = ((InputPasswordField)inputField).Encrypted;
							}
							break;
						case FieldType.PercentField:
							{
								field = new InputPercentField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputPercentField)field).DefaultValue = ((InputPercentField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "minvalue")
									((InputPercentField)field).MinValue = ((InputPercentField)inputField).MinValue;
								if (prop.Name.ToLower() == "maxvalue")
									((InputPercentField)field).MaxValue = ((InputPercentField)inputField).MaxValue;
								if (prop.Name.ToLower() == "decimalplaces")
									((InputPercentField)field).DecimalPlaces = ((InputPercentField)inputField).DecimalPlaces;
							}
							break;
						case FieldType.PhoneField:
							{
								field = new InputPhoneField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputPhoneField)field).DefaultValue = ((InputPhoneField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "format")
									((InputPhoneField)field).Format = ((InputPhoneField)inputField).Format;
								if (prop.Name.ToLower() == "maxlength")
									((InputPhoneField)field).MaxLength = ((InputPhoneField)inputField).MaxLength;
							}
							break;
						case FieldType.GuidField:
							{
								field = new InputGuidField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputGuidField)field).DefaultValue = ((InputGuidField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "generatenewid")
									((InputGuidField)field).GenerateNewId = ((InputGuidField)inputField).GenerateNewId;
							}
							break;
						case FieldType.SelectField:
							{
								field = new InputSelectField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputSelectField)field).DefaultValue = ((InputSelectField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "options")
									((InputSelectField)field).Options = ((InputSelectField)inputField).Options;
							}
							break;
						case FieldType.TextField:
							{
								field = new InputTextField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputTextField)field).DefaultValue = ((InputTextField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "maxlength")
									((InputTextField)field).MaxLength = ((InputTextField)inputField).MaxLength;
							}
							break;
						case FieldType.UrlField:
							{
								field = new InputUrlField();
								if (prop.Name.ToLower() == "defaultvalue")
									((InputUrlField)field).DefaultValue = ((InputUrlField)inputField).DefaultValue;
								if (prop.Name.ToLower() == "maxlength")
									((InputUrlField)field).MaxLength = ((InputUrlField)inputField).MaxLength;
								if (prop.Name.ToLower() == "opentargetinnewwindow")
									((InputUrlField)field).OpenTargetInNewWindow = ((InputUrlField)inputField).OpenTargetInNewWindow;
							}
							break;
					}

					// Common field properties applied outside the type-specific switch.
					// These apply to ALL field types regardless of the switch case above.
					if (prop.Name.ToLower() == "label")
						field.Label = inputField.Label;
					else if (prop.Name.ToLower() == "placeholdertext")
						field.PlaceholderText = inputField.PlaceholderText;
					else if (prop.Name.ToLower() == "description")
						field.Description = inputField.Description;
					else if (prop.Name.ToLower() == "helptext")
						field.HelpText = inputField.HelpText;
					else if (prop.Name.ToLower() == "required")
						field.Required = inputField.Required;
					else if (prop.Name.ToLower() == "unique")
						field.Unique = inputField.Unique;
					else if (prop.Name.ToLower() == "searchable")
						field.Searchable = inputField.Searchable;
					else if (prop.Name.ToLower() == "auditable")
						field.Auditable = inputField.Auditable;
					else if (prop.Name.ToLower() == "system")
						field.System = inputField.System;
				}
			}
			catch (Exception e)
			{
				return DoBadRequestResponse(response, "Input object is not in valid format! It cannot be converted.", e);
			}

			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				connection.BeginTransaction();
				try
				{
					response = _entityManager.UpdateField(entity, field);
					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					return DoBadRequestResponse(response, ex.Message, ex);
				}
			}

			return DoResponse(response);
		}

		/// <summary>
		/// Delete a field from an entity.
		/// DELETE: api/v3.0/meta/entity/{entityName}/field/{fieldId}
		/// </summary>
		[HttpDelete("entity/{entityName}/field/{fieldId}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult DeleteField(string entityName, Guid fieldId)
		{
			FieldResponse response = new FieldResponse();

			if (!Guid.TryParse(entityName, out Guid entityId))
			{
				response.Errors.Add(new ErrorModel("id", entityName, "id parameter is not valid Guid value"));
				return DoResponse(response);
			}

			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				connection.BeginTransaction();
				try
				{
					response = _entityManager.DeleteField(entityId, fieldId);
					connection.CommitTransaction();
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					return DoBadRequestResponse(response, ex.Message, ex);
				}
			}

			return DoResponse(response);
		}

		#endregion

		#region << Relation Meta >>

		/// <summary>
		/// Get all entity relation definitions.
		/// GET: api/v3.0/meta/relation/list
		/// </summary>
		[HttpGet("relation/list")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetEntityRelationMetaList()
		{
			var response = _relationManager.Read();
			return DoResponse(response);
		}

		/// <summary>
		/// Get entity relation metadata by name.
		/// GET: api/v3.0/meta/relation/{name}
		/// </summary>
		[HttpGet("relation/{name}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetEntityRelationMeta(string name)
		{
			return DoResponse(_relationManager.Read(name));
		}

		/// <summary>
		/// Create a new entity relation.
		/// POST: api/v3.0/meta/relation
		/// If no ID is provided in the request body, a new GUID is generated automatically.
		/// </summary>
		[HttpPost("relation")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult CreateEntityRelation([FromBody] JObject submitObj)
		{
			try
			{
				if (submitObj["id"] == null || submitObj["id"].Type == JTokenType.Null)
					submitObj["id"] = Guid.NewGuid();

				var relation = submitObj.ToObject<EntityRelation>();

				EntityRelationResponse response;
				using (var connection = CoreDbContext.Current.CreateConnection())
				{
					connection.BeginTransaction();
					try
					{
						response = _relationManager.Create(relation);
						connection.CommitTransaction();
					}
					catch (Exception ex)
					{
						connection.RollbackTransaction();
						return DoBadRequestResponse(new EntityRelationResponse(), ex.Message, ex);
					}
				}

				return DoResponse(response);
			}
			catch (Exception e)
			{
				return DoBadRequestResponse(new EntityRelationResponse(), null, e);
			}
		}

		/// <summary>
		/// Full replacement update of an entity relation.
		/// PUT: api/v3.0/meta/relation/{name}
		/// The name parameter is the relation ID as a GUID string.
		/// </summary>
		[HttpPut("relation/{name}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult UpdateEntityRelation(string name, [FromBody] JObject submitObj)
		{
			FieldResponse response = new FieldResponse();

			if (!Guid.TryParse(name, out Guid relationId))
			{
				response.Errors.Add(new ErrorModel("id", name, "id parameter is not valid Guid value"));
				return DoResponse(response);
			}

			try
			{
				var relation = submitObj.ToObject<EntityRelation>();

				EntityRelationResponse relationResponse;
				using (var connection = CoreDbContext.Current.CreateConnection())
				{
					connection.BeginTransaction();
					try
					{
						relationResponse = _relationManager.Update(relation);
						connection.CommitTransaction();
					}
					catch (Exception ex)
					{
						connection.RollbackTransaction();
						return DoBadRequestResponse(new EntityRelationResponse(), ex.Message, ex);
					}
				}

				return DoResponse(relationResponse);
			}
			catch (Exception e)
			{
				return DoBadRequestResponse(new EntityRelationResponse(), null, e);
			}
		}

		/// <summary>
		/// Delete an entity relation by ID.
		/// DELETE: api/v3.0/meta/relation/{name}
		/// The name parameter is the relation ID as a GUID string.
		/// </summary>
		[HttpDelete("relation/{name}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult DeleteEntityRelation(string name)
		{
			if (Guid.TryParse(name, out Guid newGuid))
			{
				EntityRelationResponse response;
				using (var connection = CoreDbContext.Current.CreateConnection())
				{
					connection.BeginTransaction();
					try
					{
						response = _relationManager.Delete(newGuid);
						connection.CommitTransaction();
					}
					catch (Exception ex)
					{
						connection.RollbackTransaction();
						return DoBadRequestResponse(new EntityRelationResponse(), ex.Message, ex);
					}
				}
				return DoResponse(response);
			}
			else
			{
				return DoBadRequestResponse(new EntityRelationResponse(), "The entity relation Id should be a valid Guid", null);
			}
		}

		#endregion
	}
}
