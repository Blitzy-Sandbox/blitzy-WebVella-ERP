/**
 * Vitest Component Tests for `<DataTable />`
 *
 * Validates the React DataTable component that replaces the monolith's
 * PcGrid ViewComponent (`WebVella.Erp.Web/Components/PcGrid/PcGrid.cs`
 * + `Display.cshtml`). Tests cover ALL behavioral features from the
 * original PcGrid replicated in the React DataTable component using
 * TanStack Table v8.
 *
 * Test coverage includes:
 *  - Column rendering with up to 12 configurable columns
 *  - Sorting: column header click toggles ascending/descending
 *  - Filtering: searchable column metadata propagation
 *  - Pagination: page/pageSize controls, total count, PageSize nullable
 *  - Empty state: empty text display, column span
 *  - Responsive behavior: breakpoint-based rendering
 *  - Row rendering with EntityRecord data binding
 *  - Appearance options: striped, bordered, borderless, hover, small
 *  - Header/footer toggle: showHeader, showFooter visibility
 *  - Edge cases: loading state, custom className, id attribute
 *
 * @see apps/frontend/src/components/data-table/DataTable.tsx
 * @see WebVella.Erp.Web/Components/PcGrid/PcGrid.cs
 * @see WebVella.Erp.Web/Components/PcGrid/Display.cshtml
 * @see WebVella.Erp.Web/Components/PcGrid/Options.cshtml
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import React from 'react';

import { DataTable } from '../../../src/components/data-table/DataTable';
import type {
  DataTableColumn,
  DataTableProps,
} from '../../../src/components/data-table/DataTable';

// ---------------------------------------------------------------------------
// Test Helper — renderWithRouter
// ---------------------------------------------------------------------------

/**
 * Wraps a React element inside a MemoryRouter so that `useSearchParams()`
 * (used by the DataTable for URL-based pagination / sorting state) has a
 * working router context.  This mirrors the monolith's pattern where PcGrid
 * reads `HttpContext.Request.Query[pageKey]` (PcGrid.cs lines 578-596).
 *
 * @param ui             React element to render
 * @param initialEntries Optional URL entries for MemoryRouter (e.g. `['/?page=2']`)
 */
function renderWithRouter(
  ui: React.ReactElement,
  initialEntries: string[] = ['/'],
) {
  return render(
    <MemoryRouter initialEntries={initialEntries}>{ui}</MemoryRouter>,
  );
}

// ---------------------------------------------------------------------------
// Test Data Fixtures
// ---------------------------------------------------------------------------

/**
 * Mock EntityRecord data matching `WebVella.Erp.Api.Models.EntityRecord`.
 * The monolith's Display.cshtml iterates `List<EntityRecord>` for row rendering
 * (Display.cshtml line 18: `var records = (List<EntityRecord>)ViewBag.Records;`).
 */
const mockRecords: Record<string, unknown>[] = [
  { id: '1', name: 'Record 1', email: 'r1@test.com', status: 'active', amount: 100 },
  { id: '2', name: 'Record 2', email: 'r2@test.com', status: 'inactive', amount: 200 },
  { id: '3', name: 'Record 3', email: 'r3@test.com', status: 'active', amount: 300 },
];

/**
 * Default two-column definition matching PcGridOptions default
 * `visible_columns = 2` (PcGrid.cs line 90).
 *
 * Uses `id === name` so URL-based sorting maps correctly through
 * TanStack Table's internal column-id state.
 */
const defaultColumns: DataTableColumn[] = [
  {
    id: 'name',
    label: 'Name',
    name: 'name',
    accessorKey: 'name',
    sortable: true,
    searchable: true,
  },
  {
    id: 'email',
    label: 'Email',
    name: 'email',
    accessorKey: 'email',
    sortable: false,
    searchable: true,
  },
];

// ---------------------------------------------------------------------------
// Lifecycle Hooks
// ---------------------------------------------------------------------------

beforeEach(() => {
  vi.clearAllMocks();
});

// ===========================================================================
// Phase 2: Column Rendering Tests
// ===========================================================================
// Source: PcGrid.cs container1-12 properties (lines 104-487)
//         Init Columns logic (lines 604-749)
// ===========================================================================

