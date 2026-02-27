/**
 * TaskCreate — Task Creation Form Page
 *
 * React 19 page component for creating new tasks in the Project Management
 * domain. Replaces the monolith's task creation workflow driven by:
 *
 *  - `TaskService.cs` — pre-create hooks that validate project assignment,
 *    auto-set the task number, compute the key (`{project_abbr}-{number}`),
 *    seed `x_search`, and add creator to `user_nn_task_watchers`
 *  - `ProjectController.cs` — `POST api/v3.0/p/project/task/create` endpoint
 *
 * In the target microservices architecture, server-side logic (number
 * generation, key computation, search indexing, watcher assignment) is handled
 * by the Inventory service Lambda handler. The frontend collects the 10
 * user-editable fields and submits them via `useCreateTask()`.
 *
 * Form fields (10):
 *  1. Subject     — text input  (required)
 *  2. Description — textarea
 *  3. Priority    — select: Low (1) / Normal (2) / High (3), default Normal
 *  4. Status      — select populated from `task_status` entity
 *  5. Type        — select populated from `task_type` entity
 *  6. Owner       — select populated from user list
 *  7. Project     — multi-select for M2M association (required, ≥1)
 *  8. Start Time  — datetime-local picker
 *  9. End Time    — datetime-local picker (due date)
 * 10. Estimated   — number input (minutes)
 *
 * @module pages/projects/TaskCreate
 */

