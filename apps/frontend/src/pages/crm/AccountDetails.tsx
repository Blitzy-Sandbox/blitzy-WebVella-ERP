/**
 * AccountDetails.tsx — Account Detail View
 *
 * React page component displaying all account fields, related contacts,
 * and action buttons. Replaces the monolith's RecordDetails.cshtml Razor Page
 * when viewing an account entity record.
 *
 * Route: /crm/accounts/:id
 *
 * Data Sources:
 *  - GET  /v1/crm/accounts/:id          → Account record
 *  - GET  /v1/crm/accounts/:id/contacts → Related contacts (ManyToMany)
 *  - DELETE /v1/crm/accounts/:id        → Delete account
 *
 * @module pages/crm/AccountDetails
 */

import { useState, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQuery, useMutation } from '@tanstack/react-query';
import apiClient from '../../api/client';
import Modal from '../../components/common/Modal';

/* ---------------------------------------------------------------------------
 * Types
 * --------------------------------------------------------------------------- */

/**
 * Account record shape returned from GET /v1/crm/accounts/:id.
 * All fields derived from NextPlugin.20190204.cs + NextPlugin.20190206.cs.
 * Lookup fields may include server-resolved $label companions.
 */
interface AccountRecord {
  id: string;
  /** Account display name */
  name: string | null;
  /** Select field value: "1" = Company, "2" = Person */
  type: string | null;
  /** GUID reference to salutation entity */
  salutation_id: string | null;
  /** Server-resolved salutation label (Mr., Ms., etc.) */
  $salutation_label?: string | null;
  first_name: string | null;
  last_name: string | null;
  email: string | null;
  fixed_phone: string | null;
  mobile_phone: string | null;
  fax_phone: string | null;
  website: string | null;
  street: string | null;
  street_2: string | null;
  city: string | null;
  region: string | null;
  post_code: string | null;
  /** GUID reference to country entity */
  country_id: string | null;
  /** Server-resolved country label */
  $country_label?: string | null;
  tax_id: string | null;
  /** GUID reference to language entity */
  language_id: string | null;
  /** Server-resolved language label */
  $language_label?: string | null;
  /** GUID reference to currency entity */
  currency_id: string | null;
  /** Server-resolved currency label */
  $currency_label?: string | null;
  notes: string | null;
  created_on: string | null;
}

/**
 * Related contact record shape from GET /v1/crm/accounts/:id/contacts.
 * Subset of contact entity fields for the sub-table display.
 */
interface RelatedContact {
  id: string;
  first_name: string | null;
  last_name: string | null;
  email: string | null;
  mobile_phone: string | null;
}

/**
 * Standard API response envelope matching the apiClient response shape.
 */
interface ApiEnvelope<T> {
  success: boolean;
  message: string;
  errors: Array<{ key: string; value: string; message: string }>;
  object: T;
  statusCode: number;
  timestamp: string;
  hash: string;
}

/* ---------------------------------------------------------------------------
 * Static Maps
 * --------------------------------------------------------------------------- */

/**
 * Salutation GUID → label map for client-side resolution fallback.
 * Salutations seeded in NextPlugin.20190206.cs.
 */
const SALUTATION_MAP: Record<string, string> = {
  '87c08ee1-8d4d-4c89-9b37-4e3cc3f98698': 'Mr.',
  'aa345e28-a8e1-485f-b42f-719ef77b2a4f': 'Ms.',
  '89c6e0c6-d5c4-42f1-82a7-7e283e5e9e0c': 'Mrs.',
  'a7c6bbe5-e1a7-4a25-bada-a4b5a5a55d21': 'Dr.',
  'f1c2ad8d-3155-4e0c-bc92-4ee41ec94bc1': 'Prof.',
};

/**
 * Account type Select field value → label map.
 * Values from NextPlugin.20190204.cs selectOption "1"=Company, "2"=Person.
 */
const ACCOUNT_TYPE_MAP: Record<string, string> = {
  '1': 'Company',
  '2': 'Person',
};

/* ---------------------------------------------------------------------------
 * Helper Functions
 * --------------------------------------------------------------------------- */

