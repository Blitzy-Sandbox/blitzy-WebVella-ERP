/**
 * WorkflowCreate.tsx — Create New Workflow Definition Page
 *
 * React page component for creating new workflow definitions (Step Functions
 * state machines). In the source monolith, job types were code-defined C#
 * classes discovered via assembly reflection in JobManager.RegisterJobTypes().
 * This page lets users define workflow steps, transitions, and conditions
 * through a visual UI, replacing the code-based approach.
 *
 * Default export for React.lazy() compatibility under /workflows/create route.
 */

import React, { useState, useCallback, useMemo } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import { useCreateWorkflow } from '../../hooks/useWorkflows';
import { useToast } from '../../components/common/ScreenMessage';
import ScreenMessage from '../../components/common/ScreenMessage';
import { ScreenMessageType } from '../../types/common';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';

/* ─────────────────────────────────────────────────────────────
   Type Definitions
   ───────────────────────────────────────────────────────────── */

/** Step Functions ASL step types */
type StepType = 'Task' | 'Choice' | 'Wait' | 'Parallel' | 'Pass' | 'Succeed' | 'Fail';

/** Terminal step types that do not require a "Next" reference */
const TERMINAL_STEP_TYPES: ReadonlySet<StepType> = new Set(['Succeed', 'Fail']);

/** Retry policy for Task steps */
interface RetryPolicy {
  errorEquals: string[];
  intervalSeconds: number;
  maxAttempts: number;
  backoffRate: number;
}

/** Catch configuration for Task steps */
interface CatchConfig {
  errorEquals: string[];
  next: string;
  resultPath?: string;
}

/** Comparison operators for Choice branches (ASL spec) */
type ComparisonOperator =
  | 'StringEquals'
  | 'StringGreaterThan'
  | 'StringLessThan'
  | 'NumericEquals'
  | 'NumericGreaterThan'
  | 'NumericLessThan'
  | 'BooleanEquals'
  | 'TimestampEquals'
  | 'IsPresent';

/** A single branch of a Choice step */
interface ChoiceBranch {
  id: string;
  variable: string;
  operator: ComparisonOperator;
  value: string;
  next: string;
}

/** A sub-branch for Parallel step */
interface ParallelBranch {
  id: string;
  name: string;
  steps: string[];
}

/** Configuration options per step type */
interface TaskConfig {
  resource: string;
  retryPolicy?: RetryPolicy;
  catchConfig?: CatchConfig;
  timeoutSeconds?: number;
  heartbeatSeconds?: number;
}

interface ChoiceConfig {
  branches: ChoiceBranch[];
  defaultNext?: string;
}

interface WaitConfig {
  seconds?: number;
  timestamp?: string;
  isDynamic: boolean;
  secondsPath?: string;
  timestampPath?: string;
}

interface ParallelConfig {
  branches: ParallelBranch[];
}

interface PassConfig {
  result?: string;
  resultPath?: string;
}

interface FailConfig {
  error: string;
  cause: string;
}

/** Union step configuration indexed by step type */
type StepConfig = {
  Task: TaskConfig;
  Choice: ChoiceConfig;
  Wait: WaitConfig;
  Parallel: ParallelConfig;
  Pass: PassConfig;
  Succeed: Record<string, never>;
  Fail: FailConfig;
};

/** A single workflow step definition */
interface WorkflowStep {
  id: string;
  name: string;
  type: StepType;
  next: string;
  config: StepConfig[StepType];
}

/** Pre-built workflow template definition */
interface WorkflowTemplate {
  id: string;
  name: string;
  description: string;
  icon: string;
  steps: WorkflowStep[];
}

/** Priority option for the dropdown */
interface PriorityOption {
  value: number;
  label: string;
}

/* ─────────────────────────────────────────────────────────────
   Constants
   ───────────────────────────────────────────────────────────── */

/**
 * Priority options mapped from the monolith's JobPriority enum
 * (JobType.cs lines 5-12): Low=1, Medium=2, High=3, Higher=4, Highest=5
 */
const PRIORITY_OPTIONS: readonly PriorityOption[] = [
  { value: 1, label: 'Low' },
  { value: 2, label: 'Medium' },
  { value: 3, label: 'High' },
  { value: 4, label: 'Higher' },
  { value: 5, label: 'Highest' },
] as const;

/** All available step types with their labels */
const STEP_TYPE_OPTIONS: readonly { value: StepType; label: string; description: string }[] = [
  { value: 'Task', label: 'Task', description: 'Execute a Lambda function or resource' },
  { value: 'Choice', label: 'Choice', description: 'Branch based on conditions' },
  { value: 'Wait', label: 'Wait', description: 'Delay execution for a duration' },
  { value: 'Parallel', label: 'Parallel', description: 'Execute branches in parallel' },
  { value: 'Pass', label: 'Pass', description: 'Pass input to output with optional transformation' },
  { value: 'Succeed', label: 'Succeed', description: 'Terminal success state' },
  { value: 'Fail', label: 'Fail', description: 'Terminal failure state' },
] as const;

/** Comparison operators for Choice step branches */
const COMPARISON_OPERATORS: readonly { value: ComparisonOperator; label: string }[] = [
  { value: 'StringEquals', label: 'String Equals' },
  { value: 'StringGreaterThan', label: 'String Greater Than' },
  { value: 'StringLessThan', label: 'String Less Than' },
  { value: 'NumericEquals', label: 'Numeric Equals' },
  { value: 'NumericGreaterThan', label: 'Numeric Greater Than' },
  { value: 'NumericLessThan', label: 'Numeric Less Than' },
  { value: 'BooleanEquals', label: 'Boolean Equals' },
  { value: 'TimestampEquals', label: 'Timestamp Equals' },
  { value: 'IsPresent', label: 'Is Present' },
] as const;

/* ─────────────────────────────────────────────────────────────
   Utility Functions
   ───────────────────────────────────────────────────────────── */

/** Generate a unique step identifier */
function generateStepId(): string {
  return `step_${Date.now()}_${Math.random().toString(36).slice(2, 9)}`;
}

/** Generate a unique branch identifier */
function generateBranchId(): string {
  return `branch_${Date.now()}_${Math.random().toString(36).slice(2, 9)}`;
}

