/**
 * WorkflowDetails.tsx — Workflow Definition Detail View Page
 *
 * React page component displaying a single workflow definition's details,
 * execution history, and current step status. Replaces the monolith's
 * JobManager.cs (job type registry: Id, Name, Priority, AllowSingleInstance,
 * CompleteClassName) and JobPool.cs (pool status, running/free threads).
 *
 * In the target architecture, this shows a Step Functions state machine
 * definition with its execution history.
 *
 * Route: /workflows/:workflowId
 * Default export for React.lazy() compatibility.
 */

import React, { useState, useCallback, useMemo } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';

import { get, post, patch, del } from '../../api/client';
import type { ApiResponse } from '../../api/client';
import { useWorkflow } from '../../hooks/useWorkflows';
import type { Workflow } from '../../hooks/useWorkflows';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';
import Modal from '../../components/common/Modal';
import ScreenMessage, { useToast } from '../../components/common/ScreenMessage';
import { ScreenMessageType } from '../../types/common';

// ---------------------------------------------------------------------------
// Local Interfaces
// ---------------------------------------------------------------------------

/**
 * Execution record for the recent-executions table.
 * Index signature satisfies the DataTable<T extends Record<string, unknown>>
 * generic constraint.
 */
interface WorkflowExecution {
  [key: string]: unknown;
  id: string;
  workflowId: string;
  status: string;
  startedOn: string | null;
  finishedOn: string | null;
  trigger: string;
  errorMessage: string | null;
}

/** API response envelope for the executions list endpoint. */
interface WorkflowExecutionResponse {
  items: WorkflowExecution[];
  totalCount: number;
}

/**
 * Step definition within a Step Functions state machine.
 * Extracted from the workflow's `attributes.steps` JSON payload.
 */
interface WorkflowStepDefinition {
  name: string;
  type: 'Task' | 'Choice' | 'Wait' | 'Parallel' | 'Pass' | 'Succeed' | 'Fail';
  next?: string;
  resource?: string;
  retryMaxAttempts?: number;
  retryIntervalSeconds?: number;
  retryBackoffRate?: number;
  catchFallback?: string;
  conditions?: Array<{
    variable: string;
    operator: string;
    value: string;
    next: string;
  }>;
  comment?: string;
}

// ---------------------------------------------------------------------------
// Priority Badge Configuration
// Maps from the monolith's JobPriority enum: Low=1, Medium=2, High=3,
// Higher=4, Highest=5 (see JobType.cs DefaultPriority).
// ---------------------------------------------------------------------------

const PRIORITY_CONFIG: Record<number, { label: string; classes: string }> = {
  1: { label: 'Low', classes: 'bg-gray-100 text-gray-700' },
  2: { label: 'Medium', classes: 'bg-blue-100 text-blue-700' },
  3: { label: 'High', classes: 'bg-yellow-100 text-yellow-800' },
  4: { label: 'Higher', classes: 'bg-orange-100 text-orange-800' },
  5: { label: 'Highest', classes: 'bg-red-100 text-red-800' },
};

// ---------------------------------------------------------------------------
// Execution Status Badge Configuration
// Maps from the monolith's JobStatus enum and Step Functions execution states.
// ---------------------------------------------------------------------------

const EXECUTION_STATUS_CONFIG: Record<string, { label: string; classes: string }> = {
  RUNNING: { label: 'Running', classes: 'bg-blue-100 text-blue-800' },
  SUCCEEDED: { label: 'Succeeded', classes: 'bg-green-100 text-green-800' },
  FAILED: { label: 'Failed', classes: 'bg-red-100 text-red-800' },
  ABORTED: { label: 'Aborted', classes: 'bg-gray-100 text-gray-800' },
  TIMED_OUT: { label: 'Timed Out', classes: 'bg-orange-100 text-orange-800' },
  PENDING: { label: 'Pending', classes: 'bg-yellow-100 text-yellow-800' },
};

/**
 * Workflow-level status labels mapping from the WorkflowStatus numeric enum.
 * Source: Job.cs — Pending=1, Running=2, Completed=3, Aborted=4, Failed=5.
 */
