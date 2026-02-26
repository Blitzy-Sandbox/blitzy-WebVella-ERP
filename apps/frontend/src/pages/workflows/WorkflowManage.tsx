/**
 * WorkflowManage.tsx — Edit Workflow Definition Page
 *
 * React page component for editing an existing workflow definition. In the
 * monolith, workflow/job definitions were registered via reflection-based
 * discovery of ErpJob subclasses with [JobAttribute] annotations
 * (JobManager.cs RegisterJobTypes() lines 56-80). In the target serverless
 * architecture, workflow definitions are Step Functions state machine
 * definitions that can be edited through this UI.
 *
 * Route: /workflows/:workflowId/manage
 * Default export for React.lazy() route-level code splitting.
 *
 * Source files:
 *  - WebVella.Erp/Jobs/JobManager.cs   → job type registry, RegisterJobTypes
 *  - WebVella.Erp/Jobs/Models/JobType.cs → JobPriority enum, JobAttribute
 *  - WebVella.Erp/Jobs/Models/SchedulePlan.cs → scheduling model
 */

import { useState, useEffect, useCallback, useMemo } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  useWorkflow,
  useUpdateWorkflow,
  WorkflowStatus,
} from '../../hooks/useWorkflows';
import type { UpdateWorkflowPayload } from '../../hooks/useWorkflows';
import ScreenMessage, { useToast } from '../../components/common/ScreenMessage';
import { ScreenMessageType } from '../../types/common';
import type { BaseResponseModel } from '../../types/common';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';
import Modal from '../../components/common/Modal';
import { del } from '../../api/client';

// ---------------------------------------------------------------------------
// Local Type Definitions
// ---------------------------------------------------------------------------

/**
 * Step type enum matching Step Functions state types.
 * Source: AWS Step Functions ASL spec — Task, Choice, Wait, Parallel, Pass,
 * Succeed, Fail.
 */
type StepType =
  | 'Task'
  | 'Choice'
  | 'Wait'
  | 'Parallel'
  | 'Pass'
  | 'Succeed'
  | 'Fail';

/** Step types that are terminal (no "Next" field). */
const TERMINAL_STEP_TYPES: ReadonlySet<StepType> = new Set<StepType>([
  'Succeed',
  'Fail',
]);

/** Retry policy for a Task/Parallel step. */
interface RetryPolicy {
  maxAttempts: number;
  intervalSeconds: number;
  backoffRate: number;
}

/** Catch configuration for error handling fallback. */
interface CatchConfig {
  errorTypes: string[];
  fallbackStep: string;
}

/** Single branch in a Choice step. */
interface ChoiceBranch {
  id: string;
  variable: string;
  comparison: string;
  value: string;
  next: string;
}

/** Parallel execution branch. */
interface ParallelBranch {
  id: string;
  name: string;
  steps: string[];
}

// -- Per-step-type configuration interfaces --

interface TaskConfig {
  resource: string;
  retryPolicy: RetryPolicy | null;
  catchConfig: CatchConfig | null;
  timeoutSeconds: number;
  heartbeatSeconds: number;
}

interface ChoiceConfig {
  branches: ChoiceBranch[];
  defaultNext: string;
}

interface WaitConfig {
  seconds: number;
  timestamp: string;
  isDynamic: boolean;
  paths: { secondsPath: string; timestampPath: string };
}

interface ParallelConfig {
  branches: ParallelBranch[];
}

interface PassConfig {
  result: string;
  resultPath: string;
}

interface FailConfig {
  error: string;
  cause: string;
}

/** Mapped type for getting the correct config shape by StepType key. */
type StepConfigMap = {
  Task: TaskConfig;
  Choice: ChoiceConfig;
  Wait: WaitConfig;
  Parallel: ParallelConfig;
  Pass: PassConfig;
  Succeed: Record<string, never>;
  Fail: FailConfig;
};

/** Union of all possible step-config shapes. */
type StepConfigValue = StepConfigMap[StepType];

/** A single workflow step in the visual editor. */
interface WorkflowStep {
  id: string;
  name: string;
  type: StepType;
  next: string;
  config: StepConfigValue;
  inputPath: string;
  outputPath: string;
}

