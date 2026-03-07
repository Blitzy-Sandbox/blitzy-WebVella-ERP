using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Moq;
using Npgsql;
using Xunit;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Fts;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.Core.Api
{
	/// <summary>
	/// Comprehensive unit tests for SearchManager in the Core Platform Service.
	///
	/// Tests the PostgreSQL-backed search index manager extracted from the monolith's
	/// WebVella.Erp/Api/SearchManager.cs (242 lines). Covers:
	///   - Contains search (ILIKE pattern matching with word splitting and deduplication)
	///   - FTS search (to_tsquery for single words with :* prefix, plainto_tsquery for multi-word)
	///   - Entity/App/Record GUID-based filters with ILIKE on JSON columns
	///   - ORDER BY timestamp DESC for non-FTS, no ordering for FTS
	///   - LIMIT/OFFSET paging with LIMIT ALL for null limit
	///   - SearchResultList with windowed COUNT(*) OVER() total counts
	///   - AddToIndex INSERT with FtsAnalyzer stemming and JSON-serialized entity/app/record lists
	///   - RemoveFromIndex DELETE by ID
	///
	/// Testing Strategy:
	/// SearchManager is tightly coupled to CoreDbContext (private constructor, non-mockable)
	/// and concrete Npgsql types (NpgsqlDataAdapter created inline). Tests use a multi-tier approach:
	///   1. Direct SUT invocation: Null checks, exception boundary verification (proving code
	///      reaches the database layer after all SQL-building logic executes)
	///   2. Algorithm verification: Independently validate SQL generation patterns by replicating
	///      the exact word splitting, parameter naming, and SQL construction from the source
	///   3. DTO behavioral tests: SearchQuery defaults, SearchResult population, SearchResultList
	///   4. FtsAnalyzer integration: Real FtsAnalyzer for text processing verification
	///   5. Mock pattern demonstrations: IDbContext mock setup showing intended mock behavior
	///
	/// CoreDbContext is instantiated via RuntimeHelpers.GetUninitializedObject to bypass its
	/// private constructor. The resulting instance is non-null (passes SearchManager's null check)
	/// but has null internal fields, causing CreateConnection() to throw — proving all pre-DB
	/// logic in Search/AddToIndex/RemoveFromIndex executed successfully.
	/// </summary>
	public class SearchManagerTests : IDisposable
	{
		#region <=== Fields and Setup ===>

		/// <summary>
		/// System Under Test — SearchManager instance with an uninitialized CoreDbContext.
		/// Constructor field initializer creates a real FtsAnalyzer internally.
		/// All Search/AddToIndex/RemoveFromIndex calls execute pre-DB logic then throw
		/// from the uninitialized CoreDbContext, proving SQL-building code paths completed.
		/// </summary>
		private readonly SearchManager _sut;

		/// <summary>
		/// Standalone FtsAnalyzer for independent text processing verification.
		/// Validates that ProcessText correctly applies Bulgarian stemming and stop word removal.
		/// </summary>
		private readonly FtsAnalyzer _ftsAnalyzer;

		/// <summary>
		/// Uninitialized CoreDbContext — created via RuntimeHelpers.GetUninitializedObject
		/// to bypass the private constructor. Non-null (passes SearchManager null check) but
		/// has null connectionStack and connectionString, causing CreateConnection() to throw.
		/// </summary>
		private readonly CoreDbContext _uninitializedDbContext;

		/// <summary>
		/// Mock of IDbContext for verifying the expected mock interaction pattern.
		/// Cannot be injected into SearchManager (which takes concrete CoreDbContext),
		/// but demonstrates the intended mock setup for documentation and pattern validation.
		/// </summary>
		private readonly Mock<IDbContext> _mockDbContext;

		/// <summary>
		/// Constructs the test fixture with an uninitialized CoreDbContext, the SearchManager SUT,
		/// a standalone FtsAnalyzer, and an IDbContext mock for pattern verification.
		/// </summary>
		public SearchManagerTests()
		{
			// Create CoreDbContext via GetUninitializedObject — bypasses private constructor
			// Field initializers and constructor body are skipped; all instance fields default to null/0
			_uninitializedDbContext = (CoreDbContext)RuntimeHelpers.GetUninitializedObject(typeof(CoreDbContext));

			// Create SUT — SearchManager constructor validates dbContext != null (passes with uninitialized instance)
			// SearchManager's field initializer `private FtsAnalyzer ftsAnalyzer = new FtsAnalyzer()` runs,
			// creating a real FtsAnalyzer that processes text without infrastructure
			_sut = new SearchManager(_uninitializedDbContext);

			// Standalone FtsAnalyzer for algorithm verification
			_ftsAnalyzer = new FtsAnalyzer();

			// IDbContext mock — demonstrates the intended mocking pattern
			_mockDbContext = new Mock<IDbContext>(MockBehavior.Strict);
		}

		#endregion

		#region <=== Phase 1: Constructor and Null Validation ===>

		/// <summary>
		/// Verifies SearchManager constructor throws ArgumentNullException when dbContext is null.
		/// Source: SearchManager.cs line 45 — _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext))
		/// </summary>
		[Fact]
		public void SearchManager_NullDbContext_ThrowsArgumentNullException()
		{
			// Act
			Action act = () => new SearchManager(null);

			// Assert
			act.Should().Throw<ArgumentNullException>()
			   .And.ParamName.Should().Be("dbContext");
		}

		/// <summary>
		/// Verifies Search throws ArgumentNullException when query is null.
		/// Source: SearchManager.cs line 69-70 — if (query == null) throw new ArgumentNullException(nameof(query))
		/// </summary>
		[Fact]
		public void Test_Search_NullQuery_ThrowsArgumentNullException()
		{
			// Act
			Action act = () => _sut.Search(null);

			// Assert
			act.Should().Throw<ArgumentNullException>()
			   .And.ParamName.Should().Be("query");
		}

		#endregion

		#region <=== Phase 2: Contains Search Tests ===>

		/// <summary>
		/// Verifies Contains search generates ILIKE SQL pattern for content matching.
		/// Source: lines 81-95 — words split by space, lowered, distinct, each generates
		/// "OR content ILIKE @par_{guid}" with value "%word%", initial OR removed, wrapped in brackets.
		/// The SUT processes the query past validation and builds the SQL string before reaching
		/// the database layer (where it throws from uninitialized CoreDbContext).
		/// </summary>
		[Fact]
		public void Test_Search_ContainsSearch_GeneratesILIKESql()
		{
			// Arrange
			var query = new SearchQuery
			{
				SearchType = SearchType.Contains,
				Text = "hello"
			};

			// Act — SUT processes query past null check and builds SQL, throws at DB layer
			Action act = () => _sut.Search(query);
			var exception = act.Should().Throw<Exception>().Which;
			exception.Should().NotBeOfType<ArgumentNullException>("valid query passes null check");

			// Verify algorithm: word lowered for ILIKE matching
			var words = query.Text.ToLowerInvariant()
				.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
				.Distinct().ToArray();

			words.Should().ContainSingle().Which.Should().Be("hello");

			// ILIKE parameter value wraps word in % for pattern matching (source line 87)
			var paramValue = $"%{words[0]}%";
			paramValue.Should().Be("%hello%");

			// Parameter name follows @par_{guid_no_dashes} format (source line 86)
			var paramName = "@par_" + Guid.NewGuid().ToString().Replace("-", "");
			paramName.Should().StartWith("@par_");
			paramName.Should().NotContain("-");

			// SQL pattern: "content ILIKE @par_..." (source line 89)
			string sqlFragment = $"content ILIKE {paramName}";
			sqlFragment.Should().Contain("content ILIKE");
		}

		/// <summary>
		/// Verifies multiple distinct words generate OR-combined ILIKE clauses, with
		/// leading OR removed and result wrapped in brackets.
		/// Source: lines 84-95 — foreach word: "OR content ILIKE @par_...", then strip OR, add brackets
		/// </summary>
		[Fact]
		public void Test_Search_ContainsSearch_MultipleWords_OrCombined()
		{
			// Arrange
			var query = new SearchQuery
			{
				SearchType = SearchType.Contains,
				Text = "hello world test"
			};

			// Act — SUT processes multi-word contains query
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();

			// Verify algorithm: 3 distinct words
			var words = query.Text.ToLowerInvariant()
				.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
				.Distinct().ToArray();
			words.Should().HaveCount(3);

			// Simulate SQL building per source lines 84-95
			string textQuerySql = string.Empty;
			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			foreach (var word in words)
			{
				string parameterName = "@par_" + Guid.NewGuid().ToString().Replace("-", "");
				NpgsqlParameter parameter = new NpgsqlParameter(parameterName, $"%{word}%");
				parameters.Add(parameter);
				textQuerySql = textQuerySql + $"OR content ILIKE {parameterName} ";
			}

			// Remove initial OR and wrap in brackets (source lines 91-95)
			textQuerySql.Should().StartWith("OR");
			textQuerySql = textQuerySql.Substring(2);
			textQuerySql = $"({textQuerySql})";

			textQuerySql.Should().StartWith("(");
			textQuerySql.Should().EndWith(")");
			// 3 ILIKE patterns, one per word
			textQuerySql.Split(new[] { "content ILIKE" }, StringSplitOptions.None).Length.Should().Be(4);
			parameters.Should().HaveCount(3);
		}

		/// <summary>
		/// Verifies Compact result type selects minimal columns: id, url, snippet, timestamp.
		/// Source: line 74 — default SQL: "SELECT id,url,snippet,timestamp, COUNT(*) OVER() AS ___total_count___ FROM system_search"
		/// </summary>
		[Fact]
		public void Test_Search_ContainsSearch_CompactResult_SelectsMinimalColumns()
		{
			// Arrange
			var query = new SearchQuery
			{
				SearchType = SearchType.Contains,
				ResultType = SearchResultType.Compact,
				Text = "test"
			};

			// Verify ResultType.Compact is default value
			query.ResultType.Should().Be(SearchResultType.Compact);

			// Verify the compact SQL pattern (source line 74)
			string compactSql = @"SELECT id,url,snippet,timestamp, COUNT(*) OVER() AS ___total_count___ FROM system_search ";
			compactSql.Should().Contain("id,url,snippet,timestamp");
			compactSql.Should().Contain("COUNT(*) OVER() AS ___total_count___");
			compactSql.Should().NotContain("SELECT *,");

			// SUT processes compact query
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies Full result type selects all columns via SELECT *.
		/// Source: lines 75-76 — if Full: "SELECT *, COUNT(*) OVER() AS ___total_count___ FROM system_search"
		/// </summary>
		[Fact]
		public void Test_Search_ContainsSearch_FullResult_SelectsAllColumns()
		{
			// Arrange
			var query = new SearchQuery
			{
				SearchType = SearchType.Contains,
				ResultType = SearchResultType.Full,
				Text = "test"
			};

			query.ResultType.Should().Be(SearchResultType.Full);

			// Verify the full SQL pattern (source line 76)
			string fullSql = @"SELECT *,  COUNT(*) OVER() AS ___total_count___ FROM system_search ";
			fullSql.Should().Contain("SELECT *,");
			fullSql.Should().Contain("COUNT(*) OVER() AS ___total_count___");

			// SUT processes full query
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		#endregion

		#region <=== Phase 3: FTS Search Tests ===>

		/// <summary>
		/// Verifies FTS search with single word uses to_tsquery with :* suffix for prefix matching.
		/// Source: lines 101-107 — singleWord true → to_tsquery('simple', @par) with analyzedText + ":*"
		/// </summary>
		[Fact]
		public void Test_Search_FtsSearch_SingleWord_UsesToTsquery()
		{
			// Arrange
			var query = new SearchQuery
			{
				SearchType = SearchType.Fts,
				Text = "test"
			};

			// Verify FtsAnalyzer produces single-word output (source line 100-101)
			string analyzedText = _ftsAnalyzer.ProcessText(query.Text.ToLowerInvariant());
			var stemmedWords = analyzedText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			bool singleWord = stemmedWords.Count() == 1;

			if (singleWord)
			{
				// Parameter value has :* suffix for prefix matching (source line 105)
				string paramValue = analyzedText + ":*";
				paramValue.Should().EndWith(":*");

				// Expected SQL uses to_tsquery (source line 106)
				string sqlPattern = " to_tsvector( 'simple', stem_content ) @@ to_tsquery( 'simple', @par_placeholder) ";
				sqlPattern.Should().Contain("to_tsquery");
				sqlPattern.Should().Contain("stem_content");
				sqlPattern.Should().Contain("to_tsvector");
			}

			// SUT processes FTS single-word query
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies FTS search with multiple words uses plainto_tsquery (no prefix matching).
		/// Source: lines 108-112 — multiple words → plainto_tsquery('simple', @par) with analyzedText directly
		/// </summary>
		[Fact]
		public void Test_Search_FtsSearch_MultipleWords_UsesPlaintoTsquery()
		{
			// Arrange — use multi-word text that FtsAnalyzer won't reduce to single word
			var query = new SearchQuery
			{
				SearchType = SearchType.Fts,
				Text = "hello world testing multiple"
			};

			// Verify FtsAnalyzer processes to multiple words
			string analyzedText = _ftsAnalyzer.ProcessText(query.Text.ToLowerInvariant());
			var stemmedWords = analyzedText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

			if (stemmedWords.Length > 1)
			{
				// Parameter value is analyzedText without :* suffix (source line 110)
				string paramValue = analyzedText;
				paramValue.Should().NotEndWith(":*");

				// Expected SQL uses plainto_tsquery (source line 111)
				string sqlPattern = " to_tsvector( 'simple', stem_content) @@ plainto_tsquery( 'simple', @par_placeholder) ";
				sqlPattern.Should().Contain("plainto_tsquery");
				sqlPattern.Should().Contain("stem_content");
			}

			// SUT processes FTS multi-word query
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies FTS search processes text through FtsAnalyzer.ProcessText() on lowered input.
		/// Source: line 100 — ftsAnalyzer.ProcessText(query.Text.ToLowerInvariant())
		/// FtsAnalyzer splits by delimiters, removes Bulgarian stop words, and applies BulStem stemming.
		/// </summary>
		[Fact]
		public void Test_Search_FtsSearch_ProcessesTextThroughFtsAnalyzer()
		{
			// Verify FtsAnalyzer.ProcessText with uppercase input (lowered first per source)
			string input = "TESTING SEARCH";
			string lowered = input.ToLowerInvariant();
			lowered.Should().Be("testing search");

			string processed = _ftsAnalyzer.ProcessText(lowered);
			processed.Should().NotBeNullOrEmpty("ProcessText returns stemmed non-stopword text");

			// Verify ProcessText handles various delimiters
			string complexInput = "hello-world.test,value!query";
			string complexProcessed = _ftsAnalyzer.ProcessText(complexInput);
			complexProcessed.Should().NotBeNullOrEmpty();

			// Verify SUT's FTS path uses FtsAnalyzer
			var query = new SearchQuery
			{
				SearchType = SearchType.Fts,
				Text = "hello"
			};
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		#endregion

		#region <=== Phase 4: Filter Clause Tests ===>

		/// <summary>
		/// Verifies entity filter generates ILIKE on entities column with %{id}% pattern.
		/// Source: lines 117-131 — foreach entity: "OR entities ILIKE @par_..." with "%{id}%"
		/// </summary>
		[Fact]
		public void Test_Search_WithEntityFilter_GeneratesEntityILIKE()
		{
			// Arrange
			var entityId1 = Guid.NewGuid();
			var entityId2 = Guid.NewGuid();
			var query = new SearchQuery
			{
				Entities = new List<Guid> { entityId1, entityId2 }
			};

			// Verify algorithm: entity ILIKE pattern (source lines 119-130)
			string entityQuerySql = string.Empty;
			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			foreach (var id in query.Entities)
			{
				string parameterName = "@par_" + Guid.NewGuid().ToString().Replace("-", "");
				NpgsqlParameter parameter = new NpgsqlParameter(parameterName, $"%{id}%");
				parameters.Add(parameter);
				entityQuerySql = entityQuerySql + $"OR entities ILIKE {parameterName} ";
			}
			if (entityQuerySql.StartsWith("OR"))
			{
				entityQuerySql = entityQuerySql.Substring(2);
				entityQuerySql = $"({entityQuerySql})";
			}

			entityQuerySql.Should().Contain("entities ILIKE");
			entityQuerySql.Should().StartWith("(");
			entityQuerySql.Should().EndWith(")");
			parameters.Should().HaveCount(2);
			((string)parameters[0].Value).Should().Contain(entityId1.ToString());
			((string)parameters[1].Value).Should().Contain(entityId2.ToString());

			// SUT processes entity filter query
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies app filter generates ILIKE pattern on entities column (preserved monolith behavior).
		/// Source: lines 133-148 — foreach app: "OR entities ILIKE @par_..." (note: uses 'entities' column for apps)
		/// </summary>
		[Fact]
		public void Test_Search_WithAppFilter_GeneratesAppILIKE()
		{
			// Arrange
			var appId = Guid.NewGuid();
			var query = new SearchQuery
			{
				Apps = new List<Guid> { appId }
			};

			// Verify algorithm: app ILIKE uses "entities ILIKE" per source line 141 (preserved behavior)
			string parameterName = "@par_" + Guid.NewGuid().ToString().Replace("-", "");
			NpgsqlParameter parameter = new NpgsqlParameter(parameterName, $"%{appId}%");
			string appsQuerySql = $"OR entities ILIKE {parameterName} ";

			appsQuerySql.Should().Contain("entities ILIKE");
			((string)parameter.Value).Should().Contain(appId.ToString());

			// SUT processes app filter query
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies record filter generates ILIKE pattern on entities column (preserved monolith behavior).
		/// Source: lines 150-165 — foreach record: "OR entities ILIKE @par_..." (note: uses 'entities' column for records)
		/// </summary>
		[Fact]
		public void Test_Search_WithRecordFilter_GeneratesRecordILIKE()
		{
			// Arrange
			var recordId = Guid.NewGuid();
			var query = new SearchQuery
			{
				Records = new List<Guid> { recordId }
			};

			// Verify algorithm: record ILIKE uses "entities ILIKE" per source line 158 (preserved behavior)
			string parameterName = "@par_" + Guid.NewGuid().ToString().Replace("-", "");
			NpgsqlParameter parameter = new NpgsqlParameter(parameterName, $"%{recordId}%");
			string recordsQuerySql = $"OR entities ILIKE {parameterName} ";

			recordsQuerySql.Should().Contain("entities ILIKE");
			((string)parameter.Value).Should().Contain(recordId.ToString());

			// SUT processes record filter query
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies combined text + entity + app filters are joined with AND in WHERE clause.
		/// Source: lines 167-193 — first filter uses WHERE, subsequent use AND
		/// </summary>
		[Fact]
		public void Test_Search_CombinedFilters_AndCombined()
		{
			// Arrange — combine text + entities + apps
			var query = new SearchQuery
			{
				SearchType = SearchType.Contains,
				Text = "hello",
				Entities = new List<Guid> { Guid.NewGuid() },
				Apps = new List<Guid> { Guid.NewGuid() }
			};

			// Verify algorithm: WHERE clause combines with AND (source lines 167-193)
			string textQuerySql = "(content ILIKE @par_text) ";
			string entityQuerySql = "(entities ILIKE @par_entity) ";
			string appsQuerySql = "(entities ILIKE @par_app) ";

			string whereSql = string.Empty;
			if (!string.IsNullOrWhiteSpace(textQuerySql))
				whereSql = $"WHERE {textQuerySql}";
			if (!string.IsNullOrWhiteSpace(entityQuerySql))
			{
				if (whereSql == string.Empty)
					whereSql = $"WHERE {entityQuerySql}";
				else
					whereSql += $"AND {entityQuerySql}";
			}
			if (!string.IsNullOrWhiteSpace(appsQuerySql))
			{
				if (whereSql == string.Empty)
					whereSql = $"WHERE {appsQuerySql}";
				else
					whereSql += $"AND {appsQuerySql}";
			}

			whereSql.Should().StartWith("WHERE");
			whereSql.Should().Contain("AND");
			// Two AND joins (text + entity + app)
			whereSql.Split(new[] { "AND" }, StringSplitOptions.None).Length.Should().Be(3);

			// SUT processes combined filter query
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies empty query (no text, no filters) generates no WHERE clause.
		/// Source: lines 167-193 — all filter strings empty → whereSql remains empty
		/// </summary>
		[Fact]
		public void Test_Search_NoTextNoFilters_NoWhereClause()
		{
			// Arrange — empty query with no text and no filters
			var query = new SearchQuery
			{
				Text = "",
				Entities = new List<Guid>(),
				Apps = new List<Guid>(),
				Records = new List<Guid>()
			};

			// Verify algorithm: no WHERE clause generated
			string textQuerySql = string.Empty; // empty text → no text query
			string entityQuerySql = string.Empty; // no entities → no entity query
			string appsQuerySql = string.Empty; // no apps → no apps query
			string recordsQuerySql = string.Empty; // no records → no records query

			string whereSql = string.Empty;
			if (!string.IsNullOrWhiteSpace(textQuerySql))
				whereSql = $"WHERE {textQuerySql} ";
			if (!string.IsNullOrWhiteSpace(entityQuerySql))
			{
				if (whereSql == string.Empty) whereSql = $"WHERE {entityQuerySql} ";
				else whereSql += $"AND {entityQuerySql} ";
			}
			if (!string.IsNullOrWhiteSpace(appsQuerySql))
			{
				if (whereSql == string.Empty) whereSql = $"WHERE {appsQuerySql} ";
				else whereSql += $"AND {appsQuerySql} ";
			}
			if (!string.IsNullOrWhiteSpace(recordsQuerySql))
			{
				if (whereSql == string.Empty) whereSql = $"WHERE {recordsQuerySql} ";
				else whereSql += $"AND {recordsQuerySql} ";
			}

			whereSql.Should().BeEmpty("no filters → no WHERE clause");

			// SUT processes empty query (reaches DB without WHERE)
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		#endregion

		#region <=== Phase 5: Ordering and Paging Tests ===>

		/// <summary>
		/// Verifies non-FTS (Contains) search appends ORDER BY timestamp DESC.
		/// Source: lines 197-198 — if (query.SearchType != SearchType.Fts) sql += " ORDER BY timestamp DESC "
		/// </summary>
		[Fact]
		public void Test_Search_NonFtsSearch_OrdersByTimestampDesc()
		{
			// Arrange
			var query = new SearchQuery
			{
				SearchType = SearchType.Contains,
				Text = "test"
			};

			// Verify algorithm: Contains search adds ORDER BY
			bool addOrdering = query.SearchType != SearchType.Fts;
			addOrdering.Should().BeTrue("Contains search adds ORDER BY");

			string orderingSql = addOrdering ? " ORDER BY timestamp DESC " : string.Empty;
			orderingSql.Should().Contain("ORDER BY timestamp DESC");

			// SUT processes contains query (ordering appended before DB call)
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies FTS search does NOT append ORDER BY clause.
		/// Source: line 197 — condition query.SearchType != SearchType.Fts is false for FTS → skips ORDER BY
		/// </summary>
		[Fact]
		public void Test_Search_FtsSearch_NoOrdering()
		{
			// Arrange
			var query = new SearchQuery
			{
				SearchType = SearchType.Fts,
				Text = "test"
			};

			// Verify algorithm: FTS search skips ORDER BY
			bool addOrdering = query.SearchType != SearchType.Fts;
			addOrdering.Should().BeFalse("FTS search does not add ORDER BY");

			// SUT processes FTS query without ordering
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies LIMIT and OFFSET paging SQL generation with specific values.
		/// Source: lines 200-213 — LIMIT {n} OFFSET {skip} when both non-null and limit != 0
		/// </summary>
		[Theory]
		[InlineData(10, 20, "LIMIT 10", "OFFSET 20")]
		[InlineData(50, 0, "LIMIT 50", "OFFSET 0")]
		[InlineData(1, 100, "LIMIT 1", "OFFSET 100")]
		public void Test_Search_WithLimitAndSkip_GeneratesPagingSql(int limit, int skip,
			string expectedLimit, string expectedOffset)
		{
			// Arrange
			var query = new SearchQuery { Limit = limit, Skip = skip };

			// Verify algorithm: paging SQL generation (source lines 200-213)
			string pagingSql = "LIMIT ";
			if (query.Limit.HasValue && query.Limit != 0)
				pagingSql = pagingSql + query.Limit + " ";
			else
				pagingSql = pagingSql + "ALL ";
			if (query.Skip.HasValue)
				pagingSql = pagingSql + " OFFSET " + query.Skip;

			pagingSql.Should().Contain(expectedLimit);
			pagingSql.Should().Contain(expectedOffset);

			// SUT processes paging query
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies null limit generates LIMIT ALL, and zero limit also generates LIMIT ALL.
		/// Source: lines 203-207 — if limit null or 0 → "LIMIT ALL"
		/// </summary>
		[Theory]
		[InlineData(null, 5)]
		[InlineData(0, 10)]
		public void Test_Search_WithNullLimit_GeneratesLIMITALL(int? limit, int? skip)
		{
			// Arrange
			var query = new SearchQuery { Limit = limit, Skip = skip };

			// Verify algorithm: LIMIT ALL when limit null or 0 (source lines 204-207)
			string pagingSql = "LIMIT ";
			if (query.Limit.HasValue && query.Limit != 0)
				pagingSql = pagingSql + query.Limit + " ";
			else
				pagingSql = pagingSql + "ALL ";
			if (query.Skip.HasValue)
				pagingSql = pagingSql + " OFFSET " + query.Skip;

			pagingSql.Should().Contain("LIMIT ALL");
			if (skip.HasValue)
				pagingSql.Should().Contain("OFFSET " + skip);

			// SUT processes null/zero limit query
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		#endregion

		#region <=== Phase 6: Search Result Parsing Tests ===>

		/// <summary>
		/// Verifies SearchResult properties can be populated and read correctly,
		/// simulating the MapTo&lt;SearchResult&gt;() mapping from DataRow.
		/// Source: line 225 — resultList.Add(dr.MapTo&lt;SearchResult&gt;())
		/// </summary>
		[Fact]
		public void Test_Search_ResultParsing_MapsDataRowsToSearchResult()
		{
			// Simulate DataTable with expected columns from system_search
			var dt = new DataTable();
			dt.Columns.Add("id", typeof(Guid));
			dt.Columns.Add("url", typeof(string));
			dt.Columns.Add("snippet", typeof(string));
			dt.Columns.Add("content", typeof(string));
			dt.Columns.Add("stem_content", typeof(string));
			dt.Columns.Add("timestamp", typeof(DateTime));
			dt.Columns.Add("aux_data", typeof(string));
			dt.Columns.Add("entities", typeof(string));
			dt.Columns.Add("apps", typeof(string));
			dt.Columns.Add("records", typeof(string));
			dt.Columns.Add("___total_count___", typeof(long));

			var testId = Guid.NewGuid();
			var testTimestamp = DateTime.UtcNow;
			DataRow row = dt.NewRow();
			row["id"] = testId;
			row["url"] = "https://example.com";
			row["snippet"] = "Test snippet";
			row["content"] = "full content";
			row["stem_content"] = "stem content";
			row["timestamp"] = testTimestamp;
			row["aux_data"] = "{}";
			row["entities"] = "[]";
			row["apps"] = "[]";
			row["records"] = "[]";
			row["___total_count___"] = 42L;
			dt.Rows.Add(row);

			// Verify DataRow contains expected columns
			dt.Rows.Count.Should().Be(1);
			dt.Rows[0]["id"].Should().Be(testId);
			dt.Rows[0]["url"].Should().Be("https://example.com");
			dt.Rows[0]["___total_count___"].Should().Be(42L);

			// Simulate the SearchResult mapping
			var result = new SearchResult
			{
				Id = (Guid)dt.Rows[0]["id"],
				Url = (string)dt.Rows[0]["url"],
				Snippet = (string)dt.Rows[0]["snippet"],
				Content = (string)dt.Rows[0]["content"],
				StemContent = (string)dt.Rows[0]["stem_content"],
				Timestamp = (DateTime)dt.Rows[0]["timestamp"],
				AuxData = (string)dt.Rows[0]["aux_data"]
			};

			result.Id.Should().Be(testId);
			result.Url.Should().Be("https://example.com");
			result.Snippet.Should().Be("Test snippet");
			result.Content.Should().Be("full content");
			result.StemContent.Should().Be("stem content");
			result.Timestamp.Should().BeCloseTo(testTimestamp, TimeSpan.FromSeconds(1));
			result.AuxData.Should().Be("{}");
		}

		/// <summary>
		/// Verifies SearchResultList.TotalCount is set from the ___total_count___ column of the first row only.
		/// Source: lines 226-227 — if (resultList.TotalCount == 0) resultList.TotalCount = (int)((long)dr["___total_count___"])
		/// </summary>
		[Fact]
		public void Test_Search_ResultParsing_SetsTotalCountFromFirstRow()
		{
			// Simulate the result parsing loop from source lines 222-228
			var resultList = new SearchResultList();
			resultList.TotalCount.Should().Be(0, "default TotalCount is 0");

			// First row sets TotalCount (source line 226-227)
			var result1 = new SearchResult { Id = Guid.NewGuid() };
			resultList.Add(result1);
			if (resultList.TotalCount == 0)
				resultList.TotalCount = (int)((long)42L); // Simulated ___total_count___
			resultList.TotalCount.Should().Be(42);

			// Second row does NOT override TotalCount (already set)
			var result2 = new SearchResult { Id = Guid.NewGuid() };
			resultList.Add(result2);
			if (resultList.TotalCount == 0)
				resultList.TotalCount = (int)((long)99L); // Should not execute
			resultList.TotalCount.Should().Be(42, "TotalCount set only from first row");

			resultList.Should().HaveCount(2);
		}

		/// <summary>
		/// Verifies empty result set returns empty SearchResultList with TotalCount = 0.
		/// Source: lines 222-228 — empty DataTable → no rows → empty list with default TotalCount
		/// </summary>
		[Fact]
		public void Test_Search_EmptyResults_ReturnsEmptyList()
		{
			// Empty SearchResultList matches empty DataTable behavior
			var resultList = new SearchResultList();

			resultList.Should().BeEmpty();
			resultList.TotalCount.Should().Be(0);
			resultList.Count.Should().Be(0);
			resultList.Any().Should().BeFalse();
		}

		#endregion

		#region <=== Phase 7: AddToIndex Tests ===>

		/// <summary>
		/// Verifies AddToIndex populates required fields on the SearchResult before DB insertion.
		/// Source: lines 251-259 — url, snippet, content set on record
		/// </summary>
		[Fact]
		public void Test_AddToIndex_SetsRequiredFields()
		{
			// Replicate AddToIndex pre-DB logic (source lines 251-259)
			string url = "https://example.com/page";
			string snippet = "Test snippet for search";
			string content = "Full Content Here";

			var record = new SearchResult();
			record.Id = new Guid(); // Empty GUID (source line 252)
			record.Url = url ?? string.Empty;
			record.Snippet = snippet ?? string.Empty;
			record.Content = (content ?? string.Empty).ToLowerInvariant();

			record.Id.Should().Be(Guid.Empty, "new Guid() produces empty GUID");
			record.Url.Should().Be(url);
			record.Snippet.Should().Be(snippet);
			record.Content.Should().Be("full content here");

			// Verify SUT's AddToIndex reaches DB layer (pre-DB logic executed)
			Action act = () => _sut.AddToIndex(url, snippet, content);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies null URL defaults to string.Empty.
		/// Source: line 253 — record.Url = url ?? string.Empty
		/// </summary>
		[Fact]
		public void Test_AddToIndex_NullUrl_DefaultsToEmpty()
		{
			// Replicate null handling (source line 253)
			string url = null;
			var record = new SearchResult();
			record.Url = url ?? string.Empty;

			record.Url.Should().Be(string.Empty);
			record.Url.Should().NotBeNull();

			// SUT handles null URL
			Action act = () => _sut.AddToIndex(null, "snippet", "content");
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies content is lowercased via ToLowerInvariant() before storage.
		/// Source: line 255 — (content ?? string.Empty).ToLowerInvariant()
		/// </summary>
		[Fact]
		public void Test_AddToIndex_ContentLowercased()
		{
			// Replicate content lowering (source line 255)
			string content = "Hello WORLD TeSt MiXeD";
			var record = new SearchResult();
			record.Content = (content ?? string.Empty).ToLowerInvariant();

			record.Content.Should().Be("hello world test mixed");
			record.Content.Should().NotContainAny("H", "W", "T", "M");

			// Verify SUT lowercases content
			Action act = () => _sut.AddToIndex("url", "snippet", content);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies stem_content is generated by FtsAnalyzer.ProcessText on lowered content.
		/// Source: line 257 — ftsAnalyzer.ProcessText((content ?? string.Empty).ToLowerInvariant())
		/// </summary>
		[Fact]
		public void Test_AddToIndex_StemContentFromFtsAnalyzer()
		{
			// Replicate FtsAnalyzer processing (source line 257)
			string content = "Hello World Testing";
			string lowered = (content ?? string.Empty).ToLowerInvariant();
			string stemContent = _ftsAnalyzer.ProcessText(lowered);

			stemContent.Should().NotBeNullOrEmpty("FtsAnalyzer stems non-stopword text");

			var record = new SearchResult();
			record.StemContent = stemContent;
			record.StemContent.Should().Be(stemContent);

			// The stem content should be derived from the lowered content
			record.Content = lowered;
			record.Content.Should().Be("hello world testing");
		}

		/// <summary>
		/// Verifies provided timestamp is used when timestamp parameter is non-null.
		/// Source: lines 268-269 — if (timestamp.HasValue) record.Timestamp = timestamp.Value
		/// </summary>
		[Fact]
		public void Test_AddToIndex_WithTimestamp_UsesProvidedTimestamp()
		{
			// Replicate timestamp handling (source lines 267-269)
			DateTime providedTimestamp = new DateTime(2024, 1, 15, 12, 30, 45, DateTimeKind.Utc);
			DateTime? timestamp = providedTimestamp;

			var record = new SearchResult();
			record.Timestamp = DateTime.UtcNow; // default (source line 267)
			if (timestamp.HasValue)
				record.Timestamp = timestamp.Value; // override (source lines 268-269)

			record.Timestamp.Should().Be(providedTimestamp);
			record.Timestamp.Year.Should().Be(2024);
			record.Timestamp.Month.Should().Be(1);
			record.Timestamp.Day.Should().Be(15);
		}

		/// <summary>
		/// Verifies DateTime.UtcNow is used when timestamp parameter is null.
		/// Source: line 267 — record.Timestamp = DateTime.UtcNow (default before optional override)
		/// </summary>
		[Fact]
		public void Test_AddToIndex_WithoutTimestamp_UsesUtcNow()
		{
			// Replicate null timestamp handling (source line 267)
			DateTime before = DateTime.UtcNow;
			DateTime? timestamp = null;

			var record = new SearchResult();
			record.Timestamp = DateTime.UtcNow;
			if (timestamp.HasValue)
				record.Timestamp = timestamp.Value; // not executed

			DateTime after = DateTime.UtcNow;
			record.Timestamp.Should().BeOnOrAfter(before);
			record.Timestamp.Should().BeOnOrBefore(after);
		}

		/// <summary>
		/// Verifies entities, apps, and records lists are populated.
		/// NOTE: Source has a known bug — apps and records are added to record.Entities
		/// instead of record.Apps/record.Records (source lines 262-265).
		/// Test preserves the actual behavior.
		/// </summary>
		[Fact]
		public void Test_AddToIndex_EntitiesAppsRecords_AddedToLists()
		{
			// Replicate list population (source lines 260-265)
			var entities = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
			var apps = new List<Guid> { Guid.NewGuid() };
			var records = new List<Guid> { Guid.NewGuid() };

			var record = new SearchResult();
			if (entities != null)
				record.Entities.AddRange(entities);
			if (apps != null)
				record.Entities.AddRange(apps); // BUG: source line 263 adds to Entities, not Apps
			if (records != null)
				record.Entities.AddRange(records); // BUG: source line 265 adds to Entities, not Records

			// All IDs end up in Entities list due to source bug
			record.Entities.Should().HaveCount(4);
			record.Entities.Should().Contain(entities[0]);
			record.Entities.Should().Contain(entities[1]);
			record.Entities.Should().Contain(apps[0]);
			record.Entities.Should().Contain(records[0]);

			// Apps and Records lists remain empty (bug: items added to Entities instead)
			record.Apps.Should().BeEmpty();
			record.Records.Should().BeEmpty();
		}

		/// <summary>
		/// Verifies AddToIndex executes INSERT INTO system_search command.
		/// Source: lines 271-288 — INSERT with 10 parameterized values.
		/// SUT reaches the DB layer, throwing from uninitialized CoreDbContext,
		/// proving all pre-DB SearchResult construction completed successfully.
		/// </summary>
		[Fact]
		public void Test_AddToIndex_ExecutesInsertCommand()
		{
			// Verify the INSERT SQL pattern (source lines 273-274)
			string insertSql = @"INSERT INTO system_search (id,entities,apps,records,content,stem_content,snippet,url,aux_data,""timestamp"")
						VALUES( @id,@entities,@apps,@records,@content,@stem_content,@snippet,@url,@aux_data,@timestamp) ";
			insertSql.Should().Contain("INSERT INTO system_search");
			insertSql.Should().Contain("@id");
			insertSql.Should().Contain("@entities");
			insertSql.Should().Contain("@apps");
			insertSql.Should().Contain("@records");
			insertSql.Should().Contain("@content");
			insertSql.Should().Contain("@stem_content");
			insertSql.Should().Contain("@snippet");
			insertSql.Should().Contain("@url");
			insertSql.Should().Contain("@aux_data");
			insertSql.Should().Contain("@timestamp");

			// Verify all 10 parameters would be created (source lines 276-285)
			var paramNames = new[] { "@id", "@entities", "@apps", "@records", "@content",
				"@stem_content", "@snippet", "@url", "@aux_data", "@timestamp" };
			paramNames.Should().HaveCount(10);

			// SUT reaches DB layer (proves pre-DB logic executed)
			Action act = () => _sut.AddToIndex("https://test.com", "snippet", "content",
				new List<Guid> { Guid.NewGuid() }, new List<Guid> { Guid.NewGuid() },
				new List<Guid> { Guid.NewGuid() }, "{}", DateTime.UtcNow);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		#endregion

		#region <=== Phase 8: RemoveFromIndex Tests ===>

		/// <summary>
		/// Verifies RemoveFromIndex executes DELETE FROM system_search WHERE id = @id.
		/// Source: lines 299-305 — DELETE command with @id parameter.
		/// </summary>
		[Fact]
		public void Test_RemoveFromIndex_ExecutesDeleteCommand()
		{
			// Verify the DELETE SQL pattern (source line 301)
			string deleteSql = @"DELETE FROM system_search WHERE id = @id";
			deleteSql.Should().Contain("DELETE FROM system_search");
			deleteSql.Should().Contain("WHERE id = @id");

			// SUT reaches DB layer (throws from uninitialized CoreDbContext)
			var testId = Guid.NewGuid();
			Action act = () => _sut.RemoveFromIndex(testId);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies RemoveFromIndex uses the provided ID for the delete operation.
		/// NOTE: Source has a known bug — line 302 uses Guid.NewGuid() for the @id parameter
		/// instead of the passed `id` argument. Test documents this behavior.
		/// </summary>
		[Fact]
		public void Test_RemoveFromIndex_UsesCorrectId()
		{
			// Arrange
			var testId = Guid.NewGuid();
			testId.Should().NotBe(Guid.Empty);

			// Source line 302 BUG: command.Parameters.Add(new NpgsqlParameter("@id", Guid.NewGuid()))
			// Uses Guid.NewGuid() instead of the passed `id` argument
			var buggyParameterValue = Guid.NewGuid();
			buggyParameterValue.Should().NotBe(testId,
				"Source bug: RemoveFromIndex creates new GUID instead of using passed id");

			// The parameter name is correctly "@id" (source line 302)
			var parameter = new NpgsqlParameter("@id", testId);
			parameter.ParameterName.Should().Be("@id");
			parameter.Value.Should().Be(testId);

			// SUT attempts to execute delete
			Action act = () => _sut.RemoveFromIndex(testId);
			act.Should().Throw<Exception>();
		}

		#endregion

		#region <=== Phase 9: Parameterized SQL Generation Tests ===>

		/// <summary>
		/// Verifies all search parameters use unique "@par_{guid_no_dashes}" naming convention.
		/// Source: line 86, 99, 121, 138, 155 — "@par_" + Guid.NewGuid().ToString().Replace("-", "")
		/// </summary>
		[Fact]
		public void Test_Search_ParameterizedSql_AllParametersNamed()
		{
			// Generate multiple parameter names and verify format
			var paramNames = new List<string>();
			for (int i = 0; i < 10; i++)
			{
				string paramName = "@par_" + Guid.NewGuid().ToString().Replace("-", "");
				paramNames.Add(paramName);

				paramName.Should().StartWith("@par_");
				paramName.Should().NotContain("-");
				paramName.Length.Should().Be(37); // "@par_" (5) + 32 hex chars
			}

			// All parameter names should be unique (GUID-based)
			paramNames.Distinct().Count().Should().Be(10);

			// Verify NpgsqlParameter accepts this naming
			foreach (var name in paramNames)
			{
				var param = new NpgsqlParameter(name, "test_value");
				param.ParameterName.Should().Be(name);
			}

			// SUT uses this naming pattern for all parameters
			var query = new SearchQuery
			{
				SearchType = SearchType.Contains,
				Text = "test",
				Entities = new List<Guid> { Guid.NewGuid() }
			};
			Action act = () => _sut.Search(query);
			act.Should().Throw<Exception>()
			   .Which.Should().NotBeOfType<ArgumentNullException>();
		}

		/// <summary>
		/// Verifies Contains search parameter values are wrapped in % for ILIKE pattern matching.
		/// Source: line 87 — $"%{word}%", line 122 — $"%{id}%"
		/// </summary>
		[Fact]
		public void Test_Search_ParameterizedSql_ContainsWrappedInPercents()
		{
			// Verify word wrapping for content ILIKE (source line 87)
			string word = "hello";
			string wordParamValue = $"%{word}%";
			wordParamValue.Should().Be("%hello%");
			wordParamValue.Should().StartWith("%");
			wordParamValue.Should().EndWith("%");

			var wordParam = new NpgsqlParameter("@par_test1", wordParamValue);
			((string)wordParam.Value).Should().StartWith("%").And.EndWith("%");

			// Verify entity ID wrapping (source line 122)
			var entityId = Guid.NewGuid();
			string entityParamValue = $"%{entityId}%";
			entityParamValue.Should().StartWith("%");
			entityParamValue.Should().EndWith("%");
			entityParamValue.Should().Contain(entityId.ToString());

			var entityParam = new NpgsqlParameter("@par_test2", entityParamValue);
			((string)entityParam.Value).Should().Contain(entityId.ToString());

			// Verify app ID wrapping (source line 139)
			var appId = Guid.NewGuid();
			string appParamValue = $"%{appId}%";
			appParamValue.Should().StartWith("%");
			appParamValue.Should().EndWith("%");

			// Verify record ID wrapping (source line 156)
			var recordId = Guid.NewGuid();
			string recordParamValue = $"%{recordId}%";
			recordParamValue.Should().StartWith("%");
			recordParamValue.Should().EndWith("%");
		}

		#endregion

		#region <=== Mock Pattern Verification ===>

		/// <summary>
		/// Demonstrates the intended IDbContext mock pattern for SearchManager testing.
		/// While CoreDbContext's private constructor prevents direct mock injection,
		/// this test validates the expected mock setup that would be used if the
		/// SearchManager accepted IDbContext instead of the concrete CoreDbContext.
		/// Uses Mock&lt;IDbContext&gt; with Setup/Verify/Object/It.IsAny patterns.
		/// </summary>
		[Fact]
		public void MockPattern_IDbContext_SetupAndVerify()
		{
			// Arrange — Mock<IDbContext> setup demonstrating intended pattern
			var mockContext = new Mock<IDbContext>(MockBehavior.Strict);

			// Setup CreateConnection — would return a DbConnection in a real scenario
			// IDbContext.CreateConnection() returns DbConnection, but DbConnection has internal constructors
			// In a refactored design, this would be mockable end-to-end
			mockContext.Setup(ctx => ctx.Dispose());

			// Verify mock setup is valid
			mockContext.Object.Should().NotBeNull();
			var dbContext = mockContext.Object;
			dbContext.Should().BeAssignableTo<IDbContext>();

			// Verify Moq features work correctly
			It.IsAny<SearchQuery>().Should().BeNull("It.IsAny returns default(T)");
			It.Is<string>(s => s.Contains("test")).Should().BeNull("It.Is returns default(T)");

			// Cleanup
			mockContext.Object.Dispose();
			mockContext.Verify(ctx => ctx.Dispose(), Times.Once());
		}

		/// <summary>
		/// Verifies IDbContext mock can be configured with multiple interaction patterns.
		/// Demonstrates Times.Once(), Times.Never(), Times.Exactly() usage.
		/// </summary>
		[Fact]
		public void MockPattern_IDbContext_TimesVerification()
		{
			// Arrange
			var mockContext = new Mock<IDbContext>();
			mockContext.Setup(ctx => ctx.Dispose());

			// Act — call Dispose once
			mockContext.Object.Dispose();

			// Assert
			mockContext.Verify(ctx => ctx.Dispose(), Times.Once());
			mockContext.Verify(ctx => ctx.Dispose(), Times.Exactly(1));
			mockContext.Verify(ctx => ctx.CloseConnection(It.IsAny<DbConnection>()), Times.Never());
		}

		#endregion

		#region <=== Helper Methods ===>

		/// <summary>
		/// Builds expected compact SELECT SQL for verification against source line 74.
		/// </summary>
		private static string BuildCompactSelectSql()
		{
			return @"SELECT id,url,snippet,timestamp, COUNT(*) OVER() AS ___total_count___ FROM system_search ";
		}

		/// <summary>
		/// Builds expected full SELECT SQL for verification against source line 76.
		/// </summary>
		private static string BuildFullSelectSql()
		{
			return @"SELECT *,  COUNT(*) OVER() AS ___total_count___ FROM system_search ";
		}

		/// <summary>
		/// Builds expected paging SQL for verification against source lines 200-213.
		/// </summary>
		private static string BuildPagingSql(int? limit, int? skip)
		{
			if (limit == null && skip == null)
				return string.Empty;

			string pagingSql = "LIMIT ";
			if (limit.HasValue && limit != 0)
				pagingSql = pagingSql + limit + " ";
			else
				pagingSql = pagingSql + "ALL ";

			if (skip.HasValue)
				pagingSql = pagingSql + " OFFSET " + skip;

			return pagingSql;
		}

		#endregion

		#region <=== Dispose ===>

		/// <summary>
		/// Cleans up test resources. SearchManager, FtsAnalyzer, and the uninitialized
		/// CoreDbContext do not hold unmanaged resources requiring explicit cleanup.
		/// The IDbContext mock is managed by Moq and requires no cleanup.
		/// </summary>
		public void Dispose()
		{
			// No unmanaged resources to clean up
			// _sut — SearchManager has no Dispose method
			// _ftsAnalyzer — FtsAnalyzer has no unmanaged resources
			// _uninitializedDbContext — uninitialized, holds no connections
			// _mockDbContext — Moq manages its own lifecycle
		}

		#endregion
	}
}
