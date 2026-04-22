/**
 * QuoteList.tsx — Quote/Estimate Listing Page
 *
 * React 19 page component for listing quotes/estimates in the Invoicing
 * bounded-context service.  Replaces the monolith's RecordList.cshtml.cs
 * pattern applied to quote entities.  Quotes are pre-invoice proposals
 * that can be converted to invoices once accepted.
 *
 * Data is fetched via TanStack Query 5 from the Invoicing Lambda
 * (backed by RDS PostgreSQL for ACID transactions) through HTTP API
 * Gateway v2 at /v1/invoicing/quotes.
 *
 * All filter, sort, and pagination state is URL-driven via
 * useSearchParams so bookmarkable and browser-navigable.
 */

import React, { useState, useMemo, useCallback } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';

import { get } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import { formatCurrency, formatDate } from '../../utils/formatters';

/* ------------------------------------------------------------------ */
/*  TypeScript interfaces                                              */
/* ------------------------------------------------------------------ */

/** Possible lifecycle states of a quote. */
type QuoteStatus =
  | 'draft'
  | 'sent'
  | 'accepted'
  | 'rejected'
  | 'expired'
  | 'converted';

/** A single quote record returned by GET /v1/invoicing/quotes. */
interface Quote extends Record<string, unknown> {
  id: string;
  quote_number: string;
  customer_id: string;
  customer_name: string;
  issue_date: string;
  expiry_date: string;
  total_amount: number;
  status: QuoteStatus;
  currency: string;
  line_items_count: number;
  converted_invoice_id: string | null;
  notes: string;
  created_on: string;
  updated_on: string;
}

/** Aggregate statistics from GET /v1/invoicing/quotes/summary. */
interface QuoteSummary {
  total_count: number;
  total_amount: number;
  draft_count: number;
  sent_count: number;
  accepted_count: number;
  rejected_count: number;
  expired_count: number;
  converted_count: number;
}

/** Paginated list envelope returned from the quotes list endpoint. */
interface QuoteListResponse {
  data: Quote[];
  total: number;
  page: number;
  pageSize: number;
}

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** Colour-coded badge configuration per quote status. */
const quoteStatusConfig: Record<
  QuoteStatus,
  { bg: string; text: string; label: string }
> = {
  draft: { bg: 'bg-gray-100', text: 'text-gray-800', label: 'Draft' },
  sent: { bg: 'bg-blue-100', text: 'text-blue-800', label: 'Sent' },
  accepted: { bg: 'bg-green-100', text: 'text-green-800', label: 'Accepted' },
  rejected: { bg: 'bg-red-100', text: 'text-red-800', label: 'Rejected' },
  expired: { bg: 'bg-orange-100', text: 'text-orange-800', label: 'Expired' },
  converted: {
    bg: 'bg-purple-100',
    text: 'text-purple-800',
    label: 'Converted',
  },
};

/** Tabs rendered in the status filter bar. Empty string means "All". */
const STATUS_TABS: ReadonlyArray<{ value: string; label: string }> = [
  { value: '', label: 'All' },
  { value: 'draft', label: 'Draft' },
  { value: 'sent', label: 'Sent' },
  { value: 'accepted', label: 'Accepted' },
  { value: 'rejected', label: 'Rejected' },
  { value: 'expired', label: 'Expired' },
  { value: 'converted', label: 'Converted' },
] as const;

/** Default number of rows per page (matches monolith's default). */
const DEFAULT_PAGE_SIZE = 20;

/* ------------------------------------------------------------------ */
/*  Helpers                                                            */
/* ------------------------------------------------------------------ */

/**
 * Returns true when a quote's expiry date is in the past and the
 * quote has not already been accepted or converted.
 */
function isQuoteExpired(expiryDate: string): boolean {
  if (!expiryDate) return false;
  return new Date(expiryDate) < new Date();
}

/* ------------------------------------------------------------------ */
/*  QuoteList page component                                           */
/* ------------------------------------------------------------------ */

/**
 * QuoteList – paginated, filterable, sortable listing of
 * quotes / estimates.  Lazy-loaded under `/invoicing/quotes`.
 */
