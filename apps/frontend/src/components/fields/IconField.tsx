/**
 * IconField — Font Awesome Icon Selector Component
 *
 * React replacement for the monolith's PcFieldIcon ViewComponent.
 * Provides a text input for entering Font Awesome CSS class strings
 * (e.g. "fas fa-home") with real-time icon preview and an optional
 * searchable dropdown of commonly used icons.
 *
 * Modes:
 *   - display: Read-only icon rendering via <i> element alongside the class name
 *   - edit:    Text input with live icon preview and searchable icon picker dropdown
 *
 * The parent FieldRenderer handles label rendering, access control (forbidden),
 * visibility, description, and error display. This component focuses on the
 * field-specific rendering for icon class values.
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

/* ------------------------------------------------------------------ */
/*  Exported Interface                                                 */
/* ------------------------------------------------------------------ */

/**
 * Props for the IconField component.
 *
 * Extends BaseFieldProps (minus value/onChange which are overridden with
 * icon-specific types) to inherit shared field properties like name, label,
 * mode, access, disabled, required, etc.
 */
export interface IconFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Font Awesome CSS class name string, e.g. "fas fa-home", or null when empty */
  value: string | null;
  /** Callback when the icon class value changes (edit mode) */
  onChange?: (value: string) => void;
}

/* ------------------------------------------------------------------ */
/*  Common Icons Registry                                              */
/* ------------------------------------------------------------------ */

/**
 * Represents a single entry in the common-icons catalogue.
 * Each entry pairs a human-readable label with the Font Awesome class string.
 */
interface IconEntry {
  readonly label: string;
  readonly className: string;
}

/**
 * Curated list of commonly-used Font Awesome 5 icons organised by category.
 * Provides a quick-pick option for users who do not know the exact class
 * string by heart.
 */
