/**
 * ContactDetails — CRM Contact Detail View
 *
 * React page component displaying all contact fields with related accounts.
 * Replaces the monolith's RecordDetails.cshtml Razor Page for the contact entity.
 * Maps to route /crm/contacts/:id.
 *
 * Contact entity fields sourced from:
 *   - NextPlugin.20190204.cs (core fields + account_nn_contact relation)
 *   - NextPlugin.20190206.cs (photo, salutation_id, created_on, x_search)
 *   - Configuration.cs (ContactSearchIndexFields)
 *
 * Sections rendered:
 *   1. Photo & Identity (photo, salutation, first_name, last_name, job_title)
 *   2. Contact Information (email, fixed_phone, mobile_phone, fax_phone)
 *   3. Address (street, street_2, city, region, post_code, country)
 *   4. Notes (notes)
 *   5. Related Accounts sub-table (account_nn_contact ManyToMany)
 *   6. Action Buttons (Edit → /crm/contacts/:id/manage, Delete with confirmation)
 */

import { useState, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQuery, useMutation } from '@tanstack/react-query';
import apiClient from '../../api/client';
import Modal from '../../components/common/Modal';

/* ---------------------------------------------------------------------------
 * Local Type Definitions
 *
 * Defined locally because depends_on_files does not include types/record.ts.
 * These interfaces model the CRM API response shapes for a contact record
 * and its related account records.
 * --------------------------------------------------------------------------- */

/** Shape of a single contact record returned by GET /v1/crm/contacts/:id */
interface ContactRecord {
  id: string;
  first_name: string;
  last_name: string;
  salutation_id: string | null;
  job_title: string | null;
  email: string | null;
  fixed_phone: string | null;
  mobile_phone: string | null;
  fax_phone: string | null;
  street: string | null;
  street_2: string | null;
  city: string | null;
  region: string | null;
  post_code: string | null;
  country_id: string | null;
  photo: string | null;
  notes: string | null;
  created_on: string | null;
  /** Resolved salutation label (e.g. "Mr.", "Dr.") from salutation_1n_contact relation */
  $salutation_label?: string;
  /** Resolved country name from country_1n_contact relation */
  $country_label?: string;
}

/** Shape of a related account record in the account_nn_contact ManyToMany join */
interface RelatedAccount {
  id: string;
  name: string;
  type: string | null;
  email: string | null;
  city: string | null;
}

/** Standard API envelope from client.ts */
interface ApiEnvelope<T> {
  success: boolean;
  object: T;
  message: string | null;
  errors: Array<{ key: string; value: string; message: string }>;
  statusCode: number;
  timestamp: string;
  hash: string | null;
}

/* ---------------------------------------------------------------------------
 * Salutation Lookup Map
 *
 * Static lookup of salutation GUIDs → labels seeded in NextPlugin.20190206.cs.
 * Used as a client-side fallback when the API response does not include the
 * resolved $salutation_label field.
 * --------------------------------------------------------------------------- */
const SALUTATION_MAP: Record<string, string> = {
  '87c08ee1-8d4d-4c89-9b37-4e3cc3f98698': 'Mr.',
  '0ede7d96-2d85-45fa-818b-01327d4c47a9': 'Ms.',
  'ab073457-ddc8-4d36-84a5-38619528b578': 'Mrs.',
  '5b8d0137-9ec5-4b1c-a9b0-e982ef8698c1': 'Dr.',
  'a74cd934-b425-4061-8f4e-a6d6b9d7adb1': 'Prof.',
};

/* ---------------------------------------------------------------------------
 * Helper Utilities
 * --------------------------------------------------------------------------- */

/**
 * Formats an ISO-8601 date string into a user-friendly display string.
 * Mirrors the monolith's "yyyy-MMM-dd HH:mm" format from the contact
 * entity's created_on DateTimeField definition.
 */
function formatDateTime(isoString: string | null | undefined): string {
  if (!isoString) return '';
  try {
    const date = new Date(isoString);
    if (Number.isNaN(date.getTime())) return '';
    const options: Intl.DateTimeFormatOptions = {
      year: 'numeric',
      month: 'short',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
    };
    return new Intl.DateTimeFormat('en-US', options).format(date);
  } catch {
    return '';
  }
}

/**
 * Resolves the salutation label from a salutation_id GUID.
 * Prefers the server-resolved $salutation_label if present, otherwise
 * falls back to the static SALUTATION_MAP.
 */
