/**
 * TopNav — Top Navigation Bar Component
 *
 * Replaces the monolith's three ViewComponents:
 *   - Nav/NavViewComponent.cs + Nav.Default.cshtml + script.js
 *   - SiteMenu/SiteMenu.cs + SiteMenu.cshtml
 *   - ApplicationMenu/ApplicationMenu.cs + ApplicationMenu.cshtml
 *
 * Combines Home link, SiteMenu (hamburger), brand/logo, ApplicationMenu
 * (sitemap areas/nodes), and right-side user controls (search, UserMenu)
 * into a single React component.
 *
 * All dropdown behaviour uses React state — zero jQuery.
 * All styling uses Tailwind CSS — zero Bootstrap.
 * Icon classes replaced by lucide-react SVG components.
 */

import { useState, useEffect, useCallback, useRef } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { Home, Menu, Search, X, ChevronDown } from 'lucide-react';
import UserMenu from './UserMenu';
import { useAppStore } from '../../stores/appStore';
import type { App, MenuItem, SitemapNodeType } from '../../types/app';

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

/**
 * Props accepted by the TopNav component.
 *
 * Both props are optional — the component reads most navigation data
 * from the useAppStore Zustand hook and computes derived values
 * (appDefaultLink) internally.
 */
export interface TopNavProps {
  /**
   * Brand logo image URL. Replaces the monolith's `theme.BrandLogo`
   * with an optional `ErpSettings.NavLogoUrl` override from
   * NavViewComponent.cs lines 31-34.
   *
   * When omitted or empty, the brand section shows only the app label.
   */
  brandLogo?: string;

  /**
   * Development mode flag. Replaces `ErpSettings.DevelopmentMode`
   * from NavViewComponent.cs line 35.
   *
   * When true, a red full-width "DEV MODE" banner is rendered above
   * the navigation bar (matching Nav.Default.cshtml line 13).
   */
  devMode?: boolean;
}

// ---------------------------------------------------------------------------
// Utility — App Default Link Computation
// ---------------------------------------------------------------------------

/**
 * Compute the default navigation link for the current application.
 *
 * Replicates the logic from NavViewComponent.cs lines 38-68:
 *   1. If app has homePages → ordered by weight, link = /{app}/a/{page}
 *   2. Else if app has sitemap areas → first area's first node by type:
 *      - ApplicationPage → /{app}/{area}/{node}/a/
 *      - EntityList      → /{app}/{area}/{node}/l/
 *      - Url             → raw URL from node.url
 *   3. Fallback → "/"
 *
 * @param app - The current App object from the appStore.
 * @returns The computed URL path for the brand logo link.
 */
function computeAppDefaultLink(app: App | null): string {
  if (!app) {
    return '/';
  }

  /* Priority 1: Home pages ordered by weight */
  if (app.homePages && app.homePages.length > 0) {
    const sorted = [...app.homePages].sort((a, b) => a.weight - b.weight);
    return `/${app.name}/a/${sorted[0].name}`;
  }

  /* Priority 2: First sitemap area → first node by type */
  if (app.sitemap && app.sitemap.areas && app.sitemap.areas.length > 0) {
    const sortedAreas = [...app.sitemap.areas].sort(
      (a, b) => a.weight - b.weight,
    );

    for (const area of sortedAreas) {
      if (area.nodes && area.nodes.length > 0) {
        const node = area.nodes[0];

        /*
         * SitemapNodeType is a const enum inlined at compile time:
         *   ApplicationPage = 2
         *   EntityList      = 1
         *   Url             = 3
         */
        switch (node.type as number) {
          case 2 /* SitemapNodeType.ApplicationPage */:
            return `/${app.name}/${area.name}/${node.name}/a/`;
          case 1 /* SitemapNodeType.EntityList */:
            return `/${app.name}/${area.name}/${node.name}/l/`;
          case 3 /* SitemapNodeType.Url */:
            return node.url || '/';
          default:
            /* Matches the monolith's throw new Exception("Type not found") */
            console.error(
              `[TopNav] computeAppDefaultLink: unknown SitemapNodeType "${String(node.type)}"`,
            );
            return '/';
        }
      }
    }
  }

  return '/';
}

