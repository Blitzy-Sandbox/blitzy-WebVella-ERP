/**
 * UrlField — URL Input Field Component
 *
 * React replacement for the monolith's PcFieldUrl ViewComponent
 * (WebVella.Erp.Web/Components/PcFieldUrl/PcFieldUrl.cs).
 *
 * Provides a URL input in edit mode and a clickable link preview in
 * display mode. Display mode features:
 *   - Truncated URL text (protocol stripped, max 50 characters)
 *   - Inline external-link icon for new-window targets
 *   - Configurable target window (new tab vs same tab)
 *   - Security attributes (rel="noopener noreferrer") on external links
 *
 * The component supports both controlled (value + onChange from parent)
 * and semi-uncontrolled (value=null, internal draft state) modes.
 *
 * @module components/fields/UrlField
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the UrlField component.
 *
 * Extends shared BaseFieldProps (omitting value/onChange for URL-specific
 * types) with URL-specific configuration options.
 */
export interface UrlFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Current URL value. Null indicates no value set. */
  value: string | null;

  /** Callback invoked when the URL value changes in edit mode. */
  onChange?: (value: string) => void;

  /** Maximum character length for the URL input. Null means no limit. */
  maxLength?: number | null;

  /**
   * Whether to open the URL in a new browser window/tab when clicked
   * in display mode. Defaults to `true`.
   */
  openTargetInNewWindow?: boolean;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Maximum number of characters for the truncated URL display text.
 * URLs longer than this are ellipsis-truncated in display mode.
 */
const DISPLAY_TRUNCATE_LENGTH = 50;

// ---------------------------------------------------------------------------
// Utility Helpers
// ---------------------------------------------------------------------------

/**
 * Joins CSS class name fragments, filtering out falsy values.
 *
 * @param parts - Variable number of class strings (or falsy values to skip)
 * @returns A single space-separated class string
 */
function joinClassNames(
  ...parts: Array<string | undefined | null | false>
): string {
  return parts.filter(Boolean).join(' ');
}

/**
 * Strips the protocol prefix (http:// or https://) and a single trailing
 * slash from a URL string for cleaner display text.
 *
 * @example
 *   stripProtocol('https://example.com/path/')
 *   // → 'example.com/path'
 *
 * @param url - The full URL string to strip
 * @returns The URL without protocol prefix and trailing slash
 */
