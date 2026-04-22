using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace WebVellaErp.Invoicing.Models
{
    /// <summary>
    /// Strongly-typed API response wrapper for a single <see cref="Invoice"/> entity.
    /// Inherits the standard response envelope from <see cref="BaseResponseModel"/> and
    /// adds a typed <see cref="Object"/> property containing the invoice payload.
    ///
    /// <para><b>Source mapping:</b></para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>WebVella.Erp/Api/Models/Entity.cs</c> — <c>EntityResponse : BaseResponseModel</c>
    ///     (lines 95-99) providing the structural pattern for single-entity response wrappers.
    ///   </description></item>
    ///   <item><description>
    ///     <c>WebVella.Erp/Api/Models/FSResponse.cs</c> — constructor pattern setting
    ///     <c>Timestamp = DateTime.UtcNow</c> and <c>Success = true</c> (lines 14-18).
    ///   </description></item>
    ///   <item><description>
    ///     <c>WebVella.Erp/Api/Models/BaseModels.cs</c> — <c>ResponseModel</c> (line 42)
    ///     establishing the <c>"object"</c> JSON key convention for payload properties.
    ///   </description></item>
    /// </list>
    ///
    /// <para><b>Architecture decisions:</b></para>
    /// <list type="bullet">
    ///   <item><description>Per AAP §0.8.1 — Full behavioral parity: response envelope matches original BaseResponseModel pattern.</description></item>
    ///   <item><description>Per AAP §0.8.9 — Backward-compatible endpoints: JSON key <c>"object"</c> preserved exactly.</description></item>
    ///   <item><description>Per AAP §0.8.2 — Native AOT compatibility: <c>System.Text.Json</c> is primary serializer.</description></item>
    ///   <item><description>Per AAP §0.5.2 — Dual-attribute pattern for backward compatibility with Newtonsoft.Json.</description></item>
    ///   <item><description>Per AAP §0.4.3 — Consumed by React SPA frontend via API Gateway v2.</description></item>
    /// </list>
    /// </summary>
    public class InvoiceResponse : BaseResponseModel
    {
        /// <summary>
        /// The invoice payload object. Contains the full <see cref="Invoice"/> entity
        /// with all financial data fields, line items, currency info, and audit metadata.
        ///
        /// <para>
        /// The JSON key <c>"object"</c> is the established convention from the original
        /// WebVella ERP monolith (<c>ResponseModel.Object</c> at BaseModels.cs line 42)
        /// and MUST be preserved for backward API compatibility.
        /// </para>
        /// </summary>
        [JsonPropertyName("object")]
        [JsonProperty(PropertyName = "object")]
        public Invoice? Object { get; set; }

        /// <summary>
        /// Default constructor initializing the response envelope with success defaults.
        /// Calls <see cref="BaseResponseModel()"/> to initialize Errors, AccessWarnings,
        /// Hash, and StatusCode, then sets Timestamp and Success following the
        /// <c>FSResponse</c> constructor pattern (FSResponse.cs lines 14-18).
        /// </summary>
        public InvoiceResponse() : base()
        {
            Timestamp = DateTime.UtcNow;
            Success = true;
        }

        /// <summary>
        /// Convenience constructor that wraps an existing <see cref="Invoice"/> instance
        /// in the standard response envelope with success defaults.
        /// </summary>
        /// <param name="invoice">The invoice entity to include as the response payload.</param>
        public InvoiceResponse(Invoice invoice) : this()
        {
            Object = invoice;
        }
    }

    /// <summary>
    /// Strongly-typed API response wrapper for a paginated list of <see cref="Invoice"/> entities.
    /// Inherits the standard response envelope from <see cref="BaseResponseModel"/> and adds
    /// a typed list payload with pagination metadata.
    ///
    /// <para><b>Source mapping:</b></para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>WebVella.Erp/Api/Models/Entity.cs</c> — <c>EntityListResponse : BaseResponseModel</c>
    ///     (lines 101-105) providing the structural pattern for list response wrappers.
    ///   </description></item>
    ///   <item><description>
    ///     <c>WebVella.Erp/Api/Models/FSResponse.cs</c> — constructor pattern (lines 14-18).
    ///   </description></item>
    /// </list>
    ///
    /// <para><b>Pagination:</b> The <see cref="TotalCount"/> property supports server-side
    /// pagination by communicating the total number of matching records, enabling the React
    /// SPA frontend to calculate page counts and render pagination controls.</para>
    /// </summary>
    public class InvoiceListResponse : BaseResponseModel
    {
        /// <summary>
        /// List of invoice entities in this page of results.
        /// Initialized to an empty list (never null) for safe enumeration.
        ///
        /// <para>
        /// The JSON key <c>"object"</c> is preserved from the monolith's
        /// <c>EntityListResponse.Object</c> pattern for backward API compatibility.
        /// </para>
        /// </summary>
        [JsonPropertyName("object")]
        [JsonProperty(PropertyName = "object")]
        public List<Invoice> Object { get; set; }

        /// <summary>
        /// Total count of invoice records matching the query criteria, irrespective
        /// of pagination. Used by the frontend to calculate total page count as
        /// <c>Math.ceil(TotalCount / PageSize)</c>.
        ///
        /// <para>
        /// The JSON key <c>"total_count"</c> uses snake_case for REST API consistency
        /// with the invoicing service's naming convention.
        /// </para>
        /// </summary>
        [JsonPropertyName("total_count")]
        [JsonProperty(PropertyName = "total_count")]
        public int TotalCount { get; set; }

        /// <summary>
        /// Default constructor initializing the response envelope with success defaults
        /// and an empty invoice list. Calls <see cref="BaseResponseModel()"/> to initialize
        /// inherited properties, then sets Timestamp, Success, and empty Object list.
        /// </summary>
        public InvoiceListResponse() : base()
        {
            Object = new List<Invoice>();
            Timestamp = DateTime.UtcNow;
            Success = true;
        }

        /// <summary>
        /// Convenience constructor that wraps an existing list of invoices with total count
        /// in the standard response envelope with success defaults.
        /// </summary>
        /// <param name="invoices">The list of invoice entities for this page of results.</param>
        /// <param name="totalCount">Total number of matching records across all pages.</param>
        public InvoiceListResponse(List<Invoice> invoices, int totalCount) : this()
        {
            Object = invoices ?? new List<Invoice>();
            TotalCount = totalCount;
        }
    }

    /// <summary>
    /// Strongly-typed API response wrapper for a single <see cref="Payment"/> entity.
    /// Inherits the standard response envelope from <see cref="BaseResponseModel"/> and
    /// adds a typed <see cref="Object"/> property containing the payment payload.
    ///
    /// <para><b>Source mapping:</b></para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>WebVella.Erp/Api/Models/Entity.cs</c> — <c>EntityResponse</c> pattern.
    ///   </description></item>
    ///   <item><description>
    ///     <c>WebVella.Erp/Api/Models/FSResponse.cs</c> — constructor pattern (lines 14-18).
    ///   </description></item>
    /// </list>
    ///
    /// <para><b>Architecture:</b> Payment responses carry financial data with decimal precision.
    /// All monetary fields in the <see cref="Payment"/> payload use <c>decimal</c> type to
    /// prevent floating-point rounding errors.</para>
    /// </summary>
    public class PaymentResponse : BaseResponseModel
    {
        /// <summary>
        /// The payment payload object. Contains the full <see cref="Payment"/> entity
        /// with amount, payment method, reference number, and audit metadata.
        ///
        /// <para>
        /// The JSON key <c>"object"</c> is preserved for backward API compatibility.
        /// </para>
        /// </summary>
        [JsonPropertyName("object")]
        [JsonProperty(PropertyName = "object")]
        public Payment? Object { get; set; }

        /// <summary>
        /// Default constructor initializing the response envelope with success defaults.
        /// Follows the <c>FSResponse</c> constructor pattern (FSResponse.cs lines 14-18).
        /// </summary>
        public PaymentResponse() : base()
        {
            Timestamp = DateTime.UtcNow;
            Success = true;
        }

        /// <summary>
        /// Convenience constructor that wraps an existing <see cref="Payment"/> instance
        /// in the standard response envelope with success defaults.
        /// </summary>
        /// <param name="payment">The payment entity to include as the response payload.</param>
        public PaymentResponse(Payment payment) : this()
        {
            Object = payment;
        }
    }

    /// <summary>
    /// Strongly-typed API response wrapper for a paginated list of <see cref="Payment"/> entities.
    /// Inherits the standard response envelope from <see cref="BaseResponseModel"/> and adds
    /// a typed list payload with pagination metadata.
    ///
    /// <para><b>Source mapping:</b></para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>WebVella.Erp/Api/Models/Entity.cs</c> — <c>EntityListResponse</c> pattern (lines 101-105).
    ///   </description></item>
    ///   <item><description>
    ///     <c>WebVella.Erp/Api/Models/FSResponse.cs</c> — constructor pattern (lines 14-18).
    ///   </description></item>
    /// </list>
    ///
    /// <para><b>Pagination:</b> The <see cref="TotalCount"/> property supports server-side
    /// pagination for payment history views in the React SPA frontend.</para>
    /// </summary>
    public class PaymentListResponse : BaseResponseModel
    {
        /// <summary>
        /// List of payment entities in this page of results.
        /// Initialized to an empty list (never null) for safe enumeration.
        ///
        /// <para>
        /// The JSON key <c>"object"</c> is preserved for backward API compatibility.
        /// </para>
        /// </summary>
        [JsonPropertyName("object")]
        [JsonProperty(PropertyName = "object")]
        public List<Payment> Object { get; set; }

        /// <summary>
        /// Total count of payment records matching the query criteria, irrespective
        /// of pagination. Used by the frontend to calculate total page count.
        /// </summary>
        [JsonPropertyName("total_count")]
        [JsonProperty(PropertyName = "total_count")]
        public int TotalCount { get; set; }

        /// <summary>
        /// Default constructor initializing the response envelope with success defaults
        /// and an empty payment list.
        /// </summary>
        public PaymentListResponse() : base()
        {
            Object = new List<Payment>();
            Timestamp = DateTime.UtcNow;
            Success = true;
        }

        /// <summary>
        /// Convenience constructor that wraps an existing list of payments with total count
        /// in the standard response envelope with success defaults.
        /// </summary>
        /// <param name="payments">The list of payment entities for this page of results.</param>
        /// <param name="totalCount">Total number of matching records across all pages.</param>
        public PaymentListResponse(List<Payment> payments, int totalCount) : this()
        {
            Object = payments ?? new List<Payment>();
            TotalCount = totalCount;
        }
    }

    /// <summary>
    /// Strongly-typed API response wrapper for a list of <see cref="LineItem"/> entities.
    /// Inherits the standard response envelope from <see cref="BaseResponseModel"/> and
    /// carries a list of line items for batch operation responses.
    ///
    /// <para><b>Purpose:</b> Used for API responses that return line items independently
    /// of their parent invoice, such as batch line item creation, bulk update results,
    /// or line item query endpoints.</para>
    ///
    /// <para><b>Source mapping:</b></para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>WebVella.Erp/Api/Models/Entity.cs</c> — <c>EntityLibraryItemsResponse</c>
    ///     (lines 107-111) providing the pattern for list-of-items response wrappers.
    ///   </description></item>
    ///   <item><description>
    ///     <c>WebVella.Erp/Api/Models/FSResponse.cs</c> — constructor pattern (lines 14-18).
    ///   </description></item>
    /// </list>
    /// </summary>
    public class LineItemListResponse : BaseResponseModel
    {
        /// <summary>
        /// List of line item entities in the response.
        /// Initialized to an empty list (never null) for safe enumeration.
        /// Each <see cref="LineItem"/> carries description, quantity, unit price,
        /// tax rate, and computed line total with decimal precision.
        ///
        /// <para>
        /// The JSON key <c>"object"</c> is preserved for backward API compatibility.
        /// </para>
        /// </summary>
        [JsonPropertyName("object")]
        [JsonProperty(PropertyName = "object")]
        public List<LineItem> Object { get; set; }

        /// <summary>
        /// Default constructor initializing the response envelope with success defaults
        /// and an empty line item list. Calls <see cref="BaseResponseModel()"/> to
        /// initialize inherited properties.
        /// </summary>
        public LineItemListResponse() : base()
        {
            Object = new List<LineItem>();
            Timestamp = DateTime.UtcNow;
            Success = true;
        }

        /// <summary>
        /// Convenience constructor that wraps an existing list of line items
        /// in the standard response envelope with success defaults.
        /// </summary>
        /// <param name="lineItems">The list of line item entities for the response payload.</param>
        public LineItemListResponse(List<LineItem> lineItems) : this()
        {
            Object = lineItems ?? new List<LineItem>();
        }
    }
}
