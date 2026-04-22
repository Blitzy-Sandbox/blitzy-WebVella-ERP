/**
 * EmailList — Email Listing Page Component
 *
 * Replaces the monolith's Mail plugin "All Emails" list page built from:
 *   - MailPlugin.20190215.cs — email entity creation, AllEmails datasource,
 *     all_emails list page with PcGrid, "Create Email" header button
 *   - MailPlugin.20190420.cs — sender/recipients JSON deserialization via
 *     ICodeVariable code snippets (EmailAddress → .Address, semicolon-joined)
 *   - MailPlugin.20200611.cs — expanded AllEmails EQL datasource:
 *     SELECT * FROM email WHERE x_search CONTAINS @searchQuery
 *     ORDER BY @sortBy @sortOrder PAGE @page PAGESIZE @pageSize
 *
 * Columns match the monolith's PcGrid configuration: Status, Priority,
 * Sender, Recipients, Subject, Created, Sent, Service.
 *
 * Default sort: created_on DESC (from AllEmails datasource default params).
 * Default page size: APP_DEFAULTS.PAGE_SIZE (10), matching monolith's
 * PcGridOptions.page_size default used in the AllEmails page.
 *
 * AAP compliance:
 *   - §0.8.1 — Full behavioral parity with monolith email list
 *   - §0.8.1 — Pure static SPA: all data fetched via API
 *   - §0.8.2 — Per-route chunk < 200KB (lazy-loaded page component)
 *   - Tailwind CSS only — zero Bootstrap
 *   - No jQuery — all DOM interaction via React
 *
 * @module pages/notifications/EmailList
 */

import { useState, useMemo, useCallback } from 'react';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';

import { get } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import { formatDateTime } from '../../utils/formatters';
import { APP_DEFAULTS } from '../../utils/constants';

// =============================================================================
// Type Definitions (from Email.cs, EmailAddress.cs, EmailStatus.cs,
// EmailPriority.cs in the monolith's WebVella.Erp.Plugins.Mail.Api namespace)
// =============================================================================

/**
 * Email address value object — mirrors the C# EmailAddress class.
 * JSON shape: { "name": "...", "address": "..." }
 */
interface EmailAddress {
  name: string;
  address: string;
}

/**
 * Email status values matching the C# EmailStatus enum.
 * Pending = 0: queued, waiting to be sent
 * Sent    = 1: successfully delivered
 * Aborted = 2: failed or manually aborted
 */
const EmailStatus = {
  Pending: 'pending',
  Sent: 'sent',
  Aborted: 'aborted',
} as const;

type EmailStatusValue = (typeof EmailStatus)[keyof typeof EmailStatus];

/**
 * Email priority values matching the C# EmailPriority enum.
 * Low    = 0
 * Normal = 1 (default)
 * High   = 2
 */
const EmailPriority = {
  Low: 'low',
  Normal: 'normal',
  High: 'high',
} as const;

type EmailPriorityValue = (typeof EmailPriority)[keyof typeof EmailPriority];

/**
 * Email entity record — TypeScript type alias derived from the C# Email class
 * and the entity/field definitions in MailPlugin.20190215.cs + patch files.
 *
 * Uses `type` (not `interface`) to satisfy the DataTable<T> generic constraint
 * `T extends Record<string, unknown>` — type aliases provide the implicit
 * index signature that interfaces lack in TypeScript.
 *
 * Field types match the DynamoDB/API JSON representation used by the
 * Notifications microservice, not the raw PostgreSQL column types.
 */
type Email = {
  /** Unique identifier (GUID) — system field */
  id: string;
  /** Email subject line — text, max 1000 chars */
  subject: string;
  /** Plain-text body content */
  content_text: string;
  /** HTML body content */
  content_html: string;
  /** Delivery timestamp (null if not yet sent) — format: yyyy-MMM-dd HH:mm */
  sent_on: string | null;
  /** Creation timestamp (always populated, auto-current) */
  created_on: string;
  /** Server/SMTP error message (null if no error) */
  server_error: string | null;
  /** Number of delivery retry attempts — default 0, min 0 */
  retries_count: number;
  /** SMTP service identifier (GUID) — required */
  service_id: string;
  /** Email priority: low | normal | high (default: normal) */
  priority: EmailPriorityValue;
  /** Reply-to email address */
  reply_to_email: string;
  /** Scheduled delivery timestamp (null if not scheduled) */
  scheduled_on: string | null;
  /** Email status: pending | sent | aborted (default: pending) */
  status: EmailStatusValue;
  /** Composite search field for full-text filtering */
  x_search: string;
  /** Sender — JSON object: { name, address } */
  sender: EmailAddress | string;
  /** Recipients — JSON array: [{ name, address }] */
  recipients: EmailAddress[] | string;
  /** Attachments — JSON string, default "[]" */
  attachments: string;
  /**
   * SMTP service display name — populated via server-side join
   * (replaces monolith's $smtp_service_email.name EQL relation join)
   */
  smtp_service_name?: string;
};

