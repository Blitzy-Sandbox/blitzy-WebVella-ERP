/**
 * AnalyticsOverview Page Component
 *
 * High-level analytics overview page that consumes aggregated data from the
 * Reporting service's event-sourced read model. The Reporting service
 * maintains read-optimised projections built by SQS event consumers that
 * listen to domain events (SNS) from every bounded context.
 *
 * This page replaces the monolith's DataSourceManager-based report pages and
 * PcChart / PcGrid ViewComponents with React 19, TanStack Query 5, and
 * Tailwind CSS 4.x utility classes.
 *
 * Route: /reports/analytics  (lazy-loaded via React.lazy)
 */

import { useState, useEffect, useMemo, type ChangeEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../../api/client';
import Chart, { ChartType } from '../../components/common/Chart';
import { DataTable, type DataTableColumn } from '../../components/data-table/DataTable';

/* ────────────────────────────────────────────────────────────
   Local TypeScript interfaces for Reporting service responses.
   Defined locally because the shared type files are not listed
   in this file's depends_on_files.
   ──────────────────────────────────────────────────────────── */

/** Single KPI metric returned by the analytics overview endpoint. */
interface KpiMetric {
  /** Human-readable name for the metric (e.g. "Total Entities") */
  label: string;
  /** Current aggregated value — number or pre-formatted string */
  value: number | string;
  /** Percentage change vs. the previous period (optional) */
  change?: number;
  /** Contextual label for the change value (e.g. "vs last month") */
  changeLabel?: string;
  /** Optional icon identifier displayed alongside the label */
  icon?: string;
}

/** Labelled numeric series for chart visualisations. */
interface TrendData {
  /** Category / time-axis labels */
  labels: string[];
  /** Corresponding numeric values */
  data: number[];
}

/** Shape of the GET /v1/reporting/analytics/overview response body. */
interface AnalyticsOverviewData {
  kpis: KpiMetric[];
  recordCreationTrend: TrendData;
  serviceActivityDistribution: TrendData;
  eventProcessingThroughput: TrendData;
}

/** Single row in the recent-activity feed. Index signature required for DataTable generic constraint. */
interface RecentActivityEvent extends Record<string, unknown> {
  id: string;
  eventName: string;
  sourceService: string;
  timestamp: string;
  details: string;
}

/** Shape of the GET /v1/reporting/analytics/recent-activity response body. */
interface RecentActivityData {
  events: RecentActivityEvent[];
  totalCount: number;
}

/** Date-range filter boundaries stored as yyyy-MM-dd strings. */
interface DateRange {
  startDate: string;
  endDate: string;
}

/* ────────────────────────────────────────────────────────────
   Constants
   ──────────────────────────────────────────────────────────── */

/** Auto-refresh interval — 60 seconds for near-real-time analytics feel. */
const REFETCH_INTERVAL_MS = 60_000;

/** Chart colour palette aligned with the monolith's Theme.cs semantic tokens. */
const CHART_COLORS = {
  primary: '#007bff',
  primaryFaded: 'rgba(0, 123, 255, 0.15)',
  success: '#28a745',
  successFaded: 'rgba(40, 167, 69, 0.25)',
  barPalette: [
    '#007bff', '#28a745', '#ffc107', '#dc3545', '#17a2b8',
    '#6c757d', '#FF9800', '#9C27B0', '#00BCD4', '#8BC34A',
  ],
} as const;

/* ────────────────────────────────────────────────────────────
   Helpers
   ──────────────────────────────────────────────────────────── */

/** Returns a DateRange spanning the last 30 days ending today. */
function getDefaultDateRange(): DateRange {
  const end = new Date();
  const start = new Date();
  start.setDate(start.getDate() - 30);
  return {
    startDate: start.toISOString().split('T')[0],
    endDate: end.toISOString().split('T')[0],
  };
}

/** Converts an ISO-8601 timestamp to a locale-aware display string. */
function formatTimestamp(iso: string): string {
  if (!iso) return '';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

/** Returns a signed percentage string, e.g. "+3.2%" or "−1.5%". */
function formatChange(change: number | undefined): string {
  if (change === undefined || change === null) return '';
  const sign = change >= 0 ? '+' : '';
  return `${sign}${change.toFixed(1)}%`;
}

/** Returns a Tailwind text-colour class reflecting the direction of change. */
function changeColorClass(change: number | undefined): string {
  if (change === undefined || change === null) return 'text-gray-500';
  if (change > 0) return 'text-green-600';
  if (change < 0) return 'text-red-600';
  return 'text-gray-500';
}

/* ────────────────────────────────────────────────────────────
   Component
   ──────────────────────────────────────────────────────────── */

/**
 * Analytics Overview page.
 *
 * Displays aggregated KPI cards, trend charts (line / bar / area), and a
 * recent-activity data table populated by the Reporting service's SQS
 * event consumers. All data is fetched via the environment-aware API
 * client — no direct AWS SDK imports.
 */
export default function AnalyticsOverview() {
  /* ── Local state: date-range filter ── */
  const [dateRange, setDateRange] = useState<DateRange>(getDefaultDateRange);

  /* ── Set document title on mount ── */
  useEffect(() => {
    document.title = 'Analytics Overview';
  }, []);

  /* ── Derive query parameters from filter state ── */
  const queryParams = useMemo<Record<string, unknown>>(
    () => ({ startDate: dateRange.startDate, endDate: dateRange.endDate }),
    [dateRange.startDate, dateRange.endDate],
  );

  /* ── TanStack Query: overview KPIs & trend data ── */
  const {
    data: overviewResponse,
    isLoading: isOverviewLoading,
    isError: isOverviewError,
    error: overviewError,
  } = useQuery({
    queryKey: ['reporting', 'analytics', 'overview', queryParams],
    queryFn: () =>
      get<AnalyticsOverviewData>('reporting/analytics/overview', queryParams),
    refetchInterval: REFETCH_INTERVAL_MS,
  });

  /* ── TanStack Query: recent-activity feed ── */
  const {
    data: activityResponse,
    isLoading: isActivityLoading,
    isError: isActivityError,
    error: activityError,
  } = useQuery({
    queryKey: ['reporting', 'analytics', 'recent-activity', queryParams],
    queryFn: () =>
      get<RecentActivityData>('reporting/analytics/recent-activity', queryParams),
    refetchInterval: REFETCH_INTERVAL_MS,
  });

  /* ── Unwrap API response envelopes ── */
  const overviewData = overviewResponse?.object ?? null;
  const activityData = activityResponse?.object ?? null;

  /* ── Memoised DataTable column definitions ── */
  const activityColumns = useMemo<DataTableColumn<RecentActivityEvent>[]>(
    () => [
      {
        id: 'eventName',
        label: 'Event',
        accessorKey: 'eventName',
        sortable: true,
      },
      {
        id: 'sourceService',
        label: 'Source Service',
        accessorKey: 'sourceService',
        sortable: true,
      },
      {
        id: 'timestamp',
        label: 'Timestamp',
        accessorKey: 'timestamp',
        sortable: true,
        cell: (_value: unknown, record: RecentActivityEvent) => (
          <time dateTime={record.timestamp}>{formatTimestamp(record.timestamp)}</time>
        ),
      },
      {
        id: 'details',
        label: 'Details',
        accessorKey: 'details',
        sortable: false,
      },
    ],
    [],
  );

  /* ── Memoised chart datasets ── */
  const recordCreationDatasets = useMemo(
    () =>
      overviewData?.recordCreationTrend
        ? [
            {
              label: 'Records Created',
              data: overviewData.recordCreationTrend.data,
              borderColor: CHART_COLORS.primary,
              backgroundColor: CHART_COLORS.primaryFaded,
              borderWidth: 2,
              fill: false,
            },
          ]
        : [],
    [overviewData?.recordCreationTrend],
  );

  const serviceActivityDatasets = useMemo(
    () =>
      overviewData?.serviceActivityDistribution
        ? [
            {
              label: 'Service Activity',
              data: overviewData.serviceActivityDistribution.data,
              backgroundColor: CHART_COLORS.barPalette.slice(
                0,
                overviewData.serviceActivityDistribution.data.length,
              ),
              borderWidth: 1,
            },
          ]
        : [],
    [overviewData?.serviceActivityDistribution],
  );

  const eventThroughputDatasets = useMemo(
    () =>
      overviewData?.eventProcessingThroughput
        ? [
            {
              label: 'Events Processed',
              data: overviewData.eventProcessingThroughput.data,
              borderColor: CHART_COLORS.success,
              backgroundColor: CHART_COLORS.successFaded,
              borderWidth: 2,
              fill: true,
            },
          ]
        : [],
    [overviewData?.eventProcessingThroughput],
  );

  /* ── Event handlers ── */
  const handleStartDateChange = (e: ChangeEvent<HTMLInputElement>) => {
    setDateRange((prev) => ({ ...prev, startDate: e.target.value }));
  };

  const handleEndDateChange = (e: ChangeEvent<HTMLInputElement>) => {
    setDateRange((prev) => ({ ...prev, endDate: e.target.value }));
  };

  /* ── Render ── */
  return (
    <main className="flex flex-col gap-6 p-6">
      {/* ── Page header ── */}
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold text-gray-900">
          Analytics Overview
        </h1>
        <p className="text-sm text-gray-500">
          Aggregated metrics and trends across all services, powered by the
          Reporting service&apos;s event-sourced read model.
        </p>
      </header>

      {/* ── Date-range filter ── */}
      <section
        aria-label="Date range filter"
        className="flex flex-wrap items-end gap-4 rounded-lg border border-gray-200 bg-white p-4"
      >
        <div className="flex flex-col gap-1">
          <label
            htmlFor="analytics-start-date"
            className="text-xs font-medium text-gray-600"
          >
            Start Date
          </label>
          <input
            id="analytics-start-date"
            type="date"
            value={dateRange.startDate}
            onChange={handleStartDateChange}
            className="rounded border border-gray-300 px-3 py-1.5 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          />
        </div>

        <div className="flex flex-col gap-1">
          <label
            htmlFor="analytics-end-date"
            className="text-xs font-medium text-gray-600"
          >
            End Date
          </label>
          <input
            id="analytics-end-date"
            type="date"
            value={dateRange.endDate}
            onChange={handleEndDateChange}
            className="rounded border border-gray-300 px-3 py-1.5 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          />
        </div>
      </section>

      {/* ── Overview error banner ── */}
      {isOverviewError && (
        <div
          role="alert"
          className="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700"
        >
          <strong className="font-medium">
            Failed to load analytics data.
          </strong>{' '}
          {overviewError instanceof Error
            ? overviewError.message
            : 'An unexpected error occurred.'}
        </div>
      )}

      {/* ── Overview loading skeleton ── */}
      {isOverviewLoading && (
        <div
          aria-busy="true"
          aria-label="Loading analytics overview"
          className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-4"
        >
          {Array.from({ length: 4 }).map((_, idx) => (
            <div
              key={`kpi-skeleton-${String(idx)}`}
              className="animate-pulse rounded-lg border border-gray-200 bg-white p-4"
            >
              <div className="mb-2 h-4 w-24 rounded bg-gray-200" />
              <div className="h-8 w-16 rounded bg-gray-200" />
            </div>
          ))}
        </div>
      )}

      {/* ── KPI metric cards ── */}
      {overviewData && overviewData.kpis.length > 0 && (
        <section
          aria-label="Key performance indicators"
          className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-4"
        >
          {overviewData.kpis.map((kpi) => (
            <article
              key={kpi.label}
              className="flex flex-col gap-1 rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
            >
              <span className="text-xs font-medium uppercase tracking-wide text-gray-500">
                {kpi.icon ? `${kpi.icon} ` : ''}
                {kpi.label}
              </span>
              <span className="text-2xl font-bold text-gray-900">
                {kpi.value}
              </span>
              {kpi.change !== undefined && (
                <span
                  className={`text-xs font-medium ${changeColorClass(kpi.change)}`}
                >
                  {formatChange(kpi.change)}
                  {kpi.changeLabel
                    ? ` ${kpi.changeLabel}`
                    : ' vs previous period'}
                </span>
              )}
            </article>
          ))}
        </section>
      )}

      {/* ── Empty state for overview ── */}
      {overviewData &&
        overviewData.kpis.length === 0 &&
        !isOverviewLoading && (
          <div className="rounded-lg border border-gray-200 bg-white p-8 text-center text-sm text-gray-500">
            No analytics data available for the selected date range.
          </div>
        )}

      {/* ── Trend charts ── */}
      {overviewData && (
        <section
          aria-label="Trend charts"
          className="grid grid-cols-1 gap-6 lg:grid-cols-3"
        >
          {/* Record creation trend — Line chart */}
          <article className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
            <h2 className="mb-3 text-sm font-semibold text-gray-700">
              Record Creation Trend
            </h2>
            <Chart
              type={ChartType.Line}
              datasets={recordCreationDatasets}
              labels={overviewData.recordCreationTrend?.labels ?? []}
              showLegend
              height="240px"
            />
          </article>

          {/* Service activity distribution — Bar chart */}
          <article className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
            <h2 className="mb-3 text-sm font-semibold text-gray-700">
              Service Activity
            </h2>
            <Chart
              type={ChartType.Bar}
              datasets={serviceActivityDatasets}
              labels={
                overviewData.serviceActivityDistribution?.labels ?? []
              }
              showLegend
              height="240px"
            />
          </article>

          {/* Event processing throughput — Area chart */}
          <article className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
            <h2 className="mb-3 text-sm font-semibold text-gray-700">
              Event Throughput
            </h2>
            <Chart
              type={ChartType.Area}
              datasets={eventThroughputDatasets}
              labels={
                overviewData.eventProcessingThroughput?.labels ?? []
              }
              showLegend
              height="240px"
            />
          </article>
        </section>
      )}

      {/* ── Recent activity section ── */}
      <section aria-label="Recent activity" className="flex flex-col gap-3">
        <h2 className="text-lg font-semibold text-gray-800">
          Recent Activity
        </h2>

        {isActivityError && (
          <div
            role="alert"
            className="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700"
          >
            <strong className="font-medium">
              Failed to load recent activity.
            </strong>{' '}
            {activityError instanceof Error
              ? activityError.message
              : 'An unexpected error occurred.'}
          </div>
        )}

        <DataTable
          columns={activityColumns}
          data={activityData?.events ?? []}
          totalCount={activityData?.totalCount ?? 0}
          loading={isActivityLoading}
          emptyText="No recent activity found for the selected date range."
        />
      </section>
    </main>
  );
}
