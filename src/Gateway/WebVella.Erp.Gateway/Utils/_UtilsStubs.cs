// =========================================================================
// WebVella ERP Gateway — Page Utility Helpers
// =========================================================================
// Provides utility methods consumed by Gateway Razor Pages (.cshtml files).
// Derived from monolith WebVella.Erp.Web/Utils/PageUtils.cs.
//
// Referenced by 12 Razor pages via @using WebVella.Erp.Gateway.Utils:
//   ApplicationHome.cshtml, ApplicationNode.cshtml, Index.cshtml,
//   RecordCreate.cshtml, RecordDetails.cshtml, RecordList.cshtml,
//   RecordManage.cshtml, RecordRelatedRecordCreate.cshtml,
//   RecordRelatedRecordDetails.cshtml, RecordRelatedRecordManage.cshtml,
//   RecordRelatedRecordsList.cshtml, Site.cshtml
//
// All pages use PageUtils.ConvertStringToJObject() to parse component
// options from the page body node tree.
// =========================================================================

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WebVella.Erp.Gateway.Utils
{
    /// <summary>
    /// Static utility methods for Razor Page rendering in the Gateway/BFF layer.
    /// Derived from <c>WebVella.Erp.Web.Utils.PageUtils</c> in the monolith.
    ///
    /// Provides JSON conversion helpers used by the page body node rendering loop
    /// in all record-oriented Razor Pages. Each page iterates over
    /// <c>Model.ErpRequestContext.Page.Body</c> nodes and converts the
    /// <c>Options</c> property from a serialized string to a <see cref="JObject"/>
    /// for component rendering.
    /// </summary>
    public static class PageUtils
    {
        /// <summary>
        /// Converts a JSON string to a <see cref="JObject"/>. Returns an empty
        /// <see cref="JObject"/> for null, empty, or trivially-empty ("{}") input.
        ///
        /// Preserved from monolith <c>WebVella.Erp.Web/Utils/PageUtils.cs</c> line 388.
        /// </summary>
        /// <param name="input">
        /// A JSON string representing component options. May be null, empty,
        /// the literal string <c>"{}"</c>, or the escaped literal <c>"\"{}\" "</c>.
        /// </param>
        /// <returns>
        /// A <see cref="JObject"/> parsed from the input, or an empty
        /// <see cref="JObject"/> if the input is null/empty/trivial.
        /// </returns>
        /// <exception cref="JsonReaderException">
        /// Thrown when the input is non-empty but contains invalid JSON.
        /// Callers should handle this gracefully during page rendering.
        /// </exception>
        public static JObject ConvertStringToJObject(string input)
        {
            if (String.IsNullOrWhiteSpace(input) || input == "{}" || input == "\"{}\"")
            {
                return new JObject();
            }
            return JsonConvert.DeserializeObject<JObject>(input);
        }
    }
}
