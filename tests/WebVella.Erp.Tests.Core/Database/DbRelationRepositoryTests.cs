using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Npgsql;
using Testcontainers.PostgreSql;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;
using Xunit;

namespace WebVella.Erp.Tests.Core.Database
{
	/// <summary>
	/// Integration tests for DbRelationRepository — the Core service's entity relation
	/// persistence layer. Tests validate all CRUD operations against a real PostgreSQL
	/// 16-alpine instance managed by Testcontainers.
	///
	/// Covers:
	///  - Relation JSON document persistence (id + JSON in 'entity_relations' table)
	///  - Read by Guid ID, by string name (case-insensitive), read all
	///  - Update JSON document with cache invalidation
	///  - FK constraint creation and deletion for OneToMany relations
	///  - Relation-specific index creation (idx_r_{name}_{field} pattern)
	///  - ManyToMany join table creation (rel_{name} with composite PK)
	///  - ManyToMany bridge record insert/delete operations
	///  - Error handling: null arguments, non-existent relations, both-null M2M delete
	///  - Cache invalidation via Cache.Clear() in finally blocks
	/// </summary>
	[Collection("Database")]
	public class DbRelationRepositoryTests : IAsyncLifetime
	{
		private readonly PostgreSqlContainer _postgres;
		private string _connectionString;

		// Test entity metadata — seeded in InitializeAsync
		private Guid _accountEntityId;
		private Guid _contactEntityId;
		private Guid _accountIdFieldId;
		private Guid _contactIdFieldId;
		private Guid _contactAccountIdFieldId;

		/// <summary>
		/// Constructs the PostgreSQL test container using the postgres:16-alpine image.
		/// The container is built lazily and started in InitializeAsync.
		/// </summary>
		public DbRelationRepositoryTests()
		{
			_postgres = new PostgreSqlBuilder()
				.WithImage("postgres:16-alpine")
				.Build();
		}

		/// <summary>
		/// Starts the PostgreSQL container, initializes the ambient database context,
		/// installs required extensions, creates the entities and entity_relations
		/// metadata tables, and seeds two test entities (account and contact) for
		/// relation CRUD testing.
		/// </summary>
		public async Task InitializeAsync()
		{
			await _postgres.StartAsync();
			_connectionString = _postgres.GetConnectionString();

			// Initialize the distributed cache used by Cache.Clear() during repository operations.
			// Use an in-memory distributed cache implementation for test isolation.
			var cacheOptions = Options.Create(new MemoryDistributedCacheOptions());
			IDistributedCache testCache = new MemoryDistributedCache(cacheOptions);
			Cache.Initialize(testCache);

			// Create ambient database context for the Core service
			CoreDbContext.CreateContext(_connectionString);

			try
			{
				// Install uuid-ossp extension (required for uuid_generate_v1 default on PK columns)
				DbRepository.CreatePostgresqlExtensions();

				// Create the entity metadata table: entities (id UUID PK, json JSON)
				using (var con = CoreDbContext.Current.CreateConnection())
				{
					var cmd = con.CreateCommand(
						"CREATE TABLE IF NOT EXISTS \"entities\" (\"id\" uuid NOT NULL PRIMARY KEY, \"json\" json NOT NULL);");
					cmd.ExecuteNonQuery();
				}

				// Create the entity relations metadata table: entity_relations (id UUID PK, json JSON)
				using (var con = CoreDbContext.Current.CreateConnection())
				{
					var cmd = con.CreateCommand(
						"CREATE TABLE IF NOT EXISTS \"entity_relations\" (\"id\" uuid NOT NULL PRIMARY KEY, \"json\" json NOT NULL);");
					cmd.ExecuteNonQuery();
				}

				// Seed two test entities: account and contact
				SeedTestEntities();
			}
			catch
			{
				// Ensure context is cleaned up on setup failure
				CoreDbContext.CloseContext();
				throw;
			}

			// Close the setup context — each test method creates its own
			CoreDbContext.CloseContext();
		}

		/// <summary>
		/// Stops and disposes the PostgreSQL container, releasing Docker resources.
		/// </summary>
		public async Task DisposeAsync()
		{
			// Ensure any remaining context is closed
			try { CoreDbContext.CloseContext(); } catch { /* ignore */ }
			await _postgres.DisposeAsync();
		}

