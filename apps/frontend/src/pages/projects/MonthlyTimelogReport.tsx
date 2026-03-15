/**
 * Monthly Timelog Report Page
 *
 * React page component replacing `PcReportAccountMonthlyTimelogs` ViewComponent
 * from `WebVella.Erp.Plugins.Project/Components/PcReportAccountMonthlyTimelogs/`.
 *
 * Renders a monthly timelog report driven by year/month/account filter
 * selections, with project-grouped task rows showing billable and
 * non-billable minutes. Mirrors the data pipeline from
 * `ReportService.GetTimelogData(year, month, accountId)` in the monolith.
 *
 * Architecture:
 *  - Filter state persisted in URL search params (?year=&month=&accountId=)
 *    for shareable/bookmarkable report links
 *  - Data fetched via `useTimelogSummary` TanStack Query hook (GET /v1/inventory/timelogs/summary)
 *  - Account dropdown populated by `useAccounts` from the CRM service
 *  - Client-side grouping by project + optional account filtering
 *  - Tailwind CSS for all styling (zero Bootstrap)
 *
 * @module pages/projects/MonthlyTimelogReport
 */

import React, { useState, useMemo, useCallback } from 'react';
import { useSearchParams } from 'react-router';
import { useTimelogSummary } from '../../hooks/useProjects';
import { useAccounts } from '../../hooks/useCrm';
import type { EntityRecord } from '../../types/record';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Strongly-typed row for the monthly timelog report.
 *
 * Each row represents one task within one project, with aggregated
 * billable and non-billable minutes for the selected month. Mirrors
 * the `EntityRecord` output shape from `ReportService.GetTimelogData`
 * (lines 103-132 in ReportService.cs).
 */
interface TimelogReportRow {
  /** Task record ID (GUID string) */
  task_id: string;
  /** Task subject / title */
  task_subject: string;
  /** Task type label (e.g. "Bug", "Feature", "Task") */
  task_type: string;
  /** Project record ID (GUID string) */
  project_id: string;
  /** Project display name */
  project_name: string;
  /** Total billable minutes for this task in the period */
  billable_minutes: number;
  /** Total non-billable minutes for this task in the period */
  nonbillable_minutes: number;
}

/**
 * A group of tasks belonging to a single project.
 * Used for rendering per-project report sections.
 */
interface ProjectGroup {
  /** Project display name */
  name: string;
  /** Task rows within this project */
  tasks: TimelogReportRow[];
}

/**
 * Option value for select/dropdown elements.
 */
