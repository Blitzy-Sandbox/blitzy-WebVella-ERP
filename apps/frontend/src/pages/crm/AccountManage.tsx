/**
 * AccountManage — Account Edit Form
 *
 * React page component for editing existing CRM accounts. Replaces the
 * monolith's `RecordManage.cshtml` Razor Page for the account entity.
 *
 * Route: `/crm/accounts/:id/manage`
 *
 * Source mappings:
 *  - `RecordManage.cshtml.cs`           → This component (form + mutations)
 *  - `NextPlugin.20190204.cs`           → Account entity field definitions
 *  - `NextPlugin.20190206.cs`           → Salutation entity, created_on field
 *  - `RecordManager.UpdateRecord()`     → {@link useUpdateAccount} mutation (PUT)
 *  - `RecordManager.DeleteRecord()`     → {@link useDeleteAccount} mutation (DELETE)
 *  - `IErpPreUpdateRecordHook`          → Lambda handler validation (server-side)
 *  - `SearchService.RegenSearchField`   → SNS event post-update (server-side)
 *
 * @module pages/crm/AccountManage
 */

import { useState, useEffect, useCallback, useMemo } from 'react';
import { useParams, useNavigate, useBlocker } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get } from '../../api/client';
import {
  useAccount,
  useUpdateAccount,
  useDeleteAccount,
  useSalutations,
} from '../../hooks/useCrm';
import type { EntityRecord, EntityRecordList } from '../../types/record';
import Modal from '../../components/common/Modal';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Typed form state for all editable account fields.
 *
 * Maps to the account entity fields defined in:
 *  - `NextPlugin.20190204.cs` — name, type, email, phone, address, business
 *  - `NextPlugin.20190206.cs` — salutation_id, created_on, x_search
 */
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

