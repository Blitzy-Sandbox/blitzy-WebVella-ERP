/**
 * StockAdjustment.tsx — Stock Adjustment Form for Manual Stock Level Changes
 *
 * React 19 page component for recording stock adjustments (increment/decrement
 * with reason tracking). Replaces the monolith's RecordCreate.cshtml.cs pattern
 * applied to stock transaction/adjustment entities in the Inventory bounded
 * context (DynamoDB-backed).
 *
 * Lifecycle mirrors the monolith's OnGet → Init → render form and
 * OnPost → validate → createRecord → redirect flow. The transactional
 * "create a record that modifies an aggregate" pattern is analogous to
 * TimeLogService.Create() from the Project plugin.
 *
 * Route: /inventory/stock/adjust
 * Pre-population: ?productId=<uuid>
 */

import React, { useState, useCallback, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';

import { get, post } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation } from '../../components/forms/DynamicForm';

/* ═══════════════════════════════════════════════════════════════════
 * Type Definitions
 * ═══════════════════════════════════════════════════════════════════ */

/** The two possible directions for a stock adjustment. */
type AdjustmentType = 'increment' | 'decrement';

/** Reason codes for stock adjustments — audit trail for every change. */
type AdjustmentReason =
  | 'purchase_receipt'
  | 'return_from_customer'
  | 'production_assembly'
  | 'inventory_count_correction'
  | 'sale_shipment'
  | 'return_to_supplier'
  | 'damage_loss'
  | 'expiration_disposal'
  | 'transfer_out'
  | 'transfer_in'
  | 'other';

/** Shape of the form state managed locally by the component. */
interface StockAdjustmentForm {
  product_id: string;
  adjustment_type: AdjustmentType;
  quantity: number;
  reason: AdjustmentReason | '';
  notes: string;
  reference_number: string;
  warehouse_id: string;
}

/** Server response after a successful stock adjustment creation. */
interface StockAdjustmentResponse {
  id: string;
  product_id: string;
  adjustment_type: AdjustmentType;
  quantity: number;
  reason: AdjustmentReason;
  previous_stock: number;
  new_stock: number;
  created_by: string;
  created_on: string;
}

/** Product search result item used in the product selector dropdown. */
interface ProductOption {
  id: string;
  name: string;
  sku: string;
}

/** Stock information returned for a selected product. */
interface ProductStockInfo {
  current_stock: number;
  reorder_level: number;
  product_name: string;
  sku: string;
}

/** Per-field validation error map. Key "_general" holds form-level errors. */
interface ValidationErrors {
  [key: string]: string;
}

/* ═══════════════════════════════════════════════════════════════════
 * Constants
 * ═══════════════════════════════════════════════════════════════════ */

/** Metadata for every supported adjustment reason. */
interface ReasonOption {
  value: AdjustmentReason;
  label: string;
  types: AdjustmentType[];
}

const ADJUSTMENT_REASONS: ReasonOption[] = [
  { value: 'purchase_receipt', label: 'Purchase/Receipt', types: ['increment'] },
  { value: 'return_from_customer', label: 'Return from Customer', types: ['increment'] },
  { value: 'production_assembly', label: 'Production/Assembly', types: ['increment'] },
  { value: 'transfer_in', label: 'Transfer In', types: ['increment'] },
  {
    value: 'inventory_count_correction',
    label: 'Inventory Count Correction',
    types: ['increment', 'decrement'],
  },
  { value: 'other', label: 'Other', types: ['increment', 'decrement'] },
  { value: 'sale_shipment', label: 'Sale/Shipment', types: ['decrement'] },
  { value: 'return_to_supplier', label: 'Return to Supplier', types: ['decrement'] },
  { value: 'damage_loss', label: 'Damage/Loss', types: ['decrement'] },
  { value: 'expiration_disposal', label: 'Expiration/Disposal', types: ['decrement'] },
  { value: 'transfer_out', label: 'Transfer Out', types: ['decrement'] },
];

