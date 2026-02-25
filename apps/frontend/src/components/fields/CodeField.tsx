/**
 * CodeField.tsx — Code Editor Field Component
 *
 * React replacement for the monolith's PcFieldCode ViewComponent.
 * Provides a code editor textarea with monospace font, tab key handling,
 * line numbers, dark/light theme support, and display mode rendering
 * via <pre><code> blocks.
 *
 * Source: WebVella.Erp.Web/Components/PcFieldCode/PcFieldCode.cs
 * Original options: Height (default "120px"), Language (default "razor"), Theme (default "cobalt")
 */

import React, { useState, useCallback, useRef } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

/* ------------------------------------------------------------------ */
/*  Exported Interface                                                 */
/* ------------------------------------------------------------------ */

/**
 * Props for the CodeField component.
 *
 * Extends BaseFieldProps (omitting value/onChange) with code-editor-specific
 * options: height, language, and theme.
 */
export interface CodeFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Code content as a string, or null when empty. */
  value: string | null;
  /** Callback fired when the code content changes in edit mode. */
  onChange?: (value: string) => void;
  /** CSS height of the editor area. Defaults to "120px". */
  height?: string;
  /** Syntax language identifier (informational — used as data attribute). Defaults to "javascript". */
  language?: string;
  /** Color theme: "dark" or "light". Defaults to "dark". */
  theme?: string;
}

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** Default height matching the monolith's PcFieldCodeOptions default. */
const DEFAULT_HEIGHT = '120px';

/** Default language — changed from monolith "razor" to "javascript" per AAP. */
const DEFAULT_LANGUAGE = 'javascript';

/** Default theme — "dark" per AAP (monolith used "cobalt"). */
const DEFAULT_THEME = 'dark';

/** Tab character inserted when Tab key is pressed. */
const TAB_CHARACTER = '  ';

/* ------------------------------------------------------------------ */
/*  Line Numbers Helper                                                */
/* ------------------------------------------------------------------ */

/**
 * Computes an array of line numbers from code content.
 * Used for the line-number gutter in edit mode.
 */
function getLineNumbers(code: string | null): number[] {
  if (!code) {
    return [1];
  }
  const lines = code.split('\n');
  return lines.map((_, index) => index + 1);
}

/* ------------------------------------------------------------------ */
/*  Theme Utilities                                                    */
/* ------------------------------------------------------------------ */

/**
 * Returns Tailwind CSS classes for the editor container based on theme.
 */
function getEditorThemeClasses(theme: string): string {
  if (theme === 'light') {
    return 'bg-white text-gray-900 border-gray-300';
  }
  // Dark theme (default)
  return 'bg-gray-900 text-green-400 border-gray-700';
}

/**
 * Returns Tailwind CSS classes for the line-number gutter based on theme.
 */
function getGutterThemeClasses(theme: string): string {
  if (theme === 'light') {
    return 'bg-gray-100 text-gray-400 border-gray-300';
  }
  return 'bg-gray-800 text-gray-500 border-gray-700';
}

/**
 * Returns Tailwind CSS classes for the display <pre><code> block based on theme.
 */
function getDisplayThemeClasses(theme: string): string {
  if (theme === 'light') {
    return 'bg-gray-50 text-gray-900 border-gray-300';
  }
  return 'bg-gray-900 text-green-400 border-gray-700';
}

/* ------------------------------------------------------------------ */
/*  CodeField Component                                                */
/* ------------------------------------------------------------------ */

/**
 * CodeField — A code editor field component.
 *
 * Edit mode: renders a monospace <textarea> with tab key handling,
 * line-number gutter, and dark/light theme support.
 *
 * Display mode: renders code inside a <pre><code> block with
 * overflow handling and theme-based styling.
 */
