import React, { useState, useEffect, useRef, useCallback } from 'react';
import { Link } from 'react-router-dom';
import type { PageSwitchItem } from '../../types/page';

/**
 * HeaderProps — Configurable properties for the page header component.
 *
 * Replaces `PcPageHeaderOptions` from the monolith's PcPageHeader ViewComponent
 * and `WvPageHeader` TagHelper. Every property maps to a corresponding C#
 * option or TagHelper attribute.
 */
export interface HeaderProps {
  /** Visibility toggle. When false the component renders nothing.
   *  Replaces C# `IsVisible` string parsed to bool. Default: true. */
  isVisible?: boolean;
  /** Accent / brand colour applied to icon background and area-label text.
   *  Replaces C# `Color` (datasource expression → resolved value). */
  color?: string;
  /** Icon foreground colour rendered inside the coloured icon circle.
   *  Replaces C# `IconColor` (default "#fff"). */
  iconColor?: string;
  /** Primary area label rendered above the title.
   *  Replaces C# `AreaLabel` (datasource expression → text). */
  areaLabel?: string;
  /** Secondary area label rendered after a "/" divider.
   *  Replaces C# `AreaSubLabel`. */
  areaSubLabel?: string;
  /** Main page title.
   *  Replaces C# `Title` (datasource expression → text). */
  title?: string;
  /** Subtitle rendered after a chevron-right divider.
   *  Replaces C# `SubTitle`. */
  subtitle?: string;
  /** Description paragraph rendered below the upper section.
   *  Replaces C# `Description`. */
  description?: string;
  /** CSS class name for the header icon (e.g. a FontAwesome class).
   *  When provided the coloured icon circle is rendered.
   *  Replaces C# `IconClass` (datasource expression → class). */
  iconClass?: string;
  /** Back-button URL. When provided a left-arrow link appears on the left.
   *  Replaces C# `ReturnUrl`. */
  returnUrl?: string;
  /** Whether the page-switch dropdown is enabled.
   *  Replaces C# `ShowPageSwitch`. Default: false. */
  showPageSwitch?: boolean;
  /** Items for the page-switch dropdown. Each item has label, url, isSelected.
   *  Replaces C# `List<PageSwitchItem>` on the TagHelper. */
  pageSwitchItems?: PageSwitchItem[];
  /** When true the header becomes sticky on scroll.
   *  Replaces C# `FixOnScroll` + jQuery script.js behaviour. */
  fixOnScroll?: boolean;
  /** Slot for right-side action buttons (ReactNode).
   *  Replaces `<wv-page-header-actions>` slot. */
  actions?: React.ReactNode;
  /** Slot for auxiliary actions below description (ReactNode).
   *  Replaces `<wv-page-header-actions-aux>` slot. */
  actionsAux?: React.ReactNode;
  /** Slot for toolbar row below description (ReactNode).
   *  Replaces `<wv-page-header-toolbar>` slot. */
  toolbar?: React.ReactNode;
}

/* ------------------------------------------------------------------ */
/*  Inline SVG Icons                                                  */
/*  Replace FontAwesome icon classes used in the monolith's TagHelper  */
/* ------------------------------------------------------------------ */

/** Left-pointing arrow for the back button (replaces fa-arrow-left). */
const ArrowLeftIcon: React.FC<{ className?: string }> = ({ className }) => (
  <svg
    xmlns="http://www.w3.org/2000/svg"
    viewBox="0 0 448 512"
    fill="currentColor"
    className={className}
    aria-hidden="true"
  >
    <path d="M9.4 233.4c-12.5 12.5-12.5 32.8 0 45.3l160 160c12.5 12.5 32.8 12.5 45.3 0s12.5-32.8 0-45.3L109.2 288 416 288c17.7 0 32-14.3 32-32s-14.3-32-32-32l-306.7 0L214.6 118.6c12.5-12.5 12.5-32.8 0-45.3s-32.8-12.5-45.3 0l-160 160z" />
  </svg>
);

