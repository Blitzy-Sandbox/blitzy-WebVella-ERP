using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Utilities;

namespace WebVella.Erp.Service.Crm.Domain.Services
{
	#region Service Interfaces for Core Platform Dependencies

	/// <summary>
	/// Abstraction for reading entity relation metadata from the Core Platform service.
	/// In production, this is implemented by a gRPC client proxy or a local wrapper
	/// delegating to the Core service's EntityRelationManager.
	/// </summary>
	public interface ICrmEntityRelationManager
	{
		/// <summary>
		/// Reads all entity relations from the Core Platform service.
		/// Returns an <see cref="EntityRelationListResponse"/> whose
		/// <c>Object</c> property contains <c>List&lt;EntityRelation&gt;</c>.
		/// </summary>
		EntityRelationListResponse Read();
	}

	/// <summary>
	/// Abstraction for reading entity metadata from the Core Platform service.
	/// In production, this is implemented by a gRPC client proxy or a local wrapper
	/// delegating to the Core service's EntityManager.
	/// </summary>
	public interface ICrmEntityManager
	{
		/// <summary>
		/// Reads all entity definitions from the Core Platform service.
		/// Returns an <see cref="EntityListResponse"/> whose
		/// <c>Object</c> property contains <c>List&lt;Entity&gt;</c>.
		/// </summary>
		EntityListResponse ReadEntities();
	}

	/// <summary>
	/// Abstraction for record CRUD operations via the Core Platform service.
	/// In production, this is implemented by a gRPC client proxy or a local wrapper
	/// delegating to the Core service's RecordManager.
	/// </summary>
	public interface ICrmRecordManager
	{
		/// <summary>
		/// Updates a record in the specified entity.
		/// </summary>
		/// <param name="entityName">Name of the entity whose record is being updated.</param>
		/// <param name="record">The patch record containing the fields to update (must include 'id').</param>
		/// <param name="executeHooks">
		/// When false, suppresses hook/event execution during the update.
		/// Critical for preventing infinite recursion when updating the x_search field,
		/// since post-update hooks trigger search index regeneration which calls this method.
		/// </param>
		/// <returns>A <see cref="QueryResponse"/> indicating success/failure with error details.</returns>
		QueryResponse UpdateRecord(string entityName, EntityRecord record, bool executeHooks = true);
	}

	#endregion

	/// <summary>
	/// CRM x_search field regeneration service. Computes a concatenated, human-readable
	/// "search index" string from a record's indexed fields and stores it into the record's
	/// <c>x_search</c> column. This denormalized search text column is used for fast
	/// filtering and searching of CRM entities.
	///
	/// <para>
	/// <b>CRM Service Scope:</b> This service handles CRM entities ONLY — account, contact,
	/// and case. Task entity search indexing belongs to the Project service and is NOT
	/// handled by this file.
	/// </para>
	///
	/// <para>
	/// The indexed field configuration per entity is NOT defined in this service — it is
	/// provided by callers (typically event subscribers that pass the appropriate
	/// <c>indexedFields</c> list for each entity type).
	/// </para>
	///
	/// <para>
	/// <b>DI Registration:</b> Register as scoped in the CRM service DI container:
	/// <code>builder.Services.AddScoped&lt;SearchService&gt;();</code>
	/// </para>
	///
	/// <para>
	/// Adapted from the monolith's <c>WebVella.Erp.Plugins.Next.Services.SearchService</c>
	/// (287 lines). Inheritance from <c>BaseService</c> removed; direct manager instantiation
	/// replaced with constructor-injected dependencies per AAP §0.5.1 and §0.6.2.
	/// All business logic preserved identically per AAP §0.8.1.
	/// </para>
	/// </summary>
	public class SearchService
	{
		private readonly ICrmEntityRelationManager _entityRelationManager;
		private readonly ICrmEntityManager _entityManager;
		private readonly ICrmRecordManager _recordManager;

