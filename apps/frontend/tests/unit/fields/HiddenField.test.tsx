/**
 * Vitest Component Tests for `<HiddenField />`
 *
 * Validates the React HiddenField component
 * (`apps/frontend/src/components/fields/HiddenField.tsx`) that replaces
 * the monolith's `PcFieldHidden` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldHidden/PcFieldHidden.cs`).
 *
 * The monolith's PcFieldHiddenOptions extend PcFieldBaseOptions with:
 *   - Name   (form field name — `"field"` default)
 *   - Value  (data-source bound value — any type serialised to string)
 *   - Mode   (Display/Edit — hidden input renders identically in both)
 *
 * Test coverage spans:
 *   - Rendering: hidden input type, name/value attributes, no visible UI,
 *     data-field-type attribute, invisibility in DOM layout
 *   - Value handling: string values, numeric-to-string coercion, null/undefined
 *     normalisation to empty string, object-to-string conversion via String()
 *   - Both modes: hidden input renders identically in display and edit modes
 *     with no visible chrome regardless of mode
 *   - onChange: prop acceptance and programmatic callback contract
 *   - Visibility: isVisible=false suppresses all output (returns null)
 *
 * @see apps/frontend/src/components/fields/HiddenField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldHidden/PcFieldHidden.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import React from 'react';
import HiddenField from '../../../src/components/fields/HiddenField';
import type { HiddenFieldProps } from '../../../src/components/fields/HiddenField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default HiddenFieldProps for consistent test setup.
 * Mirrors the PcFieldHiddenOptions defaults from PcFieldHidden.cs:
 *   - Name  = "field" (PcFieldBaseOptions default)
 *   - Value = "" (PcFieldBaseOptions default)
 *
 * The HiddenField component renders `<input type="hidden">` with the
 * serialised value. All inherited BaseFieldProps (label, mode, access, etc.)
 * are accepted for interface consistency but produce no visible output since
 * hidden fields have no visible DOM representation.
 */
