/**
 * CheckboxListField — Checkbox Group for Multiple Selection
 *
 * React replacement for the monolith's PcFieldCheckboxList ViewComponent
 * (WebVella.Erp.Web/Components/PcFieldCheckboxList/). Renders a vertical
 * list of checkboxes that allows the user to select zero or more options
 * from a provided set.
 *
 * Mapping from the monolith:
 *   PcFieldCheckboxListModel.Options  → options prop (SelectOption[])
 *   PcFieldCheckboxListModel.Value    → value prop (string[] | null)
 *   PcFieldBaseModel.Access           → access prop (WvFieldAccess)
 *   PcFieldBaseModel.Required         → required prop
 *   PcFieldBaseModel.Placeholder      → placeholder prop
 *   PcFieldBaseModel.Description      → description prop
 *   PcFieldBaseOptions.Mode           → mode prop (FieldMode)
 *   PcFieldBaseOptions.LabelMode      → labelMode prop (WvLabelRenderMode)
 *   PcFieldBaseOptions.IsVisible      → isVisible prop
 *   PcFieldBaseModel.EmptyValueMessage→ emptyValueMessage prop
 *   PcFieldBaseModel.AccessDeniedMessage → accessDeniedMessage prop
 *   PcFieldBaseModel.Locale           → locale prop
 *
 * **Edit mode** — Vertical stack of `<input type="checkbox">` elements
 * each paired with a `<label>`. Toggling a checkbox adds or removes its
 * value from the string array and propagates via `onChange`.
 *
 * **Display mode** — Shows selected option labels as styled tags/badges
 * with comma-separated fallback when no options match, or shows
 * emptyValueMessage when nothing is selected.
 *
 * Accessibility:
 *   - `role="group"` with `aria-labelledby` on the checkbox container
 *   - Individual `<input>` + `<label>` pairs with matching `id`/`htmlFor`
 *   - `aria-invalid`, `aria-required`, `aria-describedby` on inputs
 *   - Keyboard navigable (native checkbox focus management)
 *   - `aria-disabled` for readonly state
 *
 * @module components/fields/CheckboxListField
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';
import type { SelectOption } from './SelectField';

// ---------------------------------------------------------------------------
// Exported Interface
// ---------------------------------------------------------------------------

/**
 * Props for the CheckboxListField component when used directly.
 *
 * Extends BaseFieldProps (omitting value/onChange for type narrowing) with
 * checkbox-list-specific properties derived from PcFieldCheckboxListModel.
 */
export interface CheckboxListFieldProps
  extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Array of selected option values, or null when nothing is selected. */
  value: string[] | null;
  /** Callback invoked with the updated selection array on toggle. */
  onChange?: (value: string[]) => void;
  /** Available options to display as checkboxes. */
  options: SelectOption[];
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Coerce an unknown value to a string array.
 *
 * Handles the following cases from the monolith's PcFieldCheckboxList:
 *   - null/undefined → empty array
 *   - string[] → passthrough
 *   - string (JSON) → parse as string array
 *   - string (CSV) → split by comma
 *   - any other type → empty array
 */
function coerceToStringArray(raw: unknown): string[] {
  if (raw === null || raw === undefined) {
    return [];
  }

  if (Array.isArray(raw)) {
    return raw.filter((item): item is string => typeof item === 'string');
  }

  if (typeof raw === 'string') {
    const trimmed = raw.trim();
    if (trimmed === '') {
      return [];
    }

    // Attempt JSON parse for array strings like '["a","b"]'
    if (trimmed.startsWith('[')) {
      try {
        const parsed: unknown = JSON.parse(trimmed);
        if (Array.isArray(parsed)) {
          return parsed.filter(
            (item): item is string => typeof item === 'string'
          );
        }
      } catch {
        // Fall through to CSV split
      }
    }

    // CSV-style: "value1,value2,value3"
    if (trimmed.includes(',') && !trimmed.includes('{') && !trimmed.includes('[')) {
      return trimmed.split(',').map((s) => s.trim()).filter(Boolean);
    }

    // Single value string
    return [trimmed];
  }

  return [];
}

// ---------------------------------------------------------------------------
// CheckboxListField Component
// ---------------------------------------------------------------------------