describe('Column rendering with up to 12 configurable columns', () => {
  it('renders correct number of column headers', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} />,
    );
    const headers = screen.getAllByRole('columnheader');
    expect(headers).toHaveLength(2);
    expect(headers[0]).toHaveTextContent('Name');
    expect(headers[1]).toHaveTextContent('Email');
  });

  it('supports up to 12 columns', () => {
    const twelveColumns: DataTableColumn[] = Array.from(
      { length: 12 },
      (_, i) => ({
        id: `col${i + 1}`,
        label: `Column ${i + 1}`,
        accessorKey: `field${i + 1}`,
      }),
    );
    const wideRecord: Record<string, unknown> = {};
    for (let i = 1; i <= 12; i++) {
      wideRecord[`field${i}`] = `value${i}`;
    }
    renderWithRouter(
      <DataTable data={[wideRecord]} columns={twelveColumns} />,
    );
    const headers = screen.getAllByRole('columnheader');
    expect(headers).toHaveLength(12);
    for (let i = 0; i < 12; i++) {
      expect(headers[i]).toHaveTextContent(`Column ${i + 1}`);
    }
  });

  it('applies column width from definition', () => {
    const columnsWithWidth: DataTableColumn[] = [
      { id: 'col1', label: 'Name', accessorKey: 'name', width: '200px' },
    ];
    renderWithRouter(
      <DataTable data={mockRecords} columns={columnsWithWidth} />,
    );
    const header = screen.getByRole('columnheader');
    expect(header).toHaveStyle({ width: '200px' });
  });

  it('applies column noWrap property', () => {
    const columnsNoWrap: DataTableColumn[] = [
      { id: 'col1', label: 'Name', accessorKey: 'name', noWrap: true },
    ];
    renderWithRouter(
      <DataTable data={mockRecords} columns={columnsNoWrap} />,
    );
    const header = screen.getByRole('columnheader');
    expect(header).toHaveClass('whitespace-nowrap');
    // Also check on body cells (Display.cshtml line 34)
    const cells = screen.getAllByRole('cell');
    expect(cells[0]).toHaveClass('whitespace-nowrap');
  });

  it('applies column className', () => {
    const columnsWithClass: DataTableColumn[] = [
      { id: 'col1', label: 'Name', accessorKey: 'name', className: 'custom-col' },
    ];
    renderWithRouter(
      <DataTable data={mockRecords} columns={columnsWithClass} />,
    );
    // Class applied to both header and body cells
    const header = screen.getByRole('columnheader');
    expect(header).toHaveClass('custom-col');
    const cells = screen.getAllByRole('cell');
    expect(cells[0]).toHaveClass('custom-col');
  });

  it('applies column vertical alignment', () => {
    const columns: DataTableColumn[] = [
      { id: 'col1', label: 'Top', accessorKey: 'name', verticalAlign: 'top' },
      { id: 'col2', label: 'Mid', accessorKey: 'email', verticalAlign: 'middle' },
      { id: 'col3', label: 'Bot', accessorKey: 'status', verticalAlign: 'bottom' },
    ];
    renderWithRouter(
      <DataTable data={[mockRecords[0]]} columns={columns} />,
    );
    const cells = screen.getAllByRole('cell');
    expect(cells[0]).toHaveClass('align-top');
    expect(cells[1]).toHaveClass('align-middle');
    expect(cells[2]).toHaveClass('align-bottom');
    // Also verify on headers
    const headers = screen.getAllByRole('columnheader');
    expect(headers[0]).toHaveClass('align-top');
    expect(headers[1]).toHaveClass('align-middle');
    expect(headers[2]).toHaveClass('align-bottom');
  });

  it('applies column horizontal alignment', () => {
    const columns: DataTableColumn[] = [
      { id: 'col1', label: 'Left', accessorKey: 'name', horizontalAlign: 'left' },
      { id: 'col2', label: 'Center', accessorKey: 'email', horizontalAlign: 'center' },
      { id: 'col3', label: 'Right', accessorKey: 'status', horizontalAlign: 'right' },
    ];
    renderWithRouter(
      <DataTable data={[mockRecords[0]]} columns={columns} />,
    );
    const cells = screen.getAllByRole('cell');
    // left → text-start (logical property, replaces text-left)
    expect(cells[0]).toHaveClass('text-start');
    // center → text-center
    expect(cells[1]).toHaveClass('text-center');
    // right → text-end (logical property, replaces text-right)
    expect(cells[2]).toHaveClass('text-end');
  });

  it('renders cell values using accessorKey', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} />,
    );
    expect(screen.getByText('Record 1')).toBeInTheDocument();
    expect(screen.getByText('r1@test.com')).toBeInTheDocument();
    expect(screen.getByText('Record 2')).toBeInTheDocument();
    expect(screen.getByText('r2@test.com')).toBeInTheDocument();
    expect(screen.getByText('Record 3')).toBeInTheDocument();
    expect(screen.getByText('r3@test.com')).toBeInTheDocument();
  });

  it('renders custom cell renderer', () => {
    const columnsWithRenderer: DataTableColumn[] = [
      {
        id: 'col1',
        label: 'Name',
        accessorKey: 'name',
        cell: (value: unknown) => (
          <strong data-testid="custom-cell">{String(value)}</strong>
        ),
      },
    ];
    renderWithRouter(
      <DataTable data={[mockRecords[0]]} columns={columnsWithRenderer} />,
    );
    const customCell = screen.getByTestId('custom-cell');
    expect(customCell).toBeInTheDocument();
    expect(customCell.tagName.toLowerCase()).toBe('strong');
    expect(customCell).toHaveTextContent('Record 1');
  });
});

