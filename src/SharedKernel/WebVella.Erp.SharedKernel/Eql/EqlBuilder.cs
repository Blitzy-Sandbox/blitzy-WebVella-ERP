using Irony.Parsing;
using System;
using System.Collections.Generic;
using System.Linq;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.SharedKernel.Eql
{
	/// <summary>
	/// Orchestrates EQL (Entity Query Language) to PostgreSQL SQL translation.
	/// <para>
	/// Migrated from the monolith's <c>WebVella.Erp.Eql.EqlBuilder</c> (456 lines) with namespace
	/// updates for the SharedKernel. The parsing, abstract tree building, field selection, WHERE clause
	/// construction, ORDER BY, pagination, and relation traversal ($/$$) are preserved identically.
	/// </para>
	/// <para>
	/// In the microservice architecture, service-specific dependencies (entity metadata, relation metadata,
	/// and search hook execution) are injected via <see cref="IEntityMetadataProvider"/> and
	/// <see cref="IRecordSearchHookExecutor"/> interfaces rather than referencing concrete managers.
	/// Each service provides its own implementations during EQL engine initialization.
	/// </para>
	/// <para>
	/// The <see cref="CurrentContext"/> property uses the SharedKernel's <see cref="DbContextAccessor"/>
	/// pattern for ambient database context access, matching the monolith's <c>DbContext.Current</c> behavior.
	/// </para>
	/// </summary>
	public partial class EqlBuilder
	{
		/// <summary>
		/// EQL text to be parsed and translated to SQL.
		/// </summary>
		public string Text { get; private set; }

		/// <summary>
		/// EqlParameters list — named parameters with values for parameterized queries.
		/// </summary>
		public List<EqlParameter> Parameters { get; private set; } = new List<EqlParameter>();

		/// <summary>
		/// Expected parameters discovered during parsing (e.g., @page, @pagesize, @orderField).
		/// </summary>
		public List<string> ExpectedParameters { get; private set; } = new List<string>();

		/// <summary>
		/// EQL build settings controlling DISTINCT and total count inclusion.
		/// </summary>
		public EqlSettings Settings { get; private set; } = new EqlSettings();

		private IDbContext suppliedContext = null;

		/// <summary>
		/// Gets or sets the database context used for connection creation during SQL building.
		/// Falls back to <see cref="DbContextAccessor.Current"/> when no context is explicitly supplied.
		/// Preserves the monolith's <c>DbContext.Current</c> ambient context pattern.
		/// </summary>
		public IDbContext CurrentContext
		{
			get
			{
				if (suppliedContext != null)
					return suppliedContext;
				else
					return DbContextAccessor.Current;
			}
			set
			{
				suppliedContext = value;
			}
		}

		/// <summary>
		/// Provides entity and relation metadata for EQL SQL generation.
		/// Each microservice injects its own implementation that reads from its owned database.
		/// </summary>
		private readonly IEntityMetadataProvider _metadataProvider;

		/// <summary>
		/// Optional hook executor for pre-search record hooks.
		/// In the monolith, <c>RecordHookManager</c> executed pre-search hooks synchronously.
		/// In microservices, this is replaced by event-driven patterns; the executor can be null
		/// if the service does not use search hooks.
		/// </summary>
		private readonly IRecordSearchHookExecutor _hookExecutor;

		/// <summary>
		/// Creates an EqlBuilder instance with dependency injection for microservice compatibility.
		/// </summary>
		/// <param name="text">The EQL query text to parse and translate.</param>
		/// <param name="metadataProvider">Entity and relation metadata provider (required).</param>
		/// <param name="hookExecutor">Optional search hook executor (null disables hook execution).</param>
		/// <param name="currentContext">Optional database context; falls back to DbContextAccessor.Current.</param>
		/// <param name="settings">Optional EQL settings (DISTINCT, IncludeTotal).</param>
		public EqlBuilder(string text, IEntityMetadataProvider metadataProvider,
			IRecordSearchHookExecutor hookExecutor = null,
			IDbContext currentContext = null, EqlSettings settings = null)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
			Text = text;
			_metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
			_hookExecutor = hookExecutor;
			entMan = metadataProvider;
			relMan = metadataProvider;
			if (settings != null)
				Settings = settings;
		}

		/// <summary>
		/// Builds EQL to SQL translation with full field selection, WHERE, ORDER BY,
		/// PAGE/PAGESIZE, and relation traversal.
		/// </summary>
		/// <param name="parameters">Optional EQL parameters for parameterized queries.</param>
		/// <returns>
		/// An <see cref="EqlBuildResult"/> containing the generated SQL, metadata, parameters,
		/// and any errors encountered during translation.
		/// </returns>
		public EqlBuildResult Build(List<EqlParameter> parameters = null)
		{
			if (parameters != null)
				Parameters = parameters;

			var grammar = new EqlGrammar();
			var language = new LanguageData(grammar);
			var parser = new Parser(language);

			List<EqlError> errors = new List<EqlError>();
			var parseTree = Parse(Text, errors);

			EqlBuildResult result = new EqlBuildResult();

			try
			{
				if (errors.Count == 0)
					result.Tree = BuildAbstractTree(parseTree);

				var selectNode = (EqlSelectNode)result.Tree.RootNode;

				// Hook execution: in monolith this was RecordHookManager.ContainsAnyHooksForEntity / ExecutePreSearchRecordHooks
				// In microservices, hooks are replaced by events; the IRecordSearchHookExecutor
				// provides backward compatibility for services that still use the hook pattern.
				bool hooksExists = _hookExecutor != null && _hookExecutor.ContainsAnyHooksForEntity(selectNode.From.EntityName);

				if (hooksExists)
				{
					_hookExecutor.ExecutePreSearchRecordHooks(selectNode.From.EntityName, selectNode, errors);
				}

				if (errors.Count == 0)
				{
					Entity fromEntity = null;
					result.Sql = BuildSql(result.Tree, errors, result.Meta, Settings, out fromEntity);
					result.FromEntity = fromEntity;
				}

				result.Errors.AddRange(errors);
				result.Parameters.AddRange(Parameters);
				result.ExpectedParameters.AddRange(ExpectedParameters);
			}
			catch (EqlException)
			{
				throw;
			}
			catch (Exception ex)
			{
				result.Errors.Add(new EqlError { Message = ex.Message });
			}

			return result;
		}

		private ParseTree Parse(string source, List<EqlError> errors)
		{
			if (string.IsNullOrWhiteSpace(source))
				throw new EqlException("Source is empty.");

			if (errors == null)
				errors = new List<EqlError>();

			var grammar = new EqlGrammar();
			var language = new LanguageData(grammar);
			var parser = new Parser(language);
			var tree = parser.Parse(source);

			if (tree.HasErrors())
			{
				foreach (var error in tree.ParserMessages.Where(x => x.Level == Irony.ErrorLevel.Error))
				{
					EqlError err = new EqlError { Message = error.Message };
					err.Line = error.Location.Line;
					err.Column = error.Location.Column;
					errors.Add(err);
				}
			}

			return tree;
		}

		private EqlAbstractTree BuildAbstractTree(ParseTree parseTree)
		{
			EqlAbstractTree resultTree = new EqlAbstractTree();
			var rootQueryNode = parseTree.Root.ChildNodes[0];
			switch (rootQueryNode.Term.Name)
			{
				case "select_statement":
					resultTree.RootNode = new EqlSelectNode();
					BuildSelectTree((EqlSelectNode)resultTree.RootNode, rootQueryNode);
					break;
				default:
					throw new EqlException("Not supported operator in abstract tree building.");
			}

			return resultTree;
		}

		private void BuildSelectTree(EqlSelectNode selectNode, ParseTreeNode parseTreeNode)
		{
			foreach (var parseNode in parseTreeNode.ChildNodes)
			{
				switch (parseNode.Term.Name.ToLowerInvariant())
				{
					case "select": //select keyword - ignore it
						continue;
					case "column_item_list":
						BuildSelectFieldList(selectNode.Fields, parseNode);
						continue;
					case "from_clause":
						selectNode.From = new EqlFromNode { EntityName = parseNode.ChildNodes[1].ChildNodes[0].Token.ValueString };
						continue;
					case "where_clause_optional":
						if (parseNode.ChildNodes.Count == 0)
							continue;
						selectNode.Where = new EqlWhereNode();
						BuildWhereNode(selectNode.Where, parseNode);
						continue;
					case "order_clause_optional":
						if (parseNode.ChildNodes.Count == 0)
							continue;
						selectNode.OrderBy = new EqlOrderByNode();
						BuildOrderByNode(selectNode.OrderBy, parseNode);
						continue;
					case "page_clause_optional":
						{
							if (parseNode.ChildNodes.Count == 0)
								continue;

							selectNode.Page = new EqlPageNode();
							var termType = parseNode.ChildNodes[1].Term.Name.ToLowerInvariant();
							switch (termType)
							{
								case "argument":
									{
										string paramName = "@" + parseNode.ChildNodes[1].ChildNodes[1].Token.ValueString;
										if (!ExpectedParameters.Contains(paramName))
											ExpectedParameters.Add(paramName);
										var param = Parameters.SingleOrDefault(x => x.ParameterName == paramName);
										if (param == null)
											throw new EqlException($"PAGE: Parameter '{paramName}' not found.");

										int number;
										if (!Int32.TryParse((param.Value ?? string.Empty).ToString(), out number))
											throw new EqlException($"PAGE: Invalid parameter '{paramName}' value '{param.Value}'.");

										selectNode.Page.Number = number;
									}
									break;
								case "number":
									selectNode.Page.Number = Convert.ToDecimal(parseNode.ChildNodes[1].Token.ValueString);
									break;
								default:
									throw new EqlException("Invalid PAGE argument.");
							}
						}
						continue;
					case "pagesize_clause_optional":
						{
							if (parseNode.ChildNodes.Count == 0)
								continue;

							selectNode.PageSize = new EqlPageSizeNode();
							var termType = parseNode.ChildNodes[1].Term.Name.ToLowerInvariant();
							switch (termType)
							{
								case "argument":
									{
										string paramName = "@" + parseNode.ChildNodes[1].ChildNodes[1].Token.ValueString;
										if (!ExpectedParameters.Contains(paramName))
											ExpectedParameters.Add(paramName);
										var param = Parameters.SingleOrDefault(x => x.ParameterName == paramName);
										if (param == null)
											throw new EqlException($"PAGESIZE: Parameter '{paramName}' not found.");

										int number;
										if (!Int32.TryParse((param.Value ?? string.Empty).ToString(), out number))
											throw new EqlException($"PAGESIZE: Invalid parameter '{paramName}' value '{param.Value}'.");

										selectNode.PageSize.Number = number;
									}
									break;
								case "number":
									selectNode.PageSize.Number = Convert.ToDecimal(parseNode.ChildNodes[1].Token.ValueString);
									break;
								default:
									throw new EqlException("PAGESIZE: Invalid syntax.");
							}
						}
						continue;
					default:
						throw new EqlException("Unknown term in select command syntax parse tree.");
				}
			}
		}

		private void BuildSelectFieldList(List<EqlFieldNode> list, ParseTreeNode parseTreeNode)
		{
			foreach (var parseNode in parseTreeNode.ChildNodes)
			{
				var columnSourceNode = parseNode.ChildNodes[0];
				var fieldNode = columnSourceNode.ChildNodes[0];

				switch (fieldNode.Term.Name)
				{
					case "*":
						{
							list.Add(new EqlWildcardFieldNode());
						}
						continue;
					case "identifier":
						{
							string fieldName = fieldNode.ChildNodes[0].Token.ValueString;
							list.Add(new EqlFieldNode() { FieldName = fieldName });
						}
						continue;
					case "column_relation_list":
						{
							List<EqlRelationInfo> relationInfos = GetRelationInfos(fieldNode);
							//second child node is "." keyword, so we skip it and take the third node,
							//which is an identifier with field name or keyword symbol * for wildcard
							var fieldName = string.Empty;
							if (columnSourceNode.ChildNodes[2].Term.Name == "identifier")
								fieldName = columnSourceNode.ChildNodes[2].ChildNodes[0].Token.ValueString;
							else if (columnSourceNode.ChildNodes[2].Term.Name == "*")
								fieldName = "*";

							EqlRelationFieldNode relFieldNode = null;
							if (fieldName == "*")
								relFieldNode = new EqlRelationWildcardFieldNode();
							else
								relFieldNode = new EqlRelationFieldNode { FieldName = fieldName };

							relFieldNode.Relations.AddRange(relationInfos);
							list.Add(relFieldNode);
						}
						continue;
					default:
						throw new EqlException("Unknown term in select command syntax parse tree.");
				}
			}
		}

		private void BuildOrderByNode(EqlOrderByNode orderByNode, ParseTreeNode parseTreeNode)
		{
			//first 2 nodes are keywords ORDER BY
			var orderbyListNode = parseTreeNode.ChildNodes[2].ChildNodes;

			foreach (var orderMemberNode in orderbyListNode)
			{
				string fieldName = string.Empty;
				var direction = "ASC";
				if (orderMemberNode.ChildNodes[0].ChildNodes[0].Token.ValueString == "@") //argument
				{
					var paramName = "@" + orderMemberNode.ChildNodes[0].ChildNodes[1].Token.ValueString;
					if (!ExpectedParameters.Contains(paramName))
						ExpectedParameters.Add(paramName);
					var param = Parameters.SingleOrDefault(x => x.ParameterName == paramName);
					if (param == null)
						throw new EqlException($"ORDER BY: Parameter '{paramName}' not found.");

					fieldName = (param.Value ?? string.Empty).ToString();
					if (string.IsNullOrWhiteSpace(fieldName))
						throw new EqlException($"ORDER BY: Invalid order field name in parameter '{paramName}'");
				}
				else
					fieldName = orderMemberNode.ChildNodes[0].ChildNodes[0].Token.ValueString;

				if (orderMemberNode.ChildNodes.Count > 1 && orderMemberNode.ChildNodes[1].ChildNodes.Count > 0)
				{
					if (orderMemberNode.ChildNodes[1].ChildNodes[0].ChildNodes.Count > 0 &&
						orderMemberNode.ChildNodes[1].ChildNodes[0].ChildNodes[0].Token.ValueString == "@")
					{
						var paramName = "@" + orderMemberNode.ChildNodes[1].ChildNodes[0].ChildNodes[1].Token.ValueString;
						if (!ExpectedParameters.Contains(paramName))
							ExpectedParameters.Add(paramName);
						var param = Parameters.SingleOrDefault(x => x.ParameterName == paramName);
						if (param == null)
							throw new EqlException($"ORDER BY: Parameter '{paramName}' not found.");

						direction = (param.Value ?? string.Empty).ToString();
						if (string.IsNullOrWhiteSpace(direction))
							throw new EqlException($"ORDER BY: Invalid order direction in parameter '{paramName}'");

						direction = direction.ToUpper();

						if (!(direction == "ASC" || direction == "DESC"))
							throw new EqlException($"ORDER BY: Invalid direction '{direction}'");
					}
					else
						direction = orderMemberNode.ChildNodes[1].ChildNodes[0].Token.ValueString.ToUpper();
				}

				orderByNode.Fields.Add(new EqlOrderByFieldNode { FieldName = fieldName, Direction = direction });

			}
		}

		private void BuildWhereNode(EqlWhereNode whereNode, ParseTreeNode parseTreeNode)
		{
			//first child node is WHERE keyword
			var expressionNode = parseTreeNode.ChildNodes[1];
			if (expressionNode.Term.Name == "binary_expression")
				whereNode.RootExpressionNode = BuildBinaryExpressionNode(expressionNode);
			else if (expressionNode.Term.Name == "term") //when brakets are used for OR in root expression
				whereNode.RootExpressionNode = BuildBinaryExpressionNode(expressionNode.ChildNodes[0].ChildNodes[0]);
			else
				throw new EqlException("Unsupported node type during WHERE clause processing.");
		}

		private EqlBinaryExpressionNode BuildBinaryExpressionNode(ParseTreeNode parseTreeNode)
		{
			if (parseTreeNode.Term.Name != "binary_expression")
				throw new EqlException("Invalid node type during WHERE clause processing.");

			var operand1ParseNode = parseTreeNode.ChildNodes[0];
			var operatorParseNode = parseTreeNode.ChildNodes[1];
			var operand2ParseNode = parseTreeNode.ChildNodes[2];

			EqlBinaryExpressionNode resultNode = new EqlBinaryExpressionNode();
			resultNode.Operator = operatorParseNode.ChildNodes[0].Token.ValueString.ToUpperInvariant();
			resultNode.FirstOperand = BuildOperandNode(operand1ParseNode);
			resultNode.SecondOperand = BuildOperandNode(operand2ParseNode);
			return resultNode;
		}

		private EqlNode BuildOperandNode(ParseTreeNode parseTreeNode)
		{
			if (parseTreeNode.Term.Name == "binary_expression")
				return BuildBinaryExpressionNode(parseTreeNode);

			if (parseTreeNode.Term.Name == "term")
			{
				switch (parseTreeNode.ChildNodes[0].Term.Name.ToLowerInvariant())
				{
					case "expression_identifier":
						if (parseTreeNode.ChildNodes[0].ChildNodes[0].Term != null &&
							parseTreeNode.ChildNodes[0].ChildNodes[0].Term.Name == "column_relation")
						{
							var direction = EqlRelationDirectionType.TargetOrigin;
							if (parseTreeNode.ChildNodes[0].ChildNodes[0].ChildNodes[0].Token.ValueString == "$$")
								direction = EqlRelationDirectionType.OriginTarget;

							var relationNameNode = parseTreeNode.ChildNodes[0].ChildNodes[0].ChildNodes[1];
							var fieldName = parseTreeNode.ChildNodes[0].ChildNodes[2].ChildNodes[0].Token.ValueString;
							EqlRelationFieldNode result = new EqlRelationFieldNode { FieldName = fieldName };
							result.Relations.Add(new EqlRelationInfo { Name = relationNameNode.ChildNodes[0].Token.ValueString, Direction = direction });
							return result;
						}
						else
						{
							var fieldName = parseTreeNode.ChildNodes[0].ChildNodes[0].ChildNodes[0].Token.ValueString;
							return new EqlFieldNode { FieldName = fieldName };
						}
					case "argument":
						var argName = parseTreeNode.ChildNodes[0].ChildNodes[1].Token.ValueString;
						return new EqlArgumentValueNode { ArgumentName = argName };
					case "number":
						return new EqlNumberValueNode { Number = Convert.ToDecimal(parseTreeNode.ChildNodes[0].Token.ValueString) };
					case "string":
						return new EqlTextValueNode { Text = parseTreeNode.ChildNodes[0].Token.ValueString };
					case "null":
						return new EqlKeywordNode { Keyword = "null" };
					case "true":
						return new EqlKeywordNode { Keyword = "true" };
					case "false":
						return new EqlKeywordNode { Keyword = "false" };
					case "expression_list":
						return BuildBinaryExpressionNode(parseTreeNode.ChildNodes[0].ChildNodes[0]);
					default:
						throw new EqlException("Unexpected term during process of binary operations.");
				}
			}

			return null;
		}

		private List<EqlRelationInfo> GetRelationInfos(ParseTreeNode parseTreeNode)
		{
			List<EqlRelationInfo> result = new List<EqlRelationInfo>();
			foreach (var parseNode in parseTreeNode.ChildNodes)
			{
				var direction = EqlRelationDirectionType.TargetOrigin;
				if (parseNode.ChildNodes[0].Token.ValueString == "$$")
					direction = EqlRelationDirectionType.OriginTarget;

				var relationNameNode = parseNode.ChildNodes[1];
				EqlRelationInfo relInfo = new EqlRelationInfo();
				result.Add(new EqlRelationInfo { Name = relationNameNode.ChildNodes[0].Token.ValueString, Direction = direction });
			}
			return result;
		}
	}
}
