/**
 * RadioListField — Radio Button Group Field Component
 *
 * React replacement for the monolith's PcFieldRadioList ViewComponent
 * (WebVella.Erp.Web/Components/PcFieldRadioList/). Provides a radio
 * button group for single-value selection from a list of options.
 *
 *   - Edit mode: Vertical list of `<input type="radio">` elements
 *     with shared `name` attribute. Each radio is paired with a `<label>`
 *     for accessibility. Selection updates value via onChange callback.
 *   - Display mode: Shows the label text of the currently selected option,
 *     or the configured empty-value message when no selection is present.
 *   - Access control: 'forbidden' renders access-denied message,
 *     'readonly' renders as display mode with disabled styling.
 *
 * Source mapping:
 *   PcFieldRadioListOptions.Options → options prop (SelectOption[])
 *   PcFieldRadioListModel.Value     → value prop (string | null)
 *   PcFieldRadioListModel.Access    → access prop (WvFieldAccess)
 *   PcFieldRadioListModel.Required  → required prop (boolean)
 *   PcFieldBaseModel.EmptyValueMessage → emptyValueMessage prop
 *   PcFieldBaseModel.AccessDeniedMessage → accessDeniedMessage prop
 *
 * @module components/fields/RadioListField
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';
import type { SelectOption } from './SelectField';

// ---------------------------------------------------------------------------
// Exported Interface
// ---------------------------------------------------------------------------

/**
 * Props for the RadioListField component.
 *
 * Extends BaseFieldProps (omitting value/onChange for type narrowing) with
 * radio-list-specific properties derived from PcFieldRadioListModel.
 */
export interface RadioListFieldProps
  extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Currently selected radio value, or null if nothing is selected. */
  value: string | null;
  /** Callback invoked when a radio option is selected. */
  onChange?: (value: string) => void;
  /** List of available radio options. Each option has a value and label. */
  options: SelectOption[];
}

// ---------------------------------------------------------------------------
// RadioListField Component
// ---------------------------------------------------------------------------

/**
 * Radio button group for single selection from a list of options.
 *
 * Replaces the monolith's `<wv-field-radio-list>` tag helper and the
 * PcFieldRadioList ViewComponent's Display/Design modes.
 *
 * Rendering modes:
 *   - `mode === 'display'` → Read-only label text of the selected option
 *   - `mode === 'edit'`    → Vertical stack of radio+label pairs
 *
 * Access levels:
 *   - `'full'`      → Fully interactive radio buttons
 *   - `'readonly'`  → Disabled radio buttons (display mode forced)
 *   - `'forbidden'` → Access-denied message displayed
 *
 * @param props — RadioListFieldProps
 * @returns The rendered radio list field or null when not visible
 */
