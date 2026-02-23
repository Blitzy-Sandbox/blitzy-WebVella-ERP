/**
 * InvoiceDetails — Invoice Detail View Page Component
 *
 * Displays a complete invoice detail view with line items, totals breakdown,
 * payment history, and status-driven actions (send, mark paid, void, delete).
 *
 * Source mapping:
 *  - RecordDetails.cshtml.cs OnGet()  → useQuery for invoice detail fetch
 *  - RecordDetails.cshtml.cs OnPost() → useMutation for status actions + delete
 *  - RecordManager.Find()             → GET /v1/invoicing/invoices/:id
 *  - RecordManager.DeleteRecord()     → DELETE /v1/invoicing/invoices/:id
 *  - $relation.field payment pattern  → GET /v1/invoicing/invoices/:id/payments
 *  - Definitions.cs CurrencyType      → formatCurrency() helper
 *  - PcTabNav ViewComponent           → TabNav for section organization
 *  - PcGrid ViewComponent             → DataTable for payment history
 *  - PcModal ViewComponent            → Modal for action confirmations
 *
 * AAP compliance:
 *  - §0.8.1 — Full behavioral parity: all CRUD, status actions, payments
 *  - §0.8.1 — Pure static SPA: all data via API calls
 *  - §0.8.2 — API Gateway path-based versioning (/v1/)
 *  - §0.4.3 — Tailwind CSS 4.x styling (zero Bootstrap)
 *  - §0.7.2 — Post-hooks mapped to domain events via API mutations
 *
 * @module pages/invoicing/InvoiceDetails
 */

import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams, useNavigate, Link } from 'react-router-dom';

import { get, post, del } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Modal from '../../components/common/Modal';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';

// ---------------------------------------------------------------------------
// TypeScript Interfaces
// ---------------------------------------------------------------------------

/**
 * Invoice status union type.
 * Maps status lifecycle: draft → sent → (partial | paid | overdue | void).
 */
type InvoiceStatus = 'draft' | 'sent' | 'paid' | 'overdue' | 'void' | 'partial';

/**
 * Single line item within an invoice.
 * Extracted from monolith's dynamic record field pattern for invoice entities.
 */
interface InvoiceLineItem {
  /** Unique line item identifier */
  id: string;
  /** Line item description */
  description: string;
  /** Quantity of units */
  quantity: number;
  /** Price per unit */
  unit_price: number;
  /** Tax rate as a percentage (e.g. 10 for 10%) */
  tax_rate: number;
  /** Computed line total (quantity × unit_price) */
  amount: number;
}

/**
 * Payment record associated with an invoice.
 * Replaces the $relation.field query pattern for related payment records.
 */
interface InvoicePayment {
  /** Index signature required by DataTable<T extends Record<string, unknown>> */
  [key: string]: unknown;
  /** Unique payment identifier */
  id: string;
  /** Payment reference number */
  reference_number: string;
  /** ISO 8601 date when payment was received */
  payment_date: string;
  /** Payment amount in invoice currency */
  amount: number;
  /** Payment method (e.g. bank_transfer, credit_card, cash, check) */
  payment_method: string;
  /** Optional notes about the payment */
  notes: string;
}

/**
 * Full invoice detail model.
 * Combines entity metadata, line items, and computed totals.
 * Maps to monolith's RecordManager.Find() result + entity field definitions.
 */
