/**
 * ProductCreate.tsx — Product Creation Form
 *
 * React 19 page component for creating a new product in the Inventory
 * bounded-context service. Replaces the monolith's RecordCreate.cshtml.cs
 * pattern (record creation form with field validation) applied to product
 * entities. Uses DynamicForm component with client-side + server-side
 * validation and TanStack Query mutation for API submission.
 *
 * Route: /inventory/products/create (lazy-loaded)
 */

import React, { useState, useCallback, useEffect } from 'react';
import type { FormEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, Link } from 'react-router-dom';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';
import { post } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';

/* ------------------------------------------------------------------ */
/*  TypeScript Interfaces                                              */
/* ------------------------------------------------------------------ */

/** Payload sent to POST /v1/inventory/products */
interface ProductCreatePayload {
  name: string;
  sku: string;
  description: string;
  price: number;
  category: string;
  status: string;
  stock_quantity: number;
  unit_of_measure: string;
  reorder_level: number;
  weight: number | null;
  dimensions: string;
  barcode: string;
}

/** Shape of the created product returned in ApiResponse.object */
interface CreatedProduct {
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
  weight: number | null;
  dimensions: string;
  barcode: string;
  created_on: string;
  updated_on: string;
}

/** Local form field values — all stored as strings for controlled inputs */
interface FormFields {
  name: string;
  sku: string;
  description: string;
  price: string;
  category: string;
  status: string;
  stock_quantity: string;
  unit_of_measure: string;
  reorder_level: string;
  weight: string;
  dimensions: string;
  barcode: string;
}

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

const PRODUCT_CATEGORIES: ReadonlyArray<{ value: string; label: string }> = [
  { value: '', label: 'Select a category\u2026' },
  { value: 'electronics', label: 'Electronics' },
  { value: 'clothing', label: 'Clothing & Apparel' },
  { value: 'food_beverage', label: 'Food & Beverage' },
  { value: 'home_garden', label: 'Home & Garden' },
  { value: 'health_beauty', label: 'Health & Beauty' },
  { value: 'sports_outdoors', label: 'Sports & Outdoors' },
  { value: 'automotive', label: 'Automotive' },
  { value: 'office_supplies', label: 'Office Supplies' },
  { value: 'industrial', label: 'Industrial' },
  { value: 'raw_materials', label: 'Raw Materials' },
  { value: 'other', label: 'Other' },
];

const PRODUCT_STATUSES: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'active', label: 'Active' },
  { value: 'inactive', label: 'Inactive' },
  { value: 'discontinued', label: 'Discontinued' },
];

const UNITS_OF_MEASURE: ReadonlyArray<{ value: string; label: string }> = [
  { value: '', label: 'Select unit\u2026' },
  { value: 'each', label: 'Each' },
  { value: 'kg', label: 'Kilogram (kg)' },
  { value: 'g', label: 'Gram (g)' },
  { value: 'lb', label: 'Pound (lb)' },
  { value: 'oz', label: 'Ounce (oz)' },
  { value: 'liter', label: 'Liter (L)' },
  { value: 'ml', label: 'Milliliter (mL)' },
  { value: 'meter', label: 'Meter (m)' },
  { value: 'cm', label: 'Centimeter (cm)' },
  { value: 'ft', label: 'Foot (ft)' },
  { value: 'in', label: 'Inch (in)' },
  { value: 'box', label: 'Box' },
  { value: 'case', label: 'Case' },
  { value: 'pack', label: 'Pack' },
  { value: 'pair', label: 'Pair' },
  { value: 'set', label: 'Set' },
];

/** SKU format: uppercase alphanumeric with dashes, 1-50 characters */
const SKU_PATTERN = /^[A-Z0-9][A-Z0-9-]*[A-Z0-9]$|^[A-Z0-9]$/;

