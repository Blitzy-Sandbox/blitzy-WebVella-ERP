using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace WebVellaErp.Invoicing.Models
{
    /// <summary>
    /// Request DTO for creating a new invoice in the Invoicing bounded context.
    /// Replaces the monolith's generic <c>EntityRecord : Expando</c> pattern
    /// (<c>WebVella.Erp/Api/Models/EntityRecord.cs</c>) with compile-time safe,
    /// strongly-typed properties for financial data.
    ///
    /// <para><b>Source pattern:</b> <c>InputEntity</c> from
    /// <c>WebVella.Erp/Api/Models/Entity.cs</c> (lines 7-35) which uses
    /// nullable fields for create/update payloads.</para>
    ///
    /// <para><b>Design decisions:</b></para>
    /// <list type="bullet">
    ///   <item><description>Per AAP §0.8.1 — Full behavioral parity: all CRUD operations from
    ///   <c>RecordManager.cs</c> must have equivalent request DTOs.</description></item>
    ///   <item><description>Per AAP §0.4.3 — Strongly-typed C# models for compile-time safety
    ///   on financial data (not dynamic <c>EntityRecord</c>/<c>Expando</c>).</description></item>
    ///   <item><description>Per AAP §0.8.5 — Idempotency keys on all write endpoints.</description></item>
    ///   <item><description>Per AAP §0.5.2 — snake_case JSON convention preserved for backward
    ///   API compatibility with existing consumers.</description></item>
    /// </list>
    ///
    /// <para>
    /// Server-computed fields are intentionally excluded:
    /// <c>InvoiceNumber</c> (auto-generated), <c>SubTotal</c>, <c>TaxAmount</c>,
    /// <c>TotalAmount</c> (computed from line items), and <c>Status</c> (defaults
    /// to <see cref="InvoiceStatus.Draft"/>).
    /// </para>
    ///
    /// <para>
    /// A dual-attribute serialization pattern is applied on all properties:
    /// <c>[JsonPropertyName]</c> (primary, AOT-compatible via System.Text.Json) and
    /// <c>[JsonProperty]</c> (secondary, Newtonsoft.Json backward compat).
    /// </para>
    /// </summary>
    public class CreateInvoiceRequest
    {
        /// <summary>
        /// ID of the customer from the CRM service that this invoice is billed to.
        /// Cross-service reference by ID only — zero direct database access across
        /// service boundaries per AAP §0.8.1.
        /// Nullable to allow invoice creation with deferred customer assignment.
        /// </summary>
        [JsonPropertyName("customer_id")]
        [JsonProperty(PropertyName = "customer_id")]
        public Guid? CustomerId { get; set; }

        /// <summary>
        /// Date the invoice is formally issued to the customer.
        /// Stored and transmitted in UTC for consistent time handling across
        /// distributed services.
        /// </summary>
        [JsonPropertyName("issue_date")]
        [JsonProperty(PropertyName = "issue_date")]
        public DateTime IssueDate { get; set; }

        /// <summary>
        /// Date by which payment is due from the customer.
        /// Typically set to 30 days from issue date for standard NET-30 payment terms,
        /// but can be customized per invoice.
        /// </summary>
        [JsonPropertyName("due_date")]
        [JsonProperty(PropertyName = "due_date")]
        public DateTime DueDate { get; set; }

        /// <summary>
        /// ISO 4217 currency code for all monetary values on this invoice (e.g., "USD", "EUR", "BGN").
        /// References the <c>CurrencyType.Code</c> pattern from the monolith's
        /// <c>Definitions.cs</c> (lines 79-80). Nullable: if not specified, the system
        /// default currency applies.
        /// </summary>
        [JsonPropertyName("currency")]
        [JsonProperty(PropertyName = "currency")]
        public string? Currency { get; set; }

        /// <summary>
        /// Optional free-text notes or comments for the invoice.
        /// May contain payment instructions, terms, or customer-facing messages.
        /// </summary>
        [JsonPropertyName("notes")]
        [JsonProperty(PropertyName = "notes")]
        public string? Notes { get; set; }

        /// <summary>
        /// Collection of line items to create with the invoice.
        /// Each line item represents a billable product, service, or charge.
        /// Initialized to an empty list to ensure safe enumeration (never null).
        /// </summary>
        [JsonPropertyName("line_items")]
        [JsonProperty(PropertyName = "line_items")]
        public List<CreateLineItemRequest> LineItems { get; set; }

        /// <summary>
        /// Initializes a new <see cref="CreateInvoiceRequest"/> with safe default values.
        /// Sets <see cref="IssueDate"/> to today (UTC), <see cref="DueDate"/> to 30 days
        /// from today (standard NET-30 terms), and <see cref="LineItems"/> to an empty list.
        /// </summary>
        public CreateInvoiceRequest()
        {
            CustomerId = null;
            IssueDate = DateTime.UtcNow;
            DueDate = DateTime.UtcNow.AddDays(30);
            Currency = null;
            Notes = null;
            LineItems = new List<CreateLineItemRequest>();
        }
    }

    /// <summary>
    /// Request DTO for creating a single line item within an invoice.
    /// Represents one billable product, service, or charge with quantity,
    /// unit price, tax rate, and optional display ordering.
    ///
    /// <para><b>Design decisions:</b></para>
    /// <list type="bullet">
    ///   <item><description>All monetary and quantity fields use <c>decimal</c> type —
    ///   <b>never</b> <c>double</c> or <c>float</c> — to ensure financial precision
    ///   and avoid IEEE 754 binary floating-point rounding errors.</description></item>
    ///   <item><description><see cref="Quantity"/> is <c>decimal</c> (not <c>int</c>) to
    ///   support fractional quantities such as hours (1.5), weight (2.75 kg), or
    ///   partial units.</description></item>
    ///   <item><description><see cref="TaxRate"/> is expressed as a decimal fraction
    ///   (e.g., 0.20 for 20% tax, 0.0 for tax-exempt).</description></item>
    /// </list>
    ///
    /// <para>
    /// <c>LineTotal</c> is intentionally excluded — it is computed server-side as
    /// <c>Quantity × UnitPrice</c> to prevent client-side calculation discrepancies.
    /// </para>
    /// </summary>
    public class CreateLineItemRequest
    {
        /// <summary>
        /// Human-readable description of the product, service, or charge being billed.
        /// Required field — must not be null or empty for valid line items.
        /// </summary>
        [JsonPropertyName("description")]
        [JsonProperty(PropertyName = "description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Number of units being billed.
        /// Uses <c>decimal</c> to support fractional quantities (e.g., 1.5 hours, 2.75 kg).
        /// MUST be <c>decimal</c> — never <c>double</c> or <c>float</c> — for financial precision.
        /// </summary>
        [JsonPropertyName("quantity")]
        [JsonProperty(PropertyName = "quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// Price per single unit in the invoice's currency.
        /// Uses <c>decimal</c> for financial precision — never <c>double</c> or <c>float</c>.
        /// </summary>
        [JsonPropertyName("unit_price")]
        [JsonProperty(PropertyName = "unit_price")]
        public decimal UnitPrice { get; set; }

        /// <summary>
        /// Tax rate as a decimal fraction (e.g., 0.20 for 20% tax, 0.0 for tax-exempt).
        /// Applied to the computed <c>LineTotal</c> (Quantity × UnitPrice) to determine
        /// the tax amount for this line item.
        /// </summary>
        [JsonPropertyName("tax_rate")]
        [JsonProperty(PropertyName = "tax_rate")]
        public decimal TaxRate { get; set; }

        /// <summary>
        /// Optional display ordering position within the parent invoice.
        /// Lower values appear first. Nullable: if not specified, the service layer
        /// assigns a default order based on insertion sequence.
        /// </summary>
        [JsonPropertyName("sort_order")]
        [JsonProperty(PropertyName = "sort_order")]
        public int? SortOrder { get; set; }
    }

    /// <summary>
    /// Request DTO for updating an existing invoice.
    /// All properties are nullable to support partial updates — only the fields
    /// included in the request payload are modified; omitted fields retain their
    /// current values.
    ///
    /// <para><b>Source pattern:</b> <c>InputEntity</c> from
    /// <c>WebVella.Erp/Api/Models/Entity.cs</c> (lines 7-35) which uses
    /// nullable fields for partial update payloads.</para>
    ///
    /// <para><b>Status transitions:</b></para>
    /// <list type="bullet">
    ///   <item><description>Draft → Issued (invoice formally sent to customer)</description></item>
    ///   <item><description>Issued → Paid (full payment received)</description></item>
    ///   <item><description>Draft → Voided (cancelled before issuance)</description></item>
    ///   <item><description>Issued → Voided (cancelled after issuance)</description></item>
    /// </list>
    ///
    /// <para>
    /// Domain events are published on status transitions:
    /// <c>invoicing.invoice.issued</c>, <c>invoicing.invoice.paid</c>,
    /// <c>invoicing.invoice.voided</c> via SNS per AAP §0.7.2.
    /// </para>
    /// </summary>
    public class UpdateInvoiceRequest
    {
        /// <summary>
        /// Updated customer ID from the CRM service. Nullable for partial update —
        /// omit to retain the current customer assignment.
        /// </summary>
        [JsonPropertyName("customer_id")]
        [JsonProperty(PropertyName = "customer_id")]
        public Guid? CustomerId { get; set; }

        /// <summary>
        /// Updated lifecycle status of the invoice.
        /// Allows status transitions (Draft → Issued → Paid / Voided).
        /// Nullable for partial update — omit to retain the current status.
        /// Serialized as a string value (e.g., "Issued", "Paid") via
        /// <see cref="JsonStringEnumConverter"/> for AOT compatibility.
        /// </summary>
        [JsonPropertyName("status")]
        [JsonProperty(PropertyName = "status")]
        [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
        public InvoiceStatus? Status { get; set; }

        /// <summary>
        /// Updated issue date. Nullable for partial update — omit to retain
        /// the current issue date.
        /// </summary>
        [JsonPropertyName("issue_date")]
        [JsonProperty(PropertyName = "issue_date")]
        public DateTime? IssueDate { get; set; }

        /// <summary>
        /// Updated payment due date. Nullable for partial update — omit to retain
        /// the current due date.
        /// </summary>
        [JsonPropertyName("due_date")]
        [JsonProperty(PropertyName = "due_date")]
        public DateTime? DueDate { get; set; }

        /// <summary>
        /// Updated ISO 4217 currency code (e.g., "USD", "EUR").
        /// Nullable for partial update — omit to retain the current currency.
        /// </summary>
        [JsonPropertyName("currency")]
        [JsonProperty(PropertyName = "currency")]
        public string? Currency { get; set; }

        /// <summary>
        /// Updated free-text notes. Nullable for partial update — omit to retain
        /// the current notes. Set to empty string to clear existing notes.
        /// </summary>
        [JsonPropertyName("notes")]
        [JsonProperty(PropertyName = "notes")]
        public string? Notes { get; set; }

        /// <summary>
        /// Replacement set of line items. When provided, replaces ALL existing line items
        /// on the invoice with this new set. Nullable for partial update — omit to retain
        /// the current line items unchanged.
        /// Each <see cref="UpdateLineItemRequest"/> may include an <c>Id</c> to update
        /// an existing line item, or omit <c>Id</c> to create a new one.
        /// </summary>
        [JsonPropertyName("line_items")]
        [JsonProperty(PropertyName = "line_items")]
        public List<UpdateLineItemRequest>? LineItems { get; set; }
    }

    /// <summary>
    /// Request DTO for updating or creating a single line item within an invoice update.
    /// All properties are nullable to support partial updates.
    ///
    /// <para><b>Upsert behavior:</b></para>
    /// <list type="bullet">
    ///   <item><description>If <see cref="Id"/> is provided, the existing line item with
    ///   that ID is updated with the non-null fields.</description></item>
    ///   <item><description>If <see cref="Id"/> is null, a new line item is created
    ///   with the provided field values.</description></item>
    /// </list>
    ///
    /// <para>
    /// All monetary and quantity fields use <c>decimal</c> type — <b>never</b>
    /// <c>double</c> or <c>float</c> — for financial precision.
    /// </para>
    /// </summary>
    public class UpdateLineItemRequest
    {
        /// <summary>
        /// Identifier of the existing line item to update.
        /// If provided, updates the matching line item; if null, creates a new line item.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonProperty(PropertyName = "id")]
        public Guid? Id { get; set; }

        /// <summary>
        /// Updated description of the product, service, or charge.
        /// Nullable for partial update — omit to retain the current description.
        /// </summary>
        [JsonPropertyName("description")]
        [JsonProperty(PropertyName = "description")]
        public string? Description { get; set; }

        /// <summary>
        /// Updated quantity. Uses <c>decimal</c> to support fractional quantities.
        /// Nullable for partial update — omit to retain the current quantity.
        /// MUST be <c>decimal</c> — never <c>double</c> or <c>float</c>.
        /// </summary>
        [JsonPropertyName("quantity")]
        [JsonProperty(PropertyName = "quantity")]
        public decimal? Quantity { get; set; }

        /// <summary>
        /// Updated unit price. Uses <c>decimal</c> for financial precision.
        /// Nullable for partial update — omit to retain the current unit price.
        /// </summary>
        [JsonPropertyName("unit_price")]
        [JsonProperty(PropertyName = "unit_price")]
        public decimal? UnitPrice { get; set; }

        /// <summary>
        /// Updated tax rate as a decimal fraction (e.g., 0.20 for 20%).
        /// Nullable for partial update — omit to retain the current tax rate.
        /// </summary>
        [JsonPropertyName("tax_rate")]
        [JsonProperty(PropertyName = "tax_rate")]
        public decimal? TaxRate { get; set; }

        /// <summary>
        /// Updated display ordering position. Nullable for partial update —
        /// omit to retain the current sort order.
        /// </summary>
        [JsonPropertyName("sort_order")]
        [JsonProperty(PropertyName = "sort_order")]
        public int? SortOrder { get; set; }
    }

    /// <summary>
    /// Request DTO for recording a payment against an invoice.
    /// Represents a financial transaction that reduces the outstanding balance
    /// of the referenced invoice.
    ///
    /// <para><b>Source mapping:</b></para>
    /// <list type="bullet">
    ///   <item><description><c>WebVella.Erp/Api/RecordManager.cs</c> — record creation logic
    ///   now handled by the Invoicing service Lambda handler.</description></item>
    ///   <item><description><c>WebVella.Erp/Api/Models/EntityRecord.cs</c> — generic dynamic
    ///   record pattern replaced by strongly-typed class.</description></item>
    /// </list>
    ///
    /// <para><b>Architecture decisions:</b></para>
    /// <list type="bullet">
    ///   <item><description>Per AAP §0.8.1 — Full behavioral parity: payment processing
    ///   from <c>RecordManager.cs</c> must have equivalent request DTO.</description></item>
    ///   <item><description>Per AAP §0.8.5 — Idempotency keys on all write endpoints;
    ///   idempotency enforced at the handler level using <c>ReferenceNumber</c> as
    ///   a natural deduplication key.</description></item>
    ///   <item><description>Per AAP §0.7.2 — Post-create hooks replaced by SNS event
    ///   <c>invoicing.payment.created</c>.</description></item>
    /// </list>
    ///
    /// <para>
    /// All monetary fields use <c>decimal</c> type — <b>never</b> <c>double</c> or <c>float</c> —
    /// to ensure financial precision. Server-computed fields (<c>Id</c>, <c>CreatedBy</c>,
    /// <c>CreatedOn</c>) are excluded from the request.
    /// </para>
    /// </summary>
    public class CreatePaymentRequest
    {
        /// <summary>
        /// ID of the invoice that this payment is applied to.
        /// Must reference an existing invoice within the Invoicing bounded context.
        /// Intra-service reference only — zero cross-service database access per AAP §0.8.1.
        /// </summary>
        [JsonPropertyName("invoice_id")]
        [JsonProperty(PropertyName = "invoice_id")]
        public Guid InvoiceId { get; set; }

        /// <summary>
        /// Payment amount in the invoice's currency.
        /// Uses <c>decimal</c> type for financial precision — <b>never</b> <c>double</c> or <c>float</c>.
        /// Must be a positive value that does not exceed the invoice's outstanding balance.
        /// </summary>
        [JsonPropertyName("amount")]
        [JsonProperty(PropertyName = "amount")]
        public decimal Amount { get; set; }

        /// <summary>
        /// Date when the payment was received or processed.
        /// Stored as UTC to ensure consistent time handling across distributed services.
        /// </summary>
        [JsonPropertyName("payment_date")]
        [JsonProperty(PropertyName = "payment_date")]
        public DateTime PaymentDate { get; set; }

        /// <summary>
        /// Method used to make this payment (e.g., bank transfer, credit card, cash).
        /// Serialized as a string value (e.g., "BankTransfer", "CreditCard") via
        /// <see cref="JsonStringEnumConverter"/> for AOT-compatible JSON serialization
        /// and API Gateway request validation.
        /// </summary>
        [JsonPropertyName("payment_method")]
        [JsonProperty(PropertyName = "payment_method")]
        [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
        public PaymentMethod PaymentMethod { get; set; }

        /// <summary>
        /// External payment reference identifier such as a check number, wire transfer
        /// reference, credit card transaction ID, or other tracking number from the
        /// payment processor. Nullable when not applicable.
        /// Can serve as a natural deduplication key for idempotent payment recording
        /// per AAP §0.8.5.
        /// </summary>
        [JsonPropertyName("reference_number")]
        [JsonProperty(PropertyName = "reference_number")]
        public string? ReferenceNumber { get; set; }

        /// <summary>
        /// Optional free-text notes associated with this payment (e.g., memo, comments,
        /// or additional context from the payer).
        /// </summary>
        [JsonPropertyName("notes")]
        [JsonProperty(PropertyName = "notes")]
        public string? Notes { get; set; }

        /// <summary>
        /// Initializes a new <see cref="CreatePaymentRequest"/> with safe default values.
        /// Sets <see cref="PaymentDate"/> to today (UTC) and <see cref="PaymentMethod"/>
        /// to <see cref="Models.PaymentMethod.BankTransfer"/>.
        /// </summary>
        public CreatePaymentRequest()
        {
            InvoiceId = Guid.Empty;
            Amount = 0m;
            PaymentDate = DateTime.UtcNow;
            PaymentMethod = PaymentMethod.BankTransfer;
            ReferenceNumber = null;
            Notes = null;
        }
    }
}
