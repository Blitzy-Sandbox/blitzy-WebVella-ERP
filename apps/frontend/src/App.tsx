/**
 * apps/frontend/src/App.tsx
 *
 * Root application component for the WebVella ERP React SPA.
 *
 * Replaces the monolith's layout and bootstrapping layers:
 * - `_AppMaster.cshtml`  — main layout chrome (nav + sidebar + content area)
 * - `_SystemMaster.cshtml` — minimal system layout
 * - `Startup.cs` middleware pipeline (auth, error handling, routing)
 *
 * Responsibility chain:
 *   ErrorBoundary  → graceful error handling (replaces ErrorHandlingMiddleware)
 *     BrowserRouter  → HTML5 History routing (replaces MapRazorPages / MapControllerRoute)
 *       AuthProvider → Cognito session management (replaces cookie + JWT auth)
 *         AppRouter  → declarative route definitions (replaces Razor Page @page directives)
 *
 * This is a pure static SPA — no server-side rendering, no server components.
 */

import { useEffect, useCallback, Component, type ReactNode } from 'react';
import { BrowserRouter } from 'react-router-dom';
import { AppRouter } from './router';
import { useAuthStore, type AuthUser } from './stores/authStore';
import * as auth from './api/auth';

/* ═══════════════════════════════════════════════════════════════
   ErrorBoundary — Class Component (React 19 requires class for error boundaries)
   Replaces: ErpErrorHandlingMiddleware + UseDeveloperExceptionPage from Startup.cs
   ═══════════════════════════════════════════════════════════════ */

interface ErrorBoundaryProps {
  /** Child component tree wrapped by the boundary. */
  children: ReactNode;
}

interface ErrorBoundaryState {
  /** Whether an unrecoverable rendering error has been caught. */
  hasError: boolean;
  /** The caught error instance, if any. */
  error: Error | null;
}

/**
 * Application-level error boundary that catches uncaught rendering errors
 * and displays a user-friendly fallback UI instead of a blank screen.
 *
 * In the monolith, `ErpErrorHandlingMiddleware` caught exceptions globally
 * and returned an error page. This component serves the same purpose for
 * the React component tree.
 */
class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  constructor(props: ErrorBoundaryProps) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  /**
   * Derive error state when a child component throws during rendering.
   * Called during the "render" phase — must be pure (no side effects).
   */
  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { hasError: true, error };
  }

  /**
   * Log the error for observability after catching.
   * In production, errors should be forwarded to CloudWatch via a
   * structured logging endpoint; console output is the local fallback.
   */
  componentDidCatch(error: Error, errorInfo: React.ErrorInfo): void {
    console.error(
      '[ErrorBoundary] Application error:',
      JSON.stringify({
        message: error.message,
        stack: error.stack,
        componentStack: errorInfo.componentStack,
        timestamp: new Date().toISOString(),
      })
    );
  }

  /**
   * Resets the error state and navigates the user back to the home page.
   * Uses window.location to perform a full page reload, ensuring the
   * entire component tree is re-initialised from a clean slate.
   */
  private handleReset = (): void => {
    this.setState({ hasError: false, error: null });
    window.location.href = '/';
  };

  render(): ReactNode {
    if (this.state.hasError) {
      return (
        <main
          role="alert"
          aria-live="assertive"
          className="flex min-h-screen items-center justify-center bg-gray-50 p-4"
        >
          <div className="max-w-md text-center">
            <svg
              className="mx-auto mb-6 h-16 w-16 text-red-500"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4.5c-.77-.833-2.694-.833-3.464 0L3.34 16.5c-.77.833.192 2.5 1.732 2.5z"
              />
            </svg>
            <h1 className="mb-4 text-2xl font-bold text-gray-900">
              Something went wrong
            </h1>
            <p className="mb-6 text-gray-600">
              {this.state.error?.message ??
                'An unexpected error occurred. Please try again.'}
            </p>
            <button
              type="button"
              onClick={this.handleReset}
              className="rounded-md bg-blue-600 px-6 py-2.5 text-sm font-semibold text-white shadow-sm transition-colors duration-200 hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              Return to Home
            </button>
          </div>
        </main>
      );
    }

    return this.props.children;
  }
}

/* ═══════════════════════════════════════════════════════════════
   AuthProvider — Cognito Session Initialization
   Replaces: Startup.cs AddAuthentication().AddCookie().AddJwtBearer() chain,
             ErpMiddleware per-request security scope binding,
             JwtMiddleware bearer token validation
   ═══════════════════════════════════════════════════════════════ */

