/**
 * InvoiceCreate.tsx — Invoice Creation Form with Line Items and Tax Calculations
 *
 * React 19 page component for creating invoices with dynamic line item management,
 * real-time financial calculations, customer selection via CRM API search, and
 * quote-to-invoice conversion support. Replaces RecordCreate.cshtml.cs for the
 * Invoicing bounded-context (RDS PostgreSQL with ACID transactions).
 */

import { useState, useCallback, useEffect } from 'react';
import type { FormEvent, ChangeEvent } from 'react';
import { useMutation, useQueryClient, useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import DynamicForm, {
  type FormValidation,
  type ValidationError,
} from '../../components/forms/DynamicForm';
import { get, post } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import { formatCurrency } from '../../utils/formatters';
import { generateGuid } from '../../utils/helpers';
import { COMMON_CURRENCIES } from '../../utils/constants';

/* ═══════════════════════════════════════════════════════════════
 * Type Definitions
 * ═══════════════════════════════════════════════════════════════ */

/** Single line item on an invoice */
interface InvoiceLineItem {
  /** Client-side temporary UUID for React key-based list rendering */
  id: string;
  /** Description of the product or service */
  description: string;
  /** Quantity of units (must be > 0) */
  quantity: number;
  /** Price per unit (>= 0); respects currency DecimalDigits precision */
  unit_price: number;
  /** Optional per-line tax rate override as a percentage */
  tax_rate: number;
  /** Calculated read-only amount: quantity × unit_price */
  amount: number;
}

/** Complete form state for invoice creation */
interface InvoiceCreateForm {
  customer_id: string;
  issue_date: string;
  due_date: string;
  currency: string;
  tax_rate: number;
  discount_type: 'percentage' | 'fixed';
  discount_value: number;
  notes: string;
  terms: string;
  status: 'draft' | 'sent';
  line_items: InvoiceLineItem[];
  from_quote_id?: string;
}

/** Server response after successful invoice creation */
interface InvoiceCreateResponse {
  id: string;
  /** Auto-generated sequential invoice number (e.g. INV-0001) */
  invoice_number: string;
  status: string;
  total_amount: number;
  created_on: string;
}

/** Per-field validation errors */
interface ValidationErrors {
  customer_id?: string;
  issue_date?: string;
  due_date?: string;
  currency?: string;
  tax_rate?: string;
  discount_value?: string;
  notes?: string;
  terms?: string;
  line_items?: Record<number, Record<string, string>> | string;
  _form?: string;
  [key: string]: string | Record<number, Record<string, string>> | undefined;
}

/** Customer account from CRM service */
interface CustomerAccount {
  id: string;
  name: string;
}

/** Quote data for quote-to-invoice conversion */
interface QuoteData {
  id: string;
  quote_number: string;
  customer_id: string;
  customer_name: string;
  currency: string;
  tax_rate: number;
  discount_type: 'percentage' | 'fixed';
  discount_value: number;
  notes: string;
  line_items: Array<{
    description: string;
    quantity: number;
    unit_price: number;
    tax_rate: number;
  }>;
}

/* ═══════════════════════════════════════════════════════════════
 * Utility Functions
 * ═══════════════════════════════════════════════════════════════ */

/** Returns today in ISO YYYY-MM-DD format */
function getToday(): string {
  return new Date().toISOString().split('T')[0];
}

/** Returns date 30 days from today in ISO YYYY-MM-DD format */
function getDefaultDueDate(): string {
  const d = new Date();
  d.setDate(d.getDate() + 30);
  return d.toISOString().split('T')[0];
}

/** Creates a new empty line item with a unique client-side UUID */
function createEmptyLineItem(): InvoiceLineItem {
  return {
    id: generateGuid(),
    description: '',
    quantity: 1,
    unit_price: 0,
    tax_rate: 0,
    amount: 0,
  };
}

/** Rounds a number to the given decimal places */
function roundToDecimal(value: number, decimals: number): number {
  const f = Math.pow(10, decimals);
  return Math.round(value * f) / f;
}

/** Returns DecimalDigits for the given currency code */
function getCurrencyDecimals(code: string): number {
  const c = COMMON_CURRENCIES[code];
  return c ? c.decimalDigits : 2;
}

/* ═══════════════════════════════════════════════════════════════
 * Client-Side Validation
 * ═══════════════════════════════════════════════════════════════ */

function validateInvoice(data: InvoiceCreateForm): ValidationErrors {
  const errors: ValidationErrors = {};

  if (!data.customer_id) errors.customer_id = 'Customer is required';
  if (!data.issue_date) errors.issue_date = 'Issue date is required';
  if (!data.due_date) errors.due_date = 'Due date is required';
  if (data.due_date && data.issue_date && data.due_date < data.issue_date) {
    errors.due_date = 'Due date must be after issue date';
  }
  if (!data.currency) errors.currency = 'Currency is required';
  if (data.discount_value < 0) errors.discount_value = 'Discount cannot be negative';
  if (data.discount_type === 'percentage' && data.discount_value > 100) {
    errors.discount_value = 'Percentage discount cannot exceed 100%';
  }
  if (data.tax_rate < 0) errors.tax_rate = 'Tax rate cannot be negative';

  if (data.line_items.length === 0) {
    errors.line_items = 'At least one line item is required';
  } else {
    const lineErrors: Record<number, Record<string, string>> = {};
    data.line_items.forEach((item, idx) => {
      const ie: Record<string, string> = {};
      if (!item.description.trim()) ie.description = 'Description is required';
      if (!item.quantity || item.quantity <= 0)
        ie.quantity = 'Quantity must be greater than 0';
      if (item.unit_price === undefined || item.unit_price < 0)
        ie.unit_price = 'Unit price must be 0 or greater';
      if (item.tax_rate < 0) ie.tax_rate = 'Tax rate cannot be negative';
      if (Object.keys(ie).length > 0) lineErrors[idx] = ie;
    });
    if (Object.keys(lineErrors).length > 0) errors.line_items = lineErrors;
  }

  return errors;
}

/* ═══════════════════════════════════════════════════════════════
 * InvoiceCreate Component
 * ═══════════════════════════════════════════════════════════════ */

function InvoiceCreate() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const queryClient = useQueryClient();

  /* URL parameters for pre-population */
  const customerIdParam = searchParams.get('customerId') ?? '';
  const fromQuoteParam = searchParams.get('fromQuote') ?? '';

  /* ── Form state ──────────────────────────────────────────── */
  const [formData, setFormData] = useState<InvoiceCreateForm>({
    customer_id: customerIdParam,
    issue_date: getToday(),
    due_date: getDefaultDueDate(),
    currency: 'USD',
    tax_rate: 0,
    discount_type: 'percentage',
    discount_value: 0,
    notes: '',
    terms: '',
    status: 'draft',
    line_items: [createEmptyLineItem()],
    from_quote_id: fromQuoteParam || undefined,
  });

  const [errors, setErrors] = useState<ValidationErrors>({});
  const [serverValidation, setServerValidation] =
    useState<FormValidation | null>(null);
  const [customerSearch, setCustomerSearch] = useState('');
  const [selectedCustomerName, setSelectedCustomerName] = useState('');
  const [isDirty, setIsDirty] = useState(false);
  const [isCustomerDropdownOpen, setIsCustomerDropdownOpen] = useState(false);

  /* ── Customer search query (CRM service) ─────────────────── */
  const { data: customersResponse, isLoading: isLoadingCustomers } = useQuery({
    queryKey: ['crm', 'accounts', customerSearch] as const,
    queryFn: () =>
      get<{ data: CustomerAccount[] }>('/crm/accounts', {
        search: customerSearch,
        fields: 'id,name',
        pageSize: 20,
      }),
    enabled: customerSearch.length > 0 || isCustomerDropdownOpen,
    staleTime: 30_000,
  });
  const customers: CustomerAccount[] =
    customersResponse?.object?.data ?? [];

  /* ── Quote data fetch (conversion flow) ──────────────────── */
  const { data: quoteResponse, isLoading: isLoadingQuote } = useQuery({
    queryKey: ['invoicing', 'quotes', fromQuoteParam] as const,
    queryFn: () => get<QuoteData>(`/invoicing/quotes/${fromQuoteParam}`),
    enabled: Boolean(fromQuoteParam),
  });

  /* ── Fetch customer name for URL-pre-populated customerId ── */
  const { data: customerByIdResponse } = useQuery({
    queryKey: ['crm', 'accounts', 'detail', customerIdParam] as const,
    queryFn: () => get<CustomerAccount>(`/crm/accounts/${customerIdParam}`),
    enabled:
      Boolean(customerIdParam) &&
      !fromQuoteParam &&
      !selectedCustomerName,
  });

  /* ── Pre-populate customer name from ID param ────────────── */
  useEffect(() => {
    if (customerByIdResponse?.success && customerByIdResponse.object) {
      setSelectedCustomerName(customerByIdResponse.object.name);
    }
  }, [customerByIdResponse]);

  /* ── Pre-populate from quote data ────────────────────────── */
  useEffect(() => {
    if (!quoteResponse?.success || !quoteResponse.object) return;
    const q = quoteResponse.object;
    const cur = q.currency || 'USD';
    setFormData((prev) => ({
      ...prev,
      customer_id: q.customer_id,
      currency: cur,
      tax_rate: q.tax_rate ?? 0,
      discount_type: q.discount_type ?? 'percentage',
      discount_value: q.discount_value ?? 0,
      notes: q.notes ?? '',
      from_quote_id: q.id,
      line_items: q.line_items.map((li) => ({
        id: generateGuid(),
        description: li.description,
        quantity: li.quantity,
        unit_price: li.unit_price,
        tax_rate: li.tax_rate ?? 0,
        amount: roundToDecimal(
          li.quantity * li.unit_price,
          getCurrencyDecimals(cur),
        ),
      })),
    }));
    if (q.customer_name) setSelectedCustomerName(q.customer_name);
  }, [quoteResponse]);

  /* ── Unsaved-changes beforeunload warning ─────────────────── */
  useEffect(() => {
    if (!isDirty) return undefined;
    const handler = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      return '';
    };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [isDirty]);

  /* ── Financial calculations ──────────────────────────────── */
  const decimals = getCurrencyDecimals(formData.currency);

  const subtotal = formData.line_items.reduce(
    (s, li) => s + roundToDecimal(li.quantity * li.unit_price, decimals),
    0,
  );

  const taxAmount = formData.line_items.reduce((s, li) => {
    const lineTotal = li.quantity * li.unit_price;
    const rate = li.tax_rate > 0 ? li.tax_rate : formData.tax_rate;
    return s + roundToDecimal((lineTotal * rate) / 100, decimals);
  }, 0);

  const discountAmount =
    formData.discount_type === 'percentage'
      ? roundToDecimal((subtotal * formData.discount_value) / 100, decimals)
      : roundToDecimal(
          Math.min(formData.discount_value, subtotal + taxAmount),
          decimals,
        );

  const totalAmount = roundToDecimal(
    subtotal + taxAmount - discountAmount,
    decimals,
  );

  /* ── Line item handlers ──────────────────────────────────── */
  const handleAddLineItem = useCallback(() => {
    setFormData((p) => ({
      ...p,
      line_items: [...p.line_items, createEmptyLineItem()],
    }));
    setIsDirty(true);
  }, []);

  const handleRemoveLineItem = useCallback((itemId: string) => {
    setFormData((p) => {
      if (p.line_items.length <= 1) return p;
      return { ...p, line_items: p.line_items.filter((l) => l.id !== itemId) };
    });
    setIsDirty(true);
  }, []);

  const handleLineItemChange = useCallback(
    (itemId: string, field: keyof InvoiceLineItem, value: string | number) => {
      setFormData((p) => ({
        ...p,
        line_items: p.line_items.map((item) => {
          if (item.id !== itemId) return item;
          const u = { ...item, [field]: value };
          u.amount = roundToDecimal(
            u.quantity * u.unit_price,
            getCurrencyDecimals(p.currency),
          );
          return u;
        }),
      }));
      setIsDirty(true);
    },
    [],
  );

  /* ── Header field handler ────────────────────────────────── */
  const handleFieldChange = useCallback(
    (field: keyof InvoiceCreateForm, value: string | number) => {
      setFormData((p) => ({ ...p, [field]: value }));
      setIsDirty(true);
      setErrors((p) => {
        const n = { ...p };
        delete n[field];
        return n;
      });
    },
    [],
  );

  /* ── Customer selector handler ───────────────────────────── */
  const handleCustomerSelect = useCallback((c: CustomerAccount) => {
    setFormData((p) => ({ ...p, customer_id: c.id }));
    setSelectedCustomerName(c.name);
    setIsCustomerDropdownOpen(false);
    setCustomerSearch('');
    setIsDirty(true);
    setErrors((p) => ({ ...p, customer_id: undefined }));
  }, []);

  /* ── Create mutation ─────────────────────────────────────── */
  const createMutation = useMutation<
    ApiResponse<InvoiceCreateResponse>,
    ApiError,
    InvoiceCreateForm
  >({
    mutationFn: (payload) =>
      post<InvoiceCreateResponse>('/invoicing/invoices', {
        customer_id: payload.customer_id,
        issue_date: payload.issue_date,
        due_date: payload.due_date,
        currency: payload.currency,
        tax_rate: payload.tax_rate,
        discount_type: payload.discount_type,
        discount_value: payload.discount_value,
        notes: payload.notes,
        terms: payload.terms,
        status: payload.status,
        line_items: payload.line_items.map((li) => ({
          description: li.description,
          quantity: li.quantity,
          unit_price: li.unit_price,
          tax_rate: li.tax_rate,
        })),
        from_quote_id: payload.from_quote_id,
      }),
    onSuccess: (response) => {
      if (response.success && response.object) {
        setIsDirty(false);
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'invoices'] });
        if (fromQuoteParam) {
          queryClient.invalidateQueries({ queryKey: ['invoicing', 'quotes'] });
        }
        navigate(`/invoicing/invoices/${response.object.id}`);
        return;
      }
      /* Server returned success envelope but with validation errors */
      if (response.errors?.length) {
        const ve: ValidationError[] = response.errors.map((e) => ({
          propertyName: e.key,
          message: e.message,
        }));
        setServerValidation({
          message: response.message || 'Validation errors occurred',
          errors: ve,
        });
        const fe: ValidationErrors = {};
        response.errors.forEach((e) => {
          if (e.key) fe[e.key] = e.message;
        });
        setErrors(fe);
      }
    },
    onError: (error) => {
      if (error.status === 422 && error.errors?.length) {
        const ve: ValidationError[] = error.errors.map((e) => ({
          propertyName: e.key ?? '',
          message: e.message,
        }));
        setServerValidation({
          message: error.message || 'Validation failed',
          errors: ve,
        });
        const fe: ValidationErrors = {};
        error.errors.forEach((e) => {
          if (e.key) fe[e.key] = e.message;
        });
        setErrors(fe);
      } else {
        setServerValidation({
          message: error.message || 'An unexpected error occurred',
          errors: [],
        });
      }
    },
  });

  /* ── Form submission ─────────────────────────────────────── */
  const handleSubmit = useCallback(
    (e: FormEvent<HTMLFormElement>, status: 'draft' | 'sent' = 'sent') => {
      e.preventDefault();
      setServerValidation(null);
      const payload: InvoiceCreateForm = { ...formData, status };
      const v = validateInvoice(payload);

      if (Object.keys(v).length > 0) {
        setErrors(v);
        const all: ValidationError[] = [];
        Object.entries(v).forEach(([k, val]) => {
          if (typeof val === 'string') all.push({ propertyName: k, message: val });
        });
        if (all.length > 0) {
          setServerValidation({
            message: 'Please fix the errors below',
            errors: all,
          });
        }
        return;
      }

      setErrors({});
      createMutation.mutate(payload);
    },
    [formData, createMutation],
  );

  const handleSaveAsDraft = useCallback(() => {
    handleSubmit(
      { preventDefault: () => {} } as FormEvent<HTMLFormElement>,
      'draft',
    );
  }, [handleSubmit]);

  const handleCancel = useCallback(() => {
    if (
      isDirty &&
      !window.confirm('You have unsaved changes. Are you sure you want to leave?')
    ) {
      return;
    }
    navigate('/invoicing/invoices');
  }, [isDirty, navigate]);

  /* ── Line-item error helper ──────────────────────────────── */
  const getLineError = useCallback(
    (index: number, field: string): string | undefined => {
      if (
        typeof errors.line_items === 'object' &&
        errors.line_items !== null &&
        typeof errors.line_items !== 'string'
      ) {
        const le = errors.line_items as Record<number, Record<string, string>>;
        return le[index]?.[field];
      }
      return undefined;
    },
    [errors.line_items],
  );

  /* ── Currency options ────────────────────────────────────── */
  const currencyOptions = Object.entries(COMMON_CURRENCIES).map(
    ([code, info]) => ({
      code,
      label: `${info.symbol} ${code}`,
    }),
  );

  const isSubmitting = createMutation.isPending;

  /* ═══════════════════════════════════════════════════════════
   * Loading State — Quote Conversion
   * ═══════════════════════════════════════════════════════════ */
  if (fromQuoteParam && isLoadingQuote) {
    return (
      <div className="flex min-h-[50vh] items-center justify-center">
        <div className="text-center">
          <div
            className="inline-block h-8 w-8 animate-spin rounded-full border-4 border-solid border-blue-600 border-e-transparent"
            role="status"
            aria-label="Loading quote data"
          />
          <p className="mt-4 text-sm text-gray-600">Loading quote data…</p>
        </div>
      </div>
    );
  }

  /* ═══════════════════════════════════════════════════════════
   * Render
   * ═══════════════════════════════════════════════════════════ */
  return (
    <div className="mx-auto max-w-5xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ── Page Header ──────────────────────────────────────── */}
      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div>
          <nav className="mb-1" aria-label="Back navigation">
            <button
              type="button"
              onClick={handleCancel}
              className="text-sm text-blue-600 hover:text-blue-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              ← Back to Invoices
            </button>
          </nav>
          <h1 className="text-2xl font-bold text-gray-900">Create Invoice</h1>
        </div>

        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={handleCancel}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={handleSaveAsDraft}
            disabled={isSubmitting}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isSubmitting ? 'Saving…' : 'Save as Draft'}
          </button>
          <button
            type="submit"
            form="invoice-create-form"
            disabled={isSubmitting}
            className="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isSubmitting ? 'Creating…' : 'Create Invoice'}
          </button>
        </div>
      </div>

      {/* ── Quote Conversion Banner ──────────────────────────── */}
      {fromQuoteParam && quoteResponse?.success && quoteResponse.object && (
        <div
          className="mb-6 rounded-md border border-blue-200 bg-blue-50 p-4"
          role="status"
        >
          <div className="flex items-center gap-2">
            <svg
              className="h-5 w-5 flex-shrink-0 text-blue-500"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7-4a1 1 0 11-2 0 1 1 0 012 0zM9 9a.75.75 0 000 1.5h.253a.25.25 0 01.244.304l-.459 2.066A1.75 1.75 0 0010.747 15H11a.75.75 0 000-1.5h-.253a.25.25 0 01-.244-.304l.459-2.066A1.75 1.75 0 009.253 9H9z"
                clipRule="evenodd"
              />
            </svg>
            <p className="text-sm font-medium text-blue-800">
              Creating invoice from Quote #{quoteResponse.object.quote_number}
            </p>
          </div>
        </div>
      )}

      {/* ── DynamicForm Wrapper ───────────────────────────────── */}
      <DynamicForm
        id="invoice-create-form"
        name="invoice-create"
        method="post"
        labelMode="stacked"
        fieldMode="form"
        validation={serverValidation ?? undefined}
        onSubmit={(e) => handleSubmit(e, 'sent')}
      >
        {/* ── Section 1: Customer & Dates ────────────────────── */}
        <section className="mb-8 rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <h2 className="mb-4 text-lg font-semibold text-gray-900">
            Customer &amp; Dates
          </h2>

          {/* Customer selector with search */}
          <div className="mb-4">
            <label
              htmlFor="customer-search"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Customer <span className="text-red-500">*</span>
            </label>
            <div className="relative">
              {selectedCustomerName && !isCustomerDropdownOpen ? (
                <div className="flex items-center gap-2 rounded-md border border-gray-300 bg-gray-50 px-3 py-2">
                  <span className="flex-1 text-sm text-gray-900">
                    {selectedCustomerName}
                  </span>
                  <button
                    type="button"
                    onClick={() => {
                      setSelectedCustomerName('');
                      setFormData((p) => ({ ...p, customer_id: '' }));
                      setIsCustomerDropdownOpen(true);
                      setIsDirty(true);
                    }}
                    className="text-sm text-blue-600 hover:text-blue-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
                    aria-label="Change customer"
                  >
                    Change
                  </button>
                </div>
              ) : (
                <>
                  <input
                    id="customer-search"
                    type="text"
                    placeholder="Search customers by name…"
                    value={customerSearch}
                    onChange={(e: ChangeEvent<HTMLInputElement>) => {
                      setCustomerSearch(e.target.value);
                      setIsCustomerDropdownOpen(true);
                    }}
                    onFocus={() => setIsCustomerDropdownOpen(true)}
                    onBlur={() => {
                      setTimeout(() => setIsCustomerDropdownOpen(false), 200);
                    }}
                    className={`w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
                      errors.customer_id
                        ? 'border-red-300 focus:border-red-500 focus:ring-red-500'
                        : 'border-gray-300'
                    }`}
                    aria-expanded={isCustomerDropdownOpen}
                    aria-autocomplete="list"
                    aria-controls="customer-listbox"
                    role="combobox"
                  />
                  {isCustomerDropdownOpen && (
                    <ul
                      id="customer-listbox"
                      role="listbox"
                      className="absolute z-10 mt-1 max-h-48 w-full overflow-auto rounded-md border border-gray-200 bg-white shadow-lg"
                    >
                      {isLoadingCustomers && (
                        <li className="px-3 py-2 text-sm text-gray-500">
                          Searching…
                        </li>
                      )}
                      {!isLoadingCustomers &&
                        customers.length === 0 &&
                        customerSearch.length > 0 && (
                          <li className="px-3 py-2 text-sm text-gray-500">
                            No customers found
                          </li>
                        )}
                      {!isLoadingCustomers &&
                        customers.length === 0 &&
                        customerSearch.length === 0 && (
                          <li className="px-3 py-2 text-sm text-gray-500">
                            Type to search customers
                          </li>
                        )}
                      {customers.map((c) => (
                        <li
                          key={c.id}
                          role="option"
                          aria-selected={formData.customer_id === c.id}
                          className="cursor-pointer px-3 py-2 text-sm text-gray-900 hover:bg-blue-50"
                          onMouseDown={() => handleCustomerSelect(c)}
                        >
                          {c.name}
                        </li>
                      ))}
                    </ul>
                  )}
                </>
              )}
            </div>
            {errors.customer_id && (
              <p className="mt-1 text-sm text-red-600" role="alert">
                {errors.customer_id}
              </p>
            )}
          </div>

          {/* Issue Date & Due Date */}
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
                onChange={(e: ChangeEvent<HTMLInputElement>) =>
                  handleFieldChange('issue_date', e.target.value)
                }
                className={`w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
                  errors.issue_date
                    ? 'border-red-300 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300'
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
                htmlFor="due-date"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Due Date <span className="text-red-500">*</span>
              </label>
              <input
                id="due-date"
                type="date"
                value={formData.due_date}
                onChange={(e: ChangeEvent<HTMLInputElement>) =>
                  handleFieldChange('due_date', e.target.value)
                }
                className={`w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
                  errors.due_date
                    ? 'border-red-300 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300'
                }`}
              />
              {errors.due_date && (
                <p className="mt-1 text-sm text-red-600" role="alert">
                  {errors.due_date}
                </p>
              )}
            </div>
          </div>

          {/* Currency selector */}
          <div className="max-w-xs">
            <label
              htmlFor="currency"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Currency <span className="text-red-500">*</span>
            </label>
            <select
              id="currency"
              value={formData.currency}
              onChange={(e: ChangeEvent<HTMLSelectElement>) =>
                handleFieldChange('currency', e.target.value)
              }
              className={`w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
                errors.currency
                  ? 'border-red-300 focus:border-red-500 focus:ring-red-500'
                  : 'border-gray-300'
              }`}
            >
              {currencyOptions.map((o) => (
                <option key={o.code} value={o.code}>
                  {o.label}
                </option>
              ))}
            </select>
            {errors.currency && (
              <p className="mt-1 text-sm text-red-600" role="alert">
                {errors.currency}
              </p>
            )}
          </div>
        </section>

        {/* ── Section 2: Line Items ──────────────────────────── */}
        <section className="mb-8 rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <h2 className="mb-4 text-lg font-semibold text-gray-900">
            Line Items
          </h2>

          {typeof errors.line_items === 'string' && (
            <p className="mb-4 text-sm text-red-600" role="alert">
              {errors.line_items}
            </p>
          )}

          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200 text-start text-xs font-medium uppercase tracking-wider text-gray-500">
                  <th className="w-10 pb-3 pe-2">#</th>
                  <th className="min-w-[200px] pb-3 pe-2">Description</th>
                  <th className="w-24 pb-3 pe-2 text-end">Qty</th>
                  <th className="w-32 pb-3 pe-2 text-end">Unit Price</th>
                  <th className="w-24 pb-3 pe-2 text-end">Tax %</th>
                  <th className="w-32 pb-3 pe-2 text-end">Amount</th>
                  <th className="w-12 pb-3" aria-label="Actions" />
                </tr>
              </thead>
              <tbody>
                {formData.line_items.map((item, index) => (
                  <tr
                    key={item.id}
                    className="border-b border-gray-100 last:border-b-0"
                  >
                    {/* Row number */}
                    <td className="py-2 pe-2 align-top text-gray-500">
                      {index + 1}
                    </td>

                    {/* Description */}
                    <td className="py-2 pe-2 align-top">
                      <input
                        type="text"
                        value={item.description}
                        onChange={(e: ChangeEvent<HTMLInputElement>) =>
                          handleLineItemChange(
                            item.id,
                            'description',
                            e.target.value,
                          )
                        }
                        placeholder="Service or product description"
                        aria-label={`Line item ${index + 1} description`}
                        className={`w-full rounded-md border px-2 py-1.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
                          getLineError(index, 'description')
                            ? 'border-red-300'
                            : 'border-gray-300'
                        }`}
                      />
                      {getLineError(index, 'description') && (
                        <p className="mt-0.5 text-xs text-red-600">
                          {getLineError(index, 'description')}
                        </p>
                      )}
                    </td>

                    {/* Quantity */}
                    <td className="py-2 pe-2 align-top">
                      <input
                        type="number"
                        min="0.01"
                        step="1"
                        value={item.quantity || ''}
                        onChange={(e: ChangeEvent<HTMLInputElement>) =>
                          handleLineItemChange(
                            item.id,
                            'quantity',
                            parseFloat(e.target.value) || 0,
                          )
                        }
                        aria-label={`Line item ${index + 1} quantity`}
                        className={`w-full rounded-md border px-2 py-1.5 text-end text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
                          getLineError(index, 'quantity')
                            ? 'border-red-300'
                            : 'border-gray-300'
                        }`}
                      />
                      {getLineError(index, 'quantity') && (
                        <p className="mt-0.5 text-xs text-red-600">
                          {getLineError(index, 'quantity')}
                        </p>
                      )}
                    </td>

                    {/* Unit Price */}
                    <td className="py-2 pe-2 align-top">
                      <input
                        type="number"
                        min="0"
                        step="0.01"
                        value={item.unit_price || ''}
                        onChange={(e: ChangeEvent<HTMLInputElement>) =>
                          handleLineItemChange(
                            item.id,
                            'unit_price',
                            parseFloat(e.target.value) || 0,
                          )
                        }
                        aria-label={`Line item ${index + 1} unit price`}
                        className={`w-full rounded-md border px-2 py-1.5 text-end text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
                          getLineError(index, 'unit_price')
                            ? 'border-red-300'
                            : 'border-gray-300'
                        }`}
                      />
                      {getLineError(index, 'unit_price') && (
                        <p className="mt-0.5 text-xs text-red-600">
                          {getLineError(index, 'unit_price')}
                        </p>
                      )}
                    </td>

                    {/* Tax Rate */}
                    <td className="py-2 pe-2 align-top">
                      <input
                        type="number"
                        min="0"
                        max="100"
                        step="0.1"
                        value={item.tax_rate || ''}
                        onChange={(e: ChangeEvent<HTMLInputElement>) =>
                          handleLineItemChange(
                            item.id,
                            'tax_rate',
                            parseFloat(e.target.value) || 0,
                          )
                        }
                        aria-label={`Line item ${index + 1} tax rate`}
                        className="w-full rounded-md border border-gray-300 px-2 py-1.5 text-end text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                      />
                    </td>

                    {/* Calculated Amount */}
                    <td className="py-2 pe-2 text-end align-top font-medium text-gray-900">
                      {formatCurrency(
                        roundToDecimal(
                          item.quantity * item.unit_price,
                          decimals,
                        ),
                        formData.currency,
                      )}
                    </td>

                    {/* Remove button */}
                    <td className="py-2 text-center align-top">
                      <button
                        type="button"
                        onClick={() => handleRemoveLineItem(item.id)}
                        disabled={formData.line_items.length <= 1}
                        className="inline-flex h-8 w-8 items-center justify-center rounded-md text-gray-400 hover:bg-red-50 hover:text-red-600 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500 disabled:cursor-not-allowed disabled:opacity-30"
                        aria-label={`Remove line item ${index + 1}`}
                      >
                        <svg
                          className="h-4 w-4"
                          viewBox="0 0 20 20"
                          fill="currentColor"
                          aria-hidden="true"
                        >
                          <path
                            fillRule="evenodd"
                            d="M8.75 1A2.75 2.75 0 006 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 10.23 1.482l.149-.022.841 10.518A2.75 2.75 0 007.596 19h4.807a2.75 2.75 0 002.742-2.53l.841-10.519.149.023a.75.75 0 00.23-1.482A41.03 41.03 0 0014 4.193V3.75A2.75 2.75 0 0011.25 1h-2.5zM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325C8.327 4.025 9.16 4 10 4zM8.58 7.72a.75.75 0 00-1.5.06l.3 7.5a.75.75 0 101.5-.06l-.3-7.5zm4.34.06a.75.75 0 10-1.5-.06l-.3 7.5a.75.75 0 101.5.06l.3-7.5z"
                            clipRule="evenodd"
                          />
                        </svg>
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Add line item button */}
          <button
            type="button"
            onClick={handleAddLineItem}
            className="mt-4 inline-flex items-center gap-1.5 rounded-md border border-dashed border-gray-300 px-4 py-2 text-sm font-medium text-gray-600 hover:border-blue-400 hover:text-blue-600 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          >
            <svg
              className="h-4 w-4"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
            </svg>
            Add Line Item
          </button>
        </section>

        {/* ── Section 3: Summary / Totals ────────────────────── */}
        <section className="mb-8 rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <h2 className="mb-4 text-lg font-semibold text-gray-900">Summary</h2>

          <div className="flex flex-col items-end">
            <div className="w-full max-w-sm space-y-3">
              {/* Subtotal */}
              <div className="flex items-center justify-between text-sm">
                <span className="text-gray-600">Subtotal</span>
                <span className="font-medium text-gray-900">
                  {formatCurrency(subtotal, formData.currency)}
                </span>
              </div>

              {/* Discount */}
              <div className="flex items-center justify-between gap-3 text-sm">
                <div className="flex items-center gap-2">
                  <span className="text-gray-600">Discount</span>
                  <select
                    value={formData.discount_type}
                    onChange={(e: ChangeEvent<HTMLSelectElement>) =>
                      handleFieldChange(
                        'discount_type',
                        e.target.value as 'percentage' | 'fixed',
                      )
                    }
                    className="rounded border border-gray-300 px-1.5 py-0.5 text-xs focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                    aria-label="Discount type"
                  >
                    <option value="percentage">%</option>
                    <option value="fixed">Fixed</option>
                  </select>
                  <input
                    type="number"
                    min="0"
                    step="0.01"
                    value={formData.discount_value || ''}
                    onChange={(e: ChangeEvent<HTMLInputElement>) =>
                      handleFieldChange(
                        'discount_value',
                        parseFloat(e.target.value) || 0,
                      )
                    }
                    className={`w-20 rounded border px-2 py-0.5 text-end text-xs focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
                      errors.discount_value
                        ? 'border-red-300'
                        : 'border-gray-300'
                    }`}
                    aria-label="Discount value"
                  />
                </div>
                <span className="font-medium text-red-600">
                  {discountAmount > 0
                    ? `- ${formatCurrency(discountAmount, formData.currency)}`
                    : formatCurrency(0, formData.currency)}
                </span>
              </div>
              {errors.discount_value && (
                <p className="text-end text-xs text-red-600">
                  {errors.discount_value}
                </p>
              )}

              {/* Tax */}
              <div className="flex items-center justify-between gap-3 text-sm">
                <div className="flex items-center gap-2">
                  <span className="text-gray-600">Tax</span>
                  <input
                    type="number"
                    min="0"
                    max="100"
                    step="0.1"
                    value={formData.tax_rate || ''}
                    onChange={(e: ChangeEvent<HTMLInputElement>) =>
                      handleFieldChange(
                        'tax_rate',
                        parseFloat(e.target.value) || 0,
                      )
                    }
                    className={`w-16 rounded border px-2 py-0.5 text-end text-xs focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
                      errors.tax_rate ? 'border-red-300' : 'border-gray-300'
                    }`}
                    aria-label="Default tax rate percentage"
                  />
                  <span className="text-xs text-gray-500">%</span>
                </div>
                <span className="font-medium text-gray-900">
                  {taxAmount > 0
                    ? `+ ${formatCurrency(taxAmount, formData.currency)}`
                    : formatCurrency(0, formData.currency)}
                </span>
              </div>
              {errors.tax_rate && (
                <p className="text-end text-xs text-red-600">
                  {errors.tax_rate}
                </p>
              )}

              {/* Total divider */}
              <div className="border-t border-gray-200 pt-3">
                <div className="flex items-center justify-between">
                  <span className="text-base font-semibold text-gray-900">
                    Total
                  </span>
                  <span className="text-xl font-bold text-gray-900">
                    {formatCurrency(totalAmount, formData.currency)}
                  </span>
                </div>
              </div>
            </div>
          </div>
        </section>

        {/* ── Section 4: Notes & Terms ───────────────────────── */}
        <section className="mb-8 rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <h2 className="mb-4 text-lg font-semibold text-gray-900">
            Notes &amp; Terms
          </h2>

          <div className="space-y-4">
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
                value={formData.notes}
                onChange={(e: ChangeEvent<HTMLTextAreaElement>) =>
                  handleFieldChange('notes', e.target.value)
                }
                placeholder="Additional notes for the customer (visible on invoice)"
                className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>

            <div>
              <label
                htmlFor="terms"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Payment Terms
              </label>
              <textarea
                id="terms"
                rows={3}
                value={formData.terms}
                onChange={(e: ChangeEvent<HTMLTextAreaElement>) =>
                  handleFieldChange('terms', e.target.value)
                }
                placeholder="Net 30 — Payment is due within 30 days of the invoice date."
                className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>
          </div>
        </section>
      </DynamicForm>
    </div>
  );
}

export default InvoiceCreate;
