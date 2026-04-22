/**
 * Vitest Component Tests for `<CurrencyField />`
 *
 * Validates the React CurrencyField component
 * (`apps/frontend/src/components/fields/CurrencyField.tsx`) that replaces
 * the monolith's `PcFieldCurrency` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldCurrency/PcFieldCurrency.cs`).
 *
 * The monolith's PcFieldCurrencyOptions extend PcFieldBaseOptions with:
 *   - CurrencyCode (default "USD") — ISO 4217 code for symbol & formatting
 *   - DecimalDigits (default 2)    — precision for currency display
 *   - Min / Max / Step             — numeric constraints on the input
 *   - ShowCode (default false)     — prepend currency code text
 *
 * **Key difference from PercentField**: CurrencyField does NOT perform any
 * unit conversion.  The `value` is always the raw monetary amount (e.g.
 * 99.95 represents $99.95 when currencyCode is "USD").  Display formatting
 * uses `Intl.NumberFormat` with `style: 'currency'` for locale-aware
 * rendering.  Edit mode shows a `<input type="number">` with a
 * locale-derived currency-symbol prefix.
 *
 * Test coverage spans:
 *   - Display mode: Intl.NumberFormat currency formatting with USD/EUR/GBP,
 *     decimal digits precision, emptyValueMessage for null
 *   - Edit mode: number input with currency symbol prefix, onChange with
 *     number value, min/max/step attributes
 *   - Currency code formatting: symbol derivation via Intl.NumberFormat
 *     (USD=$, EUR=€, GBP=£), default "USD"
 *   - Decimal digits: 2 default, 0 digits (whole number), 3+ digits
 *   - Access control: full / readonly / forbidden
 *   - Validation: error message display, aria-invalid, error styling
 *   - Null/zero handling: null and zero values in both modes
 *   - Visibility: isVisible true/false
 *
 * @see apps/frontend/src/components/fields/CurrencyField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldCurrency/PcFieldCurrency.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import CurrencyField from '../../../src/components/fields/CurrencyField';
import type { CurrencyFieldProps } from '../../../src/components/fields/CurrencyField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default CurrencyFieldProps for consistent test setup.
 * Mirrors the PcFieldCurrencyOptions defaults from PcFieldCurrency.cs:
 *   - CurrencyCode = "USD"
 *   - DecimalDigits = 2
 *   - Min = null, Max = null, Step = null
 *   - Mode defaults to 'edit' in the component
 *   - Access defaults to 'full' in the component
 *
 * Individual tests override only the props they care about, keeping each
 * test focused and reducing boilerplate.
 */
