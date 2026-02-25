/**
 * AccountList — CRM Account Listing Page
 *
 * Replaces the monolith's account entity listing functionality driven
 * by `RecordList.cshtml` Razor Page with account entity configuration
 * from `WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs`.
 *
 * Features:
 * - Server-side pagination via page/pageSize URL search parameters
 * - Server-side sorting via sortBy/sortOrder URL search parameters
 * - Debounced text search via search URL parameter (searches x_search field)
 * - Navigate to account detail (/crm/accounts/:id) via name link or row action
 * - Create new account navigation link (/crm/accounts/create)
 *
 * Source mapping:
 *  - NextPlugin.20190204.cs → Account entity field definitions
 *    (type, website, street, region, post_code, phones, email, etc.)
 *  - NextPlugin.20190206.cs → created_on, salutation_id, first_name,
 *    last_name fields
 *  - Configuration.cs → AccountSearchIndexFields (x_search composition)
 *  - SearchService.cs → Server-side search via x_search denormalized field
 *  - RecordList.cshtml → Route pattern and page layout (replaced by React)
 *
 * @module pages/crm/AccountList
 */

import { useState, useMemo, useCallback, useEffect } from 'react';
import { useNavigate, Link, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { get } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import type { EntityRecord, EntityRecordList } from '../../types/record';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Search input debounce delay in milliseconds.
 * Prevents excessive API calls while the user is still typing.
 */
const SEARCH_DEBOUNCE_MS = 300;

/** Default number of records displayed per page. */
const DEFAULT_PAGE_SIZE = 10;

/**
 * Account type select option mapping.
 *
 * Extracted from `NextPlugin.20190204.cs` — the `InputSelectField`
 * options for the `type` field on the `account` entity:
 *   Value "1" → Label "Company"
 *   Value "2" → Label "Person"
 */
const ACCOUNT_TYPE_MAP: Record<string, string> = {
  '1': 'Company',
  '2': 'Person',
};

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * AccountList page component.
 *
 * Displays a searchable, sortable, paginated table of CRM account
 * records. Uses TanStack Query for data fetching with caching and the
 * shared {@link DataTable} component for rendering.
 *
 * All query state (search, page, pageSize, sortBy, sortOrder) is stored
 * in URL search parameters — enabling shareable URLs and browser
 * back/forward navigation. This mirrors the monolith's query-string
 * parameter pattern from `PcGrid` / `WvFilterBase`.
 *
 * Lazy-loaded via React Router:
 * ```tsx
 * const AccountList = lazy(() => import('./pages/crm/AccountList'));
 * ```
 */
function AccountList(): React.ReactElement {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  // ── Derive query state from URL search params ────────────────────────
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

  const sortBy = searchParams.get('sortBy') ?? 'name';

  const sortOrder: 'asc' | 'desc' =
    searchParams.get('sortOrder') === 'desc' ? 'desc' : 'asc';

  // ── Local search input state with debounce ──────────────────────────
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

  // ── Data fetching via TanStack Query ─────────────────────────────────
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
    queryKey: ['accounts', { search, page, pageSize, sortBy, sortOrder }],
    queryFn: () =>
      get<EntityRecordList>('/crm/accounts', {
        search: search || undefined,
        page,
        pageSize,
        sortBy,
        sortOrder,
      }),
    // Keep previous page data visible while the next page loads.
    // Replaces TanStack Query v4's keepPreviousData boolean.
    placeholderData: (previousData) => previousData,
    // Cache data for 30 seconds before marking stale
    staleTime: 30_000,
  });

  // Extract records and total count from the API response envelope.
  // ApiResponse<EntityRecordList>.object contains { records, totalCount }.
  const records: EntityRecord[] = response?.object?.records ?? [];
  const totalCount: number = response?.object?.totalCount ?? 0;

  // ── Event handlers ───────────────────────────────────────────────────

  /** Updates the local search input state (debounced before URL sync). */
  const handleSearchChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      setSearchInput(event.target.value);
    },
    [],
  );

  /**
   * Navigates programmatically to the account detail view.
   * Used by the actions column button for direct row-level navigation.
   */
  const handleRowClick = useCallback(
    (record: EntityRecord) => {
      if (record.id) {
        navigate(`/crm/accounts/${record.id}`);
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

  // ── Column definitions ───────────────────────────────────────────────
  // Maps the account entity fields from NextPlugin.20190204.cs and
  // NextPlugin.20190206.cs to DataTable column configurations.
  //
  // Displayed columns: Name, Type (badge), Email, Phone, City, Created On, Actions

  const columns = useMemo<DataTableColumn<EntityRecord>[]>(
    () => [
      // Name — linked to account detail view
      {
        id: 'name',
        name: 'name',
        label: 'Name',
        sortable: true,
        accessorKey: 'name',
        cell: (value: unknown, record: EntityRecord) => (
          <Link
            to={`/crm/accounts/${record.id}`}
            className="font-medium text-blue-600 hover:text-blue-800 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            {String(value ?? '')}
          </Link>
        ),
      },

      // Type — Company or Person badge
      // Source: NextPlugin.20190204.cs — InputSelectField with options
      //   { Label: "Company", Value: "1" }, { Label: "Person", Value: "2" }
      {
        id: 'type',
        name: 'type',
        label: 'Type',
        sortable: true,
        accessorKey: 'type',
        width: '120px',
        cell: (value: unknown) => {
          const raw = String(value ?? '');
          const label = ACCOUNT_TYPE_MAP[raw] || raw;
          if (!label) return null;
          const isCompany = raw === '1';
          return (
            <span
              className={
                isCompany
                  ? 'inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800'
                  : 'inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800'
              }
            >
              {label}
            </span>
          );
        },
      },

      // Email — clickable mailto link
      // Source: NextPlugin.20190204.cs — InputEmailField
      {
        id: 'email',
        name: 'email',
        label: 'Email',
        sortable: true,
        accessorKey: 'email',
        cell: (value: unknown) => {
          const email = String(value ?? '');
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

      // Phone — prefers mobile_phone, falls back to fixed_phone
      // Source: NextPlugin.20190204.cs — InputPhoneField (mobile_phone, fixed_phone)
      {
        id: 'phone',
        name: 'mobile_phone',
        label: 'Phone',
        sortable: false,
        accessorFn: (record: EntityRecord) =>
          String(record.mobile_phone ?? record.fixed_phone ?? ''),
        cell: (value: unknown) => String(value ?? ''),
      },

      // City
      // Source: NextPlugin.20190204.cs — InputTextField
      {
        id: 'city',
        name: 'city',
        label: 'City',
        sortable: true,
        accessorKey: 'city',
        cell: (value: unknown) => String(value ?? ''),
      },

      // Created On — formatted date
      // Source: NextPlugin.20190206.cs — DateTime field
      {
        id: 'created_on',
        name: 'created_on',
        label: 'Created On',
        sortable: true,
        accessorKey: 'created_on',
        width: '150px',
        noWrap: true,
        cell: (value: unknown) => {
          if (!value) return '';
          try {
            return new Intl.DateTimeFormat(undefined, {
              year: 'numeric',
              month: 'short',
              day: 'numeric',
            }).format(new Date(String(value)));
          } catch {
            return String(value);
          }
        },
      },

      // Actions — chevron button for row-level navigation
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
            aria-label={`View account ${String(record.name ?? '')}`}
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
    [handleRowClick],
  );

  // ── Render ───────────────────────────────────────────────────────────

  return (
    <div className="flex flex-col gap-6 p-6">
      {/* Page header — title + create button */}
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-gray-900">
            Accounts
          </h1>
          <p className="mt-1 text-sm text-gray-500">
            Manage your CRM accounts and contacts.
          </p>
        </div>

        <Link
          to="/crm/accounts/create"
          className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 20 20"
            fill="currentColor"
            className="me-1.5 h-5 w-5"
            aria-hidden="true"
          >
            <path d="M10.75 4.75a.75.75 0 0 0-1.5 0v4.5h-4.5a.75.75 0 0 0 0 1.5h4.5v4.5a.75.75 0 0 0 1.5 0v-4.5h4.5a.75.75 0 0 0 0-1.5h-4.5v-4.5Z" />
          </svg>
          Create Account
        </Link>
      </div>

      {/* Search bar with search icon */}
      <div className="relative max-w-md">
        <div className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3">
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
          placeholder="Search accounts…"
          value={searchInput}
          onChange={handleSearchChange}
          className="block w-full rounded-md border border-gray-300 bg-white py-2 pe-4 ps-10 text-sm text-gray-900 placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500/20"
          aria-label="Search accounts"
        />
      </div>

      {/* Error alert */}
      {isError && (
        <div
          className="rounded-md bg-red-50 p-4 text-sm text-red-700"
          role="alert"
        >
          <p className="font-medium">Failed to load accounts</p>
          <p className="mt-1">
            {(error as { message?: string })?.message ||
              'An unexpected error occurred. Please try again.'}
          </p>
        </div>
      )}

      {/* Initial loading spinner */}
      {isLoading && (
        <div
          className="flex items-center justify-center py-12"
          role="status"
        >
          <div className="h-8 w-8 animate-spin rounded-full border-4 border-blue-600 border-t-transparent" />
          <span className="sr-only">Loading accounts…</span>
        </div>
      )}

      {/* Data table — shown after initial data load */}
      {!isLoading && (
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
          emptyText="No accounts found. Create your first account to get started."
          name="accounts"
          responsiveBreakpoint="md"
        />
      )}
    </div>
  );
}

export default AccountList;
