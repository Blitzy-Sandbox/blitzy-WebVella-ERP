/**
 * Project Management API Endpoints Module
 *
 * Typed API functions for the Inventory (Project Management) bounded-context
 * service — tasks, timelogs, comments, and task workflows.
 *
 * Replaces the monolith's `ProjectController.cs` endpoints
 * (`api/v3.0/p/project/*`) by routing through API Gateway to the Inventory
 * Lambda handlers.
 *
 * Source mapping (ProjectController.cs → this module):
 *   - POST pc-post-list/create    → createComment
 *   - POST pc-post-list/delete    → deleteComment
 *   - POST pc-timelog-list/create  → createTimelog
 *   - POST pc-timelog-list/delete  → deleteTimelog
 *   - POST timelog/start           → startTaskTimelog
 *   - POST task/status             → changeTaskStatus
 *   - POST task/watch              → toggleTaskWatch
 *   - GET  user/get-current        → getProjectCurrentUser
 *
 * Additional CRUD endpoints for the Inventory service:
 *   - GET/POST    /inventory/tasks          → listTasks / createTask
 *   - GET/PUT/DEL /inventory/tasks/:id      → getTask / updateTask / deleteTask
 *   - GET         /inventory/timelogs       → listTimelogs
 *   - GET         /inventory/comments       → listComments
 *
 * Route prefix mapping (AAP §0.5.1):
 *   /inventory/tasks/*      — task operations
 *   /inventory/timelogs/*   — timelog operations
 *   /inventory/comments/*   — comment operations
 *   /inventory/projects/*   — project-level operations
 *
 * @module api/endpoints/projects
 */

import { get, post, put, del } from '../client';
import type { ApiResponse } from '../client';
import type { EntityRecord } from '../../types/record';

// ---------------------------------------------------------------------------
// Parameter Interfaces
// ---------------------------------------------------------------------------

/**
 * Parameters for creating a new comment (post) on a project record.
 *
 * Mirrors the monolith's `ProjectController.CreateNewPcPostListItem` which
 * extracts `relatedRecordId` (required GUID), optional `parentId`, `subject`,
 * `body`, and `relatedRecords` from the incoming `EntityRecord` body.
 *
 * After creation via `CommentService.Create`, the server re-queries the record
 * with EQL to include `$user_1n_comment.image` and `$user_1n_comment.username`
 * in the response — that enrichment is now handled by the Inventory Lambda.
 */
export interface CreateCommentParams {
  /** Required GUID of the record this comment is related to (e.g. a task). */
  relatedRecordId: string;
  /** Optional GUID of a parent comment for nested/threaded replies. */
  parentId?: string;
  /** Optional subject line for the comment. */
  subject?: string;
  /** Comment body text (required). */
  body: string;
  /** Optional array of related record GUIDs (serialised to JSON by the server). */
  relatedRecords?: string[];
}

/**
 * Parameters for creating a new timelog entry.
 *
 * Mirrors the monolith's `ProjectController.CreateTimelog` which parses
 * `minutes` (int), `isBillable` (bool), `loggedOn` (DateTime), `body`,
 * and `relatedRecords` from the incoming `EntityRecord` body.
 *
 * The server creates via `TimeLogService.Create` and re-queries with EQL to
 * include `$user_1n_timelog.image` and `$user_1n_timelog.username`.
 */
export interface CreateTimelogParams {
  /** Required GUID of the record this timelog is related to (e.g. a task). */
  relatedRecordId: string;
  /** Duration in whole minutes. */
  minutes: number;
  /** Whether the time is billable (maps to `is_billable` field). */
  isBillable: boolean;
  /** ISO 8601 datetime string for when the work was performed. */
  loggedOn: string;
  /** Optional description/note for the timelog entry. */
  body?: string;
  /** Optional array of related record GUIDs. */
  relatedRecords?: string[];
}

/**
 * Query parameters for listing tasks with filtering and pagination.
 *
 * Maps to the Inventory service's task listing endpoint which replaces the
 * monolith's EQL-based task queries from `TaskService.cs` (queue building
 * with due-date windows, status exclusion, ordering, and paging).
 */
export interface ProjectListParams {
  /** Free-text search against task subject, key, and description. */
  search?: string;
  /** 1-based page number for pagination. */
  page?: number;
  /** Number of records per page (server default applies if omitted). */
  pageSize?: number;
  /** Field name to sort by (e.g. 'created_on', 'priority', 'due_date'). */
  sortField?: string;
  /** Sort direction — ascending or descending. */
  sortType?: 'asc' | 'desc';
  /** Filter tasks by status identifier (GUID string). */
  status?: string;
  /** Filter tasks assigned to a specific user (GUID string). */
  assignedTo?: string;
}

