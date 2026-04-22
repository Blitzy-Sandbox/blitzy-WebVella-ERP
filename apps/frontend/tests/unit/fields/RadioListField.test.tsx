/**
 * Vitest Component Tests for `<RadioListField />`
 *
 * Validates the React RadioListField component
 * (`apps/frontend/src/components/fields/RadioListField.tsx`) that replaces
 * the monolith's `PcFieldRadioList` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldRadioList/PcFieldRadioList.cs`).
 *
 * The monolith's PcFieldRadioListOptions extends PcFieldBaseOptions with:
 *   - Options: List<SelectOption> — list of radio options
 * The monolith's PcFieldRadioListModel extends PcFieldBaseModel with:
 *   - Value: single string (single selection)
 *   - Access: WvFieldAccess (Full / ReadOnly / Forbidden)
 *   - EmptyValueMessage: string (default "no data")
 *   - AccessDeniedMessage: string (default "access denied")
 *
 * Test coverage spans:
 *   - Display mode: selected option label, empty value message for null,
 *     empty value message when no matching option found
 *   - Edit mode: radio rendering, shared name attribute, label pairing,
 *     checked state, onChange callback, mutual exclusivity
 *   - Single selection: deselect previous on new selection, null initial,
 *     pre-selected initial value
 *   - Access control: full / readonly / forbidden
 *   - Validation: error messages, validation error display
 *   - Null/empty handling: null value, unmatched value
 *   - Visibility: isVisible true/false
 *
 * @see apps/frontend/src/components/fields/RadioListField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldRadioList/PcFieldRadioList.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import RadioListField from '../../../src/components/fields/RadioListField';
import type { RadioListFieldProps } from '../../../src/components/fields/RadioListField';

// ---------------------------------------------------------------------------
// Test Data
// ---------------------------------------------------------------------------

/** Standard three-option set used in most tests */
const mockOptions = [
  { value: 'a', label: 'Option A' },
  { value: 'b', label: 'Option B' },
  { value: 'c', label: 'Option C' },
];

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default RadioListFieldProps for consistent test setup.
 * Mirrors the PcFieldRadioListModel defaults from PcFieldRadioList.cs.
 */
function createDefaultProps(
  overrides: Partial<RadioListFieldProps> = {},
): RadioListFieldProps {
  return {
    name: 'test_radio_list',
    value: null,
    onChange: vi.fn(),
    options: mockOptions,
    ...overrides,
  };
}

// ===========================================================================
// Tests
// ===========================================================================

