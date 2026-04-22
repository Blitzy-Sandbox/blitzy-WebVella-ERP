/**
 * FilterField — Dynamic Filter Controls per Field Type
 *
 * Replaces the monolith's `PcGridFilterField` ViewComponent
 * (`WebVella.Erp.Web/Components/PcGridFilterField/`) and ALL 20+ `WvFilter*`
 * TagHelper folders (`WvFilterText`, `WvFilterNumber`, `WvFilterDate`,
 * `WvFilterDateTime`, `WvFilterSelect`, `WvFilterMultiSelect`,
 * `WvFilterCheckbox`, `WvFilterAutonumber`, `WvFilterCurrency`,
 * `WvFilterPercent`, `WvFilterEmail`, `WvFilterPhone`, `WvFilterUrl`,
 * `WvFilterHtml`, `WvFilterTextarea`, `WvFilterFile`, `WvFilterImage`,
 * `WvFilterGuid`) with a single dynamic React component.
 *
 * Source mapping:
 *  - PcGridFilterField.cs options        → FilterFieldProps interface
 *  - Display.cshtml FieldType switch     → fieldType-based renderValueInput()
 *  - WvFilterBase.cs URL binding         → useSearchParams URL param management
 *  - WvFilterBase/base.js jQuery init    → React state + useCallback handlers
 *  - WvFilterText/Number/Date/etc.       → field-type-specific input rendering
 *
 * Key design decisions:
 *  - URL search-params (`{prefix}q_{name}_t`, `_v`, `_v2`) are the source of
 *    truth for "applied" filter state, matching the monolith's
 *    `WvFilterBase.cs` query-string binding convention (lines 74-76).
 *  - Local `useState` tracks the user's in-progress edits for responsive UI;
 *    values are committed to URL params on blur / Enter / select-change.
 *  - `const enum` members from `FieldType` and `FilterType` are used directly
 *    in switch/case and computed-property-key positions; since they are fully
 *    inlined at compile time no runtime enum objects are needed.
 *  - All styling uses Tailwind CSS 4 utility classes — zero Bootstrap.
 *
 * @module FilterField
 */

import {
  useState,
  useCallback,
  useMemo,
  type ReactElement,
  type ChangeEvent,
} from 'react';
import { useSearchParams } from 'react-router-dom';
import { FieldType } from '../../types/entity';
import { FilterType } from '../../types/filter';

/* ═══════════════════════════════════════════════════════════════════
 * Constants
 * ═══════════════════════════════════════════════════════════════════ */

/**
 * Human-readable labels for every {@link FilterType} value.
 *
 * Mirrors the C# `FilterType.GetLabel()` extension method output that
 * `WvFilterBase.cs` uses when building operator `<option>` text (line 285).
 */
const FILTER_TYPE_LABELS: Record<number, string> = {
  [FilterType.Undefined]: '',
  [FilterType.STARTSWITH]: 'starts with',
  [FilterType.CONTAINS]: 'contains',
  [FilterType.EQ]: '=',
  [FilterType.NOT]: '\u2260',
  [FilterType.LT]: '<',
  [FilterType.LTE]: '\u2264',
  [FilterType.GT]: '>',
  [FilterType.GTE]: '\u2265',
  [FilterType.REGEX]: 'regex',
  [FilterType.FTS]: 'fts',
  [FilterType.BETWEEN]: 'between',
  [FilterType.NOTBETWEEN]: 'not between',
};

/* ═══════════════════════════════════════════════════════════════════
 * Helper functions
 * ═══════════════════════════════════════════════════════════════════ */

/**
 * Return the default set of filter operators for a given field type.
 *
 * Replicates `DataUtils.GetFilterTypesForFieldType(fieldType)` called by
 * `WvFilterBase.cs` lines 193-196 when `QueryOptions` is empty.
 */
