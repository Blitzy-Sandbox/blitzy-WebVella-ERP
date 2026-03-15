/**
 * DateTimeField — Vitest Component Tests
 *
 * Comprehensive test suite for the DateTimeField component, replacing the
 * monolith's PcFieldDateTime ViewComponent. Covers combined date+time picker:
 * - Display mode: Intl.DateTimeFormat formatted output, emptyValueMessage
 * - Edit mode: <input type="datetime-local">, onChange, clearing
 * - useCurrentTimeAsDefault: auto-populate on first render
 * - Timezone handling: ISO ↔ datetime-local conversion
 * - Access control: full / readonly / forbidden
 * - Validation errors
 * - Null/empty handling
 * - Visibility toggling
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom/vitest';
import React from 'react';
import DateTimeField from '../../../src/components/fields/DateTimeField';
import type { DateTimeFieldProps } from '../../../src/components/fields/DateTimeField';

/* ────────────────────────────────────────────────────────────────
   Helpers
   ──────────────────────────────────────────────────────────────── */

/**
 * Build a complete DateTimeFieldProps object with sensible defaults.
 * Override any subset of props via the `overrides` parameter.
 */
function buildProps(overrides: Partial<DateTimeFieldProps> = {}): DateTimeFieldProps {
  return {
    name: 'event_datetime',
    value: '2024-03-15T14:30:00.000Z',
    onChange: vi.fn(),
    mode: 'edit',
    access: 'full',
    isVisible: true,
    ...overrides,
  };
}

/**
 * Compute the expected datetime-local input value for a given ISO string.
 * Mirrors the component's isoToDatetimeLocal logic.
 */
function expectedLocalValue(isoString: string): string {
  const date = new Date(isoString);
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  const hours = String(date.getHours()).padStart(2, '0');
  const minutes = String(date.getMinutes()).padStart(2, '0');
  return `${year}-${month}-${day}T${hours}:${minutes}`;
}

/* ────────────────────────────────────────────────────────────────
   Test Suite
   ──────────────────────────────────────────────────────────────── */

