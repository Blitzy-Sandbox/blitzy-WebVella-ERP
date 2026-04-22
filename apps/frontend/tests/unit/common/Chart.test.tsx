/**
 * Vitest Component Tests for `<Chart />`
 *
 * Validates the React Chart component (`apps/frontend/src/components/common/Chart.tsx`)
 * that replaces the monolith's `PcChart` ViewComponent
 * (`WebVella.Erp.Web/Components/PcChart/PcChart.cs`, `Display.cshtml`).
 *
 * The monolith's PcChartOptions define seven configuration properties:
 *  - is_visible (string → boolean): controls chart visibility
 *  - datasets (string): supports WvChartDataset[], decimal[], or CSV strings
 *  - labels (string): supports string[] or CSV string
 *  - show_legend (bool, default false): toggles Chart.js legend
 *  - type (WvChartType, default Line): selects chart type from 8 variants
 *  - height (string, null): CSS height for chart container
 *  - width (string, null): CSS width for chart container
 *
 * The PcChart.cs rendering pipeline (lines 107-184):
 *  - Reads theme colours (17 border + 17 background entries)
 *  - Normalizes datasets from 3 input formats (ChartDataset[], number[], CSV)
 *  - Assigns colour palettes based on chart type:
 *      • Line/Area: single border + background colour (first palette entry)
 *      • Bar/HorizontalBar: per-point border, per-point light background
 *      • Pie/Doughnut/Radar/PolarArea: per-point border = per-point background
 *  - Normalizes labels from string[] or CSV
 *  - Passes type, showLegend, height, width to the <wv-chart> TagHelper
 *
 * Test coverage includes:
 *  - Basic rendering (visibility, default type, container, canvas)
 *  - Chart type switching across all 8 ChartType enum values
 *  - Dataset normalization for 3 input formats + edge cases
 *  - Label normalization for 2 input formats + edge cases
 *  - Legend visibility toggle
 *  - Container dimension styling (height/width)
 *  - Colour palette auto-assignment by chart type
 *  - Canvas element presence verification
 *
 * @see PcChart.cs — Source ViewComponent with dataset/label normalization
 * @see Display.cshtml — <wv-chart> TagHelper rendering
 * @see Options.cshtml — Editor UI for PcChartOptions properties
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';

/* ─────────────────────────────────────────────────────────────────────────────
 * Mock chart.js registration
 *
 * Chart.tsx calls `ChartJS.register(...registerables)` at module level.
 * In jsdom there is no canvas context, so we stub the entire chart.js
 * module to prevent registration errors.
 * ───────────────────────────────────────────────────────────────────────────── */
vi.mock('chart.js', () => ({
  Chart: { register: vi.fn() },
  registerables: [],
}));

/* ─────────────────────────────────────────────────────────────────────────────
 * Mock react-chartjs-2
 *
 * jsdom lacks native canvas support, so react-chartjs-2 components cannot
 * render real Chart.js charts. Each mock:
 *  1. Is a `vi.fn()` so we can assert on calls and inspect props
 *  2. Renders a <canvas> stub with `data-testid` and `data-type` for
 *     DOM-based assertions (chart type verification, canvas presence)
 *
 * The async factory imports React to use `React.createElement` safely,
 * avoiding JSX transpilation issues in the hoisted mock context.
 * ───────────────────────────────────────────────────────────────────────────── */
vi.mock('react-chartjs-2', async () => {
  const React = await import('react');
  const makeChartMock = (type: string) =>
    vi.fn((props: Record<string, unknown>) =>
      React.createElement('canvas', {
        'data-testid': 'chart-canvas',
        'data-type': type,
      }),
    );
  return {
    Line: makeChartMock('line'),
    Bar: makeChartMock('bar'),
    Pie: makeChartMock('pie'),
    Doughnut: makeChartMock('doughnut'),
    Radar: makeChartMock('radar'),
    PolarArea: makeChartMock('polarArea'),
  };
});

/* ─────────────────────────────────────────────────────────────────────────────
 * Component-under-test imports
 * ───────────────────────────────────────────────────────────────────────────── */
