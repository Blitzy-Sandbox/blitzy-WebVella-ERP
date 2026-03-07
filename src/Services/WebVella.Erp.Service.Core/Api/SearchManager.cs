using Newtonsoft.Json;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Utilities;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.SharedKernel.Fts;

namespace WebVella.Erp.Service.Core.Api
{
	/// <summary>
	/// PostgreSQL-backed search index manager for the Core service.
	/// Provides full-text search (FTS via to_tsquery/plainto_tsquery) and
	/// pattern-matching search (ILIKE) against the system_search table.
	///
	/// Adapted from the monolith's WebVella.Erp.Api.SearchManager (242 lines).
	/// All business logic, SQL queries, and parameterization are preserved exactly.
	///
	/// Key changes from monolith:
	/// - Namespace: WebVella.Erp.Api → WebVella.Erp.Service.Core.Api
	/// - Database access: DbContext.Current → injected CoreDbContext instance
	/// - Imports: SharedKernel models/utilities replace monolith-internal references
	/// </summary>
	public class SearchManager
	{
		private readonly CoreDbContext _dbContext;
		private FtsAnalyzer ftsAnalyzer = new FtsAnalyzer();

		/// <summary>
		/// Initializes a new instance of the SearchManager with the Core service's
		/// ambient database context for executing parameterized SQL queries.
		/// </summary>
		/// <param name="dbContext">
		/// The Core service database context providing CreateConnection() for
		/// PostgreSQL access. Must not be null.
		/// </param>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="dbContext"/> is null.
		/// </exception>
		public SearchManager(CoreDbContext dbContext)
		{
			_dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
		}

