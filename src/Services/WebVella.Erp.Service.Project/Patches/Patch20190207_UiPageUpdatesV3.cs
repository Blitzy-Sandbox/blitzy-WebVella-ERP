using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Project.Patches
{
    /// <summary>
    /// EF Core migration converted from monolith ProjectPlugin.Patch20190207.
    /// Replaces two existing page body nodes on the "create" page
    /// (page ID: 68100014-1fd7-456c-9b26-27aa9f858287) with new PcFieldDate
    /// components for the start_time (weight 10) and end_time (weight 11) fields.
    ///
    /// Original monolith source: WebVella.Erp.Plugins.Project/ProjectPlugin.20190207.cs (79 lines).
    ///
    /// Operations performed:
    ///   1. DELETE page body node ecef4b2c-6988-44c1-acea-0e28385ec528 (old start_time/end_time node)
    ///   2. DELETE page body node 884a8db1-aff0-4f86-ab7d-8fb17698fc33 (old start_time/end_time node)
    ///   3. CREATE page body node d07d36ac-2536-4cf8-9cfc-b07eaa7a1320 (PcFieldDate - start_time)
    ///   4. CREATE page body node db2d036e-df04-4514-9533-2ac31ade4602 (PcFieldDate - end_time)
    ///
    /// All GUIDs and options JSON are preserved verbatim from the monolith source.
    /// </summary>
    [Migration("20190207000000")]
    public class Patch20190207_UiPageUpdatesV3 : Migration
    {
        // Well-known identifiers from the monolith source (preserved for traceability)
        private static readonly Guid DeletedNodeId1 = new Guid("ecef4b2c-6988-44c1-acea-0e28385ec528");
        private static readonly Guid DeletedNodeId2 = new Guid("884a8db1-aff0-4f86-ab7d-8fb17698fc33");
        private static readonly Guid CreatedNodeId1 = new Guid("d07d36ac-2536-4cf8-9cfc-b07eaa7a1320");
        private static readonly Guid CreatedNodeId2 = new Guid("db2d036e-df04-4514-9533-2ac31ade4602");
        private static readonly Guid ParentNodeId = new Guid("a1110167-15bd-46b7-ae3c-cc8ba87be98f");
        private static readonly Guid CreatePageId = new Guid("68100014-1fd7-456c-9b26-27aa9f858287");

        /// <summary>
        /// Applies the migration: deletes two old page body nodes and creates two
        /// replacement PcFieldDate components for start_time and end_time on the
        /// project "create" page.
        /// </summary>
        /// <param name="migrationBuilder">The EF Core migration builder used to emit raw SQL.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // 1. Delete page body node: ecef4b2c-6988-44c1-acea-0e28385ec528
            //    Page name: create (non-cascading delete)
            //    Source: PageService.DeletePageBodyNode(
            //        new Guid("ecef4b2c-6988-44c1-acea-0e28385ec528"),
            //        DbContext.Current.Transaction, cascade: false)
            // ============================================================
            migrationBuilder.Sql(
                "DELETE FROM page_body_nodes WHERE id = 'ecef4b2c-6988-44c1-acea-0e28385ec528';");

            // ============================================================
            // 2. Delete page body node: 884a8db1-aff0-4f86-ab7d-8fb17698fc33
            //    Page name: create (non-cascading delete)
            //    Source: PageService.DeletePageBodyNode(
            //        new Guid("884a8db1-aff0-4f86-ab7d-8fb17698fc33"),
            //        DbContext.Current.Transaction, cascade: false)
            // ============================================================
            migrationBuilder.Sql(
                "DELETE FROM page_body_nodes WHERE id = '884a8db1-aff0-4f86-ab7d-8fb17698fc33';");

            // ============================================================
            // 3. Create page body node: d07d36ac-2536-4cf8-9cfc-b07eaa7a1320
            //    Page name: create — PcFieldDate component for start_time field
            //    Parent: a1110167-15bd-46b7-ae3c-cc8ba87be98f
            //    Page:   68100014-1fd7-456c-9b26-27aa9f858287
            //    Node:   NULL
            //    Container: column2  |  Weight: 10
            //    Source: PageService.CreatePageBodyNode(id, parentId, pageId,
            //        nodeId, weight, componentName, containerId, options, transaction)
            // ============================================================
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes
    (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    'd07d36ac-2536-4cf8-9cfc-b07eaa7a1320',
    'a1110167-15bd-46b7-ae3c-cc8ba87be98f',
    '68100014-1fd7-456c-9b26-27aa9f858287',
    NULL,
    10,
    'WebVella.Erp.Web.Components.PcFieldDate',
    'column2',
    '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.start_time\"",\""default\"":\""\""}"",
  ""name"": ""start_time"",
  ""mode"": ""0"",
  ""connected_entity_id"": """"
}'
);");

            // ============================================================
            // 4. Create page body node: db2d036e-df04-4514-9533-2ac31ade4602
            //    Page name: create — PcFieldDate component for end_time field
            //    Parent: a1110167-15bd-46b7-ae3c-cc8ba87be98f
            //    Page:   68100014-1fd7-456c-9b26-27aa9f858287
            //    Node:   NULL
            //    Container: column2  |  Weight: 11
            //    Source: PageService.CreatePageBodyNode(id, parentId, pageId,
            //        nodeId, weight, componentName, containerId, options, transaction)
            // ============================================================
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes
    (id, parent_id, page_id, node_id, weight, component_name, container_id, options)
VALUES (
    'db2d036e-df04-4514-9533-2ac31ade4602',
    'a1110167-15bd-46b7-ae3c-cc8ba87be98f',
    '68100014-1fd7-456c-9b26-27aa9f858287',
    NULL,
    11,
    'WebVella.Erp.Web.Components.PcFieldDate',
    'column2',
    '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.end_time\"",\""default\"":\""\""}"",
  ""name"": ""end_time"",
  ""mode"": ""0"",
  ""connected_entity_id"": """"
}'
);");
        }

        /// <summary>
        /// Rolls back the migration by removing the two PcFieldDate nodes created in
        /// <see cref="Up"/>. The two nodes deleted during Up()
        /// (ecef4b2c-6988-44c1-acea-0e28385ec528 and 884a8db1-aff0-4f86-ab7d-8fb17698fc33)
        /// were originally provisioned by Patch20190203_InitialProjectSchema. A full reversal
        /// to the pre-Patch20190207 state requires re-running that initial migration's relevant
        /// INSERT statements for those two node IDs.
        /// </summary>
        /// <param name="migrationBuilder">The EF Core migration builder used to emit raw SQL.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // Reverse step 4: Remove end_time PcFieldDate node
            // ============================================================
            migrationBuilder.Sql(
                "DELETE FROM page_body_nodes WHERE id = 'db2d036e-df04-4514-9533-2ac31ade4602';");

            // ============================================================
            // Reverse step 3: Remove start_time PcFieldDate node
            // ============================================================
            migrationBuilder.Sql(
                "DELETE FROM page_body_nodes WHERE id = 'd07d36ac-2536-4cf8-9cfc-b07eaa7a1320';");
        }
    }
}
