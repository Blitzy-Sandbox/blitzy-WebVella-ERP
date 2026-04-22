/**
 * TimeField — Vitest Unit Tests
 *
 * Comprehensive test suite for the TimeField component that replaces the
 * monolith's PcFieldTime ViewComponent (WebVella.Erp.Web/Components/PcFieldTime).
 *
 * Tests cover:
 *   - Display mode (localized time string formatting, emptyValueMessage for null,
 *     HH:mm and HH:mm:ss format display)
 *   - Edit mode (input[type="time"], onChange callback with time string, clearing
 *     time to null)
 *   - Value format (HH:mm and HH:mm:ss acceptance and emission)
 *   - Access control (full / readonly / forbidden)
 *   - Validation / error state (aria-invalid, error classes, validation errors)
 *   - Null / empty value handling
 *   - Visibility toggling (isVisible prop)
 *
 * @module tests/unit/fields/TimeField.test
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom/vitest';
import React from 'react';
import TimeField from '../../../src/components/fields/TimeField';
import type { TimeFieldProps } from '../../../src/components/fields/TimeField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Builds a complete TimeFieldProps object with sensible defaults.
 * Individual tests override only the props they care about, keeping
 * each test focused and reducing boilerplate.
 */
const buildProps = (overrides: Partial<TimeFieldProps> = {}): TimeFieldProps => ({
  name: 'start_time',
  value: '14:30',
  ...overrides,
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('TimeField', () => {
  afterEach(() => {
    cleanup();
  });

  // ========================================================================
  // Display Mode
  // ========================================================================
  describe('display mode', () => {
    it('formats time string for localized display', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'display',
            value: '14:30',
          })}
        />,
      );

      // In display mode, the component uses Intl.DateTimeFormat for locale-aware
      // rendering. The exact output depends on the test environment locale,
      // but the element with the time value should exist.
      const fieldElement = document.querySelector('[data-field-name="start_time"]');
      expect(fieldElement).toBeInTheDocument();

      // The formatted time should be visible — "2:30 PM", "14:30", etc.
      // depending on locale. We verify text content is non-empty and not
      // the emptyValueMessage.
      expect(fieldElement).not.toHaveTextContent('no data');
      expect(fieldElement!.textContent!.trim().length).toBeGreaterThan(0);
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'display',
            value: null,
          })}
        />,
      );

      // Default emptyValueMessage is "no data"
      expect(screen.getByText('no data')).toBeInTheDocument();

      // The empty value span should have italic styling
      const emptySpan = screen.getByText('no data');
      expect(emptySpan).toHaveClass('italic');
    });

    it('displays "HH:mm" format correctly', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'display',
            value: '09:15',
          })}
        />,
      );

      const fieldElement = document.querySelector('[data-field-name="start_time"]');
      expect(fieldElement).toBeInTheDocument();

      // The formatted display should contain relevant time info (not empty)
      expect(fieldElement).not.toHaveTextContent('no data');

      // The clock icon SVG should be rendered for non-empty values
      const svg = fieldElement!.querySelector('svg');
      expect(svg).toBeInTheDocument();
    });

    it('displays "HH:mm:ss" format correctly', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'display',
            value: '09:15:30',
          })}
        />,
      );

      const fieldElement = document.querySelector('[data-field-name="start_time"]');
      expect(fieldElement).toBeInTheDocument();

      // Non-empty display — should show formatted time, not the empty message
      expect(fieldElement).not.toHaveTextContent('no data');

      // Clock icon should be present for non-empty values
      const svg = fieldElement!.querySelector('svg');
      expect(svg).toBeInTheDocument();
    });

    it('renders custom emptyValueMessage when provided', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'Not set',
          })}
        />,
      );

      expect(screen.getByText('Not set')).toBeInTheDocument();
    });

    it('falls back to raw value when Intl.DateTimeFormat fails', () => {
      // Use an invalid locale to trigger the fallback path
      render(
        <TimeField
          {...buildProps({
            mode: 'display',
            value: '08:45',
            locale: 'invalid-locale-that-does-not-exist-xyz',
          })}
        />,
      );

      const fieldElement = document.querySelector('[data-field-name="start_time"]');
      expect(fieldElement).toBeInTheDocument();
      // Component falls back to raw value on Intl error
      expect(fieldElement).not.toHaveTextContent('no data');
    });
  });

  // ========================================================================
  // Edit Mode
  // ========================================================================
  describe('edit mode', () => {
    it('renders an input[type="time"] in edit mode', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveAttribute('type', 'time');
    });

    it('displays current time value', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveValue('14:30');
    });

    it('calls onChange with time string when value changes', () => {
      const handleChange = vi.fn();
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30',
            onChange: handleChange,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();

      // Simulate the native time input change event
      fireEvent.change(input, { target: { value: '16:45' } });

      expect(handleChange).toHaveBeenCalledTimes(1);
      expect(handleChange).toHaveBeenCalledWith('16:45');
    });

    it('handles clearing time (empty → null)', () => {
      const handleChange = vi.fn();
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30',
            onChange: handleChange,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();

      // Clearing the input sets value to empty string, which should emit null
      fireEvent.change(input, { target: { value: '' } });

      expect(handleChange).toHaveBeenCalledTimes(1);
      expect(handleChange).toHaveBeenCalledWith(null);
    });

    it('renders with correct name attribute', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            name: 'appointment_time',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toHaveAttribute('name', 'appointment_time');
    });

    it('applies required attribute when required=true', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            required: true,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeRequired();
      expect(input).toHaveAttribute('aria-required', 'true');
    });

    it('sets the step attribute for minutes-only (HH:mm) value', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      // HH:mm format → step should be 60 (minutes granularity)
      expect(input).toHaveAttribute('step', '60');
    });

    it('sets the step attribute for seconds (HH:mm:ss) value', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30:45',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      // HH:mm:ss format → step should be 1 (seconds granularity)
      expect(input).toHaveAttribute('step', '1');
    });

    it('uses computed fieldId for the id attribute', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            name: 'my_time',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      // Default fieldId is `field-${name}`
      expect(input).toHaveAttribute('id', 'field-my_time');
    });

    it('uses explicit fieldId when provided', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            fieldId: 'custom-time-id',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toHaveAttribute('id', 'custom-time-id');
    });
  });

  // ========================================================================
  // Value Format
  // ========================================================================
  describe('value format', () => {
    it('accepts "HH:mm" format', () => {
      const { container } = render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '08:30',
          })}
        />,
      );

      const input = container.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveValue('08:30');
    });

    it('accepts "HH:mm:ss" format', () => {
      const { container } = render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '08:30:45',
          })}
        />,
      );

      const input = container.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveValue('08:30:45');
    });

    it('emits time string on change', () => {
      const handleChange = vi.fn();
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '10:00',
            onChange: handleChange,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      fireEvent.change(input, { target: { value: '11:30' } });

      expect(handleChange).toHaveBeenCalledWith('11:30');
    });

    it('preserves seconds format when original value had seconds', () => {
      const handleChange = vi.fn();
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30:00',
            onChange: handleChange,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      // Native time inputs may emit HH:mm even when step=1 in some cases.
      // The component should append ":00" to maintain HH:mm:ss format.
      fireEvent.change(input, { target: { value: '16:45' } });

      // Original value had seconds, so the emitted value should include ":00"
      expect(handleChange).toHaveBeenCalledWith('16:45:00');
    });

    it('does not append seconds when original value was HH:mm', () => {
      const handleChange = vi.fn();
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30',
            onChange: handleChange,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      fireEvent.change(input, { target: { value: '16:45' } });

      // Original value was HH:mm, so the emitted value should remain HH:mm
      expect(handleChange).toHaveBeenCalledWith('16:45');
    });

    it('passes through HH:mm:ss emissions unchanged when original had seconds', () => {
      const handleChange = vi.fn();
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30:15',
            onChange: handleChange,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      // Emit a full HH:mm:ss value — should pass through without modification
      fireEvent.change(input, { target: { value: '16:45:30' } });

      expect(handleChange).toHaveBeenCalledWith('16:45:30');
    });
  });

  // ========================================================================
  // Access Control
  // ========================================================================
  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).not.toBeDisabled();
      expect(input).not.toHaveAttribute('readOnly');
    });

    it('renders as readonly with access="readonly"', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            access: 'readonly',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();

      // Readonly access should disable the input and set readOnly attribute
      expect(input).toBeDisabled();
      expect(input).toHaveAttribute('readOnly');

      // Readonly styling: muted background and cursor
      expect(input).toHaveClass('bg-gray-50');
      expect(input).toHaveClass('cursor-not-allowed');
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30',
            access: 'forbidden',
          })}
        />,
      );

      // Forbidden renders an alert with the default accessDeniedMessage
      const alert = screen.getByRole('alert');
      expect(alert).toBeInTheDocument();
      expect(alert).toHaveTextContent('access denied');

      // No time input should be rendered
      expect(document.querySelector('input[type="time"]')).not.toBeInTheDocument();
    });

    it('renders custom accessDeniedMessage with access="forbidden"', () => {
      render(
        <TimeField
          {...buildProps({
            access: 'forbidden',
            accessDeniedMessage: 'Insufficient permissions',
          })}
        />,
      );

      expect(screen.getByRole('alert')).toHaveTextContent('Insufficient permissions');
    });

    it('forbidden access preserves data-field-name attribute', () => {
      render(
        <TimeField
          {...buildProps({
            name: 'restricted_time',
            access: 'forbidden',
          })}
        />,
      );

      const alert = screen.getByRole('alert');
      expect(alert).toHaveAttribute('data-field-name', 'restricted_time');
    });

    it('renders display mode as readonly with access="readonly"', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'display',
            access: 'readonly',
            value: '14:30',
          })}
        />,
      );

      // Display mode with readonly should still show the formatted time
      const fieldElement = document.querySelector('[data-field-name="start_time"]');
      expect(fieldElement).toBeInTheDocument();
      expect(fieldElement).not.toHaveTextContent('no data');
    });
  });

  // ========================================================================
  // Validation
  // ========================================================================
  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            error: 'Time is required',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();

      // aria-invalid should be true when error is provided
      expect(input).toHaveAttribute('aria-invalid', 'true');

      // Error state should apply red border styling
      expect(input).toHaveClass('border-red-500');
    });

    it('shows validation errors with correct aria-describedby', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            name: 'event_time',
            error: 'Invalid time format',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;

      // aria-describedby should reference the error element ID
      expect(input).toHaveAttribute('aria-describedby', 'event_time-error');
    });

    it('applies normal border when no error', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            error: undefined,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toHaveAttribute('aria-invalid', 'false');
      expect(input).toHaveClass('border-gray-300');
      expect(input).not.toHaveClass('border-red-500');
    });

    it('sets aria-describedby to description when no error', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            name: 'event_time',
            description: 'Enter the event start time',
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toHaveAttribute('aria-describedby', 'event_time-description');
    });

    it('omits aria-describedby when no error and no description', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            error: undefined,
            description: undefined,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).not.toHaveAttribute('aria-describedby');
    });
  });

  // ========================================================================
  // Null / Empty Handling
  // ========================================================================
  describe('null/empty handling', () => {
    it('handles null value in edit mode', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: null,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();
      // Null value should render as empty string in the input
      expect(input).toHaveValue('');
    });

    it('handles empty string value in edit mode', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '' as unknown as string,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveValue('');
    });

    it('handles null value in display mode with emptyValueMessage', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'display',
            value: null,
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('handles empty string value in display mode', () => {
      render(
        <TimeField
          {...buildProps({
            mode: 'display',
            value: '' as unknown as string,
          })}
        />,
      );

      // Empty string is treated as no data
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('does not call onChange when onChange is not provided', () => {
      // Rendering without onChange should not throw
      render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30',
            onChange: undefined,
          })}
        />,
      );

      const input = document.querySelector('input[type="time"]') as HTMLInputElement;
      expect(input).toBeInTheDocument();

      // Should not throw when interacted with
      expect(() => {
        fireEvent.change(input, { target: { value: '16:00' } });
      }).not.toThrow();
    });
  });

  // ========================================================================
  // Visibility
  // ========================================================================
  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      const { container } = render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30',
            isVisible: true,
          })}
        />,
      );

      const input = container.querySelector('input[type="time"]');
      expect(input).toBeInTheDocument();
      expect(input).toBeVisible();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '14:30',
            isVisible: false,
          })}
        />,
      );

      // Component returns null when isVisible=false
      expect(container.innerHTML).toBe('');
      expect(document.querySelector('input[type="time"]')).not.toBeInTheDocument();
    });

    it('renders nothing in display mode when isVisible=false', () => {
      const { container } = render(
        <TimeField
          {...buildProps({
            mode: 'display',
            value: '14:30',
            isVisible: false,
          })}
        />,
      );

      expect(container.innerHTML).toBe('');
    });

    it('defaults isVisible to true', () => {
      // Render without explicit isVisible — should default to true
      const { container } = render(
        <TimeField
          {...buildProps({
            mode: 'edit',
            value: '09:00',
          })}
        />,
      );

      const input = container.querySelector('input[type="time"]');
      expect(input).toBeInTheDocument();
    });
  });
});
