/**
 * DataCsvField — CSV data editor field component.
 *
 * React replacement for the monolith's PcFieldDataCsv ViewComponent.
 * Provides inline CSV data input (edit mode) with live preview table,
 * and read-only table display (display mode). Supports comma and tab
 * delimiters, optional header rows/columns, and RFC 4180 quoted-field parsing.
 *
 * @module DataCsvField
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Exported Types
// ---------------------------------------------------------------------------

/**
 * Delimiter type for CSV data — supports comma and tab separators.
 * Maps to the monolith's WvCsvDelimiterType enum.
 */
export type CsvDelimiterType = 'comma' | 'tab';

/**
 * Props for the DataCsvField component.
 * Extends base field props (minus value/onChange) with CSV-specific configuration.
 */
export interface DataCsvFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Raw CSV string value */
  value: string | null;
  /** Change handler receiving the updated CSV string */
  onChange?: (value: string) => void;
  /** CSS height for the textarea editor (e.g. '200px', '15rem') */
  height?: string;
  /** Delimiter character type — defaults to 'comma' */
  delimiter?: CsvDelimiterType;
  /** Whether the first row is a header row — defaults to true */
  hasHeader?: boolean;
  /** Whether the first column is a header column — defaults to false */
  hasHeaderColumn?: boolean;
  /** Language code for display — defaults to 'en' */
  lang?: string;
}

// ---------------------------------------------------------------------------
// Internal Helpers
// ---------------------------------------------------------------------------

/**
 * Resolves the actual delimiter character from a CsvDelimiterType value.
 */
function getDelimiterChar(type: CsvDelimiterType): string {
  return type === 'tab' ? '\t' : ',';
}

/**
 * Parses a single CSV row, respecting RFC 4180 quoted-field rules.
 * Handles delimiters within quotes and escaped quotes (double-quote "").
 */
function parseCsvRow(row: string, delimChar: string): string[] {
  const fields: string[] = [];
  let current = '';
  let inQuotes = false;
  let i = 0;

  while (i < row.length) {
    const ch = row[i];

    if (inQuotes) {
      if (ch === '"') {
        // Escaped double-quote → literal "
        if (i + 1 < row.length && row[i + 1] === '"') {
          current += '"';
          i += 2;
          continue;
        }
        // Closing quote
        inQuotes = false;
        i++;
        continue;
      }
      current += ch;
      i++;
    } else {
      if (ch === '"') {
        inQuotes = true;
        i++;
        continue;
      }
      if (ch === delimChar) {
        fields.push(current.trim());
        current = '';
        i++;
        continue;
      }
      current += ch;
      i++;
    }
  }

  // Push the trailing field
  fields.push(current.trim());
  return fields;
}

/**
 * Parses a full CSV string into a 2-D string array.
 * Normalises line endings (CRLF / CR → LF) and skips trailing blank lines.
 */
function parseCsv(csv: string, delimChar: string): string[][] {
  if (!csv || csv.trim().length === 0) {
    return [];
  }

  const normalised = csv.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  const lines = normalised.split('\n');
  const rows: string[][] = [];

  for (const line of lines) {
    if (line.length === 0) {
      continue;
    }
    rows.push(parseCsvRow(line, delimChar));
  }

  return rows;
}

// ---------------------------------------------------------------------------
// CsvTable – internal presentation component
// ---------------------------------------------------------------------------

interface CsvTableProps {
  data: string[][];
  hasHeader: boolean;
  hasHeaderColumn: boolean;
  emptyMessage: string;
}

/**
 * Renders a parsed CSV 2-D array as an accessible HTML <table>.
 */