function getDefaultQueryOptions(fieldType: FieldType): FilterType[] {
  switch (fieldType) {
    /* ── Text-like types ─────────────────────────────────────────── */
    case FieldType.TextField:
    case FieldType.MultiLineTextField:
    case FieldType.EmailField:
    case FieldType.PhoneField:
    case FieldType.UrlField:
    case FieldType.HtmlField:
      return [
        FilterType.STARTSWITH,
        FilterType.CONTAINS,
        FilterType.EQ,
        FilterType.NOT,
        FilterType.REGEX,
        FilterType.FTS,
      ];

    /* ── Numeric types ───────────────────────────────────────────── */
    case FieldType.NumberField:
    case FieldType.CurrencyField:
    case FieldType.PercentField:
    case FieldType.AutoNumberField:
      return [
        FilterType.EQ,
        FilterType.NOT,
        FilterType.LT,
        FilterType.LTE,
        FilterType.GT,
        FilterType.GTE,
        FilterType.BETWEEN,
        FilterType.NOTBETWEEN,
      ];

    /* ── Date / DateTime ─────────────────────────────────────────── */
    case FieldType.DateField:
    case FieldType.DateTimeField:
      return [
        FilterType.EQ,
        FilterType.NOT,
        FilterType.LT,
        FilterType.LTE,
        FilterType.GT,
        FilterType.GTE,
        FilterType.BETWEEN,
        FilterType.NOTBETWEEN,
      ];

    /* ── Checkbox → only equality ────────────────────────────────── */
    case FieldType.CheckboxField:
      return [FilterType.EQ];

    /* ── Select / MultiSelect ────────────────────────────────────── */
    case FieldType.SelectField:
    case FieldType.MultiSelectField:
      return [FilterType.EQ, FilterType.NOT];

    /* ── GUID ────────────────────────────────────────────────────── */
    case FieldType.GuidField:
      return [FilterType.EQ, FilterType.NOT];

    /* ── File / Image (exists / not-exists) ──────────────────────── */
    case FieldType.FileField:
    case FieldType.ImageField:
      return [FilterType.EQ, FilterType.NOT];

    /* ── Fallback for unknown / unhandled types ──────────────────── */
    default:
      return [FilterType.CONTAINS, FilterType.EQ, FilterType.NOT];
  }
}

/**
 * Choose the default filter operator when none is explicitly set.
 *
 * Matches `WvFilterBase.cs` lines 198-209:
 *   1. Prefer `FilterType.EQ` if it is in the available options.
 *   2. Otherwise fall back to the first available option.
 *   3. If options are empty, return `FilterType.Undefined`.
 */
function resolveDefaultOperator(options: FilterType[]): FilterType {
  if (options.length === 0) return FilterType.Undefined;
  if (options.includes(FilterType.EQ)) return FilterType.EQ;
  return options[0];
}

/**
 * Returns `true` when the operator is `BETWEEN` or `NOTBETWEEN`,
 * indicating that a secondary value input should be visible.
 */
function isBetweenOperator(op: FilterType): boolean {
  return op === FilterType.BETWEEN || op === FilterType.NOTBETWEEN;
}

/**
 * Safely parse a string to a {@link FilterType} number.
 * Returns `FilterType.Undefined` when the string is not a valid integer.
 */
function parseFilterType(raw: string | null): FilterType {
  if (raw === null) return FilterType.Undefined;
  const n = parseInt(raw, 10);
  return isNaN(n) ? FilterType.Undefined : (n as FilterType);
}

/* ═══════════════════════════════════════════════════════════════════
 * Public Interfaces
 * ═══════════════════════════════════════════════════════════════════ */

/**
 * Option descriptor for `<select>`-type filters (Select / MultiSelect fields).
 *
 * Mirrors C# `SelectOption` from `WebVella.Erp.Web.Models`.
 */
export interface SelectOption {
  /** The option value submitted in the filter query string. */
  value: string;
  /** Human-readable label displayed in the dropdown. */
  label: string;
  /** Optional CSS icon class for the option (e.g. `"fas fa-tag"`). */
  iconClass?: string;
  /** Optional colour hint for visual indicators. */
  color?: string;
}

/**
 * Props for the {@link FilterField} component.
 *
 * Maps 1 : 1 to `PcGridFilterFieldOptions` from
 * `PcGridFilterField.cs` lines 25-49 with React-appropriate types.
 */
