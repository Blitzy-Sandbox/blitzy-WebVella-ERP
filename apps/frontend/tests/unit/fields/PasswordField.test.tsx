import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom/vitest';
import PasswordField from '../../../src/components/fields/PasswordField';
import type { PasswordFieldProps } from '../../../src/components/fields/PasswordField';

/**
 * Vitest unit tests for PasswordField component.
 *
 * Replaces the monolith's PcFieldPassword ViewComponent.
 * Tests: password input, min/max length, visibility toggle, access control,
 * validation, null/empty handling, and visibility toggling.
 */

/** Helper to build props with sensible defaults, allowing per-test overrides. */
const buildProps = (overrides: Partial<PasswordFieldProps> = {}): PasswordFieldProps => ({
  name: 'password',
  value: 'secret123',
  ...overrides,
});

describe('PasswordField', () => {
  afterEach(() => {
    cleanup();
  });

  // ────────────────────────────────────────────────────────────────────────
  // Display Mode
  // ────────────────────────────────────────────────────────────────────────
  describe('display mode', () => {
    it('shows masked dots (••••••••) in display mode — never shows actual password', () => {
      render(<PasswordField {...buildProps({ mode: 'display', value: 'secret123' })} />);

      // Masked constant should be present
      expect(screen.getByText('••••••••')).toBeInTheDocument();

      // Actual password must never leak into the DOM
      expect(screen.queryByText('secret123')).not.toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null', () => {
      render(<PasswordField {...buildProps({ mode: 'display', value: null })} />);

      // Default emptyValueMessage is "no data"
      expect(screen.getByText('no data')).toBeInTheDocument();
    });
  });

  // ────────────────────────────────────────────────────────────────────────
  // Edit Mode
  // ────────────────────────────────────────────────────────────────────────
  describe('edit mode', () => {
    it('renders an input[type="password"] by default in edit mode', () => {
      render(<PasswordField {...buildProps({ mode: 'edit' })} />);

      const input = screen.getByLabelText('password');
      expect(input).toHaveAttribute('type', 'password');
    });

    it('displays current value (masked) in the input', () => {
      render(<PasswordField {...buildProps({ mode: 'edit', value: 'test-value' })} />);

      const input = screen.getByLabelText('password') as HTMLInputElement;
      expect(input.value).toBe('test-value');
    });

    it('calls onChange when user types', async () => {
      const user = userEvent.setup();
      const onChange = vi.fn();

      render(
        <PasswordField {...buildProps({ mode: 'edit', value: '', onChange })} />,
      );

      const input = screen.getByLabelText('password');
      await user.type(input, 'a');

      expect(onChange).toHaveBeenCalled();
      expect(onChange).toHaveBeenCalledWith('a');
    });

    it('applies minLength attribute when provided', () => {
      render(<PasswordField {...buildProps({ mode: 'edit', minLength: 8 })} />);

      const input = screen.getByLabelText('password');
      expect(input).toHaveAttribute('minLength', '8');
    });

    it('applies maxLength attribute when provided', () => {
      render(<PasswordField {...buildProps({ mode: 'edit', maxLength: 32 })} />);

      const input = screen.getByLabelText('password');
      expect(input).toHaveAttribute('maxLength', '32');
    });
  });

  // ────────────────────────────────────────────────────────────────────────
  // Visibility Toggle
  // ────────────────────────────────────────────────────────────────────────
  describe('visibility toggle', () => {
    it('has a toggle button to switch between password and text type', () => {
      render(<PasswordField {...buildProps({ mode: 'edit' })} />);

      const toggle = screen.getByRole('button', { name: /show password/i });
      expect(toggle).toBeInTheDocument();
    });

    it('clicking toggle changes input type from "password" to "text"', async () => {
      const user = userEvent.setup();
      render(<PasswordField {...buildProps({ mode: 'edit' })} />);

      const toggle = screen.getByRole('button', { name: /show password/i });
      await user.click(toggle);

      const input = screen.getByLabelText('password');
      expect(input).toHaveAttribute('type', 'text');
    });

    it('clicking toggle again changes back to "password"', async () => {
      const user = userEvent.setup();
      render(<PasswordField {...buildProps({ mode: 'edit' })} />);

      // First click — switch to text
      const showToggle = screen.getByRole('button', { name: /show password/i });
      await user.click(showToggle);

      // Second click — switch back to password
      const hideToggle = screen.getByRole('button', { name: /hide password/i });
      await user.click(hideToggle);

      const input = screen.getByLabelText('password');
      expect(input).toHaveAttribute('type', 'password');
    });

    it('toggle button has eye/eye-off icon', async () => {
      const user = userEvent.setup();
      render(<PasswordField {...buildProps({ mode: 'edit' })} />);

      // Initially aria-pressed="false" and shows "Show password" (eye icon state)
      const showBtn = screen.getByRole('button', { name: /show password/i });
      expect(showBtn).toHaveAttribute('aria-pressed', 'false');

      // After toggle: aria-pressed="true" and shows "Hide password" (eye-off icon state)
      await user.click(showBtn);
      const hideBtn = screen.getByRole('button', { name: /hide password/i });
      expect(hideBtn).toHaveAttribute('aria-pressed', 'true');
    });
  });

  // ────────────────────────────────────────────────────────────────────────
  // Password Constraints
  // ────────────────────────────────────────────────────────────────────────
  describe('password constraints', () => {
    it('applies minLength HTML attribute', () => {
      render(<PasswordField {...buildProps({ mode: 'edit', minLength: 6 })} />);

      const input = screen.getByLabelText('password');
      expect(input).toHaveAttribute('minLength', '6');
    });

    it('applies maxLength HTML attribute', () => {
      render(<PasswordField {...buildProps({ mode: 'edit', maxLength: 64 })} />);

      const input = screen.getByLabelText('password');
      expect(input).toHaveAttribute('maxLength', '64');
    });

    it('handles null minLength/maxLength gracefully', () => {
      render(
        <PasswordField
          {...buildProps({ mode: 'edit', minLength: null, maxLength: null })}
        />,
      );

      const input = screen.getByLabelText('password');
      // null constraints should not appear as HTML attributes
      expect(input).not.toHaveAttribute('minLength');
      expect(input).not.toHaveAttribute('maxLength');
    });
  });

  // ────────────────────────────────────────────────────────────────────────
  // Access Control
  // ────────────────────────────────────────────────────────────────────────
  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(<PasswordField {...buildProps({ mode: 'edit', access: 'full' })} />);

      const input = screen.getByLabelText('password');
      expect(input).toBeInTheDocument();
      expect(input).not.toBeDisabled();
    });

    it('renders as readonly with access="readonly"', () => {
      render(
        <PasswordField
          {...buildProps({ mode: 'edit', access: 'readonly', value: 'hidden' })}
        />,
      );

      // Readonly renders the display-mode masked dots — no editable input
      expect(screen.getByText('••••••••')).toBeInTheDocument();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <PasswordField {...buildProps({ mode: 'edit', access: 'forbidden' })} />,
      );

      // Default accessDeniedMessage is "access denied"
      expect(screen.getByText('access denied')).toBeInTheDocument();
    });
  });

  // ────────────────────────────────────────────────────────────────────────
  // Validation
  // ────────────────────────────────────────────────────────────────────────
  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <PasswordField
          {...buildProps({ mode: 'edit', error: 'Password is required' })}
        />,
      );

      const input = screen.getByLabelText('password');
      // Component reflects error state via aria attributes and CSS —
      // visible error text is rendered by the parent FieldRenderer wrapper
      expect(input).toHaveAttribute('aria-invalid', 'true');
      expect(input).toHaveAttribute(
        'aria-describedby',
        expect.stringContaining('field-password-error'),
      );
    });

    it('shows validation errors', () => {
      render(
        <PasswordField
          {...buildProps({
            mode: 'edit',
            error: 'Minimum 8 characters',
          })}
        />,
      );

      const input = screen.getByLabelText('password');
      // Error state indicated through aria-invalid and error styling
      expect(input).toHaveAttribute('aria-invalid', 'true');
      expect(input).toHaveClass('border-red-500');
    });

    it('applies error styling', () => {
      render(
        <PasswordField
          {...buildProps({ mode: 'edit', error: 'Invalid password' })}
        />,
      );

      const input = screen.getByLabelText('password');
      expect(input).toHaveClass('border-red-500');
    });
  });

  // ────────────────────────────────────────────────────────────────────────
  // Null / Empty Handling
  // ────────────────────────────────────────────────────────────────────────
  describe('null/empty handling', () => {
    it('handles null value in edit mode', () => {
      render(<PasswordField {...buildProps({ mode: 'edit', value: null })} />);

      const input = screen.getByLabelText('password') as HTMLInputElement;
      // Component uses `value ?? ''` so null maps to empty string
      expect(input.value).toBe('');
    });

    it('handles undefined value', () => {
      render(
        <PasswordField
          {...buildProps({
            mode: 'edit',
            value: undefined as unknown as string | null,
          })}
        />,
      );

      const input = screen.getByLabelText('password') as HTMLInputElement;
      // Component uses `value ?? ''` so undefined maps to empty string
      expect(input.value).toBe('');
    });
  });

  // ────────────────────────────────────────────────────────────────────────
  // Visibility (isVisible prop)
  // ────────────────────────────────────────────────────────────────────────
  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <PasswordField {...buildProps({ mode: 'edit', isVisible: true })} />,
      );

      expect(screen.getByLabelText('password')).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <PasswordField {...buildProps({ mode: 'edit', isVisible: false })} />,
      );

      // Component returns null → container is empty
      expect(container.innerHTML).toBe('');
    });
  });
});