interface InvoiceDetail {
  /** Unique invoice identifier */
  id: string;
  /** Display invoice number (e.g. INV-0001) */
  invoice_number: string;
  /** Associated customer ID */
  customer_id: string;
  /** Customer display name */
  customer_name: string;
  /** ISO 8601 date the invoice was issued */
  issue_date: string;
  /** ISO 8601 date the invoice is due */
  due_date: string;
  /** ISO 4217 currency code (e.g. USD, EUR, BGN) */
  currency: string;
  /** Current invoice status */
  status: InvoiceStatus;
  /** Sum of line item amounts before tax and discounts */
  subtotal: number;
  /** Calculated tax amount */
  tax_amount: number;
  /** Tax rate as a percentage */
  tax_rate: number;
  /** Discount application type */
  discount_type: 'percentage' | 'fixed';
  /** Discount value (percentage number or fixed amount) */
  discount_value: number;
  /** Calculated discount amount */
  discount_amount: number;
  /** Grand total (subtotal + tax - discount) */
  total_amount: number;
  /** Total amount already paid */
  paid_amount: number;
  /** Remaining balance (total_amount - paid_amount) */
  balance: number;
  /** Optional notes displayed on the invoice */
  notes: string;
  /** Payment terms and conditions */
  terms: string;
  /** Invoice line items */
  line_items: InvoiceLineItem[];
  /** ISO 8601 creation timestamp */
  created_on: string;
  /** ISO 8601 last-updated timestamp */
  updated_on: string;
}

// ---------------------------------------------------------------------------
// Action type for confirmation modals
// ---------------------------------------------------------------------------

/** Action types requiring user confirmation before execution */
type ConfirmAction = 'send' | 'markPaid' | 'void' | 'delete';

// ---------------------------------------------------------------------------
// Status-to-allowed-actions mapping
// ---------------------------------------------------------------------------

/**
 * Defines which action buttons are available for each invoice status.
 * Replaces monolith's hook-driven action pattern from RecordDetails.cshtml.cs
 * where HookKey controlled post-action behavior.
 */
const ALLOWED_ACTIONS: Record<InvoiceStatus, string[]> = {
  draft: ['edit', 'send', 'delete'],
  sent: ['edit', 'record_payment', 'mark_paid', 'void'],
  partial: ['edit', 'record_payment', 'mark_paid', 'void'],
  overdue: ['edit', 'record_payment', 'mark_paid', 'void'],
  paid: [],
  void: [],
};

// ---------------------------------------------------------------------------
// Helper functions
// ---------------------------------------------------------------------------

/**
 * Formats a numeric amount as a currency string.
 * Mirrors monolith's CurrencyType from Definitions.cs with Symbol,
 * DecimalDigits, and SymbolPlacement properties.
 *
 * @param amount   - Numeric value to format
 * @param currency - ISO 4217 currency code (default 'USD')
 * @returns Locale-formatted currency string
 */
function formatCurrency(amount: number, currency: string = 'USD'): string {
  try {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency,
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }).format(amount);
  } catch {
    // Fallback for unsupported currency codes
    return `${currency} ${amount.toFixed(2)}`;
  }
}

/**
 * Formats an ISO 8601 date string for display.
 *
 * @param dateStr - ISO 8601 date string
 * @returns Locale-formatted date string or empty string for invalid input
 */
function formatDate(dateStr: string | undefined | null): string {
  if (!dateStr) return '';
  try {
    return new Date(dateStr).toLocaleDateString('en-US', {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    });
  } catch {
    return dateStr;
  }
}

/**
 * Returns Tailwind CSS classes for the invoice status badge.
 * Color-coded per requirements:
 *  - draft → gray
 *  - sent → blue
 *  - paid → green
 *  - overdue → red
 *  - void → gray with line-through
 *  - partial → yellow/amber
 */
function getStatusBadgeClasses(status: InvoiceStatus): string {
  const base =
    'inline-flex items-center rounded-full px-3 py-1 text-xs font-semibold';

  switch (status) {
    case 'draft':
      return `${base} bg-gray-100 text-gray-700`;
    case 'sent':
      return `${base} bg-blue-100 text-blue-700`;
    case 'paid':
      return `${base} bg-green-100 text-green-700`;
    case 'overdue':
      return `${base} bg-red-100 text-red-700`;
    case 'void':
      return `${base} bg-gray-100 text-gray-500 line-through`;
    case 'partial':
      return `${base} bg-amber-100 text-amber-700`;
    default:
      return `${base} bg-gray-100 text-gray-700`;
  }
}

/**
 * Capitalizes the first letter of a string for display.
 */
function capitalize(str: string): string {
  if (!str) return '';
  return str.charAt(0).toUpperCase() + str.slice(1);
}