		#region ===== Helper Methods =====

		/// <summary>
		/// Seeds the account and contact test entities into the entities table
		/// and creates their physical rec_account and rec_contact tables.
		///
		/// Account: id (GuidField PK), name (TextField)
		/// Contact: id (GuidField PK), name (TextField), account_id (GuidField, nullable FK target)
		/// </summary>
		private void SeedTestEntities()
		{
			_accountIdFieldId = Guid.NewGuid();
			_contactIdFieldId = Guid.NewGuid();
			_contactAccountIdFieldId = Guid.NewGuid();

			// Create account entity with id and name fields
			var accountEntity = new DbEntity
			{
				Id = Guid.NewGuid(),
				Name = "account",
				Label = "Account",
				LabelPlural = "Accounts",
				System = false,
				Fields = new List<DbBaseField>
				{
					new DbGuidField
					{
						Id = _accountIdFieldId,
						Name = "id",
						Label = "Id",
						Required = true,
						Unique = true,
						System = true,
						GenerateNewId = true
					},
					new DbTextField
					{
						Id = Guid.NewGuid(),
						Name = "name",
						Label = "Name",
						Required = false,
						Unique = false,
						System = false
					}
				}
			};
			_accountEntityId = accountEntity.Id;

			// Create contact entity with id, name, and account_id (FK target) fields
			var contactEntity = new DbEntity
			{
				Id = Guid.NewGuid(),
				Name = "contact",
				Label = "Contact",
				LabelPlural = "Contacts",
				System = false,
				Fields = new List<DbBaseField>
				{
					new DbGuidField
					{
						Id = _contactIdFieldId,
						Name = "id",
						Label = "Id",
						Required = true,
						Unique = true,
						System = true,
						GenerateNewId = true
					},
					new DbTextField
					{
						Id = Guid.NewGuid(),
						Name = "name",
						Label = "Name",
						Required = false,
						Unique = false,
						System = false
					},
					new DbGuidField
					{
						Id = _contactAccountIdFieldId,
						Name = "account_id",
						Label = "Account Id",
						Required = false,
						Unique = false,
						System = false
					}
				}
			};
			_contactEntityId = contactEntity.Id;

			// Persist entities via DbEntityRepository (creates JSON + physical tables)
			var entityRepo = CoreDbContext.Current.EntityRepository;
			entityRepo.Create(accountEntity, null, true);
			entityRepo.Create(contactEntity, null, true);
		}

		/// <summary>
		/// Opens a fresh CoreDbContext for the current test method and returns it.
		/// Caller is responsible for closing via CoreDbContext.CloseContext() in a finally block.
		/// </summary>
		private CoreDbContext OpenTestContext()
		{
			return CoreDbContext.CreateContext(_connectionString);
		}

		/// <summary>
		/// Constructs a DbEntityRelation for a OneToMany relation from account.id → contact.account_id.
		/// Uses unique name to prevent conflicts between test runs.
		/// </summary>
		private DbEntityRelation CreateOneToManyRelationObject(string name = null)
		{
			return new DbEntityRelation
			{
				Id = Guid.NewGuid(),
				Name = name ?? $"rel_o2m_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
				Label = "Account to Contact (1:N)",
				Description = "Test OneToMany relation",
				System = false,
				RelationType = EntityRelationType.OneToMany,
				OriginEntityId = _accountEntityId,
				OriginFieldId = _accountIdFieldId,
				TargetEntityId = _contactEntityId,
				TargetFieldId = _contactAccountIdFieldId
			};
		}

		/// <summary>
		/// Constructs a DbEntityRelation for a ManyToMany relation between account.id ↔ contact.id.
		/// Uses unique name to prevent conflicts between test runs.
		/// </summary>
		private DbEntityRelation CreateManyToManyRelationObject(string name = null)
		{
			return new DbEntityRelation
			{
				Id = Guid.NewGuid(),
				Name = name ?? $"rel_m2m_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
				Label = "Account to Contact (M:N)",
				Description = "Test ManyToMany relation",
				System = false,
				RelationType = EntityRelationType.ManyToMany,
				OriginEntityId = _accountEntityId,
				OriginFieldId = _accountIdFieldId,
				TargetEntityId = _contactEntityId,
				TargetFieldId = _contactIdFieldId
			};
		}

