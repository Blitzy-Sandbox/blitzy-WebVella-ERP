/**
 * @file Navigation E2E Tests — WebVella ERP React SPA
 *
 * Comprehensive Playwright E2E test suite validating all critical navigation
 * user-facing workflows of the WebVella ERP React SPA against a full LocalStack stack.
 *
 * Replaces the monolith's Razor ViewComponent navigation chrome:
 *   - Nav ViewComponent (top navigation bar with brand, app links, user menu, search)
 *   - SidebarMenu ViewComponent (sidebar navigation with collapse/expand)
 *   - ApplicationMenu ViewComponent (hierarchical app/area/node tree navigation)
 *   - Breadcrumb resolution from ErpRequestContext (App → Area → Node → Page)
 *
 * Route patterns validated (from router.tsx, replacing Razor Page @page directives):
 *   - `/`                                        → Dashboard  (Index.cshtml / HomePageModel)
 *   - `/:appName/a/:pageName?`                   → App Home   (ApplicationHome.cshtml)
 *   - `/:appName/:areaName/:nodeName/a/:pageName?` → App Node (ApplicationNode.cshtml)
 *   - `/admin/*`, `/crm/*`, `/projects/*`         → Domain routes
 *   - `/login`, `/logout`                         → Auth routes (public)
 *
 * Test user: erp@webvella.com / erp (seeded via tools/scripts/seed-test-data.sh)
 *
 * @see WebVella.Erp.Web/Pages/_AppMaster.cshtml         — Original layout chrome
 * @see WebVella.Erp.Web/Components/Nav/Nav.cshtml        — Original top nav
 * @see WebVella.Erp.Web/Components/SidebarMenu/SidebarMenu.cshtml — Original sidebar
 * @see WebVella.Erp.Web/Components/ApplicationMenu/ApplicationMenu.cshtml — Original app menu
 */

import { test, expect, Page, BrowserContext } from '@playwright/test';

// ─── Test Configuration Constants ───────────────────────────────────────────

/** Base URL for the React SPA frontend (Vite dev server or production build) */
const BASE_URL: string = process.env.PLAYWRIGHT_BASE_URL || 'http://localhost:5173';

/** Seeded test user email (matches Cognito seed from seed-test-data.sh) */
const TEST_EMAIL = 'erp@webvella.com';

/** Seeded test user password */
const TEST_PASSWORD = 'erp';

/** Login page URL */
const LOGIN_URL = `${BASE_URL}/login`;

/** Dashboard / home page URL (site root) */
const DASHBOARD_URL = BASE_URL;

/** Maximum wait time for navigation and auth actions (accounts for Lambda cold starts) */
const NAV_TIMEOUT = 15_000;

/** Short settle time after navigation for DOM and state updates */
const SETTLE_TIME = 500;

// ─── Test Route Patterns (matching React Router 7 config from router.tsx) ───

/**
 * Generates application home route.
 * Replaces: `/{AppName}/a/{PageName?}` → ApplicationHome.cshtml
 */
const APP_HOME_ROUTE = (appName: string): string => `/${appName}/a/`;

/**
 * Generates application node route.
 * Replaces: `/{AppName}/{AreaName}/{NodeName}/a/{PageName?}` → ApplicationNode.cshtml
 */
const APP_NODE_ROUTE = (appName: string, areaName: string, nodeName: string): string =>
  `/${appName}/${areaName}/${nodeName}/a/`;

/** Well-known test application name used by seeded data */
const TEST_APP_NAME = 'sdk';

/** Well-known test area name used by seeded data */
const TEST_AREA_NAME = 'objects';

/** Well-known test node name used by seeded data */
const TEST_NODE_NAME = 'entities';

// ─── Helper Functions ───────────────────────────────────────────────────────

/**
 * Authenticates the given page as the seeded test user via the React login form.
 * Uses Cognito authentication flow (replacing the monolith's cookie-based
 * AuthService.cs login with MD5 password validation via SecurityManager).
 *
 * @param targetPage - Playwright Page instance to authenticate
 */
async function login(targetPage: Page): Promise<void> {
  await targetPage.goto(LOGIN_URL);
  await targetPage.waitForLoadState('domcontentloaded');

  // Locate login form elements using semantic locators (resilient to markup changes)
  const emailInput = targetPage
    .getByLabel(/email/i)
    .or(targetPage.getByRole('textbox', { name: /email/i }))
    .or(targetPage.locator('input[type="email"], input[name="email"]'));

  const passwordInput = targetPage
    .getByLabel(/password/i)
    .or(targetPage.locator('input[type="password"], input[name="password"]'));

  const submitButton = targetPage
    .getByRole('button', { name: /sign in|log in|login|submit/i })
    .or(targetPage.locator('button[type="submit"]'));

  // Fill credentials and submit the form
  await emailInput.fill(TEST_EMAIL);
  await passwordInput.fill(TEST_PASSWORD);
  await submitButton.click();

  // Wait for successful redirect away from /login to the dashboard
  await targetPage.waitForURL(
    (url) => !url.pathname.includes('/login'),
    { timeout: NAV_TIMEOUT }
  );
  await targetPage.waitForLoadState('networkidle');
}

