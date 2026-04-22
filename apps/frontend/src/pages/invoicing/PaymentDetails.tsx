/**
 * PaymentDetails.tsx — Payment Detail View
 *
 * React 19 page component for viewing a single payment record's details,
 * including the linked invoice information. Replaces the monolith's
 * RecordDetails.cshtml.cs pattern in the Invoicing bounded-context
 * (RDS PostgreSQL with ACID transactions).
 *
 * Route: /invoicing/payments/:id
 * Lazy-loaded via React.lazy() — protected route (billing operations role).
 *
 * Key behaviours preserved from the monolith:
 * - Record existence check with 404 handling (RecordDetails.OnGet → RecordsExists)
 * - Delete operation with confirmation (RecordDetails.OnPost → RecordManager.DeleteRecord)
 * - ACID cascade: deleting a payment atomically restores the invoice balance
 * - Linked invoice data via relation query (RecordManager relation processing)
 * - Currency formatting (Definitions.cs CurrencyType pattern)
 */

import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { get, del } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import Modal from '../../components/common/Modal';

/* ------------------------------------------------------------------ */
/*  TypeScript interfaces                                              */
/* ------------------------------------------------------------------ */

/** Full payment record returned by GET /v1/invoicing/payments/:id */
interface PaymentDetail {
  id: string;
  reference_number: string;
  invoice_id: string;
  invoice_number: string;
  customer_id: string;
  customer_name: string;
  payment_date: string;
  amount: number;
  payment_method: PaymentMethod;
  currency: string;
  notes: string;
  invoice_total: number;
  invoice_balance_before: number;
  invoice_balance_after: number;
  invoice_status: string;
  created_on: string;
  updated_on: string;
}

/** Supported payment method discriminator */
type PaymentMethod =
  | 'cash'
  | 'bank_transfer'
  | 'credit_card'
  | 'check'
  | 'other';

/** Visual configuration for each payment method badge */
const paymentMethodConfig: Record<
  PaymentMethod,
  { bg: string; text: string; label: string }
> = {
  cash: { bg: 'bg-green-100', text: 'text-green-800', label: 'Cash' },
  bank_transfer: {
    bg: 'bg-blue-100',
    text: 'text-blue-800',
    label: 'Bank Transfer',
  },
  credit_card: {
    bg: 'bg-purple-100',
    text: 'text-purple-800',
    label: 'Credit Card',
  },
  check: { bg: 'bg-yellow-100', text: 'text-yellow-800', label: 'Check' },
  other: { bg: 'bg-gray-100', text: 'text-gray-800', label: 'Other' },
};

/* ------------------------------------------------------------------ */
/*  Formatting helpers                                                 */
/* ------------------------------------------------------------------ */

/**
 * Format a numeric amount as a locale-aware currency string.
 * Uses Intl.NumberFormat to respect the CurrencyType pattern
 * (Symbol, Code, DecimalDigits, SymbolPlacement) from the monolith's
 * Definitions.cs.
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
    // Fallback for unknown currency codes
    return `${currency} ${amount.toFixed(2)}`;
  }
}

/**
 * Format an ISO date string to a human-readable locale date.
 */
function formatDate(dateString: string | null | undefined): string {
  if (!dateString) return '—';
  try {
    return new Intl.DateTimeFormat('en-US', {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    }).format(new Date(dateString));
  } catch {
    return dateString;
  }
}

/**
 * Resolve method configuration, gracefully defaulting to "other"
 * if the API returns an unknown payment method value.
 */
function getMethodConfig(method: string) {
  return (
    paymentMethodConfig[method as PaymentMethod] ?? paymentMethodConfig.other
  );
}

/* ------------------------------------------------------------------ */
/*  Component                                                          */
/* ------------------------------------------------------------------ */

/**
 * PaymentDetails — read-only detail view for a single payment record,
 * with linked invoice information and a delete action (with confirmation).
 */
