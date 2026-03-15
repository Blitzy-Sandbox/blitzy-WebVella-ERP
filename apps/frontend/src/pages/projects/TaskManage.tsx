/**
 * TaskManage Page Component — `apps/frontend/src/pages/projects/TaskManage.tsx`
 *
 * React page for editing an existing task with full recurrence template support.
 * Replaces the monolith's `RecordManage` Razor Page for task entities and the
 * `PcTaskRepeatRecurrenceSet` ViewComponent that provided the recurrence editor.
 *
 * Route: `/projects/tasks/:taskId/edit`
 *
 * Behavioral parity with the monolith (AAP §0.8.1):
 *  - RecordManage page model pre-fill    → useTask() + useEffect syncing to state
 *  - PcTaskRepeatRecurrenceSet.cs         → Inline RecurrenceTemplate editor
 *  - RecordManager.UpdateRecord()         → useUpdateTask() mutation
 *  - RecurrenceTemplate C# class          → TypeScript RecurrenceTemplate interface
 *  - recurrence_template JSON string      → Parsed/serialized RecurrenceTemplate
 *  - Bootstrap 4 form layout              → Tailwind CSS 4.x form utilities
 *  - jQuery form interactions             → React controlled inputs with useState
 *
 * Critical rules enforced:
 *  - ZERO jQuery — React controlled form inputs
 *  - ZERO Bootstrap — Tailwind CSS only
 *  - Full RecurrenceTemplate structure preserved from monolith
 *  - JSON serialization for recurrence_template field
 *  - Conditional visibility for weekly days, end-type sub-fields
 *  - Lazy-loaded via default export
 */

import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useTask, useUpdateTask } from '../../hooks/useProjects';
import type { EntityRecord } from '../../types/record';

// ---------------------------------------------------------------------------
// RecurrenceTemplate — TypeScript equivalent of C# RecurrenceTemplate
// (WebVella.Erp.Recurrence namespace)
// ---------------------------------------------------------------------------

/**
 * Mirrors the C# `RecurrenceTemplate` class from `WebVella.Erp.Recurrence`.
 *
 * Stored as a JSON string in the task entity's `recurrence_template` field.
 * Parsed on load and serialized back to JSON on save.
 */