		/// <summary>
		/// Initializes a new instance of <see cref="SearchService"/> with injected dependencies.
		/// </summary>
		/// <param name="entityRelationManager">Provides entity relation metadata from Core service.</param>
		/// <param name="entityManager">Provides entity metadata from Core service.</param>
		/// <param name="recordManager">Provides record update operations via Core service.</param>
		/// <exception cref="ArgumentNullException">Thrown if any parameter is null.</exception>
		public SearchService(
			ICrmEntityRelationManager entityRelationManager,
			ICrmEntityManager entityManager,
			ICrmRecordManager recordManager)
		{
			_entityRelationManager = entityRelationManager ?? throw new ArgumentNullException(nameof(entityRelationManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
		}

		/// <summary>
		/// Regenerates the <c>x_search</c> field for the specified record by querying all
		/// indexed fields (including relation-qualified fields), formatting their values
		/// into human-readable strings, and concatenating them with space delimiters.
		/// The result is persisted via an <c>UpdateRecord</c> call with hooks disabled
		/// to prevent infinite recursion.
		///
		/// <para>
		/// <b>Field Resolution Rules:</b>
		/// <list type="bullet">
		///   <item>Direct fields (no <c>$</c> prefix): validated against entity metadata; missing fields silently ignored.</item>
		///   <item>Relation fields (<c>$relation_name.field_name</c>): validated against relation metadata
		///         and related entity; invalid references silently ignored.</item>
		/// </list>
		/// </para>
		///
		/// <para>
		/// <b>Error Handling:</b> Individual field formatting errors are swallowed (intentional)
		/// to prevent one malformed value from aborting the entire indexing routine. Only the
		/// final <c>UpdateRecord</c> failure throws a <see cref="ValidationException"/>.
		/// </para>
		/// </summary>
		/// <param name="entityName">Name of the entity (e.g., "account", "contact", "case").</param>
		/// <param name="record">The record whose x_search field is being regenerated. Must contain an "id" property.</param>
		/// <param name="indexedFields">
		/// List of field names to include in the search index. Direct field names and
		/// relation-qualified fields (<c>$relation_name.field_name</c>) are supported.
		/// </param>
		/// <exception cref="Exception">Thrown if the entity is not found in metadata.</exception>
		/// <exception cref="ValidationException">Thrown if the x_search update fails.</exception>
		public void RegenSearchField(string entityName, EntityRecord record, List<string> indexedFields)
		{
			var searchIndex = "";
			var relations = _entityRelationManager.Read().Object;
			var entities = _entityManager.ReadEntities().Object;
			var currentEntity = entities.FirstOrDefault(x => x.Name == entityName);
			if (currentEntity == null)
				throw new Exception($"Search index generation failed: Entity {entityName} not found");

			// Generate request columns
			var requestColumns = new List<string>();
			foreach (var fieldName in indexedFields)
			{
				if (!fieldName.StartsWith("$"))
				{
					//Entity field
					var field = currentEntity.Fields.FirstOrDefault(x => x.Name == fieldName);
					if (field == null)
						continue; // missing fields are ignored

					requestColumns.Add(fieldName);
				}
				else
				{
					//Relation field
					var fieldNameArray = fieldName.Replace("$", "").Split(".", StringSplitOptions.RemoveEmptyEntries);
					if (fieldNameArray.Length != 2)
						continue; //currently we process only fields defined as "$relation_name.field_name"

					var relation = relations.FirstOrDefault(x => x.Name == fieldNameArray[0]);
					if (relation == null)
						continue; //missing relations are ignored

					Guid? relatedEntityId = null;
					if (relation.OriginEntityId == currentEntity.Id)
						relatedEntityId = relation.TargetEntityId;
					else if (relation.TargetEntityId == currentEntity.Id)
						relatedEntityId = relation.OriginEntityId;

					if (relatedEntityId == null)
						continue; // the defined relation does not include the current entity

					var relatedEntity = entities.FirstOrDefault(x => x.Id == relatedEntityId);

					if (relatedEntity == null)
						continue; // related entity no longer exists.Ignore

					var relatedField = relatedEntity.Fields.FirstOrDefault(x => x.Name == fieldNameArray[1]);

					if (relatedField == null)
						continue; //related field does not exist

					requestColumns.Add(fieldName);
				}
			}

			//Generate request

			var eqlCommand = $"SELECT {String.Join(",", requestColumns)} FROM {entityName} WHERE id = @recordId PAGE 1 PAGESIZE 1";
			var eqlParameters = new List<EqlParameter>() { new EqlParameter("recordId", (Guid)record["id"]) };
			var eqlResult = new EqlCommand(eqlCommand, eqlParameters).Execute();

			//After update creation or update, the record is existing
			if (eqlResult.Count > 0)
			{
				var currentRecord = eqlResult[0];
				foreach (var columnName in requestColumns)
				{
					if (!columnName.StartsWith("$"))
					{
						//Record column
						if (currentRecord.Properties.ContainsKey(columnName) && currentRecord[columnName] != null)
						{
							try
							{
								var stringValue = GetStringValue(columnName, currentEntity, currentRecord);

								if (!String.IsNullOrWhiteSpace(stringValue))
									searchIndex += stringValue + " ";
							}
							catch
							{
								//Do nothing
							}
						}
					}
					else
					{
						//Related record column
						var columnNameArray = columnName.Split(".", StringSplitOptions.RemoveEmptyEntries);
						if (columnNameArray.Length == 2)
						{
							if (currentRecord.Properties.ContainsKey(columnNameArray[0]) && currentRecord[columnNameArray[0]] != null)
							{
								try
								{
									if (currentRecord[columnNameArray[0]] is List<EntityRecord>)
									{
										var relatedRecords = (List<EntityRecord>)currentRecord[columnNameArray[0]];
										foreach (var relatedRecord in relatedRecords)
										{
											if (relatedRecord.Properties.ContainsKey(columnNameArray[1]) && relatedRecord[columnNameArray[1]] != null)
											{
												var stringValue = relatedRecord[columnNameArray[1]].ToString();
												if (!String.IsNullOrWhiteSpace(stringValue))
													searchIndex += stringValue + " ";
											}
										}
									}
									else if (currentRecord[columnNameArray[0]] is EntityRecord)
									{
										var relatedRecord = (EntityRecord)currentRecord[columnNameArray[0]];

										if (relatedRecord.Properties.ContainsKey(columnNameArray[1]) && relatedRecord[columnNameArray[1]] != null)
										{
											var stringValue = relatedRecord[columnNameArray[1]].ToString();
											if (!String.IsNullOrWhiteSpace(stringValue))
												searchIndex += stringValue + " ";
										}
									}
								}
								catch
								{
									//Do nothing
								}
							}
						}
					}
				}
			}
			else
			{
				//Do nothing, the eql should find a record if all is OK
			}

			var patchRecord = new EntityRecord();
			patchRecord["id"] = (Guid)record["id"];
			patchRecord["x_search"] = searchIndex;
			// executeHooks: false — prevents infinite recursion during x_search update.
			// The search service updates the x_search field, which would otherwise trigger
			// post-update hooks that call the search service again in an infinite loop.
			var updateRecordResult = _recordManager.UpdateRecord(entityName, patchRecord, executeHooks: false);
			if (!updateRecordResult.Success)
			{
				throw new ValidationException()
				{
					Message = updateRecordResult.Message,
					Errors = updateRecordResult.Errors.MapTo<ValidationError>()
				};
			}
		}

		/// <summary>
		/// Converts a record field value to its human-readable string representation
		/// based on the field's metadata type. Handles 10 specific field types with
		/// type-specific formatting rules, plus a default ToString() fallback.
		///
		/// <para>
		/// <b>Formatting Rules by FieldType:</b>
		/// <list type="bullet">
		///   <item><b>AutoNumberField:</b> Apply DisplayFormat with "N0" formatted decimal</item>
		///   <item><b>CurrencyField:</b> Currency code + symbol placement (before/after) + decimal digits</item>
		///   <item><b>DateField:</b> Format with field's Format string</item>
		///   <item><b>DateTimeField:</b> Format with field's Format string</item>
		///   <item><b>MultiSelectField:</b> Resolve option labels case-insensitively; handles both List&lt;string&gt; and comma-separated string</item>
		///   <item><b>PasswordField:</b> Intentionally ignored (returns empty string)</item>
		///   <item><b>NumberField:</b> Format with "N" + DecimalPlaces</item>
		///   <item><b>PercentField:</b> Format with "P" + DecimalPlaces</item>
		///   <item><b>SelectField:</b> Resolve option label case-insensitively; fallback to raw value</item>
		///   <item><b>default:</b> record[fieldName].ToString()</item>
		/// </list>
		/// </para>
		/// </summary>
		/// <param name="fieldName">Name of the field to format.</param>
		/// <param name="entity">Entity metadata containing field definitions.</param>
		/// <param name="record">The record containing the field value.</param>
		/// <returns>Human-readable string representation of the field value, or empty string if null/missing.</returns>
		private string GetStringValue(string fieldName, Entity entity, EntityRecord record)
		{
			var stringValue = "";
			if (!record.Properties.ContainsKey(fieldName) || record[fieldName] == null)
				return stringValue;

			var fieldMeta = entity.Fields.First(x => x.Name == fieldName);
			switch (fieldMeta.GetFieldType())
			{
				case FieldType.AutoNumberField:
					//Apply template
					{
						var exactMeta = (AutoNumberField)fieldMeta;
						if (!String.IsNullOrWhiteSpace(exactMeta.DisplayFormat))
							stringValue = string.Format(exactMeta.DisplayFormat, ((decimal)record[fieldName]).ToString("N0"));
					}
					break;

				case FieldType.CurrencyField:
					//as currency string
					{
						var exactMeta = (CurrencyField)fieldMeta;
						var currency = exactMeta.Currency;
						stringValue = currency.Code + " ";
						var amountString = ((decimal)record[fieldName]).ToString("N" + currency.DecimalDigits);
						if (exactMeta.Currency.SymbolPlacement == CurrencySymbolPlacement.Before)
						{
							stringValue += currency.SymbolNative + amountString;
						}
						else
						{
							stringValue += amountString + currency.SymbolNative;
						}
					}
					break;

				case FieldType.DateField:
					//Apply template
					{
						var exactMeta = (DateField)fieldMeta;
						stringValue = ((DateTime)record[fieldName]).ToString(exactMeta.Format);
					}
					break;

				case FieldType.DateTimeField:
					//Apply template
					{
						var exactMeta = (DateTimeField)fieldMeta;
						stringValue = ((DateTime)record[fieldName]).ToString(exactMeta.Format);
					}
					break;

				case FieldType.MultiSelectField:
					//option labels
					{
						var exactMeta = (MultiSelectField)fieldMeta;
						var values = new List<string>();
						var fieldValue = record[fieldName];
						if (fieldValue is List<string>)
							values = (List<string>)fieldValue;
						else if (fieldValue is string)
						{
							var fieldValueString = (string)fieldValue;
							if (fieldValueString.Contains(","))
							{
								values = fieldValueString.Split(",").ToList();
							}
							else
							{
								values.Add(fieldValueString);
							}
						}
						foreach (var value in values)
						{
							var option = exactMeta.Options.First(x => x.Value.ToLowerInvariant() == value.ToLowerInvariant());
							if (option != null)
							{
								stringValue += option.Label + " ";
							}
							else
							{
								stringValue += value + " ";
							}
						}
					}
					break;

				case FieldType.PasswordField:
					//ignore
					break;

				case FieldType.NumberField:
					{
						var exactMeta = (NumberField)fieldMeta;
						stringValue = ((decimal)record[fieldName]).ToString("N" + exactMeta.DecimalPlaces);
					}
					break;

				case FieldType.PercentField:
					//as percent, not float
					{
						var exactMeta = (PercentField)fieldMeta;
						stringValue = ((decimal)record[fieldName]).ToString("P" + exactMeta.DecimalPlaces);
					}
					break;

				case FieldType.SelectField:
					//option label
					{
						var exactMeta = (SelectField)fieldMeta;
						var value = (string)record[fieldName];
						var option = exactMeta.Options.First(x => x.Value.ToLowerInvariant() == value.ToLowerInvariant());
						if (option != null)
						{
							stringValue += option.Label + " ";
						}
						else
						{
							stringValue += value + " ";
						}
					}
					break;

				default:
					stringValue = record[fieldName].ToString();
					break;
			}

			return stringValue;
		}
	}
}
