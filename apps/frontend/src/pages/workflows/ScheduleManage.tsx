/**
 * ScheduleManage.tsx — Create / Edit Schedule Plan Page
 *
 * React page component that replaces the monolith's
 * `plan-manage.cshtml` + `plan-manage.cshtml.cs` from the SDK plugin.
 *
 * Routes:
 *   - /workflows/schedules/create        (create mode — no scheduleId)
 *   - /workflows/schedules/:scheduleId/edit (edit mode — with scheduleId)
 *
 * Features:
 *   - 12+ form fields matching the monolith exactly
 *   - Conditional field visibility based on SchedulePlanType
 *   - Client-side validation replicating plan-manage.cshtml.cs OnPost rules
 *   - TanStack Query mutations for create / update
 *   - Breadcrumb, page header, and toast notifications
 */

import React, { useState, useCallback, useMemo, useEffect } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import {
  useSchedulePlan,
  useCreateSchedulePlan,
  useUpdateSchedulePlan,
  useWorkflowTypes,
  SchedulePlanType,
} from '../../hooks/useWorkflows';
import type {
  CreateSchedulePlanPayload,
  UpdateSchedulePlanPayload,
  WorkflowType,
} from '../../hooks/useWorkflows';
import { useToast } from '../../components/common/ScreenMessage';
import { ScreenMessageType } from '../../types/common';
import Breadcrumb from '../../components/layout/Breadcrumb';
import type { BreadcrumbItem } from '../../components/layout/Breadcrumb';

/* -------------------------------------------------------------------------- */
/*  Constants                                                                  */
/* -------------------------------------------------------------------------- */

/**
 * Days of the week in the order the monolith renders them
 * (plan-manage.cshtml.cs lines 57-64: WeekOptions list).
 * Keys match the JSON-serialized camelCase of SchedulePlanDaysOfWeek fields.
 */
const WEEK_DAYS = [
  { key: 'scheduledOnMonday', label: 'Monday' },
  { key: 'scheduledOnTuesday', label: 'Tuesday' },
  { key: 'scheduledOnWednesday', label: 'Wednesday' },
  { key: 'scheduledOnThursday', label: 'Thursday' },
  { key: 'scheduledOnFriday', label: 'Friday' },
  { key: 'scheduledOnSaturday', label: 'Saturday' },
  { key: 'scheduledOnSunday', label: 'Sunday' },
] as const;

/**
 * Schedule plan type options for the <select> dropdown.
 * Values mirror the SchedulePlanType enum (SchedulePlan.cs lines 12-22).
 */
const SCHEDULE_TYPE_OPTIONS = [
  { value: SchedulePlanType.Interval, label: 'Interval' },
  { value: SchedulePlanType.Daily, label: 'Daily' },
  { value: SchedulePlanType.Weekly, label: 'Weekly' },
  { value: SchedulePlanType.Monthly, label: 'Monthly' },
] as const;

/** Default day-of-week map with every day unselected. */
const DEFAULT_SCHEDULED_DAYS: Record<string, boolean> = {
  scheduledOnMonday: false,
  scheduledOnTuesday: false,
  scheduledOnWednesday: false,
  scheduledOnThursday: false,
  scheduledOnFriday: false,
  scheduledOnSaturday: false,
  scheduledOnSunday: false,
};

/* -------------------------------------------------------------------------- */
/*  Local Types                                                                */
/* -------------------------------------------------------------------------- */

/** Per-field validation error messages. */
interface FormErrors {
  name?: string;
  startDate?: string;
  endDate?: string;
  intervalInMinutes?: string;
  scheduledDays?: string;
  jobTypeId?: string;
  general?: string;
}

/** Controlled form state for every editable field on the page. */
interface FormState {
  enabled: boolean;
  name: string;
  jobTypeId: string;
  startDate: string;
  endDate: string;
  type: SchedulePlanType;
  intervalInMinutes: number;
  scheduledDays: Record<string, boolean>;
  startTimespan: string;
  endTimespan: string;
}

/* -------------------------------------------------------------------------- */
/*  Helpers                                                                    */
/* -------------------------------------------------------------------------- */

/**
 * Converts an ISO date string to the format expected by
 * `<input type="datetime-local">` (YYYY-MM-DDTHH:mm).
 */
function formatDateForInput(dateStr: string | null | undefined): string {
  if (!dateStr) return '';
  const d = new Date(dateStr);
  if (Number.isNaN(d.getTime())) return '';
  return d.toISOString().slice(0, 16);
}

