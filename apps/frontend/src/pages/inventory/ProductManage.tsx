import React, { useState, useEffect, useCallback, type FormEvent } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams, useNavigate } from 'react-router-dom';
import { get, put, type ApiResponse, type ApiError } from '../../api/client';
import DynamicForm, { type FormValidation, type ValidationError } from '../../components/forms/DynamicForm';

// ---------------------------------------------------------------------------
// Type definitions
// ---------------------------------------------------------------------------

/** Product data returned from the Inventory service API. */
interface Product {
  id: string;
  name: string;
  sku: string;
  description: string;
  price: number;
  category: string;
  status: string;
  stock_quantity: number;
  unit_of_measure: string;
  reorder_level: number;
  weight: number;
  dimensions: string;
  barcode: string;
  created_on: string;
  updated_on: string;
}

/**
 * Controlled-input form state.
 * All values are stored as strings so that empty / partial numeric inputs
 * are handled naturally by the browser's native &lt;input type="number"&gt;.
 */
interface ProductFormState {
  name: string;
  sku: string;
  description: string;
  price: string;
  category: string;
  status: string;
  unit_of_measure: string;
  reorder_level: string;
  weight: string;
  dimensions: string;
  barcode: string;
}

/**
 * Payload sent to PUT /v1/inventory/products/:id.
 * Note: stock_quantity is intentionally excluded — stock changes MUST go
 * through the StockAdjustment workflow (single-entity-ownership rule).
 */
interface ProductUpdatePayload {
  name: string;
  sku: string;
  description: string;
  price: number;
  category: string;
  status: string;
  unit_of_measure: string;
  reorder_level: number;
  weight: number;
  dimensions: string;
  barcode: string;
}

// ---------------------------------------------------------------------------
// Constants – select-option arrays
// ---------------------------------------------------------------------------

const CATEGORY_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: '', label: 'Select a category…' },
  { value: 'electronics', label: 'Electronics' },
  { value: 'clothing', label: 'Clothing' },
  { value: 'food_beverage', label: 'Food & Beverage' },
  { value: 'furniture', label: 'Furniture' },
  { value: 'office_supplies', label: 'Office Supplies' },
  { value: 'raw_materials', label: 'Raw Materials' },
  { value: 'tools', label: 'Tools' },
  { value: 'other', label: 'Other' },
];

const STATUS_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: '', label: 'Select a status…' },
  { value: 'active', label: 'Active' },
  { value: 'inactive', label: 'Inactive' },
  { value: 'discontinued', label: 'Discontinued' },
];

const UNIT_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: '', label: 'Select unit…' },
  { value: 'each', label: 'Each' },
  { value: 'kg', label: 'Kilogram (kg)' },
  { value: 'g', label: 'Gram (g)' },
  { value: 'lb', label: 'Pound (lb)' },
  { value: 'oz', label: 'Ounce (oz)' },
  { value: 'liter', label: 'Liter (L)' },
  { value: 'ml', label: 'Milliliter (mL)' },
  { value: 'meter', label: 'Meter (m)' },
  { value: 'cm', label: 'Centimeter (cm)' },
  { value: 'box', label: 'Box' },
  { value: 'pack', label: 'Pack' },
  { value: 'set', label: 'Set' },
];

/** Initial (empty) form state used before data loads. */
const INITIAL_FORM_STATE: ProductFormState = {
  name: '',
  sku: '',
  description: '',
  price: '',
  category: '',
  status: '',
  unit_of_measure: '',
  reorder_level: '',
  weight: '',
  dimensions: '',
  barcode: '',
};

// ---------------------------------------------------------------------------
// Pure helpers
// ---------------------------------------------------------------------------

/**
 * Client-side validation mirroring RecordManage.cshtml.cs field rules.
 * stock_quantity is excluded because it is read-only in edit mode.
 */
