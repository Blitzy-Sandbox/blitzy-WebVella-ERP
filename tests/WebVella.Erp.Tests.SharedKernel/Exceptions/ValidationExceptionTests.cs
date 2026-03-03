using System;
using System.Collections.Generic;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Exceptions;

namespace WebVella.Erp.Tests.SharedKernel.Exceptions
{
    /// <summary>
    /// Unit tests for <see cref="ValidationException"/> — a batching/aggregation exception
    /// that collects validation errors via AddError() and throws self via CheckAndThrow().
    /// Key behaviors tested:
    ///   - Three constructor overloads (parameterless, single-message, full)
    ///   - The 'new' keyword Message property shadowing base Exception.Message
    ///   - AddError message seeding logic (first error seeds Message when blank)
    ///   - CheckAndThrow throw-self pattern
    ///   - Errors list accumulation and mutability
    /// </summary>
    public class ValidationExceptionTests
    {
        #region Constructor Tests — Parameterless

        /// <summary>
        /// Verifies that the parameterless constructor initializes the Errors list
        /// as a non-null empty list. The parameterless constructor delegates to
        /// this(null, null), which re-initializes Errors in the full constructor body.
        /// </summary>
        [Fact]
        public void Constructor_Parameterless_InitializesErrors_AsEmptyList()
        {
            // Arrange & Act
            var ex = new ValidationException();

            // Assert
            ex.Errors.Should().NotBeNull();
            ex.Errors.Should().HaveCount(0);
        }

        /// <summary>
        /// Verifies that the parameterless constructor sets the shadowed Message
        /// property to null. The delegation chain is:
        ///   ValidationException() → this(null, null) → Message = message (which is null)
        /// The field initializer (= "") is overwritten by the constructor body assignment.
        /// </summary>
        [Fact]
        public void Constructor_Parameterless_Message_IsNull()
        {
            // Arrange & Act
            var ex = new ValidationException();

            // Assert — the shadowed Message property is null (set from null parameter)
            ex.Message.Should().BeNull();
        }

        /// <summary>
        /// Verifies that the shadowed Message differs from the base Exception.Message
        /// when constructed with no arguments. The shadowed ex.Message is null, but
        /// ((Exception)ex).Message returns the default "Exception of type '...' was thrown."
        /// text because null was passed to the base constructor.
        /// </summary>
        [Fact]
        public void Constructor_Parameterless_BaseMessage_IsNull()
        {
            // Arrange & Act
            var ex = new ValidationException();

            // Assert — shadowed Message is null
            ex.Message.Should().BeNull();

            // The base Exception.Message should contain the default exception text
            // since null was passed to base(message, inner)
            string baseMessage = ((Exception)ex).Message;
            baseMessage.Should().NotBeNull();
            baseMessage.Should().NotBe(ex.Message,
                "the base Exception.Message returns a default string when null is passed, "
                + "while the shadowed Message remains null");
        }

        #endregion

        #region Constructor Tests — Single Message

        /// <summary>
        /// Verifies that the single-message constructor sets the shadowed Message
        /// property to the provided string value.
        /// </summary>
        [Fact]
        public void Constructor_WithMessage_SetsMessageProperty()
        {
            // Arrange & Act
            var ex = new ValidationException("test error");

            // Assert
            ex.Message.Should().Be("test error");
        }

        /// <summary>
        /// Verifies that the single-message constructor also sets the base
        /// Exception.Message via the base(message, inner) call. When a non-null
        /// message is provided, both the shadowed and base Message should match.
        /// </summary>
        [Fact]
        public void Constructor_WithMessage_SetsBaseMessage()
        {
            // Arrange & Act
            var ex = new ValidationException("test error");

            // Assert — base Exception.Message also receives the message
            string baseMessage = ((Exception)ex).Message;
            baseMessage.Should().Be("test error");
        }

