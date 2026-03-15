/**
 * PaymentCreate.tsx — Payment Recording Form Linked to Invoices
 *
 * Records payments against invoices in the Invoicing bounded-context.
 * Payments are transactional records that reduce an invoice's outstanding
 * balance. Uses RDS PostgreSQL for ACID transactions to ensure
 * payment-invoice balance consistency.
 *
 * Replaces the monolith's RecordCreate.cshtml.cs flow:
 *   antiforgery → collect values → validate → RecordManager.CreateRecord()
 *   → post-create hooks → redirect
 */

import { useState, useCallback, useEffect } from 'react';
import { useMutation, useQueryClient, useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import DynamicForm from '../../components/forms/DynamicForm';
import { get, post } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import { formatCurrency } from '../../utils/formatters';

/* ------------------------------------------------------------------ */
/*  TypeScript Interfaces                                              */
/* ------------------------------------------------------------------ */

/** Supported payment methods mirroring the server-side enum. */
type PaymentMethod = 'cash' | 'bank_transfer' | 'credit_card' | 'check' | 'other';

/** Form state for creating a payment record. */
interface PaymentCreateForm {
  invoice_id: string;
  payment_date: string;
  amount: number | '';
  payment_method: PaymentMethod | '';
  reference_number: string;
  notes: string;
}

/** Server response on successful payment creation. */
interface PaymentCreateResponse {
  id: string;
  reference_number: string;
  amount: number;
  invoice_id: string;
  invoice_new_balance: number;
  invoice_new_status: string;
  created_on: string;
}

/** Invoice data returned from the detail API for context display. */
interface InvoiceContext {
  id: string;
  invoice_number: string;
  customer_name: string;
  total_amount: number;
  paid_amount: number;
  balance: number;
  currency: string;
  status: string;
}

/** Invoice search result item for the selector dropdown. */
interface InvoiceSearchItem {
  id: string;
  invoice_number: string;
  customer_name: string;
  balance: number;
  currency: string;
  status: string;
}

/** Per-field validation errors. */
interface ValidationErrors {
  [key: string]: string;
}

/* ------------------------------------------------------------------ */
/*  Payment Method Configuration                                       */
/* ------------------------------------------------------------------ */

const PAYMENT_METHOD_OPTIONS: Array<{ value: PaymentMethod; label: string }> = [
  { value: 'cash', label: 'Cash' },
  { value: 'bank_transfer', label: 'Bank Transfer' },
  { value: 'credit_card', label: 'Credit Card' },
  { value: 'check', label: 'Check' },
  { value: 'other', label: 'Other' },
];

/* ------------------------------------------------------------------ */
/*  Helpers                                                            */
/* ------------------------------------------------------------------ */

/** Returns today's date as an ISO string (YYYY-MM-DD). */
function getTodayISO(): string {
  const d = new Date();
  const year = d.getFullYear();
  const month = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/**
 * Client-side validation for the payment form.
 * Mirrors the monolith's ValidateRecordSubmission() and RecordManager
 * permission / constraint checks.
 */
function validatePayment(
  data: PaymentCreateForm,
  outstandingBalance: number,
): ValidationErrors {
  const errors: ValidationErrors = {};

  if (!data.invoice_id) {
    errors.invoice_id = 'Invoice is required';
  }
  if (!data.payment_date) {
    errors.payment_date = 'Payment date is required';
  }
  if (data.amount === '' || data.amount === undefined || data.amount === null) {
    errors.amount = 'Amount is required';
  } else if (typeof data.amount === 'number' && data.amount <= 0) {
    errors.amount = 'Amount must be greater than 0';
  } else if (typeof data.amount === 'number' && data.amount > outstandingBalance) {
    errors.amount = `Amount exceeds outstanding balance of ${formatCurrency(outstandingBalance)}`;
  }
  if (!data.payment_method) {
    errors.payment_method = 'Payment method is required';
  }

  return errors;
}

/* ------------------------------------------------------------------ */
/*  PaymentCreate Component                                            */
/* ------------------------------------------------------------------ */

/**
 * Payment recording form linked to a specific invoice.
 *
 * Supports two entry paths:
 * 1. URL parameter `?invoiceId=<id>` — pre-populates invoice context
 * 2. Manual selection from a searchable dropdown of unpaid invoices
 *
 * The form collects payment details and submits via
 * `POST /v1/invoicing/payments`. On success it invalidates relevant
 * TanStack Query caches and navigates to the newly created payment.
 */
function PaymentCreate(): React.JSX.Element {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const queryClient = useQueryClient();

  /* ---- URL-driven invoice pre-selection ------------------------------ */
  const invoiceIdFromUrl = searchParams.get('invoiceId') ?? '';
  const isPreSelected = invoiceIdFromUrl.length > 0;

  /* ---- Form State ---------------------------------------------------- */
  const [formData, setFormData] = useState<PaymentCreateForm>({
    invoice_id: invoiceIdFromUrl,
    payment_date: getTodayISO(),
    amount: '',
    payment_method: '',
    reference_number: '',
    notes: '',
  });

  const [errors, setErrors] = useState<ValidationErrors>({});
  const [invoiceSearch, setInvoiceSearch] = useState<string>('');
  const [isDirty, setIsDirty] = useState<boolean>(false);
  const [showInvoiceDropdown, setShowInvoiceDropdown] = useState<boolean>(false);
  const [submitError, setSubmitError] = useState<string>('');

  /* ---- Derived invoice ID (either from URL or selector) -------------- */
  const activeInvoiceId = formData.invoice_id;

  /* ---- Invoice Detail Query (for context panel) ---------------------- */
  const {
    data: invoiceResponse,
    isLoading: isLoadingInvoice,
    isError: isInvoiceError,
  } = useQuery<ApiResponse<InvoiceContext>>({
    queryKey: ['invoicing', 'invoices', activeInvoiceId],
    queryFn: () => get<InvoiceContext>(`/v1/invoicing/invoices/${activeInvoiceId}`),
    enabled: activeInvoiceId.length > 0,
    staleTime: 30_000,
  });

  const invoiceCtx: InvoiceContext | null = invoiceResponse?.object ?? null;
  const outstandingBalance: number = invoiceCtx?.balance ?? 0;

  /* ---- Invoice Search Query (for selector dropdown) ------------------ */
  const { data: invoiceSearchResponse, isLoading: isSearching } =
    useQuery<ApiResponse<InvoiceSearchItem[]>>({
      queryKey: ['invoicing', 'invoices', 'search', invoiceSearch],
      queryFn: () =>
        get<InvoiceSearchItem[]>(
          `/v1/invoicing/invoices?status=sent,partial&pageSize=20&search=${encodeURIComponent(invoiceSearch)}`,
        ),
      enabled: !isPreSelected && invoiceSearch.length >= 0,
      staleTime: 15_000,
    });

  const invoiceSearchResults: InvoiceSearchItem[] =
    invoiceSearchResponse?.object ?? [];

  /* ---- Payment Creation Mutation ------------------------------------- */
  const createPaymentMutation = useMutation<
    ApiResponse<PaymentCreateResponse>,
    ApiError,
    PaymentCreateForm
  >({
    mutationFn: (payload) =>
      post<PaymentCreateResponse>('/v1/invoicing/payments', {
        invoice_id: payload.invoice_id,
        payment_date: payload.payment_date,
        amount: typeof payload.amount === 'number' ? payload.amount : 0,
        payment_method: payload.payment_method,
        reference_number: payload.reference_number,
        notes: payload.notes,
      }),
    onSuccess: (response) => {
      setIsDirty(false);
      /* Invalidate all relevant caches */
      queryClient.invalidateQueries({ queryKey: ['invoicing', 'payments'] });
      queryClient.invalidateQueries({ queryKey: ['invoicing', 'invoices'] });
      if (activeInvoiceId) {
        queryClient.invalidateQueries({
          queryKey: ['invoicing', 'invoices', activeInvoiceId],
        });
      }
      /* Navigate to the newly created payment detail */
      const newPaymentId = response?.object?.id;
      if (newPaymentId) {
        navigate(`/invoicing/payments/${newPaymentId}`);
      } else {
        navigate('/invoicing/payments');
      }
    },
    onError: (error: ApiError) => {
      /* Handle 422 validation errors from server */
      if (error && typeof error === 'object' && 'errors' in error) {
        const serverErrors: ValidationErrors = {};
        const errList = (error as ApiError).errors;
        if (Array.isArray(errList)) {
          for (const err of errList) {
            if (err && typeof err === 'object' && 'key' in err && 'message' in err) {
              const errObj = err as { key: string; message: string };
              serverErrors[errObj.key] = errObj.message;
            } else if (err && typeof err === 'object' && 'message' in err) {
              setSubmitError((err as { message: string }).message);
            }
          }
        }
        if (Object.keys(serverErrors).length > 0) {
          setErrors((prev) => ({ ...prev, ...serverErrors }));
        }
      } else if (error && typeof error === 'object' && 'message' in error) {
        setSubmitError((error as { message: string }).message);
      } else {
        setSubmitError('An unexpected error occurred. Please try again.');
      }
    },
  });

  /* ---- Pre-populate amount when invoice loads ------------------------ */
  useEffect(() => {
    if (invoiceCtx && formData.amount === '' && outstandingBalance > 0) {
      setFormData((prev) => ({ ...prev, amount: outstandingBalance }));
    }
  }, [invoiceCtx, outstandingBalance, formData.amount]);

  /* ---- Unsaved changes confirmation --------------------------------- */
  useEffect(() => {
    function handleBeforeUnload(event: BeforeUnloadEvent) {
      if (isDirty) {
        event.preventDefault();
      }
    }
    window.addEventListener('beforeunload', handleBeforeUnload);
    return () => {
      window.removeEventListener('beforeunload', handleBeforeUnload);
    };
  }, [isDirty]);

  /* ---- Form field update handler ------------------------------------ */
  const updateField = useCallback(
    <K extends keyof PaymentCreateForm>(field: K, value: PaymentCreateForm[K]) => {
      setFormData((prev) => ({ ...prev, [field]: value }));
      setIsDirty(true);
      /* Clear field-level error on edit */
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

  /* ---- Invoice selection from dropdown ------------------------------ */
  const handleInvoiceSelect = useCallback(
    (invoice: InvoiceSearchItem) => {
      updateField('invoice_id', invoice.id);
      setInvoiceSearch(invoice.invoice_number);
      setShowInvoiceDropdown(false);
    },
    [updateField],
  );

  /* ---- Clear selected invoice --------------------------------------- */
  const handleClearInvoice = useCallback(() => {
    setFormData((prev) => ({ ...prev, invoice_id: '', amount: '' }));
    setInvoiceSearch('');
    setShowInvoiceDropdown(false);
    setIsDirty(true);
  }, []);

  /* ---- Validation --------------------------------------------------- */
  const runValidation = useCallback((): boolean => {
    const validationErrors = validatePayment(formData, outstandingBalance);
    setErrors(validationErrors);
    return Object.keys(validationErrors).length === 0;
  }, [formData, outstandingBalance]);

  /* ---- Form submission ---------------------------------------------- */
  const handleSubmit = useCallback(
    (event?: React.FormEvent) => {
      if (event) {
        event.preventDefault();
      }
      setSubmitError('');
      if (!runValidation()) {
        return;
      }
      createPaymentMutation.mutate(formData);
    },
    [runValidation, createPaymentMutation, formData],
  );

  /* ---- Cancel / navigate back --------------------------------------- */
  const handleCancel = useCallback(() => {
    if (isDirty) {
      const confirmed = window.confirm(
        'You have unsaved changes. Are you sure you want to leave?',
      );
      if (!confirmed) return;
    }
    if (activeInvoiceId) {
      navigate(`/invoicing/invoices/${activeInvoiceId}`);
    } else {
      navigate('/invoicing/payments');
    }
  }, [isDirty, navigate, activeInvoiceId]);

  /* ---- Computed values for payment preview --------------------------- */
  const paymentAmount: number =
    typeof formData.amount === 'number' ? formData.amount : 0;
  const remainingBalance: number = outstandingBalance - paymentAmount;
  const isFullPayment: boolean =
    paymentAmount > 0 && paymentAmount === outstandingBalance;
  const isOverpayment: boolean = paymentAmount > outstandingBalance;
  const currencyCode: string = invoiceCtx?.currency ?? 'USD';
  const isSubmitting: boolean = createPaymentMutation.isPending;

  /* ================================================================== */
  /*  RENDER                                                             */
  /* ================================================================== */

  return (
    <div className="mx-auto max-w-4xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ---- Page Header -------------------------------------------- */}
      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={handleCancel}
            className="inline-flex items-center rounded-md p-2 text-gray-500 hover:bg-gray-100 hover:text-gray-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
            aria-label="Go back"
          >
            <svg
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-5 w-5"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M17 10a.75.75 0 01-.75.75H5.612l4.158 3.96a.75.75 0 11-1.04 1.08l-5.5-5.25a.75.75 0 010-1.08l5.5-5.25a.75.75 0 111.04 1.08L5.612 9.25H16.25A.75.75 0 0117 10z"
                clipRule="evenodd"
              />
            </svg>
          </button>
          <h1 className="text-2xl font-bold text-gray-900">Record Payment</h1>
        </div>
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={handleCancel}
            className="rounded-md bg-white px-4 py-2 text-sm font-semibold text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          >
            Cancel
          </button>
          <button
            type="submit"
            form="payment-create-form"
            disabled={isSubmitting || !activeInvoiceId}
            className="inline-flex items-center gap-2 rounded-md bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isSubmitting && (
              <svg
                className="h-4 w-4 animate-spin"
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
            )}
            {isSubmitting ? 'Recording…' : 'Record Payment'}
          </button>
        </div>
      </div>

      {/* ---- Global submit error banner ------------------------------ */}
      {submitError && (
        <div
          className="mb-6 rounded-md bg-red-50 p-4"
          role="alert"
        >
          <div className="flex">
            <svg
              className="h-5 w-5 flex-shrink-0 text-red-400"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
                clipRule="evenodd"
              />
            </svg>
            <p className="ms-3 text-sm text-red-700">{submitError}</p>
          </div>
        </div>
      )}

      {/* ---- Invoice Context Panel ----------------------------------- */}
      {invoiceCtx && (
        <div className="mb-6 rounded-lg border border-blue-200 bg-blue-50 p-5">
          <h2 className="mb-3 text-sm font-semibold text-blue-800">
            Invoice Summary
          </h2>
          <div className="grid grid-cols-2 gap-x-6 gap-y-3 sm:grid-cols-3">
            <div>
              <p className="text-xs font-medium text-blue-600">Invoice #</p>
              <p className="text-sm font-semibold text-blue-900">
                {invoiceCtx.invoice_number}
              </p>
            </div>
            <div>
              <p className="text-xs font-medium text-blue-600">Customer</p>
              <p className="text-sm font-semibold text-blue-900">
                {invoiceCtx.customer_name}
              </p>
            </div>
            <div>
              <p className="text-xs font-medium text-blue-600">Currency</p>
              <p className="text-sm font-semibold text-blue-900">
                {invoiceCtx.currency}
              </p>
            </div>
            <div>
              <p className="text-xs font-medium text-blue-600">Total Amount</p>
              <p className="text-sm text-blue-900">
                {formatCurrency(invoiceCtx.total_amount, currencyCode)}
              </p>
            </div>
            <div>
              <p className="text-xs font-medium text-blue-600">Amount Paid</p>
              <p className="text-sm text-blue-900">
                {formatCurrency(invoiceCtx.paid_amount, currencyCode)}
              </p>
            </div>
            <div>
              <p className="text-xs font-medium text-blue-600">
                Outstanding Balance
              </p>
              <p className="text-lg font-bold text-blue-900">
                {formatCurrency(outstandingBalance, currencyCode)}
              </p>
            </div>
          </div>
        </div>
      )}

      {/* ---- Loading / Error states for invoice fetch ----------------- */}
      {isLoadingInvoice && activeInvoiceId && (
        <div className="mb-6 flex items-center gap-2 rounded-md bg-gray-50 p-4 text-sm text-gray-600">
          <svg
            className="h-4 w-4 animate-spin"
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
          Loading invoice details…
        </div>
      )}
      {isInvoiceError && activeInvoiceId && (
        <div
          className="mb-6 rounded-md bg-red-50 p-4 text-sm text-red-700"
          role="alert"
        >
          Failed to load invoice details. Please check the invoice ID and try again.
        </div>
      )}

      {/* ---- Form ---------------------------------------------------- */}
      <DynamicForm
        id="payment-create-form"
        name="payment-create-form"
        labelMode="stacked"
        fieldMode="form"
        onSubmit={handleSubmit}
      >
        <div className="space-y-8">
          {/* ---- Section 1: Invoice Selection (hidden if pre-selected) -- */}
          {!isPreSelected && (
            <section>
              <h2 className="mb-4 text-lg font-semibold text-gray-900">
                Select Invoice
              </h2>
              <div className="relative">
                <label
                  htmlFor="invoice-search"
                  className="mb-1 block text-sm font-medium text-gray-700"
                >
                  Invoice <span className="text-red-500">*</span>
                </label>
                {/* Selected invoice chip */}
                {activeInvoiceId && invoiceCtx ? (
                  <div className="flex items-center gap-2 rounded-md border border-gray-300 bg-gray-50 px-3 py-2">
                    <span className="flex-1 text-sm text-gray-900">
                      {invoiceCtx.invoice_number} — {invoiceCtx.customer_name}{' '}
                      ({formatCurrency(outstandingBalance, currencyCode)} balance)
                    </span>
                    <button
                      type="button"
                      onClick={handleClearInvoice}
                      className="rounded p-1 text-gray-400 hover:bg-gray-200 hover:text-gray-600 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                      aria-label="Clear invoice selection"
                    >
                      <svg
                        viewBox="0 0 20 20"
                        fill="currentColor"
                        className="h-4 w-4"
                        aria-hidden="true"
                      >
                        <path d="M6.28 5.22a.75.75 0 00-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 101.06 1.06L10 11.06l3.72 3.72a.75.75 0 101.06-1.06L11.06 10l3.72-3.72a.75.75 0 00-1.06-1.06L10 8.94 6.28 5.22z" />
                      </svg>
                    </button>
                  </div>
                ) : (
                  /* Search input */
                  <div>
                    <input
                      id="invoice-search"
                      type="text"
                      value={invoiceSearch}
                      onChange={(e) => {
                        setInvoiceSearch(e.target.value);
                        setShowInvoiceDropdown(true);
                        setIsDirty(true);
                      }}
                      onFocus={() => setShowInvoiceDropdown(true)}
                      placeholder="Search by invoice number or customer…"
                      className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-indigo-600 ${
                        errors.invoice_id
                          ? 'border-red-300 text-red-900 focus:ring-red-500'
                          : 'border-gray-300 text-gray-900'
                      }`}
                      autoComplete="off"
                      aria-describedby={
                        errors.invoice_id ? 'invoice-error' : undefined
                      }
                      aria-invalid={errors.invoice_id ? 'true' : undefined}
                    />
                    {/* Dropdown results */}
                    {showInvoiceDropdown && (
                      <ul
                        className="absolute z-10 mt-1 max-h-60 w-full overflow-auto rounded-md border border-gray-200 bg-white py-1 shadow-lg"
                        role="listbox"
                        aria-label="Invoice search results"
                      >
                        {isSearching && (
                          <li className="px-3 py-2 text-sm text-gray-500">
                            Searching…
                          </li>
                        )}
                        {!isSearching && invoiceSearchResults.length === 0 && (
                          <li className="px-3 py-2 text-sm text-gray-500">
                            No unpaid invoices found.
                          </li>
                        )}
                        {invoiceSearchResults.map((inv) => (
                          <li
                            key={inv.id}
                            role="option"
                            aria-selected={false}
                            className="cursor-pointer px-3 py-2 text-sm hover:bg-indigo-50"
                            onClick={() => handleInvoiceSelect(inv)}
                            onKeyDown={(e) => {
                              if (e.key === 'Enter' || e.key === ' ') {
                                e.preventDefault();
                                handleInvoiceSelect(inv);
                              }
                            }}
                            tabIndex={0}
                          >
                            <span className="font-medium text-gray-900">
                              {inv.invoice_number}
                            </span>
                            <span className="ms-2 text-gray-600">
                              — {inv.customer_name}
                            </span>
                            <span className="ms-2 font-semibold text-indigo-700">
                              {formatCurrency(inv.balance, inv.currency)}
                            </span>
                            <span className="ms-2 text-xs uppercase text-gray-400">
                              {inv.status}
                            </span>
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>
                )}
                {errors.invoice_id && (
                  <p
                    id="invoice-error"
                    className="mt-1 text-sm text-red-600"
                    role="alert"
                  >
                    {errors.invoice_id}
                  </p>
                )}
              </div>
            </section>
          )}

          {/* ---- Section 2: Payment Details ----------------------------- */}
          <section>
            <h2 className="mb-4 text-lg font-semibold text-gray-900">
              Payment Details
            </h2>
            <div className="grid grid-cols-1 gap-x-6 gap-y-5 sm:grid-cols-2">
              {/* Payment Date */}
              <div>
                <label
                  htmlFor="payment-date"
                  className="mb-1 block text-sm font-medium text-gray-700"
                >
                  Payment Date <span className="text-red-500">*</span>
                </label>
                <input
                  id="payment-date"
                  type="date"
                  value={formData.payment_date}
                  onChange={(e) => updateField('payment_date', e.target.value)}
                  className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-indigo-600 ${
                    errors.payment_date
                      ? 'border-red-300 text-red-900 focus:ring-red-500'
                      : 'border-gray-300 text-gray-900'
                  }`}
                  aria-describedby={
                    errors.payment_date ? 'payment-date-error' : undefined
                  }
                  aria-invalid={errors.payment_date ? 'true' : undefined}
                />
                {errors.payment_date && (
                  <p
                    id="payment-date-error"
                    className="mt-1 text-sm text-red-600"
                    role="alert"
                  >
                    {errors.payment_date}
                  </p>
                )}
              </div>

              {/* Payment Method */}
              <div>
                <label
                  htmlFor="payment-method"
                  className="mb-1 block text-sm font-medium text-gray-700"
                >
                  Payment Method <span className="text-red-500">*</span>
                </label>
                <select
                  id="payment-method"
                  value={formData.payment_method}
                  onChange={(e) =>
                    updateField(
                      'payment_method',
                      e.target.value as PaymentMethod | '',
                    )
                  }
                  className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-indigo-600 ${
                    errors.payment_method
                      ? 'border-red-300 text-red-900 focus:ring-red-500'
                      : 'border-gray-300 text-gray-900'
                  }`}
                  aria-describedby={
                    errors.payment_method ? 'payment-method-error' : undefined
                  }
                  aria-invalid={errors.payment_method ? 'true' : undefined}
                >
                  <option value="">Select method…</option>
                  {PAYMENT_METHOD_OPTIONS.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
                {errors.payment_method && (
                  <p
                    id="payment-method-error"
                    className="mt-1 text-sm text-red-600"
                    role="alert"
                  >
                    {errors.payment_method}
                  </p>
                )}
              </div>

              {/* Amount */}
              <div>
                <label
                  htmlFor="payment-amount"
                  className="mb-1 block text-sm font-medium text-gray-700"
                >
                  Amount <span className="text-red-500">*</span>
                </label>
                <div className="relative">
                  <span className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3 text-sm text-gray-500">
                    {invoiceCtx?.currency ?? '$'}
                  </span>
                  <input
                    id="payment-amount"
                    type="number"
                    step="0.01"
                    min="0"
                    value={formData.amount === '' ? '' : formData.amount}
                    onChange={(e) => {
                      const raw = e.target.value;
                      if (raw === '') {
                        updateField('amount', '');
                      } else {
                        const parsed = parseFloat(raw);
                        if (!Number.isNaN(parsed)) {
                          updateField('amount', parsed);
                        }
                      }
                    }}
                    className={`block w-full rounded-md border py-2 pe-3 ps-10 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-indigo-600 ${
                      errors.amount
                        ? 'border-red-300 text-red-900 focus:ring-red-500'
                        : 'border-gray-300 text-gray-900'
                    }`}
                    placeholder="0.00"
                    aria-describedby={
                      errors.amount ? 'amount-error' : undefined
                    }
                    aria-invalid={errors.amount ? 'true' : undefined}
                  />
                </div>
                {errors.amount && (
                  <p
                    id="amount-error"
                    className="mt-1 text-sm text-red-600"
                    role="alert"
                  >
                    {errors.amount}
                  </p>
                )}
                {/* Overpayment warning */}
                {isOverpayment && !errors.amount && (
                  <p className="mt-1 text-sm text-amber-600" role="status">
                    ⚠ This amount exceeds the outstanding balance.
                  </p>
                )}
              </div>

              {/* Reference Number */}
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
                  value={formData.reference_number}
                  onChange={(e) =>
                    updateField('reference_number', e.target.value)
                  }
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-indigo-600"
                  placeholder="Check #, transaction ID, etc."
                />
              </div>

              {/* Notes — full width */}
              <div className="sm:col-span-2">
                <label
                  htmlFor="payment-notes"
                  className="mb-1 block text-sm font-medium text-gray-700"
                >
                  Notes
                </label>
                <textarea
                  id="payment-notes"
                  rows={3}
                  value={formData.notes}
                  onChange={(e) => updateField('notes', e.target.value)}
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-indigo-600"
                  placeholder="Optional payment notes…"
                />
              </div>
            </div>
          </section>

          {/* ---- Section 3: Payment Preview ----------------------------- */}
          {invoiceCtx && paymentAmount > 0 && (
            <section>
              <h2 className="mb-4 text-lg font-semibold text-gray-900">
                Payment Preview
              </h2>
              <div className="rounded-lg border border-gray-200 bg-gray-50 p-5">
                <div className="flex flex-wrap items-center justify-between gap-4">
                  {/* Current Balance */}
                  <div className="text-center">
                    <p className="text-xs font-medium uppercase tracking-wide text-gray-500">
                      Current Balance
                    </p>
                    <p className="mt-1 text-lg font-semibold text-gray-900">
                      {formatCurrency(outstandingBalance, currencyCode)}
                    </p>
                  </div>

                  {/* Arrow separator */}
                  <div className="text-gray-400" aria-hidden="true">
                    <svg
                      viewBox="0 0 20 20"
                      fill="currentColor"
                      className="h-5 w-5"
                    >
                      <path
                        fillRule="evenodd"
                        d="M2 10a.75.75 0 01.75-.75h12.59l-2.1-1.95a.75.75 0 111.02-1.1l3.5 3.25a.75.75 0 010 1.1l-3.5 3.25a.75.75 0 11-1.02-1.1l2.1-1.95H2.75A.75.75 0 012 10z"
                        clipRule="evenodd"
                      />
                    </svg>
                  </div>

                  {/* Payment Amount */}
                  <div className="text-center">
                    <p className="text-xs font-medium uppercase tracking-wide text-gray-500">
                      Payment
                    </p>
                    <p className="mt-1 text-lg font-semibold text-indigo-700">
                      − {formatCurrency(paymentAmount, currencyCode)}
                    </p>
                  </div>

                  {/* Arrow separator */}
                  <div className="text-gray-400" aria-hidden="true">
                    <svg
                      viewBox="0 0 20 20"
                      fill="currentColor"
                      className="h-5 w-5"
                    >
                      <path
                        fillRule="evenodd"
                        d="M2 10a.75.75 0 01.75-.75h12.59l-2.1-1.95a.75.75 0 111.02-1.1l3.5 3.25a.75.75 0 010 1.1l-3.5 3.25a.75.75 0 11-1.02-1.1l2.1-1.95H2.75A.75.75 0 012 10z"
                        clipRule="evenodd"
                      />
                    </svg>
                  </div>

                  {/* Remaining Balance */}
                  <div className="text-center">
                    <p className="text-xs font-medium uppercase tracking-wide text-gray-500">
                      Remaining
                    </p>
                    <p
                      className={`mt-1 text-lg font-bold ${
                        remainingBalance <= 0
                          ? 'text-green-700'
                          : 'text-gray-900'
                      }`}
                    >
                      {formatCurrency(
                        Math.max(remainingBalance, 0),
                        currencyCode,
                      )}
                    </p>
                  </div>
                </div>

                {/* Full payment badge */}
                {isFullPayment && (
                  <div className="mt-4 flex items-center justify-center gap-2 rounded-md bg-green-100 px-3 py-2 text-sm font-medium text-green-800">
                    <svg
                      viewBox="0 0 20 20"
                      fill="currentColor"
                      className="h-4 w-4"
                      aria-hidden="true"
                    >
                      <path
                        fillRule="evenodd"
                        d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z"
                        clipRule="evenodd"
                      />
                    </svg>
                    This will fully pay the invoice
                  </div>
                )}
              </div>
            </section>
          )}
        </div>
      </DynamicForm>
    </div>
  );
}

export default PaymentCreate;