function createDefaultProps(
  overrides: Partial<HiddenFieldProps> = {},
): HiddenFieldProps {
  return {
    name: 'hidden_field',
    value: 'test-value',
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Test Suite
// ---------------------------------------------------------------------------

describe('HiddenField', () => {
  afterEach(() => {
    cleanup();
  });

  // -------------------------------------------------------------------------
  // Rendering Tests
  // -------------------------------------------------------------------------
  describe('rendering', () => {
    it('renders an input[type="hidden"] element', () => {
      const props = createDefaultProps();
      const { container } = render(<HiddenField {...props} />);

      // Hidden inputs are not accessible via screen queries (they have no
      // ARIA role), so we use direct DOM querySelector — matching the
      // testing-library recommendation for hidden form elements.
      const input = container.querySelector('input[type="hidden"]');
      expect(input).toBeInTheDocument();
    });

    it('sets value attribute on hidden input', () => {
      const props = createDefaultProps({ value: 'my-secret-value' });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveValue('my-secret-value');
    });

    it('sets name attribute on hidden input', () => {
      const props = createDefaultProps({ name: 'record_id' });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveAttribute('name', 'record_id');
    });

    it('renders no visible UI elements', () => {
      const props = createDefaultProps();
      const { container } = render(<HiddenField {...props} />);

      // The component should render exactly one child element — the hidden
      // input — with zero visible text content. No labels, error messages,
      // help text, or other visual chrome should be present.
      expect(container.children.length).toBe(1);
      expect(container.textContent).toBe('');

      // Verify the only child is indeed a hidden input
      const input = container.querySelector('input[type="hidden"]');
      expect(input).toBeInTheDocument();

      // Verify no label, div, span, or other visible elements exist
      expect(container.querySelector('label')).not.toBeInTheDocument();
      expect(container.querySelector('span')).not.toBeInTheDocument();
      expect(container.querySelector('div')).not.toBeInTheDocument();
    });

    it('is not visible in the DOM layout', () => {
      const props = createDefaultProps({ value: 'invisible-data' });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toBeInTheDocument();

      // The input type must be "hidden", which browsers exclude from
      // visual rendering flow — equivalent to the monolith's hidden
      // ViewComponent that renders only `<input type="hidden">`.
      expect(input.type).toBe('hidden');

      // Hidden inputs should not be visible in accessibility queries
      expect(
        screen.queryByRole('textbox', { name: /hidden_field/i }),
      ).not.toBeInTheDocument();
    });

    it('renders data-field-type attribute for field type identification', () => {
      const props = createDefaultProps();
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector('input[type="hidden"]');
      expect(input).toBeInTheDocument();
      expect(input).toHaveAttribute('data-field-type', 'hidden');
    });

    it('renders null when isVisible is false', () => {
      const props = createDefaultProps({ isVisible: false });
      const { container } = render(<HiddenField {...props} />);

      // When isVisible is explicitly false, the component returns null,
      // mirroring PcFieldHidden.cs lines 119-133 where `isVisible = false`
      // results in `Content("")` (empty output).
      expect(container.querySelector('input')).not.toBeInTheDocument();
      expect(container.children.length).toBe(0);
      expect(container.innerHTML).toBe('');
    });

    it('renders hidden input when isVisible is true', () => {
      const props = createDefaultProps({ isVisible: true });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector('input[type="hidden"]');
      expect(input).toBeInTheDocument();
    });

    it('renders hidden input when isVisible is undefined (default visible)', () => {
      // When isVisible is not provided (undefined), the component should
      // render normally — only `isVisible === false` suppresses output.
      const props = createDefaultProps();
      // Do not set isVisible — leave it as undefined
      delete (props as Record<string, unknown>).isVisible;
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector('input[type="hidden"]');
      expect(input).toBeInTheDocument();
    });
  });

  // -------------------------------------------------------------------------
  // Value Handling Tests
  // -------------------------------------------------------------------------
  describe('value handling', () => {
    it('carries string value as form data', () => {
      const props = createDefaultProps({ value: 'form-data-payload' });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveValue('form-data-payload');

      // Verify the raw DOM value attribute matches exactly
      expect(input.getAttribute('value')).toBe('form-data-payload');
    });

    it('carries numeric value as string', () => {
      const props = createDefaultProps({ value: 42 });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toBeInTheDocument();

      // Numeric values are serialised via String() — PcFieldHidden.cs stores
      // all values as form data strings in the hidden input's value attribute.
      expect(input).toHaveValue('42');
    });

    it('carries floating-point numeric value as string', () => {
      const props = createDefaultProps({ value: 3.14159 });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toHaveValue('3.14159');
    });

    it('carries zero as string "0"', () => {
      const props = createDefaultProps({ value: 0 });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toHaveValue('0');
    });

    it('handles null value', () => {
      const props = createDefaultProps({ value: null });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toBeInTheDocument();

      // null is normalised to empty string — avoids rendering literal "null"
      // text in the hidden input value attribute. This matches defensive
      // serialisation from the HiddenField component.
      expect(input).toHaveValue('');
    });

    it('handles undefined value', () => {
      const props = createDefaultProps({ value: undefined });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toBeInTheDocument();

      // undefined is normalised to empty string — same defensive pattern
      // as null handling to avoid rendering literal "undefined" text.
      expect(input).toHaveValue('');
    });

    it('handles object value by converting to string', () => {
      const objectValue = { key: 'val', nested: { a: 1 } };
      const props = createDefaultProps({ value: objectValue });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toBeInTheDocument();

      // Objects are converted via String() which produces "[object Object]".
      // This mirrors the generic serialisation approach — callers that need
      // JSON serialisation should pre-stringify the value before passing it.
      expect(input).toHaveValue('[object Object]');
    });

    it('handles boolean true value as string "true"', () => {
      const props = createDefaultProps({ value: true });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toHaveValue('true');
    });

    it('handles boolean false value as string "false"', () => {
      const props = createDefaultProps({ value: false });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toHaveValue('false');
    });

    it('handles empty string value', () => {
      const props = createDefaultProps({ value: '' });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toHaveValue('');
    });

    it('handles GUID-like string value', () => {
      const guid = 'f16ec6db-626d-4c27-8de0-3e7ce542c55f';
      const props = createDefaultProps({ value: guid });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toHaveValue(guid);
    });
  });

  // -------------------------------------------------------------------------
  // Both Modes Tests
  // -------------------------------------------------------------------------
  describe('both modes', () => {
    it('renders hidden input in both display and edit mode', () => {
      // Display mode — mirrors PcFieldHidden returning View("Display")
      const displayProps = createDefaultProps({
        mode: 'display',
        value: 'display-val',
      });
      const { container: displayContainer } = render(
        <HiddenField {...displayProps} />,
      );
      const displayInput = displayContainer.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(displayInput).toBeInTheDocument();
      expect(displayInput).toHaveValue('display-val');
      cleanup();

      // Edit mode — mirrors PcFieldHidden returning default view
      const editProps = createDefaultProps({
        mode: 'edit',
        value: 'edit-val',
      });
      const { container: editContainer } = render(
        <HiddenField {...editProps} />,
      );
      const editInput = editContainer.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(editInput).toBeInTheDocument();
      expect(editInput).toHaveValue('edit-val');
    });

    it('renders nothing visible regardless of mode', () => {
      // Display mode — no visible text or chrome
      const displayProps = createDefaultProps({ mode: 'display' });
      const { container: displayContainer } = render(
        <HiddenField {...displayProps} />,
      );
      expect(displayContainer.textContent).toBe('');
      expect(displayContainer.querySelector('label')).not.toBeInTheDocument();
      expect(displayContainer.querySelector('span')).not.toBeInTheDocument();
      cleanup();

      // Edit mode — no visible text or chrome
      const editProps = createDefaultProps({ mode: 'edit' });
      const { container: editContainer } = render(
        <HiddenField {...editProps} />,
      );
      expect(editContainer.textContent).toBe('');
      expect(editContainer.querySelector('label')).not.toBeInTheDocument();
      expect(editContainer.querySelector('span')).not.toBeInTheDocument();
    });

    it('produces identical DOM structure in display and edit modes', () => {
      const sharedValue = 'identical-value';

      const displayProps = createDefaultProps({
        mode: 'display',
        value: sharedValue,
      });
      const { container: displayContainer } = render(
        <HiddenField {...displayProps} />,
      );
      const displayHTML = displayContainer.innerHTML;
      cleanup();

      const editProps = createDefaultProps({
        mode: 'edit',
        value: sharedValue,
      });
      const { container: editContainer } = render(
        <HiddenField {...editProps} />,
      );
      const editHTML = editContainer.innerHTML;

      // The hidden input should render identically regardless of mode —
      // this is the key differentiator from other field components that
      // switch between display and edit UI.
      expect(displayHTML).toBe(editHTML);
    });

    it('renders hidden input when mode is undefined (default)', () => {
      const props = createDefaultProps({ value: 'default-mode-val' });
      // Do not set mode — leave as undefined
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveValue('default-mode-val');
    });
  });

  // -------------------------------------------------------------------------
  // onChange Tests
  // -------------------------------------------------------------------------
  describe('onChange', () => {
    it('accepts onChange prop without errors', () => {
      // HiddenField accepts an onChange callback for programmatic value
      // change notifications from parent components. The hidden input
      // itself does not trigger onChange from user interaction (it's not
      // visible/interactive), but the prop should be accepted cleanly.
      const onChangeMock = vi.fn();
      const props = createDefaultProps({
        onChange: onChangeMock,
        value: 'initial-value',
      });
      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveValue('initial-value');

      // onChange should not have been called during render
      expect(onChangeMock).not.toHaveBeenCalled();
    });

    it('renders correctly without onChange prop', () => {
      // onChange is optional — the component must render successfully
      // even when no onChange callback is provided.
      const props = createDefaultProps({ value: 'no-callback' });
      // Explicitly ensure no onChange
      delete (props as Record<string, unknown>).onChange;

      const { container } = render(<HiddenField {...props} />);

      const input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input).toHaveValue('no-callback');
    });

    it('re-renders with updated value when props change', () => {
      const onChangeMock = vi.fn();
      const props = createDefaultProps({
        onChange: onChangeMock,
        value: 'original',
      });

      const { container, rerender } = render(<HiddenField {...props} />);
      let input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toHaveValue('original');

      // Simulate parent updating the value via props
      const updatedProps = createDefaultProps({
        onChange: onChangeMock,
        value: 'updated',
      });
      rerender(<HiddenField {...updatedProps} />);

      input = container.querySelector(
        'input[type="hidden"]',
      ) as HTMLInputElement;
      expect(input).toHaveValue('updated');
    });
  });
});