/** Vertical ellipsis icon for the page-switch trigger (replaces fa-ellipsis-v). */
const EllipsisVerticalIcon: React.FC<{ className?: string }> = ({ className }) => (
  <svg
    xmlns="http://www.w3.org/2000/svg"
    viewBox="0 0 128 512"
    fill="currentColor"
    className={className}
    aria-hidden="true"
  >
    <path d="M64 360a56 56 0 1 0 0 112 56 56 0 1 0 0-112zm0-160a56 56 0 1 0 0 112 56 56 0 1 0 0-112zM120 96A56 56 0 1 0 8 96a56 56 0 1 0 112 0z" />
  </svg>
);

/** Right-pointing chevron used as divider and selected indicator
 *  (replaces fa-angle-right). */
const ChevronRightIcon: React.FC<{ className?: string }> = ({ className }) => (
  <svg
    xmlns="http://www.w3.org/2000/svg"
    viewBox="0 0 256 512"
    fill="currentColor"
    className={className}
    aria-hidden="true"
  >
    <path d="M246.6 233.4c12.5 12.5 12.5 32.8 0 45.3l-160 160c-12.5-12.5-32.8-12.5-45.3 0s-12.5-32.8 0-45.3L146.7 256 41.4 150.6c-12.5-12.5-12.5-32.8 0-45.3s32.8-12.5 45.3 0l160 160z" />
  </svg>
);

/* ------------------------------------------------------------------ */
/*  Header Component                                                   */
/* ------------------------------------------------------------------ */

/**
 * Page header component — direct React replacement for the monolith's
 * `PcPageHeader` ViewComponent and `<wv-page-header>` TagHelper.
 *
 * Features:
 * - Back-button link (when `returnUrl` is provided)
 * - Coloured icon with configurable background and foreground
 * - Area label + optional sub-label with "/" divider
 * - Title with optional page-switch dropdown (multi-page navigation)
 * - Subtitle with chevron-right divider
 * - Description row with auxiliary actions slot
 * - Toolbar slot
 * - Sticky scroll behaviour (replaces jQuery script.js)
 */
