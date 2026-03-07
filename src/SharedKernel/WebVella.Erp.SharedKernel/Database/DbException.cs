using System;

namespace WebVella.Erp.SharedKernel.Database
{
	public class DbException : Exception
	{
		public DbException(string message) : base(message)
		{
		}
	}
}
