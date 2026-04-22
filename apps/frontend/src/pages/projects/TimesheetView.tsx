/**
 * TimesheetView — 7-Day Timesheet Grid
 *
 * React page component replacing the C# `PcProjectWidgetTimesheet` ViewComponent
 * from `WebVella.Erp.Plugins.Project/Components/PcProjectWidgetTimesheet/`.
 *
 * Renders a 7-day timesheet grid showing billable/non-billable/total minutes per
 * day, with optional per-user breakdown rows when no userId filter is applied.
 *
 * Source Logic Mapping:
 *   - PcProjectWidgetTimesheet.cs lines 85-88  → `getLast7Days()` helper
 *   - PcProjectWidgetTimesheet.cs lines 90-92  → `useTimelogs` + `useUsers` hooks
 *   - PcProjectWidgetTimesheet.cs lines 94-111 → column headers (dd MMM + Total)
 *   - PcProjectWidgetTimesheet.cs lines 113-132→ billable/nonbillable/total row init
 *   - PcProjectWidgetTimesheet.cs lines 134-166→ per-day timelog aggregation loop
 *   - PcProjectWidgetTimesheet.cs lines 168-202→ per-user breakdown rows
 *   - TimeLogService.GetTimelogsForPeriod()    → REST via `useTimelogs` hook
 *
 * Data Flow:
 *   1. Compute 7-day date range (today − 6 → today) in local timezone
 *   2. Fetch raw timelogs for the period via `useTimelogs` hook
 *   3. Fetch aggregated summary via `useTimelogSummary` hook (project-scoped)
 *   4. Fetch all users via `useUsers` hook (for per-user avatar rows)
 *   5. Memoised aggregation loop groups timelogs by date, splits billable vs
 *      non-billable, and optionally groups by user when no userId filter is set
 *
 * @module pages/projects/TimesheetView
 */

import React, { useMemo } from 'react';
import { useTimelogs, useTimelogSummary } from '../../hooks/useProjects';
import { useUsers } from '../../hooks/useUsers';
import type { EntityRecord } from '../../types/record';
import type { ErpUser } from '../../types/user';

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * Represents a single row in the timesheet grid.
 *
 * Mirrors the C# anonymous row objects built at PcProjectWidgetTimesheet.cs
 * lines 113-132 (summary rows) and lines 168-202 (per-user rows).
 */
interface TimesheetRow {
  /** Unique row identifier (e.g. 'billable', 'nonbillable', 'total', or a user ID) */
  id: string;
  /** Display label — plain string for summary rows, JSX for user rows (avatar + name) */
  label: string | React.ReactNode;
  /** 7 values in minutes, one per day. Index 0 = oldest day (today − 6), index 6 = today */
  days: number[];
  /** Sum of all 7 day values (minutes) */
  total: number;
  /** When true, the row renders with bold/emphasised styling (used for the "Total" row) */
  isHighlighted?: boolean;
}

/**
 * Props accepted by the {@link TimesheetView} component.
 *
 * Maps to `PcProjectWidgetTimesheetOptions` in the C# source:
 *   - project_id (string → Guid?) → `projectId`
 *   - user_id   (string → Guid?) → `userId`
 */
interface TimesheetViewProps {
  /** Optional project ID to scope timelogs. Passed to `useTimelogSummary`. */
  projectId?: string;
  /** Optional user ID filter. When absent, per-user breakdown rows are shown. */
  userId?: string;
}

// ---------------------------------------------------------------------------
// Date Helpers
// ---------------------------------------------------------------------------

/** Number of days displayed in the timesheet grid. */
const DAYS_IN_GRID = 7;

/**
 * Computes an array of 7 Date objects from (today − 6) through today
 * inclusive, all set to local midnight.
 *
 * Mirrors C# PcProjectWidgetTimesheet.cs lines 85-88:
 * ```csharp
 * var nowDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day);
 * for (int i = 6; i >= 0; i--) { last7Days.Add(nowDate.AddDays(-i)); }
 * ```
 */
