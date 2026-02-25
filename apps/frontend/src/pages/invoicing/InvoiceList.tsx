/**
 * InvoiceList.tsx — Invoice Listing Page with Status Filters, Search, and Summary Cards
 *
 * Replaces the monolith's RecordList.cshtml.cs + PcGrid ViewComponent pattern for
 * invoice entities within the Invoicing bounded-context service (RDS PostgreSQL backed).
 *
 * Provides:
 * - Aggregate summary cards (Total, Paid, Pending, Overdue)
 * - Status tab filtering (All | Draft | Sent | Paid | Overdue | Void)
 * - Full-text search on invoice number / customer name
 * - Date range and customer filters (collapsible panel)
 * - Sortable, paginated DataTable with currency-formatted amounts
 * - URL-driven state for bookmarkable filter/sort/pagination views
 * - TanStack Query for server state with stale-while-revalidate caching
 *
 * @module pages/invoicing/InvoiceList
 */

import { useState, useMemo, useCallback } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';
import { get } from '../../api/client';
import { DataTable, type DataTableColumn } from '../../components/data-table/DataTable';
import { formatCurrency, formatDate } from '../../utils/formatters';

/* ═══════════════════════════════════════════════════════════════════════════
   TypeScript Interfaces
   ═══════════════════════════════════════════════════════════════════════════ */

/** Invoice status values matching the Invoicing service domain model. */
type InvoiceStatus = 'draft' | 'sent' | 'paid' | 'overdue' | 'void' | 'partial';

/** Single invoice record returned by GET /v1/invoicing/invoices. */
interface Invoice {
  [key: string]: unknown;
  id: string;
  invoice_number: string;
  customer_id: string;
  customer_name: string;
  issue_date: string;
  due_date: string;
  total_amount: number;
  paid_amount: number;
  balance: number;
  status: InvoiceStatus;
  currency: string;
  line_items_count: number;
  created_on: string;
  updated_on: string;
}

/** Aggregate statistics from GET /v1/invoicing/invoices/summary. */
interface InvoiceSummary {
  total_count: number;
  total_amount: number;
  paid_amount: number;
  overdue_amount: number;
  draft_count: number;
  sent_count: number;
  paid_count: number;
  overdue_count: number;
}

/** Query parameters sent to the invoice list endpoint. */
interface InvoiceListParams {
  page?: number;
  pageSize?: number;
  sortBy?: string;
  sortOrder?: 'asc' | 'desc';
  search?: string;
  status?: InvoiceStatus;
  dateFrom?: string;
  dateTo?: string;
  customerId?: string;
}

/** Paginated response envelope from GET /v1/invoicing/invoices. */
interface InvoiceListResponse {
  data: Invoice[];
  total: number;
  page: number;
  pageSize: number;
}

/* ═══════════════════════════════════════════════════════════════════════════
   Constants & Configuration
   ═══════════════════════════════════════════════════════════════════════════ */

/** Color-coded badge configuration for each invoice status. */
const INVOICE_STATUS_CONFIG: Record<InvoiceStatus, { bg: string; text: string; label: string }> = {
  draft:   { bg: 'bg-gray-100',   text: 'text-gray-800',   label: 'Draft' },
  sent:    { bg: 'bg-blue-100',   text: 'text-blue-800',   label: 'Sent' },
  paid:    { bg: 'bg-green-100',  text: 'text-green-800',  label: 'Paid' },
  overdue: { bg: 'bg-red-100',    text: 'text-red-800',    label: 'Overdue' },
  void:    { bg: 'bg-gray-100',   text: 'text-gray-500 line-through', label: 'Void' },
  partial: { bg: 'bg-yellow-100', text: 'text-yellow-800', label: 'Partial' },
};

