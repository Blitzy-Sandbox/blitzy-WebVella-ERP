/**
 * StockList.tsx — Stock/Inventory Levels Listing with Warehouse Grouping
 *
 * Replaces the monolith's RecordList.cshtml.cs pattern applied to inventory
 * stock tracking entities. Provides an overview of stock levels across all
 * products and warehouses in the Inventory bounded-context (DynamoDB-backed).
 *
 * Features:
 * - Summary cards showing stock health (total, in-stock, low-stock, out-of-stock)
 * - Warehouse filter dropdown and stock status filter tabs
 * - Product name/SKU search with URL-synced state
 * - Warehouse grouping with collapsible groups when viewing all warehouses
 * - DataTable with sortable columns, server-side pagination (pageSize=20)
 * - Stock status color-coded badges (green/yellow/red)
 * - 60-second auto-refresh for near-real-time stock updates
 * - URL-driven state for bookmarkable/shareable views
 */

import { useState, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';
import { get, type ApiResponse } from '../../api/client';
import { DataTable, type DataTableColumn } from '../../components/data-table/DataTable';

/* ═══════════════════════════════════════════════════════════════════
 * TypeScript Interfaces
 * ═══════════════════════════════════════════════════════════════════ */

/** Individual stock item representing a product's stock level at a warehouse. */
interface StockItem extends Record<string, unknown> {
  id: string;
  product_id: string;
  product_name: string;
  product_sku: string;
  warehouse_id: string;
  warehouse_name: string;
  quantity: number;
  reserved: number;
  available: number;
  reorder_level: number;
  updated_on: string;
}

/** Aggregate stock summary statistics for the dashboard cards. */
interface StockSummary {
  total_products: number;
  in_stock_count: number;
  low_stock_count: number;
  out_of_stock_count: number;
}

/** Warehouse entity for the filter dropdown and grouping. */
interface Warehouse {
  id: string;
  name: string;
  location: string;
}

/** Paginated stock list response shape from the API. */
interface StockListResponse {
  data: StockItem[];
  total: number;
  page: number;
  pageSize: number;
}

/** Stock status classification type. */
type StockStatusType = 'in-stock' | 'low-stock' | 'out-of-stock';

/* ═══════════════════════════════════════════════════════════════════
 * Helper Functions and Constants
 * ═══════════════════════════════════════════════════════════════════ */

/** Default page size for the stock list. */
const DEFAULT_PAGE_SIZE = 20;

/** Auto-refresh interval for near-real-time stock updates (60 seconds). */
const REFETCH_INTERVAL_MS = 60_000;

/** Stale time for warehouse data (10 minutes — warehouses change infrequently). */
const WAREHOUSE_STALE_TIME_MS = 10 * 60 * 1000;

/**
 * Derives stock status from quantity and reorder level.
 * - out-of-stock: quantity <= 0
 * - low-stock: 0 < quantity <= reorderLevel
 * - in-stock: quantity > reorderLevel
 */
function getStockStatus(quantity: number, reorderLevel: number): StockStatusType {
  if (quantity <= 0) return 'out-of-stock';
  if (quantity <= reorderLevel) return 'low-stock';
  return 'in-stock';
}

/** Visual configuration for stock status badges. */
const stockStatusConfig: Record<
  StockStatusType,
  { bg: string; text: string; label: string }
> = {
  'in-stock': { bg: 'bg-green-100', text: 'text-green-800', label: 'In Stock' },
  'low-stock': { bg: 'bg-yellow-100', text: 'text-yellow-800', label: 'Low Stock' },
  'out-of-stock': { bg: 'bg-red-100', text: 'text-red-800', label: 'Out of Stock' },
};

/** Status filter tab definitions used in the toolbar. */
const STATUS_TABS: ReadonlyArray<{ value: string; label: string }> = [
  { value: '', label: 'All' },
  { value: 'in-stock', label: 'In Stock' },
  { value: 'low-stock', label: 'Low Stock' },
  { value: 'out-of-stock', label: 'Out of Stock' },
] as const;

/** Default summary values used while loading or on error. */
const EMPTY_SUMMARY: StockSummary = {
  total_products: 0,
  in_stock_count: 0,
  low_stock_count: 0,
  out_of_stock_count: 0,
};

/**
 * Formats an ISO date string into a human-readable relative time.
 * Falls back to locale date string for dates beyond 30 days.
 */
function formatRelativeTime(dateString: string): string {
  if (!dateString) return '—';

  const dateMs = new Date(dateString).getTime();
  if (Number.isNaN(dateMs)) return '—';

  const diffMs = Date.now() - dateMs;
  const diffSec = Math.floor(diffMs / 1000);
  const diffMin = Math.floor(diffSec / 60);
  const diffHour = Math.floor(diffMin / 60);
  const diffDay = Math.floor(diffHour / 24);

  if (diffSec < 60) return 'just now';
  if (diffMin < 60) return `${diffMin} minute${diffMin !== 1 ? 's' : ''} ago`;
  if (diffHour < 24) return `${diffHour} hour${diffHour !== 1 ? 's' : ''} ago`;
  if (diffDay <= 30) return `${diffDay} day${diffDay !== 1 ? 's' : ''} ago`;

  return new Date(dateString).toLocaleDateString();
}

/* ═══════════════════════════════════════════════════════════════════
 * StockList Page Component
 * ═══════════════════════════════════════════════════════════════════ */

function StockList(): React.JSX.Element {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  /* ── URL-driven state ────────────────────────────────────────── */
  const page = Number(searchParams.get('page')) || 1;
  const pageSize = Number(searchParams.get('pageSize')) || DEFAULT_PAGE_SIZE;
  const sortBy = searchParams.get('sortBy') || 'product_name';
  const sortOrder = (searchParams.get('sortOrder') || 'asc') as 'asc' | 'desc';
  const search = searchParams.get('search') || '';
  const warehouse = searchParams.get('warehouse') || '';
  const stockStatus = searchParams.get('stockStatus') || '';

  /* ── Local state ─────────────────────────────────────────────── */
  const [searchInput, setSearchInput] = useState<string>(search);
  const [isGrouped, setIsGrouped] = useState<boolean>(true);
  const [collapsedGroups, setCollapsedGroups] = useState<Set<string>>(new Set());

  /* ══════════════════════════════════════════════════════════════
   * DATA FETCHING
   * ══════════════════════════════════════════════════════════════ */

  /** Fetches the paginated stock levels list from the Inventory service. */
  const stockQuery = useQuery<ApiResponse<StockListResponse>>({
    queryKey: [
      'inventory',
      'stock',
      { page, pageSize, sortBy, sortOrder, search, warehouse, stockStatus },
    ],
    queryFn: () =>
      get<StockListResponse>('/v1/inventory/stock', {
        page,
        pageSize,
        sortBy,
        sortOrder,
        ...(search ? { search } : {}),
        ...(warehouse ? { warehouse } : {}),
        ...(stockStatus ? { stockStatus } : {}),
      }),
    refetchInterval: REFETCH_INTERVAL_MS,
  });

  /** Fetches the warehouse list for the filter dropdown and grouping. */
  const warehousesQuery = useQuery<ApiResponse<Warehouse[]>>({
    queryKey: ['inventory', 'warehouses'],
    queryFn: () => get<Warehouse[]>('/v1/inventory/warehouses'),
    staleTime: WAREHOUSE_STALE_TIME_MS,
  });

  /** Fetches aggregate stock summary statistics for the dashboard cards. */
  const summaryQuery = useQuery<ApiResponse<StockSummary>>({
    queryKey: ['inventory', 'stock', 'summary'],
    queryFn: () => get<StockSummary>('/v1/inventory/stock/summary'),
    refetchInterval: REFETCH_INTERVAL_MS,
  });

  /* ── Derived data ────────────────────────────────────────────── */
  const stockItems: StockItem[] =
    stockQuery.data?.success ? (stockQuery.data.object?.data ?? []) : [];
  const totalCount: number =
    stockQuery.data?.success ? (stockQuery.data.object?.total ?? 0) : 0;
  const warehouses: Warehouse[] =
    warehousesQuery.data?.success ? (warehousesQuery.data.object ?? []) : [];
  const summary: StockSummary =
    summaryQuery.data?.success
      ? (summaryQuery.data.object ?? EMPTY_SUMMARY)
      : EMPTY_SUMMARY;

  /** Server-side API errors returned in the response envelope. */
  const apiErrors =
    stockQuery.data && !stockQuery.data.success ? stockQuery.data.errors : null;

  /* ── Loading / error flags ───────────────────────────────────── */
  const isLoading = stockQuery.isLoading;
  const isError = stockQuery.isError || apiErrors !== null;
  const errorMessage =
    apiErrors && apiErrors.length > 0
      ? apiErrors.map((e) => e.message).join('; ')
      : stockQuery.error instanceof Error
        ? stockQuery.error.message
        : 'An unexpected error occurred while loading stock data.';

  /* ── Warehouse grouping ──────────────────────────────────────── */
  const shouldGroup = isGrouped && !warehouse;

  const groupedData = useMemo(() => {
    if (!shouldGroup || stockItems.length === 0) return null;

    const groups = new Map<
      string,
      { warehouseId: string; items: StockItem[] }
    >();

    for (const item of stockItems) {
      const existing = groups.get(item.warehouse_name);
      if (existing) {
        existing.items.push(item);
      } else {
        groups.set(item.warehouse_name, {
          warehouseId: item.warehouse_id,
          items: [item],
        });
      }
    }

    return groups;
  }, [stockItems, shouldGroup]);

  /* ── Pagination helpers ──────────────────────────────────────── */
  const pageCount = Math.max(1, Math.ceil(totalCount / pageSize));

  /* ══════════════════════════════════════════════════════════════
   * COLUMN DEFINITIONS
   * ══════════════════════════════════════════════════════════════ */

  const columns: DataTableColumn<StockItem>[] = useMemo(() => {
    const cols: DataTableColumn<StockItem>[] = [
      /* Product name — clickable link to product details */
      {
        id: 'product_name',
        label: 'Product',
        accessorKey: 'product_name',
        sortable: true,
        cell: (_value: unknown, record: StockItem) => (
          <Link
            to={`/inventory/products/${record.product_id}`}
            className="font-medium text-blue-600 hover:text-blue-800 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            {record.product_name}
          </Link>
        ),
      },
      /* SKU — monospaced text */
      {
        id: 'product_sku',
        label: 'SKU',
        accessorKey: 'product_sku',
        sortable: true,
        noWrap: true,
        cell: (_value: unknown, record: StockItem) => (
          <span className="font-mono text-sm text-gray-600">
            {record.product_sku}
          </span>
        ),
      },
    ];

    /* Show warehouse column only when not filtered to a single warehouse. */
    if (!warehouse) {
      cols.push({
        id: 'warehouse_name',
        label: 'Warehouse',
        accessorKey: 'warehouse_name',
        sortable: true,
      });
    }

    cols.push(
      /* Quantity — color-coded by stock status */
      {
        id: 'quantity',
        label: 'Quantity',
        accessorKey: 'quantity',
        sortable: true,
        horizontalAlign: 'right',
        noWrap: true,
        cell: (_value: unknown, record: StockItem) => {
          const status = getStockStatus(record.quantity, record.reorder_level);
          const colorClass =
            status === 'out-of-stock'
              ? 'text-red-600 font-semibold'
              : status === 'low-stock'
                ? 'text-yellow-600 font-semibold'
                : 'text-gray-900';
          return (
            <span className={colorClass}>
              {record.quantity.toLocaleString()}
            </span>
          );
        },
      },
      /* Reserved */
      {
        id: 'reserved',
        label: 'Reserved',
        accessorKey: 'reserved',
        sortable: true,
        horizontalAlign: 'right',
        noWrap: true,
        cell: (_value: unknown, record: StockItem) => (
          <span className="text-gray-700">
            {record.reserved.toLocaleString()}
          </span>
        ),
      },
      /* Available (quantity - reserved) */
      {
        id: 'available',
        label: 'Available',
        accessorKey: 'available',
        sortable: true,
        horizontalAlign: 'right',
        noWrap: true,
        cell: (_value: unknown, record: StockItem) => (
          <span className="font-medium text-gray-900">
            {record.available.toLocaleString()}
          </span>
        ),
      },
      /* Reorder Level */
      {
        id: 'reorder_level',
        label: 'Reorder Level',
        accessorKey: 'reorder_level',
        sortable: true,
        horizontalAlign: 'right',
        noWrap: true,
        cell: (_value: unknown, record: StockItem) => (
          <span className="text-gray-500">
            {record.reorder_level.toLocaleString()}
          </span>
        ),
      },
      /* Status — color-coded badge */
      {
        id: 'status',
        label: 'Status',
        accessorFn: (row: StockItem) =>
          getStockStatus(row.quantity, row.reorder_level),
        sortable: true,
        noWrap: true,
        cell: (_value: unknown, record: StockItem) => {
          const status = getStockStatus(
            record.quantity,
            record.reorder_level,
          );
          const config = stockStatusConfig[status];
          return (
            <span
              className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${config.bg} ${config.text}`}
            >
              {config.label}
            </span>
          );
        },
      },
      /* Last Updated — relative time */
      {
        id: 'updated_on',
        label: 'Last Updated',
        accessorKey: 'updated_on',
        sortable: true,
        noWrap: true,
        cell: (_value: unknown, record: StockItem) => (
          <span
            className="text-sm text-gray-500"
            title={
              record.updated_on
                ? new Date(record.updated_on).toLocaleString()
                : ''
            }
          >
            {formatRelativeTime(record.updated_on)}
          </span>
        ),
      },
      /* Actions — "Adjust" button */
      {
        id: 'actions',
        label: 'Actions',
        horizontalAlign: 'center',
        cell: (_value: unknown, record: StockItem) => (
          <button
            type="button"
            onClick={() =>
              navigate(
                `/inventory/stock/adjust?productId=${encodeURIComponent(record.product_id)}`,
              )
            }
            className="inline-flex min-h-[2.75rem] min-w-[2.75rem] items-center justify-center rounded border border-gray-300 bg-white px-2.5 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
          >
            Adjust
          </button>
        ),
      },
    );

    return cols;
  }, [warehouse, navigate]);

  /* ══════════════════════════════════════════════════════════════
   * EVENT HANDLERS
   * ══════════════════════════════════════════════════════════════ */

  /** Applies a search filter, resetting to page 1. */
  function handleSearchSubmit(e: React.FormEvent): void {
    e.preventDefault();
    setSearchParams((prev) => {
      const params = new URLSearchParams(prev);
      if (searchInput.trim()) {
        params.set('search', searchInput.trim());
      } else {
        params.delete('search');
      }
      params.set('page', '1');
      return params;
    });
  }

  /** Selects a warehouse filter, resetting to page 1. */
  function handleWarehouseChange(warehouseId: string): void {
    setSearchParams((prev) => {
      const params = new URLSearchParams(prev);
      if (warehouseId) {
        params.set('warehouse', warehouseId);
      } else {
        params.delete('warehouse');
      }
      params.set('page', '1');
      return params;
    });
    setCollapsedGroups(new Set());
  }

  /** Selects a stock status filter tab, resetting to page 1. */
  function handleStatusFilterChange(status: string): void {
    setSearchParams((prev) => {
      const params = new URLSearchParams(prev);
      if (status) {
        params.set('stockStatus', status);
      } else {
        params.delete('stockStatus');
      }
      params.set('page', '1');
      return params;
    });
  }

  /** Toggles a warehouse group's expanded/collapsed state. */
  function toggleGroup(warehouseName: string): void {
    setCollapsedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(warehouseName)) {
        next.delete(warehouseName);
      } else {
        next.add(warehouseName);
      }
      return next;
    });
  }

  /** Handles page change for the grouped view's manual pagination. */
  function handleManualPageChange(newPage: number): void {
    const clamped = Math.max(1, Math.min(newPage, pageCount));
    setSearchParams((prev) => {
      const params = new URLSearchParams(prev);
      params.set('page', String(clamped));
      return params;
    });
  }

  /* ══════════════════════════════════════════════════════════════
   * RENDER
   * ══════════════════════════════════════════════════════════════ */
  return (
    <div className="mx-auto max-w-screen-2xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ─── Page Header ───────────────────────────────────────── */}
      <header className="mb-6">
        <h1 className="text-2xl font-bold tracking-tight text-gray-900">
          Stock Levels
        </h1>
        <p className="mt-1 text-sm text-gray-500">
          Overview of stock levels across all products and warehouses.
        </p>
      </header>

      {/* ─── Summary Cards ─────────────────────────────────────── */}
      <section
        className="mb-6 grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-4"
        aria-label="Stock summary"
      >
        {/* Total Products */}
        <div className="rounded-lg border border-blue-200 bg-blue-50 p-4">
          <p className="text-sm font-medium text-blue-600">Total Products</p>
          <p className="mt-1 text-2xl font-bold text-blue-900">
            {summaryQuery.isLoading
              ? '—'
              : (summary.total_products ?? 0).toLocaleString()}
          </p>
        </div>
        {/* In Stock */}
        <div className="rounded-lg border border-green-200 bg-green-50 p-4">
          <p className="text-sm font-medium text-green-600">In Stock</p>
          <p className="mt-1 text-2xl font-bold text-green-900">
            {summaryQuery.isLoading
              ? '—'
              : (summary.in_stock_count ?? 0).toLocaleString()}
          </p>
        </div>
        {/* Low Stock */}
        <div className="rounded-lg border border-yellow-200 bg-yellow-50 p-4">
          <p className="text-sm font-medium text-yellow-600">Low Stock</p>
          <p className="mt-1 text-2xl font-bold text-yellow-900">
            {summaryQuery.isLoading
              ? '—'
              : (summary.low_stock_count ?? 0).toLocaleString()}
          </p>
        </div>
        {/* Out of Stock */}
        <div className="rounded-lg border border-red-200 bg-red-50 p-4">
          <p className="text-sm font-medium text-red-600">Out of Stock</p>
          <p className="mt-1 text-2xl font-bold text-red-900">
            {summaryQuery.isLoading
              ? '—'
              : (summary.out_of_stock_count ?? 0).toLocaleString()}
          </p>
        </div>
      </section>

      {/* ─── Header Actions / Filters ──────────────────────────── */}
      <div className="mb-4 flex flex-wrap items-center gap-3">
        {/* Adjust Stock primary action */}
        <button
          type="button"
          onClick={() => navigate('/inventory/stock/adjust')}
          className="inline-flex min-h-[2.75rem] items-center rounded-md bg-blue-600 px-3.5 py-2 text-sm font-semibold text-white shadow-sm hover:bg-blue-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
        >
          Adjust Stock
        </button>

        {/* Warehouse filter dropdown */}
        <div>
          <label htmlFor="warehouse-filter" className="sr-only">
            Filter by warehouse
          </label>
          <select
            id="warehouse-filter"
            value={warehouse}
            onChange={(e) => handleWarehouseChange(e.target.value)}
            className="min-h-[2.75rem] rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            <option value="">All Warehouses</option>
            {warehouses.map((w) => (
              <option key={w.id} value={w.id}>
                {w.name}
                {w.location ? ` — ${w.location}` : ''}
              </option>
            ))}
          </select>
        </div>

        {/* Stock status filter tabs */}
        <nav
          className="flex rounded-md border border-gray-300"
          role="tablist"
          aria-label="Stock status filter"
        >
          {STATUS_TABS.map((tab) => {
            const isActive = stockStatus === tab.value;
            return (
              <button
                key={tab.value}
                type="button"
                role="tab"
                aria-selected={isActive}
                onClick={() => handleStatusFilterChange(tab.value)}
                className={[
                  'min-h-[2.75rem] px-3 py-2 text-sm font-medium',
                  'first:rounded-s-md last:rounded-e-md',
                  'motion-safe:transition-colors motion-safe:duration-150',
                  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
                  isActive
                    ? 'bg-blue-600 text-white'
                    : 'bg-white text-gray-700 hover:bg-gray-50',
                ].join(' ')}
              >
                {tab.label}
              </button>
            );
          })}
        </nav>

        {/* Search input */}
        <form
          onSubmit={handleSearchSubmit}
          className="flex items-center gap-1.5 ms-auto"
        >
          <label htmlFor="stock-search" className="sr-only">
            Search products
          </label>
          <input
            id="stock-search"
            type="search"
            value={searchInput}
            onChange={(e) => setSearchInput(e.target.value)}
            placeholder="Search product name or SKU…"
            className="min-h-[2.75rem] w-56 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 placeholder:text-gray-400 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          />
          <button
            type="submit"
            className="inline-flex min-h-[2.75rem] items-center rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
          >
            Search
          </button>
        </form>

        {/* Group by warehouse toggle (only when viewing all warehouses) */}
        {!warehouse && (
          <button
            type="button"
            onClick={() => setIsGrouped((prev) => !prev)}
            aria-pressed={isGrouped}
            className={[
              'inline-flex min-h-[2.75rem] items-center rounded-md border px-3 py-2 text-sm font-medium',
              'motion-safe:transition-colors motion-safe:duration-150',
              'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
              isGrouped
                ? 'border-blue-300 bg-blue-50 text-blue-700'
                : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50',
            ].join(' ')}
          >
            {isGrouped ? 'Grouped' : 'Flat View'}
          </button>
        )}
      </div>

      {/* ─── Error Banner ──────────────────────────────────────── */}
      {isError && (
        <div
          className="mb-4 rounded-md border border-red-200 bg-red-50 p-4"
          role="alert"
        >
          <p className="text-sm font-medium text-red-800">
            Failed to load stock data
          </p>
          <p className="mt-1 text-sm text-red-600">{errorMessage}</p>
          <button
            type="button"
            onClick={() => {
              void stockQuery.refetch();
            }}
            className="mt-2 text-sm font-medium text-red-700 underline hover:text-red-900 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
          >
            Retry
          </button>
        </div>
      )}

      {/* ─── Grouped View ──────────────────────────────────────── */}
      {groupedData && !isError ? (
        <div className="space-y-4">
          {/* Empty state for grouped view */}
          {groupedData.size === 0 && !isLoading && (
            <div
              className="rounded-md bg-blue-50 p-4 text-sm text-blue-700"
              role="status"
            >
              No stock items found matching the current filters.
            </div>
          )}

          {/* Loading overlay for grouped view */}
          {isLoading && stockItems.length === 0 && (
            <div className="flex items-center justify-center py-12">
              <p className="text-sm text-gray-500">Loading stock data…</p>
            </div>
          )}

          {/* Warehouse groups */}
          {Array.from(groupedData.entries()).map(
            ([warehouseName, group]) => {
              const isCollapsed = collapsedGroups.has(warehouseName);
              const groupQty = group.items.reduce(
                (sum, item) => sum + item.quantity,
                0,
              );
              const groupReserved = group.items.reduce(
                (sum, item) => sum + item.reserved,
                0,
              );
              const groupAvailable = group.items.reduce(
                (sum, item) => sum + item.available,
                0,
              );

              return (
                <div
                  key={warehouseName}
                  className="overflow-hidden rounded-lg border border-gray-200"
                >
                  {/* Group header — collapsible toggle */}
                  <button
                    type="button"
                    onClick={() => toggleGroup(warehouseName)}
                    aria-expanded={!isCollapsed}
                    className="flex w-full items-center justify-between bg-gray-50 px-4 py-3 text-start hover:bg-gray-100 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
                  >
                    <div className="flex items-center gap-3">
                      <span
                        className="text-sm text-gray-400"
                        aria-hidden="true"
                      >
                        {isCollapsed ? '▶' : '▼'}
                      </span>
                      <span className="text-sm font-semibold text-gray-900">
                        {warehouseName}
                      </span>
                      <span className="rounded-full bg-gray-200 px-2 py-0.5 text-xs font-medium text-gray-600">
                        {group.items.length} item
                        {group.items.length !== 1 ? 's' : ''}
                      </span>
                    </div>
                    <div className="flex items-center gap-4 text-xs text-gray-500">
                      <span>
                        Qty:{' '}
                        <span className="font-medium text-gray-700">
                          {groupQty.toLocaleString()}
                        </span>
                      </span>
                      <span>
                        Reserved:{' '}
                        <span className="font-medium text-gray-700">
                          {groupReserved.toLocaleString()}
                        </span>
                      </span>
                      <span>
                        Available:{' '}
                        <span className="font-medium text-gray-700">
                          {groupAvailable.toLocaleString()}
                        </span>
                      </span>
                    </div>
                  </button>

                  {/* Group body — DataTable for this warehouse's items */}
                  {!isCollapsed && (
                    <DataTable<StockItem>
                      data={group.items}
                      columns={columns}
                      showHeader={true}
                      showFooter={false}
                      striped={true}
                      hover={true}
                      small={true}
                      loading={isLoading}
                      name={`stock-group-${group.warehouseId}`}
                    />
                  )}
                </div>
              );
            },
          )}

          {/* ─── Manual Pagination for Grouped View ──────────── */}
          {totalCount > pageSize && (
            <div className="flex items-center justify-between px-2 py-3">
              <span className="text-sm text-gray-700">
                Showing{' '}
                <span className="font-medium">
                  {(page - 1) * pageSize + 1}
                </span>{' '}
                to{' '}
                <span className="font-medium">
                  {Math.min(page * pageSize, totalCount)}
                </span>{' '}
                of{' '}
                <span className="font-medium">
                  {totalCount.toLocaleString()}
                </span>{' '}
                records
              </span>
              <nav
                className="flex items-center gap-1"
                aria-label="Stock list pagination"
              >
                <button
                  type="button"
                  onClick={() => handleManualPageChange(page - 1)}
                  disabled={page <= 1}
                  aria-label="Previous page"
                  className={[
                    'inline-flex min-h-[2.75rem] min-w-[2.75rem] items-center justify-center rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700',
                    page <= 1
                      ? 'cursor-not-allowed opacity-50'
                      : 'hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
                  ].join(' ')}
                >
                  ‹ Prev
                </button>
                <span className="px-2 text-sm text-gray-700">
                  Page {page} of {pageCount}
                </span>
                <button
                  type="button"
                  onClick={() => handleManualPageChange(page + 1)}
                  disabled={page >= pageCount}
                  aria-label="Next page"
                  className={[
                    'inline-flex min-h-[2.75rem] min-w-[2.75rem] items-center justify-center rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700',
                    page >= pageCount
                      ? 'cursor-not-allowed opacity-50'
                      : 'hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
                  ].join(' ')}
                >
                  Next ›
                </button>
              </nav>
            </div>
          )}
        </div>
      ) : !isError ? (
        /* ─── Flat DataTable View ──────────────────────────────── */
        <DataTable<StockItem>
          data={stockItems}
          columns={columns}
          totalCount={totalCount}
          pageSize={pageSize}
          currentPage={page}
          loading={isLoading}
          showHeader={true}
          showFooter={true}
          striped={true}
          hover={true}
          name="stock-list"
          emptyText="No stock items found matching the current filters."
        />
      ) : null}
    </div>
  );
}

export default StockList;
