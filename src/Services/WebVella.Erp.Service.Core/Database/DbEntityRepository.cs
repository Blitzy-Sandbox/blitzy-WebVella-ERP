using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Npgsql;
using NpgsqlTypes;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.Service.Core.Database
{
	/// <summary>
	/// Core service entity repository for reading/writing entity metadata as JSON documents.
	/// Stub implementation providing the minimum API surface required for module compilation.
	/// Full implementation to be provided by the assigned agent.
	/// </summary>
	public class DbEntityRepository
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

		public DbEntityRepository(CoreDbContext currentContext)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
		}

		/// <summary>
		/// Reads all entity definitions from the entities table as JSON documents.
		/// </summary>
		public List<DbEntity> Read()
		{
			using (DbConnection con = CurrentContext.CreateConnection())
			{
				NpgsqlCommand command = con.CreateCommand("SELECT json FROM entities;");

				using (NpgsqlDataReader reader = command.ExecuteReader())
				{
					JsonSerializerSettings settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
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
		/// Reads a single entity by ID.
		/// </summary>
		public DbEntity Read(Guid entityId)
		{
			var entities = Read();
			foreach (var entity in entities)
			{
				if (entity.Id == entityId)
					return entity;
			}
			return null;
		}
	}
}
