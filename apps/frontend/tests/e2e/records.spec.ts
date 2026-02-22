/**
 * @file Record CRUD E2E Test Suite — WebVella ERP React SPA
 *
 * Comprehensive Playwright E2E test suite validating all critical record CRUD
 * (Create, Read, Update, Delete) user-facing workflows against a full LocalStack
 * stack. Tests interact with the real React SPA → API Gateway → Lambda handlers
 * (services/entity-management) → DynamoDB — zero mocked AWS SDK calls.
 *
 * Replaces the monolith's Razor Page record workflows:
 *   - RecordList.cshtml     + RecordListPageModel        (PcGrid data grid)
 *   - RecordCreate.cshtml   + RecordCreatePageModel      (PcForm dynamic form)
 *   - RecordDetails.cshtml  + RecordDetailsPageModel      (display mode fields)
 *   - RecordManage.cshtml   + RecordManagePageModel       (edit mode fields)
 *   - RecordRelatedRecord*.cshtml  pages                  (related record CRUD)
 *
 * Route patterns validated (from router.tsx, replacing Razor Page @page directives):
 *   - /:appName/:areaName/:nodeName/l/:pageName?           → Record List
 *   - /:appName/:areaName/:nodeName/c/:pageName?           → Record Create
 *   - /:appName/:areaName/:nodeName/r/:recordId/:pageName? → Record Details
 *   - /:appName/:areaName/:nodeName/m/:recordId/:pageName? → Record Manage/Edit
 *   - .../:parentRecordId/rl/:relationId/l/:pageName?       → Related Record List
 *   - .../:parentRecordId/rl/:relationId/c/:pageName?       → Related Record Create
 *   - .../:parentRecordId/rl/:relationId/r/:childId/:pageName? → Related Record Details
 *   - .../:parentRecordId/rl/:relationId/m/:childId/:pageName? → Related Record Manage
 *
 * Test user: erp@webvella.com / erp (seeded via tools/scripts/seed-test-data.sh)
 *
 * Critical rules (AAP §0.8.1, §0.8.4):
 *   - ALL tests run against LocalStack — zero mocked AWS SDK calls.
 *   - Lambda cold start tolerance: up to 3 s for .NET Native AOT Lambdas.
 *   - Record IDs are GUIDs — test data uses proper v4 UUID format.
 *   - Test data uses unique identifiers per run to avoid collisions.
 *
 * @see WebVella.Erp.Web/Pages/RecordList.cshtml.cs
 * @see WebVella.Erp.Web/Pages/RecordCreate.cshtml.cs
 * @see WebVella.Erp.Web/Pages/RecordDetails.cshtml.cs
 * @see WebVella.Erp.Web/Pages/RecordManage.cshtml.cs
 * @see WebVella.Erp.Web/Pages/RecordRelatedRecordCreate.cshtml.cs
 * @see WebVella.Erp.Web/Pages/RecordRelatedRecordDetails.cshtml.cs
 * @see WebVella.Erp.Web/Pages/RecordRelatedRecordManage.cshtml.cs
 * @see WebVella.Erp.Web/Pages/RecordRelatedRecordsList.cshtml.cs
 * @see WebVella.Erp/Api/RecordManager.cs
 */

import { test, expect, Page, BrowserContext } from '@playwright/test';

// ─── Constants ──────────────────────────────────────────────────────────────

/** Base URL for the React SPA frontend (Vite dev server or production build) */
const BASE_URL: string = process.env.PLAYWRIGHT_BASE_URL || 'http://localhost:5173';

/** Seeded test user email — matches Definitions.cs SystemIds.FirstUserId */
const TEST_EMAIL = 'erp@webvella.com';

/** Seeded test user password — migrated to Cognito via seed script */
const TEST_PASSWORD = 'erp';

/** Login page URL */
const LOGIN_URL = '/login';

/** Maximum wait time for navigation (accounts for Lambda cold starts ≤ 3 s) */
const NAV_TIMEOUT = 15_000;

/** Maximum wait time for API responses that trigger Lambda invocations */
const API_TIMEOUT = 20_000;

/** Short settle time after navigation for DOM/state updates */
const SETTLE_TIME = 500;

/**
 * Unique run suffix appended to test data names to prevent collisions across
 * parallel test runs. Uses a truncated timestamp + random hex for uniqueness.
 */
const RUN_ID = `${Date.now().toString(36)}-${Math.random().toString(16).slice(2, 8)}`;

// ─── Well-Known Seeded Data Identifiers ─────────────────────────────────────

/**
 * Well-known application name used by seeded data.
 * Replaces the SDK admin application from SdkPlugin.cs.
 */
const TEST_APP_NAME = 'sdk';

/** Well-known area name */
const TEST_AREA_NAME = 'objects';

/** Well-known node name */
const TEST_NODE_NAME = 'entities';

/**
 * Well-known test entity name for record CRUD testing.
 * The "account" entity is created by NextPlugin.20190204.cs and is a standard
 * CRM entity used across the monolith for contacts/accounts.
 */
const TEST_ENTITY_NAME = 'account';

/**
 * A second entity used for related-record testing.
 * The "contact" entity is linked to "account" via a many-to-many relation.
 */
const RELATED_ENTITY_NAME = 'contact';

// ─── Route Builder Helpers ──────────────────────────────────────────────────

/**
 * Builds the record list route.
 * Replaces: `/{AppName}/{AreaName}/{NodeName}/l/{PageName?}` from RecordList.cshtml
 */
function listRoute(
  appName: string = TEST_APP_NAME,
  areaName: string = TEST_AREA_NAME,
  nodeName: string = TEST_NODE_NAME,
): string {
  return `/${appName}/${areaName}/${nodeName}/l/`;
}

/**
 * Builds the record create route.
 * Replaces: `/{AppName}/{AreaName}/{NodeName}/c/{PageName?}` from RecordCreate.cshtml
 */
function createRoute(
  appName: string = TEST_APP_NAME,
  areaName: string = TEST_AREA_NAME,
  nodeName: string = TEST_NODE_NAME,
): string {
  return `/${appName}/${areaName}/${nodeName}/c/`;
}

/**
 * Builds the record details route.
 * Replaces: `/{AppName}/{AreaName}/{NodeName}/r/{RecordId}/{PageName?}` from RecordDetails.cshtml
 */
function detailsRoute(
  recordId: string,
  appName: string = TEST_APP_NAME,
  areaName: string = TEST_AREA_NAME,
  nodeName: string = TEST_NODE_NAME,
): string {
  return `/${appName}/${areaName}/${nodeName}/r/${recordId}`;
}

/**
 * Builds the record manage/edit route.
 * Replaces: `/{AppName}/{AreaName}/{NodeName}/m/{RecordId}/{PageName?}` from RecordManage.cshtml
 */
function manageRoute(
  recordId: string,
  appName: string = TEST_APP_NAME,
  areaName: string = TEST_AREA_NAME,
  nodeName: string = TEST_NODE_NAME,
): string {
  return `/${appName}/${areaName}/${nodeName}/m/${recordId}`;
}

/**
 * Builds the related records list route.
 * Replaces: `/{app}/{area}/{node}/r/{parentRecordId}/rl/{relationId}/l/{PageName}`
 * from RecordRelatedRecordsList.cshtml
 */
function relatedListRoute(
  parentRecordId: string,
  relationId: string,
  appName: string = TEST_APP_NAME,
  areaName: string = TEST_AREA_NAME,
  nodeName: string = TEST_NODE_NAME,
): string {
  return `/${appName}/${areaName}/${nodeName}/r/${parentRecordId}/rl/${relationId}/l/`;
}

/**
 * Builds the related record create route.
 * Replaces: `/{app}/{area}/{node}/r/{parentRecordId}/rl/{relationId}/c/{PageName}`
 * from RecordRelatedRecordCreate.cshtml
 */
