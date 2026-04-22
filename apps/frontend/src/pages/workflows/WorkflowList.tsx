/**
 * WorkflowList.tsx — Workflow Definition Listing Page
 *
 * React page component for listing all workflow definitions. Replaces the
 * monolith's JobManager static JobType registry view and SDK admin job
 * listing pages (list.cshtml / list.cshtml.cs). In the target serverless
 * architecture this displays Step Functions state-machine definitions with
 * status, priority, single-instance flag, last execution summary, and
 * schedule counts.
 *
 * Default export for React.lazy() route-level code-splitting under the
 * /workflows route group.
 */

// ── External imports ───────────────────────────────────────────────────
import { useState, useCallback, useMemo } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';

// ── Internal imports ───────────────────────────────────────────────────
import { post, del } from '../../api/client';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import { useWorkflows } from '../../hooks/useWorkflows';
import Modal from '../../components/common/Modal';
import ScreenMessage, { useToast } from '../../components/common/ScreenMessage';
import { ScreenMessageType } from '../../types/common';

// ── Local types ────────────────────────────────────────────────────────

/**
 * Represents a workflow definition row in the listing table.
 * The GET /v1/workflows endpoint returns definition-level objects that
 * include embedded last-execution summary and schedule metadata.
 */
interface WorkflowDefinition {
  /** Unique workflow definition identifier */
  id: string;
  /** Human-readable workflow name (maps to monolith JobType.Name) */
  name: string;
  /** Workflow type name for grouping */
  typeName: string;
  /** Optional description summary */
  description?: string;
  /** Whether this workflow definition is currently enabled */
  isActive: boolean;
  /** Default execution priority 1-5 (maps to JobPriority enum) */
  priority: number;
  /** Whether only one instance may execute concurrently */
  allowSingleInstance: boolean;
  /** Status code of the most recent execution (null if never run) */
  lastExecutionStatus?: number | null;
  /** ISO timestamp of the most recent execution */
  lastExecutionTime?: string | null;
  /** Count of associated schedule plans */
  schedulesCount: number;
  /** Index signature required by DataTable<T extends Record<string, unknown>> */
  [key: string]: unknown;
}

// ── Constants ──────────────────────────────────────────────────────────

/** Default number of rows per page — matches monolith PagerSize=15 */
const DEFAULT_PAGE_SIZE = 15;

/** Priority level display configuration: label + Tailwind badge classes */
const PRIORITY_CONFIG: Record<number, { label: string; bg: string; text: string }> = {
  1: { label: 'Low', bg: 'bg-gray-100', text: 'text-gray-700' },
  2: { label: 'Medium', bg: 'bg-blue-100', text: 'text-blue-700' },
  3: { label: 'High', bg: 'bg-yellow-100', text: 'text-yellow-800' },
  4: { label: 'Higher', bg: 'bg-orange-100', text: 'text-orange-700' },
  5: { label: 'Highest', bg: 'bg-red-100', text: 'text-red-700' },
};

/** Execution status display configuration */
const EXECUTION_STATUS_CONFIG: Record<number, { label: string; cls: string }> = {
  1: { label: 'Pending', cls: 'bg-yellow-100 text-yellow-800' },
  2: { label: 'Running', cls: 'bg-blue-100 text-blue-700' },
  3: { label: 'Canceled', cls: 'bg-gray-100 text-gray-600' },
  4: { label: 'Failed', cls: 'bg-red-100 text-red-700' },
  5: { label: 'Finished', cls: 'bg-green-100 text-green-800' },
};

/** Options for the priority multi-select filter */
const PRIORITY_OPTIONS = [
  { value: '1', label: 'Low' },
  { value: '2', label: 'Medium' },
  { value: '3', label: 'High' },
  { value: '4', label: 'Higher' },
  { value: '5', label: 'Highest' },
] as const;

/** Sub-navigation tab configuration (replaces AdminPageUtils.GetJobAdminSubNav) */
const SUB_NAV_TABS = [
  { label: 'Workflows', path: '/workflows' },
  { label: 'Executions', path: '/workflows/executions' },
  { label: 'Schedules', path: '/workflows/schedules' },
] as const;

// ── Helper functions ───────────────────────────────────────────────────