/** Create a default step with sensible configuration for the given type */
function createDefaultStep(type: StepType, existingNames: string[]): WorkflowStep {
  const baseName: string = type;
  let name: string = baseName;
  let counter = 1;
  while (existingNames.includes(name)) {
    name = `${baseName}${counter}`;
    counter += 1;
  }

  const id = generateStepId();

  const configMap: Record<StepType, StepConfig[StepType]> = {
    Task: {
      resource: '',
      retryPolicy: undefined,
      catchConfig: undefined,
      timeoutSeconds: 60,
      heartbeatSeconds: undefined,
    } as TaskConfig,
    Choice: {
      branches: [
        {
          id: generateBranchId(),
          variable: '$.status',
          operator: 'StringEquals' as ComparisonOperator,
          value: '',
          next: '',
        },
      ],
      defaultNext: '',
    } as ChoiceConfig,
    Wait: {
      seconds: 10,
      timestamp: undefined,
      isDynamic: false,
      secondsPath: undefined,
      timestampPath: undefined,
    } as WaitConfig,
    Parallel: {
      branches: [],
    } as ParallelConfig,
    Pass: {
      result: undefined,
      resultPath: '$.result',
    } as PassConfig,
    Succeed: {} as Record<string, never>,
    Fail: {
      error: 'WorkflowError',
      cause: '',
    } as FailConfig,
  };

  return {
    id,
    name,
    type,
    next: '',
    config: configMap[type],
  };
}

/**
 * Detect cycles in step graph using DFS.
 * Returns true if a cycle is found.
 */
function detectCycles(steps: WorkflowStep[]): boolean {
  const adjacency = new Map<string, string[]>();
  for (const step of steps) {
    const nexts: string[] = [];
    if (step.next) {
      nexts.push(step.next);
    }
    if (step.type === 'Choice') {
      const choiceConf = step.config as ChoiceConfig;
      for (const branch of choiceConf.branches) {
        if (branch.next) {
          nexts.push(branch.next);
        }
      }
      if (choiceConf.defaultNext) {
        nexts.push(choiceConf.defaultNext);
      }
    }
    adjacency.set(step.name, nexts);
  }

  const WHITE = 0;
  const GRAY = 1;
  const BLACK = 2;
  const color = new Map<string, number>();
  for (const step of steps) {
    color.set(step.name, WHITE);
  }

  function dfs(node: string): boolean {
    color.set(node, GRAY);
    const neighbors = adjacency.get(node) ?? [];
    for (const neighbor of neighbors) {
      if (!color.has(neighbor)) continue;
      if (color.get(neighbor) === GRAY) return true;
      if (color.get(neighbor) === WHITE && dfs(neighbor)) return true;
    }
    color.set(node, BLACK);
    return false;
  }

  for (const step of steps) {
    if (color.get(step.name) === WHITE) {
      if (dfs(step.name)) return true;
    }
  }
  return false;
}

/* ─────────────────────────────────────────────────────────────
   Pre-built Workflow Templates
   ───────────────────────────────────────────────────────────── */

function buildTemplates(): WorkflowTemplate[] {
  return [
    {
      id: 'approval',
      name: 'Simple Approval Chain',
      description: 'Request → Review → Approve/Reject',
      icon: '✓',
      steps: [
        {
          id: generateStepId(),
          name: 'SubmitRequest',
          type: 'Task',
          next: 'ReviewRequest',
          config: {
            resource: 'arn:aws:lambda:us-east-1:000000000000:function:submit-request',
            timeoutSeconds: 60,
          } as TaskConfig,
        },
        {
          id: generateStepId(),
          name: 'ReviewRequest',
          type: 'Choice',
          next: '',
          config: {
            branches: [
              {
                id: generateBranchId(),
                variable: '$.decision',
                operator: 'StringEquals' as ComparisonOperator,
                value: 'approved',
                next: 'ApproveRequest',
              },
              {
                id: generateBranchId(),
                variable: '$.decision',
                operator: 'StringEquals' as ComparisonOperator,
                value: 'rejected',
                next: 'RejectRequest',
              },
            ],
            defaultNext: 'RejectRequest',
          } as ChoiceConfig,
        },
        {
          id: generateStepId(),
          name: 'ApproveRequest',
          type: 'Succeed',
          next: '',
          config: {} as Record<string, never>,
        },
        {
          id: generateStepId(),
          name: 'RejectRequest',
          type: 'Fail',
          next: '',
          config: {
            error: 'RequestRejected',
            cause: 'The request was rejected during review',
          } as FailConfig,
        },
      ],
    },
    {
      id: 'notification',
      name: 'Notification Pipeline',
      description: 'Validate → Send → Log',
      icon: '✉',
      steps: [
        {
          id: generateStepId(),
          name: 'ValidatePayload',
          type: 'Task',
          next: 'SendNotification',
          config: {
            resource: 'arn:aws:lambda:us-east-1:000000000000:function:validate-payload',
            timeoutSeconds: 30,
          } as TaskConfig,
        },
        {
          id: generateStepId(),
          name: 'SendNotification',
          type: 'Task',
          next: 'LogResult',
          config: {
            resource: 'arn:aws:lambda:us-east-1:000000000000:function:send-notification',
            timeoutSeconds: 120,
          } as TaskConfig,
        },
        {
          id: generateStepId(),
          name: 'LogResult',
          type: 'Task',
          next: 'Done',
          config: {
            resource: 'arn:aws:lambda:us-east-1:000000000000:function:log-result',
            timeoutSeconds: 30,
          } as TaskConfig,
        },
        {
          id: generateStepId(),
          name: 'Done',
          type: 'Succeed',
          next: '',
          config: {} as Record<string, never>,
        },
      ],
    },
    {
      id: 'etl',
      name: 'Data Processing (ETL)',
      description: 'Extract → Transform → Load',
      icon: '⚙',
      steps: [
        {
          id: generateStepId(),
          name: 'Extract',
          type: 'Task',
          next: 'Transform',
          config: {
            resource: 'arn:aws:lambda:us-east-1:000000000000:function:extract-data',
            timeoutSeconds: 300,
          } as TaskConfig,
        },
        {
          id: generateStepId(),
          name: 'Transform',
          type: 'Task',
          next: 'Load',
          config: {
            resource: 'arn:aws:lambda:us-east-1:000000000000:function:transform-data',
            timeoutSeconds: 300,
          } as TaskConfig,
        },
        {
          id: generateStepId(),
          name: 'Load',
          type: 'Task',
          next: 'Complete',
          config: {
            resource: 'arn:aws:lambda:us-east-1:000000000000:function:load-data',
            timeoutSeconds: 300,
          } as TaskConfig,
        },
        {
          id: generateStepId(),
          name: 'Complete',
          type: 'Succeed',
          next: '',
          config: {} as Record<string, never>,
        },
      ],
    },
  ];
}