/** Pristine form state used for initialisation and reset. */
const INITIAL_FORM: StockAdjustmentForm = {
  product_id: '',
  adjustment_type: 'increment',
  quantity: 0,
  reason: '',
  notes: '',
  reference_number: '',
  warehouse_id: '',
};

/* ═══════════════════════════════════════════════════════════════════
 * Client-Side Validation
 *
 * Mirrors the monolith's ValidateRecordSubmission + pre-create hook
 * error collection pattern from RecordCreate.cshtml.cs lines 83-99.
 * ═══════════════════════════════════════════════════════════════════ */

function validateStockAdjustment(
  data: StockAdjustmentForm,
  currentStock: number,
): ValidationErrors {
  const errors: ValidationErrors = {};

  if (!data.product_id) {
    errors.product_id = 'Product is required';
  }
  if (!data.adjustment_type) {
    errors.adjustment_type = 'Adjustment type is required';
  }
  if (!data.quantity || data.quantity <= 0) {
    errors.quantity = 'Quantity must be greater than 0';
  } else if (!Number.isInteger(data.quantity)) {
    errors.quantity = 'Quantity must be a whole number';
  }
  if (!data.reason) {
    errors.reason = 'Reason is required';
  }

  /* Prevent decrement that would yield negative stock — analogous to
     the monolith's record-manager validation errors surfaced via
     response.Errors in RecordCreate.cshtml.cs line 106. */
  if (
    data.adjustment_type === 'decrement' &&
    data.quantity > 0 &&
    data.quantity > currentStock
  ) {
    errors.quantity =
      `Cannot remove ${data.quantity} units. Current stock is ${currentStock}.`;
  }

  return errors;
}

/* ═══════════════════════════════════════════════════════════════════
 * StockAdjustment Component
 * ═══════════════════════════════════════════════════════════════════ */