        /// <summary>
        /// Verifies that the single-message constructor still initializes the
        /// Errors list as a non-null empty list.
        /// </summary>
        [Fact]
        public void Constructor_WithMessage_InitializesErrors_AsEmptyList()
        {
            // Arrange & Act
            var ex = new ValidationException("test error");

            // Assert
            ex.Errors.Should().NotBeNull();
            ex.Errors.Should().HaveCount(0);
        }

        #endregion

        #region Constructor Tests — Full Constructor

        /// <summary>
        /// Verifies that the full constructor correctly sets Message, InnerException,
        /// and propagates the inner exception's message.
        /// </summary>
        [Fact]
        public void Constructor_Full_SetsMessageAndInner()
        {
            // Arrange
            var inner = new InvalidOperationException("inner");

            // Act
            var ex = new ValidationException("outer", inner);

            // Assert
            ex.Message.Should().Be("outer");
            ex.InnerException.Should().BeSameAs(inner);
            ex.InnerException.Message.Should().Be("inner");
        }

        /// <summary>
        /// Verifies that passing null for the message parameter in the full
        /// constructor sets the shadowed Message to null.
        /// </summary>
        [Fact]
        public void Constructor_Full_NullMessage_SetsMessageToNull()
        {
            // Arrange & Act
            var ex = new ValidationException(null, null);

            // Assert
            ex.Message.Should().BeNull();
        }

        /// <summary>
        /// Verifies that each instance gets its own fresh Errors list reference.
        /// Two separately constructed instances should not share the same list object.
        /// </summary>
        [Fact]
        public void Constructor_Full_ErrorsListIsFresh()
        {
            // Arrange & Act
            var ex1 = new ValidationException("msg1", null);
            var ex2 = new ValidationException("msg2", null);

            // Assert — different list references
            ex1.Errors.Should().NotBeSameAs(ex2.Errors);
        }

        #endregion

        #region Message Shadow Tests — 'new' keyword behavior

        /// <summary>
        /// CRITICAL: Tests the 'new' keyword Message shadow behavior when the
        /// shadowed property is null. The ValidationException.Message (shadowed)
        /// is null after parameterless construction, but the base Exception.Message
        /// returns a default string like "Exception of type '...' was thrown."
        /// These two values must differ.
        /// </summary>
        [Fact]
        public void Message_Shadow_DiffersFromBaseWhenNull()
        {
            // Arrange & Act
            var ex = new ValidationException();

            // Assert
            string shadowedMessage = ex.Message;
            string baseMessage = ((Exception)ex).Message;

            // The shadowed Message is null
            shadowedMessage.Should().BeNull();

            // The base Exception.Message has a default non-null string
            baseMessage.Should().NotBeNull();

            // They must differ — this is the essence of the 'new' keyword shadow
            shadowedMessage.Should().NotBe(baseMessage);
        }

        /// <summary>
        /// Verifies that when a non-null message is provided, both the shadowed
        /// ValidationException.Message and the base Exception.Message return the
        /// same string value (since the same value is passed to both).
        /// </summary>
        [Fact]
        public void Message_Shadow_MatchesBaseWhenSet()
        {
            // Arrange & Act
            var ex = new ValidationException("hello");

            // Assert
            string shadowedMessage = ex.Message;
            string baseMessage = ((Exception)ex).Message;

            shadowedMessage.Should().Be("hello");
            baseMessage.Should().Be("hello");

            // Both should match when a non-null message is passed
            shadowedMessage.Should().Be(baseMessage);
        }

        #endregion

        #region AddError Method Tests

        /// <summary>
        /// Verifies that AddError correctly appends a ValidationError to the Errors list
        /// with the proper property values. Note that ValidationError normalizes fieldName
        /// to lowercase via ToLowerInvariant().
        /// </summary>
        [Fact]
        public void AddError_AppendsValidationError()
        {
            // Arrange
            var ex = new ValidationException();

            // Act
            ex.AddError("field1", "error msg");

            // Assert
            ex.Errors.Should().HaveCount(1);

            var error = ex.Errors[0];
            error.PropertyName.Should().Be("field1"); // already lowercase, no change
            error.Message.Should().Be("error msg");
            error.IsSystem.Should().Be(false);         // AddError always passes false
            error.Index.Should().Be(0);                 // default index
        }

