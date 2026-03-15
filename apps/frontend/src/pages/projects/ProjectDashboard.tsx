/**
 * ProjectDashboard — Project dashboard page combining 4 widget charts.
 *
 * Replaces the monolith's:
 * - PcProjectWidgetBudgetChart     (budget/effort doughnut + KPIs)
 * - PcProjectWidgetTaskDistribution (per-owner overdue/today/other grid)
 * - PcProjectWidgetTasksChart       (task due-states doughnut)
 * - PcProjectWidgetTasksPriorityChart (priority distribution doughnut)
 *
 * Data is fetched via TanStack Query hooks (`useProjectDashboard`,
 * `useTasks`, `useTimelogs`) and aggregated client-side with
 * `useMemo` to match the monolith's per-widget computation logic.
 *
 * Layout: responsive 2×2 Tailwind CSS grid (stacked on mobile).
 *
 * @module pages/projects/ProjectDashboard
 */

import { useMemo } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';

import {
  useProjectDashboard,
  useProjects,
  useTasks,
  useTimelogs,
} from '../../hooks/useProjects';
import Chart, {
  ChartType,
  type ChartDataset,
} from '../../components/common/Chart';
import {
  DataTable,
  type DataTableColumn,
} from '../../components/data-table/DataTable';
import type { EntityRecord } from '../../types/record';

/* ═══════════════════════════════════════════════════════════════
 * Theme Colours
 *
 * Values extracted from the monolith's Theme class and matched
 * against Chart.tsx DEFAULT_BORDER_COLORS for consistency.
 * ═══════════════════════════════════════════════════════════════ */

const THEME_COLORS = {
  green: '#4CAF50',
  lightBlue: '#03A9F4',
  red: '#F44336',
  orange: '#FF9800',
  teal: '#009688',
  pink: '#E91E63',
  purple: '#9C27B0',
} as const;

/* ═══════════════════════════════════════════════════════════════
 * Internal Types
 * ═══════════════════════════════════════════════════════════════ */

/** Aggregated budget/effort data for the BudgetWidget. */
interface BudgetWidgetData {
  billedHours: number;
  nonBilledHours: number;
  estimatedHours: number;
  budgetType: string;
  budgetLeft: number;
  billedPercentage: number;
  nonBilledPercentage: number;
}

/** Per-user task distribution row consumed by DataTable. */
interface TaskDistributionRow extends Record<string, unknown> {
  userId: string;
  userName: string;
  overdue: number;
  today: number;
  other: number;
}

/** Task due-state counts for the TasksChartWidget. */
interface TasksDueData {
  overdue: number;
  dueToday: number;
  notDue: number;
}

/** Task priority counts for the TasksPriorityWidget. */
interface TasksPriorityData {
  high: number;
  normal: number;
  low: number;
}

/* ═══════════════════════════════════════════════════════════════
 * Helper Functions
 * ═══════════════════════════════════════════════════════════════ */

/**
 * Safely coerces an {@link EntityRecord} field value to a number.
 * Returns `0` for `null`, `undefined`, or non-numeric values.
 */
function safeNumber(value: unknown): number {
  if (value == null) return 0;
  const num = typeof value === 'number' ? value : Number(value);
  return Number.isNaN(num) ? 0 : num;
}

/**
 * Safely coerces an {@link EntityRecord} field value to a string.
 * Returns an empty string for `null` or `undefined`.
 */
function safeString(value: unknown): string {
  if (value == null) return '';
  return String(value);
}

/**
 * Determines the due state of a task based on its `end_time` field.
 *
 * Replicates the date comparison logic from:
 * - PcProjectWidgetTaskDistribution.cs (uses `DateTime.Now.Date`)
 * - PcProjectWidgetTasksChart.cs       (uses `DateTime.Now`)
 *
 * @param endTime          - The task's `end_time` field value.
 * @param useCurrentTime   - When `true` the overdue threshold is
 *   `DateTime.Now` (full time-of-day); when `false` it is
 *   `DateTime.Now.Date` (midnight).  Distribution uses `false`;
 *   the tasks chart uses `true`.
 * @returns `'overdue'`, `'today'`, or `'other'`.
 */
