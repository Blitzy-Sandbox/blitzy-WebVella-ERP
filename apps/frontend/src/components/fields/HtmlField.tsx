/**
 * HtmlField — Rich Text / HTML Editor Field Component
 *
 * React replacement for the monolith's PcFieldHtml ViewComponent.
 * Provides a contentEditable-based rich text editor with three toolbar
 * modes (basic / standard / full) and configurable image upload modes
 * (none / base64 / url).
 *
 * Display mode renders sanitized HTML via dangerouslySetInnerHTML with
 * DOMPurify for XSS prevention (OWASP Top 10 compliance).
 *
 * Source: WebVella.Erp.Web/Components/PcFieldHtml/PcFieldHtml.cs
 * Source: WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import React, { useState, useCallback, useRef, useMemo } from 'react';
import DOMPurify from 'dompurify';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Type Exports — maps from C# HtmlUploadMode / HtmlToolbarMode enums
// ---------------------------------------------------------------------------

/**
 * Image upload mode for the HTML editor.
 *
 *   - `'none'`   → HtmlUploadMode.None   — No image upload capability
 *   - `'base64'` → HtmlUploadMode.Base64 — Images embedded as Base64 data URIs
 *   - `'url'`    → HtmlUploadMode.Url    — Images referenced by external URL
 */
export type HtmlUploadMode = 'none' | 'base64' | 'url';

/**
 * Toolbar button set mode for the HTML editor.
 *
 *   - `'basic'`    → HtmlToolbarMode.Basic    — Bold, Italic, Underline, Link
 *   - `'standard'` → HtmlToolbarMode.Standard — + Lists, Headings, Quote, Image
 *   - `'full'`     → HtmlToolbarMode.Full     — + Table, Code, Font size, Color,
 *                                                 Alignment, Undo/Redo
 */
export type HtmlToolbarMode = 'basic' | 'standard' | 'full';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the HtmlField component.
 *
 * Extends BaseFieldProps via Omit to override `value` and `onChange` with
 * HTML-content-specific types.
 */
export interface HtmlFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** HTML content string, or null for empty. */
  value: string | null;

  /** Callback invoked with the updated HTML string on content change. */
  onChange?: (value: string) => void;

  /** Controls how images are uploaded in the editor. Defaults to 'none'. */
  uploadMode?: HtmlUploadMode;

  /** Controls which toolbar buttons are displayed. Defaults to 'basic'. */
  toolbarMode?: HtmlToolbarMode;
}

// ---------------------------------------------------------------------------
// DOMPurify Configuration
// ---------------------------------------------------------------------------

/**
 * Allowed HTML tags for sanitization.
 * Preserves common rich text formatting tags while blocking script/event
 * handler injection vectors.
 */
const SANITIZE_CONFIG: DOMPurify.Config = {
  ALLOWED_TAGS: [
    'p', 'br', 'b', 'strong', 'i', 'em', 'u', 'a', 's', 'strike', 'del',
    'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
    'ul', 'ol', 'li',
    'blockquote', 'pre', 'code',
    'table', 'thead', 'tbody', 'tfoot', 'tr', 'th', 'td',
    'img', 'figure', 'figcaption',
    'div', 'span', 'sub', 'sup', 'hr',
  ],
  ALLOWED_ATTR: [
    'href', 'target', 'rel', 'src', 'alt', 'title', 'width', 'height',
    'class', 'style', 'colspan', 'rowspan', 'scope',
  ],
  ALLOW_DATA_ATTR: false,
};

/**
 * Sanitize an HTML string using DOMPurify with the configured allowlist.
 * Returns empty string for null/undefined input.
 */
function sanitizeHtml(html: string | null | undefined): string {
  if (!html) {
    return '';
  }
  return DOMPurify.sanitize(html, SANITIZE_CONFIG) as string;
}

// ---------------------------------------------------------------------------
// Toolbar Button Definitions
// ---------------------------------------------------------------------------

/** Describes a single toolbar button action. */
interface ToolbarButton {
  /** Unique key for React list rendering. */
  key: string;
  /** Accessible label and tooltip text. */
  label: string;
  /** The `document.execCommand` command name. */
  command: string;
  /** Optional argument passed to execCommand. */
  commandArg?: string;
  /** Inline SVG icon path data (viewBox 0 0 24 24). */
  iconPath: string;
  /** If true, this button requires special handling (not a simple execCommand). */
  isCustom?: boolean;
}