import { useState, useCallback, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { useCreateTask } from '../../hooks/useProjects';
import { useRecords } from '../../hooks/useRecords';
import { useUsers } from '../../hooks/useUsers';

// ---------------------------------------------------------------------------
// Local Types
// ---------------------------------------------------------------------------

/** Shape of the controlled form state for all 10 task fields. */
interface TaskFormState {
  /** Task title / subject — required */
  subject: string;
  /** Rich-text or plain-text description */
  description: string;
  /** Priority level: "1" = Low, "2" = Normal, "3" = High */
  priority: string;
  /** FK to task_status entity record (GUID string) */
  status_id: string;
  /** FK to task_type entity record (GUID string) */
  type_id: string;
  /** FK to user / owner (GUID string) — nullable */
  owner_id: string;
  /** Planned start datetime (ISO / datetime-local value) */
  start_time: string;
  /** Due date datetime (ISO / datetime-local value) */
  end_time: string;
  /** Estimated effort in minutes (stored as string for controlled input) */
  estimated_minutes: string;
  /** Selected project IDs for M2M `$project_nn_task` relation — required ≥1 */
  projectIds: string[];
}

/** Field-level validation errors keyed by field name. */
type ValidationErrors = Record<string, string>;

/** Priority option descriptor for the priority select dropdown. */
interface PriorityOption {
  value: string;
  label: string;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default initial form state. Priority defaults to "2" (Normal). */
const INITIAL_FORM_STATE: TaskFormState = {
  subject: '',
  description: '',
  priority: '2',
  status_id: '',
  type_id: '',
  owner_id: '',
  start_time: '',
  end_time: '',
  estimated_minutes: '',
  projectIds: [],
};

/** Static priority options matching the monolith's task priority enum. */
const PRIORITY_OPTIONS: PriorityOption[] = [
  { value: '1', label: 'Low' },
  { value: '2', label: 'Normal' },
  { value: '3', label: 'High' },
];

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Task creation page with a 10-field form.
 *
 * Default-exported for `React.lazy()` code-splitting on the
 * `/projects/tasks/new` route.
 */
function TaskCreate(): React.JSX.Element {
  const navigate = useNavigate();

  // ── Mutations & Queries ────────────────────────────────────────────────
  const {
    mutateAsync,
    isPending,
    isError: isMutationError,
    error: mutationError,
    isSuccess: isMutationSuccess,
    data: createdTaskData,
    reset: resetMutation,
  } = useCreateTask();

  const { data: statusData, isLoading: isStatusLoading } =
    useRecords('task_status');
  const { data: typeData, isLoading: isTypeLoading } =
    useRecords('task_type');
  const { data: projectData, isLoading: isProjectLoading } =
    useRecords('project');
  const { data: usersData, isLoading: isUsersLoading } = useUsers();

  // ── Form State ─────────────────────────────────────────────────────────
  const [form, setForm] = useState<TaskFormState>(INITIAL_FORM_STATE);
  const [validationErrors, setValidationErrors] = useState<ValidationErrors>(
    {},
  );

  // ── Derived Data ───────────────────────────────────────────────────────
  const statuses = statusData?.data ?? [];
  const types = typeData?.data ?? [];
  const projects = projectData?.data ?? [];
  const users = usersData?.object ?? [];

  const isDropdownsLoading =
    isStatusLoading || isTypeLoading || isProjectLoading || isUsersLoading;

  // ── Handlers ───────────────────────────────────────────────────────────

  /**
   * Generic handler for simple text / select / datetime fields.
   * Clears the field-level validation error on change and resets mutation
   * error state so the user sees a clean slate.
   */
  const handleFieldChange = useCallback(
    (field: keyof Omit<TaskFormState, 'projectIds'>, value: string) => {
      setForm((prev) => ({ ...prev, [field]: value }));
      setValidationErrors((prev) => {
        if (!prev[field]) return prev;
        const next = { ...prev };
        delete next[field];
        return next;
      });
      resetMutation();
    },
    [resetMutation],
  );

  /**
   * Toggles a project ID in the multi-select list.
   * Adds if not present, removes if already selected.
   */
  const handleProjectToggle = useCallback(
    (projectId: string) => {
      setForm((prev) => {
        const exists = prev.projectIds.includes(projectId);
        return {
          ...prev,
          projectIds: exists
            ? prev.projectIds.filter((id) => id !== projectId)
            : [...prev.projectIds, projectId],
        };
      });
      setValidationErrors((prev) => {
        if (!prev.projectIds) return prev;
        const next = { ...prev };
        delete next.projectIds;
        return next;
      });
      resetMutation();
    },
    [resetMutation],
  );

  /**
   * Validates form state and returns field-level errors.
   * Returns an empty object when all validations pass.
   */
  const validateForm = useCallback((): ValidationErrors => {
    const errors: ValidationErrors = {};

    if (!form.subject.trim()) {
      errors.subject = 'Subject is required.';
    }

    if (form.projectIds.length === 0) {
      errors.projectIds = 'At least one project must be selected.';
    }

    if (
      form.estimated_minutes !== '' &&
      (isNaN(Number(form.estimated_minutes)) ||
        Number(form.estimated_minutes) < 0)
    ) {
      errors.estimated_minutes =
        'Estimated minutes must be a non-negative number.';
    }

    if (form.start_time && form.end_time && form.start_time > form.end_time) {
      errors.end_time = 'End time must be after start time.';
    }

    return errors;
  }, [form]);

  /**
   * Form submission handler.
   * Validates, builds the EntityRecord payload (including the M2M
   * `$project_nn_task` relation key), calls the mutation, and navigates
   * to the new task's detail page on success.
   */
  const handleSubmit = useCallback(
    async (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();

      const errors = validateForm();
      if (Object.keys(errors).length > 0) {
        setValidationErrors(errors);
        return;
      }

      // Build the EntityRecord payload
      const taskId = crypto.randomUUID();
      const payload: Record<string, unknown> = {
        id: taskId,
        subject: form.subject.trim(),
        description: form.description,
        priority: form.priority,
        estimated_minutes:
          form.estimated_minutes !== ''
            ? Number(form.estimated_minutes)
            : null,
        start_time: form.start_time || null,
        end_time: form.end_time || null,
        // M2M relation key for project ↔ task association
        $project_nn_task: form.projectIds,
      };

      // Only include optional FK fields when a value is selected
      if (form.status_id) {
        payload.status_id = form.status_id;
      }
      if (form.type_id) {
        payload.type_id = form.type_id;
      }
      if (form.owner_id) {
        payload.owner_id = form.owner_id;
      }

      try {
        const created = await mutateAsync(payload);
        // Prefer the ID from the direct return, fall back to cached mutation
        // data (`createdTaskData`) from TanStack Query, then to the local ID.
        const newId =
          (created as Record<string, unknown>)?.id ??
          createdTaskData?.id ??
          taskId;
        navigate(`/projects/tasks/${String(newId)}`);
      } catch {
        // Mutation error is captured by useCreateTask's `error` state.
        // Scroll to top so the error banner is visible.
        window.scrollTo({ top: 0, behavior: 'smooth' });
      }
    },
    [form, validateForm, mutateAsync, navigate, createdTaskData],
  );

  /** Navigate back to the task list on cancel. */
  const handleCancel = useCallback(() => {
    navigate('/projects/tasks');
  }, [navigate]);

  // ── Render ─────────────────────────────────────────────────────────────
  return (
    <main className="mx-auto max-w-4xl px-4 py-8 sm:px-6 lg:px-8">
      {/* ── Page Header ─────────────────────────────────────────────── */}
      <div className="mb-6">
        <h1 className="text-2xl font-semibold text-gray-900">
          Create New Task
        </h1>
        <p className="mt-1 text-sm text-gray-500">
          Fill in the details below to create a new task. Fields marked with
          <span className="text-red-600"> *</span> are required.
        </p>
      </div>

      {/* ── Server Error Banner ─────────────────────────────────────── */}
      {isMutationError && mutationError && (
        <div
          role="alert"
          className="mb-6 rounded-md border border-red-300 bg-red-50 p-4"
        >
          <div className="flex">
            <svg
              className="h-5 w-5 shrink-0 text-red-400"
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
            <div className="ms-3">
              <h3 className="text-sm font-medium text-red-800">
                Task creation failed
              </h3>
              <p className="mt-1 text-sm text-red-700">
                {mutationError.message || 'An unexpected error occurred.'}
              </p>
            </div>
          </div>
        </div>
      )}

      {/* ── Form ────────────────────────────────────────────────────── */}
      <form
        onSubmit={(e) => {
          void handleSubmit(e);
        }}
        noValidate
        className="rounded-lg border border-gray-200 bg-white shadow-sm"
      >
        <div className="space-y-6 p-6 sm:p-8">
          {/* ─── Subject (required) ─────────────────────────────────── */}
          <div>
            <label
              htmlFor="task-subject"
              className="block text-sm font-medium text-gray-700"
            >
              Subject <span className="text-red-600">*</span>
            </label>
            <input
              id="task-subject"
              type="text"
              required
              value={form.subject}
              onChange={(e) => handleFieldChange('subject', e.target.value)}
              placeholder="Enter task subject"
              disabled={isPending}
              className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm
                focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500
                disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500
                ${
                  validationErrors.subject
                    ? 'border-red-300 text-red-900 placeholder-red-300'
                    : 'border-gray-300 text-gray-900 placeholder-gray-400'
                }`}
              aria-invalid={!!validationErrors.subject}
              aria-describedby={
                validationErrors.subject ? 'subject-error' : undefined
              }
            />
            {validationErrors.subject && (
              <p id="subject-error" className="mt-1 text-sm text-red-600">
                {validationErrors.subject}
              </p>
            )}
          </div>

          {/* ─── Description ────────────────────────────────────────── */}
          <div>
            <label
              htmlFor="task-description"
              className="block text-sm font-medium text-gray-700"
            >
              Description
            </label>
            <textarea
              id="task-description"
              rows={4}
              value={form.description}
              onChange={(e) =>
                handleFieldChange('description', e.target.value)
              }
              placeholder="Describe the task in detail"
              disabled={isPending}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm
                text-gray-900 shadow-sm placeholder-gray-400
                focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500
                disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500"
            />
          </div>

          {/* ─── Two-Column Grid: Priority + Status ─────────────────── */}
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            {/* Priority */}
            <div>
              <label
                htmlFor="task-priority"
                className="block text-sm font-medium text-gray-700"
              >
                Priority
              </label>
              <select
                id="task-priority"
                value={form.priority}
                onChange={(e) =>
                  handleFieldChange('priority', e.target.value)
                }
                disabled={isPending}
                className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2
                  text-sm text-gray-900 shadow-sm
                  focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500
                  disabled:cursor-not-allowed disabled:bg-gray-50"
              >
                {PRIORITY_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>

            {/* Status */}
            <div>
              <label
                htmlFor="task-status"
                className="block text-sm font-medium text-gray-700"
              >
                Status
              </label>
              <select
                id="task-status"
                value={form.status_id}
                onChange={(e) =>
                  handleFieldChange('status_id', e.target.value)
                }
                disabled={isPending || isStatusLoading}
                className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2
                  text-sm text-gray-900 shadow-sm
                  focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500
                  disabled:cursor-not-allowed disabled:bg-gray-50"
              >
                <option value="">
                  {isStatusLoading ? 'Loading…' : '— Select status —'}
                </option>
                {statuses.map((s) => (
                  <option key={String(s.id)} value={String(s.id)}>
                    {String(s.label ?? s.name ?? s.id)}
                  </option>
                ))}
              </select>
            </div>
          </div>

          {/* ─── Two-Column Grid: Type + Owner ──────────────────────── */}
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            {/* Type */}
            <div>
              <label
                htmlFor="task-type"
                className="block text-sm font-medium text-gray-700"
              >
                Type
              </label>
              <select
                id="task-type"
                value={form.type_id}
                onChange={(e) =>
                  handleFieldChange('type_id', e.target.value)
                }
                disabled={isPending || isTypeLoading}
                className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2
                  text-sm text-gray-900 shadow-sm
                  focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500
                  disabled:cursor-not-allowed disabled:bg-gray-50"
              >
                <option value="">
                  {isTypeLoading ? 'Loading…' : '— Select type —'}
                </option>
                {types.map((t) => (
                  <option key={String(t.id)} value={String(t.id)}>
                    {String(t.label ?? t.name ?? t.id)}
                  </option>
                ))}
              </select>
            </div>

            {/* Owner */}
            <div>
              <label
                htmlFor="task-owner"
                className="block text-sm font-medium text-gray-700"
              >
                Owner
              </label>
              <select
                id="task-owner"
                value={form.owner_id}
                onChange={(e) =>
                  handleFieldChange('owner_id', e.target.value)
                }
                disabled={isPending || isUsersLoading}
                className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2
                  text-sm text-gray-900 shadow-sm
                  focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500
                  disabled:cursor-not-allowed disabled:bg-gray-50"
              >
                <option value="">
                  {isUsersLoading ? 'Loading…' : '— Select owner —'}
                </option>
                {users.map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.firstName && u.lastName
                      ? `${u.firstName} ${u.lastName}`
                      : u.username}
                  </option>
                ))}
              </select>
            </div>
          </div>

          {/* ─── Project Association (required, multi-select) ───────── */}
          <fieldset>
            <legend className="block text-sm font-medium text-gray-700">
              Projects <span className="text-red-600">*</span>
            </legend>
            <p className="mt-1 text-xs text-gray-500">
              Select at least one project to associate with this task.
            </p>

            {isProjectLoading ? (
              <p className="mt-2 text-sm text-gray-500">
                Loading projects…
              </p>
            ) : projects.length === 0 ? (
              <p className="mt-2 text-sm text-gray-500">
                No projects available. Create a project first.
              </p>
            ) : (
              <div
                className="mt-2 max-h-48 space-y-2 overflow-y-auto rounded-md border border-gray-200 p-3"
                role="group"
                aria-label="Project selection"
              >
                {projects.map((proj) => {
                  const projId = String(proj.id);
                  const isChecked = form.projectIds.includes(projId);
                  const checkboxId = `project-${projId}`;
                  return (
                    <label
                      key={projId}
                      htmlFor={checkboxId}
                      className="flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5
                        transition-colors hover:bg-gray-50"
                    >
                      <input
                        id={checkboxId}
                        type="checkbox"
                        checked={isChecked}
                        onChange={() => handleProjectToggle(projId)}
                        disabled={isPending}
                        className="h-4 w-4 rounded border-gray-300 text-indigo-600
                          focus-visible:ring-2 focus-visible:ring-indigo-500
                          disabled:cursor-not-allowed"
                      />
                      <span className="text-sm text-gray-700">
                        {String(
                          proj.name ?? proj.abbr ?? proj.label ?? proj.id,
                        )}
                      </span>
                    </label>
                  );
                })}
              </div>
            )}

            {validationErrors.projectIds && (
              <p className="mt-1 text-sm text-red-600">
                {validationErrors.projectIds}
              </p>
            )}
          </fieldset>

          {/* ─── Two-Column Grid: Start Time + End Time ─────────────── */}
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            {/* Start Time */}
            <div>
              <label
                htmlFor="task-start-time"
                className="block text-sm font-medium text-gray-700"
              >
                Start Time
              </label>
              <input
                id="task-start-time"
                type="datetime-local"
                value={form.start_time}
                onChange={(e) =>
                  handleFieldChange('start_time', e.target.value)
                }
                disabled={isPending}
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm
                  text-gray-900 shadow-sm
                  focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500
                  disabled:cursor-not-allowed disabled:bg-gray-50"
              />
            </div>

            {/* End Time (Due Date) */}
            <div>
              <label
                htmlFor="task-end-time"
                className="block text-sm font-medium text-gray-700"
              >
                End Time (Due Date)
              </label>
              <input
                id="task-end-time"
                type="datetime-local"
                value={form.end_time}
                onChange={(e) =>
                  handleFieldChange('end_time', e.target.value)
                }
                disabled={isPending}
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm
                  focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500
                  disabled:cursor-not-allowed disabled:bg-gray-50
                  ${
                    validationErrors.end_time
                      ? 'border-red-300 text-red-900'
                      : 'border-gray-300 text-gray-900'
                  }`}
                aria-invalid={!!validationErrors.end_time}
                aria-describedby={
                  validationErrors.end_time ? 'end-time-error' : undefined
                }
              />
              {validationErrors.end_time && (
                <p
                  id="end-time-error"
                  className="mt-1 text-sm text-red-600"
                >
                  {validationErrors.end_time}
                </p>
              )}
            </div>
          </div>

          {/* ─── Estimated Minutes ──────────────────────────────────── */}
          <div className="max-w-xs">
            <label
              htmlFor="task-estimated-minutes"
              className="block text-sm font-medium text-gray-700"
            >
              Estimated Minutes
            </label>
            <input
              id="task-estimated-minutes"
              type="number"
              min="0"
              step="1"
              value={form.estimated_minutes}
              onChange={(e) =>
                handleFieldChange('estimated_minutes', e.target.value)
              }
              placeholder="e.g. 120"
              disabled={isPending}
              className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm
                focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500
                disabled:cursor-not-allowed disabled:bg-gray-50
                ${
                  validationErrors.estimated_minutes
                    ? 'border-red-300 text-red-900 placeholder-red-300'
                    : 'border-gray-300 text-gray-900 placeholder-gray-400'
                }`}
              aria-invalid={!!validationErrors.estimated_minutes}
              aria-describedby={
                validationErrors.estimated_minutes
                  ? 'estimated-minutes-error'
                  : undefined
              }
            />
            {validationErrors.estimated_minutes && (
              <p
                id="estimated-minutes-error"
                className="mt-1 text-sm text-red-600"
              >
                {validationErrors.estimated_minutes}
              </p>
            )}
          </div>
        </div>

        {/* ─── Form Actions ───────────────────────────────────────────── */}
        <div className="flex items-center justify-end gap-3 border-t border-gray-200 px-6 py-4 sm:px-8">
          <button
            type="button"
            onClick={handleCancel}
            disabled={isPending}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium
              text-gray-700 shadow-sm
              hover:bg-gray-50
              focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 focus-visible:ring-offset-2
              disabled:cursor-not-allowed disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="submit"
            disabled={isPending || isDropdownsLoading}
            className="inline-flex items-center gap-2 rounded-md border border-transparent
              bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm
              hover:bg-indigo-700
              focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 focus-visible:ring-offset-2
              disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isPending && (
              <svg
                className="h-4 w-4 animate-spin text-white"
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
            )}
            {isPending
              ? 'Creating…'
              : isMutationSuccess
                ? 'Created!'
                : 'Create Task'}
          </button>
        </div>
      </form>
    </main>
  );
}

export default TaskCreate;