/** Filter tab definitions for the status row. */
const STATUS_TABS: ReadonlyArray<{ key: InvoiceStatus | 'all'; label: string }> = [
  { key: 'all',     label: 'All' },
  { key: 'draft',   label: 'Draft' },
  { key: 'sent',    label: 'Sent' },
  { key: 'paid',    label: 'Paid' },
  { key: 'overdue', label: 'Overdue' },
  { key: 'void',    label: 'Void' },
] as const;

/** Default page size for the invoice listing (AAP §0.8.2 specifies 20). */
const DEFAULT_PAGE_SIZE = 20;

/* ═══════════════════════════════════════════════════════════════════════════
   InvoiceList Component
   ═══════════════════════════════════════════════════════════════════════════ */

/**
 * Invoice listing page with summary cards, collapsible filter panel,
 * status tabs, search, date range, customer filter, and a sortable
 * paginated DataTable.
 *
 * Replaces the monolith's RecordList.cshtml.cs OnGet() lifecycle with
 * client-side TanStack Query data fetching, and the PcGrid ViewComponent
 * with the shared DataTable component.
 */
function InvoiceList(): React.JSX.Element {
  /* ── URL-driven state ───────────────────────────────────────── */
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();

  const currentPage  = Number(searchParams.get('page')) || 1;
  const pageSize     = Number(searchParams.get('pageSize')) || DEFAULT_PAGE_SIZE;
  const sortBy       = searchParams.get('sortBy') || 'issue_date';
  const sortOrder    = (searchParams.get('sortOrder') as 'asc' | 'desc') || 'desc';
  const searchTerm   = searchParams.get('search') || '';
  const statusFilter = (searchParams.get('status') as InvoiceStatus | null) || undefined;
  const dateFrom     = searchParams.get('dateFrom') || '';
  const dateTo       = searchParams.get('dateTo') || '';
  const customerId   = searchParams.get('customerId') || '';

  const hasActiveFilters = !!(searchTerm || statusFilter || dateFrom || dateTo || customerId);

  /* ── Local UI state ─────────────────────────────────────────── */
  const [isFilterOpen, setIsFilterOpen] = useState<boolean>(hasActiveFilters);
  const [searchInput, setSearchInput]   = useState<string>(searchTerm);
  const [customerInput, setCustomerInput] = useState<string>(customerId);

  /* ── Query params memo ──────────────────────────────────────── */
  const queryParams: InvoiceListParams = useMemo(
    () => ({
      page: currentPage,
      pageSize,
      sortBy,
      sortOrder,
      ...(searchTerm  ? { search: searchTerm }   : {}),
      ...(statusFilter ? { status: statusFilter } : {}),
      ...(dateFrom    ? { dateFrom }              : {}),
      ...(dateTo      ? { dateTo }                : {}),
      ...(customerId  ? { customerId }            : {}),
    }),
    [currentPage, pageSize, sortBy, sortOrder, searchTerm, statusFilter, dateFrom, dateTo, customerId],
  );

  /* ── Data Fetching: Invoice List ────────────────────────────── */
  const {
    data: invoiceListResponse,
    isLoading: isListLoading,
    isError: isListError,
    error: listError,
  } = useQuery({
    queryKey: ['invoicing', 'invoices', queryParams],
    queryFn: async (): Promise<InvoiceListResponse> => {
      const params: Record<string, unknown> = { ...queryParams };
      const response = await get<InvoiceListResponse>('/v1/invoicing/invoices', params);
      if (!response.success) {
        throw new Error(response.message || 'Failed to fetch invoices');
      }
      return (
        response.object ?? { data: [], total: 0, page: currentPage, pageSize }
      );
    },
    placeholderData: (previousData) => previousData,
  });

  /* ── Data Fetching: Invoice Summary ─────────────────────────── */
  const { data: summary } = useQuery({
    queryKey: ['invoicing', 'invoices', 'summary'],
    queryFn: async (): Promise<InvoiceSummary> => {
      const response = await get<InvoiceSummary>('/v1/invoicing/invoices/summary');
      if (!response.success) {
        throw new Error(response.message || 'Failed to fetch invoice summary');
      }
      return (
        response.object ?? {
          total_count: 0,
          total_amount: 0,
          paid_amount: 0,
          overdue_amount: 0,
          draft_count: 0,
          sent_count: 0,
          paid_count: 0,
          overdue_count: 0,
        }
      );
    },
    staleTime: 30_000,
  });

  /* ── Derived data ───────────────────────────────────────────── */
  const invoices   = invoiceListResponse?.data ?? [];
  const totalCount = invoiceListResponse?.total ?? 0;

  /** Pending = total − paid − overdue. */
  const pendingAmount = useMemo(() => {
    if (!summary) return 0;
    return summary.total_amount - summary.paid_amount - summary.overdue_amount;
  }, [summary]);

  /* ── URL update helper ──────────────────────────────────────── */
  const updateFilterParams = useCallback(
    (updates: Record<string, string | undefined>) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        /* Reset to page 1 whenever a filter changes. */
        next.set('page', '1');
        for (const [key, value] of Object.entries(updates)) {
          if (value === undefined || value === '') {
            next.delete(key);
          } else {
            next.set(key, value);
          }
        }
        return next;
      });
    },
    [setSearchParams],
  );

  /* ── Event handlers ─────────────────────────────────────────── */

  const handleSearchSubmit = useCallback(
    (e?: React.FormEvent) => {
      if (e) e.preventDefault();
      updateFilterParams({ search: searchInput || undefined });
    },
    [searchInput, updateFilterParams],
  );

  const handleStatusFilter = useCallback(
    (status: InvoiceStatus | 'all') => {
      updateFilterParams({ status: status === 'all' ? undefined : status });
    },
    [updateFilterParams],
  );

  const handleDateFromChange = useCallback(
    (value: string) => updateFilterParams({ dateFrom: value || undefined }),
    [updateFilterParams],
  );

  const handleDateToChange = useCallback(
    (value: string) => updateFilterParams({ dateTo: value || undefined }),
    [updateFilterParams],
  );

  const handleCustomerChange = useCallback(
    (value: string) => {
      setCustomerInput(value);
      updateFilterParams({ customerId: value || undefined });
    },
    [updateFilterParams],
  );

  const handleClearFilters = useCallback(() => {
    setSearchInput('');
    setCustomerInput('');
    setSearchParams(new URLSearchParams());
  }, [setSearchParams]);

  const handleCreateInvoice = useCallback(() => {
    navigate('/invoicing/invoices/create');
  }, [navigate]);

  /* ── DataTable column definitions ───────────────────────────── */
  const columns: DataTableColumn<Invoice>[] = useMemo(
    () => [
      {
        id: 'invoice_number',
        label: 'Invoice #',
        accessorKey: 'invoice_number',
        sortable: true,
        noWrap: true,
        cell: (_value: unknown, record: Invoice) => (
          <Link
            to={`/invoicing/invoices/${record.id}`}
            className="font-medium text-blue-600 hover:text-blue-800 hover:underline"
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
        cell: (value: unknown) => (
          <span className="text-gray-900">{String(value ?? '')}</span>
        ),
      },
      {
        id: 'issue_date',
        label: 'Issue Date',
        accessorKey: 'issue_date',
        sortable: true,
        noWrap: true,
        cell: (value: unknown) => (
          <span className="text-gray-700">
            {value ? formatDate(String(value), 'short') : '—'}
          </span>
        ),
      },
      {
        id: 'due_date',
        label: 'Due Date',
        accessorKey: 'due_date',
        sortable: true,
        noWrap: true,
        cell: (value: unknown, record: Invoice) => {
          const isOverdue =
            record.status !== 'paid' &&
            record.status !== 'void' &&
            !!value &&
            new Date(String(value)) < new Date();
          return (
            <span className={isOverdue ? 'font-medium text-red-600' : 'text-gray-700'}>
              {value ? formatDate(String(value), 'short') : '—'}
              {isOverdue && (
                <span
                  className="ms-1 inline-block text-xs text-red-500"
                  title="Overdue"
                  aria-label="Overdue"
                >
                  ●
                </span>
              )}
            </span>
          );
        },
      },
      {
        id: 'total_amount',
        label: 'Amount',
        accessorKey: 'total_amount',
        sortable: true,
        horizontalAlign: 'right',
        noWrap: true,
        cell: (value: unknown, record: Invoice) => (
          <span className="font-medium text-gray-900">
            {formatCurrency(value as number | null, record.currency)}
          </span>
        ),
      },
      {
        id: 'paid_amount',
        label: 'Paid',
        accessorKey: 'paid_amount',
        sortable: true,
        horizontalAlign: 'right',
        noWrap: true,
        cell: (value: unknown, record: Invoice) => (
          <span className="text-green-700">
            {formatCurrency(value as number | null, record.currency)}
          </span>
        ),
      },
      {
        id: 'balance',
        label: 'Balance',
        accessorKey: 'balance',
        sortable: true,
        horizontalAlign: 'right',
        noWrap: true,
        cell: (value: unknown, record: Invoice) => {
          const bal = value as number;
          return (
            <span className={bal > 0 ? 'font-semibold text-red-600' : 'text-gray-500'}>
              {formatCurrency(bal, record.currency)}
            </span>
          );
        },
      },
      {
        id: 'status',
        label: 'Status',
        accessorKey: 'status',
        sortable: true,
        noWrap: true,
        cell: (value: unknown) => {
          const key = String(value) as InvoiceStatus;
          const cfg = INVOICE_STATUS_CONFIG[key] ?? INVOICE_STATUS_CONFIG.draft;
          return (
            <span
              className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${cfg.bg} ${cfg.text}`}
            >
              {cfg.label}
            </span>
          );
        },
      },
      {
        id: 'actions',
        label: 'Actions',
        sortable: false,
        horizontalAlign: 'center',
        cell: (_value: unknown, record: Invoice) => (
          <div className="flex items-center justify-center gap-2">
            {/* View */}
            <Link
              to={`/invoicing/invoices/${record.id}`}
              className="rounded p-1 text-gray-500 hover:bg-gray-100 hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              title="View Invoice"
              aria-label={`View invoice ${record.invoice_number}`}
            >
              <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" aria-hidden="true">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
              </svg>
            </Link>
            {/* Edit — only for editable statuses */}
            {(record.status === 'draft' || record.status === 'sent' || record.status === 'partial' || record.status === 'overdue') && (
              <Link
                to={`/invoicing/invoices/${record.id}/edit`}
                className="rounded p-1 text-gray-500 hover:bg-gray-100 hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
                title="Edit Invoice"
                aria-label={`Edit invoice ${record.invoice_number}`}
              >
                <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" aria-hidden="true">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                </svg>
              </Link>
            )}
          </div>
        ),
      },
    ],
    [],
  );

  /* ═══════════════════════════════════════════════════════════════
     Loading State
     ═══════════════════════════════════════════════════════════════ */
  if (isListLoading && !invoiceListResponse) {
    return (
      <div className="flex min-h-[50vh] items-center justify-center" role="status">
        <div className="text-center">
          <div
            className="mx-auto mb-4 h-8 w-8 animate-spin rounded-full border-4 border-blue-200 border-t-blue-600"
            aria-hidden="true"
          />
          <p className="text-sm text-gray-500">Loading invoices…</p>
        </div>
      </div>
    );
  }

  /* ═══════════════════════════════════════════════════════════════
     Error State
     ═══════════════════════════════════════════════════════════════ */
  if (isListError) {
    return (
      <div className="mx-auto max-w-lg px-4 py-12 text-center">
        <div className="mb-4 inline-flex h-12 w-12 items-center justify-center rounded-full bg-red-100">
          <svg className="h-6 w-6 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
          </svg>
        </div>
        <h2 className="mb-2 text-lg font-semibold text-gray-900">Failed to load invoices</h2>
        <p className="mb-4 text-sm text-gray-600">
          {listError instanceof Error ? listError.message : 'An unexpected error occurred. Please try again.'}
        </p>
        <button
          type="button"
          onClick={() => window.location.reload()}
          className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          Retry
        </button>
      </div>
    );
  }

  /* ═══════════════════════════════════════════════════════════════
     Main Render
     ═══════════════════════════════════════════════════════════════ */
  return (
    <div className="space-y-6">
      {/* ── Page Header ─────────────────────────────────────────── */}
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Invoices</h1>
          <p className="mt-1 text-sm text-gray-500">
            {totalCount} invoice{totalCount !== 1 ? 's' : ''} total
          </p>
        </div>

        <div className="flex items-center gap-3">
          {/* Filter toggle */}
          <button
            type="button"
            onClick={() => setIsFilterOpen((prev) => !prev)}
            className={`inline-flex items-center gap-2 rounded-md border px-3 py-2 text-sm font-medium shadow-sm transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
              isFilterOpen || hasActiveFilters
                ? 'border-blue-300 bg-blue-50 text-blue-700'
                : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
            }`}
            aria-expanded={isFilterOpen}
            aria-controls="invoice-filter-panel"
          >
            <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z" />
            </svg>
            Filters
            {hasActiveFilters && (
              <span className="inline-flex h-5 w-5 items-center justify-center rounded-full bg-blue-600 text-xs text-white">
                !
              </span>
            )}
          </button>

          {/* Create Invoice */}
          <button
            type="button"
            onClick={handleCreateInvoice}
            className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
            </svg>
            Create Invoice
          </button>
        </div>
      </div>

      {/* ── Summary Cards ───────────────────────────────────────── */}
      {summary && (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-4">
          {/* Total Invoices */}
          <div className="rounded-lg border border-blue-200 bg-blue-50 p-4">
            <div className="flex items-center justify-between">
              <p className="text-sm font-medium text-blue-600">Total Invoices</p>
              <span className="inline-flex h-8 w-8 items-center justify-center rounded-full bg-blue-100" aria-hidden="true">
                <svg className="h-4 w-4 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
              </span>
            </div>
            <p className="mt-2 text-2xl font-bold text-blue-900">{summary.total_count}</p>
            <p className="mt-1 text-sm text-blue-700">{formatCurrency(summary.total_amount)}</p>
          </div>

          {/* Paid */}
          <div className="rounded-lg border border-green-200 bg-green-50 p-4">
            <div className="flex items-center justify-between">
              <p className="text-sm font-medium text-green-600">Paid</p>
              <span className="inline-flex h-8 w-8 items-center justify-center rounded-full bg-green-100" aria-hidden="true">
                <svg className="h-4 w-4 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
              </span>
            </div>
            <p className="mt-2 text-2xl font-bold text-green-900">{summary.paid_count}</p>
            <p className="mt-1 text-sm text-green-700">{formatCurrency(summary.paid_amount)}</p>
          </div>

          {/* Sent / Pending */}
          <div className="rounded-lg border border-yellow-200 bg-yellow-50 p-4">
            <div className="flex items-center justify-between">
              <p className="text-sm font-medium text-yellow-600">Sent / Pending</p>
              <span className="inline-flex h-8 w-8 items-center justify-center rounded-full bg-yellow-100" aria-hidden="true">
                <svg className="h-4 w-4 text-yellow-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
              </span>
            </div>
            <p className="mt-2 text-2xl font-bold text-yellow-900">{summary.sent_count}</p>
            <p className="mt-1 text-sm text-yellow-700">{formatCurrency(pendingAmount)}</p>
          </div>

          {/* Overdue */}
          <div className="rounded-lg border border-red-200 bg-red-50 p-4">
            <div className="flex items-center justify-between">
              <p className="text-sm font-medium text-red-600">Overdue</p>
              <span className="inline-flex h-8 w-8 items-center justify-center rounded-full bg-red-100" aria-hidden="true">
                <svg className="h-4 w-4 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
              </span>
            </div>
            <p className="mt-2 text-2xl font-bold text-red-900">{summary.overdue_count}</p>
            <p className="mt-1 text-sm text-red-700">{formatCurrency(summary.overdue_amount)}</p>
          </div>
        </div>
      )}

      {/* ── Collapsible Filter Panel ────────────────────────────── */}
      {isFilterOpen && (
        <div
          id="invoice-filter-panel"
          className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
          role="region"
          aria-label="Invoice filters"
        >
          <div className="space-y-4">
            {/* Search */}
            <form onSubmit={handleSearchSubmit} className="flex gap-3">
              <div className="flex-1">
                <label htmlFor="invoice-search" className="sr-only">Search invoices</label>
                <div className="relative">
                  <div className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3">
                    <svg className="h-4 w-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" aria-hidden="true">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                    </svg>
                  </div>
                  <input
                    id="invoice-search"
                    type="search"
                    value={searchInput}
                    onChange={(e) => setSearchInput(e.target.value)}
                    placeholder="Search by invoice number or customer name…"
                    className="block w-full rounded-md border border-gray-300 py-2 pe-3 ps-10 text-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
                  />
                </div>
              </div>
              <button
                type="submit"
                className="rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-200 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              >
                Search
              </button>
            </form>

            {/* Status Tabs */}
            <div>
              <span className="mb-2 block text-xs font-medium uppercase tracking-wide text-gray-500">Status</span>
              <div className="flex flex-wrap gap-2" role="tablist" aria-label="Filter by status">
                {STATUS_TABS.map((tab) => {
                  const isActive = tab.key === 'all' ? !statusFilter : statusFilter === tab.key;
                  return (
                    <button
                      key={tab.key}
                      type="button"
                      role="tab"
                      aria-selected={isActive}
                      onClick={() => handleStatusFilter(tab.key)}
                      className={`rounded-full px-3 py-1.5 text-xs font-medium transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                        isActive
                          ? 'bg-blue-600 text-white'
                          : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
                      }`}
                    >
                      {tab.label}
                    </button>
                  );
                })}
              </div>
            </div>

            {/* Date Range + Customer */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
              <div>
                <label htmlFor="invoice-date-from" className="mb-1 block text-xs font-medium text-gray-700">
                  From Date
                </label>
                <input
                  id="invoice-date-from"
                  type="date"
                  value={dateFrom}
                  onChange={(e) => handleDateFromChange(e.target.value)}
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
                />
              </div>
              <div>
                <label htmlFor="invoice-date-to" className="mb-1 block text-xs font-medium text-gray-700">
                  To Date
                </label>
                <input
                  id="invoice-date-to"
                  type="date"
                  value={dateTo}
                  onChange={(e) => handleDateToChange(e.target.value)}
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
                />
              </div>
              <div>
                <label htmlFor="invoice-customer" className="mb-1 block text-xs font-medium text-gray-700">
                  Customer
                </label>
                <input
                  id="invoice-customer"
                  type="text"
                  value={customerInput}
                  onChange={(e) => handleCustomerChange(e.target.value)}
                  placeholder="Filter by customer…"
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
                />
              </div>
            </div>

            {/* Clear Filters */}
            {hasActiveFilters && (
              <div className="flex justify-end">
                <button
                  type="button"
                  onClick={handleClearFilters}
                  className="text-sm font-medium text-blue-600 hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
                >
                  Clear all filters
                </button>
              </div>
            )}
          </div>
        </div>
      )}

      {/* ── DataTable ───────────────────────────────────────────── */}
      <DataTable<Invoice>
        data={invoices}
        columns={columns}
        totalCount={totalCount}
        pageSize={pageSize}
        currentPage={currentPage}
        loading={isListLoading}
        emptyText="No invoices found. Create your first invoice to get started."
        striped
        hover
        showHeader
        showFooter
      />
    </div>
  );
}

export default InvoiceList;
