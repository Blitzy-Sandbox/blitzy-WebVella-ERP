using Xunit;
using FluentAssertions;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Models;
using Moq;

namespace WebVella.Erp.Tests.SharedKernel.Eql
{
    /// <summary>
    /// Unit tests for <see cref="EqlFieldMeta"/> — the EQL hierarchical projection metadata
    /// class describing field structure for result materialization. This class is critical
    /// for the EQL engine's ability to map query results to structured output.
    /// </summary>
    public class EqlFieldMetaTests
    {
        #region Phase 1: Default Value Tests

        /// <summary>
        /// Verifies that the Name property defaults to null when a new EqlFieldMeta is created.
        /// String properties in C# default to null unless explicitly initialized.
        /// </summary>
        [Fact]
        public void Test_Name_DefaultNull()
        {
            // Arrange & Act
            var meta = new EqlFieldMeta();

            // Assert
            meta.Name.Should().BeNull();
        }

        /// <summary>
        /// Verifies that the Field property defaults to null when a new EqlFieldMeta is created.
        /// Source code explicitly initializes: public Field Field { get; set; } = null;
        /// </summary>
        [Fact]
        public void Test_Field_DefaultNull()
        {
            // Arrange & Act
            var meta = new EqlFieldMeta();

            // Assert
            meta.Field.Should().BeNull();
        }

        /// <summary>
        /// Verifies that the Relation property defaults to null when a new EqlFieldMeta is created.
        /// Source code explicitly initializes: public EntityRelation Relation { get; set; } = null;
        /// </summary>
        [Fact]
        public void Test_Relation_DefaultNull()
        {
            // Arrange & Act
            var meta = new EqlFieldMeta();

            // Assert
            meta.Relation.Should().BeNull();
        }

        /// <summary>
        /// Verifies that the Children property is initialized as a non-null, empty List&lt;EqlFieldMeta&gt;.
        /// Source code: public List&lt;EqlFieldMeta&gt; Children { get; private set; } = new List&lt;EqlFieldMeta&gt;();
        /// The private set prevents reassignment while allowing mutation of the list contents.
        /// </summary>
        [Fact]
        public void Test_Children_InitializedAsEmptyList()
        {
            // Arrange & Act
            var meta = new EqlFieldMeta();

            // Assert
            meta.Children.Should().NotBeNull();
            meta.Children.Should().BeEmpty();
            meta.Children.Should().BeOfType<List<EqlFieldMeta>>();
        }

        #endregion

        #region Phase 2: Property Set/Get Tests

        /// <summary>
        /// Verifies that the Name property can be set and read back correctly.
        /// Name is a simple string auto-property with public get and set.
        /// </summary>
        [Fact]
        public void Test_Name_SetGet()
        {
            // Arrange
            var meta = new EqlFieldMeta();

            // Act
            meta.Name = "test_field";

            // Assert
            meta.Name.Should().Be("test_field");
        }

        /// <summary>
        /// Verifies that the Field property can be set to a non-null value and read back correctly.
        /// Field is an abstract class in SharedKernel.Models, so we use Moq to create a mock instance.
        /// This property links EqlFieldMeta to the entity field metadata it represents.
        /// </summary>
        [Fact]
        public void Test_Field_SetGet()
        {
            // Arrange
            var meta = new EqlFieldMeta();
            var mockField = new Mock<Field>();
            mockField.Object.Name = "mock_field";

            // Act
            meta.Field = mockField.Object;

            // Assert
            meta.Field.Should().NotBeNull();
            meta.Field.Should().BeSameAs(mockField.Object);
            meta.Field.Name.Should().Be("mock_field");
        }

        /// <summary>
        /// Verifies that the Relation property can be set to a non-null value and read back correctly.
        /// EntityRelation is a concrete class in SharedKernel.Models with many properties;
        /// we set a few key ones to confirm the reference is properly stored and retrieved.
        /// </summary>
        [Fact]
        public void Test_Relation_SetGet()
        {
            // Arrange
            var meta = new EqlFieldMeta();
            var relation = new EntityRelation
            {
                Name = "test_relation",
                Label = "Test Relation"
            };

            // Act
            meta.Relation = relation;

            // Assert
            meta.Relation.Should().NotBeNull();
            meta.Relation.Should().BeSameAs(relation);
            meta.Relation.Name.Should().Be("test_relation");
            meta.Relation.Label.Should().Be("Test Relation");
        }

