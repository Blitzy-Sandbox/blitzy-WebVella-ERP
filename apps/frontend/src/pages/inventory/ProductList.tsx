/**
 * ProductList.tsx — Product Listing Page with Category Filters and Search
 *
 * Replaces the monolith's RecordList.cshtml.cs pattern for inventory/product entities.
 * Features server-side pagination, sorting, text search, and category/status filtering.
 * All filter, sort, and pagination state is URL-driven via React Router useSearchParams
 * for bookmarkable and shareable product list views.
 *
 * Data fetching uses TanStack Query 5 for automatic caching, background refetching,
 * and stale-while-revalidate, replacing the monolith's synchronous RecordManager.Find().
 *
 * @module apps/frontend/src/pages/inventory
 */

import { useState, useMemo, useCallback } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';
import { get } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import type { ApiResponse } from '../../api/client';

/* ═══════════════════════════════════════════════════════════════
   TypeScript Interfaces
   ═══════════════════════════════════════════════════════════════ */

/** Product entity from the Inventory bounded-context (DynamoDB-backed). */
interface Product {
  [key: string]: unknown;
  id: string;
  name: string;
  sku: string;
  description: string;
  price: number;
  category: string;
  status: 'active' | 'inactive' | 'discontinued';
  stock_quantity: number;
  created_on: string;
  updated_on: string;
}

/** Paginated response shape returned by GET /v1/inventory/products. */
interface ProductListResponse {
  data: Product[];
  total: number;
  page: number;
  pageSize: number;
}

/* ═══════════════════════════════════════════════════════════════
   Constants
   ═══════════════════════════════════════════════════════════════ */

/** Default number of products displayed per page. */
const DEFAULT_PAGE_SIZE = 15;

/** Available product category filter options. */
const CATEGORY_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: '', label: 'All Categories' },
  { value: 'electronics', label: 'Electronics' },
  { value: 'clothing', label: 'Clothing' },
  { value: 'food', label: 'Food & Beverage' },
  { value: 'furniture', label: 'Furniture' },
  { value: 'office', label: 'Office Supplies' },
  { value: 'other', label: 'Other' },
];

/** Available product status filter options. */
const STATUS_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: '', label: 'All Statuses' },
  { value: 'active', label: 'Active' },
  { value: 'inactive', label: 'Inactive' },
  { value: 'discontinued', label: 'Discontinued' },
];

/* ═══════════════════════════════════════════════════════════════
   Helper Functions
   ═══════════════════════════════════════════════════════════════ */

/** Format a numeric amount as US-dollar currency string. */
function formatCurrency(amount: number): string {
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(amount);
}

/** Return colour class and label for the stock-level indicator dot. */
function getStockLevelIndicator(quantity: number): {
  colorClass: string;
  dotClass: string;
  label: string;
} {
  if (quantity <= 0) {
    return { colorClass: 'text-red-600', dotClass: 'bg-red-500', label: 'Out of Stock' };
  }
  if (quantity <= 10) {
    return { colorClass: 'text-yellow-600', dotClass: 'bg-yellow-500', label: 'Low Stock' };
  }
  return { colorClass: 'text-green-600', dotClass: 'bg-green-500', label: 'In Stock' };
}

/** Return Tailwind badge classes and display label for a product status. */
function getStatusBadgeClasses(status: string): {
  bg: string;
  text: string;
  label: string;
} {
  switch (status) {
    case 'active':
      return { bg: 'bg-green-100', text: 'text-green-800', label: 'Active' };
    case 'inactive':
      return { bg: 'bg-gray-100', text: 'text-gray-800', label: 'Inactive' };
    case 'discontinued':
      return { bg: 'bg-red-100', text: 'text-red-800', label: 'Discontinued' };
    default:
      return { bg: 'bg-gray-100', text: 'text-gray-600', label: status || 'Unknown' };
  }
}

/* ═══════════════════════════════════════════════════════════════
   ProductList Component
   ═══════════════════════════════════════════════════════════════ */

/**
 * Product listing page for the Inventory bounded-context.
 *
 * Replaces RecordList.cshtml.cs with:
 * - TanStack Query 5 for server-state management (cache, background refetch)
 * - URL-driven pagination, sorting, and filtering via React Router useSearchParams
 * - DataTable component replacing the monolith's PcGrid / wv-grid tag helper
 * - Tailwind CSS 4.x replacing Bootstrap 4
 */
