/**
 * Invoicing/Billing API Endpoints Module
 *
 * Typed API functions for the Invoicing/Billing bounded-context service.
 * This domain uses RDS PostgreSQL with ACID transaction guarantees.
 * Invoice, payment, and quote record CRUD patterns are derived from the
 * monolith's WebApiController.cs record endpoints applied to invoice-specific
 * entities, now served by the dedicated Invoicing Lambda service.
 *
 * Route prefix: /invoicing
 *   - Invoices:  /invoicing/invoices
 *   - Payments:  /invoicing/payments
 *   - Quotes:    /invoicing/quotes
 */

import { get, post, put, del } from '../client';
import type { ApiResponse } from '../client';
import type { EntityRecord } from '../../types/record';

// ---------------------------------------------------------------------------
// Route constants
// ---------------------------------------------------------------------------

/** Base path for invoice operations */
const INVOICES_BASE = '/invoicing/invoices';

/** Base path for payment operations */
const PAYMENTS_BASE = '/invoicing/payments';

/** Base path for quote operations */
const QUOTES_BASE = '/invoicing/quotes';

// ---------------------------------------------------------------------------
// Interface exports — Invoice
// ---------------------------------------------------------------------------

/**
 * Query parameters for listing invoices with filtering and pagination.
 * All fields are optional; when omitted the server applies defaults.
 */
export interface InvoiceListParams {
  /** Free-text search across invoice fields */
  search?: string;
  /** Page number (1-based) */
  page?: number;
  /** Number of records per page */
  pageSize?: number;
  /** Filter by invoice status (e.g. "draft", "sent", "paid", "overdue") */
  status?: string;
  /** Filter by customer ID (GUID) */
  customerId?: string;
  /** Filter invoices created on or after this date (ISO 8601) */
  fromDate?: string;
  /** Filter invoices created on or before this date (ISO 8601) */
  toDate?: string;
  /** Field name to sort by */
  sortField?: string;
  /** Sort direction */
  sortType?: 'asc' | 'desc';
}

/**
 * Parameters required to create a new invoice.
 * The Invoicing service processes this within an ACID transaction against
 * RDS PostgreSQL, mirroring the monolith's DbContext transactional pattern
 * (CreateConnection → BeginTransaction → Commit).
 */
export interface InvoiceCreateParams {
  /** ID of the customer (GUID) the invoice is billed to */
  customerId: string;
  /** Payment due date in ISO 8601 format */
  dueDate: string;
  /** Array of line items composing the invoice body */
  lineItems: InvoiceLineItem[];
  /** Optional free-text notes attached to the invoice */
  notes?: string;
  /** Optional initial status override (defaults to "draft" server-side) */
  status?: string;
}

/**
 * Represents a single line item within an invoice.
 * Total per line = quantity × unitPrice × (1 + taxRate).
 */
export interface InvoiceLineItem {
  /** Description of the goods or service */
  description: string;
  /** Quantity (must be > 0) */
  quantity: number;
  /** Unit price in the invoice currency */
  unitPrice: number;
  /** Optional tax rate as a decimal (e.g. 0.2 = 20 %) */
  taxRate?: number;
}

// ---------------------------------------------------------------------------
// Interface exports — Payment
// ---------------------------------------------------------------------------

/**
 * Query parameters for listing payments with optional filtering.
 */
export interface PaymentListParams {
  /** Filter by invoice ID to list payments for a specific invoice */
  invoiceId?: string;
  /** Page number (1-based) */
  page?: number;
  /** Number of records per page */
  pageSize?: number;
  /** Filter payments made on or after this date (ISO 8601) */
  fromDate?: string;
  /** Filter payments made on or before this date (ISO 8601) */
  toDate?: string;
}

/**
 * Parameters required to create a new payment record.
 * Processed within an ACID transaction to guarantee consistency
 * between the payment and invoice balance.
 */
export interface PaymentCreateParams {
  /** ID of the invoice this payment applies to (GUID) */
  invoiceId: string;
  /** Payment amount in the invoice currency */
  amount: number;
  /** Date the payment was received (ISO 8601) */
  paymentDate: string;
  /** Optional payment method (e.g. "bank_transfer", "credit_card", "cash") */
  method?: string;
  /** Optional external reference or transaction ID */
  reference?: string;
}