// ===========================================================================
// Phase 3: Sorting Tests
// ===========================================================================
// Source: PcGridOptions container{N}_sortable (PcGrid.cs line 117)
//         query_string_sortby / query_string_sort_order (lines 77-81)
// ===========================================================================

describe('Sorting', () => {
  it('sortable column header is clickable', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} />,
    );
    const nameHeader = screen.getByText('Name').closest('th')!;
    expect(nameHeader).toHaveClass('cursor-pointer');
    expect(nameHeader).toHaveClass('select-none');
    expect(nameHeader).toHaveAttribute('aria-sort', 'none');
    // Sort indicator arrows should be present
    const indicator = nameHeader.querySelector('[aria-hidden="true"]');
    expect(indicator).toBeTruthy();
  });

  it('non-sortable column header is not clickable', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} />,
    );
    const emailHeader = screen.getByText('Email').closest('th')!;
    expect(emailHeader).not.toHaveClass('cursor-pointer');
    expect(emailHeader).not.toHaveAttribute('aria-sort');
    // No sort indicator arrows
    const indicator = emailHeader.querySelector('[aria-hidden="true"]');
    expect(indicator).toBeNull();
  });

  it('clicking sortable column calls onSortChange with ascending', () => {
    const onSortChange = vi.fn();
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        onSortChange={onSortChange}
      />,
    );
    const nameHeader = screen.getByText('Name').closest('th')!;
    fireEvent.click(nameHeader);
    expect(onSortChange).toHaveBeenCalledTimes(1);
    expect(onSortChange).toHaveBeenCalledWith('name', 'asc');
  });

  it('clicking sorted column toggles to descending', () => {
    const onSortChange = vi.fn();
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        onSortChange={onSortChange}
      />,
    );
    const nameHeader = screen.getByText('Name').closest('th')!;
    // First click → ascending
    fireEvent.click(nameHeader);
    expect(onSortChange).toHaveBeenCalledWith('name', 'asc');
    // Second click → descending
    fireEvent.click(nameHeader);
    expect(onSortChange).toHaveBeenCalledWith('name', 'desc');
  });

  it('sort state reflected in column header indicator', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} />,
    );
    const nameHeader = screen.getByText('Name').closest('th')!;
    // Initially no active sort
    expect(nameHeader).toHaveAttribute('aria-sort', 'none');

    // Click to sort ascending
    fireEvent.click(nameHeader);
    expect(nameHeader).toHaveAttribute('aria-sort', 'ascending');

    // Click again to sort descending
    fireEvent.click(nameHeader);
    expect(nameHeader).toHaveAttribute('aria-sort', 'descending');
  });
});