// ---------------------------------------------------------------------------
// Sub-component — Recursive Menu Item Renderer
// ---------------------------------------------------------------------------

/**
 * Renders a single MenuItem and its children recursively.
 *
 * Replaces the `<partial name="NavMenu" />` rendering loop used in both
 * SiteMenu.cshtml (lines 18-28) and ApplicationMenu.cshtml (line 13).
 *
 * Supports:
 *   - HTML content items (MenuItem.isHtml) rendered via dangerouslySetInnerHTML
 *   - Plain-text items rendered as navigation links
 *   - Recursive child node rendering
 *
 * @param item      - The menu item to render.
 * @param depth     - Current nesting depth (0 = root level).
 * @param location  - Current route pathname for active detection.
 */
function MenuItemRenderer({
  item,
  depth = 0,
  location,
}: {
  item: MenuItem;
  depth?: number;
  location: string;
}) {
  /* Sort child nodes by sortOrder for consistent rendering */
  const sortedChildren =
    item.nodes && item.nodes.length > 0
      ? [...item.nodes].sort((a, b) => a.sortOrder - b.sortOrder)
      : [];

  /* Active state detection based on content as URL path */
  const isActive =
    !item.isHtml && item.content ? location.startsWith(item.content) : false;

  /* HTML content items — dangerouslySetInnerHTML (monolith: Html.Raw) */
  if (item.isHtml && item.content) {
    return (
      <li role="none" className={item.class || ''}>
        <div
          className="block px-3 py-2 text-sm text-gray-700"
          dangerouslySetInnerHTML={{ __html: item.content }}
        />
        {sortedChildren.length > 0 && (
          <ul role="menu" className="ps-2">
            {sortedChildren.map((child) => (
              <MenuItemRenderer
                key={child.id}
                item={child}
                depth={depth + 1}
                location={location}
              />
            ))}
          </ul>
        )}
      </li>
    );
  }

  /* Plain text / link items */
  const href = item.content || '#';
  const isExternal = href.startsWith('http');
  const baseClasses = `block px-3 py-2 text-sm transition-colors duration-150 
    focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 
    ${isActive ? 'text-white bg-blue-600 font-medium' : 'text-gray-700 hover:bg-gray-100'}
    ${item.class || ''}`;

  return (
    <li role="none">
      {isExternal ? (
        <a
          href={href}
          target="_blank"
          rel="noopener noreferrer"
          role="menuitem"
          className={baseClasses}
        >
          {item.content}
        </a>
      ) : (
        <Link to={href} role="menuitem" className={baseClasses}>
          {item.content}
        </Link>
      )}

      {/* Recursive child nodes */}
      {sortedChildren.length > 0 && (
        <ul role="menu" className="ps-2">
          {sortedChildren.map((child) => (
            <MenuItemRenderer
              key={child.id}
              item={child}
              depth={depth + 1}
              location={location}
            />
          ))}
        </ul>
      )}
    </li>
  );
}

// ---------------------------------------------------------------------------
// Sub-component — Application Menu Area Dropdown
// ---------------------------------------------------------------------------

/**
 * Renders a single application menu area with its nested node tree.
 *
 * Replaces the per-area rendering from ApplicationMenu.cshtml (line 10-14)
 * with area label trigger and dropdown containing sorted node items.
 *
 * @param area          - The area MenuItem to render.
 * @param isOpen        - Whether this area's dropdown is currently open.
 * @param onToggle      - Callback to toggle this area's dropdown.
 * @param location      - Current route pathname for active detection.
 */
