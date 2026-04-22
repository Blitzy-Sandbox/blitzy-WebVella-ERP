/**
 * QuoteDetails — Quote Detail View with Conversion-to-Invoice Action
 *
 * Displays a read-only view of a quote/estimate including line items,
 * totals, status information, and provides status-driven action buttons.
 * The KEY feature is the "Convert to Invoice" action available for
 * accepted quotes, which atomically creates a new invoice from the
 * quote data and updates the quote status to "converted".
 *
 * Replaces the monolith's `RecordDetails.cshtml.cs` pattern in the
 * Invoicing bounded-context. All data operations go through the HTTP
 * API Gateway to the Invoicing Lambda backed by RDS PostgreSQL ACID
 * transactions.
 *
 * Route: /invoicing/quotes/:id
 *
 * @module QuoteDetails
 */

import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { get, post, del } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Modal from '../../components/common/Modal';

/* ══════════════════════════════════════════════════════════════════
 * TypeScript Interfaces
 * ══════════════════════════════════════════════════════════════════ */

/** Valid statuses for a quote lifecycle. */
type QuoteStatus =
  | 'draft'
  | 'sent'
  | 'accepted'
  | 'rejected'
  | 'expired'
  | 'converted';

/** A single line item within a quote. */
interface QuoteLineItem {
  id: string;
  description: string;
  quantity: number;
  unit_price: number;
  tax_rate: number;
  amount: number;
  /** Index signature satisfies DataTable's Record<string, unknown> constraint. */
  [key: string]: unknown;
}

/** Full quote detail returned from the API. */
interface QuoteDetail {
  id: string;
  quote_number: string;
  customer_id: string;
  customer_name: string;
  issue_date: string;
  expiry_date: string;
  currency: string;
  status: QuoteStatus;
  subtotal: number;
  tax_amount: number;
  tax_rate: number;
  discount_type: 'percentage' | 'fixed';
  discount_value: number;
  discount_amount: number;
  total_amount: number;
  notes: string;
  line_items: QuoteLineItem[];
  converted_invoice_id: string | null;
  converted_invoice_number: string | null;
  created_on: string;
  updated_on: string;
}

/** Response payload from the convert-to-invoice endpoint. */
interface ConvertToInvoiceResponse {
  invoice_id: string;
  invoice_number: string;
}

/* ══════════════════════════════════════════════════════════════════
 * Constants & Helpers
 * ══════════════════════════════════════════════════════════════════ */

/** Tailwind class map for quote status badges. */
const STATUS_BADGE_STYLES: Record<QuoteStatus, string> = {
  draft: 'bg-gray-100 text-gray-800',
  sent: 'bg-blue-100 text-blue-800',
  accepted: 'bg-green-100 text-green-800',
  rejected: 'bg-red-100 text-red-800',
  expired: 'bg-orange-100 text-orange-800',
  converted: 'bg-purple-100 text-purple-800',
};

/**
 * Maps each quote status to the actions available in that state.
 * Mirrors the monolith's hook-based action dispatch from
 * RecordDetails.cshtml.cs OnPost.
 */
const ALLOWED_ACTIONS: Record<QuoteStatus, string[]> = {
  draft: ['edit', 'send', 'delete'],
  sent: ['edit', 'accept', 'reject'],
  accepted: ['convert_to_invoice'],
  rejected: ['delete'],
  expired: ['delete'],
  converted: [],
};

/**
 * Formats a numeric amount as a currency string.
 * Uses Intl.NumberFormat with fallback for unknown currencies,
 * replicating the CurrencyType pattern from Definitions.cs.
 */
function formatCurrency(amount: number, currency: string): string {
  try {
    return new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency,
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }).format(amount);
  } catch {
    return `${currency} ${amount.toFixed(2)}`;
  }
}

/**
 * Formats an ISO date string to a human-readable locale date.
 * Returns an em-dash for empty/missing values.
 */
function formatDate(dateString: string): string {
  if (!dateString) return '\u2014';
  try {
    return new Date(dateString).toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    });
  } catch {
    return dateString;
  }
}

/**
 * Extracts a user-facing error message from an ApiError or
 * ApiResponse object. Returns a sensible fallback string.
 */