/**
 * Format an ISO date string to "yyyy-MMM-dd HH:mm" style using Intl.
 * Mirrors the monolith's DateTime format convention.
 */
function formatDateTime(iso: string): string {
  try {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    return new Intl.DateTimeFormat('en-US', {
      year: 'numeric',
      month: 'short',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    }).format(date);
  } catch {
    return iso;
  }
}

/**
 * Resolve salutation label. Prefers server-provided $salutation_label,
 * falls back to static SALUTATION_MAP lookup by GUID.
 */
function resolveSalutation(
  salutationId: string | null | undefined,
  serverLabel: string | null | undefined,
): string {
  if (serverLabel?.trim()) return serverLabel.trim();
  if (salutationId && SALUTATION_MAP[salutationId]) {
    return SALUTATION_MAP[salutationId];
  }
  return '';
}

/**
 * Resolve account type label from the Select field value.
 */
function resolveAccountType(typeValue: string | null | undefined): string {
  if (!typeValue?.trim()) return '';
  return ACCOUNT_TYPE_MAP[typeValue.trim()] ?? typeValue.trim();
}

/**
 * Returns the trimmed non-empty value or an em-dash placeholder.
 */
function displayValue(value: string | null | undefined): string {
  const trimmed = value?.trim();
  return trimmed || '\u2014';
}

/* ---------------------------------------------------------------------------
 * Sub-Components: Layout Primitives
 * --------------------------------------------------------------------------- */

/**
 * Renders a label-value pair in a responsive grid row.
 * Used inside SectionCard for each account field.
 */
function DetailField({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="grid grid-cols-1 gap-1 px-4 py-3 sm:grid-cols-3 sm:gap-4 sm:px-6">
      <dt className="text-sm font-medium text-gray-500">{label}</dt>
      <dd className="text-sm text-gray-900 sm:col-span-2">{children}</dd>
    </div>
  );
}

/**
 * Card wrapper for a logical field section with a title header.
 * Groups related account fields (Identity, Contact Info, Address, etc.).
 */
function SectionCard({
  title,
  children,
}: {
  title: string;
  children: React.ReactNode;
}) {
  return (
    <section className="overflow-hidden rounded-lg bg-white shadow">
      <div className="border-b border-gray-200 px-4 py-4 sm:px-6">
        <h2 className="text-base font-semibold text-gray-900">{title}</h2>
      </div>
      <dl className="divide-y divide-gray-100">{children}</dl>
    </section>
  );
}

/* ---------------------------------------------------------------------------
 * Loading Skeleton
 * --------------------------------------------------------------------------- */

/**
 * Pulsing placeholder skeleton shown while account data is loading.
 */
function AccountDetailsSkeleton() {
  return (
    <div
      className="animate-pulse"
      aria-busy="true"
      aria-label="Loading account details"
    >
      {/* Header skeleton */}
      <div className="mb-8 flex items-start justify-between">
        <div className="min-w-0 flex-1">
          <div className="h-8 w-64 rounded bg-gray-200" />
          <div className="mt-2 flex gap-2">
            <div className="h-5 w-20 rounded-full bg-gray-200" />
            <div className="h-5 w-16 rounded-full bg-gray-200" />
          </div>
          <div className="mt-2 h-3 w-36 rounded bg-gray-200" />
        </div>
        <div className="flex gap-3">
          <div className="h-9 w-20 rounded-md bg-gray-200" />
          <div className="h-9 w-24 rounded-md bg-gray-200" />
        </div>
      </div>

      {/* Section cards skeleton — Identity, Contact, Address, Business */}
      {[1, 2, 3, 4].map((n) => (
        <div key={n} className="mb-6 overflow-hidden rounded-lg bg-white shadow">
          <div className="border-b border-gray-200 px-4 py-4 sm:px-6">
            <div className="h-5 w-40 rounded bg-gray-200" />
          </div>
          <div className="space-y-4 p-4 sm:p-6">
            <div className="h-4 w-full rounded bg-gray-200" />
            <div className="h-4 w-3/4 rounded bg-gray-200" />
            <div className="h-4 w-1/2 rounded bg-gray-200" />
          </div>
        </div>
      ))}
    </div>
  );
}

