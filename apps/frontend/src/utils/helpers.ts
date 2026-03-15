/**
 * General Helper Utilities — WebVella ERP Frontend
 *
 * Production-ready utility functions replacing monolith equivalents:
 *   - GUID generation / validation (GuidUtility, SecurityContext)
 *   - HTML text extraction / truncation (RenderService.GetSnippetFromHtml)
 *   - Flat list → tree conversion (RenderService.ConvertListToTree)
 *   - URL path parsing (PageService.GetInfoFromPath)
 *   - Object traversal, cloning, debouncing, CSS class merging, grouping, slugification
 *
 * Sources:
 *   - WebVella.Erp/Utilities/Helpers.cs
 *   - WebVella.Erp.Web/Services/RenderService.cs   (lines 359-485)
 *   - WebVella.Erp.Web/Services/PageService.cs      (lines 1511-1723)
 *   - WebVella.Erp.Web/Models/MenuItem.cs
 *   - WebVella.Erp.Web/Models/UrlInfo.cs
 *   - WebVella.Erp.Web/Models/PageType.cs
 *
 * Rules:
 *   - No server-side dependencies — all functions run in browser
 *   - Pure functions where possible — no side effects except debounce
 *   - TypeScript strict mode — all parameters and return types fully typed
 *   - No jQuery — zero DOM manipulation libraries
 *   - Named exports only — no default export
 */

import { EMPTY_GUID } from './constants';

// =============================================================================
// Internal Constants
// =============================================================================

/** Standard GUID/UUID pattern: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx */
const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// =============================================================================
// Phase 1: GUID Generation & Validation
// =============================================================================

/**
 * Generates a UUID v4 string.
 *
 * Uses the native `crypto.randomUUID()` API when available (all modern
 * browsers and Node.js ≥ 19). Falls back to a Math.random-based generator
 * for older environments.
 *
 * @returns A lowercase UUID v4 string in the format
 *          `xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx`.
 *
 * @example
 * ```ts
 * const id = generateGuid();
 * // "3b12f1df-5232-4fa4-8f6a-11320f9dcd5b"
 * ```
 */
export function generateGuid(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  // Fallback: Math.random-based UUID v4 generation
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

/**
 * Checks whether a GUID string represents an empty / missing value.
 *
 * Returns `true` when the input is `null`, `undefined`, an empty string,
 * or the well-known empty GUID `'00000000-0000-0000-0000-000000000000'`.
 * Mirrors the `Guid.Empty` comparison pattern used throughout the monolith's
 * `SecurityContext` and `RecordManager`.
 *
 * @param guid - The GUID string to test.
 * @returns `true` if the GUID is considered empty.
 *
 * @example
 * ```ts
 * isEmptyGuid(null);                                          // true
 * isEmptyGuid('00000000-0000-0000-0000-000000000000');        // true
 * isEmptyGuid('3b12f1df-5232-4fa4-8f6a-11320f9dcd5b');       // false
 * ```
 */
export function isEmptyGuid(guid: string | null | undefined): boolean {
  if (guid == null || guid === '') {
    return true;
  }
  return guid === EMPTY_GUID;
}

// =============================================================================
// Phase 2: Text Truncation & Sanitization
// =============================================================================

/**
 * Extracts a plain-text snippet from an HTML string.
 *
 * Replicates `RenderService.GetSnippetFromHtml` (lines 359-386):
 * 1. Parses the HTML into a DOM tree (via `DOMParser` in the browser,
 *    regex fallback elsewhere).
 * 2. Walks all leaf nodes and collects their trimmed text.
 * 3. Joins collected fragments with newlines.
 * 4. If the result exceeds `maxLength`, truncates and appends `'...'`.
 *
 * @param html      - The HTML string to extract text from.
 * @param maxLength - Maximum character count before truncation (default 150).
 * @returns The extracted plain-text snippet.
 */
export function extractSnippetText(html: string, maxLength: number = 150): string {
  if (!html || !html.trim()) {
    return '';
  }

  let textContent = '';

  if (typeof DOMParser !== 'undefined') {
    try {
      const parser = new DOMParser();
      const doc = parser.parseFromString(html, 'text/html');
      const parts: string[] = [];

      /**
       * Recursively collect trimmed text from leaf nodes — mirrors the C#
       * `root.DescendantsAndSelf()` traversal that checks `!node.HasChildNodes`.
       */
      const collectLeafText = (node: Node): void => {
        if (node.childNodes.length === 0) {
          const text = (node.textContent || '').trim();
          if (text) {
            parts.push(text);
          }
        } else {
          for (let i = 0; i < node.childNodes.length; i++) {
            collectLeafText(node.childNodes[i]);
          }
        }
      };

      collectLeafText(doc.body);
      textContent = parts.join('\n');
    } catch {
      // DOMParser failed — fall through to regex path
      textContent = stripTagsWithRegex(html);
    }
  } else {
    textContent = stripTagsWithRegex(html);
  }

  if (textContent.length > maxLength) {
    return textContent.substring(0, maxLength) + '...';
  }

  return textContent;
}

/**
 * Strips HTML tags using regex and normalises whitespace.
 * Used as a fallback when `DOMParser` is not available.
 */
function stripTagsWithRegex(html: string): string {
  return html
    .replace(/<[^>]*>/g, '\n')
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean)
    .join('\n');
}

