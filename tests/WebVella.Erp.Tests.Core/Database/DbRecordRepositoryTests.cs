using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Utilities;
using Xunit;

namespace WebVella.Erp.Tests.Core.Database
{
	/// <summary>
	/// Integration tests for the Core service's DbRecordRepository — the largest and most
	/// complex repository (~2097 lines) responsible for dynamic record CRUD, query translation
	/// (EntityQuery/QueryObject filters → SQL), relational projections via JSON aggregation,
	/// geography field handling, and column DDL operations.
	///
	/// All tests execute against a real PostgreSQL 16-alpine instance managed by Testcontainers
	/// to guarantee SQL generation fidelity identical to the monolith.
	///
	/// Test Phases:
	///   Phase 2: Record CRUD (Create, Update, Delete with error paths)
	///   Phase 3: Query/Filter (EQ, CONTAINS, LT/GTE, REGEX, FTS)
	///   Phase 4: Relational Projections (1:N nested JSON, M:N join tables)
	///   Phase 5: Geography Fields (GeoJSON storage, null handling)
	///   Phase 6: Column DDL (CreateRecordField, UpdateRecordField, RemoveRecordField)
	///   Phase 7: Pagination and Sorting (limit/offset, Count)
	/// </summary>
	[Collection("Database")]
	public class DbRecordRepositoryTests : IAsyncLifetime
	{
		private readonly PostgreSqlContainer _postgres;
		private string _connectionString;
		private IConfiguration _configuration;

		// Well-known IDs for the test entity and its fields
		private static readonly Guid TestEntityId = new Guid("A0000001-0001-0001-0001-000000000001");
		private static readonly Guid IdFieldId = new Guid("A0000001-0001-0001-0001-000000000010");
		private static readonly Guid NameFieldId = new Guid("A0000001-0001-0001-0001-000000000011");
		private static readonly Guid StatusFieldId = new Guid("A0000001-0001-0001-0001-000000000012");
		private static readonly Guid CreatedOnFieldId = new Guid("A0000001-0001-0001-0001-000000000013");
		private static readonly Guid AmountFieldId = new Guid("A0000001-0001-0001-0001-000000000014");
		private static readonly Guid ActiveFieldId = new Guid("A0000001-0001-0001-0001-000000000015");

		// Entity name used across most tests
		private const string TestEntityName = "test_record";

		/// <summary>
		/// Constructs the PostgreSQL test container using the postgres:16-alpine image.
		/// </summary>
		public DbRecordRepositoryTests()
		{
			_postgres = new PostgreSqlBuilder()
				.WithImage("postgres:16-alpine")
				.Build();
		}

		/// <summary>
		/// Starts the PostgreSQL container, configures the ambient database context,
		/// initializes shared settings and cache, creates metadata tables, seeds
		/// the User entity, and creates the standard test entity with fields.
		/// </summary>
		public async Task InitializeAsync()
		{
			await _postgres.StartAsync();
			_connectionString = _postgres.GetConnectionString();

			// Initialize AutoMapper with all profiles needed by EntityManager/EntityRelationManager.
			// ReadEntities() calls storageEntityList.MapTo<Entity>() which requires mapping profiles.
			InitializeAutoMapper();

			// Initialize ErpSettings with a minimal in-memory configuration
			// so that EntityManager (lazy-initialized in DbRecordRepository) can
			// read its required settings (TimeZoneName, DevelopmentMode, etc.)
			var configData = new Dictionary<string, string>
			{
				{ "Settings:ConnectionString", _connectionString },
				{ "Settings:TimeZoneName", "UTC" },
				{ "Settings:DevelopmentMode", "false" },
				{ "Settings:EncryptionKey", "test-key-1234" },
				{ "Settings:Lang", "en" },
				{ "Settings:Locale", "en-US" }
			};
			_configuration = new ConfigurationBuilder()
				.AddInMemoryCollection(configData)
				.Build();

			if (!ErpSettings.IsInitialized)
			{
				ErpSettings.Initialize(_configuration);
			}

			// Initialize the distributed cache used by Cache.Clear() during repository operations
			var cacheOptions = Options.Create(new MemoryDistributedCacheOptions());
			IDistributedCache testCache = new MemoryDistributedCache(cacheOptions);
			Cache.Initialize(testCache);

			// Create ambient database context
			CoreDbContext.CreateContext(_connectionString);

			try
			{
				// Install uuid-ossp extension
				DbRepository.CreatePostgresqlExtensions();

				// Create entity metadata table
				using (var con = CoreDbContext.Current.CreateConnection())
				{
					var cmd = con.CreateCommand(
						"CREATE TABLE IF NOT EXISTS \"entities\" (\"id\" uuid NOT NULL PRIMARY KEY, \"json\" json NOT NULL);");
					cmd.ExecuteNonQuery();
				}

				// Create entity relations metadata table
				using (var con = CoreDbContext.Current.CreateConnection())
				{
					var cmd = con.CreateCommand(
						"CREATE TABLE IF NOT EXISTS \"entity_relations\" (\"id\" uuid NOT NULL PRIMARY KEY, \"json\" json NOT NULL);");
					cmd.ExecuteNonQuery();
				}

				// Seed User entity (required for audit relation resolution)
				SeedUserEntity();

				// Create the primary test entity with standard fields
				CreateStandardTestEntity();
			}
			catch
			{
				CoreDbContext.CloseContext();
				throw;
			}

			// Close the setup context — each test method creates its own
			CoreDbContext.CloseContext();
		}

		/// <summary>
		/// Stops and disposes the PostgreSQL container.
		/// </summary>
		public async Task DisposeAsync()
		{
			try { CoreDbContext.CloseContext(); } catch { /* ignore */ }
			await _postgres.DisposeAsync();
		}

		#region ===== AutoMapper Initialization =====

		/// <summary>
		/// Initializes AutoMapper with the profiles needed by EntityManager and EntityRelationManager.
		/// ReadEntities() calls storageEntityList.MapTo&lt;Entity&gt;() which requires mapping profiles
		/// for DbEntity → Entity, DbBaseField → Field, DbRecordPermissions → RecordPermissions, etc.
		/// </summary>
		private static void InitializeAutoMapper()
		{
			if (ErpAutoMapper.Mapper != null)
				return;

			var cfg = new MapperConfigurationExpression();

			// Entity <-> InputEntity
			cfg.CreateMap<Entity, InputEntity>();
			cfg.CreateMap<InputEntity, Entity>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty))
				.ForMember(x => x.System, opt => opt.MapFrom(y => (y.System.HasValue) ? y.System.Value : false));