/* ─────────────────────────────────────────────────────────────
   Sub-Components: Step Configuration Editors
   ───────────────────────────────────────────────────────────── */

/** Renders configuration fields for a Task step */
function TaskConfigEditor({
  config,
  onChange,
}: {
  config: TaskConfig;
  onChange: (updated: TaskConfig) => void;
}) {
  return (
    <div className="space-y-3">
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">
          Resource (Lambda ARN) <span className="text-red-500">*</span>
        </label>
        <input
          type="text"
          value={config.resource}
          onChange={(e) => onChange({ ...config, resource: e.target.value })}
          placeholder="arn:aws:lambda:us-east-1:000000000000:function:my-function"
          className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:border-blue-500"
        />
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">
            Timeout (seconds)
          </label>
          <input
            type="number"
            min={1}
            value={config.timeoutSeconds ?? ''}
            onChange={(e) =>
              onChange({
                ...config,
                timeoutSeconds: e.target.value ? parseInt(e.target.value, 10) : undefined,
              })
            }
            placeholder="60"
            className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:border-blue-500"
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">
            Heartbeat (seconds)
          </label>
          <input
            type="number"
            min={1}
            value={config.heartbeatSeconds ?? ''}
            onChange={(e) =>
              onChange({
                ...config,
                heartbeatSeconds: e.target.value ? parseInt(e.target.value, 10) : undefined,
              })
            }
            placeholder="Optional"
            className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:border-blue-500"
          />
        </div>
      </div>
    </div>
  );
}

/** Renders configuration fields for a Choice step */
function ChoiceConfigEditor({
  config,
  stepNames,
  onChange,
}: {
  config: ChoiceConfig;
  stepNames: string[];
  onChange: (updated: ChoiceConfig) => void;
}) {
  const addBranch = () => {
    onChange({
      ...config,
      branches: [
        ...config.branches,
        {
          id: generateBranchId(),
          variable: '$.status',
          operator: 'StringEquals' as ComparisonOperator,
          value: '',
          next: '',
        },
      ],
    });
  };

  const updateBranch = (idx: number, updated: ChoiceBranch) => {
    const newBranches = [...config.branches];
    newBranches[idx] = updated;
    onChange({ ...config, branches: newBranches });
  };

  const removeBranch = (idx: number) => {
    onChange({ ...config, branches: config.branches.filter((_, i) => i !== idx) });
  };

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <span className="text-sm font-medium text-gray-700">Condition Branches</span>
        <button
          type="button"
          onClick={addBranch}
          className="inline-flex items-center gap-1 rounded-md bg-blue-50 px-2.5 py-1.5 text-xs font-medium text-blue-700 hover:bg-blue-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        >
          + Add Branch
        </button>
      </div>
      {config.branches.map((branch, idx) => (
        <div
          key={branch.id}
          className="rounded-md border border-gray-200 bg-gray-50 p-3 space-y-2"
        >
          <div className="flex items-center justify-between">
            <span className="text-xs font-semibold text-gray-500">Branch {idx + 1}</span>
            <button
              type="button"
              onClick={() => removeBranch(idx)}
              className="text-xs text-red-600 hover:text-red-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500 rounded"
              aria-label={`Remove branch ${idx + 1}`}
            >
              Remove
            </button>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
            <div>
              <label className="block text-xs text-gray-600 mb-0.5">Variable</label>
              <input
                type="text"
                value={branch.variable}
                onChange={(e) => updateBranch(idx, { ...branch, variable: e.target.value })}
                placeholder="$.status"
                className="block w-full rounded-md border border-gray-300 px-2 py-1.5 text-xs shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              />
            </div>
            <div>
              <label className="block text-xs text-gray-600 mb-0.5">Operator</label>
              <select
                value={branch.operator}
                onChange={(e) =>
                  updateBranch(idx, {
                    ...branch,
                    operator: e.target.value as ComparisonOperator,
                  })
                }
                className="block w-full rounded-md border border-gray-300 px-2 py-1.5 text-xs shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                {COMPARISON_OPERATORS.map((op) => (
                  <option key={op.value} value={op.value}>
                    {op.label}
                  </option>
                ))}
              </select>
            </div>
            <div>
              <label className="block text-xs text-gray-600 mb-0.5">Value</label>
              <input
                type="text"
                value={branch.value}
                onChange={(e) => updateBranch(idx, { ...branch, value: e.target.value })}
                placeholder="Expected value"
                className="block w-full rounded-md border border-gray-300 px-2 py-1.5 text-xs shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              />
            </div>
          </div>
          <div>
            <label className="block text-xs text-gray-600 mb-0.5">Next Step</label>
            <select
              value={branch.next}
              onChange={(e) => updateBranch(idx, { ...branch, next: e.target.value })}
              className="block w-full rounded-md border border-gray-300 px-2 py-1.5 text-xs shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            >
              <option value="">— Select next step —</option>
              {stepNames.map((sn) => (
                <option key={sn} value={sn}>
                  {sn}
                </option>
              ))}
            </select>
          </div>
        </div>
      ))}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Default Next</label>
        <select
          value={config.defaultNext ?? ''}
          onChange={(e) => onChange({ ...config, defaultNext: e.target.value || undefined })}
          className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        >
          <option value="">— None —</option>
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

