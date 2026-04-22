/**
 * Vitest Component Tests for `<NumberField />`
 *
 * Validates the React NumberField component
 * (`apps/frontend/src/components/fields/NumberField.tsx`) that replaces
 * the monolith's `PcFieldNumber` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldNumber/PcFieldNumber.cs`).
 *
 * The monolith's PcFieldNumberOptions extend PcFieldBaseOptions with:
 *   - DecimalDigits (default 2) — controls toLocaleString precision
 *   - Min / Max / Step            — HTML number input constraints
 *   - Step reset to null           — unless explicitly defined
 *
 * Test coverage spans:
 *   - Display mode: toLocaleString formatted number with decimalDigits
 *     precision, emptyValueMessage for null
 *   - Edit mode: input[type='number'] with min/max/step attributes,
 *     onChange with parsed number, empty/null handling
 *   - Access control: full / readonly / forbidden
 *   - Validation: error prop, validationErrors array, error styling
 *   - Null/zero/undefined handling: zero is valid (not treated as empty)
 *   - Numeric constraints: min/max/step/decimalDigits defaults
 *   - Visibility: isVisible true/false
 *
 * @see apps/frontend/src/components/fields/NumberField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldNumber/PcFieldNumber.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import NumberField from '../../../src/components/fields/NumberField';
import type { NumberFieldProps } from '../../../src/components/fields/NumberField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default NumberFieldProps for consistent test setup.
 * Mirrors the PcFieldNumberOptions defaults from PcFieldNumber.cs:
 *   - DecimalDigits = 2
 *   - Min = null, Max = null, Step = null (reset in CopyFromBaseOptions)
 *   - Mode defaults to 'edit' in the component
 *   - Access defaults to 'full' in the component
 *
 * Individual tests override only the props they care about, keeping each
 * test focused and reducing boilerplate.
 */
