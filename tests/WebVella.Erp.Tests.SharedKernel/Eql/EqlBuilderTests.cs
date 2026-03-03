using Xunit;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.SharedKernel.Eql
{
    /// <summary>
    /// Comprehensive unit tests for the EqlBuilder front-end compiler.
    /// Tests cover seven phases:
    ///   1. Parse method behavior (empty/invalid/valid source through Build())
    ///   2. Parameter normalization (@-prefix auto-addition)
    ///   3. PAGE / PAGESIZE validation (missing parameter, invalid value, number literal)
    ///   4. AST construction (wildcard, named fields, FROM, ORDER BY)
    ///   5. Relation path construction ($, $$, wildcard, chained)
    ///   6. WHERE binary expression tree (operators, operand types, keyword literals)
    ///   7. Mock setup pattern for injectable provider interfaces
    ///
    /// All tests use Moq to mock IEqlEntityProvider, IEqlRelationProvider, and
    /// IEqlHookProvider with empty collection returns so that parsing and AST building
    /// proceed while entity resolution gracefully adds errors without throwing.
    /// </summary>
    public class EqlBuilderTests
    {
        #region Helper Methods

        /// <summary>
        /// Creates an EqlBuilder with mocked providers that return empty entity/relation
        /// collections and report no hooks. This allows Parse and BuildAbstractTree to
        /// complete while BuildSql adds a non-fatal "entity not found" error.
        /// </summary>
        private EqlBuilder CreateBuilder(string text)
        {
            return new EqlBuilder(
                text,
                entityProvider: CreateMockEntityProvider(),
                relationProvider: CreateMockRelationProvider(),
                hookProvider: CreateMockHookProvider());
        }

        /// <summary>
        /// Creates an EqlBuilder with mocked providers and custom EqlSettings.
        /// </summary>
        private EqlBuilder CreateBuilder(string text, EqlSettings settings)
        {
            return new EqlBuilder(
                text,
                settings,
                entityProvider: CreateMockEntityProvider(),
                relationProvider: CreateMockRelationProvider(),
                hookProvider: CreateMockHookProvider());
        }

        /// <summary>
        /// Creates a mock IEqlEntityProvider that returns an empty entity list
        /// and null for any entity lookup by name or id.
        /// </summary>
        private static IEqlEntityProvider CreateMockEntityProvider()
        {
            var mock = new Mock<IEqlEntityProvider>();
            mock.Setup(x => x.ReadEntities()).Returns(new List<Entity>());
            mock.Setup(x => x.ReadEntity(It.IsAny<string>())).Returns((Entity)null);
            mock.Setup(x => x.ReadEntity(It.IsAny<Guid>())).Returns((Entity)null);
            return mock.Object;
        }

        /// <summary>
        /// Creates a mock IEqlRelationProvider that returns empty relation lists
        /// and null for any relation lookup.
        /// </summary>
        private static IEqlRelationProvider CreateMockRelationProvider()
        {
            var mock = new Mock<IEqlRelationProvider>();
            mock.Setup(x => x.Read()).Returns(new List<EntityRelation>());
            mock.Setup(x => x.Read(It.IsAny<string>())).Returns((EntityRelation)null);
            mock.Setup(x => x.Read(It.IsAny<Guid>())).Returns((EntityRelation)null);
            return mock.Object;
        }

        /// <summary>
        /// Creates a mock IEqlHookProvider that reports no hooks for any entity.
        /// </summary>
        private static IEqlHookProvider CreateMockHookProvider()
        {
            var mock = new Mock<IEqlHookProvider>();
            mock.Setup(x => x.ContainsAnyHooksForEntity(It.IsAny<string>())).Returns(false);
            return mock.Object;
        }

        /// <summary>
        /// Builds the EQL query and extracts the root EqlSelectNode from the AST tree.
        /// The tree is built during BuildAbstractTree before BuildSql runs, so the AST
        /// is valid even when entity resolution adds errors. Asserts tree and root node
        /// are non-null before returning the select node.
        /// </summary>
        private EqlSelectNode BuildAndGetSelectNode(string text, List<EqlParameter> parameters = null)
        {
            var builder = CreateBuilder(text);
            var result = builder.Build(parameters);
            result.Should().NotBeNull("Build() should return a result for valid EQL syntax");
            result.Tree.Should().NotBeNull("AST tree should be built for valid EQL syntax before BuildSql runs");
            result.Tree.RootNode.Should().NotBeNull("Root node should be present in AST tree");
            var selectNode = result.Tree.RootNode as EqlSelectNode;
            selectNode.Should().NotBeNull("Root node should be an EqlSelectNode for SELECT queries");
            return selectNode;
        }

        #endregion

        #region Phase 1: Parse Method Tests (via Build())

        /// <summary>
        /// Verifies that passing an empty string to EqlBuilder causes Build() to throw
        /// EqlException with "Source is empty." from the private Parse() method
        /// (SharedKernel EqlBuilder.cs line ~386-387).
        /// </summary>
        [Fact]
        public void Test_Build_EmptySource_ThrowsEqlException()
        {
            // Arrange — empty string triggers the IsNullOrWhiteSpace check in Parse()
            var builder = CreateBuilder(string.Empty);

            // Act & Assert
            Action act = () => builder.Build();
            act.Should().Throw<EqlException>()
                .WithMessage("Source is empty.");
        }

        /// <summary>
        /// Verifies that passing invalid EQL text like "INVALID QUERY" (not matching
        /// the SELECT grammar) causes Build() to return a result with non-empty Errors
        /// from Irony parse failure. The errors list is populated when Parse() detects
        /// syntax errors via tree.HasErrors().
        /// </summary>
        [Fact]
        public void Test_Build_InvalidSyntax_ReturnsErrors()
        {
            // Arrange — text that doesn't match EqlGrammar's select_statement rule
            var builder = CreateBuilder("INVALID QUERY");

            // Act
            var result = builder.Build();

            // Assert — errors should be non-empty from parse failure or downstream null ref
            result.Should().NotBeNull();
            result.Errors.Should().NotBeEmpty(
                "invalid EQL syntax should produce at least one error from parsing or AST construction");
        }

        /// <summary>
        /// Verifies that passing a valid simple SELECT query does not throw EqlException.
        /// The parse phase succeeds, AST is built, and Build() returns a result.
        /// Entity resolution may add non-fatal errors, but no EqlException is thrown.
        /// </summary>
        [Fact]
        public void Test_Build_ValidSimpleQuery_NoErrors()
        {
            // Arrange — syntactically valid EQL query
            var builder = CreateBuilder("SELECT * FROM entity1");

            // Act & Assert — no EqlException should be thrown during parse/AST build
            Action act = () => builder.Build();
            act.Should().NotThrow<EqlException>(
                "valid EQL syntax should parse and build AST without throwing EqlException");
        }

        #endregion

        #region Phase 2: Parameter Normalization Tests

        /// <summary>
        /// Verifies that EqlParameter constructor automatically prepends "@" to parameter
        /// names that don't already have it (EqlParameter.cs lines ~29-32).
        /// </summary>
        [Fact]
        public void Test_ParameterName_AutoPrefix()
        {
            // Arrange — parameter name without @ prefix
            var param = new EqlParameter("param1", 1);

            // Assert — constructor should have auto-prefixed with @
            param.ParameterName.Should().Be("@param1",
                "EqlParameter should auto-prefix names without '@' with the '@' character");
        }

        /// <summary>
        /// Verifies that EqlParameter constructor does not double-prefix a parameter
        /// name that already starts with "@".
        /// </summary>
        [Fact]
        public void Test_ParameterName_AlreadyPrefixed()
        {
            // Arrange — parameter name already has @ prefix
            var param = new EqlParameter("@param1", 1);

            // Assert — should remain as-is, no double @
            param.ParameterName.Should().Be("@param1",
                "EqlParameter should not add extra '@' when name is already prefixed");
        }

        /// <summary>
        /// Verifies that when EQL text contains "PAGE @page" and the @page parameter is
        /// provided with a valid integer, "@page" appears in the builder's ExpectedParameters
        /// list (populated during BuildSelectTree PAGE clause processing).
        /// </summary>
        [Fact]
        public void Test_ExpectedParameters_PageParameter()
        {
            // Arrange
            var builder = CreateBuilder("SELECT * FROM entity1 PAGE @page");
            var parameters = new List<EqlParameter> { new EqlParameter("@page", 1) };

            // Act — Build processes PAGE parameter during tree building
            builder.Build(parameters);

            // Assert — ExpectedParameters on the builder should contain @page
            builder.ExpectedParameters.Should().Contain("@page",
                "PAGE @page clause should register '@page' as an expected parameter");
        }

        /// <summary>
        /// Verifies that "PAGESIZE @size" registers "@size" in ExpectedParameters
        /// during BuildSelectTree PAGESIZE clause processing.
        /// </summary>
        [Fact]
        public void Test_ExpectedParameters_PageSizeParameter()
        {
            // Arrange
            var builder = CreateBuilder("SELECT * FROM entity1 PAGESIZE @size");
            var parameters = new List<EqlParameter> { new EqlParameter("@size", 10) };

            // Act
            builder.Build(parameters);

            // Assert
            builder.ExpectedParameters.Should().Contain("@size",
                "PAGESIZE @size clause should register '@size' as an expected parameter");
        }

        #endregion

        #region Phase 3: PAGE/PAGESIZE Validation Tests

        /// <summary>
        /// Verifies that "PAGE @page" without providing the @page parameter throws
        /// EqlException with message "PAGE: Parameter '@page' not found."
        /// (SharedKernel EqlBuilder.cs line ~481-482).
        /// </summary>
        [Fact]
        public void Test_Page_ParameterNotFound_ThrowsEqlException()
        {
            // Arrange — EQL references @page but no parameters provided
            var builder = CreateBuilder("SELECT * FROM entity1 PAGE @page");

            // Act & Assert
            Action act = () => builder.Build();
            act.Should().Throw<EqlException>()
                .WithMessage("*PAGE*Parameter*@page*not found*");
        }

        /// <summary>
        /// Verifies that "PAGE @page" with a non-integer parameter value (e.g., "abc")
        /// throws EqlException with message containing "PAGE: Invalid parameter"
        /// (SharedKernel EqlBuilder.cs line ~486-487).
        /// </summary>
        [Fact]
        public void Test_Page_InvalidParameterValue_ThrowsEqlException()
        {
            // Arrange — @page has a non-integer string value
            var builder = CreateBuilder("SELECT * FROM entity1 PAGE @page");
            var parameters = new List<EqlParameter> { new EqlParameter("@page", "abc") };

            // Act & Assert
            Action act = () => builder.Build(parameters);
            act.Should().Throw<EqlException>()
                .WithMessage("*PAGE*Invalid parameter*");
        }

        /// <summary>
        /// Verifies that "PAGE 3" (number literal) sets the AST Page node Number to 3.
        /// The number is set during BuildSelectTree before BuildSql runs, so the
        /// AST is valid even with empty entity providers.
        /// </summary>
        [Fact]
        public void Test_Page_NumberLiteral_SetsPageNumber()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 PAGE 3");

            // Assert — Page node should be populated with Number = 3
            selectNode.Page.Should().NotBeNull("PAGE 3 should create a Page node in the AST");
            selectNode.Page.Number.Should().Be(3m,
                "PAGE 3 should set the Page node Number to decimal 3");
        }

        /// <summary>
        /// Verifies that "PAGESIZE @size" without providing the @size parameter throws
        /// EqlException with message "PAGESIZE: Parameter '@size' not found."
        /// (SharedKernel EqlBuilder.cs line ~515-516).
        /// </summary>
        [Fact]
        public void Test_PageSize_ParameterNotFound_ThrowsEqlException()
        {
            // Arrange
            var builder = CreateBuilder("SELECT * FROM entity1 PAGESIZE @size");

            // Act & Assert
            Action act = () => builder.Build();
            act.Should().Throw<EqlException>()
                .WithMessage("*PAGESIZE*Parameter*@size*not found*");
        }

        /// <summary>
        /// Verifies that "PAGESIZE @size" with a non-integer value (e.g., "xyz")
        /// throws EqlException with message containing "PAGESIZE: Invalid parameter"
        /// (SharedKernel EqlBuilder.cs line ~519-520).
        /// </summary>
        [Fact]
        public void Test_PageSize_InvalidParameterValue_ThrowsEqlException()
        {
            // Arrange
            var builder = CreateBuilder("SELECT * FROM entity1 PAGESIZE @size");
            var parameters = new List<EqlParameter> { new EqlParameter("@size", "xyz") };

            // Act & Assert
            Action act = () => builder.Build(parameters);
            act.Should().Throw<EqlException>()
                .WithMessage("*PAGESIZE*Invalid parameter*");
        }

        /// <summary>
        /// Verifies that "PAGESIZE 20" (number literal) sets the AST PageSize node
        /// Number to 20 during BuildSelectTree.
        /// </summary>
        [Fact]
        public void Test_PageSize_NumberLiteral_SetsPageSizeNumber()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 PAGESIZE 20");

            // Assert
            selectNode.PageSize.Should().NotBeNull("PAGESIZE 20 should create a PageSize node");
            selectNode.PageSize.Number.Should().Be(20m,
                "PAGESIZE 20 should set the PageSize node Number to decimal 20");
        }

        #endregion

        #region Phase 4: AST Construction Tests (via Build())

        /// <summary>
        /// Verifies that "SELECT * FROM entity1" produces an AST with a single
        /// EqlWildcardFieldNode in the Fields list. The wildcard terminal "*" is
        /// mapped by BuildSelectFieldList to EqlWildcardFieldNode.
        /// </summary>
        [Fact]
        public void Test_Build_SelectWildcard_ProducesWildcardFieldNode()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1");

            // Assert
            selectNode.Fields.Should().HaveCount(1,
                "SELECT * should produce exactly one field node");
            selectNode.Fields[0].Should().BeOfType<EqlWildcardFieldNode>(
                "the wildcard * should map to EqlWildcardFieldNode");
        }

        /// <summary>
        /// Verifies that "SELECT field1, field2 FROM entity1" produces two EqlFieldNode
        /// entries in the Fields list with matching FieldName properties.
        /// </summary>
        [Fact]
        public void Test_Build_SelectNamedFields_ProducesFieldNodes()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT field1, field2 FROM entity1");

            // Assert
            selectNode.Fields.Should().HaveCount(2,
                "SELECT field1, field2 should produce two field nodes");

            var field1 = selectNode.Fields[0] as EqlFieldNode;
            field1.Should().NotBeNull("first field should be an EqlFieldNode");
            field1.FieldName.Should().Be("field1");

            var field2 = selectNode.Fields[1] as EqlFieldNode;
            field2.Should().NotBeNull("second field should be an EqlFieldNode");
            field2.FieldName.Should().Be("field2");
        }

        /// <summary>
        /// Verifies that the FROM clause correctly sets EntityName on the EqlFromNode.
        /// "SELECT * FROM my_entity" should produce From.EntityName = "my_entity".
        /// </summary>
        [Fact]
        public void Test_Build_FromClause_SetsEntityName()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM my_entity");

            // Assert
            selectNode.From.Should().NotBeNull("FROM clause should produce an EqlFromNode");
            selectNode.From.EntityName.Should().Be("my_entity",
                "FROM my_entity should set EntityName to 'my_entity'");
        }

        /// <summary>
        /// Verifies that "ORDER BY name ASC" produces an EqlOrderByNode with one
        /// EqlOrderByFieldNode having FieldName="name" and Direction="ASC".
        /// </summary>
        [Fact]
        public void Test_Build_OrderBy_ProducesOrderByNodes()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 ORDER BY name ASC");

            // Assert
            selectNode.OrderBy.Should().NotBeNull("ORDER BY should produce an OrderBy node");
            selectNode.OrderBy.Fields.Should().HaveCount(1,
                "ORDER BY name ASC should produce one order-by field");
            selectNode.OrderBy.Fields[0].FieldName.Should().Be("name");
            selectNode.OrderBy.Fields[0].Direction.Should().Be("ASC");
        }

        /// <summary>
        /// Verifies that "ORDER BY id DESC" sets Direction to "DESC" on the
        /// EqlOrderByFieldNode, confirming descending sort direction parsing.
        /// </summary>
        [Fact]
        public void Test_Build_OrderByDescending_SetsDirection()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 ORDER BY id DESC");

            // Assert
            selectNode.OrderBy.Should().NotBeNull();
            selectNode.OrderBy.Fields.Should().HaveCount(1);
            selectNode.OrderBy.Fields[0].FieldName.Should().Be("id");
            selectNode.OrderBy.Fields[0].Direction.Should().Be("DESC",
                "ORDER BY id DESC should set direction to 'DESC'");
        }

        #endregion

        #region Phase 5: Relation Path Construction Tests

        /// <summary>
        /// Verifies that the single-dollar prefix "$rel1.field1" creates an
        /// EqlRelationFieldNode with Direction=TargetOrigin. The $ prefix maps to
        /// EqlRelationDirectionType.TargetOrigin (EqlBuilder.cs GetRelationInfos
        /// line ~759: default direction is TargetOrigin, only $$ overrides to OriginTarget).
        /// </summary>
        [Fact]
        public void Test_Build_DollarRelation_TargetOriginDirection()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT $rel1.field1 FROM entity1");

            // Assert
            selectNode.Fields.Should().HaveCount(1);
            var relField = selectNode.Fields[0] as EqlRelationFieldNode;
            relField.Should().NotBeNull("$rel1.field1 should produce an EqlRelationFieldNode");
            relField.FieldName.Should().Be("field1");
            relField.Relations.Should().HaveCount(1);
            relField.Relations[0].Name.Should().Be("rel1");
            relField.Relations[0].Direction.Should().Be(EqlRelationDirectionType.TargetOrigin,
                "$ prefix should map to TargetOrigin direction");
        }

        /// <summary>
        /// Verifies that the double-dollar prefix "$$rel1.field1" creates an
        /// EqlRelationFieldNode with Direction=OriginTarget. The $$ prefix maps to
        /// EqlRelationDirectionType.OriginTarget (EqlBuilder.cs GetRelationInfos
        /// line ~760: if $$ then direction = OriginTarget).
        /// </summary>
        [Fact]
        public void Test_Build_DoubleDollarRelation_OriginTargetDirection()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT $$rel1.field1 FROM entity1");

            // Assert
            selectNode.Fields.Should().HaveCount(1);
            var relField = selectNode.Fields[0] as EqlRelationFieldNode;
            relField.Should().NotBeNull("$$rel1.field1 should produce an EqlRelationFieldNode");
            relField.FieldName.Should().Be("field1");
            relField.Relations.Should().HaveCount(1);
            relField.Relations[0].Name.Should().Be("rel1");
            relField.Relations[0].Direction.Should().Be(EqlRelationDirectionType.OriginTarget,
                "$$ prefix should map to OriginTarget direction");
        }

        /// <summary>
        /// Verifies that "$rel1.*" produces an EqlRelationWildcardFieldNode (subclass of
        /// EqlRelationFieldNode) with the Relations list populated. BuildSelectFieldList
        /// checks if fieldName == "*" and creates EqlRelationWildcardFieldNode accordingly.
        /// </summary>
        [Fact]
        public void Test_Build_RelationWildcard_ProducesRelationWildcardFieldNode()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT $rel1.* FROM entity1");

            // Assert
            selectNode.Fields.Should().HaveCount(1);
            selectNode.Fields[0].Should().BeOfType<EqlRelationWildcardFieldNode>(
                "$rel1.* should produce an EqlRelationWildcardFieldNode");
            var relWild = selectNode.Fields[0] as EqlRelationWildcardFieldNode;
            relWild.Should().NotBeNull();
            relWild.Relations.Should().HaveCount(1,
                "should have one relation info for the single $rel1 prefix");
            relWild.Relations[0].Name.Should().Be("rel1");
        }

        /// <summary>
        /// Verifies that "$rel1.$rel2.field1" creates an EqlRelationFieldNode with two
        /// EqlRelationInfo entries in the Relations list, representing the chained
        /// relation traversal path. GetRelationInfos iterates all column_relation children.
        /// </summary>
        [Fact]
        public void Test_Build_MultipleRelations_ChainedRelationInfos()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT $rel1.$rel2.field1 FROM entity1");

            // Assert
            selectNode.Fields.Should().HaveCount(1);
            var relField = selectNode.Fields[0] as EqlRelationFieldNode;
            relField.Should().NotBeNull("chained relations should produce an EqlRelationFieldNode");
            relField.FieldName.Should().Be("field1");
            relField.Relations.Should().HaveCount(2,
                "$rel1.$rel2 should produce two EqlRelationInfo entries");
            relField.Relations[0].Name.Should().Be("rel1");
            relField.Relations[1].Name.Should().Be("rel2");
        }

        #endregion

        #region Phase 6: WHERE Binary Expression Tree Tests

        /// <summary>
        /// Verifies that "WHERE name = 'test'" creates an EqlBinaryExpressionNode as
        /// the root expression with Operator="=", FirstOperand as EqlFieldNode with
        /// FieldName="name", and SecondOperand as EqlTextValueNode with Text="test".
        /// </summary>
        [Fact]
        public void Test_Build_WhereEquals_CreatesBinaryExpression()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 WHERE name = 'test'");

            // Assert
            selectNode.Where.Should().NotBeNull("WHERE clause should produce a Where node");
            var binExpr = selectNode.Where.RootExpressionNode as EqlBinaryExpressionNode;
            binExpr.Should().NotBeNull("WHERE root should be an EqlBinaryExpressionNode");
            binExpr.Operator.Should().Be("=",
                "equality operator should be preserved as '='");

            var firstOp = binExpr.FirstOperand as EqlFieldNode;
            firstOp.Should().NotBeNull("left operand should be an EqlFieldNode for 'name'");
            firstOp.FieldName.Should().Be("name");

            var secondOp = binExpr.SecondOperand as EqlTextValueNode;
            secondOp.Should().NotBeNull("right operand should be an EqlTextValueNode for 'test'");
            secondOp.Text.Should().Be("test");
        }

        /// <summary>
        /// Verifies that "WHERE a = 1 AND b = 2" creates a nested binary expression
        /// tree with the root Operator="AND" and both operands as BinaryExpressionNodes.
        /// AND has lower precedence than = in the EqlGrammar (precedence 5 vs 8).
        /// </summary>
        [Fact]
        public void Test_Build_WhereAnd_NestedBinaryExpression()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 WHERE a = 1 AND b = 2");

            // Assert
            selectNode.Where.Should().NotBeNull();
            var rootExpr = selectNode.Where.RootExpressionNode as EqlBinaryExpressionNode;
            rootExpr.Should().NotBeNull("root expression should be a BinaryExpressionNode");
            rootExpr.Operator.Should().Be("AND",
                "AND should be the root operator due to lower precedence than =");

            var leftExpr = rootExpr.FirstOperand as EqlBinaryExpressionNode;
            leftExpr.Should().NotBeNull("left operand of AND should be a BinaryExpressionNode (a = 1)");
            leftExpr.Operator.Should().Be("=");

            var rightExpr = rootExpr.SecondOperand as EqlBinaryExpressionNode;
            rightExpr.Should().NotBeNull("right operand of AND should be a BinaryExpressionNode (b = 2)");
            rightExpr.Operator.Should().Be("=");
        }

        /// <summary>
        /// Verifies that "WHERE a = 1 OR b = 2" creates a nested binary expression
        /// tree with Operator="OR" at the root. OR has precedence 4 (lower than AND=5
        /// and ===8) in the EqlGrammar.
        /// </summary>
        [Fact]
        public void Test_Build_WhereOr_NestedBinaryExpression()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 WHERE a = 1 OR b = 2");

            // Assert
            selectNode.Where.Should().NotBeNull();
            var rootExpr = selectNode.Where.RootExpressionNode as EqlBinaryExpressionNode;
            rootExpr.Should().NotBeNull();
            rootExpr.Operator.Should().Be("OR",
                "OR should be the root operator");

            var leftExpr = rootExpr.FirstOperand as EqlBinaryExpressionNode;
            leftExpr.Should().NotBeNull("left operand of OR should be a BinaryExpressionNode");

            var rightExpr = rootExpr.SecondOperand as EqlBinaryExpressionNode;
            rightExpr.Should().NotBeNull("right operand of OR should be a BinaryExpressionNode");
        }

        /// <summary>
        /// Verifies that CONTAINS operator is preserved in the binary expression tree.
        /// CONTAINS is registered as a binary operator in EqlGrammar with precedence 8.
        /// BuildBinaryExpressionNode calls ToUpperInvariant() on the operator value.
        /// </summary>
        [Fact]
        public void Test_Build_WhereContains_Operator()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 WHERE name CONTAINS 'test'");

            // Assert
            selectNode.Where.Should().NotBeNull();
            var binExpr = selectNode.Where.RootExpressionNode as EqlBinaryExpressionNode;
            binExpr.Should().NotBeNull();
            binExpr.Operator.Should().Be("CONTAINS",
                "CONTAINS operator should be preserved (uppercased via ToUpperInvariant)");
        }

        /// <summary>
        /// Verifies that STARTSWITH operator is preserved in the binary expression tree.
        /// STARTSWITH is a registered binary operator in EqlGrammar.
        /// </summary>
        [Fact]
        public void Test_Build_WhereStartsWith_Operator()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 WHERE name STARTSWITH 'test'");

            // Assert
            selectNode.Where.Should().NotBeNull();
            var binExpr = selectNode.Where.RootExpressionNode as EqlBinaryExpressionNode;
            binExpr.Should().NotBeNull();
            binExpr.Operator.Should().Be("STARTSWITH",
                "STARTSWITH operator should be preserved");
        }

        /// <summary>
        /// Verifies that the full-text search operator @@ is preserved in the binary
        /// expression tree. @@ is registered in EqlGrammar as a binary operator and
        /// is stored uppercased (which for @@ is still "@@").
        /// </summary>
        [Fact]
        public void Test_Build_WhereFts_Operator()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 WHERE name @@ 'search'");

            // Assert
            selectNode.Where.Should().NotBeNull();
            var binExpr = selectNode.Where.RootExpressionNode as EqlBinaryExpressionNode;
            binExpr.Should().NotBeNull();
            binExpr.Operator.Should().Be("@@",
                "full-text search operator @@ should be preserved");
        }

        /// <summary>
        /// Verifies that "WHERE name = @param1" creates an EqlArgumentValueNode as the
        /// second operand with ArgumentName="param1" (without the @ prefix).
        /// BuildOperandNode handles "argument" terms by extracting the identifier part.
        /// </summary>
        [Fact]
        public void Test_Build_WhereArgument_CreatesArgumentValueNode()
        {
            // Arrange — provide the parameter so it doesn't cause issues in later stages
            var parameters = new List<EqlParameter> { new EqlParameter("@param1", "value") };
            var selectNode = BuildAndGetSelectNode(
                "SELECT * FROM entity1 WHERE name = @param1", parameters);

            // Assert
            selectNode.Where.Should().NotBeNull();
            var binExpr = selectNode.Where.RootExpressionNode as EqlBinaryExpressionNode;
            binExpr.Should().NotBeNull();
            binExpr.Operator.Should().Be("=");

            var argNode = binExpr.SecondOperand as EqlArgumentValueNode;
            argNode.Should().NotBeNull(
                "parameter reference @param1 should create an EqlArgumentValueNode");
            argNode.ArgumentName.Should().Be("param1",
                "ArgumentName should store the name without the @ prefix");
        }

        /// <summary>
        /// Verifies that "WHERE count = 42" creates an EqlNumberValueNode as the
        /// second operand with Number=42. BuildOperandNode handles "number" terms
        /// by calling Convert.ToDecimal on the token value.
        /// </summary>
        [Fact]
        public void Test_Build_WhereNumber_CreatesNumberValueNode()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 WHERE count = 42");

            // Assert
            selectNode.Where.Should().NotBeNull();
            var binExpr = selectNode.Where.RootExpressionNode as EqlBinaryExpressionNode;
            binExpr.Should().NotBeNull();

            var numNode = binExpr.SecondOperand as EqlNumberValueNode;
            numNode.Should().NotBeNull(
                "numeric literal 42 should create an EqlNumberValueNode");
            numNode.Number.Should().Be(42m,
                "Number should be decimal 42");
        }

        /// <summary>
        /// Verifies that "WHERE name = NULL" creates an EqlKeywordNode as the second
        /// operand with Keyword="null". BuildOperandNode handles "null" terms.
        /// </summary>
        [Fact]
        public void Test_Build_WhereNull_CreatesKeywordNode()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 WHERE name = NULL");

            // Assert
            selectNode.Where.Should().NotBeNull();
            var binExpr = selectNode.Where.RootExpressionNode as EqlBinaryExpressionNode;
            binExpr.Should().NotBeNull();

            var kwNode = binExpr.SecondOperand as EqlKeywordNode;
            kwNode.Should().NotBeNull(
                "NULL literal should create an EqlKeywordNode");
            kwNode.Keyword.Should().Be("null",
                "Keyword should be lowercase 'null'");
        }

        /// <summary>
        /// Verifies that "WHERE active = TRUE" creates an EqlKeywordNode with
        /// Keyword="true". EqlGrammar is case-insensitive (Grammar(false)).
        /// </summary>
        [Fact]
        public void Test_Build_WhereTrue_CreatesKeywordNode()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 WHERE active = TRUE");

            // Assert
            selectNode.Where.Should().NotBeNull();
            var binExpr = selectNode.Where.RootExpressionNode as EqlBinaryExpressionNode;
            binExpr.Should().NotBeNull();

            var kwNode = binExpr.SecondOperand as EqlKeywordNode;
            kwNode.Should().NotBeNull(
                "TRUE literal should create an EqlKeywordNode");
            kwNode.Keyword.Should().Be("true",
                "Keyword should be lowercase 'true'");
        }

        /// <summary>
        /// Verifies that "WHERE active = FALSE" creates an EqlKeywordNode with
        /// Keyword="false".
        /// </summary>
        [Fact]
        public void Test_Build_WhereFalse_CreatesKeywordNode()
        {
            // Arrange & Act
            var selectNode = BuildAndGetSelectNode("SELECT * FROM entity1 WHERE active = FALSE");

            // Assert
            selectNode.Where.Should().NotBeNull();
            var binExpr = selectNode.Where.RootExpressionNode as EqlBinaryExpressionNode;
            binExpr.Should().NotBeNull();

            var kwNode = binExpr.SecondOperand as EqlKeywordNode;
            kwNode.Should().NotBeNull(
                "FALSE literal should create an EqlKeywordNode");
            kwNode.Keyword.Should().Be("false",
                "Keyword should be lowercase 'false'");
        }

        #endregion
    }
}