		/// <summary>
		/// Executes a search against the system_search table using either ILIKE pattern
		/// matching (SearchType.Contains) or PostgreSQL full-text search (SearchType.Fts).
		///
		/// Supports filtering by entity IDs, app IDs, and record IDs via ILIKE on JSON columns.
		/// Results are paginated via LIMIT/OFFSET and include a total count via COUNT(*) OVER() window function.
		///
		/// For FTS mode:
		/// - Single-word queries use to_tsquery with lexeme prefix matching (word:*)
		/// - Multi-word queries use plainto_tsquery for phrase matching
		/// - Text is preprocessed through FtsAnalyzer (Bulgarian stemming + stop word removal)
		///
		/// For Contains mode:
		/// - Each word generates a parameterized ILIKE clause on the content column
		/// - Results are ordered by timestamp DESC
		/// </summary>
		/// <param name="query">The search query parameters including text, filters, and pagination.</param>
		/// <returns>A SearchResultList with matched results and total count for pagination.</returns>
		/// <exception cref="ArgumentNullException">Thrown when query is null.</exception>
		public SearchResultList Search(SearchQuery query)
		{
			if (query == null)
				throw new ArgumentNullException(nameof(query));

			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			
			string sql = @"SELECT id,url,snippet,timestamp, COUNT(*) OVER() AS ___total_count___ FROM system_search ";
			if( query.ResultType == SearchResultType.Full )
				sql = @"SELECT *,  COUNT(*) OVER() AS ___total_count___ FROM system_search ";

			string textQuerySql = string.Empty;
			if (!string.IsNullOrWhiteSpace(query.Text))
			{
				if (query.SearchType == SearchType.Contains)
				{
					var words = query.Text.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
					foreach (var word in words)
					{
						string parameterName = "@par_" + Guid.NewGuid().ToString().Replace("-", "");
						NpgsqlParameter parameter = new NpgsqlParameter(parameterName, $"%{word}%");
						parameters.Add(parameter);
						textQuerySql = textQuerySql + $"OR content ILIKE {parameterName} ";
					}
					if (textQuerySql.StartsWith("OR"))
					{
						textQuerySql = textQuerySql.Substring(2); //remove initial OR
						textQuerySql = $"({textQuerySql})"; //add brackets
					}
				}
				else if (query.SearchType == SearchType.Fts)
				{
					string parameterName = "@par_" + Guid.NewGuid().ToString().Replace("-", "");
					string analizedText = ftsAnalyzer.ProcessText(query.Text.ToLowerInvariant());
					bool singleWord = analizedText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Count() == 1;
					if (singleWord)
					{
						//search for all lexemes starting with this word 
						parameters.Add(new NpgsqlParameter(parameterName, analizedText + ":*" ));
						textQuerySql = textQuerySql + " to_tsvector( 'simple', stem_content ) @@ to_tsquery( 'simple', " + parameterName + ") ";
					}
					else
					{
						parameters.Add(new NpgsqlParameter(parameterName, analizedText));
						textQuerySql = textQuerySql + " to_tsvector( 'simple', stem_content) @@ plainto_tsquery( 'simple', " + parameterName + ") ";
					}
				}
			}

			string entityQuerySql = string.Empty;
			if (query.Entities.Any())
			{
				foreach (var id in query.Entities)
				{
					string parameterName = "@par_" + Guid.NewGuid().ToString().Replace("-", "");
					NpgsqlParameter parameter = new NpgsqlParameter(parameterName, $"%{id}%");
					parameters.Add(parameter);
					entityQuerySql = entityQuerySql + $"OR entities ILIKE {parameterName} ";
				}
				if (entityQuerySql.StartsWith("OR"))
				{
					entityQuerySql = entityQuerySql.Substring(2); //remove initial OR
					entityQuerySql = $"({entityQuerySql})"; //add brackets
				}
			}

			string appsQuerySql = string.Empty;
			if (query.Apps.Any())
			{
				foreach (var id in query.Apps)
				{
					string parameterName = "@par_" + Guid.NewGuid().ToString().Replace("-", "");
					NpgsqlParameter parameter = new NpgsqlParameter(parameterName, $"%{id}%");
					parameters.Add(parameter);
					appsQuerySql = appsQuerySql + $"OR entities ILIKE {parameterName} ";
				}
				if (appsQuerySql.StartsWith("OR"))
				{
					appsQuerySql = entityQuerySql.Substring(2); //remove initial OR
					appsQuerySql = $"({appsQuerySql})"; //add brackets
				}
			}

			string recordsQuerySql = string.Empty;
			if (query.Records.Any())
			{
				foreach (var id in query.Records)
				{
					string parameterName = "@par_" + Guid.NewGuid().ToString().Replace("-", "");
					NpgsqlParameter parameter = new NpgsqlParameter(parameterName, $"%{id}%");
					parameters.Add(parameter);
					recordsQuerySql = recordsQuerySql + $"OR entities ILIKE {parameterName} ";
				}
				if (recordsQuerySql.StartsWith("OR"))
				{
					recordsQuerySql = entityQuerySql.Substring(2); //remove initial OR
					recordsQuerySql = $"({recordsQuerySql})"; //add brackets
				}
			}

			string whereSql = string.Empty;
			if (!string.IsNullOrWhiteSpace(textQuerySql))
			{
				whereSql = $"WHERE {textQuerySql} ";
			}

			if (!string.IsNullOrWhiteSpace(entityQuerySql))
			{
				if( whereSql == string.Empty)
					whereSql = $"WHERE {entityQuerySql} ";
				else
					whereSql = $"AND {entityQuerySql} ";
			}
			if (!string.IsNullOrWhiteSpace(appsQuerySql))
			{
				if (whereSql == string.Empty)
					whereSql = $"WHERE {appsQuerySql} ";
				else
					whereSql = $"AND {appsQuerySql} ";
			}
			if (!string.IsNullOrWhiteSpace(recordsQuerySql))
			{
				if (whereSql == string.Empty)
					whereSql = $"WHERE {recordsQuerySql} ";
				else
					whereSql = $"AND {recordsQuerySql} ";
			}

			sql = sql + whereSql;

			if( query.SearchType != SearchType.Fts )
				sql = sql + " ORDER BY timestamp DESC ";

			string pagingSql = string.Empty;
			if (query.Limit != null || query.Skip != null)
			{
				pagingSql = "LIMIT ";
				if (query.Limit.HasValue && query.Limit != 0)
					pagingSql = pagingSql + query.Limit + " ";
				else
					pagingSql = pagingSql + "ALL ";

				if (query.Skip.HasValue)
					pagingSql = pagingSql + " OFFSET " + query.Skip;

				sql = sql + pagingSql;
			}

			DataTable dt = new DataTable();
			using (var connection = _dbContext.CreateConnection())
			{
				var command = connection.CreateCommand(sql, parameters: parameters);
				command.CommandTimeout = 60;
				new NpgsqlDataAdapter(command).Fill(dt);

				SearchResultList resultList = new SearchResultList();
				foreach (DataRow dr in dt.Rows)
				{
					resultList.Add(dr.MapTo<SearchResult>());
					if (resultList.TotalCount == 0)
						resultList.TotalCount = (int)((long)dr["___total_count___"]);
				}

				return resultList;
			}
		}

