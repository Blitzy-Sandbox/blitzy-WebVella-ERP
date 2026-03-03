using Xunit;
using FluentAssertions;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.SharedKernel.Eql
{
	/// <summary>
	/// Unit tests for <see cref="EqlBuildResult"/> DTO which holds the output
	/// of the EQL compilation pipeline: parse errors, field metadata, SQL parameters,
	/// expected parameter names, the AST tree (internal), the resolved source entity,
	/// and the generated SQL string.
	///
	/// Test organization follows four phases:
	///   Phase 1 — Default value verification for all properties
	///   Phase 2 — Property behavior tests (private-set collection mutability, public set/get)
	///   Phase 3 — Internal Tree property tests (requires InternalsVisibleTo)
	///   Phase 4 — Integration-like tests with multiple items in collections
	/// </summary>
	public class EqlBuildResultTests
	{
		#region Phase 1: Default Value Tests

		/// <summary>
		/// Verifies that the Errors collection is initialized as a non-null, empty
		/// <see cref="List{EqlError}"/> by the field initializer, ensuring callers
		/// can safely add errors without null checks.
		/// </summary>
		[Fact]
		public void Test_Errors_InitializedAsEmptyList()
		{
			// Arrange & Act
			var result = new EqlBuildResult();

			// Assert
			result.Errors.Should().NotBeNull();
			result.Errors.Should().BeEmpty();
			result.Errors.Should().BeOfType<List<EqlError>>();
		}

		/// <summary>
		/// Verifies that the Meta collection is initialized as a non-null, empty
		/// <see cref="List{EqlFieldMeta}"/> by the field initializer, ensuring
		/// field projection metadata is safely accessible after construction.
		/// </summary>
		[Fact]
		public void Test_Meta_InitializedAsEmptyList()
		{
			// Arrange & Act
			var result = new EqlBuildResult();

			// Assert
			result.Meta.Should().NotBeNull();
			result.Meta.Should().BeEmpty();
			result.Meta.Should().BeOfType<List<EqlFieldMeta>>();
		}

		/// <summary>
		/// Verifies that the Parameters collection is initialized as a non-null,
		/// empty <see cref="List{EqlParameter}"/> by the field initializer, ensuring
		/// parameter bindings can be collected during EQL compilation.
		/// </summary>
		[Fact]
		public void Test_Parameters_InitializedAsEmptyList()
		{
			// Arrange & Act
			var result = new EqlBuildResult();

			// Assert
			result.Parameters.Should().NotBeNull();
			result.Parameters.Should().BeEmpty();
			result.Parameters.Should().BeOfType<List<EqlParameter>>();
		}

		/// <summary>
		/// Verifies that the ExpectedParameters collection is initialized as a
		/// non-null, empty <see cref="List{String}"/> by the field initializer,
		/// ensuring the EQL builder can track expected parameter names.
		/// </summary>
		[Fact]
		public void Test_ExpectedParameters_InitializedAsEmptyList()
		{
			// Arrange & Act
			var result = new EqlBuildResult();

			// Assert
			result.ExpectedParameters.Should().NotBeNull();
			result.ExpectedParameters.Should().BeEmpty();
			result.ExpectedParameters.Should().BeOfType<List<string>>();
		}

		/// <summary>
		/// Verifies that FromEntity defaults to null when no entity has been
		/// resolved during EQL compilation.
		/// </summary>
		[Fact]
		public void Test_FromEntity_DefaultNull()
		{
			// Arrange & Act
			var result = new EqlBuildResult();

			// Assert
			result.FromEntity.Should().BeNull();
		}

		/// <summary>
		/// Verifies that the Sql property defaults to null when no SQL has been
		/// generated. The source class has no explicit initializer for Sql, so the
		/// default value for a reference type (string) is null.
		/// </summary>
		[Fact]
		public void Test_Sql_DefaultNull()
		{
			// Arrange & Act
			var result = new EqlBuildResult();

			// Assert
			result.Sql.Should().BeNull();
		}

		#endregion

		#region Phase 2: Property Behavior Tests

		/// <summary>
		/// Verifies that although the Errors property has a private setter (preventing
		/// external reassignment of the list reference), the underlying List instance
		/// is mutable — callers can add <see cref="EqlError"/> items via Add().
		/// </summary>
		[Fact]
		public void Test_Errors_PrivateSet_CanAddItems()
		{
			// Arrange
			var result = new EqlBuildResult();
			var error = new EqlError { Message = "test compilation error" };

			// Act
			result.Errors.Add(error);

			// Assert
			result.Errors.Should().HaveCount(1);
			result.Errors[0].Message.Should().Be("test compilation error");
		}

		/// <summary>
		/// Verifies that although the Meta property has a private setter, the
		/// underlying List instance is mutable — callers can add
		/// <see cref="EqlFieldMeta"/> items via Add().
		/// </summary>
		[Fact]
		public void Test_Meta_PrivateSet_CanAddItems()
		{
			// Arrange
			var result = new EqlBuildResult();
			var fieldMeta = new EqlFieldMeta { Name = "test_field" };

			// Act
			result.Meta.Add(fieldMeta);

			// Assert
			result.Meta.Should().HaveCount(1);
			result.Meta[0].Name.Should().Be("test_field");
		}

		/// <summary>
		/// Verifies that although the Parameters property has a private setter,
		/// the underlying List instance is mutable — callers can add
		/// <see cref="EqlParameter"/> items via Add() using the two-argument constructor.
		/// </summary>
		[Fact]
		public void Test_Parameters_PrivateSet_CanAddItems()
		{
			// Arrange
			var result = new EqlBuildResult();
			var parameter = new EqlParameter("test", "value");

			// Act
			result.Parameters.Add(parameter);

			// Assert
			result.Parameters.Should().HaveCount(1);
			// EqlParameter auto-prefixes '@' if missing
			result.Parameters[0].ParameterName.Should().Be("@test");
		}

		/// <summary>
		/// Verifies that although the ExpectedParameters property has a private
		/// setter, the underlying List instance is mutable — callers can add
		/// string items via Add().
		/// </summary>
		[Fact]
		public void Test_ExpectedParameters_PrivateSet_CanAddItems()
		{
			// Arrange
			var result = new EqlBuildResult();

			// Act
			result.ExpectedParameters.Add("@param1");

			// Assert
			result.ExpectedParameters.Should().HaveCount(1);
			result.ExpectedParameters.Should().Contain("@param1");
		}

		/// <summary>
		/// Verifies that the FromEntity property supports setting a non-null
		/// <see cref="Entity"/> value and reading it back. The public setter
		/// allows direct assignment during EQL compilation.
		/// </summary>
		[Fact]
		public void Test_FromEntity_SetGet()
		{
			// Arrange
			var result = new EqlBuildResult();
			var entity = new Entity
			{
				Id = System.Guid.NewGuid(),
				Name = "test_entity",
				Label = "Test Entity"
			};

			// Act
			result.FromEntity = entity;

			// Assert
			result.FromEntity.Should().NotBeNull();
			result.FromEntity.Should().BeSameAs(entity);
			result.FromEntity.Name.Should().Be("test_entity");
		}

		/// <summary>
		/// Verifies that the Sql property supports setting a string value and
		/// reading it back. The public setter allows direct assignment after
		/// SQL generation completes.
		/// </summary>
		[Fact]
		public void Test_Sql_SetGet()
		{
			// Arrange
			var result = new EqlBuildResult();

			// Act
			result.Sql = "SELECT 1";

			// Assert
			result.Sql.Should().Be("SELECT 1");
		}

		#endregion

		#region Phase 3: Tree Property Tests (Internal — requires InternalsVisibleTo)

		/// <summary>
		/// Verifies that the internal Tree property defaults to null when
		/// no AST has been built. This test is accessible because the SharedKernel
		/// project declares InternalsVisibleTo for the test assembly.
		/// </summary>
		[Fact]
		public void Test_Tree_DefaultNull()
		{
			// Arrange & Act
			var result = new EqlBuildResult();

			// Assert — Tree is internal, accessible via InternalsVisibleTo
			result.Tree.Should().BeNull();
		}

		/// <summary>
		/// Verifies that the internal Tree property can be set to a non-null
		/// <see cref="EqlAbstractTree"/> instance and read back correctly.
		/// This test validates the internal set accessor and the round-trip
		/// behavior of the AST container.
		/// </summary>
		[Fact]
		public void Test_Tree_InternalSet()
		{
			// Arrange
			var result = new EqlBuildResult();
			var tree = new EqlAbstractTree
			{
				RootNode = new EqlSelectNode()
			};

			// Act — internal setter is accessible via InternalsVisibleTo
			result.Tree = tree;

			// Assert
			result.Tree.Should().NotBeNull();
			result.Tree.Should().BeSameAs(tree);
			result.Tree.RootNode.Should().NotBeNull();
			result.Tree.RootNode.Should().BeOfType<EqlSelectNode>();
		}

		#endregion

		#region Phase 4: Integration-like Behavior Tests

		/// <summary>
		/// Verifies that multiple <see cref="EqlError"/> objects can be added to
		/// the Errors collection, and that count, order, and individual error
		/// message content are preserved correctly.
		/// </summary>
		[Fact]
		public void Test_CanPopulateMultipleErrors()
		{
			// Arrange
			var result = new EqlBuildResult();
			var error1 = new EqlError { Message = "Syntax error at line 1" };
			var error2 = new EqlError { Message = "Unknown entity 'foo'" };
			var error3 = new EqlError { Message = "Missing parameter '@id'" };

			// Act
			result.Errors.Add(error1);
			result.Errors.Add(error2);
			result.Errors.Add(error3);

			// Assert
			result.Errors.Should().HaveCount(3);
			result.Errors[0].Message.Should().Be("Syntax error at line 1");
			result.Errors[1].Message.Should().Be("Unknown entity 'foo'");
			result.Errors[2].Message.Should().Be("Missing parameter '@id'");
			result.Errors.Should().Contain(error1);
			result.Errors.Should().Contain(error2);
			result.Errors.Should().Contain(error3);
		}

		/// <summary>
		/// Verifies that multiple <see cref="EqlParameter"/> objects can be added
		/// to the Parameters collection, and that count and parameter names are
		/// preserved correctly. Validates the collection with different parameter
		/// names and values.
		/// </summary>
		[Fact]
		public void Test_CanPopulateMultipleParameters()
		{
			// Arrange
			var result = new EqlBuildResult();
			var param1 = new EqlParameter("id", System.Guid.NewGuid());
			var param2 = new EqlParameter("name", "test_value");
			var param3 = new EqlParameter("@page", 1);

			// Act
			result.Parameters.Add(param1);
			result.Parameters.Add(param2);
			result.Parameters.Add(param3);

			// Assert
			result.Parameters.Should().HaveCount(3);
			// Verify auto-prefixing: "id" becomes "@id", "name" becomes "@name"
			result.Parameters[0].ParameterName.Should().Be("@id");
			result.Parameters[1].ParameterName.Should().Be("@name");
			// "@page" already has the prefix, so it stays "@page"
			result.Parameters[2].ParameterName.Should().Be("@page");
		}

		/// <summary>
		/// Verifies that multiple <see cref="EqlFieldMeta"/> objects can be added
		/// to the Meta collection, and that count and field names are preserved
		/// correctly. Validates the projection metadata collection with diverse
		/// field names.
		/// </summary>
		[Fact]
		public void Test_CanPopulateMultipleMeta()
		{
			// Arrange
			var result = new EqlBuildResult();
			var meta1 = new EqlFieldMeta { Name = "id" };
			var meta2 = new EqlFieldMeta { Name = "name" };
			var meta3 = new EqlFieldMeta { Name = "created_on" };

			// Act
			result.Meta.Add(meta1);
			result.Meta.Add(meta2);
			result.Meta.Add(meta3);

			// Assert
			result.Meta.Should().HaveCount(3);
			result.Meta[0].Name.Should().Be("id");
			result.Meta[1].Name.Should().Be("name");
			result.Meta[2].Name.Should().Be("created_on");
			result.Meta.Should().Contain(meta1);
			result.Meta.Should().Contain(meta2);
			result.Meta.Should().Contain(meta3);
		}

		#endregion
	}
}
