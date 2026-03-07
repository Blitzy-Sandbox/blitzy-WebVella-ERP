using System;
using System.Collections.Generic;
using FluentAssertions;
using NpgsqlTypes;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Models;
using Xunit;

namespace WebVella.Erp.Tests.SharedKernel.Database
{
    /// <summary>
    /// Comprehensive unit tests for the <see cref="DbTypeConverter"/> static class, which maps
    /// ERP <see cref="FieldType"/> enum values to PostgreSQL SQL DDL type strings and
    /// <see cref="NpgsqlDbType"/> enum values. Tests cover all four public static methods:
    /// <list type="bullet">
    ///   <item><description><c>ConvertToDatabaseSqlType</c> — FieldType → SQL DDL string</description></item>
    ///   <item><description><c>ConvertToDatabaseType</c> — FieldType → NpgsqlDbType</description></item>
    ///   <item><description><c>GetDatabaseFieldType</c> — DbBaseField subclass → NpgsqlDbType</description></item>
    ///   <item><description><c>GetDatabaseType</c> — Field → NpgsqlDbType (delegates to ConvertToDatabaseType)</description></item>
    /// </list>
    /// Each method's default/unsupported branch is verified to throw an Exception
    /// with the exact message "FieldType is not supported.".
    /// </summary>
    public class DBTypeConverterTests
    {
        #region Phase 1: ConvertToDatabaseSqlType — FieldType → SQL DDL string mapping

        /// <summary>
        /// Verifies that <see cref="DbTypeConverter.ConvertToDatabaseSqlType"/> returns the correct
        /// PostgreSQL DDL type string for each of the 20 supported (non-Relation) FieldType enum values.
        /// Key distinctions verified:
        /// - TextField → "text" (NOT "varchar(200)")
        /// - SelectField → "varchar(200)" (NOT "text")
        /// - MultiSelectField → "text[]" (PostgreSQL text array)
        /// - GeographyField → "geography" (PostGIS spatial type)
        /// </summary>
        [Theory]
        [InlineData(FieldType.AutoNumberField, "serial")]
        [InlineData(FieldType.CheckboxField, "boolean")]
        [InlineData(FieldType.CurrencyField, "numeric")]
        [InlineData(FieldType.DateField, "date")]
        [InlineData(FieldType.DateTimeField, "timestamptz")]
        [InlineData(FieldType.EmailField, "varchar(500)")]
        [InlineData(FieldType.FileField, "varchar(1000)")]
        [InlineData(FieldType.GuidField, "uuid")]
        [InlineData(FieldType.HtmlField, "text")]
        [InlineData(FieldType.ImageField, "varchar(1000)")]
        [InlineData(FieldType.MultiLineTextField, "text")]
        [InlineData(FieldType.GeographyField, "geography")]
        [InlineData(FieldType.MultiSelectField, "text[]")]
        [InlineData(FieldType.NumberField, "numeric")]
        [InlineData(FieldType.PasswordField, "varchar(500)")]
        [InlineData(FieldType.PercentField, "numeric")]
        [InlineData(FieldType.PhoneField, "varchar(100)")]
        [InlineData(FieldType.SelectField, "varchar(200)")]
        [InlineData(FieldType.TextField, "text")]
        [InlineData(FieldType.UrlField, "varchar(1000)")]
        public void ConvertToDatabaseSqlType_ShouldReturnCorrectSqlType(FieldType type, string expectedSqlType)
        {
            // Act
            var result = DbTypeConverter.ConvertToDatabaseSqlType(type);

            // Assert
            result.Should().Be(expectedSqlType);
        }

        /// <summary>
        /// Verifies that <see cref="DbTypeConverter.ConvertToDatabaseSqlType"/> throws an
        /// <see cref="Exception"/> with the exact message "FieldType is not supported."
        /// when given an unsupported/invalid FieldType enum value (cast from integer 999).
        /// This exercises the default branch of the internal switch statement.
        /// </summary>
        [Fact]
        public void ConvertToDatabaseSqlType_UnsupportedFieldType_ShouldThrowException()
        {
            // Arrange
            var unsupportedType = (FieldType)999;

            // Act
            Action act = () => DbTypeConverter.ConvertToDatabaseSqlType(unsupportedType);

            // Assert
            act.Should().Throw<Exception>().WithMessage("FieldType is not supported.");
        }

        #endregion

        #region Phase 2: ConvertToDatabaseType — FieldType → NpgsqlDbType mapping