function AreaDropdown({
  area,
  isOpen,
  onToggle,
  location,
}: {
  area: MenuItem;
  isOpen: boolean;
  onToggle: () => void;
  location: string;
}) {
  const sortedNodes =
    area.nodes && area.nodes.length > 0
      ? [...area.nodes].sort((a, b) => a.sortOrder - b.sortOrder)
      : [];

  /* Determine if any child node is active */
  const hasActiveChild = sortedNodes.some(
    (node) =>
      !node.isHtml && node.content && location.startsWith(node.content),
  );

  /* If the area has no nested nodes, render it as a direct link */
  if (sortedNodes.length === 0) {
    if (area.isHtml && area.content) {
      return (
        <div
          className="inline-flex items-center px-3 py-2 text-sm text-gray-300"
          dangerouslySetInnerHTML={{ __html: area.content }}
        />
      );
    }
    return (
      <Link
        to={area.content || '#'}
        className={`inline-flex items-center px-3 py-2 text-sm transition-colors duration-150
          focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
          ${hasActiveChild ? 'text-white font-medium' : 'text-gray-300 hover:text-white'}`}
      >
        {area.isHtml ? '' : area.content}
      </Link>
    );
  }

  /* Area with nested nodes → dropdown */
  return (
    <div className="relative inline-flex">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={isOpen}
        aria-haspopup="true"
        className={`inline-flex items-center gap-1 px-3 py-2 text-sm transition-colors duration-150
          focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
          ${hasActiveChild || isOpen ? 'text-white font-medium' : 'text-gray-300 hover:text-white'}`}
      >
        {area.isHtml ? (
          <span dangerouslySetInnerHTML={{ __html: area.content }} />
        ) : (
          <span>{area.content}</span>
        )}
        <ChevronDown
          className={`h-3.5 w-3.5 transition-transform duration-150 ${isOpen ? 'rotate-180' : ''}`}
          aria-hidden="true"
        />
      </button>

      {isOpen && (
        <ul
          role="menu"
          className={`absolute top-full z-50 mt-1 min-w-48 rounded-md border border-gray-200 bg-white py-1 shadow-lg
            ${area.isDropdownRight ? 'end-0' : 'start-0'}`}
        >
          {sortedNodes.map((node) => (
            <MenuItemRenderer
              key={node.id}
              item={node}
              depth={0}
              location={location}
            />
          ))}
        </ul>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main Component — TopNav
// ---------------------------------------------------------------------------

/**
 * TopNav — primary top navigation bar for the WebVella ERP SPA.
 *
 * Structure (matching Nav.Default.cshtml DOM order):
 *   1. Dev mode banner (conditional)
 *   2. Nav bar container
 *      a. Home link (lucide Home icon → "/")
 *      b. Site menu (hamburger → dropdown)
 *      c. Brand logo + app label (→ computed app default link)
 *      d. Application menu (sitemap areas → dropdowns)
 *      e. Right section (search icon, UserMenu)
 *
 * Dropdown state management replaces jQuery script.js:
 *   - openDropdown: string | null tracks which dropdown is currently visible
 *   - Only one dropdown can be open at a time
 *   - Clicking outside any .menu-nav-wrapper area closes all dropdowns
 */
function TopNav({ brandLogo, devMode = false }: TopNavProps) {
  // ── Store hooks ──────────────────────────────────────────────────────────
  const currentApp = useAppStore((s) => s.currentApp);
  const siteMenu = useAppStore((s) => s.siteMenu);
  const applicationMenu = useAppStore((s) => s.applicationMenu);

  // ── Router ───────────────────────────────────────────────────────────────
  const location = useLocation();

  // ── State ────────────────────────────────────────────────────────────────

  /**
   * Tracks which dropdown is currently open. null = all closed.
   * Replaces the jQuery `data-opened-menu` attribute on `#nav` from
   * script.js line 5/31 and the d-block class toggling from lines 18-32.
   */
  const [openDropdown, setOpenDropdown] = useState<string | null>(null);

  /**
   * Search input visibility toggle for the nav-right search area.
   */
  const [isSearchOpen, setIsSearchOpen] = useState<boolean>(false);

  /**
   * Search query text bound to the search input.
   */
  const [searchQuery, setSearchQuery] = useState<string>('');

  // ── Refs ─────────────────────────────────────────────────────────────────

  /**
   * Reference to the nav bar container element.
   * Used for outside-click detection (replaces jQuery closest
   * ".menu-nav-wrapper" traversal from script.js lines 42-48).
   */
  const navRef = useRef<HTMLElement>(null);

  // ── Computed values ──────────────────────────────────────────────────────

  /**
   * Computed default link for the brand logo.
   * Replicates NavViewComponent.cs lines 38-68.
   */
  const appDefaultLink = computeAppDefaultLink(currentApp);

  /**
   * App short name for display (replaces currentApp.Name.Replace("_", "")
   * from NavViewComponent.cs line 40).
   */
  const appLabel = currentApp?.label || '';
  const appColor = currentApp?.color || '#2196F3';

  // ── Callbacks ────────────────────────────────────────────────────────────

  /**
   * Toggle a specific dropdown by name. If the dropdown is already open,
   * close it; otherwise close any open dropdown and open the requested one.
   *
   * Replaces the jQuery click handler from script.js lines 9-33:
   *   - If clicked dropdown has d-block → remove d-block from all
   *   - Else → remove d-block from all, add d-block to clicked
   */
  const toggleDropdown = useCallback(
    (name: string) => {
      setOpenDropdown((prev) => (prev === name ? null : name));
    },
    [],
  );

  /**
   * Close all dropdowns. Used by outside-click handler and keyboard events.
   */
  const closeAllDropdowns = useCallback(() => {
    setOpenDropdown(null);
  }, []);

  /**
   * Toggle search input visibility.
   */
  const toggleSearch = useCallback(() => {
    setIsSearchOpen((prev) => {
      if (prev) {
        setSearchQuery('');
      }
      return !prev;
    });
  }, []);

  // ── Effects ──────────────────────────────────────────────────────────────

  /**
   * Outside-click detection.
   *
   * Replaces jQuery `document.addEventListener("click", ...)` from
   * script.js lines 35-53. Closes all dropdowns when clicking outside
   * the nav container.
   */
  useEffect(() => {
    function handleOutsideClick(event: MouseEvent) {
      if (
        navRef.current &&
        !navRef.current.contains(event.target as Node)
      ) {
        closeAllDropdowns();
      }
    }

    document.addEventListener('click', handleOutsideClick, true);
    return () => {
      document.removeEventListener('click', handleOutsideClick, true);
    };
  }, [closeAllDropdowns]);

  /**
   * Close dropdowns on route change.
   * Ensures menus close when the user navigates via a menu link.
   */
  useEffect(() => {
    closeAllDropdowns();
  }, [location.pathname, closeAllDropdowns]);

  /**
   * Close dropdowns on Escape key press for keyboard accessibility.
   */
  useEffect(() => {
    function handleEscape(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        closeAllDropdowns();
        setIsSearchOpen(false);
        setSearchQuery('');
      }
    }

    document.addEventListener('keydown', handleEscape);
    return () => {
      document.removeEventListener('keydown', handleEscape);
    };
  }, [closeAllDropdowns]);

  // ── Sorted menus ─────────────────────────────────────────────────────────

  const sortedSiteMenu = siteMenu.length > 0
    ? [...siteMenu].sort((a, b) => a.sortOrder - b.sortOrder)
    : [];

  const sortedAppMenu = applicationMenu.length > 0
    ? [...applicationMenu].sort((a, b) => a.sortOrder - b.sortOrder)
    : [];

  // ── Render ───────────────────────────────────────────────────────────────

  return (
    <>
      {/* Dev mode banner — exactly matches Nav.Default.cshtml line 13 */}
      {devMode && (
        <div
          role="alert"
          className="bg-red-600 py-1 text-center text-sm font-bold text-white"
        >
          DEV MODE
        </div>
      )}

      {/* Main navigation bar */}
      <nav
        ref={navRef}
        id="nav"
        className="flex items-center gap-0 bg-gray-800 px-2"
        role="navigation"
        aria-label="Primary navigation"
      >
        {/* ── Home link ─────────────────────────────────────────────── */}
        <div className="flex shrink-0 items-center">
          <Link
            to="/"
            className="inline-flex items-center px-2 py-2.5 text-gray-300 transition-colors duration-150 hover:text-white
              focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            aria-label="Home"
          >
            <Home className="h-5 w-5" aria-hidden="true" />
          </Link>
        </div>

        {/* ── Site menu (hamburger dropdown) ─────────────────────── */}
        {sortedSiteMenu.length > 0 && (
          <div className="relative flex shrink-0 items-center">
            <button
              type="button"
              onClick={() => toggleDropdown('siteMenu')}
              aria-expanded={openDropdown === 'siteMenu'}
              aria-haspopup="true"
              aria-label="Site menu"
              data-testid="app-switcher"
              className={`inline-flex items-center px-2 py-2.5 text-sm transition-colors duration-150
                focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
                ${openDropdown === 'siteMenu' ? 'text-white' : 'text-gray-300 hover:text-white'}`}
            >
              <Menu className="h-5 w-5" aria-hidden="true" />
            </button>

            {openDropdown === 'siteMenu' && (
              <ul
                role="menu"
                aria-label="Site navigation"
                className="absolute start-0 top-full z-50 mt-1 min-w-56 rounded-md border border-gray-200 bg-white py-1 shadow-lg"
              >
                {sortedSiteMenu.map((item) => (
                  <MenuItemRenderer
                    key={item.id}
                    item={item}
                    depth={0}
                    location={location.pathname}
                  />
                ))}
              </ul>
            )}
          </div>
        )}

        {/* ── Brand logo + app label ────────────────────────────── */}
        <div className="flex shrink-0 items-center px-3">
          <Link
            to={appDefaultLink}
            className="inline-flex items-center gap-2 text-gray-300 transition-colors duration-150 hover:text-white
              focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          >
            {brandLogo && (
              <img
                src={brandLogo}
                alt=""
                aria-hidden="true"
                className="h-7 w-auto"
                width={28}
                height={28}
              />
            )}
            <span
              className="max-w-32 truncate text-sm font-semibold"
              style={{ color: appColor }}
              title={appLabel}
            >
              {appLabel || 'WebVella'}
            </span>
          </Link>
        </div>

        {/* ── Application menu (sitemap areas/nodes) ────────────── */}
        <div
          className="flex flex-1 items-center gap-0 overflow-x-auto"
          role="menubar"
          aria-label="Application navigation"
        >
          {sortedAppMenu.map((area) => (
            <AreaDropdown
              key={area.id}
              area={area}
              isOpen={openDropdown === `area-${area.id}`}
              onToggle={() => toggleDropdown(`area-${area.id}`)}
              location={location.pathname}
            />
          ))}
        </div>

        {/* ── Right section (search + user menu) ────────────────── */}
        <div className="flex shrink-0 items-center gap-1">
          {/* Search toggle / input */}
          <div className="relative flex items-center">
            {isSearchOpen ? (
              <div className="flex items-center gap-1">
                <input
                  type="search"
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  placeholder="Search..."
                  aria-label="Search"
                  className="w-40 rounded-md border border-gray-600 bg-gray-700 px-3 py-1.5 text-sm text-white placeholder-gray-400
                    focus-visible:border-blue-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
                  autoFocus
                />
                <button
                  type="button"
                  onClick={toggleSearch}
                  aria-label="Close search"
                  className="inline-flex items-center p-1.5 text-gray-300 transition-colors duration-150 hover:text-white
                    focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
                >
                  <X className="h-4 w-4" aria-hidden="true" />
                </button>
              </div>
            ) : (
              <button
                type="button"
                onClick={toggleSearch}
                aria-label="Open search"
                className="inline-flex items-center px-2 py-2.5 text-gray-300 transition-colors duration-150 hover:text-white
                  focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                <Search className="h-5 w-5" aria-hidden="true" />
              </button>
            )}
          </div>

          {/* User menu */}
          <UserMenu />
        </div>
      </nav>
    </>
  );
}

// ---------------------------------------------------------------------------
// Default Export
// ---------------------------------------------------------------------------

export default TopNav;
