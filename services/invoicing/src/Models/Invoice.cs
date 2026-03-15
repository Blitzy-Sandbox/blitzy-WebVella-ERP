using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace WebVellaErp.Invoicing.Models
{
    /// <summary>
    /// Represents the lifecycle status of an invoice.
    /// Replaces the monolith's generic EntityRecord select-field status tracking
    /// with a compile-time safe enumeration for financial document workflow.
    /// </summary>
    /// <remarks>
    /// Values are serialized as strings (e.g., "Draft", "Issued") via
    /// <see cref="JsonStringEnumConverter"/> for AOT-compatible JSON output.
    /// Integer backing values preserved for database storage and ordering.
    /// </remarks>
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter<InvoiceStatus>))]
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum InvoiceStatus
    {
        /// <summary>Invoice created but not yet issued to the customer.</summary>
        Draft = 0,

        /// <summary>Invoice formally issued and sent to the customer.</summary>
        Issued = 1,

        /// <summary>Invoice fully paid by the customer.</summary>
        Paid = 2,

        /// <summary>Invoice cancelled or voided; no longer collectible.</summary>
        Voided = 3
    }

    /// <summary>
    /// Specifies whether the currency symbol appears before or after the amount.
    /// Derived from <c>WebVella.Erp.Api.CurrencySymbolPlacement</c> (Definitions.cs lines 58-62).
    /// </summary>
    /// <remarks>
    /// Backing integer values (Before=1, After=2) are preserved from the source monolith
    /// to ensure backward compatibility with existing serialized data and API contracts.
    /// </remarks>
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter<CurrencySymbolPlacement>))]
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum CurrencySymbolPlacement
    {
        /// <summary>Symbol is placed before the numeric amount (e.g., "$100.00").</summary>
        Before = 1,

        /// <summary>Symbol is placed after the numeric amount (e.g., "100.00€").</summary>
        After = 2
    }

    /// <summary>
    /// Encapsulates currency formatting metadata for invoice monetary values.
    /// Simplified from the monolith's <c>CurrencyType</c> (Definitions.cs lines 64-90) and
    /// <c>Currency</c> model (Currency.cs, 15 properties) to retain only the fields essential
    /// for invoice display and calculation.
    /// </summary>
    /// <remarks>
    /// JSON property names use camelCase to match the original <c>CurrencyType</c> serialization
    /// contract (e.g., "symbolNative", "decimalDigits") for backward API compatibility.
    /// Dual-attribute pattern: <see cref="JsonPropertyNameAttribute"/> (primary, AOT-compatible)
    /// and <see cref="JsonPropertyAttribute"/> (secondary, Newtonsoft.Json backward compat).
    /// </remarks>
    public class CurrencyInfo
    {
        /// <summary>
        /// ISO 4217 currency code (e.g., "USD", "EUR", "BGN").
        /// Source: Definitions.cs CurrencyType.Code (lines 79-80).
        /// </summary>
        [JsonPropertyName("code")]
        [JsonProperty(PropertyName = "code")]
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// Standard currency symbol used in display (e.g., "$", "€", "£").
        /// Source: Definitions.cs CurrencyType.Symbol (lines 67-68).
        /// </summary>
        [JsonPropertyName("symbol")]
        [JsonProperty(PropertyName = "symbol")]
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Native (locale-specific) currency symbol (e.g., "US$" vs "$").
        /// Source: Definitions.cs CurrencyType.SymbolNative (lines 70-71).
        /// </summary>
        [JsonPropertyName("symbolNative")]
        [JsonProperty(PropertyName = "symbolNative")]
        public string SymbolNative { get; set; } = string.Empty;

        /// <summary>
        /// Full human-readable currency name (e.g., "US Dollar", "Euro").
        /// Source: Definitions.cs CurrencyType.Name (lines 73-74).
        /// </summary>
        [JsonPropertyName("name")]
        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Number of decimal digits for monetary precision (e.g., 2 for USD, 0 for JPY).
        /// Source: Definitions.cs CurrencyType.DecimalDigits (lines 82-83).
        /// </summary>
        [JsonPropertyName("decimalDigits")]
        [JsonProperty(PropertyName = "decimalDigits")]
        public int DecimalDigits { get; set; } = 2;

        /// <summary>
        /// Rounding precision for monetary calculations.
        /// A value of 0 indicates no special rounding beyond decimal digits.
        /// Source: Definitions.cs CurrencyType.Rounding (lines 85-86).
        /// </summary>
        [JsonPropertyName("rounding")]
        [JsonProperty(PropertyName = "rounding")]
        public int Rounding { get; set; } = 0;

        /// <summary>
        /// Whether the currency symbol is displayed before or after the amount.
        /// Defaults to <see cref="CurrencySymbolPlacement.Before"/> (e.g., "$100.00").
        /// Source: Definitions.cs CurrencyType.SymbolPlacement (lines 88-89).
        /// </summary>
        [JsonPropertyName("symbolPlacement")]
        [JsonProperty(PropertyName = "symbolPlacement")]
        public CurrencySymbolPlacement SymbolPlacement { get; set; } = CurrencySymbolPlacement.Before;
    }

    /// <summary>
    /// Represents a financial invoice document in the Invoicing bounded context.
    /// Replaces the monolith's dynamic <c>EntityRecord : Expando</c> pattern with a
    /// compile-time safe, strongly-typed model for ACID-critical financial data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All monetary fields (<see cref="SubTotal"/>, <see cref="TaxAmount"/>,
    /// <see cref="TotalAmount"/>) use <c>decimal</c> type to prevent floating-point
    /// precision errors in financial calculations (IEEE 754 binary floats are unsuitable).
    /// </para>
    /// <para>
    /// JSON property names use snake_case convention for REST API backward compatibility
    /// with existing consumers. Dual-attribute serialization supports both System.Text.Json
    /// (Native AOT, primary) and Newtonsoft.Json (backward compat, secondary).
    /// </para>
    /// <para>
    /// Invoice data is persisted in RDS PostgreSQL with ACID transactions, making this
    /// one of only two services (with Reporting) using relational storage. All other
    /// services use DynamoDB. See AAP §0.4.2 and §0.7.4.
    /// </para>
    /// <para>
    /// Cross-service references (e.g., <see cref="CustomerId"/>) are stored by ID only —
    /// zero direct database access across service boundaries per AAP §0.8.1.
    /// </para>
    /// </remarks>
    public class Invoice
    {
        /// <summary>
        /// Unique identifier for the invoice (primary key).
        /// Follows the standard EntityRecord pattern where every record has a Guid id.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonProperty(PropertyName = "id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Auto-generated human-readable invoice number (e.g., "INV-2024-0001").
        /// Used for display, search, and regulatory compliance.
        /// </summary>
        [JsonPropertyName("invoice_number")]
        [JsonProperty(PropertyName = "invoice_number")]
        public string InvoiceNumber { get; set; }

        /// <summary>
        /// Reference to the customer entity in the CRM service.
        /// Cross-service reference by ID only — zero direct database access across
        /// service boundaries per AAP §0.8.1.
        /// </summary>
        [JsonPropertyName("customer_id")]
        [JsonProperty(PropertyName = "customer_id")]
        public Guid CustomerId { get; set; }

        /// <summary>
        /// Current lifecycle status of the invoice.
        /// Domain events are published on status transitions:
        /// invoicing.invoice.created, invoicing.invoice.paid, invoicing.invoice.voided.
        /// </summary>
        [JsonPropertyName("status")]
        [JsonProperty(PropertyName = "status")]
        [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter<InvoiceStatus>))]
        public InvoiceStatus Status { get; set; }

        /// <summary>
        /// Date the invoice was formally issued to the customer.
        /// Stored and transmitted in UTC.
        /// </summary>
        [JsonPropertyName("issue_date")]
        [JsonProperty(PropertyName = "issue_date")]
        public DateTime IssueDate { get; set; }

        /// <summary>
        /// Date by which payment is due from the customer.
        /// Default: 30 days from issue date (standard NET-30 payment terms).
        /// </summary>
        [JsonPropertyName("due_date")]
        [JsonProperty(PropertyName = "due_date")]
        public DateTime DueDate { get; set; }

        /// <summary>
        /// Sum of all line item totals before tax.
        /// Calculated as: SUM(LineItems[].LineTotal).
        /// Uses <c>decimal</c> type for financial precision — never <c>double</c> or <c>float</c>.
        /// </summary>
        [JsonPropertyName("sub_total")]
        [JsonProperty(PropertyName = "sub_total")]
        public decimal SubTotal { get; set; }

        /// <summary>
        /// Total tax amount across all line items.
        /// Calculated as: SUM(LineItems[].LineTotal * LineItems[].TaxRate).
        /// Uses <c>decimal</c> type for financial precision.
        /// </summary>
        [JsonPropertyName("tax_amount")]
        [JsonProperty(PropertyName = "tax_amount")]
        public decimal TaxAmount { get; set; }

        /// <summary>
        /// Grand total amount due (SubTotal + TaxAmount).
        /// This is the amount the customer must pay.
        /// Uses <c>decimal</c> type for financial precision.
        /// </summary>
        [JsonPropertyName("total_amount")]
        [JsonProperty(PropertyName = "total_amount")]
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// Currency information for all monetary values on this invoice.
        /// Nullable: if not set, the system default currency applies.
        /// Derived from the monolith's CurrencyType pattern in Definitions.cs.
        /// </summary>
        [JsonPropertyName("currency")]
        [JsonProperty(PropertyName = "currency")]
        public CurrencyInfo? Currency { get; set; }

        /// <summary>
        /// Optional free-text notes or comments attached to the invoice.
        /// May contain payment instructions, terms, or customer-facing messages.
        /// </summary>
        [JsonPropertyName("notes")]
        [JsonProperty(PropertyName = "notes")]
        public string Notes { get; set; }

        /// <summary>
        /// Collection of individual billable items on this invoice.
        /// Each line item has its own quantity, unit price, tax rate, and computed total.
        /// Always initialized to an empty list (never null) for safe enumeration.
        /// </summary>
        [JsonPropertyName("line_items")]
        [JsonProperty(PropertyName = "line_items")]
        public List<LineItem> LineItems { get; set; }

        /// <summary>
        /// ID of the user who created this invoice.
        /// Replaces the monolith's <c>SecurityContext.CurrentUser.Id</c> pattern —
        /// extracted from JWT claims in the Lambda handler context.
        /// </summary>
        [JsonPropertyName("created_by")]
        [JsonProperty(PropertyName = "created_by")]
        public Guid CreatedBy { get; set; }

        /// <summary>
        /// UTC timestamp when the invoice was created.
        /// Set once at creation time; immutable thereafter.
        /// </summary>
        [JsonPropertyName("created_on")]
        [JsonProperty(PropertyName = "created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// ID of the user who last modified this invoice.
        /// Updated on every mutation (status change, line item edit, etc.).
        /// </summary>
        [JsonPropertyName("last_modified_by")]
        [JsonProperty(PropertyName = "last_modified_by")]
        public Guid LastModifiedBy { get; set; }

        /// <summary>
        /// UTC timestamp of the last modification to this invoice.
        /// Updated on every mutation for optimistic concurrency and audit trails.
        /// </summary>
        [JsonPropertyName("last_modified_on")]
        [JsonProperty(PropertyName = "last_modified_on")]
        public DateTime LastModifiedOn { get; set; }

        /// <summary>
        /// Initializes a new <see cref="Invoice"/> with safe default values.
        /// All string fields default to <see cref="string.Empty"/>, all monetary
        /// fields default to <c>0m</c>, status defaults to <see cref="InvoiceStatus.Draft"/>,
        /// and DueDate defaults to 30 days from now (standard NET-30 payment terms).
        /// </summary>
        public Invoice()
        {
            Id = Guid.Empty;
            InvoiceNumber = string.Empty;
            CustomerId = Guid.Empty;
            Status = InvoiceStatus.Draft;
            IssueDate = DateTime.UtcNow;
            DueDate = DateTime.UtcNow.AddDays(30);
            SubTotal = 0m;
            TaxAmount = 0m;
            TotalAmount = 0m;
            Currency = null;
            Notes = string.Empty;
            LineItems = new List<LineItem>();
            CreatedBy = Guid.Empty;
            CreatedOn = DateTime.UtcNow;
            LastModifiedBy = Guid.Empty;
            LastModifiedOn = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Represents a single billable line item within an <see cref="Invoice"/>.
    /// Each line item captures a description, quantity, unit price, tax rate,
    /// and computed line total for one product or service being invoiced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All monetary and quantity fields use <c>decimal</c> type for financial precision.
    /// <see cref="Quantity"/> is <c>decimal</c> (not <c>int</c>) to support fractional
    /// quantities such as hours (1.5), weight (2.75 kg), or partial units.
    /// </para>
    /// <para>
    /// <see cref="TaxRate"/> is expressed as a decimal fraction (e.g., 0.20 for 20% tax,
    /// 0.0 for tax-exempt). The service layer computes the tax amount per line item as
    /// <c>LineTotal * TaxRate</c>.
    /// </para>
    /// <para>
    /// <see cref="LineTotal"/> is the pre-tax total for this line: <c>Quantity * UnitPrice</c>.
    /// It is stored (not only computed) to preserve the exact amount at the time of invoicing,
    /// guarding against retroactive price changes.
    /// </para>
    /// </remarks>
    public class LineItem
    {
        /// <summary>
        /// Unique identifier for this line item.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonProperty(PropertyName = "id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Reference to the parent <see cref="Invoice"/> that contains this line item.
        /// Used as the foreign key in the RDS PostgreSQL <c>invoice_line_items</c> table.
        /// </summary>
        [JsonPropertyName("invoice_id")]
        [JsonProperty(PropertyName = "invoice_id")]
        public Guid InvoiceId { get; set; }

        /// <summary>
        /// Human-readable description of the product, service, or charge.
        /// </summary>
        [JsonPropertyName("description")]
        [JsonProperty(PropertyName = "description")]
        public string Description { get; set; }

        /// <summary>
        /// Number of units being billed. Uses <c>decimal</c> to support fractional
        /// quantities (e.g., 1.5 hours, 2.75 kg).
        /// </summary>
        [JsonPropertyName("quantity")]
        [JsonProperty(PropertyName = "quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// Price per single unit. Uses <c>decimal</c> for financial precision.
        /// </summary>
        [JsonPropertyName("unit_price")]
        [JsonProperty(PropertyName = "unit_price")]
        public decimal UnitPrice { get; set; }

        /// <summary>
        /// Tax rate as a decimal fraction (e.g., 0.20 for 20%, 0.0 for tax-exempt).
        /// Applied to <see cref="LineTotal"/> to compute the tax amount for this line.
        /// </summary>
        [JsonPropertyName("tax_rate")]
        [JsonProperty(PropertyName = "tax_rate")]
        public decimal TaxRate { get; set; }

        /// <summary>
        /// Pre-tax total for this line item: <c>Quantity × UnitPrice</c>.
        /// Stored explicitly to preserve the exact invoiced amount and prevent
        /// retroactive changes from affecting historical invoices.
        /// </summary>
        [JsonPropertyName("line_total")]
        [JsonProperty(PropertyName = "line_total")]
        public decimal LineTotal { get; set; }

        /// <summary>
        /// Display ordering position within the parent invoice.
        /// Lower values appear first. Default is 0.
        /// </summary>
        [JsonPropertyName("sort_order")]
        [JsonProperty(PropertyName = "sort_order")]
        public int SortOrder { get; set; }

        /// <summary>
        /// Initializes a new <see cref="LineItem"/> with safe default values.
        /// All monetary fields default to <c>0m</c>, Description defaults to
        /// <see cref="string.Empty"/>, and SortOrder defaults to 0.
        /// </summary>
        public LineItem()
        {
            Id = Guid.Empty;
            InvoiceId = Guid.Empty;
            Description = string.Empty;
            Quantity = 0m;
            UnitPrice = 0m;
            TaxRate = 0m;
            LineTotal = 0m;
            SortOrder = 0;
        }
    }
}