/**
 * Query parameters for listing timelogs with optional filtering.
 *
 * Mirrors the monolith's `TimeLogService.GetTimelogsForPeriod` which supports
 * optional `projectId`, `userId`, and date range filters, plus the
 * `l_related_records CONTAINS` pattern for project-scoped timelogs.
 */
export interface TimelogListParams {
  /** Filter timelogs by related record GUID (e.g. a task or project). */
  relatedRecordId?: string;
  /** 1-based page number for pagination. */
  page?: number;
  /** Number of records per page. */
  pageSize?: number;
  /** ISO 8601 date string for the start of the date range filter. */
  fromDate?: string;
  /** ISO 8601 date string for the end of the date range filter. */
  toDate?: string;
}

// ---------------------------------------------------------------------------
// Internal Helpers
// ---------------------------------------------------------------------------

/**
 * Strips `undefined` values from a params object so that only explicitly
 * set query parameters are sent to the API. This prevents the Axios client
 * from serialising `key=undefined` into the URL query string.
 *
 * @param params - The raw parameters object (may contain undefined values)
 * @returns A cleaned object with only defined key-value pairs
 */
function buildQueryParams(
  params: Record<string, unknown>,
): Record<string, unknown> {
  const cleaned: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null) {
      cleaned[key] = value;
    }
  }
  return cleaned;
}

// ---------------------------------------------------------------------------
// Comment Endpoints
// ---------------------------------------------------------------------------

/**
 * Creates a new comment on a project-related record.
 *
 * Replaces: POST `api/v3.0/p/project/pc-post-list/create`
 * Source: `ProjectController.CreateNewPcPostListItem`
 *
 * The monolith validates `relatedRecordId` (required GUID), optionally parses
 * `parentId`, extracts `subject`/`body`, and deserialises `relatedRecords`
 * from JSON. After `CommentService.Create`, it re-queries via EQL to include
 * `$user_1n_comment.image` and `$user_1n_comment.username` in the response.
 *
 * @param params - Comment creation parameters
 * @returns API response containing the created comment with user relation data
 */
export async function createComment(
  params: CreateCommentParams,
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>('/inventory/comments', {
    relatedRecordId: params.relatedRecordId,
    parentId: params.parentId,
    subject: params.subject,
    body: params.body,
    relatedRecords: params.relatedRecords,
  });
}

/**
 * Deletes a comment by its record ID.
 *
 * Replaces: POST `api/v3.0/p/project/pc-post-list/delete`
 * Source: `ProjectController.DeletePcPostListItem`
 *
 * The monolith's `CommentService.Delete` enforces author-only deletion
 * (checks `created_by` against `SecurityContext.CurrentUser.Id`) and also
 * deletes one level of child replies. This validation is now handled by
 * the Inventory Lambda.
 *
 * @param commentId - GUID of the comment to delete
 * @returns API response confirming deletion
 */
export async function deleteComment(
  commentId: string,
): Promise<ApiResponse<void>> {
  return del<void>(`/inventory/comments/${encodeURIComponent(commentId)}`);
}

/**
 * Lists comments for a specific related record (e.g. all comments on a task).
 *
 * Additional CRUD function for the Inventory service. Replaces the EQL-based
 * comment queries from the monolith that load comments with user relation
 * fields (`$user_1n_comment.image`, `$user_1n_comment.username`).
 *
 * @param relatedRecordId - GUID of the parent record to list comments for
 * @returns API response containing an array of comment records
 */
export async function listComments(
  relatedRecordId: string,
): Promise<ApiResponse<EntityRecord[]>> {
  return get<EntityRecord[]>(
    '/inventory/comments',
    buildQueryParams({ relatedRecordId }),
  );
}

// ---------------------------------------------------------------------------
// Timelog Endpoints
// ---------------------------------------------------------------------------

/**
 * Creates a new timelog entry.
 *
 * Replaces: POST `api/v3.0/p/project/pc-timelog-list/create`
 * Source: `ProjectController.CreateTimelog`
 *
 * The monolith parses `minutes` (int), `isBillable` (bool), `loggedOn`
 * (DateTime), converts `loggedOn` to UTC via `ConvertAppDateToUtc()`, and
 * creates via `TimeLogService.Create`. It re-queries with EQL to include
 * `$user_1n_timelog.image` and `$user_1n_timelog.username`.
 *
 * @param params - Timelog creation parameters
 * @returns API response containing the created timelog with user relation data
 */
