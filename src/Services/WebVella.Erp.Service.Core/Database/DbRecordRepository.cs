using System;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.Service.Core.Database
{
	/// <summary>
	/// Core service record repository for dynamic rec_* table CRUD operations.
	/// Stub implementation providing the minimum API surface required for module compilation.
	/// Full implementation to be provided by the assigned agent.
	/// </summary>
	public class DbRecordRepository
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

		public DbRecordRepository(CoreDbContext currentContext)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
		}
	}
}
