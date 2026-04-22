/**
 * MultiSelectField Component Unit Tests
 *
 * Comprehensive Vitest test suite for the MultiSelectField React component
 * that replaces the monolith's PcFieldMultiSelect ViewComponent.
 *
 * Tests cover: display mode (tags/badges, empty-value), edit mode (dropdown,
 * checkboxes, tag chips, remove buttons, search/filter, onChange), multi-value
 * selection (string array, Select All, Clear), dropdown behaviour (open/close/
 * click-outside), access control (full/readonly/forbidden), validation errors,
 * null/empty handling, and visibility toggling.
 *
 * @see WebVella.Erp.Web/Components/PcFieldMultiSelect/PcFieldMultiSelect.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';

import MultiSelectField from '../../../src/components/fields/MultiSelectField';
import type { MultiSelectFieldProps } from '../../../src/components/fields/MultiSelectField';

/* ================================================================== */
/* Test Data                                                           */
/* ================================================================== */

/** Standard three-option set used in most tests. */
const mockOptions: MultiSelectFieldProps['options'] = [
  { value: '1', label: 'Option 1' },
  { value: '2', label: 'Option 2' },
  { value: '3', label: 'Option 3' },
];

/**
 * Extended option set (> SEARCH_THRESHOLD of 5) to trigger the in-dropdown
 * search input visibility.
 */
const extendedOptions: MultiSelectFieldProps['options'] = [
  { value: 'a', label: 'Apple' },
  { value: 'b', label: 'Banana' },
  { value: 'c', label: 'Cherry' },
  { value: 'd', label: 'Date' },
  { value: 'e', label: 'Elderberry' },
  { value: 'f', label: 'Fig' },
];

/** Options with color properties for colour-badge rendering tests. */
const colorOptions: MultiSelectFieldProps['options'] = [
  { value: 'r', label: 'Red', color: '#ff0000' },
  { value: 'g', label: 'Green', color: '#00ff00' },
  { value: 'b', label: 'Blue' },
];

/* ================================================================== */
/* Helper: default props factory                                       */
/* ================================================================== */

/**
 * Creates a default props object with sensible defaults suitable for
 * rendering MultiSelectField in edit mode.
 */
function createDefaultProps(
  overrides: Partial<MultiSelectFieldProps> = {},
): MultiSelectFieldProps {
  return {
    name: 'test_multiselect',
    value: null,
    onChange: vi.fn(),
    options: mockOptions,
    mode: 'edit',
    access: 'full',
    label: 'Test Multi-Select',
    isVisible: true,
    ...overrides,
  };
}

/* ================================================================== */
/* Test Suite                                                          */
/* ================================================================== */