/** Separator element rendered between button groups. */
const TOOLBAR_SEPARATOR: ToolbarButton = {
  key: 'sep',
  label: '',
  command: '',
  iconPath: '',
  isCustom: true,
};

/** Basic toolbar: Bold, Italic, Underline, Link */
const BASIC_BUTTONS: ToolbarButton[] = [
  {
    key: 'bold',
    label: 'Bold',
    command: 'bold',
    iconPath: 'M6.5 4h5a3.5 3.5 0 0 1 2.47 5.97A3.5 3.5 0 0 1 12 18H6.5V4Zm2 2v4h3a1.5 1.5 0 1 0 0-3h-3Zm0 6v4h3.5a1.5 1.5 0 1 0 0-3H8.5Z',
  },
  {
    key: 'italic',
    label: 'Italic',
    command: 'italic',
    iconPath: 'M10 4h6l-.5 2h-2.03l-2.94 12H13l-.5 2H6.5l.5-2h2.03l2.94-12H9.5l.5-2Z',
  },
  {
    key: 'underline',
    label: 'Underline',
    command: 'underline',
    iconPath: 'M7 4v7a5 5 0 0 0 10 0V4h-2v7a3 3 0 0 1-6 0V4H7ZM5 20h14v2H5v-2Z',
  },
  {
    key: 'link',
    label: 'Insert Link',
    command: 'createLink',
    iconPath: 'M10.59 13.41a2 2 0 0 0 2.83 0l3.17-3.17a2 2 0 0 0-2.83-2.83l-1.59 1.59M13.41 10.59a2 2 0 0 0-2.83 0l-3.17 3.17a2 2 0 0 0 2.83 2.83l1.59-1.59',
    isCustom: true,
  },
];

/** Standard additions: Lists, Headings, Quote, Image */
const STANDARD_BUTTONS: ToolbarButton[] = [
  { ...TOOLBAR_SEPARATOR, key: 'sep1' },
  {
    key: 'ul',
    label: 'Bullet List',
    command: 'insertUnorderedList',
    iconPath: 'M4 6h2v2H4V6Zm4 0h12v2H8V6ZM4 11h2v2H4v-2Zm4 0h12v2H8v-2ZM4 16h2v2H4v-2Zm4 0h12v2H8v-2Z',
  },
  {
    key: 'ol',
    label: 'Numbered List',
    command: 'insertOrderedList',
    iconPath: 'M3 4h2v5H4V5H3V4Zm5 2h12v2H8V6ZM3 11h3v1H3v1h2v1H3v1h3v1H2v-6h1Zm5 1h12v2H8v-2ZM8 18h12v2H8v-2Z',
  },
  {
    key: 'h2',
    label: 'Heading',
    command: 'formatBlock',
    commandArg: 'h2',
    iconPath: 'M4 4h2v7h5V4h2v16h-2v-7H6v7H4V4Zm14.5 4a2.5 2.5 0 0 0-2.5 2.5c0 1.38.56 2.5 2.5 4.5h-4v2h7v-2c-3-2.5-3.5-3.5-3.5-4.5a.5.5 0 0 1 1 0v1h2v-1a2.5 2.5 0 0 0-2.5-2.5Z',
  },
  {
    key: 'quote',
    label: 'Blockquote',
    command: 'formatBlock',
    commandArg: 'blockquote',
    iconPath: 'M6 17h3l2-4V7H5v6h3l-2 4Zm8 0h3l2-4V7h-6v6h3l-2 4Z',
  },
  {
    key: 'image',
    label: 'Insert Image',
    command: 'insertImage',
    iconPath: 'M21 19V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2ZM8.5 13.5l2.5 3 3.5-4.5 4.5 6H5l3.5-4.5Z',
    isCustom: true,
  },
];

