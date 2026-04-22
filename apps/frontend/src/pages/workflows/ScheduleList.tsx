/**
 * ScheduleList.tsx — Schedule Plan Listing Page
 *
 * Replaces the monolith's "Schedule Plans" page at /sdk/server/job/l/plan
 * from plan.cshtml + plan.cshtml.cs. Shows scheduled workflow triggers with
 * enabled/disabled status, schedule type (interval/daily/weekly/monthly),
 * last/next trigger times, and actions to edit, trigger, or view execution logs.
 *
 * Route: /workflows/schedules
 */

import { useState, useCallback, useMemo } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';

import {
  useSchedulePlans,
  useTriggerSchedulePlan,
  SchedulePlanType,
} from '../../hooks/useWorkflows';
import type { SchedulePlan } from '../../hooks/useWorkflows';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import ScreenMessage, { useToast } from '../../components/common/ScreenMessage';
import { ScreenMessageType } from '../../types/common';

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** Default page size matching monolith plan.cshtml.cs PagerSize = 15 */
const PAGE_SIZE = 15;

/** Short-month names used by the date formatter */
const MONTH_NAMES = [
  'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
] as const;

/** Sub-navigation items for the workflow admin section */
const SUB_NAV_ITEMS = [
  { label: 'Workflows', path: '/workflows', key: 'workflows' },
  { label: 'Executions', path: '/workflows/executions', key: 'executions' },
  { label: 'Schedules', path: '/workflows/schedules', key: 'schedules' },
] as const;

/* ------------------------------------------------------------------ */
/*  Helpers                                                            */
/* ------------------------------------------------------------------ */

/**
 * Formats an ISO date string to the "dd MMM yyyy HH:mm" pattern matching
 * the source monolith's display format. Returns "n/a" for null, undefined,
 * or invalid dates.
 */
function formatDate(dateString: string | null | undefined): string {
  if (!dateString) return 'n/a';
  const date = new Date(dateString);
  if (Number.isNaN(date.getTime())) return 'n/a';

  const day = String(date.getDate()).padStart(2, '0');
  const month = MONTH_NAMES[date.getMonth()];
  const year = date.getFullYear();
  const hours = String(date.getHours()).padStart(2, '0');
  const minutes = String(date.getMinutes()).padStart(2, '0');

  return `${day} ${month} ${year} ${hours}:${minutes}`;
}

/**
 * Returns a lowercase display label for a SchedulePlanType enum value.
 * Maps from SchedulePlan.cs enum: Interval=1, Daily=2, Weekly=3, Monthly=4.
 */
function getScheduleTypeLabel(type: SchedulePlanType): string {
  switch (type) {
    case SchedulePlanType.Interval:
      return 'interval';
    case SchedulePlanType.Daily:
      return 'daily';
    case SchedulePlanType.Weekly:
      return 'weekly';
    case SchedulePlanType.Monthly:
      return 'monthly';
    default:
      return 'unknown';
  }
}

/* ------------------------------------------------------------------ */
/*  Component                                                          */
/* ------------------------------------------------------------------ */

/**
 * ScheduleList — Schedule Plan Listing Page Component
 *
 * Renders a paginated, filterable data table of workflow schedule plans.
 * Supports client-side filtering by name (CONTAINS), type, and enabled
 * status — replicating the monolith's plan.cshtml.cs filtering logic.
 * Provides row-level actions to edit, trigger, and view executions.
 */