/**
 * CheckboxListField renders a group of checkboxes for multiple selection.
 *
 * The function signature accepts `BaseFieldProps` for compatibility with the
 * FieldRenderer FIELD_COMPONENT_MAP. Checkbox-list-specific props (options)
 * are extracted via type narrowing with safe defaults.
 *
 * @param props - BaseFieldProps with optional CheckboxListFieldProps extensions
 * @returns React element representing the checkbox list field
 */
function CheckboxListField(props: BaseFieldProps): React.JSX.Element | null {
  const {
    /* Identity */
    name,
    fieldId,

    /* Value — typed as unknown in BaseFieldProps, coerced below */
    value: rawValue,
    onChange: rawOnChange,

    /* Label — consumed by FieldRenderer wrapper; destructured to
       prevent leaking into rest-spread */
    label,
    labelMode,

    /* Mode and access */
    mode = 'edit',
    access = 'full',

    /* Validation */
    required = false,
    disabled = false,
    error,

    /* Appearance */
    className,
    placeholder,
    description,
    isVisible,

    /* Messages */
    accessDeniedMessage = 'access denied',
    emptyValueMessage = 'no data',
    locale,
  } = props;

  // -- Extract checkbox-list-specific props with defaults -----------------

  const listProps = props as unknown as Partial<
    Pick<CheckboxListFieldProps, 'options'>
  >;
  const options: SelectOption[] = listProps.options ?? [];

  // -- Derived state ------------------------------------------------------

  /** Coerce unknown value to string array for consistent logic */
  const selectedValues: string[] = useMemo(
    () => coerceToStringArray(rawValue),
    [rawValue]
  );

  /** Determine if the field should be effectively readonly */
  const isReadonly: boolean = access === 'readonly' || disabled;

  /** Unique id prefix for input/label association */
  const idPrefix: string = fieldId ?? `field-${name}`;

  /** Type-narrow onChange to string-array callback */
  const onChange = rawOnChange as ((value: string[]) => void) | undefined;

  // -- Internal state for uncontrolled fallback in edit mode --------------

  const [internalValues, setInternalValues] = useState<string[]>(selectedValues);

  /**
   * When value is provided (not null/undefined), use the prop-derived
   * array. Otherwise fall back to internal state for uncontrolled usage.
   */
  const effectiveValues: string[] = useMemo(
    () =>
      rawValue !== null && rawValue !== undefined
        ? selectedValues
        : internalValues,
    [rawValue, selectedValues, internalValues]
  );

  // -- Handlers -----------------------------------------------------------

  /**
   * Memoised toggle handler. Adds or removes the option value from the
   * selection array and propagates the updated array to the parent.
   */
  const handleToggle = useCallback(
    (optionValue: string) => {
      if (isReadonly) return;

      const isSelected = effectiveValues.includes(optionValue);
      const nextValues = isSelected
        ? effectiveValues.filter((v) => v !== optionValue)
        : [...effectiveValues, optionValue];

      setInternalValues(nextValues);
      onChange?.(nextValues);
    },
    [effectiveValues, isReadonly, onChange]
  );

  // -- Derive selected labels for display mode ----------------------------

  /**
   * Map selected values to their display labels from the options array.
   * Values without a matching option are rendered as-is (defensive).
   */
  const selectedLabels: string[] = useMemo(() => {
    if (effectiveValues.length === 0) return [];

    const labelMap = new Map<string, string>(
      options.map((opt) => [opt.value, opt.label])
    );

    return effectiveValues.map(
      (val) => labelMap.get(val) ?? val
    );
  }, [effectiveValues, options]);

  // -- Visibility guard ---------------------------------------------------
  // When isVisible is explicitly false the field should not render anything.
  // This mirrors PcFieldBaseOptions.IsVisible behaviour from the monolith
  // and matches the RadioListField / FieldRenderer visibility pattern.

  if (isVisible === false) {
    return null;
  }

  // -- Access denied guard ------------------------------------------------

  if (access === 'forbidden') {
    return (
      <div
        className={`flex items-center gap-2 text-sm text-gray-400 italic${
          className ? ` ${className}` : ''
        }`}
        role="status"
        aria-label={accessDeniedMessage}
      >
        {/* Lock icon */}
        <svg
          xmlns="http://www.w3.org/2000/svg"
          viewBox="0 0 20 20"
          fill="currentColor"
          className="h-4 w-4 shrink-0"
          aria-hidden="true"
        >
          <path
            fillRule="evenodd"
            d="M10 1a4.5 4.5 0 0 0-4.5 4.5V9H5a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-6a2 2 0 0 0-2-2h-.5V5.5A4.5 4.5 0 0 0 10 1Zm3 8V5.5a3 3 0 1 0-6 0V9h6Z"
            clipRule="evenodd"
          />
        </svg>
        <span>{accessDeniedMessage}</span>
      </div>
    );
  }

  // -- Display mode -------------------------------------------------------

  if (mode === 'display') {
    // Empty selection
    if (selectedLabels.length === 0) {
      return (
        <div
          className={`text-sm text-gray-400 italic${
            className ? ` ${className}` : ''
          }`}
          data-field-name={name}
          data-field-mode="display"
        >
          {emptyValueMessage}
        </div>
      );
    }

    // Render selected options as styled tags/badges
    return (
      <div
        className={`flex flex-wrap items-center gap-1.5${
          className ? ` ${className}` : ''
        }`}
        data-field-name={name}
        data-field-mode="display"
      >
        {selectedLabels.map((labelText, idx) => (
          <span
            key={`${effectiveValues[idx]}-${idx}`}
            className="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800"
          >
            {labelText}
          </span>
        ))}
      </div>
    );
  }

  // -- Edit mode ----------------------------------------------------------

  return (
    <div
      className={`flex flex-col gap-2${
        className ? ` ${className}` : ''
      }`}
      data-field-name={name}
      data-field-mode="edit"
      role="group"
      aria-labelledby={label ? `${idPrefix}-group-label` : undefined}
      aria-required={required || undefined}
    >
      {/* Screen-reader-only group label for the checkbox group */}
      {label && (
        <span id={`${idPrefix}-group-label`} className="sr-only">
          {label}
        </span>
      )}

      {/* Empty options fallback */}
      {options.length === 0 && (
        <span className="text-sm text-gray-400 italic">
          {placeholder || 'No options available'}
        </span>
      )}

      {/* Checkbox list */}
      {options.map((option, idx) => {
        const inputId = `${idPrefix}-option-${idx}`;
        const isChecked = effectiveValues.includes(option.value);

        return (
          <div
            key={option.value}
            className="relative flex items-start"
          >
            <div className="flex h-6 items-center">
              <input
                id={inputId}
                name={`${name}[]`}
                type="checkbox"
                value={option.value}
                checked={isChecked}
                onChange={() => handleToggle(option.value)}
                disabled={isReadonly}
                className={[
                  'h-4 w-4 rounded border-gray-300 text-blue-600',
                  'focus-visible:ring-blue-500 focus-visible:ring-2 focus-visible:ring-offset-0',
                  isReadonly
                    ? 'cursor-not-allowed bg-gray-100 opacity-60'
                    : 'cursor-pointer',
                  error ? 'border-red-500' : '',
                ]
                  .filter(Boolean)
                  .join(' ')}
                aria-invalid={error ? true : undefined}
                aria-describedby={
                  error
                    ? `${idPrefix}-error`
                    : description
                      ? `${idPrefix}-description`
                      : undefined
                }
              />
            </div>
            <div className="ms-3 text-sm leading-6">
              <label
                htmlFor={inputId}
                className={[
                  'font-medium',
                  isReadonly
                    ? 'text-gray-400 cursor-not-allowed'
                    : 'text-gray-900 cursor-pointer',
                ].join(' ')}
              >
                {option.label}
              </label>
            </div>
          </div>
        );
      })}

      {/* Description text below the checkbox group */}
      {description && (
        <p
          id={`${idPrefix}-description`}
          className="text-sm text-gray-500"
        >
          {description}
        </p>
      )}

      {/* Error message */}
      {error && (
        <p
          id={`${idPrefix}-error`}
          className="text-sm text-red-600"
          role="alert"
        >
          {error}
        </p>
      )}
    </div>
  );
}

export default CheckboxListField;