export default function ProductList(): React.ReactElement {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const [isFilterOpen, setIsFilterOpen] = useState<boolean>(false);

  /* ── Read current state from URL search params ─────────────── */
  const page = parseInt(searchParams.get('page') ?? '1', 10);
  const pageSize = parseInt(
    searchParams.get('pageSize') ?? String(DEFAULT_PAGE_SIZE),
    10,
  );
  const sortBy = searchParams.get('sortBy') ?? 'name';
  const sortOrder = (searchParams.get('sortOrder') ?? 'asc') as 'asc' | 'desc';
  const search = searchParams.get('search') ?? '';
  const category = searchParams.get('category') ?? '';
  const status = searchParams.get('status') ?? '';

  /* ── Fetch product list from Inventory service ─────────────── */
  const {
    data: response,
    isLoading,
    isError,
    error,
  } = useQuery<ApiResponse<ProductListResponse>>({
    queryKey: [
      'inventory',
      'products',
      { page, pageSize, sortBy, sortOrder, search, category, status },
    ],
    queryFn: async (): Promise<ApiResponse<ProductListResponse>> => {
      const params: Record<string, unknown> = {
        page,
        pageSize,
        sortBy,
        sortOrder,
      };
      if (search) params.search = search;
      if (category) params.category = category;
      if (status) params.status = status;
      return get<ProductListResponse>('/v1/inventory/products', params);
    },
  });

  /* ── Extract data from API response envelope ───────────────── */
  const products: Product[] =
    response?.success && response.object ? response.object.data ?? [] : [];
  const totalCount: number =
    response?.success && response.object ? response.object.total ?? 0 : 0;

  /* ── Filter helpers ────────────────────────────────────────── */
  const hasActiveFilters = Boolean(search || category || status);

  /** Update a single filter parameter and reset to page 1. */
  const updateFilter = useCallback(
    (key: string, value: string) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        if (value) {
          next.set(key, value);
        } else {
          next.delete(key);
        }
        next.set('page', '1');
        return next;
      });
    },
    [setSearchParams],
  );

  /** Clear all active filters and reset to page 1. */
  const clearAllFilters = useCallback(() => {
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      next.delete('search');
      next.delete('category');
      next.delete('status');
      next.set('page', '1');
      return next;
    });
  }, [setSearchParams]);

  /** Toggle the visibility of the filter panel. */
  const toggleFilterPanel = useCallback(() => {
    setIsFilterOpen((prev) => !prev);
  }, []);

  /* ── DataTable column definitions ──────────────────────────── */
  const columns = useMemo<DataTableColumn<Product>[]>(
    () => [
      {
        id: 'sku',
        label: 'SKU',
        accessorKey: 'sku',
        sortable: true,
        width: '120px',
        noWrap: true,
        cell: (value: unknown) => (
          <span className="font-mono text-sm text-gray-700">
            {String(value ?? '')}
          </span>
        ),
      },
      {
        id: 'name',
        label: 'Name',
        accessorKey: 'name',
        sortable: true,
        cell: (value: unknown, record: Product) => (
          <Link
            to={`/inventory/products/${record.id}`}
            className="font-medium text-blue-600 hover:text-blue-800 hover:underline"
          >
            {String(value ?? '')}
          </Link>
        ),
      },
      {
        id: 'category',
        label: 'Category',
        accessorKey: 'category',
        sortable: true,
        width: '140px',
        cell: (value: unknown) => {
          const cat = String(value ?? '');
          return cat ? (
            <span className="inline-flex items-center rounded-full bg-blue-50 px-2.5 py-0.5 text-xs font-medium text-blue-700 ring-1 ring-inset ring-blue-600/20">
              {cat}
            </span>
          ) : (
            <span className="text-gray-400">—</span>
          );
        },
      },
      {
        id: 'price',
        label: 'Price',
        accessorKey: 'price',
        sortable: true,
        width: '120px',
        horizontalAlign: 'right',
        noWrap: true,
        cell: (value: unknown) => (
          <span className="font-medium tabular-nums text-gray-900">
            {formatCurrency(typeof value === 'number' ? value : 0)}
          </span>
        ),
      },
      {
        id: 'stock_quantity',
        label: 'Stock Level',
        accessorKey: 'stock_quantity',
        sortable: true,
        width: '130px',
        horizontalAlign: 'right',
        noWrap: true,
        cell: (value: unknown) => {
          const qty = typeof value === 'number' ? value : 0;
          const indicator = getStockLevelIndicator(qty);
          return (
            <div className="flex items-center justify-end gap-2">
              <span
                className={`font-medium tabular-nums ${indicator.colorClass}`}
              >
                {qty}
              </span>
              <span
                className={`inline-block h-2 w-2 rounded-full ${indicator.dotClass}`}
                title={indicator.label}
                role="img"
                aria-label={indicator.label}
              />
            </div>
          );
        },
      },
      {
        id: 'status',
        label: 'Status',
        accessorKey: 'status',
        sortable: true,
        width: '130px',
        cell: (value: unknown) => {
          const badge = getStatusBadgeClasses(String(value ?? ''));
          return (
            <span
              className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${badge.bg} ${badge.text}`}
            >
              {badge.label}
            </span>
          );
        },
      },
      {
        id: 'actions',
        label: 'Actions',
        sortable: false,
        width: '100px',
        horizontalAlign: 'center',
        cell: (_value: unknown, record: Product) => (
          <div className="flex items-center justify-center gap-1">
            <Link
              to={`/inventory/products/${record.id}`}
              className="inline-flex items-center justify-center rounded p-1.5 text-gray-500 hover:bg-gray-100 hover:text-gray-700"
              title="View product"
              aria-label={`View ${record.name}`}
            >
              <svg
                className="h-4 w-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"
                />
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z"
                />
              </svg>
            </Link>
            <button
              type="button"
              onClick={() =>
                navigate(`/inventory/products/${record.id}/manage`)
              }
              className="inline-flex items-center justify-center rounded p-1.5 text-gray-500 hover:bg-gray-100 hover:text-gray-700"
              title="Edit product"
              aria-label={`Edit ${record.name}`}
            >
              <svg
                className="h-4 w-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"
                />
              </svg>
            </button>
          </div>
        ),
      },
    ],
    [navigate],
  );

  /* ── Render ─────────────────────────────────────────────────── */
  return (
    <div className="space-y-6">
      {/* Page Header */}
      <div className="flex flex-col gap-1">
        <div className="flex items-center gap-3">
          <h1 className="text-2xl font-bold text-gray-900">Products</h1>
          {!isLoading && (
            <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600">
              {totalCount} {totalCount === 1 ? 'product' : 'products'}
            </span>
          )}
        </div>
        <p className="text-sm text-gray-500">
          Manage your product inventory, track stock levels, and organize by
          category.
        </p>
      </div>

      {/* Actions Row */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => navigate('/inventory/products/create')}
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3.5 py-2 text-sm font-semibold text-white shadow-sm hover:bg-blue-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            <svg
              className="h-4 w-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M12 4v16m8-8H4"
              />
            </svg>
            Add Product
          </button>
        </div>

        <button
          type="button"
          onClick={toggleFilterPanel}
          className={`inline-flex items-center gap-1.5 rounded-md px-3 py-2 text-sm font-medium shadow-sm ring-1 ring-inset ${
            isFilterOpen || hasActiveFilters
              ? 'bg-blue-50 text-blue-700 ring-blue-200'
              : 'bg-white text-gray-700 ring-gray-300 hover:bg-gray-50'
          }`}
          aria-expanded={isFilterOpen}
          aria-controls="product-filter-panel"
        >
          <svg
            className="h-4 w-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth={2}
              d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z"
            />
          </svg>
          Filters
          {hasActiveFilters && (
            <span className="inline-flex h-4 w-4 items-center justify-center rounded-full bg-blue-600 text-[10px] font-bold text-white">
              {[search, category, status].filter(Boolean).length}
            </span>
          )}
        </button>
      </div>

      {/* Filter Panel (collapsible) */}
      {isFilterOpen && (
        <div
          id="product-filter-panel"
          className="rounded-lg border border-gray-200 bg-gray-50 p-4"
          role="region"
          aria-label="Product filters"
        >
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {/* Search Input */}
            <div>
              <label
                htmlFor="product-search"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Search
              </label>
              <div className="relative">
                <div className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3">
                  <svg
                    className="h-4 w-4 text-gray-400"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                    aria-hidden="true"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
                    />
                  </svg>
                </div>
                <input
                  id="product-search"
                  type="search"
                  placeholder="Search by name, SKU…"
                  value={search}
                  onChange={(e) => updateFilter('search', e.target.value)}
                  className="block w-full rounded-md border border-gray-300 bg-white py-2 pe-3 ps-9 text-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
                />
              </div>
            </div>

            {/* Category Dropdown */}
            <div>
              <label
                htmlFor="product-category"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Category
              </label>
              <select
                id="product-category"
                value={category}
                onChange={(e) => updateFilter('category', e.target.value)}
                className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
              >
                {CATEGORY_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>

            {/* Status Dropdown */}
            <div>
              <label
                htmlFor="product-status"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Status
              </label>
              <select
                id="product-status"
                value={status}
                onChange={(e) => updateFilter('status', e.target.value)}
                className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
              >
                {STATUS_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>

            {/* Clear All Button */}
            <div className="flex items-end">
              {hasActiveFilters && (
                <button
                  type="button"
                  onClick={clearAllFilters}
                  className="inline-flex items-center gap-1 rounded-md px-3 py-2 text-sm font-medium text-red-600 hover:bg-red-50 hover:text-red-700"
                >
                  <svg
                    className="h-4 w-4"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                    aria-hidden="true"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M6 18L18 6M6 6l12 12"
                    />
                  </svg>
                  Clear All
                </button>
              )}
            </div>
          </div>
        </div>
      )}

      {/* Error State */}
      {isError && (
        <div
          className="rounded-lg border border-red-200 bg-red-50 p-4"
          role="alert"
        >
          <div className="flex items-center gap-2">
            <svg
              className="h-5 w-5 shrink-0 text-red-500"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"
              />
            </svg>
            <p className="text-sm font-medium text-red-800">
              Failed to load products.{' '}
              {error instanceof Error
                ? error.message
                : 'Please try again later.'}
            </p>
          </div>
        </div>
      )}

      {/* Data Table */}
      <DataTable<Product>
        data={products}
        columns={columns}
        totalCount={totalCount}
        pageSize={pageSize || DEFAULT_PAGE_SIZE}
        loading={isLoading}
        emptyText="No products found. Adjust your filters or add a new product."
        striped
        hover
      />
    </div>
  );
}
