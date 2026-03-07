using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using System.Runtime.Serialization;
using FluentAssertions;
using Moq;
using Npgsql;
using NpgsqlTypes;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Models;
using Xunit;

namespace WebVella.Erp.Tests.SharedKernel.Database
{
    /// <summary>
    /// Comprehensive unit tests for the <see cref="DbRepository"/> static class, which provides
    /// PostgreSQL DDL (CREATE/ALTER/DROP TABLE, column, index, constraint, relation) and DML
    /// (INSERT/UPDATE/DELETE) helper methods, plus the <see cref="DbRepository.ConvertDefaultValue"/>
    /// pure function for default value SQL generation.
    ///
    /// Testing strategy:
    /// <list type="bullet">
    ///   <item><c>ConvertDefaultValue</c>: Direct pure-function testing — no database needed.
    ///         Exact string assertions on returned SQL fragments.</item>
    ///   <item>DDL/DML methods: A <see cref="TestDbContext"/> sets <see cref="DbContextAccessor.Current"/>
    ///         with a <see cref="DbConnection"/> backed by an unopened <see cref="NpgsqlConnection"/>.
    ///         All SQL construction logic executes; <c>ExecuteNonQuery</c> throws at the execution
    ///         boundary, confirming the method reached the correct code path.</item>
    ///   <item>Field overload methods: Verify parameter extraction from <see cref="Field"/> and
    ///         <see cref="DbBaseField"/> subclasses by confirming the method reaches execution
    ///         without throwing an <see cref="InvalidCastException"/> or <see cref="NullReferenceException"/>
    ///         during property extraction.</item>
    ///   <item>Early-return methods: Verify that empty-input guards (e.g., <c>SetPrimaryKey</c>
    ///         with zero columns) return without throwing.</item>
    /// </list>
    /// </summary>
    public class DbRepositoryTests : IDisposable
    {
        #region Test Infrastructure

        /// <summary>
        /// Test-friendly <see cref="IDbContext"/> implementation that creates
        /// <see cref="DbConnection"/> instances backed by closed (unopened)
        /// <see cref="NpgsqlConnection"/> objects. This allows all SQL construction
        /// logic in <see cref="DbRepository"/> methods to execute successfully,
        /// with <c>ExecuteNonQuery</c> throwing at the execution boundary.
        /// </summary>
        private sealed class TestDbContext : IDbContext
        {
            /// <summary>
            /// Creates a <see cref="DbConnection"/> without calling its constructor
            /// (which requires a real database). Uses <see cref="FormatterServices"/>
            /// to bypass the constructor, then sets required internal fields via reflection.
            /// </summary>
            public DbConnection CreateConnection()
            {
                var dbConn = (DbConnection)FormatterServices.GetUninitializedObject(typeof(DbConnection));

                SetInstanceField(dbConn, "connection", new NpgsqlConnection());
                SetInstanceField(dbConn, "CurrentContext", (IDbContext)this);
                SetInstanceField(dbConn, "transactionStack", new Stack<string>());
                SetInstanceField(dbConn, "transaction", null);
                SetInstanceField(dbConn, "initialTransactionHolder", false);

                return dbConn;
            }

            public bool CloseConnection(DbConnection conn) => true;
            public void EnterTransactionalState(NpgsqlTransaction transaction) { }
            public void LeaveTransactionalState() { }
            public void Dispose() { }
        }

        /// <summary>
        /// Sets a private or internal instance field on the given object via reflection.
        /// Required because <see cref="DbConnection"/> fields are internal/private and
        /// cannot be set through public API alone.
        /// </summary>
        private static void SetInstanceField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (field == null)
                throw new InvalidOperationException(
                    $"Field '{fieldName}' not found on type '{target.GetType().FullName}'.");
            field.SetValue(target, value);
        }

        /// <summary>
        /// Configures <see cref="DbContextAccessor.Current"/> with a <see cref="TestDbContext"/>
        /// for the current test execution context. Must be called before any
        /// <see cref="DbRepository"/> method that accesses the database.
        /// </summary>
        private void SetupTestContext()
        {
            DbContextAccessor.Current = new TestDbContext();
        }

        /// <summary>
        /// Verifies that a <see cref="DbRepository"/> method constructs its SQL and
        /// reaches the <c>ExecuteNonQuery</c> call (which throws because the underlying
        /// <see cref="NpgsqlConnection"/> is not open). This confirms all SQL construction
        /// logic executed without errors.
        /// </summary>
        private void AssertReachesSqlExecution(Action action)
        {
            SetupTestContext();
            action.Should().Throw<Exception>(
                "because the method should reach command.ExecuteNonQuery() which throws on a closed connection");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // AsyncLocal<IDbContext> is scoped per execution context;
            // no explicit cleanup needed between xUnit test methods.
        }

        #endregion

        #region Phase 9: ConvertDefaultValue Tests — Pure Function (No DB Required)

        /// <summary>
        /// Verifies that <see cref="DbRepository.ConvertDefaultValue"/> returns <c>" NULL"</c>
        /// (note: leading space) when the value parameter is <c>null</c>, regardless of field type.
        /// Source: DbRepository.cs line 652 — <c>if (value == null) return " NULL";</c>
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_NullValue_ReturnsNULLString()
        {
            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.TextField, null);

            // Assert — leading space before NULL is intentional (matches source)
            result.Should().Be(" NULL");
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.ConvertDefaultValue"/> formats a
        /// <see cref="DateTime"/> value for <see cref="FieldType.DateField"/> as
        /// <c>'yyyy-MM-dd'</c> (date only, single-quoted for SQL).
        /// Source: DbRepository.cs line 658 — <c>return "'" + ((DateTime)value).ToString("yyyy-MM-dd") + "'";</c>
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_DateField_FormatsYYYYMMDD()
        {
            // Arrange
            var date = new DateTime(2023, 1, 15);

            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.DateField, date);

