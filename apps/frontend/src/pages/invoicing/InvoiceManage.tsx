import React, { useState, useEffect, useCallback, type FormEvent } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams, useNavigate } from 'react-router-dom';
import { get, put, type ApiResponse, type ApiError } from '../../api/client';
import DynamicForm, { type FormValidation, type ValidationError } from '../../components/forms/DynamicForm';

// ---------------------------------------------------------------------------
// Type definitions
// ---------------------------------------------------------------------------

/** Possible invoice statuses. */
type InvoiceStatus = 'draft' | 'sent' | 'partial' | 'paid' | 'void' | 'overdue';

/** A single line item on an invoice. */
interface InvoiceLineItem {
  id?: string;
  description: string;
  quantity: number;
  unit_price: number;
  tax_rate: number;
  amount: number;
}

/** Controlled-input form state for a line item (all strings for native inputs). */
interface LineItemFormState {
  id: string;
  description: string;
  quantity: string;
  unit_price: string;
  tax_rate: string;
}

/** Invoice header form state — all values stored as strings for controlled inputs. */
interface InvoiceHeaderFormState {
  customer_id: string;
  issue_date: string;
  due_date: string;
  currency: string;
  notes: string;
  terms: string;
  tax_rate: string;
  discount_type: 'percentage' | 'fixed';
  discount_value: string;
}

/** Complete form state combining header + line items. */
interface InvoiceEditFormState {
  header: InvoiceHeaderFormState;
  line_items: LineItemFormState[];
}

/** Payload sent to PUT /v1/invoicing/invoices/:id. */
interface InvoiceUpdatePayload {
  id: string;
  customer_id: string;
  issue_date: string;
  due_date: string;
  currency: string;
  tax_rate: number;
  discount_type: 'percentage' | 'fixed';
  discount_value: number;
  notes: string;
  terms: string;
  line_items: InvoiceLineItem[];
}

/** Full invoice data returned from the API including read-only fields. */
interface InvoiceData {
  id: string;
  invoice_number: string;
  customer_id: string;
  customer_name?: string;
  status: InvoiceStatus;
  issue_date: string;
  due_date: string;
  currency: string;
  tax_rate: number;
  discount_type: 'percentage' | 'fixed';
  discount_value: number;
  subtotal: number;
  tax_amount: number;
  discount_amount: number;
  total: number;
  paid_amount: number;
  balance_due: number;
  notes: string;
  terms: string;
  line_items: InvoiceLineItem[];
  created_on: string;
  updated_on: string;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const CURRENCY_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'USD', label: 'USD — US Dollar' },
  { value: 'EUR', label: 'EUR — Euro' },
  { value: 'GBP', label: 'GBP — British Pound' },
  { value: 'BGN', label: 'BGN — Bulgarian Lev' },
  { value: 'CAD', label: 'CAD — Canadian Dollar' },
  { value: 'AUD', label: 'AUD — Australian Dollar' },
  { value: 'JPY', label: 'JPY — Japanese Yen' },
  { value: 'CHF', label: 'CHF — Swiss Franc' },
];

const DISCOUNT_TYPE_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'percentage', label: 'Percentage (%)' },
  { value: 'fixed', label: 'Fixed Amount' },
];

/** Statuses that cannot be edited — redirect to detail view. */
const NON_EDITABLE_STATUSES: ReadonlyArray<InvoiceStatus> = ['paid', 'void'];

/** Statuses where customer and currency are locked. */
const LOCKED_FIELD_STATUSES: ReadonlyArray<InvoiceStatus> = ['sent', 'partial', 'overdue'];

const INITIAL_HEADER: InvoiceHeaderFormState = {
  customer_id: '',
  issue_date: '',
  due_date: '',
  currency: 'USD',
  notes: '',
  terms: '',
  tax_rate: '0',
  discount_type: 'percentage',
  discount_value: '0',
};

const EMPTY_LINE_ITEM: LineItemFormState = {
  id: '',
  description: '',
  quantity: '1',
  unit_price: '0',
  tax_rate: '0',
};

// ---------------------------------------------------------------------------
// Pure helpers
// ---------------------------------------------------------------------------

/** Generate a temporary client-side ID for new line items. */
let tempIdCounter = 0;
function nextTempId(): string {
  tempIdCounter += 1;
  return `_new_${tempIdCounter}_${Date.now()}`;
}

/** Round a number to two decimal places (financial). */
function round2(value: number): number {
  return Math.round((value + Number.EPSILON) * 100) / 100;
}

/** Calculate the amount for a single line item. */
function calculateLineAmount(qty: number, price: number, lineTax: number): number {
  const base = round2(qty * price);
  const tax = round2(base * (lineTax / 100));
  return round2(base + tax);
}

/** Calculate all invoice totals from line items + header-level tax/discount. */
function calculateTotals(
  items: LineItemFormState[],
  headerTaxRate: string,
  discountType: 'percentage' | 'fixed',
  discountValue: string,
): { subtotal: number; taxAmount: number; discountAmount: number; total: number } {
  const subtotal = items.reduce((sum, item) => {
    const qty = parseFloat(item.quantity) || 0;
    const price = parseFloat(item.unit_price) || 0;
    return sum + round2(qty * price);
  }, 0);

  const tax = parseFloat(headerTaxRate) || 0;
  const taxAmount = round2(subtotal * (tax / 100));
  const afterTax = round2(subtotal + taxAmount);

  const discVal = parseFloat(discountValue) || 0;
  let discountAmount: number;
  if (discountType === 'percentage') {
    discountAmount = round2(afterTax * (discVal / 100));
  } else {
    discountAmount = round2(Math.min(discVal, afterTax));
  }

  const total = round2(afterTax - discountAmount);
  return { subtotal: round2(subtotal), taxAmount, discountAmount, total: Math.max(total, 0) };
}