function resolveSalutation(
  salutationId: string | null | undefined,
  serverLabel?: string,
): string {
  if (serverLabel) return serverLabel;
  if (!salutationId) return '';
  return SALUTATION_MAP[salutationId] ?? '';
}

/**
 * Composes the full display name from salutation + first_name + last_name.
 */
function composeDisplayName(
  salutation: string,
  firstName: string | null | undefined,
  lastName: string | null | undefined,
): string {
  const parts: string[] = [];
  if (salutation) parts.push(salutation);
  if (firstName) parts.push(firstName);
  if (lastName) parts.push(lastName);
  return parts.join(' ') || 'Unnamed Contact';
}

/**
 * Returns a safe display string, defaulting to an em-dash for empty values.
 */
function displayValue(value: string | null | undefined): string {
  return value?.trim() ? value.trim() : '—';
}

/* ---------------------------------------------------------------------------
 * Sub-components for Sections
 * --------------------------------------------------------------------------- */

/** Read-only label–value field row used across all detail sections */
function DetailField({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="grid grid-cols-1 gap-1 py-3 sm:grid-cols-3 sm:gap-4">
      <dt className="text-sm font-medium text-gray-500">{label}</dt>
      <dd className="text-sm text-gray-900 sm:col-span-2">{children}</dd>
    </div>
  );
}

/** Section card wrapper with optional title */
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
      <div className="px-4 py-2 sm:px-6">
        <dl className="divide-y divide-gray-100">{children}</dl>
      </div>
    </section>
  );
}

/* ---------------------------------------------------------------------------
 * Loading Skeleton
 * --------------------------------------------------------------------------- */
function ContactDetailsSkeleton() {
  return (
    <div className="animate-pulse space-y-6" role="status" aria-label="Loading contact details">
      {/* Header skeleton */}
      <div className="flex items-center gap-4">
        <div className="h-24 w-24 rounded-full bg-gray-200" />
        <div className="space-y-2">
          <div className="h-6 w-48 rounded bg-gray-200" />
          <div className="h-4 w-32 rounded bg-gray-200" />
        </div>
      </div>
      {/* Card skeletons */}
      {[1, 2, 3].map((i) => (
        <div key={i} className="h-48 rounded-lg bg-gray-200" />
      ))}
      <span className="sr-only">Loading…</span>
    </div>
  );
}

/* ---------------------------------------------------------------------------
 * Error Display
 * --------------------------------------------------------------------------- */
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
        className="mx-auto mb-3 h-10 w-10 text-red-400"
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
        Failed to load contact
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
function NotFoundDisplay() {
  return (
    <div className="mx-auto max-w-lg py-16 text-center">
      <p className="text-6xl font-bold text-gray-300">404</p>
      <h2 className="mt-4 text-xl font-semibold text-gray-900">
        Contact not found
      </h2>
      <p className="mt-2 text-sm text-gray-500">
        The contact you are looking for does not exist or has been deleted.
      </p>
      <Link
        to="/crm/contacts"
        className="mt-6 inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-indigo-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
      >
        Back to Contacts
      </Link>
    </div>
  );
}

/* ---------------------------------------------------------------------------
 * Photo & Identity Header
 * --------------------------------------------------------------------------- */
function PhotoIdentityHeader({
  contact,
  displayName,
}: {
  contact: ContactRecord;
  displayName: string;
}) {
  const salutation = resolveSalutation(
    contact.salutation_id,
    contact.$salutation_label,
  );

  return (
    <div className="flex flex-col items-center gap-6 sm:flex-row">
      {/* Contact Photo / Avatar */}
      <div className="flex-shrink-0">
        {contact.photo ? (
          <img
            src={contact.photo}
            alt={`Photo of ${displayName}`}
            width={96}
            height={96}
            className="h-24 w-24 rounded-full object-cover ring-2 ring-gray-200"
            loading="lazy"
            decoding="async"
            style={{ backgroundColor: '#e5e7eb' }}
          />
        ) : (
          <div
            className="flex h-24 w-24 items-center justify-center rounded-full bg-indigo-100 text-2xl font-bold text-indigo-600"
            aria-hidden="true"
          >
            {(contact.first_name?.[0] ?? '').toUpperCase()}
            {(contact.last_name?.[0] ?? '').toUpperCase()}
          </div>
        )}
      </div>

      {/* Identity Info */}
      <div className="min-w-0 flex-1 text-center sm:text-start">
        <h1 className="truncate text-2xl font-bold text-gray-900">
          {displayName}
        </h1>
        {salutation && (
          <span className="mt-1 inline-block rounded-full bg-indigo-50 px-2.5 py-0.5 text-xs font-medium text-indigo-700">
            {salutation}
          </span>
        )}
        {contact.job_title && (
          <p className="mt-1 text-sm text-gray-500">
            {contact.job_title}
          </p>
        )}
        {contact.created_on && (
          <p className="mt-1 text-xs text-gray-400">
            Created {formatDateTime(contact.created_on)}
          </p>
        )}
      </div>
    </div>
  );
}

