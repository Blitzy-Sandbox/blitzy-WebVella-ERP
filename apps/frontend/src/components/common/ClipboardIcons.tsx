/**
 * Shared clipboard action SVG icons.
 *
 * These lightweight icon components are used across multiple field components
 * (ColorField, GuidField, and any future copy-to-clipboard UIs) to provide
 * consistent visual feedback for clipboard copy operations.
 *
 * Extracted from duplicate definitions in ColorField.tsx and GuidField.tsx
 * (Directive 5 consolidation) to eliminate near-identical SVG component code.
 *
 * @module components/common/ClipboardIcons
 */

import React from 'react';

/**
 * Clipboard/copy SVG icon — renders a small clipboard outline glyph.
 *
 * Heroicons mini "clipboard-document" variant (20×20 viewBox).
 * Sized at 16×16 (`h-4 w-4`) via Tailwind and inherits the parent text
 * colour through `fill="currentColor"`.
 */
export function ClipboardIcon(): React.JSX.Element {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 20 20"
      fill="currentColor"
      aria-hidden="true"
      className="h-4 w-4"
    >
      <path d="M8 2a1 1 0 0 0 0 2h2a1 1 0 1 0 0-2H8Z" />
      <path
        d="M3 5a2 2 0 0 1 2-2 3 3 0 0 0 3 3h2a3 3 0 0 0 3-3 2 2 0 0 1 2 2v6h-4.586l1.293-1.293a1
           1 0 0 0-1.414-1.414l-3 3a1 1 0 0 0 0 1.414l3 3a1 1 0 0 0 1.414-1.414L10.414 13H15v3a2 2
           0 0 1-2 2H5a2 2 0 0 1-2-2V5Z"
      />
    </svg>
  );
}

/**
 * Checkmark SVG icon — shown after a successful clipboard copy action.
 *
 * Heroicons mini "check" variant (20×20 viewBox).
 * Sized at 16×16 (`h-4 w-4`) and uses `currentColor` fill for theming.
 */
export function CheckIcon(): React.JSX.Element {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 20 20"
      fill="currentColor"
      aria-hidden="true"
      className="h-4 w-4"
    >
      <path
        fillRule="evenodd"
        d="M16.704 4.153a.75.75 0 0 1 .143 1.052l-8 10.5a.75.75 0 0 1-1.127.075l-4.5-4.5a.75.75
           0 0 1 1.06-1.06l3.894 3.893 7.48-9.817a.75.75 0 0 1 1.05-.143Z"
        clipRule="evenodd"
      />
    </svg>
  );
}