/** Full additions: Table, Code, Font size, Color, Alignment, Undo/Redo */
const FULL_BUTTONS: ToolbarButton[] = [
  { ...TOOLBAR_SEPARATOR, key: 'sep2' },
  {
    key: 'code',
    label: 'Code Block',
    command: 'formatBlock',
    commandArg: 'pre',
    iconPath: 'M9.4 16.6 4.8 12l4.6-4.6L8 6l-6 6 6 6 1.4-1.4Zm5.2 0 4.6-4.6-4.6-4.6L16 6l6 6-6 6-1.4-1.4Z',
  },
  {
    key: 'alignLeft',
    label: 'Align Left',
    command: 'justifyLeft',
    iconPath: 'M3 3h18v2H3V3Zm0 4h12v2H3V7Zm0 4h18v2H3v-2Zm0 4h12v2H3v-2Zm0 4h18v2H3v-2Z',
  },
  {
    key: 'alignCenter',
    label: 'Align Center',
    command: 'justifyCenter',
    iconPath: 'M3 3h18v2H3V3Zm3 4h12v2H6V7ZM3 11h18v2H3v-2Zm3 4h12v2H6v-2ZM3 19h18v2H3v-2Z',
  },
  {
    key: 'alignRight',
    label: 'Align Right',
    command: 'justifyRight',
    iconPath: 'M3 3h18v2H3V3Zm6 4h12v2H9V7ZM3 11h18v2H3v-2Zm6 4h12v2H9v-2ZM3 19h18v2H3v-2Z',
  },
  { ...TOOLBAR_SEPARATOR, key: 'sep3' },
  {
    key: 'table',
    label: 'Insert Table',
    command: 'insertHTML',
    iconPath: 'M3 3h18v18H3V3Zm2 2v4h6V5H5Zm8 0v4h6V5h-6ZM5 11v4h6v-4H5Zm8 0v4h6v-4h-6ZM5 17v2h6v-2H5Zm8 0v2h6v-2h-6Z',
    isCustom: true,
  },
  {
    key: 'undo',
    label: 'Undo',
    command: 'undo',
    iconPath: 'M12.5 8c-2.65 0-5.05 1.04-6.83 2.73L3 8v9h9l-2.7-2.7A7.97 7.97 0 0 1 12.5 12c3.04 0 5.64 1.73 6.93 4.26L21.15 15A10.01 10.01 0 0 0 12.5 8Z',
  },
  {
    key: 'redo',
    label: 'Redo',
    command: 'redo',
    iconPath: 'M18.4 10.73A9.98 9.98 0 0 0 11.5 8c-3.82 0-7.16 2.14-8.85 5.26L4.37 14.5A7.97 7.97 0 0 1 11.5 10c1.59 0 3.05.5 4.2 1.3L13 14h9V5l-3.6 5.73Z',
  },
];

/**
 * Builds the toolbar button array based on the selected toolbar mode.
 */
function getToolbarButtons(mode: HtmlToolbarMode): ToolbarButton[] {
  switch (mode) {
    case 'full':
      return [...BASIC_BUTTONS, ...STANDARD_BUTTONS, ...FULL_BUTTONS];
    case 'standard':
      return [...BASIC_BUTTONS, ...STANDARD_BUTTONS];
    case 'basic':
    default:
      return [...BASIC_BUTTONS];
  }
}

// ---------------------------------------------------------------------------
// Toolbar Component
// ---------------------------------------------------------------------------

/** Props for the internal Toolbar component. */
interface ToolbarProps {
  buttons: ToolbarButton[];
  editorRef: React.RefObject<HTMLDivElement | null>;
  uploadMode: HtmlUploadMode;
  disabled: boolean;
}

/**
 * Renders the rich text toolbar above the contentEditable region.
 * Each button executes the corresponding `document.execCommand` or
 * performs custom actions (link insertion, image upload, table insertion).
 */