        #endregion

        #region Phase 3: Children Collection Tests

        /// <summary>
        /// Verifies that items can be added to the Children list via .Add() even though
        /// the set accessor is private. The private set prevents reassigning the list reference,
        /// but the list contents remain mutable — this is the intended design for building
        /// the hierarchical field projection tree.
        /// </summary>
        [Fact]
        public void Test_Children_PrivateSet_CanAddItems()
        {
            // Arrange
            var parent = new EqlFieldMeta { Name = "parent" };
            var child = new EqlFieldMeta { Name = "child1" };

            // Act
            parent.Children.Add(child);

            // Assert
            parent.Children.Should().HaveCount(1);
            parent.Children[0].Name.Should().Be("child1");
        }

        /// <summary>
        /// Verifies that the hierarchical structure is preserved when building a parent → child → grandchild
        /// tree. This tests the core use case of EqlFieldMeta: representing nested relation field
        /// projections in EQL query results (e.g., $relation.field → parent with child for field).
        /// </summary>
        [Fact]
        public void Test_Children_HierarchicalStructure()
        {
            // Arrange
            var parent = new EqlFieldMeta { Name = "parent" };
            var child = new EqlFieldMeta { Name = "child1" };
            var grandchild = new EqlFieldMeta { Name = "grandchild1" };

            // Act — build the tree bottom-up
            child.Children.Add(grandchild);
            parent.Children.Add(child);

            // Assert — verify the full three-level hierarchy
            parent.Children.Should().HaveCount(1);
            parent.Children[0].Name.Should().Be("child1");

            parent.Children[0].Children.Should().HaveCount(1);
            parent.Children[0].Children[0].Name.Should().Be("grandchild1");

            // Verify grandchild has no children (leaf node)
            parent.Children[0].Children[0].Children.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that multiple children can be added to a single parent and that
        /// the insertion order is preserved. This tests the scenario of an entity
        /// projection selecting multiple fields (e.g., SELECT field1, field2, field3).
        /// </summary>
        [Fact]
        public void Test_Children_MultipleChildren()
        {
            // Arrange
            var parent = new EqlFieldMeta { Name = "parent" };
            var child1 = new EqlFieldMeta { Name = "child1" };
            var child2 = new EqlFieldMeta { Name = "child2" };
            var child3 = new EqlFieldMeta { Name = "child3" };

            // Act
            parent.Children.Add(child1);
            parent.Children.Add(child2);
            parent.Children.Add(child3);

            // Assert — verify count and order preservation
            parent.Children.Should().HaveCount(3);
            parent.Children[0].Name.Should().Be("child1");
            parent.Children[1].Name.Should().Be("child2");
            parent.Children[2].Name.Should().Be("child3");
        }

        #endregion

        #region Phase 4: ToString Override Tests

        /// <summary>
        /// Verifies that ToString() returns the Name property value.
        /// Source implementation: public override string ToString() { return Name; }
        /// This is used for debugging and logging of EQL field projection metadata.
        /// </summary>
        [Fact]
        public void Test_ToString_ReturnsName()
        {
            // Arrange
            var meta = new EqlFieldMeta { Name = "test_field" };

            // Act
            var result = meta.ToString();

            // Assert
            result.Should().Be("test_field");
        }

        /// <summary>
        /// Verifies that ToString() returns null when Name is null (default state).
        /// Since ToString() simply returns Name, a null Name results in null output.
        /// </summary>
        [Fact]
        public void Test_ToString_NullName_ReturnsNull()
        {
            // Arrange
            var meta = new EqlFieldMeta();
            // Name is null by default

            // Act
            var result = meta.ToString();

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that ToString() returns an empty string when Name is set to "".
        /// This confirms that ToString() faithfully returns the Name value without
        /// any null-coalescing or fallback behavior.
        /// </summary>
        [Fact]
        public void Test_ToString_EmptyName_ReturnsEmpty()
        {
            // Arrange
            var meta = new EqlFieldMeta { Name = "" };

            // Act
            var result = meta.ToString();

            // Assert
            result.Should().BeEmpty();
        }

        #endregion
    }
}