        /// <summary>
        /// Verifies that AddError seeds the shadowed Message property when it is
        /// initially blank/null. After parameterless construction, Message is null
        /// (string.IsNullOrWhiteSpace(null) == true), so the first AddError call
        /// sets Message to the error's message.
        /// </summary>
        [Fact]
        public void AddError_SeedsMessage_WhenBlank()
        {
            // Arrange — parameterless constructor sets Message = null
            var ex = new ValidationException();

            // Act
            ex.AddError("f", "first error");

            // Assert — Message should be seeded from the first AddError call
            ex.Message.Should().Be("first error");
        }

        /// <summary>
        /// Verifies that AddError does NOT override the Message property when it
        /// is already set to a non-blank/non-whitespace value. The
        /// string.IsNullOrWhiteSpace check protects against overwriting.
        /// </summary>
        [Fact]
        public void AddError_DoesNotOverrideMessage_WhenAlreadySet()
        {
            // Arrange — Message is set to "initial msg" by the constructor
            var ex = new ValidationException("initial msg");

            // Act
            ex.AddError("f", "second msg");

            // Assert — Message remains "initial msg", not overwritten
            ex.Message.Should().Be("initial msg");
        }

        /// <summary>
        /// Verifies that multiple AddError calls accumulate errors in the Errors list.
        /// Each call appends a new ValidationError entry.
        /// </summary>
        [Fact]
        public void AddError_MultipleCalls_Accumulate()
        {
            // Arrange
            var ex = new ValidationException();

            // Act
            ex.AddError("a", "err1");
            ex.AddError("b", "err2");
            ex.AddError("c", "err3");

            // Assert
            ex.Errors.Should().HaveCount(3);

            // Verify each error's properties (fieldName normalized to lowercase)
            ex.Errors[0].PropertyName.Should().Be("a");
            ex.Errors[0].Message.Should().Be("err1");

            ex.Errors[1].PropertyName.Should().Be("b");
            ex.Errors[1].Message.Should().Be("err2");

            ex.Errors[2].PropertyName.Should().Be("c");
            ex.Errors[2].Message.Should().Be("err3");
        }

        /// <summary>
        /// Verifies that Message is only seeded on the first AddError call when
        /// initially blank. Subsequent AddError calls do not update Message because
        /// after the first call it's no longer blank/null/whitespace.
        /// </summary>
        [Fact]
        public void AddError_MessageOnlySetOnFirstCall_WhenInitiallyBlank()
        {
            // Arrange — Message is null after parameterless construction
            var ex = new ValidationException();

            // Act
            ex.AddError("a", "first");
            ex.AddError("b", "second");

            // Assert — Message is "first", not "second"
            ex.Message.Should().Be("first");
        }

        /// <summary>
        /// Verifies that AddError passes the index parameter through to the
        /// ValidationError constructor correctly.
        /// </summary>
        [Fact]
        public void AddError_WithIndex_PassesIndexToValidationError()
        {
            // Arrange
            var ex = new ValidationException();

            // Act
            ex.AddError("f", "msg", 5);

            // Assert
            ex.Errors[0].Index.Should().Be(5);
        }

        /// <summary>
        /// Verifies that AddError allows null fieldName. The ValidationError class
        /// has its fieldName validation commented out, so null is acceptable.
        /// The PropertyName remains null because null?.ToLowerInvariant() returns null.
        /// </summary>
        [Fact]
        public void AddError_NullFieldName_Allowed()
        {
            // Arrange
            var ex = new ValidationException();

            // Act
            ex.AddError(null, "msg");

            // Assert — null fieldName passes through as null PropertyName
            ex.Errors[0].PropertyName.Should().BeNull();
        }