/**
 * Truncates text to a maximum length, preferring a word-boundary break.
 *
 * If the text fits within `maxLength` it is returned unchanged. Otherwise
 * the function looks for a space within the last 30 % of the allowed length
 * and breaks there; if no suitable space is found it hard-truncates.
 *
 * @param text      - The input text.
 * @param maxLength - Maximum character count.
 * @param suffix    - Suffix appended when truncation occurs (default `'...'`).
 * @returns The (possibly truncated) text string.
 */
export function truncateText(text: string, maxLength: number, suffix: string = '...'): string {
  if (!text) {
    return '';
  }
  if (text.length <= maxLength) {
    return text;
  }

  const truncated = text.substring(0, maxLength);
  const lastSpace = truncated.lastIndexOf(' ');

  // Break at word boundary if the space falls within the last 30 % of the window
  if (lastSpace > maxLength * 0.7) {
    return truncated.substring(0, lastSpace) + suffix;
  }

  return truncated + suffix;
}

/**
 * Performs basic HTML sanitization by removing dangerous elements and
 * attributes. Strips `<script>`, `<style>`, `<iframe>`, `<object>`,
 * `<embed>`, and `<form>` elements as well as any `on*` event-handler
 * attributes and `javascript:` URIs.
 *
 * **Note:** For production-critical XSS prevention, use a dedicated
 * library such as DOMPurify. This function covers the most common
 * attack vectors for display-safe HTML content.
 *
 * @param html - The raw HTML string to sanitize.
 * @returns The sanitized HTML string.
 */
export function sanitizeHtml(html: string): string {
  if (!html) {
    return '';
  }

  if (typeof DOMParser !== 'undefined') {
    try {
      const parser = new DOMParser();
      const doc = parser.parseFromString(html, 'text/html');

      // Remove dangerous element types
      const dangerous = doc.querySelectorAll(
        'script, style, iframe, object, embed, form'
      );
      dangerous.forEach((el) => el.remove());

      // Remove event-handler attributes and javascript: URIs
      const allElements = doc.body.querySelectorAll('*');
      allElements.forEach((el) => {
        const attrs = Array.from(el.attributes);
        for (const attr of attrs) {
          if (
            attr.name.toLowerCase().startsWith('on') ||
            attr.value.toLowerCase().trim().startsWith('javascript:')
          ) {
            el.removeAttribute(attr.name);
          }
        }
      });

      return doc.body.innerHTML;
    } catch {
      return sanitizeHtmlWithRegex(html);
    }
  }

  return sanitizeHtmlWithRegex(html);
}

/**
 * Regex-based HTML sanitization fallback for environments without DOMParser.
 */