/** Priority dropdown option. */
interface PriorityOption {
  label: string;
  value: number;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Priority options mapped from the monolith's JobPriority enum
 * (JobType.cs): Low=1, Medium=2, High=3, Higher=4, Highest=5.
 */
const PRIORITY_OPTIONS: readonly PriorityOption[] = [
  { label: 'Low', value: 1 },
  { label: 'Medium', value: 2 },
  { label: 'High', value: 3 },
  { label: 'Higher', value: 4 },
  { label: 'Highest', value: 5 },
] as const;

/** Step type options for the add-step dropdown. */
const STEP_TYPE_OPTIONS: readonly {
  value: StepType;
  label: string;
  description: string;
}[] = [
  { value: 'Task', label: 'Task', description: 'Invoke a Lambda function or service integration' },
  { value: 'Choice', label: 'Choice', description: 'Branch based on conditions' },
  { value: 'Wait', label: 'Wait', description: 'Delay execution for a duration' },
  { value: 'Parallel', label: 'Parallel', description: 'Execute branches in parallel' },
  { value: 'Pass', label: 'Pass', description: 'Pass input to output with optional transformation' },
  { value: 'Succeed', label: 'Succeed', description: 'Terminal success state' },
  { value: 'Fail', label: 'Fail', description: 'Terminal failure state with error info' },
] as const;

/** Comparison operators for Choice step branch conditions. */
const COMPARISON_OPERATORS: readonly { value: string; label: string }[] = [
  { value: 'StringEquals', label: 'String Equals' },
  { value: 'StringNotEquals', label: 'String Not Equals' },
  { value: 'NumericEquals', label: 'Numeric Equals' },
  { value: 'NumericGreaterThan', label: 'Numeric Greater Than' },
  { value: 'NumericLessThan', label: 'Numeric Less Than' },
  { value: 'BooleanEquals', label: 'Boolean Equals' },
  { value: 'IsPresent', label: 'Is Present' },
  { value: 'IsNull', label: 'Is Null' },
] as const;

/** Color badges per step type for visual differentiation. */
const STEP_TYPE_COLORS: Record<StepType, string> = {
  Task: 'bg-blue-100 text-blue-800',
  Choice: 'bg-yellow-100 text-yellow-800',
  Wait: 'bg-purple-100 text-purple-800',
  Parallel: 'bg-indigo-100 text-indigo-800',
  Pass: 'bg-gray-100 text-gray-800',
  Succeed: 'bg-green-100 text-green-800',
  Fail: 'bg-red-100 text-red-800',
};

// ---------------------------------------------------------------------------
// Utility Functions
// ---------------------------------------------------------------------------

let stepCounter = 0;
/** Generate a unique step ID for the editor. */
function generateStepId(): string {
  stepCounter += 1;
  return `step_${Date.now()}_${stepCounter}`;
}

let branchCounter = 0;
/** Generate a unique branch ID for Choice branches. */
function generateBranchId(): string {
  branchCounter += 1;
  return `branch_${Date.now()}_${branchCounter}`;
}

/**
 * Create a default step of the given type with a unique name derived
 * from existing step names to avoid collisions.
 */
function createDefaultStep(
  type: StepType,
  existingNames: string[],
): WorkflowStep {
  let baseName: string;
  switch (type) {
    case 'Task':
      baseName = 'ProcessTask';
      break;
    case 'Choice':
      baseName = 'BranchChoice';
      break;
    case 'Wait':
      baseName = 'WaitState';
      break;
    case 'Parallel':
      baseName = 'ParallelExec';
      break;
    case 'Pass':
      baseName = 'PassThrough';
      break;
    case 'Succeed':
      baseName = 'Done';
      break;
    case 'Fail':
      baseName = 'Failed';
      break;
    default:
      baseName = 'Step';
      break;
  }

  let name = baseName;
  let idx = 2;
  while (existingNames.includes(name)) {
    name = `${baseName}${idx}`;
    idx += 1;
  }

  const configs: Record<StepType, StepConfigValue> = {
    Task: {
      resource: '',
      retryPolicy: null,
      catchConfig: null,
      timeoutSeconds: 300,
      heartbeatSeconds: 60,
    } as TaskConfig,
    Choice: {
      branches: [
        {
          id: generateBranchId(),
          variable: '$.status',
          comparison: 'StringEquals',
          value: '',
          next: 'End',
        },
      ],
      defaultNext: 'End',
    } as ChoiceConfig,
    Wait: {
      seconds: 10,
      timestamp: '',
      isDynamic: false,
      paths: { secondsPath: '', timestampPath: '' },
    } as WaitConfig,
    Parallel: {
      branches: [],
    } as ParallelConfig,
    Pass: {
      result: '{}',
      resultPath: '$',
    } as PassConfig,
    Succeed: {} as Record<string, never>,
    Fail: {
      error: 'CustomError',
      cause: 'An error occurred',
    } as FailConfig,
  };

  return {
    id: generateStepId(),
    name,
    type,
    next: TERMINAL_STEP_TYPES.has(type) ? '' : 'End',
    config: configs[type],
    inputPath: '$',
    outputPath: '$',
  };
}

// ---------------------------------------------------------------------------
// Cycle Detection — DFS-based graph analysis
// ---------------------------------------------------------------------------

/**
 * Detect cycles in the step graph to prevent infinite execution loops.
 * Returns an array of cycle descriptions (empty array = no cycles).
 */
function detectCycles(steps: WorkflowStep[]): string[] {
  const adj = new Map<string, string[]>();
  for (const step of steps) {
    const neighbours: string[] = [];
    if (step.next && step.next !== 'End') {
      neighbours.push(step.next);
    }
    if (step.type === 'Choice') {
      const cc = step.config as ChoiceConfig;
      for (const b of cc.branches) {
        if (b.next && b.next !== 'End') neighbours.push(b.next);
      }
      if (cc.defaultNext && cc.defaultNext !== 'End') {
        neighbours.push(cc.defaultNext);
      }
    }
    adj.set(step.name, neighbours);
  }

  const cycles: string[] = [];
  const visited = new Set<string>();
  const inStack = new Set<string>();

  function dfs(node: string, path: string[]): void {
    if (inStack.has(node)) {
      const cycleStart = path.indexOf(node);
      const cyclePath = path.slice(cycleStart).concat(node);
      cycles.push(`Cycle detected: ${cyclePath.join(' → ')}`);
      return;
    }
    if (visited.has(node)) return;
    visited.add(node);
    inStack.add(node);
    path.push(node);
    for (const next of adj.get(node) ?? []) {
      dfs(next, path);
    }
    path.pop();
    inStack.delete(node);
  }

  for (const step of steps) {
    if (!visited.has(step.name)) {
      dfs(step.name, []);
    }
  }
  return cycles;
}

// ---------------------------------------------------------------------------
// Sub-Components — Step Configuration Editors
// ---------------------------------------------------------------------------

/** Props shared by all step config editor sub-components. */
interface StepConfigEditorProps<T> {
  config: T;
  onChange: (updated: T) => void;
  stepNames: string[];
}

/**
 * TaskConfigEditor — resource ARN, timeout, heartbeat, retry and catch.
 */
function TaskConfigEditor({
  config,
  onChange,
  stepNames,
}: StepConfigEditorProps<TaskConfig>) {
  const handleRetryToggle = useCallback(() => {
    onChange({
      ...config,
      retryPolicy: config.retryPolicy
        ? null
        : { maxAttempts: 3, intervalSeconds: 1, backoffRate: 2 },
    });
  }, [config, onChange]);

  const handleCatchToggle = useCallback(() => {
    onChange({
      ...config,
      catchConfig: config.catchConfig
        ? null
        : { errorTypes: ['States.ALL'], fallbackStep: 'End' },
    });
  }, [config, onChange]);

  return (
    <div className="space-y-4">
      {/* Resource ARN */}
      <div>
        <label className="block text-xs font-medium text-gray-600 mb-1">
          Resource (Lambda ARN)
        </label>
        <input
          type="text"
          value={config.resource}
          onChange={(e) => onChange({ ...config, resource: e.target.value })}
          placeholder="arn:aws:lambda:us-east-1:000000000000:function:name"
          className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        />
      </div>

      {/* Timeout & Heartbeat */}
      <div className="grid grid-cols-2 gap-4">
        <div>
          <label className="block text-xs font-medium text-gray-600 mb-1">
            Timeout (seconds)
          </label>
          <input
            type="number"
            min={1}
            value={config.timeoutSeconds}
            onChange={(e) =>
              onChange({
                ...config,
                timeoutSeconds: parseInt(e.target.value, 10) || 0,
              })
            }
            className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-gray-600 mb-1">
            Heartbeat (seconds)
          </label>
          <input
            type="number"
            min={0}
            value={config.heartbeatSeconds}
            onChange={(e) =>
              onChange({
                ...config,
                heartbeatSeconds: parseInt(e.target.value, 10) || 0,
              })
            }
            className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          />
        </div>
      </div>

      {/* Retry Policy */}
      <div className="space-y-2">
        <label className="flex items-center gap-2 text-xs font-medium text-gray-600">
          <input
            type="checkbox"
            checked={config.retryPolicy !== null}
            onChange={handleRetryToggle}
            className="rounded border-gray-300 text-blue-600 focus-visible:ring-blue-500"
          />
          Enable Retry Policy
        </label>
        {config.retryPolicy && (
          <div className="grid grid-cols-3 gap-3 rounded-md border border-gray-200 bg-gray-50 p-3">
            <div>
              <label className="block text-xs text-gray-500 mb-1">
                Max Attempts
              </label>
              <input
                type="number"
                min={1}
                value={config.retryPolicy.maxAttempts}
                onChange={(e) =>
                  onChange({
                    ...config,
                    retryPolicy: {
                      ...config.retryPolicy!,
                      maxAttempts: parseInt(e.target.value, 10) || 1,
                    },
                  })
                }
                className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
              />
            </div>
            <div>
              <label className="block text-xs text-gray-500 mb-1">
                Interval (s)
              </label>
              <input
                type="number"
                min={1}
                value={config.retryPolicy.intervalSeconds}
                onChange={(e) =>
                  onChange({
                    ...config,
                    retryPolicy: {
                      ...config.retryPolicy!,
                      intervalSeconds: parseInt(e.target.value, 10) || 1,
                    },
                  })
                }
                className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
              />
            </div>
            <div>
              <label className="block text-xs text-gray-500 mb-1">
                Backoff Rate
              </label>
              <input
                type="number"
                min={1}
                step={0.1}
                value={config.retryPolicy.backoffRate}
                onChange={(e) =>
                  onChange({
                    ...config,
                    retryPolicy: {
                      ...config.retryPolicy!,
                      backoffRate: parseFloat(e.target.value) || 1,
                    },
                  })
                }
                className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
              />
            </div>
          </div>
        )}
      </div>

      {/* Catch Config */}
      <div className="space-y-2">
        <label className="flex items-center gap-2 text-xs font-medium text-gray-600">
          <input
            type="checkbox"
            checked={config.catchConfig !== null}
            onChange={handleCatchToggle}
            className="rounded border-gray-300 text-blue-600 focus-visible:ring-blue-500"
          />
          Enable Error Catch
        </label>
        {config.catchConfig && (
          <div className="grid grid-cols-2 gap-3 rounded-md border border-gray-200 bg-gray-50 p-3">
            <div>
              <label className="block text-xs text-gray-500 mb-1">
                Error Types (comma-separated)
              </label>
              <input
                type="text"
                value={config.catchConfig.errorTypes.join(', ')}
                onChange={(e) =>
                  onChange({
                    ...config,
                    catchConfig: {
                      ...config.catchConfig!,
                      errorTypes: e.target.value
                        .split(',')
                        .map((s) => s.trim())
                        .filter(Boolean),
                    },
                  })
                }
                className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
              />
            </div>
            <div>
              <label className="block text-xs text-gray-500 mb-1">
                Fallback Step
              </label>
              <select
                value={config.catchConfig.fallbackStep}
                onChange={(e) =>
                  onChange({
                    ...config,
                    catchConfig: {
                      ...config.catchConfig!,
                      fallbackStep: e.target.value,
                    },
                  })
                }
                className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
              >
                <option value="End">End</option>
                {stepNames.map((sn) => (
                  <option key={sn} value={sn}>
                    {sn}
                  </option>
                ))}
              </select>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * ChoiceConfigEditor — branch condition management with CRUD for branches.
 */
function ChoiceConfigEditor({
  config,
  onChange,
  stepNames,
}: StepConfigEditorProps<ChoiceConfig>) {
  const handleAddBranch = useCallback(() => {
    onChange({
      ...config,
      branches: [
        ...config.branches,
        {
          id: generateBranchId(),
          variable: '$.status',
          comparison: 'StringEquals',
          value: '',
          next: 'End',
        },
      ],
    });
  }, [config, onChange]);

  const handleRemoveBranch = useCallback(
    (branchId: string) => {
      onChange({
        ...config,
        branches: config.branches.filter((b) => b.id !== branchId),
      });
    },
    [config, onChange],
  );

  const handleBranchUpdate = useCallback(
    (branchId: string, field: keyof ChoiceBranch, value: string) => {
      onChange({
        ...config,
        branches: config.branches.map((b) =>
          b.id === branchId ? { ...b, [field]: value } : b,
        ),
      });
    },
    [config, onChange],
  );

  return (
    <div className="space-y-4">
      {/* Branches */}
      <div className="space-y-3">
        <span className="block text-xs font-medium text-gray-600">
          Condition Branches
        </span>
        {config.branches.map((branch, idx) => (
          <div
            key={branch.id}
            className="grid grid-cols-[1fr_1fr_1fr_1fr_auto] gap-2 items-end rounded-md border border-gray-200 bg-gray-50 p-3"
          >
            <div>
              <label className="block text-xs text-gray-500 mb-1">
                Variable
              </label>
              <input
                type="text"
                value={branch.variable}
                onChange={(e) =>
                  handleBranchUpdate(branch.id, 'variable', e.target.value)
                }
                placeholder="$.path"
                className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
              />
            </div>
            <div>
              <label className="block text-xs text-gray-500 mb-1">
                Operator
              </label>
              <select
                value={branch.comparison}
                onChange={(e) =>
                  handleBranchUpdate(branch.id, 'comparison', e.target.value)
                }
                className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
              >
                {COMPARISON_OPERATORS.map((op) => (
                  <option key={op.value} value={op.value}>
                    {op.label}
                  </option>
                ))}
              </select>
            </div>
            <div>
              <label className="block text-xs text-gray-500 mb-1">Value</label>
              <input
                type="text"
                value={branch.value}
                onChange={(e) =>
                  handleBranchUpdate(branch.id, 'value', e.target.value)
                }
                placeholder="expected"
                className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
              />
            </div>
            <div>
              <label className="block text-xs text-gray-500 mb-1">Next</label>
              <select
                value={branch.next}
                onChange={(e) =>
                  handleBranchUpdate(branch.id, 'next', e.target.value)
                }
                className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
              >
                <option value="End">End</option>
                {stepNames.map((sn) => (
                  <option key={sn} value={sn}>
                    {sn}
                  </option>
                ))}
              </select>
            </div>
            <button
              type="button"
              onClick={() => handleRemoveBranch(branch.id)}
              disabled={config.branches.length <= 1}
              className="rounded p-1 text-gray-400 hover:text-red-500 disabled:opacity-30 disabled:cursor-not-allowed focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
              aria-label={`Remove branch ${idx + 1}`}
            >
              <svg
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-4 w-4"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M8.75 1A2.75 2.75 0 006 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 10.23 1.482l.149-.022 1.005 11.26A2.75 2.75 0 007.77 19.5h4.46a2.75 2.75 0 002.751-2.539l1.005-11.26.149.022a.75.75 0 00.23-1.482A41.03 41.03 0 0014 4.193V3.75A2.75 2.75 0 0011.25 1h-2.5zM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325C8.327 4.025 9.16 4 10 4zM8.58 7.72a.75.75 0 00-1.5.06l.3 7.5a.75.75 0 101.5-.06l-.3-7.5zm4.34.06a.75.75 0 10-1.5-.06l-.3 7.5a.75.75 0 101.5.06l.3-7.5z"
                  clipRule="evenodd"
                />
              </svg>
            </button>
          </div>
        ))}
        <button
          type="button"
          onClick={handleAddBranch}
          className="inline-flex items-center gap-1 text-xs font-medium text-blue-600 hover:text-blue-700 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500 rounded px-1"
        >
          <svg
            viewBox="0 0 20 20"
            fill="currentColor"
            className="h-3.5 w-3.5"
            aria-hidden="true"
          >
            <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
          </svg>
          Add Branch
        </button>
      </div>

      {/* Default Next */}
      <div>
        <label className="block text-xs font-medium text-gray-600 mb-1">
          Default Next
        </label>
        <select
          value={config.defaultNext}
          onChange={(e) =>
            onChange({ ...config, defaultNext: e.target.value })
          }
          className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        >
          <option value="End">End</option>
          {stepNames.map((sn) => (
            <option key={sn} value={sn}>
              {sn}
            </option>
          ))}
        </select>
      </div>
    </div>
  );
}

/**
 * WaitConfigEditor — delay duration / timestamp / dynamic path configuration.
 */
function WaitConfigEditor({
  config,
  onChange,
}: StepConfigEditorProps<WaitConfig>) {
  return (
    <div className="space-y-4">
      <label className="flex items-center gap-2 text-xs font-medium text-gray-600">
        <input
          type="checkbox"
          checked={config.isDynamic}
          onChange={(e) =>
            onChange({ ...config, isDynamic: e.target.checked })
          }
          className="rounded border-gray-300 text-blue-600 focus-visible:ring-blue-500"
        />
        Use Dynamic Path (JSONPath reference)
      </label>

      {config.isDynamic ? (
        <div className="grid grid-cols-2 gap-4">
          <div>
            <label className="block text-xs text-gray-500 mb-1">
              Seconds Path
            </label>
            <input
              type="text"
              value={config.paths.secondsPath}
              onChange={(e) =>
                onChange({
                  ...config,
                  paths: { ...config.paths, secondsPath: e.target.value },
                })
              }
              placeholder="$.waitSeconds"
              className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
            />
          </div>
          <div>
            <label className="block text-xs text-gray-500 mb-1">
              Timestamp Path
            </label>
            <input
              type="text"
              value={config.paths.timestampPath}
              onChange={(e) =>
                onChange({
                  ...config,
                  paths: { ...config.paths, timestampPath: e.target.value },
                })
              }
              placeholder="$.waitUntil"
              className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
            />
          </div>
        </div>
      ) : (
        <div className="grid grid-cols-2 gap-4">
          <div>
            <label className="block text-xs text-gray-500 mb-1">
              Wait Seconds
            </label>
            <input
              type="number"
              min={0}
              value={config.seconds}
              onChange={(e) =>
                onChange({
                  ...config,
                  seconds: parseInt(e.target.value, 10) || 0,
                })
              }
              className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
            />
          </div>
          <div>
            <label className="block text-xs text-gray-500 mb-1">
              Or Timestamp (ISO 8601)
            </label>
            <input
              type="text"
              value={config.timestamp}
              onChange={(e) =>
                onChange({ ...config, timestamp: e.target.value })
              }
              placeholder="2025-12-31T23:59:59Z"
              className="block w-full rounded border border-gray-300 px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
            />
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * FailConfigEditor — error name and cause message inputs.
 */
function FailConfigEditor({
  config,
  onChange,
}: StepConfigEditorProps<FailConfig>) {
  return (
    <div className="grid grid-cols-2 gap-4">
      <div>
        <label className="block text-xs font-medium text-gray-600 mb-1">
          Error Name
        </label>
        <input
          type="text"
          value={config.error}
          onChange={(e) => onChange({ ...config, error: e.target.value })}
          placeholder="CustomError"
          className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        />
      </div>
      <div>
        <label className="block text-xs font-medium text-gray-600 mb-1">
          Cause
        </label>
        <input
          type="text"
          value={config.cause}
          onChange={(e) => onChange({ ...config, cause: e.target.value })}
          placeholder="Describe the failure cause"
          className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        />
      </div>
    </div>
  );
}

/**
 * PassConfigEditor — result JSON and result path inputs.
 */
function PassConfigEditor({
  config,
  onChange,
}: StepConfigEditorProps<PassConfig>) {
  return (
    <div className="space-y-4">
      <div>
        <label className="block text-xs font-medium text-gray-600 mb-1">
          Result (JSON)
        </label>
        <textarea
          value={config.result}
          onChange={(e) => onChange({ ...config, result: e.target.value })}
          rows={3}
          placeholder='{"key": "value"}'
          className="block w-full rounded-md border border-gray-300 px-3 py-1.5 font-mono text-xs shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        />
      </div>
      <div>
        <label className="block text-xs font-medium text-gray-600 mb-1">
          Result Path
        </label>
        <input
          type="text"
          value={config.resultPath}
          onChange={(e) =>
            onChange({ ...config, resultPath: e.target.value })
          }
          placeholder="$"
          className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        />
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// StepPanel — Expandable panel for a single workflow step
// ---------------------------------------------------------------------------

interface StepPanelProps {
  step: WorkflowStep;
  index: number;
  totalSteps: number;
  isStartStep: boolean;
  stepNames: string[];
  expanded: boolean;
  onToggleExpand: () => void;
  onUpdate: (updated: WorkflowStep) => void;
  onRemove: () => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
  onSetStart: () => void;
}

function StepPanel({
  step,
  index,
  totalSteps,
  isStartStep,
  stepNames,
  expanded,
  onToggleExpand,
  onUpdate,
  onRemove,
  onMoveUp,
  onMoveDown,
  onSetStart,
}: StepPanelProps) {
  /** Colour badge for step type. */
  const colorClass =
    STEP_TYPE_COLORS[step.type] ?? 'bg-gray-100 text-gray-700';

  /** Names of all other steps (for Next selectors). */
  const otherStepNames = useMemo(
    () => stepNames.filter((n) => n !== step.name),
    [stepNames, step.name],
  );

  /** Dispatch config changes to the parent via onUpdate. */
  const handleConfigChange = useCallback(
    (newConfig: StepConfigValue) => {
      onUpdate({ ...step, config: newConfig });
    },
    [step, onUpdate],
  );

  /** Render the appropriate config editor based on step type. */
  const renderConfigEditor = () => {
    switch (step.type) {
      case 'Task':
        return (
          <TaskConfigEditor
            config={step.config as TaskConfig}
            onChange={(c) => handleConfigChange(c)}
            stepNames={otherStepNames}
          />
        );
      case 'Choice':
        return (
          <ChoiceConfigEditor
            config={step.config as ChoiceConfig}
            onChange={(c) => handleConfigChange(c)}
            stepNames={otherStepNames}
          />
        );
      case 'Wait':
        return (
          <WaitConfigEditor
            config={step.config as WaitConfig}
            onChange={(c) => handleConfigChange(c)}
            stepNames={otherStepNames}
          />
        );
      case 'Fail':
        return (
          <FailConfigEditor
            config={step.config as FailConfig}
            onChange={(c) => handleConfigChange(c)}
            stepNames={otherStepNames}
          />
        );
      case 'Pass':
        return (
          <PassConfigEditor
            config={step.config as PassConfig}
            onChange={(c) => handleConfigChange(c)}
            stepNames={otherStepNames}
          />
        );
      case 'Succeed':
        return (
          <p className="text-xs text-gray-500 italic">
            This step marks the workflow as successfully completed. No
            configuration required.
          </p>
        );
      case 'Parallel':
        return (
          <p className="text-xs text-gray-500 italic">
            Parallel branch configuration is managed via the step definition
            JSON. Use the Input/Output fields for path configuration.
          </p>
        );
      default:
        return null;
    }
  };

  /** Whether the step type allows a "Next" selector. */
  const showNextSelector =
    step.type !== 'Choice' &&
    step.type !== 'Succeed' &&
    step.type !== 'Fail';

  return (
    <div
      className={`rounded-lg border ${
        isStartStep
          ? 'border-green-300 bg-green-50/50'
          : 'border-gray-200 bg-white'
      } shadow-sm`}
    >
      {/* Collapsed header */}
      <div className="flex items-center gap-2 px-4 py-3">
        {/* Reorder buttons */}
        <div className="flex flex-col gap-0.5">
          <button
            type="button"
            onClick={onMoveUp}
            disabled={index === 0}
            className="text-gray-400 hover:text-gray-600 disabled:opacity-25 disabled:cursor-not-allowed focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500 rounded"
            aria-label={`Move step ${step.name} up`}
          >
            <svg viewBox="0 0 20 20" fill="currentColor" className="h-3.5 w-3.5" aria-hidden="true">
              <path fillRule="evenodd" d="M14.77 12.79a.75.75 0 01-1.06-.02L10 8.832l-3.71 3.938a.75.75 0 01-1.08-1.04l4.25-4.5a.75.75 0 011.08 0l4.25 4.5a.75.75 0 01-.02 1.06z" clipRule="evenodd" />
            </svg>
          </button>
          <button
            type="button"
            onClick={onMoveDown}
            disabled={index === totalSteps - 1}
            className="text-gray-400 hover:text-gray-600 disabled:opacity-25 disabled:cursor-not-allowed focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500 rounded"
            aria-label={`Move step ${step.name} down`}
          >
            <svg viewBox="0 0 20 20" fill="currentColor" className="h-3.5 w-3.5" aria-hidden="true">
              <path fillRule="evenodd" d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z" clipRule="evenodd" />
            </svg>
          </button>
        </div>

        {/* Step index */}
        <span className="flex h-6 w-6 items-center justify-center rounded-full bg-gray-200 text-xs font-semibold text-gray-700">
          {index + 1}
        </span>

        {/* Step name */}
        <button
          type="button"
          onClick={onToggleExpand}
          className="flex flex-1 items-center gap-2 text-left focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500 rounded"
        >
          <span className="text-sm font-medium text-gray-900 truncate">
            {step.name || 'Unnamed Step'}
          </span>
          <span
            className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${colorClass}`}
          >
            {step.type}
          </span>
          {isStartStep && (
            <span className="inline-flex items-center rounded-full bg-green-100 px-2 py-0.5 text-xs font-medium text-green-700">
              Start
            </span>
          )}
        </button>

        {/* Actions */}
        {!isStartStep && (
          <button
            type="button"
            onClick={onSetStart}
            className="rounded px-2 py-1 text-xs text-gray-500 hover:bg-green-50 hover:text-green-700 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
            aria-label={`Set ${step.name} as start step`}
          >
            Set Start
          </button>
        )}
        <button
          type="button"
          onClick={onRemove}
          className="rounded p-1 text-gray-400 hover:text-red-500 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
          aria-label={`Remove step ${step.name}`}
        >
          <svg viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
            <path fillRule="evenodd" d="M8.75 1A2.75 2.75 0 006 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 10.23 1.482l.149-.022 1.005 11.26A2.75 2.75 0 007.77 19.5h4.46a2.75 2.75 0 002.751-2.539l1.005-11.26.149.022a.75.75 0 00.23-1.482A41.03 41.03 0 0014 4.193V3.75A2.75 2.75 0 0011.25 1h-2.5zM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325C8.327 4.025 9.16 4 10 4zM8.58 7.72a.75.75 0 00-1.5.06l.3 7.5a.75.75 0 101.5-.06l-.3-7.5zm4.34.06a.75.75 0 10-1.5-.06l-.3 7.5a.75.75 0 101.5.06l.3-7.5z" clipRule="evenodd" />
          </svg>
        </button>
        {/* Expand/collapse chevron */}
        <button
          type="button"
          onClick={onToggleExpand}
          className="rounded p-1 text-gray-400 hover:text-gray-600 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
          aria-label={expanded ? 'Collapse step' : 'Expand step'}
        >
          <svg
            viewBox="0 0 20 20"
            fill="currentColor"
            className={`h-4 w-4 transition-transform ${expanded ? 'rotate-180' : ''}`}
            aria-hidden="true"
          >
            <path fillRule="evenodd" d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z" clipRule="evenodd" />
          </svg>
        </button>
      </div>

      {/* Expanded body */}
      {expanded && (
        <div className="border-t border-gray-200 px-4 py-4 space-y-4">
          {/* Step Name */}
          <div>
            <label className="block text-xs font-medium text-gray-600 mb-1">
              Step Name
            </label>
            <input
              type="text"
              value={step.name}
              onChange={(e) => onUpdate({ ...step, name: e.target.value })}
              className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            />
          </div>

          {/* Step Type */}
          <div>
            <label className="block text-xs font-medium text-gray-600 mb-1">
              Step Type
            </label>
            <select
              value={step.type}
              onChange={(e) => {
                const newType = e.target.value as StepType;
                if (newType === step.type) return;
                const newStep = createDefaultStep(newType, stepNames);
                onUpdate({
                  ...step,
                  type: newType,
                  config: newStep.config,
                  next: TERMINAL_STEP_TYPES.has(newType) ? 'End' : step.next,
                });
              }}
              className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            >
              {STEP_TYPE_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label} — {opt.description}
                </option>
              ))}
            </select>
          </div>

          {/* Next Step (only for non-terminal and non-Choice types) */}
          {showNextSelector && (
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">
                Next Step
              </label>
              <select
                value={step.next}
                onChange={(e) => onUpdate({ ...step, next: e.target.value })}
                className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                <option value="End">End</option>
                {otherStepNames.map((sn) => (
                  <option key={sn} value={sn}>
                    {sn}
                  </option>
                ))}
              </select>
            </div>
          )}

          {/* Input/Output Path */}
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">
                Input Path
              </label>
              <input
                type="text"
                value={step.inputPath}
                onChange={(e) =>
                  onUpdate({ ...step, inputPath: e.target.value })
                }
                placeholder="$"
                className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">
                Output Path
              </label>
              <input
                type="text"
                value={step.outputPath}
                onChange={(e) =>
                  onUpdate({ ...step, outputPath: e.target.value })
                }
                placeholder="$"
                className="block w-full rounded-md border border-gray-300 px-3 py-1.5 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              />
            </div>
          </div>

          {/* Type-specific config */}
          <div className="rounded-md border border-gray-100 bg-gray-50/50 p-4">
            <h4 className="mb-3 text-xs font-semibold uppercase tracking-wide text-gray-500">
              {step.type} Configuration
            </h4>
            {renderConfigEditor()}
          </div>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main Component — WorkflowManage
// ---------------------------------------------------------------------------

/**
 * WorkflowManage — page-level component for editing an existing workflow
 * definition (Step Functions state machine). Default export for React.lazy()
 * route-level code splitting.
 *
 * Route: /workflows/:workflowId/manage
 */
function WorkflowManage() {
  // ---- Routing ----
  const { workflowId } = useParams<{ workflowId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  // ---- Server state ----
  const {
    data: workflowData,
    isLoading: isFetching,
    isError: isFetchError,
    error: fetchError,
  } = useWorkflow(workflowId ?? '');

  const {
    mutate: updateWorkflow,
    mutateAsync: updateWorkflowAsync,
    isPending: isUpdating,
    isError: isUpdateError,
    error: updateError,
    isSuccess: isUpdateSuccess,
  } = useUpdateWorkflow();

  // ---- Delete mutation (no pre-built hook available) ----
  const deleteMutation = useMutation({
    mutationFn: async () => {
      return del<BaseResponseModel>(`/v1/workflows/${workflowId}`);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      queryClient.invalidateQueries({ queryKey: ['workflows', workflowId] });
      showToast(ScreenMessageType.Success, 'Deleted', 'Workflow deleted successfully.');
      navigate('/workflows');
    },
    onError: (err: Error) => {
      showToast(ScreenMessageType.Error, 'Error', err.message || 'Failed to delete workflow.');
    },
  });

  // ---- Toast notifications ----
  const { messages, showToast, dismissToast } = useToast();

  // ---- Form state ----
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [priority, setPriority] = useState(2); // Medium
  const [allowSingleInstance, setAllowSingleInstance] = useState(false);
  const [isActive, setIsActive] = useState(true);
  const [timeout, setTimeout_] = useState(300);
  const [steps, setSteps] = useState<WorkflowStep[]>([]);
  const [startStepName, setStartStepName] = useState('');

  // ---- UI state ----
  const [expandedStepIds, setExpandedStepIds] = useState<Set<string>>(new Set());
  const [showDeleteModal, setShowDeleteModal] = useState(false);
  const [showValidation, setShowValidation] = useState(false);
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [formPopulated, setFormPopulated] = useState(false);

  // ---- Populate form from fetched data ----
  // workflowData is ApiResponse<Workflow>; the Workflow lives at .object
  const workflow = workflowData?.object;

  useEffect(() => {
    if (!workflow || formPopulated) return;

    // Extract fields from the Workflow object
    setName(workflow.typeName || '');

    // Attributes bag carries the definition payload
    const attrs = workflow.attributes ?? {};
    setDescription((attrs.description as string) || '');
    setPriority(workflow.priority ?? 2);
    setAllowSingleInstance(Boolean(attrs.allowSingleInstance));
    setIsActive(
      workflow.status !== undefined
        ? workflow.status !== WorkflowStatus.Aborted &&
          workflow.status !== WorkflowStatus.Failed
        : true,
    );
    setTimeout_(typeof attrs.timeout === 'number' ? attrs.timeout : 300);

    // Deserialize steps from the attributes bag
    if (Array.isArray(attrs.steps)) {
      const deserialized: WorkflowStep[] = (attrs.steps as unknown[]).map(
        (raw: unknown) => {
          const s = raw as Record<string, unknown>;
          return {
            id: (s.id as string) || generateStepId(),
            name: (s.name as string) || 'Unnamed',
            type: (s.type as StepType) || 'Task',
            config: (s.config as StepConfigValue) || ({
              resource: '',
              timeoutSeconds: 300,
              heartbeatSeconds: 0,
              retryPolicy: null,
              catchConfig: null,
            } as TaskConfig),
            next: (s.next as string) || 'End',
            inputPath: (s.inputPath as string) || '$',
            outputPath: (s.outputPath as string) || '$',
          };
        },
      );
      setSteps(deserialized);
      if (deserialized.length > 0) {
        setStartStepName(
          (attrs.startStep as string) || deserialized[0].name,
        );
      }
    }

    setFormPopulated(true);
  }, [workflow, formPopulated]);

  // ---- Derived values ----
  const stepNames = useMemo(() => steps.map((s) => s.name), [steps]);

  /** Memoised form validation. */
  const validation: FormValidation = useMemo(() => {
    const errors: ValidationError[] = [];

    if (!name.trim()) {
      errors.push({ propertyName: 'name', message: 'Name is required.' });
    }
    if (steps.length === 0) {
      errors.push({
        propertyName: 'steps',
        message: 'At least one step must be defined.',
      });
    }

    // Duplicate step names
    const nameSet = new Set<string>();
    for (const s of steps) {
      if (!s.name.trim()) {
        errors.push({
          propertyName: `step_${s.id}`,
          message: 'Every step must have a name.',
        });
      } else if (nameSet.has(s.name)) {
        errors.push({
          propertyName: `step_${s.id}`,
          message: `Duplicate step name: "${s.name}".`,
        });
      }
      nameSet.add(s.name);
    }

    // Dangling "Next" references
    for (const s of steps) {
      if (s.next !== 'End' && !nameSet.has(s.next)) {
        errors.push({
          propertyName: `step_${s.id}_next`,
          message: `Step "${s.name}" references non-existent next step "${s.next}".`,
        });
      }
      // Check Choice branch targets
      if (s.type === 'Choice') {
        const choiceCfg = s.config as ChoiceConfig;
        for (const br of choiceCfg.branches) {
          if (br.next !== 'End' && !nameSet.has(br.next)) {
            errors.push({
              propertyName: `step_${s.id}_branch_${br.id}`,
              message: `Choice branch in "${s.name}" targets non-existent step "${br.next}".`,
            });
          }
        }
        if (
          choiceCfg.defaultNext !== 'End' &&
          !nameSet.has(choiceCfg.defaultNext)
        ) {
          errors.push({
            propertyName: `step_${s.id}_defaultNext`,
            message: `Choice default in "${s.name}" targets non-existent step "${choiceCfg.defaultNext}".`,
          });
        }
      }
      // Check Catch fallback targets
      if (s.type === 'Task') {
        const taskCfg = s.config as TaskConfig;
        if (taskCfg.catchConfig) {
          const fb = taskCfg.catchConfig.fallbackStep;
          if (fb !== 'End' && !nameSet.has(fb)) {
            errors.push({
              propertyName: `step_${s.id}_catch`,
              message: `Catch in "${s.name}" targets non-existent step "${fb}".`,
            });
          }
        }
      }
    }

    // Start step must be defined
    if (steps.length > 0 && !startStepName) {
      errors.push({
        propertyName: 'startStep',
        message: 'A start step must be defined.',
      });
    } else if (startStepName && !nameSet.has(startStepName)) {
      errors.push({
        propertyName: 'startStep',
        message: `Start step "${startStepName}" is not in the step list.`,
      });
    }

    // At least one terminal step
    if (steps.length > 0) {
      const hasTerminal = steps.some(
        (s) =>
          TERMINAL_STEP_TYPES.has(s.type) || s.next === 'End',
      );
      if (!hasTerminal) {
        errors.push({
          propertyName: 'steps',
          message:
            'At least one step must be terminal (Succeed, Fail, or Next = End).',
        });
      }
    }

    // Cycle detection
    const cycles = detectCycles(steps);
    for (const desc of cycles) {
      errors.push({ propertyName: 'steps_cycle', message: desc });
    }

    return { message: errors.length > 0 ? 'Please fix the errors below.' : undefined, errors };
  }, [name, steps, startStepName]);

  // ---- Handlers ----

  /** Add a new step with sensible defaults. */
  const handleAddStep = useCallback(() => {
    const newStep = createDefaultStep('Task', stepNames);
    setSteps((prev) => [...prev, newStep]);
    setExpandedStepIds((prev) => new Set([...prev, newStep.id]));
    // If this is the first step, make it the start
    if (steps.length === 0) {
      setStartStepName(newStep.name);
    }
  }, [stepNames, steps.length]);

  /** Remove a step by index (with dependency reference check). */
  const handleRemoveStep = useCallback(
    (idx: number) => {
      const removedName = steps[idx].name;
      setSteps((prev) => {
        const updated = prev.filter((_, i) => i !== idx);
        // Clear Next references that pointed to the removed step
        return updated.map((s): WorkflowStep => {
          const patchedNext = s.next === removedName ? 'End' : s.next;
          if (s.type === 'Choice') {
            const choiceCfg = s.config as ChoiceConfig;
            return {
              ...s,
              next: patchedNext,
              config: {
                ...choiceCfg,
                branches: choiceCfg.branches.map((br: ChoiceBranch) => ({
                  ...br,
                  next: br.next === removedName ? 'End' : br.next,
                })),
                defaultNext:
                  choiceCfg.defaultNext === removedName
                    ? 'End'
                    : choiceCfg.defaultNext,
              } as ChoiceConfig,
            };
          }
          if (s.type === 'Task') {
            const taskCfg = s.config as TaskConfig;
            if (taskCfg.catchConfig) {
              return {
                ...s,
                next: patchedNext,
                config: {
                  ...taskCfg,
                  catchConfig: {
                    ...taskCfg.catchConfig,
                    fallbackStep:
                      taskCfg.catchConfig.fallbackStep === removedName
                        ? 'End'
                        : taskCfg.catchConfig.fallbackStep,
                  },
                } as TaskConfig,
              };
            }
          }
          return { ...s, next: patchedNext };
        });
      });
      // Adjust start step if it was removed
      if (startStepName === removedName) {
        setStartStepName(() => {
          const remaining = steps.filter((_, i) => i !== idx);
          return remaining.length > 0 ? remaining[0].name : '';
        });
      }
    },
    [steps, startStepName],
  );

  /** Reorder a step (move up or down). */
  const handleReorderStep = useCallback(
    (fromIndex: number, direction: 'up' | 'down') => {
      const toIndex = direction === 'up' ? fromIndex - 1 : fromIndex + 1;
      if (toIndex < 0 || toIndex >= steps.length) return;
      setSteps((prev) => {
        const updated = [...prev];
        const temp = updated[fromIndex];
        updated[fromIndex] = updated[toIndex];
        updated[toIndex] = temp;
        return updated;
      });
    },
    [steps.length],
  );

  /** Update a single step in-place. */
  const handleStepUpdate = useCallback(
    (idx: number, updated: WorkflowStep) => {
      setSteps((prev) => {
        const oldName = prev[idx].name;
        const newName = updated.name;
        const arr = [...prev];
        arr[idx] = updated;
        // Rename Next references if the step name changed
        if (oldName !== newName) {
          return arr.map((s, i): WorkflowStep => {
            if (i === idx) return s;
            let patched: WorkflowStep = { ...s };
            if (patched.next === oldName) patched = { ...patched, next: newName };
            if (patched.type === 'Choice') {
              const choiceCfg = patched.config as ChoiceConfig;
              patched = {
                ...patched,
                config: {
                  ...choiceCfg,
                  branches: choiceCfg.branches.map((br: ChoiceBranch) => ({
                    ...br,
                    next: br.next === oldName ? newName : br.next,
                  })),
                  defaultNext:
                    choiceCfg.defaultNext === oldName
                      ? newName
                      : choiceCfg.defaultNext,
                } as ChoiceConfig,
              };
            }
            if (patched.type === 'Task') {
              const taskCfg = patched.config as TaskConfig;
              if (taskCfg.catchConfig?.fallbackStep === oldName) {
                patched = {
                  ...patched,
                  config: {
                    ...taskCfg,
                    catchConfig: {
                      ...taskCfg.catchConfig,
                      fallbackStep: newName,
                    },
                  } as TaskConfig,
                };
              }
            }
            return patched;
          });
        }
        return arr;
      });
      // Update start step name if it was renamed
      if (steps[idx] && steps[idx].name !== updated.name && startStepName === steps[idx].name) {
        setStartStepName(updated.name);
      }
    },
    [steps, startStepName],
  );

  /** Toggle expand/collapse of a step panel. */
  const handleToggleExpand = useCallback((stepId: string) => {
    setExpandedStepIds((prev) => {
      const next = new Set(prev);
      if (next.has(stepId)) {
        next.delete(stepId);
      } else {
        next.add(stepId);
      }
      return next;
    });
  }, []);

  /** Set the start step. */
  const handleSetStart = useCallback((stepName: string) => {
    setStartStepName(stepName);
  }, []);

  /** Submit the form (save changes). */
  const handleSubmit = useCallback(
    (e: React.FormEvent) => {
      e.preventDefault();
      setShowValidation(true);

      if (validation.errors.length > 0) {
        showToast(
          ScreenMessageType.Error,
          'Validation Error',
          validation.message || 'Please fix validation errors.',
        );
        return;
      }

      // Build the payload — we use type assertion because UpdateWorkflowPayload
      // only declares a subset of fields, but the PUT endpoint accepts the full body.
      const payload = {
        id: workflowId ?? '',
        priority,
        status: isActive ? WorkflowStatus.Pending : WorkflowStatus.Aborted,
        typeName: name.trim(),
        attributes: {
          description: description.trim(),
          allowSingleInstance,
          timeout: timeout,
          startStep: startStepName,
          steps: steps.map((s) => ({
            id: s.id,
            name: s.name,
            type: s.type,
            config: s.config,
            next: s.next,
            inputPath: s.inputPath,
            outputPath: s.outputPath,
          })),
        },
      } as unknown as UpdateWorkflowPayload;

      updateWorkflow(payload, {
        onSuccess: () => {
          showToast(
            ScreenMessageType.Success,
            'Success',
            'Workflow updated successfully.',
          );
          navigate(`/workflows/${workflowId}`);
        },
        onError: (err: Error) => {
          showToast(
            ScreenMessageType.Error,
            'Error',
            err.message || 'Failed to update workflow.',
          );
        },
      });
    },
    [
      validation,
      workflowId,
      name,
      description,
      priority,
      allowSingleInstance,
      isActive,
      timeout,
      steps,
      startStepName,
      updateWorkflow,
      navigate,
      showToast,
    ],
  );

  /** Handle delete confirmation. */
  const handleDeleteConfirm = useCallback(() => {
    setShowDeleteModal(false);
    deleteMutation.mutate();
  }, [deleteMutation]);

  // ---- Loading state ----
  if (isFetching) {
    return (
      <div className="flex items-center justify-center py-24">
        <div className="text-center space-y-3">
          <svg
            className="mx-auto h-8 w-8 animate-spin text-blue-600"
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
              d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z"
            />
          </svg>
          <p className="text-sm text-gray-500">Loading workflow…</p>
        </div>
      </div>
    );
  }

  // ---- Error state ----
  if (isFetchError) {
    return (
      <div className="mx-auto max-w-3xl py-16">
        <div className="rounded-lg border border-red-200 bg-red-50 p-6 text-center">
          <svg
            className="mx-auto mb-3 h-10 w-10 text-red-400"
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zM8.94 6.94a.75.75 0 11-1.06-1.06.75.75 0 011.06 1.06zm2.12 0a.75.75 0 11-1.06-1.06.75.75 0 011.06 1.06zM7.25 12a.75.75 0 01.75-.75h4a.75.75 0 010 1.5H8a.75.75 0 01-.75-.75z"
              clipRule="evenodd"
            />
          </svg>
          <h2 className="text-lg font-semibold text-red-800">
            Failed to load workflow
          </h2>
          <p className="mt-1 text-sm text-red-600">
            {(fetchError as Error)?.message || 'An unexpected error occurred.'}
          </p>
          <button
            type="button"
            onClick={() => navigate('/workflows')}
            className="mt-4 inline-flex items-center rounded-md bg-red-100 px-3 py-1.5 text-sm font-medium text-red-700 hover:bg-red-200 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500"
          >
            Back to Workflows
          </button>
        </div>
      </div>
    );
  }

  // ---- Not found state ----
  if (!workflow) {
    return (
      <div className="mx-auto max-w-3xl py-16 text-center">
        <h2 className="text-lg font-semibold text-gray-800">
          Workflow not found
        </h2>
        <p className="mt-1 text-sm text-gray-500">
          The requested workflow does not exist or has been deleted.
        </p>
        <button
          type="button"
          onClick={() => navigate('/workflows')}
          className="mt-4 inline-flex items-center rounded-md bg-gray-100 px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-200 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        >
          Back to Workflows
        </button>
      </div>
    );
  }

  // ---- Main render ----
  return (
    <div className="mx-auto max-w-5xl px-4 py-6 sm:px-6 lg:px-8">
      {/* Toast notifications */}
      <ScreenMessage messages={messages} onDismiss={dismissToast} />

      {/* Delete confirmation modal */}
      <Modal
        isVisible={showDeleteModal}
        title="Confirm Delete"
        onClose={() => setShowDeleteModal(false)}
        footer={
          <div className="flex justify-end gap-3">
            <button
              type="button"
              onClick={() => setShowDeleteModal(false)}
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleDeleteConfirm}
              disabled={deleteMutation.isPending}
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500"
            >
              {deleteMutation.isPending ? 'Deleting…' : 'Confirm Delete'}
            </button>
          </div>
        }
      >
        <div className="space-y-2">
          <p className="text-sm text-gray-700">
            Are you sure you want to delete the workflow{' '}
            <span className="font-semibold">&ldquo;{name}&rdquo;</span>?
          </p>
          <p className="text-xs text-red-600">
            This action is irreversible. All workflow definitions and
            associated schedule plans will be permanently removed.
          </p>
        </div>
      </Modal>

      {/* Breadcrumb */}
      <nav aria-label="Breadcrumb" className="mb-4">
        <ol className="flex items-center gap-1.5 text-sm text-gray-500">
          <li>
            <Link
              to="/workflows"
              className="hover:text-blue-600 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500 rounded"
            >
              Workflows
            </Link>
          </li>
          <li aria-hidden="true">
            <svg viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4 text-gray-400" aria-hidden="true">
              <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
            </svg>
          </li>
          <li>
            <Link
              to={`/workflows/${workflowId}`}
              className="hover:text-blue-600 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500 rounded"
            >
              {name || 'Workflow'}
            </Link>
          </li>
          <li aria-hidden="true">
            <svg viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4 text-gray-400" aria-hidden="true">
              <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
            </svg>
          </li>
          <li aria-current="page" className="font-medium text-gray-900">
            Edit
          </li>
        </ol>
      </nav>

      {/* Page header */}
      <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">
            Edit Workflow: {name || 'Untitled'}
          </h1>
          <p className="mt-1 text-sm text-gray-500">
            Update the workflow definition and step configuration.
          </p>
        </div>
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={() => setShowDeleteModal(true)}
            disabled={deleteMutation.isPending}
            className="rounded-md border border-red-300 bg-white px-4 py-2 text-sm font-medium text-red-600 shadow-sm hover:bg-red-50 disabled:opacity-50 disabled:cursor-not-allowed focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500"
          >
            Delete
          </button>
          <button
            type="button"
            onClick={() => navigate(`/workflows/${workflowId}`)}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          >
            Cancel
          </button>
          <button
            type="submit"
            form="workflow-manage-form"
            disabled={isUpdating}
            className="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          >
            {isUpdating ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>

      {/* Form */}
      <DynamicForm
        id="workflow-manage-form"
        name="workflow-manage-form"
        onSubmit={handleSubmit}
        showValidation={showValidation}
        validation={validation}
        className="space-y-6"
      >
        {/* ── Basic Information Card ── */}
        <section className="bg-white rounded-lg shadow p-6">
          <h2 className="text-lg font-semibold text-gray-900 mb-4">
            Basic Information
          </h2>

          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            {/* Name */}
            <div className="sm:col-span-2">
              <label
                htmlFor="wf-name"
                className="block text-sm font-medium text-gray-700 mb-1"
              >
                Workflow Name <span className="text-red-500">*</span>
              </label>
              <input
                id="wf-name"
                type="text"
                value={name}
                onChange={(e) => setName(e.target.value)}
                required
                className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 ${
                  showValidation &&
                  validation.errors.some((e) => e.propertyName === 'name')
                    ? 'border-red-300 bg-red-50'
                    : 'border-gray-300'
                }`}
                placeholder="e.g. Invoice Processing Workflow"
              />
              {showValidation &&
                validation.errors
                  .filter((e) => e.propertyName === 'name')
                  .map((e, i) => (
                    <p key={i} className="mt-1 text-xs text-red-600">
                      {e.message}
                    </p>
                  ))}
            </div>

            {/* Description */}
            <div className="sm:col-span-2">
              <label
                htmlFor="wf-description"
                className="block text-sm font-medium text-gray-700 mb-1"
              >
                Description
              </label>
              <textarea
                id="wf-description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                rows={3}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
                placeholder="Describe what this workflow does…"
              />
            </div>

            {/* Priority */}
            <div>
              <label
                htmlFor="wf-priority"
                className="block text-sm font-medium text-gray-700 mb-1"
              >
                Default Priority
              </label>
              <select
                id="wf-priority"
                value={priority}
                onChange={(e) => setPriority(parseInt(e.target.value, 10))}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                {PRIORITY_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>

            {/* Allow Single Instance */}
            <div className="flex items-center gap-3 self-end pb-1">
              <label className="relative inline-flex cursor-pointer items-center">
                <input
                  type="checkbox"
                  checked={allowSingleInstance}
                  onChange={(e) => setAllowSingleInstance(e.target.checked)}
                  className="sr-only peer"
                  role="switch"
                  aria-checked={allowSingleInstance}
                />
                <div className="h-5 w-9 rounded-full bg-gray-300 after:absolute after:left-[2px] after:top-[2px] after:h-4 after:w-4 after:rounded-full after:bg-white after:transition-transform peer-checked:bg-blue-600 peer-checked:after:translate-x-full peer-focus-visible:ring-2 peer-focus-visible:ring-blue-500" />
              </label>
              <span className="text-sm text-gray-700">
                Allow Single Instance Only
              </span>
            </div>

            {/* Status */}
            <div className="flex items-center gap-3">
              <label className="relative inline-flex cursor-pointer items-center">
                <input
                  type="checkbox"
                  checked={isActive}
                  onChange={(e) => setIsActive(e.target.checked)}
                  className="sr-only peer"
                  role="switch"
                  aria-checked={isActive}
                />
                <div className="h-5 w-9 rounded-full bg-gray-300 after:absolute after:left-[2px] after:top-[2px] after:h-4 after:w-4 after:rounded-full after:bg-white after:transition-transform peer-checked:bg-green-600 peer-checked:after:translate-x-full peer-focus-visible:ring-2 peer-focus-visible:ring-green-500" />
              </label>
              <span className="text-sm text-gray-700">
                {isActive ? 'Active' : 'Inactive'}
              </span>
            </div>
          </div>
        </section>

        {/* ── Advanced Settings (collapsible) ── */}
        <section className="bg-white rounded-lg shadow">
          <button
            type="button"
            onClick={() => setShowAdvanced((p) => !p)}
            className="flex w-full items-center justify-between px-6 py-4 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 rounded-t-lg"
          >
            <h2 className="text-lg font-semibold text-gray-900">
              Advanced Settings
            </h2>
            <svg
              viewBox="0 0 20 20"
              fill="currentColor"
              className={`h-5 w-5 text-gray-400 transition-transform ${
                showAdvanced ? 'rotate-180' : ''
              }`}
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z"
                clipRule="evenodd"
              />
            </svg>
          </button>
          {showAdvanced && (
            <div className="border-t border-gray-200 px-6 pb-6 pt-4">
              <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
                {/* Timeout */}
                <div>
                  <label
                    htmlFor="wf-timeout"
                    className="block text-sm font-medium text-gray-700 mb-1"
                  >
                    Timeout (seconds)
                  </label>
                  <input
                    id="wf-timeout"
                    type="number"
                    min={1}
                    value={timeout}
                    onChange={(e) =>
                      setTimeout_(parseInt(e.target.value, 10) || 300)
                    }
                    className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
                  />
                  <p className="mt-1 text-xs text-gray-400">
                    Maximum execution duration for the entire workflow.
                  </p>
                </div>

                {/* Start Step */}
                <div>
                  <label
                    htmlFor="wf-start-step"
                    className="block text-sm font-medium text-gray-700 mb-1"
                  >
                    Start Step
                  </label>
                  <select
                    id="wf-start-step"
                    value={startStepName}
                    onChange={(e) => setStartStepName(e.target.value)}
                    className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 ${
                      showValidation &&
                      validation.errors.some(
                        (e) => e.propertyName === 'startStep',
                      )
                        ? 'border-red-300 bg-red-50'
                        : 'border-gray-300'
                    }`}
                  >
                    <option value="">Select start step…</option>
                    {stepNames.map((sn) => (
                      <option key={sn} value={sn}>
                        {sn}
                      </option>
                    ))}
                  </select>
                  {showValidation &&
                    validation.errors
                      .filter((e) => e.propertyName === 'startStep')
                      .map((e, i) => (
                        <p key={i} className="mt-1 text-xs text-red-600">
                          {e.message}
                        </p>
                      ))}
                </div>
              </div>
            </div>
          )}
        </section>

        {/* ── Workflow Steps ── */}
        <section className="bg-white rounded-lg shadow p-6">
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-lg font-semibold text-gray-900">
              Workflow Steps
            </h2>
            <span className="text-sm text-gray-500">
              {steps.length} step{steps.length !== 1 ? 's' : ''}
            </span>
          </div>

          {/* Step-level validation errors */}
          {showValidation &&
            validation.errors.filter(
              (e) =>
                e.propertyName === 'steps' ||
                e.propertyName === 'terminal' ||
                e.propertyName === 'cycle',
            ).length > 0 && (
              <div className="mb-4 rounded-md border border-red-200 bg-red-50 p-3">
                <ul className="list-disc list-inside space-y-1">
                  {validation.errors
                    .filter(
                      (e) =>
                        e.propertyName === 'steps' ||
                        e.propertyName === 'terminal' ||
                        e.propertyName === 'cycle',
                    )
                    .map((e, i) => (
                      <li key={i} className="text-sm text-red-700">
                        {e.message}
                      </li>
                    ))}
                </ul>
              </div>
            )}

          {steps.length === 0 ? (
            <div className="flex flex-col items-center justify-center rounded-lg border-2 border-dashed border-gray-300 py-12 text-center">
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth={1.5}
                className="mb-3 h-10 w-10 text-gray-400"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M12 4.5v15m7.5-7.5h-15"
                />
              </svg>
              <p className="text-sm text-gray-500">
                No steps defined yet.
              </p>
              <button
                type="button"
                onClick={handleAddStep}
                className="mt-3 inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                <svg
                  viewBox="0 0 20 20"
                  fill="currentColor"
                  className="h-4 w-4"
                  aria-hidden="true"
                >
                  <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
                </svg>
                Add First Step
              </button>
            </div>
          ) : (
            <div className="space-y-3">
              {steps.map((step, idx) => (
                <StepPanel
                  key={step.id}
                  step={step}
                  index={idx}
                  totalSteps={steps.length}
                  isStartStep={step.name === startStepName}
                  expanded={expandedStepIds.has(step.id)}
                  stepNames={stepNames}
                  onUpdate={(updated) => handleStepUpdate(idx, updated)}
                  onRemove={() => handleRemoveStep(idx)}
                  onMoveUp={() => handleReorderStep(idx, 'up')}
                  onMoveDown={() => handleReorderStep(idx, 'down')}
                  onToggleExpand={() => handleToggleExpand(step.id)}
                  onSetStart={() => handleSetStart(step.name)}
                />
              ))}

              <button
                type="button"
                onClick={handleAddStep}
                className="mt-2 inline-flex w-full items-center justify-center gap-1.5 rounded-md border-2 border-dashed border-gray-300 py-3 text-sm font-medium text-gray-600 hover:border-blue-400 hover:text-blue-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                <svg
                  viewBox="0 0 20 20"
                  fill="currentColor"
                  className="h-4 w-4"
                  aria-hidden="true"
                >
                  <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
                </svg>
                Add Step
              </button>
            </div>
          )}
        </section>
      </DynamicForm>
    </div>
  );
}

export default WorkflowManage;
