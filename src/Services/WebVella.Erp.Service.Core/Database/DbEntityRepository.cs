using Newtonsoft.Json;
using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Core.Database
{
	/// <summary>
	/// Persists entity metadata documents in the <c>entities</c> table (id + json) and manages
	/// the physical record tables (<c>rec_*</c>): creates/updates/drops tables, manages field
	/// columns, and handles automatic created_by/modified_by relation creation.
	///
	/// Scoped to the Core service database (<c>erp_core</c>) via <see cref="CoreDbContext"/>.
	///
	/// Adapted from the monolith's <c>WebVella.Erp.Database.DbEntityRepository</c>:
	///   - Namespace changed: <c>WebVella.Erp.Database</c> → <c>WebVella.Erp.Service.Core.Database</c>
	///   - <c>DbContext</c> replaced with <see cref="CoreDbContext"/> (service-scoped ambient context)
	///   - All <c>DbContext.Current.CreateConnection()</c> calls replaced with
	///     <c>CurrentContext.CreateConnection()</c>
	///   - Imports updated to SharedKernel types (<see cref="DbRepository"/>, <see cref="DbConnection"/>,
	///     <see cref="DbParameter"/>, etc.)
	///   - All business logic, SQL strings, exception messages, JSON serialization settings,
	///     and <see cref="Cache.Clear()"/> patterns preserved exactly.
	/// </summary>
	public class DbEntityRepository
	{
		/// <summary>
		/// Prefix for physical record tables. Entity "account" → table "rec_account".
		/// Preserved from monolith — used by all record CRUD operations.
		/// </summary>
		internal const string RECORD_COLLECTION_PREFIX = "rec_";

		private CoreDbContext suppliedContext = null;

		/// <summary>
		/// Gets or sets the ambient database context for this repository.
		/// Falls back to <see cref="CoreDbContext.Current"/> when no explicit context is supplied.
		/// </summary>
		public CoreDbContext CurrentContext
		{
			get
			{
				if (suppliedContext != null)
					return suppliedContext;
				else
					return CoreDbContext.Current;
			}
			set
			{
				suppliedContext = value;
			}
		}

		/// <summary>
		/// Creates a new <see cref="DbEntityRepository"/> bound to the specified
		/// <see cref="CoreDbContext"/>. When null, the repository will use
		/// <see cref="CoreDbContext.Current"/> as the ambient context.
		/// </summary>
		/// <param name="currentContext">The Core service database context.</param>
		public DbEntityRepository(CoreDbContext currentContext)
		{
			suppliedContext = currentContext;
		}

		/// <summary>
		/// Creates a new entity: persists metadata as JSON in the <c>entities</c> table,
		/// creates the physical <c>rec_{entityName}</c> table with columns for each field,
		/// and optionally auto-creates <c>user_{entityName}_created_by</c> and
		/// <c>user_{entityName}_modified_by</c> OneToMany relations.
		///
		/// <para><b>Business rules preserved from monolith:</b></para>
		/// <list type="bullet">
		///   <item>Entity JSON uses <c>TypeNameHandling.Auto</c> for polymorphic field serialization</item>
		///   <item>Table and columns created via <see cref="DbRepository"/> DDL helpers</item>
		///   <item>User audit relations auto-created for non-User entities when <paramref name="createOnlyIdField"/> is false</item>
		///   <item>Existing relations with same name are deleted before re-creation</item>
		///   <item>Deterministic relation IDs via <paramref name="sysldDictionary"/> when provided</item>
		///   <item>Transaction wrapping with rollback on failure</item>
		///   <item><see cref="Cache.Clear()"/> called in finally block on all paths</item>
		/// </list>
		/// </summary>
		/// <param name="entity">The entity metadata document to persist.</param>
		/// <param name="sysldDictionary">
		/// Optional dictionary mapping relation names to deterministic GUIDs for system entity provisioning.
		/// </param>
		/// <param name="createOnlyIdField">
		/// When true, skips automatic user relation creation (used during bootstrap when User entity
		/// does not yet exist). When false, creates audit trail relations.
		/// </param>
		/// <returns>True if the entity was created successfully; false on caught exception.</returns>
		public bool Create(DbEntity entity, Dictionary<string, Guid> sysldDictionary = null, bool createOnlyIdField = true)
		{
			try
			{
				using (DbConnection con = CurrentContext.CreateConnection())
				{
					con.BeginTransaction();

					try
					{
						List<DbParameter> parameters = new List<DbParameter>();

						JsonSerializerSettings settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };

						DbParameter parameterId = new DbParameter();
						parameterId.Name = "id";
						parameterId.Value = entity.Id;
						parameterId.Type = NpgsqlDbType.Uuid;
						parameters.Add(parameterId);

						DbParameter parameterJson = new DbParameter();
						parameterJson.Name = "json";
						parameterJson.Value = JsonConvert.SerializeObject(entity, settings);
						parameterJson.Type = NpgsqlDbType.Json;
						parameters.Add(parameterJson);

						string tableName = RECORD_COLLECTION_PREFIX + entity.Name;

						DbRepository.CreateTable(tableName);
						foreach (var field in entity.Fields)
						{
							bool isPrimaryKey = field.Name.ToLowerInvariant() == "id";
							FieldType fieldType = field.GetFieldType();
							DbRepository.CreateColumn(tableName, field);
						}

						bool result = DbRepository.InsertRecord("entities", parameters);

						if (!result)
							throw new Exception("Entity record was not added!");

						if (entity.Id != SystemIds.UserEntityId && createOnlyIdField == false)
						{
							DbEntity userEntity = Read(SystemIds.UserEntityId);

							DbRelationRepository relRep = new DbRelationRepository(CurrentContext);

							string createdByRelationName = $"user_{entity.Name}_created_by";
							string modifiedByRelationName = $"user_{entity.Name}_modified_by";

							Guid createdByRelationId = Guid.NewGuid();
							if (sysldDictionary != null && sysldDictionary.ContainsKey(createdByRelationName))
								createdByRelationId = sysldDictionary[createdByRelationName];

							Guid modifiedByRelationId = Guid.NewGuid();
							if (sysldDictionary != null && sysldDictionary.ContainsKey(modifiedByRelationName))
								modifiedByRelationId = sysldDictionary[modifiedByRelationName];

							List<DbEntityRelation> relationList = relRep.Read();
							DbEntityRelation tempRel = relationList.FirstOrDefault(r => r.Name == createdByRelationName);
							if (tempRel != null)
							{
								createdByRelationId = tempRel.Id;
								relRep.Delete(createdByRelationId);
							}
							tempRel = relationList.FirstOrDefault(r => r.Name == modifiedByRelationName);
							if (tempRel != null)
							{
								modifiedByRelationId = tempRel.Id;
								relRep.Delete(modifiedByRelationId);
							}

							DbEntityRelation createdByRelation = new DbEntityRelation();
							createdByRelation.Id = createdByRelationId;
							createdByRelation.Name = createdByRelationName;
							createdByRelation.Label = $"user<-[1]:[m]->{entity.Name}.created_by";
							createdByRelation.RelationType = EntityRelationType.OneToMany;
							createdByRelation.OriginEntityId = SystemIds.UserEntityId;
							createdByRelation.OriginFieldId = userEntity.Fields.Single(f => f.Name == "id").Id;
							createdByRelation.TargetEntityId = entity.Id;
							createdByRelation.TargetFieldId = entity.Fields.Single(f => f.Name == "created_by").Id;
							{
								bool res = relRep.Create(createdByRelation);
								if (!res)
									throw new Exception("Creation of relation between User and Area entities failed!");
							}

							DbEntityRelation modifiedByRelation = new DbEntityRelation();
							modifiedByRelation.Id = modifiedByRelationId;
							modifiedByRelation.Name = modifiedByRelationName;
							modifiedByRelation.Label = $"user<-[1]:[m]->{entity.Name}.last_modified_by";
							modifiedByRelation.RelationType = EntityRelationType.OneToMany;
							modifiedByRelation.OriginEntityId = SystemIds.UserEntityId;
							modifiedByRelation.OriginFieldId = userEntity.Fields.Single(f => f.Name == "id").Id;
							modifiedByRelation.TargetEntityId = entity.Id;
							modifiedByRelation.TargetFieldId = entity.Fields.Single(f => f.Name == "last_modified_by").Id;
							{
								bool res = relRep.Create(modifiedByRelation);
								if (!res)
									throw new Exception($"Creation of relation between User and {entity.Name} entities failed!");
							}
						}
						con.CommitTransaction();

						return result;
					}
					catch (Exception)
					{
						con.RollbackTransaction();
					}
				}
				return false;
			}
			finally
			{
				Cache.Clear();
			}
		}

		/// <summary>
		/// Updates an existing entity's JSON metadata in the <c>entities</c> table.
		/// Uses <c>TypeNameHandling.Auto</c> for polymorphic field serialization.
		/// Cache is invalidated via <see cref="Cache.Clear()"/> in the finally block.
		/// </summary>
		/// <param name="entity">The entity metadata document with updated state.</param>
		/// <returns>True if at least one row was updated; false otherwise.</returns>
		public bool Update(DbEntity entity)
		{
			try
			{
				using (DbConnection con = CurrentContext.CreateConnection())
				{
					NpgsqlCommand command = con.CreateCommand("UPDATE entities SET json=@json WHERE id=@id;");

					JsonSerializerSettings settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };

					var parameter = command.CreateParameter() as NpgsqlParameter;
					parameter.ParameterName = "json";
					parameter.Value = JsonConvert.SerializeObject(entity, settings);
					parameter.NpgsqlDbType = NpgsqlDbType.Json;
					command.Parameters.Add(parameter);

					var parameterId = command.CreateParameter() as NpgsqlParameter;
					parameterId.ParameterName = "id";
					parameterId.Value = entity.Id;
					parameterId.NpgsqlDbType = NpgsqlDbType.Uuid;
					command.Parameters.Add(parameterId);


					return command.ExecuteNonQuery() > 0;
				}
			}
			finally
			{
				Cache.Clear();
			}
		}

		/// <summary>
		/// Reads a single entity metadata document by its <paramref name="entityId"/>.
		/// Delegates to <see cref="Read()"/> and filters by ID.
		/// </summary>
		/// <param name="entityId">The GUID of the entity to read.</param>
		/// <returns>The matching <see cref="DbEntity"/>, or null if not found.</returns>
		public DbEntity Read(Guid entityId)
		{
			List<DbEntity> entities = Read();
			return entities.FirstOrDefault(e => e.Id == entityId);
		}

		/// <summary>
		/// Reads a single entity metadata document by its <paramref name="entityName"/>
		/// (case-insensitive comparison). Delegates to <see cref="Read()"/> and filters.
		/// </summary>
		/// <param name="entityName">The name of the entity to read.</param>
		/// <returns>The matching <see cref="DbEntity"/>, or null if not found.</returns>
		public DbEntity Read(string entityName)
		{
			List<DbEntity> entities = Read();
			return entities.FirstOrDefault(e => e.Name.ToLowerInvariant() == entityName.ToLowerInvariant());
		}

		/// <summary>
		/// Reads all entity metadata documents from the <c>entities</c> table.
		/// Deserializes JSON with <c>TypeNameHandling.Auto</c>, <c>NullValueHandling.Ignore</c>,
		/// and <c>MissingMemberHandling.Ignore</c> for backward compatibility with evolving
		/// entity schemas. Uses <see cref="DecimalToIntFormatConverter"/> to handle
		/// decimal-to-int conversion during deserialization of integer fields.
		/// </summary>
		/// <returns>A list of all entity metadata documents, or an empty list if none exist.</returns>
		public List<DbEntity> Read()
		{
			using (DbConnection con = CurrentContext.CreateConnection())
			{
				NpgsqlCommand command = con.CreateCommand("SELECT json FROM entities;");

				using (NpgsqlDataReader reader = command.ExecuteReader())
				{

					JsonSerializerSettings settings = new JsonSerializerSettings
					{
						TypeNameHandling = TypeNameHandling.Auto,
						// ReadAhead is required because AutoMapper-produced DbEntity JSON may place
						// the $type discriminator after regular properties (e.g., id, name).
						// Default MetadataPropertyHandling expects $type first, causing
						// "cannot instantiate abstract DbBaseField" errors on deserialization.
						MetadataPropertyHandling = MetadataPropertyHandling.ReadAhead,
						NullValueHandling = NullValueHandling.Ignore,
						MissingMemberHandling = MissingMemberHandling.Ignore,
					};
					settings.Converters.Add(new DecimalToIntFormatConverter());
					List<DbEntity> entities = new List<DbEntity>();
					while (reader.Read())
					{
						DbEntity entity = JsonConvert.DeserializeObject<DbEntity>(reader[0].ToString(), settings);
						entities.Add(entity);
					}



					reader.Close();
					return entities;
				}
			}
		}

		/// <summary>
		/// Custom <see cref="JsonConverter"/> that converts decimal values to integers during
		/// deserialization. Required for backward compatibility with entity metadata JSON that
		/// may contain integer field values serialized as decimals (e.g., <c>1.0</c> instead of <c>1</c>).
		///
		/// <para>Write is not supported — this converter is read-only.</para>
		/// </summary>
		public class DecimalToIntFormatConverter : JsonConverter
		{
			public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
			{
				throw new NotImplementedException();
			}

			public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
			{
				if (objectType == typeof(int))
				{
					return Convert.ToInt32(reader.Value.ToString());
				}

				return reader.Value;
			}

			public override bool CanConvert(Type objectType)
			{
				return objectType == typeof(int);
			}
		}


		/// <summary>
		/// Deletes an entity: removes all associated relations, drops the physical
		/// <c>rec_{entityName}</c> table, and removes the metadata row from <c>entities</c>.
		///
		/// <para><b>Business rules preserved from monolith:</b></para>
		/// <list type="bullet">
		///   <item>All relations where this entity is origin or target are deleted first</item>
		///   <item>Both metadata deletion and table drop in a single SQL command within a transaction</item>
		///   <item>Transaction rollback on any failure</item>
		///   <item><see cref="Cache.Clear()"/> called in finally block on all paths</item>
		/// </list>
		/// </summary>
		/// <param name="entityId">The GUID of the entity to delete.</param>
		/// <returns>True if at least one row was affected; false otherwise.</returns>
		public bool Delete(Guid entityId)
		{
			try
			{
				var relRepository = new DbRelationRepository(CurrentContext);
				var relations = relRepository.Read();
				var entityRelations = relations.Where(x => x.TargetEntityId == entityId || x.OriginEntityId == entityId);

				using (DbConnection con = CurrentContext.CreateConnection())
				{
					try
					{
						con.BeginTransaction();

						foreach (var relation in entityRelations)
							relRepository.Delete(relation.Id);

						var entity = Read(entityId);

						NpgsqlCommand command = con.CreateCommand("DELETE FROM entities WHERE id=@id; DROP TABLE rec_" + entity.Name);

						var parameterId = command.CreateParameter() as NpgsqlParameter;
						parameterId.ParameterName = "id";
						parameterId.Value = entityId;
						parameterId.NpgsqlDbType = NpgsqlDbType.Uuid;
						command.Parameters.Add(parameterId);

						var result = command.ExecuteNonQuery() > 0;

						con.CommitTransaction();

						return result;
					}
					catch
					{
						con.RollbackTransaction();
						throw;
					}
				}
			}
			finally
			{
				Cache.Clear();
			}
		}
	}
}
