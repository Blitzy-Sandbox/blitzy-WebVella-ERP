using Irony.Parsing;
using System;
using System.Collections.Generic;
using System.Linq;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.SharedKernel.Eql
{
	#region <--- Injectable Interfaces for EQL Engine Decoupling --->

	/// <summary>
	/// Abstracts entity metadata access for the EQL engine, replacing direct
	/// <c>EntityManager</c> usage from the monolith. Each microservice provides
	/// its own implementation scoped to its owned entities, ensuring the EQL engine
	/// operates within service boundaries (AAP 0.7.3).
	/// </summary>
	public interface IEqlEntityProvider
	{
		/// <summary>
		/// Reads entity metadata by name. Returns null if not found.
		/// Corresponds to monolith's <c>EntityManager.ReadEntity(name).Object</c>.
		/// </summary>
		/// <param name="entityName">The name of the entity to read.</param>
		/// <returns>The entity definition, or null if not found.</returns>
		Entity ReadEntity(string entityName);

		/// <summary>
		/// Reads entity metadata by ID. Returns null if not found.
		/// Corresponds to monolith's <c>EntityManager.ReadEntity(id).Object</c>.
		/// </summary>
		/// <param name="entityId">The unique identifier of the entity.</param>
		/// <returns>The entity definition, or null if not found.</returns>
		Entity ReadEntity(Guid entityId);

		/// <summary>
		/// Reads all entities visible to this service.
		/// Corresponds to monolith's <c>EntityManager.ReadEntities().Object</c>.
		/// Used by <c>EqlBuilder.Sql.BuildSql</c> for entity lookup by name and field resolution.
		/// </summary>
		/// <returns>List of all entities with their fields, or empty list if none exist.</returns>
		List<Entity> ReadEntities();
	}

	/// <summary>
	/// Abstracts entity relation metadata access for the EQL engine, replacing direct
	/// <c>EntityRelationManager</c> usage from the monolith. Each microservice provides
	/// its own implementation scoped to its owned relations.
	/// </summary>
	public interface IEqlRelationProvider
	{
		/// <summary>
		/// Reads all entity relations visible to this service.
		/// Corresponds to monolith's <c>EntityRelationManager.Read().Object</c>.
		/// Used by <c>EqlBuilder.Sql.ProcessRelationField</c> and <c>ProcessWhereJoins</c>
		/// for relation traversal SQL generation.
		/// </summary>
		/// <returns>List of all entity relations, or empty list if none exist.</returns>
		List<EntityRelation> Read();

		/// <summary>
		/// Reads a single entity relation by name.
		/// </summary>
		/// <param name="name">The name of the relation to read.</param>
		/// <returns>The entity relation, or null if not found.</returns>
		EntityRelation Read(string name);

		/// <summary>
		/// Reads a single entity relation by ID.
		/// </summary>
		/// <param name="id">The unique identifier of the relation.</param>
		/// <returns>The entity relation, or null if not found.</returns>
		EntityRelation Read(Guid id);
	}

	/// <summary>
	/// Abstracts record search hook execution for the EQL engine, replacing direct
	/// <c>RecordHookManager</c> calls from the monolith. In the microservice architecture,
	/// hooks are replaced by domain events published on the message bus; this interface
	/// preserves backward compatibility for services that still use the hook pattern internally.
	/// Services that do not use search hooks pass <c>null</c> for this dependency in the
	/// <see cref="EqlBuilder"/> constructor, which disables hook execution entirely.
	/// </summary>
	public interface IEqlHookProvider
	{
		/// <summary>
		/// Checks whether any search hooks are registered for the specified entity.
		/// Corresponds to monolith's <c>RecordHookManager.ContainsAnyHooksForEntity(entityName)</c>.
		/// </summary>
		/// <param name="entityName">The entity name to check for registered hooks.</param>
		/// <returns>True if any search hooks exist for the entity; false otherwise.</returns>
		bool ContainsAnyHooksForEntity(string entityName);

		/// <summary>
		/// Executes pre-search record hooks for the specified entity.
		/// Hooks may modify the select node or add errors to cancel the search.
		/// Corresponds to monolith's <c>RecordHookManager.ExecutePreSearchRecordHooks</c>.
		/// </summary>
		/// <param name="entityName">The entity name to execute hooks for.</param>
		/// <param name="selectNode">The EQL select node that hooks can inspect or modify.</param>
		/// <param name="errors">Error list that hooks can append to for search cancellation.</param>
		void ExecutePreSearchRecordHooks(string entityName, EqlSelectNode selectNode, List<EqlError> errors);

		/// <summary>
		/// Executes post-search record hooks for the specified entity.
		/// Hooks may modify the result set after query execution.
		/// Corresponds to monolith's <c>RecordHookManager.ExecutePostSearchRecordHooks</c>.
		/// </summary>
		/// <param name="entityName">The entity name to execute hooks for.</param>
		/// <param name="records">The entity record list that hooks can inspect or modify.</param>
		void ExecutePostSearchRecordHooks(string entityName, EntityRecordList records);
	}

	/// <summary>
	/// Abstracts entity-level permission checking for the EQL engine, replacing direct
	/// <c>SecurityContext</c> usage from the monolith. Each microservice provides its own
	/// implementation that checks JWT claims against entity record permissions.
	/// </summary>
	public interface IEqlSecurityProvider
	{
		/// <summary>
		/// Checks whether the current user has the specified permission for the given entity.
		/// Corresponds to monolith's permission checks using <c>SecurityContext.CurrentUser</c>
		/// against <c>Entity.RecordPermissions</c>.
		/// </summary>
		/// <param name="permission">The permission to check (Read, Create, Update, Delete).</param>
		/// <param name="entity">The entity to check permissions against.</param>
		/// <returns>True if the current user has the permission; false otherwise.</returns>
		bool HasEntityPermission(EntityPermission permission, Entity entity);
	}

	/// <summary>
	/// Abstracts field value extraction from raw database result objects for the EQL engine.
	/// Each microservice provides its own implementation that handles the per-field-type
	/// deserialization from Npgsql data reader results (JObject tokens) to typed .NET values.
	/// Corresponds to monolith's <c>DbRecordRepository.ExtractFieldValue</c>.
	/// </summary>
	public interface IEqlFieldValueExtractor
	{
		/// <summary>
		/// Extracts and converts a raw value (typically a JToken from row_to_json) to a
		/// properly typed .NET value based on the field's type definition.
		/// </summary>
		/// <param name="jToken">The raw value from the database query result.</param>
		/// <param name="field">The field metadata defining the expected type and conversion rules.</param>
		/// <returns>The properly typed field value, or null for null database values.</returns>
		object ExtractFieldValue(object jToken, Field field);
	}

	#endregion

	/// <summary>
	/// Orchestrates EQL (Entity Query Language) to PostgreSQL SQL translation.
	/// <para>
	/// Migrated from the monolith's <c>WebVella.Erp.Eql.EqlBuilder</c> with namespace
	/// updates for the SharedKernel. The parsing, abstract tree building, field selection,
	/// WHERE clause construction, ORDER BY, pagination, and relation traversal ($/$$) are
	/// preserved identically to produce functionally equivalent SQL output (AAP 0.8.3).
	/// </para>
	/// <para>
	/// In the microservice architecture, service-specific dependencies (entity metadata,
	/// relation metadata, and search hook execution) are injected via
	/// <see cref="IEqlEntityProvider"/>, <see cref="IEqlRelationProvider"/>, and
	/// <see cref="IEqlHookProvider"/> interfaces rather than referencing concrete managers.
	/// Each service provides its own implementations during EQL engine initialization.
	/// </para>
	/// <para>
	/// This is a partial class — the SQL generation logic resides in <c>EqlBuilder.Sql.cs</c>.
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
		/// Provides entity metadata for EQL SQL generation.
		/// Each microservice injects its own implementation that reads from its owned database.
		/// Replaces monolith's direct <c>new EntityManager(CurrentContext)</c> instantiation.
		/// </summary>
		internal readonly IEqlEntityProvider _entityProvider;

		/// <summary>
		/// Provides entity relation metadata for EQL SQL generation.
		/// Each microservice injects its own implementation that reads from its owned database.
		/// Replaces monolith's direct <c>new EntityRelationManager(CurrentContext)</c> instantiation.
		/// </summary>
		internal readonly IEqlRelationProvider _relationProvider;

		/// <summary>
		/// Optional hook provider for pre/post-search record hooks.
		/// In the monolith, <c>RecordHookManager</c> executed hooks synchronously.
		/// In microservices, this is replaced by event-driven patterns; the provider can be null
		/// if the service does not use search hooks.
		/// </summary>
		private readonly IEqlHookProvider _hookProvider;

		/// <summary>
		/// Creates an EqlBuilder instance with minimal parameters.
		/// Entity/relation/hook providers can be injected as optional parameters.
		/// </summary>
		/// <param name="text">The EQL query text to parse and translate.</param>
		/// <param name="entityProvider">Optional entity metadata provider; null disables entity resolution in build.</param>
		/// <param name="relationProvider">Optional relation metadata provider; null disables relation resolution in build.</param>
		/// <param name="hookProvider">Optional search hook provider; null disables hook execution.</param>
		public EqlBuilder(string text,
			IEqlEntityProvider entityProvider = null,
			IEqlRelationProvider relationProvider = null,
			IEqlHookProvider hookProvider = null)
			: this(text, (IDbContext)null, (EqlSettings)null, entityProvider, relationProvider, hookProvider)
		{
		}

		/// <summary>
		/// Creates an EqlBuilder instance with an explicit database context.
		/// </summary>
		/// <param name="text">The EQL query text to parse and translate.</param>
		/// <param name="currentContext">Database context for connection creation; null uses DbContextAccessor.Current.</param>
		/// <param name="entityProvider">Optional entity metadata provider.</param>
		/// <param name="relationProvider">Optional relation metadata provider.</param>
		/// <param name="hookProvider">Optional search hook provider.</param>
		public EqlBuilder(string text, IDbContext currentContext,
			IEqlEntityProvider entityProvider = null,
			IEqlRelationProvider relationProvider = null,
			IEqlHookProvider hookProvider = null)
			: this(text, currentContext, (EqlSettings)null, entityProvider, relationProvider, hookProvider)
		{
		}

		/// <summary>
		/// Creates an EqlBuilder instance with explicit database context and settings.
		/// This is the primary constructor that performs all initialization.
		/// </summary>
		/// <param name="text">The EQL query text to parse and translate.</param>
		/// <param name="currentContext">Database context for connection creation; null uses DbContextAccessor.Current.</param>
		/// <param name="settings">EQL settings controlling DISTINCT and IncludeTotal behavior; null uses defaults.</param>
		/// <param name="entityProvider">Optional entity metadata provider.</param>
		/// <param name="relationProvider">Optional relation metadata provider.</param>
		/// <param name="hookProvider">Optional search hook provider.</param>
		public EqlBuilder(string text, IDbContext currentContext, EqlSettings settings,
			IEqlEntityProvider entityProvider = null,
			IEqlRelationProvider relationProvider = null,
			IEqlHookProvider hookProvider = null)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
			Text = text;
			_entityProvider = entityProvider;
			_relationProvider = relationProvider;
			_hookProvider = hookProvider;
			if (settings != null)
				Settings = settings;
		}

		/// <summary>
		/// Creates an EqlBuilder instance with explicit settings but no database context.
		/// </summary>
		/// <param name="text">The EQL query text to parse and translate.</param>
		/// <param name="settings">EQL settings controlling DISTINCT and IncludeTotal behavior.</param>
		/// <param name="entityProvider">Optional entity metadata provider.</param>
		/// <param name="relationProvider">Optional relation metadata provider.</param>
		/// <param name="hookProvider">Optional search hook provider.</param>
		public EqlBuilder(string text, EqlSettings settings,
			IEqlEntityProvider entityProvider = null,
			IEqlRelationProvider relationProvider = null,
			IEqlHookProvider hookProvider = null)
			: this(text, (IDbContext)null, settings, entityProvider, relationProvider, hookProvider)
		{
		}

		/// <summary>
		/// Builds EQL to SQL translation with full field selection, WHERE, ORDER BY,
		/// PAGE/PAGESIZE, and relation traversal.
		/// <para>
		/// Preserved from monolith's <c>EqlBuilder.Build()</c> (lines 63-114). The parsing,
		/// AST construction, pre-search hook execution, SQL generation, error collection, and
		/// parameter propagation are functionally identical.
		/// </para>
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

				// Hook execution: in the monolith this was RecordHookManager.ContainsAnyHooksForEntity
				// / ExecutePreSearchRecordHooks. In microservices, hooks are replaced by events;
				// the IEqlHookProvider provides backward compatibility for services that still
				// use the hook pattern. Null-safe: if no provider is set, hooks are skipped.
				bool hooksExists = _hookProvider?.ContainsAnyHooksForEntity(selectNode.From.EntityName) ?? false;

				if (hooksExists)
				{
					_hookProvider.ExecutePreSearchRecordHooks(selectNode.From.EntityName, selectNode, errors);
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

		/// <summary>
		/// Parses EQL text into an Irony <see cref="ParseTree"/> using the <see cref="EqlGrammar"/>.
		/// Collects any parse errors into the provided error list.
		/// Preserved identically from monolith's <c>EqlBuilder.Parse()</c> (lines 116-141).
		/// </summary>
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

		/// <summary>
		/// Converts an Irony ParseTree into an <see cref="EqlAbstractTree"/> with typed AST nodes.
		/// Currently supports only SELECT statements. Throws <see cref="EqlException"/> for
		/// unsupported statement types.
		/// Preserved identically from monolith's <c>EqlBuilder.BuildAbstractTree()</c> (lines 143-158).
		/// </summary>
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

		/// <summary>
		/// Populates an <see cref="EqlSelectNode"/> from a parsed SELECT statement node.
		/// Handles column list, FROM clause, WHERE clause, ORDER BY, PAGE, and PAGESIZE.
		/// Parameter binding (@ prefixed) is resolved from the <see cref="Parameters"/> list.
		/// Preserved identically from monolith's <c>EqlBuilder.BuildSelectTree()</c> (lines 160-256).
		/// </summary>
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

		/// <summary>
		/// Builds the field selection list from the parsed column_item_list node.
		/// Handles simple field references, wildcard (*), and relation field references ($ / $$).
		/// Preserved identically from monolith's <c>EqlBuilder.BuildSelectFieldList()</c> (lines 258-303).
		/// </summary>
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

		/// <summary>
		/// Builds the ORDER BY clause from the parsed order_clause_optional node.
		/// Handles both literal field names and parameterized field names/directions.
		/// Preserved identically from monolith's <c>EqlBuilder.BuildOrderByNode()</c> (lines 305-358).
		/// </summary>
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

		/// <summary>
		/// Builds the WHERE clause from the parsed where_clause_optional node.
		/// Delegates to <see cref="BuildBinaryExpressionNode"/> for expression tree construction.
		/// Preserved identically from monolith's <c>EqlBuilder.BuildWhereNode()</c> (lines 360-370).
		/// </summary>
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

		/// <summary>
		/// Builds a binary expression node (operator + two operands) from the parsed binary_expression node.
		/// Recursively builds operand nodes for nested expressions.
		/// Preserved identically from monolith's <c>EqlBuilder.BuildBinaryExpressionNode()</c> (lines 372-386).
		/// </summary>
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

		/// <summary>
		/// Builds an operand node from a parsed expression operand. Handles:
		/// - Binary expressions (recursive)
		/// - Field identifiers (simple and relation-prefixed)
		/// - Arguments (@ parameters)
		/// - Number/string/null/true/false literals
		/// - Parenthesized expression lists
		/// Preserved identically from monolith's <c>EqlBuilder.BuildOperandNode()</c> (lines 388-437).
		/// </summary>
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

		/// <summary>
		/// Extracts relation traversal information ($ for target-to-origin, $$ for origin-to-target)
		/// from a parsed column_relation_list node. Returns a list of <see cref="EqlRelationInfo"/>
		/// objects encoding each relation's name and direction.
		/// Preserved identically from monolith's <c>EqlBuilder.GetRelationInfos()</c> (lines 439-453).
		/// </summary>
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
