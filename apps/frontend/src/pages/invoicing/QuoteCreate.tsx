/**
 * QuoteCreate.tsx — Quote/Estimate Creation Form
 *
 * React 19 page component for creating quotes/estimates in the Invoicing
 * bounded-context.  Replaces the monolith's `RecordCreate.cshtml.cs` pattern
 * (form submit → validate → RecordManager.CreateRecord → redirect) with a
 * pure-SPA approach using TanStack Query mutations posted to the Invoicing
 * Lambda handler via API Gateway.
 *
 * Key differences from InvoiceCreate:
 *   - Uses `expiry_date` (default: today + 30 days) instead of `due_date`
 *   - No payment-terms section — quotes don't have terms
 *   - Different default statuses: 'draft' | 'sent' (not 'draft' | 'paid')
 *   - Quote number is auto-generated server-side (AutoNumber pattern)
 *
 * Source mapping:
 *   RecordCreate.cshtml.cs  → form submission + validation + redirect
 *   RecordManager.cs        → field normalisation (currency rounding, AutoNumber)
 *   Definitions.cs          → CurrencyType formatting pattern
 *
 * AAP compliance:
 *   §0.8.1 — Full behavioural parity: line items, calculations, validation
 *   §0.8.1 — Pure static SPA: all state via API calls
 *   §0.8.5 — Tailwind CSS 4.x: no Bootstrap
 *
 * @module pages/invoicing/QuoteCreate
 */

import { useState, useCallback, useEffect, useRef } from 'react';
import { useMutation, useQueryClient, useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { get, post } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';
import { formatCurrency } from '../../utils/formatters';

/* ═══════════════════════════════════════════════════════════════════════════
 * TypeScript Interfaces
 * ═══════════════════════════════════════════════════════════════════════════ */

/** Single line item on the quote — mirrors InvoiceLineItem structure. */
interface QuoteLineItem {
  /** Client-side unique identifier for React key and removal. */
  id: string;
  /** Free-text description of the line item. */
  description: string;
  /** Quantity ordered (must be > 0). */
  quantity: number;
  /** Price per unit in the quote's currency. */
  unit_price: number;
  /** Per-line tax rate as a percentage (0–100). */
  tax_rate: number;
  /** Calculated amount: quantity × unit_price. */
  amount: number;
}

/** Complete form state for creating a new quote. */
interface QuoteCreateForm {
  /** Customer account ID from the CRM service (required). */
  customer_id: string;
  /** Quote issuance date in ISO format (YYYY-MM-DD). */
  issue_date: string;
  /** Quote expiry date in ISO format — quote-specific (not due_date). */
  expiry_date: string;
  /** ISO 4217 currency code (default: 'USD'). */
  currency: string;
  /** Global tax rate percentage applied to (subtotal − discount). */
  tax_rate: number;
  /** Whether discount is percentage or fixed amount. */
  discount_type: 'percentage' | 'fixed';
  /** Discount value (percentage 0–100 or fixed currency amount). */
  discount_value: number;
  /** Optional free-text notes for the quote. */
  notes: string;
  /** Quote status: draft for saving, sent for immediate dispatch. */
  status: 'draft' | 'sent';
  /** Dynamic line items on the quote. */
  line_items: QuoteLineItem[];
}

/** Server response after successful quote creation. */
interface QuoteCreateResponse {
  /** Server-generated UUID for the new quote record. */
  id: string;
  /** Auto-generated quote number (AutoNumber pattern from RecordManager). */
  quote_number: string;
  /** Persisted status of the quote. */
  status: string;
  /** Calculated total amount for the quote. */
  total_amount: number;
  /** ISO 8601 creation timestamp. */
  created_on: string;
}

/** Customer account returned from CRM search endpoint. */
interface CustomerAccount {
  id: string;
  name: string;
}

/** Map of field names to error messages for client-side validation. */
type ValidationErrors = Record<string, string>;

/* ═══════════════════════════════════════════════════════════════════════════
 * Constants
 * ═══════════════════════════════════════════════════════════════════════════ */

/**
 * Currency options for the selector dropdown.
 * Subset of the monolith's COMMON_CURRENCIES from Definitions.cs (CurrencyType).
 */
const CURRENCY_OPTIONS: ReadonlyArray<{ code: string; label: string }> = [
  { code: 'USD', label: 'USD — US Dollar' },
  { code: 'EUR', label: 'EUR — Euro' },
  { code: 'GBP', label: 'GBP — British Pound' },
  { code: 'JPY', label: 'JPY — Japanese Yen' },
  { code: 'CHF', label: 'CHF — Swiss Franc' },
  { code: 'CAD', label: 'CAD — Canadian Dollar' },
  { code: 'AUD', label: 'AUD — Australian Dollar' },
  { code: 'CNY', label: 'CNY — Chinese Yuan' },
  { code: 'BGN', label: 'BGN — Bulgarian Lev' },
];

/** Debounce delay for customer search input in milliseconds. */
const CUSTOMER_SEARCH_DEBOUNCE_MS = 300;

/* ═══════════════════════════════════════════════════════════════════════════
 * Helper Functions
 * ═══════════════════════════════════════════════════════════════════════════ */

/** Generate a unique ID for line items using the Web Crypto API. */
function generateLineItemId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `li-${Date.now()}-${Math.random().toString(36).substring(2, 11)}`;
}