/**
 * Query parameters for the email listing API endpoint.
 * Maps to the AllEmails datasource parameters from MailPlugin.20200611.cs.
 */
interface EmailListParams {
  status?: string;
  search?: string;
  sortBy: string;
  sortOrder: string;
  page: number;
  pageSize: number;
}

/**
 * API response shape for the paginated email list.
 * Returned inside the ApiResponse<T>.object envelope from the
 * Notifications microservice GET /v1/notifications/emails endpoint.
 */
interface EmailListResponse {
  data: Email[];
  total: number;
  page: number;
  pageSize: number;
}

// =============================================================================
// Constants
// =============================================================================

/** API endpoint path for the email listing (path-based versioning via client.ts BASE_URL) */
const EMAILS_ENDPOINT = '/notifications/emails';

/** Status filter tab definitions — matching email.status select field options */
const STATUS_FILTERS: ReadonlyArray<{ label: string; value: string }> = [
  { label: 'All', value: '' },
  { label: 'Pending', value: EmailStatus.Pending },
  { label: 'Sent', value: EmailStatus.Sent },
  { label: 'Aborted', value: EmailStatus.Aborted },
];

/** Status display configuration — color-coded badges matching monolith appearance */
const STATUS_CONFIG: Record<
  string,
  { label: string; bgClass: string; textClass: string }
> = {
  [EmailStatus.Pending]: {
    label: 'Pending',
    bgClass: 'bg-yellow-100',
    textClass: 'text-yellow-800',
  },
  [EmailStatus.Sent]: {
    label: 'Sent',
    bgClass: 'bg-green-100',
    textClass: 'text-green-800',
  },
  [EmailStatus.Aborted]: {
    label: 'Aborted',
    bgClass: 'bg-red-100',
    textClass: 'text-red-800',
  },
};

/** Priority display configuration — icon indicators matching monolith's PcGrid column */
const PRIORITY_CONFIG: Record<
  string,
  { label: string; icon: string; className: string }
> = {
  [EmailPriority.Low]: {
    label: 'Low',
    icon: '↓',
    className: 'text-blue-500',
  },
  [EmailPriority.Normal]: {
    label: 'Normal',
    icon: '→',
    className: 'text-gray-500',
  },
  [EmailPriority.High]: {
    label: 'High',
    icon: '↑',
    className: 'text-red-500',
  },
};

// =============================================================================
// Helper Functions
// =============================================================================

/**
 * Safely parses a JSON sender field into an EmailAddress object.
 * The sender field may arrive as a JSON string (from legacy storage) or
 * as a pre-parsed object (from the Notifications microservice).
 *
 * Mirrors the monolith's ICodeVariable code from MailPlugin.20190420.cs:
 *   var mailAddress = JsonConvert.DeserializeObject<EmailAddress>(
 *     (string) rowRecord["sender"]
 *   );
 *   return mailAddress.Address;
 */
function parseSender(sender: EmailAddress | string | null | undefined): string {
  if (sender == null) {
    return '';
  }

  try {
    const parsed: EmailAddress =
      typeof sender === 'string' ? JSON.parse(sender) : sender;
    if (parsed.name && parsed.address) {
      return `${parsed.name} <${parsed.address}>`;
    }
    return parsed.address || parsed.name || '';
  } catch {
    return typeof sender === 'string' ? sender : '';
  }
}

/**
 * Safely parses a JSON recipients field into a semicolon-joined address list.
 * The recipients field may arrive as a JSON string (from legacy storage) or
 * as a pre-parsed array (from the Notifications microservice).
 *
 * Mirrors the monolith's ICodeVariable code from MailPlugin.20190420.cs:
 *   var recipients = JsonConvert.DeserializeObject<List<EmailAddress>>(
 *     (string) rowRecord["recipients"]
 *   );
 *   return string.Join(";", recipients.Select(x => x.Address).ToList());
 */
function parseRecipients(
  recipients: EmailAddress[] | string | null | undefined,
): string {
  if (recipients == null) {
    return '';
  }

  try {
    const parsed: EmailAddress[] =
      typeof recipients === 'string' ? JSON.parse(recipients) : recipients;
    if (!Array.isArray(parsed)) {
      return '';
    }
    return parsed.map((r) => r.address || r.name || '').join('; ');
  } catch {
    return typeof recipients === 'string' ? recipients : '';
  }
}

