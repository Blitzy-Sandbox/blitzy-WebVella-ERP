using System;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Exceptions;

namespace WebVella.Erp.Tests.SharedKernel.Exceptions
{
    /// <summary>
    /// Unit tests for the <see cref="StorageException"/> class — a semantic marker exception
    /// for storage/persistence failures. Exercises both constructor overloads, type hierarchy,
    /// catch policy, and exception property propagation.
    ///
    /// CRITICAL: The first constructor <c>StorageException(Exception innerException)</c>
    /// dereferences <c>innerException.Message</c> directly, causing a
    /// <see cref="NullReferenceException"/> when null is passed. This is preserved monolith
    /// behavior and is explicitly tested.
    /// </summary>
    public class StorageExceptionTests
    {
        #region First Constructor — StorageException(Exception innerException)

        /// <summary>
        /// Verifies that the first constructor derives its Message from the inner exception's
        /// Message property via the <c>base(innerException.Message, innerException)</c> call.
        /// </summary>
        [Fact]
        public void Constructor_WithInnerException_SetsMessageFromInner()
        {
            // Arrange
            var inner = new InvalidOperationException("db connection failed");

            // Act
            var ex = new StorageException(inner);

            // Assert — Message is forwarded from innerException.Message
            ex.Message.Should().Be("db connection failed");
            ex.InnerException.Should().BeSameAs(inner);
        }

        /// <summary>
        /// Verifies that the InnerException property is correctly set by the first constructor.
        /// </summary>
        [Fact]
        public void Constructor_WithInnerException_SetsInnerException()
        {
            // Arrange
            var inner = new InvalidOperationException("operation failed");

            // Act
            var ex = new StorageException(inner);

            // Assert
            ex.InnerException.Should().NotBeNull();
            ex.InnerException.Should().BeSameAs(inner);
        }

        /// <summary>
        /// CRITICAL EDGE CASE: The first constructor dereferences innerException.Message
        /// directly. Passing null causes a NullReferenceException because you cannot call
        /// .Message on a null reference. The explicit (Exception)null cast is required to
        /// resolve to the first constructor overload (not the second with string message).
        /// This is preserved monolith behavior — NOT a bug to fix.
        /// </summary>
        [Fact]
        public void Constructor_WithNullInnerException_ThrowsNullReferenceException()
        {
            // Arrange — explicit cast to Exception to resolve to the first constructor
            // Without the cast, the compiler would see ambiguity between the two overloads
            Action act = () => new StorageException((Exception)null);

            // Act & Assert — NullReferenceException from innerException.Message dereference
            act.Should().Throw<NullReferenceException>();
        }

