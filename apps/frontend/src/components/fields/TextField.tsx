/**
 * TextField — Single-Line Text Input Field Component
 *
 * React replacement for the monolith's PcFieldText ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldText/`). Renders a single-line
 * text `<input>` with maxlength enforcement, placeholder text,
 * read-only / disabled states, and optional hyperlink rendering in
 * display mode.
 *
 * The component supports both controlled (parent provides `onChange`)
 * and uncontrolled (no `onChange`, internal state) usage patterns.
 *
 * Source: WebVella.Erp.Web/Components/PcFieldText/PcFieldText.cs
 *         WebVella.Erp.Web/Components/PcFieldText/Display.cshtml
 *         WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import React, { useState, useCallback } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the TextField component.
 *
 * Extends `BaseFieldProps` via `Omit` to override `value` and `onChange`
 * with string-specific types. Adds text-field-specific properties:
 *   - `maxLength`   — Maximum character count for the input
 *   - `placeholder` — Placeholder text (also inherited from BaseFieldProps)
 *   - `href`        — URL for hyperlink rendering in display mode
 *
 * Mirrors PcFieldTextOptions from the monolith which extends
 * PcFieldBaseOptions with Placeholder, Link, and Href properties.
 */
export interface TextFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Current text value. Null represents an absent value. */
  value: string | null;

  /**
   * Callback invoked when the text value changes (edit mode).
   * Receives the raw string value from the input element.
   */
  onChange?: (value: string) => void;

  /**
   * Maximum character length for the input.
   * Null or undefined means no limit. Maps to PcFieldBaseOptions.MaxLength.
   */
  maxLength?: number | null;

  /**
   * Placeholder text shown when the input is empty.
   * Overrides the inherited placeholder from BaseFieldProps for clarity.
   * Maps to PcFieldTextOptions.Placeholder.
   */
  placeholder?: string;

  /**
   * URL for rendering the value as a hyperlink in display mode.
   * When provided and mode is 'display', the value renders as an `<a>` tag.
   * Maps to PcFieldTextOptions.Href (the resolved href from the monolith's
   * Link datasource evaluation).
   */
  href?: string;
}

// ---------------------------------------------------------------------------
// TextField Component
// ---------------------------------------------------------------------------

/**
 * Single-line text input field component.
 *
 * Renders differently based on the `mode` prop:
 *
 * **Edit Mode** (`mode === 'edit'`):
 *   - Renders `<input type="text">` with Tailwind CSS styling
 *   - Enforces `maxLength` character limit when provided
 *   - Shows `placeholder` text in the empty input
 *   - Applies `readOnly` attribute when `access === 'readonly'`
 *   - Applies `required` and `disabled` HTML attributes as specified
 *   - Applies error styling (red border/ring) when `error` is set
 *
 * **Display Mode** (`mode === 'display'`):
 *   - Renders the text value as a plain `<span>`
 *   - Renders as an `<a>` hyperlink when `href` is provided
 *   - Shows `emptyValueMessage` (default: "no data") when value is empty
 *
 * Supports both controlled (parent provides `onChange`) and uncontrolled
 * (no `onChange`, internal state via `useState`) usage patterns. When
 * `onChange` is provided, the component uses the `value` prop directly
 * (controlled). When `onChange` is absent, the component manages its
 * own state internally (uncontrolled).
 *
 * @param props - TextFieldProps configuration
 * @returns The rendered field element or null if not visible
 */