function getTaskDueState(
  endTime: unknown,
  useCurrentTime: boolean,
): 'overdue' | 'today' | 'other' {
  if (endTime == null || endTime === '') return 'other';

  const end = new Date(endTime as string);
  if (Number.isNaN(end.getTime())) return 'other';

  const now = new Date();
  const todayStart = new Date(
    now.getFullYear(),
    now.getMonth(),
    now.getDate(),
  );
  const tomorrowStart = new Date(todayStart);
  tomorrowStart.setDate(tomorrowStart.getDate() + 1);

  /* C#: endTime.AddDays(1) < threshold */
  const endPlusOne = new Date(end);
  endPlusOne.setDate(endPlusOne.getDate() + 1);

  const overdueThreshold = useCurrentTime ? now : todayStart;

  if (endPlusOne < overdueThreshold) return 'overdue';
  if (end >= todayStart && end < tomorrowStart) return 'today';
  return 'other';
}

/* ═══════════════════════════════════════════════════════════════
 * BudgetWidget
 *
 * Doughnut chart showing billed vs non-billed percentage with
 * four KPI cards.  Replaces PcProjectWidgetBudgetChart.cs.
 * ═══════════════════════════════════════════════════════════════ */

function BudgetWidget({ data }: { data: BudgetWidgetData }) {
  const datasets: ChartDataset[] = useMemo(
    () => [
      {
        label: 'Budget',
        data: [data.billedPercentage, data.nonBilledPercentage],
        backgroundColor: [THEME_COLORS.green, THEME_COLORS.lightBlue],
        borderColor: [THEME_COLORS.green, THEME_COLORS.lightBlue],
        borderWidth: 2,
      },
    ],
    [data.billedPercentage, data.nonBilledPercentage],
  );

  return (
    <section
      className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
      aria-labelledby="budget-widget-title"
    >
      <h3
        id="budget-widget-title"
        className="mb-4 text-lg font-semibold text-gray-900"
      >
        Budget / Effort
      </h3>

      <div className="flex items-center justify-center">
        <Chart
          datasets={datasets}
          labels={['Billed', 'Non-Billed']}
          type={ChartType.Doughnut}
          showLegend
          height="200px"
        />
      </div>

      <div className="mt-6 grid grid-cols-2 gap-4">
        <div className="rounded-md bg-gray-50 p-3">
          <span className="block text-sm text-gray-500">Billed Hours</span>
          <span className="block text-xl font-bold text-gray-900">
            {data.billedHours.toFixed(2)}
          </span>
        </div>

        <div className="rounded-md bg-gray-50 p-3">
          <span className="block text-sm text-gray-500">Non-Billed Hours</span>
          <span className="block text-xl font-bold text-gray-900">
            {data.nonBilledHours.toFixed(2)}
          </span>
        </div>

        <div className="rounded-md bg-gray-50 p-3">
          <span className="block text-sm text-gray-500">Estimated Hours</span>
          <span className="block text-xl font-bold text-gray-900">
            {data.estimatedHours.toFixed(2)}
          </span>
        </div>

        <div className="rounded-md bg-gray-50 p-3">
          <span className="block text-sm text-gray-500">Budget Left</span>
          <span
            className={`block text-xl font-bold ${
              data.budgetLeft < 0 ? 'text-red-600' : 'text-gray-900'
            }`}
          >
            {data.budgetLeft.toFixed(2)}
            {data.budgetType === 'on duration' ? ' hrs' : ''}
          </span>
        </div>
      </div>
    </section>
  );
}

/* ═══════════════════════════════════════════════════════════════
 * TaskDistributionWidget
 *
 * DataTable showing per-user task distribution
 * (overdue / today / other).
 * Replaces PcProjectWidgetTaskDistribution.cs.
 * ═══════════════════════════════════════════════════════════════ */

function TaskDistributionWidget({ rows }: { rows: TaskDistributionRow[] }) {
  const columns: DataTableColumn<TaskDistributionRow>[] = useMemo(
    () => [
      {
        id: 'userName',
        label: 'User',
        accessorKey: 'userName',
        cell: (value: unknown) => (
          <span className="font-medium text-gray-900">
            {safeString(value)}
          </span>
        ),
      },
      {
        id: 'overdue',
        label: 'Overdue',
        accessorKey: 'overdue',
        cell: (value: unknown) => {
          const count = safeNumber(value);
          return (
            <span
              className={
                count > 0
                  ? 'font-semibold text-red-600'
                  : 'text-gray-500'
              }
            >
              {count}
            </span>
          );
        },
      },
      {
        id: 'today',
        label: 'Today',
        accessorKey: 'today',
        cell: (value: unknown) => {
          const count = safeNumber(value);
          return (
            <span
              className={
                count > 0
                  ? 'font-semibold text-orange-600'
                  : 'text-gray-500'
              }
            >
              {count}
            </span>
          );
        },
      },
      {
        id: 'other',
        label: 'Other',
        accessorKey: 'other',
      },
    ],
    [],
  );

  return (
    <section
      className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
      aria-labelledby="distribution-widget-title"
    >
      <h3
        id="distribution-widget-title"
        className="mb-4 text-lg font-semibold text-gray-900"
      >
        Task Distribution
      </h3>

      <DataTable<TaskDistributionRow>
        data={rows}
        columns={columns}
        showFooter={false}
        small
        emptyText="No tasks assigned"
      />
    </section>
  );
}

