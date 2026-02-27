/**
 * Vitest Component Tests for `<PercentField />`
 *
 * Validates the React PercentField component
 * (`apps/frontend/src/components/fields/PercentField.tsx`) that replaces
 * the monolith's `PcFieldPercent` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldPercent/PcFieldPercent.cs`).
 *
 * The monolith's PcFieldPercentOptions extend PcFieldBaseOptions with:
 *   - DecimalDigits (default 2) — precision for percentage display
 *   - Min / Max / Step          — numeric constraints on the input
 *   - ShowIcon (default false)  — icon rendering toggle
 *
 * **Critical conversion logic**: values are stored as decimals (0.5 = 50 %)
 * and displayed as percentages (50 %). Edit mode shows percentage values in
 * the number input (×100), and emits decimals via onChange (÷100).
 *
 * Test coverage spans:
 *   - Display mode: decimal → percentage formatting, decimalDigits precision,
 *     "%" suffix, emptyValueMessage for null
 *   - Edit mode: number input rendering, decimal ↔ percentage conversion on
 *     input/output, min/max/step attributes, "%" suffix indicator
 *   - Decimal ↔ percentage conversion: 0.85↔85, 0↔0, 1.0↔100, negative,
 *     values > 1.0
 *   - Access control: full / readonly / forbidden
 *   - Validation: error message display, aria-invalid
 *   - Null/empty handling: null and undefined values in both modes
 *   - Visibility: isVisible true/false
 *
 * @see apps/frontend/src/components/fields/PercentField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldPercent/PcFieldPercent.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import PercentField from '../../../src/components/fields/PercentField';
import type { PercentFieldProps } from '../../../src/components/fields/PercentField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default PercentFieldProps for consistent test setup.
 * Mirrors the PcFieldPercentOptions defaults from PcFieldPercent.cs:
 *   - DecimalDigits = 2
 *   - Min = null, Max = null, Step = null
 *   - Mode defaults to 'edit' in the component
 *   - Access defaults to 'full' in the component
 *
 * Individual tests override only the props they care about, keeping each
 * test focused and reducing boilerplate.
 */
