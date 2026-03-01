using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WebVella.Erp.Service.Admin.Services
{
	/// <summary>
	/// Defines the contract for system log and job log cleanup operations.
	/// Used by the Admin service for periodic maintenance of the system_log and jobs tables.
	/// </summary>
	public interface ILogService
	{
		/// <summary>
		/// Clears system error logs and completed/failed/canceled/aborted job records
		/// using retention-based cleanup: keeps the newest 1000 records and only
		/// purges when the oldest record exceeds 30 days.
		/// </summary>
		void ClearJobAndErrorLogs();

		/// <summary>
		/// Unconditionally purges all completed (status=3), failed (status=4),
		/// canceled (status=5), and aborted (status=6) job records from the jobs table.
		/// </summary>
		void ClearJobLogs();

		/// <summary>
		/// Unconditionally purges all records from the system_log table.
		/// </summary>
		void ClearErrorLogs();
	}

	/// <summary>
	/// Provides system log and job log cleanup operations for the Admin microservice.
	/// Migrated from WebVella.Erp.Plugins.SDK.Services.LogService with dependency injection
	/// replacing the static ErpSettings.ConnectionString access pattern.
	/// All business logic and SQL queries are preserved exactly from the monolith source.
	/// </summary>
	public class LogService : ILogService
	{
		private readonly string _connectionString;
		private readonly ILogger<LogService> _logger;

		/// <summary>
		/// Initializes a new instance of <see cref="LogService"/> with DI-injected configuration and logging.
		/// </summary>
		/// <param name="configuration">Application configuration providing the database connection string.</param>
		/// <param name="logger">Logger instance for diagnostic output.</param>
		/// <exception cref="ArgumentNullException">Thrown when the 'Default' connection string is not configured.</exception>
		public LogService(IConfiguration configuration, ILogger<LogService> logger)
		{
			_connectionString = configuration.GetConnectionString("Default")
				?? throw new ArgumentNullException(nameof(configuration), "Connection string 'Default' not found.");
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <inheritdoc />
		public void ClearJobAndErrorLogs()
		{
			//clear system logs older than 30 days and if there is more than 1000 records
			string logSql = "SELECT id, created_on FROM system_log ORDER BY created_on ASC";
			var logTable = ExecuteQuerySqlCommand(logSql);
			var logRows = logTable.Rows;
			DateTime logTreshold = DateTime.UtcNow.AddDays(-30);
			if (logRows.Count > 1000 && (DateTime)logRows[0]["created_on"] < logTreshold)
			{
				var logsToDelete = logRows.OfType<DataRow>().OrderByDescending(r => r["created_on"]).Select(r => (Guid)r["id"]).Skip(1000).ToList();
				foreach (var logId in logsToDelete)
				{
					string deleteSql = $"DELETE FROM system_log WHERE id = @id";
					List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
					parameters.Add(new NpgsqlParameter("id", logId) { NpgsqlDbType = NpgsqlDbType.Uuid });
					ExecuteNonQuerySqlCommand(deleteSql, parameters);
				}
			}

			//clear Canceled, Failed, Finished and Aborted jobs older than 30 days and if there is more than 1000 records
			string sql = "SELECT id, created_on FROM jobs WHERE status = 3 OR status = 4 OR status = 5 OR status = 6 ORDER BY created_on ASC";
			var jobTable = ExecuteQuerySqlCommand(sql);
			var jobRows = jobTable.Rows;
			DateTime jobTreshold = DateTime.UtcNow.AddDays(-30);
			if (jobRows.Count > 1000 && (DateTime)jobRows[0]["created_on"] < jobTreshold)
			{
				var jobsToDelete = jobRows.OfType<DataRow>().OrderByDescending(r => r["created_on"]).Select(r => (Guid)r["id"]).Skip(1000).ToList();
				foreach (var jobId in jobsToDelete)
				{
					string deleteSql = $"DELETE FROM jobs WHERE id = @id";
					List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
					parameters.Add(new NpgsqlParameter("id", jobId) { NpgsqlDbType = NpgsqlDbType.Uuid });
					ExecuteNonQuerySqlCommand(deleteSql, parameters);
				}
			}
		}

		/// <inheritdoc />
		public void ClearJobLogs()
		{
			//clear Canceled, Failed, Finished and Aborted jobs older than 30 days and if there is more than 1000 records
			string sql = "SELECT id, created_on FROM jobs WHERE status = 3 OR status = 4 OR status = 5 OR status = 6";
			var jobTable = ExecuteQuerySqlCommand(sql);
			var jobRows = jobTable.Rows;
			var jobsToDelete = jobRows.OfType<DataRow>().Select(r => (Guid)r["id"]).ToList();
			foreach (var jobId in jobsToDelete)
			{
				string deleteSql = $"DELETE FROM jobs WHERE id = @id";
				List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
				parameters.Add(new NpgsqlParameter("id", jobId) { NpgsqlDbType = NpgsqlDbType.Uuid });
				ExecuteNonQuerySqlCommand(deleteSql, parameters);
			}
		}

		/// <inheritdoc />
		public void ClearErrorLogs()
		{
			//clear system logs older than 30 days and if there is more than 1000 records
			string logSql = "SELECT id FROM system_log";
			var logTable = ExecuteQuerySqlCommand(logSql);
			var logRows = logTable.Rows;

			var logsToDelete = logRows.OfType<DataRow>().Select(r => (Guid)r["id"]).ToList();
			foreach (var logId in logsToDelete)
			{
				string deleteSql = $"DELETE FROM system_log WHERE id = @id";
				List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
				parameters.Add(new NpgsqlParameter("id", logId) { NpgsqlDbType = NpgsqlDbType.Uuid });
				ExecuteNonQuerySqlCommand(deleteSql, parameters);
			}

		}


		#region << Helper methods >>

		/// <summary>
		/// Executes a non-query SQL command against the Admin service database.
		/// Opens a new connection, executes the command, and returns whether any rows were affected.
		/// </summary>
		/// <param name="sql">The SQL command text to execute.</param>
		/// <param name="parameters">Optional list of NpgsqlParameter instances for parameterized queries.</param>
		/// <returns>True if the command affected one or more rows; otherwise false.</returns>
		private bool ExecuteNonQuerySqlCommand(string sql, List<NpgsqlParameter> parameters = null)
		{
			using (NpgsqlConnection con = new NpgsqlConnection(_connectionString))
			{
				try
				{
					con.Open();
					NpgsqlCommand command = new NpgsqlCommand(sql, con);
					command.CommandType = CommandType.Text;
					if (parameters != null && parameters.Count > 0)
						command.Parameters.AddRange(parameters.ToArray());
					return command.ExecuteNonQuery() > 0;
				}
				finally
				{
					con.Close();
				}
			}
		}

		/// <summary>
		/// Executes a query SQL command against the Admin service database and returns the results as a DataTable.
		/// Opens a new connection, fills a DataTable via NpgsqlDataAdapter, and returns it.
		/// </summary>
		/// <param name="sql">The SQL query text to execute.</param>
		/// <param name="parameters">Optional list of NpgsqlParameter instances for parameterized queries.</param>
		/// <returns>A DataTable containing the query results.</returns>
		private DataTable ExecuteQuerySqlCommand(string sql, List<NpgsqlParameter> parameters = null)
		{
			using (NpgsqlConnection con = new NpgsqlConnection(_connectionString))
			{
				try
				{
					con.Open();
					NpgsqlCommand command = new NpgsqlCommand(sql, con);
					command.CommandType = CommandType.Text;
					if (parameters != null && parameters.Count > 0)
						command.Parameters.AddRange(parameters.ToArray());

					DataTable resultTable = new DataTable();
					NpgsqlDataAdapter adapter = new NpgsqlDataAdapter(command);
					adapter.Fill(resultTable);
					return resultTable;
				}
				finally
				{
					con.Close();
				}
			}
		}

		#endregion
	}
}