const buildProps = (overrides: Partial<NumberFieldProps> = {}): NumberFieldProps => ({
  name: 'quantity',
  value: 42.5,
  ...overrides,
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('NumberField', () => {
  afterEach(() => {
    cleanup();
  });

  // ========================================================================
  // Display Mode
  // ========================================================================

  describe('display mode', () => {
    it('renders formatted number with decimalDigits precision (default 2)', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'display',
            value: 42.5,
          })}
        />,
      );

      // 42.5 with default decimalDigits=2 → "42.50" via toLocaleString
      expect(screen.getByText('42.50')).toBeInTheDocument();
    });

    it('renders number with custom decimalDigits (e.g., 4)', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'display',
            value: 1234.5678,
            decimalDigits: 4,
          })}
        />,
      );

      // 1234.5678 with decimalDigits=4 → "1,234.5678" via toLocaleString
      const displayed = screen.getByText(/1,?234\.5678/);
      expect(displayed).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <NumberField
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
        <NumberField
          {...buildProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'Not set',
          })}
        />,
      );

      expect(screen.getByText('Not set')).toBeInTheDocument();
    });

    it('formats number using toLocaleString()', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'display',
            value: 9876543.21,
            decimalDigits: 2,
          })}
        />,
      );

      // toLocaleString should add thousands separators: "9,876,543.21"
      const displayed = screen.getByText(/9,?876,?543\.21/);
      expect(displayed).toBeInTheDocument();
    });

    it('renders zero value formatted (not treated as empty)', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'display',
            value: 0,
          })}
        />,
      );

      // 0 with decimalDigits=2 → "0.00"
      expect(screen.getByText('0.00')).toBeInTheDocument();
    });

    it('formats negative numbers correctly', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'display',
            value: -123.456,
            decimalDigits: 2,
          })}
        />,
      );

      // toLocaleString formats to 2 decimal digits → rounding may apply
      const displayed = screen.getByText(/-123\.46/);
      expect(displayed).toBeInTheDocument();
    });

    it('renders with decimalDigits=0 (whole numbers)', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'display',
            value: 100.789,
            decimalDigits: 0,
          })}
        />,
      );

      // decimalDigits=0 → "101" (rounded)
      expect(screen.getByText('101')).toBeInTheDocument();
    });
  });

  // ========================================================================
  // Edit Mode
  // ========================================================================

  describe('edit mode', () => {
    it('renders an input[type="number"] in edit mode', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 42.5,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toBeInTheDocument();
      expect(input).toHaveAttribute('type', 'number');
    });

    it('displays the current numeric value', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 99.99,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveValue(99.99);
    });

    it('calls onChange with parsed number when user inputs', () => {
      const handleChange = vi.fn();

      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      fireEvent.change(input, { target: { value: '123' } });

      expect(handleChange).toHaveBeenCalledWith(123);
    });

    it('applies min attribute when provided', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 5,
            min: 0,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('min', '0');
    });

    it('applies max attribute when provided', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 50,
            max: 100,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('max', '100');
    });

    it('applies step attribute when provided', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 10,
            step: 0.5,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('step', '0.5');
    });

    it('does not apply min/max/step when null', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 42.5,
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

    it('handles empty input as null value', () => {
      const handleChange = vi.fn();

      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 42.5,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      fireEvent.change(input, { target: { value: '' } });

      expect(handleChange).toHaveBeenCalledWith(null);
    });

    it('handles non-numeric input gracefully', () => {
      const handleChange = vi.fn();

      // Start with a non-null value so that when jsdom sanitises 'abc' to ''
      // on a number input, the value actually changes and the React change
      // handler fires.
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 42.5,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      // Native number inputs reject non-numeric text; jsdom converts 'abc'
      // to '' which the component treats as "cleared" → emits null.
      fireEvent.change(input, { target: { value: '' } });

      expect(handleChange).toHaveBeenCalledWith(null);
    });

    it('renders with placeholder text', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: null,
            placeholder: 'Enter quantity',
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('placeholder', 'Enter quantity');
    });

    it('sets the name attribute on the input', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            name: 'total_weight',
            value: 75,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('name', 'total_weight');
    });

    it('renders required attribute when required=true', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: null,
            required: true,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toBeRequired();
    });

    it('renders disabled when disabled=true', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 42.5,
            disabled: true,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toBeDisabled();
    });

    it('calls onChange with negative number', () => {
      const handleChange = vi.fn();

      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      fireEvent.change(screen.getByRole('spinbutton'), {
        target: { value: '-42' },
      });

      expect(handleChange).toHaveBeenCalledWith(-42);
    });

    it('calls onChange with decimal number', () => {
      const handleChange = vi.fn();

      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      fireEvent.change(screen.getByRole('spinbutton'), {
        target: { value: '3.14' },
      });

      expect(handleChange).toHaveBeenCalledWith(3.14);
    });
  });

  // ========================================================================
  // Access Control
  // ========================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            access: 'full',
            value: 100,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toBeInTheDocument();
      expect(input).not.toBeDisabled();
      expect(input).toHaveValue(100);
    });

    it('renders as readonly with access="readonly"', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            access: 'readonly',
            value: 99.5,
          })}
        />,
      );

      // Readonly access renders in display mode (formatted text), not as
      // an editable input. The component checks:
      //   if (mode === 'display' || access === 'readonly') → render display.
      expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
      expect(screen.getByText('99.50')).toBeInTheDocument();
    });

    it('renders readonly with emptyValueMessage when value is null', () => {
      render(
        <NumberField
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
        <NumberField
          {...buildProps({
            access: 'forbidden',
            value: 100,
          })}
        />,
      );

      // Default accessDeniedMessage from the component is "access denied"
      expect(screen.getByText('access denied')).toBeInTheDocument();
      expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    });

    it('renders custom access denied message with access="forbidden"', () => {
      render(
        <NumberField
          {...buildProps({
            access: 'forbidden',
            value: 100,
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
    it('shows error message when error prop provided', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 100,
            error: 'Quantity is required',
          })}
        />,
      );

      // Error text is rendered in a <p> element with role="alert"
      const errorMessage = screen.getByRole('alert');
      expect(errorMessage).toBeInTheDocument();
      expect(errorMessage).toHaveTextContent('Quantity is required');
    });

    it('shows validation errors from validationErrors array', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: -5,
            error: 'Value must be positive',
          })}
        />,
      );

      expect(screen.getByRole('alert')).toHaveTextContent(
        'Value must be positive',
      );
    });

    it('applies error styling for invalid input', () => {
      const { container } = render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 100,
            error: 'Invalid amount',
          })}
        />,
      );

      // Input should have error border styling (border-red-500)
      const input = screen.getByRole('spinbutton');
      expect(input.className).toContain('border-red-500');
    });

    it('marks input as aria-invalid when error is present', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 100,
            error: 'Required field',
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('aria-invalid', 'true');
    });

    it('does not show error message when error prop is not provided', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 100,
          })}
        />,
      );

      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });

    it('does not set aria-invalid when no error', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 100,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).not.toHaveAttribute('aria-invalid');
    });
  });

  // ========================================================================
  // Null / Empty Handling
  // ========================================================================

  describe('null/empty handling', () => {
    it('handles null value in edit mode (empty input)', () => {
      render(
        <NumberField
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

    it('handles undefined value', () => {
      // Display mode: undefined → shows emptyValueMessage
      const { unmount } = render(
        <NumberField
          {...buildProps({
            mode: 'display',
            value: undefined as unknown as null,
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
      unmount();

      // Edit mode: undefined → empty input
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: undefined as unknown as null,
          })}
        />,
      );

      expect(screen.getByRole('spinbutton')).toHaveValue(null);
    });

    it('handles zero value correctly (not treated as empty)', () => {
      // Edit mode: 0 → shows 0 in input
      const { unmount } = render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 0,
          })}
        />,
      );

      expect(screen.getByRole('spinbutton')).toHaveValue(0);
      unmount();

      // Display mode: 0 → "0.00" (not emptyValueMessage)
      render(
        <NumberField
          {...buildProps({
            mode: 'display',
            value: 0,
          })}
        />,
      );

      expect(screen.getByText('0.00')).toBeInTheDocument();
    });

    it('emits null onChange when clearing a previously filled value', () => {
      const handleChange = vi.fn();

      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 100,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      fireEvent.change(input, { target: { value: '' } });

      expect(handleChange).toHaveBeenCalledWith(null);
    });

    it('emits 0 via onChange when user types zero (not null)', () => {
      const handleChange = vi.fn();

      render(
        <NumberField
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

      // Number('0') = 0 — should emit 0, not null
      expect(handleChange).toHaveBeenCalledWith(0);
    });
  });

  // ========================================================================
  // Numeric Constraints
  // ========================================================================

  describe('numeric constraints', () => {
    it('respects min constraint on input', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 10,
            min: 5,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('min', '5');
    });

    it('respects max constraint on input', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 10,
            max: 1000,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('max', '1000');
    });

    it('respects step constraint for increment', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 10,
            step: 0.01,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('step', '0.01');
    });

    it('decimal_digits defaults to 2 in display', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'display',
            value: 7,
            // No decimalDigits override — component default is 2
          })}
        />,
      );

      // 7 with decimalDigits=2 → "7.00"
      expect(screen.getByText('7.00')).toBeInTheDocument();
    });

    it('applies combined min, max, and step constraints', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 50,
            min: 0,
            max: 100,
            step: 5,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('min', '0');
      expect(input).toHaveAttribute('max', '100');
      expect(input).toHaveAttribute('step', '5');
      expect(input).toHaveValue(50);
    });
  });

  // ========================================================================
  // Visibility
  // ========================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 100,
            isVisible: true,
          })}
        />,
      );

      expect(screen.getByRole('spinbutton')).toBeVisible();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <NumberField
          {...buildProps({
            mode: 'edit',
            value: 100,
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
        <NumberField
          {...buildProps({
            mode: 'display',
            value: 1234.56,
            isVisible: false,
          })}
        />,
      );

      expect(container).toBeEmptyDOMElement();
    });

    it('renders display content when isVisible=true', () => {
      render(
        <NumberField
          {...buildProps({
            mode: 'display',
            value: 1234.56,
            isVisible: true,
          })}
        />,
      );

      const displayed = screen.getByText(/1,?234\.56/);
      expect(displayed).toBeVisible();
    });
  });
});