function RadioListField(props: RadioListFieldProps): React.JSX.Element | null {
  const {
    name,
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
    value = null,
    onChange,
    options = [],
  } = props;

  // ---- Internal State -----------------------------------------------------

  /**
   * Internal selected value state for uncontrolled usage. When an onChange
   * callback is provided, the component operates in controlled mode and
   * the external `value` prop is authoritative. When no onChange is
   * provided, this state tracks the local selection.
   */
  const [internalValue, setInternalValue] = useState<string | null>(value);

  // Determine effective value: prefer controlled (prop) over uncontrolled (state)
  const effectiveValue = onChange ? value : internalValue;

  // ---- Memoized Selected Option Label -------------------------------------

  /**
   * Derive the display label for the currently selected value by matching
   * against the options array. Returns null when no option matches.
   */
  const selectedLabel = useMemo<string | null>(() => {
    if (effectiveValue === null || effectiveValue === undefined) {
      return null;
    }
    const strValue = String(effectiveValue);
    if (strValue === '') {
      return null;
    }
    const matched = options.find(
      (opt: SelectOption) => opt.value === strValue
    );
    return matched ? matched.label : null;
  }, [effectiveValue, options]);

  // ---- Radio Change Handler -----------------------------------------------

  /**
   * Memoized change handler for radio button selection. Extracts the value
   * from the input element and propagates it to the parent via onChange
   * or updates internal state for uncontrolled mode.
   */
  const handleRadioChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>): void => {
      const selectedValue = event.target.value;
      if (onChange) {
        onChange(selectedValue);
      } else {
        setInternalValue(selectedValue);
      }
    },
    [onChange]
  );

  // ========================================================================
  // Visibility gate
  // ========================================================================

  if (isVisible === false) {
    return null;
  }

  // ========================================================================
  // Access-denied gate (for direct usage outside FieldRenderer)
  // ========================================================================

  if (access === 'forbidden') {
    return (
      <span
        className={`text-sm text-red-500 italic ${className ?? ''}`}
        role="alert"
      >
        {accessDeniedMessage}
      </span>
    );
  }

  // ========================================================================
  // Effective mode and disabled state
  // ========================================================================

  const isDisabled = disabled || access === 'readonly';
  const effectiveMode = access === 'readonly' ? 'display' : mode;

  // Unique control ID for label/input association
  const controlId = `field-${name ?? 'radio-list'}`;

  // ========================================================================
  // DISPLAY MODE
  // ========================================================================

  if (effectiveMode === 'display') {
    if (selectedLabel === null) {
      return (
        <span className={`text-sm text-gray-400 italic ${className ?? ''}`}>
          {emptyValueMessage}
        </span>
      );
    }

    return (
      <span
        className={`text-sm text-gray-900 ${className ?? ''}`}
      >
        {selectedLabel}
      </span>
    );
  }

  // ========================================================================
  // EDIT MODE — Vertical radio button group
  // ========================================================================

  // Handle empty options list gracefully
  if (options.length === 0) {
    return (
      <div className={className ?? ''}>
        <span className="text-sm text-gray-400 italic">
          {placeholder || 'No options available'}
        </span>
      </div>
    );
  }

  return (
    <fieldset
      className={className ?? ''}
      aria-required={required}
      aria-invalid={Boolean(error)}
      aria-describedby={
        [
          description ? `${controlId}-desc` : '',
          error ? `${controlId}-error` : '',
        ]
          .filter(Boolean)
          .join(' ') || undefined
      }
    >
      {/* Screen-reader accessible legend (visually hidden when label
          is rendered by FieldRenderer parent) */}
      {label && (
        <legend className="sr-only">{label}</legend>
      )}

      <div className="flex flex-col gap-2" role="radiogroup" aria-label={label ?? name ?? 'Radio list'}>
        {options.map((option: SelectOption, index: number) => {
          const optionId = `${controlId}-option-${index}`;
          const isChecked = effectiveValue === option.value;

          return (
            <label
              key={option.value}
              htmlFor={optionId}
              className={[
                'inline-flex items-center gap-2 cursor-pointer text-sm',
                isDisabled ? 'text-gray-400 cursor-not-allowed' : 'text-gray-900',
              ].join(' ')}
            >
              <input
                type="radio"
                id={optionId}
                name={name}
                value={option.value}
                checked={isChecked}
                onChange={handleRadioChange}
                disabled={isDisabled}
                required={required && index === 0}
                aria-checked={isChecked}
                className={[
                  'h-4 w-4 shrink-0 border transition-colors duration-150',
                  isDisabled
                    ? 'border-gray-300 bg-gray-100 text-gray-400 cursor-not-allowed'
                    : error
                      ? 'border-red-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-red-500 focus-visible:ring-offset-1'
                      : 'border-gray-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1',
                  'focus-visible:outline-none',
                ].join(' ')}
              />
              <span>{option.label}</span>
            </label>
          );
        })}
      </div>

      {/* Error message */}
      {error && (
        <p
          id={`${controlId}-error`}
          className="text-sm text-red-600 mt-1"
          role="alert"
        >
          {error}
        </p>
      )}

      {/* Description text */}
      {description && (
        <p
          id={`${controlId}-desc`}
          className="text-sm text-gray-500 mt-1"
        >
          {description}
        </p>
      )}
    </fieldset>
  );
}

export default RadioListField;
