import React, { useMemo } from 'react';
import { Link, useParams, useLocation } from 'react-router-dom';

// ---------------------------------------------------------------------------
// Exported Interfaces
// ---------------------------------------------------------------------------

/**
 * Represents a single breadcrumb segment.
 *
 * @property label   – Human-readable text displayed for the breadcrumb item.
 * @property href    – Optional URL. When provided the item renders as a
 *                     clickable `<Link>`, otherwise as plain text.
 * @property isActive – Indicates the segment is the current page (styled
 *                     differently and rendered without a link).
 */
export interface BreadcrumbItem {
  label: string;
  href?: string;
  isActive?: boolean;
}

/**
 * Props accepted by the `Breadcrumb` component.
 *
 * @property items    – Explicit breadcrumb items. When provided, route-based
 *                      auto-generation is skipped and these items are rendered
 *                      directly.
 * @property showHome – Whether to prepend a "Home" item linking to "/".
 *                      Defaults to `true`.
 * @property className – Additional CSS class names applied to the outer
 *                       `<nav>` element.
 */
export interface BreadcrumbProps {
  items?: BreadcrumbItem[];
  showHome?: boolean;
  className?: string;
}

// ---------------------------------------------------------------------------
// Utility: slug → human-readable label
// ---------------------------------------------------------------------------

/**
 * Converts a URL-safe slug into a title-cased display label.
 *
 * Transformation rules:
 *  1. Replace hyphens and underscores with spaces.
 *  2. Capitalise the first letter of every word.
 *
 * Examples:
 *   "account_management"  → "Account Management"
 *   "my-crm-app"          → "My Crm App"
 *   "tasks"               → "Tasks"
 */
function formatSlug(slug: string): string {
  if (!slug) return '';
  return slug
    .replace(/[-_]/g, ' ')
    .replace(/\b\w/g, (char) => char.toUpperCase());
}

// ---------------------------------------------------------------------------
// Utility: Determine page-context label from pathname tail
// ---------------------------------------------------------------------------

/**
 * Maps the page-type segment of the URL to a readable label.
 *
 * Route conventions (derived from monolith `NavViewComponent.cs` line 52-63):
 *   /…/l/         → list view
 *   /…/c/         → create
 *   /…/a/         → application page (home)
 *   /…/r/:id      → record detail
 *   /…/m/:id      → record manage / edit
 */
function resolvePageContextLabel(pathname: string): string | null {
  const segments = pathname.replace(/\/+$/, '').split('/').filter(Boolean);
  if (segments.length === 0) return null;

  const tail = segments[segments.length - 1];
  const secondLast = segments.length >= 2 ? segments[segments.length - 2] : null;

  // If the second-to-last segment is 'r' or 'm', the tail is a record ID.
  if (secondLast === 'r') return `Record ${tail}`;
  if (secondLast === 'm') return `Edit ${tail}`;

  switch (tail) {
    case 'l':
      return 'List';
    case 'c':
      return 'Create';
    case 'a':
      return null; // Application page — no extra breadcrumb
    default:
      return null;
  }
}

// ---------------------------------------------------------------------------
// Chevron-right SVG separator
// ---------------------------------------------------------------------------

/**
 * Inline SVG chevron-right icon used as visual separator between breadcrumb
 * items. Uses `currentColor` so it inherits the separator colour set via
 * Tailwind text-colour utilities.
 */
