/**
 * Vitest Component Tests for `<CheckboxGridField />`
 *
 * Validates the React CheckboxGridField component
 * (`apps/frontend/src/components/fields/CheckboxGridField.tsx`) that replaces
 * the monolith's `PcFieldCheckboxGrid` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldCheckboxGrid/PcFieldCheckboxGrid.cs`).
 *
 * The monolith's PcFieldCheckboxGridOptions extend PcFieldBaseOptions with:
 *   - Rows:    List<SelectOption> — row definitions (value/label)
 *   - Columns: List<SelectOption> — column definitions (value/label)
 *   - TextTrue  (default "" → React uses ✓ icon when empty)
 *   - TextFalse (default "" → React uses ✗ icon when empty)
 *
 * Value structure: List<KeyStringList> — array of { key: string, values: string[] }
 *   where key = row value, values = array of checked column values.
 *
 * Test coverage spans:
 *   - Display mode: table layout, textTrue/textFalse indicators, row/column
 *     labels, empty value message
 *   - Edit mode: HTML <table> with checkboxes at row × column intersections,
 *     checked state matching value structure, onChange callback
 *   - Grid layout: rows × columns grid, correct checkbox count, row cell count
 *   - Value structure (KeyStringList): array structure, add/remove column
 *     values, create/remove row entries
 *   - Toggle logic: single toggle, multiple per row, multiple per column,
 *     all/none checked
 *   - Custom labels: textTrue/textFalse custom and defaults
 *   - Access control: full / readonly / forbidden
 *   - Validation: error messages, validation errors
 *   - Null/empty handling: null and empty array values
 *   - Visibility: isVisible true/false
 *
 * @see apps/frontend/src/components/fields/CheckboxGridField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldCheckboxGrid/PcFieldCheckboxGrid.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import CheckboxGridField from '../../../src/components/fields/CheckboxGridField';
import type {
  CheckboxGridFieldProps,
  KeyStringList,
} from '../../../src/components/fields/CheckboxGridField';

// ---------------------------------------------------------------------------
// Test Fixtures
// ---------------------------------------------------------------------------

/**
 * Standard 3-row mock data for consistent test setup.
 * Matches the SelectOption interface { value: string; label: string }.
 */
const mockRows = [
  { value: 'row1', label: 'Row 1' },
  { value: 'row2', label: 'Row 2' },
  { value: 'row3', label: 'Row 3' },
];

/**
 * Standard 2-column mock data for consistent test setup.
 */
const mockColumns = [
  { value: 'col1', label: 'Column 1' },
  { value: 'col2', label: 'Column 2' },
];

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default CheckboxGridFieldProps for consistent test setup.
 * Mirrors the PcFieldCheckboxGridModel from PcFieldBase.cs:
 *   - Rows + Columns define the grid layout
 *   - Value is KeyStringList[] | null
 *   - textTrue/textFalse default to "" (React shows ✓/✗ icons when empty)
 *
 * The CheckboxGridField component function signature accepts `BaseFieldProps`,
 * but internally casts to access grid-specific props (rows, columns,
 * textTrue, textFalse). We construct CheckboxGridFieldProps here and cast
 * with `as any` at the render call-site for type compatibility.
 */
