/**
 * useWorkflows.ts — Workflow/Job TanStack Query Hooks
 *
 * TanStack Query 5 hooks for workflow and job management. Replaces the
 * monolith's JobManager singleton operations (job type registry, job creation,
 * job querying, schedule management) and SheduleManager (schedule plan CRUD,
 * trigger, recurrence) with API calls to the Workflow microservice Lambda
 * handlers via HTTP API Gateway.
 *
 * Exports 12 hooks:
 *   Query  (5): useWorkflows, useWorkflow, useWorkflowTypes,
 *               useSchedulePlans, useSchedulePlan
 *   Mutation(7): useCreateWorkflow, useUpdateWorkflow, useCancelWorkflow,
 *                useCreateSchedulePlan, useUpdateSchedulePlan,
 *                useDeleteSchedulePlan, useTriggerSchedulePlan
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, post, put, del } from '../api/client';
import type { BaseResponseModel } from '../types/common';

// ---------------------------------------------------------------------------
// Local Type Definitions
// ---------------------------------------------------------------------------

/**
 * Workflow status enum mirroring the monolith's JobStatus values.
 * Source: JobDataService.cs column `status` (int).
 */
export enum WorkflowStatus {
  Pending = 1,
  Running = 2,
  Completed = 3,
  Aborted = 4,
  Failed = 5,
}

/**
 * Schedule plan type enum mirroring the monolith's SchedulePlanType.
 * Source: SheduleManager.cs — Interval, Daily, Weekly, Monthly.
 */
export enum SchedulePlanType {
  Interval = 1,
  Daily = 2,
  Weekly = 3,
  Monthly = 4,
}

/**
 * Workflow (Job) model — maps from the monolith's Job entity persisted via
 * JobDataService.cs (columns: id, type_id, type_name, status, priority, etc.).
 */
export interface Workflow {
  id: string;
  typeId: string;
  typeName: string;
  completeClassName: string;
  attributes: Record<string, unknown>;
  status: WorkflowStatus;
  priority: number;
  startedOn: string | null;
  finishedOn: string | null;
  abortedBy: string | null;
  canceledBy: string | null;
  errorMessage: string | null;
  result: Record<string, unknown> | null;
  schedulePlanId: string | null;
  createdOn: string;
  lastModifiedOn: string;
  createdBy: string | null;
  lastModifiedBy: string | null;
}

/**
 * Paginated workflow list response returned by GET /v1/workflows.
 */
