/**
 * CheckboxGridField — Multi-dimensional Checkbox Grid Field Component
 *
 * React replacement for the monolith's PcFieldCheckboxGrid ViewComponent
 * (WebVella.Erp.Web/Components/PcFieldCheckboxGrid/). Renders a grid of
 * checkboxes arranged by rows and columns from SelectOption arrays, with:
 *
 *   - Edit mode: HTML table with checkboxes at each row × column intersection
 *   - Display mode: Same table layout with textTrue/textFalse or ✓/✗ indicators
 *   - Configurable textTrue and textFalse labels per cell state
 *   - KeyStringList value structure tracking checked column values per row
 *   - Full keyboard accessibility and ARIA labelling
 *
 * Source mapping:
 *   PcFieldCheckboxGridOptions.TextTrue    → textTrue prop (default "")
 *   PcFieldCheckboxGridOptions.TextFalse   → textFalse prop (default "")
 *   PcFieldCheckboxGridOptions.Rows        → rows prop (SelectOption[])
 *   PcFieldCheckboxGridOptions.Columns     → columns prop (SelectOption[])
 *   PcFieldCheckboxGridModel.Value         → value prop (KeyStringList[] | null)
 *   PcFieldCheckboxGridModel.Rows          → rows prop
 *   PcFieldCheckboxGridModel.Columns       → columns prop
 *
 * @module components/fields/CheckboxGridField
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';
import type { SelectOption } from './SelectField';

// ---------------------------------------------------------------------------
// Exported Interfaces
// ---------------------------------------------------------------------------

/**
 * Represents a single row entry in the checkbox grid value structure.
 *
 * Maps to the monolith's `KeyStringList` class where:
 *   - `key` identifies the row (matches a SelectOption.value from the rows array)
 *   - `values` is the list of checked column values (SelectOption.value from columns)
 *
 * Example: { key: "row1", values: ["colA", "colC"] } means row1 has columns A and C checked.
 */
export interface KeyStringList {
  /** Row identifier — matches a SelectOption.value from the rows array. */
  key: string;
  /** Array of checked column values — each matches a SelectOption.value from the columns array. */
  values: string[];
}

/**
 * Props for the CheckboxGridField component when used directly (not via
 * FieldRenderer). Extends BaseFieldProps with grid-specific value,
 * onChange typing, row/column definitions, and text label configuration.
 *
 * When CheckboxGridField is consumed through FieldRenderer's lazy-loaded
 * map, it receives BaseFieldProps and internally narrows the types.
 */
export interface CheckboxGridFieldProps
  extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Grid state: array of { key, values } entries tracking checked cells per row. */
  value: KeyStringList[] | null;
  /** Callback invoked when any checkbox in the grid is toggled. */
  onChange?: (value: KeyStringList[]) => void;
  /** Row definitions — each SelectOption provides a value (row key) and label (row header). */
  rows: SelectOption[];
  /** Column definitions — each SelectOption provides a value (column key) and label (column header). */
  columns: SelectOption[];
  /** Text displayed for checked cells in display mode (default: shows ✓ icon when empty). */
  textTrue?: string;
  /** Text displayed for unchecked cells in display mode (default: shows ✗ icon when empty). */
  textFalse?: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Coerce an unknown raw value into a KeyStringList array.
 *
 * Handles the same input variations as the monolith's PcFieldCheckboxGrid
 * InvokeAsync method:
 *   - null/undefined → empty array
 *   - KeyStringList[] → pass through
 *   - JSON string → parse to KeyStringList[]
 *   - anything else → empty array
 */