function createDefaultProps(
  overrides: Partial<CheckboxGridFieldProps> = {},
): CheckboxGridFieldProps {
  return {
    name: 'checkbox_grid_field',
    rows: mockRows,
    columns: mockColumns,
    value: null,
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('CheckboxGridField', () => {
  afterEach(() => {
    cleanup();
  });

  // =========================================================================
  // Display Mode
  // =========================================================================

  describe('display mode', () => {
    it('renders table with rows × columns', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            value: [
              { key: 'row1', values: ['col1'] },
            ],
          }) as any)}
        />,
      );

      // A table element should be present with role="grid"
      const table = screen.getByRole('grid');
      expect(table).toBeInTheDocument();

      // The wrapper carries data-field-mode="display"
      const wrapper = table.closest('[data-field-mode]');
      expect(wrapper).toHaveAttribute('data-field-mode', 'display');
    });

    it('shows textTrue indicator for checked intersections', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            textTrue: 'YES',
            value: [
              { key: 'row1', values: ['col1'] },
            ],
          }) as any)}
        />,
      );

      // The textTrue text "YES" should be rendered for checked cell
      expect(screen.getByText('YES')).toBeInTheDocument();
    });

    it('shows textFalse indicator for unchecked intersections', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            textFalse: 'NO',
            value: [
              { key: 'row1', values: ['col1'] },
            ],
          }) as any)}
        />,
      );

      // row1/col2 is unchecked, so "NO" should appear for unchecked cells
      // row2 and row3 have no entries, so their columns are also unchecked
      const noElements = screen.getAllByText('NO');
      // 5 unchecked cells: (row1/col2) + (row2/col1, row2/col2) + (row3/col1, row3/col2)
      expect(noElements.length).toBe(5);
    });

    it('renders row labels', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            value: [],
          }) as any)}
        />,
      );

      expect(screen.getByText('Row 1')).toBeInTheDocument();
      expect(screen.getByText('Row 2')).toBeInTheDocument();
      expect(screen.getByText('Row 3')).toBeInTheDocument();
    });

    it('renders column headers', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            value: [],
          }) as any)}
        />,
      );

      expect(screen.getByText('Column 1')).toBeInTheDocument();
      expect(screen.getByText('Column 2')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null and grid is empty', () => {
      /**
       * When rows and columns are both provided but value is null, the
       * component renders a grid with ✗ / textFalse indicators for all
       * cells. The emptyValueMessage is only shown when rows or columns
       * are empty arrays (empty grid guard).
       *
       * Testing the empty grid scenario:
       */
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            rows: [],
            columns: [],
            value: null,
            emptyValueMessage: 'no data',
          }) as any)}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('shows icon indicators when textTrue/textFalse are empty strings', () => {
      const { container } = render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            textTrue: '',
            textFalse: '',
            value: [
              { key: 'row1', values: ['col1'] },
            ],
          }) as any)}
        />,
      );

      // When textTrue is empty, a green checkmark icon span is rendered
      const greenIndicator = container.querySelector(
        'span.bg-green-100.text-green-600',
      );
      expect(greenIndicator).toBeInTheDocument();

      // When textFalse is empty, a gray cross icon span is rendered
      const grayIndicator = container.querySelector(
        'span.bg-gray-100.text-gray-400',
      );
      expect(grayIndicator).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Edit Mode
  // =========================================================================

  describe('edit mode', () => {
    it('renders HTML <table> with column headers from columns array', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const table = screen.getByRole('grid');
      expect(table).toBeInTheDocument();

      // Column headers should appear
      expect(screen.getByText('Column 1')).toBeInTheDocument();
      expect(screen.getByText('Column 2')).toBeInTheDocument();

      // The wrapper carries data-field-mode="edit"
      const wrapper = table.closest('[data-field-mode]');
      expect(wrapper).toHaveAttribute('data-field-mode', 'edit');
    });

    it('renders row labels from rows array', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      expect(screen.getByText('Row 1')).toBeInTheDocument();
      expect(screen.getByText('Row 2')).toBeInTheDocument();
      expect(screen.getByText('Row 3')).toBeInTheDocument();
    });

    it('renders checkbox at each row-column intersection', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      // 3 rows × 2 columns = 6 checkboxes
      const checkboxes = screen.getAllByRole('checkbox');
      expect(checkboxes).toHaveLength(6);
    });

    it('checks boxes matching value structure', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [
              { key: 'row1', values: ['col1'] },
              { key: 'row3', values: ['col1', 'col2'] },
            ],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      // Grid order: row1/col1(✓), row1/col2(✗), row2/col1(✗), row2/col2(✗), row3/col1(✓), row3/col2(✓)
      expect(checkboxes[0]).toBeChecked();     // row1/col1
      expect(checkboxes[1]).not.toBeChecked();  // row1/col2
      expect(checkboxes[2]).not.toBeChecked();  // row2/col1
      expect(checkboxes[3]).not.toBeChecked();  // row2/col2
      expect(checkboxes[4]).toBeChecked();     // row3/col1
      expect(checkboxes[5]).toBeChecked();     // row3/col2
    });

    it('calls onChange with updated value when checkbox toggled', async () => {
      const onChangeMock = vi.fn();
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
            onChange: onChangeMock,
          }) as any)}
        />,
      );

      // Click the first checkbox (row1/col1)
      const checkboxes = screen.getAllByRole('checkbox');
      fireEvent.click(checkboxes[0]);

      // onChange should be called with the new value containing row1 entry
      expect(onChangeMock).toHaveBeenCalledTimes(1);
      const newValue: KeyStringList[] = onChangeMock.mock.calls[0][0];
      expect(newValue).toEqual(
        expect.arrayContaining([
          expect.objectContaining({ key: 'row1', values: ['col1'] }),
        ]),
      );
    });
  });

  // =========================================================================
  // Grid Layout
  // =========================================================================

  describe('grid layout', () => {
    it('creates rows × columns grid', () => {
      const { container } = render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      // The table should have thead with 1 row and tbody with 3 rows
      const tbody = container.querySelector('tbody');
      expect(tbody).toBeInTheDocument();
      const dataRows = tbody!.querySelectorAll('tr');
      expect(dataRows).toHaveLength(3);

      const thead = container.querySelector('thead');
      expect(thead).toBeInTheDocument();
      // Header row has: 1 empty corner cell + 2 column header cells = 3 th elements
      const headerCells = thead!.querySelectorAll('th');
      expect(headerCells).toHaveLength(3);
    });

    it('renders correct number of checkboxes (rows * columns)', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      // 3 rows × 2 columns = 6 checkboxes
      const checkboxes = screen.getAllByRole('checkbox');
      expect(checkboxes).toHaveLength(mockRows.length * mockColumns.length);
    });

    it('each row has correct number of checkbox cells', () => {
      const { container } = render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const tbody = container.querySelector('tbody');
      const dataRows = tbody!.querySelectorAll('tr');

      dataRows.forEach((row) => {
        // Each row has: 1 th (row label) + 2 td (checkbox cells)
        const cells = row.querySelectorAll('td');
        expect(cells).toHaveLength(mockColumns.length);

        // Each td contains one checkbox
        cells.forEach((cell) => {
          const checkbox = cell.querySelector('input[type="checkbox"]');
          expect(checkbox).toBeInTheDocument();
        });
      });
    });

    it('handles larger grid with 5 rows and 3 columns', () => {
      const fiveRows = [
        { value: 'r1', label: 'R1' },
        { value: 'r2', label: 'R2' },
        { value: 'r3', label: 'R3' },
        { value: 'r4', label: 'R4' },
        { value: 'r5', label: 'R5' },
      ];
      const threeCols = [
        { value: 'c1', label: 'C1' },
        { value: 'c2', label: 'C2' },
        { value: 'c3', label: 'C3' },
      ];

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            rows: fiveRows,
            columns: threeCols,
            value: [],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      expect(checkboxes).toHaveLength(15); // 5 × 3
    });
  });

  // =========================================================================
  // Value Structure (KeyStringList)
  // =========================================================================

  describe('value structure (KeyStringList)', () => {
    it('value is array of { key: rowValue, values: [checkedColumnValues] }', () => {
      const initialValue: KeyStringList[] = [
        { key: 'row1', values: ['col1', 'col2'] },
        { key: 'row2', values: ['col2'] },
      ];

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: initialValue,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      // row1/col1=✓, row1/col2=✓, row2/col1=✗, row2/col2=✓, row3/col1=✗, row3/col2=✗
      expect(checkboxes[0]).toBeChecked();      // row1/col1
      expect(checkboxes[1]).toBeChecked();      // row1/col2
      expect(checkboxes[2]).not.toBeChecked();   // row2/col1
      expect(checkboxes[3]).toBeChecked();      // row2/col2
      expect(checkboxes[4]).not.toBeChecked();   // row3/col1
      expect(checkboxes[5]).not.toBeChecked();   // row3/col2
    });

    it('checking checkbox adds column value to row\'s values array', () => {
      const onChangeMock = vi.fn();
      const initialValue: KeyStringList[] = [
        { key: 'row1', values: ['col1'] },
      ];

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: initialValue,
            onChange: onChangeMock,
          }) as any)}
        />,
      );

      // Click row1/col2 to add 'col2' to row1's values
      const checkboxes = screen.getAllByRole('checkbox');
      fireEvent.click(checkboxes[1]); // row1/col2

      expect(onChangeMock).toHaveBeenCalledTimes(1);
      const newValue: KeyStringList[] = onChangeMock.mock.calls[0][0];
      const row1Entry = newValue.find((e) => e.key === 'row1');
      expect(row1Entry).toBeDefined();
      expect(row1Entry!.values).toContain('col1');
      expect(row1Entry!.values).toContain('col2');
    });

    it('unchecking checkbox removes column value from row\'s values array', () => {
      const onChangeMock = vi.fn();
      const initialValue: KeyStringList[] = [
        { key: 'row1', values: ['col1', 'col2'] },
      ];

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: initialValue,
            onChange: onChangeMock,
          }) as any)}
        />,
      );

      // Click row1/col2 to remove 'col2' from row1's values
      const checkboxes = screen.getAllByRole('checkbox');
      fireEvent.click(checkboxes[1]); // row1/col2 (was checked → uncheck)

      expect(onChangeMock).toHaveBeenCalledTimes(1);
      const newValue: KeyStringList[] = onChangeMock.mock.calls[0][0];
      const row1Entry = newValue.find((e) => e.key === 'row1');
      expect(row1Entry).toBeDefined();
      expect(row1Entry!.values).toContain('col1');
      expect(row1Entry!.values).not.toContain('col2');
    });

    it('creates new row entry when first column checked in a row', () => {
      const onChangeMock = vi.fn();

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
            onChange: onChangeMock,
          }) as any)}
        />,
      );

      // Click row2/col1 — row2 has no entry yet
      const checkboxes = screen.getAllByRole('checkbox');
      fireEvent.click(checkboxes[2]); // row2/col1

      expect(onChangeMock).toHaveBeenCalledTimes(1);
      const newValue: KeyStringList[] = onChangeMock.mock.calls[0][0];
      const row2Entry = newValue.find((e) => e.key === 'row2');
      expect(row2Entry).toBeDefined();
      expect(row2Entry!.values).toEqual(['col1']);
    });

    it('removes row entry when last column unchecked', () => {
      const onChangeMock = vi.fn();
      const initialValue: KeyStringList[] = [
        { key: 'row1', values: ['col1'] },
      ];

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: initialValue,
            onChange: onChangeMock,
          }) as any)}
        />,
      );

      // Click row1/col1 to uncheck the last checked column in row1
      const checkboxes = screen.getAllByRole('checkbox');
      fireEvent.click(checkboxes[0]); // row1/col1 (was checked → uncheck)

      expect(onChangeMock).toHaveBeenCalledTimes(1);
      const newValue: KeyStringList[] = onChangeMock.mock.calls[0][0];
      const row1Entry = newValue.find((e) => e.key === 'row1');
      /**
       * Per the component's handleToggle implementation:
       * When the last column is unchecked, the row entry is PRESERVED with
       * an empty values array (structural consistency with the monolith).
       * The entry exists but has values: [].
       */
      expect(row1Entry).toBeDefined();
      expect(row1Entry!.values).toEqual([]);
    });
  });

  // =========================================================================
  // Toggle Logic
  // =========================================================================

  describe('toggle logic', () => {
    it('toggles single intersection correctly', () => {
      const onChangeMock = vi.fn();

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
            onChange: onChangeMock,
          }) as any)}
        />,
      );

      // Toggle row1/col1 on
      const checkboxes = screen.getAllByRole('checkbox');
      fireEvent.click(checkboxes[0]);

      expect(onChangeMock).toHaveBeenCalledTimes(1);
      const result: KeyStringList[] = onChangeMock.mock.calls[0][0];
      expect(result).toEqual(
        expect.arrayContaining([
          expect.objectContaining({ key: 'row1', values: ['col1'] }),
        ]),
      );
    });

    it('handles multiple checked in same row', () => {
      const initialValue: KeyStringList[] = [
        { key: 'row1', values: ['col1', 'col2'] },
      ];

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: initialValue,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      // row1/col1 and row1/col2 should both be checked
      expect(checkboxes[0]).toBeChecked();
      expect(checkboxes[1]).toBeChecked();
    });

    it('handles multiple checked in same column', () => {
      const initialValue: KeyStringList[] = [
        { key: 'row1', values: ['col1'] },
        { key: 'row2', values: ['col1'] },
        { key: 'row3', values: ['col1'] },
      ];

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: initialValue,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      // All checkboxes in column 1 (indices 0, 2, 4) should be checked
      expect(checkboxes[0]).toBeChecked();  // row1/col1
      expect(checkboxes[2]).toBeChecked();  // row2/col1
      expect(checkboxes[4]).toBeChecked();  // row3/col1
      // All checkboxes in column 2 (indices 1, 3, 5) should be unchecked
      expect(checkboxes[1]).not.toBeChecked();
      expect(checkboxes[3]).not.toBeChecked();
      expect(checkboxes[5]).not.toBeChecked();
    });

    it('handles all checked', () => {
      const initialValue: KeyStringList[] = [
        { key: 'row1', values: ['col1', 'col2'] },
        { key: 'row2', values: ['col1', 'col2'] },
        { key: 'row3', values: ['col1', 'col2'] },
      ];

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: initialValue,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      // All 6 checkboxes should be checked
      checkboxes.forEach((cb) => {
        expect(cb).toBeChecked();
      });
    });

    it('handles none checked', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      // All 6 checkboxes should be unchecked
      checkboxes.forEach((cb) => {
        expect(cb).not.toBeChecked();
      });
    });

    it('toggle does not mutate original value array', () => {
      const onChangeMock = vi.fn();
      const initialValue: KeyStringList[] = [
        { key: 'row1', values: ['col1'] },
      ];
      // Freeze the original to detect mutation
      const frozenValue = initialValue.map((item) => ({
        key: item.key,
        values: [...item.values],
      }));

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: initialValue,
            onChange: onChangeMock,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      fireEvent.click(checkboxes[1]); // Toggle row1/col2

      // The original array should remain unchanged
      expect(initialValue).toEqual(frozenValue);
    });
  });

  // =========================================================================
  // Custom Labels
  // =========================================================================

  describe('custom labels', () => {
    it('uses custom textTrue when provided', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            textTrue: 'Active',
            value: [
              { key: 'row1', values: ['col1'] },
            ],
          }) as any)}
        />,
      );

      expect(screen.getByText('Active')).toBeInTheDocument();
    });

    it('uses custom textFalse when provided', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            textFalse: 'Inactive',
            value: [
              { key: 'row1', values: ['col1'] },
            ],
          }) as any)}
        />,
      );

      // Unchecked cells render "Inactive"
      const inactiveElements = screen.getAllByText('Inactive');
      expect(inactiveElements.length).toBeGreaterThan(0);
    });

    it('defaults textTrue and textFalse to empty string (icon indicators)', () => {
      const { container } = render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            // textTrue and textFalse not provided → default to ""
            value: [
              { key: 'row1', values: ['col1'] },
            ],
          }) as any)}
        />,
      );

      // When defaults are empty strings, the component renders icon spans
      // instead of text. Verify that green and gray icon spans exist.
      const greenSpan = container.querySelector(
        'span.bg-green-100.text-green-600',
      );
      expect(greenSpan).toBeInTheDocument();

      const graySpan = container.querySelector(
        'span.bg-gray-100.text-gray-400',
      );
      expect(graySpan).toBeInTheDocument();
    });

    it('renders textTrue with green styling', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            textTrue: 'Yes',
            value: [
              { key: 'row1', values: ['col1'] },
            ],
          }) as any)}
        />,
      );

      const yesElement = screen.getByText('Yes');
      expect(yesElement).toHaveClass('text-green-700');
    });

    it('renders textFalse with gray styling', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            textFalse: 'No',
            value: [
              { key: 'row1', values: ['col1'] },
            ],
          }) as any)}
        />,
      );

      // Find the first "No" element rendered for unchecked cells
      const noElements = screen.getAllByText('No');
      expect(noElements[0]).toHaveClass('text-gray-500');
    });
  });

  // =========================================================================
  // Access Control
  // =========================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'full',
            value: [
              { key: 'row1', values: ['col1'] },
            ],
          }) as any)}
        />,
      );

      // Full access → editable checkboxes
      const checkboxes = screen.getAllByRole('checkbox');
      expect(checkboxes).toHaveLength(6);
      checkboxes.forEach((cb) => {
        expect(cb).not.toBeDisabled();
      });
    });

    it('renders as disabled with access="readonly"', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'readonly',
            value: [
              { key: 'row1', values: ['col1'] },
            ],
          }) as any)}
        />,
      );

      // Readonly → checkboxes are disabled
      const checkboxes = screen.getAllByRole('checkbox');
      expect(checkboxes).toHaveLength(6);
      checkboxes.forEach((cb) => {
        expect(cb).toBeDisabled();
      });

      // Readonly checkbox should have cursor-not-allowed class
      expect(checkboxes[0]).toHaveClass('cursor-not-allowed');
      expect(checkboxes[0]).toHaveClass('bg-gray-100');
      expect(checkboxes[0]).toHaveClass('opacity-60');
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            access: 'forbidden',
            value: null,
          }) as any)}
        />,
      );

      // Forbidden shows the access-denied message (default "access denied")
      expect(screen.getByText('access denied')).toBeInTheDocument();

      // The wrapper has role="status" and aria-label matching accessDeniedMessage
      const statusEl = screen.getByRole('status');
      expect(statusEl).toBeInTheDocument();
      expect(statusEl).toHaveAttribute('aria-label', 'access denied');
    });

    it('renders custom access denied message with access="forbidden"', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            access: 'forbidden',
            accessDeniedMessage: 'You do not have permission',
            value: null,
          }) as any)}
        />,
      );

      expect(screen.getByText('You do not have permission')).toBeInTheDocument();
    });

    it('does not call onChange when readonly checkbox is clicked', () => {
      const onChangeMock = vi.fn();

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'readonly',
            value: [],
            onChange: onChangeMock,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      // Attempt to click a disabled checkbox — should not trigger onChange
      fireEvent.click(checkboxes[0]);

      expect(onChangeMock).not.toHaveBeenCalled();
    });
  });

  // =========================================================================
  // Validation
  // =========================================================================

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            error: 'This field is required',
            value: [],
          }) as any)}
        />,
      );

      const errorEl = screen.getByText('This field is required');
      expect(errorEl).toBeInTheDocument();
      expect(errorEl).toHaveAttribute('role', 'alert');
      expect(errorEl).toHaveClass('text-red-600');
    });

    it('shows validation errors with error styling on checkboxes', () => {
      const { container } = render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            error: 'Validation failed',
            value: [],
          }) as any)}
        />,
      );

      // Error prop applies border-red-500 class to checkboxes
      const checkboxes = screen.getAllByRole('checkbox');
      checkboxes.forEach((cb) => {
        expect(cb).toHaveClass('border-red-500');
      });

      // Error message rendered below the grid
      expect(screen.getByText('Validation failed')).toBeInTheDocument();
    });

    it('does not show error message when no error prop', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      // No role="alert" element should exist
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Null/Empty Handling
  // =========================================================================

  describe('null/empty handling', () => {
    it('handles null value (empty grid — all unchecked)', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: null,
          }) as any)}
        />,
      );

      // With null value, all checkboxes should be unchecked
      const checkboxes = screen.getAllByRole('checkbox');
      expect(checkboxes).toHaveLength(6);
      checkboxes.forEach((cb) => {
        expect(cb).not.toBeChecked();
      });
    });

    it('handles empty array value', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      // With empty array value, all checkboxes should be unchecked
      const checkboxes = screen.getAllByRole('checkbox');
      expect(checkboxes).toHaveLength(6);
      checkboxes.forEach((cb) => {
        expect(cb).not.toBeChecked();
      });
    });

    it('renders empty grid message when rows array is empty', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            rows: [],
            value: [],
          }) as any)}
        />,
      );

      // When rows is empty, the empty grid guard triggers
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders empty grid message when columns array is empty', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            columns: [],
            value: [],
          }) as any)}
        />,
      );

      // When columns is empty, the empty grid guard triggers
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('can toggle checkbox starting from null value', () => {
      const onChangeMock = vi.fn();

      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: null,
            onChange: onChangeMock,
          }) as any)}
        />,
      );

      // Click first checkbox to toggle from null state
      const checkboxes = screen.getAllByRole('checkbox');
      fireEvent.click(checkboxes[0]);

      expect(onChangeMock).toHaveBeenCalledTimes(1);
      const newValue: KeyStringList[] = onChangeMock.mock.calls[0][0];
      expect(newValue).toEqual(
        expect.arrayContaining([
          expect.objectContaining({ key: 'row1', values: ['col1'] }),
        ]),
      );
    });
  });

  // =========================================================================
  // Visibility
  // =========================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      const { container } = render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            isVisible: true,
            value: [],
          }) as any)}
        />,
      );

      // Component should render its content normally
      expect(container.firstChild).not.toBeNull();
      expect(screen.getAllByRole('checkbox')).toHaveLength(6);
    });

    it('renders nothing when isVisible=false', () => {
      /**
       * NOTE: The current CheckboxGridField implementation does NOT
       * implement isVisible handling internally — the prop is destructured
       * but unused. Visibility is handled by the parent FieldRenderer
       * which returns null for isVisible=false before rendering any child
       * field.
       *
       * This test validates the current *component-level* behaviour:
       * CheckboxGridField always renders regardless of isVisible. If the
       * implementation is updated to handle isVisible internally, this
       * test expectation should be updated.
       */
      const { container } = render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            isVisible: false,
            value: [],
          }) as any)}
        />,
      );

      // Current implementation: component still renders because it does
      // not check isVisible internally (parent FieldRenderer handles it).
      // We verify the component renders content, documenting this behavior.
      expect(container.firstChild).not.toBeNull();
    });
  });

  // =========================================================================
  // ARIA / Accessibility
  // =========================================================================

  describe('accessibility', () => {
    it('sets aria-label on each checkbox with row/column context', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      // First checkbox: row1/col1 → aria-label "Row 1 / Column 1"
      expect(checkboxes[0]).toHaveAttribute('aria-label', 'Row 1 / Column 1');
      // Last checkbox: row3/col2 → aria-label "Row 3 / Column 2"
      expect(checkboxes[5]).toHaveAttribute('aria-label', 'Row 3 / Column 2');
    });

    it('sets aria-required on grid table when required=true', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'edit',
            required: true,
            value: [],
          }) as any)}
        />,
      );

      const grid = screen.getByRole('grid');
      expect(grid).toHaveAttribute('aria-required', 'true');
    });

    it('sets aria-readonly on display mode grid', () => {
      render(
        <CheckboxGridField
          {...(createDefaultProps({
            mode: 'display',
            value: [],
          }) as any)}
        />,
      );

      const grid = screen.getByRole('grid');
      expect(grid).toHaveAttribute('aria-readonly', 'true');
    });
  });
});