function sanitizeHtmlWithRegex(html: string): string {
  return html
    .replace(/<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>/gi, '')
    .replace(/<style\b[^<]*(?:(?!<\/style>)<[^<]*)*<\/style>/gi, '')
    .replace(/<iframe\b[^<]*(?:(?!<\/iframe>)<[^<]*)*<\/iframe>/gi, '')
    .replace(/<object\b[^<]*(?:(?!<\/object>)<[^<]*)*<\/object>/gi, '')
    .replace(/<embed\b[^>]*\/?>/gi, '')
    .replace(/<form\b[^<]*(?:(?!<\/form>)<[^<]*)*<\/form>/gi, '')
    .replace(/\s+on\w+\s*=\s*"[^"]*"/gi, '')
    .replace(/\s+on\w+\s*=\s*'[^']*'/gi, '')
    .replace(/href\s*=\s*"javascript:[^"]*"/gi, 'href="#"')
    .replace(/href\s*=\s*'javascript:[^']*'/gi, "href='#'");
}

// =============================================================================
// Phase 3: List-to-Tree Conversion
// =============================================================================

/**
 * Generic tree node interface.
 *
 * Consumers may extend this with additional properties via the index
 * signature `[key: string]: unknown`.
 */
export interface TreeNode {
  /** Unique node identifier. */
  id: string;
  /** Parent node identifier, or `null` for root nodes. */
  parentId: string | null;
  /** Direct child nodes. */
  children: TreeNode[];
  /** Allow additional properties. */
  [key: string]: unknown;
}

/**
 * Converts a flat array of items with `id` / `parentId` relationships into a
 * nested tree structure.
 *
 * Replicates the recursive logic from `RenderService.ConvertListToTree`
 * (lines 454-485) with an efficient O(n) map-based approach that supports
 * unlimited nesting depth.
 *
 * @typeParam T - The item type; must have `id` and `parentId` properties.
 * @param items   - Flat array of items to convert.
 * @param sortKey - Optional property key to sort siblings by at each level.
 * @returns A new array of root-level tree nodes with nested `children`.
 *
 * @example
 * ```ts
 * const flat = [
 *   { id: '1', parentId: null, name: 'Root' },
 *   { id: '2', parentId: '1',  name: 'Child' },
 * ];
 * const tree = listToTree(flat);
 * // [{ id: '1', parentId: null, name: 'Root', children: [{ id: '2', ... }] }]
 * ```
 */
export function listToTree<T extends { id: string; parentId: string | null }>(
  items: T[],
  sortKey?: keyof T
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
): (T & { children: (T & { children: any[] })[] })[] {
  type TreeItem = T & { children: TreeItem[] };

  // First pass: create shallow copies with empty children arrays
  const map = new Map<string, TreeItem>();
  for (const item of items) {
    map.set(item.id, { ...item, children: [] as TreeItem[] });
  }

  // Second pass: wire parent → child relationships
  const roots: TreeItem[] = [];
  for (const item of items) {
    const treeNode = map.get(item.id)!;
    if (item.parentId === null || !map.has(item.parentId)) {
      roots.push(treeNode);
    } else {
      map.get(item.parentId)!.children.push(treeNode);
    }
  }

  // Sort siblings recursively
  if (sortKey !== undefined) {
    const sortRecursive = (nodes: TreeItem[]): void => {
      nodes.sort((a, b) => {
        const va = a[sortKey];
        const vb = b[sortKey];
        if (typeof va === 'number' && typeof vb === 'number') {
          return va - vb;
        }
        return String(va ?? '').localeCompare(String(vb ?? ''));
      });
      for (const node of nodes) {
        sortRecursive(node.children);
      }
    };
    sortRecursive(roots);
  }

  // The cast is safe — the runtime shape is correct; the two-level type
  // annotation is a pragmatic compromise for deep recursive types.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  return roots as any;
}

