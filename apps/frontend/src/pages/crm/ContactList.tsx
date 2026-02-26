/**
 * ContactList — CRM Contact Listing Page
 *
 * Replaces the monolith's contact entity listing driven by
 * `RecordList.cshtml` Razor Page with contact entity configuration from
 * `WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs` (initial creation)
 * and `NextPlugin.20190206.cs` (salutation, photo, created_on, x_search).
 *
 * Features:
 * - Server-side pagination via page/pageSize URL search parameters
 * - Server-side sorting via sortBy/sortOrder URL search parameters
 * - Debounced text search via search URL parameter (searches x_search field)
 * - Salutation resolution via useSalutations hook (30-min staleTime)
 * - Photo thumbnail rendering in first column
 * - Navigate to contact detail (/crm/contacts/:id) via name link or row action
 * - Create new contact navigation link (/crm/contacts/create)
 *
 * Display columns (9):
 *   Photo | Name | Salutation | Email | Phone | Job Title | City | Account | Created On
 *
 * Source mapping:
 *  - NextPlugin.20190204.cs → Contact entity fields (first_name, last_name,
 *    email, job_title, phones, city, etc.)
 *  - NextPlugin.20190206.cs → photo (InputImageField), salutation_id,
 *    created_on (DateTime), x_search
 *  - Configuration.cs → ContactSearchIndexFields (x_search composition)
 *  - SearchService.cs → Server-side search via x_search denormalised field
 *  - RecordList.cshtml → Route pattern and page layout (replaced by React)
 *
 * Route: /crm/contacts
 *
 * @module pages/crm/ContactList
 */

import { useState, useMemo, useCallback, useEffect } from 'react';
import { useNavigate, Link, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import apiClient from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import { useSalutations } from '../../hooks/useCrm';
import type { EntityRecord, EntityRecordList } from '../../types/record';
import type { ApiResponse } from '../../api/client';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Search input debounce delay in milliseconds.
 * Prevents excessive API calls while the user is still typing.
 * Mirrors the 300ms delay specified in the AAP for the x_search
 * denormalised field search pattern from
 * `WebVella.Erp.Plugins.Next/Services/SearchService.cs`.
 */
const SEARCH_DEBOUNCE_MS = 300;

/** Default number of records displayed per page. */
const DEFAULT_PAGE_SIZE = 10;

/**
 * Default sort field for contact listing.
 *
 * Contacts are sorted by last_name ascending by default, following the
 * conventional alphabetical directory ordering. This replaces the
 * monolith's EQL `ORDER BY last_name ASC` default from RecordList.cshtml.
 */
const DEFAULT_SORT_FIELD = 'last_name';

/**
 * TanStack Query stale time for the contact list query (30 seconds).
 * Matches the CRM_DEFAULT_STALE_TIME_MS used by useContacts in useCrm.ts.
 */
const CONTACTS_STALE_TIME_MS = 30_000;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Formats a date/datetime value for display in the Created On column.
 *
 * Uses the browser's locale-aware `Intl.DateTimeFormat` for consistent
 * rendering. Falls back to the raw string on parse failure.
 *
 * Replaces the monolith's `"yyyy-MMM-dd HH:mm"` C# format from
 * `NextPlugin.20190206.cs`.
 */
function formatDate(value: unknown): string {
  if (value == null || value === '') return '';
  try {
    return new Intl.DateTimeFormat(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    }).format(new Date(String(value)));
  } catch {
    return String(value);
  }
}

/**
 * Builds the full display name for a contact record from first_name and
 * last_name fields. Handles missing/null fields gracefully.
 */
function buildContactName(record: EntityRecord): string {
  const first = String(record.first_name ?? '').trim();
  const last = String(record.last_name ?? '').trim();
  if (first && last) return `${first} ${last}`;
  return first || last || '';
}

/**
 * Extracts the best available phone number from a contact record.
 *
 * Prefers `mobile_phone` over `fixed_phone`, matching the monolith's
 * phone display priority from `NextPlugin.20190204.cs` where
 * mobile_phone was the first phone field defined.
 */
function getPhoneNumber(record: EntityRecord): string {
  const mobile = String(record.mobile_phone ?? '').trim();
  if (mobile) return mobile;
  return String(record.fixed_phone ?? '').trim();
}