/**
 * Formats an ISO date string to a human-readable locale string.
 * Returns empty string for null/undefined/invalid dates.
 */
function formatDateTime(dateStr: string | null | undefined): string {
  if (!dateStr) return '';
  try {
    const date = new Date(dateStr);
    if (Number.isNaN(date.getTime())) return '';
    return new Intl.DateTimeFormat('en-US', {
      year: 'numeric',
      month: 'short',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
    }).format(date);
  } catch {
    return '';
  }
}

// ── Presentational sub-components ──────────────────────────────────────

/** Renders a colored priority badge: Low=gray, Medium=blue, High=yellow, Higher=orange, Highest=red */
function PriorityBadge({ priority }: { priority: number }) {
  const config = PRIORITY_CONFIG[priority] ?? PRIORITY_CONFIG[1];
  return (
    <span
      className={`inline-flex items-center rounded-full px-2 py-1 text-xs font-semibold ${config.bg} ${config.text}`}
    >
      {config.label}
    </span>
  );
}

/** Renders an Active (green) or Inactive (gray) status badge */
function StatusBadge({ isActive }: { isActive: boolean }) {
  return isActive ? (
    <span className="inline-flex items-center rounded-full bg-green-100 px-2 py-1 text-xs font-semibold text-green-800">
      Active
    </span>
  ) : (
    <span className="inline-flex items-center rounded-full bg-gray-100 px-2 py-1 text-xs font-semibold text-gray-600">
      Inactive
    </span>
  );
}

/** Renders execution status badge or "Never" placeholder */
function ExecutionStatusBadge({ status, time }: { status?: number | null; time?: string | null }) {
  if (status == null) {
    return <span className="text-sm text-gray-400">Never</span>;
  }
  const config = EXECUTION_STATUS_CONFIG[status] ?? { label: 'Unknown', cls: 'bg-gray-100 text-gray-600' };
  const formatted = formatDateTime(time);
  return (
    <div className="flex flex-col gap-0.5">
      <span className={`inline-flex w-fit items-center rounded-full px-2 py-0.5 text-xs font-semibold ${config.cls}`}>
        {config.label}
      </span>
      {formatted && <span className="text-xs text-gray-500">{formatted}</span>}
    </div>
  );
}

/** Statistics summary card for the dashboard row */
function StatCard({ label, value, color }: { label: string; value: number | string; color: string }) {
  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
      <p className="truncate text-sm text-gray-500">{label}</p>
      <p className={`mt-1 text-2xl font-semibold ${color}`}>{String(value)}</p>
    </div>
  );
}

// ── SVG icon helpers (inline, no external deps, fill="currentColor") ──

function IconCog() {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" fill="currentColor" className="h-6 w-6" aria-hidden="true">
      <path fillRule="evenodd" d="M7.84 1.804A1 1 0 0 1 8.82 1h2.36a1 1 0 0 1 .98.804l.331 1.652a6.993 6.993 0 0 1 1.929 1.115l1.598-.54a1 1 0 0 1 1.186.447l1.18 2.044a1 1 0 0 1-.205 1.251l-1.267 1.113a7.047 7.047 0 0 1 0 2.228l1.267 1.113a1 1 0 0 1 .206 1.25l-1.18 2.045a1 1 0 0 1-1.187.447l-1.598-.54a6.993 6.993 0 0 1-1.929 1.115l-.33 1.652a1 1 0 0 1-.98.804H8.82a1 1 0 0 1-.98-.804l-.331-1.652a6.993 6.993 0 0 1-1.929-1.115l-1.598.54a1 1 0 0 1-1.186-.447l-1.18-2.044a1 1 0 0 1 .205-1.251l1.267-1.114a7.05 7.05 0 0 1 0-2.227L1.821 7.773a1 1 0 0 1-.206-1.25l1.18-2.045a1 1 0 0 1 1.187-.447l1.598.54A6.992 6.992 0 0 1 7.51 3.456l.33-1.652ZM10 13a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z" clipRule="evenodd" />
    </svg>
  );
}