/* ═══════════════════════════════════════════════════════════════
 * TasksChartWidget
 *
 * 3-slice doughnut: overdue (red), due today (orange), not due
 * (green).  Replaces PcProjectWidgetTasksChart.cs.
 * ═══════════════════════════════════════════════════════════════ */

function TasksChartWidget({ data }: { data: TasksDueData }) {
  const datasets: ChartDataset[] = useMemo(
    () => [
      {
        label: 'Tasks',
        data: [data.overdue, data.dueToday, data.notDue],
        backgroundColor: [
          THEME_COLORS.red,
          THEME_COLORS.orange,
          THEME_COLORS.green,
        ],
        borderColor: [
          THEME_COLORS.red,
          THEME_COLORS.orange,
          THEME_COLORS.green,
        ],
        borderWidth: 2,
      },
    ],
    [data.overdue, data.dueToday, data.notDue],
  );

  const totalTasks = data.overdue + data.dueToday + data.notDue;

  return (
    <section
      className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
      aria-labelledby="tasks-chart-title"
    >
      <h3
        id="tasks-chart-title"
        className="mb-4 text-lg font-semibold text-gray-900"
      >
        Tasks Overview
      </h3>

      {totalTasks > 0 ? (
        <div className="flex items-center justify-center">
          <Chart
            datasets={datasets}
            labels={['Overdue', 'Due Today', 'Not Due']}
            type={ChartType.Doughnut}
            showLegend
            height="200px"
          />
        </div>
      ) : (
        <p className="py-8 text-center text-gray-500">No tasks to display</p>
      )}

      <div className="mt-4 flex justify-around text-center">
        <div>
          <span className="block text-2xl font-bold text-red-600">
            {data.overdue}
          </span>
          <span className="text-xs text-gray-500">Overdue</span>
        </div>
        <div>
          <span className="block text-2xl font-bold text-orange-600">
            {data.dueToday}
          </span>
          <span className="text-xs text-gray-500">Due Today</span>
        </div>
        <div>
          <span className="block text-2xl font-bold text-green-600">
            {data.notDue}
          </span>
          <span className="text-xs text-gray-500">Not Due</span>
        </div>
      </div>
    </section>
  );
}

/* ═══════════════════════════════════════════════════════════════
 * TasksPriorityWidget
 *
 * 3-slice doughnut: high (red), normal (lightBlue), low (green).
 * Replaces PcProjectWidgetTasksPriorityChart.cs.
 * ═══════════════════════════════════════════════════════════════ */

function TasksPriorityWidget({ data }: { data: TasksPriorityData }) {
  const datasets: ChartDataset[] = useMemo(
    () => [
      {
        label: 'Priority',
        data: [data.high, data.normal, data.low],
        backgroundColor: [
          THEME_COLORS.red,
          THEME_COLORS.lightBlue,
          THEME_COLORS.green,
        ],
        borderColor: [
          THEME_COLORS.red,
          THEME_COLORS.lightBlue,
          THEME_COLORS.green,
        ],
        borderWidth: 2,
      },
    ],
    [data.high, data.normal, data.low],
  );

  const totalTasks = data.high + data.normal + data.low;

  return (
    <section
      className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
      aria-labelledby="priority-chart-title"
    >
      <h3
        id="priority-chart-title"
        className="mb-4 text-lg font-semibold text-gray-900"
      >
        Tasks by Priority
      </h3>

      {totalTasks > 0 ? (
        <div className="flex items-center justify-center">
          <Chart
            datasets={datasets}
            labels={['High', 'Normal', 'Low']}
            type={ChartType.Doughnut}
            showLegend
            height="200px"
          />
        </div>
      ) : (
        <p className="py-8 text-center text-gray-500">No tasks to display</p>
      )}

      <div className="mt-4 flex justify-around text-center">
        <div>
          <span className="block text-2xl font-bold text-red-600">
            {data.high}
          </span>
          <span className="text-xs text-gray-500">High</span>
        </div>
        <div>
          <span className="block text-2xl font-bold text-[#03A9F4]">
            {data.normal}
          </span>
          <span className="text-xs text-gray-500">Normal</span>
        </div>
        <div>
          <span className="block text-2xl font-bold text-green-600">
            {data.low}
          </span>
          <span className="text-xs text-gray-500">Low</span>
        </div>
      </div>
    </section>
  );
}

