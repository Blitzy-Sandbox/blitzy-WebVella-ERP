import { useMemo } from 'react';
import {
  Chart as ChartJS,
  registerables,
} from 'chart.js';
import {
  Line,
  Bar,
  Pie,
  Doughnut,
  Radar,
  PolarArea,
} from 'react-chartjs-2';
import type { ChartData, ChartOptions } from 'chart.js';

/* ──────────────────────────────────────────────────────────────
 * Register all Chart.js components (scales, elements, plugins)
 * globally so react-chartjs-2 wrappers can function.
 * ──────────────────────────────────────────────────────────── */
ChartJS.register(...registerables);

/* ──────────────────────────────────────────────────────────────
 * ChartType enum
 *
 * Maps 1-to-1 with the monolith's WvChartType enum values
 * discovered in PcChart.cs lines 39-40 and the chart type
 * options logic at line 183-184.
 * ──────────────────────────────────────────────────────────── */
export enum ChartType {
  Line = 'line',
  Bar = 'bar',
  Pie = 'pie',
  Doughnut = 'doughnut',
  Area = 'area',
  Radar = 'radar',
  PolarArea = 'polarArea',
  HorizontalBar = 'horizontalBar',
}

/* ──────────────────────────────────────────────────────────────
 * ChartDataset interface
 *
 * Mirrors the monolith's WvChartDataset class used by the
 * <wv-chart> TagHelper.  Every property is optional except
 * `data` which carries the numeric payload.
 * ──────────────────────────────────────────────────────────── */
export interface ChartDataset {
  /** Human-readable series label shown in the legend. */
  label?: string;
  /** Numeric data points for this dataset. */
  data: number[];
  /** Stroke colour(s) — single string for line/area, per-point array for others. */
  borderColor?: string | string[];
  /** Fill colour(s) — single string for line/area, per-point array for others. */
  backgroundColor?: string | string[];
  /** Border width in pixels.  Defaults to 2 when omitted. */
  borderWidth?: number;
  /** Whether the area beneath the line is filled (Area chart type). */
  fill?: boolean;
}

/* ──────────────────────────────────────────────────────────────
 * ChartProps interface
 *
 * Recreates PcChartOptions (PcChart.cs lines 25-47).
 * `datasets` and `labels` accept multiple input shapes to
 * preserve the flexibility of the monolith's datasource-driven
 * pipeline.
 * ──────────────────────────────────────────────────────────── */
export interface ChartProps {
  /**
   * Conditional rendering flag.
   * Maps to PcChartOptions.IsVisible (jsonProperty "is_visible").
   * @default true
   */
  isVisible?: boolean;

  /**
   * Chart data.  Accepts three formats for backwards compat:
   * 1. ChartDataset[]   — fully specified datasets
   * 2. number[]         — auto-wrapped in a single dataset with theme colours
   * 3. string (CSV)     — parsed to number[] then wrapped
   *
   * Source: PcChart.cs lines 116-167
   */
  datasets: ChartDataset[] | number[] | string;

  /**
   * X-axis / segment labels.  Accepts:
   * 1. string[]  — used directly
   * 2. string    — CSV-split by comma and trimmed
   *
   * Source: PcChart.cs lines 169-173
   */
  labels: string[] | string;

  /**
   * Whether the chart legend is displayed.
   * Maps to PcChartOptions.ShowLegend (jsonProperty "show_legend").
   * @default false
   */
  showLegend?: boolean;

  /**
   * Visual chart type.
   * Maps to PcChartOptions.Type (jsonProperty "type").
   * @default ChartType.Line
   */
  type?: ChartType;

  /**
   * Explicit CSS height for the chart container (e.g. "300px").
   * When set, `maintainAspectRatio` is disabled so the chart
   * fills the declared height.
   * @default null
   */
  height?: string | null;

  /**
   * Explicit CSS width for the chart container (e.g. "100%").
   * @default null
   */
  width?: string | null;
}