        #endregion

        #region CheckAndThrow Method Tests

        /// <summary>
        /// Verifies that CheckAndThrow throws the same ValidationException instance
        /// (throw this) when Errors contains at least one error. The caught exception
        /// should be the SAME reference as the original.
        /// </summary>
        [Fact]
        public void CheckAndThrow_WithErrors_ThrowsSelf()
        {
            // Arrange
            var ex = new ValidationException();
            ex.AddError("f", "err");

            // Act & Assert — should throw ValidationException
            Action act = () => ex.CheckAndThrow();
            var thrown = act.Should().Throw<ValidationException>().Which;

            // Verify the thrown exception is the SAME instance (throw this)
            thrown.Should().BeSameAs(ex);
        }

        /// <summary>
        /// Verifies that CheckAndThrow does NOT throw when the Errors list is empty.
        /// A freshly constructed ValidationException with no AddError calls has
        /// an empty Errors list, so CheckAndThrow should be a no-op.
        /// </summary>
        [Fact]
        public void CheckAndThrow_NoErrors_DoesNotThrow()
        {
            // Arrange
            var ex = new ValidationException();

            // Act & Assert — should NOT throw
            Action act = () => ex.CheckAndThrow();
            act.Should().NotThrow();
        }

        /// <summary>
        /// Verifies that when CheckAndThrow throws, the caught exception contains
        /// ALL previously added errors in the Errors list.
        /// </summary>
        [Fact]
        public void CheckAndThrow_ThrownException_ContainsAllErrors()
        {
            // Arrange
            var ex = new ValidationException();
            ex.AddError("field1", "error1");
            ex.AddError("field2", "error2");
            ex.AddError("field3", "error3");

            // Act & Assert
            Action act = () => ex.CheckAndThrow();
            var thrown = act.Should().Throw<ValidationException>().Which;

            // The thrown exception should contain all 3 errors
            thrown.Errors.Should().HaveCount(3);
            thrown.Errors[0].Message.Should().Be("error1");
            thrown.Errors[1].Message.Should().Be("error2");
            thrown.Errors[2].Message.Should().Be("error3");
        }

        /// <summary>
        /// Verifies that CheckAndThrow does NOT throw when Errors is set to null.
        /// The null check in CheckAndThrow (Errors != null) protects against this case.
        /// </summary>
        [Fact]
        public void CheckAndThrow_ErrorsSetToNull_DoesNotThrow()
        {
            // Arrange
            var ex = new ValidationException();
            ex.Errors = null;

            // Act & Assert — should NOT throw because of null check
            Action act = () => ex.CheckAndThrow();
            act.Should().NotThrow();
        }

        #endregion

        #region Errors Property Tests

        /// <summary>
        /// Verifies that the Errors property is not null immediately after
        /// construction for all constructor variants. The full constructor
        /// always re-initializes Errors with a new List.
        /// </summary>
        [Fact]
        public void Errors_IsNotNull_OnConstruction()
        {
            // Act — test all constructor variants
            var ex1 = new ValidationException();
            var ex2 = new ValidationException("message");
            var ex3 = new ValidationException("message", new Exception("inner"));

            // Assert — all should have non-null Errors lists
            ex1.Errors.Should().NotBeNull();
            ex2.Errors.Should().NotBeNull();
            ex3.Errors.Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that the Errors property is mutable and can be reassigned
        /// to a completely new list. The property has a public setter.
        /// </summary>
        [Fact]
        public void Errors_IsMutable_CanBeReassigned()
        {
            // Arrange
            var ex = new ValidationException();
            var originalList = ex.Errors;
            var newList = new List<ValidationError>();

            // Act
            ex.Errors = newList;

            // Assert — the reference should now point to the new list
            ex.Errors.Should().BeSameAs(newList);
            ex.Errors.Should().NotBeSameAs(originalList);
        }

        #endregion
    }
}