/** Get today's date as an ISO date string (YYYY-MM-DD). */
function getTodayIso(): string {
  return new Date().toISOString().split('T')[0];
}

/** Get a date 30 days in the future as an ISO date string. */
function getDefaultExpiryDate(): string {
  const date = new Date();
  date.setDate(date.getDate() + 30);
  return date.toISOString().split('T')[0];
}

/** Create an empty line item with sensible defaults. */
function createEmptyLineItem(defaultTaxRate: number = 0): QuoteLineItem {
  return {
    id: generateLineItemId(),
    description: '',
    quantity: 1,
    unit_price: 0,
    tax_rate: defaultTaxRate,
    amount: 0,
  };
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Client-Side Validation
 *
 * Mirrors RecordCreate.cshtml.cs ValidateRecordSubmission():
 *   – Required fields must be non-empty
 *   – Date ordering constraints (expiry ≥ issue)
 *   – Line items must each have description + positive quantity
 *   – Discount percentage cannot exceed 100
 * ═══════════════════════════════════════════════════════════════════════════ */

function validateQuote(data: QuoteCreateForm): ValidationErrors {
  const errors: ValidationErrors = {};

  if (!data.customer_id) {
    errors.customer_id = 'Customer is required';
  }
  if (!data.issue_date) {
    errors.issue_date = 'Issue date is required';
  }
  if (!data.expiry_date) {
    errors.expiry_date = 'Expiry date is required';
  }
  if (data.expiry_date && data.issue_date && data.expiry_date < data.issue_date) {
    errors.expiry_date = 'Expiry date must be after issue date';
  }
  if (!data.currency) {
    errors.currency = 'Currency is required';
  }

  /* Line item validation */
  if (data.line_items.length === 0) {
    errors.line_items = 'At least one line item is required';
  }

  data.line_items.forEach((item, index) => {
    if (!item.description.trim()) {
      errors[`line_items.${index}.description`] = 'Description is required';
    }
    if (item.quantity <= 0) {
      errors[`line_items.${index}.quantity`] = 'Quantity must be greater than 0';
    }
    if (item.unit_price < 0) {
      errors[`line_items.${index}.unit_price`] = 'Unit price cannot be negative';
    }
    if (item.tax_rate < 0 || item.tax_rate > 100) {
      errors[`line_items.${index}.tax_rate`] = 'Tax rate must be between 0 and 100';
    }
  });

  /* Discount validation */
  if (data.discount_value < 0) {
    errors.discount_value = 'Discount cannot be negative';
  }
  if (data.discount_type === 'percentage' && data.discount_value > 100) {
    errors.discount_value = 'Discount percentage cannot exceed 100%';
  }

  /* Global tax rate validation */
  if (data.tax_rate < 0 || data.tax_rate > 100) {
    errors.tax_rate = 'Tax rate must be between 0 and 100';
  }

  return errors;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * QuoteCreate Component
 * ═══════════════════════════════════════════════════════════════════════════ */

function QuoteCreate(): React.JSX.Element {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const queryClient = useQueryClient();

  /* Pre-populate customer from ?customerId= URL parameter */
  const preselectedCustomerId = searchParams.get('customerId') ?? '';

  /* ── Form State ─────────────────────────────────────────────────────── */

  const [formData, setFormData] = useState<QuoteCreateForm>({
    customer_id: preselectedCustomerId,
    issue_date: getTodayIso(),
    expiry_date: getDefaultExpiryDate(),
    currency: 'USD',
    tax_rate: 0,
    discount_type: 'percentage',
    discount_value: 0,
    notes: '',
    status: 'draft',
    line_items: [createEmptyLineItem()],
  });

  const [errors, setErrors] = useState<ValidationErrors>({});
  const [serverValidation, setServerValidation] = useState<FormValidation | undefined>();
  const [customerSearch, setCustomerSearch] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [isCustomerDropdownOpen, setIsCustomerDropdownOpen] = useState(false);
  const [selectedCustomerName, setSelectedCustomerName] = useState('');
  const [isDirty, setIsDirty] = useState(false);

  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const customerDropdownRef = useRef<HTMLDivElement>(null);

  /* ── Customer Search Debounce ───────────────────────────────────────── */

  const handleCustomerSearchChange = useCallback((value: string) => {
    setCustomerSearch(value);
    if (debounceTimerRef.current) {
      clearTimeout(debounceTimerRef.current);
    }
    debounceTimerRef.current = setTimeout(() => {
      setDebouncedSearch(value);
    }, CUSTOMER_SEARCH_DEBOUNCE_MS);
  }, []);

  /* ── Customer Query: GET /v1/crm/accounts ──────────────────────────── */

  const { data: customerData, isLoading: isLoadingCustomers } = useQuery({
    queryKey: ['crm', 'accounts', debouncedSearch],
    queryFn: async () => {
      const response = await get<CustomerAccount[]>('/crm/accounts', {
        search: debouncedSearch,
        fields: 'id,name',
        pageSize: 20,
      });
      return response;
    },
    enabled: debouncedSearch.length > 0 || isCustomerDropdownOpen,
    staleTime: 30_000,
  });

  const customers: CustomerAccount[] = customerData?.object ?? [];

  /* ── Pre-populate customer name from URL param ─────────────────────── */

  const { data: preselectedCustomerData } = useQuery({
    queryKey: ['crm', 'accounts', 'detail', preselectedCustomerId],
    queryFn: async () => {
      const response = await get<CustomerAccount[]>('/crm/accounts', {
        search: '',
        fields: 'id,name',
        pageSize: 1,
        id: preselectedCustomerId,
      });
      return response;
    },
    enabled: preselectedCustomerId.length > 0 && selectedCustomerName === '',
  });

  /* Set the pre-selected customer name once loaded */
  useEffect(() => {
    if (
      preselectedCustomerData?.object &&
      preselectedCustomerData.object.length > 0 &&
      selectedCustomerName === ''
    ) {
      setSelectedCustomerName(preselectedCustomerData.object[0].name);
    }
  }, [preselectedCustomerData, selectedCustomerName]);

  /* ── Unsaved Changes Protection ─────────────────────────────────────── */

  useEffect(() => {
    const handleBeforeUnload = (event: BeforeUnloadEvent): void => {
      if (isDirty) {
        event.preventDefault();
      }
    };
    window.addEventListener('beforeunload', handleBeforeUnload);
    return () => {
      window.removeEventListener('beforeunload', handleBeforeUnload);
    };
  }, [isDirty]);

  /* ── Close customer dropdown on outside click ───────────────────────── */

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent): void => {
      if (
        customerDropdownRef.current &&
        !customerDropdownRef.current.contains(event.target as Node)
      ) {
        setIsCustomerDropdownOpen(false);
      }
    };
    document.addEventListener('mousedown', handleClickOutside);
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
    };
  }, []);

  /* ── Calculation Functions ──────────────────────────────────────────── */

  const calculateSubtotal = useCallback((): number => {
    return formData.line_items.reduce((sum, item) => sum + item.amount, 0);
  }, [formData.line_items]);

  const calculateDiscount = useCallback((): number => {
    const subtotal = calculateSubtotal();
    if (formData.discount_type === 'percentage') {
      return Math.round(((subtotal * formData.discount_value) / 100) * 100) / 100;
    }
    return Math.min(formData.discount_value, subtotal);
  }, [calculateSubtotal, formData.discount_type, formData.discount_value]);

  const calculateTax = useCallback((): number => {
    const subtotal = calculateSubtotal();
    const discount = calculateDiscount();
    /* Per-line-item tax accumulation — each line may have its own rate */
    const lineItemTax = formData.line_items.reduce((sum, item) => {
      return sum + Math.round(((item.amount * item.tax_rate) / 100) * 100) / 100;
    }, 0);
    /* Global tax on the discounted subtotal (additive with line taxes) */
    const globalTax =
      Math.round((((subtotal - discount) * formData.tax_rate) / 100) * 100) / 100;
    return lineItemTax + globalTax;
  }, [calculateSubtotal, calculateDiscount, formData.line_items, formData.tax_rate]);

  const calculateTotal = useCallback((): number => {
    return Math.round((calculateSubtotal() - calculateDiscount() + calculateTax()) * 100) / 100;
  }, [calculateSubtotal, calculateDiscount, calculateTax]);

  /* ── Field Change Handler ──────────────────────────────────────────── */

  const handleFieldChange = useCallback(
    (field: keyof QuoteCreateForm, value: string | number) => {
      setFormData((prev) => ({ ...prev, [field]: value }));
      setErrors((prev) => {
        const next = { ...prev };
        delete next[field];
        return next;
      });
      setIsDirty(true);
    },
    [],
  );

  /* ── Customer Selection ─────────────────────────────────────────────── */

  const handleSelectCustomer = useCallback(
    (customer: CustomerAccount) => {
      setFormData((prev) => ({ ...prev, customer_id: customer.id }));
      setSelectedCustomerName(customer.name);
      setCustomerSearch('');
      setDebouncedSearch('');
      setIsCustomerDropdownOpen(false);
      setErrors((prev) => {
        const next = { ...prev };
        delete next.customer_id;
        return next;
      });
      setIsDirty(true);
    },
    [],
  );

  const handleClearCustomer = useCallback(() => {
    setFormData((prev) => ({ ...prev, customer_id: '' }));
    setSelectedCustomerName('');
    setCustomerSearch('');
    setDebouncedSearch('');
    setIsDirty(true);
  }, []);

  /* ── Line Item Management ──────────────────────────────────────────── */

  const handleAddLineItem = useCallback(() => {
    setFormData((prev) => ({
      ...prev,
      line_items: [...prev.line_items, createEmptyLineItem(prev.tax_rate)],
    }));
    setIsDirty(true);
  }, []);

  const handleRemoveLineItem = useCallback((lineId: string) => {
    setFormData((prev) => {
      const filtered = prev.line_items.filter((item) => item.id !== lineId);
      /* Ensure at least one line item remains */
      return {
        ...prev,
        line_items: filtered.length > 0 ? filtered : [createEmptyLineItem(prev.tax_rate)],
      };
    });
    setIsDirty(true);
  }, []);

  const handleLineItemChange = useCallback(
    (lineId: string, field: keyof QuoteLineItem, value: string | number) => {
      setFormData((prev) => ({
        ...prev,
        line_items: prev.line_items.map((item) => {
          if (item.id !== lineId) return item;
          const updated = { ...item, [field]: value };
          /* Recalculate amount when quantity or unit_price changes */
          if (field === 'quantity' || field === 'unit_price') {
            updated.amount =
              Math.round(Number(updated.quantity) * Number(updated.unit_price) * 100) / 100;
          }
          return updated;
        }),
      }));
      /* Clear field-specific errors */
      setErrors((prev) => {
        const next = { ...prev };
        const lineIndex = formData.line_items.findIndex((li) => li.id === lineId);
        if (lineIndex >= 0) {
          delete next[`line_items.${lineIndex}.${field}`];
        }
        delete next.line_items;
        return next;
      });
      setIsDirty(true);
    },
    [formData.line_items],
  );

  /* ── Create Mutation: POST /v1/invoicing/quotes ────────────────────── */

  const createMutation = useMutation<
    ApiResponse<QuoteCreateResponse>,
    ApiError,
    QuoteCreateForm
  >({
    mutationFn: async (data: QuoteCreateForm) => {
      const payload = {
        customer_id: data.customer_id,
        issue_date: data.issue_date,
        expiry_date: data.expiry_date,
        currency: data.currency,
        tax_rate: data.tax_rate,
        discount_type: data.discount_type,
        discount_value: data.discount_value,
        notes: data.notes,
        status: data.status,
        line_items: data.line_items.map((item) => ({
          description: item.description,
          quantity: item.quantity,
          unit_price: item.unit_price,
          tax_rate: item.tax_rate,
        })),
      };
      return post<QuoteCreateResponse>('/invoicing/quotes', payload);
    },
    onSuccess: (response) => {
      setIsDirty(false);
      queryClient.invalidateQueries({ queryKey: ['invoicing', 'quotes'] });

      if (response.success) {
        const quoteId = response.object?.id;
        if (quoteId) {
          navigate(`/invoicing/quotes/${quoteId}`);
        } else {
          navigate('/invoicing/quotes');
        }
      } else {
        /* Server returned a non-success envelope — surface message + errors */
        const serverErrors: ValidationError[] = (response.errors ?? []).map((e) => ({
          propertyName: e.key,
          message: e.message ?? e.value,
        }));
        setServerValidation({
          message: response.message ?? 'Quote creation did not succeed.',
          errors: serverErrors,
        });
      }
    },
    onError: (error: ApiError) => {
      /* Map server 422 validation errors to FormValidation for DynamicForm */
      if (error.status === 422 || (error.errors && error.errors.length > 0)) {
        const validationErrors: ValidationError[] = error.errors.map((e) => ({
          propertyName: e.key,
          message: e.message,
        }));
        setServerValidation({
          message: error.message,
          errors: validationErrors,
        });
      } else {
        setServerValidation({
          message: error.message || 'An unexpected error occurred while creating the quote.',
          errors: [],
        });
      }
    },
  });

  /* ── Form Submission ────────────────────────────────────────────────── */

  const handleSubmit = useCallback(
    (status: 'draft' | 'sent') => {
      setServerValidation(undefined);
      const dataToSubmit: QuoteCreateForm = { ...formData, status };

      /* Client-side validation */
      const validationErrors = validateQuote(dataToSubmit);
      if (Object.keys(validationErrors).length > 0) {
        setErrors(validationErrors);
        /* Build FormValidation from client errors for the summary */
        const clientValidation: FormValidation = {
          message: 'Please correct the errors below before submitting.',
          errors: Object.entries(validationErrors).map(([key, msg]) => ({
            propertyName: key,
            message: msg,
          })),
        };
        setServerValidation(clientValidation);
        return;
      }

      setErrors({});
      createMutation.mutate(dataToSubmit);
    },
    [formData, createMutation],
  );

  /* ── Navigation Handlers ────────────────────────────────────────────── */

  const handleCancel = useCallback(() => {
    if (isDirty) {
      const confirmed = window.confirm(
        'You have unsaved changes. Are you sure you want to leave?',
      );
      if (!confirmed) return;
    }
    setIsDirty(false);
    navigate('/invoicing/quotes');
  }, [isDirty, navigate]);

  const isSubmitting = createMutation.isPending;

  /* ═══════════════════════════════════════════════════════════════════════
   * Render
   * ═══════════════════════════════════════════════════════════════════════ */

  return (
    <div className="mx-auto max-w-5xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ── Page Header ────────────────────────────────────────────────── */}
      <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={handleCancel}
            className="inline-flex items-center rounded-md p-2 text-gray-500 hover:bg-gray-100 hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            aria-label="Back to quotes"
          >
            <svg
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-5 w-5"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M17 10a.75.75 0 0 1-.75.75H5.612l4.158 3.96a.75.75 0 1 1-1.04 1.08l-5.5-5.25a.75.75 0 0 1 0-1.08l5.5-5.25a.75.75 0 1 1 1.04 1.08L5.612 9.25H16.25A.75.75 0 0 1 17 10Z"
                clipRule="evenodd"
              />
            </svg>
          </button>
          <h1 className="text-2xl font-bold tracking-tight text-gray-900">
            Create Quote
          </h1>
        </div>

        {/* Header Actions */}
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={handleCancel}
            disabled={isSubmitting}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={() => handleSubmit('draft')}
            disabled={isSubmitting}
            className="rounded-md border border-indigo-300 bg-indigo-50 px-4 py-2 text-sm font-medium text-indigo-700 shadow-sm hover:bg-indigo-100 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isSubmitting && formData.status === 'draft' ? 'Saving…' : 'Save as Draft'}
          </button>
          <button
            type="button"
            onClick={() => handleSubmit('sent')}
            disabled={isSubmitting}
            className="rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-white disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isSubmitting && formData.status === 'sent' ? 'Creating…' : 'Create Quote'}
          </button>
        </div>
      </div>

      {/* ── Form Body ──────────────────────────────────────────────────── */}
      <DynamicForm
        name="quote-create-form"
        showValidation={true}
        validation={serverValidation}
        onSubmit={(e: React.FormEvent<HTMLFormElement>) => {
          e.preventDefault();
          handleSubmit('sent');
        }}
        className="space-y-8"
      >
        {/* ──────────────────────────────────────────────────────────────
         * Section 1 — Customer & Dates
         * ────────────────────────────────────────────────────────────── */}
        <section
          className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
          aria-labelledby="section-customer-dates"
        >
          <h2
            id="section-customer-dates"
            className="mb-4 text-lg font-semibold text-gray-900"
          >
            Customer &amp; Dates
          </h2>

          {/* Customer Selector — full-width */}
          <div className="mb-4" ref={customerDropdownRef}>
            <label
              htmlFor="customer-search"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Customer <span className="text-red-500">*</span>
            </label>

            {selectedCustomerName && formData.customer_id ? (
              <div className="flex items-center gap-2 rounded-md border border-gray-300 bg-gray-50 px-3 py-2">
                <span className="flex-1 text-sm text-gray-900">
                  {selectedCustomerName}
                </span>
                <button
                  type="button"
                  onClick={handleClearCustomer}
                  className="rounded p-1 text-gray-400 hover:bg-gray-200 hover:text-gray-600 focus-visible:outline-2 focus-visible:outline-indigo-500"
                  aria-label="Clear customer selection"
                >
                  <svg viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
                    <path d="M6.28 5.22a.75.75 0 0 0-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 1 0 1.06 1.06L10 11.06l3.72 3.72a.75.75 0 1 0 1.06-1.06L11.06 10l3.72-3.72a.75.75 0 0 0-1.06-1.06L10 8.94 6.28 5.22Z" />
                  </svg>
                </button>
              </div>
            ) : (
              <div className="relative">
                <input
                  id="customer-search"
                  type="text"
                  value={customerSearch}
                  onChange={(e) => {
                    handleCustomerSearchChange(e.target.value);
                    setIsCustomerDropdownOpen(true);
                  }}
                  onFocus={() => setIsCustomerDropdownOpen(true)}
                  placeholder="Search customers…"
                  autoComplete="off"
                  aria-expanded={isCustomerDropdownOpen}
                  aria-controls="customer-listbox"
                  aria-autocomplete="list"
                  role="combobox"
                  className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ${
                    errors.customer_id
                      ? 'border-red-300 text-red-900 focus-visible:ring-red-500'
                      : 'border-gray-300 text-gray-900'
                  }`}
                />

                {/* Customer Dropdown */}
                {isCustomerDropdownOpen && (
                  <ul
                    id="customer-listbox"
                    role="listbox"
                    className="absolute z-10 mt-1 max-h-60 w-full overflow-auto rounded-md border border-gray-200 bg-white py-1 shadow-lg"
                  >
                    {isLoadingCustomers && (
                      <li className="px-3 py-2 text-sm text-gray-500">
                        Searching…
                      </li>
                    )}
                    {!isLoadingCustomers && customers.length === 0 && debouncedSearch.length > 0 && (
                      <li className="px-3 py-2 text-sm text-gray-500">
                        No customers found
                      </li>
                    )}
                    {!isLoadingCustomers && customers.length === 0 && debouncedSearch.length === 0 && (
                      <li className="px-3 py-2 text-sm text-gray-500">
                        Type to search customers
                      </li>
                    )}
                    {customers.map((customer) => (
                      <li key={customer.id} role="option" aria-selected={false}>
                        <button
                          type="button"
                          onClick={() => handleSelectCustomer(customer)}
                          className="w-full px-3 py-2 text-start text-sm text-gray-900 hover:bg-indigo-50 focus-visible:bg-indigo-50 focus-visible:outline-none"
                        >
                          {customer.name}
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            )}

            {errors.customer_id && (
              <p className="mt-1 text-sm text-red-600" role="alert">
                {errors.customer_id}
              </p>
            )}
          </div>

          {/* Issue Date + Expiry Date (half-width each) */}
          <div className="mb-4 grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <label
                htmlFor="issue-date"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Issue Date <span className="text-red-500">*</span>
              </label>
              <input
                id="issue-date"
                type="date"
                value={formData.issue_date}
                onChange={(e) => handleFieldChange('issue_date', e.target.value)}
                className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ${
                  errors.issue_date
                    ? 'border-red-300 text-red-900 focus-visible:ring-red-500'
                    : 'border-gray-300 text-gray-900'
                }`}
              />
              {errors.issue_date && (
                <p className="mt-1 text-sm text-red-600" role="alert">
                  {errors.issue_date}
                </p>
              )}
            </div>

            <div>
              <label
                htmlFor="expiry-date"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Expiry Date <span className="text-red-500">*</span>
              </label>
              <input
                id="expiry-date"
                type="date"
                value={formData.expiry_date}
                onChange={(e) => handleFieldChange('expiry_date', e.target.value)}
                className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ${
                  errors.expiry_date
                    ? 'border-red-300 text-red-900 focus-visible:ring-red-500'
                    : 'border-gray-300 text-gray-900'
                }`}
              />
              {errors.expiry_date && (
                <p className="mt-1 text-sm text-red-600" role="alert">
                  {errors.expiry_date}
                </p>
              )}
            </div>
          </div>

          {/* Currency (third-width) */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
            <div>
              <label
                htmlFor="currency"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Currency <span className="text-red-500">*</span>
              </label>
              <select
                id="currency"
                value={formData.currency}
                onChange={(e) => handleFieldChange('currency', e.target.value)}
                className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ${
                  errors.currency
                    ? 'border-red-300 text-red-900 focus-visible:ring-red-500'
                    : 'border-gray-300 text-gray-900'
                }`}
              >
                {CURRENCY_OPTIONS.map((c) => (
                  <option key={c.code} value={c.code}>
                    {c.label}
                  </option>
                ))}
              </select>
              {errors.currency && (
                <p className="mt-1 text-sm text-red-600" role="alert">
                  {errors.currency}
                </p>
              )}
            </div>
          </div>
        </section>

        {/* ──────────────────────────────────────────────────────────────
         * Section 2 — Line Items
         * ────────────────────────────────────────────────────────────── */}
        <section
          className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
          aria-labelledby="section-line-items"
        >
          <div className="mb-4 flex items-center justify-between">
            <h2
              id="section-line-items"
              className="text-lg font-semibold text-gray-900"
            >
              Line Items
            </h2>
            <button
              type="button"
              onClick={handleAddLineItem}
              className="inline-flex items-center gap-1.5 rounded-md bg-indigo-50 px-3 py-1.5 text-sm font-medium text-indigo-700 hover:bg-indigo-100 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            >
              <svg viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
                <path d="M10.75 4.75a.75.75 0 0 0-1.5 0v4.5h-4.5a.75.75 0 0 0 0 1.5h4.5v4.5a.75.75 0 0 0 1.5 0v-4.5h4.5a.75.75 0 0 0 0-1.5h-4.5v-4.5Z" />
              </svg>
              Add Line Item
            </button>
          </div>

          {errors.line_items && (
            <p className="mb-3 text-sm text-red-600" role="alert">
              {errors.line_items}
            </p>
          )}

          {/* Line Items Table */}
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200 text-left">
                  <th scope="col" className="w-10 pb-3 pe-2 text-center font-medium text-gray-500">
                    #
                  </th>
                  <th scope="col" className="min-w-[200px] pb-3 pe-2 font-medium text-gray-500">
                    Description
                  </th>
                  <th scope="col" className="w-24 pb-3 pe-2 font-medium text-gray-500">
                    Qty
                  </th>
                  <th scope="col" className="w-32 pb-3 pe-2 font-medium text-gray-500">
                    Unit Price
                  </th>
                  <th scope="col" className="w-24 pb-3 pe-2 font-medium text-gray-500">
                    Tax %
                  </th>
                  <th scope="col" className="w-32 pb-3 pe-2 text-end font-medium text-gray-500">
                    Amount
                  </th>
                  <th scope="col" className="w-12 pb-3">
                    <span className="sr-only">Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {formData.line_items.map((item, index) => {
                  const rowNum = index + 1;
                  return (
                    <tr
                      key={item.id}
                      className="border-b border-gray-100 last:border-b-0"
                    >
                      {/* Row number */}
                      <td className="py-2 pe-2 text-center text-gray-400">
                        {rowNum}
                      </td>

                      {/* Description */}
                      <td className="py-2 pe-2">
                        <input
                          type="text"
                          value={item.description}
                          onChange={(e) =>
                            handleLineItemChange(item.id, 'description', e.target.value)
                          }
                          placeholder="Item description"
                          aria-label={`Line ${rowNum} description`}
                          className={`block w-full rounded-md border px-2 py-1.5 text-sm placeholder:text-gray-400 focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ${
                            errors[`line_items.${index}.description`]
                              ? 'border-red-300'
                              : 'border-gray-300'
                          }`}
                        />
                        {errors[`line_items.${index}.description`] && (
                          <p className="mt-0.5 text-xs text-red-600">
                            {errors[`line_items.${index}.description`]}
                          </p>
                        )}
                      </td>

                      {/* Quantity */}
                      <td className="py-2 pe-2">
                        <input
                          type="number"
                          min="0"
                          step="1"
                          value={item.quantity}
                          onChange={(e) =>
                            handleLineItemChange(
                              item.id,
                              'quantity',
                              Math.max(0, Number(e.target.value)),
                            )
                          }
                          aria-label={`Line ${rowNum} quantity`}
                          className={`block w-full rounded-md border px-2 py-1.5 text-sm text-end focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ${
                            errors[`line_items.${index}.quantity`]
                              ? 'border-red-300'
                              : 'border-gray-300'
                          }`}
                        />
                        {errors[`line_items.${index}.quantity`] && (
                          <p className="mt-0.5 text-xs text-red-600">
                            {errors[`line_items.${index}.quantity`]}
                          </p>
                        )}
                      </td>

                      {/* Unit Price */}
                      <td className="py-2 pe-2">
                        <input
                          type="number"
                          min="0"
                          step="0.01"
                          value={item.unit_price}
                          onChange={(e) =>
                            handleLineItemChange(
                              item.id,
                              'unit_price',
                              Math.max(0, Number(e.target.value)),
                            )
                          }
                          aria-label={`Line ${rowNum} unit price`}
                          className={`block w-full rounded-md border px-2 py-1.5 text-sm text-end focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ${
                            errors[`line_items.${index}.unit_price`]
                              ? 'border-red-300'
                              : 'border-gray-300'
                          }`}
                        />
                        {errors[`line_items.${index}.unit_price`] && (
                          <p className="mt-0.5 text-xs text-red-600">
                            {errors[`line_items.${index}.unit_price`]}
                          </p>
                        )}
                      </td>

                      {/* Tax % */}
                      <td className="py-2 pe-2">
                        <input
                          type="number"
                          min="0"
                          max="100"
                          step="0.1"
                          value={item.tax_rate}
                          onChange={(e) =>
                            handleLineItemChange(
                              item.id,
                              'tax_rate',
                              Math.max(0, Math.min(100, Number(e.target.value))),
                            )
                          }
                          aria-label={`Line ${rowNum} tax rate`}
                          className={`block w-full rounded-md border px-2 py-1.5 text-sm text-end focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ${
                            errors[`line_items.${index}.tax_rate`]
                              ? 'border-red-300'
                              : 'border-gray-300'
                          }`}
                        />
                        {errors[`line_items.${index}.tax_rate`] && (
                          <p className="mt-0.5 text-xs text-red-600">
                            {errors[`line_items.${index}.tax_rate`]}
                          </p>
                        )}
                      </td>

                      {/* Amount (read-only, calculated) */}
                      <td className="py-2 pe-2 text-end font-medium text-gray-900">
                        {formatCurrency(item.amount, formData.currency)}
                      </td>

                      {/* Remove button */}
                      <td className="py-2 text-center">
                        <button
                          type="button"
                          onClick={() => handleRemoveLineItem(item.id)}
                          disabled={formData.line_items.length <= 1}
                          className="rounded p-1 text-gray-400 hover:bg-red-50 hover:text-red-600 focus-visible:outline-2 focus-visible:outline-red-500 disabled:cursor-not-allowed disabled:opacity-30"
                          aria-label={`Remove line ${rowNum}`}
                        >
                          <svg viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
                            <path
                              fillRule="evenodd"
                              d="M8.75 1A2.75 2.75 0 0 0 6 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 1 0 .23 1.482l.149-.022.841 10.518A2.75 2.75 0 0 0 7.596 19h4.807a2.75 2.75 0 0 0 2.742-2.53l.841-10.52.149.023a.75.75 0 0 0 .23-1.482A41.03 41.03 0 0 0 14 4.193V3.75A2.75 2.75 0 0 0 11.25 1h-2.5ZM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325C8.327 4.025 9.16 4 10 4ZM8.58 7.72a.75.75 0 0 0-1.5.06l.3 7.5a.75.75 0 1 0 1.5-.06l-.3-7.5Zm4.34.06a.75.75 0 1 0-1.5-.06l-.3 7.5a.75.75 0 1 0 1.5.06l.3-7.5Z"
                              clipRule="evenodd"
                            />
                          </svg>
                        </button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </section>

        {/* ──────────────────────────────────────────────────────────────
         * Section 3 — Discount, Tax & Totals
         * ────────────────────────────────────────────────────────────── */}
        <section
          className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
          aria-labelledby="section-totals"
        >
          <h2
            id="section-totals"
            className="mb-4 text-lg font-semibold text-gray-900"
          >
            Totals
          </h2>

          <div className="flex flex-col gap-6 lg:flex-row lg:justify-between">
            {/* Discount & Tax Controls */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3 lg:max-w-md">
              {/* Discount Type */}
              <div>
                <label
                  htmlFor="discount-type"
                  className="mb-1 block text-sm font-medium text-gray-700"
                >
                  Discount Type
                </label>
                <select
                  id="discount-type"
                  value={formData.discount_type}
                  onChange={(e) =>
                    handleFieldChange(
                      'discount_type',
                      e.target.value as 'percentage' | 'fixed',
                    )
                  }
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500"
                >
                  <option value="percentage">Percentage (%)</option>
                  <option value="fixed">Fixed Amount</option>
                </select>
              </div>

              {/* Discount Value */}
              <div>
                <label
                  htmlFor="discount-value"
                  className="mb-1 block text-sm font-medium text-gray-700"
                >
                  Discount
                </label>
                <input
                  id="discount-value"
                  type="number"
                  min="0"
                  step="0.01"
                  value={formData.discount_value}
                  onChange={(e) =>
                    handleFieldChange(
                      'discount_value',
                      Math.max(0, Number(e.target.value)),
                    )
                  }
                  className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ${
                    errors.discount_value
                      ? 'border-red-300 focus-visible:ring-red-500'
                      : 'border-gray-300'
                  }`}
                />
                {errors.discount_value && (
                  <p className="mt-1 text-sm text-red-600" role="alert">
                    {errors.discount_value}
                  </p>
                )}
              </div>

              {/* Tax Rate */}
              <div>
                <label
                  htmlFor="tax-rate"
                  className="mb-1 block text-sm font-medium text-gray-700"
                >
                  Tax Rate (%)
                </label>
                <input
                  id="tax-rate"
                  type="number"
                  min="0"
                  max="100"
                  step="0.1"
                  value={formData.tax_rate}
                  onChange={(e) =>
                    handleFieldChange(
                      'tax_rate',
                      Math.max(0, Math.min(100, Number(e.target.value))),
                    )
                  }
                  className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ${
                    errors.tax_rate
                      ? 'border-red-300 focus-visible:ring-red-500'
                      : 'border-gray-300'
                  }`}
                />
                {errors.tax_rate && (
                  <p className="mt-1 text-sm text-red-600" role="alert">
                    {errors.tax_rate}
                  </p>
                )}
              </div>
            </div>

            {/* Calculated Totals — right-aligned */}
            <div className="min-w-[240px]">
              <dl className="space-y-2 text-sm">
                <div className="flex justify-between">
                  <dt className="text-gray-600">Subtotal</dt>
                  <dd className="font-medium text-gray-900">
                    {formatCurrency(calculateSubtotal(), formData.currency)}
                  </dd>
                </div>
                {formData.discount_value > 0 && (
                  <div className="flex justify-between">
                    <dt className="text-gray-600">
                      Discount
                      {formData.discount_type === 'percentage'
                        ? ` (${formData.discount_value}%)`
                        : ''}
                    </dt>
                    <dd className="font-medium text-red-600">
                      −{formatCurrency(calculateDiscount(), formData.currency)}
                    </dd>
                  </div>
                )}
                {calculateTax() > 0 && (
                  <div className="flex justify-between">
                    <dt className="text-gray-600">Tax</dt>
                    <dd className="font-medium text-gray-900">
                      {formatCurrency(calculateTax(), formData.currency)}
                    </dd>
                  </div>
                )}
                <div className="flex justify-between border-t border-gray-200 pt-2">
                  <dt className="text-base font-semibold text-gray-900">Total</dt>
                  <dd className="text-base font-bold text-gray-900">
                    {formatCurrency(calculateTotal(), formData.currency)}
                  </dd>
                </div>
              </dl>
            </div>
          </div>
        </section>

        {/* ──────────────────────────────────────────────────────────────
         * Section 4 — Notes (no Terms section — quotes don't have
         * payment terms, per AAP spec)
         * ────────────────────────────────────────────────────────────── */}
        <section
          className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
          aria-labelledby="section-notes"
        >
          <h2
            id="section-notes"
            className="mb-4 text-lg font-semibold text-gray-900"
          >
            Notes
          </h2>
          <div>
            <label htmlFor="notes" className="sr-only">
              Notes
            </label>
            <textarea
              id="notes"
              rows={4}
              value={formData.notes}
              onChange={(e) => handleFieldChange('notes', e.target.value)}
              placeholder="Add any additional notes for the customer…"
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500"
            />
          </div>
        </section>
      </DynamicForm>
    </div>
  );
}

export default QuoteCreate;
