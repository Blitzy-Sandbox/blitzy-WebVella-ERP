import { useState, useCallback, useMemo, type ReactNode } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import {
  useSchedulePlans,
  useUpdateSchedulePlan,
  useTriggerSchedulePlan,
  SchedulePlanType,
} from '../../hooks/useWorkflows';
import type { SchedulePlan } from '../../hooks/useWorkflows';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import Modal, { ModalSize } from '../../components/common/Modal';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';

/**
 * Week day keys matching the SchedulePlan.scheduledDays Record<string, boolean> shape.
 * Order follows ISO week numbering (Monday first) matching the monolith's
 * ScheduledOnMonday..ScheduledOnSunday boolean properties.
 */
const WEEKDAY_KEYS = [
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
  'Sunday',
] as const;

/**
 * Returns a human-readable label for a SchedulePlanType enum value.
 * Matches the monolith's GetLabel() helper from plan.cshtml.cs.
 */
function getSchedulePlanTypeLabel(type: SchedulePlanType): string {
  switch (type) {
    case SchedulePlanType.Interval:
      return 'Interval';
    case SchedulePlanType.Daily:
      return 'Daily';
    case SchedulePlanType.Weekly:
      return 'Weekly';
    case SchedulePlanType.Monthly:
      return 'Monthly';
    default:
      return 'Unknown';
  }
}

/**
 * Formats a date string (ISO 8601) into a locale-friendly display string.
 * Returns empty string for null/undefined/empty values.
 */
function formatDateTime(value: string | null | undefined): string {
  if (!value) return '';
  try {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '';
    return date.toLocaleString(undefined, {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    });
  } catch {
    return '';
  }
}

/**
 * Formats a date string into date-only display (no time component).
 */
function formatDateOnly(value: string | null | undefined): string {
  if (!value) return '';
  try {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '';
    return date.toLocaleDateString(undefined, {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    });
  } catch {
    return '';
  }
}

/**
 * Normalises a timespan value (string) to HH:MM format for an HTML time input.
 *
 * The API may return the value in several shapes:
 *  - "HH:MM" or "HH:MM:SS" — already formatted, truncate to HH:MM
 *  - A numeric string like "540" representing minutes-from-midnight
 *  - null / undefined / empty — return empty string
 *
 * Matches the monolith's plan-manage.cshtml.cs conversion where
 * EndTimespan of 0 is treated as 1440 (midnight end-of-day).
 */
function normaliseTimespanToTimeInput(value: string | null | undefined): string {
  if (value === null || value === undefined || value === '') return '';
  // If it contains ":", assume time format — keep HH:MM portion
  if (value.includes(':')) {
    const parts = value.split(':');
    return `${(parts[0] ?? '00').padStart(2, '0')}:${(parts[1] ?? '00').padStart(2, '0')}`;
  }
  // Otherwise assume numeric minutes string
  const numericMinutes = parseInt(value, 10);
  if (Number.isNaN(numericMinutes)) return '';
  const hours = Math.floor(numericMinutes / 60);
  const mins = numericMinutes % 60;
  return `${String(hours).padStart(2, '0')}:${String(mins).padStart(2, '0')}`;
}

/**
 * Converts a Date-compatible string to an HTML datetime-local input value.
 */
function toDateTimeLocalValue(value: string | null | undefined): string {
  if (!value) return '';
  try {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '';
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    const hours = String(date.getHours()).padStart(2, '0');
    const minutes = String(date.getMinutes()).padStart(2, '0');
    return `${year}-${month}-${day}T${hours}:${minutes}`;
  } catch {
    return '';
  }
}

/** Shape of the edit form state for schedule plan editing */
interface EditFormState {
  enabled: boolean;
  name: string;
  startDate: string;
  endDate: string;
  type: SchedulePlanType;
  intervalInMinutes: number;
  scheduledDays: Record<string, boolean>;
  startTimespan: string;
  endTimespan: string;
}

/**
 * SchedulePlanList — Schedule Plan List/Manage/Trigger Page
 *
 * React page replacing both plan.cshtml[.cs] (listing) and plan-manage.cshtml[.cs] (edit).
 * Combined schedule plan list with inline manage via modal and trigger.
 * Route: /admin/schedule-plans
 */
