/**
 * TimelogCreate.tsx — Timelog Entry Creation Form
 *
 * React page component for creating new timelog entries. Replaces the monolith's
 * timelog creation flow from ProjectController.cs (POST /api/v3.0/p/project/pc-timelog-list/create)
 * and TimeLogService.Create().
 *
 * Form fields:
 *  - Task Association (required, pre-fillable from ?taskId= URL param)
 *  - Minutes (required, positive integer, supports "1h 30m" input helper)
 *  - Is Billable (toggle, default: true — matching monolith's default)
 *  - Logged On (date picker, default: today)
 *  - Description/Body (textarea, optional)
 *
 * URL parameters:
 *  - ?taskId=<id>   — Pre-fills the task association
 *  - ?projectId=<id> — Filters the task selector to a specific project's tasks
 *
 * @module pages/projects/TimelogCreate
 */

import React, { useState, useCallback, type FormEvent } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useCreateTimelog, useTasks } from '../../hooks/useProjects';
import type { EntityRecord } from '../../types/record';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Returns today's date formatted as YYYY-MM-DD for the <input type="date"> default.
 */
function getTodayDateString(): string {
  const now = new Date();
  const year = now.getFullYear();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/**
 * Parses a free-form minutes input string into a positive integer of minutes.
 *
 * Supported formats:
 *  - Plain number: "90" → 90
 *  - Hours only: "2h" → 120
 *  - Minutes only: "30m" → 30
 *  - Combined: "1h 30m" → 90, "1h30m" → 90, "2h 15m" → 135
 *
 * Returns NaN for invalid input.
 */
function parseMinutesInput(input: string): number {
  const trimmed = input.trim();
  if (trimmed === '') return NaN;

  // Match combined hours-and-minutes pattern: "1h 30m", "1h30m", "2h 15m"
  const combinedMatch = trimmed.match(
    /^(\d+)\s*h\s*(\d+)\s*m?$/i,
  );
  if (combinedMatch) {
    const hours = parseInt(combinedMatch[1], 10);
    const mins = parseInt(combinedMatch[2], 10);
    return hours * 60 + mins;
  }

  // Match hours-only pattern: "2h"
  const hoursMatch = trimmed.match(/^(\d+)\s*h$/i);
  if (hoursMatch) {
    return parseInt(hoursMatch[1], 10) * 60;
  }

  // Match minutes-only pattern: "30m"
  const minutesMatch = trimmed.match(/^(\d+)\s*m$/i);
  if (minutesMatch) {
    return parseInt(minutesMatch[1], 10);
  }

  // Plain number → interpret as minutes
  const plain = parseInt(trimmed, 10);
  if (!Number.isNaN(plain) && String(plain) === trimmed) {
    return plain;
  }

  return NaN;
}

/**
 * Converts a local date string (YYYY-MM-DD) to a UTC ISO-8601 string.
 * This mirrors the monolith's ConvertAppDateToUtc behaviour where the
 * logged date is stored in UTC.
 */
function toUtcIsoString(localDate: string): string {
  const date = new Date(`${localDate}T00:00:00`);
  return date.toISOString();
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * TimelogCreate — Page component for creating a new timelog entry.
 *
 * Lazy-loaded via React.lazy(). Designed to work both as a standalone route
 * (/projects/timelogs/create) and as a compact form embeddable in a
 * modal / drawer context.
 */
function TimelogCreate(): React.JSX.Element {
  // -- Routing & URL params --------------------------------------------------
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();

  const initialTaskId = searchParams.get('taskId') ?? '';
  const projectIdFilter = searchParams.get('projectId') ?? undefined;

  // -- Form state ------------------------------------------------------------
  const [taskId, setTaskId] = useState<string>(initialTaskId);
  const [minutesInput, setMinutesInput] = useState<string>('');
  const [isBillable, setIsBillable] = useState<boolean>(true);
  const [loggedOn, setLoggedOn] = useState<string>(getTodayDateString());
  const [body, setBody] = useState<string>('');

  // Client-side validation error messages
  const [validationErrors, setValidationErrors] = useState<
    Record<string, string>
  >({});

  // -- Data hooks ------------------------------------------------------------
  const {
    mutate: createTimelog,
    isPending,
    isError,
    error: mutationError,
    isSuccess,
  } = useCreateTimelog();

  const { data: tasksData, isLoading: isLoadingTasks } = useTasks(
    projectIdFilter ? { projectId: projectIdFilter } : undefined,
  );

  // Derive tasks array (EntityRecord[]) from the query data.
  // useTasks returns EntityRecordList which has `records` and `totalCount`.
  const tasks: EntityRecord[] = tasksData?.records ?? [];

  // -- Validation ------------------------------------------------------------
  const validate = useCallback((): boolean => {
    const errors: Record<string, string> = {};

    if (!taskId) {
      errors.taskId = 'A task must be selected.';
    }

    const parsedMinutes = parseMinutesInput(minutesInput);
    if (Number.isNaN(parsedMinutes) || parsedMinutes < 1) {
      errors.minutes =
        'Minutes must be a positive number (e.g. 90, 1h 30m).';
    }

    if (!loggedOn) {
      errors.loggedOn = 'Logged date is required.';
    }

    setValidationErrors(errors);
    return Object.keys(errors).length === 0;
  }, [taskId, minutesInput, loggedOn]);

  // -- Submission handler ----------------------------------------------------
  const handleSubmit = useCallback(
    (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();

      if (!validate()) return;

      const parsedMinutes = parseMinutesInput(minutesInput);

      // Build the EntityRecord payload matching the monolith's
      // ProjectController.CreateTimelog expectations:
      //  relatedRecordId, minutes, isBillable, loggedOn, body, relatedRecords
      const payload: EntityRecord = {
        relatedRecordId: taskId,
        minutes: parsedMinutes,
        isBillable,
        loggedOn: toUtcIsoString(loggedOn),
        body: body.trim(),
        relatedRecords: JSON.stringify([taskId]),
        scope: JSON.stringify(['projects']),
      };

      createTimelog(payload, {
        onSuccess: () => {
          // Navigate to the task detail page or back in history
          if (taskId) {
            navigate(`/projects/tasks/${taskId}`, { replace: true });
          } else {
            navigate(-1);
          }
        },
      });
    },
    [
      validate,
      minutesInput,
      taskId,
      isBillable,
      loggedOn,
      body,
      createTimelog,
      navigate,
    ],
  );

  // -- Cancel handler --------------------------------------------------------
  const handleCancel = useCallback(() => {
    navigate(-1);
  }, [navigate]);

  // -- Render ----------------------------------------------------------------
  return (
    <section
      className="mx-auto max-w-2xl px-4 py-6 sm:px-6 lg:px-8"
      data-testid="timelog-form-section"
    >
      {/* Page heading */}
      <h1
        id="timelog-create-heading"
        className="text-2xl font-semibold text-gray-900"
      >
        Log Time
      </h1>
      <p className="mt-1 text-sm text-gray-500">
        Record time spent on a task.
      </p>

      {/* Success banner */}
      {isSuccess && (
        <div
          role="status"
          className="mt-4 rounded-md bg-green-50 p-4 text-sm text-green-800"
        >
          Timelog created successfully. Redirecting…
        </div>
      )}

      {/* Server error banner */}
      {isError && mutationError && (
        <div
          role="alert"
          className="mt-4 rounded-md bg-red-50 p-4 text-sm text-red-800"
        >
          <strong className="font-medium">Error: </strong>
          {mutationError.message || 'Failed to create timelog.'}
        </div>
      )}

      <form
        onSubmit={handleSubmit}
        noValidate
        className="mt-6 space-y-6"
      >
        {/* ----------------------------------------------------------------- */}
        {/*  Task Association                                                 */}
        {/* ----------------------------------------------------------------- */}
        <div>
          <label
            htmlFor="timelog-task"
            className="block text-sm font-medium text-gray-700"
          >
            Task <span aria-hidden="true">*</span>
          </label>

          <select
            id="timelog-task"
            value={taskId}
            onChange={(e) => {
              setTaskId(e.target.value);
              setValidationErrors((prev) => {
                const next = { ...prev };
                delete next.taskId;
                return next;
              });
            }}
            required
            aria-required="true"
            aria-invalid={!!validationErrors.taskId}
            aria-describedby={
              validationErrors.taskId
                ? 'timelog-task-error'
                : undefined
            }
            disabled={isLoadingTasks}
            className={[
              'mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm',
              'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1',
              validationErrors.taskId
                ? 'border-red-400 text-red-900'
                : 'border-gray-300 text-gray-900',
              isLoadingTasks ? 'cursor-wait opacity-60' : '',
            ]
              .filter(Boolean)
              .join(' ')}
          >
            <option value="">
              {isLoadingTasks
                ? 'Loading tasks…'
                : '— Select a task —'}
            </option>
            {tasks.map((task) => {
              const taskKey = (task.key as string) ?? '';
              const taskSubject = (task.subject as string) ?? '';
              const label = taskKey
                ? `[${taskKey}] ${taskSubject}`
                : taskSubject || String(task.id ?? '');
              return (
                <option key={String(task.id)} value={String(task.id)}>
                  {label}
                </option>
              );
            })}
          </select>

          {validationErrors.taskId && (
            <p
              id="timelog-task-error"
              className="mt-1 text-sm text-red-600"
              role="alert"
            >
              {validationErrors.taskId}
            </p>
          )}
        </div>

        {/* ----------------------------------------------------------------- */}
        {/*  Minutes                                                          */}
        {/* ----------------------------------------------------------------- */}
        <div>
          <label
            htmlFor="timelog-minutes"
            className="block text-sm font-medium text-gray-700"
          >
            Time Spent <span aria-hidden="true">*</span>
          </label>
          <p
            id="timelog-minutes-hint"
            className="text-xs text-gray-500"
          >
            Enter minutes (e.g. 90) or use shorthand (e.g. 1h 30m).
          </p>

          <input
            id="timelog-minutes"
            name="minutes"
            type="text"
            inputMode="numeric"
            value={minutesInput}
            onChange={(e) => {
              setMinutesInput(e.target.value);
              setValidationErrors((prev) => {
                const next = { ...prev };
                delete next.minutes;
                return next;
              });
            }}
            required
            aria-required="true"
            aria-invalid={!!validationErrors.minutes}
            aria-describedby={[
              'timelog-minutes-hint',
              validationErrors.minutes
                ? 'timelog-minutes-error'
                : '',
            ]
              .filter(Boolean)
              .join(' ')}
            placeholder="e.g. 90 or 1h 30m"
            className={[
              'mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm',
              'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1',
              validationErrors.minutes
                ? 'border-red-400 text-red-900'
                : 'border-gray-300 text-gray-900',
            ]
              .filter(Boolean)
              .join(' ')}
          />

          {validationErrors.minutes && (
            <p
              id="timelog-minutes-error"
              className="mt-1 text-sm text-red-600"
              role="alert"
            >
              {validationErrors.minutes}
            </p>
          )}
        </div>

        {/* ----------------------------------------------------------------- */}
        {/*  Billable Toggle                                                  */}
        {/* ----------------------------------------------------------------- */}
        <div className="flex items-center gap-3">
          <input
            id="timelog-billable"
            type="checkbox"
            checked={isBillable}
            onChange={(e) => setIsBillable(e.target.checked)}
            className="h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1"
          />
          <label
            htmlFor="timelog-billable"
            className="text-sm font-medium text-gray-700"
          >
            Billable
          </label>
        </div>

        {/* ----------------------------------------------------------------- */}
        {/*  Logged On Date                                                   */}
        {/* ----------------------------------------------------------------- */}
        <div>
          <label
            htmlFor="timelog-loggedon"
            className="block text-sm font-medium text-gray-700"
          >
            Date Logged <span aria-hidden="true">*</span>
          </label>

          <input
            id="timelog-loggedon"
            name="date"
            type="date"
            value={loggedOn}
            onChange={(e) => {
              setLoggedOn(e.target.value);
              setValidationErrors((prev) => {
                const next = { ...prev };
                delete next.loggedOn;
                return next;
              });
            }}
            required
            aria-required="true"
            aria-invalid={!!validationErrors.loggedOn}
            aria-describedby={
              validationErrors.loggedOn
                ? 'timelog-loggedon-error'
                : undefined
            }
            className={[
              'mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm',
              'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1',
              validationErrors.loggedOn
                ? 'border-red-400 text-red-900'
                : 'border-gray-300 text-gray-900',
            ]
              .filter(Boolean)
              .join(' ')}
          />

          {validationErrors.loggedOn && (
            <p
              id="timelog-loggedon-error"
              className="mt-1 text-sm text-red-600"
              role="alert"
            >
              {validationErrors.loggedOn}
            </p>
          )}
        </div>

        {/* ----------------------------------------------------------------- */}
        {/*  Description / Body                                               */}
        {/* ----------------------------------------------------------------- */}
        <div>
          <label
            htmlFor="timelog-body"
            className="block text-sm font-medium text-gray-700"
          >
            Description
          </label>

          <textarea
            id="timelog-body"
            name="description"
            value={body}
            onChange={(e) => setBody(e.target.value)}
            rows={4}
            placeholder="Optional notes about this timelog entry…"
            className={[
              'mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm',
              'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1',
              'text-gray-900 placeholder:text-gray-400',
            ].join(' ')}
          />
        </div>

        {/* ----------------------------------------------------------------- */}
        {/*  Action Buttons                                                   */}
        {/* ----------------------------------------------------------------- */}
        <div className="flex items-center justify-end gap-3 border-t border-gray-200 pt-4">
          <button
            type="button"
            onClick={handleCancel}
            disabled={isPending}
            className={[
              'rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm',
              'hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1',
              'disabled:cursor-not-allowed disabled:opacity-50',
            ].join(' ')}
          >
            Cancel
          </button>

          <button
            type="submit"
            disabled={isPending}
            className={[
              'inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm',
              'hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1',
              'disabled:cursor-not-allowed disabled:opacity-50',
            ].join(' ')}
          >
            {isPending && (
              <span
                className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-current border-t-transparent"
                aria-hidden="true"
              />
            )}
            {isPending ? 'Saving…' : 'Log Time'}
          </button>
        </div>
      </form>
    </section>
  );
}

export default TimelogCreate;
