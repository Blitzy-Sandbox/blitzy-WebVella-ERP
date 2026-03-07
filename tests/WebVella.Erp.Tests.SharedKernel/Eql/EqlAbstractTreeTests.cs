using System.Collections.Generic;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Eql;
using Xunit;

namespace WebVella.Erp.Tests.SharedKernel.Eql
{
    /// <summary>
    /// Comprehensive unit tests for all 19 EQL AST node types defined in
    /// <see cref="EqlAbstractTree"/> and related classes. Tests verify:
    /// - Node type enum values via the virtual Type property
    /// - Default property initialization
    /// - Property set/get round-trip behavior
    /// - Collection initialization and mutation
    /// - Class inheritance hierarchy
    /// - Polymorphic operand assignments
    /// </summary>
    public class EqlAbstractTreeTests
    {
        #region Phase 1: EqlAbstractTree Tests

        /// <summary>
        /// Verifies that a newly constructed EqlAbstractTree has its RootNode
        /// property defaulting to null (no AST root assigned).
        /// </summary>
        [Fact]
        public void Test_EqlAbstractTree_DefaultRootNode_IsNull()
        {
            // Arrange & Act
            var tree = new EqlAbstractTree();

            // Assert
            tree.RootNode.Should().BeNull();
        }

        /// <summary>
        /// Verifies that setting the RootNode property to an EqlSelectNode
        /// persists the value and can be read back correctly.
        /// </summary>
        [Fact]
        public void Test_EqlAbstractTree_SetRootNode_Persists()
        {
            // Arrange
            var tree = new EqlAbstractTree();
            var selectNode = new EqlSelectNode();

            // Act
            tree.RootNode = selectNode;

            // Assert
            tree.RootNode.Should().NotBeNull();
            tree.RootNode.Should().BeSameAs(selectNode);
        }

        #endregion

        #region Phase 2: Base EqlNode Tests

        /// <summary>
        /// Verifies that the base EqlNode class returns the default EqlNodeType value
        /// (Keyword, which is the first enum member with value 0) when its virtual
        /// Type property is not overridden.
        /// </summary>
        [Fact]
        public void Test_EqlNode_DefaultType()
        {
            // Arrange & Act
            var node = new EqlNode();

            // Assert — default(EqlNodeType) is Keyword (value 0, first enum member)
            node.Type.Should().Be(EqlNodeType.Keyword);
        }

        #endregion

        #region Phase 3: EqlKeywordNode Tests

        /// <summary>
        /// Verifies that EqlKeywordNode.Type returns EqlNodeType.Keyword.
        /// </summary>
        [Fact]
        public void Test_EqlKeywordNode_Type_IsKeyword()
        {
            // Arrange & Act
            var node = new EqlKeywordNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.Keyword);
        }

        /// <summary>
        /// Verifies that setting Keyword property to "null" (the string literal)
        /// persists and reads back correctly.
        /// </summary>
        [Fact]
        public void Test_EqlKeywordNode_Keyword_SetGet()
        {
            // Arrange
            var node = new EqlKeywordNode();

            // Act
            node.Keyword = "null";

            // Assert
            node.Keyword.Should().Be("null");
        }

        /// <summary>
        /// Verifies that the Keyword property defaults to null on a new instance.
        /// </summary>
        [Fact]
        public void Test_EqlKeywordNode_Keyword_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlKeywordNode();

