using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Npgsql;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.Service.Core.Database
{
	/// <summary>
	/// Core-service-scoped ambient database context replacing the monolith's shared static DbContext.Current singleton.
	/// Manages a LIFO connection stack, shared NpgsqlTransaction state, and provides repository entry points
	/// (DbRecordRepository, DbEntityRepository, DbRelationRepository, DbSystemSettingsRepository) — all scoped
	/// to the Core service's own PostgreSQL database (erp_core).
	///
	/// Uses the same AsyncLocal + ConcurrentDictionary ambient context pattern as the original monolith,
	/// but scoped to the Core service only. Other services (CRM, Project, Mail) have their own XxxDbContext.
	///
	/// Implements IDbContext (from SharedKernel) to enable DbConnection and DbRepository to interact
	/// with the ambient context without direct coupling to this service-specific type.
	/// </summary>
	public class CoreDbContext : IDbContext
	{
		private static AsyncLocal<string> currentDbContextId = new AsyncLocal<string>();
		private static ConcurrentDictionary<string, CoreDbContext> dbContextDict = new ConcurrentDictionary<string, CoreDbContext>();
		private readonly object lockObj = new object();
		public static CoreDbContext Current
		{
			get
			{
				if (currentDbContextId == null || String.IsNullOrWhiteSpace(currentDbContextId.Value))
					return null;

				CoreDbContext context = null;
				dbContextDict.TryGetValue(currentDbContextId.Value, out context);
				return context;
			}
		}
		//private static AsyncLocal<DbContext> current = new AsyncLocal<DbContext>();
		private static string connectionString;

		/// <summary>
		/// Exposes the configured connection string for direct ADO.NET access
		/// by service components that need raw SQL queries (e.g., SecurityManager
		/// role loading) without going through the ambient context pattern.
		/// </summary>
		public static string ConnectionString => connectionString;

		public DbRecordRepository RecordRepository { get; private set; }
		public DbEntityRepository EntityRepository { get; private set; }
		public DbRelationRepository RelationRepository { get; private set; }
		public DbSystemSettingsRepository SettingsRepository { get; private set; }
		public NpgsqlTransaction Transaction { get { return transaction; } }

		private Stack<DbConnection> connectionStack;
		private NpgsqlTransaction transaction;

		#region <--- Context and Connection --->

		private CoreDbContext()
		{
			connectionStack = new Stack<DbConnection>();
			RecordRepository = new DbRecordRepository(this);
			EntityRepository = new DbEntityRepository(this);
			RelationRepository = new DbRelationRepository(this);
			SettingsRepository = new DbSystemSettingsRepository(this);
		}

		/// <summary>
		/// 
		/// </summary>
		/// <returns></returns>
		public DbConnection CreateConnection()
		{
			DbConnection con = null;
			if (transaction != null)
				con = new DbConnection(transaction, this);
			else
				con = new DbConnection(connectionString, this);

			connectionStack.Push(con);

			Debug.WriteLine($"ERP CreateConnection: {currentDbContextId.Value} | Stack count: {connectionStack.Count} | Hash: {con.GetHashCode()}");
			StackTrace t = new StackTrace();
			Debug.WriteLine($"========== ERP CreateConnection Stack =====");
			Debug.WriteLine($"{t.ToString()}");
			return con;
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="conn"></param>
		public bool CloseConnection(DbConnection conn)
		{
			lock (lockObj)
			{
				var dbConn = connectionStack.Peek();
				if (dbConn != conn)
					throw new DbException("You are trying to close connection, before closing inner connections.");

				connectionStack.Pop();

				Debug.WriteLine($"ERP CloseConnection: {currentDbContextId.Value} | Stack count: {connectionStack.Count} | Hash: {conn.GetHashCode()}");
				return connectionStack.Count == 0;
			}
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="transaction"></param>
		public void EnterTransactionalState(NpgsqlTransaction transaction)
		{
			this.transaction = transaction;
		}

		/// <summary>
		/// 
		/// </summary>
		public void LeaveTransactionalState()
		{
			this.transaction = null;
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="connString"></param>
		public static CoreDbContext CreateContext(string connString)
		{
			connectionString = connString;

			currentDbContextId.Value = Guid.NewGuid().ToString();
			if (!dbContextDict.TryAdd(currentDbContextId.Value, new CoreDbContext()))
				throw new Exception("Cannot create new context and store it into context dictionary");

			Debug.WriteLine($"ERP CreateContext: {currentDbContextId.Value} | dbContextDict count: {dbContextDict.Keys.Count}");

			CoreDbContext context;
			if (!dbContextDict.TryGetValue(currentDbContextId.Value, out context))
				throw new Exception("Cannot create new context and read it into context dictionary");

			// Register with SharedKernel's DbContextAccessor so that DbRepository static methods
			// can access the ambient IDbContext without direct coupling to CoreDbContext.
			DbContextAccessor.Current = context;

			return context;
		}


		public static void CloseContext()
		{
			if (Current != null)
			{
				if (Current.transaction != null)
				{
					Current.transaction.Rollback();
					throw new DbException("Trying to release database context in transactional state. There is open transaction in created connections.");
				}

				//if (current.Value.connectionStack.Count > 0)
				//{
				//	throw new DbException("Trying to release database context with already opened connection. Close connection before");
				//}
			}

			Debug.WriteLine($"ERP CloseContext BEFORE: {currentDbContextId.Value} | dbContextDict count: {dbContextDict.Keys.Count}");
			string idValue = null;
			if (currentDbContextId != null && !string.IsNullOrWhiteSpace(currentDbContextId.Value))
				idValue = currentDbContextId.Value;

			if (!string.IsNullOrWhiteSpace(idValue))
			{
				CoreDbContext context;
				dbContextDict.TryRemove(idValue, out context);
				if (context != null)
					context.Dispose();

				currentDbContextId.Value = null;
			}

			// Clear SharedKernel's DbContextAccessor so that DbRepository static methods
			// no longer reference a disposed context.
			DbContextAccessor.Current = null;

			Debug.WriteLine($"ERP CloseContext AFTER: dbContextDict count: {dbContextDict.Keys.Count}");

		}


		#endregion


		#region <--- Dispose --->

		/// <summary>
		/// 
		/// </summary>
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="disposing"></param>
		public void Dispose(bool disposing)
		{
			if (disposing)
			{
				CloseContext();
			}
		}
		#endregion
	}
}
