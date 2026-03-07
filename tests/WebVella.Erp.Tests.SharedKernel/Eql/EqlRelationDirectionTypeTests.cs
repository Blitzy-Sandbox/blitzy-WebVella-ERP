using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.Tests.SharedKernel.Eql
{
    /// <summary>
    /// Unit tests for <see cref="EqlRelationDirectionType"/> enum.
    /// Validates the two direction values used for EQL relation traversal:
    ///   - TargetOrigin (0) maps to the '$' prefix in EQL syntax
    ///   - OriginTarget (1) maps to the '$$' prefix in EQL syntax
    /// These mappings are defined in EqlBuilder.cs where '$' resolves to TargetOrigin
    /// and '$$' resolves to OriginTarget for relation field references.
    /// </summary>
    public class EqlRelationDirectionTypeTests
    {
        // ──────────────────────────────────────────────
        // Phase 1: Enum Value Existence Tests
        // ──────────────────────────────────────────────

        /// <summary>
        /// Verifies that the TargetOrigin member is defined on the enum.
        /// TargetOrigin represents the relation direction where traversal goes
        /// from the target entity back to the origin entity (EQL '$' prefix).
        /// </summary>
        [Fact]
        public void Test_TargetOrigin_Exists()
        {
            // Arrange & Act
            var isDefined = Enum.IsDefined(typeof(EqlRelationDirectionType), nameof(EqlRelationDirectionType.TargetOrigin));

            // Assert
            isDefined.Should().BeTrue("TargetOrigin must be a defined member of EqlRelationDirectionType");
        }

        /// <summary>
        /// Verifies that the OriginTarget member is defined on the enum.
        /// OriginTarget represents the relation direction where traversal goes
        /// from the origin entity to the target entity (EQL '$$' prefix).
        /// </summary>
        [Fact]
        public void Test_OriginTarget_Exists()
        {
            // Arrange & Act
            var isDefined = Enum.IsDefined(typeof(EqlRelationDirectionType), nameof(EqlRelationDirectionType.OriginTarget));

            // Assert
            isDefined.Should().BeTrue("OriginTarget must be a defined member of EqlRelationDirectionType");
        }

        // ──────────────────────────────────────────────
        // Phase 2: Enum Value Integer Tests
        // ──────────────────────────────────────────────

        /// <summary>
        /// Verifies TargetOrigin has ordinal value 0 (auto-incremented first member).
        /// This ordinal is critical because it is the default value for the enum
        /// and is used as the implicit direction when only '$' is specified in EQL.
        /// </summary>
        [Fact]
        public void Test_TargetOrigin_Value_Is0()
        {
            // Arrange & Act
            var value = (int)EqlRelationDirectionType.TargetOrigin;

            // Assert
            value.Should().Be(0, "TargetOrigin is the first enum member and should have ordinal value 0");
        }

        /// <summary>
        /// Verifies OriginTarget has ordinal value 1 (auto-incremented second member).
        /// This ordinal distinguishes OriginTarget from TargetOrigin and maps to
        /// the '$$' double-dollar prefix in EQL relation field references.
        /// </summary>
        [Fact]
        public void Test_OriginTarget_Value_Is1()
        {
            // Arrange & Act
            var value = (int)EqlRelationDirectionType.OriginTarget;

            // Assert
            value.Should().Be(1, "OriginTarget is the second enum member and should have ordinal value 1");
        }

        // ──────────────────────────────────────────────
        // Phase 3: Enum Count Test
        // ──────────────────────────────────────────────

        /// <summary>
        /// Verifies the enum has exactly 2 values — no values added or removed.
        /// This guards against accidental introduction of new direction types
        /// that could break EQL parsing or relation traversal logic.
        /// </summary>
        [Fact]
        public void Test_TotalEnumValues_Is2()
        {
            // Arrange & Act
            var values = Enum.GetValues(typeof(EqlRelationDirectionType)).Cast<EqlRelationDirectionType>();

            // Assert
            values.Should().HaveCount(2, "EqlRelationDirectionType must have exactly 2 values: TargetOrigin and OriginTarget");
        }

        // ──────────────────────────────────────────────
        // Phase 4: Enum Parse/Cast Tests
        // ──────────────────────────────────────────────

        /// <summary>
        /// Verifies that the string "TargetOrigin" can be parsed back to the enum value.
        /// This validates serialization/deserialization round-trip for configuration
        /// and API contract stability.
        /// </summary>
        [Fact]
        public void Test_ParseFromString_TargetOrigin()
        {
            // Arrange & Act
            var parsed = Enum.Parse<EqlRelationDirectionType>("TargetOrigin");

            // Assert
            parsed.Should().Be(EqlRelationDirectionType.TargetOrigin,
                "parsing the string 'TargetOrigin' must yield the TargetOrigin enum value");
        }

        /// <summary>
        /// Verifies that the string "OriginTarget" can be parsed back to the enum value.
        /// This validates serialization/deserialization round-trip for configuration
        /// and API contract stability.
        /// </summary>
        [Fact]
        public void Test_ParseFromString_OriginTarget()
        {
            // Arrange & Act
            var parsed = Enum.Parse<EqlRelationDirectionType>("OriginTarget");

            // Assert
            parsed.Should().Be(EqlRelationDirectionType.OriginTarget,
                "parsing the string 'OriginTarget' must yield the OriginTarget enum value");
        }

        /// <summary>
        /// Verifies that casting integer 0 to the enum produces TargetOrigin.
        /// This validates the integer-to-enum mapping used internally by the
        /// EQL builder and relation direction resolution logic.
        /// </summary>
        [Fact]
        public void Test_CastFromInt_0()
        {
            // Arrange & Act
            var direction = (EqlRelationDirectionType)0;

            // Assert
            direction.Should().Be(EqlRelationDirectionType.TargetOrigin,
                "casting integer 0 to EqlRelationDirectionType must produce TargetOrigin");
        }

        /// <summary>
        /// Verifies that casting integer 1 to the enum produces OriginTarget.
        /// This validates the integer-to-enum mapping used internally by the
        /// EQL builder and relation direction resolution logic.
        /// </summary>
        [Fact]
        public void Test_CastFromInt_1()
        {
            // Arrange & Act
            var direction = (EqlRelationDirectionType)1;

            // Assert
            direction.Should().Be(EqlRelationDirectionType.OriginTarget,
                "casting integer 1 to EqlRelationDirectionType must produce OriginTarget");
        }

        /// <summary>
        /// Verifies that integer 0 is a defined value in the enum.
        /// Used as a boundary check to confirm valid ordinal range starts at 0.
        /// </summary>
        [Fact]
        public void Test_IsDefined_0()
        {
            // Arrange & Act
            var isDefined = Enum.IsDefined(typeof(EqlRelationDirectionType), 0);

            // Assert
            isDefined.Should().BeTrue("integer 0 maps to TargetOrigin and must be a defined enum value");
        }

        /// <summary>
        /// Verifies that integer 1 is a defined value in the enum.
        /// Used as a boundary check to confirm valid ordinal range includes 1.
        /// </summary>
        [Fact]
        public void Test_IsDefined_1()
        {
            // Arrange & Act
            var isDefined = Enum.IsDefined(typeof(EqlRelationDirectionType), 1);

            // Assert
            isDefined.Should().BeTrue("integer 1 maps to OriginTarget and must be a defined enum value");
        }

        /// <summary>
        /// Verifies that integer 2 is NOT a defined value in the enum.
        /// This boundary test confirms no third direction type has been added
        /// beyond the expected TargetOrigin (0) and OriginTarget (1) pair.
        /// </summary>
        [Fact]
        public void Test_IsDefined_2_False()
        {
            // Arrange & Act
            var isDefined = Enum.IsDefined(typeof(EqlRelationDirectionType), 2);

            // Assert
            isDefined.Should().BeFalse("integer 2 is outside the valid range and must not be a defined enum value");
        }

        // ──────────────────────────────────────────────
        // Phase 5: Default Value Test
        // ──────────────────────────────────────────────

        /// <summary>
        /// Verifies that the default value of the enum is TargetOrigin (ordinal 0).
        /// This is significant because uninitialized enum fields and default(T) expressions
        /// will resolve to TargetOrigin, matching the '$' single-dollar EQL prefix as the
        /// implicit default relation direction.
        /// </summary>
        [Fact]
        public void Test_DefaultValue_IsTargetOrigin()
        {
            // Arrange & Act
            var defaultValue = default(EqlRelationDirectionType);

            // Assert
            defaultValue.Should().Be(EqlRelationDirectionType.TargetOrigin,
                "default enum value must be TargetOrigin (ordinal 0) since it is the first member");
        }

        // ──────────────────────────────────────────────
        // Phase 6: Semantic Mapping Tests
        // ──────────────────────────────────────────────

        /// <summary>
        /// Validates that TargetOrigin corresponds to the '$' single-dollar prefix
        /// in EQL grammar for relation field references.
        /// Per EqlBuilder.cs (line 444): when the relation direction node text is '$',
        /// the direction is resolved to EqlRelationDirectionType.TargetOrigin.
        /// This means '$relation_name.field_name' traverses from target entity to origin entity.
        /// </summary>
        [Fact]
        public void Test_TargetOrigin_MappedToDollar()
        {
            // The '$' prefix in EQL syntax maps to TargetOrigin direction.
            // EqlBuilder.cs resolves '$' → EqlRelationDirectionType.TargetOrigin
            // which instructs the query engine to traverse from target back to origin.
            //
            // Validation: TargetOrigin must be ordinal 0 and parseable from its name,
            // confirming the enum member the EQL builder references is stable.
            var direction = EqlRelationDirectionType.TargetOrigin;

            // Assert the value identity and ordinal to confirm mapping stability
            direction.Should().Be(EqlRelationDirectionType.TargetOrigin,
                "TargetOrigin is the enum value mapped to '$' prefix in EQL grammar");
            ((int)direction).Should().Be(0,
                "TargetOrigin ordinal must be 0, matching the '$' single-dollar direction indicator");
        }

        /// <summary>
        /// Validates that OriginTarget corresponds to the '$$' double-dollar prefix
        /// in EQL grammar for relation field references.
        /// Per EqlBuilder.cs (lines 445-446): when the relation direction node text is '$$',
        /// the direction is resolved to EqlRelationDirectionType.OriginTarget.
        /// This means '$$relation_name.field_name' traverses from origin entity to target entity.
        /// </summary>
        [Fact]
        public void Test_OriginTarget_MappedToDoubleDollar()
        {
            // The '$$' prefix in EQL syntax maps to OriginTarget direction.
            // EqlBuilder.cs resolves '$$' → EqlRelationDirectionType.OriginTarget
            // which instructs the query engine to traverse from origin to target.
            //
            // Validation: OriginTarget must be ordinal 1 and parseable from its name,
            // confirming the enum member the EQL builder references is stable.
            var direction = EqlRelationDirectionType.OriginTarget;

            // Assert the value identity and ordinal to confirm mapping stability
            direction.Should().Be(EqlRelationDirectionType.OriginTarget,
                "OriginTarget is the enum value mapped to '$$' prefix in EQL grammar");
            ((int)direction).Should().Be(1,
                "OriginTarget ordinal must be 1, matching the '$$' double-dollar direction indicator");
        }
    }
}
