/**
 * Sidebar Navigation Component
 *
 * Replaces the monolith's SidebarMenu ViewComponent (SidebarMenu.cs + SidebarMenu.cshtml).
 * Renders a collapsible sidebar with hierarchical menu items sourced from the Zustand
 * appStore, active state highlighting via React Router NavLink, and a collapse toggle
 * button in the footer.
 *
 * Architecture:
 * - Menu items come from useAppStore().sidebarMenu (MenuItem[])
 * - Items may contain HTML content (isHtml=true with dangerouslySetInnerHTML) or plain text
 * - HTML content is parsed to extract link URLs for NavLink-based client-side routing
 * - Recursive rendering supports nested menu item hierarchies (replacing NavMenu partial)
 * - Collapsed mode shows icon-only items with hover tooltips
 * - Footer toggle button switches between expanded (w-64) and collapsed (w-16)
 *
 * Source: WebVella.Erp.Web/Components/SidebarMenu/SidebarMenu.cs
 *         WebVella.Erp.Web/Components/SidebarMenu/SidebarMenu.cshtml
 */

import { useState, useCallback, useEffect } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import { useAppStore } from '../../stores/appStore';
import type { MenuItem } from '../../types/app';

// ---------------------------------------------------------------------------
// SidebarProps — Public interface (named export)
// ---------------------------------------------------------------------------

/**
 * Props for the Sidebar component.
 * State is controlled by the parent AppShell component which manages
 * collapsed state and persists it to localStorage.
 */
export interface SidebarProps {
  /** Whether the sidebar is in collapsed (icon-only) mode. Default: false. */
  collapsed?: boolean;
  /** Callback invoked when the user clicks the collapse/expand toggle button. */
  onToggle?: () => void;
}

// ---------------------------------------------------------------------------
// Internal helper utilities
// ---------------------------------------------------------------------------

/**
 * Extracts a link URL from HTML content.
 * The monolith's BaseErpPageModel.Init() builds MenuItem.content as HTML strings
 * containing <a href="..."> tags. This parser extracts the href for NavLink routing.
 *
 * Example: '<a href="/app/area/node/l/"><span class="icon fa fa-list"></span> Contacts</a>'
 * Returns: '/app/area/node/l/'
 */
function extractHref(htmlContent: string): string | null {
  const hrefMatch = htmlContent.match(/href=["']([^"']+)["']/);
  if (!hrefMatch || hrefMatch[1] === '#' || hrefMatch[1].startsWith('javascript:')) {
    return null;
  }
  return hrefMatch[1];
}

/**
 * Extracts the icon CSS class from HTML content.
 * Looks for <span class="icon ..."> or <i class="..."> patterns.
 */
function extractIconClass(htmlContent: string): string {
  const iconMatch = htmlContent.match(
    /<(?:span|i)\s[^>]*class=["'](?:icon\s+)?([^"']+)["'][^>]*>/
  );
  return iconMatch ? iconMatch[1].replace(/\bicon\b/, '').trim() : '';
}

/**
 * Strips all HTML tags from a string to produce plain text.
 * Used for tooltip labels in collapsed mode and for NavLink text content.
 */
function stripHtmlTags(html: string): string {
  return html.replace(/<[^>]+>/g, '').trim();
}

// ---------------------------------------------------------------------------
// SidebarMenuItem — Internal recursive sub-component
// Replaces the monolith's @Html.Partial("NavMenu", menuItem) recursive partial.
// ---------------------------------------------------------------------------

interface SidebarMenuItemProps {
  /** The menu item data to render. */
  item: MenuItem;
  /** Whether the sidebar is in collapsed (icon-only) mode. */
  collapsed: boolean;
  /** Current nesting depth for indentation styling. */
  depth: number;
  /** Set of expanded menu item IDs for submenu visibility tracking. */
  expandedItems: Set<string>;
  /** Callback to toggle expand/collapse of a specific menu item. */
  onToggleExpand: (id: string) => void;
  /** Current route pathname from useLocation, passed from parent for efficiency. */
  currentPath: string;
}

/**
 * Renders a single sidebar menu item with optional recursive children.
 * Supports both HTML content (dangerouslySetInnerHTML for backward compat)
 * and structured NavLink rendering for client-side routing.
 */
