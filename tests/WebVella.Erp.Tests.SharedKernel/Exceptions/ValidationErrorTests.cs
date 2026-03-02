using System;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Exceptions;

namespace WebVella.Erp.Tests.SharedKernel.Exceptions
{
    /// <summary>
    /// Unit tests for the <see cref="ValidationError"/> class — a lightweight, serialization-friendly
    /// data container representing a single validation failure. This class is the foundational type
    /// used by <see cref="ValidationException"/>.
    ///
    /// Tests cover:
    /// - Constructor guard clauses (negative index, null/empty/whitespace message)
    /// - Intentionally commented-out fieldName validation (null/empty/whitespace fieldName is ALLOWED)
    /// - PropertyName lowercase normalization via ToLowerInvariant()
    /// - Default parameter values (isSystem=false, index=0)
    /// - Property mutability (all auto-properties have public setters)
    /// - Edge cases (long.MaxValue, special characters, Unicode, whitespace fieldNames)
    /// </summary>
    public class ValidationErrorTests
    {
        // ============================================================
        // Region: Constructor Validation — Negative Index
        // ============================================================

        /// <summary>
        /// Validates that a negative index value (-1) causes the constructor to throw
        /// an <see cref="ArgumentException"/> with message containing "index".
        /// Source: ValidationError.cs line 35-36 — if (index &lt; 0) throw new ArgumentException("index");
        /// </summary>
        [Fact]
        public void Constructor_NegativeIndex_ThrowsArgumentException()
        {
            // Arrange
            Action act = () => new ValidationError("field", "msg", false, -1);

            // Act & Assert
            act.Should().Throw<ArgumentException>()
                .WithMessage("*index*");
        }

        /// <summary>
        /// Validates that the minimum possible long value (long.MinValue) causes the constructor
        /// to throw an <see cref="ArgumentException"/>, since long.MinValue is negative.
        /// </summary>
        [Fact]
        public void Constructor_NegativeIndexMinValue_ThrowsArgumentException()
        {
            // Arrange
            Action act = () => new ValidationError("field", "msg", false, long.MinValue);

            // Act & Assert
            act.Should().Throw<ArgumentException>();
        }

        /// <summary>
        /// Validates that an index of zero does not throw, since zero is the boundary value
        /// and the guard clause only rejects negative values (index &lt; 0).
        /// </summary>
        [Fact]
        public void Constructor_ZeroIndex_DoesNotThrow()
        {
            // Arrange
            Action act = () => new ValidationError("field", "msg", false, 0);

            // Act & Assert
            act.Should().NotThrow();
        }

        /// <summary>
        /// Validates that a positive index value (42) does not throw and is correctly assigned
        /// to the <see cref="ValidationError.Index"/> property.
        /// </summary>
        [Fact]
        public void Constructor_PositiveIndex_DoesNotThrow()
        {
            // Act
            var error = new ValidationError("field", "msg", false, 42);

            // Assert
            error.Index.Should().Be(42);
        }

        // ============================================================
        // Region: Constructor Validation — Message
        // ============================================================

        /// <summary>
        /// Validates that passing null as the message parameter causes the constructor
        /// to throw an <see cref="ArgumentException"/> with message containing "message".
        /// Source: ValidationError.cs line 41-42 — if (string.IsNullOrWhiteSpace(message))
        /// </summary>
        [Fact]
        public void Constructor_NullMessage_ThrowsArgumentException()
        {
            // Arrange
            Action act = () => new ValidationError("field", null);

            // Act & Assert
            act.Should().Throw<ArgumentException>()
                .WithMessage("*message*");
        }

        /// <summary>
        /// Validates that passing an empty string as the message parameter causes the constructor
        /// to throw an <see cref="ArgumentException"/>.
        /// </summary>
        [Fact]
        public void Constructor_EmptyMessage_ThrowsArgumentException()
        {
            // Arrange
            Action act = () => new ValidationError("field", "");

            // Act & Assert
            act.Should().Throw<ArgumentException>();
        }

        /// <summary>
        /// Validates that passing a whitespace-only string as the message parameter causes the
        /// constructor to throw an <see cref="ArgumentException"/>, since string.IsNullOrWhiteSpace
        /// returns true for whitespace strings.
        /// </summary>
        [Fact]
        public void Constructor_WhitespaceMessage_ThrowsArgumentException()
        {
            // Arrange
            Action act = () => new ValidationError("field", "   ");

            // Act & Assert
            act.Should().Throw<ArgumentException>();
        }