/* ═══════════════════════════════════════════════════════════════
 * ProjectListSection — Inline project list DataTable
 *
 * Renders a table of all projects with columns for name, status, and owner.
 * Replaces the monolith's RecordListPageModel for the project entity.
 * Click-to-navigate sends the user to the project detail/task list.
 * ═══════════════════════════════════════════════════════════════ */

interface ProjectRow extends Record<string, unknown> {
  id: string;
  name: string;
  abbr: string;
  status: string;
  owner: string;
  description: string;
}

function ProjectListSection() {
  const navigate = useNavigate();
  const { data: projects, isLoading } = useProjects();

  const rows: ProjectRow[] = useMemo(() => {
    if (!projects || !Array.isArray(projects)) return [];
    return projects.map((p) => ({
      id: safeString(p.id ?? p['id'] ?? ''),
      name: safeString(p['name'] ?? ''),
      abbr: safeString(p['abbr'] ?? p['abbreviation'] ?? ''),
      status: safeString(p['status'] ?? ''),
      owner: safeString(p['owner_id'] ?? p['owner'] ?? ''),
      description: safeString(p['description'] ?? ''),
    }));
  }, [projects]);

  const columns: DataTableColumn<ProjectRow>[] = useMemo(
    () => [
      {
        id: 'name',
        label: 'Name',
        sortable: true,
        render: (_val: unknown, row: ProjectRow) => (
          <span className="font-medium text-blue-600 hover:underline">
            {row.name || '—'}
          </span>
        ),
      },
      { id: 'abbr', label: 'Abbr', sortable: true },
      { id: 'status', label: 'Status', sortable: true },
      { id: 'owner', label: 'Owner', sortable: true },
    ],
    [],
  );

  if (isLoading) {
    return (
      <section aria-label="Projects" className="mb-4">
        <h3 className="mb-2 text-lg font-semibold text-gray-800">Projects</h3>
        <p className="text-sm text-gray-500">Loading projects…</p>
      </section>
    );
  }

  return (
    <section aria-label="Projects" className="mb-4">
      <h3 className="mb-2 text-lg font-semibold text-gray-800">Projects</h3>
      <DataTable<ProjectRow>
        data={rows}
        columns={columns}
        pageSize={10}
        onRowClick={(row) => navigate(`/projects/${row.id}/tasks`)}
      />
    </section>
  );
}

/* ═══════════════════════════════════════════════════════════════
 * ProjectDashboard — Main Page Component
 *
 * Route: /projects/:projectId/dashboard
 * Optional URL params: ?userId=<guid> for user-scoped filtering
 *
 * Fetches data via three TanStack Query hooks and performs all
 * aggregation client-side using useMemo, matching the monolith's
 * per-widget computation logic exactly.
 * ═══════════════════════════════════════════════════════════════ */