// =============================================================================
// Data Fetching Hook
// =============================================================================

/**
 * TanStack Query hook for fetching the paginated email list.
 *
 * Replaces the monolith's in-process AllEmails EQL datasource execution:
 *   SELECT * FROM email
 *   WHERE x_search CONTAINS @searchQuery
 *   ORDER BY @sortBy @sortOrder
 *   PAGE @page PAGESIZE @pageSize
 *
 * Query key: ['emails', params] — ensures automatic refetching when any
 * filter, sort, or pagination parameter changes.
 *
 * @param params - Email list query parameters (status, search, sort, pagination)
 */
function useEmails(params: EmailListParams) {
  return useQuery({
    queryKey: ['emails', params],
    queryFn: async () => {
      const queryParams: Record<string, unknown> = {
        sortBy: params.sortBy,
        sortOrder: params.sortOrder,
        page: params.page,
        pageSize: params.pageSize,
      };

      if (params.status) {
        queryParams.status = params.status;
      }

      if (params.search) {
        queryParams.search = params.search;
      }

      const response = await get<EmailListResponse | Email[]>(
        EMAILS_ENDPOINT,
        queryParams,
      );
      const obj = response.object;
      // Normalize: API may return raw Email[] array or { data, total } envelope
      if (Array.isArray(obj)) {
        return { data: obj as Email[], total: obj.length, page: params.page, pageSize: params.pageSize };
      }
      return (obj as EmailListResponse) ?? { data: [], total: 0, page: 1, pageSize: params.pageSize };
    },
    placeholderData: (previousData) => previousData,
  });
}

// =============================================================================
// Main Component
// =============================================================================

/**
 * EmailList — Primary email management listing page.
 *
 * Renders:
 *  1. Page header with "Emails" title and "Compose Email" button
 *  2. Status filter tabs (All | Pending | Sent | Aborted)
 *  3. Search input for x_search field filtering
 *  4. DataTable with 8 columns: Status, Priority, Sender, Recipients,
 *     Subject, Created, Sent, Service
 *  5. Server-side sorting and pagination via URL search params
 *
 * All state managed via URL search params for bookmarkable views.
 */
