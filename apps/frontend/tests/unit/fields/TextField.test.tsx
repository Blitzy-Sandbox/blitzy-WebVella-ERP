import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom/vitest';
import TextField from '../../../src/components/fields/TextField';
import type { TextFieldProps } from '../../../src/components/fields/TextField';
import type {
  BaseFieldProps,
  WvFieldAccess,
  FieldMode,
  WvLabelRenderMode,
} from '../../../src/components/fields/FieldRenderer';

/**
 * Vitest unit tests for the TextField component.
 *
 * Replaces the monolith's PcFieldText ViewComponent
 * (WebVella.Erp.Web/Components/PcFieldText/).
 *
 * Covers: display mode rendering, edit mode input behavior, access control
 * (full / readonly / forbidden), validation & error signalling, null/empty
 * value handling, label prop acceptance, visibility toggling, and link
 * feature (href prop).
 *
 * Source references:
 *   - WebVella.Erp.Web/Components/PcFieldText/PcFieldText.cs
 *   - WebVella.Erp.Web/Components/PcFieldText/Display.cshtml
 *   - WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Builds a complete TextFieldProps object with sensible defaults.
 * Individual tests override specific props to isolate behaviour under test.
 *
 * TextFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> and adds
 * value (string | null), onChange, maxLength, placeholder, href.
 */
const buildProps = (overrides: Partial<TextFieldProps> = {}): TextFieldProps => ({
  name: 'test-field',
  value: 'test value',
  ...overrides,
});

// ---------------------------------------------------------------------------
// Test Suite
// ---------------------------------------------------------------------------