import Chart, {
  ChartType,
  ChartDataset,
  ChartProps,
} from '../../../src/components/common/Chart';
import {
  Line,
  Bar,
  Pie,
  Doughnut,
  Radar,
  PolarArea,
} from 'react-chartjs-2';

/* ─────────────────────────────────────────────────────────────────────────────
 * Utility — cast an imported mock to `vi.fn()` return type for call assertions
 * ───────────────────────────────────────────────────────────────────────────── */
const asMock = (fn: unknown) => fn as ReturnType<typeof vi.fn>;

/* ─────────────────────────────────────────────────────────────────────────────
 * Shared test data constants
 *
 * sampleDatasets — fully-specified ChartDataset[] with explicit colours
 * sampleLabels   — string[] of month labels
 * sampleCsvDatasets — comma-separated number string (PcChart.cs CSV format)
 * sampleCsvLabels   — comma-separated label string (PcChart.cs CSV format)
 * ───────────────────────────────────────────────────────────────────────────── */
const sampleDatasets: ChartDataset[] = [
  {
    label: 'Sales',
    data: [10, 20, 30, 40, 50],
    borderColor: '#009688',
    backgroundColor: '#80CBC4',
  },
];

const sampleLabels: string[] = ['Jan', 'Feb', 'Mar', 'Apr', 'May'];

const sampleCsvDatasets = '10,20,30,40,50';

const sampleCsvLabels = 'Jan,Feb,Mar,Apr,May';

/* Expected default colour palette (first 5 entries from PcChart.cs lines 107-114) */
const BORDER_PALETTE = [
  '#009688', '#E91E63', '#4CAF50', '#FF9800', '#F44336',
];
const BG_LIGHT_PALETTE = [
  '#80CBC4', '#F48FB1', '#A5D6A7', '#FFCC80', '#EF9A9A',
];

/* ═══════════════════════════════════════════════════════════════════════════════
 * Test suites
 * ═══════════════════════════════════════════════════════════════════════════════ */