function CodeField(props: CodeFieldProps): React.JSX.Element | null {
  const {
    /* CodeField-specific props */
    value,
    onChange,
    height = DEFAULT_HEIGHT,
    language = DEFAULT_LANGUAGE,
    theme = DEFAULT_THEME,

    /* BaseFieldProps — used for mode/access/visibility/display control */
    name,
    label,
    labelMode,
    mode = 'display',
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
  } = props;

  /* ---- Refs ---- */
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  /* ---- Local State ---- */
  const [isFocused, setIsFocused] = useState(false);

  /* ---- Derived Values ---- */
  const effectiveDisabled = disabled || access === 'readonly';
  const isEditMode = mode === 'edit';
  const displayValue = value ?? '';
  const lineNumbers = getLineNumbers(displayValue);
  const resolvedTheme = theme === 'light' ? 'light' : 'dark';

  /* ---- Handlers ---- */

  /**
   * Handles text changes in the textarea and propagates to the parent.
   */
  const handleChange = useCallback(
    (event: React.ChangeEvent<HTMLTextAreaElement>) => {
      if (onChange && !effectiveDisabled) {
        onChange(event.target.value);
      }
    },
    [onChange, effectiveDisabled],
  );

  /**
   * Intercepts the Tab key to insert a tab character at the current
   * cursor position instead of moving focus to the next element.
   * Also supports Shift+Tab for outdenting selected lines.
   */
  const handleKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLTextAreaElement>) => {
      if (event.key !== 'Tab') {
        return;
      }

      event.preventDefault();

      const textarea = textareaRef.current;
      if (!textarea || effectiveDisabled) {
        return;
      }

      const { selectionStart, selectionEnd } = textarea;
      const currentValue = textarea.value;

      if (event.shiftKey) {
        // Shift+Tab: remove leading tab/spaces from the current line
        const beforeCursor = currentValue.substring(0, selectionStart);
        const lineStart = beforeCursor.lastIndexOf('\n') + 1;
        const linePrefix = currentValue.substring(lineStart, selectionStart);

        if (linePrefix.startsWith(TAB_CHARACTER)) {
          const newValue =
            currentValue.substring(0, lineStart) +
            currentValue.substring(lineStart + TAB_CHARACTER.length);
          if (onChange) {
            onChange(newValue);
          }
          // Restore cursor position after outdent
          requestAnimationFrame(() => {
            const newPos = Math.max(selectionStart - TAB_CHARACTER.length, lineStart);
            textarea.setSelectionRange(newPos, newPos);
          });
        }
      } else {
        // Tab: insert tab character at cursor position
        const newValue =
          currentValue.substring(0, selectionStart) +
          TAB_CHARACTER +
          currentValue.substring(selectionEnd);

        if (onChange) {
          onChange(newValue);
        }

        // Restore cursor position after tab insertion
        requestAnimationFrame(() => {
          const newPos = selectionStart + TAB_CHARACTER.length;
          textarea.setSelectionRange(newPos, newPos);
        });
      }
    },
    [effectiveDisabled, onChange],
  );

  /**
   * Handles focus events for focus ring styling.
   */
  const handleFocus = useCallback(() => {
    setIsFocused(true);
  }, []);

  /**
   * Handles blur events for focus ring styling.
   */
  const handleBlur = useCallback(() => {
    setIsFocused(false);
  }, []);

  /* ---- Visibility Guard ---- */
  if (!isVisible) {
    return null;
  }

  /* ---- Access Denied ---- */
  if (access === 'forbidden') {
    return (
      <div
        className={[
          'flex items-center gap-2 rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-500',
          className,
        ]
          .filter(Boolean)
          .join(' ')}
        role="status"
        aria-label={accessDeniedMessage}
      >
        {/* Lock icon */}
        <svg
          className="h-4 w-4 shrink-0 text-gray-400"
          viewBox="0 0 20 20"
          fill="currentColor"
          aria-hidden="true"
        >
          <path
            fillRule="evenodd"
            d="M10 1a4.5 4.5 0 00-4.5 4.5V9H5a2 2 0 00-2 2v6a2 2 0 002 2h10a2 2 0 002-2v-6a2 2 0 00-2-2h-.5V5.5A4.5 4.5 0 0010 1zm3 8V5.5a3 3 0 10-6 0V9h6z"
            clipRule="evenodd"
          />
        </svg>
        <span>{accessDeniedMessage}</span>
      </div>
    );
  }

  /* ---- Display Mode ---- */
  if (!isEditMode) {
    // When there is no content, show the empty value message
    if (!displayValue) {
      return (
        <div
          className={['text-sm italic text-gray-400', className].filter(Boolean).join(' ')}
          data-field-name={name}
          data-field-language={language}
          data-label-mode={labelMode}
        >
          {emptyValueMessage}
        </div>
      );
    }

    return (
      <div
        className={['relative', className].filter(Boolean).join(' ')}
        data-field-name={name}
        data-field-language={language}
        data-label-mode={labelMode}
      >
        <pre
          className={[
            'overflow-auto rounded-md border p-3 font-mono text-sm',
            getDisplayThemeClasses(resolvedTheme),
          ].join(' ')}
          style={{ height, maxHeight: '80vh' }}
          tabIndex={0}
          role="region"
          aria-label={label ? `${label} code` : `${name} code`}
        >
          <code lang={locale}>{displayValue}</code>
        </pre>
        {description && (
          <p className="mt-1 text-sm text-gray-500">{description}</p>
        )}
      </div>
    );
  }

  /* ---- Edit Mode ---- */
  const editorTheme = getEditorThemeClasses(resolvedTheme);
  const gutterTheme = getGutterThemeClasses(resolvedTheme);

  return (
    <div
      className={['relative', className].filter(Boolean).join(' ')}
      data-field-name={name}
      data-field-language={language}
      data-label-mode={labelMode}
    >
      <div
        className={[
          'flex overflow-hidden rounded-md border font-mono text-sm',
          editorTheme,
          isFocused ? 'ring-2 ring-blue-500 ring-offset-0' : '',
          error ? 'border-red-500' : '',
          effectiveDisabled ? 'cursor-not-allowed opacity-60' : '',
        ]
          .filter(Boolean)
          .join(' ')}
        style={{ height }}
      >
        {/* Line Numbers Gutter */}
        <div
          className={[
            'flex shrink-0 select-none flex-col items-end overflow-hidden border-inline-end px-2 py-3 text-right text-xs leading-5',
            gutterTheme,
          ].join(' ')}
          aria-hidden="true"
          style={{ minWidth: '3rem' }}
        >
          {lineNumbers.map((num) => (
            <span key={num}>{num}</span>
          ))}
        </div>

        {/* Textarea Editor */}
        <textarea
          ref={textareaRef}
          id={name}
          name={name}
          value={displayValue}
          onChange={handleChange}
          onKeyDown={handleKeyDown}
          onFocus={handleFocus}
          onBlur={handleBlur}
          disabled={effectiveDisabled}
          required={required}
          placeholder={placeholder}
          aria-label={label || name}
          aria-required={required}
          aria-invalid={error ? true : undefined}
          aria-describedby={
            [
              error ? `${name}-error` : '',
              description ? `${name}-description` : '',
            ]
              .filter(Boolean)
              .join(' ') || undefined
          }
          className={[
            'w-full resize-none bg-transparent p-3 leading-5 outline-none',
            'overflow-auto',
            effectiveDisabled ? 'cursor-not-allowed' : '',
          ]
            .filter(Boolean)
            .join(' ')}
          lang={locale}
          spellCheck={false}
          autoCapitalize="off"
          autoCorrect="off"
          data-language={language}
          data-theme={resolvedTheme}
        />
      </div>

      {/* Error Message */}
      {error && (
        <p id={`${name}-error`} className="mt-1 text-sm text-red-600" role="alert">
          {error}
        </p>
      )}

      {/* Description */}
      {description && (
        <p id={`${name}-description`} className="mt-1 text-sm text-gray-500">
          {description}
        </p>
      )}
    </div>
  );
}

export default CodeField;
