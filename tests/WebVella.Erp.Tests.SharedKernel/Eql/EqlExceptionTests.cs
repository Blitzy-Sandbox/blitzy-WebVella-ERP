using System;
using System.Collections.Generic;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.Tests.SharedKernel.Eql
{
    /// <summary>
    /// Unit tests for <see cref="EqlException"/> — an Exception subclass wrapping
    /// <see cref="List{EqlError}"/> with three constructor overloads:
    ///   1. EqlException(string message)
    ///   2. EqlException(EqlError error)
    ///   3. EqlException(List&lt;EqlError&gt; errors)
    /// Tests verify all constructors, null/empty/whitespace message handling,
    /// Errors property behavior, and exception hierarchy.
    /// </summary>
    public class EqlExceptionTests
    {
        // ────────────────────────────────────────────────────────────────
        // Phase 1: Constructor(string message) Tests
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that the string constructor sets the exception Message
        /// to the provided non-null, non-whitespace message text.
        /// </summary>
        [Fact]
        public void Test_StringConstructor_SetsMessage()
        {
            // Arrange & Act
            var exception = new EqlException("test error");

            // Assert
            exception.Message.Should().Be("test error");
        }

        /// <summary>
        /// Verifies that the string constructor adds a single EqlError
        /// to the Errors list with the provided message text.
        /// </summary>
        [Fact]
        public void Test_StringConstructor_AddsErrorToList()
        {
            // Arrange & Act
            var exception = new EqlException("test error");

            // Assert
            exception.Errors.Should().HaveCount(1);
            exception.Errors[0].Message.Should().Be("test error");
        }

        /// <summary>
        /// Verifies that passing null to the string constructor causes the
        /// exception Message to fall back to the default message
        /// "One or more Eql errors occurred." because string.IsNullOrWhiteSpace(null)
        /// is true.
        /// </summary>
        [Fact]
        public void Test_StringConstructor_NullMessage_UsesDefaultMessage()
        {
            // Arrange & Act
            var exception = new EqlException((string)null);

            // Assert
            exception.Message.Should().Be("One or more Eql errors occurred.");
        }

        /// <summary>
        /// Verifies that passing an empty string to the string constructor causes
        /// the exception Message to fall back to the default message because
        /// string.IsNullOrWhiteSpace("") is true.
        /// </summary>
        [Fact]
        public void Test_StringConstructor_EmptyMessage_UsesDefaultMessage()
        {
            // Arrange & Act
            var exception = new EqlException("");

            // Assert
            exception.Message.Should().Be("One or more Eql errors occurred.");
        }

        /// <summary>
        /// Verifies that passing a whitespace-only string to the string constructor
        /// causes the exception Message to fall back to the default message because
        /// string.IsNullOrWhiteSpace("   ") is true.
        /// </summary>
        [Fact]
        public void Test_StringConstructor_WhitespaceMessage_UsesDefaultMessage()
        {
            // Arrange & Act
            var exception = new EqlException("   ");

            // Assert
            exception.Message.Should().Be("One or more Eql errors occurred.");
        }

        /// <summary>
        /// Verifies that even when null is passed to the string constructor, the
        /// Errors list still contains exactly one EqlError entry whose Message
        /// property is null (the constructor unconditionally calls Errors.Add).
        /// </summary>
        [Fact]
        public void Test_StringConstructor_NullMessage_ErrorStillAdded()
        {
            // Arrange & Act
            var exception = new EqlException((string)null);

            // Assert
            exception.Errors.Should().HaveCount(1);
            exception.Errors[0].Should().NotBeNull();
            exception.Errors[0].Message.Should().BeNull();
        }

        // ────────────────────────────────────────────────────────────────
        // Phase 2: Constructor(EqlError error) Tests
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that the EqlError constructor sets the exception Message
        /// from the provided error's Message property.
        /// </summary>
        [Fact]
        public void Test_ErrorConstructor_SetsMessageFromError()
        {
            // Arrange
            var error = new EqlError { Message = "specific error" };

            // Act
            var exception = new EqlException(error);

            // Assert
            exception.Message.Should().Be("specific error");
        }

        /// <summary>
        /// Verifies that the EqlError constructor adds exactly one entry
        /// to the Errors list.
        /// </summary>
        [Fact]
        public void Test_ErrorConstructor_AddsErrorToList()
        {
            // Arrange
            var error = new EqlError { Message = "test" };

            // Act
            var exception = new EqlException(error);

            // Assert
            exception.Errors.Should().HaveCount(1);
            exception.Errors[0].Should().BeSameAs(error);
        }

        /// <summary>
        /// Verifies that passing a null EqlError to the error constructor
        /// causes the exception Message to fall back to the default message
        /// via the null-conditional operator (error?.Message ?? "...").
        /// </summary>
        [Fact]
        public void Test_ErrorConstructor_NullError_UsesDefaultMessage()
        {
            // Arrange & Act
            var exception = new EqlException((EqlError)null);

            // Assert
            exception.Message.Should().Be("One or more Eql errors occurred.");
        }

        /// <summary>
        /// Verifies that passing a null EqlError still adds the null reference
        /// to the Errors list (the constructor unconditionally calls Errors.Add(error)).
        /// </summary>
        [Fact]
        public void Test_ErrorConstructor_NullError_AddsNullToList()
        {
            // Arrange & Act
            var exception = new EqlException((EqlError)null);

            // Assert
            exception.Errors.Should().HaveCount(1);
            exception.Errors[0].Should().BeNull();
        }

        /// <summary>
        /// Verifies that an EqlError with Line and Column values set is
        /// preserved exactly in the Errors collection after construction.
        /// </summary>
        [Fact]
        public void Test_ErrorConstructor_ErrorWithLineColumn()
        {
            // Arrange
            var error = new EqlError { Message = "syntax error", Line = 5, Column = 10 };

            // Act
            var exception = new EqlException(error);

            // Assert
            exception.Errors.Should().HaveCount(1);
            exception.Errors[0].Message.Should().Be("syntax error");
            exception.Errors[0].Line.Should().Be(5);
            exception.Errors[0].Column.Should().Be(10);
        }

        // ────────────────────────────────────────────────────────────────
        // Phase 3: Constructor(List<EqlError> errors) Tests
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that the list constructor always sets the exception Message
        /// to the default "One or more Eql errors occurred." regardless of the
        /// errors list content.
        /// </summary>
        [Fact]
        public void Test_ListConstructor_SetsDefaultMessage()
        {
            // Arrange & Act
            var exception = new EqlException(new List<EqlError>());

            // Assert
            exception.Message.Should().Be("One or more Eql errors occurred.");
        }

        /// <summary>
        /// Verifies that the list constructor adds all provided EqlError items
        /// to the Errors collection.
        /// </summary>
        [Fact]
        public void Test_ListConstructor_AddsAllErrors()
        {
            // Arrange
            var errors = new List<EqlError>
            {
                new EqlError { Message = "error 1" },
                new EqlError { Message = "error 2" },
                new EqlError { Message = "error 3" }
            };

            // Act
            var exception = new EqlException(errors);

            // Assert
            exception.Errors.Should().HaveCount(3);
        }

        /// <summary>
        /// Verifies that passing a null list to the list constructor results in
        /// an empty Errors collection (the "if (errors != null)" guard prevents
        /// AddRange from being called).
        /// </summary>
        [Fact]
        public void Test_ListConstructor_NullList_EmptyErrors()
        {
            // Arrange & Act
            var exception = new EqlException((List<EqlError>)null);

            // Assert
            exception.Errors.Should().NotBeNull();
            exception.Errors.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that passing an empty list to the list constructor results
        /// in an empty Errors collection.
        /// </summary>
        [Fact]
        public void Test_ListConstructor_EmptyList_EmptyErrors()
        {
            // Arrange & Act
            var exception = new EqlException(new List<EqlError>());

            // Assert
            exception.Errors.Should().NotBeNull();
            exception.Errors.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that the list constructor preserves the ordering of EqlError
        /// items from the input list in the Errors collection.
        /// </summary>
        [Fact]
        public void Test_ListConstructor_PreservesErrorOrder()
        {
            // Arrange
            var error1 = new EqlError { Message = "first" };
            var error2 = new EqlError { Message = "second" };
            var error3 = new EqlError { Message = "third" };
            var errors = new List<EqlError> { error1, error2, error3 };

            // Act
            var exception = new EqlException(errors);

            // Assert
            exception.Errors.Should().HaveCount(3);
            exception.Errors.Should().ContainInOrder(error1, error2, error3);
        }

        // ────────────────────────────────────────────────────────────────
        // Phase 4: Errors Property & Exception Hierarchy Tests
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that the Errors property is initialized as a non-null empty
        /// list by the field initializer, even before any constructor body
        /// modifies it. Tested via the list constructor with null (which skips
        /// AddRange), confirming the initializer ran.
        /// </summary>
        [Fact]
        public void Test_Errors_InitializedAsEmptyList()
        {
            // Arrange & Act — null list means AddRange is skipped,
            // so Errors retains its field-initializer value
            var exception = new EqlException((List<EqlError>)null);

            // Assert
            exception.Errors.Should().NotBeNull();
            exception.Errors.Should().BeEmpty();
            exception.Errors.Should().BeOfType<List<EqlError>>();
        }

        /// <summary>
        /// Verifies that the Errors property has a private setter — external
        /// code cannot reassign it, but the underlying list is mutable (items
        /// can be added). This test confirms mutability by adding items
        /// post-construction.
        /// </summary>
        [Fact]
        public void Test_Errors_PrivateSet()
        {
            // Arrange
            var exception = new EqlException("test");

            // Act — the list itself is mutable even though the property setter is private
            exception.Errors.Add(new EqlError { Message = "additional error" });

            // Assert — original error from constructor + manually added error
            exception.Errors.Should().HaveCount(2);
            exception.Errors[0].Message.Should().Be("test");
            exception.Errors[1].Message.Should().Be("additional error");

            // Verify via reflection that the Errors setter is private
            var property = typeof(EqlException).GetProperty("Errors");
            property.Should().NotBeNull();
            property.GetSetMethod(nonPublic: true).Should().NotBeNull();
            property.GetSetMethod(nonPublic: false).Should().BeNull();
        }

        /// <summary>
        /// Verifies that EqlException inherits from System.Exception, confirming
        /// its position in the exception hierarchy.
        /// </summary>
        [Fact]
        public void Test_IsException_BaseClass()
        {
            // Arrange & Act
            var exception = new EqlException("test");

            // Assert
            (exception is Exception).Should().BeTrue();
            exception.Should().BeAssignableTo<Exception>();
        }

        /// <summary>
        /// Verifies that a thrown EqlException can be caught by a catch block
        /// targeting EqlException specifically, and that the exception's
        /// Message and Errors are accessible in the catch handler.
        /// </summary>
        [Fact]
        public void Test_ExceptionCanBeCaught()
        {
            // Arrange
            EqlException caught = null;

            // Act
            try
            {
                throw new EqlException("test catch");
            }
            catch (EqlException ex)
            {
                caught = ex;
            }

            // Assert
            caught.Should().NotBeNull();
            caught.Message.Should().Be("test catch");
            caught.Errors.Should().HaveCount(1);
        }

        /// <summary>
        /// Verifies that a thrown EqlException can be caught by a catch block
        /// targeting the base System.Exception type, confirming polymorphic
        /// exception handling works correctly.
        /// </summary>
        [Fact]
        public void Test_ExceptionCanBeCaughtAsException()
        {
            // Arrange
            Exception caught = null;

            // Act
            try
            {
                throw new EqlException("test base catch");
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            // Assert
            caught.Should().NotBeNull();
            caught.Should().BeOfType<EqlException>();
            caught.Message.Should().Be("test base catch");

            // Verify we can cast back and access EqlException-specific members
            var eqlException = caught as EqlException;
            eqlException.Should().NotBeNull();
            eqlException.Errors.Should().HaveCount(1);
        }
    }
}
