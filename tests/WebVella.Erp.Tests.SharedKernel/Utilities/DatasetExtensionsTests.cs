using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Utilities;
using Xunit;

namespace WebVella.Erp.Tests.SharedKernel.Utilities
{
    /// <summary>
    /// Comprehensive unit tests for the <see cref="DatasetExtensions"/> class
    /// (declared in the <c>System.Data</c> namespace) which provides extension methods
    /// bridging ADO.NET <see cref="DataTable"/>/<see cref="DataRow"/> types to
    /// <see cref="EntityRecord"/> collections used throughout the ERP system.
    ///
    /// Three extension methods are tested:
    /// <list type="bullet">
    ///   <item><c>DataTable.AsRecordList()</c> — converts rows to <c>List&lt;EntityRecord&gt;</c> with UTC DateTime normalization</item>
    ///   <item><c>List&lt;EntityRecord&gt;.AsRecordDictionary()</c> — keys records by their <c>id</c> Guid</item>
    ///   <item><c>DataRow.DataRowToHash()</c> — computes an MD5 hash of concatenated column name/value pairs</item>
    /// </list>
    /// </summary>
    public class DatasetExtensionsTests
    {
        #region Helper Methods

        /// <summary>
        /// Creates a <see cref="DataTable"/> with the specified column definitions.
        /// Each tuple contains (columnName, columnType).
        /// </summary>
        /// <param name="columns">Array of tuples defining column name and .NET type.</param>
        /// <returns>A new <see cref="DataTable"/> with the requested schema and no rows.</returns>
        private static DataTable CreateTable(params (string name, Type type)[] columns)
        {
            var table = new DataTable("TestTable");
            foreach (var (name, type) in columns)
            {
                table.Columns.Add(name, type);
            }
            return table;
        }

        /// <summary>
        /// Creates an <see cref="EntityRecord"/> pre-populated with the specified key-value pairs.
        /// </summary>
        /// <param name="fields">Array of (key, value) tuples to set on the record.</param>
        /// <returns>A new <see cref="EntityRecord"/> instance with all fields set.</returns>
        private static EntityRecord CreateRecord(params (string key, object value)[] fields)
        {
            var record = new EntityRecord();
            foreach (var (key, value) in fields)
            {
                record[key] = value;
            }
            return record;
        }

        #endregion

        #region AsRecordList Tests

