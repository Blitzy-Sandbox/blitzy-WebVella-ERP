/**
 * Workflow/Job/Schedule Operations API Module
 *
 * Typed API functions for the Workflow Engine bounded-context service.
 * Replaces WebApiController.cs lines 3403–3881 (jobs, schedule plans, system log).
 * Routes to the Workflow service backed by Step Functions Local.
 *
 * Source mapping:
 *   - GetJobs (line 3419)              → getJobs
 *   - GetSchedulePlansList (line 3704) → getSchedulePlansList
 *   - GetSchedulePlan (line 3727)      → getSchedulePlan
 *   - UpdateSchedulePlan (line 3450)   → updateSchedulePlan
 *   - TriggerNowSchedulePlan (line 3672) → triggerSchedulePlan
 *   - CreateTestSchedulePlan (line 3759) → createTestSchedulePlan
 *   - GetSystemLog (line 3817)         → getSystemLog
 */

import { get, post, put } from '../client';
import type { ApiResponse } from '../client';
import type { EntityRecord } from '../../types/record';

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * Query parameters for the job listing endpoint.
 *
 * All parameters are optional. When omitted, the backend returns an
 * unfiltered, paginated list. Maps to the query-string parameters accepted
 * by `WebApiController.GetJobs` (line 3419).
 */
export interface JobListParams {
  /** ISO 8601 date string — include jobs that started on or after this date */
  startFromDate?: string;
  /** ISO 8601 date string — include jobs that started on or before this date */
  startToDate?: string;
  /** ISO 8601 date string — include jobs that finished on or after this date */
  finishedFromDate?: string;
  /** ISO 8601 date string — include jobs that finished on or before this date */
  finishedToDate?: string;
  /** Filter by job type name */
  typeName?: string;
  /** Filter by job status integer (maps to monolith JobStatus enum) */
  status?: number;
  /** Filter by job priority integer (maps to monolith JobPriority enum) */
  priority?: number;
  /** Filter by originating schedule plan GUID */
  schedulePlanId?: string;
  /** 1-based page number */
  page?: number;
  /** Number of records per page */
  pageSize?: number;
}

/**
 * Payload for creating or updating a schedule plan.
 *
 * Mirrors the validated fields from `WebApiController.UpdateSchedulePlan`
 * (line 3450). The backend performs comprehensive validation:
 *   - `type` 1 = Daily, 2 = Interval, 3 = Weekly, 4 = Monthly
 *   - `startDate` must be before `endDate` when both are provided
 *   - At least one day must be selected in `scheduleDays` for Daily/Interval types
 *   - `intervalInMinutes` must be 0–1440
 *   - `startTimespan` / `endTimespan` are HH:mm strings; backend converts to minutes
 */
export interface SchedulePlanUpdate {
  /** Plan display name */
  name: string;
  /** Schedule plan type: 1 = Daily, 2 = Interval, 3 = Weekly, 4 = Monthly */
  type: number;
  /** GUID of the job type to execute */
  jobTypeId: string;
  /** ISO 8601 date string — plan activation date */
  startDate: string;
  /** ISO 8601 date string — optional plan expiration date */
  endDate?: string;
  /** Which days of the week the plan is active */
  scheduleDays: SchedulePlanDaysOfWeek;
  /** Interval in minutes (0–1440) for Interval-type plans */
  intervalInMinutes?: number;
  /** Earliest time-of-day to trigger (HH:mm format) */
  startTimespan?: string;
  /** Latest time-of-day to trigger (HH:mm format) */
  endTimespan?: string;
  /** Whether the plan is currently enabled */
  enabled: boolean;
}

/**
 * Boolean map of which days of the week a schedule plan is active.
 *
 * Serialised as JSON in the monolith's `schedule_days` column; this
 * interface mirrors the C# `SchedulePlanDaysOfWeek` structure.
 */
export interface SchedulePlanDaysOfWeek {
  scheduledOnMonday: boolean;
  scheduledOnTuesday: boolean;
  scheduledOnWednesday: boolean;
  scheduledOnThursday: boolean;
  scheduledOnFriday: boolean;
  scheduledOnSaturday: boolean;
  scheduledOnSunday: boolean;
}

/**
 * Query parameters for the system log listing endpoint.
 *
 * Maps to `WebApiController.GetSystemLog` (line 3817) which queries the
 * `system_log` entity sorted by `created_on` descending.
 */
export interface SystemLogParams {
  /** ISO 8601 date string — include entries from this date onward */
  fromDate?: string;
  /** ISO 8601 date string — include entries up to this date */
  untilDate?: string;
  /** Filter by log entry type (exact match) */
  type?: string;
  /** Filter by source (contains match) */
  source?: string;
  /** Filter by message content (contains match) */
  message?: string;
  /** Filter by notification delivery status */
  notificationStatus?: string;
  /** 1-based page number (default: 1) */
  page?: number;
  /** Number of records per page (default: 15) */
  pageSize?: number;
}

/**
 * Read-only DTO representing a persisted schedule plan.
 *
 * Mirrors the monolith's `OutputSchedulePlan` AutoMapper mapping
 * returned by `GetSchedulePlansList` (line 3704) and
 * `GetSchedulePlan` (line 3727).
 */
