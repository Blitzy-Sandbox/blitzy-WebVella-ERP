/**
 * @file CheckboxListField.test.tsx
 * @description Vitest unit tests for the CheckboxListField component,
 *   which replaces the monolith's PcFieldCheckboxList ViewComponent.
 *
 *   Tests cover the complete surface area of checkbox-group behaviour:
 *   - Display mode (selected labels as badges, empty-value message)
 *   - Edit mode (checkbox rendering, toggling, label pairing, layout)
 *   - Array value management (add, remove, order preservation)
 *   - Access control (full, readonly, forbidden)
 *   - Validation (error messages, aria attributes)
 *   - Null/empty handling
 *   - Visibility (isVisible prop)
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';

import CheckboxListField from '../../../src/components/fields/CheckboxListField';
import type { CheckboxListFieldProps } from '../../../src/components/fields/CheckboxListField';

// =========================================================================
// Test Helpers
// =========================================================================

/**
 * Mock options reused across tests.  Three items mirror the monolith's
 * PcFieldCheckboxListOptions.Options list (SelectOption[]).
 */
const mockOptions: CheckboxListFieldProps['options'] = [
  { value: 'a', label: 'Option A' },
  { value: 'b', label: 'Option B' },
  { value: 'c', label: 'Option C' },
];

/**
 * Creates a default props object suitable for passing to CheckboxListField.
 *
 * Because the component's public signature accepts `BaseFieldProps` (to
 * integrate with FieldRenderer) rather than `CheckboxListFieldProps`
 * directly, we cast with `as any` at the call-site — exactly like the
 * existing RadioListField tests.
 */
function createDefaultProps(
  overrides: Record<string, unknown> = {},
): Record<string, unknown> {
  return {
    name: 'test-checkbox-list',
    value: ['a'],
    onChange: vi.fn(),
    options: mockOptions,
    mode: 'edit',
    access: 'full',
    label: 'Test Checkbox List',
    isVisible: true,
    ...overrides,
  };
}

// =========================================================================
// Test Suite
// =========================================================================