const COMMON_ICONS: readonly IconEntry[] = [
  /* ---- Navigation & Interface ---- */
  { label: 'Home', className: 'fas fa-home' },
  { label: 'Search', className: 'fas fa-search' },
  { label: 'Bars (Menu)', className: 'fas fa-bars' },
  { label: 'Cog (Settings)', className: 'fas fa-cog' },
  { label: 'Cogs', className: 'fas fa-cogs' },
  { label: 'Sliders', className: 'fas fa-sliders-h' },
  { label: 'Ellipsis (More)', className: 'fas fa-ellipsis-h' },
  { label: 'Ellipsis Vertical', className: 'fas fa-ellipsis-v' },
  { label: 'Expand', className: 'fas fa-expand' },
  { label: 'Compress', className: 'fas fa-compress' },
  { label: 'External Link', className: 'fas fa-external-link-alt' },
  { label: 'Link', className: 'fas fa-link' },
  { label: 'Unlink', className: 'fas fa-unlink' },

  /* ---- Actions ---- */
  { label: 'Plus', className: 'fas fa-plus' },
  { label: 'Plus Circle', className: 'fas fa-plus-circle' },
  { label: 'Minus', className: 'fas fa-minus' },
  { label: 'Minus Circle', className: 'fas fa-minus-circle' },
  { label: 'Times (Close)', className: 'fas fa-times' },
  { label: 'Times Circle', className: 'fas fa-times-circle' },
  { label: 'Check', className: 'fas fa-check' },
  { label: 'Check Circle', className: 'fas fa-check-circle' },
  { label: 'Edit (Pencil)', className: 'fas fa-edit' },
  { label: 'Trash', className: 'fas fa-trash' },
  { label: 'Trash Alt', className: 'fas fa-trash-alt' },
  { label: 'Undo', className: 'fas fa-undo' },
  { label: 'Redo', className: 'fas fa-redo' },
  { label: 'Sync', className: 'fas fa-sync' },
  { label: 'Download', className: 'fas fa-download' },
  { label: 'Upload', className: 'fas fa-upload' },
  { label: 'Save', className: 'fas fa-save' },
  { label: 'Copy', className: 'fas fa-copy' },
  { label: 'Clipboard', className: 'fas fa-clipboard' },
  { label: 'Print', className: 'fas fa-print' },
  { label: 'Share', className: 'fas fa-share' },
  { label: 'Share Alt', className: 'fas fa-share-alt' },

  /* ---- Arrows & Directions ---- */
  { label: 'Arrow Up', className: 'fas fa-arrow-up' },
  { label: 'Arrow Down', className: 'fas fa-arrow-down' },
  { label: 'Arrow Left', className: 'fas fa-arrow-left' },
  { label: 'Arrow Right', className: 'fas fa-arrow-right' },
  { label: 'Chevron Up', className: 'fas fa-chevron-up' },
  { label: 'Chevron Down', className: 'fas fa-chevron-down' },
  { label: 'Chevron Left', className: 'fas fa-chevron-left' },
  { label: 'Chevron Right', className: 'fas fa-chevron-right' },
  { label: 'Angle Down', className: 'fas fa-angle-down' },
  { label: 'Sort', className: 'fas fa-sort' },

  /* ---- Status & Alerts ---- */
  { label: 'Info Circle', className: 'fas fa-info-circle' },
  { label: 'Exclamation Triangle', className: 'fas fa-exclamation-triangle' },
  { label: 'Exclamation Circle', className: 'fas fa-exclamation-circle' },
  { label: 'Question Circle', className: 'fas fa-question-circle' },
  { label: 'Bell', className: 'fas fa-bell' },
  { label: 'Bell Slash', className: 'fas fa-bell-slash' },
  { label: 'Ban', className: 'fas fa-ban' },
  { label: 'Lock', className: 'fas fa-lock' },
  { label: 'Unlock', className: 'fas fa-unlock' },
  { label: 'Shield', className: 'fas fa-shield-alt' },
  { label: 'Eye', className: 'fas fa-eye' },
  { label: 'Eye Slash', className: 'fas fa-eye-slash' },

  /* ---- Users & People ---- */
  { label: 'User', className: 'fas fa-user' },
  { label: 'User Circle', className: 'fas fa-user-circle' },
  { label: 'User Plus', className: 'fas fa-user-plus' },
  { label: 'Users', className: 'fas fa-users' },
  { label: 'User Shield', className: 'fas fa-user-shield' },
  { label: 'User Tag', className: 'fas fa-user-tag' },
  { label: 'Address Book', className: 'fas fa-address-book' },
  { label: 'ID Card', className: 'fas fa-id-card' },

  /* ---- Communication ---- */
  { label: 'Envelope', className: 'fas fa-envelope' },
  { label: 'Envelope Open', className: 'fas fa-envelope-open' },
  { label: 'Phone', className: 'fas fa-phone' },
  { label: 'Phone Alt', className: 'fas fa-phone-alt' },
  { label: 'Comment', className: 'fas fa-comment' },
  { label: 'Comments', className: 'fas fa-comments' },
  { label: 'Paper Plane', className: 'fas fa-paper-plane' },
  { label: 'Inbox', className: 'fas fa-inbox' },

  /* ---- Content & Media ---- */
  { label: 'File', className: 'fas fa-file' },
  { label: 'File Alt', className: 'fas fa-file-alt' },
  { label: 'Folder', className: 'fas fa-folder' },
  { label: 'Folder Open', className: 'fas fa-folder-open' },
  { label: 'Image', className: 'fas fa-image' },
  { label: 'Camera', className: 'fas fa-camera' },
  { label: 'Video', className: 'fas fa-video' },
  { label: 'Music', className: 'fas fa-music' },
  { label: 'Paperclip', className: 'fas fa-paperclip' },

  /* ---- Business & Finance ---- */
  { label: 'Building', className: 'fas fa-building' },
  { label: 'Briefcase', className: 'fas fa-briefcase' },
  { label: 'Calendar', className: 'fas fa-calendar' },
  { label: 'Calendar Alt', className: 'fas fa-calendar-alt' },
  { label: 'Clock', className: 'fas fa-clock' },
  { label: 'Chart Bar', className: 'fas fa-chart-bar' },
  { label: 'Chart Line', className: 'fas fa-chart-line' },
  { label: 'Chart Pie', className: 'fas fa-chart-pie' },
  { label: 'Dollar Sign', className: 'fas fa-dollar-sign' },
  { label: 'Credit Card', className: 'fas fa-credit-card' },
  { label: 'Shopping Cart', className: 'fas fa-shopping-cart' },
  { label: 'Store', className: 'fas fa-store' },
  { label: 'Receipt', className: 'fas fa-receipt' },
  { label: 'Tags', className: 'fas fa-tags' },
  { label: 'Tag', className: 'fas fa-tag' },
  { label: 'Box', className: 'fas fa-box' },
  { label: 'Boxes', className: 'fas fa-boxes' },
  { label: 'Warehouse', className: 'fas fa-warehouse' },
  { label: 'Truck', className: 'fas fa-truck' },
  { label: 'Shipping Fast', className: 'fas fa-shipping-fast' },

  /* ---- Data & Technology ---- */
  { label: 'Database', className: 'fas fa-database' },
  { label: 'Server', className: 'fas fa-server' },
  { label: 'Cloud', className: 'fas fa-cloud' },
  { label: 'Code', className: 'fas fa-code' },
  { label: 'Terminal', className: 'fas fa-terminal' },
  { label: 'Bug', className: 'fas fa-bug' },
  { label: 'Plug', className: 'fas fa-plug' },
  { label: 'Puzzle Piece', className: 'fas fa-puzzle-piece' },
  { label: 'Key', className: 'fas fa-key' },
  { label: 'Wrench', className: 'fas fa-wrench' },
  { label: 'Tools', className: 'fas fa-tools' },
  { label: 'Hammer', className: 'fas fa-hammer' },

  /* ---- Content Formatting ---- */
  { label: 'List', className: 'fas fa-list' },
  { label: 'List Alt', className: 'fas fa-list-alt' },
  { label: 'Table', className: 'fas fa-table' },
  { label: 'Th', className: 'fas fa-th' },
  { label: 'Th Large', className: 'fas fa-th-large' },
  { label: 'Columns', className: 'fas fa-columns' },
  { label: 'Align Left', className: 'fas fa-align-left' },
  { label: 'Align Center', className: 'fas fa-align-center' },
  { label: 'Bold', className: 'fas fa-bold' },
  { label: 'Italic', className: 'fas fa-italic' },
  { label: 'Heading', className: 'fas fa-heading' },
  { label: 'Paragraph', className: 'fas fa-paragraph' },
  { label: 'Quote Left', className: 'fas fa-quote-left' },

  /* ---- Miscellaneous ---- */
  { label: 'Star', className: 'fas fa-star' },
  { label: 'Heart', className: 'fas fa-heart' },
  { label: 'Thumbs Up', className: 'fas fa-thumbs-up' },
  { label: 'Thumbs Down', className: 'fas fa-thumbs-down' },
  { label: 'Flag', className: 'fas fa-flag' },
  { label: 'Bookmark', className: 'fas fa-bookmark' },
  { label: 'Globe', className: 'fas fa-globe' },
  { label: 'Map Marker', className: 'fas fa-map-marker-alt' },
  { label: 'Map', className: 'fas fa-map' },
  { label: 'Compass', className: 'fas fa-compass' },
  { label: 'Crown', className: 'fas fa-crown' },
  { label: 'Fire', className: 'fas fa-fire' },
  { label: 'Bolt', className: 'fas fa-bolt' },
  { label: 'Magic', className: 'fas fa-magic' },
  { label: 'Lightbulb', className: 'fas fa-lightbulb' },
  { label: 'Trophy', className: 'fas fa-trophy' },
  { label: 'Medal', className: 'fas fa-medal' },
  { label: 'Gift', className: 'fas fa-gift' },
  { label: 'Graduation Cap', className: 'fas fa-graduation-cap' },
  { label: 'Book', className: 'fas fa-book' },
  { label: 'Sitemap', className: 'fas fa-sitemap' },
  { label: 'Project Diagram', className: 'fas fa-project-diagram' },
  { label: 'Tasks', className: 'fas fa-tasks' },
  { label: 'Clipboard List', className: 'fas fa-clipboard-list' },
  { label: 'Clipboard Check', className: 'fas fa-clipboard-check' },
  { label: 'Sign In', className: 'fas fa-sign-in-alt' },
  { label: 'Sign Out', className: 'fas fa-sign-out-alt' },
  { label: 'Power Off', className: 'fas fa-power-off' },
] as const;