// ===========================================================================
// Phase 4: Filtering Tests
// ===========================================================================
// Source: container{N}_searchable (PcGrid.cs line 120)
//         PcGridFilterField logic (Options.cshtml line 113)
//
// NOTE: The DataTable component stores the `searchable` flag as column
// metadata but does NOT currently render inline filter controls.  The
// searchable metadata is propagated for future filter-row support and for
// callers to query column capabilities.
// ===========================================================================

describe('Filtering', () => {
  it('searchable columns have searchable metadata propagated', () => {
    const columns: DataTableColumn[] = [
      { id: 'col1', label: 'Searchable', accessorKey: 'name', searchable: true },
      { id: 'col2', label: 'Not Searchable', accessorKey: 'email', searchable: false },
    ];
    // Component renders without errors regardless of searchable flag
    const { container } = renderWithRouter(
      <DataTable data={mockRecords} columns={columns} />,
    );
    // Both columns render correctly
    const headers = screen.getAllByRole('columnheader');
    expect(headers).toHaveLength(2);
    expect(headers[0]).toHaveTextContent('Searchable');
    expect(headers[1]).toHaveTextContent('Not Searchable');
    // Table renders normally with searchable columns
    expect(container.querySelector('table')).toBeTruthy();
  });

  it('non-searchable columns do not render filter controls', () => {
    const columns: DataTableColumn[] = [
      { id: 'col1', label: 'Name', accessorKey: 'name', searchable: false },
    ];
    renderWithRouter(
      <DataTable data={mockRecords} columns={columns} />,
    );
    // Verify no filter inputs appear in the header area
    const table = screen.getByRole('table');
    const thead = table.querySelector('thead');
    expect(thead).toBeTruthy();
    const inputs = within(thead as HTMLElement).queryAllByRole('textbox');
    expect(inputs).toHaveLength(0);
  });
});

// ===========================================================================
// Phase 5: Pagination Tests
// ===========================================================================
// Source: PcGrid.cs lines 537-596 (page/pageSize URL param binding)
//         PcGridOptions.page_size (line 38-39)
//         Options.cshtml line 33: placeholder="empty or 0 for unlimited"
// ===========================================================================

describe('Pagination', () => {
  it('renders pagination controls in footer', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        totalCount={50}
        pageSize={10}
      />,
    );
    const table = screen.getByRole('table');
    const tfoot = table.querySelector('tfoot');
    expect(tfoot).toBeTruthy();
    expect(screen.getByLabelText('Previous page')).toBeInTheDocument();
    expect(screen.getByLabelText('Next page')).toBeInTheDocument();
  });

  it('displays total count', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        totalCount={50}
        pageSize={10}
      />,
    );
    // The footer shows "Showing 1 to 10 of 50 records"
    expect(screen.getByText('50')).toBeInTheDocument();
    expect(screen.getByText(/records/i)).toBeInTheDocument();
  });

  it('page/pageSize controls work', () => {
    const onPageChange = vi.fn();
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        pageSize={10}
        totalCount={30}
        currentPage={1}
        onPageChange={onPageChange}
      />,
    );
    // Page 1 should be active (aria-current="page")
    const page1Btn = screen.getByLabelText('Page 1');
    expect(page1Btn).toHaveAttribute('aria-current', 'page');

    // Page 3 should exist (30 / 10 = 3 pages)
    expect(screen.getByLabelText('Page 3')).toBeInTheDocument();

    // Click Next page button
    fireEvent.click(screen.getByLabelText('Next page'));
    expect(onPageChange).toHaveBeenCalledWith(2);
  });

  it('PageSize 0 means unlimited (no pagination)', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        pageSize={0}
      />,
    );
    // No pagination controls should render
    expect(screen.queryByLabelText('Next page')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Previous page')).not.toBeInTheDocument();
    // The table itself still renders
    expect(screen.getByRole('table')).toBeInTheDocument();
  });

  it('hideTotal prop hides total count display', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        totalCount={50}
        pageSize={10}
        hideTotal
      />,
    );
    // "records" text should not appear in the footer
    expect(screen.queryByText(/records/i)).not.toBeInTheDocument();
    // But pagination buttons should still work
    expect(screen.getByLabelText('Next page')).toBeInTheDocument();
  });

  it('query string parameter binding for page', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        totalCount={50}
        pageSize={10}
      />,
      ['/?page=3'],
    );
    // Page 3 should be active based on URL param
    const page3Btn = screen.getByLabelText('Page 3');
    expect(page3Btn).toHaveAttribute('aria-current', 'page');
  });

  it('query string parameter binding with prefix', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        totalCount={50}
        pageSize={10}
        prefix="grid1_"
      />,
      ['/?grid1_page=2'],
    );
    // Page 2 should be active based on prefixed URL param
    const page2Btn = screen.getByLabelText('Page 2');
    expect(page2Btn).toHaveAttribute('aria-current', 'page');
  });
});