describe('Chart component', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  /* ─────────────────────────────────────────────────────────────────────────
   * 2.1 Basic Rendering Tests
   * ───────────────────────────────────────────────────────────────────────── */
  describe('basic rendering', () => {
    it('renders chart with required props', () => {
      render(<Chart datasets={sampleDatasets} labels={sampleLabels} />);
      expect(screen.getByRole('img')).toBeDefined();
      expect(screen.getByTestId('chart-canvas')).toBeDefined();
    });

    it('renders nothing when isVisible is false', () => {
      const { container } = render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          isVisible={false}
        />,
      );
      expect(container.innerHTML).toBe('');
      expect(screen.queryByTestId('chart-canvas')).toBeNull();
    });

    it('renders with default type (Line)', () => {
      render(<Chart datasets={sampleDatasets} labels={sampleLabels} />);
      expect(asMock(Line)).toHaveBeenCalled();
      expect(screen.getByTestId('chart-canvas').getAttribute('data-type')).toBe(
        'line',
      );
    });

    it('renders container element', () => {
      render(<Chart datasets={sampleDatasets} labels={sampleLabels} />);
      const container = screen.getByRole('img');
      expect(container.tagName).toBe('DIV');
      expect(container.getAttribute('aria-label')).toBe('Chart');
    });
  });

  /* ─────────────────────────────────────────────────────────────────────────
   * 2.2 Chart Type Switching Tests
   *
   * PcChart.cs lines 39-40: WvChartType enum with 8 values
   * PcChart.cs line 183-184: area type remapped from enum value "4"
   * ───────────────────────────────────────────────────────────────────────── */
  describe('chart type switching', () => {
    it('renders Line chart', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          type={ChartType.Line}
        />,
      );
      expect(asMock(Line)).toHaveBeenCalled();
      expect(screen.getByTestId('chart-canvas').getAttribute('data-type')).toBe(
        'line',
      );
    });

    it('renders Bar chart', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          type={ChartType.Bar}
        />,
      );
      expect(asMock(Bar)).toHaveBeenCalled();
      expect(screen.getByTestId('chart-canvas').getAttribute('data-type')).toBe(
        'bar',
      );
    });

    it('renders Pie chart', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          type={ChartType.Pie}
        />,
      );
      expect(asMock(Pie)).toHaveBeenCalled();
      expect(screen.getByTestId('chart-canvas').getAttribute('data-type')).toBe(
        'pie',
      );
    });

    it('renders Doughnut chart', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          type={ChartType.Doughnut}
        />,
      );
      expect(asMock(Doughnut)).toHaveBeenCalled();
      expect(screen.getByTestId('chart-canvas').getAttribute('data-type')).toBe(
        'doughnut',
      );
    });

    it('renders Area chart as Line with fill', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          type={ChartType.Area}
        />,
      );
      /* Area uses the Line component internally (PcChart.cs line 183-184) */
      expect(asMock(Line)).toHaveBeenCalled();
      expect(screen.getByTestId('chart-canvas').getAttribute('data-type')).toBe(
        'line',
      );
      /* Verify fill: true is set on datasets for Area type */
      const lineProps = asMock(Line).mock.calls[0][0] as Record<string, any>;
      const datasets = lineProps.data?.datasets ?? [];
      expect(datasets.length).toBeGreaterThan(0);
      expect(datasets[0].fill).toBe(true);
    });

    it('renders Radar chart', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          type={ChartType.Radar}
        />,
      );
      expect(asMock(Radar)).toHaveBeenCalled();
      expect(screen.getByTestId('chart-canvas').getAttribute('data-type')).toBe(
        'radar',
      );
    });

    it('renders PolarArea chart', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          type={ChartType.PolarArea}
        />,
      );
      expect(asMock(PolarArea)).toHaveBeenCalled();
      expect(screen.getByTestId('chart-canvas').getAttribute('data-type')).toBe(
        'polarArea',
      );
    });

    it('renders HorizontalBar chart as Bar with indexAxis y', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          type={ChartType.HorizontalBar}
        />,
      );
      /* HorizontalBar renders as <Bar> with options.indexAxis = 'y' */
      expect(asMock(Bar)).toHaveBeenCalled();
      expect(screen.getByTestId('chart-canvas').getAttribute('data-type')).toBe(
        'bar',
      );
      const barProps = asMock(Bar).mock.calls[0][0] as Record<string, any>;
      expect(barProps.options?.indexAxis).toBe('y');
    });
  });

  /* ─────────────────────────────────────────────────────────────────────────
   * 2.3 Dataset Binding Tests
   *
   * PcChart.cs lines 116-167: three dataset input formats
   *   1. List<WvChartDataset> (fully specified)
   *   2. List<decimal> (auto-wrapped with theme colours)
   *   3. CSV string (parsed to decimal list then wrapped)
   * ───────────────────────────────────────────────────────────────────────── */
  describe('dataset binding', () => {
    it('accepts ChartDataset array', () => {
      const multiDatasets: ChartDataset[] = [
        {
          label: 'Revenue',
          data: [100, 200, 300],
          borderColor: '#FF0000',
          backgroundColor: '#FFAAAA',
        },
        {
          label: 'Costs',
          data: [50, 80, 120],
          borderColor: '#0000FF',
          backgroundColor: '#AAAAFF',
        },
      ];
      render(<Chart datasets={multiDatasets} labels={['Q1', 'Q2', 'Q3']} />);
      expect(screen.getByTestId('chart-canvas')).toBeDefined();

      const lineProps = asMock(Line).mock.calls[0][0] as Record<string, any>;
      const ds = lineProps.data?.datasets;
      expect(ds).toHaveLength(2);
      expect(ds[0].label).toBe('Revenue');
      expect(ds[0].data).toEqual([100, 200, 300]);
      expect(ds[0].borderColor).toBe('#FF0000');
      expect(ds[0].backgroundColor).toBe('#FFAAAA');
      expect(ds[1].label).toBe('Costs');
      expect(ds[1].data).toEqual([50, 80, 120]);
    });

    it('accepts number array', () => {
      const numberData = [10, 20, 30, 40, 50] as unknown as ChartDataset[] | number[] | string;
      render(
        <Chart
          datasets={numberData as any}
          labels={sampleLabels}
        />,
      );
      expect(screen.getByTestId('chart-canvas')).toBeDefined();

      const lineProps = asMock(Line).mock.calls[0][0] as Record<string, any>;
      const ds = lineProps.data?.datasets;
      expect(ds).toHaveLength(1);
      expect(ds[0].data).toEqual([10, 20, 30, 40, 50]);
    });

    it('accepts CSV string for datasets', () => {
      render(
        <Chart
          datasets={sampleCsvDatasets as any}
          labels={sampleLabels}
        />,
      );
      expect(screen.getByTestId('chart-canvas')).toBeDefined();

      const lineProps = asMock(Line).mock.calls[0][0] as Record<string, any>;
      const ds = lineProps.data?.datasets;
      expect(ds).toHaveLength(1);
      expect(ds[0].data).toEqual([10, 20, 30, 40, 50]);
    });

    it('handles empty datasets gracefully', () => {
      render(
        <Chart datasets={[] as any} labels={sampleLabels} />,
      );
      /* Component should render without throwing */
      expect(screen.getByRole('img')).toBeDefined();
    });

    it('handles invalid CSV gracefully', () => {
      render(
        <Chart datasets={'abc,def' as any} labels={sampleLabels} />,
      );
      /* Component should render without error; invalid CSV yields 0 datasets */
      expect(screen.getByRole('img')).toBeDefined();
    });
  });

  /* ─────────────────────────────────────────────────────────────────────────
   * 2.4 Label Tests
   *
   * PcChart.cs lines 169-173: label normalization
   *   1. List<string> (pass-through)
   *   2. CSV string (split by comma)
   * ───────────────────────────────────────────────────────────────────────── */
  describe('label normalization', () => {
    it('accepts string array for labels', () => {
      render(
        <Chart datasets={sampleDatasets} labels={['A', 'B', 'C']} />,
      );
      expect(screen.getByTestId('chart-canvas')).toBeDefined();

      const lineProps = asMock(Line).mock.calls[0][0] as Record<string, any>;
      expect(lineProps.data?.labels).toEqual(['A', 'B', 'C']);
    });

    it('accepts CSV string for labels', () => {
      render(
        <Chart datasets={sampleDatasets} labels={sampleCsvLabels} />,
      );
      expect(screen.getByTestId('chart-canvas')).toBeDefined();

      const lineProps = asMock(Line).mock.calls[0][0] as Record<string, any>;
      expect(lineProps.data?.labels).toEqual(['Jan', 'Feb', 'Mar', 'Apr', 'May']);
    });

    it('handles empty labels', () => {
      render(
        <Chart datasets={sampleDatasets} labels={[] as string[]} />,
      );
      /* Component renders without error; empty label array is valid */
      expect(screen.getByRole('img')).toBeDefined();

      const lineProps = asMock(Line).mock.calls[0][0] as Record<string, any>;
      expect(lineProps.data?.labels).toEqual([]);
    });
  });

  /* ─────────────────────────────────────────────────────────────────────────
   * 2.5 Legend Tests
   *
   * PcChartOptions.ShowLegend defaults to false (PcChart.cs line 37)
   * ───────────────────────────────────────────────────────────────────────── */
  describe('legend visibility', () => {
    it('hides legend by default', () => {
      render(<Chart datasets={sampleDatasets} labels={sampleLabels} />);

      const lineProps = asMock(Line).mock.calls[0][0] as Record<string, any>;
      expect(lineProps.options?.plugins?.legend?.display).toBe(false);
    });

    it('shows legend when showLegend is true', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          showLegend={true}
        />,
      );

      const lineProps = asMock(Line).mock.calls[0][0] as Record<string, any>;
      expect(lineProps.options?.plugins?.legend?.display).toBe(true);
    });
  });

  /* ─────────────────────────────────────────────────────────────────────────
   * 2.6 Dimension Tests
   *
   * PcChartOptions.Height and Width (PcChart.cs lines 42-46)
   * Default: null (no inline dimension styles)
   * ───────────────────────────────────────────────────────────────────────── */
  describe('dimension styling', () => {
    it('applies custom height', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          height="300px"
        />,
      );
      const container = screen.getByRole('img');
      expect(container.style.height).toBe('300px');
    });

    it('applies custom width', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          width="100%"
        />,
      );
      const container = screen.getByRole('img');
      expect(container.style.width).toBe('100%');
    });

    it('renders without explicit dimensions', () => {
      render(<Chart datasets={sampleDatasets} labels={sampleLabels} />);
      const container = screen.getByRole('img');
      /* Default: no height or width inline styles (only position: relative) */
      expect(container.style.height).toBe('');
      expect(container.style.width).toBe('');
      expect(container.style.position).toBe('relative');
    });

    it('applies both height and width', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          height="400px"
          width="600px"
        />,
      );
      const container = screen.getByRole('img');
      expect(container.style.height).toBe('400px');
      expect(container.style.width).toBe('600px');
    });
  });

  /* ─────────────────────────────────────────────────────────────────────────
   * 2.7 Colour Palette Tests
   *
   * PcChart.cs lines 107-114: theme colour arrays (17 border + 17 background)
   * Colour assignment rules (lines 145-164):
   *  • Line/Area: single border + background (first palette entry)
   *  • Bar/HorizontalBar: per-point border, per-point light background
   *  • Pie/Doughnut/Radar/PolarArea: per-point border = per-point background
   * ───────────────────────────────────────────────────────────────────────── */
  describe('colour palette', () => {
    it('auto-assigns colours when datasets is number array', () => {
      const numberData = [10, 20, 30, 40, 50] as unknown as ChartDataset[] | number[] | string;
      render(
        <Chart
          datasets={numberData as any}
          labels={sampleLabels}
          type={ChartType.Line}
        />,
      );

      const lineProps = asMock(Line).mock.calls[0][0] as Record<string, any>;
      const ds = lineProps.data?.datasets;
      expect(ds).toHaveLength(1);
      /* Line type: single colour from first palette entry */
      expect(ds[0].borderColor).toBe(BORDER_PALETTE[0]);
      expect(ds[0].backgroundColor).toBe(BG_LIGHT_PALETTE[0]);
    });

    it('preserves explicitly provided colours', () => {
      const explicitDs: ChartDataset[] = [
        {
          label: 'Custom',
          data: [5, 15, 25],
          borderColor: '#FF00FF',
          backgroundColor: '#00FF00',
        },
      ];
      render(
        <Chart datasets={explicitDs} labels={['X', 'Y', 'Z']} />,
      );

      const lineProps = asMock(Line).mock.calls[0][0] as Record<string, any>;
      const ds = lineProps.data?.datasets;
      expect(ds[0].borderColor).toBe('#FF00FF');
      expect(ds[0].backgroundColor).toBe('#00FF00');
    });
  });

  /* ─────────────────────────────────────────────────────────────────────────
   * 2.8 Canvas Element Tests
   *
   * Verifies that a <canvas> element is present in the rendered output.
   * With mocked react-chartjs-2, each chart mock renders a <canvas> stub
   * with data-testid="chart-canvas" and a data-type attribute.
   * ───────────────────────────────────────────────────────────────────────── */
  describe('canvas element', () => {
    it('contains a canvas element', () => {
      render(<Chart datasets={sampleDatasets} labels={sampleLabels} />);
      const canvas = screen.getByTestId('chart-canvas');
      expect(canvas).toBeDefined();
      expect(canvas.tagName).toBe('CANVAS');
    });

    it('canvas has appropriate test id', () => {
      render(
        <Chart
          datasets={sampleDatasets}
          labels={sampleLabels}
          type={ChartType.Bar}
        />,
      );
      const canvas = screen.getByTestId('chart-canvas');
      expect(canvas).toBeDefined();
      expect(canvas.getAttribute('data-testid')).toBe('chart-canvas');
      expect(canvas.getAttribute('data-type')).toBe('bar');
    });
  });
});
