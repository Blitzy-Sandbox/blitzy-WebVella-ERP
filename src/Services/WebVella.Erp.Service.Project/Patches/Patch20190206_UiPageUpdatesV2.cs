using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Project.Patches
{
    /// <summary>
    /// EF Core migration converting monolith ProjectPlugin.Patch20190206.
    /// Replaces the PcFieldDateTime nodes for start_time and end_time with PcFieldDate
    /// nodes on the "details" page (3a40b8e6-0a87-4eee-9b6b-6c665ebee28c), and adjusts
    /// weights of existing sibling nodes to accommodate the new entries.
    /// Source: WebVella.Erp.Plugins.Project/ProjectPlugin.20190206.cs (144 lines)
    ///
    /// Operations converted:
    ///   2 DeletePageBodyNode calls → DELETE SQL on public.app_page_body_node
    ///   2 CreatePageBodyNode calls → INSERT SQL on public.app_page_body_node
    ///   3 UpdatePageBodyNode calls → UPDATE SQL on public.app_page_body_node
    ///
    /// Semantic change: DateTime fields replaced with Date-only fields for start_time
    /// and end_time; weights shifted (3→4, 4→5, 5→6) to accommodate new nodes at
    /// weight 1 (start_time) and weight 3 (end_time).
    /// </summary>
    [Migration("20190206000000")]
    public class Patch20190206_UiPageUpdatesV2 : Migration
    {
        /// <summary>
        /// Applies all page body node delete, create, and update operations from the
        /// original Patch20190206. Uses PostgreSQL dollar-quoting ($PGOPTS$...$PGOPTS$)
        /// for the options column to safely embed JSON containing double quotes,
        /// backslashes, and nested escape sequences (including ICodeVariable C# scripts).
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ------------------------------------------------------------------
            // Operation 1: Delete page body node on "details" page (old start_time DateTime)
            // ID: 291477ec-dd9c-4fc3-97a0-d2fd62809b2f
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Original component: PcFieldDateTime (start_time, weight=2)
            // cascade=false — deletes only this node, no children
            // ------------------------------------------------------------------
            migrationBuilder.Sql(
                "DELETE FROM public.app_page_body_node WHERE id = '291477ec-dd9c-4fc3-97a0-d2fd62809b2f';");

            // ------------------------------------------------------------------
            // Operation 2: Delete page body node on "details" page (old end_time DateTime)
            // ID: 798e39b3-7a36-406b-bed6-e77da68fc50f
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Original component: PcFieldDateTime (end_time, weight=1 — from initial schema,
            // but note the original carried start_time label at weight=1)
            // cascade=false — deletes only this node, no children
            // ------------------------------------------------------------------
            migrationBuilder.Sql(
                "DELETE FROM public.app_page_body_node WHERE id = '798e39b3-7a36-406b-bed6-e77da68fc50f';");

            // ------------------------------------------------------------------
            // Operation 3: Create page body node on "details" page (new start_time Date)
            // ID: 151d5da3-161a-44c0-97fa-84c76c9d3b60
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Parent: 651e5fb2-56df-4c46-86b3-19a641dc942d
            // Component: PcFieldDate (replaces old PcFieldDateTime for start_time)
            // Weight: 1
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node
    (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    '151d5da3-161a-44c0-97fa-84c76c9d3b60',
    '651e5fb2-56df-4c46-86b3-19a641dc942d',
    '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    NULL,
    1,
    'WebVella.Erp.Web.Components.PcFieldDate',
    'body',
    $PGOPTS${
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.start_time\"",\""default\"":\""\""}"",
  ""name"": ""start_time"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}$PGOPTS$
);");

            // ------------------------------------------------------------------
            // Operation 4: Create page body node on "details" page (new end_time Date)
            // ID: caa34ee6-0be6-48eb-b6bd-8b9f1ef83009
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Parent: 651e5fb2-56df-4c46-86b3-19a641dc942d
            // Component: PcFieldDate (replaces old PcFieldDateTime for end_time)
            // Weight: 3
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node
    (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    'caa34ee6-0be6-48eb-b6bd-8b9f1ef83009',
    '651e5fb2-56df-4c46-86b3-19a641dc942d',
    '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    NULL,
    3,
    'WebVella.Erp.Web.Components.PcFieldDate',
    'body',
    $PGOPTS${
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.end_time\"",\""default\"":\""\""}"",
  ""name"": ""end_time"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}$PGOPTS$
);");

            // ------------------------------------------------------------------
            // Operation 5: Update page body node on "details" page (Created on)
            // ID: b2935724-bfcc-4821-bdb2-81bc9b14f015
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Parent: 651e5fb2-56df-4c46-86b3-19a641dc942d
            // Component: PcFieldDateTime
            // Weight change: 3 → 4 (shifted down to make room for new Date nodes)
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 4,
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
            // Operation 6: Update page body node on "details" page (Recurrence HTML)
            // ID: 526c7435-9ace-4032-b754-5d2e9c817436
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Parent: 651e5fb2-56df-4c46-86b3-19a641dc942d
            // Component: PcFieldHtml
            // Weight change: 4 → 5 (shifted down)
            // CRITICAL: Options contain ICodeVariable C# script for recurrence link —
            // preserved verbatim from source including all escape sequences.
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 5,
    component_name = 'WebVella.Erp.Web.Components.PcFieldHtml',
    container_id = 'body',
    options = $PGOPTS${
  ""label_text"": ""Recurrence"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry{\\n\\t\\t\\tif (pageModel == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t\\t\\t//try read data source by name and get result as specified type object\\n\\t\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""Record\\\"");\\n\\t\\n\\t\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\t\\tif (dataSource == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t\\t\\treturn \\\""<a href='#' onclick=\\\\\\\""ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal',{htmlId:'wv-97402edb-3a5a-4cc3-bc40-4d4d012619e2',action:'open',payload:null})\\\\\\\"">Does not repeat</a>\\\"";\\n\\t\\t}\\n\\t\\tcatch(Exception ex){\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""name"": ""field"",
  ""mode"": ""2"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1"",
  ""connected_entity_id"": """"
}$PGOPTS$
WHERE id = '526c7435-9ace-4032-b754-5d2e9c817436';");

            // ------------------------------------------------------------------
            // Operation 7: Update page body node on "details" page (Recurrence Modal)
            // ID: 97402edb-3a5a-4cc3-bc40-4d4d012619e2
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // Parent: 651e5fb2-56df-4c46-86b3-19a641dc942d
            // Component: PcModal
            // Weight change: 5 → 6 (shifted down)
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 6,
    component_name = 'WebVella.Erp.Web.Components.PcModal',
    container_id = 'body',
    options = $PGOPTS${
  ""title"": ""Task recurrence setting"",
  ""backdrop"": ""true"",
  ""size"": ""2"",
  ""position"": ""0""
}$PGOPTS$
WHERE id = '97402edb-3a5a-4cc3-bc40-4d4d012619e2';");
        }

        /// <summary>
        /// Reverses all changes made by the Up() method.
        /// - Reverts the 3 updated page body nodes to their previous weight values
        ///   (state as set by migration 20190205000000).
        /// - Deletes the 2 newly created PcFieldDate nodes.
        /// - Re-inserts the 2 deleted PcFieldDateTime nodes (original data from
        ///   migration 20190203000000 — InitialProjectSchema).
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ------------------------------------------------------------------
            // Reverse Operation 7: Revert Recurrence Modal weight from 6 back to 5
            // Previous state set by migration 20190205000000
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 5,
    component_name = 'WebVella.Erp.Web.Components.PcModal',
    container_id = 'body',
    options = $PGOPTS${
  ""title"": ""Task recurrence setting"",
  ""backdrop"": ""true"",
  ""size"": ""2"",
  ""position"": ""0""
}$PGOPTS$
WHERE id = '97402edb-3a5a-4cc3-bc40-4d4d012619e2';");

            // ------------------------------------------------------------------
            // Reverse Operation 6: Revert Recurrence HTML weight from 5 back to 4
            // Previous state set by migration 20190205000000
            // ICodeVariable C# script preserved verbatim.
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    node_id = NULL,
    weight = 4,
    component_name = 'WebVella.Erp.Web.Components.PcFieldHtml',
    container_id = 'body',
    options = $PGOPTS${
  ""label_text"": ""Recurrence"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry{\\n\\t\\t\\tif (pageModel == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t\\t\\t//try read data source by name and get result as specified type object\\n\\t\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""Record\\\"");\\n\\t\\n\\t\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\t\\tif (dataSource == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t\\t\\treturn \\\""<a href='#' onclick=\\\\\\\""ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal',{htmlId:'wv-97402edb-3a5a-4cc3-bc40-4d4d012619e2',action:'open',payload:null})\\\\\\\"">Does not repeat</a>\\\"";\\n\\t\\t}\\n\\t\\tcatch(Exception ex){\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""name"": ""field"",
  ""mode"": ""2"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1"",
  ""connected_entity_id"": """"
}$PGOPTS$
WHERE id = '526c7435-9ace-4032-b754-5d2e9c817436';");

            // ------------------------------------------------------------------
            // Reverse Operation 5: Revert Created on weight from 4 back to 3
            // Previous state set by migration 20190205000000
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
            // Reverse Operations 3-4: Delete the two PcFieldDate nodes created in Up()
            // ------------------------------------------------------------------
            migrationBuilder.Sql(
                "DELETE FROM public.app_page_body_node WHERE id = 'caa34ee6-0be6-48eb-b6bd-8b9f1ef83009';");

            migrationBuilder.Sql(
                "DELETE FROM public.app_page_body_node WHERE id = '151d5da3-161a-44c0-97fa-84c76c9d3b60';");

            // ------------------------------------------------------------------
            // Reverse Operations 1-2: Re-insert the two deleted PcFieldDateTime nodes
            // Original data sourced from migration 20190203000000 (InitialProjectSchema)
            // ------------------------------------------------------------------

            // Restore node 798e39b3: PcFieldDateTime for start_time (weight=1)
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node
    (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    '798e39b3-7a36-406b-bed6-e77da68fc50f',
    '651e5fb2-56df-4c46-86b3-19a641dc942d',
    '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    NULL,
    1,
    'WebVella.Erp.Web.Components.PcFieldDateTime',
    'body',
    $PGOPTS${
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.start_time\"",\""default\"":\""\""}"",
  ""name"": ""start_time"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}$PGOPTS$
);");

            // Restore node 291477ec: PcFieldDateTime for end_time (weight=2)
            migrationBuilder.Sql(@"INSERT INTO public.app_page_body_node
    (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    '291477ec-dd9c-4fc3-97a0-d2fd62809b2f',
    '651e5fb2-56df-4c46-86b3-19a641dc942d',
    '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    NULL,
    2,
    'WebVella.Erp.Web.Components.PcFieldDateTime',
    'body',
    $PGOPTS${
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.end_time\"",\""default\"":\""\""}"",
  ""name"": ""end_time"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}$PGOPTS$
);");
        }
    }
}
