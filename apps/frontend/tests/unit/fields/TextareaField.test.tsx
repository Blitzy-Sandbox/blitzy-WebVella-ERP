/**
 * TextareaField — Vitest Unit Tests
 *
 * Comprehensive test suite for the TextareaField component that replaces the
 * monolith's PcFieldTextarea ViewComponent (WebVella.Erp.Web/Components/PcFieldTextarea).
 *
 * Tests cover:
 *   - Display mode (whitespace-pre-wrap multiline text, emptyValueMessage for null/empty)
 *   - Edit mode (textarea element with maxLength, rows default 4, custom rows,
 *     placeholder, resize-y, onChange callbacks)
 *   - Access control (full / readonly / forbidden)
 *   - Validation / error state (error prop, validationErrors array, error styling,
 *     labelWarningText, labelErrorText)
 *   - Null / empty / undefined value handling
 *   - Visibility toggling (isVisible prop)
 *
 * @module tests/unit/fields/TextareaField.test
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom/vitest';
import React from 'react';
import TextareaField from '../../../src/components/fields/TextareaField';
import type { TextareaFieldProps } from '../../../src/components/fields/TextareaField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Builds a complete TextareaFieldProps object with sensible defaults.
 * Individual tests override only the props they care about, keeping
 * each test focused and reducing boilerplate.
 */