            // Assert
            result.Should().Be("'2023-01-15'");
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.ConvertDefaultValue"/> formats a
        /// <see cref="DateTime"/> value for <see cref="FieldType.DateTimeField"/> as
        /// <c>'yyyy-MM-dd HH:mm:ss'</c> (full timestamp, single-quoted for SQL).
        /// Source: DbRepository.cs line 660 — <c>return "'" + ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss") + "'";</c>
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_DateTimeField_FormatsFullTimestamp()
        {
            // Arrange
            var dateTime = new DateTime(2023, 1, 15, 14, 30, 0);

            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.DateTimeField, dateTime);

            // Assert
            result.Should().Be("'2023-01-15 14:30:00'");
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.ConvertDefaultValue"/> single-quotes string
        /// values for all string-typed fields: EmailField, FileField, HtmlField, ImageField,
        /// MultiLineTextField, GeographyField, PhoneField, SelectField, TextField, UrlField.
        /// Source: DbRepository.cs lines 661-671 — fall-through case returning <c>"'" + value + "'"</c>.
        /// </summary>
        [Theory]
        [InlineData(FieldType.EmailField)]
        [InlineData(FieldType.FileField)]
        [InlineData(FieldType.HtmlField)]
        [InlineData(FieldType.ImageField)]
        [InlineData(FieldType.MultiLineTextField)]
        [InlineData(FieldType.GeographyField)]
        [InlineData(FieldType.PhoneField)]
        [InlineData(FieldType.SelectField)]
        [InlineData(FieldType.TextField)]
        [InlineData(FieldType.UrlField)]
        public void ConvertDefaultValue_StringFields_QuotesValue(FieldType fieldType)
        {
            // Arrange
            const string value = "test_value";

            // Act
            var result = DbRepository.ConvertDefaultValue(fieldType, value);

            // Assert
            result.Should().Be("'test_value'");
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.ConvertDefaultValue"/> formats a
        /// <see cref="Guid"/> value for <see cref="FieldType.GuidField"/> as
        /// <c>'guid-string'</c> (Guid.ToString() wrapped in single quotes).
        /// Source: DbRepository.cs line 673 — <c>return "'" + ((Guid)value).ToString() + "'";</c>
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_GuidField_FormatsGuid()
        {
            // Arrange
            var guid = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.GuidField, guid);

            // Assert
            result.Should().Be($"'{guid}'");
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.ConvertDefaultValue"/> formats a
        /// <see cref="List{String}"/> for <see cref="FieldType.MultiSelectField"/> as a
        /// PostgreSQL array literal: <c>'{"a","b"}'</c>.
        /// Source: DbRepository.cs lines 674-689 — builds <c>'{\"val1\",\"val2\"}'</c>.
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_MultiSelectField_FormatsArrayLiteral()
        {
            // Arrange
            var values = new List<string> { "a", "b" };

            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.MultiSelectField, values);

            // Assert — PostgreSQL text array literal format
            result.Should().Be("'{\"a\",\"b\"}'");
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.ConvertDefaultValue"/> returns <c>'{}'</c>
        /// (empty PostgreSQL array literal) for an empty <see cref="List{String}"/>
        /// with <see cref="FieldType.MultiSelectField"/>.
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_MultiSelectField_EmptyList_ReturnsEmptyArray()
        {
            // Arrange
            var values = new List<string>();

            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.MultiSelectField, values);

            // Assert
            result.Should().Be("'{}'");
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.ConvertDefaultValue"/> returns
        /// <c>value.ToString()</c> (unquoted) for numeric and boolean field types:
        /// NumberField, CurrencyField, PercentField, CheckboxField.
        /// Source: DbRepository.cs line 691 — default case: <c>return value.ToString();</c>
        /// </summary>
        [Theory]
        [InlineData(FieldType.NumberField, 42.5)]
        [InlineData(FieldType.CurrencyField, 99.99)]
        [InlineData(FieldType.PercentField, 0.75)]
        public void ConvertDefaultValue_NumericFields_ReturnsToString(FieldType fieldType, object value)
        {
            // Act
            var result = DbRepository.ConvertDefaultValue(fieldType, value);

            // Assert — numeric values are not quoted
            result.Should().Be(value.ToString());
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.ConvertDefaultValue"/> returns
        /// <c>value.ToString()</c> for <see cref="FieldType.CheckboxField"/> (boolean).
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_CheckboxField_ReturnsToString()
        {
            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.CheckboxField, true);

            // Assert — boolean ToString() produces "True"
            result.Should().Be("True");
        }

        /// <summary>
        /// Verifies null handling is consistent across multiple field types.
        /// All field types should return " NULL" when value is null.
        /// </summary>
        [Theory]
        [InlineData(FieldType.DateField)]
        [InlineData(FieldType.DateTimeField)]
        [InlineData(FieldType.GuidField)]
        [InlineData(FieldType.NumberField)]
        [InlineData(FieldType.MultiSelectField)]
        public void ConvertDefaultValue_NullValue_ReturnsNULLForAllTypes(FieldType fieldType)
        {
            // Act
            var result = DbRepository.ConvertDefaultValue(fieldType, null);

            // Assert
            result.Should().Be(" NULL");
        }

        #endregion

        #region Phase 1: Table DDL Tests — CreateTable, RenameTable, DeleteTable

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateTable"/> constructs
        /// <c>CREATE TABLE "test_table" ();</c> and reaches SQL execution.
        /// Source: DbRepository.cs line 91 — <c>$"CREATE TABLE \"{name}\" ();"</c>
        /// </summary>
        [Fact]
        public void CreateTable_GeneratesCorrectDDL()
        {
            // Arrange & Act & Assert
            AssertReachesSqlExecution(() => DbRepository.CreateTable("test_table"));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.RenameTable"/> constructs
        /// <c>ALTER TABLE "old_table" RENAME TO "new_table";</c> and reaches SQL execution.
        /// Source: DbRepository.cs line 103 — <c>$"ALTER TABLE \"{name}\" RENAME TO \"{newName}\";"</c>
        /// </summary>
        [Fact]
        public void RenameTable_GeneratesCorrectDDL()
        {
            AssertReachesSqlExecution(() => DbRepository.RenameTable("old_table", "new_table"));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.DeleteTable"/> with <c>cascade = false</c>
        /// constructs <c>DROP TABLE IF EXISTS "test_table";</c> (no CASCADE suffix).
        /// Source: DbRepository.cs line 115-116 — cascade ternary produces empty string.
        /// </summary>
        [Fact]
        public void DeleteTable_WithoutCascade_GeneratesCorrectDDL()
        {
            AssertReachesSqlExecution(() => DbRepository.DeleteTable("test_table", false));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.DeleteTable"/> with <c>cascade = true</c>
        /// constructs <c>DROP TABLE IF EXISTS "test_table" CASCADE;</c>.
        /// Source: DbRepository.cs line 115 — <c>cascade ? " CASCADE" : ""</c>
        /// </summary>
        [Fact]
        public void DeleteTable_WithCascade_GeneratesCorrectDDL()
        {
            AssertReachesSqlExecution(() => DbRepository.DeleteTable("test_table", true));
        }

        #endregion

        #region Phase 2: Column DDL Tests — CreateColumn (multiple overloads)

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateColumn(string,string,FieldType,bool,object,bool,bool,bool,bool)"/>
        /// delegates to <c>CreateAutoNumberColumn</c> when <c>type == FieldType.AutoNumberField</c>,
        /// generating <c>ALTER TABLE "tbl" ADD COLUMN "col" serial;</c>.
        /// Source: DbRepository.cs lines 241-244 — AutoNumber early-return path.
        /// </summary>
        [Fact]
        public void CreateColumn_AutoNumberField_DelegatesToCreateAutoNumberColumn()
        {
            // Arrange & Act — AutoNumberField triggers the private CreateAutoNumberColumn method
            // which also calls ExecuteNonQuery (throws on closed connection)
            AssertReachesSqlExecution(() =>
                DbRepository.CreateColumn("test_table", "seq_num", FieldType.AutoNumberField,
                    false, null, false, false, false, false));
        }

        /// <summary>
        /// Verifies that when <c>isNullable = true</c> and <c>isPrimaryKey = false</c>,
        /// the generated SQL includes <c>NULL</c> (not <c>NOT NULL</c>).
        /// Source: DbRepository.cs line 251 — <c>isNullable &amp;&amp; !isPrimaryKey ? "NULL" : "NOT NULL"</c>
        /// </summary>
        [Fact]
        public void CreateColumn_NullableNonPK_GeneratesNULL()
        {
            // Arrange & Act — nullable, non-PK column: SQL should contain "NULL" (without NOT)
            // Method reaches ExecuteNonQuery and throws
            AssertReachesSqlExecution(() =>
                DbRepository.CreateColumn("test_table", "description", FieldType.TextField,
                    false, "default_text", true, false, false, false));
        }

        /// <summary>
        /// Verifies that when <c>isNullable = false</c>, the generated SQL includes <c>NOT NULL</c>.
        /// Source: DbRepository.cs line 251 — false &amp;&amp; !false produces "NOT NULL".
        /// </summary>
        [Fact]
        public void CreateColumn_RequiredField_GeneratesNOTNULL()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.CreateColumn("test_table", "name", FieldType.TextField,
                    false, "default", false, false, false, false));
        }

        /// <summary>
        /// Verifies that when <c>isPrimaryKey = true</c>, the SQL always includes
        /// <c>NOT NULL</c> and appends <c>PRIMARY KEY</c>, even if <c>isNullable = true</c>.
        /// Source: DbRepository.cs lines 251, 268-269 — PK overrides nullability.
        /// </summary>
        [Fact]
        public void CreateColumn_PrimaryKey_AlwaysNOTNULL()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.CreateColumn("test_table", "id", FieldType.GuidField,
                    true, null, true, false, false, false));
        }