export interface FilterFieldProps {
  /**
   * Field name — used for URL query-string parameter keys:
   * `{prefix}q_{name}_t`, `{prefix}q_{name}_v`, `{prefix}q_{name}_v2`.
   */
  name: string;
  /** Display label shown above the filter control. Falls back to `name`. */
  label?: string;
  /**
   * Determines which filter UI to render (text input, number input,
   * date picker, checkbox tri-state, select dropdown, etc.).
   */
  fieldType: FieldType;
  /** Currently selected filter operator type (e.g. CONTAINS, EQ, BETWEEN). */
  queryType?: FilterType;
  /** Available filter operator choices for the operator dropdown. */
  queryOptions?: FilterType[];
  /**
   * Query-string prefix for namespacing when multiple grids coexist on
   * the same page (e.g. `"grid1_"`).
   */
  prefix?: string;
  /** Options for Select / MultiSelect field type filters. */
  valueOptions?: SelectOption[];
  /** Whether this filter field is visible. Defaults to `true`. */
  isVisible?: boolean;
  /** Controlled primary filter value. */
  value?: string;
  /** Controlled secondary filter value (for BETWEEN / NOTBETWEEN). */
  value2?: string;
  /**
   * Callback fired when any filter parameter changes.
   * The parent can use this to apply filters, update API queries, etc.
   */
  onChange?: (
    name: string,
    filterType: FilterType,
    value: string,
    value2?: string,
  ) => void;
  /** Callback fired when the user clears this filter. */
  onClear?: (name: string) => void;
}

/* ═══════════════════════════════════════════════════════════════════
 * Tailwind class tokens (shared across render helpers)
 * ═══════════════════════════════════════════════════════════════════ */

/** Base classes for primary & secondary value `<input>` / `<select>`. */
const VALUE_INPUT_BASE =
  'flex-1 min-w-0 border border-gray-300 px-3 py-1.5 text-sm ' +
  'focus:border-blue-500 focus:ring-1 focus:ring-blue-500 focus:outline-none';

/** End-rounded modifier applied when there is no secondary input visible. */
const ROUNDED_END = 'rounded-e';

/* ═══════════════════════════════════════════════════════════════════
 * Component
 * ═══════════════════════════════════════════════════════════════════ */

/**
 * Dynamic filter-field component that renders the correct filter UI for
 * any of the 18+ supported entity field types.
 *
 * Returns `null` when `isVisible` is `false`.
 */
