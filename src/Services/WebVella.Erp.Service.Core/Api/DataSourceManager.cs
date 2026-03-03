using Microsoft.Extensions.Caching.Distributed;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Utilities;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Api
{
	/// <summary>
	/// Central runtime manager for data sources — both database-defined
	/// (<see cref="DatabaseDataSource"/>) and code-defined (<see cref="CodeDataSource"/>).
	///
	/// Provides full CRUD for database data sources, reflection-based discovery of
	/// <see cref="CodeDataSource"/> subclasses from loaded assemblies, datasource
	/// execution via the EQL engine, and parameter parsing/conversion.
	///
	/// Adapted from the monolith's <c>WebVella.Erp.Api.DataSourceManager</c> (539 lines).
	/// All business logic is preserved identically.
	///
	/// KEY CHANGE: Replaces the monolith's static <c>IMemoryCache</c> with an injected
	/// <see cref="IDistributedCache"/> (Redis-backed) for cross-instance cache coherence
	/// in the microservice architecture. The 1-hour absolute expiration TTL is preserved
	/// (AAP 0.8.3 Performance Baselines).
	///
	/// Constructor injection replaces the monolith's <c>new DbDataSourceRepository()</c>
	/// instantiation to support per-service DI and testability.
	/// </summary>
	public class DataSourceManager
	{
		private readonly DbDataSourceRepository _repository;
		private readonly IDistributedCache _cache;

		/// <summary>
		/// Cache key for the merged list of code + database data sources.
		/// Prefixed with "core:" to namespace within the shared Redis instance.
		/// </summary>
		private const string CACHE_KEY = "core:datasources";

		/// <summary>
		/// Statically held list of all <see cref="CodeDataSource"/> subclass instances
		/// discovered via assembly reflection during the first construction.
		/// Populated once and shared across all DataSourceManager instances.
		/// </summary>
		private static List<CodeDataSource> codeDataSources = new List<CodeDataSource>();

		/// <summary>
		/// Thread-safety lock object for one-time <see cref="InitCodeDataSources"/> initialization.
		/// </summary>
		private static readonly object _initLock = new object();

		/// <summary>
		/// Flag indicating whether <see cref="InitCodeDataSources"/> has completed.
		/// Uses double-checked locking in the constructor for thread safety.
		/// </summary>
		private static bool _codeDataSourcesInitialized = false;

		#region <=== Cache Related ===>

		/// <summary>
		/// Serializes and stores the merged data source list in the distributed cache
		/// with a 1-hour absolute expiration (preserving the monolith's cache TTL).
		/// </summary>
		/// <param name="dataSources">The merged list of code + database data sources.</param>
		private void AddToCache(List<DataSourceBase> dataSources)
		{
			var options = new DistributedCacheEntryOptions()
				.SetAbsoluteExpiration(TimeSpan.FromHours(1));
			var json = JsonConvert.SerializeObject(dataSources, new JsonSerializerSettings
			{
				TypeNameHandling = TypeNameHandling.Objects,
				TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple
			});
			_cache.SetString(CACHE_KEY, json, options);
		}

		/// <summary>
		/// Retrieves the merged data source list from the distributed cache.
		/// Returns null on cache miss so the caller can rebuild from DB + code sources.
		/// </summary>
		/// <returns>The cached list of data sources, or null if not cached.</returns>
		private List<DataSourceBase> GetFromCache()
		{
			var json = _cache.GetString(CACHE_KEY);
			if (string.IsNullOrEmpty(json))
				return null;

			return JsonConvert.DeserializeObject<List<DataSourceBase>>(json, new JsonSerializerSettings
			{
				TypeNameHandling = TypeNameHandling.Objects,
				TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple
			});
		}

		/// <summary>
		/// Invalidates the data source cache entry. Called after Create, Update, and Delete
		/// operations to ensure subsequent <see cref="GetAll"/> calls rebuild from the database.
		/// </summary>
		public void RemoveFromCache()
		{
			_cache.Remove(CACHE_KEY);
		}

		#endregion

		/// <summary>
		/// Creates a new DataSourceManager with injected dependencies.
		/// On first construction, performs one-time assembly scanning to discover
		/// all <see cref="CodeDataSource"/> subclasses (thread-safe, idempotent).
		/// </summary>
		/// <param name="repository">Database data source repository for CRUD operations.</param>
		/// <param name="cache">Distributed cache (Redis) for cross-instance caching.</param>
		public DataSourceManager(DbDataSourceRepository repository, IDistributedCache cache)
		{
			_repository = repository ?? throw new ArgumentNullException(nameof(repository));
			_cache = cache ?? throw new ArgumentNullException(nameof(cache));

			if (!_codeDataSourcesInitialized)
			{
				lock (_initLock)
				{
					if (!_codeDataSourcesInitialized)
					{
						InitCodeDataSources();
						_codeDataSourcesInitialized = true;
					}
				}
			}
		}

		/// <summary>
		/// Scans all loaded assemblies (excluding System.* and Microsoft.* namespaces)
		/// for concrete subclasses of <see cref="CodeDataSource"/> and instantiates them.
		/// Throws if duplicate IDs are detected across code data sources.
		///
		/// Preserved identically from the monolith's <c>DataSourceManager.InitCodeDataSources()</c>.
		/// </summary>
		private static void InitCodeDataSources()
		{
			var assemblies = AppDomain.CurrentDomain.GetAssemblies()
							.Where(a => !(a.FullName.ToLowerInvariant().StartsWith("microsoft.")
								|| a.FullName.ToLowerInvariant().StartsWith("system.")));

			foreach (var assembly in assemblies)
			{
				foreach (Type type in assembly.GetTypes())
				{
					if (type.IsSubclassOf(typeof(CodeDataSource)))
					{
						if (type.IsAbstract)
							continue;

						var instance = (CodeDataSource)Activator.CreateInstance(type);

						if (codeDataSources.Any(x => x.Id == instance.Id))
							throw new Exception($"Multiple code data sources with same ID ('{instance.Id}'). This is not allowed.");

						codeDataSources.Add(instance);
					}
				}
			}
		}

		/// <summary>
		/// Retrieves a single data source by its unique identifier.
		/// Searches the merged list of code + database data sources.
		/// </summary>
		/// <param name="id">The unique identifier of the data source.</param>
		/// <returns>The data source, or null if not found.</returns>
		public DataSourceBase Get(Guid id)
		{
			return GetAll().SingleOrDefault(x => x.Id == id);
		}

		/// <summary>
		/// Retrieves all data sources — both code-defined and database-defined.
		/// Results are cached in the distributed cache with a 1-hour TTL.
		/// On cache miss, code data sources are merged with database data sources
		/// and the result is cached before returning.
		/// </summary>
		/// <returns>The complete list of all data sources.</returns>
		public List<DataSourceBase> GetAll()
		{
			var cached = GetFromCache();
			if (cached != null)
				return cached;

			List<DataSourceBase> result = new List<DataSourceBase>();
			result.AddRange(codeDataSources);

			DataTable dt = _repository.GetAll();
			foreach (DataRow row in dt.Rows)
			{
				var ds = (DataSourceBase)row.MapTo<DatabaseDataSource>();
				if (result.Any(x => x.Id == ds.Id))
					throw new Exception($"Database data source have same  ID ('{ds.Id}') as already existing code data source. This is not allowed.");
				result.Add(ds);
			}

			AddToCache(result);
			return result;
		}

		/// <summary>
		/// Retrieves a single database data source by its unique identifier.
		/// Returns null if no matching record exists.
		/// </summary>
		/// <param name="id">The unique identifier of the data source.</param>
		/// <returns>The database data source, or null if not found.</returns>
		private DatabaseDataSource GetDatabaseDataSourceById(Guid id)
		{
			DataRow dr = _repository.Get(id);
			if (dr == null)
				return null;

			return dr.MapTo<DatabaseDataSource>();
		}

		/// <summary>
		/// Retrieves a single database data source by its name.
		/// Returns null if no matching record exists.
		/// </summary>
		/// <param name="name">The name of the data source.</param>
		/// <returns>The database data source, or null if not found.</returns>
		private DataSourceBase GetDatabaseDataSourceByName(string name)
		{
			DataRow dr = _repository.Get(name);
			if (dr == null)
				return null;

			return dr.MapTo<DatabaseDataSource>();
		}

		/// <summary>
		/// Creates a new database data source with full validation:
		/// - Parses and validates EQL text via <see cref="EqlBuilder"/>
		/// - Validates parameter definitions match EQL expectations
		/// - Enforces unique name constraint
		/// - Persists via <see cref="DbDataSourceRepository"/> and invalidates cache
		///
		/// Preserved identically from the monolith's <c>DataSourceManager.Create()</c>.
		/// </summary>
		/// <param name="name">Unique name for the data source.</param>
		/// <param name="description">Optional description text.</param>
		/// <param name="weight">Sort weight for UI ordering.</param>
		/// <param name="eql">EQL query text to compile and validate.</param>
		/// <param name="parameters">Newline-delimited parameter definitions (name,type,value[,ignoreParseErrors]).</param>
		/// <param name="returnTotal">Whether to include total count in query results.</param>
		/// <returns>The newly created database data source.</returns>
		public DatabaseDataSource Create(string name, string description, int weight, string eql, string parameters, bool returnTotal = true)
		{
			ValidationException validation = new ValidationException();

			List<DataSourceParameter> dsParams = ProcessParametersText(parameters);
			List<EqlParameter> eqlParams = new List<EqlParameter>();
			foreach (var dsPar in dsParams)
				eqlParams.Add(ConvertDataSourceParameterToEqlParameter(dsPar));

			EqlBuilder builder = new EqlBuilder(eql);
			var result = builder.Build(eqlParams);
			if (result.Errors.Count > 0)
			{
				foreach (var err in result.Errors)
				{
					if (err.Line.HasValue || err.Column.HasValue)
						validation.AddError("eql", $"{err.Message} [{err.Line},{err.Column}]");
					else
						validation.AddError("eql", err.Message);
				}
			}
			validation.CheckAndThrow();

			foreach (var par in result.Parameters)
			{
				if (!eqlParams.Any(x => x.ParameterName == par.ParameterName))
				{
					validation.AddError("parameters", $"Parameter '{par.ParameterName}' is missing.");
				}
			}
			validation.CheckAndThrow();

			DatabaseDataSource ds = new DatabaseDataSource();
			ds.Id = Guid.NewGuid();
			ds.Name = name;
			ds.Description = description;
			ds.EqlText = eql;
			ds.SqlText = result.Sql;
			ds.EntityName = result.FromEntity.Name;
			ds.ReturnTotal = returnTotal;
			ds.Parameters.AddRange(dsParams);
			ds.Fields.AddRange(ProcessFieldsMeta(result.Meta));

			if (string.IsNullOrWhiteSpace(ds.Name))
				validation.AddError("name", "Name is required.");
			else if (GetDatabaseDataSourceByName(ds.Name) != null)
				validation.AddError("name", "DataSource record with same name already exists.");

			if (string.IsNullOrWhiteSpace(ds.EqlText))
				validation.AddError("eql", "Eql is required.");

			if (string.IsNullOrWhiteSpace(ds.SqlText))
				validation.AddError("sql", "Sql is required.");

			validation.CheckAndThrow();

			_repository.Create(ds.Id, ds.Name, ds.Description, ds.Weight, ds.EqlText, ds.SqlText,
				JsonConvert.SerializeObject(ds.Parameters), JsonConvert.SerializeObject(ds.Fields), ds.EntityName, ds.ReturnTotal);

			RemoveFromCache();

			return _repository.Get(ds.Id).MapTo<DatabaseDataSource>();
		}

		/// <summary>
		/// Updates an existing database data source with full validation:
		/// - Validates EQL text via <see cref="EqlBuilder"/> with settings
		/// - Validates parameter definitions match EQL expectations
		/// - Enforces unique name constraint (excluding self)
		/// - Persists via <see cref="DbDataSourceRepository"/> and invalidates cache
		///
		/// Preserved identically from the monolith's <c>DataSourceManager.Update()</c>.
		/// </summary>
		/// <param name="id">The unique identifier of the data source to update.</param>
		/// <param name="name">Updated name (must be unique).</param>
		/// <param name="description">Updated description.</param>
		/// <param name="weight">Updated sort weight.</param>
		/// <param name="eql">Updated EQL query text.</param>
		/// <param name="parameters">Updated newline-delimited parameter definitions.</param>
		/// <param name="returnTotal">Whether to include total count in query results.</param>
		/// <returns>The updated database data source.</returns>
		public DatabaseDataSource Update(Guid id, string name, string description, int weight, string eql, string parameters, bool returnTotal = true)
		{
			ValidationException validation = new ValidationException();

			if (string.IsNullOrWhiteSpace(eql))
				throw new ArgumentException(nameof(eql));

			List<EqlParameter> eqlParams = new List<EqlParameter>();
			List<DataSourceParameter> dsParams = new List<DataSourceParameter>();
			if (!string.IsNullOrWhiteSpace(parameters))
			{
				dsParams = ProcessParametersText(parameters);
				foreach (var dsPar in dsParams)
					eqlParams.Add(ConvertDataSourceParameterToEqlParameter(dsPar));
			}

			EqlBuilder builder = new EqlBuilder(eql, settings: new EqlSettings() { IncludeTotal = returnTotal });
			var result = builder.Build(eqlParams);
			if (result.Errors.Count > 0)
			{
				foreach (var err in result.Errors)
				{
					if (err.Line.HasValue || err.Column.HasValue)
						validation.AddError("eql", $"{err.Message} [{err.Line},{err.Column}]");
					else
						validation.AddError("eql", err.Message);
				}
			}
			validation.CheckAndThrow();

			foreach (var par in result.Parameters)
			{
				if (!eqlParams.Any(x => x.ParameterName == par.ParameterName))
				{
					validation.AddError("parameters", $"Parameter '{par.ParameterName}' is missing.");
				}
			}
			validation.CheckAndThrow();

			DatabaseDataSource ds = new DatabaseDataSource();
			ds.Id = id;
			ds.Name = name;
			ds.Description = description;
			ds.EqlText = eql;
			ds.SqlText = result.Sql;
			ds.EntityName = result.FromEntity.Name;
			ds.ReturnTotal = returnTotal;
			ds.Parameters.AddRange(dsParams);
			ds.Fields.AddRange(ProcessFieldsMeta(result.Meta));

			if (string.IsNullOrWhiteSpace(ds.Name))
				validation.AddError("name", "Name is required.");
			else
			{
				var existingDS = GetDatabaseDataSourceByName(ds.Name);
				if (existingDS != null && existingDS.Id != ds.Id)
					validation.AddError("name", "Another DataSource with same name already exists.");
			}

			if (string.IsNullOrWhiteSpace(ds.EqlText))
				validation.AddError("eql", "Eql is required.");

			if (string.IsNullOrWhiteSpace(ds.SqlText))
				validation.AddError("sql", "Sql is required.");


			validation.CheckAndThrow();

			_repository.Update(ds.Id, ds.Name, ds.Description, ds.Weight, ds.EqlText, ds.SqlText, JsonConvert.SerializeObject(ds.Parameters),
				JsonConvert.SerializeObject(ds.Fields), ds.EntityName, ds.ReturnTotal);

			RemoveFromCache();

			return GetDatabaseDataSourceById(ds.Id);
		}

		/// <summary>
		/// Recursively builds a list of <see cref="DataSourceModelFieldMeta"/> from
		/// EQL field metadata. Handles relation fields (prefixed with "$") and
		/// regular fields, preserving the hierarchical child structure.
		///
		/// Preserved identically from the monolith's <c>DataSourceManager.ProcessFieldsMeta()</c>.
		/// </summary>
		/// <param name="fields">EQL field metadata from the build result.</param>
		/// <returns>Hierarchical field metadata tree for the data source.</returns>
		private List<DataSourceModelFieldMeta> ProcessFieldsMeta(List<EqlFieldMeta> fields)
		{
			List<DataSourceModelFieldMeta> result = new List<DataSourceModelFieldMeta>();

			if (fields == null)
				return result;

			foreach (var fieldMeta in fields)
			{
				DataSourceModelFieldMeta dsMeta = new DataSourceModelFieldMeta();
				dsMeta.EntityName = string.Empty;
				if (fieldMeta.Relation != null)
				{
					dsMeta.Name = "$" + fieldMeta.Relation.Name;
					dsMeta.Type = FieldType.RelationField;
				}
				if (fieldMeta.Field != null)
				{
					dsMeta.Name = fieldMeta.Field.Name;
					dsMeta.Type = fieldMeta.Field.GetFieldType();
				}

				dsMeta.Children.AddRange(ProcessFieldsMeta(fieldMeta.Children));
				result.Add(dsMeta);
			}

			return result;
		}

		/// <summary>
		/// Parses a newline-delimited parameter definition string into a list of
		/// <see cref="DataSourceParameter"/> objects. Each line has the format:
		/// <c>name,type,value[,ignoreParseErrors]</c>.
		///
		/// Preserved identically from the monolith's <c>DataSourceManager.ProcessParametersText()</c>.
		/// </summary>
		/// <param name="parameters">Newline-delimited parameter definitions.</param>
		/// <returns>Parsed list of data source parameters.</returns>
		private List<DataSourceParameter> ProcessParametersText(string parameters)
		{
			List<DataSourceParameter> dsParams = new List<DataSourceParameter>();

			if (string.IsNullOrWhiteSpace(parameters))
				return dsParams;

			foreach (var line in parameters.Split("\n", StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = line.Replace("\r", "").Split(",", StringSplitOptions.RemoveEmptyEntries);
				if (parts.Count() < 3 || parts.Count() > 4)
					throw new Exception("Invalid parameter description: " + line);

				DataSourceParameter dsPar = new DataSourceParameter();
				dsPar.Name = parts[0].Trim();
				dsPar.Type = parts[1].ToLowerInvariant().Trim();
				if (string.IsNullOrWhiteSpace(dsPar.Type))
					throw new Exception("Invalid parameter type in: " + line);

				dsPar.Value = parts[2].Trim();
				if (parts.Count() == 4)
				{
					try
					{
						dsPar.IgnoreParseErrors = bool.Parse(parts[3]);
					}
					catch
					{
						dsPar.IgnoreParseErrors = false;
					}
				}
				dsParams.Add(dsPar);
			}
			return dsParams;
		}

		/// <summary>
		/// Converts a list of <see cref="DataSourceParameter"/> objects back into the
		/// newline-delimited text format used by <see cref="ProcessParametersText"/>.
		///
		/// Preserved identically from the monolith's <c>DataSourceManager.ConvertParamsToText()</c>.
		/// </summary>
		/// <param name="parameters">The parameters to serialize.</param>
		/// <returns>Newline-delimited parameter definition string.</returns>
		public string ConvertParamsToText(List<DataSourceParameter> parameters)
		{
			var result = "";
			foreach (var param in parameters)
			{
				if (param.IgnoreParseErrors)
					result += $"{param.Name},{param.Type},{param.Value},true" + Environment.NewLine;
				else
					result += $"{param.Name},{param.Type},{param.Value}" + Environment.NewLine;
			}

			return result;
		}

		/// <summary>
		/// Converts a <see cref="DataSourceParameter"/> to an <see cref="EqlParameter"/>
		/// suitable for EQL command execution. Ensures the parameter name is prefixed with "@".
		///
		/// Preserved identically from the monolith's
		/// <c>DataSourceManager.ConvertDataSourceParameterToEqlParameter()</c>.
		/// </summary>
		/// <param name="dsParameter">The data source parameter to convert.</param>
		/// <returns>An EQL parameter with resolved value and "@"-prefixed name.</returns>
		public EqlParameter ConvertDataSourceParameterToEqlParameter(DataSourceParameter dsParameter)
		{
			var parName = dsParameter.Name;
			if (!parName.StartsWith("@"))
				parName = "@" + parName;

			return new EqlParameter(parName, GetDataSourceParameterValue(dsParameter), dsParameter.Type);
		}

		/// <summary>
		/// Resolves the runtime value of a <see cref="DataSourceParameter"/> based on its
		/// declared type and textual value. Handles special sentinel values:
		///
		/// - <c>null</c> — returns CLR null
		/// - <c>guid.empty</c> — returns <see cref="Guid.Empty"/>
		/// - <c>now</c> — returns <see cref="DateTime.Now"/>
		/// - <c>utc_now</c> — returns <see cref="DateTime.UtcNow"/>
		/// - <c>string.empty</c> — returns <see cref="String.Empty"/>
		/// - <c>current_user_id</c> — returns <see cref="SecurityContext.CurrentUser"/>.Id
		/// - <c>current_user_email</c> — returns <see cref="SecurityContext.CurrentUser"/>.Email
		///
		/// Type-based parsing covers: guid, int, decimal, date, text, bool.
		///
		/// Preserved from the monolith's <c>DataSourceManager.GetDataSourceParameterValue()</c>
		/// with the addition of <c>current_user_id</c> and <c>current_user_email</c> handling
		/// via <see cref="SecurityContext"/> (adapted for JWT-propagated identity in microservices).
		/// </summary>
		/// <param name="dsParameter">The data source parameter with type and value to resolve.</param>
		/// <returns>The resolved CLR value for use in EQL query execution.</returns>
		public object GetDataSourceParameterValue(DataSourceParameter dsParameter)
		{
			switch (dsParameter.Type.ToLower())
			{
				case "guid":
					{
						if (string.IsNullOrWhiteSpace(dsParameter.Value))
							return null;

						if (dsParameter.Value.ToLowerInvariant() == "null")
							return null;

						if (dsParameter.Value.ToLowerInvariant() == "guid.empty")
							return Guid.Empty;

						if (dsParameter.Value.ToLowerInvariant() == "current_user_id")
							return SecurityContext.CurrentUser?.Id;

						if (Guid.TryParse(dsParameter.Value, out Guid value))
							return value;

						if (dsParameter.IgnoreParseErrors)
							return null;

						throw new Exception($"Invalid Guid value for parameter: " + dsParameter.Name);
					}
				case "int":
					{
						if (string.IsNullOrWhiteSpace(dsParameter.Value))
							return null;

						if (Int32.TryParse(dsParameter.Value, out int value))
							return value;

						if (dsParameter.Value.ToLowerInvariant() == "null")
							return null;

						if (dsParameter.IgnoreParseErrors)
							return null;

						throw new Exception($"Invalid int value for parameter: " + dsParameter.Name);
					}
				case "decimal":
					{
						if (string.IsNullOrWhiteSpace(dsParameter.Value))
							return null;

						if (Decimal.TryParse(dsParameter.Value, out decimal value))
							return value;

						if (dsParameter.IgnoreParseErrors)
							return null;

						throw new Exception($"Invalid decimal value for parameter: " + dsParameter.Name);
					}
				case "date":
					{
						if (string.IsNullOrWhiteSpace(dsParameter.Value))
							return null;

						if (dsParameter.Value.ToLowerInvariant() == "null")
							return null;

						if (dsParameter.Value.ToLowerInvariant() == "now")
							return DateTime.Now;

						if (dsParameter.Value.ToLowerInvariant() == "utc_now")
							return DateTime.UtcNow;

						if (DateTime.TryParse(dsParameter.Value, out DateTime value))
							return value;

						if (dsParameter.IgnoreParseErrors)
							return null;

						throw new Exception($"Invalid datetime value for parameter: " + dsParameter.Name);
					}
				case "text":
					{
						if (dsParameter.Value.ToLowerInvariant() == "null")
							return null;

						if (dsParameter.Value.ToLowerInvariant() == "string.empty")
							return String.Empty;

						if (dsParameter.Value.ToLowerInvariant() == "current_user_email")
							return SecurityContext.CurrentUser?.Email;

						if (dsParameter.IgnoreParseErrors)
							return null;

						return dsParameter.Value;
					}
				case "bool":
					{
						if (dsParameter.Value.ToLowerInvariant() == "null")
							return null;

						if (dsParameter.Value.ToLowerInvariant() == "true")
							return true;

						if (dsParameter.Value.ToLowerInvariant() == "false")
							return false;

						if (dsParameter.IgnoreParseErrors)
							return null;

						throw new Exception($"Invalid boolean value for parameter: " + dsParameter.Name);
					}
				default:
					throw new Exception($"Invalid parameter type '{dsParameter.Type}' for '{dsParameter.Name}'");
			}
		}

		/// <summary>
		/// Deletes a database data source by its unique identifier and invalidates the cache.
		///
		/// Preserved identically from the monolith's <c>DataSourceManager.Delete()</c>.
		/// </summary>
		/// <param name="id">The unique identifier of the data source to delete.</param>
		public void Delete(Guid id)
		{
			_repository.Delete(id);
			RemoveFromCache();
		}

		/// <summary>
		/// Executes a data source by its unique identifier, merging caller-provided
		/// parameters with the data source's default parameter definitions.
		///
		/// For <see cref="DatabaseDataSource"/>: constructs and executes an <see cref="EqlCommand"/>
		/// with the data source's EQL text and merged parameters.
		///
		/// For <see cref="CodeDataSource"/>: invokes the <see cref="CodeDataSource.Execute"/>
		/// method with parameters converted to a string-keyed dictionary.
		///
		/// Preserved identically from the monolith's <c>DataSourceManager.Execute(Guid, ...)</c>.
		/// </summary>
		/// <param name="id">The unique identifier of the data source to execute.</param>
		/// <param name="parameters">Optional caller-provided EQL parameters to merge.</param>
		/// <returns>The query result as an <see cref="EntityRecordList"/>.</returns>
		public EntityRecordList Execute(Guid id, List<EqlParameter> parameters = null)
		{
			var ds = Get(id);
			if (ds == null)
				throw new Exception("DataSource not found.");

			if (parameters == null)
				parameters = new List<EqlParameter>();

			foreach (var par in ds.Parameters)
				if (!(parameters.Any(x => x.ParameterName == par.Name) || parameters.Any(x => x.ParameterName == "@" + par.Name)))
					parameters.Add(new EqlParameter(par.Name, par.Value));

			if (ds is DatabaseDataSource)
				return new EqlCommand(((DatabaseDataSource)ds).EqlText, new EqlSettings { IncludeTotal = ds.ReturnTotal }, parameters).Execute();
			else if (ds is CodeDataSource)
			{
				var args = new Dictionary<string, object>();
				foreach (var param in parameters)
				{
					args[param.ParameterName] = param.Value;
				}
				var codeDs = (CodeDataSource)ds;
				return (EntityRecordList)codeDs.Execute(args);
			}
			else
				throw new NotImplementedException();
		}

		/// <summary>
		/// Executes an ad-hoc EQL query with optional inline parameter definitions.
		/// Parameters are parsed from newline-delimited text and converted to EQL parameters.
		///
		/// Preserved identically from the monolith's <c>DataSourceManager.Execute(string, ...)</c>.
		/// </summary>
		/// <param name="eql">The EQL query text to execute.</param>
		/// <param name="parameters">Optional newline-delimited parameter definitions.</param>
		/// <param name="returnTotal">Whether to include total count in query results.</param>
		/// <returns>The query result as an <see cref="EntityRecordList"/>.</returns>
		public EntityRecordList Execute(string eql, string parameters = null, bool returnTotal = true)
		{
			if (string.IsNullOrWhiteSpace(eql))
				throw new ArgumentException(nameof(eql));

			List<EqlParameter> eqlParams = new List<EqlParameter>();
			if (!string.IsNullOrWhiteSpace(parameters))
			{
				List<DataSourceParameter> dsParams = ProcessParametersText(parameters);
				foreach (var dsPar in dsParams)
					eqlParams.Add(ConvertDataSourceParameterToEqlParameter(dsPar));
			}
			return new EqlCommand(eql, new EqlSettings { IncludeTotal = returnTotal }, eqlParams).Execute();
		}

		/// <summary>
		/// Generates the PostgreSQL SQL text from an EQL query without executing it.
		/// Useful for debugging, SQL preview in the admin UI, and query plan analysis.
		///
		/// Preserved identically from the monolith's <c>DataSourceManager.GenerateSql()</c>.
		/// </summary>
		/// <param name="eql">The EQL query text to translate.</param>
		/// <param name="parameters">Optional newline-delimited parameter definitions.</param>
		/// <param name="returnTotal">Whether to include total count in the generated SQL.</param>
		/// <returns>The generated PostgreSQL SQL text.</returns>
		public string GenerateSql(string eql, string parameters, bool returnTotal = true)
		{
			ValidationException validation = new ValidationException();
			List<DataSourceParameter> dsParams = ProcessParametersText(parameters);
			List<EqlParameter> eqlParams = new List<EqlParameter>();
			foreach (var dsPar in dsParams)
				eqlParams.Add(ConvertDataSourceParameterToEqlParameter(dsPar));

			EqlBuilder builder = new EqlBuilder(eql, settings: new EqlSettings { IncludeTotal = returnTotal });
			var result = builder.Build(eqlParams);
			if (result.Errors.Count > 0)
			{
				foreach (var err in result.Errors)
				{
					if (err.Line.HasValue || err.Column.HasValue)
						validation.AddError("eql", $"{err.Message} [{err.Line},{err.Column}]");
					else
						validation.AddError("eql", err.Message);
				}
			}
			validation.CheckAndThrow();

			return result.Sql;
		}
	}
}