        /// <summary>
        /// Verifies that the inner exception's StackTrace is preserved and accessible
        /// through the StorageException wrapper when the inner exception has been thrown.
        /// </summary>
        [Fact]
        public void Constructor_WithInnerException_PropagatesStackTrace()
        {
            // Arrange — throw and catch to populate StackTrace
            Exception inner;
            try
            {
                throw new InvalidOperationException("operation failed");
            }
            catch (Exception caught)
            {
                inner = caught;
            }

            // Act
            var ex = new StorageException(inner);

            // Assert — StackTrace is populated because the inner exception was thrown
            ex.InnerException.Should().NotBeNull();
            ex.InnerException.StackTrace.Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that chained exception hierarchies are preserved when wrapping
        /// an exception that itself has an inner exception.
        /// </summary>
        [Fact]
        public void Constructor_WithInnerException_ChainedExceptions()
        {
            // Arrange — create a three-level exception chain
            var root = new Exception("root");
            var middle = new InvalidOperationException("middle", root);

            // Act
            var ex = new StorageException(middle);

            // Assert — full chain is preserved
            ex.InnerException.Should().BeSameAs(middle);
            ex.InnerException.InnerException.Should().BeSameAs(root);
            ex.Message.Should().Be("middle");
        }

        #endregion

        #region Second Constructor — StorageException(string message, Exception innerException)

        /// <summary>
        /// Verifies that the second constructor correctly sets both the Message and
        /// InnerException properties from explicit parameters.
        /// </summary>
        [Fact]
        public void Constructor_WithMessageAndInner_SetsMessage()
        {
            // Arrange
            var inner = new Exception("inner");

            // Act
            var ex = new StorageException("custom error", inner);

            // Assert
            ex.Message.Should().Be("custom error");
            ex.InnerException.Should().BeSameAs(inner);
        }

        /// <summary>
        /// Verifies that the second constructor works with only a message parameter,
        /// defaulting innerException to null. When a string is passed as the sole argument,
        /// C# resolves to the second constructor (string message, Exception innerException = null).
        /// </summary>
        [Fact]
        public void Constructor_WithMessageOnly_NullInnerException()
        {
            // Act — resolves to second constructor: (string message, Exception innerException = null)
            var ex = new StorageException("error happened");

            // Assert
            ex.Message.Should().Be("error happened");
            ex.InnerException.Should().BeNull();
        }

        /// <summary>
        /// Verifies behavior when both message and innerException are explicitly null.
        /// Uses named parameters to ensure resolution to the second constructor.
        /// When null is passed as message to Exception base constructor, the Message
        /// property returns the .NET default message (not null).
        /// </summary>
        [Fact]
        public void Constructor_WithNullMessage_NullInner()
        {
            // Act — named parameters ensure resolution to the second constructor
            var ex = new StorageException(message: null, innerException: null);

            // Assert — .NET Exception returns default message when null is passed to base(null, null)
            // Default message is "Exception of type '...StorageException' was thrown."
            ex.Message.Should().NotBeNull();
            ex.InnerException.Should().BeNull();
        }

        /// <summary>
        /// Verifies behavior when message is null but innerException is provided.
        /// Resolves to the second constructor due to two arguments.
        /// </summary>
        [Fact]
        public void Constructor_WithNullMessage_WithInner()
        {
            // Arrange
            var inner = new Exception("inner msg");

            // Act — two arguments resolve to second constructor
            var ex = new StorageException(null, inner);

            // Assert — .NET Exception returns default message when null message is passed
            ex.Message.Should().NotBeNull();
            ex.InnerException.Should().BeSameAs(inner);
        }

        /// <summary>
        /// Verifies that an empty string message is preserved as-is (not converted to null
        /// or default message by the Exception base class).
        /// </summary>
        [Fact]
        public void Constructor_WithEmptyMessage()
        {
            // Act — empty string resolves to second constructor
            var ex = new StorageException("");

            // Assert — empty string is preserved (unlike null, which triggers default)
            ex.Message.Should().Be("");
        }

        #endregion

        #region Type-Based Catch Policy

        /// <summary>
        /// Verifies that StorageException can be caught by its specific type
        /// in a catch clause, confirming it works as a semantic exception filter.
        /// </summary>
        [Fact]
        public void CatchPolicy_CatchesStorageException()
        {
            // Arrange
            StorageException caught = null;

            // Act
            try
            {
                throw new StorageException("storage failure");
            }
            catch (StorageException ex)
            {
                caught = ex;
            }

            // Assert
            caught.Should().NotBeNull();
            caught.Message.Should().Be("storage failure");
        }

        /// <summary>
        /// Verifies that StorageException is also caught by a generic catch (Exception)
        /// clause, confirming it inherits from Exception.
        /// </summary>
        [Fact]
        public void CatchPolicy_CaughtAsBaseException()
        {
            // Arrange
            Exception caught = null;

            // Act
            try
            {
                throw new StorageException("storage failure");
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            // Assert
            caught.Should().NotBeNull();
            caught.Should().BeOfType<StorageException>();
        }

        /// <summary>
        /// Verifies the type hierarchy: StorageException is a subclass of Exception.
        /// </summary>
        [Fact]
        public void CatchPolicy_IsException()
        {
            // Act & Assert — StorageException inherits from Exception
            typeof(StorageException).IsSubclassOf(typeof(Exception)).Should().BeTrue();
        }

        #endregion

        #region Exception Property Propagation

        /// <summary>
        /// Verifies that InnerException is correctly propagated and accessible
        /// through the StorageException wrapper.
        /// </summary>
        [Fact]
        public void InnerException_Propagated_Correctly()
        {
            // Arrange
            var inner = new InvalidOperationException("database timeout");

            // Act
            var ex = new StorageException(inner);

            // Assert
            ex.InnerException.Should().NotBeNull();
            ex.InnerException.Should().BeSameAs(inner);
            ex.InnerException.Message.Should().Be("database timeout");
        }

        /// <summary>
        /// Verifies that when using the first constructor, the Message property
        /// is derived from the inner exception's Message via base(innerException.Message, ...).
        /// </summary>
        [Fact]
        public void Message_Propagated_FromInnerException()
        {
            // Arrange
            var inner = new InvalidOperationException("connection pool exhausted");

            // Act — first constructor: base(innerException.Message, innerException)
            var ex = new StorageException(inner);

            // Assert — Message is the inner exception's message
            ex.Message.Should().Be("connection pool exhausted");
        }

        /// <summary>
        /// Verifies that when using the second constructor, the Message property
        /// is set from the explicit message parameter via base(message, innerException).
        /// </summary>
        [Fact]
        public void Message_Propagated_FromExplicitMessage()
        {
            // Arrange
            var inner = new Exception("inner detail");

            // Act — second constructor: base(message, innerException)
            var ex = new StorageException("explicit storage error", inner);

            // Assert — Message is the explicitly provided string, not the inner's message
            ex.Message.Should().Be("explicit storage error");
        }

        #endregion
    }
}