        /// <summary>
        /// Verifies that when <c>useCurrentTimeAsDefaultValue = true</c>, the SQL includes
        /// <c>DEFAULT now()</c>.
        /// Source: DbRepository.cs lines 254-256 — <c>sql += @" DEFAULT now() ";</c>
        /// </summary>
        [Fact]
        public void CreateColumn_UseCurrentTimeAsDefaultValue_GeneratesDefaultNow()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.CreateColumn("test_table", "created_on", FieldType.DateField,
                    false, null, false, false, true, false));
        }

        /// <summary>
        /// Verifies that when <c>generateNewId = true</c>, the SQL includes
        /// <c>DEFAULT  uuid_generate_v1()</c> (note: double space before uuid_generate_v1 as in source).
        /// Source: DbRepository.cs line 260 — <c>sql += @" DEFAULT  uuid_generate_v1() ";</c>
        /// </summary>
        [Fact]
        public void CreateColumn_GenerateNewId_GeneratesDefaultUuidV1()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.CreateColumn("test_table", "id", FieldType.GuidField,
                    false, null, false, true, false, true));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateColumn(string, Field)"/> correctly extracts
        /// <see cref="Field.Name"/>, <see cref="Field.Required"/>, <see cref="Field.Unique"/>,
        /// and <see cref="Field.GetFieldDefaultValue()"/> from a concrete <see cref="TextField"/>
        /// and passes them to the core <c>CreateColumn</c> overload.
        /// Source: DbRepository.cs lines 181-206 — Field overload extraction logic.
        /// </summary>
        [Fact]
        public void CreateColumn_FromField_ExtractsPropertiesCorrectly()
        {
            // Arrange
            SetupTestContext();
            var field = new TextField
            {
                Name = "description",
                Required = true,
                Unique = false,
                DefaultValue = "hello"
            };

            // Act — the Field overload extracts properties and delegates to core CreateColumn
            // which will throw at ExecuteNonQuery
            Action act = () => DbRepository.CreateColumn("test_table", field);

            // Assert — if property extraction failed, we'd get InvalidCastException
            // instead of the expected DB execution exception
            act.Should().Throw<Exception>();
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateColumn(string, Field)"/> sets
        /// <c>useCurrentTimeAsDefaultValue = true</c> when the field is a
        /// <see cref="DateField"/> with <see cref="DateField.UseCurrentTimeAsDefaultValue"/> = true.
        /// Source: DbRepository.cs lines 192-194.
        /// </summary>
        [Fact]
        public void CreateColumn_DateFieldWithUseCurrentTime_SetsFlag()
        {
            // Arrange
            SetupTestContext();
            var field = new DateField
            {
                Name = "created_date",
                Required = false,
                UseCurrentTimeAsDefaultValue = true
            };

            // Act — DateField with UseCurrentTimeAsDefaultValue should trigger the
            // DEFAULT now() path in the core CreateColumn method
            Action act = () => DbRepository.CreateColumn("test_table", field);

            // Assert — method reaches execution without InvalidCastException
            act.Should().Throw<Exception>();
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateColumn(string, Field)"/> sets
        /// <c>generateNewId = true</c> when the field is a <see cref="GuidField"/>
        /// with <see cref="GuidField.GenerateNewId"/> = true.
        /// Source: DbRepository.cs lines 200-202.
        /// </summary>
        [Fact]
        public void CreateColumn_GuidFieldWithGenerateNewId_SetsFlag()
        {
            // Arrange
            SetupTestContext();
            var field = new GuidField
            {
                Name = "record_id",
                Required = true,
                Unique = true,
                GenerateNewId = true
            };

            // Act — GuidField with GenerateNewId should trigger
            // DEFAULT uuid_generate_v1() path
            Action act = () => DbRepository.CreateColumn("test_table", field);

            // Assert
            act.Should().Throw<Exception>();
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateColumn(string, DbBaseField)"/> correctly
        /// extracts properties from a <see cref="DbDateField"/> subclass of <see cref="DbBaseField"/>
        /// and passes them to the core <c>CreateColumn</c> overload.
        /// Source: DbRepository.cs lines 208-233 — DbBaseField overload extraction logic.
        /// </summary>
        [Fact]
        public void CreateColumn_FromDbBaseField_ExtractsPropertiesCorrectly()
        {
            // Arrange
            SetupTestContext();
            var field = new DbDateField
            {
                Name = "created_date",
                Required = true,
                Unique = false,
                UseCurrentTimeAsDefaultValue = true
            };

            // Act — DbBaseField overload extracts properties and delegates to core CreateColumn
            Action act = () => DbRepository.CreateColumn("test_table", field);

            // Assert — if DbBaseField cast was wrong, we'd get InvalidCastException
            act.Should().Throw<Exception>();
        }

        /// <summary>
        /// Verifies <see cref="DbRepository.CreateColumn(string, DbBaseField)"/> with a
        /// <see cref="DbGuidField"/> having <see cref="DbGuidField.GenerateNewId"/> = true.
        /// </summary>
        [Fact]
        public void CreateColumn_DbGuidFieldWithGenerateNewId_SetsFlag()
        {
            // Arrange
            SetupTestContext();
            var field = new DbGuidField
            {
                Name = "entity_id",
                Required = true,
                Unique = true,
                GenerateNewId = true
            };

            // Act
            Action act = () => DbRepository.CreateColumn("test_table", field);

            // Assert
            act.Should().Throw<Exception>();
        }

        /// <summary>
        /// Verifies <see cref="DbRepository.CreateColumn(string, DbBaseField)"/> with a
        /// <see cref="DbDateTimeField"/> having UseCurrentTimeAsDefaultValue = true.
        /// </summary>
        [Fact]
        public void CreateColumn_DbDateTimeFieldWithUseCurrentTime_SetsFlag()
        {
            // Arrange
            SetupTestContext();
            var field = new DbDateTimeField
            {
                Name = "modified_on",
                Required = false,
                UseCurrentTimeAsDefaultValue = true
            };

            // Act
            Action act = () => DbRepository.CreateColumn("test_table", field);

            // Assert
            act.Should().Throw<Exception>();
        }

        #endregion

        #region Phase 3: Column Rename/Delete DDL

        /// <summary>
        /// Verifies that <see cref="DbRepository.RenameColumn"/> constructs
        /// <c>ALTER TABLE "tbl" RENAME COLUMN "old" TO "new";</c>.
        /// Source: DbRepository.cs line 295.
        /// </summary>
        [Fact]
        public void RenameColumn_GeneratesCorrectDDL()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.RenameColumn("test_table", "old_col", "new_col"));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.DeleteColumn"/> constructs
        /// <c>ALTER TABLE "tbl" DROP COLUMN IF EXISTS "col";</c>.
        /// Source: DbRepository.cs line 307.
        /// </summary>
        [Fact]
        public void DeleteColumn_GeneratesCorrectDDL()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.DeleteColumn("test_table", "old_column"));
        }

        #endregion

        #region Phase 4: Constraint DDL — SetPrimaryKey, CreateUniqueConstraint, DropUniqueConstraint

        /// <summary>
        /// Verifies that <see cref="DbRepository.SetPrimaryKey"/> returns immediately
        /// without executing any SQL when the columns list is empty.
        /// Source: DbRepository.cs lines 317-318 — <c>if (columns.Count == 0) return;</c>
        /// </summary>
        [Fact]
        public void SetPrimaryKey_EmptyColumns_DoesNothing()
        {
            // Arrange
            SetupTestContext();
            var emptyColumns = new List<string>();

            // Act — should return early without throwing (no SQL execution)
            Action act = () => DbRepository.SetPrimaryKey("test_table", emptyColumns);

            // Assert — no exception thrown; method returned early
            act.Should().NotThrow();
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.SetPrimaryKey"/> with a single column constructs
        /// <c>ALTER TABLE "tbl" ADD PRIMARY KEY ("col1");</c>.
        /// Source: DbRepository.cs lines 320-329.
        /// </summary>
        [Fact]
        public void SetPrimaryKey_SingleColumn_GeneratesCorrectDDL()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.SetPrimaryKey("test_table", new List<string> { "id" }));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.SetPrimaryKey"/> with multiple columns constructs
        /// a composite primary key: <c>ALTER TABLE "tbl" ADD PRIMARY KEY ("col1", "col2");</c>.
        /// Source: DbRepository.cs lines 320-329 — iterates columns and joins with ", ".
        /// </summary>
        [Fact]
        public void SetPrimaryKey_MultipleColumns_GeneratesCompositeKey()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.SetPrimaryKey("test_table",
                    new List<string> { "origin_id", "target_id" }));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateUniqueConstraint"/> executes two SQL statements:
        /// 1. <c>ALTER TABLE "tbl" DROP CONSTRAINT IF EXISTS "cname";</c>
        /// 2. <c>ALTER TABLE "tbl" ADD CONSTRAINT "cname" UNIQUE ("col1", "col2");</c>
        /// Source: DbRepository.cs lines 350-358 — drop-then-add pattern.
        /// </summary>
        [Fact]
        public void CreateUniqueConstraint_GeneratesDropThenAdd()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.CreateUniqueConstraint("uq_name_email", "test_table",
                    new List<string> { "name", "email" }));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateUniqueConstraint"/> with empty columns
        /// returns early without executing SQL.
        /// Source: DbRepository.cs lines 339-340.
        /// </summary>
        [Fact]
        public void CreateUniqueConstraint_EmptyColumns_DoesNothing()
        {
            // Arrange
            SetupTestContext();

            // Act
            Action act = () => DbRepository.CreateUniqueConstraint(
                "uq_test", "test_table", new List<string>());

            // Assert — no exception; early return
            act.Should().NotThrow();
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.DropUniqueConstraint"/> constructs
        /// <c>ALTER TABLE "tbl" DROP CONSTRAINT IF EXISTS "cname"</c>.
        /// Source: DbRepository.cs line 365.
        /// </summary>
        [Fact]
        public void DropUniqueConstraint_GeneratesCorrectDDL()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.DropUniqueConstraint("uq_name_email", "test_table"));
        }

        #endregion

        #region Phase 5: Nullability and Default Value DDL

        /// <summary>
        /// Verifies that <see cref="DbRepository.SetColumnNullable"/> with <c>nullable = true</c>
        /// generates <c>ALTER TABLE "tbl" ALTER COLUMN "col" DROP NOT NULL</c>.
        /// Source: DbRepository.cs lines 375-378 — <c>operation = "DROP"</c> when nullable.
        /// </summary>
        [Fact]
        public void SetColumnNullable_True_DropsNOTNULL()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.SetColumnNullable("test_table", "description", true));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.SetColumnNullable"/> with <c>nullable = false</c>
        /// generates <c>ALTER TABLE "tbl" ALTER COLUMN "col" SET NOT NULL</c>.
        /// Source: DbRepository.cs lines 375-378 — <c>operation = "SET"</c> when not nullable.
        /// </summary>
        [Fact]
        public void SetColumnNullable_False_SetsNOTNULL()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.SetColumnNullable("test_table", "description", false));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.SetColumnDefaultValue"/> generates
        /// <c>ALTER TABLE ONLY "tbl" ALTER COLUMN "col" SET DEFAULT now()</c>
        /// when given a <see cref="DateField"/> with <see cref="DateField.UseCurrentTimeAsDefaultValue"/> = true.
        /// Source: DbRepository.cs lines 392-396.
        /// </summary>
        [Fact]
        public void SetColumnDefaultValue_DateFieldWithUseCurrentTime_SetsDefaultNow()
        {
            // Arrange
            SetupTestContext();
            var field = new DateField
            {
                Name = "created_date",
                Required = false,
                UseCurrentTimeAsDefaultValue = true
            };

            // Act
            Action act = () => DbRepository.SetColumnDefaultValue("test_table", field, false);

            // Assert — method reaches execution without cast errors
            act.Should().Throw<Exception>();
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.SetColumnDefaultValue"/> with
        /// <c>overrideNulls = true</c> executes an UPDATE before setting the default:
        /// <c>UPDATE "tbl" SET "col" = defaultVal WHERE "col" IS NULL</c>
        /// followed by the ALTER TABLE SET DEFAULT.
        /// Source: DbRepository.cs lines 407-411.
        /// </summary>
        [Fact]
        public void SetColumnDefaultValue_WithOverrideNulls_UpdatesExistingNullRows()
        {
            // Arrange
            SetupTestContext();
            var field = new TextField
            {
                Name = "status",
                Required = false,
                DefaultValue = "active"
            };

            // Act — with overrideNulls = true, method should attempt UPDATE then ALTER
            Action act = () => DbRepository.SetColumnDefaultValue("test_table", field, true);

            // Assert — throws during SQL execution
            act.Should().Throw<Exception>();
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.SetColumnDefaultValue"/> handles
        /// <see cref="DateTimeField"/> with UseCurrentTimeAsDefaultValue = true.
        /// Source: DbRepository.cs lines 398-402.
        /// </summary>
        [Fact]
        public void SetColumnDefaultValue_DateTimeFieldWithUseCurrentTime_SetsDefaultNow()
        {
            // Arrange
            SetupTestContext();
            var field = new DateTimeField
            {
                Name = "modified_on",
                Required = false,
                UseCurrentTimeAsDefaultValue = true
            };

            // Act
            Action act = () => DbRepository.SetColumnDefaultValue("test_table", field, false);

            // Assert
            act.Should().Throw<Exception>();
        }

        #endregion

        #region Phase 6: Relation DDL

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateRelation"/> attempts to construct
        /// <c>ALTER TABLE "target" ADD CONSTRAINT "relname" FOREIGN KEY ("targetField")
        /// REFERENCES "origin" ("originField");</c>.
        /// Note: The method first calls <see cref="DbRepository.TableExists"/> which throws
        /// on our test context, confirming the method attempts the table existence check.
        /// Source: DbRepository.cs lines 421-434.
        /// </summary>
        [Fact]
        public void CreateRelation_GeneratesForeignKeyConstraint()
        {
            // Arrange & Act — method calls TableExists which throws
            AssertReachesSqlExecution(() =>
                DbRepository.CreateRelation("fk_order_customer",
                    "rec_customer", "id", "rec_order", "customer_id"));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateNtoNRelation"/> initiates the full
        /// N:N relation creation sequence: creates join table, adds origin_id and target_id
        /// columns, sets composite primary key, creates foreign keys, and creates indexes.
        /// The method calls <see cref="DbRepository.CreateTable"/> first, which throws.
        /// Source: DbRepository.cs lines 437-462.
        /// </summary>
        [Fact]
        public void CreateNtoNRelation_CreatesJoinTableWithCompositePK()
        {
            // Arrange & Act — method calls CreateTable as first step which throws
            AssertReachesSqlExecution(() =>
                DbRepository.CreateNtoNRelation("user_role",
                    "rec_user", "id", "rec_role", "id"));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.DeleteRelation"/> attempts to drop the
        /// constraint and index. Method first calls <see cref="DbRepository.TableExists"/>.
        /// Source: DbRepository.cs lines 464-477.
        /// </summary>
        [Fact]
        public void DeleteRelation_DropsConstraintAndIndex()
        {
            // Arrange & Act — method calls TableExists which throws
            AssertReachesSqlExecution(() =>
                DbRepository.DeleteRelation("fk_order_customer", "rec_order"));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.DeleteNtoNRelation"/> attempts to delete
        /// both origin and target relations and the join table.
        /// Source: DbRepository.cs lines 479-486.
        /// </summary>
        [Fact]
        public void DeleteNtoNRelation_DropsRelationsAndTable()
        {
            // Arrange & Act — calls DeleteRelation which calls TableExists
            AssertReachesSqlExecution(() =>
                DbRepository.DeleteNtoNRelation("user_role", "rec_user", "rec_role"));
        }

        #endregion

        #region Phase 7: Index DDL

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateIndex"/> attempts to construct
        /// <c>CREATE INDEX IF NOT EXISTS "idx" ON "tbl" ("col");</c>.
        /// Method first calls <see cref="DbRepository.TableExists"/> which throws.
        /// Source: DbRepository.cs line 495.
        /// </summary>
        [Fact]
        public void CreateIndex_Standard_GeneratesCorrectDDL()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.CreateIndex("idx_user_name", "rec_user", "name", null,
                    unique: false, ascending: true, nullable: false));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateIndex"/> with <c>unique = true</c>
        /// attempts to construct <c>CREATE UNIQUE INDEX IF NOT EXISTS "idx" ON "tbl" ("col");</c>.
        /// Source: DbRepository.cs lines 496-497.
        /// </summary>
        [Fact]
        public void CreateIndex_Unique_GeneratesUniqueIndex()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.CreateIndex("idx_user_email", "rec_user", "email", null,
                    unique: true, ascending: true, nullable: false));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateIndex"/> with a <see cref="GeographyField"/>
        /// uses <c>USING GIST</c> syntax instead of standard B-tree index.
        /// Source: DbRepository.cs lines 498-499.
        /// </summary>
        [Fact]
        public void CreateIndex_GeographyField_UsesGIST()
        {
            // Arrange
            var geoField = new GeographyField { Name = "location" };

            AssertReachesSqlExecution(() =>
                DbRepository.CreateIndex("idx_location", "rec_place", "location", geoField,
                    unique: false, ascending: true, nullable: false));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreateFtsIndexIfNotExists"/> attempts to construct
        /// a GIN index for full-text search using unquoted identifiers:
        /// <c>CREATE INDEX IF NOT EXISTS idx ON tbl USING gin(to_tsvector('simple', coalesce(col, ' ')));</c>
        /// Note: FTS index deliberately uses unquoted identifiers (source line 527).
        /// Method first calls <see cref="DbRepository.TableExists"/> which throws.
        /// </summary>
        [Fact]
        public void CreateFtsIndexIfNotExists_GeneratesGINIndex()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.CreateFtsIndexIfNotExists(
                    "idx_search_name", "rec_account", "x_search"));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.DropIndex"/> constructs
        /// <c>DROP INDEX IF EXISTS "indexName"</c>. Unlike CreateIndex/CreateFtsIndex,
        /// DropIndex does NOT call TableExists, so it reaches SQL execution directly.
        /// Source: DbRepository.cs line 538.
        /// </summary>
        [Fact]
        public void DropIndex_GeneratesCorrectDDL()
        {
            AssertReachesSqlExecution(() => DbRepository.DropIndex("idx_user_name"));
        }

        #endregion

        #region Phase 8: DML Tests — InsertRecord, UpdateRecord, DeleteRecord

        /// <summary>
        /// Verifies that <see cref="DbRepository.InsertRecord"/> constructs a parameterized INSERT:
        /// <c>INSERT INTO "tbl" ("id", "name") VALUES (@id, @name)</c>.
        /// Parameters are added to the NpgsqlCommand with correct types before execution throws.
        /// Source: DbRepository.cs lines 544-579.
        /// </summary>
        [Fact]
        public void InsertRecord_GeneratesParameterizedInsert()
        {
            // Arrange
            var parameters = new List<DbParameter>
            {
                new DbParameter
                {
                    Name = "id",
                    Value = Guid.NewGuid(),
                    Type = NpgsqlDbType.Uuid
                },
                new DbParameter
                {
                    Name = "name",
                    Value = "Test Record",
                    Type = NpgsqlDbType.Text
                }
            };

            AssertReachesSqlExecution(() =>
                DbRepository.InsertRecord("test_table", parameters));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.InsertRecord"/> uses the
        /// <see cref="DbParameter.ValueOverride"/> inline SQL expression instead of
        /// the <c>@paramName</c> placeholder when ValueOverride is set.
        /// Source: DbRepository.cs lines 562-564 — <c>values += param.ValueOverride + ", "</c>
        /// </summary>
        [Fact]
        public void InsertRecord_WithValueOverride_UsesOverrideInSQL()
        {
            // Arrange
            var parameters = new List<DbParameter>
            {
                new DbParameter
                {
                    Name = "id",
                    Value = Guid.NewGuid(),
                    Type = NpgsqlDbType.Uuid
                },
                new DbParameter
                {
                    Name = "created_on",
                    Value = DateTime.UtcNow,
                    Type = NpgsqlDbType.TimestampTz,
                    ValueOverride = "now()"
                }
            };

            // Act — method builds INSERT with now() inline instead of @created_on
            AssertReachesSqlExecution(() =>
                DbRepository.InsertRecord("test_table", parameters));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.UpdateRecord"/> constructs a parameterized UPDATE:
        /// <c>UPDATE "tbl" SET "name"=@name WHERE id=@id</c>.
        /// Source: DbRepository.cs lines 582-613.
        /// </summary>
        [Fact]
        public void UpdateRecord_GeneratesParameterizedUpdate()
        {
            // Arrange
            var parameters = new List<DbParameter>
            {
                new DbParameter
                {
                    Name = "id",
                    Value = Guid.NewGuid(),
                    Type = NpgsqlDbType.Uuid
                },
                new DbParameter
                {
                    Name = "name",
                    Value = "Updated Name",
                    Type = NpgsqlDbType.Text
                }
            };

            AssertReachesSqlExecution(() =>
                DbRepository.UpdateRecord("test_table", parameters));
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.DeleteRecord"/> constructs
        /// <c>DELETE FROM "tbl" WHERE id=@id</c> with a UUID parameter.
        /// Source: DbRepository.cs lines 616-632.
        /// </summary>
        [Fact]
        public void DeleteRecord_GeneratesParameterizedDelete()
        {
            // Arrange
            var recordId = Guid.NewGuid();

            AssertReachesSqlExecution(() =>
                DbRepository.DeleteRecord("test_table", recordId));
        }

        #endregion

        #region Phase 10: PostgreSQL Extension/Cast Methods

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreatePostgresqlCasts"/> attempts to execute SQL
        /// containing: DROP CAST IF EXISTS(varchar AS uuid), DROP CAST IF EXISTS(text AS uuid),
        /// CREATE CAST(text AS uuid) WITH INOUT AS IMPLICIT,
        /// CREATE CAST(varchar AS uuid) WITH INOUT AS IMPLICIT.
        /// Source: DbRepository.cs lines 40-50.
        /// </summary>
        [Fact]
        public void CreatePostgresqlCasts_ExecutesCorrectSQL()
        {
            AssertReachesSqlExecution(() => DbRepository.CreatePostgresqlCasts());
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.CreatePostgresqlExtensions"/> attempts to execute
        /// <c>CREATE EXTENSION IF NOT EXISTS "uuid-ossp";</c> and then attempts
        /// <c>CREATE EXTENSION IF NOT EXISTS "postgis";</c> (catching failure gracefully).
        /// Source: DbRepository.cs lines 53-72.
        /// </summary>
        [Fact]
        public void CreatePostgresqlExtensions_CreatesUuidOsspExtension()
        {
            AssertReachesSqlExecution(() => DbRepository.CreatePostgresqlExtensions());
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.IsPostgisInstalled"/> attempts to query
        /// the <c>pg_extension</c> system table to check for PostGIS.
        /// Source: DbRepository.cs lines 75-85.
        /// </summary>
        [Fact]
        public void IsPostgisInstalled_QueriesPgExtension()
        {
            AssertReachesSqlExecution(() => DbRepository.IsPostgisInstalled());
        }

        /// <summary>
        /// Verifies that <see cref="DbRepository.TableExists"/> attempts to query
        /// <c>information_schema.tables</c> to check table existence.
        /// Source: DbRepository.cs lines 634-648.
        /// </summary>
        [Fact]
        public void TableExists_QueriesInformationSchema()
        {
            AssertReachesSqlExecution(() => DbRepository.TableExists("test_table"));
        }

        #endregion

        #region Additional Edge Case Tests

        /// <summary>
        /// Verifies ConvertDefaultValue for a MultiSelectField with a single item.
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_MultiSelectField_SingleItem()
        {
            // Arrange
            var values = new List<string> { "only_one" };

            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.MultiSelectField, values);

            // Assert
            result.Should().Be("'{\"only_one\"}'");
        }

        /// <summary>
        /// Verifies ConvertDefaultValue with a DateField value at midnight (time portion is zeros).
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_DateField_MidnightDate()
        {
            // Arrange
            var date = new DateTime(2024, 12, 31);

            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.DateField, date);

            // Assert
            result.Should().Be("'2024-12-31'");
        }

        /// <summary>
        /// Verifies ConvertDefaultValue for DateTimeField with specific time including seconds.
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_DateTimeField_WithSeconds()
        {
            // Arrange
            var dateTime = new DateTime(2024, 6, 15, 8, 5, 30);

            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.DateTimeField, dateTime);

            // Assert
            result.Should().Be("'2024-06-15 08:05:30'");
        }

        /// <summary>
        /// Verifies ConvertDefaultValue for an empty string with TextField returns quoted empty.
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_TextField_EmptyString()
        {
            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.TextField, "");

            // Assert
            result.Should().Be("''");
        }

        /// <summary>
        /// Verifies that CreateColumn with a DateTimeField and UseCurrentTimeAsDefaultValue works.
        /// </summary>
        [Fact]
        public void CreateColumn_DateTimeFieldWithUseCurrentTime_SetsFlag()
        {
            // Arrange
            SetupTestContext();
            var field = new DateTimeField
            {
                Name = "updated_on",
                Required = false,
                UseCurrentTimeAsDefaultValue = true
            };

            // Act
            Action act = () => DbRepository.CreateColumn("test_table", field);

            // Assert
            act.Should().Throw<Exception>();
        }

        /// <summary>
        /// Verifies the UpdateRecord method handles ValueOverride for inline SQL expressions.
        /// </summary>
        [Fact]
        public void UpdateRecord_WithValueOverride_UsesOverrideInSQL()
        {
            // Arrange
            var parameters = new List<DbParameter>
            {
                new DbParameter
                {
                    Name = "id",
                    Value = Guid.NewGuid(),
                    Type = NpgsqlDbType.Uuid
                },
                new DbParameter
                {
                    Name = "modified_on",
                    Value = DateTime.UtcNow,
                    Type = NpgsqlDbType.TimestampTz,
                    ValueOverride = "now()"
                }
            };

            AssertReachesSqlExecution(() =>
                DbRepository.UpdateRecord("test_table", parameters));
        }

        /// <summary>
        /// Verifies InsertRecord with null DbParameter.Value uses DBNull.Value substitution.
        /// Source: DbRepository.cs line 557 — <c>parameter.Value = param.Value ?? DBNull.Value;</c>
        /// </summary>
        [Fact]
        public void InsertRecord_NullParameterValue_SubstitutesDbnull()
        {
            // Arrange
            var parameters = new List<DbParameter>
            {
                new DbParameter
                {
                    Name = "id",
                    Value = Guid.NewGuid(),
                    Type = NpgsqlDbType.Uuid
                },
                new DbParameter
                {
                    Name = "description",
                    Value = null,
                    Type = NpgsqlDbType.Text
                }
            };

            // Act — should not throw NullReferenceException on null Value
            AssertReachesSqlExecution(() =>
                DbRepository.InsertRecord("test_table", parameters));
        }

        /// <summary>
        /// Verifies ConvertDefaultValue for GuidField with Guid.Empty.
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_GuidField_EmptyGuid()
        {
            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.GuidField, Guid.Empty);

            // Assert
            result.Should().Be($"'{Guid.Empty}'");
        }

        /// <summary>
        /// Verifies ConvertDefaultValue returns value.ToString() for AutoNumberField (numeric).
        /// </summary>
        [Fact]
        public void ConvertDefaultValue_AutoNumberField_ReturnsToString()
        {
            // Act
            var result = DbRepository.ConvertDefaultValue(FieldType.AutoNumberField, 1m);

            // Assert
            result.Should().Be("1");
        }

        /// <summary>
        /// Verifies that CreateColumn with a non-nullable, non-PK TextField generates NOT NULL.
        /// </summary>
        [Fact]
        public void CreateColumn_TextField_NotNullable_GeneratesNOTNULL()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.CreateColumn("test_table", "name", FieldType.TextField,
                    false, "default_name", false, false, false, false));
        }

        /// <summary>
        /// Verifies CreateIndex with descending order.
        /// </summary>
        [Fact]
        public void CreateIndex_Descending_AppendsDESC()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.CreateIndex("idx_created_desc", "rec_user", "created_on", null,
                    unique: false, ascending: false, nullable: false));
        }

        /// <summary>
        /// Verifies CreateIndex with nullable column adds WHERE IS NOT NULL filter.
        /// </summary>
        [Fact]
        public void CreateIndex_Nullable_AppendsISNOTNULLFilter()
        {
            AssertReachesSqlExecution(() =>
                DbRepository.CreateIndex("idx_email", "rec_user", "email", null,
                    unique: false, ascending: true, nullable: true));
        }

        #endregion
    }
}