/** Map API response to controlled-input form state. */
function invoiceToFormState(invoice: InvoiceData): InvoiceEditFormState {
  return {
    header: {
      customer_id: invoice.customer_id ?? '',
      issue_date: invoice.issue_date ?? '',
      due_date: invoice.due_date ?? '',
      currency: invoice.currency ?? 'USD',
      notes: invoice.notes ?? '',
      terms: invoice.terms ?? '',
      tax_rate: invoice.tax_rate != null ? String(invoice.tax_rate) : '0',
      discount_type: invoice.discount_type ?? 'percentage',
      discount_value: invoice.discount_value != null ? String(invoice.discount_value) : '0',
    },
    line_items: (invoice.line_items ?? []).map((li) => ({
      id: li.id ?? nextTempId(),
      description: li.description ?? '',
      quantity: li.quantity != null ? String(li.quantity) : '1',
      unit_price: li.unit_price != null ? String(li.unit_price) : '0',
      tax_rate: li.tax_rate != null ? String(li.tax_rate) : '0',
    })),
  };
}

/** Convert form state to the typed update payload. */
function formStateToPayload(id: string, state: InvoiceEditFormState): InvoiceUpdatePayload {
  return {
    id,
    customer_id: state.header.customer_id.trim(),
    issue_date: state.header.issue_date,
    due_date: state.header.due_date,
    currency: state.header.currency,
    tax_rate: parseFloat(state.header.tax_rate) || 0,
    discount_type: state.header.discount_type,
    discount_value: parseFloat(state.header.discount_value) || 0,
    notes: state.header.notes.trim(),
    terms: state.header.terms.trim(),
    line_items: state.line_items.map((li) => {
      const qty = parseFloat(li.quantity) || 0;
      const price = parseFloat(li.unit_price) || 0;
      const lineTax = parseFloat(li.tax_rate) || 0;
      return {
        id: li.id.startsWith('_new_') ? undefined : li.id,
        description: li.description.trim(),
        quantity: qty,
        unit_price: price,
        tax_rate: lineTax,
        amount: calculateLineAmount(qty, price, lineTax),
      };
    }),
  };
}

/** Client-side validation for the invoice edit form. */
function validateInvoiceUpdate(
  state: InvoiceEditFormState,
  paidAmount: number,
): ValidationError[] {
  const errors: ValidationError[] = [];
  const { header, line_items } = state;

  if (!header.customer_id.trim()) {
    errors.push({ propertyName: 'customer_id', message: 'Customer is required.' });
  }
  if (!header.issue_date) {
    errors.push({ propertyName: 'issue_date', message: 'Issue date is required.' });
  }
  if (!header.due_date) {
    errors.push({ propertyName: 'due_date', message: 'Due date is required.' });
  }
  if (header.issue_date && header.due_date && header.due_date < header.issue_date) {
    errors.push({ propertyName: 'due_date', message: 'Due date must be on or after the issue date.' });
  }
  if (!header.currency) {
    errors.push({ propertyName: 'currency', message: 'Currency is required.' });
  }

  const taxRate = parseFloat(header.tax_rate);
  if (header.tax_rate.trim() !== '' && (Number.isNaN(taxRate) || taxRate < 0 || taxRate > 100)) {
    errors.push({ propertyName: 'tax_rate', message: 'Tax rate must be between 0 and 100.' });
  }

  const discVal = parseFloat(header.discount_value);
  if (header.discount_value.trim() !== '' && (Number.isNaN(discVal) || discVal < 0)) {
    errors.push({ propertyName: 'discount_value', message: 'Discount value must be non-negative.' });
  }
  if (header.discount_type === 'percentage' && discVal > 100) {
    errors.push({ propertyName: 'discount_value', message: 'Percentage discount cannot exceed 100%.' });
  }

  if (line_items.length === 0) {
    errors.push({ propertyName: 'line_items', message: 'At least one line item is required.' });
  }

  line_items.forEach((li, idx) => {
    if (!li.description.trim()) {
      errors.push({ propertyName: `line_items[${idx}].description`, message: `Line ${idx + 1}: Description is required.` });
    }
    const qty = parseFloat(li.quantity);
    if (Number.isNaN(qty) || qty <= 0) {
      errors.push({ propertyName: `line_items[${idx}].quantity`, message: `Line ${idx + 1}: Quantity must be greater than zero.` });
    }
    const price = parseFloat(li.unit_price);
    if (Number.isNaN(price) || price < 0) {
      errors.push({ propertyName: `line_items[${idx}].unit_price`, message: `Line ${idx + 1}: Unit price must be non-negative.` });
    }
    const lt = parseFloat(li.tax_rate);
    if (li.tax_rate.trim() !== '' && (Number.isNaN(lt) || lt < 0 || lt > 100)) {
      errors.push({ propertyName: `line_items[${idx}].tax_rate`, message: `Line ${idx + 1}: Tax rate must be between 0 and 100.` });
    }
  });

  // Paid amount guard — cannot reduce total below what has already been paid
  if (paidAmount > 0 && errors.length === 0) {
    const totals = calculateTotals(line_items, header.tax_rate, header.discount_type, header.discount_value);
    if (totals.total < paidAmount) {
      errors.push({
        propertyName: '_form',
        message: `Total ($${totals.total.toFixed(2)}) cannot be less than the already paid amount ($${paidAmount.toFixed(2)}).`,
      });
    }
  }

  return errors;
}