/* ------------------------------------------------------------------ */
/*  Utility: Icon class validation heuristic                           */
/* ------------------------------------------------------------------ */

/**
 * Simple heuristic to determine whether a string looks like a valid
 * Font Awesome class.  Matches patterns such as:
 *   - "fas fa-home"
 *   - "far fa-envelope"
 *   - "fab fa-github"
 *   - "fal fa-arrow-left"
 *   - "fa fa-cog"
 *
 * This does NOT guarantee the icon exists — it only checks that the
 * format is plausible for rendering via an <i> element.
 */
function looksLikeIconClass(value: string): boolean {
  if (!value || value.trim().length === 0) return false;
  /* Must contain at least one "fa" prefix token and at least one "fa-" icon token */
  const tokens = value.trim().split(/\s+/);
  const hasFaPrefix = tokens.some(
    (t) => t === 'fa' || t === 'fas' || t === 'far' || t === 'fab' || t === 'fal' || t === 'fad',
  );
  const hasFaIcon = tokens.some((t) => t.startsWith('fa-'));
  return hasFaPrefix && hasFaIcon;
}

/* ------------------------------------------------------------------ */
/*  Inline SVG Icons                                                   */
/* ------------------------------------------------------------------ */

/**
 * Chevron-down icon for the dropdown toggle button.
 */