function SidebarMenuItem({
  item,
  collapsed,
  depth,
  expandedItems,
  onToggleExpand,
  currentPath,
}: SidebarMenuItemProps) {
  const hasChildren = item.nodes.length > 0;
  const isExpanded = expandedItems.has(item.id);

  /* Sort children by sortOrder for correct display order. */
  const sortedChildren = hasChildren
    ? [...item.nodes].sort((a, b) => a.sortOrder - b.sortOrder)
    : [];

  /* Extract structured data from HTML content for NavLink routing. */
  const linkHref = item.isHtml ? extractHref(item.content) : null;
  const iconClass = item.isHtml ? extractIconClass(item.content) : '';
  const plainLabel = stripHtmlTags(item.content);

  /* Determine active state from the class field or path matching. */
  const isActiveFromClass = item.class.includes('active');
  /* Match bidirectionally: current path may be a prefix of the node URL
     (e.g. /crm/contacts/list matches /crm/contacts/list/l) or vice versa.
     Handles deep-link navigation without the /l or /a page-type suffix. */
  const isActiveFromPath = linkHref
    ? currentPath === linkHref ||
      (linkHref !== '/' && currentPath.startsWith(linkHref)) ||
      (currentPath !== '/' && linkHref.startsWith(currentPath + '/'))
    : false;
  const isActive = isActiveFromClass || isActiveFromPath;

  /* Base CSS classes for the menu item container. */
  const baseClasses = [
    'flex items-center gap-3 py-2 text-sm rounded-md',
    'transition-colors duration-150',
    collapsed ? 'justify-center px-2 mx-1' : 'px-4',
  ]
    .filter(Boolean)
    .join(' ');

  /* Nesting indentation via left border (only when expanded, depth > 0). */
  const nestingClasses =
    depth > 0 && !collapsed
      ? 'ms-4 border-s border-gray-700 ps-0'
      : '';

  /* Active vs inactive color classes. */
  const activeClasses = isActive
    ? 'bg-gray-900 text-white font-medium'
    : 'text-gray-300 hover:bg-gray-700 hover:text-white';

  /* Memoized expand/collapse toggle handler. */
  const handleExpandClick = useCallback(
    (e: React.MouseEvent) => {
      e.preventDefault();
      e.stopPropagation();
      onToggleExpand(item.id);
    },
    [onToggleExpand, item.id],
  );

  /* Keyboard expand/collapse handler for accessibility. */
  const handleExpandKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        onToggleExpand(item.id);
      }
    },
    [onToggleExpand, item.id],
  );

  /**
   * Renders the chevron SVG indicator for expand/collapse.
   * Rotates 90 degrees when expanded. This is a pure visual indicator —
   * interactive wrapper (button or span) is chosen by the caller to prevent
   * illegal <button> inside <button> nesting (HTML spec violation).
   */
  const chevronSvg = hasChildren && !collapsed ? (
    <svg
      className={[
        'w-3 h-3 shrink-0 transition-transform duration-200',
        isExpanded ? 'rotate-90' : '',
      ].join(' ')}
      viewBox="0 0 12 12"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M4 2l4 4-4 4" />
    </svg>
  ) : null;

  /**
   * Renders the chevron as an interactive button. Used only when the parent
   * element is NOT a <button> (e.g., inside NavLink <a> or <div>) to avoid
   * nesting <button> inside <button>, which violates the HTML spec.
   */
  const renderChevronButton = () => {
    if (!chevronSvg) return null;
    return (
      <button
        type="button"
        onClick={handleExpandClick}
        onKeyDown={handleExpandKeyDown}
        className="ms-auto p-1 text-gray-400 hover:text-white rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
        aria-label={isExpanded ? `Collapse ${plainLabel}` : `Expand ${plainLabel}`}
        aria-expanded={isExpanded}
      >
        {chevronSvg}
      </button>
    );
  };

  /** Renders an icon element from a CSS class string or a default dot icon. */
  const renderIcon = (cssIconClass: string) => {
    if (cssIconClass) {
      return (
        <i
          className={`${cssIconClass} shrink-0 w-4 text-center`}
          aria-hidden="true"
        />
      );
    }
    return (
      <svg
        className="w-4 h-4 shrink-0"
        viewBox="0 0 16 16"
        fill="currentColor"
        aria-hidden="true"
      >
        <circle cx="8" cy="8" r="3" />
      </svg>
    );
  };

  /**
   * Renders the item content in one of three modes:
   * 1. NavLink (preferred) — when a valid href is extracted from HTML content
   * 2. dangerouslySetInnerHTML — when HTML content has no parseable link
   * 3. Plain text — when isHtml is false
   */
  const renderItemContent = () => {
    /* Mode 1: Extracted link → use NavLink for client-side routing + active state.
       NavLink is an <a> element, so a nested <button> for the chevron is valid HTML. */
    if (linkHref) {
      return (
        <NavLink
          to={linkHref}
          end={linkHref === '/'}
          className={({ isActive: navActive }) => {
            const effectiveActive = navActive || isActive;
            const activeStyle = effectiveActive
              ? 'bg-gray-900 text-white font-medium active'
              : 'text-gray-300 hover:bg-gray-700 hover:text-white';
            return `${baseClasses} ${nestingClasses} ${activeStyle} focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400`;
          }}
          title={collapsed ? plainLabel : undefined}
          aria-current={isActive ? 'page' : undefined}
          data-active={isActive ? 'true' : undefined}
        >
          {renderIcon(iconClass)}
          {!collapsed && (
            <span className="truncate flex-1">{plainLabel}</span>
          )}
          {renderChevronButton()}
        </NavLink>
      );
    }

    /* Mode 2: HTML content without parseable link → dangerouslySetInnerHTML */
    if (item.isHtml && item.content) {
      const combinedClasses = [
        baseClasses,
        nestingClasses,
        activeClasses,
        item.class || '',
      ]
        .filter(Boolean)
        .join(' ');

      /* If item has children, wrap in a clickable container for expand/collapse.
         Parent is a <div>, so nesting a <button> chevron is valid HTML. */
      if (hasChildren) {
        return (
          <div className="flex items-center w-full">
            <div
              className={`${combinedClasses} flex-1`}
              dangerouslySetInnerHTML={{ __html: item.content }}
              title={collapsed ? plainLabel : undefined}
            />
            {renderChevronButton()}
          </div>
        );
      }

      return (
        <div
          className={combinedClasses}
          dangerouslySetInnerHTML={{ __html: item.content }}
          title={collapsed ? plainLabel : undefined}
        />
      );
    }

    /* Mode 3: Plain text content with children — parent is a <button>, so the
       chevron must be a non-interactive <span> to avoid <button> inside <button>. */
    if (hasChildren) {
      return (
        <button
          type="button"
          onClick={handleExpandClick}
          className={`${baseClasses} ${nestingClasses} ${activeClasses} w-full text-start focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400`}
          aria-expanded={isExpanded}
          title={collapsed ? plainLabel : undefined}
        >
          {renderIcon(iconClass)}
          {!collapsed && (
            <span className="truncate flex-1">{item.content}</span>
          )}
          {chevronSvg && (
            <span className="ml-auto shrink-0" aria-hidden="true">
              {chevronSvg}
            </span>
          )}
        </button>
      );
    }

    return (
      <span
        className={`${baseClasses} ${nestingClasses} ${activeClasses} ${item.class || ''}`}
        title={collapsed ? plainLabel : undefined}
      >
        {renderIcon(iconClass)}
        {!collapsed && <span className="truncate">{item.content}</span>}
      </span>
    );
  };

  return (
    <li role="none">
      {renderItemContent()}

      {/* Recursively render child items when expanded and sidebar is not collapsed. */}
      {hasChildren && isExpanded && !collapsed && (
        <ul className="mt-1" role="group" aria-label={`${plainLabel} submenu`}>
          {sortedChildren.map((child) => (
            <SidebarMenuItem
              key={child.id}
              item={child}
              collapsed={collapsed}
              depth={depth + 1}
              expandedItems={expandedItems}
              onToggleExpand={onToggleExpand}
              currentPath={currentPath}
            />
          ))}
        </ul>
      )}
    </li>
  );
}