function validateProductForm(values: ProductFormState): ValidationError[] {
  const errors: ValidationError[] = [];

  /* Name – required, max 200 */
  if (!values.name.trim()) {
    errors.push({ propertyName: 'name', message: 'Product name is required.' });
  } else if (values.name.trim().length > 200) {
    errors.push({ propertyName: 'name', message: 'Product name must not exceed 200 characters.' });
  }

  /* SKU – required, max 50, alphanumeric-dash */
  if (!values.sku.trim()) {
    errors.push({ propertyName: 'sku', message: 'SKU is required.' });
  } else if (values.sku.trim().length > 50) {
    errors.push({ propertyName: 'sku', message: 'SKU must not exceed 50 characters.' });
  } else if (!/^[A-Za-z0-9-]+$/.test(values.sku.trim())) {
    errors.push({ propertyName: 'sku', message: 'SKU must contain only letters, numbers, and dashes.' });
  }

  /* Price – required, >= 0 */
  if (values.price.trim() === '') {
    errors.push({ propertyName: 'price', message: 'Price is required.' });
  } else {
    const priceNum = parseFloat(values.price);
    if (Number.isNaN(priceNum) || priceNum < 0) {
      errors.push({ propertyName: 'price', message: 'Price must be a non-negative number.' });
    }
  }

  /* Category – required */
  if (!values.category) {
    errors.push({ propertyName: 'category', message: 'Category is required.' });
  }

  /* Status – required */
  if (!values.status) {
    errors.push({ propertyName: 'status', message: 'Status is required.' });
  }

  /* Optional numeric: reorder_level */
  if (values.reorder_level.trim() !== '') {
    const n = parseFloat(values.reorder_level);
    if (Number.isNaN(n) || n < 0) {
      errors.push({ propertyName: 'reorder_level', message: 'Reorder level must be a non-negative number.' });
    }
  }

  /* Optional numeric: weight */
  if (values.weight.trim() !== '') {
    const n = parseFloat(values.weight);
    if (Number.isNaN(n) || n < 0) {
      errors.push({ propertyName: 'weight', message: 'Weight must be a non-negative number.' });
    }
  }

  return errors;
}

/** Map a Product API response into controlled-input string state. */
function productToFormState(product: Product): ProductFormState {
  return {
    name: product.name ?? '',
    sku: product.sku ?? '',
    description: product.description ?? '',
    price: product.price != null ? String(product.price) : '',
    category: product.category ?? '',
    status: product.status ?? '',
    unit_of_measure: product.unit_of_measure ?? '',
    reorder_level: product.reorder_level != null ? String(product.reorder_level) : '',
    weight: product.weight != null ? String(product.weight) : '',
    dimensions: product.dimensions ?? '',
    barcode: product.barcode ?? '',
  };
}

/** Convert controlled-input state to the typed update payload. */
function formStateToPayload(values: ProductFormState): ProductUpdatePayload {
  return {
    name: values.name.trim(),
    sku: values.sku.trim(),
    description: values.description.trim(),
    price: parseFloat(values.price) || 0,
    category: values.category,
    status: values.status,
    unit_of_measure: values.unit_of_measure,
    reorder_level: values.reorder_level.trim() !== '' ? parseFloat(values.reorder_level) : 0,
    weight: values.weight.trim() !== '' ? parseFloat(values.weight) : 0,
    dimensions: values.dimensions.trim(),
    barcode: values.barcode.trim(),
  };
}

// ---------------------------------------------------------------------------
// Reusable sub-components (internal – not exported)
// ---------------------------------------------------------------------------

/** Inline error text shown beneath a form field. */
function FieldError({ id, message }: { id: string; message: string | undefined }): React.JSX.Element | null {
  if (!message) return null;
  return (
    <p id={id} className="mt-1 text-sm text-red-600" role="alert">
      {message}
    </p>
  );
}