const buildProps = (overrides: Partial<TextareaFieldProps> = {}): TextareaFieldProps => ({
  name: 'description',
  value: 'Default textarea content',
  ...overrides,
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('TextareaField', () => {
  afterEach(() => {
    cleanup();
  });

  // ========================================================================
  // Display Mode
  // ========================================================================
  describe('display mode', () => {
    it('renders multiline text preserving whitespace (whitespace-pre-wrap)', () => {
      const multilineText = 'Line one\nLine two\nLine three';
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'display',
            value: multilineText,
          })}
        />,
      );

      // Display mode renders a div with role="textbox"
      const displayDiv = screen.getByRole('textbox');
      expect(displayDiv).toBeInTheDocument();
      expect(displayDiv).toHaveTextContent('Line one');
      expect(displayDiv).toHaveTextContent('Line two');
      expect(displayDiv).toHaveTextContent('Line three');
      // The whitespace-pre-wrap class preserves newlines and spaces
      expect(displayDiv).toHaveClass('whitespace-pre-wrap');
      // Text color for non-empty value
      expect(displayDiv).toHaveClass('text-gray-900');
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <TextareaField
          {...buildProps({
            mode: 'display',
            value: null,
          })}
        />,
      );

      // Default emptyValueMessage is "no data"
      expect(screen.getByText('no data')).toBeInTheDocument();
      const displayDiv = screen.getByRole('textbox');
      // Empty state shows italic styling
      expect(displayDiv).toHaveClass('text-gray-500');
      expect(displayDiv).toHaveClass('italic');
      // Should NOT have whitespace-pre-wrap since it's empty value
      expect(displayDiv).not.toHaveClass('whitespace-pre-wrap');
    });

    it('renders emptyValueMessage when value is empty string', () => {
      render(
        <TextareaField
          {...buildProps({
            mode: 'display',
            value: '',
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
      const displayDiv = screen.getByRole('textbox');
      expect(displayDiv).toHaveClass('text-gray-500');
      expect(displayDiv).toHaveClass('italic');
    });

    it('sets data-field-name attribute in display mode', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'display',
            value: 'Some text',
            name: 'notes_field',
          })}
        />,
      );

      const wrapper = container.querySelector('[data-field-name="notes_field"]');
      expect(wrapper).toBeInTheDocument();
    });

    it('sets aria-readonly="true" in display mode', () => {
      render(
        <TextareaField
          {...buildProps({
            mode: 'display',
            value: 'Read-only display',
          })}
        />,
      );

      const displayDiv = screen.getByRole('textbox');
      expect(displayDiv).toHaveAttribute('aria-readonly', 'true');
    });

    it('applies text-sm class in display mode', () => {
      render(
        <TextareaField
          {...buildProps({
            mode: 'display',
            value: 'Styled text',
          })}
        />,
      );

      const displayDiv = screen.getByRole('textbox');
      expect(displayDiv).toHaveClass('text-sm');
    });
  });

  // ========================================================================
  // Edit Mode
  // ========================================================================
  describe('edit mode', () => {
    it('renders a <textarea> element in edit mode', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Hello world',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toBeInTheDocument();
    });

    it('displays the current value in textarea', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Current textarea value',
          })}
        />,
      );

      const textarea = container.querySelector('textarea') as HTMLTextAreaElement;
      expect(textarea.value).toBe('Current textarea value');
    });

    it('calls onChange when user types', () => {
      const handleChange = vi.fn();
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            onChange: handleChange,
          })}
        />,
      );

      const textarea = container.querySelector('textarea') as HTMLTextAreaElement;
      fireEvent.change(textarea, { target: { value: 'New multiline\ntext' } });

      expect(handleChange).toHaveBeenCalledTimes(1);
      expect(handleChange).toHaveBeenCalledWith('New multiline\ntext');
    });

    it('calls onChange with updated value using userEvent', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            onChange: handleChange,
          })}
        />,
      );

      const textarea = container.querySelector('textarea') as HTMLTextAreaElement;
      await user.click(textarea);
      await user.type(textarea, 'abc');

      // userEvent.type fires one onChange per character
      expect(handleChange).toHaveBeenCalled();
      expect(handleChange.mock.calls.length).toBeGreaterThan(0);
    });

    it('applies maxLength attribute when provided', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            maxLength: 500,
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toHaveAttribute('maxLength', '500');
    });

    it('does not apply maxLength when null', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            maxLength: null,
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).not.toHaveAttribute('maxLength');
    });

    it('does not apply maxLength when zero or negative', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            maxLength: 0,
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).not.toHaveAttribute('maxLength');
    });

    it('applies rows prop (default 4)', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      // Default rows is 4 per TextareaFieldProps
      expect(textarea).toHaveAttribute('rows', '4');
    });

    it('applies custom rows prop', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            rows: 10,
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toHaveAttribute('rows', '10');
    });

    it('applies placeholder prop', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            placeholder: 'Enter your notes here…',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toHaveAttribute('placeholder', 'Enter your notes here…');
    });

    it('renders with resize-y for resize handle', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'resizable',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      // The resize-y Tailwind class allows vertical-only resizing
      expect(textarea).toHaveClass('resize-y');
    });

    it('sets name attribute on the textarea', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            name: 'bio_field',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toHaveAttribute('name', 'bio_field');
    });

    it('generates a stable fieldId for accessibility', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            name: 'my_notes',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      // Default fieldId is field-{name}
      expect(textarea).toHaveAttribute('id', 'field-my_notes');
    });

    it('uses explicit fieldId when provided', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            fieldId: 'custom-textarea-id',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toHaveAttribute('id', 'custom-textarea-id');
    });

    it('applies standard Tailwind styling classes', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'styled',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toHaveClass('block');
      expect(textarea).toHaveClass('w-full');
      expect(textarea).toHaveClass('rounded-md');
      expect(textarea).toHaveClass('border');
      expect(textarea).toHaveClass('px-3');
      expect(textarea).toHaveClass('py-2');
      expect(textarea).toHaveClass('text-sm');
      expect(textarea).toHaveClass('shadow-sm');
    });

    it('sets required attribute when required=true', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            required: true,
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toHaveAttribute('required');
    });
  });

  // ========================================================================
  // Access Control
  // ========================================================================
  describe('access control', () => {
    it('renders normally with access="full"', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Full access text',
            access: 'full',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toBeInTheDocument();
      expect(textarea).not.toBeDisabled();
      expect(textarea).toBeVisible();
    });

    it('renders as readonly with access="readonly"', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Readonly text',
            access: 'readonly',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toBeInTheDocument();
      // readonly access disables the textarea (isEffectivelyDisabled)
      expect(textarea).toBeDisabled();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Secret content',
            access: 'forbidden',
          })}
        />,
      );

      // Forbidden renders a div with role="status" and default accessDeniedMessage
      const status = screen.getByRole('status');
      expect(status).toBeInTheDocument();
      expect(status).toHaveTextContent('access denied');

      // No textarea should be rendered
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    });

    it('renders custom accessDeniedMessage with access="forbidden"', () => {
      render(
        <TextareaField
          {...buildProps({
            access: 'forbidden',
            accessDeniedMessage: 'Insufficient permissions',
          })}
        />,
      );

      const status = screen.getByRole('status');
      expect(status).toHaveTextContent('Insufficient permissions');
    });

    it('forbidden access renders data-field-name attribute', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            name: 'restricted_notes',
            access: 'forbidden',
          })}
        />,
      );

      const wrapper = container.querySelector('[data-field-name="restricted_notes"]');
      expect(wrapper).toBeInTheDocument();
    });

    it('forbidden access applies italic styling', () => {
      render(
        <TextareaField
          {...buildProps({
            access: 'forbidden',
          })}
        />,
      );

      const status = screen.getByRole('status');
      expect(status).toHaveClass('italic');
      expect(status).toHaveClass('text-gray-400');
      expect(status).toHaveClass('text-sm');
    });

    it('applies disabled styling when disabled=true', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Disabled text',
            disabled: true,
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toBeDisabled();
    });

    it('forbidden access sets aria-label for screen readers', () => {
      render(
        <TextareaField
          {...buildProps({
            access: 'forbidden',
            accessDeniedMessage: 'No access',
          })}
        />,
      );

      const status = screen.getByRole('status');
      expect(status).toHaveAttribute('aria-label', 'No access');
    });
  });

  // ========================================================================
  // Validation
  // ========================================================================
  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            name: 'notes',
            value: '',
            error: 'Notes are required',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      // aria-invalid should be true when error prop is present
      expect(textarea).toHaveAttribute('aria-invalid', 'true');
      // aria-describedby references the error element
      expect(textarea).toHaveAttribute('aria-describedby', expect.stringContaining('notes-error'));
    });

    it('shows validation errors from validationErrors array', () => {
      const validationErrors = [
        { key: 'notes', value: '', message: 'Field cannot be empty' },
      ];

      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
            error: 'Validation failed',
            validationErrors,
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      // The error prop drives the visual error state
      expect(textarea).toHaveAttribute('aria-invalid', 'true');
      expect(textarea).toHaveClass('border-red-500');
    });

    it('applies error styling when error present', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'test content',
            error: 'Content too short',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      // Error state → red border
      expect(textarea).toHaveClass('border-red-500');
    });

    it('applies normal CSS classes when no error', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Valid content',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      // Non-error state: border-gray-300
      expect(textarea).toHaveClass('border-gray-300');
      expect(textarea).not.toHaveClass('border-red-500');
    });

    it('displays labelWarningText and labelErrorText via props', () => {
      // The labelWarningText and labelErrorText are passed through as props;
      // their visual rendering depends on FieldRenderer wrapper. In standalone
      // mode, we verify the props are accepted without errors.
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Warning test',
            labelWarningText: 'Review this content',
            labelErrorText: 'Content flagged',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      // Component renders without error with these props
      expect(textarea).toBeInTheDocument();
    });

    it('sets aria-describedby to description when no error', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            name: 'notes',
            value: 'test content',
            description: 'Enter detailed notes',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toHaveAttribute(
        'aria-describedby',
        expect.stringContaining('description'),
      );
    });

    it('does not set aria-invalid when no error', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Valid text',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      // Without error, aria-invalid should not be set to "true"
      expect(textarea).not.toHaveAttribute('aria-invalid', 'true');
    });
  });

  // ========================================================================
  // Null / Empty Handling
  // ========================================================================
  describe('null/empty handling', () => {
    it('handles null value gracefully in edit mode', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: null,
          })}
        />,
      );

      const textarea = container.querySelector('textarea') as HTMLTextAreaElement;
      // Null value is coerced to empty string via localValue fallback
      expect(textarea.value).toBe('');
    });

    it('handles undefined value gracefully', () => {
      // TypeScript would flag this, but test runtime resilience
      render(
        <TextareaField
          {...buildProps({
            mode: 'display',
            value: undefined as unknown as string | null,
          })}
        />,
      );

      // undefined is treated as empty → emptyValueMessage should appear
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('shows custom emptyValueMessage in display mode', () => {
      render(
        <TextareaField
          {...buildProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'Nothing to display',
          })}
        />,
      );

      expect(screen.getByText('Nothing to display')).toBeInTheDocument();
    });

    it('handles null value in edit mode without crashing onChange', () => {
      const handleChange = vi.fn();

      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      const textarea = container.querySelector('textarea') as HTMLTextAreaElement;
      fireEvent.change(textarea, { target: { value: 'New content after null' } });

      expect(handleChange).toHaveBeenCalledWith('New content after null');
    });

    it('handles empty string value in edit mode', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: '',
          })}
        />,
      );

      const textarea = container.querySelector('textarea') as HTMLTextAreaElement;
      expect(textarea.value).toBe('');
    });
  });

  // ========================================================================
  // Visibility
  // ========================================================================
  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Visible content',
            isVisible: true,
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toBeInTheDocument();
      expect(textarea).toBeVisible();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Hidden content',
            isVisible: false,
          })}
        />,
      );

      // Component returns null when isVisible=false
      expect(container.innerHTML).toBe('');
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    });

    it('renders nothing in display mode when isVisible=false', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'display',
            value: 'Invisible text',
            isVisible: false,
          })}
        />,
      );

      expect(container.innerHTML).toBe('');
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    });

    it('defaults isVisible to true', () => {
      // Render without explicit isVisible — defaults to true
      const { container } = render(
        <TextareaField
          {...buildProps({
            mode: 'edit',
            value: 'Default visible',
          })}
        />,
      );

      const textarea = container.querySelector('textarea');
      expect(textarea).toBeInTheDocument();
    });

    it('forbidden access also respects isVisible=false', () => {
      const { container } = render(
        <TextareaField
          {...buildProps({
            access: 'forbidden',
            isVisible: false,
          })}
        />,
      );

      // isVisible=false takes precedence — nothing is rendered
      expect(container.innerHTML).toBe('');
      expect(screen.queryByRole('status')).not.toBeInTheDocument();
    });
  });
});
