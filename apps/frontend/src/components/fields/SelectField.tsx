/**
 * SelectField — Dropdown Select Field Component
 *
 * React replacement for the monolith's PcFieldSelect ViewComponent
 * (WebVella.Erp.Web/Components/PcFieldSelect/). Provides a fully
 * accessible custom dropdown select with:
 *
 *   - Custom dropdown with client-side search/filter capability
 *   - Optional per-option icon display (showIcon + option.iconClass)
 *   - Optional per-option color badge (option.color)
 *   - AJAX datasource loading via ajaxDatasourceApi
 *   - Client-side filtering by selectMatchType (contains/startsWith/exact)
 *   - Display mode with optional anchor link (href)
 *   - ARIA combobox pattern for full keyboard accessibility
 *
 * Source mapping:
 *   PcFieldSelectOptions.Options           → options prop (SelectOption[])
 *   PcFieldSelectOptions.ShowIcon          → showIcon prop
 *   PcFieldSelectOptions.AjaxDatasourceApi → ajaxDatasourceApi prop
 *   PcFieldSelectOptions.SelectMatchingType→ selectMatchType prop
 *   PcFieldSelectOptions.Placeholder       → placeholder prop (BaseFieldProps)
 *   PcFieldSelectOptions.Href              → href prop
 *   PcFieldSelectModel.Options             → options prop
 *   PcFieldSelectModel.Value               → value prop
 *
 * @module components/fields/SelectField
 */

import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import type { BaseFieldProps } from './FieldRenderer';
import apiClient from '../../api/client';

// ---------------------------------------------------------------------------
// Exported Interfaces
// ---------------------------------------------------------------------------

/**
 * Represents a single option in the select dropdown.
 *
 * Maps to the monolith's `SelectOption` class (Value, Label, IconClass, Color)
 * from WebVella.Erp.Api.Models.
 */
export interface SelectOption {
  /** The option's stored value (persisted when selected). */
  value: string;
  /** The human-readable display label. */
  label: string;
  /** Optional CSS class string for an icon (e.g. "fa fa-user"). */
  iconClass?: string;
  /** Optional color value for a color badge (hex, named, or rgb). */
  color?: string;
}

/**
 * Props for the SelectField component.
 *
 * Extends BaseFieldProps (omitting value/onChange for type narrowing) with
 * select-specific properties derived from PcFieldSelectOptions.
 */
export interface SelectFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Currently selected option value, or null if nothing selected. */
  value: string | null;
  /** Callback invoked when the selected value changes. */
  onChange?: (value: string | null) => void;
  /** Static list of available options. */
  options: SelectOption[];
  /** Whether to display option icons from iconClass. Default: false. */
  showIcon?: boolean;
  /** Placeholder text when no option is selected. */
  placeholder?: string;
  /** URL rendered as a link wrapping the label in display mode. */
  href?: string;
  /** Client-side option filtering strategy. Default: 'contains'. */
  selectMatchType?: 'contains' | 'startsWith' | 'exact';
  /** API endpoint URL for loading options asynchronously on mount. */
  ajaxDatasourceApi?: string;
}

// ---------------------------------------------------------------------------
// SVG icon path constants
// ---------------------------------------------------------------------------

/** Chevron-down SVG path data for the dropdown indicator. */
const CHEVRON_DOWN_PATH =
  'M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z';

/** Checkmark SVG path data for the selected-option indicator. */
const CHECK_PATH =
  'M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z';

// ---------------------------------------------------------------------------
// Threshold for showing the in-dropdown search input
// ---------------------------------------------------------------------------
const SEARCH_THRESHOLD = 5;

// ---------------------------------------------------------------------------
// SelectField Component
// ---------------------------------------------------------------------------

/**
 * Dropdown select field with search, AJAX loading, icons and color badges.
 *
 * Renders as a custom ARIA combobox in edit mode and as plain text (or link)
 * in display mode. Replaces the monolith's `<wv-field-select>` tag helper
 * and PcFieldSelect ViewComponent.
 */