/**
 * Menu item interface matching the C# `MenuItem` model from `MenuItem.cs`
 * (lines 7-36).
 *
 * Property name mappings:
 *   - `Class` → `className` (reserved word in JS/TS)
 *   - `Nodes` → `nodes`
 *   - `SortOrder` → `sortOrder`
 *   - `isDropdownRight` (kept lowercase to match C# field casing)
 */
export interface MenuItem {
  /** Unique menu item identifier. */
  id: string;
  /** Parent menu item identifier, or `null` for root items. */
  parentId: string | null;
  /** Renderable content — plain text or HTML string. */
  content: string;
  /** CSS class name(s) to attach (e.g. `'active'`). */
  className: string;
  /** Whether `content` should be rendered as raw HTML. */
  isHtml: boolean;
  /** Whether to render the wrapper `<li>` element. */
  renderWrapper: boolean;
  /** Child menu items (populated by tree conversion). */
  nodes: MenuItem[];
  /** Whether dropdown sub-menu opens to the right. */
  isDropdownRight: boolean;
  /** Sort weight — lower values appear first. */
  sortOrder: number;
}

/**
 * Converts a flat list of `MenuItem` objects into a nested tree.
 *
 * Replicates `RenderService.ConvertListToTree` (lines 454-485):
 * 1. Creates deep copies of each item with empty `nodes` arrays.
 * 2. Identifies root items (`parentId === null`).
 * 3. Recursively assigns children, ordered by `sortOrder` ascending.
 *
 * The implementation uses an O(n) map for parent lookup (improving on the
 * monolith's linear `First()` search) while preserving identical output.
 *
 * @param items - Flat array of menu items.
 * @returns Tree-structured array of root-level `MenuItem` nodes.
 */
export function convertMenuListToTree(items: MenuItem[]): MenuItem[] {
  const map = new Map<string, MenuItem>();
  const roots: MenuItem[] = [];

  // Create copies with empty nodes (mirrors C# `new MenuItem { Nodes = new List<MenuItem>() }`)
  for (const item of items) {
    map.set(item.id, {
      id: item.id,
      parentId: item.parentId,
      content: item.content,
      className: item.className,
      isHtml: item.isHtml,
      renderWrapper: item.renderWrapper,
      nodes: [],
      isDropdownRight: item.isDropdownRight,
      sortOrder: item.sortOrder,
    });
  }

  // Build parent → child relationships
  for (const item of items) {
    const copy = map.get(item.id)!;
    if (item.parentId === null || !map.has(item.parentId)) {
      roots.push(copy);
    } else {
      map.get(item.parentId)!.nodes.push(copy);
    }
  }

  // Sort at every level by sortOrder (matches C# `.OrderBy(x => x.SortOrder)`)
  const sortNodes = (nodes: MenuItem[]): void => {
    nodes.sort((a, b) => a.sortOrder - b.sortOrder);
    for (const node of nodes) {
      sortNodes(node.nodes);
    }
  };
  sortNodes(roots);

  return roots;
}

// =============================================================================
// Phase 4: URL Path Parsing
// =============================================================================

/**
 * Page type discriminator matching `PageType.cs` enum labels.
 *
 * | C# Enum Value      | TypeScript Literal |
 * |---------------------|--------------------|
 * | `PageType.Home`     | `'home'`           |
 * | `PageType.Site`     | `'site'`           |
 * | `PageType.Application` | `'application'` |
 * | `PageType.RecordList`  | `'recordList'`  |
 * | `PageType.RecordCreate`| `'recordCreate'`|
 * | `PageType.RecordDetails`| `'recordDetails'`|
 * | `PageType.RecordManage` | `'recordManage'`|
 */
export type PageType =
  | 'home'
  | 'site'
  | 'application'
  | 'recordList'
  | 'recordCreate'
  | 'recordDetails'
  | 'recordManage';

/**
 * Parsed URL information matching the C# `UrlInfo` model from `UrlInfo.cs`.
 *
 * All string properties default to `''`; all nullable ID properties default
 * to `null` — mirroring the C# property initialisers.
 */
