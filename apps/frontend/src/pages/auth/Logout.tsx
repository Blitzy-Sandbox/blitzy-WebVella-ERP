/**
 * Logout Page Component — `apps/frontend/src/pages/auth/Logout.tsx`
 *
 * Replaces the monolith's `WebVella.Erp.Web/Pages/logout.cshtml` and
 * `logout.cshtml.cs` (`LogoutModel`).
 *
 * Monolith behaviour replicated:
 *  1. `LogoutModel.OnGet` / `OnPost` call `authService.Logout()` which
 *     invokes `HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)`
 *     to invalidate the cookie-based session.
 *     → Replaced by calling `logout()` from `api/auth.ts` which sends a
 *       Cognito `GlobalSignOutCommand` and wipes in-memory tokens.
 *
 *  2. Both handlers then execute `IPageHook` / `ILogoutPageHook` hooks.
 *     → Hooks are replaced by domain events (SNS/SQS) in the target
 *       architecture; no frontend hook execution is needed.
 *
 *  3. Both handlers return `new LocalRedirectResult("/")`.
 *     → Replaced by `navigate('/', { replace: true })` via React Router.
 *
 *  4. The view template `logout.cshtml` renders NO visual content.
 *     → This component displays a minimal "Logging out…" indicator
 *       during the brief async operation, then redirects.
 *
 * Design decisions:
 *  - `useEffect` on mount performs the logout — combines both GET and POST
 *    semantics from the monolith (both did the same thing).
 *  - `replace: true` prevents the user from pressing browser-back and
 *    landing on the logout page again.
 *  - Errors from Cognito `GlobalSignOutCommand` are caught gracefully:
 *    local state is always cleared and the user is redirected to `/`.
 *    This mirrors the monolith's silent-fail pattern where logout always
 *    succeeds from the user's perspective.
 *  - No TanStack Query mutation: logout is a fire-and-forget action that
 *    always succeeds locally regardless of server response.
 *  - Public route — no `ProtectedRoute` wrapper needed (analogous to the
 *    monolith where logout is accessible even with a stale session).
 *  - Default export for `React.lazy()` compatibility in `router.tsx`.
 *
 * Tailwind CSS replaces Bootstrap 4 from `_SystemMaster.cshtml`.
 *
 * @module pages/auth/Logout
 */

import React, { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { logout } from '../../api/auth';
import { useAuthStore } from '../../stores/authStore';

/**
 * Logout page component.
 *
 * On mount, performs the Cognito sign-out flow, clears the client-side
 * auth store, and programmatically redirects to the home page (`/`).
 *
 * Renders a minimal centered "Logging out…" message during the brief
 * async operation. If an unexpected error occurs and navigation somehow
 * fails, an error message is shown with a manual redirect hint.
 */
export default function Logout(): React.JSX.Element {
  const navigate = useNavigate();
  const logoutSuccess = useAuthStore((state) => state.logoutSuccess);
  const [isLoggingOut, setIsLoggingOut] = useState<boolean>(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    /**
     * Asynchronous logout sequence replicating `LogoutModel.OnGet`:
     *  1. Call Cognito GlobalSignOutCommand + clear in-memory tokens
     *  2. Reset Zustand auth store (isAuthenticated, currentUser, etc.)
     *  3. Redirect to "/" with history replacement
     *
     * The catch block ensures local state is ALWAYS cleared even if the
     * Cognito call fails (e.g. network error, already-revoked token).
     * This matches the monolith where `AuthService.Logout()` is called
     * without error propagation — the user is always sent to "/".
     */
    let isMounted = true;

    const performLogout = async (): Promise<void> => {
      try {
        // Step 1: Invalidate Cognito tokens server-side
        // Replaces: authService.Logout() → HttpContext.SignOutAsync(Cookie)
        await logout();

        // Step 2: Clear client-side auth state
        // Replaces: ErpMiddleware clearing SecurityContext on next request
        logoutSuccess();

        // Step 3: Navigate to home page
        // Replaces: return new LocalRedirectResult("/")
        navigate('/', { replace: true });
      } catch {
        // Even if Cognito sign-out fails (network, revoked token, etc.),
        // always clear local state and redirect. The monolith's logout
        // handler does not surface errors — it always redirects to "/".
        logoutSuccess();

        try {
          navigate('/', { replace: true });
        } catch {
          // If navigation itself fails (extremely unlikely), show error UI
          // so the user is not stuck on a blank page.
          if (isMounted) {
            setError('Logout completed. Please navigate to the home page.');
          }
        }
      } finally {
        if (isMounted) {
          setIsLoggingOut(false);
        }
      }
    };

    performLogout();

    // Cleanup flag to prevent state updates on unmounted component
    return () => {
      isMounted = false;
    };
  }, [navigate, logoutSuccess]);

  // Error state — shown only if navigation itself fails (edge case).
  // Provides a manual link so the user is never stuck.
  if (error) {
    return (
      <main
        className="flex min-h-screen items-center justify-center bg-gray-100"
        role="status"
        aria-live="polite"
      >
        <div className="text-center">
          <p className="text-red-600">{error}</p>
          <p className="mt-2 text-sm text-gray-500">
            <a href="/" className="underline hover:text-gray-700">
              Go to home page
            </a>
          </p>
        </div>
      </main>
    );
  }

  // Default: minimal loading indicator while the async logout runs.
  // The original logout.cshtml rendered NO content; this is a minor UX
  // improvement for the brief moment before the redirect fires.
  if (isLoggingOut) {
    return (
      <main
        className="flex min-h-screen items-center justify-center bg-gray-100"
        role="status"
        aria-live="polite"
        aria-label="Logging out"
      >
        <div className="text-center">
          <p className="text-gray-600">Logging out…</p>
        </div>
      </main>
    );
  }

  // Post-logout: should not normally render (navigation fires first),
  // but provides a fallback to avoid a blank screen.
  return (
    <main
      className="flex min-h-screen items-center justify-center bg-gray-100"
      role="status"
      aria-live="polite"
    >
      <div className="text-center">
        <p className="text-gray-500">Redirecting…</p>
      </div>
    </main>
  );
}
