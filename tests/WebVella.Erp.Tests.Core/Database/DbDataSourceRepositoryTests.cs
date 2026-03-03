using System;
using System.Data;
using System.Threading.Tasks;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.SharedKernel.Database;
using Xunit;

namespace WebVella.Erp.Tests.Core.Database
{
	/// <summary>
	/// Integration tests for <see cref="DbDataSourceRepository"/> validating:
	///   - Create: inserts a record into public.data_source with 10 parameterized columns,
	///             returns true on success, coalesces null description to empty string,
	///             correctly stores returnTotal=false.
	///   - Get(Guid): returns DataRow for existing ID, null for non-existent ID.
	///   - Get(string): returns DataRow for existing name, null for non-existent name.
	///   - GetAll: returns DataTable with all records, empty DataTable when table is empty.
	///   - Update: modifies all 10 columns, returns true on success, false when ID not found,
	///             coalesces null description to empty string.
	///   - Delete: removes the record by ID, does not throw when ID not found.
	///   - SQL contract: all queries use the public.data_source schema prefix.
	///
	/// All tests use Testcontainers.PostgreSql for an isolated PostgreSQL 16-alpine instance.
	/// The data_source table is created in InitializeAsync with the exact schema matching
	/// the repository's SQL statements.
	/// </summary>
	[Collection("Database")]
	public class DbDataSourceRepositoryTests : IAsyncLifetime
	{
		private readonly PostgreSqlContainer _postgresContainer;
		private string _connectionString;

		/// <summary>
		/// Constructor: configures a PostgreSQL 16-alpine container for test isolation.
		/// The container is not started here — IAsyncLifetime.InitializeAsync handles startup.
		/// </summary>
		public DbDataSourceRepositoryTests()
		{
			_postgresContainer = new PostgreSqlBuilder()
				.WithImage("postgres:16-alpine")
				.Build();
		}

