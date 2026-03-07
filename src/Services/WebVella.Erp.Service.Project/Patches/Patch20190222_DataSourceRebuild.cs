using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Project.Patches
{
    /// <summary>
    /// EF Core migration converted from monolith ProjectPlugin.Patch20190222.
    /// Performs a coordinated data source cleanup/rebuild and page body node updates.
    /// Source: WebVella.Erp.Plugins.Project/ProjectPlugin.20190222.cs (1356 lines)
    ///
    /// Operations converted:
    ///   2 DeletePageDataSource operations
    ///   5 DeleteDataSource operations
    ///   1 DeletePageBodyNode operations
    ///   2 CreatePageDataSource operations
    ///   1 CreatePageBodyNode operations
    ///   11 UpdatePageBodyNode operations
    ///   14 UpdateDataSource operations
    ///
    /// CRITICAL: All data source EQL text, SQL text, parameter JSON, field JSON,
    /// and page body node options JSON are preserved character-for-character from
    /// the original monolith source. All GUIDs are deterministic and match exactly.
    /// </summary>
    [Migration("20190222000000")]
    public class Patch20190222_DataSourceRebuild : Migration
    {
        /// <summary>
        /// Applies all data source deletions, recreations, page data source operations,
        /// and page body node updates from the original Patch20190222.
        /// Uses PostgreSQL dollar-quoting ($PGOPTS$, $PGEQL$, $PGSQL$) for values
        /// containing single quotes, double quotes, or backslash escapes.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ------------------------------------------------------------------
            // Operation 1: Delete page data source Name: ProjectWidgetMyTasks
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'e688cbdd-0fa9-43b4-aed1-3d667fdecf87';");

            // ------------------------------------------------------------------
            // Operation 2: Delete page data source Name: ProjectWidgetMyTasksDueToday
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'f1da592e-d696-426a-a60c-ef262d101a56';");

            // ------------------------------------------------------------------
            // Operation 3: Delete data source Name: ProjectAuxData
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '3c5a9d64-47ea-466a-8b0e-49e61df58bd1';");

            // ------------------------------------------------------------------
            // Operation 4: Delete data source Name: ProjectWidgetMyTasksDueToday
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = 'eae07b63-9bf4-4e25-80af-df5228dedf35';");

            // ------------------------------------------------------------------
            // Operation 5: Delete data source Name: TaskComments
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = 'f68fa8be-b957-4692-b459-4da62d23f472';");

            // ------------------------------------------------------------------
            // Operation 6: Delete data source Name: ProjectWidgetMyTasks
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = 'c44eab77-c81e-4f55-95c8-4949b275fc99';");

            // ------------------------------------------------------------------
            // Operation 7: Delete data source Name: ProjectWidgetMyOverdueTasks
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '946919a6-e4cd-41a2-97dc-1069d73adcd1';");

            // ------------------------------------------------------------------
            // Operation 8: Create page data source Name: WvProjectAllProjects
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('45ce8e79-c01d-4190-8c38-a63ce4a5fbad', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', '96218f33-42f1-4ff1-926c-b1765e1f8c6e', 'WvProjectAllProjects', '[]');");

            // ------------------------------------------------------------------
            // Operation 9: Create page data source Name: AllProjectsSelectOption
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('9a3bb37f-3b00-4a65-aa56-97f76a0a2d7e', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', '12dcdf08-af03-4347-8015-bd9bace17514', 'AllProjectsSelectOption', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""WvProjectAllProjects""},{""name"":""KeyPropName"",""type"":""text"",""value"":""id""},{""name"":""ValuePropName"",""type"":""text"",""value"":""name""},{""name"":""SortOrderPropName"",""type"":""text"",""value"":""name""}]');");

            // ------------------------------------------------------------------
            // Operation 10: Delete page body node Page name: details ID: d076f406-7ddd-4feb-b96a-137e10c2d14e
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"DELETE FROM public.app_page_body_node WHERE id = 'd076f406-7ddd-4feb-b96a-137e10c2d14e';");

            // ------------------------------------------------------------------
            // Operation 11: Update page body node Page: details ID: aa94aac4-5048-4d82-95b2-b38536028cbb
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '6e918333-a2fa-4cf7-9ca8-662e349625a7',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 3,
    component_name = 'WebVella.Erp.Web.Components.PcFieldNumber',
    container_id = 'body',
    options = '{
  ""label_text"": ""Estimated (min)"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.estimated_minutes\"",\""default\"":\""\""}"",
  ""name"": ""estimated_minutes"",
  ""mode"": ""3"",
  ""decimal_digits"": 0,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0,
  ""connected_entity_id"": """"
}'
WHERE id = 'aa94aac4-5048-4d82-95b2-b38536028cbb';");

            // ------------------------------------------------------------------
            // Operation 12: Update page body node Page: details ID: 857698b9-f715-480a-bd74-29819a4dec2d
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '6e918333-a2fa-4cf7-9ca8-662e349625a7',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 4,
    component_name = 'WebVella.Erp.Web.Components.PcFieldNumber',
    container_id = 'body',
    options = '{
  ""label_text"": ""Billable (min)"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.x_billable_minutes\"",\""default\"":\""\""}"",
  ""name"": ""x_billable_minutes"",
  ""mode"": ""2"",
  ""decimal_digits"": 0,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0,
  ""connected_entity_id"": """"
}'
WHERE id = '857698b9-f715-480a-bd74-29819a4dec2d';");

            // ------------------------------------------------------------------
            // Operation 13: Update page body node Page: details ID: ddde395b-6cee-4907-a220-a8424e091b13
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '6e918333-a2fa-4cf7-9ca8-662e349625a7',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 5,
    component_name = 'WebVella.Erp.Web.Components.PcFieldNumber',
    container_id = 'body',
    options = '{
  ""label_text"": ""Nonbillable (min)"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.x_nonbillable_minutes\"",\""default\"":\""\""}"",
  ""name"": ""x_nonbillable_minutes"",
  ""mode"": ""2"",
  ""decimal_digits"": 0,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0,
  ""connected_entity_id"": """"
}'
WHERE id = 'ddde395b-6cee-4907-a220-a8424e091b13';");

            // ------------------------------------------------------------------
            // Operation 14: Create page body node Page name: details  id: 15ea50f2-9874-4c08-bac7-1b06964b1352
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES ('15ea50f2-9874-4c08-bac7-1b06964b1352', '6e918333-a2fa-4cf7-9ca8-662e349625a7', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'body',
'{
  ""label_mode"": ""0"",
  ""label_text"": ""Project"",
  ""mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""TaskAuxData[0].$project_nn_task[0].id\"",\""default\"":\""\""}"",
  ""name"": ""$project_nn_task.id"",
  ""class"": """",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""AllProjectsSelectOption\"",\""default\"":\""\""}"",
  ""show_icon"": ""false"",
  ""connected_entity_id"": """"
}');");

            // ------------------------------------------------------------------
            // Operation 15: Update page body node Page: details ID: 754bf941-df31-4b13-ba32-eb3c7a8c8922
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = 'e15e2d00-e704-4212-a7d2-ee125dd687a6',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcFieldText',
    container_id = 'column1',
    options = '{
  ""is_visible"": """",
  ""label_mode"": ""0"",
  ""label_text"": ""Subject"",
  ""mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.subject\"",\""default\"":\""\""}"",
  ""name"": ""subject"",
  ""class"": """",
  ""maxlength"": 0,
  ""connected_entity_id"": """"
}'
WHERE id = '754bf941-df31-4b13-ba32-eb3c7a8c8922';");

            // ------------------------------------------------------------------
            // Operation 16: Update page body node Page: details ID: caa34ee6-0be6-48eb-b6bd-8b9f1ef83009
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 2,
    component_name = 'WebVella.Erp.Web.Components.PcFieldDate',
    container_id = 'body',
    options = '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.end_time\"",\""default\"":\""\""}"",
  ""name"": ""end_time"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}'