/** Deep equality check for form state to detect dirty state. */
function isFormStateDirty(current: InvoiceEditFormState, original: InvoiceEditFormState): boolean {
  // Compare header fields
  const hKeys = Object.keys(current.header) as Array<keyof InvoiceHeaderFormState>;
  for (const key of hKeys) {
    if (current.header[key] !== original.header[key]) return true;
  }
  // Compare line items
  if (current.line_items.length !== original.line_items.length) return true;
  for (let i = 0; i < current.line_items.length; i++) {
    const c = current.line_items[i];
    const o = original.line_items[i];
    if (c.description !== o.description || c.quantity !== o.quantity ||
        c.unit_price !== o.unit_price || c.tax_rate !== o.tax_rate) {
      return true;
    }
  }
  return false;
}

/** Return a human-readable badge color class for invoice status. */
function statusBadgeClasses(status: InvoiceStatus): string {
  switch (status) {
    case 'draft':
      return 'bg-slate-100 text-slate-700';
    case 'sent':
      return 'bg-blue-100 text-blue-700';
    case 'partial':
      return 'bg-amber-100 text-amber-700';
    case 'paid':
      return 'bg-green-100 text-green-700';
    case 'void':
      return 'bg-red-100 text-red-700';
    case 'overdue':
      return 'bg-orange-100 text-orange-700';
    default:
      return 'bg-slate-100 text-slate-700';
  }
}

// ---------------------------------------------------------------------------
// Reusable sub-components (internal — not exported)
// ---------------------------------------------------------------------------

/** Inline error text rendered beneath a form field. */
function FieldError({ id, message }: { id: string; message: string | undefined }): React.JSX.Element | null {
  if (!message) return null;
  return (
    <p id={id} className="mt-1 text-sm text-red-600" role="alert">
      {message}
    </p>
  );
}

/** Informational icon (SVG 14×14) used in restriction banners. */
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

/** Warning icon (SVG) for paid-amount warnings. */
function WarningIcon(): React.JSX.Element {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 20 20"
      fill="currentColor"
      className="h-5 w-5 shrink-0"
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.168 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495ZM10 6a.75.75 0 0 1 .75.75v3.5a.75.75 0 0 1-1.5 0v-3.5A.75.75 0 0 1 10 6Zm0 9a1 1 0 1 0 0-2 1 1 0 0 0 0 2Z"
        clipRule="evenodd"
      />
    </svg>
  );
}

/** Trash icon for removing line items. */
function TrashIcon(): React.JSX.Element {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 20 20"
      fill="currentColor"
      className="h-4 w-4"
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        d="M8.75 1A2.75 2.75 0 0 0 6 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 1 0 .23 1.482l.149-.022.841 10.518A2.75 2.75 0 0 0 7.596 19h4.807a2.75 2.75 0 0 0 2.742-2.53l.841-10.52.149.023a.75.75 0 0 0 .23-1.482A41.03 41.03 0 0 0 14 4.193V3.75A2.75 2.75 0 0 0 11.25 1h-2.5ZM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325C8.327 4.025 9.16 4 10 4ZM8.58 7.72a.75.75 0 0 1 .7.798l-.2 4.5a.75.75 0 0 1-1.497-.066l.2-4.5a.75.75 0 0 1 .797-.731Zm2.84 0a.75.75 0 0 1 .798.731l.2 4.5a.75.75 0 1 1-1.497.066l-.2-4.5a.75.75 0 0 1 .7-.798Z"
        clipRule="evenodd"
      />
    </svg>
  );
}

