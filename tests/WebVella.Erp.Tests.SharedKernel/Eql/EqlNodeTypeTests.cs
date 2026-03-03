using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.Tests.SharedKernel.Eql
{
    /// <summary>
    /// Unit tests for the <see cref="EqlNodeType"/> enum.
    /// Validates that all 16 enum values are present, have correct ordinal positions,
    /// parse correctly from strings and integers, and that the total count is preserved
    /// after migration from the monolith (<c>WebVella.Erp.Eql.EqlNodeType</c>) to the
    /// shared kernel (<c>WebVella.Erp.SharedKernel.Eql.EqlNodeType</c>).
    /// </summary>
    public class EqlNodeTypeTests
    {
        // ────────────────────────────────────────────────────────────────────
        // Phase 1: Enum Value Existence Tests
        // Each [Fact] verifies that the named enum member is defined.
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public void Test_Keyword_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.Keyword).Should().BeTrue(
                "EqlNodeType.Keyword must be defined in the enum");
        }

        [Fact]
        public void Test_NumberValue_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.NumberValue).Should().BeTrue(
                "EqlNodeType.NumberValue must be defined in the enum");
        }

        [Fact]
        public void Test_TextValue_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.TextValue).Should().BeTrue(
                "EqlNodeType.TextValue must be defined in the enum");
        }

        [Fact]
        public void Test_ArgumentValue_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.ArgumentValue).Should().BeTrue(
                "EqlNodeType.ArgumentValue must be defined in the enum");
        }

        [Fact]
        public void Test_Select_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.Select).Should().BeTrue(
                "EqlNodeType.Select must be defined in the enum");
        }

        [Fact]
        public void Test_Field_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.Field).Should().BeTrue(
                "EqlNodeType.Field must be defined in the enum");
        }

        [Fact]
        public void Test_RelationField_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.RelationField).Should().BeTrue(
                "EqlNodeType.RelationField must be defined in the enum");
        }

        [Fact]
        public void Test_RelationWildcardField_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.RelationWildcardField).Should().BeTrue(
                "EqlNodeType.RelationWildcardField must be defined in the enum");
        }

        [Fact]
        public void Test_WildcardField_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.WildcardField).Should().BeTrue(
                "EqlNodeType.WildcardField must be defined in the enum");
        }

        [Fact]
        public void Test_From_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.From).Should().BeTrue(
                "EqlNodeType.From must be defined in the enum");
        }

        [Fact]
        public void Test_Where_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.Where).Should().BeTrue(
                "EqlNodeType.Where must be defined in the enum");
        }

        [Fact]
        public void Test_BinaryExpression_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.BinaryExpression).Should().BeTrue(
                "EqlNodeType.BinaryExpression must be defined in the enum");
        }

        [Fact]
        public void Test_OrderBy_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.OrderBy).Should().BeTrue(
                "EqlNodeType.OrderBy must be defined in the enum");
        }

        [Fact]
        public void Test_OrderByField_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.OrderByField).Should().BeTrue(
                "EqlNodeType.OrderByField must be defined in the enum");
        }

        [Fact]
        public void Test_Page_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.Page).Should().BeTrue(
                "EqlNodeType.Page must be defined in the enum");
        }

        [Fact]
        public void Test_PageSize_Exists()
        {
            Enum.IsDefined(typeof(EqlNodeType), EqlNodeType.PageSize).Should().BeTrue(
                "EqlNodeType.PageSize must be defined in the enum");
        }

        // ────────────────────────────────────────────────────────────────────
        // Phase 2: Enum Value Ordinal Tests (parameterized)
        // Validates that each enum member has the expected auto-incremented
        // integer value (Keyword=0 through PageSize=15).
        // ────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Keyword", 0)]
        [InlineData("NumberValue", 1)]
        [InlineData("TextValue", 2)]
        [InlineData("ArgumentValue", 3)]
        [InlineData("Select", 4)]
        [InlineData("Field", 5)]
        [InlineData("RelationField", 6)]
        [InlineData("RelationWildcardField", 7)]
        [InlineData("WildcardField", 8)]
        [InlineData("From", 9)]
        [InlineData("Where", 10)]
        [InlineData("BinaryExpression", 11)]
        [InlineData("OrderBy", 12)]
        [InlineData("OrderByField", 13)]
        [InlineData("Page", 14)]
        [InlineData("PageSize", 15)]
        public void Test_EnumValue_HasCorrectOrdinal(string name, int expectedOrdinal)
        {
            // Parse the enum member by name
            var parsed = Enum.Parse<EqlNodeType>(name);

            // Cast to int and verify the ordinal position
            ((int)parsed).Should().Be(expectedOrdinal,
                $"EqlNodeType.{name} must have ordinal value {expectedOrdinal}");
        }

        // ────────────────────────────────────────────────────────────────────
        // Phase 3: Enum Count Test
        // Ensures no values were added to or removed from the enum during
        // migration. The monolith defines exactly 16 members.
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public void Test_TotalEnumValues_Is16()
        {
            var values = Enum.GetValues(typeof(EqlNodeType))
                .Cast<EqlNodeType>()
                .ToList();

            values.Should().HaveCount(16,
                "EqlNodeType must contain exactly 16 members matching the original monolith definition");
        }

        // ────────────────────────────────────────────────────────────────────
        // Phase 4: Enum Parse & Casting Tests
        // Validates round-trip parsing from string, casting from int, and
        // boundary checks via Enum.IsDefined for all valid ordinals.
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public void Test_ParseFromString()
        {
            // Parse from string name should yield the correct enum value
            var result = Enum.Parse<EqlNodeType>("Select");

            result.Should().Be(EqlNodeType.Select,
                "parsing the string 'Select' must return EqlNodeType.Select");
        }

        [Fact]
        public void Test_ParseFromInt()
        {
            // Casting integer 4 should yield EqlNodeType.Select
            var result = (EqlNodeType)4;

            result.Should().Be(EqlNodeType.Select,
                "casting integer 4 to EqlNodeType must return EqlNodeType.Select");
        }

        [Fact]
        public void Test_IsDefined_AllValidValues()
        {
            // Every integer from 0 through 15 must map to a defined enum member
            for (int i = 0; i <= 15; i++)
            {
                Enum.IsDefined(typeof(EqlNodeType), i).Should().BeTrue(
                    $"integer {i} must be a defined value in EqlNodeType");
            }

            // Integer 16 (one past the last value) must NOT be defined
            Enum.IsDefined(typeof(EqlNodeType), 16).Should().BeFalse(
                "integer 16 must not be a defined value in EqlNodeType — only 0..15 are valid");

            // Negative value must NOT be defined
            Enum.IsDefined(typeof(EqlNodeType), -1).Should().BeFalse(
                "integer -1 must not be a defined value in EqlNodeType");
        }
    }
}
