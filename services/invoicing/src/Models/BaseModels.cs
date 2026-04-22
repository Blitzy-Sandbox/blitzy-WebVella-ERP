using System.Net;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace WebVellaErp.Invoicing.Models
{
    /// <summary>
    /// Base API response envelope model for the Invoicing service.
    /// All invoicing-specific response models (InvoiceResponse, PaymentResponse, etc.)
    /// inherit from this class to provide a standardized response structure with
    /// success/error indicators, timestamps, and error/warning collections.
    ///
    /// Migrated from WebVella.Erp.Api.Models.BaseResponseModel with dual-attribute
    /// serialization: System.Text.Json (PRIMARY, AOT-safe) and Newtonsoft.Json
    /// (SECONDARY, backward-compatible). JSON property names are preserved exactly
    /// from the original monolith for API backward compatibility.
    /// </summary>
    public class BaseResponseModel
    {
        /// <summary>
        /// UTC timestamp of when the response was generated.
        /// </summary>
        [JsonPropertyName("timestamp")]
        [JsonProperty(PropertyName = "timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Indicates whether the API operation completed successfully.
        /// </summary>
        [JsonPropertyName("success")]
        [JsonProperty(PropertyName = "success")]
        public bool Success { get; set; }

        /// <summary>
        /// Human-readable message describing the result of the operation.
        /// May be null if no additional message is needed.
        /// </summary>
        [JsonPropertyName("message")]
        [JsonProperty(PropertyName = "message")]
        public string? Message { get; set; }

        /// <summary>
        /// Optional hash value for cache validation or change detection.
        /// Initialized to null in the default constructor.
        /// </summary>
        [JsonPropertyName("hash")]
        [JsonProperty(PropertyName = "hash")]
        public string? Hash { get; set; }

        /// <summary>
        /// Collection of error details when the operation fails.
        /// Initialized to an empty list in the default constructor.
        /// </summary>
        [JsonPropertyName("errors")]
        [JsonProperty(PropertyName = "errors")]
        public List<ErrorModel> Errors { get; set; }

        /// <summary>
        /// Collection of access-related warnings (e.g., permission issues
        /// that did not block the operation but should be surfaced to the caller).
        /// Initialized to an empty list in the default constructor.
        /// </summary>
        [JsonPropertyName("accessWarnings")]
        [JsonProperty(PropertyName = "accessWarnings")]
        public List<AccessWarningModel> AccessWarnings { get; set; }

        /// <summary>
        /// HTTP status code for the response. This property is runtime-only and
        /// is excluded from JSON serialization via [JsonIgnore] from BOTH
        /// System.Text.Json and Newtonsoft.Json to avoid leaking transport-layer
        /// details into the response body regardless of which serializer is used.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [Newtonsoft.Json.JsonIgnore]
        public HttpStatusCode StatusCode { get; set; }

        /// <summary>
        /// Default constructor initializing all collection properties and defaults.
        /// Sets Hash to null, Errors and AccessWarnings to empty lists,
        /// and StatusCode to 200 OK.
        /// </summary>
        public BaseResponseModel()
        {
            Hash = null;
            Errors = new List<ErrorModel>();
            AccessWarnings = new List<AccessWarningModel>();
            StatusCode = HttpStatusCode.OK;
        }
    }

    /// <summary>
    /// Generic API response model that wraps a single result object.
    /// Inherits the standard response envelope from <see cref="BaseResponseModel"/>
    /// and adds an Object property for the response payload.
    ///
    /// Migrated from WebVella.Erp.Api.Models.ResponseModel. The JSON key "object"
    /// is the established convention from the original monolith and MUST be preserved
    /// for backward API compatibility.
    /// </summary>
    public class ResponseModel : BaseResponseModel
    {
        /// <summary>
        /// The response payload object. The JSON key is "object" to maintain
        /// backward compatibility with existing API consumers. This is the
        /// established convention from the original WebVella ERP monolith.
        /// </summary>
        [JsonPropertyName("object")]
        [JsonProperty(PropertyName = "object")]
        public object? Object { get; set; }

        /// <summary>
        /// Default constructor that calls the base constructor to initialize
        /// all inherited properties with their default values.
        /// </summary>
        public ResponseModel() : base()
        {
        }
    }

    /// <summary>
    /// Model representing an access warning that occurred during an API operation.
    /// Access warnings indicate permission-related issues that did not prevent
    /// the operation from completing but should be communicated to the caller.
    ///
    /// Migrated from WebVella.Erp.Api.Models.AccessWarningModel.
    /// </summary>
    public class AccessWarningModel
    {
        /// <summary>
        /// Identifies the resource or field that triggered the access warning.
        /// </summary>
        [JsonPropertyName("key")]
        [JsonProperty(PropertyName = "key")]
        public string? Key { get; set; }

        /// <summary>
        /// Machine-readable warning code for programmatic handling.
        /// </summary>
        [JsonPropertyName("code")]
        [JsonProperty(PropertyName = "code")]
        public string? Code { get; set; }

        /// <summary>
        /// Human-readable warning message describing the access issue.
        /// </summary>
        [JsonPropertyName("message")]
        [JsonProperty(PropertyName = "message")]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Model representing a validation or processing error that occurred
    /// during an API operation. Each error identifies a specific field/key,
    /// the problematic value, and a human-readable message.
    ///
    /// Migrated from WebVella.Erp.Api.Models.ErrorModel. Preserves both
    /// the parameterless constructor (for deserialization) and the parameterized
    /// constructor (for convenient error creation in service code).
    /// </summary>
    public class ErrorModel
    {
        /// <summary>
        /// Identifies the field or property that caused the error.
        /// </summary>
        [JsonPropertyName("key")]
        [JsonProperty(PropertyName = "key")]
        public string? Key { get; set; }

        /// <summary>
        /// The value that caused the validation error.
        /// </summary>
        [JsonPropertyName("value")]
        [JsonProperty(PropertyName = "value")]
        public string? Value { get; set; }

        /// <summary>
        /// Human-readable error message describing what went wrong.
        /// </summary>
        [JsonPropertyName("message")]
        [JsonProperty(PropertyName = "message")]
        public string? Message { get; set; }

        /// <summary>
        /// Default parameterless constructor required for JSON deserialization
        /// by both System.Text.Json and Newtonsoft.Json serializers.
        /// </summary>
        public ErrorModel()
        {
        }

        /// <summary>
        /// Parameterized constructor for convenient error creation in service code.
        /// </summary>
        /// <param name="key">The field or property that caused the error.</param>
        /// <param name="value">The value that caused the validation error.</param>
        /// <param name="message">Human-readable error description.</param>
        public ErrorModel(string key, string value, string message)
        {
            Key = key;
            Value = value;
            Message = message;
        }
    }
}
