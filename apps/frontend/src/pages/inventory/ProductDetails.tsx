/**
 * ProductDetails — Product Detail View with Stock Levels and Pricing History
 *
 * Replaces the monolith's `RecordDetails.cshtml.cs` page-model pattern applied
 * to product/inventory entities.  Preserves full behavioural parity:
 *
 * - **Page lifecycle** (`OnGet` → `Init` → `RecordsExists` check → render)
 *   becomes four concurrent TanStack Query fetches (product, stock, pricing,
 *   related records) with 404 detection on the primary query.
 *
 * - **Delete flow** (`OnPost op=delete` → `RecordManager.DeleteRecord` →
 *   redirect to list) becomes a `useMutation` calling `DELETE /v1/…` with
 *   `onSuccess` cache-invalidation + navigate.
 *
 * - **Related-data display** (EQL `$relation` joins in `TaskService.cs`)
 *   becomes a dedicated `/related` endpoint rendered via DataTable tabs.
 *
 * Source files:
 *   WebVella.Erp.Web/Pages/RecordDetails.cshtml.cs
 *   WebVella.Erp.Web/Pages/RecordDetails.cshtml
 *   WebVella.Erp/Api/RecordManager.cs
 *   WebVella.Erp.Plugins.Project/Services/TaskService.cs
 *
 * @module pages/inventory/ProductDetails
 */

import { useState, useCallback, useMemo } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams, useNavigate, Link } from 'react-router-dom';

import { get, del } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Modal from '../../components/common/Modal';
import Chart, { ChartType } from '../../components/common/Chart';

// ---------------------------------------------------------------------------
// TypeScript Interfaces
// ---------------------------------------------------------------------------

/** Product status values mirroring the inventory service domain model. */
type ProductStatus = 'active' | 'inactive' | 'discontinued';

/** Full product record returned by GET /v1/inventory/products/:id. */
interface Product {
  id: string;
  name: string;
  sku: string;
  description: string;
  category: string;
  status: ProductStatus;
  price: number;
  currency: string;
  unit_of_measure: string;
  reorder_level: number;
  barcode: string;
  weight: number | null;
  dimensions: string;
  created_on: string;
  updated_on: string;
}

/** Per-warehouse stock level entry. */
interface StockLevel {
  warehouse_name: string;
  quantity: number;
  reserved: number;
  available: number;
  [key: string]: unknown;
}

/** Aggregated stock response from GET /v1/inventory/products/:id/stock. */
interface StockResponse {
  total_stock: number;
  available_stock: number;
  reserved_stock: number;
  levels: StockLevel[];
}

/** Individual pricing history entry. */
interface PricingHistoryEntry {
  date: string;
  price: number;
  changed_by: string;
  note: string;
  [key: string]: unknown;
}

/** Pricing history response. */
interface PricingHistoryResponse {
  entries: PricingHistoryEntry[];
}

/** A single related record (order, supplier, etc.). */
interface RelatedRecord {
  id: string;
  type: string;
  name: string;
  status: string;
  date: string;
  [key: string]: unknown;
}

/** Related records response grouped by relation type. */
interface RelatedRecordsResponse {
  orders: RelatedRecord[];
  suppliers: RelatedRecord[];
}

// ---------------------------------------------------------------------------
// Helper — currency formatter
// ---------------------------------------------------------------------------

/**
 * Formats a number as a currency string using the Intl API.
 * Falls back to USD when no currency code is provided.
 */
function formatCurrency(value: number, currency = 'USD'): string {
  try {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency,
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }).format(value);
  } catch {
    return `${currency} ${value.toFixed(2)}`;
  }
}

/**
 * Formats an ISO 8601 date string to a localised human-readable form.
 * Returns an em-dash when the input is falsy or unparseable.
 */
function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat('en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date);
}

/**
 * Formats an ISO 8601 date string to a short date (no time).
 */