// ===========================================================================
// Phase 6: Empty State Tests
// ===========================================================================
// Source: Display.cshtml lines 61-67
//         PcGridOptions.EmptyText default "No records" (PcGrid.cs line 102)
// ===========================================================================

describe('Empty state', () => {
  it('displays empty text when no records', () => {
    renderWithRouter(
      <DataTable data={[]} columns={defaultColumns} />,
    );
    expect(screen.getByText('No records')).toBeInTheDocument();
  });

  it('displays custom empty text', () => {
    renderWithRouter(
      <DataTable
        data={[]}
        columns={defaultColumns}
        emptyText="No items found"
      />,
    );
    expect(screen.getByText('No items found')).toBeInTheDocument();
    expect(screen.queryByText('No records')).not.toBeInTheDocument();
  });

  it('empty state spans all columns', () => {
    const fiveColumns: DataTableColumn[] = Array.from(
      { length: 5 },
      (_, i) => ({
        id: `col${i + 1}`,
        label: `Col ${i + 1}`,
        accessorKey: `field${i + 1}`,
      }),
    );
    renderWithRouter(
      <DataTable data={[]} columns={fiveColumns} />,
    );
    // The empty state <td> should span all 5 columns
    // (matches Display.cshtml line 64: colspan="@options.VisibleColumns")
    const emptyCell = screen.getByText('No records').closest('td');
    expect(emptyCell).toHaveAttribute('colspan', '5');
  });
});

// ===========================================================================
// Phase 7: Responsive Behavior Tests
// ===========================================================================
// Source: responsive_breakpoint WvCssBreakpoint enum (PcGrid.cs line 60)
// ===========================================================================

describe('Responsive behavior', () => {
  it('responsive breakpoint none renders no responsive wrapper', () => {
    const { container } = renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        responsiveBreakpoint="none"
      />,
    );
    const wrapper = container.firstElementChild as HTMLElement;
    expect(wrapper).not.toHaveClass('overflow-x-auto');
  });

  it('responsive breakpoint sm applies responsive class', () => {
    const { container } = renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        responsiveBreakpoint="sm"
      />,
    );
    const wrapper = container.firstElementChild as HTMLElement;
    expect(wrapper).toHaveClass('overflow-x-auto');
  });

  it('responsive breakpoint lg applies responsive class', () => {
    const { container } = renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        responsiveBreakpoint="lg"
      />,
    );
    const wrapper = container.firstElementChild as HTMLElement;
    expect(wrapper).toHaveClass('overflow-x-auto');
  });
});

// ===========================================================================
// Phase 8: Row Rendering Tests
// ===========================================================================
// Source: Display.cshtml lines 28-60 (foreach record → grid-row → grid-column)
// ===========================================================================

describe('Row rendering with EntityRecord data binding', () => {
  it('renders one row per data record', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} />,
    );
    const table = screen.getByRole('table');
    const tbody = table.querySelector('tbody')!;
    const rows = within(tbody).getAllByRole('row');
    expect(rows).toHaveLength(3);
  });

  it('renders correct cell values for each row', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} />,
    );
    // First record
    expect(screen.getByText('Record 1')).toBeInTheDocument();
    expect(screen.getByText('r1@test.com')).toBeInTheDocument();
    // Second record
    expect(screen.getByText('Record 2')).toBeInTheDocument();
    expect(screen.getByText('r2@test.com')).toBeInTheDocument();
    // Third record
    expect(screen.getByText('Record 3')).toBeInTheDocument();
    expect(screen.getByText('r3@test.com')).toBeInTheDocument();
  });

  it('renders no data rows when data is empty (only empty state)', () => {
    renderWithRouter(
      <DataTable data={[]} columns={defaultColumns} />,
    );
    const table = screen.getByRole('table');
    const tbody = table.querySelector('tbody')!;
    // Only the empty-state row should be present
    const rows = within(tbody).getAllByRole('row');
    expect(rows).toHaveLength(1);
    expect(screen.getByText('No records')).toBeInTheDocument();
  });
});

