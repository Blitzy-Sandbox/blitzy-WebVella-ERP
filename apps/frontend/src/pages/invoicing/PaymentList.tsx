/**
 * PaymentList.tsx — Payment Records Listing Page
 *
 * Replaces the monolith's RecordList.cshtml.cs pattern for the Invoicing
 * bounded-context.  Renders a paginated, filterable, sortable grid of payment
 * records with summary cards, payment-method badges, and cross-entity links
 * to invoices.
 *
 * All data is fetched via TanStack Query 5 through the centralized API
 * client (GET /invoicing/payments and GET /invoicing/payments/summary).
 * Filter, sort, and pagination state is driven entirely by URL search
 * parameters for bookmarkable views and browser back/forward navigation.
 */

import React, { useState, useMemo, useCallback } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';

import { get } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import { formatCurrency, formatDate } from '../../utils/formatters';

/* ────────────────────────────────────────────────────────────────────────────
 * TypeScript Interfaces
 * ──────────────────────────────────────────────────────────────────────────── */

/** Supported payment methods – mirrors the server-side enum. */
type PaymentMethod = 'cash' | 'bank_transfer' | 'credit_card' | 'check' | 'other';

/** Individual payment record returned by the Invoicing service. */
interface Payment {
  /** Index signature required by DataTable<T extends Record<string, unknown>> */
  [key: string]: unknown;
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
  created_on: string;
  updated_on: string;
}

/** Aggregate statistics returned by GET /invoicing/payments/summary. */
interface PaymentSummary {
  total_count: number;
  total_amount: number;
  this_month_count: number;
  this_month_amount: number;
  methods_breakdown: Record<PaymentMethod, number>;
}

/** Paginated list envelope returned by GET /invoicing/payments. */
interface PaymentListResponse {
  data: Payment[];
  total: number;
  page: number;
  pageSize: number;
}

/* ────────────────────────────────────────────────────────────────────────────
 * Constants
 * ──────────────────────────────────────────────────────────────────────────── */

/** Default rows per page for the payment data grid. */
const DEFAULT_PAGE_SIZE = 20;

/** Ordered list of supported payment methods for filter dropdowns. */
const PAYMENT_METHODS: PaymentMethod[] = [
  'cash',
  'bank_transfer',
  'credit_card',
  'check',
  'other',
];

/**
 * Visual configuration per payment method — Tailwind classes for badges,
 * human-readable labels, and emoji icons.
 */
const paymentMethodConfig: Record<
  PaymentMethod,
  { bg: string; text: string; label: string; icon: string }
> = {
  cash: { bg: 'bg-green-100', text: 'text-green-800', label: 'Cash', icon: '💵' },
  bank_transfer: {
    bg: 'bg-blue-100',
    text: 'text-blue-800',
    label: 'Bank Transfer',
    icon: '🏦',
  },
  credit_card: {
    bg: 'bg-purple-100',
    text: 'text-purple-800',
    label: 'Credit Card',
    icon: '💳',
  },
  check: { bg: 'bg-yellow-100', text: 'text-yellow-800', label: 'Check', icon: '📝' },
  other: { bg: 'bg-gray-100', text: 'text-gray-800', label: 'Other', icon: '📋' },
};

/* ────────────────────────────────────────────────────────────────────────────
 * Component
 * ──────────────────────────────────────────────────────────────────────────── */

/**
 * PaymentList – page-level React component for the `/invoicing/payments` route.
 *
 * Features:
 * - Summary cards (total payments, this-month, method breakdown)
 * - Collapsible filter panel (search, method, date range, invoice)
 * - Sortable, paginated DataTable with 8 columns
 * - URL-driven state for all filters, sort, and pagination
 * - Pre-filter by `?invoiceId=` when navigating from an invoice detail page
 */