		/// <summary>
		/// Checks whether a foreign key constraint exists on a given table via information_schema.
		/// </summary>
		private bool FkConstraintExists(string constraintName, string tableName)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			conn.Open();
			using var cmd = new NpgsqlCommand(
				@"SELECT EXISTS (
					SELECT 1 FROM information_schema.table_constraints
					WHERE constraint_type = 'FOREIGN KEY'
					AND table_schema = 'public'
					AND table_name = @tableName
					AND constraint_name = @constraintName
				)", conn);
			cmd.Parameters.AddWithValue("tableName", tableName);
			cmd.Parameters.AddWithValue("constraintName", constraintName);
			return (bool)cmd.ExecuteScalar();
		}

		/// <summary>
		/// Checks whether a PostgreSQL index exists by name via pg_indexes.
		/// </summary>
		private bool IndexExists(string indexName)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			conn.Open();
			using var cmd = new NpgsqlCommand(
				"SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = @indexName)",
				conn);
			cmd.Parameters.AddWithValue("indexName", indexName);
			return (bool)cmd.ExecuteScalar();
		}

		/// <summary>
		/// Checks whether a table exists in the public schema using information_schema.
		/// Uses a separate raw NpgsqlConnection to avoid interfering with test context state.
		/// </summary>
		private bool TableExistsRaw(string tableName)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			conn.Open();
			using var cmd = new NpgsqlCommand(
				"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name=@t)",
				conn);
			cmd.Parameters.AddWithValue("t", tableName);
			return (bool)cmd.ExecuteScalar();
		}

		/// <summary>
		/// Inserts a test record into a rec_* table with just the id column populated.
		/// Used for setting up M:M bridge record test data (FK constraints require
		/// referenced rows to exist).
		/// </summary>
		private void InsertTestRecord(string tableName, Guid id)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			conn.Open();
			using var cmd = new NpgsqlCommand(
				$"INSERT INTO \"{tableName}\" (\"id\") VALUES (@id) ON CONFLICT DO NOTHING", conn);
			cmd.Parameters.AddWithValue("id", id);
			cmd.ExecuteNonQuery();
		}

		/// <summary>
		/// Counts the number of rows in a join table (rel_*) matching optional origin/target filters.
		/// </summary>
		private int CountJoinTableRows(string tableName, Guid? originId = null, Guid? targetId = null)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			conn.Open();
			string sql = $"SELECT COUNT(*) FROM \"{tableName}\"";
			var conditions = new List<string>();
			if (originId.HasValue) conditions.Add("origin_id = @originId");
			if (targetId.HasValue) conditions.Add("target_id = @targetId");
			if (conditions.Count > 0) sql += " WHERE " + string.Join(" AND ", conditions);

			using var cmd = new NpgsqlCommand(sql, conn);
			if (originId.HasValue) cmd.Parameters.AddWithValue("originId", originId.Value);
			if (targetId.HasValue) cmd.Parameters.AddWithValue("targetId", targetId.Value);
			return Convert.ToInt32(cmd.ExecuteScalar());
		}

		/// <summary>
		/// Reads the raw JSON string for a relation from the entity_relations table by ID.
		/// Used for verifying JSON persistence format (TypeNameHandling.Auto).
		/// </summary>
		private string ReadRawRelationJson(Guid relationId)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			conn.Open();
			using var cmd = new NpgsqlCommand(
				"SELECT json FROM entity_relations WHERE id = @id", conn);
			cmd.Parameters.AddWithValue("id", relationId);
			var result = cmd.ExecuteScalar();
			return result?.ToString();
		}

		/// <summary>
		/// Deletes a relation and suppresses any exceptions during cleanup.
		/// Used in test teardown blocks to ensure the database doesn't accumulate stale relations.
		/// </summary>
		private void CleanupRelation(Guid relationId)
		{
			try
			{
				CoreDbContext.Current.RelationRepository.Delete(relationId);
			}
			catch
			{
				// Ignore cleanup errors — relation may not exist if test failed during creation
			}
		}

		#endregion

		#region ===== Phase 2: Relation Document Persistence Tests =====

		/// <summary>
		/// Verifies that DbRelationRepository.Create() persists the relation as a JSON document
		/// in the entity_relations table using TypeNameHandling.Auto serialization settings.
		/// </summary>
		[Fact]
		public void Create_ShouldPersistRelationAsJsonDocument()
		{
			OpenTestContext();
			try
			{
				var relation = CreateOneToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;

				// Act
				bool result = repo.Create(relation);

				// Assert — verify creation succeeded
				result.Should().BeTrue();

				// Assert — verify raw JSON in database
				string rawJson = ReadRawRelationJson(relation.Id);
				rawJson.Should().NotBeNullOrEmpty("relation JSON should be persisted in entity_relations table");

				// Verify JSON can be deserialized with TypeNameHandling.Auto
				var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
				var deserialized = JsonConvert.DeserializeObject<DbEntityRelation>(rawJson, settings);
				deserialized.Should().NotBeNull();
				deserialized.Id.Should().Be(relation.Id);
				deserialized.Name.Should().Be(relation.Name);
				deserialized.Label.Should().Be(relation.Label);
				deserialized.Description.Should().Be(relation.Description);
				deserialized.System.Should().Be(relation.System);
				deserialized.RelationType.Should().Be(EntityRelationType.OneToMany);
				deserialized.OriginEntityId.Should().Be(relation.OriginEntityId);
				deserialized.OriginFieldId.Should().Be(relation.OriginFieldId);
				deserialized.TargetEntityId.Should().Be(relation.TargetEntityId);
				deserialized.TargetFieldId.Should().Be(relation.TargetFieldId);

				// Cleanup
				CleanupRelation(relation.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that Read(Guid) returns the correct DbEntityRelation by ID.
		/// Source: Read(Guid) delegates to Read() and filters by id.
		/// </summary>
		[Fact]
		public void Read_ById_ShouldReturnCorrectRelation()
		{
			OpenTestContext();
			try
			{
				var relation = CreateManyToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;
				repo.Create(relation);

				// Act
				var result = repo.Read(relation.Id);

				// Assert
				result.Should().NotBeNull();
				result.Id.Should().Be(relation.Id);
				result.Name.Should().Be(relation.Name);
				result.Label.Should().Be(relation.Label);
				result.RelationType.Should().Be(EntityRelationType.ManyToMany);
				result.OriginEntityId.Should().Be(relation.OriginEntityId);
				result.TargetEntityId.Should().Be(relation.TargetEntityId);

				// Cleanup
				CleanupRelation(relation.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that Read(string) returns the correct relation by name (case-insensitive).
		/// Source: Read(string) uses .ToLowerInvariant() comparison.
		/// </summary>
		[Fact]
		public void Read_ByName_ShouldReturnCorrectRelation()
		{
			OpenTestContext();
			try
			{
				var relation = CreateManyToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;
				repo.Create(relation);

				// Act — use mixed-case name to verify case-insensitive comparison
				var result = repo.Read(relation.Name.ToUpperInvariant());

				// Assert
				result.Should().NotBeNull();
				result.Id.Should().Be(relation.Id);
				result.Name.Should().Be(relation.Name);

				// Cleanup
				CleanupRelation(relation.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that Read() returns all relations from the entity_relations table.
		/// Source: SQL SELECT json FROM entity_relations;
		/// </summary>
		[Fact]
		public void Read_All_ShouldReturnAllRelations()
		{
			OpenTestContext();
			try
			{
				var repo = CoreDbContext.Current.RelationRepository;

				// Record baseline count
				int baselineCount = repo.Read().Count;

				// Create two new relations
				var rel1 = CreateManyToManyRelationObject();
				var rel2 = CreateManyToManyRelationObject();
				repo.Create(rel1);
				repo.Create(rel2);

				// Act
				var allRelations = repo.Read();

				// Assert — should contain at least 2 more than baseline
				allRelations.Should().NotBeNull();
				allRelations.Count.Should().BeGreaterThanOrEqualTo(baselineCount + 2);
				allRelations.Should().Contain(r => r.Id == rel1.Id);
				allRelations.Should().Contain(r => r.Id == rel2.Id);

				// Cleanup
				CleanupRelation(rel1.Id);
				CleanupRelation(rel2.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that Update() modifies the relation JSON document in the database.
		/// Source: UPDATE entity_relations SET json=@json WHERE id=@id;
		/// </summary>
		[Fact]
		public void Update_ShouldModifyRelationJsonDocument()
		{
			OpenTestContext();
			try
			{
				var relation = CreateManyToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;
				repo.Create(relation);

				// Modify properties
				relation.Label = "Updated Label";
				relation.Description = "Updated Description";

				// Act
				bool updateResult = repo.Update(relation);

				// Assert
				updateResult.Should().BeTrue();
				var readBack = repo.Read(relation.Id);
				readBack.Should().NotBeNull();
				readBack.Label.Should().Be("Updated Label");
				readBack.Description.Should().Be("Updated Description");
				// Ensure unchanged properties are preserved
				readBack.Name.Should().Be(relation.Name);
				readBack.RelationType.Should().Be(relation.RelationType);

				// Cleanup
				CleanupRelation(relation.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region ===== Phase 3: FK Constraint Tests (1:N) =====

		/// <summary>
		/// Verifies that creating a OneToMany relation adds a foreign key constraint
		/// on the target table (rec_contact) referencing the origin table (rec_account).
		/// Source: DbRelationRepository.Create() calls DbRepository.CreateRelation() for non-M2M types.
		/// </summary>
		[Fact]
		public void Create_OneToMany_ShouldCreateFkConstraint()
		{
			OpenTestContext();
			try
			{
				var relation = CreateOneToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;

				// Act
				repo.Create(relation);

				// Assert — FK constraint should exist on rec_contact
				bool fkExists = FkConstraintExists(relation.Name, "rec_contact");
				fkExists.Should().BeTrue(
					$"FK constraint '{relation.Name}' should exist on 'rec_contact' after OneToMany relation creation");

				// Cleanup
				CleanupRelation(relation.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that creating a OneToMany relation creates an index on the non-id target field.
		/// Source: Create calls DbRepository.CreateIndex() with pattern idx_r_{rel.Name}_{field.Name}
		/// for fields where Name != "id".
		/// </summary>
		[Fact]
		public void Create_OneToMany_ShouldCreateRelationIndex()
		{
			OpenTestContext();
			try
			{
				var relation = CreateOneToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;

				// Act
				repo.Create(relation);

				// Assert — index on target field (account_id) which is not "id"
				// Expected index name: idx_r_{relationName}_account_id
				string expectedIndexName = $"idx_r_{relation.Name}_account_id";
				bool indexExists = IndexExists(expectedIndexName);
				indexExists.Should().BeTrue(
					$"Index '{expectedIndexName}' should be created on rec_contact.account_id for OneToMany relation");

				// Origin field is "id" so no index should be created for it
				string originIndexName = $"idx_r_{relation.Name}_id";
				bool originIndexExists = IndexExists(originIndexName);
				originIndexExists.Should().BeFalse(
					"Index should NOT be created for origin field 'id' (PK already indexed)");

				// Cleanup
				CleanupRelation(relation.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that deleting a OneToMany relation removes the FK constraint and index.
		/// Source: Delete calls DbRepository.DropIndex() then DbRepository.DeleteRelation().
		/// </summary>
		[Fact]
		public void Delete_OneToMany_ShouldRemoveFkAndIndex()
		{
			OpenTestContext();
			try
			{
				var relation = CreateOneToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;

				// Setup — create the relation
				repo.Create(relation);
				string expectedIndexName = $"idx_r_{relation.Name}_account_id";

				// Verify preconditions
				FkConstraintExists(relation.Name, "rec_contact").Should().BeTrue("FK should exist before delete");
				IndexExists(expectedIndexName).Should().BeTrue("Index should exist before delete");

				// Act
				bool deleteResult = repo.Delete(relation.Id);

				// Assert
				deleteResult.Should().BeTrue();
				FkConstraintExists(relation.Name, "rec_contact").Should().BeFalse(
					"FK constraint should be removed after OneToMany relation deletion");
				IndexExists(expectedIndexName).Should().BeFalse(
					"Index should be removed after OneToMany relation deletion");

				// Verify relation JSON is also removed from entity_relations table
				var readResult = repo.Read(relation.Id);
				readResult.Should().BeNull("Relation should not exist after deletion");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region ===== Phase 4: Many-to-Many Join Table Tests =====

		/// <summary>
		/// Verifies that creating a ManyToMany relation creates a join table (rel_{name})
		/// with origin_id and target_id columns forming a composite primary key,
		/// plus FK constraints and indexes on both columns.
		/// Source: Create calls DbRepository.CreateNtoNRelation() which creates rel_{name} with composite PK.
		/// </summary>
		[Fact]
		public void Create_ManyToMany_ShouldCreateJoinTable()
		{
			OpenTestContext();
			try
			{
				var relation = CreateManyToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;
				string joinTableName = $"rel_{relation.Name}";

				// Act
				repo.Create(relation);

				// Assert — join table should exist
				TableExistsRaw(joinTableName).Should().BeTrue(
					$"Join table '{joinTableName}' should exist after ManyToMany relation creation");

				// Assert — verify composite primary key columns (origin_id, target_id)
				using var conn = new NpgsqlConnection(_connectionString);
				conn.Open();
				using var cmd = new NpgsqlCommand(
					@"SELECT column_name FROM information_schema.columns
					  WHERE table_schema = 'public' AND table_name = @tableName
					  ORDER BY ordinal_position", conn);
				cmd.Parameters.AddWithValue("tableName", joinTableName);
				var columns = new List<string>();
				using (var reader = cmd.ExecuteReader())
				{
					while (reader.Read())
						columns.Add(reader.GetString(0));
				}
				columns.Should().Contain("origin_id", "Join table should have origin_id column");
				columns.Should().Contain("target_id", "Join table should have target_id column");

				// Assert — verify indexes on the join table
				string originIndexName = $"idx_{relation.Name}_origin_id";
				string targetIndexName = $"idx_{relation.Name}_target_id";
				IndexExists(originIndexName).Should().BeTrue(
					$"Index '{originIndexName}' should exist on join table");
				IndexExists(targetIndexName).Should().BeTrue(
					$"Index '{targetIndexName}' should exist on join table");

				// Cleanup
				CleanupRelation(relation.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that deleting a ManyToMany relation drops the join table (rel_{name}).
		/// Source: Delete calls DbRepository.DeleteNtoNRelation().
		/// </summary>
		[Fact]
		public void Delete_ManyToMany_ShouldDropJoinTable()
		{
			OpenTestContext();
			try
			{
				var relation = CreateManyToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;
				string joinTableName = $"rel_{relation.Name}";

				// Setup — create the M:N relation and verify the join table exists
				repo.Create(relation);
				TableExistsRaw(joinTableName).Should().BeTrue("Join table should exist before delete");

				// Act
				bool deleteResult = repo.Delete(relation.Id);

				// Assert
				deleteResult.Should().BeTrue();
				TableExistsRaw(joinTableName).Should().BeFalse(
					$"Join table '{joinTableName}' should be dropped after ManyToMany relation deletion");

				// Verify relation is removed from entity_relations table
				repo.Read(relation.Id).Should().BeNull();
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region ===== Phase 5: M2M Bridge Record Operations =====

		/// <summary>
		/// Verifies that CreateManyToManyRecord inserts a bridge row into the rel_{name} table.
		/// Source: SQL INSERT INTO {tableName} VALUES (@origin_id, @target_id)
		/// </summary>
		[Fact]
		public void CreateManyToManyRecord_ShouldInsertBridgeRow()
		{
			OpenTestContext();
			try
			{
				var relation = CreateManyToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;
				repo.Create(relation);
				string joinTableName = $"rel_{relation.Name}";

				// Insert referenced records into origin and target tables
				var originId = Guid.NewGuid();
				var targetId = Guid.NewGuid();
				InsertTestRecord("rec_account", originId);
				InsertTestRecord("rec_contact", targetId);

				// Act
				repo.CreateManyToManyRecord(relation.Id, originId, targetId);

				// Assert — verify bridge row exists in join table
				int count = CountJoinTableRows(joinTableName, originId, targetId);
				count.Should().Be(1, "Exactly one bridge row should exist with the specified origin and target IDs");

				// Cleanup
				CleanupRelation(relation.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that DeleteManyToManyRecord with both origin and target IDs removes
		/// only the specific bridge row, leaving other rows intact.
		/// Source: DELETE FROM {tableName} WHERE origin_id=@origin_id AND target_id=@target_id
		/// </summary>
		[Fact]
		public void DeleteManyToManyRecord_ByBothIds_ShouldRemoveSpecificRow()
		{
			OpenTestContext();
			try
			{
				var relation = CreateManyToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;
				repo.Create(relation);
				string joinTableName = $"rel_{relation.Name}";

				// Insert 3 origin records and 2 target records
				var origin1 = Guid.NewGuid();
				var origin2 = Guid.NewGuid();
				var target1 = Guid.NewGuid();
				var target2 = Guid.NewGuid();
				InsertTestRecord("rec_account", origin1);
				InsertTestRecord("rec_account", origin2);
				InsertTestRecord("rec_contact", target1);
				InsertTestRecord("rec_contact", target2);

				// Create 3 bridge rows
				repo.CreateManyToManyRecord(relation.Id, origin1, target1);
				repo.CreateManyToManyRecord(relation.Id, origin1, target2);
				repo.CreateManyToManyRecord(relation.Id, origin2, target1);

				// Verify baseline: 3 rows
				CountJoinTableRows(joinTableName).Should().Be(3);

				// Act — delete specific row (origin1, target1)
				repo.DeleteManyToManyRecord(relation.Name, origin1, target1);

				// Assert — only 2 rows remain
				CountJoinTableRows(joinTableName).Should().Be(2);
				CountJoinTableRows(joinTableName, origin1, target1).Should().Be(0,
					"Specific bridge row (origin1, target1) should be removed");
				CountJoinTableRows(joinTableName, origin1, target2).Should().Be(1,
					"Other bridge rows should remain intact");
				CountJoinTableRows(joinTableName, origin2, target1).Should().Be(1,
					"Other bridge rows should remain intact");

				// Cleanup
				CleanupRelation(relation.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that DeleteManyToManyRecord with only originId (targetId=null) removes
		/// all bridge rows for that origin.
		/// Source: DELETE FROM {tableName} WHERE origin_id=@origin_id
		/// </summary>
		[Fact]
		public void DeleteManyToManyRecord_ByOriginOnly_ShouldRemoveAllForOrigin()
		{
			OpenTestContext();
			try
			{
				var relation = CreateManyToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;
				repo.Create(relation);
				string joinTableName = $"rel_{relation.Name}";

				// Insert records
				var origin1 = Guid.NewGuid();
				var origin2 = Guid.NewGuid();
				var target1 = Guid.NewGuid();
				var target2 = Guid.NewGuid();
				InsertTestRecord("rec_account", origin1);
				InsertTestRecord("rec_account", origin2);
				InsertTestRecord("rec_contact", target1);
				InsertTestRecord("rec_contact", target2);

				// Create bridge rows: origin1→target1, origin1→target2, origin2→target1
				repo.CreateManyToManyRecord(relation.Id, origin1, target1);
				repo.CreateManyToManyRecord(relation.Id, origin1, target2);
				repo.CreateManyToManyRecord(relation.Id, origin2, target1);

				// Act — delete all for origin1 (targetId = null)
				repo.DeleteManyToManyRecord(relation.Name, originId: origin1, targetId: null);

				// Assert — only origin2→target1 should remain
				CountJoinTableRows(joinTableName).Should().Be(1);
				CountJoinTableRows(joinTableName, origin1).Should().Be(0,
					"All bridge rows for origin1 should be removed");
				CountJoinTableRows(joinTableName, origin2).Should().Be(1,
					"Bridge rows for origin2 should remain intact");

				// Cleanup
				CleanupRelation(relation.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that DeleteManyToManyRecord with only targetId (originId=null) removes
		/// all bridge rows for that target.
		/// Source: DELETE FROM {tableName} WHERE target_id=@target_id
		/// </summary>
		[Fact]
		public void DeleteManyToManyRecord_ByTargetOnly_ShouldRemoveAllForTarget()
		{
			OpenTestContext();
			try
			{
				var relation = CreateManyToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;
				repo.Create(relation);
				string joinTableName = $"rel_{relation.Name}";

				// Insert records
				var origin1 = Guid.NewGuid();
				var origin2 = Guid.NewGuid();
				var target1 = Guid.NewGuid();
				var target2 = Guid.NewGuid();
				InsertTestRecord("rec_account", origin1);
				InsertTestRecord("rec_account", origin2);
				InsertTestRecord("rec_contact", target1);
				InsertTestRecord("rec_contact", target2);

				// Create bridge rows: origin1→target1, origin2→target1, origin1→target2
				repo.CreateManyToManyRecord(relation.Id, origin1, target1);
				repo.CreateManyToManyRecord(relation.Id, origin2, target1);
				repo.CreateManyToManyRecord(relation.Id, origin1, target2);

				// Act — delete all for target1 (originId = null)
				repo.DeleteManyToManyRecord(relation.Name, originId: null, targetId: target1);

				// Assert — only origin1→target2 should remain
				CountJoinTableRows(joinTableName).Should().Be(1);
				CountJoinTableRows(joinTableName, targetId: target1).Should().Be(0,
					"All bridge rows for target1 should be removed");
				CountJoinTableRows(joinTableName, targetId: target2).Should().Be(1,
					"Bridge rows for target2 should remain intact");

				// Cleanup
				CleanupRelation(relation.Id);
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that DeleteManyToManyRecord throws when both originId and targetId are null.
		/// Source: Exact error message "Both origin id and target id cannot be null when delete many to many relation!"
		/// </summary>
		[Fact]
		public void DeleteManyToManyRecord_BothNull_ShouldThrow()
		{
			OpenTestContext();
			try
			{
				var repo = CoreDbContext.Current.RelationRepository;

				// Act & Assert — both null should throw Exception with exact message
				Action act = () => repo.DeleteManyToManyRecord("any_relation_name", originId: null, targetId: null);
				act.Should().Throw<Exception>()
					.WithMessage("Both origin id and target id cannot be null when delete many to many relation!");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region ===== Phase 6: Error Handling Tests =====

		/// <summary>
		/// Verifies that Create throws ArgumentNullException when passed a null relation.
		/// Source: Line 42-43 null check — throw new ArgumentNullException("relation")
		/// </summary>
		[Fact]
		public void Create_WithNullRelation_ShouldThrowArgumentNullException()
		{
			OpenTestContext();
			try
			{
				var repo = CoreDbContext.Current.RelationRepository;

				// Act & Assert
				Action act = () => repo.Create(null);
				act.Should().Throw<ArgumentNullException>()
					.And.ParamName.Should().Be("relation");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that Delete throws StorageException when the relation ID does not exist.
		/// Source: Delete method checks Read result and throws StorageException with exact message.
		/// </summary>
		[Fact]
		public void Delete_NonExistentRelation_ShouldThrowStorageException()
		{
			OpenTestContext();
			try
			{
				var repo = CoreDbContext.Current.RelationRepository;
				var nonExistentId = Guid.NewGuid();

				// Act & Assert
				Action act = () => repo.Delete(nonExistentId);
				act.Should().Throw<StorageException>()
					.WithMessage("There is no record with specified relation id.");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Verifies that Create, Update, and Delete operations invalidate the entity/relation
		/// cache by calling Cache.Clear() in their finally blocks.
		/// This test seeds cache entries, performs a mutation, then verifies the cache is empty.
		/// </summary>
		[Fact]
		public void Create_ShouldInvalidateCache()
		{
			OpenTestContext();
			try
			{
				// Seed cache with dummy relation data
				var dummyRelations = new List<EntityRelation>
				{
					new EntityRelation { Id = Guid.NewGuid(), Name = "dummy_cache_test" }
				};
				Cache.AddRelations(dummyRelations);
				Cache.GetRelations().Should().NotBeNull("Cache should have relations before the test");

				// Act — Create a relation (which calls Cache.Clear() in its finally block)
				var relation = CreateManyToManyRelationObject();
				var repo = CoreDbContext.Current.RelationRepository;
				repo.Create(relation);

				// Assert — cache should be cleared after Create
				Cache.GetRelations().Should().BeNull(
					"Cache.Clear() should be called in Create's finally block, removing all cached data");

				// Verify same behavior for Update: re-seed cache, then update
				Cache.AddRelations(dummyRelations);
				Cache.GetRelations().Should().NotBeNull("Cache should have relations before Update");
				relation.Label = "Updated for cache test";
				repo.Update(relation);
				Cache.GetRelations().Should().BeNull(
					"Cache.Clear() should be called in Update's finally block");

				// Verify same behavior for Delete: re-seed cache, then delete
				Cache.AddRelations(dummyRelations);
				Cache.GetRelations().Should().NotBeNull("Cache should have relations before Delete");
				repo.Delete(relation.Id);
				Cache.GetRelations().Should().BeNull(
					"Cache.Clear() should be called in Delete's finally block");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion
	}
}