function coerceToKeyStringList(raw: unknown): KeyStringList[] {
  if (raw === null || raw === undefined) {
    return [];
  }

  if (Array.isArray(raw)) {
    /* Validate that each item has at minimum `key` and `values` properties */
    return raw.filter(
      (item): item is KeyStringList =>
        item !== null &&
        typeof item === 'object' &&
        typeof (item as KeyStringList).key === 'string' &&
        Array.isArray((item as KeyStringList).values)
    );
  }

  if (typeof raw === 'string') {
    const trimmed = raw.trim();
    if (trimmed === '') {
      return [];
    }
    if (trimmed.startsWith('[') || trimmed.startsWith('{')) {
      try {
        const parsed: unknown = JSON.parse(trimmed);
        if (Array.isArray(parsed)) {
          return parsed.filter(
            (item): item is KeyStringList =>
              item !== null &&
              typeof item === 'object' &&
              typeof (item as KeyStringList).key === 'string' &&
              Array.isArray((item as KeyStringList).values)
          );
        }
      } catch {
        /* JSON parse failure — fall through to empty array */
      }
    }
  }

  return [];
}

/**
 * Build a lookup map from a KeyStringList array for O(1) checked-state
 * resolution at each grid intersection.
 *
 * Returns Map<rowKey, Set<columnValue>>.
 */
function buildCheckedMap(
  items: KeyStringList[]
): Map<string, Set<string>> {
  const map = new Map<string, Set<string>>();
  for (const item of items) {
    map.set(item.key, new Set(item.values));
  }
  return map;
}

// ---------------------------------------------------------------------------
// CheckboxGridField Component
// ---------------------------------------------------------------------------

/**
 * CheckboxGridField renders a grid of checkboxes with rows × columns.
 *
 * **Edit mode** — HTML `<table>` with:
 *   - Column headers from the `columns` SelectOption array
 *   - Row labels from the `rows` SelectOption array
 *   - Native `<input type="checkbox">` at each intersection
 *   - Toggle adds/removes the column value from the row's values array
 *
 * **Display mode** — Same table layout but checkboxes replaced with:
 *   - `textTrue` label (or ✓ icon when empty) for checked cells
 *   - `textFalse` label (or ✗ icon when empty) for unchecked cells
 *
 * The component respects `access` (readonly / forbidden), `disabled`,
 * `required`, and `error` props inherited from BaseFieldProps.
 *
 * The function signature uses BaseFieldProps for compatibility with the
 * FieldRenderer FIELD_COMPONENT_MAP. Grid-specific props (rows, columns,
 * textTrue, textFalse) are extracted with safe defaults.
 */