/** Per-field validation error messages. */
interface FormErrors {
  [field: string]: string;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * staleTime for reference / lookup data — 30 minutes (1 800 000 ms).
 * Countries, languages, and currencies change extremely rarely.
 */
const LOOKUP_STALE_TIME_MS = 30 * 60 * 1000;

/** Basic email format validation. */
const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** URL must start with http:// or https:// */
const URL_REGEX = /^https?:\/\/.+/i;

/** Empty form state used before account data arrives from the server. */
const EMPTY_FORM: AccountFormState = {
  name: '',
  type: 'company',
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

/**
 * Extracts form-compatible string values from a server-side {@link EntityRecord}.
 *
 * Every dynamic `unknown` value is coerced to a string, defaulting to an
 * empty string for nullish values.  This mirrors the implicit coercion the
 * monolith's Razor model-binding performed in `RecordManage.cshtml.cs`.
 */
function recordToFormState(record: EntityRecord): AccountFormState {
  const str = (key: string): string => {
    const v = record[key];
    if (v === null || v === undefined) return '';
    return String(v);
  };

  return {
    name: str('name'),
    type: str('type') || 'company',
    salutation_id: str('salutation_id'),
    first_name: str('first_name'),
    last_name: str('last_name'),
    email: str('email'),
    fixed_phone: str('fixed_phone'),
    mobile_phone: str('mobile_phone'),
    fax_phone: str('fax_phone'),
    website: str('website'),
    street: str('street'),
    street_2: str('street_2'),
    city: str('city'),
    region: str('region'),
    post_code: str('post_code'),
    country_id: str('country_id'),
    tax_id: str('tax_id'),
    language_id: str('language_id'),
    currency_id: str('currency_id'),
    notes: str('notes'),
  };
}

// ---------------------------------------------------------------------------
// Shared Tailwind class fragments
// ---------------------------------------------------------------------------

const INPUT_BASE =
  'mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm ' +
  'focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 ' +
  'focus-visible:outline-indigo-600';

const INPUT_NORMAL = `${INPUT_BASE} border-gray-300 text-gray-900`;
const INPUT_ERROR = `${INPUT_BASE} border-red-300 text-red-900`;
const INPUT_DISABLED = `${INPUT_NORMAL} bg-gray-100 cursor-not-allowed`;

const LABEL = 'block text-sm font-medium text-gray-700';
const REQUIRED_MARK = 'text-red-500';
const ERROR_TEXT = 'mt-1 text-xs text-red-600';

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Account edit form page component.
 *
 * Pre-populates all fields from the existing account record fetched via
 * {@link useAccount}, supports dirty-form detection with {@link useBlocker},
 * Company / Person conditional field display, lookup dropdowns for salutations
 * / countries / languages / currencies, and delete confirmation via {@link Modal}.
 *
 * Default export enables lazy loading from `router.tsx`:
 * ```ts
 * const AccountManage = lazy(() => import('./pages/crm/AccountManage'));
 * ```
 */
export default function AccountManage() {
  // -- Routing -----------------------------------------------------------------
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  // -- Server State ------------------------------------------------------------
  const {
    data: account,
    isLoading: isAccountLoading,
    isError: isAccountError,
    error: accountError,
  } = useAccount(id ?? '');

  const updateAccount = useUpdateAccount();
  const deleteAccount = useDeleteAccount();

  // -- Lookup Queries ----------------------------------------------------------
  const { data: salutationsData, isLoading: salutationsLoading } =
    useSalutations();

  const { data: countriesData } = useQuery<EntityRecordList, Error>({
    queryKey: ['lookup', 'countries'],
    queryFn: async (): Promise<EntityRecordList> => {
      const response = await get<EntityRecordList>('/crm/countries');
      if (typeof (response as Record<string, unknown>)?.success === 'boolean' && !(response as Record<string, unknown>).success) {
        throw new Error((response as Record<string, unknown>).message as string || 'Failed to fetch countries');
      }
      const raw = ((response as Record<string, unknown>)?.object ?? response) as Record<string, unknown>;
      const records = (raw?.records ?? raw?.data ?? raw?.items ?? []) as EntityRecord[];
      const totalCount = Number(raw?.totalCount ?? (raw?.meta as Record<string, unknown>)?.total ?? records.length);
      return { records, totalCount };
    },
    staleTime: LOOKUP_STALE_TIME_MS,
  });

  const { data: languagesData } = useQuery<EntityRecordList, Error>({
    queryKey: ['lookup', 'languages'],
    queryFn: async (): Promise<EntityRecordList> => {
      const response = await get<EntityRecordList>('/crm/languages');
      if (typeof (response as Record<string, unknown>)?.success === 'boolean' && !(response as Record<string, unknown>).success) {
        throw new Error((response as Record<string, unknown>).message as string || 'Failed to fetch languages');
      }
      const raw = ((response as Record<string, unknown>)?.object ?? response) as Record<string, unknown>;
      const records = (raw?.records ?? raw?.data ?? raw?.items ?? []) as EntityRecord[];
      const totalCount = Number(raw?.totalCount ?? (raw?.meta as Record<string, unknown>)?.total ?? records.length);
      return { records, totalCount };
    },
    staleTime: LOOKUP_STALE_TIME_MS,
  });

  const { data: currenciesData } = useQuery<EntityRecordList, Error>({
    queryKey: ['lookup', 'currencies'],
    queryFn: async (): Promise<EntityRecordList> => {
      const response = await get<EntityRecordList>('/crm/currencies');
      if (typeof (response as Record<string, unknown>)?.success === 'boolean' && !(response as Record<string, unknown>).success) {
        throw new Error((response as Record<string, unknown>).message as string || 'Failed to fetch currencies');
      }
      const raw = ((response as Record<string, unknown>)?.object ?? response) as Record<string, unknown>;
      const records = (raw?.records ?? raw?.data ?? raw?.items ?? []) as EntityRecord[];
      const totalCount = Number(raw?.totalCount ?? (raw?.meta as Record<string, unknown>)?.total ?? records.length);
      return { records, totalCount };
    },
    staleTime: LOOKUP_STALE_TIME_MS,
  });

  // -- Local State -------------------------------------------------------------
  const [formState, setFormState] = useState<AccountFormState>(EMPTY_FORM);
  const [originalState, setOriginalState] =
    useState<AccountFormState>(EMPTY_FORM);
  const [errors, setErrors] = useState<FormErrors>({});
  const [serverError, setServerError] = useState('');
  const [isFormInitialized, setIsFormInitialized] = useState(false);
  const [showDeleteModal, setShowDeleteModal] = useState(false);
  /** Prevents the dirty-form blocker from firing during intentional navigation. */
  const [isNavigating, setIsNavigating] = useState(false);

  // -- Initialise form from fetched account data --------------------------------
  useEffect(() => {
    if (account && !isFormInitialized) {
      const state = recordToFormState(account);
      setFormState(state);
      setOriginalState(state);
      setIsFormInitialized(true);
    }
  }, [account, isFormInitialized]);

  // -- Computed ----------------------------------------------------------------
  /** Show Person-specific fields (salutation, first/last name). */
  const isPerson = useMemo(
    () => formState.type === 'person',
    [formState.type],
  );

  /** True when the current form state differs from the original fetched data. */
  const isDirty = useMemo(
    () => JSON.stringify(formState) !== JSON.stringify(originalState),
    [formState, originalState],
  );

  // -- Navigation blocker (dirty-form warning) ---------------------------------
  const blocker = useBlocker(isDirty && !isNavigating);

  // -- Handlers ----------------------------------------------------------------

  /**
   * Returns a change handler for a given form field.
   * Clears any existing field-level error on edit and resets the server error.
   */
  const handleFieldChange = useCallback(
    (field: keyof AccountFormState) =>
      (
        e: React.ChangeEvent<
          HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement
        >,
      ) => {
        setFormState((prev) => ({ ...prev, [field]: e.target.value }));
        setErrors((prev) => {
          if (!prev[field]) return prev;
          const next = { ...prev };
          delete next[field];
          return next;
        });
        setServerError('');
      },
    [],
  );

  /** Validates current form state and populates `errors` map. */
  const validate = useCallback((): boolean => {
    const next: FormErrors = {};

    if (!formState.name.trim()) {
      next.name = 'Account name is required';
    }
    if (!formState.type) {
      next.type = 'Account type is required';
    }
    if (formState.email && !EMAIL_REGEX.test(formState.email)) {
      next.email = 'Please enter a valid email address';
    }
    if (formState.website && !URL_REGEX.test(formState.website)) {
      next.website =
        'Please enter a valid URL (e.g., https://example.com)';
    }

    setErrors(next);
    return Object.keys(next).length === 0;
  }, [formState]);

  /** Submits the form via PUT mutation and navigates to details on success. */
  const handleSubmit = useCallback(
    async (e: React.FormEvent) => {
      e.preventDefault();
      setServerError('');

      if (!validate() || !id) return;

      const payload: EntityRecord = { ...formState };

      try {
        await updateAccount.mutateAsync({ id, data: payload });
        setIsNavigating(true);
        navigate(`/crm/accounts/${id}`);
      } catch (err) {
        const message =
          err instanceof Error ? err.message : 'Failed to update account';
        setServerError(message);
      }
    },
    [formState, validate, id, updateAccount, navigate],
  );

  /** Navigates back to account detail view without saving. */
  const handleCancel = useCallback(() => {
    if (!isDirty) {
      navigate(`/crm/accounts/${id}`);
      return;
    }
    // If dirty, let the blocker handle the warning; navigate anyway
    setIsNavigating(true);
    navigate(`/crm/accounts/${id}`);
  }, [navigate, id, isDirty]);

  /** Opens the delete confirmation modal. */
  const handleDeleteClick = useCallback(() => {
    setShowDeleteModal(true);
  }, []);

  /** Executes deletion and redirects to account list. */
  const handleDeleteConfirm = useCallback(async () => {
    if (!id) return;
    try {
      await deleteAccount.mutateAsync(id);
      // Invalidate contact queries that may reference the deleted account
      queryClient.invalidateQueries({ queryKey: ['contacts'] });
      setIsNavigating(true);
      navigate('/crm/accounts');
    } catch {
      setServerError('Failed to delete account. Please try again.');
    } finally {
      setShowDeleteModal(false);
    }
  }, [id, deleteAccount, navigate, queryClient]);

  /** Closes the delete confirmation modal without action. */
  const handleDeleteCancel = useCallback(() => {
    setShowDeleteModal(false);
  }, []);

  // ---------------------------------------------------------------------------
  // Render: Loading skeleton
  // ---------------------------------------------------------------------------
  if (isAccountLoading) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <div className="animate-pulse space-y-6">
          <div className="h-8 w-56 rounded bg-gray-200" />
          {[1, 2, 3].map((section) => (
            <div
              key={section}
              className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
            >
              <div className="mb-4 h-5 w-40 rounded bg-gray-200" />
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                {[1, 2, 3, 4].map((field) => (
                  <div key={field} className="space-y-2">
                    <div className="h-4 w-24 rounded bg-gray-200" />
                    <div className="h-10 w-full rounded bg-gray-100" />
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>
      </div>
    );
  }

  // ---------------------------------------------------------------------------
  // Render: 404 / Error state
  // ---------------------------------------------------------------------------
  if (isAccountError) {
    const is404 =
      accountError?.message?.includes('missing data') ||
      accountError?.message?.includes('404');

    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <div className="rounded-lg border border-red-200 bg-red-50 p-8 text-center">
          <svg
            className="mx-auto h-12 w-12 text-red-400"
            fill="none"
            viewBox="0 0 24 24"
            strokeWidth={1.5}
            stroke="currentColor"
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M12 9v3.75m9-.75a9 9 0 11-18 0 9 9 0 0118 0zm-9 3.75h.008v.008H12v-.008z"
            />
          </svg>
          <h2 className="mt-4 text-lg font-semibold text-red-800">
            {is404 ? 'Account Not Found' : 'Error Loading Account'}
          </h2>
          <p className="mt-2 text-sm text-red-600">
            {is404
              ? 'The account you are looking for does not exist or has been deleted.'
              : accountError?.message || 'An unexpected error occurred.'}
          </p>
          <button
            type="button"
            onClick={() => navigate('/crm/accounts')}
            className="mt-6 inline-flex items-center rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
          >
            Back to Accounts
          </button>
        </div>
      </div>
    );
  }

  // ---------------------------------------------------------------------------
  // Render: Main form
  // ---------------------------------------------------------------------------
  return (
    <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
      {/* Page header */}
      <div className="mb-6">
        <h1 className="text-2xl font-bold tracking-tight text-gray-900">
          Edit Account
        </h1>
        <p className="mt-1 text-sm text-gray-500">
          Update the account details below. Fields marked with{' '}
          <span className={REQUIRED_MARK}>*</span> are required.
        </p>
      </div>

      {/* Server error banner */}
      {serverError && (
        <div
          className="mb-6 rounded-lg border border-red-200 bg-red-50 p-4"
          role="alert"
        >
          <div className="flex">
            <svg
              className="h-5 w-5 shrink-0 text-red-400"
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
            <p className="ms-3 text-sm text-red-700">{serverError}</p>
          </div>
        </div>
      )}

      <form onSubmit={handleSubmit} noValidate>
        {/* ============================================================= */}
        {/* Identity Section                                              */}
        {/* ============================================================= */}
        <section className="mb-6 overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 px-6 py-4">
            <h2 className="text-lg font-semibold text-gray-900">Identity</h2>
          </div>
          <div className="grid grid-cols-1 gap-x-6 gap-y-5 px-6 py-5 sm:grid-cols-2">
            {/* Account Type */}
            <div>
              <label htmlFor="account-type" className={LABEL}>
                Type <span className={REQUIRED_MARK}>*</span>
              </label>
              <select
                id="account-type"
                value={formState.type}
                onChange={handleFieldChange('type')}
                className={errors.type ? INPUT_ERROR : INPUT_NORMAL}
              >
                <option value="company">Company</option>
                <option value="person">Person</option>
              </select>
              {errors.type && (
                <p className={ERROR_TEXT} role="alert">
                  {errors.type}
                </p>
              )}
            </div>

            {/* Account Name */}
            <div>
              <label htmlFor="account-name" className={LABEL}>
                Account Name <span className={REQUIRED_MARK}>*</span>
              </label>
              <input
                id="account-name"
                type="text"
                value={formState.name}
                onChange={handleFieldChange('name')}
                className={errors.name ? INPUT_ERROR : INPUT_NORMAL}
              />
              {errors.name && (
                <p className={ERROR_TEXT} role="alert">
                  {errors.name}
                </p>
              )}
            </div>

            {/* Person-specific fields */}
            {isPerson && (
              <>
                {/* Salutation */}
                <div>
                  <label htmlFor="salutation" className={LABEL}>
                    Salutation
                  </label>
                  <select
                    id="salutation"
                    value={formState.salutation_id}
                    onChange={handleFieldChange('salutation_id')}
                    disabled={salutationsLoading}
                    className={
                      salutationsLoading ? INPUT_DISABLED : INPUT_NORMAL
                    }
                  >
                    <option value="">— Select —</option>
                    {salutationsData?.records?.map((s: EntityRecord) => (
                      <option key={String(s.id)} value={String(s.id)}>
                        {String(s['label'] ?? s['name'] ?? s.id)}
                      </option>
                    ))}
                  </select>
                </div>

                {/* First Name */}
                <div>
                  <label htmlFor="first-name" className={LABEL}>
                    First Name
                  </label>
                  <input
                    id="first-name"
                    type="text"
                    value={formState.first_name}
                    onChange={handleFieldChange('first_name')}
                    className={INPUT_NORMAL}
                  />
                </div>

                {/* Last Name */}
                <div>
                  <label htmlFor="last-name" className={LABEL}>
                    Last Name
                  </label>
                  <input
                    id="last-name"
                    type="text"
                    value={formState.last_name}
                    onChange={handleFieldChange('last_name')}
                    className={INPUT_NORMAL}
                  />
                </div>
              </>
            )}
          </div>
        </section>

        {/* ============================================================= */}
        {/* Contact Information Section                                    */}
        {/* ============================================================= */}
        <section className="mb-6 overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 px-6 py-4">
            <h2 className="text-lg font-semibold text-gray-900">
              Contact Information
            </h2>
          </div>
          <div className="grid grid-cols-1 gap-x-6 gap-y-5 px-6 py-5 sm:grid-cols-2">
            {/* Email */}
            <div>
              <label htmlFor="email" className={LABEL}>
                Email
              </label>
              <input
                id="email"
                type="email"
                value={formState.email}
                onChange={handleFieldChange('email')}
                className={errors.email ? INPUT_ERROR : INPUT_NORMAL}
              />
              {errors.email && (
                <p className={ERROR_TEXT} role="alert">
                  {errors.email}
                </p>
              )}
            </div>

            {/* Website */}
            <div>
              <label htmlFor="website" className={LABEL}>
                Website
              </label>
              <input
                id="website"
                type="url"
                value={formState.website}
                onChange={handleFieldChange('website')}
                placeholder="https://"
                className={errors.website ? INPUT_ERROR : INPUT_NORMAL}
              />
              {errors.website && (
                <p className={ERROR_TEXT} role="alert">
                  {errors.website}
                </p>
              )}
            </div>

            {/* Fixed Phone */}
            <div>
              <label htmlFor="fixed-phone" className={LABEL}>
                Fixed Phone
              </label>
              <input
                id="fixed-phone"
                type="tel"
                value={formState.fixed_phone}
                onChange={handleFieldChange('fixed_phone')}
                className={INPUT_NORMAL}
              />
            </div>

            {/* Mobile Phone */}
            <div>
              <label htmlFor="mobile-phone" className={LABEL}>
                Mobile Phone
              </label>
              <input
                id="mobile-phone"
                type="tel"
                value={formState.mobile_phone}
                onChange={handleFieldChange('mobile_phone')}
                className={INPUT_NORMAL}
              />
            </div>

            {/* Fax */}
            <div>
              <label htmlFor="fax-phone" className={LABEL}>
                Fax
              </label>
              <input
                id="fax-phone"
                type="tel"
                value={formState.fax_phone}
                onChange={handleFieldChange('fax_phone')}
                className={INPUT_NORMAL}
              />
            </div>
          </div>
        </section>

        {/* ============================================================= */}
        {/* Address Section                                                */}
        {/* ============================================================= */}
        <section className="mb-6 overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 px-6 py-4">
            <h2 className="text-lg font-semibold text-gray-900">Address</h2>
          </div>
          <div className="grid grid-cols-1 gap-x-6 gap-y-5 px-6 py-5 sm:grid-cols-2">
            {/* Street */}
            <div className="sm:col-span-2">
              <label htmlFor="street" className={LABEL}>
                Street
              </label>
              <input
                id="street"
                type="text"
                value={formState.street}
                onChange={handleFieldChange('street')}
                className={INPUT_NORMAL}
              />
            </div>

            {/* Street 2 */}
            <div className="sm:col-span-2">
              <label htmlFor="street-2" className={LABEL}>
                Street 2
              </label>
              <input
                id="street-2"
                type="text"
                value={formState.street_2}
                onChange={handleFieldChange('street_2')}
                className={INPUT_NORMAL}
              />
            </div>

            {/* City */}
            <div>
              <label htmlFor="city" className={LABEL}>
                City
              </label>
              <input
                id="city"
                type="text"
                value={formState.city}
                onChange={handleFieldChange('city')}
                className={INPUT_NORMAL}
              />
            </div>

            {/* Region */}
            <div>
              <label htmlFor="region" className={LABEL}>
                Region / State
              </label>
              <input
                id="region"
                type="text"
                value={formState.region}
                onChange={handleFieldChange('region')}
                className={INPUT_NORMAL}
              />
            </div>

            {/* Post Code */}
            <div>
              <label htmlFor="post-code" className={LABEL}>
                Post Code
              </label>
              <input
                id="post-code"
                type="text"
                value={formState.post_code}
                onChange={handleFieldChange('post_code')}
                className={INPUT_NORMAL}
              />
            </div>

            {/* Country */}
            <div>
              <label htmlFor="country" className={LABEL}>
                Country
              </label>
              <select
                id="country"
                value={formState.country_id}
                onChange={handleFieldChange('country_id')}
                className={INPUT_NORMAL}
              >
                <option value="">— Select —</option>
                {countriesData?.records?.map((c: EntityRecord) => (
                  <option key={String(c.id)} value={String(c.id)}>
                    {String(c['label'] ?? c['name'] ?? c.id)}
                  </option>
                ))}
              </select>
            </div>
          </div>
        </section>

        {/* ============================================================= */}
        {/* Business Information Section                                   */}
        {/* ============================================================= */}
        <section className="mb-6 overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 px-6 py-4">
            <h2 className="text-lg font-semibold text-gray-900">
              Business Information
            </h2>
          </div>
          <div className="grid grid-cols-1 gap-x-6 gap-y-5 px-6 py-5 sm:grid-cols-2">
            {/* Tax ID */}
            <div>
              <label htmlFor="tax-id" className={LABEL}>
                Tax ID
              </label>
              <input
                id="tax-id"
                type="text"
                value={formState.tax_id}
                onChange={handleFieldChange('tax_id')}
                className={INPUT_NORMAL}
              />
            </div>

            {/* Language */}
            <div>
              <label htmlFor="language" className={LABEL}>
                Language
              </label>
              <select
                id="language"
                value={formState.language_id}
                onChange={handleFieldChange('language_id')}
                className={INPUT_NORMAL}
              >
                <option value="">— Select —</option>
                {languagesData?.records?.map((l: EntityRecord) => (
                  <option key={String(l.id)} value={String(l.id)}>
                    {String(l['label'] ?? l['name'] ?? l.id)}
                  </option>
                ))}
              </select>
            </div>

            {/* Currency */}
            <div>
              <label htmlFor="currency" className={LABEL}>
                Currency
              </label>
              <select
                id="currency"
                value={formState.currency_id}
                onChange={handleFieldChange('currency_id')}
                className={INPUT_NORMAL}
              >
                <option value="">— Select —</option>
                {currenciesData?.records?.map((c: EntityRecord) => (
                  <option key={String(c.id)} value={String(c.id)}>
                    {String(c['label'] ?? c['name'] ?? c.id)}
                  </option>
                ))}
              </select>
            </div>
          </div>
        </section>

        {/* ============================================================= */}
        {/* Notes Section                                                  */}
        {/* ============================================================= */}
        <section className="mb-8 overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 px-6 py-4">
            <h2 className="text-lg font-semibold text-gray-900">Notes</h2>
          </div>
          <div className="px-6 py-5">
            <label htmlFor="notes" className={LABEL}>
              Notes
            </label>
            <textarea
              id="notes"
              rows={4}
              value={formState.notes}
              onChange={handleFieldChange('notes')}
              className={`${INPUT_NORMAL} resize-y`}
            />
          </div>
        </section>

        {/* ============================================================= */}
        {/* Form Actions                                                   */}
        {/* ============================================================= */}
        <div className="flex flex-col-reverse gap-3 border-t border-gray-200 pt-6 sm:flex-row sm:items-center sm:justify-between">
          {/* Danger zone — delete */}
          <button
            type="button"
            onClick={handleDeleteClick}
            disabled={deleteAccount.isPending}
            className="inline-flex items-center justify-center rounded-md border border-red-300 bg-white px-4 py-2 text-sm font-medium text-red-700 shadow-sm hover:bg-red-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {deleteAccount.isPending ? 'Deleting…' : 'Delete Account'}
          </button>

          {/* Primary actions */}
          <div className="flex gap-3">
            <button
              type="button"
              onClick={handleCancel}
              className="inline-flex items-center justify-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={updateAccount.isPending || !isDirty}
              className="inline-flex items-center justify-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {updateAccount.isPending ? 'Saving…' : 'Save Changes'}
            </button>
          </div>
        </div>
      </form>

      {/* ================================================================ */}
      {/* Delete Confirmation Modal                                        */}
      {/* ================================================================ */}
      <Modal
        isVisible={showDeleteModal}
        title="Delete Account"
        onClose={handleDeleteCancel}
        footer={
          <div className="flex justify-end gap-3">
            <button
              type="button"
              onClick={handleDeleteCancel}
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleDeleteConfirm}
              disabled={deleteAccount.isPending}
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {deleteAccount.isPending ? 'Deleting…' : 'Confirm Delete'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to delete this account? This action cannot be
          undone and all associated data will be permanently removed.
        </p>
      </Modal>

      {/* ================================================================ */}
      {/* Unsaved Changes Blocker Modal                                    */}
      {/* ================================================================ */}
      {blocker.state === 'blocked' && (
        <Modal
          isVisible
          title="Unsaved Changes"
          onClose={() => blocker.reset?.()}
          footer={
            <div className="flex justify-end gap-3">
              <button
                type="button"
                onClick={() => blocker.reset?.()}
                className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
              >
                Stay on Page
              </button>
              <button
                type="button"
                onClick={() => blocker.proceed?.()}
                className="rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
              >
                Discard Changes
              </button>
            </div>
          }
        >
          <p className="text-sm text-gray-600">
            You have unsaved changes. Are you sure you want to leave this page?
            Your changes will be lost.
          </p>
        </Modal>
      )}
    </div>
  );
}
