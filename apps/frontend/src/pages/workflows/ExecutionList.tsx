import React, { useState, useCallback, useMemo } from 'react';
import { Link, useSearchParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, del } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import Modal from '../../components/common/Modal';
import ScreenMessage, { useToast } from '../../components/common/ScreenMessage';
import { ScreenMessageType } from '../../types/common';

/* ─────────────────────────────────────────────────────────────
   Local types — mapped from monolith's Job.cs / JobStatus enum
   ───────────────────────────────────────────────────────────── */

/** Execution status values matching source JobStatus enum (Job.cs lines 6-13). */
enum ExecutionStatus {
  Pending = 1,
  Running = 2,
  Canceled = 3,
  Failed = 4,
  Finished = 5,
  Aborted = 6,
}

/** Single workflow execution record returned by the API. */
interface WorkflowExecution {
  id: string;
  workflowName: string;
  status: ExecutionStatus;
  createdOn: string;
  startedOn: string | null;
  finishedOn: string | null;
  errorMessage?: string;
  /** Index signature required by DataTable<T extends Record<string, unknown>> */
  [key: string]: unknown;
}

/** Shape of the paginated API response for execution listing. */
interface ExecutionListResponse {
  items: WorkflowExecution[];
  totalCount: number;
}

/* ─────────────────────────────────────────────────────────────
   Constants
   ───────────────────────────────────────────────────────────── */

/** Status badge configuration — label + Tailwind colour classes per status value. */
const STATUS_CONFIG: Record<ExecutionStatus, { label: string; className: string }> = {
  [ExecutionStatus.Pending]: {
    label: 'Pending',
    className: 'bg-yellow-100 text-yellow-800',
  },
  [ExecutionStatus.Running]: {
    label: 'Running',
    className: 'bg-blue-100 text-blue-800',
  },
  [ExecutionStatus.Canceled]: {
    label: 'Canceled',
    className: 'bg-gray-100 text-gray-600',
  },
  [ExecutionStatus.Failed]: {
    label: 'Failed',
    className: 'bg-red-100 text-red-800',
  },
  [ExecutionStatus.Finished]: {
    label: 'Finished',
    className: 'bg-green-100 text-green-800',
  },
  [ExecutionStatus.Aborted]: {
    label: 'Aborted',
    className: 'bg-orange-100 text-orange-800',
  },
};

/** Options exposed in the status filter checkboxes. */
const STATUS_OPTIONS = Object.entries(STATUS_CONFIG).map(([value, config]) => ({
  value: Number(value) as ExecutionStatus,
  label: config.label,
}));

/** Default page size matching monolith PagerSize = 15 (list.cshtml.cs line 24). */
const DEFAULT_PAGE_SIZE = 15;

/** Sub-navigation tabs replicating AdminPageUtils.GetJobAdminSubNav. */
const SUB_NAV_TABS = [
  { label: 'Workflows', path: '/workflows' },
  { label: 'Executions', path: '/workflows/executions' },
  { label: 'Schedules', path: '/workflows/schedules' },
] as const;

/* ─────────────────────────────────────────────────────────────
   Helpers
   ───────────────────────────────────────────────────────────── */

/**
 * Formats an ISO-8601 date string into a human-readable datetime.
 * Returns an em-dash when the value is null / undefined / invalid.
 */
function formatDateTime(dateStr: string | null | undefined): string {
  if (!dateStr) return '\u2014';
  try {
    const date = new Date(dateStr);
    if (Number.isNaN(date.getTime())) return '\u2014';
    return new Intl.DateTimeFormat('en-US', {
      year: 'numeric',
      month: 'short',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
    }).format(date);
  } catch {
    return '\u2014';
  }
}

/**
 * Parses a comma-separated string of numeric values into an ExecutionStatus array.
 * Silently discards non-numeric or out-of-range entries.
 */
function parseStatusParam(raw: string | null): ExecutionStatus[] {
  if (!raw) return [];
  return raw
    .split(',')
    .map(Number)
    .filter((n) => !Number.isNaN(n) && n >= ExecutionStatus.Pending && n <= ExecutionStatus.Aborted) as ExecutionStatus[];
}

/* ─────────────────────────────────────────────────────────────
   Component
   ───────────────────────────────────────────────────────────── */

/**
 * ExecutionList — Workflow Execution History Listing Page.
 *
 * Directly replaces the monolith's "Background Jobs" list page
 * (`list.cshtml` + `list.cshtml.cs`). Renders a paginated, sortable,
 * filterable data-table of workflow execution records with status badges,
 * datetime columns, a filter drawer, and a "Clear Completed" mutation.
 */
