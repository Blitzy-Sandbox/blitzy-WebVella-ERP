/**
 * Login Page Component — `apps/frontend/src/pages/auth/Login.tsx`
 *
 * React login page replacing the monolith's `login.cshtml` and
 * `login.cshtml.cs` (`LoginModel`). Implements Cognito authentication
 * with a username/password form, error display, brand logo, and
 * return-URL redirect.
 *
 * Behavioral parity with the monolith (AAP §0.8.1):
 *  - `[AllowAnonymous]`              → Public route (no ProtectedRoute wrapper)
 *  - `OnGet` already-auth redirect   → useEffect checking isAuthenticated
 *  - `authService.Authenticate()`    → `login()` from api/auth.ts (Cognito)
 *  - `[BindProperty] ReturnUrl`      → useSearchParams().get('returnUrl')
 *  - `Error` display                 → useState for error + conditional div
 *  - `BrandLogo` from theme          → Static img placeholder
 *  - `LocalRedirectResult(ReturnUrl)` → navigate(returnUrl, { replace: true })
 *  - Bootstrap 4 card layout         → Tailwind CSS 4.x equivalents
 *  - `@Html.AntiForgeryToken()`      → Not needed (CSRF N/A for SPA with JWT)
 *  - `_SystemMaster.cshtml` layout   → Full-page centered layout (no AppShell)
 *
 * Security (AAP §0.8.3):
 *  - No secrets in bundle
 *  - No inline styles
 *  - Proper autoComplete attributes for credential management
 *  - role="alert" on error messages for screen readers
 */

import { useState, useEffect, type FormEvent } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { login } from '../../api/auth';
import { useAuthStore } from '../../stores/authStore';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Default access-token TTL in milliseconds (1 hour).
 *
 * Cognito's default access-token lifetime is 3600 seconds. This constant is
 * used as metadata for the Zustand store's `tokenExpiresAt` — the actual
 * token lifecycle is managed entirely by `api/auth.ts`.
 */
const DEFAULT_TOKEN_TTL_MS = 3600 * 1000;

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Login page component.
 *
 * Accessible at `/login` (public route — no ProtectedRoute wrapper), matching
 * the monolith's `@page "/login"` directive with `[AllowAnonymous]`.
 *
 * If the user is already authenticated, the component redirects immediately
 * to `returnUrl` (or `/` as the default), replicating the monolith's OnGet
 * guard (login.cshtml.cs lines 42-48).
 */