const WORKFLOW_STATUS_CONFIG: Record<number, { label: string; classes: string }> = {
  1: { label: 'Pending', classes: 'bg-yellow-100 text-yellow-800' },
  2: { label: 'Running', classes: 'bg-blue-100 text-blue-800' },
  3: { label: 'Completed', classes: 'bg-green-100 text-green-800' },
  4: { label: 'Aborted', classes: 'bg-gray-100 text-gray-800' },
  5: { label: 'Failed', classes: 'bg-red-100 text-red-800' },
};

/**
 * Step type visual configuration for the state machine visualization.
 * Each step type gets a distinct color scheme for its card border and background.
 */
const STEP_TYPE_COLORS: Record<string, string> = {
  Task: 'border-blue-400 bg-blue-50',
  Choice: 'border-purple-400 bg-purple-50',
  Wait: 'border-yellow-400 bg-yellow-50',
  Parallel: 'border-teal-400 bg-teal-50',
  Pass: 'border-gray-400 bg-gray-50',
  Succeed: 'border-green-400 bg-green-50',
  Fail: 'border-red-400 bg-red-50',
};

/** Unicode icons per step type for the state machine visualization nodes. */
const STEP_TYPE_ICONS: Record<string, string> = {
  Task: '⚙️',
  Choice: '🔀',
  Wait: '⏳',
  Parallel: '⇶',
  Pass: '➡️',
  Succeed: '✅',
  Fail: '❌',
};

// ---------------------------------------------------------------------------
// Helper Functions
// ---------------------------------------------------------------------------

/**
 * Formats an ISO 8601 date string to a human-readable format.
 * Returns a dash when the input is null or undefined.
 */
function formatDateTime(dateStr: string | null | undefined): string {
  if (!dateStr) return '—';
  try {
    return new Date(dateStr).toLocaleDateString('en-US', {
      year: 'numeric',
      month: 'short',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    });
  } catch {
    return '—';
  }
}

/**
 * Calculates a human-readable duration between two timestamps.
 * If `finishedOn` is null the duration is computed relative to Date.now().
 */
function calculateDuration(
  startedOn: string | null,
  finishedOn: string | null,
): string {
  if (!startedOn) return '—';
  const start = new Date(startedOn).getTime();
  const end = finishedOn ? new Date(finishedOn).getTime() : Date.now();
  const diffMs = Math.max(0, end - start);
  const totalSeconds = Math.floor(diffMs / 1000);
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return `${minutes}m ${seconds}s`;
  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return `${hours}h ${remainingMinutes}m`;
}

/**
 * Safely extracts step definitions from the workflow's attributes payload.
 * Returns an empty array when no step data is available.
 */
function extractSteps(
  workflow: Workflow | undefined,
): WorkflowStepDefinition[] {
  if (!workflow?.attributes) return [];
  const raw = workflow.attributes['steps'] ?? workflow.attributes['definition'];
  if (Array.isArray(raw)) {
    return raw as WorkflowStepDefinition[];
  }
  return [];
}

// ---------------------------------------------------------------------------
// StateMachineVisualization — Sub-component
// ---------------------------------------------------------------------------

/**
 * Renders a vertical flow diagram of the workflow's Step Functions state
 * machine definition. Each step is a styled card with type indicators,
 * transition arrows, retry policies, and catch configurations.
 *
 * New capability replacing the monolith's flat job type display.
 */
