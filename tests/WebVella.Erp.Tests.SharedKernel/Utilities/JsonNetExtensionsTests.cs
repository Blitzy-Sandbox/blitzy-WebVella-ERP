using System;
using Newtonsoft.Json.Linq;
using Xunit;
using FluentAssertions;

namespace WebVella.Erp.Tests.SharedKernel.Utilities
{
	/// <summary>
	/// Comprehensive unit tests for the JToken.IsNullOrEmpty() extension method
	/// defined in Newtonsoft.Json.Linq namespace by the SharedKernel JsonNetExtensions class.
	///
	/// The extension returns true for exactly 5 conditions:
	///   1. C# null reference
	///   2. Empty JSON array (JTokenType.Array with no values)
	///   3. Empty JSON object (JTokenType.Object with no values)
	///   4. Empty string (JTokenType.String with ToString() == String.Empty)
	///   5. Explicit JSON null literal (JTokenType.Null)
	///
	/// All other inputs — including whitespace strings, non-empty values,
	/// arrays/objects with values, numeric types, booleans, and dates — return false.
	/// </summary>
	public class JsonNetExtensionsTests
	{
		// =====================================================================
		// TRUE CASES — IsNullOrEmpty should return true
		// =====================================================================

		/// <summary>
		/// Verifies that a null JToken reference is correctly identified as null-or-empty.
		/// This tests the first condition: token == null.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_NullTokenReference_ReturnsTrue()
		{
			// Arrange
			JToken token = null;

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeTrue("a null JToken reference should be considered null-or-empty");
		}

		/// <summary>
		/// Verifies that an empty JArray (no elements) is correctly identified as null-or-empty.
		/// This tests the second condition: JTokenType.Array && !HasValues.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_EmptyArray_ReturnsTrue()
		{
			// Arrange
			JToken token = new JArray();

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeTrue("an empty JSON array should be considered null-or-empty");
		}

		/// <summary>
		/// Verifies that an empty JObject (no properties) is correctly identified as null-or-empty.
		/// This tests the third condition: JTokenType.Object && !HasValues.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_EmptyObject_ReturnsTrue()
		{
			// Arrange
			JToken token = new JObject();

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeTrue("an empty JSON object should be considered null-or-empty");
		}

		/// <summary>
		/// Verifies that an empty string JValue is correctly identified as null-or-empty.
		/// This tests the fourth condition: JTokenType.String && ToString() == String.Empty.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_EmptyString_ReturnsTrue()
		{
			// Arrange
			JToken token = new JValue(String.Empty);

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeTrue("an empty string JValue should be considered null-or-empty");
		}

		/// <summary>
		/// Verifies that an explicit JSON null literal is correctly identified as null-or-empty.
		/// This tests the fifth condition: JTokenType.Null.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_ExplicitJsonNull_ReturnsTrue()
		{
			// Arrange
			JToken token = JValue.CreateNull();

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeTrue("an explicit JSON null value should be considered null-or-empty");
		}

		// =====================================================================
		// FALSE CASES — IsNullOrEmpty should return false
		// =====================================================================

		/// <summary>
		/// Verifies that a whitespace-only string is NOT considered null-or-empty.
		/// This is a key behavioral detail: the extension checks for String.Empty specifically,
		/// not for IsNullOrWhiteSpace. Whitespace strings have content and are not empty.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_WhitespaceString_ReturnsFalse()
		{
			// Arrange
			JToken token = new JValue("  ");

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeFalse("a whitespace-only string is NOT treated as empty by IsNullOrEmpty");
		}

		/// <summary>
		/// Verifies that a non-empty string value returns false.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_NonEmptyString_ReturnsFalse()
		{
			// Arrange
			JToken token = new JValue("hello");

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeFalse("a non-empty string should not be considered null-or-empty");
		}

		/// <summary>
		/// Verifies that an array containing elements returns false.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_NonEmptyArray_ReturnsFalse()
		{
			// Arrange
			JToken token = new JArray(1, 2, 3);

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeFalse("a non-empty array with values should not be considered null-or-empty");
		}

		/// <summary>
		/// Verifies that an object containing properties returns false.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_NonEmptyObject_ReturnsFalse()
		{
			// Arrange
			JToken token = JObject.Parse("{\"key\":\"value\"}");

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeFalse("a non-empty object with properties should not be considered null-or-empty");
		}

		/// <summary>
		/// Verifies that an integer JValue returns false.
		/// Integer types are not matched by any of the 5 true conditions.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_IntegerValue_ReturnsFalse()
		{
			// Arrange
			JToken token = new JValue(42);

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeFalse("an integer value should not be considered null-or-empty");
		}

		/// <summary>
		/// Verifies that a boolean JValue returns false.
		/// Boolean types are not matched by any of the 5 true conditions.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_BoolValue_ReturnsFalse()
		{
			// Arrange
			JToken token = new JValue(true);

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeFalse("a boolean value should not be considered null-or-empty");
		}

		/// <summary>
		/// Verifies that a zero integer value returns false.
		/// Zero is a valid numeric value and should not be treated as empty.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_ZeroValue_ReturnsFalse()
		{
			// Arrange
			JToken token = new JValue(0);

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeFalse("zero is a valid numeric value and should not be considered null-or-empty");
		}

		/// <summary>
		/// Verifies that a DateTime JValue returns false.
		/// Date types are not matched by any of the 5 true conditions.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_DateValue_ReturnsFalse()
		{
			// Arrange
			JToken token = new JValue(DateTime.Now);

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeFalse("a DateTime value should not be considered null-or-empty");
		}

		// =====================================================================
		// EDGE CASES — boundary conditions that may appear ambiguous
		// =====================================================================

		/// <summary>
		/// Verifies that an array containing a single null element returns false.
		/// Even though the element is null, the array itself HasValues (count > 0).
		/// The IsNullOrEmpty check evaluates the token itself, not its children.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_ArrayWithSingleNull_ReturnsFalse()
		{
			// Arrange
			JToken token = new JArray(JValue.CreateNull());

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeFalse("an array with a null element still has values and is not empty");
		}

		/// <summary>
		/// Verifies that an object with a property whose value is null returns false.
		/// The object has a property (HasValues is true), even though the property's value is null.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_ObjectWithNullProperty_ReturnsFalse()
		{
			// Arrange
			JToken token = JObject.Parse("{\"key\":null}");

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeFalse("an object with a null-valued property still has properties and is not empty");
		}

		/// <summary>
		/// Verifies that an array containing a nested empty array returns false.
		/// The outer array has values (the inner empty array is a child element), so HasValues is true.
		/// Only the outer token is evaluated, not its children recursively.
		/// Note: We use JToken.Parse("[[]]") because the JArray(JArray) constructor is a copy constructor
		/// that copies content from the parameter rather than nesting it as a child element.
		/// </summary>
		[Fact]
		public void IsNullOrEmpty_NestedEmptyArray_ReturnsFalse()
		{
			// Arrange — parse JSON literal to get a true nested structure: an array containing one empty array
			JToken token = JToken.Parse("[[]]");

			// Act
			bool result = token.IsNullOrEmpty();

			// Assert
			result.Should().BeFalse("an array containing a nested empty array still has values at the top level");
		}
	}
}