/* ──────────────────────────────────────────────────────────────
 * Theme colour palettes
 *
 * Replicated from PcChart.cs lines 107-114 which read the
 * Theme class properties.  The hex values below correspond to
 * the Material Design palette that WebVella ERP uses as its
 * default theme (see WebVella.Erp.Web/Models/Theme.cs).
 *
 * 17 border colours (full saturation) and 17 background colours
 * (light / pastel variants).
 * ──────────────────────────────────────────────────────────── */
const DEFAULT_BORDER_COLORS: readonly string[] = [
  '#009688', // Teal
  '#E91E63', // Pink
  '#4CAF50', // Green
  '#FF9800', // Orange
  '#F44336', // Red
  '#9C27B0', // Purple
  '#673AB7', // DeepPurple
  '#2196F3', // Blue
  '#03A9F4', // LightBlue
  '#00BCD4', // Cyan
  '#4CAF50', // Green (repeated per source)
  '#3F51B5', // Indigo
  '#8BC34A', // LightGreen
  '#CDDC39', // Lime
  '#FFEB3B', // Yellow
  '#FFC107', // Amber
  '#FF5722', // DeepOrange
] as const;

const DEFAULT_BG_COLORS_LIGHT: readonly string[] = [
  '#80CBC4', // TealLight
  '#F48FB1', // PinkLight
  '#A5D6A7', // GreenLight
  '#FFCC80', // OrangeLight
  '#EF9A9A', // RedLight
  '#CE93D8', // PurpleLight
  '#B39DDB', // DeepPurpleLight
  '#90CAF9', // BlueLight
  '#81D4FA', // LightBlueLight
  '#80DEEA', // CyanLight
  '#A5D6A7', // GreenLight (repeated per source)
  '#9FA8DA', // IndigoLight
  '#C5E1A5', // LightGreenLight
  '#E6EE9C', // LimeLight
  '#FFF9C4', // YellowLight
  '#FFE082', // AmberLight
  '#FFAB91', // DeepOrangeLight
] as const;

/* ──────────────────────────────────────────────────────────────
 * Helper — colour at palette index (wraps around)
 * ──────────────────────────────────────────────────────────── */
function borderColorAt(index: number): string {
  return DEFAULT_BORDER_COLORS[index % DEFAULT_BORDER_COLORS.length];
}
function bgLightColorAt(index: number): string {
  return DEFAULT_BG_COLORS_LIGHT[index % DEFAULT_BG_COLORS_LIGHT.length];
}

/* ──────────────────────────────────────────────────────────────
 * normalizeLabels
 *
 * Replicates PcChart.cs lines 169-173.
 * Accepts string[] (pass-through) or CSV string.
 * ──────────────────────────────────────────────────────────── */
function normalizeLabels(labels: string[] | string): string[] {
  if (Array.isArray(labels)) {
    return labels;
  }
  if (typeof labels === 'string' && labels.includes(',')) {
    return labels.split(',').map((s) => s.trim());
  }
  if (typeof labels === 'string' && labels.length > 0) {
    return [labels.trim()];
  }
  return [];
}

/* ──────────────────────────────────────────────────────────────
 * parseCsvToNumbers
 *
 * Replicates PcChart.cs lines 122-138.
 * Returns null when any token cannot be parsed to a number.
 * ──────────────────────────────────────────────────────────── */
function parseCsvToNumbers(csv: string): number[] | null {
  if (!csv.includes(',')) {
    return null;
  }
  const tokens = csv.split(',');
  const result: number[] = [];
  for (const token of tokens) {
    const parsed = Number(token.trim());
    if (Number.isNaN(parsed)) {
      return null;
    }
    result.push(parsed);
  }
  return result.length > 0 ? result : null;
}

/* ──────────────────────────────────────────────────────────────
 * isChartDatasetArray  — runtime type-guard
 * ──────────────────────────────────────────────────────────── */
function isChartDatasetArray(
  value: ChartDataset[] | number[] | string,
): value is ChartDataset[] {
  if (!Array.isArray(value) || value.length === 0) {
    return false;
  }
  const first = value[0];
  return (
    typeof first === 'object' &&
    first !== null &&
    'data' in first &&
    Array.isArray((first as ChartDataset).data)
  );
}