function ExecutionList(): React.ReactElement {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const queryClient = useQueryClient();
  const { messages, showToast, dismissToast } = useToast();

  /* ---- Local UI state ---- */
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);
  const [isConfirmModalOpen, setIsConfirmModalOpen] = useState(false);

  /* ---- Filter form state (committed to URL on Apply) ---- */
  const [filterWorkflowName, setFilterWorkflowName] = useState(
    searchParams.get('workflowName') ?? '',
  );
  const [filterStatuses, setFilterStatuses] = useState<ExecutionStatus[]>(() =>
    parseStatusParam(searchParams.get('statuses')),
  );
  const [filterDateFrom, setFilterDateFrom] = useState(
    searchParams.get('dateFrom') ?? '',
  );
  const [filterDateTo, setFilterDateTo] = useState(
    searchParams.get('dateTo') ?? '',
  );

  /* ---- Derive query params from URL ---- */
  const page = Number(searchParams.get('page')) || 1;
  const pageSize = Number(searchParams.get('pageSize')) || DEFAULT_PAGE_SIZE;
  const sortBy = searchParams.get('sortBy') ?? 'createdOn';
  const sortOrder = searchParams.get('sortOrder') ?? 'asc';

  /** Filters that are currently active (committed to URL). */
  const activeFilters = useMemo(
    () => ({
      workflowName: searchParams.get('workflowName') ?? '',
      statuses: parseStatusParam(searchParams.get('statuses')),
      dateFrom: searchParams.get('dateFrom') ?? '',
      dateTo: searchParams.get('dateTo') ?? '',
    }),
    [searchParams],
  );

  /* ---- Data fetching ---- */
  const {
    data: executionsResponse,
    isLoading,
    isError,
    error,
  } = useQuery<ExecutionListResponse>({
    queryKey: [
      'workflow-executions',
      { page, pageSize, sortBy, sortOrder, filters: activeFilters },
    ],
    queryFn: async () => {
      const params: Record<string, string> = {
        page: String(page),
        pageSize: String(pageSize),
        sortBy,
        sortOrder,
      };
      if (activeFilters.workflowName) {
        params.workflowName = activeFilters.workflowName;
      }
      if (activeFilters.statuses.length > 0) {
        params.statuses = activeFilters.statuses.join(',');
      }
      if (activeFilters.dateFrom) {
        params.dateFrom = activeFilters.dateFrom;
      }
      if (activeFilters.dateTo) {
        params.dateTo = activeFilters.dateTo;
      }

      const response = await get<ExecutionListResponse>(
        '/workflows/executions',
        params,
      );
      return response.object ?? { items: [], totalCount: 0 };
    },
  });

  /* ---- Clear Completed mutation (replaces OnPost → LogService.ClearJobLogs) ---- */
  const clearCompletedMutation = useMutation({
    mutationFn: async () => {
      const response = await del<void>('/workflows/executions/completed');
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workflow-executions'] });
      showToast(
        ScreenMessageType.Success,
        'Success',
        'Completed executions cleared successfully.',
      );
      setIsConfirmModalOpen(false);
    },
    onError: (err: Error) => {
      showToast(
        ScreenMessageType.Error,
        'Error',
        err.message || 'Failed to clear completed executions.',
      );
      setIsConfirmModalOpen(false);
    },
  });

  /* ─────────────────────────────────────────────────
     Event handlers
     ───────────────────────────────────────────────── */

  const handleOpenDrawer = useCallback(() => {
    setIsDrawerOpen(true);
  }, []);

  const handleCloseDrawer = useCallback(() => {
    setIsDrawerOpen(false);
  }, []);

  /** Commit the local filter values into URL search params. */
  const handleApplyFilters = useCallback(() => {
    const params = new URLSearchParams(searchParams);
    params.set('page', '1');

    if (filterWorkflowName) {
      params.set('workflowName', filterWorkflowName);
    } else {
      params.delete('workflowName');
    }

    if (filterStatuses.length > 0) {
      params.set('statuses', filterStatuses.join(','));
    } else {
      params.delete('statuses');
    }

    if (filterDateFrom) {
      params.set('dateFrom', filterDateFrom);
    } else {
      params.delete('dateFrom');
    }

    if (filterDateTo) {
      params.set('dateTo', filterDateTo);
    } else {
      params.delete('dateTo');
    }

    setSearchParams(params);
    setIsDrawerOpen(false);
  }, [
    searchParams,
    setSearchParams,
    filterWorkflowName,
    filterStatuses,
    filterDateFrom,
    filterDateTo,
  ]);

  /** Reset every filter field and navigate to the clean base URL. */
  const handleClearFilters = useCallback(() => {
    setFilterWorkflowName('');
    setFilterStatuses([]);
    setFilterDateFrom('');
    setFilterDateTo('');
    navigate('/workflows/executions');
    setIsDrawerOpen(false);
  }, [navigate]);

  /** Toggle a single status value inside the filter-status checkbox group. */
  const handleStatusToggle = useCallback((status: ExecutionStatus) => {
    setFilterStatuses((prev) =>
      prev.includes(status)
        ? prev.filter((s) => s !== status)
        : [...prev, status],
    );
  }, []);

  const handleClearCompletedClick = useCallback(() => {
    setIsConfirmModalOpen(true);
  }, []);

  const handleConfirmClear = useCallback(() => {
    clearCompletedMutation.mutate();
  }, [clearCompletedMutation]);

  const handleCancelClear = useCallback(() => {
    setIsConfirmModalOpen(false);
  }, []);

  /* ─────────────────────────────────────────────────
     Column definitions (memoised to avoid re-render)
     ───────────────────────────────────────────────── */

  const columns = useMemo<DataTableColumn<WorkflowExecution>[]>(
    () => [
      {
        id: 'action',
        label: 'Action',
        width: '1%',
        sortable: false,
        noWrap: true,
        cell: (_value: unknown, record: WorkflowExecution) => (
          <Link
            to={`/workflows/executions/${record.id}`}
            className="inline-flex items-center justify-center rounded px-2 py-1 text-sm font-medium text-blue-600 hover:text-blue-800 hover:bg-blue-50 transition-colors"
            title="View execution details"
          >
            {/* Eye icon — replaces source fa-eye from list.cshtml line 33 */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path d="M10 12.5a2.5 2.5 0 100-5 2.5 2.5 0 000 5z" />
              <path
                fillRule="evenodd"
                d="M.664 10.59a1.651 1.651 0 010-1.186A10.004 10.004 0 0110 3c4.257 0 7.893 2.66 9.336 6.41.147.381.146.804 0 1.186A10.004 10.004 0 0110 17c-4.257 0-7.893-2.66-9.336-6.41zM14 10a4 4 0 11-8 0 4 4 0 018 0z"
                clipRule="evenodd"
              />
            </svg>
          </Link>
        ),
      },
      {
        id: 'createdOn',
        label: 'Created On',
        width: '150px',
        sortable: true,
        accessorKey: 'createdOn',
        cell: (_value: unknown, record: WorkflowExecution) => (
          <span className="text-sm text-gray-700 whitespace-nowrap">
            {formatDateTime(record.createdOn)}
          </span>
        ),
      },
      {
        id: 'startedOn',
        label: 'Started On',
        width: '150px',
        sortable: true,
        accessorKey: 'startedOn',
        cell: (_value: unknown, record: WorkflowExecution) => (
          <span className="text-sm text-gray-700 whitespace-nowrap">
            {formatDateTime(record.startedOn)}
          </span>
        ),
      },
      {
        id: 'finishedOn',
        label: 'Finished On',
        width: '150px',
        sortable: true,
        accessorKey: 'finishedOn',
        cell: (_value: unknown, record: WorkflowExecution) => (
          <span className="text-sm text-gray-700 whitespace-nowrap">
            {formatDateTime(record.finishedOn)}
          </span>
        ),
      },
      {
        id: 'workflowName',
        label: 'Workflow Name',
        sortable: true,
        accessorKey: 'workflowName',
        cell: (_value: unknown, record: WorkflowExecution) => (
          <span className="text-sm text-gray-900">
            {record.workflowName || '\u2014'}
          </span>
        ),
      },
      {
        id: 'status',
        label: 'Status',
        width: '120px',
        sortable: true,
        accessorKey: 'status',
        cell: (_value: unknown, record: WorkflowExecution) => {
          const config = STATUS_CONFIG[record.status];
          if (!config) {
            return <span className="text-sm text-gray-500">Unknown</span>;
          }
          return (
            <span
              className={`inline-block rounded-full px-2 py-1 text-xs font-semibold ${config.className}`}
            >
              {config.label}
            </span>
          );
        },
      },
    ],
    [],
  );

  /** Number of currently-active filters, used for the badge on the Search button. */
  const activeFilterCount = useMemo(() => {
    let count = 0;
    if (activeFilters.workflowName) count += 1;
    if (activeFilters.statuses.length > 0) count += 1;
    if (activeFilters.dateFrom) count += 1;
    if (activeFilters.dateTo) count += 1;
    return count;
  }, [activeFilters]);

  /* ─────────────────────────────────────────────────
     Render
     ───────────────────────────────────────────────── */

  return (
    <div className="flex flex-col gap-6">
      {/* Toast notification container */}
      <ScreenMessage messages={messages} onDismiss={dismissToast} />

      {/* Breadcrumb */}
      <nav aria-label="Breadcrumb" className="text-sm">
        <ol className="flex items-center gap-1.5 text-gray-500">
          <li>
            <Link
              to="/workflows"
              className="hover:text-blue-600 transition-colors"
            >
              Workflows
            </Link>
          </li>
          <li aria-hidden="true" className="select-none">
            /
          </li>
          <li className="font-medium text-gray-900" aria-current="page">
            Executions
          </li>
        </ol>
      </nav>

      {/* Page header — replaces <wv-page-header> from list.cshtml lines 10-26 */}
      <header className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-3">
          {/* Icon container — replaces fa fa-cog */}
          <div className="flex h-10 w-10 items-center justify-center rounded-lg bg-blue-50 text-blue-600">
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-5 w-5"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M7.84 1.804A1 1 0 018.82 1h2.36a1 1 0 01.98.804l.331 1.652a6.993 6.993 0 011.929 1.115l1.598-.54a1 1 0 011.186.447l1.18 2.044a1 1 0 01-.205 1.251l-1.267 1.113a7.047 7.047 0 010 2.228l1.267 1.113a1 1 0 01.206 1.25l-1.18 2.045a1 1 0 01-1.187.447l-1.598-.54a6.993 6.993 0 01-1.929 1.115l-.33 1.652a1 1 0 01-.98.804H8.82a1 1 0 01-.98-.804l-.331-1.652a6.993 6.993 0 01-1.929-1.115l-1.598.54a1 1 0 01-1.186-.447l-1.18-2.044a1 1 0 01.205-1.251l1.267-1.114a7.05 7.05 0 010-2.227L1.821 7.773a1 1 0 01-.206-1.25l1.18-2.045a1 1 0 011.187-.447l1.598.54A6.993 6.993 0 017.51 3.456l.33-1.652zM10 13a3 3 0 100-6 3 3 0 000 6z"
                clipRule="evenodd"
              />
            </svg>
          </div>
          <h1 className="text-2xl font-bold text-gray-900">
            Workflow Executions
          </h1>
        </div>

        {/* Action buttons — Clear Completed + Search */}
        <div className="flex items-center gap-2">
          {/* Clear Completed — replaces "Clear Finished" form (list.cshtml lines 14-16) */}
          <button
            type="button"
            onClick={handleClearCompletedClick}
            disabled={clearCompletedMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md border border-red-300 bg-white px-3 py-2 text-sm font-medium text-red-700 shadow-sm hover:bg-red-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500 disabled:cursor-not-allowed disabled:opacity-50 transition-colors"
          >
            {clearCompletedMutation.isPending ? (
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
            ) : (
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-4 w-4"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M8.75 1A2.75 2.75 0 006 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 10.23 1.482l.149-.022.841 10.518A2.75 2.75 0 007.596 19h4.807a2.75 2.75 0 002.742-2.53l.841-10.52.149.023a.75.75 0 00.23-1.482A41.03 41.03 0 0014 3.68V3.75A2.75 2.75 0 0011.25 1h-2.5zM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325C8.327 4.025 9.16 4 10 4zM8.58 7.72a.75.75 0 00-1.5.06l.3 7.5a.75.75 0 101.5-.06l-.3-7.5zm4.34.06a.75.75 0 10-1.5-.06l-.3 7.5a.75.75 0 101.5.06l.3-7.5z"
                  clipRule="evenodd"
                />
              </svg>
            )}
            Clear Completed
          </button>

          {/* Search / filter toggle — replaces ErpEvent.DISPATCH drawer open */}
          <button
            type="button"
            onClick={handleOpenDrawer}
            className="relative inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500 transition-colors"
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
                d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
                clipRule="evenodd"
              />
            </svg>
            Search
            {activeFilterCount > 0 && (
              <span className="absolute -top-1.5 -end-1.5 flex h-5 w-5 items-center justify-center rounded-full bg-blue-600 text-xs font-bold text-white">
                {activeFilterCount}
              </span>
            )}
          </button>
        </div>
      </header>

      {/* Sub-navigation tabs — replaces AdminPageUtils.GetJobAdminSubNav("job") */}
      <nav
        aria-label="Workflow section navigation"
        className="border-b border-gray-200"
      >
        <ul className="-mb-px flex gap-0" role="tablist">
          {SUB_NAV_TABS.map((tab) => {
            const isActive = tab.path === '/workflows/executions';
            return (
              <li key={tab.path} role="presentation">
                <Link
                  to={tab.path}
                  role="tab"
                  aria-selected={isActive}
                  className={`inline-block border-b-2 px-4 py-3 text-sm font-medium transition-colors ${
                    isActive
                      ? 'border-blue-500 text-blue-600'
                      : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700'
                  }`}
                >
                  {tab.label}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>

      {/* Error alert */}
      {isError && (
        <div className="rounded-md bg-red-50 p-4" role="alert">
          <div className="flex items-start gap-3">
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="mt-0.5 h-5 w-5 shrink-0 text-red-400"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
                clipRule="evenodd"
              />
            </svg>
            <p className="text-sm text-red-700">
              {error instanceof Error
                ? error.message
                : 'Failed to load workflow executions. Please try again.'}
            </p>
          </div>
        </div>
      )}

      {/* Data table — replaces <wv-grid> from list.cshtml lines 28-71 */}
      <div className="overflow-x-auto rounded-lg border border-gray-200 bg-white shadow-sm">
        <DataTable<WorkflowExecution>
          data={executionsResponse?.items ?? []}
          columns={columns}
          totalCount={executionsResponse?.totalCount ?? 0}
          pageSize={pageSize}
          currentPage={page}
          loading={isLoading}
          emptyText="No executions found"
          bordered
          hover
        />
      </div>

      {/* Confirmation modal — replaces onclick="return confirm('Are you sure ?')" */}
      <Modal
        isVisible={isConfirmModalOpen}
        title="Clear Completed Executions"
        onClose={handleCancelClear}
        footer={
          <div className="flex justify-end gap-2">
            <button
              type="button"
              onClick={handleCancelClear}
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500 transition-colors"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmClear}
              disabled={clearCompletedMutation.isPending}
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50 transition-colors"
            >
              {clearCompletedMutation.isPending ? 'Clearing\u2026' : 'Clear'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to clear all completed workflow executions? This
          action cannot be undone.
        </p>
      </Modal>

      {/* Filter drawer — replaces <wv-drawer> from list.cshtml lines 76-80 */}
      <Drawer
        isVisible={isDrawerOpen}
        title="Filter Executions"
        onClose={handleCloseDrawer}
        width="360px"
      >
        <div className="flex flex-col gap-5 p-4">
          {/* Workflow Name — replaces type_name CONTAINS filter */}
          <div className="flex flex-col gap-1.5">
            <label
              htmlFor="filter-workflow-name"
              className="text-sm font-medium text-gray-700"
            >
              Workflow Name
            </label>
            <input
              id="filter-workflow-name"
              type="text"
              value={filterWorkflowName}
              onChange={(e) => setFilterWorkflowName(e.target.value)}
              placeholder="Search by workflow name\u2026"
              className="rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* Status — replaces type_id EQ filter, enhanced to multi-select */}
          <fieldset className="flex flex-col gap-1.5">
            <legend className="text-sm font-medium text-gray-700">
              Status
            </legend>
            <div className="flex flex-col gap-2">
              {STATUS_OPTIONS.map((opt) => (
                <label
                  key={opt.value}
                  className="inline-flex cursor-pointer items-center gap-2"
                >
                  <input
                    type="checkbox"
                    checked={filterStatuses.includes(opt.value)}
                    onChange={() => handleStatusToggle(opt.value)}
                    className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                  />
                  <span className="text-sm text-gray-700">{opt.label}</span>
                </label>
              ))}
            </div>
          </fieldset>

          {/* Date range — enhancement over source (created_on filter) */}
          <div className="flex flex-col gap-1.5">
            <label
              htmlFor="filter-date-from"
              className="text-sm font-medium text-gray-700"
            >
              Created From
            </label>
            <input
              id="filter-date-from"
              type="date"
              value={filterDateFrom}
              onChange={(e) => setFilterDateFrom(e.target.value)}
              className="rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <label
              htmlFor="filter-date-to"
              className="text-sm font-medium text-gray-700"
            >
              Created To
            </label>
            <input
              id="filter-date-to"
              type="date"
              value={filterDateTo}
              onChange={(e) => setFilterDateTo(e.target.value)}
              className="rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* Drawer action buttons — Apply + Clear All */}
          <div className="flex gap-2 border-t border-gray-200 pt-2">
            <button
              type="button"
              onClick={handleApplyFilters}
              className="flex-1 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 transition-colors"
            >
              Apply
            </button>
            <button
              type="button"
              onClick={handleClearFilters}
              className="flex-1 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500 transition-colors"
            >
              Clear All
            </button>
          </div>
        </div>
      </Drawer>
    </div>
  );
}

export default ExecutionList;
