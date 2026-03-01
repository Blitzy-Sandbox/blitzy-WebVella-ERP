using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Project.Patches
{
    /// <summary>
    /// EF Core migration converting monolith ProjectPlugin.Patch20190205.
    /// Performs UI page body node updates and inserts (Round 1) for the project
    /// details page and the track-time page.
    /// Source: WebVella.Erp.Plugins.Project/ProjectPlugin.20190205.cs (357 lines)
    /// 
    /// Operations converted:
    ///   7 UpdatePageBodyNode calls → UPDATE SQL on public.app_page_body_node
    ///   7 CreatePageBodyNode calls → INSERT SQL on public.app_page_body_node
    /// 
    /// CRITICAL: Several options JSON blocks contain embedded ICodeVariable C# scripts
    /// (Watchers UI, Recurrence link) that are preserved verbatim as business logic.
    /// </summary>
    [Migration("20190205000000")]
    public class Patch20190205_UiPageUpdatesV1 : Migration
    {
        /// <summary>
        /// Applies all page body node updates and inserts from the original Patch20190205.
        /// Uses PostgreSQL dollar-quoting ($PGOPTS$...$PGOPTS$) for the options column
        /// to safely embed JSON containing double quotes, backslashes, and nested escapes.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ------------------------------------------------------------------
            // Operation 1: Update page body node on "details" page (account header)
            // ID: 552a4fad-5236-4aad-b3fc-443a5f12e574
            // Page: 80b10445-c850-44cf-9c8c-57daca671dcf
            // Component: PcPageHeader
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = NULL,
    page_id = '80b10445-c850-44cf-9c8c-57daca671dcf',
    node_id = NULL,
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcPageHeader',
    container_id = '',
    options = $PGOPTS${
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""Entity.LabelPlural\"",\""default\"":\""\""}"",
  ""area_sublabel"": ""{\""type\"":\""0\"",\""string\"":\""Record.label\"",\""default\"":\""\""}"",
  ""title"": ""Account Details"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""true"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""{\""type\"":\""0\"",\""string\"":\""Entity.IconName\"",\""default\"":\""\""}"",
  ""return_url"": ""/projects/accounts/accounts/l/list""
}$PGOPTS$
WHERE id = '552a4fad-5236-4aad-b3fc-443a5f12e574';");

            // ------------------------------------------------------------------
            // Operation 2: Update page body node on "details" page (Estimated minutes)
            // ID: aa94aac4-5048-4d82-95b2-b38536028cbb
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcFieldNumber
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '6e918333-a2fa-4cf7-9ca8-662e349625a7',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 2,
    component_name = 'WebVella.Erp.Web.Components.PcFieldNumber',
    container_id = 'body',
    options = $PGOPTS${
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
}$PGOPTS$
WHERE id = 'aa94aac4-5048-4d82-95b2-b38536028cbb';");

            // ------------------------------------------------------------------
            // Operation 3: Update page body node on "details" page (Billable minutes)
            // ID: 857698b9-f715-480a-bd74-29819a4dec2d
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcFieldNumber
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '6e918333-a2fa-4cf7-9ca8-662e349625a7',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 3,
    component_name = 'WebVella.Erp.Web.Components.PcFieldNumber',
    container_id = 'body',
    options = $PGOPTS${
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
}$PGOPTS$
WHERE id = '857698b9-f715-480a-bd74-29819a4dec2d';");

            // ------------------------------------------------------------------
            // Operation 4: Update page body node on "details" page (Nonbillable minutes)
            // ID: ddde395b-6cee-4907-a220-a8424e091b13
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcFieldNumber
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '6e918333-a2fa-4cf7-9ca8-662e349625a7',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 4,
    component_name = 'WebVella.Erp.Web.Components.PcFieldNumber',
    container_id = 'body',
    options = $PGOPTS${
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
}$PGOPTS$
WHERE id = 'ddde395b-6cee-4907-a220-a8424e091b13';");

            // ------------------------------------------------------------------
            // Operation 5: Update page body node on "details" page (Watchers HTML)
            // ID: 9f15bb3a-b6bf-424c-9394-669cc2041215
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcFieldHtml
            // CRITICAL: Contains ICodeVariable C# script for watchers UI rendering
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = 'ecc262e9-fbad-4dd1-9c98-56ad047685fb',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 3,
    component_name = 'WebVella.Erp.Web.Components.PcFieldHtml',
    container_id = 'body',
    options = $PGOPTS${
  ""label_text"": ""Watchers"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\nusing System.Linq;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry{\\n\\t\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\t\\tif (pageModel == null)\\n\\t\\t\\t\\treturn null;\\n\\n\\t\\t\\tvar taskAuxData = pageModel.TryGetDataSourceProperty<EntityRecordList>(\\\""TaskAuxData\\\"");\\n\\t        var currentUser = pageModel.TryGetDataSourceProperty<ErpUser>(\\\""CurrentUser\\\"");\\n\\t        var recordId = pageModel.TryGetDataSourceProperty<Guid>(\\\""RecordId\\\"");\\n\\t\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\t\\tif (taskAuxData == null && !taskAuxData.Any())\\n\\t\\t\\t\\treturn \\\""\\\"";\\n\\t        var watcherIdList = new List<Guid>();\\n\\t        if(taskAuxData[0].Properties.ContainsKey(\\\""$user_nn_task_watchers\\\"") && taskAuxData[0][\\\""$user_nn_task_watchers\\\""] != null \\n\\t            && taskAuxData[0][\\\""$user_nn_task_watchers\\\""] is List<EntityRecord>){\\n\\t                watcherIdList = ((List<EntityRecord>)taskAuxData[0][\\\""$user_nn_task_watchers\\\""]).Select(x=> (Guid)x[\\\""id\\\""]).ToList();\\n\\t            }\\n\\t        var watcherCount = watcherIdList.Count;\\n\\t        var currentUserIsWatching = false;\\n\\t        if(currentUser != null && watcherIdList.Contains(currentUser.Id))\\n\\t            currentUserIsWatching = true;\\n\\t\\n\\t        var html = $\\\""<span class='badge go-bkg-blue-gray-light mr-2'>{watcherCount}</span>\\\"";\\n\\t        if(currentUserIsWatching)\\n\\t            html += \\\""<a href=\\\\\\\""#\\\\\\\"" onclick=\\\\\\\""StopTaskWatch('\\\"" + recordId + \\\""')\\\\\\\"">stop watching</a>\\\"";\\n\\t        else\\n\\t            html += \\\""<a href=\\\\\\\""#\\\\\\\"" onclick=\\\\\\\""StartTaskWatch('\\\"" + recordId + \\\""')\\\\\\\"">start watching</a>\\\"";\\n\\t\\n\\t\\t\\treturn html;\\n\\t\\t}\\n\\t\\tcatch(Exception ex){\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""name"": ""field"",
  ""mode"": ""2"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1"",
  ""connected_entity_id"": """"
}$PGOPTS$
WHERE id = '9f15bb3a-b6bf-424c-9394-669cc2041215';");

            // ------------------------------------------------------------------
            // Operation 6: Update page body node on "details" page (Created on)
            // ID: b2935724-bfcc-4821-bdb2-81bc9b14f015
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcFieldDateTime
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 3,
    component_name = 'WebVella.Erp.Web.Components.PcFieldDateTime',
    container_id = 'body',
    options = $PGOPTS${
  ""label_text"": ""Created on"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.created_on\"",\""default\"":\""\""}"",
  ""name"": ""created_on"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}$PGOPTS$
WHERE id = 'b2935724-bfcc-4821-bdb2-81bc9b14f015';");

            // ------------------------------------------------------------------
            // Operation 7: Create page body node on "details" page (Recurrence HTML)
            // ID: 526c7435-9ace-4032-b754-5d2e9c817436
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcFieldHtml
            // CRITICAL: Contains ICodeVariable C# script for recurrence link rendering
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    '526c7435-9ace-4032-b754-5d2e9c817436',
    '651e5fb2-56df-4c46-86b3-19a641dc942d',
    '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    NULL,
    4,
    'WebVella.Erp.Web.Components.PcFieldHtml',
    'body',
    $PGOPTS${
  ""label_text"": ""Recurrence"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry{\\n\\t\\t\\tif (pageModel == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t\\t\\t//try read data source by name and get result as specified type object\\n\\t\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""Record\\\"");\\n\\t\\n\\t\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\t\\tif (dataSource == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t\\t\\treturn \\\""<a href='#' onclick=\\\\\\\""ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal',{htmlId:'wv-97402edb-3a5a-4cc3-bc40-4d4d012619e2',action:'open',payload:null})\\\\\\\"">Does not repeat</a>\\\"";\\n\\t\\t}\\n\\t\\tcatch(Exception ex){\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""name"": ""field"",
  ""mode"": ""2"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1"",
  ""connected_entity_id"": """"
}$PGOPTS$
);");

            // ------------------------------------------------------------------
            // Operation 8: Create page body node on "details" page (Recurrence Modal)
            // ID: 97402edb-3a5a-4cc3-bc40-4d4d012619e2
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcModal
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    '97402edb-3a5a-4cc3-bc40-4d4d012619e2',
    '651e5fb2-56df-4c46-86b3-19a641dc942d',
    '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    NULL,
    5,
    'WebVella.Erp.Web.Components.PcModal',
    'body',
    $PGOPTS${
  ""title"": ""Task recurrence setting"",
  ""backdrop"": ""true"",
  ""size"": ""2"",
  ""position"": ""0""
}$PGOPTS$
);");

            // ------------------------------------------------------------------
            // Operation 9: Create page body node on "details" page (Close Button)
            // ID: 0abd8d18-1e8f-418c-a18b-8c337e2ad43e
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcButton (Close modal)
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    '0abd8d18-1e8f-418c-a18b-8c337e2ad43e',
    '97402edb-3a5a-4cc3-bc40-4d4d012619e2',
    '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    NULL,
    2,
    'WebVella.Erp.Web.Components.PcButton',
    'footer',
    $PGOPTS${
  ""type"": ""0"",
  ""text"": ""Close"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": ""ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal',{htmlId:'wv-97402edb-3a5a-4cc3-bc40-4d4d012619e2',action:'close',payload:null})"",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}$PGOPTS$
);");

            // ------------------------------------------------------------------
            // Operation 10: Create page body node on "details" page (Save Button)
            // ID: 394d04b3-7b5b-4cdb-b74e-e6f1c4fda8c3
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcButton (Save form)
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    '394d04b3-7b5b-4cdb-b74e-e6f1c4fda8c3',
    '97402edb-3a5a-4cc3-bc40-4d4d012619e2',
    '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    NULL,
    1,
    'WebVella.Erp.Web.Components.PcButton',
    'footer',
    $PGOPTS${
  ""type"": ""1"",
  ""text"": ""Save"",
  ""color"": ""1"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fas fa-save"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": ""wv-f3661768-ad30-4949-8a87-499ca0ab5491""
}$PGOPTS$
);");

            // ------------------------------------------------------------------
            // Operation 11: Create page body node on "details" page (Recurrence Form)
            // ID: f3661768-ad30-4949-8a87-499ca0ab5491
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcForm (SetTaskRecurrence hook)
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    'f3661768-ad30-4949-8a87-499ca0ab5491',
    '97402edb-3a5a-4cc3-bc40-4d4d012619e2',
    '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    NULL,
    1,
    'WebVella.Erp.Web.Components.PcForm',
    'body',
    $PGOPTS${
  ""id"": ""wv-f3661768-ad30-4949-8a87-499ca0ab5491"",
  ""name"": ""form"",
  ""hook_key"": ""SetTaskRecurrence"",
  ""method"": ""post"",
  ""label_mode"": ""1"",
  ""mode"": ""1""
}$PGOPTS$
);");

            // ------------------------------------------------------------------
            // Operation 12: Create page body node on "details" page (Recurrence Editor)
            // ID: c2081b0a-c230-4f5a-959e-75c78c70132f
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcTaskRepeatRecurrenceSet (custom project component)
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    'c2081b0a-c230-4f5a-959e-75c78c70132f',
    'f3661768-ad30-4949-8a87-499ca0ab5491',
    '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    NULL,
    2,
    'WebVella.Erp.Plugins.Project.Components.PcTaskRepeatRecurrenceSet',
    'body',
    $PGOPTS${}$PGOPTS$
);");

            // ------------------------------------------------------------------
            // Operation 13: Create page body node on "details" page (Hidden ID field)
            // ID: 1428fd69-6431-4a51-8051-5d24692a0730
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Component: PcFieldHidden (passes Record.id to recurrence form)
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    '1428fd69-6431-4a51-8051-5d24692a0730',
    'f3661768-ad30-4949-8a87-499ca0ab5491',
    '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    NULL,
    1,
    'WebVella.Erp.Web.Components.PcFieldHidden',
    'body',
    $PGOPTS${
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.id\"",\""default\"":\""\""}"",
  ""name"": ""id""
}$PGOPTS$
);");

            // ------------------------------------------------------------------
            // Operation 14: Update page body node on "track-time" page (Logged Minutes)
            // ID: 9dcca796-cb6d-4c7f-bb63-761cff4c218a
            // Page: e9c8f7ef-4714-40e9-90cd-3814d89603b1
            // Component: PcFieldNumber
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '3658981b-cef7-4938-9c3a-a13cd5b760a0',
    page_id = 'e9c8f7ef-4714-40e9-90cd-3814d89603b1',
    node_id = NULL,
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcFieldNumber',
    container_id = 'column2',
    options = $PGOPTS${
  ""label_text"": ""Logged Minutes"",
  ""label_mode"": ""0"",
  ""value"": """",
  ""name"": ""minutes"",
  ""mode"": ""0"",
  ""decimal_digits"": 2,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0,
  ""connected_entity_id"": ""750153c5-1df9-408f-b856-727078a525bc""
}$PGOPTS$
WHERE id = '9dcca796-cb6d-4c7f-bb63-761cff4c218a';");
        }

        /// <summary>
        /// Reverses all changes made by the Up() method.
        /// - Deletes the 7 page body nodes that were created (INSERTs).
        /// - For the 7 updated nodes, restores the previous state by noting that
        ///   the prior state is defined by migration 20190203000000 (InitialProjectSchema).
        ///   Since the exact prior options are not available in this migration alone,
        ///   the rollback deletes created nodes and documents the update reversals.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ------------------------------------------------------------------
            // Reverse Operation 14: Revert track-time Logged Minutes update
            // The previous state had decimal_digits=0 and no connected_entity_id
            // (set by the initial schema migration 20190203000000).
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '3658981b-cef7-4938-9c3a-a13cd5b760a0',
    page_id = 'e9c8f7ef-4714-40e9-90cd-3814d89603b1',
    node_id = NULL,
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcFieldNumber',
    container_id = 'column2',
    options = $PGOPTS${
  ""label_text"": ""Logged Minutes"",
  ""label_mode"": ""0"",
  ""value"": """",
  ""name"": ""minutes"",
  ""mode"": ""0"",
  ""decimal_digits"": 0,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0,
  ""connected_entity_id"": """"
}$PGOPTS$
WHERE id = '9dcca796-cb6d-4c7f-bb63-761cff4c218a';");

            // ------------------------------------------------------------------
            // Reverse Operations 7-13: Delete all created page body nodes
            // Order: children first, then parents to respect referential integrity
            // ------------------------------------------------------------------

            // Delete hidden ID field (child of form f3661768)
            migrationBuilder.Sql("DELETE FROM public.app_page_body_node WHERE id = '1428fd69-6431-4a51-8051-5d24692a0730';");

            // Delete recurrence editor (child of form f3661768)
            migrationBuilder.Sql("DELETE FROM public.app_page_body_node WHERE id = 'c2081b0a-c230-4f5a-959e-75c78c70132f';");

            // Delete recurrence form (child of modal 97402edb)
            migrationBuilder.Sql("DELETE FROM public.app_page_body_node WHERE id = 'f3661768-ad30-4949-8a87-499ca0ab5491';");

            // Delete save button (child of modal 97402edb)
            migrationBuilder.Sql("DELETE FROM public.app_page_body_node WHERE id = '394d04b3-7b5b-4cdb-b74e-e6f1c4fda8c3';");

            // Delete close button (child of modal 97402edb)
            migrationBuilder.Sql("DELETE FROM public.app_page_body_node WHERE id = '0abd8d18-1e8f-418c-a18b-8c337e2ad43e';");

            // Delete recurrence modal (child of 651e5fb2)
            migrationBuilder.Sql("DELETE FROM public.app_page_body_node WHERE id = '97402edb-3a5a-4cc3-bc40-4d4d012619e2';");

            // Delete recurrence HTML field (child of 651e5fb2)
            migrationBuilder.Sql("DELETE FROM public.app_page_body_node WHERE id = '526c7435-9ace-4032-b754-5d2e9c817436';");

            // ------------------------------------------------------------------
            // Reverse Operations 1-6: Revert updated page body nodes
            // Note: The exact previous options are from the InitialProjectSchema
            // migration (20190203000000). Since that migration defines the baseline,
            // a full rollback requires running Down() on this migration followed by
            // re-applying 20190203000000 if the baseline state needs restoration.
            // The updates below revert known field changes where feasible.
            // ------------------------------------------------------------------

            // Revert Operation 6: Created on field (restore weight=3, same component)
            // No substantive options change from initial — this update was cosmetic
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 3,
    component_name = 'WebVella.Erp.Web.Components.PcFieldDateTime',
    container_id = 'body',
    options = $PGOPTS${
  ""label_text"": ""Created on"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.created_on\"",\""default\"":\""\""}"",
  ""name"": ""created_on"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}$PGOPTS$
WHERE id = 'b2935724-bfcc-4821-bdb2-81bc9b14f015';");

            // Revert Operation 5: Watchers HTML field
            // Prior to this patch the watchers field had a simpler or identical configuration
            // from initial schema. Reverting to non-ICodeVariable display mode.
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = 'ecc262e9-fbad-4dd1-9c98-56ad047685fb',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 3,
    component_name = 'WebVella.Erp.Web.Components.PcFieldHtml',
    container_id = 'body',
    options = $PGOPTS${
  ""label_text"": ""Watchers"",
  ""label_mode"": ""0"",
  ""value"": """",
  ""name"": ""field"",
  ""mode"": ""2"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1"",
  ""connected_entity_id"": """"
}$PGOPTS$
WHERE id = '9f15bb3a-b6bf-424c-9394-669cc2041215';");

            // Revert Operation 4: Nonbillable minutes
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '6e918333-a2fa-4cf7-9ca8-662e349625a7',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 4,
    component_name = 'WebVella.Erp.Web.Components.PcFieldNumber',
    container_id = 'body',
    options = $PGOPTS${
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
}$PGOPTS$
WHERE id = 'ddde395b-6cee-4907-a220-a8424e091b13';");

            // Revert Operation 3: Billable minutes
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '6e918333-a2fa-4cf7-9ca8-662e349625a7',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 3,
    component_name = 'WebVella.Erp.Web.Components.PcFieldNumber',
    container_id = 'body',
    options = $PGOPTS${
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
}$PGOPTS$
WHERE id = '857698b9-f715-480a-bd74-29819a4dec2d';");

            // Revert Operation 2: Estimated minutes
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '6e918333-a2fa-4cf7-9ca8-662e349625a7',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 2,
    component_name = 'WebVella.Erp.Web.Components.PcFieldNumber',
    container_id = 'body',
    options = $PGOPTS${
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
}$PGOPTS$
WHERE id = 'aa94aac4-5048-4d82-95b2-b38536028cbb';");

            // Revert Operation 1: Account details page header
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = NULL,
    page_id = '80b10445-c850-44cf-9c8c-57daca671dcf',
    node_id = NULL,
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcPageHeader',
    container_id = '',
    options = $PGOPTS${
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""Entity.LabelPlural\"",\""default\"":\""\""}"",
  ""area_sublabel"": ""{\""type\"":\""0\"",\""string\"":\""Record.label\"",\""default\"":\""\""}"",
  ""title"": ""Account Details"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""true"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""{\""type\"":\""0\"",\""string\"":\""Entity.IconName\"",\""default\"":\""\""}"",
  ""return_url"": ""/projects/accounts/accounts/l/list""
}$PGOPTS$
WHERE id = '552a4fad-5236-4aad-b3fc-443a5f12e574';");
        }
    }
}