export interface WorkflowListResponse {
  items: Workflow[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/**
 * Workflow type model — maps from the monolith's JobManager.JobTypes registry
 * populated via reflection-based assembly scanning in RegisterJobTypes().
 */
export interface WorkflowType {
  id: string;
  name: string;
  label: string;
  description: string;
  assemblyName: string;
  className: string;
  defaultPriority: number;
  allowMultipleInstances: boolean;
}

/**
 * Schedule plan model — maps from the monolith's SchedulePlan entity
 * managed by SheduleManager.cs.
 */
export interface SchedulePlan {
  id: string;
  name: string;
  type: SchedulePlanType;
  startTimespan: string | null;
  endTimespan: string | null;
  intervalInMinutes: number;
  scheduledDays: Record<string, boolean> | null;
  jobTypeId: string;
  jobTypeName: string;
  jobAttributes: Record<string, unknown> | null;
  enabled: boolean;
  startDate: string | null;
  endDate: string | null;
  lastTriggerTime: string | null;
  nextTriggerTime: string | null;
  lastStartedJobId: string | null;
  createdOn: string;
  lastModifiedOn: string;
  createdBy: string | null;
  lastModifiedBy: string | null;
}

/**
 * Paginated schedule plan list.
 */
export interface SchedulePlanListResponse {
  items: SchedulePlan[];
  totalCount: number;
}

// ---------------------------------------------------------------------------
// Query Parameter Interfaces
// ---------------------------------------------------------------------------

/**
 * Parameters for listing workflows with filters — mirrors the monolith's
 * JobManager.GetJobs(out totalCount, startFromDate, startToDate,
 * finishedFromDate, finishedToDate, typeName, status, priority,
 * schedulePlanId, page, pageSize).
 */
export interface WorkflowListParams {
  page?: number;
  pageSize?: number;
  status?: WorkflowStatus;
  typeId?: string;
  priority?: number;
  schedulePlanId?: string;
  dateFrom?: string;
  dateTo?: string;
}

/**
 * Payload for creating a new workflow/job — mirrors JobManager.CreateJob()
 * parameters: typeId, attributes, priority, creatorId, schedulePlanId.
 */
export interface CreateWorkflowPayload {
  typeId: string;
  attributes?: Record<string, unknown>;
  priority?: number;
  schedulePlanId?: string;
}

/**
 * Payload for updating an existing workflow/job — mirrors
 * JobManager.UpdateJob(job) where status, priority, and error metadata
 * can be changed.
 */
export interface UpdateWorkflowPayload {
  id: string;
  status?: WorkflowStatus;
  priority?: number;
  errorMessage?: string;
  result?: Record<string, unknown>;
  abortedBy?: string;
  canceledBy?: string;
}

/**
 * Payload for creating a schedule plan — mirrors
 * SheduleManager.CreateSchedulePlan(plan).
 */
export interface CreateSchedulePlanPayload {
  name: string;
  type: SchedulePlanType;
  startTimespan?: string;
  endTimespan?: string;
  intervalInMinutes?: number;
  scheduledDays?: Record<string, boolean>;
  jobTypeId: string;
  jobAttributes?: Record<string, unknown>;
  enabled?: boolean;
  startDate?: string;
  endDate?: string;
}

/**
 * Payload for updating a schedule plan — mirrors
 * SheduleManager.UpdateSchedulePlan(plan).
 */
export interface UpdateSchedulePlanPayload {
  id: string;
  name?: string;
  type?: SchedulePlanType;
  startTimespan?: string;
  endTimespan?: string;
  intervalInMinutes?: number;
  scheduledDays?: Record<string, boolean>;
  jobTypeId?: string;
  jobAttributes?: Record<string, unknown>;
  enabled?: boolean;
  startDate?: string;
  endDate?: string;
}

// ---------------------------------------------------------------------------
// Query Key Constants
// ---------------------------------------------------------------------------

/** Stable query key factories for consistent cache management. */
const workflowKeys = {
  all: ['workflows'] as const,
  lists: () => [...workflowKeys.all, 'list'] as const,
  list: (params?: WorkflowListParams) =>
    [...workflowKeys.lists(), params ?? {}] as const,
  details: () => [...workflowKeys.all, 'detail'] as const,
  detail: (id: string) => [...workflowKeys.details(), id] as const,
} as const;

const workflowTypeKeys = {
  all: ['workflow-types'] as const,
} as const;

const schedulePlanKeys = {
  all: ['schedule-plans'] as const,
  lists: () => [...schedulePlanKeys.all, 'list'] as const,
  list: () => [...schedulePlanKeys.lists()] as const,
  details: () => [...schedulePlanKeys.all, 'detail'] as const,
  detail: (id: string) => [...schedulePlanKeys.details(), id] as const,
} as const;

// ---------------------------------------------------------------------------
// Ten-minute staleTime constant (milliseconds) for workflow types.
// Types change very rarely, so aggressive caching is appropriate — mirrors
// the monolith's static `JobManager.JobTypes` which only updates on restart.
// ---------------------------------------------------------------------------
const TEN_MINUTES_MS = 10 * 60 * 1000;

// ---------------------------------------------------------------------------
// QUERY HOOKS (5)
// ---------------------------------------------------------------------------

/**
 * Lists workflows/jobs with pagination and optional filters.
 *
 * Replaces: JobManager.GetJobs(out totalCount, startFromDate, startToDate,
 *   finishedFromDate, finishedToDate, typeName, status, priority,
 *   schedulePlanId, page, pageSize)
 *
 * @param params - Optional filters: page, pageSize, status, typeId,
 *   priority, schedulePlanId, dateFrom, dateTo.
 */
export function useWorkflows(params?: WorkflowListParams) {
  return useQuery({
    queryKey: workflowKeys.list(params),
    queryFn: () =>
      get<WorkflowListResponse>('/workflows', params as Record<string, unknown>),
  });
}

/**
 * Retrieves a single workflow/job by its ID.
 *
 * Replaces: JobManager.GetJob(id)
 *
 * @param id - Workflow UUID. The query is disabled when id is falsy.
 */
export function useWorkflow(id: string | undefined | null) {
  return useQuery({
    queryKey: workflowKeys.detail(id ?? ''),
    queryFn: () => get<Workflow>(`/workflows/${id}`),
    enabled: Boolean(id),
  });
}

/**
 * Lists available workflow/job types.
 *
 * Replaces: JobManager.JobTypes static registry populated by reflection-based
 * assembly scanning in RegisterJobTypes().
 *
 * Uses a 10-minute staleTime because types almost never change at runtime.
 */
export function useWorkflowTypes() {
  return useQuery({
    queryKey: workflowTypeKeys.all,
    queryFn: () => get<WorkflowType[]>('/workflows/types'),
    staleTime: TEN_MINUTES_MS,
  });
}

/**
 * Lists all schedule plans.
 *
 * Replaces: SheduleManager.GetSchedulePlans()
 */
export function useSchedulePlans() {
  return useQuery({
    queryKey: schedulePlanKeys.list(),
    queryFn: () => get<SchedulePlanListResponse>('/workflows/schedules'),
  });
}

/**
 * Retrieves a single schedule plan by ID.
 *
 * Replaces: SheduleManager.GetSchedulePlan(id)
 *
 * @param id - Schedule plan UUID. The query is disabled when id is falsy.
 */
export function useSchedulePlan(id: string | undefined | null) {
  return useQuery({
    queryKey: schedulePlanKeys.detail(id ?? ''),
    queryFn: () => get<SchedulePlan>(`/workflows/schedules/${id}`),
    enabled: Boolean(id),
  });
}

// ---------------------------------------------------------------------------
// MUTATION HOOKS (7)
// ---------------------------------------------------------------------------

/**
 * Creates a new workflow/job with Pending status.
 *
 * Replaces: JobManager.CreateJob(typeId, attributes, priority, creatorId,
 *   schedulePlanId, jobId)
 *
 * Invalidates the workflow list cache on success so any listing view
 * reflects the newly created job.
 */
export function useCreateWorkflow() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (payload: CreateWorkflowPayload) =>
      post<BaseResponseModel>('/workflows', payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: workflowKeys.all });
    },
  });
}