export default function ProjectDashboard() {
  /* ── Route parameters ───────────────────────────────────────── */
  const { projectId } = useParams<{ projectId: string }>();
  const [searchParams] = useSearchParams();
  const userId = searchParams.get('userId') ?? undefined;

  /* ── Data fetching via TanStack Query hooks ─────────────────── */
  const {
    data: dashboardData,
    isLoading: isDashboardLoading,
    isError: isDashboardError,
    error: dashboardError,
  } = useProjectDashboard(projectId);

  const {
    data: tasksData,
    isLoading: isTasksLoading,
    isError: isTasksError,
    error: tasksError,
  } = useTasks({ projectId, pageSize: 10000 });

  const {
    data: timelogsData,
    isLoading: isTimelogsLoading,
    isError: isTimelogsError,
    error: timelogsError,
  } = useTimelogs({ pageSize: 10000 });

  /* ── Derived state ──────────────────────────────────────────── */
  const isLoading = isDashboardLoading || isTasksLoading || isTimelogsLoading;
  const isError = isDashboardError || isTasksError || isTimelogsError;
  const error = dashboardError ?? tasksError ?? timelogsError;

  /** All tasks belonging to this project. */
  const tasks: EntityRecord[] = useMemo(
    () => tasksData?.records ?? [],
    [tasksData],
  );

  /** Set of task IDs for this project (used to scope timelogs). */
  const projectTaskIds: Set<string> = useMemo(
    () => new Set(tasks.map((t) => safeString(t.id))),
    [tasks],
  );

  /**
   * Timelogs scoped to this project by matching task IDs.
   * The monolith's ProjectService.GetProjectTimelogs(projectId)
   * returned only timelogs belonging to the project's tasks.
   */
  const timelogs: EntityRecord[] = useMemo(() => {
    const allTimelogs = timelogsData?.records ?? [];
    if (projectTaskIds.size === 0) return allTimelogs;
    return allTimelogs.filter((tl) =>
      projectTaskIds.has(safeString(tl['task_id'])),
    );
  }, [timelogsData, projectTaskIds]);

  /**
   * Tasks filtered by userId (from URL search params).
   * Used by TasksChartWidget and TasksPriorityWidget which in the
   * monolith accepted an optional `user_id` option.
   */
  const filteredTasks: EntityRecord[] = useMemo(() => {
    if (!userId) return tasks;
    return tasks.filter(
      (task) => safeString(task['owner_id']) === userId,
    );
  }, [tasks, userId]);

  /* ──────────────────────────────────────────────────────────────
   * Budget aggregation
   *
   * Replicates PcProjectWidgetBudgetChart.cs lines 77-153:
   *  1. Sum billable and non-billable minutes from timelogs
   *  2. Sum estimated_minutes from all tasks
   *  3. Convert minutes → hours (Math.Round(x / 60, 2))
   *  4. Compute budgetLeft based on budget_type
   *  5. Compute billed/non-billed percentages for the doughnut
   * ────────────────────────────────────────────────────────────── */
  const budgetData: BudgetWidgetData = useMemo(() => {
    let loggedBillableMinutes = 0;
    let loggedNonBillableMinutes = 0;

    for (const tl of timelogs) {
      const minutes = safeNumber(tl['minutes']);
      const isBillable =
        tl['is_billable'] === true || tl['is_billable'] === 'true';
      if (isBillable) {
        loggedBillableMinutes += minutes;
      } else {
        loggedNonBillableMinutes += minutes;
      }
    }

    let projectEstimatedMinutes = 0;
    for (const task of tasks) {
      projectEstimatedMinutes += safeNumber(task['estimated_minutes']);
    }

    /* Math.Round(x / 60, 2) equivalent */
    const billedHours =
      Math.round((loggedBillableMinutes / 60) * 100) / 100;
    const nonBilledHours =
      Math.round((loggedNonBillableMinutes / 60) * 100) / 100;
    const estimatedHours =
      Math.round((projectEstimatedMinutes / 60) * 100) / 100;

    /* Budget-left calculation — matches source lines 114-126 */
    const budgetType = safeString(
      dashboardData?.['budget_type'] ?? '',
    );
    const budgetAmount = safeNumber(
      dashboardData?.['budget_amount'] ?? 0,
    );
    const hourRate = safeNumber(dashboardData?.['hour_rate'] ?? 0);

    let budgetLeft = 0;
    if (budgetType === 'on duration') {
      budgetLeft = budgetAmount - billedHours;
    } else if (hourRate > 0 && budgetAmount > 0) {
      budgetLeft = budgetAmount / hourRate - billedHours;
    }
    budgetLeft = Math.round(budgetLeft * 100) / 100;

    /* Percentage for doughnut — Math.Round(billed * 100 / total, 0) */
    const totalLogged = billedHours + nonBilledHours;
    const billedPercentage =
      totalLogged > 0
        ? Math.round((billedHours * 100) / totalLogged)
        : 0;
    const nonBilledPercentage =
      totalLogged > 0 ? 100 - billedPercentage : 0;

    return {
      billedHours,
      nonBilledHours,
      estimatedHours,
      budgetType,
      budgetLeft,
      billedPercentage,
      nonBilledPercentage,
    };
  }, [timelogs, tasks, dashboardData]);

  /* ──────────────────────────────────────────────────────────────
   * Task distribution
   *
   * Replicates PcProjectWidgetTaskDistribution.cs lines 75-154:
   *  1. Group tasks by owner_id
   *  2. Count overdue/today/other per owner using midnight comparison
   *  3. Tasks with no owner_id → "No owner" row
   * ────────────────────────────────────────────────────────────── */
  const distributionData: TaskDistributionRow[] = useMemo(() => {
    const userMap = new Map<
      string,
      { userName: string; overdue: number; today: number; other: number }
    >();

    for (const task of tasks) {
      const ownerId = safeString(task['owner_id']);
      const dueState = getTaskDueState(task['end_time'], false);

      if (!userMap.has(ownerId)) {
        const ownerName = ownerId
          ? safeString(task['owner_name']) || 'Unknown User'
          : 'No owner';
        userMap.set(ownerId, {
          userName: ownerName,
          overdue: 0,
          today: 0,
          other: 0,
        });
      }

      const entry = userMap.get(ownerId)!;
      entry[dueState] += 1;
    }

    return Array.from(userMap.entries()).map(
      ([id, entry]) => ({
        userId: id,
        userName: entry.userName,
        overdue: entry.overdue,
        today: entry.today,
        other: entry.other,
      }),
    );
  }, [tasks]);

  /* ──────────────────────────────────────────────────────────────
   * Tasks due-state chart
   *
   * Replicates PcProjectWidgetTasksChart.cs lines 79-118:
   *  Uses DateTime.Now (full time) for overdue threshold.
   * ────────────────────────────────────────────────────────────── */
  const tasksDueData: TasksDueData = useMemo(() => {
    let overdue = 0;
    let dueToday = 0;
    let notDue = 0;

    for (const task of filteredTasks) {
      const state = getTaskDueState(task['end_time'], true);
      if (state === 'overdue') overdue += 1;
      else if (state === 'today') dueToday += 1;
      else notDue += 1;
    }

    return { overdue, dueToday, notDue };
  }, [filteredTasks]);

  /* ──────────────────────────────────────────────────────────────
   * Tasks priority chart
   *
   * Replicates PcProjectWidgetTasksPriorityChart.cs lines 81-119:
   *  Priority field: "1" → low, "2" → normal, "3" → high.
   * ────────────────────────────────────────────────────────────── */
  const tasksPriorityData: TasksPriorityData = useMemo(() => {
    let high = 0;
    let normal = 0;
    let low = 0;

    for (const task of filteredTasks) {
      const priority = safeString(task['priority']);
      switch (priority) {
        case '3':
          high += 1;
          break;
        case '2':
          normal += 1;
          break;
        case '1':
          low += 1;
          break;
        default:
          /* Unknown priority values treated as normal (defensive) */
          normal += 1;
          break;
      }
    }

    return { high, normal, low };
  }, [filteredTasks]);

  /* ── Loading state ──────────────────────────────────────────── */
  if (isLoading) {
    return (
      <div
        className="flex min-h-[400px] items-center justify-center"
        role="status"
      >
        <div className="flex flex-col items-center gap-3">
          <div
            className="h-8 w-8 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600"
            aria-hidden="true"
          />
          <span className="text-sm text-gray-500">Loading dashboard…</span>
        </div>
      </div>
    );
  }

  /* ── Error state ────────────────────────────────────────────── */
  if (isError) {
    return (
      <div
        className="mx-auto max-w-lg rounded-lg border border-red-200 bg-red-50 p-6 text-center"
        role="alert"
      >
        <h2 className="text-lg font-semibold text-red-800">
          Dashboard Error
        </h2>
        <p className="mt-2 text-sm text-red-600">
          {error?.message ??
            'An unexpected error occurred while loading the dashboard.'}
        </p>
      </div>
    );
  }

  /* ── Render — project list + responsive 2×2 grid ─────────── */
  return (
    <div className="p-6">
      <h2 className="mb-6 text-2xl font-bold text-gray-900">
        Project Dashboard
      </h2>

      {/* Project list section — replaces the monolith's RecordListPageModel
          rendering for the project entity.  Shows name, status, and owner
          columns in a DataTable with click-to-navigate to project details. */}
      <ProjectListSection />

      <div className="mt-6 grid grid-cols-1 gap-6 lg:grid-cols-2">
        <BudgetWidget data={budgetData} />
        <TaskDistributionWidget rows={distributionData} />
        <TasksChartWidget data={tasksDueData} />
        <TasksPriorityWidget data={tasksPriorityData} />
      </div>
    </div>
  );
}