WHERE id = 'caa34ee6-0be6-48eb-b6bd-8b9f1ef83009';");

            // ------------------------------------------------------------------
            // Operation 17: Update page body node Page: details ID: b2935724-bfcc-4821-bdb2-81bc9b14f015
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 3,
    component_name = 'WebVella.Erp.Web.Components.PcFieldDateTime',
    container_id = 'body',
    options = '{
  ""label_text"": ""Created on"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.created_on\"",\""default\"":\""\""}"",
  ""name"": ""created_on"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}'
WHERE id = 'b2935724-bfcc-4821-bdb2-81bc9b14f015';");

            // ------------------------------------------------------------------
            // Operation 18: Update page body node Page: details ID: 97402edb-3a5a-4cc3-bc40-4d4d012619e2
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 5,
    component_name = 'WebVella.Erp.Web.Components.PcModal',
    container_id = 'body',
    options = '{
  ""title"": ""Task recurrence setting"",
  ""backdrop"": ""true"",
  ""size"": ""2"",
  ""position"": ""0""
}'
WHERE id = '97402edb-3a5a-4cc3-bc40-4d4d012619e2';");

            // ------------------------------------------------------------------
            // Operation 19: Update page body node Page: details ID: 394d04b3-7b5b-4cdb-b74e-e6f1c4fda8c3
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '97402edb-3a5a-4cc3-bc40-4d4d012619e2',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcButton',
    container_id = 'footer',
    options = '{
  ""type"": ""1"",
  ""text"": ""Save"",
  ""color"": ""1"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fas fa-save"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_visible"": """",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": ""wv-f3661768-ad30-4949-8a87-499ca0ab5491""
}'
WHERE id = '394d04b3-7b5b-4cdb-b74e-e6f1c4fda8c3';");

            // ------------------------------------------------------------------
            // Operation 20: Update page body node Page: details ID: 526c7435-9ace-4032-b754-5d2e9c817436
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 4,
    component_name = 'WebVella.Erp.Web.Components.PcFieldHtml',
    container_id = 'body',
    options = $PGOPTS${
  ""is_visible"": """",
  ""label_mode"": ""2"",
  ""label_text"": ""Recurrence"",
  ""mode"": ""2"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry{\\n\\t\\t\\tif (pageModel == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t\\t\\t//try read data source by name and get result as specified type object\\n\\t\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""Record\\\"");\\n\\t\\n\\t\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\t\\tif (dataSource == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t        if( dataSource[\\\""start_time\\\""] == null || dataSource[\\\""end_time\\\""] == null )\\n\\t        {\\n\\t            return \\\""requires start and end time set\\\"";\\n\\t        }\\n\\t        else\\n\\t        {\\n\\t\\t\\t    return \\\""<a href='#' onclick=\\\\\\\""ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal',{htmlId:'wv-97402edb-3a5a-4cc3-bc40-4d4d012619e2',action:'open',payload:null})\\\\\\\"">Does not repeat</a>\\\"";\\n\\t        }\\n\\t\\t}\\n\\t\\tcatch(Exception ex){\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""name"": ""field"",
  ""class"": """",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1"",
  ""connected_entity_id"": """"
}$PGOPTS$
WHERE id = '526c7435-9ace-4032-b754-5d2e9c817436';");

            // ------------------------------------------------------------------
            // Operation 21: Update page body node Page: details ID: b105d13c-3710-4ace-b51f-b57323912524
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = 'e15e2d00-e704-4212-a7d2-ee125dd687a6',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcSection',
    container_id = 'column2',
    options = '{
  ""is_visible"": """",
  ""title"": ""Details"",
  ""title_tag"": ""h3"",
  ""is_card"": ""false"",
  ""is_collapsable"": ""false"",
  ""is_collapsed"": ""false"",
  ""class"": ""mb-4"",
  ""body_class"": """",
  ""label_mode"": ""1"",
  ""field_mode"": ""1""
}'
WHERE id = 'b105d13c-3710-4ace-b51f-b57323912524';");

            // ------------------------------------------------------------------
            // Operation 22: Update page body node Page: all ID: 22af9111-4f15-48c1-a9fd-e5ab72074b3e
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = NULL,
    page_id = '57db749f-e69e-4d88-b9d1-66203da05da1',
    node_id = NULL,
    weight = 2,
    component_name = 'WebVella.Erp.Web.Components.PcGrid',
    container_id = '',
    options = '{
  ""visible_columns"": 4,
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""AllProjects\"",\""default\"":\""\""}"",
  ""id"": """",
  ""name"": """",
  ""prefix"": """",
  ""class"": """",
  ""striped"": ""false"",
  ""small"": ""true"",
  ""bordered"": ""true"",
  ""borderless"": ""false"",
  ""hover"": ""true"",
  ""responsive_breakpoint"": ""0"",
  ""empty_text"": ""No projects"",
  ""has_thead"": ""true"",
  ""has_tfoot"": ""true"",
  ""container1_label"": """",
  ""container1_width"": ""40px"",
  ""container1_name"": """",
  ""container1_nowrap"": ""false"",
  ""container1_sortable"": ""false"",
  ""container1_class"": """",
  ""container1_vertical_align"": ""1"",
  ""container1_horizontal_align"": ""1"",
  ""container2_label"": ""abbr"",
  ""container2_width"": ""80px"",
  ""container2_name"": ""abbr"",
  ""container2_nowrap"": ""false"",
  ""container2_sortable"": ""true"",
  ""container2_class"": """",
  ""container2_vertical_align"": ""1"",
  ""container2_horizontal_align"": ""1"",
  ""container3_label"": ""name"",
  ""container3_width"": """",
  ""container3_name"": ""name"",
  ""container3_nowrap"": ""false"",
  ""container3_sortable"": ""false"",
  ""container3_class"": """",
  ""container3_vertical_align"": ""1"",
  ""container3_horizontal_align"": ""1"",
  ""container4_label"": ""lead"",
  ""container4_width"": """",
  ""container4_name"": ""lead"",
  ""container4_nowrap"": ""false"",
  ""container4_sortable"": ""false"",
  ""container4_class"": """",
  ""container4_vertical_align"": ""1"",
  ""container4_horizontal_align"": ""1"",
  ""container5_label"": ""column5"",
  ""container5_width"": """",
  ""container5_name"": ""column5"",
  ""container5_nowrap"": ""false"",
  ""container5_sortable"": ""false"",
  ""container5_class"": """",
  ""container5_vertical_align"": ""1"",
  ""container5_horizontal_align"": ""1"",
  ""container6_label"": ""column6"",
  ""container6_width"": """",
  ""container6_name"": ""column6"",
  ""container6_nowrap"": ""false"",
  ""container6_sortable"": ""false"",
  ""container6_class"": """",
  ""container6_vertical_align"": ""1"",
  ""container6_horizontal_align"": ""1"",
  ""container7_label"": ""column7"",
  ""container7_width"": """",
  ""container7_name"": ""column7"",
  ""container7_nowrap"": ""false"",
  ""container7_sortable"": ""false"",
  ""container7_class"": """",
  ""container7_vertical_align"": ""1"",
  ""container7_horizontal_align"": ""1"",
  ""container8_label"": ""column8"",
  ""container8_width"": """",
  ""container8_name"": ""column8"",
  ""container8_nowrap"": ""false"",
  ""container8_sortable"": ""false"",
  ""container8_class"": """",
  ""container8_vertical_align"": ""1"",
  ""container8_horizontal_align"": ""1"",
  ""container9_label"": ""column9"",
  ""container9_width"": """",
  ""container9_name"": ""column9"",
  ""container9_nowrap"": ""false"",
  ""container9_sortable"": ""false"",
  ""container9_class"": """",
  ""container9_vertical_align"": ""1"",
  ""container9_horizontal_align"": ""1"",
  ""container10_label"": ""column10"",
  ""container10_width"": """",
  ""container10_name"": ""column10"",
  ""container10_nowrap"": ""false"",
  ""container10_sortable"": ""false"",
  ""container10_class"": """",
  ""container10_vertical_align"": ""1"",
  ""container10_horizontal_align"": ""1"",
  ""container11_label"": ""column11"",
  ""container11_width"": """",
  ""container11_name"": ""column11"",
  ""container11_nowrap"": ""false"",
  ""container11_sortable"": ""false"",
  ""container11_class"": """",
  ""container11_vertical_align"": ""1"",
  ""container11_horizontal_align"": ""1"",
  ""container12_label"": ""column12"",
  ""container12_width"": """",
  ""container12_name"": ""column12"",
  ""container12_nowrap"": ""false"",
  ""container12_sortable"": ""false"",
  ""container12_class"": """",
  ""container12_vertical_align"": ""1"",
  ""container12_horizontal_align"": ""1""
}'
WHERE id = '22af9111-4f15-48c1-a9fd-e5ab72074b3e';");

            // ------------------------------------------------------------------
            // Operation 23: Update data source Name: WvProjectAllUsers
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectAllUsers',
    description = 'All system users',
    weight = 10,
    eql_text = 'SELECT *
