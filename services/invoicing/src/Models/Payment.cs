using System;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace WebVellaErp.Invoicing.Models
{
    /// <summary>
    /// Specifies the method used to make a payment against an invoice.
    /// Replaces the monolith's generic select-field approach (where payment methods
    /// were stored as string values in a dynamic <c>EntityRecord</c> dictionary).
    ///
    /// <para>
    /// Decorated with <c>[JsonConverter(typeof(JsonStringEnumConverter))]</c> so that
    /// enum values are serialized as human-readable strings (e.g., <c>"BankTransfer"</c>)
    /// rather than numeric ordinals, ensuring API Gateway request/response clarity and
    /// AOT-compatible serialization for .NET 9 Native AOT Lambda functions.
    /// </para>
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter<PaymentMethod>))]
    public enum PaymentMethod
    {
        /// <summary>Wire transfer or ACH payment.</summary>
        BankTransfer = 0,

        /// <summary>Credit card payment.</summary>
        CreditCard = 1,

        /// <summary>Debit card payment.</summary>
        DebitCard = 2,

        /// <summary>Cash payment.</summary>
        Cash = 3,

        /// <summary>Check (cheque) payment.</summary>
        Check = 4,

        /// <summary>Other payment methods not covered above.</summary>
        Other = 5
    }

    /// <summary>
    /// Represents a payment recorded against an invoice in the Invoicing bounded context.
    /// This is a strongly-typed domain model replacing the monolith's generic
    /// <c>EntityRecord : Expando</c> pattern (<c>WebVella.Erp/Api/Models/EntityRecord.cs</c>)
    /// with compile-time safe properties designed for financial payment data.
    ///
    /// <para><b>Source mapping:</b></para>
    /// <list type="bullet">
    ///   <item><description><c>WebVella.Erp/Api/Models/EntityRecord.cs</c> — generic dynamic record pattern replaced by strongly-typed class</description></item>
    ///   <item><description><c>WebVella.Erp/Api/RecordManager.cs</c> — record CRUD processing logic now in Invoicing service Lambda handlers</description></item>
    ///   <item><description><c>WebVella.Erp/Api/Definitions.cs</c> — enum and model patterns</description></item>
    ///   <item><description><c>WebVella.Erp/Api/Models/BaseModels.cs</c> — dual-attribute JSON serialization pattern</description></item>
    /// </list>
    ///
    /// <para><b>Architecture decisions:</b></para>
    /// <list type="bullet">
    ///   <item><description>Per AAP §0.8.1 — Full behavioral parity: payment processing from <c>RecordManager.cs</c> must have equivalent model</description></item>
    ///   <item><description>Per AAP §0.4.2 — Self-contained bounded context: Payment belongs exclusively to the Invoicing service</description></item>
    ///   <item><description>Per AAP §0.4.3 — Strongly-typed C# model for compile-time safety on financial data</description></item>
    ///   <item><description>Per AAP §0.8.5 — Event naming: <c>invoicing.payment.created</c>, <c>invoicing.payment.processed</c></description></item>
    ///   <item><description>Per AAP §0.7.2 — Post-create hooks replaced by SNS event <c>invoicing.payment.created</c></description></item>
    /// </list>
    ///
    /// <para>
    /// A dual-attribute serialization pattern is applied on all properties:
    /// <c>System.Text.Json.Serialization.JsonPropertyName</c> (primary, AOT-compatible) and
    /// <c>Newtonsoft.Json.JsonProperty</c> (backward compatibility). All JSON property names
    /// use <b>snake_case</b> convention for backward API compatibility.
    /// </para>
    ///
    /// <para>
    /// All monetary fields use <c>decimal</c> type — <b>never</b> <c>double</c> or <c>float</c> —
    /// to ensure financial precision and avoid floating-point rounding errors.
    /// </para>
    /// </summary>
    public class Payment
    {
        /// <summary>
        /// Unique identifier for this payment record.
        /// Follows the standard <c>EntityRecord</c> pattern where every record has
        /// a <c>Guid</c> primary key field named <c>"id"</c>.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        [JsonProperty(PropertyName = "id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Reference to the parent invoice that this payment is applied to.
        /// Cross-references the <c>Invoice.Id</c> within the same Invoicing bounded context.
        /// Per AAP §0.8.1, zero cross-service database access — this is an intra-service reference only.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("invoice_id")]
        [JsonProperty(PropertyName = "invoice_id")]
        public Guid InvoiceId { get; set; }

        /// <summary>
        /// Payment amount in the invoice's currency.
        /// Uses <c>decimal</c> type for financial precision — <b>never</b> <c>double</c> or <c>float</c>.
        /// Corresponds to the monetary value that reduces the invoice's outstanding balance.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("amount")]
        [JsonProperty(PropertyName = "amount")]
        public decimal Amount { get; set; }

        /// <summary>
        /// Date when the payment was received or processed.
        /// Stored as UTC to ensure consistent time handling across distributed services.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("payment_date")]
        [JsonProperty(PropertyName = "payment_date")]
        public DateTime PaymentDate { get; set; }

        /// <summary>
        /// Method used to make this payment (e.g., bank transfer, credit card, cash).
        /// Replaces the monolith's generic select-field approach where payment methods
        /// were stored as string values in an <c>EntityRecord</c> dictionary.
        /// Serialized as a string value via <see cref="JsonStringEnumConverter"/>.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("payment_method")]
        [JsonProperty(PropertyName = "payment_method")]
        public PaymentMethod PaymentMethod { get; set; }

        /// <summary>
        /// External payment reference identifier such as a check number, wire transfer
        /// reference, credit card transaction ID, or other tracking number from the
        /// payment processor. Defaults to <see cref="string.Empty"/> when not applicable.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("reference_number")]
        [JsonProperty(PropertyName = "reference_number")]
        public string ReferenceNumber { get; set; }

        /// <summary>
        /// Optional free-text notes associated with this payment (e.g., memo, comments,
        /// or additional context). Defaults to <see cref="string.Empty"/>.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("notes")]
        [JsonProperty(PropertyName = "notes")]
        public string Notes { get; set; }

        /// <summary>
        /// Identifier of the user who created this payment record.
        /// Replaces the monolith's <c>SecurityContext.CurrentUser.Id</c> pattern —
        /// the user ID is now extracted from the JWT claims in the Lambda event context
        /// and explicitly set on the model.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("created_by")]
        [JsonProperty(PropertyName = "created_by")]
        public Guid CreatedBy { get; set; }

        /// <summary>
        /// UTC timestamp when this payment record was created.
        /// Automatically set to <c>DateTime.UtcNow</c> by the default constructor and
        /// overridden by the service layer during record persistence.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("created_on")]
        [JsonProperty(PropertyName = "created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// Initializes a new <see cref="Payment"/> instance with safe default values.
        /// Follows the pattern from <c>ErpUser</c> constructor in
        /// <c>WebVella.Erp/Api/Models/ErpUser.cs</c> which initializes all fields
        /// to non-null, type-safe defaults.
        /// </summary>
        public Payment()
        {
            Id = Guid.Empty;
            InvoiceId = Guid.Empty;
            Amount = 0m;
            PaymentDate = DateTime.UtcNow;
            PaymentMethod = PaymentMethod.BankTransfer;
            ReferenceNumber = string.Empty;
            Notes = string.Empty;
            CreatedBy = Guid.Empty;
            CreatedOn = DateTime.UtcNow;
        }
    }
}