export default function SchedulePlanList(): ReactNode {
  const navigate = useNavigate();

  /* ── TanStack Query hooks ─────────────────────────────────── */
  const { data: schedulePlansData, isLoading, isError, error } = useSchedulePlans();
  const updateMutation = useUpdateSchedulePlan();
  const triggerMutation = useTriggerSchedulePlan();

  /* ── Local UI state ───────────────────────────────────────── */
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);
  const [isEditModalOpen, setIsEditModalOpen] = useState(false);
  const [selectedPlan, setSelectedPlan] = useState<SchedulePlan | null>(null);
  const [nameFilter, setNameFilter] = useState('');
  const [triggerConfirmId, setTriggerConfirmId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<EditFormState>({
    enabled: false,
    name: '',
    startDate: '',
    endDate: '',
    type: SchedulePlanType.Interval,
    intervalInMinutes: 0,
    scheduledDays: {},
    startTimespan: '',
    endTimespan: '',
  });
  const [editErrors, setEditErrors] = useState<string[]>([]);

  /* ── Sub-nav tab configuration ────────────────────────────── */
  const subNavTabs: TabConfig[] = useMemo(
    () => [
      { id: 'jobs', label: 'Jobs' },
      { id: 'schedule-plans', label: 'Schedule Plans' },
    ],
    [],
  );

  const handleTabChange = useCallback(
    (tabId: string) => {
      if (tabId === 'jobs') {
        navigate('/admin/jobs');
      }
    },
    [navigate],
  );

  /* ── Drawer handlers ──────────────────────────────────────── */
  const handleDrawerToggle = useCallback(() => {
    setIsDrawerOpen((prev) => !prev);
  }, []);

  const handleFilterClear = useCallback(() => {
    setNameFilter('');
  }, []);

  /* ── Trigger handlers ─────────────────────────────────────── */
  const handleTriggerClick = useCallback((planId: string) => {
    setTriggerConfirmId(planId);
  }, []);

  const handleTriggerConfirm = useCallback(() => {
    if (!triggerConfirmId) return;
    triggerMutation.mutate(triggerConfirmId, {
      onSettled: () => {
        setTriggerConfirmId(null);
      },
    });
  }, [triggerConfirmId, triggerMutation]);

  const handleTriggerCancel = useCallback(() => {
    setTriggerConfirmId(null);
  }, []);

  /* ── Edit modal handlers ──────────────────────────────────── */
  const handleEditClick = useCallback((plan: SchedulePlan) => {
    setSelectedPlan(plan);
    setEditErrors([]);
    setEditForm({
      enabled: plan.enabled ?? false,
      name: plan.name ?? '',
      startDate: toDateTimeLocalValue(plan.startDate),
      endDate: toDateTimeLocalValue(plan.endDate),
      type: plan.type ?? SchedulePlanType.Interval,
      intervalInMinutes: plan.intervalInMinutes ?? 0,
      scheduledDays: plan.scheduledDays ? { ...plan.scheduledDays } : {},
      startTimespan: normaliseTimespanToTimeInput(plan.startTimespan),
      endTimespan: normaliseTimespanToTimeInput(plan.endTimespan),
    });
    setIsEditModalOpen(true);
  }, []);

  const handleModalClose = useCallback(() => {
    setIsEditModalOpen(false);
    setSelectedPlan(null);
    setEditErrors([]);
  }, []);

  const handleWeekdayToggle = useCallback((day: string) => {
    setEditForm((prev) => ({
      ...prev,
      scheduledDays: {
        ...prev.scheduledDays,
        [day]: !prev.scheduledDays[day],
      },
    }));
  }, []);

  /**
   * Validates the edit form matching the monolith's plan-manage.cshtml.cs OnPost validation:
   * - Name is required
   * - If StartDate and EndDate both set, StartDate must be < EndDate
   * - For Daily/Interval types, at least one day must be selected
   * - For Interval type, IntervalInMinutes must be 1-1440
   */
  const validateEditForm = useCallback((): string[] => {
    const errors: string[] = [];
    if (!editForm.name.trim()) {
      errors.push('Name is required.');
    }
    if (editForm.startDate && editForm.endDate) {
      const start = new Date(editForm.startDate);
      const end = new Date(editForm.endDate);
      if (start >= end) {
        errors.push('Start Date must be before End Date.');
      }
    }
    if (
      editForm.type === SchedulePlanType.Daily ||
      editForm.type === SchedulePlanType.Interval
    ) {
      const hasDay = Object.values(editForm.scheduledDays).some(Boolean);
      if (!hasDay) {
        errors.push('At least one day must be selected for this schedule type.');
      }
    }
    if (editForm.type === SchedulePlanType.Interval) {
      if (editForm.intervalInMinutes < 1 || editForm.intervalInMinutes > 1440) {
        errors.push('Interval must be between 1 and 1440 minutes.');
      }
    }
    return errors;
  }, [editForm]);

  const handleSave = useCallback(() => {
    const errors = validateEditForm();
    if (errors.length > 0) {
      setEditErrors(errors);
      return;
    }
    if (!selectedPlan) return;

    updateMutation.mutate(
      {
        id: selectedPlan.id,
        enabled: editForm.enabled,
        name: editForm.name.trim(),
        startDate: editForm.startDate
          ? new Date(editForm.startDate).toISOString()
          : undefined,
        endDate: editForm.endDate
          ? new Date(editForm.endDate).toISOString()
          : undefined,
        type: editForm.type,
        intervalInMinutes: editForm.intervalInMinutes,
        scheduledDays: editForm.scheduledDays,
        startTimespan: editForm.startTimespan || undefined,
        endTimespan: editForm.endTimespan || undefined,
      },
      {
        onSuccess: () => {
          handleModalClose();
        },
      },
    );
  }, [selectedPlan, editForm, updateMutation, validateEditForm, handleModalClose]);

  /* ── Filtered data ────────────────────────────────────────── */
  const filteredPlans = useMemo(() => {
    const items = schedulePlansData?.items ?? [];
    if (!nameFilter.trim()) return items;
    const lower = nameFilter.toLowerCase();
    return items.filter((plan) => (plan.name ?? '').toLowerCase().includes(lower));
  }, [schedulePlansData, nameFilter]);

  /* ── DataTable column definitions (6 columns matching source) */
  const columns: DataTableColumn<SchedulePlan>[] = useMemo(
    () => [
      {
        id: 'actions',
        label: '',
        width: '140px',
        sortable: false,
        cell: (_value: unknown, record: SchedulePlan): ReactNode => (
          <div className="flex items-center gap-1">
            {/* Edit button */}
            <button
              type="button"
              className="inline-flex items-center justify-center rounded border border-gray-300 bg-white px-2 py-1 text-xs text-gray-700 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              onClick={() => handleEditClick(record)}
              aria-label={`Edit schedule plan ${record.name}`}
              title="Edit"
            >
              <svg
                className="h-3.5 w-3.5"
                fill="currentColor"
                viewBox="0 0 512 512"
                aria-hidden="true"
              >
                <path d="M362.7 19.3L314.3 67.7 444.3 197.7l48.4-48.4c25-25 25-65.5 0-90.5L453.3 19.3c-25-25-65.5-25-90.5 0zm-71 71L58.6 323.5c-10.4 10.4-18 23.3-22.2 37.4L1 481.2C-1.5 489.7 .8 498.8 7 505s15.3 8.5 23.7 6.1l120.3-35.4c14.1-4.2 27-11.8 37.4-22.2L421.7 220.3 291.7 90.3z" />
              </svg>
            </button>
            {/* Trigger button */}
            <button
              type="button"
              className="inline-flex items-center justify-center rounded border border-gray-300 bg-white px-2 py-1 text-xs text-gray-700 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              onClick={() => handleTriggerClick(record.id)}
              aria-label={`Trigger schedule plan ${record.name}`}
              title="Trigger Now"
            >
              <svg
                className="h-3.5 w-3.5"
                fill="currentColor"
                viewBox="0 0 384 512"
                aria-hidden="true"
              >
                <path d="M73 39c-14.8-9.1-33.4-9.4-48.5-.9S0 62.6 0 80V432c0 17.4 9.4 33.4 24.5 41.9s33.7 8.1 48.5-.9L361 297c14.3-8.8 23-24.2 23-41s-8.7-32.2-23-41L73 39z" />
              </svg>
            </button>
            {/* View job logs link */}
            <Link
              to={`/admin/jobs?q_type_id_t=EQ&q_type_id_v=${record.id}`}
              className="inline-flex items-center justify-center rounded border border-gray-300 bg-white px-2 py-1 text-xs text-gray-700 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              aria-label={`View job logs for ${record.name}`}
              title="View job logs"
            >
              <svg
                className="h-3.5 w-3.5"
                fill="currentColor"
                viewBox="0 0 512 512"
                aria-hidden="true"
              >
                <path d="M498.1 5.6c10.1 7 15.4 19.1 13.5 31.2l-64 416c-1.5 9.7-7.4 18.2-16 23s-18.9 5.4-28 1.6L284 427.7l-68.5 74.1c-8.9 9.7-22.9 12.9-35.2 8.1S160 492.3 160 480V396.4c0-4 1.5-7.8 4.2-10.7L331.8 202.8c5.8-6.3 5.4-16-.9-21.9s-16.6-5.4-22.4.8L124.3 368.6 31.6 325.2c-9.6-4.5-15.7-14.1-15.7-24.7c0-10.1 5.5-19.4 14.4-24.3L477 5.6c9.5-5.3 21.2-5.3 30.7 0z" />
              </svg>
            </Link>
          </div>
        ),
      },
      {
        id: 'enabled',
        label: '',
        width: '30px',
        sortable: false,
        cell: (_value: unknown, record: SchedulePlan): ReactNode => (
          <span
            className={`inline-block rounded px-2 py-0.5 text-xs font-semibold ${
              record.enabled
                ? 'bg-green-100 text-green-800'
                : 'bg-red-100 text-red-800'
            }`}
          >
            {record.enabled ? 'ON' : 'OFF'}
          </span>
        ),
      },
      {
        id: 'name',
        label: 'Name',
        sortable: true,
        accessorKey: 'name',
        cell: (_value: unknown, record: SchedulePlan): ReactNode => (
          <div>
            <span className="font-medium text-gray-900">{record.name}</span>
            {(record.startDate || record.endDate) && (
              <div className="mt-0.5 text-xs text-gray-500">
                {record.startDate && (
                  <span>Start: {formatDateOnly(record.startDate)}</span>
                )}
                {record.startDate && record.endDate && <span> &bull; </span>}
                {record.endDate && (
                  <span>End: {formatDateOnly(record.endDate)}</span>
                )}
              </div>
            )}
          </div>
        ),
      },
      {
        id: 'type',
        label: 'Type',
        width: '100px',
        sortable: false,
        cell: (_value: unknown, record: SchedulePlan): ReactNode => (
          <span className="text-sm text-gray-700">
            {getSchedulePlanTypeLabel(record.type)}
          </span>
        ),
      },
      {
        id: 'lastTriggerTime',
        label: 'Last Trigger',
        width: '140px',
        sortable: false,
        accessorKey: 'lastTriggerTime',
        cell: (_value: unknown, record: SchedulePlan): ReactNode => (
          <span className="text-sm text-gray-600">
            {formatDateTime(record.lastTriggerTime)}
          </span>
        ),
      },
      {
        id: 'nextTriggerTime',
        label: 'Next Trigger',
        width: '140px',
        sortable: false,
        accessorKey: 'nextTriggerTime',
        cell: (_value: unknown, record: SchedulePlan): ReactNode => (
          <span className="text-sm text-gray-600">
            {formatDateTime(record.nextTriggerTime)}
          </span>
        ),
      },
    ],
    [handleEditClick, handleTriggerClick],
  );

  /* ── Render ───────────────────────────────────────────────── */
  return (
    <div className="flex min-h-full flex-col">
      {/* Page header */}
      <header className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-2">
          <svg
            className="h-5 w-5 text-red-600"
            fill="currentColor"
            viewBox="0 0 448 512"
            aria-hidden="true"
          >
            <path d="M152 24c0-13.3-10.7-24-24-24s-24 10.7-24 24V64H64C28.7 64 0 92.7 0 128v16 48V448c0 35.3 28.7 64 64 64H384c35.3 0 64-28.7 64-64V192 144 128c0-35.3-28.7-64-64-64H344V24c0-13.3-10.7-24-24-24s-24 10.7-24 24V64H152V24zM48 192H400V448c0 8.8-7.2 16-16 16H64c-8.8 0-16-7.2-16-16V192z" />
          </svg>
          <h1 className="text-xl font-semibold text-gray-900">Schedule Plans</h1>
        </div>
        <div className="flex items-center gap-2">
          <button
            type="button"
            className="inline-flex items-center gap-1.5 rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
            onClick={handleDrawerToggle}
          >
            <svg className="h-4 w-4" fill="currentColor" viewBox="0 0 512 512" aria-hidden="true">
              <path d="M416 208c0 45.9-14.9 88.3-40 122.7L502.6 457.4c12.5 12.5 12.5 32.8 0 45.3s-32.8 12.5-45.3 0L330.7 376c-34.4 25.2-76.8 40-122.7 40C93.1 416 0 322.9 0 208S93.1 0 208 0S416 93.1 416 208zM208 352a144 144 0 1 0 0-288 144 144 0 1 0 0 288z" />
            </svg>
            Search schedule plans
          </button>
        </div>
      </header>

      {/* Sub-navigation tabs */}
      <div className="mb-4">
        <TabNav
          tabs={subNavTabs}
          activeTabId="schedule-plans"
          onTabChange={handleTabChange}
        />
      </div>

      {/* Error state */}
      {isError && (
        <div
          className="mb-4 rounded border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700"
          role="alert"
        >
          {(error as Error)?.message || 'Failed to load schedule plans.'}
        </div>
      )}

      {/* Data table */}
      <DataTable<SchedulePlan>
        data={filteredPlans}
        columns={columns}
        totalCount={filteredPlans.length}
        pageSize={15}
        bordered
        hover
        loading={isLoading}
        emptyText="No schedule plans found"
      />

      {/* Search drawer */}
      <Drawer
        isVisible={isDrawerOpen}
        width="400px"
        title="Search Schedule Plans"
        titleAction={
          <button
            type="button"
            className="text-sm text-blue-600 hover:text-blue-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
            onClick={handleFilterClear}
          >
            clear all
          </button>
        }
        onClose={handleDrawerToggle}
      >
        <div className="flex flex-col gap-4 p-4">
          <div>
            <label
              htmlFor="filter-name"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Name <span className="text-xs text-gray-400">(contains)</span>
            </label>
            <input
              id="filter-name"
              type="text"
              className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              placeholder="Search by name..."
              value={nameFilter}
              onChange={(e) => setNameFilter(e.target.value)}
            />
          </div>
        </div>
      </Drawer>

      {/* Trigger confirmation modal */}
      <Modal
        isVisible={triggerConfirmId !== null}
        title="Trigger Schedule Plan"
        onClose={handleTriggerCancel}
      >
        <div className="p-4">
          <p className="text-sm text-gray-700">
            Are you sure you want to trigger this schedule plan now?
          </p>
        </div>
        <div className="flex justify-end gap-2 border-t border-gray-200 px-4 py-3">
          <button
            type="button"
            className="rounded border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
            onClick={handleTriggerCancel}
          >
            Cancel
          </button>
          <button
            type="button"
            className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500 disabled:opacity-50"
            onClick={handleTriggerConfirm}
            disabled={triggerMutation.isPending}
          >
            {triggerMutation.isPending ? 'Triggering...' : 'Trigger Now'}
          </button>
        </div>
      </Modal>

      {/* Edit modal — replaces monolith's separate plan-manage.cshtml page */}
      <Modal
        isVisible={isEditModalOpen}
        title={`Edit Schedule Plan: ${selectedPlan?.name ?? ''}`}
        size={ModalSize.Large}
        onClose={handleModalClose}
      >
        <div className="max-h-[70vh] overflow-y-auto p-4">
          {/* Validation errors */}
          {editErrors.length > 0 && (
            <div
              className="mb-4 rounded border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700"
              role="alert"
            >
              <ul className="list-inside list-disc">
                {editErrors.map((err) => (
                  <li key={err}>{err}</li>
                ))}
              </ul>
            </div>
          )}

          {/* Mutation error */}
          {updateMutation.isError && (
            <div
              className="mb-4 rounded border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700"
              role="alert"
            >
              {(updateMutation.error as Error)?.message || 'Failed to update schedule plan.'}
            </div>
          )}

          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            {/* Enabled checkbox */}
            <div className="col-span-full flex items-center gap-2">
              <input
                id="edit-enabled"
                type="checkbox"
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                checked={editForm.enabled}
                onChange={(e) =>
                  setEditForm((prev) => ({ ...prev, enabled: e.target.checked }))
                }
              />
              <label htmlFor="edit-enabled" className="text-sm font-medium text-gray-700">
                Enabled
              </label>
            </div>

            {/* Id (read-only) */}
            <div>
              <label className="mb-1 block text-sm font-medium text-gray-500">Id</label>
              <div className="rounded bg-gray-100 px-3 py-2 text-sm text-gray-600">
                {selectedPlan?.id ?? ''}
              </div>
            </div>

            {/* Name (required) */}
            <div>
              <label
                htmlFor="edit-name"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Name <span className="text-red-500">*</span>
              </label>
              <input
                id="edit-name"
                type="text"
                required
                className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                value={editForm.name}
                onChange={(e) =>
                  setEditForm((prev) => ({ ...prev, name: e.target.value }))
                }
              />
            </div>

            {/* Job Type Name (read-only) */}
            <div>
              <label className="mb-1 block text-sm font-medium text-gray-500">
                Job Type Name
              </label>
              <div className="rounded bg-gray-100 px-3 py-2 text-sm text-gray-600">
                {selectedPlan?.jobTypeName ?? ''}
              </div>
            </div>

            {/* Next Trigger Time (read-only) */}
            <div>
              <label className="mb-1 block text-sm font-medium text-gray-500">
                Next Trigger Time
              </label>
              <div className="rounded bg-gray-100 px-3 py-2 text-sm text-gray-600">
                {formatDateTime(selectedPlan?.nextTriggerTime)}
              </div>
            </div>

            {/* Start Date */}
            <div>
              <label
                htmlFor="edit-start-date"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Start Date
              </label>
              <input
                id="edit-start-date"
                type="datetime-local"
                className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                value={editForm.startDate}
                onChange={(e) =>
                  setEditForm((prev) => ({ ...prev, startDate: e.target.value }))
                }
              />
            </div>

            {/* End Date */}
            <div>
              <label
                htmlFor="edit-end-date"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                End Date
              </label>
              <input
                id="edit-end-date"
                type="datetime-local"
                className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                value={editForm.endDate}
                onChange={(e) =>
                  setEditForm((prev) => ({ ...prev, endDate: e.target.value }))
                }
              />
            </div>

            {/* Type select */}
            <div>
              <label
                htmlFor="edit-type"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Type
              </label>
              <select
                id="edit-type"
                className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                value={editForm.type}
                onChange={(e) =>
                  setEditForm((prev) => ({
                    ...prev,
                    type: Number(e.target.value) as SchedulePlanType,
                  }))
                }
              >
                <option value={SchedulePlanType.Interval}>Interval</option>
                <option value={SchedulePlanType.Daily}>Daily</option>
                <option value={SchedulePlanType.Weekly}>Weekly</option>
                <option value={SchedulePlanType.Monthly}>Monthly</option>
              </select>
            </div>

            {/* Interval in minutes (only relevant for Interval type) */}
            <div>
              <label
                htmlFor="edit-interval"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Interval
              </label>
              <div className="flex items-center gap-2">
                <input
                  id="edit-interval"
                  type="number"
                  min={1}
                  max={1440}
                  className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                  value={editForm.intervalInMinutes}
                  onChange={(e) =>
                    setEditForm((prev) => ({
                      ...prev,
                      intervalInMinutes: parseInt(e.target.value, 10) || 0,
                    }))
                  }
                />
                <span className="text-sm text-gray-500">minutes</span>
              </div>
            </div>

            {/* Scheduled Days checkboxes */}
            <div className="col-span-full">
              <label className="mb-2 block text-sm font-medium text-gray-700">
                Scheduled Days
              </label>
              <div className="flex flex-wrap gap-3">
                {WEEKDAY_KEYS.map((day) => (
                  <label
                    key={day}
                    className="flex items-center gap-1.5 text-sm text-gray-700"
                  >
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                      checked={editForm.scheduledDays[day] ?? false}
                      onChange={() => handleWeekdayToggle(day)}
                    />
                    {day}
                  </label>
                ))}
              </div>
            </div>

            {/* Start Timespan (time picker) */}
            <div>
              <label
                htmlFor="edit-start-time"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Start Time of Day
              </label>
              <input
                id="edit-start-time"
                type="time"
                className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                value={editForm.startTimespan}
                onChange={(e) =>
                  setEditForm((prev) => ({ ...prev, startTimespan: e.target.value }))
                }
              />
            </div>

            {/* End Timespan (time picker) */}
            <div>
              <label
                htmlFor="edit-end-time"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                End Time of Day
              </label>
              <input
                id="edit-end-time"
                type="time"
                className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                value={editForm.endTimespan}
                onChange={(e) =>
                  setEditForm((prev) => ({ ...prev, endTimespan: e.target.value }))
                }
              />
            </div>
          </div>
        </div>

        {/* Modal footer — save / cancel */}
        <div className="flex justify-end gap-2 border-t border-gray-200 px-4 py-3">
          <button
            type="button"
            className="rounded border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
            onClick={handleModalClose}
          >
            Cancel
          </button>
          <button
            type="button"
            className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500 disabled:opacity-50"
            onClick={handleSave}
            disabled={updateMutation.isPending}
          >
            {updateMutation.isPending ? 'Saving...' : 'Save Schedule Plan'}
          </button>
        </div>
      </Modal>
    </div>
  );
}