/* ---------------------------------------------------------------------------
 * Error Display
 * --------------------------------------------------------------------------- */

/**
 * Red alert banner shown when the account data fetch fails.
 * Includes an optional retry button to re-trigger the query.
 */
function ErrorDisplay({
  message,
  onRetry,
}: {
  message: string;
  onRetry?: () => void;
}) {
  return (
    <div
      role="alert"
      className="mx-auto max-w-lg rounded-lg border border-red-200 bg-red-50 p-6 text-center"
    >
      <svg
        className="mx-auto h-10 w-10 text-red-400"
        fill="none"
        viewBox="0 0 24 24"
        stroke="currentColor"
        aria-hidden="true"
      >
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth={2}
          d="M12 9v2m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
        />
      </svg>
      <h3 className="mb-1 text-lg font-semibold text-red-800">
        Failed to load account
      </h3>
      <p className="mb-4 text-sm text-red-600">{message}</p>
      {onRetry && (
        <button
          type="button"
          onClick={onRetry}
          className="inline-flex items-center rounded-md bg-red-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
        >
          Try again
        </button>
      )}
    </div>
  );
}

/* ---------------------------------------------------------------------------
 * Not Found Display (404)
 * --------------------------------------------------------------------------- */

/**
 * Displayed when the account is not found (null response after successful fetch).
 */
function NotFoundDisplay() {
  return (
    <div className="mx-auto max-w-lg py-16 text-center">
      <p className="text-6xl font-bold text-gray-300">404</p>
      <h2 className="mt-4 text-xl font-semibold text-gray-900">
        Account not found
      </h2>
      <p className="mt-2 text-sm text-gray-500">
        The account you are looking for does not exist or has been deleted.
      </p>
      <Link
        to="/crm/accounts"
        className="mt-6 inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-indigo-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
      >
        Back to Accounts
      </Link>
    </div>
  );
}

/* ---------------------------------------------------------------------------
 * Account Identity Header
 * --------------------------------------------------------------------------- */

/**
 * Renders the account name, type badge, salutation badge, and creation timestamp.
 * Positioned at the top of the detail page alongside action buttons.
 */
function AccountIdentityHeader({
  account,
}: {
  account: AccountRecord;
}) {
  const salutation = resolveSalutation(
    account.salutation_id,
    account.$salutation_label,
  );
  const accountType = resolveAccountType(account.type);

  return (
    <div className="min-w-0 flex-1">
      <h1 className="truncate text-2xl font-bold text-gray-900">
        {account.name?.trim() || 'Unnamed Account'}
      </h1>
      <div className="mt-1 flex flex-wrap items-center gap-2">
        {accountType && (
          <span className="inline-flex items-center rounded-full bg-indigo-50 px-2.5 py-0.5 text-xs font-medium text-indigo-700">
            {accountType}
          </span>
        )}
        {salutation && (
          <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-700">
            {salutation}
          </span>
        )}
      </div>
      {account.created_on && (
        <p className="mt-1 text-xs text-gray-400">
          Created {formatDateTime(account.created_on)}
        </p>
      )}
    </div>
  );
}

/* ---------------------------------------------------------------------------
 * Related Contacts Sub-table
 * --------------------------------------------------------------------------- */

/**
 * Renders a table of contacts related to the current account via the
 * account_nn_contact ManyToMany relation. Each contact name links to its
 * detail page at /crm/contacts/:contactId.
 */