/** Small info icon (SVG, 14 × 14) used next to the stock quantity hint. */
function InfoIcon(): React.JSX.Element {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 16 16"
      fill="currentColor"
      className="h-3.5 w-3.5 shrink-0"
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        d="M15 8A7 7 0 1 1 1 8a7 7 0 0 1 14 0ZM9 5a1 1 0 1 1-2 0 1 1 0 0 1 2 0ZM6.75 8a.75.75 0 0 0 0 1.5h.75v1.75a.75.75 0 0 0 1.5 0v-2.5A.75.75 0 0 0 8.25 8h-1.5Z"
        clipRule="evenodd"
      />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// CSS helper – input class builder
// ---------------------------------------------------------------------------

function inputClasses(hasError: boolean): string {
  const base =
    'mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2';
  return hasError
    ? `${base} border-red-300 text-red-900 focus-visible:outline-red-500`
    : `${base} border-slate-300 text-slate-900 focus-visible:outline-indigo-600`;
}

// ---------------------------------------------------------------------------
// Main component
// ---------------------------------------------------------------------------

/**
 * **ProductManage** — Product edit form page component.
 *
 * Replaces the monolith's `RecordManage.cshtml.cs` for product entities:
 *
 * 1. Loads existing product data via `GET /v1/inventory/products/:id`
 *    (matches the OnGet → Init → RecordsExists → populate pattern).
 * 2. Pre-populates all form fields with current values.
 * 3. Submits updates via `PUT /v1/inventory/products/:id`
 *    (matches OnPost → set record["id"] → UpdateRecord pattern).
 * 4. `stock_quantity` is displayed read-only — stock changes go through
 *    the StockAdjustment page (single-entity-ownership rule from AAP §0.8).
 * 5. Handles 404 (product not found), validation errors (422),
 *    dirty-state tracking, and unsaved-changes confirmation.
 */
function ProductManage(): React.JSX.Element {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  // ── Form state ──────────────────────────────────────────────────────────
  const [formState, setFormState] = useState<ProductFormState>(INITIAL_FORM_STATE);
  const [originalState, setOriginalState] = useState<ProductFormState>(INITIAL_FORM_STATE);
  const [stockQuantity, setStockQuantity] = useState<number>(0);
  const [validation, setValidation] = useState<FormValidation>({ errors: [] });
  const [isDataLoaded, setIsDataLoaded] = useState(false);

  // ── Fetch existing product data ─────────────────────────────────────────
  const {
    data: productResponse,
    isLoading,
    isError,
    error: fetchError,
  } = useQuery<ApiResponse<Product>>({
    queryKey: ['inventory', 'products', id],
    queryFn: () => get<Product>(`/v1/inventory/products/${id}`),
    enabled: !!id,
    retry: (failureCount, error) => {
      // Never retry 404s — the product simply does not exist
      const apiErr = error as unknown as ApiError;
      if (apiErr?.status === 404) return false;
      return failureCount < 2;
    },
  });

  // ── Sync fetched data into form state ───────────────────────────────────
  useEffect(() => {
    if (productResponse?.success && productResponse.object) {
      const product = productResponse.object;
      const state = productToFormState(product);
      setFormState(state);
      setOriginalState(state);
      setStockQuantity(product.stock_quantity ?? 0);
      setIsDataLoaded(true);
    }
  }, [productResponse]);

  // ── Dirty-state comparison ──────────────────────────────────────────────
  const isDirty = useCallback((): boolean => {
    if (!isDataLoaded) return false;
    return (Object.keys(originalState) as Array<keyof ProductFormState>).some(
      (key) => formState[key] !== originalState[key],
    );
  }, [formState, originalState, isDataLoaded]);

  // ── beforeunload listener for unsaved changes ───────────────────────────
  useEffect(() => {
    const handler = (e: BeforeUnloadEvent): void => {
      if (isDirty()) {
        e.preventDefault();
      }
    };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [isDirty]);

  // ── Update mutation (PUT) ───────────────────────────────────────────────
  const updateMutation = useMutation<ApiResponse<Product>, ApiError, ProductUpdatePayload>({
    mutationFn: (payload) => put<Product>(`/v1/inventory/products/${id}`, payload),
    onSuccess: (response) => {
      if (response.success) {
        // Invalidate all product queries so list / details pages refetch
        queryClient.invalidateQueries({ queryKey: ['inventory', 'products'] });
        navigate(`/inventory/products/${id}`);
      } else {
        // API returned success:false — map errors to form validation
        const mapped: ValidationError[] = (response.errors ?? []).map((err) => ({
          propertyName: err.key ?? 'general',
          message: err.message ?? err.value ?? 'Validation failed.',
        }));
        setValidation({
          message: response.message ?? 'Product update failed. Please correct the errors below.',
          errors: mapped,
        });
      }
    },
    onError: (error) => {
      if (error.status === 422 && error.errors?.length) {
        // Server-side validation errors (e.g. SKU uniqueness violation)
        const mapped: ValidationError[] = error.errors.map((err) => ({
          propertyName: err.key ?? 'general',
          message: err.message ?? err.value ?? 'Validation failed.',
        }));
        setValidation({
          message: error.message ?? 'Validation failed. Please correct the errors below.',
          errors: mapped,
        });
      } else {
        setValidation({
          message: error.message ?? 'An unexpected error occurred while updating the product.',
          errors: [],
        });
      }
    },
  });

  // ── Form submission handler ─────────────────────────────────────────────
  const handleSubmit = useCallback(
    (e?: FormEvent) => {
      if (e) e.preventDefault();

      // Client-side validation first
      const clientErrors = validateProductForm(formState);
      if (clientErrors.length > 0) {
        setValidation({ message: 'Please correct the errors below.', errors: clientErrors });
        return;
      }

      setValidation({ errors: [] });
      updateMutation.mutate(formStateToPayload(formState));
    },
    [formState, updateMutation],
  );

  // ── Cancel handler with dirty-state guard ───────────────────────────────
  const handleCancel = useCallback(() => {
    if (isDirty()) {
      const confirmed = window.confirm('You have unsaved changes. Are you sure you want to leave?');
      if (!confirmed) return;
    }
    navigate(`/inventory/products/${id}`);
  }, [isDirty, navigate, id]);

  // ── Generic field change handler ────────────────────────────────────────
  const handleFieldChange = useCallback(
    (field: keyof ProductFormState) =>
      (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>) => {
        setFormState((prev) => ({ ...prev, [field]: e.target.value }));
        // Clear the field-specific error immediately on user input
        setValidation((prev) => ({
          ...prev,
          errors: prev.errors.filter((err) => err.propertyName !== field),
        }));
      },
    [],
  );

  // ── Field-error lookup ──────────────────────────────────────────────────
  const getFieldError = useCallback(
    (fieldName: string): string | undefined =>
      validation.errors.find((e) => e.propertyName === fieldName)?.message,
    [validation.errors],
  );

  // ── Derived render-time values ──────────────────────────────────────────
  const is404 = isError && (fetchError as unknown as ApiError)?.status === 404;
  const productName = productResponse?.object?.name ?? 'Product';
  const showValidation = validation.errors.length > 0 || !!validation.message;

  // ────────────────────────────────────────────────────────────────────────
  // Loading state
  // ────────────────────────────────────────────────────────────────────────
  if (isLoading) {
    return (
      <div
        className="flex min-h-[24rem] items-center justify-center"
        role="status"
        aria-label="Loading product data"
      >
        <div className="flex flex-col items-center gap-3">
          <div className="h-8 w-8 animate-spin rounded-full border-4 border-slate-200 border-t-indigo-600" />
          <p className="text-sm text-slate-500">Loading product data…</p>
        </div>
      </div>
    );
  }

  // ────────────────────────────────────────────────────────────────────────
  // 404 — product not found
  // ────────────────────────────────────────────────────────────────────────
  if (is404) {
    return (
      <div className="flex min-h-[24rem] items-center justify-center">
        <div className="text-center">
          <h2 className="text-xl font-semibold text-slate-800">Product Not Found</h2>
          <p className="mt-2 text-sm text-slate-500">
            The product you are trying to edit does not exist or has been removed.
          </p>
          <button
            type="button"
            onClick={() => navigate('/inventory/products')}
            className="mt-4 inline-flex items-center gap-1.5 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          >
            <span aria-hidden="true">←</span> Back to Products
          </button>
        </div>
      </div>
    );
  }

  // ────────────────────────────────────────────────────────────────────────
  // General fetch error
  // ────────────────────────────────────────────────────────────────────────
  if (isError) {
    return (
      <div className="flex min-h-[24rem] items-center justify-center">
        <div className="text-center">
          <h2 className="text-xl font-semibold text-red-700">Error Loading Product</h2>
          <p className="mt-2 text-sm text-slate-500">
            {(fetchError as unknown as ApiError)?.message ?? 'An unexpected error occurred while loading the product.'}
          </p>
          <button
            type="button"
            onClick={() => navigate('/inventory/products')}
            className="mt-4 inline-flex items-center gap-1.5 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          >
            <span aria-hidden="true">←</span> Back to Products
          </button>
        </div>
      </div>
    );
  }

  // ────────────────────────────────────────────────────────────────────────
  // Main edit form
  // ────────────────────────────────────────────────────────────────────────
  return (
    <div className="mx-auto max-w-4xl px-4 py-6 sm:px-6 lg:px-8">
      {/* Page header */}
      <div className="mb-6">
        <button
          type="button"
          onClick={handleCancel}
          className="mb-2 inline-flex items-center gap-1 text-sm text-indigo-600 hover:text-indigo-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          aria-label={`Back to ${productName} details`}
        >
          <span aria-hidden="true">←</span> Back to Product Details
        </button>
        <h1 className="text-2xl font-bold text-slate-900">{productName}</h1>
        <p className="mt-1 text-sm text-slate-500">Edit Product</p>
      </div>

      {/* Header actions */}
      <div className="mb-6 flex items-center gap-3">
        <button
          type="button"
          onClick={() => handleSubmit()}
          disabled={updateMutation.isPending}
          className="inline-flex items-center gap-1.5 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {updateMutation.isPending ? 'Saving…' : 'Save'}
        </button>
        <button
          type="button"
          onClick={handleCancel}
          disabled={updateMutation.isPending}
          className="inline-flex items-center gap-1.5 rounded-md bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm ring-1 ring-inset ring-slate-300 hover:bg-slate-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
        >
          Cancel
        </button>
      </div>

      {/* DynamicForm wrapper ─ replaces <wv-form method="UpdateRecord"> */}
      <DynamicForm
        name="product-edit"
        method="post"
        showValidation={showValidation}
        validation={validation}
        onSubmit={handleSubmit}
        className="space-y-8"
      >
        {/* ────── Section 1 — Basic Information ────── */}
        <fieldset className="rounded-lg border border-slate-200 p-6">
          <legend className="px-2 text-base font-semibold text-slate-900">Basic Information</legend>

          <div className="mt-4 grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-2">
            {/* Name */}
            <div>
              <label htmlFor="product-name" className="block text-sm font-medium text-slate-700">
                Name <span className="text-red-500" aria-hidden="true">*</span>
              </label>
              <input
                id="product-name"
                type="text"
                required
                maxLength={200}
                value={formState.name}
                onChange={handleFieldChange('name')}
                aria-invalid={!!getFieldError('name')}
                aria-describedby={getFieldError('name') ? 'err-name' : undefined}
                className={inputClasses(!!getFieldError('name'))}
              />
              <FieldError id="err-name" message={getFieldError('name')} />
            </div>

            {/* SKU */}
            <div>
              <label htmlFor="product-sku" className="block text-sm font-medium text-slate-700">
                SKU <span className="text-red-500" aria-hidden="true">*</span>
              </label>
              <input
                id="product-sku"
                type="text"
                required
                maxLength={50}
                value={formState.sku}
                onChange={handleFieldChange('sku')}
                aria-invalid={!!getFieldError('sku')}
                aria-describedby={getFieldError('sku') ? 'err-sku' : undefined}
                className={`${inputClasses(!!getFieldError('sku'))} font-mono`}
              />
              <FieldError id="err-sku" message={getFieldError('sku')} />
            </div>
          </div>

          {/* Description */}
          <div className="mt-4">
            <label htmlFor="product-description" className="block text-sm font-medium text-slate-700">
              Description
            </label>
            <textarea
              id="product-description"
              rows={3}
              value={formState.description}
              onChange={handleFieldChange('description')}
              className="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
            />
          </div>

          <div className="mt-4 grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-2">
            {/* Category */}
            <div>
              <label htmlFor="product-category" className="block text-sm font-medium text-slate-700">
                Category <span className="text-red-500" aria-hidden="true">*</span>
              </label>
              <select
                id="product-category"
                required
                value={formState.category}
                onChange={handleFieldChange('category')}
                aria-invalid={!!getFieldError('category')}
                aria-describedby={getFieldError('category') ? 'err-category' : undefined}
                className={inputClasses(!!getFieldError('category'))}
              >
                {CATEGORY_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
              <FieldError id="err-category" message={getFieldError('category')} />
            </div>

            {/* Status */}
            <div>
              <label htmlFor="product-status" className="block text-sm font-medium text-slate-700">
                Status <span className="text-red-500" aria-hidden="true">*</span>
              </label>
              <select
                id="product-status"
                required
                value={formState.status}
                onChange={handleFieldChange('status')}
                aria-invalid={!!getFieldError('status')}
                aria-describedby={getFieldError('status') ? 'err-status' : undefined}
                className={inputClasses(!!getFieldError('status'))}
              >
                {STATUS_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
              <FieldError id="err-status" message={getFieldError('status')} />
            </div>
          </div>
        </fieldset>

        {/* ────── Section 2 — Pricing & Stock ────── */}
        <fieldset className="rounded-lg border border-slate-200 p-6">
          <legend className="px-2 text-base font-semibold text-slate-900">Pricing &amp; Stock</legend>

          <div className="mt-4 grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-2">
            {/* Price */}
            <div>
              <label htmlFor="product-price" className="block text-sm font-medium text-slate-700">
                Price <span className="text-red-500" aria-hidden="true">*</span>
              </label>
              <div className="relative mt-1">
                <span className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3 text-sm text-slate-500">
                  $
                </span>
                <input
                  id="product-price"
                  type="number"
                  required
                  min={0}
                  step="0.01"
                  value={formState.price}
                  onChange={handleFieldChange('price')}
                  aria-invalid={!!getFieldError('price')}
                  aria-describedby={getFieldError('price') ? 'err-price' : undefined}
                  className={`block w-full rounded-md border py-2 pe-3 ps-7 text-sm shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2 ${
                    getFieldError('price')
                      ? 'border-red-300 text-red-900 focus-visible:outline-red-500'
                      : 'border-slate-300 text-slate-900 focus-visible:outline-indigo-600'
                  }`}
                />
              </div>
              <FieldError id="err-price" message={getFieldError('price')} />
            </div>

            {/* Stock Quantity — READ-ONLY */}
            <div>
              <label htmlFor="product-stock-quantity" className="block text-sm font-medium text-slate-700">
                Stock Quantity
              </label>
              <input
                id="product-stock-quantity"
                type="number"
                value={stockQuantity}
                readOnly
                disabled
                aria-describedby="stock-qty-hint"
                className="mt-1 block w-full rounded-md border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-500 shadow-sm"
              />
              <p id="stock-qty-hint" className="mt-1 flex items-center gap-1 text-xs text-amber-600">
                <InfoIcon />
                Use Stock Adjustment to change stock levels.
              </p>
            </div>
          </div>

          <div className="mt-4 grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-2">
            {/* Reorder Level */}
            <div>
              <label htmlFor="product-reorder-level" className="block text-sm font-medium text-slate-700">
                Reorder Level
              </label>
              <input
                id="product-reorder-level"
                type="number"
                min={0}
                step="1"
                value={formState.reorder_level}
                onChange={handleFieldChange('reorder_level')}
                aria-invalid={!!getFieldError('reorder_level')}
                aria-describedby={getFieldError('reorder_level') ? 'err-reorder' : undefined}
                className={inputClasses(!!getFieldError('reorder_level'))}
              />
              <FieldError id="err-reorder" message={getFieldError('reorder_level')} />
            </div>

            {/* Unit of Measure */}
            <div>
              <label htmlFor="product-unit" className="block text-sm font-medium text-slate-700">
                Unit of Measure
              </label>
              <select
                id="product-unit"
                value={formState.unit_of_measure}
                onChange={handleFieldChange('unit_of_measure')}
                className="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
              >
                {UNIT_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>
          </div>
        </fieldset>

        {/* ────── Section 3 — Physical Attributes ────── */}
        <fieldset className="rounded-lg border border-slate-200 p-6">
          <legend className="px-2 text-base font-semibold text-slate-900">Physical Attributes</legend>

          <div className="mt-4 grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-2">
            {/* Weight */}
            <div>
              <label htmlFor="product-weight" className="block text-sm font-medium text-slate-700">
                Weight
              </label>
              <input
                id="product-weight"
                type="number"
                min={0}
                step="0.01"
                value={formState.weight}
                onChange={handleFieldChange('weight')}
                aria-invalid={!!getFieldError('weight')}
                aria-describedby={getFieldError('weight') ? 'err-weight' : undefined}
                className={inputClasses(!!getFieldError('weight'))}
              />
              <FieldError id="err-weight" message={getFieldError('weight')} />
            </div>

            {/* Dimensions */}
            <div>
              <label htmlFor="product-dimensions" className="block text-sm font-medium text-slate-700">
                Dimensions
              </label>
              <input
                id="product-dimensions"
                type="text"
                placeholder="L × W × H"
                value={formState.dimensions}
                onChange={handleFieldChange('dimensions')}
                className="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
              />
            </div>
          </div>

          {/* Barcode */}
          <div className="mt-4">
            <label htmlFor="product-barcode" className="block text-sm font-medium text-slate-700">
              Barcode
            </label>
            <input
              id="product-barcode"
              type="text"
              placeholder="UPC / EAN"
              value={formState.barcode}
              onChange={handleFieldChange('barcode')}
              className="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2 font-mono text-sm shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
            />
          </div>
        </fieldset>
      </DynamicForm>
    </div>
  );
}

export default ProductManage;