// ---------------------------------------------------------------------------
// Interface exports — Quote
// ---------------------------------------------------------------------------

/**
 * Query parameters for listing quotes with optional filtering.
 */
export interface QuoteListParams {
  /** Free-text search across quote fields */
  search?: string;
  /** Page number (1-based) */
  page?: number;
  /** Number of records per page */
  pageSize?: number;
  /** Filter by quote status (e.g. "draft", "sent", "accepted", "rejected") */
  status?: string;
  /** Filter by customer ID (GUID) */
  customerId?: string;
}

// ---------------------------------------------------------------------------
// Invoice functions
// ---------------------------------------------------------------------------

/**
 * List invoices with optional filtering and pagination.
 *
 * Sends GET /invoicing/invoices with query parameters derived from
 * {@link InvoiceListParams}. Mirrors the monolith's record list endpoint
 * with entity-specific route ownership.
 *
 * @param params - Optional filtering, pagination, and sorting parameters
 * @returns Paginated array of invoice entity records
 */
export async function listInvoices(
  params?: InvoiceListParams
): Promise<ApiResponse<EntityRecord[]>> {
  return get<EntityRecord[]>(
    INVOICES_BASE,
    params as Record<string, unknown> | undefined
  );
}

/**
 * Retrieve a single invoice by its unique identifier, including line items.
 *
 * Sends GET /invoicing/invoices/{invoiceId}.
 *
 * @param invoiceId - The GUID of the invoice to retrieve
 * @returns The invoice entity record with nested line items
 */
export async function getInvoice(
  invoiceId: string
): Promise<ApiResponse<EntityRecord>> {
  return get<EntityRecord>(`${INVOICES_BASE}/${encodeURIComponent(invoiceId)}`);
}

/**
 * Create a new invoice within an ACID transaction.
 *
 * Sends POST /invoicing/invoices. The server-side Invoicing Lambda
 * wraps this in an RDS PostgreSQL transaction and publishes an SNS event
 * `invoicing.invoice.created` for cross-domain consumers (inventory update,
 * notification workflows).
 *
 * @param invoice - Invoice creation parameters including line items
 * @returns The newly created invoice entity record
 */
export async function createInvoice(
  invoice: InvoiceCreateParams
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>(INVOICES_BASE, invoice);
}

/**
 * Update an existing invoice with partial data.
 *
 * Sends PUT /invoicing/invoices/{invoiceId}. Only the provided fields
 * are updated; omitted fields retain their current values.
 *
 * @param invoiceId - The GUID of the invoice to update
 * @param invoice   - Partial invoice data to merge
 * @returns The updated invoice entity record
 */
export async function updateInvoice(
  invoiceId: string,
  invoice: Partial<InvoiceCreateParams>
): Promise<ApiResponse<EntityRecord>> {
  return put<EntityRecord>(
    `${INVOICES_BASE}/${encodeURIComponent(invoiceId)}`,
    invoice
  );
}

/**
 * Delete an invoice by its unique identifier.
 *
 * Sends DELETE /invoicing/invoices/{invoiceId}. The server enforces
 * business rules (e.g. cannot delete a paid invoice).
 *
 * @param invoiceId - The GUID of the invoice to delete
 * @returns Success/error response without an object payload
 */
export async function deleteInvoice(
  invoiceId: string
): Promise<ApiResponse<void>> {
  return del<void>(`${INVOICES_BASE}/${encodeURIComponent(invoiceId)}`);
}

// ---------------------------------------------------------------------------
// Payment functions
// ---------------------------------------------------------------------------

/**
 * List payments with optional filtering and pagination.
 *
 * Sends GET /invoicing/payments. Can be scoped to a specific invoice
 * via the `invoiceId` parameter.
 *
 * @param params - Optional filtering and pagination parameters
 * @returns Paginated array of payment entity records
 */
export async function listPayments(
  params?: PaymentListParams
): Promise<ApiResponse<EntityRecord[]>> {
  return get<EntityRecord[]>(
    PAYMENTS_BASE,
    params as Record<string, unknown> | undefined
  );
}

/**
 * Retrieve a single payment by its unique identifier.
 *
 * Sends GET /invoicing/payments/{paymentId}.
 *
 * @param paymentId - The GUID of the payment to retrieve
 * @returns The payment entity record
 */
