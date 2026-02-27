/**
 * Vitest Component Tests for `<DataCsvField />`
 *
 * Validates the React DataCsvField component
 * (`apps/frontend/src/components/fields/DataCsvField.tsx`) that replaces
 * the monolith's `PcFieldDataCsv` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldDataCsv/PcFieldDataCsv.cs`).
 *
 * The monolith's PcFieldDataCsvOptions extend PcFieldBaseOptions with:
 *   - height (string): CSS height for the textarea editor
 *   - delimiter_value_ds (default "comma"): WvCsvDelimiterType.COMMA or TAB
 *   - has_header_value_ds (default "true"): whether the first row is a header
 *   - has_header_column_value_ds (default "false"): whether the first column is a header
 *   - lang_ds (default "en"): language code
 *
 * Test coverage spans:
 *   - Display mode: CSV string → HTML table rendering, header rows/columns,
 *     comma/tab delimiter, empty value handling
 *   - Edit mode: monospace textarea, live preview table, onChange callback,
 *     height CSS, delimiter toolbar, header row toggle
 *   - CSV parsing: row/column splitting, quoted fields, whitespace trimming,
 *     empty cells, single row/column edge cases
 *   - Delimiter support: default comma, tab switching, correct parsing
 *   - Header handling: hasHeader=true/false, <th> elements, hasHeaderColumn
 *   - Access control: full / readonly / forbidden modes
 *   - Validation: error messages, validation error display
 *   - Null/empty handling: null value, empty string value
 *   - Visibility: isVisible=true renders, isVisible=false renders nothing
 *
 * @see apps/frontend/src/components/fields/DataCsvField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldDataCsv/PcFieldDataCsv.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import DataCsvField from '../../../src/components/fields/DataCsvField';
import type { DataCsvFieldProps, CsvDelimiterType } from '../../../src/components/fields/DataCsvField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default DataCsvFieldProps for consistent test setup.
 * Mirrors the PcFieldDataCsvOptions defaults from PcFieldDataCsv.cs (lines 25–70):
 *   - Height = ""
 *   - DelimiterValueDs = "comma"
 *   - HasHeaderValueDs = "true"
 *   - HasHeaderColumnValueDs = "false"
 *   - LangDs = "en"
 */
function createDefaultProps(
  overrides: Partial<DataCsvFieldProps> = {},
): DataCsvFieldProps {
  return {
    name: 'csv_field',
    value: null,
    ...overrides,
  };
}

/** Simple comma-separated CSV fixture with header */
const SIMPLE_CSV = 'Name,Age,City\nAlice,30,Paris\nBob,25,London';

/** Tab-separated CSV fixture with header */
const TAB_CSV = 'Name\tAge\tCity\nAlice\t30\tParis\nBob\t25\tLondon';

/** CSV fixture with quoted fields containing delimiters */
const QUOTED_CSV = '"Last, First",Age,"City, State"\n"Doe, John",40,"New York, NY"';

/** Single row CSV (no newlines, just one header row) */
const SINGLE_ROW_CSV = 'Name,Age,City';