/**
 * Formats a payment method string for display (e.g. bank_transfer → Bank Transfer).
 */
function formatPaymentMethod(method: string): string {
  if (!method) return '';
  return method
    .split('_')
    .map((word) => capitalize(word))
    .join(' ');
}

// ---------------------------------------------------------------------------
// Confirmation modal content configuration
// ---------------------------------------------------------------------------

/** Configuration for each confirmation modal */
const MODAL_CONFIG: Record<
  ConfirmAction,
  { title: string; message: string; confirmLabel: string; isDangerous: boolean }
> = {
  send: {
    title: 'Send Invoice',
    message: 'Send this invoice to the customer? They will receive a notification.',
    confirmLabel: 'Send Invoice',
    isDangerous: false,
  },
  markPaid: {
    title: 'Mark as Paid',
    message:
      'Mark this invoice as fully paid? This will update the balance to zero.',
    confirmLabel: 'Mark Paid',
    isDangerous: false,
  },
  void: {
    title: 'Void Invoice',
    message:
      'Void this invoice? This action cannot be undone. The invoice will be permanently marked as void.',
    confirmLabel: 'Void Invoice',
    isDangerous: true,
  },
  delete: {
    title: 'Delete Invoice',
    message:
      'Delete this invoice? This action cannot be undone. The invoice and all associated data will be permanently removed.',
    confirmLabel: 'Delete Invoice',
    isDangerous: true,
  },
};

// ---------------------------------------------------------------------------
// Payment history DataTable column definitions
// ---------------------------------------------------------------------------

/**
 * Builds column definitions for the payment history DataTable.
 * Each column maps to a payment record field.
 *
 * @param currency - Currency code for amount formatting
 */