describe('RadioListField', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  // =========================================================================
  // Display Mode
  // =========================================================================

  describe('display mode', () => {
    it('shows selected option label text', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'display',
            value: 'b',
          }) as any)}
        />,
      );

      // Display mode renders a <span> with the matched option label
      expect(screen.getByText('Option B')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'display',
            value: null,
          }) as any)}
        />,
      );

      // Default emptyValueMessage is "no data"
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when no matching option found', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'display',
            value: 'non_existent_value',
          }) as any)}
        />,
      );

      // When value doesn't match any option, emptyValueMessage is shown
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders custom emptyValueMessage when provided', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'Nothing selected',
          }) as any)}
        />,
      );

      expect(screen.getByText('Nothing selected')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Edit Mode
  // =========================================================================

  describe('edit mode', () => {
    it('renders radio button for each option in vertical list', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
          }) as any)}
        />,
      );

      // Should render 3 radio buttons
      const radios = screen.getAllByRole('radio');
      expect(radios).toHaveLength(3);

      // The radio group container should exist
      const radioGroup = screen.getByRole('radiogroup');
      expect(radioGroup).toBeInTheDocument();
    });

    it('all radios share same name attribute', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            name: 'shared_radio_name',
          }) as any)}
        />,
      );

      const radios = screen.getAllByRole('radio');
      radios.forEach((radio) => {
        expect(radio).toHaveAttribute('name', 'shared_radio_name');
      });
    });

    it('each radio paired with <label>', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
          }) as any)}
        />,
      );

      // Each option should have its label text visible
      expect(screen.getByText('Option A')).toBeInTheDocument();
      expect(screen.getByText('Option B')).toBeInTheDocument();
      expect(screen.getByText('Option C')).toBeInTheDocument();

      // Each radio should be associated with a label (via htmlFor/id)
      const radios = screen.getAllByRole('radio');
      radios.forEach((radio) => {
        const radioId = radio.getAttribute('id');
        expect(radioId).toBeTruthy();
        // The label element wraps the radio, so the radio is inside a <label>
        const labelElement = radio.closest('label');
        expect(labelElement).toBeInTheDocument();
        expect(labelElement).toHaveAttribute('for', radioId);
      });
    });

    it('selects radio matching current value', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            value: 'b',
          }) as any)}
        />,
      );

      const radios = screen.getAllByRole('radio');

      // Radio for 'a' should not be checked
      expect(radios[0]).not.toBeChecked();
      // Radio for 'b' should be checked
      expect(radios[1]).toBeChecked();
      // Radio for 'c' should not be checked
      expect(radios[2]).not.toBeChecked();
    });

    it('calls onChange with new value when radio selected', async () => {
      const onChange = vi.fn();
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            value: 'a',
            onChange,
          }) as any)}
        />,
      );

      const radios = screen.getAllByRole('radio');

      // Click on the third radio ('c')
      fireEvent.click(radios[2]);

      expect(onChange).toHaveBeenCalledTimes(1);
      expect(onChange).toHaveBeenCalledWith('c');
    });

    it('only one radio can be selected at a time', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            value: 'a',
          }) as any)}
        />,
      );

      const radios = screen.getAllByRole('radio');

      // Only 'a' should be checked initially
      const checkedRadios = radios.filter(
        (radio) => (radio as HTMLInputElement).checked,
      );
      expect(checkedRadios).toHaveLength(1);
      expect(checkedRadios[0]).toHaveAttribute('value', 'a');
    });
  });

  // =========================================================================
  // Single Selection (mutual exclusivity)
  // =========================================================================

  describe('single selection', () => {
    it('selects new radio and deselects previous', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();

      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            value: 'a',
            onChange,
          }) as any)}
        />,
      );

      const radios = screen.getAllByRole('radio');

      // Verify initial state — 'a' is selected
      expect(radios[0]).toBeChecked();
      expect(radios[1]).not.toBeChecked();
      expect(radios[2]).not.toBeChecked();

      // User clicks radio 'c' — onChange is called with 'c'
      await user.click(radios[2]);

      expect(onChange).toHaveBeenCalledWith('c');
    });

    it('handles no initial selection (null value)', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            value: null,
          }) as any)}
        />,
      );

      const radios = screen.getAllByRole('radio');

      // None should be checked when value is null
      radios.forEach((radio) => {
        expect(radio).not.toBeChecked();
      });
    });

    it('handles initial selection', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            value: 'c',
          }) as any)}
        />,
      );

      const radios = screen.getAllByRole('radio');

      // Only 'c' should be checked
      expect(radios[0]).not.toBeChecked();
      expect(radios[1]).not.toBeChecked();
      expect(radios[2]).toBeChecked();
    });
  });

  // =========================================================================
  // Access Control
  // =========================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'full',
          }) as any)}
        />,
      );

      const radios = screen.getAllByRole('radio');
      expect(radios).toHaveLength(3);

      // All radios should be enabled when access is full
      radios.forEach((radio) => {
        expect(radio).not.toBeDisabled();
      });
    });

    it('renders as disabled with access="readonly"', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'readonly',
            value: 'b',
          }) as any)}
        />,
      );

      // In readonly mode, the component forces display mode.
      // It should render the selected option label text instead of radios.
      // The component sets effectiveMode = 'display' when access = 'readonly'.
      expect(screen.getByText('Option B')).toBeInTheDocument();

      // There should be no interactive radio buttons
      const radios = screen.queryAllByRole('radio');
      expect(radios).toHaveLength(0);
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'forbidden',
          }) as any)}
        />,
      );

      // Default accessDeniedMessage is "access denied"
      expect(screen.getByText('access denied')).toBeInTheDocument();

      // The access denied span has role="alert"
      expect(screen.getByRole('alert')).toHaveTextContent('access denied');

      // No radio buttons should be rendered
      const radios = screen.queryAllByRole('radio');
      expect(radios).toHaveLength(0);
    });

    it('renders custom access denied message when provided', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            access: 'forbidden',
            accessDeniedMessage: 'Not permitted',
          }) as any)}
        />,
      );

      expect(screen.getByText('Not permitted')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Validation
  // =========================================================================

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            error: 'This field is required',
          }) as any)}
        />,
      );

      // The error message should be displayed
      expect(screen.getByText('This field is required')).toBeInTheDocument();

      // The error message should have role="alert" for accessibility
      expect(screen.getByRole('alert')).toHaveTextContent(
        'This field is required',
      );
    });

    it('shows validation errors', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            error: 'Selection is invalid',
          }) as any)}
        />,
      );

      const errorElement = screen.getByText('Selection is invalid');
      expect(errorElement).toBeInTheDocument();
      expect(errorElement).toBeVisible();
    });

    it('does not show error message when error is not provided', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
          }) as any)}
        />,
      );

      // No alert role elements should exist when there is no error
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Null/Empty Handling
  // =========================================================================

  describe('null/empty handling', () => {
    it('handles null value (no selection)', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            value: null,
          }) as any)}
        />,
      );

      const radios = screen.getAllByRole('radio');

      // No radio should be checked with null value
      radios.forEach((radio) => {
        expect(radio).not.toBeChecked();
      });

      // All 3 options should still render
      expect(radios).toHaveLength(3);
    });

    it('handles value not matching any option', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            value: 'xyz_nonexistent',
          }) as any)}
        />,
      );

      const radios = screen.getAllByRole('radio');

      // No radio should be checked when value doesn't match any option
      radios.forEach((radio) => {
        expect(radio).not.toBeChecked();
      });

      // Options should still be rendered
      expect(radios).toHaveLength(3);
    });

    it('handles value not matching any option in display mode', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'display',
            value: 'xyz_nonexistent',
          }) as any)}
        />,
      );

      // Should fall back to emptyValueMessage
      expect(screen.getByText('no data')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Visibility
  // =========================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            isVisible: true,
          }) as any)}
        />,
      );

      // Component should be rendered with radio buttons
      const radios = screen.getAllByRole('radio');
      expect(radios).toHaveLength(3);
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            isVisible: false,
          }) as any)}
        />,
      );

      // Component should render null — nothing in the DOM
      expect(container.innerHTML).toBe('');

      // No radio buttons should be found
      expect(screen.queryAllByRole('radio')).toHaveLength(0);
    });

    it('renders nothing in display mode when isVisible=false', () => {
      const { container } = render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'display',
            value: 'a',
            isVisible: false,
          }) as any)}
        />,
      );

      expect(container.innerHTML).toBe('');
    });
  });

  // =========================================================================
  // Edge Cases
  // =========================================================================

  describe('edge cases', () => {
    it('renders empty options gracefully', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            options: [],
          }) as any)}
        />,
      );

      // No radio buttons should be rendered
      expect(screen.queryAllByRole('radio')).toHaveLength(0);
    });

    it('uses default name for radio group when name not provided', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            name: undefined as unknown as string,
          }) as any)}
        />,
      );

      // Radio group should still be accessible
      const radioGroup = screen.getByRole('radiogroup');
      expect(radioGroup).toBeInTheDocument();
    });

    it('renders fieldset with aria-required when required is true', () => {
      const { container } = render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            required: true,
          }) as any)}
        />,
      );

      const fieldset = container.querySelector('fieldset');
      expect(fieldset).toHaveAttribute('aria-required', 'true');
    });

    it('renders fieldset with aria-invalid when error is present', () => {
      const { container } = render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            error: 'Some error',
          }) as any)}
        />,
      );

      const fieldset = container.querySelector('fieldset');
      expect(fieldset).toHaveAttribute('aria-invalid', 'true');
    });

    it('renders description text when provided', () => {
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            description: 'Please select one option',
          }) as any)}
        />,
      );

      expect(screen.getByText('Please select one option')).toBeInTheDocument();
    });

    it('fires onChange via fireEvent.click for radio inputs', () => {
      const onChange = vi.fn();
      render(
        <RadioListField
          {...(createDefaultProps({
            mode: 'edit',
            value: null,
            onChange,
          }) as any)}
        />,
      );

      const radios = screen.getAllByRole('radio');

      // Click on the second radio ('b') — this triggers the change handler
      fireEvent.click(radios[1]);

      expect(onChange).toHaveBeenCalledWith('b');
    });
  });
});