/** Single column CSV (no delimiters) */
const SINGLE_COL_CSV = 'Name\nAlice\nBob';

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('DataCsvField', () => {
  afterEach(() => {
    cleanup();
  });

  // =========================================================================
  // Display Mode
  // =========================================================================

  describe('display mode', () => {
    it('parses CSV string into HTML table', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            hasHeader: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      expect(table).toBeInTheDocument();

      // Header row should have th elements
      const thElements = within(table).getAllByRole('columnheader');
      expect(thElements).toHaveLength(3);
      expect(thElements[0]).toHaveTextContent('Name');
      expect(thElements[1]).toHaveTextContent('Age');
      expect(thElements[2]).toHaveTextContent('City');

      // Data rows
      const rows = within(table).getAllByRole('row');
      // 1 header row + 2 data rows = 3 total
      expect(rows).toHaveLength(3);

      // Verify data cells
      const cells = within(table).getAllByRole('cell');
      expect(cells[0]).toHaveTextContent('Alice');
      expect(cells[1]).toHaveTextContent('30');
      expect(cells[2]).toHaveTextContent('Paris');
    });

    it('renders header row when hasHeader=true', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            hasHeader: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      const thElements = within(table).getAllByRole('columnheader');
      expect(thElements).toHaveLength(3);
      expect(thElements[0]).toHaveTextContent('Name');
    });

    it('renders first column as header when hasHeaderColumn=true', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            hasHeader: true,
            hasHeaderColumn: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      // First column cells in body rows should be <th> with scope="row"
      const rowHeaders = within(table).getAllByRole('rowheader');
      expect(rowHeaders.length).toBeGreaterThan(0);
      expect(rowHeaders[0]).toHaveTextContent('Alice');
    });

    it('handles comma delimiter correctly', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            delimiter: 'comma',
            hasHeader: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      const thElements = within(table).getAllByRole('columnheader');
      expect(thElements).toHaveLength(3);
      expect(thElements[0]).toHaveTextContent('Name');
      expect(thElements[1]).toHaveTextContent('Age');
      expect(thElements[2]).toHaveTextContent('City');
    });

    it('handles tab delimiter correctly', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: TAB_CSV,
            mode: 'display',
            delimiter: 'tab',
            hasHeader: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      const thElements = within(table).getAllByRole('columnheader');
      expect(thElements).toHaveLength(3);
      expect(thElements[0]).toHaveTextContent('Name');
      expect(thElements[1]).toHaveTextContent('Age');
      expect(thElements[2]).toHaveTextContent('City');
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: null,
            mode: 'display',
            emptyValueMessage: 'no data',
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
      // No table should be rendered
      expect(screen.queryByRole('table')).not.toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is empty string', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: '',
            mode: 'display',
            emptyValueMessage: 'no data',
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('table')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Edit Mode
  // =========================================================================

  describe('edit mode', () => {
    it('renders textarea for raw CSV input with monospace font', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'edit',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toBeInTheDocument();
      expect(textarea.tagName.toLowerCase()).toBe('textarea');
      // Monospace font is applied via Tailwind's font-mono class
      expect(textarea).toHaveClass('font-mono');
    });

    it('shows live preview table below textarea', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'edit',
          })}
        />,
      );

      // Should have both textarea and a table preview
      expect(screen.getByRole('textbox')).toBeInTheDocument();
      expect(screen.getByText('Preview')).toBeInTheDocument();
      expect(screen.getByRole('table')).toBeInTheDocument();
    });

    it('calls onChange with updated CSV string when textarea edited', async () => {
      const onChangeMock = vi.fn();
      const user = userEvent.setup();

      render(
        <DataCsvField
          {...createDefaultProps({
            value: '',
            mode: 'edit',
            onChange: onChangeMock,
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      await user.type(textarea, 'A,B');

      // onChange should have been called with each keystroke
      expect(onChangeMock).toHaveBeenCalled();
      // Last call should have the full typed string
      const lastCallValue = onChangeMock.mock.calls[onChangeMock.mock.calls.length - 1][0];
      expect(lastCallValue).toBe('A,B');
    });

    it('applies height prop as CSS style', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'edit',
            height: '300px',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toHaveStyle({ height: '300px' });
    });

    it('supports delimiter toggle (comma/tab)', async () => {
      const user = userEvent.setup();

      render(
        <DataCsvField
          {...createDefaultProps({
            value: TAB_CSV,
            mode: 'edit',
            delimiter: 'comma',
          })}
        />,
      );

      // There should be Comma and Tab buttons
      const tabButton = screen.getByRole('button', { name: /tab/i });
      expect(tabButton).toBeInTheDocument();

      // Click the Tab button to switch delimiter
      await user.click(tabButton);

      // After switching to tab delimiter, the tab-separated values should
      // display correctly in the preview table
      const table = screen.getByRole('table');
      expect(table).toBeInTheDocument();
    });

    it('supports header row toggle', async () => {
      const user = userEvent.setup();

      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'edit',
          })}
        />,
      );

      // Header row checkbox should exist
      const headerCheckbox = screen.getByRole('checkbox');
      expect(headerCheckbox).toBeInTheDocument();
      // Default is checked (hasHeader=true by default)
      expect(headerCheckbox).toBeChecked();

      // Click to toggle it off
      await user.click(headerCheckbox);
      expect(headerCheckbox).not.toBeChecked();
    });
  });

  // =========================================================================
  // CSV Parsing
  // =========================================================================

  describe('CSV parsing', () => {
    it('splits rows by newline', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: 'A\nB\nC',
            mode: 'display',
            hasHeader: false,
          })}
        />,
      );

      const table = screen.getByRole('table');
      const rows = within(table).getAllByRole('row');
      expect(rows).toHaveLength(3);
    });

    it('splits columns by comma delimiter', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: 'A,B,C',
            mode: 'display',
            delimiter: 'comma',
            hasHeader: false,
          })}
        />,
      );

      const cells = screen.getAllByRole('cell');
      expect(cells).toHaveLength(3);
      expect(cells[0]).toHaveTextContent('A');
      expect(cells[1]).toHaveTextContent('B');
      expect(cells[2]).toHaveTextContent('C');
    });

    it('splits columns by tab delimiter', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: 'A\tB\tC',
            mode: 'display',
            delimiter: 'tab',
            hasHeader: false,
          })}
        />,
      );

      const cells = screen.getAllByRole('cell');
      expect(cells).toHaveLength(3);
      expect(cells[0]).toHaveTextContent('A');
      expect(cells[1]).toHaveTextContent('B');
      expect(cells[2]).toHaveTextContent('C');
    });

    it('handles quoted fields containing delimiters', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: QUOTED_CSV,
            mode: 'display',
            delimiter: 'comma',
            hasHeader: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      // The quoted field "Last, First" should be rendered as a single cell header
      const thElements = within(table).getAllByRole('columnheader');
      expect(thElements[0]).toHaveTextContent('Last, First');
      expect(thElements[1]).toHaveTextContent('Age');
      expect(thElements[2]).toHaveTextContent('City, State');

      // Body row with quoted values
      const cells = within(table).getAllByRole('cell');
      expect(cells[0]).toHaveTextContent('Doe, John');
      expect(cells[1]).toHaveTextContent('40');
      expect(cells[2]).toHaveTextContent('New York, NY');
    });

    it('trims whitespace from cells', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: ' A , B , C ',
            mode: 'display',
            delimiter: 'comma',
            hasHeader: false,
          })}
        />,
      );

      const cells = screen.getAllByRole('cell');
      expect(cells[0]).toHaveTextContent('A');
      expect(cells[1]).toHaveTextContent('B');
      expect(cells[2]).toHaveTextContent('C');
    });

    it('handles empty cells', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: 'A,,C',
            mode: 'display',
            delimiter: 'comma',
            hasHeader: false,
          })}
        />,
      );

      const cells = screen.getAllByRole('cell');
      expect(cells).toHaveLength(3);
      expect(cells[0]).toHaveTextContent('A');
      expect(cells[1]).toHaveTextContent('');
      expect(cells[2]).toHaveTextContent('C');
    });

    it('handles single row CSV', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SINGLE_ROW_CSV,
            mode: 'display',
            delimiter: 'comma',
            hasHeader: false,
          })}
        />,
      );

      const table = screen.getByRole('table');
      const rows = within(table).getAllByRole('row');
      expect(rows).toHaveLength(1);
      const cells = within(table).getAllByRole('cell');
      expect(cells).toHaveLength(3);
      expect(cells[0]).toHaveTextContent('Name');
      expect(cells[1]).toHaveTextContent('Age');
      expect(cells[2]).toHaveTextContent('City');
    });

    it('handles single column CSV', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SINGLE_COL_CSV,
            mode: 'display',
            delimiter: 'comma',
            hasHeader: false,
          })}
        />,
      );

      const table = screen.getByRole('table');
      const rows = within(table).getAllByRole('row');
      expect(rows).toHaveLength(3);
      // Each row should have exactly 1 cell
      for (const row of rows) {
        const cells = within(row).getAllByRole('cell');
        expect(cells).toHaveLength(1);
      }
    });
  });

  // =========================================================================
  // Delimiter Support
  // =========================================================================

  describe('delimiter support', () => {
    it('defaults to comma delimiter', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            hasHeader: true,
          })}
        />,
      );

      // With default comma delimiter, the comma-separated fixture should parse correctly
      const table = screen.getByRole('table');
      const thElements = within(table).getAllByRole('columnheader');
      expect(thElements).toHaveLength(3);
      expect(thElements[0]).toHaveTextContent('Name');
    });

    it('switches to tab delimiter when delimiter="tab"', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: TAB_CSV,
            mode: 'display',
            delimiter: 'tab' as CsvDelimiterType,
            hasHeader: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      const thElements = within(table).getAllByRole('columnheader');
      expect(thElements).toHaveLength(3);
    });

    it('parses correctly with comma delimiter', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: 'X,Y\n1,2\n3,4',
            mode: 'display',
            delimiter: 'comma' as CsvDelimiterType,
            hasHeader: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      const thElements = within(table).getAllByRole('columnheader');
      expect(thElements[0]).toHaveTextContent('X');
      expect(thElements[1]).toHaveTextContent('Y');

      const cells = within(table).getAllByRole('cell');
      expect(cells[0]).toHaveTextContent('1');
      expect(cells[1]).toHaveTextContent('2');
      expect(cells[2]).toHaveTextContent('3');
      expect(cells[3]).toHaveTextContent('4');
    });

    it('parses correctly with tab delimiter', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: 'X\tY\n1\t2\n3\t4',
            mode: 'display',
            delimiter: 'tab' as CsvDelimiterType,
            hasHeader: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      const thElements = within(table).getAllByRole('columnheader');
      expect(thElements[0]).toHaveTextContent('X');
      expect(thElements[1]).toHaveTextContent('Y');

      const cells = within(table).getAllByRole('cell');
      expect(cells[0]).toHaveTextContent('1');
      expect(cells[1]).toHaveTextContent('2');
      expect(cells[2]).toHaveTextContent('3');
      expect(cells[3]).toHaveTextContent('4');
    });
  });

  // =========================================================================
  // Header Handling
  // =========================================================================

  describe('header handling', () => {
    it('treats first row as header when hasHeader=true (default)', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            hasHeader: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      // First row = header → rendered as <th> in <thead>
      const thElements = within(table).getAllByRole('columnheader');
      expect(thElements.length).toBeGreaterThan(0);
      expect(thElements[0]).toHaveTextContent('Name');

      // Body rows should not include the header text as a cell
      const cells = within(table).getAllByRole('cell');
      const cellTexts = cells.map((c) => c.textContent);
      expect(cellTexts).not.toContain('Name');
    });

    it('treats all rows as data when hasHeader=false', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            hasHeader: false,
          })}
        />,
      );

      const table = screen.getByRole('table');
      // No <th> columnheader elements in body when hasHeader=false
      const thElements = within(table).queryAllByRole('columnheader');
      expect(thElements).toHaveLength(0);

      // All 3 rows are data rows → 3 rows × 3 cells = 9 cells
      const cells = within(table).getAllByRole('cell');
      expect(cells).toHaveLength(9);
      // First row data is now in cells, not header
      expect(cells[0]).toHaveTextContent('Name');
    });

    it('renders header row with <th> elements', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            hasHeader: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      const thElements = within(table).getAllByRole('columnheader');
      expect(thElements).toHaveLength(3);

      // Verify they are actual <th> elements
      for (const th of thElements) {
        expect(th.tagName.toLowerCase()).toBe('th');
      }
    });

    it('renders first column as header when hasHeaderColumn=true', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            hasHeader: true,
            hasHeaderColumn: true,
          })}
        />,
      );

      const table = screen.getByRole('table');
      // Body row first columns should be <th scope="row"> → rowheader role
      const rowHeaders = within(table).getAllByRole('rowheader');
      expect(rowHeaders.length).toBeGreaterThan(0);
      // First data row, first column = "Alice"
      expect(rowHeaders[0]).toHaveTextContent('Alice');
      expect(rowHeaders[0].tagName.toLowerCase()).toBe('th');
    });

    it('defaults hasHeaderColumn to false', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            hasHeader: true,
            // hasHeaderColumn not set → defaults to false
          })}
        />,
      );

      const table = screen.getByRole('table');
      // No <th scope="row"> elements should exist in body rows
      const rowHeaders = within(table).queryAllByRole('rowheader');
      expect(rowHeaders).toHaveLength(0);
    });
  });

  // =========================================================================
  // Access Control
  // =========================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toBeInTheDocument();
      expect(textarea).not.toBeDisabled();
    });

    it('renders as readonly with access="readonly"', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'edit',
            access: 'readonly',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toBeInTheDocument();
      // readonly access disables the textarea (disabled attribute)
      expect(textarea).toBeDisabled();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'edit',
            access: 'forbidden',
            accessDeniedMessage: 'access denied',
          })}
        />,
      );

      // Should render "access denied" text instead of the editor
      expect(screen.getByText('access denied')).toBeInTheDocument();
      // No textarea or table should be present
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
      expect(screen.queryByRole('table')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Validation
  // =========================================================================

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'edit',
            error: 'CSV data is invalid',
          })}
        />,
      );

      expect(screen.getByText('CSV data is invalid')).toBeInTheDocument();
      expect(screen.getByRole('alert')).toHaveTextContent('CSV data is invalid');
    });

    it('shows validation errors', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'edit',
            error: 'Field is required',
          })}
        />,
      );

      // Error message is visible
      expect(screen.getByText('Field is required')).toBeInTheDocument();

      // The textarea should have aria-invalid set to true
      const textarea = screen.getByRole('textbox');
      expect(textarea).toHaveAttribute('aria-invalid', 'true');
    });
  });

  // =========================================================================
  // Null/Empty Handling
  // =========================================================================

  describe('null/empty handling', () => {
    it('handles null value', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: null,
            mode: 'display',
            emptyValueMessage: 'no data',
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('handles empty string value', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: '',
            mode: 'display',
            emptyValueMessage: 'no data',
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Visibility
  // =========================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            isVisible: true,
            hasHeader: true,
          })}
        />,
      );

      expect(screen.getByRole('table')).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <DataCsvField
          {...createDefaultProps({
            value: SIMPLE_CSV,
            mode: 'display',
            isVisible: false,
            hasHeader: true,
          })}
        />,
      );

      // Component returns empty fragment when not visible
      expect(screen.queryByRole('table')).not.toBeInTheDocument();
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
      // Container should essentially be empty (just the root div from render)
      expect(container.textContent).toBe('');
    });
  });
});
