using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using Moq;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebVella.Erp.Service.Admin.Services;

namespace WebVella.Erp.Tests.Admin.Services
{
	/// <summary>
	/// Integration tests for <see cref="LogService"/> from the Admin/SDK microservice.
	/// Tests exercise the three cleanup methods (ClearJobAndErrorLogs, ClearJobLogs, ClearErrorLogs)
	/// against a real PostgreSQL 16 instance managed by Testcontainers.
	/// Covers retention-based cleanup, unconditional purge, SQL parameterization,
	/// and edge cases (boundary conditions at 1000 row count and 30-day threshold).
	/// </summary>
	public class LogServiceTests : IAsyncLifetime
	{
		private readonly PostgreSqlContainer _postgresContainer;
		private string _connectionString;

		/// <summary>
		/// Initializes the PostgreSQL container builder with the postgres:16-alpine image
		/// matching the AAP docker-compose specification.
		/// </summary>
		public LogServiceTests()
		{
			_postgresContainer = new PostgreSqlBuilder("postgres:16-alpine")
				.Build();
		}

		/// <summary>
		/// Starts the PostgreSQL container and creates the system_log and jobs tables
		/// required by LogService SQL queries.
		/// </summary>
		public async Task InitializeAsync()
		{
			await _postgresContainer.StartAsync();
			_connectionString = _postgresContainer.GetConnectionString();
			await CreateTables();
		}

		/// <summary>
		/// Stops and disposes the PostgreSQL container after each test.
		/// </summary>
		public async Task DisposeAsync()
		{
			await _postgresContainer.DisposeAsync();
		}

		#region << Helper Methods >>

		/// <summary>
		/// Creates a LogService instance configured with the Testcontainers connection string.
		/// Uses ConfigurationBuilder with in-memory collection to supply the connection string
		/// and a mocked ILogger to satisfy the constructor DI requirements.
		/// </summary>
		private LogService CreateLogService()
		{
			var inMemorySettings = new Dictionary<string, string>
			{
				{ "ConnectionStrings:Default", _connectionString }
			};
			IConfiguration config = new ConfigurationBuilder()
				.AddInMemoryCollection(inMemorySettings)
				.Build();
			var loggerMock = new Mock<ILogger<LogService>>();
			return new LogService(config, loggerMock.Object);
		}

