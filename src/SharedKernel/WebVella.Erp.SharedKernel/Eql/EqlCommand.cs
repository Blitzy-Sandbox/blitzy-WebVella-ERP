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
	/// Wraps EQL query execution against PostgreSQL via NpgsqlCommand.
	/// <para>
	/// Migrated from the monolith's <c>WebVella.Erp.Eql.EqlCommand</c> (321 lines) with namespace
	/// updates for the SharedKernel. Preserves the 600-second CommandTimeout (AAP Performance
	/// Baseline Rule 16), parameterized query execution, and JSON result set mapping.
	/// </para>
	/// <para>
	/// In the microservice architecture, service-specific dependencies are injected:
	/// <list type="bullet">
	///   <item><see cref="IEntityMetadataProvider"/> — entity/relation metadata for EQL building and result mapping</item>
	///   <item><see cref="IRecordSearchHookExecutor"/> — optional hook execution (null disables hooks)</item>
	///   <item><see cref="IFieldValueExtractor"/> — typed field value extraction from JSON results</item>
	///   <item><see cref="ISecurityContextAccessor"/> — permission checking during result mapping</item>
	/// </list>
	/// </para>
	/// </summary>
	public class EqlCommand
	{
		/// <summary>
		/// EQL text to be parsed, translated to SQL, and executed.
		/// </summary>
		public string Text { get; set; }

		/// <summary>
		/// DbConnection object for execution within an existing connection context.
		/// </summary>
		public DbConnection Connection { get; private set; }

		/// <summary>
		/// Raw NpgsqlConnection object for direct connection execution.
		/// </summary>
		public NpgsqlConnection NpgConnection { get; private set; }

		/// <summary>
		/// NpgsqlTransaction for transactional execution when using NpgConnection.
		/// </summary>
		public NpgsqlTransaction NpgTransaction { get; private set; }

		/// <summary>
		/// List of EqlParameters for parameterized query execution.
		/// </summary>
		public List<EqlParameter> Parameters { get; private set; } = new List<EqlParameter>();

		/// <summary>
		/// EqlSettings controlling DISTINCT and total count inclusion.
		/// </summary>
		public EqlSettings Settings { get; private set; } = new EqlSettings();

		private IDbContext suppliedContext = null;

		/// <summary>
		/// Gets or sets the database context used for connection creation.
		/// Falls back to <see cref="DbContextAccessor.Current"/> when no context is explicitly supplied.
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
		/// Entity/relation metadata provider for EQL building and result mapping.
		/// </summary>
		private readonly IEntityMetadataProvider _metadataProvider;

		/// <summary>
		/// Optional hook executor for pre/post-search hooks.
		/// </summary>
		private readonly IRecordSearchHookExecutor _hookExecutor;

		/// <summary>
		/// Typed field value extractor for JSON result mapping.
		/// </summary>
		private readonly IFieldValueExtractor _fieldValueExtractor;

		/// <summary>
		/// Optional security context accessor for entity permission checking during result mapping.
		/// </summary>
		private readonly ISecurityContextAccessor _securityAccessor;

		/// <summary>
		/// Creates command with EQL text and variadic parameter array.
		/// </summary>
		public EqlCommand(string text, IEntityMetadataProvider metadataProvider,
			IFieldValueExtractor fieldValueExtractor,
			IRecordSearchHookExecutor hookExecutor = null,
			ISecurityContextAccessor securityAccessor = null,
			params EqlParameter[] parameters)
		{
			Text = text;
			if (string.IsNullOrWhiteSpace(text))
				throw new ArgumentException("Command text cannot be null or empty.");

			_metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
			_fieldValueExtractor = fieldValueExtractor ?? throw new ArgumentNullException(nameof(fieldValueExtractor));
			_hookExecutor = hookExecutor;
			_securityAccessor = securityAccessor;

			NpgConnection = null;
			Connection = null;

			if (parameters != null && parameters.Length > 0)
				Parameters.AddRange(parameters);
		}

		/// <summary>
		/// Creates command with EQL text, settings, and variadic parameter array.
		/// </summary>
		public EqlCommand(string text, IEntityMetadataProvider metadataProvider,
			IFieldValueExtractor fieldValueExtractor,
			EqlSettings settings,
			IRecordSearchHookExecutor hookExecutor = null,
			ISecurityContextAccessor securityAccessor = null,
			params EqlParameter[] parameters)
			: this(text, metadataProvider, fieldValueExtractor, hookExecutor, securityAccessor, parameters)
		{
			if (settings != null)
				Settings = settings;
		}

		/// <summary>
		/// Creates command with EQL text, explicit database context, and variadic parameter array.
		/// </summary>
		public EqlCommand(string text, IEntityMetadataProvider metadataProvider,
			IFieldValueExtractor fieldValueExtractor,
			IDbContext currentContext,
			IRecordSearchHookExecutor hookExecutor = null,
			ISecurityContextAccessor securityAccessor = null,
			params EqlParameter[] parameters)
			: this(text, metadataProvider, fieldValueExtractor, hookExecutor, securityAccessor, parameters)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
		}

		/// <summary>
		/// Creates command with EQL text, explicit database context, settings, and variadic parameter array.
		/// </summary>
		public EqlCommand(string text, IEntityMetadataProvider metadataProvider,
			IFieldValueExtractor fieldValueExtractor,
			IDbContext currentContext, EqlSettings settings,
			IRecordSearchHookExecutor hookExecutor = null,
			ISecurityContextAccessor securityAccessor = null,
			params EqlParameter[] parameters)
			: this(text, metadataProvider, fieldValueExtractor, currentContext, hookExecutor, securityAccessor, parameters)
		{
			if (settings != null)
				Settings = settings;
		}

		/// <summary>
		/// Creates command with EQL text, optional parameter list, and optional database context.
		/// </summary>
		public EqlCommand(string text, IEntityMetadataProvider metadataProvider,
			IFieldValueExtractor fieldValueExtractor,
			List<EqlParameter> parameters = null,
			IDbContext currentContext = null,
			IRecordSearchHookExecutor hookExecutor = null,
			ISecurityContextAccessor securityAccessor = null)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
			Text = text;

			if (string.IsNullOrWhiteSpace(text))
				throw new ArgumentException("Command text cannot be null or empty.");

			_metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
			_fieldValueExtractor = fieldValueExtractor ?? throw new ArgumentNullException(nameof(fieldValueExtractor));
			_hookExecutor = hookExecutor;
			_securityAccessor = securityAccessor;

			NpgConnection = null;
			Connection = null;

			if (parameters != null)
				Parameters.AddRange(parameters);
		}

		/// <summary>
		/// Creates command with EQL text, settings, optional parameter list, and optional database context.
		/// </summary>
		public EqlCommand(string text, IEntityMetadataProvider metadataProvider,
			IFieldValueExtractor fieldValueExtractor,
			EqlSettings settings,
			List<EqlParameter> parameters = null,
			IDbContext currentContext = null,
			IRecordSearchHookExecutor hookExecutor = null,
			ISecurityContextAccessor securityAccessor = null)
			: this(text, metadataProvider, fieldValueExtractor, parameters, currentContext, hookExecutor, securityAccessor)
		{
			if (settings != null)
				Settings = settings;
		}

		/// <summary>
		/// Creates command with EQL text, explicit DbConnection, and optional parameters.
		/// </summary>
		public EqlCommand(string text, IEntityMetadataProvider metadataProvider,
			IFieldValueExtractor fieldValueExtractor,
			DbConnection connection,
			List<EqlParameter> parameters = null,
			IDbContext currentContext = null,
			IRecordSearchHookExecutor hookExecutor = null,
			ISecurityContextAccessor securityAccessor = null)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
			Text = text;

			if (string.IsNullOrWhiteSpace(text))
				throw new ArgumentException("Command text cannot be null or empty.");

			_metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
			_fieldValueExtractor = fieldValueExtractor ?? throw new ArgumentNullException(nameof(fieldValueExtractor));
			_hookExecutor = hookExecutor;
			_securityAccessor = securityAccessor;

			NpgConnection = null;
			Connection = connection;

			if (connection == null)
				throw new ArgumentNullException(nameof(connection));

			if (parameters != null)
				Parameters.AddRange(parameters);
		}

		/// <summary>
		/// Creates command with EQL text, raw NpgsqlConnection, and optional transaction/parameters.
		/// </summary>
		public EqlCommand(string text, IEntityMetadataProvider metadataProvider,
			IFieldValueExtractor fieldValueExtractor,
			NpgsqlConnection connection,
			NpgsqlTransaction transaction = null,
			List<EqlParameter> parameters = null,
			IDbContext currentContext = null,
			IRecordSearchHookExecutor hookExecutor = null,
			ISecurityContextAccessor securityAccessor = null)
		{
			if (currentContext != null)
				suppliedContext = currentContext;

			Text = text;

			if (string.IsNullOrWhiteSpace(text))
				throw new ArgumentException("Command text cannot be null or empty.");

			_metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
			_fieldValueExtractor = fieldValueExtractor ?? throw new ArgumentNullException(nameof(fieldValueExtractor));
			_hookExecutor = hookExecutor;
			_securityAccessor = securityAccessor;

			Connection = null;
			NpgConnection = connection;
			NpgTransaction = transaction;

			if (connection == null)
				throw new ArgumentNullException(nameof(connection));

			if (parameters != null)
				Parameters.AddRange(parameters);
		}

		/// <summary>
		/// Executes the EQL command against PostgreSQL and returns the result set.
		/// Uses the AAP-mandated 600-second CommandTimeout (Performance Baseline Rule 16).
		/// </summary>
		/// <returns>An <see cref="EntityRecordList"/> with records and TotalCount from the query result.</returns>
		public EntityRecordList Execute()
		{
			EqlBuilder eqlBuilder = new EqlBuilder(Text, _metadataProvider, _hookExecutor, CurrentContext, Settings);
			var eqlBuildResult = eqlBuilder.Build(Parameters);

			if (eqlBuildResult.Errors.Count > 0)
				throw new EqlException(eqlBuildResult.Errors);

			if (CurrentContext == null)
				throw new EqlException("DbContext need to be created.");

			EntityRecordList result = new EntityRecordList();

			DataTable dt = new DataTable();
			var npgsParameters = eqlBuildResult.Parameters.Select(x => x.ToNpgsqlParameter()).ToList();
			NpgsqlCommand command = null;

			bool hooksExists = _hookExecutor != null && _hookExecutor.ContainsAnyHooksForEntity(eqlBuildResult.FromEntity.Name);

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
					command.CommandTimeout = 600; // AAP Performance Baseline Rule 16: 600-second EQL timeout
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
						_hookExecutor.ExecutePostSearchRecordHooks(eqlBuildResult.FromEntity.Name, result);
					}

					return result;
				}
			}

			command.CommandTimeout = 600; // AAP Performance Baseline Rule 16: 600-second EQL timeout
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
		/// Gets field metadata for the EQL query without executing it.
		/// </summary>
		/// <returns>List of field metadata entries describing each field in the query result.</returns>
		public List<EqlFieldMeta> GetMeta()
		{
			EqlBuilder eqlBuilder = new EqlBuilder(Text, _metadataProvider, _hookExecutor, CurrentContext, Settings);
			var eqlBuildResult = eqlBuilder.Build(Parameters);

			if (eqlBuildResult.Errors.Count > 0)
				throw new EqlException(eqlBuildResult.Errors);

			return eqlBuildResult.Meta;
		}

		/// <summary>
		/// Gets the generated SQL for the EQL query without executing it.
		/// Useful for debugging and SQL analysis.
		/// </summary>
		/// <returns>The generated PostgreSQL SQL string.</returns>
		public string GetSql()
		{
			EqlBuilder eqlBuilder = new EqlBuilder(Text, _metadataProvider, _hookExecutor, CurrentContext, Settings);
			var eqlBuildResult = eqlBuilder.Build(Parameters);

			if (eqlBuildResult.Errors.Count > 0)
				throw new EqlException(eqlBuildResult.Errors);

			return eqlBuildResult.Sql;
		}

		private EntityRecord ConvertJObjectToEntityRecord(JObject jObj, List<EqlFieldMeta> fieldMeta)
		{
			EntityRecord record = new EntityRecord();
			foreach (EqlFieldMeta meta in fieldMeta)
			{
				if (meta.Field != null)
				{
					var entity = _metadataProvider.ReadEntity(meta.Field.EntityName);
					if (entity == null)
						throw new Exception($"Entity '{meta.Field.Name}' not found");

					// Permission check: in the monolith, SecurityContext.HasEntityPermission was used.
					// In microservices, the ISecurityContextAccessor provides the same check.
					if (_securityAccessor != null)
					{
						bool hasPermission = _securityAccessor.HasEntityPermission(EntityPermission.Read, entity);
						if (!hasPermission)
							throw new Exception($"No access to entity '{meta.Field.EntityName}'");
					}

					record[meta.Field.Name] = _fieldValueExtractor.ExtractFieldValue(jObj[meta.Field.Name], meta.Field);
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
	}
}
