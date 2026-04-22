/**
 * UserMenu Component — `apps/frontend/src/components/layout/UserMenu.tsx`
 *
 * Combined user dropdown menu component replacing both the monolith's
 * `UserMenu/` ViewComponent (custom menu items from pageModel.UserMenu)
 * and `UserNav/` ViewComponent (admin settings cog, logout action, user
 * avatar display).
 *
 * Renders:
 *   1. User avatar trigger (Cognito profile picture or initials fallback)
 *   2. Dropdown panel with:
 *      - User info header (name, email, role badge)
 *      - Custom menu items from appStore.userMenu (MenuItem[])
 *      - Admin "Manage Page" link (visible only when user isAdmin AND
 *        current page has an id)
 *      - Logout action (Cognito signout → clear auth state → navigate)
 *
 * Replaces:
 *   - UserMenu.cs / UserMenu.cshtml — iterates `pageModel.UserMenu`
 *   - UserNavViewComponent.cs / UserNav.Default.cshtml — admin cog,
 *     logout link, avatar display with `/fs` prefix normalization
 *   - jQuery `data-navclick-handler` dropdown behaviour from Nav/script.js
 *
 * Zero jQuery, zero Bootstrap — all behaviour via React state, all styling
 * via Tailwind CSS utility classes.
 */

import { useState, useRef, useEffect, useCallback } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useAuthStore } from '../../stores/authStore';
import { logout } from '../../api/auth';
import { useAppStore } from '../../stores/appStore';
import type { MenuItem } from '../../types/app';

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