/* ---------------------------------------------------------------------------
 * Related Accounts Sub-table
 * --------------------------------------------------------------------------- */
function RelatedAccountsTable({
  accounts,
  isLoading,
}: {
  accounts: RelatedAccount[];
  isLoading: boolean;
}) {
  if (isLoading) {
    return (
      <section className="overflow-hidden rounded-lg bg-white shadow">
        <div className="border-b border-gray-200 px-4 py-4 sm:px-6">
          <h2 className="text-base font-semibold text-gray-900">
            Related Accounts
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
          Related Accounts
          {accounts.length > 0 && (
            <span className="ms-2 inline-flex items-center rounded-full bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-600">
              {accounts.length}
            </span>
          )}
        </h2>
      </div>

      {accounts.length === 0 ? (
        <div className="px-4 py-8 text-center text-sm text-gray-500">
          No related accounts found.
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
                  Type
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
                  City
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 bg-white">
              {accounts.map((account) => (
                <tr
                  key={account.id}
                  className="transition-colors duration-150 hover:bg-gray-50"
                >
                  <td className="whitespace-nowrap px-4 py-3 text-sm">
                    <Link
                      to={`/crm/accounts/${account.id}`}
                      className="font-medium text-indigo-600 hover:text-indigo-500 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                    >
                      {account.name || 'Unnamed Account'}
                    </Link>
                  </td>
                  <td className="whitespace-nowrap px-4 py-3 text-sm text-gray-500">
                    {account.type ? (
                      <span className="inline-flex items-center rounded-full bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-700">
                        {account.type}
                      </span>
                    ) : (
                      '—'
                    )}
                  </td>
                  <td className="whitespace-nowrap px-4 py-3 text-sm text-gray-500">
                    {account.email ? (
                      <a
                        href={`mailto:${account.email}`}
                        className="text-indigo-600 hover:text-indigo-500"
                      >
                        {account.email}
                      </a>
                    ) : (
                      '—'
                    )}
                  </td>
                  <td className="whitespace-nowrap px-4 py-3 text-sm text-gray-500">
                    {displayValue(account.city)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/* ===========================================================================
 * ContactDetails — Main Page Component
 * =========================================================================== */

export default function ContactDetails() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();

  /* ---- State ---- */
  const [showDeleteModal, setShowDeleteModal] = useState(false);

  /* ---- Contact Data Query ---- */
  const {
    data: contactResponse,
    isLoading: isContactLoading,
    error: contactError,
    refetch: refetchContact,
  } = useQuery<ApiEnvelope<ContactRecord>>({
    queryKey: ['crm', 'contacts', id],
    queryFn: async () => {
      const response = await apiClient.get<ApiEnvelope<ContactRecord>>(
        `/v1/crm/contacts/${id}`,
      );
      return response.data;
    },
    enabled: !!id,
  });

  const contact = contactResponse?.object ?? null;

  /* ---- Related Accounts Query ---- */
  const {
    data: accountsResponse,
    isLoading: isAccountsLoading,
  } = useQuery<ApiEnvelope<RelatedAccount[]>>({
    queryKey: ['crm', 'contacts', id, 'accounts'],
    queryFn: async () => {
      const response = await apiClient.get<ApiEnvelope<RelatedAccount[]>>(
        `/v1/crm/contacts/${id}/accounts`,
      );
      return response.data;
    },
    enabled: !!contact,
  });

  const relatedAccounts = accountsResponse?.object ?? [];

  /* ---- Delete Mutation ---- */
  const deleteMutation = useMutation({
    mutationFn: async () => {
      const response = await apiClient.delete<ApiEnvelope<null>>(
        `/v1/crm/contacts/${id}`,
      );
      return response.data;
    },
    onSuccess: () => {
      navigate('/crm/contacts');
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
  const salutationLabel = contact
    ? resolveSalutation(contact.salutation_id, contact.$salutation_label)
    : '';
  const displayName = contact
    ? composeDisplayName(salutationLabel, contact.first_name, contact.last_name)
    : '';
  const countryDisplay = contact?.$country_label ?? displayValue(contact?.country_id);

  /* ---- Render: Loading ---- */
  if (isContactLoading) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <ContactDetailsSkeleton />
      </div>
    );
  }

  /* ---- Render: Error ---- */
  if (contactError) {
    const errorMessage =
      (contactError as Error)?.message ?? 'An unexpected error occurred.';
    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <ErrorDisplay message={errorMessage} onRetry={() => refetchContact()} />
      </div>
    );
  }

  /* ---- Render: 404 Not Found ---- */
  if (!contact) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
        <NotFoundDisplay />
      </div>
    );
  }

  /* ---- Render: Contact Detail Page ---- */
  return (
    <div className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
      {/* ---- Page Header: Photo + Identity + Action Buttons ---- */}
      <div className="mb-8 flex flex-col gap-6 sm:flex-row sm:items-start sm:justify-between">
        <PhotoIdentityHeader contact={contact} displayName={displayName} />

        {/* Action Buttons */}
        <div className="flex flex-shrink-0 gap-3 self-center sm:self-start">
          <Link
            to={`/crm/contacts/${id}/manage`}
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
            {deleteMutation.isPending ? 'Deleting…' : 'Delete'}
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
            'Could not delete the contact. Please try again.'}
        </div>
      )}

      {/* ---- Detail Sections ---- */}
      <div className="space-y-6">
        {/* Contact Information Section */}
        <SectionCard title="Contact Information">
          <DetailField label="Email">
            {contact.email ? (
              <a
                href={`mailto:${contact.email}`}
                className="text-indigo-600 hover:text-indigo-500"
              >
                {contact.email}
              </a>
            ) : (
              displayValue(null)
            )}
          </DetailField>
          <DetailField label="Fixed Phone">
            {contact.fixed_phone ? (
              <a
                href={`tel:${contact.fixed_phone}`}
                className="text-indigo-600 hover:text-indigo-500"
              >
                {contact.fixed_phone}
              </a>
            ) : (
              displayValue(null)
            )}
          </DetailField>
          <DetailField label="Mobile Phone">
            {contact.mobile_phone ? (
              <a
                href={`tel:${contact.mobile_phone}`}
                className="text-indigo-600 hover:text-indigo-500"
              >
                {contact.mobile_phone}
              </a>
            ) : (
              displayValue(null)
            )}
          </DetailField>
          <DetailField label="Fax">
            {displayValue(contact.fax_phone)}
          </DetailField>
        </SectionCard>

        {/* Address Section */}
        <SectionCard title="Address">
          <DetailField label="Street">
            {displayValue(contact.street)}
          </DetailField>
          {contact.street_2 && (
            <DetailField label="Street Line 2">
              {contact.street_2}
            </DetailField>
          )}
          <DetailField label="City">
            {displayValue(contact.city)}
          </DetailField>
          <DetailField label="State / Region">
            {displayValue(contact.region)}
          </DetailField>
          <DetailField label="Postal Code">
            {displayValue(contact.post_code)}
          </DetailField>
          <DetailField label="Country">
            {countryDisplay}
          </DetailField>
        </SectionCard>

        {/* Notes Section */}
        {contact.notes && (
          <SectionCard title="Notes">
            <div className="py-3 text-sm text-gray-900 whitespace-pre-wrap">
              {contact.notes}
            </div>
          </SectionCard>
        )}

        {/* Related Accounts Sub-table */}
        <RelatedAccountsTable
          accounts={relatedAccounts}
          isLoading={isAccountsLoading}
        />
      </div>

      {/* ---- Delete Confirmation Modal ---- */}
      <Modal
        isVisible={showDeleteModal}
        id="delete-contact-modal"
        title="Delete Contact"
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
              {deleteMutation.isPending ? 'Deleting…' : 'Delete Contact'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-500">
          Are you sure you want to delete{' '}
          <strong className="text-gray-700">{displayName}</strong>? This action
          cannot be undone. All associated data will be permanently removed.
        </p>
      </Modal>
    </div>
  );
}