const buildProps = (overrides: Partial<CurrencyFieldProps> = {}): CurrencyFieldProps => ({
  name: 'price',
  value: 1234.56,
  ...overrides,
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('CurrencyField', () => {
  afterEach(() => {
    cleanup();
  });

  // ========================================================================
  // Display Mode
  // ========================================================================

  describe('display mode', () => {
    it('formats value using Intl.NumberFormat with default currency "USD"', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 1234.56,
          })}
        />,
      );

      // Intl.NumberFormat('en-US', {style:'currency', currency:'USD'}).format(1234.56) → "$1,234.56"
      expect(screen.getByText('$1,234.56')).toBeInTheDocument();
    });

    it('formats value with custom currencyCode (e.g., "EUR")', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 1234.56,
            currencyCode: 'EUR',
          })}
        />,
      );

      // Intl.NumberFormat('en-US', {style:'currency', currency:'EUR'}).format(1234.56) → "€1,234.56"
      expect(screen.getByText('€1,234.56')).toBeInTheDocument();
    });

    it('respects decimalDigits for decimal precision', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 99.9,
            decimalDigits: 3,
          })}
        />,
      );

      // With 3 decimal digits: "$99.900"
      expect(screen.getByText('$99.900')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: null,
          })}
        />,
      );

      // Default emptyValueMessage is "no data"
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('displays currency symbol derived from currencyCode', () => {
      const { container } = render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 500,
          })}
        />,
      );

      // The formatted currency string should contain the "$" symbol
      const displaySpan = container.querySelector('.text-gray-900');
      expect(displaySpan).toBeInTheDocument();
      expect(displaySpan).toHaveTextContent('$');
      expect(displaySpan).toHaveTextContent('$500.00');
    });

    it('does not render a number input in display mode', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 1234.56,
          })}
        />,
      );

      expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
    });

    it('renders custom emptyValueMessage when value is null', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'Not set',
          })}
        />,
      );

      expect(screen.getByText('Not set')).toBeInTheDocument();
    });
  });

  // ========================================================================
  // Edit Mode
  // ========================================================================

  describe('edit mode', () => {
    it('renders a number input in edit mode', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toBeInTheDocument();
      expect(input).toHaveAttribute('type', 'number');
    });

    it('shows currency symbol prefix in input group', () => {
      const { container } = render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
          })}
        />,
      );

      // The currency symbol prefix is rendered as an aria-hidden span
      const symbolSpan = container.querySelector('[aria-hidden="true"]');
      expect(symbolSpan).toBeInTheDocument();
      expect(symbolSpan).toHaveTextContent('$');
    });

    it('displays current numeric value', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 1234.56,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      // CurrencyField does NOT convert values — the raw amount is shown directly
      expect(input).toHaveValue(1234.56);
    });

    it('calls onChange with number when user types', () => {
      const handleChange = vi.fn();

      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      fireEvent.change(input, { target: { value: '99.95' } });

      // onChange receives the raw parsed number — no unit conversion
      expect(handleChange).toHaveBeenCalledWith(99.95);
    });

    it('calls onChange with null when input is cleared', () => {
      const handleChange = vi.fn();

      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      fireEvent.change(input, { target: { value: '' } });

      // Empty input → onChange receives null
      expect(handleChange).toHaveBeenCalledWith(null);
    });

    it('applies min attribute', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
            min: 0,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('min', '0');
    });

    it('applies max attribute', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
            max: 10000,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('max', '10000');
    });

    it('applies step attribute', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
            step: 0.01,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('step', '0.01');
    });

    it('applies min, max, and step attributes together', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
            min: 0,
            max: 10000,
            step: 1,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('min', '0');
      expect(input).toHaveAttribute('max', '10000');
      expect(input).toHaveAttribute('step', '1');
    });

    it('does not set min/max/step attributes when they are null', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
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

    it('sets the name attribute on the input', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            name: 'total_amount',
            value: 100,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveAttribute('name', 'total_amount');
    });

    it('handles user typing via userEvent to emit correct value', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      await user.click(input);
      await user.type(input, '250');

      // After typing '250', the last onChange call should have the raw value 250
      const lastCall = handleChange.mock.calls[handleChange.mock.calls.length - 1];
      expect(lastCall[0]).toBe(250);
    });

    it('handles clearing input via userEvent', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
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
  // Currency Code Formatting
  // ========================================================================

  describe('currency code formatting', () => {
    it('formats USD values with "$" symbol', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 1234.56,
            currencyCode: 'USD',
          })}
        />,
      );

      expect(screen.getByText('$1,234.56')).toBeInTheDocument();
    });

    it('formats EUR values with "€" symbol', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 1234.56,
            currencyCode: 'EUR',
          })}
        />,
      );

      expect(screen.getByText('€1,234.56')).toBeInTheDocument();
    });

    it('formats GBP values with "£" symbol', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 1234.56,
            currencyCode: 'GBP',
          })}
        />,
      );

      expect(screen.getByText('£1,234.56')).toBeInTheDocument();
    });

    it('defaults to "USD" when currencyCode not provided', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 500,
          })}
        />,
      );

      // Default currencyCode is "USD" → "$500.00"
      expect(screen.getByText('$500.00')).toBeInTheDocument();
    });

    it('uses Intl.NumberFormat for locale-aware formatting', () => {
      const { container } = render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 1234567.89,
          })}
        />,
      );

      // en-US locale with USD should produce comma-separated thousands
      const displaySpan = container.querySelector('.text-gray-900');
      expect(displaySpan).toBeInTheDocument();
      expect(displaySpan).toHaveTextContent('$1,234,567.89');
    });

    it('shows USD "$" symbol prefix in edit mode', () => {
      const { container } = render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
            currencyCode: 'USD',
          })}
        />,
      );

      const symbolSpan = container.querySelector('[aria-hidden="true"]');
      expect(symbolSpan).toBeInTheDocument();
      expect(symbolSpan).toHaveTextContent('$');
    });

    it('shows EUR "€" symbol prefix in edit mode', () => {
      const { container } = render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
            currencyCode: 'EUR',
          })}
        />,
      );

      const symbolSpan = container.querySelector('[aria-hidden="true"]');
      expect(symbolSpan).toBeInTheDocument();
      expect(symbolSpan).toHaveTextContent('€');
    });

    it('shows GBP "£" symbol prefix in edit mode', () => {
      const { container } = render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
            currencyCode: 'GBP',
          })}
        />,
      );

      const symbolSpan = container.querySelector('[aria-hidden="true"]');
      expect(symbolSpan).toBeInTheDocument();
      expect(symbolSpan).toHaveTextContent('£');
    });
  });

  // ========================================================================
  // Decimal Digits
  // ========================================================================

  describe('decimal digits', () => {
    it('displays 2 decimal places by default', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 1234.5,
          })}
        />,
      );

      // Default decimalDigits is 2 → "$1,234.50" (padded)
      expect(screen.getByText('$1,234.50')).toBeInTheDocument();
    });

    it('displays custom decimal places (e.g., 0 digits → whole number)', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 1234.56,
            decimalDigits: 0,
          })}
        />,
      );

      // 0 decimal digits → "$1,235" (rounded to nearest integer)
      expect(screen.getByText('$1,235')).toBeInTheDocument();
    });

    it('displays 3+ decimal places when configured', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 99.1234,
            decimalDigits: 4,
          })}
        />,
      );

      // 4 decimal digits → "$99.1234"
      expect(screen.getByText('$99.1234')).toBeInTheDocument();
    });

    it('displays 1 decimal place when decimalDigits is 1', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 1234.56,
            decimalDigits: 1,
          })}
        />,
      );

      // 1 decimal digit, 1234.56 rounds to 1234.6 → "$1,234.6"
      expect(screen.getByText('$1,234.6')).toBeInTheDocument();
    });

    it('pads value to minimum decimal places (e.g., 100 with 2 digits → "$100.00")', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 100,
          })}
        />,
      );

      // Integer 100 with default 2 decimal digits → "$100.00"
      expect(screen.getByText('$100.00')).toBeInTheDocument();
    });
  });

  // ========================================================================
  // Access Control
  // ========================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <CurrencyField
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
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            access: 'readonly',
            value: 99.95,
          })}
        />,
      );

      // Readonly access renders in display mode (formatted text), not as a
      // disabled input. The component checks:
      //   if (mode === 'display' || access === 'readonly') → render display.
      expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
      expect(screen.getByText('$99.95')).toBeInTheDocument();
    });

    it('renders readonly with emptyValueMessage when value is null', () => {
      render(
        <CurrencyField
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
        <CurrencyField
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
        <CurrencyField
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
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
            error: 'Amount is required',
          })}
        />,
      );

      // Error text is rendered in a <p> element with role="alert"
      const errorMessage = screen.getByRole('alert');
      expect(errorMessage).toBeInTheDocument();
      expect(errorMessage).toHaveTextContent('Amount is required');
    });

    it('shows validation errors', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: -5,
            error: 'Amount must be positive',
          })}
        />,
      );

      expect(screen.getByRole('alert')).toHaveTextContent('Amount must be positive');
    });

    it('marks input as aria-invalid when error is present', () => {
      render(
        <CurrencyField
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

    it('applies error styling', () => {
      const { container } = render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
            error: 'Invalid amount',
          })}
        />,
      );

      // Error message paragraph
      const errorParagraph = container.querySelector('[role="alert"]');
      expect(errorParagraph).toBeInTheDocument();
      expect(errorParagraph).toHaveTextContent('Invalid amount');

      // Input should have error border styling (border-red-500)
      const input = screen.getByRole('spinbutton');
      expect(input.className).toContain('border-red-500');
    });

    it('does not show error message when error prop is not provided', () => {
      render(
        <CurrencyField
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
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).not.toHaveAttribute('aria-invalid');
    });

    it('renders description text alongside the input', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 100,
            description: 'Enter the total amount in USD',
          })}
        />,
      );

      expect(screen.getByText('Enter the total amount in USD')).toBeInTheDocument();
    });
  });

  // ========================================================================
  // Null / Empty Handling
  // ========================================================================

  describe('null/empty handling', () => {
    it('handles null value in edit mode (empty input)', () => {
      render(
        <CurrencyField
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

    it('handles zero value correctly', () => {
      // Edit mode: 0 → shows 0 in input
      const { unmount } = render(
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: 0,
          })}
        />,
      );

      expect(screen.getByRole('spinbutton')).toHaveValue(0);
      unmount();

      // Display mode: 0 → "$0.00" (not emptyValueMessage)
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 0,
          })}
        />,
      );

      expect(screen.getByText('$0.00')).toBeInTheDocument();
    });

    it('shows emptyValueMessage in display mode for null', () => {
      render(
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: null,
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('emits null onChange when clearing a previously filled value', () => {
      const handleChange = vi.fn();

      render(
        <CurrencyField
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

    it('handles undefined value in display mode — shows emptyValueMessage', () => {
      render(
        <CurrencyField
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
        <CurrencyField
          {...buildProps({
            mode: 'edit',
            value: undefined as unknown as null,
          })}
        />,
      );

      const input = screen.getByRole('spinbutton');
      expect(input).toHaveValue(null);
    });

    it('handles 0 input to emit 0 via onChange (not null)', () => {
      const handleChange = vi.fn();

      render(
        <CurrencyField
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

      // parseFloat('0') = 0 — should emit 0, not null
      expect(handleChange).toHaveBeenCalledWith(0);
    });
  });

  // ========================================================================
  // Visibility
  // ========================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <CurrencyField
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
        <CurrencyField
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
        <CurrencyField
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
        <CurrencyField
          {...buildProps({
            mode: 'display',
            value: 1234.56,
            isVisible: true,
          })}
        />,
      );

      expect(screen.getByText('$1,234.56')).toBeVisible();
    });
  });
});