export default function QuoteList(): React.JSX.Element {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  /* ---- local UI state ---- */
  const [isFilterOpen, setIsFilterOpen] = useState<boolean>(false);

  /* ---- URL-driven query state ---- */
  const page = Number(searchParams.get('page')) || 1;
  const pageSize =
    Number(searchParams.get('pageSize')) || DEFAULT_PAGE_SIZE;
  const sortBy = searchParams.get('sortBy') || 'issue_date';
  const sortOrder =
    (searchParams.get('sortOrder') as 'asc' | 'desc') || 'desc';
  const search = searchParams.get('search') || '';
  const status = searchParams.get('status') || '';
  const dateFrom = searchParams.get('dateFrom') || '';
  const dateTo = searchParams.get('dateTo') || '';
  const customerId = searchParams.get('customerId') || '';

  /* ---- search-param updater (merges into current params) ---- */
  const updateParams = useCallback(
    (updates: Record<string, string>) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        Object.entries(updates).forEach(([key, value]) => {
          if (value) {
            next.set(key, value);
          } else {
            next.delete(key);
          }
        });
        return next;
      });
    },
    [setSearchParams],
  );

  /* ---------------------------------------------------------------- */
  /*  Data fetching                                                    */
  /* ---------------------------------------------------------------- */

  /** Paginated quote list query. */
  const {
    data: quotesResponse,
    isLoading: isQuotesLoading,
    isError: isQuotesError,
    error: quotesError,
  } = useQuery<ApiResponse<QuoteListResponse>, ApiError>({
    queryKey: [
      'invoicing',
      'quotes',
      {
        page,
        pageSize,
        sortBy,
        sortOrder,
        search,
        status,
        dateFrom,
        dateTo,
        customerId,
      },
    ],
    queryFn: () =>
      get<QuoteListResponse>('/invoicing/quotes', {
        page,
        pageSize,
        sortBy,
        sortOrder,
        ...(search ? { search } : {}),
        ...(status ? { status } : {}),
        ...(dateFrom ? { dateFrom } : {}),
        ...(dateTo ? { dateTo } : {}),
        ...(customerId ? { customerId } : {}),
      }),
  });

  /** Summary statistics query (independent of filters). */
  const { data: summaryResponse, isLoading: isSummaryLoading } = useQuery<
    ApiResponse<QuoteSummary>,
    ApiError
  >({
    queryKey: ['invoicing', 'quotes', 'summary'],
    queryFn: () => get<QuoteSummary>('/invoicing/quotes/summary'),
  });

  /* ---- derived data ---- */
  const quotes: Quote[] = quotesResponse?.success
    ? quotesResponse.object?.data ?? []
    : [];
  const totalCount: number = quotesResponse?.success
    ? quotesResponse.object?.total ?? 0
    : 0;
  const summary: QuoteSummary | undefined = summaryResponse?.success
    ? summaryResponse.object
    : undefined;

  /** User-facing error message incorporating ApiError.message and
   *  ApiError.status for diagnostics. */
  const errorMessage: string = isQuotesError
    ? (quotesError as ApiError)?.message ||
      quotesResponse?.message ||
      'Failed to load quotes'
    : '';
  const errorStatus: number | undefined = isQuotesError
    ? (quotesError as ApiError)?.status
    : undefined;

  /* ---------------------------------------------------------------- */
  /*  Event handlers                                                   */
  /* ---------------------------------------------------------------- */

  const handlePageChange = useCallback(
    (newPage: number) => {
      updateParams({ page: String(newPage) });
    },
    [updateParams],
  );

  const handlePageSizeChange = useCallback(
    (newSize: number) => {
      updateParams({ pageSize: String(newSize), page: '1' });
    },
    [updateParams],
  );

  const handleSortChange = useCallback(
    (newSortBy: string, newSortOrder: 'asc' | 'desc') => {
      updateParams({
        sortBy: newSortBy,
        sortOrder: newSortOrder,
        page: '1',
      });
    },
    [updateParams],
  );

  const handleSearchChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      updateParams({ search: e.target.value, page: '1' });
    },
    [updateParams],
  );

  const handleStatusChange = useCallback(
    (newStatus: string) => {
      updateParams({ status: newStatus, page: '1' });
    },
    [updateParams],
  );

  const handleDateFromChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      updateParams({ dateFrom: e.target.value, page: '1' });
    },
    [updateParams],
  );

  const handleDateToChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      updateParams({ dateTo: e.target.value, page: '1' });
    },
    [updateParams],
  );

  const handleCustomerChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      updateParams({ customerId: e.target.value, page: '1' });
    },
    [updateParams],
  );

  const handleClearFilters = useCallback(() => {
    setSearchParams({});
  }, [setSearchParams]);

  const handleToggleFilters = useCallback(() => {
    setIsFilterOpen((prev) => !prev);
  }, []);

  const handleCreateQuote = useCallback(() => {
    navigate('/invoicing/quotes/create');
  }, [navigate]);

  /* ---------------------------------------------------------------- */
  /*  Column definitions (memoised)                                    */
  /* ---------------------------------------------------------------- */

  const columns = useMemo<DataTableColumn<Quote>[]>(
    () => [
      /* Quote # — clickable link to detail page */
      {
        id: 'quote_number',
        label: 'Quote #',
        accessorKey: 'quote_number',
        sortable: true,
        cell: (_value: unknown, record: Quote) => (
          <Link
            to={`/invoicing/quotes/${record.id}`}
            className="font-medium text-blue-600 hover:text-blue-800 hover:underline"
          >
            {record.quote_number}
          </Link>
        ),
      },

      /* Customer name */
      {
        id: 'customer_name',
        label: 'Customer',
        accessorKey: 'customer_name',
        sortable: true,
        cell: (_value: unknown, record: Quote) => (
          <span className="text-gray-900">
            {record.customer_name || '\u2014'}
          </span>
        ),
      },

      /* Issue Date */
      {
        id: 'issue_date',
        label: 'Issue Date',
        accessorKey: 'issue_date',
        sortable: true,
        cell: (_value: unknown, record: Quote) => (
          <span className="text-gray-700">
            {formatDate(record.issue_date, 'short')}
          </span>
        ),
      },

      /* Expiry Date — shows expired indicator when past due */
      {
        id: 'expiry_date',
        label: 'Expiry Date',
        accessorKey: 'expiry_date',
        sortable: true,
        cell: (_value: unknown, record: Quote) => {
          const expired =
            record.status !== 'converted' &&
            record.status !== 'accepted' &&
            isQuoteExpired(record.expiry_date);
          return (
            <span
              className={
                expired ? 'font-medium text-red-600' : 'text-gray-700'
              }
            >
              {formatDate(record.expiry_date, 'short')}
              {expired && (
                <span
                  className="ml-1 inline-flex items-center rounded-full bg-red-50 px-1.5 py-0.5 text-xs font-medium text-red-700"
                  aria-label="Expired"
                >
                  Expired
                </span>
              )}
            </span>
          );
        },
      },

      /* Amount — currency-formatted */
      {
        id: 'total_amount',
        label: 'Amount',
        accessorKey: 'total_amount',
        sortable: true,
        cell: (_value: unknown, record: Quote) => (
          <span className="font-medium text-gray-900">
            {formatCurrency(record.total_amount, record.currency)}
          </span>
        ),
      },

      /* Status — colour-coded badge */
      {
        id: 'status',
        label: 'Status',
        accessorKey: 'status',
        sortable: true,
        cell: (_value: unknown, record: Quote) => {
          const config =
            quoteStatusConfig[record.status] ?? quoteStatusConfig.draft;
          return (
            <span
              className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${config.bg} ${config.text}`}
            >
              {config.label}
            </span>
          );
        },
      },

      /* Converted — link to converted invoice or dash */
      {
        id: 'converted',
        label: 'Converted',
        sortable: false,
        cell: (_value: unknown, record: Quote) =>
          record.converted_invoice_id ? (
            <Link
              to={`/invoicing/invoices/${record.converted_invoice_id}`}
              className="text-purple-600 hover:text-purple-800 hover:underline"
            >
              View Invoice
            </Link>
          ) : (
            <span className="text-gray-400">{'\u2014'}</span>
          ),
      },

      /* Actions — View / Edit / Convert */
      {
        id: 'actions',
        label: 'Actions',
        sortable: false,
        cell: (_value: unknown, record: Quote) => (
          <div className="flex items-center gap-2">
            {/* View */}
            <Link
              to={`/invoicing/quotes/${record.id}`}
              className="rounded p-1 text-gray-500 hover:bg-gray-100 hover:text-gray-700"
              aria-label={`View quote ${record.quote_number}`}
            >
              <svg
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-4 w-4"
                aria-hidden="true"
              >
                <path d="M10 12.5a2.5 2.5 0 100-5 2.5 2.5 0 000 5z" />
                <path
                  fillRule="evenodd"
                  d="M.664 10.59a1.651 1.651 0 010-1.186A10.004 10.004 0 0110 3c4.257 0 7.893 2.66 9.336 6.41.147.381.146.804 0 1.186A10.004 10.004 0 0110 17c-4.257 0-7.893-2.66-9.336-6.41zM14 10a4 4 0 11-8 0 4 4 0 018 0z"
                  clipRule="evenodd"
                />
              </svg>
            </Link>

            {/* Edit */}
            <Link
              to={`/invoicing/quotes/${record.id}?edit=true`}
              className="rounded p-1 text-gray-500 hover:bg-gray-100 hover:text-gray-700"
              aria-label={`Edit quote ${record.quote_number}`}
            >
              <svg
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-4 w-4"
                aria-hidden="true"
              >
                <path d="M2.695 14.763l-1.262 3.154a.5.5 0 00.65.65l3.155-1.262a4 4 0 001.343-.885L17.5 5.5a2.121 2.121 0 00-3-3L3.58 13.42a4 4 0 00-.885 1.343z" />
              </svg>
            </Link>

            {/* Convert — only for accepted, non-converted quotes */}
            {record.status === 'accepted' &&
              !record.converted_invoice_id && (
                <Link
                  to={`/invoicing/quotes/${record.id}/convert`}
                  className="rounded p-1 text-purple-500 hover:bg-purple-50 hover:text-purple-700"
                  aria-label={`Convert quote ${record.quote_number} to invoice`}
                >
                  <svg
                    viewBox="0 0 20 20"
                    fill="currentColor"
                    className="h-4 w-4"
                    aria-hidden="true"
                  >
                    <path
                      fillRule="evenodd"
                      d="M10 18a8 8 0 100-16 8 8 0 000 16zM6.75 9.25a.75.75 0 000 1.5h4.59l-2.1 1.95a.75.75 0 001.02 1.1l3.5-3.25a.75.75 0 000-1.1l-3.5-3.25a.75.75 0 10-1.02 1.1l2.1 1.95H6.75z"
                      clipRule="evenodd"
                    />
                  </svg>
                </Link>
              )}
          </div>
        ),
      },
    ],
    [],
  );

  /* ---------------------------------------------------------------- */
  /*  Render                                                           */
  /* ---------------------------------------------------------------- */

  return (
    <div className="flex flex-col gap-6 p-6">
      {/* ---- Page header ---- */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">
            Quotes / Estimates
          </h1>
          <p className="mt-1 text-sm text-gray-500">
            {isSummaryLoading
              ? 'Loading\u2026'
              : `${summary?.total_count ?? 0} total quotes`}
          </p>
        </div>

        <div className="flex items-center gap-3">
          {/* Filter toggle */}
          <button
            type="button"
            onClick={handleToggleFilters}
            className="inline-flex items-center gap-1.5 rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            aria-expanded={isFilterOpen}
            aria-controls="quote-filter-panel"
          >
            <svg
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M2.628 1.601C5.028 1.206 7.49 1 10 1s4.973.206 7.372.601a.75.75 0 01.628.74v2.288a2.25 2.25 0 01-.659 1.59l-4.682 4.683a2.25 2.25 0 00-.659 1.59v3.037c0 .684-.31 1.33-.844 1.757l-1.937 1.55A.75.75 0 018 18.25v-5.757a2.25 2.25 0 00-.659-1.591L2.659 6.22A2.25 2.25 0 012 4.629V2.34a.75.75 0 01.628-.74z"
                clipRule="evenodd"
              />
            </svg>
            Filters
          </button>

          {/* Create Quote */}
          <button
            type="button"
            onClick={handleCreateQuote}
            className="inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            <svg
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
            </svg>
            Create Quote
          </button>
        </div>
      </div>

      {/* ---- Summary cards ---- */}
      {!isSummaryLoading && summary && (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {/* Total Quotes (blue) */}
          <div className="rounded-lg border border-blue-200 bg-blue-50 p-4">
            <p className="text-sm font-medium text-blue-600">Total Quotes</p>
            <p className="mt-1 text-2xl font-bold text-blue-900">
              {summary.total_count}
            </p>
            <p className="mt-1 text-sm text-blue-700">
              {formatCurrency(summary.total_amount)}
            </p>
          </div>

          {/* Accepted (green) */}
          <div className="rounded-lg border border-green-200 bg-green-50 p-4">
            <p className="text-sm font-medium text-green-600">Accepted</p>
            <p className="mt-1 text-2xl font-bold text-green-900">
              {summary.accepted_count}
            </p>
          </div>

          {/* Pending / Sent (yellow) */}
          <div className="rounded-lg border border-yellow-200 bg-yellow-50 p-4">
            <p className="text-sm font-medium text-yellow-600">Pending</p>
            <p className="mt-1 text-2xl font-bold text-yellow-900">
              {summary.sent_count}
            </p>
            <p className="mt-1 text-sm text-yellow-700">Awaiting response</p>
          </div>

          {/* Expired + Rejected (red) */}
          <div className="rounded-lg border border-red-200 bg-red-50 p-4">
            <p className="text-sm font-medium text-red-600">
              Expired / Rejected
            </p>
            <p className="mt-1 text-2xl font-bold text-red-900">
              {summary.expired_count + summary.rejected_count}
            </p>
          </div>
        </div>
      )}

      {/* ---- Filter panel (collapsible) ---- */}
      {isFilterOpen && (
        <div
          id="quote-filter-panel"
          className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
          role="region"
          aria-label="Quote filters"
        >
          <div className="flex flex-col gap-4">
            {/* Search */}
            <div>
              <label
                htmlFor="quote-search"
                className="block text-sm font-medium text-gray-700"
              >
                Search
              </label>
              <input
                id="quote-search"
                type="text"
                value={search}
                onChange={handleSearchChange}
                placeholder="Search by quote number or customer name\u2026"
                className="mt-1 block w-full rounded-lg border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>

            {/* Status tabs */}
            <div>
              <span className="block text-sm font-medium text-gray-700">
                Status
              </span>
              <nav
                className="mt-1 flex flex-wrap gap-1"
                aria-label="Quote status filter"
              >
                {STATUS_TABS.map((tab) => (
                  <button
                    key={tab.value || '__all'}
                    type="button"
                    onClick={() => handleStatusChange(tab.value)}
                    className={`rounded-full px-3 py-1 text-sm font-medium transition-colors ${
                      status === tab.value
                        ? 'bg-blue-600 text-white'
                        : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
                    }`}
                    aria-pressed={status === tab.value}
                  >
                    {tab.label}
                  </button>
                ))}
              </nav>
            </div>

            {/* Date range */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <div>
                <label
                  htmlFor="quote-date-from"
                  className="block text-sm font-medium text-gray-700"
                >
                  From Date
                </label>
                <input
                  id="quote-date-from"
                  type="date"
                  value={dateFrom}
                  onChange={handleDateFromChange}
                  className="mt-1 block w-full rounded-lg border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                />
              </div>
              <div>
                <label
                  htmlFor="quote-date-to"
                  className="block text-sm font-medium text-gray-700"
                >
                  To Date
                </label>
                <input
                  id="quote-date-to"
                  type="date"
                  value={dateTo}
                  onChange={handleDateToChange}
                  className="mt-1 block w-full rounded-lg border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                />
              </div>
            </div>

            {/* Customer select */}
            <div>
              <label
                htmlFor="quote-customer"
                className="block text-sm font-medium text-gray-700"
              >
                Customer
              </label>
              <select
                id="quote-customer"
                value={customerId}
                onChange={handleCustomerChange}
                className="mt-1 block w-full rounded-lg border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              >
                <option value="">All Customers</option>
              </select>
            </div>

            {/* Clear all */}
            <div className="flex justify-end">
              <button
                type="button"
                onClick={handleClearFilters}
                className="text-sm font-medium text-blue-600 hover:text-blue-800"
              >
                Clear All
              </button>
            </div>
          </div>
        </div>
      )}

      {/* ---- Error state ---- */}
      {isQuotesError && (
        <div
          className="rounded-lg border border-red-200 bg-red-50 p-4"
          role="alert"
        >
          <div className="flex items-center gap-2">
            <p className="text-sm font-medium text-red-800">{errorMessage}</p>
            {errorStatus !== undefined && (
              <span className="text-xs text-red-600">
                (Error {errorStatus})
              </span>
            )}
          </div>
        </div>
      )}

      {/* ---- Data table ---- */}
      <DataTable<Quote>
        data={quotes}
        columns={columns}
        totalCount={totalCount}
        pageSize={pageSize}
        currentPage={page}
        onPageChange={handlePageChange}
        onPageSizeChange={handlePageSizeChange}
        onSortChange={handleSortChange}
        loading={isQuotesLoading}
        emptyText="No quotes found. Create your first quote to get started."
      />
    </div>
  );
}