/** Optional props for the UserMenu component. */
interface UserMenuProps {
  /** Additional CSS class names to apply to the root wrapper. */
  className?: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Derive up to two initials from the user's first and last name.
 * Falls back to the first letter of the email when names are empty.
 *
 * Replaces the monolith's default `avatar.png` fallback with a
 * dynamically generated initials badge.
 */
function getInitials(
  firstName: string,
  lastName: string,
  email: string,
): string {
  const first = firstName.trim();
  const last = lastName.trim();

  if (first && last) {
    return `${first.charAt(0)}${last.charAt(0)}`.toUpperCase();
  }
  if (first) {
    return first.charAt(0).toUpperCase();
  }
  if (last) {
    return last.charAt(0).toUpperCase();
  }
  /* Fall back to email initial */
  return email ? email.charAt(0).toUpperCase() : '?';
}

/**
 * Normalise an avatar image URL.
 *
 * Mirrors the monolith's avatar URL logic from the commented-out section
 * in `UserNav.Default.cshtml`:
 *   - Empty / whitespace string → return `null` (caller shows initials)
 *   - Starts with `http` or `/fs` → use as-is
 *   - Otherwise → prefix with `/fs`
 */
function normalizeAvatarUrl(image: string): string | null {
  const trimmed = image.trim();
  if (!trimmed) {
    return null;
  }
  if (trimmed.startsWith('http') || trimmed.startsWith('/fs')) {
    return trimmed;
  }
  return `/fs${trimmed.startsWith('/') ? '' : '/'}${trimmed}`;
}

/**
 * Build a display name from user attributes.
 * Prefers "FirstName LastName"; falls back to email.
 */
function getDisplayName(
  firstName: string,
  lastName: string,
  email: string,
): string {
  const parts = [firstName.trim(), lastName.trim()].filter(Boolean);
  return parts.length > 0 ? parts.join(' ') : email;
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

/**
 * Renders a single custom menu item from the appStore's userMenu array.
 *
 * Replaces the `<partial name="NavMenu" for="@menuItem"/>` rendering loop
 * from UserMenu.cshtml. Supports both HTML content items (MenuItem.isHtml)
 * and plain-text link items. Recursively renders child nodes.
 */
function UserMenuItemRenderer({ item }: { item: MenuItem }) {
  /* If the item contains raw HTML content, render it directly */
  if (item.isHtml && item.content) {
    return (
      <li role="none">
        <div
          className={`block px-4 py-2 text-sm text-gray-700 ${item.class ?? ''}`}
          /* Matches monolith's Html.Raw(menuItem.Content) pattern */
          dangerouslySetInnerHTML={{ __html: item.content }}
        />
        {/* Render child nodes recursively */}
        {item.nodes && item.nodes.length > 0 && (
          <ul role="menu" className="pl-2">
            {[...item.nodes]
              .sort((a, b) => a.sortOrder - b.sortOrder)
              .map((child) => (
                <UserMenuItemRenderer key={child.id} item={child} />
              ))}
          </ul>
        )}
      </li>
    );
  }

  /* Plain-text item rendered as a navigation link */
  const href = item.content || '#';
  const isExternal = href.startsWith('http');

  return (
    <li role="none">
      {isExternal ? (
        <a
          href={href}
          target="_blank"
          rel="noopener noreferrer"
          role="menuitem"
          className={`block px-4 py-2 text-sm text-gray-700 hover:bg-gray-100
            focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
            ${item.class ?? ''}`}
        >
          {item.content}
        </a>
      ) : (
        <Link
          to={href}
          role="menuitem"
          className={`block px-4 py-2 text-sm text-gray-700 hover:bg-gray-100
            focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
            ${item.class ?? ''}`}
        >
          {item.content}
        </Link>
      )}

      {/* Render child nodes recursively */}
      {item.nodes && item.nodes.length > 0 && (
        <ul role="menu" className="pl-2">
          {[...item.nodes]
            .sort((a, b) => a.sortOrder - b.sortOrder)
            .map((child) => (
              <UserMenuItemRenderer key={child.id} item={child} />
            ))}
        </ul>
      )}
    </li>
  );
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * UserMenu — dropdown menu component for the top navigation bar.
 *
 * Combines:
 *  - User avatar/trigger (from commented-out code in UserNav.Default.cshtml)
 *  - Custom menu items (from UserMenu.cshtml iteration)
 *  - Admin settings link (from UserNav.Default.cshtml conditional block)
 *  - Logout action (from UserNav.Default.cshtml logout link)
 */
function UserMenu({ className }: UserMenuProps) {
  // ── Store hooks ──────────────────────────────────────────────────────────

  const currentUser = useAuthStore((s) => s.currentUser);
  const logoutSuccess = useAuthStore((s) => s.logoutSuccess);

  const userMenu = useAppStore((s) => s.userMenu);
  const currentPage = useAppStore((s) => s.currentPage);

  // ── Router ───────────────────────────────────────────────────────────────

  const navigate = useNavigate();

  // ── State ────────────────────────────────────────────────────────────────

  const [isDropdownOpen, setIsDropdownOpen] = useState<boolean>(false);
  const [isLoggingOut, setIsLoggingOut] = useState<boolean>(false);

  // ── Refs ─────────────────────────────────────────────────────────────────

  const dropdownRef = useRef<HTMLDivElement>(null);

  // ── Derived values ───────────────────────────────────────────────────────

  const avatarUrl = currentUser
    ? normalizeAvatarUrl(currentUser.image)
    : null;

  const displayName = currentUser
    ? getDisplayName(currentUser.firstName, currentUser.lastName, currentUser.email)
    : '';

  const initials = currentUser
    ? getInitials(currentUser.firstName, currentUser.lastName, currentUser.email)
    : '?';

  /**
   * Admin settings section is visible only when:
   *  1. The current user is an administrator
   *  2. There is an active page with a valid id
   *
   * Replaces `(ViewBag.PageId != null && erpUser != null && erpUser.IsAdmin)`
   * from UserNav.Default.cshtml.
   */
  const showAdminSettings =
    currentUser?.isAdmin === true && currentPage?.id != null;

  /**
   * Admin "Manage Page" URL. Replaces the monolith's
   * `/sdk/objects/page/r/{PageId}/generated-body` with the new React admin route.
   */
  const managePageUrl = currentPage?.id
    ? `/admin/pages/${currentPage.id}/edit`
    : '#';

  /**
   * Primary role display string.
   * Shows the first role, or "User" as default.
   */
  const primaryRole = currentUser?.roles?.[0] ?? 'user';
  const roleLabel = primaryRole.charAt(0).toUpperCase() + primaryRole.slice(1);

  // Sorted menu items by sortOrder
  const sortedMenuItems = [...userMenu].sort(
    (a, b) => a.sortOrder - b.sortOrder,
  );

  // ── Callbacks ────────────────────────────────────────────────────────────

  /**
   * Toggle the dropdown open/closed.
   * Replaces the monolith's jQuery `data-navclick-handler` toggle logic
   * from Nav/script.js.
   */
  const toggleDropdown = useCallback(() => {
    setIsDropdownOpen((prev) => !prev);
  }, []);

  /**
   * Close the dropdown (used by escape key and outside-click handlers).
   */
  const closeDropdown = useCallback(() => {
    setIsDropdownOpen(false);
  }, []);

  /**
   * Handle the logout action.
   *
   * 1. Call `logout()` from `api/auth.ts` — invalidates Cognito tokens
   * 2. Call `logoutSuccess()` from `authStore` — clears client auth state
   * 3. Navigate to `/login`
   *
   * Replaces the monolith's `<a href="/logout">` which triggered
   * `AuthService.Logout()` cookie sign-out.
   */
  const handleLogout = useCallback(async () => {
    if (isLoggingOut) {
      return; /* Prevent double-clicks */
    }

    setIsLoggingOut(true);
    closeDropdown();

    try {
      await logout();
    } catch {
      /* Cognito signout failure is non-blocking — clear local state anyway */
    }

    logoutSuccess();
    navigate('/login', { replace: true });
  }, [isLoggingOut, closeDropdown, logoutSuccess, navigate]);

  // ── Effects ──────────────────────────────────────────────────────────────

  /**
   * Outside-click detection — closes the dropdown when the user clicks
   * anywhere outside the dropdown container.
   *
   * Replaces the jQuery `document.addEventListener("click", ...)` from
   * Nav/script.js that cleared all `.menu-nav-wrapper` active states.
   */
  useEffect(() => {
    if (!isDropdownOpen) {
      return;
    }

    function handleClickOutside(event: MouseEvent) {
      if (
        dropdownRef.current &&
        !dropdownRef.current.contains(event.target as Node)
      ) {
        closeDropdown();
      }
    }

    function handleEscapeKey(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        closeDropdown();
      }
    }

    document.addEventListener('mousedown', handleClickOutside);
    document.addEventListener('keydown', handleEscapeKey);

    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
      document.removeEventListener('keydown', handleEscapeKey);
    };
  }, [isDropdownOpen, closeDropdown]);

  // ── Guard: no user ───────────────────────────────────────────────────────

  if (!currentUser) {
    return null;
  }

  // ── Render ───────────────────────────────────────────────────────────────

  return (
    <div
      ref={dropdownRef}
      className={`relative flex items-center ${className ?? ''}`}
    >
      {/* ── Admin settings icon (cog) ─────────────────────────────────── */}
      {showAdminSettings && (
        <a
          href={managePageUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="inline-flex items-center justify-center px-2 py-2
            text-gray-400 hover:text-gray-600
            focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
            rounded"
          title="Manage Page"
          aria-label="Manage Page"
        >
          {/* Cog icon — replaces monolith's <span class="fa fa-cog icon"> */}
          <svg
            className="h-5 w-5"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            strokeWidth={1.5}
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M9.594 3.94c.09-.542.56-.94 1.11-.94h2.593c.55 0 1.02.398
                 1.11.94l.213 1.281c.063.374.313.686.645.87.074.04.147.083
                 .22.127.325.196.72.257 1.075.124l1.217-.456a1.125 1.125 0
                 0 1 1.37.49l1.296 2.247a1.125 1.125 0 0
                 1-.26 1.431l-1.003.827c-.293.241-.438.613-.43.992a7.723
                 7.723 0 0 1 0 .255c-.008.378.137.75.43.991l1.004.827c.424
                 .35.534.955.26 1.43l-1.298 2.247a1.125 1.125 0 0
                 1-1.369.491l-1.217-.456c-.355-.133-.75-.072-1.076.124a6.47
                 6.47 0 0 1-.22.128c-.331.183-.581.495-.644.869l-.213
                 1.281c-.09.543-.56.941-1.11.941h-2.594c-.55
                 0-1.019-.398-1.11-.94l-.213-1.281c-.062-.374-.312-.686-.644-.87a6.52
                 6.52 0 0 1-.22-.127c-.325-.196-.72-.257-1.076-.124l-1.217.456a1.125
                 1.125 0 0 1-1.369-.49l-1.297-2.247a1.125 1.125 0 0
                 1 .26-1.431l1.004-.827c.292-.24.437-.613.43-.991a6.932 6.932 0 0
                 1 0-.255c.007-.38-.138-.751-.43-.992l-1.004-.827a1.125 1.125 0 0
                 1-.26-1.43l1.297-2.247a1.125 1.125 0 0
                 1 1.37-.491l1.216.456c.356.133.751.072 1.076-.124.072-.044.146-.086.22-.128.332-.183.582-.495.644-.869l.214-1.28Z"
            />
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z"
            />
          </svg>
        </a>
      )}

      {/* ── Avatar trigger button ─────────────────────────────────────── */}
      <button
        type="button"
        onClick={toggleDropdown}
        className="flex items-center gap-2 rounded-full px-1 py-1
          hover:bg-gray-100
          focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        aria-expanded={isDropdownOpen}
        aria-haspopup="true"
        aria-label={`User menu for ${displayName}`}
      >
        {avatarUrl ? (
          <img
            src={avatarUrl}
            alt=""
            width={32}
            height={32}
            className="h-8 w-8 rounded-full object-cover"
            style={{ backgroundColor: '#d1d5db' }}
          />
        ) : (
          <span
            className="flex h-8 w-8 items-center justify-center rounded-full
              bg-gray-500 text-sm font-medium text-white"
            aria-hidden="true"
          >
            {initials}
          </span>
        )}

        {/* Chevron indicator */}
        <svg
          className={`h-4 w-4 text-gray-500 transition-transform duration-150 ${
            isDropdownOpen ? 'rotate-180' : ''
          }`}
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          strokeWidth={2}
          aria-hidden="true"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            d="M19.5 8.25l-7.5 7.5-7.5-7.5"
          />
        </svg>
      </button>

      {/* ── Dropdown panel ────────────────────────────────────────────── */}
      {isDropdownOpen && (
        <div
          className="absolute right-0 top-full z-50 mt-1 w-56
            rounded-md bg-white shadow-lg ring-1 ring-black/5"
          role="menu"
          aria-orientation="vertical"
          aria-label="User menu"
        >
          {/* ── User info header ────────────────────────────────────── */}
          <div className="border-b border-gray-200 px-4 py-3">
            <p className="truncate text-sm font-medium text-gray-900">
              {displayName}
            </p>
            <p className="truncate text-xs text-gray-500">
              {currentUser.email}
            </p>
            <span
              className="mt-1 inline-block rounded-full bg-gray-100 px-2 py-0.5
                text-xs font-medium text-gray-600"
            >
              {roleLabel}
            </span>
          </div>

          {/* ── Custom menu items (from appStore.userMenu) ──────────── */}
          {sortedMenuItems.length > 0 && (
            <>
              <ul role="menu" className="py-1">
                {sortedMenuItems.map((item) => (
                  <UserMenuItemRenderer key={item.id} item={item} />
                ))}
              </ul>
              <div className="border-t border-gray-200 my-0" role="separator" />
            </>
          )}

          {/* ── Admin: Manage Page link ─────────────────────────────── */}
          {showAdminSettings && (
            <>
              <div className="py-1">
                <a
                  href={managePageUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  role="menuitem"
                  className="flex items-center gap-2 px-4 py-2 text-sm text-gray-700
                    hover:bg-gray-100
                    focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
                >
                  {/* Page/file icon — replaces <span class="fas fa-file icon fa-fw"> */}
                  <svg
                    className="h-4 w-4 text-gray-400"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                    strokeWidth={1.5}
                    aria-hidden="true"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      d="M19.5 14.25v-2.625a3.375 3.375 0 0
                         0-3.375-3.375h-1.5A1.125 1.125 0 0
                         1 13.5 7.125v-1.5a3.375 3.375 0 0
                         0-3.375-3.375H8.25m2.25 0H5.625c-.621
                         0-1.125.504-1.125 1.125v17.25c0 .621.504
                         1.125 1.125 1.125h12.75c.621 0
                         1.125-.504 1.125-1.125V11.25a9 9 0 0
                         0-9-9Z"
                    />
                  </svg>
                  Manage Page
                </a>
              </div>
              <div className="border-t border-gray-200 my-0" role="separator" />
            </>
          )}

          {/* ── Logout action ───────────────────────────────────────── */}
          <div className="py-1">
            <button
              type="button"
              role="menuitem"
              onClick={handleLogout}
              disabled={isLoggingOut}
              className="flex w-full items-center gap-2 px-4 py-2 text-sm
                text-red-600 hover:bg-red-50
                focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500
                disabled:cursor-not-allowed disabled:opacity-50"
            >
              {/* Sign-out icon — replaces <span class="fas fa-sign-out-alt"> */}
              <svg
                className="h-4 w-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                strokeWidth={1.5}
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M15.75 9V5.25A2.25 2.25 0 0 0 13.5 3h-6a2.25
                     2.25 0 0 0-2.25 2.25v13.5A2.25 2.25 0 0 0 7.5
                     21h6a2.25 2.25 0 0 0 2.25-2.25V15m3 0 3-3m0
                     0-3-3m3 3H9"
                />
              </svg>
              {isLoggingOut ? 'Signing out…' : 'Sign out'}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

export default UserMenu;