export async function getPayment(
  paymentId: string
): Promise<ApiResponse<EntityRecord>> {
  return get<EntityRecord>(
    `${PAYMENTS_BASE}/${encodeURIComponent(paymentId)}`
  );
}

/**
 * Create a new payment within an ACID transaction.
 *
 * Sends POST /invoicing/payments. The server-side Lambda atomically
 * creates the payment record and updates the related invoice balance
 * within a single PostgreSQL transaction.
 *
 * @param payment - Payment creation parameters
 * @returns The newly created payment entity record
 */
export async function createPayment(
  payment: PaymentCreateParams
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>(PAYMENTS_BASE, payment);
}

/**
 * Update an existing payment with partial data.
 *
 * Sends PUT /invoicing/payments/{paymentId}.
 *
 * @param paymentId - The GUID of the payment to update
 * @param payment   - Partial payment data to merge
 * @returns The updated payment entity record
 */
export async function updatePayment(
  paymentId: string,
  payment: Partial<PaymentCreateParams>
): Promise<ApiResponse<EntityRecord>> {
  return put<EntityRecord>(
    `${PAYMENTS_BASE}/${encodeURIComponent(paymentId)}`,
    payment
  );
}

/**
 * Delete a payment by its unique identifier.
 *
 * Sends DELETE /invoicing/payments/{paymentId}. The server recalculates
 * the invoice balance after payment removal.
 *
 * @param paymentId - The GUID of the payment to delete
 * @returns Success/error response without an object payload
 */
export async function deletePayment(
  paymentId: string
): Promise<ApiResponse<void>> {
  return del<void>(`${PAYMENTS_BASE}/${encodeURIComponent(paymentId)}`);
}

// ---------------------------------------------------------------------------
// Quote functions
// ---------------------------------------------------------------------------

/**
 * List quotes with optional filtering and pagination.
 *
 * Sends GET /invoicing/quotes.
 *
 * @param params - Optional filtering and pagination parameters
 * @returns Paginated array of quote entity records
 */
export async function listQuotes(
  params?: QuoteListParams
): Promise<ApiResponse<EntityRecord[]>> {
  return get<EntityRecord[]>(
    QUOTES_BASE,
    params as Record<string, unknown> | undefined
  );
}

/**
 * Retrieve a single quote by its unique identifier.
 *
 * Sends GET /invoicing/quotes/{quoteId}.
 *
 * @param quoteId - The GUID of the quote to retrieve
 * @returns The quote entity record
 */
export async function getQuote(
  quoteId: string
): Promise<ApiResponse<EntityRecord>> {
  return get<EntityRecord>(`${QUOTES_BASE}/${encodeURIComponent(quoteId)}`);
}

/**
 * Create a new quote.
 *
 * Sends POST /invoicing/quotes. Quotes use the generic EntityRecord
 * format since their field structure mirrors the dynamic entity system.
 *
 * @param quote - The quote entity record to create
 * @returns The newly created quote entity record
 */
export async function createQuote(
  quote: EntityRecord
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>(QUOTES_BASE, quote);
}

/**
 * Update an existing quote.
 *
 * Sends PUT /invoicing/quotes/{quoteId}. The entire quote body is
 * provided as an EntityRecord, allowing dynamic field updates.
 *
 * @param quoteId - The GUID of the quote to update
 * @param quote   - The updated quote entity record body
 * @returns The updated quote entity record
 */
export async function updateQuote(
  quoteId: string,
  quote: EntityRecord
): Promise<ApiResponse<EntityRecord>> {
  return put<EntityRecord>(
    `${QUOTES_BASE}/${encodeURIComponent(quoteId)}`,
    quote
  );
}

/**
 * Convert an existing quote into an invoice.
 *
 * Sends POST /invoicing/quotes/{quoteId}/convert. This is a workflow
 * operation that:
 *   1. Reads the quote data
 *   2. Creates a new invoice from the quote fields
 *   3. Marks the quote status as "converted"
 *   4. Publishes an SNS event for cross-domain notification
 *
 * All steps are executed within a single ACID transaction.
 *
 * @param quoteId - The GUID of the quote to convert
 * @returns The newly created invoice entity record
 */
export async function convertQuoteToInvoice(
  quoteId: string
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>(
    `${QUOTES_BASE}/${encodeURIComponent(quoteId)}/convert`
  );
}