/* ──────────────────────────────────────────────────────────────
 * isNumberArray  — runtime type-guard
 * ──────────────────────────────────────────────────────────── */
function isNumberArray(
  value: ChartDataset[] | number[] | string,
): value is number[] {
  return Array.isArray(value) && value.length > 0 && typeof value[0] === 'number';
}

/* ──────────────────────────────────────────────────────────────
 * applyColors
 *
 * Assigns border and background colours to a single dataset
 * based on chart type, replicating PcChart.cs lines 145-166.
 *
 * Colour rules:
 *  • Line / Area         → single border + single background
 *  • Bar / HorizontalBar → per-point border, per-point *light*
 *                          background
 *  • Pie / Doughnut /
 *    Radar / PolarArea   → per-point border, per-point border
 *                          (same full-saturation) as background
 * ──────────────────────────────────────────────────────────── */
function applyColors(
  dataset: ChartDataset,
  chartType: ChartType,
): ChartDataset {
  /* If the consumer already provided explicit colours, respect them. */
  if (dataset.borderColor !== undefined && dataset.backgroundColor !== undefined) {
    return dataset;
  }

  const count = dataset.data.length;
  const isSingleColor =
    chartType === ChartType.Line || chartType === ChartType.Area;
  const isBarLike =
    chartType === ChartType.Bar || chartType === ChartType.HorizontalBar;

  if (isSingleColor) {
    return {
      ...dataset,
      borderColor: dataset.borderColor ?? borderColorAt(0),
      backgroundColor: dataset.backgroundColor ?? bgLightColorAt(0),
    };
  }

  /* Per-point colour arrays */
  const borders: string[] = [];
  const backgrounds: string[] = [];
  for (let i = 0; i < count; i++) {
    borders.push(borderColorAt(i));
    backgrounds.push(isBarLike ? bgLightColorAt(i) : borderColorAt(i));
  }

  return {
    ...dataset,
    borderColor: dataset.borderColor ?? borders,
    backgroundColor: dataset.backgroundColor ?? backgrounds,
  };
}

/* ──────────────────────────────────────────────────────────────
 * normalizeDatasets
 *
 * Replicates the full dataset processing pipeline from
 * PcChart.cs lines 116-167.
 * ──────────────────────────────────────────────────────────── */
function normalizeDatasets(
  raw: ChartDataset[] | number[] | string,
  chartType: ChartType,
): ChartDataset[] {
  /* 1. Fully-specified ChartDataset[] — apply missing colours */
  if (isChartDatasetArray(raw)) {
    return raw.map((ds) => applyColors(ds, chartType));
  }

  /* 2. number[] — wrap into a single dataset */
  if (isNumberArray(raw)) {
    const wrapped: ChartDataset = {
      data: raw,
      borderWidth: 2,
    };
    return [applyColors(wrapped, chartType)];
  }

  /* 3. CSV string — parse then wrap */
  if (typeof raw === 'string') {
    const numbers = parseCsvToNumbers(raw);
    if (numbers !== null) {
      const wrapped: ChartDataset = {
        data: numbers,
        borderWidth: 2,
      };
      return [applyColors(wrapped, chartType)];
    }
  }

  /* Fallback — empty dataset array to prevent Chart.js errors */
  return [];
}

/* ──────────────────────────────────────────────────────────────
 * renderChart
 *
 * Maps ChartType → react-chartjs-2 component.
 *
 * Special cases:
 *  • Area          → <Line> with `fill: true` on every dataset
 *  • HorizontalBar → <Bar>  with `indexAxis: 'y'`
 *
 * Each branch casts the shared `data` and `options` objects to
 * the chart-type-specific generics expected by react-chartjs-2.
 * This is safe because the underlying Chart.js runtime handles
 * all chart types through the same core; the generic parameters
 * only constrain the TypeScript surface.
 * ──────────────────────────────────────────────────────────── */
