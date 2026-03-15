/**
 * MultiSelectField — Multi-Select Dropdown Field Component
 *
 * React replacement for the monolith's PcFieldMultiSelect ViewComponent
 * (WebVella.Erp.Web/Components/PcFieldMultiSelect/). Provides a fully
 * accessible custom multi-select dropdown with:
 *
 *   - Dropdown panel showing checkboxes for every option
 *   - Selected items rendered as removable tags/chips inside the control
 *   - Per-tag × (remove) button to deselect individual values
 *   - Client-side search/filter input to narrow the options list
 *   - "Select All" and "Clear" bulk actions
 *   - Click-outside-to-close behaviour
 *   - Full keyboard accessibility (arrow keys, enter, escape, space)
 *   - Display mode rendering as styled tags/badges or comma-separated labels
 *
 * Source mapping:
 *   PcFieldMultiSelectOptions.Options          → options prop (SelectOption[])
 *   PcFieldMultiSelectOptions.Placeholder      → placeholder prop
 *   PcFieldMultiSelectModel.Value (List<string>) → value prop (string[] | null)
 *   PcFieldBaseModel.Access / Mode / Required  → access / mode / required props
 *
 * @module components/fields/MultiSelectField
 */

import React, { useState, useCallback, useMemo, useRef, useEffect } from 'react';
import type { BaseFieldProps } from './FieldRenderer';
import type { SelectOption } from './SelectField';

// ---------------------------------------------------------------------------
// Exported Interface
// ---------------------------------------------------------------------------

/**
 * Props for the MultiSelectField component.
 *
 * Extends BaseFieldProps (omitting value/onChange for multi-select type
 * narrowing) with options-specific properties derived from
 * PcFieldMultiSelectOptions / PcFieldMultiSelectModel.
 */
export interface MultiSelectFieldProps
  extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Array of currently selected option values, or null when empty. */
  value: string[] | null;
  /** Callback invoked when the set of selected values changes. */
  onChange?: (value: string[]) => void;
  /** Static list of available options with value + label. */
  options: SelectOption[];
  /** Placeholder text displayed when no options are selected. */
  placeholder?: string;
}

// ---------------------------------------------------------------------------
// SVG icon path constants (consistent with SelectField sibling)
// ---------------------------------------------------------------------------

/** Chevron-down SVG path data for the dropdown indicator. */
const CHEVRON_DOWN_PATH =
  'M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z';

/** Checkmark SVG path data for checkbox checked indicator. */
const CHECK_PATH =
  'M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z';

/** Threshold above which the in-dropdown search input is shown. */
const SEARCH_THRESHOLD = 5;

// ---------------------------------------------------------------------------
// MultiSelectField Component
// ---------------------------------------------------------------------------

/**
 * Multi-select dropdown field with tag chips, checkboxes, and search filter.
 *
 * Renders as removable tags/badges in display mode and as a custom ARIA
 * listbox dropdown (multiselectable) in edit mode. Replaces the monolith's
 * `<wv-field-multiselect>` tag helper and PcFieldMultiSelect ViewComponent.
 */