        /// <summary>
        /// Verifies that <see cref="DbTypeConverter.ConvertToDatabaseType"/> returns the correct
        /// <see cref="NpgsqlDbType"/> enum value for each of the 20 supported (non-Relation) FieldType values.
        /// Key distinctions verified:
        /// - MultiSelectField → NpgsqlDbType.Array | NpgsqlDbType.Text (bitwise OR for text array)
        /// - GeographyField → NpgsqlDbType.Geography (PostGIS type)
        /// - TextField → NpgsqlDbType.Text (matches DDL "text")
        /// </summary>
        [Theory]
        [InlineData(FieldType.AutoNumberField, NpgsqlDbType.Numeric)]
        [InlineData(FieldType.CheckboxField, NpgsqlDbType.Boolean)]
        [InlineData(FieldType.CurrencyField, NpgsqlDbType.Numeric)]
        [InlineData(FieldType.DateField, NpgsqlDbType.Date)]
        [InlineData(FieldType.DateTimeField, NpgsqlDbType.TimestampTz)]
        [InlineData(FieldType.EmailField, NpgsqlDbType.Varchar)]
        [InlineData(FieldType.FileField, NpgsqlDbType.Varchar)]
        [InlineData(FieldType.GuidField, NpgsqlDbType.Uuid)]
        [InlineData(FieldType.HtmlField, NpgsqlDbType.Text)]
        [InlineData(FieldType.ImageField, NpgsqlDbType.Varchar)]
        [InlineData(FieldType.MultiLineTextField, NpgsqlDbType.Text)]
        [InlineData(FieldType.GeographyField, NpgsqlDbType.Geography)]
        [InlineData(FieldType.MultiSelectField, NpgsqlDbType.Array | NpgsqlDbType.Text)]
        [InlineData(FieldType.NumberField, NpgsqlDbType.Numeric)]
        [InlineData(FieldType.PasswordField, NpgsqlDbType.Varchar)]
        [InlineData(FieldType.PercentField, NpgsqlDbType.Numeric)]
        [InlineData(FieldType.PhoneField, NpgsqlDbType.Varchar)]
        [InlineData(FieldType.SelectField, NpgsqlDbType.Varchar)]
        [InlineData(FieldType.TextField, NpgsqlDbType.Text)]
        [InlineData(FieldType.UrlField, NpgsqlDbType.Varchar)]
        public void ConvertToDatabaseType_ShouldReturnCorrectNpgsqlDbType(FieldType type, NpgsqlDbType expectedType)
        {
            // Act
            var result = DbTypeConverter.ConvertToDatabaseType(type);

            // Assert
            result.Should().Be(expectedType);
        }

        /// <summary>
        /// Verifies that <see cref="DbTypeConverter.ConvertToDatabaseType"/> throws an
        /// <see cref="Exception"/> with the exact message "FieldType is not supported."
        /// when given an unsupported/invalid FieldType enum value (cast from integer 999).
        /// This exercises the default branch of the internal switch statement.
        /// </summary>
        [Fact]
        public void ConvertToDatabaseType_UnsupportedFieldType_ShouldThrowException()
        {
            // Arrange
            var unsupportedType = (FieldType)999;

            // Act
            Action act = () => DbTypeConverter.ConvertToDatabaseType(unsupportedType);

            // Assert
            act.Should().Throw<Exception>().WithMessage("FieldType is not supported.");
        }

        #endregion

        #region Phase 3: GetDatabaseFieldType — DbBaseField subclass → NpgsqlDbType mapping

        /// <summary>
        /// Provides test data for <see cref="GetDatabaseFieldType_ShouldReturnCorrectNpgsqlDbType"/>.
        /// Each entry is a concrete <see cref="DbBaseField"/> subclass instance paired with its
        /// expected <see cref="NpgsqlDbType"/>. All 20 concrete Db*Field types are included.
        /// Notably, <see cref="DbTreeSelectField"/> maps to <c>NpgsqlDbType.Array | NpgsqlDbType.Uuid</c>,
        /// which is unique to this method and not present in the FieldType enum-based methods.
        /// </summary>
        public static IEnumerable<object[]> DbBaseFieldTestData()
        {
            yield return new object[] { new DbAutoNumberField(), NpgsqlDbType.Numeric };
            yield return new object[] { new DbCheckboxField(), NpgsqlDbType.Boolean };
            yield return new object[] { new DbCurrencyField(), NpgsqlDbType.Numeric };
            yield return new object[] { new DbDateField(), NpgsqlDbType.Date };
            yield return new object[] { new DbDateTimeField(), NpgsqlDbType.TimestampTz };
            yield return new object[] { new DbEmailField(), NpgsqlDbType.Varchar };
            yield return new object[] { new DbFileField(), NpgsqlDbType.Varchar };
            yield return new object[] { new DbGuidField(), NpgsqlDbType.Uuid };
            yield return new object[] { new DbHtmlField(), NpgsqlDbType.Text };
            yield return new object[] { new DbImageField(), NpgsqlDbType.Varchar };
            yield return new object[] { new DbMultiLineTextField(), NpgsqlDbType.Text };
            yield return new object[] { new DbMultiSelectField(), NpgsqlDbType.Array | NpgsqlDbType.Text };
            yield return new object[] { new DbNumberField(), NpgsqlDbType.Numeric };
            yield return new object[] { new DbPasswordField(), NpgsqlDbType.Varchar };
            yield return new object[] { new DbPercentField(), NpgsqlDbType.Numeric };
            yield return new object[] { new DbPhoneField(), NpgsqlDbType.Varchar };
            yield return new object[] { new DbSelectField(), NpgsqlDbType.Varchar };
            yield return new object[] { new DbTextField(), NpgsqlDbType.Text };
            yield return new object[] { new DbTreeSelectField(), NpgsqlDbType.Array | NpgsqlDbType.Uuid };
            yield return new object[] { new DbUrlField(), NpgsqlDbType.Varchar };
        }

