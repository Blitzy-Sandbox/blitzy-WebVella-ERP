// =============================================================================
// GeographyField.cs — Geography Field Type Model with GeographyFieldFormat Enum
// =============================================================================
// Migrated from monolith source:
//   - WebVella.Erp/Api/Models/FieldTypes/GeographyField.cs (lines 1-54)
//   - Reference: WebVella.Erp/Database/FieldTypes/DbGeographyField.cs
//
// Contains:
//   - GeographyFieldFormat enum — Defines GeoJSON vs Text output format
//   - InputGeographyField class — Request DTO for geography field creation/update
//   - GeographyField class — Persisted/returned geography field model
//
// Namespace Migration:
//   Old: WebVella.Erp.Api.Models
//   New: WebVellaErp.EntityManagement.Models
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json [JsonPropertyName("...")] for AOT-safe serialization
//
// Configuration Migration:
//   Old: ErpSettings.DefaultSRID (global static config binder)
//   New: Constant 4326 (WGS 84 — the standard Spatial Reference System Identifier
//        for GPS coordinates and the universal default for geographic data)
// =============================================================================

using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Defines the serialization/display format for geography field values.
    /// Controls how spatial data is represented when read from the datastore.
    /// <list type="bullet">
    ///   <item><term>GeoJSON</term><description>ST_AsGeoJSON output — RFC 7946 compliant JSON geometry (default)</description></item>
    ///   <item><term>Text</term><description>STAsText (WKT) output — OGC Well-Known Text representation</description></item>
    /// </list>
    /// Migrated from: WebVella.Erp.Api.Models.GeographyFieldFormat (GeographyField.cs lines 48-53)
    /// </summary>
    [Serializable]
    public enum GeographyFieldFormat
    {
        /// <summary>
        /// ST_AsGeoJSON — Returns geography data as GeoJSON (default format).
        /// </summary>
        GeoJSON = 1,

        /// <summary>
        /// STAsText — Returns geography data as Well-Known Text (WKT).
        /// </summary>
        Text = 2
    }

    /// <summary>
    /// Request DTO for creating or updating a geography field definition.
    /// Properties are nullable (inherited from <see cref="InputField"/>) to support
    /// partial updates where only changed values are provided.
    /// 
    /// Geography fields store spatial/geospatial data (points, lines, polygons) and
    /// support configurable output formats (GeoJSON or WKT) and spatial reference
    /// system identifiers (SRID).
    /// 
    /// Migrated from: WebVella.Erp.Api.Models.InputGeographyField (GeographyField.cs lines 6-25)
    /// </summary>
    public class InputGeographyField : InputField
    {
        /// <summary>
        /// Static field type discriminator — always returns <see cref="FieldType.GeographyField"/>.
        /// Used for polymorphic deserialization and field type identification.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType { get { return FieldType.GeographyField; } }

        /// <summary>
        /// Default value for the geography field when a record is created without
        /// explicitly providing a value. Typically a GeoJSON or WKT string, or null
        /// for no default spatial data.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string DefaultValue { get; set; } = string.Empty;

        /// <summary>
        /// Maximum character length for the serialized geography value.
        /// Null indicates no maximum length constraint.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        /// <summary>
        /// Number of visible lines in the UI text area when editing the raw
        /// geography value. Null uses the platform default.
        /// </summary>
        [JsonPropertyName("visibleLineNumber")]
        public int? VisibleLineNumber { get; set; }

        /// <summary>
        /// Output format for geography values: GeoJSON (default) or WKT Text.
        /// Null uses the system default (GeoJSON).
        /// </summary>
        [JsonPropertyName("format")]
        public GeographyFieldFormat? Format { get; set; }

        /// <summary>
        /// Spatial Reference System Identifier. Defaults to 4326 (WGS 84),
        /// the standard coordinate system for GPS and most geographic data.
        /// Replaces the monolith's <c>ErpSettings.DefaultSRID</c> with the
        /// well-known constant for microservice independence.
        /// </summary>
        [JsonPropertyName("srid")]
        public int SRID { get; set; } = 4326;
    }

    /// <summary>
    /// Persisted/returned model for a geography field definition.
    /// Properties are non-nullable (represent fully materialized field metadata).
    /// Inherits base field properties (Id, Name, Label, Required, etc.) from
    /// <see cref="Field"/>.
    /// 
    /// Geography fields store spatial/geospatial data (points, lines, polygons) and
    /// support configurable output formats (GeoJSON or WKT) and spatial reference
    /// system identifiers (SRID).
    /// 
    /// Migrated from: WebVella.Erp.Api.Models.GeographyField (GeographyField.cs lines 27-47)
    /// </summary>
    [Serializable]
    public class GeographyField : Field
    {
        /// <summary>
        /// Static field type discriminator — always returns <see cref="FieldType.GeographyField"/>.
        /// Used for polymorphic deserialization and field type identification.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType { get { return FieldType.GeographyField; } }

        /// <summary>
        /// Default value for the geography field when a record is created without
        /// explicitly providing a value. Typically a GeoJSON or WKT string, or null
        /// for no default spatial data.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string DefaultValue { get; set; } = string.Empty;

        /// <summary>
        /// Maximum character length for the serialized geography value.
        /// Null indicates no maximum length constraint.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        /// <summary>
        /// Number of visible lines in the UI text area when editing the raw
        /// geography value. Null uses the platform default.
        /// </summary>
        [JsonPropertyName("visibleLineNumber")]
        public int? VisibleLineNumber { get; set; }

        /// <summary>
        /// Output format for geography values: GeoJSON (default) or WKT Text.
        /// Null uses the system default (GeoJSON).
        /// </summary>
        [JsonPropertyName("format")]
        public GeographyFieldFormat? Format { get; set; }

        /// <summary>
        /// Spatial Reference System Identifier. Defaults to 4326 (WGS 84),
        /// the standard coordinate system for GPS and most geographic data.
        /// Replaces the monolith's <c>ErpSettings.DefaultSRID</c> with the
        /// well-known constant for microservice independence.
        /// </summary>
        [JsonPropertyName("srid")]
        public int SRID { get; set; } = 4326;
    }
}