export function FilterField({
  name,
  label,
  fieldType,
  queryType: propQueryType,
  queryOptions: propQueryOptions,
  prefix = '',
  valueOptions,
  isVisible = true,
  value: propValue,
  value2: propValue2,
  onChange,
  onClear,
}: FilterFieldProps): ReactElement | null {
  /* ── Early exit ─────────────────────────────────────────────── */
  if (!isVisible) return null;

  /* ── URL search-param integration ───────────────────────────── */
  const [searchParams, setSearchParams] = useSearchParams();

  /**
   * Query-string parameter keys matching the monolith's
   * `WvFilterBase.cs` convention (lines 74-76).
   */
  const typeKey = `${prefix}q_${name}_t`;
  const valueKey = `${prefix}q_${name}_v`;
  const value2Key = `${prefix}q_${name}_v2`;

  /* ── Derived: effective query options ────────────────────────── */
  const effectiveOptions: FilterType[] = useMemo(() => {
    if (propQueryOptions && propQueryOptions.length > 0) {
      return propQueryOptions;
    }
    return getDefaultQueryOptions(fieldType);
  }, [propQueryOptions, fieldType]);

  /* ── Local state for user-editable values ───────────────────── */

  /**
   * Initialise local state from (in priority order):
   *   1. Controlled props (value / value2 / queryType)
   *   2. URL search-params
   *   3. Computed defaults
   *
   * `useState` lazy initialisers ensure we only read `searchParams`
   * once on mount; subsequent URL changes are handled by the parent
   * re-mounting or re-keying the filter field.
   */
  const [localOperator, setLocalOperator] = useState<FilterType>(() => {
    if (
      propQueryType !== undefined &&
      propQueryType !== FilterType.Undefined
    ) {
      return propQueryType;
    }
    const urlType = parseFilterType(searchParams.get(typeKey));
    if (urlType !== FilterType.Undefined) return urlType;
    return resolveDefaultOperator(effectiveOptions);
  });

  const [localValue, setLocalValue] = useState<string>(
    () => propValue ?? searchParams.get(valueKey) ?? '',
  );

  const [localValue2, setLocalValue2] = useState<string>(
    () => propValue2 ?? searchParams.get(value2Key) ?? '',
  );

  /* ── Derived booleans ───────────────────────────────────────── */
  const showSecondary = isBetweenOperator(localOperator);
  const hasClearableValue = localValue !== '' || localValue2 !== '';

  /* ── Commit helper — writes local state to URL params ───────── */
  const commitToUrl = useCallback(
    (op: FilterType, val: string, val2: string) => {
      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev);

          /* operator */
          next.set(typeKey, String(op));

          /* primary value */
          if (val) {
            next.set(valueKey, val);
          } else {
            next.delete(valueKey);
          }

          /* secondary value — only for BETWEEN / NOTBETWEEN */
          if (isBetweenOperator(op) && val2) {
            next.set(value2Key, val2);
          } else {
            next.delete(value2Key);
          }

          return next;
        },
        { replace: true },
      );
    },
    [setSearchParams, typeKey, valueKey, value2Key],
  );

  /* ── Event handlers ─────────────────────────────────────────── */

  /** Operator `<select>` change — commit immediately. */
  const handleOperatorChange = useCallback(
    (e: ChangeEvent<HTMLSelectElement>) => {
      const newOp = parseInt(e.target.value, 10) as FilterType;
      const wasBetween = isBetweenOperator(localOperator);
      const nowBetween = isBetweenOperator(newOp);

      setLocalOperator(newOp);

      /* When switching away from BETWEEN, clear secondary value. */
      let effectiveVal2 = localValue2;
      if (wasBetween && !nowBetween) {
        setLocalValue2('');
        effectiveVal2 = '';
      }

      commitToUrl(newOp, localValue, effectiveVal2);
      onChange?.(
        name,
        newOp,
        localValue,
        nowBetween ? effectiveVal2 : undefined,
      );
    },
    [localOperator, localValue, localValue2, commitToUrl, onChange, name],
  );

  /** Primary value change — update local state (responsive typing). */
  const handleValueChange = useCallback(
    (e: ChangeEvent<HTMLInputElement | HTMLSelectElement>) => {
      setLocalValue(e.target.value);
    },
    [],
  );

  /**
   * Primary value commit (blur / Enter) — flush to URL + notify parent.
   * For `<select>` elements the commit is immediate (no blur needed).
   */
  const handleValueCommit = useCallback(
    (newVal?: string) => {
      const val = newVal ?? localValue;
      commitToUrl(
        localOperator,
        val,
        showSecondary ? localValue2 : '',
      );
      onChange?.(
        name,
        localOperator,
        val,
        showSecondary ? localValue2 || undefined : undefined,
      );
    },
    [localOperator, localValue, localValue2, showSecondary, commitToUrl, onChange, name],
  );

  /** Secondary value change — update local state. */
  const handleValue2Change = useCallback(
    (e: ChangeEvent<HTMLInputElement>) => {
      setLocalValue2(e.target.value);
    },
    [],
  );

  /** Secondary value commit (blur / Enter). */
  const handleValue2Commit = useCallback(() => {
    commitToUrl(localOperator, localValue, localValue2);
    onChange?.(name, localOperator, localValue, localValue2 || undefined);
  }, [localOperator, localValue, localValue2, commitToUrl, onChange, name]);

  /** Immediate value commit for `<select>` inputs (checkbox, select, file). */
  const handleSelectValueChange = useCallback(
    (e: ChangeEvent<HTMLSelectElement>) => {
      const val = e.target.value;
      setLocalValue(val);
      commitToUrl(localOperator, val, showSecondary ? localValue2 : '');
      onChange?.(
        name,
        localOperator,
        val,
        showSecondary ? localValue2 || undefined : undefined,
      );
    },
    [localOperator, localValue2, showSecondary, commitToUrl, onChange, name],
  );

  /** Clear this filter — reset local state, remove URL params, notify parent. */
  const handleClear = useCallback(() => {
    setLocalOperator(resolveDefaultOperator(effectiveOptions));
    setLocalValue('');
    setLocalValue2('');
    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.delete(typeKey);
        next.delete(valueKey);
        next.delete(value2Key);
        return next;
      },
      { replace: true },
    );
    onClear?.(name);
  }, [
    effectiveOptions,
    setSearchParams,
    typeKey,
    valueKey,
    value2Key,
    onClear,
    name,
  ]);

  /** Keyboard handler — commit on Enter. */
  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Enter') {
        handleValueCommit();
      }
    },
    [handleValueCommit],
  );

  /** Keyboard handler for secondary input — commit on Enter. */
  const handleValue2KeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Enter') {
        handleValue2Commit();
      }
    },
    [handleValue2Commit],
  );

  /* ═══════════════════════════════════════════════════════════════
   * Render helpers
   * ═══════════════════════════════════════════════════════════════ */

  /**
   * Operator selector — dropdown when multiple options, static label
   * with hidden input when only one option.
   *
   * Mirrors `WvFilterBase.cs` lines 274-308.
   */
  const renderOperatorSelector = (): ReactElement => {
    if (effectiveOptions.length <= 1) {
      const singleOp = effectiveOptions[0] ?? FilterType.EQ;
      return (
        <span className="inline-flex items-center border border-gray-300 border-e-0 bg-gray-50 px-2 text-sm rounded-s">
          <span>{FILTER_TYPE_LABELS[singleOp] ?? '='}</span>
          <input type="hidden" name={typeKey} value={String(singleOp)} />
        </span>
      );
    }

    return (
      <select
        className={
          'border border-gray-300 border-e-0 bg-gray-50 px-2 py-1.5 text-sm ' +
          'rounded-s focus:border-blue-500 focus:ring-1 focus:ring-blue-500 focus:outline-none'
        }
        name={typeKey}
        value={String(localOperator)}
        onChange={handleOperatorChange}
        aria-label={`Filter operator for ${label ?? name}`}
      >
        {effectiveOptions.map((opt) => (
          <option key={opt} value={String(opt)}>
            {FILTER_TYPE_LABELS[opt] ?? String(opt)}
          </option>
        ))}
      </select>
    );
  };

  /**
   * "&" divider + secondary value input shown only for BETWEEN / NOTBETWEEN.
   *
   * Mirrors `WvFilterBase.cs` AndDivider (lines 357-370) and individual
   * filter TagHelpers' value2 controls (e.g. `WvFilterDate.cs` lines 51-66).
   */
  const renderBetweenControls = (
    inputType: 'number' | 'date' | 'datetime-local',
  ): ReactElement => (
    <>
      {/* ── And divider ────────────────────────────────────────── */}
      <span
        className={`inline-flex items-center border-y border-gray-300 bg-gray-100 px-2 text-sm${
          showSecondary ? '' : ' hidden'
        }`}
        aria-hidden="true"
      >
        &amp;
      </span>

      {/* ── Secondary value input ──────────────────────────────── */}
      <input
        type={inputType}
        className={`${VALUE_INPUT_BASE} ${ROUNDED_END}${
          showSecondary ? '' : ' hidden'
        }`}
        name={showSecondary ? value2Key : undefined}
        value={localValue2}
        onChange={handleValue2Change}
        onBlur={handleValue2Commit}
        onKeyDown={handleValue2KeyDown}
        aria-label={`Secondary filter value for ${label ?? name}`}
      />
    </>
  );

  /**
   * Field-type-specific value input dispatcher.
   *
   * Mirrors `PcGridFilterField/Display.cshtml` lines 14-89 (the giant
   * `@switch ((FieldType)options.FieldType)` block) and the concrete
   * rendering in each `WvFilter*.cs` TagHelper.
   */
  const renderValueInput = (): ReactElement => {
    const roundedEnd = showSecondary ? '' : ` ${ROUNDED_END}`;

    switch (fieldType) {
      /* ── Text-like types (WvFilterText pattern) ────────────── */
      case FieldType.TextField:
      case FieldType.MultiLineTextField:
      case FieldType.EmailField:
      case FieldType.PhoneField:
      case FieldType.UrlField:
      case FieldType.HtmlField:
      case FieldType.GuidField:
        return (
          <input
            type="text"
            className={`${VALUE_INPUT_BASE}${roundedEnd}`}
            name={valueKey}
            value={localValue}
            onChange={handleValueChange}
            onBlur={() => handleValueCommit()}
            onKeyDown={handleKeyDown}
            aria-label={`Filter value for ${label ?? name}`}
          />
        );

      /* ── Number-like types (WvFilterNumber pattern) ────────── */
      case FieldType.NumberField:
      case FieldType.CurrencyField:
      case FieldType.PercentField:
      case FieldType.AutoNumberField:
        return (
          <>
            <input
              type="number"
              className={`${VALUE_INPUT_BASE}${roundedEnd}`}
              name={valueKey}
              value={localValue}
              onChange={handleValueChange}
              onBlur={() => handleValueCommit()}
              onKeyDown={handleKeyDown}
              aria-label={`Filter value for ${label ?? name}`}
            />
            {renderBetweenControls('number')}
          </>
        );

      /* ── Date (WvFilterDate.cs lines 31-65) ────────────────── */
      case FieldType.DateField:
        return (
          <>
            <input
              type="date"
              className={`${VALUE_INPUT_BASE}${roundedEnd}`}
              name={valueKey}
              value={localValue}
              onChange={handleValueChange}
              onBlur={() => handleValueCommit()}
              onKeyDown={handleKeyDown}
              aria-label={`Filter value for ${label ?? name}`}
            />
            {renderBetweenControls('date')}
          </>
        );

      /* ── DateTime (WvFilterDateTime.cs lines 32-65) ────────── */
      case FieldType.DateTimeField:
        return (
          <>
            <input
              type="datetime-local"
              className={`${VALUE_INPUT_BASE}${roundedEnd}`}
              name={valueKey}
              value={localValue}
              onChange={handleValueChange}
              onBlur={() => handleValueCommit()}
              onKeyDown={handleKeyDown}
              aria-label={`Filter value for ${label ?? name}`}
            />
            {renderBetweenControls('datetime-local')}
          </>
        );

      /* ── Checkbox — tri-state select (WvFilterCheckbox.cs 30-55) */
      case FieldType.CheckboxField:
        return (
          <select
            className={`${VALUE_INPUT_BASE} ${ROUNDED_END}`}
            name={valueKey}
            value={localValue}
            onChange={handleSelectValueChange}
            aria-label={`Filter value for ${label ?? name}`}
          >
            <option value="">{'\u2014'}</option>
            <option value="true">true</option>
            <option value="false">false</option>
          </select>
        );

      /* ── Select — dropdown with valueOptions (WvFilterSelect.cs 36-63) */
      case FieldType.SelectField:
        return (
          <select
            className={`${VALUE_INPUT_BASE}${roundedEnd}`}
            name={valueKey}
            value={localValue}
            onChange={handleSelectValueChange}
            aria-label={`Filter value for ${label ?? name}`}
          >
            <option value="">{'\u2014'}</option>
            {(valueOptions ?? []).map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        );

      /* ── MultiSelect — comma-separated text input (WvFilterMultiSelect) */
      case FieldType.MultiSelectField:
        return (
          <input
            type="text"
            className={`${VALUE_INPUT_BASE}${roundedEnd}`}
            name={valueKey}
            value={localValue}
            onChange={handleValueChange}
            onBlur={() => handleValueCommit()}
            onKeyDown={handleKeyDown}
            placeholder="Comma-separated values"
            aria-label={`Filter value for ${label ?? name}`}
          />
        );

      /* ── File / Image — exists / not-exists select ─────────── */
      case FieldType.FileField:
      case FieldType.ImageField:
        return (
          <select
            className={`${VALUE_INPUT_BASE} ${ROUNDED_END}`}
            name={valueKey}
            value={localValue}
            onChange={handleSelectValueChange}
            aria-label={`Filter value for ${label ?? name}`}
          >
            <option value="">{'\u2014'}</option>
            <option value="true">has file</option>
            <option value="false">no file</option>
          </select>
        );

      /* ── Fallback — generic text input ─────────────────────── */
      default:
        return (
          <input
            type="text"
            className={`${VALUE_INPUT_BASE}${roundedEnd}`}
            name={valueKey}
            value={localValue}
            onChange={handleValueChange}
            onBlur={() => handleValueCommit()}
            onKeyDown={handleKeyDown}
            aria-label={`Filter value for ${label ?? name}`}
          />
        );
    }
  };

  /* ═══════════════════════════════════════════════════════════════
   * Main render
   * ═══════════════════════════════════════════════════════════════ */

  return (
    <div className="space-y-1" data-name={name} data-prefix={prefix}>
      {/* ── Label row with optional "clear" link ─────────────── */}
      <div className="flex items-center">
        <label className="block text-sm font-medium text-gray-700">
          {label || name}
        </label>
        {hasClearableValue && (
          <button
            type="button"
            className={
              'ms-2 text-xs text-blue-600 ' +
              'hover:text-blue-800 ' +
              'focus-visible:outline focus-visible:outline-2 ' +
              'focus-visible:outline-offset-1 focus-visible:outline-blue-600'
            }
            onClick={handleClear}
          >
            clear
          </button>
        )}
      </div>

      {/* ── Input group: operator selector + value input(s) ──── */}
      <div className="flex items-stretch">
        {renderOperatorSelector()}
        {renderValueInput()}
      </div>
    </div>
  );
}
