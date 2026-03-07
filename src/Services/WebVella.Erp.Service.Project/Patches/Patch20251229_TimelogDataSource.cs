using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Project.Patches
{
    /// <summary>
    /// EF Core migration equivalent of the monolith's ProjectPlugin.Patch20251229.
    /// Updates the WvProjectTimeLogsForRecordId data source (id: e66b8374-82ea-4305-8456-085b3a1f1f2d)
    /// to include $user_1n_timelog.image and $user_1n_timelog.username fields in the EQL query
    /// and adds a corresponding correlated subquery in the generated SQL text that joins
    /// rec_timelog to rec_user via the user_1n_timelog relation.
    ///
    /// Source: WebVella.Erp.Plugins.Project/ProjectPlugin.20251229.cs (Patch20251229 method)
    /// Original call: new DbDataSourceRepository().Update(id, name, description, weight, eqlText, sqlText, parametersJson, fieldsJson, entityName, returnTotal)
    /// Converted to: migrationBuilder.Sql() with raw UPDATE on public.data_source table
    ///
    /// Migration tracking: Replaces monolith's PluginSettings.Version date-based versioning
    /// with EF Core's __EFMigrationsHistory table for migration ordering and discovery.
    /// </summary>
    [Migration("20251229000000")]
    public class Patch20251229_TimelogDataSource : Migration
    {
        /// <summary>
        /// Data source identifier for WvProjectTimeLogsForRecordId.
        /// Preserved verbatim from the monolith source: new Guid("e66b8374-82ea-4305-8456-085b3a1f1f2d").
        /// </summary>
        private static readonly Guid DataSourceId = new Guid("e66b8374-82ea-4305-8456-085b3a1f1f2d");

        /// <summary>
        /// Applies the data source update: adds $user_1n_timelog.image and
        /// $user_1n_timelog.username to the WvProjectTimeLogsForRecordId data source.
        /// All field definitions, parameter definitions, EQL text, and SQL text are
        /// preserved verbatim from the monolith source.
        ///
        /// This migration is idempotent — running it multiple times produces the same result
        /// because it uses an unconditional UPDATE keyed on the data source's unique identifier.
        ///
        /// Equivalent to the original monolith call:
        ///   new WebVella.Erp.Database.DbDataSourceRepository().Update(
        ///       id, name, description, weight, eqlText, sqlText,
        ///       parametersJson, fieldsJson, entityName, returnTotal);
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Update the WvProjectTimeLogsForRecordId data source record in public.data_source.
            // PostgreSQL dollar-quoting ($tag$...$tag$) is used for string values containing
            // double quotes (SQL identifiers) and special characters (JSON) to avoid escaping issues.
            //
            // Preserved from source:
            //   id:          e66b8374-82ea-4305-8456-085b3a1f1f2d
            //   name:        WvProjectTimeLogsForRecordId
            //   description: Get all time logs for a record
            //   weight:      10
            //   entityName:  timelog
            //   returnTotal: true
            //   EQL:         SELECT with $user_1n_timelog.image and $user_1n_timelog.username
            //   SQL:         Correlated subquery joining rec_timelog → rec_user via user_1n_timelog
            //   Parameters:  sortBy, sortOrder, page, pageSize, recordId
            //   Fields:      id, body, created_by, created_on, is_billable, logged_on, minutes,
            //                l_scope, l_related_records, and nested $user_1n_timelog (id, image, username)
            migrationBuilder.Sql(@"
UPDATE public.data_source SET
    name = 'WvProjectTimeLogsForRecordId',
    description = 'Get all time logs for a record',
    weight = 10,
    eql_text = $eql$SELECT *,$user_1n_timelog.image,$user_1n_timelog.username
FROM timelog
WHERE l_related_records CONTAINS @recordId 
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize$eql$,
    sql_text = $sqltext$SELECT row_to_json( X ) FROM (
SELECT 
	 rec_timelog.""id"" AS ""id"",
	 rec_timelog.""body"" AS ""body"",
	 rec_timelog.""created_by"" AS ""created_by"",
	 rec_timelog.""created_on"" AS ""created_on"",
	 rec_timelog.""is_billable"" AS ""is_billable"",
	 rec_timelog.""logged_on"" AS ""logged_on"",
	 rec_timelog.""minutes"" AS ""minutes"",
	 rec_timelog.""l_scope"" AS ""l_scope"",
	 rec_timelog.""l_related_records"" AS ""l_related_records"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $user_1n_timelog
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM (
	 SELECT 
		 user_1n_timelog.""id"" AS ""id"",
		 user_1n_timelog.""image"" AS ""image"",
		 user_1n_timelog.""username"" AS ""username""
	 FROM rec_user user_1n_timelog
	 WHERE user_1n_timelog.id = rec_timelog.created_by ) d )::jsonb AS ""$user_1n_timelog""	
	-------< $user_1n_timelog

FROM rec_timelog
WHERE  ( rec_timelog.""l_related_records""  ILIKE  CONCAT ( '%' , @recordId , '%' ) )
ORDER BY rec_timelog.""created_on"" DESC
LIMIT 1000
OFFSET 0
) X
$sqltext$,
    parameters_json = $params$[{""name"":""sortBy"",""type"":""text"",""value"":""created_on"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""desc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""1000"",""ignore_parse_errors"":false},{""name"":""recordId"",""type"":""text"",""value"":""string.empty"",""ignore_parse_errors"":false}]$params$,
    fields_json = $flds$[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":10,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_billable"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""logged_on"",""type"":4,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_related_records"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_timelog"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]$flds$,
    entity_name = 'timelog',
    return_total = true
WHERE id = 'e66b8374-82ea-4305-8456-085b3a1f1f2d';
");
        }

        /// <summary>
        /// Reverts the data source to its pre-patch state, removing the $user_1n_timelog
        /// relation fields (image and username) from the EQL, SQL, and field definitions.
        /// The rollback restores the basic timelog query without user relation joins.
        ///
        /// Rollback strategy:
        /// - EQL: Reverts SELECT to "SELECT *" without $user_1n_timelog relation fields
        /// - SQL: Removes the correlated subquery joining rec_timelog to rec_user
        /// - Fields: Removes the $user_1n_timelog nested field definition
        /// - Parameters: Unchanged (same parameter set applies to both versions)
        /// - Other columns (name, description, weight, entity_name, return_total): Unchanged
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert the WvProjectTimeLogsForRecordId data source to its state before
            // the $user_1n_timelog fields were added. This removes the user image and
            // username correlated subquery and the nested field definition.
            migrationBuilder.Sql(@"
UPDATE public.data_source SET
    name = 'WvProjectTimeLogsForRecordId',
    description = 'Get all time logs for a record',
    weight = 10,
    eql_text = $eql$SELECT *
FROM timelog
WHERE l_related_records CONTAINS @recordId 
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize$eql$,
    sql_text = $sqltext$SELECT row_to_json( X ) FROM (
SELECT 
	 rec_timelog.""id"" AS ""id"",
	 rec_timelog.""body"" AS ""body"",
	 rec_timelog.""created_by"" AS ""created_by"",
	 rec_timelog.""created_on"" AS ""created_on"",
	 rec_timelog.""is_billable"" AS ""is_billable"",
	 rec_timelog.""logged_on"" AS ""logged_on"",
	 rec_timelog.""minutes"" AS ""minutes"",
	 rec_timelog.""l_scope"" AS ""l_scope"",
	 rec_timelog.""l_related_records"" AS ""l_related_records"",
	 COUNT(*) OVER() AS ___total_count___
FROM rec_timelog
WHERE  ( rec_timelog.""l_related_records""  ILIKE  CONCAT ( '%' , @recordId , '%' ) )
ORDER BY rec_timelog.""created_on"" DESC
LIMIT 1000
OFFSET 0
) X
$sqltext$,
    parameters_json = $params$[{""name"":""sortBy"",""type"":""text"",""value"":""created_on"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""desc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""1000"",""ignore_parse_errors"":false},{""name"":""recordId"",""type"":""text"",""value"":""string.empty"",""ignore_parse_errors"":false}]$params$,
    fields_json = $flds$[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":10,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_billable"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""logged_on"",""type"":4,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_related_records"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]$flds$,
    entity_name = 'timelog',
    return_total = true
WHERE id = 'e66b8374-82ea-4305-8456-085b3a1f1f2d';
");
        }
    }
}