export interface UrlInfo {
  /** Resolved page type. Defaults to `'site'` (matching C# default). */
  pageType: PageType;
  /** Whether the path includes a relation segment (`/rl/`). */
  hasRelation: boolean;
  /** Application slug from the URL. */
  appName: string;
  /** Area slug from the URL. */
  areaName: string;
  /** Node slug from the URL. */
  nodeName: string;
  /** Page name slug from the URL. */
  pageName: string;
  /** Record GUID extracted from the path, or `null`. */
  recordId: string | null;
  /** Relation GUID extracted from the path, or `null`. */
  relationId: string | null;
  /** Parent record GUID (relation paths only), or `null`. */
  parentRecordId: string | null;
}

/**
 * Tests whether a string is a valid hyphenated GUID/UUID.
 * Used internally by `parseUrlPath` to replicate `Guid.TryParse`.
 */
function isValidGuid(value: string): boolean {
  return GUID_PATTERN.test(value);
}

/**
 * Creates a default `UrlInfo` matching the C# `new UrlInfo()` initialiser
 * (default `PageType = PageType.Site`).
 */
function createDefaultUrlInfo(): UrlInfo {
  return {
    pageType: 'site',
    hasRelation: false,
    appName: '',
    areaName: '',
    nodeName: '',
    pageName: '',
    recordId: null,
    relationId: null,
    parentRecordId: null,
  };
}

/**
 * Parses a URL path into a structured `UrlInfo` object.
 *
 * Replicates `PageService.GetInfoFromPath` (lines 1511-1723) **exactly**,
 * preserving every branching rule, segment index, and GUID-try-parse
 * fallback from the original C# implementation.
 *
 * ### Supported URL patterns
 *
 * | Pattern | PageType |
 * |---------|----------|
 * | `/` | `home` |
 * | `/{app}/a/{page}` | `application` |
 * | `/s/{page}` | `site` |
 * | `/s/{plugin}/{page}` | `site` |
 * | `/{app}/{area}/{node}/l/{page?}` | `recordList` |
 * | `/{app}/{area}/{node}/c/{page?}` | `recordCreate` |
 * | `/{app}/{area}/{node}/r/{recordId}/{page?}` | `recordDetails` |
 * | `/{app}/{area}/{node}/m/{page?}` | `recordManage` |
 * | `/{app}/{area}/{node}/r/{recordId}/rl/{relId}/{type}/{…}` | relation variants |
 * | `/{app}/{area}/{node}/a/{page?}` | `application` (nested) |
 *
 * @param path - The URL pathname to parse (e.g. `'/myapp/a/dashboard'`).
 * @returns A fully populated `UrlInfo` object.
 */