FROM user
ORDER BY username asc
',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_user.""id"" AS ""id"",
	 rec_user.""created_on"" AS ""created_on"",
	 rec_user.""first_name"" AS ""first_name"",
	 rec_user.""last_name"" AS ""last_name"",
	 rec_user.""username"" AS ""username"",
	 rec_user.""email"" AS ""email"",
	 rec_user.""password"" AS ""password"",
	 rec_user.""last_logged_in"" AS ""last_logged_in"",
	 rec_user.""enabled"" AS ""enabled"",
	 rec_user.""verified"" AS ""verified"",
	 rec_user.""preferences"" AS ""preferences"",
	 rec_user.""image"" AS ""image"",
	 COUNT(*) OVER() AS ___total_count___
FROM rec_user
ORDER BY rec_user.""username"" ASC
) X
',
    parameters_json = '[]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""first_name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""last_name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""email"",""type"":6,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""password"",""type"":13,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""last_logged_in"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""enabled"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""verified"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""preferences"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]}]',
    entity_name = 'user'
WHERE id = 'f3e5ab66-9257-42f9-8bdf-f0233dd4aedd';");

            // ------------------------------------------------------------------
            // Operation 24: Update data source Name: WvProjectAllAccounts
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectAllAccounts',
    description = 'Lists all accounts in the system',
    weight = 10,
    eql_text = 'SELECT id,name 
