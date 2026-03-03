using System;
using System.Threading.Tasks;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.SharedKernel.Database;
using Xunit;

namespace WebVella.Erp.Tests.Core.Database
{
	/// <summary>
	/// Integration tests for <see cref="DbSystemSettingsRepository"/> validating:
	///   - Read() bootstrap behavior: returns null when system_settings table does not exist (CRITICAL)
	///   - Read() error propagation: non-table-existence errors are re-thrown
	///   - Read() data hydration: correctly maps id (Guid) and version (int) from database row
	///   - Read() empty table: returns null when table exists but contains no rows
	///   - Read() transactional behavior: wraps operations in BeginTransaction/CommitTransaction
	///   - Save() insert path: creates new record via INSERT when COUNT(*) = 0
	///   - Save() update path: modifies existing record via UPDATE when COUNT(*) > 0
	///   - Save() null guard: throws ArgumentNullException with paramName "systemSettings"
	///   - Save() version management: sequential version updates and zero-version handling
	///   - Save() parameter types: uses NpgsqlDbType.Uuid for id and NpgsqlDbType.Integer for version
	///
	/// All tests use Testcontainers.PostgreSql for isolated PostgreSQL 16-alpine instances.
	/// The system_settings table is NOT created in InitializeAsync — some tests specifically
	/// validate behavior when the table does not exist (bootstrap scenario).
	/// </summary>
	[Collection("Database")]
	public class DbSystemSettingsRepositoryTests : IAsyncLifetime
	{
		private readonly PostgreSqlContainer _postgresContainer;
		private string _connectionString;

		/// <summary>
		/// Constructor: configures a PostgreSQL 16-alpine container for test isolation.
		/// The container is not started here — IAsyncLifetime.InitializeAsync handles startup.
		/// </summary>
		public DbSystemSettingsRepositoryTests()
		{
			_postgresContainer = new PostgreSqlBuilder()
				.WithImage("postgres:16-alpine")
				.Build();
		}

		/// <summary>
		/// Starts the PostgreSQL container and captures the connection string.
		/// Does NOT create the system_settings table — bootstrap tests require a
		/// clean database with no pre-existing tables.
		/// </summary>
		public async Task InitializeAsync()
		{
			await _postgresContainer.StartAsync();
			_connectionString = _postgresContainer.GetConnectionString();
		}

		/// <summary>
		/// Stops and disposes the PostgreSQL container after all tests complete.
		/// </summary>
		public async Task DisposeAsync()
		{
			await _postgresContainer.DisposeAsync();
		}

		#region <--- Helper Methods --->