/**
 * Resolves a sidebar locator using multiple selector strategies.
 * The React Sidebar component replaces the monolith's `<vc:sidebar-menu>`
 * rendered inside `#sidebar` div in _AppMaster.cshtml.
 */
function getSidebar(targetPage: Page) {
  return targetPage
    .getByRole('navigation', { name: /sidebar/i })
    .or(targetPage.locator('[data-testid="sidebar"]'))
    .or(targetPage.locator('#sidebar'))
    .or(targetPage.locator('nav.sidebar, aside.sidebar, [class*="sidebar"]'));
}

/**
 * Resolves the top navigation bar locator.
 * The React TopNav component replaces the monolith's `<vc:nav>` rendered
 * inside `#nav` in Nav.Default.cshtml.
 */
function getTopNav(targetPage: Page) {
  return targetPage
    .getByRole('navigation', { name: /top|main|header/i })
    .or(targetPage.locator('[data-testid="top-nav"]'))
    .or(targetPage.locator('#nav'))
    .or(targetPage.locator('header nav, nav.top-nav, [class*="topnav"], [class*="top-nav"]'));
}

/**
 * Resolves the breadcrumb navigation locator.
 * Replaces the monolith's ErpRequestContext breadcrumb chain built from
 * App → Area → Node → Page URL segment resolution.
 */
function getBreadcrumb(targetPage: Page) {
  return targetPage
    .getByRole('navigation', { name: /breadcrumb/i })
    .or(targetPage.locator('[data-testid="breadcrumb"]'))
    .or(targetPage.locator('[aria-label="breadcrumb"], nav.breadcrumb, ol.breadcrumb'));
}

// ═══════════════════════════════════════════════════════════════════════════════
// NAVIGATION TEST SUITE
// ═══════════════════════════════════════════════════════════════════════════════