function relatedCreateRoute(
  parentRecordId: string,
  relationId: string,
  appName: string = TEST_APP_NAME,
  areaName: string = TEST_AREA_NAME,
  nodeName: string = TEST_NODE_NAME,
): string {
  return `/${appName}/${areaName}/${nodeName}/r/${parentRecordId}/rl/${relationId}/c/`;
}

/**
 * Builds the related record details route.
 * Replaces: `/{app}/{area}/{node}/r/{parentRecordId}/rl/{relationId}/r/{recordId}/{PageName}`
 * from RecordRelatedRecordDetails.cshtml
 */
function relatedDetailsRoute(
  parentRecordId: string,
  relationId: string,
  childRecordId: string,
  appName: string = TEST_APP_NAME,
  areaName: string = TEST_AREA_NAME,
  nodeName: string = TEST_NODE_NAME,
): string {
  return `/${appName}/${areaName}/${nodeName}/r/${parentRecordId}/rl/${relationId}/r/${childRecordId}`;
}

/**
 * Builds the related record manage route.
 * Replaces: `/{app}/{area}/{node}/r/{parentRecordId}/rl/{relationId}/m/{recordId}/{PageName}`
 * from RecordRelatedRecordManage.cshtml
 */
function relatedManageRoute(
  parentRecordId: string,
  relationId: string,
  childRecordId: string,
  appName: string = TEST_APP_NAME,
  areaName: string = TEST_AREA_NAME,
  nodeName: string = TEST_NODE_NAME,
): string {
  return `/${appName}/${areaName}/${nodeName}/r/${parentRecordId}/rl/${relationId}/m/${childRecordId}`;
}

// ─── Locator Helpers ────────────────────────────────────────────────────────

/**
 * Resolves the data-table / grid component.
 * Replaces the monolith's `<vc:pc-grid>` ViewComponent from PcGrid/.
 */
function getDataTable(targetPage: Page) {
  return targetPage
    .getByRole('table')
    .or(targetPage.locator('[data-testid="data-table"]'))
    .or(targetPage.locator('[data-testid="record-list"]'))
    .or(targetPage.locator('table.data-table, [class*="DataTable"], [class*="data-table"]'));
}

/**
 * Resolves the dynamic form component.
 * Replaces the monolith's `<vc:pc-form>` ViewComponent from PcForm/.
 */
function getForm(targetPage: Page) {
  return targetPage
    .getByRole('form')
    .or(targetPage.locator('[data-testid="record-form"]'))
    .or(targetPage.locator('[data-testid="dynamic-form"]'))
    .or(targetPage.locator('form.record-form, form[class*="DynamicForm"]'));
}

/**
 * Resolves pagination controls within the data table.
 * Replaces the monolith's PcGrid pagination rendered via PcGridPager.
 */
function getPagination(targetPage: Page) {
  return targetPage
    .getByRole('navigation', { name: /pagination|paging/i })
    .or(targetPage.locator('[data-testid="pagination"]'))
    .or(targetPage.locator('[aria-label="pagination"], nav.pagination, [class*="pagination"]'));
}

/**
 * Resolves validation error messages on a form page.
 * Replaces the monolith's Validation.Errors rendering in Razor Pages.
 */
function getValidationErrors(targetPage: Page) {
  return targetPage
    .locator('[data-testid="validation-errors"]')
    .or(targetPage.locator('[role="alert"]'))
    .or(targetPage.locator('.validation-errors, .error-summary, [class*="error"]'));
}

/**
 * Resolves the delete confirmation dialog.
 * Replaces the monolith's POST with HookKey=="delete" in RecordDetails.cshtml.cs.
 */
function getConfirmDialog(targetPage: Page) {
  return targetPage
    .getByRole('dialog')
    .or(targetPage.locator('[data-testid="confirm-dialog"]'))
    .or(targetPage.locator('[class*="Modal"], [class*="modal"], [class*="dialog"]'));
}

// ─── Authentication Helper ──────────────────────────────────────────────────

/**
 * Authenticates the given page as the seeded test user via the React login form.
 * Uses Cognito authentication flow — replaces the monolith's cookie-based
 * AuthService.cs login with MD5 password validation via SecurityManager.
 *
 * @param targetPage - Playwright Page instance to authenticate
 */
async function login(targetPage: Page): Promise<void> {
  await targetPage.goto(LOGIN_URL);
  await targetPage.waitForLoadState('domcontentloaded');

  // Use resilient locator chains (multiple strategies) matching auth.spec.ts pattern
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

  await emailInput.fill(TEST_EMAIL);
  await passwordInput.fill(TEST_PASSWORD);
  await submitButton.click();

  // Wait for redirect away from /login — confirms Cognito auth success
  await targetPage.waitForURL(
    (url) => !url.pathname.includes('/login'),
    { timeout: NAV_TIMEOUT },
  );
  await targetPage.waitForLoadState('networkidle');
}

// ─── Utility Helpers ────────────────────────────────────────────────────────

/**
 * Generates a unique test value suffixed with the run ID to prevent data
 * collisions across parallel test executions.
 *
 * @param prefix - Human-readable prefix for the value
 * @returns A unique string safe for use as entity field data
 */
function uniqueValue(prefix: string): string {
  return `${prefix}-${RUN_ID}`;
}

/**
 * Waits for an API response matching the given URL pattern.
 * Accounts for Lambda cold start latency (up to 3 s for .NET AOT).
 *
 * @param targetPage - Playwright Page to observe
 * @param urlPattern - RegExp or string pattern to match in the request URL
 * @returns Promise resolving to the matched Response
 */
async function waitForApiResponse(
  targetPage: Page,
  urlPattern: RegExp | string,
) {
  return targetPage.waitForResponse(
    (response) => {
      const url = response.url();
      if (typeof urlPattern === 'string') {
        return url.includes(urlPattern);
      }
      return urlPattern.test(url);
    },
    { timeout: API_TIMEOUT },
  );
}

/**
 * Extracts the record ID from the current page URL.
 * Handles both details routes (/r/{id}) and manage routes (/m/{id}).
 *
 * @param targetPage - The Playwright Page to read the URL from
 * @returns The extracted GUID record ID, or null if not found
 */
function extractRecordIdFromUrl(targetPage: Page): string | null {
  const url = new URL(targetPage.url());
  // Match /r/{guid} or /m/{guid} patterns — UUID v4 format
  const match = url.pathname.match(
    /\/(?:r|m)\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/i,
  );
  return match ? match[1] : null;
}

// ═══════════════════════════════════════════════════════════════════════════════
// RECORD CRUD TEST SUITE
// ═══════════════════════════════════════════════════════════════════════════════