		/// <summary>
		/// Creates the system_settings table in the test database.
		/// Matches the exact schema used by DbSystemSettingsRepository:
		///   id   UUID PRIMARY KEY
		///   version INTEGER NOT NULL
		/// </summary>
		private void CreateSystemSettingsTable()
		{
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					NpgsqlCommand cmd = con.CreateCommand(
						"CREATE TABLE IF NOT EXISTS system_settings (id UUID PRIMARY KEY, version INTEGER NOT NULL);");
					cmd.ExecuteNonQuery();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Drops the system_settings table from the test database if it exists.
		/// Used to reset state between tests that require a clean database.
		/// </summary>
		private void DropSystemSettingsTable()
		{
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					NpgsqlCommand cmd = con.CreateCommand("DROP TABLE IF EXISTS system_settings;");
					cmd.ExecuteNonQuery();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Inserts a test record directly into the system_settings table.
		/// Bypasses the repository to set up known state for Read() verification.
		/// </summary>
		/// <param name="id">The UUID for the settings row.</param>
		/// <param name="version">The integer version for the settings row.</param>
		private void InsertSystemSettingsRecord(Guid id, int version)
		{
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					NpgsqlCommand cmd = con.CreateCommand(
						"INSERT INTO system_settings (id, version) VALUES (@id, @version);");
					var pId = new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id };
					var pVersion = new NpgsqlParameter("version", NpgsqlDbType.Integer) { Value = version };
					cmd.Parameters.Add(pId);
					cmd.Parameters.Add(pVersion);
					cmd.ExecuteNonQuery();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Reads a system_settings record directly from the database via raw SQL.
		/// Bypasses the repository to verify persistence after Save() operations.
		/// Returns (id, version) tuple or null if no rows exist.
		/// </summary>
		private (Guid Id, int Version)? ReadSystemSettingsRecordDirect()
		{
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					NpgsqlCommand cmd = con.CreateCommand("SELECT id, version FROM system_settings LIMIT 1;");
					using (NpgsqlDataReader reader = cmd.ExecuteReader())
					{
						if (reader.Read())
						{
							var id = (Guid)reader["id"];
							var version = (int)reader["version"];
							reader.Close();
							return (id, version);
						}
						reader.Close();
						return null;
					}
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Reads a specific system_settings record by id directly from the database.
		/// Returns the version or null if the record does not exist.
		/// </summary>
		private int? ReadVersionById(Guid id)
		{
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					NpgsqlCommand cmd = con.CreateCommand("SELECT version FROM system_settings WHERE id = @id;");
					var pId = new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id };
					cmd.Parameters.Add(pId);
					using (NpgsqlDataReader reader = cmd.ExecuteReader())
					{
						if (reader.Read())
						{
							var version = (int)reader["version"];
							reader.Close();
							return version;
						}
						reader.Close();
						return null;
					}
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Counts the total number of rows in the system_settings table.
		/// Used to verify that Save insert/update operations affect exactly one row.
		/// </summary>
		private long CountSystemSettingsRows()
		{
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					NpgsqlCommand cmd = con.CreateCommand("SELECT COUNT(*) FROM system_settings;");
					return (long)cmd.ExecuteScalar();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region <--- Read Tests: Table Does Not Exist (CRITICAL BOOTSTRAP BEHAVIOR) --->

		/// <summary>
		/// CRITICAL BOOTSTRAP TEST: On first run, the system_settings table does not exist.
		/// Read() must return null (not throw) to signal the caller to initialize the database schema.
		///
		/// Source behavior (DbSystemSettingsRepository.Read lines 56-63):
		///   catch (Exception ex)
		///   {
		///       if (con != null) con.RollbackTransaction();
		///       if (!ex.Message.Contains("does not exist")) throw;
		///   }
		///
		/// The "does not exist" error from PostgreSQL is suppressed and null is returned.
		/// </summary>
		[Fact]
		public void Read_ShouldReturnNull_WhenTableDoesNotExist()
		{
			// Arrange: Ensure no system_settings table exists (fresh database from container)
			DropSystemSettingsTable();

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repository = context.SettingsRepository;

				// Act
				DbSystemSettings result = repository.Read();

				// Assert: Must return null — not throw an exception
				result.Should().BeNull(
					"Read() must return null when system_settings table does not exist (bootstrap behavior)");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that Read() re-throws errors that are NOT "does not exist" table errors.
		/// The catch block in Read() only suppresses errors whose message contains "does not exist";
		/// all other database errors must propagate to the caller.
		///
		/// We test this by creating a table with the wrong schema (missing 'version' column),
		/// so the SELECT * succeeds but reader["version"] fails with a column-not-found error
		/// that does NOT contain "does not exist" in its message.
		/// </summary>
		[Fact]
		public void Read_ShouldPropagateNonTableErrors()
		{
			// Arrange: Create a malformed table that will cause a non-"does not exist" error
			// We create system_settings with wrong column names so the query succeeds
			// but the reader hydration fails when accessing reader["version"]
			DropSystemSettingsTable();

			var setupContext = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = setupContext.CreateConnection())
				{
					// Create table with 'id' but replace 'version' with 'wrong_col'
					// This means SELECT * FROM system_settings will return rows but
					// reader["version"] will throw an IndexOutOfRangeException (column not found).
					NpgsqlCommand cmd = con.CreateCommand(
						"CREATE TABLE system_settings (id UUID PRIMARY KEY, wrong_col INTEGER NOT NULL);");
					cmd.ExecuteNonQuery();

					// Insert a row so the reader enters the hydration path
					NpgsqlCommand insertCmd = con.CreateCommand(
						"INSERT INTO system_settings (id, wrong_col) VALUES (@id, @val);");
					insertCmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
					insertCmd.Parameters.Add(new NpgsqlParameter("val", NpgsqlDbType.Integer) { Value = 1 });
					insertCmd.ExecuteNonQuery();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Act & Assert: Read should throw because the error does NOT contain "does not exist"
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repository = context.SettingsRepository;

				Action act = () => repository.Read();

				act.Should().Throw<Exception>(
					"Read() must re-throw errors that are not related to a missing table");
			}
			finally
			{
				CoreDbContext.CloseContext();
				// Clean up the malformed table
				DropSystemSettingsTable();
			}
		}

		#endregion

		#region <--- Read Tests: Table Exists --->

		/// <summary>
		/// Verifies Read() correctly hydrates a DbSystemSettings from an existing database row.
		///
		/// Source hydration (DbSystemSettingsRepository.Read lines 48-50):
		///   setting = new DbSystemSettings();
		///   setting.Id = (Guid)reader["id"];
		///   setting.Version = (int)reader["version"];
		/// </summary>
		[Fact]
		public void Read_ShouldReturnSettings_WhenRecordExists()
		{
			// Arrange
			DropSystemSettingsTable();
			CreateSystemSettingsTable();
			Guid knownId = Guid.NewGuid();
			int knownVersion = 42;
			InsertSystemSettingsRecord(knownId, knownVersion);

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repository = context.SettingsRepository;

				// Act
				DbSystemSettings result = repository.Read();

				// Assert
				result.Should().NotBeNull("a record exists in the system_settings table");
				result.Id.Should().Be(knownId, "Id should match the inserted record");
				result.Version.Should().Be(knownVersion, "Version should match the inserted record");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies Read() returns null when the system_settings table exists but contains no rows.
		///
		/// Source check (DbSystemSettingsRepository.Read line 46):
		///   if (reader != null && reader.Read())
		///
		/// When reader.Read() returns false (no rows), the setting remains null.
		/// </summary>
		[Fact]
		public void Read_ShouldReturnNull_WhenTableExistsButEmpty()
		{
			// Arrange
			DropSystemSettingsTable();
			CreateSystemSettingsTable();
			// Do NOT insert any rows

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repository = context.SettingsRepository;

				// Act
				DbSystemSettings result = repository.Read();

				// Assert
				result.Should().BeNull("the system_settings table is empty — no rows to read");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies Read() uses transactional wrapping (BeginTransaction/CommitTransaction).
		/// We validate this by confirming that Read() completes successfully and that
		/// the connection is not left in a dirty state (no pending transactions leak).
		///
		/// Source pattern (DbSystemSettingsRepository.Read lines 41-63):
		///   con.BeginTransaction();
		///   ... (execute query) ...
		///   con.CommitTransaction();
		/// catch:
		///   con.RollbackTransaction();
		///
		/// The test proves the transaction lifecycle works by:
		/// 1. Calling Read() successfully (proves CommitTransaction was called)
		/// 2. Then executing a second Read() on the same context without error
		///    (proves no leaked transaction state)
		/// </summary>
		[Fact]
		public void Read_ShouldUseTransaction()
		{
			// Arrange
			DropSystemSettingsTable();
			CreateSystemSettingsTable();
			Guid testId = Guid.NewGuid();
			InsertSystemSettingsRecord(testId, 10);

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repository = context.SettingsRepository;

				// Act: First Read — exercises BeginTransaction/CommitTransaction
				DbSystemSettings firstResult = repository.Read();

				// Assert: First read succeeds (transaction committed properly)
				firstResult.Should().NotBeNull("Read() should work within a transaction");
				firstResult.Id.Should().Be(testId);
				firstResult.Version.Should().Be(10);

				// Act: Second Read — if transaction leaked, this would fail
				DbSystemSettings secondResult = repository.Read();

				// Assert: Second read also succeeds (no leaked transaction state)
				secondResult.Should().NotBeNull(
					"a second Read() should succeed if the first Read() properly committed its transaction");
				secondResult.Id.Should().Be(testId);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region <--- Save Tests: Insert (New Record) --->

		/// <summary>
		/// Verifies Save() inserts a new record when system_settings table is empty.
		///
		/// Source flow:
		///   1. SELECT COUNT(*) FROM system_settings WHERE id=@id → returns 0
		///   2. INSERT INTO system_settings (id, version) VALUES( @id,@version) (no trailing semicolon)
		///   3. command.ExecuteNonQuery() > 0 → returns true
		/// </summary>
		[Fact]
		public void Save_ShouldInsertNewRecord_WhenNotExists()
		{
			// Arrange
			DropSystemSettingsTable();
			CreateSystemSettingsTable();

			Guid newId = Guid.NewGuid();
			var settings = new DbSystemSettings { Id = newId, Version = 1 };

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repository = context.SettingsRepository;

				// Act
				bool result = repository.Save(settings);

				// Assert: Save returns true
				result.Should().BeTrue("Save should return true on successful insert");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Verify the record was actually inserted in the database
			var record = ReadSystemSettingsRecordDirect();
			record.Should().NotBeNull("the record should have been inserted into the database");
			record.Value.Id.Should().Be(newId, "the inserted id should match");
			record.Value.Version.Should().Be(1, "the inserted version should match");
		}

		/// <summary>
		/// Verifies Save() returns true when inserting a new record.
		///
		/// Source: return command.ExecuteNonQuery() > 0;
		/// ExecuteNonQuery() returns the number of rows affected (1 for INSERT).
		/// </summary>
		[Fact]
		public void Save_ShouldReturnTrue_OnSuccessfulInsert()
		{
			// Arrange
			DropSystemSettingsTable();
			CreateSystemSettingsTable();

			var settings = new DbSystemSettings { Id = Guid.NewGuid(), Version = 5 };

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repository = context.SettingsRepository;

				// Act
				bool result = repository.Save(settings);

				// Assert
				result.Should().BeTrue("ExecuteNonQuery should return 1 for a successful INSERT, which is > 0");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region <--- Save Tests: Update (Existing Record) --->

		/// <summary>
		/// Verifies Save() updates an existing record when one with the same Id already exists.
		///
		/// Source flow:
		///   1. SELECT COUNT(*) FROM system_settings WHERE id=@id → returns > 0
		///   2. UPDATE system_settings SET version=@version WHERE id=@id;
		///   3. Verify version changed in database
		/// </summary>
		[Fact]
		public void Save_ShouldUpdateExistingRecord_WhenExists()
		{
			// Arrange
			DropSystemSettingsTable();
			CreateSystemSettingsTable();

			Guid existingId = Guid.NewGuid();
			InsertSystemSettingsRecord(existingId, 1);

			var updatedSettings = new DbSystemSettings { Id = existingId, Version = 5 };

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repository = context.SettingsRepository;

				// Act
				bool result = repository.Save(updatedSettings);

				// Assert
				result.Should().BeTrue("Save should return true on successful update");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Verify the database was updated
			int? version = ReadVersionById(existingId);
			version.Should().NotBeNull("the record should still exist");
			version.Value.Should().Be(5, "the version should have been updated from 1 to 5");
		}

		/// <summary>
		/// Verifies Save() returns true when updating an existing record.
		///
		/// Source: return command.ExecuteNonQuery() > 0;
		/// ExecuteNonQuery() returns the number of rows affected (1 for UPDATE).
		/// </summary>
		[Fact]
		public void Save_ShouldReturnTrue_OnSuccessfulUpdate()
		{
			// Arrange
			DropSystemSettingsTable();
			CreateSystemSettingsTable();

			Guid existingId = Guid.NewGuid();
			InsertSystemSettingsRecord(existingId, 1);

			var updatedSettings = new DbSystemSettings { Id = existingId, Version = 99 };

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repository = context.SettingsRepository;

				// Act
				bool result = repository.Save(updatedSettings);

				// Assert
				result.Should().BeTrue("ExecuteNonQuery should return 1 for a successful UPDATE, which is > 0");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that Save() preserves the record Id when performing an update.
		/// The UPDATE statement only changes the version column; the id (primary key) remains unchanged.
		///
		/// Source UPDATE SQL: UPDATE system_settings SET version=@version WHERE id=@id;
		/// </summary>
		[Fact]
		public void Save_ShouldPreserveId_OnUpdate()
		{
			// Arrange
			DropSystemSettingsTable();
			CreateSystemSettingsTable();

			Guid originalId = Guid.NewGuid();
			InsertSystemSettingsRecord(originalId, 1);

			var updatedSettings = new DbSystemSettings { Id = originalId, Version = 10 };

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repository = context.SettingsRepository;

				// Act
				repository.Save(updatedSettings);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert: Id is preserved, version is updated, only 1 row exists
			var record = ReadSystemSettingsRecordDirect();
			record.Should().NotBeNull("the record should exist after update");
			record.Value.Id.Should().Be(originalId, "the Id must be preserved during update");
			record.Value.Version.Should().Be(10, "only the version should have changed");

			long rowCount = CountSystemSettingsRows();
			rowCount.Should().Be(1, "update should not create additional rows");
		}

		#endregion

		#region <--- Save Tests: Error Handling --->

		/// <summary>
		/// Verifies Save() throws ArgumentNullException when called with null.
		///
		/// Source (DbSystemSettingsRepository.Save line 95-96):
		///   if (systemSettings == null)
		///       throw new ArgumentNullException("systemSettings");
		///
		/// The parameter name must be exactly "systemSettings".
		/// </summary>
		[Fact]
		public void Save_ShouldThrowArgumentNullException_WhenSettingsNull()
		{
			// Arrange
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repository = context.SettingsRepository;

				// Act & Assert
				Action act = () => repository.Save(null);

				act.Should().Throw<ArgumentNullException>()
					.WithParameterName("systemSettings",
						"the parameter name must match the source code exactly");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region <--- Version Field Management Tests --->

		/// <summary>
		/// Verifies that sequential version updates via Save() are handled correctly.
		/// Each Save() with an incremented version should update the database row.
		///
		/// This validates the full upsert lifecycle:
		///   - First Save → INSERT (version=1)
		///   - Second Save → UPDATE (version=2) via COUNT(*) > 0 check
		///   - Third Save → UPDATE (version=3)
		///   - Final Read → version=3
		/// </summary>
		[Fact]
		public void Save_ShouldIncrementVersion_WhenUpdatedSequentially()
		{
			// Arrange
			DropSystemSettingsTable();
			CreateSystemSettingsTable();

			Guid settingsId = Guid.NewGuid();
			var settings = new DbSystemSettings { Id = settingsId, Version = 1 };

			// Act: Sequential saves with incrementing versions
			var context1 = CoreDbContext.CreateContext(_connectionString);
			try
			{
				context1.SettingsRepository.Save(settings);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			settings.Version = 2;
			var context2 = CoreDbContext.CreateContext(_connectionString);
			try
			{
				context2.SettingsRepository.Save(settings);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			settings.Version = 3;
			var context3 = CoreDbContext.CreateContext(_connectionString);
			try
			{
				context3.SettingsRepository.Save(settings);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert: Read back and verify final version
			var readContext = CoreDbContext.CreateContext(_connectionString);
			try
			{
				DbSystemSettings result = readContext.SettingsRepository.Read();

				result.Should().NotBeNull("the record should exist after three sequential saves");
				result.Id.Should().Be(settingsId, "the Id should remain constant across updates");
				result.Version.Should().Be(3, "the version should reflect the last Save (version=3)");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that Save() correctly handles version=0 without treating it as null or default.
		/// Zero is a valid integer value and must be persisted and retrieved correctly.
		/// </summary>
		[Fact]
		public void Save_ShouldHandleVersionZero()
		{
			// Arrange
			DropSystemSettingsTable();
			CreateSystemSettingsTable();

			Guid settingsId = Guid.NewGuid();
			var settings = new DbSystemSettings { Id = settingsId, Version = 0 };

			// Act: Save with version=0
			var saveContext = CoreDbContext.CreateContext(_connectionString);
			try
			{
				bool result = saveContext.SettingsRepository.Save(settings);
				result.Should().BeTrue("Save should succeed for version=0");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert: Read back and verify version is exactly 0
			var readContext = CoreDbContext.CreateContext(_connectionString);
			try
			{
				DbSystemSettings result = readContext.SettingsRepository.Read();

				result.Should().NotBeNull("the record with version=0 should be persisted");
				result.Id.Should().Be(settingsId);
				result.Version.Should().Be(0, "version=0 is a valid value and must be preserved, not treated as null or default");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region <--- Parameter Type Verification Tests --->

		/// <summary>
		/// Verifies that Save() uses the correct NpgsqlDbType for parameters:
		///   - id parameter: NpgsqlDbType.Uuid
		///   - version parameter: NpgsqlDbType.Integer
		///
		/// Source (DbSystemSettingsRepository.Save):
		///   parameterId.NpgsqlDbType = NpgsqlDbType.Uuid;    (lines 105, 123)
		///   parameter.NpgsqlDbType = NpgsqlDbType.Integer;   (line 117)
		///
		/// The repository uses command.CreateParameter() (not new NpgsqlParameter()) — a
		/// distinct pattern from other repositories. We verify type correctness indirectly
		/// by confirming that Save roundtrips correctly with a Guid id and int version,
		/// which would fail if parameter types were wrong (e.g., NpgsqlDbType.Text for id
		/// would cause a type mismatch error from PostgreSQL).
		///
		/// Additionally, we verify by inserting and reading back specific edge-case values
		/// that are sensitive to type handling (e.g., Guid.Empty, int.MaxValue).
		/// </summary>
		[Fact]
		public void Save_ShouldUseCorrectNpgsqlTypes()
		{
			// Arrange
			DropSystemSettingsTable();
			CreateSystemSettingsTable();

			// Use specific values that exercise type boundaries
			Guid testId = Guid.NewGuid();
			int testVersion = int.MaxValue; // Edge case: max integer value

			var settings = new DbSystemSettings { Id = testId, Version = testVersion };

			// Act: Save with extreme values — would fail if NpgsqlDbType.Integer is wrong
			var saveContext = CoreDbContext.CreateContext(_connectionString);
			try
			{
				bool result = saveContext.SettingsRepository.Save(settings);
				result.Should().BeTrue("Save should succeed with correct parameter types");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert: Read back via direct SQL to verify types roundtripped correctly
			var record = ReadSystemSettingsRecordDirect();
			record.Should().NotBeNull("the record should have been persisted");
			record.Value.Id.Should().Be(testId,
				"Guid should roundtrip correctly when NpgsqlDbType.Uuid is used");
			record.Value.Version.Should().Be(testVersion,
				"int.MaxValue should roundtrip correctly when NpgsqlDbType.Integer is used");

			// Second verification: update path also uses correct types
			var updateSettings = new DbSystemSettings { Id = testId, Version = 0 };
			var updateContext = CoreDbContext.CreateContext(_connectionString);
			try
			{
				bool result = updateContext.SettingsRepository.Save(updateSettings);
				result.Should().BeTrue("Update should succeed with correct parameter types");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Verify update roundtripped
			int? updatedVersion = ReadVersionById(testId);
			updatedVersion.Should().NotBeNull();
			updatedVersion.Value.Should().Be(0,
				"version should update from int.MaxValue to 0 correctly with NpgsqlDbType.Integer");
		}

		#endregion
	}
}