function IconEye() {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
      <path d="M10 12.5a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5Z" />
      <path fillRule="evenodd" d="M.664 10.59a1.651 1.651 0 0 1 0-1.186A10.004 10.004 0 0 1 10 3c4.257 0 7.893 2.66 9.336 6.41.147.381.146.804 0 1.186A10.004 10.004 0 0 1 10 17c-4.257 0-7.893-2.66-9.336-6.41ZM14 10a4 4 0 1 1-8 0 4 4 0 0 1 8 0Z" clipRule="evenodd" />
    </svg>
  );
}

function IconPencil() {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
      <path d="m5.433 13.917 1.262-3.155A4 4 0 0 1 7.58 9.42l6.92-6.918a2.121 2.121 0 0 1 3 3l-6.92 6.918c-.383.383-.84.685-1.343.886l-3.154 1.262a.5.5 0 0 1-.65-.65Z" />
      <path d="M3.5 5.75c0-.69.56-1.25 1.25-1.25H10A.75.75 0 0 0 10 3H4.75A2.75 2.75 0 0 0 2 5.75v9.5A2.75 2.75 0 0 0 4.75 18h9.5A2.75 2.75 0 0 0 17 15.25V10a.75.75 0 0 0-1.5 0v5.25c0 .69-.56 1.25-1.25 1.25h-9.5c-.69 0-1.25-.56-1.25-1.25v-9.5Z" />
    </svg>
  );
}

function IconPlay() {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
      <path fillRule="evenodd" d="M2 10a8 8 0 1 1 16 0 8 8 0 0 1-16 0Zm6.39-2.908a.75.75 0 0 1 .766.027l3.5 2.25a.75.75 0 0 1 0 1.262l-3.5 2.25A.75.75 0 0 1 8 12.25v-4.5a.75.75 0 0 1 .39-.658Z" clipRule="evenodd" />
    </svg>
  );
}

function IconTrash() {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
      <path fillRule="evenodd" d="M8.75 1A2.75 2.75 0 0 0 6 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 1 0 .23 1.482l.149-.022.841 10.518A2.75 2.75 0 0 0 7.596 19h4.807a2.75 2.75 0 0 0 2.742-2.53l.841-10.519.149.023a.75.75 0 0 0 .23-1.482A41.03 41.03 0 0 0 14 4.193V3.75A2.75 2.75 0 0 0 11.25 1h-2.5ZM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325C8.327 4.025 9.16 4 10 4ZM8.58 7.72a.75.75 0 0 1 .7.798l-.2 4.5a.75.75 0 0 1-1.496-.066l.2-4.5a.75.75 0 0 1 .796-.731ZM11.42 7.72a.75.75 0 0 1 .798.731l.2 4.5a.75.75 0 1 1-1.496.066l-.2-4.5a.75.75 0 0 1 .697-.798Z" clipRule="evenodd" />
    </svg>
  );
}

function IconSearch() {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
      <path fillRule="evenodd" d="M9 3.5a5.5 5.5 0 1 0 0 11 5.5 5.5 0 0 0 0-11ZM2 9a7 7 0 1 1 12.452 4.391l3.328 3.329a.75.75 0 1 1-1.06 1.06l-3.329-3.328A7 7 0 0 1 2 9Z" clipRule="evenodd" />
    </svg>
  );
}

function IconPlus() {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
      <path d="M10.75 4.75a.75.75 0 0 0-1.5 0v4.5h-4.5a.75.75 0 0 0 0 1.5h4.5v4.5a.75.75 0 0 0 1.5 0v-4.5h4.5a.75.75 0 0 0 0-1.5h-4.5v-4.5Z" />
    </svg>
  );
}

function IconChevronRight() {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" fill="currentColor" className="h-3 w-3 text-gray-400" aria-hidden="true">
      <path fillRule="evenodd" d="M8.22 5.22a.75.75 0 0 1 1.06 0l4.25 4.25a.75.75 0 0 1 0 1.06l-4.25 4.25a.75.75 0 0 1-1.06-1.06L11.94 10 8.22 6.28a.75.75 0 0 1 0-1.06Z" clipRule="evenodd" />
    </svg>
  );
}

// ════════════════════════════════════════════════════════════════════════
// MAIN COMPONENT
// ════════════════════════════════════════════════════════════════════════

/**
 * WorkflowList — primary landing page for the /workflows route group.
 * Displays a paginated, sortable, filterable listing of all workflow
 * definitions with quick-action capabilities (view, edit, run, delete).
 */
