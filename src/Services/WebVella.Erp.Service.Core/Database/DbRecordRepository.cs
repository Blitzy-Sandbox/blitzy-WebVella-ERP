using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Core.Database
{
	/// <summary>
	/// Core service record repository for dynamic rec_* table CRUD operations.
	/// Stub implementation providing the minimum API surface required for module compilation.
	/// Full implementation to be provided by the assigned agent.
	/// </summary>
	public class DbRecordRepository
	{
		internal const string RECORD_COLLECTION_PREFIX = "rec_";

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

		public DbRecordRepository(CoreDbContext currentContext)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
		}

		/// <summary>
		/// Creates a new column in the entity's record table for the given field.
		/// Also creates unique constraint and search index as needed.
		/// </summary>
		public void CreateRecordField(string entityName, Field field)
		{
			string tableName = RECORD_COLLECTION_PREFIX + entityName;

			DbRepository.CreateColumn(tableName, field);
			if (field.Unique)
				DbRepository.CreateUniqueConstraint("idx_u_" + entityName + "_" + field.Name, tableName, new List<string> { field.Name });
			if (field.Searchable)
				DbRepository.CreateIndex("idx_s_" + entityName + "_" + field.Name, tableName, field.Name, field);
		}

		/// <summary>
		/// Updates an existing column in the entity's record table for the given field.
		/// Adjusts default value, nullability, and search index.
		/// </summary>
		public void UpdateRecordField(string entityName, Field field)
		{
			// Don't update default value for auto number field
			if (field.GetFieldType() == FieldType.AutoNumberField)
				return;

			string tableName = RECORD_COLLECTION_PREFIX + entityName;

			bool overrideNulls = field.Required && field.GetFieldDefaultValue() != null;
			DbRepository.SetColumnDefaultValue(RECORD_COLLECTION_PREFIX + entityName, field, overrideNulls);

			DbRepository.SetColumnNullable(RECORD_COLLECTION_PREFIX + entityName, field.Name, !field.Required);

			if (field.Searchable)
				DbRepository.CreateIndex("idx_s_" + entityName + "_" + field.Name, tableName, field.Name, field);
			else
				DbRepository.DropIndex("idx_s_" + entityName + "_" + field.Name);
		}

		/// <summary>
		/// Removes a column from the entity's record table for the given field.
		/// Also drops unique constraint and search index as appropriate.
		/// </summary>
		public void RemoveRecordField(string entityName, Field field)
		{
			string tableName = RECORD_COLLECTION_PREFIX + entityName;

			// Constraint may be removed automatically by PostgreSQL, but ensure it
			if (field.Unique)
				DbRepository.DropUniqueConstraint("idx_u_" + entityName + "_" + field.Name, tableName);
			if (field.Searchable)
				DbRepository.CreateIndex("idx_s_" + entityName + "_" + field.Name, tableName, field.Name, field);

			DbRepository.DeleteColumn(tableName, field.Name);
		}
	}
}