function ChevronDownIcon(): React.JSX.Element {
  return (
    <svg
      viewBox="0 0 20 20"
      fill="currentColor"
      aria-hidden="true"
      className="inline-block h-4 w-4"
    >
      <path
        fillRule="evenodd"
        d="M5.22 8.22a.75.75 0 0 1 1.06 0L10 11.94l3.72-3.72a.75.75 0 1 1 1.06 1.06l-4.25 4.25a.75.75 0 0 1-1.06 0L5.22 9.28a.75.75 0 0 1 0-1.06Z"
        clipRule="evenodd"
      />
    </svg>
  );
}

/** Search / magnifying-glass icon for the icon picker filter input. */
function SearchIcon(): React.JSX.Element {
  return (
    <svg
      viewBox="0 0 20 20"
      fill="currentColor"
      aria-hidden="true"
      className="inline-block h-4 w-4"
    >
      <path
        fillRule="evenodd"
        d="M9 3.5a5.5 5.5 0 1 0 0 11 5.5 5.5 0 0 0 0-11ZM2 9a7 7 0 1 1 12.452 4.391l3.328 3.329a.75.75 0 1 1-1.06 1.06l-3.329-3.328A7 7 0 0 1 2 9Z"
        clipRule="evenodd"
      />
    </svg>
  );
}

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** Maximum number of icons visible in the dropdown to prevent DOM bloat */
const MAX_DROPDOWN_ITEMS = 50;

/* ------------------------------------------------------------------ */
/*  IconField Component                                                */
/* ------------------------------------------------------------------ */

/**
 * IconField renders a Font Awesome icon class selector with real-time
 * preview.
 *
 * In **display mode** the icon is rendered via an `<i>` element and the
 * class name is shown as supporting text.
 *
 * In **edit mode** a text input allows entering or modifying the icon
 * class. A live preview appears beside the input. A searchable dropdown
 * of common icons provides quick selection without memorising class names.
 *
 * @param props — IconFieldProps
 * @returns JSX element for the icon field
 */
