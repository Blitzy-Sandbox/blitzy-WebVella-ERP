import { useState, useCallback, useMemo, type ReactNode } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { get, del } from '../../api/client';
import type { ApiResponse } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import Modal, { ModalSize } from '../../components/common/Modal';

/* ──────────────────────────────────────────────────────────────
   Types
   ────────────────────────────────────────────────────────────── */

/** Shape of a single diagnostic log record returned by the service API. */
interface LogRecord {
  /** Unique identifier for the log entry. */
  id: string;
  /** ISO-8601 timestamp when the log was created. */
  created_on: string;
  /** Log severity: 1 = error, anything else = info. */
  type: number;
  /** Originating module / class / service. */
  source: string;
  /** Human-readable log message. */
  message: string;
  /**
   * Notification delivery state.
   * 1 = do-not-notify, 2 = not-notified, 3 = notified, 4 = failed.
   */
  notification_status: number;
  /** Allow additional dynamic properties for JSON detail view. */
  [key: string]: unknown;
}

/** Envelope returned by GET /v1/diagnostics/logs. */
interface LogListResponse {
  records: LogRecord[];
  totalCount: number;
}

/* ──────────────────────────────────────────────────────────────
   Constants
   ────────────────────────────────────────────────────────────── */

/** Matches the monolith PagerSize = 15 from list.cshtml.cs. */
const PAGE_SIZE = 15;

/** API base path for diagnostic logs. */
const LOGS_ENDPOINT = '/v1/diagnostics/logs';

/* ──────────────────────────────────────────────────────────────
   Helpers
   ────────────────────────────────────────────────────────────── */

/**
 * Formats an ISO-8601 date string into a locale-appropriate datetime string.
 * Replaces the monolith's `ConvertToAppDate()` server helper.
 */
function formatDate(dateStr: string): string {
  if (!dateStr) return '';
  try {
    return new Intl.DateTimeFormat(undefined, {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    }).format(new Date(dateStr));
  } catch {
    return dateStr;
  }
}

/**
 * Renders the type icon for a log record.
 * type === 1 → red exclamation circle (error)
 * otherwise  → blue info circle
 */
function renderTypeIcon(type: number): ReactNode {
  if (type === 1) {
    return (
      <i
        className="fas fa-exclamation-circle text-red-500"
        aria-label="Error"
        role="img"
      />
    );
  }
  return (
    <i
      className="fas fa-info-circle text-blue-500"
      aria-label="Info"
      role="img"
    />
  );
}

/**
 * Renders a compound icon for the notification status column.
 * Matches the monolith's 4-state switch:
 *   1 → ban (do-not-notify)
 *   2 → hourglass (not-notified)
 *   3 → check (notified)
 *   4 → exclamation-triangle (failed)
 */
function renderNotificationStatus(status: number): ReactNode {
  switch (status) {
    case 1:
      return (
        <span title="Do not notify" className="inline-flex items-center gap-1">
          <i className="fas fa-envelope text-gray-500" aria-hidden="true" />
          <i className="fas fa-ban text-gray-400" aria-hidden="true" />
          <span className="sr-only">Do not notify</span>
        </span>
      );
    case 2:
      return (
        <span title="Not notified" className="inline-flex items-center gap-1">
          <i className="fas fa-envelope text-gray-500" aria-hidden="true" />
          <i
            className="fas fa-hourglass-half text-yellow-500"
            aria-hidden="true"
          />
          <span className="sr-only">Not notified</span>
        </span>
      );
    case 3:
      return (
        <span title="Notified" className="inline-flex items-center gap-1">
          <i className="fas fa-envelope text-gray-500" aria-hidden="true" />
          <i className="fas fa-check text-green-500" aria-hidden="true" />
          <span className="sr-only">Notified</span>
        </span>
      );
    case 4:
      return (
        <span title="Failed" className="inline-flex items-center gap-1">
          <i className="fas fa-envelope text-gray-500" aria-hidden="true" />
          <i
            className="fas fa-exclamation-triangle text-red-500"
            aria-hidden="true"
          />
          <span className="sr-only">Failed</span>
        </span>
      );
    default:
      return null;
  }
}

/* ──────────────────────────────────────────────────────────────
   Component
   ────────────────────────────────────────────────────────────── */

/**
 * Diagnostic log listing page.
 *
 * Route: `/admin/logs`
 *
 * Replaces `WebVella.Erp.Plugins.SDK/Pages/log/list.cshtml[.cs]`.
 * Features:
 * - Paginated DataTable (pageSize 15) with 6 columns
 * - Per-record JSON detail modal (ExtraLarge)
 * - "Clear logs" with confirmation
 * - Search drawer (source CONTAINS, message CONTAINS)
 * - Notification status 4-state icon rendering
 * - Error / info type icon rendering
 */