export default function Login(): React.JSX.Element {
  // ── Routing hooks ─────────────────────────────────────────────────────
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();

  /**
   * Return URL read from query string — replaces
   * `[BindProperty(Name = "returnUrl")] public new string ReturnUrl`
   * from LoginModel. Falls back to "/" when absent, matching the
   * monolith's `new LocalRedirectResult("/")`.
   */
  const returnUrl = searchParams.get('returnUrl') || '/';

  // ── Auth store selectors ──────────────────────────────────────────────
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const loginSuccess = useAuthStore((state) => state.loginSuccess);
  const setAuthError = useAuthStore((state) => state.setAuthError);

  // ── Local form state ──────────────────────────────────────────────────
  // Replaces [BindProperty] string Username / Password from LoginModel
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');

  // Replaces [BindProperty] string Error from LoginModel
  const [error, setError] = useState<string | null>(null);

  // Loading/disabled state during async form submission
  const [isSubmitting, setIsSubmitting] = useState(false);

  // ── Already-authenticated redirect ────────────────────────────────────
  // Replaces login.cshtml.cs OnGet lines 42-48:
  //   if (CurrentUser != null) {
  //     if (!string.IsNullOrWhiteSpace(ReturnUrl))
  //       return new LocalRedirectResult(ReturnUrl);
  //     else
  //       return new LocalRedirectResult("/");
  //   }
  useEffect(() => {
    if (isAuthenticated) {
      navigate(returnUrl, { replace: true });
    }
  }, [isAuthenticated, navigate, returnUrl]);

  // ── Form submission handler ───────────────────────────────────────────
  // Replaces login.cshtml.cs OnPost:
  //   - ModelState.IsValid check → basic client-side validation
  //   - authService.Authenticate(Username, Password) → login(email, password)
  //   - Error = "Invalid username or password" → setError(...)
  //   - LocalRedirectResult(ReturnUrl) → navigate(returnUrl, { replace: true })
  const handleSubmit = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    setError(null);

    // Basic client-side validation (replaces ModelState.IsValid check)
    const trimmedEmail = email.trim();
    const trimmedPassword = password.trim();

    if (!trimmedEmail) {
      setError('Email is required');
      return;
    }
    if (!trimmedPassword) {
      setError('Password is required');
      return;
    }

    setIsSubmitting(true);

    try {
      // Call Cognito auth via api/auth.ts
      // Replaces: ErpUser user = authService.Authenticate(Username, Password);
      const result = await login(trimmedEmail, trimmedPassword);

      // Update Zustand auth store — replaces cookie sign-in + SecurityContext
      // scope binding from ErpMiddleware.cs
      loginSuccess(
        {
          id: result.user.id,
          email: result.user.email,
          firstName: '',
          lastName: '',
          image: '',
          roles: result.user.roles,
          isAdmin: result.user.roles.includes('administrator'),
        },
        Date.now() + DEFAULT_TOKEN_TTL_MS,
      );

      // Redirect to returnUrl or "/" — replaces LocalRedirectResult
      navigate(returnUrl, { replace: true });
    } catch (err: unknown) {
      // Authentication failed — replaces login.cshtml.cs lines 100-104:
      //   Error = "Invalid username or password";
      //   BeforeRender();
      //   return Page();
      const errorMessage =
        err instanceof Error
          ? err.message
          : 'Invalid username or password';
      setError(errorMessage);
      setAuthError(errorMessage);
    } finally {
      setIsSubmitting(false);
    }
  };

  // ── Render ────────────────────────────────────────────────────────────
  // Layout: _SystemMaster.cshtml (minimal, no app chrome) →
  //   full-page centered container with Tailwind
  // Card:   Bootstrap .card.login-card.card-sm.shadow-sm →
  //   Tailwind rounded-lg bg-white shadow-md
  return (
    <div className="flex min-h-screen items-center justify-center bg-gray-100">
      {/* Login card — replaces .card.login-card.card-sm.shadow-sm */}
      <div className="w-full max-w-sm rounded-lg bg-white shadow-md">
        {/* Card header — replaces .card-header.text-center with BrandLogo + AppName */}
        <div className="border-b border-gray-200 px-6 py-4 text-center">
          <img
            src="/favicon.ico"
            alt=""
            aria-hidden="true"
            className="mx-auto mb-2 h-8 w-8"
            width={32}
            height={32}
          />
          <h1 className="text-lg font-semibold text-gray-800">
            WebVella ERP
          </h1>
        </div>

        {/* Card body — replaces .card-body.pt-3.pb-3 */}
        <div className="px-6 py-4">
          {/* Error alert — replaces .alert.alert-danger */}
          {error && (
            <div
              className="mb-4 rounded border border-red-400 bg-red-50 px-4 py-3 text-sm text-red-700"
              role="alert"
            >
              {error}
            </div>
          )}

          {/* Login form — replaces <form method="post" name="form"> */}
          {/* No @Html.AntiForgeryToken() — CSRF N/A for SPA with JWT */}
          <form onSubmit={handleSubmit} noValidate>
            {/* Email field — replaces .form-group with input type="email" */}
            <div className="mb-4">
              <label
                htmlFor="loginEmail"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Email
              </label>
              <input
                type="email"
                id="loginEmail"
                name="email"
                data-testid="email-input"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                className="block w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
                autoComplete="email"
                autoFocus
                disabled={isSubmitting}
                required
                aria-required="true"
              />
            </div>

            {/* Password field — replaces .form-group with input type="password" */}
            <div className="mb-4">
              <label
                htmlFor="loginPassword"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Password
              </label>
              <input
                type="password"
                id="loginPassword"
                name="password"
                data-testid="password-input"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className="block w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
                autoComplete="current-password"
                disabled={isSubmitting}
                required
                aria-required="true"
              />
            </div>

            {/* Submit button — replaces .btn.btn-primary.btn-block.mt-4.btn-sm */}
            <button
              type="submit"
              data-testid="login-button"
              className="mt-4 w-full rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
              disabled={isSubmitting}
            >
              {isSubmitting ? 'Logging in\u2026' : 'Login'}
            </button>
          </form>
        </div>
      </div>
    </div>
  );
}