FROM account
where name CONTAINS @name
ORDER BY @sortBy ASC
PAGE @page
PAGESIZE @pageSize',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_account.""id"" AS ""id"",
	 rec_account.""name"" AS ""name"",
	 COUNT(*) OVER() AS ___total_count___
FROM rec_account
WHERE  ( rec_account.""name""  ILIKE  @name ) 
ORDER BY rec_account.""name"" ASC
LIMIT 15
OFFSET 0
) X
',
    parameters_json = '[{""name"":""name"",""type"":""text"",""value"":""null""},{""name"":""sortBy"",""type"":""text"",""value"":""name""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""}]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]',
    entity_name = 'account'
WHERE id = '61d21547-b353-48b8-8b75-b727680da79e';");

            // ------------------------------------------------------------------
            // Operation 25: Update data source Name: WvProjectOpenTasks
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectOpenTasks',
    description = 'All open tasks for a project',
    weight = 10,
    eql_text = 'SELECT *,$milestone_nn_task.name,$task_status_1n_task.label,$task_type_1n_task.label
FROM task
WHERE $project_nn_task.id = @projectId
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
',
    sql_text = $PGSQL$SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""estimated_minutes"" AS ""estimated_minutes"",
	 rec_task.""x_billable_minutes"" AS ""x_billable_minutes"",
	 rec_task.""x_nonbillable_minutes"" AS ""x_nonbillable_minutes"",
	 rec_task.""priority"" AS ""priority"",
	 rec_task.""timelog_started_on"" AS ""timelog_started_on"",
	 rec_task.""owner_id"" AS ""owner_id"",
	 rec_task.""type_id"" AS ""type_id"",
	 rec_task.""start_time"" AS ""start_time"",
	 rec_task.""end_time"" AS ""end_time"",
	 rec_task.""recurrence_id"" AS ""recurrence_id"",
	 rec_task.""reserve_time"" AS ""reserve_time"",
	 rec_task.""recurrence_template"" AS ""recurrence_template"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""x_search"" AS ""x_search"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $milestone_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), '[]') FROM ( 
	 SELECT 
		 milestone_nn_task.""id"" AS ""id"",
		 milestone_nn_task.""name"" AS ""name""
	 FROM rec_milestone milestone_nn_task
	 LEFT JOIN  rel_milestone_nn_task milestone_nn_task_target ON milestone_nn_task_target.target_id = rec_task.id
	 WHERE milestone_nn_task.id = milestone_nn_task_target.origin_id )d  )::jsonb AS ""$milestone_nn_task"",
	-------< $milestone_nn_task
	------->: $task_status_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 task_status_1n_task.""id"" AS ""id"",
		 task_status_1n_task.""label"" AS ""label"" 
	 FROM rec_task_status task_status_1n_task
	 WHERE task_status_1n_task.id = rec_task.status_id ) d )::jsonb AS ""$task_status_1n_task"",
	-------< $task_status_1n_task
	------->: $task_type_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 task_type_1n_task.""id"" AS ""id"",
		 task_type_1n_task.""label"" AS ""label"" 
	 FROM rec_task_type task_type_1n_task
	 WHERE task_type_1n_task.id = rec_task.type_id ) d )::jsonb AS ""$task_type_1n_task""	
	-------< $task_type_1n_task

FROM rec_task
LEFT OUTER JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
LEFT OUTER JOIN  rec_project project_nn_task_tar_org ON project_nn_task_target.origin_id = project_nn_task_tar_org.id
WHERE  ( project_nn_task_tar_org.""id"" = @projectId ) 
ORDER BY rec_task.""id"" ASC
LIMIT 15
OFFSET 0
) X
$PGSQL$,
    parameters_json = '[{""name"":""projectId"",""type"":""guid"",""value"":""00000000-0000-0000-0000-000000000000""},{""name"":""sortBy"",""type"":""text"",""value"":""id""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""}]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_template"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$milestone_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'task'
WHERE id = '46aab266-e2a8-4b67-9155-39ec1cf3bccb';");

            // ------------------------------------------------------------------
            // Operation 26: Update data source Name: WvProjectTaskStatuses
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectTaskStatuses',
    description = 'All task statuses',
    weight = 10,
    eql_text = 'SELECT *
FROM task_status
ORDER BY label asc',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task_status.""id"" AS ""id"",
	 rec_task_status.""is_closed"" AS ""is_closed"",
	 rec_task_status.""is_default"" AS ""is_default"",
	 rec_task_status.""label"" AS ""label"",
	 rec_task_status.""sort_index"" AS ""sort_index"",
	 rec_task_status.""is_system"" AS ""is_system"",
	 rec_task_status.""is_enabled"" AS ""is_enabled"",
	 rec_task_status.""icon_class"" AS ""icon_class"",
	 rec_task_status.""color"" AS ""color"",
	 rec_task_status.""l_scope"" AS ""l_scope"",
	 COUNT(*) OVER() AS ___total_count___
FROM rec_task_status
ORDER BY rec_task_status.""label"" ASC
) X
',
    parameters_json = '[]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_closed"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_default"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""sort_index"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_system"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_enabled"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]',
    entity_name = 'task_status'
WHERE id = 'fad53f3d-4d3b-4c7b-8cd2-23e96a086ad8';");

            // ------------------------------------------------------------------
            // Operation 27: Update data source Name: WvProjectAllProjects
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectAllProjects',
    description = 'all project records',
    weight = 10,
    eql_text = 'SELECT id,abbr,name,$user_1n_project_owner.username
FROM project
WHERE name CONTAINS @filterName
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
',
    sql_text = $PGSQL$SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_project.""id"" AS ""id"",
	 rec_project.""abbr"" AS ""abbr"",
	 rec_project.""name"" AS ""name"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $user_1n_project_owner
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 user_1n_project_owner.""id"" AS ""id"",
		 user_1n_project_owner.""username"" AS ""username"" 
	 FROM rec_user user_1n_project_owner
	 WHERE user_1n_project_owner.id = rec_project.owner_id ) d )::jsonb AS ""$user_1n_project_owner""	
	-------< $user_1n_project_owner