/** Plus icon for adding line items. */
function PlusIcon(): React.JSX.Element {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 20 20"
      fill="currentColor"
      className="h-4 w-4"
      aria-hidden="true"
    >
      <path d="M10.75 4.75a.75.75 0 0 0-1.5 0v4.5h-4.5a.75.75 0 0 0 0 1.5h4.5v4.5a.75.75 0 0 0 1.5 0v-4.5h4.5a.75.75 0 0 0 0-1.5h-4.5v-4.5Z" />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// CSS helper — input class builder
// ---------------------------------------------------------------------------

function inputClasses(hasError: boolean, disabled?: boolean): string {
  const base =
    'mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2';
  if (disabled) {
    return `${base} border-slate-200 bg-slate-50 text-slate-500 cursor-not-allowed`;
  }
  return hasError
    ? `${base} border-red-300 text-red-900 focus-visible:outline-red-500`
    : `${base} border-slate-300 text-slate-900 focus-visible:outline-indigo-600`;
}

// ---------------------------------------------------------------------------
// Main component
// ---------------------------------------------------------------------------

/**
 * **InvoiceManage** — Invoice edit form page component.
 *
 * Replaces the monolith's `RecordManage.cshtml.cs` for invoice entities:
 *
 * 1. Loads existing invoice data via `GET /v1/invoicing/invoices/:id`
 *    (matches the OnGet → Init → RecordsExists → populate pattern).
 * 2. Pre-populates all form fields with current values including line items.
 * 3. Submits updates via `PUT /v1/invoicing/invoices/:id`
 *    (matches OnPost → set record["id"] → UpdateRecord pattern).
 * 4. Invoice number is displayed read-only (AutoNumber — cannot change).
 * 5. Status-based edit restrictions: paid/void → redirect to detail,
 *    sent/partial → customer & currency locked.
 * 6. Paid amount guard — cannot reduce total below already-paid amount.
 * 7. Handles 404, 409 (optimistic concurrency), 422 (validation), and
 *    dirty-state tracking with unsaved-changes confirmation.
 */
function InvoiceManage(): React.JSX.Element {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  // ── Form state ──────────────────────────────────────────────────────────
  const [formState, setFormState] = useState<InvoiceEditFormState>({
    header: { ...INITIAL_HEADER },
    line_items: [],
  });
  const [originalState, setOriginalState] = useState<InvoiceEditFormState>({
    header: { ...INITIAL_HEADER },
    line_items: [],
  });
  const [validation, setValidation] = useState<FormValidation>({ errors: [] });
  const [isDataLoaded, setIsDataLoaded] = useState(false);
  const [concurrencyError, setConcurrencyError] = useState<string>('');

  // Read-only metadata from the fetched invoice
  const [invoiceNumber, setInvoiceNumber] = useState<string>('');
  const [invoiceStatus, setInvoiceStatus] = useState<InvoiceStatus>('draft');
  const [paidAmount, setPaidAmount] = useState<number>(0);
  const [customerName, setCustomerName] = useState<string>('');

  // ── Derived: whether specific fields are locked ──────────────────────────
  const isFieldLocked = LOCKED_FIELD_STATUSES.includes(invoiceStatus);

  // ── Fetch existing invoice data ─────────────────────────────────────────
  const {
    data: invoiceResponse,
    isLoading,
    isError,
    error: fetchError,
  } = useQuery<ApiResponse<InvoiceData>>({
    queryKey: ['invoicing', 'invoices', id],
    queryFn: () => get<InvoiceData>(`/v1/invoicing/invoices/${id}`),
    enabled: !!id,
    retry: (failureCount, error) => {
      const apiErr = error as unknown as ApiError;
      if (apiErr?.status === 404) return false;
      return failureCount < 2;
    },
  });

  // ── Sync fetched data into form state ───────────────────────────────────
  useEffect(() => {
    if (invoiceResponse?.success && invoiceResponse.object) {
      const invoice = invoiceResponse.object;

      // Redirect paid/void invoices to detail — cannot edit
      if (NON_EDITABLE_STATUSES.includes(invoice.status)) {
        navigate(`/invoicing/invoices/${id}`, { replace: true });
        return;
      }

      const state = invoiceToFormState(invoice);
      setFormState(state);
      setOriginalState(structuredClone(state));
      setInvoiceNumber(invoice.invoice_number ?? '');
      setInvoiceStatus(invoice.status);
      setPaidAmount(invoice.paid_amount ?? 0);
      setCustomerName(invoice.customer_name ?? '');
      setIsDataLoaded(true);
    }
  }, [invoiceResponse, id, navigate]);

  // ── Dirty-state comparison ──────────────────────────────────────────────
  const isDirty = useCallback((): boolean => {
    if (!isDataLoaded) return false;
    return isFormStateDirty(formState, originalState);
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

  // ── Real-time totals calculation ────────────────────────────────────────
  const totals = calculateTotals(
    formState.line_items,
    formState.header.tax_rate,
    formState.header.discount_type,
    formState.header.discount_value,
  );

  // Warn if total is below paid amount (but don't block form editing)
  const totalBelowPaid = paidAmount > 0 && totals.total < paidAmount;

  // ── Update mutation (PUT) ───────────────────────────────────────────────
  const updateMutation = useMutation<ApiResponse<InvoiceData>, ApiError, InvoiceUpdatePayload>({
    mutationFn: (payload) => put<InvoiceData>(`/v1/invoicing/invoices/${id}`, payload),
    onSuccess: (response) => {
      if (response.success) {
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'invoices'] });
        queryClient.invalidateQueries({ queryKey: ['invoicing', 'invoices', id] });
        navigate(`/invoicing/invoices/${id}`);
      } else {
        const mapped: ValidationError[] = (response.errors ?? []).map((err) => ({
          propertyName: err.key ?? 'general',
          message: err.message ?? err.value ?? 'Validation failed.',
        }));
        setValidation({
          message: response.message ?? 'Invoice update failed. Please correct the errors below.',
          errors: mapped,
        });
      }
    },
    onError: (error) => {
      if (error.status === 409) {
        setConcurrencyError(
          'This invoice has been modified by another user. Please reload the page and try again.',
        );
      } else if (error.status === 422 && error.errors?.length) {
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
          message: error.message ?? 'An unexpected error occurred while updating the invoice.',
          errors: [],
        });
      }
    },
  });

  // ── Form submission handler ─────────────────────────────────────────────
  const handleSubmit = useCallback(
    (e?: FormEvent) => {
      if (e) e.preventDefault();

      setConcurrencyError('');

      // Client-side validation first
      const clientErrors = validateInvoiceUpdate(formState, paidAmount);
      if (clientErrors.length > 0) {
        setValidation({ message: 'Please correct the errors below.', errors: clientErrors });
        return;
      }

      setValidation({ errors: [] });
      updateMutation.mutate(formStateToPayload(id!, formState));
    },
    [formState, paidAmount, updateMutation, id],
  );

  // ── Cancel handler with dirty-state guard ───────────────────────────────
  const handleCancel = useCallback(() => {
    if (isDirty()) {
      const confirmed = window.confirm('You have unsaved changes. Are you sure you want to leave?');
      if (!confirmed) return;
    }
    navigate(`/invoicing/invoices/${id}`);
  }, [isDirty, navigate, id]);

  // ── Header field change handler ─────────────────────────────────────────
  const handleHeaderChange = useCallback(
    (field: keyof InvoiceHeaderFormState) =>
      (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>) => {
        const value = e.target.value;
        setFormState((prev) => ({
          ...prev,
          header: { ...prev.header, [field]: value },
        }));
        setValidation((prev) => ({
          ...prev,
          errors: prev.errors.filter((err) => err.propertyName !== field),
        }));
      },
    [],
  );

  // ── Line item handlers ──────────────────────────────────────────────────
  const handleLineItemChange = useCallback(
    (index: number, field: keyof LineItemFormState) =>
      (e: React.ChangeEvent<HTMLInputElement>) => {
        setFormState((prev) => {
          const items = [...prev.line_items];
          items[index] = { ...items[index], [field]: e.target.value };
          return { ...prev, line_items: items };
        });
        setValidation((prev) => ({
          ...prev,
          errors: prev.errors.filter((err) => !err.propertyName.startsWith(`line_items[${index}]`)),
        }));
      },
    [],
  );

  const addLineItem = useCallback(() => {
    setFormState((prev) => ({
      ...prev,
      line_items: [...prev.line_items, { ...EMPTY_LINE_ITEM, id: nextTempId() }],
    }));
  }, []);

  const removeLineItem = useCallback(
    (index: number) => {
      if (formState.line_items.length <= 1) return; // Must keep at least one
      setFormState((prev) => ({
        ...prev,
        line_items: prev.line_items.filter((_, i) => i !== index),
      }));
      // Clear errors for removed line and reindex remaining
      setValidation((prev) => ({
        ...prev,
        errors: prev.errors.filter((err) => !err.propertyName.startsWith('line_items[')),
      }));
    },
    [formState.line_items.length],
  );

  // ── Field-error lookup ──────────────────────────────────────────────────
  const getFieldError = useCallback(
    (fieldName: string): string | undefined =>
      validation.errors.find((e) => e.propertyName === fieldName)?.message,
    [validation.errors],
  );

  // ── Derived render-time values ──────────────────────────────────────────
  const is404 = isError && (fetchError as unknown as ApiError)?.status === 404;
  const showValidation = validation.errors.length > 0 || !!validation.message;
  const formError = getFieldError('_form');

  // ────────────────────────────────────────────────────────────────────────
  // Loading state
  // ────────────────────────────────────────────────────────────────────────
  if (isLoading) {
    return (
      <div
        className="flex min-h-[24rem] items-center justify-center"
        role="status"
        aria-label="Loading invoice data"
      >
        <div className="flex flex-col items-center gap-3">
          <div className="h-8 w-8 animate-spin rounded-full border-4 border-slate-200 border-t-indigo-600" />
          <p className="text-sm text-slate-500">Loading invoice data…</p>
        </div>
      </div>
    );
  }

  // ────────────────────────────────────────────────────────────────────────
  // 404 — invoice not found
  // ────────────────────────────────────────────────────────────────────────
  if (is404) {
    return (
      <div className="flex min-h-[24rem] items-center justify-center">
        <div className="text-center">
          <h2 className="text-xl font-semibold text-slate-800">Invoice Not Found</h2>
          <p className="mt-2 text-sm text-slate-500">
            The invoice you are trying to edit does not exist or has been removed.
          </p>
          <button
            type="button"
            onClick={() => navigate('/invoicing/invoices')}
            className="mt-4 inline-flex items-center gap-1.5 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          >
            <span aria-hidden="true">←</span> Back to Invoices
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
          <h2 className="text-xl font-semibold text-red-700">Error Loading Invoice</h2>
          <p className="mt-2 text-sm text-slate-500">
            {(fetchError as unknown as ApiError)?.message ??
              'An unexpected error occurred while loading the invoice.'}
          </p>
          <button
            type="button"
            onClick={() => navigate('/invoicing/invoices')}
            className="mt-4 inline-flex items-center gap-1.5 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          >
            <span aria-hidden="true">←</span> Back to Invoices
          </button>
        </div>
      </div>
    );
  }

  // ────────────────────────────────────────────────────────────────────────
  // Main edit form
  // ────────────────────────────────────────────────────────────────────────
  return (
    <div className="mx-auto max-w-5xl px-4 py-6 sm:px-6 lg:px-8">
      {/* Page header */}
      <div className="mb-6">
        <button
          type="button"
          onClick={handleCancel}
          className="mb-2 inline-flex items-center gap-1 text-sm text-indigo-600 hover:text-indigo-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          aria-label={`Back to invoice ${invoiceNumber} details`}
        >
          <span aria-hidden="true">←</span> Back to Invoice Details
        </button>
        <div className="flex items-center gap-3">
          <h1 className="text-2xl font-bold text-slate-900">
            Edit Invoice {invoiceNumber ? `#${invoiceNumber}` : ''}
          </h1>
          <span
            className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium capitalize ${statusBadgeClasses(invoiceStatus)}`}
          >
            {invoiceStatus}
          </span>
        </div>
        {customerName && (
          <p className="mt-1 text-sm text-slate-500">Customer: {customerName}</p>
        )}
      </div>

      {/* Concurrency conflict alert */}
      {concurrencyError && (
        <div
          className="mb-6 flex items-start gap-3 rounded-md border border-amber-300 bg-amber-50 p-4"
          role="alert"
        >
          <WarningIcon />
          <div>
            <p className="text-sm font-medium text-amber-800">{concurrencyError}</p>
            <button
              type="button"
              onClick={() => {
                setConcurrencyError('');
                queryClient.invalidateQueries({ queryKey: ['invoicing', 'invoices', id] });
              }}
              className="mt-2 text-sm font-medium text-amber-700 underline hover:text-amber-900 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-amber-600"
            >
              Reload invoice data
            </button>
          </div>
        </div>
      )}

      {/* Edit restriction banner for sent/partial invoices */}
      {isFieldLocked && (
        <div className="mb-6 flex items-start gap-2 rounded-md border border-blue-200 bg-blue-50 p-4">
          <InfoIcon />
          <p className="text-sm text-blue-800">
            This invoice has been {invoiceStatus}. Customer and currency fields are locked. Line items and other fields
            remain editable.
          </p>
        </div>
      )}

      {/* Paid amount warning */}
      {totalBelowPaid && (
        <div
          className="mb-6 flex items-start gap-3 rounded-md border border-amber-300 bg-amber-50 p-4"
          role="alert"
        >
          <WarningIcon />
          <p className="text-sm font-medium text-amber-800">
            Current total (${totals.total.toFixed(2)}) is less than the paid amount ($
            {paidAmount.toFixed(2)}). You will not be able to save until the total is at least equal
            to the paid amount.
          </p>
        </div>
      )}

      {/* Form-level error */}
      {formError && (
        <div className="mb-6 rounded-md border border-red-300 bg-red-50 p-4" role="alert">
          <p className="text-sm font-medium text-red-800">{formError}</p>
        </div>
      )}

      {/* Header actions */}
      <div className="mb-6 flex items-center gap-3">
        <button
          type="button"
          onClick={() => handleSubmit()}
          disabled={updateMutation.isPending}
          className="inline-flex items-center gap-1.5 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {updateMutation.isPending ? 'Saving…' : 'Save Changes'}
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

      {/* DynamicForm wrapper — replaces <wv-form method="UpdateRecord"> */}
      <DynamicForm
        name="invoice-edit"
        method="post"
        showValidation={showValidation}
        validation={validation}
        onSubmit={handleSubmit}
        className="space-y-8"
      >
        {/* ────── Section 1 — Invoice Number (read-only) & Customer & Dates ────── */}
        <fieldset className="rounded-lg border border-slate-200 p-6">
          <legend className="px-2 text-base font-semibold text-slate-900">
            Invoice Details
          </legend>

          <div className="mt-4 grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-2">
            {/* Invoice Number — READ-ONLY (AutoNumber) */}
            <div>
              <label
                htmlFor="invoice-number"
                className="block text-sm font-medium text-slate-700"
              >
                Invoice Number
              </label>
              <input
                id="invoice-number"
                type="text"
                value={invoiceNumber}
                readOnly
                disabled
                aria-describedby="invoice-number-hint"
                className={inputClasses(false, true)}
              />
              <p
                id="invoice-number-hint"
                className="mt-1 flex items-center gap-1 text-xs text-slate-400"
              >
                <InfoIcon />
                Auto-generated — cannot be modified.
              </p>
            </div>

            {/* Customer */}
            <div>
              <label
                htmlFor="invoice-customer"
                className="block text-sm font-medium text-slate-700"
              >
                Customer <span className="text-red-500" aria-hidden="true">*</span>
              </label>
              <input
                id="invoice-customer"
                type="text"
                required
                placeholder="Customer ID"
                value={formState.header.customer_id}
                onChange={handleHeaderChange('customer_id')}
                readOnly={isFieldLocked}
                disabled={isFieldLocked}
                aria-invalid={!!getFieldError('customer_id')}
                aria-describedby={
                  getFieldError('customer_id')
                    ? 'err-customer'
                    : isFieldLocked
                      ? 'customer-locked-hint'
                      : undefined
                }
                className={inputClasses(!!getFieldError('customer_id'), isFieldLocked)}
              />
              {isFieldLocked && (
                <p id="customer-locked-hint" className="mt-1 flex items-center gap-1 text-xs text-amber-600">
                  <InfoIcon />
                  Locked — invoice has been {invoiceStatus}.
                </p>
              )}
              <FieldError id="err-customer" message={getFieldError('customer_id')} />
            </div>
          </div>

          <div className="mt-4 grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-3">
            {/* Issue Date */}
            <div>
              <label
                htmlFor="invoice-issue-date"
                className="block text-sm font-medium text-slate-700"
              >
                Issue Date <span className="text-red-500" aria-hidden="true">*</span>
              </label>
              <input
                id="invoice-issue-date"
                type="date"
                required
                value={formState.header.issue_date}
                onChange={handleHeaderChange('issue_date')}
                aria-invalid={!!getFieldError('issue_date')}
                aria-describedby={getFieldError('issue_date') ? 'err-issue-date' : undefined}
                className={inputClasses(!!getFieldError('issue_date'))}
              />
              <FieldError id="err-issue-date" message={getFieldError('issue_date')} />
            </div>

            {/* Due Date */}
            <div>
              <label
                htmlFor="invoice-due-date"
                className="block text-sm font-medium text-slate-700"
              >
                Due Date <span className="text-red-500" aria-hidden="true">*</span>
              </label>
              <input
                id="invoice-due-date"
                type="date"
                required
                value={formState.header.due_date}
                onChange={handleHeaderChange('due_date')}
                aria-invalid={!!getFieldError('due_date')}
                aria-describedby={getFieldError('due_date') ? 'err-due-date' : undefined}
                className={inputClasses(!!getFieldError('due_date'))}
              />
              <FieldError id="err-due-date" message={getFieldError('due_date')} />
            </div>

            {/* Currency */}
            <div>
              <label
                htmlFor="invoice-currency"
                className="block text-sm font-medium text-slate-700"
              >
                Currency <span className="text-red-500" aria-hidden="true">*</span>
              </label>
              <select
                id="invoice-currency"
                required
                value={formState.header.currency}
                onChange={handleHeaderChange('currency')}
                disabled={isFieldLocked}
                aria-invalid={!!getFieldError('currency')}
                aria-describedby={
                  getFieldError('currency')
                    ? 'err-currency'
                    : isFieldLocked
                      ? 'currency-locked-hint'
                      : undefined
                }
                className={inputClasses(!!getFieldError('currency'), isFieldLocked)}
              >
                {CURRENCY_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
              {isFieldLocked && (
                <p id="currency-locked-hint" className="mt-1 flex items-center gap-1 text-xs text-amber-600">
                  <InfoIcon />
                  Locked — cannot change currency after sending.
                </p>
              )}
              <FieldError id="err-currency" message={getFieldError('currency')} />
            </div>
          </div>
        </fieldset>

        {/* ────── Section 2 — Line Items ────── */}
        <fieldset className="rounded-lg border border-slate-200 p-6">
          <legend className="px-2 text-base font-semibold text-slate-900">Line Items</legend>

          {getFieldError('line_items') && (
            <div className="mt-2 rounded-md bg-red-50 p-3">
              <p className="text-sm text-red-700">{getFieldError('line_items')}</p>
            </div>
          )}

          {/* Line item table header */}
          <div className="mt-4 hidden md:grid md:grid-cols-12 md:gap-x-3 md:gap-y-0">
            <div className="col-span-4 text-xs font-medium uppercase tracking-wide text-slate-500">
              Description
            </div>
            <div className="col-span-2 text-xs font-medium uppercase tracking-wide text-slate-500">
              Quantity
            </div>
            <div className="col-span-2 text-xs font-medium uppercase tracking-wide text-slate-500">
              Unit Price
            </div>
            <div className="col-span-1 text-xs font-medium uppercase tracking-wide text-slate-500">
              Tax %
            </div>
            <div className="col-span-2 text-end text-xs font-medium uppercase tracking-wide text-slate-500">
              Amount
            </div>
            <div className="col-span-1" />
          </div>

          {/* Line items */}
          {formState.line_items.map((item, index) => {
            const qty = parseFloat(item.quantity) || 0;
            const price = parseFloat(item.unit_price) || 0;
            const lineTax = parseFloat(item.tax_rate) || 0;
            const lineAmount = calculateLineAmount(qty, price, lineTax);

            return (
              <div
                key={item.id || index}
                className="mt-3 grid grid-cols-1 gap-x-3 gap-y-2 rounded-md border border-slate-100 bg-white p-3 md:grid-cols-12 md:items-start md:border-0 md:p-0 md:pt-2"
              >
                {/* Description */}
                <div className="md:col-span-4">
                  <label
                    htmlFor={`li-desc-${index}`}
                    className="block text-xs font-medium text-slate-500 md:sr-only"
                  >
                    Description
                  </label>
                  <input
                    id={`li-desc-${index}`}
                    type="text"
                    placeholder="Item description"
                    value={item.description}
                    onChange={handleLineItemChange(index, 'description')}
                    aria-invalid={!!getFieldError(`line_items[${index}].description`)}
                    className={inputClasses(!!getFieldError(`line_items[${index}].description`))}
                  />
                  <FieldError
                    id={`err-li-desc-${index}`}
                    message={getFieldError(`line_items[${index}].description`)}
                  />
                </div>

                {/* Quantity */}
                <div className="md:col-span-2">
                  <label
                    htmlFor={`li-qty-${index}`}
                    className="block text-xs font-medium text-slate-500 md:sr-only"
                  >
                    Quantity
                  </label>
                  <input
                    id={`li-qty-${index}`}
                    type="number"
                    min={0}
                    step="1"
                    value={item.quantity}
                    onChange={handleLineItemChange(index, 'quantity')}
                    aria-invalid={!!getFieldError(`line_items[${index}].quantity`)}
                    className={inputClasses(!!getFieldError(`line_items[${index}].quantity`))}
                  />
                  <FieldError
                    id={`err-li-qty-${index}`}
                    message={getFieldError(`line_items[${index}].quantity`)}
                  />
                </div>

                {/* Unit Price */}
                <div className="md:col-span-2">
                  <label
                    htmlFor={`li-price-${index}`}
                    className="block text-xs font-medium text-slate-500 md:sr-only"
                  >
                    Unit Price
                  </label>
                  <input
                    id={`li-price-${index}`}
                    type="number"
                    min={0}
                    step="0.01"
                    value={item.unit_price}
                    onChange={handleLineItemChange(index, 'unit_price')}
                    aria-invalid={!!getFieldError(`line_items[${index}].unit_price`)}
                    className={inputClasses(!!getFieldError(`line_items[${index}].unit_price`))}
                  />
                  <FieldError
                    id={`err-li-price-${index}`}
                    message={getFieldError(`line_items[${index}].unit_price`)}
                  />
                </div>

                {/* Line Tax */}
                <div className="md:col-span-1">
                  <label
                    htmlFor={`li-tax-${index}`}
                    className="block text-xs font-medium text-slate-500 md:sr-only"
                  >
                    Tax %
                  </label>
                  <input
                    id={`li-tax-${index}`}
                    type="number"
                    min={0}
                    max={100}
                    step="0.01"
                    value={item.tax_rate}
                    onChange={handleLineItemChange(index, 'tax_rate')}
                    aria-invalid={!!getFieldError(`line_items[${index}].tax_rate`)}
                    className={inputClasses(!!getFieldError(`line_items[${index}].tax_rate`))}
                  />
                  <FieldError
                    id={`err-li-tax-${index}`}
                    message={getFieldError(`line_items[${index}].tax_rate`)}
                  />
                </div>

                {/* Calculated Amount (read-only) */}
                <div className="flex items-center justify-end md:col-span-2 md:pt-2">
                  <span className="text-sm font-medium text-slate-700">
                    ${lineAmount.toFixed(2)}
                  </span>
                </div>

                {/* Remove button */}
                <div className="flex items-center justify-end md:col-span-1 md:pt-1">
                  <button
                    type="button"
                    onClick={() => removeLineItem(index)}
                    disabled={formState.line_items.length <= 1}
                    aria-label={`Remove line item ${index + 1}`}
                    className="inline-flex items-center rounded p-1.5 text-slate-400 hover:bg-red-50 hover:text-red-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500 disabled:cursor-not-allowed disabled:opacity-30"
                  >
                    <TrashIcon />
                  </button>
                </div>
              </div>
            );
          })}

          {/* Add line item button */}
          <div className="mt-4">
            <button
              type="button"
              onClick={addLineItem}
              className="inline-flex items-center gap-1.5 rounded-md border border-dashed border-slate-300 px-3 py-2 text-sm font-medium text-slate-600 hover:border-indigo-400 hover:text-indigo-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
            >
              <PlusIcon />
              Add Line Item
            </button>
          </div>
        </fieldset>

        {/* ────── Section 3 — Totals ────── */}
        <fieldset className="rounded-lg border border-slate-200 p-6">
          <legend className="px-2 text-base font-semibold text-slate-900">Totals</legend>

          <div className="mt-4 grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-3">
            {/* Tax Rate (header-level) */}
            <div>
              <label
                htmlFor="invoice-tax-rate"
                className="block text-sm font-medium text-slate-700"
              >
                Tax Rate (%)
              </label>
              <input
                id="invoice-tax-rate"
                type="number"
                min={0}
                max={100}
                step="0.01"
                value={formState.header.tax_rate}
                onChange={handleHeaderChange('tax_rate')}
                aria-invalid={!!getFieldError('tax_rate')}
                aria-describedby={getFieldError('tax_rate') ? 'err-tax-rate' : undefined}
                className={inputClasses(!!getFieldError('tax_rate'))}
              />
              <FieldError id="err-tax-rate" message={getFieldError('tax_rate')} />
            </div>

            {/* Discount Type */}
            <div>
              <label
                htmlFor="invoice-discount-type"
                className="block text-sm font-medium text-slate-700"
              >
                Discount Type
              </label>
              <select
                id="invoice-discount-type"
                value={formState.header.discount_type}
                onChange={handleHeaderChange('discount_type')}
                className={inputClasses(false)}
              >
                {DISCOUNT_TYPE_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>

            {/* Discount Value */}
            <div>
              <label
                htmlFor="invoice-discount-value"
                className="block text-sm font-medium text-slate-700"
              >
                Discount Value
              </label>
              <input
                id="invoice-discount-value"
                type="number"
                min={0}
                step="0.01"
                value={formState.header.discount_value}
                onChange={handleHeaderChange('discount_value')}
                aria-invalid={!!getFieldError('discount_value')}
                aria-describedby={getFieldError('discount_value') ? 'err-discount' : undefined}
                className={inputClasses(!!getFieldError('discount_value'))}
              />
              <FieldError id="err-discount" message={getFieldError('discount_value')} />
            </div>
          </div>

          {/* Computed totals summary */}
          <div className="mt-6 border-t border-slate-200 pt-4">
            <dl className="space-y-2">
              <div className="flex justify-between text-sm">
                <dt className="text-slate-500">Subtotal</dt>
                <dd className="font-medium text-slate-700">${totals.subtotal.toFixed(2)}</dd>
              </div>
              <div className="flex justify-between text-sm">
                <dt className="text-slate-500">
                  Tax ({parseFloat(formState.header.tax_rate) || 0}%)
                </dt>
                <dd className="font-medium text-slate-700">${totals.taxAmount.toFixed(2)}</dd>
              </div>
              <div className="flex justify-between text-sm">
                <dt className="text-slate-500">
                  Discount
                  {formState.header.discount_type === 'percentage'
                    ? ` (${parseFloat(formState.header.discount_value) || 0}%)`
                    : ''}
                </dt>
                <dd className="font-medium text-red-600">
                  {totals.discountAmount > 0 ? `−$${totals.discountAmount.toFixed(2)}` : '$0.00'}
                </dd>
              </div>
              <div className="flex justify-between border-t border-slate-200 pt-2 text-base font-semibold">
                <dt className="text-slate-900">Total</dt>
                <dd className={totalBelowPaid ? 'text-red-600' : 'text-slate-900'}>
                  ${totals.total.toFixed(2)}
                </dd>
              </div>
              {paidAmount > 0 && (
                <>
                  <div className="flex justify-between text-sm">
                    <dt className="text-slate-500">Paid Amount</dt>
                    <dd className="font-medium text-green-600">${paidAmount.toFixed(2)}</dd>
                  </div>
                  <div className="flex justify-between text-sm font-medium">
                    <dt className="text-slate-700">Balance Due</dt>
                    <dd className="text-slate-900">
                      ${Math.max(round2(totals.total - paidAmount), 0).toFixed(2)}
                    </dd>
                  </div>
                </>
              )}
            </dl>
          </div>
        </fieldset>

        {/* ────── Section 4 — Notes & Terms ────── */}
        <fieldset className="rounded-lg border border-slate-200 p-6">
          <legend className="px-2 text-base font-semibold text-slate-900">Notes &amp; Terms</legend>

          <div className="mt-4 grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-2">
            {/* Notes */}
            <div>
              <label
                htmlFor="invoice-notes"
                className="block text-sm font-medium text-slate-700"
              >
                Notes
              </label>
              <textarea
                id="invoice-notes"
                rows={4}
                placeholder="Additional notes visible to the customer…"
                value={formState.header.notes}
                onChange={handleHeaderChange('notes')}
                className="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
              />
            </div>

            {/* Terms */}
            <div>
              <label
                htmlFor="invoice-terms"
                className="block text-sm font-medium text-slate-700"
              >
                Terms &amp; Conditions
              </label>
              <textarea
                id="invoice-terms"
                rows={4}
                placeholder="Payment terms and conditions…"
                value={formState.header.terms}
                onChange={handleHeaderChange('terms')}
                className="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
              />
            </div>
          </div>
        </fieldset>
      </DynamicForm>
    </div>
  );
}

export default InvoiceManage;