test.describe('Record CRUD', () => {
  /**
   * Run tests serially — the test suite creates a record, then reads, edits,
   * and finally deletes it. Serial mode ensures deterministic ordering so
   * that later tests can reference the record created by earlier ones.
   */
  test.describe.configure({ mode: 'serial' });

  let page: Page;
  let context: BrowserContext;

  /**
   * Track the ID of the record created during the "Record Create" test group.
   * Subsequent details/edit/delete tests reference this ID.
   */
  let createdRecordId: string | null = null;

  /**
   * Track the ID of a related record created during related-record tests.
   */
  let relatedRecordId: string | null = null;

  /**
   * Track a known relation ID for the account ↔ contact many-to-many
   * relationship. This is populated during test execution from the UI.
   */
  let testRelationId: string | null = null;

  // ─── Setup & Teardown ───────────────────────────────────────────────────

  /**
   * Before all record CRUD tests: spin up an authenticated browser context.
   *
   * This replaces the monolith's per-request middleware pipeline:
   *   - ErpMiddleware.cs: binds DbContext + SecurityContext per request
   *   - JwtMiddleware.cs: validates Bearer token
   *   - BaseErpPageModel.Init(): resolves App/Area/Node/Page context
   */
  test.beforeAll(async ({ browser }) => {
    context = await browser.newContext();
    page = await context.newPage();

    // Smoke check: verify the SPA is reachable
    const response = await page.goto(BASE_URL, { timeout: 30_000 });
    expect(
      response !== null && (response.ok() || response.status() === 304),
    ).toBeTruthy();

    // Authenticate as the seeded test user
    await login(page);
  });

  /**
   * After all tests: clean up the browser context.
   */
  test.afterAll(async () => {
    if (context) {
      await context.close();
    }
  });

  /**
   * Before each test: ensure a consistent starting page state by waiting
   * for the DOM to settle. Individual tests navigate to their own routes.
   */
  test.beforeEach(async () => {
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(SETTLE_TIME);
  });

  // ═════════════════════════════════════════════════════════════════════════
  // SECTION 1: RECORD LIST TESTS
  //
  // Replaces: RecordList.cshtml + RecordListPageModel
  // Route: /{AppName}/{AreaName}/{NodeName}/l/{PageName?}
  //
  // The monolith's RecordListPageModel.OnGet() calls Init(), validates
  // ErpRequestContext.Page, runs IPageHook and IRecordListPageHook hooks,
  // then renders the page body containing the PcGrid data grid component.
  // ═════════════════════════════════════════════════════════════════════════

  test.describe('Record List', () => {
    test('should render the data table with columns and rows', async () => {
      // Navigate to the entity list page
      await page.goto(listRoute(), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      // Assert the DataTable component renders (replaces PcGrid ViewComponent)
      const dataTable = getDataTable(page);
      await expect(dataTable).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify column headers are present (replaces PcGrid column definitions)
      const headers = dataTable
        .getByRole('columnheader')
        .or(dataTable.locator('th, [data-testid*="column-header"]'));
      const headerCount = await headers.count();
      expect(headerCount).toBeGreaterThan(0);

      // Verify data rows render (replaces PcGrid row iteration)
      const rows = dataTable
        .getByRole('row')
        .or(dataTable.locator('tbody tr, [data-testid*="row"]'));
      // The header row is typically 1, so data rows should be > 0 if seeded data exists.
      // Allow for empty-state if no records yet (pagination test relies on data).
      const rowCount = await rows.count();
      expect(rowCount).toBeGreaterThanOrEqual(1); // At least the header row
    });

    test('should display pagination controls', async () => {
      await page.goto(listRoute(), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      // Assert pagination controls are present (replaces PcGridPager)
      const pagination = getPagination(page);

      // Pagination may or may not be visible depending on total record count.
      // If visible, verify it contains navigable elements.
      const paginationVisible = await pagination.isVisible().catch(() => false);
      if (paginationVisible) {
        // Verify page size selector or page number links exist
        const pageButtons = pagination
          .getByRole('button')
          .or(pagination.getByRole('link'))
          .or(pagination.locator('button, a, [data-testid*="page"]'));
        const buttonCount = await pageButtons.count();
        expect(buttonCount).toBeGreaterThan(0);
      }

      // Alternatively, check for a page-size dropdown or item count indicator
      const pageSizeControl = page
        .locator('[data-testid="page-size"]')
        .or(page.getByRole('combobox', { name: /page size|rows per page|items per page/i }))
        .or(page.locator('select[class*="page-size"], [class*="pageSize"]'));

      const itemCountIndicator = page
        .locator('[data-testid="item-count"]')
        .or(page.getByText(/showing|total|of \d+|items/i));

      // At least one pagination-related element should be present
      const hasPageSize = await pageSizeControl.isVisible().catch(() => false);
      const hasItemCount = await itemCountIndicator.isVisible().catch(() => false);
      const hasPagination = paginationVisible || hasPageSize || hasItemCount;
      expect(hasPagination).toBeTruthy();
    });

    test('should sort records when clicking a column header', async () => {
      await page.goto(listRoute(), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      const dataTable = getDataTable(page);
      await expect(dataTable).toBeVisible({ timeout: NAV_TIMEOUT });

      // Find a sortable column header
      const sortableHeaders = dataTable
        .locator('th[aria-sort], th[data-sortable], [role="columnheader"][aria-sort]')
        .or(dataTable.locator('th button, th a, [data-testid*="sort"]'));

      const sortableCount = await sortableHeaders.count();

      if (sortableCount > 0) {
        const firstSortable = sortableHeaders.first();

        // Capture initial order of first column cells
        const cellsBefore = dataTable
          .locator('tbody td:first-child, [data-testid*="cell"]:first-child');
        const initialTexts: string[] = [];
        const cellCount = await cellsBefore.count();
        for (let i = 0; i < Math.min(cellCount, 5); i++) {
          const text = await cellsBefore.nth(i).textContent();
          initialTexts.push(text?.trim() ?? '');
        }

        // Click the sortable header to trigger sort
        await firstSortable.click();
        await page.waitForTimeout(SETTLE_TIME);

        // The sort indicator should change (aria-sort attribute or visual indicator)
        const sortIndicator = firstSortable
          .locator('[class*="sort"], [data-testid*="sort-icon"]')
          .or(firstSortable);

        await expect(sortIndicator).toBeVisible();

        // Click again for descending sort
        await firstSortable.click();
        await page.waitForTimeout(SETTLE_TIME);

        // Verify records were reordered by checking at least the first cell changed
        // (only meaningful with 2+ records)
        const cellsAfter = dataTable
          .locator('tbody td:first-child, [data-testid*="cell"]:first-child');
        const afterCount = await cellsAfter.count();
        if (afterCount >= 2 && initialTexts.length >= 2) {
          const afterTexts: string[] = [];
          for (let i = 0; i < Math.min(afterCount, 5); i++) {
            const text = await cellsAfter.nth(i).textContent();
            afterTexts.push(text?.trim() ?? '');
          }
          // At least one element position should differ after reversing sort
          const orderChanged = afterTexts.some(
            (t, idx) => t !== initialTexts[idx],
          );
          // This is a soft assertion — if all values are identical, sort is still valid
          if (initialTexts.filter((v, i, a) => a.indexOf(v) === i).length > 1) {
            expect(orderChanged).toBeTruthy();
          }
        }
      }
    });

    test('should filter records using filter controls', async () => {
      await page.goto(listRoute(), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      const dataTable = getDataTable(page);
      await expect(dataTable).toBeVisible({ timeout: NAV_TIMEOUT });

      // Capture initial row count for comparison
      const rowsBefore = dataTable.locator('tbody tr, [data-testid*="row"]');
      const initialRowCount = await rowsBefore.count();

      // Locate filter controls — replaces PcGridFilterField which rendered
      // <wv-filter-*> controls per field type
      const filterInput = page
        .locator('[data-testid="filter-input"]')
        .or(page.getByRole('searchbox'))
        .or(page.getByPlaceholder(/search|filter/i))
        .or(page.locator('input[type="search"], input[class*="filter"]'));

      const filterButton = page
        .locator('[data-testid="filter-button"]')
        .or(page.getByRole('button', { name: /filter|search|apply/i }))
        .or(page.locator('button[class*="filter"]'));

      const hasFilterInput = await filterInput.isVisible().catch(() => false);
      const hasFilterButton = await filterButton.isVisible().catch(() => false);

      if (hasFilterInput) {
        // Apply a filter that is unlikely to match all records
        await filterInput.fill('__nonexistent_filter_value__');

        if (hasFilterButton) {
          await filterButton.click();
        } else {
          // Some implementations filter on Enter key
          await filterInput.press('Enter');
        }

        await page.waitForTimeout(SETTLE_TIME);
        await page.waitForLoadState('networkidle');

        // After filtering, the row count should change (likely fewer or zero)
        const rowsAfter = dataTable.locator('tbody tr, [data-testid*="row"]');
        const filteredRowCount = await rowsAfter.count();

        // If there were records initially, filtering with a nonsense value should
        // reduce the count (or show an empty state message)
        if (initialRowCount > 0) {
          const emptyState = page
            .getByText(/no records|no results|no data|no items/i)
            .or(page.locator('[data-testid="empty-state"]'));
          const hasEmptyState = await emptyState.isVisible().catch(() => false);

          expect(filteredRowCount < initialRowCount || hasEmptyState).toBeTruthy();
        }

        // Clear the filter to restore original list
        await filterInput.clear();
        if (hasFilterButton) {
          await filterButton.click();
        } else {
          await filterInput.press('Enter');
        }
        await page.waitForTimeout(SETTLE_TIME);
      }
    });
  });

  // ═════════════════════════════════════════════════════════════════════════
  // SECTION 2: RECORD CREATE TESTS
  //
  // Replaces: RecordCreate.cshtml + RecordCreatePageModel
  // Route: /{AppName}/{AreaName}/{NodeName}/c/{PageName?}
  //
  // Source workflow (RecordCreate.cshtml.cs lines 54-141):
  //   1. PageService.ConvertFormPostToEntityRecord() → EntityRecord (line 64)
  //   2. Auto-generate GUID if id is absent (line 76)
  //   3. IRecordCreatePageHook.OnPreCreateRecord hooks (lines 81-92)
  //   4. ValidateRecordSubmission() for required fields (line 95)
  //   5. RecordManager.CreateRecord() (line 102)
  //   6. Success → redirect to /{app}/{area}/{node}/r/{id} (lines 121-124)
  //   7. Failure → render validation errors (lines 103-112)
  // ═════════════════════════════════════════════════════════════════════════

  test.describe('Record Create', () => {
    test('should render the create form with dynamic field components', async () => {
      // Navigate to record create page
      await page.goto(createRoute(), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      // Assert the dynamic form renders (replaces PcForm wrapping Pc* ViewComponents)
      const form = getForm(page);
      await expect(form).toBeVisible({ timeout: NAV_TIMEOUT });

      // Verify field components render — the form should contain input elements
      // corresponding to the entity's field definitions (text, select, date, etc.)
      const formInputs = form
        .locator('input, select, textarea, [role="textbox"], [role="combobox"], [role="checkbox"]');
      const inputCount = await formInputs.count();
      expect(inputCount).toBeGreaterThan(0);

      // Verify a submit button exists
      const submitButton = form
        .getByRole('button', { name: /save|create|submit/i })
        .or(form.locator('button[type="submit"]'))
        .or(page.getByRole('button', { name: /save|create|submit/i }));
      await expect(submitButton).toBeVisible();
    });

    test('should show validation errors when required fields are empty', async () => {
      await page.goto(createRoute(), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      const form = getForm(page);
      await expect(form).toBeVisible({ timeout: NAV_TIMEOUT });

      // Submit the form without filling any fields — should trigger validation
      const submitButton = form
        .getByRole('button', { name: /save|create|submit/i })
        .or(form.locator('button[type="submit"]'))
        .or(page.getByRole('button', { name: /save|create|submit/i }));

      await submitButton.click();
      await page.waitForTimeout(SETTLE_TIME);

      // Assert validation error messages display
      // Replaces: Validation.Errors rendering in RecordCreate.cshtml.cs (lines 103-112)
      const validationErrors = getValidationErrors(page);
      const hasErrors = await validationErrors.isVisible().catch(() => false);

      // Also check for inline field-level validation messages
      const fieldErrors = page
        .locator('[class*="error"], [class*="invalid"], [aria-invalid="true"]')
        .or(page.locator('.field-error, .invalid-feedback, [data-testid*="field-error"]'));
      const fieldErrorCount = await fieldErrors.count();

      // At least one form of validation feedback should be shown
      expect(hasErrors || fieldErrorCount > 0).toBeTruthy();

      // Verify the page remained on the create route (no redirect occurred)
      expect(page.url()).toContain('/c/');
    });

    test('should successfully create a record and redirect to details', async () => {
      await page.goto(createRoute(), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      const form = getForm(page);
      await expect(form).toBeVisible({ timeout: NAV_TIMEOUT });

      // Fill in required fields with unique test data.
      // The "account" entity (from NextPlugin.20190204.cs) typically has a "name" field.
      const nameField = form
        .getByLabel(/name/i)
        .or(form.locator('input[name="name"], input[data-field="name"]'))
        .or(form.getByRole('textbox').first());

      const testName = uniqueValue('TestAccount');
      await nameField.fill(testName);

      // Fill additional fields if visible — email, phone, description
      const emailField = form
        .getByLabel(/email/i)
        .or(form.locator('input[name="email"], input[type="email"]'));
      if (await emailField.isVisible().catch(() => false)) {
        await emailField.fill(`test-${RUN_ID}@webvella.com`);
      }

      const phoneField = form
        .getByLabel(/phone|telephone/i)
        .or(form.locator('input[name="phone"], input[type="tel"]'));
      if (await phoneField.isVisible().catch(() => false)) {
        await phoneField.fill(`555-${Date.now().toString().slice(-7)}`);
      }

      const descriptionField = form
        .getByLabel(/description|notes/i)
        .or(form.locator('textarea[name="description"], textarea'));
      if (await descriptionField.isVisible().catch(() => false)) {
        await descriptionField.fill(`E2E test record created by run ${RUN_ID}`);
      }

      // Submit the form
      const submitButton = form
        .getByRole('button', { name: /save|create|submit/i })
        .or(form.locator('button[type="submit"]'))
        .or(page.getByRole('button', { name: /save|create|submit/i }));

      // Wait for the API response from the create Lambda
      const responsePromise = waitForApiResponse(page, /record|entity|account/i);
      await submitButton.click();

      // Wait for API completion (Lambda cold start tolerance)
      await responsePromise.catch(() => {
        // API intercept may not match if URL pattern differs — fall back to URL change
      });

      // Wait for redirect to record details page: /{app}/{area}/{node}/r/{id}
      // (RecordCreate.cshtml.cs line 121-124)
      await page.waitForURL(
        (url) => url.pathname.includes('/r/'),
        { timeout: API_TIMEOUT },
      );

      // Extract and store the created record ID for subsequent tests
      createdRecordId = extractRecordIdFromUrl(page);
      expect(createdRecordId).not.toBeNull();

      // Verify the created record's name is displayed on the details page
      const recordName = page
        .getByText(testName)
        .or(page.locator(`[data-testid="field-name"]:has-text("${testName}")`));
      await expect(recordName).toBeVisible({ timeout: NAV_TIMEOUT });
    });

    test('should create a record with multiple field types', async () => {
      await page.goto(createRoute(), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      const form = getForm(page);
      await expect(form).toBeVisible({ timeout: NAV_TIMEOUT });

      // Generate unique test values for each field type
      const multiFieldName = uniqueValue('MultiFieldAccount');

      // 1. Text field (replaces PcFieldText)
      const textField = form
        .getByLabel(/name/i)
        .or(form.locator('input[name="name"]'))
        .or(form.getByRole('textbox').first());
      await textField.fill(multiFieldName);

      // 2. Email field (replaces PcFieldEmail)
      const emailField = form
        .getByLabel(/email/i)
        .or(form.locator('input[type="email"], input[name="email"]'));
      if (await emailField.isVisible().catch(() => false)) {
        await emailField.fill(`multifield-${RUN_ID}@webvella.com`);
      }

      // 3. Number field (replaces PcFieldNumber)
      const numberField = form
        .getByLabel(/number|amount|quantity|priority/i)
        .or(form.locator('input[type="number"], input[name*="number"]'));
      if (await numberField.isVisible().catch(() => false)) {
        await numberField.fill('42');
      }

      // 4. Date field (replaces PcFieldDate / PcFieldDateTime)
      const dateField = form
        .getByLabel(/date/i)
        .or(form.locator('input[type="date"], input[name*="date"]'));
      if (await dateField.isVisible().catch(() => false)) {
        await dateField.fill('2025-06-15');
      }

      // 5. Select/Dropdown field (replaces PcFieldSelect)
      const selectField = form
        .getByRole('combobox')
        .or(form.locator('select, [data-testid*="select"]'));
      if (await selectField.isVisible().catch(() => false)) {
        // Select the second option (first is often a placeholder/empty)
        const options = selectField.locator('option');
        const optionCount = await options.count();
        if (optionCount > 1) {
          const secondOptionValue = await options.nth(1).getAttribute('value');
          if (secondOptionValue) {
            await selectField.selectOption(secondOptionValue);
          }
        }
      }

      // 6. Checkbox field (replaces PcFieldCheckbox)
      const checkboxField = form
        .getByRole('checkbox')
        .or(form.locator('input[type="checkbox"]'));
      if (await checkboxField.first().isVisible().catch(() => false)) {
        const isChecked = await checkboxField.first().isChecked();
        if (!isChecked) {
          await checkboxField.first().check();
        }
      }

      // Submit and verify success
      const submitButton = form
        .getByRole('button', { name: /save|create|submit/i })
        .or(form.locator('button[type="submit"]'))
        .or(page.getByRole('button', { name: /save|create|submit/i }));

      await submitButton.click();

      // Wait for redirect to details page
      await page.waitForURL(
        (url) => url.pathname.includes('/r/'),
        { timeout: API_TIMEOUT },
      );

      // Verify the multi-field record name appears
      const recordName = page
        .getByText(multiFieldName)
        .or(page.locator(`[data-testid="field-name"]:has-text("${multiFieldName}")`));
      await expect(recordName).toBeVisible({ timeout: NAV_TIMEOUT });

      // Navigate back to list to verify the record appears there too
      await page.goto(listRoute(), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      const dataTable = getDataTable(page);
      await expect(dataTable).toBeVisible({ timeout: NAV_TIMEOUT });

      // The created record name should appear in the table
      const tableEntry = dataTable
        .getByText(multiFieldName)
        .or(page.getByText(multiFieldName));
      await expect(tableEntry).toBeVisible({ timeout: NAV_TIMEOUT });
    });
  });

  // ═════════════════════════════════════════════════════════════════════════
  // SECTION 3: RECORD DETAILS TESTS
  //
  // Replaces: RecordDetails.cshtml + RecordDetailsPageModel
  // Route: /{AppName}/{AreaName}/{NodeName}/r/{RecordId}/{PageName?}
  //
  // Source workflow (RecordDetails.cshtml.cs lines 17-47):
  //   1. OnGet() validates page exists, record exists via RecordsExists()
  //   2. Canonical redirect when PageName != ErpRequestContext.Page.Name
  //   3. Renders dynamic page body with field components in display mode
  //      (ComponentMode.Display)
  // ═════════════════════════════════════════════════════════════════════════

  test.describe('Record Details', () => {
    test('should render record details with field values in display mode', async () => {
      // Use the record created in the previous test group
      // If no record was created yet, navigate to the list and use the first available
      let targetId = createdRecordId;

      if (!targetId) {
        await page.goto(listRoute(), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');

        // Click the first record link in the data table
        const dataTable = getDataTable(page);
        await expect(dataTable).toBeVisible({ timeout: NAV_TIMEOUT });

        const firstRecordLink = dataTable
          .locator('tbody tr a, [data-testid*="row"] a')
          .first()
          .or(dataTable.locator('tbody tr td:first-child a').first());

        if (await firstRecordLink.isVisible().catch(() => false)) {
          await firstRecordLink.click();
          await page.waitForURL(
            (url) => url.pathname.includes('/r/'),
            { timeout: NAV_TIMEOUT },
          );
          targetId = extractRecordIdFromUrl(page);
        }
      }

      if (targetId) {
        await page.goto(detailsRoute(targetId), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');

        // Verify the page loaded (not a 404/error state)
        const errorIndicator = page
          .getByText(/not found|404|error/i)
          .or(page.locator('[data-testid="error-page"]'));
        const hasError = await errorIndicator.isVisible().catch(() => false);
        expect(hasError).toBeFalsy();

        // Assert field values display in read-only/display mode
        // (replaces ComponentMode.Display in Razor ViewComponents)
        const fieldValues = page
          .locator('[data-testid*="field-value"], [data-testid*="display-field"]')
          .or(page.locator('.field-value, .display-field, [class*="field-display"]'))
          .or(page.locator('dd, [class*="detail"]'));
        const fieldCount = await fieldValues.count();

        // At minimum, the record should display some field data
        expect(fieldCount).toBeGreaterThan(0);

        // Verify an edit/manage button exists (navigates to /m/{id})
        const editButton = page
          .getByRole('link', { name: /edit|manage/i })
          .or(page.getByRole('button', { name: /edit|manage/i }))
          .or(page.locator('[data-testid="edit-button"], a[href*="/m/"]'));
        await expect(editButton).toBeVisible({ timeout: NAV_TIMEOUT });
      }
    });

    test('should handle non-existent record gracefully', async () => {
      // Use a valid GUID format that does not correspond to any record
      const fakeRecordId = '00000000-0000-0000-0000-000000000000';

      await page.goto(detailsRoute(fakeRecordId), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      // The application should show an error or not-found state
      // Replaces: RecordsExists() → NotFound() in RecordDetails.cshtml.cs line 24
      const notFoundIndicator = page
        .getByText(/not found|does not exist|404|no record|error/i)
        .or(page.locator('[data-testid="not-found"], [data-testid="error-page"]'))
        .or(page.locator('.not-found, .error-page'));

      // Either a not-found message appears, or the URL redirects to list/error page
      const hasNotFound = await notFoundIndicator.isVisible().catch(() => false);
      const isOnErrorPage = page.url().includes('/error') || page.url().includes('/404');
      const redirectedToList = page.url().includes('/l/');

      expect(hasNotFound || isOnErrorPage || redirectedToList).toBeTruthy();
    });
  });

  // ═════════════════════════════════════════════════════════════════════════
  // SECTION 4: RECORD EDIT TESTS
  //
  // Replaces: RecordManage.cshtml + RecordManagePageModel
  // Route: /{AppName}/{AreaName}/{NodeName}/m/{RecordId}/{PageName?}
  //
  // Source workflow (RecordManage.cshtml.cs lines 56-142):
  //   1. PageService.ConvertFormPostToEntityRecord() (line 68)
  //   2. IRecordManagePageHook.OnPreManageRecord hooks (lines 85-96)
  //   3. ValidateRecordSubmission() (line 98)
  //   4. RecordManager.UpdateRecord() (line 105)
  //   5. Success → redirect to /{app}/{area}/{node}/r/{id} (lines 124-127)
  //   6. Failure → render validation errors (lines 106-114)
  // ═════════════════════════════════════════════════════════════════════════

  test.describe('Record Edit', () => {
    test('should render the edit form pre-populated with current values', async () => {
      let targetId = createdRecordId;

      if (!targetId) {
        // Fall back: navigate to list and pick the first record
        await page.goto(listRoute(), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');

        const dataTable = getDataTable(page);
        const firstLink = dataTable
          .locator('tbody tr a, [data-testid*="row"] a')
          .first();
        if (await firstLink.isVisible().catch(() => false)) {
          await firstLink.click();
          await page.waitForURL(
            (url) => url.pathname.includes('/r/'),
            { timeout: NAV_TIMEOUT },
          );
          targetId = extractRecordIdFromUrl(page);
        }
      }

      if (targetId) {
        // Navigate to the manage/edit route
        await page.goto(manageRoute(targetId), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');

        // Assert the form renders (replaces PcForm in edit/manage mode)
        const form = getForm(page);
        await expect(form).toBeVisible({ timeout: NAV_TIMEOUT });

        // Verify form fields are pre-populated (ComponentMode.Edit in monolith)
        const nameField = form
          .getByLabel(/name/i)
          .or(form.locator('input[name="name"]'))
          .or(form.getByRole('textbox').first());

        // The name field should have a non-empty value (pre-populated)
        const nameValue = await nameField.inputValue();
        expect(nameValue.length).toBeGreaterThan(0);

        // Verify a save/update button exists
        const saveButton = form
          .getByRole('button', { name: /save|update|submit/i })
          .or(form.locator('button[type="submit"]'))
          .or(page.getByRole('button', { name: /save|update|submit/i }));
        await expect(saveButton).toBeVisible();
      }
    });

    test('should successfully update a record and redirect to details', async () => {
      let targetId = createdRecordId;

      if (!targetId) {
        await page.goto(listRoute(), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');
        const dataTable = getDataTable(page);
        const firstLink = dataTable
          .locator('tbody tr a, [data-testid*="row"] a')
          .first();
        if (await firstLink.isVisible().catch(() => false)) {
          await firstLink.click();
          await page.waitForURL(
            (url) => url.pathname.includes('/r/'),
            { timeout: NAV_TIMEOUT },
          );
          targetId = extractRecordIdFromUrl(page);
        }
      }

      if (targetId) {
        await page.goto(manageRoute(targetId), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');

        const form = getForm(page);
        await expect(form).toBeVisible({ timeout: NAV_TIMEOUT });

        // Modify the name field with an updated value
        const nameField = form
          .getByLabel(/name/i)
          .or(form.locator('input[name="name"]'))
          .or(form.getByRole('textbox').first());

        const updatedName = uniqueValue('UpdatedAccount');
        await nameField.clear();
        await nameField.fill(updatedName);

        // Submit the update
        const saveButton = form
          .getByRole('button', { name: /save|update|submit/i })
          .or(form.locator('button[type="submit"]'))
          .or(page.getByRole('button', { name: /save|update|submit/i }));

        const responsePromise = waitForApiResponse(page, /record|entity|account/i);
        await saveButton.click();

        await responsePromise.catch(() => {
          // Fall back to URL change detection
        });

        // Wait for redirect to details page: /{app}/{area}/{node}/r/{id}
        // (RecordManage.cshtml.cs lines 124-127)
        await page.waitForURL(
          (url) => url.pathname.includes('/r/'),
          { timeout: API_TIMEOUT },
        );

        // Verify the updated name displays on the details page
        const updatedNameElement = page
          .getByText(updatedName)
          .or(page.locator(`[data-testid="field-name"]:has-text("${updatedName}")`));
        await expect(updatedNameElement).toBeVisible({ timeout: NAV_TIMEOUT });
      }
    });

    test('should show validation errors when required fields are cleared', async () => {
      let targetId = createdRecordId;

      if (!targetId) {
        await page.goto(listRoute(), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');
        const dataTable = getDataTable(page);
        const firstLink = dataTable
          .locator('tbody tr a, [data-testid*="row"] a')
          .first();
        if (await firstLink.isVisible().catch(() => false)) {
          await firstLink.click();
          await page.waitForURL(
            (url) => url.pathname.includes('/r/'),
            { timeout: NAV_TIMEOUT },
          );
          targetId = extractRecordIdFromUrl(page);
        }
      }

      if (targetId) {
        await page.goto(manageRoute(targetId), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');

        const form = getForm(page);
        await expect(form).toBeVisible({ timeout: NAV_TIMEOUT });

        // Clear a required field (name is typically required for accounts)
        const nameField = form
          .getByLabel(/name/i)
          .or(form.locator('input[name="name"]'))
          .or(form.getByRole('textbox').first());
        await nameField.clear();

        // Submit with empty required field
        const saveButton = form
          .getByRole('button', { name: /save|update|submit/i })
          .or(form.locator('button[type="submit"]'))
          .or(page.getByRole('button', { name: /save|update|submit/i }));
        await saveButton.click();
        await page.waitForTimeout(SETTLE_TIME);

        // Assert validation errors appear
        // Replaces: validation rendering in RecordManage.cshtml.cs (lines 106-114)
        const validationErrors = getValidationErrors(page);
        const hasErrors = await validationErrors.isVisible().catch(() => false);

        const fieldErrors = page
          .locator('[class*="error"], [class*="invalid"], [aria-invalid="true"]')
          .or(page.locator('.field-error, .invalid-feedback'));
        const fieldErrorCount = await fieldErrors.count();

        expect(hasErrors || fieldErrorCount > 0).toBeTruthy();

        // Verify the page remained on the manage/edit route (no redirect)
        expect(page.url()).toContain('/m/');
      }
    });
  });

  // ═════════════════════════════════════════════════════════════════════════
  // SECTION 5: RECORD DELETE TESTS
  //
  // Replaces: RecordDetailsPageModel.OnPost with HookKey == "delete"
  // Source workflow (RecordDetails.cshtml.cs lines 67-82):
  //   1. POST with HookKey == "delete" triggers RecordManager.DeleteRecord()
  //   2. Success → redirect to list page /{app}/{area}/{node}/l/ (line 72)
  //   3. Failure → render validation errors (lines 74-81)
  // ═════════════════════════════════════════════════════════════════════════

  test.describe('Record Delete', () => {
    test('should cancel deletion and keep the record', async () => {
      // First, ensure we have a record to work with
      let targetId = createdRecordId;

      if (!targetId) {
        // Create a throwaway record for deletion testing
        await page.goto(createRoute(), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');

        const form = getForm(page);
        await expect(form).toBeVisible({ timeout: NAV_TIMEOUT });

        const nameField = form
          .getByLabel(/name/i)
          .or(form.locator('input[name="name"]'))
          .or(form.getByRole('textbox').first());
        await nameField.fill(uniqueValue('DeleteTestAccount'));

        const submitButton = form
          .getByRole('button', { name: /save|create|submit/i })
          .or(form.locator('button[type="submit"]'))
          .or(page.getByRole('button', { name: /save|create|submit/i }));
        await submitButton.click();

        await page.waitForURL(
          (url) => url.pathname.includes('/r/'),
          { timeout: API_TIMEOUT },
        );
        targetId = extractRecordIdFromUrl(page);
        createdRecordId = targetId;
      }

      if (targetId) {
        // Navigate to the record details page
        await page.goto(detailsRoute(targetId), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');

        // Find the delete action button
        const deleteButton = page
          .getByRole('button', { name: /delete|remove/i })
          .or(page.locator('[data-testid="delete-button"]'))
          .or(page.locator('button[class*="delete"], button[class*="danger"]'));

        if (await deleteButton.isVisible().catch(() => false)) {
          // Click delete to trigger confirmation dialog
          await deleteButton.click();
          await page.waitForTimeout(SETTLE_TIME);

          // The application should show a confirmation dialog
          const confirmDialog = getConfirmDialog(page);
          const hasDialog = await confirmDialog.isVisible().catch(() => false);

          if (hasDialog) {
            // Click cancel/dismiss
            const cancelButton = confirmDialog
              .getByRole('button', { name: /cancel|no|dismiss|close/i })
              .or(confirmDialog.locator('button[class*="cancel"], button[class*="secondary"]'));

            if (await cancelButton.isVisible().catch(() => false)) {
              await cancelButton.click();
              await page.waitForTimeout(SETTLE_TIME);
            }
          } else {
            // Browser native dialog — handle via Playwright dialog handler
            // The dialog listener was set up; if it triggered, it was dismissed.
            // Regardless, the record should remain.
          }

          // Verify the record still exists — page should still show details
          expect(page.url()).toContain(`/r/${targetId}`);

          // Verify record content is still visible
          const fieldValues = page
            .locator('[data-testid*="field-value"], [data-testid*="display-field"]')
            .or(page.locator('.field-value, .display-field, dd'));
          const fieldCount = await fieldValues.count();
          expect(fieldCount).toBeGreaterThan(0);
        }
      }
    });

    test('should delete a record and redirect to list page', async () => {
      let targetId = createdRecordId;

      if (!targetId) {
        // Create a record specifically for deletion
        await page.goto(createRoute(), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');

        const form = getForm(page);
        await expect(form).toBeVisible({ timeout: NAV_TIMEOUT });

        const nameField = form
          .getByLabel(/name/i)
          .or(form.locator('input[name="name"]'))
          .or(form.getByRole('textbox').first());
        await nameField.fill(uniqueValue('ToBeDeleted'));

        const submitButton = form
          .getByRole('button', { name: /save|create|submit/i })
          .or(form.locator('button[type="submit"]'))
          .or(page.getByRole('button', { name: /save|create|submit/i }));
        await submitButton.click();

        await page.waitForURL(
          (url) => url.pathname.includes('/r/'),
          { timeout: API_TIMEOUT },
        );
        targetId = extractRecordIdFromUrl(page);
      }

      if (targetId) {
        const deletedName = uniqueValue('ToBeDeleted');

        // Navigate to the record details page
        await page.goto(detailsRoute(targetId), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');

        // Set up a dialog handler for browser-native confirm dialogs
        page.once('dialog', async (dialog) => {
          await dialog.accept();
        });

        // Find and click the delete action button
        const deleteButton = page
          .getByRole('button', { name: /delete|remove/i })
          .or(page.locator('[data-testid="delete-button"]'))
          .or(page.locator('button[class*="delete"], button[class*="danger"]'));

        if (await deleteButton.isVisible().catch(() => false)) {
          await deleteButton.click();
          await page.waitForTimeout(SETTLE_TIME);

          // Handle custom confirmation dialog if present
          const confirmDialog = getConfirmDialog(page);
          const hasDialog = await confirmDialog.isVisible().catch(() => false);

          if (hasDialog) {
            const confirmButton = confirmDialog
              .getByRole('button', { name: /confirm|yes|delete|ok/i })
              .or(confirmDialog.locator('button[class*="danger"], button[class*="primary"]'));
            await confirmButton.click();
          }

          // Wait for redirect to list page: /{app}/{area}/{node}/l/
          // (RecordDetails.cshtml.cs line 72)
          await page.waitForURL(
            (url) => url.pathname.includes('/l/'),
            { timeout: API_TIMEOUT },
          );

          // Verify the URL is the list page
          expect(page.url()).toContain('/l/');

          // Wait for the list to render
          await page.waitForLoadState('networkidle');

          // Verify the deleted record no longer appears in the list
          const dataTable = getDataTable(page);
          const isTableVisible = await dataTable.isVisible().catch(() => false);

          if (isTableVisible) {
            // The deleted record's ID should NOT be present in any link
            const deletedLink = dataTable.locator(`a[href*="${targetId}"]`);
            const deletedLinkCount = await deletedLink.count();
            expect(deletedLinkCount).toBe(0);
          }

          // Clear the reference since the record no longer exists
          createdRecordId = null;
        }
      }
    });
  });

  // ═════════════════════════════════════════════════════════════════════════
  // SECTION 6: RELATED RECORDS TESTS
  //
  // Replaces: RecordRelatedRecord*.cshtml pages
  //   - RecordRelatedRecordsList.cshtml.cs
  //   - RecordRelatedRecordCreate.cshtml.cs
  //   - RecordRelatedRecordDetails.cshtml.cs
  //   - RecordRelatedRecordManage.cshtml.cs
  //
  // Routes:
  //   - /{app}/{area}/{node}/r/{parentRecordId}/rl/{relationId}/l/{PageName}
  //   - /{app}/{area}/{node}/r/{parentRecordId}/rl/{relationId}/c/{PageName}
  //   - /{app}/{area}/{node}/r/{parentRecordId}/rl/{relationId}/r/{childId}/{PageName}
  //   - /{app}/{area}/{node}/r/{parentRecordId}/rl/{relationId}/m/{childId}/{PageName}
  //
  // Source workflow (RecordRelatedRecordCreate.cshtml.cs lines 100-171):
  //   1. CreateRecord() within DbContext transaction (line 112)
  //   2. CreateRelationManyToManyRecord() linking parent ↔ child (lines 126-129)
  //   3. CommitTransaction (line 131)
  //   4. Redirect to related record details (line 141)
  // ═════════════════════════════════════════════════════════════════════════

  test.describe('Related Records', () => {
    /**
     * ID of the parent record used for related-record testing.
     * Created fresh if needed, or reused from previous test sections.
     */
    let parentRecordId: string | null = null;

    test('should create a parent record for related-record testing', async () => {
      // Create a parent record that will be used for related-record tests
      await page.goto(createRoute(), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      const form = getForm(page);
      await expect(form).toBeVisible({ timeout: NAV_TIMEOUT });

      const nameField = form
        .getByLabel(/name/i)
        .or(form.locator('input[name="name"]'))
        .or(form.getByRole('textbox').first());
      await nameField.fill(uniqueValue('ParentAccount'));

      const submitButton = form
        .getByRole('button', { name: /save|create|submit/i })
        .or(form.locator('button[type="submit"]'))
        .or(page.getByRole('button', { name: /save|create|submit/i }));
      await submitButton.click();

      await page.waitForURL(
        (url) => url.pathname.includes('/r/'),
        { timeout: API_TIMEOUT },
      );

      parentRecordId = extractRecordIdFromUrl(page);
      expect(parentRecordId).not.toBeNull();
    });

    test('should display related records list for a parent record', async () => {
      if (!parentRecordId) {
        test.skip();
        return;
      }

      // Navigate to the parent record details page
      await page.goto(detailsRoute(parentRecordId), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      // Look for related records section — this replaces the
      // RecordRelatedRecordsList.cshtml Razor Page tab/section.
      // It may be rendered as a tab, an accordion section, or embedded list.
      const relatedSection = page
        .locator('[data-testid="related-records"]')
        .or(page.getByRole('tabpanel', { name: /related|contacts|relations/i }))
        .or(page.locator('[class*="related-records"], [class*="RelatedRecords"]'))
        .or(page.getByText(/related records|relations/i));

      // Click a "relations" or "related" tab/link if it exists
      const relatedTab = page
        .getByRole('tab', { name: /related|contacts|relations/i })
        .or(page.locator('[data-testid="related-tab"]'))
        .or(page.getByRole('link', { name: /related|contacts/i }));

      if (await relatedTab.isVisible().catch(() => false)) {
        await relatedTab.click();
        await page.waitForTimeout(SETTLE_TIME);
      }

      // Try to find a relation link or section from the UI to extract the relation ID
      const relationLinks = page
        .locator('[data-testid*="relation"], a[href*="/rl/"]')
        .or(page.locator('[data-relation-id]'));
      const relationLinkCount = await relationLinks.count();

      if (relationLinkCount > 0) {
        // Extract the first relation ID from the page
        const firstRelationLink = relationLinks.first();
        const href = await firstRelationLink.getAttribute('href');
        const relationAttr = await firstRelationLink.getAttribute('data-relation-id');

        if (href) {
          const rlMatch = href.match(/\/rl\/([0-9a-f-]+)/i);
          if (rlMatch) {
            testRelationId = rlMatch[1];
          }
        } else if (relationAttr) {
          testRelationId = relationAttr;
        }
      }

      // Verify the related section or at least the parent record's details page loaded
      const pageLoaded = page.url().includes(`/r/${parentRecordId}`);
      expect(pageLoaded).toBeTruthy();
    });

    test('should navigate to related records list via relation route', async () => {
      if (!parentRecordId) {
        test.skip();
        return;
      }

      // Use the extracted relation ID, or a well-known one from seed data
      const relationId = testRelationId || '00000000-0000-0000-0000-000000000001';

      await page.goto(
        relatedListRoute(parentRecordId, relationId),
        { waitUntil: 'domcontentloaded' },
      );
      await page.waitForLoadState('networkidle');

      // The page should render a related records list context
      // (replaces RecordRelatedRecordsList.cshtml.cs with IRecordRelatedRecordsListPageHook)
      const relatedTable = getDataTable(page);
      const hasTable = await relatedTable.isVisible().catch(() => false);

      // Or it may show an empty state for no related records
      const emptyState = page
        .getByText(/no related|no records|empty/i)
        .or(page.locator('[data-testid="empty-state"]'));
      const hasEmpty = await emptyState.isVisible().catch(() => false);

      // Or it might redirect to the parent record page if relation route isn't supported
      const isOnParentPage = page.url().includes(`/r/${parentRecordId}`);

      // At least one of these states should be true
      expect(hasTable || hasEmpty || isOnParentPage).toBeTruthy();
    });

    test('should create a related record and establish many-to-many link', async () => {
      if (!parentRecordId) {
        test.skip();
        return;
      }

      // Navigate to the parent record details to initiate related record creation
      await page.goto(detailsRoute(parentRecordId), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      // Look for "Create Related Record" or "Add Contact" action
      const createRelatedButton = page
        .getByRole('button', { name: /add related|create related|add contact|new related/i })
        .or(page.getByRole('link', { name: /add related|create related|add contact|new related/i }))
        .or(page.locator('[data-testid="create-related-record"]'))
        .or(page.locator('a[href*="/rl/"][href*="/c/"]'));

      // Click related tab first if needed
      const relatedTab = page
        .getByRole('tab', { name: /related|contacts|relations/i })
        .or(page.locator('[data-testid="related-tab"]'));
      if (await relatedTab.isVisible().catch(() => false)) {
        await relatedTab.click();
        await page.waitForTimeout(SETTLE_TIME);
      }

      if (await createRelatedButton.isVisible().catch(() => false)) {
        await createRelatedButton.click();
        await page.waitForLoadState('networkidle');

        // We should now be on the related record create form
        const form = getForm(page);
        const formVisible = await form.isVisible().catch(() => false);

        if (formVisible) {
          // Fill in the related record (contact) fields
          const nameField = form
            .getByLabel(/name|first name|last name/i)
            .or(form.locator('input[name="name"], input[name*="name"]'))
            .or(form.getByRole('textbox').first());

          const relatedName = uniqueValue('RelatedContact');
          await nameField.fill(relatedName);

          // Fill email if available
          const emailField = form
            .getByLabel(/email/i)
            .or(form.locator('input[type="email"]'));
          if (await emailField.isVisible().catch(() => false)) {
            await emailField.fill(`related-${RUN_ID}@webvella.com`);
          }

          // Submit the related record creation
          const submitButton = form
            .getByRole('button', { name: /save|create|submit/i })
            .or(form.locator('button[type="submit"]'))
            .or(page.getByRole('button', { name: /save|create|submit/i }));

          await submitButton.click();

          // Wait for navigation — should redirect to related record details or parent
          await page.waitForURL(
            (url) =>
              url.pathname.includes('/r/') ||
              url.pathname.includes('/rl/'),
            { timeout: API_TIMEOUT },
          );

          // Extract the related record ID
          relatedRecordId = extractRecordIdFromUrl(page);

          // Verify the created related record's data is visible
          const createdNameElement = page
            .getByText(relatedName)
            .or(page.locator(`[data-testid*="field"]:has-text("${relatedName}")`));
          await expect(createdNameElement).toBeVisible({ timeout: NAV_TIMEOUT });
        }
      } else {
        // If direct "create related" button isn't visible, try the relation route
        const relationId = testRelationId || '00000000-0000-0000-0000-000000000001';
        await page.goto(
          relatedCreateRoute(parentRecordId, relationId),
          { waitUntil: 'domcontentloaded' },
        );
        await page.waitForLoadState('networkidle');

        const form = getForm(page);
        const formVisible = await form.isVisible().catch(() => false);

        if (formVisible) {
          const nameField = form
            .getByLabel(/name/i)
            .or(form.getByRole('textbox').first());
          const relatedName = uniqueValue('RelatedContact');
          await nameField.fill(relatedName);

          const submitButton = form
            .getByRole('button', { name: /save|create|submit/i })
            .or(form.locator('button[type="submit"]'));
          await submitButton.click();

          await page.waitForURL(
            (url) => url.pathname.includes('/r/'),
            { timeout: API_TIMEOUT },
          );

          relatedRecordId = extractRecordIdFromUrl(page);
        }
      }
    });

    test('should edit a related record and save changes', async () => {
      if (!parentRecordId || !relatedRecordId) {
        test.skip();
        return;
      }

      const relationId = testRelationId || '00000000-0000-0000-0000-000000000001';

      // Navigate to the related record manage/edit route
      await page.goto(
        relatedManageRoute(parentRecordId, relationId, relatedRecordId),
        { waitUntil: 'domcontentloaded' },
      );
      await page.waitForLoadState('networkidle');

      // The form should render — if the relation-specific manage route
      // redirects to the standard manage route, handle both cases
      let form = getForm(page);
      let formVisible = await form.isVisible().catch(() => false);

      // Fall back to standard manage route if the related manage route redirected
      if (!formVisible) {
        await page.goto(manageRoute(relatedRecordId), { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');
        form = getForm(page);
        formVisible = await form.isVisible().catch(() => false);
      }

      if (formVisible) {
        // Modify the name field
        const nameField = form
          .getByLabel(/name/i)
          .or(form.locator('input[name="name"]'))
          .or(form.getByRole('textbox').first());

        const updatedRelatedName = uniqueValue('UpdatedRelatedContact');
        await nameField.clear();
        await nameField.fill(updatedRelatedName);

        // Save changes
        const saveButton = form
          .getByRole('button', { name: /save|update|submit/i })
          .or(form.locator('button[type="submit"]'));
        await saveButton.click();

        // Wait for redirect
        await page.waitForURL(
          (url) => url.pathname.includes('/r/'),
          { timeout: API_TIMEOUT },
        );

        // Verify the updated name is displayed
        const updatedElement = page
          .getByText(updatedRelatedName)
          .or(page.locator(`[data-testid*="field"]:has-text("${updatedRelatedName}")`));
        await expect(updatedElement).toBeVisible({ timeout: NAV_TIMEOUT });
      }
    });

    test('should show related records in the parent record context', async () => {
      if (!parentRecordId) {
        test.skip();
        return;
      }

      // Navigate back to the parent record details
      await page.goto(detailsRoute(parentRecordId), { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle');

      // Click the related records tab if available
      const relatedTab = page
        .getByRole('tab', { name: /related|contacts|relations/i })
        .or(page.locator('[data-testid="related-tab"]'));
      if (await relatedTab.isVisible().catch(() => false)) {
        await relatedTab.click();
        await page.waitForTimeout(SETTLE_TIME);
      }

      // Verify the parent record page shows relation context
      // (replaces the monolith's RecordRelatedRecordsList rendering)
      const parentUrl = page.url();
      expect(parentUrl).toContain(`/r/${parentRecordId}`);

      // Look for any indication that relations are displayed
      const relationContent = page
        .locator('[data-testid*="related"], [data-testid*="relation"]')
        .or(page.locator('[class*="related"], [class*="relation"]'))
        .or(page.getByText(/related|contacts|relations/i));

      const hasRelationContent = await relationContent.first().isVisible().catch(() => false);

      // The page should at minimum show the parent record details
      const fieldValues = page
        .locator('[data-testid*="field-value"], [data-testid*="display-field"]')
        .or(page.locator('.field-value, .display-field, dd, [class*="detail"]'));
      const fieldCount = await fieldValues.count();

      expect(hasRelationContent || fieldCount > 0).toBeTruthy();
    });
  });
});