export function parseUrlPath(path: string): UrlInfo {
  const result = createDefaultUrlInfo();
  const pathNodes = path.split('/');

  // ── Home: / ──────────────────────────────────────────────── line 1516
  if (path === '/') {
    result.pageType = 'home';
    return result;
  }

  // ── Application Home: /{app}/a/{page} ───────────────────── line 1523
  if (pathNodes.length >= 3 && pathNodes[2].toLowerCase() === 'a') {
    result.pageType = 'application';
    result.appName = pathNodes[1].toLowerCase();
    if (pathNodes.length >= 4) {
      result.pageName = pathNodes[3].toLowerCase();
    }
    return result;
  }

  // ── Site / Plugin pages ─────────────────────────────────── line 1536
  if (pathNodes.length >= 4 && pathNodes[1].toLowerCase() === 's') {
    result.pageType = 'site';
    result.pageName = pathNodes[3].toLowerCase();
    return result;
  } else if (pathNodes.length >= 3 && pathNodes[1].toLowerCase() === 's') {
    result.pageType = 'site';
    result.pageName = pathNodes[2].toLowerCase();
    return result;
  }

  // ── 5+ segment paths ───────────────────────────────────── line 1551
  if (pathNodes.length >= 5) {
    const seg4 = pathNodes[4].toLowerCase();

    // ── Record path: /app/area/node/r/… ───────────────────── line 1553
    if (seg4 === 'r') {
      result.appName = pathNodes[1].toLowerCase();
      result.areaName = pathNodes[2].toLowerCase();
      result.nodeName = pathNodes[3].toLowerCase();
      result.recordId = null;

      if (pathNodes.length >= 6 && isValidGuid(pathNodes[5])) {
        result.recordId = pathNodes[5].toLowerCase();
      }

      // Case 1: Has relation (/rl/) ───────────────────────── line 1569
      if (pathNodes.length >= 7 && pathNodes[6].toLowerCase() === 'rl') {
        result.hasRelation = true;

        if (pathNodes.length >= 8 && isValidGuid(pathNodes[7])) {
          result.relationId = pathNodes[7].toLowerCase();
        }

        if (pathNodes.length >= 9) {
          const relAction = pathNodes[8].toLowerCase();

          switch (relAction) {
            case 'l': {
              // RecordList inside relation ──────────────────── line 1579
              if (pathNodes.length >= 10) {
                result.pageName = pathNodes[9];
              }
              result.parentRecordId =
                pathNodes.length >= 6 && isValidGuid(pathNodes[5])
                  ? pathNodes[5].toLowerCase()
                  : null;
              result.recordId =
                pathNodes.length >= 10 && isValidGuid(pathNodes[9])
                  ? pathNodes[9].toLowerCase()
                  : null;
              result.pageType = 'recordList';
              return result;
            }
            case 'c': {
              // RecordCreate inside relation ────────────────── line 1595
              if (pathNodes.length >= 10) {
                result.pageName = pathNodes[9];
              }
              result.parentRecordId =
                pathNodes.length >= 6 && isValidGuid(pathNodes[5])
                  ? pathNodes[5].toLowerCase()
                  : null;
              result.recordId =
                pathNodes.length >= 10 && isValidGuid(pathNodes[9])
                  ? pathNodes[9].toLowerCase()
                  : null;
              result.pageType = 'recordCreate';
              return result;
            }
            case 'r': {
              // RecordDetails inside relation ───────────────── line 1612
              if (pathNodes.length >= 11) {
                result.pageName = pathNodes[10];
              }
              result.parentRecordId =
                pathNodes.length >= 6 && isValidGuid(pathNodes[5])
                  ? pathNodes[5].toLowerCase()
                  : null;
              result.recordId =
                pathNodes.length >= 10 && isValidGuid(pathNodes[9])
                  ? pathNodes[9].toLowerCase()
                  : null;
              result.pageType = 'recordDetails';
              return result;
            }
            case 'm': {
              // RecordManage inside relation ────────────────── line 1629
              if (pathNodes.length >= 11) {
                result.pageName = pathNodes[10];
              }
              result.parentRecordId =
                pathNodes.length >= 6 && isValidGuid(pathNodes[5])
                  ? pathNodes[5].toLowerCase()
                  : null;
              result.recordId =
                pathNodes.length >= 10 && isValidGuid(pathNodes[9])
                  ? pathNodes[9].toLowerCase()
                  : null;
              result.pageType = 'recordManage';
              return result;
            }
            default: {
              // Unknown relation URL structure ──────────────── line 1645
              result.pageType = 'recordDetails';
              return result;
            }
          }
        } else {
          // Relation segment present but no action code ────── line 1652
          result.pageType = 'recordDetails';
          return result;
        }
      }

      // Case 2: No relation ─────────────────────────────────── line 1660
      if (pathNodes.length >= 7) {
        result.pageName = pathNodes[6];
      }
      result.pageType = 'recordDetails';
      return result;
    }

    // ── Create: /app/area/node/c/{page?} ──────────────────── line 1670
    if (seg4 === 'c') {
      result.pageType = 'recordCreate';
      result.appName = pathNodes[1].toLowerCase();
      result.areaName = pathNodes[2].toLowerCase();
      result.nodeName = pathNodes[3].toLowerCase();
      if (pathNodes.length >= 6) {
        result.pageName = pathNodes[5].toLowerCase();
      }
    }

    // ── Manage: /app/area/node/m/{page?} ──────────────────── line 1682
    else if (seg4 === 'm') {
      result.pageType = 'recordManage';
      result.appName = pathNodes[1].toLowerCase();
      result.areaName = pathNodes[2].toLowerCase();
      result.nodeName = pathNodes[3].toLowerCase();
      if (pathNodes.length >= 6) {
        result.pageName = pathNodes[5].toLowerCase();
      }
    }

    // ── List: /app/area/node/l/{page?} ────────────────────── line 1694
    else if (seg4 === 'l') {
      result.pageType = 'recordList';
      result.appName = pathNodes[1].toLowerCase();
      result.areaName = pathNodes[2].toLowerCase();
      result.nodeName = pathNodes[3].toLowerCase();
      if (pathNodes.length >= 6) {
        result.pageName = pathNodes[5].toLowerCase();
      }
    }

    // ── Nested Application: /app/area/node/a/{page?} ──────── line 1706
    else if (seg4 === 'a') {
      result.pageType = 'application';
      result.appName = pathNodes[1].toLowerCase();
      result.areaName = pathNodes[2].toLowerCase();
      result.nodeName = pathNodes[3].toLowerCase();
      if (pathNodes.length >= 6) {
        result.pageName = pathNodes[5].toLowerCase();
      }
      return result;
    }
  }

  return result;
}

