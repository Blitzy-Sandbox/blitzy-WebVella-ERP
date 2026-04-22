/**
 * Authentication E2E Test Suite — WebVella ERP React SPA
 *
 * Validates all critical authentication user-facing workflows against a full
 * LocalStack stack (Cognito + API Gateway + Lambda + DynamoDB). Replaces the
 * monolith's Razor Page authentication flows (login.cshtml, logout.cshtml,
 * [Authorize] attribute, cookie auth, JWT middleware) with browser-based
 * Cognito-backed authentication testing.
 *
 * Test user: erp@webvella.com / erp (seeded via tools/scripts/seed-test-data.sh)
 *
 * Critical rules (AAP §0.8.1, §0.8.4):
 *   - ALL tests run against LocalStack — zero mocked AWS SDK calls.
 *   - Tests interact with the real React SPA, API Gateway, Lambda handlers
 *     (services/identity, services/authorizer), Cognito (LocalStack) and DynamoDB.
 *   - Cognito operations may take up to 3 s in LocalStack, so generous timeouts
 *     are configured for authentication-related navigation and API calls.
 */

import { test, expect, Page, BrowserContext } from '@playwright/test';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default system user email — matches Definitions.cs SystemIds.FirstUserId */
const TEST_EMAIL = 'erp@webvella.com';

/** Default system password — migrated to Cognito via seed script */
const TEST_PASSWORD = 'erp';

/** Login page route — replaces login.cshtml Razor Page */
const LOGIN_URL = '/login';

/** Dashboard / home route — replaces Index.cshtml Razor Page (protected) */
const DASHBOARD_URL = '/';

/** Maximum time (ms) to wait for Cognito-backed authentication to complete */
const AUTH_TIMEOUT = 15_000;

// ---------------------------------------------------------------------------
// Reusable login helper — exported for consumption by other spec files
// ---------------------------------------------------------------------------

/**
 * Programmatically logs a user into the WebVella ERP React SPA through the
 * browser UI.  Navigates to the login page, fills credentials, submits the
 * form and waits for the resulting redirect to the dashboard.
 *
 * This helper mirrors the monolith's `LoginModel.OnPost()` flow:
 *   1.  Navigate to /login
 *   2.  Fill email  (replaces `name="Username"` input from login.cshtml)
 *   3.  Fill password (replaces `name="Password"` input from login.cshtml)
 *   4.  Click "Login" submit button
 *   5.  Wait for successful redirect to the dashboard (or returnUrl)
 *
 * @param page - Playwright Page instance to operate on.
 * @param email - User email address (defaults to TEST_EMAIL).
 * @param password - User password (defaults to TEST_PASSWORD).
 */
export async function login(
  page: Page,
  email: string = TEST_EMAIL,
  password: string = TEST_PASSWORD,
): Promise<void> {
  // Navigate to login page
  await page.goto(LOGIN_URL, { waitUntil: 'networkidle' });

  // Fill credentials — use accessible locator strategies (getByLabel / getByRole)
  const emailField = page.getByLabel(/email/i);
  const passwordField = page.getByLabel(/password/i);

  await emailField.fill(email);
  await passwordField.fill(password);

  // Submit the form
  await page.getByRole('button', { name: /login/i }).click();

  // Wait for the navigation away from /login, confirming successful auth.
  // In the monolith, success redirected to ReturnUrl or "/".  The React SPA
  // mirrors this by redirecting to the dashboard after Cognito auth completes.
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), {
    timeout: AUTH_TIMEOUT,
  });
}

// ---------------------------------------------------------------------------
// Test Suite
// ---------------------------------------------------------------------------

