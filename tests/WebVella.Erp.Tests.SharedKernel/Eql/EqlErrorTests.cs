using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.Tests.SharedKernel.Eql
{
    /// <summary>
    /// Unit tests for <see cref="EqlError"/> — a simple error DTO with
    /// Message (string), Line (int?), and Column (int?) properties.
    /// Covers default values, set/get behavior, nullable edge cases,
    /// and object initializer patterns.
    /// </summary>
    public class EqlErrorTests
    {
        // ──────────────────────────────────────────────────────────────
        // Phase 1: Property Default Value Tests
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// A newly constructed EqlError should have Message defaulting to null
        /// because string is a reference type with a null default.
        /// </summary>
        [Fact]
        public void Test_Message_DefaultNull()
        {
            // Arrange & Act
            var error = new EqlError();

            // Assert
            error.Message.Should().BeNull();
        }

        /// <summary>
        /// A newly constructed EqlError should have Line defaulting to null
        /// because int? (nullable int) defaults to null.
        /// </summary>
        [Fact]
        public void Test_Line_DefaultNull()
        {
            // Arrange & Act
            var error = new EqlError();

            // Assert
            error.Line.Should().BeNull();
        }

        /// <summary>
        /// A newly constructed EqlError should have Column defaulting to null
        /// because int? (nullable int) defaults to null.
        /// </summary>
        [Fact]
        public void Test_Column_DefaultNull()
        {
            // Arrange & Act
            var error = new EqlError();

            // Assert
            error.Column.Should().BeNull();
        }

        // ──────────────────────────────────────────────────────────────
        // Phase 2: Property Set/Get Tests
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Setting Message to a known string should read back the same value.
        /// </summary>
        [Fact]
        public void Test_Message_SetGet()
        {
            // Arrange
            var error = new EqlError();

            // Act
            error.Message = "test error";

            // Assert
            error.Message.Should().Be("test error");
        }

        /// <summary>
        /// Setting Line to a known integer should read back the same value.
        /// </summary>
        [Fact]
        public void Test_Line_SetGet()
        {
            // Arrange
            var error = new EqlError();

            // Act
            error.Line = 5;

            // Assert
            error.Line.Should().Be(5);
        }

        /// <summary>
        /// Setting Column to a known integer should read back the same value.
        /// </summary>
        [Fact]
        public void Test_Column_SetGet()
        {
            // Arrange
            var error = new EqlError();

            // Act
            error.Column = 10;

            // Assert
            error.Column.Should().Be(10);
        }

        /// <summary>
        /// Setting all three properties at once via object initializer
        /// should store and retrieve each value correctly.
        /// </summary>
        [Fact]
        public void Test_AllProperties_SetGet()
        {
            // Arrange & Act
            var error = new EqlError
            {
                Message = "syntax error near token",
                Line = 42,
                Column = 17
            };

            // Assert
            error.Message.Should().Be("syntax error near token");
            error.Line.Should().Be(42);
            error.Column.Should().Be(17);
        }

        // ──────────────────────────────────────────────────────────────
        // Phase 3: Nullable Behavior Tests
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Explicitly setting Line to null should result in a null value,
        /// even after it was previously set to a non-null value.
        /// </summary>
        [Fact]
        public void Test_Line_SetToNull()
        {
            // Arrange
            var error = new EqlError { Line = 10 };

            // Act
            error.Line = null;

            // Assert
            error.Line.Should().BeNull();
        }

        /// <summary>
        /// Explicitly setting Column to null should result in a null value,
        /// even after it was previously set to a non-null value.
        /// </summary>
        [Fact]
        public void Test_Column_SetToNull()
        {
            // Arrange
            var error = new EqlError { Column = 20 };

            // Act
            error.Column = null;

            // Assert
            error.Column.Should().BeNull();
        }

        /// <summary>
        /// Setting Line to zero should store 0 — the value must not be
        /// confused with null. Zero is a valid line number.
        /// </summary>
        [Fact]
        public void Test_Line_SetToZero()
        {
            // Arrange
            var error = new EqlError();

            // Act
            error.Line = 0;

            // Assert
            error.Line.Should().Be(0);
            error.Line.Should().NotBe(null);
        }

        /// <summary>
        /// Setting Column to zero should store 0 — the value must not be
        /// confused with null. Zero is a valid column number.
        /// </summary>
        [Fact]
        public void Test_Column_SetToZero()
        {
            // Arrange
            var error = new EqlError();

            // Act
            error.Column = 0;

            // Assert
            error.Column.Should().Be(0);
            error.Column.Should().NotBe(null);
        }

        /// <summary>
        /// Setting Line to a negative value should store the negative value.
        /// EqlError is a plain DTO with no validation constraints.
        /// </summary>
        [Fact]
        public void Test_Line_SetToNegative()
        {
            // Arrange
            var error = new EqlError();

            // Act
            error.Line = -1;

            // Assert
            error.Line.Should().Be(-1);
        }

        // ──────────────────────────────────────────────────────────────
        // Phase 4: Object Initializer Tests
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Using an object initializer with all three properties should
        /// correctly populate every property.
        /// </summary>
        [Fact]
        public void Test_ObjectInitializer_AllProperties()
        {
            // Arrange & Act
            var error = new EqlError
            {
                Message = "err",
                Line = 1,
                Column = 2
            };

            // Assert
            error.Message.Should().Be("err");
            error.Line.Should().Be(1);
            error.Column.Should().Be(2);
        }

        /// <summary>
        /// Using an object initializer with only Message set should leave
        /// Line and Column at their default null values.
        /// </summary>
        [Fact]
        public void Test_ObjectInitializer_PartialProperties()
        {
            // Arrange & Act
            var error = new EqlError { Message = "err" };

            // Assert
            error.Message.Should().Be("err");
            error.Line.Should().BeNull();
            error.Column.Should().BeNull();
        }

        /// <summary>
        /// Using an empty object initializer (no properties set) should
        /// result in all defaults: Message = null, Line = null, Column = null.
        /// </summary>
        [Fact]
        public void Test_ObjectInitializer_Empty()
        {
            // Arrange & Act
            var error = new EqlError { };

            // Assert
            error.Message.Should().BeNull();
            error.Line.Should().BeNull();
            error.Column.Should().BeNull();
        }
    }
}