function Toolbar({ buttons, editorRef, uploadMode, disabled }: ToolbarProps): React.JSX.Element {
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  /**
   * Execute a standard execCommand on the editable region.
   * Uses document.execCommand which, while deprecated, is the only
   * approach that works reliably with contentEditable without a
   * heavyweight library dependency.
   */
  const execCommand = useCallback(
    (command: string, arg?: string) => {
      if (disabled) return;
      editorRef.current?.focus();
      document.execCommand(command, false, arg);
    },
    [disabled, editorRef]
  );

  /** Prompt the user for a URL and create a link. */
  const handleLink = useCallback(() => {
    if (disabled) return;
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return;

    const url = window.prompt('Enter URL:', 'https://');
    if (url) {
      editorRef.current?.focus();
      document.execCommand('createLink', false, url);
    }
  }, [disabled, editorRef]);

  /** Handle image insertion based on uploadMode. */
  const handleImage = useCallback(() => {
    if (disabled) return;

    if (uploadMode === 'base64') {
      fileInputRef.current?.click();
    } else if (uploadMode === 'url') {
      const url = window.prompt('Enter image URL:', 'https://');
      if (url) {
        editorRef.current?.focus();
        document.execCommand('insertImage', false, url);
      }
    } else {
      /* uploadMode === 'none' — image insertion not available */
      return;
    }
  }, [disabled, uploadMode, editorRef]);

  /** Read a file as Base64 data URI and insert as image. */
  const handleFileChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      const file = event.target.files?.[0];
      if (!file) return;

      const reader = new FileReader();
      reader.onload = () => {
        const dataUri = reader.result as string;
        editorRef.current?.focus();
        document.execCommand('insertImage', false, dataUri);
      };
      reader.readAsDataURL(file);

      /* Reset file input so the same file can be selected again. */
      event.target.value = '';
    },
    [editorRef]
  );

  /** Insert a simple 3×3 table. */
  const handleTable = useCallback(() => {
    if (disabled) return;
    const tableHtml =
      '<table style="border-collapse:collapse;width:100%">' +
      '<tbody>' +
      Array.from({ length: 3 })
        .map(
          () =>
            '<tr>' +
            Array.from({ length: 3 })
              .map(() => '<td style="border:1px solid #d1d5db;padding:0.5rem">&nbsp;</td>')
              .join('') +
            '</tr>'
        )
        .join('') +
      '</tbody></table><p><br></p>';
    editorRef.current?.focus();
    document.execCommand('insertHTML', false, tableHtml);
  }, [disabled, editorRef]);

  /** Dispatch button action based on its type. */
  const handleButtonClick = useCallback(
    (button: ToolbarButton) => {
      if (button.key === 'link') {
        handleLink();
      } else if (button.key === 'image') {
        handleImage();
      } else if (button.key === 'table') {
        handleTable();
      } else if (button.command === 'formatBlock' && button.commandArg) {
        execCommand(button.command, `<${button.commandArg}>`);
      } else {
        execCommand(button.command, button.commandArg);
      }
    },
    [execCommand, handleLink, handleImage, handleTable]
  );

  return (
    <div
      className="flex flex-wrap items-center gap-0.5 border-b border-gray-300 bg-gray-50 px-1 py-1"
      role="toolbar"
      aria-label="Text formatting"
    >
      {buttons.map((button) => {
        /* Render separator */
        if (button.label === '' && button.command === '') {
          return (
            <div
              key={button.key}
              className="mx-1 h-5 w-px bg-gray-300"
              role="separator"
              aria-orientation="vertical"
            />
          );
        }

        /* Skip image button when uploadMode is 'none'. */
        if (button.key === 'image' && uploadMode === 'none') {
          return null;
        }

        return (
          <button
            key={button.key}
            type="button"
            title={button.label}
            aria-label={button.label}
            disabled={disabled}
            onClick={() => handleButtonClick(button)}
            className="inline-flex items-center justify-center rounded p-1.5 text-gray-600 hover:bg-gray-200 hover:text-gray-900 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-blue-500 disabled:cursor-not-allowed disabled:opacity-40"
          >
            <svg
              className="h-4 w-4"
              viewBox="0 0 24 24"
              fill="currentColor"
              aria-hidden="true"
            >
              <path d={button.iconPath} />
            </svg>
          </button>
        );
      })}

      {/* Hidden file input for Base64 image upload */}
      {uploadMode === 'base64' && (
        <input
          ref={fileInputRef}
          type="file"
          accept="image/*"
          onChange={handleFileChange}
          className="sr-only"
          aria-hidden="true"
          tabIndex={-1}
        />
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Display Mode Component
// ---------------------------------------------------------------------------

/** Props for the internal HtmlDisplay component. */
interface HtmlDisplayProps {
  html: string | null;
  emptyMessage: string;
}

/**
 * Renders sanitized HTML content in display (read-only) mode.
 * Uses DOMPurify.sanitize before dangerouslySetInnerHTML to prevent XSS.
 * Applies prose-like Tailwind Typography styling for rich text readability.
 */
function HtmlDisplay({ html, emptyMessage }: HtmlDisplayProps): React.JSX.Element {
  const sanitized = useMemo(() => sanitizeHtml(html), [html]);

  if (!sanitized) {
    return (
      <span className="text-sm text-gray-400 italic">
        {emptyMessage}
      </span>
    );
  }

  return (
    <div
      className={[
        'text-sm text-gray-900',
        /* Prose-like typography styling via Tailwind utility classes */
        '[&_h1]:text-2xl [&_h1]:font-bold [&_h1]:mb-2',
        '[&_h2]:text-xl [&_h2]:font-bold [&_h2]:mb-2',
        '[&_h3]:text-lg [&_h3]:font-semibold [&_h3]:mb-1',
        '[&_h4]:text-base [&_h4]:font-semibold [&_h4]:mb-1',
        '[&_h5]:text-sm [&_h5]:font-semibold',
        '[&_h6]:text-xs [&_h6]:font-semibold',
        '[&_p]:mb-2 [&_p]:leading-relaxed',
        '[&_a]:text-blue-600 [&_a]:underline [&_a]:hover:text-blue-800',
        '[&_ul]:list-disc [&_ul]:ps-5 [&_ul]:mb-2',
        '[&_ol]:list-decimal [&_ol]:ps-5 [&_ol]:mb-2',
        '[&_li]:mb-0.5',
        '[&_blockquote]:border-s-4 [&_blockquote]:border-gray-300 [&_blockquote]:ps-4 [&_blockquote]:italic [&_blockquote]:text-gray-600 [&_blockquote]:mb-2',
        '[&_pre]:bg-gray-100 [&_pre]:rounded [&_pre]:p-3 [&_pre]:text-sm [&_pre]:overflow-x-auto [&_pre]:mb-2',
        '[&_code]:bg-gray-100 [&_code]:rounded [&_code]:px-1 [&_code]:text-sm [&_code]:font-mono',
        '[&_table]:w-full [&_table]:border-collapse [&_table]:mb-2',
        '[&_th]:border [&_th]:border-gray-300 [&_th]:bg-gray-50 [&_th]:px-3 [&_th]:py-1.5 [&_th]:text-start [&_th]:text-sm [&_th]:font-medium',
        '[&_td]:border [&_td]:border-gray-300 [&_td]:px-3 [&_td]:py-1.5 [&_td]:text-sm',
        '[&_img]:max-w-full [&_img]:h-auto [&_img]:rounded',
        '[&_hr]:border-gray-200 [&_hr]:my-4',
      ].join(' ')}
      dangerouslySetInnerHTML={{ __html: sanitized }}
    />
  );
}

// ---------------------------------------------------------------------------
// Edit Mode Component
// ---------------------------------------------------------------------------

/** Props for the internal HtmlEditor component. */
interface HtmlEditorProps {
  value: string | null;
  onChange?: (value: string) => void;
  toolbarMode: HtmlToolbarMode;
  uploadMode: HtmlUploadMode;
  disabled: boolean;
  placeholder?: string;
  controlId: string;
  name: string;
  error?: string;
  required: boolean;
}

/**
 * Renders the contentEditable rich text editor with the appropriate toolbar.
 * Extracts innerHTML on blur and input events and passes sanitized HTML
 * back via onChange.
 */
function HtmlEditor({
  value,
  onChange,
  toolbarMode,
  uploadMode,
  disabled,
  placeholder,
  controlId,
  name,
  error,
  required,
}: HtmlEditorProps): React.JSX.Element {
  const editorRef = useRef<HTMLDivElement | null>(null);
  const [isEmpty, setIsEmpty] = useState<boolean>(!value);
  const buttons = useMemo(() => getToolbarButtons(toolbarMode), [toolbarMode]);

  /**
   * Emit the current editor content via onChange.
   * Sanitizes output HTML to prevent stored XSS.
   */
  const emitChange = useCallback(() => {
    if (!editorRef.current || !onChange) return;
    const rawHtml = editorRef.current.innerHTML;
    /* Treat empty or whitespace-only content as empty string. */
    const trimmed = rawHtml.replace(/<br\s*\/?>/gi, '').replace(/&nbsp;/gi, '').trim();
    const output = trimmed === '' ? '' : sanitizeHtml(rawHtml);
    setIsEmpty(trimmed === '');
    onChange(output);
  }, [onChange]);

  /** Handle input events from the contentEditable div. */
  const handleInput = useCallback(() => {
    emitChange();
  }, [emitChange]);

  /** Handle blur to ensure final value is captured. */
  const handleBlur = useCallback(() => {
    emitChange();
  }, [emitChange]);

  /**
   * Handle paste events — strip external formatting by pasting plain HTML
   * through DOMPurify sanitization.
   */
  const handlePaste = useCallback(
    (event: React.ClipboardEvent<HTMLDivElement>) => {
      event.preventDefault();
      const clipboardHtml = event.clipboardData.getData('text/html');
      const clipboardText = event.clipboardData.getData('text/plain');
      const content = clipboardHtml ? sanitizeHtml(clipboardHtml) : clipboardText;
      document.execCommand('insertHTML', false, content);
    },
    []
  );

  /**
   * Prevent default Enter from creating divs — use <br> or <p> depending
   * on context. This is handled by the browser's contentEditable default
   * behavior which is acceptable for our use case.
   */

  /** Initial HTML content — set once on mount via dangerouslySetInnerHTML. */
  const initialHtml = useMemo(() => sanitizeHtml(value), [value]);

  const hasError = Boolean(error);

  return (
    <div
      className={[
        'rounded-md border bg-white',
        hasError
          ? 'border-red-500 focus-within:ring-1 focus-within:ring-red-500'
          : 'border-gray-300 focus-within:border-blue-500 focus-within:ring-1 focus-within:ring-blue-500',
        disabled ? 'opacity-60 cursor-not-allowed' : '',
      ].join(' ')}
    >
      <Toolbar
        buttons={buttons}
        editorRef={editorRef}
        uploadMode={uploadMode}
        disabled={disabled}
      />

      <div className="relative">
        {/* Placeholder overlay — visible only when editor is empty */}
        {isEmpty && placeholder && !disabled && (
          <div
            className="pointer-events-none absolute inset-0 px-3 py-2 text-sm text-gray-400"
            aria-hidden="true"
          >
            {placeholder}
          </div>
        )}

        <div
          ref={editorRef}
          id={controlId}
          role="textbox"
          aria-multiline="true"
          aria-label={name}
          aria-required={required}
          aria-invalid={hasError}
          aria-describedby={hasError ? `${name}-error` : undefined}
          contentEditable={!disabled}
          suppressContentEditableWarning
          onInput={handleInput}
          onBlur={handleBlur}
          onPaste={handlePaste}
          dangerouslySetInnerHTML={{ __html: initialHtml }}
          className={[
            'min-h-[8rem] px-3 py-2 text-sm text-gray-900 outline-none overflow-auto',
            /* Prose-like typography for editing area */
            '[&_h1]:text-2xl [&_h1]:font-bold [&_h1]:mb-2',
            '[&_h2]:text-xl [&_h2]:font-bold [&_h2]:mb-2',
            '[&_h3]:text-lg [&_h3]:font-semibold [&_h3]:mb-1',
            '[&_p]:mb-1 [&_p]:leading-relaxed',
            '[&_a]:text-blue-600 [&_a]:underline',
            '[&_ul]:list-disc [&_ul]:ps-5',
            '[&_ol]:list-decimal [&_ol]:ps-5',
            '[&_blockquote]:border-s-4 [&_blockquote]:border-gray-300 [&_blockquote]:ps-4 [&_blockquote]:italic [&_blockquote]:text-gray-600',
            '[&_pre]:bg-gray-100 [&_pre]:rounded [&_pre]:p-2 [&_pre]:text-sm [&_pre]:overflow-x-auto',
            '[&_code]:bg-gray-100 [&_code]:rounded [&_code]:px-1 [&_code]:text-sm [&_code]:font-mono',
            '[&_table]:w-full [&_table]:border-collapse',
            '[&_td]:border [&_td]:border-gray-300 [&_td]:px-2 [&_td]:py-1',
            '[&_th]:border [&_th]:border-gray-300 [&_th]:bg-gray-50 [&_th]:px-2 [&_th]:py-1 [&_th]:font-medium',
            '[&_img]:max-w-full [&_img]:h-auto [&_img]:rounded',
            disabled ? 'bg-gray-100 text-gray-500' : '',
          ].join(' ')}
        />
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// HtmlField — Main Exported Component
// ---------------------------------------------------------------------------

/**
 * Rich text / HTML editor field component.
 *
 * Replaces the monolith's PcFieldHtml ViewComponent. Supports:
 *   - Display mode: renders sanitized HTML via dangerouslySetInnerHTML
 *   - Edit mode: contentEditable div with configurable toolbar
 *   - Three toolbar modes: basic (4 buttons), standard (+5), full (+8)
 *   - Three upload modes: none, base64, url
 *   - XSS prevention via DOMPurify sanitization
 *
 * Accepts `BaseFieldProps` for FieldRenderer compatibility (dynamic dispatch).
 * When used directly, pass `HtmlFieldProps` for full type safety on
 * `value`, `onChange`, `uploadMode`, and `toolbarMode`.
 *
 * @param props — BaseFieldProps (via FieldRenderer) or HtmlFieldProps (direct)
 * @returns The rendered HTML field component
 */
function HtmlField(props: BaseFieldProps): React.JSX.Element {
  const {
    name,
    label,
    labelMode = 'stacked',
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
  } = props;

  /*
   * Extract HTML-specific props.
   * When used via FieldRenderer, `value` is `unknown` — coerce to string | null.
   * When used directly with HtmlFieldProps, `value` is already string | null.
   * Extra props (uploadMode, toolbarMode) default when not supplied by FieldRenderer.
   */
  const value: string | null =
    typeof props.value === 'string' ? props.value : props.value != null ? String(props.value) : null;
  const onChange = props.onChange as ((value: string) => void) | undefined;
  const htmlExtras = props as unknown as Partial<HtmlFieldProps>;
  const uploadMode: HtmlUploadMode = htmlExtras.uploadMode ?? 'none';
  const toolbarMode: HtmlToolbarMode = htmlExtras.toolbarMode ?? 'basic';

  /* Phase 1: Visibility check */
  if (!isVisible) {
    return <></>;
  }

  /* Phase 2: Access control — forbidden renders lock message */
  if (access === 'forbidden') {
    return (
      <div className={className}>
        <div
          className="flex items-center gap-2 rounded border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-400"
          role="status"
          aria-label="Access denied"
        >
          <svg
            className="h-4 w-4 shrink-0"
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
      </div>
    );
  }

  /* Compute effective disabled and mode from access level */
  const effectiveDisabled = disabled || access === 'readonly';
  const effectiveMode = access === 'readonly' ? 'display' : mode;
  const controlId = props.fieldId ?? `field-${name}`;

  /* Render the field content based on effective mode */
  const fieldContent =
    effectiveMode === 'display' ? (
      <HtmlDisplay html={value} emptyMessage={emptyValueMessage} />
    ) : (
      <HtmlEditor
        value={value}
        onChange={onChange}
        toolbarMode={toolbarMode}
        uploadMode={uploadMode}
        disabled={effectiveDisabled}
        placeholder={placeholder}
        controlId={controlId}
        name={name}
        error={error}
        required={required}
      />
    );

  /* Error message section */
  const errorSection = error ? (
    <p className="text-sm text-red-600 mt-1" id={`${name}-error`} role="alert">
      {error}
    </p>
  ) : null;

  /* Description section */
  const descriptionSection = description ? (
    <p className="text-sm text-gray-500 mt-1">{description}</p>
  ) : null;

  /* Build the label element */
  const labelElement = label && labelMode !== 'hidden' ? (
    <label
      htmlFor={controlId}
      className="text-sm font-medium text-gray-700 flex items-center gap-1"
    >
      <span>{label}</span>
      {required && (
        <span className="text-red-500" aria-hidden="true">
          *
        </span>
      )}
    </label>
  ) : null;

  /* Layout based on labelMode */
  if (labelMode === 'hidden' || !label) {
    return (
      <div className={className}>
        {fieldContent}
        {errorSection}
        {descriptionSection}
      </div>
    );
  }

  if (labelMode === 'horizontal') {
    return (
      <div className={`grid grid-cols-12 gap-2 items-start ${className ?? ''}`}>
        <div className="col-span-3">
          {labelElement}
        </div>
        <div className="col-span-9">
          {fieldContent}
          {errorSection}
          {descriptionSection}
        </div>
      </div>
    );
  }

  if (labelMode === 'inline') {
    return (
      <div className={`flex items-start gap-2 ${className ?? ''}`}>
        {labelElement}
        <div className="flex-1 min-w-0">
          {fieldContent}
          {errorSection}
          {descriptionSection}
        </div>
      </div>
    );
  }

  /* Default: stacked layout — label above field */
  return (
    <div className={`flex flex-col gap-1 ${className ?? ''}`}>
      {labelElement}
      {fieldContent}
      {errorSection}
      {descriptionSection}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------

export default HtmlField;