        /// <summary>
        /// Validates that a valid, non-empty, non-whitespace message does not throw
        /// and is correctly assigned to the <see cref="ValidationError.Message"/> property.
        /// </summary>
        [Fact]
        public void Constructor_ValidMessage_DoesNotThrow()
        {
            // Act
            var error = new ValidationError("field", "valid error message");

            // Assert
            error.Message.Should().Be("valid error message");
        }

        // ============================================================
        // Region: PropertyName Normalization
        // ============================================================

        /// <summary>
        /// Validates that the fieldName "MyFieldName" is normalized to lowercase "myfieldname"
        /// via ToLowerInvariant() in the constructor.
        /// Source: ValidationError.cs line 44 — PropertyName = fieldName?.ToLowerInvariant();
        /// </summary>
        [Fact]
        public void Constructor_FieldName_NormalizedToLowercase()
        {
            // Act
            var error = new ValidationError("MyFieldName", "msg");

            // Assert
            error.PropertyName.Should().Be("myfieldname");
        }

        /// <summary>
        /// Validates that an all-uppercase fieldName "ALLCAPS" is normalized to lowercase "allcaps".
        /// </summary>
        [Fact]
        public void Constructor_UppercaseFieldName_NormalizedToLowercase()
        {
            // Act
            var error = new ValidationError("ALLCAPS", "msg");

            // Assert
            error.PropertyName.Should().Be("allcaps");
        }

        /// <summary>
        /// Validates that a mixed-case fieldName "FirstName" is normalized to lowercase "firstname".
        /// </summary>
        [Fact]
        public void Constructor_MixedCaseFieldName_NormalizedToLowercase()
        {
            // Act
            var error = new ValidationError("FirstName", "msg");

            // Assert
            error.PropertyName.Should().Be("firstname");
        }

        /// <summary>
        /// Validates that an already-lowercase fieldName "email" remains unchanged after
        /// ToLowerInvariant() normalization.
        /// </summary>
        [Fact]
        public void Constructor_AlreadyLowercaseFieldName_Unchanged()
        {
            // Act
            var error = new ValidationError("email", "msg");

            // Assert
            error.PropertyName.Should().Be("email");
        }

        /// <summary>
        /// CRITICAL TEST: Validates that null fieldName is ALLOWED because the fieldName validation
        /// is commented out in the source code (lines 38-39). The null-conditional operator in
        /// <c>fieldName?.ToLowerInvariant()</c> returns null when fieldName is null.
        /// This is intentional monolith behavior — null fieldName allows object-level errors.
        /// </summary>
        [Fact]
        public void Constructor_NullFieldName_AllowedAndStaysNull()
        {
            // Act
            var error = new ValidationError(null, "msg");

            // Assert — PropertyName should remain null (null?.ToLowerInvariant() == null)
            error.PropertyName.Should().BeNull();
        }

        /// <summary>
        /// Validates that an empty string fieldName is allowed (commented-out validation) and
        /// stays as an empty string after ToLowerInvariant() normalization.
        /// </summary>
        [Fact]
        public void Constructor_EmptyFieldName_AllowedAndNormalizedToEmpty()
        {
            // Act
            var error = new ValidationError("", "msg");

            // Assert — empty string lowercased is still empty string
            error.PropertyName.Should().Be("");
        }

        // ============================================================
        // Region: Property Assignment Tests
        // ============================================================

        /// <summary>
        /// Validates that all four properties are correctly assigned when all constructor
        /// parameters are explicitly provided, including PropertyName lowercase normalization.
        /// </summary>
        [Fact]
        public void Constructor_AllPropertiesAssignedCorrectly()
        {
            // Act
            var error = new ValidationError("FieldX", "Error occurred", true, 7);

            // Assert
            error.PropertyName.Should().Be("fieldx");    // lowercased
            error.Message.Should().Be("Error occurred");
            error.IsSystem.Should().Be(true);
            error.Index.Should().Be(7);
        }

        /// <summary>
        /// Validates that the default value for the isSystem parameter is false when not
        /// explicitly specified in the constructor call.
        /// </summary>
        [Fact]
        public void Constructor_DefaultIsSystem_IsFalse()
        {
            // Act — only fieldName and message provided; isSystem defaults to false
            var error = new ValidationError("f", "msg");

            // Assert
            error.IsSystem.Should().Be(false);
        }

        /// <summary>
        /// Validates that the default value for the index parameter is 0 when not explicitly
        /// specified in the constructor call.
        /// </summary>
        [Fact]
        public void Constructor_DefaultIndex_IsZero()
        {
            // Act — only fieldName and message provided; index defaults to 0
            var error = new ValidationError("f", "msg");

            // Assert
            error.Index.Should().Be(0);
        }