function renderChart(
  chartType: ChartType,
  data: ChartData<'line' | 'bar' | 'pie' | 'doughnut' | 'radar' | 'polarArea'>,
  options: ChartOptions,
): React.JSX.Element {
  switch (chartType) {
    case ChartType.Line:
      return (
        <Line
          data={data as ChartData<'line'>}
          options={options as ChartOptions<'line'>}
        />
      );

    case ChartType.Area:
      return (
        <Line
          data={{
            ...data,
            datasets: (data as ChartData<'line'>).datasets.map((ds) => ({
              ...ds,
              fill: true,
            })),
          }}
          options={options as ChartOptions<'line'>}
        />
      );

    case ChartType.Bar:
      return (
        <Bar
          data={data as ChartData<'bar'>}
          options={options as ChartOptions<'bar'>}
        />
      );

    case ChartType.HorizontalBar:
      return (
        <Bar
          data={data as ChartData<'bar'>}
          options={{
            ...(options as ChartOptions<'bar'>),
            indexAxis: 'y' as const,
          }}
        />
      );

    case ChartType.Pie:
      return (
        <Pie
          data={data as ChartData<'pie'>}
          options={options as ChartOptions<'pie'>}
        />
      );

    case ChartType.Doughnut:
      return (
        <Doughnut
          data={data as ChartData<'doughnut'>}
          options={options as ChartOptions<'doughnut'>}
        />
      );

    case ChartType.Radar:
      return (
        <Radar
          data={data as ChartData<'radar'>}
          options={options as ChartOptions<'radar'>}
        />
      );

    case ChartType.PolarArea:
      return (
        <PolarArea
          data={data as ChartData<'polarArea'>}
          options={options as ChartOptions<'polarArea'>}
        />
      );

    default: {
      /* Defensive: render a Line chart for unknown types */
      return (
        <Line
          data={data as ChartData<'line'>}
          options={options as ChartOptions<'line'>}
        />
      );
    }
  }
}

/* ──────────────────────────────────────────────────────────────
 * Chart component
 *
 * Drop-in React replacement for the monolith's PcChart
 * ViewComponent and <wv-chart> TagHelper.
 *
 * Usage:
 *   <Chart
 *     datasets={[{ data: [10, 20, 30] }]}
 *     labels={['Jan', 'Feb', 'Mar']}
 *     type={ChartType.Bar}
 *     showLegend
 *     height="300px"
 *   />
 * ──────────────────────────────────────────────────────────── */
export default function Chart({
  isVisible = true,
  datasets,
  labels,
  showLegend = false,
  type = ChartType.Line,
  height = null,
  width = null,
}: ChartProps): React.JSX.Element | null {
  /* Visibility guard — mirrors PcChart.cs isVisible check */
  if (!isVisible) {
    return null;
  }

  /* Memoize heavy normalisation work so it doesn't re-run on
   * every render when parent state changes. */
  const normalizedLabels = useMemo(() => normalizeLabels(labels), [labels]);

  const normalizedDatasets = useMemo(
    () => normalizeDatasets(datasets, type),
    [datasets, type],
  );

  /* Build the chart data payload consumed by react-chartjs-2 */
  const chartData = useMemo(
    () => ({
      labels: normalizedLabels,
      datasets: normalizedDatasets.map((ds) => ({
        label: ds.label ?? '',
        data: ds.data,
        borderColor: ds.borderColor,
        backgroundColor: ds.backgroundColor,
        borderWidth: ds.borderWidth ?? 2,
        fill: ds.fill ?? false,
      })),
    }),
    [normalizedLabels, normalizedDatasets],
  );

  /* Build the Chart.js options object */
  const chartOptions: ChartOptions = useMemo(
    () => ({
      responsive: true,
      maintainAspectRatio: !height,
      plugins: {
        legend: {
          display: showLegend,
        },
      },
    }),
    [height, showLegend],
  );

  /* Container dimensions — only applied when explicitly provided */
  const containerStyle: React.CSSProperties = useMemo(
    () => ({
      ...(height ? { height } : {}),
      ...(width ? { width } : {}),
      position: 'relative' as const,
    }),
    [height, width],
  );

  return (
    <div style={containerStyle} role="img" aria-label="Chart">
      {renderChart(type, chartData, chartOptions)}
    </div>
  );
}