function buildPaymentColumns(
  currency: string,
): DataTableColumn<InvoicePayment>[] {
  return [
    {
      id: 'payment_date',
      label: 'Date',
      accessorKey: 'payment_date',
      width: '140px',
      cell: (value: unknown) => formatDate(value as string),
    },
    {
      id: 'reference_number',
      label: 'Reference',
      accessorKey: 'reference_number',
      width: '160px',
    },
    {
      id: 'payment_method',
      label: 'Method',
      accessorKey: 'payment_method',
      width: '140px',
      cell: (value: unknown) => formatPaymentMethod(value as string),
    },
    {
      id: 'amount',
      label: 'Amount',
      accessorKey: 'amount',
      width: '140px',
      cell: (value: unknown) => (
        <span className="font-medium text-green-700">
          {formatCurrency(value as number, currency)}
        </span>
      ),
    },
    {
      id: 'notes',
      label: 'Notes',
      accessorKey: 'notes',
    },
  ];
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * InvoiceDetails — Page component for viewing a single invoice.
 *
 * Replaces the monolith's RecordDetailsPageModel lifecycle:
 *  - OnGet() → useQuery hooks for invoice detail + payment history
 *  - OnPost(delete) → useMutation for DELETE /v1/invoicing/invoices/:id
 *  - OnPost(hook actions) → useMutation for send/mark-paid/void API endpoints
 *  - RecordsExists() → 404 handling on query failure
 *  - BeforeRender() → React state-driven re-rendering
 *
 * Route: /invoicing/invoices/:invoiceId
 */
export default function InvoiceDetails(): React.ReactElement {
  // -------------------------------------------------------------------------
  // Routing
  // -------------------------------------------------------------------------
  const { invoiceId } = useParams<{ invoiceId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  // -------------------------------------------------------------------------
  // Local state — modal visibility
  // -------------------------------------------------------------------------
  const [activeModal, setActiveModal] = useState<ConfirmAction | null>(null);
  const [actionError, setActionError] = useState<string>('');

  // -------------------------------------------------------------------------
  // Data fetching: Invoice detail
  // Query key: ['invoicing', 'invoices', id]
  // Endpoint: GET /v1/invoicing/invoices/:id
  // -------------------------------------------------------------------------
  const {
    data: invoiceResponse,
    isLoading: isInvoiceLoading,
    isError: isInvoiceError,
    error: invoiceError,
  } = useQuery<ApiResponse<InvoiceDetail>, ApiError>({
    queryKey: ['invoicing', 'invoices', invoiceId],
    queryFn: () => get<InvoiceDetail>(`/invoicing/invoices/${invoiceId}`),
    enabled: Boolean(invoiceId),
  });

  // -------------------------------------------------------------------------
  // Data fetching: Payment history
  // Query key: ['invoicing', 'invoices', id, 'payments']
  // Endpoint: GET /v1/invoicing/invoices/:id/payments
  // -------------------------------------------------------------------------
  const {
    data: paymentsResponse,
    isLoading: isPaymentsLoading,
  } = useQuery<ApiResponse<InvoicePayment[]>, ApiError>({
    queryKey: ['invoicing', 'invoices', invoiceId, 'payments'],
    queryFn: () =>
      get<InvoicePayment[]>(`/invoicing/invoices/${invoiceId}/payments`),
    enabled: Boolean(invoiceId),
  });

  // -------------------------------------------------------------------------
  // Mutations: Status actions
  // -------------------------------------------------------------------------

  /** Send invoice — POST /v1/invoicing/invoices/:id/send */
  const sendMutation = useMutation<ApiResponse<InvoiceDetail>, ApiError>({
    mutationFn: () =>
      post<InvoiceDetail>(`/invoicing/invoices/${invoiceId}/send`),
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: ['invoicing', 'invoices', invoiceId],
      });
      queryClient.invalidateQueries({
        queryKey: ['invoicing', 'invoices', invoiceId, 'payments'],
      });
      setActiveModal(null);
      setActionError('');
    },
    onError: (error: ApiError) => {
      setActionError(error.message || 'Failed to send invoice.');
    },
  });

  /** Mark paid — POST /v1/invoicing/invoices/:id/mark-paid */
  const markPaidMutation = useMutation<ApiResponse<InvoiceDetail>, ApiError>({
    mutationFn: () =>
      post<InvoiceDetail>(`/invoicing/invoices/${invoiceId}/mark-paid`),
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: ['invoicing', 'invoices', invoiceId],
      });
      queryClient.invalidateQueries({
        queryKey: ['invoicing', 'invoices', invoiceId, 'payments'],
      });
      setActiveModal(null);
      setActionError('');
    },
    onError: (error: ApiError) => {
      setActionError(error.message || 'Failed to mark invoice as paid.');
    },
  });

  /** Void invoice — POST /v1/invoicing/invoices/:id/void */
  const voidMutation = useMutation<ApiResponse<InvoiceDetail>, ApiError>({
    mutationFn: () =>
      post<InvoiceDetail>(`/invoicing/invoices/${invoiceId}/void`),
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: ['invoicing', 'invoices', invoiceId],
      });
      setActiveModal(null);
      setActionError('');
    },
    onError: (error: ApiError) => {
      setActionError(error.message || 'Failed to void invoice.');
    },
  });

  /** Delete invoice — DELETE /v1/invoicing/invoices/:id */
  const deleteMutation = useMutation<ApiResponse<void>, ApiError>({
    mutationFn: () => del<void>(`/invoicing/invoices/${invoiceId}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['invoicing', 'invoices'] });
      navigate('/invoicing/invoices');
    },
    onError: (error: ApiError) => {
      setActionError(error.message || 'Failed to delete invoice.');
    },
  });

  // -------------------------------------------------------------------------
  // Derived data
  // -------------------------------------------------------------------------
  const invoice = invoiceResponse?.success ? invoiceResponse.object : undefined;
  const payments =
    paymentsResponse?.success ? (paymentsResponse.object ?? []) : [];
  const currency = invoice?.currency ?? 'USD';
  const actions = invoice ? (ALLOWED_ACTIONS[invoice.status] ?? []) : [];
  const paymentColumns = buildPaymentColumns(currency);

  // -------------------------------------------------------------------------
  // Action handlers
  // -------------------------------------------------------------------------

  /** Opens a confirmation modal for the specified action */
  function openConfirmModal(action: ConfirmAction): void {
    setActionError('');
    setActiveModal(action);
  }

  /** Closes the active confirmation modal */
  function closeConfirmModal(): void {
    setActiveModal(null);
    setActionError('');
  }

  /** Executes the confirmed action */
  function executeAction(): void {
    if (!activeModal) return;

    switch (activeModal) {
      case 'send':
        sendMutation.mutate();
        break;
      case 'markPaid':
        markPaidMutation.mutate();
        break;
      case 'void':
        voidMutation.mutate();
        break;
      case 'delete':
        deleteMutation.mutate();
        break;
    }
  }

  /** Whether any mutation is currently in flight */
  const isMutating =
    sendMutation.isPending ||
    markPaidMutation.isPending ||
    voidMutation.isPending ||
    deleteMutation.isPending;

  // -------------------------------------------------------------------------
  // Render: Loading state
  // -------------------------------------------------------------------------
  if (isInvoiceLoading) {
    return (
      <div className="flex min-h-[50vh] items-center justify-center">
        <div className="text-center">
          <div
            className="mx-auto mb-4 h-8 w-8 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600"
            role="status"
            aria-label="Loading invoice details"
          />
          <p className="text-sm text-gray-500">Loading invoice details…</p>
        </div>
      </div>
    );
  }

  // -------------------------------------------------------------------------
  // Render: Error / 404 state
  // Replaces RecordDetails.cshtml.cs RecordsExists() → NotFound()
  // -------------------------------------------------------------------------
  if (isInvoiceError || !invoice) {
    const is404 = (invoiceError as ApiError | undefined)?.status === 404;
    return (
      <div className="flex min-h-[50vh] items-center justify-center">
        <div className="text-center">
          <h2 className="mb-2 text-xl font-semibold text-gray-900">
            {is404 ? 'Invoice Not Found' : 'Error Loading Invoice'}
          </h2>
          <p className="mb-4 text-sm text-gray-500">
            {is404
              ? 'The invoice you are looking for does not exist or has been removed.'
              : (invoiceError as ApiError | undefined)?.message ??
                'An unexpected error occurred while loading the invoice.'}
          </p>
          <Link
            to="/invoicing/invoices"
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            ← Back to Invoices
          </Link>
        </div>
      </div>
    );
  }

  // -------------------------------------------------------------------------
  // Tab content: Line Items
  // -------------------------------------------------------------------------
  const lineItemsContent = (
    <div className="overflow-x-auto">
      {/* Line Items Table */}
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-gray-200 text-start text-xs font-medium uppercase tracking-wider text-gray-500">
            <th className="px-4 py-3 text-start" scope="col">
              #
            </th>
            <th className="px-4 py-3 text-start" scope="col">
              Description
            </th>
            <th className="px-4 py-3 text-end" scope="col">
              Qty
            </th>
            <th className="px-4 py-3 text-end" scope="col">
              Unit Price
            </th>
            <th className="px-4 py-3 text-end" scope="col">
              Tax %
            </th>
            <th className="px-4 py-3 text-end" scope="col">
              Amount
            </th>
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-100">
          {invoice.line_items.length === 0 ? (
            <tr>
              <td
                colSpan={6}
                className="px-4 py-8 text-center text-sm text-gray-400"
              >
                No line items on this invoice.
              </td>
            </tr>
          ) : (
            invoice.line_items.map((item, index) => (
              <tr
                key={item.id}
                className="hover:bg-gray-50 transition-colors duration-150"
              >
                <td className="px-4 py-3 text-gray-500">{index + 1}</td>
                <td className="px-4 py-3 text-gray-900">
                  {item.description || ''}
                </td>
                <td className="px-4 py-3 text-end text-gray-700">
                  {item.quantity}
                </td>
                <td className="px-4 py-3 text-end text-gray-700">
                  {formatCurrency(item.unit_price, currency)}
                </td>
                <td className="px-4 py-3 text-end text-gray-700">
                  {item.tax_rate}%
                </td>
                <td className="px-4 py-3 text-end font-medium text-gray-900">
                  {formatCurrency(item.amount, currency)}
                </td>
              </tr>
            ))
          )}
        </tbody>
      </table>

      {/* Totals Section — right-aligned below line items */}
      <div className="mt-6 flex justify-end">
        <dl className="w-full max-w-xs space-y-2 text-sm">
          <div className="flex justify-between">
            <dt className="text-gray-500">Subtotal</dt>
            <dd className="font-medium text-gray-900">
              {formatCurrency(invoice.subtotal, currency)}
            </dd>
          </div>

          <div className="flex justify-between">
            <dt className="text-gray-500">
              Tax ({invoice.tax_rate}%)
            </dt>
            <dd className="font-medium text-gray-900">
              {formatCurrency(invoice.tax_amount, currency)}
            </dd>
          </div>

          {invoice.discount_amount > 0 && (
            <div className="flex justify-between">
              <dt className="text-gray-500">
                Discount
                {invoice.discount_type === 'percentage'
                  ? ` (-${invoice.discount_value}%)`
                  : ''}
              </dt>
              <dd className="font-medium text-red-600">
                -{formatCurrency(invoice.discount_amount, currency)}
              </dd>
            </div>
          )}

          <div className="flex justify-between border-t border-gray-200 pt-2">
            <dt className="text-base font-bold text-gray-900">Total</dt>
            <dd className="text-base font-bold text-gray-900">
              {formatCurrency(invoice.total_amount, currency)}
            </dd>
          </div>

          <div className="flex justify-between">
            <dt className="text-gray-500">Amount Paid</dt>
            <dd className="font-medium text-green-700">
              {formatCurrency(invoice.paid_amount, currency)}
            </dd>
          </div>

          <div className="flex justify-between border-t border-gray-200 pt-2">
            <dt className="text-base font-bold text-gray-900">Balance Due</dt>
            <dd
              className={`text-base font-bold ${
                invoice.balance > 0 ? 'text-red-600' : 'text-green-700'
              }`}
            >
              {formatCurrency(invoice.balance, currency)}
            </dd>
          </div>
        </dl>
      </div>
    </div>
  );

  // -------------------------------------------------------------------------
  // Tab content: Payments
  // -------------------------------------------------------------------------
  const paymentsContent = (
    <div>
      {isPaymentsLoading ? (
        <div className="flex items-center justify-center py-12">
          <div
            className="h-6 w-6 animate-spin rounded-full border-2 border-gray-200 border-t-blue-600"
            role="status"
            aria-label="Loading payments"
          />
          <span className="ms-2 text-sm text-gray-500">
            Loading payments…
          </span>
        </div>
      ) : payments.length === 0 ? (
        <div className="py-12 text-center">
          <p className="mb-4 text-sm text-gray-400">
            No payments recorded for this invoice.
          </p>
          {actions.includes('record_payment') && (
            <Link
              to={`/invoicing/payments/create?invoiceId=${invoiceId}`}
              className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-4 py-2 text-sm font-medium text-white hover:bg-green-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600"
            >
              + Record Payment
            </Link>
          )}
        </div>
      ) : (
        <>
          <DataTable<InvoicePayment>
            data={payments}
            columns={paymentColumns}
            totalCount={payments.length}
            pageSize={10}
          />
          {actions.includes('record_payment') && (
            <div className="mt-4 flex justify-end">
              <Link
                to={`/invoicing/payments/create?invoiceId=${invoiceId}`}
                className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-4 py-2 text-sm font-medium text-white hover:bg-green-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600"
              >
                + Record Payment
              </Link>
            </div>
          )}
        </>
      )}
    </div>
  );

  // -------------------------------------------------------------------------
  // Tab content: Notes & Terms
  // -------------------------------------------------------------------------
  const notesTermsContent = (
    <div className="space-y-6">
      {/* Notes Section */}
      <section>
        <h3 className="mb-2 text-sm font-semibold text-gray-700">Notes</h3>
        {invoice.notes ? (
          <p className="whitespace-pre-wrap text-sm text-gray-600">
            {invoice.notes}
          </p>
        ) : (
          <p className="text-sm italic text-gray-400">No notes provided.</p>
        )}
      </section>

      {/* Terms Section */}
      <section>
        <h3 className="mb-2 text-sm font-semibold text-gray-700">
          Terms &amp; Conditions
        </h3>
        {invoice.terms ? (
          <p className="whitespace-pre-wrap text-sm text-gray-600">
            {invoice.terms}
          </p>
        ) : (
          <p className="text-sm italic text-gray-400">No terms specified.</p>
        )}
      </section>
    </div>
  );

  // -------------------------------------------------------------------------
  // Tab configuration for TabNav
  // -------------------------------------------------------------------------
  const tabs: TabConfig[] = [
    {
      id: 'line-items',
      label: 'Line Items',
      content: lineItemsContent,
    },
    {
      id: 'payments',
      label: `Payments (${payments.length})`,
      content: paymentsContent,
    },
    {
      id: 'notes-terms',
      label: 'Notes & Terms',
      content: notesTermsContent,
    },
  ];

  // -------------------------------------------------------------------------
  // Modal content for active confirmation
  // -------------------------------------------------------------------------
  const modalConfig = activeModal ? MODAL_CONFIG[activeModal] : null;

  // -------------------------------------------------------------------------
  // Main render
  // -------------------------------------------------------------------------
  return (
    <div className="mx-auto max-w-5xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ================================================================
          Page Header
          ================================================================ */}
      <div className="mb-6">
        {/* Breadcrumb / back link */}
        <Link
          to="/invoicing/invoices"
          className="mb-3 inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          <span aria-hidden="true">←</span>
          Back to Invoices
        </Link>

        <div className="flex flex-wrap items-start justify-between gap-4">
          {/* Title + status badge */}
          <div className="flex items-center gap-3">
            <h1 className="text-2xl font-bold text-gray-900">
              Invoice #{invoice.invoice_number}
            </h1>
            <span className={getStatusBadgeClasses(invoice.status)}>
              {capitalize(invoice.status)}
            </span>
          </div>

          {/* Action buttons — conditional based on invoice status */}
          <div className="flex flex-wrap items-center gap-2">
            {actions.includes('edit') && (
              <Link
                to={`/invoicing/invoices/${invoiceId}/edit`}
                className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              >
                Edit
              </Link>
            )}

            {actions.includes('send') && (
              <button
                type="button"
                onClick={() => openConfirmModal('send')}
                disabled={isMutating}
                className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              >
                Send
              </button>
            )}

            {actions.includes('record_payment') && (
              <Link
                to={`/invoicing/payments/create?invoiceId=${invoiceId}`}
                className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-green-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600"
              >
                Record Payment
              </Link>
            )}

            {actions.includes('mark_paid') && (
              <button
                type="button"
                onClick={() => openConfirmModal('markPaid')}
                disabled={isMutating}
                className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-green-700 disabled:cursor-not-allowed disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600"
              >
                Mark Paid
              </button>
            )}

            {actions.includes('void') && (
              <button
                type="button"
                onClick={() => openConfirmModal('void')}
                disabled={isMutating}
                className="inline-flex items-center gap-1.5 rounded-md border border-red-300 bg-white px-3 py-2 text-sm font-medium text-red-600 shadow-sm hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
              >
                Void
              </button>
            )}

            {actions.includes('delete') && (
              <button
                type="button"
                onClick={() => openConfirmModal('delete')}
                disabled={isMutating}
                className="inline-flex items-center gap-1.5 rounded-md bg-red-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 disabled:cursor-not-allowed disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
              >
                Delete
              </button>
            )}
          </div>
        </div>
      </div>

      {/* ================================================================
          Invoice Information Grid (two-column layout)
          ================================================================ */}
      <div className="mb-6 rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <div className="grid grid-cols-1 gap-x-8 gap-y-4 sm:grid-cols-2">
          {/* Left Column */}
          <div className="space-y-3">
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Invoice Number
              </dt>
              <dd className="mt-0.5 text-sm font-medium text-gray-900">
                {invoice.invoice_number}
              </dd>
            </div>
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Customer
              </dt>
              <dd className="mt-0.5 text-sm text-gray-900">
                <Link
                  to={`/crm/accounts/${invoice.customer_id}`}
                  className="font-medium text-blue-600 hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
                >
                  {invoice.customer_name || '—'}
                </Link>
              </dd>
            </div>
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Issue Date
              </dt>
              <dd className="mt-0.5 text-sm text-gray-900">
                {formatDate(invoice.issue_date)}
              </dd>
            </div>
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Due Date
              </dt>
              <dd className="mt-0.5 text-sm text-gray-900">
                <span
                  className={
                    invoice.status === 'overdue' ? 'font-semibold text-red-600' : ''
                  }
                >
                  {formatDate(invoice.due_date)}
                  {invoice.status === 'overdue' && ' (Overdue)'}
                </span>
              </dd>
            </div>
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Status
              </dt>
              <dd className="mt-1">
                <span className={getStatusBadgeClasses(invoice.status)}>
                  {capitalize(invoice.status)}
                </span>
              </dd>
            </div>
          </div>

          {/* Right Column */}
          <div className="space-y-3">
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Created On
              </dt>
              <dd className="mt-0.5 text-sm text-gray-900">
                {formatDate(invoice.created_on)}
              </dd>
            </div>
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Updated On
              </dt>
              <dd className="mt-0.5 text-sm text-gray-900">
                {formatDate(invoice.updated_on)}
              </dd>
            </div>
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Currency
              </dt>
              <dd className="mt-0.5 text-sm text-gray-900">
                {invoice.currency}
              </dd>
            </div>
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Total Amount
              </dt>
              <dd className="mt-0.5 text-lg font-bold text-gray-900">
                {formatCurrency(invoice.total_amount, currency)}
              </dd>
            </div>
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Balance Due
              </dt>
              <dd
                className={`mt-0.5 text-lg font-bold ${
                  invoice.balance > 0 ? 'text-red-600' : 'text-green-700'
                }`}
              >
                {formatCurrency(invoice.balance, currency)}
              </dd>
            </div>
          </div>
        </div>
      </div>

      {/* ================================================================
          Tabbed Content: Line Items / Payments / Notes & Terms
          ================================================================ */}
      <div className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <TabNav tabs={tabs} />
      </div>

      {/* ================================================================
          Action Confirmation Modal
          ================================================================ */}
      <Modal
        isVisible={activeModal !== null}
        title={modalConfig?.title ?? ''}
        onClose={closeConfirmModal}
      >
        <div className="space-y-4">
          {/* Error banner */}
          {actionError && (
            <div
              role="alert"
              className="rounded-md bg-red-50 p-3 text-sm text-red-700"
            >
              {actionError}
            </div>
          )}

          {/* Confirmation message */}
          <p className="text-sm text-gray-600">{modalConfig?.message ?? ''}</p>

          {/* Action buttons */}
          <div className="flex justify-end gap-3">
            <button
              type="button"
              onClick={closeConfirmModal}
              disabled={isMutating}
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={executeAction}
              disabled={isMutating}
              className={`inline-flex items-center rounded-md px-4 py-2 text-sm font-medium text-white shadow-sm disabled:cursor-not-allowed disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 ${
                modalConfig?.isDangerous
                  ? 'bg-red-600 hover:bg-red-700 focus-visible:outline-red-600'
                  : 'bg-blue-600 hover:bg-blue-700 focus-visible:outline-blue-600'
              }`}
            >
              {isMutating ? (
                <>
                  <span
                    className="me-2 inline-block h-4 w-4 animate-spin rounded-full border-2 border-white/30 border-t-white"
                    aria-hidden="true"
                  />
                  Processing…
                </>
              ) : (
                modalConfig?.confirmLabel ?? 'Confirm'
              )}
            </button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