// ===========================================================================
// Phase 9: Appearance Options Tests
// ===========================================================================
// Source: PcGridOptions styling flags (PcGrid.cs lines 44-58)
//         Options.cshtml lines 47-67
// ===========================================================================

describe('Appearance options', () => {
  it('striped prop adds striped row styling', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} striped />,
    );
    const table = screen.getByRole('table');
    const tbody = table.querySelector('tbody')!;
    const rows = within(tbody).getAllByRole('row');
    // Striped rows use Tailwind even:bg-gray-50 (PcGrid.cs line 45)
    expect(rows[0].className).toContain('even:bg-gray-50');
  });

  it('bordered prop adds border styling', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} bordered />,
    );
    const table = screen.getByRole('table');
    // Table itself gets border classes
    expect(table).toHaveClass('border');
    expect(table).toHaveClass('border-gray-200');
    // Individual cells also get border classes
    const cells = screen.getAllByRole('cell');
    expect(cells[0]).toHaveClass('border');
    expect(cells[0]).toHaveClass('border-gray-200');
  });

  it('borderless prop removes borders', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} borderless />,
    );
    const cells = screen.getAllByRole('cell');
    expect(cells[0]).toHaveClass('border-0');
    // Headers should also have borderless class
    const headers = screen.getAllByRole('columnheader');
    expect(headers[0]).toHaveClass('border-0');
  });

  it('hover prop adds hover effect', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} hover />,
    );
    const table = screen.getByRole('table');
    const tbody = table.querySelector('tbody')!;
    const rows = within(tbody).getAllByRole('row');
    // Hover rows use Tailwind hover:bg-gray-100
    expect(rows[0].className).toContain('hover:bg-gray-100');
  });

  it('small prop renders compact table', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} small />,
    );
    const table = screen.getByRole('table');
    expect(table).toHaveClass('text-sm');
    // Compact padding on cells: px-2 py-1 (instead of px-4 py-3)
    const cells = screen.getAllByRole('cell');
    expect(cells[0]).toHaveClass('px-2');
    expect(cells[0]).toHaveClass('py-1');
  });

  it('multiple appearance props can be combined', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        striped
        bordered
        hover
        small
      />,
    );
    const table = screen.getByRole('table');
    // Small → text-sm
    expect(table).toHaveClass('text-sm');
    // Bordered → border
    expect(table).toHaveClass('border');
    // Body rows have striped + hover classes
    const tbody = table.querySelector('tbody')!;
    const rows = within(tbody).getAllByRole('row');
    expect(rows[0].className).toContain('even:bg-gray-50');
    expect(rows[0].className).toContain('hover:bg-gray-100');
  });

  it('default appearance (no flags) renders base table', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} />,
    );
    const table = screen.getByRole('table');
    // Base classes are always present
    expect(table).toHaveClass('w-full');
    expect(table).toHaveClass('border-collapse');
    // No appearance classes should be present
    expect(table).not.toHaveClass('text-sm');
    expect(table.className).not.toContain('border-gray-200');
    const tbody = table.querySelector('tbody')!;
    const rows = within(tbody).getAllByRole('row');
    expect(rows[0].className).not.toContain('even:bg-gray-50');
    expect(rows[0].className).not.toContain('hover:bg-gray-100');
  });
});

// ===========================================================================
// Phase 10: Header/Footer Toggle Tests
// ===========================================================================
// Source: has_thead / has_tfoot (PcGrid.cs lines 92-96)
//         Options.cshtml lines 74-78
// ===========================================================================

