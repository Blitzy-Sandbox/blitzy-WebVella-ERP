/**
 * DateField — Vitest Component Tests
 *
 * Comprehensive test suite for the DateField component, replacing the
 * monolith's PcFieldDate ViewComponent. Covers date picker field:
 * - Display mode: Intl.DateTimeFormat localized output, emptyValueMessage
 * - Edit mode: <input type="date">, onChange with ISO string, clearing
 * - useCurrentTimeAsDefault: auto-populate today on first render
 * - Date formatting: YYYY-MM-DD acceptance/emission, timezone-aware display
 * - Access control: full / readonly / forbidden
 * - Validation errors and error prop styling
 * - Null/empty handling
 * - Visibility toggling
 *
 * Source: WebVella.Erp.Web/Components/PcFieldDate/PcFieldDate.cs
 *         WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom/vitest';
import React from 'react';
import DateField from '../../../src/components/fields/DateField';
import type { DateFieldProps } from '../../../src/components/fields/DateField';

/* ────────────────────────────────────────────────────────────────
   Helpers
   ──────────────────────────────────────────────────────────────── */

/**
 * Returns today's date as "YYYY-MM-DD" using local timezone.
 * Mirrors the component's internal getTodayISODate helper.
 */
function todayISODate(): string {
  const now = new Date();
  const year = now.getFullYear();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/**
 * Build a complete DateFieldProps object with sensible defaults.
 * Override any subset of props via the `overrides` parameter.
 */
function buildProps(overrides: Partial<DateFieldProps> = {}): DateFieldProps {
  return {
    name: 'birth_date',
    value: '2024-03-15',
    onChange: vi.fn(),
    mode: 'edit',
    access: 'full',
    isVisible: true,
    ...overrides,
  };
}

/* ────────────────────────────────────────────────────────────────
   Test Suite
   ──────────────────────────────────────────────────────────────── */

describe('DateField', () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  /* ═══════════════════════════ DISPLAY MODE ═══════════════════════════ */

  describe('display mode', () => {
    it('formats ISO date string using Intl.DateTimeFormat for localized display', () => {
      const isoValue = '2024-03-15';
      render(
        <DateField {...buildProps({ mode: 'display', value: isoValue })} />,
      );

      // Compute the expected formatted string using the same Intl formatter
      // The component parses as local midnight to avoid timezone offset issues
      const expected = new Intl.DateTimeFormat(undefined, {
        year: 'numeric',
        month: 'long',
        day: 'numeric',
      }).format(new Date(`${isoValue}T00:00:00`));

      const element = screen.getByText(expected);
      expect(element).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <DateField {...buildProps({ mode: 'display', value: null })} />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is empty string', () => {
      render(
        <DateField
          {...buildProps({ mode: 'display', value: '' as unknown as string | null })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('correctly formats various date strings', () => {
      // Render with a known date
      const { rerender } = render(
        <DateField {...buildProps({ mode: 'display', value: '2000-01-01' })} />,
      );

      const expected1 = new Intl.DateTimeFormat(undefined, {
        year: 'numeric',
        month: 'long',
        day: 'numeric',
      }).format(new Date('2000-01-01T00:00:00'));
      expect(screen.getByText(expected1)).toBeInTheDocument();

      // Re-render with a different date
      rerender(
        <DateField {...buildProps({ mode: 'display', value: '2023-12-25' })} />,
      );

      const expected2 = new Intl.DateTimeFormat(undefined, {
        year: 'numeric',
        month: 'long',
        day: 'numeric',
      }).format(new Date('2023-12-25T00:00:00'));
      expect(screen.getByText(expected2)).toBeInTheDocument();
    });

    it('renders custom emptyValueMessage when configured', () => {
      render(
        <DateField
          {...buildProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'Not set',
          })}
        />,
      );

      expect(screen.getByText('Not set')).toBeInTheDocument();
    });

    it('applies italic styling to empty value message', () => {
      render(
        <DateField {...buildProps({ mode: 'display', value: null })} />,
      );

      const span = screen.getByText('no data');
      expect(span).toHaveClass('italic');
      expect(span).toHaveClass('text-gray-400');
    });
  });

  /* ═══════════════════════════ EDIT MODE ═══════════════════════════ */

  describe('edit mode', () => {
    it('renders an input[type="date"] in edit mode', () => {
      const { container } = render(
        <DateField {...buildProps({ mode: 'edit', value: '2024-03-15' })} />,
      );

      const input = container.querySelector('input[type="date"]');
      expect(input).toBeInTheDocument();
    });

    it('displays current date value in the input', () => {
      const { container } = render(
        <DateField {...buildProps({ mode: 'edit', value: '2024-06-20' })} />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input.value).toBe('2024-06-20');
    });

    it('calls onChange with ISO date string when date is selected', () => {
      const handleChange = vi.fn();
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15',
            onChange: handleChange,
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      fireEvent.change(input, { target: { value: '2024-07-04' } });

      expect(handleChange).toHaveBeenCalledWith('2024-07-04');
    });

    it('handles clearing the date (empty input → null)', () => {
      const handleChange = vi.fn();
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15',
            onChange: handleChange,
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      fireEvent.change(input, { target: { value: '' } });

      expect(handleChange).toHaveBeenCalledWith(null);
    });

    it('renders with proper Tailwind CSS styling', () => {
      const { container } = render(
        <DateField {...buildProps({ mode: 'edit', value: '2024-03-15' })} />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveClass('block');
      expect(input).toHaveClass('w-full');
      expect(input).toHaveClass('rounded-md');
      expect(input).toHaveClass('border');
      expect(input).toHaveClass('border-gray-300');
      expect(input).toHaveClass('px-3');
      expect(input).toHaveClass('py-2');
      expect(input).toHaveClass('text-sm');
      expect(input).toHaveClass('shadow-sm');
    });

    it('sets the name attribute on the input', () => {
      const { container } = render(
        <DateField
          {...buildProps({ mode: 'edit', value: '2024-03-15', name: 'start_date' })}
        />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      expect(input).toHaveAttribute('name', 'start_date');
    });

    it('sets required attribute when required=true', () => {
      const { container } = render(
        <DateField
          {...buildProps({ mode: 'edit', value: '2024-03-15', required: true })}
        />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      expect(input).toBeRequired();
    });
  });

  /* ═══════════════════════════ useCurrentTimeAsDefault ═══════════════════════════ */

  describe('useCurrentTimeAsDefault', () => {
    it('does not default to today when useCurrentTimeAsDefault=false', () => {
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: null,
            useCurrentTimeAsDefault: false,
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      expect(input.value).toBe('');
    });

    it('defaults to today date when useCurrentTimeAsDefault=true and no value', () => {
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: null,
            useCurrentTimeAsDefault: true,
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      expect(input.value).toBe(todayISODate());
    });

    it('does not override existing value when useCurrentTimeAsDefault=true', () => {
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2020-01-15',
            useCurrentTimeAsDefault: true,
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      expect(input.value).toBe('2020-01-15');
    });

    it('shows today in display mode when useCurrentTimeAsDefault=true and no value', () => {
      render(
        <DateField
          {...buildProps({
            mode: 'display',
            value: null,
            useCurrentTimeAsDefault: true,
          })}
        />,
      );

      const today = todayISODate();
      const expected = new Intl.DateTimeFormat(undefined, {
        year: 'numeric',
        month: 'long',
        day: 'numeric',
      }).format(new Date(`${today}T00:00:00`));

      expect(screen.getByText(expected)).toBeInTheDocument();
    });
  });

  /* ═══════════════════════════ DATE FORMATTING ═══════════════════════════ */

  describe('date formatting', () => {
    it('accepts ISO date string "YYYY-MM-DD" format', () => {
      const { container } = render(
        <DateField
          {...buildProps({ mode: 'edit', value: '2024-12-31' })}
        />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      expect(input.value).toBe('2024-12-31');
    });

    it('emits ISO date string "YYYY-MM-DD" format on change', () => {
      const handleChange = vi.fn();
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2024-01-01',
            onChange: handleChange,
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      fireEvent.change(input, { target: { value: '2024-09-15' } });

      // Verify the emitted value is in YYYY-MM-DD format
      expect(handleChange).toHaveBeenCalledTimes(1);
      const emittedValue = handleChange.mock.calls[0][0] as string;
      expect(emittedValue).toBe('2024-09-15');
      expect(emittedValue).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    });

    it('handles timezone-aware date display', () => {
      // Parsing as local midnight (YYYY-MM-DDT00:00:00) avoids timezone shifts
      const isoValue = '2024-06-15';
      render(
        <DateField {...buildProps({ mode: 'display', value: isoValue })} />,
      );

      // The component uses `new Date('2024-06-15T00:00:00')` (local midnight)
      // so the date should always display as June 15 regardless of timezone
      const expected = new Intl.DateTimeFormat(undefined, {
        year: 'numeric',
        month: 'long',
        day: 'numeric',
      }).format(new Date(`${isoValue}T00:00:00`));

      expect(screen.getByText(expected)).toBeInTheDocument();
    });

    it('falls back to raw string for malformed dates', () => {
      render(
        <DateField
          {...buildProps({ mode: 'display', value: 'not-a-date' })}
        />,
      );

      // When validation fails, the component returns the raw string
      expect(screen.getByText('not-a-date')).toBeInTheDocument();
    });
  });

  /* ═══════════════════════════ ACCESS CONTROL ═══════════════════════════ */

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15',
            access: 'full',
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]');
      expect(input).toBeInTheDocument();
      expect(input).not.toBeDisabled();
    });

    it('renders as readonly with access="readonly"', () => {
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15',
            access: 'readonly',
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]');
      expect(input).toBeInTheDocument();
      expect(input).toBeDisabled();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15',
            access: 'forbidden',
          })}
        />,
      );

      // The component should render an alert element with the access-denied message
      const alert = screen.getByRole('alert');
      expect(alert).toBeInTheDocument();
      expect(alert).toHaveTextContent('access denied');

      // No date input should be present when access is forbidden
      expect(
        document.querySelector('input[type="date"]'),
      ).not.toBeInTheDocument();
    });

    it('shows custom accessDeniedMessage when configured', () => {
      render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15',
            access: 'forbidden',
            accessDeniedMessage: 'You do not have permission',
          })}
        />,
      );

      const alert = screen.getByRole('alert');
      expect(alert).toHaveTextContent('You do not have permission');
    });
  });

  /* ═══════════════════════════ VALIDATION ═══════════════════════════ */

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15',
            error: 'Date is required',
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]');
      expect(input).toHaveAttribute('aria-invalid', 'true');
      expect(input).toHaveClass('border-red-500');
    });

    it('shows validation errors', () => {
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '',
            error: 'Invalid date format',
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]');
      expect(input).toHaveAttribute('aria-invalid', 'true');
      expect(input).toHaveClass('border-red-500');
    });

    it('does not set aria-invalid when no error', () => {
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15',
            error: undefined,
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]');
      expect(input).not.toHaveAttribute('aria-invalid');
      expect(input).toHaveClass('border-gray-300');
    });
  });

  /* ═══════════════════════════ NULL / EMPTY HANDLING ═══════════════════════════ */

  describe('null/empty handling', () => {
    it('handles null value in edit mode', () => {
      const { container } = render(
        <DateField {...buildProps({ mode: 'edit', value: null })} />,
      );

      const input = container.querySelector('input[type="date"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input.value).toBe('');
    });

    it('handles undefined value', () => {
      render(
        <DateField
          {...buildProps({
            mode: 'display',
            value: undefined as unknown as string | null,
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('handles null value in display mode', () => {
      render(
        <DateField {...buildProps({ mode: 'display', value: null })} />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });
  });

  /* ═══════════════════════════ VISIBILITY ═══════════════════════════ */

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15',
            isVisible: true,
          })}
        />,
      );

      const input = container.querySelector('input[type="date"]');
      expect(input).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <DateField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15',
            isVisible: false,
          })}
        />,
      );

      expect(container.innerHTML).toBe('');
      expect(
        document.querySelector('input[type="date"]'),
      ).not.toBeInTheDocument();
    });
  });
});