function extractErrorMessage(
  err: Partial<ApiError> | null | undefined,
  fallback: string,
): string {
  if (!err) return fallback;
  if (err.message) return err.message;
  if (err.errors && Array.isArray(err.errors) && err.errors.length > 0) {
    return err.errors
      .map((e) => {
        if (typeof e === 'object' && e !== null && 'message' in e) {
          return (e as { message: string }).message;
        }
        return String(e);
      })
      .join(', ');
  }
  return fallback;
}

/* ══════════════════════════════════════════════════════════════════
 * QuoteDetails Component
 * ══════════════════════════════════════════════════════════════════ */

/**
 * Page component for viewing a single quote with all details,
 * line items, totals, and status-driven action buttons.
 *
 * Replaces the RecordDetails page model pattern for the Invoicing
 * bounded context. Data is fetched via TanStack Query from
 * `GET /v1/invoicing/quotes/:id` and mutations execute status
 * transitions and the critical convert-to-invoice action.
 */
function QuoteDetails(): React.ReactElement {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ── Modal visibility state ──────────────────────────────── */
  const [showConvertModal, setShowConvertModal] = useState(false);
  const [showSendModal, setShowSendModal] = useState(false);
  const [showAcceptModal, setShowAcceptModal] = useState(false);
  const [showRejectModal, setShowRejectModal] = useState(false);
  const [showDeleteModal, setShowDeleteModal] = useState(false);

  /* ── Action feedback state ───────────────────────────────── */
  const [actionError, setActionError] = useState<string | null>(null);

  /* ── Query: fetch quote detail ───────────────────────────── */
  const {
    data: quoteResponse,
    isLoading,
    isError,
    error: queryError,
  } = useQuery<ApiResponse<QuoteDetail>, ApiError>({
    queryKey: ['invoicing', 'quotes', id],
    queryFn: () => get<QuoteDetail>(`/v1/invoicing/quotes/${id}`),
    enabled: Boolean(id),
  });

  const quote =
    quoteResponse?.success === true ? (quoteResponse.object ?? null) : null;

  /* ── Mutation: Send quote ────────────────────────────────── */
  const sendMutation = useMutation<ApiResponse<QuoteDetail>, ApiError>({
    mutationFn: () =>
      post<QuoteDetail>(`/v1/invoicing/quotes/${id}/send`),
    onSuccess: (response) => {
      if (response.success) {
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'quotes', id] });
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'quotes'] });
        setShowSendModal(false);
        setActionError(null);
      } else {
        setActionError(
          response.message ||
            response.errors?.map((e) => e.message).join(', ') ||
            'Failed to send quote.',
        );
      }
    },
    onError: (err) => {
      setActionError(extractErrorMessage(err, 'Failed to send quote.'));
    },
  });

  /* ── Mutation: Accept quote ──────────────────────────────── */
  const acceptMutation = useMutation<ApiResponse<QuoteDetail>, ApiError>({
    mutationFn: () =>
      post<QuoteDetail>(`/v1/invoicing/quotes/${id}/accept`),
    onSuccess: (response) => {
      if (response.success) {
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'quotes', id] });
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'quotes'] });
        setShowAcceptModal(false);
        setActionError(null);
      } else {
        setActionError(
          response.message ||
            response.errors?.map((e) => e.message).join(', ') ||
            'Failed to accept quote.',
        );
      }
    },
    onError: (err) => {
      setActionError(extractErrorMessage(err, 'Failed to accept quote.'));
    },
  });

  /* ── Mutation: Reject quote ──────────────────────────────── */
  const rejectMutation = useMutation<ApiResponse<QuoteDetail>, ApiError>({
    mutationFn: () =>
      post<QuoteDetail>(`/v1/invoicing/quotes/${id}/reject`),
    onSuccess: (response) => {
      if (response.success) {
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'quotes', id] });
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'quotes'] });
        setShowRejectModal(false);
        setActionError(null);
      } else {
        setActionError(
          response.message ||
            response.errors?.map((e) => e.message).join(', ') ||
            'Failed to reject quote.',
        );
      }
    },
    onError: (err) => {
      setActionError(extractErrorMessage(err, 'Failed to reject quote.'));
    },
  });

  /* ── Mutation: Convert to Invoice (KEY ACTION) ───────────── */
  const convertMutation = useMutation<
    ApiResponse<ConvertToInvoiceResponse>,
    ApiError
  >({
    mutationFn: () =>
      post<ConvertToInvoiceResponse>(
        `/v1/invoicing/quotes/${id}/convert`,
      ),
    onSuccess: (response) => {
      if (response.success && response.object) {
        const { invoice_id, invoice_number } = response.object;
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'quotes'] });
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'invoices'] });
        queryClient.invalidateQueries({
          queryKey: ['invoicing', 'quotes', id],
        });
        setShowConvertModal(false);
        setActionError(null);
        navigate(`/invoicing/invoices/${invoice_id}`, {
          state: {
            successMessage: `Quote converted to Invoice #${invoice_number}`,
          },
        });
      } else {
        setActionError(
          response.message ||
            response.errors?.map((e) => e.message).join(', ') ||
            'Failed to convert quote to invoice.',
        );
      }
    },
    onError: (err) => {
      setActionError(
        extractErrorMessage(err, 'Failed to convert quote to invoice.'),
      );
    },
  });

  /* ── Mutation: Delete quote ──────────────────────────────── */
  const deleteMutation = useMutation<ApiResponse<void>, ApiError>({
    mutationFn: () => del<void>(`/v1/invoicing/quotes/${id}`),
    onSuccess: (response) => {
      if (response.success) {
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'quotes'] });
        setShowDeleteModal(false);
        setActionError(null);
        navigate('/invoicing/quotes');
      } else {
        setActionError(
          response.message ||
            response.errors?.map((e) => e.message).join(', ') ||
            'Failed to delete quote.',
        );
      }
    },
    onError: (err) => {
      setActionError(extractErrorMessage(err, 'Failed to delete quote.'));
    },
  });

  /* ── Computed helpers ────────────────────────────────────── */
  const isAnyMutating =
    sendMutation.isPending ||
    acceptMutation.isPending ||
    rejectMutation.isPending ||
    convertMutation.isPending ||
    deleteMutation.isPending;

  /* ── Line Items Table Column Definitions ─────────────────── */
  const lineItemColumns: DataTableColumn<QuoteLineItem>[] = [
    {
      id: 'row_number',
      label: '#',
      width: '3.5rem',
      horizontalAlign: 'center',
      accessorFn: () => '',
      cell: (_value: unknown, record: QuoteLineItem) => {
        const idx = quote
          ? quote.line_items.findIndex((item) => item.id === record.id)
          : -1;
        return (
          <span className="text-sm text-gray-500">{idx >= 0 ? idx + 1 : ''}</span>
        );
      },
    },
    {
      id: 'description',
      label: 'Description',
      accessorKey: 'description',
    },
    {
      id: 'quantity',
      label: 'Qty',
      accessorKey: 'quantity',
      width: '5rem',
      horizontalAlign: 'right',
    },
    {
      id: 'unit_price',
      label: 'Unit Price',
      accessorKey: 'unit_price',
      width: '8rem',
      horizontalAlign: 'right',
      cell: (value: unknown) =>
        formatCurrency(Number(value) || 0, quote?.currency ?? 'USD'),
    },
    {
      id: 'tax_rate',
      label: 'Tax %',
      accessorKey: 'tax_rate',
      width: '5rem',
      horizontalAlign: 'right',
      cell: (value: unknown) => `${Number(value) || 0}%`,
    },
    {
      id: 'amount',
      label: 'Amount',
      accessorKey: 'amount',
      width: '8rem',
      horizontalAlign: 'right',
      cell: (value: unknown) => (
        <span className="font-medium">
          {formatCurrency(Number(value) || 0, quote?.currency ?? 'USD')}
        </span>
      ),
    },
  ];

  /* ══════════════════════════════════════════════════════════════
   * Loading state
   * ══════════════════════════════════════════════════════════════ */
  if (isLoading) {
    return (
      <div
        className="flex items-center justify-center"
        style={{ minBlockSize: '24rem' }}
        role="status"
      >
        <div className="flex flex-col items-center gap-3">
          <div className="h-8 w-8 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600" />
          <span className="text-sm text-gray-500">
            Loading quote details&hellip;
          </span>
        </div>
      </div>
    );
  }

  /* ══════════════════════════════════════════════════════════════
   * Error / 404 state
   * ══════════════════════════════════════════════════════════════ */
  if (isError || !quote) {
    const is404 =
      (queryError as ApiError | undefined)?.status === 404 ||
      quoteResponse?.statusCode === 404;

    return (
      <div
        className="flex items-center justify-center"
        style={{ minBlockSize: '24rem' }}
      >
        <div className="text-center">
          <h2 className="text-lg font-semibold text-gray-900">
            {is404 ? 'Quote Not Found' : 'Error Loading Quote'}
          </h2>
          <p className="mt-1 text-sm text-gray-500">
            {is404
              ? 'The requested quote does not exist or has been deleted.'
              : extractErrorMessage(
                  queryError as ApiError | undefined,
                  'An unexpected error occurred.',
                )}
          </p>
          <Link
            to="/invoicing/quotes"
            className="mt-4 inline-flex items-center gap-1.5 text-sm font-medium text-blue-600 hover:text-blue-800"
          >
            &larr; Back to Quotes
          </Link>
        </div>
      </div>
    );
  }

  const actions = ALLOWED_ACTIONS[quote.status] ?? [];
  const currency = quote.currency || 'USD';

  /* ══════════════════════════════════════════════════════════════
   * Main render
   * ══════════════════════════════════════════════════════════════ */
  return (
    <div className="mx-auto max-w-5xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ─ Action error banner ─ */}
      {actionError && (
        <div className="mb-4 rounded-md bg-red-50 p-4" role="alert">
          <div className="flex">
            <svg
              className="h-5 w-5 flex-shrink-0 text-red-400"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
                clipRule="evenodd"
              />
            </svg>
            <div className="ms-3">
              <p className="text-sm text-red-700">{actionError}</p>
            </div>
            <div className="ms-auto ps-3">
              <button
                type="button"
                className="inline-flex rounded-md bg-red-50 p-1.5 text-red-500 hover:bg-red-100 focus:outline-none focus-visible:ring-2 focus-visible:ring-red-600 focus-visible:ring-offset-2"
                onClick={() => setActionError(null)}
              >
                <span className="sr-only">Dismiss</span>
                <svg
                  className="h-4 w-4"
                  viewBox="0 0 20 20"
                  fill="currentColor"
                  aria-hidden="true"
                >
                  <path d="M6.28 5.22a.75.75 0 00-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 101.06 1.06L10 11.06l3.72 3.72a.75.75 0 101.06-1.06L11.06 10l3.72-3.72a.75.75 0 00-1.06-1.06L10 8.94 6.28 5.22z" />
                </svg>
              </button>
            </div>
          </div>
        </div>
      )}

      {/* ═══ Page header ═══ */}
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-3">
          <Link
            to="/invoicing/quotes"
            className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700"
            aria-label="Back to Quotes list"
          >
            <svg
              className="h-4 w-4"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M17 10a.75.75 0 01-.75.75H5.612l4.158 3.96a.75.75 0 11-1.04 1.08l-5.5-5.25a.75.75 0 010-1.08l5.5-5.25a.75.75 0 111.04 1.08L5.612 9.25H16.25A.75.75 0 0117 10z"
                clipRule="evenodd"
              />
            </svg>
            Quotes
          </Link>
          <span className="text-gray-300" aria-hidden="true">
            /
          </span>
          <h1 className="text-xl font-semibold text-gray-900">
            Quote #{quote.quote_number}
          </h1>
          <span
            className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium capitalize ${STATUS_BADGE_STYLES[quote.status]}`}
          >
            {quote.status}
          </span>
        </div>

        {/* ─ Action buttons ─ */}
        <div className="flex flex-wrap items-center gap-2">
          {actions.includes('edit') && (
            <Link
              to={`/invoicing/quotes/${id}/edit`}
              className="inline-flex items-center rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              Edit
            </Link>
          )}

          {actions.includes('send') && (
            <button
              type="button"
              disabled={isAnyMutating}
              className="inline-flex items-center rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={() => setShowSendModal(true)}
            >
              Send Quote
            </button>
          )}

          {actions.includes('accept') && (
            <button
              type="button"
              disabled={isAnyMutating}
              className="inline-flex items-center rounded-md bg-green-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-green-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={() => setShowAcceptModal(true)}
            >
              Accept
            </button>
          )}

          {actions.includes('reject') && (
            <button
              type="button"
              disabled={isAnyMutating}
              className="inline-flex items-center rounded-md bg-red-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={() => setShowRejectModal(true)}
            >
              Reject
            </button>
          )}

          {actions.includes('convert_to_invoice') && (
            <button
              type="button"
              disabled={isAnyMutating}
              className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-green-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={() => setShowConvertModal(true)}
            >
              <svg
                className="h-4 w-4"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M15.312 11.424a5.5 5.5 0 01-9.201 2.466l-.312-.311h2.433a.75.75 0 000-1.5H4.598a.75.75 0 00-.75.75v3.634a.75.75 0 001.5 0v-2.033l.311.31a7 7 0 0011.712-3.138.75.75 0 00-1.449-.39zm.117-5.36a.75.75 0 00-.149-1.049A7 7 0 003.641 9.4a.75.75 0 101.449.39A5.5 5.5 0 0114.29 7.424l.312.311H12.17a.75.75 0 100 1.5h3.634a.75.75 0 00.75-.75V4.851a.75.75 0 00-1.5 0v2.033l-.311-.31a7.014 7.014 0 00.686-.51z"
                  clipRule="evenodd"
                />
              </svg>
              Convert to Invoice
            </button>
          )}

          {quote.status === 'converted' && quote.converted_invoice_id && (
            <Link
              to={`/invoicing/invoices/${quote.converted_invoice_id}`}
              className="inline-flex items-center gap-1.5 rounded-md bg-purple-50 px-3 py-2 text-sm font-medium text-purple-700 ring-1 ring-inset ring-purple-200 hover:bg-purple-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-purple-600"
            >
              View Invoice #{quote.converted_invoice_number}
            </Link>
          )}

          {actions.includes('delete') && (
            <button
              type="button"
              disabled={isAnyMutating}
              className="inline-flex items-center rounded-md bg-white px-3 py-2 text-sm font-medium text-red-600 shadow-sm ring-1 ring-inset ring-red-300 hover:bg-red-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={() => setShowDeleteModal(true)}
            >
              Delete
            </button>
          )}
        </div>
      </div>

      {/* ═══ Quote info grid ═══ */}
      <section
        className="mt-6 rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
        aria-label="Quote information"
      >
        <div className="grid grid-cols-1 gap-x-8 gap-y-4 sm:grid-cols-2">
          {/* Left column */}
          <div className="space-y-4">
            <dl>
              <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                Quote Number
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {quote.quote_number}
              </dd>
            </dl>
            <dl>
              <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                Customer
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {quote.customer_name || '\u2014'}
              </dd>
            </dl>
            <dl>
              <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                Issue Date
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {formatDate(quote.issue_date)}
              </dd>
            </dl>
            <dl>
              <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                Expiry Date
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {formatDate(quote.expiry_date)}
              </dd>
            </dl>
            <dl>
              <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                Status
              </dt>
              <dd className="mt-1">
                <span
                  className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium capitalize ${STATUS_BADGE_STYLES[quote.status]}`}
                >
                  {quote.status}
                </span>
              </dd>
            </dl>
          </div>

          {/* Right column */}
          <div className="space-y-4">
            <dl>
              <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                Created On
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {formatDate(quote.created_on)}
              </dd>
            </dl>
            <dl>
              <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                Updated On
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {formatDate(quote.updated_on)}
              </dd>
            </dl>
            <dl>
              <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                Currency
              </dt>
              <dd className="mt-1 text-sm text-gray-900">{currency}</dd>
            </dl>
            {quote.notes && (
              <dl>
                <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Notes
                </dt>
                <dd className="mt-1 text-sm text-gray-900">
                  {quote.notes}
                </dd>
              </dl>
            )}
            {quote.status === 'converted' && quote.converted_invoice_id && (
              <dl>
                <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Converted To
                </dt>
                <dd className="mt-1">
                  <Link
                    to={`/invoicing/invoices/${quote.converted_invoice_id}`}
                    className="text-sm font-medium text-purple-600 hover:text-purple-800"
                  >
                    Invoice #{quote.converted_invoice_number}
                  </Link>
                </dd>
              </dl>
            )}
          </div>
        </div>
      </section>

      {/* ═══ Line items ═══ */}
      <section className="mt-6" aria-label="Line items">
        <h2 className="text-base font-semibold text-gray-900">Line Items</h2>
        <div className="mt-3 overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
          {quote.line_items.length > 0 ? (
            <DataTable<QuoteLineItem>
              data={quote.line_items}
              columns={lineItemColumns}
              pageSize={0}
            />
          ) : (
            <div className="px-6 py-8 text-center text-sm text-gray-500">
              No line items on this quote.
            </div>
          )}
        </div>
      </section>

      {/* ═══ Totals ═══ */}
      <section className="mt-6" aria-label="Quote totals">
        <div className="flex justify-end">
          <div className="w-full max-w-xs space-y-2 rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
            <div className="flex items-center justify-between text-sm">
              <span className="text-gray-500">Subtotal</span>
              <span className="text-gray-900">
                {formatCurrency(quote.subtotal, currency)}
              </span>
            </div>
            <div className="flex items-center justify-between text-sm">
              <span className="text-gray-500">
                Tax ({quote.tax_rate}%)
              </span>
              <span className="text-gray-900">
                {formatCurrency(quote.tax_amount, currency)}
              </span>
            </div>
            {quote.discount_amount > 0 && (
              <div className="flex items-center justify-between text-sm">
                <span className="text-gray-500">
                  Discount
                  {quote.discount_type === 'percentage'
                    ? ` (${quote.discount_value}%)`
                    : ''}
                </span>
                <span className="text-red-600">
                  &minus;{formatCurrency(quote.discount_amount, currency)}
                </span>
              </div>
            )}
            <div className="border-t border-gray-200 pt-2">
              <div className="flex items-center justify-between">
                <span className="text-sm font-semibold text-gray-900">
                  Total
                </span>
                <span className="text-lg font-bold text-gray-900">
                  {formatCurrency(quote.total_amount, currency)}
                </span>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* ═══ Collapsible notes ═══ */}
      {quote.notes && (
        <section className="mt-6" aria-label="Quote notes">
          <details
            className="group rounded-lg border border-gray-200 bg-white shadow-sm"
            open
          >
            <summary className="flex cursor-pointer items-center justify-between px-4 py-3 text-sm font-semibold text-gray-900 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600">
              Notes
              <svg
                className="h-4 w-4 text-gray-400 transition-transform group-open:rotate-180"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z"
                  clipRule="evenodd"
                />
              </svg>
            </summary>
            <div className="border-t border-gray-200 px-4 py-3">
              <p className="whitespace-pre-wrap text-sm text-gray-700">
                {quote.notes}
              </p>
            </div>
          </details>
        </section>
      )}

      {/* ═══════════════════════════════════════════════════════
       * MODALS
       * ═══════════════════════════════════════════════════════ */}

      {/* ── Convert to Invoice Modal (KEY FEATURE) ────────── */}
      <Modal
        isVisible={showConvertModal}
        title="Convert Quote to Invoice?"
        onClose={() => {
          setShowConvertModal(false);
          setActionError(null);
        }}
        footer={
          <div className="flex items-center justify-end gap-3">
            <button
              type="button"
              className="rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
              onClick={() => {
                setShowConvertModal(false);
                setActionError(null);
              }}
              disabled={convertMutation.isPending}
            >
              Cancel
            </button>
            <button
              type="button"
              className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-green-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={() => convertMutation.mutate()}
              disabled={convertMutation.isPending}
            >
              {convertMutation.isPending ? 'Converting\u2026' : 'Convert'}
            </button>
          </div>
        }
      >
        <div className="space-y-3">
          <p className="text-sm text-gray-600">
            This will create a new invoice with all line items from this quote.
            The quote status will change to &ldquo;Converted&rdquo;.
          </p>
          <div className="rounded-md bg-gray-50 p-3">
            <p className="text-sm text-gray-700">
              <span className="font-medium">
                Quote #{quote.quote_number}
              </span>
              {' \u2192 '}
              <span className="font-medium text-green-700">
                Invoice (new)
              </span>
            </p>
          </div>
          {convertMutation.isError && (
            <p className="text-sm text-red-600" role="alert">
              {extractErrorMessage(
                convertMutation.error as ApiError | undefined,
                'Conversion failed. Please try again.',
              )}
            </p>
          )}
        </div>
      </Modal>

      {/* ── Send Quote Modal ────────────────────────────────── */}
      <Modal
        isVisible={showSendModal}
        title="Send Quote?"
        onClose={() => {
          setShowSendModal(false);
          setActionError(null);
        }}
        footer={
          <div className="flex items-center justify-end gap-3">
            <button
              type="button"
              className="rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
              onClick={() => {
                setShowSendModal(false);
                setActionError(null);
              }}
              disabled={sendMutation.isPending}
            >
              Cancel
            </button>
            <button
              type="button"
              className="rounded-md bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={() => sendMutation.mutate()}
              disabled={sendMutation.isPending}
            >
              {sendMutation.isPending ? 'Sending\u2026' : 'Send'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to send this quote to{' '}
          <span className="font-medium text-gray-900">
            {quote.customer_name}
          </span>
          ? The quote status will change to &ldquo;Sent&rdquo;.
        </p>
      </Modal>

      {/* ── Accept Quote Modal ──────────────────────────────── */}
      <Modal
        isVisible={showAcceptModal}
        title="Accept Quote?"
        onClose={() => {
          setShowAcceptModal(false);
          setActionError(null);
        }}
        footer={
          <div className="flex items-center justify-end gap-3">
            <button
              type="button"
              className="rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
              onClick={() => {
                setShowAcceptModal(false);
                setActionError(null);
              }}
              disabled={acceptMutation.isPending}
            >
              Cancel
            </button>
            <button
              type="button"
              className="rounded-md bg-green-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-green-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={() => acceptMutation.mutate()}
              disabled={acceptMutation.isPending}
            >
              {acceptMutation.isPending ? 'Accepting\u2026' : 'Accept'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Mark this quote as accepted? Once accepted, you can convert it to an
          invoice.
        </p>
      </Modal>

      {/* ── Reject Quote Modal ──────────────────────────────── */}
      <Modal
        isVisible={showRejectModal}
        title="Reject Quote?"
        onClose={() => {
          setShowRejectModal(false);
          setActionError(null);
        }}
        footer={
          <div className="flex items-center justify-end gap-3">
            <button
              type="button"
              className="rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
              onClick={() => {
                setShowRejectModal(false);
                setActionError(null);
              }}
              disabled={rejectMutation.isPending}
            >
              Cancel
            </button>
            <button
              type="button"
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={() => rejectMutation.mutate()}
              disabled={rejectMutation.isPending}
            >
              {rejectMutation.isPending ? 'Rejecting\u2026' : 'Reject'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to reject this quote? This action can be undone
          by creating a new quote.
        </p>
      </Modal>

      {/* ── Delete Quote Modal ──────────────────────────────── */}
      <Modal
        isVisible={showDeleteModal}
        title="Delete Quote?"
        onClose={() => {
          setShowDeleteModal(false);
          setActionError(null);
        }}
        footer={
          <div className="flex items-center justify-end gap-3">
            <button
              type="button"
              className="rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
              onClick={() => {
                setShowDeleteModal(false);
                setActionError(null);
              }}
              disabled={deleteMutation.isPending}
            >
              Cancel
            </button>
            <button
              type="button"
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={() => deleteMutation.mutate()}
              disabled={deleteMutation.isPending}
            >
              {deleteMutation.isPending ? 'Deleting\u2026' : 'Delete'}
            </button>
          </div>
        }
      >
        <div className="space-y-3">
          <p className="text-sm text-gray-600">
            Are you sure you want to permanently delete{' '}
            <span className="font-medium text-gray-900">
              Quote #{quote.quote_number}
            </span>
            ? This action cannot be undone.
          </p>
          <div className="rounded-md bg-red-50 p-3">
            <p className="text-xs text-red-700">
              All data associated with this quote including line items will be
              permanently removed.
            </p>
          </div>
        </div>
      </Modal>
    </div>
  );
}

export default QuoteDetails;