function formatDate(iso: string | null | undefined): string {
  if (!iso) return '—';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat('en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  }).format(date);
}

// ---------------------------------------------------------------------------
// Stock-level indicator helpers
// ---------------------------------------------------------------------------

interface StockIndicator {
  label: string;
  colorClass: string;
  dotClass: string;
}

/**
 * Derives a color-coded stock-level indicator from the current stock
 * quantity and the product's reorder level.
 *
 * - Green  → stock > reorder_level × 2  ("In Stock")
 * - Yellow → 0 < stock ≤ reorder_level  ("Low Stock")
 * - Red    → stock = 0                  ("Out of Stock")
 */
function getStockIndicator(
  totalStock: number,
  reorderLevel: number,
): StockIndicator {
  if (totalStock <= 0) {
    return {
      label: 'Out of Stock',
      colorClass: 'text-red-700 bg-red-50 ring-red-600/20',
      dotClass: 'bg-red-500',
    };
  }
  if (totalStock <= reorderLevel) {
    return {
      label: 'Low Stock',
      colorClass: 'text-yellow-800 bg-yellow-50 ring-yellow-600/20',
      dotClass: 'bg-yellow-500',
    };
  }
  return {
    label: 'In Stock',
    colorClass: 'text-green-700 bg-green-50 ring-green-600/20',
    dotClass: 'bg-green-500',
  };
}

// ---------------------------------------------------------------------------
// Status badge helper
// ---------------------------------------------------------------------------

function statusBadgeClass(status: ProductStatus): string {
  switch (status) {
    case 'active':
      return 'text-green-700 bg-green-50 ring-green-600/20';
    case 'inactive':
      return 'text-gray-600 bg-gray-50 ring-gray-500/10';
    case 'discontinued':
      return 'text-red-700 bg-red-50 ring-red-600/20';
    default:
      return 'text-gray-600 bg-gray-50 ring-gray-500/10';
  }
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * ProductDetails page component.
 *
 * Mounted at `/inventory/products/:id`.
 * Lazy-loaded by the router via `React.lazy()`.
 */
export default function ProductDetails(): React.JSX.Element {
  // ── Route params & navigation ───────────────────────────────
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  // ── Local UI state ──────────────────────────────────────────
  const [deleteModalVisible, setDeleteModalVisible] = useState(false);
  const [activeRelatedTab, setActiveRelatedTab] = useState<'orders' | 'suppliers'>('orders');
  const [pricingCollapsed, setPricingCollapsed] = useState(false);
  const [relatedCollapsed, setRelatedCollapsed] = useState(false);

  // ── Data fetching: product detail ───────────────────────────
  const {
    data: productResponse,
    isLoading: productLoading,
    isError: productError,
    error: productFetchError,
  } = useQuery<ApiResponse<Product>, ApiError>({
    queryKey: ['inventory', 'products', id],
    queryFn: () => get<Product>(`/inventory/products/${id}`),
    enabled: Boolean(id),
  });

  // ── Data fetching: stock levels ─────────────────────────────
  const {
    data: stockResponse,
    isLoading: stockLoading,
  } = useQuery<ApiResponse<StockResponse>, ApiError>({
    queryKey: ['inventory', 'products', id, 'stock'],
    queryFn: () => get<StockResponse>(`/inventory/products/${id}/stock`),
    enabled: Boolean(id),
  });

  // ── Data fetching: pricing history ──────────────────────────
  const {
    data: pricingResponse,
    isLoading: pricingLoading,
  } = useQuery<ApiResponse<PricingHistoryResponse>, ApiError>({
    queryKey: ['inventory', 'products', id, 'pricing-history'],
    queryFn: () => get<PricingHistoryResponse>(`/inventory/products/${id}/pricing-history`),
    enabled: Boolean(id),
  });

  // ── Data fetching: related records ──────────────────────────
  const {
    data: relatedResponse,
    isLoading: relatedLoading,
  } = useQuery<ApiResponse<RelatedRecordsResponse>, ApiError>({
    queryKey: ['inventory', 'products', id, 'related'],
    queryFn: () => get<RelatedRecordsResponse>(`/inventory/products/${id}/related`),
    enabled: Boolean(id),
  });

  // ── Delete mutation ─────────────────────────────────────────
  const deleteMutation = useMutation<ApiResponse<void>, ApiError>({
    mutationFn: () => del<void>(`/inventory/products/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['inventory', 'products'] });
      navigate('/inventory/products');
    },
  });

  // ── Extracted data ──────────────────────────────────────────
  const product = productResponse?.object ?? null;
  const stock = stockResponse?.object ?? null;
  const pricingHistory = pricingResponse?.object?.entries ?? [];
  const relatedRecords = relatedResponse?.object ?? { orders: [], suppliers: [] };

  // ── Memoised: stock indicator ───────────────────────────────
  const stockIndicator = useMemo<StockIndicator>(() => {
    if (!product || !stock) {
      return { label: '—', colorClass: 'text-gray-600 bg-gray-50 ring-gray-500/10', dotClass: 'bg-gray-400' };
    }
    return getStockIndicator(stock.total_stock, product.reorder_level);
  }, [product, stock]);

  // ── Memoised: pricing chart data ────────────────────────────
  const pricingChartLabels = useMemo<string[]>(
    () => pricingHistory.map((entry) => formatDate(entry.date)),
    [pricingHistory],
  );

  const pricingChartDatasets = useMemo(
    () => [
      {
        label: 'Price',
        data: pricingHistory.map((entry) => entry.price),
        borderColor: '#2563eb',
        backgroundColor: '#93c5fd',
        borderWidth: 2,
        fill: false,
      },
    ],
    [pricingHistory],
  );

  // ── Memoised: stock table columns ──────────────────────────
  const stockColumns = useMemo<DataTableColumn<StockLevel>[]>(
    () => [
      { id: 'warehouse_name', label: 'Location', accessorKey: 'warehouse_name', sortable: true },
      {
        id: 'quantity',
        label: 'Quantity',
        accessorKey: 'quantity',
        sortable: true,
        horizontalAlign: 'right',
        cell: (value) => String(value ?? 0),
      },
      {
        id: 'reserved',
        label: 'Reserved',
        accessorKey: 'reserved',
        sortable: true,
        horizontalAlign: 'right',
        cell: (value) => String(value ?? 0),
      },
      {
        id: 'available',
        label: 'Available',
        accessorKey: 'available',
        sortable: true,
        horizontalAlign: 'right',
        cell: (value) => String(value ?? 0),
      },
    ],
    [],
  );

  // ── Memoised: related record columns (orders) ──────────────
  const orderColumns = useMemo<DataTableColumn<RelatedRecord>[]>(
    () => [
      { id: 'name', label: 'Order', accessorKey: 'name', sortable: true },
      { id: 'status', label: 'Status', accessorKey: 'status', sortable: true },
      {
        id: 'date',
        label: 'Date',
        accessorKey: 'date',
        sortable: true,
        cell: (value) => formatDate(value as string),
      },
    ],
    [],
  );

  // ── Memoised: related record columns (suppliers) ───────────
  const supplierColumns = useMemo<DataTableColumn<RelatedRecord>[]>(
    () => [
      { id: 'name', label: 'Supplier', accessorKey: 'name', sortable: true },
      { id: 'status', label: 'Status', accessorKey: 'status', sortable: true },
      {
        id: 'date',
        label: 'Date',
        accessorKey: 'date',
        sortable: true,
        cell: (value) => formatDate(value as string),
      },
    ],
    [],
  );

  // ── Memoised: pricing history table columns ────────────────
  const pricingHistoryColumns = useMemo<DataTableColumn<PricingHistoryEntry>[]>(
    () => [
      {
        id: 'date',
        label: 'Date',
        accessorKey: 'date',
        sortable: true,
        cell: (value) => formatDate(value as string),
      },
      {
        id: 'price',
        label: 'Price',
        accessorKey: 'price',
        sortable: true,
        horizontalAlign: 'right',
        cell: (value) => formatCurrency(value as number, product?.currency),
      },
      { id: 'changed_by', label: 'Changed By', accessorKey: 'changed_by' },
      { id: 'note', label: 'Note', accessorKey: 'note' },
    ],
    [product?.currency],
  );

  // ── Callbacks ───────────────────────────────────────────────
  const handleOpenDeleteModal = useCallback(() => {
    setDeleteModalVisible(true);
  }, []);

  const handleCloseDeleteModal = useCallback(() => {
    setDeleteModalVisible(false);
  }, []);

  const handleConfirmDelete = useCallback(() => {
    deleteMutation.mutate();
    setDeleteModalVisible(false);
  }, [deleteMutation]);

  const handleNavigateEdit = useCallback(() => {
    navigate(`/inventory/products/${id}/edit`);
  }, [navigate, id]);

  const handleNavigateStockAdjust = useCallback(() => {
    navigate(`/inventory/stock/adjust?productId=${id}`);
  }, [navigate, id]);

  const handleTabChange = useCallback((tab: 'orders' | 'suppliers') => {
    setActiveRelatedTab(tab);
  }, []);

  const handleTogglePricing = useCallback(() => {
    setPricingCollapsed((prev) => !prev);
  }, []);

  const handleToggleRelated = useCallback(() => {
    setRelatedCollapsed((prev) => !prev);
  }, []);

  // ── Loading state ───────────────────────────────────────────
  if (productLoading) {
    return (
      <div className="flex items-center justify-center min-h-[60vh]">
        <div className="flex flex-col items-center gap-3">
          <div
            className="inline-block h-8 w-8 animate-spin rounded-full border-4 border-solid border-blue-600 border-e-transparent"
            role="status"
            aria-label="Loading product details"
          />
          <p className="text-sm text-gray-500">Loading product details…</p>
        </div>
      </div>
    );
  }

  // ── Error state ─────────────────────────────────────────────
  if (productError) {
    const is404 = productFetchError?.status === 404;
    if (is404) {
      return (
        <div className="flex flex-col items-center justify-center min-h-[60vh] gap-4">
          <svg
            className="h-16 w-16 text-gray-400"
            fill="none"
            viewBox="0 0 24 24"
            strokeWidth={1.5}
            stroke="currentColor"
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M12 9v3.75m9-.75a9 9 0 1 1-18 0 9 9 0 0 1 18 0Zm-9 3.75h.008v.008H12v-.008Z"
            />
          </svg>
          <h2 className="text-xl font-semibold text-gray-900">Product Not Found</h2>
          <p className="text-sm text-gray-500">
            The product you are looking for does not exist or has been removed.
          </p>
          <Link
            to="/inventory/products"
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-blue-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            ← Back to Products
          </Link>
        </div>
      );
    }

    return (
      <div className="rounded-md bg-red-50 p-4 my-6" role="alert">
        <div className="flex">
          <svg
            className="h-5 w-5 text-red-400 shrink-0"
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M10 18a8 8 0 1 0 0-16 8 8 0 0 0 0 16ZM8.28 7.22a.75.75 0 0 0-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 1 0 1.06 1.06L10 11.06l1.72 1.72a.75.75 0 1 0 1.06-1.06L11.06 10l1.72-1.72a.75.75 0 0 0-1.06-1.06L10 8.94 8.28 7.22Z"
              clipRule="evenodd"
            />
          </svg>
          <div className="ms-3">
            <h3 className="text-sm font-medium text-red-800">Failed to load product</h3>
            <p className="mt-1 text-sm text-red-700">
              {productFetchError?.message || 'An unexpected error occurred. Please try again.'}
            </p>
          </div>
        </div>
      </div>
    );
  }

  // ── Product not in response ─────────────────────────────────
  if (!product) {
    return (
      <div className="flex flex-col items-center justify-center min-h-[60vh] gap-4">
        <h2 className="text-xl font-semibold text-gray-900">Product Not Found</h2>
        <p className="text-sm text-gray-500">No product data available.</p>
        <Link
          to="/inventory/products"
          className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-blue-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          ← Back to Products
        </Link>
      </div>
    );
  }

  // ── Main render ─────────────────────────────────────────────
  return (
    <div className="mx-auto max-w-7xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ─── Page Header ──────────────────────────────────────── */}
      <div className="mb-6">
        <Link
          to="/inventory/products"
          className="inline-flex items-center gap-1 text-sm font-medium text-blue-600 hover:text-blue-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor" aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" d="M15.75 19.5 8.25 12l7.5-7.5" />
          </svg>
          Back to Products
        </Link>
      </div>

      <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-3">
            <h1 className="text-2xl font-bold tracking-tight text-gray-900 truncate">
              {product.name}
            </h1>
            <span
              className={`inline-flex items-center rounded-md px-2 py-1 text-xs font-medium ring-1 ring-inset ${statusBadgeClass(product.status)}`}
            >
              {product.status.charAt(0).toUpperCase() + product.status.slice(1)}
            </span>
          </div>
          <p className="mt-1 text-sm text-gray-500">
            SKU: <code className="font-mono text-gray-700">{product.sku}</code>
          </p>
        </div>

        {/* Header Actions */}
        <div className="flex shrink-0 gap-2">
          <button
            type="button"
            onClick={handleNavigateStockAdjust}
            className="inline-flex items-center gap-1.5 rounded-md bg-white px-3 py-2 text-sm font-semibold text-gray-900 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
          >
            <svg className="h-4 w-4 text-gray-500" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor" aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" d="M12 4.5v15m7.5-7.5h-15" />
            </svg>
            Adjust Stock
          </button>
          <button
            type="button"
            onClick={handleNavigateEdit}
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-blue-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor" aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" d="m16.862 4.487 1.687-1.688a1.875 1.875 0 1 1 2.652 2.652L10.582 16.07a4.5 4.5 0 0 1-1.897 1.13L6 18l.8-2.685a4.5 4.5 0 0 1 1.13-1.897l8.932-8.931Z" />
            </svg>
            Edit
          </button>
          <button
            type="button"
            onClick={handleOpenDeleteModal}
            className="inline-flex items-center gap-1.5 rounded-md bg-red-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
          >
            <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor" aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" d="m14.74 9-.346 9m-4.788 0L9.26 9m9.968-3.21c.342.052.682.107 1.022.166m-1.022-.165L18.16 19.673a2.25 2.25 0 0 1-2.244 2.077H8.084a2.25 2.25 0 0 1-2.244-2.077L4.772 5.79m14.456 0a48.108 48.108 0 0 0-3.478-.397m-12 .562c.34-.059.68-.114 1.022-.165m0 0a48.11 48.11 0 0 1 3.478-.397m7.5 0v-.916c0-1.18-.91-2.164-2.09-2.201a51.964 51.964 0 0 0-3.32 0c-1.18.037-2.09 1.022-2.09 2.201v.916m7.5 0a48.667 48.667 0 0 0-7.5 0" />
            </svg>
            Delete
          </button>
        </div>
      </div>

      {/* Delete mutation error */}
      {deleteMutation.isError && (
        <div className="mt-4 rounded-md bg-red-50 p-3" role="alert">
          <p className="text-sm text-red-700">
            {deleteMutation.error?.message || 'Failed to delete product. Please try again.'}
          </p>
        </div>
      )}

      {/* ─── Product Info Section ────────────────────────────── */}
      <section className="mt-8" aria-labelledby="product-info-heading">
        <h2 id="product-info-heading" className="text-lg font-semibold text-gray-900 mb-4">
          Product Information
        </h2>
        <div className="rounded-lg bg-white shadow ring-1 ring-gray-900/5">
          <dl className="grid grid-cols-1 sm:grid-cols-2 divide-y sm:divide-y-0 sm:divide-x divide-gray-100">
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">Name</dt>
              <dd className="mt-1 text-sm text-gray-900">{product.name || '—'}</dd>
            </div>
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">SKU</dt>
              <dd className="mt-1 text-sm text-gray-900 font-mono">{product.sku || '—'}</dd>
            </div>
            <div className="px-4 py-4 sm:px-6 sm:col-span-2">
              <dt className="text-sm font-medium text-gray-500">Description</dt>
              <dd className="mt-1 text-sm text-gray-900 whitespace-pre-wrap">{product.description || '—'}</dd>
            </div>
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">Category</dt>
              <dd className="mt-1 text-sm text-gray-900">{product.category || '—'}</dd>
            </div>
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">Status</dt>
              <dd className="mt-1">
                <span
                  className={`inline-flex items-center rounded-md px-2 py-1 text-xs font-medium ring-1 ring-inset ${statusBadgeClass(product.status)}`}
                >
                  {product.status.charAt(0).toUpperCase() + product.status.slice(1)}
                </span>
              </dd>
            </div>
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">Price</dt>
              <dd className="mt-1 text-sm text-gray-900 font-semibold">
                {formatCurrency(product.price, product.currency)}
              </dd>
            </div>
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">Unit of Measure</dt>
              <dd className="mt-1 text-sm text-gray-900">{product.unit_of_measure || '—'}</dd>
            </div>
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">Reorder Level</dt>
              <dd className="mt-1 text-sm text-gray-900">{product.reorder_level}</dd>
            </div>
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">Barcode</dt>
              <dd className="mt-1 text-sm text-gray-900 font-mono">{product.barcode || '—'}</dd>
            </div>
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">Weight</dt>
              <dd className="mt-1 text-sm text-gray-900">
                {product.weight != null ? `${product.weight} kg` : '—'}
              </dd>
            </div>
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">Dimensions</dt>
              <dd className="mt-1 text-sm text-gray-900">{product.dimensions || '—'}</dd>
            </div>
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">Created On</dt>
              <dd className="mt-1 text-sm text-gray-900">{formatDateTime(product.created_on)}</dd>
            </div>
            <div className="px-4 py-4 sm:px-6">
              <dt className="text-sm font-medium text-gray-500">Updated On</dt>
              <dd className="mt-1 text-sm text-gray-900">{formatDateTime(product.updated_on)}</dd>
            </div>
          </dl>
        </div>
      </section>

      {/* ─── Stock Levels Section ────────────────────────────── */}
      <section className="mt-8" aria-labelledby="stock-levels-heading">
        <h2 id="stock-levels-heading" className="text-lg font-semibold text-gray-900 mb-4">
          Stock Levels
        </h2>

        {stockLoading ? (
          <div className="flex items-center gap-2 py-4">
            <div
              className="inline-block h-5 w-5 animate-spin rounded-full border-2 border-solid border-blue-600 border-e-transparent"
              role="status"
              aria-label="Loading stock levels"
            />
            <span className="text-sm text-gray-500">Loading stock levels…</span>
          </div>
        ) : stock ? (
          <>
            {/* Summary Cards */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3 mb-6">
              <div className="rounded-lg bg-white p-5 shadow ring-1 ring-gray-900/5">
                <p className="text-sm font-medium text-gray-500">Total Stock</p>
                <p className="mt-2 text-3xl font-bold text-gray-900">{stock.total_stock}</p>
              </div>
              <div className="rounded-lg bg-white p-5 shadow ring-1 ring-gray-900/5">
                <p className="text-sm font-medium text-gray-500">Available</p>
                <p className="mt-2 text-3xl font-bold text-green-600">{stock.available_stock}</p>
              </div>
              <div className="rounded-lg bg-white p-5 shadow ring-1 ring-gray-900/5">
                <p className="text-sm font-medium text-gray-500">Reserved</p>
                <p className="mt-2 text-3xl font-bold text-yellow-600">{stock.reserved_stock}</p>
              </div>
            </div>

            {/* Stock Level Indicator */}
            <div className="mb-6">
              <span
                className={`inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-sm font-medium ring-1 ring-inset ${stockIndicator.colorClass}`}
              >
                <span className={`inline-block h-2 w-2 rounded-full ${stockIndicator.dotClass}`} aria-hidden="true" />
                {stockIndicator.label}
              </span>
            </div>

            {/* Stock by Warehouse Table */}
            {stock.levels.length > 0 && (
              <div className="rounded-lg bg-white shadow ring-1 ring-gray-900/5">
                <DataTable<StockLevel>
                  data={stock.levels}
                  columns={stockColumns}
                  pageSize={0}
                  showFooter={false}
                  striped
                  hover
                  emptyText="No warehouse stock data available"
                />
              </div>
            )}
          </>
        ) : (
          <p className="text-sm text-gray-500">Stock information is unavailable.</p>
        )}
      </section>

      {/* ─── Pricing History Section (collapsible) ───────────── */}
      <section className="mt-8" aria-labelledby="pricing-history-heading">
        <button
          type="button"
          onClick={handleTogglePricing}
          className="flex w-full items-center justify-between text-start"
          aria-expanded={!pricingCollapsed}
          aria-controls="pricing-history-content"
        >
          <h2 id="pricing-history-heading" className="text-lg font-semibold text-gray-900">
            Pricing History
          </h2>
          <svg
            className={`h-5 w-5 text-gray-400 transition-transform duration-200 ${pricingCollapsed ? '' : 'rotate-180'}`}
            fill="none"
            viewBox="0 0 24 24"
            strokeWidth={2}
            stroke="currentColor"
            aria-hidden="true"
          >
            <path strokeLinecap="round" strokeLinejoin="round" d="m19.5 8.25-7.5 7.5-7.5-7.5" />
          </svg>
        </button>

        {!pricingCollapsed && (
          <div id="pricing-history-content" className="mt-4">
            {pricingLoading ? (
              <div className="flex items-center gap-2 py-4">
                <div
                  className="inline-block h-5 w-5 animate-spin rounded-full border-2 border-solid border-blue-600 border-e-transparent"
                  role="status"
                  aria-label="Loading pricing history"
                />
                <span className="text-sm text-gray-500">Loading pricing history…</span>
              </div>
            ) : pricingHistory.length > 0 ? (
              <>
                {/* Price line chart */}
                <div className="rounded-lg bg-white p-4 shadow ring-1 ring-gray-900/5 mb-4">
                  <Chart
                    datasets={pricingChartDatasets}
                    labels={pricingChartLabels}
                    type={ChartType.Line}
                    showLegend
                    height="300px"
                  />
                </div>

                {/* Pricing history table */}
                <div className="rounded-lg bg-white shadow ring-1 ring-gray-900/5">
                  <DataTable<PricingHistoryEntry>
                    data={pricingHistory}
                    columns={pricingHistoryColumns}
                    pageSize={10}
                    striped
                    hover
                    emptyText="No pricing history available"
                  />
                </div>
              </>
            ) : (
              <p className="text-sm text-gray-500 py-2">No pricing history recorded for this product.</p>
            )}
          </div>
        )}
      </section>

      {/* ─── Related Records Section (collapsible with tabs) ── */}
      <section className="mt-8 mb-12" aria-labelledby="related-records-heading">
        <button
          type="button"
          onClick={handleToggleRelated}
          className="flex w-full items-center justify-between text-start"
          aria-expanded={!relatedCollapsed}
          aria-controls="related-records-content"
        >
          <h2 id="related-records-heading" className="text-lg font-semibold text-gray-900">
            Related Records
          </h2>
          <svg
            className={`h-5 w-5 text-gray-400 transition-transform duration-200 ${relatedCollapsed ? '' : 'rotate-180'}`}
            fill="none"
            viewBox="0 0 24 24"
            strokeWidth={2}
            stroke="currentColor"
            aria-hidden="true"
          >
            <path strokeLinecap="round" strokeLinejoin="round" d="m19.5 8.25-7.5 7.5-7.5-7.5" />
          </svg>
        </button>

        {!relatedCollapsed && (
          <div id="related-records-content" className="mt-4">
            {relatedLoading ? (
              <div className="flex items-center gap-2 py-4">
                <div
                  className="inline-block h-5 w-5 animate-spin rounded-full border-2 border-solid border-blue-600 border-e-transparent"
                  role="status"
                  aria-label="Loading related records"
                />
                <span className="text-sm text-gray-500">Loading related records…</span>
              </div>
            ) : (
              <>
                {/* Tab Navigation */}
                <nav className="flex gap-4 border-b border-gray-200 mb-4" aria-label="Related record tabs">
                  <button
                    type="button"
                    onClick={() => handleTabChange('orders')}
                    className={`pb-2 text-sm font-medium focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                      activeRelatedTab === 'orders'
                        ? 'border-b-2 border-blue-600 text-blue-600'
                        : 'text-gray-500 hover:text-gray-700 hover:border-b-2 hover:border-gray-300'
                    }`}
                    aria-selected={activeRelatedTab === 'orders'}
                    role="tab"
                  >
                    Orders ({relatedRecords.orders.length})
                  </button>
                  <button
                    type="button"
                    onClick={() => handleTabChange('suppliers')}
                    className={`pb-2 text-sm font-medium focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                      activeRelatedTab === 'suppliers'
                        ? 'border-b-2 border-blue-600 text-blue-600'
                        : 'text-gray-500 hover:text-gray-700 hover:border-b-2 hover:border-gray-300'
                    }`}
                    aria-selected={activeRelatedTab === 'suppliers'}
                    role="tab"
                  >
                    Suppliers ({relatedRecords.suppliers.length})
                  </button>
                </nav>

                {/* Tab Panel: Orders */}
                {activeRelatedTab === 'orders' && (
                  <div role="tabpanel" aria-label="Orders">
                    <div className="rounded-lg bg-white shadow ring-1 ring-gray-900/5">
                      <DataTable<RelatedRecord>
                        data={relatedRecords.orders}
                        columns={orderColumns}
                        pageSize={10}
                        striped
                        hover
                        emptyText="No related orders found"
                      />
                    </div>
                  </div>
                )}

                {/* Tab Panel: Suppliers */}
                {activeRelatedTab === 'suppliers' && (
                  <div role="tabpanel" aria-label="Suppliers">
                    <div className="rounded-lg bg-white shadow ring-1 ring-gray-900/5">
                      <DataTable<RelatedRecord>
                        data={relatedRecords.suppliers}
                        columns={supplierColumns}
                        pageSize={10}
                        striped
                        hover
                        emptyText="No related suppliers found"
                      />
                    </div>
                  </div>
                )}
              </>
            )}
          </div>
        )}
      </section>

      {/* ─── Delete Confirmation Modal ────────────────────────── */}
      <Modal
        isVisible={deleteModalVisible}
        title="Delete Product"
        onClose={handleCloseDeleteModal}
        footer={
          <div className="flex justify-end gap-3">
            <button
              type="button"
              onClick={handleCloseDeleteModal}
              className="rounded-md bg-white px-3 py-2 text-sm font-semibold text-gray-900 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmDelete}
              disabled={deleteMutation.isPending}
              className="rounded-md bg-red-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {deleteMutation.isPending ? 'Deleting…' : 'Confirm Delete'}
            </button>
          </div>
        }
      >
        <div className="flex gap-3">
          <svg
            className="h-6 w-6 text-red-600 shrink-0 mt-0.5"
            fill="none"
            viewBox="0 0 24 24"
            strokeWidth={1.5}
            stroke="currentColor"
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126ZM12 15.75h.007v.008H12v-.008Z"
            />
          </svg>
          <div>
            <p className="text-sm text-gray-700">
              Are you sure you want to delete{' '}
              <span className="font-semibold">{product.name}</span>?
            </p>
            <p className="mt-2 text-sm text-gray-500">
              This action cannot be undone. All associated stock records, pricing history,
              and related records will also be removed.
            </p>
          </div>
        </div>
      </Modal>
    </div>
  );
}