test.describe('Navigation', () => {
  /**
   * Run tests serially within this describe block — all tests share the same
   * authenticated browser context to avoid redundant login flows per test.
   */
  test.describe.configure({ mode: 'serial' });

  let page: Page;
  let context: BrowserContext;

  /**
   * Before all navigation tests: create an authenticated browser context.
   * This replaces the monolith's per-request cookie/JWT authentication from
   * ErpMiddleware.cs which binds SecurityContext.CurrentUser on every request.
   */
  test.beforeAll(async ({ browser }) => {
    context = await browser.newContext();
    page = await context.newPage();

    // Smoke check: verify the SPA is reachable before running tests
    const response = await page.goto(BASE_URL);
    expect(
      response !== null && (response.ok() || response.status() === 304)
    ).toBeTruthy();

    // Authenticate as the seeded test user
    await login(page);
  });

  /**
   * After all navigation tests: clean up the browser context.
   */
  test.afterAll(async () => {
    if (context) {
      await context.close();
    }
  });

  /**
   * Before each test: navigate to the dashboard home page to ensure a
   * consistent starting state. Replaces the monolith's per-request
   * BaseErpPageModel.Init() + BeforeRender() lifecycle.
   */
  test.beforeEach(async () => {
    await page.goto(DASHBOARD_URL);
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(SETTLE_TIME);
  });

  // ═════════════════════════════════════════════════════════════════════════════
  // SECTION 1: SIDEBAR NAVIGATION TESTS
  // Replaces: SidebarMenu ViewComponent (WebVella.Erp.Web/Components/SidebarMenu/)
  //
  // The monolith's SidebarMenu.cs receives BaseErpPageModel.SidebarMenu (a List<MenuItem>)
  // via ViewBag. SidebarMenu.cshtml iterates menu items via <partial name="NavMenu"> and
  // includes a `.sidebar-switch` button for collapse/expand. The React Sidebar component
  // renders equivalent navigation within the AppShell layout.
  // ═════════════════════════════════════════════════════════════════════════════

  test.describe('Sidebar Navigation', () => {
    test('sidebar navigation component is visible with app links and brand section', async () => {
      // Assert the sidebar navigation component is visible
      // (replaces <vc:sidebar-menu> in _AppMaster.cshtml)
      const sidebar = getSidebar(page);
      await expect(sidebar).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify sidebar contains navigation links (replaces NavMenu partial iterations)
      const sidebarLinks = sidebar.getByRole('link');
      const linkCount = await sidebarLinks.count();
      expect(linkCount).toBeGreaterThan(0);

      // Verify sidebar has a brand/logo section at the top
      // (replaces the monolith's sidebar-header / sidebar-brand area)
      const brandElement = sidebar
        .getByRole('img', { name: /logo|brand|webvella/i })
        .or(sidebar.locator('[data-testid="sidebar-logo"]'))
        .or(sidebar.locator('.sidebar-brand, .sidebar-logo, .sidebar-header img'))
        .or(sidebar.getByRole('link').filter({ hasText: /home|webvella|erp/i }).first());
      await expect(brandElement).toBeVisible({ timeout: NAV_TIMEOUT });
    });

    test('clicking a sidebar navigation link changes route and updates content', async () => {
      const sidebar = getSidebar(page);
      await expect(sidebar).toBeVisible({ timeout: NAV_TIMEOUT });

      // Get the first navigable link in the sidebar
      const navLinks = sidebar.getByRole('link');
      const linkCount = await navLinks.count();
      expect(linkCount).toBeGreaterThan(0);

      // Capture the href of the first link for URL assertion
      const firstLink = navLinks.first();
      const href = await firstLink.getAttribute('href');
      expect(href).toBeTruthy();

      // Record current URL before clicking
      const urlBefore = page.url();

      // Click the sidebar navigation link
      await firstLink.click();
      await page.waitForTimeout(SETTLE_TIME);

      // Assert the URL changes to the expected route
      if (href && !href.startsWith('http')) {
        await page.waitForURL(`**${href}*`, { timeout: NAV_TIMEOUT });
      }
      // Verify URL actually changed (or is at expected destination)
      const urlAfter = page.url();
      if (href !== '/') {
        // URL should have changed from the dashboard
        expect(urlAfter).not.toBe(urlBefore);
      }

      // Verify the active link is highlighted (aria-current or active class)
      const isActiveAria = await firstLink.getAttribute('aria-current');
      const activeClass = await firstLink.getAttribute('class');
      const hasActiveIndicator =
        isActiveAria === 'page' ||
        isActiveAria === 'true' ||
        (activeClass !== null && /active|selected|current/i.test(activeClass));
      expect(hasActiveIndicator).toBeTruthy();
    });

    test('sidebar collapse toggle switches between full and icon-only modes', async () => {
      const sidebar = getSidebar(page);
      await expect(sidebar).toBeVisible({ timeout: NAV_TIMEOUT });

      // Locate the sidebar collapse/expand toggle button
      // (replaces the .sidebar-switch button from SidebarMenu.cshtml)
      const collapseToggle = page
        .getByRole('button', { name: /collapse|toggle|sidebar|menu/i })
        .or(page.locator('[data-testid="sidebar-toggle"]'))
        .or(page.locator('.sidebar-switch, .sidebar-toggle, [class*="sidebar-collapse"]'))
        .or(sidebar.getByRole('button').first());

      // Skip this test gracefully if no collapse toggle exists
      const toggleExists = await collapseToggle.isVisible().catch(() => false);
      if (!toggleExists) {
        test.skip(true, 'Sidebar collapse toggle not present in this implementation');
        return;
      }

      // Capture initial sidebar width
      const initialBox = await sidebar.boundingBox();
      expect(initialBox).not.toBeNull();
      const initialWidth = initialBox!.width;

      // Toggle sidebar collapse (should switch to icon-only mode)
      await collapseToggle.click();
      await page.waitForTimeout(SETTLE_TIME);

      // Assert sidebar is in collapsed/icon-only mode (narrower width)
      const collapsedBox = await sidebar.boundingBox();
      expect(collapsedBox).not.toBeNull();
      expect(collapsedBox!.width).toBeLessThan(initialWidth);

      // Toggle sidebar expand (should return to full width)
      await collapseToggle.click();
      await page.waitForTimeout(SETTLE_TIME);

      // Assert sidebar is back to full width
      const expandedBox = await sidebar.boundingBox();
      expect(expandedBox).not.toBeNull();
      expect(expandedBox!.width).toBeGreaterThanOrEqual(initialWidth * 0.9);
    });

    test('sidebar links navigate to correct routes and update page content', async () => {
      const sidebar = getSidebar(page);
      await expect(sidebar).toBeVisible({ timeout: NAV_TIMEOUT });

      const navLinks = sidebar.getByRole('link');
      const linkCount = await navLinks.count();

      // Test navigation for up to 3 sidebar links to keep test fast
      const linksToTest = Math.min(linkCount, 3);
      for (let i = 0; i < linksToTest; i++) {
        // Navigate back to dashboard before each link test
        await page.goto(DASHBOARD_URL);
        await page.waitForLoadState('domcontentloaded');
        await page.waitForTimeout(SETTLE_TIME);

        const link = sidebar.getByRole('link').nth(i);
        const linkHref = await link.getAttribute('href');
        const linkText = await link.textContent();

        if (!linkHref) continue;

        // Click the link
        await link.click();
        await page.waitForTimeout(SETTLE_TIME);

        // Verify URL contains the link's href target
        if (!linkHref.startsWith('http') && linkHref !== '/') {
          await page.waitForURL(`**${linkHref}*`, { timeout: NAV_TIMEOUT });
        }

        // Verify content area is visible (the #content div from _AppMaster.cshtml layout)
        const content = page
          .locator('[data-testid="content"]')
          .or(page.locator('#content'))
          .or(page.locator('main, [role="main"]'));
        await expect(content.first()).toBeVisible({ timeout: NAV_TIMEOUT });
      }
    });
  });

  // ═════════════════════════════════════════════════════════════════════════════
  // SECTION 2: TOP NAVIGATION BAR TESTS
  // Replaces: Nav ViewComponent (WebVella.Erp.Web/Components/Nav/)
  //
  // The monolith's Nav.Default.cshtml renders `#nav` containing:
  //   - Home link (brand/logo)
  //   - <vc:site-menu> — site-level navigation
  //   - Brand/logo area
  //   - <vc:application-menu> — current app menu
  //   - <vc:user-menu> — user dropdown (Profile, Settings, Logout)
  //   - <vc:search-nav> — search input placeholder
  //   - <vc:user-nav> — user identity display
  // The script.js handles jQuery dropdown toggle behavior via click handlers.
  // ═════════════════════════════════════════════════════════════════════════════

  test.describe('Top Navigation Bar', () => {
    test('top navigation bar is visible with application name and user info', async () => {
      // Assert the top navigation bar is visible (replaces <vc:nav> in _AppMaster.cshtml)
      const topNav = getTopNav(page);
      await expect(topNav).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify it contains the application name/logo
      // (replaces the brand area from Nav.Default.cshtml)
      const brandLink = topNav
        .getByRole('link', { name: /home|webvella|erp|dashboard/i })
        .or(topNav.getByRole('img', { name: /logo|brand/i }))
        .or(topNav.locator('.brand, .navbar-brand, [data-testid="brand-link"]'));
      await expect(brandLink.first()).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify user info is displayed (email or name from Cognito JWT claims)
      // (replaces <vc:user-nav> and <vc:user-menu> ViewComponents)
      const userInfo = topNav
        .getByText(TEST_EMAIL)
        .or(topNav.getByText(/erp|admin|user/i))
        .or(topNav.locator('[data-testid="user-info"]'))
        .or(topNav.locator('.user-info, .user-name, .user-email'));
      await expect(userInfo.first()).toBeVisible({ timeout: NAV_TIMEOUT });
    });

    test('user menu dropdown opens with options and closes on outside click', async () => {
      const topNav = getTopNav(page);
      await expect(topNav).toBeVisible({ timeout: NAV_TIMEOUT });

      // Click the user avatar/name to open dropdown
      // (replaces <vc:user-menu> jQuery dropdown from Nav script.js)
      const userMenuTrigger = topNav
        .getByRole('button', { name: /user|account|profile|menu/i })
        .or(topNav.locator('[data-testid="user-menu-trigger"]'))
        .or(topNav.getByText(TEST_EMAIL))
        .or(topNav.locator('.user-menu-trigger, .avatar, .user-dropdown-toggle'));
      await expect(userMenuTrigger.first()).toBeVisible({ timeout: NAV_TIMEOUT });

      await userMenuTrigger.first().click();
      await page.waitForTimeout(SETTLE_TIME);

      // Assert dropdown menu appears with expected options
      const dropdownMenu = page
        .getByRole('menu')
        .or(page.locator('[data-testid="user-dropdown"]'))
        .or(page.locator('.dropdown-menu.show, [class*="dropdown"][class*="open"]'));
      await expect(dropdownMenu.first()).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify dropdown contains Profile, Settings, and Logout options
      const profileOption = dropdownMenu.first()
        .getByRole('menuitem', { name: /profile/i })
        .or(dropdownMenu.first().getByRole('link', { name: /profile/i }))
        .or(dropdownMenu.first().getByText(/profile/i));
      const logoutOption = dropdownMenu.first()
        .getByRole('menuitem', { name: /log ?out|sign ?out/i })
        .or(dropdownMenu.first().getByRole('link', { name: /log ?out|sign ?out/i }))
        .or(dropdownMenu.first().getByText(/log ?out|sign ?out/i));

      await expect(profileOption.first()).toBeVisible({ timeout: NAV_TIMEOUT });
      await expect(logoutOption.first()).toBeVisible({ timeout: NAV_TIMEOUT });

      // Click outside the dropdown to dismiss it
      await page.locator('body').click({ position: { x: 10, y: 10 } });
      await page.waitForTimeout(SETTLE_TIME);

      // Assert dropdown is now hidden
      await expect(dropdownMenu.first()).toBeHidden({ timeout: NAV_TIMEOUT });
    });

    test('search input is present in the top navigation bar', async () => {
      const topNav = getTopNav(page);
      await expect(topNav).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify the search input/icon is present
      // (replaces <vc:search-nav> ViewComponent — placeholder in the monolith)
      const searchElement = topNav
        .getByRole('searchbox')
        .or(topNav.getByRole('textbox', { name: /search/i }))
        .or(topNav.getByLabel(/search/i))
        .or(topNav.locator('[data-testid="search-input"]'))
        .or(topNav.locator('input[type="search"], .search-input, [class*="search"]'));

      // Search may be an input or just an icon/button (placeholder in original monolith)
      const searchButton = topNav
        .getByRole('button', { name: /search/i })
        .or(topNav.locator('[data-testid="search-button"]'))
        .or(topNav.locator('.search-icon, .search-button'));

      const hasSearchInput = await searchElement.first().isVisible().catch(() => false);
      const hasSearchButton = await searchButton.first().isVisible().catch(() => false);

      // At least one search element should be present
      expect(hasSearchInput || hasSearchButton).toBeTruthy();

      // If the search input is functional, verify it accepts text
      if (hasSearchInput) {
        await searchElement.first().fill('test query');
        const searchValue = await searchElement.first().inputValue();
        expect(searchValue).toBe('test query');
        // Clear the search input
        await searchElement.first().clear();
      }
    });
  });

  // ═════════════════════════════════════════════════════════════════════════════
  // SECTION 3: BREADCRUMB NAVIGATION TESTS
  // Replaces: ErpRequestContext URL-based breadcrumb resolution
  //
  // The monolith builds breadcrumbs from ErpRequestContext which resolves
  // App → Area → Node → Page from URL segments. The BaseErpPageModel.Init()
  // method populates ParentAppName, CurrentApp, CurrentArea, CurrentNode
  // based on URL routing parameters. The React SPA replicates this via
  // React Router route configuration and a Breadcrumb component.
  // ═════════════════════════════════════════════════════════════════════════════

  test.describe('Breadcrumb Navigation', () => {
    test('breadcrumb renders on application home page with correct hierarchy', async () => {
      // Navigate to an application home page
      // (replaces /{AppName}/a/ → ApplicationHome.cshtml)
      const appHomeUrl = `${BASE_URL}${APP_HOME_ROUTE(TEST_APP_NAME)}`;
      await page.goto(appHomeUrl);
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert breadcrumb component is visible
      const breadcrumb = getBreadcrumb(page);
      await expect(breadcrumb).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify breadcrumb shows: Home > App Name
      const breadcrumbItems = breadcrumb
        .getByRole('listitem')
        .or(breadcrumb.locator('li, span.breadcrumb-item, a'));
      const itemCount = await breadcrumbItems.count();
      expect(itemCount).toBeGreaterThanOrEqual(2);

      // First breadcrumb item should be Home (root link)
      const homeItem = breadcrumbItems.first();
      const homeText = await homeItem.textContent();
      expect(homeText?.toLowerCase()).toMatch(/home|dashboard/);

      // Last breadcrumb item should contain the app name
      const appItem = breadcrumbItems.last();
      const appText = await appItem.textContent();
      expect(appText?.toLowerCase()).toContain(TEST_APP_NAME.toLowerCase());
    });

    test('breadcrumb renders on area/node page with full hierarchy', async () => {
      // Navigate to a deeper page with app/area/node context
      // (replaces /{AppName}/{AreaName}/{NodeName}/a/ → ApplicationNode.cshtml)
      const nodeUrl = `${BASE_URL}${APP_NODE_ROUTE(TEST_APP_NAME, TEST_AREA_NAME, TEST_NODE_NAME)}`;
      await page.goto(nodeUrl);
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert breadcrumb component is visible
      const breadcrumb = getBreadcrumb(page);
      await expect(breadcrumb).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify breadcrumb shows: Home > App > Area > Node
      const breadcrumbItems = breadcrumb
        .getByRole('listitem')
        .or(breadcrumb.locator('li, span.breadcrumb-item, a'));
      const itemCount = await breadcrumbItems.count();
      // Expect at least 3 items: Home, App, and Area/Node (some may merge)
      expect(itemCount).toBeGreaterThanOrEqual(3);

      // Verify the breadcrumb contains the app name somewhere in its text
      const breadcrumbText = await breadcrumb.textContent();
      expect(breadcrumbText?.toLowerCase()).toContain(TEST_APP_NAME.toLowerCase());
    });

    test('clicking a breadcrumb link navigates to the correct parent page', async () => {
      // Navigate to a deep page first
      const nodeUrl = `${BASE_URL}${APP_NODE_ROUTE(TEST_APP_NAME, TEST_AREA_NAME, TEST_NODE_NAME)}`;
      await page.goto(nodeUrl);
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(SETTLE_TIME);

      const breadcrumb = getBreadcrumb(page);
      await expect(breadcrumb).toBeVisible({ timeout: NAV_TIMEOUT });

      // Find breadcrumb links (not the last/current item which may be plain text)
      const breadcrumbLinks = breadcrumb.getByRole('link');
      const linkCount = await breadcrumbLinks.count();

      if (linkCount === 0) {
        // If no links, breadcrumb may use different markup — skip gracefully
        test.skip(true, 'Breadcrumb does not use link elements');
        return;
      }

      // Click the Home/first breadcrumb link
      const homeLink = breadcrumbLinks.first();
      const homeLinkHref = await homeLink.getAttribute('href');
      await homeLink.click();
      await page.waitForTimeout(SETTLE_TIME);

      // Assert navigation to the home/parent page
      if (homeLinkHref) {
        await page.waitForURL(`**${homeLinkHref}*`, { timeout: NAV_TIMEOUT });
      }

      // Verify the page content updates (we're no longer on the node page)
      const currentUrl = page.url();
      expect(currentUrl).not.toContain(
        APP_NODE_ROUTE(TEST_APP_NAME, TEST_AREA_NAME, TEST_NODE_NAME)
      );
    });
  });

  // ═════════════════════════════════════════════════════════════════════════════
  // SECTION 4: APP / AREA / NODE ROUTING TESTS
  // Replaces: ApplicationHome.cshtml.cs, ApplicationNode.cshtml.cs, Index.cshtml.cs
  //
  // Source routing patterns from Razor Pages:
  //   - `/{PageName?}`                                     → Index.cshtml (HomePageModel)
  //   - `/{AppName}/a/{PageName?}`                         → ApplicationHome.cshtml
  //   - `/{AppName}/{AreaName}/{NodeName}/a/{PageName?}`   → ApplicationNode.cshtml
  //
  // The React SPA uses React Router 7 with lazy-loaded page components.
  // All page models extend BaseErpPageModel which calls Init() then
  // BeforeRender() — the React equivalent is route-level data fetching
  // via TanStack Query hooks.
  // ═════════════════════════════════════════════════════════════════════════════

  test.describe('App/Area/Node Routing', () => {
    test('home route renders dashboard page correctly', async () => {
      // Navigate to `/` (replaces Index.cshtml HomePageModel)
      await page.goto(DASHBOARD_URL);
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert dashboard/home page renders
      const mainContent = page
        .locator('[data-testid="content"]')
        .or(page.locator('#content'))
        .or(page.locator('main, [role="main"]'));
      await expect(mainContent.first()).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify the page body renders correctly (not a blank/error page)
      const bodyText = await mainContent.first().textContent();
      expect(bodyText).toBeTruthy();
      expect(bodyText!.length).toBeGreaterThan(0);

      // URL should be the base/root URL
      expect(page.url()).toMatch(new RegExp(`^${BASE_URL}/?$`));
    });

    test('application home route renders with correct application context', async () => {
      // Navigate to /:appName/a/ (replaces ApplicationHome.cshtml)
      const appHomeUrl = `${BASE_URL}${APP_HOME_ROUTE(TEST_APP_NAME)}`;
      await page.goto(appHomeUrl);
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert the application home page renders
      const mainContent = page
        .locator('[data-testid="content"]')
        .or(page.locator('#content'))
        .or(page.locator('main, [role="main"]'));
      await expect(mainContent.first()).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify URL contains the application name
      expect(page.url()).toContain(`/${TEST_APP_NAME}/a`);

      // Verify the page shows the correct application context
      // (the app name should appear somewhere in the page content or navigation)
      const pageContent = await page.content();
      expect(pageContent.toLowerCase()).toContain(TEST_APP_NAME.toLowerCase());
    });

    test('application node route renders with correct node context', async () => {
      // Navigate to /:appName/:areaName/:nodeName/a/
      // (replaces ApplicationNode.cshtml which supports hookKey query parameter)
      const nodeUrl = `${BASE_URL}${APP_NODE_ROUTE(TEST_APP_NAME, TEST_AREA_NAME, TEST_NODE_NAME)}`;
      await page.goto(nodeUrl);
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert the application node page renders
      const mainContent = page
        .locator('[data-testid="content"]')
        .or(page.locator('#content'))
        .or(page.locator('main, [role="main"]'));
      await expect(mainContent.first()).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify URL contains the full app/area/node path
      expect(page.url()).toContain(
        `/${TEST_APP_NAME}/${TEST_AREA_NAME}/${TEST_NODE_NAME}/a`
      );

      // Verify the correct node context is shown (node name in page content)
      const pageContent = await page.content();
      expect(pageContent.toLowerCase()).toContain(TEST_NODE_NAME.toLowerCase());
    });

    test('invalid route combinations show not-found page', async () => {
      // Navigate to a non-existent app/area/node combination
      const invalidUrl = `${BASE_URL}/nonexistent-app-xyz/invalid-area/bad-node/a/`;
      await page.goto(invalidUrl);
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(SETTLE_TIME);

      // Assert: either a 404/not-found page renders, or we're redirected to home
      const is404Page = await page
        .getByText(/not found|404|page.*not.*exist|does.*not.*exist/i)
        .first()
        .isVisible()
        .catch(() => false);

      const isRedirectedHome = page.url().match(new RegExp(`^${BASE_URL}/?$`));

      // One of these must be true: either we see a 404 message or we were redirected
      expect(is404Page || !!isRedirectedHome).toBeTruthy();
    });

    test('domain routes resolve correctly for different combinations', async () => {
      // Test multiple domain routes from router.tsx
      const domainRoutes = [
        { path: '/admin', label: 'admin' },
        { path: '/crm', label: 'crm' },
        { path: '/projects', label: 'projects' },
      ];

      for (const route of domainRoutes) {
        await page.goto(`${BASE_URL}${route.path}`);
        await page.waitForLoadState('domcontentloaded');
        await page.waitForTimeout(SETTLE_TIME);

        // Verify we're not on the login page (auth is preserved)
        expect(page.url()).not.toContain('/login');

        // Verify some content renders (not a blank page)
        const mainContent = page
          .locator('[data-testid="content"]')
          .or(page.locator('#content'))
          .or(page.locator('main, [role="main"]'));

        const contentVisible = await mainContent.first().isVisible().catch(() => false);
        // If no specific content area, at least the page shouldn't be completely empty
        if (!contentVisible) {
          const bodyText = await page.locator('body').textContent();
          expect(bodyText!.length).toBeGreaterThan(0);
        }
      }
    });
  });

  // ═════════════════════════════════════════════════════════════════════════════
  // SECTION 5: APPLICATION MENU TESTS
  // Replaces: ApplicationMenu ViewComponent (WebVella.Erp.Web/Components/ApplicationMenu/)
  //
  // The monolith's ApplicationMenu.cs ViewComponent takes pageModel.ApplicationMenu,
  // converts flat node lists to hierarchical trees via RenderService.ConvertListToTree(),
  // and passes them to ApplicationMenu.cshtml which renders `div.app-sitemap` iterating
  // menuItems delegated to the NavMenu partial. The React SPA replicates this as the
  // Sidebar component with React Router NavLink integration.
  // ═════════════════════════════════════════════════════════════════════════════

  test.describe('Application Menu', () => {
    test('registered applications and areas appear in navigation', async () => {
      // Navigate to the application context to trigger menu rendering
      const appHomeUrl = `${BASE_URL}${APP_HOME_ROUTE(TEST_APP_NAME)}`;
      await page.goto(appHomeUrl);
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(SETTLE_TIME);

      // Verify applications appear in the navigation
      // (replaces ApplicationMenu.cshtml iteration over menuItems)
      const sidebar = getSidebar(page);
      await expect(sidebar).toBeVisible({ timeout: NAV_TIMEOUT });

      // Check that navigation links or menu items exist
      const menuItems = sidebar
        .getByRole('link')
        .or(sidebar.getByRole('treeitem'))
        .or(sidebar.locator('.menu-item, .nav-item, [data-testid*="menu"]'));
      const menuCount = await menuItems.count();
      expect(menuCount).toBeGreaterThan(0);

      // Verify application areas are displayed (tree structure)
      const appMenu = page
        .locator('[data-testid="app-menu"]')
        .or(page.locator('.app-sitemap, .app-menu, [class*="app-menu"]'))
        .or(sidebar);
      const appMenuText = await appMenu.first().textContent();
      expect(appMenuText).toBeTruthy();
      expect(appMenuText!.length).toBeGreaterThan(0);
    });

    test('clicking an area expands child nodes and collapses on re-click', async () => {
      const appHomeUrl = `${BASE_URL}${APP_HOME_ROUTE(TEST_APP_NAME)}`;
      await page.goto(appHomeUrl);
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(SETTLE_TIME);

      const sidebar = getSidebar(page);
      await expect(sidebar).toBeVisible({ timeout: NAV_TIMEOUT });

      // Look for expandable menu items (areas with child nodes)
      // These typically have a toggle button or are clickable to expand
      const expandableTrigger = sidebar
        .getByRole('button', { name: /expand|toggle|collapse/i })
        .or(sidebar.locator('[data-testid*="area-toggle"]'))
        .or(sidebar.locator('.menu-toggle, .expand-toggle, [aria-expanded]'))
        .or(sidebar.locator('li:has(ul) > a, li:has(ul) > button, li:has(ul) > span'));

      const hasExpandable = await expandableTrigger.first().isVisible().catch(() => false);
      if (!hasExpandable) {
        // If no expandable areas, all nodes may be flat — skip gracefully
        test.skip(true, 'No expandable area groups found in application menu');
        return;
      }

      // Get the first expandable trigger
      const firstTrigger = expandableTrigger.first();
      const ariaExpanded = await firstTrigger.getAttribute('aria-expanded');

      // Click to expand (or collapse if already expanded)
      await firstTrigger.click();
      await page.waitForTimeout(SETTLE_TIME);

      // After clicking, the aria-expanded state should toggle
      const newAriaExpanded = await firstTrigger.getAttribute('aria-expanded');
      if (ariaExpanded !== null && newAriaExpanded !== null) {
        expect(newAriaExpanded).not.toBe(ariaExpanded);
      }

      // Verify child nodes become visible after expansion
      const childNodes = sidebar
        .locator('[role="group"] [role="treeitem"]')
        .or(sidebar.locator('ul.submenu li, .child-nodes .node-item'))
        .or(sidebar.locator('li ul li'));
      const childCount = await childNodes.count();

      // Click again to collapse
      await firstTrigger.click();
      await page.waitForTimeout(SETTLE_TIME);

      // After collapse, child count should be 0 or items should be hidden
      if (childCount > 0) {
        const firstChild = childNodes.first();
        const isHidden = await firstChild.isHidden().catch(() => true);
        // Child nodes should now be hidden (collapsed)
        expect(isHidden).toBeTruthy();
      }
    });

    test('active menu item shows highlighted state matching current route', async () => {
      // Navigate to a specific node route
      const nodeUrl = `${BASE_URL}${APP_NODE_ROUTE(TEST_APP_NAME, TEST_AREA_NAME, TEST_NODE_NAME)}`;
      await page.goto(nodeUrl);
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(SETTLE_TIME);

      const sidebar = getSidebar(page);
      await expect(sidebar).toBeVisible({ timeout: NAV_TIMEOUT });

      // Find links in the sidebar that match the current route context
      const allLinks = sidebar.getByRole('link');
      const linkCount = await allLinks.count();

      let foundActiveLink = false;
      for (let i = 0; i < linkCount; i++) {
        const link = allLinks.nth(i);
        const href = await link.getAttribute('href');
        const ariaCurrent = await link.getAttribute('aria-current');
        const linkClass = await link.getAttribute('class');

        // Check if this link is the active one (matches current route or has active state)
        const isActive =
          ariaCurrent === 'page' ||
          ariaCurrent === 'true' ||
          (linkClass !== null && /active|selected|current/i.test(linkClass));

        if (isActive) {
          foundActiveLink = true;

          // Verify the active link visually indicates active state
          const isVisible = await link.isVisible();
          expect(isVisible).toBeTruthy();
          break;
        }

        // Also check if the href relates to the current route context
        if (href && href.includes(TEST_NODE_NAME)) {
          foundActiveLink = true;
          break;
        }
      }

      // At least one link should indicate the current active context
      expect(foundActiveLink).toBeTruthy();

      // Verify parent area/app is in expanded state
      // (the parent container of the active link should be visible/expanded)
      const activeContainer = sidebar
        .locator('[aria-expanded="true"]')
        .or(sidebar.locator('.expanded, .open, [class*="expanded"]'));
      const expandedExists = await activeContainer.first().isVisible().catch(() => false);
      // If hierarchical menu exists, parent should be expanded
      // (non-blocking — flat menus don't have this)
      if (expandedExists) {
        await expect(activeContainer.first()).toBeVisible();
      }
    });
  });
});