const buildProps = (overrides: Partial<PercentFieldProps> = {}): PercentFieldProps => ({
  name: 'completion_rate',
  value: 0.5,
  ...overrides,
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('PercentField', () => {
  afterEach(() => {
    cleanup();
  });

  // ========================================================================
  // Display Mode
  // ========================================================================

  describe('display mode', () => {
    it('displays 0.85 as "85.00%" with default decimalDigits', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 0.85,
          })}
        />,
      );

      // Default decimalDigits is 2 → "85.00%"
      expect(screen.getByText('85.00%')).toBeInTheDocument();
    });

    it('displays 0.5 as "50.00%" with default decimalDigits', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 0.5,
          })}
        />,
      );

      expect(screen.getByText('50.00%')).toBeInTheDocument();
    });

    it('displays 1.0 as "100.00%" with default decimalDigits', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 1.0,
          })}
        />,
      );

      expect(screen.getByText('100.00%')).toBeInTheDocument();
    });

    it('displays 0 as "0.00%" with default decimalDigits', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 0,
          })}
        />,
      );

      expect(screen.getByText('0.00%')).toBeInTheDocument();
    });

    it('respects decimalDigits precision (e.g., 0.856 with 1 digit → "85.6%")', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 0.856,
            decimalDigits: 1,
          })}
        />,
      );

      expect(screen.getByText('85.6%')).toBeInTheDocument();
    });

    it('respects decimalDigits=0 (e.g., 0.856 → "86%")', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 0.856,
            decimalDigits: 0,
          })}
        />,
      );

      // (0.856 * 100).toFixed(0) = "85.6".toFixed(0) = "86" (rounds)
      expect(screen.getByText('86%')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: null,
          })}
        />,
      );

      // Default emptyValueMessage is "no data"
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders custom emptyValueMessage when value is null', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'N/A',
          })}
        />,
      );

      expect(screen.getByText('N/A')).toBeInTheDocument();
    });

    it('includes "%" suffix in the formatted display string', () => {
      const { container } = render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 0.75,
          })}
        />,
      );

      // The formatted display string should contain the "%" character
      const displaySpan = container.querySelector('.text-gray-900');
      expect(displaySpan).toBeInTheDocument();
      expect(displaySpan).toHaveTextContent('%');
      expect(displaySpan).toHaveTextContent('75.00%');
    });

    it('does not render a number input in display mode', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 0.5,
          })}
        />,
      );

      expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    });
  });

  // ========================================================================
  // Edit Mode
  // ========================================================================

  describe('edit mode', () => {
    it('renders a number input in edit mode', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toBeInTheDocument();
      expect(input).toHaveAttribute('type', 'number');
    });

    it('converts stored decimal to display percentage (0.5 → shows 50 in input)', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      // Stored decimal 0.5 → display percentage 50 in the input
      expect(input).toHaveValue(50);
    });

    it('calls onChange with decimal value (user enters 50 → onChange(0.5))', () => {
      const handleChange = vi.fn();

      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      fireEvent.change(input, { target: { value: '50' } });

      // User types 50 (percentage) → onChange receives 0.5 (decimal)
      expect(handleChange).toHaveBeenCalledWith(0.5);
    });

    it('calls onChange with null when input is cleared', () => {
      const handleChange = vi.fn();

      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      fireEvent.change(input, { target: { value: '' } });

      // Empty input → onChange receives null
      expect(handleChange).toHaveBeenCalledWith(null);
    });

    it('applies min attribute to the input', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            min: 0,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('min', '0');
    });

    it('applies max attribute to the input', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            max: 100,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('max', '100');
    });

    it('applies step attribute to the input', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            step: 0.01,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('step', '0.01');
    });

    it('applies min, max, and step attributes together', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            min: 0,
            max: 100,
            step: 1,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('min', '0');
      expect(input).toHaveAttribute('max', '100');
      expect(input).toHaveAttribute('step', '1');
    });

    it('does not set min/max/step attributes when they are null', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            min: null,
            max: null,
            step: null,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).not.toHaveAttribute('min');
      expect(input).not.toHaveAttribute('max');
      expect(input).not.toHaveAttribute('step');
    });

    it('shows "%" suffix indicator next to the input', () => {
      const { container } = render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
          })}
        />,
      );

      // The "%" suffix is rendered as an aria-hidden span
      const wrapper = within(container.firstElementChild as HTMLElement);
      const suffixSpan = container.querySelector('[aria-hidden="true"]');
      expect(suffixSpan).toBeInTheDocument();
      expect(suffixSpan).toHaveTextContent('%');

      // Also verify the wrapper contains both the input and suffix
      expect(wrapper.getByRole('spinbutton')).toBeInTheDocument();
    });

    it('sets the name attribute on the input', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            name: 'progress',
            value: 0.5,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('name', 'progress');
    });

    it('handles user typing via userEvent to emit correct decimal', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      await user.click(input);
      await user.type(input, '75');

      // After typing '75', the last onChange call should emit 0.75
      const lastCall = handleChange.mock.calls[handleChange.mock.calls.length - 1];
      expect(lastCall[0]).toBe(0.75);
    });

    it('handles clearing input via userEvent', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      await user.clear(input);

      // Clearing the input should emit null
      expect(handleChange).toHaveBeenCalledWith(null);
    });
  });

  // ========================================================================
  // Decimal ↔ Percentage Conversion
  // ========================================================================

  describe('decimal ↔ percentage conversion', () => {
    it('converts 0.85 to display 85 in edit input', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.85,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveValue(85);
    });

    it('converts user input 85 to emit 0.85 via onChange', () => {
      const handleChange = vi.fn();

      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      fireEvent.change(input, { target: { value: '85' } });

      expect(handleChange).toHaveBeenCalledWith(0.85);
    });

    it('handles 0 correctly — stores 0, displays "0" in edit and "0.00%" in display', () => {
      // Edit mode: decimal 0 → percentage 0 in input
      const { unmount } = render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0,
          })}
        />,
      );

      expect(screen.getByRole('spinbutton')).toHaveValue(0);
      unmount();

      // Display mode: decimal 0 → "0.00%"
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 0,
          })}
        />,
      );

      expect(screen.getByText('0.00%')).toBeInTheDocument();
    });

    it('handles 0 input to emit 0 via onChange (not null)', () => {
      const handleChange = vi.fn();

      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      fireEvent.change(screen.getByRole('spinbutton'), {
        target: { value: '0' },
      });

      // parseFloat('0') = 0, 0/100 = 0 — should emit 0, not null
      expect(handleChange).toHaveBeenCalledWith(0);
    });

    it('handles 1.0 correctly — stores 1.0, displays 100 in edit and "100.00%" in display', () => {
      // Edit mode: decimal 1.0 → percentage 100 in input
      const { unmount } = render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 1.0,
          })}
        />,
      );

      expect(screen.getByRole('spinbutton')).toHaveValue(100);
      unmount();

      // Display mode: decimal 1.0 → "100.00%"
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 1.0,
          })}
        />,
      );

      expect(screen.getByText('100.00%')).toBeInTheDocument();
    });

    it('handles negative values correctly (-0.25 → -25 in edit, "-25.00%" in display)', () => {
      // Edit mode: decimal -0.25 → percentage -25 in input
      const { unmount } = render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: -0.25,
          })}
        />,
      );

      expect(screen.getByRole('spinbutton')).toHaveValue(-25);
      unmount();

      // Display mode: decimal -0.25 → "-25.00%"
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: -0.25,
          })}
        />,
      );

      expect(screen.getByText('-25.00%')).toBeInTheDocument();
    });

    it('handles negative input to emit correct negative decimal', () => {
      const handleChange = vi.fn();

      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      fireEvent.change(screen.getByRole('spinbutton'), {
        target: { value: '-25' },
      });

      expect(handleChange).toHaveBeenCalledWith(-0.25);
    });

    it('handles values > 1.0 (e.g., 1.5 → 150 in edit, "150.00%" in display)', () => {
      // Edit mode: decimal 1.5 → percentage 150 in input
      const { unmount } = render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 1.5,
          })}
        />,
      );

      expect(screen.getByRole('spinbutton')).toHaveValue(150);
      unmount();

      // Display mode: decimal 1.5 → "150.00%"
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 1.5,
          })}
        />,
      );

      expect(screen.getByText('150.00%')).toBeInTheDocument();
    });

    it('handles very small decimals correctly (0.001 → 0.1 in edit)', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.001,
          })}
        />,
      );

      expect(screen.getByRole('spinbutton')).toHaveValue(0.1);
    });

    it('handles IEEE-754 edge case: 0.1 → displays 10 (not 10.000000000000002)', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.1,
          })}
        />,
      );

      // The decimalToInputString function rounds to precision+4 digits to
      // avoid IEEE-754 artifacts. 0.1 * 100 = 10.000000000000002 in JS,
      // but rounding should produce clean "10".
      expect(screen.getByRole('spinbutton')).toHaveValue(10);
    });
  });

  // ========================================================================
  // Access Control
  // ========================================================================

  describe('access control', () => {
    it('renders normally with access="full" — editable number input', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            access: 'full',
            value: 0.5,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toBeInTheDocument();
      expect(input).not.toBeDisabled();
      expect(input).toHaveValue(50);
    });

    it('renders as readonly with access="readonly" — display format, no input', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            access: 'readonly',
            value: 0.85,
          })}
        />,
      );

      // Readonly access renders in display mode (formatted text), not as a disabled input.
      // The component checks: if (mode === 'display' || access === 'readonly') → render display.
      expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
      expect(screen.getByText('85.00%')).toBeInTheDocument();
    });

    it('renders readonly with emptyValueMessage when value is null', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            access: 'readonly',
            value: null,
          })}
        />,
      );

      expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <PercentField
          {...buildProps({
            access: 'forbidden',
            value: 0.5,
          })}
        />,
      );

      // Default accessDeniedMessage from PcFieldBaseModel is "access denied"
      expect(screen.getByText('access denied')).toBeInTheDocument();
      expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    });

    it('renders custom access denied message with access="forbidden"', () => {
      render(
        <PercentField
          {...buildProps({
            access: 'forbidden',
            value: 0.5,
            accessDeniedMessage: 'You do not have permission',
          })}
        />,
      );

      expect(screen.getByText('You do not have permission')).toBeInTheDocument();
    });
  });

  // ========================================================================
  // Validation
  // ========================================================================

  describe('validation', () => {
    it('shows error message when error prop is provided', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            error: 'Value must be between 0% and 100%',
          })}
        />,
      );

      // Error text is rendered in a <p> element with role="alert"
      const errorMessage = screen.getByRole('alert');
      expect(errorMessage).toBeInTheDocument();
      expect(errorMessage).toHaveTextContent('Value must be between 0% and 100%');
    });

    it('marks input as aria-invalid when error is present', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            error: 'Required field',
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('aria-invalid', 'true');
    });

    it('does not show error message when error prop is not provided', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
          })}
        />,
      );

      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });

    it('does not set aria-invalid when no error', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).not.toHaveAttribute('aria-invalid');
    });

    it('shows validation error with correct styling', () => {
      const { container } = render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            error: 'Invalid percentage',
          })}
        />,
      );

      // Error message paragraph
      const errorParagraph = container.querySelector('[role="alert"]');
      expect(errorParagraph).toBeInTheDocument();
      expect(errorParagraph).toHaveTextContent('Invalid percentage');

      // Input should have error border styling
      const input = screen.getByRole('spinbutton');
      expect(input.className).toContain('border-red-500');
    });

    it('renders description text alongside the input', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            description: 'Enter the completion rate as a percentage',
          })}
        />,
      );

      expect(
        screen.getByText('Enter the completion rate as a percentage'),
      ).toBeInTheDocument();
    });
  });

  // ========================================================================
  // Null / Empty Handling
  // ========================================================================

  describe('null/empty handling', () => {
    it('handles null value in edit mode — empty input', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: null,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      // null value → empty string in the input → valueAsNumber is NaN
      // jest-dom treats NaN valueAsNumber as no value (null)
      expect(input).toHaveValue(null);
    });

    it('handles null value in display mode — shows emptyValueMessage', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: null,
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('handles undefined value in display mode — shows emptyValueMessage', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: undefined as unknown as null,
          })}
        />,
      );

      // The formattedValue check: (value === null || value === undefined) → ''
      // Then the render check: (value !== null && value !== undefined) ? display : emptyValueMessage
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('handles undefined value in edit mode — empty input', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: undefined as unknown as null,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveValue(null);
    });

    it('emits null onChange when clearing a previously filled value', () => {
      const handleChange = vi.fn();

      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      fireEvent.change(input, { target: { value: '' } });

      expect(handleChange).toHaveBeenCalledWith(null);
    });
  });

  // ========================================================================
  // Visibility
  // ========================================================================

  describe('visibility', () => {
    it('renders content when isVisible=true (default)', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            isVisible: true,
          })}
        />,
      );

      expect(screen.getByRole('spinbutton')).toBeVisible();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <PercentField
          {...buildProps({
            mode: 'edit',
            value: 0.5,
            isVisible: false,
          })}
        />,
      );

      // Component returns <React.Fragment /> when invisible, rendering no DOM
      expect(container).toBeEmptyDOMElement();
      expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    });

    it('renders nothing in display mode when isVisible=false', () => {
      const { container } = render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 0.85,
            isVisible: false,
          })}
        />,
      );

      expect(container).toBeEmptyDOMElement();
    });

    it('renders display content when isVisible=true', () => {
      render(
        <PercentField
          {...buildProps({
            mode: 'display',
            value: 0.85,
            isVisible: true,
          })}
        />,
      );

      expect(screen.getByText('85.00%')).toBeVisible();
    });
  });
});