function TextField({
  name,
  fieldId,
  value,
  onChange,
  label,
  labelMode,
  mode = 'edit',
  access = 'full',
  required = false,
  disabled = false,
  error,
  className,
  placeholder,
  description,
  isVisible = true,
  emptyValueMessage = 'no data',
  accessDeniedMessage = 'access denied',
  locale,
  maxLength,
  href,
}: TextFieldProps): React.JSX.Element | null {
  // ---------------------------------------------------------------------------
  // Internal State (uncontrolled component support)
  // ---------------------------------------------------------------------------

  // Manage internal input state for uncontrolled usage (when no onChange
  // callback is provided by the parent). In controlled mode (onChange present),
  // this state is kept in sync but the prop value takes precedence.
  const [localValue, setLocalValue] = useState<string>(value ?? '');

  // ---------------------------------------------------------------------------
  // Event Handlers
  // ---------------------------------------------------------------------------

  // Memoized onChange handler that extracts the input event target value and
  // either propagates it to the parent component via the onChange prop
  // (controlled mode) or updates internal state (uncontrolled mode).
  const handleChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      const newValue = event.target.value;
      if (onChange) {
        onChange(newValue);
      } else {
        setLocalValue(newValue);
      }
    },
    [onChange]
  );

  // ---------------------------------------------------------------------------
  // Visibility Check
  // ---------------------------------------------------------------------------

  // When isVisible is false, render nothing. FieldRenderer also performs this
  // check but we handle it here for standalone usage.
  if (!isVisible) {
    return null;
  }

  // ---------------------------------------------------------------------------
  // Computed Values
  // ---------------------------------------------------------------------------

  // Unique control identifier for label-input association via htmlFor/id.
  const controlId = fieldId ?? `field-${name}`;

  // Determine the effective input value: use the prop value in controlled mode
  // (onChange provided), or the internal state in uncontrolled mode.
  const effectiveValue = onChange !== undefined ? (value ?? '') : localValue;

  // Determine whether the field is in read-only access mode.
  // When access is 'readonly', the input is rendered with the readOnly attribute
  // and gets muted styling to indicate non-editable state.
  const isReadonly = access === 'readonly';

  // ---------------------------------------------------------------------------
  // Display Mode Rendering
  // ---------------------------------------------------------------------------

  if (mode === 'display') {
    // Determine if there is a meaningful value to display.
    const hasValue = value !== null && value !== undefined && value !== '';

    // Empty / null value: show the configurable empty value message.
    // Mirrors PcFieldBaseModel.EmptyValueMessage (default: "no data").
    if (!hasValue) {
      return (
        <span className="text-sm text-gray-500 italic">
          {emptyValueMessage}
        </span>
      );
    }

    // Link rendering when href is provided.
    // Mirrors PcFieldTextOptions.Href evaluated from the Link datasource.
    // The monolith's Display.cshtml passes Href to the wv-field-text tag
    // helper which renders an <a> element when Link is non-empty.
    if (href) {
      return (
        <a
          href={href}
          className="text-sm text-blue-600 hover:text-blue-800 hover:underline"
          rel="noopener noreferrer"
        >
          {value}
        </a>
      );
    }

    // Plain text display.
    return (
      <span className="text-sm text-gray-900">
        {value}
      </span>
    );
  }

  // ---------------------------------------------------------------------------
  // Edit Mode Rendering
  // ---------------------------------------------------------------------------

  // Build dynamic CSS class names for the input element using Tailwind CSS 4.x.
  // Applies conditional styling for error state, read-only state, and disabled state.
  const inputClassNames = [
    // Base input styling: full width, rounded corners, border, padding, text size, shadow
    'w-full',
    'rounded-md',
    'border',
    'px-3',
    'py-2',
    'text-sm',
    'shadow-sm',
    // Focus state: remove browser outline, add ring indicator
    'focus:outline-none',
    'focus:ring-1',
    // Conditional border and ring colors based on error state.
    // Error: red border and focus ring for visual error indication.
    // Normal: gray border with blue focus ring.
    error
      ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
      : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500',
    // Read-only state: muted background and not-allowed cursor.
    // Renders when access === 'readonly' per PcFieldBase access control.
    isReadonly ? 'bg-gray-50 cursor-not-allowed' : '',
    // Disabled state: grayed-out background, muted text, not-allowed cursor.
    disabled ? 'bg-gray-100 text-gray-500 cursor-not-allowed' : '',
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <input
      type="text"
      id={controlId}
      name={name}
      value={effectiveValue}
      onChange={handleChange}
      placeholder={placeholder}
      maxLength={maxLength != null ? maxLength : undefined}
      readOnly={isReadonly}
      disabled={disabled}
      required={required}
      className={inputClassNames}
      aria-invalid={Boolean(error)}
      aria-describedby={error ? `${name}-error` : undefined}
      aria-required={required}
    />
  );
}

// ---------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------

export default TextField;