function MultiSelectField(props: MultiSelectFieldProps): React.JSX.Element | null {
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
  } = props;

  // ---- State management ---------------------------------------------------

  /** Whether the dropdown panel is open. */
  const [isOpen, setIsOpen] = useState(false);
  /** Current text in the search filter input. */
  const [searchText, setSearchText] = useState('');
  /** Index of the keyboard-highlighted option in filteredOptions. */
  const [highlightedIndex, setHighlightedIndex] = useState(-1);

  // ---- Refs ---------------------------------------------------------------

  /** Container element reference for click-outside detection. */
  const containerRef = useRef<HTMLDivElement>(null);
  /** Reference to the search input for auto-focus on dropdown open. */
  const searchInputRef = useRef<HTMLInputElement>(null);
  /** Reference to the trigger element for returning focus after close. */
  const triggerRef = useRef<HTMLDivElement>(null);

  // ---- Derived identifiers ------------------------------------------------

  const controlId = fieldId ?? `field-${name ?? 'multiselect'}`;
  const listboxId = `${controlId}-listbox`;

  // ---- Normalised value ---------------------------------------------------

  /** Normalise value to a reliable string array. */
  const selectedValues = useMemo<string[]>(() => {
    if (!value) return [];
    if (!Array.isArray(value)) return [];
    return value;
  }, [value]);

  // ---- Memo: filter options by search text --------------------------------

  const filteredOptions = useMemo<SelectOption[]>(() => {
    const query = searchText.trim().toLowerCase();
    if (!query) return options;
    return options.filter((opt) =>
      (opt.label ?? '').toLowerCase().includes(query),
    );
  }, [options, searchText]);

  // ---- Memo: resolve selected option objects for display ------------------

  const selectedOptions = useMemo<SelectOption[]>(() => {
    if (selectedValues.length === 0) return [];
    return selectedValues
      .map((sv) => options.find((opt) => opt.value === sv))
      .filter((opt): opt is SelectOption => opt !== undefined);
  }, [selectedValues, options]);

  /** Whether the search input should be visible in the dropdown. */
  const showSearch = options.length > SEARCH_THRESHOLD;

  // ---- Effect: close dropdown on click-outside ----------------------------

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
    const el = document.getElementById(
      `${listboxId}-option-${highlightedIndex}`,
    );
    if (el) {
      el.scrollIntoView?.({ block: 'nearest' });
    }
  }, [isOpen, highlightedIndex, listboxId]);

  // ---- Callback: toggle individual option selection -----------------------

  const handleToggleOption = useCallback(
    (optionValue: string): void => {
      const current = selectedValues;
      const isSelected = current.includes(optionValue);
      const next = isSelected
        ? current.filter((v) => v !== optionValue)
        : [...current, optionValue];
      onChange?.(next);
    },
    [selectedValues, onChange],
  );

  // ---- Callback: remove a single tag/chip --------------------------------

  const handleRemoveTag = useCallback(
    (optionValue: string, event: React.MouseEvent): void => {
      event.stopPropagation();
      const next = selectedValues.filter((v) => v !== optionValue);
      onChange?.(next);
    },
    [selectedValues, onChange],
  );

  // ---- Callback: select all visible options -------------------------------

  const handleSelectAll = useCallback((): void => {
    const allValues = new Set(selectedValues);
    for (const opt of filteredOptions) {
      allValues.add(opt.value);
    }
    onChange?.(Array.from(allValues));
  }, [selectedValues, filteredOptions, onChange]);

  // ---- Callback: clear all selections ------------------------------------

  const handleClearAll = useCallback((): void => {
    onChange?.([]);
  }, [onChange]);

  // ---- Callback: search input change -------------------------------------

  const handleSearchChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>): void => {
      setSearchText(event.target.value);
      setHighlightedIndex(0);
    },
    [],
  );

  // ---- Callback: toggle dropdown open/close ------------------------------

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

  // ---- Callback: keyboard navigation -------------------------------------

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
            handleToggleOption(filteredOptions[highlightedIndex].value);
          } else if (!isOpen) {
            event.preventDefault();
            setIsOpen(true);
          }
          break;

        case ' ':
          if (!isSearchFocused) {
            event.preventDefault();
            if (isOpen && highlightedIndex >= 0 && highlightedIndex < filteredOptions.length) {
              handleToggleOption(filteredOptions[highlightedIndex].value);
            } else if (!isOpen) {
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
    [isOpen, highlightedIndex, filteredOptions, handleToggleOption],
  );

  // ========================================================================
  // Visibility gate
  // ========================================================================

  if (isVisible === false) {
    return null;
  }

  // ========================================================================
  // Access-denied gate
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
  // DISPLAY MODE — show selected values as styled tags/badges
  // ========================================================================

  if (mode === 'display') {
    if (selectedOptions.length === 0) {
      return (
        <span className={`text-sm text-gray-400 italic ${className ?? ''}`}>
          {emptyValueMessage}
        </span>
      );
    }

    return (
      <div
        className={`flex flex-wrap gap-1.5 ${className ?? ''}`}
        aria-label={label ?? name ?? 'Selected values'}
      >
        {selectedOptions.map((opt) => (
          <span
            key={opt.value}
            className="inline-flex items-center gap-1 rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800"
          >
            {opt.color && (
              <span
                className="inline-block h-2 w-2 rounded-full shrink-0"
                style={{ backgroundColor: opt.color }}
                aria-hidden="true"
              />
            )}
            {opt.label}
          </span>
        ))}
      </div>
    );
  }

  // ========================================================================
  // EDIT MODE — custom multi-select dropdown with checkboxes
  // ========================================================================

  const isDisabled = disabled || access === 'readonly';
  const allFilteredSelected = filteredOptions.length > 0 &&
    filteredOptions.every((opt) => selectedValues.includes(opt.value));

  return (
    <div
      ref={containerRef}
      className={`relative ${className ?? ''}`}
      onKeyDown={handleKeyDown}
    >
      {/* ------- Trigger (div, not button, so nested remove buttons are valid HTML) */}
      <div
        ref={triggerRef}
        id={controlId}
        role="combobox"
        tabIndex={isDisabled ? -1 : 0}
        aria-expanded={isOpen}
        aria-haspopup="listbox"
        aria-controls={isOpen ? listboxId : undefined}
        aria-label={label ?? name ?? 'Multi-select'}
        aria-required={required}
        aria-invalid={Boolean(error)}
        aria-disabled={isDisabled || undefined}
        aria-describedby={
          description ? `${controlId}-desc` : undefined
        }
        aria-activedescendant={
          isOpen && highlightedIndex >= 0 && filteredOptions[highlightedIndex]
            ? `${listboxId}-option-${highlightedIndex}`
            : undefined
        }
        onClick={handleToggle}
        className={[
          'flex w-full min-h-[2.5rem] items-center gap-1 rounded-md border px-2 py-1.5 text-sm shadow-sm',
          'transition-colors duration-150 select-none',
          isDisabled
            ? 'border-gray-200 bg-gray-100 text-gray-500 cursor-not-allowed'
            : isOpen
              ? 'border-blue-500 ring-1 ring-blue-500 bg-white text-gray-900 cursor-pointer'
              : error
                ? 'border-red-300 bg-white text-gray-900 cursor-pointer focus-visible:border-red-500 focus-visible:ring-1 focus-visible:ring-red-500'
                : 'border-gray-300 bg-white text-gray-900 cursor-pointer focus-visible:border-blue-500 focus-visible:ring-1 focus-visible:ring-blue-500',
          'focus-visible:outline-none',
        ].join(' ')}
      >
        {/* Selected tags area */}
        <span className="flex flex-1 flex-wrap items-center gap-1 min-w-0">
          {selectedOptions.length > 0 ? (
            selectedOptions.map((opt) => (
              <span
                key={opt.value}
                className={[
                  'inline-flex items-center gap-0.5 rounded bg-blue-100 px-1.5 py-0.5 text-xs font-medium text-blue-800',
                  isDisabled ? 'opacity-60' : '',
                ].join(' ')}
              >
                <span className="truncate max-w-[8rem]">{opt.label}</span>
                {!isDisabled && (
                  <button
                    type="button"
                    aria-label={`Remove ${opt.label}`}
                    onClick={(e) => handleRemoveTag(opt.value, e)}
                    className="ms-0.5 shrink-0 rounded-sm p-0.5 text-blue-600 hover:bg-blue-200 hover:text-blue-900 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
                    tabIndex={-1}
                  >
                    <svg
                      className="h-3 w-3"
                      viewBox="0 0 20 20"
                      fill="currentColor"
                      aria-hidden="true"
                    >
                      <path d="M6.28 5.22a.75.75 0 00-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 101.06 1.06L10 11.06l3.72 3.72a.75.75 0 101.06-1.06L11.06 10l3.72-3.72a.75.75 0 00-1.06-1.06L10 8.94 6.28 5.22z" />
                    </svg>
                  </button>
                )}
              </span>
            ))
          ) : (
            <span className="text-gray-400 truncate">
              {placeholder || 'Select\u2026'}
            </span>
          )}
        </span>

        {/* Chevron indicator */}
        <svg
          className={[
            'h-5 w-5 shrink-0 text-gray-400',
            'transition-transform duration-150',
            isOpen ? 'rotate-180' : '',
          ].join(' ')}
          viewBox="0 0 20 20"
          fill="currentColor"
          aria-hidden="true"
        >
          <path fillRule="evenodd" d={CHEVRON_DOWN_PATH} clipRule="evenodd" />
        </svg>
      </div>

      {/* Hidden description for screen readers */}
      {description && (
        <span id={`${controlId}-desc`} className="sr-only">
          {description}
        </span>
      )}

      {/* ------- Dropdown panel ------------------------------------------ */}
      {isOpen && (
        <div
          className={[
            'absolute z-50 mt-1 w-full rounded-md border border-gray-200',
            'bg-white shadow-lg',
          ].join(' ')}
        >
          {/* Search input */}
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

          {/* Bulk actions: Select All / Clear */}
          <div className="flex items-center justify-between border-b border-gray-100 px-3 py-1.5">
            <button
              type="button"
              onClick={handleSelectAll}
              disabled={allFilteredSelected || filteredOptions.length === 0}
              className={[
                'text-xs font-medium',
                allFilteredSelected || filteredOptions.length === 0
                  ? 'text-gray-300 cursor-default'
                  : 'text-blue-600 hover:text-blue-800 focus-visible:outline-none focus-visible:underline',
              ].join(' ')}
              aria-label="Select all options"
            >
              Select All
            </button>
            <button
              type="button"
              onClick={handleClearAll}
              disabled={selectedValues.length === 0}
              className={[
                'text-xs font-medium',
                selectedValues.length === 0
                  ? 'text-gray-300 cursor-default'
                  : 'text-red-600 hover:text-red-800 focus-visible:outline-none focus-visible:underline',
              ].join(' ')}
              aria-label="Clear all selections"
            >
              Clear
            </button>
          </div>

          {/* Options listbox (multi-selectable) */}
          <ul
            id={listboxId}
            role="listbox"
            aria-label={label ?? name ?? 'Options'}
            aria-multiselectable="true"
            className="max-h-60 overflow-auto py-1"
            tabIndex={-1}
          >
            {filteredOptions.map((option, index) => {
              const isSelected = selectedValues.includes(option.value);
              const isHighlighted = index === highlightedIndex;

              return (
                <li
                  key={option.value}
                  id={`${listboxId}-option-${index}`}
                  role="option"
                  aria-selected={isSelected}
                  onClick={() => handleToggleOption(option.value)}
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
                  {/* Checkbox indicator */}
                  <span
                    className={[
                      'flex h-4 w-4 shrink-0 items-center justify-center rounded border',
                      isSelected
                        ? 'border-blue-600 bg-blue-600'
                        : 'border-gray-300 bg-white',
                    ].join(' ')}
                    aria-hidden="true"
                  >
                    {isSelected && (
                      <svg
                        className="h-3 w-3 text-white"
                        viewBox="0 0 20 20"
                        fill="currentColor"
                      >
                        <path
                          fillRule="evenodd"
                          d={CHECK_PATH}
                          clipRule="evenodd"
                        />
                      </svg>
                    )}
                  </span>

                  {/* Option color badge */}
                  {option.color && (
                    <span
                      className="inline-block h-3 w-3 rounded-full shrink-0"
                      style={{ backgroundColor: option.color }}
                      aria-hidden="true"
                    />
                  )}

                  {/* Option label */}
                  <span className="truncate">{option.label}</span>
                </li>
              );
            })}

            {/* No-results message */}
            {filteredOptions.length === 0 && (
              <li
                className="px-3 py-2 text-sm text-gray-400 italic"
                role="status"
              >
                No matching options
              </li>
            )}
          </ul>
        </div>
      )}
    </div>
  );
}

export default MultiSelectField;