        /// <summary>
        /// Verifies that calling <c>AsRecordList()</c> on a DataTable that has column
        /// definitions but zero rows returns a non-null empty list.
        /// </summary>
        [Fact]
        public void AsRecordList_EmptyTable_ReturnsEmptyList()
        {
            // Arrange
            var table = CreateTable(("name", typeof(string)), ("age", typeof(int)));

            // Act
            var result = table.AsRecordList();

            // Assert
            result.Should().NotBeNull();
            result.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that a single-row DataTable with string and int columns produces
        /// exactly one <see cref="EntityRecord"/> with the correct keys and values.
        /// </summary>
        [Fact]
        public void AsRecordList_SingleRow_ReturnsOneEntityRecord()
        {
            // Arrange
            var table = CreateTable(("name", typeof(string)), ("age", typeof(int)));
            table.Rows.Add("Alice", 30);

            // Act
            var result = table.AsRecordList();

            // Assert
            result.Should().HaveCount(1);
            result[0]["name"].Should().Be("Alice");
            result[0]["age"].Should().Be(30);
        }

        /// <summary>
        /// Verifies that a DataTable with 3 rows produces a list of exactly 3 EntityRecords.
        /// </summary>
        [Fact]
        public void AsRecordList_MultipleRows_ReturnsCorrectCount()
        {
            // Arrange
            var table = CreateTable(("name", typeof(string)), ("value", typeof(int)));
            table.Rows.Add("Row1", 1);
            table.Rows.Add("Row2", 2);
            table.Rows.Add("Row3", 3);

            // Act
            var result = table.AsRecordList();

            // Assert
            result.Should().HaveCount(3);
        }

        /// <summary>
        /// Verifies that every column name from the DataTable schema appears as a key
        /// in the resulting <see cref="EntityRecord"/>'s Properties dictionary.
        /// </summary>
        [Fact]
        public void AsRecordList_ColumnNames_BecomeEntityRecordKeys()
        {
            // Arrange
            var table = CreateTable(
                ("first_name", typeof(string)),
                ("last_name", typeof(string)),
                ("email", typeof(string))
            );
            table.Rows.Add("John", "Doe", "john@example.com");

            // Act
            var result = table.AsRecordList();

            // Assert — verify each column name exists as a key in the EntityRecord properties
            var record = result[0];
            record.Properties.ContainsKey("first_name").Should().BeTrue();
            record.Properties.ContainsKey("last_name").Should().BeTrue();
            record.Properties.ContainsKey("email").Should().BeTrue();
        }

        /// <summary>
        /// Verifies that a <see cref="DateTime"/> value with <see cref="DateTimeKind.Utc"/>
        /// is preserved as-is (no conversion applied) and remains UTC.
        /// The source code stores DateTime columns as <c>DateTime?</c>.
        /// </summary>
        [Fact]
        public void AsRecordList_DateTimeUtc_PreservedAsIs()
        {
            // Arrange
            var utcDate = DateTime.SpecifyKind(new DateTime(2024, 6, 15, 10, 30, 0), DateTimeKind.Utc);
            var table = CreateTable(("created_on", typeof(DateTime)));
            table.Rows.Add(utcDate);

            // Act
            var result = table.AsRecordList();

            // Assert — the stored value is DateTime? with UTC kind and identical ticks
            var storedValue = result[0]["created_on"];
            storedValue.Should().NotBeNull();
            storedValue.Should().BeOfType<DateTime>();

            var storedDate = (DateTime)storedValue;
            storedDate.Kind.Should().Be(DateTimeKind.Utc);
            storedDate.Should().Be(utcDate);
        }

        /// <summary>
        /// Verifies that a <see cref="DateTime"/> value with <see cref="DateTimeKind.Local"/>
        /// is converted to UTC via <c>ToUniversalTime()</c>.
        /// The resulting value should have <c>DateTimeKind.Utc</c> and match the expected
        /// UTC equivalent of the local time.
        /// </summary>
        [Fact]
        public void AsRecordList_DateTimeLocal_ConvertedToUtc()
        {
            // Arrange
            var localDate = DateTime.SpecifyKind(new DateTime(2024, 6, 15, 10, 30, 0), DateTimeKind.Local);
            var expectedUtc = localDate.ToUniversalTime();
            var table = CreateTable(("created_on", typeof(DateTime)));
            table.Rows.Add(localDate);

            // Act
            var result = table.AsRecordList();

            // Assert — value should be converted to UTC
            var storedValue = result[0]["created_on"];
            storedValue.Should().NotBeNull();
            storedValue.Should().BeOfType<DateTime>();

            var storedDate = (DateTime)storedValue;
            storedDate.Kind.Should().Be(DateTimeKind.Utc);
            storedDate.Should().Be(expectedUtc);
        }

        /// <summary>
        /// Verifies that a <see cref="DateTime"/> value with <see cref="DateTimeKind.Unspecified"/>
        /// is converted to UTC via <c>ToUniversalTime()</c>.
        /// .NET treats Unspecified as Local when calling <c>ToUniversalTime()</c>.
        /// </summary>
        [Fact]
        public void AsRecordList_DateTimeUnspecified_ConvertedToUtc()
        {
            // Arrange
            var unspecifiedDate = DateTime.SpecifyKind(new DateTime(2024, 6, 15, 10, 30, 0), DateTimeKind.Unspecified);
            // .NET treats Unspecified as Local for ToUniversalTime()
            var expectedUtc = unspecifiedDate.ToUniversalTime();
            var table = CreateTable(("created_on", typeof(DateTime)));
            table.Rows.Add(unspecifiedDate);

            // Act
            var result = table.AsRecordList();

            // Assert — value should be converted to UTC
            var storedValue = result[0]["created_on"];
            storedValue.Should().NotBeNull();
            storedValue.Should().BeOfType<DateTime>();

            var storedDate = (DateTime)storedValue;
            storedDate.Kind.Should().Be(DateTimeKind.Utc);
            storedDate.Should().Be(expectedUtc);
        }

        /// <summary>
        /// Verifies that <c>DBNull.Value</c> in a nullable column is preserved as-is
        /// in the EntityRecord (the else branch of the DateTime check stores raw values).
        /// </summary>
        [Fact]
        public void AsRecordList_NullValues_PreservedInEntityRecord()
        {
            // Arrange — build a table allowing DBNull in the string column
            var table = CreateTable(("name", typeof(string)), ("value", typeof(int)));
            table.Columns["name"]!.AllowDBNull = true;
            var row = table.NewRow();
            row["name"] = DBNull.Value;
            row["value"] = 42;
            table.Rows.Add(row);

            // Act
            var result = table.AsRecordList();

            // Assert — DBNull.Value is preserved for the null column
            result.Should().HaveCount(1);
            result[0]["name"].Should().Be(DBNull.Value);
            result[0]["value"].Should().Be(42);
        }

        /// <summary>
        /// Verifies that columns of different types (int, string, Guid, decimal, bool)
        /// all have their values correctly preserved in the resulting EntityRecord.
        /// </summary>
        [Fact]
        public void AsRecordList_MixedColumnTypes_AllPreserved()
        {
            // Arrange
            var table = CreateTable(
                ("id", typeof(Guid)),
                ("name", typeof(string)),
                ("count", typeof(int)),
                ("price", typeof(decimal)),
                ("active", typeof(bool))
            );
            var testGuid = Guid.NewGuid();
            table.Rows.Add(testGuid, "Widget", 100, 29.99m, true);

            // Act
            var result = table.AsRecordList();

            // Assert — all types should be preserved through the else branch
            var record = result[0];
            record["id"].Should().Be(testGuid);
            record["name"].Should().Be("Widget");
            record["count"].Should().Be(100);
            record["price"].Should().Be(29.99m);
            record["active"].Should().Be(true);
        }

        #endregion

        #region AsRecordDictionary Tests

        /// <summary>
        /// Verifies that an empty <c>List&lt;EntityRecord&gt;</c> produces
        /// an empty dictionary.
        /// </summary>
        [Fact]
        public void AsRecordDictionary_EmptyList_ReturnsEmptyDictionary()
        {
            // Arrange
            var list = new List<EntityRecord>();

            // Act
            var result = list.AsRecordDictionary();

            // Assert
            result.Should().NotBeNull();
            result.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that a list with a single EntityRecord containing an "id" Guid
        /// produces a dictionary with exactly one entry keyed by that Guid.
        /// </summary>
        [Fact]
        public void AsRecordDictionary_SingleRecord_ReturnsDictionaryWithOneEntry()
        {
            // Arrange
            var id = Guid.NewGuid();
            var list = new List<EntityRecord>
            {
                CreateRecord(("id", id), ("name", "Test"))
            };

            // Act
            var result = list.AsRecordDictionary();

            // Assert
            result.Should().HaveCount(1);
            result.ContainsKey(id).Should().BeTrue();
            result[id]["name"].Should().Be("Test");
        }

        /// <summary>
        /// Verifies that 3 records with unique Guid "id" fields produce a dictionary
        /// with 3 entries, each accessible by its respective Guid key.
        /// </summary>
        [Fact]
        public void AsRecordDictionary_MultipleRecords_AllKeyed()
        {
            // Arrange
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var id3 = Guid.NewGuid();
            var list = new List<EntityRecord>
            {
                CreateRecord(("id", id1), ("name", "First")),
                CreateRecord(("id", id2), ("name", "Second")),
                CreateRecord(("id", id3), ("name", "Third"))
            };

            // Act
            var result = list.AsRecordDictionary();

            // Assert
            result.Should().HaveCount(3);
            result.ContainsKey(id1).Should().BeTrue();
            result.ContainsKey(id2).Should().BeTrue();
            result.ContainsKey(id3).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that two records sharing the same "id" Guid cause an
        /// <see cref="ArgumentException"/> to be thrown by the underlying
        /// <c>Dictionary.Add</c> call.
        /// </summary>
        [Fact]
        public void AsRecordDictionary_DuplicateIds_ThrowsArgumentException()
        {
            // Arrange
            var sharedId = Guid.NewGuid();
            var list = new List<EntityRecord>
            {
                CreateRecord(("id", sharedId), ("name", "First")),
                CreateRecord(("id", sharedId), ("name", "Duplicate"))
            };

            // Act
            Action act = () => list.AsRecordDictionary();

            // Assert — Dictionary.Add throws ArgumentException for duplicate keys
            act.Should().Throw<ArgumentException>();
        }

        /// <summary>
        /// Verifies that an EntityRecord missing the "id" key causes an exception
        /// when <c>AsRecordDictionary</c> attempts to access <c>rec["id"]</c>.
        /// The Expando indexer throws <see cref="KeyNotFoundException"/> for missing keys.
        /// </summary>
        [Fact]
        public void AsRecordDictionary_RecordWithoutIdField_ThrowsException()
        {
            // Arrange — record has "name" but no "id" field
            var list = new List<EntityRecord>
            {
                CreateRecord(("name", "NoId"))
            };

            // Act
            Action act = () => list.AsRecordDictionary();

            // Assert — accessing non-existent "id" key in Expando's PropertyBag
            // throws KeyNotFoundException, which is a subclass of Exception
            act.Should().Throw<Exception>();
        }

        /// <summary>
        /// Verifies that the dictionary key type is <see cref="Guid"/> and matches
        /// the value stored in the EntityRecord's "id" field.
        /// </summary>
        [Fact]
        public void AsRecordDictionary_IdIsCorrectType_Guid()
        {
            // Arrange
            var expectedId = Guid.NewGuid();
            var list = new List<EntityRecord>
            {
                CreateRecord(("id", expectedId), ("data", "value"))
            };

            // Act
            var result = list.AsRecordDictionary();

            // Assert — the dictionary should contain the exact Guid key
            result.Should().ContainKey(expectedId);
            // Verify the single key matches the expected Guid value
            result.Should().HaveCount(1);
            var actualKey = result.Keys.First();
            actualKey.Should().Be(expectedId);
        }

        #endregion

        #region DataRowToHash Tests

        /// <summary>
        /// Verifies that <c>DataRowToHash()</c> on a row with a single non-null column
        /// returns a non-null, non-empty hash string.
        /// </summary>
        [Fact]
        public void DataRowToHash_SingleColumn_ReturnsNonEmptyHash()
        {
            // Arrange
            var table = CreateTable(("value", typeof(string)));
            table.Rows.Add("test data");
            var row = table.Rows[0];

            // Act
            var hash = row.DataRowToHash();

            // Assert
            hash.Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Verifies that two rows with identical column values produce the same hash,
        /// confirming the hash computation is deterministic.
        /// </summary>
        [Fact]
        public void DataRowToHash_SameData_ProducesSameHash()
        {
            // Arrange — two rows with identical data in the same table
            var table = CreateTable(("name", typeof(string)), ("count", typeof(int)));
            table.Rows.Add("Alpha", 42);
            table.Rows.Add("Alpha", 42);

            // Act
            var hash1 = table.Rows[0].DataRowToHash();
            var hash2 = table.Rows[1].DataRowToHash();

            // Assert — identical input data must produce identical hashes
            hash1.Should().Be(hash2);
        }

        /// <summary>
        /// Verifies that two rows with different column values produce different hashes.
        /// </summary>
        [Fact]
        public void DataRowToHash_DifferentData_ProducesDifferentHash()
        {
            // Arrange — two rows with different data
            var table = CreateTable(("name", typeof(string)), ("count", typeof(int)));
            table.Rows.Add("Alpha", 42);
            table.Rows.Add("Beta", 99);

            // Act
            var hash1 = table.Rows[0].DataRowToHash();
            var hash2 = table.Rows[1].DataRowToHash();

            // Assert — different input data should produce different hashes
            hash1.Should().NotBe(hash2);
        }

        /// <summary>
        /// Verifies that when a column value is <see cref="DBNull.Value"/>, the hash
        /// computation uses the "NULL" placeholder string for that column.
        /// The source code concatenates <c>{columnName}NULL</c> for null/DBNull values.
        /// </summary>
        [Fact]
        public void DataRowToHash_NullValue_UsesNULLPlaceholder()
        {
            // Arrange — table with a single nullable column set to DBNull
            var table = CreateTable(("col", typeof(string)));
            table.Columns["col"]!.AllowDBNull = true;
            var row = table.NewRow();
            row["col"] = DBNull.Value;
            table.Rows.Add(row);

            // Act
            var hash = table.Rows[0].DataRowToHash();

            // Assert — the hash should match the expected computation
            // Source code: result.Append(column.ColumnName.ToString() + "NULL") → "colNULL"
            var expectedHash = CryptoUtility.ComputeMD5Hash("colNULL");
            hash.Should().NotBeNullOrEmpty();
            hash.Should().Be(expectedHash);
        }

        /// <summary>
        /// Verifies that the hash returned by <c>DataRowToHash()</c> matches the MD5 format
        /// produced by <see cref="CryptoUtility.ComputeMD5Hash"/>: <c>BitConverter.ToString</c>
        /// output with dash-separated uppercase hex pairs (e.g., "A1-B2-C3-D4-...").
        /// MD5 produces 16 bytes → 47 characters in this format.
        /// </summary>
        [Fact]
        public void DataRowToHash_ReturnsMD5Format()
        {
            // Arrange
            var table = CreateTable(("data", typeof(string)));
            table.Rows.Add("test");

            // Act
            var hash = table.Rows[0].DataRowToHash();

            // Assert — MD5 via BitConverter.ToString produces "XX-XX-XX-XX-XX-XX-XX-XX-XX-XX-XX-XX-XX-XX-XX-XX"
            // Pattern: 16 pairs of uppercase hex digits separated by dashes = 47 characters
            hash.Should().NotBeNullOrEmpty();
            hash.Should().MatchRegex(@"^[0-9A-F]{2}(-[0-9A-F]{2}){15}$");

            // Double-check: verify the hash matches what CryptoUtility.ComputeMD5Hash
            // would produce for the same concatenated input string.
            // Source code: result.Append(column.ColumnName.ToString() + row[column.ColumnName].ToString())
            // → "datatest"
            var expectedHash = CryptoUtility.ComputeMD5Hash("data" + "test");
            hash.Should().Be(expectedHash);
        }

        #endregion
    }
}