export async function createTimelog(
  params: CreateTimelogParams,
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>('/inventory/timelogs', {
    relatedRecordId: params.relatedRecordId,
    minutes: params.minutes,
    isBillable: params.isBillable,
    loggedOn: params.loggedOn,
    body: params.body,
    relatedRecords: params.relatedRecords,
  });
}

/**
 * Deletes a timelog entry by its record ID.
 *
 * Replaces: POST `api/v3.0/p/project/pc-timelog-list/delete`
 * Source: `ProjectController.DeleteTimelog`
 *
 * The monolith's `TimeLogService.Delete` enforces author-only deletion
 * (checks `created_by` against `SecurityContext.CurrentUser.Id`). This
 * validation is now handled server-side by the Inventory Lambda.
 *
 * @param timelogId - GUID of the timelog to delete
 * @returns API response confirming deletion
 */
export async function deleteTimelog(
  timelogId: string,
): Promise<ApiResponse<void>> {
  return del<void>(`/inventory/timelogs/${encodeURIComponent(timelogId)}`);
}

/**
 * Lists timelog entries with optional filtering by related record and date range.
 *
 * Additional CRUD function for the Inventory service. Replaces the monolith's
 * `TimeLogService.GetTimelogsForPeriod` which supports optional `projectId`,
 * `userId`, and date range filters with EQL `CONTAINS` on `l_related_records`.
 *
 * @param params - Optional filtering and pagination parameters
 * @returns API response containing an array of timelog records
 */
export async function listTimelogs(
  params?: TimelogListParams,
): Promise<ApiResponse<EntityRecord[]>> {
  const queryParams = params
    ? buildQueryParams({
        relatedRecordId: params.relatedRecordId,
        page: params.page,
        pageSize: params.pageSize,
        fromDate: params.fromDate,
        toDate: params.toDate,
      })
    : undefined;

  return get<EntityRecord[]>('/inventory/timelogs', queryParams);
}

// ---------------------------------------------------------------------------
// Task Workflow Endpoints
// ---------------------------------------------------------------------------

/**
 * Starts a timelog timer on a specific task.
 *
 * Replaces: POST `api/v3.0/p/project/timelog/start`
 * Source: `ProjectController.StartTimeLog`
 *
 * The monolith validates that the task exists via `TaskService.GetTask` and
 * checks that `timelog_started_on` is not already set before calling
 * `TaskService.StartTaskTimelog`. Returns a failure response (not exception)
 * if the task is not found or already has an active timer.
 *
 * @param taskId - GUID of the task to start timing
 * @returns API response indicating success or failure with message
 */
export async function startTaskTimelog(
  taskId: string,
): Promise<ApiResponse<void>> {
  return post<void>(
    `/inventory/tasks/${encodeURIComponent(taskId)}/timelog/start`,
  );
}

/**
 * Changes the status of a task.
 *
 * Replaces: POST `api/v3.0/p/project/task/status`
 * Source: `ProjectController.TaskSetStatus`
 *
 * The monolith validates task existence, checks that the current `status_id`
 * differs from the requested `statusId` to prevent redundant updates, then
 * calls `TaskService.SetStatus`. Returns a failure response (not exception)
 * if the task is not found or the status is already set.
 *
 * @param taskId - GUID of the task to update
 * @param status - GUID string of the new status to set
 * @returns API response indicating success or failure with message
 */
export async function changeTaskStatus(
  taskId: string,
  status: string,
): Promise<ApiResponse<void>> {
  return post<void>(
    `/inventory/tasks/${encodeURIComponent(taskId)}/status`,
    { statusId: status },
  );
}

/**
 * Toggles task watch (subscribe/unsubscribe) for a user.
 *
 * Replaces: POST `api/v3.0/p/project/task/watch`
 * Source: `ProjectController.TaskSetWatch`
 *
 * The monolith validates task existence, validates optional userId (falls back
 * to `SecurityContext.CurrentUser.Id`), then creates or removes a
 * `user_nn_task_watchers` many-to-many relation record via
 * `RecordManager.CreateRelationManyToManyRecord` /
 * `RecordManager.RemoveRelationManyToManyRecord`.
 *
 * @param taskId - GUID of the task to watch/unwatch
 * @param watch  - `true` to start watching, `false` to stop watching
 * @param userId - Optional GUID of the user; defaults to current user server-side
 * @returns API response indicating success or failure with message
 */