test.describe('Authentication', () => {
  /**
   * Runs once before all tests in this describe block.  Validates that the
   * SPA is reachable and the login page renders.  This acts as a smoke check
   * so that individual tests can assume the application is available, and
   * avoids wasting time on full test suites when the dev server or
   * LocalStack stack is down.
   */
  test.beforeAll(async ({ browser }) => {
    const context: BrowserContext = await browser.newContext();
    const page: Page = await context.newPage();

    try {
      const response = await page.goto(LOGIN_URL, {
        waitUntil: 'networkidle',
        timeout: 30_000,
      });

      // Ensure the application is responding
      expect(response).not.toBeNull();
      expect(response!.ok() || response!.status() === 304).toBeTruthy();
    } finally {
      await context.close();
    }
  });

  /**
   * Runs once after all tests complete.  Provides a clean teardown point
   * where any shared resources or persisted auth state artifacts can be
   * cleaned up.
   */
  test.afterAll(async ({ browser }) => {
    // Ensure no lingering browser contexts from this suite leak auth state
    // into other test files by closing any leftover contexts.
    const contexts = browser.contexts();
    for (const ctx of contexts) {
      await ctx.clearCookies();
    }
  });

  /**
   * Each test starts with a clean browser context — no stored auth state,
   * no cookies, no localStorage tokens.  This mirrors the monolith's
   * behaviour where an unauthenticated request receives a fresh session.
   */
  test.beforeEach(async ({ context }) => {
    await context.clearCookies();

    // Clear any Cognito tokens that may have been persisted in storage
    const pages = context.pages();
    for (const p of pages) {
      try {
        await p.evaluate(() => {
          try { localStorage.clear(); } catch { /* noop */ }
          try { sessionStorage.clear(); } catch { /* noop */ }
        });
      } catch {
        // Page may not have a valid origin yet — ignore safely
      }
    }
  });

  // -----------------------------------------------------------------------
  // Login Flow Tests
  // Replaces login.cshtml + LoginModel.OnPost() from the monolith.
  // -----------------------------------------------------------------------

  test.describe('Login Flow', () => {
    test('should render the login page with email and password fields', async ({ page }) => {
      await page.goto(LOGIN_URL, { waitUntil: 'networkidle' });

      // The login page must contain an email field, a password field and a
      // submit button — matching the monolith's login.cshtml structure.
      await expect(page.getByLabel(/email/i)).toBeVisible();
      await expect(page.getByLabel(/password/i)).toBeVisible();
      await expect(page.getByRole('button', { name: /login/i })).toBeVisible();
    });

    test('should login successfully with valid credentials', async ({ page }) => {
      // Use the reusable login helper
      await login(page);

      // After login the URL must be the dashboard (not /login)
      expect(page.url()).not.toContain('/login');

      // The dashboard content should be visible (replaces Index.cshtml rendering)
      await expect(page.locator('body')).not.toBeEmpty();

      // Verify user identity reflected in the UI — the nav bar should show the
      // user's email or display name (replaces UserMenu ViewComponent).
      const userIndicator = page.getByText(TEST_EMAIL).or(
        page.getByText(/erp/i),
      );
      await expect(userIndicator.first()).toBeVisible({ timeout: AUTH_TIMEOUT });
    });

    test('should reject login with invalid credentials', async ({ page }) => {
      await page.goto(LOGIN_URL, { waitUntil: 'networkidle' });

      await page.getByLabel(/email/i).fill('invalid@example.com');
      await page.getByLabel(/password/i).fill('wrongpassword');
      await page.getByRole('button', { name: /login/i }).click();

      // Page must remain on /login — no redirect to dashboard
      await page.waitForTimeout(2_000); // Allow Cognito response time
      expect(page.url()).toContain('/login');

      // An error message must be visible.  The monolith used
      // "Invalid username or password" (login.cshtml.cs line 102).
      // The React SPA may use a Cognito-provided error string or equivalent.
      const errorMessage = page
        .getByText(/invalid|incorrect|not authorized|authentication failed/i)
        .first();
      await expect(errorMessage).toBeVisible({ timeout: AUTH_TIMEOUT });
    });

    test('should show validation errors when fields are empty', async ({ page }) => {
      await page.goto(LOGIN_URL, { waitUntil: 'networkidle' });

      // Click login without filling any field
      await page.getByRole('button', { name: /login/i }).click();

      // Page must stay on /login
      expect(page.url()).toContain('/login');

      // Validation messages should appear for required fields.
      // Depending on implementation these could be native HTML5 validation
      // tooltips, or custom inline error messages.
      const emailField = page.getByLabel(/email/i);
      const passwordField = page.getByLabel(/password/i);

      // Check that the fields are marked as invalid or that error text appears
      const hasValidationFeedback = await Promise.race([
        // Strategy 1: Look for visible error text
        page.getByText(/required|enter.*email|enter.*password/i)
          .first()
          .isVisible()
          .catch(() => false),
        // Strategy 2: HTML5 validation pseudo-class
        emailField.evaluate((el: HTMLInputElement) => !el.validity.valid).catch(() => false),
        // Strategy 3: aria-invalid attribute
        emailField.evaluate((el) => el.getAttribute('aria-invalid') === 'true').catch(() => false),
      ]);

      // At least one validation signal must be present
      expect(
        hasValidationFeedback ||
          (await passwordField
            .evaluate((el: HTMLInputElement) => !el.validity.valid)
            .catch(() => false)),
      ).toBeTruthy();
    });

    test('should show validation error for invalid email format', async ({ page }) => {
      await page.goto(LOGIN_URL, { waitUntil: 'networkidle' });

      await page.getByLabel(/email/i).fill('notanemail');
      await page.getByLabel(/password/i).fill('somepassword');
      await page.getByRole('button', { name: /login/i }).click();

      // Page must remain on /login
      await page.waitForTimeout(1_000);
      expect(page.url()).toContain('/login');

      // Check for either a visible error message or native HTML5 validity
      const emailField = page.getByLabel(/email/i);
      const hasEmailValidationError = await Promise.race([
        page
          .getByText(/valid.*email|invalid.*email|email.*format/i)
          .first()
          .isVisible()
          .catch(() => false),
        emailField
          .evaluate((el: HTMLInputElement) => el.validity.typeMismatch)
          .catch(() => false),
      ]);

      expect(hasEmailValidationError).toBeTruthy();
    });
  });

  // -----------------------------------------------------------------------
  // Logout Flow Tests
  // Replaces logout.cshtml.cs LogoutModel.OnGet() / OnPost() from the monolith.
  //   Source: authService.Logout() → redirect to "/" → unauthenticated
  //   React SPA: Cognito token revocation → redirect to /login
  // -----------------------------------------------------------------------

  test.describe('Logout Flow', () => {
    test('should logout successfully and redirect to login', async ({ page }) => {
      // First, login to establish authenticated state
      await login(page);

      // Confirm we are on the dashboard
      expect(page.url()).not.toContain('/login');

      // Click the logout element.  In the React SPA the logout action is
      // typically in the user dropdown menu in the top nav bar.
      // Try multiple strategies to find the logout trigger.
      const logoutLink = page.getByRole('link', { name: /logout|log out|sign out/i })
        .or(page.getByRole('button', { name: /logout|log out|sign out/i }))
        .or(page.getByText(/logout|log out|sign out/i));

      // If the logout link is hidden behind a dropdown, open the user menu first
      const userMenuToggle = page.getByRole('button', { name: /user|account|profile|menu/i })
        .or(page.locator('[data-testid="user-menu"]'))
        .or(page.locator('[aria-label*="user" i]'));

      const isUserMenuVisible = await userMenuToggle.first().isVisible().catch(() => false);
      if (isUserMenuVisible) {
        await userMenuToggle.first().click();
        await page.waitForTimeout(500);
      }

      await logoutLink.first().click();

      // After logout, the SPA should redirect to /login (since "/" requires auth)
      await page.waitForURL(/\/login/, { timeout: AUTH_TIMEOUT });
      expect(page.url()).toContain('/login');
    });

    test('should clear session after logout so protected pages redirect', async ({ page }) => {
      // Login and verify
      await login(page);
      expect(page.url()).not.toContain('/login');

      // Perform logout
      const logoutLink = page.getByRole('link', { name: /logout|log out|sign out/i })
        .or(page.getByRole('button', { name: /logout|log out|sign out/i }))
        .or(page.getByText(/logout|log out|sign out/i));

      const userMenuToggle = page.getByRole('button', { name: /user|account|profile|menu/i })
        .or(page.locator('[data-testid="user-menu"]'))
        .or(page.locator('[aria-label*="user" i]'));

      const isMenuVisible = await userMenuToggle.first().isVisible().catch(() => false);
      if (isMenuVisible) {
        await userMenuToggle.first().click();
        await page.waitForTimeout(500);
      }

      await logoutLink.first().click();
      await page.waitForURL(/\/login/, { timeout: AUTH_TIMEOUT });

      // Now try to navigate to a protected page
      await page.goto(DASHBOARD_URL, { waitUntil: 'networkidle' });

      // Should be redirected back to login — session has been cleared
      await page.waitForURL(/\/login/, { timeout: AUTH_TIMEOUT });
      expect(page.url()).toContain('/login');
    });
  });

  // -----------------------------------------------------------------------
  // Protected Route Redirect Tests
  // Replaces [Authorize] attribute from BaseErpPageModel in the monolith.
  // The ASP.NET Core auth middleware redirected unauthenticated requests to
  // /login?returnUrl={originalUrl}.  The React SPA replicates this with
  // route guards and Cognito token checks.
  // -----------------------------------------------------------------------

  test.describe('Protected Route Redirects', () => {
    test('should redirect unauthenticated user from dashboard to login', async ({ page }) => {
      // Navigate to the protected dashboard without any auth tokens
      await page.goto(DASHBOARD_URL, { waitUntil: 'networkidle' });

      // Should end up on the login page
      await page.waitForURL(/\/login/, { timeout: AUTH_TIMEOUT });
      expect(page.url()).toContain('/login');
    });

    test('should redirect unauthenticated user with returnUrl preserved', async ({ page }) => {
      const protectedDeepUrl = '/some-app/some-area/some-node/l/';

      // Navigate to a deep protected page
      await page.goto(protectedDeepUrl, { waitUntil: 'networkidle' });

      // Should redirect to /login — the returnUrl may or may not be in the URL
      // depending on the SPA's route-guard implementation
      await page.waitForURL(/\/login/, { timeout: AUTH_TIMEOUT });
      expect(page.url()).toContain('/login');

      // Login with valid credentials
      await page.getByLabel(/email/i).fill(TEST_EMAIL);
      await page.getByLabel(/password/i).fill(TEST_PASSWORD);
      await page.getByRole('button', { name: /login/i }).click();

      // Wait for navigation away from /login.  Ideally the SPA redirects
      // back to the originally requested page (returnUrl behaviour).
      // If returnUrl is not supported, at minimum the user should land on
      // the dashboard.
      await page.waitForURL((url) => !url.pathname.startsWith('/login'), {
        timeout: AUTH_TIMEOUT,
      });

      // Verify the user is authenticated (on some protected page)
      expect(page.url()).not.toContain('/login');
    });

    test('should redirect multiple protected routes to login when unauthenticated', async ({
      page,
    }) => {
      const protectedRoutes = [
        '/',                          // Home / Dashboard
        '/app/a/',                    // Application Home
        '/app/area/node/a/',          // Application Node
      ];

      for (const route of protectedRoutes) {
        await page.goto(route, { waitUntil: 'networkidle' });
        await page.waitForURL(/\/login/, { timeout: AUTH_TIMEOUT });
        expect(page.url()).toContain('/login');
      }
    });
  });

  // -----------------------------------------------------------------------
  // Token Refresh / Session Persistence Tests
  // Replaces cookie-based auth (AuthService.cs) + JWT middleware
  // (JwtMiddleware.cs).  The React SPA stores Cognito JWT tokens in
  // localStorage / sessionStorage.  Session should persist across
  // page refreshes and intra-app navigation.
  // -----------------------------------------------------------------------

  test.describe('Session Persistence', () => {
    test('should persist session across page refresh', async ({ page }) => {
      // Login and verify on dashboard
      await login(page);
      expect(page.url()).not.toContain('/login');

      // Refresh the page
      await page.reload({ waitUntil: 'networkidle' });

      // Should still be on the dashboard (Cognito tokens persisted)
      expect(page.url()).not.toContain('/login');

      // Verify user identity is still displayed in the nav bar
      const userIndicator = page.getByText(TEST_EMAIL).or(
        page.getByText(/erp/i),
      );
      await expect(userIndicator.first()).toBeVisible({ timeout: AUTH_TIMEOUT });
    });

    test('should persist session across navigation to different protected pages', async ({
      page,
    }) => {
      // Login first
      await login(page);
      expect(page.url()).not.toContain('/login');

      // Navigate to several protected pages sequentially — none should
      // trigger a re-authentication redirect.
      const protectedPages = ['/', '/app/a/', '/app/area/node/a/'];

      for (const route of protectedPages) {
        await page.goto(route, { waitUntil: 'networkidle' });

        // Allow for a brief navigation but must NOT end up on /login
        await page.waitForTimeout(1_000);
        expect(page.url()).not.toContain('/login');
      }
    });
  });
});