/**
 * Extracts the related account name from a contact record.
 *
 * In the monolith, this came from the `$account_nn_contact.name`
 * relation navigation in EQL. The CRM microservice denormalises this
 * into the contact record as `account_name`. Fallback checks the
 * nested `$account_nn_contact` array that may be returned by the
 * Entity Management service's relation resolution.
 */
function getAccountName(record: EntityRecord): string {
  // Preferred: denormalised field from API
  const direct = record.account_name;
  if (direct != null && direct !== '') return String(direct);

  // Fallback: nested relation array (Entity Management pattern)
  const relation = record['$account_nn_contact'];
  if (Array.isArray(relation) && relation.length > 0) {
    const first = relation[0] as EntityRecord | undefined;
    if (first?.name != null) return String(first.name);
  }

  return '';
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * ContactList page component.
 *
 * Displays a searchable, sortable, paginated table of CRM contact
 * records. Uses TanStack Query for data fetching with caching, the
 * shared {@link DataTable} component for rendering, and the
 * {@link useSalutations} hook for salutation label resolution.
 *
 * All query state (search, page, pageSize, sortBy, sortOrder) is stored
 * in URL search parameters — enabling shareable URLs and browser
 * back/forward navigation. This mirrors the monolith's query-string
 * parameter pattern from `PcGrid` / `WvFilterBase`.
 *
 * Lazy-loaded via React Router:
 * ```tsx
 * const ContactList = lazy(() => import('./pages/crm/ContactList'));
 * ```
 */
function ContactList(): React.ReactElement {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  // ── Derive query state from URL search params ──────────────────────
  // These values drive the TanStack Query key and the API call parameters.
  // DataTable also reads/writes these params for pagination and sorting,
  // keeping both the parent data-fetch and child table in sync.

  const search = searchParams.get('search') ?? '';

  const page = Math.max(
    1,
    parseInt(searchParams.get('page') ?? '1', 10) || 1,
  );

  const pageSize = Math.max(
    1,
    parseInt(
      searchParams.get('pageSize') ?? String(DEFAULT_PAGE_SIZE),
      10,
    ) || DEFAULT_PAGE_SIZE,
  );

  const sortBy = searchParams.get('sortBy') ?? DEFAULT_SORT_FIELD;

  const sortOrder: 'asc' | 'desc' =
    searchParams.get('sortOrder') === 'desc' ? 'desc' : 'asc';

  // ── Local search input state with debounce ─────────────────────────
  // The input field is controlled by local state to avoid hitting the API
  // on every keystroke. After the user stops typing for SEARCH_DEBOUNCE_MS
  // the value is propagated to the URL search params, which triggers a
  // TanStack Query refetch.

  const [searchInput, setSearchInput] = useState<string>(search);

  // Sync local input with URL when URL changes externally
  // (e.g., browser back/forward navigation).
  useEffect(() => {
    setSearchInput(search);
  }, [search]);

  // Debounce: propagate local input value → URL search param after delay
  useEffect(() => {
    const timer = setTimeout(() => {
      setSearchParams((prev) => {
        const currentSearch = prev.get('search') ?? '';
        const trimmed = searchInput.trim();

        // Bail out if the trimmed value is already in the URL to avoid
        // unnecessary navigation events.
        if (trimmed === currentSearch) {
          return prev;
        }

        const params = new URLSearchParams(prev);
        if (trimmed) {
          params.set('search', trimmed);
        } else {
          params.delete('search');
        }
        // Always reset to the first page when the search text changes
        params.set('page', '1');
        return params;
      });
    }, SEARCH_DEBOUNCE_MS);

    return () => clearTimeout(timer);
  }, [searchInput, setSearchParams]);

  // ── Salutation lookup data ─────────────────────────────────────────
  // Fetches salutation reference data (Mr., Ms., Mrs., Dr., Prof.) from
  // GET /v1/crm/salutations with a 30-minute staleTime since salutations
  // rarely change. Replaces NextPlugin.20190206.cs salutation entity and
  // the server-side relation resolution in the Razor Page rendering.

  const { data: salutationData, isLoading: salutationsLoading } =
    useSalutations();

  /**
   * Memoised lookup map from salutation record ID (GUID string) to the
   * human-readable label (e.g., "Mr.", "Ms.", "Dr.").
   *
   * Built from the useSalutations() query data. The salutation entity
   * records have `id` and `name` (or `label`) fields.
   */
  const salutationMap = useMemo<Map<string, string>>(() => {
    const map = new Map<string, string>();
    if (!salutationData?.records) return map;

    for (const record of salutationData.records) {
      const id = String(record.id ?? '');
      // Try 'label' first (common display field), fall back to 'name'
      const label = String(record.label ?? record.name ?? '');
      if (id && label) {
        map.set(id, label);
      }
    }
    return map;
  }, [salutationData]);

  // ── Data fetching via TanStack Query ───────────────────────────────
  // The query key includes all filter/sort/page parameters so that
  // TanStack Query treats each combination as a distinct cache entry
  // and automatically refetches when any parameter changes.

  const {
    data: response,
    isLoading,
    isFetching,
    isError,
    error,
  } = useQuery({
    queryKey: ['crm', 'contacts', { search, page, pageSize, sortBy, sortOrder }],
    queryFn: async (): Promise<ApiResponse<EntityRecordList>> => {
      const axiosResponse = await apiClient.get<ApiResponse<EntityRecordList>>(
        '/crm/contacts',
        {
          params: {
            search: search || undefined,
            page,
            pageSize,
            sortBy,
            sortOrder,
          },
        },
      );
      return axiosResponse.data;
    },
    // Keep previous page data visible while the next page loads.
    // Replaces TanStack Query v4's keepPreviousData boolean.
    placeholderData: (previousData) => previousData,
    // Cache data for 30 seconds before marking stale
    staleTime: CONTACTS_STALE_TIME_MS,
  });

  // Extract records and total count from the API response envelope.
  // ApiResponse<EntityRecordList>.object contains { records, totalCount }.
  const records: EntityRecord[] = response?.object?.records ?? [];
  const totalCount: number = response?.object?.totalCount ?? 0;

  // ── Event handlers ─────────────────────────────────────────────────

  /** Updates the local search input state (debounced before URL sync). */
  const handleSearchChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      setSearchInput(event.target.value);
    },
    [],
  );

  /**
   * Navigates programmatically to the contact detail view.
   * Used by the actions column button for direct row-level navigation.
   */
  const handleRowClick = useCallback(
    (record: EntityRecord) => {
      if (record.id) {
        navigate(`/crm/contacts/${record.id}`);
      }
    },
    [navigate],
  );

  /**
   * Called when DataTable changes the active page.
   * DataTable already updates the page URL param internally; this callback
   * provides supplementary UX (scroll to top of the list).
   */
  const handlePageChange = useCallback(() => {
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }, []);

  /**
   * Called when DataTable changes the sort column/direction.
   * DataTable already updates sortBy/sortOrder URL params; this callback
   * additionally resets pagination to page 1 so the user sees results
   * from the beginning of the re-sorted dataset.
   */
  const handleSortChange = useCallback(
    (_newSortBy: string, _newSortOrder: 'asc' | 'desc') => {
      setSearchParams((prev) => {
        const params = new URLSearchParams(prev);
        params.set('page', '1');
        return params;
      });
    },
    [setSearchParams],
  );

  // ── Column definitions ─────────────────────────────────────────────
  // Maps the contact entity fields from NextPlugin.20190204.cs and
  // NextPlugin.20190206.cs to DataTable column configurations.
  //
  // Displayed columns (9):
  //   Photo | Name | Salutation | Email | Phone | Job Title | City |
  //   Account | Created On
  // Plus an Actions column for row-level navigation.

  const columns = useMemo<DataTableColumn<EntityRecord>[]>(
    () => [
      // ── Photo — small rounded avatar thumbnail ─────────────────────
      // Source: NextPlugin.20190206.cs — InputImageField for contact photo
      {
        id: 'photo',
        name: 'photo',
        label: '',
        width: '52px',
        horizontalAlign: 'center',
        verticalAlign: 'middle',
        accessorKey: 'photo',
        cell: (value: unknown) => {
          const url = String(value ?? '').trim();
          if (!url) {
            // Default avatar placeholder — a neutral person silhouette
            return (
              <span
                className="inline-flex h-8 w-8 items-center justify-center rounded-full bg-gray-200"
                aria-hidden="true"
              >
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  viewBox="0 0 20 20"
                  fill="currentColor"
                  className="h-5 w-5 text-gray-400"
                >
                  <path d="M10 8a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM3.465 14.493a1.23 1.23 0 0 0 .41 1.412A9.957 9.957 0 0 0 10 18c2.31 0 4.438-.784 6.131-2.1.43-.333.604-.903.408-1.41a7.002 7.002 0 0 0-13.074.003Z" />
                </svg>
              </span>
            );
          }
          return (
            <img
              src={url}
              alt=""
              width={32}
              height={32}
              loading="lazy"
              decoding="async"
              className="h-8 w-8 rounded-full object-cover bg-gray-100"
            />
          );
        },
      },

      // ── Name — first_name + last_name, linked to detail view ───────
      // Source: NextPlugin.20190204.cs — InputTextField first_name, last_name
      {
        id: 'name',
        name: 'last_name',
        label: 'Name',
        sortable: true,
        accessorFn: (record: EntityRecord) => buildContactName(record),
        cell: (value: unknown, record: EntityRecord) => {
          const displayName = String(value ?? '');
          if (!displayName) return '';
          return (
            <Link
              to={`/crm/contacts/${record.id}`}
              className="font-medium text-blue-600 hover:text-blue-800 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              {displayName}
            </Link>
          );
        },
      },

      // ── Salutation — resolved label from salutation_id lookup ──────
      // Source: NextPlugin.20190206.cs — salutation_id GUID field
      // Resolved via useSalutations() reference data hook
      {
        id: 'salutation',
        name: 'salutation_id',
        label: 'Salutation',
        sortable: true,
        width: '110px',
        accessorKey: 'salutation_id',
        cell: (value: unknown) => {
          const id = String(value ?? '').trim();
          if (!id) return '';
          return salutationMap.get(id) ?? '';
        },
      },

      // ── Email — clickable mailto link ──────────────────────────────
      // Source: NextPlugin.20190204.cs — InputEmailField
      {
        id: 'email',
        name: 'email',
        label: 'Email',
        sortable: true,
        accessorKey: 'email',
        cell: (value: unknown) => {
          const email = String(value ?? '').trim();
          if (!email) return '';
          return (
            <a
              href={`mailto:${email}`}
              className="text-gray-700 hover:text-blue-600 hover:underline"
            >
              {email}
            </a>
          );
        },
      },

      // ── Phone — prefers mobile_phone, falls back to fixed_phone ────
      // Source: NextPlugin.20190204.cs — InputPhoneField (mobile_phone, fixed_phone)
      {
        id: 'phone',
        name: 'mobile_phone',
        label: 'Phone',
        sortable: false,
        accessorFn: (record: EntityRecord) => getPhoneNumber(record),
        cell: (value: unknown) => {
          const phone = String(value ?? '').trim();
          if (!phone) return '';
          return (
            <a
              href={`tel:${phone}`}
              className="text-gray-700 hover:text-blue-600 hover:underline"
            >
              {phone}
            </a>
          );
        },
      },

      // ── Job Title ──────────────────────────────────────────────────
      // Source: NextPlugin.20190204.cs — InputTextField job_title
      {
        id: 'job_title',
        name: 'job_title',
        label: 'Job Title',
        sortable: true,
        accessorKey: 'job_title',
        cell: (value: unknown) => String(value ?? ''),
      },

      // ── City ───────────────────────────────────────────────────────
      // Source: NextPlugin.20190204.cs — InputTextField city
      {
        id: 'city',
        name: 'city',
        label: 'City',
        sortable: true,
        accessorKey: 'city',
        cell: (value: unknown) => String(value ?? ''),
      },

      // ── Account — related account name ─────────────────────────────
      // Source: Configuration.cs — $account_nn_contact.name relation
      // In the target architecture, the CRM service denormalises the
      // account name into the contact record or returns it as a nested
      // relation object.
      {
        id: 'account',
        name: 'account_name',
        label: 'Account',
        sortable: true,
        accessorFn: (record: EntityRecord) => getAccountName(record),
        cell: (value: unknown) => String(value ?? ''),
      },

      // ── Created On — formatted date ────────────────────────────────
      // Source: NextPlugin.20190206.cs — DateTime field with
      //   UseCurrentTimeAsDefaultValue=true, format "yyyy-MMM-dd HH:mm"
      {
        id: 'created_on',
        name: 'created_on',
        label: 'Created On',
        sortable: true,
        accessorKey: 'created_on',
        width: '150px',
        noWrap: true,
        cell: (value: unknown) => formatDate(value),
      },

      // ── Actions — chevron button for row-level navigation ──────────
      {
        id: 'actions',
        label: '',
        width: '60px',
        horizontalAlign: 'center',
        cell: (_value: unknown, record: EntityRecord) => (
          <button
            type="button"
            onClick={() => handleRowClick(record)}
            className="rounded p-1 text-gray-400 hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
            aria-label={`View contact ${buildContactName(record)}`}
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M7.21 14.77a.75.75 0 0 1 .02-1.06L11.168 10 7.23 6.29a.75.75 0 1 1 1.04-1.08l4.5 4.25a.75.75 0 0 1 0 1.08l-4.5 4.25a.75.75 0 0 1-1.06-.02Z"
                clipRule="evenodd"
              />
            </svg>
          </button>
        ),
      },
    ],
    [salutationMap, handleRowClick],
  );

  // ── Render ─────────────────────────────────────────────────────────

  // Show a combined loading state: contacts query initial load OR
  // salutation reference data still loading (needed for column rendering).
  const isInitialLoading = isLoading || salutationsLoading;

  return (
    <div className="flex flex-col gap-6 p-6">
      {/* Page header — title + create button */}
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-gray-900">
            Contacts
          </h1>
          <p className="mt-1 text-sm text-gray-500">
            Manage your CRM contacts and their details.
          </p>
        </div>

        <Link
          to="/crm/contacts/create"
          className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
        >
          {/* Plus icon — Heroicons mini "plus" */}
          <svg
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 20 20"
            fill="currentColor"
            className="me-1.5 h-5 w-5"
            aria-hidden="true"
          >
            <path d="M10.75 4.75a.75.75 0 0 0-1.5 0v4.5h-4.5a.75.75 0 0 0 0 1.5h4.5v4.5a.75.75 0 0 0 1.5 0v-4.5h4.5a.75.75 0 0 0 0-1.5h-4.5v-4.5Z" />
          </svg>
          Create Contact
        </Link>
      </div>

      {/* Search bar with search icon */}
      <div className="relative max-w-md">
        <div className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3">
          {/* Heroicons mini "magnifying-glass" */}
          <svg
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 20 20"
            fill="currentColor"
            className="h-5 w-5 text-gray-400"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M9 3.5a5.5 5.5 0 1 0 0 11 5.5 5.5 0 0 0 0-11ZM2 9a7 7 0 1 1 12.452 4.391l3.328 3.329a.75.75 0 1 1-1.06 1.06l-3.329-3.328A7 7 0 0 1 2 9Z"
              clipRule="evenodd"
            />
          </svg>
        </div>
        <input
          type="search"
          placeholder="Search contacts…"
          value={searchInput}
          onChange={handleSearchChange}
          className="block w-full rounded-md border border-gray-300 bg-white py-2 pe-4 ps-10 text-sm text-gray-900 placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
          aria-label="Search contacts"
        />
      </div>

      {/* Error alert */}
      {isError && (
        <div
          className="rounded-md bg-red-50 p-4 text-sm text-red-700"
          role="alert"
        >
          <p className="font-medium">Failed to load contacts</p>
          <p className="mt-1">
            {(error as { message?: string })?.message ||
              'An unexpected error occurred. Please try again.'}
          </p>
        </div>
      )}

      {/* Initial loading spinner — shown during first data fetch */}
      {isInitialLoading && (
        <div
          className="flex items-center justify-center py-12"
          role="status"
        >
          <div className="h-8 w-8 animate-spin rounded-full border-4 border-blue-600 border-t-transparent" />
          <span className="sr-only">Loading contacts…</span>
        </div>
      )}

      {/* Data table — shown after initial data load */}
      {!isInitialLoading && (
        <DataTable<EntityRecord>
          data={records}
          columns={columns}
          totalCount={totalCount}
          pageSize={pageSize}
          currentPage={page}
          onPageChange={handlePageChange}
          onSortChange={handleSortChange}
          loading={isFetching}
          hover
          striped
          emptyText="No contacts found. Create your first contact to get started."
          name="contacts"
          responsiveBreakpoint="md"
        />
      )}
    </div>
  );
}

export default ContactList;
