using System.Linq;
using System.Reflection;
using Xunit;
using FluentAssertions;
using Irony.Parsing;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.Tests.SharedKernel.Eql
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="EqlGrammar"/> — an Irony grammar definition
    /// class that defines the EntityQL (EQL) dialect used throughout the WebVella ERP platform.
    /// NOTE: EqlGrammar is an internal class. These tests rely on
    /// [InternalsVisibleTo("WebVella.Erp.Tests.SharedKernel")]
    /// being configured in the SharedKernel project (WebVella.Erp.SharedKernel.csproj).
    /// </summary>
    public class EqlGrammarTests
    {
        private readonly EqlGrammar _grammar;
        private readonly LanguageData _languageData;
        private readonly Parser _parser;

        public EqlGrammarTests()
        {
            _grammar = new EqlGrammar();
            _languageData = new LanguageData(_grammar);
            _parser = new Parser(_languageData);
        }

        private ParseTree ParseEql(string query)
        {
            return _parser.Parse(query);
        }

        private static ParseTreeNode FindChildByName(ParseTreeNode parent, string termName)
        {
            return parent.ChildNodes.FirstOrDefault(n => n.Term.Name == termName);
        }

        #region Phase 1: Grammar Initialization Tests

        [Fact]
        public void Test_Grammar_IsNotCaseSensitive()
        {
            _grammar.CaseSensitive.Should().BeFalse(
                "EqlGrammar constructor passes false to Grammar(false), making keywords case-insensitive");
        }

        [Fact]
        public void Test_Grammar_LanguageAttribute()
        {
            var attr = typeof(EqlGrammar).GetCustomAttribute<LanguageAttribute>();
            attr.Should().NotBeNull("EqlGrammar should be decorated with [Language] attribute");
            attr.LanguageName.Should().Be("EntityQL",
                "the grammar language name should be 'EntityQL'");
        }

        [Fact]
        public void Test_Grammar_RootIsSet()
        {
            _grammar.Root.Should().NotBeNull(
                "EqlGrammar.Root should be set to the 'root' non-terminal (stmtRoot)");
        }

        #endregion

        #region Phase 2: Valid Query Parsing Tests

        [Fact]
        public void Test_Parse_SimpleSelect_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1");
            tree.HasErrors().Should().BeFalse("simple SELECT * FROM should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectNamedFields_Valid()
        {
            var tree = ParseEql("SELECT field1, field2 FROM entity1");
            tree.HasErrors().Should().BeFalse("SELECT with named fields should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithWhere_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE name = 'test'");
            tree.HasErrors().Should().BeFalse("SELECT with WHERE clause should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithWhereAnd_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE a = 1 AND b = 'test'");
            tree.HasErrors().Should().BeFalse("WHERE with AND condition should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithWhereOr_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE a = 1 OR b = 2");
            tree.HasErrors().Should().BeFalse("WHERE with OR condition should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithOrderBy_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 ORDER BY name ASC");
            tree.HasErrors().Should().BeFalse("ORDER BY ASC should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithOrderByDesc_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 ORDER BY name DESC");
            tree.HasErrors().Should().BeFalse("ORDER BY DESC should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithPage_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 PAGE 1");
            tree.HasErrors().Should().BeFalse("PAGE with number literal should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithPageSize_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 PAGESIZE 20");
            tree.HasErrors().Should().BeFalse("PAGESIZE with number literal should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithPageAndPageSize_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 PAGE 1 PAGESIZE 20");
            tree.HasErrors().Should().BeFalse("PAGE + PAGESIZE together should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithRelationField_Valid()
        {
            var tree = ParseEql("SELECT $rel1.field1 FROM entity1");
            tree.HasErrors().Should().BeFalse("single $ relation field should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithDoubleRelationField_Valid()
        {
            var tree = ParseEql("SELECT $$rel1.field1 FROM entity1");
            tree.HasErrors().Should().BeFalse("double $$ relation field should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithRelationWildcard_Valid()
        {
            var tree = ParseEql("SELECT $rel1.* FROM entity1");
            tree.HasErrors().Should().BeFalse("relation wildcard $rel.* should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithArgument_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE id = @param1");
            tree.HasErrors().Should().BeFalse("WHERE with @param argument should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithPageArgument_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 PAGE @page");
            tree.HasErrors().Should().BeFalse("PAGE with @argument should parse without errors");
        }

        [Fact]
        public void Test_Parse_SelectWithPageSizeArgument_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 PAGESIZE @size");
            tree.HasErrors().Should().BeFalse("PAGESIZE with @argument should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereContains_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE name CONTAINS 'test'");
            tree.HasErrors().Should().BeFalse("CONTAINS operator should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereStartsWith_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE name STARTSWITH 'test'");
            tree.HasErrors().Should().BeFalse("STARTSWITH operator should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereFts_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE name @@ 'search'");
            tree.HasErrors().Should().BeFalse("@@ (FTS) operator should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereRegex_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE name ~ 'pattern'");
            tree.HasErrors().Should().BeFalse("~ (regex) operator should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereRegexCaseInsensitive_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE name ~* 'pattern'");
            tree.HasErrors().Should().BeFalse("~* (case-insensitive regex) operator should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereNotRegex_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE name !~ 'pattern'");
            tree.HasErrors().Should().BeFalse("!~ (negated regex) operator should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereNotRegexCI_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE name !~* 'pattern'");
            tree.HasErrors().Should().BeFalse("!~* (negated CI regex) operator should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereNotEqual_Valid()
        {
            var tree1 = ParseEql("SELECT * FROM entity1 WHERE a <> 1");
            tree1.HasErrors().Should().BeFalse("<> (not equal) operator should parse without errors");

            var tree2 = ParseEql("SELECT * FROM entity1 WHERE a != 1");
            tree2.HasErrors().Should().BeFalse("!= (not equal) operator should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereComparisons_Valid()
        {
            ParseEql("SELECT * FROM entity1 WHERE a >= 1")
                .HasErrors().Should().BeFalse(">= operator should parse without errors");
            ParseEql("SELECT * FROM entity1 WHERE a <= 1")
                .HasErrors().Should().BeFalse("<= operator should parse without errors");
            ParseEql("SELECT * FROM entity1 WHERE a > 1")
                .HasErrors().Should().BeFalse("> operator should parse without errors");
            ParseEql("SELECT * FROM entity1 WHERE a < 1")
                .HasErrors().Should().BeFalse("< operator should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereNull_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE name = NULL");
            tree.HasErrors().Should().BeFalse("comparison with NULL should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereTrue_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE active = TRUE");
            tree.HasErrors().Should().BeFalse("comparison with TRUE should parse without errors");
        }

        [Fact]
        public void Test_Parse_WhereFalse_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE active = FALSE");
            tree.HasErrors().Should().BeFalse("comparison with FALSE should parse without errors");
        }

        [Fact]
        public void Test_Parse_BlockComment_Valid()
        {
            var tree = ParseEql("SELECT * /* block comment */ FROM entity1");
            tree.HasErrors().Should().BeFalse("block comments should be stripped during parsing");
        }

        [Fact]
        public void Test_Parse_LineComment_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 -- line comment\nWHERE a = 1");
            tree.HasErrors().Should().BeFalse("line comments should be stripped during parsing");
        }

        [Fact]
        public void Test_Parse_StringWithDoubledQuotes_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE name = 'it''s'");
            tree.HasErrors().Should().BeFalse("doubled single-quotes in string literals should parse correctly");
        }

        [Fact]
        public void Test_Parse_ChainedRelations_Valid()
        {
            var tree = ParseEql("SELECT $rel1.$rel2.field1 FROM entity1");
            tree.HasErrors().Should().BeFalse("chained relation $rel1.$rel2.field should parse without errors");
        }

        [Fact]
        public void Test_Parse_MultipleOrderFields_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 ORDER BY a ASC, b DESC");
            tree.HasErrors().Should().BeFalse("multiple ORDER BY fields should parse without errors");
        }

        [Fact]
        public void Test_Parse_OrderByArgument_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 ORDER BY @field");
            tree.HasErrors().Should().BeFalse("ORDER BY with @argument should parse without errors");
        }

        [Fact]
        public void Test_Parse_OrderByArgumentDirection_Valid()
        {
            var tree = ParseEql("SELECT * FROM entity1 ORDER BY name @dir");
            tree.HasErrors().Should().BeFalse("ORDER BY with @argument direction should parse without errors");
        }

        #endregion

        #region Phase 3: Invalid Query Parsing Tests

        [Fact]
        public void Test_Parse_EmptyString_HasErrors()
        {
            var tree = ParseEql("");
            tree.HasErrors().Should().BeTrue("empty input should produce parse errors");
        }

        [Fact]
        public void Test_Parse_NoSelect_HasErrors()
        {
            var tree = ParseEql("FROM entity1");
            tree.HasErrors().Should().BeTrue("missing SELECT keyword should produce parse errors");
        }

        [Fact]
        public void Test_Parse_NoFrom_HasErrors()
        {
            var tree = ParseEql("SELECT *");
            tree.HasErrors().Should().BeTrue("missing FROM clause should produce parse errors");
        }

        [Fact]
        public void Test_Parse_InvalidOperator_HasErrors()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE a INVALID b");
            tree.HasErrors().Should().BeTrue("unrecognized operator 'INVALID' should produce parse errors");
        }

        [Fact]
        public void Test_Parse_IncompleteWhere_HasErrors()
        {
            var tree = ParseEql("SELECT * FROM entity1 WHERE");
            tree.HasErrors().Should().BeTrue("incomplete WHERE clause should produce parse errors");
        }

        #endregion

        #region Phase 4: Terminal/Non-Terminal Verification

        [Fact]
        public void Test_Grammar_NumberTerminal_Exists()
        {
            var terminals = _languageData.GrammarData.Terminals;
            terminals.Should().Contain(
                t => t.Name == "NUMBER" && t is NumberLiteral,
                "EqlGrammar should define a NumberLiteral terminal named 'NUMBER'");
        }

        [Fact]
        public void Test_Grammar_StringTerminal_Exists()
        {
            var terminals = _languageData.GrammarData.Terminals;
            terminals.Should().Contain(
                t => t.Name == "STRING" && t is StringLiteral,
                "EqlGrammar should define a StringLiteral terminal named 'STRING'");
        }

        [Fact]
        public void Test_Parse_CaseInsensitive()
        {
            var treeUpper = ParseEql("SELECT * FROM entity1");
            treeUpper.HasErrors().Should().BeFalse("uppercase keywords should parse without errors");

            var treeLower = ParseEql("select * from entity1");
            treeLower.HasErrors().Should().BeFalse(
                "lowercase keywords should also parse without errors (case-insensitive grammar)");

            var treeMixed = ParseEql("Select * From entity1");
            treeMixed.HasErrors().Should().BeFalse(
                "mixed-case keywords should also parse without errors");
        }

        #endregion

        #region Phase 5: Operator Precedence Tests

        [Fact]
        public void Test_Parse_OperatorPrecedence_AndOverOr()
        {
            // AND (precedence 5) binds tighter than OR (precedence 4).
            // "a = 1 OR b = 2 AND c = 3" parses as "(a = 1) OR ((b = 2) AND (c = 3))"
            // so OR is the root operator of the WHERE expression tree.
            var tree = ParseEql("SELECT * FROM e WHERE a = 1 OR b = 2 AND c = 3");
            tree.HasErrors().Should().BeFalse(
                "expression with mixed AND/OR should parse without errors");

            // Navigate parse tree: root -> select_statement -> where_clause_optional
            var root = tree.Root;
            root.Should().NotBeNull("parse tree root should not be null");

            var selectStmt = root.ChildNodes.FirstOrDefault(
                n => n.Term.Name == "select_statement") ?? root;

            var whereNode = FindChildByName(selectStmt, "where_clause_optional");
            whereNode.Should().NotBeNull("WHERE clause should be present in the parse tree");

            // 'expression' is MarkTransient so binary_expression appears directly
            var rootBinExpr = FindChildByName(whereNode, "binary_expression");
            rootBinExpr.Should().NotBeNull("root expression should be a binary_expression");

            // binary_expression: [left_operand, binary_operator, right_operand]
            rootBinExpr.ChildNodes.Should().HaveCount(3,
                "binary_expression should have 3 children: left, operator, right");

            // Extract operator text from the middle child (binary_operator)
            var operatorText = rootBinExpr.ChildNodes[1].FindTokenAndGetText();
            operatorText.Should().NotBeNull("binary operator token text should be extractable");

            // Lower precedence (OR=4) groups at the top of the expression tree
            operatorText.Should().Be("OR",
                "AND (precedence 5) should bind tighter than OR (precedence 4), " +
                "making OR the root operator of the expression tree");
        }

        #endregion
    }
}