// ---------------------------------------------------------------------------
// Sidebar — Main component (default export)
// Replaces the full SidebarMenu ViewComponent rendering pipeline.
// ---------------------------------------------------------------------------

/**
 * Collapsible sidebar navigation component.
 *
 * Reads hierarchical menu items from the Zustand appStore (sidebarMenu)
 * and renders them recursively. Supports expanded (w-64) and collapsed (w-16)
 * modes controlled by the parent AppShell component.
 *
 * The footer contains a toggle button with a double-chevron icon matching the
 * monolith's .sidebar-switch button with fa-angle-double-right icon.
 */
function Sidebar({ collapsed = false, onToggle }: SidebarProps) {
  const sidebarMenu = useAppStore((state) => state.sidebarMenu);
  const location = useLocation();
  const [expandedItems, setExpandedItems] = useState<Set<string>>(new Set());

  /**
   * Auto-expand parent items whose children have links matching the current path.
   * This ensures the sidebar highlights the active node on initial load and
   * whenever the route changes — replicating the monolith's SidebarMenu
   * ViewComponent which rendered child nodes expanded when active.
   */
  useEffect(() => {
    const currentPath = location.pathname.toLowerCase();
    const idsToExpand = new Set<string>();

    for (const item of sidebarMenu) {
      if (item.nodes.length > 0) {
        for (const child of item.nodes) {
          const childHref = child.isHtml ? extractHref(child.content) : null;
          if (childHref) {
            const normalised = childHref.toLowerCase();
            /* Match bidirectionally: current path may be a prefix of the
               node URL (e.g. /crm/contacts/list matches /crm/contacts/list/l)
               or vice versa. This handles deep-link navigation to
               /:appName/:areaName/:nodeName without the /l or /a suffix. */
            if (
              currentPath === normalised ||
              (normalised !== '/' && currentPath.startsWith(normalised)) ||
              (currentPath !== '/' && normalised.startsWith(currentPath + '/'))
            ) {
              idsToExpand.add(item.id);
              break;
            }
          }
          /* Also check the class attribute for 'active' marker set by AppShell. */
          if (child.class.includes('active')) {
            idsToExpand.add(item.id);
            break;
          }
        }
      }
    }

    if (idsToExpand.size > 0) {
      setExpandedItems((prev) => {
        const merged = new Set(prev);
        for (const id of idsToExpand) merged.add(id);
        return merged;
      });
    }
  }, [location.pathname, sidebarMenu]);

  /** Toggle expand/collapse state for a specific menu item by ID. */
  const handleToggleExpand = useCallback((id: string) => {
    setExpandedItems((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }, []);

  /** Handle toggle button click, relaying to parent onToggle callback. */
  const handleToggleClick = useCallback(() => {
    if (onToggle) {
      onToggle();
    }
  }, [onToggle]);

  /* Sort top-level menu items by sortOrder for correct display order. */
  const sortedMenu = [...sidebarMenu].sort(
    (a, b) => a.sortOrder - b.sortOrder,
  );

  return (
    <nav
      className={[
        'flex flex-col bg-gray-800 text-white h-full',
        'transition-all duration-300',
        collapsed ? 'w-16' : 'w-64',
      ].join(' ')}
      role="navigation"
      aria-label="Sidebar navigation"
    >
      {/* Sidebar Body — scrollable menu item list */}
      <div className="flex-1 overflow-y-auto py-2">
        {sortedMenu.length > 0 ? (
          <ul className="space-y-0.5" role="menubar" aria-label="Navigation menu">
            {sortedMenu.map((item) => (
              <SidebarMenuItem
                key={item.id}
                item={item}
                collapsed={collapsed}
                depth={0}
                expandedItems={expandedItems}
                onToggleExpand={handleToggleExpand}
                currentPath={location.pathname}
              />
            ))}
          </ul>
        ) : (
          /* Empty state — displayed when the store has no sidebar menu items */
          !collapsed && (
            <p className="px-4 py-3 text-sm text-gray-500 italic">
              No navigation items
            </p>
          )
        )}
      </div>

      {/* Sidebar Footer — collapse/expand toggle button */}
      <div className="border-t border-gray-700 p-2">
        <button
          type="button"
          onClick={handleToggleClick}
          className={[
            'w-full flex items-center justify-center p-2',
            'text-gray-400 hover:text-white hover:bg-gray-700',
            'rounded transition-colors duration-150',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400',
          ].join(' ')}
          aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
        >
          {/*
           * Double-chevron icon — equivalent to monolith's
           * <i class="fas fa-fw fa-angle-double-right icon"></i>
           * Rotates 180° when expanded to point left (collapse direction).
           */}
          <svg
            className={[
              'w-5 h-5 transition-transform duration-300',
              collapsed ? '' : 'rotate-180',
            ].join(' ')}
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
          >
            <polyline points="13 17 18 12 13 7" />
            <polyline points="6 17 11 12 6 7" />
          </svg>
        </button>
      </div>
    </nav>
  );
}

export default Sidebar;