describe('DateTimeField', () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  /* ═══════════════════════════ DISPLAY MODE ═══════════════════════════ */

  describe('display mode', () => {
    it('formats ISO datetime string using Intl.DateTimeFormat with dateStyle and timeStyle', () => {
      const isoValue = '2024-03-15T14:30:00.000Z';
      render(
        <DateTimeField {...buildProps({ mode: 'display', value: isoValue })} />,
      );

      // Compute the expected formatted string using the same Intl formatter
      const expected = new Intl.DateTimeFormat(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
      }).format(new Date(isoValue));

      const element = screen.getByText(expected);
      expect(element).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <DateTimeField {...buildProps({ mode: 'display', value: null })} />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('shows both date and time parts in display', () => {
      const isoValue = '2024-06-20T09:15:00.000Z';
      render(
        <DateTimeField {...buildProps({ mode: 'display', value: isoValue })} />,
      );

      const expected = new Intl.DateTimeFormat(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
      }).format(new Date(isoValue));

      const timeElement = screen.getByText(expected);
      expect(timeElement).toBeInTheDocument();
      // The component renders a <time> element with the original ISO as datetime attribute
      expect(timeElement.tagName).toBe('TIME');
      expect(timeElement).toHaveAttribute('datetime', isoValue);
    });
  });

  /* ═══════════════════════════ EDIT MODE ═══════════════════════════ */

  describe('edit mode', () => {
    it('renders an input[type="datetime-local"] in edit mode', () => {
      const { container } = render(
        <DateTimeField
          {...buildProps({ mode: 'edit', value: '2024-03-15T14:30:00.000Z' })}
        />,
      );

      const input = container.querySelector('input[type="datetime-local"]');
      expect(input).toBeInTheDocument();
    });

    it('displays current datetime value in the input', () => {
      const isoValue = '2024-03-15T14:30:00.000Z';
      const { container } = render(
        <DateTimeField {...buildProps({ mode: 'edit', value: isoValue })} />,
      );

      const input = container.querySelector(
        'input[type="datetime-local"]',
      ) as HTMLInputElement;
      expect(input.value).toBe(expectedLocalValue(isoValue));
    });

    it('calls onChange with ISO datetime string when value changes', () => {
      const handleChange = vi.fn();
      const { container } = render(
        <DateTimeField
          {...buildProps({ mode: 'edit', value: null, onChange: handleChange })}
        />,
      );

      const input = container.querySelector(
        'input[type="datetime-local"]',
      ) as HTMLInputElement;
      fireEvent.change(input, { target: { value: '2024-06-20T10:15' } });

      expect(handleChange).toHaveBeenCalledTimes(1);
      // The component should emit an ISO 8601 UTC string
      const calledWith = handleChange.mock.calls[0][0] as string;
      expect(calledWith).toMatch(
        /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/,
      );
    });

    it('handles clearing the datetime (empty → null)', () => {
      const handleChange = vi.fn();
      const { container } = render(
        <DateTimeField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15T14:30:00.000Z',
            onChange: handleChange,
          })}
        />,
      );

      const input = container.querySelector(
        'input[type="datetime-local"]',
      ) as HTMLInputElement;
      fireEvent.change(input, { target: { value: '' } });

      expect(handleChange).toHaveBeenCalledWith(null);
    });
  });

  /* ═══════════════════ useCurrentTimeAsDefault ═══════════════════ */

  describe('useCurrentTimeAsDefault', () => {
    it('defaults to current datetime when useCurrentTimeAsDefault=true and no value', async () => {
      const handleChange = vi.fn();
      const beforeMs = Date.now();

      render(
        <DateTimeField
          {...buildProps({
            mode: 'edit',
            value: null,
            useCurrentTimeAsDefault: true,
            onChange: handleChange,
          })}
        />,
      );

      // onChange is scheduled via Promise.resolve().then() — wait for the microtask
      await waitFor(() => {
        expect(handleChange).toHaveBeenCalledTimes(1);
      });

      const calledWith = handleChange.mock.calls[0][0] as string;
      // Must be a valid ISO 8601 string
      expect(calledWith).toMatch(/^\d{4}-\d{2}-\d{2}T/);

      // Verify the generated time is reasonable (within 5 seconds of now)
      const calledMs = new Date(calledWith).getTime();
      const afterMs = Date.now();
      expect(calledMs).toBeGreaterThanOrEqual(beforeMs - 1000);
      expect(calledMs).toBeLessThanOrEqual(afterMs + 1000);
    });

    it('does not default when useCurrentTimeAsDefault=false', async () => {
      const handleChange = vi.fn();

      render(
        <DateTimeField
          {...buildProps({
            mode: 'edit',
            value: null,
            useCurrentTimeAsDefault: false,
            onChange: handleChange,
          })}
        />,
      );

      // Wait long enough to confirm nothing fires
      await new Promise((resolve) => {
        setTimeout(resolve, 50);
      });
      expect(handleChange).not.toHaveBeenCalled();
    });

    it('does not override existing value', async () => {
      const handleChange = vi.fn();

      render(
        <DateTimeField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15T14:30:00.000Z',
            useCurrentTimeAsDefault: true,
            onChange: handleChange,
          })}
        />,
      );

      // Wait to ensure no microtask fires onChange
      await new Promise((resolve) => {
        setTimeout(resolve, 50);
      });
      expect(handleChange).not.toHaveBeenCalled();
    });
  });

  /* ═══════════════════ TIMEZONE HANDLING ═══════════════════ */

  describe('timezone handling', () => {
    it('converts ISO string to datetime-local format for input', () => {
      const isoValue = '2024-07-04T18:00:00.000Z';
      const { container } = render(
        <DateTimeField {...buildProps({ mode: 'edit', value: isoValue })} />,
      );

      const input = container.querySelector(
        'input[type="datetime-local"]',
      ) as HTMLInputElement;
      // The value must be in YYYY-MM-DDTHH:mm format (local time)
      expect(input.value).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);
    });

    it('handles UTC ↔ local conversion', () => {
      const utcIso = '2024-12-31T23:59:00.000Z';
      const { container } = render(
        <DateTimeField {...buildProps({ mode: 'edit', value: utcIso })} />,
      );

      const input = container.querySelector(
        'input[type="datetime-local"]',
      ) as HTMLInputElement;

      // The input value should represent the local equivalent of the UTC time
      expect(input.value).toBe(expectedLocalValue(utcIso));
    });

    it('emits ISO datetime string on change', () => {
      const handleChange = vi.fn();
      const { container } = render(
        <DateTimeField
          {...buildProps({ mode: 'edit', value: null, onChange: handleChange })}
        />,
      );

      const input = container.querySelector(
        'input[type="datetime-local"]',
      ) as HTMLInputElement;
      fireEvent.change(input, { target: { value: '2024-08-15T10:30' } });

      expect(handleChange).toHaveBeenCalledTimes(1);
      const emittedIso = handleChange.mock.calls[0][0] as string;

      // Should be an ISO 8601 string ending with Z
      expect(emittedIso).toMatch(
        /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/,
      );

      // Round-trip: parse back and verify the date parts are sensible
      const parsed = new Date(emittedIso);
      expect(parsed.getFullYear()).toBe(2024);
      // August = month index 7
      expect(parsed.getMonth()).toBe(7);
    });
  });

  /* ═══════════════════ ACCESS CONTROL ═══════════════════ */

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      const { container } = render(
        <DateTimeField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15T14:30:00.000Z',
            access: 'full',
          })}
        />,
      );

      const input = container.querySelector('input[type="datetime-local"]');
      expect(input).toBeInTheDocument();
      expect(input).not.toBeDisabled();
    });

    it('renders as readonly with access="readonly"', () => {
      const { container } = render(
        <DateTimeField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15T14:30:00.000Z',
            access: 'readonly',
          })}
        />,
      );

      const input = container.querySelector('input[type="datetime-local"]');
      expect(input).toBeInTheDocument();
      expect(input).toBeDisabled();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <DateTimeField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15T14:30:00.000Z',
            access: 'forbidden',
          })}
        />,
      );

      // The component should render an alert element with the access-denied message
      const alert = screen.getByRole('alert');
      expect(alert).toBeInTheDocument();
      expect(alert).toHaveTextContent('access denied');

      // No datetime input should be present
      expect(
        document.querySelector('input[type="datetime-local"]'),
      ).not.toBeInTheDocument();
    });
  });

  /* ═══════════════════ VALIDATION ═══════════════════ */

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      const { container } = render(
        <DateTimeField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15T14:30:00.000Z',
            error: 'Date is required',
          })}
        />,
      );

      const input = container.querySelector('input[type="datetime-local"]');
      expect(input).toHaveAttribute('aria-invalid', 'true');
      expect(input).toHaveClass('border-red-500');
    });

    it('shows validation errors', () => {
      const { container } = render(
        <DateTimeField
          {...buildProps({
            mode: 'edit',
            value: 'invalid-date',
            error: 'Invalid date format',
          })}
        />,
      );

      const input = container.querySelector('input[type="datetime-local"]');
      expect(input).toHaveAttribute('aria-invalid', 'true');
      expect(input).toHaveClass('border-red-500');
      expect(input).toHaveClass('focus:ring-red-500');
    });
  });

  /* ═══════════════════ NULL / EMPTY HANDLING ═══════════════════ */

  describe('null/empty handling', () => {
    it('handles null value', () => {
      render(
        <DateTimeField {...buildProps({ mode: 'display', value: null })} />,
      );
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('handles undefined value', () => {
      render(
        <DateTimeField
          {...buildProps({
            mode: 'display',
            value: undefined as unknown as string | null,
          })}
        />,
      );
      expect(screen.getByText('no data')).toBeInTheDocument();
    });
  });

  /* ═══════════════════ VISIBILITY ═══════════════════ */

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      const { container } = render(
        <DateTimeField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15T14:30:00.000Z',
            isVisible: true,
          })}
        />,
      );

      const input = container.querySelector('input[type="datetime-local"]');
      expect(input).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <DateTimeField
          {...buildProps({
            mode: 'edit',
            value: '2024-03-15T14:30:00.000Z',
            isVisible: false,
          })}
        />,
      );

      expect(container.innerHTML).toBe('');
      expect(
        document.querySelector('input[type="datetime-local"]'),
      ).not.toBeInTheDocument();
    });
  });
});
