using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Inventory.Models
{
    /// <summary>
    /// Standard API response wrapper model for the Inventory (Project Management) microservice
    /// Lambda handlers. Provides a consistent envelope for all API responses with timestamp,
    /// success indicator, message, and payload.
    ///
    /// This is a simplified, AOT-compatible replacement for the monolith's
    /// <c>WebVella.Erp.Api.Models.BaseResponseModel</c> (Timestamp, Success, Message, Hash,
    /// Errors, AccessWarnings, StatusCode) and <c>ResponseModel</c> (adds Object property).
    ///
    /// Simplifications from monolith:
    ///   - Hash, Errors, AccessWarnings, StatusCode are omitted — those cross-cutting
    ///     concerns are handled at the API Gateway level and shared infrastructure layer.
    ///   - Uses <see cref="JsonPropertyName"/> from System.Text.Json for .NET 9 Native AOT
    ///     compatibility, replacing Newtonsoft.Json <c>[JsonProperty]</c> attributes.
    ///   - JSON property names are backward-compatible with the monolith's serialization
    ///     contract: "timestamp", "success", "message", "object".
    ///
    /// Source reference: WebVella.Erp/Api/Models/BaseModels.cs (lines 8–48)
    /// </summary>
    public class ResponseModel
    {
        /// <summary>
        /// UTC timestamp indicating when the response was generated.
        /// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless constructor,
        /// preserving the monolith's <c>BaseResponseModel.Timestamp</c> pattern.
        /// Serialized as ISO 8601 string in JSON output.
        /// </summary>
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Indicates whether the requested operation completed successfully.
        /// Defaults to <c>false</c> in the parameterless constructor — callers must
        /// explicitly set to <c>true</c> on success, ensuring fail-safe behavior.
        /// </summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Human-readable message describing the operation result.
        /// Used for both success confirmations and error descriptions.
        /// May be <c>null</c> when no additional context is needed.
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        /// <summary>
        /// Response payload containing the operation result data.
        /// This is a dynamic object that can hold any serializable type — entity records,
        /// collections, status objects, etc. Corresponds to the monolith's
        /// <c>ResponseModel.Object</c> property with <c>[JsonProperty(PropertyName = "object")]</c>.
        /// May be <c>null</c> when the response carries no payload (e.g., delete confirmations).
        /// </summary>
        [JsonPropertyName("object")]
        public object? Object { get; set; }

        /// <summary>
        /// Initializes a new <see cref="ResponseModel"/> with default values.
        /// <c>Success</c> defaults to <c>false</c> (fail-safe) and <c>Timestamp</c>
        /// is set to the current UTC time. Callers are expected to set <c>Success = true</c>
        /// and populate <c>Object</c> and <c>Message</c> as appropriate after construction.
        /// </summary>
        public ResponseModel()
        {
            Timestamp = DateTime.UtcNow;
            Success = false;
            Message = null;
            Object = null;
        }
    }
}