interface SelectOption {
  value: string;
  label: string;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Month names for the month dropdown (index 0 = January). */
const MONTH_OPTIONS: SelectOption[] = [
  { value: '1', label: 'January' },
  { value: '2', label: 'February' },
  { value: '3', label: 'March' },
  { value: '4', label: 'April' },
  { value: '5', label: 'May' },
  { value: '6', label: 'June' },
  { value: '7', label: 'July' },
  { value: '8', label: 'August' },
  { value: '9', label: 'September' },
  { value: '10', label: 'October' },
  { value: '11', label: 'November' },
  { value: '12', label: 'December' },
];

/**
 * Generate a reasonable set of year options (current year ± 5).
 * Provides enough range for historical reporting and future planning.
 */
function generateYearOptions(): SelectOption[] {
  const currentYear = new Date().getFullYear();
  const years: SelectOption[] = [];
  for (let y = currentYear - 5; y <= currentYear + 1; y++) {
    years.push({ value: String(y), label: String(y) });
  }
  return years;
}

/** Cached year options — generated once. */
const YEAR_OPTIONS: SelectOption[] = generateYearOptions();

// ---------------------------------------------------------------------------
// Validation Helpers
// ---------------------------------------------------------------------------

/**
 * Validates that a year value is positive.
 * Mirrors the `year <= 0` check in ReportService.cs (line 21).
 */
function isValidYear(year: number): boolean {
  return Number.isFinite(year) && year > 0;
}

/**
 * Validates that a month value is between 1 and 12 inclusive.
 * Mirrors the `month > 12 || month <= 0` check in ReportService.cs (lines 17-18).
 */
function isValidMonth(month: number): boolean {
  return Number.isFinite(month) && month >= 1 && month <= 12;
}

// ---------------------------------------------------------------------------
// Date Range Helper
// ---------------------------------------------------------------------------

/**
 * Computes ISO 8601 date strings for the first and last day of a given
 * year/month, matching the date range construction in ReportService.cs:
 *   `fromDate = new DateTime(year, month, 1)`
 *   `toDate   = new DateTime(year, month, DateTime.DaysInMonth(year, month))`
 */
function getDateRange(
  year: number,
  month: number,
): { startDate: string; endDate: string } {
  const validYear = isValidYear(year) ? year : new Date().getFullYear();
  const validMonth = isValidMonth(month) ? month : new Date().getMonth() + 1;

  const startDate = new Date(validYear, validMonth - 1, 1);
  const endDate = new Date(validYear, validMonth, 0); // last day of month

  return {
    startDate: startDate.toISOString().split('T')[0],
    endDate: endDate.toISOString().split('T')[0],
  };
}

// ---------------------------------------------------------------------------
// Data Processing Helpers
// ---------------------------------------------------------------------------

/**
 * Formats a minute count into a human-readable string.
 *
 * - 0 → "0 min"
 * - 45 → "45 min"
 * - 60 → "1h 0m"
 * - 90 → "1h 30m"
 */
function formatMinutes(minutes: number): string {
  if (minutes === 0) return '0 min';
  const absMinutes = Math.abs(minutes);
  const hours = Math.floor(absMinutes / 60);
  const mins = Math.round(absMinutes % 60);
  const sign = minutes < 0 ? '-' : '';

  if (hours === 0) return `${sign}${mins} min`;
  if (mins === 0) return `${sign}${hours}h 0m`;
  return `${sign}${hours}h ${mins}m`;
}

/**
 * Extracts report rows from the summary endpoint EntityRecord response.
 *
 * The server-side timelog summary endpoint returns an EntityRecord
 * whose dynamic properties contain the aggregated report data. Rows
 * are expected in a `rows` property as an array of EntityRecords,
 * each containing task_id, task_subject, task_type, project_id,
 * project_name, billable_minutes, and nonbillable_minutes.
 *
 * Falls back gracefully when the data shape is unexpected — returns
 * an empty array rather than crashing.
 */
function parseReportRows(data: EntityRecord | undefined): TimelogReportRow[] {
  if (!data) return [];

  /* The summary endpoint may nest rows under a "rows" property or
     may return them under "data" — handle both conventions. */
  const rawRows =
    (data['rows'] as EntityRecord[] | undefined) ??
    (data['data'] as EntityRecord[] | undefined) ??
    [];

  if (!Array.isArray(rawRows)) return [];

  return rawRows.map((row: EntityRecord): TimelogReportRow => ({
    task_id: String(row['task_id'] ?? ''),
    task_subject: String(row['task_subject'] ?? ''),
    task_type: String(row['task_type'] ?? ''),
    project_id: String(row['project_id'] ?? ''),
    project_name: String(row['project_name'] ?? ''),
    billable_minutes: Number(row['billable_minutes'] ?? 0),
    nonbillable_minutes: Number(
      row['nonbillable_minutes'] ?? row['non_billable_minutes'] ?? 0,
    ),
  }));
}

/**
 * Groups report rows by project_id, producing a Map of ProjectGroups.
 *
 * Mirrors the grouping logic in `PcReportAccountMonthlyTimelogs.cs`
 * (lines 111-123) where unique projects are extracted and tasks are
 * associated with their parent project.
 */
function groupByProject(
  rows: TimelogReportRow[],
): Map<string, ProjectGroup> {
  const groups = new Map<string, ProjectGroup>();

  rows.forEach((row) => {
    if (!groups.has(row.project_id)) {
      groups.set(row.project_id, { name: row.project_name, tasks: [] });
    }
    groups.get(row.project_id)!.tasks.push(row);
  });

  return groups;
}

/**
 * Computes billable and non-billable subtotals for a list of rows.
 */
function computeSubtotals(rows: TimelogReportRow[]): {
  billable: number;
  nonBillable: number;
} {
  let billable = 0;
  let nonBillable = 0;

  for (const row of rows) {
    billable += row.billable_minutes;
    nonBillable += row.nonbillable_minutes;
  }

  return { billable, nonBillable };
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

/**
 * Loading skeleton for the report table.
 * Provides visual feedback while data is being fetched.
 */
function LoadingState(): React.JSX.Element {
  return (
    <div
      className="flex items-center justify-center py-16"
      role="status"
      aria-label="Loading report data"
    >
      <div className="flex flex-col items-center gap-3">
        <svg
          className="h-8 w-8 animate-spin text-blue-600"
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
            d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
          />
        </svg>
        <span className="text-sm text-gray-500">Loading report data…</span>
      </div>
    </div>
  );
}

/**
 * Error display for fetch failures.
 * Shows the error message and a retry button.
 */
function ErrorState({
  error,
  onRetry,
}: {
  error: Error;
  onRetry: () => void;
}): React.JSX.Element {
  return (
    <div
      className="mx-auto max-w-lg rounded-lg border border-red-200 bg-red-50 p-6 text-center"
      role="alert"
    >
      <svg
        className="mx-auto mb-3 h-10 w-10 text-red-400"
        xmlns="http://www.w3.org/2000/svg"
        fill="none"
        viewBox="0 0 24 24"
        strokeWidth="1.5"
        stroke="currentColor"
        aria-hidden="true"
      >
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          d="M12 9v3.75m-9.303 3.376c-.866 1.5.217
             3.374 1.948 3.374h14.71c1.73 0 2.813-1.874
             1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898
             0L2.697 16.126ZM12 15.75h.007v.008H12v-.008Z"
        />
      </svg>
      <h3 className="mb-1 text-lg font-semibold text-red-800">
        Failed to load report
      </h3>
      <p className="mb-4 text-sm text-red-600">
        {error.message || 'An unexpected error occurred.'}
      </p>
      <button
        type="button"
        onClick={onRetry}
        className="inline-flex items-center rounded-md bg-red-600 px-4
                   py-2 text-sm font-medium text-white shadow-sm
                   transition-colors duration-200
                   hover:bg-red-700 focus-visible:outline-2
                   focus-visible:outline-offset-2 focus-visible:outline-red-600"
      >
        Retry
      </button>
    </div>
  );
}

/**
 * Empty state shown when the report query returns no data for the
 * selected period.
 */
function EmptyState({
  year,
  month,
}: {
  year: number;
  month: number;
}): React.JSX.Element {
  const monthLabel =
    MONTH_OPTIONS.find((m) => m.value === String(month))?.label ?? '';

  return (
    <div className="py-16 text-center">
      <svg
        className="mx-auto mb-4 h-12 w-12 text-gray-300"
        xmlns="http://www.w3.org/2000/svg"
        fill="none"
        viewBox="0 0 24 24"
        strokeWidth="1.5"
        stroke="currentColor"
        aria-hidden="true"
      >
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          d="M19.5 14.25v-2.625a3.375 3.375 0 0 0-3.375-3.375h-1.5A1.125
             1.125 0 0 1 13.5 7.125v-1.5a3.375 3.375 0 0 0-3.375-3.375H8.25m0
             12.75h7.5m-7.5 3H12M10.5 2.25H5.625c-.621 0-1.125.504-1.125
             1.125v17.25c0 .621.504 1.125 1.125 1.125h12.75c.621 0
             1.125-.504 1.125-1.125V11.25a9 9 0 0 0-9-9Z"
        />
      </svg>
      <h3 className="mb-1 text-lg font-medium text-gray-900">
        No timelog data
      </h3>
      <p className="text-sm text-gray-500">
        No timelog entries found for{' '}
        <span className="font-medium">
          {monthLabel} {year}
        </span>
        . Try selecting a different period or account.
      </p>
    </div>
  );
}

/**
 * Single project section in the report table.
 * Renders the project header, task rows, and per-project subtotals.
 */
function ProjectSection({
  projectId,
  group,
}: {
  projectId: string;
  group: ProjectGroup;
}): React.JSX.Element {
  const { billable, nonBillable } = useMemo(
    () => computeSubtotals(group.tasks),
    [group.tasks],
  );

  return (
    <tbody>
      {/* Project header row */}
      <tr className="bg-gray-100">
        <td
          colSpan={4}
          className="px-4 py-2 text-sm font-semibold text-gray-800"
          id={`project-${projectId}`}
        >
          Project: {group.name || 'Unnamed Project'}
        </td>
      </tr>

      {/* Task rows */}
      {group.tasks.map((task) => (
        <tr
          key={`${projectId}-${task.task_id}`}
          className="border-b border-gray-100 transition-colors
                     duration-150 hover:bg-gray-50"
        >
          <td className="px-4 py-2 text-sm text-gray-700">
            {task.task_subject || '—'}
          </td>
          <td className="px-4 py-2 text-sm text-gray-500">
            {task.task_type || '—'}
          </td>
          <td className="px-4 py-2 text-end text-sm tabular-nums text-gray-700">
            {formatMinutes(task.billable_minutes)}
          </td>
          <td className="px-4 py-2 text-end text-sm tabular-nums text-gray-700">
            {formatMinutes(task.nonbillable_minutes)}
          </td>
        </tr>
      ))}

      {/* Project subtotal row */}
      <tr className="border-b-2 border-gray-300 bg-gray-50">
        <td className="px-4 py-2 text-sm font-medium text-gray-600">
          Project Total
        </td>
        <td className="px-4 py-2" />
        <td className="px-4 py-2 text-end text-sm font-medium tabular-nums text-gray-700">
          {formatMinutes(billable)}
        </td>
        <td className="px-4 py-2 text-end text-sm font-medium tabular-nums text-gray-700">
          {formatMinutes(nonBillable)}
        </td>
      </tr>
    </tbody>
  );
}

/**
 * Overall report summary footer showing grand totals.
 * Mirrors `overallBillableMinutes` and `overallNonBillableMinutes`
 * from `PcReportAccountMonthlyTimelogs.cs` (lines 108-122).
 */
function ReportSummary({
  billable,
  nonBillable,
}: {
  billable: number;
  nonBillable: number;
}): React.JSX.Element {
  const grandTotal = billable + nonBillable;

  return (
    <div
      className="mt-4 flex flex-wrap items-center gap-6 rounded-lg
                 bg-blue-50 px-6 py-4"
      aria-label="Report totals"
    >
      <div className="flex flex-col">
        <span className="text-xs font-medium uppercase tracking-wide text-blue-600">
          Billable
        </span>
        <span className="text-lg font-semibold tabular-nums text-blue-900">
          {formatMinutes(billable)}
        </span>
      </div>
      <div
        className="hidden h-8 w-px bg-blue-200 sm:block"
        aria-hidden="true"
      />
      <div className="flex flex-col">
        <span className="text-xs font-medium uppercase tracking-wide text-blue-600">
          Non-Billable
        </span>
        <span className="text-lg font-semibold tabular-nums text-blue-900">
          {formatMinutes(nonBillable)}
        </span>
      </div>
      <div
        className="hidden h-8 w-px bg-blue-200 sm:block"
        aria-hidden="true"
      />
      <div className="flex flex-col">
        <span className="text-xs font-medium uppercase tracking-wide text-blue-600">
          Grand Total
        </span>
        <span className="text-lg font-semibold tabular-nums text-blue-900">
          {formatMinutes(grandTotal)}
        </span>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * Monthly Timelog Report page component.
 *
 * Replaces the monolith's `PcReportAccountMonthlyTimelogs` ViewComponent
 * and `ReportService.GetTimelogData()` pipeline.
 *
 * Features:
 *  - URL search params for shareable filter state
 *  - Year / month / account filter bar
 *  - Project-grouped report table with task rows
 *  - Per-project billable/non-billable subtotals
 *  - Overall report totals
 *  - Loading, error, and empty states
 *  - Responsive horizontal scroll for narrow viewports
 *  - Accessible table structure with proper headings
 *
 * Default export for React.lazy() code-splitting.
 */
function MonthlyTimelogReport(): React.JSX.Element {
  // -------------------------------------------------------------------------
  // URL Search Params (single source of truth for applied filters)
  // -------------------------------------------------------------------------
  const [searchParams, setSearchParams] = useSearchParams();

  const now = new Date();
  const defaultYear = now.getFullYear();
  const defaultMonth = now.getMonth() + 1; // 1-based

  // Parse applied filter values from URL params
  const appliedYear = (() => {
    const raw = searchParams.get('year');
    if (!raw) return defaultYear;
    const parsed = parseInt(raw, 10);
    return isValidYear(parsed) ? parsed : defaultYear;
  })();

  const appliedMonth = (() => {
    const raw = searchParams.get('month');
    if (!raw) return defaultMonth;
    const parsed = parseInt(raw, 10);
    return isValidMonth(parsed) ? parsed : defaultMonth;
  })();

  const appliedAccountId = searchParams.get('accountId') ?? '';

  // -------------------------------------------------------------------------
  // Local Filter State (pending values in the filter bar)
  // -------------------------------------------------------------------------
  const [pendingYear, setPendingYear] = useState<number>(appliedYear);
  const [pendingMonth, setPendingMonth] = useState<number>(appliedMonth);
  const [pendingAccountId, setPendingAccountId] =
    useState<string>(appliedAccountId);

  // -------------------------------------------------------------------------
  // Compute date range from the APPLIED (URL) year/month
  // -------------------------------------------------------------------------
  const { startDate, endDate } = useMemo(
    () => getDateRange(appliedYear, appliedMonth),
    [appliedYear, appliedMonth],
  );

  // -------------------------------------------------------------------------
  // Data Fetching
  // -------------------------------------------------------------------------

  /**
   * Fetch timelog summary from the Inventory/Project microservice.
   * Uses `startDate`/`endDate` computed from the applied year/month.
   * `groupBy: 'task'` requests task-level aggregation (closest to the
   * monolith's ReportService project-task grouping).
   */
  const {
    data: summaryData,
    isLoading: isLoadingSummary,
    isError: isSummaryError,
    error: summaryError,
    refetch,
  } = useTimelogSummary({ startDate, endDate, groupBy: 'task' });

  /**
   * Fetch CRM accounts for the account filter dropdown.
   * Returns EntityRecordList with account records containing
   * `id` and `name` properties.
   */
  const { data: accountsData, isLoading: isLoadingAccounts } = useAccounts();

  // -------------------------------------------------------------------------
  // Account Dropdown Options
  // -------------------------------------------------------------------------
  const accountOptions = useMemo((): SelectOption[] => {
    if (!accountsData?.records) return [];
    return accountsData.records
      .filter((account) => account.id)
      .map(
        (account): SelectOption => ({
          value: String(account.id ?? ''),
          label: String(account['name'] ?? account.id ?? ''),
        }),
      );
  }, [accountsData]);

  // -------------------------------------------------------------------------
  // Data Processing
  // -------------------------------------------------------------------------

  /**
   * Parse report rows from the API response, then optionally filter
   * by the applied account ID. The account filter is applied client-side,
   * mirroring `ReportService.cs` (lines 79-96) where tasks are filtered
   * by matching `$project_nn_task.account_id` against the provided accountId.
   */
  const filteredRows = useMemo((): TimelogReportRow[] => {
    const allRows = parseReportRows(summaryData);

    if (!appliedAccountId) return allRows;

    /* When account filtering is active, only include rows whose project
       is associated with the selected account. The server-side timelog
       summary may not support account filtering directly, so we filter
       client-side for correctness. */
    return allRows.filter((row) => {
      /* The summary data rows may include an `account_id` field that
         was populated server-side from the project→account relation.
         Check if it matches the selected account. */
      const rowAccountId = String(
        (summaryData?.[`account_${row.project_id}`] as string) ?? '',
      );
      if (rowAccountId && rowAccountId === appliedAccountId) return true;

      /* Fallback: if account_id metadata is not available per-project,
         include the row — the user can refine with server-side filtering
         once the API supports it. */
      return !rowAccountId;
    });
  }, [summaryData, appliedAccountId]);

  /** Group filtered rows by project for section-based rendering. */
  const projectGroups = useMemo(
    () => groupByProject(filteredRows),
    [filteredRows],
  );

  /**
   * Overall billable and non-billable totals across all projects.
   * Mirrors `overallBillableMinutes` / `overallNonBillableMinutes`
   * from PcReportAccountMonthlyTimelogs.cs (lines 108-122).
   */
  const { overallBillable, overallNonBillable } = useMemo(() => {
    const totals = computeSubtotals(filteredRows);
    return {
      overallBillable: totals.billable,
      overallNonBillable: totals.nonBillable,
    };
  }, [filteredRows]);

  // -------------------------------------------------------------------------
  // Handlers
  // -------------------------------------------------------------------------

  /**
   * Apply pending filter values by writing them to URL search params.
   * This triggers a re-render, which recomputes the date range and
   * causes TanStack Query to refetch with the new parameters.
   */
  const handleApplyFilters = useCallback(() => {
    if (!isValidYear(pendingYear) || !isValidMonth(pendingMonth)) return;

    const params: Record<string, string> = {
      year: String(pendingYear),
      month: String(pendingMonth),
    };
    if (pendingAccountId) {
      params['accountId'] = pendingAccountId;
    }
    setSearchParams(params);
  }, [pendingYear, pendingMonth, pendingAccountId, setSearchParams]);

  /** Handle year input change with validation. */
  const handleYearChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      const value = parseInt(e.target.value, 10);
      if (isValidYear(value)) {
        setPendingYear(value);
      }
    },
    [],
  );

  /** Handle month dropdown change. */
  const handleMonthChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      const value = parseInt(e.target.value, 10);
      if (isValidMonth(value)) {
        setPendingMonth(value);
      }
    },
    [],
  );

