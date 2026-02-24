// =============================================================================
// AutoNumberField.cs — Auto-Number Field Type Model
// =============================================================================
// Migrates the auto-number field type from the monolith source:
//   - WebVella.Erp/Api/Models/FieldTypes/AutoNumberField.cs (API models)
//   - WebVella.Erp/Database/FieldTypes/DbAutoNumberField.cs (DB model, reference)
//
// Contains two classes:
//   1. InputAutoNumberField : InputField — request DTO for creating/updating
//      auto-number fields via the Entity Management Lambda handlers.
//   2. AutoNumberField : Field — persisted/returned model representing an
//      auto-number field stored in DynamoDB (PK=ENTITY#{id}, SK=FIELD#{fieldId}).
//
// Auto-number fields generate sequential numeric identifiers for records,
// supporting configurable starting numbers and display format strings
// (e.g., "INV-{0:0000}" for invoice numbering).
//
// Namespace Migration:
//   Old: WebVella.Erp.Api.Models (monolith)
//   New: WebVellaErp.EntityManagement.Models (FieldTypes is organizational only)
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json [JsonPropertyName("...")] for AOT-safe serialization
//
// Source: WebVella.Erp/Api/Models/FieldTypes/AutoNumberField.cs lines 7-36
// =============================================================================

using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Input (request) DTO for creating or updating an auto-number field definition.
    /// Inherits common field properties (Id, Name, Label, Required, Searchable, etc.)
    /// from <see cref="InputField"/> and adds auto-number–specific configuration:
    /// starting number, display format, and default value.
    ///
    /// Used by FieldHandler Lambda to deserialize POST/PUT payloads for auto-number
    /// field creation and modification requests.
    ///
    /// Migrated from: WebVella.Erp.Api.Models.InputAutoNumberField (lines 7-20)
    /// </summary>
    public class InputAutoNumberField : InputField
    {
        /// <summary>
        /// Static discriminator property identifying this input as an auto-number field.
        /// Used for polymorphic field type dispatch during deserialization.
        /// Always returns <see cref="FieldType.AutoNumberField"/> (enum value 1).
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.AutoNumberField;

        /// <summary>
        /// The default numeric value assigned to new records when no explicit value
        /// is provided. Nullable: when null, the system uses StartingNumber or 0.
        /// Stored as decimal for consistency with the monolith's numeric precision.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        /// <summary>
        /// Format string controlling how the auto-number is displayed to users.
        /// Supports standard .NET composite formatting (e.g., "{0:0000}" produces
        /// zero-padded four-digit numbers like "0001", "0042"). When empty or null,
        /// the raw numeric value is displayed.
        /// </summary>
        [JsonPropertyName("displayFormat")]
        public string DisplayFormat { get; set; } = string.Empty;

        /// <summary>
        /// The initial numeric value from which auto-numbering begins for new
        /// entities. Subsequent records increment from the highest existing value.
        /// Nullable: when null, numbering starts from 1 by default.
        /// </summary>
        [JsonPropertyName("startingNumber")]
        public decimal? StartingNumber { get; set; }
    }

    /// <summary>
    /// Persisted model representing an auto-number field stored in DynamoDB.
    /// Inherits common persisted field properties (Id, Name, Label, Required,
    /// Searchable, EntityName, etc.) from <see cref="Field"/> and adds
    /// auto-number–specific configuration.
    ///
    /// Returned by Entity Management API responses and used internally by
    /// RecordService for auto-number value generation during record creation.
    ///
    /// Marked [Serializable] for CLR serialization compatibility, matching the
    /// original monolith pattern.
    ///
    /// Migrated from: WebVella.Erp.Api.Models.AutoNumberField (lines 22-36)
    /// </summary>
    [Serializable]
    public class AutoNumberField : Field
    {
        /// <summary>
        /// Static discriminator property identifying this as an auto-number field.
        /// Used by <see cref="Field.GetFieldType"/> for runtime type resolution
        /// and by <see cref="Field.GetFieldDefaultValue"/> to retrieve the default.
        /// Always returns <see cref="FieldType.AutoNumberField"/> (enum value 1).
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.AutoNumberField;

        /// <summary>
        /// The default numeric value for this auto-number field.
        /// Retrieved by <see cref="Field.GetFieldDefaultValue"/> when creating
        /// new records that do not provide an explicit auto-number value.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        /// <summary>
        /// Format string controlling the display representation of the auto-number.
        /// Applied via string.Format when rendering field values in API responses
        /// and frontend display (e.g., "INV-{0:0000}" → "INV-0042").
        /// </summary>
        [JsonPropertyName("displayFormat")]
        public string DisplayFormat { get; set; } = string.Empty;

        /// <summary>
        /// The initial starting value for auto-number generation.
        /// When a new entity is created with this field, the first record's
        /// auto-number value begins at this number. Nullable to allow system
        /// default behavior (start from 1).
        /// </summary>
        [JsonPropertyName("startingNumber")]
        public decimal? StartingNumber { get; set; }
    }
}