/** Renders configuration fields for a Wait step */
function WaitConfigEditor({
  config,
  onChange,
}: {
  config: WaitConfig;
  onChange: (updated: WaitConfig) => void;
}) {
  return (
    <div className="space-y-3">
      <div className="flex items-center gap-3">
        <label className="flex items-center gap-2 text-sm text-gray-700">
          <input
            type="checkbox"
            checked={config.isDynamic}
            onChange={(e) => onChange({ ...config, isDynamic: e.target.checked })}
            className="rounded border-gray-300 text-blue-600 focus-visible:ring-blue-500"
          />
          Dynamic (from input path)
        </label>
      </div>
      {config.isDynamic ? (
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Seconds Path</label>
            <input
              type="text"
              value={config.secondsPath ?? ''}
              onChange={(e) =>
                onChange({ ...config, secondsPath: e.target.value || undefined })
              }
              placeholder="$.waitSeconds"
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Timestamp Path</label>
            <input
              type="text"
              value={config.timestampPath ?? ''}
              onChange={(e) =>
                onChange({ ...config, timestampPath: e.target.value || undefined })
              }
              placeholder="$.waitUntil"
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            />
          </div>
        </div>
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Wait Seconds
            </label>
            <input
              type="number"
              min={1}
              value={config.seconds ?? ''}
              onChange={(e) =>
                onChange({
                  ...config,
                  seconds: e.target.value ? parseInt(e.target.value, 10) : undefined,
                })
              }
              placeholder="10"
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Timestamp</label>
            <input
              type="datetime-local"
              value={config.timestamp ?? ''}
              onChange={(e) =>
                onChange({ ...config, timestamp: e.target.value || undefined })
              }
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            />
          </div>
        </div>
      )}
    </div>
  );
}

/** Renders configuration fields for a Fail step */
function FailConfigEditor({
  config,
  onChange,
}: {
  config: FailConfig;
  onChange: (updated: FailConfig) => void;
}) {
  return (
    <div className="space-y-3">
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Error</label>
        <input
          type="text"
          value={config.error}
          onChange={(e) => onChange({ ...config, error: e.target.value })}
          placeholder="ErrorName"
          className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        />
      </div>
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Cause</label>
        <textarea
          value={config.cause}
          onChange={(e) => onChange({ ...config, cause: e.target.value })}
          placeholder="Human-readable failure cause"
          rows={2}
          className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        />
      </div>
    </div>
  );
}

/** Renders configuration fields for a Pass step */
function PassConfigEditor({
  config,
  onChange,
}: {
  config: PassConfig;
  onChange: (updated: PassConfig) => void;
}) {
  return (
    <div className="space-y-3">
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Result (JSON)</label>
        <textarea
          value={config.result ?? ''}
          onChange={(e) => onChange({ ...config, result: e.target.value || undefined })}
          placeholder='{"key": "value"}'
          rows={3}
          className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm font-mono shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        />
      </div>
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Result Path</label>
        <input
          type="text"
          value={config.resultPath ?? ''}
          onChange={(e) => onChange({ ...config, resultPath: e.target.value || undefined })}
          placeholder="$.result"
          className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        />
      </div>
    </div>
  );
}

/* ─────────────────────────────────────────────────────────────
   Step Panel (expandable per step)
   ───────────────────────────────────────────────────────────── */