export default function WorkflowList() {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const queryClient = useQueryClient();
  const { messages, showToast, dismissToast } = useToast();

  // ── Local UI state ───────────────────────────────────────────────
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);
  const [isDeleteModalOpen, setIsDeleteModalOpen] = useState(false);
  const [selectedWorkflowId, setSelectedWorkflowId] = useState<string | null>(null);

  // Filter form state — initialised from URL, pushed back on Apply
  const [filterName, setFilterName] = useState(searchParams.get('name') ?? '');
  const [filterStatus, setFilterStatus] = useState(searchParams.get('status') ?? '');
  const [filterPriority, setFilterPriority] = useState<string[]>(() => {
    const raw = searchParams.get('priority');
    return raw ? raw.split(',').filter(Boolean) : [];
  });

  // ── Derive query params from URL search params ───────────────────
  const page = Number(searchParams.get('page') ?? '1') || 1;
  const pageSize =
    Number(searchParams.get('pageSize') ?? String(DEFAULT_PAGE_SIZE)) || DEFAULT_PAGE_SIZE;

  const queryParams = useMemo(
    () => ({
      page,
      pageSize,
      status: searchParams.get('status') ? Number(searchParams.get('status')) : undefined,
      priority: searchParams.get('priority')
        ? Number(searchParams.get('priority')!.split(',')[0])
        : undefined,
      typeId: searchParams.get('typeId') ?? undefined,
    }),
    [page, pageSize, searchParams],
  );

  // ── Data fetching via TanStack Query ─────────────────────────────
  const { data, isLoading, isError, error, isFetching } = useWorkflows(queryParams);

  // Safely extract list data — handles both ApiResponse<T>.object and direct T
  const responseData = (data as unknown as Record<string, unknown>)?.object ?? data;
  const items: WorkflowDefinition[] = useMemo(() => {
    const raw = (responseData as unknown as Record<string, unknown>)?.items;
    return Array.isArray(raw) ? (raw as WorkflowDefinition[]) : [];
  }, [responseData]);
  const totalCount =
    ((responseData as unknown as Record<string, unknown>)?.totalCount as number) ?? 0;

  // ── Mutations ────────────────────────────────────────────────────
  const executeMutation = useMutation({
    mutationFn: (id: string) => post(`/v1/workflows/${id}/execute`, {}),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      showToast(ScreenMessageType.Success, 'Success', 'Workflow execution started');
    },
    onError: (err: Error) => {
      showToast(
        ScreenMessageType.Error,
        'Error',
        err.message || 'Failed to execute workflow',
      );
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => del(`/v1/workflows/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      showToast(ScreenMessageType.Success, 'Success', 'Workflow deleted');
      setIsDeleteModalOpen(false);
      setSelectedWorkflowId(null);
    },
    onError: (err: Error) => {
      showToast(
        ScreenMessageType.Error,
        'Error',
        err.message || 'Failed to delete workflow',
      );
    },
  });

  // ── Event handlers ───────────────────────────────────────────────
  const handleRunNow = useCallback(
    (id: string) => {
      executeMutation.mutate(id);
    },
    [executeMutation],
  );

  const handleDeleteClick = useCallback((id: string) => {
    setSelectedWorkflowId(id);
    setIsDeleteModalOpen(true);
  }, []);

  const handleConfirmDelete = useCallback(() => {
    if (selectedWorkflowId) {
      deleteMutation.mutate(selectedWorkflowId);
    }
  }, [selectedWorkflowId, deleteMutation]);

  const handleCancelDelete = useCallback(() => {
    setIsDeleteModalOpen(false);
    setSelectedWorkflowId(null);
  }, []);

  const handleFilterApply = useCallback(() => {
    const params = new URLSearchParams(searchParams);
    params.set('page', '1');

    if (filterName.trim()) {
      params.set('name', filterName.trim());
    } else {
      params.delete('name');
    }

    if (filterStatus) {
      params.set('status', filterStatus);
    } else {
      params.delete('status');
    }

    if (filterPriority.length > 0) {
      params.set('priority', filterPriority.join(','));
    } else {
      params.delete('priority');
    }

    setSearchParams(params);
    setIsDrawerOpen(false);
  }, [searchParams, filterName, filterStatus, filterPriority, setSearchParams]);

  const handleFilterClear = useCallback(() => {
    setFilterName('');
    setFilterStatus('');
    setFilterPriority([]);
    setSearchParams(new URLSearchParams());
    setIsDrawerOpen(false);
  }, [setSearchParams]);

  const handlePriorityToggle = useCallback((value: string) => {
    setFilterPriority((prev) =>
      prev.includes(value) ? prev.filter((v) => v !== value) : [...prev, value],
    );
  }, []);

  // ── Computed statistics ──────────────────────────────────────────
  const stats = useMemo(() => {
    const active = items.filter((w) => w.isActive).length;
    const running = items.filter((w) => w.lastExecutionStatus === 2).length;
    const failed = items.filter((w) => w.lastExecutionStatus === 4).length;
    return { total: totalCount, active, running, failed };
  }, [items, totalCount]);

  // ── Lookup workflow for delete modal warning ─────────────────────
  const selectedWorkflow = useMemo(
    () => items.find((w) => w.id === selectedWorkflowId) ?? null,
    [items, selectedWorkflowId],
  );

  // ── Column definitions (memoised) ────────────────────────────────
  const columns = useMemo<DataTableColumn<WorkflowDefinition>[]>(
    () => [
      /* ─── Actions ─── */
      {
        id: 'actions',
        label: 'Actions',
        width: '160px',
        horizontalAlign: 'center',
        cell: (_value: unknown, record: WorkflowDefinition) => (
          <div className="flex items-center justify-center gap-1">
            <Link
              to={`/workflows/${record.id}`}
              className="inline-flex items-center justify-center rounded p-1.5 text-gray-500 transition-colors duration-200 hover:bg-gray-100 hover:text-gray-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              title="View details"
            >
              <IconEye />
              <span className="sr-only">View</span>
            </Link>
            <Link
              to={`/workflows/${record.id}/edit`}
              className="inline-flex items-center justify-center rounded p-1.5 text-gray-500 transition-colors duration-200 hover:bg-gray-100 hover:text-gray-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              title="Edit"
            >
              <IconPencil />
              <span className="sr-only">Edit</span>
            </Link>
            <button
              type="button"
              className="inline-flex items-center justify-center rounded p-1.5 text-green-600 transition-colors duration-200 hover:bg-green-50 hover:text-green-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-500 disabled:pointer-events-none disabled:opacity-50"
              title="Run Now"
              disabled={executeMutation.isPending}
              onClick={() => handleRunNow(record.id)}
            >
              <IconPlay />
              <span className="sr-only">Run Now</span>
            </button>
            <button
              type="button"
              className="inline-flex items-center justify-center rounded p-1.5 text-red-500 transition-colors duration-200 hover:bg-red-50 hover:text-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500 disabled:pointer-events-none disabled:opacity-50"
              title="Delete"
              disabled={deleteMutation.isPending}
              onClick={() => handleDeleteClick(record.id)}
            >
              <IconTrash />
              <span className="sr-only">Delete</span>
            </button>
          </div>
        ),
      },
      /* ─── Status ─── */
      {
        id: 'status',
        label: 'Status',
        width: '100px',
        accessorFn: (record: WorkflowDefinition) => (record.isActive ? 1 : 0),
        cell: (_value: unknown, record: WorkflowDefinition) => (
          <StatusBadge isActive={record.isActive} />
        ),
      },
      /* ─── Name ─── */
      {
        id: 'name',
        label: 'Name',
        sortable: true,
        accessorFn: (record: WorkflowDefinition) => record.name || record.typeName || '',
        cell: (_value: unknown, record: WorkflowDefinition) => (
          <div className="flex flex-col">
            <Link
              to={`/workflows/${record.id}`}
              className="font-medium text-blue-600 hover:text-blue-800 hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-500"
            >
              {record.name || record.typeName || 'Untitled'}
            </Link>
            {record.description && (
              <span className="mt-0.5 line-clamp-1 text-xs text-gray-500">
                {record.description}
              </span>
            )}
          </div>
        ),
      },
      /* ─── Priority ─── */
      {
        id: 'priority',
        label: 'Priority',
        width: '110px',
        sortable: true,
        horizontalAlign: 'center',
        accessorFn: (record: WorkflowDefinition) => record.priority,
        cell: (_value: unknown, record: WorkflowDefinition) => (
          <PriorityBadge priority={record.priority} />
        ),
      },
      /* ─── Single Instance ─── */
      {
        id: 'singleInstance',
        label: 'Single Instance',
        width: '120px',
        horizontalAlign: 'center',
        accessorFn: (record: WorkflowDefinition) => record.allowSingleInstance,
        cell: (_value: unknown, record: WorkflowDefinition) =>
          record.allowSingleInstance ? (
            <span className="inline-flex items-center gap-1 text-sm font-medium text-amber-700">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="currentColor" className="h-3.5 w-3.5" aria-hidden="true">
                <path fillRule="evenodd" d="M8 1a3.5 3.5 0 0 0-3.5 3.5V7A1.5 1.5 0 0 0 3 8.5v5A1.5 1.5 0 0 0 4.5 15h7a1.5 1.5 0 0 0 1.5-1.5v-5A1.5 1.5 0 0 0 11.5 7V4.5A3.5 3.5 0 0 0 8 1Zm2 6V4.5a2 2 0 1 0-4 0V7h4Z" clipRule="evenodd" />
              </svg>
              Yes
            </span>
          ) : (
            <span className="text-sm text-gray-400">No</span>
          ),
      },
      /* ─── Last Execution ─── */
      {
        id: 'lastExecution',
        label: 'Last Execution',
        width: '150px',
        accessorFn: (record: WorkflowDefinition) => record.lastExecutionTime ?? '',
        cell: (_value: unknown, record: WorkflowDefinition) => (
          <ExecutionStatusBadge
            status={record.lastExecutionStatus}
            time={record.lastExecutionTime}
          />
        ),
      },
      /* ─── Schedules ─── */
      {
        id: 'schedules',
        label: 'Schedules',
        width: '100px',
        horizontalAlign: 'center',
        accessorFn: (record: WorkflowDefinition) => record.schedulesCount ?? 0,
        cell: (_value: unknown, record: WorkflowDefinition) => {
          const count = record.schedulesCount ?? 0;
          return count > 0 ? (
            <Link
              to={`/workflows/schedules?workflowId=${record.id}`}
              className="font-medium text-blue-600 hover:text-blue-800 hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-500"
            >
              {count}
            </Link>
          ) : (
            <span className="text-sm text-gray-400">0</span>
          );
        },
      },
    ],
    [handleRunNow, handleDeleteClick, executeMutation.isPending, deleteMutation.isPending],
  );

  // ── Active filter count (for badge) ──────────────────────────────
  const activeFilterCount = useMemo(() => {
    let count = 0;
    if (searchParams.get('name')) count += 1;
    if (searchParams.get('status')) count += 1;
    if (searchParams.get('priority')) count += 1;
    return count;
  }, [searchParams]);

  // ════════════════════════════════════════════════════════════════════
  // RENDER
  // ════════════════════════════════════════════════════════════════════
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      {/* ── Toast notifications ──────────────────────────────────── */}
      <ScreenMessage messages={messages} onDismiss={dismissToast} />

      {/* ── Page header ──────────────────────────────────────────── */}
      <header className="border-b border-gray-200 bg-white px-6 py-4">
        {/* Breadcrumb */}
        <nav aria-label="Breadcrumb" className="mb-3">
          <ol className="flex items-center gap-1 text-sm text-gray-500">
            <li>
              <Link
                to="/"
                className="hover:text-gray-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-500"
              >
                Home
              </Link>
            </li>
            <li aria-hidden="true"><IconChevronRight /></li>
            <li aria-current="page" className="font-medium text-gray-900">
              Workflows
            </li>
          </ol>
        </nav>

        {/* Title row */}
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-blue-50 text-blue-600">
              <IconCog />
            </div>
            <h1 className="text-xl font-semibold text-gray-900">Workflows</h1>
          </div>

          <div className="flex items-center gap-2">
            {/* Search / Filter toggle */}
            <button
              type="button"
              className="relative inline-flex items-center gap-1.5 rounded-lg border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors duration-200 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              onClick={() => setIsDrawerOpen(true)}
            >
              <IconSearch />
              Search
              {activeFilterCount > 0 && (
                <span className="ml-1 inline-flex h-5 min-w-5 items-center justify-center rounded-full bg-blue-600 px-1 text-xs font-semibold text-white">
                  {activeFilterCount}
                </span>
              )}
            </button>

            {/* Create Workflow */}
            <button
              type="button"
              className="inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors duration-200 hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              onClick={() => navigate('/workflows/create')}
            >
              <IconPlus />
              Create Workflow
            </button>
          </div>
        </div>
      </header>

      {/* ── Sub-navigation tabs ──────────────────────────────────── */}
      <nav aria-label="Workflow navigation" className="border-b border-gray-200 bg-white px-6">
        <ul className="flex gap-6" role="tablist">
          {SUB_NAV_TABS.map((tab) => {
            const isActive = tab.path === '/workflows';
            return (
              <li key={tab.path} role="presentation">
                <Link
                  to={tab.path}
                  role="tab"
                  aria-selected={isActive}
                  className={`inline-flex items-center border-b-2 px-1 py-3 text-sm font-medium transition-colors duration-200 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-500 ${
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

      {/* ── Statistics cards ──────────────────────────────────────── */}
      <section aria-label="Workflow statistics" className="bg-gray-50 px-6 py-4">
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
          <StatCard label="Total Workflows" value={stats.total} color="text-gray-900" />
          <StatCard label="Active" value={stats.active} color="text-green-700" />
          <StatCard label="Running" value={stats.running} color="text-blue-700" />
          <StatCard label="Failed (24h)" value={stats.failed} color="text-red-700" />
        </div>
      </section>

      {/* ── Main content area ────────────────────────────────────── */}
      <section className="flex flex-1 flex-col overflow-hidden px-6 py-4">
        {isError ? (
          /* ── Error state ────────────────────────────────────────── */
          <div
            className="rounded-lg border border-red-200 bg-red-50 p-6 text-center"
            role="alert"
          >
            <p className="text-sm font-medium text-red-800">
              Failed to load workflows.
            </p>
            <p className="mt-1 text-sm text-red-600">
              {(error as Error)?.message || 'An unexpected error occurred.'}
            </p>
            <button
              type="button"
              className="mt-3 rounded-lg bg-red-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-red-500"
              onClick={() => queryClient.invalidateQueries({ queryKey: ['workflows'] })}
            >
              Retry
            </button>
          </div>
        ) : !isLoading && items.length === 0 ? (
          /* ── Empty state ────────────────────────────────────────── */
          <div className="flex flex-1 flex-col items-center justify-center rounded-lg border-2 border-dashed border-gray-300 bg-white p-12 text-center">
            <div className="flex h-12 w-12 items-center justify-center rounded-full bg-gray-100 text-gray-400">
              <IconCog />
            </div>
            <h2 className="mt-4 text-sm font-semibold text-gray-900">
              No workflows found
            </h2>
            <p className="mt-1 text-sm text-gray-500">
              {activeFilterCount > 0
                ? 'Try adjusting your filters to see results.'
                : 'Create your first workflow to get started.'}
            </p>
            {activeFilterCount > 0 ? (
              <button
                type="button"
                className="mt-4 rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-500"
                onClick={handleFilterClear}
              >
                Clear Filters
              </button>
            ) : (
              <button
                type="button"
                className="mt-4 inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-500"
                onClick={() => navigate('/workflows/create')}
              >
                <IconPlus />
                Create Workflow
              </button>
            )}
          </div>
        ) : (
          /* ── Data table ─────────────────────────────────────────── */
          <div className="flex-1 overflow-auto rounded-lg border border-gray-200 bg-white">
            <DataTable<WorkflowDefinition>
              columns={columns}
              data={items}
              totalCount={totalCount}
              currentPage={page}
              pageSize={pageSize}
              loading={isLoading || isFetching}
              emptyText="No workflows match the current filters."
              onPageChange={(newPage) => {
                const params = new URLSearchParams(searchParams);
                params.set('page', String(newPage));
                setSearchParams(params);
              }}
              onPageSizeChange={(newSize) => {
                const params = new URLSearchParams(searchParams);
                params.set('pageSize', String(newSize));
                params.set('page', '1');
                setSearchParams(params);
              }}
              onSortChange={(sortBy, sortOrder) => {
                const params = new URLSearchParams(searchParams);
                params.set('sortBy', sortBy);
                params.set('sortOrder', sortOrder);
                params.set('page', '1');
                setSearchParams(params);
              }}
            />
          </div>
        )}
      </section>

      {/* ── Filter drawer ────────────────────────────────────────── */}
      <Drawer
        isVisible={isDrawerOpen}
        title="Filter Workflows"
        onClose={() => setIsDrawerOpen(false)}
      >
        <div className="flex h-full flex-col">
          <div className="flex-1 space-y-5 p-6">
            {/* Name filter */}
            <div>
              <label htmlFor="filter-name" className="mb-1.5 block text-sm font-medium text-gray-700">
                Name
              </label>
              <input
                id="filter-name"
                type="text"
                className="block w-full rounded-lg border border-gray-300 px-3 py-2 text-sm shadow-sm transition-colors placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                placeholder="Search by name…"
                value={filterName}
                onChange={(e) => setFilterName(e.target.value)}
              />
            </div>

            {/* Status filter */}
            <div>
              <label htmlFor="filter-status" className="mb-1.5 block text-sm font-medium text-gray-700">
                Status
              </label>
              <select
                id="filter-status"
                className="block w-full rounded-lg border border-gray-300 px-3 py-2 text-sm shadow-sm transition-colors focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                value={filterStatus}
                onChange={(e) => setFilterStatus(e.target.value)}
              >
                <option value="">All</option>
                <option value="1">Active</option>
                <option value="0">Inactive</option>
              </select>
            </div>

            {/* Priority filter (multi-select as checkboxes) */}
            <fieldset>
              <legend className="mb-1.5 text-sm font-medium text-gray-700">
                Priority
              </legend>
              <div className="space-y-2">
                {PRIORITY_OPTIONS.map((opt) => (
                  <label
                    key={opt.value}
                    className="flex items-center gap-2 text-sm text-gray-700"
                  >
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                      checked={filterPriority.includes(opt.value)}
                      onChange={() => handlePriorityToggle(opt.value)}
                    />
                    {opt.label}
                  </label>
                ))}
              </div>
            </fieldset>
          </div>

          {/* Drawer action buttons */}
          <div className="flex items-center gap-3 border-t border-gray-200 px-6 py-4">
            <button
              type="button"
              className="flex-1 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-500"
              onClick={handleFilterApply}
            >
              Apply
            </button>
            <button
              type="button"
              className="flex-1 rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-500"
              onClick={handleFilterClear}
            >
              Clear All
            </button>
          </div>
        </div>
      </Drawer>

      {/* ── Delete confirmation modal ────────────────────────────── */}
      <Modal
        isVisible={isDeleteModalOpen}
        title="Delete Workflow"
        onClose={handleCancelDelete}
      >
        <div className="p-6">
          <p className="text-sm text-gray-600">
            Are you sure you want to delete this workflow?
            {selectedWorkflow?.name && (
              <span className="font-medium text-gray-900">
                {' '}
                &quot;{selectedWorkflow.name}&quot;
              </span>
            )}
          </p>
          {selectedWorkflow && selectedWorkflow.schedulesCount > 0 && (
            <div
              className="mt-3 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800"
              role="alert"
            >
              <strong>Warning:</strong> This workflow has{' '}
              {selectedWorkflow.schedulesCount} active schedule
              {selectedWorkflow.schedulesCount > 1 ? 's' : ''}. Deleting it will
              remove all associated schedules.
            </div>
          )}
        </div>
        <div className="flex items-center justify-end gap-3 border-t border-gray-200 px-6 py-4">
          <button
            type="button"
            className="rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-500"
            onClick={handleCancelDelete}
          >
            Cancel
          </button>
          <button
            type="button"
            className="rounded-lg bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-red-500 disabled:opacity-50"
            disabled={deleteMutation.isPending}
            onClick={handleConfirmDelete}
          >
            {deleteMutation.isPending ? 'Deleting…' : 'Delete'}
          </button>
        </div>
      </Modal>
    </div>
  );
}