function IconField(props: IconFieldProps): React.JSX.Element {
  const {
    /* Identity */
    name,
    /* eslint-disable @typescript-eslint/no-unused-vars -- destructured for schema compliance */
    label: _label,
    labelMode: _labelMode,
    /* eslint-enable @typescript-eslint/no-unused-vars */

    /* Mode & access */
    mode = 'edit',
    access: _access,

    /* Validation */
    required = false,
    disabled = false,
    error,

    /* Appearance */
    className,
    placeholder = 'fas fa-home',

    /* eslint-disable @typescript-eslint/no-unused-vars */
    description: _description,
    isVisible: _isVisible,
    /* eslint-enable @typescript-eslint/no-unused-vars */

    /* Messages */
    emptyValueMessage = 'no data',
    accessDeniedMessage: _accessDeniedMessage,

    /* Locale */
    locale: _locale,

    /* Icon-specific */
    value,
    onChange,

    /* Collect remaining so they don't spread onto DOM elements */
    ...restProps
  } = props;

  // Suppress unused-variable warnings for destructured-but-unused props
  void _label;
  void _labelMode;
  void _access;
  void _description;
  void _isVisible;
  void _accessDeniedMessage;
  void _locale;
  void restProps;

  /* ---- Local State ---- */

  /** Local text input value — kept in sync with `value` prop */
  const [inputValue, setInputValue] = useState<string>(value ?? '');

  /** Whether the icon-picker dropdown is visible */
  const [isDropdownOpen, setIsDropdownOpen] = useState<boolean>(false);

  /** Search/filter text within the dropdown */
  const [filterText, setFilterText] = useState<string>('');

  /* ---- Memoized Values ---- */

  /**
   * Determines whether the current input looks like a valid icon class
   * for rendering the preview <i> element.
   */
  const isValidPreview = useMemo(
    () => looksLikeIconClass(inputValue),
    [inputValue],
  );

  /**
   * Filtered subset of COMMON_ICONS that match the current filter text.
   * Matches against both the human-readable label and the CSS class string.
   * Capped at MAX_DROPDOWN_ITEMS to avoid rendering hundreds of DOM nodes.
   */
  const filteredIcons = useMemo(() => {
    const query = filterText.trim().toLowerCase();
    if (query.length === 0) {
      return COMMON_ICONS.slice(0, MAX_DROPDOWN_ITEMS);
    }
    const results: IconEntry[] = [];
    for (const entry of COMMON_ICONS) {
      if (results.length >= MAX_DROPDOWN_ITEMS) break;
      if (
        entry.label.toLowerCase().includes(query) ||
        entry.className.toLowerCase().includes(query)
      ) {
        results.push(entry);
      }
    }
    return results;
  }, [filterText]);

  /* ---- Handlers ---- */

  /**
   * Handles text input changes — updates local state and propagates
   * the new icon class to the parent via onChange.
   */
  const handleInputChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>): void => {
      const newValue = e.target.value;
      setInputValue(newValue);
      onChange?.(newValue);
    },
    [onChange],
  );

  /**
   * Handles selection of an icon from the dropdown picker.
   * Sets the value, closes the dropdown, and resets the filter.
   */
  const handleIconSelect = useCallback(
    (iconClass: string): void => {
      setInputValue(iconClass);
      onChange?.(iconClass);
      setIsDropdownOpen(false);
      setFilterText('');
    },
    [onChange],
  );

  /** Toggle the dropdown open/closed */
  const handleToggleDropdown = useCallback((): void => {
    setIsDropdownOpen((prev) => {
      if (!prev) {
        /* Reset filter when opening */
        setFilterText('');
      }
      return !prev;
    });
  }, []);

  /** Close the dropdown (used by blur/outside-click) */
  const handleCloseDropdown = useCallback((): void => {
    /* Small delay so click on dropdown item registers before close */
    setTimeout(() => {
      setIsDropdownOpen(false);
    }, 200);
  }, []);

  /** Update filter text in dropdown search */
  const handleFilterChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>): void => {
      setFilterText(e.target.value);
    },
    [],
  );

  /* ---- Keyboard handler for dropdown items ---- */
  const handleIconKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLButtonElement>, iconClass: string): void => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        handleIconSelect(iconClass);
      }
    },
    [handleIconSelect],
  );

  /* ================================================================ */
  /*  DISPLAY MODE                                                     */
  /* ================================================================ */

  if (mode === 'display') {
    /* Empty value */
    if (!value || value.trim().length === 0) {
      return (
        <span
          className={[
            'text-sm text-gray-400 italic',
            className,
          ]
            .filter(Boolean)
            .join(' ')}
        >
          {emptyValueMessage}
        </span>
      );
    }

    /* Render icon + class name */
    const hasValidIcon = looksLikeIconClass(value);

    return (
      <span
        className={[
          'inline-flex items-center gap-2 text-sm text-gray-900',
          className,
        ]
          .filter(Boolean)
          .join(' ')}
      >
        {hasValidIcon && (
          <i
            className={value}
            aria-hidden="true"
            role="img"
          />
        )}
        <span className="font-mono text-xs text-gray-600 select-all">
          {value}
        </span>
      </span>
    );
  }

  /* ================================================================ */
  /*  EDIT MODE                                                        */
  /* ================================================================ */

  const fieldId = `field-${name}`;
  const errorId = error ? `${name}-error` : undefined;

  return (
    <div className={['relative', className].filter(Boolean).join(' ')}>
      {/* ---- Input row: preview + text input + dropdown toggle ---- */}
      <div className="flex items-center gap-2">
        {/* Live icon preview */}
        <span
          className={[
            'flex h-10 w-10 shrink-0 items-center justify-center rounded-md border',
            isValidPreview
              ? 'border-gray-300 bg-white text-gray-700'
              : 'border-dashed border-gray-300 bg-gray-50 text-gray-400',
          ].join(' ')}
          aria-hidden="true"
        >
          {isValidPreview ? (
            <i className={inputValue} aria-hidden="true" />
          ) : (
            <i className="fas fa-icons" aria-hidden="true" />
          )}
        </span>

        {/* Text input for icon class */}
        <input
          type="text"
          id={fieldId}
          name={name}
          value={inputValue}
          onChange={handleInputChange}
          onFocus={() => {}}
          placeholder={placeholder}
          disabled={disabled}
          required={required}
          autoComplete="off"
          spellCheck={false}
          className={[
            'block w-full rounded-md border px-3 py-2 text-sm font-mono shadow-sm',
            'focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500',
            'disabled:bg-gray-100 disabled:text-gray-500',
            error
              ? 'border-red-300 text-red-900 focus:border-red-500 focus:ring-red-500'
              : 'border-gray-300 text-gray-900',
          ].join(' ')}
          aria-invalid={Boolean(error)}
          aria-describedby={errorId}
        />

        {/* Dropdown toggle button */}
        <button
          type="button"
          onClick={handleToggleDropdown}
          onBlur={handleCloseDropdown}
          disabled={disabled}
          className={[
            'inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-md border',
            'border-gray-300 bg-white text-gray-500 shadow-sm',
            'hover:bg-gray-50 hover:text-gray-700',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-1',
            'disabled:cursor-not-allowed disabled:opacity-50',
          ].join(' ')}
          aria-label="Open icon picker"
          aria-expanded={isDropdownOpen}
          aria-haspopup="listbox"
        >
          <ChevronDownIcon />
        </button>
      </div>

      {/* ---- Dropdown icon picker ---- */}
      {isDropdownOpen && !disabled && (
        <div
          className={[
            'absolute inset-inline-start-0 z-20 mt-1 w-full',
            'max-h-72 overflow-hidden rounded-md border border-gray-200 bg-white shadow-lg',
            'flex flex-col',
          ].join(' ')}
          role="dialog"
          aria-label="Icon picker"
        >
          {/* Search filter input */}
          <div className="flex items-center gap-2 border-b border-gray-200 px-3 py-2">
            <span className="text-gray-400">
              <SearchIcon />
            </span>
            <input
              type="text"
              value={filterText}
              onChange={handleFilterChange}
              placeholder="Search icons…"
              className="w-full border-0 bg-transparent text-sm text-gray-900 placeholder:text-gray-400 focus:outline-none focus:ring-0"
              autoComplete="off"
              spellCheck={false}
              /* eslint-disable-next-line jsx-a11y/no-autofocus -- dropdown context, focus is expected */
              autoFocus
              aria-label="Filter icons"
            />
          </div>

          {/* Icon grid */}
          <div
            className="overflow-y-auto p-2"
            role="listbox"
            aria-label="Available icons"
          >
            {filteredIcons.length === 0 ? (
              <p className="px-3 py-4 text-center text-sm text-gray-400">
                No icons match your search.
              </p>
            ) : (
              <div className="grid grid-cols-6 gap-1 sm:grid-cols-8">
                {filteredIcons.map((entry) => (
                  <button
                    key={entry.className}
                    type="button"
                    role="option"
                    aria-selected={inputValue === entry.className}
                    aria-label={entry.label}
                    title={`${entry.label} (${entry.className})`}
                    className={[
                      'flex h-10 w-full items-center justify-center rounded-md text-base',
                      'transition-colors duration-150',
                      'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500',
                      inputValue === entry.className
                        ? 'bg-blue-100 text-blue-700'
                        : 'text-gray-600 hover:bg-gray-100 hover:text-gray-900',
                    ].join(' ')}
                    onMouseDown={(e) => {
                      /* Prevent blur on the toggle button from firing before selection */
                      e.preventDefault();
                    }}
                    onClick={() => handleIconSelect(entry.className)}
                    onKeyDown={(e) => handleIconKeyDown(e, entry.className)}
                  >
                    <i className={entry.className} aria-hidden="true" />
                  </button>
                ))}
              </div>
            )}
          </div>

          {/* Showing count footer */}
          <div className="border-t border-gray-200 px-3 py-1.5 text-xs text-gray-400">
            {filteredIcons.length === 0
              ? '0 icons'
              : filteredIcons.length === 1
                ? '1 icon'
                : `${filteredIcons.length} icons`}
            {filterText.trim().length > 0 && ' matching'}
          </div>
        </div>
      )}
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  Default Export                                                      */
/* ------------------------------------------------------------------ */

export default IconField;