function CsvTable({
  data,
  hasHeader,
  hasHeaderColumn,
  emptyMessage,
}: CsvTableProps): React.JSX.Element {
  if (data.length === 0) {
    return (
      <span className="text-sm italic text-gray-500">{emptyMessage}</span>
    );
  }

  const headerRow = hasHeader ? data[0] : null;
  const bodyRows = hasHeader ? data.slice(1) : data;
  const maxCols = data.reduce((max, row) => Math.max(max, row.length), 0);

  return (
    <div className="overflow-x-auto rounded-md border border-gray-200">
      <table
        className="min-w-full divide-y divide-gray-200 text-sm"
        role="table"
      >
        {headerRow && (
          <thead className="bg-gray-50">
            <tr>
              {Array.from({ length: maxCols }, (_, colIdx) => (
                <th
                  key={`h-${colIdx}`}
                  scope="col"
                  className="px-3 py-2 text-start text-xs font-semibold uppercase tracking-wider text-gray-600"
                >
                  {headerRow[colIdx] ?? ''}
                </th>
              ))}
            </tr>
          </thead>
        )}
        <tbody className="divide-y divide-gray-100 bg-white">
          {bodyRows.length === 0 ? (
            <tr>
              <td
                colSpan={maxCols}
                className="px-3 py-4 text-center text-sm italic text-gray-400"
              >
                {emptyMessage}
              </td>
            </tr>
          ) : (
            bodyRows.map((row, rowIdx) => (
              <tr
                key={`r-${rowIdx}`}
                className={rowIdx % 2 === 0 ? 'bg-white' : 'bg-gray-50'}
              >
                {Array.from({ length: maxCols }, (_, colIdx) => {
                  const isHeaderCol = hasHeaderColumn && colIdx === 0;
                  const cellValue = row[colIdx] ?? '';

                  if (isHeaderCol) {
                    return (
                      <th
                        key={`c-${rowIdx}-${colIdx}`}
                        scope="row"
                        className="whitespace-nowrap px-3 py-2 text-start text-sm font-medium text-gray-900"
                      >
                        {cellValue}
                      </th>
                    );
                  }

                  return (
                    <td
                      key={`c-${rowIdx}-${colIdx}`}
                      className="whitespace-nowrap px-3 py-2 text-sm text-gray-700"
                    >
                      {cellValue}
                    </td>
                  );
                })}
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}

// ---------------------------------------------------------------------------
// DataCsvField – main exported component
// ---------------------------------------------------------------------------

/**
 * DataCsvField component.
 *
 * **Edit / form / inline-edit modes** — monospace textarea for raw CSV input
 * with a delimiter selector, header-row toggle, and a live preview table.
 *
 * **Display mode** — read-only table rendered from the CSV value.
 */
function DataCsvField(props: DataCsvFieldProps): React.JSX.Element {
  const {
    value,
    onChange,
    height = '200px',
    delimiter = 'comma',
    hasHeader = true,
    hasHeaderColumn = false,
    lang = 'en',
    // BaseFieldProps members accessed per schema requirements
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
  } = props;

  // -- local state ----------------------------------------------------------

  const [textareaValue, setTextareaValue] = useState<string>(value ?? '');
  const [activeDelimiter, setActiveDelimiter] = useState<CsvDelimiterType>(delimiter);
  const [headerEnabled, setHeaderEnabled] = useState<boolean>(hasHeader);

  // -- derived values -------------------------------------------------------

  // Effective locale combines the explicit locale prop with the lang fallback
  const effectiveLocale = locale ?? lang;

  // When labelMode is 'hidden' there is no visible label rendered by FieldRenderer
  // so the field itself must provide an accessible name via aria-label.
  const needsAriaLabel = labelMode === 'hidden' || !label;
  const ariaLabel = needsAriaLabel ? (label ?? name) : undefined;

  const isEditMode = mode === 'edit';
  const isReadOnly = access === 'readonly' || disabled;

  // Resolve delimiter character — in edit modes honour toolbar; in display use prop
  const delimiterChar = useMemo(
    () => getDelimiterChar(isEditMode ? activeDelimiter : delimiter),
    [isEditMode, activeDelimiter, delimiter],
  );

  // Parse CSV for display or live preview
  const parsedData = useMemo(() => {
    const raw = isEditMode ? textareaValue : (value ?? '');
    return parseCsv(raw, delimiterChar);
  }, [isEditMode, textareaValue, value, delimiterChar]);

  // -- callbacks ------------------------------------------------------------

  const handleTextareaChange = useCallback(
    (event: React.ChangeEvent<HTMLTextAreaElement>) => {
      const next = event.target.value;
      setTextareaValue(next);
      onChange?.(next);
    },
    [onChange],
  );

  const handleDelimiterChange = useCallback(
    (next: CsvDelimiterType) => {
      setActiveDelimiter(next);
    },
    [],
  );

  const handleHeaderToggle = useCallback(() => {
    setHeaderEnabled((prev) => !prev);
  }, []);

  // -- sync external value into internal textarea ---------------------------

  const prevValueRef = React.useRef(value);
  if (value !== prevValueRef.current) {
    prevValueRef.current = value;
    if (value !== textareaValue) {
      setTextareaValue(value ?? '');
    }
  }

  // -- render gates ---------------------------------------------------------

  if (!isVisible) {
    return <></>;
  }

  if (access === 'forbidden') {
    return (
      <div
        className={`text-sm italic text-gray-500 ${className ?? ''}`}
        role="alert"
        aria-live="polite"
        data-field-name={name}
        lang={effectiveLocale}
      >
        {accessDeniedMessage}
      </div>
    );
  }

  // -- common identifiers ---------------------------------------------------

  const fieldId = props.fieldId ?? `field-${name}`;
  const errorId = error ? `${name}-error` : undefined;
  const descriptionId = description ? `${name}-description` : undefined;

  // -- display mode ---------------------------------------------------------

  if (mode === 'display') {
    const hasValue =
      value !== null && value !== undefined && value.trim().length > 0;

    if (!hasValue) {
      return (
        <div
          className={`text-sm ${className ?? ''}`}
          data-field-name={name}
          lang={effectiveLocale}
        >
          <span className="italic text-gray-500">{emptyValueMessage}</span>
        </div>
      );
    }

    return (
      <div
        className={className ?? ''}
        data-field-name={name}
        lang={effectiveLocale}
        aria-label={ariaLabel}
      >
        <CsvTable
          data={parsedData}
          hasHeader={hasHeader}
          hasHeaderColumn={hasHeaderColumn}
          emptyMessage={emptyValueMessage}
        />
      </div>
    );
  }

  // -- edit / form / inline-edit mode ---------------------------------------

  return (
    <div
      className={`flex flex-col gap-3 ${className ?? ''}`}
      data-field-name={name}
      lang={effectiveLocale}
    >
      {/* Editor toolbar */}
      <div
        className="flex flex-wrap items-center gap-3 text-sm"
        role="toolbar"
        aria-label="CSV editor controls"
      >
        {/* Delimiter selector */}
        <fieldset className="flex items-center gap-2">
          <legend className="sr-only">Delimiter</legend>
          <span className="text-xs font-medium text-gray-600">Delimiter:</span>
          <button
            type="button"
            onClick={() => handleDelimiterChange('comma')}
            disabled={isReadOnly}
            className={`rounded-md px-2.5 py-1 text-xs font-medium transition-colors ${
              activeDelimiter === 'comma'
                ? 'bg-blue-600 text-white'
                : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
            } disabled:cursor-not-allowed disabled:opacity-50`}
            aria-pressed={activeDelimiter === 'comma'}
          >
            Comma
          </button>
          <button
            type="button"
            onClick={() => handleDelimiterChange('tab')}
            disabled={isReadOnly}
            className={`rounded-md px-2.5 py-1 text-xs font-medium transition-colors ${
              activeDelimiter === 'tab'
                ? 'bg-blue-600 text-white'
                : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
            } disabled:cursor-not-allowed disabled:opacity-50`}
            aria-pressed={activeDelimiter === 'tab'}
          >
            Tab
          </button>
        </fieldset>

        {/* Header row toggle */}
        <label className="flex cursor-pointer items-center gap-1.5 text-xs text-gray-600">
          <input
            type="checkbox"
            checked={headerEnabled}
            onChange={handleHeaderToggle}
            disabled={isReadOnly}
            className="size-3.5 rounded border-gray-300 text-blue-600 focus:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
          />
          <span className="font-medium">Header row</span>
        </label>
      </div>

      {/* CSV textarea editor */}
      <textarea
        id={fieldId}
        name={name}
        value={textareaValue}
        onChange={handleTextareaChange}
        placeholder={placeholder ?? 'Enter CSV data…'}
        disabled={isReadOnly}
        required={required}
        style={{ height }}
        className={`block w-full resize-y rounded-md border font-mono text-sm leading-relaxed ${
          error
            ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
            : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
        } px-3 py-2 shadow-sm focus:outline-none focus:ring-1 disabled:cursor-not-allowed disabled:bg-gray-100 disabled:text-gray-500`}
        aria-invalid={Boolean(error)}
        aria-describedby={
          [errorId, descriptionId].filter(Boolean).join(' ') || undefined
        }
        aria-required={required}
        aria-label={ariaLabel}
      />

      {/* Description text */}
      {description && (
        <p id={descriptionId} className="text-xs text-gray-500">
          {description}
        </p>
      )}

      {/* Error message */}
      {error && (
        <p id={errorId} className="text-xs text-red-600" role="alert">
          {error}
        </p>
      )}

      {/* Live CSV preview */}
      {textareaValue.trim().length > 0 && (
        <div className="flex flex-col gap-1.5">
          <span className="text-xs font-medium text-gray-500">Preview</span>
          <CsvTable
            data={parsedData}
            hasHeader={headerEnabled}
            hasHeaderColumn={hasHeaderColumn}
            emptyMessage={emptyValueMessage}
          />
        </div>
      )}
    </div>
  );
}

export default DataCsvField;