        /// <summary>
        /// Verifies that <see cref="DbTypeConverter.GetDatabaseFieldType"/> returns the correct
        /// <see cref="NpgsqlDbType"/> for each concrete <see cref="DbBaseField"/> subclass using
        /// is-type discrimination. All 20 concrete Db*Field types are tested via MemberData.
        /// </summary>
        [Theory]
        [MemberData(nameof(DbBaseFieldTestData))]
        public void GetDatabaseFieldType_ShouldReturnCorrectNpgsqlDbType(DbBaseField field, NpgsqlDbType expectedType)
        {
            // Act
            var result = DbTypeConverter.GetDatabaseFieldType(field);

            // Assert
            result.Should().Be(expectedType);
        }

        /// <summary>
        /// Verifies that <see cref="DbTypeConverter.GetDatabaseFieldType"/> throws an
        /// <see cref="Exception"/> with the exact message "FieldType is not supported."
        /// when given an unrecognized <see cref="DbBaseField"/> subclass that doesn't match
        /// any of the is-type checks in the if/else chain.
        /// Uses a private test-only subclass (<see cref="UnsupportedDbField"/>) that inherits
        /// from DbBaseField but is not recognized by the converter.
        /// </summary>
        [Fact]
        public void GetDatabaseFieldType_UnsupportedType_ShouldThrowException()
        {
            // Arrange — UnsupportedDbField is a concrete DbBaseField subclass not in the converter
            var unsupportedField = new UnsupportedDbField();

            // Act
            Action act = () => DbTypeConverter.GetDatabaseFieldType(unsupportedField);

            // Assert
            act.Should().Throw<Exception>().WithMessage("FieldType is not supported.");
        }

        #endregion

        #region Phase 4: GetDatabaseType — Field → NpgsqlDbType delegation

        /// <summary>
        /// Verifies that <see cref="DbTypeConverter.GetDatabaseType(Field)"/> correctly delegates
        /// to <see cref="DbTypeConverter.ConvertToDatabaseType"/> by extracting the
        /// <see cref="FieldType"/> from the concrete <see cref="Field"/> instance via
        /// <see cref="Field.GetFieldType()"/> and returning the corresponding NpgsqlDbType.
        /// Three different concrete Field subclasses are tested to confirm consistent
        /// delegation behavior across different field types:
        /// - TextField → NpgsqlDbType.Text
        /// - AutoNumberField → NpgsqlDbType.Numeric
        /// - GuidField → NpgsqlDbType.Uuid
        /// </summary>
        [Fact]
        public void GetDatabaseType_WithField_ShouldDelegateToConvertToDatabaseType()
        {
            // Arrange — instantiate three different concrete Field subclasses
            var textField = new TextField();
            var autoNumberField = new AutoNumberField();
            var guidField = new GuidField();

            // Act — call GetDatabaseType with each Field instance
            var textResult = DbTypeConverter.GetDatabaseType(textField);
            var autoNumberResult = DbTypeConverter.GetDatabaseType(autoNumberField);
            var guidResult = DbTypeConverter.GetDatabaseType(guidField);

            // Assert — verify results match what ConvertToDatabaseType would return
            // for the corresponding FieldType values, confirming delegation behavior
            textResult.Should().Be(DbTypeConverter.ConvertToDatabaseType(FieldType.TextField));
            autoNumberResult.Should().Be(DbTypeConverter.ConvertToDatabaseType(FieldType.AutoNumberField));
            guidResult.Should().Be(DbTypeConverter.ConvertToDatabaseType(FieldType.GuidField));

            // Also verify the actual expected NpgsqlDbType values for completeness
            textResult.Should().Be(NpgsqlDbType.Text);
            autoNumberResult.Should().Be(NpgsqlDbType.Numeric);
            guidResult.Should().Be(NpgsqlDbType.Uuid);
        }

        #endregion

        #region Test Helpers

        /// <summary>
        /// A concrete <see cref="DbBaseField"/> subclass that is intentionally NOT recognized
        /// by <see cref="DbTypeConverter.GetDatabaseFieldType"/>. Used exclusively for testing
        /// the unsupported type exception path in the is-type discrimination chain.
        /// </summary>
        private class UnsupportedDbField : DbBaseField { }

        #endregion
    }
}