function getLast7Days(): Date[] {
  const now = new Date();
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const days: Date[] = [];

  for (let i = DAYS_IN_GRID - 1; i >= 0; i--) {
    const d = new Date(today.getTime());
    d.setDate(today.getDate() - i);
    days.push(d);
  }

  return days;
}

/**
 * Formats a date as "dd MMM" (e.g. "15 Jan") for column headers.
 * Mirrors C# `DateTime.ToString("dd MMM")`.
 */
function formatDateHeader(date: Date): string {
  const day = String(date.getDate()).padStart(2, '0');
  const month = date.toLocaleDateString('en-US', { month: 'short' });
  return `${day} ${month}`;
}

/**
 * Formats a date as "dd-MM" (e.g. "15-01") for grouping-key matching.
 * Mirrors C# `DateTime.ToString("dd-MM")` used at line 136 and line 139.
 */
function formatDateKey(date: Date): string {
  const day = String(date.getDate()).padStart(2, '0');
  const month = String(date.getMonth() + 1).padStart(2, '0');
  return `${day}-${month}`;
}

/**
 * Produces an ISO 8601 date string (YYYY-MM-DD) for API query parameters.
 * Uses local date parts to match the C# local-timezone approach.
 */
function toIsoDateString(date: Date): string {
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, '0');
  const d = String(date.getDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

/**
 * Formats a minute total as human-readable text.
 * - 0 → "0m"
 * - 90 → "1h 30m"
 * - 60 → "1h"
 * - 45 → "45m"
 */
function formatMinutes(totalMinutes: number): string {
  if (totalMinutes <= 0) return '0m';
  const hours = Math.floor(totalMinutes / 60);
  const mins = Math.round(totalMinutes % 60);
  if (hours === 0) return `${mins}m`;
  if (mins === 0) return `${hours}h`;
  return `${hours}h ${mins}m`;
}

/**
 * Safely extracts a numeric "minutes" value from a dynamic EntityRecord field.
 * Returns 0 when the value is missing, null, or not coercible to a number.
 */
function extractMinutes(record: EntityRecord): number {
  const raw = record['minutes'];
  if (typeof raw === 'number') return raw;
  if (typeof raw === 'string') {
    const parsed = Number(raw);
    return Number.isFinite(parsed) ? parsed : 0;
  }
  return 0;
}

// ---------------------------------------------------------------------------
// Default Avatar
// ---------------------------------------------------------------------------

/** Fallback avatar path for users without a profile image. */
const DEFAULT_AVATAR_PATH = '/assets/avatar-default.png';

// ---------------------------------------------------------------------------
// Aggregation — builds TimesheetRow[] from raw timelogs
// ---------------------------------------------------------------------------

/**
 * Core aggregation function that mirrors PcProjectWidgetTimesheet.cs lines
 * 113-202.  Pure function — no side-effects.
 *
 * 1. Initialises 3 summary rows: Billable, Non-Billable, Total.
 * 2. Groups timelogs by `logged_on` date key ("dd-MM").
 * 3. For each of the 7 days, matches the timelog group and accumulates
 *    minutes into billable / non-billable / total rows.
 * 4. When `showUserBreakdown` is true (no userId filter), groups timelogs
 *    by `created_by` and appends a row per user with avatar + name.
 */
function buildTimesheetRows(
  timelogs: EntityRecord[],
  users: ErpUser[],
  last7Days: Date[],
  showUserBreakdown: boolean,
): TimesheetRow[] {
  // --- Row initialisation (C# lines 113-132) ---
  const billableRow: TimesheetRow = {
    id: 'billable',
    label: 'Billable',
    days: new Array<number>(DAYS_IN_GRID).fill(0),
    total: 0,
  };

  const nonbillableRow: TimesheetRow = {
    id: 'nonbillable',
    label: 'Non-Billable',
    days: new Array<number>(DAYS_IN_GRID).fill(0),
    total: 0,
  };

  const totalRow: TimesheetRow = {
    id: 'total',
    label: 'Total',
    days: new Array<number>(DAYS_IN_GRID).fill(0),
    total: 0,
    isHighlighted: true,
  };

  // --- Group timelogs by date key (C# line 136) ---
  const groupedByDate = new Map<string, EntityRecord[]>();
  const processedIds = new Set<string>();

  for (const tl of timelogs) {
    // Deduplicate by record id for safety
    const recordId = tl.id;
    if (recordId) {
      if (processedIds.has(recordId)) continue;
      processedIds.add(recordId);
    }

    const loggedOn = tl['logged_on'];
    if (loggedOn == null) continue;

    const logDate = new Date(loggedOn as string);
    if (Number.isNaN(logDate.getTime())) continue;

    const key = formatDateKey(logDate);
    const bucket = groupedByDate.get(key);
    if (bucket) {
      bucket.push(tl);
    } else {
      groupedByDate.set(key, [tl]);
    }
  }

  // --- Per-day aggregation (C# lines 134-166) ---
  for (let dayIndex = 0; dayIndex < DAYS_IN_GRID; dayIndex++) {
    const dateKey = formatDateKey(last7Days[dayIndex]);
    const dayTimelogs = groupedByDate.get(dateKey);
    if (!dayTimelogs) continue;

    for (const tl of dayTimelogs) {
      const minutes = extractMinutes(tl);
      const isBillable = tl['is_billable'] === true;

      // Always add to total (C# lines 155-158)
      totalRow.days[dayIndex] += minutes;
      totalRow.total += minutes;

      if (isBillable) {
        billableRow.days[dayIndex] += minutes;
        billableRow.total += minutes;
      } else {
        nonbillableRow.days[dayIndex] += minutes;
        nonbillableRow.total += minutes;
      }
    }
  }

  const result: TimesheetRow[] = [billableRow, nonbillableRow, totalRow];

  // --- Per-user breakdown (C# lines 168-202, only when userId is null) ---
  if (showUserBreakdown) {
    const userMap = new Map<string, ErpUser>();
    for (const u of users) {
      userMap.set(u.id, u);
    }

    // Group ALL timelogs (pre-dedup set already applied) by created_by
    const groupedByUser = new Map<string, EntityRecord[]>();
    for (const tl of timelogs) {
      const recordId = tl.id;
      if (recordId && !processedIds.has(recordId)) continue;

      const createdBy = tl['created_by'] as string | undefined;
      if (!createdBy) continue;

      const bucket = groupedByUser.get(createdBy);
      if (bucket) {
        bucket.push(tl);
      } else {
        groupedByUser.set(createdBy, [tl]);
      }
    }

    for (const [userIdKey, userTimelogs] of groupedByUser) {
      const user = userMap.get(userIdKey);
      const username = user?.username ?? 'Unknown User';
      const avatarSrc = user?.image ? `/fs/${user.image}` : DEFAULT_AVATAR_PATH;

      // JSX label with avatar circle + username (C# lines 178-193)
      const userLabel = (
        <span className="inline-flex items-center gap-2">
          <img
            src={avatarSrc}
            alt=""
            aria-hidden="true"
            width={24}
            height={24}
            className="inline-block size-6 rounded-full object-cover bg-gray-200"
          />
          <span>{username}</span>
        </span>
      );

      const userRow: TimesheetRow = {
        id: userIdKey,
        label: userLabel,
        days: new Array<number>(DAYS_IN_GRID).fill(0),
        total: 0,
      };

      // Re-group this user's timelogs by date
      for (const tl of userTimelogs) {
        const loggedOn = tl['logged_on'];
        if (loggedOn == null) continue;

        const logDate = new Date(loggedOn as string);
        if (Number.isNaN(logDate.getTime())) continue;

        const dayKey = formatDateKey(logDate);
        for (let dayIndex = 0; dayIndex < DAYS_IN_GRID; dayIndex++) {
          if (formatDateKey(last7Days[dayIndex]) === dayKey) {
            const minutes = extractMinutes(tl);
            userRow.days[dayIndex] += minutes;
            userRow.total += minutes;
            break;
          }
        }
      }

      result.push(userRow);
    }
  }

  return result;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * 7-day timesheet grid page component.
 *
 * Displays billable / non-billable / total minutes per day across the last
 * 7 calendar days, with optional per-user breakdown rows including avatar
 * thumbnails and usernames.
 *
 * Replaces the monolith's `PcProjectWidgetTimesheet` ViewComponent and
 * `TimeLogService.GetTimelogsForPeriod()` EQL query.
 *
 * @param props.projectId - Optional project ID filter (passed to summary hook)
 * @param props.userId    - Optional user ID filter; omit for per-user breakdown
 */
function TimesheetView({ projectId, userId }: TimesheetViewProps) {
  // -----------------------------------------------------------------------
  // Date range computation — memoised so it only recalculates on mount
  // -----------------------------------------------------------------------
  const last7Days = useMemo(() => getLast7Days(), []);

  const startDate = useMemo(
    () => toIsoDateString(last7Days[0]),
    [last7Days],
  );

  const endDate = useMemo(() => {
    const dayAfterToday = new Date(last7Days[DAYS_IN_GRID - 1].getTime());
    dayAfterToday.setDate(dayAfterToday.getDate() + 1);
    return toIsoDateString(dayAfterToday);
  }, [last7Days]);

  // -----------------------------------------------------------------------
  // Data fetching hooks
  // -----------------------------------------------------------------------

  /**
   * Raw timelog records for the 7-day window.
   *
   * Replaces `TimeLogService.GetTimelogsForPeriod(projectId, userId, startDate, endDate)`
   * (TimeLogService.cs line 74-115). PageSize is set large to retrieve all
   * records in the window for client-side aggregation.
   */
  const {
    data: timelogData,
    isLoading: timelogsLoading,
    isError: timelogsError,
    error: timelogsErrorObj,
  } = useTimelogs({
    userId,
    startDate,
    endDate,
    pageSize: 9999,
    sort: 'logged_on:asc',
  });

  /**
   * Aggregated timelog summary scoped to the project (when projectId is set).
   *
   * `useTimelogSummary` supports `projectId` which `useTimelogs` does not,
   * so this provides project-level total metrics complementing the raw
   * timelog aggregation rendered in the grid.
   */
  const { data: summaryData } = useTimelogSummary({
    projectId,
    startDate,
    endDate,
    groupBy: 'date',
  });

  /**
   * All users — required for per-user breakdown rows.
   *
   * Replaces `UserService.GetAll()` at PcProjectWidgetTimesheet.cs line 92.
   * The `data` property follows `ApiResponse<ErpUser[]>` envelope shape.
   */
  const { data: usersResponse } = useUsers();

  // -----------------------------------------------------------------------
  // Extract arrays from hook responses
  // -----------------------------------------------------------------------
  const timelogs: EntityRecord[] = timelogData?.records ?? [];
  const users: ErpUser[] = usersResponse?.object ?? [];
  const showUserBreakdown = !userId;

  // -----------------------------------------------------------------------
  // Memoised row aggregation — mirrors C# lines 113-202
  // -----------------------------------------------------------------------
  const rows = useMemo(
    () => buildTimesheetRows(timelogs, users, last7Days, showUserBreakdown),
    [timelogs, users, last7Days, showUserBreakdown],
  );

  /**
   * Extract optional project-level total from the summary endpoint.
   * The summary `EntityRecord` may contain a `total_minutes` aggregate field.
   */
  const summaryTotalMinutes = useMemo(() => {
    if (!summaryData) return null;
    const total = summaryData['total_minutes'];
    return typeof total === 'number' ? total : null;
  }, [summaryData]);

  // -----------------------------------------------------------------------
  // Loading state
  // -----------------------------------------------------------------------
  if (timelogsLoading) {
    return (
      <div
        className="flex items-center justify-center p-8"
        role="status"
        aria-label="Loading timesheet"
      >
        <svg
          className="size-6 animate-spin text-blue-600"
          xmlns="http://www.w3.org/2000/svg"
          fill="none"
          viewBox="0 0 24 24"
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
            d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
          />
        </svg>
        <span className="ms-2 text-sm text-gray-500">
          Loading timesheet…
        </span>
      </div>
    );
  }

  // -----------------------------------------------------------------------
  // Error state
  // -----------------------------------------------------------------------
  if (timelogsError) {
    return (
      <div
        className="rounded-md border border-red-200 bg-red-50 p-4"
        role="alert"
      >
        <p className="text-sm font-medium text-red-800">
          Failed to load timesheet data
        </p>
        {timelogsErrorObj?.message && (
          <p className="mt-1 text-sm text-red-600">
            {timelogsErrorObj.message}
          </p>
        )}
      </div>
    );
  }

  // -----------------------------------------------------------------------
  // Empty state
  // -----------------------------------------------------------------------
  if (timelogs.length === 0) {
    return (
      <div className="rounded-md border border-gray-200 bg-gray-50 p-8 text-center">
        <p className="text-sm text-gray-500">
          No time logged in the last {DAYS_IN_GRID} days.
        </p>
      </div>
    );
  }

  // -----------------------------------------------------------------------
  // Render — 7-day timesheet grid table
  // -----------------------------------------------------------------------
  return (
    <section aria-label="7-day timesheet" className="space-y-3">
      {/* Optional project-level summary from useTimelogSummary */}
      {summaryTotalMinutes !== null && (
        <p className="text-sm text-gray-600">
          <span className="font-medium">Period Total:</span>{' '}
          {formatMinutes(summaryTotalMinutes)}
        </p>
      )}

      {/* Responsive scroll wrapper for narrow viewports */}
      <div className="overflow-x-auto rounded-md border border-gray-200">
        <table className="min-w-full divide-y divide-gray-200 text-sm">
          {/* ---- Column headers ---- */}
          <thead className="bg-gray-50">
            <tr>
              {/* Label column — empty header (C# line 95) */}
              <th
                className="whitespace-nowrap px-3 py-2 text-start font-medium text-gray-700"
                scope="col"
              >
                <span className="sr-only">Category</span>
              </th>

              {/* 7 date columns — "dd MMM", right-aligned, 10% width (C# lines 97-107) */}
              {last7Days.map((date, idx) => (
                <th
                  key={idx}
                  className="w-[10%] whitespace-nowrap px-3 py-2 text-end font-medium text-gray-700"
                  scope="col"
                >
                  {formatDateHeader(date)}
                </th>
              ))}

              {/* Total column — bold, right-aligned, 10% width (C# lines 108-111) */}
              <th
                className="w-[10%] whitespace-nowrap px-3 py-2 text-end font-bold text-gray-900"
                scope="col"
              >
                Total
              </th>
            </tr>
          </thead>

          {/* ---- Data rows ---- */}
          <tbody className="divide-y divide-gray-100 bg-white">
            {rows.map((row) => {
              const isSummaryRow =
                row.id === 'billable' ||
                row.id === 'nonbillable' ||
                row.id === 'total';
              const rowClasses = row.isHighlighted
                ? 'bg-gray-100 font-bold'
                : isSummaryRow
                  ? ''
                  : 'hover:bg-gray-50';

              return (
                <tr key={row.id} className={rowClasses}>
                  {/* Label cell */}
                  <td className="whitespace-nowrap px-3 py-2 text-start text-gray-800">
                    {row.label}
                  </td>

                  {/* 7 day-value cells — right-aligned, tabular numbers */}
                  {row.days.map((dayVal, dayIdx) => (
                    <td
                      key={dayIdx}
                      className={`whitespace-nowrap px-3 py-2 text-end tabular-nums ${
                        row.isHighlighted
                          ? 'font-bold text-gray-900'
                          : 'text-gray-700'
                      }`}
                    >
                      {dayVal > 0 ? formatMinutes(dayVal) : '\u2014'}
                    </td>
                  ))}

                  {/* Total cell */}
                  <td
                    className={`whitespace-nowrap px-3 py-2 text-end tabular-nums ${
                      row.isHighlighted
                        ? 'font-bold text-gray-900'
                        : 'text-gray-700'
                    }`}
                  >
                    {row.total > 0 ? formatMinutes(row.total) : '\u2014'}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}

export default TimesheetView;