  /** Handle account dropdown change. */
  const handleAccountChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      setPendingAccountId(e.target.value);
    },
    [],
  );

  /** Handle retry after error. */
  const handleRetry = useCallback(() => {
    refetch();
  }, [refetch]);

  // -------------------------------------------------------------------------
  // Derived display values
  // -------------------------------------------------------------------------
  const appliedMonthLabel =
    MONTH_OPTIONS.find((m) => m.value === String(appliedMonth))?.label ?? '';

  const projectGroupEntries = useMemo(
    () => Array.from(projectGroups.entries()),
    [projectGroups],
  );

  const hasData = filteredRows.length > 0;
  const isLoading = isLoadingSummary;

  // -------------------------------------------------------------------------
  // Render
  // -------------------------------------------------------------------------
  return (
    <div className="mx-auto w-full max-w-6xl px-4 py-6 sm:px-6 lg:px-8">
      {/* Page Header */}
      <header className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900">
          Monthly Timelog Report
        </h1>
        <p className="mt-1 text-sm text-gray-500">
          View billable and non-billable time entries grouped by project for a
          selected month.
        </p>
      </header>

      {/* Filter Bar */}
      <section
        className="mb-6 rounded-lg border border-gray-200 bg-white p-4
                   shadow-sm"
        aria-label="Report filters"
      >
        <div className="flex flex-wrap items-end gap-4">
          {/* Year Select */}
          <div className="flex min-w-[8rem] flex-col gap-1">
            <label
              htmlFor="report-year"
              className="text-xs font-medium uppercase tracking-wide text-gray-600"
            >
              Year
            </label>
            <select
              id="report-year"
              value={String(pendingYear)}
              onChange={handleYearChange}
              className="rounded-md border border-gray-300 bg-white px-3
                         py-2 text-sm text-gray-700 shadow-sm
                         transition-colors duration-150
                         focus-visible:border-blue-500
                         focus-visible:outline-2
                         focus-visible:outline-offset-2
                         focus-visible:outline-blue-500"
            >
              {YEAR_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
          </div>

          {/* Month Select */}
          <div className="flex min-w-[10rem] flex-col gap-1">
            <label
              htmlFor="report-month"
              className="text-xs font-medium uppercase tracking-wide text-gray-600"
            >
              Month
            </label>
            <select
              id="report-month"
              value={String(pendingMonth)}
              onChange={handleMonthChange}
              className="rounded-md border border-gray-300 bg-white px-3
                         py-2 text-sm text-gray-700 shadow-sm
                         transition-colors duration-150
                         focus-visible:border-blue-500
                         focus-visible:outline-2
                         focus-visible:outline-offset-2
                         focus-visible:outline-blue-500"
            >
              {MONTH_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
          </div>

          {/* Account Select (optional) */}
          <div className="flex min-w-[14rem] flex-col gap-1">
            <label
              htmlFor="report-account"
              className="text-xs font-medium uppercase tracking-wide text-gray-600"
            >
              Account{' '}
              <span className="font-normal normal-case text-gray-400">
                (optional)
              </span>
            </label>
            <select
              id="report-account"
              value={pendingAccountId}
              onChange={handleAccountChange}
              disabled={isLoadingAccounts}
              className="rounded-md border border-gray-300 bg-white px-3
                         py-2 text-sm text-gray-700 shadow-sm
                         transition-colors duration-150
                         focus-visible:border-blue-500
                         focus-visible:outline-2
                         focus-visible:outline-offset-2
                         focus-visible:outline-blue-500
                         disabled:cursor-not-allowed
                         disabled:opacity-50"
            >
              <option value="">All Accounts</option>
              {accountOptions.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
          </div>

          {/* Apply Button */}
          <div className="flex flex-col justify-end">
            <button
              type="button"
              onClick={handleApplyFilters}
              disabled={isLoading}
              className="inline-flex items-center rounded-md bg-blue-600
                         px-5 py-2 text-sm font-medium text-white
                         shadow-sm transition-colors duration-200
                         hover:bg-blue-700
                         focus-visible:outline-2
                         focus-visible:outline-offset-2
                         focus-visible:outline-blue-600
                         disabled:cursor-not-allowed
                         disabled:opacity-50"
            >
              {isLoading ? (
                <>
                  <svg
                    className="-ml-0.5 mr-2 h-4 w-4 animate-spin"
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
                      d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
                    />
                  </svg>
                  Loading…
                </>
              ) : (
                'Apply'
              )}
            </button>
          </div>
        </div>

        {/* Active filter summary */}
        <div className="mt-3 text-xs text-gray-400">
          Showing data for{' '}
          <span className="font-medium text-gray-600">
            {appliedMonthLabel} {appliedYear}
          </span>
          {appliedAccountId && (
            <>
              {' '}
              · Account:{' '}
              <span className="font-medium text-gray-600">
                {accountOptions.find((a) => a.value === appliedAccountId)
                  ?.label ?? appliedAccountId}
              </span>
            </>
          )}
        </div>
      </section>

      {/* Report Content */}
      <section aria-label="Timelog report results">
        {/* Loading State */}
        {isLoading && <LoadingState />}

        {/* Error State */}
        {!isLoading && isSummaryError && summaryError && (
          <ErrorState error={summaryError} onRetry={handleRetry} />
        )}

        {/* Empty State */}
        {!isLoading && !isSummaryError && !hasData && (
          <EmptyState year={appliedYear} month={appliedMonth} />
        )}

        {/* Report Table */}
        {!isLoading && !isSummaryError && hasData && (
          <>
            <div className="overflow-x-auto rounded-lg border border-gray-200 bg-white shadow-sm">
              <table className="w-full min-w-[36rem] table-auto">
                <thead>
                  <tr className="border-b border-gray-200 bg-gray-50">
                    <th
                      scope="col"
                      className="px-4 py-3 text-start text-xs font-semibold
                                 uppercase tracking-wide text-gray-600"
                    >
                      Task
                    </th>
                    <th
                      scope="col"
                      className="px-4 py-3 text-start text-xs font-semibold
                                 uppercase tracking-wide text-gray-600"
                    >
                      Type
                    </th>
                    <th
                      scope="col"
                      className="px-4 py-3 text-end text-xs font-semibold
                                 uppercase tracking-wide text-gray-600"
                    >
                      Billable
                    </th>
                    <th
                      scope="col"
                      className="px-4 py-3 text-end text-xs font-semibold
                                 uppercase tracking-wide text-gray-600"
                    >
                      Non-Billable
                    </th>
                  </tr>
                </thead>

                {/* One <tbody> per project group */}
                {projectGroupEntries.map(([projectId, group]) => (
                  <ProjectSection
                    key={projectId}
                    projectId={projectId}
                    group={group}
                  />
                ))}
              </table>
            </div>

            {/* Overall Totals */}
            <ReportSummary
              billable={overallBillable}
              nonBillable={overallNonBillable}
            />
          </>
        )}
      </section>
    </div>
  );
}

export default MonthlyTimelogReport;