const LogList: React.FC = () => {
  /* ── URL-based pagination state ───────────────────────────── */
  const [searchParams, setSearchParams] = useSearchParams();
  const currentPage = Number(searchParams.get('page') || '1') || 1;

  /* ── Local UI state ───────────────────────────────────────── */
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedRecord, setSelectedRecord] = useState<LogRecord | null>(null);

  // Filter input values (live in the drawer)
  const [filterSource, setFilterSource] = useState('');
  const [filterMessage, setFilterMessage] = useState('');

  // Applied filter values (submitted to API)
  const [appliedSource, setAppliedSource] = useState('');
  const [appliedMessage, setAppliedMessage] = useState('');

  const queryClient = useQueryClient();

  /* ── Data fetching ────────────────────────────────────────── */
  const { data: logsResponse, isLoading } = useQuery<ApiResponse<LogListResponse>>({
    queryKey: ['admin-logs', currentPage, appliedSource, appliedMessage],
    queryFn: () => {
      const params: Record<string, string | number> = {
        page: currentPage,
        pageSize: PAGE_SIZE,
      };
      if (appliedSource) {
        params.source = appliedSource;
      }
      if (appliedMessage) {
        params.message = appliedMessage;
      }
      return get<LogListResponse>(LOGS_ENDPOINT, params);
    },
  });

  /* ── Clear-logs mutation ──────────────────────────────────── */
  const clearMutation = useMutation({
    mutationFn: () => del<void>(LOGS_ENDPOINT),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin-logs'] });
    },
  });

  /* ── Drawer handlers ──────────────────────────────────────── */
  const handleOpenDrawer = useCallback(() => setIsDrawerOpen(true), []);
  const handleCloseDrawer = useCallback(() => setIsDrawerOpen(false), []);

  /* ── Modal handlers ───────────────────────────────────────── */
  const handleOpenModal = useCallback((record: LogRecord) => {
    setSelectedRecord(record);
    setIsModalOpen(true);
  }, []);

  const handleCloseModal = useCallback(() => {
    setIsModalOpen(false);
    setSelectedRecord(null);
  }, []);

  /* ── Clear-logs handler (with window.confirm) ─────────────── */
  const handleClearLogs = useCallback(() => {
    if (window.confirm('Are you sure you want to clear all error logs?')) {
      clearMutation.mutate();
    }
  }, [clearMutation]);

  /* ── Filter handlers ──────────────────────────────────────── */
  const handleApplyFilters = useCallback(() => {
    setAppliedSource(filterSource);
    setAppliedMessage(filterMessage);
    setIsDrawerOpen(false);
    // Reset pagination to page 1 when filters change
    setSearchParams({ page: '1' });
  }, [filterSource, filterMessage, setSearchParams]);

  const handleClearFilters = useCallback(() => {
    setFilterSource('');
    setFilterMessage('');
    setAppliedSource('');
    setAppliedMessage('');
    setSearchParams({ page: '1' });
  }, [setSearchParams]);

  /* ── Page change (from DataTable) ─────────────────────────── */
  const handlePageChange = useCallback(
    (page: number) => {
      const newParams = new URLSearchParams(searchParams);
      newParams.set('page', String(page));
      setSearchParams(newParams);
    },
    [searchParams, setSearchParams],
  );

  /* ── Column definitions ───────────────────────────────────── */
  const columns = useMemo<DataTableColumn<LogRecord>[]>(
    () => [
      {
        id: 'action',
        label: '',
        width: '1%',
        cell: (_value: unknown, record: LogRecord) => (
          <button
            type="button"
            className="inline-flex items-center justify-center rounded p-1 text-blue-600 hover:bg-blue-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-500"
            onClick={() => handleOpenModal(record)}
            aria-label="View log details"
          >
            <i className="fas fa-eye" aria-hidden="true" />
          </button>
        ),
      },
      {
        id: 'created_on',
        label: 'Date',
        width: '150px',
        accessorKey: 'created_on',
        cell: (value: unknown) => (
          <span className="whitespace-nowrap text-sm">
            {formatDate(value as string)}
          </span>
        ),
      },
      {
        id: 'type',
        label: 'Type',
        width: '40px',
        accessorKey: 'type',
        cell: (value: unknown) => renderTypeIcon(value as number),
      },
      {
        id: 'source',
        label: 'Source',
        accessorKey: 'source',
      },
      {
        id: 'message',
        label: 'Message',
        accessorKey: 'message',
      },
      {
        id: 'notification_status',
        label: 'Status',
        width: '40px',
        accessorKey: 'notification_status',
        cell: (value: unknown) => renderNotificationStatus(value as number),
      },
    ],
    [handleOpenModal],
  );

  /* ── Derived data ─────────────────────────────────────────── */
  const records: LogRecord[] = logsResponse?.object?.records ?? [];
  const totalCount: number = logsResponse?.object?.totalCount ?? 0;

  /* ── Render ───────────────────────────────────────────────── */
  return (
    <div className="flex flex-col gap-4">
      {/* ── Page Header ──────────────────────────────────────── */}
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-200 pb-4">
        <div className="flex items-center gap-3">
          <div
            className="flex h-10 w-10 shrink-0 items-center justify-center rounded text-white"
            style={{ backgroundColor: '#dc3545' }}
            aria-hidden="true"
          >
            <i className="fas fa-sticky-note" />
          </div>
          <h1 className="text-xl font-semibold text-gray-800">
            Diagnostic Logs
          </h1>
        </div>

        <div className="flex items-center gap-2">
          <button
            type="button"
            className="inline-flex items-center gap-1.5 rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
            onClick={handleClearLogs}
            disabled={clearMutation.isPending}
          >
            <i className="fas fa-trash-alt" aria-hidden="true" />
            {clearMutation.isPending ? 'Clearing…' : 'Clear logs'}
          </button>

          <button
            type="button"
            className="inline-flex items-center gap-1.5 rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50"
            onClick={handleOpenDrawer}
          >
            <i className="fas fa-search" aria-hidden="true" />
            Search logs
          </button>
        </div>
      </div>

      {/* ── Active-filter indicator ──────────────────────────── */}
      {(appliedSource || appliedMessage) && (
        <div className="flex flex-wrap items-center gap-2 text-sm text-gray-600">
          <span className="font-medium">Active filters:</span>
          {appliedSource && (
            <span className="rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
              Source: {appliedSource}
            </span>
          )}
          {appliedMessage && (
            <span className="rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
              Message: {appliedMessage}
            </span>
          )}
          <button
            type="button"
            className="text-xs text-red-500 underline hover:text-red-700"
            onClick={handleClearFilters}
          >
            Clear
          </button>
        </div>
      )}

      {/* ── Data Table ───────────────────────────────────────── */}
      <DataTable<LogRecord>
        data={records}
        columns={columns}
        totalCount={totalCount}
        pageSize={PAGE_SIZE}
        currentPage={currentPage}
        onPageChange={handlePageChange}
        bordered
        hover
        loading={isLoading}
        emptyText="No logs found"
      />

      {/* ── JSON Detail Modal ────────────────────────────────── */}
      <Modal
        isVisible={isModalOpen}
        title="Log Details"
        size={ModalSize.ExtraLarge}
        onClose={handleCloseModal}
        footer={
          <button
            type="button"
            className="rounded border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50"
            onClick={handleCloseModal}
          >
            Close
          </button>
        }
      >
        {selectedRecord ? (
          <pre className="max-h-[60vh] overflow-auto rounded bg-gray-50 p-4 text-sm leading-relaxed">
            <code className="language-json">
              {JSON.stringify(selectedRecord, null, 2)}
            </code>
          </pre>
        ) : null}
      </Modal>

      {/* ── Search / Filter Drawer ───────────────────────────── */}
      <Drawer
        isVisible={isDrawerOpen}
        width="400px"
        title="Search logs"
        id="logSearchDrawer"
        onClose={handleCloseDrawer}
        titleAction={
          <button
            type="button"
            className="text-sm text-blue-600 hover:text-blue-800 hover:underline"
            onClick={handleClearFilters}
          >
            clear all
          </button>
        }
      >
        <div className="flex flex-col gap-4 p-4">
          {/* Source filter */}
          <div>
            <label
              htmlFor="log-filter-source"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Source{' '}
              <span className="text-xs font-normal text-gray-400">
                (contains)
              </span>
            </label>
            <input
              id="log-filter-source"
              type="text"
              value={filterSource}
              onChange={(e) => setFilterSource(e.target.value)}
              className="w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              placeholder="Filter by source…"
            />
          </div>

          {/* Message filter */}
          <div>
            <label
              htmlFor="log-filter-message"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Message{' '}
              <span className="text-xs font-normal text-gray-400">
                (contains)
              </span>
            </label>
            <input
              id="log-filter-message"
              type="text"
              value={filterMessage}
              onChange={(e) => setFilterMessage(e.target.value)}
              className="w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              placeholder="Filter by message…"
            />
          </div>

          {/* Apply button */}
          <button
            type="button"
            className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-500"
            onClick={handleApplyFilters}
          >
            Apply Filters
          </button>
        </div>
      </Drawer>
    </div>
  );
};

export default LogList;