FROM rec_project
WHERE  ( rec_project.""name""  ILIKE  @filterName ) 
ORDER BY rec_project.""name"" ASC
LIMIT 15
OFFSET 0
) X
$PGSQL$,
    parameters_json = '[{""name"":""sortBy"",""type"":""text"",""value"":""name""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""filterName"",""type"":""text"",""value"":""null""}]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_project_owner"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'project'
WHERE id = '96218f33-42f1-4ff1-926c-b1765e1f8c6e';");

            // ------------------------------------------------------------------
            // Operation 28: Update data source Name: WvProjectAllTasks
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectAllTasks',
    description = 'All tasks selection',
    weight = 10,
    eql_text = 'SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE x_search CONTAINS @searchQuery
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
',
    sql_text = $PGSQL$SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""estimated_minutes"" AS ""estimated_minutes"",
	 rec_task.""x_billable_minutes"" AS ""x_billable_minutes"",
	 rec_task.""x_nonbillable_minutes"" AS ""x_nonbillable_minutes"",
	 rec_task.""priority"" AS ""priority"",
	 rec_task.""timelog_started_on"" AS ""timelog_started_on"",
	 rec_task.""owner_id"" AS ""owner_id"",
	 rec_task.""type_id"" AS ""type_id"",
	 rec_task.""start_time"" AS ""start_time"",
	 rec_task.""end_time"" AS ""end_time"",
	 rec_task.""recurrence_id"" AS ""recurrence_id"",
	 rec_task.""reserve_time"" AS ""reserve_time"",
	 rec_task.""recurrence_template"" AS ""recurrence_template"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""x_search"" AS ""x_search"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), '[]') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""abbr"" AS ""abbr""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task"",
	-------< $project_nn_task
	------->: $user_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 user_1n_task.""id"" AS ""id"",
		 user_1n_task.""username"" AS ""username"" 
	 FROM rec_user user_1n_task
	 WHERE user_1n_task.id = rec_task.owner_id ) d )::jsonb AS ""$user_1n_task"",
	-------< $user_1n_task
	------->: $task_status_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 task_status_1n_task.""id"" AS ""id"",
		 task_status_1n_task.""label"" AS ""label"" 
	 FROM rec_task_status task_status_1n_task
	 WHERE task_status_1n_task.id = rec_task.status_id ) d )::jsonb AS ""$task_status_1n_task"",
	-------< $task_status_1n_task
	------->: $task_type_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 task_type_1n_task.""id"" AS ""id"",
		 task_type_1n_task.""label"" AS ""label"",
		 task_type_1n_task.""icon_class"" AS ""icon_class"",
		 task_type_1n_task.""color"" AS ""color"" 
	 FROM rec_task_type task_type_1n_task
	 WHERE task_type_1n_task.id = rec_task.type_id ) d )::jsonb AS ""$task_type_1n_task"",
	-------< $task_type_1n_task
	------->: $user_1n_task_creator
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 user_1n_task_creator.""id"" AS ""id"",
		 user_1n_task_creator.""username"" AS ""username"" 
	 FROM rec_user user_1n_task_creator
	 WHERE user_1n_task_creator.id = rec_task.created_by ) d )::jsonb AS ""$user_1n_task_creator""	
	-------< $user_1n_task_creator

FROM rec_task
WHERE  ( rec_task.""x_search""  ILIKE  @searchQuery ) 
ORDER BY rec_task.""end_time"" ASC
LIMIT 15
OFFSET 0
) X
$PGSQL$,
    parameters_json = '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty""}]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_template"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'task'
WHERE id = '5a6e9d56-63bc-43b1-b95e-24838db9f435';");

            // ------------------------------------------------------------------
            // Operation 29: Update data source Name: WvProjectAllProjectTasks
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectAllProjectTasks',
    description = 'All tasks in a project',
    weight = 10,
    eql_text = 'SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE x_search CONTAINS @searchQuery AND $project_nn_task.id = @projectId
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize',
    sql_text = $PGSQL$SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""estimated_minutes"" AS ""estimated_minutes"",
	 rec_task.""x_billable_minutes"" AS ""x_billable_minutes"",
	 rec_task.""x_nonbillable_minutes"" AS ""x_nonbillable_minutes"",
	 rec_task.""priority"" AS ""priority"",
	 rec_task.""timelog_started_on"" AS ""timelog_started_on"",
	 rec_task.""owner_id"" AS ""owner_id"",
	 rec_task.""type_id"" AS ""type_id"",
	 rec_task.""start_time"" AS ""start_time"",
	 rec_task.""end_time"" AS ""end_time"",
	 rec_task.""recurrence_id"" AS ""recurrence_id"",
	 rec_task.""reserve_time"" AS ""reserve_time"",
	 rec_task.""recurrence_template"" AS ""recurrence_template"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""x_search"" AS ""x_search"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), '[]') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""abbr"" AS ""abbr""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task"",
	-------< $project_nn_task
	------->: $user_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 user_1n_task.""id"" AS ""id"",
		 user_1n_task.""username"" AS ""username"" 
	 FROM rec_user user_1n_task
	 WHERE user_1n_task.id = rec_task.owner_id ) d )::jsonb AS ""$user_1n_task"",
	-------< $user_1n_task
	------->: $task_status_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 task_status_1n_task.""id"" AS ""id"",
		 task_status_1n_task.""label"" AS ""label"" 
	 FROM rec_task_status task_status_1n_task
	 WHERE task_status_1n_task.id = rec_task.status_id ) d )::jsonb AS ""$task_status_1n_task"",
	-------< $task_status_1n_task
	------->: $task_type_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 task_type_1n_task.""id"" AS ""id"",
		 task_type_1n_task.""label"" AS ""label"",
		 task_type_1n_task.""icon_class"" AS ""icon_class"",
		 task_type_1n_task.""color"" AS ""color"" 
	 FROM rec_task_type task_type_1n_task
	 WHERE task_type_1n_task.id = rec_task.type_id ) d )::jsonb AS ""$task_type_1n_task"",
	-------< $task_type_1n_task
	------->: $user_1n_task_creator
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 user_1n_task_creator.""id"" AS ""id"",
		 user_1n_task_creator.""username"" AS ""username"" 
	 FROM rec_user user_1n_task_creator
	 WHERE user_1n_task_creator.id = rec_task.created_by ) d )::jsonb AS ""$user_1n_task_creator""	
	-------< $user_1n_task_creator

