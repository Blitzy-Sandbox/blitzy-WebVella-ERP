using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Project.Patches
{
    /// <summary>
    /// EF Core migration converted from monolith ProjectPlugin.Patch20211013.
    /// Updates the track-time page cancel button (PcButton component) page body node
    /// to change the onclick handler from an ErpEvent.DISPATCH modal close command
    /// to a jQuery-based modal hide call, and adds additional button option properties
    /// (icon_right, is_visible).
    ///
    /// Original monolith source: WebVella.Erp.Plugins.Project/ProjectPlugin.20211013.cs (53 lines).
    ///
    /// Operations performed:
    ///   1. UPDATE page body node 9946104a-a6ec-4a0b-b996-7bc630c16287:
    ///      - Page: track-time (e9c8f7ef-4714-40e9-90cd-3814d89603b1)
    ///      - Parent: 6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4
    ///      - Component: WebVella.Erp.Web.Components.PcButton
    ///      - Container: footer, Weight: 2
    ///      - Key change: onclick handler updated from
    ///        ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal',{htmlId:'wv-...',action:'close',payload:null})
    ///        to $('#wv-6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4').modal("hide")
    ///      - Added properties: icon_right, is_visible (not in original creation)
    ///
    /// Business logic preserved:
    ///   - Cancel button on track-time page footer closes the parent modal dialog
    ///   - Button styling: type=0, color=0, size=3 (unchanged)
    ///   - Button text: "Cancel" (unchanged)
    ///
    /// All GUIDs and options JSON are preserved verbatim from the monolith source.
    /// PluginSettings.Version tracking replaced by EF Core __EFMigrationsHistory table.
    /// </summary>
    [Migration("20211013000000")]
    public class Patch20211013_DashboardTaskUiNodes : Migration
    {
        // Well-known identifiers from the monolith source (preserved for traceability)
        private static readonly Guid NodeId = new Guid("9946104a-a6ec-4a0b-b996-7bc630c16287");
        private static readonly Guid ParentNodeId = new Guid("6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4");
        private static readonly Guid TrackTimePageId = new Guid("e9c8f7ef-4714-40e9-90cd-3814d89603b1");

        /// <summary>
        /// Applies the migration: updates the track-time page cancel button (PcButton)
        /// page body node with revised options JSON. The onclick handler is changed from
        /// an ErpEvent.DISPATCH call to a direct jQuery modal("hide") invocation.
        ///
        /// This migration also adds the icon_right and is_visible properties to the
        /// button options that were not present in the original Patch20190203 creation.
        ///
        /// Source operation:
        ///   new WebVella.Erp.Web.Services.PageService().UpdatePageBodyNode(
        ///       id, parentId, pageId, nodeId, weight,
        ///       componentName, containerId, options,
        ///       DbContext.Current.Transaction)
        ///
        /// PostgreSQL dollar-quoting ($PGOPTS$...$PGOPTS$) is used for the options column
        /// value to safely embed JSON containing double quotes and special characters.
        /// </summary>
        /// <param name="migrationBuilder">The EF Core migration builder used to emit raw SQL.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // Update page body node: 9946104a-a6ec-4a0b-b996-7bc630c16287
            // Page: track-time (e9c8f7ef-4714-40e9-90cd-3814d89603b1)
            // Parent: 6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4
            // Component: WebVella.Erp.Web.Components.PcButton
            // Container: footer | Weight: 2
            //
            // Source: ProjectPlugin.20211013.cs — Patch20211013 method
            //   new PageService().UpdatePageBodyNode(id, parentId, pageId,
            //       nodeId, weight, componentName, containerId, options,
            //       DbContext.Current.Transaction)
            //
            // Key change: onclick handler updated to use jQuery modal hide
            // instead of ErpEvent.DISPATCH for closing the track-time modal.
            //
            // CRITICAL: All options JSON properties are preserved exactly
            // from the monolith source file, including type, text, color,
            // size, class, id, icon_class, is_block, is_outline, icon_right,
            // is_active, is_disabled, is_visible, onclick, href, new_tab, form.
            // ============================================================
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4',
    page_id = 'e9c8f7ef-4714-40e9-90cd-3814d89603b1',
    node_id = NULL,
    weight = 2,
    component_name = 'WebVella.Erp.Web.Components.PcButton',
    container_id = 'footer',
    options = $PGOPTS${
  ""type"": ""0"",
  ""text"": ""Cancel"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""icon_right"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""is_visible"": """",
  ""onclick"": ""$('#wv-6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4').modal(\""hide\"")"",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}$PGOPTS$
WHERE id = '9946104a-a6ec-4a0b-b996-7bc630c16287';");
        }

        /// <summary>
        /// Rolls back the migration by reverting the track-time cancel button (PcButton)
        /// page body node to its original state as provisioned by Patch20190203_InitialProjectSchema.
        ///
        /// Key reversions:
        ///   - Restores the onclick handler from jQuery modal("hide") back to the
        ///     ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal', ...) call
        ///   - Removes the icon_right and is_visible properties added in Up()
        ///   - Restores original JSON property set matching the initial creation
        ///
        /// Rollback strategy:
        ///   The page body node is reverted to the exact options JSON from the initial
        ///   Patch20190203 creation (line 7365 of ProjectPlugin.20190203.cs), which used
        ///   ErpEvent.DISPATCH to close the modal and did not include icon_right or is_visible.
        /// </summary>
        /// <param name="migrationBuilder">The EF Core migration builder used to emit raw SQL.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // Revert page body node: 9946104a-a6ec-4a0b-b996-7bc630c16287
            // Restore to original state from Patch20190203_InitialProjectSchema.
            //
            // Key differences from Up() options:
            //   - onclick uses ErpEvent.DISPATCH modal close instead of jQuery modal("hide")
            //   - No icon_right property
            //   - No is_visible property
            //   - Original property set: type, text, color, size, class, id,
            //     icon_class, is_block, is_outline, is_active, is_disabled,
            //     onclick, href, new_tab, form
            //
            // Source: Patch20190203 creation at ProjectPlugin.20190203.cs line 7365
            // ============================================================
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = '6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4',
    page_id = 'e9c8f7ef-4714-40e9-90cd-3814d89603b1',
    node_id = NULL,
    weight = 2,
    component_name = 'WebVella.Erp.Web.Components.PcButton',
    container_id = 'footer',
    options = $PGOPTS${
  ""type"": ""0"",
  ""text"": ""Cancel"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": ""ErpEvent.DISPATCH('WebVella.Erp.Web.Components.PcModal',{htmlId:'wv-6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4',action:'close',payload:null})"",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}$PGOPTS$
WHERE id = '9946104a-a6ec-4a0b-b996-7bc630c16287';");
        }
    }
}