function CheckboxGridField(props: BaseFieldProps): React.JSX.Element {
  const {
    /* Identity */
    name,
    fieldId,

    /* Value — typed as unknown in BaseFieldProps, coerced below */
    value: rawValue,
    onChange: rawOnChange,

    /* Label — consumed by FieldRenderer wrapper; destructured here
       for completeness so rest-spread does not leak them */
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

  /* ---- Extract grid-specific props with defaults -------------------- */

  const gridProps = props as unknown as Partial<
    Pick<
      CheckboxGridFieldProps,
      'rows' | 'columns' | 'textTrue' | 'textFalse' | 'value' | 'onChange'
    >
  >;

  const rows: SelectOption[] = gridProps.rows ?? [];
  const columns: SelectOption[] = gridProps.columns ?? [];
  const textTrue: string = gridProps.textTrue ?? '';
  const textFalse: string = gridProps.textFalse ?? '';

  /* ---- Derived state ------------------------------------------------ */

  /** Coerce unknown value to KeyStringList[] for consistent logic */
  const parsedValue: KeyStringList[] = useMemo(
    () => coerceToKeyStringList(rawValue),
    [rawValue]
  );

  /** Internal state for uncontrolled fallback in edit mode */
  const [internalValue, setInternalValue] = useState<KeyStringList[]>(
    parsedValue
  );

  /**
   * When value is provided (not null/undefined), use the prop-derived
   * array. Otherwise fall back to internal state for uncontrolled usage.
   */
  const effectiveValue: KeyStringList[] =
    rawValue !== null && rawValue !== undefined ? parsedValue : internalValue;

  /**
   * Lookup map: rowKey → Set<columnValue> for O(1) checked-state
   * resolution at each grid intersection.
   */
  const checkedMap = useMemo(
    () => buildCheckedMap(effectiveValue),
    [effectiveValue]
  );

  /** Determine if the field should be effectively readonly */
  const isReadonly: boolean = access === 'readonly' || disabled;

  /** Unique id prefix for input/label association */
  const inputId: string = fieldId ?? `field-${name}`;

  /** Type-narrow onChange to KeyStringList[] callback */
  const onChange = rawOnChange as
    | ((value: KeyStringList[]) => void)
    | undefined;

  /* ---- Helpers ------------------------------------------------------- */

  /**
   * Determine if a specific grid cell (row × column) is checked.
   */
  const isCellChecked = useCallback(
    (rowKey: string, colValue: string): boolean => {
      return checkedMap.get(rowKey)?.has(colValue) ?? false;
    },
    [checkedMap]
  );

  /* ---- Toggle handler ----------------------------------------------- */

  /**
   * Memoised toggle handler. Adds or removes a column value from the
   * corresponding row entry in the KeyStringList array.
   *
   * If the row has no entry yet, a new KeyStringList item is created.
   * If after removal the row's values array is empty, the row entry is
   * preserved (with an empty values array) to maintain structural
   * consistency with the monolith's behaviour.
   */
  const handleToggle = useCallback(
    (rowKey: string, colValue: string): void => {
      if (isReadonly) return;

      /* Deep-clone the effective value to avoid mutation */
      const nextValue: KeyStringList[] = effectiveValue.map((item) => ({
        key: item.key,
        values: [...item.values],
      }));

      const rowItem = nextValue.find((r) => r.key === rowKey);

      if (rowItem) {
        const colIndex = rowItem.values.indexOf(colValue);
        if (colIndex >= 0) {
          /* Uncheck: remove column value from the row's values */
          rowItem.values.splice(colIndex, 1);
        } else {
          /* Check: add column value to the row's values */
          rowItem.values.push(colValue);
        }
      } else {
        /* Row has no entry yet — create one with the toggled column checked */
        nextValue.push({ key: rowKey, values: [colValue] });
      }

      setInternalValue(nextValue);
      onChange?.(nextValue);
    },
    [effectiveValue, isReadonly, onChange]
  );

  /* ---- Access denied guard ------------------------------------------ */

  if (access === 'forbidden') {
    return (
      <div
        className={`flex items-center gap-2 text-sm text-gray-400 italic${
          className ? ` ${className}` : ''
        }`}
        role="status"
        aria-label={accessDeniedMessage}
      >
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

  /* ---- Empty grid guard --------------------------------------------- */

  if (rows.length === 0 || columns.length === 0) {
    return (
      <div
        className={`text-sm text-gray-500 italic${
          className ? ` ${className}` : ''
        }`}
        data-field-name={name}
        role="status"
      >
        {emptyValueMessage}
      </div>
    );
  }

  /* ---- Display mode ------------------------------------------------- */

  if (mode === 'display') {
    return (
      <div
        className={className ?? ''}
        data-field-name={name}
        data-field-mode="display"
      >
        <div className="overflow-x-auto">
          <table
            className="min-w-full border-collapse text-sm"
            role="grid"
            aria-label={label ?? name}
            aria-readonly="true"
          >
            <thead>
              <tr>
                {/* Empty top-left corner cell */}
                <th
                  className="border border-gray-200 bg-gray-50 px-3 py-2"
                  scope="col"
                >
                  <span className="sr-only">Row labels</span>
                </th>
                {columns.map((col) => (
                  <th
                    key={col.value}
                    scope="col"
                    className="border border-gray-200 bg-gray-50 px-3 py-2 text-center font-medium text-gray-700"
                  >
                    {col.label}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.value}>
                  <th
                    scope="row"
                    className="border border-gray-200 bg-gray-50 px-3 py-2 text-start font-medium text-gray-700"
                  >
                    {row.label}
                  </th>
                  {columns.map((col) => {
                    const checked = isCellChecked(row.value, col.value);
                    return (
                      <td
                        key={col.value}
                        className="border border-gray-200 px-3 py-2 text-center"
                      >
                        {checked ? (
                          textTrue ? (
                            <span className="text-green-700">{textTrue}</span>
                          ) : (
                            <span
                              className="inline-flex h-5 w-5 items-center justify-center rounded-full bg-green-100 text-green-600 mx-auto"
                              aria-label={`${row.label} / ${col.label}: checked`}
                            >
                              <svg
                                xmlns="http://www.w3.org/2000/svg"
                                viewBox="0 0 20 20"
                                fill="currentColor"
                                className="h-3.5 w-3.5"
                                aria-hidden="true"
                              >
                                <path
                                  fillRule="evenodd"
                                  d="M16.704 4.153a.75.75 0 0 1 .143 1.052l-8 10.5a.75.75 0 0 1-1.127.075l-4.5-4.5a.75.75 0 0 1 1.06-1.06l3.894 3.893 7.48-9.817a.75.75 0 0 1 1.05-.143Z"
                                  clipRule="evenodd"
                                />
                              </svg>
                            </span>
                          )
                        ) : textFalse ? (
                          <span className="text-gray-500">{textFalse}</span>
                        ) : (
                          <span
                            className="inline-flex h-5 w-5 items-center justify-center rounded-full bg-gray-100 text-gray-400 mx-auto"
                            aria-label={`${row.label} / ${col.label}: unchecked`}
                          >
                            <svg
                              xmlns="http://www.w3.org/2000/svg"
                              viewBox="0 0 20 20"
                              fill="currentColor"
                              className="h-3.5 w-3.5"
                              aria-hidden="true"
                            >
                              <path d="M6.28 5.22a.75.75 0 0 0-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 1 0 1.06 1.06L10 11.06l3.72 3.72a.75.75 0 1 0 1.06-1.06L11.06 10l3.72-3.72a.75.75 0 0 0-1.06-1.06L10 8.94 6.28 5.22Z" />
                            </svg>
                          </span>
                        )}
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        {/* Description text below the grid */}
        {description && (
          <p
            id={`${inputId}-description`}
            className="mt-1 text-sm text-gray-500"
          >
            {description}
          </p>
        )}
      </div>
    );
  }

  /* ---- Edit mode ---------------------------------------------------- */

  return (
    <div
      className={className ?? ''}
      data-field-name={name}
      data-field-mode="edit"
    >
      <div className="overflow-x-auto">
        <table
          className="min-w-full border-collapse text-sm"
          role="grid"
          aria-label={label ?? name}
          aria-required={required || undefined}
        >
          <thead>
            <tr>
              {/* Empty top-left corner cell */}
              <th
                className="border border-gray-200 bg-gray-50 px-3 py-2"
                scope="col"
              >
                <span className="sr-only">Row labels</span>
              </th>
              {columns.map((col) => (
                <th
                  key={col.value}
                  scope="col"
                  className="border border-gray-200 bg-gray-50 px-3 py-2 text-center font-medium text-gray-700"
                >
                  {col.label}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.value}>
                <th
                  scope="row"
                  className="border border-gray-200 bg-gray-50 px-3 py-2 text-start font-medium text-gray-700"
                >
                  {row.label}
                </th>
                {columns.map((col) => {
                  const checked = isCellChecked(row.value, col.value);
                  const cellId = `${inputId}-${row.value}-${col.value}`;
                  return (
                    <td
                      key={col.value}
                      className="border border-gray-200 px-3 py-2 text-center"
                    >
                      <div className="flex items-center justify-center">
                        <input
                          id={cellId}
                          name={`${name}[${row.value}][${col.value}]`}
                          type="checkbox"
                          checked={checked}
                          onChange={() => handleToggle(row.value, col.value)}
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
                          aria-label={`${row.label} / ${col.label}`}
                          aria-checked={checked}
                        />
                      </div>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Error message */}
      {error && (
        <p
          id={`${inputId}-error`}
          className="mt-1 text-sm text-red-600"
          role="alert"
        >
          {error}
        </p>
      )}

      {/* Description text below the grid */}
      {description && (
        <p
          id={`${inputId}-description`}
          className="mt-1 text-sm text-gray-500"
        >
          {description}
        </p>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------

export default CheckboxGridField;
