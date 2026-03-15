/**
 * ExecutionDetails — Individual Workflow Execution Detail Page.
 *
 * Replaces the monolith's job detail modal from list.cshtml (lines 33-41)
 * which showed the full job record serialized as indented JSON via
 * `JsonConvert.SerializeObject(record, Formatting.Indented)`, plus the
 * execution state tracking from `JobPool.cs` (status transitions:
 * context added to pool → Running → Finished / Failed / Aborted).
 *
 * @module pages/workflows/ExecutionDetails
 */

import React, { useState, useMemo, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../../api/client';
import type { BaseResponseModel } from '../../types/common';
import Modal from '../../components/common/Modal';
import TabNav, { type TabConfig } from '../../components/common/TabNav';
import { useAuthStore } from '../../stores/authStore';

/* ─────────────────────────────────────────────────────────────
   Local types — mapped from monolith's Job.cs / JobStatus enum
   ───────────────────────────────────────────────────────────── */

/**
 * Execution status values matching source JobStatus enum (Job.cs lines 6-13).
 * Maps to Step Functions execution states:
 *   Pending → pending, Running → RUNNING, Canceled → cancelled,
 *   Failed → FAILED, Finished → SUCCEEDED, Aborted → ABORTED
 */
enum ExecutionStatus {
  Pending = 1,
  Running = 2,
  Canceled = 3,
  Failed = 4,
  Finished = 5,
  Aborted = 6,
}

/**
 * Full workflow execution record returned by
 * GET /v1/workflows/executions/{executionId}.
 *
 * Mapped from monolith Job.cs (lines 26-83) properties plus
 * Step Functions execution metadata.
 */
interface WorkflowExecution {
  /** Unique execution identifier (GUID string). Maps from Job.Id. */
  id: string;
  /** Parent workflow definition identifier. Maps from Job.TypeId. */
  workflowId: string;
  /** Display name for the workflow definition. Maps from Job.TypeName. */
  workflowName: string;
  /** Fully qualified workflow class name. Maps from Job.CompleteClassName. */
  completeClassName?: string;
  /** Current execution state. Maps from Job.Status / JobStatus enum. */
  status: ExecutionStatus;
  /** Execution priority level (1–5). Maps from Job.Priority. */
  priority: number;
  /** ISO 8601 timestamp when the execution record was created. Maps from Job.CreatedOn. */
  createdOn: string;
  /** ISO 8601 timestamp when execution began running. Maps from Job.StartedOn. */
  startedOn: string | null;
  /** ISO 8601 timestamp when execution finished. Maps from Job.FinishedOn. */
  finishedOn: string | null;
  /** Error details if execution failed. Maps from Job.ErrorMessage. */
  errorMessage?: string | null;
  /** User who aborted the execution. Maps from Job.AbortedBy. */
  abortedBy?: string | null;
  /** User who canceled the execution. Maps from Job.CanceledBy. */
  canceledBy?: string | null;
  /** Dynamic input payload (JSON). Maps from Job.Attributes. */
  input?: Record<string, unknown> | null;
  /** Dynamic output payload (JSON). Maps from Job.Result. */
  output?: Record<string, unknown> | null;
  /** Associated schedule plan ID. Maps from Job.SchedulePlanId. */
  schedulePlanId?: string | null;
  /** User who created the execution. Maps from Job.CreatedBy. */
  createdBy?: string | null;
  /** Last modification timestamp. Maps from Job.LastModifiedOn. */
  lastModifiedOn?: string | null;
  /** User who last modified the execution. Maps from Job.LastModifiedBy. */
  lastModifiedBy?: string | null;
}

/**
 * Single step within a workflow execution timeline.
 *
 * Represents a Step Functions history event, mapped from the
 * monolith's flat job execution model to a structured step view.
 * Each step corresponds to a state machine state transition.
 */
interface ExecutionStep {
  /** Unique step event identifier. */
  id: string;
  /** Human-readable step/state name. */
  name: string;
  /** Step type: Task, Choice, Wait, Parallel, Pass, Succeed, Fail. */
  type: string;
  /** Step execution status. */
  status: 'Entered' | 'Succeeded' | 'Failed' | 'Aborted' | 'InProgress';
  /** ISO 8601 timestamp when the step started. */
  startedOn: string | null;
  /** ISO 8601 timestamp when the step completed. */
  finishedOn: string | null;
  /** Step input data payload. */
  input?: Record<string, unknown> | null;
  /** Step output data payload. */
  output?: Record<string, unknown> | null;
  /** Error details if the step failed. */
  error?: string | null;
}

/** Shape of the execution history API response. */
interface ExecutionHistoryResponse {
  steps: ExecutionStep[];
}

/* ─────────────────────────────────────────────────────────────
   Constants
   ───────────────────────────────────────────────────────────── */

/**
 * Status badge configuration — label + Tailwind colour classes per
 * execution status value. Consistent with ExecutionList.tsx STATUS_CONFIG.
 */
const STATUS_CONFIG: Record<
  ExecutionStatus,
  { label: string; className: string }
> = {
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

/** Timeline dot + text colour classes per step status. */
const STEP_STATUS_COLORS: Record<
  string,
  { dotClass: string; textClass: string }
> = {
  Succeeded: { dotClass: 'bg-green-500', textClass: 'text-green-700' },
  Failed: { dotClass: 'bg-red-500', textClass: 'text-red-700' },
  InProgress: {
    dotClass: 'bg-blue-500 animate-pulse',
    textClass: 'text-blue-700',
  },
  Entered: { dotClass: 'bg-blue-300', textClass: 'text-blue-600' },
  Aborted: { dotClass: 'bg-gray-400', textClass: 'text-gray-600' },
};

/** Default step status colours for unrecognised status values. */
const DEFAULT_STEP_COLORS = {
  dotClass: 'bg-gray-300',
  textClass: 'text-gray-500',
};

/** Sub-navigation links matching AdminPageUtils.GetJobAdminSubNav. */
const SUB_NAV_LINKS = [
  { label: 'Workflows', path: '/workflows' },
  { label: 'Executions', path: '/workflows/executions' },
  { label: 'Schedules', path: '/workflows/schedules' },
] as const;

/* ─────────────────────────────────────────────────────────────
   Helper functions
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
      second: '2-digit',
      hour12: false,
    }).format(date);
  } catch {
    return '\u2014';
  }
}

/**
 * Computes a human-readable duration string between two ISO-8601 timestamps.
 * If the execution is still running (no finishedOn), calculates from now.
 * Returns "\u2014" if no startedOn is available.
 */
function formatDuration(
  startedOn: string | null | undefined,
  finishedOn: string | null | undefined,
): string {
  if (!startedOn) return '\u2014';
  try {
    const start = new Date(startedOn);
    if (Number.isNaN(start.getTime())) return '\u2014';

    const end = finishedOn ? new Date(finishedOn) : new Date();
    if (Number.isNaN(end.getTime())) return '\u2014';

    const diffMs = Math.max(0, end.getTime() - start.getTime());
    const totalSeconds = Math.floor(diffMs / 1000);
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;

    const parts: string[] = [];
    if (hours > 0) parts.push(`${hours}h`);
    if (minutes > 0) parts.push(`${minutes}m`);
    parts.push(`${seconds}s`);

    return parts.join(' ');
  } catch {
    return '\u2014';
  }
}

/* ─────────────────────────────────────────────────────────────
   Sub-component: JsonViewer
   ───────────────────────────────────────────────────────────── */

/**
 * Safely renders a JSON payload as a formatted, scrollable code block.
 * Handles null/undefined data and malformed objects gracefully.
 * Replaces the monolith's `JsonConvert.SerializeObject(record, Formatting.Indented)`.
 */
function JsonViewer({
  data,
}: {
  data: Record<string, unknown> | null | undefined;
}): React.ReactElement {
  const formatted = useMemo(() => {
    if (data === null || data === undefined) return 'null';
    try {
      return JSON.stringify(data, null, 2);
    } catch {
      return 'Unable to serialise data';
    }
  }, [data]);

  return (
    <div className="max-h-80 overflow-auto rounded-md border border-gray-200 bg-gray-50">
      <pre className="p-4 text-sm leading-relaxed font-mono text-gray-800 whitespace-pre-wrap break-words">
        <code>{formatted}</code>
      </pre>
    </div>
  );
}

/* ─────────────────────────────────────────────────────────────
   Sub-component: StepTimelineItem
   ───────────────────────────────────────────────────────────── */

/**
 * Renders a single step in the execution timeline.
 * Shows step name, type badge, status, timestamps, and an expandable
 * section with input/output JSON data via TabNav.
 */
function StepTimelineItem({
  step,
  isExpanded,
  isLast,
  onToggle,
}: {
  step: ExecutionStep;
  isExpanded: boolean;
  isLast: boolean;
  onToggle: () => void;
}): React.ReactElement {
  const colors = STEP_STATUS_COLORS[step.status] ?? DEFAULT_STEP_COLORS;

  /** Per-step data tabs (input / output) for the expandable section. */
  const stepTabs = useMemo<TabConfig[]>(
    () => [
      {
        id: `step-${step.id}-input`,
        label: 'Step Input',
        content: <JsonViewer data={step.input} />,
      },
      {
        id: `step-${step.id}-output`,
        label: 'Step Output',
        content: <JsonViewer data={step.output} />,
      },
    ],
    [step.id, step.input, step.output],
  );

  return (
    <div className="relative flex gap-4">
      {/* Vertical connector line between steps */}
      {!isLast && (
        <div
          className="absolute top-6 start-[5px] bottom-0 w-0.5 bg-gray-200"
          aria-hidden="true"
        />
      )}

      {/* Timeline dot */}
      <div
        className={`relative z-10 mt-1.5 h-3 w-3 flex-shrink-0 rounded-full ${colors.dotClass}`}
        aria-hidden="true"
      />

      {/* Step content card */}
      <div className="flex-1 pb-6">
        <button
          type="button"
          onClick={onToggle}
          className="w-full text-start rounded-lg border border-gray-200 bg-white p-4 shadow-sm hover:shadow-md transition-shadow focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          aria-expanded={isExpanded}
        >
          <div className="flex flex-wrap items-center gap-2">
            {/* Step name */}
            <span className="text-sm font-semibold text-gray-900">
              {step.name || 'Unnamed Step'}
            </span>

            {/* Type badge */}
            <span className="rounded bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-600">
              {step.type}
            </span>

            {/* Status */}
            <span className={`text-xs font-medium ${colors.textClass}`}>
              {step.status}
            </span>

            {/* Expand / collapse chevron */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className={`ms-auto h-4 w-4 text-gray-400 transition-transform ${
                isExpanded ? 'rotate-180' : ''
              }`}
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M5.22 8.22a.75.75 0 0 1 1.06 0L10 11.94l3.72-3.72a.75.75 0 1 1 1.06 1.06l-4.25 4.25a.75.75 0 0 1-1.06 0L5.22 9.28a.75.75 0 0 1 0-1.06z"
                clipRule="evenodd"
              />
            </svg>
          </div>

          {/* Timestamps row */}
          <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-500">
            <span>Started: {formatDateTime(step.startedOn)}</span>
            <span>Finished: {formatDateTime(step.finishedOn)}</span>
          </div>

          {/* Error message */}
          {step.error && (
            <p className="mt-2 text-xs text-red-600">{step.error}</p>
          )}
        </button>

        {/* Expandable data section */}
        {isExpanded && (
          <div className="mt-2 rounded-lg border border-gray-100 bg-gray-50 p-3">
            <TabNav tabs={stepTabs} visibleTabs={2} />
          </div>
        )}
      </div>
    </div>
  );
}

/* ─────────────────────────────────────────────────────────────
   Helper: Sub-navigation renderer
   ───────────────────────────────────────────────────────────── */

/**
 * Renders the shared workflow sub-navigation pill bar.
 * Highlights the Executions tab as active.
 */
function SubNav(): React.ReactElement {
  return (
    <nav className="flex gap-1" aria-label="Workflow sub-navigation">
      {SUB_NAV_LINKS.map((tab) => (
        <Link
          key={tab.path}
          to={tab.path}
          className={`rounded-full px-4 py-2 text-sm font-medium transition-colors ${
            tab.path === '/workflows/executions'
              ? 'bg-blue-600 text-white'
              : 'text-gray-600 hover:bg-gray-100 hover:text-gray-800'
          }`}
        >
          {tab.label}
        </Link>
      ))}
    </nav>
  );
}

/* ═════════════════════════════════════════════════════════════
   Main Component
   ═════════════════════════════════════════════════════════════ */

/**
 * **ExecutionDetails** — Full-page view for a single workflow execution.
 *
 * Fetches execution metadata and step-by-step history, displays status
 * badges, input/output JSON tabs, a vertical step timeline, and action
 * buttons (Back, Stop, Re-run). Protected by admin role check.
 *
 * Route: `/workflows/executions/:executionId`
 *
 * Replaces:
 * - Monolith `list.cshtml` job detail modal (lines 33-41)
 * - `JobPool.cs` status tracking and AbortJob cooperative abort
 * - `JobDataService.cs` database access for job records
 */
export default function ExecutionDetails(): React.ReactElement {
  const { executionId } = useParams<{ executionId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  // Admin role check — replaces SecurityContext.HasMetaPermission()
  const isAdmin = useAuthStore((state) => state.isAdmin());

  /* ── Local UI state ──────────────────────────────────────── */
  const [isStopModalOpen, setIsStopModalOpen] = useState(false);
  const [expandedSteps, setExpandedSteps] = useState<Set<string>>(
    new Set(),
  );

  /* ── Data fetching: Execution detail ─────────────────────── */
  const {
    data: execution,
    isLoading: isExecutionLoading,
    isError: isExecutionError,
    error: executionError,
  } = useQuery<WorkflowExecution | null>({
    queryKey: ['workflow-executions', executionId],
    queryFn: async () => {
      if (!executionId) return null;
      const response = await get<WorkflowExecution>(
        `/v1/workflows/executions/${executionId}`,
      );
      if (!response.success) {
        throw new Error(
          response.message || 'Failed to fetch execution details',
        );
      }
      return response.object ?? null;
    },
    enabled: Boolean(executionId),
  });

  /* ── Data fetching: Execution step history ───────────────── */
  const { data: historyData, isLoading: isHistoryLoading } =
    useQuery<ExecutionHistoryResponse>({
      queryKey: ['workflow-executions', executionId, 'history'],
      queryFn: async () => {
        if (!executionId) return { steps: [] };
        const response = await get<ExecutionHistoryResponse>(
          `/v1/workflows/executions/${executionId}/history`,
        );
        if (!response.success) {
          throw new Error(
            response.message || 'Failed to fetch execution history',
          );
        }
        return response.object ?? { steps: [] };
      },
      enabled: Boolean(executionId) && Boolean(execution),
    });

  /* ── Stop execution mutation ─────────────────────────────── */
  const stopMutation = useMutation<void, Error, void>({
    mutationFn: async () => {
      const response = await post<BaseResponseModel>(
        `/v1/workflows/executions/${executionId}/stop`,
      );
      // Check outer API response envelope
      if (!response.success) {
        throw new Error(
          response.message || 'Failed to stop execution',
        );
      }
      // Check inner BaseResponseModel — access .success, .message, .errors
      const result = response.object;
      if (result && !result.success) {
        const errorDetail =
          result.errors?.length > 0
            ? result.errors.map((err) => err.message).join(', ')
            : result.message;
        throw new Error(errorDetail || 'Failed to stop execution');
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: ['workflow-executions', executionId],
      });
      queryClient.invalidateQueries({
        queryKey: ['workflow-executions'],
      });
      setIsStopModalOpen(false);
    },
    onError: () => {
      setIsStopModalOpen(false);
    },
  });

  /* ── Derived values (memoised) ───────────────────────────── */

  /** Human-readable duration from start to finish (or current time). */
  const duration = useMemo(
    () => formatDuration(execution?.startedOn, execution?.finishedOn),
    [execution?.startedOn, execution?.finishedOn],
  );

  /** Whether the execution can be stopped (only RUNNING or PENDING). */
  const canStop = useMemo(
    () =>
      execution != null &&
      (execution.status === ExecutionStatus.Running ||
        execution.status === ExecutionStatus.Pending),
    [execution],
  );

  /** Status badge configuration for the current execution. */
  const statusConfig = useMemo(
    () =>
      execution
        ? STATUS_CONFIG[execution.status] ?? {
            label: 'Unknown',
            className: 'bg-gray-100 text-gray-600',
          }
        : null,
    [execution],
  );

  /** Input / Output data tabs for the TabNav component. */
  const dataTabs = useMemo<TabConfig[]>(
    () => [
      {
        id: 'input-data',
        label: 'Input Data',
        content: <JsonViewer data={execution?.input} />,
      },
      {
        id: 'output-data',
        label: 'Output Data',
        content: <JsonViewer data={execution?.output} />,
      },
    ],
    [execution?.input, execution?.output],
  );

  /* ── Event handlers ──────────────────────────────────────── */

  const handleStopClick = useCallback(() => {
    setIsStopModalOpen(true);
  }, []);

  const handleConfirmStop = useCallback(() => {
    stopMutation.mutate();
  }, [stopMutation]);

  const handleCancelStop = useCallback(() => {
    setIsStopModalOpen(false);
  }, []);

  const handleBackToList = useCallback(() => {
    navigate('/workflows/executions');
  }, [navigate]);

  const handleRerun = useCallback(() => {
    if (execution?.workflowId) {
      navigate(`/workflows/${execution.workflowId}?action=execute`);
    }
  }, [navigate, execution?.workflowId]);

  const handleToggleStep = useCallback((stepId: string) => {
    setExpandedSteps((prev) => {
      const next = new Set(prev);
      if (next.has(stepId)) {
        next.delete(stepId);
      } else {
        next.add(stepId);
      }
      return next;
    });
  }, []);

  /* ═══════════════════════════════════════════════════════════
     Render guards — early exits for special states
     ═══════════════════════════════════════════════════════════ */

  /* ── Guard: Admin-only protection ────────────────────────── */
  if (!isAdmin) {
    return (
      <div className="flex flex-col items-center justify-center py-16 text-center">
        <svg
          xmlns="http://www.w3.org/2000/svg"
          viewBox="0 0 24 24"
          fill="currentColor"
          className="mb-4 h-12 w-12 text-gray-400"
          aria-hidden="true"
        >
          <path
            fillRule="evenodd"
            d="M12 1.5a5.25 5.25 0 0 0-5.25 5.25v3a3 3 0 0 0-3 3v6.75a3 3 0 0 0 3 3h10.5a3 3 0 0 0 3-3v-6.75a3 3 0 0 0-3-3v-3c0-2.9-2.35-5.25-5.25-5.25zm3.75 8.25v-3a3.75 3.75 0 1 0-7.5 0v3h7.5z"
            clipRule="evenodd"
          />
        </svg>
        <h2 className="text-lg font-semibold text-gray-900">Access Denied</h2>
        <p className="mt-1 text-sm text-gray-500">
          You do not have permission to view execution details.
        </p>
        <Link
          to="/workflows/executions"
          className="mt-4 inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          Back to Executions
        </Link>
      </div>
    );
  }

  /* ── Guard: Loading state ────────────────────────────────── */
  if (isExecutionLoading) {
    return (
      <div className="space-y-6">
        <SubNav />
        <div className="animate-pulse space-y-4" aria-busy="true" aria-label="Loading execution details">
          <div className="h-8 w-64 rounded bg-gray-200" />
          <div className="h-40 rounded-lg bg-gray-200" />
          <div className="h-32 rounded-lg bg-gray-200" />
          <div className="h-60 rounded-lg bg-gray-200" />
        </div>
      </div>
    );
  }

  /* ── Guard: Error state ──────────────────────────────────── */
  if (isExecutionError) {
    const errorMsg =
      executionError instanceof Error
        ? executionError.message
        : 'An unexpected error occurred while loading execution details.';

    return (
      <div className="space-y-6">
        <SubNav />
        <div
          className="rounded-lg border border-red-200 bg-red-50 p-6 text-center"
          role="alert"
        >
          <h2 className="text-lg font-semibold text-red-800">
            Error Loading Execution
          </h2>
          <p className="mt-2 text-sm text-red-600">{errorMsg}</p>
          <Link
            to="/workflows/executions"
            className="mt-4 inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            Back to Executions
          </Link>
        </div>
      </div>
    );
  }

  /* ── Guard: 404 — execution not found ────────────────────── */
  if (!execution) {
    return (
      <div className="space-y-6">
        <SubNav />
        <div className="rounded-lg border border-gray-200 bg-white p-6 text-center">
          <h2 className="text-lg font-semibold text-gray-900">
            Execution Not Found
          </h2>
          <p className="mt-2 text-sm text-gray-500">
            The execution with ID &quot;{executionId}&quot; could not be found.
          </p>
          <Link
            to="/workflows/executions"
            className="mt-4 inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            Back to Executions
          </Link>
        </div>
      </div>
    );
  }

  /* ═══════════════════════════════════════════════════════════
     Main render
     ═══════════════════════════════════════════════════════════ */

  const steps = historyData?.steps ?? [];

  return (
    <div className="space-y-6">
      {/* ── Sub-navigation ───────────────────────────────────── */}
      <SubNav />

      {/* ── Breadcrumb ────────────────────────────────────────── */}
      <nav aria-label="Breadcrumb">
        <ol className="flex items-center gap-1.5 text-sm text-gray-500">
          <li>
            <Link
              to="/workflows/executions"
              className="hover:text-blue-600 hover:underline"
            >
              Executions
            </Link>
          </li>
          <li aria-hidden="true" className="select-none">
            /
          </li>
          <li className="font-medium text-gray-900" aria-current="page">
            {execution.id.length > 8
              ? `${execution.id.slice(0, 8)}\u2026`
              : execution.id}
          </li>
        </ol>
      </nav>

      {/* ── Header: Title + Status Badge + Actions ────────────── */}
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          <h1 className="text-xl font-bold text-gray-900">
            Execution Details
          </h1>
          {statusConfig && (
            <span
              className={`inline-flex items-center rounded-full px-3 py-1 text-sm font-medium ${statusConfig.className}`}
            >
              {statusConfig.label}
            </span>
          )}
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {/* Back to Executions */}
          <button
            type="button"
            onClick={handleBackToList}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="me-1.5 h-4 w-4"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M17 10a.75.75 0 0 1-.75.75H5.612l4.158 3.96a.75.75 0 1 1-1.04 1.08l-5.5-5.25a.75.75 0 0 1 0-1.08l5.5-5.25a.75.75 0 1 1 1.04 1.08L5.612 9.25H16.25A.75.75 0 0 1 17 10z"
                clipRule="evenodd"
              />
            </svg>
            Back to Executions
          </button>

          {/* Stop Execution (shown only when execution is stoppable) */}
          {canStop && (
            <button
              type="button"
              onClick={handleStopClick}
              disabled={stopMutation.isPending}
              className="inline-flex items-center rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
            >
              {stopMutation.isPending ? 'Stopping\u2026' : 'Stop Execution'}
            </button>
          )}

          {/* Re-run Workflow */}
          <button
            type="button"
            onClick={handleRerun}
            className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            Re-run Workflow
          </button>
        </div>
      </div>

      {/* ── Summary Card ──────────────────────────────────────── */}
      <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 px-6 py-4">
          <h2 className="text-base font-semibold text-gray-900">
            Execution Summary
          </h2>
        </div>

        <dl className="grid grid-cols-1 gap-x-6 gap-y-4 px-6 py-5 sm:grid-cols-2 lg:grid-cols-3">
          {/* Execution ID */}
          <div>
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Execution ID
            </dt>
            <dd className="mt-1 break-all font-mono text-sm text-gray-900">
              {execution.id}
            </dd>
          </div>

          {/* Workflow Name */}
          <div>
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Workflow
            </dt>
            <dd className="mt-1 text-sm text-gray-900">
              {execution.workflowName || '\u2014'}
            </dd>
          </div>

          {/* Status */}
          <div>
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Status
            </dt>
            <dd className="mt-1">
              {statusConfig && (
                <span
                  className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${statusConfig.className}`}
                >
                  {statusConfig.label}
                </span>
              )}
            </dd>
          </div>

          {/* Priority */}
          <div>
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Priority
            </dt>
            <dd className="mt-1 text-sm text-gray-900">
              {execution.priority ?? '\u2014'}
            </dd>
          </div>

          {/* Started At */}
          <div>
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Started At
            </dt>
            <dd className="mt-1 text-sm text-gray-900">
              {formatDateTime(execution.startedOn)}
            </dd>
          </div>

          {/* Finished At */}
          <div>
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Finished At
            </dt>
            <dd className="mt-1 text-sm text-gray-900">
              {execution.finishedOn
                ? formatDateTime(execution.finishedOn)
                : execution.status === ExecutionStatus.Running
                  ? 'In Progress'
                  : '\u2014'}
            </dd>
          </div>

          {/* Duration */}
          <div>
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Duration
            </dt>
            <dd className="mt-1 text-sm text-gray-900">{duration}</dd>
          </div>

          {/* Created At */}
          <div>
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Created At
            </dt>
            <dd className="mt-1 text-sm text-gray-900">
              {formatDateTime(execution.createdOn)}
            </dd>
          </div>

          {/* Created By (conditional) */}
          {execution.createdBy && (
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Created By
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {execution.createdBy}
              </dd>
            </div>
          )}

          {/* Schedule Plan (conditional) */}
          {execution.schedulePlanId && (
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Schedule Plan
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                <Link
                  to={`/workflows/schedules/${execution.schedulePlanId}`}
                  className="text-blue-600 hover:text-blue-800 hover:underline"
                >
                  {execution.schedulePlanId.length > 8
                    ? `${execution.schedulePlanId.slice(0, 8)}\u2026`
                    : execution.schedulePlanId}
                </Link>
              </dd>
            </div>
          )}

          {/* Aborted By (conditional) */}
          {execution.abortedBy && (
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Aborted By
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {execution.abortedBy}
              </dd>
            </div>
          )}

          {/* Canceled By (conditional) */}
          {execution.canceledBy && (
            <div>
              <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
                Canceled By
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {execution.canceledBy}
              </dd>
            </div>
          )}
        </dl>

        {/* Error message alert box */}
        {execution.errorMessage && (
          <div
            className="mx-6 mb-5 rounded-md border border-red-200 bg-red-50 p-4"
            role="alert"
          >
            <div className="flex items-start gap-3">
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-5 w-5 flex-shrink-0 text-red-500"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M18 10a8 8 0 1 1-16 0 8 8 0 0 1 16 0zm-8-5a.75.75 0 0 1 .75.75v4.5a.75.75 0 0 1-1.5 0v-4.5A.75.75 0 0 1 10 5zm0 10a1 1 0 1 0 0-2 1 1 0 0 0 0 2z"
                  clipRule="evenodd"
                />
              </svg>
              <div>
                <h3 className="text-sm font-semibold text-red-800">
                  Execution Error
                </h3>
                <p className="mt-1 whitespace-pre-wrap text-sm text-red-700">
                  {execution.errorMessage}
                </p>
              </div>
            </div>
          </div>
        )}
      </section>

      {/* ── Execution Data Tabs (Input / Output) ──────────────── */}
      <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 px-6 py-4">
          <h2 className="text-base font-semibold text-gray-900">
            Execution Data
          </h2>
        </div>
        <div className="px-6 py-5">
          <TabNav tabs={dataTabs} visibleTabs={2} />
        </div>
      </section>

      {/* ── Step Timeline ─────────────────────────────────────── */}
      <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 px-6 py-4">
          <h2 className="text-base font-semibold text-gray-900">
            Execution Steps
          </h2>
        </div>
        <div className="px-6 py-5">
          {isHistoryLoading ? (
            <div
              className="animate-pulse space-y-4"
              aria-busy="true"
              aria-label="Loading execution steps"
            >
              {[1, 2, 3].map((i) => (
                <div key={i} className="flex gap-4">
                  <div className="h-3 w-3 rounded-full bg-gray-200" />
                  <div className="h-16 flex-1 rounded-lg bg-gray-200" />
                </div>
              ))}
            </div>
          ) : steps.length === 0 ? (
            <p className="py-6 text-center text-sm text-gray-500">
              No step history available for this execution.
            </p>
          ) : (
            <div className="relative">
              {steps.map((step, index) => (
                <StepTimelineItem
                  key={step.id}
                  step={step}
                  isExpanded={expandedSteps.has(step.id)}
                  isLast={index === steps.length - 1}
                  onToggle={() => handleToggleStep(step.id)}
                />
              ))}
            </div>
          )}
        </div>
      </section>

      {/* ── Stop Execution Confirmation Modal ─────────────────── */}
      <Modal
        isVisible={isStopModalOpen}
        title="Stop Execution"
        onClose={handleCancelStop}
        footer={
          <div className="flex justify-end gap-2">
            <button
              type="button"
              onClick={handleCancelStop}
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmStop}
              disabled={stopMutation.isPending}
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
            >
              {stopMutation.isPending ? 'Stopping\u2026' : 'Confirm Stop'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to stop this execution? This action sends a
          cooperative abort signal &mdash; the workflow will finish its current
          step before stopping.
        </p>
        {stopMutation.isError && (
          <p className="mt-2 text-sm text-red-600" role="alert">
            {stopMutation.error?.message ||
              'An error occurred while stopping the execution.'}
          </p>
        )}
      </Modal>
    </div>
  );
}