function stripProtocol(url: string): string {
  return url.replace(/^https?:\/\//, '').replace(/\/$/, '');
}

/**
 * Truncates a string to the specified maximum length, appending an
 * ellipsis character (…) when truncation occurs.
 *
 * @param text - The text to potentially truncate
 * @param maxLength - Maximum allowed length before truncation
 * @returns The original string or its truncated form with ellipsis
 */
function truncateText(text: string, maxLength: number): string {
  if (text.length <= maxLength) {
    return text;
  }
  return `${text.slice(0, maxLength)}…`;
}

/**
 * Ensures a URL string has a protocol prefix. If the URL already starts
 * with `http://` or `https://`, it is returned as-is. Otherwise
 * `https://` is prepended.
 *
 * @param url - The URL string to normalise
 * @returns A URL string guaranteed to start with a protocol
 */
function ensureProtocol(url: string): string {
  if (/^https?:\/\//i.test(url)) {
    return url;
  }
  return `https://${url}`;
}

// ---------------------------------------------------------------------------
// External Link Icon
// ---------------------------------------------------------------------------

/**
 * Inline SVG icon indicating an external link (arrow pointing out of a box).
 * Uses `currentColor` for fill so it inherits the parent text color.
 * Marked `aria-hidden` since it is purely decorative alongside link text.
 */
function ExternalLinkIcon(): React.JSX.Element {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 20 20"
      fill="currentColor"
      className="inline-block h-3.5 w-3.5 flex-shrink-0"
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        d="M4.25 5.5a.75.75 0 0 0-.75.75v8.5c0 .414.336.75.75.75h8.5a.75.75 0 0 0 .75-.75v-4a.75.75 0 0 1 1.5 0v4A2.25 2.25 0 0 1 12.75 17h-8.5A2.25 2.25 0 0 1 2 14.75v-8.5A2.25 2.25 0 0 1 4.25 4h5a.75.75 0 0 1 0 1.5h-5Zm7.5-2.25a.75.75 0 0 1 .75-.75h4.5a.75.75 0 0 1 .75.75v4.5a.75.75 0 0 1-1.5 0V5.56l-5.22 5.22a.75.75 0 1 1-1.06-1.06l5.22-5.22H12.5a.75.75 0 0 1-.75-.75Z"
        clipRule="evenodd"
      />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// UrlField Component
// ---------------------------------------------------------------------------

/**
 * UrlField — URL input field with link preview in display mode.
 *
 * **Edit mode:** Renders a standard `<input type="url">` with Tailwind
 * styling, optional maxLength enforcement, and ARIA-compliant error display.
 *
 * **Display mode:** Renders the URL as a clickable `<a>` link with:
 *   - Truncated display text (protocol stripped, max 50 chars with ellipsis)
 *   - External-link icon when `openTargetInNewWindow` is true
 *   - Security attributes (`rel="noopener noreferrer"`) for external links
 *   - Empty-value placeholder when value is null/empty
 *
 * The component respects the FieldRenderer access/mode computation but
 * also provides defense-in-depth guards for standalone usage outside
 * the FieldRenderer wrapper.
 *
 * @param props - UrlFieldProps
 * @returns JSX element for the URL field, or null if hidden
 */
function UrlField(props: UrlFieldProps): React.JSX.Element | null {
  const {
    // ----- 15 BaseFieldProps members_accessed -----
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
    // ----- URL-specific props -----
    value,
    onChange,
    maxLength,
    openTargetInNewWindow = true,
    // ----- Additional BaseFieldProps -----
    fieldId,
  } = props;

  // -----------------------------------------------------------------------
  // Hooks — must be called unconditionally (Rules of Hooks)
  // -----------------------------------------------------------------------

  /**
   * Local draft value for semi-uncontrolled mode. When `value` is null
   * (no parent-controlled value), the component falls back to this
   * internal state for the edit input.
   */
  const [localValue, setLocalValue] = useState<string>('');

  /**
   * Memoized change handler that updates both the internal draft state
   * and propagates the new value to the parent via `onChange`.
   */
  const handleChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const newValue = e.target.value;
      setLocalValue(newValue);
      onChange?.(newValue);
    },
    [onChange],
  );

  /**
   * Computed display information for display mode: the truncated text
   * shown inside the link, and the full href (guaranteed to have a protocol).
   */
  const displayInfo = useMemo(() => {
    if (!value) {
      return { displayText: '', href: '' };
    }
    const stripped = stripProtocol(value);
    const displayText = truncateText(stripped, DISPLAY_TRUNCATE_LENGTH);
    const href = ensureProtocol(value);
    return { displayText, href };
  }, [value]);

  // -----------------------------------------------------------------------
  // Early Returns (after all hooks)
  // -----------------------------------------------------------------------

  // Visibility guard — defense in depth (FieldRenderer also checks)
  if (!isVisible) {
    return null;
  }

  // Access-denied guard — defense in depth (FieldRenderer also checks)
  if (access === 'forbidden') {
    return (
      <span
        className="text-sm italic text-red-400"
        role="alert"
        data-field-name={name}
      >
        {accessDeniedMessage}
      </span>
    );
  }

  // -----------------------------------------------------------------------
  // Derived State
  // -----------------------------------------------------------------------

  // Effective mode: readonly access forces display mode
  const effectiveMode = access === 'readonly' ? 'display' : mode;

  // Effective disabled: readonly access forces disabled state
  const effectiveDisabled = disabled || access === 'readonly';

  // Control ID for label ↔ input association and ARIA references
  const controlId = fieldId ?? `field-${name}`;

  // When label mode is hidden, provide an accessible name via aria-label
  const ariaLabel =
    labelMode === 'hidden' && label ? label : undefined;

  // ARIA describedby: error takes precedence, then description
  const ariaDescribedBy = error
    ? `${name}-error`
    : description
      ? `${name}-description`
      : undefined;

  // -----------------------------------------------------------------------
  // Display Mode
  // -----------------------------------------------------------------------

  if (effectiveMode === 'display') {
    // Empty value → placeholder message
    if (!value) {
      return (
        <span
          className={joinClassNames(
            'text-sm italic text-gray-400',
            className,
          )}
          data-field-name={name}
          lang={locale}
        >
          {emptyValueMessage}
        </span>
      );
    }

    // Render clickable URL link with optional external-link icon
    return (
      <span
        className={joinClassNames(
          'inline-flex items-center gap-1 text-sm',
          className,
        )}
        data-field-name={name}
        lang={locale}
      >
        <a
          href={displayInfo.href}
          target={openTargetInNewWindow ? '_blank' : '_self'}
          rel={openTargetInNewWindow ? 'noopener noreferrer' : undefined}
          className={joinClassNames(
            'text-blue-600 underline decoration-blue-300 underline-offset-2',
            'hover:text-blue-800 hover:decoration-blue-500',
            'focus-visible:outline-none focus-visible:ring-2',
            'focus-visible:ring-blue-500 focus-visible:ring-offset-1',
          )}
          title={value}
        >
          {displayInfo.displayText}
        </a>
        {openTargetInNewWindow && <ExternalLinkIcon />}
      </span>
    );
  }

  // -----------------------------------------------------------------------
  // Edit Mode
  // -----------------------------------------------------------------------

  // Controlled value from parent, or internal draft when value is null
  const inputValue = value !== null ? value : localValue;

  // Build the input class string with conditional error-state styling
  const inputClassName = joinClassNames(
    'block w-full rounded-md border px-3 py-2 text-sm shadow-sm',
    'placeholder:text-gray-400',
    'focus:outline-none focus:ring-1',
    'disabled:bg-gray-100 disabled:text-gray-500',
    error
      ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
      : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500',
    className,
  );

  return (
    <input
      type="url"
      id={controlId}
      name={name}
      value={inputValue}
      onChange={handleChange}
      placeholder={placeholder}
      disabled={effectiveDisabled}
      required={required}
      maxLength={maxLength ?? undefined}
      className={inputClassName}
      aria-invalid={error ? true : undefined}
      aria-describedby={ariaDescribedBy}
      aria-required={required || undefined}
      aria-label={ariaLabel}
      autoComplete="url"
      data-field-name={name}
      lang={locale}
    />
  );
}

export default UrlField;