function StockAdjustment(): React.ReactElement {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const queryClient = useQueryClient();

  /* Read pre-population URL parameter — mirrors the monolith's
     RecordCreate OnGet() query-string pass-through. */
  const initialProductId = searchParams.get('productId') ?? '';

  /* ── Form state ─────────────────────────────────────────────── */
  const [form, setForm] = useState<StockAdjustmentForm>(() => ({
    ...INITIAL_FORM,
    product_id: initialProductId,
  }));
  const [errors, setErrors] = useState<ValidationErrors>({});
  const [productSearch, setProductSearch] = useState('');
  const [isProductDropdownOpen, setIsProductDropdownOpen] = useState(false);
  const [isDirty, setIsDirty] = useState(false);
  const [submitSuccess, setSubmitSuccess] = useState(false);

  /* ── Product search query (typeahead) ───────────────────────── */
  const productListQuery = useQuery<ApiResponse<ProductOption[]>, ApiError>({
    queryKey: ['inventory', 'products', 'search', productSearch],
    queryFn: () =>
      get<ProductOption[]>('/inventory/products', {
        search: productSearch,
        fields: 'id,name,sku',
        pageSize: 20,
      }),
    enabled: productSearch.length >= 1,
    staleTime: 30_000,
  });

  const productOptions: ProductOption[] =
    productListQuery.data?.success === true && productListQuery.data?.object
      ? productListQuery.data.object
      : [];

  /* ── Stock level query for the selected product ─────────────── */
  const stockQuery = useQuery<ApiResponse<ProductStockInfo>, ApiError>({
    queryKey: ['inventory', 'products', form.product_id, 'stock'],
    queryFn: () =>
      get<ProductStockInfo>(
        `/inventory/products/${encodeURIComponent(form.product_id)}/stock`,
      ),
    enabled: form.product_id.length > 0,
  });

  const currentStock: number =
    stockQuery.data?.success === true && stockQuery.data?.object != null
      ? stockQuery.data.object.current_stock
      : 0;

  const reorderLevel: number =
    stockQuery.data?.success === true && stockQuery.data?.object != null
      ? stockQuery.data.object.reorder_level
      : 0;

  const selectedProductName: string =
    stockQuery.data?.success === true && stockQuery.data?.object != null
      ? stockQuery.data.object.product_name
      : '';

  const selectedProductSku: string =
    stockQuery.data?.success === true && stockQuery.data?.object != null
      ? stockQuery.data.object.sku
      : '';

  /* ── Computed values ────────────────────────────────────────── */
  const adjustedQuantity = form.quantity || 0;
  const newStock =
    form.adjustment_type === 'increment'
      ? currentStock + adjustedQuantity
      : currentStock - adjustedQuantity;

  const isNegativeStock = newStock < 0;
  const isLowStock = newStock > 0 && newStock <= reorderLevel;

  const filteredReasons = ADJUSTMENT_REASONS.filter((r) =>
    r.types.includes(form.adjustment_type),
  );

  /* ── Auto-populate product search text on URL pre-population ── */
  useEffect(() => {
    if (
      initialProductId &&
      stockQuery.data?.success === true &&
      stockQuery.data?.object != null
    ) {
      setProductSearch(stockQuery.data.object.product_name);
    }
  }, [initialProductId, stockQuery.data]);

  /* ── Unsaved changes — browser navigation guard ─────────────── */
  useEffect(() => {
    if (!isDirty) return undefined;

    const handleBeforeUnload = (e: BeforeUnloadEvent): void => {
      e.preventDefault();
    };

    window.addEventListener('beforeunload', handleBeforeUnload);
    return () => {
      window.removeEventListener('beforeunload', handleBeforeUnload);
    };
  }, [isDirty]);

  /* ── Field change handler ───────────────────────────────────── */
  const handleFieldChange = useCallback(
    (field: keyof StockAdjustmentForm, value: string | number): void => {
      setIsDirty(true);
      setForm((prev) => {
        const updated = { ...prev, [field]: value };
        /* When adjustment type changes, reset reason if it is no longer
           valid for the new type. */
        if (field === 'adjustment_type') {
          const newType = value as AdjustmentType;
          const isReasonValid = ADJUSTMENT_REASONS.some(
            (r) => r.value === prev.reason && r.types.includes(newType),
          );
          if (!isReasonValid) {
            updated.reason = '';
          }
        }
        return updated;
      });
      /* Clear the error for the field that was just changed. */
      setErrors((prev) => {
        if (prev[field]) {
          const next = { ...prev };
          delete next[field];
          return next;
        }
        return prev;
      });
    },
    [],
  );

  /* ── Product selection from dropdown ────────────────────────── */
  const handleProductSelect = useCallback(
    (product: ProductOption): void => {
      handleFieldChange('product_id', product.id);
      setProductSearch(product.name);
      setIsProductDropdownOpen(false);
    },
    [handleFieldChange],
  );

  /* ── Close dropdown when clicking outside ───────────────────── */
  useEffect(() => {
    if (!isProductDropdownOpen) return undefined;

    const handleClickOutside = (): void => {
      setIsProductDropdownOpen(false);
    };

    /* Delay listener so the current click event doesn't trigger it. */
    const timerId = window.setTimeout(() => {
      document.addEventListener('click', handleClickOutside);
    }, 0);

    return () => {
      window.clearTimeout(timerId);
      document.removeEventListener('click', handleClickOutside);
    };
  }, [isProductDropdownOpen]);

  /* ── Create mutation ────────────────────────────────────────── */
  const createMutation = useMutation<
    ApiResponse<StockAdjustmentResponse>,
    ApiError,
    StockAdjustmentForm
  >({
    mutationFn: (data: StockAdjustmentForm) =>
      post<StockAdjustmentResponse>('/inventory/stock/adjustments', data),

    onSuccess: (response: ApiResponse<StockAdjustmentResponse>) => {
      if (response.success) {
        setIsDirty(false);
        setSubmitSuccess(true);

        /* Invalidate related queries so other views reflect the change. */
        queryClient.invalidateQueries({ queryKey: ['inventory', 'stock'] });
        queryClient.invalidateQueries({
          queryKey: ['inventory', 'products', form.product_id, 'stock'],
        });

        /* Navigate to the originating context — mirrors RecordCreate.cshtml.cs
           lines 121-124 redirect logic. */
        const targetUrl = initialProductId
          ? `/inventory/products/${encodeURIComponent(form.product_id)}`
          : '/inventory/stock';
        navigate(targetUrl);
      } else {
        /* Server returned success=false with field-level errors in the
           ApiResponse envelope (mirrors monolith's response.Errors). */
        const serverErrors: ValidationErrors = {};
        if (response.errors && response.errors.length > 0) {
          for (const err of response.errors) {
            serverErrors[err.key || '_general'] = err.message;
          }
        }
        if (Object.keys(serverErrors).length === 0) {
          serverErrors._general =
            response.object
              ? 'Adjustment created with warnings.'
              : 'The server rejected the adjustment.';
        }
        setErrors(serverErrors);
      }
    },

    onError: (error: ApiError) => {
      /* Handle HTTP-level errors (e.g. 422 Insufficient stock). Uses
         error.message, error.errors, and error.status from the ApiError
         interface produced by the client interceptor. */
      const serverErrors: ValidationErrors = {};
      if (error.status === 422 && error.errors && error.errors.length > 0) {
        for (const err of error.errors) {
          serverErrors[err.key || '_general'] = err.message;
        }
      } else if (error.errors && error.errors.length > 0) {
        for (const err of error.errors) {
          serverErrors[err.key || '_general'] = err.message;
        }
      } else {
        serverErrors._general =
          error.message || 'An unexpected error occurred';
      }
      setErrors(serverErrors);
    },
  });

  /* ── Form validate-and-submit ───────────────────────────────── */
  const handleSubmit = useCallback((): void => {
    const validationErrors = validateStockAdjustment(form, currentStock);
    if (Object.keys(validationErrors).length > 0) {
      setErrors(validationErrors);
      return;
    }
    setErrors({});
    createMutation.mutate(form);
  }, [form, currentStock, createMutation]);

  /* ── Cancel with unsaved-changes confirmation ───────────────── */
  const handleCancel = useCallback((): void => {
    if (isDirty) {
      const confirmed = window.confirm(
        'You have unsaved changes. Are you sure you want to leave?',
      );
      if (!confirmed) return;
    }
    const fallbackUrl = initialProductId
      ? `/inventory/products/${encodeURIComponent(initialProductId)}`
      : '/inventory/stock';
    navigate(fallbackUrl);
  }, [isDirty, navigate, initialProductId]);

  /* ── Build FormValidation for DynamicForm ───────────────────── */
  const formValidation: FormValidation | undefined =
    Object.keys(errors).length > 0
      ? {
          message: errors._general || 'Please correct the errors below.',
          errors: Object.entries(errors)
            .filter(([key]) => key !== '_general')
            .map(([key, message]) => ({ propertyName: key, message })),
        }
      : undefined;

  /* ═════════════════════════════════════════════════════════════
   * Render
   * ═════════════════════════════════════════════════════════════ */
  return (
    <div className="mx-auto max-w-4xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ── Page header ───────────────────────────────────────── */}
      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div
            className="flex h-10 w-10 items-center justify-center rounded-lg bg-indigo-100"
            aria-hidden="true"
          >
            <svg
              className="h-6 w-6 text-indigo-600"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M7.5 21L3 16.5m0 0L7.5 12M3 16.5h13.5m0-13.5L21 7.5m0 0L16.5 12M21 7.5H7.5"
              />
            </svg>
          </div>
          <div>
            <h1 className="text-2xl font-bold text-gray-900">
              Stock Adjustment
            </h1>
            <p className="text-sm text-gray-500">
              Record a manual stock level change
            </p>
          </div>
        </div>

        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={handleCancel}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors duration-200 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={handleSubmit}
            disabled={createMutation.isPending}
            className="inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors duration-200 hover:bg-indigo-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {createMutation.isPending ? (
              <>
                <svg
                  className="me-2 h-4 w-4 animate-spin"
                  viewBox="0 0 24 24"
                  fill="none"
                  aria-hidden="true"
                >
                  <circle
                    className="opacity-25"
                    cx="12"
                    cy="12"
                    r="10"
                    stroke="currentColor"
                    strokeWidth="4"
                  />
                  <path
                    className="opacity-75"
                    fill="currentColor"
                    d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
                  />
                </svg>
                Submitting…
              </>
            ) : (
              'Submit Adjustment'
            )}
          </button>
        </div>
      </div>

      {/* ── Success banner ────────────────────────────────────── */}
      {submitSuccess && (
        <div
          className="mb-6 rounded-lg border border-green-200 bg-green-50 p-4"
          role="status"
        >
          <p className="text-sm font-medium text-green-800">
            Stock adjustment recorded successfully. Redirecting…
          </p>
        </div>
      )}

      {/* ── DynamicForm wrapper ───────────────────────────────── */}
      <DynamicForm
        name="stock-adjustment"
        labelMode="stacked"
        fieldMode="form"
        validation={formValidation}
        onSubmit={(e) => {
          e.preventDefault();
          handleSubmit();
        }}
        className="space-y-6"
      >
        {/* ── Current stock info panel ────────────────────────── */}
        {form.product_id &&
          stockQuery.data?.success === true &&
          stockQuery.data?.object != null && (
            <div className="rounded-lg border border-blue-200 bg-blue-50 p-4">
              <div className="flex flex-wrap items-center gap-x-6 gap-y-2">
                <div>
                  <p className="text-xs font-medium uppercase tracking-wide text-blue-600">
                    Product
                  </p>
                  <p className="text-sm font-semibold text-blue-900">
                    {selectedProductName}{' '}
                    <span className="font-normal text-blue-700">
                      ({selectedProductSku})
                    </span>
                  </p>
                </div>
                <div>
                  <p className="text-xs font-medium uppercase tracking-wide text-blue-600">
                    Current Stock
                  </p>
                  <p className="text-2xl font-bold text-blue-900">
                    {currentStock}
                  </p>
                </div>
                <div>
                  <p className="text-xs font-medium uppercase tracking-wide text-blue-600">
                    Reorder Level
                  </p>
                  <p className="text-sm font-semibold text-blue-900">
                    {reorderLevel}
                  </p>
                </div>
              </div>
            </div>
          )}

        {/* Loading indicator while stock info is fetching */}
        {form.product_id && stockQuery.isLoading && (
          <div className="rounded-lg border border-gray-200 bg-gray-50 p-4">
            <p className="text-sm text-gray-500">
              Loading product stock information…
            </p>
          </div>
        )}

        {/* ── Section 1: Product Selection ────────────────────── */}
        <fieldset>
          <legend className="mb-3 text-base font-semibold text-gray-900">
            Product Selection
          </legend>
          <div className="relative">
            <label
              htmlFor="product-search"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Product{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <input
              id="product-search"
              type="text"
              value={productSearch}
              onChange={(e) => {
                setProductSearch(e.target.value);
                setIsProductDropdownOpen(true);
                if (!e.target.value) {
                  handleFieldChange('product_id', '');
                }
              }}
              onFocus={() => {
                if (productSearch.length >= 1) {
                  setIsProductDropdownOpen(true);
                }
              }}
              placeholder="Search by product name or SKU…"
              autoComplete="off"
              aria-expanded={isProductDropdownOpen}
              aria-controls="product-search-listbox"
              aria-autocomplete="list"
              role="combobox"
              className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 ${
                errors.product_id
                  ? 'border-red-300 text-red-900 placeholder:text-red-300'
                  : 'border-gray-300 text-gray-900 placeholder:text-gray-400'
              }`}
            />
            {errors.product_id && (
              <p className="mt-1 text-sm text-red-600" role="alert">
                {errors.product_id}
              </p>
            )}

            {/* Dropdown results */}
            {isProductDropdownOpen && productSearch.length >= 1 && (
              <ul
                id="product-search-listbox"
                role="listbox"
                className="absolute z-10 mt-1 max-h-60 w-full overflow-auto rounded-md border border-gray-200 bg-white py-1 shadow-lg"
              >
                {productListQuery.isLoading && (
                  <li className="px-3 py-2 text-sm text-gray-500">
                    Searching…
                  </li>
                )}
                {productListQuery.isError && (
                  <li className="px-3 py-2 text-sm text-red-600">
                    Error loading products
                  </li>
                )}
                {productOptions.length === 0 &&
                  !productListQuery.isLoading &&
                  !productListQuery.isError && (
                    <li className="px-3 py-2 text-sm text-gray-500">
                      No products found
                    </li>
                  )}
                {productOptions.map((product) => (
                  <li
                    key={product.id}
                    role="option"
                    aria-selected={form.product_id === product.id}
                    tabIndex={0}
                    className={`cursor-pointer px-3 py-2 text-sm transition-colors hover:bg-indigo-50 ${
                      form.product_id === product.id
                        ? 'bg-indigo-50 font-medium text-indigo-700'
                        : 'text-gray-900'
                    }`}
                    onClick={() => handleProductSelect(product)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        handleProductSelect(product);
                      }
                    }}
                  >
                    <span className="font-medium">{product.name}</span>
                    <span className="ms-2 text-gray-500">
                      ({product.sku})
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </fieldset>

        {/* ── Section 2: Adjustment Details ───────────────────── */}
        <fieldset>
          <legend className="mb-3 text-base font-semibold text-gray-900">
            Adjustment Details
          </legend>
          <div className="space-y-4">
            {/* Adjustment type toggle */}
            <div>
              <span className="mb-2 block text-sm font-medium text-gray-700">
                Adjustment Type{' '}
                <span className="text-red-500" aria-hidden="true">
                  *
                </span>
              </span>
              <div
                className="inline-flex rounded-lg border border-gray-300 p-1"
                role="radiogroup"
                aria-label="Adjustment type"
              >
                <button
                  type="button"
                  role="radio"
                  aria-checked={form.adjustment_type === 'increment'}
                  onClick={() =>
                    handleFieldChange('adjustment_type', 'increment')
                  }
                  className={`rounded-md px-4 py-2 text-sm font-medium transition-colors ${
                    form.adjustment_type === 'increment'
                      ? 'bg-green-600 text-white shadow-sm'
                      : 'bg-transparent text-gray-700 hover:bg-gray-100'
                  }`}
                >
                  + Increment
                </button>
                <button
                  type="button"
                  role="radio"
                  aria-checked={form.adjustment_type === 'decrement'}
                  onClick={() =>
                    handleFieldChange('adjustment_type', 'decrement')
                  }
                  className={`rounded-md px-4 py-2 text-sm font-medium transition-colors ${
                    form.adjustment_type === 'decrement'
                      ? 'bg-red-600 text-white shadow-sm'
                      : 'bg-transparent text-gray-700 hover:bg-gray-100'
                  }`}
                >
                  − Decrement
                </button>
              </div>
              {errors.adjustment_type && (
                <p className="mt-1 text-sm text-red-600" role="alert">
                  {errors.adjustment_type}
                </p>
              )}
            </div>

            {/* Quantity + Warehouse row */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <div>
                <label
                  htmlFor="quantity"
                  className="mb-1 block text-sm font-medium text-gray-700"
                >
                  Quantity{' '}
                  <span className="text-red-500" aria-hidden="true">
                    *
                  </span>
                </label>
                <input
                  id="quantity"
                  type="number"
                  min={1}
                  step={1}
                  value={form.quantity || ''}
                  onChange={(e) =>
                    handleFieldChange(
                      'quantity',
                      parseInt(e.target.value, 10) || 0,
                    )
                  }
                  placeholder="Enter quantity"
                  className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 ${
                    errors.quantity
                      ? 'border-red-300 text-red-900'
                      : 'border-gray-300 text-gray-900'
                  }`}
                />
                {form.quantity > 0 && !errors.quantity && (
                  <p
                    className={`mt-1 text-xs ${
                      form.adjustment_type === 'increment'
                        ? 'text-green-600'
                        : 'text-red-600'
                    }`}
                  >
                    {form.adjustment_type === 'increment'
                      ? `Adding ${form.quantity} unit${form.quantity !== 1 ? 's' : ''}`
                      : `Removing ${form.quantity} unit${form.quantity !== 1 ? 's' : ''}`}
                  </p>
                )}
                {errors.quantity && (
                  <p className="mt-1 text-sm text-red-600" role="alert">
                    {errors.quantity}
                  </p>
                )}
              </div>

              <div>
                <label
                  htmlFor="warehouse"
                  className="mb-1 block text-sm font-medium text-gray-700"
                >
                  Warehouse / Location
                </label>
                <input
                  id="warehouse"
                  type="text"
                  value={form.warehouse_id}
                  onChange={(e) =>
                    handleFieldChange('warehouse_id', e.target.value)
                  }
                  placeholder="Optional warehouse ID"
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm transition-colors placeholder:text-gray-400 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
                />
              </div>
            </div>

            {/* Reason dropdown */}
            <div>
              <label
                htmlFor="reason"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Reason{' '}
                <span className="text-red-500" aria-hidden="true">
                  *
                </span>
              </label>
              <select
                id="reason"
                value={form.reason}
                onChange={(e) => handleFieldChange('reason', e.target.value)}
                className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 ${
                  errors.reason
                    ? 'border-red-300 text-red-900'
                    : 'border-gray-300 text-gray-900'
                }`}
              >
                <option value="">Select a reason…</option>
                {filteredReasons.map((reason) => (
                  <option key={reason.value} value={reason.value}>
                    {reason.label}
                  </option>
                ))}
              </select>
              {errors.reason && (
                <p className="mt-1 text-sm text-red-600" role="alert">
                  {errors.reason}
                </p>
              )}
            </div>
          </div>
        </fieldset>

        {/* ── Section 3: Additional Information ───────────────── */}
        <fieldset>
          <legend className="mb-3 text-base font-semibold text-gray-900">
            Additional Information
          </legend>
          <div className="space-y-4">
            <div>
              <label
                htmlFor="reference-number"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Reference Number
              </label>
              <input
                id="reference-number"
                type="text"
                value={form.reference_number}
                onChange={(e) =>
                  handleFieldChange('reference_number', e.target.value)
                }
                placeholder="PO number, order number, etc."
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm transition-colors placeholder:text-gray-400 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
              />
            </div>
            <div>
              <label
                htmlFor="notes"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Notes
              </label>
              <textarea
                id="notes"
                rows={3}
                value={form.notes}
                onChange={(e) => handleFieldChange('notes', e.target.value)}
                placeholder="Additional details about this adjustment…"
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm transition-colors placeholder:text-gray-400 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
              />
            </div>
          </div>
        </fieldset>

        {/* ── Section 4: Adjustment Preview ───────────────────── */}
        {form.product_id &&
          adjustedQuantity > 0 &&
          stockQuery.data?.success === true && (
            <fieldset>
              <legend className="mb-3 text-base font-semibold text-gray-900">
                Adjustment Preview
              </legend>
              <div className="rounded-lg border border-gray-200 bg-gray-50 p-4">
                <div className="flex items-center justify-center gap-4">
                  {/* Current stock */}
                  <div className="text-center">
                    <p className="text-xs font-medium uppercase tracking-wide text-gray-500">
                      Current Stock
                    </p>
                    <p className="text-3xl font-bold text-gray-900">
                      {currentStock}
                    </p>
                  </div>

                  {/* Arrow */}
                  <div className="flex h-8 w-8 items-center justify-center">
                    <svg
                      className={`h-6 w-6 ${
                        form.adjustment_type === 'increment'
                          ? 'text-green-500'
                          : 'text-red-500'
                      }`}
                      fill="none"
                      viewBox="0 0 24 24"
                      strokeWidth={2}
                      stroke="currentColor"
                      aria-hidden="true"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        d="M13.5 4.5L21 12m0 0l-7.5 7.5M21 12H3"
                      />
                    </svg>
                  </div>

                  {/* Delta */}
                  <div className="text-center">
                    <p className="text-xs font-medium uppercase tracking-wide text-gray-500">
                      {form.adjustment_type === 'increment' ? '+' : '−'}{' '}
                      {adjustedQuantity}
                    </p>
                    <p className="text-xl font-semibold text-gray-600">
                      {form.adjustment_type === 'increment'
                        ? 'Adding'
                        : 'Removing'}
                    </p>
                  </div>

                  {/* Arrow */}
                  <div className="flex h-8 w-8 items-center justify-center">
                    <svg
                      className="h-6 w-6 text-gray-400"
                      fill="none"
                      viewBox="0 0 24 24"
                      strokeWidth={2}
                      stroke="currentColor"
                      aria-hidden="true"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        d="M13.5 4.5L21 12m0 0l-7.5 7.5M21 12H3"
                      />
                    </svg>
                  </div>

                  {/* New stock */}
                  <div className="text-center">
                    <p className="text-xs font-medium uppercase tracking-wide text-gray-500">
                      New Stock
                    </p>
                    <p
                      className={`text-3xl font-bold ${
                        isNegativeStock
                          ? 'text-red-600'
                          : isLowStock
                            ? 'text-amber-600'
                            : 'text-green-600'
                      }`}
                    >
                      {newStock}
                    </p>
                  </div>
                </div>

                {/* Negative stock warning */}
                {isNegativeStock && (
                  <div
                    className="mt-4 flex items-center gap-2 rounded-md border border-red-200 bg-red-50 p-3"
                    role="alert"
                  >
                    <svg
                      className="h-5 w-5 flex-shrink-0 text-red-500"
                      fill="currentColor"
                      viewBox="0 0 20 20"
                      aria-hidden="true"
                    >
                      <path
                        fillRule="evenodd"
                        d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
                        clipRule="evenodd"
                      />
                    </svg>
                    <p className="text-sm font-medium text-red-800">
                      Warning: This adjustment would result in negative stock (
                      {newStock} units).
                    </p>
                  </div>
                )}

                {/* Low stock threshold warning */}
                {isLowStock && !isNegativeStock && (
                  <div
                    className="mt-4 flex items-center gap-2 rounded-md border border-amber-200 bg-amber-50 p-3"
                    role="alert"
                  >
                    <svg
                      className="h-5 w-5 flex-shrink-0 text-amber-500"
                      fill="currentColor"
                      viewBox="0 0 20 20"
                      aria-hidden="true"
                    >
                      <path
                        fillRule="evenodd"
                        d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 6a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 6zm0 9a1 1 0 100-2 1 1 0 000 2z"
                        clipRule="evenodd"
                      />
                    </svg>
                    <p className="text-sm font-medium text-amber-800">
                      Warning: New stock level ({newStock}) is at or below
                      reorder level ({reorderLevel}).
                    </p>
                  </div>
                )}
              </div>
            </fieldset>
          )}
      </DynamicForm>
    </div>
  );
}

export default StockAdjustment;