describe('MultiSelectField', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  /* ================================================================ */
  /* Display Mode                                                      */
  /* ================================================================ */

  describe('display mode', () => {
    it('shows selected values as comma-separated labels or tags', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({
            mode: 'display',
            value: ['1', '2'],
          })}
        />,
      );
      // In display mode, selected values render as styled tags/badges
      expect(screen.getByText('Option 1')).toBeInTheDocument();
      expect(screen.getByText('Option 2')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({
            mode: 'display',
            value: null,
          })}
        />,
      );
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is empty array', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({
            mode: 'display',
            value: [],
          })}
        />,
      );
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders custom emptyValueMessage', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'nothing here',
          })}
        />,
      );
      expect(screen.getByText('nothing here')).toBeInTheDocument();
    });

    it('shows tags/badges for selected items', () => {
      const { container } = render(
        <MultiSelectField
          {...createDefaultProps({
            mode: 'display',
            value: ['1', '3'],
          })}
        />,
      );
      // Tags container wraps all badge spans
      const tags = container.querySelectorAll('span.inline-flex');
      expect(tags.length).toBeGreaterThanOrEqual(2);
      expect(screen.getByText('Option 1')).toBeInTheDocument();
      expect(screen.getByText('Option 3')).toBeInTheDocument();
    });

    it('renders color badges for options with color property', () => {
      const { container } = render(
        <MultiSelectField
          {...createDefaultProps({
            mode: 'display',
            value: ['r', 'g'],
            options: colorOptions,
          })}
        />,
      );
      const colorBadges = container.querySelectorAll(
        'span.rounded-full[aria-hidden="true"]',
      );
      expect(colorBadges.length).toBe(2);
    });
  });

  /* ================================================================ */
  /* Edit Mode                                                         */
  /* ================================================================ */

  describe('edit mode', () => {
    it('renders a multi-select dropdown component', () => {
      render(<MultiSelectField {...createDefaultProps()} />);
      const combobox = screen.getByRole('combobox');
      expect(combobox).toBeInTheDocument();
      expect(combobox).toHaveAttribute('aria-haspopup', 'listbox');
    });

    it('shows checkboxes for each option when dropdown is open', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: ['1'] })}
        />,
      );
      // Open the dropdown
      await user.click(screen.getByRole('combobox'));
      // Each option rendered as role="option"
      const options = screen.getAllByRole('option');
      expect(options).toHaveLength(3);
    });

    it('displays selected items as tags/chips in the trigger area', () => {
      const { container } = render(
        <MultiSelectField
          {...createDefaultProps({ value: ['1', '2'] })}
        />,
      );
      // Selected items render as tag elements within the trigger
      expect(screen.getByText('Option 1')).toBeInTheDocument();
      expect(screen.getByText('Option 2')).toBeInTheDocument();
      // Tags have the characteristic bg-blue-100 styling
      const tags = container.querySelectorAll('.bg-blue-100');
      expect(tags.length).toBeGreaterThanOrEqual(2);
    });

    it('shows remove button (×) on each tag', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({ value: ['1', '2'] })}
        />,
      );
      expect(
        screen.getByLabelText('Remove Option 1'),
      ).toBeInTheDocument();
      expect(
        screen.getByLabelText('Remove Option 2'),
      ).toBeInTheDocument();
    });

    it('includes search/filter input when options exceed threshold', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({
            options: extendedOptions,
            value: null,
          })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const searchInput = screen.getByRole('searchbox');
      expect(searchInput).toBeInTheDocument();
      expect(searchInput).toHaveAttribute('aria-label', 'Search options');
    });

    it('does not show search input when options are at or below threshold', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ options: mockOptions, value: null })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
    });

    it('calls onChange with updated string[] on selection', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: [], onChange })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const options = screen.getAllByRole('option');
      await user.click(options[0]); // Select "Option 1"
      expect(onChange).toHaveBeenCalledWith(['1']);
    });

    it('calls onChange with updated string[] on deselection', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: ['1', '2'], onChange })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      // Click on Option 1 (already selected) to deselect
      const options = screen.getAllByRole('option');
      await user.click(options[0]);
      expect(onChange).toHaveBeenCalledWith(['2']);
    });

    it('shows placeholder text when no values selected', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({
            value: null,
            placeholder: 'Pick items',
          })}
        />,
      );
      expect(screen.getByText('Pick items')).toBeInTheDocument();
    });

    it('shows default placeholder when no custom placeholder is given and no values selected', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({ value: null, placeholder: '' })}
        />,
      );
      // Default placeholder contains "Select…" (with Unicode ellipsis)
      expect(screen.getByText(/Select/)).toBeInTheDocument();
    });

    it('marks selected options with aria-selected=true', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: ['2'] })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const options = screen.getAllByRole('option');
      expect(options[1]).toHaveAttribute('aria-selected', 'true');
      expect(options[0]).toHaveAttribute('aria-selected', 'false');
      expect(options[2]).toHaveAttribute('aria-selected', 'false');
    });
  });

  /* ================================================================ */
  /* Multi-value Selection                                             */
  /* ================================================================ */

  describe('multi-value selection', () => {
    it('supports selecting multiple values (string array)', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: ['1'], onChange })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const options = screen.getAllByRole('option');
      await user.click(options[1]); // Select Option 2
      expect(onChange).toHaveBeenCalledWith(['1', '2']);
    });

    it('adds value to array on checkbox check', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: ['1'], onChange })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const options = screen.getAllByRole('option');
      await user.click(options[2]); // Select Option 3
      expect(onChange).toHaveBeenCalledWith(['1', '3']);
    });

    it('removes value from array on checkbox uncheck', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: ['1', '2'], onChange })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const options = screen.getAllByRole('option');
      await user.click(options[0]); // Deselect Option 1
      expect(onChange).toHaveBeenCalledWith(['2']);
    });

    it('removes value when tag × is clicked', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: ['1', '2'], onChange })}
        />,
      );
      const removeBtn = screen.getByLabelText('Remove Option 1');
      await user.click(removeBtn);
      expect(onChange).toHaveBeenCalledWith(['2']);
    });

    it('handles "Select All" action if provided', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: [], onChange })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const selectAllBtn = screen.getByLabelText('Select all options');
      await user.click(selectAllBtn);
      expect(onChange).toHaveBeenCalledWith(['1', '2', '3']);
    });

    it('handles "Clear" action', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: ['1', '2', '3'], onChange })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const clearBtn = screen.getByLabelText('Clear all selections');
      await user.click(clearBtn);
      expect(onChange).toHaveBeenCalledWith([]);
    });

    it('Select All merges with existing selections outside filtered set', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({
            options: extendedOptions,
            value: ['a'],
            onChange,
          })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      // Type to filter — only options matching "ber" should remain
      const searchInput = screen.getByRole('searchbox');
      await user.type(searchInput, 'ber');
      // Click Select All — should merge filtered matches with existing selections
      const selectAllBtn = screen.getByLabelText('Select all options');
      await user.click(selectAllBtn);
      const calledWith = onChange.mock.calls[0][0] as string[];
      expect(calledWith).toContain('a'); // existing selection preserved
      expect(calledWith).toContain('e'); // Elderberry matched "ber"
    });

    it('disables Select All when all filtered options are already selected', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: ['1', '2', '3'] })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const selectAllBtn = screen.getByLabelText('Select all options');
      expect(selectAllBtn).toBeDisabled();
    });

    it('disables Clear when no values are selected', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: [] })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const clearBtn = screen.getByLabelText('Clear all selections');
      expect(clearBtn).toBeDisabled();
    });
  });

  /* ================================================================ */
  /* Search / Filter                                                   */
  /* ================================================================ */

  describe('search/filter', () => {
    it('filters options by search input text', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({
            options: extendedOptions,
            value: null,
          })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const searchInput = screen.getByRole('searchbox');
      await user.type(searchInput, 'Cher');
      // Only Cherry should remain
      const options = screen.getAllByRole('option');
      expect(options).toHaveLength(1);
      expect(screen.getByText('Cherry')).toBeInTheDocument();
    });

    it('shows all options when search is empty', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({
            options: extendedOptions,
            value: null,
          })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const options = screen.getAllByRole('option');
      expect(options).toHaveLength(extendedOptions.length);
    });

    it('shows "no results" when no options match search', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({
            options: extendedOptions,
            value: null,
          })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const searchInput = screen.getByRole('searchbox');
      await user.type(searchInput, 'ZZZZZ');
      expect(screen.queryAllByRole('option')).toHaveLength(0);
      expect(screen.getByText('No matching options')).toBeInTheDocument();
    });

    it('search is case-insensitive', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({
            options: extendedOptions,
            value: null,
          })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const searchInput = screen.getByRole('searchbox');
      await user.type(searchInput, 'apple');
      const options = screen.getAllByRole('option');
      expect(options).toHaveLength(1);
      expect(screen.getByText('Apple')).toBeInTheDocument();
    });
  });

  /* ================================================================ */
  /* Dropdown Behaviour                                                */
  /* ================================================================ */

  describe('dropdown behavior', () => {
    it('opens dropdown on click', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: null })}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toHaveAttribute('aria-expanded', 'false');
      await user.click(combobox);
      expect(combobox).toHaveAttribute('aria-expanded', 'true');
      expect(screen.getByRole('listbox')).toBeInTheDocument();
    });

    it('closes dropdown on click outside', async () => {
      const user = userEvent.setup();
      const { container } = render(
        <div>
          <span data-testid="outside-element">Outside</span>
          <MultiSelectField
            {...createDefaultProps({ value: null })}
          />
        </div>,
      );
      const combobox = screen.getByRole('combobox');
      await user.click(combobox);
      expect(combobox).toHaveAttribute('aria-expanded', 'true');
      // Simulate click outside by using fireEvent.mouseDown on parent
      fireEvent.mouseDown(screen.getByTestId('outside-element'));
      expect(combobox).toHaveAttribute('aria-expanded', 'false');
    });

    it('keeps dropdown open while selecting options', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: [] })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      expect(screen.getByRole('combobox')).toHaveAttribute(
        'aria-expanded',
        'true',
      );
      const options = screen.getAllByRole('option');
      await user.click(options[0]);
      // Dropdown should remain open after selecting an option
      expect(screen.getByRole('listbox')).toBeInTheDocument();
    });

    it('sets aria-controls to listbox id when open', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: null })}
        />,
      );
      const combobox = screen.getByRole('combobox');
      // Before open, no aria-controls
      expect(combobox).not.toHaveAttribute('aria-controls');
      await user.click(combobox);
      // After open, aria-controls references the listbox
      const listbox = screen.getByRole('listbox');
      expect(combobox).toHaveAttribute('aria-controls', listbox.id);
    });

    it('listbox has aria-multiselectable=true', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ value: null })}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const listbox = screen.getByRole('listbox');
      expect(listbox).toHaveAttribute('aria-multiselectable', 'true');
    });
  });

  /* ================================================================ */
  /* Access Control                                                    */
  /* ================================================================ */

  describe('access control', () => {
    it('renders normally with access="full"', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ access: 'full', value: null })}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).not.toHaveAttribute('aria-disabled');
      // Should be clickable — opens dropdown
      await user.click(combobox);
      expect(combobox).toHaveAttribute('aria-expanded', 'true');
    });

    it('renders as readonly/disabled with access="readonly"', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ access: 'readonly', value: ['1'] })}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toHaveAttribute('aria-disabled', 'true');
      // Should not open dropdown when clicked
      await user.click(combobox);
      expect(combobox).toHaveAttribute('aria-expanded', 'false');
    });

    it('does not show remove buttons on tags in readonly mode', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({ access: 'readonly', value: ['1', '2'] })}
        />,
      );
      expect(
        screen.queryByLabelText('Remove Option 1'),
      ).not.toBeInTheDocument();
      expect(
        screen.queryByLabelText('Remove Option 2'),
      ).not.toBeInTheDocument();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({ access: 'forbidden', value: ['1'] })}
        />,
      );
      expect(screen.getByText('access denied')).toBeInTheDocument();
      expect(screen.getByRole('alert')).toBeInTheDocument();
      expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    });

    it('renders custom accessDeniedMessage with access="forbidden"', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({
            access: 'forbidden',
            value: null,
            accessDeniedMessage: 'no permissions',
          })}
        />,
      );
      expect(screen.getByText('no permissions')).toBeInTheDocument();
    });

    it('renders as disabled with disabled=true prop', async () => {
      const user = userEvent.setup();
      render(
        <MultiSelectField
          {...createDefaultProps({ disabled: true, value: ['1'] })}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toHaveAttribute('aria-disabled', 'true');
      await user.click(combobox);
      expect(combobox).toHaveAttribute('aria-expanded', 'false');
    });
  });

  /* ================================================================ */
  /* Validation                                                        */
  /* ================================================================ */

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({ value: null, error: 'Required field' })}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toHaveAttribute('aria-invalid', 'true');
    });

    it('applies error border styling when error prop is set', () => {
      const { container } = render(
        <MultiSelectField
          {...createDefaultProps({ value: null, error: 'Something wrong' })}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox.className).toContain('border-red');
    });

    it('shows aria-invalid=false when no error', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({ value: null })}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toHaveAttribute('aria-invalid', 'false');
    });

    it('sets aria-required when required prop is true', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({ value: null, required: true })}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toHaveAttribute('aria-required', 'true');
    });
  });

  /* ================================================================ */
  /* Null / Empty Handling                                             */
  /* ================================================================ */

  describe('null/empty handling', () => {
    it('handles null value (empty selection) — shows placeholder', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({
            value: null,
            placeholder: 'Choose options',
          })}
        />,
      );
      expect(screen.getByText('Choose options')).toBeInTheDocument();
    });

    it('handles empty array value — shows placeholder', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({
            value: [],
            placeholder: 'Choose options',
          })}
        />,
      );
      expect(screen.getByText('Choose options')).toBeInTheDocument();
    });

    it('does not render remove buttons when value is null', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({ value: null })}
        />,
      );
      const removeButtons = screen.queryAllByLabelText(/Remove /);
      expect(removeButtons).toHaveLength(0);
    });

    it('does not render remove buttons when value is empty array', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({ value: [] })}
        />,
      );
      const removeButtons = screen.queryAllByLabelText(/Remove /);
      expect(removeButtons).toHaveLength(0);
    });
  });

  /* ================================================================ */
  /* Visibility                                                        */
  /* ================================================================ */

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <MultiSelectField
          {...createDefaultProps({ isVisible: true, value: null })}
        />,
      );
      expect(screen.getByRole('combobox')).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <MultiSelectField
          {...createDefaultProps({ isVisible: false, value: null })}
        />,
      );
      expect(container.innerHTML).toBe('');
      expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    });

    it('renders nothing when isVisible=false even with selected values', () => {
      const { container } = render(
        <MultiSelectField
          {...createDefaultProps({ isVisible: false, value: ['1', '2'] })}
        />,
      );
      expect(container.innerHTML).toBe('');
    });
  });
});