const Header: React.FC<HeaderProps> = ({
  isVisible = true,
  color,
  iconColor = '#fff',
  areaLabel,
  areaSubLabel,
  title,
  subtitle,
  description,
  iconClass,
  returnUrl,
  showPageSwitch = false,
  pageSwitchItems = [],
  fixOnScroll = false,
  actions,
  actionsAux,
  toolbar,
}) => {
  /* ---- Sticky scroll state ---- */
  const headerRef = useRef<HTMLDivElement>(null);
  const [isFixed, setIsFixed] = useState(false);
  const [headerHeight, setHeaderHeight] = useState(0);
  const originalOffsetRef = useRef<number>(0);
  const hasCalculatedOffset = useRef(false);

  /* ---- Page-switch dropdown state ---- */
  const [isDropdownOpen, setIsDropdownOpen] = useState(false);
  const dropdownRef = useRef<HTMLDivElement>(null);

  /* ---------------------------------------------------------------- */
  /*  Sticky scroll handler (replaces jQuery script.js)               */
  /* ---------------------------------------------------------------- */
  const handleScroll = useCallback(() => {
    if (!headerRef.current || !fixOnScroll) return;

    // Capture original offset on first meaningful scroll
    if (!hasCalculatedOffset.current) {
      const rect = headerRef.current.getBoundingClientRect();
      originalOffsetRef.current = rect.top + window.scrollY;
      setHeaderHeight(headerRef.current.offsetHeight);
      hasCalculatedOffset.current = true;
    }

    if (window.scrollY > originalOffsetRef.current) {
      setIsFixed(true);
    } else {
      setIsFixed(false);
    }
  }, [fixOnScroll]);

  useEffect(() => {
    if (!fixOnScroll) return;

    // Calculate initial offset when the component mounts
    if (headerRef.current) {
      const rect = headerRef.current.getBoundingClientRect();
      originalOffsetRef.current = rect.top + window.scrollY;
      setHeaderHeight(headerRef.current.offsetHeight);
      hasCalculatedOffset.current = true;
    }

    window.addEventListener('scroll', handleScroll, { passive: true });
    return () => {
      window.removeEventListener('scroll', handleScroll);
    };
  }, [fixOnScroll, handleScroll]);

  /* ---------------------------------------------------------------- */
  /*  Outside-click handler for page-switch dropdown                  */
  /* ---------------------------------------------------------------- */
  const handleClickOutside = useCallback(
    (event: MouseEvent) => {
      if (
        dropdownRef.current &&
        !dropdownRef.current.contains(event.target as Node)
      ) {
        setIsDropdownOpen(false);
      }
    },
    [],
  );

  useEffect(() => {
    if (isDropdownOpen) {
      document.addEventListener('mousedown', handleClickOutside);
    }
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
    };
  }, [isDropdownOpen, handleClickOutside]);

  /* ---------------------------------------------------------------- */
  /*  Toggle callback for page-switch dropdown                        */
  /* ---------------------------------------------------------------- */
  const toggleDropdown = useCallback(() => {
    setIsDropdownOpen((prev) => !prev);
  }, []);

  /* ---- Visibility gate (mirrors output.SuppressOutput()) ---- */
  if (!isVisible) {
    return null;
  }

  /* ---- Determine whether the page-switch dropdown is applicable ---- */
  const hasPageSwitch =
    showPageSwitch && Array.isArray(pageSwitchItems) && pageSwitchItems.length > 1;

  /* ---- Determine whether the description row should render ---- */
  const hasDescription = Boolean(description) || Boolean(actionsAux);

  /* ---- Determine whether toolbar should render ---- */
  const hasToolbar = Boolean(toolbar);

  /* ---- Determine whether right-side actions exist ---- */
  const hasActions = Boolean(actions);

  /* ---- Determine icon presence ---- */
  const hasIcon = Boolean(iconClass);

  /* ---- Determine back button presence ---- */
  const hasBackButton = Boolean(returnUrl);

  /* ---------------------------------------------------------------- */
  /*  Build dynamic root className                                     */
  /*                                                                   */
  /*  Mirrors the monolith's CSS class list:                           */
  /*    pc-page-header [has-toolbar] [has-icon / no-icon] [has-btn-back] */
  /* ---------------------------------------------------------------- */
  const rootClasses = [
    // Base styling — replaces .pc-page-header
    'w-full',
    'bg-white',
    'border-b',
    'border-gray-200',
    // Conditional structural markers (data-attributes used for potential external styling)
    hasToolbar ? 'has-toolbar' : '',
    hasIcon ? 'has-icon' : 'no-icon',
    hasBackButton ? 'has-btn-back' : '',
    // Fixed positioning when sticky-scrolled
    isFixed ? 'fixed top-0 inset-inline-start-0 inset-inline-end-0 z-50 shadow-md' : '',
  ]
    .filter(Boolean)
    .join(' ');

  /* ---------------------------------------------------------------- */
  /*  Render helpers                                                   */
  /* ---------------------------------------------------------------- */

  /** Renders the back-button link (left actions). */
  const renderBackButton = () => {
    if (!hasBackButton) return null;
    return (
      <div className="flex items-center pe-3">
        <Link
          to={returnUrl as string}
          className="inline-flex items-center justify-center px-2 py-1 text-sm border border-gray-400 rounded text-gray-600 hover:bg-gray-100 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500 transition-colors"
          aria-label="Go back"
        >
          <ArrowLeftIcon className="w-3.5 h-3.5" />
        </Link>
      </div>
    );
  };

  /** Renders the coloured meta icon. */
  const renderIcon = () => {
    if (!hasIcon) return null;
    return (
      <div
        className="flex-none flex items-center justify-center w-10 h-10 rounded me-3"
        style={{ backgroundColor: color || 'transparent' }}
      >
        <span
          className={iconClass}
          style={{ color: iconColor }}
          aria-hidden="true"
        />
      </div>
    );
  };

  /** Renders the area label row (label + optional sub-label). */
  const renderAreaLabel = () => {
    if (!areaLabel) return null;
    return (
      <div
        className="text-xs font-medium uppercase tracking-wide mb-0.5"
        style={{ color: color || undefined }}
      >
        <span>{areaLabel}</span>
        {areaSubLabel && (
          <>
            <span className="mx-1 opacity-60">/</span>
            <span>{areaSubLabel}</span>
          </>
        )}
      </div>
    );
  };

  /** Renders a single page-switch dropdown item. */
  const renderPageSwitchItem = (item: PageSwitchItem, index: number) => (
    <Link
      key={`${item.url}-${index}`}
      to={item.url}
      className="flex items-center gap-2 px-2 py-1.5 text-sm text-gray-700 hover:bg-gray-100 rounded"
      onClick={() => setIsDropdownOpen(false)}
    >
      {item.isSelected && (
        <ChevronRightIcon className="w-3 h-3 text-gray-500 flex-none" />
      )}
      {!item.isSelected && <span className="w-3 flex-none" />}
      <span className={item.isSelected ? 'font-semibold' : ''}>{item.label}</span>
    </Link>
  );

  /** Renders the title section — either with page-switch dropdown or plain text. */
  const renderTitle = () => {
    if (!title && !hasPageSwitch) return null;

    return (
      <div className="flex items-center flex-wrap gap-x-1">
        {hasPageSwitch ? (
          <div className="relative inline-flex items-center" ref={dropdownRef}>
            {/* Dropdown trigger: ellipsis icon + title text */}
            <button
              type="button"
              onClick={toggleDropdown}
              className="inline-flex items-center gap-1.5 text-lg font-semibold text-gray-900 hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500 cursor-pointer"
              aria-expanded={isDropdownOpen}
              aria-haspopup="listbox"
              aria-label={`Switch page: ${title || ''}`}
            >
              <EllipsisVerticalIcon className="w-4 h-4 text-gray-500" />
              <span>{title || ''}</span>
            </button>

            {/* Dropdown menu */}
            {isDropdownOpen && (
              <div
                className="absolute top-full mt-1 inset-inline-start-0 min-w-[12rem] bg-white border border-gray-200 rounded-md shadow-lg z-50 py-1"
                role="listbox"
                aria-label="Page switch"
              >
                {pageSwitchItems.map((item, idx) => renderPageSwitchItem(item, idx))}
              </div>
            )}
          </div>
        ) : (
          <span className="text-lg font-semibold text-gray-900">
            {title || ''}
          </span>
        )}

        {/* Subtitle with chevron divider */}
        {subtitle && (
          <>
            <ChevronRightIcon className="w-3.5 h-3.5 text-gray-400 mx-1 self-center" />
            <span className="text-lg font-normal text-gray-500">{subtitle}</span>
          </>
        )}
      </div>
    );
  };

  /* ---------------------------------------------------------------- */
  /*  Component JSX                                                    */
  /* ---------------------------------------------------------------- */
  return (
    <>
      {/* Placeholder div to prevent layout jump when header is fixed */}
      {isFixed && fixOnScroll && (
        <div style={{ height: headerHeight }} aria-hidden="true" />
      )}

      <div ref={headerRef} className={rootClasses}>
        {/* ======== Upper section ======== */}
        <div className="flex items-start px-4 py-3">
          {/* Left: back button */}
          {renderBackButton()}

          {/* Meta: icon + title block */}
          <div className="flex items-start flex-1 min-w-0">
            {renderIcon()}

            <div className="min-w-0 flex-1">
              {renderAreaLabel()}
              {renderTitle()}
            </div>
          </div>

          {/* Right: action buttons slot */}
          {hasActions && (
            <div className="flex items-center gap-2 ps-4 flex-none">
              {actions}
            </div>
          )}
        </div>

        {/* ======== Description + aux actions row ======== */}
        {hasDescription && (
          <div className="flex flex-wrap items-center px-4 pb-3 gap-4">
            {description && (
              <div className="flex-1 min-w-0 text-sm text-gray-600">
                {description}
              </div>
            )}
            {actionsAux && (
              <div className="flex-none self-center">{actionsAux}</div>
            )}
          </div>
        )}

        {/* ======== Toolbar row ======== */}
        {hasToolbar && (
          <div className="border-t border-gray-100 px-4 py-2">{toolbar}</div>
        )}
      </div>
    </>
  );
};

export default Header;
