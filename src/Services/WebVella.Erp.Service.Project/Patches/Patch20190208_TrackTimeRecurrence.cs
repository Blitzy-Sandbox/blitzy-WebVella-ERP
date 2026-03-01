using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Project.Patches
{
    /// <summary>
    /// EF Core migration converted from monolith ProjectPlugin.Patch20190208.
    /// Updates the track-time timer page body node (PcFieldHtml component) on the
    /// track-time page (e9c8f7ef-4714-40e9-90cd-3814d89603b1) to adjust the
    /// ICodeVariable C# script that renders the timer display.
    ///
    /// Original monolith source: WebVella.Erp.Plugins.Project/ProjectPlugin.20190208.cs (47 lines).
    ///
    /// Operations performed:
    ///   1. UPDATE page body node c57d94a6-9c90-4071-b54b-2c05b79aa522:
    ///      - Component: WebVella.Erp.Web.Components.PcFieldHtml
    ///      - Parent: e84c527a-4feb-4d60-ab91-4b1ecd89b39c
    ///      - Container: column2, Weight: 1
    ///      - Key change: Comments out the real-time seconds accumulation line
    ///        (loggedSeconds += DateTime.UtcNow delta), making the timer display
    ///        show only the recorded logged_minutes * 60 seconds, with the client-side
    ///        wv-timer class handling live counting instead.
    ///      - Adds "class" property to options; reorders JSON properties.
    ///
    /// Business logic preserved:
    ///   - Timer hours:minutes:seconds calculation from logged_minutes field
    ///   - Hidden inputs for timelog_total_seconds and timelog_started_on
    ///   - ICodeVariable evaluation reading RowRecord data source
    ///   - Default display: "00 : 00 : 00" with go-gray styling
    ///
    /// All GUIDs and options JSON are preserved verbatim from the monolith source.
    /// </summary>
    [Migration("20190208000000")]
    public class Patch20190208_TrackTimeRecurrence : Migration
    {
        // Well-known identifiers from the monolith source (preserved for traceability)
        private static readonly Guid NodeId = new Guid("c57d94a6-9c90-4071-b54b-2c05b79aa522");
        private static readonly Guid ParentNodeId = new Guid("e84c527a-4feb-4d60-ab91-4b1ecd89b39c");
        private static readonly Guid TrackTimePageId = new Guid("e9c8f7ef-4714-40e9-90cd-3814d89603b1");

        /// <summary>
        /// Applies the migration: updates the track-time timer PcFieldHtml page body node
        /// with revised options JSON. The ICodeVariable C# script within the options computes
        /// hours:minutes:seconds from logged_minutes, renders the wv-timer span, and emits
        /// hidden inputs for timelog_total_seconds and timelog_started_on.
        ///
        /// Key behavioral change from Patch20190203 original:
        ///   - The line that accumulated real-time elapsed seconds from timelog_started_on
        ///     is now commented out, delegating live timer updates to the client-side
        ///     wv-timer JavaScript component.
        ///
        /// PostgreSQL dollar-quoting ($PGOPTS$...$PGOPTS$) is used for the options column
        /// value to safely embed JSON containing double quotes, backslashes, single quotes,
        /// and nested escape sequences (the ICodeVariable C# code string).
        /// </summary>
        /// <param name="migrationBuilder">The EF Core migration builder used to emit raw SQL.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // Update page body node: c57d94a6-9c90-4071-b54b-2c05b79aa522
            // Page: track-time (e9c8f7ef-4714-40e9-90cd-3814d89603b1)
            // Parent: e84c527a-4feb-4d60-ab91-4b1ecd89b39c
            // Component: WebVella.Erp.Web.Components.PcFieldHtml
            // Container: column2 | Weight: 1
            //
            // Source: new PageService().UpdatePageBodyNode(
            //     id, parentId, pageId, nodeId, weight,
            //     componentName, containerId, options,
            //     DbContext.Current.Transaction)
            //
            // The options JSON contains an ICodeVariable C# script that:
            //   - Reads RowRecord data source for logged_minutes and timelog_started_on
            //   - Computes hours:minutes:seconds display string
            //   - Generates HTML span with class 'go-gray wv-timer'
            //   - Emits hidden inputs for timelog_total_seconds and timelog_started_on
            //
            // CRITICAL: The embedded C# code and all escape sequences are
            // preserved EXACTLY from the monolith source file.
            // ============================================================
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = 'e84c527a-4feb-4d60-ab91-4b1ecd89b39c',
    page_id = 'e9c8f7ef-4714-40e9-90cd-3814d89603b1',
    node_id = NULL,
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcFieldHtml',
    container_id = 'column2',
    options = $PGOPTS${
  ""label_mode"": ""0"",
  ""label_text"": """",
  ""mode"": ""4"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry{\\n\\t\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\t\\tif (pageModel == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t\\t\\t//try read data source by name and get result as specified type object\\n\\t\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""RowRecord\\\"");\\n\\t\\n\\t\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\t\\tif (dataSource == null)\\n\\t\\t\\t\\treturn null;\\n\\t        var loggedSeconds = ((int)dataSource[\\\""logged_minutes\\\""])*60;\\n\\t        var logStartedOn = (DateTime?)dataSource[\\\""timelog_started_on\\\""];\\n\\t        var logStartString = \\\""\\\"";\\n\\t        if(logStartedOn != null){\\n\\t            //loggedSeconds = loggedSeconds + (int)((DateTime.UtcNow - logStartedOn.Value).TotalSeconds);\\n\\t            logStartString = logStartedOn.Value.ToString(\\\""o\\\"");\\n\\t        }\\n\\n\\t        var hours = (int)(loggedSeconds/3600);\\n\\t        var loggedSecondsLeft = loggedSeconds - hours*3600;\\n\\t        var hoursString = \\\""00\\\"";\\n\\t        if(hours < 10)\\n\\t            hoursString = \\\""0\\\"" + hours;\\n            else\\n                hoursString = hours.ToString();\\n\\t            \\n\\t        var minutes = (int)(loggedSecondsLeft/60);\\n\\t        var minutesString = \\\""00\\\"";\\n\\t        if(minutes < 10)\\n\\t            minutesString = \\\""0\\\"" + minutes;\\n            else\\n                minutesString = minutes.ToString();\\t        \\n                \\n            var seconds =  loggedSecondsLeft -  minutes*60;\\n\\t        var secondsString = \\\""00\\\"";\\n\\t        if(seconds < 10)\\n\\t            secondsString = \\\""0\\\"" + seconds;\\n            else\\n                secondsString = seconds.ToString();\\t                    \\n            \\n            var result = $\\\""<span class='go-gray wv-timer' style='font-size:16px;'>{hoursString + \\\"" : \\\"" + minutesString + \\\"" : \\\"" + secondsString}</span>\\\\n\\\"";\\n            result += $\\\""<input type='hidden' name='timelog_total_seconds' value='{loggedSeconds}'/>\\\\n\\\"";\\n            result += $\\\""<input type='hidden' name='timelog_started_on' value='{logStartString}'/>\\\"";\\n            return result;\\n\\t\\t}\\n\\t\\tcatch(Exception ex){\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\\n\"",\""default\"":\""<span class=\\\""go-gray\\\"" style='font-size:16px;'>00 : 00 : 00</span>\""}"",
  ""name"": ""field"",
  ""class"": """",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1"",
  ""connected_entity_id"": """"
}$PGOPTS$
WHERE id = 'c57d94a6-9c90-4071-b54b-2c05b79aa522';");
        }

        /// <summary>
        /// Rolls back the migration by reverting the track-time timer PcFieldHtml page body
        /// node to its original state as provisioned by Patch20190203_InitialProjectSchema.
        ///
        /// Key reversion:
        ///   - Restores the real-time seconds accumulation line (un-comments the
        ///     loggedSeconds += DateTime.UtcNow delta calculation)
        ///   - Removes the "class" property added in Up()
        ///   - Restores original JSON property ordering (label_text before label_mode,
        ///     mode after name)
        ///
        /// The ICodeVariable C# script and all other component configuration are restored
        /// to the exact state from the initial Patch20190203 creation.
        /// </summary>
        /// <param name="migrationBuilder">The EF Core migration builder used to emit raw SQL.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // Revert page body node: c57d94a6-9c90-4071-b54b-2c05b79aa522
            // Restore to original state from Patch20190203_InitialProjectSchema.
            //
            // Key difference from Up() options:
            //   - The line computing real-time elapsed seconds is active (not commented):
            //       loggedSeconds = loggedSeconds + (int)((DateTime.UtcNow -
            //           logStartedOn.Value).TotalSeconds);
            //   - No "class" property in options
            //   - Original property order: label_text, label_mode, value, name, mode, ...
            //
            // Source: Patch20190203 creation of this node at line 2362-2383
            // ============================================================
            migrationBuilder.Sql(@"UPDATE public.app_page_body_node SET
    parent_id = 'e84c527a-4feb-4d60-ab91-4b1ecd89b39c',
    page_id = 'e9c8f7ef-4714-40e9-90cd-3814d89603b1',
    node_id = NULL,
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcFieldHtml',
    container_id = 'column2',
    options = $PGOPTS${
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry{\\n\\t\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\t\\tif (pageModel == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t\\t\\t//try read data source by name and get result as specified type object\\n\\t\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""RowRecord\\\"");\\n\\t\\n\\t\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\t\\tif (dataSource == null)\\n\\t\\t\\t\\treturn null;\\n\\t        var loggedSeconds = ((int)dataSource[\\\""logged_minutes\\\""])*60;\\n\\t        var logStartedOn = (DateTime?)dataSource[\\\""timelog_started_on\\\""];\\n\\t        var logStartString = \\\""\\\"";\\n\\t        if(logStartedOn != null){\\n\\t            loggedSeconds = loggedSeconds + (int)((DateTime.UtcNow - logStartedOn.Value).TotalSeconds);\\n\\t            logStartString = logStartedOn.Value.ToString(\\\""o\\\"");\\n\\t        }\\n\\n\\t        var hours = (int)(loggedSeconds/3600);\\n\\t        var loggedSecondsLeft = loggedSeconds - hours*3600;\\n\\t        var hoursString = \\\""00\\\"";\\n\\t        if(hours < 10)\\n\\t            hoursString = \\\""0\\\"" + hours;\\n            else\\n                hoursString = hours.ToString();\\n\\t            \\n\\t        var minutes = (int)(loggedSecondsLeft/60);\\n\\t        var minutesString = \\\""00\\\"";\\n\\t        if(minutes < 10)\\n\\t            minutesString = \\\""0\\\"" + minutes;\\n            else\\n                minutesString = minutes.ToString();\\t        \\n                \\n            var seconds =  loggedSecondsLeft -  minutes*60;\\n\\t        var secondsString = \\\""00\\\"";\\n\\t        if(seconds < 10)\\n\\t            secondsString = \\\""0\\\"" + seconds;\\n            else\\n                secondsString = seconds.ToString();\\t                    \\n            \\n            var result = $\\\""<span class='go-gray wv-timer' style='font-size:16px;'>{hoursString + \\\"" : \\\"" + minutesString + \\\"" : \\\"" + secondsString}</span>\\\\n\\\"";\\n            result += $\\\""<input type='hidden' name='timelog_total_seconds' value='{loggedSeconds}'/>\\\\n\\\"";\\n            result += $\\\""<input type='hidden' name='timelog_started_on' value='{logStartString}'/>\\\"";\\n            return result;\\n\\t\\t}\\n\\t\\tcatch(Exception ex){\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\\n\"",\""default\"":\""<span class=\\\""go-gray\\\"" style='font-size:16px;'>00 : 00 : 00</span>\""}"",
  ""name"": ""field"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1"",
  ""connected_entity_id"": """"
}$PGOPTS$
WHERE id = 'c57d94a6-9c90-4071-b54b-2c05b79aa522';");
        }
    }
}