function StepPanel({
  step,
  index,
  totalSteps,
  stepNames,
  expanded,
  hasError,
  onToggle,
  onUpdate,
  onRemove,
  onMoveUp,
  onMoveDown,
}: {
  step: WorkflowStep;
  index: number;
  totalSteps: number;
  stepNames: string[];
  expanded: boolean;
  hasError: boolean;
  onToggle: () => void;
  onUpdate: (updated: WorkflowStep) => void;
  onRemove: () => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
}) {
  const isTerminal = TERMINAL_STEP_TYPES.has(step.type);

  /** Names available for "next step" selection, excluding self */
  const availableNextSteps = stepNames.filter((n) => n !== step.name);

  const handleNameChange = (newName: string) => {
    onUpdate({ ...step, name: newName });
  };

  const handleTypeChange = (newType: StepType) => {
    const existingNames = stepNames;
    const defaultStep = createDefaultStep(newType, existingNames);
    onUpdate({
      ...step,
      type: newType,
      config: defaultStep.config,
      next: TERMINAL_STEP_TYPES.has(newType) ? '' : step.next,
    });
  };

  const handleNextChange = (next: string) => {
    onUpdate({ ...step, next });
  };

  const handleConfigChange = (config: StepConfig[StepType]) => {
    onUpdate({ ...step, config });
  };

  const stepTypeLabel = STEP_TYPE_OPTIONS.find((o) => o.value === step.type)?.label ?? step.type;

  return (
    <div
      className={`rounded-lg border ${
        hasError ? 'border-red-300 bg-red-50' : 'border-gray-200 bg-white'
      } shadow-sm`}
    >
      {/* Header — always visible */}
      <div className="flex items-center gap-2 px-4 py-3">
        {/* Drag handle / reorder controls */}
        <div className="flex flex-col gap-0.5">
          <button
            type="button"
            onClick={onMoveUp}
            disabled={index === 0}
            className="p-0.5 text-gray-400 hover:text-gray-600 disabled:opacity-30 disabled:cursor-not-allowed focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 rounded"
            aria-label={`Move ${step.name} up`}
          >
            <svg viewBox="0 0 20 20" fill="currentColor" className="h-3.5 w-3.5" aria-hidden="true">
              <path
                fillRule="evenodd"
                d="M14.77 12.79a.75.75 0 01-1.06-.02L10 8.832 6.29 12.77a.75.75 0 11-1.08-1.04l4.25-4.5a.75.75 0 011.08 0l4.25 4.5a.75.75 0 01-.02 1.06z"
                clipRule="evenodd"
              />
            </svg>
          </button>
          <button
            type="button"
            onClick={onMoveDown}
            disabled={index === totalSteps - 1}
            className="p-0.5 text-gray-400 hover:text-gray-600 disabled:opacity-30 disabled:cursor-not-allowed focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 rounded"
            aria-label={`Move ${step.name} down`}
          >
            <svg viewBox="0 0 20 20" fill="currentColor" className="h-3.5 w-3.5" aria-hidden="true">
              <path
                fillRule="evenodd"
                d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z"
                clipRule="evenodd"
              />
            </svg>
          </button>
        </div>

        {/* Step summary */}
        <button
          type="button"
          onClick={onToggle}
          className="flex flex-1 items-center gap-2 text-start focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 rounded"
          aria-expanded={expanded}
        >
          <span
            className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${
              isTerminal
                ? step.type === 'Succeed'
                  ? 'bg-green-100 text-green-800'
                  : 'bg-red-100 text-red-800'
                : step.type === 'Choice'
                  ? 'bg-purple-100 text-purple-800'
                  : step.type === 'Wait'
                    ? 'bg-yellow-100 text-yellow-800'
                    : step.type === 'Parallel'
                      ? 'bg-indigo-100 text-indigo-800'
                      : 'bg-blue-100 text-blue-800'
            }`}
          >
            {stepTypeLabel}
          </span>
          <span className="text-sm font-medium text-gray-900">{step.name}</span>
          {!isTerminal && step.next && (
            <span className="text-xs text-gray-400">→ {step.next}</span>
          )}
          <svg
            className={`ms-auto h-4 w-4 text-gray-400 transition-transform ${
              expanded ? 'rotate-180' : ''
            }`}
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z"
              clipRule="evenodd"
            />
          </svg>
        </button>

        {/* Remove button */}
        <button
          type="button"
          onClick={onRemove}
          className="rounded p-1 text-gray-400 hover:text-red-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500"
          aria-label={`Remove step ${step.name}`}
        >
          <svg viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
            <path
              fillRule="evenodd"
              d="M8.75 1A2.75 2.75 0 006 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 10.23 1.482l.149-.022.841 10.518A2.75 2.75 0 007.596 19h4.807a2.75 2.75 0 002.742-2.53l.841-10.52.149.023a.75.75 0 00.23-1.482A41.03 41.03 0 0014 4.193V3.75A2.75 2.75 0 0011.25 1h-2.5zM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325C8.327 4.025 9.16 4 10 4zM8.58 7.72a.75.75 0 00-1.5.06l.3 7.5a.75.75 0 101.5-.06l-.3-7.5zm4.34.06a.75.75 0 10-1.5-.06l-.3 7.5a.75.75 0 101.5.06l.3-7.5z"
              clipRule="evenodd"
            />
          </svg>
        </button>
      </div>

      {/* Expanded configuration panel */}
      {expanded && (
        <div className="border-t border-gray-200 px-4 py-4 space-y-4">
          {/* Step name & type */}
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Step Name</label>
              <input
                type="text"
                value={step.name}
                onChange={(e) => handleNameChange(e.target.value)}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:border-blue-500"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Step Type</label>
              <select
                value={step.type}
                onChange={(e) => handleTypeChange(e.target.value as StepType)}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                {STEP_TYPE_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label} — {opt.description}
                  </option>
                ))}
              </select>
            </div>
          </div>

          {/* Next step selector (non-terminal only) */}
          {!isTerminal && step.type !== 'Choice' && (
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Next Step</label>
              <select
                value={step.next}
                onChange={(e) => handleNextChange(e.target.value)}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                <option value="">— Select next step —</option>
                {availableNextSteps.map((sn) => (
                  <option key={sn} value={sn}>
                    {sn}
                  </option>
                ))}
              </select>
            </div>
          )}

          {/* Type-specific configuration */}
          {step.type === 'Task' && (
            <TaskConfigEditor
              config={step.config as TaskConfig}
              onChange={(c) => handleConfigChange(c)}
            />
          )}
          {step.type === 'Choice' && (
            <ChoiceConfigEditor
              config={step.config as ChoiceConfig}
              stepNames={availableNextSteps}
              onChange={(c) => handleConfigChange(c)}
            />
          )}
          {step.type === 'Wait' && (
            <WaitConfigEditor
              config={step.config as WaitConfig}
              onChange={(c) => handleConfigChange(c)}
            />
          )}
          {step.type === 'Fail' && (
            <FailConfigEditor
              config={step.config as FailConfig}
              onChange={(c) => handleConfigChange(c)}
            />
          )}
          {step.type === 'Pass' && (
            <PassConfigEditor
              config={step.config as PassConfig}
              onChange={(c) => handleConfigChange(c)}
            />
          )}
          {step.type === 'Succeed' && (
            <p className="text-sm text-gray-500 italic">
              No configuration needed — this is a terminal success state.
            </p>
          )}
          {step.type === 'Parallel' && (
            <p className="text-sm text-gray-500 italic">
              Parallel branch definitions can be configured via the raw ASL editor after creation.
            </p>
          )}
        </div>
      )}
    </div>
  );
}

/* ═════════════════════════════════════════════════════════════
   Main Component: WorkflowCreate
   ═════════════════════════════════════════════════════════════ */

function WorkflowCreate(): React.JSX.Element {
  const navigate = useNavigate();
  const {
    mutate,
    mutateAsync,
    isPending,
    isError,
    error: mutationError,
    isSuccess,
    data: mutationData,
  } = useCreateWorkflow();
  const { messages, showToast, dismissToast } = useToast();

  /* ── Templates (stable across renders) ── */
  const templates = useMemo<WorkflowTemplate[]>(() => buildTemplates(), []);

  /* ── Form State ── */
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [priority, setPriority] = useState<number>(1); // Default: Low (matching JobAttribute default)
  const [allowSingleInstance, setAllowSingleInstance] = useState(false);
  const [timeout, setTimeout_] = useState<number>(300); // Default: 300 seconds
  const [steps, setSteps] = useState<WorkflowStep[]>(() => {
    /* Start with default "Start → Task → Succeed" template */
    const taskStep = createDefaultStep('Task', []);
    taskStep.name = 'ProcessTask';
    const succeedStep = createDefaultStep('Succeed', ['ProcessTask']);
    succeedStep.name = 'Done';
    taskStep.next = 'Done';
    return [taskStep, succeedStep];
  });
  const [expandedSteps, setExpandedSteps] = useState<Set<string>>(() => new Set());
  const [showValidation, setShowValidation] = useState(false);
  const [selectedTemplate, setSelectedTemplate] = useState<string | null>(null);

  /* ── Derived: step names (for dropdowns) ── */
  const stepNames = useMemo(() => steps.map((s) => s.name), [steps]);

  /* ── Validation Logic (Phase 5) ── */
  const validation = useMemo<FormValidation>(() => {
    const errors: ValidationError[] = [];

    // Name is required and non-empty
    if (!name.trim()) {
      errors.push({ propertyName: 'name', message: 'Name is required.' });
    }

    // At least one step must be defined
    if (steps.length === 0) {
      errors.push({ propertyName: 'steps', message: 'At least one step must be defined.' });
    }

    // At least one terminal step (Succeed or Fail) must exist
    const hasTerminal = steps.some((s) => TERMINAL_STEP_TYPES.has(s.type));
    if (!hasTerminal) {
      errors.push({
        propertyName: 'steps',
        message: 'At least one terminal step (Succeed or Fail) must exist.',
      });
    }

    // All non-terminal, non-Choice steps must have a valid "Next" reference
    for (const step of steps) {
      if (!TERMINAL_STEP_TYPES.has(step.type) && step.type !== 'Choice') {
        if (!step.next) {
          errors.push({
            propertyName: `step_${step.name}_next`,
            message: `Step "${step.name}" must have a next step specified.`,
          });
        } else if (!stepNames.includes(step.next)) {
          errors.push({
            propertyName: `step_${step.name}_next`,
            message: `Step "${step.name}" references non-existent next step "${step.next}".`,
          });
        }
      }
    }

    // No circular step references
    if (steps.length > 0 && detectCycles(steps)) {
      errors.push({ propertyName: 'steps', message: 'Circular step references detected.' });
    }

    // Choice steps must have at least one condition branch
    for (const step of steps) {
      if (step.type === 'Choice') {
        const choiceConf = step.config as ChoiceConfig;
        if (!choiceConf.branches || choiceConf.branches.length === 0) {
          errors.push({
            propertyName: `step_${step.name}_branches`,
            message: `Choice step "${step.name}" must have at least one condition branch.`,
          });
        }
      }
    }

    // Task steps must have a resource specified
    for (const step of steps) {
      if (step.type === 'Task') {
        const taskConf = step.config as TaskConfig;
        if (!taskConf.resource || !taskConf.resource.trim()) {
          errors.push({
            propertyName: `step_${step.name}_resource`,
            message: `Task step "${step.name}" must have a resource (Lambda ARN) specified.`,
          });
        }
      }
    }

    // Timeout must be positive integer
    if (!timeout || timeout < 1 || !Number.isInteger(timeout)) {
      errors.push({ propertyName: 'timeout', message: 'Timeout must be a positive integer.' });
    }

    const message =
      errors.length > 0 ? `Please fix ${errors.length} error${errors.length > 1 ? 's' : ''} before submitting.` : undefined;

    return { message, errors };
  }, [name, steps, stepNames, timeout]);

  /* ── Identify steps with errors for highlighting ── */
  const stepsWithErrors = useMemo<Set<string>>(() => {
    const set = new Set<string>();
    for (const err of validation.errors) {
      const match = err.propertyName.match(/^step_(.+?)_/);
      if (match) {
        set.add(match[1]);
      }
    }
    return set;
  }, [validation.errors]);

  /* ── Handlers ── */

  const handleAddStep = useCallback(
    (type: StepType = 'Task') => {
      const newStep = createDefaultStep(type, stepNames);
      setSteps((prev) => [...prev, newStep]);
      setExpandedSteps((prev) => new Set(prev).add(newStep.id));
    },
    [stepNames],
  );

  const handleRemoveStep = useCallback(
    (stepId: string) => {
      const stepToRemove = steps.find((s) => s.id === stepId);
      if (!stepToRemove) return;

      // Check if other steps reference this step
      const dependents = steps.filter(
        (s) =>
          s.id !== stepId &&
          (s.next === stepToRemove.name ||
            (s.type === 'Choice' &&
              (s.config as ChoiceConfig).branches.some((b) => b.next === stepToRemove.name))),
      );

      if (dependents.length > 0) {
        const dependentNames = dependents.map((d) => d.name).join(', ');
        const confirmed = window.confirm(
          `Step "${stepToRemove.name}" is referenced by: ${dependentNames}. Removing it will clear those references. Continue?`,
        );
        if (!confirmed) return;

        // Clear references to the removed step
        setSteps((prev) =>
          prev
            .filter((s) => s.id !== stepId)
            .map((s) => {
              let updated = { ...s };
              if (updated.next === stepToRemove.name) {
                updated = { ...updated, next: '' };
              }
              if (updated.type === 'Choice') {
                const choiceConf = { ...(updated.config as ChoiceConfig) };
                choiceConf.branches = choiceConf.branches.map((b) =>
                  b.next === stepToRemove.name ? { ...b, next: '' } : b,
                );
                if (choiceConf.defaultNext === stepToRemove.name) {
                  choiceConf.defaultNext = '';
                }
                updated = { ...updated, config: choiceConf };
              }
              return updated;
            }),
        );
      } else {
        setSteps((prev) => prev.filter((s) => s.id !== stepId));
      }

      setExpandedSteps((prev) => {
        const next = new Set(prev);
        next.delete(stepId);
        return next;
      });
    },
    [steps],
  );

  const handleMoveStep = useCallback((stepId: string, direction: 'up' | 'down') => {
    setSteps((prev) => {
      const idx = prev.findIndex((s) => s.id === stepId);
      if (idx < 0) return prev;
      const targetIdx = direction === 'up' ? idx - 1 : idx + 1;
      if (targetIdx < 0 || targetIdx >= prev.length) return prev;
      const copy = [...prev];
      [copy[idx], copy[targetIdx]] = [copy[targetIdx], copy[idx]];
      return copy;
    });
  }, []);

  const handleStepUpdate = useCallback((stepId: string, updated: WorkflowStep) => {
    setSteps((prev) => prev.map((s) => (s.id === stepId ? updated : s)));
  }, []);

  const handleToggleExpand = useCallback((stepId: string) => {
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

  const handleTemplateSelect = useCallback(
    (templateId: string) => {
      const template = templates.find((t) => t.id === templateId);
      if (!template) return;

      setSelectedTemplate(templateId);
      // Deep-clone steps so each template application gets fresh IDs
      const clonedSteps: WorkflowStep[] = template.steps.map((s) => ({
        ...s,
        id: generateStepId(),
        config: JSON.parse(JSON.stringify(s.config)) as StepConfig[StepType],
      }));
      setSteps(clonedSteps);
      setExpandedSteps(new Set());
    },
    [templates],
  );

  const handleSubmit = useCallback(
    async (e: React.FormEvent) => {
      e.preventDefault();
      setShowValidation(true);

      if (validation.errors.length > 0) {
        showToast(ScreenMessageType.Error, 'Validation Failed', validation.message ?? 'Please fix all errors.');
        return;
      }

      try {
        const workflowData = {
          typeId: crypto.randomUUID(),
          attributes: {
            name: name.trim(),
            description: description.trim(),
            allowSingleInstance,
            timeout,
            steps: steps.map((s) => ({
              name: s.name,
              type: s.type,
              next: s.next || undefined,
              config: s.config,
            })),
          },
          priority,
        };

        const result = await mutateAsync(workflowData);

        showToast(ScreenMessageType.Success, 'Success', 'Workflow created successfully.');

        // Redirect to detail page. Try to extract ID from response, fall back to list.
        const responseData = result as { object?: { id?: string } };
        const newWorkflowId = responseData?.object?.id ?? workflowData.typeId;
        navigate(`/workflows/${newWorkflowId}`);
      } catch (err: unknown) {
        const errorMessage = err instanceof Error ? err.message : 'An unexpected error occurred.';
        showToast(ScreenMessageType.Error, 'Error', errorMessage);
      }
    },
    [
      name,
      description,
      priority,
      allowSingleInstance,
      timeout,
      steps,
      validation,
      mutateAsync,
      showToast,
      navigate,
    ],
  );

  /**
   * Alternative callback-style submission using mutate() — used for the
   * keyboard shortcut path where the caller does not need the promise.
   */
  const handleMutateCallback = useCallback(
    (payload: Parameters<typeof mutate>[0]) => {
      mutate(payload, {
        onSuccess: (result) => {
          showToast(ScreenMessageType.Success, 'Success', 'Workflow created successfully.');
          const responseData = result as { object?: { id?: string } };
          const newId = responseData?.object?.id ?? '';
          if (newId) {
            navigate(`/workflows/${newId}`);
          }
        },
        onError: (err) => {
          const msg = err instanceof Error ? err.message : 'An unexpected error occurred.';
          showToast(ScreenMessageType.Error, 'Error', msg);
        },
      });
    },
    [mutate, showToast, navigate],
  );

  /* ── Keyboard shortcut: Ctrl/Cmd + Enter to submit via callback ── */
  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
        e.preventDefault();
        setShowValidation(true);
        if (validation.errors.length > 0) {
          showToast(ScreenMessageType.Error, 'Validation Failed', validation.message ?? 'Please fix all errors.');
          return;
        }
        const payload = {
          typeId: crypto.randomUUID(),
          attributes: {
            name: name.trim(),
            description: description.trim(),
            allowSingleInstance,
            timeout,
            steps: steps.map((s) => ({
              name: s.name,
              type: s.type,
              next: s.next || undefined,
              config: s.config,
            })),
          },
          priority,
        };
        handleMutateCallback(payload);
      }
    },
    [validation, name, description, allowSingleInstance, timeout, steps, priority, showToast, handleMutateCallback],
  );

  /**
   * Derive server-side error message from the mutation error state.
   * Used alongside showValidation for persistent inline error display.
   */
  const serverError = useMemo<string | undefined>(() => {
    if (!isError || !mutationError) return undefined;
    return mutationError instanceof Error ? mutationError.message : 'Server error occurred.';
  }, [isError, mutationError]);

  /**
   * Track whether the mutation completed successfully (for UI guards).
   * Also references mutationData to confirm the response payload.
   */
  const hasSucceeded = isSuccess && mutationData != null;

  /* ── Priority dropdown options (memoized) ── */
  const priorityOptions = useMemo(() => PRIORITY_OPTIONS, []);

  /* ── Add step type selector state ── */
  const [addStepType, setAddStepType] = useState<StepType>('Task');

  /* ═════════════════════════════════════════════════════════════
     JSX Render
     ═════════════════════════════════════════════════════════════ */
  return (
    <div className="min-h-screen bg-gray-50" onKeyDown={handleKeyDown} role="presentation">
      {/* Toast notifications */}
      <ScreenMessage messages={messages} onDismiss={dismissToast} />

      {/* Page Header */}
      <header className="bg-white border-b border-gray-200">
        <div className="mx-auto max-w-7xl px-4 py-4 sm:px-6 lg:px-8">
          {/* Breadcrumb */}
          <nav aria-label="Breadcrumb" className="mb-2">
            <ol className="flex items-center gap-1.5 text-sm text-gray-500">
              <li>
                <Link
                  to="/workflows"
                  className="hover:text-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 rounded"
                >
                  Workflows
                </Link>
              </li>
              <li aria-hidden="true">
                <svg viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4">
                  <path
                    fillRule="evenodd"
                    d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
                    clipRule="evenodd"
                  />
                </svg>
              </li>
              <li aria-current="page" className="font-medium text-gray-900">
                Create
              </li>
            </ol>
          </nav>

          {/* Title + Actions */}
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <h1 className="text-2xl font-bold text-gray-900">Create Workflow</h1>
            <div className="flex items-center gap-3">
              <button
                type="button"
                onClick={() => navigate('/workflows')}
                className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={(e) => void handleSubmit(e as unknown as React.FormEvent)}
                disabled={isPending}
                className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
              >
                {isPending && (
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
                      d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z"
                    />
                  </svg>
                )}
                {isPending ? 'Creating…' : 'Create'}
              </button>
            </div>
          </div>
        </div>
      </header>

      {/* Main Content */}
      <main className="mx-auto max-w-7xl px-4 py-6 sm:px-6 lg:px-8 space-y-6">
        {/* Server-side mutation error banner */}
        {serverError && !hasSucceeded && (
          <div className="rounded-md bg-red-50 border border-red-200 p-4" role="alert">
            <div className="flex items-start gap-3">
              <svg
                className="h-5 w-5 text-red-400 flex-shrink-0 mt-0.5"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
                  clipRule="evenodd"
                />
              </svg>
              <p className="text-sm text-red-700">{serverError}</p>
            </div>
          </div>
        )}

        <DynamicForm
          onSubmit={handleSubmit}
          showValidation={showValidation}
          validation={validation}
          name="workflow-create-form"
        >
          {/* ── Section 1: Basic Information ── */}
          <section className="bg-white rounded-lg shadow p-6 space-y-6">
            <h2 className="text-lg font-semibold text-gray-900">Basic Information</h2>

            {/* Name — full width */}
            <div>
              <label htmlFor="workflow-name" className="block text-sm font-medium text-gray-700 mb-1">
                Name <span className="text-red-500">*</span>
              </label>
              <input
                id="workflow-name"
                type="text"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="Enter workflow name"
                required
                className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:border-blue-500 ${
                  showValidation && validation.errors.some((e) => e.propertyName === 'name')
                    ? 'border-red-300 bg-red-50'
                    : 'border-gray-300'
                }`}
              />
              {showValidation &&
                validation.errors
                  .filter((e) => e.propertyName === 'name')
                  .map((e) => (
                    <p key={e.propertyName} className="mt-1 text-xs text-red-600">
                      {e.message}
                    </p>
                  ))}
            </div>

            {/* Description — full width */}
            <div>
              <label htmlFor="workflow-description" className="block text-sm font-medium text-gray-700 mb-1">
                Description
              </label>
              <textarea
                id="workflow-description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                placeholder="Describe the workflow's purpose (optional)"
                rows={3}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:border-blue-500"
              />
            </div>

            {/* 2-column grid: Priority | Single Instance */}
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-6">
              <div>
                <label htmlFor="workflow-priority" className="block text-sm font-medium text-gray-700 mb-1">
                  Default Priority
                </label>
                <select
                  id="workflow-priority"
                  value={priority}
                  onChange={(e) => setPriority(parseInt(e.target.value, 10))}
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
                >
                  {priorityOptions.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              </div>

              <div className="flex items-end">
                <label className="flex items-center gap-2 text-sm text-gray-700 pb-2">
                  <input
                    type="checkbox"
                    checked={allowSingleInstance}
                    onChange={(e) => setAllowSingleInstance(e.target.checked)}
                    className="rounded border-gray-300 text-blue-600 focus-visible:ring-blue-500"
                  />
                  Allow Single Instance Only
                </label>
              </div>
            </div>

            {/* Timeout — half width */}
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-6">
              <div>
                <label htmlFor="workflow-timeout" className="block text-sm font-medium text-gray-700 mb-1">
                  Timeout (seconds)
                </label>
                <input
                  id="workflow-timeout"
                  type="number"
                  min={1}
                  step={1}
                  value={timeout}
                  onChange={(e) => setTimeout_(parseInt(e.target.value, 10) || 0)}
                  className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:border-blue-500 ${
                    showValidation && validation.errors.some((e) => e.propertyName === 'timeout')
                      ? 'border-red-300 bg-red-50'
                      : 'border-gray-300'
                  }`}
                />
                {showValidation &&
                  validation.errors
                    .filter((e) => e.propertyName === 'timeout')
                    .map((e) => (
                      <p key={e.propertyName} className="mt-1 text-xs text-red-600">
                        {e.message}
                      </p>
                    ))}
              </div>
            </div>
          </section>

          {/* ── Section 2: Workflow Steps ── */}
          <section className="bg-white rounded-lg shadow p-6 space-y-6">
            <div className="flex items-center justify-between">
              <h2 className="text-lg font-semibold text-gray-900">Workflow Steps</h2>
              <span className="text-sm text-gray-500">
                {steps.length} step{steps.length !== 1 ? 's' : ''}
              </span>
            </div>

            {/* Template Selector */}
            <div>
              <h3 className="text-sm font-medium text-gray-700 mb-3">Start from a Template</h3>
              <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                {templates.map((tpl) => (
                  <button
                    key={tpl.id}
                    type="button"
                    onClick={() => handleTemplateSelect(tpl.id)}
                    className={`flex flex-col items-start gap-1 rounded-lg border-2 p-4 text-start transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 ${
                      selectedTemplate === tpl.id
                        ? 'border-blue-500 bg-blue-50'
                        : 'border-gray-200 bg-white hover:border-gray-300 hover:bg-gray-50'
                    }`}
                  >
                    <span className="text-2xl" aria-hidden="true">
                      {tpl.icon}
                    </span>
                    <span className="text-sm font-semibold text-gray-900">{tpl.name}</span>
                    <span className="text-xs text-gray-500">{tpl.description}</span>
                  </button>
                ))}
              </div>
            </div>

            {/* Step validation summary */}
            {showValidation &&
              validation.errors.filter((e) => e.propertyName === 'steps').length > 0 && (
                <div className="rounded-md bg-red-50 border border-red-200 p-3">
                  <ul className="list-disc list-inside space-y-1 text-sm text-red-700">
                    {validation.errors
                      .filter((e) => e.propertyName === 'steps')
                      .map((e, i) => (
                        <li key={i}>{e.message}</li>
                      ))}
                  </ul>
                </div>
              )}

            {/* Step List */}
            <div className="space-y-3">
              {steps.map((step, idx) => (
                <StepPanel
                  key={step.id}
                  step={step}
                  index={idx}
                  totalSteps={steps.length}
                  stepNames={stepNames}
                  expanded={expandedSteps.has(step.id)}
                  hasError={showValidation && stepsWithErrors.has(step.name)}
                  onToggle={() => handleToggleExpand(step.id)}
                  onUpdate={(updated) => handleStepUpdate(step.id, updated)}
                  onRemove={() => handleRemoveStep(step.id)}
                  onMoveUp={() => handleMoveStep(step.id, 'up')}
                  onMoveDown={() => handleMoveStep(step.id, 'down')}
                />
              ))}
            </div>

            {/* Add Step */}
            <div className="flex items-center gap-3">
              <select
                value={addStepType}
                onChange={(e) => setAddStepType(e.target.value as StepType)}
                className="rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
                aria-label="Step type to add"
              >
                {STEP_TYPE_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
              <button
                type="button"
                onClick={() => handleAddStep(addStepType)}
                className="inline-flex items-center gap-1.5 rounded-md border-2 border-dashed border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-600 hover:border-blue-400 hover:text-blue-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                <svg viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4" aria-hidden="true">
                  <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
                </svg>
                Add Step
              </button>
            </div>

            {steps.length === 0 && (
              <div className="rounded-lg border-2 border-dashed border-gray-300 p-8 text-center">
                <p className="text-sm text-gray-500">
                  No steps defined. Add a step or select a template above to get started.
                </p>
              </div>
            )}
          </section>
        </DynamicForm>
      </main>
    </div>
  );
}

export default WorkflowCreate;
