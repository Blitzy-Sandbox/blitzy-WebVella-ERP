using Newtonsoft.Json.Linq;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.SharedKernel.Eql
{
	/// <summary>
	/// Execution facade that compiles EQL text via <see cref="EqlBuilder"/>, binds
	/// parameters, executes the generated SQL via <see cref="NpgsqlCommand"/> with a
	/// 600-second CommandTimeout (AAP 0.8.3 Performance Baseline), and materializes
	/// results into an <see cref="EntityRecordList"/>.
	/// <para>
	/// Migrated from the monolith's <c>WebVella.Erp.Eql.EqlCommand</c> (321 lines).
	/// Namespace changed from <c>WebVella.Erp.Eql</c> to <c>WebVella.Erp.SharedKernel.Eql</c>.
	/// </para>
	/// <para>
	/// Service-specific dependencies that were concrete in the monolith are now injectable:
	/// <list type="bullet">
	///   <item><see cref="IEqlEntityProvider"/> — replaces <c>new EntityManager()</c></item>
	///   <item><see cref="IEqlRelationProvider"/> — replaces <c>new EntityRelationManager()</c></item>
	///   <item><see cref="IEqlSecurityProvider"/> — replaces <c>SecurityContext.HasEntityPermission()</c></item>
	///   <item><see cref="IEqlHookProvider"/> — replaces <c>RecordHookManager.*</c></item>
	///   <item><see cref="IEqlFieldValueExtractor"/> — replaces <c>DbRecordRepository.ExtractFieldValue()</c></item>
	/// </list>
	/// All providers are optional (nullable) for backward compatibility; null providers
	/// result in no-op behavior via null-conditional operators.
	/// </para>
	/// </summary>
	public class EqlCommand
	{
		/// <summary>
		/// Eql text
		/// </summary>
		public string Text { get; set; }

		/// <summary>
		/// DbConnection object
		/// </summary>
		public DbConnection Connection { get; private set; }

		/// <summary>
		/// NpgsqlConnection object
		/// </summary>
		public NpgsqlConnection NpgConnection { get; private set; }

		/// <summary>
		/// NpgsqlConnection object
		/// </summary>
		public NpgsqlTransaction NpgTransaction { get; private set; }

		/// <summary>
		/// List of EqlParameters
		/// </summary>
		public List<EqlParameter> Parameters { get; private set; } = new List<EqlParameter>();

		/// <summary>
		/// EqlSettings object
		/// </summary>
		public EqlSettings Settings { get; private set; } = new EqlSettings();

		private IDbContext suppliedContext = null;

		/// <summary>
		/// Gets or sets the database context used for connection creation.
		/// Falls back to <see cref="DbContextAccessor.Current"/> when no context is explicitly supplied.
		/// Preserves the monolith's ambient <c>DbContext.Current</c> pattern via <see cref="DbContextAccessor"/>.
		/// </summary>
		public IDbContext CurrentContext
		{
			get
			{
				if (suppliedContext != null)
					return suppliedContext;
				else
					return DbContextAccessor.Current;
			}
			set
			{
				suppliedContext = value;
			}
		}

		/// <summary>
		/// Global default entity metadata provider used by constructors that do not
		/// accept an explicit provider parameter (e.g. the <c>List&lt;EqlParameter&gt;</c>
		/// overloads preserved from the monolith).  Each microservice sets this once
		/// during startup so that callers like SecurityManager.GetUser() work without
		/// code changes.  Thread-safe: set once at startup, read-only thereafter.
		/// </summary>
		public static IEqlEntityProvider DefaultEntityProvider { get; set; }

		/// <summary>
		/// Global default relation metadata provider — see <see cref="DefaultEntityProvider"/>.
		/// </summary>
		public static IEqlRelationProvider DefaultRelationProvider { get; set; }

		/// <summary>
		/// Global default field value extractor — see <see cref="DefaultEntityProvider"/>.
		/// Converts raw JToken values from PostgreSQL JSON results to typed .NET values.
		/// </summary>
		public static IEqlFieldValueExtractor DefaultFieldValueExtractor { get; set; }

		/// <summary>
		/// Global default security provider — see <see cref="DefaultEntityProvider"/>.
		/// Checks entity-level read permissions during result materialization.
		/// </summary>
		public static IEqlSecurityProvider DefaultSecurityProvider { get; set; }

		/// <summary>
		/// Provides entity metadata for EQL building and result materialization.
		/// Replaces monolith's <c>new EntityManager()</c> in <c>ConvertJObjectToEntityRecord</c>.
		/// </summary>
		private readonly IEqlEntityProvider _entityProvider;

		/// <summary>
		/// Provides entity relation metadata for EQL SQL generation.
		/// Passed to <see cref="EqlBuilder"/> for relation traversal ($/$$) SQL generation.
		/// </summary>
		private readonly IEqlRelationProvider _relationProvider;

		/// <summary>
		/// Provides entity-level permission checking during result materialization.
		/// Replaces monolith's <c>SecurityContext.HasEntityPermission()</c>.
		/// </summary>
		private readonly IEqlSecurityProvider _securityProvider;

		/// <summary>
		/// Provides pre/post-search hook execution for EQL queries.
		/// Replaces monolith's <c>RecordHookManager</c> calls.
		/// </summary>
		private readonly IEqlHookProvider _hookProvider;

		/// <summary>
		/// Extracts typed field values from raw JSON database results.
		/// Replaces monolith's <c>DbRecordRepository.ExtractFieldValue()</c>.
		/// </summary>
		private readonly IEqlFieldValueExtractor _fieldValueExtractor;

		/// <summary>
		/// Creates command with EQL text and optional injectable providers.
		/// Providers default to null for backward compatibility; null providers
		/// result in no-op behavior via null-conditional operators in execution methods.
		/// </summary>
		/// <param name="text">The EQL query text.</param>
		/// <param name="entityProvider">Optional entity metadata provider.</param>
		/// <param name="securityProvider">Optional security permission provider.</param>
		/// <param name="hookProvider">Optional search hook provider.</param>
		/// <param name="fieldValueExtractor">Optional field value extractor.</param>
		/// <param name="relationProvider">Optional relation metadata provider for EqlBuilder.</param>
		/// <param name="parameters">Optional EQL parameters for parameterized queries.</param>
		public EqlCommand(string text,
			IEqlEntityProvider entityProvider = null,
			IEqlSecurityProvider securityProvider = null,
			IEqlHookProvider hookProvider = null,
			IEqlFieldValueExtractor fieldValueExtractor = null,
			IEqlRelationProvider relationProvider = null,
			params EqlParameter[] parameters)
		{
			Text = text;

			if (string.IsNullOrWhiteSpace(text))
				throw new ArgumentException("Command text cannot be null or empty.");

			_entityProvider = entityProvider;
			_relationProvider = relationProvider;
			_securityProvider = securityProvider;
			_hookProvider = hookProvider;
			_fieldValueExtractor = fieldValueExtractor;

			NpgConnection = null;

			Connection = null;

			if (parameters != null && parameters.Length > 0)
				Parameters.AddRange(parameters);
		}

		/// <summary>
		/// Creates command with EQL text, settings, and optional injectable providers.
		/// </summary>
		public EqlCommand(string text, EqlSettings settings,
			IEqlEntityProvider entityProvider = null,
			IEqlSecurityProvider securityProvider = null,
			IEqlHookProvider hookProvider = null,
			IEqlFieldValueExtractor fieldValueExtractor = null,
			IEqlRelationProvider relationProvider = null,
			params EqlParameter[] parameters)
			: this(text, entityProvider, securityProvider, hookProvider, fieldValueExtractor, relationProvider, parameters)
		{
			if (settings != null)
				Settings = settings;
		}

		/// <summary>
		/// Creates command with EQL text, explicit database context, and optional injectable providers.
		/// </summary>
		public EqlCommand(string text, IDbContext currentContext,
			IEqlEntityProvider entityProvider = null,
			IEqlSecurityProvider securityProvider = null,
			IEqlHookProvider hookProvider = null,
			IEqlFieldValueExtractor fieldValueExtractor = null,
			IEqlRelationProvider relationProvider = null,
			params EqlParameter[] parameters)
			: this(text, entityProvider, securityProvider, hookProvider, fieldValueExtractor, relationProvider, parameters)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
		}

		/// <summary>
		/// Creates command with EQL text, explicit database context, settings, and optional injectable providers.
		/// </summary>
		public EqlCommand(string text, IDbContext currentContext, EqlSettings settings,
			IEqlEntityProvider entityProvider = null,
			IEqlSecurityProvider securityProvider = null,
			IEqlHookProvider hookProvider = null,
			IEqlFieldValueExtractor fieldValueExtractor = null,
			IEqlRelationProvider relationProvider = null,
			params EqlParameter[] parameters)
			: this(text, currentContext, entityProvider, securityProvider, hookProvider, fieldValueExtractor, relationProvider, parameters)
		{
			if (settings != null)
				Settings = settings;
		}

		/// <summary>
		/// Creates command with EQL text, optional parameter list, and optional database context.
		/// Preserves monolith's <c>List&lt;EqlParameter&gt;</c> constructor overload.
		/// </summary>
		public EqlCommand(string text, List<EqlParameter> parameters = null, IDbContext currentContext = null)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
			Text = text;

			if (string.IsNullOrWhiteSpace(text))
				throw new ArgumentException("Command text cannot be null or empty.");

			// Fall back to the static default providers so that callers preserved
			// from the monolith (SecurityManager, RecordManager, etc.) that do not
			// pass explicit providers still get entity/relation metadata resolution,
			// field value extraction, and security permission checks.
			_entityProvider = DefaultEntityProvider;
			_relationProvider = DefaultRelationProvider;
			_fieldValueExtractor = DefaultFieldValueExtractor;
			_securityProvider = DefaultSecurityProvider;

			NpgConnection = null;

			Connection = null;

			if (parameters != null)
				Parameters.AddRange(parameters);
		}

		/// <summary>
		/// Creates command with EQL text, settings, optional parameter list, and optional database context.
		/// </summary>
		public EqlCommand(string text, EqlSettings settings, List<EqlParameter> parameters = null, IDbContext currentContext = null)
		: this(text, parameters, currentContext)
		{
			if (settings != null)
				Settings = settings;
		}

		/// <summary>
		/// Creates command with EQL text, explicit DbConnection, and optional parameters.
		/// </summary>
		/// <param name="text">The EQL query text.</param>
		/// <param name="connection">The DbConnection to use for query execution.</param>
		/// <param name="parameters">Optional list of EQL parameters.</param>
		/// <param name="currentContext">Optional database context override.</param>
		public EqlCommand(string text, DbConnection connection, List<EqlParameter> parameters = null, IDbContext currentContext = null)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
			Text = text;

			if (string.IsNullOrWhiteSpace(text))
				throw new ArgumentException("Command text cannot be null or empty.");

			NpgConnection = null;

			Connection = connection;

			if (connection == null)
				throw new ArgumentNullException(nameof(connection));

			if (parameters != null)
				Parameters.AddRange(parameters);
		}

		/// <summary>
		/// Creates command with EQL text, raw NpgsqlConnection, optional transaction, and optional parameters.
		/// </summary>
		/// <param name="text">The EQL query text.</param>
		/// <param name="connection">The NpgsqlConnection to use for query execution.</param>
		/// <param name="transaction">Optional NpgsqlTransaction for transactional execution.</param>
		/// <param name="parameters">Optional list of EQL parameters.</param>
		/// <param name="currentContext">Optional database context override.</param>
		public EqlCommand(string text, NpgsqlConnection connection, NpgsqlTransaction transaction = null, List<EqlParameter> parameters = null, IDbContext currentContext = null)
		{
			if (currentContext != null)
				suppliedContext = currentContext;

			Text = text;

			if (string.IsNullOrWhiteSpace(text))
				throw new ArgumentException("Command text cannot be null or empty.");

			Connection = null;

			NpgConnection = connection;
			NpgTransaction = transaction;

			if (connection == null)
				throw new ArgumentNullException(nameof(connection));

			if (parameters != null)
				Parameters.AddRange(parameters);
		}

		/// <summary>
		/// Executes the command to database.
		/// Compiles EQL via <see cref="EqlBuilder"/>, executes the generated SQL with a
		/// 600-second CommandTimeout (AAP 0.8.3), and materializes <see cref="EntityRecordList"/> results.
		/// </summary>
		/// <returns>An <see cref="EntityRecordList"/> containing the query results with TotalCount.</returns>
		public EntityRecordList Execute()
		{
			EqlBuilder eqlBuilder = new EqlBuilder(Text, CurrentContext, Settings, _entityProvider, _relationProvider, _hookProvider);
			var eqlBuildResult = eqlBuilder.Build(Parameters);

			if (eqlBuildResult.Errors.Count > 0)
				throw new EqlException(eqlBuildResult.Errors);

			if (CurrentContext == null)
				throw new EqlException("DbContext need to be created.");

			EntityRecordList result = new EntityRecordList();

			DataTable dt = new DataTable();
			var npgsParameters = eqlBuildResult.Parameters.Select(x => x.ToNpgsqlParameter()).ToList();
			NpgsqlCommand command = null;

			bool hooksExists = _hookProvider?.ContainsAnyHooksForEntity(eqlBuildResult.FromEntity.Name) ?? false;

			if (Connection != null)
				command = Connection.CreateCommand(eqlBuildResult.Sql, parameters: npgsParameters);
			else if (NpgConnection != null)
			{
				if (NpgTransaction != null)
					command = new NpgsqlCommand(eqlBuildResult.Sql, NpgConnection, NpgTransaction);
				else
					command = new NpgsqlCommand(eqlBuildResult.Sql, NpgConnection);
				command.Parameters.AddRange(npgsParameters.ToArray());
			}
			else
			{
				if (CurrentContext == null)
					throw new EqlException("DbContext needs to be initialized before using EqlCommand without supplying connection.");

				using (var connection = CurrentContext.CreateConnection())
				{
					command = connection.CreateCommand(eqlBuildResult.Sql, parameters: npgsParameters);
					command.CommandTimeout = 600;
					new NpgsqlDataAdapter(command).Fill(dt);

					foreach (DataRow dr in dt.Rows)
					{
						var jObj = JObject.Parse((string)dr[0]);
						if (result.TotalCount == 0 && jObj.ContainsKey("___total_count___"))
							result.TotalCount = int.Parse(((JValue)jObj["___total_count___"]).ToString());
						result.Add(ConvertJObjectToEntityRecord(jObj, eqlBuildResult.Meta));
					}

					if (hooksExists)
					{
						_hookProvider?.ExecutePostSearchRecordHooks(eqlBuildResult.FromEntity.Name, result);
					}

					return result;
				}
			}

			command.CommandTimeout = 600;
			new NpgsqlDataAdapter(command).Fill(dt);
			foreach (DataRow dr in dt.Rows)
			{
				var jObj = JObject.Parse((string)dr[0]);
				if (result.TotalCount == 0 && jObj.ContainsKey("___total_count___"))
					result.TotalCount = int.Parse(((JValue)jObj["___total_count___"]).ToString());
				result.Add(ConvertJObjectToEntityRecord(jObj, eqlBuildResult.Meta));
			}

			return result;
		}

		/// <summary>
		/// Gets field meta
		/// </summary>
		/// <returns>List of field metadata entries describing each field in the EQL query projection.</returns>
		public List<EqlFieldMeta> GetMeta()
		{
			EqlBuilder eqlBuilder = new EqlBuilder(Text, CurrentContext, Settings, _entityProvider, _relationProvider, _hookProvider);
			var eqlBuildResult = eqlBuilder.Build(Parameters);

			if (eqlBuildResult.Errors.Count > 0)
				throw new EqlException(eqlBuildResult.Errors);

			return eqlBuildResult.Meta;
		}

		/// <summary>
		/// Gets sql
		/// </summary>
		/// <returns>The generated PostgreSQL SQL string for the EQL query.</returns>
		public string GetSql()
		{
			EqlBuilder eqlBuilder = new EqlBuilder(Text, CurrentContext, Settings, _entityProvider, _relationProvider, _hookProvider);
			var eqlBuildResult = eqlBuilder.Build(Parameters);

			if (eqlBuildResult.Errors.Count > 0)
				throw new EqlException(eqlBuildResult.Errors);

			return eqlBuildResult.Sql;
		}

		/// <summary>
		/// Converts a PostgreSQL JSON row (<see cref="JObject"/>) to an <see cref="EntityRecord"/>
		/// using field metadata for column mapping and per-entity permission checks.
		/// Preserves the monolith's recursive relation traversal pattern.
		/// </summary>
		/// <param name="jObj">The parsed JSON object from the PostgreSQL row_to_json result.</param>
		/// <param name="fieldMeta">Field metadata list for column-to-property mapping.</param>
		/// <returns>A populated <see cref="EntityRecord"/> instance.</returns>
		private EntityRecord ConvertJObjectToEntityRecord(JObject jObj, List<EqlFieldMeta> fieldMeta)
		{
			EntityRecord record = new EntityRecord();
			foreach (EqlFieldMeta meta in fieldMeta)
			{
				if (meta.Field != null)
				{
					var entity = _entityProvider?.ReadEntity(meta.Field.EntityName);
					if(entity == null)
						throw new Exception($"Entity '{meta.Field.Name}' not found");

					bool hasPermisstion = _securityProvider?.HasEntityPermission(EntityPermission.Read, entity) ?? true;
					if (!hasPermisstion)
						throw new Exception($"No access to entity '{meta.Field.EntityName}'");

					record[meta.Field.Name] = _fieldValueExtractor?.ExtractFieldValue(jObj[meta.Field.Name], meta.Field);
				}
				else if (meta.Relation != null)
				{
					List<EntityRecord> relRecords = new List<EntityRecord>();
					JArray relatedJsonRecords = jObj[meta.Name].Value<JArray>();
					foreach (JObject relatedObj in relatedJsonRecords)
						relRecords.Add(ConvertJObjectToEntityRecord(relatedObj, meta.Children));

					record[meta.Name] = relRecords;
				}
			}
			return record;
		}

		/// <summary>
		/// No-op hook provider used as a reference implementation for when no hook provider is injected.
		/// All methods return false/no-op, ensuring the EQL engine can operate without hook support.
		/// In practice, null-conditional operators (<c>_hookProvider?.</c>) provide the same behavior
		/// when <c>_hookProvider</c> is null; this class serves as documentation and potential fallback.
		/// </summary>
		private class NullEqlHookProvider : IEqlHookProvider
		{
			public bool ContainsAnyHooksForEntity(string entityName) => false;
			public void ExecutePreSearchRecordHooks(string entityName, EqlSelectNode selectNode, List<EqlError> errors) { }
			public void ExecutePostSearchRecordHooks(string entityName, EntityRecordList records) { }
		}
	}
}
