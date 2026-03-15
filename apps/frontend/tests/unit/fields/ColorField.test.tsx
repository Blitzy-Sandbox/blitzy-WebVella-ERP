/**
 * Vitest Component Tests for `<ColorField />`
 *
 * Validates the React ColorField component
 * (`apps/frontend/src/components/fields/ColorField.tsx`) that replaces
 * the monolith's `PcFieldColor` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldColor/PcFieldColor.cs`).
 *
 * The monolith's PcFieldColorOptions extend PcFieldBaseOptions (inheriting
 * IsVisible, LabelMode, LabelText, Mode, Name). The component provides a
 * native browser color picker (`<input type="color">`) alongside a hex
 * text input in edit mode, and a color swatch with hex value text in
 * display mode — mirroring the original Razor ViewComponent behavior.
 *
 * Test coverage spans:
 *   - Display mode: color swatch rendering with inline background-color
 *     style, hex value text, emptyValueMessage for null/empty values
 *   - Edit mode: native `<input type="color">` element, text input for
 *     manual hex entry, color picker and text input sync, onChange
 *     callbacks for both inputs, current value display in both inputs
 *   - Color sync: changing color picker updates text input, valid hex
 *     propagation from text input to onChange, handling valid hex values
 *   - Access control: full (read/write), readonly (disabled), forbidden
 *     (access denied message)
 *   - Validation: error prop styling (red borders, aria-invalid)
 *   - Null/empty handling: null value, undefined value
 *   - Visibility: isVisible true renders component, isVisible false
 *     renders nothing
 *
 * @see apps/frontend/src/components/fields/ColorField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldColor/PcFieldColor.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import ColorField from '../../../src/components/fields/ColorField';
import type { ColorFieldProps } from '../../../src/components/fields/ColorField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default ColorFieldProps for consistent test setup.
 * Mirrors the PcFieldColorOptions defaults from PcFieldColor.cs:
 *   - Name  = "field" (PcFieldBaseOptions default)
 *   - Value = "" (PcFieldBaseOptions default — mapped to null in React)
 *
 * The ColorField component renders a native `<input type="color">` and a
 * hex text input in edit mode, or a colored swatch with hex text in
 * display mode. Override any prop via the `overrides` parameter.
 */
function createDefaultProps(
  overrides: Partial<ColorFieldProps> = {},
): ColorFieldProps {
  return {
    name: 'color_field',
    value: '#ff0000',
    ...overrides,
  };
}

/** Well-known hex color value used across test cases. */
const TEST_COLOR = '#ff0000';

/** Alternate hex color value for change detection scenarios. */
const ALT_COLOR = '#00ff00';