/* -------------------------------------------------------------------------- */
/*  Clock SVG Icon (shared by Start / End Timespan fields)                     */
/* -------------------------------------------------------------------------- */

/**
 * Inline clock icon matching the monolith's `<wv-field-prepend>` clock icons
 * from plan-manage.cshtml lines 69, 73.
 */
function ClockIcon(): React.JSX.Element {
  return (
    <svg
      className="h-4 w-4 text-gray-400"
      fill="none"
      viewBox="0 0 24 24"
      stroke="currentColor"
      aria-hidden="true"
    >
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        strokeWidth={2}
        d="M12 8v4l3 3m6-3a9 9 0 1 1-18 0 9 9 0 0 1 18 0z"
      />
    </svg>
  );
}

/* ========================================================================== */
/*  ScheduleManage Component                                                   */
/* ========================================================================== */

export default function ScheduleManage(): React.JSX.Element {
  /* ---------------------------------------------------------------------- */
  /*  Routing + External Hooks                                               */
  /* ---------------------------------------------------------------------- */

  const { scheduleId } = useParams<{ scheduleId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { showToast } = useToast();

  const isEditMode = Boolean(scheduleId);

  /* ---------------------------------------------------------------------- */
  /*  Data Fetching                                                          */
  /* ---------------------------------------------------------------------- */

  /** Fetch schedule plan by ID (edit mode only — disabled when no id). */
  const {
    data: schedulePlanResponse,
    isLoading: isPlanLoading,
    isError: isPlanError,
    error: planError,
  } = useSchedulePlan(scheduleId);

  /** Unwrap the ApiResponse envelope to get the actual SchedulePlan data. */
  const schedulePlan = schedulePlanResponse?.object;

  /** Fetch available workflow/job types for the create-mode dropdown. */
  const { data: workflowTypesResponse, isLoading: isTypesLoading } =
    useWorkflowTypes();

  /** Unwrap the ApiResponse envelope to get the WorkflowType array. */
  const workflowTypes = workflowTypesResponse?.object;

  /** TanStack mutations for creating and updating schedule plans. */
  const createMutation = useCreateSchedulePlan();
  const updateMutation = useUpdateSchedulePlan();

  /* ---------------------------------------------------------------------- */
  /*  Form State                                                             */
  /* ---------------------------------------------------------------------- */

  const [form, setForm] = useState<FormState>({
    enabled: true,
    name: '',
    jobTypeId: '',
    startDate: '',
    endDate: '',
    type: SchedulePlanType.Interval,
    intervalInMinutes: 0,
    scheduledDays: { ...DEFAULT_SCHEDULED_DAYS },
    startTimespan: '',
    endTimespan: '',
  });

  const [errors, setErrors] = useState<FormErrors>({});
  const [isSubmitting, setIsSubmitting] = useState(false);

  /* ---------------------------------------------------------------------- */
  /*  Populate Form from Fetched Data (Edit Mode)                            */
  /* ---------------------------------------------------------------------- */

  useEffect(() => {
    if (!schedulePlan) return;

    setForm({
      enabled: schedulePlan.enabled,
      name: schedulePlan.name ?? '',
      jobTypeId: schedulePlan.jobTypeId ?? '',
      startDate: formatDateForInput(schedulePlan.startDate),
      endDate: formatDateForInput(schedulePlan.endDate),
      type: schedulePlan.type ?? SchedulePlanType.Interval,
      intervalInMinutes: schedulePlan.intervalInMinutes ?? 0,
      scheduledDays: schedulePlan.scheduledDays
        ? { ...DEFAULT_SCHEDULED_DAYS, ...schedulePlan.scheduledDays }
        : { ...DEFAULT_SCHEDULED_DAYS },
      startTimespan: schedulePlan.startTimespan ?? '',
      endTimespan: schedulePlan.endTimespan ?? '',
    });
  }, [schedulePlan]);

  /* ---------------------------------------------------------------------- */
  /*  Conditional Field Visibility                                           */
  /* ---------------------------------------------------------------------- */

  /**
   * Controls which optional form sections are visible based on the selected
   * schedule type.  Mirrors the monolith's conditional `<wv-row>` rendering.
   *
   * - Interval : intervalMinutes ✓  scheduleDays ✓  timespans ✓
   * - Daily    : intervalMinutes ✗  scheduleDays ✓  timespans ✓
   * - Weekly   : intervalMinutes ✗  scheduleDays ✓  timespans ✗
   * - Monthly  : intervalMinutes ✗  scheduleDays ✗  timespans ✗
   */
  const fieldVisibility = useMemo(
    () => ({
      showIntervalMinutes: form.type === SchedulePlanType.Interval,
      showScheduleDays:
        form.type === SchedulePlanType.Interval ||
        form.type === SchedulePlanType.Daily ||
        form.type === SchedulePlanType.Weekly,
      showTimespans:
        form.type === SchedulePlanType.Interval ||
        form.type === SchedulePlanType.Daily,
    }),
    [form.type],
  );

  /* ---------------------------------------------------------------------- */
  /*  Field Change Handlers                                                  */
  /* ---------------------------------------------------------------------- */

  /** Generic handler for single-field updates, clears the field error. */
  const handleFieldChange = useCallback(
    <K extends keyof FormState>(field: K, value: FormState[K]) => {
      setForm((prev) => ({ ...prev, [field]: value }));
      setErrors((prev) => {
        const next = { ...prev };
        delete next[field as keyof FormErrors];
        return next;
      });
    },
    [],
  );

  /** Toggles a single day-of-week checkbox in scheduledDays. */
  const handleDayToggle = useCallback((dayKey: string) => {
    setForm((prev) => ({
      ...prev,
      scheduledDays: {
        ...prev.scheduledDays,
        [dayKey]: !prev.scheduledDays[dayKey],
      },
    }));
    setErrors((prev) => {
      const next = { ...prev };
      delete next.scheduledDays;
      return next;
    });
  }, []);

  /* ---------------------------------------------------------------------- */
  /*  Validation                                                             */
  /* ---------------------------------------------------------------------- */

  /**
   * Replicates ALL validation from plan-manage.cshtml.cs (lines 210-227).
   * Returns a FormErrors map — empty map means "valid".
   */
  const validate = useCallback((): FormErrors => {
    const errs: FormErrors = {};

    /* Name is required (plan-manage.cshtml.cs line 212) */
    if (!form.name.trim()) {
      errs.name = 'Name is required field and cannot be empty.';
    }

    /* Start date must be before end date (line 217) */
    if (form.startDate && form.endDate) {
      const start = new Date(form.startDate);
      const end = new Date(form.endDate);
      if (start >= end) {
        errs.startDate = 'Start date must be before end date.';
        errs.endDate = 'End date must be after start date.';
      }
    }

    /* Daily / Interval — at least one day must be selected (line 220) */
    if (
      form.type === SchedulePlanType.Daily ||
      form.type === SchedulePlanType.Interval
    ) {
      const hasSelectedDay = Object.values(form.scheduledDays).some(Boolean);
      if (!hasSelectedDay) {
        errs.scheduledDays = 'At least one day have to be selected';
      }
    }

    /* Interval — interval must be 1‑1440 minutes (line 225) */
    if (form.type === SchedulePlanType.Interval) {
      if (form.intervalInMinutes <= 0 || form.intervalInMinutes > 1440) {
        errs.intervalInMinutes =
          'Interval must be greater than 0 and less or equal than 1440 minutes.';
      }
    }

    /* Create mode — workflow type is required */
    if (!isEditMode && !form.jobTypeId) {
      errs.jobTypeId = 'Workflow type is required.';
    }

    return errs;
  }, [form, isEditMode]);

  /* ---------------------------------------------------------------------- */
  /*  Form Submission                                                        */
  /* ---------------------------------------------------------------------- */

  /**
   * Handles Save button click.  Validates, then fires the appropriate
   * mutation (create or update).  On success, invalidates the schedule-plans
   * cache and redirects to the list page (mirrors plan-manage.cshtml.cs
   * Redirect(ReturnUrl) at line 237).
   */
  const handleSubmit = useCallback(
    async (e: React.FormEvent<HTMLFormElement>) => {
      e.preventDefault();

      const validationErrors = validate();
      if (Object.keys(validationErrors).length > 0) {
        setErrors(validationErrors);
        return;
      }

      setIsSubmitting(true);
      setErrors({});

      try {
        if (isEditMode && scheduleId) {
          /* ---- UPDATE ---- */
          const payload: UpdateSchedulePlanPayload = {
            id: scheduleId,
            name: form.name,
            type: form.type,
            enabled: form.enabled,
            startDate: form.startDate || undefined,
            endDate: form.endDate || undefined,
            intervalInMinutes: fieldVisibility.showIntervalMinutes
              ? form.intervalInMinutes
              : undefined,
            scheduledDays: fieldVisibility.showScheduleDays
              ? form.scheduledDays
              : undefined,
            startTimespan: fieldVisibility.showTimespans
              ? form.startTimespan || undefined
              : undefined,
            endTimespan: fieldVisibility.showTimespans
              ? form.endTimespan || undefined
              : undefined,
          };
          await updateMutation.mutateAsync(payload);
          showToast(
            ScreenMessageType.Success,
            'Saved',
            'Schedule plan updated successfully.',
          );
        } else {
          /* ---- CREATE ---- */
          const payload: CreateSchedulePlanPayload = {
            name: form.name,
            jobTypeId: form.jobTypeId,
            type: form.type,
            enabled: form.enabled,
            startDate: form.startDate || undefined,
            endDate: form.endDate || undefined,
            intervalInMinutes: fieldVisibility.showIntervalMinutes
              ? form.intervalInMinutes
              : undefined,
            scheduledDays: fieldVisibility.showScheduleDays
              ? form.scheduledDays
              : undefined,
            startTimespan: fieldVisibility.showTimespans
              ? form.startTimespan || undefined
              : undefined,
            endTimespan: fieldVisibility.showTimespans
              ? form.endTimespan || undefined
              : undefined,
          };
          await createMutation.mutateAsync(payload);
          showToast(
            ScreenMessageType.Success,
            'Saved',
            'Schedule plan created successfully.',
          );
        }

        /* Explicit invalidation + navigate to list page */
        await queryClient.invalidateQueries({ queryKey: ['schedule-plans'] });
        navigate('/workflows/schedules');
      } catch (err: unknown) {
        const errorMessage =
          err instanceof Error ? err.message : 'An unexpected error occurred.';
        showToast(
          ScreenMessageType.Error,
          'Failed to save schedule plan.',
          errorMessage,
        );
        setErrors({ general: errorMessage });
      } finally {
        setIsSubmitting(false);
      }
    },
    [
      form,
      isEditMode,
      scheduleId,
      validate,
      fieldVisibility,
      createMutation,
      updateMutation,
      queryClient,
      navigate,
      showToast,
    ],
  );

  /* ---------------------------------------------------------------------- */
  /*  Breadcrumb Items                                                       */
  /* ---------------------------------------------------------------------- */

  const breadcrumbItems: BreadcrumbItem[] = useMemo(
    () => [
      { label: 'Workflows', href: '/workflows' },
      { label: 'Schedules', href: '/workflows/schedules' },
      {
        label: isEditMode ? 'Edit Schedule' : 'Create Schedule',
        isActive: true,
      },
    ],
    [isEditMode],
  );

  /* ---------------------------------------------------------------------- */
  /*  Loading State (Edit Mode)                                              */
  /* ---------------------------------------------------------------------- */

  if (isEditMode && isPlanLoading) {
    return (
      <div className="flex items-center justify-center min-h-[50vh]">
        <div className="text-center">
          <div
            className="inline-block h-8 w-8 animate-spin rounded-full border-4 border-solid border-blue-600 border-r-transparent"
            role="status"
            aria-label="Loading"
          >
            <span className="sr-only">Loading schedule plan…</span>
          </div>
          <p className="mt-2 text-sm text-gray-500">Loading schedule plan…</p>
        </div>
      </div>
    );
  }

  /* ---------------------------------------------------------------------- */
  /*  Error State (Edit Mode)                                                */
  /* ---------------------------------------------------------------------- */

  if (isEditMode && isPlanError) {
    return (
      <div
        className="rounded-lg border border-red-200 bg-red-50 p-6 text-center"
        role="alert"
      >
        <h2 className="text-lg font-semibold text-red-800">
          Failed to load schedule plan
        </h2>
        <p className="mt-1 text-sm text-red-600">
          {planError instanceof Error
            ? planError.message
            : 'The schedule plan could not be found.'}
        </p>
        <Link
          to="/workflows/schedules"
          className="mt-4 inline-block rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
        >
          Back to Schedules
        </Link>
      </div>
    );
  }

  /* ---------------------------------------------------------------------- */
  /*  Main Render                                                            */
  /* ---------------------------------------------------------------------- */

  return (
    <div className="space-y-6">
      {/* Breadcrumb Navigation */}
      <Breadcrumb items={breadcrumbItems} />

      {/* Page Header with actions */}
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-gray-900">
          {isEditMode ? 'Edit Schedule' : 'Create Schedule'}
        </h1>
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={() => navigate('/workflows/schedules')}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500"
          >
            Cancel
          </button>
          <button
            type="submit"
            form="schedule-form"
            disabled={isSubmitting}
            className="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {isSubmitting ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>

      {/* Validation Error Summary */}
      {Object.keys(errors).length > 0 && (
        <div
          className="rounded-md border border-red-200 bg-red-50 p-4"
          role="alert"
          aria-live="polite"
        >
          <h3 className="text-sm font-medium text-red-800">
            Please fix the following errors:
          </h3>
          <ul className="mt-2 list-disc ps-5 text-sm text-red-700">
            {Object.entries(errors)
              .filter(([, msg]) => Boolean(msg))
              .map(([key, msg]) => (
                <li key={key}>{msg}</li>
              ))}
          </ul>
        </div>
      )}

      {/* ------------------------------------------------------------------ */}
      {/* Form Card                                                          */}
      {/* ------------------------------------------------------------------ */}
      <form id="schedule-form" onSubmit={handleSubmit} noValidate>
        <div className="rounded-lg bg-white p-6 shadow">
          <div className="grid grid-cols-1 gap-x-6 gap-y-5 sm:grid-cols-2">
            {/* ============================================================ */}
            {/* Row 1: Enabled (col 1) + Id (col 2, edit only)               */}
            {/* ============================================================ */}
            <div className="flex items-end">
              <label className="inline-flex items-center gap-2 text-sm font-medium text-gray-700">
                <input
                  type="checkbox"
                  checked={form.enabled}
                  onChange={(e) => handleFieldChange('enabled', e.target.checked)}
                  className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                />
                <span>Enable this schedule plan</span>
              </label>
            </div>

            {isEditMode && scheduleId ? (
              <div>
                <label
                  className="block text-sm font-medium text-gray-700"
                  htmlFor="schedule-id"
                >
                  Id
                </label>
                <input
                  id="schedule-id"
                  type="text"
                  value={scheduleId}
                  readOnly
                  aria-readonly="true"
                  className="mt-1 block w-full rounded-md border-gray-300 bg-gray-50 px-3 py-2 text-sm text-gray-500 shadow-sm"
                />
              </div>
            ) : (
              <div aria-hidden="true" />
            )}

            {/* ============================================================ */}
            {/* Row 2: Name (col 1) + Workflow Type (col 2)                   */}
            {/* ============================================================ */}
            <div>
              <label
                className="block text-sm font-medium text-gray-700"
                htmlFor="schedule-name"
              >
                Name{' '}
                <span className="text-red-500" aria-hidden="true">
                  *
                </span>
              </label>
              <input
                id="schedule-name"
                type="text"
                value={form.name}
                onChange={(e) => handleFieldChange('name', e.target.value)}
                required
                aria-required="true"
                aria-invalid={Boolean(errors.name)}
                aria-describedby={errors.name ? 'name-error' : undefined}
                className={`mt-1 block w-full rounded-md px-3 py-2 text-sm shadow-sm focus:ring-blue-500 focus:border-blue-500 ${
                  errors.name
                    ? 'border-red-300 text-red-900'
                    : 'border-gray-300'
                }`}
              />
              {errors.name && (
                <p
                  id="name-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {errors.name}
                </p>
              )}
            </div>

            <div>
              <label
                className="block text-sm font-medium text-gray-700"
                htmlFor="schedule-job-type"
              >
                Workflow Type
                {!isEditMode && (
                  <>
                    {' '}
                    <span className="text-red-500" aria-hidden="true">
                      *
                    </span>
                  </>
                )}
              </label>
              {isEditMode ? (
                <input
                  id="schedule-job-type"
                  type="text"
                  value={
                    schedulePlan
                      ? `${schedulePlan.jobTypeName} (${schedulePlan.jobTypeId})`
                      : form.jobTypeId
                  }
                  readOnly
                  aria-readonly="true"
                  className="mt-1 block w-full rounded-md border-gray-300 bg-gray-50 px-3 py-2 text-sm text-gray-500 shadow-sm"
                />
              ) : (
                <>
                  <select
                    id="schedule-job-type"
                    value={form.jobTypeId}
                    onChange={(e) =>
                      handleFieldChange('jobTypeId', e.target.value)
                    }
                    required
                    aria-required="true"
                    aria-invalid={Boolean(errors.jobTypeId)}
                    aria-describedby={
                      errors.jobTypeId ? 'job-type-error' : undefined
                    }
                    disabled={isTypesLoading}
                    className={`mt-1 block w-full rounded-md px-3 py-2 text-sm shadow-sm focus:ring-blue-500 focus:border-blue-500 ${
                      errors.jobTypeId
                        ? 'border-red-300 text-red-900'
                        : 'border-gray-300'
                    }`}
                  >
                    <option value="">
                      {isTypesLoading
                        ? 'Loading workflow types…'
                        : 'Select workflow type…'}
                    </option>
                    {workflowTypes?.map((wt: WorkflowType) => (
                      <option key={wt.id} value={wt.id}>
                        {wt.name}
                      </option>
                    ))}
                  </select>
                  {errors.jobTypeId && (
                    <p
                      id="job-type-error"
                      className="mt-1 text-sm text-red-600"
                      role="alert"
                    >
                      {errors.jobTypeId}
                    </p>
                  )}
                </>
              )}
            </div>

            {/* ============================================================ */}
            {/* Row 3: Start Date (col 1) + End Date (col 2)                 */}
            {/* ============================================================ */}
            <div>
              <label
                className="block text-sm font-medium text-gray-700"
                htmlFor="schedule-start-date"
              >
                Start Date
              </label>
              <input
                id="schedule-start-date"
                type="datetime-local"
                value={form.startDate}
                onChange={(e) =>
                  handleFieldChange('startDate', e.target.value)
                }
                aria-invalid={Boolean(errors.startDate)}
                aria-describedby={
                  errors.startDate ? 'start-date-error' : undefined
                }
                className={`mt-1 block w-full rounded-md px-3 py-2 text-sm shadow-sm focus:ring-blue-500 focus:border-blue-500 ${
                  errors.startDate
                    ? 'border-red-300 text-red-900'
                    : 'border-gray-300'
                }`}
              />
              {errors.startDate && (
                <p
                  id="start-date-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {errors.startDate}
                </p>
              )}
            </div>

            <div>
              <label
                className="block text-sm font-medium text-gray-700"
                htmlFor="schedule-end-date"
              >
                End Date
              </label>
              <input
                id="schedule-end-date"
                type="datetime-local"
                value={form.endDate}
                onChange={(e) =>
                  handleFieldChange('endDate', e.target.value)
                }
                aria-invalid={Boolean(errors.endDate)}
                aria-describedby={
                  errors.endDate ? 'end-date-error' : undefined
                }
                className={`mt-1 block w-full rounded-md px-3 py-2 text-sm shadow-sm focus:ring-blue-500 focus:border-blue-500 ${
                  errors.endDate
                    ? 'border-red-300 text-red-900'
                    : 'border-gray-300'
                }`}
              />
              {errors.endDate && (
                <p
                  id="end-date-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {errors.endDate}
                </p>
              )}
            </div>

            {/* ============================================================ */}
            {/* Row 4: Type (col 1) + Next Trigger (col 2, edit only)        */}
            {/* ============================================================ */}
            <div>
              <label
                className="block text-sm font-medium text-gray-700"
                htmlFor="schedule-type"
              >
                Type
              </label>
              <select
                id="schedule-type"
                value={form.type}
                onChange={(e) =>
                  handleFieldChange(
                    'type',
                    Number(e.target.value) as SchedulePlanType,
                  )
                }
                className="mt-1 block w-full rounded-md border-gray-300 px-3 py-2 text-sm shadow-sm focus:ring-blue-500 focus:border-blue-500"
              >
                {SCHEDULE_TYPE_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>

            {isEditMode && schedulePlan?.nextTriggerTime ? (
              <div>
                <label
                  className="block text-sm font-medium text-gray-700"
                  htmlFor="schedule-next-trigger"
                >
                  Next Trigger
                </label>
                <input
                  id="schedule-next-trigger"
                  type="text"
                  value={new Date(schedulePlan.nextTriggerTime).toLocaleString()}
                  readOnly
                  aria-readonly="true"
                  className="mt-1 block w-full rounded-md border-gray-300 bg-gray-50 px-3 py-2 text-sm text-gray-500 shadow-sm"
                />
              </div>
            ) : (
              <div aria-hidden="true" />
            )}

            {/* ============================================================ */}
            {/* Row 5: Interval in Minutes (conditional — Interval type)      */}
            {/* ============================================================ */}
            {fieldVisibility.showIntervalMinutes && (
              <div className="sm:col-span-1">
                <label
                  className="block text-sm font-medium text-gray-700"
                  htmlFor="schedule-interval"
                >
                  Trigger Each
                </label>
                <div className="mt-1 flex items-center gap-2">
                  <input
                    id="schedule-interval"
                    type="number"
                    min={0}
                    max={1440}
                    step={1}
                    value={form.intervalInMinutes}
                    onChange={(e) =>
                      handleFieldChange(
                        'intervalInMinutes',
                        Number(e.target.value),
                      )
                    }
                    aria-invalid={Boolean(errors.intervalInMinutes)}
                    aria-describedby={
                      errors.intervalInMinutes ? 'interval-error' : undefined
                    }
                    className={`block w-full rounded-md px-3 py-2 text-sm shadow-sm focus:ring-blue-500 focus:border-blue-500 ${
                      errors.intervalInMinutes
                        ? 'border-red-300 text-red-900'
                        : 'border-gray-300'
                    }`}
                  />
                  <span className="text-sm text-gray-500 whitespace-nowrap">
                    minutes
                  </span>
                </div>
                {errors.intervalInMinutes && (
                  <p
                    id="interval-error"
                    className="mt-1 text-sm text-red-600"
                    role="alert"
                  >
                    {errors.intervalInMinutes}
                  </p>
                )}
              </div>
            )}

            {/* ============================================================ */}
            {/* Row 6: Schedule Days (conditional, full width)                */}
            {/* ============================================================ */}
            {fieldVisibility.showScheduleDays && (
              <div className="sm:col-span-2">
                <fieldset>
                  <legend className="text-sm font-medium text-gray-700">
                    Schedule Days
                  </legend>
                  <div className="mt-2 flex flex-wrap gap-x-6 gap-y-2">
                    {WEEK_DAYS.map((day) => (
                      <label
                        key={day.key}
                        className="inline-flex items-center gap-2 text-sm text-gray-700"
                      >
                        <input
                          type="checkbox"
                          checked={form.scheduledDays[day.key] ?? false}
                          onChange={() => handleDayToggle(day.key)}
                          className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                        />
                        {day.label}
                      </label>
                    ))}
                  </div>
                  {errors.scheduledDays && (
                    <p
                      className="mt-1 text-sm text-red-600"
                      role="alert"
                    >
                      {errors.scheduledDays}
                    </p>
                  )}
                </fieldset>
              </div>
            )}

            {/* ============================================================ */}
            {/* Row 7: Start Timespan (col 1) + End Timespan (col 2)         */}
            {/* ============================================================ */}
            {fieldVisibility.showTimespans && (
              <>
                <div>
                  <label
                    className="block text-sm font-medium text-gray-700"
                    htmlFor="schedule-start-timespan"
                  >
                    Start Timespan
                  </label>
                  <div className="relative mt-1">
                    <div className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3">
                      <ClockIcon />
                    </div>
                    <input
                      id="schedule-start-timespan"
                      type="time"
                      value={form.startTimespan}
                      onChange={(e) =>
                        handleFieldChange('startTimespan', e.target.value)
                      }
                      className="block w-full rounded-md border-gray-300 ps-10 py-2 text-sm shadow-sm focus:ring-blue-500 focus:border-blue-500"
                    />
                  </div>
                </div>

                <div>
                  <label
                    className="block text-sm font-medium text-gray-700"
                    htmlFor="schedule-end-timespan"
                  >
                    End Timespan
                  </label>
                  <div className="relative mt-1">
                    <div className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3">
                      <ClockIcon />
                    </div>
                    <input
                      id="schedule-end-timespan"
                      type="time"
                      value={form.endTimespan}
                      onChange={(e) =>
                        handleFieldChange('endTimespan', e.target.value)
                      }
                      className="block w-full rounded-md border-gray-300 ps-10 py-2 text-sm shadow-sm focus:ring-blue-500 focus:border-blue-500"
                    />
                  </div>
                </div>
              </>
            )}
          </div>
        </div>
      </form>
    </div>
  );
}
