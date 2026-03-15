/**
 * Vitest Component Tests for `<CodeField />`
 *
 * Validates the React CodeField component
 * (`apps/frontend/src/components/fields/CodeField.tsx`) that replaces
 * the monolith's `PcFieldCode` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldCode/PcFieldCode.cs`).
 *
 * The monolith's PcFieldCodeOptions extend PcFieldBaseOptions with:
 *   - Height (default "120px"): CSS height of the code editor
 *   - Language (default "razor" → React maps to "javascript"): Syntax language
 *   - Theme (default "cobalt" → React maps to "dark"): Editor colour theme
 *
 * Test coverage spans:
 *   - Display mode: <pre><code> block rendering, monospace font, theme colours,
 *     horizontal overflow with scroll, empty-value message
 *   - Edit mode: monospace textarea, height CSS, onChange callback,
 *     Tab key insertion (indentation), current code value display
 *   - Height configuration: default 120px, custom values
 *   - Theme support: dark (bg-gray-900 text-green-400), light (bg-white text-gray-900),
 *     default dark
 *   - Language prop: data attribute, default "javascript"
 *   - Access control: full / readonly / forbidden
 *   - Validation: error messages, validation error display
 *   - Null/empty handling: null and empty-string values
 *   - Visibility: isVisible true/false
 *
 * @see apps/frontend/src/components/fields/CodeField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldCode/PcFieldCode.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import CodeField from '../../../src/components/fields/CodeField';
import type { CodeFieldProps } from '../../../src/components/fields/CodeField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default CodeFieldProps for consistent test setup.
 * Mirrors the PcFieldCodeOptions defaults from PcFieldCode.cs:
 *   - Height = "120px"
 *   - Language = "javascript" (was "razor" in monolith, mapped per AAP)
 *   - Theme = "dark" (was "cobalt" in monolith, mapped per AAP)
 */
function createDefaultProps(
  overrides: Partial<CodeFieldProps> = {},
): CodeFieldProps {
  return {
    name: 'code_field',
    value: null,
    ...overrides,
  };
}

/** Multi-line code fixture simulating a real code snippet. */
const SAMPLE_CODE = 'function hello() {\n  return "world";\n}';

/** Simple single-line code fixture. */
const SIMPLE_CODE = 'const x = 1;';