/** Default fallback color when value is null/undefined — matches component constant. */
const DEFAULT_COLOR = '#000000';

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('ColorField', () => {
  afterEach(() => {
    cleanup();
  });

  // =========================================================================
  // Display Mode
  // =========================================================================

  describe('display mode', () => {
    it('shows color swatch (small colored square) with hex value text', () => {
      const props = createDefaultProps({
        mode: 'display',
        value: TEST_COLOR,
      });
      render(<ColorField {...props} />);

      // The color swatch is rendered as a <span> with role="img"
      const swatch = screen.getByRole('img', {
        name: `Color swatch: ${TEST_COLOR}`,
      });
      expect(swatch).toBeInTheDocument();

      // Hex value text is rendered alongside the swatch
      expect(screen.getByText(TEST_COLOR)).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null', () => {
      const props = createDefaultProps({
        mode: 'display',
        value: null,
      });
      render(<ColorField {...props} />);

      // When value is null, the component should display the empty value message
      expect(screen.getByText('no data')).toBeInTheDocument();

      // Color swatch should NOT be rendered
      expect(
        screen.queryByRole('img', { name: /color swatch/i }),
      ).not.toBeInTheDocument();
    });

    it('applies inline background-color style to swatch', () => {
      const props = createDefaultProps({
        mode: 'display',
        value: '#336699',
      });
      render(<ColorField {...props} />);

      const swatch = screen.getByRole('img', {
        name: 'Color swatch: #336699',
      });
      expect(swatch).toHaveStyle({ backgroundColor: '#336699' });
    });

    it('displays custom emptyValueMessage when provided and value is null', () => {
      const props = createDefaultProps({
        mode: 'display',
        value: null,
        emptyValueMessage: 'no color selected',
      });
      render(<ColorField {...props} />);

      expect(screen.getByText('no color selected')).toBeInTheDocument();
    });

    it('renders copy-to-clipboard button in display mode', () => {
      const props = createDefaultProps({
        mode: 'display',
        value: TEST_COLOR,
      });
      render(<ColorField {...props} />);

      const copyButton = screen.getByRole('button', {
        name: `Copy color value ${TEST_COLOR}`,
      });
      expect(copyButton).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Edit Mode
  // =========================================================================

  describe('edit mode', () => {
    it('renders an input[type="color"] element', () => {
      const props = createDefaultProps({ mode: 'edit' });
      render(<ColorField {...props} />);

      // The native color picker input should be rendered
      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      expect(colorInput).toBeInTheDocument();
      expect(colorInput).toHaveAttribute('type', 'color');
    });

    it('renders a text input showing hex value', () => {
      const props = createDefaultProps({
        mode: 'edit',
        value: TEST_COLOR,
      });
      render(<ColorField {...props} />);

      // The hex text input should show the current color value
      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;
      expect(textInput).toBeInTheDocument();
      expect(textInput.value).toBe(TEST_COLOR);
    });

    it('color picker and text input stay in sync', () => {
      const onChange = vi.fn();
      const props = createDefaultProps({
        mode: 'edit',
        value: TEST_COLOR,
        onChange,
      });
      render(<ColorField {...props} />);

      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Change color picker — text input should update via internal state
      fireEvent.change(colorInput, { target: { value: ALT_COLOR } });

      // After color picker change, text input internal state is updated
      expect(textInput.value).toBe(ALT_COLOR);
    });

    it('calls onChange when color is picked', () => {
      const onChange = vi.fn();
      const props = createDefaultProps({
        mode: 'edit',
        value: TEST_COLOR,
        onChange,
      });
      render(<ColorField {...props} />);

      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;

      fireEvent.change(colorInput, { target: { value: '#0000ff' } });

      expect(onChange).toHaveBeenCalledTimes(1);
      expect(onChange).toHaveBeenCalledWith('#0000ff');
    });

    it('calls onChange when hex is manually typed', () => {
      const onChange = vi.fn();
      const props = createDefaultProps({
        mode: 'edit',
        value: TEST_COLOR,
        onChange,
      });
      render(<ColorField {...props} />);

      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Clear and type a full valid hex color — the component auto-prepends #
      // and propagates only valid 6-digit hex values to onChange
      fireEvent.change(textInput, { target: { value: '#abcdef' } });

      expect(onChange).toHaveBeenCalledWith('#abcdef');
    });

    it('displays current color value in both inputs', () => {
      const props = createDefaultProps({
        mode: 'edit',
        value: '#abcdef',
      });
      render(<ColorField {...props} />);

      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Color picker should display the normalized (lowercase) color value
      expect(colorInput.value).toBe('#abcdef');
      // Text input should display the value from internal state
      expect(textInput.value).toBe('#abcdef');
    });

    it('sets name attribute on the color picker input', () => {
      const props = createDefaultProps({
        mode: 'edit',
        name: 'my_color',
      });
      render(<ColorField {...props} />);

      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      expect(colorInput).toHaveAttribute('name', 'my_color');
    });

    it('sets placeholder on the hex text input', () => {
      const props = createDefaultProps({
        mode: 'edit',
        placeholder: '#ffffff',
      });
      render(<ColorField {...props} />);

      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;
      expect(textInput).toHaveAttribute('placeholder', '#ffffff');
    });

    it('uses default placeholder when none provided', () => {
      const props = createDefaultProps({ mode: 'edit' });
      render(<ColorField {...props} />);

      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;
      expect(textInput).toHaveAttribute('placeholder', '#000000');
    });
  });

  // =========================================================================
  // Color Sync
  // =========================================================================

  describe('color sync', () => {
    it('changing color picker updates text input', () => {
      const onChange = vi.fn();
      const props = createDefaultProps({
        mode: 'edit',
        value: TEST_COLOR,
        onChange,
      });
      render(<ColorField {...props} />);

      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Simulate picking a new color via the native picker
      fireEvent.change(colorInput, { target: { value: '#123456' } });

      // The internal text state should sync to the new color
      expect(textInput.value).toBe('#123456');
      // onChange should have been called
      expect(onChange).toHaveBeenCalledWith('#123456');
    });

    it('changing text input updates color picker when parent re-renders', () => {
      const onChange = vi.fn();
      const props = createDefaultProps({
        mode: 'edit',
        value: TEST_COLOR,
        onChange,
      });
      const { rerender } = render(<ColorField {...props} />);

      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Type a new valid hex value in the text input
      fireEvent.change(textInput, { target: { value: '#aabbcc' } });

      // onChange should have been called with the valid hex
      expect(onChange).toHaveBeenCalledWith('#aabbcc');

      // Simulate parent re-rendering with the new value
      rerender(<ColorField {...props} value="#aabbcc" />);

      // Now the color picker should reflect the new value
      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      expect(colorInput.value).toBe('#aabbcc');
    });

    it('handles valid hex values', () => {
      const onChange = vi.fn();
      const props = createDefaultProps({
        mode: 'edit',
        value: DEFAULT_COLOR,
        onChange,
      });
      render(<ColorField {...props} />);

      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Valid 6-digit hex with # prefix should propagate
      fireEvent.change(textInput, { target: { value: '#ff9900' } });
      expect(onChange).toHaveBeenCalledWith('#ff9900');
    });

    it('does not propagate invalid hex values to onChange', () => {
      const onChange = vi.fn();
      const props = createDefaultProps({
        mode: 'edit',
        value: TEST_COLOR,
        onChange,
      });
      render(<ColorField {...props} />);

      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Partial hex should NOT propagate to onChange
      fireEvent.change(textInput, { target: { value: '#ff' } });
      expect(onChange).not.toHaveBeenCalled();

      // Invalid characters should NOT propagate to onChange
      fireEvent.change(textInput, { target: { value: '#gggggg' } });
      expect(onChange).not.toHaveBeenCalled();
    });

    it('auto-prepends # when user omits it in text input', () => {
      const onChange = vi.fn();
      const props = createDefaultProps({
        mode: 'edit',
        value: TEST_COLOR,
        onChange,
      });
      render(<ColorField {...props} />);

      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Typing without # — the component auto-prepends it
      fireEvent.change(textInput, { target: { value: 'abcdef' } });

      // After auto-prepend, #abcdef is a valid hex, so onChange fires
      expect(onChange).toHaveBeenCalledWith('#abcdef');
      // The internal text state should include the #
      expect(textInput.value).toBe('#abcdef');
    });
  });

  // =========================================================================
  // Access Control
  // =========================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      const props = createDefaultProps({
        mode: 'edit',
        access: 'full',
      });
      render(<ColorField {...props} />);

      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Both inputs should be enabled when access is full
      expect(colorInput).not.toBeDisabled();
      expect(textInput).not.toBeDisabled();
    });

    it('renders as readonly with access="readonly"', () => {
      const props = createDefaultProps({
        mode: 'edit',
        access: 'readonly',
        value: TEST_COLOR,
      });
      render(<ColorField {...props} />);

      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Both inputs should be disabled when access is readonly
      expect(colorInput).toBeDisabled();
      expect(textInput).toBeDisabled();
    });

    it('renders access denied message with access="forbidden"', () => {
      const props = createDefaultProps({
        mode: 'edit',
        access: 'forbidden',
        value: TEST_COLOR,
      });
      render(<ColorField {...props} />);

      // Should show the default access denied message
      expect(screen.getByText('access denied')).toBeInTheDocument();

      // Should NOT render any inputs
      expect(
        document.querySelector('input[type="color"]'),
      ).not.toBeInTheDocument();
      expect(
        document.querySelector('input[type="text"]'),
      ).not.toBeInTheDocument();
    });

    it('renders custom accessDeniedMessage with access="forbidden"', () => {
      const props = createDefaultProps({
        mode: 'edit',
        access: 'forbidden',
        accessDeniedMessage: 'You cannot view this field',
      });
      render(<ColorField {...props} />);

      expect(
        screen.getByText('You cannot view this field'),
      ).toBeInTheDocument();
    });

    it('renders as disabled when disabled prop is true', () => {
      const props = createDefaultProps({
        mode: 'edit',
        access: 'full',
        disabled: true,
        value: TEST_COLOR,
      });
      render(<ColorField {...props} />);

      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Both inputs should be disabled when disabled prop is true
      expect(colorInput).toBeDisabled();
      expect(textInput).toBeDisabled();
    });
  });

  // =========================================================================
  // Validation
  // =========================================================================

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      const props = createDefaultProps({
        mode: 'edit',
        error: 'Invalid color value',
        value: TEST_COLOR,
      });
      render(<ColorField {...props} />);

      // The text input should have aria-invalid for accessibility
      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;
      expect(textInput).toHaveAttribute('aria-invalid', 'true');
    });

    it('shows validation errors via error styling on inputs', () => {
      const props = createDefaultProps({
        mode: 'edit',
        error: 'Color is required',
        value: null,
      });
      render(<ColorField {...props} />);

      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // When error prop is set, the text input gets error border classes
      // The component applies 'border-red-500' class when hasError is true
      expect(textInput.className).toContain('border-red-500');
    });

    it('does not show error styling when no error is provided', () => {
      const props = createDefaultProps({
        mode: 'edit',
        value: TEST_COLOR,
      });
      render(<ColorField {...props} />);

      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Without error, the standard border class should be applied
      expect(textInput.className).toContain('border-gray-300');
      expect(textInput.className).not.toContain('border-red-500');
    });

    it('sets required attribute when required prop is true', () => {
      const props = createDefaultProps({
        mode: 'edit',
        required: true,
        value: TEST_COLOR,
      });
      render(<ColorField {...props} />);

      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      expect(colorInput).toHaveAttribute('required');
      expect(textInput).toHaveAttribute('required');
    });
  });

  // =========================================================================
  // Null / Empty Handling
  // =========================================================================

  describe('null/empty handling', () => {
    it('handles null value', () => {
      const props = createDefaultProps({
        mode: 'edit',
        value: null,
      });
      render(<ColorField {...props} />);

      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Color picker should use the default fallback color
      expect(colorInput.value).toBe(DEFAULT_COLOR);
      // Text input should be empty when value is null
      expect(textInput.value).toBe('');
    });

    it('handles undefined value', () => {
      // TypeScript ColorFieldProps requires value: string | null,
      // but test undefined as a defensive edge case
      const props = createDefaultProps({
        mode: 'edit',
        value: undefined as unknown as string | null,
      });
      render(<ColorField {...props} />);

      const colorInput = document.querySelector(
        'input[type="color"]',
      ) as HTMLInputElement;
      const textInput = document.querySelector(
        'input[type="text"]',
      ) as HTMLInputElement;

      // Color picker should use the default fallback color
      expect(colorInput.value).toBe(DEFAULT_COLOR);
      // Text input should be empty when value is falsy
      expect(textInput.value).toBe('');
    });

    it('handles null value in display mode', () => {
      const props = createDefaultProps({
        mode: 'display',
        value: null,
      });
      render(<ColorField {...props} />);

      // Should show the empty value message
      expect(screen.getByText('no data')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Visibility
  // =========================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      const props = createDefaultProps({
        mode: 'edit',
        isVisible: true,
        value: TEST_COLOR,
      });
      const { container } = render(<ColorField {...props} />);

      // Component should render content
      expect(container.firstChild).toBeInTheDocument();
      expect(
        document.querySelector('input[type="color"]'),
      ).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const props = createDefaultProps({
        mode: 'edit',
        isVisible: false,
        value: TEST_COLOR,
      });
      const { container } = render(<ColorField {...props} />);

      // Component should return null when not visible
      expect(container.firstChild).toBeNull();
    });

    it('renders display mode content when isVisible=true', () => {
      const props = createDefaultProps({
        mode: 'display',
        isVisible: true,
        value: TEST_COLOR,
      });
      render(<ColorField {...props} />);

      // Color swatch should be visible
      const swatch = screen.getByRole('img', {
        name: `Color swatch: ${TEST_COLOR}`,
      });
      expect(swatch).toBeInTheDocument();
    });

    it('renders nothing in display mode when isVisible=false', () => {
      const props = createDefaultProps({
        mode: 'display',
        isVisible: false,
        value: TEST_COLOR,
      });
      const { container } = render(<ColorField {...props} />);

      expect(container.firstChild).toBeNull();
    });
  });
});