        /// <summary>
        /// Validates that explicitly passing true for isSystem correctly sets the IsSystem property.
        /// </summary>
        [Fact]
        public void Constructor_ExplicitIsSystemTrue()
        {
            // Act
            var error = new ValidationError("f", "msg", true);

            // Assert
            error.IsSystem.Should().Be(true);
        }

        /// <summary>
        /// Validates that explicitly passing false for isSystem correctly sets the IsSystem property.
        /// </summary>
        [Fact]
        public void Constructor_ExplicitIsSystemFalse()
        {
            // Act
            var error = new ValidationError("f", "msg", false);

            // Assert
            error.IsSystem.Should().Be(false);
        }

        // ============================================================
        // Region: Property Mutability Tests
        // ============================================================

        /// <summary>
        /// Validates that the Index property is mutable — it can be changed after construction
        /// because it's an auto-property with a public setter.
        /// </summary>
        [Fact]
        public void Index_IsMutable_CanBeChanged()
        {
            // Arrange
            var error = new ValidationError("f", "msg");

            // Act
            error.Index = 99;

            // Assert
            error.Index.Should().Be(99);
        }

        /// <summary>
        /// Validates that the PropertyName property is mutable and can be changed after construction.
        /// NOTE: Setting PropertyName after construction does NOT normalize to lowercase —
        /// only the constructor performs ToLowerInvariant() normalization.
        /// </summary>
        [Fact]
        public void PropertyName_IsMutable_CanBeChanged()
        {
            // Arrange
            var error = new ValidationError("f", "msg");

            // Act — setting directly bypasses ToLowerInvariant() normalization
            error.PropertyName = "newname";

            // Assert
            error.PropertyName.Should().Be("newname");
        }

        /// <summary>
        /// Validates that the Message property is mutable and can be changed after construction.
        /// </summary>
        [Fact]
        public void Message_IsMutable_CanBeChanged()
        {
            // Arrange
            var error = new ValidationError("f", "msg");

            // Act
            error.Message = "new message";

            // Assert
            error.Message.Should().Be("new message");
        }

        /// <summary>
        /// Validates that the IsSystem property is mutable and can be changed after construction.
        /// </summary>
        [Fact]
        public void IsSystem_IsMutable_CanBeChanged()
        {
            // Arrange — starts with isSystem=false (default)
            var error = new ValidationError("f", "msg");

            // Act
            error.IsSystem = true;

            // Assert
            error.IsSystem.Should().Be(true);
        }

        // ============================================================
        // Region: Edge Cases and Boundary Tests
        // ============================================================

        /// <summary>
        /// Validates that long.MaxValue is accepted as a valid index value, testing the upper
        /// boundary of the index range.
        /// </summary>
        [Fact]
        public void Constructor_LargeIndex_Accepted()
        {
            // Act
            var error = new ValidationError("f", "msg", false, long.MaxValue);

            // Assert
            error.Index.Should().Be(long.MaxValue);
        }

        /// <summary>
        /// Validates that special characters (dots, brackets) in the fieldName are preserved
        /// after lowercase normalization, since ToLowerInvariant() only affects letter casing.
        /// </summary>
        [Fact]
        public void Constructor_SpecialCharactersInFieldName()
        {
            // Act
            var error = new ValidationError("field.name[0]", "msg");

            // Assert — special characters are not affected by ToLowerInvariant()
            error.PropertyName.Should().Be("field.name[0]");
        }

        /// <summary>
        /// Validates that a Unicode fieldName containing the Turkish İ (U+0130, capital I with
        /// dot above) is normalized via ToLowerInvariant() using the invariant culture rules.
        /// The exact lowercased output depends on the .NET ICU implementation.
        /// </summary>
        [Fact]
        public void Constructor_UnicodeFieldName_Normalized()
        {
            // Arrange — "FİELD" contains Turkish İ (U+0130)
            string unicodeFieldName = "F\u0130ELD";
            string expectedLowered = unicodeFieldName.ToLowerInvariant();

            // Act
            var error = new ValidationError(unicodeFieldName, "msg");

            // Assert — PropertyName should equal the ToLowerInvariant() result
            error.PropertyName.Should().Be(expectedLowered);
        }

        /// <summary>
        /// Validates that a whitespace-only fieldName "  " is ALLOWED because the fieldName
        /// validation is commented out in the source code. Whitespace has no casing, so
        /// ToLowerInvariant() preserves it as-is.
        /// </summary>
        [Fact]
        public void Constructor_WhitespaceFieldName_Allowed()
        {
            // Act — whitespace fieldName is allowed (commented-out validation)
            var error = new ValidationError("  ", "msg");

            // Assert — whitespace preserved; lowercasing whitespace has no effect
            error.PropertyName.Should().Be("  ");
        }
    }
}