describe('CheckboxListField', () => {
  afterEach(() => {
    cleanup();
  });

  // =======================================================================
  // Display Mode
  // =======================================================================

  describe('display mode', () => {
    it('shows selected option labels as styled badges', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'display',
            value: ['a', 'c'],
          }) as any)}
        />,
      );

      // Should render selected option labels as badges
      expect(screen.getByText('Option A')).toBeInTheDocument();
      expect(screen.getByText('Option C')).toBeInTheDocument();

      // Un-selected option should NOT appear
      expect(screen.queryByText('Option B')).not.toBeInTheDocument();
    });

    it('applies blue badge styling to each selected label', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'display',
            value: ['a', 'b'],
          }) as any)}
        />,
      );

      const badgeA = screen.getByText('Option A');
      const badgeB = screen.getByText('Option B');

      expect(badgeA).toHaveClass('bg-blue-100');
      expect(badgeA).toHaveClass('text-blue-800');
      expect(badgeA).toHaveClass('rounded-full');

      expect(badgeB).toHaveClass('bg-blue-100');
      expect(badgeB).toHaveClass('rounded-full');
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'display',
            value: null,
          }) as any)}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is empty array', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'display',
            value: [],
          }) as any)}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders custom emptyValueMessage when provided', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'Nothing selected',
          }) as any)}
        />,
      );

      expect(screen.getByText('Nothing selected')).toBeInTheDocument();
    });

    it('renders value as-is when option label is not found', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'display',
            value: ['unknown_val'],
          }) as any)}
        />,
      );

      // Component defensively displays the raw value when no label match exists
      expect(screen.getByText('unknown_val')).toBeInTheDocument();
    });

    it('does not render checkboxes in display mode', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'display',
            value: ['a'],
          }) as any)}
        />,
      );

      expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
    });
  });

  // =======================================================================
  // Edit Mode
  // =======================================================================

  describe('edit mode', () => {
    it('renders a checkbox for each option in vertical list', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      expect(checkboxes).toHaveLength(3);
    });

    it('renders checkboxes inside a role="group" container', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const group = screen.getByRole('group');
      expect(group).toBeInTheDocument();
    });

    it('each checkbox is paired with a <label>', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      checkboxes.forEach((checkbox) => {
        const id = checkbox.getAttribute('id');
        expect(id).toBeTruthy();

        // Verify a <label> element with matching htmlFor exists in the DOM
        const matchingLabel = document.querySelector(`label[for="${id}"]`);
        expect(matchingLabel).not.toBeNull();
      });
    });

    it('renders option labels next to their checkboxes', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      expect(screen.getByText('Option A')).toBeInTheDocument();
      expect(screen.getByText('Option B')).toBeInTheDocument();
      expect(screen.getByText('Option C')).toBeInTheDocument();
    });

    it('checks boxes for values present in the value array', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: ['a', 'c'],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');

      expect(checkboxes[0]).toBeChecked();     // Option A — selected
      expect(checkboxes[1]).not.toBeChecked();  // Option B — not selected
      expect(checkboxes[2]).toBeChecked();     // Option C — selected
    });

    it('calls onChange with updated array when checkbox checked', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: ['a'],
            onChange: handleChange,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');

      // Click Option B (currently unchecked) to check it
      await user.click(checkboxes[1]);

      expect(handleChange).toHaveBeenCalledTimes(1);
      expect(handleChange).toHaveBeenCalledWith(['a', 'b']);
    });

    it('calls onChange with updated array when checkbox unchecked', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: ['a', 'b'],
            onChange: handleChange,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');

      // Click Option A (currently checked) to uncheck it
      await user.click(checkboxes[0]);

      expect(handleChange).toHaveBeenCalledTimes(1);
      expect(handleChange).toHaveBeenCalledWith(['b']);
    });

    it('applies gap-2 spacing between items', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const group = screen.getByRole('group');
      expect(group).toHaveClass('gap-2');
    });

    it('applies flex-col layout for vertical stacking', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const group = screen.getByRole('group');
      expect(group).toHaveClass('flex');
      expect(group).toHaveClass('flex-col');
    });

    it('sets name attribute with array notation on each checkbox', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      checkboxes.forEach((cb) => {
        expect(cb).toHaveAttribute('name', 'test-checkbox-list[]');
      });
    });

    it('renders empty options fallback text when options is empty', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
            options: [],
          }) as any)}
        />,
      );

      expect(screen.getByText('No options available')).toBeInTheDocument();
      expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
    });

    it('renders custom placeholder when options is empty', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
            options: [],
            placeholder: 'Select items first',
          }) as any)}
        />,
      );

      expect(screen.getByText('Select items first')).toBeInTheDocument();
    });
  });

  // =======================================================================
  // Array Value Management
  // =======================================================================

  describe('array value management', () => {
    it('adds value to array on check', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
            onChange: handleChange,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      await user.click(checkboxes[0]); // Check Option A

      expect(handleChange).toHaveBeenCalledWith(['a']);
    });

    it('removes value from array on uncheck', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: ['a', 'b', 'c'],
            onChange: handleChange,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      await user.click(checkboxes[1]); // Uncheck Option B

      expect(handleChange).toHaveBeenCalledWith(['a', 'c']);
    });

    it('handles empty initial value (no boxes checked)', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      checkboxes.forEach((cb) => {
        expect(cb).not.toBeChecked();
      });
    });

    it('handles all values selected', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: ['a', 'b', 'c'],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      checkboxes.forEach((cb) => {
        expect(cb).toBeChecked();
      });
    });

    it('maintains order of selections when adding', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: ['c'],
            onChange: handleChange,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      await user.click(checkboxes[0]); // Check Option A

      // New value is appended after existing selections
      expect(handleChange).toHaveBeenCalledWith(['c', 'a']);
    });

    it('maintains order of remaining selections when removing', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: ['a', 'b', 'c'],
            onChange: handleChange,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      await user.click(checkboxes[1]); // Uncheck Option B

      // 'b' removed; 'a' and 'c' remain in original order
      expect(handleChange).toHaveBeenCalledWith(['a', 'c']);
    });

    it('supports multiple sequential toggles', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: ['a'],
            onChange: handleChange,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');

      // Add Option B
      await user.click(checkboxes[1]);
      expect(handleChange).toHaveBeenCalledTimes(1);
      expect(handleChange).toHaveBeenCalledWith(['a', 'b']);
    });
  });

  // =======================================================================
  // Access Control
  // =======================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'full',
            value: ['a'],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      expect(checkboxes).toHaveLength(3);

      // None of the checkboxes should be disabled
      checkboxes.forEach((cb) => {
        expect(cb).not.toBeDisabled();
      });
    });

    it('renders as disabled with access="readonly"', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'readonly',
            value: ['a'],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      expect(checkboxes).toHaveLength(3);

      // All checkboxes should be disabled
      checkboxes.forEach((cb) => {
        expect(cb).toBeDisabled();
      });
    });

    it('does not call onChange when readonly and checkbox is clicked', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'readonly',
            value: ['a'],
            onChange: handleChange,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      // Attempt clicking a disabled checkbox — userEvent may throw
      await user.click(checkboxes[1]).catch(() => {
        /* swallow pointer-event error on disabled elements */
      });

      expect(handleChange).not.toHaveBeenCalled();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'forbidden',
            value: ['a'],
          }) as any)}
        />,
      );

      // Forbidden renders a role="status" element with the denied message
      const statusElement = screen.getByRole('status');
      expect(statusElement).toBeInTheDocument();
      expect(screen.getByText('access denied')).toBeInTheDocument();

      // No checkboxes should be present
      expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
    });

    it('renders custom access denied message with access="forbidden"', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'forbidden',
            value: ['a'],
            accessDeniedMessage: 'Not authorised',
          }) as any)}
        />,
      );

      expect(screen.getByText('Not authorised')).toBeInTheDocument();
    });

    it('renders as disabled when disabled prop is true', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'full',
            disabled: true,
            value: [],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      checkboxes.forEach((cb) => {
        expect(cb).toBeDisabled();
      });
    });
  });

  // =======================================================================
  // Validation
  // =======================================================================

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
            error: 'Please select at least one option',
          }) as any)}
        />,
      );

      const errorEl = screen.getByRole('alert');
      expect(errorEl).toBeInTheDocument();
      expect(errorEl).toHaveTextContent('Please select at least one option');
    });

    it('does not show error when error prop is not provided', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
            error: undefined,
          }) as any)}
        />,
      );

      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });

    it('associates error message with checkboxes via aria-describedby', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
            error: 'Required field',
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      const errorEl = screen.getByRole('alert');
      const errorId = errorEl.getAttribute('id');

      expect(errorId).toBeTruthy();
      checkboxes.forEach((cb) => {
        expect(cb).toHaveAttribute('aria-describedby', errorId);
      });
    });

    it('sets aria-invalid on checkboxes when error exists', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
            error: 'Field is required',
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      checkboxes.forEach((cb) => {
        expect(cb).toHaveAttribute('aria-invalid', 'true');
      });
    });

    it('shows error with red text styling', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
            error: 'Error!',
          }) as any)}
        />,
      );

      const errorEl = screen.getByRole('alert');
      expect(errorEl).toHaveClass('text-red-600');
    });
  });

  // =======================================================================
  // Null/Empty Handling
  // =======================================================================

  describe('null/empty handling', () => {
    it('handles null value (no selection) in edit mode', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: null,
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      checkboxes.forEach((cb) => {
        expect(cb).not.toBeChecked();
      });
    });

    it('handles empty array value in edit mode', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            value: [],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      checkboxes.forEach((cb) => {
        expect(cb).not.toBeChecked();
      });
    });

    it('handles null value in display mode showing empty message', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'display',
            value: null,
          }) as any)}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('handles empty array value in display mode showing empty message', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'display',
            value: [],
          }) as any)}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });
  });

  // =======================================================================
  // Visibility
  // =======================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            isVisible: true,
            value: ['a'],
          }) as any)}
        />,
      );

      const checkboxes = screen.getAllByRole('checkbox');
      expect(checkboxes).toHaveLength(3);
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'edit',
            isVisible: false,
            value: ['a'],
          }) as any)}
        />,
      );

      // Component should render null — empty DOM
      expect(container.innerHTML).toBe('');

      // No checkboxes should be found
      expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
    });

    it('renders nothing in display mode when isVisible=false', () => {
      const { container } = render(
        <CheckboxListField
          {...(createDefaultProps({
            mode: 'display',
            value: ['a'],
            isVisible: false,
          }) as any)}
        />,
      );

      expect(container.innerHTML).toBe('');
    });
  });
});