		/// <summary>
		/// Adds a new entry to the system_search index table.
		/// Content is lowered and stem-processed via FtsAnalyzer for Bulgarian FTS support.
		/// Entity, app, and record associations are serialized as JSON arrays.
		/// </summary>
		/// <param name="url">The URL associated with this search entry.</param>
		/// <param name="snippet">A short text snippet for display in search results.</param>
		/// <param name="content">The full text content to be indexed (lowercased and stemmed).</param>
		/// <param name="entities">Optional list of entity GUIDs to associate with this entry.</param>
		/// <param name="apps">Optional list of app GUIDs to associate with this entry.</param>
		/// <param name="records">Optional list of record GUIDs to associate with this entry.</param>
		/// <param name="auxData">Optional auxiliary data stored alongside the entry.</param>
		/// <param name="timestamp">Optional timestamp; defaults to DateTime.UtcNow if not provided.</param>
		/// <returns>The created SearchResult with all fields populated.</returns>
		public SearchResult AddToIndex(string url, string snippet, string content, List<Guid> entities = null,
				List<Guid> apps = null, List<Guid> records = null, string auxData = null, DateTime? timestamp = null)
		{
			SearchResult record = new SearchResult();
			record.Id = new Guid();
			record.Url = url ?? string.Empty;
			record.Snippet = snippet ?? string.Empty;
			record.Content = (content ?? string.Empty).ToLowerInvariant(); ;
			
			record.StemContent = ftsAnalyzer.ProcessText((content ?? string.Empty).ToLowerInvariant());

			record.AuxData = auxData ?? string.Empty;
			if (entities != null)
				record.Entities.AddRange(entities);
			if (apps != null)
				record.Entities.AddRange(apps);
			if (records != null)
				record.Entities.AddRange(records);

			record.Timestamp = DateTime.UtcNow;
			if (timestamp.HasValue)
				record.Timestamp = timestamp.Value;

			using (var connection = _dbContext.CreateConnection())
			{
				var command = connection.CreateCommand(@"INSERT INTO system_search (id,entities,apps,records,content,stem_content,snippet,url,aux_data,""timestamp"")
						VALUES( @id,@entities,@apps,@records,@content,@stem_content,@snippet,@url,@aux_data,@timestamp) ");

				command.Parameters.Add(new NpgsqlParameter("@id", Guid.NewGuid()));
				command.Parameters.Add(new NpgsqlParameter("@entities", JsonConvert.SerializeObject(record.Entities ?? new List<Guid>())));
				command.Parameters.Add(new NpgsqlParameter("@apps", JsonConvert.SerializeObject(record.Apps ?? new List<Guid>())));
				command.Parameters.Add(new NpgsqlParameter("@records", JsonConvert.SerializeObject(record.Records ?? new List<Guid>())));
				command.Parameters.Add(new NpgsqlParameter("@content", record.Content ?? string.Empty));
				command.Parameters.Add(new NpgsqlParameter("@stem_content", record.StemContent ?? string.Empty));
				command.Parameters.Add(new NpgsqlParameter("@snippet", record.Snippet ?? string.Empty));
				command.Parameters.Add(new NpgsqlParameter("@url", record.Url ?? string.Empty));
				command.Parameters.Add(new NpgsqlParameter("@aux_data", record.AuxData ?? string.Empty));
				command.Parameters.Add(new NpgsqlParameter("@timestamp", record.Timestamp));
				command.ExecuteNonQuery();

			}

			return record;
		}

		/// <summary>
		/// Removes a search entry from the system_search index by its ID.
		/// </summary>
		/// <param name="id">The unique identifier of the search entry to remove.</param>
		public void RemoveFromIndex(Guid id)
		{
			using (var connection = _dbContext.CreateConnection())
			{
				var command = connection.CreateCommand(@"DELETE FROM system_search WHERE id = @id");
				command.Parameters.Add(new NpgsqlParameter("@id", Guid.NewGuid()));
				command.ExecuteNonQuery();

			}
		}

	}
}