/**
 * Updates a workflow/job's status, priority, or metadata.
 *
 * Replaces: JobManager.UpdateJob(job) which delegates to
 * JobDataService.UpdateJob().
 *
 * Invalidates both the workflow list and the specific workflow detail
 * cache entries.
 */
export function useUpdateWorkflow() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (payload: UpdateWorkflowPayload) => {
      const { id, ...body } = payload;
      return put<BaseResponseModel>(`/workflows/${id}`, body);
    },
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: workflowKeys.all });
      queryClient.invalidateQueries({
        queryKey: workflowKeys.detail(variables.id),
      });
    },
  });
}

/**
 * Cancels/aborts a running workflow.
 *
 * Replaces: Setting job status to Aborted via JobManager.UpdateJob().
 *
 * Uses POST /v1/workflows/{id}/cancel rather than a generic status update
 * because cancellation may trigger server-side cleanup (e.g., Step Functions
 * state machine abort, DLQ notification).
 */
export function useCancelWorkflow() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      post<BaseResponseModel>(`/workflows/${id}/cancel`),
    onSuccess: (_data, id) => {
      queryClient.invalidateQueries({ queryKey: workflowKeys.all });
      queryClient.invalidateQueries({
        queryKey: workflowKeys.detail(id),
      });
    },
  });
}

/**
 * Creates a new schedule plan.
 *
 * Replaces: SheduleManager.CreateSchedulePlan(plan) which auto-generates
 * an ID and calculates NextTriggerTime from the plan type and interval.
 */
export function useCreateSchedulePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (payload: CreateSchedulePlanPayload) =>
      post<BaseResponseModel>('/workflows/schedules', payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: schedulePlanKeys.all });
    },
  });
}

/**
 * Updates an existing schedule plan's configuration.
 *
 * Replaces: SheduleManager.UpdateSchedulePlan(plan).
 */
export function useUpdateSchedulePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (payload: UpdateSchedulePlanPayload) => {
      const { id, ...body } = payload;
      return put<BaseResponseModel>(`/workflows/schedules/${id}`, body);
    },
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: schedulePlanKeys.all });
      queryClient.invalidateQueries({
        queryKey: schedulePlanKeys.detail(variables.id),
      });
    },
  });
}

/**
 * Deletes a schedule plan.
 *
 * Replaces: SheduleManager.DeleteSchedulePlan(id) which removes the plan
 * from the schedule_plans table.
 */
export function useDeleteSchedulePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      del<BaseResponseModel>(`/workflows/schedules/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: schedulePlanKeys.all });
    },
  });
}

/**
 * Manually triggers a schedule plan for immediate execution.
 *
 * Replaces: SheduleManager.TriggerNowSchedulePlan(id) which sets
 * NextTriggerTime to DateTime.UtcNow + 1 minute to force the schedule
 * processor to pick it up on its next cycle.
 *
 * In the serverless target architecture, the trigger endpoint invokes the
 * Workflow service Lambda directly, which starts a Step Functions execution
 * or creates a pending job entry.
 *
 * Invalidates workflow list cache because triggering a plan creates a
 * new workflow/job instance.
 */
export function useTriggerSchedulePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      post<BaseResponseModel>(`/workflows/schedules/${id}/trigger`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: workflowKeys.all });
      queryClient.invalidateQueries({ queryKey: schedulePlanKeys.all });
    },
  });
}
