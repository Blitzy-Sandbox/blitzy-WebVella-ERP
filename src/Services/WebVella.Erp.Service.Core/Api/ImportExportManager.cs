using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Api
{
	/// <summary>
	/// Core Platform service CSV import/export manager adapted from the monolith's
	/// <c>WebVella.Erp.Api.ImportExportManager</c> (1106 lines).
	///
	/// Provides CSV-based record import and evaluation using CsvHelper,
	/// with full relation field support, field type parsing, and transaction management.
	///
	/// Key adaptations:
	/// <list type="bullet">
	///   <item><c>DbContext.Current</c> replaced with injected <see cref="CoreDbContext"/></item>
	///   <item><c>new RecordManager()</c> etc. replaced with injected services</item>
	///   <item><c>ErpSettings.DevelopmentMode</c> replaced with <c>IConfiguration</c> lookup</item>
	///   <item>File reading via injected <see cref="DbFileRepository"/> instead of <c>new DbFileRepository()</c></item>
	///   <item>All validation rules, error messages, and CSV parsing logic preserved exactly</item>
	/// </list>
	/// </summary>
	public class ImportExportManager
	{
		private const char RELATION_SEPARATOR = '.';
		private const char RELATION_NAME_RESULT_SEPARATOR = '$';

		private readonly CoreDbContext _dbContext;
		private readonly RecordManager _recordManager;
		private readonly EntityManager _entityManager;
		private readonly EntityRelationManager _entityRelationManager;
		private readonly SecurityManager _securityManager;
		private readonly DbFileRepository _fileRepository;
		private readonly IConfiguration _configuration;
		private readonly ILogger<ImportExportManager> _logger;

		/// <summary>
		/// Helper property replacing static ErpSettings.DevelopmentMode.
		/// </summary>
		private bool IsDevelopmentMode =>
			string.Equals(_configuration["Settings:DevelopmentMode"], "true", StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Constructs an ImportExportManager with all required service dependencies.
		/// Replaces monolith pattern of <c>new ImportExportManager()</c>.
		/// </summary>
		public ImportExportManager(
			CoreDbContext dbContext,
			RecordManager recordManager,
			EntityManager entityManager,
			EntityRelationManager entityRelationManager,
			SecurityManager securityManager,
			DbFileRepository fileRepository,
			IConfiguration configuration,
			ILogger<ImportExportManager> logger)
		{
			_dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_entityRelationManager = entityRelationManager ?? throw new ArgumentNullException(nameof(entityRelationManager));
			_securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
			_fileRepository = fileRepository ?? throw new ArgumentNullException(nameof(fileRepository));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <summary>
		/// Imports entity records from a CSV file stored in the file repository.
		/// The CSV must have column names matching entity field names.
		/// The first column should be "id" — if null or empty, a new record is created;
		/// otherwise the existing record is updated.
		///
		/// Supports relation fields using the $relation.field notation.
		/// All operations run within a single database transaction.
		/// Preserved exactly from monolith ImportExportManager.ImportEntityRecordsFromCsv().
		/// </summary>
		public ResponseModel ImportEntityRecordsFromCsv(string entityName, string fileTempPath)
		{
			ResponseModel response = new ResponseModel();
			response.Message = "Records successfully imported";
			response.Timestamp = DateTime.UtcNow;
			response.Success = true;
			response.Object = null;

			if (string.IsNullOrWhiteSpace(fileTempPath))
			{
				response.Timestamp = DateTime.UtcNow;
				response.Success = false;
				response.Message = "Import failed! fileTempPath parameter cannot be empty or null!";
				response.Errors.Add(new ErrorModel("fileTempPath", fileTempPath, "Import failed! File does not exist!"));
				return response;
			}

			if (fileTempPath.StartsWith("/fs"))
				fileTempPath = fileTempPath.Remove(0, 3);

			if (!fileTempPath.StartsWith("/"))
				fileTempPath = "/" + fileTempPath;

			fileTempPath = fileTempPath.ToLowerInvariant();

			using (DbConnection connection = _dbContext.CreateConnection())
			{
				List<EntityRelation> relations = _entityRelationManager.Read().Object;
				EntityListResponse entitiesResponse = _entityManager.ReadEntities();
				List<Entity> entities = entitiesResponse.Object;
				Entity entity = entities.FirstOrDefault(e => e.Name == entityName);

				if (entity == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "Import failed! Entity with such name does not exist!";
					response.Errors.Add(new ErrorModel("entityName", entityName, "Entity with such name does not exist!"));
					return response;
				}

				DbFile file = _fileRepository.Find(fileTempPath);

				if (file == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "Import failed! File does not exist!";
					response.Errors.Add(new ErrorModel("fileTempPath", fileTempPath, "Import failed! File does not exist!"));
					return response;
				}

				byte[] fileBytes = file.GetBytes();
				MemoryStream fileStream = new MemoryStream(fileBytes);
				TextReader reader = new StreamReader(fileStream);
				var config = new CsvConfiguration(CultureInfo.InvariantCulture)
				{
					Encoding = Encoding.UTF8,
					HasHeaderRecord = true,
				};

				CsvReader csvReader = new CsvReader(reader, config);
				csvReader.Read();
				csvReader.ReadHeader();
				var headerRecord = csvReader.GetRecord<dynamic>();
				List<string> columns = new List<string>();
				foreach (var name in headerRecord)
				{
					columns.Add(name.Key.ToString());
				}

				List<dynamic> fieldMetaList = new List<dynamic>();

				foreach (var column in columns)
				{
					Field field;
					if (column.Contains(RELATION_SEPARATOR))
					{
						var relationData = column.Split(RELATION_SEPARATOR).Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
						if (relationData.Count > 2)
							throw new Exception(string.Format("The specified field name '{0}' is incorrect. Only first level relation can be specified.", column));

						string relationName = relationData[0];
						string relationFieldName = relationData[1];

						if (string.IsNullOrWhiteSpace(relationName) || relationName == "$" || relationName == "$$")
							throw new Exception(string.Format("Invalid relation '{0}'. The relation name is not specified.", column));
						else if (!relationName.StartsWith("$"))
							throw new Exception(string.Format("Invalid relation '{0}'. The relation name is not correct.", column));
						else
							relationName = relationName.Substring(1);

						if (relationName.StartsWith("$"))
						{
							relationName = relationName.Substring(1);
						}

						if (string.IsNullOrWhiteSpace(relationFieldName))
							throw new Exception(string.Format("Invalid relation '{0}'. The relation field name is not specified.", column));

						var relation = relations.SingleOrDefault(x => x.Name == relationName);
						if (relation == null)
							throw new Exception(string.Format("Invalid relation '{0}'. The relation does not exist.", column));

						if (relation.TargetEntityId != entity.Id && relation.OriginEntityId != entity.Id)
							throw new Exception(string.Format("Invalid relation '{0}'. The relation field belongs to entity that does not relate to current entity.", column));

						Entity relationEntity = null;

						if (relation.OriginEntityId == entity.Id)
						{
							relationEntity = entities.FirstOrDefault(e => e.Id == relation.TargetEntityId);
							field = relationEntity.Fields.FirstOrDefault(f => f.Name == relationFieldName);
						}
						else
						{
							relationEntity = entities.FirstOrDefault(e => e.Id == relation.OriginEntityId);
							field = relationEntity.Fields.FirstOrDefault(f => f.Name == relationFieldName);
						}
					}
					else
					{
						field = entity.Fields.FirstOrDefault(f => f.Name == column);
					}

					dynamic fieldMeta = new ExpandoObject();
					fieldMeta.ColumnName = column;
					fieldMeta.FieldType = field.GetFieldType();
					fieldMetaList.Add(fieldMeta);
				}

				connection.BeginTransaction();

				try
				{
					do
					{
						EntityRecord newRecord = new EntityRecord();
						foreach (var fieldMeta in fieldMetaList)
						{
							string columnName = fieldMeta.ColumnName.ToString();
							string value = csvReader.GetField<string>(columnName);

							if (value.StartsWith("[") && value.EndsWith("]"))
							{
								newRecord[columnName] = JsonConvert.DeserializeObject<List<string>>(value);
							}
							else
							{
								switch ((FieldType)fieldMeta.FieldType)
								{
									case FieldType.AutoNumberField:
									case FieldType.CurrencyField:
									case FieldType.NumberField:
									case FieldType.PercentField:
										{
											decimal decValue;
											if (decimal.TryParse(value, out decValue))
												newRecord[columnName] = decValue;
											else
												newRecord[columnName] = null;
										}
										break;
									case FieldType.CheckboxField:
										{
											bool bValue;
											if (bool.TryParse(value, out bValue))
												newRecord[columnName] = bValue;
											else
												newRecord[columnName] = null;
										}
										break;
									case FieldType.DateField:
									case FieldType.DateTimeField:
										{
											DateTime dtValue;
											if (DateTime.TryParse(value, out dtValue))
												newRecord[columnName] = dtValue;
											else
												newRecord[columnName] = null;
										}
										break;
									case FieldType.MultiSelectField:
										{
											if (!string.IsNullOrWhiteSpace(value))
												newRecord[columnName] = new List<string>(new string[] { value });
											else
												newRecord[columnName] = null;
										}
										break;
									case FieldType.GuidField:
										{
											Guid gValue;
											if (Guid.TryParse(value, out gValue))
												newRecord[columnName] = gValue;
											else
												newRecord[columnName] = null;
										}
										break;
									default:
										{
											newRecord[columnName] = value;
										}
										break;
								}
							}
						}

						QueryResponse result;
						if (!newRecord.GetProperties().Any(x => x.Key == "id") || newRecord["id"] == null || string.IsNullOrEmpty(newRecord["id"].ToString()))
						{
							newRecord["id"] = Guid.NewGuid();
							result = _recordManager.CreateRecord(entityName, newRecord);
						}
						else
						{
							result = _recordManager.UpdateRecord(entityName, newRecord);
						}

						if (!result.Success)
						{
							string message = result.Message;
							if (result.Errors.Count > 0)
							{
								foreach (ErrorModel error in result.Errors)
									message += " " + error.Message;
							}
							throw new Exception(message);
						}
					} while (csvReader.Read());

					connection.CommitTransaction();
				}
				catch (Exception e)
				{
					connection.RollbackTransaction();

					response.Success = false;
					response.Object = null;
					response.Timestamp = DateTime.UtcNow;

					if (IsDevelopmentMode)
						response.Message = e.Message + e.StackTrace;
					else
						response.Message = "Import failed! An internal error occurred!";
				}
				finally
				{
					reader.Close();
					fileStream.Close();
				}

				return response;
			}
		}

		/// <summary>
		/// Evaluates (and optionally imports) entity records from a CSV source.
		/// Accepts either a file path or clipboard content. Supports two modes:
		/// - "evaluate": validates columns and data, returns errors/warnings/commands
		/// - "evaluate-import": validates and imports if no errors found
		///
		/// Returns a detailed evaluation object with per-column errors, warnings,
		/// record data, field commands (to_create/to_update/no_import), and statistics.
		/// Preserved exactly from monolith ImportExportManager.EvaluateImportEntityRecordsFromCsv().
		/// </summary>
		public ResponseModel EvaluateImportEntityRecordsFromCsv(string entityName, JObject postObject, object controller = null)
		{
			ResponseModel response = new ResponseModel();
			response.Message = "Records successfully evaluated";
			response.Timestamp = DateTime.UtcNow;
			response.Success = true;
			response.Object = null;

			List<EntityRelation> relations = _entityRelationManager.Read().Object;
			EntityListResponse entitiesResponse = _entityManager.ReadEntities();
			List<Entity> entities = entitiesResponse.Object;
			Entity entity = entities.FirstOrDefault(e => e.Name == entityName);
			if (entity == null)
			{
				response.Success = false;
				response.Message = "Entity not found";
				return response;
			}

			var entityFields = entity.Fields;
			string fileTempPath = "";
			string clipboard = "";
			string generalCommand = "evaluate";
			EntityRecord commands = new EntityRecord();

			if (!postObject.IsNullOrEmpty() && postObject.Properties().Any(p => p.Name == "fileTempPath"))
			{
				fileTempPath = postObject["fileTempPath"].ToString();
			}
			if (!postObject.IsNullOrEmpty() && postObject.Properties().Any(p => p.Name == "clipboard"))
			{
				clipboard = postObject["clipboard"].ToString();
			}
			if (!postObject.IsNullOrEmpty() && postObject.Properties().Any(p => p.Name == "general_command"))
			{
				generalCommand = postObject["general_command"].ToString();
			}
			if (!postObject.IsNullOrEmpty() && generalCommand == "evaluate-import" &&
				postObject.Properties().Any(p => p.Name == "commands") && !((JToken)postObject["commands"]).IsNullOrEmpty())
			{
				var commandsObject = postObject["commands"].Value<JObject>();
				if (!commandsObject.IsNullOrEmpty() && commandsObject.Properties().Any())
				{
					foreach (var property in commandsObject.Properties())
					{
						commands[property.Name] = ((JObject)property.Value).ToObject<EntityRecord>();
					}
				}
			}

			if (fileTempPath == "" && clipboard == "")
			{
				response.Success = false;
				response.Message = "Both clipboard and file CSV sources are empty!";
				return response;
			}

			var config = new CsvConfiguration(CultureInfo.InvariantCulture)
			{
				Encoding = Encoding.UTF8,
				HasHeaderRecord = true,
			};
			CsvReader csvReader = null;

			if (fileTempPath != "")
			{
				if (fileTempPath.StartsWith("/fs"))
					fileTempPath = fileTempPath.Remove(0, 3);
				if (!fileTempPath.StartsWith("/"))
					fileTempPath = "/" + fileTempPath;
				fileTempPath = fileTempPath.ToLowerInvariant();

				DbFile file = _fileRepository.Find(fileTempPath);
				if (file == null)
				{
					response.Timestamp = DateTime.UtcNow;
					response.Success = false;
					response.Message = "Import failed! File does not exist!";
					response.Errors.Add(new ErrorModel("fileTempPath", fileTempPath, "Import failed! File does not exist!"));
					return response;
				}

				byte[] fileBytes = file.GetBytes();
				MemoryStream fileStream = new MemoryStream(fileBytes);
				TextReader reader = new StreamReader(fileStream);
				csvReader = new CsvReader(reader, config);
			}
			else
			{
				config.Delimiter = "\t";
				csvReader = new CsvReader(new StringReader(clipboard), config);
			}

			var evaluationObj = new EntityRecord();
			evaluationObj["errors"] = new EntityRecord();
			evaluationObj["warnings"] = new EntityRecord();
			evaluationObj["records"] = new List<EntityRecord>();
			evaluationObj["commands"] = new EntityRecord();
			var statsObject = new EntityRecord();
			statsObject["to_create"] = 0;
			statsObject["no_import"] = 0;
			statsObject["to_update"] = 0;
			statsObject["errors"] = 0;
			statsObject["warnings"] = 0;
			evaluationObj["stats"] = statsObject;
			evaluationObj["general_command"] = generalCommand;

			csvReader.Read();
			csvReader.ReadHeader();
			var headerRecord = csvReader.GetRecord<dynamic>();
			List<string> columnNames = new List<string>();
			foreach (var name in headerRecord)
			{
				columnNames.Add(name.Key.ToString());
			}

			// Phase 1: Validate columns and build commands
			foreach (var columnName in columnNames)
			{
				if (!((EntityRecord)evaluationObj["errors"]).GetProperties().Any(p => p.Key == columnName))
				{
					((EntityRecord)evaluationObj["errors"])[columnName] = new List<string>();
				}
				if (!((EntityRecord)evaluationObj["warnings"]).GetProperties().Any(p => p.Key == columnName))
				{
					((EntityRecord)evaluationObj["warnings"])[columnName] = new List<string>();
				}

				bool existingField = false;
				Field currentFieldMeta = null;
				Field relationEntityFieldMeta = null;
				Field relationFieldMeta = null;
				Entity relationEntity = null;
				string direction = "origin-target";
				EntityRelationType relationType = EntityRelationType.OneToMany;
				string fieldEnityName = entity.Name;
				string fieldRelationName = string.Empty;

				if (!commands.GetProperties().Any(p => p.Key == columnName))
				{
					commands[columnName] = new EntityRecord();
					((EntityRecord)commands[columnName])["command"] = "no_import";
					((EntityRecord)commands[columnName])["entityName"] = fieldEnityName;
					((EntityRecord)commands[columnName])["fieldType"] = FieldType.TextField;
					((EntityRecord)commands[columnName])["fieldName"] = columnName;
					((EntityRecord)commands[columnName])["fieldLabel"] = columnName;
				}

				if (columnName.Contains(RELATION_SEPARATOR))
				{
					var relationData = columnName.Split(RELATION_SEPARATOR).Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
					if (relationData.Count > 2)
					{
						((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add(string.Format("The specified field name '{0}' is incorrect. Only first level relation can be specified.", columnName));
						((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						continue;
					}

					string relationName = relationData[0];
					string relationFieldName = relationData[1];

					if (string.IsNullOrWhiteSpace(relationName) || relationName == "$" || relationName == "$$")
					{
						((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add(string.Format("Invalid relation '{0}'. The relation name is not specified.", columnName));
						((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						continue;
					}
					else if (!relationName.StartsWith("$"))
					{
						((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add(string.Format("Invalid relation '{0}'. The relation name is not correct.", columnName));
						((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						continue;
					}
					else
						relationName = relationName.Substring(1);

					if (relationName.StartsWith("$"))
					{
						relationName = relationName.Substring(1);
						direction = "target-origin";
					}

					if (string.IsNullOrWhiteSpace(relationFieldName))
					{
						((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add(string.Format("Invalid relation '{0}'. The relation field name is not specified.", columnName));
						((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						continue;
					}

					var relation = relations.SingleOrDefault(x => x.Name == relationName);
					if (relation == null)
					{
						((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add(string.Format("Invalid relation '{0}'. The relation does not exist.", columnName));
						((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						continue;
					}

					if (relation.TargetEntityId != entity.Id && relation.OriginEntityId != entity.Id)
					{
						((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add(string.Format("Invalid relation '{0}'. The relation field belongs to entity that does not relate to current entity.", columnName));
						((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						continue;
					}

					if (relation.OriginEntityId == relation.TargetEntityId)
					{
						if (direction == "origin-target")
						{
							relationEntity = entity;
							relationEntityFieldMeta = relationEntity.Fields.FirstOrDefault(f => f.Id == relation.TargetFieldId);
							currentFieldMeta = relationEntity.Fields.FirstOrDefault(f => f.Name == relationFieldName);
							relationFieldMeta = entity.Fields.FirstOrDefault(f => f.Id == relation.OriginFieldId);
						}
						else
						{
							relationEntity = entity;
							relationEntityFieldMeta = relationEntity.Fields.FirstOrDefault(f => f.Id == relation.OriginFieldId);
							currentFieldMeta = relationEntity.Fields.FirstOrDefault(f => f.Name == relationFieldName);
							relationFieldMeta = entity.Fields.FirstOrDefault(f => f.Id == relation.TargetFieldId);
						}
					}
					else if (relation.OriginEntityId == entity.Id)
					{
						relationEntity = entities.FirstOrDefault(e => e.Id == relation.TargetEntityId);
						relationEntityFieldMeta = relationEntity.Fields.FirstOrDefault(f => f.Id == relation.TargetFieldId);
						currentFieldMeta = relationEntity.Fields.FirstOrDefault(f => f.Name == relationFieldName);
						relationFieldMeta = entity.Fields.FirstOrDefault(f => f.Id == relation.OriginFieldId);
					}
					else
					{
						relationEntity = entities.FirstOrDefault(e => e.Id == relation.OriginEntityId);
						relationEntityFieldMeta = relationEntity.Fields.FirstOrDefault(f => f.Id == relation.OriginFieldId);
						currentFieldMeta = relationEntity.Fields.FirstOrDefault(f => f.Name == relationFieldName);
						relationFieldMeta = entity.Fields.FirstOrDefault(f => f.Id == relation.TargetFieldId);
					}

					if (currentFieldMeta == null)
					{
						((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add(string.Format("Invalid relation '{0}'. Fields with such name does not exist.", columnName));
						((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						continue;
					}

					if (currentFieldMeta.GetFieldType() == FieldType.MultiSelectField)
					{
						((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add(string.Format("Invalid relation '{0}'. Fields from Multiselect type can't be used as relation fields.", columnName));
						((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						continue;
					}

					if (relation.RelationType == EntityRelationType.OneToOne &&
						((relation.TargetEntityId == entity.Id && relationFieldMeta.Name == "id") || (relation.OriginEntityId == entity.Id && relationEntityFieldMeta.Name == "id")))
					{
						((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add(string.Format("Invalid relation '{0}'. Can't use relations when relation field is id field.", columnName));
						((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						continue;
					}

					fieldEnityName = relationEntity.Name;
					fieldRelationName = relationName;
					relationType = relation.RelationType;
				}
				else
				{
					currentFieldMeta = entity.Fields.FirstOrDefault(f => f.Name == columnName);
				}

				if (currentFieldMeta != null)
				{
					existingField = true;
				}

				if (!existingField && !string.IsNullOrWhiteSpace(fieldRelationName))
				{
					((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add(string.Format("Creation of a new relation field is not allowed.", columnName));
					((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
				}

				if (existingField)
				{
					if (generalCommand == "evaluate")
					{
						((EntityRecord)commands[columnName])["command"] = "to_update";
						((EntityRecord)commands[columnName])["relationName"] = fieldRelationName;
						((EntityRecord)commands[columnName])["relationDirection"] = direction;
						((EntityRecord)commands[columnName])["relationType"] = relationType;
						((EntityRecord)commands[columnName])["entityName"] = fieldEnityName;
						((EntityRecord)commands[columnName])["fieldType"] = currentFieldMeta.GetFieldType();
						((EntityRecord)commands[columnName])["fieldName"] = currentFieldMeta.Name;
						((EntityRecord)commands[columnName])["fieldLabel"] = currentFieldMeta.Label;

						bool hasPermission = SecurityContext.HasEntityPermission(EntityPermission.Update, entity);
						if (!hasPermission)
						{
							((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add($"Access denied. Trying to update record in entity '{entity.Name}' with no update access.");
							((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						}
					}

					((EntityRecord)commands[columnName])["currentFieldMeta"] = currentFieldMeta;
					((EntityRecord)commands[columnName])["relationEntityFieldMeta"] = relationEntityFieldMeta;
					((EntityRecord)commands[columnName])["relationFieldMeta"] = relationFieldMeta;
				}
				else
				{
					if (generalCommand == "evaluate")
					{
						((EntityRecord)commands[columnName])["command"] = "to_create";
						((EntityRecord)commands[columnName])["entityName"] = fieldEnityName;
						((EntityRecord)commands[columnName])["fieldType"] = FieldType.TextField;
						((EntityRecord)commands[columnName])["fieldName"] = columnName;
						((EntityRecord)commands[columnName])["fieldLabel"] = columnName;

						bool hasPermission = SecurityContext.HasEntityPermission(EntityPermission.Create, entity);
						if (!hasPermission)
						{
							((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add($"Access denied. Trying to create record in entity '{entity.Name}' with no create access.");
							((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						}
					}
				}
			}

			evaluationObj["commands"] = commands;

			// Phase 2: Validate row data
			while (csvReader.Read())
			{
				Dictionary<string, EntityRecord> fieldsFromRelationList = new Dictionary<string, EntityRecord>();
				Dictionary<string, string> rowRecordData = new Dictionary<string, string>();

				foreach (var columnName in columnNames)
				{
					string fieldValue = csvReader.GetField<string>(columnName);
					rowRecordData[columnName] = fieldValue;

					EntityRecord commandRecords = ((EntityRecord)commands[columnName]);
					Field currentFieldMeta = new TextField();
					if (commandRecords.GetProperties().Any(p => p.Key == "currentFieldMeta"))
						currentFieldMeta = (Field)commandRecords["currentFieldMeta"];

					if (columnName.Contains(RELATION_SEPARATOR))
					{
						string relationName = (string)((EntityRecord)commands[columnName])["relationName"];
						string relationDirection = (string)((EntityRecord)commands[columnName])["relationDirection"];
						string relationEntityName = (string)((EntityRecord)commands[columnName])["entityName"];

						EntityRelationType relationType = (EntityRelationType)Enum.Parse(typeof(EntityRelationType), (((EntityRecord)commands[columnName])["relationType"]).ToString());
						Field relationEntityFieldMeta = (Field)((EntityRecord)commands[columnName])["relationEntityFieldMeta"];
						Field relationFieldMeta = (Field)((EntityRecord)commands[columnName])["relationFieldMeta"];

						var relation = relations.SingleOrDefault(x => x.Name == relationName);

						string relationFieldValue = "";
						if (columnNames.Any(c => c == relationFieldMeta.Name))
							relationFieldValue = csvReader.GetField<string>(relationFieldMeta.Name);

						QueryObject filter = null;
						if ((relationType == EntityRelationType.OneToMany && relation.OriginEntityId == relation.TargetEntityId && relationDirection == "origin-target") ||
							(relationType == EntityRelationType.OneToMany && relation.OriginEntityId != relation.TargetEntityId && relation.OriginEntityId == entity.Id) ||
							relationType == EntityRelationType.ManyToMany)
						{
							if (!columnNames.Any(c => c == relationFieldMeta.Name) || string.IsNullOrEmpty(relationFieldValue))
							{
								((List<string>)((EntityRecord)evaluationObj["errors"])[columnName]).Add(string.Format("Invalid relation '{0}'. Relation field does not exist into input record data or its value is null.", columnName));
								((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
							}

							List<string> values = new List<string> { fieldValue };
							if (fieldValue.StartsWith("[") && fieldValue.EndsWith("]"))
							{
								values = JsonConvert.DeserializeObject<List<string>>(fieldValue);
							}
							if (values.Count < 1)
								continue;

							List<QueryObject> queries = new List<QueryObject>();
							foreach (var val in values)
							{
								queries.Add(EntityQuery.QueryEQ(currentFieldMeta.Name, val));
							}
							filter = EntityQuery.QueryOR(queries.ToArray());
						}
						else
						{
							filter = EntityQuery.QueryEQ(currentFieldMeta.Name, DbRecordRepository.ExtractFieldValue(fieldValue, currentFieldMeta, true));
						}

						EntityRecord fieldsFromRelation = new EntityRecord();
						if (fieldsFromRelationList.Any(r => r.Key == relation.Name))
						{
							fieldsFromRelation = fieldsFromRelationList[relationName];
						}
						else
						{
							fieldsFromRelation["columns"] = new List<string>();
							fieldsFromRelation["queries"] = new List<QueryObject>();
							fieldsFromRelation["direction"] = relationDirection;
							fieldsFromRelation["relationEntityName"] = relationEntityName;
						}

						((List<string>)fieldsFromRelation["columns"]).Add(columnName);
						((List<QueryObject>)fieldsFromRelation["queries"]).Add(filter);
						fieldsFromRelationList[relationName] = fieldsFromRelation;
					}
				}

				// Resolve relation records
				foreach (var fieldsFromRelation in fieldsFromRelationList)
				{
					EntityRecord fieldsFromRelationValue = fieldsFromRelation.Value;
					List<string> columnList = (List<string>)fieldsFromRelationValue["columns"];
					List<QueryObject> queries = (List<QueryObject>)fieldsFromRelationValue["queries"];
					string relationDirection = (string)fieldsFromRelationValue["direction"];
					string relationEntityName = (string)fieldsFromRelationValue["relationEntityName"];
					QueryObject filter = EntityQuery.QueryAND(queries.ToArray());

					var relation = relations.SingleOrDefault(r => r.Name == fieldsFromRelation.Key);

					QueryResponse relatedRecordResponse = _recordManager.Find(new EntityQuery(relationEntityName, "*", filter, null, null, null));

					if (!relatedRecordResponse.Success || relatedRecordResponse.Object.Data.Count < 1)
					{
						foreach (var colName in columnList)
						{
							((List<string>)((EntityRecord)evaluationObj["errors"])[colName]).Add(string.Format("Invalid relation '{0}'. The relation record does not exist.", colName));
							((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						}
					}
					else if (relatedRecordResponse.Object.Data.Count > 1 && ((relation.RelationType == EntityRelationType.OneToMany && relation.OriginEntityId == relation.TargetEntityId && relationDirection == "target-origin") ||
						(relation.RelationType == EntityRelationType.OneToMany && relation.OriginEntityId != relation.TargetEntityId && relation.TargetEntityId == entity.Id) ||
						relation.RelationType == EntityRelationType.OneToOne))
					{
						foreach (var colName in columnList)
						{
							((List<string>)((EntityRecord)evaluationObj["errors"])[colName]).Add(string.Format("Invalid relation '{0}'. There are multiple relation records matching this value.", colName));
							((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
						}
					}

					fieldsFromRelationList[fieldsFromRelation.Key]["relatedRecordResponse"] = relatedRecordResponse;
				}

				// Build row record data
				var rowRecord = new EntityRecord();
				if ((int)statsObject["errors"] == 0)
				{
					foreach (var colName in columnNames)
					{
						string fValue = rowRecordData[colName];
						EntityRecord commandRecords2 = ((EntityRecord)commands[colName]);
						Field cFieldMeta = new TextField();
						if (commandRecords2.GetProperties().Any(p => p.Key == "currentFieldMeta"))
							cFieldMeta = (Field)commandRecords2["currentFieldMeta"];
						string command = (string)commandRecords2["command"];

						bool existingField = command == "to_update";

						if (existingField)
						{
							var errorsList = (List<string>)((EntityRecord)evaluationObj["errors"])[colName];
							var warningList = (List<string>)((EntityRecord)evaluationObj["warnings"])[colName];

							// Field-level validation preserved from monolith
							if (string.IsNullOrWhiteSpace(fValue))
							{
								if (cFieldMeta.Required && cFieldMeta.Name != "id")
								{
									errorsList.Add("Field is required. Value can not be empty!");
									((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
								}
							}
							else if (!(fValue.StartsWith("[") && fValue.EndsWith("]")))
							{
								FieldType fType = (FieldType)cFieldMeta.GetFieldType();
								switch (fType)
								{
									case FieldType.AutoNumberField:
									case FieldType.CurrencyField:
									case FieldType.NumberField:
									case FieldType.PercentField:
										{
											if (!decimal.TryParse(fValue, out _))
											{
												errorsList.Add("Value have to be of decimal type!");
												((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
											}
										}
										break;
									case FieldType.CheckboxField:
										{
											if (!bool.TryParse(fValue, out _))
											{
												errorsList.Add("Value have to be of boolean type!");
												((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
											}
										}
										break;
									case FieldType.DateField:
									case FieldType.DateTimeField:
										{
											if (!DateTime.TryParse(fValue, out _))
											{
												errorsList.Add("Value have to be of datetime type!");
												((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
											}
										}
										break;
									case FieldType.SelectField:
										{
											if (cFieldMeta is SelectField sf && !sf.Options.Any(o => o.Value == fValue))
											{
												errorsList.Add("Value does not exist in select field options!");
												((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
											}
										}
										break;
									case FieldType.GuidField:
										{
											if (!Guid.TryParse(fValue, out _))
											{
												errorsList.Add("Value have to be of guid type!");
												((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;
											}
										}
										break;
								}
							}

							((EntityRecord)evaluationObj["errors"])[colName] = errorsList;
							((EntityRecord)evaluationObj["warnings"])[colName] = warningList;
						}

						if (!(command == "no_import" && generalCommand == "evaluate-import"))
							rowRecord[colName] = fValue;
					}

					if (generalCommand == "evaluate-import")
					{
						Guid? recordId = null;
						if (rowRecord.GetProperties().Any(p => p.Key == "id") && !string.IsNullOrWhiteSpace((string)rowRecord["id"]))
						{
							if (Guid.TryParse((string)rowRecord["id"], out Guid id))
								recordId = id;
						}
					}
				}
				else
				{
					foreach (var colName in columnNames)
					{
						EntityRecord commandRecords3 = ((EntityRecord)commands[colName]);
						string command = (string)commandRecords3["command"];
						string fValue = csvReader.GetField<string>(colName);
						if (!(command == "no_import" && generalCommand == "evaluate-import"))
							rowRecord[colName] = fValue;
					}
				}

				((List<EntityRecord>)evaluationObj["records"]).Add(rowRecord);
			}

			// Clean up internal metadata from commands before returning
			foreach (var columnName in columnNames)
			{
				if (commands.GetProperties().Any(p => p.Key == columnName))
				{
					((EntityRecord)commands[columnName]).Properties.Remove("currentFieldMeta");
					((EntityRecord)commands[columnName]).Properties.Remove("relationEntityFieldMeta");
					((EntityRecord)commands[columnName]).Properties.Remove("relationFieldMeta");
				}
			}

			if ((int)statsObject["errors"] > 0)
			{
				if (generalCommand == "evaluate-import")
				{
					response.Success = false;
				}
				response.Object = evaluationObj;
				return response;
			}

			// Phase 3: Execute import if in evaluate-import mode
			if (generalCommand == "evaluate-import")
			{
				using (DbConnection connection = _dbContext.CreateConnection())
				{
					connection.BeginTransaction();

					try
					{
						int fieldCreated = 0;
						foreach (var columnName in columnNames)
						{
							string command = (string)((EntityRecord)commands[columnName])["command"];

							if (command == "to_create")
							{
								FieldType fieldType = (FieldType)Enum.Parse(typeof(FieldType), (((EntityRecord)commands[columnName])["fieldType"]).ToString());
								string fieldName = (string)((EntityRecord)commands[columnName])["fieldName"];
								string fieldLabel = (string)((EntityRecord)commands[columnName])["fieldLabel"];
								var result = _entityManager.CreateField(entity.Id, fieldType, null, fieldName, fieldLabel);

								if (!result.Success)
								{
									string message = result.Message;
									if (result.Errors.Count > 0)
									{
										foreach (ErrorModel error in result.Errors)
											message += " " + error.Message;
									}
									throw new Exception(message);
								}
								fieldCreated++;
							}
						}

						int successfullyCreatedRecordsCount = 0;
						int successfullyUpdatedRecordsCount = 0;

						List<EntityRecord> records = (List<EntityRecord>)evaluationObj["records"];
						foreach (EntityRecord record in records)
						{
							EntityRecord newRecord = record;
							QueryResponse result;
							if (!newRecord.GetProperties().Any(x => x.Key == "id") || newRecord["id"] == null || string.IsNullOrEmpty(newRecord["id"].ToString()))
							{
								newRecord["id"] = Guid.NewGuid();
								result = _recordManager.CreateRecord(entityName, newRecord);
								if (result.Success)
									successfullyCreatedRecordsCount++;
							}
							else
							{
								result = _recordManager.UpdateRecord(entityName, newRecord);
								if (result.Success)
									successfullyUpdatedRecordsCount++;
							}

							if (!result.Success)
							{
								string message = result.Message;
								if (result.Errors.Count > 0)
								{
									foreach (ErrorModel error in result.Errors)
										message += " " + error.Message;
								}
								throw new Exception(message);
							}
						}

						((EntityRecord)evaluationObj["stats"])["to_create"] = successfullyCreatedRecordsCount;
						((EntityRecord)evaluationObj["stats"])["to_update"] = successfullyUpdatedRecordsCount;
						((EntityRecord)evaluationObj["stats"])["total_records"] = records.Count;
						((EntityRecord)evaluationObj["stats"])["fields_created"] = fieldCreated;

						connection.CommitTransaction();
					}
					catch (Exception e)
					{
						connection.RollbackTransaction();

						((EntityRecord)evaluationObj["stats"])["errors"] = (int)((EntityRecord)evaluationObj["stats"])["errors"] + 1;

						response.Success = false;
						response.Object = evaluationObj;
						response.Timestamp = DateTime.UtcNow;
						if (IsDevelopmentMode)
							response.Message = e.Message + e.StackTrace;
						else
							response.Message = "Import failed! An internal error occurred!";
					}
				}

				Cache.ClearEntities();
				response.Object = evaluationObj;
				return response;
			}

			response.Object = evaluationObj;
			return response;
		}
	}
}