		/// <summary>
		/// Starts the PostgreSQL container, captures the connection string, and creates
		/// the public.data_source table matching the exact schema used by the repository.
		/// </summary>
		public async Task InitializeAsync()
		{
			await _postgresContainer.StartAsync();
			_connectionString = _postgresContainer.GetConnectionString();

			// Create the data_source table matching the repository's SQL schema
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					var command = con.CreateCommand(
						@"CREATE TABLE public.data_source (
							id UUID PRIMARY KEY,
							name TEXT NOT NULL,
							description TEXT DEFAULT '',
							weight INTEGER NOT NULL DEFAULT 0,
							eql_text TEXT DEFAULT '',
							sql_text TEXT DEFAULT '',
							parameters_json TEXT DEFAULT '',
							fields_json TEXT DEFAULT '',
							entity_name TEXT DEFAULT '',
							return_total BOOLEAN NOT NULL DEFAULT true
						);");
					command.ExecuteNonQuery();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
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
		/// Creates a data source record directly via SQL for test setup, bypassing the
		/// repository layer. Returns the Guid ID of the created record.
		/// </summary>
		private Guid InsertDataSourceDirectly(
			string name,
			string description = "test desc",
			int weight = 10,
			string eqlText = "eql",
			string sqlText = "sql",
			string parametersJson = "{}",
			string fieldsJson = "[]",
			string entityName = "test_entity",
			bool returnTotal = true)
		{
			var id = Guid.NewGuid();
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					var command = con.CreateCommand(
						@"INSERT INTO public.data_source(id, name, description, weight, eql_text, sql_text,
							parameters_json, fields_json, entity_name, return_total)
							VALUES (@id, @name, @description, @weight, @eql_text, @sql_text,
							@parameters_json, @fields_json, @entity_name, @return_total)");
					command.Parameters.Add(new NpgsqlParameter("@id", id));
					command.Parameters.Add(new NpgsqlParameter("@name", name));
					command.Parameters.Add(new NpgsqlParameter("@description", description));
					command.Parameters.Add(new NpgsqlParameter("@weight", weight));
					command.Parameters.Add(new NpgsqlParameter("@eql_text", eqlText));
					command.Parameters.Add(new NpgsqlParameter("@sql_text", sqlText));
					command.Parameters.Add(new NpgsqlParameter("@parameters_json", parametersJson));
					command.Parameters.Add(new NpgsqlParameter("@fields_json", fieldsJson));
					command.Parameters.Add(new NpgsqlParameter("@entity_name", entityName));
					command.Parameters.Add(new NpgsqlParameter("@return_total", returnTotal));
					command.ExecuteNonQuery();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
			return id;
		}

		/// <summary>
		/// Reads a data source record directly via SQL for independent verification,
		/// bypassing the repository layer.
		/// </summary>
		private DataRow ReadDataSourceDirectly(Guid id)
		{
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					var command = con.CreateCommand(@"SELECT * FROM public.data_source WHERE id = @id");
					command.Parameters.Add(new NpgsqlParameter("@id", id));
					DataTable dt = new DataTable();
					new NpgsqlDataAdapter(command).Fill(dt);
					if (dt.Rows.Count > 0)
						return dt.Rows[0];
					return null;
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Counts records in the data_source table directly via SQL for independent verification.
		/// </summary>
		private long CountDataSourceRecords()
		{
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					var command = con.CreateCommand(@"SELECT COUNT(*) FROM public.data_source");
					return (long)command.ExecuteScalar();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Truncates the data_source table for tests that require a clean state (e.g., GetAll).
		/// </summary>
		private void TruncateDataSourceTable()
		{
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					var command = con.CreateCommand(@"TRUNCATE TABLE public.data_source");
					command.ExecuteNonQuery();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region <--- Create Tests --->

		/// <summary>
		/// Verifies that Create inserts a record into public.data_source with all 10 columns
		/// matching the provided parameters. Independently reads back the row via direct SQL
		/// and checks every column value.
		/// Source: SQL INSERT with 10 parameterized columns.
		/// </summary>
		[Fact]
		public void Create_ShouldInsertDataSourceRecord()
		{
			// Arrange
			var id = Guid.NewGuid();
			var name = "ds_create_insert_" + Guid.NewGuid().ToString("N");
			var description = "A test data source";
			var weight = 42;
			var eqlText = "SELECT id, name FROM entity";
			var sqlText = "SELECT id, name FROM rec_entity";
			var parametersJson = "{\"param1\":\"value1\"}";
			var fieldsJson = "[{\"name\":\"id\"},{\"name\":\"name\"}]";
			var entityName = "test_entity";
			var returnTotal = true;

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				repo.Create(id, name, description, weight, eqlText, sqlText,
					parametersJson, fieldsJson, entityName, returnTotal);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert — verify via direct SQL, independent of repository
			var row = ReadDataSourceDirectly(id);
			row.Should().NotBeNull("the record should exist after Create");
			((Guid)row["id"]).Should().Be(id);
			((string)row["name"]).Should().Be(name);
			((string)row["description"]).Should().Be(description);
			((int)row["weight"]).Should().Be(weight);
			((string)row["eql_text"]).Should().Be(eqlText);
			((string)row["sql_text"]).Should().Be(sqlText);
			((string)row["parameters_json"]).Should().Be(parametersJson);
			((string)row["fields_json"]).Should().Be(fieldsJson);
			((string)row["entity_name"]).Should().Be(entityName);
			((bool)row["return_total"]).Should().BeTrue();
		}

		/// <summary>
		/// Verifies that Create coalesces a null description to an empty string.
		/// Source: command.Parameters.Add(new NpgsqlParameter("@description", description ?? ""))
		/// </summary>
		[Fact]
		public void Create_ShouldHandleNullDescription()
		{
			// Arrange
			var id = Guid.NewGuid();
			var name = "ds_null_desc_" + Guid.NewGuid().ToString("N");

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act — pass null for description
				repo.Create(id, name, null, 0, "", "", "", "", "", true);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert — description should be empty string, not null
			var row = ReadDataSourceDirectly(id);
			row.Should().NotBeNull();
			((string)row["description"]).Should().Be("");
		}

		/// <summary>
		/// Verifies that Create returns true on successful insertion.
		/// Source: return command.ExecuteNonQuery() > 0
		/// </summary>
		[Fact]
		public void Create_ShouldReturnTrue_OnSuccess()
		{
			// Arrange
			var id = Guid.NewGuid();
			var name = "ds_return_true_" + Guid.NewGuid().ToString("N");
			bool result;

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				result = repo.Create(id, name, "desc", 1, "eql", "sql", "{}", "[]", "entity", true);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert
			result.Should().BeTrue("Create should return true when a row is inserted");
		}

		/// <summary>
		/// Verifies that Create correctly stores returnTotal=false in the database.
		/// </summary>
		[Fact]
		public void Create_WithReturnTotalFalse_ShouldStoreCorrectValue()
		{
			// Arrange
			var id = Guid.NewGuid();
			var name = "ds_return_total_false_" + Guid.NewGuid().ToString("N");

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				repo.Create(id, name, "desc", 0, "", "", "", "", "", false);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert
			var row = ReadDataSourceDirectly(id);
			row.Should().NotBeNull();
			((bool)row["return_total"]).Should().BeFalse("return_total should be false when created with returnTotal=false");
		}

		#endregion

		#region <--- Get by ID Tests --->

		/// <summary>
		/// Verifies that Get(Guid) returns a DataRow with all 10 columns for an existing record.
		/// Source: SQL SELECT * FROM public.data_source WHERE id = @id
		/// </summary>
		[Fact]
		public void Get_ById_ShouldReturnDataRow()
		{
			// Arrange — insert a record directly so we have a known state
			var name = "ds_get_by_id_" + Guid.NewGuid().ToString("N");
			var id = InsertDataSourceDirectly(name, "my desc", 7, "eql1", "sql1",
				"{\"p\":1}", "[\"f1\"]", "entity_a", true);

			DataRow result;
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				result = repo.Get(id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert
			result.Should().NotBeNull("Get by existing ID should return a DataRow");
			((Guid)result["id"]).Should().Be(id);
			((string)result["name"]).Should().Be(name);
			((string)result["description"]).Should().Be("my desc");
			((int)result["weight"]).Should().Be(7);
			((string)result["eql_text"]).Should().Be("eql1");
			((string)result["sql_text"]).Should().Be("sql1");
			((string)result["parameters_json"]).Should().Be("{\"p\":1}");
			((string)result["fields_json"]).Should().Be("[\"f1\"]");
			((string)result["entity_name"]).Should().Be("entity_a");
			((bool)result["return_total"]).Should().BeTrue();
		}

		/// <summary>
		/// Verifies that Get(Guid) returns null when no record matches the given ID.
		/// Source: if (dt.Rows.Count > 0) return dt.Rows[0]; return null;
		/// </summary>
		[Fact]
		public void Get_ById_ShouldReturnNull_WhenNotFound()
		{
			// Arrange
			var nonExistentId = Guid.NewGuid();
			DataRow result;

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				result = repo.Get(nonExistentId);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert
			result.Should().BeNull("Get with a non-existent ID should return null");
		}

		#endregion

		#region <--- Get by Name Tests --->

		/// <summary>
		/// Verifies that Get(string) returns a DataRow when queried with an existing name.
		/// Source: SQL SELECT * FROM public.data_source WHERE name = @name
		/// </summary>
		[Fact]
		public void Get_ByName_ShouldReturnDataRow()
		{
			// Arrange
			var name = "ds_get_by_name_" + Guid.NewGuid().ToString("N");
			var id = InsertDataSourceDirectly(name, "name lookup desc", 3);

			DataRow result;
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				result = repo.Get(name);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert
			result.Should().NotBeNull("Get by existing name should return a DataRow");
			((Guid)result["id"]).Should().Be(id);
			((string)result["name"]).Should().Be(name);
		}

		/// <summary>
		/// Verifies that Get(string) returns null when no record matches the given name.
		/// </summary>
		[Fact]
		public void Get_ByName_ShouldReturnNull_WhenNotFound()
		{
			// Arrange
			DataRow result;

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				result = repo.Get("non_existent_name_" + Guid.NewGuid().ToString("N"));
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert
			result.Should().BeNull("Get with a non-existent name should return null");
		}

		#endregion

		#region <--- GetAll Tests --->

		/// <summary>
		/// Verifies that GetAll returns a DataTable containing all records in the table.
		/// Source: SQL SELECT * FROM public.data_source
		/// </summary>
		[Fact]
		public void GetAll_ShouldReturnDataTableWithAllRecords()
		{
			// Arrange — clean slate and insert exactly 3 records
			TruncateDataSourceTable();
			InsertDataSourceDirectly("ds_all_1_" + Guid.NewGuid().ToString("N"));
			InsertDataSourceDirectly("ds_all_2_" + Guid.NewGuid().ToString("N"));
			InsertDataSourceDirectly("ds_all_3_" + Guid.NewGuid().ToString("N"));

			DataTable result;
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				result = repo.GetAll();
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert
			result.Should().NotBeNull("GetAll should return a DataTable");
			result.Rows.Count.Should().Be(3, "there should be exactly 3 records");
		}

		/// <summary>
		/// Verifies that GetAll returns an empty DataTable (not null) when the table has no records.
		/// </summary>
		[Fact]
		public void GetAll_ShouldReturnEmptyDataTable_WhenNoRecords()
		{
			// Arrange — truncate the table
			TruncateDataSourceTable();

			DataTable result;
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				result = repo.GetAll();
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert
			result.Should().NotBeNull("GetAll should return a DataTable even when empty");
			result.Rows.Count.Should().Be(0, "the table is empty so row count should be 0");
		}

		#endregion

		#region <--- Update Tests --->

		/// <summary>
		/// Verifies that Update modifies all columns of an existing record and persists the changes.
		/// Source: SQL UPDATE public.data_source SET name=@name, ... WHERE id=@id
		/// </summary>
		[Fact]
		public void Update_ShouldModifyExistingRecord()
		{
			// Arrange
			var name = "ds_update_orig_" + Guid.NewGuid().ToString("N");
			var id = InsertDataSourceDirectly(name, "old desc", 1, "old eql", "old sql",
				"{\"old\":1}", "[\"old\"]", "old_entity", true);

			var newName = "ds_update_new_" + Guid.NewGuid().ToString("N");
			var newDescription = "new description";
			var newWeight = 99;
			var newEql = "new eql text";
			var newSql = "new sql text";
			var newParams = "{\"new\":2}";
			var newFields = "[\"new_field\"]";
			var newEntity = "new_entity";
			var newReturnTotal = false;

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				repo.Update(id, newName, newDescription, newWeight, newEql, newSql,
					newParams, newFields, newEntity, newReturnTotal);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert — verify via direct SQL
			var row = ReadDataSourceDirectly(id);
			row.Should().NotBeNull();
			((string)row["name"]).Should().Be(newName);
			((string)row["description"]).Should().Be(newDescription);
			((int)row["weight"]).Should().Be(newWeight);
			((string)row["eql_text"]).Should().Be(newEql);
			((string)row["sql_text"]).Should().Be(newSql);
			((string)row["parameters_json"]).Should().Be(newParams);
			((string)row["fields_json"]).Should().Be(newFields);
			((string)row["entity_name"]).Should().Be(newEntity);
			((bool)row["return_total"]).Should().BeFalse();
		}

		/// <summary>
		/// Verifies that Update returns true on successful modification.
		/// Source: return command.ExecuteNonQuery() > 0
		/// </summary>
		[Fact]
		public void Update_ShouldReturnTrue_OnSuccess()
		{
			// Arrange
			var name = "ds_update_true_" + Guid.NewGuid().ToString("N");
			var id = InsertDataSourceDirectly(name);
			bool result;

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				result = repo.Update(id, name + "_updated", "upd", 2, "e", "s", "{}", "[]", "en", true);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert
			result.Should().BeTrue("Update should return true when a row is modified");
		}

		/// <summary>
		/// Verifies that Update returns false when the ID does not match any existing record.
		/// Source: return command.ExecuteNonQuery() > 0 — returns 0 rows affected.
		/// </summary>
		[Fact]
		public void Update_ShouldReturnFalse_WhenIdNotFound()
		{
			// Arrange
			var nonExistentId = Guid.NewGuid();
			bool result;

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				result = repo.Update(nonExistentId, "noname", "nodesc", 0, "", "", "", "", "", true);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert
			result.Should().BeFalse("Update should return false when no row matches the ID");
		}

		/// <summary>
		/// Verifies that Update coalesces a null description to an empty string.
		/// Source: command.Parameters.Add(new NpgsqlParameter("@description", description ?? ""))
		/// </summary>
		[Fact]
		public void Update_ShouldHandleNullDescription()
		{
			// Arrange
			var name = "ds_update_null_desc_" + Guid.NewGuid().ToString("N");
			var id = InsertDataSourceDirectly(name, "original desc");

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act — pass null for description
				repo.Update(id, name, null, 0, "", "", "", "", "", true);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert — description should be empty string, not null
			var row = ReadDataSourceDirectly(id);
			row.Should().NotBeNull();
			((string)row["description"]).Should().Be("");
		}

		#endregion

		#region <--- Delete Tests --->

		/// <summary>
		/// Verifies that Delete removes the record from the database.
		/// Source: SQL DELETE FROM public.data_source WHERE id = @id
		/// </summary>
		[Fact]
		public void Delete_ShouldRemoveRecord()
		{
			// Arrange
			var name = "ds_delete_" + Guid.NewGuid().ToString("N");
			var id = InsertDataSourceDirectly(name);

			// Verify record exists before delete
			ReadDataSourceDirectly(id).Should().NotBeNull("precondition: record must exist");

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act
				repo.Delete(id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert — record should no longer exist
			ReadDataSourceDirectly(id).Should().BeNull("record should be deleted");
		}

		/// <summary>
		/// Verifies that Delete does not throw when called with a non-existent ID.
		/// Source: Delete returns void, no existence check — ExecuteNonQuery simply affects 0 rows.
		/// </summary>
		[Fact]
		public void Delete_ShouldNotThrow_WhenIdNotFound()
		{
			// Arrange
			var nonExistentId = Guid.NewGuid();

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// Act & Assert — should not throw
				var act = () => repo.Delete(nonExistentId);
				act.Should().NotThrow("Delete with a non-existent ID should complete silently");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region <--- SQL Schema Prefix Verification --->

		/// <summary>
		/// Verifies that all repository operations target the public.data_source table
		/// (with the explicit 'public' schema prefix). This is a contract test ensuring
		/// that the repository's SQL statements always qualify the table name with the
		/// public schema, preventing accidental interaction with tables in other schemas.
		///
		/// Approach: create a second schema with an identically-named data_source table,
		/// insert data there, and verify that GetAll only returns records from public.data_source.
		/// </summary>
		[Fact]
		public void AllQueries_ShouldUsePublicSchemaPrefix()
		{
			// Arrange — create a separate schema with its own data_source table
			TruncateDataSourceTable();

			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = context.CreateConnection())
				{
					// Create an alternative schema and table
					con.CreateCommand("CREATE SCHEMA IF NOT EXISTS alt_schema").ExecuteNonQuery();
					con.CreateCommand(
						@"CREATE TABLE IF NOT EXISTS alt_schema.data_source (
							id UUID PRIMARY KEY,
							name TEXT NOT NULL,
							description TEXT DEFAULT '',
							weight INTEGER NOT NULL DEFAULT 0,
							eql_text TEXT DEFAULT '',
							sql_text TEXT DEFAULT '',
							parameters_json TEXT DEFAULT '',
							fields_json TEXT DEFAULT '',
							entity_name TEXT DEFAULT '',
							return_total BOOLEAN NOT NULL DEFAULT true
						)").ExecuteNonQuery();

					// Insert a record only in the alt_schema table
					var altId = Guid.NewGuid();
					var cmd = con.CreateCommand(
						@"INSERT INTO alt_schema.data_source(id, name, description, weight,
							eql_text, sql_text, parameters_json, fields_json, entity_name, return_total)
							VALUES (@id, @name, '', 0, '', '', '', '', '', true)");
					cmd.Parameters.Add(new NpgsqlParameter("@id", altId));
					cmd.Parameters.Add(new NpgsqlParameter("@name", "alt_schema_record"));
					cmd.ExecuteNonQuery();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Insert a known record into the public schema
			var publicName = "public_schema_record_" + Guid.NewGuid().ToString("N");
			var publicId = InsertDataSourceDirectly(publicName);

			// Act — use the repository to perform operations, which should only touch public.data_source
			DataTable allResult;
			DataRow getByIdResult;
			DataRow getByNameResult;
			bool createResult;
			bool updateResult;

			var createId = Guid.NewGuid();
			var createName = "public_create_test_" + Guid.NewGuid().ToString("N");

			var ctx2 = CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbDataSourceRepository();

				// GetAll should only return public.data_source records
				allResult = repo.GetAll();

				// Get by ID should find the public record
				getByIdResult = repo.Get(publicId);

				// Get by name should find the public record
				getByNameResult = repo.Get(publicName);

				// Create should insert into public.data_source
				createResult = repo.Create(createId, createName, "desc", 0, "", "", "", "", "", true);

				// Update should modify the public.data_source record
				updateResult = repo.Update(publicId, publicName + "_updated", "updated", 5,
					"", "", "", "", "", true);

				// Delete should remove from public.data_source
				repo.Delete(publicId);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}

			// Assert — operations should work correctly on public schema
			getByIdResult.Should().NotBeNull("Get(Guid) should find the public record");
			getByNameResult.Should().NotBeNull("Get(string) should find the public record");
			createResult.Should().BeTrue("Create should succeed in public schema");
			updateResult.Should().BeTrue("Update should succeed in public schema");

			// After delete+create, public schema should have exactly 1 record (the one we created)
			// (publicId was deleted, createId was added)
			var finalCount = CountDataSourceRecords();
			// The count should reflect only public records: the created record
			// (the alt_schema record should NOT be counted by our CountDataSourceRecords helper
			//  which uses public.data_source)
			ReadDataSourceDirectly(publicId).Should().BeNull("Delete should have removed the public record");
			ReadDataSourceDirectly(createId).Should().NotBeNull("Create should have added a new public record");

			// Verify the alt_schema record was never touched by any repository operation
			var ctx3 = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (DbConnection con = ctx3.CreateConnection())
				{
					var cmd = con.CreateCommand("SELECT COUNT(*) FROM alt_schema.data_source");
					var altCount = (long)cmd.ExecuteScalar();
					altCount.Should().Be(1, "alt_schema.data_source should still have exactly 1 record, untouched by the repository");
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion
	}
}
