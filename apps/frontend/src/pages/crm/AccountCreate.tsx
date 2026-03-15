/**
 * AccountCreate — React page component for creating new CRM accounts.
 *
 * Replaces the monolith's `RecordCreate.cshtml` Razor Page for the account
 * entity. Renders all CRM-specific fields defined in the Next plugin patches
 * (`NextPlugin.20190204.cs` and `NextPlugin.20190206.cs`).
 *
 * Route: `/crm/accounts/create`
 *
 * Sections:
 *  - Identity (name, type, salutation, first/last name)
 *  - Contact  (email, phones, website)
 *  - Address  (street, city, region, post code, country)
 *  - Business (tax ID, language, currency)
 *  - Notes    (multiline)
 *
 * Conditional display:
 *  - type === "1" (Company): hides first_name, last_name, salutation_id
 *  - type === "2" (Person):  shows first_name, last_name, salutation_id
 */

import { useState, useCallback, useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import apiClient from '../../api/client';
import { useCreateAccount, useSalutations } from '../../hooks/useCrm';
import type { EntityRecord } from '../../types/record';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Account type select options.
 * Values "1" (Company) and "2" (Person) match the InputSelectField options
 * defined in NextPlugin.20190204.cs.
 */
const ACCOUNT_TYPE_OPTIONS: ReadonlyArray<{ label: string; value: string }> = [
  { label: 'Company', value: '1' },
  { label: 'Person', value: '2' },
] as const;

/** Stale time for reference-data queries (countries, languages, currencies). */
const REFERENCE_DATA_STALE_TIME_MS = 30 * 60 * 1000; // 30 minutes

// ---------------------------------------------------------------------------
// Type Definitions
// ---------------------------------------------------------------------------

/** Per-field validation error messages keyed by field name. */
interface FormErrors {
  [key: string]: string;
}

/** Typed form-state matching the editable account entity fields. */
interface AccountFormState {
  name: string;
  type: string;
  salutation_id: string;
  first_name: string;
  last_name: string;
  email: string;
  fixed_phone: string;
  mobile_phone: string;
  fax_phone: string;
  website: string;
  street: string;
  street_2: string;
  city: string;
  region: string;
  post_code: string;
  country_id: string;
  tax_id: string;
  language_id: string;
  currency_id: string;
  notes: string;
}

/** Shape of reference-data records returned by the CRM API. */
interface ReferenceRecord {
  id?: string;
  name?: string;
  [key: string]: unknown;
}

/**
 * Initial (blank) form state.
 * – `type` defaults to "1" (Company), matching the monolith's
 *   `DefaultValue = "1"` on the InputSelectField.
 * – All other fields default to empty strings.
 */
const INITIAL_FORM_STATE: AccountFormState = {
  name: '',
  type: '1',
  salutation_id: '',
  first_name: '',
  last_name: '',
  email: '',
  fixed_phone: '',
  mobile_phone: '',
  fax_phone: '',
  website: '',
  street: '',
  street_2: '',
  city: '',
  region: '',
  post_code: '',
  country_id: '',
  tax_id: '',
  language_id: '',
  currency_id: '',
  notes: '',
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Simple email regex matching standard user@domain.tld patterns. */
const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** URL must begin with http:// or https:// */
const URL_REGEX = /^https?:\/\/.+/;

/**
 * Safely extract an array of reference records from an Axios API response.
 * Handles both `{ object: { records: [...] } }` and `{ object: [...] }` shapes.
 */
function extractRecords(data: unknown): ReferenceRecord[] {
  if (!data || typeof data !== 'object') return [];
  const envelope = data as Record<string, unknown>;
  if (!envelope.success) return [];
  const obj = envelope.object;
  if (Array.isArray(obj)) return obj as ReferenceRecord[];
  if (obj && typeof obj === 'object' && 'records' in obj) {
    return (obj as { records: ReferenceRecord[] }).records ?? [];
  }
  return [];
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * AccountCreate page component.
 *
 * Provides a multi-section form for creating a new CRM account. Uses
 * TanStack Query for the create mutation (`useCreateAccount`) and for
 * fetching reference / lookup data (salutations, countries, languages,
 * currencies). Navigates to the new account's detail page on success or
 * back to the account list on cancel.
 */
export default function AccountCreate() {
  const navigate = useNavigate();

  // ------ Form state ------
  const [formData, setFormData] = useState<AccountFormState>(INITIAL_FORM_STATE);
  const [errors, setErrors] = useState<FormErrors>({});

  // ------ Create mutation ------
  const {
    mutate,
    mutateAsync,
    isPending,
    isError: isMutationError,
    error: mutationError,
    isSuccess,
    reset: resetMutation,
  } = useCreateAccount();

  // `mutate` is available for fire-and-forget usage; this component uses
  // `mutateAsync` in handleSubmit where we need to await the result for
  // navigation. Both are destructured for API completeness.

  // ------ Salutations (from useCrm hook) ------
  const {
    data: salutationsData,
    isLoading: salutationsLoading,
  } = useSalutations();

  // ------ Reference-data queries via apiClient.get() ------
  const {
    data: countriesData,
    isLoading: countriesLoading,
  } = useQuery<ReferenceRecord[], Error>({
    queryKey: ['crm', 'countries'],
    queryFn: async (): Promise<ReferenceRecord[]> => {
      const response = await apiClient.get('/crm/countries');
      return extractRecords(response.data);
    },
    staleTime: REFERENCE_DATA_STALE_TIME_MS,
  });

  const {
    data: languagesData,
    isLoading: languagesLoading,
  } = useQuery<ReferenceRecord[], Error>({
    queryKey: ['crm', 'languages'],
    queryFn: async (): Promise<ReferenceRecord[]> => {
      const response = await apiClient.get('/crm/languages');
      return extractRecords(response.data);
    },
    staleTime: REFERENCE_DATA_STALE_TIME_MS,
  });

  const {
    data: currenciesData,
    isLoading: currenciesLoading,
  } = useQuery<ReferenceRecord[], Error>({
    queryKey: ['crm', 'currencies'],
    queryFn: async (): Promise<ReferenceRecord[]> => {
      const response = await apiClient.get('/crm/currencies');
      return extractRecords(response.data);
    },
    staleTime: REFERENCE_DATA_STALE_TIME_MS,
  });

  // ------ Computed values ------
  /** True when the selected account type is "Person". */
  const isPerson = useMemo(() => formData.type === '2', [formData.type]);

  /** Salutation records for the dropdown, falling back to empty array. */
  const salutationRecords = useMemo(
    () => (salutationsData?.records ?? []) as ReferenceRecord[],
    [salutationsData],
  );

  // ------ Event handlers ------

  /**
   * Generic change handler for all text inputs, selects, and textareas.
   * Clears the field-level error when the user starts editing.
   */
  const handleChange = useCallback(
    (
      e: React.ChangeEvent<
        HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement
      >,
    ) => {
      const { name, value } = e.target;
      setFormData((prev) => ({ ...prev, [name]: value }));
      setErrors((prev) => {
        if (prev[name]) {
          const next = { ...prev };
          delete next[name];
          return next;
        }
        return prev;
      });
    },
    [],
  );

  /**
   * Client-side validation. Returns `true` when the form is valid.
   *
   * Rules:
   *  - `name` is always required (matches monolith Required=true)
   *  - `type` is always required
   *  - `first_name` and `last_name` are required when type is Person
   *  - `email` must be a valid email when provided
   *  - `website` must start with http:// or https:// when provided
   */
  const validate = useCallback((): boolean => {
    const next: FormErrors = {};

    if (!formData.name.trim()) {
      next.name = 'Account name is required.';
    }
    if (!formData.type) {
      next.type = 'Account type is required.';
    }

    // Person-specific required fields
    if (formData.type === '2') {
      if (!formData.first_name.trim()) {
        next.first_name = 'First name is required for Person accounts.';
      }
      if (!formData.last_name.trim()) {
        next.last_name = 'Last name is required for Person accounts.';
      }
    }

    // Optional format validation
    if (formData.email && !EMAIL_REGEX.test(formData.email)) {
      next.email = 'Please enter a valid email address.';
    }
    if (formData.website && !URL_REGEX.test(formData.website)) {
      next.website = 'URL must start with http:// or https://.';
    }

    setErrors(next);
    return Object.keys(next).length === 0;
  }, [formData]);

  /**
   * Form submission handler.
   * Validates → builds EntityRecord → calls mutateAsync → navigates.
   */
  const handleSubmit = useCallback(
    async (e: React.FormEvent<HTMLFormElement>) => {
      e.preventDefault();
      resetMutation();

      if (!validate()) return;

      // Build the EntityRecord payload, omitting empty optional fields
      const record: EntityRecord = {};
      const entries = Object.entries(formData) as [string, string][];
      for (const [key, value] of entries) {
        if (value !== '') {
          record[key] = value;
        }
      }
      // Always include required fields even if they appear empty
      record.name = formData.name;
      record.type = formData.type;

      try {
        const created = await mutateAsync(record);
        // Navigate to the new account's detail page
        const createdId =
          created && typeof created === 'object' && 'id' in created
            ? String((created as EntityRecord).id)
            : undefined;

        if (createdId) {
          navigate(`/crm/accounts/${createdId}`);
        } else {
          navigate('/crm/accounts');
        }
      } catch (err: unknown) {
        // Attempt to map server-side validation errors to individual fields
        if (err && typeof err === 'object' && 'errors' in err) {
          const apiErr = err as {
            errors?: Array<{ key?: string; message?: string; value?: string }>;
          };
          if (Array.isArray(apiErr.errors) && apiErr.errors.length > 0) {
            const fieldErrors: FormErrors = {};
            for (const item of apiErr.errors) {
              const fieldKey = item.key ?? '';
              const msg = item.message ?? item.value ?? 'Validation error';
              if (fieldKey && fieldKey in formData) {
                fieldErrors[fieldKey] = msg;
              } else {
                // Accumulate unkeyed errors under _form
                fieldErrors._form = fieldErrors._form
                  ? `${fieldErrors._form} ${msg}`
                  : msg;
              }
            }
            setErrors(fieldErrors);
            return;
          }
        }
        // Fallback generic error
        const message =
          err instanceof Error ? err.message : 'An unexpected error occurred.';
        setErrors({ _form: message });
      }
    },
    [formData, validate, mutateAsync, resetMutation, navigate],
  );

  /** Navigate back to the accounts list. */
  const handleCancel = useCallback(() => {
    navigate('/crm/accounts');
  }, [navigate]);

  // ------ Render helpers ------

  /** Renders an inline error message below a form field. */
  const fieldError = (field: string) =>
    errors[field] ? (
      <p
        className="mt-1 text-sm text-red-600"
        role="alert"
        id={`${field}-error`}
      >
        {errors[field]}
      </p>
    ) : null;

  /** Common input className (Tailwind). */
  const inputClass = (field: string) =>
    `block w-full rounded-md border px-3 py-2 text-sm shadow-sm transition-colors
     focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
     ${
       errors[field]
         ? 'border-red-500 text-red-900 placeholder-red-400'
         : 'border-gray-300 text-gray-900 placeholder-gray-400'
     }`;

  /** Common label className. */
  const labelClass = 'block text-sm font-medium text-gray-700 mb-1';

  /** Required asterisk. */
  const req = <span className="text-red-500 ms-0.5">*</span>;

  // ------ JSX ------

  return (
    <main className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
      {/* Page header */}
      <div className="mb-8">
        <h1 className="text-2xl font-bold text-gray-900">Create Account</h1>
        <p className="mt-1 text-sm text-gray-500">
          Fill in the details below to create a new CRM account.
        </p>
      </div>

      {/* Form-level error */}
      {errors._form && (
        <div
          className="mb-6 rounded-md border border-red-300 bg-red-50 p-4 text-sm text-red-700"
          role="alert"
        >
          {errors._form}
        </div>
      )}

      {/* Mutation-level error (from useCreateAccount) */}
      {isMutationError && !errors._form && (
        <div
          className="mb-6 rounded-md border border-red-300 bg-red-50 p-4 text-sm text-red-700"
          role="alert"
        >
          {mutationError?.message ?? 'Failed to create account.'}
        </div>
      )}

      {/* Success banner (brief — will navigate away almost immediately) */}
      {isSuccess && (
        <div
          className="mb-6 rounded-md border border-green-300 bg-green-50 p-4 text-sm text-green-700"
          role="status"
        >
          Account created successfully. Redirecting…
        </div>
      )}

      <form onSubmit={handleSubmit} noValidate>
        {/* ============================================================
            IDENTITY SECTION
            ============================================================ */}
        <section
          className="mb-8 rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
          aria-labelledby="section-identity"
        >
          <h2
            id="section-identity"
            className="mb-4 text-lg font-semibold text-gray-800"
          >
            Identity
          </h2>

          <div className="grid grid-cols-1 gap-6 md:grid-cols-2">
            {/* Account Name */}
            <div>
              <label htmlFor="name" className={labelClass}>
                Account Name{req}
              </label>
              <input
                id="name"
                name="name"
                type="text"
                required
                value={formData.name}
                onChange={handleChange}
                placeholder="Enter account name"
                className={inputClass('name')}
                aria-invalid={!!errors.name}
                aria-describedby={errors.name ? 'name-error' : undefined}
              />
              {fieldError('name')}
            </div>

            {/* Account Type */}
            <div>
              <label htmlFor="type" className={labelClass}>
                Account Type{req}
              </label>
              <select
                id="type"
                name="type"
                required
                value={formData.type}
                onChange={handleChange}
                className={inputClass('type')}
                aria-invalid={!!errors.type}
                aria-describedby={errors.type ? 'type-error' : undefined}
              >
                <option value="" disabled>
                  Select type…
                </option>
                {ACCOUNT_TYPE_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
              {fieldError('type')}
            </div>

            {/* --- Person-specific fields (conditionally rendered) --- */}
            {isPerson && (
              <>
                {/* Salutation */}
                <div>
                  <label htmlFor="salutation_id" className={labelClass}>
                    Salutation
                  </label>
                  <select
                    id="salutation_id"
                    name="salutation_id"
                    value={formData.salutation_id}
                    onChange={handleChange}
                    className={inputClass('salutation_id')}
                    disabled={salutationsLoading}
                    aria-busy={salutationsLoading}
                  >
                    <option value="">
                      {salutationsLoading
                        ? 'Loading salutations…'
                        : 'Select salutation…'}
                    </option>
                    {salutationRecords.map((s) => (
                      <option key={String(s.id)} value={String(s.id)}>
                        {String(s.name ?? '')}
                      </option>
                    ))}
                  </select>
                  {fieldError('salutation_id')}
                </div>

                {/* First Name */}
                <div>
                  <label htmlFor="first_name" className={labelClass}>
                    First Name{req}
                  </label>
                  <input
                    id="first_name"
                    name="first_name"
                    type="text"
                    required
                    value={formData.first_name}
                    onChange={handleChange}
                    placeholder="Enter first name"
                    className={inputClass('first_name')}
                    aria-invalid={!!errors.first_name}
                    aria-describedby={
                      errors.first_name ? 'first_name-error' : undefined
                    }
                  />
                  {fieldError('first_name')}
                </div>

                {/* Last Name */}
                <div>
                  <label htmlFor="last_name" className={labelClass}>
                    Last Name{req}
                  </label>
                  <input
                    id="last_name"
                    name="last_name"
                    type="text"
                    required
                    value={formData.last_name}
                    onChange={handleChange}
                    placeholder="Enter last name"
                    className={inputClass('last_name')}
                    aria-invalid={!!errors.last_name}
                    aria-describedby={
                      errors.last_name ? 'last_name-error' : undefined
                    }
                  />
                  {fieldError('last_name')}
                </div>
              </>
            )}
          </div>
        </section>

        {/* ============================================================
            CONTACT SECTION
            ============================================================ */}
        <section
          className="mb-8 rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
          aria-labelledby="section-contact"
        >
          <h2
            id="section-contact"
            className="mb-4 text-lg font-semibold text-gray-800"
          >
            Contact
          </h2>

          <div className="grid grid-cols-1 gap-6 md:grid-cols-2">
            {/* Email */}
            <div>
              <label htmlFor="email" className={labelClass}>
                Email
              </label>
              <input
                id="email"
                name="email"
                type="email"
                value={formData.email}
                onChange={handleChange}
                placeholder="email@example.com"
                className={inputClass('email')}
                aria-invalid={!!errors.email}
                aria-describedby={errors.email ? 'email-error' : undefined}
              />
              {fieldError('email')}
            </div>

            {/* Website */}
            <div>
              <label htmlFor="website" className={labelClass}>
                Website
              </label>
              <input
                id="website"
                name="website"
                type="url"
                value={formData.website}
                onChange={handleChange}
                placeholder="https://example.com"
                className={inputClass('website')}
                aria-invalid={!!errors.website}
                aria-describedby={errors.website ? 'website-error' : undefined}
              />
              {fieldError('website')}
            </div>

            {/* Fixed Phone */}
            <div>
              <label htmlFor="fixed_phone" className={labelClass}>
                Phone (Fixed)
              </label>
              <input
                id="fixed_phone"
                name="fixed_phone"
                type="tel"
                value={formData.fixed_phone}
                onChange={handleChange}
                placeholder="Enter phone number"
                className={inputClass('fixed_phone')}
              />
              {fieldError('fixed_phone')}
            </div>

            {/* Mobile Phone */}
            <div>
              <label htmlFor="mobile_phone" className={labelClass}>
                Phone (Mobile)
              </label>
              <input
                id="mobile_phone"
                name="mobile_phone"
                type="tel"
                value={formData.mobile_phone}
                onChange={handleChange}
                placeholder="Enter mobile number"
                className={inputClass('mobile_phone')}
              />
              {fieldError('mobile_phone')}
            </div>

            {/* Fax */}
            <div>
              <label htmlFor="fax_phone" className={labelClass}>
                Fax
              </label>
              <input
                id="fax_phone"
                name="fax_phone"
                type="tel"
                value={formData.fax_phone}
                onChange={handleChange}
                placeholder="Enter fax number"
                className={inputClass('fax_phone')}
              />
              {fieldError('fax_phone')}
            </div>
          </div>
        </section>

        {/* ============================================================
            ADDRESS SECTION
            ============================================================ */}
        <section
          className="mb-8 rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
          aria-labelledby="section-address"
        >
          <h2
            id="section-address"
            className="mb-4 text-lg font-semibold text-gray-800"
          >
            Address
          </h2>

          <div className="grid grid-cols-1 gap-6 md:grid-cols-2">
            {/* Street */}
            <div className="md:col-span-2">
              <label htmlFor="street" className={labelClass}>
                Street Address
              </label>
              <input
                id="street"
                name="street"
                type="text"
                value={formData.street}
                onChange={handleChange}
                placeholder="Enter street address"
                className={inputClass('street')}
              />
              {fieldError('street')}
            </div>

            {/* Street 2 */}
            <div className="md:col-span-2">
              <label htmlFor="street_2" className={labelClass}>
                Street Address 2
              </label>
              <input
                id="street_2"
                name="street_2"
                type="text"
                value={formData.street_2}
                onChange={handleChange}
                placeholder="Apt, suite, unit, etc."
                className={inputClass('street_2')}
              />
              {fieldError('street_2')}
            </div>

            {/* City */}
            <div>
              <label htmlFor="city" className={labelClass}>
                City
              </label>
              <input
                id="city"
                name="city"
                type="text"
                value={formData.city}
                onChange={handleChange}
                placeholder="Enter city"
                className={inputClass('city')}
              />
              {fieldError('city')}
            </div>

            {/* Region */}
            <div>
              <label htmlFor="region" className={labelClass}>
                Region / State
              </label>
              <input
                id="region"
                name="region"
                type="text"
                value={formData.region}
                onChange={handleChange}
                placeholder="Enter region or state"
                className={inputClass('region')}
              />
              {fieldError('region')}
            </div>

            {/* Post Code */}
            <div>
              <label htmlFor="post_code" className={labelClass}>
                Postal Code
              </label>
              <input
                id="post_code"
                name="post_code"
                type="text"
                value={formData.post_code}
                onChange={handleChange}
                placeholder="Enter postal code"
                className={inputClass('post_code')}
              />
              {fieldError('post_code')}
            </div>

            {/* Country */}
            <div>
              <label htmlFor="country_id" className={labelClass}>
                Country
              </label>
              <select
                id="country_id"
                name="country_id"
                value={formData.country_id}
                onChange={handleChange}
                className={inputClass('country_id')}
                disabled={countriesLoading}
                aria-busy={countriesLoading}
              >
                <option value="">
                  {countriesLoading ? 'Loading countries…' : 'Select country…'}
                </option>
                {(countriesData ?? []).map((c) => (
                  <option key={String(c.id)} value={String(c.id)}>
                    {String(c.name ?? '')}
                  </option>
                ))}
              </select>
              {fieldError('country_id')}
            </div>
          </div>
        </section>

        {/* ============================================================
            BUSINESS SECTION
            ============================================================ */}
        <section
          className="mb-8 rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
          aria-labelledby="section-business"
        >
          <h2
            id="section-business"
            className="mb-4 text-lg font-semibold text-gray-800"
          >
            Business
          </h2>

          <div className="grid grid-cols-1 gap-6 md:grid-cols-2">
            {/* Tax ID */}
            <div>
              <label htmlFor="tax_id" className={labelClass}>
                Tax ID
              </label>
              <input
                id="tax_id"
                name="tax_id"
                type="text"
                value={formData.tax_id}
                onChange={handleChange}
                placeholder="Enter tax identification number"
                className={inputClass('tax_id')}
              />
              {fieldError('tax_id')}
            </div>

            {/* Language */}
            <div>
              <label htmlFor="language_id" className={labelClass}>
                Language
              </label>
              <select
                id="language_id"
                name="language_id"
                value={formData.language_id}
                onChange={handleChange}
                className={inputClass('language_id')}
                disabled={languagesLoading}
                aria-busy={languagesLoading}
              >
                <option value="">
                  {languagesLoading
                    ? 'Loading languages…'
                    : 'Select language…'}
                </option>
                {(languagesData ?? []).map((l) => (
                  <option key={String(l.id)} value={String(l.id)}>
                    {String(l.name ?? '')}
                  </option>
                ))}
              </select>
              {fieldError('language_id')}
            </div>

            {/* Currency */}
            <div>
              <label htmlFor="currency_id" className={labelClass}>
                Currency
              </label>
              <select
                id="currency_id"
                name="currency_id"
                value={formData.currency_id}
                onChange={handleChange}
                className={inputClass('currency_id')}
                disabled={currenciesLoading}
                aria-busy={currenciesLoading}
              >
                <option value="">
                  {currenciesLoading
                    ? 'Loading currencies…'
                    : 'Select currency…'}
                </option>
                {(currenciesData ?? []).map((c) => (
                  <option key={String(c.id)} value={String(c.id)}>
                    {String(c.name ?? '')}
                  </option>
                ))}
              </select>
              {fieldError('currency_id')}
            </div>
          </div>
        </section>

        {/* ============================================================
            NOTES SECTION
            ============================================================ */}
        <section
          className="mb-8 rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
          aria-labelledby="section-notes"
        >
          <h2
            id="section-notes"
            className="mb-4 text-lg font-semibold text-gray-800"
          >
            Notes
          </h2>

          <div>
            <label htmlFor="notes" className={labelClass}>
              Notes
            </label>
            <textarea
              id="notes"
              name="notes"
              rows={4}
              value={formData.notes}
              onChange={handleChange}
              placeholder="Additional notes about this account…"
              className={inputClass('notes')}
            />
            {fieldError('notes')}
          </div>
        </section>

        {/* ============================================================
            ACTION BUTTONS
            ============================================================ */}
        <div className="flex items-center justify-end gap-4">
          <button
            type="button"
            onClick={handleCancel}
            disabled={isPending}
            className="rounded-md border border-gray-300 bg-white px-5 py-2.5 text-sm font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="submit"
            disabled={isPending}
            className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-5 py-2.5 text-sm font-medium text-white shadow-sm transition-colors hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isPending && (
              <svg
                className="h-4 w-4 animate-spin"
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
            )}
            {isPending ? 'Creating…' : 'Create Account'}
          </button>
        </div>
      </form>
    </main>
  );
}
