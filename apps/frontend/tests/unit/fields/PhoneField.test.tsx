/**
 * PhoneField — Vitest Unit Tests
 *
 * Comprehensive test suite for the PhoneField component that replaces the
 * monolith's PcFieldPhone ViewComponent (WebVella.Erp.Web/Components/PcFieldPhone).
 *
 * Tests cover:
 *   - Display mode (tel: clickable link rendering, emptyValueMessage for null/empty)
 *   - Edit mode (input[type="tel"], onChange callback, maxLength attribute)
 *   - Phone formatting (tel: link in display, optional formatting on blur)
 *   - Access control (full / readonly / forbidden)
 *   - Validation / error state (aria-invalid, error classes, validation errors)
 *   - Null / empty value handling
 *   - Visibility toggling (isVisible prop)
 *
 * @module tests/unit/fields/PhoneField.test
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom/vitest';
import React from 'react';
import PhoneField from '../../../src/components/fields/PhoneField';
import type { PhoneFieldProps } from '../../../src/components/fields/PhoneField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Builds a complete PhoneFieldProps object with sensible defaults.
 * Individual tests override only the props they care about, keeping
 * each test focused and reducing boilerplate.
 */
const buildProps = (overrides: Partial<PhoneFieldProps> = {}): PhoneFieldProps => ({
  name: 'phone',
  value: '+1 (555) 123-4567',
  ...overrides,
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('PhoneField', () => {
  afterEach(() => {
    cleanup();
  });

  // ========================================================================
  // Display Mode
  // ========================================================================
  describe('display mode', () => {
    it('renders phone number as <a href="tel:value"> clickable link', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: '+1 (555) 123-4567',
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toBeInTheDocument();
      // The tel: href should contain sanitized digits with leading +
      expect(link).toHaveAttribute('href', 'tel:+15551234567');
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: null,
          })}
        />,
      );

      // Default emptyValueMessage is "no data"
      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is empty string', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: '',
          })}
        />,
      );

      // Empty string triggers emptyValueMessage
      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('renders custom emptyValueMessage when provided', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'No phone number',
          })}
        />,
      );

      expect(screen.getByText('No phone number')).toBeInTheDocument();
    });

    it('renders phone icon alongside the link', () => {
      const { container } = render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: '5551234567',
          })}
        />,
      );

      // The phone icon renders an <svg> with aria-hidden="true"
      const svg = container.querySelector('svg[aria-hidden="true"]');
      expect(svg).toBeInTheDocument();
    });

    it('sets data-field-name attribute in display mode', () => {
      const { container } = render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: '+15551234567',
            name: 'mobile',
          })}
        />,
      );

      const wrapper = container.querySelector('[data-field-name="mobile"]');
      expect(wrapper).toBeInTheDocument();
    });

    it('formats 10-digit phone numbers in display text', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: '5551234567',
          })}
        />,
      );

      const link = screen.getByRole('link');
      // 10-digit formatting: (555) 123-4567
      expect(link).toHaveTextContent('(555) 123-4567');
    });
  });

  // ========================================================================
  // Edit Mode
  // ========================================================================
  describe('edit mode', () => {
    it('renders an input[type="tel"] in edit mode', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '5551234567',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeInTheDocument();
      expect(input).toHaveAttribute('type', 'tel');
    });

    it('displays current phone value', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '+1 (555) 987-6543',
          })}
        />,
      );

      const input = screen.getByRole('textbox') as HTMLInputElement;
      expect(input.value).toBe('+1 (555) 987-6543');
    });

    it('calls onChange when user types', async () => {
      const handleChange = vi.fn();
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '',
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      fireEvent.change(input, { target: { value: '5551234567' } });

      expect(handleChange).toHaveBeenCalledTimes(1);
      expect(handleChange).toHaveBeenCalledWith('5551234567');
    });

    it('calls onChange with updated value using userEvent', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '',
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      await user.click(input);
      await user.type(input, '555');

      // userEvent.type fires one onChange per character
      expect(handleChange).toHaveBeenCalled();
      expect(handleChange.mock.calls.length).toBeGreaterThan(0);
    });

    it('applies maxLength attribute when provided', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '',
            maxLength: 15,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('maxLength', '15');
    });

    it('does not apply maxLength when null', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '',
            maxLength: null,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).not.toHaveAttribute('maxLength');
    });

    it('sets name attribute on the input', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '',
            name: 'contact_phone',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('name', 'contact_phone');
    });

    it('sets autoComplete="tel" on the input', () => {
      render(
        <PhoneField {...buildProps({ mode: 'edit', value: '' })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('autoComplete', 'tel');
    });

    it('sets placeholder when provided', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '',
            placeholder: 'Enter phone…',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('placeholder', 'Enter phone…');
    });
  });

  // ========================================================================
  // Phone Formatting
  // ========================================================================
  describe('phone formatting', () => {
    it('renders tel: link for display mode', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: '(555) 123-4567',
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toBeInTheDocument();
      // tel: href sanitizes to digits only
      expect(link).toHaveAttribute('href', 'tel:5551234567');
    });

    it('preserves leading + in tel: href for international numbers', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: '+44 20 7946 0958',
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toHaveAttribute('href', 'tel:+442079460958');
    });

    it('optional phone formatting on blur', () => {
      const handleChange = vi.fn();

      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '5551234567',
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('textbox');

      // Focus and then blur to trigger formatting
      fireEvent.focus(input);
      fireEvent.blur(input);

      // 10-digit number should be formatted to (555) 123-4567
      expect(handleChange).toHaveBeenCalledWith('(555) 123-4567');
    });

    it('formats 11-digit US numbers with leading 1 on blur', () => {
      const handleChange = vi.fn();

      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '15551234567',
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      fireEvent.focus(input);
      fireEvent.blur(input);

      // 11-digit with leading 1 → +1 (XXX) XXX-XXXX
      expect(handleChange).toHaveBeenCalledWith('+1 (555) 123-4567');
    });

    it('does not reformat already-formatted numbers on blur', () => {
      const handleChange = vi.fn();

      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '(555) 123-4567',
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      fireEvent.focus(input);
      fireEvent.blur(input);

      // Already formatted — onChange should not be called since result matches current value
      expect(handleChange).not.toHaveBeenCalled();
    });

    it('does not format empty value on blur', () => {
      const handleChange = vi.fn();

      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '',
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      fireEvent.focus(input);
      fireEvent.blur(input);

      // No formatting for empty string
      expect(handleChange).not.toHaveBeenCalled();
    });

    it('does not format international numbers on blur (non-standard lengths)', () => {
      const handleChange = vi.fn();

      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '+44 20 7946 0958',
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      fireEvent.focus(input);
      fireEvent.blur(input);

      // International numbers are returned unchanged, so no onChange call
      expect(handleChange).not.toHaveBeenCalled();
    });
  });

  // ========================================================================
  // Access Control
  // ========================================================================
  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '5551234567',
            access: 'full',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeInTheDocument();
      expect(input).not.toBeDisabled();
      expect(input).toBeVisible();
    });

    it('renders as readonly with access="readonly"', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '5551234567',
            access: 'readonly',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeInTheDocument();
      // readonly access disables the input
      expect(input).toBeDisabled();
      expect(input).toHaveAttribute('readOnly');
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '5551234567',
            access: 'forbidden',
          })}
        />,
      );

      // Forbidden renders a span with role="alert" and default accessDeniedMessage
      const alert = screen.getByRole('alert');
      expect(alert).toBeInTheDocument();
      expect(alert).toHaveTextContent('access denied');

      // No input or link should be rendered
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('renders custom accessDeniedMessage with access="forbidden"', () => {
      render(
        <PhoneField
          {...buildProps({
            access: 'forbidden',
            accessDeniedMessage: 'Insufficient permissions',
          })}
        />,
      );

      expect(screen.getByRole('alert')).toHaveTextContent(
        'Insufficient permissions',
      );
    });

    it('forbidden access renders data-field-name attribute', () => {
      render(
        <PhoneField
          {...buildProps({
            name: 'restricted_phone',
            access: 'forbidden',
          })}
        />,
      );

      const alert = screen.getByRole('alert');
      expect(alert).toHaveAttribute('data-field-name', 'restricted_phone');
    });

    it('applies disabled styling class when disabled=true', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '5551234567',
            disabled: true,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeDisabled();
      // Disabled state applies muted background class
      expect(input).toHaveClass('bg-gray-50');
    });
  });

  // ========================================================================
  // Validation
  // ========================================================================
  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: 'invalid',
            error: 'Please enter a valid phone number',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      // Component sets aria-invalid when error is truthy
      expect(input).toHaveAttribute('aria-invalid', 'true');
      // Error state applies red border classes
      expect(input).toHaveClass('border-red-500');
    });

    it('sets aria-describedby to error id when error is present', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            name: 'phone',
            value: '',
            error: 'Phone is required',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      // aria-describedby = `${name}-error` = "phone-error"
      expect(input).toHaveAttribute('aria-describedby', 'phone-error');
    });

    it('shows validation errors', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: 'abc',
            error: 'Invalid phone format',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      // Validation state indicated via aria-invalid
      expect(input).toHaveAttribute('aria-invalid', 'true');
      // Error red border class applied
      expect(input).toHaveClass('border-red-500');
      expect(input).toHaveClass('focus:ring-red-500');
    });

    it('applies normal CSS classes when no error', () => {
      render(
        <PhoneField
          {...buildProps({ mode: 'edit', value: '5551234567' })}
        />,
      );

      const input = screen.getByRole('textbox');
      // Non-error state classes: border-gray-300
      expect(input).toHaveClass('border-gray-300');
    });

    it('does not set aria-invalid when no error', () => {
      render(
        <PhoneField {...buildProps({ mode: 'edit', value: '' })} />,
      );

      const input = screen.getByRole('textbox');
      // aria-invalid should be 'false' (no error)
      expect(input).toHaveAttribute('aria-invalid', 'false');
    });

    it('sets required attribute when required=true', () => {
      render(
        <PhoneField
          {...buildProps({ mode: 'edit', value: '', required: true })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('required');
    });
  });

  // ========================================================================
  // Null / Empty Handling
  // ========================================================================
  describe('null/empty handling', () => {
    it('handles null value in edit mode', () => {
      render(
        <PhoneField
          {...buildProps({ mode: 'edit', value: null })}
        />,
      );

      const input = screen.getByRole('textbox') as HTMLInputElement;
      // Null value is coerced to empty string
      expect(input.value).toBe('');
    });

    it('handles undefined value', () => {
      // TypeScript would flag this, but test runtime resilience
      render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: undefined as unknown as string | null,
          })}
        />,
      );

      // undefined is treated as empty → emptyValueMessage should appear
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('shows emptyValueMessage in display mode', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'Not provided',
          })}
        />,
      );

      expect(screen.getByText('Not provided')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('handles whitespace-only value in display mode as empty', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: '   ',
          })}
        />,
      );

      // Whitespace-only string is treated as empty
      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('handles null value in edit mode without crashing onChange', () => {
      const handleChange = vi.fn();

      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      fireEvent.change(input, { target: { value: '555' } });

      expect(handleChange).toHaveBeenCalledWith('555');
    });
  });

  // ========================================================================
  // Visibility
  // ========================================================================
  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '5551234567',
            isVisible: true,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeInTheDocument();
      expect(input).toBeVisible();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <PhoneField
          {...buildProps({
            mode: 'edit',
            value: '5551234567',
            isVisible: false,
          })}
        />,
      );

      // Component returns null when isVisible=false
      expect(container.innerHTML).toBe('');
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('renders nothing in display mode when isVisible=false', () => {
      const { container } = render(
        <PhoneField
          {...buildProps({
            mode: 'display',
            value: '+1 (555) 123-4567',
            isVisible: false,
          })}
        />,
      );

      expect(container.innerHTML).toBe('');
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });
  });
});