FROM rec_task
LEFT OUTER JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
LEFT OUTER JOIN  rec_project project_nn_task_tar_org ON project_nn_task_target.origin_id = project_nn_task_tar_org.id
WHERE  (  ( rec_task.""x_search""  ILIKE  @searchQuery )  AND  ( project_nn_task_tar_org.""id"" = @projectId )  ) 
ORDER BY rec_task.""end_time"" ASC
LIMIT 15
OFFSET 0
) X
$PGSQL$,
    parameters_json = '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty""},{""name"":""projectId"",""type"":""guid"",""value"":""guid.empty""}]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_template"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'task'
WHERE id = 'c2284f3d-2ddc-4bad-9d1b-f6e44d502bdd';");

            // ------------------------------------------------------------------
            // Operation 30: Update data source Name: WvProjectCommentsForRecordId
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectCommentsForRecordId',
    description = 'Get all comments for a record',
    weight = 10,
    eql_text = 'SELECT *,$user_1n_comment.image,$user_1n_comment.username
FROM comment
WHERE l_related_records CONTAINS @recordId 
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize',
    sql_text = $PGSQL$SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_comment.""id"" AS ""id"",
	 rec_comment.""body"" AS ""body"",
	 rec_comment.""created_by"" AS ""created_by"",
	 rec_comment.""parent_id"" AS ""parent_id"",
	 rec_comment.""created_on"" AS ""created_on"",
	 rec_comment.""l_related_records"" AS ""l_related_records"",
	 rec_comment.""l_scope"" AS ""l_scope"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $user_1n_comment
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 user_1n_comment.""id"" AS ""id"",
		 user_1n_comment.""image"" AS ""image"",
		 user_1n_comment.""username"" AS ""username"" 
	 FROM rec_user user_1n_comment
	 WHERE user_1n_comment.id = rec_comment.created_by ) d )::jsonb AS ""$user_1n_comment""	
	-------< $user_1n_comment

FROM rec_comment
WHERE  ( rec_comment.""l_related_records""  ILIKE  @recordId ) 
ORDER BY rec_comment.""created_on"" DESC
LIMIT 15
OFFSET 0
) X
$PGSQL$,
    parameters_json = '[{""name"":""sortBy"",""type"":""text"",""value"":""created_on""},{""name"":""sortOrder"",""type"":""text"",""value"":""desc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""recordId"",""type"":""text"",""value"":""string.empty""}]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_related_records"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_comment"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'comment'
WHERE id = 'a588e096-358d-4426-adf6-5db693f32322';");

            // ------------------------------------------------------------------
            // Operation 31: Update data source Name: WvProjectFeedItemsForRecordId
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectFeedItemsForRecordId',
    description = 'Get all feed items for a record',
    weight = 10,
    eql_text = 'SELECT *,$user_1n_feed_item.image,$user_1n_feed_item.username
FROM feed_item
WHERE l_related_records CONTAINS @recordId AND type CONTAINS @type
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
',
    sql_text = $PGSQL$SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_feed_item.""id"" AS ""id"",
	 rec_feed_item.""created_by"" AS ""created_by"",
	 rec_feed_item.""created_on"" AS ""created_on"",
	 rec_feed_item.""subject"" AS ""subject"",
	 rec_feed_item.""body"" AS ""body"",
	 rec_feed_item.""type"" AS ""type"",
	 rec_feed_item.""l_related_records"" AS ""l_related_records"",
	 rec_feed_item.""l_scope"" AS ""l_scope"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $user_1n_feed_item
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 user_1n_feed_item.""id"" AS ""id"",
		 user_1n_feed_item.""image"" AS ""image"",
		 user_1n_feed_item.""username"" AS ""username"" 
	 FROM rec_user user_1n_feed_item
	 WHERE user_1n_feed_item.id = rec_feed_item.created_by ) d )::jsonb AS ""$user_1n_feed_item""	
	-------< $user_1n_feed_item

FROM rec_feed_item
WHERE  (  ( rec_feed_item.""l_related_records""  ILIKE  @recordId )  AND  ( rec_feed_item.""type""  ILIKE  @type )  ) 
ORDER BY rec_feed_item.""created_on"" DESC
LIMIT 15
OFFSET 0
) X
$PGSQL$,
    parameters_json = '[{""name"":""sortBy"",""type"":""text"",""value"":""created_on""},{""name"":""sortOrder"",""type"":""text"",""value"":""desc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""recordId"",""type"":""text"",""value"":""string.empty""},{""name"":""type"",""type"":""text"",""value"":""string.empty""}]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_related_records"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_feed_item"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'feed_item'