function RelatedContactsTable({
  contacts,
  isLoading,
}: {
  contacts: RelatedContact[];
  isLoading: boolean;
}) {
  if (isLoading) {
    return (
      <section className="overflow-hidden rounded-lg bg-white shadow">
        <div className="border-b border-gray-200 px-4 py-4 sm:px-6">
          <h2 className="text-base font-semibold text-gray-900">
            Related Contacts
          </h2>
        </div>
        <div className="animate-pulse p-6">
          <div className="h-4 w-full rounded bg-gray-200" />
          <div className="mt-3 h-4 w-3/4 rounded bg-gray-200" />
        </div>
      </section>
    );
  }

  return (
    <section className="overflow-hidden rounded-lg bg-white shadow">
      <div className="border-b border-gray-200 px-4 py-4 sm:px-6">
        <h2 className="text-base font-semibold text-gray-900">
          Related Contacts
          {contacts.length > 0 && (
            <span className="ms-2 inline-flex items-center rounded-full bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-600">
              {contacts.length}
            </span>
          )}
        </h2>
      </div>

      {contacts.length === 0 ? (
        <div className="px-4 py-8 text-center text-sm text-gray-500">
          No related contacts found.
        </div>
      ) : (
        <div className="overflow-x-auto">
          <table className="min-w-full divide-y divide-gray-200">
            <thead className="bg-gray-50">
              <tr>
                <th
                  scope="col"
                  className="px-4 py-3 text-start text-xs font-medium uppercase tracking-wider text-gray-500"
                >
                  Name
                </th>
                <th
                  scope="col"
                  className="px-4 py-3 text-start text-xs font-medium uppercase tracking-wider text-gray-500"
                >
                  Email
                </th>
                <th
                  scope="col"
                  className="px-4 py-3 text-start text-xs font-medium uppercase tracking-wider text-gray-500"
                >
                  Mobile Phone
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 bg-white">
              {contacts.map((contact) => {
                const fullName =
                  [contact.first_name, contact.last_name]
                    .filter(Boolean)
                    .join(' ')
                    .trim() || 'Unnamed Contact';

                return (
                  <tr
                    key={contact.id}
                    className="transition-colors duration-150 hover:bg-gray-50"
                  >
                    <td className="whitespace-nowrap px-4 py-3 text-sm">
                      <Link
                        to={`/crm/contacts/${contact.id}`}
                        className="font-medium text-indigo-600 hover:text-indigo-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                      >
                        {fullName}
                      </Link>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-sm text-gray-500">
                      {contact.email ? (
                        <a
                          href={`mailto:${contact.email}`}
                          className="text-indigo-600 hover:text-indigo-500"
                        >
                          {contact.email}
                        </a>
                      ) : (
                        '\u2014'
                      )}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-sm text-gray-500">
                      {contact.mobile_phone ? (
                        <a
                          href={`tel:${contact.mobile_phone}`}
                          className="text-indigo-600 hover:text-indigo-500"
                        >
                          {contact.mobile_phone}
                        </a>
                      ) : (
                        '\u2014'
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/* ===========================================================================
 * AccountDetails — Main Page Component
 * =========================================================================== */

/**
 * Account detail page component. Fetches and displays a single CRM account
 * record with all its fields organized into card-based sections (Identity,
 * Contact Information, Address, Business Information, Notes), plus a related
 * contacts sub-table and Edit/Delete action buttons.
 *
 * Replaces the monolith's RecordDetails.cshtml.cs for account entities,
 * converting server-side RecordManager.Find() and OnPost() delete to
 * client-side TanStack Query operations against the CRM microservice.
 */
export default function AccountDetails() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();

  /* ---- State ---- */
  const [showDeleteModal, setShowDeleteModal] = useState(false);

  /* ---- Account Data Query ---- */
  const {
    data: accountResponse,
    isLoading: isAccountLoading,
    error: accountError,
    refetch: refetchAccount,
  } = useQuery<ApiEnvelope<AccountRecord>>({
    queryKey: ['crm', 'accounts', id],
    queryFn: async () => {
      const response = await apiClient.get<ApiEnvelope<AccountRecord>>(
        `/v1/crm/accounts/${id}`,
      );
      return response.data;
    },
    enabled: !!id,
  });

  const account = accountResponse?.object ?? null;

  /* ---- Related Contacts Query ---- */
  const { data: contactsResponse, isLoading: isContactsLoading } =
    useQuery<ApiEnvelope<RelatedContact[]>>({
      queryKey: ['crm', 'accounts', id, 'contacts'],
      queryFn: async () => {
        const response = await apiClient.get<ApiEnvelope<RelatedContact[]>>(
          `/v1/crm/accounts/${id}/contacts`,
        );
        return response.data;
      },
      enabled: !!account,
    });

  const relatedContacts = contactsResponse?.object ?? [];

  /* ---- Delete Mutation ---- */
  const deleteMutation = useMutation({
    mutationFn: async () => {
      const response = await apiClient.delete<ApiEnvelope<null>>(
        `/v1/crm/accounts/${id}`,
      );
      return response.data;
    },
    onSuccess: () => {
      navigate('/crm/accounts');
    },
  });

  /* ---- Event Handlers ---- */

  /** Opens the delete confirmation modal */
  const handleDeleteClick = useCallback(() => {
    setShowDeleteModal(true);
  }, []);

  /** Closes the delete confirmation modal */
  const handleDeleteCancel = useCallback(() => {
    setShowDeleteModal(false);
  }, []);

  /** Executes the delete mutation and closes the modal */
  const handleDeleteConfirm = useCallback(() => {
    deleteMutation.mutate();
    setShowDeleteModal(false);
  }, [deleteMutation]);

  /* ---- Derived Values ---- */
  const accountDisplayName = account?.name?.trim() || 'Unnamed Account';
  const countryDisplay =
    account?.$country_label ?? displayValue(account?.country_id);
  const languageDisplay =
    account?.$language_label ?? displayValue(account?.language_id);
  const currencyDisplay =
    account?.$currency_label ?? displayValue(account?.currency_id);

  /* ---- Render: Loading ---- */
  if (isAccountLoading) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <AccountDetailsSkeleton />
      </div>
    );
  }

  /* ---- Render: Error ---- */
  if (accountError) {
    const errorMessage =
      (accountError as Error)?.message ?? 'An unexpected error occurred.';
    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <ErrorDisplay
          message={errorMessage}
          onRetry={() => refetchAccount()}
        />
      </div>
    );
  }

  /* ---- Render: 404 Not Found ---- */
  if (!account) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <NotFoundDisplay />
      </div>
    );
  }

  /* ---- Render: Account Detail Page ---- */
  return (
    <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
      {/* ---- Page Header: Identity + Action Buttons ---- */}
      <div className="mb-8 flex flex-col gap-6 sm:flex-row sm:items-start sm:justify-between">
        <AccountIdentityHeader account={account} />

        {/* Action Buttons */}
        <div className="flex flex-shrink-0 gap-3 self-center sm:self-start">
          <Link
            to={`/crm/accounts/${id}/manage`}
            className="inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-indigo-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          >
            <svg
              className="-ms-0.5 me-1.5 h-4 w-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"
              />
            </svg>
            Edit
          </Link>
          <button
            type="button"
            onClick={handleDeleteClick}
            disabled={deleteMutation.isPending}
            className="inline-flex items-center rounded-md bg-red-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:pointer-events-none disabled:opacity-50"
          >
            <svg
              className="-ms-0.5 me-1.5 h-4 w-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
              />
            </svg>
            {deleteMutation.isPending ? 'Deleting\u2026' : 'Delete'}
          </button>
        </div>
      </div>

      {/* ---- Delete Error Banner ---- */}
      {deleteMutation.isError && (
        <div
          role="alert"
          className="mb-6 rounded-md border border-red-200 bg-red-50 p-4 text-sm text-red-700"
        >
          <strong>Delete failed:</strong>{' '}
          {(deleteMutation.error as Error)?.message ??
            'Could not delete the account. Please try again.'}
        </div>
      )}

      {/* ---- Detail Sections ---- */}
      <div className="space-y-6">
        {/* Identity Section */}
        <SectionCard title="Identity">
          <DetailField label="Account Name">
            {displayValue(account.name)}
          </DetailField>
          <DetailField label="Type">
            {resolveAccountType(account.type) ? (
              <span className="inline-flex items-center rounded-full bg-indigo-50 px-2.5 py-0.5 text-xs font-medium text-indigo-700">
                {resolveAccountType(account.type)}
              </span>
            ) : (
              displayValue(null)
            )}
          </DetailField>
          <DetailField label="Salutation">
            {resolveSalutation(
              account.salutation_id,
              account.$salutation_label,
            ) || '\u2014'}
          </DetailField>
          <DetailField label="First Name">
            {displayValue(account.first_name)}
          </DetailField>
          <DetailField label="Last Name">
            {displayValue(account.last_name)}
          </DetailField>
        </SectionCard>

        {/* Contact Information Section */}
        <SectionCard title="Contact Information">
          <DetailField label="Email">
            {account.email ? (
              <a
                href={`mailto:${account.email}`}
                className="text-indigo-600 hover:text-indigo-500"
              >
                {account.email}
              </a>
            ) : (
              displayValue(null)
            )}
          </DetailField>
          <DetailField label="Fixed Phone">
            {account.fixed_phone ? (
              <a
                href={`tel:${account.fixed_phone}`}
                className="text-indigo-600 hover:text-indigo-500"
              >
                {account.fixed_phone}
              </a>
            ) : (
              displayValue(null)
            )}
          </DetailField>
          <DetailField label="Mobile Phone">
            {account.mobile_phone ? (
              <a
                href={`tel:${account.mobile_phone}`}
                className="text-indigo-600 hover:text-indigo-500"
              >
                {account.mobile_phone}
              </a>
            ) : (
              displayValue(null)
            )}
          </DetailField>
          <DetailField label="Fax">
            {displayValue(account.fax_phone)}
          </DetailField>
          <DetailField label="Website">
            {account.website ? (
              <a
                href={
                  account.website.startsWith('http')
                    ? account.website
                    : `https://${account.website}`
                }
                target="_blank"
                rel="noopener noreferrer"
                className="text-indigo-600 hover:text-indigo-500"
              >
                {account.website}
                <span className="sr-only"> (opens in new tab)</span>
              </a>
            ) : (
              displayValue(null)
            )}
          </DetailField>
        </SectionCard>

        {/* Address Section */}
        <SectionCard title="Address">
          <DetailField label="Street">
            {displayValue(account.street)}
          </DetailField>
          {account.street_2 && (
            <DetailField label="Street Line 2">
              {account.street_2}
            </DetailField>
          )}
          <DetailField label="City">
            {displayValue(account.city)}
          </DetailField>
          <DetailField label="State / Region">
            {displayValue(account.region)}
          </DetailField>
          <DetailField label="Postal Code">
            {displayValue(account.post_code)}
          </DetailField>
          <DetailField label="Country">{countryDisplay}</DetailField>
        </SectionCard>

        {/* Business Information Section */}
        <SectionCard title="Business Information">
          <DetailField label="Tax ID">
            {displayValue(account.tax_id)}
          </DetailField>
          <DetailField label="Language">{languageDisplay}</DetailField>
          <DetailField label="Currency">{currencyDisplay}</DetailField>
        </SectionCard>

        {/* Notes Section — only rendered when notes exist */}
        {account.notes && (
          <SectionCard title="Notes">
            <div className="whitespace-pre-wrap px-4 py-3 text-sm text-gray-900 sm:px-6">
              {account.notes}
            </div>
          </SectionCard>
        )}

        {/* Related Contacts Sub-table */}
        <RelatedContactsTable
          contacts={relatedContacts}
          isLoading={isContactsLoading}
        />
      </div>

      {/* ---- Delete Confirmation Modal ---- */}
      <Modal
        isVisible={showDeleteModal}
        id="delete-account-modal"
        title="Delete Account"
        onClose={handleDeleteCancel}
        footer={
          <div className="flex justify-end gap-3">
            <button
              type="button"
              onClick={handleDeleteCancel}
              className="rounded-md bg-white px-3 py-2 text-sm font-semibold text-gray-900 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleDeleteConfirm}
              disabled={deleteMutation.isPending}
              className="rounded-md bg-red-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:pointer-events-none disabled:opacity-50"
            >
              {deleteMutation.isPending ? 'Deleting\u2026' : 'Delete Account'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-500">
          Are you sure you want to delete{' '}
          <strong className="text-gray-700">{accountDisplayName}</strong>? This
          action cannot be undone. All associated data will be permanently
          removed.
        </p>
      </Modal>
    </div>
  );
}