function ChevronSeparator(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 20 20"
      fill="currentColor"
      aria-hidden="true"
      className="inline-block h-4 w-4 shrink-0 text-gray-400"
    >
      <path
        fillRule="evenodd"
        d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
        clipRule="evenodd"
      />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Breadcrumb Component
// ---------------------------------------------------------------------------

/**
 * Breadcrumb navigation component.
 *
 * This component replaces the implicit breadcrumb behaviour in the monolith
 * where navigation context was derived from `ErpRequestContext` properties
 * (`CurrentApp`, `SitemapArea`, `SitemapNode`) and rendered via
 * `NavViewComponent.cs` (lines 25-28).
 *
 * It supports two modes:
 *   1. **Route-based** (default) — breadcrumb items are auto-generated from
 *      React Router parameters (`:appName`, `:areaName`, `:nodeName`, etc.).
 *   2. **Explicit** — when `items` prop is supplied, those items are rendered
 *      verbatim.
 *
 * The breadcrumb hierarchy follows the monolith's URL convention:
 *   Home → App → Area → Node → [Page Context]
 */
function Breadcrumb({
  items: explicitItems,
  showHome = true,
  className = '',
}: BreadcrumbProps): React.ReactElement {
  // React Router hooks ---------------------------------------------------
  const params = useParams<{
    appName?: string;
    areaName?: string;
    nodeName?: string;
    pageName?: string;
    recordId?: string;
  }>();

  const location = useLocation();

  // Build breadcrumb items -----------------------------------------------
  const breadcrumbItems: BreadcrumbItem[] = useMemo(() => {
    // --- Explicit mode ---
    if (explicitItems && explicitItems.length > 0) {
      // Mark the last item as active if not already set.
      return explicitItems.map((item, idx) => ({
        ...item,
        isActive: item.isActive ?? idx === explicitItems.length - 1,
      }));
    }

    // --- Route-based mode ---
    const trail: BreadcrumbItem[] = [];

    // 1. Home
    if (showHome) {
      trail.push({ label: 'Home', href: '/' });
    }

    const { appName, areaName, nodeName } = params;

    // 2. Application
    if (appName) {
      trail.push({
        label: formatSlug(appName),
        href: `/${appName}/a/`,
      });
    }

    // 3. Area
    if (appName && areaName) {
      // Link to the area's first node in list view (convention from monolith
      // NavViewComponent.cs line 54-57).
      const areaHref = nodeName
        ? `/${appName}/${areaName}/${nodeName}/l/`
        : `/${appName}/${areaName}/`;
      trail.push({
        label: formatSlug(areaName),
        href: areaHref,
      });
    }

    // 4. Node
    if (appName && areaName && nodeName) {
      trail.push({
        label: formatSlug(nodeName),
        href: `/${appName}/${areaName}/${nodeName}/l/`,
      });
    }

    // 5. Page context (list / create / detail / manage)
    const pageContextLabel = resolvePageContextLabel(location.pathname);
    if (pageContextLabel) {
      trail.push({ label: pageContextLabel });
    }

    // Mark the last item as active.
    if (trail.length > 0) {
      trail[trail.length - 1] = {
        ...trail[trail.length - 1],
        isActive: true,
        href: undefined, // Active item should not be a link.
      };
    }

    return trail;
  }, [explicitItems, showHome, params, location.pathname]);

  // Nothing to render ----------------------------------------------------
  if (breadcrumbItems.length === 0) {
    return <nav aria-label="Breadcrumb" className={className} />;
  }

  // Render ---------------------------------------------------------------
  return (
    <nav
      aria-label="Breadcrumb"
      className={`flex items-center gap-2 text-sm text-gray-600 ${className}`.trim()}
    >
      <ol className="flex items-center gap-1" role="list">
        {breadcrumbItems.map((item, index) => {
          const isLast = index === breadcrumbItems.length - 1;
          const active = item.isActive ?? isLast;

          return (
            <li key={`${item.label}-${index}`} className="flex items-center gap-1">
              {/* Separator (before every item except the first) */}
              {index > 0 && <ChevronSeparator />}

              {/* Item content */}
              {active || !item.href ? (
                <span
                  className="font-semibold text-gray-900"
                  aria-current={active ? 'page' : undefined}
                >
                  {item.label}
                </span>
              ) : (
                <Link
                  to={item.href}
                  className="transition-colors duration-150 hover:text-blue-600 hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
                >
                  {item.label}
                </Link>
              )}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

export default Breadcrumb;