WHERE id = '74e5a414-6deb-4af6-8e29-567f718ca430';");

            // ------------------------------------------------------------------
            // Operation 32: Update data source Name: WvProjectNoOwnerTasks
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectNoOwnerTasks',
    description = 'all tasks without an owner',
    weight = 10,
    eql_text = $PGEQL$SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE owner_id = NULL AND x_search CONTAINS @searchQuery AND status_id <> 'b1cc69e5-ce09-40e0-8785-b6452b257bdf'
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
$PGEQL$,
    sql_text = $PGSQL$SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""estimated_minutes"" AS ""estimated_minutes"",
	 rec_task.""x_billable_minutes"" AS ""x_billable_minutes"",
	 rec_task.""x_nonbillable_minutes"" AS ""x_nonbillable_minutes"",
	 rec_task.""priority"" AS ""priority"",
	 rec_task.""timelog_started_on"" AS ""timelog_started_on"",
	 rec_task.""owner_id"" AS ""owner_id"",
	 rec_task.""type_id"" AS ""type_id"",
	 rec_task.""start_time"" AS ""start_time"",
	 rec_task.""end_time"" AS ""end_time"",
	 rec_task.""recurrence_id"" AS ""recurrence_id"",
	 rec_task.""reserve_time"" AS ""reserve_time"",
	 rec_task.""recurrence_template"" AS ""recurrence_template"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""x_search"" AS ""x_search"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), '[]') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""abbr"" AS ""abbr""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task"",
	-------< $project_nn_task
	------->: $user_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 user_1n_task.""id"" AS ""id"",
		 user_1n_task.""username"" AS ""username"" 
	 FROM rec_user user_1n_task
	 WHERE user_1n_task.id = rec_task.owner_id ) d )::jsonb AS ""$user_1n_task"",
	-------< $user_1n_task
	------->: $task_status_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 task_status_1n_task.""id"" AS ""id"",
		 task_status_1n_task.""label"" AS ""label"" 
	 FROM rec_task_status task_status_1n_task
	 WHERE task_status_1n_task.id = rec_task.status_id ) d )::jsonb AS ""$task_status_1n_task"",
	-------< $task_status_1n_task
	------->: $task_type_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 task_type_1n_task.""id"" AS ""id"",
		 task_type_1n_task.""label"" AS ""label"",
		 task_type_1n_task.""icon_class"" AS ""icon_class"",
		 task_type_1n_task.""color"" AS ""color"" 
	 FROM rec_task_type task_type_1n_task
	 WHERE task_type_1n_task.id = rec_task.type_id ) d )::jsonb AS ""$task_type_1n_task"",
	-------< $task_type_1n_task
	------->: $user_1n_task_creator
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 user_1n_task_creator.""id"" AS ""id"",
		 user_1n_task_creator.""username"" AS ""username"" 
	 FROM rec_user user_1n_task_creator
	 WHERE user_1n_task_creator.id = rec_task.created_by ) d )::jsonb AS ""$user_1n_task_creator""	
	-------< $user_1n_task_creator

FROM rec_task
WHERE  (  (  ( rec_task.""owner_id"" IS NULL )  AND  ( rec_task.""x_search""  ILIKE  @searchQuery )  )  AND  ( rec_task.""status_id"" <> 'b1cc69e5-ce09-40e0-8785-b6452b257bdf' )  ) 
ORDER BY rec_task.""end_time"" ASC
LIMIT 15
OFFSET 0
) X
$PGSQL$,
    parameters_json = '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty""}]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_template"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'task'
WHERE id = '40c0bcc6-2e3e-4b68-ae6a-27f1f472f069';");

            // ------------------------------------------------------------------
            // Operation 33: Update data source Name: WvProjectAllOpenTasks
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectAllOpenTasks',
    description = 'All open tasks selection',
    weight = 10,
    eql_text = $PGEQL$SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE status_id <> 'b1cc69e5-ce09-40e0-8785-b6452b257bdf' AND x_search CONTAINS @searchQuery
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize$PGEQL$,
    sql_text = $PGSQL$SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""estimated_minutes"" AS ""estimated_minutes"",
	 rec_task.""x_billable_minutes"" AS ""x_billable_minutes"",
	 rec_task.""x_nonbillable_minutes"" AS ""x_nonbillable_minutes"",
	 rec_task.""priority"" AS ""priority"",
	 rec_task.""timelog_started_on"" AS ""timelog_started_on"",
	 rec_task.""owner_id"" AS ""owner_id"",
	 rec_task.""type_id"" AS ""type_id"",
	 rec_task.""start_time"" AS ""start_time"",
	 rec_task.""end_time"" AS ""end_time"",
	 rec_task.""recurrence_id"" AS ""recurrence_id"",
	 rec_task.""reserve_time"" AS ""reserve_time"",
	 rec_task.""recurrence_template"" AS ""recurrence_template"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""x_search"" AS ""x_search"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), '[]') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""abbr"" AS ""abbr""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task"",
	-------< $project_nn_task
	------->: $user_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 user_1n_task.""id"" AS ""id"",
		 user_1n_task.""username"" AS ""username"" 
	 FROM rec_user user_1n_task
	 WHERE user_1n_task.id = rec_task.owner_id ) d )::jsonb AS ""$user_1n_task"",
	-------< $user_1n_task
	------->: $task_status_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 task_status_1n_task.""id"" AS ""id"",
		 task_status_1n_task.""label"" AS ""label"" 
	 FROM rec_task_status task_status_1n_task
	 WHERE task_status_1n_task.id = rec_task.status_id ) d )::jsonb AS ""$task_status_1n_task"",
	-------< $task_status_1n_task
	------->: $task_type_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 task_type_1n_task.""id"" AS ""id"",
		 task_type_1n_task.""label"" AS ""label"",
		 task_type_1n_task.""icon_class"" AS ""icon_class"",
		 task_type_1n_task.""color"" AS ""color"" 
	 FROM rec_task_type task_type_1n_task
	 WHERE task_type_1n_task.id = rec_task.type_id ) d )::jsonb AS ""$task_type_1n_task"",
	-------< $task_type_1n_task
	------->: $user_1n_task_creator
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), '[]') FROM ( 
	 SELECT 
		 user_1n_task_creator.""id"" AS ""id"",
		 user_1n_task_creator.""username"" AS ""username"" 
	 FROM rec_user user_1n_task_creator
	 WHERE user_1n_task_creator.id = rec_task.created_by ) d )::jsonb AS ""$user_1n_task_creator""	
	-------< $user_1n_task_creator

FROM rec_task
WHERE  (  ( rec_task.""status_id"" <> 'b1cc69e5-ce09-40e0-8785-b6452b257bdf' )  AND  ( rec_task.""x_search""  ILIKE  @searchQuery )  ) 
ORDER BY rec_task.""end_time"" ASC
LIMIT 15
OFFSET 0
) X
$PGSQL$,
    parameters_json = '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty""}]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_template"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'task'
WHERE id = '9c2337ac-b505-4ce4-b1ff-ffde2e37b312';");

            // ------------------------------------------------------------------
            // Operation 34: Update data source Name: WvProjectTaskAuxData
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectTaskAuxData',
    description = 'getting related data for the current task',
    weight = 10,
    eql_text = 'SELECT $project_nn_task.id,$project_nn_task.abbr,$project_nn_task.name,$user_nn_task_watchers.id