function ScheduleList() {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const queryClient = useQueryClient();

  /* ---- Toast notifications ---- */
  const { messages, showToast, dismissToast } = useToast();

  /* ---- Data fetching — all plans (client-side filter/sort/paginate) ---- */
  const {
    data: schedulePlanData,
    isLoading,
    isError,
    error,
  } = useSchedulePlans();

  /* ---- Trigger mutation ---- */
  const triggerMutation = useTriggerSchedulePlan();

  /* ---- Drawer state ---- */
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);

  /* Draft filter values (edited inside the drawer before "Apply") */
  const [draftFilterName, setDraftFilterName] = useState(
    searchParams.get('name') ?? '',
  );
  const [draftFilterType, setDraftFilterType] = useState(
    searchParams.get('type') ?? '',
  );
  const [draftFilterEnabled, setDraftFilterEnabled] = useState(
    searchParams.get('enabled') ?? '',
  );

  /* ---- Active filter values (committed to URL search params) ---- */
  const filterName = searchParams.get('name') ?? '';
  const filterType = searchParams.get('type') ?? '';
  const filterEnabled = searchParams.get('enabled') ?? '';
  const currentPage = Math.max(
    1,
    parseInt(searchParams.get('page') ?? '1', 10) || 1,
  );

  /* ---- Client-side filtering (plan.cshtml.cs lines 103-118) ---- */
  const filteredData = useMemo(() => {
    /* ApiResponse envelope: payload is in .object, not directly on data */
    const items = schedulePlanData?.object?.items ?? [];

    return items.filter((plan: SchedulePlan) => {
      // Name CONTAINS filter (case-insensitive)
      if (
        filterName &&
        !plan.name.toLowerCase().includes(filterName.toLowerCase())
      ) {
        return false;
      }
      // Type exact match filter
      if (filterType && plan.type !== Number(filterType)) {
        return false;
      }
      // Enabled filter
      if (filterEnabled === 'true' && !plan.enabled) return false;
      if (filterEnabled === 'false' && plan.enabled) return false;

      return true;
    });
  }, [schedulePlanData?.object?.items, filterName, filterType, filterEnabled]);

  /* ---- Sorting: Name asc, CreatedOn desc (plan.cshtml.cs line 99) ---- */
  const sortedData = useMemo(() => {
    return [...filteredData].sort((a, b) => {
      const nameCompare = a.name.localeCompare(b.name);
      if (nameCompare !== 0) return nameCompare;
      // Secondary sort: CreatedOn descending
      const aTime = new Date(a.createdOn).getTime();
      const bTime = new Date(b.createdOn).getTime();
      return bTime - aTime;
    });
  }, [filteredData]);

  /* ---- Pagination (page size 15) ---- */
  const paginatedData = useMemo(() => {
    const start = (currentPage - 1) * PAGE_SIZE;
    return sortedData.slice(start, start + PAGE_SIZE);
  }, [sortedData, currentPage]);

  /* ---- Active filter count (badge indicator) ---- */
  const activeFilterCount = useMemo(() => {
    let count = 0;
    if (filterName) count += 1;
    if (filterType) count += 1;
    if (filterEnabled) count += 1;
    return count;
  }, [filterName, filterType, filterEnabled]);

  /* ================================================================ */
  /*  Event handlers                                                   */
  /* ================================================================ */

  /** Trigger a schedule plan immediately. */
  const handleTrigger = useCallback(
    (planId: string, planName: string) => {
      triggerMutation.mutate(planId, {
        onSuccess: () => {
          showToast(
            ScreenMessageType.Success,
            'Success',
            `Schedule plan "${planName}" triggered successfully`,
          );
          // Explicit invalidation ensures the listing refreshes NextTriggerTime
          queryClient.invalidateQueries({ queryKey: ['schedule-plans'] });
        },
        onError: (err: unknown) => {
          const msg =
            err instanceof Error
              ? err.message
              : 'Failed to trigger schedule plan';
          showToast(ScreenMessageType.Error, 'Error', msg);
        },
      });
    },
    [triggerMutation, showToast, queryClient],
  );

  /** Apply draft filter values to URL search params. */
  const handleFilterApply = useCallback(() => {
    const params = new URLSearchParams();
    if (draftFilterName) params.set('name', draftFilterName);
    if (draftFilterType) params.set('type', draftFilterType);
    if (draftFilterEnabled) params.set('enabled', draftFilterEnabled);
    params.set('page', '1'); // Reset to first page on filter change
    setSearchParams(params);
    setIsDrawerOpen(false);
  }, [draftFilterName, draftFilterType, draftFilterEnabled, setSearchParams]);

  /** Reset all filters and URL search params. */
  const handleFilterClear = useCallback(() => {
    setDraftFilterName('');
    setDraftFilterType('');
    setDraftFilterEnabled('');
    setSearchParams(new URLSearchParams());
    setIsDrawerOpen(false);
  }, [setSearchParams]);

  /** Navigate to a new page number. */
  const handlePageChange = useCallback(
    (page: number) => {
      const params = new URLSearchParams(searchParams);
      params.set('page', String(page));
      setSearchParams(params);
    },
    [searchParams, setSearchParams],
  );

  /** Open filter drawer and sync drafts with current URL params. */
  const handleDrawerOpen = useCallback(() => {
    setDraftFilterName(filterName);
    setDraftFilterType(filterType);
    setDraftFilterEnabled(filterEnabled);
    setIsDrawerOpen(true);
  }, [filterName, filterType, filterEnabled]);

  /* ================================================================ */
  /*  Column definitions (6 columns matching plan.cshtml grid)         */
  /* ================================================================ */

  const columns = useMemo<DataTableColumn<SchedulePlan & Record<string, unknown>>[]>(
    () => [
      /* -- Actions (140px) -- pencil / trigger / executions ---------- */
      {
        id: 'actions',
        label: '',
        width: '140px',
        sortable: false,
        accessorFn: (record: SchedulePlan) => record.id,
        cell: (_value: unknown, record: SchedulePlan) => (
          <div className="flex items-center gap-1">
            {/* Edit — replaces pencil icon link at plan.cshtml line 28 */}
            <Link
              to={`/workflows/schedules/${record.id}/edit`}
              className="inline-flex items-center justify-center rounded p-1.5 text-gray-500 hover:bg-gray-100 hover:text-gray-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              title="Edit schedule plan"
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-4 w-4"
                aria-hidden="true"
              >
                <path d="M2.695 14.763l-1.262 3.154a.5.5 0 00.65.65l3.155-1.262a4 4 0 001.343-.885L17.5 5.5a2.121 2.121 0 00-3-3L3.58 13.42a4 4 0 00-.885 1.343z" />
              </svg>
              <span className="sr-only">Edit</span>
            </Link>

            {/* Trigger Now — replaces form at plan.cshtml lines 29,32-34 */}
            <button
              type="button"
              onClick={() => handleTrigger(record.id, record.name)}
              disabled={triggerMutation.isPending}
              className="inline-flex items-center justify-center rounded p-1.5 text-gray-500 hover:bg-blue-50 hover:text-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
              title="Trigger now"
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-4 w-4"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M2 10a8 8 0 1116 0 8 8 0 01-16 0zm6.39-2.908a.75.75 0 01.766.027l3.5 2.25a.75.75 0 010 1.262l-3.5 2.25A.75.75 0 018 12.25v-4.5a.75.75 0 01.39-.658z"
                  clipRule="evenodd"
                />
              </svg>
              <span className="sr-only">Trigger now</span>
            </button>

            {/* View Executions — replaces link at plan.cshtml line 30 */}
            <Link
              to={`/workflows/executions?workflowId=${record.id}`}
              className="inline-flex items-center justify-center rounded p-1.5 text-gray-500 hover:bg-gray-100 hover:text-gray-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              title="View executions"
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-4 w-4"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M6 4.75A.75.75 0 016.75 4h10.5a.75.75 0 010 1.5H6.75A.75.75 0 016 4.75zM6 10a.75.75 0 01.75-.75h10.5a.75.75 0 010 1.5H6.75A.75.75 0 016 10zm0 5.25a.75.75 0 01.75-.75h10.5a.75.75 0 010 1.5H6.75a.75.75 0 01-.75-.75zM1.99 4.75a1 1 0 011-1H3a1 1 0 011 1v.01a1 1 0 01-1 1h-.01a1 1 0 01-1-1v-.01zM1.99 15.25a1 1 0 011-1H3a1 1 0 011 1v.01a1 1 0 01-1 1h-.01a1 1 0 01-1-1v-.01zM1.99 10a1 1 0 011-1H3a1 1 0 011 1v.01a1 1 0 01-1 1h-.01a1 1 0 01-1-1V10z"
                  clipRule="evenodd"
                />
              </svg>
              <span className="sr-only">View executions</span>
            </Link>
          </div>
        ),
      },

      /* -- Status (30px) -- ON green / OFF red badges ----------------- */
      {
        id: 'status',
        label: 'Status',
        width: '30px',
        sortable: false,
        accessorKey: 'enabled',
        cell: (value: unknown) => {
          const isEnabled = value as boolean;
          return isEnabled ? (
            <span className="inline-flex items-center rounded-full bg-green-100 px-2 py-0.5 text-xs font-semibold text-green-800">
              ON
            </span>
          ) : (
            <span className="inline-flex items-center rounded-full bg-red-100 px-2 py-0.5 text-xs font-semibold text-red-800">
              OFF
            </span>
          );
        },
      },

      /* -- Name — primary text + start/end date subtitle -------------- */
      {
        id: 'name',
        label: 'Name',
        sortable: true,
        accessorKey: 'name',
        cell: (_value: unknown, record: SchedulePlan) => (
          <div>
            <div className="font-medium text-gray-900">{record.name}</div>
            <div className="text-xs text-gray-400">
              <span>start date: {formatDate(record.startDate)}</span>
              <span className="mx-1">|</span>
              <span>end date: {formatDate(record.endDate)}</span>
            </div>
          </div>
        ),
      },

      /* -- Type (100px) — interval / daily / weekly / monthly --------- */
      {
        id: 'type',
        label: 'Type',
        width: '100px',
        sortable: true,
        accessorKey: 'type',
        cell: (value: unknown) => (
          <span className="capitalize text-gray-700">
            {getScheduleTypeLabel(value as SchedulePlanType)}
          </span>
        ),
      },

      /* -- Last Trigger (140px) --------------------------------------- */
      {
        id: 'lastTrigger',
        label: 'Last Trigger',
        width: '140px',
        sortable: true,
        accessorKey: 'lastTriggerTime',
        cell: (value: unknown) => (
          <span className="text-sm text-gray-700">
            {formatDate(value as string | null)}
          </span>
        ),
      },

      /* -- Next Trigger (140px) --------------------------------------- */
      {
        id: 'nextTrigger',
        label: 'Next Trigger',
        width: '140px',
        sortable: true,
        accessorKey: 'nextTriggerTime',
        cell: (value: unknown) => (
          <span className="text-sm text-gray-700">
            {formatDate(value as string | null)}
          </span>
        ),
      },
    ],
    [handleTrigger, triggerMutation.isPending],
  );

  /* ================================================================ */
  /*  Error state                                                      */
  /* ================================================================ */

  if (isError) {
    return (
      <div className="p-6">
        <ScreenMessage messages={messages} onDismiss={dismissToast} />
        <div className="rounded-lg border border-red-200 bg-red-50 p-4">
          <h3 className="text-sm font-medium text-red-800">
            Error loading schedule plans
          </h3>
          <p className="mt-1 text-sm text-red-700">
            {error instanceof Error
              ? error.message
              : 'An unexpected error occurred.'}
          </p>
        </div>
      </div>
    );
  }

  /* ================================================================ */
  /*  Render                                                           */
  /* ================================================================ */

  return (
    <div className="flex flex-col gap-6 p-6">
      {/* Toast notification container (portaled to top-center) */}
      <ScreenMessage messages={messages} onDismiss={dismissToast} />

      {/* ---- Sub-navigation tabs ----------------------------------- */}
      {/* Replaces AdminPageUtils.GetJobAdminSubNav("plan") */}
      <nav aria-label="Workflow administration" className="border-b border-gray-200">
        <ul className="-mb-px flex gap-0" role="tablist">
          {SUB_NAV_ITEMS.map((item) => (
            <li key={item.key} role="presentation">
              <Link
                to={item.path}
                role="tab"
                aria-selected={item.key === 'schedules'}
                className={`inline-block border-b-2 px-4 py-2 text-sm font-medium transition-colors ${
                  item.key === 'schedules'
                    ? 'border-blue-500 text-blue-600'
                    : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700'
                }`}
              >
                {item.label}
              </Link>
            </li>
          ))}
        </ul>
      </nav>

      {/* ---- Page header ------------------------------------------- */}
      {/* Replaces <wv-page-header> from plan.cshtml lines 9-20 */}
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-3">
          {/* Calendar icon (replaces far fa-calendar-alt) */}
          <div className="flex h-10 w-10 items-center justify-center rounded-lg bg-blue-50 text-blue-600">
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-5 w-5"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M5.75 2a.75.75 0 01.75.75V4h7V2.75a.75.75 0 011.5 0V4h.25A2.75 2.75 0 0118 6.75v8.5A2.75 2.75 0 0115.25 18H4.75A2.75 2.75 0 012 15.25v-8.5A2.75 2.75 0 014.75 4H5V2.75A.75.75 0 015.75 2zm-1 5.5c-.69 0-1.25.56-1.25 1.25v6.5c0 .69.56 1.25 1.25 1.25h10.5c.69 0 1.25-.56 1.25-1.25v-6.5c0-.69-.56-1.25-1.25-1.25H4.75z"
                clipRule="evenodd"
              />
            </svg>
          </div>

          <div>
            {/* Breadcrumb: Workflows > Schedules */}
            <nav aria-label="Breadcrumb" className="text-xs text-gray-500">
              <ol className="flex items-center gap-1">
                <li>
                  <Link to="/workflows" className="hover:text-gray-700">
                    Workflows
                  </Link>
                </li>
                <li aria-hidden="true" className="select-none">
                  /
                </li>
                <li className="text-gray-700" aria-current="page">
                  Schedules
                </li>
              </ol>
            </nav>
            <h1 className="text-xl font-semibold text-gray-900">
              Schedule Plans
            </h1>
          </div>
        </div>

        {/* Header actions */}
        <div className="flex items-center gap-2">
          {/* Search / filter button */}
          <button
            type="button"
            onClick={handleDrawerOpen}
            className="relative inline-flex items-center gap-2 rounded-lg border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
                clipRule="evenodd"
              />
            </svg>
            Search
            {activeFilterCount > 0 && (
              <span className="inline-flex h-5 w-5 items-center justify-center rounded-full bg-blue-600 text-xs font-medium text-white">
                {activeFilterCount}
              </span>
            )}
          </button>

          {/* Create Schedule button */}
          <button
            type="button"
            onClick={() => navigate('/workflows/schedules/create')}
            className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
            </svg>
            Create Schedule
          </button>
        </div>
      </div>

      {/* ---- Data Table --------------------------------------------- */}
      {/* Replaces <wv-grid> from plan.cshtml lines 22-73 */}
      <DataTable<SchedulePlan & Record<string, unknown>>
        data={paginatedData as (SchedulePlan & Record<string, unknown>)[]}
        columns={columns}
        totalCount={sortedData.length}
        pageSize={PAGE_SIZE}
        currentPage={currentPage}
        onPageChange={handlePageChange}
        loading={isLoading}
        emptyText="No schedule plans found"
        bordered
        hover
        responsiveBreakpoint="md"
      />

      {/* ---- Filter Drawer ----------------------------------------- */}
      {/* Replaces <wv-drawer> from plan.cshtml lines 78-80 */}
      <Drawer
        isVisible={isDrawerOpen}
        onClose={() => setIsDrawerOpen(false)}
        title="Filter Schedule Plans"
        width="400px"
      >
        <div className="flex flex-col gap-4">
          {/* Name filter — CONTAINS (plan.cshtml.cs lines 103-118) */}
          <div>
            <label
              htmlFor="filter-name"
              className="block text-sm font-medium text-gray-700"
            >
              Name
            </label>
            <input
              id="filter-name"
              type="text"
              value={draftFilterName}
              onChange={(e) => setDraftFilterName(e.target.value)}
              placeholder="Filter by name…"
              className="mt-1 block w-full rounded-lg border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* Type filter — SchedulePlanType select */}
          <div>
            <label
              htmlFor="filter-type"
              className="block text-sm font-medium text-gray-700"
            >
              Type
            </label>
            <select
              id="filter-type"
              value={draftFilterType}
              onChange={(e) => setDraftFilterType(e.target.value)}
              className="mt-1 block w-full rounded-lg border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            >
              <option value="">All types</option>
              <option value={String(SchedulePlanType.Interval)}>Interval</option>
              <option value={String(SchedulePlanType.Daily)}>Daily</option>
              <option value={String(SchedulePlanType.Weekly)}>Weekly</option>
              <option value={String(SchedulePlanType.Monthly)}>Monthly</option>
            </select>
          </div>

          {/* Enabled filter — ON / OFF status */}
          <div>
            <label
              htmlFor="filter-enabled"
              className="block text-sm font-medium text-gray-700"
            >
              Status
            </label>
            <select
              id="filter-enabled"
              value={draftFilterEnabled}
              onChange={(e) => setDraftFilterEnabled(e.target.value)}
              className="mt-1 block w-full rounded-lg border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            >
              <option value="">All</option>
              <option value="true">Enabled (ON)</option>
              <option value="false">Disabled (OFF)</option>
            </select>
          </div>

          {/* Action buttons */}
          <div className="flex items-center gap-2 pt-2">
            <button
              type="button"
              onClick={handleFilterApply}
              className="flex-1 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
            >
              Apply
            </button>
            <button
              type="button"
              onClick={handleFilterClear}
              className="flex-1 rounded-lg border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
            >
              Clear All
            </button>
          </div>
        </div>
      </Drawer>
    </div>
  );
}

export default ScheduleList;