export default function PaymentDetails() {
  /* ---- routing --------------------------------------------------- */
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ---- local state ----------------------------------------------- */
  const [isDeleteModalOpen, setIsDeleteModalOpen] = useState(false);

  /* ---- data fetching --------------------------------------------- */
  const {
    data: paymentResponse,
    isLoading,
    isError,
    error,
  } = useQuery<ApiResponse<PaymentDetail>, ApiError>({
    queryKey: ['invoicing', 'payments', id],
    queryFn: () => get<PaymentDetail>(`/invoicing/payments/${id}`),
    enabled: Boolean(id),
    retry: (failureCount, err) => {
      // Do not retry on 404 — the resource genuinely doesn't exist
      if (err && (err as ApiError).status === 404) return false;
      return failureCount < 2;
    },
  });

  /* ---- delete mutation ------------------------------------------- */
  const deleteMutation = useMutation<ApiResponse<void>, ApiError, void>({
    mutationFn: () => del<void>(`/invoicing/payments/${id}`),
    onSuccess: () => {
      // Invalidate both payment and invoice caches so that list views
      // and invoice detail views reflect the restored balance.
      queryClient.invalidateQueries({ queryKey: ['invoicing', 'payments'] });
      queryClient.invalidateQueries({ queryKey: ['invoicing', 'invoices'] });
      navigate('/invoicing/payments');
    },
  });

  /* ---- derived data ---------------------------------------------- */
  const payment: PaymentDetail | undefined = paymentResponse?.success
    ? paymentResponse.object
    : undefined;

  const is404 =
    isError && (error as ApiError | undefined)?.status === 404;

  /* ---- event handlers -------------------------------------------- */
  function handleDeleteConfirm() {
    deleteMutation.mutate();
  }

  function handleDeleteCancel() {
    setIsDeleteModalOpen(false);
  }

  /* ================================================================ */
  /*  RENDER — Loading state                                          */
  /* ================================================================ */
  if (isLoading) {
    return (
      <div className="flex min-h-[60vh] items-center justify-center">
        <div className="text-center">
          <div
            className="mx-auto mb-4 h-10 w-10 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600"
            role="status"
            aria-label="Loading payment details"
          />
          <p className="text-sm text-gray-500">Loading payment details…</p>
        </div>
      </div>
    );
  }

  /* ================================================================ */
  /*  RENDER — 404 Not Found                                          */
  /* ================================================================ */
  if (is404) {
    return (
      <div className="flex min-h-[60vh] flex-col items-center justify-center gap-4">
        <div className="rounded-lg bg-red-50 p-8 text-center">
          <h2 className="mb-2 text-lg font-semibold text-red-800">
            Payment Not Found
          </h2>
          <p className="mb-4 text-sm text-red-600">
            The payment record you are looking for does not exist or has been
            removed.
          </p>
          <Link
            to="/invoicing/payments"
            className="inline-flex items-center gap-1 text-sm font-medium text-blue-600 hover:underline"
          >
            ← Back to Payments
          </Link>
        </div>
      </div>
    );
  }

  /* ================================================================ */
  /*  RENDER — General error                                          */
  /* ================================================================ */
  if (isError || !payment) {
    const errorMessage =
      (error as ApiError | undefined)?.message ??
      paymentResponse?.message ??
      'An unexpected error occurred while loading payment details.';

    return (
      <div className="flex min-h-[60vh] flex-col items-center justify-center gap-4">
        <div className="rounded-lg bg-red-50 p-8 text-center">
          <h2 className="mb-2 text-lg font-semibold text-red-800">
            Error Loading Payment
          </h2>
          <p className="mb-4 text-sm text-red-600">{errorMessage}</p>
          <Link
            to="/invoicing/payments"
            className="inline-flex items-center gap-1 text-sm font-medium text-blue-600 hover:underline"
          >
            ← Back to Payments
          </Link>
        </div>
      </div>
    );
  }

  /* ================================================================ */
  /*  RENDER — Success                                                */
  /* ================================================================ */
  const methodCfg = getMethodConfig(payment.payment_method);
  const restorationAmount = payment.amount;
  const title = payment.reference_number
    ? `Payment #${payment.reference_number}`
    : 'Payment Details';

  return (
    <section className="mx-auto w-full max-w-5xl px-4 py-6 sm:px-6 lg:px-8">
      {/* -------------------------------------------------------------- */}
      {/*  Page header                                                    */}
      {/* -------------------------------------------------------------- */}
      <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex flex-col gap-2">
          {/* Back link */}
          <Link
            to="/invoicing/payments"
            className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              className="h-4 w-4"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M12.707 5.293a1 1 0 010 1.414L9.414 10l3.293 3.293a1 1 0 01-1.414 1.414l-4-4a1 1 0 010-1.414l4-4a1 1 0 011.414 0z"
                clipRule="evenodd"
              />
            </svg>
            Back to Payments
          </Link>

          {/* Title + badge row */}
          <div className="flex flex-wrap items-center gap-3">
            <h1 className="text-2xl font-bold text-gray-900">{title}</h1>
            <span
              className={`inline-flex items-center rounded-full px-3 py-0.5 text-xs font-medium ${methodCfg.bg} ${methodCfg.text}`}
            >
              {methodCfg.label}
            </span>
          </div>
        </div>

        {/* Header action — delete button */}
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => setIsDeleteModalOpen(true)}
            disabled={deleteMutation.isPending}
            className="inline-flex items-center gap-2 rounded-md border border-red-300 bg-white px-4 py-2 text-sm font-medium text-red-700 shadow-sm hover:bg-red-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              className="h-4 w-4"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M9 2a1 1 0 00-.894.553L7.382 4H4a1 1 0 000 2v10a2 2 0 002 2h8a2 2 0 002-2V6a1 1 0 100-2h-3.382l-.724-1.447A1 1 0 0011 2H9zM7 8a1 1 0 012 0v6a1 1 0 11-2 0V8zm5-1a1 1 0 00-1 1v6a1 1 0 102 0V8a1 1 0 00-1-1z"
                clipRule="evenodd"
              />
            </svg>
            Delete Payment
          </button>
        </div>
      </div>

      {/* -------------------------------------------------------------- */}
      {/*  Payment info section — two-column grid                        */}
      {/* -------------------------------------------------------------- */}
      <div className="mb-6 grid grid-cols-1 gap-6 md:grid-cols-2">
        {/* Left column */}
        <div className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <h2 className="mb-4 text-sm font-semibold uppercase tracking-wider text-gray-500">
            Payment Information
          </h2>

          <dl className="space-y-4">
            {/* Reference Number */}
            <div>
              <dt className="text-xs font-medium text-gray-500">
                Reference Number
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {payment.reference_number || '—'}
              </dd>
            </div>

            {/* Payment Date */}
            <div>
              <dt className="text-xs font-medium text-gray-500">
                Payment Date
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {formatDate(payment.payment_date)}
              </dd>
            </div>

            {/* Amount — large and prominent */}
            <div>
              <dt className="text-xs font-medium text-gray-500">Amount</dt>
              <dd className="mt-1 text-2xl font-bold text-gray-900">
                {formatCurrency(payment.amount, payment.currency)}
              </dd>
            </div>

            {/* Payment Method */}
            <div>
              <dt className="text-xs font-medium text-gray-500">
                Payment Method
              </dt>
              <dd className="mt-1">
                <span
                  className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${methodCfg.bg} ${methodCfg.text}`}
                >
                  {methodCfg.label}
                </span>
              </dd>
            </div>
          </dl>
        </div>

        {/* Right column */}
        <div className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <h2 className="mb-4 text-sm font-semibold uppercase tracking-wider text-gray-500">
            Record Details
          </h2>

          <dl className="space-y-4">
            {/* Created On */}
            <div>
              <dt className="text-xs font-medium text-gray-500">Created On</dt>
              <dd className="mt-1 text-sm text-gray-900">
                {formatDate(payment.created_on)}
              </dd>
            </div>

            {/* Updated On */}
            <div>
              <dt className="text-xs font-medium text-gray-500">Updated On</dt>
              <dd className="mt-1 text-sm text-gray-900">
                {formatDate(payment.updated_on)}
              </dd>
            </div>

            {/* Notes */}
            <div>
              <dt className="text-xs font-medium text-gray-500">Notes</dt>
              <dd className="mt-1 whitespace-pre-wrap text-sm text-gray-900">
                {payment.notes || '—'}
              </dd>
            </div>
          </dl>
        </div>
      </div>

      {/* -------------------------------------------------------------- */}
      {/*  Linked invoice section                                        */}
      {/* -------------------------------------------------------------- */}
      <div className="rounded-lg border border-blue-200 bg-blue-50 p-6 shadow-sm">
        <h2 className="mb-4 text-sm font-semibold uppercase tracking-wider text-blue-700">
          Linked Invoice
        </h2>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {/* Invoice Number — clickable link */}
          <div>
            <dt className="text-xs font-medium text-blue-600">Invoice #</dt>
            <dd className="mt-1">
              <Link
                to={`/invoicing/invoices/${payment.invoice_id}`}
                className="text-sm font-semibold text-blue-700 underline decoration-blue-300 underline-offset-2 hover:text-blue-900 hover:decoration-blue-700"
              >
                {payment.invoice_number || '—'}
              </Link>
            </dd>
          </div>

          {/* Customer Name */}
          <div>
            <dt className="text-xs font-medium text-blue-600">Customer</dt>
            <dd className="mt-1 text-sm text-gray-900">
              {payment.customer_name || '—'}
            </dd>
          </div>

          {/* Invoice Total */}
          <div>
            <dt className="text-xs font-medium text-blue-600">
              Invoice Total
            </dt>
            <dd className="mt-1 text-sm font-medium text-gray-900">
              {formatCurrency(payment.invoice_total, payment.currency)}
            </dd>
          </div>

          {/* Invoice Balance Before */}
          <div>
            <dt className="text-xs font-medium text-blue-600">
              Balance Before Payment
            </dt>
            <dd className="mt-1 text-sm text-gray-900">
              {formatCurrency(
                payment.invoice_balance_before,
                payment.currency,
              )}
            </dd>
          </div>

          {/* Invoice Balance After */}
          <div>
            <dt className="text-xs font-medium text-blue-600">
              Balance After Payment
            </dt>
            <dd className="mt-1 text-sm font-medium text-gray-900">
              {payment.invoice_balance_after === 0 ? (
                <span className="text-green-700">
                  {formatCurrency(0, payment.currency)}{' '}
                  <span className="ml-1 inline-flex items-center rounded-full bg-green-100 px-2 py-0.5 text-xs font-medium text-green-800">
                    Fully Paid
                  </span>
                </span>
              ) : (
                <span className="text-red-700">
                  {formatCurrency(
                    payment.invoice_balance_after,
                    payment.currency,
                  )}
                </span>
              )}
            </dd>
          </div>

          {/* Invoice Status */}
          <div>
            <dt className="text-xs font-medium text-blue-600">
              Invoice Status
            </dt>
            <dd className="mt-1">
              <span className="inline-flex items-center rounded-full bg-white px-2.5 py-0.5 text-xs font-medium capitalize text-gray-700 ring-1 ring-inset ring-gray-300">
                {payment.invoice_status || '—'}
              </span>
            </dd>
          </div>
        </div>
      </div>

      {/* -------------------------------------------------------------- */}
      {/*  Delete confirmation modal                                     */}
      {/* -------------------------------------------------------------- */}
      <Modal
        isVisible={isDeleteModalOpen}
        onClose={handleDeleteCancel}
        title="Delete Payment?"
        footer={
          <div className="flex justify-end gap-3">
            <button
              type="button"
              onClick={handleDeleteCancel}
              disabled={deleteMutation.isPending}
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleDeleteConfirm}
              disabled={deleteMutation.isPending}
              className="inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {deleteMutation.isPending ? (
                <>
                  <span
                    className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                    aria-hidden="true"
                  />
                  Deleting…
                </>
              ) : (
                'Delete'
              )}
            </button>
          </div>
        }
      >
        <div className="space-y-3">
          <p className="text-sm text-gray-600">
            Deleting this payment will restore{' '}
            <span className="font-semibold text-gray-900">
              {formatCurrency(restorationAmount, payment.currency)}
            </span>{' '}
            to the invoice balance for{' '}
            <span className="font-semibold text-gray-900">
              Invoice #{payment.invoice_number}
            </span>
            . This action cannot be undone.
          </p>

          {/* Balance impact preview */}
          <div className="rounded-md bg-gray-50 p-3">
            <p className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Balance Impact
            </p>
            <div className="mt-2 flex items-center gap-2 text-sm">
              <span className="text-gray-700">
                {formatCurrency(
                  payment.invoice_balance_after,
                  payment.currency,
                )}
              </span>
              <svg
                xmlns="http://www.w3.org/2000/svg"
                className="h-4 w-4 text-gray-400"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M12.293 5.293a1 1 0 011.414 0l4 4a1 1 0 010 1.414l-4 4a1 1 0 01-1.414-1.414L14.586 11H3a1 1 0 110-2h11.586l-2.293-2.293a1 1 0 010-1.414z"
                  clipRule="evenodd"
                />
              </svg>
              <span className="font-semibold text-red-700">
                {formatCurrency(
                  payment.invoice_balance_before,
                  payment.currency,
                )}
              </span>
            </div>
          </div>

          {/* Mutation error display */}
          {deleteMutation.isError && (
            <div
              className="rounded-md bg-red-50 p-3 text-sm text-red-700"
              role="alert"
            >
              {(deleteMutation.error as ApiError | undefined)?.message ??
                'Failed to delete payment. Please try again.'}
            </div>
          )}
        </div>
      </Modal>
    </section>
  );
}