            // Assert
            node.Keyword.Should().BeNull();
        }

        #endregion

        #region Phase 4: EqlNumberValueNode Tests

        /// <summary>
        /// Verifies that EqlNumberValueNode.Type returns EqlNodeType.NumberValue.
        /// </summary>
        [Fact]
        public void Test_EqlNumberValueNode_Type_IsNumberValue()
        {
            // Arrange & Act
            var node = new EqlNumberValueNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.NumberValue);
        }

        /// <summary>
        /// Verifies that setting Number to 42.5m persists and reads back correctly.
        /// </summary>
        [Fact]
        public void Test_EqlNumberValueNode_Number_SetGet()
        {
            // Arrange
            var node = new EqlNumberValueNode();

            // Act
            node.Number = 42.5m;

            // Assert
            node.Number.Should().Be(42.5m);
        }

        /// <summary>
        /// Verifies that the Number property defaults to 0m (decimal default value).
        /// </summary>
        [Fact]
        public void Test_EqlNumberValueNode_Number_DefaultZero()
        {
            // Arrange & Act
            var node = new EqlNumberValueNode();

            // Assert
            node.Number.Should().Be(0m);
        }

        #endregion

        #region Phase 5: EqlTextValueNode Tests

        /// <summary>
        /// Verifies that EqlTextValueNode.Type returns EqlNodeType.TextValue.
        /// </summary>
        [Fact]
        public void Test_EqlTextValueNode_Type_IsTextValue()
        {
            // Arrange & Act
            var node = new EqlTextValueNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.TextValue);
        }

        /// <summary>
        /// Verifies that setting Text to "hello" persists and reads back correctly.
        /// </summary>
        [Fact]
        public void Test_EqlTextValueNode_Text_SetGet()
        {
            // Arrange
            var node = new EqlTextValueNode();

            // Act
            node.Text = "hello";

            // Assert
            node.Text.Should().Be("hello");
        }

        #endregion

        #region Phase 6: EqlArgumentValueNode Tests

        /// <summary>
        /// Verifies that EqlArgumentValueNode.Type returns EqlNodeType.ArgumentValue.
        /// </summary>
        [Fact]
        public void Test_EqlArgumentValueNode_Type_IsArgumentValue()
        {
            // Arrange & Act
            var node = new EqlArgumentValueNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.ArgumentValue);
        }

        /// <summary>
        /// Verifies that setting ArgumentName to "param1" persists and reads back.
        /// </summary>
        [Fact]
        public void Test_EqlArgumentValueNode_ArgumentName_SetGet()
        {
            // Arrange
            var node = new EqlArgumentValueNode();

            // Act
            node.ArgumentName = "param1";

            // Assert
            node.ArgumentName.Should().Be("param1");
        }

        #endregion

        #region Phase 7: EqlSelectNode Tests

        /// <summary>
        /// Verifies that EqlSelectNode.Type returns EqlNodeType.Select.
        /// </summary>
        [Fact]
        public void Test_EqlSelectNode_Type_IsSelect()
        {
            // Arrange & Act
            var node = new EqlSelectNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.Select);
        }

        /// <summary>
        /// Verifies that EqlSelectNode.Fields is initialized to a non-null empty
        /// List&lt;EqlFieldNode&gt; by default.
        /// </summary>
        [Fact]
        public void Test_EqlSelectNode_Fields_InitializedEmpty()
        {
            // Arrange & Act
            var node = new EqlSelectNode();

            // Assert
            node.Fields.Should().NotBeNull();
            node.Fields.Should().BeEmpty();
            node.Fields.Should().BeOfType<List<EqlFieldNode>>();
        }

        /// <summary>
        /// Verifies that the Fields list has a private setter but items can still
        /// be added to the collection via the public Add method on the list instance.
        /// </summary>
        [Fact]
        public void Test_EqlSelectNode_Fields_PrivateSet()
        {
            // Arrange
            var node = new EqlSelectNode();
            var field = new EqlFieldNode { FieldName = "test_field" };

            // Act — Fields has private set, but the list itself is mutable
            node.Fields.Add(field);

            // Assert
            node.Fields.Should().HaveCount(1);
            node.Fields[0].FieldName.Should().Be("test_field");
        }

        /// <summary>
        /// Verifies that EqlSelectNode.From defaults to null.
        /// </summary>
        [Fact]
        public void Test_EqlSelectNode_From_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlSelectNode();

            // Assert
            node.From.Should().BeNull();
        }

        /// <summary>
        /// Verifies that EqlSelectNode.Where defaults to null.
        /// </summary>
        [Fact]
        public void Test_EqlSelectNode_Where_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlSelectNode();

            // Assert
            node.Where.Should().BeNull();
        }

        /// <summary>
        /// Verifies that EqlSelectNode.OrderBy defaults to null.
        /// </summary>
        [Fact]
        public void Test_EqlSelectNode_OrderBy_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlSelectNode();

            // Assert
            node.OrderBy.Should().BeNull();
        }

        /// <summary>
        /// Verifies that EqlSelectNode.Page defaults to null.
        /// </summary>
        [Fact]
        public void Test_EqlSelectNode_Page_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlSelectNode();

            // Assert
            node.Page.Should().BeNull();
        }

        /// <summary>
        /// Verifies that EqlSelectNode.PageSize defaults to null.
        /// </summary>
        [Fact]
        public void Test_EqlSelectNode_PageSize_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlSelectNode();

            // Assert
            node.PageSize.Should().BeNull();
        }

        #endregion

        #region Phase 8: EqlFieldNode Tests

        /// <summary>
        /// Verifies that EqlFieldNode.Type returns EqlNodeType.Field.
        /// </summary>
        [Fact]
        public void Test_EqlFieldNode_Type_IsField()
        {
            // Arrange & Act
            var node = new EqlFieldNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.Field);
        }

        /// <summary>
        /// Verifies that setting FieldName to "test_field" persists and reads back.
        /// </summary>
        [Fact]
        public void Test_EqlFieldNode_FieldName_SetGet()
        {
            // Arrange
            var node = new EqlFieldNode();

            // Act
            node.FieldName = "test_field";

            // Assert
            node.FieldName.Should().Be("test_field");
        }

        #endregion

        #region Phase 9: EqlRelationInfo Tests

        /// <summary>
        /// Verifies that setting Name to "rel1" on EqlRelationInfo persists and reads back.
        /// </summary>
        [Fact]
        public void Test_EqlRelationInfo_Name_SetGet()
        {
            // Arrange
            var info = new EqlRelationInfo();

            // Act
            info.Name = "rel1";

            // Assert
            info.Name.Should().Be("rel1");
        }

        /// <summary>
        /// Verifies that Direction can be set to TargetOrigin and read back correctly.
        /// </summary>
        [Fact]
        public void Test_EqlRelationInfo_Direction_TargetOrigin()
        {
            // Arrange
            var info = new EqlRelationInfo();

            // Act
            info.Direction = EqlRelationDirectionType.TargetOrigin;

            // Assert
            info.Direction.Should().Be(EqlRelationDirectionType.TargetOrigin);
        }

        /// <summary>
        /// Verifies that Direction can be set to OriginTarget and read back correctly.
        /// </summary>
        [Fact]
        public void Test_EqlRelationInfo_Direction_OriginTarget()
        {
            // Arrange
            var info = new EqlRelationInfo();

            // Act
            info.Direction = EqlRelationDirectionType.OriginTarget;

            // Assert
            info.Direction.Should().Be(EqlRelationDirectionType.OriginTarget);
        }

        #endregion

        #region Phase 10: EqlRelationFieldNode Tests

        /// <summary>
        /// Verifies that EqlRelationFieldNode.Type returns EqlNodeType.RelationField.
        /// </summary>
        [Fact]
        public void Test_EqlRelationFieldNode_Type_IsRelationField()
        {
            // Arrange & Act
            var node = new EqlRelationFieldNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.RelationField);
        }

        /// <summary>
        /// Verifies that EqlRelationFieldNode.Relations is initialized to a non-null
        /// empty List&lt;EqlRelationInfo&gt;.
        /// </summary>
        [Fact]
        public void Test_EqlRelationFieldNode_Relations_InitializedEmpty()
        {
            // Arrange & Act
            var node = new EqlRelationFieldNode();

            // Assert
            node.Relations.Should().NotBeNull();
            node.Relations.Should().BeEmpty();
            node.Relations.Should().BeOfType<List<EqlRelationInfo>>();
        }

        /// <summary>
        /// Verifies that items can be added to the Relations list (private set but
        /// the list instance itself is mutable via Add).
        /// </summary>
        [Fact]
        public void Test_EqlRelationFieldNode_Relations_PrivateSet_CanAdd()
        {
            // Arrange
            var node = new EqlRelationFieldNode();
            var relation = new EqlRelationInfo
            {
                Name = "test_relation",
                Direction = EqlRelationDirectionType.TargetOrigin
            };

            // Act
            node.Relations.Add(relation);

            // Assert
            node.Relations.Should().HaveCount(1);
            node.Relations[0].Name.Should().Be("test_relation");
            node.Relations[0].Direction.Should().Be(EqlRelationDirectionType.TargetOrigin);
        }

        /// <summary>
        /// Verifies that EqlRelationFieldNode inherits from EqlFieldNode,
        /// confirming it has access to the FieldName property.
        /// </summary>
        [Fact]
        public void Test_EqlRelationFieldNode_InheritsFromEqlFieldNode()
        {
            // Arrange & Act
            var node = new EqlRelationFieldNode();

            // Assert — the node should be assignable to EqlFieldNode
            node.Should().BeAssignableTo<EqlFieldNode>();
            // Verify the inherited FieldName property is accessible
            node.FieldName = "inherited_field";
            node.FieldName.Should().Be("inherited_field");
        }

        #endregion

        #region Phase 11: EqlRelationWildcardFieldNode Tests

        /// <summary>
        /// Verifies that EqlRelationWildcardFieldNode.Type returns EqlNodeType.RelationWildcardField.
        /// </summary>
        [Fact]
        public void Test_EqlRelationWildcardFieldNode_Type_IsRelationWildcardField()
        {
            // Arrange & Act
            var node = new EqlRelationWildcardFieldNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.RelationWildcardField);
        }

        /// <summary>
        /// Verifies that EqlRelationWildcardFieldNode inherits from EqlRelationFieldNode,
        /// confirming it has access to the Relations collection property.
        /// </summary>
        [Fact]
        public void Test_EqlRelationWildcardFieldNode_InheritsFromRelationFieldNode()
        {
            // Arrange & Act
            var node = new EqlRelationWildcardFieldNode();

            // Assert — should be assignable to EqlRelationFieldNode
            node.Should().BeAssignableTo<EqlRelationFieldNode>();
            // Verify the inherited Relations property is accessible and initialized
            node.Relations.Should().NotBeNull();
            node.Relations.Should().BeEmpty();
        }

        #endregion

        #region Phase 12: EqlWildcardFieldNode Tests

        /// <summary>
        /// Verifies that EqlWildcardFieldNode.Type returns EqlNodeType.WildcardField.
        /// </summary>
        [Fact]
        public void Test_EqlWildcardFieldNode_Type_IsWildcardField()
        {
            // Arrange & Act
            var node = new EqlWildcardFieldNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.WildcardField);
        }

        /// <summary>
        /// Verifies that EqlWildcardFieldNode inherits from EqlFieldNode,
        /// confirming it has access to the FieldName property.
        /// </summary>
        [Fact]
        public void Test_EqlWildcardFieldNode_InheritsFromFieldNode()
        {
            // Arrange & Act
            var node = new EqlWildcardFieldNode();

            // Assert — should be assignable to EqlFieldNode
            node.Should().BeAssignableTo<EqlFieldNode>();
            // Verify the inherited FieldName property is accessible
            node.FieldName = "wildcard_field";
            node.FieldName.Should().Be("wildcard_field");
        }

        #endregion

        #region Phase 13: EqlFromNode Tests

        /// <summary>
        /// Verifies that EqlFromNode.Type returns EqlNodeType.From.
        /// </summary>
        [Fact]
        public void Test_EqlFromNode_Type_IsFrom()
        {
            // Arrange & Act
            var node = new EqlFromNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.From);
        }

        /// <summary>
        /// Verifies that setting EntityName to "my_entity" persists and reads back.
        /// </summary>
        [Fact]
        public void Test_EqlFromNode_EntityName_SetGet()
        {
            // Arrange
            var node = new EqlFromNode();

            // Act
            node.EntityName = "my_entity";

            // Assert
            node.EntityName.Should().Be("my_entity");
        }

        #endregion

        #region Phase 14: EqlOrderByNode Tests

        /// <summary>
        /// Verifies that EqlOrderByNode.Type returns EqlNodeType.OrderBy.
        /// </summary>
        [Fact]
        public void Test_EqlOrderByNode_Type_IsOrderBy()
        {
            // Arrange & Act
            var node = new EqlOrderByNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.OrderBy);
        }

        /// <summary>
        /// Verifies that EqlOrderByNode.Fields is initialized to a non-null empty
        /// List&lt;EqlOrderByFieldNode&gt; (private set).
        /// </summary>
        [Fact]
        public void Test_EqlOrderByNode_Fields_InitializedEmpty()
        {
            // Arrange & Act
            var node = new EqlOrderByNode();

            // Assert
            node.Fields.Should().NotBeNull();
            node.Fields.Should().BeEmpty();
            node.Fields.Should().BeOfType<List<EqlOrderByFieldNode>>();
        }

        #endregion

        #region Phase 15: EqlOrderByFieldNode Tests

        /// <summary>
        /// Verifies that EqlOrderByFieldNode.Type returns EqlNodeType.OrderByField.
        /// </summary>
        [Fact]
        public void Test_EqlOrderByFieldNode_Type_IsOrderByField()
        {
            // Arrange & Act
            var node = new EqlOrderByFieldNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.OrderByField);
        }

        /// <summary>
        /// Verifies that EqlOrderByFieldNode.FieldName defaults to null.
        /// </summary>
        [Fact]
        public void Test_EqlOrderByFieldNode_FieldName_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlOrderByFieldNode();

            // Assert
            node.FieldName.Should().BeNull();
        }

        /// <summary>
        /// Verifies that EqlOrderByFieldNode.Direction defaults to null.
        /// </summary>
        [Fact]
        public void Test_EqlOrderByFieldNode_Direction_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlOrderByFieldNode();

            // Assert
            node.Direction.Should().BeNull();
        }

        /// <summary>
        /// Verifies that setting Direction to "ASC" persists and reads back correctly.
        /// </summary>
        [Fact]
        public void Test_EqlOrderByFieldNode_Direction_SetGet()
        {
            // Arrange
            var node = new EqlOrderByFieldNode();

            // Act
            node.Direction = "ASC";

            // Assert
            node.Direction.Should().Be("ASC");
        }

        #endregion

        #region Phase 16: EqlPageNode / EqlPageSizeNode Tests

        /// <summary>
        /// Verifies that EqlPageNode.Type returns EqlNodeType.Page.
        /// </summary>
        [Fact]
        public void Test_EqlPageNode_Type_IsPage()
        {
            // Arrange & Act
            var node = new EqlPageNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.Page);
        }

        /// <summary>
        /// Verifies that EqlPageNode.Number defaults to null (nullable decimal).
        /// </summary>
        [Fact]
        public void Test_EqlPageNode_Number_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlPageNode();

            // Assert
            node.Number.Should().BeNull();
        }

        /// <summary>
        /// Verifies that EqlPageSizeNode.Type returns EqlNodeType.PageSize.
        /// </summary>
        [Fact]
        public void Test_EqlPageSizeNode_Type_IsPageSize()
        {
            // Arrange & Act
            var node = new EqlPageSizeNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.PageSize);
        }

        /// <summary>
        /// Verifies that EqlPageSizeNode.Number defaults to null (nullable decimal).
        /// </summary>
        [Fact]
        public void Test_EqlPageSizeNode_Number_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlPageSizeNode();

            // Assert
            node.Number.Should().BeNull();
        }

        #endregion

        #region Phase 17: EqlWhereNode Tests

        /// <summary>
        /// Verifies that EqlWhereNode.Type returns EqlNodeType.Where.
        /// </summary>
        [Fact]
        public void Test_EqlWhereNode_Type_IsWhere()
        {
            // Arrange & Act
            var node = new EqlWhereNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.Where);
        }

        /// <summary>
        /// Verifies that EqlWhereNode.RootExpressionNode defaults to null.
        /// </summary>
        [Fact]
        public void Test_EqlWhereNode_RootExpressionNode_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlWhereNode();

            // Assert
            node.RootExpressionNode.Should().BeNull();
        }

        #endregion

        #region Phase 18: EqlBinaryExpressionNode Tests

        /// <summary>
        /// Verifies that EqlBinaryExpressionNode.Type returns EqlNodeType.BinaryExpression.
        /// </summary>
        [Fact]
        public void Test_EqlBinaryExpressionNode_Type_IsBinaryExpression()
        {
            // Arrange & Act
            var node = new EqlBinaryExpressionNode();

            // Assert
            node.Type.Should().Be(EqlNodeType.BinaryExpression);
        }

        /// <summary>
        /// Verifies that EqlBinaryExpressionNode.Operator defaults to null.
        /// </summary>
        [Fact]
        public void Test_EqlBinaryExpressionNode_Operator_DefaultNull()
        {
            // Arrange & Act
            var node = new EqlBinaryExpressionNode();

            // Assert
            node.Operator.Should().BeNull();
        }

        /// <summary>
        /// Verifies that setting FirstOperand and SecondOperand to EqlNode instances
        /// persists and reads back correctly.
        /// </summary>
        [Fact]
        public void Test_EqlBinaryExpressionNode_Operands_SetGet()
        {
            // Arrange
            var node = new EqlBinaryExpressionNode();
            var first = new EqlFieldNode { FieldName = "field_a" };
            var second = new EqlTextValueNode { Text = "value_b" };

            // Act
            node.Operator = "=";
            node.FirstOperand = first;
            node.SecondOperand = second;

            // Assert
            node.Operator.Should().Be("=");
            node.FirstOperand.Should().BeSameAs(first);
            node.SecondOperand.Should().BeSameAs(second);
        }

        /// <summary>
        /// Verifies that FirstOperand and SecondOperand accept polymorphic EqlNode
        /// subclasses — FirstOperand as EqlFieldNode and SecondOperand as
        /// EqlTextValueNode — confirming the property type is the base EqlNode.
        /// </summary>
        [Fact]
        public void Test_EqlBinaryExpressionNode_PolymorphicOperands()
        {
            // Arrange
            var node = new EqlBinaryExpressionNode();
            var fieldOperand = new EqlFieldNode { FieldName = "status" };
            var textOperand = new EqlTextValueNode { Text = "active" };

            // Act
            node.FirstOperand = fieldOperand;
            node.SecondOperand = textOperand;

            // Assert — verify polymorphic assignment
            node.FirstOperand.Should().BeOfType<EqlFieldNode>();
            node.SecondOperand.Should().BeOfType<EqlTextValueNode>();
            // Verify the concrete types retain their properties
            ((EqlFieldNode)node.FirstOperand).FieldName.Should().Be("status");
            ((EqlTextValueNode)node.SecondOperand).Text.Should().Be("active");
            // Verify the Type property returns the correct subclass enum
            node.FirstOperand.Type.Should().Be(EqlNodeType.Field);
            node.SecondOperand.Type.Should().Be(EqlNodeType.TextValue);
        }

        #endregion
    }
}