function SelectField(props: SelectFieldProps): React.JSX.Element | null {
  // Destructure with defaults that mirror the monolith behaviour
  const {
    name,
    fieldId,
    label,
    labelMode,
    mode = 'edit',
    access = 'full',
    required = false,
    disabled = false,
    error,
    className,
    placeholder = '',
    description,
    isVisible = true,
    emptyValueMessage = 'no data',
    accessDeniedMessage = 'access denied',
    locale,
    value = null,
    onChange,
    options = [],
    showIcon = false,
    href,
    selectMatchType = 'contains',
    ajaxDatasourceApi,
  } = props;

  // ---- State management ---------------------------------------------------

  /** Whether the dropdown panel is open. */
  const [isOpen, setIsOpen] = useState(false);
  /** Current text in the search filter input. */
  const [searchText, setSearchText] = useState('');
  /** Options loaded from the AJAX datasource endpoint. */
  const [ajaxOptions, setAjaxOptions] = useState<SelectOption[]>([]);
  /** Whether an AJAX request is in-flight. */
  const [isLoading, setIsLoading] = useState(false);
  /** Index of the keyboard-highlighted option in filteredOptions. */
  const [highlightedIndex, setHighlightedIndex] = useState(-1);

  // ---- Refs ---------------------------------------------------------------

  const containerRef = useRef<HTMLDivElement>(null);
  const searchInputRef = useRef<HTMLInputElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  // ---- Derived identifiers ------------------------------------------------

  const controlId = fieldId ?? `field-${name ?? 'select'}`;
  const listboxId = `${controlId}-listbox`;

  // ---- Memo: merge static + AJAX options ----------------------------------

  const allOptions = useMemo<SelectOption[]>(() => {
    if (ajaxOptions.length === 0) return options;
    const combined = [...options];
    for (const ajaxOpt of ajaxOptions) {
      if (!combined.some((o) => o.value === ajaxOpt.value)) {
        combined.push(ajaxOpt);
      }
    }
    return combined;
  }, [options, ajaxOptions]);

  // ---- Memo: filter options by search text & selectMatchType ---------------

  const filteredOptions = useMemo<SelectOption[]>(() => {
    const query = searchText.trim().toLowerCase();
    if (!query) return allOptions;
    return allOptions.filter((opt) => {
      const optLabel = (opt.label ?? '').toLowerCase();
      switch (selectMatchType) {
        case 'startsWith':
          return optLabel.startsWith(query);
        case 'exact':
          return optLabel === query;
        case 'contains':
        default:
          return optLabel.includes(query);
      }
    });
  }, [allOptions, searchText, selectMatchType]);

  // ---- Memo: resolve selected option from value ---------------------------

  const selectedOption = useMemo<SelectOption | null>(() => {
    if (value === null || value === undefined) return null;
    const strValue = String(value);
    if (strValue === '') return null;
    return allOptions.find((opt) => opt.value === strValue) ?? null;
  }, [value, allOptions]);

  // Whether the search input is visible in the current dropdown
  const showSearch = allOptions.length > SEARCH_THRESHOLD || Boolean(ajaxDatasourceApi);

  // ---- Effect: AJAX datasource fetch on mount -----------------------------

  useEffect(() => {
    if (!ajaxDatasourceApi) return;
    let cancelled = false;

    setIsLoading(true);

    apiClient
      .get(ajaxDatasourceApi)
      .then((response) => {
        if (cancelled) return;

        const data = response.data;
        let loaded: SelectOption[] = [];

        // Handle both a direct array response and the ApiResponse envelope
        if (Array.isArray(data)) {
          loaded = data;
        } else if (
          data !== null &&
          typeof data === 'object' &&
          'object' in (data as Record<string, unknown>) &&
          Array.isArray((data as Record<string, unknown>).object)
        ) {
          loaded = (data as Record<string, unknown>).object as SelectOption[];
        }

        setAjaxOptions(loaded);
      })
      .catch(() => {
        // AJAX failure is non-fatal; static options remain available
      })
      .finally(() => {
        if (!cancelled) setIsLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [ajaxDatasourceApi]);

  // ---- Effect: close dropdown when clicking outside -----------------------

  useEffect(() => {
    if (!isOpen) return;

    const handleClickOutside = (event: MouseEvent): void => {
      if (
        containerRef.current &&
        !containerRef.current.contains(event.target as Node)
      ) {
        setIsOpen(false);
        setSearchText('');
        setHighlightedIndex(-1);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
    };
  }, [isOpen]);

  // ---- Effect: focus search input when dropdown opens ---------------------

  useEffect(() => {
    if (isOpen && showSearch && searchInputRef.current) {
      searchInputRef.current.focus();
    }
  }, [isOpen, showSearch]);

  // ---- Effect: scroll highlighted option into view ------------------------

  useEffect(() => {
    if (!isOpen || highlightedIndex < 0) return;
    const el = document.getElementById(`${listboxId}-option-${highlightedIndex}`);
    if (el) {
      el.scrollIntoView?.({ block: 'nearest' });
    }
  }, [isOpen, highlightedIndex, listboxId]);

  // ---- Callback: select an option -----------------------------------------

  const handleSelect = useCallback(
    (optionValue: string | null): void => {
      onChange?.(optionValue);
      setIsOpen(false);
      setSearchText('');
      setHighlightedIndex(-1);
      // Return focus to the trigger after selection
      triggerRef.current?.focus();
    },
    [onChange],
  );

  // ---- Callback: search input change --------------------------------------

  const handleSearchChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>): void => {
      setSearchText(event.target.value);
      setHighlightedIndex(0);
    },
    [],
  );

  // ---- Callback: toggle dropdown ------------------------------------------

  const handleToggle = useCallback((): void => {
    if (disabled || access === 'readonly') return;
    setIsOpen((prev) => {
      if (prev) {
        setSearchText('');
        setHighlightedIndex(-1);
      }
      return !prev;
    });
  }, [disabled, access]);

  // ---- Callback: keyboard navigation --------------------------------------

  const handleKeyDown = useCallback(
    (event: React.KeyboardEvent): void => {
      const isSearchFocused =
        searchInputRef.current !== null &&
        searchInputRef.current === document.activeElement;

      switch (event.key) {
        case 'ArrowDown':
          event.preventDefault();
          if (!isOpen) {
            setIsOpen(true);
          } else {
            setHighlightedIndex((prev) =>
              Math.min(prev + 1, filteredOptions.length - 1),
            );
          }
          break;

        case 'ArrowUp':
          event.preventDefault();
          if (isOpen) {
            setHighlightedIndex((prev) => Math.max(prev - 1, 0));
          }
          break;

        case 'Enter':
          if (
            isOpen &&
            highlightedIndex >= 0 &&
            highlightedIndex < filteredOptions.length
          ) {
            event.preventDefault();
            handleSelect(filteredOptions[highlightedIndex].value);
          } else if (!isOpen) {
            event.preventDefault();
            setIsOpen(true);
          }
          break;

        case ' ':
          // Space opens the dropdown only when the search input is NOT focused
          if (!isSearchFocused) {
            event.preventDefault();
            if (!isOpen) {
              setIsOpen(true);
            }
          }
          break;

        case 'Escape':
          if (isOpen) {
            event.preventDefault();
            setIsOpen(false);
            setSearchText('');
            setHighlightedIndex(-1);
            triggerRef.current?.focus();
          }
          break;

        case 'Home':
          if (isOpen && filteredOptions.length > 0 && !isSearchFocused) {
            event.preventDefault();
            setHighlightedIndex(0);
          }
          break;

        case 'End':
          if (isOpen && filteredOptions.length > 0 && !isSearchFocused) {
            event.preventDefault();
            setHighlightedIndex(filteredOptions.length - 1);
          }
          break;

        case 'Tab':
          if (isOpen) {
            setIsOpen(false);
            setSearchText('');
            setHighlightedIndex(-1);
          }
          break;

        default:
          break;
      }
    },
    [isOpen, highlightedIndex, filteredOptions, handleSelect],
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

  // Coerce value to string for reliable comparison
  const effectiveValue =
    value !== null && value !== undefined && value !== '' ? String(value) : null;

  // ========================================================================
  // DISPLAY MODE
  // ========================================================================

  if (mode === 'display') {
    // No value — show empty message
    if (effectiveValue === null || selectedOption === null) {
      return (
        <span className={`text-sm text-gray-400 italic ${className ?? ''}`}>
          {emptyValueMessage}
        </span>
      );
    }

    // Build the display content fragments
    const iconFragment = showIcon && selectedOption.iconClass ? (
      <i className={selectedOption.iconClass} aria-hidden="true" />
    ) : null;

    const colorFragment = selectedOption.color ? (
      <span
        className="inline-block h-3 w-3 rounded-full shrink-0"
        style={{ backgroundColor: selectedOption.color }}
        aria-hidden="true"
      />
    ) : null;

    const labelFragment = <span>{selectedOption.label}</span>;

    // Wrap in <a> when href is provided
    if (href) {
      return (
        <a
          href={href}
          className={[
            'inline-flex items-center gap-1.5 text-sm',
            'text-blue-600 hover:text-blue-800 hover:underline',
            className ?? '',
          ].join(' ')}
        >
          {iconFragment}
          {colorFragment}
          {labelFragment}
        </a>
      );
    }

    return (
      <span
        className={[
          'inline-flex items-center gap-1.5 text-sm text-gray-900',
          className ?? '',
        ].join(' ')}
      >
        {iconFragment}
        {colorFragment}
        {labelFragment}
      </span>
    );
  }

  // ========================================================================
  // EDIT MODE (custom dropdown / combobox)
  // ========================================================================

  const isDisabled = disabled || access === 'readonly';

  return (
    <div
      ref={containerRef}
      className={`relative ${className ?? ''}`}
      onKeyDown={handleKeyDown}
    >
      {/* ------- Trigger button ----------------------------------------- */}
      <button
        ref={triggerRef}
        type="button"
        id={controlId}
        role="combobox"
        aria-expanded={isOpen}
        aria-haspopup="listbox"
        aria-controls={isOpen ? listboxId : undefined}
        aria-label={label ?? name ?? 'Select'}
        aria-required={required}
        aria-invalid={Boolean(error)}
        aria-describedby={
          description ? `${controlId}-desc` : undefined
        }
        aria-activedescendant={
          isOpen && highlightedIndex >= 0 && filteredOptions[highlightedIndex]
            ? `${listboxId}-option-${highlightedIndex}`
            : undefined
        }
        disabled={isDisabled}
        onClick={handleToggle}
        className={[
          'flex w-full items-center justify-between rounded-md border px-3 py-2 text-sm shadow-sm',
          'transition-colors duration-150',
          isDisabled
            ? 'border-gray-200 bg-gray-100 text-gray-500 cursor-not-allowed'
            : isOpen
              ? 'border-blue-500 ring-1 ring-blue-500 bg-white text-gray-900'
              : error
                ? 'border-red-300 bg-white text-gray-900 focus-visible:border-red-500 focus-visible:ring-1 focus-visible:ring-red-500'
                : 'border-gray-300 bg-white text-gray-900 focus-visible:border-blue-500 focus-visible:ring-1 focus-visible:ring-blue-500',
          'focus-visible:outline-none',
        ].join(' ')}
      >
        {/* Selected value or placeholder */}
        <span className="inline-flex items-center gap-1.5 truncate min-w-0">
          {selectedOption ? (
            <>
              {showIcon && selectedOption.iconClass && (
                <i
                  className={`${selectedOption.iconClass} shrink-0`}
                  aria-hidden="true"
                />
              )}
              {selectedOption.color && (
                <span
                  className="inline-block h-3 w-3 rounded-full shrink-0"
                  style={{ backgroundColor: selectedOption.color }}
                  aria-hidden="true"
                />
              )}
              <span className="truncate">{selectedOption.label}</span>
            </>
          ) : (
            <span className="text-gray-400 truncate">
              {placeholder || 'Select\u2026'}
            </span>
          )}
        </span>

        {/* Chevron or loading spinner */}
        {isLoading ? (
          <svg
            className="h-4 w-4 shrink-0 animate-spin text-gray-400 ms-2"
            viewBox="0 0 24 24"
            fill="none"
            aria-hidden="true"
          >
            <circle
              className="opacity-25"
              cx="12"
              cy="12"
              r="10"
              stroke="currentColor"
              strokeWidth="4"
            />
            <path
              className="opacity-75"
              fill="currentColor"
              d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
            />
          </svg>
        ) : (
          <svg
            className={[
              'h-5 w-5 shrink-0 text-gray-400 ms-2',
              'transition-transform duration-150',
              isOpen ? 'rotate-180' : '',
            ].join(' ')}
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path fillRule="evenodd" d={CHEVRON_DOWN_PATH} clipRule="evenodd" />
          </svg>
        )}
      </button>

      {/* Hidden description for screen readers */}
      {description && (
        <span id={`${controlId}-desc`} className="sr-only">
          {description}
        </span>
      )}

      {/* ------- Dropdown panel ----------------------------------------- */}
      {isOpen && (
        <div
          className={[
            'absolute z-50 mt-1 w-full rounded-md border border-gray-200',
            'bg-white shadow-lg',
          ].join(' ')}
        >
          {/* Search input (shown when option count exceeds threshold) */}
          {showSearch && (
            <div className="border-b border-gray-100 p-2">
              <input
                ref={searchInputRef}
                type="text"
                value={searchText}
                onChange={handleSearchChange}
                placeholder="Search\u2026"
                className={[
                  'block w-full rounded border border-gray-200 px-2.5 py-1.5 text-sm',
                  'text-gray-900 placeholder:text-gray-400',
                  'focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500',
                ].join(' ')}
                aria-label="Search options"
                role="searchbox"
                autoComplete="off"
              />
            </div>
          )}

          {/* Options listbox */}
          <ul
            id={listboxId}
            role="listbox"
            aria-label={label ?? name ?? 'Options'}
            className="max-h-60 overflow-auto py-1"
            tabIndex={-1}
          >
            {/* Clear / none option (only when not required) */}
            {!required && (
              <li
                id={`${listboxId}-option-clear`}
                role="option"
                aria-selected={effectiveValue === null}
                onClick={() => handleSelect(null)}
                className={[
                  'flex cursor-pointer items-center gap-2 px-3 py-2 text-sm',
                  effectiveValue === null
                    ? 'bg-blue-50 text-blue-700'
                    : 'text-gray-400 hover:bg-gray-50',
                ].join(' ')}
              >
                <span className="italic">
                  {placeholder || '\u2014 None \u2014'}
                </span>
              </li>
            )}

            {/* Rendered options */}
            {filteredOptions.map((option, index) => {
              const isSelected = effectiveValue === option.value;
              const isHighlighted = index === highlightedIndex;

              return (
                <li
                  key={option.value}
                  id={`${listboxId}-option-${index}`}
                  role="option"
                  aria-selected={isSelected}
                  onClick={() => handleSelect(option.value)}
                  onMouseEnter={() => setHighlightedIndex(index)}
                  className={[
                    'flex cursor-pointer items-center gap-2 px-3 py-2 text-sm',
                    isHighlighted
                      ? 'bg-blue-50'
                      : isSelected
                        ? 'bg-gray-50'
                        : 'hover:bg-gray-50',
                    isSelected ? 'font-medium text-blue-700' : 'text-gray-900',
                  ].join(' ')}
                >
                  {/* Color badge */}
                  {option.color && (
                    <span
                      className="inline-block h-3 w-3 rounded-full shrink-0"
                      style={{ backgroundColor: option.color }}
                      aria-hidden="true"
                    />
                  )}

                  {/* Option icon */}
                  {showIcon && option.iconClass && (
                    <i
                      className={`${option.iconClass} shrink-0`}
                      aria-hidden="true"
                    />
                  )}

                  {/* Option label */}
                  <span className="truncate">{option.label}</span>

                  {/* Selected checkmark */}
                  {isSelected && (
                    <svg
                      className="ms-auto h-4 w-4 shrink-0 text-blue-600"
                      viewBox="0 0 20 20"
                      fill="currentColor"
                      aria-hidden="true"
                    >
                      <path
                        fillRule="evenodd"
                        d={CHECK_PATH}
                        clipRule="evenodd"
                      />
                    </svg>
                  )}
                </li>
              );
            })}

            {/* No-results message */}
            {filteredOptions.length === 0 && (
              <li
                className="px-3 py-2 text-sm text-gray-400 italic"
                role="status"
              >
                {isLoading ? 'Loading options\u2026' : 'No matching options'}
              </li>
            )}
          </ul>
        </div>
      )}
    </div>
  );
}

export default SelectField;