interface RecurrenceTemplate {
  /** Recurrence pattern type: none, daily, weekly, monthly, yearly */
  type: 'none' | 'daily' | 'weekly' | 'monthly' | 'yearly';
  /** Repeat every N periods (days/weeks/months/years) */
  interval: number;
  /** How the recurrence ends: never, after N occurrences, or on a specific date */
  endType: 'never' | 'afterOccurrences' | 'onDate';
  /** Number of occurrences before ending (used when endType === 'afterOccurrences') */
  endAfterOccurrences: number;
  /** End date ISO string (used when endType === 'onDate'), or null */
  endDate: string | null;
  /** Weekly day selection booleans — visible only when type === 'weekly' */
  repeatOnMon: boolean;
  repeatOnTue: boolean;
  repeatOnWed: boolean;
  repeatOnThu: boolean;
  repeatOnFri: boolean;
  repeatOnSat: boolean;
  repeatOnSun: boolean;
  /** Whether completed occurrences regenerate new ones */
  allowRegeneration: boolean;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default RecurrenceTemplate matching the C# default constructor. */
const DEFAULT_RECURRENCE: RecurrenceTemplate = {
  type: 'none',
  interval: 1,
  endType: 'never',
  endAfterOccurrences: 1,
  endDate: null,
  repeatOnMon: false,
  repeatOnTue: false,
  repeatOnWed: false,
  repeatOnThu: false,
  repeatOnFri: false,
  repeatOnSat: false,
  repeatOnSun: false,
  allowRegeneration: false,
};

/** Priority options matching E2E test label values. */
const PRIORITY_OPTIONS = [
  { value: 'low', label: 'low' },
  { value: 'medium', label: 'medium' },
  { value: 'high', label: 'high' },
  { value: 'urgent', label: 'urgent' },
] as const;

/** Status options matching inventory Lambda string values and E2E test labels. */
const STATUS_OPTIONS = [
  { value: 'not started', label: 'not started' },
  { value: 'in progress', label: 'in progress' },
  { value: 'completed', label: 'completed' },
  { value: 'on hold', label: 'on hold' },
] as const;

/** Type options matching inventory Lambda string values and E2E test labels. */
const TYPE_OPTIONS = [
  { value: 'bug', label: 'bug' },
  { value: 'feature', label: 'feature' },
  { value: 'task', label: 'task' },
  { value: 'improvement', label: 'improvement' },
] as const;

/** Recurrence type options matching C# RecurrenceType enum. */
const RECURRENCE_TYPE_OPTIONS = [
  { value: 'none', label: 'None' },
  { value: 'daily', label: 'Daily' },
  { value: 'weekly', label: 'Weekly' },
  { value: 'monthly', label: 'Monthly' },
  { value: 'yearly', label: 'Yearly' },
] as const;

/** Recurrence end-type options matching C# RecurrenceEndType enum. */
const RECURRENCE_END_TYPE_OPTIONS = [
  { value: 'never', label: 'Never' },
  { value: 'afterOccurrences', label: 'After N occurrences' },
  { value: 'onDate', label: 'On specific date' },
] as const;

/** Weekday checkboxes matching C# RepeatOn* booleans. */
const WEEKDAY_OPTIONS = [
  { key: 'repeatOnMon' as const, label: 'Mon' },
  { key: 'repeatOnTue' as const, label: 'Tue' },
  { key: 'repeatOnWed' as const, label: 'Wed' },
  { key: 'repeatOnThu' as const, label: 'Thu' },
  { key: 'repeatOnFri' as const, label: 'Fri' },
  { key: 'repeatOnSat' as const, label: 'Sat' },
  { key: 'repeatOnSun' as const, label: 'Sun' },
] as const;

/**
 * Period label for the interval field, dynamically derived from recurrence type.
 */
function getPeriodLabel(type: RecurrenceTemplate['type']): string {
  switch (type) {
    case 'daily':
      return 'day(s)';
    case 'weekly':
      return 'week(s)';
    case 'monthly':
      return 'month(s)';
    case 'yearly':
      return 'year(s)';
    default:
      return '';
  }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Safely parse a JSON string into a RecurrenceTemplate.
 * Returns the default template when the value is falsy or unparseable.
 */
function parseRecurrenceTemplate(value: unknown): RecurrenceTemplate {
  if (typeof value !== 'string' || value.trim().length === 0) {
    return { ...DEFAULT_RECURRENCE };
  }
  try {
    const parsed = JSON.parse(value) as Partial<RecurrenceTemplate>;
    return {
      type: parsed.type ?? DEFAULT_RECURRENCE.type,
      interval:
        typeof parsed.interval === 'number' && parsed.interval >= 1
          ? parsed.interval
          : DEFAULT_RECURRENCE.interval,
      endType: parsed.endType ?? DEFAULT_RECURRENCE.endType,
      endAfterOccurrences:
        typeof parsed.endAfterOccurrences === 'number' && parsed.endAfterOccurrences >= 1
          ? parsed.endAfterOccurrences
          : DEFAULT_RECURRENCE.endAfterOccurrences,
      endDate:
        typeof parsed.endDate === 'string' && parsed.endDate.length > 0
          ? parsed.endDate
          : DEFAULT_RECURRENCE.endDate,
      repeatOnMon: parsed.repeatOnMon === true,
      repeatOnTue: parsed.repeatOnTue === true,
      repeatOnWed: parsed.repeatOnWed === true,
      repeatOnThu: parsed.repeatOnThu === true,
      repeatOnFri: parsed.repeatOnFri === true,
      repeatOnSat: parsed.repeatOnSat === true,
      repeatOnSun: parsed.repeatOnSun === true,
      allowRegeneration: parsed.allowRegeneration === true,
    };
  } catch {
    return { ...DEFAULT_RECURRENCE };
  }
}

/**
 * Coerce an unknown entity field value to a string.
 * Returns `fallback` when the value is null, undefined, or not a string.
 */
function toStr(value: unknown, fallback = ''): string {
  if (typeof value === 'string') return value;
  if (typeof value === 'number') return String(value);
  return fallback;
}

/**
 * Convert an entity field value to a date-input-compatible string (YYYY-MM-DD)
 * or datetime-local string (YYYY-MM-DDTHH:mm).
 */
function toDateTimeLocal(value: unknown): string {
  if (!value) return '';
  const str = typeof value === 'string' ? value : String(value);
  try {
    const d = new Date(str);
    if (Number.isNaN(d.getTime())) return '';
    // Format as YYYY-MM-DDTHH:mm for datetime-local input
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  } catch {
    return '';
  }
}

/**
 * Convert an entity field value to a date-input-compatible string (YYYY-MM-DD).
 */
function toDateStr(value: unknown): string {
  if (!value) return '';
  const str = typeof value === 'string' ? value : String(value);
  try {
    const d = new Date(str);
    if (Number.isNaN(d.getTime())) return '';
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  } catch {
    return '';
  }
}

// ---------------------------------------------------------------------------
// Shared Tailwind class tokens
// ---------------------------------------------------------------------------

const inputClasses =
  'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 focus-visible:border-indigo-500 disabled:cursor-not-allowed disabled:bg-gray-100 disabled:text-gray-500';

const labelClasses = 'block text-sm font-medium text-gray-700 mb-1';

const selectClasses =
  'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 focus-visible:border-indigo-500 disabled:cursor-not-allowed disabled:bg-gray-100';

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * TaskManage — Edit an existing task including recurrence template.
 *
 * Route: `/projects/tasks/:taskId/edit`
 *
 * Loads the task via `useTask(taskId)`, pre-fills all form fields including
 * the recurrence editor, and submits updates via `useUpdateTask()`.
 */
export default function TaskManage(): React.JSX.Element {
  // ── Routing hooks ───────────────────────────────────────────────────────
  const { taskId } = useParams<{ taskId: string }>();
  const navigate = useNavigate();

  // Fallback to empty string when route param is missing (should never happen
  // if router config is correct, but guards against undefined access).
  const resolvedTaskId = taskId ?? '';

  // ── Data fetching ───────────────────────────────────────────────────────
  const {
    data: taskData,
    isLoading: isTaskLoading,
    isError: isTaskError,
    error: taskError,
  } = useTask(resolvedTaskId);

  const updateTask = useUpdateTask();

  // ── Form state: task fields ─────────────────────────────────────────────
  const [subject, setSubject] = useState('');
  const [description, setDescription] = useState('');
  const [priority, setPriority] = useState('medium');
  const [statusId, setStatusId] = useState('');
  const [typeId, setTypeId] = useState('');
  const [ownerId, setOwnerId] = useState('');
  const [projectId, setProjectId] = useState('');
  const [startTime, setStartTime] = useState('');
  const [endTime, setEndTime] = useState('');
  const [estimatedMinutes, setEstimatedMinutes] = useState('');

  // ── Form state: recurrence template ─────────────────────────────────────
  const [recurrence, setRecurrence] = useState<RecurrenceTemplate>({
    ...DEFAULT_RECURRENCE,
  });

  // ── Form state: UI control ──────────────────────────────────────────────
  const [isRecurrenceOpen, setIsRecurrenceOpen] = useState(false);
  const [validationError, setValidationError] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);

  // ── Synchronize loaded task data into form state ────────────────────────
  // Fires when the TanStack Query data arrives or changes.
  useEffect(() => {
    if (!taskData) return;

    const record = taskData as EntityRecord;

    setSubject(toStr(record['subject']));
    setDescription(toStr(record['description']));
    setPriority(toStr(record['priority'], 'medium'));
    setStatusId(toStr(record['status_id']));
    setTypeId(toStr(record['type_id']));
    setOwnerId(toStr(record['owner_id']));

    // Project may be a relation field stored as string or array
    const proj = record['project_id'] ?? record['$project_nn_task'];
    if (Array.isArray(proj) && proj.length > 0) {
      setProjectId(toStr(proj[0]));
    } else {
      setProjectId(toStr(proj));
    }

    setStartTime(toDateTimeLocal(record['start_time']));
    setEndTime(toDateTimeLocal(record['end_time']));
    setEstimatedMinutes(
      record['estimated_minutes'] != null
        ? String(record['estimated_minutes'])
        : '',
    );

    // Parse recurrence_template JSON string into RecurrenceTemplate object
    const tpl = parseRecurrenceTemplate(record['recurrence_template']);
    setRecurrence(tpl);

    // Auto-expand the recurrence section if the task already has a pattern
    if (tpl.type !== 'none') {
      setIsRecurrenceOpen(true);
    }
  }, [taskData]);

  // ── Recurrence updater helper ───────────────────────────────────────────
  function updateRecurrence<K extends keyof RecurrenceTemplate>(
    key: K,
    value: RecurrenceTemplate[K],
  ): void {
    setRecurrence((prev) => ({ ...prev, [key]: value }));
  }

  // ── Form submission ─────────────────────────────────────────────────────
  async function handleSubmit(e: React.FormEvent<HTMLFormElement>): Promise<void> {
    e.preventDefault();
    setValidationError(null);
    setSubmitError(null);

    // Client-side validation: subject is required
    const trimmedSubject = subject.trim();
    if (trimmedSubject.length === 0) {
      setValidationError('Subject is required.');
      return;
    }

    // Build the update payload as an EntityRecord
    const payload: EntityRecord = {
      subject: trimmedSubject,
      description,
      priority,
      status_id: statusId || undefined,
      type_id: typeId || undefined,
      owner_id: ownerId || undefined,
      project_id: projectId || undefined,
      start_time: startTime ? new Date(startTime).toISOString() : null,
      end_time: endTime ? new Date(endTime).toISOString() : null,
      estimated_minutes:
        estimatedMinutes !== '' ? Number(estimatedMinutes) : null,
      // Serialize recurrence template back to JSON string (matching monolith)
      recurrence_template: JSON.stringify(recurrence),
    };

    try {
      await updateTask.mutateAsync({ id: resolvedTaskId, data: payload });
      // Navigate to task detail view on success
      navigate(`/projects/tasks/${resolvedTaskId}`);
    } catch (err: unknown) {
      const message =
        err instanceof Error ? err.message : 'An unexpected error occurred.';
      setSubmitError(message);
    }
  }

  // ── Cancel handler ──────────────────────────────────────────────────────
  function handleCancel(): void {
    navigate(`/projects/tasks/${resolvedTaskId}`);
  }

  // ── Loading state ───────────────────────────────────────────────────────
  if (isTaskLoading) {
    return (
      <div className="flex items-center justify-center min-h-[320px]">
        <div className="flex flex-col items-center gap-3">
          <div
            className="h-8 w-8 animate-spin rounded-full border-4 border-indigo-200 border-t-indigo-600"
            role="status"
            aria-label="Loading task data"
          />
          <p className="text-sm text-gray-500">Loading task…</p>
        </div>
      </div>
    );
  }

  // ── Error state ─────────────────────────────────────────────────────────
  if (isTaskError) {
    return (
      <div className="mx-auto max-w-3xl px-4 py-8">
        <div
          className="rounded-md border border-red-300 bg-red-50 p-4"
          role="alert"
        >
          <h2 className="text-base font-semibold text-red-800">
            Failed to load task
          </h2>
          <p className="mt-1 text-sm text-red-700">
            {taskError?.message ?? 'An unexpected error occurred while loading the task.'}
          </p>
          <button
            type="button"
            onClick={handleCancel}
            className="mt-3 inline-flex items-center rounded-md bg-red-100 px-3 py-1.5 text-sm font-medium text-red-800 hover:bg-red-200 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500"
          >
            Back to task
          </button>
        </div>
      </div>
    );
  }

  // ── Derived values ──────────────────────────────────────────────────────
  const isSubmitting = updateTask.isPending;
  const showRecurrenceFields = recurrence.type !== 'none';
  const showWeeklyDays = recurrence.type === 'weekly';
  const showEndOccurrences = recurrence.endType === 'afterOccurrences';
  const showEndDate = recurrence.endType === 'onDate';
  const periodLabel = getPeriodLabel(recurrence.type);

  // ── Render ──────────────────────────────────────────────────────────────
  return (
    <div className="mx-auto max-w-3xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ── Page header ──────────────────────────────────────────────── */}
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Edit Task</h1>
        <p className="mt-1 text-sm text-gray-500">
          Update the task fields below and save your changes.
        </p>
      </div>

      {/* ── Validation / submit errors ───────────────────────────────── */}
      {validationError && (
        <div
          className="mb-4 rounded-md border border-yellow-300 bg-yellow-50 p-3 text-sm text-yellow-800"
          role="alert"
        >
          {validationError}
        </div>
      )}
      {submitError && (
        <div
          className="mb-4 rounded-md border border-red-300 bg-red-50 p-3 text-sm text-red-700"
          role="alert"
        >
          {submitError}
        </div>
      )}

      <form onSubmit={handleSubmit} noValidate>
        {/* ═══════════════════════════════════════════════════════════════
            Section 1 — Core Task Fields
            ═══════════════════════════════════════════════════════════════ */}
        <fieldset
          className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm sm:p-6"
          disabled={isSubmitting}
        >
          <legend className="sr-only">Task details</legend>

          {/* Subject (required) */}
          <div className="mb-4">
            <label htmlFor="task-subject" className={labelClasses}>
              Subject <span className="text-red-500" aria-hidden="true">*</span>
            </label>
            <input
              id="task-subject"
              type="text"
              required
              autoComplete="off"
              placeholder="Enter task subject"
              value={subject}
              onChange={(e) => setSubject(e.target.value)}
              className={inputClasses}
            />
          </div>

          {/* Description */}
          <div className="mb-4">
            <label htmlFor="task-description" className={labelClasses}>
              Description
            </label>
            <textarea
              id="task-description"
              rows={4}
              placeholder="Enter task description"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              className={inputClasses}
            />
          </div>

          {/* Two-column row: Priority + Status */}
          <div className="mb-4 grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <label htmlFor="task-priority" className={labelClasses}>
                Priority
              </label>
              <select
                id="task-priority"
                value={priority}
                onChange={(e) => setPriority(e.target.value)}
                className={selectClasses}
              >
                {PRIORITY_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>

            <div>
              <label htmlFor="task-status" className={labelClasses}>
                Status
              </label>
              <select
                id="task-status"
                name="status"
                value={statusId}
                onChange={(e) => setStatusId(e.target.value)}
                className={selectClasses}
              >
                <option value="">— Select status —</option>
                {STATUS_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>
          </div>

          {/* Two-column row: Type + Owner */}
          <div className="mb-4 grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <label htmlFor="task-type" className={labelClasses}>
                Type
              </label>
              <select
                id="task-type"
                name="type"
                value={typeId}
                onChange={(e) => setTypeId(e.target.value)}
                className={selectClasses}
              >
                <option value="">— Select type —</option>
                {TYPE_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>

            <div>
              <label htmlFor="task-owner" className={labelClasses}>
                Owner
              </label>
              <input
                id="task-owner"
                type="text"
                placeholder="Owner ID"
                value={ownerId}
                onChange={(e) => setOwnerId(e.target.value)}
                className={inputClasses}
              />
            </div>
          </div>

          {/* Project */}
          <div className="mb-4">
            <label htmlFor="task-project" className={labelClasses}>
              Project
            </label>
            <input
              id="task-project"
              type="text"
              placeholder="Project ID"
              value={projectId}
              onChange={(e) => setProjectId(e.target.value)}
              className={inputClasses}
            />
          </div>

          {/* Two-column row: Start Time + End Time */}
          <div className="mb-4 grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <label htmlFor="task-start-time" className={labelClasses}>
                Start Time
              </label>
              <input
                id="task-start-time"
                type="datetime-local"
                value={startTime}
                onChange={(e) => setStartTime(e.target.value)}
                className={inputClasses}
              />
            </div>

            <div>
              <label htmlFor="task-end-time" className={labelClasses}>
                End Time
              </label>
              <input
                id="task-end-time"
                type="datetime-local"
                value={endTime}
                onChange={(e) => setEndTime(e.target.value)}
                className={inputClasses}
              />
            </div>
          </div>

          {/* Estimated Minutes */}
          <div className="mb-4">
            <label htmlFor="task-estimated-minutes" className={labelClasses}>
              Estimated Minutes
            </label>
            <input
              id="task-estimated-minutes"
              type="number"
              min="0"
              step="1"
              placeholder="0"
              value={estimatedMinutes}
              onChange={(e) => setEstimatedMinutes(e.target.value)}
              className={inputClasses}
            />
          </div>
        </fieldset>

        {/* ═══════════════════════════════════════════════════════════════
            Section 2 — Recurrence Template Editor
            (replaces PcTaskRepeatRecurrenceSet ViewComponent)
            ═══════════════════════════════════════════════════════════════ */}
        <fieldset
          className="mt-6 rounded-lg border border-gray-200 bg-white shadow-sm"
          disabled={isSubmitting}
        >
          {/* Collapsible section header */}
          <button
            type="button"
            className="flex w-full items-center justify-between px-4 py-3 text-start sm:px-6"
            onClick={() => setIsRecurrenceOpen((prev) => !prev)}
            aria-expanded={isRecurrenceOpen}
            aria-controls="recurrence-section"
          >
            <span className="text-sm font-semibold text-gray-900">
              Recurrence Settings
            </span>
            <svg
              className={`h-5 w-5 text-gray-400 transition-transform duration-200 ${
                isRecurrenceOpen ? 'rotate-180' : ''
              }`}
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M5.22 8.22a.75.75 0 0 1 1.06 0L10 11.94l3.72-3.72a.75.75 0 1 1 1.06 1.06l-4.25 4.25a.75.75 0 0 1-1.06 0L5.22 9.28a.75.75 0 0 1 0-1.06Z"
                clipRule="evenodd"
              />
            </svg>
          </button>

          {isRecurrenceOpen && (
            <div
              id="recurrence-section"
              className="border-t border-gray-200 px-4 pb-5 pt-4 sm:px-6"
            >
              {/* Recurrence Type */}
              <div className="mb-4">
                <label htmlFor="recurrence-type" className={labelClasses}>
                  Recurrence Type
                </label>
                <select
                  id="recurrence-type"
                  value={recurrence.type}
                  onChange={(e) =>
                    updateRecurrence(
                      'type',
                      e.target.value as RecurrenceTemplate['type'],
                    )
                  }
                  className={selectClasses}
                >
                  {RECURRENCE_TYPE_OPTIONS.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              </div>

              {/* ── Conditional fields shown only when type !== 'none' ── */}
              {showRecurrenceFields && (
                <>
                  {/* Interval */}
                  <div className="mb-4">
                    <label
                      htmlFor="recurrence-interval"
                      className={labelClasses}
                    >
                      Repeat every
                    </label>
                    <div className="flex items-center gap-2">
                      <input
                        id="recurrence-interval"
                        type="number"
                        min="1"
                        step="1"
                        value={recurrence.interval}
                        onChange={(e) =>
                          updateRecurrence(
                            'interval',
                            Math.max(1, parseInt(e.target.value, 10) || 1),
                          )
                        }
                        className={`${inputClasses} max-w-[6rem]`}
                      />
                      <span className="text-sm text-gray-600">
                        {periodLabel}
                      </span>
                    </div>
                  </div>

                  {/* ── Weekly day selection (visible when type=weekly) ── */}
                  {showWeeklyDays && (
                    <div className="mb-4">
                      <span className={labelClasses}>Repeat on</span>
                      <div className="mt-1 flex flex-wrap gap-3">
                        {WEEKDAY_OPTIONS.map(({ key, label }) => (
                          <label
                            key={key}
                            className="inline-flex items-center gap-1.5 text-sm text-gray-700"
                          >
                            <input
                              type="checkbox"
                              checked={recurrence[key]}
                              onChange={(e) =>
                                updateRecurrence(key, e.target.checked)
                              }
                              className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus-visible:ring-2 focus-visible:ring-indigo-500"
                            />
                            {label}
                          </label>
                        ))}
                      </div>
                    </div>
                  )}

                  {/* End Type — radio group */}
                  <div className="mb-4">
                    <span className={labelClasses}>Ends</span>
                    <div className="mt-1 flex flex-col gap-2">
                      {RECURRENCE_END_TYPE_OPTIONS.map((opt) => (
                        <label
                          key={opt.value}
                          className="inline-flex items-center gap-2 text-sm text-gray-700"
                        >
                          <input
                            type="radio"
                            name="recurrence-end-type"
                            value={opt.value}
                            checked={recurrence.endType === opt.value}
                            onChange={() =>
                              updateRecurrence(
                                'endType',
                                opt.value as RecurrenceTemplate['endType'],
                              )
                            }
                            className="h-4 w-4 border-gray-300 text-indigo-600 focus-visible:ring-2 focus-visible:ring-indigo-500"
                          />
                          {opt.label}
                        </label>
                      ))}
                    </div>
                  </div>

                  {/* End After Occurrences (visible when endType=afterOccurrences) */}
                  {showEndOccurrences && (
                    <div className="mb-4">
                      <label
                        htmlFor="recurrence-end-occurrences"
                        className={labelClasses}
                      >
                        End after (occurrences)
                      </label>
                      <input
                        id="recurrence-end-occurrences"
                        type="number"
                        min="1"
                        step="1"
                        value={recurrence.endAfterOccurrences}
                        onChange={(e) =>
                          updateRecurrence(
                            'endAfterOccurrences',
                            Math.max(
                              1,
                              parseInt(e.target.value, 10) || 1,
                            ),
                          )
                        }
                        className={`${inputClasses} max-w-[8rem]`}
                      />
                    </div>
                  )}

                  {/* End Date (visible when endType=onDate) */}
                  {showEndDate && (
                    <div className="mb-4">
                      <label
                        htmlFor="recurrence-end-date"
                        className={labelClasses}
                      >
                        End date
                      </label>
                      <input
                        id="recurrence-end-date"
                        type="date"
                        value={
                          recurrence.endDate
                            ? toDateStr(recurrence.endDate)
                            : ''
                        }
                        onChange={(e) =>
                          updateRecurrence(
                            'endDate',
                            e.target.value || null,
                          )
                        }
                        className={`${inputClasses} max-w-[14rem]`}
                      />
                    </div>
                  )}

                  {/* Allow Regeneration */}
                  <div className="mb-2">
                    <label className="inline-flex items-center gap-2 text-sm text-gray-700">
                      <input
                        type="checkbox"
                        checked={recurrence.allowRegeneration}
                        onChange={(e) =>
                          updateRecurrence(
                            'allowRegeneration',
                            e.target.checked,
                          )
                        }
                        className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus-visible:ring-2 focus-visible:ring-indigo-500"
                      />
                      Allow regeneration
                    </label>
                    <p className="mt-1 text-xs text-gray-500">
                      When enabled, completing an occurrence generates the next
                      one.
                    </p>
                  </div>
                </>
              )}
            </div>
          )}
        </fieldset>

        {/* ═══════════════════════════════════════════════════════════════
            Section 3 — Form Actions
            ═══════════════════════════════════════════════════════════════ */}
        <div className="mt-6 flex items-center justify-end gap-3">
          <button
            type="button"
            onClick={handleCancel}
            disabled={isSubmitting}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="submit"
            disabled={isSubmitting}
            className="inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isSubmitting ? 'Saving…' : 'Save Changes'}
          </button>
        </div>
      </form>
    </div>
  );
}