/**
 * Default Cognito access-token lifetime in milliseconds (1 hour).
 * Cognito issues access tokens with a configurable but typically 60-minute
 * expiry. This constant is the fallback when the exact `exp` claim is
 * unavailable from the public API surface of the auth module.
 */
const DEFAULT_TOKEN_LIFETIME_MS = 3600 * 1000;

/**
 * AuthProvider restores the user's Cognito session on initial mount.
 *
 * Workflow:
 * 1. The Zustand `authStore` initialises with `isLoading: true` to prevent
 *    an unauthenticated flash (login page) while the check runs.
 * 2. `auth.refreshAccessToken()` attempts to obtain a fresh access token
 *    using a stored refresh token (Cognito `InitiateAuth` REFRESH_TOKEN flow).
 * 3. On success, `auth.getCurrentUser()` extracts the user profile from
 *    the cached Cognito `GetUser` response.
 * 4. The `CognitoUser` is mapped to `AuthUser` and pushed into the store
 *    via `loginSuccess`, which also sets `isLoading: false`.
 * 5. On failure (expired refresh token, network error, etc.), the store
 *    is reset via `logoutSuccess` and the user must re-authenticate.
 */
function AuthProvider({
  children,
}: {
  children: ReactNode;
}): React.JSX.Element {
  const loginSuccess = useAuthStore((state) => state.loginSuccess);
  const logoutSuccess = useAuthStore((state) => state.logoutSuccess);
  const setLoading = useAuthStore((state) => state.setLoading);

  const initializeAuth = useCallback(async (): Promise<void> => {
    try {
      /* Explicitly signal loading in case a hot-reload or remount
         occurs after the store was already settled. */
      setLoading(true);

      /* Attempt to restore the Cognito session using the refresh token.
         Returns the new access-token string, or null if no valid session. */
      const accessToken = await auth.refreshAccessToken();

      if (accessToken) {
        /* Extract user profile from the cached Cognito token claims. */
        const cognitoUser = auth.getCurrentUser();

        if (cognitoUser) {
          /* Map the Cognito-native user shape to the application-wide
             AuthUser interface consumed by the Zustand store.
             Fields not available from Cognito (firstName, lastName, image)
             are initialised with safe defaults — they can be populated
             later via a user-profile API call if needed. */
          const user: AuthUser = {
            id: cognitoUser.id,
            email: cognitoUser.email,
            firstName: '',
            lastName: '',
            image: '',
            roles: cognitoUser.roles,
            isAdmin: cognitoUser.roles.includes('administrator'),
          };

          const tokenExpiresAt = Date.now() + DEFAULT_TOKEN_LIFETIME_MS;
          loginSuccess(user, tokenExpiresAt);
          return;
        }
      }

      /* No valid session could be restored — reset to unauthenticated. */
      logoutSuccess();
    } catch (error: unknown) {
      /* Session restoration failed — the user must re-authenticate.
         Log a warning for observability but do not surface it to the UI
         because this is a normal flow (e.g., expired refresh token). */
      const message =
        error instanceof Error ? error.message : 'Unknown auth error';
      console.warn('[AuthProvider] Session restoration failed:', message);
      logoutSuccess();
    }
  }, [loginSuccess, logoutSuccess, setLoading]);

  useEffect(() => {
    initializeAuth();
  }, [initializeAuth]);

  return <>{children}</>;
}

/* ═══════════════════════════════════════════════════════════════
   App — Root Component
   Rendered by main.tsx inside React.StrictMode + QueryClientProvider.
   ═══════════════════════════════════════════════════════════════ */

/**
 * Root application component that assembles the provider stack:
 *
 * ```
 * ErrorBoundary          – catches uncaught rendering errors
 *   └─ BrowserRouter     – HTML5 History client-side routing
 *        └─ AuthProvider  – Cognito session init on mount
 *             └─ AppRouter – declarative route tree (lazy-loaded pages)
 * ```
 *
 * The layout chrome (nav, sidebar, content area) is provided by
 * `AppShell` inside `AppRouter`, not here. This keeps App.tsx focused
 * on cross-cutting concerns (error handling, routing, auth) while
 * `AppShell` owns the visual structure — mirroring the monolith's
 * separation between `Startup.cs` (pipeline) and `_AppMaster.cshtml`
 * (layout).
 */
function App(): React.JSX.Element {
  return (
    <ErrorBoundary>
      <BrowserRouter>
        <AuthProvider>
          <AppRouter />
        </AuthProvider>
      </BrowserRouter>
    </ErrorBoundary>
  );
}

export default App;
