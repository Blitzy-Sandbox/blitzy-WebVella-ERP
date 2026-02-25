/**
 * HiddenField — Hidden Input Component
 *
 * React replacement for the monolith's `PcFieldHidden/` ViewComponent.
 * Renders a hidden `<input>` element that carries form data without any
 * visible UI. In React controlled forms this component may not be strictly
 * necessary (data lives in state), but it is preserved for backward
 * compatibility with traditional HTML form submissions and for use-cases
 * where hidden form data must be serialised on submit.
 *
 * Source: WebVella.Erp.Web/Components/PcFieldHidden/PcFieldHidden.cs
 */

import React from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props accepted by the `HiddenField` component.
 *
 * Extends `BaseFieldProps` but overrides `value` and `onChange` with
 * `unknown` types for maximum flexibility — hidden fields can carry
 * any serialisable data type (string, number, GUID, JSON, etc.).
 *
 * Many inherited props (label, mode, access, className, …) are accepted
 * for interface consistency with the field rendering pipeline but have no
 * visual effect since hidden fields produce no visible DOM output.
 */
export interface HiddenFieldProps
  extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** The hidden value to carry as form data. Serialised via `String()`. */
  value: unknown;

  /**
   * Optional callback invoked when the hidden value changes
   * programmatically. Accepts any value type matching the field payload.
   */
  onChange?: (value: unknown) => void;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Renders a hidden `<input>` element.
 *
 * Behaviour mirrors the original PcFieldHidden ViewComponent:
 *   - When `isVisible` is explicitly `false`, the component renders nothing.
 *   - In all other cases (display mode, edit mode) it renders a single
 *     `<input type="hidden">` with the `name` and serialised `value`.
 *   - No labels, error messages, or other chrome are rendered.
 *
 * @param props - {@link HiddenFieldProps}
 * @returns A hidden input element or `null` when not visible.
 */
const HiddenField: React.FC<HiddenFieldProps> = ({
  name,
  value,
  isVisible,
}) => {
  /*
   * Visibility gate — mirrors the monolith's `isVisible` data-source check
   * in PcFieldHidden.cs (lines 119-133). When the flag is explicitly set to
   * `false`, the component outputs nothing, exactly as the original returned
   * `Content("")` for invisible fields.
   */
  if (isVisible === false) {
    return null;
  }

  /*
   * Serialise value for the HTML hidden input. `null` and `undefined` are
   * normalised to an empty string to avoid rendering the literal text
   * "null" or "undefined" in the DOM attribute (defensive pattern UI8).
   */
  const serialisedValue: string =
    value !== null && value !== undefined ? String(value) : '';

  return (
    <input
      type="hidden"
      name={name}
      value={serialisedValue}
      data-field-type="hidden"
    />
  );
};

HiddenField.displayName = 'HiddenField';

export default HiddenField;