export async function toggleTaskWatch(
  taskId: string,
  watch: boolean,
  userId?: string,
): Promise<ApiResponse<void>> {
  return post<void>(
    `/inventory/tasks/${encodeURIComponent(taskId)}/watch`,
    buildQueryParams({ startWatch: watch, userId }),
  );
}

/**
 * Retrieves the current authenticated user's project profile.
 *
 * Replaces: GET `api/v3.0/p/project/user/get-current`
 * Source: `ProjectController.GetCurrentUser`
 *
 * The monolith queries `SELECT * FROM user WHERE id = @currentUserId` using
 * `RecordManager.Find` with the authenticated user's claim. The Inventory
 * Lambda extracts the user identity from the JWT claims in the request context.
 *
 * @returns API response containing the current user's entity record
 */
export async function getProjectCurrentUser(): Promise<
  ApiResponse<EntityRecord>
> {
  return get<EntityRecord>('/inventory/projects/current-user');
}

// ---------------------------------------------------------------------------
// Task CRUD Endpoints
// ---------------------------------------------------------------------------

/**
 * Lists tasks with optional filtering, sorting, and pagination.
 *
 * Additional CRUD function for the Inventory service. Replaces the monolith's
 * EQL-based task queue builder in `TaskService.cs` which constructs dynamic
 * EQL with due-date windows, closed-status exclusion, ordering, and paging.
 *
 * @param params - Optional filtering and pagination parameters
 * @returns API response containing an array of task records
 */
export async function listTasks(
  params?: ProjectListParams,
): Promise<ApiResponse<EntityRecord[]>> {
  const queryParams = params
    ? buildQueryParams({
        search: params.search,
        page: params.page,
        pageSize: params.pageSize,
        sortField: params.sortField,
        sortType: params.sortType,
        status: params.status,
        assignedTo: params.assignedTo,
      })
    : undefined;

  return get<EntityRecord[]>('/inventory/tasks', queryParams);
}

/**
 * Retrieves a single task by its ID.
 *
 * Replaces the monolith's `TaskService.GetTask` which executes
 * `SELECT * FROM task WHERE id = @taskId` via EQL.
 *
 * @param taskId - GUID of the task to retrieve
 * @returns API response containing the task entity record
 */
export async function getTask(
  taskId: string,
): Promise<ApiResponse<EntityRecord>> {
  return get<EntityRecord>(
    `/inventory/tasks/${encodeURIComponent(taskId)}`,
  );
}

/**
 * Creates a new task record.
 *
 * Additional CRUD function for the Inventory service. The task body uses the
 * generic `EntityRecord` format with dynamic fields such as `subject`,
 * `status_id`, `priority`, `assigned_to`, `timelog_started_on`, etc.
 *
 * The Inventory Lambda handles the monolith's post-create logic from
 * `TaskService` (key calculation from project abbreviation + task number,
 * watcher relation seeding, activity feed creation).
 *
 * @param task - Entity record containing task field values
 * @returns API response containing the created task record
 */
export async function createTask(
  task: EntityRecord,
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>('/inventory/tasks', task);
}

/**
 * Updates an existing task record by ID.
 *
 * Additional CRUD function for the Inventory service. Replaces direct
 * `RecordManager.UpdateRecord` invocations. The Inventory Lambda handles
 * post-update recalculation (key regeneration, status change side-effects,
 * activity feed creation) from `TaskService`.
 *
 * @param taskId - GUID of the task to update
 * @param task   - Entity record containing the updated field values
 * @returns API response containing the updated task record
 */
export async function updateTask(
  taskId: string,
  task: EntityRecord,
): Promise<ApiResponse<EntityRecord>> {
  return put<EntityRecord>(
    `/inventory/tasks/${encodeURIComponent(taskId)}`,
    task,
  );
}

/**
 * Deletes a task record by its ID.
 *
 * Additional CRUD function for the Inventory service. Replaces direct
 * `RecordManager.DeleteRecord("task", taskId)` invocations. The Inventory
 * Lambda publishes a domain event (`inventory.task.deleted`) via SNS for
 * cross-service consumers (e.g. Reporting service read-model update).
 *
 * @param taskId - GUID of the task to delete
 * @returns API response confirming deletion
 */
export async function deleteTask(
  taskId: string,
): Promise<ApiResponse<void>> {
  return del<void>(`/inventory/tasks/${encodeURIComponent(taskId)}`);
}