// =============================================================================
// Phase 5: Miscellaneous Helpers
// =============================================================================

/**
 * Safely accesses a nested object property by dot-notation path.
 *
 * Supports both dot notation (`'a.b.c'`) and array-index notation
 * (`'items[0].name'`). Returns `undefined` when any segment in the path
 * resolves to a nullish value.
 *
 * Replaces `ReflectionExtensions.GetPropValue` from
 * `WebVella.Erp.Web/Utils/ReflectionExtensions.cs`.
 *
 * @param obj  - The root object to traverse.
 * @param path - Dot-separated property path (e.g. `'address.city'`).
 * @returns The value at the path, or `undefined` if unreachable.
 *
 * @example
 * ```ts
 * getNestedProperty({ a: { b: [10, 20] } }, 'a.b[1]'); // 20
 * getNestedProperty({ a: null }, 'a.b.c');               // undefined
 * ```
 */
export function getNestedProperty(obj: Record<string, unknown>, path: string): unknown {
  if (obj == null || !path) {
    return undefined;
  }

  // Normalise array notation → dot notation: 'items[0].name' → 'items.0.name'
  const normalizedPath = path.replace(/\[(\d+)\]/g, '.$1');
  const segments = normalizedPath.split('.');

  let current: unknown = obj;
  for (const segment of segments) {
    if (current == null || typeof current !== 'object') {
      return undefined;
    }
    current = (current as Record<string, unknown>)[segment];
  }

  return current;
}

/**
 * Creates a deep clone of a value for immutable operations.
 *
 * Prefers the native `structuredClone` API (available in modern browsers
 * and Node.js ≥ 17). Falls back to `JSON.parse(JSON.stringify(…))` for
 * older environments (note: the fallback does not preserve `Date`, `Map`,
 * `Set`, `RegExp`, or circular references).
 *
 * @typeParam T - The value type.
 * @param obj - The value to clone.
 * @returns A deep copy of `obj`.
 */
export function deepClone<T>(obj: T): T {
  if (obj === null || obj === undefined) {
    return obj;
  }

  if (typeof structuredClone === 'function') {
    return structuredClone(obj);
  }

  return JSON.parse(JSON.stringify(obj)) as T;
}