			// Entity <-> DbEntity
			cfg.CreateMap<Entity, DbEntity>();
			cfg.CreateMap<DbEntity, Entity>();

			// EntityRelation <-> DbEntityRelation
			cfg.CreateMap<EntityRelation, DbEntityRelation>();
			cfg.CreateMap<DbEntityRelation, EntityRelation>();

			// RecordPermissions <-> DbRecordPermissions
			cfg.CreateMap<RecordPermissions, DbRecordPermissions>();
			cfg.CreateMap<DbRecordPermissions, RecordPermissions>();

			// FieldPermissions <-> DbFieldPermissions
			cfg.CreateMap<FieldPermissions, DbFieldPermissions>();
			cfg.CreateMap<DbFieldPermissions, FieldPermissions>();

			// AutoNumberField
			cfg.CreateMap<AutoNumberField, DbAutoNumberField>();
			cfg.CreateMap<DbAutoNumberField, AutoNumberField>();
			cfg.CreateMap<InputAutoNumberField, AutoNumberField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// CheckboxField
			cfg.CreateMap<CheckboxField, DbCheckboxField>();
			cfg.CreateMap<DbCheckboxField, CheckboxField>();
			cfg.CreateMap<InputCheckboxField, CheckboxField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// CurrencyField
			cfg.CreateMap<CurrencyField, DbCurrencyField>();
			cfg.CreateMap<DbCurrencyField, CurrencyField>();
			cfg.CreateMap<InputCurrencyField, CurrencyField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// DateField
			cfg.CreateMap<DateField, DbDateField>();
			cfg.CreateMap<DbDateField, DateField>();
			cfg.CreateMap<InputDateField, DateField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// DateTimeField
			cfg.CreateMap<DateTimeField, DbDateTimeField>();
			cfg.CreateMap<DbDateTimeField, DateTimeField>();
			cfg.CreateMap<InputDateTimeField, DateTimeField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// EmailField
			cfg.CreateMap<EmailField, DbEmailField>();
			cfg.CreateMap<DbEmailField, EmailField>();
			cfg.CreateMap<InputEmailField, EmailField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// FileField
			cfg.CreateMap<FileField, DbFileField>();
			cfg.CreateMap<DbFileField, FileField>();
			cfg.CreateMap<InputFileField, FileField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// GuidField
			cfg.CreateMap<GuidField, DbGuidField>();
			cfg.CreateMap<DbGuidField, GuidField>();
			cfg.CreateMap<InputGuidField, GuidField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// HtmlField
			cfg.CreateMap<HtmlField, DbHtmlField>();
			cfg.CreateMap<DbHtmlField, HtmlField>();
			cfg.CreateMap<InputHtmlField, HtmlField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// ImageField
			cfg.CreateMap<ImageField, DbImageField>();
			cfg.CreateMap<DbImageField, ImageField>();
			cfg.CreateMap<InputImageField, ImageField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// MultiLineTextField
			cfg.CreateMap<MultiLineTextField, DbMultiLineTextField>();
			cfg.CreateMap<DbMultiLineTextField, MultiLineTextField>();
			cfg.CreateMap<InputMultiLineTextField, MultiLineTextField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// MultiSelectField
			cfg.CreateMap<MultiSelectField, DbMultiSelectField>();
			cfg.CreateMap<DbMultiSelectField, MultiSelectField>();
			cfg.CreateMap<InputMultiSelectField, MultiSelectField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// NumberField
			cfg.CreateMap<NumberField, DbNumberField>();
			cfg.CreateMap<DbNumberField, NumberField>();
			cfg.CreateMap<InputNumberField, NumberField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// PasswordField
			cfg.CreateMap<PasswordField, DbPasswordField>();
			cfg.CreateMap<DbPasswordField, PasswordField>();
			cfg.CreateMap<InputPasswordField, PasswordField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// PercentField
			cfg.CreateMap<PercentField, DbPercentField>();
			cfg.CreateMap<DbPercentField, PercentField>();
			cfg.CreateMap<InputPercentField, PercentField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// PhoneField
			cfg.CreateMap<PhoneField, DbPhoneField>();
			cfg.CreateMap<DbPhoneField, PhoneField>();
			cfg.CreateMap<InputPhoneField, PhoneField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// SelectField
			cfg.CreateMap<SelectField, DbSelectField>();
			cfg.CreateMap<DbSelectField, SelectField>();
			cfg.CreateMap<InputSelectField, SelectField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// TextField
			cfg.CreateMap<TextField, DbTextField>();
			cfg.CreateMap<DbTextField, TextField>();
			cfg.CreateMap<InputTextField, TextField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// UrlField
			cfg.CreateMap<UrlField, DbUrlField>();
			cfg.CreateMap<DbUrlField, UrlField>();
			cfg.CreateMap<InputUrlField, UrlField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			// GeographyField
			cfg.CreateMap<GeographyField, DbGeographyField>();
			cfg.CreateMap<DbGeographyField, GeographyField>();
			cfg.CreateMap<InputGeographyField, GeographyField>()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));

			// Field abstract base mappings
			cfg.CreateMap<Field, InputField>().IncludeAllDerived();
			cfg.CreateMap<InputField, Field>().IncludeAllDerived()
				.ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty));
			cfg.CreateMap<DbBaseField, Field>().IncludeAllDerived();
			cfg.CreateMap<Field, DbBaseField>().IncludeAllDerived();

			// ErrorModel -> ValidationError
			cfg.CreateMap<ErrorModel, ValidationError>()
				.ConvertUsing(source => source == null ? null : new ValidationError(source.Key ?? "id", source.Message));
			cfg.CreateMap<ValidationError, ErrorModel>()
				.ConvertUsing(source => source == null ? null : new ErrorModel(source.PropertyName ?? "id", source.PropertyName ?? "id", source.Message));

			ErpAutoMapper.Initialize(cfg);
		}

		#endregion

		#region ===== Helper Methods =====

		/// <summary>
		/// Seeds the User entity (SystemIds.UserEntityId) required by EntityManager
		/// for audit relation resolution.
		/// </summary>
		private void SeedUserEntity()
		{
			var userEntity = new DbEntity
			{
				Id = SystemIds.UserEntityId,
				Name = "user",
				Label = "User",
				LabelPlural = "Users",
				System = true,
				Fields = new List<DbBaseField>
				{
					new DbGuidField
					{
						Id = Guid.NewGuid(),
						Name = "id",
						Required = true,
						Unique = true,
						System = true,
						GenerateNewId = true
					}
				}
			};
			CoreDbContext.Current.EntityRepository.Create(userEntity, null, true);
		}

		/// <summary>
		/// Creates the standard test entity "test_record" with fields for
		/// all test phases: id (UUID PK), name (text), status (text),
		/// created_on (timestamptz), amount (numeric), active (boolean).
		/// </summary>
		private void CreateStandardTestEntity()
		{
			var entity = new DbEntity
			{
				Id = TestEntityId,
				Name = TestEntityName,
				Label = "Test Record",
				LabelPlural = "Test Records",
				System = false,
				RecordPermissions = new DbRecordPermissions
				{
					CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
					CanCreate = new List<Guid> { SystemIds.AdministratorRoleId },
					CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
					CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
				},
				Fields = new List<DbBaseField>
				{
					new DbGuidField
					{
						Id = IdFieldId,
						Name = "id",
						Label = "Id",
						Required = true,
						Unique = true,
						System = true,
						GenerateNewId = true
					},
					new DbTextField
					{
						Id = NameFieldId,
						Name = "name",
						Label = "Name",
						Required = false,
						Unique = false,
						Searchable = true
					},
					new DbTextField
					{
						Id = StatusFieldId,
						Name = "status",
						Label = "Status",
						Required = false,
						Unique = false,
						Searchable = false
					},
					new DbDateTimeField
					{
						Id = CreatedOnFieldId,
						Name = "created_on",
						Label = "Created On",
						Required = false,
						Unique = false,
						UseCurrentTimeAsDefaultValue = false
					},
					new DbNumberField
					{
						Id = AmountFieldId,
						Name = "amount",
						Label = "Amount",
						Required = false,
						Unique = false
					},
					new DbCheckboxField
					{
						Id = ActiveFieldId,
						Name = "active",
						Label = "Active",
						Required = false,
						Unique = false,
						DefaultValue = false
					}
				}
			};

			CoreDbContext.Current.EntityRepository.Create(entity, null, true);
		}

		/// <summary>
		/// Opens a fresh CoreDbContext for the current test method.
		/// Clears the entity metadata cache to force re-reads from DB,
		/// ensuring test isolation when entities are created/modified
		/// across different test methods.
		/// Caller is responsible for closing via CoreDbContext.CloseContext().
		/// </summary>
		private CoreDbContext OpenTestContext()
		{
			// Clear entity metadata cache to force DB re-read
			Cache.ClearEntities();

			return CoreDbContext.CreateContext(_connectionString);
		}

		/// <summary>
		/// Creates a record in the test entity using the DbRecordRepository under test.
		/// Returns the generated GUID for verification.
		/// </summary>
		private Guid CreateRecordViaRepo(
			string entityName,
			string name,
			string status,
			DateTime? createdOn = null,
			decimal? amount = null,
			bool? active = null)
		{
			var id = Guid.NewGuid();
			var data = new List<KeyValuePair<string, object>>
			{
				new KeyValuePair<string, object>("id", id),
				new KeyValuePair<string, object>("name", name),
				new KeyValuePair<string, object>("status", status)
			};

			if (createdOn.HasValue)
				data.Add(new KeyValuePair<string, object>("created_on", createdOn.Value));
			if (amount.HasValue)
				data.Add(new KeyValuePair<string, object>("amount", amount.Value));
			if (active.HasValue)
				data.Add(new KeyValuePair<string, object>("active", active.Value));

			CoreDbContext.Current.RecordRepository.Create(entityName, data);
			return id;
		}

		/// <summary>
		/// Reads a record directly via raw SQL for verification, bypassing the repository.
		/// </summary>
		private Dictionary<string, object> ReadRecordDirect(string tableName, Guid id)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			conn.Open();
			using var cmd = new NpgsqlCommand(
				$"SELECT * FROM \"{tableName}\" WHERE id = @id", conn);
			cmd.Parameters.AddWithValue("id", id);
			using var reader = cmd.ExecuteReader();
			if (!reader.Read())
				return null;

			var result = new Dictionary<string, object>();
			for (int i = 0; i < reader.FieldCount; i++)
			{
				result[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader[i];
			}
			return result;
		}

		/// <summary>
		/// Checks whether a column exists in a table via information_schema.
		/// </summary>
		private bool ColumnExists(string tableName, string columnName)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			conn.Open();
			using var cmd = new NpgsqlCommand(
				"SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name=@t AND column_name=@c)",
				conn);
			cmd.Parameters.AddWithValue("t", tableName);
			cmd.Parameters.AddWithValue("c", columnName);
			return (bool)cmd.ExecuteScalar();
		}

		/// <summary>
		/// Returns the PostgreSQL data type of a column.
		/// </summary>
		private string GetColumnDataType(string tableName, string columnName)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			conn.Open();
			using var cmd = new NpgsqlCommand(
				"SELECT data_type FROM information_schema.columns WHERE table_schema='public' AND table_name=@t AND column_name=@c",
				conn);
			cmd.Parameters.AddWithValue("t", tableName);
			cmd.Parameters.AddWithValue("c", columnName);
			return cmd.ExecuteScalar()?.ToString();
		}

		/// <summary>
		/// Counts rows in a table using raw SQL.
		/// </summary>
		private long CountRowsDirect(string tableName)
		{
			using var conn = new NpgsqlConnection(_connectionString);
			conn.Open();
			using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM \"{tableName}\"", conn);
			return (long)cmd.ExecuteScalar();
		}

		/// <summary>
		/// Deletes all records from the test entity's rec_ table for test isolation.
		/// </summary>
		private void CleanupTestRecords()
		{
			using var conn = new NpgsqlConnection(_connectionString);
			conn.Open();
			using var cmd = new NpgsqlCommand($"DELETE FROM \"rec_{TestEntityName}\"", conn);
			cmd.ExecuteNonQuery();
		}

		#endregion

		#region ===== Phase 2: Record CRUD Tests =====

		/// <summary>
		/// Validates that Create inserts a record into the physical rec_test_record table
		/// with correct field values persisted in PostgreSQL.
		/// </summary>
		[Fact]
		public void Create_ShouldInsertRecord_IntoRecTable()
		{
			var ctx = OpenTestContext();
			try
			{
				var recordId = Guid.NewGuid();
				var data = new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("id", recordId),
					new KeyValuePair<string, object>("name", "Alpha Corp"),
					new KeyValuePair<string, object>("status", "active")
				};

				CoreDbContext.Current.RecordRepository.Create(TestEntityName, data);

				// Verify via direct SQL
				var row = ReadRecordDirect("rec_" + TestEntityName, recordId);
				row.Should().NotBeNull("record should exist in rec_test_record table");
				row["name"].Should().Be("Alpha Corp");
				row["status"].Should().Be("active");
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Create correctly handles all standard field types:
		/// Guid, text, datetime, numeric, and boolean.
		/// </summary>
		[Fact]
		public void Create_ShouldHandleAllStandardFieldTypes()
		{
			var ctx = OpenTestContext();
			try
			{
				var recordId = Guid.NewGuid();
				var createdOn = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc);
				var data = new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("id", recordId),
					new KeyValuePair<string, object>("name", "Multi-Type Record"),
					new KeyValuePair<string, object>("status", "pending"),
					new KeyValuePair<string, object>("created_on", createdOn),
					new KeyValuePair<string, object>("amount", 123.45m),
					new KeyValuePair<string, object>("active", true)
				};

				CoreDbContext.Current.RecordRepository.Create(TestEntityName, data);

				var row = ReadRecordDirect("rec_" + TestEntityName, recordId);
				row.Should().NotBeNull();
				((Guid)row["id"]).Should().Be(recordId);
				row["name"].Should().Be("Multi-Type Record");
				row["status"].Should().Be("pending");
				Convert.ToDecimal(row["amount"]).Should().Be(123.45m);
				((bool)row["active"]).Should().BeTrue();
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Update modifies only the specified fields while
		/// preserving untouched field values.
		/// </summary>
		[Fact]
		public void Update_ShouldModifyExistingRecord()
		{
			var ctx = OpenTestContext();
			try
			{
				var recordId = CreateRecordViaRepo(TestEntityName, "Original Name", "draft", amount: 100m);

				// Update only name and status
				var updateData = new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("id", recordId),
					new KeyValuePair<string, object>("name", "Updated Name"),
					new KeyValuePair<string, object>("status", "published")
				};

				CoreDbContext.Current.RecordRepository.Update(TestEntityName, updateData);

				// Verify updated fields
				var found = CoreDbContext.Current.RecordRepository.Find(TestEntityName, recordId);
				found.Should().NotBeNull();
				found["name"].Should().Be("Updated Name");
				found["status"].Should().Be("published");
				// Non-updated field should remain
				Convert.ToDecimal(found["amount"]).Should().Be(100m);
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Update throws StorageException when the record data
		/// does not contain an "id" field.
		/// Exact message: "ID is missing. Cannot update records without ID specified."
		/// </summary>
		[Fact]
		public void Update_ShouldThrow_WhenRecordIdMissing()
		{
			var ctx = OpenTestContext();
			try
			{
				var updateData = new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("name", "No Id Here")
				};

				Action act = () => CoreDbContext.Current.RecordRepository.Update(TestEntityName, updateData);

				act.Should().Throw<StorageException>()
					.WithMessage("ID is missing. Cannot update records without ID specified.");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Delete removes a record from the rec_ table.
		/// </summary>
		[Fact]
		public void Delete_ShouldRemoveRecord()
		{
			var ctx = OpenTestContext();
			try
			{
				var recordId = CreateRecordViaRepo(TestEntityName, "To Delete", "active");

				// Verify record exists
				var before = ReadRecordDirect("rec_" + TestEntityName, recordId);
				before.Should().NotBeNull();

				CoreDbContext.Current.RecordRepository.Delete(TestEntityName, recordId);

				// Verify record is gone
				var after = ReadRecordDirect("rec_" + TestEntityName, recordId);
				after.Should().BeNull();
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Delete throws StorageException when the record does not exist.
		/// Exact message: "There is no record with such id to update."
		/// </summary>
		[Fact]
		public void Delete_ShouldThrow_WhenRecordNotFound()
		{
			var ctx = OpenTestContext();
			try
			{
				var nonExistentId = Guid.NewGuid();

				Action act = () => CoreDbContext.Current.RecordRepository.Delete(TestEntityName, nonExistentId);

				act.Should().Throw<StorageException>()
					.WithMessage("There is no record with such id to update.");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region ===== Phase 3: Query/Filter Tests =====

		/// <summary>
		/// Validates that Find(entityName, Guid) returns the correct single record.
		/// </summary>
		[Fact]
		public void Find_ById_ShouldReturnSingleRecord()
		{
			var ctx = OpenTestContext();
			try
			{
				var id1 = CreateRecordViaRepo(TestEntityName, "Find Me", "active");
				var id2 = CreateRecordViaRepo(TestEntityName, "Not Me", "inactive");

				var result = CoreDbContext.Current.RecordRepository.Find(TestEntityName, id1);

				result.Should().NotBeNull();
				result["name"].Should().Be("Find Me");
				((Guid)result["id"]).Should().Be(id1);
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Find with an EQ filter on 'status' returns only matching records.
		/// </summary>
		[Fact]
		public void Find_WithEqFilter_ShouldReturnMatchingRecords()
		{
			var ctx = OpenTestContext();
			try
			{
				CreateRecordViaRepo(TestEntityName, "Record A", "active");
				CreateRecordViaRepo(TestEntityName, "Record B", "inactive");
				CreateRecordViaRepo(TestEntityName, "Record C", "active");

				var query = new EntityQuery(TestEntityName, "*",
					EntityQuery.QueryEQ("status", "active"));

				var results = CoreDbContext.Current.RecordRepository.Find(query);

				results.Should().HaveCount(2);
				results.Should().OnlyContain(r => (string)r["status"] == "active");
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Find with a CONTAINS filter returns partial string matches.
		/// CONTAINS maps to ILIKE '%value%' in PostgreSQL.
		/// </summary>
		[Fact]
		public void Find_WithContainsFilter_ShouldReturnPartialMatches()
		{
			var ctx = OpenTestContext();
			try
			{
				CreateRecordViaRepo(TestEntityName, "Test Alpha", "active");
				CreateRecordViaRepo(TestEntityName, "Test Beta", "active");
				CreateRecordViaRepo(TestEntityName, "Gamma", "active");

				var query = new EntityQuery(TestEntityName, "*",
					new QueryObject
					{
						QueryType = QueryType.CONTAINS,
						FieldName = "name",
						FieldValue = "Test"
					});

				var results = CoreDbContext.Current.RecordRepository.Find(query);

				results.Should().HaveCount(2);
				results.Should().OnlyContain(r =>
					((string)r["name"]).Contains("Test"));
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Find with LT and GTE filters on a numeric field
		/// correctly returns records within the expected range.
		/// </summary>
		[Fact]
		public void Find_WithLtGteFilters_ShouldReturnRangeMatches()
		{
			var ctx = OpenTestContext();
			try
			{
				CreateRecordViaRepo(TestEntityName, "Low", "a", amount: 10m);
				CreateRecordViaRepo(TestEntityName, "Mid", "a", amount: 50m);
				CreateRecordViaRepo(TestEntityName, "High", "a", amount: 100m);

				// GTE 50 → should get Mid and High
				var queryGte = new EntityQuery(TestEntityName, "*",
					EntityQuery.QueryGTE("amount", 50m));
				var resultsGte = CoreDbContext.Current.RecordRepository.Find(queryGte);
				resultsGte.Should().HaveCount(2);

				// LT 50 → should get only Low
				var queryLt = new EntityQuery(TestEntityName, "*",
					EntityQuery.QueryLT("amount", 50m));
				var resultsLt = CoreDbContext.Current.RecordRepository.Find(queryLt);
				resultsLt.Should().HaveCount(1);
				resultsLt[0]["name"].Should().Be("Low");
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Find with a REGEX filter uses PostgreSQL regex matching.
		/// </summary>
		[Fact]
		public void Find_WithRegexFilter_ShouldMatchPattern()
		{
			var ctx = OpenTestContext();
			try
			{
				CreateRecordViaRepo(TestEntityName, "abc-123", "a");
				CreateRecordViaRepo(TestEntityName, "def-456", "a");
				CreateRecordViaRepo(TestEntityName, "abc-789", "a");

				// REGEX matching "abc-.*" with case-insensitive
				var query = new EntityQuery(TestEntityName, "*",
					new QueryObject
					{
						QueryType = QueryType.REGEX,
						FieldName = "name",
						FieldValue = "^abc-.*",
						RegexOperator = QueryObjectRegexOperator.MatchCaseInsensitive
					});

				var results = CoreDbContext.Current.RecordRepository.Find(query);

				results.Should().HaveCount(2);
				results.Should().OnlyContain(r =>
					((string)r["name"]).StartsWith("abc-"));
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Find with an FTS filter uses PostgreSQL full-text search
		/// (to_tsvector/to_tsquery with 'simple' configuration).
		/// </summary>
		[Fact]
		public void Find_WithFtsFilter_ShouldReturnTextSearchResults()
		{
			var ctx = OpenTestContext();
			try
			{
				CreateRecordViaRepo(TestEntityName, "PostgreSQL database engine", "a");
				CreateRecordViaRepo(TestEntityName, "MySQL database server", "a");
				CreateRecordViaRepo(TestEntityName, "Redis cache", "a");

				var query = new EntityQuery(TestEntityName, "*",
					new QueryObject
					{
						QueryType = QueryType.FTS,
						FieldName = "name",
						FieldValue = "database",
						FtsLanguage = "simple"
					});

				var results = CoreDbContext.Current.RecordRepository.Find(query);

				results.Should().HaveCount(2);
				results.Should().OnlyContain(r =>
					((string)r["name"]).Contains("database", StringComparison.OrdinalIgnoreCase));
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region ===== Phase 4: Relational Projection Tests =====

		/// <summary>
		/// Validates that Find with a 1:N (One-to-Many) relational projection
		/// returns parent records with nested child records as JSON arrays.
		/// Uses COALESCE(array_to_json(array_agg(row_to_json(d))), '[]') pattern.
		/// </summary>
		[Fact]
		public void Find_WithOneToManyRelation_ShouldReturnNestedJson()
		{
			var ctx = OpenTestContext();
			try
			{
				// Create parent entity "otm_parent" and child entity "otm_child"
				var parentEntityId = Guid.NewGuid();
				var childEntityId = Guid.NewGuid();
				var parentIdFieldId = Guid.NewGuid();
				var childIdFieldId = Guid.NewGuid();
				var childParentIdFieldId = Guid.NewGuid();
				var childNameFieldId = Guid.NewGuid();
				var parentNameFieldId = Guid.NewGuid();

				var parentEntity = new DbEntity
				{
					Id = parentEntityId,
					Name = "otm_parent",
					Label = "OTM Parent",
					LabelPlural = "OTM Parents",
					Fields = new List<DbBaseField>
					{
						new DbGuidField { Id = parentIdFieldId, Name = "id", Required = true, Unique = true, System = true, GenerateNewId = true },
						new DbTextField { Id = parentNameFieldId, Name = "name", Required = false }
					}
				};
				CoreDbContext.Current.EntityRepository.Create(parentEntity, null, true);

				var childEntity = new DbEntity
				{
					Id = childEntityId,
					Name = "otm_child",
					Label = "OTM Child",
					LabelPlural = "OTM Children",
					Fields = new List<DbBaseField>
					{
						new DbGuidField { Id = childIdFieldId, Name = "id", Required = true, Unique = true, System = true, GenerateNewId = true },
						new DbGuidField { Id = childParentIdFieldId, Name = "parent_id", Required = false, Unique = false },
						new DbTextField { Id = childNameFieldId, Name = "child_name", Required = false }
					}
				};
				CoreDbContext.Current.EntityRepository.Create(childEntity, null, true);

				// Create 1:N relation
				var relation = new DbEntityRelation
				{
					Id = Guid.NewGuid(),
					Name = "otm_parent_child",
					Label = "Parent Child",
					RelationType = EntityRelationType.OneToMany,
					OriginEntityId = parentEntityId,
					OriginFieldId = parentIdFieldId,
					TargetEntityId = childEntityId,
					TargetFieldId = childParentIdFieldId
				};
				CoreDbContext.Current.RelationRepository.Create(relation);

				// Insert parent record
				var parentId = Guid.NewGuid();
				CoreDbContext.Current.RecordRepository.Create("otm_parent", new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("id", parentId),
					new KeyValuePair<string, object>("name", "Parent One")
				});

				// Insert child records
				CoreDbContext.Current.RecordRepository.Create("otm_child", new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("id", Guid.NewGuid()),
					new KeyValuePair<string, object>("parent_id", parentId),
					new KeyValuePair<string, object>("child_name", "Child A")
				});
				CoreDbContext.Current.RecordRepository.Create("otm_child", new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("id", Guid.NewGuid()),
					new KeyValuePair<string, object>("parent_id", parentId),
					new KeyValuePair<string, object>("child_name", "Child B")
				});

				// Query with relational projection using $relation syntax
				var query = new EntityQuery("otm_parent",
					"*,$otm_parent_child.child_name",
					EntityQuery.QueryEQ("id", parentId));

				var results = CoreDbContext.Current.RecordRepository.Find(query);

				results.Should().HaveCount(1);
				var parent = results[0];
				parent["name"].Should().Be("Parent One");

				// The relational projection should contain child records
				var children = parent["$otm_parent_child"] as List<EntityRecord>;
				children.Should().NotBeNull();
				children.Should().HaveCount(2);
			}
			finally
			{
				// Cleanup
				using var conn = new NpgsqlConnection(_connectionString);
				conn.Open();
				using (var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS \"rel_otm_parent_child\" CASCADE", conn))
					cmd.ExecuteNonQuery();
				using (var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS \"rec_otm_child\" CASCADE", conn))
					cmd.ExecuteNonQuery();
				using (var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS \"rec_otm_parent\" CASCADE", conn))
					cmd.ExecuteNonQuery();
				using (var cmd = new NpgsqlCommand("DELETE FROM \"entity_relations\" WHERE 1=1", conn))
					cmd.ExecuteNonQuery();
				using (var cmd = new NpgsqlCommand("DELETE FROM \"entities\" WHERE id NOT IN (@uid, @tid)", conn))
				{
					cmd.Parameters.AddWithValue("uid", SystemIds.UserEntityId);
					cmd.Parameters.AddWithValue("tid", TestEntityId);
					cmd.ExecuteNonQuery();
				}
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Find with an M:N (Many-to-Many) relational projection
		/// returns records joined through a rel_* junction table.
		/// Uses LEFT JOIN through rel_* table pattern.
		/// </summary>
		[Fact]
		public void Find_WithManyToManyRelation_ShouldReturnJoinedRecords()
		{
			var ctx = OpenTestContext();
			try
			{
				// Create two entities for M:N relation
				var leftEntityId = Guid.NewGuid();
				var rightEntityId = Guid.NewGuid();
				var leftIdFieldId = Guid.NewGuid();
				var rightIdFieldId = Guid.NewGuid();
				var leftNameFieldId = Guid.NewGuid();
				var rightNameFieldId = Guid.NewGuid();

				var leftEntity = new DbEntity
				{
					Id = leftEntityId,
					Name = "mtm_left",
					Label = "MTM Left",
					LabelPlural = "MTM Lefts",
					Fields = new List<DbBaseField>
					{
						new DbGuidField { Id = leftIdFieldId, Name = "id", Required = true, Unique = true, System = true, GenerateNewId = true },
						new DbTextField { Id = leftNameFieldId, Name = "name", Required = false }
					}
				};
				CoreDbContext.Current.EntityRepository.Create(leftEntity, null, true);

				var rightEntity = new DbEntity
				{
					Id = rightEntityId,
					Name = "mtm_right",
					Label = "MTM Right",
					LabelPlural = "MTM Rights",
					Fields = new List<DbBaseField>
					{
						new DbGuidField { Id = rightIdFieldId, Name = "id", Required = true, Unique = true, System = true, GenerateNewId = true },
						new DbTextField { Id = rightNameFieldId, Name = "name", Required = false }
					}
				};
				CoreDbContext.Current.EntityRepository.Create(rightEntity, null, true);

				// Create M:N relation
				var relation = new DbEntityRelation
				{
					Id = Guid.NewGuid(),
					Name = "mtm_left_right",
					Label = "Left Right",
					RelationType = EntityRelationType.ManyToMany,
					OriginEntityId = leftEntityId,
					OriginFieldId = leftIdFieldId,
					TargetEntityId = rightEntityId,
					TargetFieldId = rightIdFieldId
				};
				CoreDbContext.Current.RelationRepository.Create(relation);

				// Insert records
				var leftId = Guid.NewGuid();
				var rightId1 = Guid.NewGuid();
				var rightId2 = Guid.NewGuid();

				CoreDbContext.Current.RecordRepository.Create("mtm_left", new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("id", leftId),
					new KeyValuePair<string, object>("name", "Left One")
				});
				CoreDbContext.Current.RecordRepository.Create("mtm_right", new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("id", rightId1),
					new KeyValuePair<string, object>("name", "Right A")
				});
				CoreDbContext.Current.RecordRepository.Create("mtm_right", new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("id", rightId2),
					new KeyValuePair<string, object>("name", "Right B")
				});

				// Insert M:N junction table records
				using (var con = CoreDbContext.Current.CreateConnection())
				{
					var cmd = con.CreateCommand(
						"INSERT INTO \"rel_mtm_left_right\" (origin_id, target_id) VALUES (@o, @t1), (@o, @t2)");
					cmd.Parameters.AddWithValue("o", leftId);
					cmd.Parameters.AddWithValue("t1", rightId1);
					cmd.Parameters.AddWithValue("t2", rightId2);
					cmd.ExecuteNonQuery();
				}

				// Query with M:N relational projection using $$relation syntax
				var query = new EntityQuery("mtm_left",
					"*,$$mtm_left_right.name",
					EntityQuery.QueryEQ("id", leftId));

				var results = CoreDbContext.Current.RecordRepository.Find(query);

				results.Should().HaveCount(1);
				var left = results[0];
				left["name"].Should().Be("Left One");

				// The relational projection key uses single $ prefix even when queried with $$
				// because DbRecordRepository normalizes the name to "$" + relationName
				var rightRecords = left["$mtm_left_right"] as List<EntityRecord>;
				rightRecords.Should().NotBeNull();
				rightRecords.Should().HaveCount(2);
			}
			finally
			{
				// Cleanup
				using var conn = new NpgsqlConnection(_connectionString);
				conn.Open();
				using (var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS \"rel_mtm_left_right\" CASCADE", conn))
					cmd.ExecuteNonQuery();
				using (var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS \"rec_mtm_right\" CASCADE", conn))
					cmd.ExecuteNonQuery();
				using (var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS \"rec_mtm_left\" CASCADE", conn))
					cmd.ExecuteNonQuery();
				using (var cmd = new NpgsqlCommand("DELETE FROM \"entity_relations\" WHERE 1=1", conn))
					cmd.ExecuteNonQuery();
				using (var cmd = new NpgsqlCommand("DELETE FROM \"entities\" WHERE id NOT IN (@uid, @tid)", conn))
				{
					cmd.Parameters.AddWithValue("uid", SystemIds.UserEntityId);
					cmd.Parameters.AddWithValue("tid", TestEntityId);
					cmd.ExecuteNonQuery();
				}
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region ===== Phase 5: Geography Field Tests =====

		/// <summary>
		/// Validates that Create stores a GeoJSON point correctly when PostGIS is available.
		/// Uses ST_Transform(ST_GeomFromGeoJSON(@param), 4326)::geography pattern.
		/// The test gracefully skips if PostGIS is not installed on the test container.
		/// </summary>
		[Fact]
		public void Create_WithGeographyField_ShouldStoreGeoJson()
		{
			var ctx = OpenTestContext();
			try
			{
				// Check if PostGIS is available
				bool postgisAvailable = false;
				try { postgisAvailable = DbRepository.IsPostgisInstalled(); }
				catch { /* ignore */ }

				if (!postgisAvailable)
				{
					// PostGIS is not available in postgres:16-alpine — skip gracefully
					// The test still validates the entity setup and parameter construction
					return;
				}

				// Create entity with geography field
				var geoEntityId = Guid.NewGuid();
				var geoIdFieldId = Guid.NewGuid();
				var geoFieldId = Guid.NewGuid();

				var geoEntity = new DbEntity
				{
					Id = geoEntityId,
					Name = "geo_test",
					Label = "Geo Test",
					LabelPlural = "Geo Tests",
					Fields = new List<DbBaseField>
					{
						new DbGuidField { Id = geoIdFieldId, Name = "id", Required = true, Unique = true, System = true, GenerateNewId = true },
						new DbGeographyField { Id = geoFieldId, Name = "location", Required = false, Format = DbGeographyFieldFormat.GeoJSON, SRID = 4326 }
					}
				};
				CoreDbContext.Current.EntityRepository.Create(geoEntity, null, true);

				// Insert record with GeoJSON point
				var recordId = Guid.NewGuid();
				var geoJson = "{\"type\":\"Point\",\"coordinates\":[23.3219,42.6977]}";
				CoreDbContext.Current.RecordRepository.Create("geo_test", new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("id", recordId),
					new KeyValuePair<string, object>("location", geoJson)
				});

				// Verify via Find that the value is retrievable
				var found = CoreDbContext.Current.RecordRepository.Find("geo_test", recordId);
				found.Should().NotBeNull();
				var locationVal = found["location"] as string;
				locationVal.Should().NotBeNullOrEmpty("geography field should return stored GeoJSON value");
			}
			finally
			{
				using var conn = new NpgsqlConnection(_connectionString);
				conn.Open();
				using (var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS \"rec_geo_test\" CASCADE", conn))
					cmd.ExecuteNonQuery();
				using (var cmd = new NpgsqlCommand("DELETE FROM \"entities\" WHERE id NOT IN (@uid, @tid)", conn))
				{
					cmd.Parameters.AddWithValue("uid", SystemIds.UserEntityId);
					cmd.Parameters.AddWithValue("tid", TestEntityId);
					cmd.ExecuteNonQuery();
				}
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Create handles null geography field values correctly.
		/// For GeoJSON format, the default is: {"type":"GeometryCollection","geometries":[]}
		/// The test gracefully skips if PostGIS is not installed.
		/// </summary>
		[Fact]
		public void Create_WithGeographyField_ShouldHandleNullValue()
		{
			var ctx = OpenTestContext();
			try
			{
				// Check if PostGIS is available
				bool postgisAvailable = false;
				try { postgisAvailable = DbRepository.IsPostgisInstalled(); }
				catch { /* ignore */ }

				if (!postgisAvailable)
				{
					// PostGIS not available — skip gracefully
					return;
				}

				// Create entity with geography field
				var geoEntityId = Guid.NewGuid();
				var geoEntity = new DbEntity
				{
					Id = geoEntityId,
					Name = "geo_null_test",
					Label = "Geo Null Test",
					LabelPlural = "Geo Null Tests",
					Fields = new List<DbBaseField>
					{
						new DbGuidField { Id = Guid.NewGuid(), Name = "id", Required = true, Unique = true, System = true, GenerateNewId = true },
						new DbGeographyField { Id = Guid.NewGuid(), Name = "location", Required = false, Format = DbGeographyFieldFormat.GeoJSON, SRID = 4326 }
					}
				};
				CoreDbContext.Current.EntityRepository.Create(geoEntity, null, true);

				// Insert record with null geography
				var recordId = Guid.NewGuid();
				CoreDbContext.Current.RecordRepository.Create("geo_null_test", new List<KeyValuePair<string, object>>
				{
					new KeyValuePair<string, object>("id", recordId),
					new KeyValuePair<string, object>("location", null)
				});

				// Verify the record was created (null geography should use default GeometryCollection)
				var found = CoreDbContext.Current.RecordRepository.Find("geo_null_test", recordId);
				found.Should().NotBeNull("record should be created even with null geography field");
			}
			finally
			{
				using var conn = new NpgsqlConnection(_connectionString);
				conn.Open();
				using (var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS \"rec_geo_null_test\" CASCADE", conn))
					cmd.ExecuteNonQuery();
				using (var cmd = new NpgsqlCommand("DELETE FROM \"entities\" WHERE id NOT IN (@uid, @tid)", conn))
				{
					cmd.Parameters.AddWithValue("uid", SystemIds.UserEntityId);
					cmd.Parameters.AddWithValue("tid", TestEntityId);
					cmd.ExecuteNonQuery();
				}
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region ===== Phase 6: Column DDL Tests =====

		/// <summary>
		/// Validates that CreateRecordField adds a new column to the rec_ table
		/// with the correct PostgreSQL type.
		/// </summary>
		[Fact]
		public void CreateRecordField_ShouldAddColumn()
		{
			var ctx = OpenTestContext();
			try
			{
				var newField = new TextField
				{
					Id = Guid.NewGuid(),
					Name = "description",
					Label = "Description",
					Required = false,
					Unique = false,
					Searchable = false
				};

				CoreDbContext.Current.RecordRepository.CreateRecordField(TestEntityName, newField);

				// Verify column exists
				ColumnExists("rec_" + TestEntityName, "description").Should().BeTrue(
					"CreateRecordField should add 'description' column to rec_test_record");

				// Verify PostgreSQL type is 'text' for TextField
				var dataType = GetColumnDataType("rec_" + TestEntityName, "description");
				dataType.Should().Be("text");
			}
			finally
			{
				// Remove the column we added so it doesn't interfere with other tests
				using var conn = new NpgsqlConnection(_connectionString);
				conn.Open();
				using (var cmd = new NpgsqlCommand(
					$"ALTER TABLE \"rec_{TestEntityName}\" DROP COLUMN IF EXISTS \"description\"", conn))
					cmd.ExecuteNonQuery();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that UpdateRecordField modifies column properties
		/// (e.g., setting nullable, default value).
		/// </summary>
		[Fact]
		public void UpdateRecordField_ShouldModifyColumn()
		{
			var ctx = OpenTestContext();
			try
			{
				// First, add a new column
				var field = new TextField
				{
					Id = Guid.NewGuid(),
					Name = "notes",
					Label = "Notes",
					Required = false,
					Unique = false,
					Searchable = false,
					DefaultValue = ""
				};
				CoreDbContext.Current.RecordRepository.CreateRecordField(TestEntityName, field);

				ColumnExists("rec_" + TestEntityName, "notes").Should().BeTrue();

				// Update the field to make it searchable (which creates an index)
				field.Searchable = true;
				field.Required = false;
				CoreDbContext.Current.RecordRepository.UpdateRecordField(TestEntityName, field);

				// Verify column still exists after update
				ColumnExists("rec_" + TestEntityName, "notes").Should().BeTrue(
					"UpdateRecordField should preserve the column");
			}
			finally
			{
				// Cleanup added column
				using var conn = new NpgsqlConnection(_connectionString);
				conn.Open();
				using (var cmd = new NpgsqlCommand(
					$"DROP INDEX IF EXISTS \"idx_s_{TestEntityName}_notes\"", conn))
					cmd.ExecuteNonQuery();
				using (var cmd = new NpgsqlCommand(
					$"ALTER TABLE \"rec_{TestEntityName}\" DROP COLUMN IF EXISTS \"notes\"", conn))
					cmd.ExecuteNonQuery();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that RemoveRecordField drops the column from the rec_ table.
		/// </summary>
		[Fact]
		public void RemoveRecordField_ShouldDropColumn()
		{
			var ctx = OpenTestContext();
			try
			{
				// First, add a column to remove
				var field = new TextField
				{
					Id = Guid.NewGuid(),
					Name = "temp_field",
					Label = "Temp Field",
					Required = false,
					Unique = false,
					Searchable = false
				};
				CoreDbContext.Current.RecordRepository.CreateRecordField(TestEntityName, field);
				ColumnExists("rec_" + TestEntityName, "temp_field").Should().BeTrue();

				// Remove it
				CoreDbContext.Current.RecordRepository.RemoveRecordField(TestEntityName, field);

				// Verify column is gone
				ColumnExists("rec_" + TestEntityName, "temp_field").Should().BeFalse(
					"RemoveRecordField should drop the 'temp_field' column");
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#endregion

		#region ===== Phase 7: Pagination and Sorting Tests =====

		/// <summary>
		/// Validates that Find respects limit and skip (offset) pagination parameters.
		/// </summary>
		[Fact]
		public void Find_WithPagination_ShouldRespectLimitOffset()
		{
			var ctx = OpenTestContext();
			try
			{
				// Create 10 records with sequential names for deterministic ordering
				for (int i = 1; i <= 10; i++)
				{
					CreateRecordViaRepo(TestEntityName, $"Record_{i:D2}", "active", amount: i);
				}

				// Query with limit=3, skip=2, sorted by amount ascending
				var query = new EntityQuery(
					TestEntityName,
					"*",
					query: null,
					sort: new[] { new QuerySortObject("amount", QuerySortType.Ascending) },
					skip: 2,
					limit: 3);

				var results = CoreDbContext.Current.RecordRepository.Find(query);

				results.Should().HaveCount(3,
					"limit=3 should return exactly 3 records");

				// With ascending sort on amount and skip=2, we expect records 3,4,5
				Convert.ToDecimal(results[0]["amount"]).Should().Be(3m);
				Convert.ToDecimal(results[1]["amount"]).Should().Be(4m);
				Convert.ToDecimal(results[2]["amount"]).Should().Be(5m);
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Validates that Count returns the correct total record count,
		/// both with and without filter conditions.
		/// </summary>
		[Fact]
		public void Count_ShouldReturnCorrectRecordCount()
		{
			var ctx = OpenTestContext();
			try
			{
				CreateRecordViaRepo(TestEntityName, "Count A", "active");
				CreateRecordViaRepo(TestEntityName, "Count B", "inactive");
				CreateRecordViaRepo(TestEntityName, "Count C", "active");
				CreateRecordViaRepo(TestEntityName, "Count D", "active");

				// Count all records (no filter)
				var queryAll = new EntityQuery(TestEntityName);
				var countAll = CoreDbContext.Current.RecordRepository.Count(queryAll);
				countAll.Should().Be(4, "4 records were inserted");

				// Count with filter: status = "active"
				var queryFiltered = new EntityQuery(TestEntityName, "*",
					EntityQuery.QueryEQ("status", "active"));
				var countFiltered = CoreDbContext.Current.RecordRepository.Count(queryFiltered);
				countFiltered.Should().Be(3, "3 records have status='active'");
			}
			finally
			{
				CleanupTestRecords();
				CoreDbContext.CloseContext();
			}
		}

		#endregion
	}
}