const INITIAL_FORM_STATE: FormFields = {
  name: '',
  sku: '',
  description: '',
  price: '',
  category: '',
  status: 'active',
  stock_quantity: '0',
  unit_of_measure: '',
  reorder_level: '',
  weight: '',
  dimensions: '',
  barcode: '',
};

/* ------------------------------------------------------------------ */
/*  Client-Side Validation                                             */
/* ------------------------------------------------------------------ */

/**
 * Validates form fields and returns an array of validation errors.
 * Mirrors the server-side validation in RecordManager.CreateRecord()
 * which enforces required fields, format patterns, and min/max constraints
 * per field type (Text max length, Currency >= 0, Number integer check).
 */
function validateFormFields(fields: FormFields): ValidationError[] {
  const errors: ValidationError[] = [];

  /* --- Name (required, max 200) --- */
  const trimmedName = fields.name.trim();
  if (!trimmedName) {
    errors.push({ propertyName: 'name', message: 'Product name is required.' });
  } else if (trimmedName.length > 200) {
    errors.push({
      propertyName: 'name',
      message: 'Product name must not exceed 200 characters.',
    });
  }

  /* --- SKU (required, max 50, alphanumeric + dashes) --- */
  const skuValue = fields.sku.trim().toUpperCase();
  if (!skuValue) {
    errors.push({ propertyName: 'sku', message: 'SKU is required.' });
  } else if (skuValue.length > 50) {
    errors.push({
      propertyName: 'sku',
      message: 'SKU must not exceed 50 characters.',
    });
  } else if (!SKU_PATTERN.test(skuValue)) {
    errors.push({
      propertyName: 'sku',
      message:
        'SKU must contain only uppercase letters, numbers, and dashes (cannot start or end with a dash).',
    });
  }

  /* --- Price (required, >= 0) --- */
  const priceStr = fields.price.trim();
  if (priceStr === '') {
    errors.push({ propertyName: 'price', message: 'Price is required.' });
  } else {
    const priceVal = parseFloat(priceStr);
    if (Number.isNaN(priceVal)) {
      errors.push({
        propertyName: 'price',
        message: 'Price must be a valid number.',
      });
    } else if (priceVal < 0) {
      errors.push({
        propertyName: 'price',
        message: 'Price must be zero or greater.',
      });
    }
  }

  /* --- Category (required) --- */
  if (!fields.category) {
    errors.push({
      propertyName: 'category',
      message: 'Category is required.',
    });
  }

  /* --- Status (required) --- */
  if (!fields.status) {
    errors.push({ propertyName: 'status', message: 'Status is required.' });
  }

  /* --- Stock Quantity (>= 0 integer, if provided) --- */
  if (fields.stock_quantity.trim() !== '') {
    const stockVal = Number(fields.stock_quantity);
    if (Number.isNaN(stockVal) || stockVal < 0) {
      errors.push({
        propertyName: 'stock_quantity',
        message: 'Stock quantity must be zero or a positive number.',
      });
    } else if (!Number.isInteger(stockVal)) {
      errors.push({
        propertyName: 'stock_quantity',
        message: 'Stock quantity must be a whole number.',
      });
    }
  }

  /* --- Reorder Level (>= 0, if provided) --- */
  if (fields.reorder_level.trim() !== '') {
    const reorderVal = parseFloat(fields.reorder_level);
    if (Number.isNaN(reorderVal) || reorderVal < 0) {
      errors.push({
        propertyName: 'reorder_level',
        message: 'Reorder level must be zero or greater.',
      });
    }
  }

  /* --- Weight (>= 0, if provided) --- */
  if (fields.weight.trim() !== '') {
    const weightVal = parseFloat(fields.weight);
    if (Number.isNaN(weightVal) || weightVal < 0) {
      errors.push({
        propertyName: 'weight',
        message: 'Weight must be zero or greater.',
      });
    }
  }

  return errors;
}

/* ------------------------------------------------------------------ */
/*  Presentational Helpers                                             */
/* ------------------------------------------------------------------ */