export default function PaymentList(): React.JSX.Element {
  /* ── routing & URL state ─────────────────────────────────────────────── */
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();

  /* ── local UI state ──────────────────────────────────────────────────── */
  const [isFilterOpen, setIsFilterOpen] = useState<boolean>(() =>
    Boolean(
      searchParams.get('search') ||
        searchParams.get('method') ||
        searchParams.get('dateFrom') ||
        searchParams.get('dateTo') ||
        searchParams.get('invoiceId'),
    ),
  );
  const [searchInputValue, setSearchInputValue] = useState<string>(
    searchParams.get('search') ?? '',
  );

  /* ── derived URL parameters ──────────────────────────────────────────── */
  const currentPage = Number(searchParams.get('page') ?? '1');
  const pageSize = Number(
    searchParams.get('pageSize') ?? String(DEFAULT_PAGE_SIZE),
  );
  const sortBy = searchParams.get('sortBy') ?? 'payment_date';
  const sortOrder = (searchParams.get('sortOrder') ?? 'desc') as
    | 'asc'
    | 'desc';
  const searchFilter = searchParams.get('search') ?? '';
  const methodFilter = (searchParams.get('method') ?? '') as
    | PaymentMethod
    | '';
  const dateFrom = searchParams.get('dateFrom') ?? '';
  const dateTo = searchParams.get('dateTo') ?? '';
  const invoiceId = searchParams.get('invoiceId') ?? '';

  /* ── API query parameters ────────────────────────────────────────────── */
  const queryParams = useMemo(() => {
    const params: Record<string, unknown> = {
      page: currentPage,
      pageSize,
      sortBy,
      sortOrder,
    };
    if (searchFilter) params.search = searchFilter;
    if (methodFilter) params.method = methodFilter;
    if (dateFrom) params.dateFrom = dateFrom;
    if (dateTo) params.dateTo = dateTo;
    if (invoiceId) params.invoiceId = invoiceId;
    return params;
  }, [
    currentPage,
    pageSize,
    sortBy,
    sortOrder,
    searchFilter,
    methodFilter,
    dateFrom,
    dateTo,
    invoiceId,
  ]);

  /* ── TanStack Query: payment list ────────────────────────────────────── */
  const {
    data: listResponse,
    isLoading: isListLoading,
    isError: isListError,
    error: listError,
  } = useQuery({
    queryKey: ['invoicing', 'payments', queryParams],
    queryFn: async () => {
      const response = await get<PaymentListResponse>(
        '/invoicing/payments',
        queryParams,
      );
      if (!response.success) {
        throw new Error(response.message || 'Failed to fetch payments');
      }
      return response.object;
    },
  });

  /* ── TanStack Query: payment summary ─────────────────────────────────── */
  const { data: summaryData } = useQuery({
    queryKey: ['invoicing', 'payments', 'summary'],
    queryFn: async () => {
      const response = await get<PaymentSummary>(
        '/invoicing/payments/summary',
      );
      if (!response.success) {
        throw new Error(
          response.message || 'Failed to fetch payment summary',
        );
      }
      return response.object;
    },
  });

  /* ── derived data ────────────────────────────────────────────────────── */
  const payments: Payment[] = listResponse?.data ?? [];
  const totalCount: number = listResponse?.total ?? 0;
  const hasActiveFilters = Boolean(
    searchFilter || methodFilter || dateFrom || dateTo || invoiceId,
  );

  /* ── URL update helper ───────────────────────────────────────────────── */
  const updateSearchParams = useCallback(
    (updates: Record<string, string>) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        for (const [key, value] of Object.entries(updates)) {
          if (value) {
            next.set(key, value);
          } else {
            next.delete(key);
          }
        }
        // Reset to page 1 when filters change (unless page itself is being set)
        if (!('page' in updates)) {
          next.set('page', '1');
        }
        return next;
      });
    },
    [setSearchParams],
  );

  /* ── event handlers ──────────────────────────────────────────────────── */
  const handleSearchSubmit = useCallback(
    (e: React.FormEvent) => {
      e.preventDefault();
      updateSearchParams({ search: searchInputValue });
    },
    [searchInputValue, updateSearchParams],
  );

  const handleMethodFilterChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      updateSearchParams({ method: e.target.value });
    },
    [updateSearchParams],
  );

  const handleDateFromChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      updateSearchParams({ dateFrom: e.target.value });
    },
    [updateSearchParams],
  );

  const handleDateToChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      updateSearchParams({ dateTo: e.target.value });
    },
    [updateSearchParams],
  );

  const handleInvoiceFilterChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      updateSearchParams({ invoiceId: e.target.value });
    },
    [updateSearchParams],
  );

  const handleClearAllFilters = useCallback(() => {
    setSearchInputValue('');
    setSearchParams(new URLSearchParams());
  }, [setSearchParams]);

  const handleNavigateToCreate = useCallback(() => {
    navigate('/invoicing/payments/create');
  }, [navigate]);

  const handlePageChange = useCallback(
    (page: number) => {
      updateSearchParams({ page: String(page) });
    },
    [updateSearchParams],
  );

  const handlePageSizeChange = useCallback(
    (size: number) => {
      updateSearchParams({ pageSize: String(size), page: '1' });
    },
    [updateSearchParams],
  );

  /* ── DataTable column definitions ────────────────────────────────────── */
  const columns = useMemo<DataTableColumn<Payment>[]>(
    () => [
      {
        id: 'reference_number',
        label: 'Reference',
        accessorKey: 'reference_number',
        sortable: true,
        noWrap: true,
        cell: (_value: unknown, record: Payment) => (
          <Link
            to={`/invoicing/payments/${record.id}`}
            className="font-medium text-blue-600 hover:text-blue-800 hover:underline"
          >
            {record.reference_number || '—'}
          </Link>
        ),
      },
      {
        id: 'invoice_number',
        label: 'Invoice',
        accessorKey: 'invoice_number',
        sortable: true,
        noWrap: true,
        cell: (_value: unknown, record: Payment) => (
          <Link
            to={`/invoicing/invoices/${record.invoice_id}`}
            className="text-blue-600 hover:text-blue-800 hover:underline"
          >
            {record.invoice_number}
          </Link>
        ),
      },
      {
        id: 'customer_name',
        label: 'Customer',
        accessorKey: 'customer_name',
        sortable: true,
      },
      {
        id: 'payment_date',
        label: 'Date',
        accessorKey: 'payment_date',
        sortable: true,
        noWrap: true,
        cell: (_value: unknown, record: Payment) =>
          formatDate(record.payment_date),
      },
      {
        id: 'amount',
        label: 'Amount',
        accessorKey: 'amount',
        sortable: true,
        noWrap: true,
        cell: (_value: unknown, record: Payment) => (
          <span className="font-medium">
            {formatCurrency(record.amount, record.currency)}
          </span>
        ),
      },
      {
        id: 'payment_method',
        label: 'Method',
        accessorKey: 'payment_method',
        sortable: true,
        noWrap: true,
        cell: (_value: unknown, record: Payment) => {
          const config =
            paymentMethodConfig[record.payment_method] ??
            paymentMethodConfig.other;
          return (
            <span
              className={`inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-medium ${config.bg} ${config.text}`}
            >
              <span aria-hidden="true">{config.icon}</span>
              {config.label}
            </span>
          );
        },
      },
      {
        id: 'notes',
        label: 'Notes',
        accessorKey: 'notes',
        sortable: false,
        cell: (_value: unknown, record: Payment) => {
          const text = record.notes ?? '';
          if (text.length > 50) {
            return (
              <span title={text} className="text-gray-600">
                {text.slice(0, 50)}…
              </span>
            );
          }
          return <span className="text-gray-600">{text || '—'}</span>;
        },
      },
      {
        id: 'actions',
        label: 'Actions',
        sortable: false,
        noWrap: true,
        cell: (_value: unknown, record: Payment) => (
          <Link
            to={`/invoicing/payments/${record.id}`}
            className="inline-flex items-center justify-center rounded p-1.5 text-gray-500 hover:bg-gray-100 hover:text-gray-700"
            aria-label={`View payment ${record.reference_number || record.id}`}
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
              className="h-5 w-5"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M2.036 12.322a1.012 1.012 0 010-.639C3.423 7.51 7.36 4.5 12 4.5c4.638 0 8.573 3.007 9.963 7.178.07.207.07.431 0 .639C20.577 16.49 16.64 19.5 12 19.5c-4.638 0-8.573-3.007-9.963-7.178z"
              />
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"
              />
            </svg>
          </Link>
        ),
      },
    ],
    [],
  );

  /* ── render ──────────────────────────────────────────────────────────── */
  return (
    <div className="flex flex-col gap-6">
      {/* ── page header ────────────────────────────────────────────────── */}
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Payments</h1>
          {!isListLoading && (
            <p className="mt-1 text-sm text-gray-500">
              {totalCount} {totalCount === 1 ? 'payment' : 'payments'} found
            </p>
          )}
        </div>

        <div className="flex items-center gap-3">
          {/* filter toggle */}
          <button
            type="button"
            onClick={() => setIsFilterOpen((prev) => !prev)}
            className={`inline-flex items-center gap-2 rounded-lg border px-4 py-2 text-sm font-medium transition-colors ${
              isFilterOpen || hasActiveFilters
                ? 'border-blue-300 bg-blue-50 text-blue-700'
                : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
            }`}
            aria-expanded={isFilterOpen}
            aria-controls="payment-filter-panel"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M12 3c2.755 0 5.455.232 8.083.678.533.09.917.556.917 1.096v1.044a2.25 2.25 0 01-.659 1.591l-5.432 5.432a2.25 2.25 0 00-.659 1.591v2.927a2.25 2.25 0 01-1.244 2.013L9.75 21v-6.568a2.25 2.25 0 00-.659-1.591L3.659 7.409A2.25 2.25 0 013 5.818V4.774c0-.54.384-1.006.917-1.096A48.32 48.32 0 0112 3z"
              />
            </svg>
            Filters
            {hasActiveFilters && (
              <span
                className="inline-flex h-5 w-5 items-center justify-center rounded-full bg-blue-600 text-xs text-white"
                aria-label="Filters active"
              >
                !
              </span>
            )}
          </button>

          {/* create payment */}
          <button
            type="button"
            onClick={handleNavigateToCreate}
            className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M12 4.5v15m7.5-7.5h-15"
              />
            </svg>
            Record Payment
          </button>
        </div>
      </div>

      {/* ── summary cards ──────────────────────────────────────────────── */}
      {summaryData && (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {/* Total Payments */}
          <div className="rounded-lg border border-blue-200 bg-blue-50 p-4">
            <div className="flex items-center gap-3">
              <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-blue-100">
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  fill="none"
                  viewBox="0 0 24 24"
                  strokeWidth={1.5}
                  stroke="currentColor"
                  className="h-5 w-5 text-blue-600"
                  aria-hidden="true"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M2.25 18.75a60.07 60.07 0 0115.797 2.101c.727.198 1.453-.342 1.453-1.096V18.75M3.75 4.5v.75A.75.75 0 013 6h-.75m0 0v-.375c0-.621.504-1.125 1.125-1.125H20.25M2.25 6v9m18-10.5v.75c0 .414.336.75.75.75h.75m-1.5-1.5h.375c.621 0 1.125.504 1.125 1.125v9.75c0 .621-.504 1.125-1.125 1.125h-.375m1.5-1.5H21a.75.75 0 00-.75.75v.75m0 0H3.75m0 0h-.375a1.125 1.125 0 01-1.125-1.125V15m1.5 1.5v-.75A.75.75 0 003 15h-.75M15 10.5a3 3 0 11-6 0 3 3 0 016 0zm3 0h.008v.008H18V10.5zm-12 0h.008v.008H6V10.5z"
                  />
                </svg>
              </div>
              <div className="min-w-0">
                <p className="text-sm font-medium text-blue-600">
                  Total Payments
                </p>
                <p className="text-lg font-bold text-blue-900">
                  {summaryData.total_count}
                </p>
                <p className="text-sm text-blue-700">
                  {formatCurrency(summaryData.total_amount)}
                </p>
              </div>
            </div>
          </div>

          {/* This Month */}
          <div className="rounded-lg border border-green-200 bg-green-50 p-4">
            <div className="flex items-center gap-3">
              <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-green-100">
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  fill="none"
                  viewBox="0 0 24 24"
                  strokeWidth={1.5}
                  stroke="currentColor"
                  className="h-5 w-5 text-green-600"
                  aria-hidden="true"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M6.75 3v2.25M17.25 3v2.25M3 18.75V7.5a2.25 2.25 0 012.25-2.25h13.5A2.25 2.25 0 0121 7.5v11.25m-18 0A2.25 2.25 0 005.25 21h13.5A2.25 2.25 0 0021 18.75m-18 0v-7.5A2.25 2.25 0 015.25 9h13.5A2.25 2.25 0 0121 11.25v7.5"
                  />
                </svg>
              </div>
              <div className="min-w-0">
                <p className="text-sm font-medium text-green-600">
                  This Month
                </p>
                <p className="text-lg font-bold text-green-900">
                  {summaryData.this_month_count}
                </p>
                <p className="text-sm text-green-700">
                  {formatCurrency(summaryData.this_month_amount)}
                </p>
              </div>
            </div>
          </div>

          {/* Method Breakdown */}
          <div className="rounded-lg border border-gray-200 bg-white p-4 sm:col-span-2 lg:col-span-1">
            <p className="mb-2 text-sm font-medium text-gray-600">
              By Method
            </p>
            <div className="flex flex-wrap gap-2">
              {PAYMENT_METHODS.map((method) => {
                const config = paymentMethodConfig[method];
                const amount =
                  summaryData.methods_breakdown?.[method] ?? 0;
                return (
                  <span
                    key={method}
                    className={`inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-xs font-medium ${config.bg} ${config.text}`}
                  >
                    <span aria-hidden="true">{config.icon}</span>
                    {config.label}: {formatCurrency(amount)}
                  </span>
                );
              })}
            </div>
          </div>
        </div>
      )}

      {/* ── filter panel (collapsible) ─────────────────────────────────── */}
      {isFilterOpen && (
        <section
          id="payment-filter-panel"
          className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
          aria-label="Payment filters"
        >
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {/* Search */}
            <div className="sm:col-span-2 lg:col-span-1">
              <label
                htmlFor="payment-search"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Search
              </label>
              <form onSubmit={handleSearchSubmit} className="flex gap-2">
                <input
                  id="payment-search"
                  type="text"
                  value={searchInputValue}
                  onChange={(e) => setSearchInputValue(e.target.value)}
                  placeholder="Reference, invoice, customer…"
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                />
                <button
                  type="submit"
                  className="shrink-0 rounded-md bg-gray-100 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-200"
                >
                  Go
                </button>
              </form>
            </div>

            {/* Payment Method */}
            <div>
              <label
                htmlFor="payment-method-filter"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Payment Method
              </label>
              <select
                id="payment-method-filter"
                value={methodFilter}
                onChange={handleMethodFilterChange}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              >
                <option value="">All Methods</option>
                {PAYMENT_METHODS.map((method) => (
                  <option key={method} value={method}>
                    {paymentMethodConfig[method].label}
                  </option>
                ))}
              </select>
            </div>

            {/* Date From */}
            <div>
              <label
                htmlFor="payment-date-from"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Date From
              </label>
              <input
                id="payment-date-from"
                type="date"
                value={dateFrom}
                onChange={handleDateFromChange}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>

            {/* Date To */}
            <div>
              <label
                htmlFor="payment-date-to"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Date To
              </label>
              <input
                id="payment-date-to"
                type="date"
                value={dateTo}
                onChange={handleDateToChange}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>

            {/* Invoice filter */}
            <div>
              <label
                htmlFor="payment-invoice-filter"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Invoice
              </label>
              <input
                id="payment-invoice-filter"
                type="text"
                value={invoiceId}
                onChange={handleInvoiceFilterChange}
                placeholder="Invoice ID or number"
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>
          </div>

          {/* Clear all link */}
          {hasActiveFilters && (
            <div className="mt-3 flex justify-end">
              <button
                type="button"
                onClick={handleClearAllFilters}
                className="text-sm font-medium text-blue-600 hover:text-blue-800 hover:underline"
              >
                Clear All
              </button>
            </div>
          )}
        </section>
      )}

      {/* ── invoice filter banner ──────────────────────────────────────── */}
      {invoiceId && (
        <div className="flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-4 py-2 text-sm text-blue-800">
          <svg
            xmlns="http://www.w3.org/2000/svg"
            fill="none"
            viewBox="0 0 24 24"
            strokeWidth={1.5}
            stroke="currentColor"
            className="h-4 w-4 shrink-0"
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25"
            />
          </svg>
          <span>
            Showing payments for invoice{' '}
            <strong className="font-semibold">{invoiceId}</strong>
          </span>
          <button
            type="button"
            onClick={() => updateSearchParams({ invoiceId: '' })}
            className="ml-auto text-blue-600 hover:text-blue-800 hover:underline"
            aria-label="Remove invoice filter"
          >
            Remove filter
          </button>
        </div>
      )}

      {/* ── error state ────────────────────────────────────────────────── */}
      {isListError && (
        <div
          role="alert"
          className="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-800"
        >
          <p className="font-medium">Failed to load payments</p>
          <p className="mt-1">
            {listError instanceof Error
              ? listError.message
              : 'An unexpected error occurred. Please try again.'}
          </p>
        </div>
      )}

      {/* ── data table ─────────────────────────────────────────────────── */}
      <DataTable<Payment>
        data={payments}
        columns={columns}
        totalCount={totalCount}
        pageSize={pageSize}
        currentPage={currentPage}
        onPageChange={handlePageChange}
        onPageSizeChange={handlePageSizeChange}
        loading={isListLoading}
        emptyText="No payments found. Adjust your filters or record a new payment."
        striped
        hover
      />
    </div>
  );
}
