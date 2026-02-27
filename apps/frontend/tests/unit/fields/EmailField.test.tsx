/**
 * EmailField — Vitest Unit Tests
 *
 * Comprehensive test suite for the EmailField component that replaces the
 * monolith's PcFieldEmail ViewComponent (WebVella.Erp.Web/Components/PcFieldEmail).
 *
 * Tests cover:
 *   - Display mode (mailto: clickable link rendering, emptyValueMessage for null/empty)
 *   - Edit mode (input[type="email"], onChange callback, maxLength attribute,
 *     native browser email validation)
 *   - Email validation (native type="email" validation, custom error prop)
 *   - Access control (full / readonly / forbidden)
 *   - Validation / error state (aria-invalid, error classes, validation errors)
 *   - Null / empty value handling
 *   - Visibility toggling (isVisible prop)
 *
 * @module tests/unit/fields/EmailField.test
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom/vitest';
import React from 'react';
import EmailField from '../../../src/components/fields/EmailField';
import type { EmailFieldProps } from '../../../src/components/fields/EmailField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Builds a complete EmailFieldProps object with sensible defaults.
 * Individual tests override only the props they care about, keeping
 * each test focused and reducing boilerplate.
 */
const buildProps = (overrides: Partial<EmailFieldProps> = {}): EmailFieldProps => ({
  name: 'email',
  value: 'user@example.com',
  ...overrides,
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('EmailField', () => {
  afterEach(() => {
    cleanup();
  });

  // ========================================================================
  // Display Mode
  // ========================================================================
  describe('display mode', () => {
    it('renders email as <a href="mailto:value"> clickable link', () => {
      render(
        <EmailField
          {...buildProps({
            mode: 'display',
            value: 'admin@webvella.com',
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toBeInTheDocument();
      // The mailto: href should contain the email address
      expect(link).toHaveAttribute('href', expect.stringContaining('mailto:admin@webvella.com'));
      expect(link).toHaveTextContent('admin@webvella.com');
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <EmailField
          {...buildProps({
            mode: 'display',
            value: null,
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
      // No link should be rendered for null values
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is empty string', () => {
      render(
        <EmailField
          {...buildProps({
            mode: 'display',
            value: '',
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('renders custom emptyValueMessage when provided', () => {
      render(
        <EmailField
          {...buildProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'No email provided',
          })}
        />,
      );

      expect(screen.getByText('No email provided')).toBeInTheDocument();
    });

    it('applies correct link styling', () => {
      render(
        <EmailField
          {...buildProps({
            mode: 'display',
            value: 'styled@example.com',
          })}
        />,
      );

      const link = screen.getByRole('link');
      // EmailField applies blue link styling from Tailwind
      expect(link).toHaveClass('text-blue-600');
    });

    it('renders envelope icon alongside the link', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'display',
            value: 'icon@example.com',
          })}
        />,
      );

      // The envelope icon renders an <svg> with aria-hidden="true"
      const svg = container.querySelector('svg[aria-hidden="true"]');
      expect(svg).toBeInTheDocument();
    });

    it('sets data-field-name attribute in display mode', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'display',
            value: 'test@example.com',
            name: 'contact_email',
          })}
        />,
      );

      const wrapper = container.querySelector('[data-field-name="contact_email"]');
      expect(wrapper).toBeInTheDocument();
    });

    it('sets aria-label for screen readers on the link', () => {
      render(
        <EmailField
          {...buildProps({
            mode: 'display',
            value: 'a11y@example.com',
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toHaveAttribute('aria-label', expect.stringContaining('a11y@example.com'));
    });
  });

  // ========================================================================
  // Edit Mode
  // ========================================================================
  describe('edit mode', () => {
    it('renders an input[type="email"] in edit mode', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'test@example.com',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toBeInTheDocument();
      expect(input).toHaveAttribute('type', 'email');
    });

    it('displays current email value', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'current@example.com',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]') as HTMLInputElement;
      expect(input.value).toBe('current@example.com');
    });

    it('calls onChange when user types', () => {
      const handleChange = vi.fn();
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: '',
            onChange: handleChange,
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]') as HTMLInputElement;
      fireEvent.change(input, { target: { value: 'new@email.com' } });

      expect(handleChange).toHaveBeenCalledTimes(1);
      expect(handleChange).toHaveBeenCalledWith('new@email.com');
    });

    it('calls onChange with updated value using userEvent', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: '',
            onChange: handleChange,
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]') as HTMLInputElement;
      await user.click(input);
      await user.type(input, 'a@b');

      // userEvent.type fires one onChange per character
      expect(handleChange).toHaveBeenCalled();
      expect(handleChange.mock.calls.length).toBeGreaterThan(0);
    });

    it('applies maxLength attribute when provided', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: '',
            maxLength: 50,
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toHaveAttribute('maxLength', '50');
    });

    it('does not apply maxLength when null', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: '',
            maxLength: null,
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).not.toHaveAttribute('maxLength');
    });

    it('has native email validation from type="email"', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: '',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      // type="email" enables native browser email validation
      expect(input).toHaveAttribute('type', 'email');
    });

    it('sets name attribute on the input', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: '',
            name: 'contact_email',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toHaveAttribute('name', 'contact_email');
    });

    it('sets autoComplete="email" on the input', () => {
      const { container } = render(
        <EmailField {...buildProps({ mode: 'edit', value: '' })} />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toHaveAttribute('autoComplete', 'email');
    });

    it('sets placeholder when provided', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: '',
            placeholder: 'Enter email address…',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toHaveAttribute('placeholder', 'Enter email address…');
    });

    it('generates a stable fieldId for accessibility', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: '',
            name: 'my_email',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      // Default fieldId is field-{name}
      expect(input).toHaveAttribute('id', 'field-my_email');
    });

    it('uses explicit fieldId when provided', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: '',
            fieldId: 'custom-id-123',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toHaveAttribute('id', 'custom-id-123');
    });
  });

  // ========================================================================
  // Email Validation
  // ========================================================================
  describe('email validation', () => {
    it('uses native type="email" browser validation', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: '',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]') as HTMLInputElement;
      // The type="email" attribute enables native browser email validation
      expect(input.type).toBe('email');
    });

    it('shows custom error when error prop provided', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'invalid',
            error: 'Please enter a valid email',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      // aria-invalid should be true when error prop is present
      expect(input).toHaveAttribute('aria-invalid', 'true');
      // Error styling applied — red border
      expect(input).toHaveClass('border-red-500');
    });

    it('sets aria-describedby to error element when error present', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            name: 'work_email',
            value: '',
            error: 'Email is required',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toHaveAttribute('aria-describedby', 'work_email-error');
    });

    it('sets aria-describedby to description when no error', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            name: 'work_email',
            value: 'test@example.com',
            description: 'Your work email address',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toHaveAttribute('aria-describedby', 'work_email-description');
    });

    it('sets required attribute when required=true', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: '',
            required: true,
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toHaveAttribute('required');
    });
  });

  // ========================================================================
  // Access Control
  // ========================================================================
  describe('access control', () => {
    it('renders normally with access="full"', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'full@example.com',
            access: 'full',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toBeInTheDocument();
      expect(input).not.toBeDisabled();
      expect(input).toBeVisible();
    });

    it('renders as readonly with access="readonly"', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'readonly@example.com',
            access: 'readonly',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toBeInTheDocument();
      // readonly access disables the input
      expect(input).toBeDisabled();
      expect(input).toHaveAttribute('readOnly');
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'secret@example.com',
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
        <EmailField
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
        <EmailField
          {...buildProps({
            name: 'restricted_email',
            access: 'forbidden',
          })}
        />,
      );

      const alert = screen.getByRole('alert');
      expect(alert).toHaveAttribute('data-field-name', 'restricted_email');
    });

    it('applies disabled styling class when disabled=true', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'disabled@example.com',
            disabled: true,
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toBeDisabled();
      // Disabled state applies muted background class
      expect(input).toHaveClass('bg-gray-50');
      expect(input).toHaveClass('cursor-not-allowed');
    });

    it('renders readonly input with muted styling', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'readonly@example.com',
            access: 'readonly',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toHaveClass('bg-gray-50');
      expect(input).toHaveClass('cursor-not-allowed');
      expect(input).toHaveClass('text-gray-500');
    });
  });

  // ========================================================================
  // Validation
  // ========================================================================
  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            name: 'email',
            value: '',
            error: 'Email is required',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toHaveAttribute('aria-invalid', 'true');
      // aria-describedby references the error element
      expect(input).toHaveAttribute('aria-describedby', 'email-error');
    });

    it('shows validation errors', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'invalid-email',
            error: 'Invalid email format',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      // Validation state indicated via aria-invalid
      expect(input).toHaveAttribute('aria-invalid', 'true');
      // Error red border class applied
      expect(input).toHaveClass('border-red-500');
      expect(input).toHaveClass('focus:ring-red-500');
    });

    it('applies error styling when error present', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'test@test.com',
            error: 'Duplicate email',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      // Error state → red border and ring
      expect(input).toHaveClass('border-red-500');
      expect(input).toHaveClass('focus:border-red-500');
      expect(input).toHaveClass('focus:ring-red-500');
    });

    it('applies normal CSS classes when no error', () => {
      const { container } = render(
        <EmailField
          {...buildProps({ mode: 'edit', value: 'ok@example.com' })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      // Non-error state classes: border-gray-300
      expect(input).toHaveClass('border-gray-300');
    });

    it('does not set aria-invalid when no error', () => {
      const { container } = render(
        <EmailField {...buildProps({ mode: 'edit', value: '' })} />,
      );

      const input = container.querySelector('input[type="email"]');
      // aria-invalid should be 'false' (no error)
      expect(input).toHaveAttribute('aria-invalid', 'false');
    });
  });

  // ========================================================================
  // Null / Empty Handling
  // ========================================================================
  describe('null/empty handling', () => {
    it('handles null value in edit mode', () => {
      const { container } = render(
        <EmailField
          {...buildProps({ mode: 'edit', value: null })}
        />,
      );

      const input = container.querySelector('input[type="email"]') as HTMLInputElement;
      // Null value is coerced to empty string
      expect(input.value).toBe('');
    });

    it('handles undefined value', () => {
      // TypeScript would flag this, but test runtime resilience
      render(
        <EmailField
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
        <EmailField
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
        <EmailField
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

      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]') as HTMLInputElement;
      fireEvent.change(input, { target: { value: 'new@email.com' } });

      expect(handleChange).toHaveBeenCalledWith('new@email.com');
    });
  });

  // ========================================================================
  // Visibility
  // ========================================================================
  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'visible@example.com',
            isVisible: true,
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toBeInTheDocument();
      expect(input).toBeVisible();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'hidden@example.com',
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
        <EmailField
          {...buildProps({
            mode: 'display',
            value: 'invisible@example.com',
            isVisible: false,
          })}
        />,
      );

      expect(container.innerHTML).toBe('');
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('defaults isVisible to true', () => {
      // Render without explicit isVisible — defaults to true
      const { container } = render(
        <EmailField
          {...buildProps({
            mode: 'edit',
            value: 'default@example.com',
          })}
        />,
      );

      const input = container.querySelector('input[type="email"]');
      expect(input).toBeInTheDocument();
    });
  });
});
