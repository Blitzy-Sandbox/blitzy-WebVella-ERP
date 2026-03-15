using System.Net;
using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Base API response envelope model. All service responses inherit from this class
    /// to provide a standardized response structure with success/error indicators,
    /// timestamps, and error/warning collections.
    /// Migrated from WebVella.Erp.Api.Models.BaseResponseModel with Newtonsoft.Json
    /// replaced by System.Text.Json for AOT-safe serialization.
    /// </summary>
    public class BaseResponseModel
    {
        /// <summary>
        /// UTC timestamp of when the response was generated.
        /// </summary>
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Indicates whether the API operation completed successfully.
        /// </summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Human-readable message describing the result of the operation.
        /// May be null if no additional message is needed.
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        /// <summary>
        /// Optional hash value for cache validation or change detection.
        /// Initialized to null in the default constructor.
        /// </summary>
        [JsonPropertyName("hash")]
        public string? Hash { get; set; }

        /// <summary>
        /// Collection of error details when the operation fails.
        /// Initialized to an empty list in the default constructor.
        /// </summary>
        [JsonPropertyName("errors")]
        public List<ErrorModel> Errors { get; set; }

        /// <summary>
        /// Collection of access-related warnings (e.g., permission issues
        /// that did not block the operation but should be surfaced to the caller).
        /// Initialized to an empty list in the default constructor.
        /// </summary>
        [JsonPropertyName("accessWarnings")]
        public List<AccessWarningModel> AccessWarnings { get; set; }

        /// <summary>
        /// HTTP status code for the response. This property is runtime-only and
        /// is excluded from JSON serialization to avoid leaking transport-layer
        /// details into the response body.
        /// </summary>
        [JsonIgnore]
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
    /// Migrated from WebVella.Erp.Api.Models.ResponseModel.
    /// </summary>
    public class ResponseModel : BaseResponseModel
    {
        /// <summary>
        /// The response payload object. The JSON key is "object" to maintain
        /// backward compatibility with existing API consumers.
        /// </summary>
        [JsonPropertyName("object")]
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
    /// Migrated from WebVella.Erp.Api.Models.AccessWarningModel.
    /// </summary>
    public class AccessWarningModel
    {
        /// <summary>
        /// Identifies the resource or field that triggered the access warning.
        /// </summary>
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        /// <summary>
        /// Machine-readable warning code for programmatic handling.
        /// </summary>
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        /// <summary>
        /// Human-readable warning message describing the access issue.
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Model representing a validation or processing error that occurred
    /// during an API operation. Each error identifies a specific field/key,
    /// the problematic value, and a human-readable message.
    /// Migrated from WebVella.Erp.Api.Models.ErrorModel.
    /// </summary>
    public class ErrorModel
    {
        /// <summary>
        /// Identifies the field or property that caused the error.
        /// </summary>
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        /// <summary>
        /// The value that caused the validation error.
        /// </summary>
        [JsonPropertyName("value")]
        public string? Value { get; set; }

        /// <summary>
        /// Human-readable error message describing what went wrong.
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        /// <summary>
        /// Default parameterless constructor.
        /// </summary>
        public ErrorModel()
        {
        }

        /// <summary>
        /// Parameterized constructor for convenient error creation.
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