function EmailList(): React.ReactElement {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  // ---------------------------------------------------------------------------
  // Local state for the search input (controlled input, synced to URL on submit)
  // ---------------------------------------------------------------------------
  const [searchInput, setSearchInput] = useState<string>(
    () => searchParams.get('search') ?? '',
  );

  // ---------------------------------------------------------------------------
  // Derive query parameters from URL search params
  // ---------------------------------------------------------------------------
  const queryParams = useMemo<EmailListParams>(() => {
    const status = searchParams.get('status') ?? '';
    const search = searchParams.get('search') ?? '';
    const sortBy = searchParams.get('sortBy') ?? 'created_on';
    const sortOrder = searchParams.get('sortOrder') ?? 'desc';
    const page = parseInt(searchParams.get('page') ?? '1', 10) || 1;
    const pageSize =
      parseInt(searchParams.get('pageSize') ?? '', 10) ||
      APP_DEFAULTS.PAGE_SIZE;

    return { status, search, sortBy, sortOrder, page, pageSize };
  }, [searchParams]);

  // ---------------------------------------------------------------------------
  // Data fetching
  // ---------------------------------------------------------------------------
  const { data: emailData, isLoading, isError, error } = useEmails(queryParams);

  const emails = emailData?.data ?? [];
  const totalCount = emailData?.total ?? 0;

  // ---------------------------------------------------------------------------
  // URL search param update helper — preserves existing params
  // ---------------------------------------------------------------------------
  const updateSearchParams = useCallback(
    (updates: Record<string, string>) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        for (const [key, value] of Object.entries(updates)) {
          if (value === '' || value == null) {
            next.delete(key);
          } else {
            next.set(key, value);
          }
        }
        return next;
      });
    },
    [setSearchParams],
  );

  // ---------------------------------------------------------------------------
  // Event handlers
  // ---------------------------------------------------------------------------

  /** Handle status filter tab click */
  const handleStatusFilter = useCallback(
    (status: string) => {
      updateSearchParams({ status, page: '1' });
    },
    [updateSearchParams],
  );

  /** Handle search form submission */
  const handleSearchSubmit = useCallback(
    (e: React.FormEvent) => {
      e.preventDefault();
      updateSearchParams({ search: searchInput, page: '1' });
    },
    [searchInput, updateSearchParams],
  );

  /** Handle clearing the search */
  const handleClearSearch = useCallback(() => {
    setSearchInput('');
    updateSearchParams({ search: '', page: '1' });
  }, [updateSearchParams]);

  /** Handle page change from DataTable pagination */
  const handlePageChange = useCallback(
    (page: number) => {
      updateSearchParams({ page: String(page) });
    },
    [updateSearchParams],
  );

  /** Handle page size change from DataTable */
  const handlePageSizeChange = useCallback(
    (size: number) => {
      updateSearchParams({ pageSize: String(size), page: '1' });
    },
    [updateSearchParams],
  );

  /** Handle sort change from DataTable column headers */
  const handleSortChange = useCallback(
    (sortBy: string, sortOrder: 'asc' | 'desc') => {
      updateSearchParams({ sortBy, sortOrder, page: '1' });
    },
    [updateSearchParams],
  );

    // ---------------------------------------------------------------------------
  // Column definitions — matching monolith's PcGrid 8-column configuration
  // ---------------------------------------------------------------------------
  const columns = useMemo<DataTableColumn<Email>[]>(
    () => [
      {
        id: 'status',
        label: 'Status',
        name: 'status',
        sortable: true,
        width: '100px',
        cell: (_value: unknown, record: Email) => {
          const config = STATUS_CONFIG[record.status] ?? {
            label: record.status || 'Unknown',
            bgClass: 'bg-gray-100',
            textClass: 'text-gray-800',
          };
          return (
            <span
              className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${config.bgClass} ${config.textClass}`}
            >
              {config.label}
            </span>
          );
        },
      },
      {
        id: 'priority',
        label: 'Priority',
        name: 'priority',
        sortable: true,
        width: '90px',
        horizontalAlign: 'center' as const,
        cell: (_value: unknown, record: Email) => {
          const config = PRIORITY_CONFIG[record.priority] ?? {
            label: record.priority || 'Normal',
            icon: '→',
            className: 'text-gray-500',
          };
          return (
            <span
              className={`text-lg font-bold ${config.className}`}
              title={config.label}
              aria-label={`Priority: ${config.label}`}
            >
              {config.icon}
            </span>
          );
        },
      },
      {
        id: 'sender',
        label: 'Sender',
        name: 'sender',
        sortable: false,
        cell: (_value: unknown, record: Email) => {
          const senderDisplay = parseSender(record.sender);
          return (
            <span
              className="truncate block max-w-[12rem]"
              title={senderDisplay}
            >
              {senderDisplay || '\u2014'}
            </span>
          );
        },
      },
      {
        id: 'recipients',
        label: 'Recipients',
        name: 'recipients',
        sortable: false,
        cell: (_value: unknown, record: Email) => {
          const recipientDisplay = parseRecipients(record.recipients);
          return (
            <span
              className="truncate block max-w-[14rem]"
              title={recipientDisplay}
            >
              {recipientDisplay || '\u2014'}
            </span>
          );
        },
      },
      {
        id: 'subject',
        label: 'Subject',
        name: 'subject',
        sortable: true,
        cell: (_value: unknown, record: Email) => (
          <Link
            to={`/notifications/emails/${record.id}`}
            className="text-blue-600 hover:text-blue-800 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            {record.subject || '(No subject)'}
          </Link>
        ),
      },
      {
        id: 'created_on',
        label: 'Created',
        name: 'created_on',
        sortable: true,
        width: '150px',
        noWrap: true,
        cell: (_value: unknown, record: Email) =>
          formatDateTime(record.created_on) || '\u2014',
      },
      {
        id: 'sent_on',
        label: 'Sent',
        name: 'sent_on',
        sortable: true,
        width: '150px',
        noWrap: true,
        cell: (_value: unknown, record: Email) =>
          record.sent_on ? formatDateTime(record.sent_on) : '\u2014',
      },
      {
        id: 'smtp_service_name',
        label: 'Service',
        name: 'smtp_service_name',
        sortable: false,
        width: '140px',
        cell: (_value: unknown, record: Email) => (
          <span className="truncate block max-w-[8rem]" title={record.smtp_service_name ?? ''}>
            {record.smtp_service_name || '\u2014'}
          </span>
        ),
      },
    ],
    [],
  );

  // ---------------------------------------------------------------------------
  // Active status filter for tab highlighting
  // ---------------------------------------------------------------------------
  const activeStatus = queryParams.status;

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------
  return (
    <div className="flex flex-col gap-6">
      {/* ── Page Header ───────────────────────────────────────── */}
      <header className="flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-semibold text-gray-900">Emails</h1>
        <Link
          to="/notifications/emails/compose"
          data-testid="compose-email"
          className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors duration-200 hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          <svg
            className="h-4 w-4"
            viewBox="0 0 16 16"
            fill="currentColor"
            aria-hidden="true"
          >
            <path d="M8 2a.75.75 0 0 1 .75.75v4.5h4.5a.75.75 0 0 1 0 1.5h-4.5v4.5a.75.75 0 0 1-1.5 0v-4.5h-4.5a.75.75 0 0 1 0-1.5h4.5v-4.5A.75.75 0 0 1 8 2Z" />
          </svg>
          Compose Email
        </Link>
      </header>

      {/* ── Status Filter Tabs + Search ───────────────────────── */}
      <div className="flex flex-wrap items-end justify-between gap-4">
        {/* Status filter tabs */}
        <nav
          className="flex gap-1 rounded-lg bg-gray-100 p-1"
          role="tablist"
          aria-label="Filter emails by status"
        >
          {STATUS_FILTERS.map((filter) => {
            const isActive = activeStatus === filter.value;
            return (
              <button
                key={filter.value || 'all'}
                type="button"
                role="tab"
                aria-selected={isActive}
                aria-controls="email-list-table"
                onClick={() => handleStatusFilter(filter.value)}
                className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors duration-150 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                  isActive
                    ? 'bg-white text-gray-900 shadow-sm'
                    : 'text-gray-600 hover:text-gray-900'
                }`}
              >
                {filter.label}
              </button>
            );
          })}
        </nav>

        {/* Search form */}
        <form
          onSubmit={handleSearchSubmit}
          className="flex items-center gap-2"
          role="search"
          aria-label="Search emails"
        >
          <label htmlFor="email-search-input" className="sr-only">
            Search emails
          </label>
          <div className="relative">
            <input
              id="email-search-input"
              type="search"
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
              placeholder="Search emails…"
              className="block w-64 rounded-md border border-gray-300 bg-white py-1.5 pe-10 ps-3 text-sm text-gray-900 placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
            {searchInput && (
              <button
                type="button"
                onClick={handleClearSearch}
                className="absolute inset-y-0 end-8 flex items-center pe-1 text-gray-400 hover:text-gray-600"
                aria-label="Clear search"
              >
                <svg
                  className="h-4 w-4"
                  viewBox="0 0 16 16"
                  fill="currentColor"
                  aria-hidden="true"
                >
                  <path d="M4.28 3.22a.75.75 0 0 0-1.06 1.06L6.94 8l-3.72 3.72a.75.75 0 1 0 1.06 1.06L8 9.06l3.72 3.72a.75.75 0 1 0 1.06-1.06L9.06 8l3.72-3.72a.75.75 0 0 0-1.06-1.06L8 6.94 4.28 3.22Z" />
                </svg>
              </button>
            )}
          </div>
          <button
            type="submit"
            className="inline-flex items-center rounded-md bg-gray-100 px-3 py-1.5 text-sm font-medium text-gray-700 transition-colors duration-150 hover:bg-gray-200 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            <svg
              className="me-1.5 h-4 w-4"
              viewBox="0 0 16 16"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M9.965 11.026a5 5 0 1 1 1.06-1.06l2.755 2.754a.75.75 0 1 1-1.06 1.06l-2.755-2.754ZM10.5 7a3.5 3.5 0 1 1-7 0 3.5 3.5 0 0 1 7 0Z"
                clipRule="evenodd"
              />
            </svg>
            Search
          </button>
        </form>
      </div>

      {/* ── Error state ───────────────────────────────────────── */}
      {isError && (
        <div
          className="rounded-md border border-red-200 bg-red-50 p-4"
          role="alert"
        >
          <p className="text-sm text-red-700">
            Failed to load emails.{' '}
            {error instanceof Error
              ? error.message
              : 'An unexpected error occurred.'}
          </p>
        </div>
      )}

      {/* ── Data Table ────────────────────────────────────────── */}
      <div id="email-list-table" role="tabpanel">
        <DataTable<Email>
          data={emails}
          columns={columns}
          totalCount={totalCount}
          pageSize={queryParams.pageSize}
          currentPage={queryParams.page}
          onPageChange={handlePageChange}
          onPageSizeChange={handlePageSizeChange}
          onSortChange={handleSortChange}
          onRowClick={(record) => navigate(`/notifications/emails/${record.id}`)}
          loading={isLoading}
          emptyText="No emails found"
          hover
          striped
          responsiveBreakpoint="md"
          rowTestId="email-row"
        />
      </div>
    </div>
  );
}

export default EmailList;