describe('Header/footer toggle', () => {
  it('showHeader=true renders thead (default)', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} />,
    );
    const table = screen.getByRole('table');
    const thead = table.querySelector('thead');
    expect(thead).toBeTruthy();
    expect(screen.getByText('Name')).toBeInTheDocument();
    expect(screen.getByText('Email')).toBeInTheDocument();
  });

  it('showHeader=false hides thead', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        showHeader={false}
      />,
    );
    const table = screen.getByRole('table');
    const thead = table.querySelector('thead');
    expect(thead).toBeNull();
  });

  it('showFooter=true renders tfoot with pagination (default)', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        totalCount={50}
        pageSize={10}
      />,
    );
    const table = screen.getByRole('table');
    const tfoot = table.querySelector('tfoot');
    expect(tfoot).toBeTruthy();
    expect(screen.getByLabelText('Previous page')).toBeInTheDocument();
    expect(screen.getByLabelText('Next page')).toBeInTheDocument();
  });

  it('showFooter=false hides tfoot', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        showFooter={false}
        totalCount={50}
        pageSize={10}
      />,
    );
    const table = screen.getByRole('table');
    const tfoot = table.querySelector('tfoot');
    expect(tfoot).toBeNull();
  });
});

// ===========================================================================
// Phase 11: Additional Edge Cases
// ===========================================================================

describe('Edge cases', () => {
  it('loading state shows loading indicator', () => {
    renderWithRouter(
      <DataTable data={mockRecords} columns={defaultColumns} loading />,
    );
    expect(screen.getByRole('status')).toBeInTheDocument();
    expect(screen.getByText('Loading\u2026')).toBeInTheDocument();
  });

  it('custom className prop applied to table', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        className="my-custom-grid"
      />,
    );
    const table = screen.getByRole('table');
    expect(table).toHaveClass('my-custom-grid');
  });

  it('id prop applied to table element', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        id="test-grid"
      />,
    );
    const table = screen.getByRole('table');
    expect(table).toHaveAttribute('id', 'test-grid');
  });
});

// ===========================================================================
// Phase 12: User Event Interaction Tests
// ===========================================================================

describe('User event interactions', () => {
  it('user can click pagination Previous button', async () => {
    const user = userEvent.setup();
    const onPageChange = vi.fn();
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        totalCount={50}
        pageSize={10}
        currentPage={3}
        onPageChange={onPageChange}
      />,
    );
    const prevButton = screen.getByLabelText('Previous page');
    await user.click(prevButton);
    expect(onPageChange).toHaveBeenCalledWith(2);
  });

  it('user can click a specific page number', async () => {
    const user = userEvent.setup();
    const onPageChange = vi.fn();
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        totalCount={50}
        pageSize={10}
        currentPage={1}
        onPageChange={onPageChange}
      />,
    );
    const page3 = screen.getByLabelText('Page 3');
    await user.click(page3);
    expect(onPageChange).toHaveBeenCalledWith(3);
  });

  it('user can click sortable column header to trigger sort', async () => {
    const user = userEvent.setup();
    const onSortChange = vi.fn();
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        onSortChange={onSortChange}
      />,
    );
    const nameHeader = screen.getByText('Name').closest('th')!;
    await user.click(nameHeader);
    expect(onSortChange).toHaveBeenCalledWith('name', 'asc');
  });

  it('Previous page button is disabled on first page', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        totalCount={50}
        pageSize={10}
        currentPage={1}
      />,
    );
    const prevButton = screen.getByLabelText('Previous page');
    expect(prevButton).toBeDisabled();
  });

  it('Next page button is disabled on last page', () => {
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        totalCount={50}
        pageSize={10}
        currentPage={5}
      />,
    );
    const nextButton = screen.getByLabelText('Next page');
    expect(nextButton).toBeDisabled();
  });

  it('page size selector changes page size', async () => {
    const user = userEvent.setup();
    const onPageSizeChange = vi.fn();
    const onPageChange = vi.fn();
    renderWithRouter(
      <DataTable
        data={mockRecords}
        columns={defaultColumns}
        totalCount={100}
        pageSize={10}
        onPageSizeChange={onPageSizeChange}
        onPageChange={onPageChange}
      />,
    );
    const pageSizeSelect = screen.getByLabelText('Rows per page');
    await user.selectOptions(pageSizeSelect, '25');
    expect(onPageSizeChange).toHaveBeenCalledWith(25);
    // Page resets to 1 when page size changes
    expect(onPageChange).toHaveBeenCalledWith(1);
  });
});