/** Common CSS class string for text inputs */
const INPUT_BASE =
  'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm ' +
  'placeholder:text-gray-400 ' +
  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ' +
  'disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500';

/** Additional class applied when a field has a validation error */
const INPUT_ERROR = 'border-red-500 focus-visible:outline-red-600';

/** Returns combined input classes, adding error styles when applicable */
function inputClasses(hasError: boolean): string {
  return hasError ? `${INPUT_BASE} ${INPUT_ERROR}` : INPUT_BASE;
}

/** Helper: look up a field error from the current validation state */
function fieldError(
  errors: ValidationError[],
  propertyName: string,
): string | undefined {
  return errors.find((e) => e.propertyName === propertyName)?.message;
}

/* ------------------------------------------------------------------ */
/*  ProductCreate Component                                            */
/* ------------------------------------------------------------------ */

/**
 * Product creation page component.
 *
 * Replaces the monolith's `RecordCreate.cshtml.cs` OnGet → render form →
 * OnPost → CreateRecord → redirect-to-detail lifecycle with a React SPA
 * equivalent driven by TanStack Query mutation and React Router navigation.
 */
function ProductCreate(): React.JSX.Element {
  /* -- Navigation & query cache ------------------------------------ */
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* -- Form field state -------------------------------------------- */
  const [fields, setFields] = useState<FormFields>(INITIAL_FORM_STATE);

  /* -- Validation & messaging state -------------------------------- */
  const [validation, setValidation] = useState<FormValidation>({
    errors: [],
  });
  const [isDirty, setIsDirty] = useState(false);
  const [physicalOpen, setPhysicalOpen] = useState(false);

  /* -- Unsaved-changes guard --------------------------------------- */
  useEffect(() => {
    if (!isDirty) return undefined;
    const handler = (e: BeforeUnloadEvent) => {
      e.preventDefault();
    };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [isDirty]);

  /* -- Field change handler ---------------------------------------- */
  const handleFieldChange = useCallback(
    (name: keyof FormFields, value: string) => {
      setFields((prev) => ({ ...prev, [name]: value }));
      setIsDirty(true);
      /* Clear per-field error on change for immediate feedback */
      setValidation((prev) => ({
        ...prev,
        errors: prev.errors.filter((e) => e.propertyName !== name),
      }));
    },
    [],
  );

  /* -- Build payload from string form fields ----------------------- */
  const buildPayload = useCallback(
    (f: FormFields): ProductCreatePayload => ({
      name: f.name.trim(),
      sku: f.sku.trim().toUpperCase(),
      description: f.description.trim(),
      price: parseFloat(parseFloat(f.price).toFixed(2)),
      category: f.category,
      status: f.status,
      stock_quantity: f.stock_quantity.trim() !== '' ? parseInt(f.stock_quantity, 10) : 0,
      unit_of_measure: f.unit_of_measure,
      reorder_level: f.reorder_level.trim() !== '' ? parseFloat(f.reorder_level) : 0,
      weight: f.weight.trim() !== '' ? parseFloat(f.weight) : null,
      dimensions: f.dimensions.trim(),
      barcode: f.barcode.trim(),
    }),
    [],
  );

  /* -- TanStack Query mutation ------------------------------------- */
  const createMutation = useMutation<
    ApiResponse<CreatedProduct>,
    ApiError,
    ProductCreatePayload
  >({
    mutationFn: (payload: ProductCreatePayload) =>
      post<CreatedProduct>('/v1/inventory/products', payload),
    onSuccess: (response) => {
      setIsDirty(false);
      /* Invalidate product list cache so new product appears immediately */
      queryClient.invalidateQueries({ queryKey: ['inventory', 'products'] });
      /* Navigate to the newly created product detail page */
      const createdId = response.object?.id;
      if (createdId) {
        navigate(`/inventory/products/${createdId}`);
      } else {
        navigate('/inventory/products');
      }
    },
    onError: (error: ApiError) => {
      /* Map server-side errors to per-field validation errors */
      const serverErrors: ValidationError[] = [];
      if (error.errors && error.errors.length > 0) {
        for (const apiErr of error.errors) {
          serverErrors.push({
            propertyName: apiErr.key ?? '',
            message: apiErr.message ?? apiErr.value ?? 'Validation failed.',
          });
        }
      }

      /* 409 Conflict — duplicate SKU */
      if (error.status === 409) {
        serverErrors.push({
          propertyName: 'sku',
          message: error.message || 'A product with this SKU already exists.',
        });
      }

      setValidation({
        message:
          error.message || 'Failed to create product. Please correct the errors below.',
        errors: serverErrors,
      });
    },
  });

  /* -- Submit handler --------------------------------------------- */
  const handleSubmit = useCallback(
    (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();

      /* Client-side validation */
      const clientErrors = validateFormFields(fields);
      if (clientErrors.length > 0) {
        setValidation({
          message: 'Please correct the errors below.',
          errors: clientErrors,
        });
        return;
      }

      /* Clear previous errors and submit */
      setValidation({ errors: [] });
      createMutation.mutate(buildPayload(fields));
    },
    [fields, createMutation, buildPayload],
  );

  /* -- Cancel handler --------------------------------------------- */
  const handleCancel = useCallback(() => {
    if (
      isDirty &&
      !window.confirm(
        'You have unsaved changes. Are you sure you want to leave?',
      )
    ) {
      return;
    }
    navigate('/inventory/products');
  }, [isDirty, navigate]);

  const isSubmitting = createMutation.isPending;
  const currentErrors = validation.errors;

  /* ---------------------------------------------------------------- */
  /*  JSX Render                                                       */
  /* ---------------------------------------------------------------- */

  return (
    <main className="mx-auto max-w-4xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ---- Page Header ---- */}
      <div className="mb-6">
        <Link
          to="/inventory/products"
          className="inline-flex items-center gap-1 text-sm text-blue-600 hover:text-blue-800"
        >
          {/* Chevron left icon */}
          <svg
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 20 20"
            fill="currentColor"
            className="h-4 w-4"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M12.79 5.23a.75.75 0 01-.02 1.06L8.832 10l3.938 3.71a.75.75 0 11-1.04 1.08l-4.5-4.25a.75.75 0 010-1.08l4.5-4.25a.75.75 0 011.06.02z"
              clipRule="evenodd"
            />
          </svg>
          Back to Products
        </Link>

        <div className="mt-2 flex items-center justify-between">
          <h1 className="flex items-center gap-2 text-2xl font-bold text-gray-900">
            {/* Plus icon */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-6 w-6 text-blue-600"
              aria-hidden="true"
            >
              <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
            </svg>
            Create Product
          </h1>

          {/* Header action buttons */}
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={handleCancel}
              disabled={isSubmitting}
              className={
                'inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ' +
                'hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ' +
                'disabled:cursor-not-allowed disabled:opacity-50'
              }
            >
              Cancel
            </button>
            <button
              type="submit"
              form="product-create-form"
              disabled={isSubmitting}
              className={
                'inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm ' +
                'hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ' +
                'disabled:cursor-not-allowed disabled:opacity-50'
              }
            >
              {isSubmitting ? (
                <>
                  <svg
                    className="-ml-0.5 mr-1.5 h-4 w-4 animate-spin"
                    xmlns="http://www.w3.org/2000/svg"
                    fill="none"
                    viewBox="0 0 24 24"
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
                  Creating…
                </>
              ) : (
                'Create Product'
              )}
            </button>
          </div>
        </div>
      </div>

      {/* ---- Form ---- */}
      <DynamicForm
        id="product-create-form"
        name="product-create"
        method="post"
        showValidation={
          (validation.message !== undefined && validation.message !== '') ||
          currentErrors.length > 0
        }
        validation={validation}
        onSubmit={handleSubmit}
        className="space-y-8"
      >
        {/* ============================================================ */}
        {/* Section 1 — Basic Information                                 */}
        {/* ============================================================ */}
        <fieldset className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <legend className="px-2 text-base font-semibold text-gray-900">
            Basic Information
          </legend>

          <div className="mt-4 grid grid-cols-1 gap-x-6 gap-y-5 md:grid-cols-2">
            {/* Name */}
            <div>
              <label
                htmlFor="product-name"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Name <span className="text-red-500">*</span>
              </label>
              <input
                id="product-name"
                type="text"
                required
                maxLength={200}
                placeholder="Enter product name"
                value={fields.name}
                onChange={(e) => handleFieldChange('name', e.target.value)}
                disabled={isSubmitting}
                className={inputClasses(!!fieldError(currentErrors, 'name'))}
                aria-invalid={!!fieldError(currentErrors, 'name')}
                aria-describedby={
                  fieldError(currentErrors, 'name')
                    ? 'product-name-error'
                    : undefined
                }
              />
              {fieldError(currentErrors, 'name') && (
                <p
                  id="product-name-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {fieldError(currentErrors, 'name')}
                </p>
              )}
            </div>

            {/* SKU */}
            <div>
              <label
                htmlFor="product-sku"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                SKU <span className="text-red-500">*</span>
              </label>
              <input
                id="product-sku"
                type="text"
                required
                maxLength={50}
                placeholder="e.g. PROD-001"
                value={fields.sku}
                onChange={(e) =>
                  handleFieldChange('sku', e.target.value.toUpperCase())
                }
                disabled={isSubmitting}
                className={inputClasses(!!fieldError(currentErrors, 'sku'))}
                aria-invalid={!!fieldError(currentErrors, 'sku')}
                aria-describedby={
                  fieldError(currentErrors, 'sku')
                    ? 'product-sku-error'
                    : undefined
                }
              />
              {fieldError(currentErrors, 'sku') && (
                <p
                  id="product-sku-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {fieldError(currentErrors, 'sku')}
                </p>
              )}
            </div>

            {/* Description (full width) */}
            <div className="md:col-span-2">
              <label
                htmlFor="product-description"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Description
              </label>
              <textarea
                id="product-description"
                rows={4}
                placeholder="Enter product description"
                value={fields.description}
                onChange={(e) =>
                  handleFieldChange('description', e.target.value)
                }
                disabled={isSubmitting}
                className={inputClasses(
                  !!fieldError(currentErrors, 'description'),
                )}
              />
            </div>

            {/* Category */}
            <div>
              <label
                htmlFor="product-category"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Category <span className="text-red-500">*</span>
              </label>
              <select
                id="product-category"
                required
                value={fields.category}
                onChange={(e) =>
                  handleFieldChange('category', e.target.value)
                }
                disabled={isSubmitting}
                className={inputClasses(
                  !!fieldError(currentErrors, 'category'),
                )}
                aria-invalid={!!fieldError(currentErrors, 'category')}
                aria-describedby={
                  fieldError(currentErrors, 'category')
                    ? 'product-category-error'
                    : undefined
                }
              >
                {PRODUCT_CATEGORIES.map((cat) => (
                  <option key={cat.value} value={cat.value}>
                    {cat.label}
                  </option>
                ))}
              </select>
              {fieldError(currentErrors, 'category') && (
                <p
                  id="product-category-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {fieldError(currentErrors, 'category')}
                </p>
              )}
            </div>

            {/* Status */}
            <div>
              <label
                htmlFor="product-status"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Status <span className="text-red-500">*</span>
              </label>
              <select
                id="product-status"
                required
                value={fields.status}
                onChange={(e) =>
                  handleFieldChange('status', e.target.value)
                }
                disabled={isSubmitting}
                className={inputClasses(
                  !!fieldError(currentErrors, 'status'),
                )}
                aria-invalid={!!fieldError(currentErrors, 'status')}
                aria-describedby={
                  fieldError(currentErrors, 'status')
                    ? 'product-status-error'
                    : undefined
                }
              >
                {PRODUCT_STATUSES.map((s) => (
                  <option key={s.value} value={s.value}>
                    {s.label}
                  </option>
                ))}
              </select>
              {fieldError(currentErrors, 'status') && (
                <p
                  id="product-status-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {fieldError(currentErrors, 'status')}
                </p>
              )}
            </div>
          </div>
        </fieldset>

        {/* ============================================================ */}
        {/* Section 2 — Pricing & Stock                                   */}
        {/* ============================================================ */}
        <fieldset className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <legend className="px-2 text-base font-semibold text-gray-900">
            Pricing &amp; Stock
          </legend>

          <div className="mt-4 grid grid-cols-1 gap-x-6 gap-y-5 md:grid-cols-2">
            {/* Price */}
            <div>
              <label
                htmlFor="product-price"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Price <span className="text-red-500">*</span>
              </label>
              <div className="relative">
                <span className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3 text-gray-500">
                  $
                </span>
                <input
                  id="product-price"
                  type="number"
                  required
                  min={0}
                  step="0.01"
                  placeholder="0.00"
                  value={fields.price}
                  onChange={(e) =>
                    handleFieldChange('price', e.target.value)
                  }
                  disabled={isSubmitting}
                  className={`ps-7 ${inputClasses(
                    !!fieldError(currentErrors, 'price'),
                  )}`}
                  aria-invalid={!!fieldError(currentErrors, 'price')}
                  aria-describedby={
                    fieldError(currentErrors, 'price')
                      ? 'product-price-error'
                      : undefined
                  }
                />
              </div>
              {fieldError(currentErrors, 'price') && (
                <p
                  id="product-price-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {fieldError(currentErrors, 'price')}
                </p>
              )}
            </div>

            {/* Stock Quantity */}
            <div>
              <label
                htmlFor="product-stock-quantity"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Stock Quantity
              </label>
              <input
                id="product-stock-quantity"
                type="number"
                min={0}
                step="1"
                placeholder="0"
                value={fields.stock_quantity}
                onChange={(e) =>
                  handleFieldChange('stock_quantity', e.target.value)
                }
                disabled={isSubmitting}
                className={inputClasses(
                  !!fieldError(currentErrors, 'stock_quantity'),
                )}
                aria-invalid={
                  !!fieldError(currentErrors, 'stock_quantity')
                }
                aria-describedby={
                  fieldError(currentErrors, 'stock_quantity')
                    ? 'product-stock-error'
                    : undefined
                }
              />
              {fieldError(currentErrors, 'stock_quantity') && (
                <p
                  id="product-stock-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {fieldError(currentErrors, 'stock_quantity')}
                </p>
              )}
            </div>

            {/* Reorder Level */}
            <div>
              <label
                htmlFor="product-reorder-level"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Reorder Level
              </label>
              <input
                id="product-reorder-level"
                type="number"
                min={0}
                step="1"
                placeholder="Low-stock alert threshold"
                value={fields.reorder_level}
                onChange={(e) =>
                  handleFieldChange('reorder_level', e.target.value)
                }
                disabled={isSubmitting}
                className={inputClasses(
                  !!fieldError(currentErrors, 'reorder_level'),
                )}
                aria-invalid={
                  !!fieldError(currentErrors, 'reorder_level')
                }
                aria-describedby={
                  fieldError(currentErrors, 'reorder_level')
                    ? 'product-reorder-error'
                    : undefined
                }
              />
              {fieldError(currentErrors, 'reorder_level') && (
                <p
                  id="product-reorder-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {fieldError(currentErrors, 'reorder_level')}
                </p>
              )}
            </div>

            {/* Unit of Measure */}
            <div>
              <label
                htmlFor="product-unit"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Unit of Measure
              </label>
              <select
                id="product-unit"
                value={fields.unit_of_measure}
                onChange={(e) =>
                  handleFieldChange('unit_of_measure', e.target.value)
                }
                disabled={isSubmitting}
                className={inputClasses(false)}
              >
                {UNITS_OF_MEASURE.map((u) => (
                  <option key={u.value} value={u.value}>
                    {u.label}
                  </option>
                ))}
              </select>
            </div>
          </div>
        </fieldset>

        {/* ============================================================ */}
        {/* Section 3 — Physical Attributes (collapsible)                 */}
        {/* ============================================================ */}
        <fieldset className="rounded-lg border border-gray-200 bg-white shadow-sm">
          <button
            type="button"
            onClick={() => setPhysicalOpen((prev) => !prev)}
            className={
              'flex w-full items-center justify-between px-6 py-4 text-start ' +
              'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600'
            }
            aria-expanded={physicalOpen}
            aria-controls="physical-attributes-panel"
          >
            <legend className="text-base font-semibold text-gray-900">
              Physical Attributes
            </legend>
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className={`h-5 w-5 text-gray-500 transition-transform duration-200 ${
                physicalOpen ? 'rotate-180' : ''
              }`}
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z"
                clipRule="evenodd"
              />
            </svg>
          </button>

          {physicalOpen && (
            <div
              id="physical-attributes-panel"
              className="border-t border-gray-200 px-6 pb-6 pt-4"
            >
              <div className="grid grid-cols-1 gap-x-6 gap-y-5 md:grid-cols-2">
                {/* Weight */}
                <div>
                  <label
                    htmlFor="product-weight"
                    className="mb-1 block text-sm font-medium text-gray-700"
                  >
                    Weight
                  </label>
                  <input
                    id="product-weight"
                    type="number"
                    min={0}
                    step="0.01"
                    placeholder="Shipping weight"
                    value={fields.weight}
                    onChange={(e) =>
                      handleFieldChange('weight', e.target.value)
                    }
                    disabled={isSubmitting}
                    className={inputClasses(
                      !!fieldError(currentErrors, 'weight'),
                    )}
                    aria-invalid={!!fieldError(currentErrors, 'weight')}
                    aria-describedby={
                      fieldError(currentErrors, 'weight')
                        ? 'product-weight-error'
                        : undefined
                    }
                  />
                  {fieldError(currentErrors, 'weight') && (
                    <p
                      id="product-weight-error"
                      className="mt-1 text-sm text-red-600"
                      role="alert"
                    >
                      {fieldError(currentErrors, 'weight')}
                    </p>
                  )}
                </div>

                {/* Dimensions */}
                <div>
                  <label
                    htmlFor="product-dimensions"
                    className="mb-1 block text-sm font-medium text-gray-700"
                  >
                    Dimensions
                  </label>
                  <input
                    id="product-dimensions"
                    type="text"
                    placeholder="L x W x H (e.g. 30x20x10)"
                    value={fields.dimensions}
                    onChange={(e) =>
                      handleFieldChange('dimensions', e.target.value)
                    }
                    disabled={isSubmitting}
                    className={inputClasses(false)}
                  />
                </div>

                {/* Barcode (full width) */}
                <div className="md:col-span-2">
                  <label
                    htmlFor="product-barcode"
                    className="mb-1 block text-sm font-medium text-gray-700"
                  >
                    Barcode
                  </label>
                  <input
                    id="product-barcode"
                    type="text"
                    placeholder="UPC / EAN barcode"
                    value={fields.barcode}
                    onChange={(e) =>
                      handleFieldChange('barcode', e.target.value)
                    }
                    disabled={isSubmitting}
                    className={inputClasses(false)}
                  />
                </div>
              </div>
            </div>
          )}
        </fieldset>
      </DynamicForm>
    </main>
  );
}

/* ------------------------------------------------------------------ */
/*  Default Export                                                      */
/* ------------------------------------------------------------------ */

export default ProductCreate;