describe('TextField', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  // ────────────────────────────────────────────────────────────────────────
  // Display Mode Tests
  // ────────────────────────────────────────────────────────────────────────
  describe('display mode', () => {
    const mode: FieldMode = 'display';

    it('renders value as plain text in display mode', () => {
      render(<TextField {...buildProps({ mode, value: 'Hello World' })} />);

      const element = screen.getByText('Hello World');
      expect(element).toBeInTheDocument();
      expect(element).toHaveTextContent('Hello World');
    });

    it('renders emptyValueMessage when value is null', () => {
      render(<TextField {...buildProps({ mode, value: null })} />);

      const msg = screen.getByText('no data');
      expect(msg).toBeInTheDocument();
      expect(msg).toHaveClass('text-gray-500');
      expect(msg).toHaveClass('italic');
    });

    it('renders emptyValueMessage when value is empty string', () => {
      render(<TextField {...buildProps({ mode, value: '' })} />);

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders value as link when href is provided', () => {
      render(
        <TextField
          {...buildProps({
            mode,
            value: 'Click me',
            href: 'https://example.com',
          })}
        />,
      );

      const link = screen.getByRole('link', { name: 'Click me' });
      expect(link).toBeInTheDocument();
      expect(link).toHaveAttribute('href', 'https://example.com');
      expect(link).toHaveClass('text-blue-600');
    });

    it('applies correct display styling classes', () => {
      render(
        <TextField {...buildProps({ mode, value: 'Styled text' })} />,
      );

      const el = screen.getByText('Styled text');
      expect(el).toHaveClass('text-sm');
      expect(el).toHaveClass('text-gray-900');
    });
  });

  // ────────────────────────────────────────────────────────────────────────
  // Edit Mode Tests
  // ────────────────────────────────────────────────────────────────────────
  describe('edit mode', () => {
    const mode: FieldMode = 'edit';

    it('renders an input[type="text"] in edit mode', () => {
      render(<TextField {...buildProps({ mode })} />);

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('type', 'text');
    });

    it('displays the current value in the input', () => {
      const onChange = vi.fn();
      render(
        <TextField
          {...buildProps({ mode, value: 'current-val', onChange })}
        />,
      );

      expect(screen.getByRole('textbox')).toHaveValue('current-val');
    });

    it('calls onChange when user types', async () => {
      const user = userEvent.setup();
      const onChange = vi.fn();
      render(
        <TextField {...buildProps({ mode, value: '', onChange })} />,
      );

      const input = screen.getByRole('textbox');
      await user.type(input, 'a');

      await waitFor(() => {
        expect(onChange).toHaveBeenCalled();
      });
      expect(onChange).toHaveBeenCalledWith('a');
    });

    it('handles onChange via fireEvent.change', () => {
      const onChange = vi.fn();
      render(
        <TextField {...buildProps({ mode, value: '', onChange })} />,
      );

      const input = screen.getByRole('textbox');
      fireEvent.change(input, { target: { value: 'new value' } });

      expect(onChange).toHaveBeenCalledWith('new value');
    });

    it('clears input value via user interaction', async () => {
      const user = userEvent.setup();
      const onChange = vi.fn();
      render(
        <TextField
          {...buildProps({ mode, value: 'hello', onChange })}
        />,
      );

      const input = screen.getByRole('textbox');
      await user.clear(input);

      expect(onChange).toHaveBeenCalledWith('');
    });

    it('focuses input on click', async () => {
      const user = userEvent.setup();
      render(<TextField {...buildProps({ mode })} />);

      const input = screen.getByRole('textbox');
      await user.click(input);

      expect(document.activeElement).toBe(input);
    });

    it('applies placeholder prop to input', () => {
      render(
        <TextField
          {...buildProps({ mode, placeholder: 'Enter text...' })}
        />,
      );

      expect(screen.getByRole('textbox')).toHaveAttribute(
        'placeholder',
        'Enter text...',
      );
    });

    it('applies maxLength attribute when provided', () => {
      render(
        <TextField {...buildProps({ mode, maxLength: 50 })} />,
      );

      expect(screen.getByRole('textbox')).toHaveAttribute('maxLength', '50');
    });

    it('does not apply maxLength when null/undefined', () => {
      render(
        <TextField {...buildProps({ mode, maxLength: null })} />,
      );

      expect(screen.getByRole('textbox')).not.toHaveAttribute('maxLength');
    });

    it('sets required attribute when required=true', () => {
      render(
        <TextField {...buildProps({ mode, required: true })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeRequired();
      expect(input).toHaveAttribute('aria-required', 'true');
    });

    it('renders with proper Tailwind CSS input styling', () => {
      render(<TextField {...buildProps({ mode })} />);

      const input = screen.getByRole('textbox');
      expect(input).toHaveClass('w-full');
      expect(input).toHaveClass('rounded-md');
      expect(input).toHaveClass('border');
      expect(input).toHaveClass('border-gray-300');
      expect(input).toHaveClass('px-3');
      expect(input).toHaveClass('py-2');
      expect(input).toHaveClass('text-sm');
      expect(input).toHaveClass('shadow-sm');
    });

    it('renders as disabled when disabled=true', () => {
      render(
        <TextField {...buildProps({ mode, disabled: true })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeDisabled();
      expect(input).toHaveClass('bg-gray-100');
      expect(input).toHaveClass('text-gray-500');
      expect(input).toHaveClass('cursor-not-allowed');
    });
  });

  // --------------------------------------------------------------------------
  // Access Control Tests (WvFieldAccess)
  // --------------------------------------------------------------------------

  describe('access control', () => {
    const mode: FieldMode = 'edit';

    it('renders normally with access="full"', () => {
      const fullAccess: WvFieldAccess = 'full';
      render(
        <TextField {...buildProps({ mode, access: fullAccess })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeInTheDocument();
      expect(input).not.toHaveAttribute('readOnly');
      expect(input).not.toBeDisabled();
    });

    it('renders as readonly with access="readonly" (readOnly attribute on input)', () => {
      const readonlyAccess: WvFieldAccess = 'readonly';
      render(
        <TextField {...buildProps({ mode, access: readonlyAccess })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeInTheDocument();
      expect(input).toHaveAttribute('readOnly');
    });

    it('applies readonly styling (bg-gray-50 cursor-not-allowed) for readonly', () => {
      const readonlyAccess: WvFieldAccess = 'readonly';
      render(
        <TextField {...buildProps({ mode, access: readonlyAccess })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveClass('bg-gray-50');
      expect(input).toHaveClass('cursor-not-allowed');
    });

    it('renders input with access="forbidden" (component renders input without special handling)', () => {
      const forbiddenAccess: WvFieldAccess = 'forbidden';
      render(
        <TextField {...buildProps({ mode, access: forbiddenAccess })} />,
      );

      // TextField does not internally handle forbidden access — FieldRenderer
      // would intercept this. The raw component still renders an input.
      const input = screen.getByRole('textbox');
      expect(input).toBeInTheDocument();
    });

    it('readonly access does not prevent value display in display mode', () => {
      const readonlyAccess: WvFieldAccess = 'readonly';
      render(
        <TextField
          {...buildProps({ mode: 'display', access: readonlyAccess, value: 'Read-only value' })}
        />,
      );

      expect(screen.getByText('Read-only value')).toBeInTheDocument();
    });
  });

  // --------------------------------------------------------------------------
  // Validation & Error Display Tests
  // --------------------------------------------------------------------------

  describe('validation', () => {
    const mode: FieldMode = 'edit';

    it('applies error styling (border-red-500) when error prop is provided', () => {
      render(
        <TextField {...buildProps({ mode, error: 'Field is required' })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveClass('border-red-500');
      expect(input).toHaveClass('focus:border-red-500');
      expect(input).toHaveClass('focus:ring-red-500');
    });

    it('sets aria-invalid=true when error prop is provided', () => {
      render(
        <TextField {...buildProps({ mode, error: 'Required' })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('aria-invalid', 'true');
    });

    it('sets aria-describedby to error id when error prop is provided', () => {
      render(
        <TextField {...buildProps({ mode, name: 'email', error: 'Invalid email' })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('aria-describedby', 'email-error');
    });

    it('does not set aria-invalid or aria-describedby when no error', () => {
      render(
        <TextField {...buildProps({ mode })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('aria-invalid', 'false');
      expect(input).not.toHaveAttribute('aria-describedby');
    });

    it('applies normal border styling (border-gray-300) when no error', () => {
      render(
        <TextField {...buildProps({ mode })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveClass('border-gray-300');
      expect(input).toHaveClass('focus:border-blue-500');
      expect(input).toHaveClass('focus:ring-blue-500');
    });

    it('accepts validationErrors prop without crashing', () => {
      const validationErrors = [
        { key: 'minLength', value: '3', message: 'Minimum 3 characters' },
      ];

      // TextField does not render validationErrors itself — FieldRenderer does.
      // Verify the component accepts the prop without runtime errors.
      const { container } = render(
        <TextField
          {...buildProps({ mode, validationErrors })}
        />,
      );

      expect(container).toBeTruthy();
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    it('accepts labelWarningText prop without crashing', () => {
      const { container } = render(
        <TextField
          {...buildProps({ mode, labelWarningText: 'Approaching limit' })}
        />,
      );

      expect(container).toBeTruthy();
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    it('accepts labelErrorText prop without crashing', () => {
      const { container } = render(
        <TextField
          {...buildProps({ mode, labelErrorText: 'Critical error' })}
        />,
      );

      expect(container).toBeTruthy();
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    it('accepts initErrors prop without crashing', () => {
      const { container } = render(
        <TextField
          {...buildProps({ mode, initErrors: ['Init error 1', 'Init error 2'] })}
        />,
      );

      expect(container).toBeTruthy();
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });
  });

  // --------------------------------------------------------------------------
  // Null / Empty Value Handling Tests
  // --------------------------------------------------------------------------

  describe('null/empty handling', () => {
    it('handles null value gracefully in edit mode (empty input)', () => {
      const onChange = vi.fn();
      render(
        <TextField {...buildProps({ mode: 'edit', value: null, onChange })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveValue('');
    });

    it('handles undefined value gracefully in edit mode', () => {
      // value is typed as string | null, so we cast to cover the runtime case
      render(
        <TextField
          {...buildProps({ mode: 'edit', value: undefined as unknown as string | null })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveValue('');
    });

    it('handles empty string value in edit mode', () => {
      const onChange = vi.fn();
      render(
        <TextField {...buildProps({ mode: 'edit', value: '', onChange })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveValue('');
    });

    it('shows "no data" as default emptyValueMessage in display mode for null value', () => {
      render(
        <TextField {...buildProps({ mode: 'display', value: null })} />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('shows "no data" as default emptyValueMessage in display mode for empty string', () => {
      render(
        <TextField {...buildProps({ mode: 'display', value: '' })} />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('shows custom emptyValueMessage when provided for null value', () => {
      render(
        <TextField
          {...buildProps({ mode: 'display', value: null, emptyValueMessage: 'Nothing here' })}
        />,
      );

      expect(screen.getByText('Nothing here')).toBeInTheDocument();
      expect(screen.queryByText('no data')).not.toBeInTheDocument();
    });

    it('applies italic empty value styling in display mode for null value', () => {
      render(
        <TextField {...buildProps({ mode: 'display', value: null })} />,
      );

      const emptyEl = screen.getByText('no data');
      expect(emptyEl).toHaveClass('text-sm');
      expect(emptyEl).toHaveClass('text-gray-500');
      expect(emptyEl).toHaveClass('italic');
    });
  });

  // --------------------------------------------------------------------------
  // Label Rendering Tests
  // --------------------------------------------------------------------------

  describe('label', () => {
    const mode: FieldMode = 'edit';

    it('accepts label prop without crashing (labels rendered by FieldRenderer)', () => {
      const { container } = render(
        <TextField {...buildProps({ mode, label: 'Full Name' })} />,
      );

      expect(container).toBeTruthy();
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    it('accepts required=true and marks input with required attribute and aria-required', () => {
      render(
        <TextField {...buildProps({ mode, label: 'Email', required: true })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('required');
      expect(input).toHaveAttribute('aria-required', 'true');
    });

    it('accepts labelHelpText prop without crashing', () => {
      const { container } = render(
        <TextField
          {...buildProps({ mode, label: 'Username', labelHelpText: 'Your unique identifier' })}
        />,
      );

      expect(container).toBeTruthy();
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    it('accepts labelMode="hidden" prop without crashing', () => {
      const hiddenMode: WvLabelRenderMode = 'hidden';
      const { container } = render(
        <TextField
          {...buildProps({ mode, label: 'Hidden Label', labelMode: hiddenMode })}
        />,
      );

      // TextField does not render labels itself — it only renders the input.
      // FieldRenderer handles label rendering per labelMode.
      expect(container).toBeTruthy();
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    it('accepts labelMode="stacked" prop without crashing', () => {
      const stackedMode: WvLabelRenderMode = 'stacked';
      const { container } = render(
        <TextField
          {...buildProps({ mode, label: 'Stacked Label', labelMode: stackedMode })}
        />,
      );

      expect(container).toBeTruthy();
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    it('accepts labelMode="horizontal" prop without crashing', () => {
      const horizontalMode: WvLabelRenderMode = 'horizontal';
      const { container } = render(
        <TextField
          {...buildProps({ mode, label: 'Horizontal Label', labelMode: horizontalMode })}
        />,
      );

      expect(container).toBeTruthy();
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    it('accepts labelMode="inline" prop without crashing', () => {
      const inlineMode: WvLabelRenderMode = 'inline';
      const { container } = render(
        <TextField
          {...buildProps({ mode, label: 'Inline Label', labelMode: inlineMode })}
        />,
      );

      expect(container).toBeTruthy();
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });
  });

  // --------------------------------------------------------------------------
  // Visibility Tests
  // --------------------------------------------------------------------------

  describe('visibility', () => {
    it('renders component when isVisible=true', () => {
      render(
        <TextField {...buildProps({ mode: 'edit', isVisible: true })} />,
      );

      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <TextField {...buildProps({ mode: 'edit', isVisible: false })} />,
      );

      expect(container.innerHTML).toBe('');
    });

    it('renders component when isVisible is undefined (default visible)', () => {
      // isVisible defaults to true when undefined
      render(
        <TextField
          {...buildProps({ mode: 'edit', isVisible: undefined as unknown as boolean })}
        />,
      );

      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    it('renders nothing in display mode when isVisible=false', () => {
      const { container } = render(
        <TextField
          {...buildProps({ mode: 'display', value: 'Should not show', isVisible: false })}
        />,
      );

      expect(container.innerHTML).toBe('');
    });

    it('renders display content when isVisible=true', () => {
      render(
        <TextField
          {...buildProps({ mode: 'display', value: 'Visible text', isVisible: true })}
        />,
      );

      expect(screen.getByText('Visible text')).toBeInTheDocument();
    });
  });

  // --------------------------------------------------------------------------
  // Link Feature Tests (from PcFieldTextOptions)
  // --------------------------------------------------------------------------

  describe('link feature', () => {
    it('renders value as <a> tag with href in display mode when href provided', () => {
      render(
        <TextField
          {...buildProps({
            mode: 'display',
            value: 'Click me',
            href: 'https://example.com',
          })}
        />,
      );

      const link = screen.getByText('Click me');
      expect(link.tagName).toBe('A');
      expect(link).toHaveAttribute('href', 'https://example.com');
    });

    it('applies link styling classes when href is provided in display mode', () => {
      render(
        <TextField
          {...buildProps({
            mode: 'display',
            value: 'Styled link',
            href: 'https://example.com/page',
          })}
        />,
      );

      const link = screen.getByText('Styled link');
      expect(link).toHaveClass('text-sm');
      expect(link).toHaveClass('text-blue-600');
    });

    it('renders with rel="noopener noreferrer" for security', () => {
      render(
        <TextField
          {...buildProps({
            mode: 'display',
            value: 'Secure link',
            href: 'https://external.com',
          })}
        />,
      );

      const link = screen.getByText('Secure link');
      expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    });

    it('does not render link in edit mode even if href is provided', () => {
      render(
        <TextField
          {...buildProps({
            mode: 'edit',
            value: 'Edit value',
            href: 'https://example.com',
          })}
        />,
      );

      // In edit mode the component renders an <input>, not an <a> tag
      expect(screen.getByRole('textbox')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('renders plain text when href is not provided in display mode', () => {
      render(
        <TextField
          {...buildProps({ mode: 'display', value: 'Plain text' })}
        />,
      );

      const el = screen.getByText('Plain text');
      expect(el.tagName).toBe('SPAN');
      expect(el).not.toHaveAttribute('href');
    });

    it('does not render link when value is null even if href is provided', () => {
      render(
        <TextField
          {...buildProps({
            mode: 'display',
            value: null,
            href: 'https://example.com',
          })}
        />,
      );

      // Null value shows emptyValueMessage instead of a link
      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });
  });
});