function StateMachineVisualization({
  steps,
  currentStep,
}: {
  steps: WorkflowStepDefinition[];
  currentStep?: string;
}) {
  if (steps.length === 0) {
    return (
      <div className="rounded-lg border-2 border-dashed border-gray-300 bg-gray-50 p-8 text-center">
        <p className="text-sm text-gray-500">
          No step definitions available for this workflow.
        </p>
        <p className="mt-1 text-xs text-gray-400">
          Step data will appear once the workflow definition includes a state
          machine configuration.
        </p>
      </div>
    );
  }

  return (
    <div
      className="flex flex-col items-center gap-2 py-4"
      role="img"
      aria-label="Workflow state machine diagram"
    >
      {/* Start node */}
      <div className="flex h-8 w-8 items-center justify-center rounded-full bg-green-500 shadow">
        <span className="text-xs font-bold text-white" aria-hidden="true">
          ▶
        </span>
      </div>
      <div className="h-6 w-0.5 bg-gray-300" aria-hidden="true" />

      {steps.map((step, index) => {
        const isActive = step.name === currentStep;
        const isLast = index === steps.length - 1;
        const typeColor =
          STEP_TYPE_COLORS[step.type] ?? 'border-gray-400 bg-gray-50';

        return (
          <div key={step.name} className="flex flex-col items-center">
            <div
              className={[
                'relative w-64 rounded-lg border-2 p-3 transition-shadow',
                typeColor,
                isActive
                  ? 'ring-2 ring-blue-500 ring-offset-2 shadow-lg'
                  : 'shadow-sm',
              ].join(' ')}
            >
              {/* Header: icon + name + type badge */}
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <span
                    className="text-base"
                    role="img"
                    aria-label={step.type}
                  >
                    {STEP_TYPE_ICONS[step.type] ?? '⚙️'}
                  </span>
                  <span className="text-sm font-semibold text-gray-900">
                    {step.name}
                  </span>
                </div>
                <span className="rounded bg-white/80 px-1.5 py-0.5 text-xs font-medium text-gray-600">
                  {step.type}
                </span>
              </div>

              {/* Resource ARN */}
              {step.resource && (
                <p
                  className="mt-1 truncate text-xs text-gray-500"
                  title={step.resource}
                >
                  Resource: {step.resource}
                </p>
              )}

              {/* Comment */}
              {step.comment && (
                <p className="mt-1 text-xs italic text-gray-400">
                  {step.comment}
                </p>
              )}

              {/* Retry / Catch policies */}
              <div className="mt-1 flex flex-wrap gap-1">
                {step.retryMaxAttempts != null &&
                  step.retryMaxAttempts > 0 && (
                    <span className="inline-block rounded bg-yellow-100 px-1.5 py-0.5 text-xs text-yellow-700">
                      Retry: {step.retryMaxAttempts}×
                      {step.retryIntervalSeconds != null &&
                        ` @ ${step.retryIntervalSeconds}s`}
                    </span>
                  )}
                {step.catchFallback && (
                  <span className="inline-block rounded bg-red-100 px-1.5 py-0.5 text-xs text-red-700">
                    Catch → {step.catchFallback}
                  </span>
                )}
              </div>

              {/* Choice conditions */}
              {step.conditions && step.conditions.length > 0 && (
                <div className="mt-2 space-y-1 border-t border-gray-200 pt-1">
                  {step.conditions.map((cond, ci) => (
                    <div
                      key={ci}
                      className="flex items-center gap-1 text-xs text-purple-700"
                    >
                      <span className="font-medium">if</span>
                      <code className="rounded bg-white/80 px-1">
                        {cond.variable} {cond.operator} {cond.value}
                      </code>
                      <span>→</span>
                      <span className="font-semibold">{cond.next}</span>
                    </div>
                  ))}
                </div>
              )}

              {/* Active step indicator */}
              {isActive && (
                <div className="absolute -end-1 -top-1 flex h-4 w-4 items-center justify-center rounded-full bg-blue-500">
                  <span className="animate-pulse text-[8px] text-white">
                    ●
                  </span>
                </div>
              )}
            </div>

            {/* Transition arrow */}
            {!isLast && (
              <>
                <div className="h-4 w-0.5 bg-gray-300" aria-hidden="true" />
                {step.next && (
                  <span className="text-[10px] text-gray-400">
                    → {step.next}
                  </span>
                )}
                <div className="h-4 w-0.5 bg-gray-300" aria-hidden="true" />
              </>
            )}
          </div>
        );
      })}

      {/* End node */}
      <div className="h-6 w-0.5 bg-gray-300" aria-hidden="true" />
      <div className="flex h-8 w-8 items-center justify-center rounded-full bg-gray-600 shadow">
        <span className="text-xs font-bold text-white" aria-hidden="true">
          ■
        </span>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// WorkflowDetails — Main Component
// ---------------------------------------------------------------------------

/**
 * Workflow Definition Detail View.
 *
 * Renders a workflow's metadata (overview), recent execution history,
 * and associated schedules in a tabbed layout. Provides actions to edit,
 * run, enable/disable, and delete the workflow.
 *
 * Route: `/workflows/:workflowId`
 */
function WorkflowDetails(): React.JSX.Element {
  // ---- Routing & Navigation ------------------------------------------------
  const { workflowId } = useParams<{ workflowId: string }>();
  const navigate = useNavigate();

  // ---- Server State --------------------------------------------------------
  const queryClient = useQueryClient();
  const {
    data: workflowResponse,
    isLoading: isWorkflowLoading,
    isError: isWorkflowError,
    error: workflowError,
  } = useWorkflow(workflowId ?? '');

  /** Unwrap the ApiResponse envelope to get the actual Workflow object. */
  const workflow: Workflow | undefined = workflowResponse?.object;

  // Recent executions — separate query key for independent cache lifecycle.
  const {
    data: executionsResponse,
    isLoading: isExecutionsLoading,
    isError: isExecutionsError,
  } = useQuery<ApiResponse<WorkflowExecutionResponse>>({
    queryKey: ['workflow-executions', { workflowId, limit: 10 }],
    queryFn: () =>
      get<WorkflowExecutionResponse>(
        `/v1/workflows/${workflowId}/executions?limit=10`,
      ),
    enabled: Boolean(workflowId),
  });

  /** Unwrap the ApiResponse envelope for execution data. */
  const executionsData: WorkflowExecutionResponse | undefined =
    executionsResponse?.object;

  // ---- Local UI State ------------------------------------------------------
  const [isDeleteModalVisible, setIsDeleteModalVisible] = useState(false);
  const [activeTab, setActiveTab] = useState<string>('overview');
  const { messages, showToast, dismissToast } = useToast();

  // ---- Mutations -----------------------------------------------------------

  /** Run Now — POST /v1/workflows/{workflowId}/execute */
  const runNowMutation = useMutation({
    mutationFn: () =>
      post<{ executionId: string }>(
        `/v1/workflows/${workflowId}/execute`,
        {},
      ),
    onSuccess: () => {
      showToast(
        ScreenMessageType.Success,
        'Workflow Started',
        'A new execution has been triggered.',
      );
      queryClient.invalidateQueries({
        queryKey: ['workflow-executions'],
      });
    },
    onError: (err: Error) => {
      showToast(
        ScreenMessageType.Error,
        'Run Failed',
        err.message || 'Could not start the workflow execution.',
      );
    },
  });

  /** Enable / Disable — PATCH /v1/workflows/{workflowId}/status */
  const toggleStatusMutation = useMutation({
    mutationFn: (enabled: boolean) =>
      patch<Workflow>(`/v1/workflows/${workflowId}/status`, { enabled }),
    onSuccess: (_data, enabled) => {
      showToast(
        ScreenMessageType.Info,
        'Status Updated',
        `Workflow has been ${enabled ? 'enabled' : 'disabled'}.`,
      );
      queryClient.invalidateQueries({
        queryKey: ['workflows', workflowId],
      });
    },
    onError: (err: Error) => {
      showToast(
        ScreenMessageType.Error,
        'Toggle Failed',
        err.message || 'Could not update the workflow status.',
      );
    },
  });

  /** Delete Workflow — DELETE /v1/workflows/{workflowId} */
  const deleteMutation = useMutation({
    mutationFn: () => del<void>(`/v1/workflows/${workflowId}`),
    onSuccess: () => {
      showToast(
        ScreenMessageType.Success,
        'Workflow Deleted',
        'The workflow has been removed.',
      );
      navigate('/workflows');
    },
    onError: (err: Error) => {
      showToast(
        ScreenMessageType.Error,
        'Delete Failed',
        err.message || 'Could not delete the workflow.',
      );
    },
  });

  // ---- Event Handlers (memoized) -------------------------------------------

  const handleRunNow = useCallback(() => {
    runNowMutation.mutate();
  }, [runNowMutation]);

  const handleToggleStatus = useCallback(() => {
    if (!workflow) return;
    // Status 3 (Completed) or status "enabled" is Active → disable; otherwise enable
    const isCurrentlyEnabled = workflow.status === 2 || workflow.status === 1;
    toggleStatusMutation.mutate(!isCurrentlyEnabled);
  }, [workflow, toggleStatusMutation]);

  const handleDeleteClick = useCallback(() => {
    setIsDeleteModalVisible(true);
  }, []);

  const handleDeleteConfirm = useCallback(() => {
    setIsDeleteModalVisible(false);
    deleteMutation.mutate();
  }, [deleteMutation]);

  const handleDeleteCancel = useCallback(() => {
    setIsDeleteModalVisible(false);
  }, []);

  const handleTabChange = useCallback((tabId: string) => {
    setActiveTab(tabId);
  }, []);

  // ---- Derived Data (memoized) ---------------------------------------------

  const steps = useMemo(() => extractSteps(workflow), [workflow]);

  /**
   * Determine the "current step" for a running workflow.
   * Uses the latest running execution's progress if available.
   */
  const currentStep = useMemo<string | undefined>(() => {
    if (!executionsData?.items) return undefined;
    const running = executionsData.items.find(
      (ex: WorkflowExecution) => ex.status.toUpperCase() === 'RUNNING',
    );
    return running ? (running as unknown as Record<string, string>)['currentStep'] : undefined;
  }, [executionsData]);

  /** Whether the workflow's status indicates it is currently active/enabled. */
  const isWorkflowEnabled = useMemo(() => {
    if (!workflow) return false;
    // Pending (1) or Running (2) → enabled; Completed (3), Aborted (4), Failed (5) → disabled
    return workflow.status === 1 || workflow.status === 2;
  }, [workflow]);

  const priorityCfg = useMemo(
    () => PRIORITY_CONFIG[workflow?.priority ?? 2] ?? PRIORITY_CONFIG[2],
    [workflow],
  );

  const workflowStatusCfg = useMemo(
    () =>
      WORKFLOW_STATUS_CONFIG[workflow?.status ?? 1] ??
      WORKFLOW_STATUS_CONFIG[1],
    [workflow],
  );

  // ---- Execution Table Columns ---------------------------------------------

  const executionColumns = useMemo<DataTableColumn<WorkflowExecution>[]>(
    () => [
      {
        id: 'id',
        label: 'Execution ID',
        sortable: true,
        accessorKey: 'id',
        cell: (_value: unknown, row: WorkflowExecution) => (
          <Link
            to={`/workflows/executions/${row.id}`}
            className="font-mono text-sm text-blue-600 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          >
            {row.id.slice(0, 8)}…
          </Link>
        ),
      },
      {
        id: 'status',
        label: 'Status',
        sortable: true,
        accessorKey: 'status',
        cell: (_value: unknown, row: WorkflowExecution) => {
          const cfg =
            EXECUTION_STATUS_CONFIG[row.status.toUpperCase()] ??
            EXECUTION_STATUS_CONFIG['PENDING'];
          return (
            <span
              className={`inline-block rounded-full px-2.5 py-0.5 text-xs font-medium ${cfg.classes}`}
            >
              {cfg.label}
            </span>
          );
        },
      },
      {
        id: 'startedOn',
        label: 'Started On',
        sortable: true,
        accessorKey: 'startedOn',
        cell: (_value: unknown, row: WorkflowExecution) => (
          <span className="text-sm text-gray-600">
            {formatDateTime(row.startedOn)}
          </span>
        ),
      },
      {
        id: 'duration',
        label: 'Duration',
        sortable: false,
        accessorFn: (row: WorkflowExecution) =>
          calculateDuration(row.startedOn, row.finishedOn),
        cell: (_value: unknown, row: WorkflowExecution) => (
          <span className="text-sm text-gray-600">
            {calculateDuration(row.startedOn, row.finishedOn)}
          </span>
        ),
      },
      {
        id: 'trigger',
        label: 'Trigger',
        sortable: true,
        accessorKey: 'trigger',
        cell: (_value: unknown, row: WorkflowExecution) => (
          <span className="text-sm capitalize text-gray-700">
            {row.trigger || 'manual'}
          </span>
        ),
      },
    ],
    [],
  );

  // ---- Tab Configurations --------------------------------------------------

  const tabs = useMemo<TabConfig[]>(
    () => [
      {
        id: 'overview',
        label: 'Overview',
        content: (
          <div className="space-y-6">
            {/* Workflow definition info cards */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {/* ID */}
              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Workflow ID
                </dt>
                <dd
                  className="mt-1 truncate font-mono text-sm text-gray-900"
                  title={workflow?.id}
                >
                  {workflow?.id ?? '—'}
                </dd>
              </div>

              {/* Status */}
              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Status
                </dt>
                <dd className="mt-1">
                  <span
                    className={`inline-block rounded-full px-2.5 py-0.5 text-xs font-medium ${workflowStatusCfg.classes}`}
                  >
                    {workflowStatusCfg.label}
                  </span>
                </dd>
              </div>

              {/* Priority */}
              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Default Priority
                </dt>
                <dd className="mt-1">
                  <span
                    className={`inline-block rounded-full px-2.5 py-0.5 text-xs font-medium ${priorityCfg.classes}`}
                  >
                    {priorityCfg.label}
                  </span>
                </dd>
              </div>

              {/* Type Name */}
              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Type
                </dt>
                <dd className="mt-1 text-sm text-gray-900">
                  {workflow?.typeName ?? '—'}
                </dd>
              </div>

              {/* Class Name */}
              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Class Name
                </dt>
                <dd
                  className="mt-1 truncate font-mono text-sm text-gray-700"
                  title={workflow?.completeClassName}
                >
                  {workflow?.completeClassName ?? '—'}
                </dd>
              </div>

              {/* Allow Single Instance */}
              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Single Instance Only
                </dt>
                <dd className="mt-1 text-sm text-gray-900">
                  {workflow?.attributes?.['allowSingleInstance'] === true
                    ? 'Yes'
                    : 'No'}
                </dd>
              </div>

              {/* Created On */}
              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Created On
                </dt>
                <dd className="mt-1 text-sm text-gray-900">
                  {formatDateTime(workflow?.createdOn)}
                </dd>
              </div>

              {/* Last Modified On */}
              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Last Modified
                </dt>
                <dd className="mt-1 text-sm text-gray-900">
                  {formatDateTime(workflow?.lastModifiedOn)}
                </dd>
              </div>
            </div>

            {/* Description */}
            {Boolean(workflow?.attributes?.['description']) && (
              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Description
                </dt>
                <dd className="mt-1 text-sm text-gray-700">
                  {String(workflow?.attributes['description'] ?? '')}
                </dd>
              </div>
            )}

            {/* State Machine Visualization */}
            <section aria-labelledby="sm-heading">
              <h3
                id="sm-heading"
                className="mb-3 text-base font-semibold text-gray-900"
              >
                State Machine Definition
              </h3>
              <div className="overflow-x-auto rounded-lg border border-gray-200 bg-white p-4">
                <StateMachineVisualization
                  steps={steps}
                  currentStep={currentStep}
                />
              </div>
            </section>
          </div>
        ),
      },
      {
        id: 'executions',
        label: 'Executions',
        content: (
          <div className="space-y-4">
            {isExecutionsLoading && (
              <div className="flex items-center justify-center py-12">
                <div
                  className="h-8 w-8 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600"
                  role="status"
                  aria-label="Loading executions"
                />
              </div>
            )}

            {isExecutionsError && (
              <div className="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700">
                Failed to load recent executions.
              </div>
            )}

            {!isExecutionsLoading && !isExecutionsError && (
              <>
                <DataTable<WorkflowExecution>
                  columns={executionColumns}
                  data={executionsData?.items ?? []}
                  emptyText="No executions recorded for this workflow."
                />
                <div className="flex justify-end">
                  <Link
                    to={`/workflows/executions?workflowId=${workflowId}`}
                    className="text-sm font-medium text-blue-600 hover:text-blue-700 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
                  >
                    View All Executions →
                  </Link>
                </div>
              </>
            )}
          </div>
        ),
      },
      {
        id: 'schedules',
        label: 'Schedules',
        content: (
          <div className="space-y-4">
            <p className="text-sm text-gray-600">
              Schedules associated with this workflow definition.
            </p>
            <Link
              to={`/workflows/schedules?workflowId=${workflowId}`}
              className="inline-flex items-center gap-1.5 rounded-md bg-gray-100 px-3 py-2 text-sm font-medium text-gray-700 transition-colors hover:bg-gray-200 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500"
            >
              Manage Schedules →
            </Link>
          </div>
        ),
      },
    ],
    [
      workflow,
      workflowStatusCfg,
      priorityCfg,
      steps,
      currentStep,
      isExecutionsLoading,
      isExecutionsError,
      executionsData,
      executionColumns,
      workflowId,
    ],
  );

  // ==========================================================================
  // Render — Loading State
  // ==========================================================================

  if (isWorkflowLoading) {
    return (
      <main className="mx-auto max-w-7xl px-4 py-8 sm:px-6 lg:px-8">
        <div className="flex items-center justify-center py-20">
          <div
            className="h-10 w-10 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600"
            role="status"
            aria-label="Loading workflow details"
          />
        </div>
      </main>
    );
  }

  // ==========================================================================
  // Render — Error State
  // ==========================================================================

  if (isWorkflowError || !workflow) {
    const errorMsg =
      workflowError instanceof Error
        ? workflowError.message
        : 'Failed to load workflow details.';

    return (
      <main className="mx-auto max-w-7xl px-4 py-8 sm:px-6 lg:px-8">
        {/* Breadcrumb */}
        <nav aria-label="Breadcrumb" className="mb-6">
          <ol className="flex items-center gap-2 text-sm text-gray-500">
            <li>
              <Link
                to="/workflows"
                className="hover:text-blue-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              >
                Workflows
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li className="text-gray-400">Not Found</li>
          </ol>
        </nav>

        <div className="rounded-lg border border-red-200 bg-red-50 p-8 text-center">
          <h2 className="text-lg font-semibold text-red-800">
            Workflow Not Found
          </h2>
          <p className="mt-2 text-sm text-red-600">{errorMsg}</p>
          <Link
            to="/workflows"
            className="mt-4 inline-block rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500"
          >
            Back to Workflows
          </Link>
        </div>
      </main>
    );
  }

  // ==========================================================================
  // Render — Main Content
  // ==========================================================================

  return (
    <main className="mx-auto max-w-7xl px-4 py-8 sm:px-6 lg:px-8">
      {/* ScreenMessage toast container */}
      <ScreenMessage messages={messages} onDismiss={dismissToast} />

      {/* ---------------------------------------------------------------- */}
      {/* Breadcrumb Navigation                                            */}
      {/* ---------------------------------------------------------------- */}
      <nav aria-label="Breadcrumb" className="mb-6">
        <ol className="flex items-center gap-2 text-sm text-gray-500">
          <li>
            <Link
              to="/workflows"
              className="hover:text-blue-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
            >
              Workflows
            </Link>
          </li>
          <li aria-hidden="true">/</li>
          <li className="font-medium text-gray-900">
            {workflow.typeName || 'Workflow'}
          </li>
        </ol>
      </nav>

      {/* ---------------------------------------------------------------- */}
      {/* Sub-Navigation Tabs (Workflows / Executions / Schedules)         */}
      {/* ---------------------------------------------------------------- */}
      <div className="mb-6 flex gap-4 border-b border-gray-200">
        <Link
          to="/workflows"
          className="border-b-2 border-blue-600 pb-2 text-sm font-medium text-blue-600"
          aria-current="page"
        >
          Workflows
        </Link>
        <Link
          to="/workflows/executions"
          className="border-b-2 border-transparent pb-2 text-sm font-medium text-gray-500 transition-colors hover:border-gray-300 hover:text-gray-700"
        >
          Executions
        </Link>
        <Link
          to="/workflows/schedules"
          className="border-b-2 border-transparent pb-2 text-sm font-medium text-gray-500 transition-colors hover:border-gray-300 hover:text-gray-700"
        >
          Schedules
        </Link>
      </div>

      {/* ---------------------------------------------------------------- */}
      {/* Page Header with Action Buttons                                  */}
      {/* ---------------------------------------------------------------- */}
      <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">
            {workflow.typeName || 'Untitled Workflow'}
          </h1>
          {Boolean(workflow.attributes?.['description']) && (
            <p className="mt-1 text-sm text-gray-600">
              {String(workflow.attributes['description'])}
            </p>
          )}
        </div>

        {/* Action buttons */}
        <div className="flex flex-wrap gap-2">
          {/* Edit */}
          <Link
            to={`/workflows/${workflowId}/edit`}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          >
            Edit
          </Link>

          {/* Run Now */}
          <button
            type="button"
            onClick={handleRunNow}
            disabled={runNowMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {runNowMutation.isPending ? 'Starting…' : 'Run Now'}
          </button>

          {/* Enable / Disable */}
          <button
            type="button"
            onClick={handleToggleStatus}
            disabled={toggleStatusMutation.isPending}
            className={[
              'inline-flex items-center gap-1.5 rounded-md px-3 py-2 text-sm font-medium shadow-sm transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 disabled:cursor-not-allowed disabled:opacity-50',
              isWorkflowEnabled
                ? 'border border-yellow-300 bg-yellow-50 text-yellow-800 hover:bg-yellow-100 focus-visible:outline-yellow-500'
                : 'border border-green-300 bg-green-50 text-green-800 hover:bg-green-100 focus-visible:outline-green-500',
            ].join(' ')}
          >
            {toggleStatusMutation.isPending
              ? 'Updating…'
              : isWorkflowEnabled
                ? 'Disable'
                : 'Enable'}
          </button>

          {/* Delete */}
          <button
            type="button"
            onClick={handleDeleteClick}
            disabled={deleteMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md border border-red-300 bg-white px-3 py-2 text-sm font-medium text-red-700 shadow-sm transition-colors hover:bg-red-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {deleteMutation.isPending ? 'Deleting…' : 'Delete'}
          </button>
        </div>
      </div>

      {/* ---------------------------------------------------------------- */}
      {/* Tabbed Content (Overview / Executions / Schedules)               */}
      {/* ---------------------------------------------------------------- */}
      <section className="rounded-lg bg-white shadow">
        <TabNav
          tabs={tabs}
          activeTabId={activeTab}
          onTabChange={handleTabChange}
          className="p-6"
        />
      </section>

      {/* ---------------------------------------------------------------- */}
      {/* Delete Confirmation Modal                                        */}
      {/* ---------------------------------------------------------------- */}
      <Modal
        isVisible={isDeleteModalVisible}
        id="delete-workflow-modal"
        title="Delete Workflow"
        onClose={handleDeleteCancel}
        footer={
          <div className="flex justify-end gap-2">
            <button
              type="button"
              onClick={handleDeleteCancel}
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleDeleteConfirm}
              disabled={deleteMutation.isPending}
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {deleteMutation.isPending ? 'Deleting…' : 'Confirm Delete'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to delete the workflow{' '}
          <strong className="font-semibold text-gray-900">
            {workflow.typeName}
          </strong>
          ? This action cannot be undone. All execution history will be
          permanently removed.
        </p>
      </Modal>
    </main>
  );
}

// ---------------------------------------------------------------------------
// Default Export
// ---------------------------------------------------------------------------

export default WorkflowDetails;