/** Multi-line code with several lines for line-number testing. */
const MULTI_LINE_CODE = 'line 1\nline 2\nline 3\nline 4\nline 5';

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('CodeField', () => {
  afterEach(() => {
    cleanup();
  });

  // =========================================================================
  // Display Mode
  // =========================================================================

  describe('display mode', () => {
    it('renders code in <pre><code> block with monospace font', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SAMPLE_CODE,
            mode: 'display',
          })}
        />,
      );

      // The component renders a <pre> with role="region"
      const region = screen.getByRole('region');
      expect(region).toBeInTheDocument();
      expect(region.tagName).toBe('PRE');
      expect(region).toHaveClass('font-mono');

      // Inside the <pre> there should be a <code> element containing the code
      const codeElement = region.querySelector('code');
      expect(codeElement).toBeInTheDocument();
      expect(codeElement).toHaveTextContent('function hello()');
      expect(codeElement).toHaveTextContent('return "world"');
    });

    it('applies background color based on theme', () => {
      // Dark theme
      const { rerender } = render(
        <CodeField
          {...createDefaultProps({
            value: SAMPLE_CODE,
            mode: 'display',
            theme: 'dark',
          })}
        />,
      );
      let region = screen.getByRole('region');
      expect(region).toHaveClass('bg-gray-900');
      expect(region).toHaveClass('text-green-400');

      // Light theme
      rerender(
        <CodeField
          {...createDefaultProps({
            value: SAMPLE_CODE,
            mode: 'display',
            theme: 'light',
          })}
        />,
      );
      region = screen.getByRole('region');
      expect(region).toHaveClass('bg-gray-50');
      expect(region).toHaveClass('text-gray-900');
    });

    it('handles horizontal overflow with scroll', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SAMPLE_CODE,
            mode: 'display',
          })}
        />,
      );

      const region = screen.getByRole('region');
      expect(region).toHaveClass('overflow-auto');
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: null,
            mode: 'display',
          })}
        />,
      );

      // When value is null the component shows the empty-value message
      expect(screen.getByText('no data')).toBeInTheDocument();
      // No <pre> should be rendered
      expect(screen.queryByRole('region')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Edit Mode
  // =========================================================================

  describe('edit mode', () => {
    it('renders a textarea with monospace font (font-mono)', () => {
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toBeInTheDocument();
      expect(textarea.tagName).toBe('TEXTAREA');

      // The editor container wrapping the textarea has the font-mono class
      const editorContainer = container.querySelector('.font-mono');
      expect(editorContainer).toBeInTheDocument();
      // Verify the textarea is a descendant of the font-mono container
      expect(editorContainer!.contains(textarea)).toBe(true);
    });

    it('applies height prop as CSS style (default "120px")', () => {
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
          })}
        />,
      );

      const editorContainer = container.querySelector('.font-mono');
      expect(editorContainer).toHaveStyle({ height: '120px' });
    });

    it('calls onChange when user edits code', () => {
      const handleChange = vi.fn();
      render(
        <CodeField
          {...createDefaultProps({
            value: '',
            mode: 'edit',
            onChange: handleChange,
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      fireEvent.change(textarea, { target: { value: 'const y = 2;' } });
      expect(handleChange).toHaveBeenCalledTimes(1);
      expect(handleChange).toHaveBeenCalledWith('const y = 2;');
    });

    it('handles Tab key insertion (inserts tab instead of focus change)', () => {
      const handleChange = vi.fn();
      render(
        <CodeField
          {...createDefaultProps({
            value: 'hello',
            mode: 'edit',
            onChange: handleChange,
          })}
        />,
      );

      const textarea = screen.getByRole('textbox') as HTMLTextAreaElement;

      // Position cursor at the beginning of the text
      textarea.selectionStart = 0;
      textarea.selectionEnd = 0;

      // Fire Tab keydown — the component intercepts Tab and inserts
      // TAB_CHARACTER ('  ' — 2 spaces) at the cursor position
      fireEvent.keyDown(textarea, { key: 'Tab' });

      expect(handleChange).toHaveBeenCalledTimes(1);
      // Two spaces should be inserted at position 0
      expect(handleChange).toHaveBeenCalledWith('  hello');
    });

    it('displays current code value', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SAMPLE_CODE,
            mode: 'edit',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toHaveValue(SAMPLE_CODE);
    });
  });

  // =========================================================================
  // Height Configuration
  // =========================================================================

  describe('height configuration', () => {
    it('applies default height "120px" when not specified', () => {
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
          })}
        />,
      );

      const editorContainer = container.querySelector('.font-mono');
      expect(editorContainer).toBeInTheDocument();
      expect(editorContainer).toHaveStyle({ height: '120px' });
    });

    it('applies custom height when provided', () => {
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            height: '400px',
          })}
        />,
      );

      const editorContainer = container.querySelector('.font-mono');
      expect(editorContainer).toBeInTheDocument();
      expect(editorContainer).toHaveStyle({ height: '400px' });
    });
  });

  // =========================================================================
  // Theme Support
  // =========================================================================

  describe('theme support', () => {
    it('applies dark theme styling (bg-gray-900 text-green-400)', () => {
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            theme: 'dark',
          })}
        />,
      );

      const editorContainer = container.querySelector('.font-mono');
      expect(editorContainer).toBeInTheDocument();
      expect(editorContainer).toHaveClass('bg-gray-900');
      expect(editorContainer).toHaveClass('text-green-400');
    });

    it('applies light theme styling (bg-white text-gray-900)', () => {
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            theme: 'light',
          })}
        />,
      );

      const editorContainer = container.querySelector('.font-mono');
      expect(editorContainer).toBeInTheDocument();
      expect(editorContainer).toHaveClass('bg-white');
      expect(editorContainer).toHaveClass('text-gray-900');
    });

    it('defaults to "dark" theme', () => {
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            // No theme prop — should default to "dark"
          })}
        />,
      );

      const editorContainer = container.querySelector('.font-mono');
      expect(editorContainer).toBeInTheDocument();
      expect(editorContainer).toHaveClass('bg-gray-900');
      expect(editorContainer).toHaveClass('text-green-400');

      // Also verify via the data-theme attribute on textarea
      const textarea = screen.getByRole('textbox');
      expect(textarea).toHaveAttribute('data-theme', 'dark');
    });
  });

  // =========================================================================
  // Language Prop
  // =========================================================================

  describe('language prop', () => {
    it('accepts language prop (e.g., "javascript")', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            language: 'javascript',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toHaveAttribute('data-language', 'javascript');
    });

    it('defaults to "javascript" language', () => {
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            // No language prop — should default to "javascript"
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toHaveAttribute('data-language', 'javascript');

      // Also verify the parent wrapper data attribute
      const wrapper = container.querySelector('[data-field-language]');
      expect(wrapper).toHaveAttribute('data-field-language', 'javascript');
    });
  });

  // =========================================================================
  // Access Control
  // =========================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toBeInTheDocument();
      expect(textarea).not.toBeDisabled();
    });

    it('renders as readonly with access="readonly"', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            access: 'readonly',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toBeInTheDocument();
      // The component sets effectiveDisabled = true when access="readonly",
      // which maps to the textarea disabled attribute
      expect(textarea).toBeDisabled();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            access: 'forbidden',
          })}
        />,
      );

      // No textarea should be rendered when access is forbidden
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();

      // The component renders a status div with the access-denied message
      const statusEl = screen.getByRole('status');
      expect(statusEl).toBeInTheDocument();
      expect(screen.getByText('access denied')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Validation
  // =========================================================================

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            error: 'Code is required',
          })}
        />,
      );

      const alert = screen.getByRole('alert');
      expect(alert).toBeInTheDocument();
      expect(alert).toHaveTextContent('Code is required');
    });

    it('shows validation errors', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            error: 'Invalid syntax detected',
          })}
        />,
      );

      // The error text should be visible
      expect(screen.getByText('Invalid syntax detected')).toBeInTheDocument();

      // The error border class should be applied to the editor container
      const textarea = screen.getByRole('textbox');
      expect(textarea).toHaveAttribute('aria-invalid', 'true');
    });
  });

  // =========================================================================
  // Null / Empty Handling
  // =========================================================================

  describe('null/empty handling', () => {
    it('handles null value', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: null,
            mode: 'display',
          })}
        />,
      );

      // Display mode with null value shows the empty-value message
      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('region')).not.toBeInTheDocument();
    });

    it('handles empty string value', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: '',
            mode: 'display',
          })}
        />,
      );

      // Empty string is falsy, so the empty-value message is shown
      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('region')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Visibility
  // =========================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: SAMPLE_CODE,
            mode: 'display',
            isVisible: true,
          })}
        />,
      );

      // Component should render content
      expect(container.firstChild).not.toBeNull();
      expect(screen.getByRole('region')).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: SAMPLE_CODE,
            mode: 'display',
            isVisible: false,
          })}
        />,
      );

      // Component returns null when not visible
      expect(container.firstChild).toBeNull();
    });
  });

  // =========================================================================
  // Additional Integration Behaviour
  // =========================================================================

  describe('additional behaviour', () => {
    it('renders line numbers in edit mode', () => {
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: MULTI_LINE_CODE,
            mode: 'edit',
          })}
        />,
      );

      // The component renders line numbers in a gutter div (aria-hidden)
      const gutter = container.querySelector('[aria-hidden="true"]');
      expect(gutter).toBeInTheDocument();
      // 5-line code should have line numbers 1-5
      const lineSpans = gutter!.querySelectorAll('span');
      expect(lineSpans).toHaveLength(5);
      expect(lineSpans[0]).toHaveTextContent('1');
      expect(lineSpans[4]).toHaveTextContent('5');
    });

    it('does not call onChange when readonly and Tab is pressed', () => {
      const handleChange = vi.fn();
      render(
        <CodeField
          {...createDefaultProps({
            value: 'hello',
            mode: 'edit',
            access: 'readonly',
            onChange: handleChange,
          })}
        />,
      );

      const textarea = screen.getByRole('textbox') as HTMLTextAreaElement;
      fireEvent.keyDown(textarea, { key: 'Tab' });

      // onChange should not be called because the field is readonly
      expect(handleChange).not.toHaveBeenCalled();
    });

    it('applies custom accessDeniedMessage', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            access: 'forbidden',
            accessDeniedMessage: 'No permission',
          })}
        />,
      );

      expect(screen.getByText('No permission')).toBeInTheDocument();
    });

    it('applies custom emptyValueMessage', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: null,
            mode: 'display',
            emptyValueMessage: 'Nothing here',
          })}
        />,
      );

      expect(screen.getByText('Nothing here')).toBeInTheDocument();
    });

    it('sets the textarea name attribute to the field name', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            name: 'my_code',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toHaveAttribute('name', 'my_code');
      expect(textarea).toHaveAttribute('id', 'my_code');
    });

    it('inserts tab at cursor position in the middle of text', () => {
      const handleChange = vi.fn();
      render(
        <CodeField
          {...createDefaultProps({
            value: 'abcdef',
            mode: 'edit',
            onChange: handleChange,
          })}
        />,
      );

      const textarea = screen.getByRole('textbox') as HTMLTextAreaElement;
      // Position cursor at position 3 (between 'c' and 'd')
      textarea.selectionStart = 3;
      textarea.selectionEnd = 3;

      fireEvent.keyDown(textarea, { key: 'Tab' });

      // Two spaces inserted at position 3: 'abc' + '  ' + 'def'
      expect(handleChange).toHaveBeenCalledWith('abc  def');
    });

    it('renders description text in edit mode', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            description: 'Enter JavaScript code here',
          })}
        />,
      );

      expect(screen.getByText('Enter JavaScript code here')).toBeInTheDocument();
    });

    it('renders description text in display mode', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SAMPLE_CODE,
            mode: 'display',
            description: 'Generated code output',
          })}
        />,
      );

      expect(screen.getByText('Generated code output')).toBeInTheDocument();
    });

    it('applies height style in display mode on <pre> element', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SAMPLE_CODE,
            mode: 'display',
            height: '250px',
          })}
        />,
      );

      const region = screen.getByRole('region');
      expect(region).toHaveStyle({ height: '250px' });
    });

    it('supports user clicking into textarea for focus', async () => {
      const user = userEvent.setup();
      const { container } = render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      await user.click(textarea);

      // After clicking, the textarea should be the active element
      expect(document.activeElement).toBe(textarea);
    });

    it('sets required and aria-required on textarea when required=true', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: '',
            mode: 'edit',
            required: true,
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toHaveAttribute('aria-required', 'true');
      expect(textarea).toBeRequired();
    });

    it('handles non-Tab key presses normally', () => {
      const handleChange = vi.fn();
      render(
        <CodeField
          {...createDefaultProps({
            value: 'test',
            mode: 'edit',
            onChange: handleChange,
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      // Non-Tab keys should not trigger the tab-insertion logic
      fireEvent.keyDown(textarea, { key: 'Enter' });
      expect(handleChange).not.toHaveBeenCalled();
    });

    it('sets aria-label on the textarea', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            name: 'my_code',
            label: 'Source Code',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toHaveAttribute('aria-label', 'Source Code');
    });

    it('falls back to name for aria-label when label is not provided', () => {
      render(
        <CodeField
          {...createDefaultProps({
            value: SIMPLE_CODE,
            mode: 'edit',
            name: 'my_code',
          })}
        />,
      );

      const textarea = screen.getByRole('textbox');
      expect(textarea).toHaveAttribute('aria-label', 'my_code');
    });
  });
});