export interface OutputSchedulePlan {
  /** Schedule plan unique identifier (GUID) */
  id: string;
  /** Plan display name */
  name: string;
  /** Schedule plan type: 1 = Daily, 2 = Interval, 3 = Weekly, 4 = Monthly */
  type: number;
  /** GUID of the associated job type */
  jobTypeId: string;
  /** ISO 8601 date string — activation date */
  startDate: string;
  /** ISO 8601 date string — optional expiration date */
  endDate?: string;
  /** Active days of the week */
  scheduledDays: SchedulePlanDaysOfWeek;
  /** Interval in minutes for Interval-type plans */
  intervalInMinutes?: number;
  /** Earliest trigger time stored as minutes-since-midnight */
  startTimespan?: number;
  /** Latest trigger time stored as minutes-since-midnight (1440 = end-of-day) */
  endTimespan?: number;
  /** Whether the plan is currently enabled */
  enabled: boolean;
  /** ISO 8601 date string — next scheduled trigger time */
  nextTriggerTime?: string;
  /** ISO 8601 date string — most recent trigger time */
  lastTriggerTime?: string;
  /** GUID of the user who last modified this plan */
  lastModifiedBy?: string;
  /** ISO 8601 date string — creation timestamp */
  createdOn: string;
}

// ---------------------------------------------------------------------------
// API Functions — Jobs
// ---------------------------------------------------------------------------

/**
 * Retrieve a paginated, filtered list of background jobs.
 *
 * Replaces `WebApiController.GetJobs` (line 3419) which calls
 * `JobManager.Current.GetJobs(out totalCount, ...)`.
 *
 * @param params - Optional filtering and pagination parameters.
 * @returns A list of job entity records wrapped in the standard API envelope.
 */
export async function getJobs(
  params?: JobListParams,
): Promise<ApiResponse<EntityRecord[]>> {
  return get<EntityRecord[]>(
    '/workflow/jobs',
    params as Record<string, unknown> | undefined,
  );
}

// ---------------------------------------------------------------------------
// API Functions — Schedule Plans
// ---------------------------------------------------------------------------

/**
 * Retrieve the full list of schedule plans.
 *
 * Replaces `WebApiController.GetSchedulePlansList` (line 3704) which calls
 * `ScheduleManager.Current.GetSchedulePlans()` and maps results to
 * `OutputSchedulePlan` DTOs.
 *
 * @returns All schedule plans wrapped in the standard API envelope.
 */
export async function getSchedulePlansList(): Promise<
  ApiResponse<OutputSchedulePlan[]>
> {
  return get<OutputSchedulePlan[]>('/workflow/schedule-plans/list');
}

/**
 * Retrieve a single schedule plan by its identifier.
 *
 * Replaces `WebApiController.GetSchedulePlan` (line 3727) which calls
 * `ScheduleManager.Current.GetSchedulePlan(planId)`.
 *
 * @param planId - GUID of the schedule plan to retrieve.
 * @returns The schedule plan DTO wrapped in the standard API envelope.
 */
export async function getSchedulePlan(
  planId: string,
): Promise<ApiResponse<OutputSchedulePlan>> {
  return get<OutputSchedulePlan>(`/workflow/schedule-plans/${encodeURIComponent(planId)}`);
}

/**
 * Update an existing schedule plan.
 *
 * Replaces `WebApiController.UpdateSchedulePlan` (line 3450) which
 * performs per-field validation (name, type 1–4, job_type_id, date ranges,
 * schedule_days JSON, interval 0–1440, start/end timespan, enabled flag),
 * cross-field validation (startDate < endDate, at least one active day for
 * Daily/Interval types), then calls `ScheduleManager.Current.UpdateSchedulePlan`.
 *
 * @param planId - GUID of the schedule plan to update.
 * @param plan   - Partial update payload — only supplied fields are changed.
 * @returns The updated schedule plan DTO.
 */
export async function updateSchedulePlan(
  planId: string,
  plan: Partial<SchedulePlanUpdate>,
): Promise<ApiResponse<OutputSchedulePlan>> {
  return put<OutputSchedulePlan>(
    `/workflow/schedule-plans/${encodeURIComponent(planId)}`,
    plan,
  );
}

/**
 * Immediately trigger execution of a schedule plan.
 *
 * Replaces `WebApiController.TriggerNowSchedulePlan` (line 3672) which
 * sets `NextTriggerTime = UtcNow + 1 minute` to cause the scheduler to
 * pick up the plan on its next sweep.
 *
 * @param planId - GUID of the schedule plan to trigger.
 * @returns A success/failure envelope with no typed payload.
 */
export async function triggerSchedulePlan(
  planId: string,
): Promise<ApiResponse<void>> {
  return post<void>(
    `/workflow/schedule-plans/${encodeURIComponent(planId)}/trigger`,
  );
}

/**
 * Create a test schedule plan with default parameters.
 *
 * Replaces `WebApiController.CreateTestSchedulePlan` (line 3759) which
 * creates a plan with all days enabled, Daily type, and a hardcoded
 * test JobTypeId. A new GUID is generated server-side.
 *
 * @returns The newly created test schedule plan DTO.
 */
export async function createTestSchedulePlan(): Promise<
  ApiResponse<OutputSchedulePlan>
> {
  return get<OutputSchedulePlan>('/workflow/schedule-plans/test');
}

// ---------------------------------------------------------------------------
// API Functions — System Log
// ---------------------------------------------------------------------------

/**
 * Retrieve a paginated, filtered list of system log entries.
 *
 * Replaces `WebApiController.GetSystemLog` (line 3817) which queries the
 * `system_log` entity with text-contains filters for `source` and
 * `message`, exact-match filters for `type` and `notificationStatus`,
 * date-range filters via `fromDate`/`untilDate`, and sorts results by
 * `created_on` descending.
 *
 * Defaults: page = 1, pageSize = 15.
 *
 * @param params - Optional filtering and pagination parameters.
 * @returns A list of system log entity records wrapped in the standard API envelope.
 */
export async function getSystemLog(
  params?: SystemLogParams,
): Promise<ApiResponse<EntityRecord[]>> {
  return get<EntityRecord[]>(
    '/workflow/system-log',
    params as Record<string, unknown> | undefined,
  );
}
