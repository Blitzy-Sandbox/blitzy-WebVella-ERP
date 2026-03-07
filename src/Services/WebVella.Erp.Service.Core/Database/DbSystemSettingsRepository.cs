using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.Service.Core.Database
{
	/// <summary>
	/// System settings model representing a row in the system_settings table.
	/// Preserves the monolith's DbSystemSettings (WebVella.Erp.Database.DbSystemSettings)
	/// with exactly two columns: id (Guid, inherited from DbDocumentBase) and version (int).
	/// Used by ERPService bootstrap to track schema version for migration orchestration.
	/// </summary>
	public class DbSystemSettings : DbDocumentBase
	{
		[JsonProperty(PropertyName = "version")]
		public int Version { get; set; }
	}

	/// <summary>
	/// Repository for reading and writing the system_settings table in the Core service database (erp_core).
	/// Preserves the monolith's DbSystemSettingsRepository (WebVella.Erp.Database.DbSystemSettingsRepository)
	/// with two core methods:
	///   - Read(): Fetches current system settings with table-not-exists error suppression for first-run bootstrap.
	///   - Save(): Insert-or-update (upsert) pattern for system settings persistence.
	///
	/// Scoped to the Core service via CoreDbContext replacing the monolith's shared DbContext singleton.
	/// The Read() method's error suppression is CRITICAL bootstrap behavior: on first run when the
	/// system_settings table does not yet exist, the PostgreSQL "does not exist" error is silently
	/// caught and null is returned, signaling the caller to initialize the database schema.
	/// </summary>
	public class DbSystemSettingsRepository
	{
		private CoreDbContext suppliedContext = null;
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
		public DbSystemSettingsRepository(CoreDbContext currentContext)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
		}
		public DbSystemSettings Read()
		{
			DbSystemSettings setting = null;

			using (DbConnection con = CurrentContext.CreateConnection())
			{
				try
				{
					con.BeginTransaction();
					NpgsqlCommand command = con.CreateCommand("SELECT * FROM system_settings;");

					using (var reader = command.ExecuteReader())
					{
						if (reader != null && reader.Read())
						{
							setting = new DbSystemSettings();
							setting.Id = (Guid)reader["id"];
							setting.Version = (int)reader["version"];
						}
						reader.Close();
					}
					con.CommitTransaction();
				}
				catch (Exception ex)
				{
					if (con != null)
						con.RollbackTransaction();

					if (!ex.Message.Contains("does not exist"))
						throw;
				}
			}
			return setting;
		}

		public bool Save(DbSystemSettings systemSettings)
		{
			if (systemSettings == null)
				throw new ArgumentNullException("systemSettings");

			using (DbConnection con = CurrentContext.CreateConnection())
			{
				bool recordExists = false;
				NpgsqlCommand command = con.CreateCommand("SELECT COUNT(*) FROM system_settings WHERE id=@id;");
				var parameterId = command.CreateParameter();
				parameterId.ParameterName = "id";
				parameterId.Value = systemSettings.Id;
				parameterId.NpgsqlDbType = NpgsqlDbType.Uuid;
				command.Parameters.Add(parameterId);

				recordExists = ((long)command.ExecuteScalar()) > 0;

				if (recordExists)
					command = con.CreateCommand("UPDATE system_settings SET version=@version WHERE id=@id;");
				else
					command = con.CreateCommand("INSERT INTO system_settings (id, version) VALUES( @id,@version)");

				var parameter = command.CreateParameter();
				parameter.ParameterName = "version";
				parameter.Value = systemSettings.Version;
				parameter.NpgsqlDbType = NpgsqlDbType.Integer;
				command.Parameters.Add(parameter);

				parameterId = command.CreateParameter();
				parameterId.ParameterName = "id";
				parameterId.Value = systemSettings.Id;
				parameterId.NpgsqlDbType = NpgsqlDbType.Uuid;
				command.Parameters.Add(parameterId);

				return command.ExecuteNonQuery() > 0;
			}
		}
	}
}