/**
 * Creates a debounced version of a function that delays invocation until
 * `delay` milliseconds have elapsed since the last call.
 *
 * Commonly used for search-input throttling and batched API requests.
 *
 * @typeParam T - The original function type.
 * @param fn    - The function to debounce.
 * @param delay - Delay in milliseconds.
 * @returns A debounced wrapper with the same parameter signature.
 *
 * @example
 * ```ts
 * const search = debounce((query: string) => api.search(query), 300);
 * inputElement.addEventListener('input', (e) => search(e.target.value));
 * ```
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function debounce<T extends (...args: any[]) => any>(
  fn: T,
  delay: number
): (...args: Parameters<T>) => void {
  let timeoutId: ReturnType<typeof setTimeout> | null = null;

  return function debounced(this: unknown, ...args: Parameters<T>): void {
    if (timeoutId !== null) {
      clearTimeout(timeoutId);
    }
    timeoutId = setTimeout(() => {
      fn.apply(this, args);
      timeoutId = null;
    }, delay);
  };
}

/**
 * Builds a CSS class-name string from a mix of strings, conditional
 * objects, and falsy values. Inspired by `clsx` / `classnames` but
 * zero-dependency.
 *
 * @param args - Class-name segments: strings are included as-is,
 *               object entries are included when their value is truthy,
 *               falsy values are silently skipped.
 * @returns A single space-separated class-name string.
 *
 * @example
 * ```ts
 * classNames('btn', { 'btn-active': true, 'btn-disabled': false }, null);
 * // "btn btn-active"
 * ```
 */
export function classNames(
  ...args: (string | Record<string, boolean> | undefined | null | false)[]
): string {
  const classes: string[] = [];

  for (const arg of args) {
    if (!arg) {
      continue;
    }
    if (typeof arg === 'string') {
      classes.push(arg);
    } else if (typeof arg === 'object') {
      for (const [key, value] of Object.entries(arg)) {
        if (value) {
          classes.push(key);
        }
      }
    }
  }

  return classes.join(' ');
}

/**
 * Groups array items into a `Record` keyed by the return value of `keyFn`.
 *
 * Used for grouping pages by sitemap node, records by entity, etc.
 *
 * @typeParam T - The item type.
 * @param items - Array of items to group.
 * @param keyFn - Function that returns the grouping key for an item.
 * @returns An object mapping each key to its array of matching items.
 *
 * @example
 * ```ts
 * groupBy([{ type: 'a', v: 1 }, { type: 'b', v: 2 }, { type: 'a', v: 3 }], (i) => i.type);
 * // { a: [{ type: 'a', v: 1 }, { type: 'a', v: 3 }], b: [{ type: 'b', v: 2 }] }
 * ```
 */
export function groupBy<T>(
  items: T[],
  keyFn: (item: T) => string
): Record<string, T[]> {
  const result: Record<string, T[]> = {};

  for (const item of items) {
    const key = keyFn(item);
    if (!result[key]) {
      result[key] = [];
    }
    result[key].push(item);
  }

  return result;
}

/**
 * Converts a text string to a URL-safe slug.
 *
 * Transformation steps:
 * 1. Lowercase
 * 2. Unicode NFD normalisation → strip combining diacritics
 * 3. Remove non-alphanumeric characters (except spaces and hyphens)
 * 4. Replace whitespace runs with a single hyphen
 * 5. Collapse consecutive hyphens
 * 6. Trim leading / trailing hyphens
 *
 * @param text - The input text to slugify.
 * @returns A URL-safe slug string, or `''` for falsy input.
 *
 * @example
 * ```ts
 * slugify('Hello World!');        // "hello-world"
 * slugify('Ünïcödé Tëxt');       // "unicode-text"
 * slugify('  Multiple   Spaces '); // "multiple-spaces"
 * ```
 */
export function slugify(text: string): string {
  if (!text) {
    return '';
  }

  return text
    .toLowerCase()
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '') // strip combining diacritical marks
    .replace(/[^a-z0-9\s-]/g, '')    // remove non-alphanumeric (except space/hyphen)
    .replace(/\s+/g, '-')            // collapse whitespace → single hyphen
    .replace(/-+/g, '-')             // collapse consecutive hyphens
    .replace(/^-|-$/g, '');          // trim edge hyphens
}