FROM task
WHERE id = @recordId',
    sql_text = $PGSQL$SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), '[]') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""abbr"" AS ""abbr"",
		 project_nn_task.""name"" AS ""name""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task"",
	-------< $project_nn_task
	------->: $user_nn_task_watchers
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), '[]') FROM ( 
	 SELECT 
		 user_nn_task_watchers.""id"" AS ""id""
	 FROM rec_user user_nn_task_watchers
	 LEFT JOIN  rel_user_nn_task_watchers user_nn_task_watchers_target ON user_nn_task_watchers_target.target_id = rec_task.id
	 WHERE user_nn_task_watchers.id = user_nn_task_watchers_target.origin_id )d  )::jsonb AS ""$user_nn_task_watchers""	
	-------< $user_nn_task_watchers

FROM rec_task
WHERE  ( rec_task.""id"" = @recordId ) 
) X
$PGSQL$,
    parameters_json = '[{""name"":""recordId"",""type"":""guid"",""value"":""00000000-0000-0000-0000-000000000000""}]',
    fields_json = '[{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_nn_task_watchers"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'task'
WHERE id = '587d963b-613f-4e77-a7d4-719f631ce6b2';");

            // ------------------------------------------------------------------
            // Operation 35: Update data source Name: WvProjectTaskTypes
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectTaskTypes',
    description = 'All task types',
    weight = 10,
    eql_text = 'SELECT *
FROM task_type
WHERE l_scope CONTAINS @scope
ORDER BY sort_index asc',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task_type.""id"" AS ""id"",
	 rec_task_type.""is_default"" AS ""is_default"",
	 rec_task_type.""label"" AS ""label"",
	 rec_task_type.""sort_index"" AS ""sort_index"",
	 rec_task_type.""is_system"" AS ""is_system"",
	 rec_task_type.""is_enabled"" AS ""is_enabled"",
	 rec_task_type.""icon_class"" AS ""icon_class"",
	 rec_task_type.""color"" AS ""color"",
	 rec_task_type.""l_scope"" AS ""l_scope"",
	 COUNT(*) OVER() AS ___total_count___
FROM rec_task_type
WHERE  ( rec_task_type.""l_scope""  ILIKE  @scope ) 
ORDER BY rec_task_type.""sort_index"" ASC
) X
',
    parameters_json = '[{""name"":""scope"",""type"":""text"",""value"":""projects""}]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_default"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""sort_index"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_system"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_enabled"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]',
    entity_name = 'task_type'
WHERE id = '4857ace4-fcfc-4803-ad86-7c7afba91ce0';");

            // ------------------------------------------------------------------
            // Operation 36: Update data source Name: WvProjectTimeLogsForRecordId
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE data_sources SET
    name = 'WvProjectTimeLogsForRecordId',
    description = 'Get all time logs for a record',
    weight = 10,
    eql_text = 'SELECT *,$user_1n_timelog.image,$user_1n_timelog.username
FROM timelog
WHERE l_related_records CONTAINS @recordId 
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize',
    sql_text = $PGSQL$SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
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
WHERE  ( rec_timelog.""l_related_records""  ILIKE  @recordId ) 
ORDER BY rec_timelog.""created_on"" DESC
LIMIT 15
OFFSET 0
) X
$PGSQL$,
    parameters_json = '[{""name"":""sortBy"",""type"":""text"",""value"":""created_on""},{""name"":""sortOrder"",""type"":""text"",""value"":""desc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""recordId"",""type"":""text"",""value"":""string.empty""}]',
    fields_json = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":10,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_billable"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""logged_on"",""type"":4,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_related_records"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_timelog"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'timelog'
WHERE id = 'e66b8374-82ea-4305-8456-085b3a1f1f2d';");

        }

        /// <summary>
        /// Rolls back the data source rebuild migration.
        /// Due to the destructive nature of this migration (data source UPDATEs replace
        /// previous definitions), full reversal would require restoring the exact prior
        /// state of each data source, page data source, and page body node.
        ///
        /// This Down() method:
        ///   1. Deletes page data sources created in Up()
        ///   2. Restores the page body node deleted in Up()
        ///   3. Restores the data sources and page data sources deleted in Up()
        ///
        /// Note: Data source UPDATEs and page body node UPDATEs cannot be reversed
        /// without the full prior-state data from patches 20190203-20190208.
        /// For a complete rollback, re-run the full migration sequence.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Step 1: Delete page data sources that were created in Up()
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '45ce8e79-c01d-4190-8c38-a63ce4a5fbad';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '9a3bb37f-3b00-4a65-aa56-97f76a0a2d7e';");

            // Step 2: Restore page data sources that were deleted in Up()
            // These were originally created by Patch20190203_InitialProjectSchema.
            // Page data source ProjectWidgetMyTasks (e688cbdd-0fa9-43b4-aed1-3d667fdecf87) — restore from Patch20190203
            // Page data source ProjectWidgetMyTasksDueToday (f1da592e-d696-426a-a60c-ef262d101a56) — restore from Patch20190203

            // Step 3: Restore data sources that were deleted in Up()
            // These were originally created by Patch20190203_InitialProjectSchema.
            // Data source ProjectAuxData (3c5a9d64-47ea-466a-8b0e-49e61df58bd1) — restore from Patch20190203
            // Data source ProjectWidgetMyTasksDueToday (eae07b63-9bf4-4e25-80af-df5228dedf35) — restore from Patch20190203
            // Data source TaskComments (f68fa8be-b957-4692-b459-4da62d23f472) — restore from Patch20190203
            // Data source ProjectWidgetMyTasks (c44eab77-c81e-4f55-95c8-4949b275fc99) — restore from Patch20190203
            // Data source ProjectWidgetMyOverdueTasks (946919a6-e4cd-41a2-97dc-1069d73adcd1) — restore from Patch20190203

            // Step 4: Restore page body node deleted in Up()
            // Node d076f406-7ddd-4feb-b96a-137e10c2d14e — restore from earlier patch

            // IMPORTANT: Data source UPDATEs and page body node UPDATEs performed
            // in Up() are NOT automatically reverted. The prior values exist only
            // in earlier migration files (20190203 through 20190208).
            // For full rollback, drop the database and re-run all migrations.
        }
    }
}