		/// <summary>
		/// Creates the system_log and jobs tables matching the schema expected by LogService SQL queries.
		/// system_log: id UUID PK, created_on TIMESTAMPTZ NOT NULL, message TEXT
		/// jobs: id UUID PK, created_on TIMESTAMPTZ NOT NULL, status INTEGER NOT NULL
		/// </summary>
		private async Task CreateTables()
		{
			using var conn = new NpgsqlConnection(_connectionString);
			await conn.OpenAsync();
			using var cmd = new NpgsqlCommand(@"
				CREATE TABLE IF NOT EXISTS system_log (
					id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
					created_on TIMESTAMPTZ NOT NULL DEFAULT NOW(),
					message TEXT
				);
				CREATE TABLE IF NOT EXISTS jobs (
					id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
					created_on TIMESTAMPTZ NOT NULL DEFAULT NOW(),
					status INTEGER NOT NULL DEFAULT 0
				);", conn);
			await cmd.ExecuteNonQueryAsync();
		}

		/// <summary>
		/// Inserts <paramref name="count"/> rows into the system_log table with sequential
		/// created_on dates starting from <paramref name="startDate"/> at 1-minute intervals.
		/// Uses PostgreSQL generate_series for efficient bulk insertion.
		/// </summary>
		/// <param name="count">Number of rows to insert.</param>
		/// <param name="startDate">The created_on timestamp for the first (oldest) row.</param>
		private async Task InsertSystemLogRows(int count, DateTime startDate)
		{
			if (count <= 0) return;
			using var conn = new NpgsqlConnection(_connectionString);
			await conn.OpenAsync();
			using var cmd = new NpgsqlCommand(@"
				INSERT INTO system_log (id, created_on, message)
				SELECT gen_random_uuid(),
					   @start_date + (i * interval '1 minute'),
					   'Test log entry ' || i
				FROM generate_series(0, @upper_bound) AS s(i)", conn);
			cmd.Parameters.Add(new NpgsqlParameter("start_date", NpgsqlDbType.TimestampTz) { Value = startDate });
			cmd.Parameters.AddWithValue("upper_bound", count - 1);
			await cmd.ExecuteNonQueryAsync();
		}

		/// <summary>
		/// Inserts <paramref name="count"/> rows into the jobs table with the given
		/// <paramref name="status"/> and sequential created_on dates starting from
		/// <paramref name="startDate"/> at 1-minute intervals.
		/// Status values: 1=Running, 2=Pending, 3=Canceled, 4=Failed, 5=Finished, 6=Aborted.
		/// </summary>
		/// <param name="count">Number of rows to insert.</param>
		/// <param name="status">The job status integer value.</param>
		/// <param name="startDate">The created_on timestamp for the first (oldest) row.</param>
		private async Task InsertJobRows(int count, int status, DateTime startDate)
		{
			if (count <= 0) return;
			using var conn = new NpgsqlConnection(_connectionString);
			await conn.OpenAsync();
			using var cmd = new NpgsqlCommand(@"
				INSERT INTO jobs (id, created_on, status)
				SELECT gen_random_uuid(),
					   @start_date + (i * interval '1 minute'),
					   @status
				FROM generate_series(0, @upper_bound) AS s(i)", conn);
			cmd.Parameters.Add(new NpgsqlParameter("start_date", NpgsqlDbType.TimestampTz) { Value = startDate });
			cmd.Parameters.AddWithValue("status", status);
			cmd.Parameters.AddWithValue("upper_bound", count - 1);
			await cmd.ExecuteNonQueryAsync();
		}

		/// <summary>
		/// Returns the total row count for the specified table.
		/// </summary>
		/// <param name="tableName">The name of the table to count rows in.</param>
		/// <returns>The number of rows in the table.</returns>
		private async Task<int> GetRowCount(string tableName)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			await conn.OpenAsync();
			using var cmd = new NpgsqlCommand($"SELECT COUNT(*)::int FROM {tableName}", conn);
			return (int)await cmd.ExecuteScalarAsync();
		}

		/// <summary>
		/// Returns the row count for the specified table filtered by an array of status values.
		/// Uses PostgreSQL ANY operator with a parameterized integer array.
		/// </summary>
		/// <param name="tableName">The name of the table to count rows in.</param>
		/// <param name="statuses">Array of status values to filter by.</param>
		/// <returns>The number of matching rows.</returns>
		private async Task<int> GetJobRowCount(string tableName, int[] statuses)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			await conn.OpenAsync();
			using var cmd = new NpgsqlCommand(
				$"SELECT COUNT(*)::int FROM {tableName} WHERE status = ANY(@statuses)", conn);
			cmd.Parameters.AddWithValue("statuses", statuses);
			return (int)await cmd.ExecuteScalarAsync();
		}

		/// <summary>
		/// Truncates both the system_log and jobs tables to reset state.
		/// Available for use if the class is refactored to share a container across tests.
		/// </summary>
		private async Task CleanupTables()
		{
			using var conn = new NpgsqlConnection(_connectionString);
			await conn.OpenAsync();
			using var cmd = new NpgsqlCommand(
				"TRUNCATE TABLE system_log; TRUNCATE TABLE jobs;", conn);
			await cmd.ExecuteNonQueryAsync();
		}

		#endregion

		#region << ClearJobAndErrorLogs — System Log Retention Logic Tests >>

		/// <summary>
		/// Verifies that ClearJobAndErrorLogs keeps the newest 1000 system_log rows
		/// and deletes the rest when the count exceeds 1000 and the oldest row is
		/// more than 30 days old.
		/// Source: LogService.cs line 23 — if (logRows.Count > 1000 && oldest < threshold)
		/// </summary>
		[Fact]
		public async Task ClearJobAndErrorLogs_SystemLogOver1000Rows_OldestOver30Days_KeepsNewest1000()
		{
			// Arrange: Insert 1500 system_log rows, oldest dated 60 days ago
			var startDate = DateTime.UtcNow.AddDays(-60);
			await InsertSystemLogRows(1500, startDate);
			var service = CreateLogService();

			// Act
			service.ClearJobAndErrorLogs();

			// Assert: system_log row count should be exactly 1000
			var count = await GetRowCount("system_log");
			count.Should().Be(1000);

			// Assert: The 1000 remaining rows should be the newest ones
			// The 500 oldest rows (indices 0-499) were deleted; remaining rows start at index 500
			using var conn = new NpgsqlConnection(_connectionString);
			await conn.OpenAsync();
			using var cmd = new NpgsqlCommand("SELECT MIN(created_on) FROM system_log", conn);
			var minDate = (DateTime)await cmd.ExecuteScalarAsync();
			var expectedMinDate = startDate.AddMinutes(500);
			// Allow 1-minute tolerance for timestamp precision
			minDate.Should().BeOnOrAfter(expectedMinDate.AddMinutes(-1));
		}

		/// <summary>
		/// Verifies that ClearJobAndErrorLogs does not delete any system_log rows
		/// when the row count is 1000 or fewer, even if the oldest row exceeds 30 days.
		/// Source: LogService.cs line 23 — if (logRows.Count > 1000 && ...) condition fails
		/// </summary>
		[Fact]
		public async Task ClearJobAndErrorLogs_SystemLogUnder1000Rows_NoDeletions()
		{
			// Arrange: Insert 500 system_log rows, oldest dated 60 days ago
			await InsertSystemLogRows(500, DateTime.UtcNow.AddDays(-60));
			var service = CreateLogService();

			// Act
			service.ClearJobAndErrorLogs();

			// Assert: system_log row count should remain 500 (no change)
			var count = await GetRowCount("system_log");
			count.Should().Be(500);
		}

		/// <summary>
		/// Verifies that ClearJobAndErrorLogs does not delete any system_log rows
		/// when the oldest row is within the 30-day retention window,
		/// even if the row count exceeds 1000.
		/// Source: LogService.cs line 23 — (DateTime)logRows[0]["created_on"] &lt; logTreshold fails
		/// </summary>
		[Fact]
		public async Task ClearJobAndErrorLogs_SystemLogOver1000Rows_OldestWithin30Days_NoDeletions()
		{
			// Arrange: Insert 1500 system_log rows, oldest dated 10 days ago
			await InsertSystemLogRows(1500, DateTime.UtcNow.AddDays(-10));
			var service = CreateLogService();

			// Act
			service.ClearJobAndErrorLogs();

			// Assert: system_log row count should remain 1500
			var count = await GetRowCount("system_log");
			count.Should().Be(1500);
		}

		#endregion

		#region << ClearJobAndErrorLogs — Jobs Retention Logic Tests >>

		/// <summary>
		/// Verifies that ClearJobAndErrorLogs keeps the newest 1000 terminal-status job rows
		/// and deletes the rest when the terminal job count exceeds 1000 and the oldest is
		/// more than 30 days old.
		/// Terminal statuses: 3=Canceled, 4=Failed, 5=Finished, 6=Aborted.
		/// </summary>
		[Fact]
		public async Task ClearJobAndErrorLogs_JobsOver1000TerminalRows_OldestOver30Days_KeepsNewest1000()
		{
			// Arrange: Insert 1500 job rows with mixed terminal statuses, oldest 60 days ago
			// 375 each of status 3 (Canceled), 4 (Failed), 5 (Finished), 6 (Aborted)
			var startDate = DateTime.UtcNow.AddDays(-60);
			await InsertJobRows(375, 3, startDate);
			await InsertJobRows(375, 4, startDate.AddMinutes(375));
			await InsertJobRows(375, 5, startDate.AddMinutes(750));
			await InsertJobRows(375, 6, startDate.AddMinutes(1125));
			var service = CreateLogService();

			// Act
			service.ClearJobAndErrorLogs();

			// Assert: terminal job count should be 1000
			var terminalCount = await GetJobRowCount("jobs", new[] { 3, 4, 5, 6 });
			terminalCount.Should().Be(1000);
		}

		/// <summary>
		/// Verifies that ClearJobAndErrorLogs does not delete any terminal job rows
		/// when the terminal row count is 1000 or fewer.
		/// </summary>
		[Fact]
		public async Task ClearJobAndErrorLogs_JobsUnder1000TerminalRows_NoDeletions()
		{
			// Arrange: Insert 500 job rows with status 5 (Finished), oldest 60 days ago
			await InsertJobRows(500, 5, DateTime.UtcNow.AddDays(-60));
			var service = CreateLogService();

			// Act
			service.ClearJobAndErrorLogs();

			// Assert: job row count should remain 500
			var count = await GetRowCount("jobs");
			count.Should().Be(500);
		}

		/// <summary>
		/// Verifies that ClearJobAndErrorLogs does not delete any terminal job rows
		/// when the oldest terminal job is within the 30-day retention window.
		/// </summary>
		[Fact]
		public async Task ClearJobAndErrorLogs_JobsOver1000TerminalRows_OldestWithin30Days_NoDeletions()
		{
			// Arrange: Insert 1500 job rows with status 4 (Failed), oldest 10 days ago
			await InsertJobRows(1500, 4, DateTime.UtcNow.AddDays(-10));
			var service = CreateLogService();

			// Act
			service.ClearJobAndErrorLogs();

			// Assert: job row count should remain 1500
			var count = await GetRowCount("jobs");
			count.Should().Be(1500);
		}

		/// <summary>
		/// Verifies that ClearJobAndErrorLogs only affects terminal-status jobs (3,4,5,6)
		/// and leaves Running (1) and Pending (2) jobs completely untouched.
		/// Source: LogService.cs line 36 — WHERE status = 3 OR status = 4 OR status = 5 OR status = 6
		/// </summary>
		[Fact]
		public async Task ClearJobAndErrorLogs_RunningJobs_NotAffected()
		{
			// Arrange: Insert 750 running (status 1) + 750 pending (status 2) jobs
			var startDate = DateTime.UtcNow.AddDays(-60);
			await InsertJobRows(750, 1, startDate);
			await InsertJobRows(750, 2, startDate);
			// Also insert 1500 finished (status 5) jobs to trigger retention cleanup
			await InsertJobRows(1500, 5, startDate);
			var service = CreateLogService();

			// Act
			service.ClearJobAndErrorLogs();

			// Assert: Running + Pending job count unchanged (1500)
			var runningPendingCount = await GetJobRowCount("jobs", new[] { 1, 2 });
			runningPendingCount.Should().Be(1500);

			// Assert: Finished job count reduced to 1000 (500 oldest deleted)
			var finishedCount = await GetJobRowCount("jobs", new[] { 5 });
			finishedCount.Should().Be(1000);
		}

		#endregion

		#region << ClearJobLogs — Unconditional Purge Tests >>

		/// <summary>
		/// Verifies that ClearJobLogs unconditionally purges all jobs with terminal statuses
		/// (3=Canceled, 4=Failed, 5=Finished, 6=Aborted) regardless of count or age.
		/// Source: LogService.cs lines 53-67 — no count check, no age check.
		/// </summary>
		[Fact]
		public async Task ClearJobLogs_AllTerminalStatusJobs_PurgedCompletely()
		{
			// Arrange: Insert 200 job rows: 50 each with status 3, 4, 5, 6
			var startDate = DateTime.UtcNow.AddDays(-10);
			await InsertJobRows(50, 3, startDate);
			await InsertJobRows(50, 4, startDate);
			await InsertJobRows(50, 5, startDate);
			await InsertJobRows(50, 6, startDate);
			var service = CreateLogService();

			// Act
			service.ClearJobLogs();

			// Assert: No jobs with terminal statuses should remain
			var terminalCount = await GetJobRowCount("jobs", new[] { 3, 4, 5, 6 });
			terminalCount.Should().Be(0);
		}

		/// <summary>
		/// Verifies that ClearJobLogs does not affect Running (1) and Pending (2) jobs.
		/// Only terminal statuses (3,4,5,6) are targeted for deletion.
		/// </summary>
		[Fact]
		public async Task ClearJobLogs_RunningAndPendingJobs_NotAffected()
		{
			// Arrange: Insert 100 running (1) + 100 pending (2) + 100 finished (5) jobs
			var startDate = DateTime.UtcNow.AddDays(-10);
			await InsertJobRows(100, 1, startDate);
			await InsertJobRows(100, 2, startDate);
			await InsertJobRows(100, 5, startDate);
			var service = CreateLogService();

			// Act
			service.ClearJobLogs();

			// Assert: 200 jobs remain (all running + pending)
			var totalCount = await GetRowCount("jobs");
			totalCount.Should().Be(200);

			// Assert: 0 jobs with status 5 (Finished)
			var finishedCount = await GetJobRowCount("jobs", new[] { 5 });
			finishedCount.Should().Be(0);
		}

		/// <summary>
		/// Verifies that ClearJobLogs completes without error when the jobs table is empty.
		/// This tests the empty result set edge case for the SELECT + DELETE loop.
		/// </summary>
		[Fact]
		public async Task ClearJobLogs_EmptyJobsTable_NoErrors()
		{
			// Arrange: Ensure jobs table is empty (fresh container)
			var initialCount = await GetRowCount("jobs");
			initialCount.Should().Be(0);
			var service = CreateLogService();

			// Act & Assert: ClearJobLogs completes without exception
			Action act = () => service.ClearJobLogs();
			act.Should().NotThrow();
		}

		#endregion

		#region << ClearErrorLogs — Unconditional Purge Tests >>

		/// <summary>
		/// Verifies that ClearErrorLogs unconditionally purges all rows from system_log.
		/// Source: LogService.cs lines 69-85 — selects all ids and deletes one by one.
		/// </summary>
		[Fact]
		public async Task ClearErrorLogs_AllSystemLogRows_PurgedCompletely()
		{
			// Arrange: Insert 500 system_log rows
			await InsertSystemLogRows(500, DateTime.UtcNow.AddDays(-10));
			var service = CreateLogService();

			// Act
			service.ClearErrorLogs();

			// Assert: system_log table should have 0 rows
			var count = await GetRowCount("system_log");
			count.Should().Be(0);
		}

		/// <summary>
		/// Verifies that ClearErrorLogs completes without error when system_log is empty.
		/// Tests the empty result set edge case.
		/// </summary>
		[Fact]
		public async Task ClearErrorLogs_EmptySystemLogTable_NoErrors()
		{
			// Arrange: Ensure system_log table is empty (fresh container)
			var initialCount = await GetRowCount("system_log");
			initialCount.Should().Be(0);
			var service = CreateLogService();

			// Act & Assert: ClearErrorLogs completes without exception
			Action act = () => service.ClearErrorLogs();
			act.Should().NotThrow();
		}

		#endregion

		#region << SQL Parameterization Verification Tests >>

		/// <summary>
		/// Verifies that ClearJobAndErrorLogs uses UUID-typed NpgsqlParameter for DELETE operations.
		/// Source: LogService.cs lines 29-30 — new NpgsqlParameter("id", logId) { NpgsqlDbType = NpgsqlDbType.Uuid }
		/// The test proves UUID parameter handling by inserting rows with UUID primary keys
		/// and verifying the correct rows are deleted without type mismatch errors.
		/// </summary>
		[Fact]
		public async Task ClearJobAndErrorLogs_DeletesUseUuidParameterType()
		{
			// Arrange: Insert 1100 system_log rows with UUID primary keys
			await InsertSystemLogRows(1100, DateTime.UtcNow.AddDays(-60));
			var service = CreateLogService();

			// Act: The method internally uses NpgsqlParameter("id", logId) { NpgsqlDbType = NpgsqlDbType.Uuid }
			// If UUID parameter typing were incorrect, this would throw a type mismatch exception
			service.ClearJobAndErrorLogs();

			// Assert: Exactly 1000 rows should remain, proving UUID-parameterized deletes worked
			var remainingCount = await GetRowCount("system_log");
			remainingCount.Should().Be(1000);

			// Verify remaining rows have valid UUID primary keys (not corrupted by string concatenation)
			using var conn = new NpgsqlConnection(_connectionString);
			await conn.OpenAsync();
			using var cmd = new NpgsqlCommand("SELECT id FROM system_log LIMIT 1", conn);
			var result = await cmd.ExecuteScalarAsync();
			result.Should().BeOfType<Guid>(
				"all IDs should be valid UUIDs after parameterized UUID deletion");
		}

		#endregion

		#region << Connection String Handling Tests >>

		/// <summary>
		/// Verifies that LogService correctly uses the configured connection string
		/// from IConfiguration to connect to the database.
		/// Tests that the dependency-injected connection string (replacing the monolith's
		/// static ErpSettings.ConnectionString) is used for all database operations.
		/// </summary>
		[Fact]
		public async Task LogService_UsesConfiguredConnectionString()
		{
			// Arrange: Insert a row so the table is not empty
			await InsertSystemLogRows(1, DateTime.UtcNow);
			var service = CreateLogService();

			// Act: This accesses the database using the configured connection string
			service.ClearErrorLogs();

			// Assert: If wrong connection string was used, the operation would throw
			// Verify it connected to the correct Testcontainers PostgreSQL and operated successfully
			var count = await GetRowCount("system_log");
			count.Should().Be(0,
				"service should connect to the Testcontainers PostgreSQL " +
				"using the configured connection string and delete all rows");
		}

		#endregion

		#region << Edge Case Tests >>

		/// <summary>
		/// Verifies the boundary condition: exactly 1000 system_log rows should NOT trigger deletion.
		/// Source: LogService.cs line 23 — condition is "logRows.Count > 1000" (strictly greater than).
		/// At exactly 1000, the condition is false and no rows are deleted.
		/// </summary>
		[Fact]
		public async Task ClearJobAndErrorLogs_Exactly1000SystemLogRows_NoDeletions()
		{
			// Arrange: Insert exactly 1000 system_log rows, oldest 60 days ago
			await InsertSystemLogRows(1000, DateTime.UtcNow.AddDays(-60));
			var service = CreateLogService();

			// Act
			service.ClearJobAndErrorLogs();

			// Assert: Count remains 1000 (condition is > 1000, NOT >= 1000)
			var count = await GetRowCount("system_log");
			count.Should().Be(1000);
		}

		/// <summary>
		/// Verifies the boundary condition: 1001 system_log rows triggers deletion of exactly 1 row.
		/// At 1001 rows (just above the 1000 threshold), the method keeps the newest 1000
		/// and deletes the single oldest row.
		/// </summary>
		[Fact]
		public async Task ClearJobAndErrorLogs_1001SystemLogRows_OldestOver30Days_DeletesOne()
		{
			// Arrange: Insert 1001 system_log rows, oldest 60 days ago
			await InsertSystemLogRows(1001, DateTime.UtcNow.AddDays(-60));
			var service = CreateLogService();

			// Act
			service.ClearJobAndErrorLogs();

			// Assert: Count should be 1000 (1 row deleted — the oldest)
			var count = await GetRowCount("system_log");
			count.Should().Be(1000);
		}

		/// <summary>
		/// Verifies the 30-day threshold boundary condition: when the oldest row is at
		/// approximately the 30-day mark, no deletions occur.
		/// Source: LogService.cs line 23 uses strict less-than comparison:
		/// (DateTime)logRows[0]["created_on"] &lt; logTreshold
		/// If oldest == threshold exactly, the condition is false → no deletions.
		/// A 5-second buffer accounts for timing differences between test setup and method execution.
		/// </summary>
		[Fact]
		public async Task ClearJobAndErrorLogs_OldestExactly30DaysAgo_NoDeletions()
		{
			// Arrange: Insert 1500 rows with the oldest at approximately the 30-day threshold.
			// Using AddSeconds(5) as a buffer to ensure the oldest row is just barely NEWER
			// than the threshold computed inside the method (DateTime.UtcNow.AddDays(-30)).
			// This accounts for the small timing gap between test data insertion and method execution.
			var oldestDate = DateTime.UtcNow.AddDays(-30).AddSeconds(5);
			await InsertSystemLogRows(1500, oldestDate);
			var service = CreateLogService();

			// Act
			service.ClearJobAndErrorLogs();

			// Assert: All 1500 rows should remain because the oldest row is not
			// strictly older than the 30-day threshold (strict '<' comparison).
			var count = await GetRowCount("system_log");
			count.Should().Be(1500);
		}

		#endregion
	}
}
